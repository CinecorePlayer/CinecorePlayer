#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CinecorePlayer2025.Engines
{
    internal static class WatchHistoryStore
    {
        public sealed class Entry
        {
            public string MediaPath { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public DateTime FirstWatchedAtUtc { get; set; }
            public DateTime LastWatchedAtUtc { get; set; }
            public DateTime LastSessionStartedAtUtc { get; set; }
            public double PositionSeconds { get; set; }
            public double DurationSeconds { get; set; }
            public int PlayCount { get; set; }
            public bool Completed { get; set; }
            public double Rating { get; set; }
        }

        private sealed class Envelope
        {
            public Dictionary<string, Entry> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly object Sync = new();
        private static readonly Dictionary<string, DateTime> LastProgressWrite = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxEntries = 500;
        private const double WatchedThreshold = 0.90;
        private static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CinecorePlayer2025", "watchHistory.json");

        public static long StoreVersionTicks
        {
            get
            {
                try { return File.Exists(StorePath) ? File.GetLastWriteTimeUtc(StorePath).Ticks : 0; }
                catch { return 0; }
            }
        }

        public static IReadOnlyList<Entry> LoadAll()
        {
            lock (Sync)
            {
                return LoadEnvelope().Items.Values
                    .Where(entry => entry != null && entry.Completed && !string.IsNullOrWhiteSpace(entry.MediaPath))
                    .Where(entry => !IsYouTubePath(entry.MediaPath))
                    .OrderByDescending(entry => entry.LastWatchedAtUtc)
                    .Select(Clone)
                    .ToList();
            }
        }

        public static void RecordStarted(string mediaPath, string? displayName)
        {
            if (!ShouldTrack(mediaPath)) return;
            lock (Sync)
            {
                Envelope env = LoadEnvelope();
                Entry entry = GetOrCreate(env, mediaPath, displayName);
                DateTime now = DateTime.UtcNow;
                if (entry.LastSessionStartedAtUtc == DateTime.MinValue || now - entry.LastSessionStartedAtUtc > TimeSpan.FromMinutes(2))
                    entry.PlayCount = Math.Max(0, entry.PlayCount) + 1;
                entry.LastSessionStartedAtUtc = now;
                entry.LastWatchedAtUtc = now;
                if (entry.FirstWatchedAtUtc == DateTime.MinValue) entry.FirstWatchedAtUtc = now;
                SaveEnvelope(env);
            }
        }

        public static void RecordProgress(string mediaPath, string? displayName, double positionSeconds, double durationSeconds, bool force = false)
        {
            if (!ShouldTrack(mediaPath) || positionSeconds < 1) return;
            DateTime now = DateTime.UtcNow;
            lock (Sync)
            {
                bool reachedWatchedThreshold = durationSeconds > 1 &&
                    positionSeconds >= durationSeconds * WatchedThreshold;
                if (!force && !reachedWatchedThreshold &&
                    LastProgressWrite.TryGetValue(mediaPath, out DateTime last) &&
                    now - last < TimeSpan.FromSeconds(20))
                    return;
                LastProgressWrite[mediaPath] = now;
                Envelope env = LoadEnvelope();
                Entry entry = GetOrCreate(env, mediaPath, displayName);
                entry.PositionSeconds = Math.Max(0, positionSeconds);
                entry.DurationSeconds = Math.Max(entry.DurationSeconds, durationSeconds);
                if (reachedWatchedThreshold)
                {
                    entry.Completed = true;
                    entry.PositionSeconds = Math.Max(entry.PositionSeconds, entry.DurationSeconds);
                }
                entry.LastWatchedAtUtc = now;
                if (entry.FirstWatchedAtUtc == DateTime.MinValue) entry.FirstWatchedAtUtc = now;
                SaveEnvelope(env);
            }
        }

        public static void RecordCompleted(string mediaPath, string? displayName, double durationSeconds)
        {
            if (!ShouldTrack(mediaPath)) return;
            lock (Sync)
            {
                Envelope env = LoadEnvelope();
                Entry entry = GetOrCreate(env, mediaPath, displayName);
                entry.DurationSeconds = Math.Max(entry.DurationSeconds, durationSeconds);
                entry.PositionSeconds = entry.DurationSeconds;
                entry.Completed = true;
                entry.LastWatchedAtUtc = DateTime.UtcNow;
                if (entry.FirstWatchedAtUtc == DateTime.MinValue) entry.FirstWatchedAtUtc = entry.LastWatchedAtUtc;
                LastProgressWrite.Remove(mediaPath);
                SaveEnvelope(env);
            }
        }

        public static void SetRating(string mediaPath, double rating)
        {
            if (string.IsNullOrWhiteSpace(mediaPath)) return;
            lock (Sync)
            {
                Envelope env = LoadEnvelope();
                Entry entry = GetOrCreate(env, mediaPath, null);
                entry.Rating = Math.Round(Math.Clamp(rating, 0, 5) * 2, MidpointRounding.AwayFromZero) / 2.0;
                SaveEnvelope(env);
            }
        }

        private static bool ShouldTrack(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                if (File.Exists(path))
                {
                    string full = Path.GetFullPath(path);
                    string assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets"));
                    return !full.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                }
                return Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) &&
                       (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                       !IsYouTubeUri(uri);
            }
            catch { return false; }
        }

        private static bool IsYouTubePath(string? path)
        {
            try { return Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && IsYouTubeUri(uri); }
            catch { return false; }
        }

        private static bool IsYouTubeUri(Uri uri)
        {
            string host = uri.Host ?? string.Empty;
            return host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                   host.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase);
        }

        private static Entry GetOrCreate(Envelope env, string path, string? displayName)
        {
            if (!env.Items.TryGetValue(path, out Entry? entry) || entry == null)
            {
                entry = new Entry { MediaPath = path };
                env.Items[path] = entry;
            }
            if (!string.IsNullOrWhiteSpace(displayName)) entry.DisplayName = displayName.Trim();
            if (string.IsNullOrWhiteSpace(entry.DisplayName)) entry.DisplayName = Path.GetFileNameWithoutExtension(path) ?? path;
            return entry;
        }

        private static Envelope LoadEnvelope()
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    Envelope env = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(StorePath)) ?? new Envelope();
                    env.Items = new Dictionary<string, Entry>(env.Items ?? new(), StringComparer.OrdinalIgnoreCase);
                    return env;
                }
            }
            catch { }
            return new Envelope();
        }

        private static void SaveEnvelope(Envelope env)
        {
            string? temporary = null;
            try
            {
                while (env.Items.Count > MaxEntries)
                {
                    string oldest = env.Items.OrderBy(pair => pair.Value.LastWatchedAtUtc).First().Key;
                    env.Items.Remove(oldest);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                temporary = StorePath + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporary, JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                if (File.Exists(StorePath)) File.Replace(temporary, StorePath, null, true);
                else File.Move(temporary, StorePath);
            }
            catch { }
            finally
            {
                try { if (!string.IsNullOrWhiteSpace(temporary) && File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static Entry Clone(Entry entry) => new()
        {
            MediaPath = entry.MediaPath,
            DisplayName = entry.DisplayName,
            FirstWatchedAtUtc = entry.FirstWatchedAtUtc,
            LastWatchedAtUtc = entry.LastWatchedAtUtc,
            LastSessionStartedAtUtc = entry.LastSessionStartedAtUtc,
            PositionSeconds = entry.PositionSeconds,
            DurationSeconds = entry.DurationSeconds,
            PlayCount = entry.PlayCount,
            Completed = entry.Completed,
            Rating = Math.Clamp(entry.Rating, 0, 5)
        };
    }
}
