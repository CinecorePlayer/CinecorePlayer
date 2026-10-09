#nullable enable
using System;
using System.Text.RegularExpressions;
using System.Text;

namespace CinecorePlayer2025.Utilities
{
    internal static class MusicTextIdentity
    {
        // Repair only reversible UTF-8 decoded as Windows-1252 (or Latin-1), including cached
        // tags: "LumiÃ¨re", "PrzybyÅ‚owicz", "Thereâ€™s", "DeÌsole" (combining accents).
        private const string Continuation = @"[-¿ŒœŠšŸŽžƒˆ˜–—‘-„†-•…‰‹›€™]";
        private static readonly Regex Mojibake = new(
            "(?:[Â-ß]" + Continuation + "|[à-ï]" + Continuation + "{2}|[ð-ô]" + Continuation + "{3})+",
            RegexOptions.Compiled);

        public static string RepairEncoding(string? value)
        {
            string text = value ?? "";
            if (text.Length == 0) return text;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var legacy = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var strict = new UTF8Encoding(false, true);
            for (int pass = 0; pass < 2; pass++)
            {
                string repaired = Mojibake.Replace(text, match =>
                {
                    try
                    {
                        var bytes = new byte[match.Value.Length];
                        for (int i = 0; i < match.Value.Length; i++)
                        {
                            char c = match.Value[i];
                            // Byte non definiti in 1252 (0x81, 0x8D, 0x8F, 0x90, 0x9D) arrivano come C1.
                            if (c <= 'ÿ') bytes[i] = (byte)c;
                            else { var one = legacy.GetBytes(new[] { c }); if (one.Length != 1) return match.Value; bytes[i] = one[0]; }
                        }
                        string decoded = strict.GetString(bytes);
                        return decoded.Length < match.Value.Length ? decoded : match.Value;
                    }
                    catch (ArgumentException) { return match.Value; }
                });
                if (repaired == text) break;
                text = repaired;
            }
            try { text = text.Normalize(NormalizationForm.FormC); } catch (ArgumentException) { }
            return text;
        }

        // Tags are titles, not filenames: keep numbers, punctuation and edition names.
        public static string Title(string? value, bool filename = false)
        {
            string title = RepairEncoding(value).Normalize().Trim();
            if (filename)
            {
                title = Regex.Replace(title, @"\.(flac|mp3|m4a|wav|ogg|opus|aac|wma|aiff|ape)$", "", RegexOptions.IgnoreCase);
                title = Regex.Replace(title, @"^(?:(?:disc|cd)\s*\d+\s*[-_. ]+)?\d{1,3}(?:[-_.]\d{1,3})?\s*[-_.)]\s*", "", RegexOptions.IgnoreCase);
                title = Regex.Replace(title, @"^0\d{1,2}\s+", "");
                title = title.Replace('_', ' ');
            }
            title = Regex.Replace(title, @"[\[(](?:official\s+(?:audio|video|music video)|lyric\s*video|\d+\s*bit[^\])]*|flac|mp3|\d+(?:\.\d+)?\s*khz)[\])]", "", RegexOptions.IgnoreCase);
            return Regex.Replace(title, @"\s+", " ").Trim();
        }

        public static string Artist(string? value) => Regex.Replace(RepairEncoding(value).Normalize(), @"[\p{Cf}]", "").Trim();
    }
}
