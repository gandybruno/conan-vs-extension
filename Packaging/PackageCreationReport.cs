using System.Windows;

namespace conan_vs_extension.Packaging
{
    /**
     * Tells the user how a package creation ended - the same way from the Package tab and from
     * the "Create Conan Package" menu command.
     */
    public static class PackageCreationReport
    {
        /**
         * One or two sentences describing the outcome.
         */
        public static string Describe(PackageCreationResult result)
        {
            if (result.Success)
            {
                return Loc.T($"Package {result.Reference} created in the local Conan cache ({string.Join(", ", result.Packaged)}).\nTo publish it: conan upload {result.Reference} -r=<remote>",
                             $"Paquet {result.Reference} créé dans le cache Conan local ({string.Join(", ", result.Packaged)}).\nPour le publier : conan upload {result.Reference} -r=<remote>");
            }

            if (result.Cancelled)
            {
                return Loc.T("Package creation cancelled.", "Création du paquet annulée.");
            }

            string done = result.Packaged.Count == 0
                ? string.Empty
                : Loc.T($"\n\nAlready packaged: {string.Join(", ", result.Packaged)}.", $"\n\nDéjà empaquetées : {string.Join(", ", result.Packaged)}.");
            return Loc.T($"The package could not be created:\n{result.Error}{done}", $"Le paquet n'a pas pu être créé :\n{result.Error}{done}");
        }

        /**
         * Shows the outcome in a message box (nothing when the user cancelled).
         */
        public static void Show(PackageCreationResult result)
        {
            if (result.Cancelled)
            {
                return;
            }

            MessageBox.Show(Describe(result), "Conan C/C++ Package Manager", MessageBoxButton.OK,
                            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }
}
