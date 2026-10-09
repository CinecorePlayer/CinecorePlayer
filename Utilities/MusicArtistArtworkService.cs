#nullable enable
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
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
    /// <summary>
    /// Foto degli artisti. Il ritratto arriva subito (Spotify se configurato, altrimenti
    /// Deezer, che non richiede chiavi e restituisce tutti gli omonimi); lo sfondo
    /// orizzontale di AudioDB viene cercato dopo, in background, e notificato con
    /// <see cref="ArtworkUpdated"/>. Prima tutto era in coda singola e il ritratto
    /// attendeva i download dei fanart, mentre la ricerca gratuita di AudioDB restituisce
    /// un solo artista (es. "Aurora UK" per AURORA) e lasciava quasi tutti senza foto.
    /// </summary>
    internal static class MusicArtistArtworkService
    {
        internal sealed record Artwork(string? Portrait, string? Landscape, string Source = "AudioDB", string? ArtistUrl = null);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly SemaphoreSlim Gate = new(3, 3);
        // AudioDB (chiave pubblica) limita le richieste: i fanart restano in coda singola.
        private static readonly SemaphoreSlim AudioDbGate = new(1, 1);
        private static readonly ConcurrentDictionary<string, Lazy<Task<Artwork>>> Requests = new();
        private sealed record CachedArtwork(Artwork Value, DateTime Expires);
        private static readonly ConcurrentDictionary<string, CachedArtwork> Results = new();
        private static readonly ConcurrentDictionary<string, byte> LandscapeRequests = new();

        /// <summary>Identità dell'artista (vedi <see cref="Identity"/>) e artwork aggiornato.</summary>
        public static event Action<string, Artwork>? ArtworkUpdated;

        static MusicArtistArtworkService() => Http.DefaultRequestHeaders.UserAgent.TryParseAdd("CinecorePlayer2025/1.0");
        internal static void ResetProviderCache()
        {
            Results.Clear();
            Requests.Clear();
        }
        public static string Identity(string? value) => Regex.Replace((value ?? "").Normalize(NormalizationForm.FormD).ToUpperInvariant(), @"[^\p{L}\p{N}]", "");

        // Confronto tollerante per i provider che traslitterano (Przybyłowicz / Przybylowicz).
        private static string LooseIdentity(string? value)
        {
            var builder = new StringBuilder();
            foreach (char c in (value ?? "").Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                builder.Append(c switch { 'ł' => 'l', 'Ł' => 'L', 'ø' => 'o', 'Ø' => 'O', 'đ' => 'd', 'Đ' => 'D', 'ß' => 's', 'æ' => 'a', 'Æ' => 'A', _ => c });
            }
            return Identity(builder.ToString());
        }

        private static string Directory_() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "ArtistArtwork");
        private static string FileKey(string artist) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Identity(artist))));

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
                Results[key] = new CachedArtwork(result, DateTime.UtcNow.Add(result.Portrait != null ? TimeSpan.FromDays(7) : TimeSpan.FromMinutes(30)));
                return result;
            }
            finally { Requests.TryRemove(key, out _); }
        }

        private static string? _preferredSource;
        private static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "music-metadata-source.txt");

        /// <summary>
        /// Fonte preferita per le foto degli artisti: "auto" (Spotify, poi TIDAL, poi Deezer),
        /// "spotify", "tidal" o "deezer". Le altre restano di riserva, nell'ordine.
        /// </summary>
        public static string PreferredSource
        {
            get
            {
                if (_preferredSource != null) return _preferredSource;
                try { _preferredSource = File.Exists(PreferencePath) ? File.ReadAllText(PreferencePath).Trim().ToLowerInvariant() : "auto"; }
                catch { _preferredSource = "auto"; }
                if (_preferredSource is not ("spotify" or "tidal" or "deezer")) _preferredSource = "auto";
                return _preferredSource;
            }
            set
            {
                _preferredSource = value is "spotify" or "tidal" or "deezer" ? value : "auto";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
                    File.WriteAllText(PreferencePath, _preferredSource);
                }
                catch (Exception ex) { Dbg.Warn("[MUSIC] metadata source save failed: " + ex.Message); }
                ResetProviderCache();
            }
        }

        /// <summary>Fonti da interrogare, nell'ordine: solo quelle utilizzabili (Deezer non chiede chiavi).</summary>
        private static System.Collections.Generic.List<string> SourceOrder()
        {
            string[] order = PreferredSource switch
            {
                "tidal" => new[] { "Tidal", "Spotify", "Deezer" },
                "deezer" => new[] { "Deezer", "Spotify", "Tidal" },
                _ => new[] { "Spotify", "Tidal", "Deezer" }
            };
            return order.Where(source => source switch
            {
                "Spotify" => SpotifyArtistArtworkService.IsConfigured,
                "Tidal" => TidalArtistArtworkService.IsConfigured,
                _ => true
            }).ToList();
        }

        private static bool CacheIsUsable(Artwork cached, DateTime written)
        {
            if (cached.Portrait != null && !File.Exists(cached.Portrait)) return false;
            if (cached.Landscape != null && !File.Exists(cached.Landscape)) return false;
            // Una foto presa quando la fonte preferita non era disponibile (o non era quella) va richiesta di nuovo.
            var order = SourceOrder();
            if (cached.Source != order[0] && cached.Source != "Checked:" + string.Join(">", order)) return false;
            TimeSpan age = DateTime.UtcNow - written;
            return cached.Portrait != null ? age < TimeSpan.FromDays(30) : age < TimeSpan.FromDays(1) && cached.Source != "AudioDB";
        }

        private static async Task<Artwork> ResolveCoreAsync(string artist, string? album)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            var spotify = new SpotifyArtistArtworkService.ArtistImage(null, null, null);
            string directory = Directory_();
            string key = FileKey(artist);
            string manifest = Path.Combine(directory, key + ".json");
            try
            {
                if (File.Exists(manifest))
                {
                    var cached = JsonSerializer.Deserialize<Artwork>(await File.ReadAllTextAsync(manifest).ConfigureAwait(false));
                    DateTime written = File.GetLastWriteTimeUtc(manifest);
                    if (cached != null && CacheIsUsable(cached, written))
                    {
                        if (cached.Landscape == null && DateTime.UtcNow - written > TimeSpan.FromDays(1))
                            StartLandscapeLookup(artist, album, directory, key, manifest, cached);
                        return cached;
                    }
                }

                // Si chiede alla fonte preferita; se non ha la foto, alle altre nell'ordine.
                var order = SourceOrder();
                string? portrait = null;
                string winner = "None";
                foreach (string provider in order)
                {
                    if (provider == "Deezer")
                    {
                        portrait = await DeezerPortraitAsync(artist, directory, key).ConfigureAwait(false);
                        // Crediti multipli ("A, B", "A & B", "A feat. B"): foto del primo artista.
                        if (portrait == null && PrimaryArtist(artist) is string primary)
                            portrait = await DeezerPortraitAsync(primary, directory, key).ConfigureAwait(false);
                        if (portrait != null) spotify = new SpotifyArtistArtworkService.ArtistImage(portrait, null, null);
                    }
                    else
                    {
                        var found = provider == "Tidal"
                            ? await TidalArtistArtworkService.ResolveAsync(artist, directory).ConfigureAwait(false)
                            : await SpotifyArtistArtworkService.ResolveAsync(artist, directory).ConfigureAwait(false);
                        if (found.Portrait != null) { spotify = found; portrait = found.Portrait; }
                    }
                    if (portrait != null) { winner = provider; break; }
                }
                string source = winner == order[0] ? winner : "Checked:" + string.Join(">", order);
                var result = new Artwork(portrait, spotify.Landscape, source, spotify.ArtistUrl);
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(result)).ConfigureAwait(false);
                if (result.Landscape == null)
                    StartLandscapeLookup(artist, album, directory, key, manifest, result);
                return result;
            }
            catch
            {
                return new Artwork(spotify.Portrait, spotify.Landscape, "None", spotify.ArtistUrl);
            }
            finally { Gate.Release(); }
        }

        private static string? PrimaryArtist(string artist)
        {
            var parts = Regex.Split(artist, @"\s*(?:,|;|/|&|\+|\bfeat\.?|\bft\.?|\bfeaturing\b|\bx\b|\bvs\.?|\bwith\b)\s*", RegexOptions.IgnoreCase)
                .Where(p => p.Trim().Length > 1).ToArray();
            return parts.Length > 1 ? parts[0].Trim() : null;
        }

        private static async Task<string?> DeezerPortraitAsync(string artist, string directory, string key)
        {
            try
            {
                // API pubblica: si invia solo il nome dell'artista, mai un percorso locale.
                string json = await Http.GetStringAsync("https://api.deezer.com/search/artist?limit=25&q=" + Uri.EscapeDataString(artist)).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
                string exact = Identity(artist), loose = LooseIdentity(artist);
                var candidates = data.EnumerateArray()
                    .Select(a => new
                    {
                        Name = a.TryGetProperty("name", out var n) ? n.GetString() : null,
                        Fans = a.TryGetProperty("nb_fan", out var f) && f.TryGetInt64(out long fans) ? fans : 0,
                        Picture = a.TryGetProperty("picture_xl", out var p) ? p.GetString() : null
                    })
                    .Where(a => a.Picture != null && !a.Picture.Contains("/artist//", StringComparison.Ordinal))
                    .ToList();
                var best = candidates.Where(a => Identity(a.Name) == exact).OrderByDescending(a => a.Fans).FirstOrDefault()
                        ?? candidates.Where(a => LooseIdentity(a.Name) == loose).OrderByDescending(a => a.Fans).FirstOrDefault();
                if (best == null || !Uri.TryCreate(best.Picture, UriKind.Absolute, out var url) || url.Scheme != "https" ||
                    !url.Host.EndsWith("dzcdn.net", StringComparison.OrdinalIgnoreCase)) return null;
                byte[] bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes.Length < 1024 || bytes.Length > 16 * 1024 * 1024) return null;
                using (var stream = new MemoryStream(bytes)) using (Image.FromStream(stream)) { }
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, key + "-portrait.jpg");
                await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                return path;
            }
            catch { return null; }
        }

        private static void StartLandscapeLookup(string artist, string? album, string directory, string key, string manifest, Artwork current)
        {
            if (!LandscapeRequests.TryAdd(key, 0)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var found = await AudioDbArtworkAsync(artist, album, directory, key).ConfigureAwait(false);
                    var updated = current with
                    {
                        Portrait = current.Portrait ?? found.Portrait,
                        Landscape = current.Landscape ?? found.Landscape,
                        Source = current.Portrait == null && found.Portrait != null && current.Source == "None" ? "AudioDB" : current.Source
                    };
                    // Riscrivi sempre: aggiorna anche la data dell'ultimo tentativo.
                    try { await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(updated)).ConfigureAwait(false); } catch { }
                    if (updated == current) return;
                    foreach (var entry in Results.Where(r => r.Key.StartsWith(Identity(artist) + "|", StringComparison.Ordinal)).ToList())
                        Results[entry.Key] = new CachedArtwork(updated, DateTime.UtcNow.AddDays(7));
                    try { ArtworkUpdated?.Invoke(Identity(artist), updated); } catch { }
                }
                catch { }
                finally { LandscapeRequests.TryRemove(key, out _); }
            });
        }

        private static async Task<(string? Portrait, string? Landscape)> AudioDbArtworkAsync(string artist, string? album, string directory, string key)
        {
            await AudioDbGate.WaitAsync().ConfigureAwait(false);
            try
            {
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
                if (!doc.RootElement.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array) return (null, null);
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
                            string path = Path.Combine(directory, key + (landscape ? "-wide.jpg" : "-audiodb-portrait.jpg"));
                            await File.WriteAllBytesAsync(path, data).ConfigureAwait(false); return path;
                        }
                        catch { return null; }
                    }
                    string? landscapeFound = null;
                    foreach (string field in new[] { "strArtistFanart", "strArtistFanart2", "strArtistFanart3", "strArtistFanart4", "strArtistWideThumb" })
                        if ((landscapeFound = await Fetch(field, true).ConfigureAwait(false)) != null) break;
                    string? portraitFound = await Fetch("strArtistThumb", false).ConfigureAwait(false);
                    return (portraitFound, landscapeFound);
                }
                return (null, null);
            }
            catch { return (null, null); }
            finally
            {
                // Pausa fra richieste per rispettare il limite della chiave pubblica.
                _ = Task.Delay(1200).ContinueWith(_ => AudioDbGate.Release(), TaskScheduler.Default);
            }
        }
    }
}
