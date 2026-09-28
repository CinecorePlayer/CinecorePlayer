#nullable enable
using System;
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
    internal static class MusicArtistArtworkService
    {
        internal sealed record Artwork(string? Portrait, string? Landscape, string Source = "AudioDB", string? ArtistUrl = null);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly ConcurrentDictionary<string, Lazy<Task<Artwork>>> Requests = new();
        private sealed record CachedArtwork(Artwork Value, DateTime Expires);
        private static readonly ConcurrentDictionary<string, CachedArtwork> Results = new();
        static MusicArtistArtworkService() => Http.DefaultRequestHeaders.UserAgent.TryParseAdd("CinecorePlayer2025/1.0");
        internal static void ResetProviderCache()
        {
            Results.Clear();
            Requests.Clear();
        }
        public static string Identity(string? value) => Regex.Replace((value ?? "").Normalize(NormalizationForm.FormD).ToUpperInvariant(), @"[^\p{L}\p{N}]", "");
        public static Task<Artwork> ResolveAsync(string artist, string? album = null)
        {
            string key = Identity(artist) + "|" + Identity(album);
            if (Results.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow &&
                (cached.Value.Portrait == null || File.Exists(cached.Value.Portrait)) &&
                (cached.Value.Landscape == null || File.Exists(cached.Value.Landscape))) return Task.FromResult(cached.Value);
            return Requests.GetOrAdd(key, _ => new Lazy<Task<Artwork>>(() => ResolveAndCacheAsync(key, artist, album))).Value;
        }
        private static async Task<Artwork> ResolveAndCacheAsync(string key, string artist, string? album)
        {
            try
            {
                var result = await ResolveCoreAsync(artist, album).ConfigureAwait(false);
                Results[key] = new CachedArtwork(result, DateTime.UtcNow.Add(result.Landscape == null ? TimeSpan.FromMinutes(15) : TimeSpan.FromDays(7)));
                return result;
            }
            finally { Requests.TryRemove(key, out _); }
        }
        private static async Task<Artwork> ResolveCoreAsync(string artist, string? album, string? cacheDirectory = null)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            var spotify = new SpotifyArtistArtworkService.ArtistImage(null, null, null);
            try
            {
                string directory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "ArtistArtwork");
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Identity(artist))));
                string manifest = Path.Combine(directory, key + ".json");
                if (File.Exists(manifest) && DateTime.UtcNow - File.GetLastWriteTimeUtc(manifest) < TimeSpan.FromDays(7))
                {
                    var cached = JsonSerializer.Deserialize<Artwork>(await File.ReadAllTextAsync(manifest).ConfigureAwait(false));
                    if (cached != null && (!SpotifyArtistArtworkService.IsConfigured || cached.Source is "Spotify" or "SpotifyChecked") &&
                        (cached.Portrait == null || File.Exists(cached.Portrait)) &&
                        (cached.Landscape == null || File.Exists(cached.Landscape)) &&
                        (cached.Landscape != null || DateTime.UtcNow - File.GetLastWriteTimeUtc(manifest) < TimeSpan.FromMinutes(15))) return cached;
                }
                spotify = await SpotifyArtistArtworkService.ResolveAsync(artist, directory).ConfigureAwait(false);
                if (spotify.Portrait != null && spotify.Landscape != null)
                {
                    var complete = new Artwork(spotify.Portrait, spotify.Landscape, "Spotify", spotify.ArtistUrl);
                    Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(complete)).ConfigureAwait(false);
                    return complete;
                }
                // Public artist metadata API; only the artist name is sent, never a local path.
                string json = await Http.GetStringAsync("https://www.theaudiodb.com/api/v1/json/123/search.php?s=" + Uri.EscapeDataString(artist)).ConfigureAwait(false);
                using (var search = JsonDocument.Parse(json))
                {
                    bool exact = search.RootElement.TryGetProperty("artists", out var matches) && matches.ValueKind == JsonValueKind.Array &&
                        matches.EnumerateArray().Any(a => a.TryGetProperty("strArtist", out var name) && Identity(name.GetString()) == Identity(artist));
                    // The free search returns only one artist. Disambiguate names through
                    // the album instead of accepting a different artist's photograph.
                    if (!exact && !string.IsNullOrWhiteSpace(album))
                    {
                        await Task.Delay(2100).ConfigureAwait(false);
                        string albumsJson = await Http.GetStringAsync("https://www.theaudiodb.com/api/v1/json/123/searchalbum.php?s=" + Uri.EscapeDataString(artist) + "&a=" + Uri.EscapeDataString(album)).ConfigureAwait(false);
                        using var albumDoc = JsonDocument.Parse(albumsJson);
                        if (albumDoc.RootElement.TryGetProperty("album", out var albums) && albums.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var match in albums.EnumerateArray())
                            {
                                if (!match.TryGetProperty("strArtist", out var name) || Identity(name.GetString()) != Identity(artist) ||
                                    !match.TryGetProperty("strAlbum", out var albumName) || Identity(albumName.GetString()) != Identity(album) ||
                                    !match.TryGetProperty("idArtist", out var id) || !long.TryParse(id.GetString(), out long artistId)) continue;
                                await Task.Delay(2100).ConfigureAwait(false);
                                json = await Http.GetStringAsync("https://www.theaudiodb.com/api/v1/json/123/artist.php?i=" + artistId).ConfigureAwait(false);
                                break;
                            }
                        }
                    }
                }
                using var doc = JsonDocument.Parse(json);
                var result = new Artwork(null, null);
                if (doc.RootElement.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in artists.EnumerateArray())
                    {
                        string? name = item.TryGetProperty("strArtist", out var n) ? n.GetString() : null;
                        if (Identity(name) != Identity(artist)) continue;
                        Directory.CreateDirectory(directory);
                        async Task<string?> Fetch(string field, bool landscape)
                        {
                            try
                            {
                                if (!item.TryGetProperty(field, out var entry) || !Uri.TryCreate(entry.GetString(), UriKind.Absolute, out var url) || url.Scheme != "https" || !(url.Host == "theaudiodb.com" || url.Host.EndsWith(".theaudiodb.com", StringComparison.OrdinalIgnoreCase))) return null;
                                byte[] data = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                                if (data.Length > 16 * 1024 * 1024) return null;
                                using var stream = new MemoryStream(data); using var image = Image.FromStream(stream);
                                if (landscape && !MusicLandscapeArtworkService.IsLandscape(image.Width, image.Height)) return null;
                                string path = Path.Combine(directory, key + (landscape ? "-wide.jpg" : "-portrait.jpg"));
                                await File.WriteAllBytesAsync(path, data).ConfigureAwait(false); return path;
                            }
                            catch { return null; }
                        }
                        Task<string?> portraitTask = Fetch("strArtistThumb", false);
                        async Task<string?> FindLandscapeAsync()
                        {
                            foreach (string field in new[] { "strArtistFanart", "strArtistFanart2", "strArtistFanart3", "strArtistFanart4", "strArtistWideThumb" })
                            {
                                string? found = await Fetch(field, true).ConfigureAwait(false);
                                if (found != null) return found;
                            }
                            return null;
                        }
                        Task<string?> landscapeTask = FindLandscapeAsync();
                        await Task.WhenAll(portraitTask, landscapeTask).ConfigureAwait(false);
                        string? portrait = portraitTask.Result;
                        string? landscape = landscapeTask.Result;
                        result = new Artwork(spotify.Portrait ?? portrait, spotify.Landscape ?? landscape,
                            spotify.Portrait != null ? "Spotify" : SpotifyArtistArtworkService.IsConfigured ? "SpotifyChecked" : "AudioDB", spotify.ArtistUrl);
                        break;
                    }
                }
                if (result.Portrait == null && spotify.Portrait != null)
                    result = new Artwork(spotify.Portrait, spotify.Landscape, "Spotify", spotify.ArtistUrl);
                else if (SpotifyArtistArtworkService.IsConfigured && result.Source == "AudioDB")
                    result = result with { Source = "SpotifyChecked" };
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(result)).ConfigureAwait(false);
                return result;
            }
            catch { return new Artwork(spotify.Portrait, spotify.Landscape,
                spotify.Portrait != null ? "Spotify" : "AudioDB", spotify.ArtistUrl); }
            finally { Gate.Release(); }
        }
    }
}
