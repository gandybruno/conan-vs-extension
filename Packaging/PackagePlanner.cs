using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace conan_vs_extension.Packaging
{
    /**
     * Decides, from the settings entered by the user and what the project builds, what "Create
     * package" has to do - or why it cannot. Works on plain data only (no Visual Studio, no
     * Conan), which keeps the rules in one place.
     */
    public static class PackagePlanner
    {
        // Conan 2 package names: lowercase, 2 to 101 characters.
        private static readonly Regex _validName = new Regex(@"^[a-z0-9_][a-z0-9_+.\-]{1,100}$");
        private static readonly Regex _validVersion = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_+.\-]*$");

        /**
         * A package name Conan accepts, derived from a project name ("My Lib" gives "my_lib").
         */
        public static string SuggestName(string projectName)
        {
            string name = Regex.Replace((projectName ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9_+.\-]", "_");
            name = name.TrimStart('+', '.', '-');
            return name.Length >= 2 ? name : "lib" + name;
        }

        /**
         * Checks what the user entered.
         *
         * @return The problems to fix, one sentence each; empty when everything is valid.
         */
        public static List<string> Validate(PackageSettings settings, string version)
        {
            var problems = new List<string>();

            if (!_validName.IsMatch(settings.Name ?? string.Empty))
            {
                problems.Add(Loc.T("The name must be lowercase, at least 2 characters long, with only letters, digits and _ + . -",
                                   "Le nom doit être en minuscules, d'au moins 2 caractères, avec uniquement des lettres, chiffres et _ + . -"));
            }

            if (!_validVersion.IsMatch(version ?? string.Empty))
            {
                problems.Add(Loc.T("The version is required (letters, digits and _ + . - only), e.g. 1.0.0",
                                   "La version est obligatoire (lettres, chiffres et _ + . - uniquement), par exemple 1.0.0"));
            }

            if (string.IsNullOrWhiteSpace(settings.License))
            {
                problems.Add(Loc.T("The license is required, e.g. MIT or Proprietary.",
                                   "La licence est obligatoire, par exemple MIT ou Proprietary."));
            }

            if (string.IsNullOrWhiteSpace(settings.Headers))
            {
                problems.Add(Loc.T("The public headers folder is required.", "Le dossier des headers publics est obligatoire."));
            }

            if (settings.Configurations == null || settings.Configurations.Count == 0)
            {
                problems.Add(Loc.T("Select at least one configuration to package.", "Sélectionnez au moins une configuration à empaqueter."));
            }

            return problems;
        }

        /**
         * One configuration to build and export, with the Conan settings of its profile.
         */
        public class Step
        {
            public LibraryConfiguration Configuration { get; set; }
            public string BuildType { get; set; }
            public string Arch { get; set; }
        }

        /**
         * Works out the configurations to package and fills the part of the settings that comes
         * from the project (library type and name, output folders, /GL build types).
         *
         * @param settings Updated in place.
         * @param configurations Every configuration of the project (VcProjectInspector).
         * @param projectDirectory Where the project file, `.conan` profiles and headers folder are.
         * @return The steps, or the reason why the package cannot be created.
         */
        public static (List<Step> steps, string error) Plan(PackageSettings settings, IReadOnlyList<LibraryConfiguration> configurations, string projectDirectory)
        {
            var steps = new List<Step>();

            foreach (string name in settings.Configurations)
            {
                LibraryConfiguration configuration = configurations.FirstOrDefault(c => c.Name == name);
                if (configuration == null)
                {
                    return (null, Loc.T($"The project has no '{name}' configuration any more: review the configurations to package.",
                                        $"Le projet n'a plus de configuration '{name}' : revoyez les configurations à empaqueter."));
                }

                if (!configuration.IsLibrary)
                {
                    return (null, Loc.T($"'{name}' does not build a library (static library or DLL).",
                                        $"'{name}' ne produit pas une bibliothèque (bibliothèque statique ou DLL)."));
                }

                Dictionary<string, string> profile = ConanProfileFile.ReadSettings(Path.Combine(projectDirectory, ".conan", configuration.ProfileName));
                if (!profile.TryGetValue("build_type", out string buildType) || !profile.TryGetValue("arch", out string arch))
                {
                    return (null, Loc.T($"No Conan profile could be generated for '{name}' (see the Conan pane of the Output window).",
                                        $"Aucun profil Conan n'a pu être généré pour '{name}' (voir l'onglet Conan de la fenêtre Output)."));
                }

                Step twin = steps.FirstOrDefault(s => s.BuildType == buildType && s.Arch == arch);
                if (twin != null)
                {
                    return (null, Loc.T($"'{twin.Configuration.Name}' and '{name}' are the same binary for Conan ({buildType}, {arch}): package only one of them.",
                                        $"'{twin.Configuration.Name}' et '{name}' sont le même binaire pour Conan ({buildType}, {arch}) : n'en empaquetez qu'une."));
                }

                steps.Add(new Step { Configuration = configuration, BuildType = buildType, Arch = arch });
            }

            if (steps.Select(s => s.Configuration.PackageType).Distinct().Count() > 1 ||
                steps.Select(s => s.Configuration.TargetName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                return (null, Loc.T("The selected configurations do not build the same library (static/DLL, or a different file name): they cannot go in one package.",
                                    "Les configurations sélectionnées ne produisent pas la même bibliothèque (statique/DLL, ou nom de fichier différent) : elles ne peuvent pas aller dans un même paquet."));
            }

            string headers = Path.Combine(projectDirectory, settings.Headers ?? string.Empty);
            if (!Directory.Exists(headers))
            {
                return (null, Loc.T($"The public headers folder '{headers}' does not exist.",
                                    $"Le dossier des headers publics '{headers}' n'existe pas."));
            }

            settings.Type = steps[0].Configuration.PackageType;
            settings.Libs = new List<string> { steps[0].Configuration.TargetName };
            settings.OutputDirs = steps.ToDictionary(
                s => s.BuildType + "_" + s.Arch,
                s => MakeRelative(projectDirectory, s.Configuration.OutputDirectory));

            // A static library compiled with /GL holds compiler-specific intermediate code: only
            // the toolset that built it can link it.
            settings.LtcgBuildTypes = steps
                .Where(s => s.Configuration.WholeProgramOptimization && s.Configuration.PackageType == PackageSettings.StaticLibrary)
                .Select(s => s.BuildType)
                .Distinct()
                .ToList();

            return (steps, null);
        }

        /**
         * path relative to baseDirectory, with forward slashes ("." for the same folder); path
         * itself when it is on another drive.
         */
        public static string MakeRelative(string baseDirectory, string path)
        {
            string from = Path.GetFullPath(baseDirectory).TrimEnd('\\', '/') + "\\";
            string to = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";

            if (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
            {
                return to.TrimEnd('\\').Replace('\\', '/');
            }

            string relative = Uri.UnescapeDataString(new Uri(from).MakeRelativeUri(new Uri(to)).ToString()).TrimEnd('/');
            return relative.Length == 0 ? "." : relative;
        }
    }
}
