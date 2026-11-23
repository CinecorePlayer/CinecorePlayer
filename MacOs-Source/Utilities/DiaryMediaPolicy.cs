#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CinecorePlayer2025.Utilities;

// The diary follows content identity, never merely the presence of a video stream.
internal static class DiaryMediaPolicy
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> RecordedCategories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Roots = new(StringComparer.OrdinalIgnoreCase);
    private static long _indexStamp = -1;
    private static long _historyStamp = -1;
    private static long _rootsStamp = -1;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m2ts", ".mts", ".m2v", ".ts", ".mpg", ".mpeg", ".vob", ".3gp", ".ogv", ".iso", ".cinedisc" };

    internal static string? IndexedCategory(string path)
    {
        lock (Sync)
        {
            string index = Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "cinematicLibraryIndex.json");
            try
            {
                long stamp = File.GetLastWriteTimeUtc(index).Ticks;
                if (stamp != _indexStamp)
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(index));
                    var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var category in json.RootElement.GetProperty("Categories").EnumerateObject())
                        foreach (var entry in category.Value.EnumerateArray())
                            if (entry.GetString() is { Length: > 0 } key)
                                updated[key] = category.Name;
                    Categories.Clear();
                    foreach (var pair in updated) Categories[pair.Key] = pair.Value;
                    _indexStamp = stamp;
                }
            }
            catch { /* Keep the last valid index during an atomic rewrite. */ }
            return Categories.TryGetValue(path, out string? value) ? value : null;
        }
    }

    private static string? RecordedCategory(string path)
    {
        lock (Sync)
        {
            try
            {
                string history = Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "watchHistory.json");
                long stamp = File.GetLastWriteTimeUtc(history).Ticks;
                if (stamp != _historyStamp)
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(history));
                    var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in json.RootElement.GetProperty("Items").EnumerateObject())
                        if (item.Value.TryGetProperty("MediaCategory", out var category) && category.GetString() is { Length: > 0 } value)
                            updated[item.Name] = value;
                    RecordedCategories.Clear();
                    foreach (var pair in updated) RecordedCategories[pair.Key] = pair.Value;
                    _historyStamp = stamp;
                }
            }
            catch { }
            return RecordedCategories.TryGetValue(path, out var found) ? found : null;
        }
    }

    private static string? RootCategory(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) return null;
        lock (Sync)
        {
            try
            {
                string file = Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "cinematicLibraryRoots.json");
                long stamp = File.GetLastWriteTimeUtc(file).Ticks;
                if (stamp != _rootsStamp)
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(file));
                    var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var category in json.RootElement.GetProperty("Roots").EnumerateObject())
                        foreach (var root in category.Value.EnumerateArray())
                            if (root.GetString() is { Length: > 0 } value)
                                updated[Path.GetFullPath(value).TrimEnd('\\', '/') + Path.DirectorySeparatorChar] = category.Name;
                    Roots.Clear();
                    foreach (var pair in updated) Roots[pair.Key] = pair.Value;
                    _rootsStamp = stamp;
                }
                string full = Path.GetFullPath(uri?.IsFile == true ? uri.LocalPath : path);
                string? found = null;
                int longest = 0;
                foreach (var root in Roots)
                    if (root.Key.Length > longest && full.StartsWith(root.Key, StringComparison.OrdinalIgnoreCase))
                    { found = root.Value; longest = root.Key.Length; }
                // A TV root can also contain unrelated videos: require an episode identity.
                if (found is "TV Series" or "Serie")
                    return MovieMetadataService.ExtractMediaTitleInfoFromPath(path).IsTvEpisode ? found : null;
                return found;
            }
            catch { return null; }
        }
    }

    internal static bool IsCinemaCategory(string? category) => category?.Trim().ToLowerInvariant() is "film" or "movies" or "movie" or "serie" or "series" or "tv series" or "serie tv";

    internal static bool IsEligible(string? path, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string probe = path;
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase)) return false;
                probe = uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath);
            }
            string extension = Path.GetExtension(probe);
            if (!VideoExtensions.Contains(extension) && (extension.Length > 0 ||
                !IsCinemaCategory(category ?? PlaybackTitleHints.GetCategory(path) ?? RecordedCategory(path) ?? JellyfinClient.RememberedTitle(path)?.Category))) return false;
            string? indexed = IndexedCategory(path) ?? RootCategory(path);
            // Explicit Video/Music/Photo membership wins over inferred metadata.
            if (indexed != null) return IsCinemaCategory(indexed);
            string? hint = category ?? PlaybackTitleHints.GetCategory(path) ?? RecordedCategory(path);
            if (hint != null) return IsCinemaCategory(hint);
            var remembered = JellyfinClient.RememberedTitle(path);
            if (remembered.HasValue) return IsCinemaCategory(remembered.Value.Category);
            // Legacy history and films opened outside the library need positive evidence.
            return MovieMetadataService.ExtractMediaTitleInfoFromPath(path).IsTvEpisode ||
                MovieMetadataService.IsCachedTitleResolved(path);
        }
        catch { return false; }
    }
}
