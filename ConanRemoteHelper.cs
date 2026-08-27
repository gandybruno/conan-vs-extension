using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace conan_vs_extension
{
    /// <summary>
    /// Reads the Conan remotes already configured on this machine (`conan remote list`) so the
    /// plugin can auto-fill its own GitLab URL / project ID settings instead of requiring the
    /// user to type them in a second time. Conan already knows the remote's address - it needs it
    /// for `conan install` - so we just parse it back out of the CLI output.
    ///
    /// This intentionally only recovers the URL and project ID. Conan does not expose stored
    /// remote credentials (PRIVATE-TOKEN / passwords) through `conan remote list` for security
    /// reasons, so the token still has to be entered manually in Tools > Options.
    ///
    /// Also note this only helps populate the settings used for "browse everything" against the
    /// GitLab Packages REST API (see ConanPackageManagerControl.FetchLibrariesFromGitLabAsync) -
    /// GitLab's own Conan v2 protocol implementation does not support a real wildcard "list all"
    /// search (confirmed: `q=*` returns nothing, `q=he*` works fine), so that REST API is still
    /// needed for the "Parcourir" tab. See the conversation history / README for details.
    /// </summary>
    public static class ConanRemoteHelper
    {
        public class ConanRemote
        {
            public string Name { get; set; }
            public string Url { get; set; }
            public bool Enabled { get; set; } = true;
            public bool VerifySsl { get; set; } = true;

            /// <summary>
            /// The username currently authenticated on this remote (`conan remote list-users`),
            /// or null/empty if anonymous - i.e. whether `conan remote login` was ever run for it.
            /// </summary>
            public string User { get; set; }
        }

        // Matches lines like:
        //   my-gitlab: https://gitlab.com/api/v4/projects/12345678/packages/conan [Verify SSL: True, Enabled: True]
        private static readonly Regex RemoteLineRegex = new Regex(@"^(?<name>\S+):\s+(?<url>\S+)\s+\[(?<attrs>[^\]]*)\]", RegexOptions.Compiled);

        // Matches the Conan remote URL form GitLab's Conan v2 registry uses:
        //   https://gitlab.com/api/v4/projects/12345678/packages/conan
        private static readonly Regex GitLabRemoteUrlRegex = new Regex(
            @"^(?<base>https?://[^/]+)/api/v4/projects/(?<projectId>[^/?]+)/packages/conan/?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Runs `conan remote list` and parses the configured remotes (name + URL).
        /// Returns an empty list (never throws) if conan isn't configured/reachable.
        /// </summary>
        public static async Task<List<ConanRemote>> ListRemotesAsync()
        {
            var remotes = new List<ConanRemote>();

            string conanPath = string.IsNullOrWhiteSpace(GlobalSettings.ConanExecutablePath)
                ? "conan"
                : GlobalSettings.ConanExecutablePath;

            var psi = new ProcessStartInfo
            {
                FileName = conanPath,
                Arguments = "remote list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(psi))
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit(10000));

                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        Match match = RemoteLineRegex.Match(line.Trim());
                        if (match.Success)
                        {
                            string attrs = match.Groups["attrs"].Value;

                            remotes.Add(new ConanRemote
                            {
                                Name = match.Groups["name"].Value,
                                Url = match.Groups["url"].Value,
                                Enabled = !Regex.IsMatch(attrs, @"Enabled:\s*False", RegexOptions.IgnoreCase),
                                VerifySsl = !Regex.IsMatch(attrs, @"Verify SSL:\s*False", RegexOptions.IgnoreCase)
                            });
                        }
                    }
                }
            }
            catch
            {
                // conan not found / not configured / timed out - caller treats an empty list the
                // same as "nothing detected" and falls back to manual configuration.
            }

            return remotes;
        }

        /// <summary>
        /// Looks for a configured Conan remote whose URL matches GitLab's Conan v2 registry
        /// pattern (".../api/v4/projects/{id}/packages/conan") and, if found, returns the base
        /// GitLab URL and project ID to use for the plugin's own GitLab Packages API calls.
        /// Returns null if no such remote is configured.
        /// </summary>
        public static async Task<(string baseUrl, string projectId, string remoteName)?> TryDetectGitLabRemoteAsync()
        {
            List<ConanRemote> remotes = await ListRemotesAsync();

            foreach (var remote in remotes)
            {
                Match match = GitLabRemoteUrlRegex.Match(remote.Url ?? string.Empty);
                if (match.Success)
                {
                    return (match.Groups["base"].Value, match.Groups["projectId"].Value, remote.Name);
                }
            }

            return null;
        }

        /// <summary>
        /// Lists every recipe available on a given Conan remote via the standard Conan v2
        /// protocol (`conan list "*" -r=&lt;remoteName&gt; --format=json`), which works out of the
        /// box for Artifactory, conancenter, a generic conan_server, etc. Returns null on any
        /// failure (conan not found, remote unreachable...), and an empty dictionary (not null)
        /// when the command succeeds but the remote genuinely has 0 matching recipes OR when the
        /// remote's search implementation doesn't support the bare "*" wildcard (a known GitLab
        /// limitation) - callers distinguish "genuinely empty" from "wildcard not supported" by
        /// their own means (e.g. falling back to a vendor-specific API for known URL patterns).
        /// </summary>
        public static Task<Dictionary<string, HashSet<string>>> ListAllPackagesAsync(string remoteName)
        {
            if (string.IsNullOrWhiteSpace(remoteName))
            {
                return Task.FromResult<Dictionary<string, HashSet<string>>>(null);
            }

            return RunConanListAsync($"list \"*\" -r={remoteName} --format=json");
        }

        /// <summary>
        /// Lists every recipe already present in the LOCAL Conan cache (`conan list "*" -c
        /// --format=json`) - i.e. packages already downloaded at some point, that Conan can use
        /// without reaching any remote at all. Used as the offline fallback when a remote can't be
        /// reached: showing "what the server has" while offline is misleading, so instead we show
        /// "what's genuinely usable right now". Returns null only if the `conan` executable itself
        /// couldn't be run; returns an empty dictionary (not null) if the cache is genuinely empty.
        /// </summary>
        public static Task<Dictionary<string, HashSet<string>>> ListLocalCachePackagesAsync()
        {
            return RunConanListAsync("list \"*\" -c --format=json");
        }

        /// <summary>
        /// Shared implementation behind ListAllPackagesAsync (a specific remote, via -r=) and
        /// ListLocalCachePackagesAsync (the local cache, via -c) - `conan list` returns the exact
        /// same JSON shape either way, keyed by "Local Cache" or the remote's name.
        /// </summary>
        private static async Task<Dictionary<string, HashSet<string>>> RunConanListAsync(string arguments)
        {
            string conanPath = string.IsNullOrWhiteSpace(GlobalSettings.ConanExecutablePath)
                ? "conan"
                : GlobalSettings.ConanExecutablePath;

            ConanOutputPane.Log($"conan {arguments}");

            try
            {
                (int exitCode, string output, string stderr) = await RunConanCommandAsync(conanPath, arguments, 20000);

                if (exitCode != 0)
                {
                    ConanOutputPane.Log($"  -> échec (code {exitCode}) : {(string.IsNullOrWhiteSpace(stderr) ? output : stderr)}");
                }

                int jsonStart = output.IndexOf('{');
                if (jsonStart < 0)
                {
                    return null;
                }

                JObject root = JObject.Parse(output.Substring(jsonStart));
                JProperty remoteProperty = root.Properties().FirstOrDefault();
                if (remoteProperty == null || !(remoteProperty.Value is JObject packagesObject))
                {
                    return null;
                }

                var libraries = new Dictionary<string, HashSet<string>>();
                var recipeRegex = new Regex(@"^([\w\-.]+)\/([\w\d.\-]+(?:@[\w\-.]+\/[\w\-.]+)?)$");

                foreach (var prop in packagesObject.Properties())
                {
                    Match match = recipeRegex.Match(prop.Name);
                    if (match.Success)
                    {
                        string libName = match.Groups[1].Value;
                        string version = match.Groups[2].Value;
                        if (!libraries.ContainsKey(libName))
                        {
                            libraries[libName] = new HashSet<string>();
                        }
                        libraries[libName].Add(version);
                    }
                }

                ConanOutputPane.Log($"  -> {libraries.Count} bibliothèque(s) trouvée(s).");
                return libraries;
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log($"  -> exception : {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Adds a new Conan remote (`conan remote add`), same effect as running the command in a
        /// terminal - lets the "Package Sources" Options page manage remotes NuGet-style instead of
        /// requiring the user to leave Visual Studio.
        /// </summary>
        public static Task<(bool success, string error)> AddRemoteAsync(string name, string url, bool verifySsl)
        {
            string sslFlag = verifySsl ? string.Empty : " --insecure";
            return RunConanRemoteCommandAsync($"remote add \"{name}\" \"{url}\"{sslFlag}");
        }

        /// <summary>Removes a Conan remote (`conan remote remove`).</summary>
        public static Task<(bool success, string error)> RemoveRemoteAsync(string name)
        {
            return RunConanRemoteCommandAsync($"remote remove \"{name}\"");
        }

        /// <summary>Changes a remote's URL and/or SSL verification (`conan remote update`).</summary>
        public static Task<(bool success, string error)> UpdateRemoteUrlAsync(string name, string url, bool verifySsl)
        {
            string sslFlag = verifySsl ? " --secure" : " --insecure";
            return RunConanRemoteCommandAsync($"remote update \"{name}\" --url=\"{url}\"{sslFlag}");
        }

        /// <summary>Enables a previously disabled remote (`conan remote enable`).</summary>
        public static Task<(bool success, string error)> EnableRemoteAsync(string name)
        {
            return RunConanRemoteCommandAsync($"remote enable \"{name}\"");
        }

        /// <summary>Disables a remote without removing it (`conan remote disable`).</summary>
        public static Task<(bool success, string error)> DisableRemoteAsync(string name)
        {
            return RunConanRemoteCommandAsync($"remote disable \"{name}\"");
        }

        // Matches a remote's own header line in `conan remote list-users` output, e.g. "my-gitlab:"
        // (the per-remote details that follow, like "Username: foo", are indented under it).
        private static readonly Regex RemoteUsersHeaderRegex = new Regex(@"^(?<name>\S+):\s*$", RegexOptions.Compiled);
        private static readonly Regex RemoteUsersUsernameRegex = new Regex(@"Username:\s*(?<user>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Runs `conan remote list-users` and returns, for every remote that currently has a
        /// logged-in user, its username - used to show authentication status next to each remote
        /// in the "Package Sources" Options page. A remote missing from the result is anonymous
        /// (never logged in, or logged out). Never throws; returns an empty dictionary on failure.
        /// </summary>
        public static async Task<Dictionary<string, string>> ListRemoteUsersAsync()
        {
            var users = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string conanPath = string.IsNullOrWhiteSpace(GlobalSettings.ConanExecutablePath)
                ? "conan"
                : GlobalSettings.ConanExecutablePath;

            ConanOutputPane.Log("conan remote list-users");

            try
            {
                (int exitCode, string stdout, string stderr) = await RunConanCommandAsync(conanPath, "remote list-users", 15000);
                if (exitCode != 0)
                {
                    ConanOutputPane.Log($"  -> échec (code {exitCode}) : {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
                    return users;
                }

                string currentRemote = null;
                foreach (var rawLine in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = rawLine.TrimEnd();

                    Match header = RemoteUsersHeaderRegex.Match(line.TrimStart());
                    if (header.Success)
                    {
                        currentRemote = header.Groups["name"].Value;
                        continue;
                    }

                    if (currentRemote == null)
                    {
                        continue;
                    }

                    Match userMatch = RemoteUsersUsernameRegex.Match(line);
                    if (userMatch.Success && !string.Equals(userMatch.Groups["user"].Value, "None", StringComparison.OrdinalIgnoreCase))
                    {
                        users[currentRemote] = userMatch.Groups["user"].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log($"  -> exception : {ex.Message}");
            }

            return users;
        }

        /// <summary>
        /// Authenticates on a remote (`conan remote login`), same effect as running it in a
        /// terminal. The password is never passed as a command-line argument (which would make it
        /// visible to anything that can see the process list) - it's piped through the child
        /// process's standard input instead, exactly like typing it at the interactive prompt.
        /// </summary>
        public static async Task<(bool success, string error)> LoginRemoteAsync(string name, string username, string password)
        {
            string conanPath = string.IsNullOrWhiteSpace(GlobalSettings.ConanExecutablePath)
                ? "conan"
                : GlobalSettings.ConanExecutablePath;

            string arguments = $"remote login \"{name}\" \"{username}\"";
            ConanOutputPane.Log($"conan {arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = conanPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(psi))
                {
                    await process.StandardInput.WriteLineAsync(password ?? string.Empty);
                    process.StandardInput.Close();

                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                    Task readsTask = Task.WhenAll(stdoutTask, stderrTask);

                    Task completed = await Task.WhenAny(readsTask, Task.Delay(20000));
                    if (completed != readsTask)
                    {
                        try { process.Kill(); } catch { /* already exited on its own */ }
                        ConanOutputPane.Log("  -> délai dépassé (20s), processus arrêté.");
                        return (false, "conan n'a pas répondu dans le délai imparti (20s).");
                    }

                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;
                    await Task.Run(() => process.WaitForExit(5000));

                    if (process.ExitCode != 0)
                    {
                        string error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                        ConanOutputPane.Log($"  -> échec (code {process.ExitCode}) : {error}");
                        return (false, string.IsNullOrWhiteSpace(error) ? $"conan a retourné le code {process.ExitCode}." : error.Trim());
                    }

                    ConanOutputPane.Log("  -> OK");
                    return (true, null);
                }
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log($"  -> exception : {ex.Message}");
                return (false, ex.Message);
            }
        }

        /// <summary>Clears a remote's stored authentication (`conan remote logout`).</summary>
        public static Task<(bool success, string error)> LogoutRemoteAsync(string name)
        {
            return RunConanRemoteCommandAsync($"remote logout \"{name}\"");
        }

        private static async Task<(bool success, string error)> RunConanRemoteCommandAsync(string arguments)
        {
            string conanPath = string.IsNullOrWhiteSpace(GlobalSettings.ConanExecutablePath)
                ? "conan"
                : GlobalSettings.ConanExecutablePath;

            ConanOutputPane.Log($"conan {arguments}");

            try
            {
                (int exitCode, string stdout, string stderr) = await RunConanCommandAsync(conanPath, arguments, 20000);
                if (exitCode != 0)
                {
                    string error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    ConanOutputPane.Log($"  -> échec (code {exitCode}) : {error}");
                    System.Diagnostics.Debug.WriteLine($"[ConanRemoteHelper] 'conan {arguments}' failed (exit {exitCode}): {error}");
                    return (false, string.IsNullOrWhiteSpace(error) ? $"conan a retourné le code {exitCode}." : error.Trim());
                }

                ConanOutputPane.Log("  -> OK");
                return (true, null);
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log($"  -> exception : {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ConanRemoteHelper] 'conan {arguments}' threw: {ex}");
                return (false, ex.Message);
            }
        }

        public class RecipeMetadata
        {
            public string Description { get; set; }
            public string License { get; set; }
            public string Homepage { get; set; }
            public string Author { get; set; }
            public List<string> Topics { get; set; }
        }

        /// <summary>
        /// Fetches the detail fields ConanCenter/NuGet show for a package (description, license,
        /// homepage, author, topics) straight from the recipe's own conanfile.py, on the given
        /// remote. Only the recipe is downloaded (`--only-recipe`, no binaries - this is the same
        /// lightweight text file ConanCenter's own web page is generated from, typically a few KB,
        /// nothing like the compiled binary packages), and it is removed from the local cache again
        /// immediately after being read, so browsing packages in this UI leaves nothing behind in
        /// `conan cache`. Returns null on any failure (remote unreachable, recipe not found,
        /// unsupported conan CLI flag, ...); callers treat that as "no details available". Failures
        /// are logged via Debug.WriteLine (visible in Visual Studio's Output window while debugging
        /// the extension) since there's no other way to see what conan actually said.
        /// </summary>
        public static async Task<RecipeMetadata> GetRecipeMetadataAsync(string libName, string version, string remoteName)
        {
            if (string.IsNullOrWhiteSpace(libName) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(remoteName))
            {
                return null;
            }

            string reference = $"{libName}/{version}";
            string conanPath = string.IsNullOrWhiteSpace(GlobalSettings.ConanExecutablePath)
                ? "conan"
                : GlobalSettings.ConanExecutablePath;

            try
            {
                (int downloadExitCode, string _, string downloadStderr) = await RunConanCommandAsync(
                    conanPath, $"download \"{reference}\" -r={remoteName} --only-recipe", 30000);
                if (downloadExitCode != 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ConanRemoteHelper] 'conan download {reference} --only-recipe' failed (exit {downloadExitCode}): {downloadStderr}");
                    return null;
                }

                (int cachePathExitCode, string cachePathOutput, string cachePathStderr) = await RunConanCommandAsync(
                    conanPath, $"cache path \"{reference}\"", 10000);
                if (cachePathExitCode != 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ConanRemoteHelper] 'conan cache path {reference}' failed (exit {cachePathExitCode}): {cachePathStderr}");
                    return null;
                }

                string cachePath = cachePathOutput
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .LastOrDefault(line => line.Length > 0);

                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ConanRemoteHelper] 'conan cache path {reference}' returned no usable path. Raw output: {cachePathOutput}");
                    return null;
                }

                string conanfilePath = Path.Combine(cachePath, "conanfile.py");
                if (!File.Exists(conanfilePath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ConanRemoteHelper] conanfile.py not found at '{conanfilePath}' (cache path returned '{cachePath}').");
                    return null;
                }

                string text = await Task.Run(() => File.ReadAllText(conanfilePath));

                var metadata = new RecipeMetadata
                {
                    Description = ExtractStringAttribute(text, "description"),
                    License = ExtractLicenseAttribute(text),
                    Homepage = ExtractStringAttribute(text, "homepage"),
                    Author = ExtractStringAttribute(text, "author"),
                    Topics = ExtractTopicsAttribute(text)
                };

                // Best-effort cleanup: this recipe was only downloaded to read its metadata, not
                // because the user asked to install it, so don't leave it sitting in the cache.
                try
                {
                    await RunConanCommandAsync(conanPath, $"remove \"{reference}\" -c", 15000);
                }
                catch
                {
                    // Not fatal - the metadata was already read successfully above.
                }

                return metadata;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConanRemoteHelper] GetRecipeMetadataAsync('{reference}', remote '{remoteName}') threw: {ex}");
                return null;
            }
        }

        private static async Task<(int exitCode, string stdout, string stderr)> RunConanCommandAsync(string conanPath, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = conanPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                Task readsTask = Task.WhenAll(stdoutTask, stderrTask);

                // Bound the actual wait, not just WaitForExit() afterwards: if `conan` never closes
                // its output (hangs waiting on a credential prompt, a stalled TLS handshake, a slow
                // proxy...) ReadToEndAsync alone would never return and the UI would stay stuck on
                // "chargement" forever, even with a perfectly good internet connection. If we hit
                // the timeout, kill the process and report it as a failure so callers can fall back
                // (e.g. to the local cache) instead of hanging.
                Task completed = await Task.WhenAny(readsTask, Task.Delay(timeoutMs));

                if (completed != readsTask)
                {
                    try { process.Kill(); } catch { /* already exited on its own */ }
                    ConanOutputPane.Log($"  -> délai dépassé ({timeoutMs / 1000}s), processus arrêté.");
                    return (-1, string.Empty, $"'conan {arguments}' n'a pas répondu dans le délai imparti ({timeoutMs / 1000}s).");
                }

                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                // The reads already completed above, so the process has necessarily exited (or is
                // about to) - this just gives it a moment to flush ExitCode.
                await Task.Run(() => process.WaitForExit(5000));
                return (process.ExitCode, stdout, stderr);
            }
        }

        // conanfile.py class attributes are plain Python assignments, e.g.:
        //   description = "A formatting library"
        //   homepage = "https://github.com/fmtlib/fmt"
        // Handles single/double-quoted strings and triple-quoted (multi-line) strings, since
        // "description" is often written as a longer triple-quoted block.
        private static string ExtractStringAttribute(string text, string attributeName)
        {
            Match match = Regex.Match(text, attributeName + "\\s*=\\s*\"\"\"(.*?)\"\"\"", RegexOptions.Singleline);
            if (!match.Success)
            {
                match = Regex.Match(text, attributeName + "\\s*=\\s*'''(.*?)'''", RegexOptions.Singleline);
            }
            if (!match.Success)
            {
                match = Regex.Match(text, attributeName + "\\s*=\\s*\"([^\"]*)\"");
            }
            if (!match.Success)
            {
                match = Regex.Match(text, attributeName + "\\s*=\\s*'([^']*)'");
            }
            if (!match.Success)
            {
                return null;
            }

            string value = match.Groups[1].Value.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        // "license" can be a single string ("MIT") or a tuple/list of several
        // (("MIT", "Apache-2.0")) when a recipe is dual-licensed.
        private static string ExtractLicenseAttribute(string text)
        {
            string single = ExtractStringAttribute(text, "license");
            if (single != null)
            {
                return single;
            }

            Match tupleMatch = Regex.Match(text, @"license\s*=\s*[\(\[]([^\)\]]*)[\)\]]", RegexOptions.Singleline);
            if (tupleMatch.Success)
            {
                List<string> values = Regex.Matches(tupleMatch.Groups[1].Value, "[\"']([^\"']+)[\"']")
                    .Cast<Match>()
                    .Select(m => m.Groups[1].Value)
                    .ToList();

                if (values.Count > 0)
                {
                    return string.Join(", ", values);
                }
            }

            return null;
        }

        // "topics" is always a tuple/list of short tag strings, e.g. topics = ("logging", "formatting").
        private static List<string> ExtractTopicsAttribute(string text)
        {
            Match match = Regex.Match(text, @"topics\s*=\s*[\(\[]([^\)\]]*)[\)\]]", RegexOptions.Singleline);
            if (!match.Success)
            {
                return null;
            }

            List<string> values = Regex.Matches(match.Groups[1].Value, "[\"']([^\"']+)[\"']")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

            return values.Count > 0 ? values : null;
        }
    }
}
