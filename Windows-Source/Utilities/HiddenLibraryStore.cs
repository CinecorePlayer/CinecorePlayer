#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Titoli nascosti dalla libreria dal menu del tasto destro. Il file resta sul disco: si nasconde soltanto.
    /// Ogni voce ricorda il titolo mostrato, cosi' l'elenco "Elementi nascosti" e' leggibile.
    /// </summary>
    internal static class HiddenLibraryStore
    {
        private static readonly object Gate = new();
        private static Dictionary<string, string>? _entries;
        private static string StorePath => Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "hidden-library.json");

        public static event Action? Changed;

        private static Dictionary<string, string> Entries()
        {
            if (_entries != null) return _entries;
            try
            {
                if (File.Exists(StorePath))
                    _entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath));
            }
            catch { }
            _entries = new Dictionary<string, string>(_entries ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            return _entries;
        }

        public static bool Contains(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            lock (Gate) { var e = Entries(); return e.Count > 0 && e.ContainsKey(path); }
        }

        public static int Count { get { lock (Gate) return Entries().Count; } }

        /// <summary>Titoli nascosti raggruppati per nome (una serie conta una volta).</summary>
        public static List<(string Title, List<string> Paths)> Groups()
        {
            lock (Gate)
                return Entries().GroupBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => (g.Key, g.Select(kv => kv.Key).ToList())).ToList();
        }

        public static void Hide(IEnumerable<string> paths, string title)
        {
            lock (Gate)
            {
                var e = Entries();
                foreach (var p in paths.Where(p => !string.IsNullOrWhiteSpace(p))) e[p] = title;
                Save(e);
            }
            try { Changed?.Invoke(); } catch { }
        }

        public static void Show(IEnumerable<string> paths)
        {
            lock (Gate)
            {
                var e = Entries();
                foreach (var p in paths) e.Remove(p);
                Save(e);
            }
            try { Changed?.Invoke(); } catch { }
        }

        private static void Save(Dictionary<string, string> e)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                string tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(e));
                File.Move(tmp, StorePath, overwrite: true);
            }
            catch { }
        }
    }
}
