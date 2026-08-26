using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace conan_vs_extension{

    public enum PackageRegistrySource
    {
        JFrog,
        GitLab
    }

    public static class GlobalSettings
    {
        private static string _conanExecutablePath = string.Empty;

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

        // Which registry the package list should be fetched from.
        public static PackageRegistrySource PackageSource { get; set; } = PackageRegistrySource.JFrog;

        // ---- JFrog Artifactory settings ----
        private static string _jFrogPackagesUrl = "https://jfrog.example.com/artifactory/conan/.conan/packages.ref.json";
        public static string JFrogPackagesUrl
        {
            get => _jFrogPackagesUrl;
            set => _jFrogPackagesUrl = string.IsNullOrWhiteSpace(value) ? _jFrogPackagesUrl : value.Trim();
        }

        // ---- GitLab Package Registry (Conan) settings ----

        // Base URL of the GitLab instance, e.g. https://gitlab.com or https://gitlab.mycompany.com
        private static string _gitLabUrl = string.Empty;
        public static string GitLabUrl
        {
            get => _gitLabUrl;
            set => _gitLabUrl = value?.Trim().TrimEnd('/');
        }

        // Numeric project ID, or URL-encoded project path (e.g. "group%2Fsubgroup%2Fproject").
        private static string _gitLabProjectId = string.Empty;
        public static string GitLabProjectId
        {
            get => _gitLabProjectId;
            set => _gitLabProjectId = value?.Trim();
        }

        // Personal/Project access token with at least "read_api" scope.
        // Required only for private projects; leave empty for public ones.
        // Used by the legacy single-source "GitLab (manuel)" entry.
        private static string _gitLabPrivateToken = string.Empty;
        public static string GitLabPrivateToken
        {
            get => _gitLabPrivateToken;
            set => _gitLabPrivateToken = value?.Trim();
        }

        // ---- Multi-remote support (NuGet-style "Source de package" list) ----

        // Which entry of the package source dropdown was last selected, so the document window
        // remembers it across project/session re-opens.
        private static string _selectedPackageSourceName = string.Empty;
        public static string SelectedPackageSourceName
        {
            get => _selectedPackageSourceName;
            set => _selectedPackageSourceName = value?.Trim();
        }

        // One GitLab access token per Conan remote (a machine can have several remotes pointing
        // at different GitLab projects/instances). Stored as "name1=token1;name2=token2" (each
        // part URL-escaped) since DialogPage's default persistence only handles simple types like
        // string, not a Dictionary. Not meant to be edited directly - use
        // GetGitLabTokenForRemote/SetGitLabTokenForRemote below.
        private static string _gitLabRemoteTokensRaw = string.Empty;
        public static string GitLabRemoteTokensRaw
        {
            get => _gitLabRemoteTokensRaw;
            set => _gitLabRemoteTokensRaw = value ?? string.Empty;
        }

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

            // Fall back to the legacy single token, in case the same GitLab project was
            // previously configured through the manual GitLab source.
            return GitLabPrivateToken;
        }

        public static void SetGitLabTokenForRemote(string remoteName, string token)
        {
            if (string.IsNullOrWhiteSpace(remoteName))
            {
                return;
            }

            Dictionary<string, string> tokens = ParseRemoteTokens();
            tokens[remoteName] = token ?? string.Empty;

            _gitLabRemoteTokensRaw = string.Join(";", tokens.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        }

        private static Dictionary<string, string> ParseRemoteTokens()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(_gitLabRemoteTokensRaw))
            {
                return result;
            }

            foreach (var entry in _gitLabRemoteTokensRaw.Split(';'))
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
