#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private static readonly SemaphoreSlim DisplayImageDecodeGate = new(3, 3);
        private static readonly SemaphoreSlim NetworkImageDownloadGate = new(3, 3);
        private readonly Dictionary<string, DateTime> _networkArtRetryAfter = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, DateTime> _decodeRetryAfter = new(StringComparer.OrdinalIgnoreCase);

        private Image? LoadImage(string path)
        {
            try
            {
                lock (_imageCacheSync)
                {
                    if (_imageCache.TryGetValue(path, out var cached))
                        return cached;
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var src = Image.FromStream(fs);
                var clone = new Bitmap(src);
                lock (_imageCacheSync)
                    _imageCache[path] = clone;
                return clone;
            }
            catch
            {
                return null;
            }
        }

        private string? ResolveDisplayImagePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string value = path.Trim();
            if (File.Exists(value))
                return value;

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            string cache = NetworkArtworkCachePath(value);
            if (File.Exists(cache))
                return cache;

            QueueNetworkArtworkDownload(value, cache);
            return null;
        }

        private void QueueNetworkArtworkDownload(string url, string cachePath)
        {
            lock (_networkArtSync)
            {
                if (IsDisposed || Disposing ||
                    _networkArtRetryAfter.TryGetValue(url, out var retryAfter) && retryAfter > DateTime.UtcNow)
                    return;
                if (_networkArtDownloads.Count >= 12) return;
                if (!_networkArtDownloads.Add(url))
                    return;
            }

            Task.Run(async () =>
            {
                string? temp = null;
                bool entered = false, succeeded = false;
                try
                {
                    await NetworkImageDownloadGate.WaitAsync().ConfigureAwait(false);
                    entered = true;
                    if (IsDisposed || Disposing) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    byte[]? bytes = await JellyfinClient.TryDownloadArtworkAsync(url).ConfigureAwait(false);
                    if (bytes == null)
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.TryAddWithoutValidation("User-Agent", "CinecorePlayer/1.0 DLNA");
                        req.Headers.TryAddWithoutValidation("X-AV-Client-Info", "CinecorePlayer/1.0");
                        using var res = await NetworkArtworkHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                        res.EnsureSuccessStatusCode();
                        bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                    if (bytes.Length < 64)
                        return;

                    // Do not cache an HTML error page or an unsupported image forever.
                    using (var stream = new MemoryStream(bytes))
                    using (var validation = ImageAssetDecoder.Read(stream)) { }
                    temp = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, cachePath, true);
                    succeeded = true;
                }
                catch { }
                finally
                {
                    lock (_networkArtSync)
                    {
                        _networkArtDownloads.Remove(url);
                        if (!succeeded) _networkArtRetryAfter[url] = DateTime.UtcNow.AddMinutes(5);
                        else _networkArtRetryAfter.Remove(url);
                    }
                    if (entered) NetworkImageDownloadGate.Release();
                    if (temp != null) { try { File.Delete(temp); } catch { } }
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                            QueueCoalescedImageRefresh();
                    }
                    catch { }
                }
            });
        }

        private static string NetworkArtworkCachePath(string url)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
            string key = Convert.ToHexString(hash).ToLowerInvariant();
            string ext = ".jpg";
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    string candidate = Path.GetExtension(uri.AbsolutePath);
                    if (candidate is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp")
                        ext = candidate;
                }
            }
            catch { }

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "network-art");
            return Path.Combine(dir, key + ext);
        }

        private readonly Dictionary<string, string> _displayFallbackKeys = new(StringComparer.OrdinalIgnoreCase);

        private Image? LoadImageForDisplay(string path, int desiredMaxSide)
        {
            int bucket = Math.Max(192, Math.Min(1600, ((Math.Max(1, desiredMaxSide) + 127) / 128) * 128));
            string cacheKey = $"scaled:{bucket}:{path}";
            lock (_imageCacheSync)
            {
                if (_imageCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            QueueDisplayImageDecode(path, bucket, cacheKey);
            lock (_imageCacheSync)
            {
                // Reuse an already decoded size while the larger detail artwork
                // loads. Opening a sheet must not replace artwork with a dark slab.
                if (_displayFallbackKeys.TryGetValue(path, out var fallbackKey) && _imageCache.TryGetValue(fallbackKey, out var fallback)) return fallback;
                return _imageCache.TryGetValue(path, out var original) ? original : null;
            }
        }

        /// <summary>
        /// True when the image for <paramref name="desiredMaxSide"/> is decoded at its own size,
        /// not served by a fallback of another size while it loads.
        /// </summary>
        private bool IsDisplayImageExact(string? path, int desiredMaxSide)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            int bucket = Math.Max(192, Math.Min(1600, ((Math.Max(1, desiredMaxSide) + 127) / 128) * 128));
            lock (_imageCacheSync)
                return _imageCache.ContainsKey($"scaled:{bucket}:{path}");
        }

        private void QueueDisplayImageDecode(string path, int bucket, string cacheKey)
        {
            lock (_imageCacheSync)
            {
                if (_imageCache.ContainsKey(cacheKey) ||
                    _imageLoadRequests.Contains(cacheKey) ||
                    (_decodeRetryAfter.TryGetValue(cacheKey, out var retryAt) && retryAt > DateTime.UtcNow))
                {
                    return;
                }

                _imageLoadRequests.Add(cacheKey);
            }

            _ = Task.Run(async () =>
            {
                Bitmap? decoded = null;
                bool gateEntered = false;
                try
                {
                    await DisplayImageDecodeGate.WaitAsync().ConfigureAwait(false);
                    gateEntered = true;

                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var src = ImageAssetDecoder.Read(fs);

                    int maxSide = Math.Max(src.Width, src.Height);
                    if (maxSide <= 0)
                        return;

                    double scale = Math.Min(1.0, bucket / (double)maxSide);
                    int w = Math.Max(1, (int)Math.Round(src.Width * scale));
                    int h = Math.Max(1, (int)Math.Round(src.Height * scale));
                    decoded = new Bitmap(w, h);
                    using (var g = Graphics.FromImage(decoded))
                    {
                        g.CompositingQuality = CompositingQuality.HighSpeed;
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(src, new Rectangle(0, 0, w, h));
                    }
                }
                catch
                {
                    lock (_imageCacheSync)
                        _decodeRetryAfter[cacheKey] = DateTime.UtcNow.AddSeconds(3);
                }
                finally
                {
                    if (gateEntered)
                        DisplayImageDecodeGate.Release();

                    lock (_imageCacheSync)
                    {
                        _imageLoadRequests.Remove(cacheKey);
                        if (decoded != null && !IsDisposed && !Disposing)
                        {
                            if (_imageCache.TryGetValue(cacheKey, out var previous))
                            {
                                try { previous.Dispose(); } catch { }
                            }
                            _imageCache[cacheKey] = decoded;
                            _displayFallbackKeys[path] = cacheKey;
                            decoded = null;
                        }
                    }

                    try { decoded?.Dispose(); } catch { }
                    try
                    {
                        if (!IsDisposed && !Disposing && IsHandleCreated)
                            QueueCoalescedImageRefresh();
                    }
                    catch { }
                }
            });
        }

        private void QueueCoalescedImageRefresh()
        {
            if (Interlocked.Exchange(ref _imageUiRefreshQueued, 1) != 0)
                return;

            _ = Task.Run(async () =>
            {
                await Task.Delay(55).ConfigureAwait(false);
                try
                {
                    if (IsDisposed || Disposing || !IsHandleCreated)
                    {
                        Interlocked.Exchange(ref _imageUiRefreshQueued, 0);
                        return;
                    }

                    BeginInvoke(new Action(() =>
                    {
                        Interlocked.Exchange(ref _imageUiRefreshQueued, 0);
                        // Un'immagine nuova e' pronta: le miniature provvisorie (segnaposto o
                        // risoluzione ridotta) si scartano, cosi' il ridisegno usa subito quella vera.
                        foreach (var card in _provisionalCards.Values) card.Bitmap.Dispose();
                        _provisionalCards.Clear();
                        Invalidate();
                    }));
                }
                catch
                {
                    Interlocked.Exchange(ref _imageUiRefreshQueued, 0);
                }
            });
        }

        private string? GetCachedVideoThumbnailPath(string path)
        {
            try
            {
                string cache = VideoThumbnailPath(path);
                return File.Exists(cache) ? cache : null;
            }
            catch
            {
                return null;
            }
        }

        private void QueueVideoThumbnail(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string cache;
            try
            {
                cache = VideoThumbnailPath(path);
                if (File.Exists(cache))
                    return;
            }
            catch
            {
                return;
            }

            lock (_videoThumbnailRequests)
            {
                if (_videoThumbnailRequests.Contains(path) || _videoThumbnailFailures.Contains(path))
                    return;
                _videoThumbnailRequests.Add(path);
            }

            Task.Run(async () =>
            {
                bool ok = false;
                try
                {
                    await VideoThumbnailGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (File.Exists(cache))
                        {
                            ok = true;
                            return;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(cache) ?? AppDataDir);
                        using var th = new Thumbnailer();
                        th.Open(path);
                        using Bitmap? frame = th.Get(4.0, maxW: 720, realtime: true)
                                             ?? th.Get(1.0, maxW: 720, realtime: true)
                                             ?? th.Get(10.0, maxW: 720, realtime: true);
                        if (frame == null || IsMostlyDark(frame))
                            return;

                        frame.Save(cache, System.Drawing.Imaging.ImageFormat.Jpeg);
                        ok = true;
                    }
                    finally
                    {
                        try { VideoThumbnailGate.Release(); } catch { }
                    }
                }
                catch { }
                finally
                {
                    lock (_videoThumbnailRequests)
                    {
                        _videoThumbnailRequests.Remove(path);
                        if (!ok)
                            _videoThumbnailFailures.Add(path);
                    }

                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    Invalidate();
                                }
                                catch { }
                            }));
                    }
                    catch { }
                }
            });
        }

        private void QueueVideoQualityProbe(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            if (TryGetCachedVideoQuality(path) != null)
                return;

            lock (_videoQualityRequests)
            {
                if (_videoQualityRequests.Contains(path) || _videoQualityFailures.Contains(path))
                    return;
                // Le analisi passano una alla volta (VideoQualityGate): la coda puo' contenere tutta
                // la libreria. Con il limite a 12 i film oltre i primi restavano senza durata ne'
                // etichette finche' la pagina non veniva ricostruita.
                if (_videoQualityRequests.Count >= 2000)
                    return;
                _videoQualityRequests.Add(path);
            }

            Task.Run(async () =>
            {
                bool ok = false;
                try
                {
                    await VideoQualityGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // Mai durante un film: l'analisi legge dallo stesso disco (spesso di rete).
                        while (MusicLyricsService.VideoPlaybackBusy && !IsDisposed)
                            await Task.Delay(3000).ConfigureAwait(false);
                        ok = TryProbeVideoQuality(path) != null;
                    }
                    finally
                    {
                        try { VideoQualityGate.Release(); } catch { }
                    }
                }
                catch { }
                finally
                {
                    lock (_videoQualityRequests)
                    {
                        _videoQualityRequests.Remove(path);
                        if (!ok)
                            _videoQualityFailures.Add(path);
                    }

                    if (ok)
                    {
                        try
                        {
                            if (!IsDisposed && IsHandleCreated)
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    try
                                    {
                                        RefreshSingleItemQuality(path);
                                    }
                                    catch { }
                                }));
                            }
                        }
                        catch { }
                    }
                }
            });
        }

        private void RefreshSingleItemQuality(string path)
        {
            _itemCache.TryRemove(path, out LibraryItem? previous);
            LibraryItem? updated = ToItem(path);
            if (updated == null)
            {
                Invalidate();
                return;
            }
            // L'elemento ricostruito non deve perdere cast, recensioni e trama gia' arrivati: la
            // scheda aperta li vedeva sparire e ricomparire un secondo dopo.
            LibraryItem? rich = _detailItem != null && string.Equals(_detailItem.Path, path, StringComparison.OrdinalIgnoreCase) && _detailItem.RichDetailsResolved
                ? _detailItem
                : previous is { RichDetailsResolved: true } ? previous : _items.FirstOrDefault(item => item.RichDetailsResolved && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            if (rich != null)
            {
                if (!string.IsNullOrWhiteSpace(rich.Overview)) updated.Overview = rich.Overview;
                if (rich.Genres.Count > 0) updated.Genres = rich.Genres;
                updated.Tagline = rich.Tagline;
                updated.Director = rich.Director;
                updated.Rating = rich.Rating;
                updated.CastMembers = rich.CastMembers;
                updated.Reviews = rich.Reviews;
                updated.TmdbId = rich.TmdbId;
                updated.ImdbId = rich.ImdbId;
                updated.ReviewMediaType = rich.ReviewMediaType;
                updated.RichDetailsResolved = true;
            }

            static void ReplaceIn(List<LibraryItem> list, string targetPath, LibraryItem replacement)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    LibraryItem existing = list[i];
                    if (string.Equals(existing.Path, targetPath, StringComparison.OrdinalIgnoreCase))
                        list[i] = replacement;
                    if (!existing.IsGroup) continue;
                    for (int child = 0; child < existing.Children.Count; child++)
                    {
                        if (string.Equals(existing.Children[child].Path, targetPath, StringComparison.OrdinalIgnoreCase))
                            existing.Children[child] = replacement;
                    }
                }
            }

            ReplaceIn(_items, path, updated);
            foreach (ContentSnapshot snapshot in _contentSnapshots.Values)
                ReplaceIn(snapshot.Items, path, updated);
            if (string.Equals(_detailItem?.Path, path, StringComparison.OrdinalIgnoreCase)) _detailItem = updated;
            if (string.Equals(_heroItem?.Path, path, StringComparison.OrdinalIgnoreCase)) _heroItem = updated;
            Invalidate();
        }

        private static bool IsMostlyDark(Bitmap bmp)
        {
            try
            {
                int dark = 0;
                int total = 0;
                int stepX = Math.Max(1, bmp.Width / 10);
                int stepY = Math.Max(1, bmp.Height / 10);
                for (int y = stepY / 2; y < bmp.Height; y += stepY)
                {
                    for (int x = stepX / 2; x < bmp.Width; x += stepX)
                    {
                        Color c = bmp.GetPixel(x, y);
                        int lum = (c.R + c.G + c.B) / 3;
                        total++;
                        if (lum < 20)
                            dark++;
                    }
                }
                return total > 0 && dark / (double)total > 0.90;
            }
            catch
            {
                return false;
            }
        }

        private static string VideoThumbnailPath(string path)
        {
            // Il vecchio nome includeva LastWriteTimeUtc del media: ogni paint faceva
            // quindi I/O sul disco della libreria. Il path normalizzato è stabile e rende
            // il lookup della miniatura una semplice lettura della cache locale.
            string key;
            try { key = NormalizeRootPath(path); }
            catch { key = path; }
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            string name = Convert.ToHexString(hash).ToLowerInvariant() + ".jpg";
            return Path.Combine(AppDataDir, "cinematicVideoThumbs", name);
        }

        private void TrimImageCache()
        {
            // Cards and resampled artwork are rebuilt from the images kept below; a reload may
            // also have replaced a poster file behind an unchanged path.
            ClearRenderedCaches();
            try
            {
                var keep = new HashSet<string>(
                    _items.Take(80)
                        .SelectMany(i => new[] { i.ArtPath ?? string.Empty, i.WideArtPath ?? string.Empty })
                        .Where(p => !string.IsNullOrWhiteSpace(p)),
                    StringComparer.OrdinalIgnoreCase);
                lock (_imageCacheSync)
                {
                    foreach (string key in _imageCache.Keys.ToList())
                    {
                        string rawPath = key.StartsWith("scaled:", StringComparison.Ordinal)
                            ? key[(key.IndexOf(':', 7) + 1)..]
                            : key;
                        if (keep.Contains(rawPath))
                            continue;
                        if (_imageCache.Remove(key, out var img))
                        {
                            try { img.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private void TrimItemCache()
        {
            try
            {
                if (_itemCache.Count <= 2200)
                    return;

                var keep = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
                foreach (string key in _itemCache.Keys.ToList())
                {
                    if (!keep.Contains(key) && _itemCache.Count > 1800)
                        _itemCache.TryRemove(key, out _);
                }
            }
            catch { }
        }


    }
}
