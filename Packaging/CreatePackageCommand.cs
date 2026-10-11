using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace conan_vs_extension.Packaging
{
    /**
     * "Create Conan Package", in the Project menu and the context menu of a project that builds
     * a library - the counterpart of NuGet's "Pack". Creates the package straight away from the
     * saved settings; while they are missing or incomplete, it opens the Package tab instead.
     */
    internal sealed class CreatePackageCommand
    {
        public const int CommandId = 0x0101;

        private readonly AsyncPackage _package;

        private CreatePackageCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;

            var menuItem = new OleMenuCommand(Execute, new CommandID(ConanToolWindowCommand.CommandSet, CommandId));
            menuItem.BeforeQueryStatus += OnBeforeQueryStatus;
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                _ = new CreatePackageCommand(package, commandService);
            }
        }

        /**
         * Only shown for a project that builds a library.
         */
        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var command = (OleMenuCommand)sender;
            command.Text = Loc.T("Create Conan Package", "Créer le paquet Conan");
            try
            {
                Project project = GetTargetProject();
                command.Visible = project != null && PackagePropertiesControl.IsLibraryProject(project);
            }
            catch (Exception)
            {
                command.Visible = false;
            }
        }

        /**
         * The project selected in Solution Explorer, else the startup project, else the only
         * library of the solution (nothing is selected right after a solution is opened).
         */
        private static Project GetTargetProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            DTE dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
            if (dte == null)
            {
                return null;
            }

            Project project = ConanToolWindowCommand.GetSelectedProject(dte) ?? ProjectConfigurationManager.GetStartupProject(dte);
            if (project != null)
            {
                return project;
            }

            List<Project> libraries = ProjectConfigurationManager.GetAllProjectsFlat(dte).Where(PackagePropertiesControl.IsLibraryProject).ToList();
            return libraries.Count == 1 ? libraries[0] : null;
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Project project = GetTargetProject();
            if (project == null || !PackagePropertiesControl.IsLibraryProject(project))
            {
                return;
            }

            string projectDirectory = Path.GetDirectoryName(project.FullName);
            PackageSettings settings = ConanDataFile.Load(projectDirectory).Package;
            if (settings == null || PackagePlanner.Validate(settings, VersionFile.Read(projectDirectory)).Count > 0)
            {
                // Nothing usable saved yet: the Package tab is where it is entered.
                ConanToolWindowCommand.Instance.OpenPackageManager(project).ShowPackageTab();
                return;
            }

            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                var progress = new Progress<string>(message => SetStatusBarText("Conan: " + message));
                PackageCreationResult result = await PackageCreator.CreateAsync(project, progress, CancellationToken.None);

                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetStatusBarText(string.Empty);
                PackageCreationReport.Show(result);
            });
        }

        private static void SetStatusBarText(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            (ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText(text);
        }
    }
}
