using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace conan_vs_extension{

    /**
     * Process-wide (in-memory, static) settings for the extension. Two different persistence
     * strategies are used side by side here, for two different reasons:
     *
     * - ConanExecutablePath is backed by ConanOptionsPage's DialogPage persistence, which
     * only flushes to disk when Tools > Options is closed with OK.
     * - SelectedPackageSourceName and GitLabRemoteTokensRaw are
     * written continuously at runtime (every time the user picks a different package source, or
     * types a GitLab token into the "browse" prompt) and are instead persisted immediately to a
     * small JSON file on disk (see PersistedSettings), since they need to survive
     * closing and reopening Visual Studio without ever going through Tools > Options.
     */
    public static class GlobalSettings
    {
        /**
         * The bare command resolved from the system PATH - the default when nothing was configured.
         */
        public const string SystemConanCommand = "conan";

        private static string _conanExecutablePath = SystemConanCommand;

        /**
         * Full path to the `conan` executable to invoke for every command this extension runs
         * (or the bare string "conan" to resolve it from the system PATH). Set from
         * ConanOptionsPage and read by every class that shells out to Conan
         * (ConanRemoteHelper, ProjectConfigurationManager, ...).
         */
        public static string ConanExecutablePath
        {
            get
            {
                return _conanExecutablePath;
            }
            set
            {
                _conanExecutablePath = value?.Trim();
            }

        }

        /**
         * The actual path/command to invoke for every `conan` process this extension starts.
         * Centralizes the "ConanExecutablePath, or the bare command 'conan' if unset" fallback
         * that used to be repeated (inconsistently - see below) at every call site in
         * ConanRemoteHelper and ProjectConfigurationManager.
         *
         * Falling back to "conan" (rather than an empty string) matters: an empty ProcessStartInfo
         * FileName - or an empty path baked into the generated conan_install.bat - fails outright,
         * whereas "conan" lets the OS resolve it from the system PATH, which is exactly what
         * "Use System Conan" in ConanOptionsPage is meant to do (it stores ConanExecutablePath as
         * the literal string "conan").
         *
         * @return ConanExecutablePath if one is configured, otherwise the bare command "conan".
         */
        public static string GetResolvedConanExecutablePath()
        {
            return string.IsNullOrWhiteSpace(ConanExecutablePath) ? SystemConanCommand : ConanExecutablePath;
        }

        /**
         * Whether the Conan command that would be run (see GetResolvedConanExecutablePath) can be
         * found: an explicit path must exist on disk, a bare command is looked up on the system
         * PATH (trying the usual Windows executable extensions). Only checks the file system -
         * it never starts the process.
         *
         * @return true if a Conan executable could be located.
         */
        public static bool IsConanAvailable()
        {
            string command = GetResolvedConanExecutablePath();

            if (command.IndexOfAny(new[] { '\\', '/' }) >= 0 || Path.IsPathRooted(command))
            {
                return File.Exists(command);
            }

            string[] extensions = { string.Empty, ".exe", ".cmd", ".bat" };
            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            foreach (string directory in pathVariable.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                foreach (string extension in extensions)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(directory.Trim().Trim('"'), command + extension)))
                        {
                            return true;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Malformed PATH entry - ignore it.
                    }
                }
            }

            return false;
        }
        // ---- Multi-remote support (NuGet-style "Source de package" list) ----
        //
        // These two are written continuously at runtime (every time the user picks a different
        // source, or types in a GitLab token) rather than through an explicit "Save" action, so
        // they can't rely on DialogPage's persistence - that only flushes to disk when the Options
        // dialog is closed with OK (or SaveSettingsToStorage() is called explicitly), which never
        // happens during normal use of the package manager window. Instead they're persisted to a
        // small JSON file next to the plugin's other local state (targets-data.json), written
        // immediately on every change, so the selected source/tokens survive closing and reopening
        // Visual Studio without requiring a trip through Tools > Options.

        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".conan-vs-extension", "plugin-settings.json");

        /**
         * Shape of the small JSON file at SettingsFilePath - deliberately NOT
         * encrypted at rest. GitLabRemoteTokensRaw can hold live GitLab access
         * tokens, so this file should be treated the same as any other plaintext credential file
         * on disk (readable by anything running as the same Windows user).
         */
        private class PersistedSettings
        {
            public string SelectedPackageSourceName { get; set; } = string.Empty;
            public string GitLabRemoteTokensRaw { get; set; } = string.Empty;
        }

        private static PersistedSettings _persisted;

        private static PersistedSettings Persisted => _persisted ?? (_persisted = LoadPersistedSettings());

        /**
         * Reads SettingsFilePath from disk, falling back to defaults if it is missing or unreadable.
         */
        private static PersistedSettings LoadPersistedSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    return JsonConvert.DeserializeObject<PersistedSettings>(json) ?? new PersistedSettings();
                }
            }
            catch
            {
                // Corrupt/unreadable file - fall back to defaults rather than break the extension.
            }

            return new PersistedSettings();
        }

        /**
         * Writes Persisted back to SettingsFilePath immediately (best-effort - failures are swallowed).
         */
        private static void SavePersistedSettings()
        {
            try
            {
                string folder = Path.GetDirectoryName(SettingsFilePath);
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.WriteAllText(SettingsFilePath, JsonConvert.SerializeObject(Persisted, Formatting.Indented));
            }
            catch
            {
                // Best-effort - losing this just means the selection/tokens won't be remembered.
            }
        }

        // Which entry of the package source dropdown was last selected, so the document window
        // remembers it across project/session re-opens.
        public static string SelectedPackageSourceName
        {
            get => Persisted.SelectedPackageSourceName;
            set
            {
                Persisted.SelectedPackageSourceName = value?.Trim() ?? string.Empty;
                SavePersistedSettings();
            }
        }

        // One GitLab access token per Conan remote (a machine can have several remotes pointing
        // at different GitLab projects/instances). Stored as "name1=token1;name2=token2" (each
        // part URL-escaped). Not meant to be edited directly - use
        // GetGitLabTokenForRemote/SetGitLabTokenForRemote below.
        public static string GitLabRemoteTokensRaw
        {
            get => Persisted.GitLabRemoteTokensRaw;
            set
            {
                Persisted.GitLabRemoteTokensRaw = value ?? string.Empty;
                SavePersistedSettings();
            }
        }

        /**
         * Returns the GitLab access token stored for remoteName, or an empty string if none was set for it.
         *
         * @param remoteName A Conan remote name, as returned by `conan remote list`.
         * @return The stored token, or an empty string if none is set.
         */
        public static string GetGitLabTokenForRemote(string remoteName)
        {
            if (!string.IsNullOrWhiteSpace(remoteName))
            {
                Dictionary<string, string> tokens = ParseRemoteTokens();
                if (tokens.TryGetValue(remoteName, out string token))
                {
                    return token;
                }
            }

            return string.Empty;
        }

        /**
         * Stores (or replaces) the GitLab access token used for one specific Conan remote and persists it immediately.
         *
         * @param remoteName A Conan remote name, as returned by `conan remote list`.
         * @param token The access token to store (plaintext - see PersistedSettings), or empty to clear it.
         */
        public static void SetGitLabTokenForRemote(string remoteName, string token)
        {
            if (string.IsNullOrWhiteSpace(remoteName))
            {
                return;
            }

            Dictionary<string, string> tokens = ParseRemoteTokens();
            tokens[remoteName] = token ?? string.Empty;

            GitLabRemoteTokensRaw = string.Join(";", tokens.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        }

        /**
         * Parses GitLabRemoteTokensRaw ("name1=token1;name2=token2", URL-escaped) into a lookup by remote name.
         */
        private static Dictionary<string, string> ParseRemoteTokens()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string raw = GitLabRemoteTokensRaw;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return result;
            }

            foreach (var entry in raw.Split(';'))
            {
                int separatorIndex = entry.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string name = Uri.UnescapeDataString(entry.Substring(0, separatorIndex));
                string token = Uri.UnescapeDataString(entry.Substring(separatorIndex + 1));
                result[name] = token;
            }

            return result;
        }
    }
}
