using System;
using System.IO;
using conan_vs_extension.Packaging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace conan_vs_extension
{
    /**
     * The content of a project's `conandata.yml`: the packages it consumes (`requirements`) and,
     * for a library that is itself published as a package, how it is packaged (`package`). Every
     * read and write of that file goes through this class, so one section is never lost when the
     * other is updated.
     */
    public class ConanDataFile
    {
        public const string FileName = "conandata.yml";

        /** Requirement strings, e.g. "zlib/1.3.1" ("name/version[@user/channel]"). */
        public string[] Requirements { get; set; } = new string[] { };

        /** Null for a project that only consumes packages. */
        public PackageSettings Package { get; set; }

        public static string GetPath(string projectDirectory)
        {
            return Path.Combine(projectDirectory, FileName);
        }

        /**
         * Reads the project's `conandata.yml`. A missing or empty file gives an empty instance,
         * never null.
         */
        public static ConanDataFile Load(string projectDirectory)
        {
            string path = GetPath(projectDirectory);
            if (!File.Exists(path))
            {
                return new ConanDataFile();
            }

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            // Deserialize<T> returns null (rather than a default-constructed instance) when the
            // YAML document is empty (e.g. an empty or comment-only conandata.yml).
            ConanDataFile data = deserializer.Deserialize<ConanDataFile>(File.ReadAllText(path)) ?? new ConanDataFile();
            data.Requirements = data.Requirements ?? new string[] { };
            return data;
        }

        /**
         * Writes the file with its guard. The caller decides whether it may be written
         * (ManagedFileGuard.CanWrite): a hand-edited file must be left alone.
         */
        public void Save(string projectDirectory)
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();

            string contents = string.Join(Environment.NewLine, ManagedFileGuard.Lines) + Environment.NewLine + serializer.Serialize(this);
            File.WriteAllText(GetPath(projectDirectory), contents);
        }
    }
}
