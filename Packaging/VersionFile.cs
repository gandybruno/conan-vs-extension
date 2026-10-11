using System.IO;

namespace conan_vs_extension.Packaging
{
    /**
     * `version.txt`, next to the project file: the version of the package, alone on one line so
     * that a release script or a CI job can read and bump it without knowing anything about Conan.
     */
    public static class VersionFile
    {
        public const string FileName = "version.txt";

        /**
         * @return The version, or null when the file is missing or empty.
         */
        public static string Read(string projectDirectory)
        {
            string path = Path.Combine(projectDirectory, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            string version = File.ReadAllText(path).Trim();
            return version.Length == 0 ? null : version;
        }

        /**
         * Writes the version, unless the file already holds it.
         */
        public static void Write(string projectDirectory, string version)
        {
            version = (version ?? string.Empty).Trim();
            if (Read(projectDirectory) != version)
            {
                File.WriteAllText(Path.Combine(projectDirectory, FileName), version + "\n");
            }
        }
    }
}
