using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Microsoft.VisualStudio.Shell;

namespace conan_vs_extension
{
    /**
     * Hosts ConanPackageSourcesControl (a WPF UserControl) inside the WinForms-based
     * Tools > Options dialog via an ElementHost - the same technique NuGet itself
     * uses for its own "Package Sources" page, since DialogPage's default UI is a plain
     * PropertyGrid and can't host a custom grid/buttons layout on its own.
     */
    [Guid(GuidList.strConanPackageSourcesOptionsPage)]
    public class ConanPackageSourcesOptionsPage : DialogPage
    {
        private ElementHost _host;

        protected override IWin32Window Window
        {
            get
            {
                if (_host == null)
                {
                    _host = new ElementHost
                    {
                        Dock = DockStyle.Fill,
                        Child = new ConanPackageSourcesControl()
                    };
                }

                return _host;
            }
        }
    }
}
