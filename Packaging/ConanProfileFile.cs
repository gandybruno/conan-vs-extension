using System;
using System.Collections.Generic;
using System.IO;

namespace conan_vs_extension.Packaging
{
    /**
     * Reads back the Conan profiles ConanProfilesManager writes under a project's `.conan`
     * folder, so the settings Conan will really use (build_type, arch...) are taken from the
     * profile itself instead of being worked out a second time.
     */
    public static class ConanProfileFile
    {
        /**
         * @return The `[settings]` of the profile ("build_type" -> "Debug"...); empty when the file is missing.
         */
        public static Dictionary<string, string> ReadSettings(string profilePath)
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(profilePath))
            {
                return settings;
            }

            bool inSettings = false;
            foreach (string rawLine in File.ReadAllLines(profilePath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("["))
                {
                    inSettings = line.Equals("[settings]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                int equals = line.IndexOf('=');
                if (inSettings && equals > 0)
                {
                    settings[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
                }
            }

            return settings;
        }
    }
}
