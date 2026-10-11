using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace conan_vs_extension
{
    /**
     * Answers "can this package/version be used by the current project?" and "which binaries
     * does the source publish for it?" before anything is installed, using Conan itself:
     * `conan graph info` resolves the package against the project's profile (settings such as
     * compiler.version, build_type, runtime) without installing anything, and `conan list
     * "name/version:*"` lists the binary packages a remote holds.
     */
    public static class ConanCompatibilityHelper
    {
        public enum CompatibilityState
        {
            /** Conan could not tell (error, timeout...) - never blocks the install. */
            Unknown,
            /** A prebuilt binary matches the project's profile (cache or remote). */
            PrebuiltAvailable,
            /** No matching binary, but the recipe can build the package from source. */
            WillBuild,
            /** The recipe refuses the project's configuration (invalid configuration). */
            Incompatible
        }

        public class CompatibilityResult
        {
            public CompatibilityState State { get; set; } = CompatibilityState.Unknown;

            /** Why it is incompatible, or why the check failed. */
            public string Reason { get; set; }

            /** Short description of the profile used, e.g. "msvc 194, Debug". */
            public string Profile { get; set; }
        }

        /**
         * Resolves name/version against the project's profile (`conan graph info`, nothing is
         * installed or built) and classifies the outcome.
         *
         * @param projectDirectory Directory holding the project's `.conan\<profile>` file.
         * @param profileName Profile file name under `.conan` (e.g. "Debug_x64"); the default profile is used when it does not exist yet.
         */
        public static async Task<CompatibilityResult> CheckAsync(string name, string version, string remoteName, string projectDirectory, string profileName)
        {
            var result = new CompatibilityResult();

            try
            {
                string profileArgument = File.Exists(Path.Combine(projectDirectory, ".conan", profileName))
                    ? $".conan/{profileName}"
                    : "default";
                string remoteArgument = string.IsNullOrWhiteSpace(remoteName) ? string.Empty : $" -r=\"{remoteName}\"";

                string arguments = $"graph info --requires=\"{name}/{version}\" -pr:h=\"{profileArgument}\" -pr:b=default{remoteArgument} --build=missing --format=json";
                ConanOutputPane.Log($"conan {arguments}");

                await ProjectConfigurationManager.EnsureDefaultProfileAsync();

                int exitCode;
                string stdout;
                string stderr;
                await ConanRemoteHelper.RecipeCacheGate.WaitAsync();
                try
                {
                    (exitCode, stdout, stderr) = await ConanRemoteHelper.RunConanAsync(arguments, 30000, projectDirectory);
                }
                finally
                {
                    ConanRemoteHelper.RecipeCacheGate.Release();
                }

                int jsonStart = stdout.IndexOf('{');
                if (jsonStart < 0)
                {
                    result.Reason = FirstLine(stderr, Loc.T($"conan returned code {exitCode}.", $"conan a retourné le code {exitCode}."));
                    ConanOutputPane.Log(Loc.T($"  -> check impossible: {result.Reason}", $"  -> vérification impossible : {result.Reason}"));
                    return result;
                }

                JObject root = JObject.Parse(stdout.Substring(jsonStart));
                JObject nodes = root["graph"]?["nodes"] as JObject;
                JObject node = nodes?.Properties()
                    .Select(p => p.Value as JObject)
                    .FirstOrDefault(n => ((string)n?["ref"] ?? string.Empty).StartsWith(name + "/", StringComparison.OrdinalIgnoreCase));

                if (node == null)
                {
                    result.Reason = FirstLine(stderr, Loc.T("Package not found in the dependency graph.", "Paquet introuvable dans le graphe de dépendances."));
                    return result;
                }

                JObject settings = node["settings"] as JObject;
                string compiler = (string)settings?["compiler"];
                string compilerVersion = (string)settings?["compiler.version"];
                string buildType = (string)settings?["build_type"];
                result.Profile = string.Join(", ", new[] { $"{compiler} {compilerVersion}".Trim(), buildType }.Where(s => !string.IsNullOrEmpty(s)));

                string binary = (string)node["binary"] ?? string.Empty;
                string invalid = node["info_invalid"]?.Type == JTokenType.String ? (string)node["info_invalid"] : null;

                if (binary.Equals("Invalid", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(invalid))
                {
                    result.State = CompatibilityState.Incompatible;
                    result.Reason = string.IsNullOrWhiteSpace(invalid) ? Loc.T("configuration refused by the recipe.", "configuration refusée par la recette.") : invalid.Trim();
                }
                else if (binary.Equals("Build", StringComparison.OrdinalIgnoreCase))
                {
                    result.State = CompatibilityState.WillBuild;
                }
                else if (binary.Equals("Missing", StringComparison.OrdinalIgnoreCase))
                {
                    result.State = CompatibilityState.Incompatible;
                    result.Reason = Loc.T("no binary exists for this profile and building is not possible.", "aucun binaire n'existe pour ce profil et la compilation n'est pas possible.");
                }
                else if (binary.Length > 0)
                {
                    // Cache, Download, Update, Editable, Skip...
                    result.State = CompatibilityState.PrebuiltAvailable;
                }

                ConanOutputPane.Log($"  -> {name}/{version} : {binary} ({result.Profile})");
            }
            catch (Exception ex)
            {
                result.Reason = ex.Message;
                ConanOutputPane.Log(Loc.T($"  -> check impossible: {ex.Message}", $"  -> vérification impossible : {ex.Message}"));
            }

            return result;
        }

        /**
         * The distinct binaries a remote publishes for name/version, one human-readable line each
         * (e.g. "msvc 194 - Debug - dynamic - x86_64 - c++17"). Empty when there are none, null
         * when the remote could not be queried.
         */
        public static async Task<List<string>> ListBinariesAsync(string name, string version, string remoteName)
        {
            if (string.IsNullOrWhiteSpace(remoteName))
            {
                return null;
            }

            try
            {
                string arguments = $"list \"{name}/{version}:*\" -r=\"{remoteName}\" --format=json";
                ConanOutputPane.Log($"conan {arguments}");

                (int exitCode, string stdout, string stderr) = await ConanRemoteHelper.RunConanAsync(arguments, 20000);
                int jsonStart = stdout.IndexOf('{');
                if (jsonStart < 0)
                {
                    ConanOutputPane.Log(Loc.T($"  -> failed (code {exitCode}): {FirstLine(stderr, string.Empty)}", $"  -> échec (code {exitCode}) : {FirstLine(stderr, string.Empty)}"));
                    return null;
                }

                JObject listing = JObject.Parse(stdout.Substring(jsonStart));

                // Unreachable remote: conan exits 0 with {"<remote>": {"error": "..."}}.
                if (listing.Properties().Any(p => p.Value is JObject o && o["error"]?.Type == JTokenType.String))
                {
                    ConanOutputPane.Log(Loc.T("  -> remote unreachable: published binaries not listed.", "  -> remote injoignable : binaires publiés non listés."));
                    return null;
                }

                var lines = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JToken package in listing.Descendants()
                    .OfType<JProperty>().Where(p => p.Name == "packages").Select(p => p.Value))
                {
                    foreach (JProperty entry in (package as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                    {
                        JObject settings = entry.Value?["info"]?["settings"] as JObject;
                        if (settings == null)
                        {
                            // No settings in the package id: header-only / platform-independent package.
                            lines.Add(Loc.T("compiler-independent (header-only)", "indépendant du compilateur (header-only)"));
                            continue;
                        }

                        // This extension only drives Windows projects: ignore other platforms' binaries.
                        string os = (string)settings["os"];
                        if (!string.IsNullOrEmpty(os) && !os.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var parts = new List<string>();
                        string compiler = (string)settings["compiler"];
                        string compilerVersion = (string)settings["compiler.version"];
                        if (!string.IsNullOrEmpty(compiler))
                        {
                            parts.Add($"{compiler} {compilerVersion}".Trim());
                        }

                        foreach (string key in new[] { "build_type", "compiler.runtime", "arch" })
                        {
                            string value = (string)settings[key];
                            if (!string.IsNullOrEmpty(value))
                            {
                                parts.Add(value);
                            }
                        }

                        string cppstd = (string)settings["compiler.cppstd"];
                        if (!string.IsNullOrEmpty(cppstd))
                        {
                            parts.Add("c++" + cppstd);
                        }

                        if (parts.Count > 0)
                        {
                            lines.Add(string.Join(" - ", parts));
                        }
                    }
                }

                ConanOutputPane.Log(Loc.T($"  -> {lines.Count} binary configuration(s).", $"  -> {lines.Count} configuration(s) de binaire."));
                return lines.ToList();
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log(Loc.T($"  -> exception: {ex.Message}", $"  -> exception : {ex.Message}"));
                return null;
            }
        }

        private static string FirstLine(string text, string fallback)
        {
            string line = (text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                ?? (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(line) ? fallback : line.Trim();
        }
    }
}
