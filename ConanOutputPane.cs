using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;

namespace conan_vs_extension
{
    /// <summary>
    /// A dedicated "Conan" pane in Visual Studio's Output window, so the user can see exactly
    /// what commands the plugin runs (and their result) instead of it all being invisible unless
    /// they attach a debugger. Every `conan` invocation the extension makes (install, list,
    /// remote management, recipe metadata lookups...) should be logged here.
    /// </summary>
    public static class ConanOutputPane
    {
        private static readonly Guid PaneGuid = new Guid("2f9a6b3e-9b7a-4a7a-9a9e-6c1c3f9c9a10");
        private static IVsOutputWindowPane _pane;

        /// <summary>
        /// Creates (or finds) the "Conan" Output window pane. Must be called from the UI thread,
        /// typically once from the package's InitializeAsync.
        /// </summary>
        public static void EnsureInitialized()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_pane != null)
            {
                return;
            }

            try
            {
                var outputWindow = ServiceProvider.GlobalProvider.GetService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                if (outputWindow == null)
                {
                    return;
                }

                Guid paneGuid = PaneGuid;
                if (outputWindow.GetPane(ref paneGuid, out IVsOutputWindowPane existingPane) != VSConstants.S_OK || existingPane == null)
                {
                    outputWindow.CreatePane(ref paneGuid, "Conan", 1, 1);
                    outputWindow.GetPane(ref paneGuid, out existingPane);
                }

                _pane = existingPane;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConanOutputPane] EnsureInitialized failed: {ex}");
            }
        }

        /// <summary>
        /// Writes a timestamped line to the "Conan" Output pane. Safe to call from any thread.
        /// Falls back to Debug.WriteLine if the pane isn't available yet (e.g. called before
        /// package initialization finished).
        /// </summary>
        public static void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            if (_pane == null)
            {
                System.Diagnostics.Debug.WriteLine("[ConanOutputPane] " + line);
                return;
            }

            try
            {
                _pane.OutputStringThreadSafe(line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConanOutputPane] Log failed: {ex}");
            }
        }

        /// <summary>Brings the Output window to the front, focused on the "Conan" pane.</summary>
        public static void Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                EnsureInitialized();
                _pane?.Activate();

                DTE dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
                dte?.ExecuteCommand("View.Output");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConanOutputPane] Show failed: {ex}");
            }
        }
    }
}
