using System;
using System.IO;
using System.Linq;
using System.Reflection;
using conan_vs_extension.Packaging;

namespace conan_vs_extension
{
    /**
     * Generates and updates the two files this extension keeps per project: `conanfile.py` (a
     * fixed recipe: the one of an application that only consumes packages, or the one of a
     * library that is also published as a package) and `conandata.yml` (the data both recipes
     * read - see ConanDataFile). Both files carry a comment guard at the top (ManagedFileGuard);
     * every write here first checks it is still present, so a user who has hand-edited either
     * file (and therefore removed the guard) never has their changes silently overwritten.
     */
    public static class ConanFileManager
    {
        private const string LibraryRecipeResource = "conan_vs_extension.Resources.conanfile_library.py";

        private static readonly string[] _applicationRecipe = new[]
        {
            "",
            "from conan import ConanFile",
            "from conan.tools.microsoft import vs_layout, MSBuildDeps",
            "class ConanApplication(ConanFile):",
            "    package_type = \"application\"",
            "    settings = \"os\", \"compiler\", \"build_type\", \"arch\"",
            "",
            "    def layout(self):",
            "        vs_layout(self)",
            "",
            "    def generate(self):",
            "        deps = MSBuildDeps(self)",
            "        deps.generate()",
            "",
            "    def requirements(self):",
            "        requirements = self.conan_data.get('requirements', [])",
            "        for requirement in requirements:",
            "            self.requires(requirement.replace('@_/_', ''))"
        };

        /**
         * Whether the file at path still starts with the "managed by this extension" guard.
         */
        public static bool IsFileCommentGuarded(string path)
        {
            return ManagedFileGuard.IsGuarded(path);
        }

        /**
         * Reads the `requirements:` list out of a project's `conandata.yml`, e.g. `["zlib/1.3.1", "fmt/10.2.1"]`.
         *
         * @param projectDirectory Directory containing the project's `conandata.yml`.
         * @return The requirement strings ("name/version[@user/channel]"), or an empty array if the file is missing/empty.
         */
        public static string[] GetConandataRequirements(string projectDirectory)
        {
            return ConanDataFile.Load(projectDirectory).Requirements.Select(NormalizeReference).ToArray();
        }

        /**
         * Drops the "@_/_" (user "_", channel "_") suffix from a recipe reference. Conan 2 does not
         * treat "name/1.0@_/_" as equal to "name/1.0" (no user/channel), so keeping it makes Conan
         * ignore editable packages registered under the plain reference. A real user/channel
         * ("name/1.0@user/channel") is kept as is.
         */
        public static string NormalizeReference(string reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return reference;
            }

            return System.Text.RegularExpressions.Regex.Replace(reference.Trim(), @"@_/_(?![\w.\-])", string.Empty);
        }

        /**
         * Rewrites a guarded `conandata.yml` that still holds "@_/_" references (written by older
         * versions of the extension) in their plain "name/version" form. No-ops otherwise.
         */
        public static void NormalizeConandataFile(string projectDirectory)
        {
            string path = ConanDataFile.GetPath(projectDirectory);
            if (!File.Exists(path) || !IsFileCommentGuarded(path))
            {
                return;
            }

            string text = File.ReadAllText(path);
            string normalized = System.Text.RegularExpressions.Regex.Replace(text, @"@_/_(?![\w.\-])", string.Empty);
            if (normalized != text)
            {
                File.WriteAllText(path, normalized);
            }
        }

        /**
         * (Re)generates the project's `conanfile.py`, unless a hand-edited (non-guarded) one
         * already exists: the library recipe when `conandata.yml` has a `package` section, the
         * application recipe otherwise.
         *
         * @param projectDirectory Directory the file should live in.
         * @return True if the file was (re)written; false if a hand-edited version was left untouched.
         */
        public static bool ReCreateConanfile(string projectDirectory)
        {
            string conanfilePath = Path.Combine(projectDirectory, "conanfile.py");
            if (!ManagedFileGuard.CanWrite(conanfilePath))
            {
                return false;
            }

            bool isPackaged = ConanDataFile.Load(projectDirectory).Package != null;
            string[] recipe = isPackaged ? LoadLibraryRecipe() : _applicationRecipe;

            // Leave an up-to-date file alone: it may be open in the editor.
            string contents = string.Join(Environment.NewLine, ManagedFileGuard.Lines.Concat(recipe));
            if (!File.Exists(conanfilePath) || File.ReadAllText(conanfilePath) != contents)
            {
                File.WriteAllText(conanfilePath, contents);
            }
            return true;
        }

