#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>Volume scelto dall'utente per ogni film. Il livello del player (0..1) e quello
    /// dell'amplificatore di rete (0..1 sulla sua scala) sono tenuti separati: non sono confrontabili.</summary>
    internal static class FilmVolumeStore
    {
        public sealed class Entry
        {
            public float? Player { get; set; }
            public float? Amplifier { get; set; }
            public DateTime Updated { get; set; }
        }

        private const int MaxEntries = 3000;
        private static readonly object Gate = new();
        private static Dictionary<string, Entry>? _entries;
        private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "film-volume.json");

        private static Dictionary<string, Entry> Entries()
        {
            if (_entries != null) return _entries;
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(StorePath));
                if (loaded != null) return _entries = new Dictionary<string, Entry>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }

        public static (float? Player, float? Amplifier) Get(string path)
        {
            lock (Gate)
                return Entries().TryGetValue(path, out var entry) ? (Valid(entry.Player), Valid(entry.Amplifier)) : (null, null);
        }

        private static float? Valid(float? level) => level is float value && float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : null;

        /// <summary>Salva un livello; null lo cancella (il film torna al comportamento normale).</summary>
        public static void Set(string path, bool amplifier, float? level)
        {
            lock (Gate)
            {
                var entries = Entries();
                entries.TryGetValue(path, out var entry);
                entry ??= new Entry();
                if (amplifier) entry.Amplifier = Valid(level); else entry.Player = Valid(level);
                entry.Updated = DateTime.UtcNow;
                if (entry.Player == null && entry.Amplifier == null) entries.Remove(path);
                else entries[path] = entry;
                if (entries.Count > MaxEntries)
                    foreach (string old in entries.OrderBy(pair => pair.Value.Updated).Take(entries.Count - MaxEntries).Select(pair => pair.Key).ToArray())
                        entries.Remove(old);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                    string temp = StorePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(entries));
                    File.Move(temp, StorePath, true);
                }
                catch (Exception ex) { Dbg.Warn("[VOLUME] save failed: " + ex.Message); }
            }
        }
    }
}
