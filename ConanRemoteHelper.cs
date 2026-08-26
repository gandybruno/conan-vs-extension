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
        }

        // Matches lines like:
        //   my-gitlab: https://gitlab.com/api/v4/projects/12345678/packages/conan [Verify SSL: True, Enabled: True]
        private static readonly Regex RemoteLineRegex = new Regex(@"^(?<name>\S+):\s+(?<url>\S+)\s+\[", RegexOptions.Compiled);

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
                            remotes.Add(new ConanRemote
                            {
                                Name = match.Groups["name"].Value,
                                Url = match.Groups["url"].Value
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
        public static async Task<Dictionary<string, HashSet<string>>> ListAllPackagesAsync(string remoteName)
        {
            if (string.IsNullOrWhiteSpace(remoteName))
            {
                return null;
            }

            string conanPath = string.IsNullOrWhiteSpace(GlobalSettings.ConanExecutablePath)
                ? "conan"
                : GlobalSettings.ConanExecutablePath;

            var psi = new ProcessStartInfo
            {
                FileName = conanPath,
                Arguments = $"list \"*\" -r={remoteName} --format=json",
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
                    await Task.Run(() => process.WaitForExit(20000));

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

                    return libraries;
                }
            }
            catch
            {
                return null;
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
                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                await Task.Run(() => process.WaitForExit(timeoutMs));
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
