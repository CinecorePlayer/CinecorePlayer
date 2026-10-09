#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Traduzioni in spagnolo, cercate per testo inglese (vedi <see cref="AppLanguage.Localize"/>). I dati stanno in
    /// SpanishTexts.Data.cs, una riga per testo: inglese, tabulazione, spagnolo.
    /// </summary>
    internal static partial class SpanishTexts
    {
        private static Dictionary<string, string>? _map;

        public static string Get(string english)
        {
            if (string.IsNullOrEmpty(english)) return english;
            var map = _map ??= Build();
            if (map.TryGetValue(english, out string? spanish)) return spanish;
            // Testi composti al momento ("72 movies", "1h 28m left"): resta la parte con le cifre, si traduce la parola in coda.
            int cut = english.LastIndexOf(' ');
            if (cut > 0 && cut < english.Length - 1 && char.IsDigit(english[0]) &&
                map.TryGetValue(english[(cut + 1)..], out string? tail))
                return english[..(cut + 1)] + tail;
            return english;
        }

        private static Dictionary<string, string> Build()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string block in Data)
                foreach (string raw in block.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || tab >= line.Length - 1) continue;
                    map[Decode(line[..tab])] = Decode(line[(tab + 1)..]);
                }
            return map;
        }

        /// <summary>Nei dati ritorni a capo, tabulazioni e barre rovesciate sono scritti con una barra davanti.</summary>
        private static string Decode(string text)
        {
            if (text.IndexOf('\\') < 0) return text;
            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    char next = text[++i];
                    result.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => next });
                }
                else result.Append(c);
            }
            return result.ToString();
        }
    }
}
