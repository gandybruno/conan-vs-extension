using Microsoft.VisualStudio.Shell;

namespace conan_vs_extension
{
    /**
     * Hosts ConanPackageManagerControl as a document window (an editor-style tab in
     * the main editing area), the same way NuGet's own "Manage NuGet Packages..." window is a
     * document rather than a docked tool window. This class mirrors NuGet.Client's
     * PackageManagerWindowPane: a plain WindowPane subclass with no IVsEditorFactory registration
     * required, since the window is created directly via IVsUIShell.CreateDocumentWindow
     * (see ConanToolWindowCommand.OpenPackageManagerAsync).
     */
    public class ConanPackageManagerWindowPane : WindowPane
    {
        private ConanPackageManagerControl _content;

        public ConanPackageManagerWindowPane(ConanPackageManagerControl control)
            : base(null)
        {
            _content = control;
        }

        /**
         * IVsWindowPane content shown inside the document window frame.
         */
        public override object Content
        {
            get { return _content; }
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    _content = null;
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
