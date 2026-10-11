using System;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace conan_vs_extension.Packaging
{
    /**
     * Builds one configuration of a project with Visual Studio itself - the same build as the
     * Build menu (Build pane, Error List, project dependencies), whatever configuration is
     * currently active in the toolbar.
     */
    public static class VsProjectBuilder
    {
        /**
         * @return (true, null) when the build succeeded; (false, why) otherwise.
         */
        public static async Task<(bool success, string error)> BuildAsync(Project project, LibraryConfiguration configuration)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            DTE dte = project.DTE;
            SolutionBuild solutionBuild = dte.Solution.SolutionBuild;

            string solutionConfiguration = FindSolutionConfiguration(solutionBuild, project, configuration);
            if (solutionConfiguration == null)
            {
                return (false, Loc.T($"No solution configuration builds '{configuration.Name}' of this project (see Build > Configuration Manager).",
                                     $"Aucune configuration de solution ne compile '{configuration.Name}' pour ce projet (voir Générer > Gestionnaire de configurations)."));
            }

            if (solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
            {
                return (false, Loc.T("A build is already running.", "Une compilation est déjà en cours."));
            }

            var done = new TaskCompletionSource<bool>();
            BuildEvents buildEvents = dte.Events.BuildEvents;
            _dispBuildEvents_OnBuildDoneEventHandler onBuildDone = (scope, action) => done.TrySetResult(true);
            buildEvents.OnBuildDone += onBuildDone;
            try
            {
                solutionBuild.BuildProject(solutionConfiguration, project.UniqueName, false);
                await done.Task;
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                buildEvents.OnBuildDone -= onBuildDone;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // LastBuildInfo: the number of projects that failed to build.
            return solutionBuild.LastBuildInfo == 0
                ? (true, (string)null)
                : (false, Loc.T($"The build of '{configuration.Name}' failed (see the Build pane of the Output window and the Error List).",
                                $"La compilation de '{configuration.Name}' a échoué (voir l'onglet Build de la fenêtre Output et la Liste d'erreurs)."));
        }

        /**
         * The solution configuration ("Debug|x64") in which the project is built with the wanted
         * project configuration: they usually have the same name, but the Configuration Manager
         * can map them differently.
         */
        private static string FindSolutionConfiguration(SolutionBuild solutionBuild, Project project, LibraryConfiguration configuration)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (SolutionConfiguration solutionConfiguration in solutionBuild.SolutionConfigurations)
            {
                foreach (SolutionContext context in solutionConfiguration.SolutionContexts)
                {
                    if (context.ShouldBuild &&
                        string.Equals(context.ProjectName, project.UniqueName, StringComparison.OrdinalIgnoreCase) &&
                        context.ConfigurationName == configuration.ConfigurationName &&
                        context.PlatformName == configuration.PlatformName)
                    {
                        string platform = (solutionConfiguration as EnvDTE80.SolutionConfiguration2)?.PlatformName;
                        return string.IsNullOrEmpty(platform) ? solutionConfiguration.Name : solutionConfiguration.Name + "|" + platform;
                    }
                }
            }

            return null;
        }
    }
}
