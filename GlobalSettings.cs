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
        private static string _gitLabPrivateToken = string.Empty;
        public static string GitLabPrivateToken
        {
            get => _gitLabPrivateToken;
            set => _gitLabPrivateToken = value?.Trim();
        }
    }
}
