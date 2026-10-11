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
    /**
     * Command handler
     */
    internal sealed class ConanToolWindowCommand
    {
        /**
         * Command ID.
         */
        public const int CommandId = 0x0100;

        /**
         * Command menu group (command set GUID).
         */
        public static readonly Guid CommandSet = new Guid("3e9359cf-7e7a-4e99-8c6d-254264e59580");

        /**
         * VS Package that provides this command, not null.
         */
        private readonly AsyncPackage package;

        /**
         * Initializes a new instance of the ConanToolWindowCommand class.
         * Adds our command handlers for menu (commands must exist in the command table file)
         *
         * @param package Owner package, not null.
         * @param commandService Command service to add command to, not null.
         */
        private ConanToolWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        /**
         * Gets the instance of the command.
         */
        public static ConanToolWindowCommand Instance
        {
            get;
            private set;
        }

        /**
         * Gets the service provider from the owner package.
         */
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /**
         * Initializes the singleton instance of the command.
         *
         * @param package Owner package, not null.
         */
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in ConanToolWindowCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ConanToolWindowCommand(package, commandService);
        }

        /**
         * Opens the NuGet-style Conan Package Manager as a document window (an editor-style tab),
         * scoped to the selected project (falling back to the solution's startup project),
         * the same way "Manage NuGet Packages..." opens per-project.
         *
         * @param sender The event sender.
         * @param e The event args.
         */
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
                    Loc.T("Select a project in Solution Explorer (or set a startup project) before opening the Conan package manager.", "Sélectionnez un projet dans l'Explorateur de solutions (ou définissez un projet de démarrage) avant d'ouvrir le gestionnaire de packages Conan."),
                    "Conan C/C++ Package Manager",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            OpenPackageManager(project);
        }

        /**
         * Resolves the currently selected project in Solution Explorer, if any. Mirrors what
         * right-clicking a project node and choosing "Manage NuGet Packages..." resolves to.
         */
        internal static Project GetSelectedProject(DTE dte)
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

        /**
         * Opens (or creates) the document window hosting ConanPackageManagerControl
         * for the given project, following the same IVsUIShell.CreateDocumentWindow pattern NuGet's
         * own NuGetPackage.CreateDocWindowAsync uses to open "Manage NuGet Packages..." as a tab in
         * the main editing area instead of a docked tool window.
         *
         * @return The control shown in the new window.
         */
        internal ConanPackageManagerControl OpenPackageManager(Project project)
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

            // If a document window for this project is already open - e.g. left open from before
            // the project was reloaded/reopened - CreateDocumentWindow below would otherwise just
            // re-show that existing frame as-is (same moniker), silently keeping its now-stale
            // EnvDTE.Project reference and Conan data instead of the fresh one just resolved above.
            // That's what shows as a blank window until manually closed and reopened. Closing it
            // here guarantees every invocation creates a brand new window bound to the current,
            // live Project instance.
            if (Microsoft.VisualStudio.Shell.VsShellUtilities.IsDocumentOpen(
                    Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider,
                    documentName,
                    Guid.Empty,
                    out IVsUIHierarchy _,
                    out uint _,
                    out IVsWindowFrame existingFrame) && existingFrame != null)
            {
                existingFrame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
            }

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

            // This document window only ever exists for the lifetime it's explicitly opened in -
            // there's no IVsEditorFactory/IVsPersistDocData behind it that could recreate its
            // content from scratch. Without this, Visual Studio remembers it was open in the
            // solution's .suo and tries to auto-reopen it at the next solution load, before this
            // package (and the command that actually builds the content) has run - which is
            // exactly the "Opération en cours d'exécution..." placeholder tab stuck forever seen at
            // startup. Marking it DontAutoOpen tells Visual Studio to just skip restoring it.
            try
            {
                windowFrame.SetProperty((int)__VSFPROPID5.VSFPROPID_DontAutoOpen, true);
            }
            catch
            {
                // Best-effort - an older VS SDK without this property just keeps the old behavior.
            }

            ErrorHandler.ThrowOnFailure(windowFrame.Show());
            return control;
        }
    }
}
