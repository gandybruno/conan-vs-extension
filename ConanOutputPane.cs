using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Threading.Tasks;

namespace conan_vs_extension
{
    /**
     * A dedicated "Conan" pane in Visual Studio's Output window, so the user can see exactly
     * what commands the plugin runs (and their result) instead of it all being invisible unless
     * they attach a debugger. Every `conan` invocation the extension makes (install, list,
     * remote management, recipe metadata lookups...) is logged here.
     *
     * Like CMake's own Output pane, this surfaces itself automatically as soon as something is
     * written to it - no dedicated "Logs" button needed. It brings the Output window forward
     * (and selects the "Conan" pane) without stealing keyboard focus from wherever the user
     * currently is, exactly like VS's Build/CMake panes do.
     */
    public static class ConanOutputPane
    {
        private static readonly Guid PaneGuid = new Guid("2f9a6b3e-9b7a-4a7a-9a9e-6c1c3f9c9a10");
        private static IVsOutputWindowPane _pane;

        // Throttles how often Log() schedules a "bring the pane into view" hop to the UI thread.
        // A `conan install --build=missing` can stream hundreds of compiler output lines in quick
        // succession - without this, every single one would queue its own UI-thread hop.
        private static long _lastBroughtIntoViewTicks;

        /**
         * Creates (or finds) the "Conan" Output window pane. Must be called from the UI thread,
         * typically once from the package's InitializeAsync.
         */
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

        /**
         * Writes a timestamped line to the "Conan" Output pane and brings that pane into view
         * (without stealing focus) so the user sees activity happening in real time, the same way
         * VS's own Build/CMake output panes pop into view on their own. Safe to call from any
         * thread - the write itself is thread-safe, and bringing the pane into view is hopped onto
         * the UI thread in the background (fire-and-forget) so callers on a background thread (the
         * usual case - conan runs as an external process) are never blocked by it.
         */
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

            long nowTicks = DateTime.UtcNow.Ticks;
            long lastTicks = System.Threading.Interlocked.Read(ref _lastBroughtIntoViewTicks);
            if (nowTicks - lastTicks > TimeSpan.TicksPerSecond * 2 &&
                System.Threading.Interlocked.CompareExchange(ref _lastBroughtIntoViewTicks, nowTicks, lastTicks) == lastTicks)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(BringIntoViewAsync);
            }
        }

        /**
         * Selects the "Conan" pane and shows the Output tool window (creating/uncollapsing it if
         * needed) without activating it - i.e. it appears on screen but the keyboard focus/caret
         * stays wherever the user was, exactly like a build starting doesn't yank focus out of the
         * editor.
         */
        private static async Task BringIntoViewAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                EnsureInitialized();
                _pane?.Activate();

                var uiShell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;
                if (uiShell == null)
                {
                    return;
                }

                Guid outputWindowGuid = VSConstants.StandardToolWindows.Output;
                if (uiShell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fFindFirst, ref outputWindowGuid, out IVsWindowFrame frame) == VSConstants.S_OK
                    && frame != null)
                {
                    frame.ShowNoActivate();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConanOutputPane] BringIntoViewAsync failed: {ex}");
            }
        }
    }
}
