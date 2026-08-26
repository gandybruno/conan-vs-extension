using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    }
}
