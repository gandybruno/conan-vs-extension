using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace conan_vs_extension.Packaging
{
    /**
     * The "Package" tab of the Conan package manager window: the form where a library project
     * is described as a Conan package (name, version, license, public headers, configurations)
     * and from which the package is created. It only reads and shows; the rules are in
     * PackagePlanner and the work in PackageCreator.
     */
    public partial class PackagePropertiesControl : UserControl
    {
        /**
         * One line of the "Configurations to package" list.
         */
        public class ConfigurationItem
        {
            public string Name { get; set; }
            public string Label { get; set; }
            public bool IsLibrary { get; set; }
            public bool IsSelected { get; set; }
            public string Warning { get; set; }
            public Visibility WarningVisibility => string.IsNullOrEmpty(Warning) ? Visibility.Collapsed : Visibility.Visible;
        }

        private readonly Project _project;
        private List<ConfigurationItem> _configurations = new List<ConfigurationItem>();
        private CancellationTokenSource _cancellation;

        public PackagePropertiesControl(Project project)
        {
            InitializeComponent();
            _project = project;
        }

        /**
         * Whether the project builds a library in at least one configuration - otherwise there
         * is nothing to package and the tab is not offered.
         */
        public static bool IsLibraryProject(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return VcProjectInspector.GetConfigurations(project).Any(c => c.IsLibrary);
        }

        private string ProjectDirectory
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return Path.GetDirectoryName(_project.FullName);
            }
        }

        /**
         * Fills the form from the project and its files. Called each time the tab is shown, so
         * it follows what changed meanwhile (a configuration added, a file edited by hand).
         */
        public void Reload()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectDirectory = ProjectDirectory;
            List<LibraryConfiguration> configurations = VcProjectInspector.GetConfigurations(_project);
            PackageSettings saved = ConanDataFile.Load(projectDirectory).Package;
            PackageSettings settings = saved ?? new PackageSettings
            {
                Name = PackagePlanner.SuggestName(_project.Name),
                Headers = Directory.Exists(Path.Combine(projectDirectory, "include")) ? "include" : ".",
                Configurations = DefaultConfigurations(configurations)
            };

            NameTextBox.Text = settings.Name;
            VersionTextBox.Text = VersionFile.Read(projectDirectory) ?? "1.0.0";
            LicenseTextBox.Text = settings.License;
            DescriptionTextBox.Text = settings.Description;
            HeadersTextBox.Text = settings.Headers;
            PdbCheckBox.IsChecked = settings.Pdb;
            ToolsetCompatibilityCheckBox.IsChecked = settings.ToolsetCompatibility;

            _configurations = configurations.Select(c => new ConfigurationItem
            {
                Name = c.Name,
                Label = c.Name + " - " + DescribeType(c),
                IsLibrary = c.IsLibrary,
                IsSelected = c.IsLibrary && settings.Configurations.Contains(c.Name),
                Warning = c.WholeProgramOptimization && c.PackageType == PackageSettings.StaticLibrary
                    ? Loc.T("Compiled with /GL (whole program optimization): this binary only suits projects using the same toolset.",
                            "Compilée avec /GL (optimisation de l'ensemble du programme) : ce binaire ne convient qu'aux projets utilisant le même toolset.")
                    : null
            }).ToList();
            ConfigurationsItemsControl.ItemsSource = _configurations;

            ShowNotice(projectDirectory);
            StatusTextBlock.Text = saved == null
                ? Loc.T("Not saved yet: these are suggested values.", "Pas encore enregistré : ce sont des valeurs proposées.")
                : string.Empty;
        }

        /**
         * Debug and Release (every library configuration) of the platform currently active.
         */
        private List<string> DefaultConfigurations(List<LibraryConfiguration> configurations)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string activePlatform = _project.ConfigurationManager?.ActiveConfiguration?.PlatformName;
            List<string> names = configurations
                .Where(c => c.IsLibrary && c.PlatformName == activePlatform)
                .Select(c => c.Name)
                .ToList();
            return names.Count > 0 ? names : configurations.Where(c => c.IsLibrary).Select(c => c.Name).ToList();
        }

        private static string DescribeType(LibraryConfiguration configuration)
        {
            switch (configuration.PackageType)
            {
                case PackageSettings.StaticLibrary:
                    return Loc.T("static library", "bibliothèque statique");
                case PackageSettings.SharedLibrary:
                    return "DLL";
                default:
                    return Loc.T("not a library", "pas une bibliothèque");
            }
        }

        /**
         * Tells the user when the extension no longer drives one of the two generated files.
         */
        private void ShowNotice(string projectDirectory)
        {
            string notice = null;
            bool canSave = true;

            if (!ManagedFileGuard.CanWrite(ConanDataFile.GetPath(projectDirectory)))
            {
                canSave = false;
                notice = Loc.T("conandata.yml was modified by hand (its two comment lines at the top were removed): the extension no longer writes it, so this form is read-only.",
                               "conandata.yml a été modifié à la main (ses deux lignes de commentaire en tête ont été retirées) : l'extension ne l'écrit plus, ce formulaire est donc en lecture seule.");
            }
            else if (!ManagedFileGuard.CanWrite(Path.Combine(projectDirectory, "conanfile.py")))
            {
                notice = Loc.T("conanfile.py was modified by hand: the extension no longer regenerates it. \"Create package\" uses your recipe, which may ignore the settings below.",
                               "conanfile.py a été modifié à la main : l'extension ne le régénère plus. « Créer le paquet » utilise votre recette, qui peut ignorer les réglages ci-dessous.");
            }

            NoticeTextBlock.Text = notice ?? string.Empty;
            NoticeBorder.Visibility = notice == null ? Visibility.Collapsed : Visibility.Visible;
            SaveButton.IsEnabled = canSave;
            CreateButton.IsEnabled = canSave && GlobalSettings.IsConanAvailable();
        }

        /**
         * The settings as the form shows them, keeping what the form does not show (the part
         * read from the project) from the saved ones.
         */
        private PackageSettings ReadForm()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            PackageSettings settings = ConanDataFile.Load(ProjectDirectory).Package ?? new PackageSettings();
            settings.Name = NameTextBox.Text.Trim();
            settings.License = LicenseTextBox.Text.Trim();
            settings.Description = string.IsNullOrWhiteSpace(DescriptionTextBox.Text) ? null : DescriptionTextBox.Text.Trim();
            settings.Headers = HeadersTextBox.Text.Trim().Replace('\\', '/');
            settings.Pdb = PdbCheckBox.IsChecked == true;
            settings.ToolsetCompatibility = ToolsetCompatibilityCheckBox.IsChecked == true;
            settings.Configurations = _configurations.Where(c => c.IsSelected).Select(c => c.Name).ToList();
            return settings;
        }

        /**
         * Validates the form and writes version.txt, conandata.yml and the recipe.
         *
         * @return False, with the reason shown under the buttons, when nothing was written.
         */
        private bool Save()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            PackageSettings settings = ReadForm();
            string version = VersionTextBox.Text.Trim();

            List<string> problems = PackagePlanner.Validate(settings, version);
            if (problems.Count > 0)
            {
                StatusTextBlock.Text = string.Join("\n", problems);
                return false;
            }

            string projectDirectory = ProjectDirectory;
            VersionFile.Write(projectDirectory, version);
            if (!ConanFileManager.WritePackageSettings(projectDirectory, settings))
            {
                ShowNotice(projectDirectory);
                return false;
            }

            ShowNotice(projectDirectory);
            StatusTextBlock.Text = Loc.T("Saved (conandata.yml, version.txt, conanfile.py).", "Enregistré (conandata.yml, version.txt, conanfile.py).");
            return true;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Save();
        }

        private void BrowseHeadersButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectDirectory = ProjectDirectory;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = Loc.T("Folder of the public headers of the library", "Dossier des headers publics de la bibliothèque");
                dialog.ShowNewFolderButton = false;
                string current = Path.Combine(projectDirectory, HeadersTextBox.Text.Trim());
                dialog.SelectedPath = Directory.Exists(current) ? Path.GetFullPath(current) : projectDirectory;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    HeadersTextBox.Text = PackagePlanner.MakeRelative(projectDirectory, dialog.SelectedPath);
                }
            }
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(CreatePackageAsync);
        }

        private async Task CreatePackageAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!Save())
            {
                return;
            }

            PackageCreationResult result;
            _cancellation = new CancellationTokenSource();
            CancelButton.IsEnabled = true;
            OperationStatusText.Text = Loc.T("Creating the package...", "Création du paquet...");
            OperationOverlay.Visibility = Visibility.Visible;
            try
            {
                var progress = new Progress<string>(message => OperationStatusText.Text = message);
                result = await PackageCreator.CreateAsync(_project, progress, _cancellation.Token);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                OperationOverlay.Visibility = Visibility.Collapsed;
                _cancellation.Dispose();
                _cancellation = null;
            }

            StatusTextBlock.Text = PackageCreationReport.Describe(result);
            PackageCreationReport.Show(result);
        }

        /**
         * Stops the conan command in progress; a Visual Studio build already started finishes first.
         */
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancelButton.IsEnabled = false;
            OperationStatusText.Text = Loc.T("Cancelling...", "Annulation en cours...");
            _cancellation?.Cancel();
            ProjectConfigurationManager.CancelConanInstall();
        }
    }
}
