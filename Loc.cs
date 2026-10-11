using System;
using System.Globalization;
using System.Windows.Markup;
using Microsoft.VisualStudio.Shell;

namespace conan_vs_extension
{
    /**
     * Minimal localization: every user-visible text is written once in English and once in
     * French next to where it is used (`Loc.T("Install", "Installer")` in code,
     * `{local:Loc 'Install', 'Installer'}` in XAML), and the one matching the language of the
     * Visual Studio UI is shown (French when VS runs in French, English otherwise).
     */
    public static class Loc
    {
        private static bool? _isFrench;

        /**
         * Whether the Visual Studio UI language is French. Read once from the shell (its UI
         * locale, not the Windows display language) by Initialize; until then, from the culture of
         * the current thread.
         */
        public static bool IsFrench
        {
            get
            {
                return _isFrench ?? string.Equals(
                    CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase);
            }
        }

        /**
         * Reads the Visual Studio UI language from the UI thread, whose culture Visual Studio sets
         * to its own UI language (not necessarily the Windows display language). Call once from
         * the UI thread at package start; other threads would report the Windows culture.
         */
        public static void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _isFrench = string.Equals(
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase);
        }

        /**
         * @param en English text.
         * @param fr French text.
         * @return The text in the language of the Visual Studio UI.
         */
        public static string T(string en, string fr)
        {
            return IsFrench ? fr : en;
        }
    }

    /**
     * XAML markup extension: `Text="{local:Loc 'English', 'Français'}"` (escape an apostrophe
     * with a backslash).
     */
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        private readonly string _en;
        private readonly string _fr;

        public LocExtension(string en, string fr)
        {
            _en = en;
            _fr = fr;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return Loc.T(_en, _fr);
        }
    }
}
