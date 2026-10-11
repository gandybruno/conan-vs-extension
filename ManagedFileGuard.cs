using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace conan_vs_extension
{
    /**
     * The two comment lines at the top of the files this extension generates (`conanfile.py`,
     * `conandata.yml`). A file is only ever rewritten while they are still there: removing them
     * is how a user takes a file over, and it is then never overwritten again.
     */
    public static class ManagedFileGuard
    {
        public static readonly string[] Lines = new[]
        {
            "# This file is managed by the Conan Visual Studio Extension, contents will be overwritten.",
            "# To keep your changes, remove these comment lines, but the plugin won't be able to modify your requirements"
        };

        /**
         * @return False if the file doesn't exist, is shorter than the guard, or its first lines don't match it exactly.
         */
        public static bool IsGuarded(string path)
        {
            if (!File.Exists(path)) return false;

            var firstLines = new List<string>(Lines.Length);

            using (var reader = new StreamReader(path))
            {
                for (int i = 0; i < Lines.Length; i++)
                {
                    if (reader.EndOfStream) return false;
                    firstLines.Add(reader.ReadLine());
                }
            }

            return firstLines.SequenceEqual(Lines);
        }

        /**
         * Whether the extension may write path: it does not exist yet, or it still carries the guard.
         */
        public static bool CanWrite(string path)
        {
            return !File.Exists(path) || IsGuarded(path);
        }
    }
}
