#nullable enable
using System.Globalization;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Lingua di partenza: italiano solo se Windows è in italiano, altrimenti inglese.
    /// Una scelta salvata nelle impostazioni ha sempre la precedenza.
    /// </summary>
    internal static class AppLanguage
    {
        public static string SystemDefault
        {
            get
            {
                try
                {
                    return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en";
                }
                catch { return "en"; }
            }
        }

        /// <summary>Lingua attiva, per i componenti che non ricevono la lingua dal player.</summary>
        public static string Current { get; set; } = SystemDefault;
        public static bool English => Current == "en";
        public static string T(string italian, string english) => English ? english : italian;

        public static string Normalize(string? language)
            => language is "en" or "it" ? language : SystemDefault;
    }
}
