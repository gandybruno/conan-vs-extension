using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using EnvDTE;
using Task = System.Threading.Tasks.Task;

namespace conan_vs_extension
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class ConanToolWindowCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("3e9359cf-7e7a-4e99-8c6d-254264e59580");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConanToolWindowCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private ConanToolWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static ConanToolWindowCommand Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in ConanToolWindowCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ConanToolWindowCommand(package, commandService);
        }

        /// <summary>
        /// Opens the NuGet-style Conan Package Manager as a document window (an editor-style tab),
        /// scoped to the selected project (falling back to the solution's startup project),
        /// the same way "Manage NuGet Packages..." opens per-project.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            DTE dte = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
            if (dte == null)
            {
                throw new NotSupportedException("Cannot resolve DTE.");
            }

            Project project = GetSelectedProject(dte) ?? ProjectConfigurationManager.GetStartupProject(dte);
            if (project == null)
            {
                Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(
                    this.package,
                    "Sélectionnez un projet dans l'Explorateur de solutions (ou définissez un projet de démarrage) avant d'ouvrir le gestionnaire de packages Conan.",
                    "Conan C/C++ Package Manager",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            OpenPackageManagerDocumentWindow(project);
        }

        /// <summary>
        /// Resolves the currently selected project in Solution Explorer, if any. Mirrors what
        /// right-clicking a project node and choosing "Manage NuGet Packages..." resolves to.
        /// </summary>
        private static Project GetSelectedProject(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var selectedItems = dte.SelectedItems;
                if (selectedItems != null && selectedItems.Count > 0)
                {
                    foreach (SelectedItem selectedItem in selectedItems)
                    {
                        if (selectedItem.Project != null)
                        {
                            return selectedItem.Project;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the startup-project fallback below.
            }

            return null;
        }

        /// <summary>
        /// Opens (or creates) the document window hosting <see cref="ConanPackageManagerControl"/>
        /// for the given project, following the same IVsUIShell.CreateDocumentWindow pattern NuGet's
        /// own NuGetPackage.CreateDocWindowAsync uses to open "Manage NuGet Packages..." as a tab in
        /// the main editing area instead of a docked tool window.
        /// </summary>
        private void OpenPackageManagerDocumentWindow(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var uiShell = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;
            var solution = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution;
            if (uiShell == null || solution == null)
            {
                throw new NotSupportedException("Cannot resolve IVsUIShell/IVsSolution.");
            }

            IVsHierarchy hierarchy = null;
            solution.GetProjectOfUniqueName(project.UniqueName, out hierarchy);

            string documentName = "ConanPackageManager:" + project.UniqueName;
            uint itemId = hierarchy != null ? (uint)VSConstants.VSITEMID.Root : (uint)VSConstants.VSITEMID.Nil;

            var control = new ConanPackageManagerControl(project);
            var windowPane = new ConanPackageManagerWindowPane(control);

            Guid emptyGuid = Guid.Empty;
            Guid editorType = new Guid(GuidList.strConanPackageManagerEditorType);

            uint windowFlags = (uint)_VSRDTFLAGS.RDT_DontAddToMRU | (uint)_VSRDTFLAGS.RDT_DontSaveAs;

            IntPtr punkDocView = IntPtr.Zero;
            IntPtr punkDocData = IntPtr.Zero;
            IVsWindowFrame windowFrame = null;

            try
            {
                punkDocView = Marshal.GetIUnknownForObject(windowPane);
                punkDocData = Marshal.GetIUnknownForObject(control);

                string caption = "Conan: " + project.Name;

                int hr = uiShell.CreateDocumentWindow(
                    windowFlags,
                    documentName,
                    (IVsUIHierarchy)hierarchy,
                    itemId,
                    punkDocView,
                    punkDocData,
                    ref editorType,
                    null,
                    ref emptyGuid,
                    null,
                    caption,
                    string.Empty,
                    null,
                    out windowFrame);

                ErrorHandler.ThrowOnFailure(hr);
            }
            finally
            {
                if (punkDocView != IntPtr.Zero)
                {
                    Marshal.Release(punkDocView);
                }
                if (punkDocData != IntPtr.Zero)
                {
                    Marshal.Release(punkDocData);
                }
            }

            if (windowFrame == null)
            {
                throw new NotSupportedException("Cannot create the Conan Package Manager document window.");
            }

            ErrorHandler.ThrowOnFailure(windowFrame.Show());
        }
    }
}
