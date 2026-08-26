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
using Microsoft.VisualStudio.Threading;
using System.Windows.Navigation;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace conan_vs_extension
{

    public static class MessageBoxWithInput
    {
        public static string Show(string message, string title, string textDefault)
        {
            // Création du formulaire
            System.Windows.Forms.Form inputForm = new System.Windows.Forms.Form();
            inputForm.Text = title;

            System.Windows.Forms.Label label = new System.Windows.Forms.Label { Text = message, AutoSize = true, Left = 10, Top = 10 };
            System.Windows.Forms.TextBox textBox = new System.Windows.Forms.TextBox { Left = 10, Top = 40, Width = 260 };
            System.Windows.Forms.Button okButton = new System.Windows.Forms.Button { Text = "OK", Left = 150, Width = 60, Top = 70, DialogResult = System.Windows.Forms.DialogResult.OK };
            System.Windows.Forms.Button cancelButton = new System.Windows.Forms.Button { Text = "Annuler", Left = 220, Width = 60, Top = 70, DialogResult = System.Windows.Forms.DialogResult.Cancel };
            textBox.Text = textDefault;
            inputForm.Controls.Add(label);
            inputForm.Controls.Add(textBox);
            inputForm.Controls.Add(okButton);
            inputForm.Controls.Add(cancelButton);

            inputForm.AcceptButton = okButton;
            inputForm.CancelButton = cancelButton;
            inputForm.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            inputForm.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            inputForm.ClientSize = new System.Drawing.Size(300, 120);

            // Affichage de la boîte
            System.Windows.Forms.DialogResult dialogResult = inputForm.ShowDialog();
            return dialogResult == System.Windows.Forms.DialogResult.OK ? textBox.Text : null;
        }
    }

    public class Library
    {
        //public string Description { get; set; }
        //public List<string> License { get; set; }
        public List<string> Versions { get; set; }
    }

    public class RootObject
    {
        public Dictionary<string, Library> Libraries { get; set; }
    }

    public class Requirements
    {
        public Requirements(string[] requirements)
        {
            this.requirements = requirements;
        }
        public Requirements()
        {
            this.requirements = new string[] { };
        }
        public string[] requirements { get; set; }
    }

    /// <summary>
    /// Interaction logic for ConanToolWindowControl.
    /// </summary>
    public partial class ConanToolWindowControl : UserControl
    {
        private DTE _dte;
        private RootObject _jsonData;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConanToolWindowControl"/> class.
        /// </summary>
        public ConanToolWindowControl()
        {
            this.InitializeComponent();
            LibraryHeader.Visibility = Visibility.Collapsed;

            this.Loaded += ConanToolWindowControl_Loaded;

            ToggleUIEnableState(IsConanInitialized());
            _ = InitializeAsync();
        }

        private void ConanToolWindowControl_Loaded(object sender, RoutedEventArgs e)
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

            UpdateForeground(currentColor);
        }

        public void UpdateForeground(Color color)
        {
            var brush = new SolidColorBrush(color);
            this.Foreground = brush;
            ShowPackagesCheckbox.Foreground = brush;
            LibraryNameLabel.Foreground = brush;
        }

        private async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _dte = (DTE)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            if (_dte == null)
            {
                throw new InvalidOperationException("Cannot access DTE service.");
            }

            await CopyJsonFileFromResourceIfNeededAsync();
            await LoadLibrariesFromJsonAsync();
        }
        private async Task CopyJsonFileFromResourceIfNeededAsync()
        {
            //mise à jour du fichier avant
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

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FilterListView(LibrarySearchTextBox.Text, ShowPackagesCheckbox.IsChecked ?? false);
        }

        private async Task LoadLibrariesFromJsonAsync()
        {
            string userConanFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".conan-vs-extension");
            string jsonFilePath = Path.Combine(userConanFolder, "targets-data.json");

            string json = await Task.Run(() => File.ReadAllText(jsonFilePath));
            _jsonData = JsonConvert.DeserializeObject<RootObject>(json);

            await ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                PackagesListView.Items.Clear();
                foreach (var library in _jsonData.Libraries.Keys)
                {
                    PackagesListView.Items.Add(library);
                }
            });
        }

        private void FilterListView(string searchText, bool onlyInstalled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_jsonData == null || _jsonData.Libraries == null) return;

            PackagesListView.Items.Clear();

            var filteredLibraries = _jsonData.Libraries
                .Where(kv => kv.Key.Contains(searchText))
                .ToList();
            Project startupProject = ProjectConfigurationManager.GetStartupProject(_dte);

            if (onlyInstalled && startupProject != null && startupProject.Object is VCProject vcProject)
            {
                string projectFilePath = startupProject.FullName;
                string projectDirectory = Path.GetDirectoryName(projectFilePath);

                var requirements = ConanFileManager.GetConandataRequirements(projectDirectory);
                foreach (var requirement in requirements)
                {
                    string key = requirement.Split('/')[0];
                    if (filteredLibraries.Any(library => library.Key == key))
                    {
                        PackagesListView.Items.Add(key);
                    }
                }
            }
            else
            {
                foreach (var library in filteredLibraries)
                {
                    PackagesListView.Items.Add(library.Key);
                }
            }

        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (PackagesListView.SelectedItem is string selectedItem)
            {
                UpdateLibraryInfo(selectedItem);
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            System.Diagnostics.Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        public void UpdatePanel(string name, string description, string licenses, List<string> versions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            LibraryNameLabel.Content = name;
            VersionsComboBox.ItemsSource = versions;
            VersionsComboBox.SelectedIndex = 0;

            DescriptionTextBlock.Text = description ?? "No description available.";
            LicenseText.Text = licenses ?? "No description available.";

            MoreInfoHyperlink.NavigateUri = GetMoreInfoUri(name);
            GitHubRecipeLink.NavigateUri = new Uri($"https://example.com/");

            Project startupProject = ProjectConfigurationManager.GetStartupProject(_dte);

            if (startupProject != null && startupProject.Object is VCProject vcProject)
            {
                string projectFilePath = startupProject.FullName;
                string projectDirectory = Path.GetDirectoryName(projectFilePath);

                var requirements = ConanFileManager.GetConandataRequirements(projectDirectory);
                bool isInstalled = requirements.Any(e => e.StartsWith(name + "/"));

                InstallButton.Visibility = isInstalled ? Visibility.Collapsed : Visibility.Visible;
                RemoveButton.Visibility = isInstalled ? Visibility.Visible : Visibility.Collapsed;
                VersionsComboBox.IsEnabled = !isInstalled;
                LibraryHeader.Visibility = Visibility.Visible;
                UnsupportedProjectType.Visibility = Visibility.Collapsed;
            }
            else
            {
                LibraryHeader.Visibility = Visibility.Collapsed;
                UnsupportedProjectType.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Builds the "more info" link for a package depending on the configured registry source.
        /// </summary>
        private Uri GetMoreInfoUri(string name)
        {
            if (GlobalSettings.PackageSource == PackageRegistrySource.GitLab)
            {
                (string baseUrl, _) = ResolveGitLabProjectEndpoint(GlobalSettings.GitLabUrl, GlobalSettings.GitLabProjectId);
                if (!string.IsNullOrWhiteSpace(baseUrl))
                {
                    // GitLab doesn't expose a per-package UI route reachable from a numeric project ID alone,
                    // so link to the project's package registry, filtered by name where possible.
                    return new Uri($"{baseUrl}/-/packages?search[]={Uri.EscapeDataString(name)}");
                }
            }

            return new Uri($"https://jfrog.example.com/ui/packages/{name}");
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedLibrary = LibraryNameLabel.Content.ToString();
            var selectedVersion = VersionsComboBox.SelectedItem.ToString();

            ThreadHelper.ThrowIfNotOnUIThread();

            Project startupProject = ProjectConfigurationManager.GetStartupProject(_dte);

            if (startupProject.Object is VCProject)
            {
                string projectFilePath = startupProject.FullName;
                string projectDirectory = Path.GetDirectoryName(projectFilePath);

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

                    InstallButton.Visibility = Visibility.Collapsed;
                    RemoveButton.Visibility = Visibility.Visible;
                    VersionsComboBox.IsEnabled = false;
                }
                else
                {
                    MessageBox.Show($"Requirement {selectedLibrary}/{selectedVersion} could not be added to conandata.yml because it was modified. Please, update the file manually.",
                                    "Conan C/C++ Package Manager",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                }

                _ = ProjectConfigurationManager.SaveConanPrebuildEventsAllConfigAsync(startupProject);
                FilterListView(LibrarySearchTextBox.Text, ShowPackagesCheckbox.IsChecked ?? false);
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedLibrary = LibraryNameLabel.Content.ToString();
            var selectedVersion = VersionsComboBox.SelectedItem.ToString();

            ThreadHelper.ThrowIfNotOnUIThread();

            Array activeSolutionProjects = _dte.ActiveSolutionProjects as Array;
            Project activeProject = activeSolutionProjects.GetValue(0) as Project;

            string projectFilePath = activeProject.FullName;
            string projectDirectory = Path.GetDirectoryName(projectFilePath);

            string conandataPath = Path.Combine(projectDirectory, "conandata.yml");

            if (ConanFileManager.IsFileCommentGuarded(conandataPath))
            {
                ConanFileManager.RemoveRequirement(projectDirectory, selectedLibrary + "/" + selectedVersion);
                MessageBox.Show($"Removing {selectedLibrary} version {selectedVersion}", "Conan C/C++ Package Manager");

                InstallButton.Visibility = Visibility.Visible;
                RemoveButton.Visibility = Visibility.Collapsed;
                VersionsComboBox.IsEnabled = true;
            }
            else
            {
                MessageBox.Show($"Requirement {selectedLibrary}/{selectedVersion} could not be removed from conandata.yml because it was modified. Please, update the file manually.",
                                "Conan C/C++ Package Manager",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
            }

            FilterListView(LibrarySearchTextBox.Text, ShowPackagesCheckbox.IsChecked ?? false);
        }


        private void UpdateLibraryInfo(string name)
        {
            if (_jsonData != null && _jsonData.Libraries.ContainsKey(name))
            {
                var library = _jsonData.Libraries[name];
                var versions = library.Versions;
                //var description = library.Description ?? "No description available.";
                //var licenses = library.License != null ? string.Join(", ", library.License) : "No license information.";

                //UpdatePanel(name, description, licenses, versions);
                UpdatePanel(name, "", "", versions);
            }
        }

        /// <summary>
        /// Handles click on the button by displaying a message box.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event args.</param>

        private void ShowConfigurationDialog()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte.ExecuteCommand("Tools.Options", GuidList.strConanOptionsPage);
        }

        private bool IsConanInitialized()
        {
            return !string.IsNullOrEmpty(GlobalSettings.ConanExecutablePath);
        }

        private void ToggleUIEnableState(bool enabled)
        {
            LibrarySearchTextBox.IsEnabled = enabled;

            ShowPackagesCheckbox.IsEnabled = enabled;
            UpdateButton.IsEnabled = enabled;

            PackagesListView.IsEnabled = enabled;
            LibraryHeader.IsEnabled = enabled;

            if (!enabled)
            {
                LibrarySearchTextBox.Text = "Click 'configure' to set the Conan path -->";
            }
            else
            {
                LibrarySearchTextBox.Text = "";
            }
        }


        private void Configuration_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowConfigurationDialog();
            ToggleUIEnableState(IsConanInitialized());
        }

        private void ShowPackagesCheckbox_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FilterListView(LibrarySearchTextBox.Text, ShowPackagesCheckbox.IsChecked ?? false);
        }

        private async Task UpdateJsonDataAsync(bool popUp)
        {
            try
            {
                Dictionary<string, HashSet<string>> libraries = GlobalSettings.PackageSource == PackageRegistrySource.GitLab
                    ? await FetchLibrariesFromGitLabAsync(popUp)
                    : await FetchLibrariesFromJFrogAsync(popUp);

                // null means the fetch was cancelled or could not run (already reported to the user if popUp).
                if (libraries == null)
                {
                    return;
                }

                WriteLibrariesJson(libraries);

                // Reload the freshly written data straight into the tool window, no VS restart needed.
                await LoadLibrariesFromJsonAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                FilterListView(LibrarySearchTextBox.Text, ShowPackagesCheckbox.IsChecked ?? false);

                if (popUp)
                    MessageBox.Show($"Liste des packages conan mise à jour ({libraries.Count} bibliothèque(s)).", "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (popUp)
                    MessageBox.Show($"Erreur mise à jour : {ex.Message}", "Error - Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Fetches the package list from a JFrog Artifactory "packages.ref.json" index.
        /// Recipe references are expected in the form "name/version@_/_".
        /// </summary>
        private async Task<Dictionary<string, HashSet<string>>> FetchLibrariesFromJFrogAsync(bool popUp)
        {
            string jsonUrl = GlobalSettings.JFrogPackagesUrl;

            //Pour un update on peut changer l'URL
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

                // Expression régulière pour extraire le nom de la bibliothèque et la version
                return ParseRecipeReferences(recipeRefs, @"^([\w\d]+)\/([\d.]+)@_\/_$");
            }
        }

        /// <summary>
        /// Works out the GitLab base URL and project ID to use, tolerating either:
        /// - a plain base URL (e.g. "https://gitlab.com") plus a separate Project ID/Path setting, or
        /// - a full API URL pasted directly into the "GitLab URL" field
        ///   (e.g. "https://gitlab.com/api/v4/projects/12345678/packages?package_type=conan"),
        ///   in which case the project ID is extracted automatically and the separate field is ignored.
        /// </summary>
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

        /// <summary>
        /// Fetches the package list from a GitLab Package Registry (Conan) project, using the
        /// generic packages endpoint: GET /api/v4/projects/:id/packages?package_type=conan
        /// which returns a (paginated) JSON array of objects such as:
        /// [{"name": "hello/0.1@mycompany/test", "conan_package_name": "hello", "version": "0.1", ...}, ...]
        /// </summary>
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

                // The endpoint is paginated (100 items max per page), so keep requesting
                // pages until a short page tells us we've reached the end.
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

                // GitLab recipe refs look like "name/version@user/channel" or just "name/version".
                // Unlike JFrog's "@_/_" convention, GitLab packages can carry a real, meaningful
                // user/channel (e.g. "@mycompany/stable" vs "@mycompany/beta") that is NOT
                // interchangeable, and different channels can even share the same version number.
                // So the "@user/channel" suffix must be kept as part of the selectable version,
                // otherwise Conan can no longer resolve the package ("hello/1.0" != "hello/1.0@mycompany/stable").
                return ParseRecipeReferences(recipeRefs, @"^([\w\-.]+)\/([\w\d.\-]+(?:@[\w\-.]+\/[\w\-.]+)?)$");
            }
        }

        /// <summary>
        /// Parses a list of Conan recipe references (e.g. "zlib/1.3.1@_/_") into a
        /// name -> set of versions dictionary, using the supplied regex to split name and version.
        /// The second capture group is used verbatim as the "version" (it may include a
        /// "@user/channel" suffix when that suffix must be preserved to keep the reference resolvable).
        /// </summary>
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

        /// <summary>
        /// Writes the fetched libraries to the local targets-data.json cache used by the tool window.
        /// </summary>
        private void WriteLibrariesJson(Dictionary<string, HashSet<string>> libraries)
        {
            string userConanFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".conan-vs-extension");
            string jsonFilePath = Path.Combine(userConanFolder, "targets-data.json");

            if (!Directory.Exists(userConanFolder))
            {
                Directory.CreateDirectory(userConanFolder);
            }

            // Création de la structure JSON finale
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

            // Sérialisation avec indentation pour une belle sortie
            string formattedJson = JsonConvert.SerializeObject(outputJson, Formatting.Indented);
            File.WriteAllText(jsonFilePath, formattedJson);
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            _ = UpdateJsonDataAsync(true);
        }
    }
}
