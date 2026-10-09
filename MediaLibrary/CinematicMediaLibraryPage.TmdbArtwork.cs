#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private static string? FirstGroupedPosterPath(IEnumerable<string?> paths)
        {
            string?[] candidates = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            // Use a validated local poster (usually the TMDb cache) ahead of a DLNA
            // thumbnail URL. Keep the server image as fallback if TMDb has no result.
            foreach (string? path in candidates)
            {
                if (MovieMetadataService.IsUsablePosterImage(path))
                    return path;
            }
            foreach (string? path in candidates)
                if (!string.IsNullOrWhiteSpace(path) && IsNetworkPath(path)) return path;
            return null;
        }

        private static string ResolveMetadataLookupKey(string path, string title, int? yearHint = null)
        {
            if (IsNetworkPath(path) && !string.IsNullOrWhiteSpace(title))
                return AddNetworkYearHint(path, CleanTitle(title), yearHint);
            try
            {
                string best = MovieMetadataService.GetBestKnownDisplayTitle(path);
                if (!string.IsNullOrWhiteSpace(best))
                    return CleanTitle(best);
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(title))
                return CleanTitle(title);
            return Path.GetFileNameWithoutExtension(path) ?? path;
        }

        private static string AddNetworkYearHint(string path, string title, int? yearHint)
        {
            if (!IsNetworkPath(path) || !yearHint.HasValue || string.IsNullOrWhiteSpace(title))
                return title;
            try
            {
                if (MovieMetadataService.ExtractMediaTitleInfoFromPath(title).Year.HasValue)
                    return title;
            }
            catch { }
            return $"{title} ({yearHint.Value.ToString(CultureInfo.InvariantCulture)})";
        }

        private static IEnumerable<string> MetadataLookupCandidates(string path, string metadataKey, string title)
        {
            // DLNA URLs are commonly opaque transport endpoints. Searching them
            // first can consume the entire TMDb timeout before the actual title.
            bool networkYearKnown = IsNetworkPath(path) && MovieMetadataService.ExtractMediaTitleInfoFromPath(metadataKey).Year.HasValue;
            string[] candidates = IsNetworkPath(path)
                ? networkYearKnown ? new[] { metadataKey } : new[] { metadataKey, title }
                : new[] { path, metadataKey, title };
            return candidates.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string? ResolvePosterPath(string path, string metadataKey, string title, bool allowNearbyArt = true)
        {
            bool hasNetworkYear = IsNetworkPath(path) && MovieMetadataService.ExtractMediaTitleInfoFromPath(metadataKey).Year.HasValue;
            IEnumerable<string> keys = hasNetworkYear
                ? new[] { metadataKey }
                : new[] { path, metadataKey, title };
            foreach (string key in keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string? value = MovieMetadataService.GetCachedPosterPath(key);
                    if (MovieMetadataService.IsUsablePosterImage(value))
                        return value;
                }
                catch { }
            }

            if (!allowNearbyArt)
                return null;
            string? nearby = FindNearbyArt(path, poster: true);
            return MovieMetadataService.IsUsablePosterImage(nearby) ? nearby : null;
        }

        private static string? ResolveBackdropPath(string path, string metadataKey, string title, bool allowNearbyArt = true)
        {
            bool hasNetworkYear = IsNetworkPath(path) && MovieMetadataService.ExtractMediaTitleInfoFromPath(metadataKey).Year.HasValue;
            IEnumerable<string> keys = hasNetworkYear
                ? new[] { metadataKey }
                : new[] { path, metadataKey, title };
            foreach (string key in keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string? value = MovieMetadataService.GetCachedBackdropPath(key);
                    if (LooksLikeUsableBackdrop(value))
                        return value;
                }
                catch { }
            }

            if (!allowNearbyArt)
                return null;

            string? nearby = FindNearbyArt(path, poster: false);
            if (LooksLikeUsableBackdrop(nearby))
                return nearby;
            return null;
        }

        // Paint calls this for every visible card on every repaint; the lookups behind it
        // (cache validation, metadata keys) cost ~20% of a frame. Once per title every few
        // seconds is enough: completed downloads invalidate the page themselves.
        private readonly Dictionary<string, long> _tmdbArtworkCheckedAt = new(StringComparer.OrdinalIgnoreCase);

        private void QueueTmdbArtworkResolve(LibraryItem? item, bool includeBackdrop = false)
        {
            if (item == null)
                return;
            long now = Environment.TickCount64;
            string displayTitle = FirstNonEmpty(item.SeriesTitle, item.Title);
            string itemIdentity = IsNetworkPath(item.Path)
                ? ResolveMetadataLookupKey(item.Path, displayTitle, item.Year).Trim().ToUpperInvariant()
                : item.Path;
            string throttleKey = itemIdentity + (includeBackdrop ? "|b" : "|p");
            if (_tmdbArtworkCheckedAt.TryGetValue(throttleKey, out long last) && now - last < 4000)
                return;
            _tmdbArtworkCheckedAt[throttleKey] = now;
            if (_tmdbArtworkCheckedAt.Count > 4000) _tmdbArtworkCheckedAt.Clear();

            if (item.IsGroup)
            {
                foreach (LibraryItem child in item.Children.Take(2))
                    QueueTmdbArtworkResolve(child, includeBackdrop);
                return;
            }

            if (!ShouldResolveRichOverview(item.Category))
                return;

            // This method is not called while smooth scrolling. Validate the cached path
            // here so a removed/corrupt cache entry cannot permanently suppress TMDb.
            RefreshCachedArtwork(item, allowNearbyArt: false);
            // HTTP artwork from a DLNA server is only a fallback thumbnail, not a
            // completed TMDb lookup. Poster validation intentionally accepts only
            // usable local artwork here.
            bool needsPoster = !MovieMetadataService.IsUsablePosterImage(item.ArtPath);
            // Grid cards only need a poster. Downloading a 4K backdrop for every card
            // kept all TMDb worker slots occupied for tens of seconds, so later covers
            // were never even queued. Backdrops remain eager for hero/detail surfaces.
            bool needsBackdrop = includeBackdrop &&
                                 !LooksLikeUsableBackdrop(item.WideArtPath);
            if (!needsPoster && !needsBackdrop)
                return;

            string path = item.Path;
            bool networkTv = IsNetworkPath(path) && string.Equals(item.Category, "TV Series", StringComparison.OrdinalIgnoreCase);
            string metadataKey = ResolveMetadataLookupKey(path, displayTitle, item.Year);
            string requestKey = IsNetworkPath(path)
                ? (networkTv ? "tv-title:" : "network-title:") + metadataKey.Trim().ToUpperInvariant()
                : NormalizeRootPath(path);
            if (string.IsNullOrWhiteSpace(requestKey))
                return;

            lock (_tmdbArtworkSync)
            {
                if (_tmdbArtworkRetryAfterUtc.TryGetValue(requestKey, out DateTime retryAfter) && retryAfter > DateTime.UtcNow)
                    return;
                if (_tmdbArtworkRequests.Count >= 4)
                {
                    // Slots full: allow a retry on the next paint instead of after the throttle.
                    _tmdbArtworkCheckedAt.Remove(throttleKey);
                    return;
                }
                if (!_tmdbArtworkRequests.Add(requestKey))
                    return;
            }

            // DLNA duration often differs from TMDb's runtime (cuts, intros, extras).
            // Passing it makes the matcher query the runtime of up to 20 candidates
            // serially. For network items the server title/year are more reliable and
            // avoid several seconds of unnecessary TMDb calls.
            double? durationSeconds = IsNetworkPath(path)
                ? null
                : item.DurationMinutes.HasValue ? item.DurationMinutes.Value * 60.0 : null;

            _ = Task.Run(async () =>
            {
                bool posterResolved = !needsPoster;
                bool backdropResolved = !needsBackdrop;
                try
                {
                    Task posterTask = needsPoster ? Task.Run(() =>
                    {
                        try
                        {
                            using var posterCts = new CancellationTokenSource(TimeSpan.FromSeconds(28));
                            string? poster = null;
                            if (networkTv)
                            {
                                poster = MovieMetadataService.ResolveTvTitleAndPoster(path, metadataKey, item.Year, durationSeconds, posterCts.Token);
                            }
                            else
                            {
                                foreach (string lookup in MetadataLookupCandidates(path, metadataKey, displayTitle))
                                {
                                    var resolved = MovieMetadataService.ResolveTitleAndPoster(lookup, durationSeconds, posterCts.Token);
                                    if (!MovieMetadataService.IsUsablePosterImage(resolved.localPosterPath))
                                        continue;
                                    poster = resolved.localPosterPath;
                                    break;
                                }
                            }
                            posterResolved = !string.IsNullOrWhiteSpace(poster);
                            if (posterResolved)
                                PostArtworkUpdate(path, metadataKey, poster, null);
                        }
                        catch (Exception ex)
                        {
                            Dbg.Warn($"TMDb poster failed for '{Path.GetFileName(path)}': {ex.Message}");
                        }
                    }) : Task.CompletedTask;

                    Task backdropTask = needsBackdrop ? Task.Run(() =>
                    {
                        try
                        {
                            using var backdropCts = new CancellationTokenSource(TimeSpan.FromSeconds(28));
                            string? backdrop = null;
                            if (networkTv)
                            {
                                backdrop = MovieMetadataService.ResolveTvTitleAndBackdrop(path, metadataKey, item.Year, durationSeconds, backdropCts.Token);
                            }
                            else
                            {
                                foreach (string lookup in MetadataLookupCandidates(path, metadataKey, displayTitle))
                                {
                                    var resolved = MovieMetadataService.ResolveTitleAndBackdrop(lookup, durationSeconds, backdropCts.Token);
                                    if (!string.IsNullOrWhiteSpace(resolved.localBackdropPath) && LooksLikeUsableBackdrop(resolved.localBackdropPath))
                                    {
                                        backdrop = resolved.localBackdropPath;
                                        break;
                                    }
                                }
                            }
                            backdropResolved = !string.IsNullOrWhiteSpace(backdrop);
                            if (backdropResolved)
                                PostArtworkUpdate(path, metadataKey, null, backdrop);
                        }
                        catch (Exception ex)
                        {
                            Dbg.Warn($"TMDb backdrop failed for '{Path.GetFileName(path)}': {ex.Message}");
                        }
                    }) : Task.CompletedTask;
                    await Task.WhenAll(posterTask, backdropTask).ConfigureAwait(false);
                }
                finally
                {
                    lock (_tmdbArtworkSync)
                    {
                        _tmdbArtworkRequests.Remove(requestKey);
                        if (posterResolved && backdropResolved)
                            _tmdbArtworkRetryAfterUtc.Remove(requestKey);
                        else
                            _tmdbArtworkRetryAfterUtc[requestKey] = DateTime.UtcNow.AddSeconds(25);
                    }

                    // Freeing one of the four worker slots must wake the visible grid even
                    // when this particular lookup failed; otherwise cards after the first
                    // four never get a chance to enqueue their own poster request.
                    QueueArtworkPump();
                }
            });
        }

        private void QueueArtworkPump()
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated)
                    return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || Disposing)
                        return;
                    QueueCoalescedImageRefresh();
                }));
            }
            catch { }
        }

        private void PostArtworkUpdate(string path, string metadataKey, string? poster, string? backdrop)
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated)
                    return;

                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ApplyTmdbArtworkToItemCopies(path, metadataKey, poster, backdrop);
                        QueueCoalescedImageRefresh();
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void RefreshVisibleArtworkFromCache()
        {
            // Only touch the active model. Snapshot lists hold the same cached item
            // instances, and scanning every historical snapshot for every poster event
            // caused O(categories * items) work and visible UI churn.
            foreach (LibraryItem item in _items.Distinct())
            {
                if (item.IsGroup)
                {
                    foreach (LibraryItem child in item.Children)
                        RefreshCachedArtwork(child);
                    item.ArtPath = string.Equals(item.GroupKind, "Season", StringComparison.OrdinalIgnoreCase)
                        ? FirstGroupedPosterPath(item.Children.Select(child => child.ArtPath)) ?? item.ArtPath
                        : FirstExistingPath(item.Children.Select(child => child.ArtPath)) ?? item.ArtPath;
                    item.WideArtPath = FirstExistingPath(item.Children.Select(child => child.WideArtPath)) ?? item.WideArtPath;
                }
                else
                {
                    RefreshCachedArtwork(item);
                }
            }

            if (_detailItem != null)
                RefreshCachedArtwork(_detailItem);

            Invalidate();
        }

        private void ApplyTmdbArtworkToItemCopies(string path, string metadataKey, string? poster, string? backdrop)
        {
            bool hasPoster = MovieMetadataService.IsUsablePosterImage(poster);
            bool hasBackdrop = !string.IsNullOrWhiteSpace(backdrop) && LooksLikeUsableBackdrop(backdrop);
            if (!hasPoster && !hasBackdrop)
                return;

            void Apply(LibraryItem? target)
            {
                if (target == null)
                    return;
                if (hasPoster)
                    target.ArtPath = poster;
                if (hasBackdrop)
                    target.WideArtPath = backdrop;
            }

            bool MatchesRequestedTitle(LibraryItem? target)
            {
                if (target == null || !string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!IsNetworkPath(path))
                    return true;
                string targetTitle = FirstNonEmpty(target.SeriesTitle, target.Title);
                string targetKey = ResolveMetadataLookupKey(target.Path, targetTitle, target.Year);
                return string.Equals(targetKey, metadataKey, StringComparison.OrdinalIgnoreCase);
            }

            if (_itemCache.TryGetValue(path, out var cached) && MatchesRequestedTitle(cached))
                Apply(cached);

            IEnumerable<LibraryItem> allCopies = _items;

            foreach (var existing in allCopies.Where(MatchesRequestedTitle))
                Apply(existing);

            foreach (var group in allCopies.Where(existing => existing.IsGroup).Distinct())
            {
                bool groupContainsTarget = false;
                foreach (var child in group.Children.Where(MatchesRequestedTitle))
                {
                    Apply(child);
                    groupContainsTarget = true;
                }

                if (!groupContainsTarget)
                    continue;

                if (hasPoster && !MovieMetadataService.IsUsablePosterImage(group.ArtPath))
                    group.ArtPath = string.Equals(group.GroupKind, "Season", StringComparison.OrdinalIgnoreCase)
                        ? FirstGroupedPosterPath(group.Children.Select(child => child.ArtPath))
                        : FirstExistingPath(group.Children.Select(child => child.ArtPath));
                if (hasBackdrop && (string.IsNullOrWhiteSpace(group.WideArtPath) || !LooksLikeUsableBackdrop(group.WideArtPath)))
                    group.WideArtPath = FirstExistingPath(group.Children.Select(child => child.WideArtPath));
            }

            if (MatchesRequestedTitle(_detailItem))
                Apply(_detailItem);
        }

        private static bool LooksLikeUsableBackdrop(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return false;
                return new FileInfo(path).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string? FindNearbyArt(string path, bool poster)
        {
            try
            {
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    return null;

                string[] names = poster
                    ? new[] { "poster", "folder", "cover", "movie-poster" }
                    : new[] { "backdrop", "fanart", "background", "landscape", "movie-backdrop" };
                string[] exts = { ".jpg", ".jpeg", ".png", ".webp" };

                foreach (string name in names)
                {
                    foreach (string ext in exts)
                    {
                        string candidate = Path.Combine(dir, name + ext);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
            catch { }

            return null;
        }


    }
}
