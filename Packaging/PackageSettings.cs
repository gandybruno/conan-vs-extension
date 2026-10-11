using System.Collections.Generic;

namespace conan_vs_extension.Packaging
{
    /**
     * The `package` section of `conandata.yml`: everything the generated library recipe
     * (Resources/conanfile_library.py) needs to turn the project into a Conan package. The
     * version is not here: it lives in `version.txt` (see VersionFile).
     */
    public class PackageSettings
    {
        public const string StaticLibrary = "static-library";
        public const string SharedLibrary = "shared-library";

        // Entered by the user.

        public string Name { get; set; }
        public string Description { get; set; }
        public string License { get; set; }

        /** Folder of the public headers, relative to the project. */
        public string Headers { get; set; } = "include";

        /** Whether the .pdb files are packaged next to the binaries. */
        public bool Pdb { get; set; } = true;

        /** Whether a project built with a newer MSVC toolset may use the binaries of an older one. */
        public bool ToolsetCompatibility { get; set; } = true;

        /** The project configurations packaged by "Create package", e.g. "Debug|x64". */
        public List<string> Configurations { get; set; } = new List<string>();

        // Read from the Visual Studio project each time a package is created (PackageCreator).

        /** StaticLibrary or SharedLibrary. */
        public string Type { get; set; }

        /** Library names without extension (the project's target name). */
        public List<string> Libs { get; set; } = new List<string>();

        /** Conan build types compiled with /GL, whose binaries only suit the toolset that built them. */
        public List<string> LtcgBuildTypes { get; set; } = new List<string>();

        /** Output folder relative to the project, per "<build_type>_<arch>" (Conan settings). */
        public Dictionary<string, string> OutputDirs { get; set; } = new Dictionary<string, string>();
    }
}
