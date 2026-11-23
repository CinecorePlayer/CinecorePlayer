#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    internal static partial class MusicArtistArtworkService
    {
        internal sealed record Artwork(string? Portrait, string? Landscape, string Source = "AudioDB", string? ArtistUrl = null, int PortraitRevision = 0, string? PortraitProvider = null);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly SemaphoreSlim Gate = new(3, 3);
        // AudioDB (chiave pubblica) limita le richieste: i fanart restano in coda singola.
        private static readonly SemaphoreSlim AudioDbGate = new(1, 1);
        private static readonly ConcurrentDictionary<string, Lazy<Task<Artwork>>> Requests = new();
        private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> DeezerPortraits = new();
        private static readonly ConcurrentDictionary<string, CachedArtwork> DeezerPortraitResults = new();
        private sealed record CachedArtwork(Artwork Value, DateTime Expires);
        private static readonly ConcurrentDictionary<string, CachedArtwork> Results = new();
        private static readonly ConcurrentDictionary<string, byte> LandscapeRequests = new();
        private static int _providerGeneration;

        /// <summary>Identità dell'artista (vedi <see cref="Identity"/>) e artwork aggiornato.</summary>
        public static event Action<string, Artwork>? ArtworkUpdated;

        static MusicArtistArtworkService() => Http.DefaultRequestHeaders.UserAgent.TryParseAdd("CinecorePlayer2025/1.0");
        internal static void ResetProviderCache()
        {
            Interlocked.Increment(ref _providerGeneration);
            Results.Clear();
            Requests.Clear();
            DeezerPortraits.Clear();
            DeezerPortraitResults.Clear();
            PortraitChecks.Clear();
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

        private static string Directory_() => Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "ArtistArtwork");
        private static string FileKey(string artist, string? album) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v4-artist-photo|" + Identity(artist) + "|" + Identity(album))));

        public static Task<Artwork> ResolveAsync(string artist, string? album = null)
        {
            string key = Identity(artist) + "|" + Identity(album);
            if (Results.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow &&
                (cached.Value.Portrait == null || File.Exists(cached.Value.Portrait)) &&
                (cached.Value.Landscape == null || MusicLandscapeArtworkService.IsLandscapeFile(cached.Value.Landscape))) return Task.FromResult(cached.Value);
            return Requests.GetOrAdd(key, _ => new Lazy<Task<Artwork>>(() => ResolveAndCacheAsync(key, artist, album))).Value;
        }

        private static async Task<Artwork> ResolveAndCacheAsync(string key, string artist, string? album)
        {
            int generation = Volatile.Read(ref _providerGeneration);
            try
            {
                var result = await ResolveCoreAsync(artist, album).ConfigureAwait(false);
                if (generation == Volatile.Read(ref _providerGeneration))
                {
                    if (Results.TryGetValue(key, out var arrived)) result = result with
                    { Portrait = result.Portrait ?? arrived.Value.Portrait, Landscape = result.Landscape ?? arrived.Value.Landscape };
                    Results[key] = new CachedArtwork(result, DateTime.UtcNow.Add(result.Portrait != null && result.Landscape != null ? TimeSpan.FromDays(7) : TimeSpan.FromMinutes(2)));
                }
                return result;
            }
            finally { Requests.TryRemove(key, out _); }
        }

        private static string? _preferredSource;
        private static string PreferencePath => Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "music-metadata-source.txt");

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
            // V2 rejects legacy panorama files below 1080p, including portraits that
            // an older build accidentally reused in the rotating music banner.
            if (cached.Landscape != null && !MusicLandscapeArtworkService.IsLandscapeFile(cached.Landscape)) return false;
            // Le manifest senza alcuna immagine possono risalire alla vecchia rotta TIDAL
            // /searchResults/{query}, ora ritirata: non conservarli come cache valida.
            if (cached.Portrait == null && cached.Landscape == null && cached.Source.StartsWith("Checked:", StringComparison.Ordinal)) return false;
            // Una foto presa quando la fonte preferita non era disponibile (o non era quella) va richiesta di nuovo.
            var order = SourceOrder();
            if (cached.Source != order[0] && cached.Source != "Checked:" + string.Join(">", order)) return false;
            TimeSpan age = DateTime.UtcNow - written;
            return cached.Portrait != null ? age < TimeSpan.FromDays(30) : age < TimeSpan.FromMinutes(2);
        }

        private static async Task<Artwork> ResolveCoreAsync(string artist, string? album)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            var spotify = new SpotifyArtistArtworkService.ArtistImage(null, null, null);
            string directory = Directory_();
            string key = FileKey(artist, album);
            string manifest = Path.Combine(directory, key + ".json");
            try
            {
                if (File.Exists(manifest))
                {
                    var cached = JsonSerializer.Deserialize<Artwork>(await File.ReadAllTextAsync(manifest).ConfigureAwait(false));
                    DateTime written = File.GetLastWriteTimeUtc(manifest);
                    if (cached != null && CacheIsUsable(cached, written) && cached.PortraitRevision >= 1)
                    {
                        // La foto panoramica e' dell'artista, non dell'album: trovata per un suo album vale per tutti.
                        if (cached.Landscape == null && SharedLandscape(artist, directory) is { } shared)
                        {
                            cached = cached with { Landscape = shared };
                            try { await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(cached)).ConfigureAwait(false); } catch { }
                            return cached;
                        }
                        if (cached.Landscape == null && DateTime.UtcNow - written > TimeSpan.FromMinutes(15))
                            StartLandscapeLookup(artist, album, directory, key, manifest, cached);
                        return cached;
                    }
                }

                // Si chiede alla fonte preferita; se non ha la foto, alle altre nell'ordine.
                var order = SourceOrder();
                // Upgrade existing portraits once; retain verified files rather than
                // invalidating and downloading the whole artwork cache again.
                var legacy = File.Exists(manifest) ? JsonSerializer.Deserialize<Artwork>(await File.ReadAllTextAsync(manifest).ConfigureAwait(false)) : null;
                string? portrait = PreferredSource == "auto" || PrimaryArtist(artist) != null ? await WikimediaPortraitAsync(artist, directory, key).ConfigureAwait(false) : null;
                if (portrait == null && PrimaryArtist(artist) == null && legacy?.Portrait != null && !legacy.Portrait.Contains("-tidal-", StringComparison.OrdinalIgnoreCase) && File.Exists(legacy.Portrait) &&
                    await IsCataloguePortraitAsync(artist, legacy.Portrait).ConfigureAwait(false))
                {
                    var verified = legacy with { PortraitRevision = 1 };
                    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(verified)).ConfigureAwait(false);
                    if (verified.Landscape == null) StartLandscapeLookup(artist, album, directory, key, manifest, verified);
                    return verified;
                }
                string winner = "None";
                if (portrait != null) { winner = "Wikimedia"; spotify = new(portrait, null, null); }
                foreach (string provider in portrait == null ? order : Enumerable.Empty<string>())
                {
                    if (provider == "Deezer")
                    {
                        // Combined release credits can have their own album fallback.
                        // Resolve the primary artist's profile rather than that release.
                        string primary = PrimaryArtist(artist) ?? artist;
                        portrait = await DeezerPortraitAsync(primary, directory, key).ConfigureAwait(false);
                        if (portrait != null && primary != artist)
                        {
                            if (portrait != null && !await IsCataloguePortraitAsync(artist, portrait).ConfigureAwait(false)) portrait = null;
                        }
                        if (portrait != null) spotify = new SpotifyArtistArtworkService.ArtistImage(portrait, null, null);
                    }
                    else
                    {
                        var found = provider == "Tidal"
                            ? await TidalArtistArtworkService.ResolveAsync(artist, directory).ConfigureAwait(false)
                            : await SpotifyArtistArtworkService.ResolveAsync(artist, directory).ConfigureAwait(false);
                        if (found.Portrait != null && await IsCataloguePortraitAsync(artist, found.Portrait).ConfigureAwait(false))
                        { spotify = found; portrait = found.Portrait; }
                    }
                    if (portrait != null) { winner = provider; break; }
                }
                if (portrait == null && PreferredSource != "auto")
                {
                    portrait = await WikimediaPortraitAsync(artist, directory, key).ConfigureAwait(false);
                    if (portrait != null) { winner = "Wikimedia"; spotify = new(portrait, null, null); }
                }
                string source = winner == order[0] ? winner : "Checked:" + string.Join(">", order);
                var result = new Artwork(portrait, spotify.Landscape, source, spotify.ArtistUrl, 1, winner);
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
            var parts = Regex.Split(artist, @"\s*(?:,|;|\bfeat\.?|\bft\.?|\bfeaturing\b)\s*", RegexOptions.IgnoreCase)
                .Where(p => p.Trim().Length > 1).ToArray();
            return parts.Length > 1 ? parts[0].Trim() : null;
        }

        private static async Task<string?> DeezerPortraitAsync(string artist, string directory, string key)
        {
            string identity = Identity(artist);
            if (DeezerPortraitResults.TryGetValue(identity, out var cached) && cached.Expires > DateTime.UtcNow &&
                (cached.Value.Portrait == null || File.Exists(cached.Value.Portrait))) return cached.Value.Portrait;
            try
            {
                string? photo = await DeezerPortraits.GetOrAdd(identity, _ => new Lazy<Task<string?>>(() => FetchDeezerPortraitAsync(artist, directory, key))).Value.ConfigureAwait(false);
                DeezerPortraitResults[identity] = new(new Artwork(photo, null, "Deezer"), DateTime.UtcNow.Add(photo == null ? TimeSpan.FromMinutes(2) : TimeSpan.FromDays(7)));
                return photo;
            }
            finally { DeezerPortraits.TryRemove(identity, out _); }
        }

        private static async Task<string?> FetchDeezerPortraitAsync(string artist, string directory, string key)
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
                        Id = a.TryGetProperty("id", out var id) && id.TryGetInt64(out long artistId) ? artistId : 0,
                        Fans = a.TryGetProperty("nb_fan", out var f) && f.TryGetInt64(out long fans) ? fans : 0,
                        Picture = a.TryGetProperty("picture_xl", out var p) ? p.GetString() : null
                    })
                    .Where(a => a.Picture != null && !a.Picture.Contains("/artist//", StringComparison.Ordinal))
                    .ToList();
                foreach (var best in candidates.Where(a => Identity(a.Name) == exact || LooseIdentity(a.Name) == loose)
                    .OrderByDescending(a => Identity(a.Name) == exact).ThenByDescending(a => a.Fans))
                {
                    if (best.Id <= 0 || !IsDeezerImage(best.Picture, "artist", out var url)) continue;
                    byte[] bytes = await Http.GetByteArrayAsync(url!).ConfigureAwait(false);
                    if (bytes.Length < 1024 || bytes.Length > 16 * 1024 * 1024) continue;
                    if (!await IsDeezerPortraitAsync(best.Id, url!, bytes).ConfigureAwait(false))
                    { Dbg.Log("[ARTIST ART] rejected album artwork returned as portrait for " + artist); continue; }
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, key + "-portrait.jpg");
                    await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                    return path;
                }
                return null;
            }
            catch { return null; }
        }

        private static bool IsDeezerImage(string? value, string kind, out Uri? uri)
        {
            uri = null;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
                (uri.Host.Equals("dzcdn.net", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase)) &&
                Regex.IsMatch(uri.AbsolutePath, "^/images/" + kind + "/[a-f0-9]{32}/", RegexOptions.IgnoreCase);
        }

        // Copia Mac: la miniatura per il confronto la fa SkiaSharp (ArtworkPixels), GDI qui non c'e'.
        private static byte[] PhotoPixels(byte[] data) => ArtworkPixels.Rgb32(data);

        private static bool SamePhoto(byte[] a, byte[] b)
        {
            long difference = 0;
            for (int i = 0; i < a.Length; i++) difference += Math.Abs(a[i] - b[i]);
            return difference <= a.Length * 10L;
        }

        // Deezer sometimes exposes an album cover under /images/artist as well.
        // Check catalogue image hashes and decoded thumbnails, including re-encoded copies.
        private static async Task<bool> IsDeezerPortraitAsync(long artistId, Uri portrait, byte[] bytes)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var covers = new System.Collections.Generic.List<Uri>();
                string hash = portrait.Host.EndsWith("dzcdn.net", StringComparison.OrdinalIgnoreCase) ? portrait.AbsolutePath.Split('/').ElementAtOrDefault(3) ?? "" : "";
                for (int page = 0; page < 5; page++)
                {
                    using var albums = JsonDocument.Parse(await Http.GetStringAsync($"https://api.deezer.com/artist/{artistId}/albums?limit=100&index={page * 100}", timeout.Token).ConfigureAwait(false));
                    if (!albums.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return false;
                    foreach (var album in data.EnumerateArray())
                    {
                        if (album.TryGetProperty("md5_image", out var md5) && string.Equals(md5.GetString(), hash, StringComparison.OrdinalIgnoreCase)) return false;
                        string? cover = album.TryGetProperty("cover_small", out var small) ? small.GetString() : null;
                        if (IsDeezerImage(cover, "cover", out var url)) covers.Add(url!);
                    }
                    if (!albums.RootElement.TryGetProperty("next", out var next) || string.IsNullOrWhiteSpace(next.GetString())) break;
                    if (page == 4) return false; // An incomplete catalogue cannot certify the fallback photo.
                }
                byte[] pixels = PhotoPixels(bytes); int matched = 0;
                await Parallel.ForEachAsync(covers.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token }, async (cover, token) =>
                {
                    if (Volatile.Read(ref matched) != 0) return;
                    try
                    {
                        byte[] thumb = await Http.GetByteArrayAsync(cover, token).ConfigureAwait(false);
                        if (thumb.Length <= 1024 * 1024 && SamePhoto(pixels, PhotoPixels(thumb))) Interlocked.Exchange(ref matched, 1);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }).ConfigureAwait(false);
                return matched == 0;
            }
            catch { return false; }
        }

        private static void StartLandscapeLookup(string artist, string? album, string directory, string key, string manifest, Artwork current)
        {
            int generation = Volatile.Read(ref _providerGeneration);
            if (!LandscapeRequests.TryAdd(key, 0)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var found = SharedLandscape(artist, directory) is { } known ? (Portrait: current.Portrait, Landscape: (string?)known)
                        : await AudioDbArtworkAsync(artist, album, directory, key).ConfigureAwait(false);
                    // TheAudioDB ha spesso solo panoramiche 1280x720 (scartate: su schermi grandi si vedono sfocate).
                    // Wikimedia Commons ha foto libere ad alta risoluzione dell'artista.
                    if (found.Landscape == null && current.Landscape == null)
                    {
                        string? commons = await CommonsLandscapeAsync(artist, album, directory, key).ConfigureAwait(false);
                        if (commons != null) found = (found.Portrait, commons);
                    }
                    if (generation != Volatile.Read(ref _providerGeneration)) return;
                    var updated = current with
                    {
                        Portrait = current.Portrait ?? found.Portrait,
                        Landscape = current.Landscape ?? found.Landscape,
                        PortraitProvider = current.Portrait == null && found.Portrait != null ? "AudioDB" : current.PortraitProvider,
                        Source = current.Portrait == null && found.Portrait != null && current.Source == "None" ? "AudioDB" : current.Source
                    };
                    // Riscrivi sempre: aggiorna anche la data dell'ultimo tentativo.
                    try { await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(updated)).ConfigureAwait(false); } catch { }
                    if (updated.Landscape != null) RememberSharedLandscape(artist, directory, updated.Landscape);
                    if (updated == current) return;
                    string requestIdentity = Identity(artist) + "|" + Identity(album);
                    Results[requestIdentity] = new CachedArtwork(updated, DateTime.UtcNow.Add(updated.Landscape != null ? TimeSpan.FromDays(7) : TimeSpan.FromMinutes(2)));
                    try { ArtworkUpdated?.Invoke(requestIdentity, updated); } catch { }
                }
                catch { }
                finally { LandscapeRequests.TryRemove(key, out _); }
            });
        }

        private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);

        private static string SharedLandscapeFile(string artist, string directory) =>
            Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("artist-landscape|" + Identity(artist)))) + ".landscape.txt");

        /// <summary>Foto panoramica gia' trovata per questo artista (con qualsiasi album), se il file c'e' ancora.</summary>
        private static string? SharedLandscape(string artist, string directory)
        {
            try
            {
                string file = SharedLandscapeFile(artist, directory);
                if (!File.Exists(file)) return null;
                string path = File.ReadAllText(file).Trim();
                return MusicLandscapeArtworkService.IsLandscapeFile(path) ? path : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Foto panoramica gia' su disco per artista/album, senza rete e senza attese: serve ad aprire la scheda
        /// dell'album con lo sfondo gia' al suo posto (prima arrivava dopo, a scheda aperta).
        /// </summary>
        public static string? CachedLandscape(string artist, string? album)
        {
            try
            {
                string directory = Directory_();
                string manifest = Path.Combine(directory, FileKey(artist, album) + ".json");
                if (File.Exists(manifest))
                {
                    var cached = JsonSerializer.Deserialize<Artwork>(File.ReadAllText(manifest));
                    if (cached?.Landscape != null && MusicLandscapeArtworkService.IsLandscapeFile(cached.Landscape)) return cached.Landscape;
                }
                return SharedLandscape(artist, directory);
            }
            catch { return null; }
        }

        private static void RememberSharedLandscape(string artist, string directory, string landscape)
        {
            try { Directory.CreateDirectory(directory); File.WriteAllText(SharedLandscapeFile(artist, directory), landscape); } catch { }
        }
        private static readonly ConcurrentDictionary<string, string?> MusicBrainzIds = new(StringComparer.Ordinal);

        private static async Task<JsonDocument?> GetWikiJsonAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("CinecorePlayer/0.3 (https://github.com/CinecorePlayer/CinecorePlayer)");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        /// <summary>
        /// Foto panoramica ad alta risoluzione da Wikimedia Commons: artista trovato su Wikidata tramite l'identificativo
        /// MusicBrainz (niente omonimi), poi la sua categoria Commons; si sceglie una foto larga almeno 2560 px.
        /// </summary>
        private static async Task<string?> CommonsLandscapeAsync(string artist, string? album, string directory, string key)
        {
            try
            {
                string? mbid = await MusicBrainzArtistIdAsync(artist, album).ConfigureAwait(false);
                if (mbid == null) return null;
                using var found = await GetWikiJsonAsync("https://www.wikidata.org/w/api.php?action=query&list=search&format=json&srlimit=1&srsearch=" +
                    Uri.EscapeDataString("haswbstatement:P434=" + mbid)).ConfigureAwait(false);
                string? entity = found != null && found.RootElement.TryGetProperty("query", out var q) && q.TryGetProperty("search", out var hits) && hits.GetArrayLength() > 0
                    ? hits[0].GetProperty("title").GetString() : null;
                if (entity == null) return null;
                using var claims = await GetWikiJsonAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&props=claims&format=json&ids=" + Uri.EscapeDataString(entity)).ConfigureAwait(false);
                string? category = null;
                try { category = claims!.RootElement.GetProperty("entities").GetProperty(entity).GetProperty("claims").GetProperty("P373")[0].GetProperty("mainsnak").GetProperty("datavalue").GetProperty("value").GetString(); } catch { }
                if (string.IsNullOrWhiteSpace(category)) return null;
                var candidates = new List<(int Index, string Url)>();
                string artistWords = Identity(artist);
                // Fino a quattro pagine di risultati: per gli artisti molto fotografati le prime 50 sono loghi e locandine.
                for (int offset = 0; offset < 200 && candidates.Count < 3; offset += 50)
                {
                    using var files = await GetWikiJsonAsync("https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=50&gsroffset=" + offset +
                        "&prop=imageinfo&iiprop=size|url&iiurlwidth=2560&gsrsearch=" + Uri.EscapeDataString("deepcat:\"" + category + "\" filetype:bitmap")).ConfigureAwait(false);
                    if (files == null || !files.RootElement.TryGetProperty("query", out var fq) || !fq.TryGetProperty("pages", out var pages)) break;
                    foreach (var page in pages.EnumerateObject())
                    {
                        var p = page.Value;
                        string title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        // Niente loghi, locandine, firme o foto in cui l'artista e' con altre persone famose.
                        if (Regex.IsMatch(title, @"logo|signature|autograph|poster|flyer|ticket|map|cover|album|greets|meets|with|president|award|\.png$", RegexOptions.IgnoreCase)) continue;
                        if (!p.TryGetProperty("imageinfo", out var info) || info.GetArrayLength() == 0) continue;
                        var i = info[0];
                        int w = i.GetProperty("width").GetInt32(), h = i.GetProperty("height").GetInt32();
                        double ratio = w / (double)Math.Max(1, h);
                        if (w < 2560 || ratio < 1.45 || ratio > 2.2) continue;
                        string? url = i.TryGetProperty("thumburl", out var thumb) ? thumb.GetString() : i.GetProperty("url").GetString();
                        int index = offset + (p.TryGetProperty("index", out var ix) ? ix.GetInt32() : 49);
                        // Prima le foto che nel nome portano l'artista.
                        if (!Identity(title).Contains(artistWords, StringComparison.Ordinal)) index += 1000;
                        if (url != null) candidates.Add((index, url));
                    }
                    await Task.Delay(300).ConfigureAwait(false);
                }
                foreach (var pick in candidates.OrderBy(c => c.Index).Take(3))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, pick.Url);
                    request.Headers.UserAgent.ParseAdd("CinecorePlayer/0.3 (https://github.com/CinecorePlayer/CinecorePlayer)");
                    using var response = await Http.SendAsync(request).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;
                    byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (data.Length > 24 * 1024 * 1024) continue;
                    string path = Path.Combine(directory, key + "-commons-wide.jpg");
                    Directory.CreateDirectory(directory);
                    await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
                    if (MusicLandscapeArtworkService.IsLandscapeFile(path)) return path;
                    try { File.Delete(path); } catch { }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>Identificativo MusicBrainz dell'artista: dall'album quando c'e' (distingue gli omonimi), altrimenti dal nome esatto.</summary>
        private static async Task<string?> MusicBrainzArtistIdAsync(string artist, string? album)
        {
            string cacheKey = Identity(artist) + "|" + Identity(album);
            if (MusicBrainzIds.TryGetValue(cacheKey, out var known)) return known;
            await MusicBrainzGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string? resolved = await MusicBrainzLookupAsync(artist, album).ConfigureAwait(false);
                MusicBrainzIds[cacheKey] = resolved;
                return resolved;
            }
            finally { _ = Task.Delay(1100).ContinueWith(_ => MusicBrainzGate.Release(), TaskScheduler.Default); }
        }

        private static async Task<string?> MusicBrainzLookupAsync(string artist, string? album)
        {
            try
            {
                async Task<JsonDocument?> Get(string url)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.UserAgent.ParseAdd("CinecorePlayer/0.3 (https://github.com/CinecorePlayer/CinecorePlayer)");
                    using var response = await Http.SendAsync(request).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) return null;
                    return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
                string Quote(string value) => "\"" + value.Replace("\"", " ") + "\"";
                string target = Identity(artist);
                if (!string.IsNullOrWhiteSpace(album))
                {
                    using var groups = await Get("https://musicbrainz.org/ws/2/release-group/?fmt=json&limit=10&query=" +
                        Uri.EscapeDataString("releasegroup:" + Quote(album) + " AND artist:" + Quote(artist))).ConfigureAwait(false);
                    if (groups != null && groups.RootElement.TryGetProperty("release-groups", out var list))
                        foreach (var group in list.EnumerateArray())
                            if (group.TryGetProperty("artist-credit", out var credits))
                                foreach (var credit in credits.EnumerateArray())
                                    if (credit.TryGetProperty("artist", out var a) && a.TryGetProperty("name", out var n) && Identity(n.GetString()) == target &&
                                        a.TryGetProperty("id", out var id)) return id.GetString();
                    await Task.Delay(1100).ConfigureAwait(false); // limite di MusicBrainz: una richiesta al secondo
                }
                using var artists = await Get("https://musicbrainz.org/ws/2/artist/?fmt=json&limit=5&query=" + Uri.EscapeDataString("artist:" + Quote(artist))).ConfigureAwait(false);
                if (artists != null && artists.RootElement.TryGetProperty("artists", out var found))
                    foreach (var candidate in found.EnumerateArray())
                        if (candidate.TryGetProperty("name", out var name) && Identity(name.GetString()) == target &&
                            candidate.TryGetProperty("score", out var score) && score.GetInt32() >= 90 && candidate.TryGetProperty("id", out var id))
                            return id.GetString();
                return null;
            }
            catch { return null; }
        }

        private static async Task<(string? Portrait, string? Landscape)> AudioDbArtworkAsync(string artist, string? album, string directory, string key)
        {
            await AudioDbGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Prima per identificativo MusicBrainz: la ricerca per nome della chiave gratuita restituisce un solo
                // artista e spesso quello sbagliato (AURORA -> "Aurora UK", senza foto panoramiche).
                string? mbid = await MusicBrainzArtistIdAsync(artist, album).ConfigureAwait(false);
                string json = mbid != null
                    ? await Http.GetStringAsync("https://www.theaudiodb.com/api/v1/json/123/artist-mb.php?i=" + Uri.EscapeDataString(mbid)).ConfigureAwait(false)
                    : await Http.GetStringAsync("https://www.theaudiodb.com/api/v1/json/123/search.php?s=" + Uri.EscapeDataString(artist)).ConfigureAwait(false);
                using (var search = JsonDocument.Parse(json))
                if (mbid == null)
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
                            string path = Path.Combine(directory, key + (landscape ? "-wide-1080p.jpg" : "-audiodb-portrait.jpg"));
                            await File.WriteAllBytesAsync(path, data).ConfigureAwait(false); return path;
                        }
                        catch { return null; }
                    }
                    // Publish the portrait before the optional large fanart downloads.
                    string? portraitFound = await Fetch("strArtistThumb", false).ConfigureAwait(false);
                    if (portraitFound != null)
                    {
                        string identity = Identity(artist) + "|" + Identity(album);
                        Results.TryGetValue(identity, out var previous);
                        var photo = previous?.Value is Artwork known ? known with { Portrait = known.Portrait ?? portraitFound }
                            : new Artwork(portraitFound, null, "AudioDB", null, 1, "AudioDB");
                        Results[identity] = new(photo, DateTime.UtcNow.AddMinutes(2));
                        try { ArtworkUpdated?.Invoke(identity, photo); } catch { }
                    }
                    string? landscapeFound = null;
                    foreach (string field in new[] { "strArtistFanart", "strArtistFanart2", "strArtistFanart3", "strArtistFanart4", "strArtistWideThumb" })
                        if ((landscapeFound = await Fetch(field, true).ConfigureAwait(false)) != null) break;
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
