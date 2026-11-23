#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
#if CINECORE_MAC
using SkiaSharp;
#endif
using System.IO;
using System.Linq;
using System.Net;
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
        private const string GeniusArtworkBrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128.0 Safari/537.36";
        private static readonly HttpClient GeniusHttp = new() { Timeout = TimeSpan.FromSeconds(12) };

        private static readonly SemaphoreSlim Gate = new(4, 4);
        private static readonly SemaphoreSlim TrackArtworkGate = new(3, 3);
        private static readonly SemaphoreSlim LocalArtworkGate = new(3, 3);
        private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> Inflight = new(StringComparer.OrdinalIgnoreCase);
        internal static event Action<string, string>? ArtworkResolved;
        private static readonly ConcurrentDictionary<string, bool> EmbeddedChecked = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string?> Memory = new(StringComparer.OrdinalIgnoreCase);
        private sealed record TagSnapshot(long Length, long Written, MediaProbe.AudioTags Tags);
        private static readonly ConcurrentDictionary<string, TagSnapshot> TagCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> Misses = new(StringComparer.OrdinalIgnoreCase);
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

        private sealed record LocalCover(string? Path, DateTime Expires);
        private static readonly ConcurrentDictionary<string, LocalCover> LocalCovers = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
        private static readonly Regex CoverName = new(@"cover|front|folder|album|artwork|jacket", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NotCoverName = new(@"back|rear|inlay|tray|disc|\bcd\b|booklet|spine|inside", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Copertina gia' presente sul disco: nella cartella dell'album (anche con nomi come
        /// "The_Witcher_3-Soundtrack_cover.jpg" o l'unica immagine presente), nelle cartelle
        /// sorelle (flac/ accanto a mp3/cover.jpg) e in quella superiore.
        /// </summary>
        public static string? FindLocalAlbumCover(string? mediaPath, MediaProbe.AudioTags? knownTags = null)
        {
            if (string.IsNullOrWhiteSpace(mediaPath)) return null;
            string? dir = Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrWhiteSpace(dir)) return null;
            string albumName = CleanFileName((knownTags ?? SafeReadTags(mediaPath)).Album);
            string cacheKey = dir + "|" + MusicArtistArtworkService.Identity(albumName);
            if (LocalCovers.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow && (cached.Path == null || File.Exists(cached.Path))) return cached.Path;
            string? Find()
            {
                string folder = dir;
                try
                {
                    string? Pick(string candidateDir, bool allowSingle)
                    {
                        if (!Directory.Exists(candidateDir)) return null;
                        var images = Directory.EnumerateFiles(candidateDir)
                            .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                            .Take(40).ToList();
                        var named = images.Where(f => CoverName.IsMatch(Path.GetFileNameWithoutExtension(f)) && !NotCoverName.IsMatch(Path.GetFileNameWithoutExtension(f)))
                            .OrderByDescending(f => Regex.IsMatch(Path.GetFileNameWithoutExtension(f), "front|cover", RegexOptions.IgnoreCase))
                            .ThenByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                        if (named != null) return named;
                        if (allowSingle && images.Count == 1)
                        {
#if CINECORE_MAC
                            using var image = SKCodec.Create(images[0]);
                            if (image == null) return null;
                            double aspect = image.Info.Width / (double)Math.Max(1, image.Info.Height);
#else
                            using var image = ImageAssetDecoder.Load(images[0]);
                            double aspect = image.Width / (double)Math.Max(1, image.Height);
#endif
                            if (aspect >= .8 && aspect <= 1.25) return images[0];
                        }
                        return null;
                    }

                    string? found = Pick(folder, allowSingle: true);
                    if (found != null) return found;
                    // Guarda nella cartella album sopra solo per sottocartelle CD/FLAC/MP3
                    // con un nome album che corrisponde ai tag del brano. Altrimenti una
                    // cover.jpg in Downloads o in una cartella condivisa può contaminare
                    // album senza immagini proprie.
                    string? parent = Path.GetDirectoryName(folder);
                    string parentName = Path.GetFileName(parent ?? string.Empty);
                    string folderName = Path.GetFileName(folder);
                    bool isDiscOrFormat = Regex.IsMatch(folderName,
                        @"^(?:(?:cd|disc|disk|disco)\s*\d+|flac|mp3|aac|wav)$", RegexOptions.IgnoreCase);
                    bool parentMatchesAlbum = albumName.Length > 0 && SameArtworkAlbumTitle(parentName, albumName);
                    if (isDiscOrFormat && parentMatchesAlbum && !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                    {
                        found = Pick(parent, allowSingle: false);
                        if (found != null) return found;
                        var siblings = Directory.EnumerateDirectories(parent).Take(8).ToList();
                        if (siblings.Count <= 6)
                            foreach (string sibling in siblings)
                                if (!string.Equals(sibling, folder, StringComparison.OrdinalIgnoreCase) &&
                                    Regex.IsMatch(Path.GetFileName(sibling), @"^(?:(?:cd|disc|disk|disco)\s*\d+|flac|mp3|aac|wav)$", RegexOptions.IgnoreCase) &&
                                    (found = Pick(sibling, allowSingle: false)) != null)
                                    return found;
                    }
                }
                catch { }
                return null;
            }
            string? found = Find();
            if (LocalCovers.Count > 4096) LocalCovers.Clear();
            LocalCovers[cacheKey] = new(found, DateTime.UtcNow.AddSeconds(30));
            return found;
        }

        public static string? GetCachedArtworkPath(string? mediaPath, MediaProbe.AudioTags? knownTags = null)
        {
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                return null;
            if (FindLocalAlbumCover(mediaPath, knownTags) is { } local)
                return local;
            try
            {
                string embedded = EmbeddedCachePath(mediaPath);
                if (File.Exists(embedded)) return embedded;
                var tags = knownTags ?? SafeReadTags(mediaPath);
                string songCache = TrackArtworkCachePath(mediaPath, tags);
                if (File.Exists(songCache)) return songCache;
                if (!HasTrustedRemoteArtworkIdentity(tags))
                    return null;
                foreach (var albumKey in BuildAlbumKeys(mediaPath, tags))
                {
                    string albumCache = AlbumCachePathFor(albumKey);
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

        internal static void RetryProviderMisses()
        {
            Misses.Clear();
            try
            {
                foreach (string marker in Directory.EnumerateFiles(Path.GetDirectoryName(CachePathFor("x"))!, "*.lookup-miss"))
                    try { File.Delete(marker); } catch { }
            }
            catch { }
        }

        // Un brano cercato senza risultato non si ricerca a ogni avvio: prima erano ~115 richieste e 45 s di rete a ogni
        // apertura della libreria. Il segno resta su disco una settimana (o finche' si chiede di riprovare).
        private static bool ProvidersUnavailable
        {
            get { try { return !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); } catch { return false; } }
        }

        /// <summary>Connessione tornata: i mancati risultati recenti in memoria (anche quelli dovuti alla rete) si dimenticano.</summary>
        internal static void ForgetRecentMisses() => Misses.Clear();

        private static bool RecentLookupMiss(string missKey)
        {
            try
            {
                var marker = new FileInfo(CachePathFor(missKey) + ".lookup-miss");
                return marker.Exists && DateTime.UtcNow - marker.LastWriteTimeUtc < TimeSpan.FromDays(7);
            }
            catch { return false; }
        }

        private static void RememberLookupMiss(string missKey)
        {
            try
            {
                string marker = CachePathFor(missKey) + ".lookup-miss";
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            }
            catch { }
        }

        internal static async Task<string?> PrepareLocalArtworkAsync(string mediaPath, MediaProbe.AudioTags knownTags, CancellationToken ct)
        {
            await LocalArtworkGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string? cover = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(mediaPath)) return null;
                    return FindLocalAlbumCover(mediaPath, knownTags) ?? EmbeddedArtwork(mediaPath, knownTags);
                }, ct).ConfigureAwait(false);
                if (cover != null) AnnounceArtwork(mediaPath, cover);
                return cover;
            }
            finally { LocalArtworkGate.Release(); }
        }

        internal static string? GetCachedAlbumArtworkPath(string mediaPath, MediaProbe.AudioTags? knownTags = null)
        {
            if (FindLocalAlbumCover(mediaPath, knownTags) is { } local) return local;
            foreach (string key in BuildAlbumKeys(mediaPath, knownTags ?? SafeReadTags(mediaPath)))
            {
                string cached = AlbumCachePathFor(key);
                if (File.Exists(cached)) return cached;
            }
            return null;
        }

        // A track image, including a Genius song cover, cannot establish an album image.
        // Resolve the publication separately and share only exact album matches.
        internal static Task<string?> ResolveAlbumArtworkAsync(string mediaPath, CancellationToken ct)
            => Task.Run(async () =>
            {
                var tags = SafeReadTags(mediaPath);
                var keys = BuildAlbumKeys(mediaPath, tags).ToList();
                if (keys.Count == 0) return null;
                if (GetCachedAlbumArtworkPath(mediaPath, tags) is { } cached) return cached;
                string identity = "publication|" + keys[0];
                var shared = Inflight.GetOrAdd(identity, _ => new Lazy<Task<string?>>(
                    async () =>
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        string marker = CachePathFor("publication-miss-v1|" + keys[0] + "|" + MusicCatalog.Signature) + ".album-miss";
                        if (RecentMarker(marker)) return null;
                        await Gate.WaitAsync(deadline.Token).ConfigureAwait(false);
                        try
                        {
                            if (GetCachedAlbumArtworkPath(mediaPath, tags) is { } ready) return ready;
                            string output = CachePathFor("publication-v1|" + keys[0]);
                            if (MusicCatalog.AskedFirst && await TryCatalogOnceAsync(mediaPath, tags, identity, output, keys, deadline.Token).ConfigureAwait(false) is { } first) return first;
                            string artist = FirstNonEmpty(tags.AlbumArtist, tags.Artist);
                            string album = CleanFileName(tags.Album);
                            string? url = await SearchItunesArtworkAsync(artist + " " + NormalizePublicationTitle(album), artist, album, deadline.Token).ConfigureAwait(false);
                            if (url != null && await DownloadArtworkAsync(url, output, deadline.Token).ConfigureAwait(false) is { } found)
                            {
                                foreach (string key in keys) File.Copy(found, AlbumCachePathFor(key), overwrite: true);
                                return found;
                            }
                            if (await TryDeezerOnceAsync(mediaPath, tags, identity, output, keys, deadline.Token).ConfigureAwait(false) is { } deezer) return deezer;
                            if (!MusicCatalog.AskedFirst && await TryCatalogOnceAsync(mediaPath, tags, identity, output, keys, deadline.Token).ConfigureAwait(false) is { } last) return last;
                            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
                            return null;
                        }
                        finally { Gate.Release(); }
                    }, LazyThreadSafetyMode.ExecutionAndPublication));
                var work = shared.Value;
                try { return await work.WaitAsync(ct).ConfigureAwait(false); }
                finally { if (work.IsCompleted) Inflight.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(identity, shared)); }
            }, ct);

        public static Task<string?> ResolveArtworkAsync(string? mediaPath, CancellationToken ct, MediaProbe.AudioTags? knownTags = null)
            => Task.Run(async () =>
            {
                if (string.IsNullOrWhiteSpace(mediaPath)) return null;
                // Share requests for this file, never its embedded artwork with other tracks.
                string key = Path.GetFullPath(mediaPath);
                var shared = Inflight.GetOrAdd(key, _ => new Lazy<Task<string?>>(
                    async () =>
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        return await ResolveArtworkCoreAsync(mediaPath, deadline.Token, knownTags).ConfigureAwait(false);
                    }, LazyThreadSafetyMode.ExecutionAndPublication));
                Task<string?> work = shared.Value;
                try
                {
                    string? result = await work.WaitAsync(ct).ConfigureAwait(false);
                    if (result != null) AnnounceArtwork(mediaPath, result);
                    return result;
                }
                finally
                {
                    if (work.IsCompleted) Inflight.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(key, shared));
                }
            }, ct);

        private static void AnnounceArtwork(string path, string artwork)
        {
            foreach (Action<string, string> handler in ArtworkResolved?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { handler(path, artwork); } catch { }
        }

        internal static bool IsLooseCollection(string? value)
            => Regex.IsMatch(value ?? "", @"\b(?:unreleased|unrelased|unreleaseds|inediti|demos?|misc|raccolta|collection|unknown(?: album)?)\b", RegexOptions.IgnoreCase);

        private static string EmbeddedCachePath(string path)
        {
            var file = new FileInfo(path);
#if CINECORE_MAC
            // v2: le miniature dei video (non quadrate) non sono piu' copertine; le v1 gia' salvate non valgono.
            return CachePathFor($"embedded-v2|{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
#else
            return CachePathFor($"embedded-v1|{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
#endif
        }

        private static string? EmbeddedArtwork(string path, MediaProbe.AudioTags? knownTags)
        {
            string output = EmbeddedCachePath(path);
            if (File.Exists(output)) return output;
            if (EmbeddedChecked.Count > 4096) EmbeddedChecked.Clear();
            if (!EmbeddedChecked.TryAdd(output, true)) return null;
            try
            {
#if CINECORE_MAC
                byte[]? bytes = MediaProbe.ReadEmbeddedAudioArtwork(path, knownTags ?? SafeReadTags(path));
                // Brani presi da un video (YouTube "Official Video"): l'immagine incorporata e' la miniatura 16:9 del
                // video, non la copertina. Non vale come copertina dell'album: si cerca quella vera, altrimenti segnaposto.
                if (bytes == null || NormalizeMacImage(bytes, 16, coverShaped: true) is not { } jpeg) return null;
#else
                byte[]? bytes = MediaProbe.ReadEmbeddedAudioArtwork(path);
                if (bytes == null) return null;
                using var stream = new MemoryStream(bytes);
                using var image = ImageAssetDecoder.Read(stream);
                if (image.Width < 16 || image.Height < 16) return null;
#endif
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                string temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
#if CINECORE_MAC
                    File.WriteAllBytes(temp, jpeg);
#else
                    image.Save(temp, System.Drawing.Imaging.ImageFormat.Jpeg);
#endif
                    File.Move(temp, output, overwrite: true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                return output;
            }
            catch { EmbeddedChecked.TryRemove(output, out _); return null; }
        }

        private static async Task<string?> ResolveArtworkCoreAsync(string? mediaPath, CancellationToken ct, MediaProbe.AudioTags? knownTags)
        {
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                return null;

            if (FindLocalAlbumCover(mediaPath, knownTags) is { } localCover)
                return localCover;
            // Embedded artwork is local, authoritative, and independent of provider tags.
            if (EmbeddedArtwork(mediaPath, knownTags) is { } embedded) return embedded;

            var tags = knownTags ?? SafeReadTags(mediaPath);
#if CINECORE_MAC
            if (IsLooseCollection(tags.Album) || (string.IsNullOrWhiteSpace(tags.Album) &&
                IsLooseCollection(Path.GetFileName(Path.GetDirectoryName(mediaPath)))))
                return await TryTrackArtworkAsync(mediaPath, tags, ct, preferGenius: true).ConfigureAwait(false);
#endif
            if (!HasTrustedRemoteArtworkIdentity(tags))
                return await TryTrackArtworkAsync(mediaPath, tags, ct).ConfigureAwait(false);
            if (File.Exists(TrackArtworkCachePath(mediaPath, tags))) return TrackArtworkCachePath(mediaPath, tags);
            string queryKey;
            try { queryKey = BuildQueryKey(mediaPath, tags); }
            catch { queryKey = BuildQueryKey(mediaPath, new MediaProbe.AudioTags()); }

            var albumKeys = BuildAlbumKeys(mediaPath, tags).ToList();
            foreach (var albumKey in albumKeys)
            {
                string albumCache = AlbumCachePathFor(albumKey);
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

            string missKey = queryKey + "|" + MusicCatalog.Signature;
            string lookupKey = albumKeys.Count > 0 ? (HasPublicationSubtitle(tags.Album) ? "itunes-album-v5|" : "itunes-album-v4|") + albumKeys[0] : queryKey;
            if (Misses.TryGetValue(missKey, out var retryAt) && retryAt > DateTime.UtcNow) return null;
            if (RecentLookupMiss(missKey)) { Misses[missKey] = DateTime.UtcNow.AddHours(1); return null; }
            bool lookupFailed = false;
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Misses.TryGetValue(missKey, out retryAt) && retryAt > DateTime.UtcNow) return null;
                foreach (var albumKey in albumKeys)
                {
                    string albumCache = AlbumCachePathFor(albumKey);
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

                // Spotify / TIDAL (se configurati): per primi, a meno che l'utente abbia messo Deezer davanti.
                if (MusicCatalog.AskedFirst && await TryCatalogOnceAsync(mediaPath, tags, queryKey, cachePath, albumKeys, ct).ConfigureAwait(false) is { } fromCatalog)
                {
                    Memory[queryKey] = fromCatalog;
                    return fromCatalog;
                }

                if (TryReadCachedItunesLookup(lookupKey, out var cachedArtworkUrl))
                {
                    if (string.IsNullOrWhiteSpace(cachedArtworkUrl))
                    {
                        // iTunes non l'aveva trovato: prova Deezer (una sola volta per album).
                        string? deezerSaved = await TryDeezerOnceAsync(mediaPath, tags, queryKey, cachePath, albumKeys, ct).ConfigureAwait(false);
                        if (deezerSaved == null && !MusicCatalog.AskedFirst)
                            deezerSaved = await TryCatalogOnceAsync(mediaPath, tags, queryKey, cachePath, albumKeys, ct).ConfigureAwait(false);
                        if (deezerSaved == null)
                            deezerSaved = await TryGeniusArtworkOnceAsync(mediaPath, tags, queryKey, cachePath, ct).ConfigureAwait(false);
                        if (deezerSaved == null)
                            deezerSaved = await TryTrackArtworkAsync(mediaPath, tags, ct).ConfigureAwait(false);
                        Memory[queryKey] = deezerSaved;
                        return deezerSaved;
                    }

                    string? cachedSaved = await DownloadArtworkAsync(cachedArtworkUrl, cachePath, ct).ConfigureAwait(false);
                    if (cachedSaved != null)
                    {
                        foreach (string albumKey in albumKeys)
                        {
                            try { File.Copy(cachedSaved, AlbumCachePathFor(albumKey), overwrite: true); } catch { }
                        }
                        Memory[queryKey] = cachedSaved;
                        return cachedSaved;
                    }
                    // A dead cached URL must not prevent the other providers from being tried.
                }

                var terms = BuildSearchTerms(mediaPath, tags).Take(tags.Album.Length > 0 ? 1 : 2).ToList();
                if (terms.Count == 0)
                {
                    WriteCachedItunesLookup(lookupKey, null);
                    Memory[queryKey] = null;
                    return null;
                }

                string? saved = null;
                string? selectedArtworkUrl = null;
                foreach (var term in terms)
                {
                    string? artworkUrl = await SearchItunesArtworkAsync(term,
                        FirstNonEmpty(tags.AlbumArtist, tags.Artist), CleanFileName(tags.Album), ct, CleanFileName(tags.Title)).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(artworkUrl))
                        continue;

                    selectedArtworkUrl = artworkUrl;
                    saved = await DownloadArtworkAsync(artworkUrl, cachePath, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(saved))
                        break;
                }

                WriteCachedItunesLookup(lookupKey, selectedArtworkUrl);
                bool isGeniusArtwork = false;
                if (string.IsNullOrWhiteSpace(saved))
                    saved = await TryDeezerOnceAsync(mediaPath, tags, queryKey, cachePath, albumKeys, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(saved) && !MusicCatalog.AskedFirst)
                    saved = await TryCatalogOnceAsync(mediaPath, tags, queryKey, cachePath, albumKeys, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(saved))
                {
                    saved = await TryGeniusArtworkOnceAsync(mediaPath, tags, queryKey, cachePath, ct).ConfigureAwait(false);
                    isGeniusArtwork = saved != null;
                }

                if (saved == null)
                {
                    saved = await TryTrackArtworkAsync(mediaPath, tags, ct).ConfigureAwait(false);
                    isGeniusArtwork = saved != null; // This result belongs only to the song.
                }

                if (!string.IsNullOrWhiteSpace(saved) && !isGeniusArtwork)
                {
                    foreach (var albumKey in albumKeys)
                    {
                        try
                        {
                            string albumCache = AlbumCachePathFor(albumKey);
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
                lookupFailed = true;
                return null;
            }
            finally
            {
                if (!Memory.TryGetValue(queryKey, out var resolved) || string.IsNullOrWhiteSpace(resolved))
                    if (!ct.IsCancellationRequested)
                    {
                        Misses[missKey] = DateTime.UtcNow.AddMinutes(2);
                        // Solo un "non trovato" vero resta su disco; un errore di rete si ritenta al prossimo avvio.
                        if (!lookupFailed && !ProvidersUnavailable) RememberLookupMiss(missKey);
                    }
                try { Gate.Release(); } catch { }
            }
        }

        private static (string Artist, string Title) TrackArtworkIdentity(string mediaPath, MediaProbe.AudioTags tags)
        {
            string title = MusicTextIdentity.Title(FirstNonEmpty(tags.Title, Path.GetFileNameWithoutExtension(mediaPath)), filename: string.IsNullOrWhiteSpace(tags.Title));
            string artist = FirstNonEmpty(tags.Artist, tags.AlbumArtist);
            if (artist.Length == 0) artist = MusicLyricsService.InferArtistFromPath(mediaPath, ref title) ?? "";
            if (artist.Length > 0) title = Regex.Replace(title, @"^" + Regex.Escape(artist) + @"\s*[-–]\s*", "", RegexOptions.IgnoreCase);
            return (MusicTextIdentity.Artist(artist), ArtworkTrackTitle(title));
        }

        private static string ArtworkTrackTitle(string? title)
        {
            string value = MusicTextIdentity.Title(title);
            // Only discard numeric parenthetical subtitles and explicit version labels.
            // Do not shorten ordinary words or accept an arbitrary title prefix.
            value = Regex.Replace(value, @"\s*[\(\[]\s*\d+(?:\s*[,;]\s*\d+)+\s*[\)\]]", " ");
            value = Regex.Replace(value, @"\s*[\(\[][^\)\]]*(?:unreleased|snippet|demo|alternative version|alt\.? version|acoustic|live(?: version)?)[^\)\]]*[\)\]]", " ", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s+[-–]\s+(?:acoustic|live(?:\s+.*)?|alternative version|demo|remaster(?:ed)?(?:\s+\d{4})?)$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(value, @"\s+", " ").Trim();
        }

        internal static bool SameArtworkTrackTitle(string? candidate, string? wanted)
        {
            string a = MusicArtistArtworkService.Identity(ArtworkTrackTitle(candidate));
            string b = MusicArtistArtworkService.Identity(ArtworkTrackTitle(wanted));
            return a.Length > 0 && a == b;
        }

        private static string TrackArtworkCachePath(string mediaPath, MediaProbe.AudioTags tags)
        {
            var identity = TrackArtworkIdentity(mediaPath, tags);
            var file = new FileInfo(mediaPath);
            return CachePathFor($"track-art-v1|{file.FullName}|{identity.Artist}|{identity.Title}|{file.Length}|{file.LastWriteTimeUtc.Ticks}".ToLowerInvariant());
        }

        private static async Task<string?> TryTrackArtworkAsync(string mediaPath, MediaProbe.AudioTags tags, CancellationToken ct, bool preferGenius = false)
        {
            var identity = TrackArtworkIdentity(mediaPath, tags);
            if (identity.Artist.Length == 0 || identity.Title.Length == 0) return null;
            string output = TrackArtworkCachePath(mediaPath, tags);
            if (File.Exists(output)) return output;
            string miss = "song|" + output;
            if (Misses.TryGetValue(miss, out var retry) && retry > DateTime.UtcNow) return null;
            await TrackArtworkGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (File.Exists(output)) return output;
                var songTags = new MediaProbe.AudioTags { Artist = identity.Artist, Title = identity.Title };
                string songKey = "song-v1|" + output;
                if (preferGenius && await TryGeniusArtworkOnceAsync(mediaPath, songTags, songKey, output, ct).ConfigureAwait(false) is { } unreleased)
                    return unreleased;
                string? url = await SearchItunesArtworkAsync(identity.Artist + " " + identity.Title, identity.Artist, "", ct, identity.Title).ConfigureAwait(false);
                if (url != null && await DownloadArtworkAsync(url, output, ct).ConfigureAwait(false) is { } itunes) return itunes;
                url = await MusicCatalog.FindAlbumCoverUrlAsync(identity.Artist, "", identity.Title, ct).ConfigureAwait(false);
                if (url != null && await DownloadArtworkAsync(url, output, ct).ConfigureAwait(false) is { } catalog) return catalog;
                using var response = await Http.GetAsync("https://api.deezer.com/search/track?limit=15&q=" + Uri.EscapeDataString(identity.Artist + " " + identity.Title), ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        foreach (var track in data.EnumerateArray())
                            if (SameArtworkTrackTitle(ReadJsonString(track, "title"), identity.Title) &&
                                track.TryGetProperty("artist", out var artist) && SameGeniusArtworkArtist(ReadJsonString(artist, "name"), identity.Artist) &&
                                track.TryGetProperty("album", out var album) && ReadJsonString(album, "cover_xl") is { Length: > 0 } cover &&
                                await DownloadArtworkAsync(cover, output, ct).ConfigureAwait(false) is { } deezer) return deezer;
                }
                url = await SearchItunesArtworkAsync(identity.Artist + " " + identity.Title, identity.Artist, "", ct, identity.Title, allowTitlePrefix: true).ConfigureAwait(false);
                if (url != null && await DownloadArtworkAsync(url, output, ct).ConfigureAwait(false) is { } prefixed) return prefixed;
                if (!preferGenius && await TryGeniusArtworkOnceAsync(mediaPath, songTags, songKey, output, ct).ConfigureAwait(false) is { } genius) return genius;
                Misses[miss] = DateTime.UtcNow.AddMinutes(15);
                return null;
            }
            finally { TrackArtworkGate.Release(); }
        }

        // Spotify / TIDAL: un tentativo per album e per insieme di fonti configurate, ricordato con
        // un file ".catalog". Il suffisso v3 invalida cache e mancati risultati del matching permissivo.
        private static async Task<string?> TryCatalogOnceAsync(string mediaPath, MediaProbe.AudioTags tags, string queryKey, string cachePath, List<string> albumKeys, CancellationToken ct)
        {
            string signature = MusicCatalog.Signature;
            if (signature.Length == 0) return null;
            string marker = CachePathFor((HasPublicationSubtitle(tags.Album) ? "catalog-v5|" : "catalog-v4|") + signature + "|" + (albumKeys.FirstOrDefault() ?? queryKey)) + ".catalog";
            if (RecentMarker(marker)) return null;
            try
            {
                string artist = FirstNonEmpty(tags.AlbumArtist, tags.Artist);
                string album = CleanFileName(tags.Album);
                string title = CleanFileName(tags.Title);
                string? url = await MusicCatalog.FindAlbumCoverUrlAsync(artist, album, title, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(url) && await DownloadArtworkAsync(url, cachePath, ct).ConfigureAwait(false) is { } saved)
                {
                    foreach (var albumKey in albumKeys)
                    {
                        try { string albumCache = AlbumCachePathFor(albumKey); if (!File.Exists(albumCache)) File.Copy(saved, albumCache, overwrite: false); } catch { }
                    }
                    return saved;
                }
            }
            catch (OperationCanceledException) { throw; }
            // Errore di rete: niente segno "gia' cercato", si riprova quando la connessione torna.
            catch { return null; }
            try { File.WriteAllText(marker, DateTime.UtcNow.ToString("O")); } catch { }
            return null;
        }

        // Genius è il fallback per copertine di demo e inediti assenti dai cataloghi commerciali.
        // Il risultato viene legato al titolo esatto e all'artista del brano.
        private static async Task<string?> TryGeniusArtworkOnceAsync(string mediaPath, MediaProbe.AudioTags tags, string queryKey, string cachePath, CancellationToken ct)
        {
            // A song-page image cannot verify the edition of a named album.
            if (!string.IsNullOrWhiteSpace(tags.Album) && !IsLooseCollection(tags.Album)) return null;
            string marker = CachePathFor("genius-art-v4|" + queryKey) + ".genius";
            if (RecentMarker(marker)) return null;

            string title = CleanFileName(tags.Title);
            if (title.Length == 0) title = CleanFileName(Path.GetFileNameWithoutExtension(mediaPath));
            string artist = FirstNonEmpty(tags.Artist, tags.AlbumArtist);
            if (artist.Length == 0)
                artist = MusicLyricsService.InferArtistFromPath(mediaPath, ref title) ?? string.Empty;
            string album = CleanFileName(tags.Album);
            title = Regex.Replace(title, @"\s*[\(\[][^\)\]]*(?:unreleased|snippet|demo|leak|alternative version|alt\.? version)[^\)\]]*[\)\]]", " ", RegexOptions.IgnoreCase).Trim();
            if (title.Length == 0 || artist.Length == 0) { WriteGeniusMarker(marker); return null; }

            var terms = new List<string>();
            void AddTerm(string value)
            {
                value = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
                if (value.Length > 0 && !terms.Contains(value, StringComparer.OrdinalIgnoreCase)) terms.Add(value);
            }
            if (artist.Length > 0) AddTerm(artist + " " + title);
            if (album.Length > 0) AddTerm(album + " " + title);
            AddTerm(title);

            try
            {
                foreach (string term in terms.Take(3))
                {
                    string? searchJson = await GeniusClient.GetTextAsync("https://genius.com/api/search/song?per_page=10&q=" + Uri.EscapeDataString(term), ct).ConfigureAwait(false);
                    if (searchJson == null) return null;
                    using var document = JsonDocument.Parse(searchJson);
                    if (!document.RootElement.TryGetProperty("response", out var root) || !root.TryGetProperty("sections", out var sections)) continue;

                    var matches = new List<(string ImageUrl, string SongUrl, int Score)>();
                    int resultCount = 0, exactSongCount = 0, directImageCount = 0;
                    foreach (var section in sections.EnumerateArray())
                    {
                        if (!section.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) continue;
                        resultCount += hits.GetArrayLength();
                        foreach (var hit in hits.EnumerateArray())
                        {
                            if (!hit.TryGetProperty("result", out var result)) continue;
                            if (!SameArtworkTrackTitle(ReadJsonString(result, "title"), title)) continue;
                            string candidateArtist = result.TryGetProperty("primary_artist", out var primary)
                                ? ReadJsonString(primary, "name") : ReadJsonString(result, "artist_names");
                            if (artist.Length > 0 && !SameGeniusArtworkArtist(candidateArtist, artist)) continue;
                            string imageUrl = ReadJsonString(result, "song_art_image_url");
                            if (imageUrl.Length == 0) imageUrl = ReadJsonString(result, "song_art_image_thumbnail_url");
                            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri) || imageUri.Scheme != "https" ||
                                !string.Equals(imageUri.Host, "images.genius.com", StringComparison.OrdinalIgnoreCase)) imageUrl = string.Empty;
                            exactSongCount++;
                            if (imageUrl.Length > 0) directImageCount++;
                            string songUrl = ReadJsonString(result, "url");
                            if (!IsGeniusSongUrl(songUrl) && imageUrl.Length == 0) continue;
                            int score = (artist.Length > 0 ? 2 : 0) + (IsGeniusSongUrl(songUrl) ? 1 : 0);
                            matches.Add((imageUrl, songUrl, score));
                        }
                    }
                    Dbg.Log($"[MUSIC ART][GENIUS] title='{title}' search hits={resultCount}, exact songs={exactSongCount}, direct images={directImageCount}", Dbg.LogLevel.Info);
                    foreach (var match in matches.OrderByDescending(match => match.Score))
                    {
                        if (match.ImageUrl.Length > 0 && await DownloadGeniusArtworkAsync(match.ImageUrl, cachePath, ct).ConfigureAwait(false) is { } directSaved)
                            return directSaved;
                        if (match.SongUrl.Length == 0) continue;
                        string? pageImage = await GeniusSongPageArtworkUrlAsync(match.SongUrl, ct).ConfigureAwait(false);
                        if (pageImage != null && await DownloadGeniusArtworkAsync(pageImage, cachePath, ct).ConfigureAwait(false) is { } pageSaved)
                            return pageSaved;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Dbg.Warn($"[MUSIC ART][GENIUS] artwork lookup failed for title='{title}': {ex.Message}");
                // Errore di rete: si riprova al ritorno della connessione (niente segno "gia' cercato").
                if (ex is System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException or TimeoutException) return null;
            }

            Dbg.Log($"[MUSIC ART][GENIUS] no usable artwork found for title='{title}'", Dbg.LogLevel.Info);
            WriteGeniusMarker(marker);
            return null;
        }

        private static async Task<string?> DownloadGeniusArtworkAsync(string imageUrl, string cachePath, CancellationToken ct)
        {
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("images.genius.com", StringComparison.OrdinalIgnoreCase)) return null;
            string temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".genius.tmp";
            string stage = "request setup";
            string? contentType = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.Clear();
                request.Headers.UserAgent.ParseAdd(GeniusArtworkBrowserAgent);
                request.Headers.Accept.ParseAdd("image/jpeg,image/png,image/gif,*/*;q=0.2");
                request.Headers.Referrer = new Uri("https://genius.com/");
                stage = "HTTP response";
                using var response = await GeniusHttp.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Dbg.Warn($"[MUSIC ART][GENIUS] image returned HTTP {(int)response.StatusCode}");
                    return null;
                }
                contentType = response.Content.Headers.ContentType?.MediaType;
                if (response.Content.Headers.ContentLength is > 16 * 1024 * 1024) return null;
                stage = "read image data";
                byte[] data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (data.Length < 128 || data.Length > 16 * 1024 * 1024) return null;
                stage = "decode image data";
                data = NormalizeGeniusArtwork(data) ?? Array.Empty<byte>();
                if (data.Length == 0) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                stage = "write cache file";
                await File.WriteAllBytesAsync(temporary, data, ct).ConfigureAwait(false);
                File.Move(temporary, cachePath, overwrite: true);
                return cachePath;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Dbg.Warn($"[MUSIC ART][GENIUS] image download failed at {stage}: {ex.GetType().Name} ({contentType ?? "unknown content type"})");
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                return null;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static byte[]? NormalizeGeniusArtwork(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data);
                using var image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
                return image.Width >= 24 && image.Height >= 24 ? data : null;
            }
            catch
            {
                // Genius serves WebP for some song-art URLs. The app's GDI+ image path
                // cannot decode WebP, but SkiaSharp is already part of the player.
                try
                {
                    using var bitmap = SkiaSharp.SKBitmap.Decode(data);
                    if (bitmap == null || bitmap.Width < 24 || bitmap.Height < 24) return null;
                    using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
                    using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
                    return encoded?.ToArray();
                }
                catch { return null; }
            }
        }

        private static bool IsGeniusSongUrl(string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            return uri.Host.Equals("genius.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".genius.com", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string?> GeniusSongPageArtworkUrlAsync(string songUrl, CancellationToken ct)
        {
            if (!IsGeniusSongUrl(songUrl)) return null;
            string? html = await GeniusClient.GetTextAsync(songUrl, ct).ConfigureAwait(false);
            if (html == null || html.Length > 4 * 1024 * 1024) return null;
            foreach (Match meta in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                string tag = meta.Value;
                var property = Regex.Match(tag, @"\b(?:property|name)\s*=\s*[\""'](?<value>og:image|twitter:image)[\""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!property.Success) continue;
                var content = Regex.Match(tag, @"\bcontent\s*=\s*[\""'](?<value>[^\""']+)[\""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!content.Success) continue;
                string imageUrl = WebUtility.HtmlDecode(content.Groups["value"].Value);
                if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
                    uri.Host.Equals("images.genius.com", StringComparison.OrdinalIgnoreCase)) return uri.AbsoluteUri;
            }
            Dbg.Log("[MUSIC ART][GENIUS] song page had no usable Open Graph image", Dbg.LogLevel.Info);
            return null;
        }

        private static string ReadJsonString(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;

        private static string GeniusArtworkText(string? value)
        {
            string text = Regex.Replace(value ?? string.Empty,
                @"\s*[\(\[][^\)\]]*(?:unreleased|snippet|demo|leak|alternative version|alt\.? version)[^\)\]]*[\)\]]", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s+(?:feat\.?|ft\.?|featuring)\s+.+$", " ", RegexOptions.IgnoreCase);
            text = text.Replace("&", " and ", StringComparison.Ordinal);
            return MusicArtistArtworkService.Identity(text);
        }

        internal static bool SameArtworkAlbumTitle(string? candidate, string? wanted)
        {
            string a = MusicArtistArtworkService.Identity(NormalizePublicationTitle(candidate));
            string b = MusicArtistArtworkService.Identity(NormalizePublicationTitle(wanted));
            return a.Length > 0 && b.Length > 0 && a == b;
        }

        internal static string NormalizePublicationTitle(string? title)
            => Regex.Replace(Regex.Replace(title ?? "", @"\s*[\(\[](?:(?:official|original)\s+(?:soundtrack|score)|remaster(?:ed)?(?:\s+\d{4})?)[\)\]]", " ", RegexOptions.IgnoreCase),
                @"\s+(?:official|original)\s+(?:soundtrack|score)\s*$", "", RegexOptions.IgnoreCase).Trim();

        private static bool SameGeniusArtworkArtist(string candidate, string wanted)
        {
            string a = MusicArtistArtworkService.Identity(candidate), b = MusicArtistArtworkService.Identity(wanted);
            return a.Length > 0 && b.Length > 0 && a == b;
        }

        private static void WriteGeniusMarker(string marker)
        {
            try { File.WriteAllText(marker, DateTime.UtcNow.ToString("O")); } catch { }
        }

        // Deezer: catalogo ampio (colonne sonore, videogiochi). Un tentativo per album,
        // ricordato con un file ".deezer" per non ripetere la ricerca a ogni avvio.
        private static async Task<string?> TryDeezerOnceAsync(string mediaPath, MediaProbe.AudioTags tags, string queryKey, string cachePath, List<string> albumKeys, CancellationToken ct)
        {
            string marker = CachePathFor("deezer-v3|" + (albumKeys.FirstOrDefault() ?? queryKey)) + ".deezer";
            if (RecentMarker(marker)) return null;
            try
            {
                foreach (var term in BuildSearchTerms(mediaPath, tags).Take(tags.Album.Length > 0 ? 1 : 2))
                {
                    string url = "https://api.deezer.com/search/album?limit=5&q=" + Uri.EscapeDataString(term);
                    using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
                    foreach (var album in data.EnumerateArray())
                    {
                        string candidateAlbum = ReadJsonString(album, "title");
                        string candidateArtist = album.TryGetProperty("artist", out var artistElement)
                            ? ReadJsonString(artistElement, "name") : string.Empty;
                        string wantedAlbum = CleanFileName(tags.Album);
                        string wantedArtist = FirstNonEmpty(tags.AlbumArtist, tags.Artist);
                        // Album search cannot identify a song whose album tag is missing.
                        if (wantedAlbum.Length == 0 || !SameArtworkAlbumTitle(candidateAlbum, wantedAlbum)) continue;
                        if (wantedArtist.Length > 0 && !SameGeniusArtworkArtist(candidateArtist, wantedArtist)) continue;
                        string? cover = album.TryGetProperty("cover_xl", out var xl) ? xl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(cover)) continue;
                        string? saved = await DownloadArtworkAsync(cover, cachePath, ct).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(saved)) continue;
                        foreach (var albumKey in albumKeys)
                        {
                            try { string albumCache = AlbumCachePathFor(albumKey); if (!File.Exists(albumCache)) File.Copy(saved, albumCache, overwrite: false); } catch { }
                        }
                        return saved;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            // Errore di rete: niente segno "gia' cercato", si riprova quando la connessione torna.
            catch { return null; }
            try { File.WriteAllText(marker, DateTime.UtcNow.ToString("O")); } catch { }
            return null;
        }

        private static async Task<string?> SearchItunesArtworkAsync(string term, string expectedArtist, string expectedAlbum, CancellationToken ct, string expectedTitle = "", bool allowTitlePrefix = false)
        {
            // Titolo locale piu' corto di quello pubblicato ("All My Demons" -> "All My Demons Greeting Me As A Friend"):
            // vale solo con l'artista uguale e solo se nessun titolo coincide; vince il titolo pubblicato piu' corto.
            string? prefixBest = null; int prefixLength = int.MaxValue;
            static string Words(string? value) => Regex.Replace(Regex.Replace((value ?? "").Normalize(System.Text.NormalizationForm.FormD).ToUpperInvariant(), @"[^\p{L}\p{N}]+", " "), @"\s+", " ").Trim();
            string wantedIdentity = Words(ArtworkTrackTitle(expectedTitle));
            foreach (var country in new[] { "IT", "US" })
            {
                foreach (var entity in new[] { "album", "song" })
                {
                    if (expectedAlbum.Length == 0 && entity == "album") continue;
                    string url = "https://itunes.apple.com/search"
                        + "?media=music&entity=" + Uri.EscapeDataString(entity) + (allowTitlePrefix ? "&limit=25" : "&limit=8")
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

                        if (expectedArtist.Length > 0 && !SameGeniusArtworkArtist(ReadJsonString(item, "artistName"), expectedArtist))
                            continue;
                        if (expectedAlbum.Length > 0 && !SameArtworkAlbumTitle(ReadJsonString(item, "collectionName"), expectedAlbum))
                            continue;
                        if (expectedAlbum.Length == 0 && (expectedTitle.Length == 0 || !SameArtworkTrackTitle(ReadJsonString(item, "trackName"), expectedTitle)))
                        {
                            if (allowTitlePrefix && expectedAlbum.Length == 0 && wantedIdentity.Contains(' ') && expectedArtist.Length > 0)
                            {
                                string published = Words(ArtworkTrackTitle(ReadJsonString(item, "trackName")));
                                if (published.StartsWith(wantedIdentity + " ", StringComparison.Ordinal) && published.Length < prefixLength)
                                { prefixLength = published.Length; prefixBest = PreferLargerArtworkUrl(artwork); }
                            }
                            continue;
                        }

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
                            if (expectedAlbum.Length == 0) Dbg.Log($"[MUSIC ART] song match: '{ReadJsonString(item, "trackName")}' by '{ReadJsonString(item, "artistName")}'");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(best))
                        return best;
                }
            }

            if (prefixBest != null) Dbg.Log($"[MUSIC ART] title-prefix match for '{expectedTitle}' by '{expectedArtist}'");
            return prefixBest;
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
                    if (DateTime.UtcNow.Ticks - entry.UpdatedUtcTicks > TimeSpan.FromHours(entry.ArtworkUrl == null ? 1 : 24 * 7).Ticks)
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
                tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

                byte[] data = await Http.GetByteArrayAsync(artworkUrl, ct).ConfigureAwait(false);
                if (data.Length < 128)
                    return null;

#if CINECORE_MAC
                if (NormalizeMacImage(data, 24) is not { } jpeg) return null;
                data = jpeg;
#else
                using (var ms = new MemoryStream(data))
                using (var img = ImageAssetDecoder.Load(ms))
                {
                    if (img.Width < 24 || img.Height < 24)
                        return null;
                    using var normalized = new MemoryStream();
                    img.Save(normalized, System.Drawing.Imaging.ImageFormat.Jpeg);
                    data = normalized.ToArray();
                }
#endif

                await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
                File.Move(tmp, cachePath, overwrite: true);
                return cachePath;
            }
            catch
            {
                try { if (!string.IsNullOrWhiteSpace(tmp) && File.Exists(tmp)) File.Delete(tmp); } catch { }
                return null;
            }
        }

#if CINECORE_MAC
        private static byte[]? NormalizeMacImage(byte[] bytes, int minimumSide, bool coverShaped = false)
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap == null || bitmap.Width < minimumSide || bitmap.Height < minimumSide) return null;
            if (coverShaped)
            {
                double aspect = bitmap.Width / (double)bitmap.Height;
                if (aspect < .8 || aspect > 1.25) return null;
            }
            using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92);
            return jpeg?.ToArray();
        }
#endif

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
                string version = HasPublicationSubtitle(tags.Album) ? "music-art-v6" : "music-art-v5";
                return $"{version}|{fi.FullName}|{FirstNonEmpty(tags.AlbumArtist, tags.Artist)}|{tags.Title}|{tags.Album}|{term}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}".ToLowerInvariant();
            }
            catch
            {
                string version = HasPublicationSubtitle(tags.Album) ? "music-art-v6" : "music-art-v5";
                return $"{version}|{mediaPath}|{FirstNonEmpty(tags.AlbumArtist, tags.Artist)}|{tags.Title}|{tags.Album}|{term}".ToLowerInvariant();
            }
        }

        private static bool HasPublicationSubtitle(string? title) => Regex.IsMatch(title ?? "", @"\s+-\s+");
        private static string AlbumCachePathFor(string albumKey)
            => CachePathFor((HasPublicationSubtitle(albumKey) ? "album-v5|" : "album-v4|") + albumKey);

        private static IEnumerable<string> BuildAlbumKeys(string mediaPath, MediaProbe.AudioTags tags)
        {
            string album = CleanMusicTitle(tags.Album);
            string artist = CleanMusicTitle(FirstNonEmpty(tags.AlbumArtist, tags.Artist));
            if (!string.IsNullOrWhiteSpace(album) && !IsLooseCollection(album))
            {
                yield return (artist + "|" + album).Trim('|').ToLowerInvariant();
            }
        }

        // Una cartella o un nome file non identificano in modo affidabile una pubblicazione.
        // Le richieste remote partono soltanto da tag coerenti: artista e titolo o album.
        internal static bool HasTrustedRemoteArtworkIdentity(MediaProbe.AudioTags tags)
        {
            if (tags == null) return false;
            string artist = CleanFileName(FirstNonEmpty(tags.AlbumArtist, tags.Artist));
            string album = CleanFileName(tags.Album);
            string title = CleanFileName(tags.Title);
            return artist.Length > 0 && (album.Length > 0 || title.Length > 0);
        }

        private static MediaProbe.AudioTags SafeReadTags(string mediaPath)
        {
            try
            {
                var file = new FileInfo(mediaPath);
                long length = file.Length, written = file.LastWriteTimeUtc.Ticks;
                if (TagCache.TryGetValue(mediaPath, out var cached) && cached.Length == length && cached.Written == written)
                    return cached.Tags;
                var tags = MediaProbe.ReadAudioTags(mediaPath);
                if (TagCache.Count > 4096) TagCache.Clear();
                TagCache[mediaPath] = new(length, written, tags);
                return tags;
            }
            catch { return new MediaProbe.AudioTags(); }
        }

        private static bool RecentMarker(string path)
            => File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(15);

        private static string CachePathFor(string queryKey)
        {
#if CINECORE_MAC
            string folder = Path.Combine(CinecoreMac.LibraryStore.AppDataDir, "music-artwork");
#else
            string folder = Path.Combine(
                global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025",
                "music-artwork");
#endif
            return Path.Combine(folder, Sha256(queryKey) + ".jpg");
        }

        private static string LookupCachePath()
        {
#if CINECORE_MAC
            string folder = Path.Combine(CinecoreMac.LibraryStore.AppDataDir, "music-artwork");
#else
            string folder = Path.Combine(
                global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025",
                "music-artwork");
#endif
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
