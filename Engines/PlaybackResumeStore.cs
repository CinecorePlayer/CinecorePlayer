#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CinecorePlayer2025.Engines
{
    internal static class PlaybackResumeStore
    {
        private sealed class Envelope
        {
            public Dictionary<string, Entry> Items { get; set; } = new();
        }

        public sealed class Entry
        {
            public string MediaPath { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public double PositionSeconds { get; set; }
            public int PositionMinutes { get; set; }
            public double DurationSeconds { get; set; }
            public DateTime SavedAt { get; set; }
        }

        private static string ResumeFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025",
                "resume.json");

        private static string LegacyResumeFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CinecorePlayer2025",
                "resume.json");

        private const double MinSecondsForResume = 30.0;
        private const int MaxEntries = 30;

        private static bool IsInternalAppMediaPath(string? mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                return true;

            try
            {
                if (Uri.TryCreate(mediaPath, UriKind.Absolute, out var uri) &&
                    !uri.IsFile &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    return false;
                }
            }
            catch { }

            try
            {
                string fullPath = Path.GetFullPath(mediaPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string baseDir = Path.GetFullPath(AppContext.BaseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string assetsDir = Path.Combine(baseDir, "Assets")
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string assetsPrefix = assetsDir + Path.DirectorySeparatorChar;
                if (!string.IsNullOrWhiteSpace(assetsDir) &&
                    (string.Equals(fullPath, assetsDir, StringComparison.OrdinalIgnoreCase) ||
                     fullPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool ShouldPersistResumeForPath(string? mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                return false;

            try
            {
                if (Uri.TryCreate(mediaPath, UriKind.Absolute, out var uri) && !uri.IsFile)
                    return false;
            }
            catch { }

            if (IsInternalAppMediaPath(mediaPath))
                return false;

            return true;
        }

        private static void SanitizeEnvelope(Envelope env)
        {
            if (env?.Items == null || env.Items.Count == 0)
                return;

            try
            {
                var invalidKeys = env.Items
                    .Where(kvp => kvp.Value == null || !ShouldPersistResumeForPath(kvp.Value.MediaPath))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in invalidKeys)
                    env.Items.Remove(key);
            }
            catch { }
        }

        private static Envelope? TryLoadEnvelopeFrom(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var env = JsonSerializer.Deserialize<Envelope>(json) ?? new Envelope();
                    SanitizeEnvelope(env);
                    return env;
                }
            }
            catch { }
            return null;
        }

        // Parsed store, reused while neither file changes on disk. Loading used to read and
        // deserialize both JSON files on every lookup (and rewrite them): the library called
        // it per title while building a grid, ~100 ms each. Guarded by Sync; read-only
        // callers get the cached envelope, writers work on a copy.
        private static readonly object Sync = new();
        private static Envelope? _cache;
        private static (DateTime Primary, DateTime Legacy) _cacheStamp;

        private static DateTime StampOf(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        private static (DateTime Primary, DateTime Legacy) CurrentStamp()
            => (StampOf(ResumeFilePath), StampOf(LegacyResumeFilePath));

        private static Envelope LoadEnvelope()
        {
            lock (Sync)
            {
                var stamp = CurrentStamp();
                if (_cache != null && stamp == _cacheStamp)
                    return _cache;
                _cache = LoadEnvelopeFromDisk();
                _cacheStamp = CurrentStamp();
                return _cache;
            }
        }

        private static Envelope CloneEnvelope(Envelope source)
            => new() { Items = new Dictionary<string, Entry>(source.Items) };

        private static Envelope LoadEnvelopeFromDisk()
        {
            var primary = TryLoadEnvelopeFrom(ResumeFilePath);
            var legacy = TryLoadEnvelopeFrom(LegacyResumeFilePath);

            var merged = new Envelope();
            void Merge(Envelope? source)
            {
                if (source?.Items == null || source.Items.Count == 0)
                    return;

                foreach (var pair in source.Items)
                {
                    var entry = pair.Value;
                    if (entry == null || !ShouldPersistResumeForPath(entry.MediaPath))
                        continue;

                    string key = string.IsNullOrWhiteSpace(pair.Key) ? entry.MediaPath : pair.Key;
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    if (!merged.Items.TryGetValue(key, out var existing) || entry.SavedAt > existing.SavedAt)
                        merged.Items[key] = entry;
                }
            }

            Merge(primary);
            Merge(legacy);

            if (merged.Items.Count > 0)
            {
                SanitizeEnvelope(merged);
                // Rewrite only when merging the legacy copy actually changed the primary file.
                bool changed = primary?.Items == null || merged.Items.Count != primary.Items.Count ||
                    merged.Items.Any(pair => !primary.Items.TryGetValue(pair.Key, out var existing) || existing.SavedAt != pair.Value.SavedAt);
                if (changed)
                {
                    try { WriteEnvelopeFiles(merged); } catch { }
                }
                return merged;
            }

            if (primary != null)
                return primary;
            if (legacy != null)
                return legacy;

            return new Envelope();
        }

        private static void SaveEnvelope(Envelope env)
        {
            lock (Sync)
            {
                WriteEnvelopeFiles(env);
                _cache = env;
                _cacheStamp = CurrentStamp();
            }
        }

        private static void WriteEnvelopeFiles(Envelope env)
        {
            string? tempFile = null;
            try
            {
                var dir = Path.GetDirectoryName(ResumeFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true });
                tempFile = ResumeFilePath + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(tempFile, json, new UTF8Encoding(false));

                if (File.Exists(ResumeFilePath))
                    File.Replace(tempFile, ResumeFilePath, null, true);
                else
                    File.Move(tempFile, ResumeFilePath);

                try
                {
                    var legacyDir = Path.GetDirectoryName(LegacyResumeFilePath);
                    if (!string.IsNullOrEmpty(legacyDir) && !Directory.Exists(legacyDir))
                        Directory.CreateDirectory(legacyDir);
                    File.WriteAllText(LegacyResumeFilePath, json, new UTF8Encoding(false));
                }
                catch { }
            }
            catch { /* non facciamo crashare il player per problemi IO */ }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempFile))
                {
                    try
                    {
                        if (File.Exists(tempFile))
                            File.Delete(tempFile);
                    }
                    catch { }
                }
            }
        }

        private static void TrimToMaxEntries(Envelope env)
        {
            try
            {
                while (env.Items.Count > MaxEntries)
                {
                    // trova l'entry più vecchia per SavedAt
                    var oldest = env.Items
                        .OrderBy(kvp => kvp.Value.SavedAt)
                        .First();

                    Dbg.Log($"Resume: removed oldest entry '{oldest.Value.DisplayName}' ({oldest.Value.SavedAt}).");
                    env.Items.Remove(oldest.Key);
                }
            }
            catch
            {
                // best-effort, non facciamo crashare nulla se qui fallisce
            }
        }

        /// <summary>
        /// Se la posizione è sensata (non all'inizio e non a fine file) salva;
        /// altrimenti rimuove l'eventuale entry.
        /// </summary>
        // Titoli tolti a mano da "Continua a guardare": restano nascosti finche' non li si guarda di nuovo.
        // Cancellare la voce non bastava: la chiave salvata puo' differire dal percorso (e i titoli di un
        // server tornano dal server), quindi al ricaricamento la riga ricompariva.
        private static Dictionary<string, DateTime>? _hidden;
        private static string HiddenFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "resume-hidden.json");

        private static Dictionary<string, DateTime> Hidden
        {
            get
            {
                if (_hidden != null) return _hidden;
                try
                {
                    if (File.Exists(HiddenFilePath))
                        _hidden = new Dictionary<string, DateTime>(
                            JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(HiddenFilePath)) ?? new(), StringComparer.OrdinalIgnoreCase);
                }
                catch { }
                return _hidden ??= new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>Toglie un titolo da "Continua a guardare" per scelta dell'utente.</summary>
        public static void Hide(string mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath)) return;
            lock (Sync)
            {
                var env = CloneEnvelope(LoadEnvelope());
                var keys = env.Items.Where(pair => string.Equals(pair.Key, mediaPath, StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(pair.Value?.MediaPath, mediaPath, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key).ToList();
                foreach (string key in keys) env.Items.Remove(key);
                if (keys.Count > 0) SaveEnvelope(env);
                Hidden[mediaPath] = DateTime.UtcNow;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(HiddenFilePath)!);
                    File.WriteAllText(HiddenFilePath, JsonSerializer.Serialize(Hidden));
                }
                catch { }
            }
        }

        /// <summary>Vero se il titolo e' stato tolto a mano dopo l'ultima volta che e' stato guardato.</summary>
        public static bool IsHidden(string? mediaPath, DateTime savedAt)
        {
            if (string.IsNullOrWhiteSpace(mediaPath)) return false;
            lock (Sync)
                return Hidden.TryGetValue(mediaPath, out DateTime hiddenAt) && savedAt.ToUniversalTime() <= hiddenAt;
        }

        public static void SaveOrClear(string mediaPath, double positionSeconds, double durationSeconds)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                return;

            lock (Sync)
                SaveOrClearLocked(mediaPath, positionSeconds, durationSeconds);
        }

        private static void SaveOrClearLocked(string mediaPath, double positionSeconds, double durationSeconds)
        {
            var env = CloneEnvelope(LoadEnvelope());
            var remaining = Math.Max(0.0, durationSeconds - positionSeconds);

            Dbg.Log($"Resume: SaveOrClear mediaPath='{mediaPath}', pos={positionSeconds:0.0}s, dur={durationSeconds:0.0}s, remaining={remaining:0.0}s");

            if (!ShouldPersistResumeForPath(mediaPath))
            {
                if (env.Items.Remove(mediaPath))
                    SaveEnvelope(env);
                return;
            }

            // Salva solo se siamo in una posizione realmente riprendibile:
            // né all'inizio né a fine file. Prima a EOF veniva salvata la posizione finale
            // e la coda rientrava sullo stesso elemento invece di avanzare pulita.
            bool completedOrNearEnd = durationSeconds > 0 &&
                (remaining <= 15.0 || positionSeconds >= durationSeconds * 0.985);
            bool shouldClear =
                durationSeconds <= 0 ||
                positionSeconds <= 0 ||
                positionSeconds < MinSecondsForResume ||
                completedOrNearEnd;

            if (shouldClear)
            {
                bool removed = env.Items.Remove(mediaPath);
                if (removed || completedOrNearEnd)
                    Dbg.Log($"Resume: cleared entry for '{mediaPath}'" + (completedOrNearEnd ? " (completed)" : string.Empty));
                SaveEnvelope(env);
                return;
            }

            var entry = new Entry
            {
                MediaPath = mediaPath,
                DisplayName = Path.GetFileName(mediaPath),
                PositionSeconds = positionSeconds,
                PositionMinutes = (int)Math.Round(positionSeconds / 60.0),
                DurationSeconds = durationSeconds,
                SavedAt = DateTime.Now
            };

            env.Items[mediaPath] = entry;
            SanitizeEnvelope(env);
            TrimToMaxEntries(env);

            Dbg.Log($"Resume: saved '{entry.DisplayName}' at {entry.PositionSeconds:0.0}s ({entry.PositionMinutes}m).");
            SaveEnvelope(env);
        }

        public static Entry? Load(string mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath) || !ShouldPersistResumeForPath(mediaPath))
                return null;

            var env = LoadEnvelope();
            env.Items.TryGetValue(mediaPath, out var entry);
            return entry != null && ShouldPersistResumeForPath(entry.MediaPath) ? entry : null;
        }

        public static IReadOnlyCollection<Entry> LoadAll()
        {
            var env = LoadEnvelope();
            return env.Items.Values
                .Where(entry => entry != null && ShouldPersistResumeForPath(entry.MediaPath))
                .OrderByDescending(entry => entry.SavedAt)
                .ToList();
        }
    }

}
