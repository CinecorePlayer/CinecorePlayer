#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CinecorePlayer2025
{
    internal static class PlaybackTitleHints
    {
        private sealed class HintEntry
        {
            public string? Title { get; set; }
            public string? Category { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public DateTime SavedAtUtc { get; set; }
        }

        private static readonly object Sync = new();
        private static readonly Dictionary<string, HintEntry> Items = new(StringComparer.OrdinalIgnoreCase);

        public static void Set(string? path, string? title = null, string? category = null, int width = 0, int height = 0)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string key = path.Trim();
            lock (Sync)
            {
                if (!Items.TryGetValue(key, out HintEntry? entry))
                {
                    entry = new HintEntry();
                    Items[key] = entry;
                }

                if (!string.IsNullOrWhiteSpace(title))
                    entry.Title = title.Trim();
                if (!string.IsNullOrWhiteSpace(category))
                    entry.Category = category.Trim();
                if (width > 0)
                    entry.Width = width;
                if (height > 0)
                    entry.Height = height;
                entry.SavedAtUtc = DateTime.UtcNow;

                if (Items.Count > 4096)
                {
                    foreach (string staleKey in Items
                        .OrderBy(pair => pair.Value.SavedAtUtc)
                        .Take(Math.Max(1, Items.Count - 3072))
                        .Select(pair => pair.Key)
                        .ToList())
                    {
                        Items.Remove(staleKey);
                    }
                }
            }
        }

        public static string? GetTitle(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            lock (Sync)
                return Items.TryGetValue(path.Trim(), out HintEntry? entry) ? entry.Title : null;
        }

        public static string? GetCategory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            lock (Sync)
                return Items.TryGetValue(path.Trim(), out HintEntry? entry) ? entry.Category : null;
        }

        public static (int Width, int Height) GetDimensions(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return (0, 0);

            lock (Sync)
            {
                return Items.TryGetValue(path.Trim(), out HintEntry? entry)
                    ? (entry.Width, entry.Height)
                    : (0, 0);
            }
        }
    }

    internal sealed class PlaybackQueueStateStore
    {
        private static string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CinecorePlayer2025",
            "playback-queue.json");

        public (List<string> Items, int CurrentIndex, bool SessionActive, bool ShuffleMode) Load()
        {
            DeletePersistedQueue();
            return (new List<string>(), -1, false, false);
        }

        public void Save(IEnumerable<string>? items, int currentIndex, bool sessionActive, bool shuffleMode)
        {
            DeletePersistedQueue();
        }

        private static void DeletePersistedQueue()
        {
            try
            {
                if (File.Exists(QueueFilePath))
                    File.Delete(QueueFilePath);
            }
            catch { }
        }
    }
}
