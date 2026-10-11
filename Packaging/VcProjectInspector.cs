using System;
using System.Collections;
using System.Collections.Generic;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VCProjectEngine;

namespace conan_vs_extension.Packaging
{
    /**
     * What packaging needs to know about one configuration of a Visual C++ project
     * (e.g. "Debug|x64"), read once from Visual Studio so the rest of the packaging code works
     * on plain data.
     */
    public class LibraryConfiguration
    {
        /** "Debug|x64". */
        public string Name { get; set; }

        public string ConfigurationName { get; set; }

        public string PlatformName { get; set; }

        /** Name of its Conan profile under `.conan` (see ConanProfilesManager.getProfileName). */
        public string ProfileName { get; set; }

        /** PackageSettings.StaticLibrary or SharedLibrary; null when it does not build a library. */
        public string PackageType { get; set; }

        /** File name of the library, without extension. */
        public string TargetName { get; set; }

        /** Absolute folder the binaries are written to. */
        public string OutputDirectory { get; set; }

        /** Compiled with /GL (whole program optimization). */
        public bool WholeProgramOptimization { get; set; }

        public bool IsLibrary => PackageType != null;
    }

    /**
     * Reads from a Visual C++ project what it builds: for each configuration, whether it is a
     * library, its name and where Visual Studio writes it.
     */
    public static class VcProjectInspector
    {
        /**
         * @return One entry per configuration of the project; empty when it is not a Visual C++ project.
         */
        public static List<LibraryConfiguration> GetConfigurations(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var configurations = new List<LibraryConfiguration>();
            if (!(project?.Object is VCProject vcProject))
            {
                return configurations;
            }

            foreach (VCConfiguration vcConfig in (IEnumerable)vcProject.Configurations)
            {
                try
                {
                    configurations.Add(new LibraryConfiguration
                    {
                        Name = vcConfig.Name,
                        ConfigurationName = vcConfig.ConfigurationName,
                        PlatformName = (vcConfig.Platform as VCPlatform)?.Name,
                        ProfileName = ConanProfilesManager.getProfileName(vcConfig),
                        PackageType = GetPackageType(Evaluate(vcConfig, "$(ConfigurationType)")),
                        TargetName = Evaluate(vcConfig, "$(TargetName)"),
                        OutputDirectory = Evaluate(vcConfig, "$(OutDir)"),
                        WholeProgramOptimization = IsEnabled(Evaluate(vcConfig, "$(WholeProgramOptimization)"))
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VcProjectInspector] Configuration '{vcConfig.Name}' could not be read: {ex.Message}");
                }
            }

            return configurations;
        }

        private static string Evaluate(VCConfiguration vcConfig, string expression)
        {
            return (vcConfig.Evaluate(expression) ?? string.Empty).Trim();
        }

        private static string GetPackageType(string configurationType)
        {
            if (configurationType.Equals("StaticLibrary", StringComparison.OrdinalIgnoreCase))
            {
                return PackageSettings.StaticLibrary;
            }

            if (configurationType.Equals("DynamicLibrary", StringComparison.OrdinalIgnoreCase))
            {
                return PackageSettings.SharedLibrary;
            }

            return null;
        }

        // "true", but also "PGInstrument", "PGOptimize"...: every value other than off uses /GL.
        private static bool IsEnabled(string wholeProgramOptimization)
        {
            return wholeProgramOptimization.Length > 0 &&
                   !wholeProgramOptimization.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }
}
