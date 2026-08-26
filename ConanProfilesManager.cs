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
    public class ConanProfilesManager
    {
        
        public ConanProfilesManager()
        {
        }

        public static string getProfileName(VCConfiguration vcConfig)
        {
            return vcConfig.Name.Replace("|", "_");
        }

        private string getConanArch(string platform)
        {
            var archMap = new Dictionary<string, string>();
            archMap["x64"] = "x86_64";
            archMap["Win32"] = "x86";
            archMap["ARM64"] = "armv8";
            return archMap[platform];
        }

        private string getConanCompilerVersion(string platformToolset, string vsVersion)
        {
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


        private string getConanCppstd(string languageStandard)
        {
            // https://learn.microsoft.com/en-us/cpp/build/reference/std-specify-language-standard-version?view=msvc-170

            if (languageStandard.ToLower().Contains("default"))
            {
                return "14";
            }

            List<string> cppStdValues = new List<string>() { "14", "17", "20", "23" };

            foreach (string cppStdValue in cppStdValues)
            {
                if (languageStandard.Contains(cppStdValue))
                {
                    return cppStdValue;
                }
            }
            return "null";
        }

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
                                string compilerVersion = getConanCompilerVersion(toolset, vsVersion);
                                string arch = getConanArch(vcConfig.Evaluate("$(PlatformName)").ToString());
                                IVCRulePropertyStorage generalRule = vcConfig.Rules.Item("ConfigurationGeneral") as IVCRulePropertyStorage;
                                string languageStandard = generalRule == null ? null : generalRule.GetEvaluatedPropertyValue("LanguageStandard");
                                string cppStd = getConanCppstd(languageStandard);

                                var tools = (IVCCollection) vcConfig.Tools;
                                var vcCTool = (VCCLCompilerTool) tools.Item("VCCLCompilerTool");

                                string runtime = GetRuntimeLibraryType(vcCTool.RuntimeLibrary);

                                string buildType = vcConfig.ConfigurationName;
                                string profileContent =
$@"
[settings]
arch={arch}
build_type={buildType}
compiler=msvc
compiler.cppstd={cppStd}
compiler.runtime={runtime}
" +
$@"
compiler.runtime_type={buildType}
compiler.version={compilerVersion}
os=Windows
";

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

                        if (configErrors.Count > 0)
                        {
                            MessageBox.Show(
                                "There was a problem generating the Conan profile(s) for:" + Environment.NewLine +
                                string.Join(Environment.NewLine, configErrors),
                                "Error - Conan C/C++ Package Manager",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                    }
                }
                //MessageBox.Show($"Generated profiles for actual project.", "Conan profiles generated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"There was a problem generating the file: {ex.Message}", "Error - Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }
}
