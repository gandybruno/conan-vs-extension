using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace conan_vs_extension
{
    /**
     * Beyond the build events it was originally built for, this class also proactively runs
     * `conan install` at the moments a user would expect packages to just be there - the solution
     * opening, a project being added, and the active configuration/profile changing - instead of
     * only doing it lazily from the pre-build event script during an actual Build (which used to
     * force a "build once just to run conan install, build again for real" cycle. See
     * ProjectConfigurationManager.EnsureConanPackagesInstalledAsync for why.
     *
     * Those proactive installs run in the background (e.g. right after switching the active
     * configuration/profile), so a Build the user starts immediately afterwards could previously
     * race with a Conan install that hadn't finished yet - Conan and MSBuild both touching the
     * project's generated files/props at the same time, which is almost certainly why a build
     * started "too fast" after a Debug/Release switch could fail outright (e.g. link.exe unable
     * to write a file with a mapped section still open on it). Every proactive install here is
     * registered with ConanInstallTracker - the SAME registry a user-initiated
     * install/removal from the package manager window also registers with - and
     * UpdateSolution_Begin (the very first thing that fires when a Build starts, for
     * the whole solution) blocks until every one of them has finished, exactly like CMake Tools
     * makes a Build wait for an in-flight configure/generate.
     */
    public class BuildEventsHandler : IVsUpdateSolutionEvents
    {
        private readonly DTE _dte;
        private readonly BuildEvents _buildEvents;
        private readonly SolutionEvents _solutionEvents;
        private ConanProfilesManager _profiles_manager;

        private readonly IVsSolutionBuildManager2 _solutionBuildManager;
        private uint _updateSolutionEventsCookie;

        // The command that started the build being postponed (Build, Rebuild, Clean, a single
        // project, F5...), so that exact command is re-issued once Conan has finished - see
        // OnBeforeCommandExecute / UpdateSolution_Begin. Kept as a field: EnvDTE event sources
        // stop firing once garbage collected.
        private readonly CommandEvents _commandEvents;
        private string _lastBuildCommandGuid;
        private int _lastBuildCommandId;
        private DateTime _lastBuildCommandTime;
        private string _replayCommandGuid;
        private int _replayCommandId;
        private bool _restartScheduled;

        private static readonly string Std97CommandSet = VSConstants.GUID_VSStandardCommandSet97.ToString("B");
        private static readonly string Std2KCommandSet = VSConstants.VSStd2K.ToString("B");

        // A recorded command only explains a build that starts shortly after it (a "save changes?"
        // or "projects are out of date" prompt can sit in between).
        private static readonly TimeSpan BuildCommandMaxAge = TimeSpan.FromMinutes(2);

        public BuildEventsHandler(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = dte;
            _buildEvents = _dte.Events.BuildEvents;
            _solutionEvents = _dte.Events.SolutionEvents;
            _profiles_manager = new ConanProfilesManager();

            // Subscribe to compilation events
            _buildEvents.OnBuildProjConfigBegin += OnBuildProjConfigBegin;
            _buildEvents.OnBuildProjConfigDone += OnBuildProjConfigDone;

            // Proactively install packages when the solution finishes loading, or a project is
            // added to an already-open solution.
            _solutionEvents.Opened += OnSolutionOpened;
            _solutionEvents.ProjectAdded += OnProjectAdded;

            // Proactively install packages when the user switches the active
            // configuration/platform (the toolbar dropdowns) - that's what changes which Conan
            // profile applies. EnvDTE has no event for this; it requires the native
            // IVsUpdateSolutionEvents.OnActiveProjectCfgChange callback.
            _solutionBuildManager = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager2;
            _solutionBuildManager?.AdviseUpdateSolutionEvents(this, out _updateSolutionEventsCookie);

            _commandEvents = _dte.Events.CommandEvents;
            _commandEvents.BeforeExecute += OnBeforeCommandExecute;
        }

        /**
         * Remembers the last command that starts a build (or a clean, or a debug session, which
         * builds first), so UpdateSolution_Begin knows what to re-issue if it has to postpone it.
         * Fires for every command Visual Studio executes, keystrokes included: stay cheap.
         */
        private void OnBeforeCommandExecute(string Guid, int ID, object CustomIn, object CustomOut, ref bool CancelDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool startsDebugging = false;
            if (string.Equals(Guid, Std97CommandSet, StringComparison.OrdinalIgnoreCase))
            {
                switch ((VSConstants.VSStd97CmdID)ID)
                {
                    case VSConstants.VSStd97CmdID.BuildSln:
                    case VSConstants.VSStd97CmdID.RebuildSln:
                    case VSConstants.VSStd97CmdID.CleanSln:
                    case VSConstants.VSStd97CmdID.BuildSel:
                    case VSConstants.VSStd97CmdID.RebuildSel:
                    case VSConstants.VSStd97CmdID.CleanSel:
                    case VSConstants.VSStd97CmdID.BuildCtx:
                    case VSConstants.VSStd97CmdID.RebuildCtx:
                    case VSConstants.VSStd97CmdID.CleanCtx:
                        break;
                    case VSConstants.VSStd97CmdID.Start:
                    case VSConstants.VSStd97CmdID.StartNoDebug:
                    case VSConstants.VSStd97CmdID.StepInto:
                    case VSConstants.VSStd97CmdID.StepOver:
                        startsDebugging = true;
                        break;
                    default:
                        return;
                }
            }
            else if (string.Equals(Guid, Std2KCommandSet, StringComparison.OrdinalIgnoreCase))
            {
                switch ((VSConstants.VSStd2KCmdID)ID)
                {
                    case VSConstants.VSStd2KCmdID.PROJSTARTDEBUG:
                    case VSConstants.VSStd2KCmdID.PROJSTEPINTO:
                        startsDebugging = true;
                        break;
                    default:
                        return;
                }
            }
            else
            {
                return;
            }

            // While a debug session is running these mean Continue/Step: no build follows.
            if (startsDebugging && _dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
            {
                return;
            }

            _lastBuildCommandGuid = Guid;
            _lastBuildCommandId = ID;
            _lastBuildCommandTime = DateTime.UtcNow;
        }

        private bool IsConanEnabledForProject(string projectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var project = ProjectConfigurationManager.GetProjectByName(_dte, projectName);
            return project != null && ProjectConfigurationManager.conandataFileExists(project);
        }

        private void OnBuildProjConfigBegin(string Project, string ProjectConfig, string Platform, string SolutionConfig)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsConanEnabledForProject(Project))
            {
                return;
            }

            Project invokedProject = ProjectConfigurationManager.GetProjectByName(_dte, Project);
            _profiles_manager.GenerateProfilesForProject(invokedProject);

            // conan_install.bat is always overwritten, but the pre-build event is only added
            // when none of the project's pre-build events calls it yet.
            _ = ProjectConfigurationManager.SaveConanPrebuildEventsAllConfigAsync(invokedProject);
        }

        private void OnBuildProjConfigDone(string Project, string ProjectConfig, string Platform, string SolutionConfig, bool Success)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsConanEnabledForProject(Project))
            {
                return;
            }

            Project invokedProject = ProjectConfigurationManager.GetProjectByName(_dte, Project);
            VCConfiguration config = ProjectConfigurationManager.GetVCConfig(invokedProject, ProjectConfig, Platform);
            // FIXME: the problem with this is that the first time you build
            // the build is started before this step finishes
            // maybe we can inject the dependency to the project via a prebuild event? 
            // with a script?
            _ = ProjectConfigurationManager.InjectConanDepsAsync(invokedProject, config);
        }

        private void OnSolutionOpened()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = ConanInstallTracker.Track("__solution__", InstallConanPackagesForAllProjectsAsync);
        }

        private void OnProjectAdded(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project == null) return;
            _ = ConanInstallTracker.Track(project.FullName, () => InstallConanPackagesForProjectAsync(project));
        }

        private async Task InstallConanPackagesForAllProjectsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            foreach (Project project in ProjectConfigurationManager.GetAllProjectsFlat(_dte))
            {
                await InstallConanPackagesForProjectAsync(project);
            }
        }

        /**
         * Regenerates this project's Conan profile(s) and runs `conan install` for its currently
         * active configuration right away - used when the solution opens, a project is added, or
         * the active configuration/platform changes. Errors are logged rather than shown (there's
         * no explicit user action to attach a message box to here); the pre-build event script
         * remains as a safety net and will retry on the next actual Build regardless.
         */
        private async Task InstallConanPackagesForProjectAsync(Project project)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                if (project == null || !ProjectConfigurationManager.conandataFileExists(project))
                {
                    return;
                }

                _profiles_manager.GenerateProfilesForProject(project);
                await ProjectConfigurationManager.SaveConanPrebuildEventsAllConfigAsync(project);

                VCConfiguration activeConfig = ProjectConfigurationManager.GetActiveVCConfiguration(project);
                if (activeConfig == null)
                {
                    return;
                }

                await ProjectConfigurationManager.EnsureConanPackagesInstalledAsync(project, activeConfig, force: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BuildEventsHandler] Proactive Conan install failed for a project: {ex}");
            }
        }

        #region IVsUpdateSolutionEvents

        /**
         * Fires when the user switches the active configuration/platform in the toolbar (or a
         * project's active config changes for any other reason) - the moment that decides which
         * Conan profile applies. Proactively installs for the newly active configuration so the
         * next Build doesn't have to.
         */
        public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (pIVsHierarchy != null &&
                    ErrorHandler.Succeeded(pIVsHierarchy.GetProperty(
                        (uint)VSConstants.VSITEMID.Root, (int)__VSHPROPID.VSHPROPID_ExtObject, out object extObject)) &&
                    extObject is Project project)
                {
                    _ = ConanInstallTracker.Track(project.FullName, () => InstallConanPackagesForProjectAsync(project));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BuildEventsHandler] OnActiveProjectCfgChange failed: {ex}");
            }

            return VSConstants.S_OK;
        }

        /**
         * The first thing that fires when the user starts a Build, for the whole solution -
         * before any project's compile/link actually begins. If a Conan install/update is still
         * running at this point, this Build attempt is CANCELLED outright (pfCancelUpdate = 1)
         * rather than blocked on the spot.
         *
         * An earlier version tried to block by synchronously Join()-ing the pending install here,
         * which pumps the UI message queue while still inside this Solution Build Manager
         * callback. That turned out to be unsafe: pumping messages mid-callback can let another
         * message/command re-enter the build manager (or other shell state) while it's only
         * half-initialized, and was almost certainly what crashed Visual Studio outright. Cancelling
         * is safe (no message pumping involved) - we just asynchronously wait for the pending
         * install(s) afterwards and re-issue the Build ourselves once they're done, which produces
         * the same "wait for Conan, then build" behavior for the user without touching the message
         * loop from inside the callback.
         */
        public int UpdateSolution_Begin(ref int pfCancelUpdate)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Which command started this build, if it is known (consumed either way: it only
            // explains this build, not a later one).
            string commandGuid = null;
            int commandId = 0;
            if (_lastBuildCommandGuid != null && DateTime.UtcNow - _lastBuildCommandTime < BuildCommandMaxAge)
            {
                commandGuid = _lastBuildCommandGuid;
                commandId = _lastBuildCommandId;
            }
            _lastBuildCommandGuid = null;

            Microsoft.VisualStudio.Threading.JoinableTask[] pending = ConanInstallTracker.GetPending();
            if (pending.Length == 0)
            {
                return VSConstants.S_OK;
            }

            pfCancelUpdate = 1;
            ConanOutputPane.Log(Loc.T("Build postponed: a Conan install/update is still running. The build will restart automatically once it has finished.", "Compilation reportée : une installation/mise à jour Conan est encore en cours. La compilation redémarrera automatiquement une fois celle-ci terminée."));

            // Re-issue what the user asked for (Rebuild, a single project, F5...), not a plain
            // "Build Solution". Asked again while already waiting: the latest request wins and
            // is issued only once.
            _replayCommandGuid = commandGuid;
            _replayCommandId = commandId;
            if (_restartScheduled)
            {
                return VSConstants.S_OK;
            }
            _restartScheduled = true;

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // JoinAsync (unlike Join) never pumps the message queue - it's a plain
                    // asynchronous wait, safe to do from a background continuation like this one.
                    await Task.WhenAll(pending.Select(joinableTask => joinableTask.JoinAsync()));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BuildEventsHandler] Waiting for pending Conan installs failed: {ex}");
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _restartScheduled = false;

                try
                {
                    ConanOutputPane.Log(Loc.T("Conan install finished, restarting the build...", "Installation Conan terminée, relance de la compilation..."));
                    if (_replayCommandGuid != null)
                    {
                        object customIn = null;
                        object customOut = null;
                        _dte.Commands.Raise(_replayCommandGuid, _replayCommandId, ref customIn, ref customOut);
                    }
                    else
                    {
                        // Not started by a known command (another extension, automation...).
                        _dte.ExecuteCommand("Build.BuildSolution");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BuildEventsHandler] Failed to re-trigger the build after waiting for Conan: {ex}");
                }
            });

            return VSConstants.S_OK;
        }

        public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;

        public int UpdateSolution_Cancel() => VSConstants.S_OK;

        public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand) => VSConstants.S_OK;

        #endregion

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _buildEvents.OnBuildProjConfigBegin -= OnBuildProjConfigBegin;
            _buildEvents.OnBuildProjConfigDone -= OnBuildProjConfigDone;
            _solutionEvents.Opened -= OnSolutionOpened;
            _solutionEvents.ProjectAdded -= OnProjectAdded;
            _commandEvents.BeforeExecute -= OnBeforeCommandExecute;

            if (_solutionBuildManager != null && _updateSolutionEventsCookie != 0)
            {
                _solutionBuildManager.UnadviseUpdateSolutionEvents(_updateSolutionEventsCookie);
            }
        }
    }
}
