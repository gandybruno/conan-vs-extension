using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace conan_vs_extension
{
    /**
     * Translates a Visual C++ project's build configurations into Conan "host profile" files
     * (one per configuration, under the project's `.conan\` folder) - the arch/compiler/
     * build_type settings Conan needs to resolve the exact same binaries MSVC is about to build,
     * so `conan install` fetches (or builds) matching prebuilt packages instead of source-only
     * ones. Profiles are only regenerated when their computed content actually changed, and a
     * `.runconan` marker file is dropped alongside so the pre-build script (see
     * ProjectConfigurationManager) knows to re-run `conan install`.
     */
    public class ConanProfilesManager
    {

        public ConanProfilesManager()
        {
        }

        /**
         * Turns a VCConfiguration's name (e.g. "Debug|x64") into a filesystem-safe profile file name (e.g. "Debug_x64").
         */
        public static string getProfileName(VCConfiguration vcConfig)
        {
            return vcConfig.Name.Replace("|", "_");
        }

        /**
         * Maps an MSBuild platform name to the Conan `arch` setting.
         *
         * @throws NotSupportedException The platform isn't one this extension has a known mapping for.
         */
        private string getConanArch(string platform)
        {
            var archMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            archMap["x64"] = "x86_64";
            archMap["Win32"] = "x86";
            archMap["x86"] = "x86";
            archMap["ARM"] = "armv7";
            archMap["ARM64"] = "armv8";
            archMap["ARM64EC"] = "arm64ec";

            if (!archMap.TryGetValue(platform ?? string.Empty, out string arch))
            {
                throw new NotSupportedException(
                    $"Unknown or unsupported platform '{platform}'. " +
                    "The Conan extension does not know which Conan arch this platform maps to.");
            }

            return arch;
        }

        /**
         * Maps the project's PlatformToolset (and, for VS 17.10+, the exact MSBuild version) to the Conan `compiler.version` setting for `compiler=msvc`. See https://docs.conan.io/2/reference/config_files/settings.html.
         *
         * @throws NotSupportedException The toolset isn't one this extension has a known mapping for yet.
         */
        private string getConanCompilerVersion(string platformToolset, string vsVersion, string vcToolsVersion)
        {
            // The MSVC toolset really used by the project ($(VCToolsVersion), e.g. "14.44.35207")
            // decides Conan's compiler.version: 14.0x -> 190, 14.1x -> 191 ... 14.4x -> 194, 14.5x -> 195.
            // This also holds when a newer Visual Studio still builds with an older toolset (v143 in VS 2026).
            if (!string.IsNullOrWhiteSpace(vcToolsVersion))
            {
                string[] parts = vcToolsVersion.Split('.');
                if (parts.Length >= 2 && parts[0] == "14" && int.TryParse(parts[1], out int minor))
                {
                    return (190 + minor / 10).ToString();
                }
            }

            if (Version.TryParse(vsVersion, out Version parsedVersion))
            {
                // https://github.com/conan-io/conan/issues/16239
                if (parsedVersion.Major == 17 && parsedVersion.Minor >= 10)
                {
                    return "194";
                }
            }

            // See https://docs.conan.io/2/reference/config_files/settings.html for the
            // mapping between the MSVC platform toolset and Conan's msvc compiler.version.
            // Note: Visual Studio 18 jumped straight from v143 to v145 (v144 doesn't exist).
            var msvcVersionMap = new Dictionary<string, string>();
            msvcVersionMap["v145"] = "195"; // Visual Studio 18 2026
            msvcVersionMap["v143"] = "193";
            msvcVersionMap["v142"] = "192";
            msvcVersionMap["v141"] = "191";

            if (!msvcVersionMap.TryGetValue(platformToolset, out string compilerVersion))
            {
                throw new NotSupportedException(
                    $"Unknown or unsupported PlatformToolset '{platformToolset}'. " +
                    "The Conan extension does not yet know which Conan compiler.version this toolset maps to.");
            }

            return compilerVersion;
        }

        /**
         * Maps the project's Runtime Library setting (/MT, /MD, ...) to the Conan `compiler.runtime` setting ("static" or "dynamic").
         */
        private string GetRuntimeLibraryType(runtimeLibraryOption runtimeLibraryValue)
        {
            switch (runtimeLibraryValue)
            {
                case runtimeLibraryOption.rtMultiThreaded:
                case runtimeLibraryOption.rtMultiThreadedDebug:
                    return "static";
                case runtimeLibraryOption.rtMultiThreadedDLL:
                case runtimeLibraryOption.rtMultiThreadedDebugDLL:
                    return "dynamic";
                default:
                    return "dynamic";
            }
        }


        /**
         * Whether the project links the debug C runtime (/MTd, /MDd) - what Conan calls `compiler.runtime_type=Debug`.
         */
        private static bool IsDebugRuntime(runtimeLibraryOption runtimeLibraryValue)
        {
            return runtimeLibraryValue == runtimeLibraryOption.rtMultiThreadedDebug ||
                   runtimeLibraryValue == runtimeLibraryOption.rtMultiThreadedDebugDLL;
        }

        /**
         * The Conan `build_type` of a configuration. Its name is used when it is one Conan knows
         * (Debug, Release, RelWithDebInfo, MinSizeRel); a configuration named anything else
         * ("Debug-Static", "ReleaseUnicode"...) is Debug or Release depending on the C runtime it links.
         */
        private static string GetConanBuildType(string configurationName, bool debugRuntime)
        {
            foreach (string buildType in new[] { "Debug", "Release", "RelWithDebInfo", "MinSizeRel" })
            {
                if (string.Equals(configurationName, buildType, StringComparison.OrdinalIgnoreCase))
                {
                    return buildType;
                }
            }

            return debugRuntime ? "Debug" : "Release";
        }

        /**
         * Maps the project's "C++ Language Standard" setting to the Conan `compiler.cppstd` value ("14", "17", "20", "23"). See https://learn.microsoft.com/cpp/build/reference/std-specify-language-standard-version.
         *
         * @return The cppstd value, or null when the setting is unknown/unreadable: the profile then leaves `compiler.cppstd` out and Conan uses the compiler's default.
         */
        private string getConanCppstd(string languageStandard, string compilerVersion)
        {
            // https://learn.microsoft.com/en-us/cpp/build/reference/std-specify-language-standard-version?view=msvc-170

            if (string.IsNullOrWhiteSpace(languageStandard))
            {
                return null;
            }

            string value = languageStandard.ToLowerInvariant();

            if (value.Contains("default"))
            {
                return "14";
            }

            List<string> cppStdValues = new List<string>() { "14", "17", "20", "23" };

            foreach (string cppStdValue in cppStdValues)
            {
                if (value.Contains(cppStdValue))
                {
                    return cppStdValue;
                }
            }

            // /std:c++latest: the newest standard that toolset implements.
            if (value.Contains("latest"))
            {
                switch (compilerVersion)
                {
                    case "190":
                    case "191":
                        return "17";
                    case "192":
                        return "20";
                    default:
                        return "23";
                }
            }

            return null;
        }

        private static string EvaluateOrEmpty(VCConfiguration vcConfig, string expression)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string value = vcConfig.Evaluate(expression)?.ToString() ?? string.Empty;
                return value == expression ? string.Empty : value;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static readonly HashSet<string> _toolsetWarnings = new HashSet<string>();
        private static readonly HashSet<string> _profileErrors = new HashSet<string>();

        /**
         * Tells the user (once per project and toolset) when the project does not use the platform
         * toolset the running Visual Studio provides by default, or when its toolset is not installed:
         * Conan then builds packages for that toolset's compiler.version, not for the IDE's own.
         */
        private static void WarnIfToolsetDiffers(string projectName, string toolset, string vsVersion, string vcToolsVersion, string compilerVersion)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string defaultToolset = null;
            if (Version.TryParse(vsVersion, out Version parsed))
            {
                switch (parsed.Major)
                {
                    case 18: defaultToolset = "v145"; break;
                    case 17: defaultToolset = "v143"; break;
                    case 16: defaultToolset = "v142"; break;
                    case 15: defaultToolset = "v141"; break;
                }
            }

            bool notInstalled = string.IsNullOrWhiteSpace(vcToolsVersion);
            bool differs = defaultToolset != null && !string.Equals(toolset, defaultToolset, StringComparison.OrdinalIgnoreCase);
            if (!notInstalled && !differs)
            {
                return;
            }

            if (!_toolsetWarnings.Add($"{projectName}|{toolset}|{vcToolsVersion}"))
            {
                return;
            }

            string nl = Environment.NewLine + Environment.NewLine;
            string message = notInstalled
                ? Loc.T($"The project \"{projectName}\" uses the {toolset} toolset, which does not seem to be installed with this version of Visual Studio.{nl}" +
                        $"Conan will use compiler.version={compilerVersion} from the toolset mapping, and building packages may fail.{nl}" +
                        "Install this toolset (Visual Studio Installer) or change the project's \"Platform Toolset\".",
                        $"Le projet « {projectName} » utilise le toolset {toolset}, qui ne semble pas installé avec cette version de Visual Studio.{nl}" +
                        $"Conan utilisera compiler.version={compilerVersion} d'après le mappage du toolset, et la compilation des paquets risque d'échouer.{nl}" +
                        "Installez ce toolset (Visual Studio Installer) ou changez le « Jeu d'outils de plateforme » du projet.")
                : Loc.T($"The project \"{projectName}\" uses the {toolset} toolset (MSVC {vcToolsVersion}), while this version of Visual Studio provides {defaultToolset} by default.{nl}" +
                        $"Conan will use compiler.version={compilerVersion} for this project: package binaries must match this compiler.{nl}" +
                        $"To use {defaultToolset}, change the \"Platform Toolset\" in the project properties.",
                        $"Le projet « {projectName} » utilise le toolset {toolset} (MSVC {vcToolsVersion}), alors que cette version de Visual Studio fournit {defaultToolset} par défaut.{nl}" +
                        $"Conan utilisera compiler.version={compilerVersion} pour ce projet : les binaires des paquets doivent correspondre à ce compilateur.{nl}" +
                        $"Pour utiliser {defaultToolset}, changez le « Jeu d'outils de plateforme » dans les propriétés du projet.");

            ConanOutputPane.Log(message.Replace(nl, " "));

            // A toolset that differs from the IDE's default is only informative (the package manager
            // checks each package against the project's profile): log it. A toolset that is not
            // installed will make builds fail, so that one is still shown.
            if (notInstalled)
            {
                MessageBox.Show(message, "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /**
         * Writes one Conan profile file per build configuration of project
         * (under its `.conan\` folder), only if the project already opted into Conan (has a
         * `conandata.yml`). Unwritable/unrecognized configurations (e.g. an unmapped
         * PlatformToolset or platform) are collected and reported once in the Conan Output pane
         * rather than aborting the whole loop, so one bad configuration never blocks profile
         * generation for the others.
         *
         * @param project The EnvDTE project to generate profiles for.
         */
        public void GenerateProfilesForProject(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (project != null && project.Object is VCProject vcProject)
                {
                    string projectDirectory = System.IO.Path.GetDirectoryName(project.FullName);
                    string path = Path.Combine(projectDirectory, "conandata.yml");

                    // only generate the profiles if we had a conandata that means
                    // that at some point the user wanted to use conan

                    bool fileExists = File.Exists(path);

                    if (fileExists)
                    {
                        string conanProjectDirectory = System.IO.Path.Combine(projectDirectory, ".conan");
                        if (!Directory.Exists(conanProjectDirectory))
                        {
                            Directory.CreateDirectory(conanProjectDirectory);
                        }

                        var configErrors = new List<string>();

                        foreach (VCConfiguration vcConfig in (IEnumerable)vcProject.Configurations)
                        {
                            string profileName = getProfileName(vcConfig);

                            try
                            {
                                string profilePath = System.IO.Path.Combine(conanProjectDirectory, profileName);

                                string toolset = vcConfig.Evaluate("$(PlatformToolset)").ToString();
                                string vsVersion = vcConfig.Evaluate("$(MSBuildVersion)").ToString();
                                string vcToolsVersion = EvaluateOrEmpty(vcConfig, "$(VCToolsVersion)");
                                string compilerVersion = getConanCompilerVersion(toolset, vsVersion, vcToolsVersion);
                                WarnIfToolsetDiffers(project.Name, toolset, vsVersion, vcToolsVersion, compilerVersion);
                                string arch = getConanArch(vcConfig.Evaluate("$(PlatformName)").ToString());
                                IVCRulePropertyStorage generalRule = vcConfig.Rules.Item("ConfigurationGeneral") as IVCRulePropertyStorage;
                                string languageStandard = generalRule == null ? null : generalRule.GetEvaluatedPropertyValue("LanguageStandard");
                                string cppStd = getConanCppstd(languageStandard, compilerVersion);

                                var tools = (IVCCollection) vcConfig.Tools;
                                var vcCTool = (VCCLCompilerTool) tools.Item("VCCLCompilerTool");

                                string runtime = GetRuntimeLibraryType(vcCTool.RuntimeLibrary);
                                bool debugRuntime = IsDebugRuntime(vcCTool.RuntimeLibrary);

                                // Neither comes from the configuration's name alone: Conan only accepts
                                // its own build types, and Debug/Release for the runtime type.
                                string buildType = GetConanBuildType(vcConfig.ConfigurationName, debugRuntime);
                                string runtimeType = debugRuntime ? "Debug" : "Release";

                                var profileLines = new List<string>
                                {
                                    "",
                                    "[settings]",
                                    $"arch={arch}",
                                    $"build_type={buildType}",
                                    "compiler=msvc"
                                };
                                if (cppStd != null)
                                {
                                    profileLines.Add($"compiler.cppstd={cppStd}");
                                }
                                profileLines.AddRange(new[]
                                {
                                    $"compiler.runtime={runtime}",
                                    "",
                                    $"compiler.runtime_type={runtimeType}",
                                    $"compiler.version={compilerVersion}",
                                    "os=Windows",
                                    ""
                                });
                                string profileContent = string.Join("\r\n", profileLines);

                                bool shouldWriteFile = true;

                                if (File.Exists(profilePath))
                                {
                                    string existingProfileContent = File.ReadAllText(profilePath);
                                    if (existingProfileContent == profileContent)
                                    {
                                        shouldWriteFile = false;
                                    }
                                }

                                if (shouldWriteFile)
                                {
                                    File.WriteAllText(profilePath, profileContent);
                                    // We create this .runconan file to indicate that there were changes in the profile and that
                                    // Conan should run, this file is removed by the script that launches conan to reset the state
                                    string runConanFilePath = System.IO.Path.Combine(conanProjectDirectory, ".runconan");
                                    File.WriteAllText(runConanFilePath, "");
                                }
                            }
                            catch (Exception configEx)
                            {
                                // Don't let one bad configuration (e.g. an unrecognized/new PlatformToolset)
                                // prevent profiles from being generated for the other configurations.
                                configErrors.Add($"{profileName}: {configEx.Message}");
                            }
                        }

                        // Profiles are regenerated at every build, solution load and configuration
                        // switch: report a configuration that cannot be mapped once, in the Conan
                        // pane, rather than with a message box each time.
                        foreach (string configError in configErrors)
                        {
                            if (_profileErrors.Add($"{project.FullName}|{configError}"))
                            {
                                ConanOutputPane.Log(Loc.T($"[{project.Name}] No Conan profile generated for {configError}", $"[{project.Name}] Pas de profil Conan généré pour {configError}"));
                            }
                        }
                    }
                }
                //MessageBox.Show($"Generated profiles for actual project.", "Conan profiles generated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T($"There was a problem generating the file: {ex.Message}", $"Un problème est survenu lors de la génération du fichier : {ex.Message}"), "Error - Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }
}
