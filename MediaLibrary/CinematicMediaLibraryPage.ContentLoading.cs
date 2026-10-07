#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private sealed class ContentSnapshot
        {
            public List<LibraryItem> Items { get; init; } = new();
            public List<ResumeItem> Resume { get; init; } = new();
            public List<(string Icon, string Text)> Stats { get; init; } = new();
            public bool IsComplete { get; init; }
            public int PhotoTotal { get; init; }
        }

        private readonly Dictionary<string, ContentSnapshot> _contentSnapshots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ContentSnapshot> _latestContentSnapshots = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, bool> RemoteStorageRoots = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object AudioTagIndexSync = new();
        private static Dictionary<string, MediaProbe.AudioTags>? AudioTagIndex;
        private static bool AudioTagIndexDirty;
        private static string AudioTagIndexPath => Path.Combine(AppDataDir, "audioTagIndex.json");
        private string _visibleContentRouteKey = string.Empty;
        private int _initialContentReadyRaised;

        internal event EventHandler? InitialContentReady;

        private async void SignalInitialContentReady()
        {
            if (Interlocked.Exchange(ref _initialContentReadyRaised, 1) != 0)
                return;

            // The complete model is already visible behind the splash. Give its
            // first artwork requests one short opportunity to finish before the
            // splash leaves, instead of showing another Home loader afterwards.
            var artworkWatch = Stopwatch.StartNew();
            while (!IsDisposed && !Disposing && artworkWatch.ElapsedMilliseconds < 1000)
            {
                try { await Task.Delay(50).ConfigureAwait(true); } catch { break; }
                bool pending;
                lock (_imageCacheSync) pending = _imageLoadRequests.Count > 0;
                if (!pending)
                    break;
            }
            try { InitialContentReady?.Invoke(this, EventArgs.Empty); } catch { }
        }

        public void PrepareInitialContentForFirstFrame()
        {
            if (_contentPrepared)
                return;

            _contentPrepared = true;
            // Lo splash copre il primo frame: costruire qui un'anteprima sincrona
            // obbligava a scandire la libreria di nuovo subito dopo OnShown.
            // L'unico snapshot completo viene preparato sul worker di RefreshContent.
            SetContentLoading(true, LoadingCategoryLabel(_category));

            // Il completamento asincrono parte da OnShown, quando WinForms ha già
            // installato il proprio SynchronizationContext e il message loop è attivo.
        }

        public void CompleteInitialContentAfterFirstFrame()
        {
            if (!_contentPrepared)
            {
                EnsureInitialContentPrepared();
                return;
            }

            RefreshContent();
        }

        private async void RefreshContent()
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(RefreshContent)); } catch { }
                return;
            }

            int version = Interlocked.Increment(ref _contentRefreshVersion);
            var refreshCts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _contentRefreshCts, refreshCts);
            try { previous?.Cancel(); } catch { }
            try { previous?.Dispose(); } catch { }
            CancellationToken token = refreshCts.Token;
            string snapshotKey = BuildContentSnapshotKey();
            string routeKey = BuildContentRouteKey();
            bool preserveVisibleModel = _items.Count > 32 &&
                                        string.Equals(_visibleContentRouteKey, routeKey, StringComparison.Ordinal);

            bool hasCurrentSnapshot = _contentSnapshots.TryGetValue(snapshotKey, out ContentSnapshot? cachedSnapshot);
            if (!hasCurrentSnapshot)
                _latestContentSnapshots.TryGetValue(routeKey, out cachedSnapshot);
            // Album groups from an incomplete tag scan are rebuilt with the final
            // metadata; publish music once rather than resizing an interim carousel.
            if (_category == "Music" && cachedSnapshot is { IsComplete: false })
            { cachedSnapshot = null; hasCurrentSnapshot = false; }
            if (cachedSnapshot != null)
            {
                if (_category == "Photos") _photoTotalAvailable = cachedSnapshot.PhotoTotal;
                if (!preserveVisibleModel || cachedSnapshot.Items.Count >= _items.Count)
                {
                    ApplyContentItems(cachedSnapshot.Items, selectHero: true);
                    _visibleContentRouteKey = routeKey;
                    ApplyContentAuxiliary(cachedSnapshot.Resume, cachedSnapshot.Stats);
                }
                // Uno snapshot parziale evita il bianco, ma non deve scoprire una
                // griglia che sta ancora cambiando. La maschera termina solo sullo
                // snapshot completo della rotta corrente.
                SetContentLoading(!preserveVisibleModel && !(hasCurrentSnapshot && cachedSnapshot.IsComplete), LoadingCategoryLabel(_category));
                Invalidate();

                // The key includes the backing JSON timestamps. A complete snapshot is
                // therefore current and must not be rebuilt/replaced on every visit.
                if (hasCurrentSnapshot && cachedSnapshot.IsComplete)
                {
                    TrimImageCache();
                    TrimItemCache();
                    SignalInitialContentReady();
                    return;
                }
            }
            else if (!preserveVisibleModel)
            {
                // Cambia pagina subito: non lasciare per centinaia di ms le card della
                // categoria precedente mentre il nuovo modello viene costruito.
                ApplyContentItems(Array.Empty<LibraryItem>(), selectHero: false);
                ApplyContentAuxiliary(Array.Empty<ResumeItem>(), Array.Empty<(string Icon, string Text)>());
                _heroItem = null;
                SetContentLoading(true, LoadingCategoryLabel(_category));
                Invalidate();
            }

            try
            {
                // Build the route once. The old partial 32-item pass and forced
                // pause duplicated filesystem and metadata work under the mask.
                await Task.Yield();
                var buildWatch = Stopwatch.StartNew();
                List<LibraryItem> completeItems = await Task.Run(() => BuildItems(token), token).ConfigureAwait(true);
                buildWatch.Stop();
                if (!IsCurrentRefresh(version, token))
                    return;

                if (buildWatch.ElapsedMilliseconds >= 80)
                    Dbg.Log($"[LIB PERF] {_category} complete batch: build={buildWatch.ElapsedMilliseconds}ms, items={completeItems.Count}");

                List<LibraryItem> items = cachedSnapshot == null || IsNetworkSourceActive()
                    ? completeItems
                    : MergeStableItems(cachedSnapshot.Items, completeItems);
                ApplyContentItems(items, selectHero: true);
                _visibleContentRouteKey = routeKey;
                Invalidate();
                WarmRichMetadataInBackground(items);

                var auxiliary = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var resume = BuildResumeItems(items);
                    token.ThrowIfCancellationRequested();
                    var stats = BuildStatsCells(items);
                    return (Resume: resume, Stats: stats);
                }, token).ConfigureAwait(true);

                if (!IsCurrentRefresh(version, token))
                    return;

                ApplyContentAuxiliary(auxiliary.Resume, auxiliary.Stats);
                StoreContentSnapshot(snapshotKey, items, auxiliary.Resume, auxiliary.Stats, isComplete: true);
                SetContentLoading(false);
                TrimImageCache();
                TrimItemCache();
                Invalidate();
                SignalInitialContentReady();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (IsCurrentRefresh(version, token))
                {
                    SetContentLoading(false);
                    SignalInitialContentReady();
                }
                Dbg.Warn("Library refresh failed: " + ex.Message);
            }
        }

        private bool IsCurrentRefresh(int version, CancellationToken token)
            => !token.IsCancellationRequested &&
               version == Volatile.Read(ref _contentRefreshVersion) &&
               !IsDisposed && !Disposing;

        private void ApplyContentItems(IReadOnlyList<LibraryItem> items, bool selectHero)
        {
            // A partial/cached refresh may contain a mixture of raw episodes and season
            // groups. Canonicalize the model at the last hand-off to the renderer so a
            // single episode can never leak beside its season card.
            if (_view == PageView.Collection &&
                string.Equals(_category, "TV Series", StringComparison.OrdinalIgnoreCase))
            {
                var episodes = items
                    .SelectMany<LibraryItem, LibraryItem>(item => item.IsGroup &&
                                                                   string.Equals(item.GroupKind, "Season", StringComparison.OrdinalIgnoreCase)
                        ? item.Children
                        : new[] { item })
                    .Where(item => item != null && !item.IsGroup)
                    .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                items = GroupTvSeasons(episodes);
            }
            else if (_view == PageView.Collection &&
                     string.Equals(_category, "Music", StringComparison.OrdinalIgnoreCase) &&
                     items.Any(item => !item.IsGroup))
            {
                // Snapshot parziale e catalogo completo possono contenere lo stesso
                // brano una volta come figlio dell'album e una volta come elemento
                // grezzo. Normalizza sempre prima dell'handoff al renderer.
                var tracks = items
                    .SelectMany<LibraryItem, LibraryItem>(item => item.IsGroup &&
                                                                  string.Equals(item.GroupKind, "Album", StringComparison.OrdinalIgnoreCase)
                        ? item.Children
                        : new[] { item })
                    .Where(item => item != null && !item.IsGroup && !string.IsNullOrWhiteSpace(item.Path))
                    .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                items = GroupMusicAlbums(tracks);
            }

            // Re-entering Videos often rebuilt an identical list and cleared/re-added
            // every card. Keep the painted model when identity and order are unchanged;
            // cached items are mutable, so artwork/quality updates still flow through.
            if (ContentItemsEquivalent(_items, items))
                return;

            string? existingHeroPath = _heroItem?.Path;
            _items.Clear();
            _items.AddRange(items);
            if (_activeGroup != null && !_items.Any(item =>
                    string.Equals(item.Path, _activeGroup.Path, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Title, _activeGroup.Title, StringComparison.OrdinalIgnoreCase)))
            {
                CloseGroupPicker(invalidate: false);
            }

            _gridScroll = Math.Max(0, Math.Min(_gridScroll, _gridScrollMax));
            ClampSmoothGridScrollTarget();
            bool heroStillPresent = !string.IsNullOrWhiteSpace(existingHeroPath) &&
                _items.Any(item => string.Equals(item.Path, existingHeroPath, StringComparison.OrdinalIgnoreCase));
            if (selectHero && !heroStillPresent)
                SelectHeroItem(force: true);
        }

        private static bool ContentItemsEquivalent(IReadOnlyList<LibraryItem> left, IReadOnlyList<LibraryItem> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                LibraryItem a = left[i];
                LibraryItem b = right[i];
                if (!string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(a.Title, b.Title, StringComparison.Ordinal) ||
                    !string.Equals(a.Category, b.Category, StringComparison.Ordinal) ||
                    !string.Equals(a.ArtPath, b.ArtPath, StringComparison.Ordinal) ||
                    !string.Equals(a.WideArtPath, b.WideArtPath, StringComparison.Ordinal) ||
                    !string.Equals(a.GroupSubtitle, b.GroupSubtitle, StringComparison.Ordinal) ||
                    a.IsGroup != b.IsGroup ||
                    a.Children.Count != b.Children.Count)
                    return false;

                for (int child = 0; child < a.Children.Count; child++)
                {
                    if (!string.Equals(a.Children[child].Path, b.Children[child].Path, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(a.Children[child].Title, b.Children[child].Title, StringComparison.Ordinal) ||
                        !string.Equals(a.Children[child].ArtPath, b.Children[child].ArtPath, StringComparison.Ordinal))
                        return false;
                }
            }

            return true;
        }

        private static List<LibraryItem> MergeStableItems(
            IReadOnlyList<LibraryItem> visibleItems,
            IReadOnlyList<LibraryItem> completeItems)
        {
            static string Key(LibraryItem item) => (item.Path ?? string.Empty) + "\u001f" + (item.Title ?? string.Empty);

            var richByKey = completeItems
                .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var merged = new List<LibraryItem>(completeItems.Count);
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (LibraryItem item in visibleItems)
            {
                string key = Key(item);
                if (richByKey.TryGetValue(key, out LibraryItem? rich) && added.Add(key))
                    merged.Add(rich);
            }

            foreach (LibraryItem item in completeItems)
            {
                string key = Key(item);
                if (added.Add(key))
                    merged.Add(item);
            }

            return merged;
        }

        private void ApplyContentAuxiliary(IReadOnlyList<ResumeItem> resume, IReadOnlyList<(string Icon, string Text)> stats)
        {
            _resumeItems.Clear();
            _resumeItems.AddRange(resume);
            _statsCells.Clear();
            _statsCells.AddRange(stats);
            _resumeOffset = Math.Max(0, Math.Min(_resumeOffset, Math.Max(0, _resumeItems.Count - 1)));
        }

        private string BuildContentSnapshotKey()
        {
            static long WriteTicks(string path)
            {
                try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0; }
                catch { return 0; }
            }

            return string.Join("|",
                _source,
                _view,
                _category,
                _sortMode,
                _category == "Photos" ? _filter : "",
                _photoVisibleLimit,
                WriteTicks(IndexPath),
                WriteTicks(FavoritesPath),
                WriteTicks(PlaylistsPath),
                WatchHistoryStore.StoreVersionTicks,
                _networkConnectedServerKey,
                _networkItems.Count,
                _networkCatalogRevision);
        }

        private string BuildContentRouteKey()
        {
            return string.Join("|",
                _source,
                _view,
                _category,
                _sortMode,
                _category == "Photos" ? _filter : "",
                _networkConnectedServerKey);
        }

        private void StoreContentSnapshot(
            string key,
            IReadOnlyList<LibraryItem> items,
            IReadOnlyList<ResumeItem>? resume,
            IReadOnlyList<(string Icon, string Text)>? stats,
            bool isComplete)
        {
            var snapshot = new ContentSnapshot
            {
                PhotoTotal = _photoTotalAvailable,
                Items = items.ToList(),
                Resume = resume?.ToList() ?? new List<ResumeItem>(),
                Stats = stats?.ToList() ?? new List<(string Icon, string Text)>(),
                IsComplete = isComplete
            };
            _contentSnapshots[key] = snapshot;
            _latestContentSnapshots[BuildContentRouteKey()] = snapshot;

            if (_contentSnapshots.Count > 18)
            {
                foreach (string staleKey in _contentSnapshots.Keys.Take(_contentSnapshots.Count - 14).ToList())
                    _contentSnapshots.Remove(staleKey);
            }

            if (_latestContentSnapshots.Count > 16)
            {
                foreach (string staleKey in _latestContentSnapshots.Keys.Take(_latestContentSnapshots.Count - 12).ToList())
                    _latestContentSnapshots.Remove(staleKey);
            }
        }

        private void RefreshFilteredOnly()
        {
            SelectHeroItem(force: true);
            Invalidate();
        }

        private void RotateHero(bool force = false)
        {
            SelectHeroItem(force);
            Invalidate();
        }

        private void SelectHeroItem(bool force)
        {
            var next = PickHeroItem(force);
            _heroItem = next;
            _heroRotationKey = next?.Path;
            _heroRotationUtc = DateTime.UtcNow;
            QueueTmdbArtworkResolve(next, includeBackdrop: true);
            QueueRichDetailResolve(next);
        }

        private LibraryItem? PickHeroItem(bool force)
        {
            var resumeWatch = Stopwatch.StartNew();
            IReadOnlyDictionary<string, PlaybackResumeStore.Entry> resumeLookup;
            try
            {
                resumeLookup = PlaybackResumeStore.LoadAll()
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.MediaPath))
                    .GroupBy(entry => entry.MediaPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                resumeLookup = new Dictionary<string, PlaybackResumeStore.Entry>(StringComparer.OrdinalIgnoreCase);
            }
            resumeWatch.Stop();
            if (resumeWatch.ElapsedMilliseconds >= 80)
                Dbg.Log($"[LIB PERF] resume snapshot for hero: {resumeWatch.ElapsedMilliseconds}ms");

            var pool = VisibleItems()
                .Where(item => IsHeroCandidate(item, resumeLookup))
                .ToList();

            if (pool.Count == 0)
                pool = VisibleItems().ToList();
            if (pool.Count == 0)
                pool = _items.ToList();
            if (pool.Count == 0)
                return null;

            if (!force && _heroItem != null && (DateTime.UtcNow - _heroRotationUtc) < TimeSpan.FromSeconds(12))
                return _heroItem;

            if (pool.Count == 1)
                return pool[0];

            var choices = pool.Where(i => !string.Equals(i.Path, _heroRotationKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (choices.Count == 0)
                choices = pool;

            return choices[_heroRandom.Next(choices.Count)];
        }

        private static bool IsHeroCandidate(
            LibraryItem item,
            IReadOnlyDictionary<string, PlaybackResumeStore.Entry> resumeLookup)
        {
            if (item == null || !IsVideoPath(item.Path))
                return false;

            try
            {
                resumeLookup.TryGetValue(item.Path, out PlaybackResumeStore.Entry? entry);
                if (entry == null)
                    return true;
                if (ResumeCompleted(entry))
                    return false;
                return entry.PositionSeconds <= 1;
            }
            catch
            {
                return true;
            }
        }

        private void QueueRefresh()
        {
            try
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            }
            catch { }
        }

        private List<LibraryItem> BuildItems(CancellationToken token = default)
        {
            var totalWatch = Stopwatch.StartNew();
            bool lightweight = _view == PageView.Home;
            int naturalMaxItems = _view == PageView.Collection ? (UsesTemporalSections(_category) ? 3200 : 1800) : 260;
            int maxItems = naturalMaxItems;
            int prefetchLimit = _view == PageView.Collection ? maxItems : 360;
            if (!string.Equals(_category, "Photos", StringComparison.OrdinalIgnoreCase))
                _photoTotalAvailable = 0;

            if (string.Equals(_category, "Playlists", StringComparison.OrdinalIgnoreCase))
                return new List<LibraryItem>();

            if (IsNetworkSourceActive())
                return BuildNetworkItemsForCurrentCategory(maxItems);

            var candidates = AllPathsForCategory(_category)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path =>
                {
                    token.ThrowIfCancellationRequested();
                    try { return (_itemCache.ContainsKey(path) || lightweight || IsRemoteStoragePath(path) || File.Exists(path)) && !ShouldIgnoreMediaPath(path); }
                    catch { return false; }
                })
                .Where(PathBelongsToCurrentCategory);

            if (string.Equals(_category, "Photos", StringComparison.OrdinalIgnoreCase))
            {
                if (_filter.StartsWith("Folder:", StringComparison.Ordinal))
                    candidates = candidates.Where(path => string.Equals(Path.GetDirectoryName(path), _filter[7..], StringComparison.OrdinalIgnoreCase));
                else if (_filter == "Recent")
                    candidates = candidates.Where(path => SafeSortDate(path) >= DateTime.UtcNow.AddDays(-45));
                IEnumerable<string> sortedPhotos = _sortMode switch
                {
                    1 => candidates.OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase),
                    2 => candidates.OrderBy(SafeSortDate),
                    3 => candidates.OrderByDescending(path => { try { return new FileInfo(path).Length; } catch { return 0L; } }),
                    _ => candidates.OrderByDescending(SafeSortDate)
                };
                var photoPaths = sortedPhotos.ToList();

                _photoTotalAvailable = photoPaths.Count;
                int take = Math.Max(PhotoPageSize, _photoVisibleLimit);
                return ApplyActiveSort(photoPaths
                    .Take(take)
                    .Select(path => ToItem(path))
                    .Where(item => item != null)
                    .Cast<LibraryItem>());
            }

            if (_sortMode == 0 && _view == PageView.Home)
            {
                candidates = candidates
                    .OrderByDescending(path => _itemCache.TryGetValue(path, out LibraryItem? cached)
                        ? cached.SortDateUtc
                        : IsRemoteStoragePath(path) ? DateTime.MinValue : SafeSortDate(path))
                    .Take(prefetchLimit);
            }

            var candidateWatch = Stopwatch.StartNew();
            List<string> candidatePaths = candidates.ToList();
            candidateWatch.Stop();

            var resumeWatch = Stopwatch.StartNew();
            IReadOnlyDictionary<string, PlaybackResumeStore.Entry> resumeLookup;
            try
            {
                resumeLookup = PlaybackResumeStore.LoadAll()
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.MediaPath))
                    .GroupBy(entry => entry.MediaPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                resumeLookup = new Dictionary<string, PlaybackResumeStore.Entry>(StringComparer.OrdinalIgnoreCase);
            }
            resumeWatch.Stop();

            var paths = new List<LibraryItem>();
            long slowestItemMs = 0;
            string slowestItemName = string.Empty;
            var itemWatch = Stopwatch.StartNew();
            foreach (string path in candidatePaths)
            {
                token.ThrowIfCancellationRequested();
                var singleWatch = Stopwatch.StartNew();
                bool readFile = !lightweight &&
                    (string.Equals(_category, "Music", StringComparison.OrdinalIgnoreCase) ||
                     !IsRemoteStoragePath(path));
                LibraryItem? item = ToItem(path, allowMediaFileIo: readFile, resumeLookup);
                singleWatch.Stop();
                if (singleWatch.ElapsedMilliseconds > slowestItemMs)
                {
                    slowestItemMs = singleWatch.ElapsedMilliseconds;
                    slowestItemName = Path.GetFileName(path);
                }
                if (item != null)
                    paths.Add(item);
            }
            itemWatch.Stop();

            paths = GroupCollectionItems(paths);

            if (string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase))
            {
                var watchedAt = WatchHistoryStore.LoadAll()
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.MediaPath))
                    .ToDictionary(entry => entry.MediaPath, entry => entry.LastWatchedAtUtc, StringComparer.OrdinalIgnoreCase);
                paths = paths.OrderByDescending(item => watchedAt.TryGetValue(item.Path, out DateTime date) ? date : DateTime.MinValue).ToList();
            }
            else
            {
                paths = ApplyActiveSort(paths);
            }

            List<LibraryItem> result = paths.Take(maxItems).ToList();
            if (string.Equals(_category, "Music", StringComparison.OrdinalIgnoreCase))
                FlushAudioTagIndex();
            totalWatch.Stop();
            if (totalWatch.ElapsedMilliseconds >= 80)
            {
                Dbg.Log($"[LIB PERF] build {_category}: total={totalWatch.ElapsedMilliseconds}ms, candidates={candidateWatch.ElapsedMilliseconds}ms/{candidatePaths.Count}, resume={resumeWatch.ElapsedMilliseconds}ms, items={itemWatch.ElapsedMilliseconds}ms/{paths.Count}, slowest={slowestItemMs}ms '{slowestItemName}', lightweight={lightweight}");
            }
            return result;
        }

        private static bool IsRemoteStoragePath(string path)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                return true;
            try
            {
                string root = Path.GetPathRoot(path) ?? string.Empty;
                if (root.Length == 0)
                    return false;
                return RemoteStorageRoots.GetOrAdd(root, static driveRoot =>
                {
                    try { return new DriveInfo(driveRoot).DriveType == DriveType.Network; }
                    catch { return false; }
                });
            }
            catch { return false; }
        }

        /// <summary>Tags already in the index, without touching the audio file.</summary>
        private static MediaProbe.AudioTags? TryGetIndexedAudioTags(string path)
        {
            lock (AudioTagIndexSync)
            {
                AudioTagIndex ??= LoadJson<Dictionary<string, MediaProbe.AudioTags>>(AudioTagIndexPath)
                    is { } saved
                    ? new Dictionary<string, MediaProbe.AudioTags>(saved, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, MediaProbe.AudioTags>(StringComparer.OrdinalIgnoreCase);
                if (!AudioTagIndex.TryGetValue(path, out MediaProbe.AudioTags? cached)) return null;
                // Voci scritte da versioni precedenti con caratteri persi (U+FFFD, controlli C1):
                // non sono riparabili, quindi si rilegge il file.
                if (IsDamagedTagText(cached.Title) || IsDamagedTagText(cached.Artist) ||
                    IsDamagedTagText(cached.Album) || IsDamagedTagText(cached.AlbumArtist))
                {
                    AudioTagIndex.Remove(path);
                    AudioTagIndexDirty = true;
                    return null;
                }
                cached.Title = MusicTextIdentity.RepairEncoding(cached.Title);
                cached.Artist = MusicTextIdentity.RepairEncoding(cached.Artist);
                cached.Album = MusicTextIdentity.RepairEncoding(cached.Album);
                cached.AlbumArtist = MusicTextIdentity.RepairEncoding(cached.AlbumArtist);
                return cached;
            }
        }

        private static bool IsDamagedTagText(string? value)
            => !string.IsNullOrEmpty(value) && value.Any(c => c == '�' || (c >= '' && c <= ''));

        private static MediaProbe.AudioTags ReadIndexedAudioTags(string path)
        {
            if (TryGetIndexedAudioTags(path) is { } cached)
                return cached;

            MediaProbe.AudioTags tags = MediaProbe.ReadAudioTags(path);
            if (tags.HasAny || tags.DurationMinutes.HasValue)
            {
                lock (AudioTagIndexSync)
                {
                    AudioTagIndex![path] = tags;
                    AudioTagIndexDirty = true;
                }
            }
            return tags;
        }

        private static void FlushAudioTagIndex()
        {
            lock (AudioTagIndexSync)
            {
                if (!AudioTagIndexDirty || AudioTagIndex == null)
                    return;
                try
                {
                    Directory.CreateDirectory(AppDataDir);
                    string path = AudioTagIndexPath;
                    string staging = path + ".tmp";
                    File.WriteAllText(staging, JsonSerializer.Serialize(AudioTagIndex), new UTF8Encoding(false));
                    File.Move(staging, path, overwrite: true);
                    AudioTagIndexDirty = false;
                }
                catch { AudioTagIndexDirty = true; }
            }
        }

        private static void InvalidateAudioTagIndex()
        {
            lock (AudioTagIndexSync)
            {
                AudioTagIndex = new Dictionary<string, MediaProbe.AudioTags>(StringComparer.OrdinalIgnoreCase);
                AudioTagIndexDirty = true;
            }
            FlushAudioTagIndex();
        }


    }
}
