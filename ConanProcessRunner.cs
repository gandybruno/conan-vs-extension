using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace conan_vs_extension
{
    /**
     * Runs the long `conan` commands (`install`, `export-pkg`...): the ones that can compile for
     * minutes and whose output must be shown while it happens. Short commands that only return
     * a result go through ConanRemoteHelper.RunConanAsync instead.
     */
    public static class ConanProcessRunner
    {
        // A command that prints nothing at all for this long is considered hung.
        private const int SilenceTimeoutMs = 15 * 60 * 1000;

        /**
         * Runs `conan <arguments>` in workingDirectory, streaming its stdout/stderr line-by-line
         * into ConanOutputPane as they arrive (rather than only showing the full output once the
         * process exits - a `--build=missing` compiling a large binary from source can otherwise
         * look frozen for several minutes). There is no limit on the total duration: the process
         * is only stopped when cancellationToken is cancelled, or when it has printed nothing at
         * all for SilenceTimeoutMs - a hung process - so the UI never gets stuck waiting
         * indefinitely.
         *
         * @return (true, null) on success; (false, an error message built from stderr/stdout or the cancellation/timeout/exception) otherwise.
         */
        public static async Task<(bool success, string error)> RunStreamingAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            // "conan install", "conan export-pkg"...: how the command is named in messages.
            string command = "conan " + arguments.Split(' ')[0];

            var psi = new ProcessStartInfo
            {
                FileName = GlobalSettings.GetResolvedConanExecutablePath(),
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            ConanOutputPane.Log($"[{System.IO.Path.GetFileName(workingDirectory)}] conan {arguments}");

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            var processExited = new TaskCompletionSource<bool>();
            var cancelled = new TaskCompletionSource<bool>();
            long lastOutputTicks = DateTime.UtcNow.Ticks;

            try
            {
                using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
                        stdoutBuilder.AppendLine(e.Data);
                        ConanOutputPane.Log(e.Data);
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
                        stderrBuilder.AppendLine(e.Data);
                        ConanOutputPane.Log(e.Data);
                    };
                    process.Exited += (s, e) => processExited.TrySetResult(true);

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Compiling missing binaries has no predictable duration: wait as long as the
                    // command keeps working. It is only stopped on the user's request, or when it
                    // hangs (waiting on input, a stalled download...) - told apart from a long
                    // build by its total silence.
                    using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                    {
                        while (true)
                        {
                            Task completed = await Task.WhenAny(processExited.Task, cancelled.Task, Task.Delay(30000));
                            if (completed == processExited.Task)
                            {
                                break;
                            }

                            if (completed == cancelled.Task)
                            {
                                await Task.Run(() => KillProcessTree(process));
                                ConanOutputPane.Log(Loc.T("  -> cancelled by the user, process stopped.", "  -> annulé par l'utilisateur, processus arrêté."));
                                return (false, Loc.T($"'{command}' was cancelled.", $"'{command}' a été annulé."));
                            }

                            long silenceMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastOutputTicks)) / TimeSpan.TicksPerMillisecond;
                            if (silenceMs > SilenceTimeoutMs)
                            {
                                await Task.Run(() => KillProcessTree(process));
                                int minutes = SilenceTimeoutMs / 60000;
                                ConanOutputPane.Log(Loc.T($"  -> no output for {minutes} min, process stopped.", $"  -> aucune sortie depuis {minutes} min, processus arrêté."));
                                return (false, Loc.T($"'{command}' printed nothing for {minutes} minutes and was stopped.", $"'{command}' n'a rien affiché pendant {minutes} minutes et a été arrêté."));
                            }
                        }
                    }

                    // The parameterless WaitForExit() guarantees the async output/error readers
                    // have finished delivering their last buffered lines before we read the
                    // builders below (a timed WaitForExit(ms) does not give that guarantee) - the
                    // process has already exited at this point so this returns immediately.
                    await Task.Run(() => process.WaitForExit());

                    if (process.ExitCode != 0)
                    {
                        string error = stderrBuilder.Length > 0 ? stderrBuilder.ToString() : stdoutBuilder.ToString();
                        error = string.IsNullOrWhiteSpace(error) ? Loc.T($"conan returned code {process.ExitCode}.", $"conan a retourné le code {process.ExitCode}.") : error.Trim();
                        ConanOutputPane.Log(Loc.T($"  -> failed (code {process.ExitCode})", $"  -> échec (code {process.ExitCode})"));
                        return (false, error);
                    }

                    ConanOutputPane.Log("  -> OK");
                    return (true, null);
                }
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log(Loc.T($"  -> exception: {ex.Message}", $"  -> exception : {ex.Message}"));
                return (false, ex.Message);
            }
        }

        /**
         * Kills a process and everything it started (`taskkill /T`): `conan install --build=missing`
         * runs cmake, msbuild and the compiler as children, which Process.Kill alone would leave
         * running.
         */
        private static void KillProcessTree(Process process)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {process.Id} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var killer = Process.Start(psi))
                {
                    killer.WaitForExit(10000);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConanProcessRunner] taskkill failed: {ex.Message}");
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // already exited on its own
            }
        }
    }
}
