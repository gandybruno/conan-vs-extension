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

        public BuildEventsHandler(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = dte;
            _buildEvents = _dte.Events.BuildEvents;
            _solutionEvents = _dte.Events.SolutionEvents;
            _profiles_manager = new ConanProfilesManager();

            // Subscribe to compilation events
            _buildEvents.OnBuildDone += OnBuildDone;
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

            // We always overrwrite the script for the prebuild event
            // the ps1 is always overwritten but the event is only added if no conan_install.ps1 is found
            // in the prebuild events
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

        private void OnBuildDone(vsBuildScope Scope, vsBuildAction Action)
        {
            var message = "OnBuildDone";
            System.Diagnostics.Debug.WriteLine(message);
        }

        private void OnSolutionOpened()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ConanInstallTracker.Track("__solution__", InstallConanPackagesForAllProjectsAsync);
        }

        private void OnProjectAdded(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project == null) return;
            ConanInstallTracker.Track(project.FullName, () => InstallConanPackagesForProjectAsync(project));
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

                await ProjectConfigurationManager.EnsureConanPackagesInstalledAsync(project, activeConfig, showErrors: false);
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
                    ConanInstallTracker.Track(project.FullName, () => InstallConanPackagesForProjectAsync(project));
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

            Microsoft.VisualStudio.Threading.JoinableTask[] pending = ConanInstallTracker.GetPending();
            if (pending.Length == 0)
            {
                return VSConstants.S_OK;
            }

            pfCancelUpdate = 1;
            ConanOutputPane.Log("Compilation reportée : une installation/mise à jour Conan est encore en cours. La compilation redémarrera automatiquement une fois celle-ci terminée.");

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
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

                try
                {
                    ConanOutputPane.Log("Installation Conan terminée, relance de la compilation...");
                    _dte.ExecuteCommand("Build.BuildSolution");
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
            _buildEvents.OnBuildDone -= OnBuildDone;
            _buildEvents.OnBuildProjConfigBegin -= OnBuildProjConfigBegin;
            _buildEvents.OnBuildProjConfigDone -= OnBuildProjConfigDone;
            _solutionEvents.Opened -= OnSolutionOpened;
            _solutionEvents.ProjectAdded -= OnProjectAdded;

            if (_solutionBuildManager != null && _updateSolutionEventsCookie != 0)
            {
                _solutionBuildManager.UnadviseUpdateSolutionEvents(_updateSolutionEventsCookie);
            }
        }
    }
}
