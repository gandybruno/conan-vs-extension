using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.Threading.Tasks;

namespace conan_vs_extension
{
    /// <summary>
    /// Beyond the build events it was originally built for, this class also proactively runs
    /// `conan install` at the moments a user would expect packages to just be there - the solution
    /// opening, a project being added, and the active configuration/profile changing - instead of
    /// only doing it lazily from the pre-build event script during an actual Build (which used to
    /// force a "build once just to run conan install, build again for real" cycle. See
    /// ProjectConfigurationManager.EnsureConanPackagesInstalledAsync for why.
    /// </summary>
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
            _ = InstallConanPackagesForAllProjectsAsync();
        }

        private void OnProjectAdded(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = InstallConanPackagesForProjectAsync(project);
        }

        private async Task InstallConanPackagesForAllProjectsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            foreach (Project project in ProjectConfigurationManager.GetAllProjectsFlat(_dte))
            {
                await InstallConanPackagesForProjectAsync(project);
            }
        }

        /// <summary>
        /// Regenerates this project's Conan profile(s) and runs `conan install` for its currently
        /// active configuration right away - used when the solution opens, a project is added, or
        /// the active configuration/platform changes. Errors are logged rather than shown (there's
        /// no explicit user action to attach a message box to here); the pre-build event script
        /// remains as a safety net and will retry on the next actual Build regardless.
        /// </summary>
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

        /// <summary>
        /// Fires when the user switches the active configuration/platform in the toolbar (or a
        /// project's active config changes for any other reason) - the moment that decides which
        /// Conan profile applies. Proactively installs for the newly active configuration so the
        /// next Build doesn't have to.
        /// </summary>
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
                    _ = InstallConanPackagesForProjectAsync(project);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BuildEventsHandler] OnActiveProjectCfgChange failed: {ex}");
            }

            return VSConstants.S_OK;
        }

        public int UpdateSolution_Begin(ref int pfCancelUpdate) => VSConstants.S_OK;

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
