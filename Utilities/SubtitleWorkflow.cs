#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>Per ogni film, il sottotitolo esterno che l'utente ha scelto di vedere (suffisso del file:
    /// "it", "en", oppure vuoto per "Film.srt"). Senza una scelta il player lascia i sottotitoli spenti.</summary>
    internal static class ExternalSubtitleChoice
    {
        private const int MaxEntries = 3000;
        private static readonly object Gate = new();
        private static Dictionary<string, string>? _entries;
        private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "subtitle-choice.json");

        private static Dictionary<string, string> Entries()
        {
            if (_entries != null) return _entries;
            try
            {
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath));
                if (loaded != null) return _entries = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return _entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public static string? Get(string videoPath)
        {
            lock (Gate) return Entries().TryGetValue(videoPath, out string? suffix) ? suffix : null;
        }

        public static void Set(string videoPath, string? suffix)
        {
            lock (Gate)
            {
                var entries = Entries();
                if (suffix == null ? !entries.Remove(videoPath) : entries.TryGetValue(videoPath, out string? current) && current == suffix) return;
                if (suffix != null) entries[videoPath] = suffix;
                if (entries.Count > MaxEntries)
                    foreach (string old in entries.Keys.Take(entries.Count - MaxEntries).ToArray()) entries.Remove(old);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                    string temp = StorePath + ".tmp";
                    File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(entries));
                    File.Move(temp, StorePath, true);
                }
                catch (Exception ex) { Dbg.Warn("[SUBS] choice save failed: " + ex.Message); }
            }
        }
    }

    /// <summary>Salvataggio di un sottotitolo accanto al video e riallineamento sull'audio.</summary>
    internal static class SubtitleWorkflow
    {
        /// <param name="Replaced">Il file esisteva gia' ed e' stato riscritto.</param>
        public sealed record Outcome(string Path, SubtitleAligner.Result? Alignment, bool TimingChanged, bool Replaced)
        {
            /// <summary>Salvato nella cartella del player perche' quella del film non e' scrivibile.</summary>
            public bool InPlayerFolder => string.Equals(System.IO.Path.GetDirectoryName(Path), SubtitleFile.PlayerFolder, StringComparison.OrdinalIgnoreCase);
        }

        private static string BackupFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "subtitle-backup");

        /// <summary>Nome del file accanto al video: "Film.it.srt" (i renderer lo caricano da soli e ne leggono la lingua).</summary>
        public static string TargetPath(string videoPath, string language) =>
            Path.Combine(Path.GetDirectoryName(videoPath) ?? "", Path.GetFileNameWithoutExtension(videoPath) + "." + language + ".srt");

        // Prima di sovrascrivere un .srt se ne tiene una copia fuori dalla cartella del film
        // (accanto al video comparirebbe nel player come un'altra traccia).
        private static void Backup(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                Directory.CreateDirectory(BackupFolder);
                File.Copy(path, Path.Combine(BackupFolder, Path.GetFileNameWithoutExtension(path) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".srt"), overwrite: true);
            }
            catch (Exception ex) { Dbg.Warn("[SUBS] backup failed: " + ex.Message); }
        }

        /// <summary>Salva il testo scaricato accanto al video, riallineato se l'analisi e' affidabile.</summary>
        public static async Task<Outcome> SaveDownloadedAsync(string videoPath, string language, string srtText, IProgress<double>? progress, CancellationToken ct)
        {
            var cues = SubtitleFile.Parse(srtText);
            if (cues.Count == 0) throw new InvalidDataException("empty subtitle");
            SubtitleAligner.Result? alignment = null;
            try { alignment = await SubtitleAligner.AlignAsync(videoPath, cues, progress, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn("[SUBS] align failed: " + ex.Message); }
            bool apply = alignment is { Reliable: true, Changed: true };
            var final = apply ? SubtitleAligner.Apply(cues, alignment!) : cues;
            string target = TargetPath(videoPath, language);
            bool replaced;
            try
            {
                replaced = File.Exists(target);
                Backup(target);
                SubtitleFile.Save(target, final);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cartella del film non scrivibile (disco di rete, mount in sola lettura).
                Directory.CreateDirectory(SubtitleFile.PlayerFolder);
                target = Path.Combine(SubtitleFile.PlayerFolder, Path.GetFileName(target));
                replaced = File.Exists(target);
                Backup(target);
                SubtitleFile.Save(target, final);
            }
            Dbg.Log($"[SUBS] saved '{target}' ({cues.Count} cues), alignment: {alignment?.Detail ?? "none"}, applied={apply}", Dbg.LogLevel.Info);
            return new Outcome(target, alignment, apply, replaced);
        }

        /// <summary>Riallinea un .srt gia' presente. Il file viene riscritto solo se l'analisi e' affidabile e cambia davvero i tempi.</summary>
        public static async Task<Outcome> RealignAsync(string videoPath, string srtPath, IProgress<double>? progress, CancellationToken ct)
        {
            var cues = SubtitleFile.Parse(SubtitleFile.Decode(await File.ReadAllBytesAsync(srtPath, ct).ConfigureAwait(false)));
            var alignment = await SubtitleAligner.AlignAsync(videoPath, cues, progress, ct).ConfigureAwait(false);
            bool apply = alignment is { Reliable: true, Changed: true };
            if (apply)
            {
                Backup(srtPath);
                SubtitleFile.Save(srtPath, SubtitleAligner.Apply(cues, alignment));
            }
            Dbg.Log($"[SUBS] realign '{Path.GetFileName(srtPath)}': {alignment.Detail}, offset {alignment.OffsetSeconds:0.00}s, applied={apply}", Dbg.LogLevel.Info);
            return new Outcome(srtPath, alignment, apply, apply);
        }

        /// <summary>Descrizione breve dell'esito, per la riga di stato.</summary>
        public static string Describe(Outcome outcome, bool english)
        {
            string T(string italian, string englishText) => english ? englishText : italian;
            var alignment = outcome.Alignment;
            if (alignment == null || !alignment.Reliable)
                return T("allineamento sull'audio non sicuro: tempi lasciati com'erano", "audio alignment not certain: timings left as they were");
            if (!alignment.Changed)
                return T("già in sincrono con l'audio", "already in sync with the audio");
            string offset = alignment.OffsetSeconds.ToString("+0.00;-0.00", System.Globalization.CultureInfo.CurrentCulture) + " s";
            return Math.Abs(alignment.Speed - 1) > 1e-6
                ? T("riallineato: velocità corretta e spostato di ", "realigned: speed corrected and shifted by ") + offset
                : T("riallineato: spostato di ", "realigned: shifted by ") + offset;
        }
    }
}
