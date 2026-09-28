#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    internal static class MusicArtworkService
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private static readonly SemaphoreSlim Gate = new(2, 2);
        private static readonly ConcurrentDictionary<string, string?> Memory = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object LookupCacheLock = new();
        private static Dictionary<string, ItunesLookupCacheEntry>? LookupCache;

        private sealed class ItunesLookupCacheEntry
        {
            public string? ArtworkUrl { get; set; }
            public long UpdatedUtcTicks { get; set; }
        }

        static MusicArtworkService()
        {
            try
            {
                Http.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer2025/1.0");
            }
            catch { }
        }

        public static string? GetCachedArtworkPath(string? mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                return null;

            try
            {
                var tags = SafeReadTags(mediaPath);
                foreach (var albumKey in BuildAlbumKeys(mediaPath, tags))
                {
                    string albumCache = CachePathFor("album|" + albumKey);
                    if (File.Exists(albumCache))
                        return albumCache;
                }

                string queryKey = BuildQueryKey(mediaPath, tags);
                if (Memory.TryGetValue(queryKey, out var cachedMemory) &&
                    !string.IsNullOrWhiteSpace(cachedMemory) &&
                    File.Exists(cachedMemory))
                {
                    return cachedMemory;
                }

                string cachePath = CachePathFor(queryKey);
                if (File.Exists(cachePath))
                {
                    Memory[queryKey] = cachePath;
                    return cachePath;
                }
            }
            catch { }

            return null;
        }

        public static async Task<string?> ResolveArtworkAsync(string? mediaPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                return null;

            var tags = SafeReadTags(mediaPath);
            string queryKey;
            try { queryKey = BuildQueryKey(mediaPath, tags); }
            catch { queryKey = BuildQueryKey(mediaPath, new MediaProbe.AudioTags()); }

            var albumKeys = BuildAlbumKeys(mediaPath, tags).ToList();
            foreach (var albumKey in albumKeys)
            {
                string albumCache = CachePathFor("album|" + albumKey);
                if (File.Exists(albumCache))
                {
                    Memory[queryKey] = albumCache;
                    return albumCache;
                }
            }

            if (Memory.TryGetValue(queryKey, out var known) &&
                !string.IsNullOrWhiteSpace(known) &&
                File.Exists(known))
            {
                return known;
            }

            string cachePath = CachePathFor(queryKey);
            if (File.Exists(cachePath))
            {
                Memory[queryKey] = cachePath;
                return cachePath;
            }

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var albumKey in albumKeys)
                {
                    string albumCache = CachePathFor("album|" + albumKey);
                    if (File.Exists(albumCache))
                    {
                        Memory[queryKey] = albumCache;
                        return albumCache;
                    }
                }

                if (Memory.TryGetValue(queryKey, out known) &&
                    !string.IsNullOrWhiteSpace(known) &&
                    File.Exists(known))
                {
                    return known;
                }

                if (File.Exists(cachePath))
                {
                    Memory[queryKey] = cachePath;
                    return cachePath;
                }

                if (TryReadCachedItunesLookup(queryKey, out var cachedArtworkUrl))
                {
                    if (string.IsNullOrWhiteSpace(cachedArtworkUrl))
                    {
                        Memory[queryKey] = null;
                        return null;
                    }

                    string? cachedSaved = await DownloadArtworkAsync(cachedArtworkUrl, cachePath, ct).ConfigureAwait(false);
                    Memory[queryKey] = cachedSaved;
                    return cachedSaved;
                }

                var terms = BuildSearchTerms(mediaPath, tags).ToList();
                if (terms.Count == 0)
                {
                    WriteCachedItunesLookup(queryKey, null);
                    Memory[queryKey] = null;
                    return null;
                }

                string? saved = null;
                string? selectedArtworkUrl = null;
                foreach (var term in terms)
                {
                    string? artworkUrl = await SearchItunesArtworkAsync(term, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(artworkUrl))
                        continue;

                    selectedArtworkUrl = artworkUrl;
                    saved = await DownloadArtworkAsync(artworkUrl, cachePath, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(saved))
                        break;
                }

                WriteCachedItunesLookup(queryKey, selectedArtworkUrl);

                if (!string.IsNullOrWhiteSpace(saved))
                {
                    foreach (var albumKey in albumKeys)
                    {
                        try
                        {
                            string albumCache = CachePathFor("album|" + albumKey);
                            if (!File.Exists(albumCache))
                                File.Copy(saved, albumCache, overwrite: false);
                        }
                        catch { }
                    }
                }

                Memory[queryKey] = saved;
                return saved;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                Memory[queryKey] = null;
                return null;
            }
            finally
            {
                try { Gate.Release(); } catch { }
            }
        }

        private static async Task<string?> SearchItunesArtworkAsync(string term, CancellationToken ct)
        {
            foreach (var country in new[] { "IT", "US" })
            {
                foreach (var entity in new[] { "album", "song" })
                {
                    string url = "https://itunes.apple.com/search"
                        + "?media=music&entity=" + Uri.EscapeDataString(entity) + "&limit=8"
                        + "&country=" + Uri.EscapeDataString(country)
                        + "&term=" + Uri.EscapeDataString(term);

                    using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                    if (!doc.RootElement.TryGetProperty("results", out var results) ||
                        results.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    string? best = null;
                    int bestScore = int.MinValue;
                    foreach (var item in results.EnumerateArray())
                    {
                        string? artwork = ReadString(item, "artworkUrl100");
                        if (string.IsNullOrWhiteSpace(artwork))
                            continue;

                        int score = entity == "album" ? 1 : 0;
                        string haystack = string.Join(" ",
                            ReadString(item, "artistName"),
                            ReadString(item, "trackName"),
                            ReadString(item, "collectionName")).ToLowerInvariant();

                        foreach (var token in Tokenize(term))
                            if (haystack.Contains(token))
                                score++;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = PreferLargerArtworkUrl(artwork);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(best))
                        return best;
                }
            }

            return null;
        }

        private static bool TryReadCachedItunesLookup(string queryKey, out string? artworkUrl)
        {
            artworkUrl = null;
            try
            {
                lock (LookupCacheLock)
                {
                    LookupCache ??= LoadLookupCache();
                    if (!LookupCache.TryGetValue(queryKey, out var entry) || entry == null)
                        return false;

                    artworkUrl = entry.ArtworkUrl;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void WriteCachedItunesLookup(string queryKey, string? artworkUrl)
        {
            if (string.IsNullOrWhiteSpace(queryKey))
                return;

            try
            {
                lock (LookupCacheLock)
                {
                    LookupCache ??= LoadLookupCache();
                    LookupCache[queryKey] = new ItunesLookupCacheEntry
                    {
                        ArtworkUrl = string.IsNullOrWhiteSpace(artworkUrl) ? null : artworkUrl,
                        UpdatedUtcTicks = DateTime.UtcNow.Ticks
                    };
                    SaveLookupCache(LookupCache);
                }
            }
            catch { }
        }

        private static Dictionary<string, ItunesLookupCacheEntry> LoadLookupCache()
        {
            try
            {
                string path = LookupCachePath();
                if (!File.Exists(path))
                    return new Dictionary<string, ItunesLookupCacheEntry>(StringComparer.OrdinalIgnoreCase);

                string json = File.ReadAllText(path, Encoding.UTF8);
                var data = JsonSerializer.Deserialize<Dictionary<string, ItunesLookupCacheEntry>>(json);
                return data != null
                    ? new Dictionary<string, ItunesLookupCacheEntry>(data, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, ItunesLookupCacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, ItunesLookupCacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveLookupCache(Dictionary<string, ItunesLookupCacheEntry> data)
        {
            try
            {
                string path = LookupCachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch { }
        }

        private static async Task<string?> DownloadArtworkAsync(string artworkUrl, string cachePath, CancellationToken ct)
        {
            string? tmp = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                tmp = cachePath + ".tmp";

                byte[] data = await Http.GetByteArrayAsync(artworkUrl, ct).ConfigureAwait(false);
                if (data.Length < 128)
                    return null;

                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true))
                {
                    if (img.Width < 24 || img.Height < 24)
                        return null;
                }

                await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
                if (File.Exists(cachePath))
                    File.Delete(cachePath);
                File.Move(tmp, cachePath);
                return cachePath;
            }
            catch
            {
                try { if (!string.IsNullOrWhiteSpace(tmp) && File.Exists(tmp)) File.Delete(tmp); } catch { }
                return null;
            }
        }

        private static IEnumerable<string> BuildSearchTerms(string mediaPath, MediaProbe.AudioTags tags)
        {
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in BuildSearchTermsCore(mediaPath, tags))
            {
                string normalized = Regex.Replace(term ?? string.Empty, @"\s+", " ").Trim();
                if (normalized.Length > 0 && yielded.Add(normalized))
                    yield return normalized;
            }
        }

        private static IEnumerable<string> BuildSearchTermsCore(string mediaPath, MediaProbe.AudioTags tags)
        {
            string artist = FirstNonEmpty(tags.AlbumArtist, tags.Artist);
            string album = CleanFileName(tags.Album);
            string title = CleanFileName(tags.Title);

            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(album))
                yield return $"{artist} {album}";

            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                yield return $"{artist} {title}";

            string name = CleanFileName(Path.GetFileNameWithoutExtension(mediaPath));
            string folder = CleanFileName(Path.GetFileName(Path.GetDirectoryName(mediaPath) ?? string.Empty));
            string parent = CleanFileName(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(mediaPath) ?? string.Empty) ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(parent) && !string.IsNullOrWhiteSpace(folder))
                yield return $"{parent} {folder}";

            if (!string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(name))
                yield return $"{folder} {name}";

            if (!string.IsNullOrWhiteSpace(name))
                yield return name;
        }

        private static string BuildQueryKey(string mediaPath, MediaProbe.AudioTags tags)
        {
            string term = BuildSearchTerms(mediaPath, tags).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                ?? CleanFileName(Path.GetFileNameWithoutExtension(mediaPath));
            if (string.IsNullOrWhiteSpace(term))
                term = mediaPath;

            try
            {
                var fi = new FileInfo(mediaPath);
                return $"{term}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}".ToLowerInvariant();
            }
            catch
            {
                return $"{term}|{mediaPath}".ToLowerInvariant();
            }
        }

        private static IEnumerable<string> BuildAlbumKeys(string mediaPath, MediaProbe.AudioTags tags)
        {
            string album = CleanMusicTitle(tags.Album);
            string artist = CleanMusicTitle(FirstNonEmpty(tags.AlbumArtist, tags.Artist));
            if (!string.IsNullOrWhiteSpace(album))
            {
                yield return (artist + "|" + album).Trim('|').ToLowerInvariant();
            }

            string folder = CleanFileName(Path.GetFileName(Path.GetDirectoryName(mediaPath) ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                string parent = CleanFileName(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(mediaPath) ?? string.Empty) ?? string.Empty));
                yield return (parent + "|" + folder).Trim('|').ToLowerInvariant();
            }
        }

        private static MediaProbe.AudioTags SafeReadTags(string mediaPath)
        {
            try { return MediaProbe.ReadAudioTags(mediaPath); }
            catch { return new MediaProbe.AudioTags(); }
        }

        private static string CachePathFor(string queryKey)
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025",
                "music-artwork");
            return Path.Combine(folder, Sha256(queryKey) + ".jpg");
        }

        private static string LookupCachePath()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025",
                "music-artwork");
            return Path.Combine(folder, "itunes-lookup-cache.json");
        }

        private static string Sha256(string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string? ReadString(JsonElement item, string name)
        {
            try
            {
                if (item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                    return prop.GetString();
            }
            catch { }
            return null;
        }

        private static string PreferLargerArtworkUrl(string url)
        {
            try
            {
                string larger = Regex.Replace(url, @"\d+x\d+bb", "600x600bb", RegexOptions.IgnoreCase);
                larger = Regex.Replace(larger, @"\d+x\d+-\d+", "600x600-100", RegexOptions.IgnoreCase);
                return larger;
            }
            catch { return url; }
        }

        private static string FirstNonEmpty(params string[] values)
            => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

        private static string CleanFileName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string s = CleanMusicTitle(value);
            s = Regex.Replace(s, @"\[[^\]]+\]|\([^\)]*(official|lyrics?|audio|video|remaster|remastered|explicit|clean)[^\)]*\)", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"^\s*\d{1,3}\s*[-._ ]+", " ");
            s = Regex.Replace(s, @"[_\.]+", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        private static string CleanMusicTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string s = Regex.Replace(value.Trim(), @"\s+", " ");
            var plus = Regex.Match(s, @"\s*\+\s*");
            if (plus.Success)
                s = s.Substring(0, plus.Index).Trim();
            return s;
        }

        private static string[] Tokenize(string value)
            => Regex.Split((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+")
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray();
    }
}
