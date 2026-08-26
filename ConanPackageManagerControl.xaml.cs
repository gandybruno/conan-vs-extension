using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VCProjectEngine;
using System.IO;
using System.Reflection;
using EnvDTE;
using System.Windows.Navigation;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace conan_vs_extension
{
    /// <summary>
    /// NuGet-style Conan Package Manager UI: "Parcourir" / "Installé" tabs, a search box, a
    /// package list, and a details pane with a version picker and Install/Uninstall actions.
    /// Unlike the older <see cref="ConanToolWindowControl"/> (a docked tool window that always
    /// acted on the solution's startup project), this control is opened as a document window
    /// (see <see cref="ConanPackageManagerWindowPane"/> and ConanToolWindowCommand) scoped to one
    /// specific EnvDTE Project, passed in at construction time - matching how NuGet's own
    /// "Manage NuGet Packages..." works per-project.
    ///
    /// Note: RootObject/Library/Requirements and MessageBoxWithInput are intentionally NOT
    /// redeclared here - they already exist (namespace-level) in ConanToolWindowControl.xaml.cs,
    /// compiled into the same assembly, and are reused as-is.
    /// </summary>
    public partial class ConanPackageManagerControl : UserControl
    {
        private enum PackageTab
        {
            Browse,
            Installed
        }

        /// <summary>
        /// One entry of the "Source de package" dropdown, NuGet-style: a Conan remote already
        /// configured on this machine (via `conan remote add`), browsed via the standard Conan v2
        /// protocol. No manual URL/token configuration in the plugin itself - everything comes
        /// from Conan's own remote list.
        /// </summary>
        private class PackageSourceEntry
        {
            public string DisplayName { get; set; }
            public string RemoteName { get; set; }
            public string RemoteUrl { get; set; }

            public override string ToString() => DisplayName;
        }

        private readonly Project _project;
        private RootObject _jsonData;
        private PackageTab _currentTab = PackageTab.Browse;
        private List<PackageSourceEntry> _sources = new List<PackageSourceEntry>();

        // Incremented every time a new metadata fetch starts, so a slow fetch for a package the
        // user has since clicked away from can detect it's stale and avoid overwriting the panel
        // with outdated info (NuGet has the same "fetching details" race to guard against).
        private int _metadataRequestId;

        public ConanPackageManagerControl(Project project)
        {
            this.InitializeComponent();
            _project = project;

            this.Loaded += ConanPackageManagerControl_Loaded;

            HeaderTitleTextBlock.Text = "Gestionnaire de packages Conan : " + (project?.Name ?? string.Empty);

            UpdateTabVisual();
            ToggleUIEnableState(IsConanInitialized());
            _ = InitializeAsync();
        }

        private void ConanPackageManagerControl_Loaded(object sender, RoutedEventArgs e)
        {
            VSColorTheme.ThemeChanged += OnThemeChanged;
            UpdateTheme();
        }

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            UpdateTheme();
        }

        public void UpdateTheme()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var currentThemeColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            var currentColor = Color.FromRgb(currentThemeColor.R, currentThemeColor.G, currentThemeColor.B);
            this.Foreground = new SolidColorBrush(currentColor);
        }

        private async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            await EnsureDefaultJsonFileExistsAsync();
            await LoadLibrariesFromJsonAsync();

            // Populate the "Source de package" dropdown entirely from `conan remote list`.
            // Selecting a source (including the initial auto-selection) triggers the first real
            // fetch via PackageSourceComboBox_SelectionChanged.
            await LoadPackageSourcesAsync();
        }

        private async Task LoadPackageSourcesAsync()
        {
            var sources = new List<PackageSourceEntry>();

            List<ConanRemoteHelper.ConanRemote> remotes = await ConanRemoteHelper.ListRemotesAsync();
            foreach (var remote in remotes)
            {
                sources.Add(new PackageSourceEntry
                {
                    DisplayName = remote.Name,
                    RemoteName = remote.Name,
                    RemoteUrl = remote.Url
                });
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _sources = sources;
            PackageSourceComboBox.ItemsSource = _sources;

            PackageSourceEntry toSelect = _sources.FirstOrDefault(s =>
                string.Equals(s.DisplayName, GlobalSettings.SelectedPackageSourceName, StringComparison.OrdinalIgnoreCase));

            // Setting SelectedItem fires PackageSourceComboBox_SelectionChanged, which kicks off
            // the first real fetch for whichever source ends up selected.
            PackageSourceComboBox.SelectedItem = toSelect ?? _sources.FirstOrDefault();
        }

        private void PackageSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (PackageSourceComboBox.SelectedItem is PackageSourceEntry entry)
            {
                GlobalSettings.SelectedPackageSourceName = entry.DisplayName;
                _ = UpdateJsonDataAsync(false);
            }
        }

        private async Task EnsureDefaultJsonFileExistsAsync()
        {
            string userConanFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".conan-vs-extension");
            string jsonFilePath = Path.Combine(userConanFolder, "targets-data.json");

            if (!File.Exists(jsonFilePath))
            {
                if (!Directory.Exists(userConanFolder))
                {
                    Directory.CreateDirectory(userConanFolder);
                }

                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = "conan_vs_extension.Resources.targets-data.json";
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                using (var reader = new StreamReader(stream))
                {
                    string jsonContent = await reader.ReadToEndAsync();
                    using (var writer = new StreamWriter(jsonFilePath))
                    {
                        await writer.WriteAsync(jsonContent);
                    }
                }
            }
        }

        private async Task LoadLibrariesFromJsonAsync()
        {
            string userConanFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".conan-vs-extension");
            string jsonFilePath = Path.Combine(userConanFolder, "targets-data.json");

            string json = await Task.Run(() => File.ReadAllText(jsonFilePath));
            _jsonData = JsonConvert.DeserializeObject<RootObject>(json);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            FilterListView(LibrarySearchTextBox.Text);
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FilterListView(LibrarySearchTextBox.Text);
        }

        private void BrowseTabButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _currentTab = PackageTab.Browse;
            UpdateTabVisual();
            FilterListView(LibrarySearchTextBox.Text);
        }

        private void InstalledTabButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _currentTab = PackageTab.Installed;
            UpdateTabVisual();
            FilterListView(LibrarySearchTextBox.Text);
        }

        private void UpdateTabVisual()
        {
            bool browseActive = _currentTab == PackageTab.Browse;
            BrowseTabUnderline.Visibility = browseActive ? Visibility.Visible : Visibility.Hidden;
            InstalledTabUnderline.Visibility = browseActive ? Visibility.Hidden : Visibility.Visible;
            BrowseTabButton.Opacity = browseActive ? 1.0 : 0.7;
            InstalledTabButton.Opacity = browseActive ? 0.7 : 1.0;
        }

        private bool IsSupportedProject()
        {
            return _project != null && _project.Object is VCProject;
        }

        private string GetProjectDirectory()
        {
            return Path.GetDirectoryName(_project.FullName);
        }

        private void FilterListView(string searchText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_jsonData == null || _jsonData.Libraries == null) return;

            PackagesListBox.Items.Clear();

            bool supported = IsSupportedProject();
            UnsupportedProjectType.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;

            var filteredLibraries = _jsonData.Libraries
                .Where(kv => kv.Key.IndexOf(searchText ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (_currentTab == PackageTab.Installed)
            {
                if (supported)
                {
                    string projectDirectory = GetProjectDirectory();
                    var requirements = ConanFileManager.GetConandataRequirements(projectDirectory);
                    foreach (var requirement in requirements)
                    {
                        string key = requirement.Split('/')[0];
                        if (filteredLibraries.Any(library => library.Key == key))
                        {
                            PackagesListBox.Items.Add(key);
                        }
                    }
                }
                // else: nothing to show - the UnsupportedProjectType banner already explains why.
            }
            else
            {
                foreach (var library in filteredLibraries)
                {
                    PackagesListBox.Items.Add(library.Key);
                }
            }
        }

        private void PackagesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (PackagesListBox.SelectedItem is string selectedItem)
            {
                UpdateLibraryInfo(selectedItem);
            }
            else
            {
                DetailsPanel.Visibility = Visibility.Collapsed;
                NoSelectionTextBlock.Visibility = Visibility.Visible;
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            System.Diagnostics.Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private void UpdateLibraryInfo(string name)
        {
            if (_jsonData != null && _jsonData.Libraries.ContainsKey(name))
            {
                var library = _jsonData.Libraries[name];
                UpdatePanel(name, library.Versions);
            }
        }

        public void UpdatePanel(string name, List<string> versions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsSupportedProject())
            {
                DetailsPanel.Visibility = Visibility.Collapsed;
                NoSelectionTextBlock.Visibility = Visibility.Collapsed;
                return;
            }

            NoSelectionTextBlock.Visibility = Visibility.Collapsed;
            DetailsPanel.Visibility = Visibility.Visible;

            LibraryNameLabel.Content = name;
            VersionsComboBox.ItemsSource = versions;
            VersionsComboBox.SelectedIndex = 0;

            MoreInfoHyperlink.NavigateUri = GetMoreInfoUri(name);
            GitHubRecipeLink.NavigateUri = new Uri("https://example.com/");

            string projectDirectory = GetProjectDirectory();
            var requirements = ConanFileManager.GetConandataRequirements(projectDirectory);
            string installedRequirement = requirements.FirstOrDefault(r => r.StartsWith(name + "/"));
            bool isInstalled = installedRequirement != null;

            InstallButton.Visibility = isInstalled ? Visibility.Collapsed : Visibility.Visible;
            RemoveButton.Visibility = isInstalled ? Visibility.Visible : Visibility.Collapsed;
            VersionsComboBox.IsEnabled = !isInstalled;

            InstalledStatusTextBlock.Text = isInstalled
                ? $"Installé : {installedRequirement.Substring(name.Length + 1)}"
                : "Non installé dans ce projet.";
        }

        private void VersionsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (LibraryNameLabel.Content is string name && VersionsComboBox.SelectedItem is string version)
            {
                _ = RefreshPackageMetadataAsync(name, version);
            }
        }

        /// <summary>
        /// Fetches recipe metadata (description, license, homepage, author, topics) straight from
        /// the recipe's own conanfile.py, on-demand for the selected package/version - mirroring
        /// how NuGet's "Manage Packages" pane only downloads a package's nuspec once it's selected,
        /// rather than for the whole list up front (which would be far too slow).
        /// </summary>
        private async Task RefreshPackageMetadataAsync(string name, string version)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            int requestId = ++_metadataRequestId;

            MetadataLoadingTextBlock.Visibility = Visibility.Visible;
            MetadataUnavailableTextBlock.Visibility = Visibility.Collapsed;
            PackageDescriptionTextBlock.Visibility = Visibility.Collapsed;
            PackageLicensePanel.Visibility = Visibility.Collapsed;
            PackageAuthorPanel.Visibility = Visibility.Collapsed;
            PackageTopicsPanel.Visibility = Visibility.Collapsed;
            PackageHomepageLine.Visibility = Visibility.Collapsed;

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version) ||
                !(PackageSourceComboBox.SelectedItem is PackageSourceEntry source))
            {
                MetadataLoadingTextBlock.Visibility = Visibility.Collapsed;
                return;
            }

            ConanRemoteHelper.RecipeMetadata metadata =
                await ConanRemoteHelper.GetRecipeMetadataAsync(name, version, source.RemoteName);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (requestId != _metadataRequestId)
            {
                // The user has since selected a different package/version - this result is stale.
                return;
            }

            MetadataLoadingTextBlock.Visibility = Visibility.Collapsed;

            if (metadata == null)
            {
                // conan couldn't retrieve/read the recipe (wrong CLI flag for this conan version,
                // remote unreachable, recipe not found...) - say so instead of leaving the panel
                // silently empty. The real error is logged to Debug.WriteLine.
                MetadataUnavailableTextBlock.Visibility = Visibility.Visible;
                return;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Description))
            {
                PackageDescriptionTextBlock.Text = metadata.Description;
                PackageDescriptionTextBlock.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrWhiteSpace(metadata.License))
            {
                PackageLicenseTextBlock.Text = metadata.License;
                PackageLicensePanel.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Author))
            {
                PackageAuthorTextBlock.Text = metadata.Author;
                PackageAuthorPanel.Visibility = Visibility.Visible;
            }

            if (metadata.Topics != null && metadata.Topics.Count > 0)
            {
                PackageTopicsTextBlock.Text = string.Join(", ", metadata.Topics);
                PackageTopicsPanel.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Homepage) &&
                Uri.TryCreate(metadata.Homepage, UriKind.Absolute, out Uri homepageUri))
            {
                PackageHomepageHyperlink.NavigateUri = homepageUri;
                PackageHomepageLine.Visibility = Visibility.Visible;
            }
        }

        private Uri GetMoreInfoUri(string name)
        {
            if (PackageSourceComboBox.SelectedItem is PackageSourceEntry source)
            {
                Match match = Regex.Match(source.RemoteUrl ?? string.Empty,
                    @"^(?<base>https?://[^/]+)/api/v4/projects/(?<projectId>[^/?]+)/packages/conan/?$",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    return new Uri($"{match.Groups["base"].Value}/-/packages?search[]={Uri.EscapeDataString(name)}");
                }

                if (Uri.TryCreate(source.RemoteUrl, UriKind.Absolute, out Uri remoteUri))
                {
                    return remoteUri;
                }
            }

            return new Uri("about:blank");
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsSupportedProject() || LibraryNameLabel.Content == null) return;

            var selectedLibrary = LibraryNameLabel.Content.ToString();
            var selectedVersion = VersionsComboBox.SelectedItem?.ToString();
            if (selectedVersion == null) return;

            string projectDirectory = GetProjectDirectory();

            ConanFileManager.ReCreateConanfile(projectDirectory);

            string conandataPath = Path.Combine(projectDirectory, "conandata.yml");
            if (!File.Exists(conandataPath))
            {
                ConanFileManager.ReCreateConanData(projectDirectory);
            }

            if (ConanFileManager.IsFileCommentGuarded(conandataPath))
            {
                ConanFileManager.WriteNewRequirement(projectDirectory, selectedLibrary + "/" + selectedVersion);
                MessageBox.Show($"Requirement {selectedLibrary}/{selectedVersion} added to conandata.yml", "Conan C/C++ Package Manager");
            }
            else
            {
                MessageBox.Show($"Requirement {selectedLibrary}/{selectedVersion} could not be added to conandata.yml because it was modified. Please, update the file manually.",
                                "Conan C/C++ Package Manager",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
            }

            _ = ProjectConfigurationManager.SaveConanPrebuildEventsAllConfigAsync(_project);
            FilterListView(LibrarySearchTextBox.Text);
            UpdateLibraryInfo(selectedLibrary);
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (LibraryNameLabel.Content == null) return;

            var selectedLibrary = LibraryNameLabel.Content.ToString();
            var selectedVersion = VersionsComboBox.SelectedItem?.ToString();
            if (selectedVersion == null) return;

            string projectDirectory = GetProjectDirectory();
            string conandataPath = Path.Combine(projectDirectory, "conandata.yml");

            if (ConanFileManager.IsFileCommentGuarded(conandataPath))
            {
                ConanFileManager.RemoveRequirement(projectDirectory, selectedLibrary + "/" + selectedVersion);
                MessageBox.Show($"Removing {selectedLibrary} version {selectedVersion}", "Conan C/C++ Package Manager");
            }
            else
            {
                MessageBox.Show($"Requirement {selectedLibrary}/{selectedVersion} could not be removed from conandata.yml because it was modified. Please, update the file manually.",
                                "Conan C/C++ Package Manager",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
            }

            FilterListView(LibrarySearchTextBox.Text);
            UpdateLibraryInfo(selectedLibrary);
        }

        private void ShowConfigurationDialog()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DTE dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
            dte?.ExecuteCommand("Tools.Options", GuidList.strConanOptionsPage);
        }

        private bool IsConanInitialized()
        {
            return !string.IsNullOrEmpty(GlobalSettings.ConanExecutablePath);
        }

        private void ToggleUIEnableState(bool enabled)
        {
            LibrarySearchTextBox.IsEnabled = enabled;
            UpdateButton.IsEnabled = enabled;
            PackagesListBox.IsEnabled = enabled;
            BrowseTabButton.IsEnabled = enabled;
            InstalledTabButton.IsEnabled = enabled;
            PackageSourceComboBox.IsEnabled = enabled;

            LibrarySearchTextBox.Text = enabled ? "" : "Cliquez sur 'Configurer' pour définir le chemin de Conan -->";
        }

        private void Configuration_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowConfigurationDialog();
            ToggleUIEnableState(IsConanInitialized());
        }

        // ---- Package source fetching: entirely driven by Conan's own remotes (`conan remote
        // list`), no manual URL/token configuration in the plugin. See
        // FetchLibrariesFromConanRemoteAsync for the standard-protocol-first, GitLab-REST-fallback
        // logic. ----

        private async Task UpdateJsonDataAsync(bool popUp)
        {
            if (!(PackageSourceComboBox.SelectedItem is PackageSourceEntry source))
            {
                if (popUp)
                {
                    MessageBox.Show(
                        "Aucun remote Conan n'est configuré sur cette machine. Ajoutez-en un avec 'conan remote add <nom> <url>' puis cliquez sur Actualiser.",
                        "Conan C/C++ Package Manager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return;
            }

            LoadingOverlay.Visibility = Visibility.Visible;
            try
            {
                Dictionary<string, HashSet<string>> libraries = await FetchLibrariesFromConanRemoteAsync(source, popUp);

                if (libraries == null)
                {
                    return;
                }

                WriteLibrariesJson(libraries);
                await LoadLibrariesFromJsonAsync();

                if (popUp)
                    MessageBox.Show($"Liste des packages conan mise à jour ({libraries.Count} bibliothèque(s)) depuis '{source.DisplayName}'.", "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (popUp)
                    MessageBox.Show($"Erreur mise à jour : {ex.Message}", "Error - Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Browses a Conan remote already configured on this machine. Tries the standard Conan v2
        /// protocol first (works out of the box for Artifactory, conancenter, a generic
        /// conan_server...); if that comes back empty and the remote's URL matches GitLab's Conan
        /// v2 registry pattern, falls back to GitLab's own Packages REST API, since GitLab's
        /// search endpoint doesn't support a bare "*" wildcard (confirmed: q=* returns nothing,
        /// q=he* works fine) even though its own documentation says it should.
        /// </summary>
        private async Task<Dictionary<string, HashSet<string>>> FetchLibrariesFromConanRemoteAsync(PackageSourceEntry source, bool popUp)
        {
            Dictionary<string, HashSet<string>> libraries = await ConanRemoteHelper.ListAllPackagesAsync(source.RemoteName);

            if (libraries != null && libraries.Count > 0)
            {
                return libraries;
            }

            Match gitLabMatch = Regex.Match(source.RemoteUrl ?? string.Empty,
                @"^(?<base>https?://[^/]+)/api/v4/projects/(?<projectId>[^/?]+)/packages/conan/?$",
                RegexOptions.IgnoreCase);

            if (gitLabMatch.Success)
            {
                return await FetchLibrariesFromGitLabPackagesApiAsync(
                    gitLabMatch.Groups["base"].Value,
                    gitLabMatch.Groups["projectId"].Value,
                    source.RemoteName,
                    popUp);
            }

            if (popUp)
            {
                MessageBox.Show($"Aucun package trouvé sur le remote '{source.RemoteName}'.", "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return libraries ?? new Dictionary<string, HashSet<string>>();
        }

        /// <summary>
        /// GitLab's Packages REST API, keyed to one specific Conan remote's name so each
        /// GitLab-backed remote can have its own access token (see
        /// GlobalSettings.GetGitLabTokenForRemote/SetGitLabTokenForRemote).
        /// </summary>
        private async Task<Dictionary<string, HashSet<string>>> FetchLibrariesFromGitLabPackagesApiAsync(string baseUrl, string projectId, string remoteName, bool popUp)
        {
            string token = GlobalSettings.GetGitLabTokenForRemote(remoteName);

            if (string.IsNullOrWhiteSpace(token) && popUp)
            {
                string input = MessageBoxWithInput.Show(
                    $"Le remote GitLab '{remoteName}' nécessite un token d'accès (scope 'read_api') pour lister ses packages.\nEntrez le token (laissez vide pour un projet public) :",
                    "Conan C/C++ Package Manager", string.Empty);
                if (input != null)
                {
                    token = input;
                    GlobalSettings.SetGitLabTokenForRemote(remoteName, token);
                }
            }

            using (var httpClient = new HttpClient())
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    httpClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
                }

                var recipeRefs = new List<string>();
                const int perPage = 100;
                int page = 1;

                while (true)
                {
                    string pageUrl = $"{baseUrl}/api/v4/projects/{projectId}/packages?package_type=conan&per_page={perPage}&page={page}";
                    string jsonContent = await httpClient.GetStringAsync(pageUrl);

                    JArray pkgArray = JArray.Parse(jsonContent);
                    if (pkgArray.Count == 0)
                    {
                        break;
                    }

                    recipeRefs.AddRange(pkgArray.Select(item => item["name"]?.ToString()).Where(name => name != null));

                    if (pkgArray.Count < perPage)
                    {
                        break;
                    }

                    page++;
                }

                return ParseRecipeReferences(recipeRefs, @"^([\w\-.]+)\/([\w\d.\-]+(?:@[\w\-.]+\/[\w\-.]+)?)$");
            }
        }

        private Dictionary<string, HashSet<string>> ParseRecipeReferences(IEnumerable<string> recipeRefs, string pattern)
        {
            var libraries = new Dictionary<string, HashSet<string>>();
            Regex regex = new Regex(pattern);

            foreach (var fullName in recipeRefs)
            {
                Match match = regex.Match(fullName);
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

        private void WriteLibrariesJson(Dictionary<string, HashSet<string>> libraries)
        {
            string userConanFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".conan-vs-extension");
            string jsonFilePath = Path.Combine(userConanFolder, "targets-data.json");

            if (!Directory.Exists(userConanFolder))
            {
                Directory.CreateDirectory(userConanFolder);
            }

            JObject outputJson = new JObject();
            JObject librariesJson = new JObject();
            foreach (var kvp in libraries)
            {
                librariesJson[kvp.Key] = new JObject
                {
                    ["versions"] = new JArray(kvp.Value)
                };
            }
            outputJson["libraries"] = librariesJson;

            string formattedJson = JsonConvert.SerializeObject(outputJson, Formatting.Indented);
            File.WriteAllText(jsonFilePath, formattedJson);
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            _ = UpdateJsonDataAsync(true);
        }
    }
}
