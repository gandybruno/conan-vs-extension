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

        private readonly Project _project;
        private RootObject _jsonData;
        private PackageTab _currentTab = PackageTab.Browse;

        public ConanPackageManagerControl(Project project)
        {
            this.InitializeComponent();
            _project = project;

            this.Loaded += ConanPackageManagerControl_Loaded;

            HeaderTitleTextBlock.Text = "Gestionnaire de packages Conan : " + (project?.Name ?? string.Empty);
            PackageSourceLabel.Text = "Source : " + (GlobalSettings.PackageSource == PackageRegistrySource.GitLab ? "GitLab" : "JFrog");

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

            await CopyJsonFileFromResourceIfNeededAsync();
            await LoadLibrariesFromJsonAsync();
        }

        private async Task CopyJsonFileFromResourceIfNeededAsync()
        {
            // Update the local cache first (best-effort; keeps whatever cache already exists if it fails).
            await UpdateJsonDataAsync(false);

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

        private Uri GetMoreInfoUri(string name)
        {
            if (GlobalSettings.PackageSource == PackageRegistrySource.GitLab)
            {
                (string baseUrl, _) = ResolveGitLabProjectEndpoint(GlobalSettings.GitLabUrl, GlobalSettings.GitLabProjectId);
                if (!string.IsNullOrWhiteSpace(baseUrl))
                {
                    return new Uri($"{baseUrl}/-/packages?search[]={Uri.EscapeDataString(name)}");
                }
            }

            return new Uri($"https://jfrog.example.com/ui/packages/{name}");
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

            LibrarySearchTextBox.Text = enabled ? "" : "Cliquez sur 'Configurer' pour définir le chemin de Conan -->";
        }

        private void Configuration_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowConfigurationDialog();
            ToggleUIEnableState(IsConanInitialized());
        }

        // ---- Package source fetching (JFrog / GitLab). Mirrors ConanToolWindowControl's logic;
        // kept as a self-contained copy here rather than shared, to avoid touching the still-working
        // ConanToolWindowControl while this new document-window UI is being introduced. ----

        private async Task UpdateJsonDataAsync(bool popUp)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            try
            {
                Dictionary<string, HashSet<string>> libraries = GlobalSettings.PackageSource == PackageRegistrySource.GitLab
                    ? await FetchLibrariesFromGitLabAsync(popUp)
                    : await FetchLibrariesFromJFrogAsync(popUp);

                if (libraries == null)
                {
                    return;
                }

                WriteLibrariesJson(libraries);
                await LoadLibrariesFromJsonAsync();

                if (popUp)
                    MessageBox.Show($"Liste des packages conan mise à jour ({libraries.Count} bibliothèque(s)).", "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private async Task<Dictionary<string, HashSet<string>>> FetchLibrariesFromJFrogAsync(bool popUp)
        {
            string jsonUrl = GlobalSettings.JFrogPackagesUrl;

            if (popUp)
            {
                string input = MessageBoxWithInput.Show("Entrez URL de packages.ref.json :", "Setting package", jsonUrl);
                if (input == null)
                {
                    return null;
                }
                jsonUrl = input;
                GlobalSettings.JFrogPackagesUrl = jsonUrl;
            }

            using (var httpClient = new HttpClient())
            {
                string jsonContent = await httpClient.GetStringAsync(jsonUrl);

                JObject obj = JObject.Parse(jsonContent);
                JArray pkgArray = (JArray)obj["packages"];
                var recipeRefs = pkgArray?.Select(item => item.ToString()) ?? Enumerable.Empty<string>();

                return ParseRecipeReferences(recipeRefs, @"^([\w\d]+)\/([\d.]+)@_\/_$");
            }
        }

        private static (string baseUrl, string projectId) ResolveGitLabProjectEndpoint(string configuredUrl, string configuredProjectId)
        {
            configuredUrl = configuredUrl?.Trim() ?? string.Empty;

            Match match = Regex.Match(configuredUrl, @"^(https?://[^/]+)/api/v4/projects/([^/?]+)/packages", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return (match.Groups[1].Value, match.Groups[2].Value);
            }

            return (configuredUrl.TrimEnd('/'), configuredProjectId?.Trim() ?? string.Empty);
        }

        private async Task<Dictionary<string, HashSet<string>>> FetchLibrariesFromGitLabAsync(bool popUp)
        {
            (string baseUrl, string projectId) = ResolveGitLabProjectEndpoint(GlobalSettings.GitLabUrl, GlobalSettings.GitLabProjectId);

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(projectId))
            {
                if (popUp)
                {
                    MessageBox.Show(
                        "Veuillez renseigner l'URL GitLab (ex: https://gitlab.com) et l'identifiant du projet (ex: 12345678) dans Tools > Options > Conan C/C++ Package Manager > Package Source - GitLab.",
                        "Conan C/C++ Package Manager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return null;
            }

            using (var httpClient = new HttpClient())
            {
                if (!string.IsNullOrWhiteSpace(GlobalSettings.GitLabPrivateToken))
                {
                    httpClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", GlobalSettings.GitLabPrivateToken);
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
