#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Store JSON per le Playlist dell'utente.
    /// 
    /// Requisiti:
    /// - Playlist solo per: Video, Musica, Foto
    /// - Ogni playlist è una lista ordinata di path locali.
    /// </summary>
    internal sealed class PlaylistsStore
    {
        private sealed class Envelope
        {
            // category -> (playlistName -> items)
            public Dictionary<string, Dictionary<string, List<string>>> Categories { get; set; }
                = new(StringComparer.OrdinalIgnoreCase);
        }

        private readonly object _lock = new();
        private Envelope _env;

        private static string StorePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CinecorePlayer2025",
                "playlists.json");

        public PlaylistsStore()
        {
            _env = Load();
            EnsureCategory("Video");
            EnsureCategory("Musica");
            EnsureCategory("Foto");
        }

        private static Envelope Load()
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    var json = File.ReadAllText(StorePath);
                    return JsonSerializer.Deserialize<Envelope>(json) ?? new Envelope();
                }
            }
            catch
            {
                // best effort
            }
            return new Envelope();
        }

        private void SaveNoLock()
        {
            try
            {
                var dir = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_env, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StorePath, json);
            }
            catch
            {
                // best effort
            }
        }

        private Dictionary<string, List<string>> EnsureCategory(string category)
        {
            if (!_env.Categories.TryGetValue(category, out var cat))
            {
                cat = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                _env.Categories[category] = cat;
            }
            return cat;
        }

        public IReadOnlyList<string> GetPlaylistNames(string category)
        {
            lock (_lock)
            {
                var cat = EnsureCategory(category);
                return cat.Keys
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public int CountPlaylists(string category)
        {
            lock (_lock)
            {
                var cat = EnsureCategory(category);
                return cat.Count;
            }
        }

        public int CountItems(string category, string playlistName)
        {
            lock (_lock)
            {
                var cat = EnsureCategory(category);
                if (!cat.TryGetValue(playlistName, out var list)) return 0;
                return list.Count;
            }
        }

        public bool ContainsPlaylist(string category, string playlistName)
        {
            lock (_lock)
            {
                var cat = EnsureCategory(category);
                return cat.ContainsKey(playlistName);
            }
        }

        public bool EnsurePlaylist(string category, string playlistName)
        {
            if (string.IsNullOrWhiteSpace(playlistName))
                return false;

            lock (_lock)
            {
                var cat = EnsureCategory(category);
                if (cat.ContainsKey(playlistName))
                    return false;

                cat[playlistName] = new List<string>();
                SaveNoLock();
                return true;
            }
        }

        public bool DeletePlaylist(string category, string playlistName)
        {
            lock (_lock)
            {
                var cat = EnsureCategory(category);
                if (!cat.Remove(playlistName))
                    return false;
                SaveNoLock();
                return true;
            }
        }

        public bool RenamePlaylist(string category, string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
                return false;

            lock (_lock)
            {
                var cat = EnsureCategory(category);
                if (!cat.TryGetValue(oldName, out var items))
                    return false;
                if (cat.ContainsKey(newName))
                    return false;

                cat.Remove(oldName);
                cat[newName] = items;
                SaveNoLock();
                return true;
            }
        }

        public IReadOnlyList<string> GetItems(string category, string playlistName)
        {
            lock (_lock)
            {
                var cat = EnsureCategory(category);
                if (!cat.TryGetValue(playlistName, out var list))
                    return Array.Empty<string>();
                return list.ToList();
            }
        }

        public bool AddItem(string category, string playlistName, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(playlistName))
                return false;

            lock (_lock)
            {
                var cat = EnsureCategory(category);
                if (!cat.TryGetValue(playlistName, out var list))
                {
                    list = new List<string>();
                    cat[playlistName] = list;
                }

                // non duplicare
                if (list.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                    return false;

                list.Add(path);
                SaveNoLock();
                return true;
            }
        }

        public bool RemoveItem(string category, string playlistName, string path)
        {
            lock (_lock)
            {
                var cat = EnsureCategory(category);
                if (!cat.TryGetValue(playlistName, out var list))
                    return false;

                int idx = list.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return false;
                list.RemoveAt(idx);
                SaveNoLock();
                return true;
            }
        }

        /// <summary>
        /// Rimuove dai contenuti della playlist i file che non esistono più su disco.
        /// Ritorna quante entry sono state rimosse.
        /// </summary>
        public int PruneMissing(string category, string playlistName)
        {
            lock (_lock)
            {
                var cat = EnsureCategory(category);
                if (!cat.TryGetValue(playlistName, out var list))
                    return 0;

                int before = list.Count;
                list.RemoveAll(p => string.IsNullOrWhiteSpace(p) || !File.Exists(p));
                int removed = before - list.Count;
                if (removed > 0) SaveNoLock();
                return removed;
            }
        }
    }
}
