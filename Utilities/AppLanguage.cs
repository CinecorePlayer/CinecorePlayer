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
        public static string T(string italian, string english) => Localize(English ? english : italian);

        /// <summary>
        /// Spagnolo: l'interfaccia lavora in inglese (tutte le scelte "inglese o italiano" restano quelle di sempre)
        /// e ogni testo, prima di arrivare a schermo, passa da qui e viene sostituito con la sua traduzione. Un testo
        /// senza traduzione resta in inglese.
        /// </summary>
        public static bool Spanish { get; set; }

        public static string Localize(string text) => Spanish ? SpanishTexts.Get(text) : text;

        public static string Normalize(string? language)
            => language is "en" or "it" or "es" ? language : SystemDefault;
    }
}
