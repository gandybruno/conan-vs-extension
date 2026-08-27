using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /**
     * NuGet-style Conan Package Manager UI: "Parcourir" / "Installé" tabs, a search box, a
     * package list, and a details pane with a version picker and Install/Uninstall actions.
     * This control is opened as a document window (see ConanPackageManagerWindowPane
     * and ConanToolWindowCommand) scoped to one specific EnvDTE Project, passed in at
     * construction time - matching how NuGet's own "Manage NuGet Packages..." works per-project.
     * It is the sole, actively maintained package manager UI (a now-removed docked tool window
     * used to duplicate this functionality without most of its fixes).
     *
     * Note: RootObject/Library/Requirements and MessageBoxWithInput are intentionally NOT
     * redeclared here - they already exist (namespace-level) in ConanSharedModels.cs,
     * compiled into the same assembly, and are reused as-is.
     */
    public partial class ConanPackageManagerControl : UserControl
    {
        /**
         * Which of the two NuGet-style tabs is active.
         */
        private enum PackageTab
        {
            /**
             * Every package the current source offers.
             */
            Browse,
            /**
             * Only the packages already required by this project (conandata.yml).
             */
            Installed
        }

        /**
         * One entry of the "Source de package" dropdown, NuGet-style: a Conan remote already
         * configured on this machine (via `conan remote add`), browsed via the standard Conan v2
         * protocol. No manual URL/token configuration in the plugin itself - everything comes
         * from Conan's own remote list.
         */
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

        // Whether a background fetch of the package source (remote list / local cache fallback)
        // is currently running. Deliberately NOT the same thing as "show LoadingOverlay": the
        // "Installé" tab only ever reads conandata.yml + the already-on-disk targets-data.json, it
        // never needs a network round-trip, so it must never be stuck behind a spinner just
        // because a slow/hung fetch for the "Parcourir" tab happens to still be in flight.
        private bool _isFetchingPackageSource;

        // Incremented every time a new metadata fetch starts, so a slow fetch for a package the
        // user has since clicked away from can detect it's stale and avoid overwriting the panel
        // with outdated info (NuGet has the same "fetching details" race to guard against).
        private int _metadataRequestId;

        // Most-recent-first history shown in the search box's dropdown - like NuGet's own search
        // field. Kept in memory only (per document window), most recent at index 0, capped and
        // de-duplicated by CommitSearchToHistory.
        private readonly ObservableCollection<string> _recentSearches = new ObservableCollection<string>();
        private const int MaxRecentSearches = 10;

        public ConanPackageManagerControl(Project project)
        {
            this.InitializeComponent();
            _project = project;

            this.Loaded += ConanPackageManagerControl_Loaded;

            HeaderTitleTextBlock.Text = "Gestionnaire de packages Conan : " + (project?.Name ?? string.Empty);

            LibrarySearchComboBox.ItemsSource = _recentSearches;
            // ComboBox has no public "TextChanged" event of its own (that belongs to the internal
            // PART_EditableTextBox) - but since it's a routed event, an ancestor can still listen
            // for it via AddHandler even without a dedicated CLR event to wire up in XAML.
            LibrarySearchComboBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(SearchTextBox_TextChanged));

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

        /**
         * The loading overlay should only ever cover the "Parcourir" tab while a remote/cache
         * fetch is genuinely in flight - never the "Installé" tab, which only reads local files
         * (conandata.yml + the on-disk targets-data.json) and has no reason to wait on a network
         * call that might be slow or hung.
         */
        private void UpdateLoadingOverlayVisibility()
        {
            LoadingOverlay.Visibility = (_isFetchingPackageSource && _currentTab == PackageTab.Browse)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Show the loading overlay right away, before the (possibly stale) cached package
            // list from a previous session gets a chance to render - otherwise that old list
            // flashes on screen for an instant before the overlay appears, which briefly looks
            // like a fresh answer from the server. LoadPackageSourcesAsync/UpdateJsonDataAsync
            // (or the empty-sources fallback below) are responsible for hiding it again.
            _isFetchingPackageSource = true;
            UpdateLoadingOverlayVisibility();

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
            // the first real fetch for whichever source ends up selected (and hides the loading
            // overlay once that fetch completes, one way or another).
            PackageSourceComboBox.SelectedItem = toSelect ?? _sources.FirstOrDefault();

            if (_sources.Count == 0)
            {
                // Nothing to select -> SelectionChanged never fires -> nothing would ever hide
                // the overlay InitializeAsync just showed.
                _isFetchingPackageSource = false;
                UpdateLoadingOverlayVisibility();
            }
        }

        /**
         * Persists the newly-selected package source and re-fetches its package list (without the "found nothing"/error popups Update_Click shows, since this can also fire from code during initial load).
         */
        private void PackageSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (PackageSourceComboBox.SelectedItem is PackageSourceEntry entry)
            {
                GlobalSettings.SelectedPackageSourceName = entry.DisplayName;
                _ = UpdateJsonDataAsync(false);
            }
        }

        /**
         * Seeds `%UserProfile%\.conan-vs-extension\targets-data.json` from the extension's bundled sample data, the very first time the package manager ever runs on this machine (before any real fetch has written a copy).
         */
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

        /**
         * (Re)loads `targets-data.json` from disk into _jsonData and re-applies the current search filter - called after every fetch/cache-fallback that rewrites the file.
         */
        private async Task LoadLibrariesFromJsonAsync()
        {
            string userConanFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".conan-vs-extension");
            string jsonFilePath = Path.Combine(userConanFolder, "targets-data.json");

            string json = await Task.Run(() => File.ReadAllText(jsonFilePath));
            _jsonData = JsonConvert.DeserializeObject<RootObject>(json);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            FilterListView(LibrarySearchComboBox.Text);
        }

        /**
         * Live-filters the package list as the user types in the search combobox (see FilterListView) - this does NOT touch the "recent searches" history, only CommitSearchToHistory (Enter/magnifying glass) does.
         */
        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FilterListView(LibrarySearchComboBox.Text);
        }

        /**
         * Enter in the search box - like NuGet's Ctrl+L search field - commits the current text to
         * the "recent searches" dropdown (the list itself already filters live via TextChanged).
         */
        private void LibrarySearchComboBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                CommitSearchToHistory(LibrarySearchComboBox.Text);
            }
        }

        /**
         * The magnifying glass button - "launches" the search, same effect as pressing Enter.
         */
        private void SearchIconButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CommitSearchToHistory(LibrarySearchComboBox.Text);
        }

        /**
         * Adds a search term to the "recent searches" dropdown, most-recent-first, de-duplicated
         * and capped at MaxRecentSearches entries - mirroring NuGet's own search
         * history dropdown.
         */
        private void CommitSearchToHistory(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            _recentSearches.Remove(text);
            _recentSearches.Insert(0, text);

            while (_recentSearches.Count > MaxRecentSearches)
            {
                _recentSearches.RemoveAt(_recentSearches.Count - 1);
            }
        }

        /**
         * Switches to the "Parcourir" (Browse) tab: every package the current source offers, matching the search filter.
         */
        private void BrowseTabButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _currentTab = PackageTab.Browse;
            UpdateTabVisual();
            UpdateLoadingOverlayVisibility(); // a fetch may still be in flight from when we left this tab
            FilterListView(LibrarySearchComboBox.Text);
        }

        /**
         * Switches to the "Installé" (Installed) tab: only the packages already required by this project, matching the search filter.
         */
        private void InstalledTabButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _currentTab = PackageTab.Installed;
            UpdateTabVisual();
            // "Installé" only reads conandata.yml + the local targets-data.json - it never needs
            // to wait on a "Parcourir" remote fetch, however slow or stuck that might be.
            UpdateLoadingOverlayVisibility();
            FilterListView(LibrarySearchComboBox.Text);
        }

        /**
         * Updates the tab buttons' opacity and underline to reflect _currentTab.
         */
        private void UpdateTabVisual()
        {
            bool browseActive = _currentTab == PackageTab.Browse;
            BrowseTabUnderline.Visibility = browseActive ? Visibility.Visible : Visibility.Hidden;
            InstalledTabUnderline.Visibility = browseActive ? Visibility.Hidden : Visibility.Visible;
            BrowseTabButton.Opacity = browseActive ? 1.0 : 0.7;
            InstalledTabButton.Opacity = browseActive ? 0.7 : 1.0;
        }

        /**
         * Whether the document window's project is a Visual C++ (VCProject) one - this extension only knows how to wire up MSBuild/VC projects.
         */
        private bool IsSupportedProject()
        {
            return _project != null && _project.Object is VCProject;
        }

        /**
         * Directory containing the current project's project file - where conanfile.py/conandata.yml live.
         */
        private string GetProjectDirectory()
        {
            return Path.GetDirectoryName(_project.FullName);
        }

        /**
         * Repopulates `PackagesListBox` for the current tab (Browse: every matching package from _jsonData; Installed: only this project's requirements) filtered by searchText.
         */
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

        /**
         * Shows the details panel for the newly-selected package, or the "select a package" placeholder if the selection was cleared.
         */
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

        /**
         * Looks up name's known versions in _jsonData and renders its details panel via UpdatePanel.
         */
        private void UpdateLibraryInfo(string name)
        {
            if (_jsonData != null && _jsonData.Libraries.ContainsKey(name))
            {
                var library = _jsonData.Libraries[name];
                UpdatePanel(name, library.Versions);
            }
        }

        /**
         * Renders the details panel for one package: name, version picker, Install/Uninstall button, and installed-status text - then kicks off an async recipe metadata fetch (RefreshPackageMetadataAsync) for the initially-selected version.
         *
         * @param name The package (library) name.
         * @param versions Every version known for it (from the current package source).
         */
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

        /**
         * Re-fetches recipe metadata (description, license, ...) for the newly-picked version - see RefreshPackageMetadataAsync.
         */
        private void VersionsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (LibraryNameLabel.Content is string name && VersionsComboBox.SelectedItem is string version)
            {
                _ = RefreshPackageMetadataAsync(name, version);
            }
        }

        /**
         * Fetches recipe metadata (description, license, homepage, author, topics) straight from
         * the recipe's own conanfile.py, on-demand for the selected package/version - mirroring
         * how NuGet's "Manage Packages" pane only downloads a package's nuspec once it's selected,
         * rather than for the whole list up front (which would be far too slow).
         */
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

        /**
         * Builds the "more information about this package" link: the source's own package-registry search page when it's GitLab-shaped, otherwise the remote's base URL as a best-effort fallback.
         */
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

        /**
         * Adds the selected package/version to `conandata.yml` (generating conanfile.py/
         * conandata.yml first if this is the project's first requirement) and runs `conan
         * install` for it right away, showing the full-panel `InstallOperationOverlay` for
         * the duration - registered with ConanInstallTracker so a Build started
         * from Visual Studio's own menu while this is running waits for it instead of racing it.
         */
        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

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

            if (!ConanFileManager.IsFileCommentGuarded(conandataPath))
            {
                MessageBox.Show($"Requirement {selectedLibrary}/{selectedVersion} could not be added to conandata.yml because it was modified. Please, update the file manually.",
                                "Conan C/C++ Package Manager",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            ConanFileManager.WriteNewRequirement(projectDirectory, selectedLibrary + "/" + selectedVersion);

            // Run `conan install` right now instead of waiting for the pre-build event to notice
            // the change during an actual Build - that's what used to force a "build once to run
            // conan install, then build again for real" cycle. Doing it here means the very next
            // Build already has everything it needs.
            InstallButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
            InstallOperationStatusText.Text = $"Installation de {selectedLibrary}/{selectedVersion} en cours...";
            InstallOperationOverlay.Visibility = Visibility.Visible;
            try
            {
                await ProjectConfigurationManager.SaveConanPrebuildEventsAllConfigAsync(_project);

                VCConfiguration activeConfig = ProjectConfigurationManager.GetActiveVCConfiguration(_project);

                // Registered with the SAME tracker BuildEventsHandler uses for its own proactive
                // installs, so a Build started from Visual Studio's menu while this is still
                // running (however unlikely, since the button is disabled) - or more realistically,
                // one that starts right as this finishes - correctly waits for it too.
                bool installed = false;
                await ConanInstallTracker.Track(_project.FullName, async () =>
                {
                    installed = await ProjectConfigurationManager.EnsureConanPackagesInstalledAsync(_project, activeConfig, showErrors: true);
                }).JoinAsync();

                if (installed)
                {
                    MessageBox.Show($"{selectedLibrary}/{selectedVersion} installé.", "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"{selectedLibrary}/{selectedVersion} a été ajouté à conandata.yml, mais 'conan install' a échoué :\n{ProjectConfigurationManager.LastConanInstallError}\n\nLe prochain Build réessaiera automatiquement.",
                        "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                // This method is an async void event handler - an exception left to propagate out
                // of it (e.g. an unexpected VCProject COM failure) would not be catchable by any
                // caller and can destabilize/crash Visual Studio instead of just this operation.
                ConanOutputPane.Log($"Installation de {selectedLibrary}/{selectedVersion} : exception inattendue : {ex}");
                MessageBox.Show(
                    $"Une erreur inattendue est survenue pendant l'installation de {selectedLibrary}/{selectedVersion} :\n{ex.Message}",
                    "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                InstallOperationOverlay.Visibility = Visibility.Collapsed;
                InstallButton.IsEnabled = true;
                RemoveButton.IsEnabled = true;
            }

            FilterListView(LibrarySearchComboBox.Text);
            UpdateLibraryInfo(selectedLibrary);
        }

        /**
         * Removes the selected package's actual installed requirement (looked up from
         * `conandata.yml` - never from `VersionsComboBox`, which lists remote-available
         * versions rather than what's installed) and re-runs `conan install` so the project
         * reflects the removal immediately, showing the full-panel `InstallOperationOverlay`
         * for the duration and registering with ConanInstallTracker just like
         * InstallButton_Click.
         */
        private async void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (LibraryNameLabel.Content == null) return;

            var selectedLibrary = LibraryNameLabel.Content.ToString();

            string projectDirectory = GetProjectDirectory();

            // Important: don't use VersionsComboBox.SelectedItem here. That combo lists versions
            // available on the remote (defaulting to index 0, whatever that happens to be) and is
            // simply disabled while a package is installed - it does NOT track which version is
            // actually installed. Using it as the version to remove meant we were building a
            // "name/wrongVersion" string that never matched the real entry in conandata.yml, so
            // RemoveRequirement's exact-match guard silently did nothing (no error, requirement
            // stayed in the file). Instead, look up the actual installed requirement directly.
            var requirements = ConanFileManager.GetConandataRequirements(projectDirectory);
            string installedRequirement = requirements.FirstOrDefault(r => r.StartsWith(selectedLibrary + "/"));
            if (installedRequirement == null)
            {
                return; // Nothing installed for this library - nothing to remove.
            }

            string conandataPath = Path.Combine(projectDirectory, "conandata.yml");

            if (!ConanFileManager.IsFileCommentGuarded(conandataPath))
            {
                MessageBox.Show($"Requirement {installedRequirement} could not be removed from conandata.yml because it was modified. Please, update the file manually.",
                                "Conan C/C++ Package Manager",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            ConanFileManager.RemoveRequirement(projectDirectory, installedRequirement);

            InstallButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
            InstallOperationStatusText.Text = $"Suppression de {installedRequirement} en cours...";
            InstallOperationOverlay.Visibility = Visibility.Visible;
            try
            {
                VCConfiguration activeConfig = ProjectConfigurationManager.GetActiveVCConfiguration(_project);

                bool installed = false;
                await ConanInstallTracker.Track(_project.FullName, async () =>
                {
                    installed = await ProjectConfigurationManager.EnsureConanPackagesInstalledAsync(_project, activeConfig, showErrors: true);
                }).JoinAsync();

                if (!installed)
                {
                    MessageBox.Show(
                        $"{installedRequirement} a été retiré de conandata.yml, mais 'conan install' a échoué :\n{ProjectConfigurationManager.LastConanInstallError}\n\nLe prochain Build réessaiera automatiquement.",
                        "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                // This method is an async void event handler - an exception left to propagate out
                // of it (e.g. an unexpected VCProject COM failure) would not be catchable by any
                // caller and can destabilize/crash Visual Studio instead of just this operation.
                ConanOutputPane.Log($"Suppression de {installedRequirement} : exception inattendue : {ex}");
                MessageBox.Show(
                    $"Une erreur inattendue est survenue pendant la suppression de {installedRequirement} :\n{ex.Message}",
                    "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                InstallOperationOverlay.Visibility = Visibility.Collapsed;
                InstallButton.IsEnabled = true;
                RemoveButton.IsEnabled = true;
            }

            FilterListView(LibrarySearchComboBox.Text);

            // In the "Installé" tab, removing a package makes it disappear from the list entirely
            // (it only ever lists installed requirements) - so there is no longer anything to show
            // details for. Calling UpdateLibraryInfo(selectedLibrary) unconditionally here used to
            // re-render the just-removed package's details anyway (it's still a known library in
            // _jsonData, just no longer installed), which is what left stale info on screen. Only
            // refresh the details panel if the library is still actually present in the (now
            // refreshed) list - e.g. still shown as "not installed" in the "Parcourir" tab.
            if (PackagesListBox.Items.Contains(selectedLibrary))
            {
                PackagesListBox.SelectedItem = selectedLibrary;
                UpdateLibraryInfo(selectedLibrary);
            }
            else
            {
                PackagesListBox.SelectedItem = null;
                DetailsPanel.Visibility = Visibility.Collapsed;
                NoSelectionTextBlock.Visibility = Visibility.Visible;
            }
        }

        /**
         * Opens Tools > Options directly on the "Conan Package Manager > General" page (where the Conan executable path is configured).
         */
        private void ShowConfigurationDialog()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DTE dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
            dte?.ExecuteCommand("Tools.Options", GuidList.strConanOptionsPage);
        }

        /**
         * Whether a Conan executable path has been configured yet - most of the UI stays disabled until it has.
         */
        private bool IsConanInitialized()
        {
            return !string.IsNullOrEmpty(GlobalSettings.ConanExecutablePath);
        }

        /**
         * Enables/disables every interactive control until Conan is configured, showing an instructional message in the search box while disabled.
         */
        private void ToggleUIEnableState(bool enabled)
        {
            LibrarySearchComboBox.IsEnabled = enabled;
            UpdateButton.IsEnabled = enabled;
            PackagesListBox.IsEnabled = enabled;
            BrowseTabButton.IsEnabled = enabled;
            InstalledTabButton.IsEnabled = enabled;
            PackageSourceComboBox.IsEnabled = enabled;

            LibrarySearchComboBox.Text = enabled ? "" : "Cliquez sur 'Configurer' pour définir le chemin de Conan -->";
        }

        /**
         * The gear/"Configurer" button: opens the Options page, then re-evaluates whether the UI should now be enabled.
         */
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

        /**
         * Refreshes `targets-data.json` from the currently-selected package source. On
         * success, writes the fetched list and clears the offline banner; if the source can't be
         * reached at all, falls back to whatever Conan's local cache genuinely has available
         * (ConanRemoteHelper.ListLocalCachePackagesAsync) and shows the offline
         * banner, rather than silently leaving a stale on-disk list on screen.
         *
         * @param popUp Whether to show result/error message boxes - true for an explicit user-triggered refresh (Update_Click), false for the automatic fetch on source selection/startup.
         */
        private async Task UpdateJsonDataAsync(bool popUp)
        {
            if (!(PackageSourceComboBox.SelectedItem is PackageSourceEntry source))
            {
                _isFetchingPackageSource = false;
                UpdateLoadingOverlayVisibility();

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

            _isFetchingPackageSource = true;
            UpdateLoadingOverlayVisibility();
            try
            {
                Dictionary<string, HashSet<string>> libraries = await FetchLibrariesFromConanRemoteAsync(source, popUp);

                if (libraries != null)
                {
                    // The remote answered - even if it has 0 matching recipes, that's a real
                    // (reachable) result, not an offline fallback.
                    OfflineBannerBorder.Visibility = Visibility.Collapsed;
                    WriteLibrariesJson(libraries);
                    await LoadLibrariesFromJsonAsync();

                    if (popUp)
                        MessageBox.Show($"Liste des packages conan mise à jour ({libraries.Count} bibliothèque(s)) depuis '{source.DisplayName}'.", "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Remote unreachable (network down, VPN off, server down...): don't leave the UI
                // silently showing whatever stale targets-data.json happened to be on disk from a
                // past session - that's exactly the "looks like server data but we're offline" bug.
                // Fall back to what Conan's own local cache genuinely has available right now.
                ConanOutputPane.Log($"Remote '{source.DisplayName}' injoignable, repli sur le cache local.");
                Dictionary<string, HashSet<string>> cacheLibraries = await ConanRemoteHelper.ListLocalCachePackagesAsync();

                OfflineBannerBorder.Visibility = Visibility.Visible;
                WriteLibrariesJson(cacheLibraries ?? new Dictionary<string, HashSet<string>>());
                await LoadLibrariesFromJsonAsync();

                if (popUp)
                {
                    int cacheCount = cacheLibraries?.Count ?? 0;
                    MessageBox.Show(
                        $"Le remote '{source.DisplayName}' est injoignable (hors ligne ?).\n" +
                        $"{cacheCount} bibliothèque(s) affichée(s) depuis le cache Conan local (paquets déjà téléchargés).",
                        "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                if (popUp)
                    MessageBox.Show($"Erreur mise à jour : {ex.Message}", "Error - Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isFetchingPackageSource = false;
                UpdateLoadingOverlayVisibility();
            }
        }

        /**
         * Browses a Conan remote already configured on this machine. Tries the standard Conan v2
         * protocol first (works out of the box for Artifactory, conancenter, a generic
         * conan_server...); if that comes back empty and the remote's URL matches GitLab's Conan
         * v2 registry pattern, falls back to GitLab's own Packages REST API, since GitLab's
         * search endpoint doesn't support a bare "*" wildcard (confirmed: q=* returns nothing,
         * q=he* works fine) even though its own documentation says it should.
         *
         * Returns null specifically when the remote could not be reached at all (as opposed to
         * being reached but genuinely empty) so the caller can fall back to the local cache
         * instead of silently keeping stale data on screen.
         */
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
                Dictionary<string, HashSet<string>> gitLabLibraries = await FetchLibrariesFromGitLabPackagesApiAsync(
                    gitLabMatch.Groups["base"].Value,
                    gitLabMatch.Groups["projectId"].Value,
                    source.RemoteName,
                    popUp);

                // If the standard Conan protocol call above genuinely failed to reach the remote
                // (null) AND the GitLab REST fallback also couldn't reach it (null), report
                // "unreachable" upward. But if either one got a real (even empty) answer, that's
                // reachable - propagate the real result.
                if (gitLabLibraries != null)
                {
                    return gitLabLibraries;
                }

                return libraries; // null if the standard protocol also failed; empty dict if it succeeded with 0 matches
            }

            // No GitLab fallback applies here: `libraries` is either a genuine empty result (the
            // standard Conan protocol reached the remote fine, 0 matches) or null (unreachable).
            if (libraries != null && popUp)
            {
                MessageBox.Show($"Aucun package trouvé sur le remote '{source.RemoteName}'.", "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return libraries;
        }

        /**
         * GitLab's Packages REST API, keyed to one specific Conan remote's name so each
         * GitLab-backed remote can have its own access token (see
         * GlobalSettings.GetGitLabTokenForRemote/SetGitLabTokenForRemote). Returns null (rather
         * than letting the exception propagate) on any network failure, so the caller can tell
         * "remote unreachable" apart from "reachable, 0 packages" and fall back to the local
         * cache instead of crashing the refresh or showing stale data.
         */
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

            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(15);

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
            catch (Exception ex)
            {
                // Network unreachable, DNS failure, timeout, etc. - this is exactly the "no
                // internet" case the user reported; treat it the same as the standard Conan
                // protocol failing (null), rather than letting the whole refresh throw.
                System.Diagnostics.Debug.WriteLine($"[ConanPackageManagerControl] GitLab Packages API unreachable for '{remoteName}': {ex}");
                ConanOutputPane.Log($"GitLab Packages API ({remoteName}) injoignable : {ex.Message}");
                return null;
            }
        }

        /**
         * Parses recipe references (e.g. "zlib/1.3.1@_/_" or "hello/1.0@mycompany/stable") into a name -> set-of-versions map, using pattern to split name from version(+user/channel).
         */
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

        /**
         * Serializes a name -> versions map to `%UserProfile%\.conan-vs-extension\targets-data.json`, the on-disk cache LoadLibrariesFromJsonAsync reads back.
         */
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

        /**
         * The refresh (🔄) button: re-fetches the package list from the current source, with result/error message boxes.
         */
        private void Update_Click(object sender, RoutedEventArgs e)
        {
            _ = UpdateJsonDataAsync(true);
        }

    }
}