        /**
         * The lines of the library recipe embedded in the assembly (Resources/conanfile_library.py).
         */
        private static string[] LoadLibraryRecipe()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(LibraryRecipeResource))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            }
        }

        /**
         * (Re)creates an empty, guarded `conandata.yml` (just a `requirements:` key), unless a hand-edited (non-guarded) one already exists.
         *
         * @param projectDirectory Directory the file should live in.
         * @return True if the file was (re)written; false if a hand-edited version was left untouched.
         */
        public static bool ReCreateConanData(string projectDirectory)
        {
            string conandataPath = ConanDataFile.GetPath(projectDirectory);
            if (!ManagedFileGuard.CanWrite(conandataPath))
            {
                return false;
            }

            string conandataContents = string.Join(Environment.NewLine,
                ManagedFileGuard.Lines.Concat(new string[] { "requirements:" })
            );

            File.WriteAllText(conandataPath, conandataContents);
            return true;
        }

        /**
         * Adds (or replaces) a requirement in the project's `conandata.yml`. Since a
         * conanfile.py can only require one version/channel of a given library at a time, any
         * existing requirement for the same library name is replaced rather than appended
         * alongside the new one. No-ops if the file isn't guarded (hand-edited) or already lists
         * this exact requirement.
         *
         * @param projectDirectory Directory containing the project's `conandata.yml`.
         * @param newRequirement Full requirement string, e.g. "zlib/1.3.1".
         */
        public static void WriteNewRequirement(string projectDirectory, string newRequirement)
        {
            if (!IsFileCommentGuarded(ConanDataFile.GetPath(projectDirectory)))
            {
                return;
            }

            ConanDataFile data = ConanDataFile.Load(projectDirectory);
            string[] requirements = data.Requirements.Select(NormalizeReference).ToArray();
            newRequirement = NormalizeReference(newRequirement);
            string libraryName = newRequirement.Split('/')[0];

            // A conanfile.py can only require one version/channel of a given library at a time
            // (Conan raises "Duplicated requirement" otherwise), so replace any existing
            // requirement for the same library instead of just appending if the exact string differs.
            if (!requirements.Contains(newRequirement))
            {
                data.Requirements = requirements
                    .Where(req => req.Split('/')[0] != libraryName)
                    .Append(newRequirement)
                    .ToArray();
                data.Save(projectDirectory);
            }
        }

        /**
         * Removes one exact requirement string from the project's `conandata.yml`. No-ops if the file isn't guarded or doesn't contain it.
         *
         * @param projectDirectory Directory containing the project's `conandata.yml`.
         * @param oldRequirement The exact requirement string to remove, e.g. "zlib/1.3.1" - must match a GetConandataRequirements entry exactly (see the caller's note on why the version must come from there, not a version picker's current selection).
         */
        public static void RemoveRequirement(string projectDirectory, string oldRequirement)
        {
            if (!IsFileCommentGuarded(ConanDataFile.GetPath(projectDirectory)))
            {
                return;
            }

            ConanDataFile data = ConanDataFile.Load(projectDirectory);
            string[] requirements = data.Requirements.Select(NormalizeReference).ToArray();
            if (requirements.Contains(oldRequirement))
            {
                data.Requirements = requirements.Where(req => req != oldRequirement).ToArray();
                data.Save(projectDirectory);
            }
        }

        /**
         * Stores how the project is packaged (the `package` section of `conandata.yml`) and
         * switches its `conanfile.py` to the library recipe. Creates `conandata.yml` when the
         * project had none yet.
         *
         * @return False, without writing anything, when `conandata.yml` was hand-edited.
         */
        public static bool WritePackageSettings(string projectDirectory, PackageSettings settings)
        {
            if (!ManagedFileGuard.CanWrite(ConanDataFile.GetPath(projectDirectory)))
            {
                return false;
            }

            ConanDataFile data = ConanDataFile.Load(projectDirectory);
            data.Package = settings;
            data.Save(projectDirectory);
            ReCreateConanfile(projectDirectory);
            return true;
        }
    }
}
