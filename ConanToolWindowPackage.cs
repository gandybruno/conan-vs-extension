using EnvDTE;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;
using System.Windows.Media;

namespace conan_vs_extension
{
    /**
     * This is the class that implements the package exposed by this assembly.
     *
     * The minimum requirement for a class to be considered a valid package for Visual Studio
     * is to implement the IVsPackage interface and register itself with the shell.
     * This package uses the helper classes defined inside the Managed Package Framework (MPF)
     * to do it: it derives from the Package class that provides the implementation of the
     * IVsPackage interface and uses the registration attributes defined in the framework to
     * register itself and its components with the shell. These attributes tell the pkgdef creation
     * utility what data to put into .pkgdef file.
     * To get loaded into VS, the package must be referred by <Asset Type="Microsoft.VisualStudio.VsPackage" ...> in .vsixmanifest file.
     */
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(ConanToolWindowPackage.PackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    [ProvideOptionPage(typeof(ConanOptionsPage), "Conan Package Manager", "General", 0, 0, true)]
    [ProvideOptionPage(typeof(ConanPackageSourcesOptionsPage), "Conan Package Manager", "Package Sources", 0, 0, true)]
    public sealed class ConanToolWindowPackage : AsyncPackage
    {
        /**
         * ConanToolWindowPackage GUID string.
         */
        public const string PackageGuidString = "aa174917-4533-456c-b017-3e359a30f0e2";

        /**
         * Initializes a new instance of the ConanToolWindowPackage class.
         */
        public ConanToolWindowPackage()
        {
            // Inside this method you can place any initialization code that does not require
            // any Visual Studio service because at this point the package object is created but
            // not sited yet inside Visual Studio environment. The place to do all the other
            // initialization is the Initialize method.
        }

        #region Package Members
        private BuildEventsHandler _event_handler;

        /**
         * Initialization of the package; this method is called right after the package is sited, so this is the place
         * where you can put all the initialization code that rely on services provided by VisualStudio.
         *
         * @param cancellationToken A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.
         * @param progress A provider for progress updates.
         * @return A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.
         */
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // When initialized asynchronously, the current thread may be a background thread at this point.
            // Do any initialization that requires the UI thread after switching to the UI thread.
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Loc.Initialize();
            ConanOutputPane.EnsureInitialized();
            await ConanToolWindowCommand.InitializeAsync(this);

            ConanOptionsPage optionsPage = (ConanOptionsPage)GetDialogPage(typeof(ConanOptionsPage));
            GlobalSettings.ConanExecutablePath = optionsPage.ConanExecutablePath;

            DTE _dte = (DTE)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            if (_dte == null)
            {
                throw new InvalidOperationException("Cannot access DTE service.");
            }
            _event_handler = new BuildEventsHandler(_dte);
        }

        #endregion
    }
}
