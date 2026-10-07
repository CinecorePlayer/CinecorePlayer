#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>Sottotitoli SubRip (.srt): lettura tollerante, scrittura pulita in UTF-8.</summary>
    internal static class SubtitleFile
    {
        public sealed record Cue(double Start, double End, string Text);

        private static readonly Regex Timing = new(
            @"(\d{1,3}):(\d{1,2}):(\d{1,2})[,.:](\d{1,3})\s*-->\s*(\d{1,3}):(\d{1,2}):(\d{1,2})[,.:](\d{1,3})", RegexOptions.Compiled);

        /// <summary>I .srt in giro sono in UTF-8 o, spesso per l'italiano, in Windows-1252:
        /// se i byte non sono UTF-8 valido si leggono come 1252 (altrimenti "è" diventa "Ã¨").</summary>
        public static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
            catch (DecoderFallbackException) { }
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
            catch { return Encoding.Latin1.GetString(bytes); }
        }

        public static List<Cue> Parse(string text)
        {
            var cues = new List<Cue>();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var match = Timing.Match(lines[i]);
                if (!match.Success) continue;
                double start = Seconds(match, 1), end = Seconds(match, 5);
                var body = new List<string>();
                int next = i + 1;
                for (; next < lines.Length; next++)
                {
                    string line = lines[next];
                    if (line.Trim().Length == 0) break;
                    // Un blocco senza riga vuota prima: numero + tempi del sottotitolo successivo.
                    if (next + 1 < lines.Length && Timing.IsMatch(lines[next + 1]) && int.TryParse(line.Trim(), out _)) break;
                    if (Timing.IsMatch(line)) break;
                    body.Add(line.TrimEnd());
                }
                i = next - 1;
                if (end > start && body.Count > 0)
                    cues.Add(new Cue(start, end, string.Join("\n", body)));
            }
            return cues.OrderBy(cue => cue.Start).ToList();
        }

        private static double Seconds(Match match, int group)
        {
            int Part(int offset) => int.Parse(match.Groups[group + offset].Value, CultureInfo.InvariantCulture);
            string fraction = match.Groups[group + 3].Value.PadRight(3, '0');
            return Part(0) * 3600 + Part(1) * 60 + Part(2) + int.Parse(fraction, CultureInfo.InvariantCulture) / 1000.0;
        }

        public static string Write(IEnumerable<Cue> cues)
        {
            var builder = new StringBuilder();
            int number = 1;
            foreach (var cue in cues)
            {
                if (cue.End <= 0) continue; // finito prima dell'inizio dopo uno spostamento
                builder.Append(number++).Append("\r\n")
                    .Append(Stamp(Math.Max(0, cue.Start))).Append(" --> ").Append(Stamp(cue.End)).Append("\r\n")
                    .Append(cue.Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
            }
            return builder.ToString();
        }

        private static string Stamp(double seconds)
        {
            long ms = (long)Math.Round(seconds * 1000);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", ms / 3600000, ms / 60000 % 60, ms / 1000 % 60, ms % 1000);
        }

        public static void Save(string path, IEnumerable<Cue> cues) =>
            File.WriteAllText(path, Write(cues), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        /// <summary>Cartella del player per i sottotitoli dei film che stanno in cartelle non scrivibili
        /// (dischi di rete, mount in sola lettura). I file hanno lo stesso nome del video.</summary>
        public static string PlayerFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "subtitles");

        /// <summary>I .srt del video (stesso nome, eventuale suffisso di lingua): accanto al file e nella cartella del player.</summary>
        public static List<string> FindNextTo(string videoPath)
        {
            var result = FindIn(Path.GetDirectoryName(videoPath), videoPath);
            result.AddRange(FindInPlayerFolder(videoPath));
            return result;
        }

        public static List<string> FindInPlayerFolder(string videoPath) => FindIn(PlayerFolder, videoPath);

        private static List<string> FindIn(string? folder, string videoPath)
        {
            var result = new List<string>();
            try
            {
                string name = Path.GetFileNameWithoutExtension(videoPath);
                if (folder == null || name.Length == 0 || !Directory.Exists(folder)) return result;
                foreach (string file in Directory.EnumerateFiles(folder, "*.srt"))
                {
                    string candidate = Path.GetFileNameWithoutExtension(file);
                    if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
                        result.Add(file);
                }
            }
            catch { }
            return result;
        }
    }
}
