#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    // Separate from video resume: completed songs must remain in album progress.
    internal static class MusicListeningStore
    {
        internal sealed record Progress(double Position, double Duration, DateTime UpdatedUtc);
        private static readonly object Sync = new();
        private static Dictionary<string, Progress>? _items;
        private static DateTime _lastWrite;
        private static Task _write = Task.CompletedTask;
        private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "music-listening.json");
        private static Dictionary<string, Progress> Items
        {
            get
            {
                if (_items != null) return _items;
                try { _items = JsonSerializer.Deserialize<Dictionary<string, Progress>>(File.ReadAllText(StorePath)); } catch { }
                return _items = new Dictionary<string, Progress>(_items ?? new(), StringComparer.OrdinalIgnoreCase);
            }
        }
        public static Dictionary<string, Progress> Snapshot()
        {
            lock (Sync) return new(Items, StringComparer.OrdinalIgnoreCase);
        }
        public static void Record(string path, double position, double duration)
        {
            if (string.IsNullOrWhiteSpace(path) || !double.IsFinite(duration) || !double.IsFinite(position) || duration <= 0 || position < 1) return;
            if (Path.GetFullPath(path).StartsWith(Path.Combine(AppContext.BaseDirectory, "Assets") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            lock (Sync)
            {
                bool completed = position >= duration - Math.Min(3, duration * .02);
                if (Items.TryGetValue(path, out var previous) && previous.Position >= previous.Duration * .98)
                    position = Math.Max(position, previous.Position);
                Items[path] = new(completed ? duration : Math.Clamp(position, 0, duration), duration, DateTime.UtcNow);
                if (!completed && DateTime.UtcNow - _lastWrite < TimeSpan.FromSeconds(5)) return;
                _lastWrite = DateTime.UtcNow;
                var snapshot = Items.OrderByDescending(p => p.Value.UpdatedUtc).Take(20000).ToDictionary(p => p.Key, p => p.Value);
                // Serialize writes in order so an older worker cannot replace new progress.
                _write = _write.ContinueWith(_ =>
                {
                    try
                    {
                        string path = StorePath;
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot));
                        File.Move(path + ".tmp", path, true);
                    }
                    catch { }
                }, TaskScheduler.Default);
            }
        }
    }
}
