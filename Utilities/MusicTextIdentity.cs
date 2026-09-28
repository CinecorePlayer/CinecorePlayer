#nullable enable
using System;
using System.Text.RegularExpressions;
using System.Text;

namespace CinecorePlayer2025.Utilities
{
    internal static class MusicTextIdentity
    {
        // Repair only reversible UTF-8 decoded as Windows-1252, including cached tags.
        public static string RepairEncoding(string? value)
        {
            string text = value ?? "";
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            for (int pass = 0; pass < 2; pass++)
            {
                const string continuation = @"[\u0080-\u00BF\u0152\u0153\u0160\u0161\u0178\u017D\u017E\u0192\u02C6\u02DC\u2013-\u2026\u2030\u2039\u203A\u20AC\u2122]";
                var legacy = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                string repaired = Regex.Replace(text, "(?:[ÃÂ]" + continuation + "|â" + continuation + "{2}|ð" + continuation + "{3})+", match =>
                {
                    try { return new UTF8Encoding(false, true).GetString(legacy.GetBytes(match.Value)); }
                    catch (ArgumentException) { return match.Value; }
                });
                if (repaired == text) break;
                text = repaired;
            }
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
