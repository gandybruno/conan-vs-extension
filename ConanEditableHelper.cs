using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace conan_vs_extension
{
    /**
     * Wraps Conan's "editable packages" (`conan editable add/remove/list`): a package whose sources
     * live in a local folder (the one holding its conanfile.py) is consumed straight from there
     * instead of from the Conan cache, so the library can be modified and debugged while a project
     * is using it. Editable state is global to the Conan home, not per project.
     */
    public static class ConanEditableHelper
    {
        /**
         * Lists the packages currently editable (`conan editable list --format=json`) as
         * "name/version" -> path of the recipe. Returns an empty dictionary (never throws) if conan
         * can't be run or has none.
         */
        public static async Task<Dictionary<string, string>> ListEditablesAsync()
        {
            var editables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                (int exitCode, string stdout, string _) = await ConanRemoteHelper.RunConanAsync("editable list --format=json", 20000);
                int jsonStart = stdout.IndexOf('{');
                if (exitCode != 0 || jsonStart < 0)
                {
                    return editables;
                }

                foreach (JProperty property in JObject.Parse(stdout.Substring(jsonStart)).Properties())
                {
                    // Keys look like "fmt/10.2.1" (possibly with "#revision" or "@user/channel").
                    string reference = property.Name;
                    int revision = reference.IndexOf('#');
                    if (revision > 0)
                    {
                        reference = reference.Substring(0, revision);
                    }
                    // Deliberately NOT normalized: Conan treats "name/1.0@_/_" (e.g. registered with
                    // --user=_ --channel=_) as a different reference from "name/1.0", so only an exact
                    // match applies to the project's requirement.

                    string path = (property.Value as JObject)?.Value<string>("path");
                    if (!string.IsNullOrEmpty(path))
                    {
                        editables[reference] = path;
                    }
                }
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log($"conan editable list : exception : {ex.Message}");
            }

            return editables;
        }

        /**
         * Makes name/version editable from the folder containing its conanfile.py
         * (`conan editable add <folder> --name --version`; the explicit name/version cover recipes
         * that do not declare them).
         */
        public static Task<(bool success, string error)> AddEditableAsync(string folder, string name, string version)
        {
            // `version` may carry a real "@user/channel" (kept as is); without one, no --user/--channel
            // is passed so the editable matches the plain "name/version" written in conandata.yml.
            string userChannel = string.Empty;
            int at = version.IndexOf('@');
            if (at >= 0)
            {
                string[] parts = version.Substring(at + 1).Split('/');
                version = version.Substring(0, at);
                if (parts.Length == 2 && parts[0] != "_" && parts[1] != "_")
                {
                    userChannel = $" --user=\"{parts[0]}\" --channel=\"{parts[1]}\"";
                }
            }

            if (!File.Exists(Path.Combine(folder, "conanfile.py")) && !File.Exists(Path.Combine(folder, "conanfile.txt")))
            {
                return Task.FromResult((false, $"Aucun conanfile.py trouvé dans '{folder}'."));
            }

            return AddAfterCleaningLegacyAsync(name, version, userChannel, folder);
        }

        private static async Task<(bool success, string error)> AddAfterCleaningLegacyAsync(string name, string version, string userChannel, string folder)
        {
            // Drop the "@_/_" editable left by the --user=_ --channel=_ workaround: it never matches
            // the plain requirement and would only linger in `conan editable list`.
            if (userChannel.Length == 0)
            {
                await ConanRemoteHelper.RunConanCheckedAsync($"editable remove --refs=\"{name}/{version}@_/_\"");
            }

            return await ConanRemoteHelper.RunConanCheckedAsync(
                $"editable add \"{folder.TrimEnd('\\', '/')}\" --name=\"{name}\" --version=\"{version}\"{userChannel}");
        }

        /**
         * Goes back to the normal (cache) package: `conan editable remove --refs=name/version`.
         */
        public static Task<(bool success, string error)> RemoveEditableAsync(string name, string version)
        {
            // `version` may already carry "@user/channel". Without one, also remove the "@_/_" form
            // (registered by the old --user=_ --channel=_ workaround), which is a distinct reference.
            string refs = $"--refs=\"{name}/{version}\"";
            if (!version.Contains("@"))
            {
                refs += $" --refs=\"{name}/{version}@_/_\"";
            }

            return ConanRemoteHelper.RunConanCheckedAsync($"editable remove {refs}");
        }
    }
}
