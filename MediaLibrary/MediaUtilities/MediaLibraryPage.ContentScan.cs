using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using System;

#nullable enable

namespace CinecorePlayer2025
{
    internal sealed partial class MediaLibraryPage
    {
        private sealed class TvEpisodeOption
        {
            public FileInfo? File { get; set; }
            public string SourcePath { get; set; } = string.Empty;
            public string SourceName { get; set; } = string.Empty;
            public int? EpisodeNumber { get; set; }
            public string DisplayText { get; set; } = string.Empty;
            public string FilePath => !string.IsNullOrWhiteSpace(SourcePath)
                ? SourcePath
                : (File?.FullName ?? string.Empty);
            public string FileName => !string.IsNullOrWhiteSpace(SourceName)
                ? SourceName
                : (!string.IsNullOrWhiteSpace(File?.Name) ? File!.Name : Path.GetFileName(FilePath));
            public override string ToString() => DisplayText;
        }

        private sealed class TvSeasonGroup
        {
            public string SeriesTitle { get; set; } = string.Empty;
            public int? SeasonNumber { get; set; }
            public int? RepresentativeEpisodeNumber { get; set; }
            public string RepresentativePath { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public List<TvEpisodeOption> Episodes { get; set; } = new();
        }

        private sealed class CollectionBucketInfo
        {
            public string BucketKey { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Subtitle { get; set; } = string.Empty;
            public Action? ActivateAction { get; set; }
            public object? ContextTag { get; set; }
            public bool CompactTile { get; set; }
            public string? ArtworkKey { get; set; }
            public string? PrimaryActionLabel { get; set; }
            public Action? PrimaryAction { get; set; }
            public string? SecondaryActionLabel { get; set; }
            public Action? SecondaryAction { get; set; }
        }

        private sealed class LibraryItemContext
        {
            public string? FilePath { get; private set; }
            public TvSeasonGroup? SeasonGroup { get; private set; }
            public bool IsSeasonGroup => SeasonGroup != null;
            public string RepresentativePath => SeasonGroup?.RepresentativePath ?? FilePath ?? string.Empty;

            public static LibraryItemContext FromFile(string path)
                => new() { FilePath = path };

            public static LibraryItemContext FromSeasonGroup(TvSeasonGroup seasonGroup)
                => new() { SeasonGroup = seasonGroup };

            public List<string> GetPaths()
            {
                if (SeasonGroup != null)
                {
                    return SeasonGroup.Episodes
                        .Select(ep => ep.FilePath)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                if (!string.IsNullOrWhiteSpace(FilePath))
                    return new List<string> { FilePath! };

                return new List<string>();
            }
        }

        private sealed class LibraryRenderItem
        {
            public FileInfo? File { get; private set; }
            public TvSeasonGroup? SeasonGroup { get; private set; }
            public CollectionBucketInfo? CollectionBucket { get; private set; }
            public string? SectionTitle { get; private set; }
            public string? SectionBucket { get; private set; }
            public bool IsSeasonGroup => SeasonGroup != null;
            public bool IsCollectionBucket => CollectionBucket != null;
            public bool IsSectionHeader => !string.IsNullOrWhiteSpace(SectionTitle);
            public string RepresentativePath => SeasonGroup?.RepresentativePath ?? File?.FullName ?? string.Empty;

            public static LibraryRenderItem FromFile(FileInfo file) => new() { File = file };
            public static LibraryRenderItem FromSeasonGroup(TvSeasonGroup seasonGroup) => new() { SeasonGroup = seasonGroup };
            public static LibraryRenderItem FromCollectionBucket(CollectionBucketInfo bucket) => new() { CollectionBucket = bucket };
            public static LibraryRenderItem FromSectionTitle(string sectionTitle, string? sectionBucket = null) => new() { SectionTitle = sectionTitle, SectionBucket = sectionBucket };
        }

        // ------------ SCAN / FILTER / RENDER GRID ------------
        private void StopAnimatedLibraryVisuals()
        {
            try { _progressiveTimer.Stop(); } catch { }
            try { _carouselViewport.StopAnimation(true); } catch { }
            try { _grid.StopAnimatedScroll(true); } catch { }
        }

        private void RefreshContent()
        {
            try { _thumbCts?.Cancel(); } catch { }
            try { _scanCts?.Cancel(); } catch { }

            StopAnimatedLibraryVisuals();
            ResetProgressiveRender();

            _scanCts = new CancellationTokenSource();
            _thumbCts = new CancellationTokenSource();
            var ct = _scanCts.Token;

            string catNow = _selCat;
            string srcNow = _selSrc;
            var thisScanCts = _scanCts;
            // ----- sorgente URL: solo il pannellino link -----
            if (_selSrc == "URL")
            {
                // per la sorgente URL niente carosello "Riprendi"
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;

                HideMask();
                _grid.Controls.Clear();

                _urlPane ??= new UrlPane(url => SafeOpen(url));
                _urlPane.Dock = DockStyle.Top;

                var host = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = _urlPane.Height + 40,
                    BackColor = Color.Black,
                    Padding = new Padding(0, 8, 0, 0)
                };
                host.Controls.Add(_urlPane);
                host.Controls.Add(new InfoRow("Supportati link diretti HTTP/HTTPS (anche HLS .m3u8)."));

                _grid.Controls.Add(host);
                _grid.Visible = true;              // ri-mostra la griglia con il pannello URL
                _grid.UpdateThemedScrollbar();
                return;
            }
            else if (_selSrc == "YouTube")
            {
                // sorgente YouTube: UI dedicata, niente carosello e niente scansione dischi
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;

                HideMask();
                _grid.Controls.Clear();

                _ytPane ??= new YouTubePane(url => SafeOpen(url));
                _ytPane.Dock = DockStyle.Top;

                _grid.Controls.Add(_ytPane);
                _grid.Visible = true;
                _grid.UpdateThemedScrollbar();
                return;
            }
            // ----- sorgente DLNA -----
            else if (_selSrc == "Rete domestica")
            {
                RefreshDlnaSourceContent();
                return;
            }

            var exts = ExtsForCategory(_selCat);
            var rootsList = AllRootsForCategory(_selCat).ToList();

            // reset visibilità sezioni / carosello per lo stato “normale”
            bool isPlaylist = string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase);
            bool isPreferiti = string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase);
            bool isFilm = string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase);
            bool isFoto = string.Equals(_selCat, "Foto", StringComparison.OrdinalIgnoreCase);
            bool isUrlSrc = string.Equals(_selSrc, "URL", StringComparison.OrdinalIgnoreCase);
            bool isYtSrc = string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase);

            // niente carosello per Playlist / Preferiti / Foto e per le sorgenti URL / YouTube
            bool showCarousel = ShouldShowRecentCarouselForCurrentState();

            _secAll.Visible = !(isPlaylist || isPreferiti || isFilm || isUrlSrc || isYtSrc);
            _secRecenti.Visible = showCarousel;
            _carouselHost.Visible = showCarousel;

            HideInlineRootsCallToAction();

            if (isPlaylist || isPreferiti)
            {
                if (!_mask.Visible)
                {
                    try { ShowMask(string.Empty, showSpinner: false); } catch { }
                }

                ApplyFilterAndRender();
                return;
            }

            // se siamo su "Il mio computer" e non ci sono cartelle configurate
            // per Film/Video/Foto/Musica → schermata vuota full-page
            if (string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase)
                && IsLocalLibraryCategory(_selCat)
                && rootsList.Count == 0)
            {
                HideMask();

                // qui vogliamo SOLO l’empty-state centrale
                _secAll.Visible = false;
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;

                ShowInlineRootsCallToAction(_selCat);
                return;
            }

            // ----- 1) prova a caricare SUBITO dall'indice persistente -----
            List<FileInfo> initial = new();
            bool useIndex = string.Equals(srcNow, "Il mio computer", StringComparison.OrdinalIgnoreCase)
                            && IsLocalLibraryCategory(catNow);

            if (useIndex)
            {
                var stored = _libraryIndex.GetPaths(catNow);
                if (stored.Count > 0)
                {
                    // NON facciamo File.Exists qui: creiamo solo i FileInfo
                    foreach (var p in stored)
                    {
                        if (ShouldIgnoreMediaPath(p))
                            continue;

                        try
                        {
                            initial.Add(new FileInfo(p));
                        }
                        catch { }
                    }

                    // pulizia dei path inesistenti in background, per non bloccare l'UI
                    Task.Run(() =>
                    {
                        try { _libraryIndex.RemoveMissing(catNow); }
                        catch { }
                    }, ct);
                }
            }

            bool hadIndexInitial = initial.Count > 0;

            if (hadIndexInitial)
            {
                lock (_cacheLock)
                    _cache = initial;

                ApplyFilterAndRender();
            }
            else
            {
                if (useIndex)
                {
                    // per la prima indicizzazione locale mostriamo la mask con lo spinner
                    ShowMask("Caricamento libreria in corso…");

                    _grid.SuspendLayout();
                    _grid.Visible = false;
                    _grid.Controls.Clear();
                    _grid.ResumeLayout();

                    _grid.Controls.Add(new InfoRow("Indicizzazione della libreria in corso…"));
                    _grid.Visible = true;
                    _grid.UpdateThemedScrollbar();
                }
                else
                {
                    // per le altre sorgenti (es. DLNA) la mask ha già senso
                    ShowMask("Ricerca in corso…");
                    _grid.SuspendLayout();
                    _grid.Visible = false;
                    _grid.Controls.Clear();
                    _grid.ResumeLayout();
                }
            }

            var roots = rootsList;

            // ----- 2) in background fai la scansione completa e aggiorna indice + UI -----
            Task.Run(() =>
            {
                var list = new List<FileInfo>();
                try
                {
                    foreach (var root in roots)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (IsSystemPath(root)) continue;

                        try
                        {
                            foreach (var f in EnumerateFilesSafe(root, exts, ct))
                            {
                                if (ct.IsCancellationRequested) break;
                                if (ShouldIgnoreMediaPath(f))
                                    continue;

                                try
                                {
                                    var fi = new FileInfo(f);
                                    if (fi.Exists)
                                        list.Add(fi);
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                if (ct.IsCancellationRequested) return;

                if (useIndex && !hadIndexInitial && string.Equals(catNow, "Film", StringComparison.OrdinalIgnoreCase))
                {
                    try { PrewarmFilmMetadataCache(list.Take(250), ct); }
                    catch (OperationCanceledException) { return; }
                    catch { }
                }

                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) return;
                        if (!ReferenceEquals(_scanCts, thisScanCts)) return;
                        if (_selCat != catNow || _selSrc != srcNow) return;

                        var previousIndexedSnapshot = initial;

                        lock (_cacheLock)
                            _cache = list;

                        if (useIndex)
                            _libraryIndex.ReplacePaths(catNow, list.Select(fi => fi.FullName));

                        UpdateRecentsFromScanFor(catNow, list);

                        bool backgroundResultChanged = HasFileInfoPathSetChanged(previousIndexedSnapshot, list);

                        // Se avevamo già mostrato roba dall'indice JSON, evitiamo il ricalcolo completo
                        // solo quando il risultato di background è identico. Se invece i file sono cambiati,
                        // aggiorniamo davvero la griglia e il carosello.
                        bool shouldRerender = !(useIndex && hadIndexInitial) || backgroundResultChanged;

                        if (shouldRerender)
                        {
                            // ApplyFilterAndRender si occuperà di togliere la mask quando i dati sono pronti
                            ApplyFilterAndRender();

                            _grid.UpdateThemedScrollbar();
                            _header.Invalidate();
                            _grid.Invalidate();
                        }
                        else
                        {
                            // qui davvero non rifacciamo nulla, quindi la mask si può togliere
                            HideMask();
                        }
                    }));
                }
            }, ct);
        }

        private static bool HasFileInfoPathSetChanged(IEnumerable<FileInfo>? previous, IEnumerable<FileInfo>? current)
        {
            var previousPaths = (previous ?? Enumerable.Empty<FileInfo>())
                .Where(fi => fi != null)
                .Select(fi => fi.FullName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var currentPaths = (current ?? Enumerable.Empty<FileInfo>())
                .Where(fi => fi != null)
                .Select(fi => fi.FullName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (previousPaths.Count != currentPaths.Count)
                return true;

            var set = new HashSet<string>(previousPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var path in currentPaths)
            {
                if (!set.Remove(path))
                    return true;
            }

            return set.Count != 0;
        }

        private static bool ShouldIgnoreMediaPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            try
            {
                string normalized = path.Replace('/', '\\');
                if (Regex.IsMatch(normalized,
                    @"(?i)\\(?:sample(?:,screens)?|samples|screens?|screen ?caps?|trailers?|teasers?|extras?|featurettes?|proofs?)\\"))
                {
                    return true;
                }

                string fileName = Path.GetFileNameWithoutExtension(normalized) ?? string.Empty;
                if (Regex.IsMatch(fileName,
                    @"(?ix)(?:^|[\s._\-\(\[])(sample|trailer|teaser|promo|clip)(?:$|[\s._\-\)\]])"))
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private CancellationTokenSource GetOrNewThumbCts()
        {
            if (_thumbCts == null || _thumbCts.IsCancellationRequested)
                _thumbCts = new CancellationTokenSource();
            return _thumbCts;
        }
        private void PrewarmFilmMetadataCache(IEnumerable<FileInfo> items, CancellationToken ct)
        {
            foreach (var fi in items ?? Enumerable.Empty<FileInfo>())
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    string path = fi.FullName;
                    bool hasPoster = !string.IsNullOrWhiteSpace(MovieMetadataService.GetCachedPosterPath(path));
                    bool titleResolved = MovieMetadataService.IsCachedTitleResolved(path);
                    if (hasPoster && titleResolved)
                        continue;

                    double? durationSeconds = null;
                    var mins = GetDurationMinutesCached(path);
                    if (mins.HasValue)
                        durationSeconds = mins.Value * 60.0;

                    MovieMetadataService.ResolveTitleAndPoster(path, durationSeconds, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }
            }
        }

        private void ApplyFilterAndRender()
        {
            // snapshot
            List<FileInfo> cacheSnapshot;
            lock (_cacheLock) cacheSnapshot = _cache.ToList();

            var category = _selCat;
            var srcSel = _selSrc;
            var selExt = _selExt;
            var sortIndex = _sortIndex;
            var selectedPlaylistKeySnapshot = _selectedPlaylistKey;
            var selectedPlaylistBucketSnapshot = _selectedPlaylistBucketKey;
            var selectedFavoritesBucketSnapshot = _selectedFavoritesBucketKey;
            var queryLower = (_search.Text ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            _filterCts?.Cancel();
            _filterCts = new CancellationTokenSource();
            var ct = _filterCts.Token;

            Task.Run(() =>
            {
                var filtered = BuildFilteredListCore(
                    cacheSnapshot,
                    category,
                    selExt,
                    sortIndex,
                    queryLower,
                    ct);

                if (ct.IsCancellationRequested || IsDisposed)
                    return;

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || ct.IsCancellationRequested)
                            return;

                        // se nel frattempo l'utente è andato altrove, ignora
                        if (!string.Equals(_selCat, category, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(_selSrc, srcSel, StringComparison.OrdinalIgnoreCase))
                            return;

                        if (string.Equals(category, "Playlist", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.Equals(_selectedPlaylistKey ?? string.Empty, selectedPlaylistKeySnapshot ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                                return;
                            if (!string.Equals(_selectedPlaylistBucketKey ?? string.Empty, selectedPlaylistBucketSnapshot ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                                return;
                        }

                        if (string.Equals(category, "Preferiti", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(_selectedFavoritesBucketKey ?? string.Empty, selectedFavoritesBucketSnapshot ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                            return;

                        // Se la mask è già visibile (tipicamente cambio categoria/sorgente),
                        // la lasciamo su finché il render progressivo non è COMPLETO, così
                        // non si vedono “distruzione/costruzione” degli elementi.
                        bool keepMaskUntilDone = _mask.Visible;
                        if (!keepMaskUntilDone)
                            HideMask();

                        var renderItems = BuildRenderItemsFromFilteredFiles(filtered, category);
                        StartProgressiveRender(renderItems, keepMaskUntilDone);
                    }));
                }
                catch
                {
                    // form già distrutta, ignora
                }
            });
        }


        private static readonly Regex _tvEpisodePathRegex = new Regex(
            @"(?:\bS\d{1,2}E\d{1,3}\b)|(?:\b\d{1,2}x\d{1,3}\b)|(?:\b(?:season|stagione)\s*\d{1,2}\b)|(?:\b(?:episode|episodio|ep)\s*\d{1,3}\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool LooksLikeTvEpisodePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string sample = path
                .Replace(Path.DirectorySeparatorChar, ' ')
                .Replace(Path.AltDirectorySeparatorChar, ' ')
                .Replace('.', ' ')
                .Replace('_', ' ');

            return _tvEpisodePathRegex.IsMatch(sample);
        }

        private bool BelongsToMovieOrSeriesCategory(string path, double? durationMinutes)
        {
            var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            if (!IsAnyVideoExtension(ext))
                return false;

            if (LooksLikeTvEpisodePath(path))
                return true;

            return durationMinutes.HasValue && durationMinutes.Value >= 40.0;
        }

        private bool BelongsToMovieOrSeriesCategory(FileInfo fi)
        {
            return BelongsToMovieOrSeriesCategory(fi.FullName, GetDurationMinutesCached(fi.FullName));
        }

        private bool BelongsToShortVideoCategory(string path, double? durationMinutes)
        {
            var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            if (!IsAnyVideoExtension(ext))
                return false;

            return !BelongsToMovieOrSeriesCategory(path, durationMinutes);
        }

        private bool BelongsToShortVideoCategory(FileInfo fi)
        {
            return BelongsToShortVideoCategory(fi.FullName, GetDurationMinutesCached(fi.FullName));
        }

        private List<FileInfo> BuildFilteredListCore(
        List<FileInfo> src,
        string category,
        string selExt,
        int sortIndex,
        string queryLower,
        CancellationToken ct)
        {
            bool isPlaylistCategory = string.Equals(category, "Playlist", StringComparison.OrdinalIgnoreCase);
            bool isPlaylistDetail = isPlaylistCategory && !string.IsNullOrWhiteSpace(_selectedPlaylistKey);

            // categoria Preferiti / Playlist → rileggo direttamente dagli store persistenti
            if (string.Equals(category, "Preferiti", StringComparison.OrdinalIgnoreCase))
            {
                var favs = _favs.All()
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    .Select(p =>
                    {
                        if (ct.IsCancellationRequested) return null;
                        try { return new FileInfo(p); }
                        catch { return null; }
                    })
                    .Where(fi => fi != null)
                    .Cast<FileInfo>()
                    .ToList();

                src = favs;
            }
            else if (isPlaylistCategory)
            {
                if (isPlaylistDetail)
                {
                    string playlistKey = _selectedPlaylistKey!;
                    if (!_playlistBuckets.PlaylistExists(playlistKey))
                    {
                        _selectedPlaylistKey = null;
                        return new List<FileInfo>();
                    }

                    var playlist = _playlistBuckets.GetPaths(playlistKey)
                        .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                        .Select(p =>
                        {
                            if (ct.IsCancellationRequested) return null;
                            try { return new FileInfo(p); }
                            catch { return null; }
                        })
                        .Where(fi => fi != null)
                        .Cast<FileInfo>()
                        .ToList();

                    src = playlist;
                }
                else
                {
                    src = new List<FileInfo>();
                }
            }

            if (ct.IsCancellationRequested)
                return new List<FileInfo>();

            // filtro testo (nome + percorso)
            if (!string.IsNullOrEmpty(queryLower))
            {
                var tokens = queryLower
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (tokens.Length > 0)
                {
                    src = src.Where(fi =>
                    {
                        if (ct.IsCancellationRequested) return false;

                        string name = fi.Name.ToLowerInvariant();
                        string path = (fi.DirectoryName ?? "").ToLowerInvariant();

                        foreach (var t in tokens)
                            if (!(name.Contains(t) || path.Contains(t)))
                                return false;
                        return true;
                    }).ToList();
                }
            }

            string catLower = category.ToLowerInvariant();
            bool isFilm = catLower == "film";
            bool isVideo = catLower == "video";
            bool isCollectionHub = catLower == "preferiti" || catLower == "playlist";

            if (isFilm || isVideo)
            {
                src = src.Where(fi =>
                {
                    if (ct.IsCancellationRequested) return false;
                    return isFilm
                        ? BelongsToMovieOrSeriesCategory(fi)
                        : BelongsToShortVideoCategory(fi);
                }).ToList();
            }
            else if (!isCollectionHub)
            {
                // altre categorie: filtro per estensione
                var allowed = new HashSet<string>(ExtsForCategory(category), StringComparer.OrdinalIgnoreCase);
                src = src.Where(fi =>
                {
                    if (ct.IsCancellationRequested) return false;
                    return allowed.Contains(Path.GetExtension(fi.FullName));
                }).ToList();
            }

            // filtro chip estensione
            if (!string.Equals(selExt, "Tutte", StringComparison.OrdinalIgnoreCase))
            {
                src = src.Where(fi =>
                {
                    if (ct.IsCancellationRequested) return false;
                    return string.Equals(
                        Path.GetExtension(fi.FullName),
                        selExt,
                        StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }

            if (ct.IsCancellationRequested)
                return new List<FileInfo>();

            // ordinamento (nelle playlist dettagliate manteniamo l'ordine manuale)
            if (!isPlaylistDetail)
            {
                src = sortIndex switch
                {
                    1 => src.OrderBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                    2 => src.OrderByDescending(fi => fi.Length).ToList(),
                    _ => src.OrderByDescending(fi => fi.LastWriteTimeUtc).ToList()
                };
            }

            if (ct.IsCancellationRequested)
                return new List<FileInfo>();

            return src.ToList();
        }

        private List<LibraryRenderItem> BuildRenderItemsFromFilteredFiles(List<FileInfo> src, string category)
        {
            var items = new List<LibraryRenderItem>();

            if (string.Equals(category, "Film", StringComparison.OrdinalIgnoreCase))
            {
                var filmSections = BuildMovieAndSeriesRenderItems(src);
                if (_seriesSectionFirst)
                {
                    AppendRenderSection(items, "Serie TV", filmSections.SeriesItems, "Serie");
                    AppendRenderSection(items, "Film", filmSections.MovieItems, "Film");
                }
                else
                {
                    AppendRenderSection(items, "Film", filmSections.MovieItems, "Film");
                    AppendRenderSection(items, "Serie TV", filmSections.SeriesItems, "Serie");
                }

                return items;
            }

            if (string.Equals(category, "Preferiti", StringComparison.OrdinalIgnoreCase))
                return BuildFavoritesRenderItems(src);

            if (string.Equals(category, "Playlist", StringComparison.OrdinalIgnoreCase))
                return BuildPlaylistRenderItems(src);

            foreach (var fi in src)
                items.Add(LibraryRenderItem.FromFile(fi));
            return items;
        }

        private void AppendRenderSection(List<LibraryRenderItem> target, string title, IEnumerable<LibraryRenderItem>? sectionItems, string? sectionBucket = null)
        {
            if (target == null || sectionItems == null)
                return;

            var materialized = sectionItems
                .Where(item => item != null)
                .ToList();

            if (materialized.Count == 0)
                return;

            target.Add(LibraryRenderItem.FromSectionTitle(title, sectionBucket));
            target.AddRange(materialized);
        }

        private static string NormalizeCollectionBucketKey(string? bucket)
        {
            string value = (bucket ?? string.Empty).Trim();
            if (value.StartsWith("film", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("serie", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("series", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("tv", StringComparison.OrdinalIgnoreCase))
                return "Film";
            if (value.StartsWith("video", StringComparison.OrdinalIgnoreCase))
                return "Video";
            if (value.StartsWith("foto", StringComparison.OrdinalIgnoreCase))
                return "Foto";
            if (value.StartsWith("music", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("musica", StringComparison.OrdinalIgnoreCase))
                return "Musica";
            return "Video";
        }

        private static string GetCollectionBucketDisplayName(string? bucket)
        {
            return NormalizeCollectionBucketKey(bucket) switch
            {
                "Film" => "Film e Serie TV",
                "Foto" => "Foto",
                "Musica" => "Musica",
                _ => "Video"
            };
        }

        private string InferCollectionBucketForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "Video";

            string ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            string extCategory = string.Empty;
            try { extCategory = CategoryFromExt(ext); } catch { }

            if (string.Equals(extCategory, "Foto", StringComparison.OrdinalIgnoreCase))
                return "Foto";
            if (string.Equals(extCategory, "Musica", StringComparison.OrdinalIgnoreCase))
                return "Musica";

            if (IsAnyVideoExtension(ext))
            {
                double? mins = null;
                try { mins = GetDurationMinutesCached(path); } catch { }
                return (LooksLikeTvEpisodePath(path) || BelongsToMovieOrSeriesCategory(path, mins)) ? "Film" : "Video";
            }

            return "Video";
        }

        internal string ResolveEffectiveCategoryForPath(string? path)
        {
            if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase))
            {
                return InferCollectionBucketForPath(path ?? string.Empty);
            }

            return _selCat;
        }

        private void AppendCollectionHubSection(
            List<LibraryRenderItem> target,
            string title,
            string bucketKey,
            IEnumerable<LibraryRenderItem>? sectionItems,
            string categoryContext)
        {
            if (target == null)
                return;

            target.Add(LibraryRenderItem.FromSectionTitle(title, bucketKey));

            var materialized = sectionItems?
                .Where(item => item != null)
                .ToList() ?? new List<LibraryRenderItem>();

            if (materialized.Count > 0)
                target.AddRange(materialized);
        }

        private void ResetCollectionSelectionStateForTargetCategory(string? targetCategory)
        {
            string normalizedTarget = targetCategory?.Trim() ?? string.Empty;

            if (!string.Equals(normalizedTarget, "Playlist", StringComparison.OrdinalIgnoreCase))
            {
                _selectedPlaylistKey = null;
                _selectedPlaylistBucketKey = null;
            }

            if (!string.Equals(normalizedTarget, "Preferiti", StringComparison.OrdinalIgnoreCase))
            {
                _selectedFavoritesBucketKey = null;
            }
        }

        private void NavigateToCollectionBucket(string bucketKey)
        {
            string category = NormalizeCollectionBucketKey(bucketKey) switch
            {
                "Foto" => "Foto",
                "Musica" => "Musica",
                "Film" => "Film",
                _ => "Video"
            };

            try { SetCategory(category); } catch { }
        }

        private bool HandleCollectionBackRequested()
        {
            if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(_selectedPlaylistKey))
                {
                    ReturnToPlaylistBucket();
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(_selectedPlaylistBucketKey))
                {
                    ReturnToPlaylistCategories();
                    return true;
                }
            }

            if (string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_selectedFavoritesBucketKey))
            {
                ReturnToFavoritesOverview();
                return true;
            }

            return false;
        }

        private void RequestContentFocusAfterRender()
        {
            try
            {
                _pendingFocusFirstGridItem = true;
                _remoteZone = RemoteZone.Content;
                NotifyRemoteNavigationResetRequested();
                StopAnimatedLibraryVisuals();
                ShowMask(string.Empty, showSpinner: false);
            }
            catch { }
        }

        private void HandlePlaylistCreateRequested()
        {
            string bucketKey = !string.IsNullOrWhiteSpace(_selectedPlaylistKey)
                ? NormalizeCollectionBucketKey(_playlistBuckets.GetPlaylistBucket(_selectedPlaylistKey))
                : NormalizeCollectionBucketKey(_selectedPlaylistBucketKey);

            if (string.IsNullOrWhiteSpace(bucketKey))
                return;

            ShowCreatePlaylistOverlay(bucketKey, name =>
            {
                string playlistKey = _playlistBuckets.EnsurePlaylist(name, bucketKey);
                _selectedPlaylistBucketKey = bucketKey;
                _selectedPlaylistKey = playlistKey;
                try { LayoutHeader(); } catch { }
                RequestContentFocusAfterRender();
                ApplyFilterAndRender();
            });
        }

        private void OpenFavoritesBucket(string bucketKey)
        {
            _selectedPlaylistKey = null;
            _selectedPlaylistBucketKey = null;
            _selectedFavoritesBucketKey = NormalizeCollectionBucketKey(bucketKey);
            try { LayoutHeader(); } catch { }
            RequestContentFocusAfterRender();
            ApplyFilterAndRender();
        }

        private void ReturnToFavoritesOverview()
        {
            if (string.IsNullOrWhiteSpace(_selectedFavoritesBucketKey))
                return;

            _selectedFavoritesBucketKey = null;
            try { LayoutHeader(); } catch { }
            RequestContentFocusAfterRender();
            ApplyFilterAndRender();
        }

        private void OpenPlaylistBucket(string bucketKey)
        {
            _selectedFavoritesBucketKey = null;
            _selectedPlaylistBucketKey = NormalizeCollectionBucketKey(bucketKey);
            _selectedPlaylistKey = null;
            try { LayoutHeader(); } catch { }
            RequestContentFocusAfterRender();
            ApplyFilterAndRender();
        }

        private void OpenPlaylistDetail(string playlistKey)
        {
            if (string.IsNullOrWhiteSpace(playlistKey))
                return;

            _selectedFavoritesBucketKey = null;

            if (!_playlistBuckets.PlaylistExists(playlistKey))
            {
                _selectedPlaylistKey = null;
                try { LayoutHeader(); } catch { }
                RequestContentFocusAfterRender();
                ApplyFilterAndRender();
                return;
            }

            _selectedPlaylistBucketKey = NormalizeCollectionBucketKey(_playlistBuckets.GetPlaylistBucket(playlistKey));
            _selectedPlaylistKey = playlistKey;
            try { LayoutHeader(); } catch { }
            RequestContentFocusAfterRender();
            ApplyFilterAndRender();
        }

        private void ReturnToPlaylistBucket()
        {
            if (string.IsNullOrWhiteSpace(_selectedPlaylistKey))
                return;

            _selectedPlaylistKey = null;
            try { LayoutHeader(); } catch { }
            RequestContentFocusAfterRender();
            ApplyFilterAndRender();
        }

        private void ReturnToPlaylistCategories()
        {
            if (string.IsNullOrWhiteSpace(_selectedPlaylistBucketKey) && string.IsNullOrWhiteSpace(_selectedPlaylistKey))
                return;

            _selectedPlaylistKey = null;
            _selectedPlaylistBucketKey = null;
            try { LayoutHeader(); } catch { }
            RequestContentFocusAfterRender();
            ApplyFilterAndRender();
        }

        private void DeletePlaylistAndRefresh(string playlistKey)
        {
            if (string.IsNullOrWhiteSpace(playlistKey))
                return;

            string playlistName = _playlistBuckets.GetPlaylistName(playlistKey) ?? "questa playlist";
            DialogResult choice;
            try
            {
                choice = MessageBox.Show(
                    this,
                    $"Eliminare la playlist \"{playlistName}\"?{Environment.NewLine}{Environment.NewLine}Gli elementi NON verranno cancellati dal disco.",
                    "Elimina playlist",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
            }
            catch
            {
                choice = DialogResult.Yes;
            }

            if (choice != DialogResult.Yes)
                return;

            if (_playlistBuckets.DeletePlaylist(playlistKey))
            {
                if (string.Equals(_selectedPlaylistKey, playlistKey, StringComparison.OrdinalIgnoreCase))
                    _selectedPlaylistKey = null;

                try { LayoutHeader(); } catch { }
                ApplyFilterAndRender();
            }
        }

        private void CloseTransientMenus()
        {
            try { _itemMenu?.Close(ToolStripDropDownCloseReason.CloseCalled); } catch { }
            try { _playlistHubMenu?.Close(ToolStripDropDownCloseReason.CloseCalled); } catch { }
            try { _menuExt?.Close(ToolStripDropDownCloseReason.CloseCalled); } catch { }
            try { _menuSort?.Close(ToolStripDropDownCloseReason.CloseCalled); } catch { }
        }

        private void DeferUiAction(Action action)
        {
            if (action == null)
                return;

            try { BeginInvoke(action); }
            catch
            {
                try { action(); } catch { }
            }
        }

        private static string? ExtractPlaylistHubKeyFromControl(Control? control)
        {
            for (Control? p = control; p != null; p = p.Parent)
            {
                if (p.Tag is string playlistKey && !string.IsNullOrWhiteSpace(playlistKey))
                    return playlistKey;
            }

            return control?.Tag as string;
        }

        private ContextMenuStrip EnsurePlaylistHubMenu()
        {
            if (_playlistHubMenu != null && !_playlistHubMenu.IsDisposed)
                return _playlistHubMenu;

            _playlistHubMenu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                RenderMode = ToolStripRenderMode.Professional,
                BackColor = Color.FromArgb(26, 26, 26),
                ForeColor = Color.Gainsboro
            };
            ApplyDarkMenuTheme(_playlistHubMenu);
            _playlistHubMenu.Opening += (_, __) => PopulatePlaylistHubMenu(_playlistHubMenu);
            return _playlistHubMenu;
        }

        private void PopulatePlaylistHubMenu(ContextMenuStrip menu)
        {
            if (menu == null)
                return;

            menu.Items.Clear();

            string? playlistKey = ExtractPlaylistHubKeyFromControl(menu.SourceControl);
            if (string.IsNullOrWhiteSpace(playlistKey) || !_playlistBuckets.PlaylistExists(playlistKey))
            {
                menu.Items.Add(new ToolStripMenuItem("Nessuna azione disponibile") { Enabled = false });
                ApplyDarkMenuTheme(menu);
                return;
            }

            string capturedKey = playlistKey;
            string playlistName = _playlistBuckets.GetPlaylistName(capturedKey) ?? capturedKey;
            string playlistBucket = NormalizeCollectionBucketKey(_playlistBuckets.GetPlaylistBucket(capturedKey));
            bool canPlaySequential = SupportsSequentialPlaybackForBucket(playlistBucket);
            bool supportsQueueOps = SupportsQueueOperationsForBucket(playlistBucket);
            var playlistPaths = GetPlaylistQueuePaths(capturedKey);
            bool playlistQueued = playlistPaths.Count > 0 && playlistPaths.All(IsPathQueued);

            var openItem = new ToolStripMenuItem("Apri playlist");
            openItem.Click += (_, __) => OpenPlaylistDetail(capturedKey);
            menu.Items.Add(openItem);

            if (playlistPaths.Count > 0 && canPlaySequential)
            {
                menu.Items.Add(new ToolStripSeparator());

                var playItem = new ToolStripMenuItem("Riproduci playlist");
                playItem.Click += (_, __) =>
                {
                    CloseTransientMenus();
                    RequestQueuePlay(playlistPaths, 0, shuffle: false);
                };
                menu.Items.Add(playItem);

                if (supportsQueueOps)
                {
                    var shuffleItem = new ToolStripMenuItem("Riproduci playlist in shuffle");
                    shuffleItem.Click += (_, __) =>
                    {
                        CloseTransientMenus();
                        RequestQueuePlay(playlistPaths, 0, shuffle: true);
                    };
                    menu.Items.Add(shuffleItem);

                    var queueItem = new ToolStripMenuItem(playlistQueued ? "Rimuovi playlist dalla coda" : "Accoda playlist");
                    queueItem.Click += (_, __) =>
                    {
                        CloseTransientMenus();
                        if (playlistQueued)
                            RequestQueueRemove(playlistPaths);
                        else
                            RequestQueueAppend(playlistPaths);
                    };
                    menu.Items.Add(queueItem);
                }
            }

            if (supportsQueueOps)
                AddQueueSnapshotSubmenu(menu, menu.Items);

            menu.Items.Add(new ToolStripSeparator());

            var deleteItem = new ToolStripMenuItem($"Elimina \"{playlistName}\"");
            deleteItem.Click += (_, __) =>
            {
                CloseTransientMenus();
                DeferUiAction(() => DeletePlaylistAndRefresh(capturedKey));
            };
            menu.Items.Add(deleteItem);

            ApplyDarkMenuTheme(menu);
            ApplyDarkMenuThemeRecursive(menu.Items);
        }

        private static LibraryItemContext? ExtractLibraryItemContextFromControl(Control? control)
        {
            for (Control? p = control; p != null; p = p.Parent)
            {
                if (p.Tag is LibraryItemContext ctx)
                    return ctx;
            }
            return control?.Tag as LibraryItemContext;
        }

        private static FileCard? FindOwningFileCard(Control? control)
        {
            for (Control? p = control; p != null; p = p.Parent)
            {
                if (p is FileCard fileCard)
                    return fileCard;
            }
            return null;
        }

        private static bool IsQueuePlayablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                if (File.Exists(path))
                    return true;
            }
            catch { }

            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
                return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.IsFile;

            return false;
        }

        private static int FindFirstMatchingPathIndex(IReadOnlyList<string> orderedPaths, IEnumerable<string> candidatePaths)
        {
            if (orderedPaths == null || orderedPaths.Count == 0 || candidatePaths == null)
                return -1;

            var candidates = candidatePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
                return -1;

            for (int i = 0; i < orderedPaths.Count; i++)
            {
                string orderedPath = orderedPaths[i];
                if (candidates.Any(candidate => string.Equals(candidate, orderedPath, StringComparison.OrdinalIgnoreCase)))
                    return i;
            }

            return -1;
        }

        private List<string> GetPlaylistQueuePaths(string playlistKey)
        {
            if (string.IsNullOrWhiteSpace(playlistKey) || !_playlistBuckets.PlaylistExists(playlistKey))
                return new List<string>();

            return _playlistBuckets.GetPaths(playlistKey)
                .Where(IsQueuePlayablePath)
                .ToList();
        }

        private void RequestQueuePlay(IEnumerable<string> paths, int startIndex, bool shuffle)
        {
            var queuePaths = (paths ?? Enumerable.Empty<string>())
                .Where(IsQueuePlayablePath)
                .ToList();

            if (queuePaths.Count == 0)
                return;

            if (startIndex < 0)
                startIndex = 0;
            if (startIndex >= queuePaths.Count)
                startIndex = queuePaths.Count - 1;

            try { QueuePlayRequested?.Invoke(queuePaths, startIndex, shuffle); } catch { }
        }

        private void RequestQueueAppend(IEnumerable<string> paths)
        {
            var queuePaths = (paths ?? Enumerable.Empty<string>())
                .Where(IsQueuePlayablePath)
                .ToList();

            if (queuePaths.Count == 0)
                return;

            try { QueueAppendRequested?.Invoke(queuePaths); } catch { }
        }

        private void RequestQueueRemove(IEnumerable<string> paths)
        {
            var queuePaths = (paths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (queuePaths.Count == 0)
                return;

            try { QueueRemoveRequested?.Invoke(queuePaths); } catch { }
        }

        private void RequestQueueClear()
        {
            try { QueueClearRequested?.Invoke(); } catch { }
        }

        private bool IsPathQueued(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try { return QueueContainsPathResolver?.Invoke(path) == true; }
            catch { return false; }
        }

        private static bool SupportsQueueOperationsForBucket(string? bucket)
        {
            string normalized = NormalizeCollectionBucketKey(bucket);
            return string.Equals(normalized, "Video", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Musica", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SupportsSequentialPlaybackForBucket(string? bucket)
        {
            string normalized = NormalizeCollectionBucketKey(bucket);
            return string.Equals(normalized, "Film", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Video", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Musica", StringComparison.OrdinalIgnoreCase);
        }

        private List<PlaybackQueueViewItem> GetQueueSnapshotItems()
        {
            try
            {
                return (QueueSnapshotResolver?.Invoke() ?? Array.Empty<PlaybackQueueViewItem>())
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path))
                    .OrderBy(item => item.Index)
                    .ToList();
            }
            catch
            {
                return new List<PlaybackQueueViewItem>();
            }
        }

        private void RequestQueuePlayPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try { QueuePlayPathRequested?.Invoke(path); } catch { }
        }

        private void RequestQueueMovePath(string path, int delta)
        {
            if (string.IsNullOrWhiteSpace(path) || delta == 0)
                return;

            try { QueueMoveRequested?.Invoke(path, delta); } catch { }
        }

        private void RequestQueueEditor()
        {
            try { QueueEditorRequested?.Invoke(); } catch { }
        }

        private bool IsHeaderShuffleMusicContext()
        {
            if (string.Equals(_selCat, "Musica", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_selectedPlaylistKey))
            {
                string playlistBucket = NormalizeCollectionBucketKey(_playlistBuckets.GetPlaylistBucket(_selectedPlaylistKey));
                return string.Equals(playlistBucket, "Musica", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase))
            {
                string favoritesBucket = NormalizeCollectionBucketKey(_selectedFavoritesBucketKey);
                return string.Equals(favoritesBucket, "Musica", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private void CollectVisibleQueuePlayablePaths(Control parent, List<string> target)
        {
            if (parent == null || target == null)
                return;

            foreach (Control child in parent.Controls)
            {
                if (child == null || child.IsDisposed || !child.Visible)
                    continue;

                if (child is FileCard fileCard)
                {
                    string path = fileCard.FilePath;
                    if (IsQueuePlayablePath(path))
                        target.Add(path);
                    continue;
                }

                CollectVisibleQueuePlayablePaths(child, target);
            }
        }

        private List<string> GetHeaderQueuePlayablePaths()
        {
            if (!IsHeaderShuffleMusicContext())
                return new List<string>();

            var paths = new List<string>();
            try { CollectVisibleQueuePlayablePaths(_grid, paths); } catch { }

            return paths
                .Where(IsQueuePlayablePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<string> GetHeaderShufflePaths()
            => GetHeaderQueuePlayablePaths();

        private bool ShouldShowHeaderPlayButton()
            => GetHeaderQueuePlayablePaths().Count > 0;

        private bool ShouldShowHeaderShuffleButton()
            => GetHeaderShufflePaths().Count > 1;

        private bool ShouldShowCollectionBackButton()
        {
            if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(_selectedPlaylistKey) || !string.IsNullOrWhiteSpace(_selectedPlaylistBucketKey);

            if (string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(_selectedFavoritesBucketKey);

            return false;
        }

        private bool ShouldShowCreatePlaylistButton()
        {
            if (!string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase))
                return false;

            string bucketKey = !string.IsNullOrWhiteSpace(_selectedPlaylistKey)
                ? NormalizeCollectionBucketKey(_playlistBuckets.GetPlaylistBucket(_selectedPlaylistKey))
                : NormalizeCollectionBucketKey(_selectedPlaylistBucketKey);

            return !string.IsNullOrWhiteSpace(bucketKey);
        }

        private void RequestHeaderPlay()
        {
            var paths = GetHeaderQueuePlayablePaths();
            if (paths.Count == 0)
                return;

            RequestQueuePlay(paths, 0, shuffle: false);
        }

        private void RequestHeaderShuffle()
        {
            var paths = GetHeaderShufflePaths();
            if (paths.Count <= 1)
                return;

            RequestQueuePlay(paths, 0, shuffle: true);
        }

        private void AddQueueSnapshotSubmenu(ContextMenuStrip rootMenu, ToolStripItemCollection targetItems)
        {
            var snapshot = GetQueueSnapshotItems();
            if (snapshot.Count == 0)
                return;

            if (targetItems.Count > 0 && targetItems[targetItems.Count - 1] is not ToolStripSeparator)
                targetItems.Add(new ToolStripSeparator());

            var openEditorItem = new ToolStripMenuItem($"Apri editor coda ({snapshot.Count})…");
            openEditorItem.Click += (_, __) =>
            {
                CloseTransientMenus();
                RequestQueueEditor();
            };
            targetItems.Add(openEditorItem);

            var clearQueueItem = new ToolStripMenuItem("Svuota coda");
            clearQueueItem.Click += (_, __) =>
            {
                CloseTransientMenus();
                RequestQueueClear();
            };
            targetItems.Add(clearQueueItem);

            ApplyDarkMenuTheme(rootMenu);
            ApplyDarkMenuThemeRecursive(rootMenu.Items);
        }

        private ContextMenuStrip EnsureLibraryItemMenu()

        {
            if (_itemMenu != null && !_itemMenu.IsDisposed)
                return _itemMenu;

            _itemMenu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                RenderMode = ToolStripRenderMode.Professional,
                BackColor = Color.FromArgb(26, 26, 26),
                ForeColor = Color.Gainsboro
            };
            ApplyDarkMenuTheme(_itemMenu);
            _itemMenu.Opening += (_, __) => PopulateLibraryItemMenu(_itemMenu);
            return _itemMenu;
        }

        private void PopulateLibraryItemMenu(ContextMenuStrip menu)
        {
            if (menu == null)
                return;

            menu.Items.Clear();

            var ctx = ExtractLibraryItemContextFromControl(menu.SourceControl);
            var paths = ctx?.GetPaths()
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (ctx == null || paths.Count == 0)
            {
                menu.Items.Add(new ToolStripMenuItem("Nessuna azione disponibile") { Enabled = false });
                ApplyDarkMenuTheme(menu);
                return;
            }

            string primaryPath = ctx.RepresentativePath;
            bool allFav = paths.All(p => _favs.IsFav(p));
            bool allQueued = paths.Count > 0 && paths.All(IsPathQueued);
            string inferredBucket = InferCollectionBucketForPath(primaryPath);
            var bucketPlaylists = _playlistBuckets.GetPlaylists(inferredBucket);
            var assignedPlaylistKeys = paths
                .SelectMany(p => _playlistBuckets.GetPlaylistKeysForPath(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool anyPlaylist = assignedPlaylistKeys.Count > 0;
            bool isPlaylistDetail = string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(_selectedPlaylistKey);
            string? activePlaylistKey = isPlaylistDetail ? _selectedPlaylistKey : null;
            var activePlaylistPaths = !string.IsNullOrWhiteSpace(activePlaylistKey)
                ? GetPlaylistQueuePaths(activePlaylistKey!)
                : new List<string>();
            int activePlaylistStartIndex = FindFirstMatchingPathIndex(activePlaylistPaths, paths);
            bool canPlayFromCurrentPlaylist = activePlaylistPaths.Count > 0 && activePlaylistStartIndex >= 0;
            bool singlePathInActivePlaylist = !ctx.IsSeasonGroup &&
                                              !string.IsNullOrWhiteSpace(activePlaylistKey) &&
                                              paths.Count == 1 &&
                                              _playlistBuckets.Contains(primaryPath, activePlaylistKey!);
            bool supportsQueueOps = SupportsQueueOperationsForBucket(inferredBucket);
            bool supportsSequentialPlayback = SupportsSequentialPlaybackForBucket(inferredBucket);
            string activePlaylistBucket = !string.IsNullOrWhiteSpace(activePlaylistKey)
                ? NormalizeCollectionBucketKey(_playlistBuckets.GetPlaylistBucket(activePlaylistKey!))
                : inferredBucket;
            bool activePlaylistSupportsSequentialPlayback = SupportsSequentialPlaybackForBucket(activePlaylistBucket);
            bool activePlaylistSupportsQueueOps = SupportsQueueOperationsForBucket(activePlaylistBucket);

            var openItem = new ToolStripMenuItem(ctx.IsSeasonGroup ? "Apri stagione" : "Apri");
            openItem.Click += (_, __) =>
            {
                if (ctx.IsSeasonGroup && ctx.SeasonGroup != null)
                    ShowSeasonEpisodeOverlay(ctx.SeasonGroup, ctx.SeasonGroup.SeriesTitle);
                else if (!string.IsNullOrWhiteSpace(primaryPath))
                    SafeOpen(primaryPath);
            };
            menu.Items.Add(openItem);

            if (supportsSequentialPlayback)
            {
                var playNowItem = new ToolStripMenuItem("Riproduci ora");
                playNowItem.Click += (_, __) =>
                {
                    CloseTransientMenus();
                    RequestQueuePlay(paths, 0, shuffle: false);
                };
                menu.Items.Add(playNowItem);
            }

            if (supportsQueueOps && paths.Count > 1)
            {
                var playShuffleItem = new ToolStripMenuItem("Riproduci in shuffle");
                playShuffleItem.Click += (_, __) =>
                {
                    CloseTransientMenus();
                    RequestQueuePlay(paths, 0, shuffle: true);
                };
                menu.Items.Add(playShuffleItem);
            }

            if (supportsSequentialPlayback || supportsQueueOps || canPlayFromCurrentPlaylist)
                menu.Items.Add(new ToolStripSeparator());

            if (canPlayFromCurrentPlaylist && activePlaylistSupportsSequentialPlayback)
            {
                var playFromHereItem = new ToolStripMenuItem("Riproduci playlist da qui");
                playFromHereItem.Click += (_, __) =>
                {
                    CloseTransientMenus();
                    RequestQueuePlay(activePlaylistPaths, activePlaylistStartIndex, shuffle: false);
                };
                menu.Items.Add(playFromHereItem);

            }

            if (supportsQueueOps)
            {
                var queueItem = new ToolStripMenuItem(allQueued ? "Rimuovi dalla coda" : "Aggiungi alla coda");
                queueItem.Click += (_, __) =>
                {
                    CloseTransientMenus();
                    if (allQueued)
                        RequestQueueRemove(paths);
                    else
                        RequestQueueAppend(paths);
                };
                menu.Items.Add(queueItem);
            }

            if (supportsQueueOps || activePlaylistSupportsQueueOps)
                AddQueueSnapshotSubmenu(menu, menu.Items);
            menu.Items.Add(new ToolStripSeparator());

            var favItem = new ToolStripMenuItem(allFav ? "Rimuovi dai Preferiti" : "Aggiungi ai Preferiti");
            favItem.Click += (_, __) =>
            {
                bool nextFav = !allFav;
                foreach (var p in paths)
                    _favs.Set(p, nextFav);

                try
                {
                    var owningCard = FindOwningFileCard(menu.SourceControl);
                    if (owningCard != null && paths.Count == 1)
                        owningCard.SetFavoriteState(nextFav);
                }
                catch { }

                if (string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase))
                    ApplyFilterAndRender();
            };
            menu.Items.Add(favItem);

            var playlistRoot = new ToolStripMenuItem("Playlist");
            foreach (var playlist in bucketPlaylists)
            {
                string capturedKey = playlist.Key;
                bool allInPlaylist = paths.Count > 0 && paths.All(p => _playlistBuckets.Contains(p, capturedKey));
                string label = allInPlaylist ? playlist.Name + "  ✓" : playlist.Name;
                var playlistItem = new ToolStripMenuItem(label);
                playlistItem.Click += (_, __) =>
                {
                    foreach (var p in paths)
                        _playlistBuckets.Set(p, capturedKey, true);

                    if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase))
                        ApplyFilterAndRender();
                };
                playlistRoot.DropDownItems.Add(playlistItem);
            }

            if (playlistRoot.DropDownItems.Count > 0)
                playlistRoot.DropDownItems.Add(new ToolStripSeparator());

            var newPlaylistItem = new ToolStripMenuItem("Nuova playlist…");
            newPlaylistItem.Click += (_, __) =>
            {
                var capturedPaths = paths.ToList();
                string capturedBucket = NormalizeCollectionBucketKey(inferredBucket);
                CloseTransientMenus();
                DeferUiAction(() =>
                {
                    ShowCreatePlaylistOverlay(capturedBucket, name =>
                    {
                        string playlistKey = _playlistBuckets.EnsurePlaylist(name, capturedBucket);
                        foreach (var p in capturedPaths)
                            _playlistBuckets.Set(p, playlistKey, true);

                        if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase))
                        {
                            _selectedPlaylistBucketKey = capturedBucket;
                            _selectedPlaylistKey = playlistKey;
                            try { LayoutHeader(); } catch { }
                            RequestContentFocusAfterRender();
                            ApplyFilterAndRender();
                        }
                    });
                });
            };
            playlistRoot.DropDownItems.Add(newPlaylistItem);
            menu.Items.Add(playlistRoot);

            if (anyPlaylist)
            {
                var removePlaylistRoot = new ToolStripMenuItem("Rimuovi da Playlist");
                foreach (var playlistKey in assignedPlaylistKeys)
                {
                    string capturedKey = playlistKey;
                    string label = _playlistBuckets.GetPlaylistName(capturedKey) ?? capturedKey;
                    var playlistItem = new ToolStripMenuItem(label);
                    playlistItem.Click += (_, __) =>
                    {
                        foreach (var p in paths)
                            _playlistBuckets.Remove(p, capturedKey);

                        if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase))
                            ApplyFilterAndRender();
                    };
                    removePlaylistRoot.DropDownItems.Add(playlistItem);
                }

                if (assignedPlaylistKeys.Count > 1)
                    removePlaylistRoot.DropDownItems.Add(new ToolStripSeparator());

                var removeEverywhere = new ToolStripMenuItem("Rimuovi da tutte le playlist");
                removeEverywhere.Click += (_, __) =>
                {
                    foreach (var p in paths)
                        _playlistBuckets.RemoveEverywhere(p);

                    if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase))
                        ApplyFilterAndRender();
                };
                removePlaylistRoot.DropDownItems.Add(removeEverywhere);
                menu.Items.Add(removePlaylistRoot);
            }

            if (singlePathInActivePlaylist && !string.IsNullOrWhiteSpace(activePlaylistKey))
            {
                menu.Items.Add(new ToolStripSeparator());

                string capturedActivePlaylistKey = activePlaylistKey!;
                var removeCurrent = new ToolStripMenuItem("Rimuovi da questa playlist");
                removeCurrent.Click += (_, __) =>
                {
                    _playlistBuckets.Remove(primaryPath, capturedActivePlaylistKey);
                    ApplyFilterAndRender();
                };
                menu.Items.Add(removeCurrent);

                var moveUpItem = new ToolStripMenuItem("Sposta su")
                {
                    Enabled = _playlistBuckets.CanMove(capturedActivePlaylistKey, primaryPath, -1)
                };
                moveUpItem.Click += (_, __) =>
                {
                    if (_playlistBuckets.Move(capturedActivePlaylistKey, primaryPath, -1))
                        ApplyFilterAndRender();
                };
                menu.Items.Add(moveUpItem);

                var moveDownItem = new ToolStripMenuItem("Sposta giù")
                {
                    Enabled = _playlistBuckets.CanMove(capturedActivePlaylistKey, primaryPath, 1)
                };
                moveDownItem.Click += (_, __) =>
                {
                    if (_playlistBuckets.Move(capturedActivePlaylistKey, primaryPath, 1))
                        ApplyFilterAndRender();
                };
                menu.Items.Add(moveDownItem);
            }

            ApplyDarkMenuTheme(menu);
            ApplyDarkMenuThemeRecursive(menu.Items);
        }

        private (List<LibraryRenderItem> MovieItems, List<LibraryRenderItem> SeriesItems) BuildMovieAndSeriesRenderItems(IEnumerable<FileInfo> files)
        {
            var movieItems = new List<LibraryRenderItem>();
            var groupMap = new Dictionary<string, TvSeasonGroup>(StringComparer.OrdinalIgnoreCase);
            var seriesGroups = new List<TvSeasonGroup>();

            foreach (var fi in files ?? Enumerable.Empty<FileInfo>())
            {
                var info = MovieMetadataService.ExtractMediaTitleInfoFromPath(fi.FullName);
                if (!info.IsTvEpisode || string.IsNullOrWhiteSpace(info.SeriesTitle))
                {
                    movieItems.Add(LibraryRenderItem.FromFile(fi));
                    continue;
                }

                string seriesTitle = info.SeriesTitle.Trim();
                string key = seriesTitle + "|" + (info.SeasonNumber.HasValue ? info.SeasonNumber.Value.ToString("00") : "specials");

                if (!groupMap.TryGetValue(key, out var group))
                {
                    group = new TvSeasonGroup
                    {
                        SeriesTitle = seriesTitle,
                        SeasonNumber = info.SeasonNumber,
                        RepresentativeEpisodeNumber = info.EpisodeNumber,
                        RepresentativePath = fi.FullName,
                        DisplayName = BuildSeasonGroupDisplayName(seriesTitle, info.SeasonNumber)
                    };

                    groupMap[key] = group;
                    seriesGroups.Add(group);
                }
                else if (!group.RepresentativeEpisodeNumber.HasValue ||
                         (info.EpisodeNumber.HasValue && info.EpisodeNumber.Value < group.RepresentativeEpisodeNumber.Value))
                {
                    group.RepresentativeEpisodeNumber = info.EpisodeNumber;
                    group.RepresentativePath = fi.FullName;
                }

                string bestTitle = string.Empty;
                try { bestTitle = MovieMetadataService.GetBestKnownDisplayTitle(fi.FullName); } catch { }

                string resolvedSeriesTitle = ExtractSeriesTitleFromBestKnownDisplay(bestTitle, info);
                if (!string.IsNullOrWhiteSpace(resolvedSeriesTitle) &&
                    !string.Equals(group.SeriesTitle, resolvedSeriesTitle, StringComparison.OrdinalIgnoreCase))
                {
                    group.SeriesTitle = resolvedSeriesTitle;
                    group.DisplayName = BuildSeasonGroupDisplayName(resolvedSeriesTitle, info.SeasonNumber);
                }

                group.Episodes.Add(new TvEpisodeOption
                {
                    File = fi,
                    EpisodeNumber = info.EpisodeNumber,
                    DisplayText = BuildEpisodeChoiceDisplay(info, fi, bestTitle)
                });
            }

            foreach (var group in seriesGroups)
            {
                group.Episodes = group.Episodes
                    .OrderBy(ep => ep.EpisodeNumber ?? int.MaxValue)
                    .ThenBy(ep => ep.DisplayText, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var seriesItems = seriesGroups
                .Select(LibraryRenderItem.FromSeasonGroup)
                .ToList();

            return (movieItems, seriesItems);
        }

        private static string BuildCountLabel(int count, string singular, string plural)
        {
            return count == 1 ? $"1 {singular}" : $"{count} {plural}";
        }

        private string[] GetPlaylistSearchTokens()
        {
            string queryLower = (_search.Text ?? string.Empty).Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(queryLower)
                ? Array.Empty<string>()
                : queryLower.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private bool PlaylistMatchesSearch((string Key, string Name, string Bucket) playlist, string[] tokens)
        {
            if (tokens == null || tokens.Length == 0)
                return true;

            string haystack = (playlist.Name + " " + GetCollectionBucketDisplayName(playlist.Bucket)).ToLowerInvariant();
            foreach (var token in tokens)
                if (!haystack.Contains(token))
                    return false;
            return true;
        }

        private List<LibraryRenderItem> BuildFavoritesRenderItems(IEnumerable<FileInfo> files)
        {
            var filmLikeFiles = new List<FileInfo>();
            var shortVideos = new List<FileInfo>();
            var photos = new List<FileInfo>();
            var music = new List<FileInfo>();

            foreach (var fi in files ?? Enumerable.Empty<FileInfo>())
            {
                string ext = (Path.GetExtension(fi.FullName) ?? string.Empty).ToLowerInvariant();
                string extCategory = string.Empty;
                try { extCategory = CategoryFromExt(ext); } catch { }

                if (string.Equals(extCategory, "Foto", StringComparison.OrdinalIgnoreCase))
                {
                    photos.Add(fi);
                    continue;
                }

                if (string.Equals(extCategory, "Musica", StringComparison.OrdinalIgnoreCase))
                {
                    music.Add(fi);
                    continue;
                }

                if (IsAnyVideoExtension(ext))
                {
                    double? mins = null;
                    try { mins = GetDurationMinutesCached(fi.FullName); } catch { }

                    if (LooksLikeTvEpisodePath(fi.FullName) || BelongsToMovieOrSeriesCategory(fi.FullName, mins))
                        filmLikeFiles.Add(fi);
                    else
                        shortVideos.Add(fi);
                }
            }

            var filmSections = BuildMovieAndSeriesRenderItems(filmLikeFiles);
            IEnumerable<LibraryRenderItem> filmAndSeries = _seriesSectionFirst
                ? filmSections.SeriesItems.Concat(filmSections.MovieItems)
                : filmSections.MovieItems.Concat(filmSections.SeriesItems);

            string selectedBucket = NormalizeCollectionBucketKey(_selectedFavoritesBucketKey);
            if (!string.IsNullOrWhiteSpace(_selectedFavoritesBucketKey))
            {
                var scopedItems = new List<LibraryRenderItem>();
                switch (selectedBucket)
                {
                    case "Film":
                        AppendCollectionHubSection(scopedItems, "Film e Serie TV", selectedBucket, filmAndSeries, "Preferiti");
                        break;
                    case "Foto":
                        AppendCollectionHubSection(scopedItems, "Foto", selectedBucket, photos.Select(LibraryRenderItem.FromFile), "Preferiti");
                        break;
                    case "Musica":
                        AppendCollectionHubSection(scopedItems, "Musica", selectedBucket, music.Select(LibraryRenderItem.FromFile), "Preferiti");
                        break;
                    default:
                        AppendCollectionHubSection(scopedItems, "Video", "Video", shortVideos.Select(LibraryRenderItem.FromFile), "Preferiti");
                        break;
                }
                return scopedItems;
            }

            var items = new List<LibraryRenderItem>
            {
                LibraryRenderItem.FromCollectionBucket(new CollectionBucketInfo
                {
                    BucketKey = "Film",
                    Title = "Film e Serie TV",
                    Subtitle = BuildCountLabel(filmLikeFiles.Count, "preferito", "preferiti"),
                    ActivateAction = () => OpenFavoritesBucket("Film"),
                    CompactTile = true,
                    ArtworkKey = "preferiti-film"
                }),
                LibraryRenderItem.FromCollectionBucket(new CollectionBucketInfo
                {
                    BucketKey = "Video",
                    Title = "Video",
                    Subtitle = BuildCountLabel(shortVideos.Count, "preferito", "preferiti"),
                    ActivateAction = () => OpenFavoritesBucket("Video"),
                    CompactTile = true,
                    ArtworkKey = "preferiti-video"
                }),
                LibraryRenderItem.FromCollectionBucket(new CollectionBucketInfo
                {
                    BucketKey = "Foto",
                    Title = "Foto",
                    Subtitle = BuildCountLabel(photos.Count, "preferito", "preferiti"),
                    ActivateAction = () => OpenFavoritesBucket("Foto"),
                    CompactTile = true,
                    ArtworkKey = "preferiti-foto"
                }),
                LibraryRenderItem.FromCollectionBucket(new CollectionBucketInfo
                {
                    BucketKey = "Musica",
                    Title = "Musica",
                    Subtitle = BuildCountLabel(music.Count, "preferito", "preferiti"),
                    ActivateAction = () => OpenFavoritesBucket("Musica"),
                    CompactTile = true,
                    ArtworkKey = "preferiti-musica"
                })
            };

            bool showOverviewSections =
                !string.IsNullOrWhiteSpace((_search.Text ?? string.Empty).Trim()) ||
                !string.Equals(_selExt, "Tutte", StringComparison.OrdinalIgnoreCase);

            if (!showOverviewSections)
                return items;

            AppendCollectionHubSection(items, "Film e Serie TV", "Film", filmAndSeries, "Preferiti");
            AppendCollectionHubSection(items, "Video", "Video", shortVideos.Select(LibraryRenderItem.FromFile), "Preferiti");
            AppendCollectionHubSection(items, "Foto", "Foto", photos.Select(LibraryRenderItem.FromFile), "Preferiti");
            AppendCollectionHubSection(items, "Musica", "Musica", music.Select(LibraryRenderItem.FromFile), "Preferiti");
            return items;
        }

        private List<LibraryRenderItem> BuildPlaylistRenderItems(IEnumerable<FileInfo> files)
        {
            if (!string.IsNullOrWhiteSpace(_selectedPlaylistKey))
                return BuildPlaylistDetailRenderItems(files);

            if (!string.IsNullOrWhiteSpace(_selectedPlaylistBucketKey))
                return BuildPlaylistBucketRenderItems(_selectedPlaylistBucketKey);

            return BuildPlaylistCategoryHubRenderItems();
        }

        private List<LibraryRenderItem> BuildPlaylistCategoryHubRenderItems()
        {
            var tokens = GetPlaylistSearchTokens();
            var playlists = _playlistBuckets.GetPlaylists();
            var items = new List<LibraryRenderItem>();

            foreach (var bucketKey in new[] { "Film", "Video", "Foto", "Musica" })
            {
                var bucketPlaylists = playlists
                    .Where(p => string.Equals(NormalizeCollectionBucketKey(p.Bucket), bucketKey, StringComparison.OrdinalIgnoreCase))
                    .Where(p => PlaylistMatchesSearch(p, tokens))
                    .ToList();

                string subtitle = bucketPlaylists.Count == 0
                    ? "Nessuna playlist"
                    : BuildCountLabel(bucketPlaylists.Count, "playlist", "playlist");

                string capturedBucket = bucketKey;
                items.Add(LibraryRenderItem.FromCollectionBucket(new CollectionBucketInfo
                {
                    BucketKey = bucketKey,
                    Title = GetCollectionBucketDisplayName(bucketKey),
                    Subtitle = subtitle,
                    ActivateAction = () => OpenPlaylistBucket(capturedBucket),
                    CompactTile = true,
                    ArtworkKey = "playlist-" + bucketKey.ToLowerInvariant()
                }));
            }

            return items;
        }

        private List<LibraryRenderItem> BuildPlaylistBucketRenderItems(string? bucket)
        {
            string bucketKey = NormalizeCollectionBucketKey(bucket);
            var tokens = GetPlaylistSearchTokens();
            var playlists = _playlistBuckets.GetPlaylists(bucketKey)
                .Where(p => PlaylistMatchesSearch(p, tokens))
                .ToList();

            var items = new List<LibraryRenderItem>
            {
                LibraryRenderItem.FromSectionTitle(GetCollectionBucketDisplayName(bucketKey), bucketKey)
            };

            if (playlists.Count == 0)
                return items;

            foreach (var playlist in playlists)
            {
                int itemCount = _playlistBuckets.GetPlaylistItemCount(playlist.Key);
                string countLabel = BuildCountLabel(itemCount, "elemento", "elementi");
                string capturedKey = playlist.Key;
                items.Add(LibraryRenderItem.FromCollectionBucket(new CollectionBucketInfo
                {
                    BucketKey = bucketKey,
                    Title = playlist.Name,
                    Subtitle = countLabel,
                    ActivateAction = () => OpenPlaylistDetail(capturedKey),
                    ContextTag = capturedKey,
                    CompactTile = true,
                    ArtworkKey = "playlist-" + bucketKey.ToLowerInvariant()
                }));
            }

            return items;
        }

        private List<LibraryRenderItem> BuildPlaylistDetailRenderItems(IEnumerable<FileInfo> files)
        {
            string? playlistKey = _selectedPlaylistKey;
            if (string.IsNullOrWhiteSpace(playlistKey) || !_playlistBuckets.PlaylistExists(playlistKey))
            {
                _selectedPlaylistKey = null;
                return BuildPlaylistBucketRenderItems(_selectedPlaylistBucketKey);
            }

            string bucketKey = NormalizeCollectionBucketKey(_playlistBuckets.GetPlaylistBucket(playlistKey));
            string playlistName = _playlistBuckets.GetPlaylistName(playlistKey) ?? "Playlist";
            var visibleFiles = (files ?? Enumerable.Empty<FileInfo>())
                .Where(fi => fi != null)
                .ToList();

            int totalCount = _playlistBuckets.GetPlaylistItemCount(playlistKey);

            var items = new List<LibraryRenderItem>
            {
                LibraryRenderItem.FromSectionTitle(playlistName, bucketKey)
            };

            if (visibleFiles.Count == 0)
                return items;

            foreach (var fi in visibleFiles)
                items.Add(LibraryRenderItem.FromFile(fi));

            return items;
        }

        private static string BuildSeasonGroupDisplayName(string seriesTitle, int? seasonNumber)
        {
            string name = string.IsNullOrWhiteSpace(seriesTitle) ? "Serie TV" : seriesTitle.Trim();
            return seasonNumber.HasValue
                ? $"{name} • Stagione {seasonNumber.Value:00}"
                : name + " • Speciali";
        }


        private static string ExtractSeriesTitleFromBestKnownDisplay(string? bestDisplayTitle, MovieMetadataService.MediaTitleInfo info)
        {
            string fallback = !string.IsNullOrWhiteSpace(info.SeriesTitle)
                ? info.SeriesTitle!.Trim()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(bestDisplayTitle))
                return fallback;

            string normalized = Regex.Replace(bestDisplayTitle ?? string.Empty, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return fallback;

            int bulletIndex = normalized.IndexOf('•');
            if (bulletIndex > 0)
            {
                string head = normalized.Substring(0, bulletIndex).Trim();
                if (!string.IsNullOrWhiteSpace(head))
                    return head;
            }

            var match = Regex.Match(normalized, @"^(?<title>.+?)\s+(?:S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3})\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string head = match.Groups["title"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(head))
                    return head;
            }

            return fallback;
        }

        private static string BuildEpisodeChoiceDisplay(MovieMetadataService.MediaTitleInfo info, FileInfo fi)
            => BuildEpisodeChoiceDisplay(info, fi, null);

        private static string BuildEpisodeChoiceDisplay(MovieMetadataService.MediaTitleInfo info, string fileNameOrDisplayName)
            => BuildEpisodeChoiceDisplay(info, fileNameOrDisplayName, null);

        private static string NormalizeEpisodeDisplayText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return Regex.Replace(value ?? string.Empty, @"\s+", " ")
                .Trim(' ', '-', '.', '_', '•', '–', '—', ':');
        }

        private static bool IsGenericEpisodePlaceholderText(string? value)
        {
            string normalized = NormalizeEpisodeDisplayText(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return true;

            string compact = Regex.Replace(normalized, @"[^a-z0-9]+", string.Empty, RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(compact))
                return true;

            return compact == "file"
                || compact == "video"
                || compact == "episode"
                || compact == "item"
                || compact == "media"
                || compact == "track";
        }

        private static bool IsSeriesOnlyDisplayText(string normalized, MovieMetadataService.MediaTitleInfo info)
        {
            if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(info.SeriesTitle))
                return false;

            string seriesTitle = NormalizeEpisodeDisplayText(info.SeriesTitle);
            if (string.IsNullOrWhiteSpace(seriesTitle))
                return false;

            if (string.Equals(normalized, seriesTitle, StringComparison.OrdinalIgnoreCase))
                return true;

            string codePattern = @"(?:S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3}|E\d{1,3})";
            return Regex.IsMatch(normalized, $@"^{Regex.Escape(seriesTitle)}\s*•\s*{codePattern}\s*$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(normalized, $@"^{Regex.Escape(seriesTitle)}\s+{codePattern}\s*$", RegexOptions.IgnoreCase);
        }

        private static string? TryExtractEpisodeTitleFromText(string? value, MovieMetadataService.MediaTitleInfo info)
        {
            string normalized = NormalizeEpisodeDisplayText(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            string codePattern = @"(?:S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3}|E\d{1,3})";
            string seriesTitle = NormalizeEpisodeDisplayText(info.SeriesTitle);

            if (!string.IsNullOrWhiteSpace(seriesTitle) && IsSeriesOnlyDisplayText(normalized, info))
                return null;

            var patterns = new List<string>();
            if (!string.IsNullOrWhiteSpace(seriesTitle))
            {
                patterns.Add($@"^{Regex.Escape(seriesTitle)}\s*•\s*{codePattern}\s*[-–—:]*\s*(?<title>.+)$");
                patterns.Add($@"^{Regex.Escape(seriesTitle)}\s+{codePattern}\s*[-–—:]*\s*(?<title>.+)$");
                patterns.Add($@"^{Regex.Escape(seriesTitle)}\s*[-–—:]+\s*(?<title>.+)$");
            }

            patterns.Add($@"^(?:{codePattern})\s*[-–—: ]+\s*(?<title>.+)$");
            patterns.Add($@"^.+?\s*•\s*{codePattern}\s*[-–—:]*\s*(?<title>.+)$");
            patterns.Add($@"^.+?\s+{codePattern}\s*[-–—:]*\s*(?<title>.+)$");

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(normalized, pattern, RegexOptions.IgnoreCase);
                if (!match.Success)
                    continue;

                string title = NormalizeEpisodeDisplayText(match.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }

            return null;
        }

        private static string BuildEpisodeChoiceDisplay(MovieMetadataService.MediaTitleInfo info, FileInfo fi, string? resolvedDisplayTitle)
            => BuildEpisodeChoiceDisplay(info, Path.GetFileNameWithoutExtension(fi.Name) ?? fi.Name, resolvedDisplayTitle);

        private static string BuildEpisodeChoiceDisplay(MovieMetadataService.MediaTitleInfo info, string fileNameOrDisplayName, string? resolvedDisplayTitle)
        {
            string fallback = fileNameOrDisplayName ?? string.Empty;
            try
            {
                if (!string.IsNullOrWhiteSpace(fallback) && Path.HasExtension(fallback))
                    fallback = Path.GetFileNameWithoutExtension(fallback) ?? fallback;
            }
            catch { }

            string parsedEpisodeTitle = !string.IsNullOrWhiteSpace(info.EpisodeTitle)
                ? NormalizeEpisodeDisplayText(info.EpisodeTitle)
                : (TryExtractEpisodeTitleFromText(fallback, info) ?? string.Empty);

            string parsedFallback = fallback;
            if (info.EpisodeNumber.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(parsedEpisodeTitle))
                    parsedFallback = $"E{info.EpisodeNumber.Value:00} • {parsedEpisodeTitle}";
                else if (IsGenericEpisodePlaceholderText(fallback))
                    parsedFallback = $"E{info.EpisodeNumber.Value:00}";
                else
                    parsedFallback = $"E{info.EpisodeNumber.Value:00} • {fallback}";
            }
            else if (!string.IsNullOrWhiteSpace(parsedEpisodeTitle))
            {
                parsedFallback = parsedEpisodeTitle;
            }

            if (string.IsNullOrWhiteSpace(resolvedDisplayTitle))
                return parsedFallback;

            string normalized = NormalizeEpisodeDisplayText(resolvedDisplayTitle);
            if (string.IsNullOrWhiteSpace(normalized) || IsGenericEpisodePlaceholderText(normalized))
                return parsedFallback;

            string? episodeTitle = TryExtractEpisodeTitleFromText(normalized, info);
            if (!string.IsNullOrWhiteSpace(episodeTitle))
            {
                if (info.EpisodeNumber.HasValue)
                    return $"E{info.EpisodeNumber.Value:00} • {episodeTitle}";
                return episodeTitle;
            }

            if (IsSeriesOnlyDisplayText(normalized, info))
                return parsedFallback;

            if (info.EpisodeNumber.HasValue && Regex.IsMatch(normalized, @"^E\d{1,3}\s*•", RegexOptions.IgnoreCase))
                return normalized;

            if (info.EpisodeNumber.HasValue)
                return $"E{info.EpisodeNumber.Value:00} • {normalized}";

            return normalized;
        }

        private bool ShouldLoadFilmPosterForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                double? mins = GetDurationMinutesCached(path);
                return BelongsToMovieOrSeriesCategory(path, mins);
            }
            catch
            {
                return false;
            }
        }

        private void QueueLibraryThumbLoadForFileCard(FileCard card, string path, CancellationToken ct)
        {
            if (ShouldLoadFilmPosterForPath(path))
            {
                QueueFilmPosterLoad(
                    mediaPath: path,
                    ct: ct,
                    applyImage: bmp => ApplyBitmapToCard(card, bmp),
                    applyTitle: title =>
                    {
                        if (string.IsNullOrWhiteSpace(title)) return;
                        TryPostToControl(card, () => card.SetDisplayName(title!));
                    },
                    fallbackLoad: () => card.BeginThumbLoad(ct));
                return;
            }

            card.BeginThumbLoad(ct);
        }

        private void QueueLibraryThumbLoadForSeasonCard(SeasonSelectorCard card, TvSeasonGroup group, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(group.RepresentativePath))
                return;

            QueueFilmPosterLoad(
                mediaPath: group.RepresentativePath,
                ct: ct,
                applyImage: bmp => ApplyBitmapToCard(card, bmp),
                applyTitle: title =>
                {
                    if (string.IsNullOrWhiteSpace(title)) return;
                    TryPostToControl(card, () => card.SetDisplayName(title!));
                },
                fallbackLoad: () => QueueLocalThumbLoadForSeasonCard(card, group.RepresentativePath, ct));
        }

        private void QueueLocalThumbLoadForSeasonCard(SeasonSelectorCard card, string path, CancellationToken ct)
        {
            _ = Task.Run(() =>
            {
                Bitmap? bmp = null;
                try
                {
                    if (ct.IsCancellationRequested) return;

                    bmp = TryLoadThumb(path, 520);
                    if (bmp == null)
                    {
                        var cat = CategoryFromExt((Path.GetExtension(path) ?? string.Empty).ToLowerInvariant());
                        bmp = GetCategoryPlaceholder(cat, 520);
                    }

                    if (bmp == null || ct.IsCancellationRequested)
                    {
                        try { bmp?.Dispose(); } catch { }
                        return;
                    }

                    ApplyBitmapToCard(card, bmp);
                }
                catch (OperationCanceledException)
                {
                    try { bmp?.Dispose(); } catch { }
                }
                catch
                {
                    try { bmp?.Dispose(); } catch { }
                }
            }, ct);
        }

        private void QueueFilmPosterLoad(
            string mediaPath,
            CancellationToken ct,
            Action<Bitmap> applyImage,
            Action<string?>? applyTitle,
            Action fallbackLoad)
        {
            _ = Task.Run(async () =>
            {
                string? latestTitle = null;
                bool cachedPosterApplied = false;
                bool cachedTitleResolved = false;

                try
                {
                    latestTitle = MovieMetadataService.GetBestKnownDisplayTitle(mediaPath);
                    if (!string.IsNullOrWhiteSpace(latestTitle))
                        applyTitle?.Invoke(latestTitle);
                }
                catch { }

                try
                {
                    cachedTitleResolved = MovieMetadataService.IsCachedTitleResolved(mediaPath);
                    var cachedPosterPath = MovieMetadataService.GetCachedPosterPath(mediaPath);
                    if (!string.IsNullOrWhiteSpace(cachedPosterPath) && File.Exists(cachedPosterPath))
                    {
                        var cachedBmp = LoadBitmapClone(cachedPosterPath);
                        if (cachedBmp != null)
                        {
                            applyImage(cachedBmp);
                            cachedPosterApplied = true;
                        }
                    }

                    if (cachedPosterApplied)
                    {
                        if (!string.IsNullOrWhiteSpace(latestTitle))
                            applyTitle?.Invoke(latestTitle);

                        if (!cachedTitleResolved)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                                    if (ct.IsCancellationRequested)
                                        return;

                                    double? durationSeconds = null;
                                    var mins = GetDurationMinutesCached(mediaPath);
                                    if (mins.HasValue)
                                        durationSeconds = mins.Value * 60.0;

                                    var refreshed = MovieMetadataService.ResolveTitleAndPoster(mediaPath, durationSeconds, ct);
                                    if (!string.IsNullOrWhiteSpace(refreshed.normalizedTitle))
                                        applyTitle?.Invoke(refreshed.normalizedTitle);

                                    if (!string.IsNullOrWhiteSpace(refreshed.localPosterPath) && File.Exists(refreshed.localPosterPath))
                                    {
                                        var retryBmp = LoadBitmapClone(refreshed.localPosterPath);
                                        if (retryBmp != null)
                                            applyImage(retryBmp);
                                    }
                                }
                                catch (OperationCanceledException) { }
                                catch { }
                            }, ct);
                        }

                        return;
                    }
                }
                catch { }

                int maxAttempts = 3;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        if (ct.IsCancellationRequested)
                            return;

                        double? durationSeconds = null;
                        var mins = GetDurationMinutesCached(mediaPath);
                        if (mins.HasValue)
                            durationSeconds = mins.Value * 60.0;

                        var resolved = MovieMetadataService.ResolveTitleAndPoster(mediaPath, durationSeconds, ct);
                        if (!string.IsNullOrWhiteSpace(resolved.normalizedTitle))
                            latestTitle = resolved.normalizedTitle;

                        if (!string.IsNullOrWhiteSpace(resolved.localPosterPath) && File.Exists(resolved.localPosterPath))
                        {
                            var bmp = LoadBitmapClone(resolved.localPosterPath);
                            if (bmp != null)
                            {
                                applyTitle?.Invoke(latestTitle);
                                applyImage(bmp);
                                return;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        // best-effort: il retry gestisce i picchi/429 di TMDb.
                    }

                    if (attempt < maxAttempts)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(750 * attempt), ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(latestTitle))
                    applyTitle?.Invoke(latestTitle);

                if (ct.IsCancellationRequested)
                    return;

                if (!cachedPosterApplied)
                    fallbackLoad();

                if (cachedPosterApplied)
                    return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested)
                            return;

                        double? durationSeconds = null;
                        var mins = GetDurationMinutesCached(mediaPath);
                        if (mins.HasValue)
                            durationSeconds = mins.Value * 60.0;

                        var retryResolved = MovieMetadataService.ResolveTitleAndPoster(mediaPath, durationSeconds, ct);
                        if (!string.IsNullOrWhiteSpace(retryResolved.normalizedTitle))
                            applyTitle?.Invoke(retryResolved.normalizedTitle);

                        if (!string.IsNullOrWhiteSpace(retryResolved.localPosterPath) && File.Exists(retryResolved.localPosterPath))
                        {
                            var retryBmp = LoadBitmapClone(retryResolved.localPosterPath);
                            if (retryBmp != null)
                                applyImage(retryBmp);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch
                    {
                    }
                }, ct);
            }, ct);
        }

        private static Bitmap? LoadBitmapClone(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var img = Image.FromStream(fs);
                return new Bitmap(img);
            }
            catch
            {
                return null;
            }
        }

        private static void TryPostToControl(Control control, Action action)
        {
            try
            {
                if (control == null || control.IsDisposed)
                    return;

                if (control.InvokeRequired)
                    control.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (control.IsDisposed) return;
                            action();
                        }
                        catch { }
                    }));
                else
                    action();
            }
            catch { }
        }

        private static void ApplyBitmapToCard(FileCard card, Bitmap bmp)
        {
            TryPostToControl(card, () =>
            {
                if (card.IsDisposed)
                {
                    try { bmp.Dispose(); } catch { }
                    return;
                }
                card.SetImage(bmp);
            });
        }

        private static void ApplyBitmapToCard(SeasonSelectorCard card, Bitmap bmp)
        {
            TryPostToControl(card, () =>
            {
                if (card.IsDisposed)
                {
                    try { bmp.Dispose(); } catch { }
                    return;
                }
                card.SetImage(bmp);
            });
        }

        private List<FileInfo> BuildFilteredList()
        {
            List<FileInfo> src;
            lock (_cacheLock) src = _cache.ToList();

            // preferiti = usa lista preferiti come sorgente
            if (string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase))
            {
                var favs = _favs.All()
                    .Where(File.Exists)
                    .Select(p =>
                    {
                        try { return new FileInfo(p); }
                        catch { return null; }
                    })
                    .Where(fi => fi != null)
                    .Cast<FileInfo>()
                    .ToList();
                src = favs;
            }

            // filtro testo (nome + percorso)
            string q = (_search.Text ?? "").Trim().ToLowerInvariant();
            if (q.Length > 0)
            {
                var tokens = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                src = src.Where(fi =>
                {
                    string name = fi.Name.ToLowerInvariant();
                    string path = (fi.DirectoryName ?? "").ToLowerInvariant();
                    foreach (var t in tokens)
                        if (!(name.Contains(t) || path.Contains(t)))
                            return false;
                    return true;
                }).ToList();
            }

            string catLower = _selCat.ToLowerInvariant();
            bool isFilm = catLower == "film";
            bool isVideo = catLower == "video";

            if (isFilm || isVideo)
            {
                src = src.Where(fi =>
                    isFilm
                        ? BelongsToMovieOrSeriesCategory(fi)
                        : BelongsToShortVideoCategory(fi))
                    .ToList();
            }
            else
            {
                // altre categorie: filtro solo per estensione
                var allowed = new HashSet<string>(ExtsForCategory(_selCat), StringComparer.OrdinalIgnoreCase);
                src = src.Where(fi => allowed.Contains(Path.GetExtension(fi.FullName))).ToList();
            }

            // filtro chip estensione singola
            if (!string.Equals(_selExt, "Tutte", StringComparison.OrdinalIgnoreCase))
            {
                src = src.Where(fi =>
                        string.Equals(
                            Path.GetExtension(fi.FullName),
                            _selExt,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // sort
            src = _sortIndex switch
            {
                1 => src.OrderBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                2 => src.OrderByDescending(fi => fi.Length).ToList(),
                _ => src.OrderByDescending(fi => fi.LastWriteTimeUtc).ToList()
            };

            return src.Take(250).ToList();
        }

        private (int CardWidth, int CardHeight, int ImgHeight, int OuterWidth, int RowHeight) GetGridCardLayout()
        {
            const int cardWidth = 300;
            const int cardHeight = 236;
            const int imgHeight = 170;
            const int outerWidth = 320;
            const int rowHeight = 248;
            return (cardWidth, cardHeight, imgHeight, outerWidth, rowHeight);
        }

        private static bool ShouldRenderImmediately(IReadOnlyList<LibraryRenderItem> list)
        {
            if (list == null || list.Count == 0)
                return true;

            if (list.Count <= 14)
                return true;

            int contentItems = 0;
            bool hasOnlyCollectionRows = true;
            foreach (var item in list)
            {
                if (item == null)
                    continue;

                if (item.File != null || item.IsSeasonGroup)
                {
                    contentItems++;
                    hasOnlyCollectionRows = false;
                }
                else if (!item.IsCollectionBucket && !item.IsSectionHeader)
                {
                    hasOnlyCollectionRows = false;
                }
            }

            if (hasOnlyCollectionRows)
                return true;

            return contentItems > 0 && contentItems <= 10 && list.Count <= 18;
        }

        private void StartProgressiveRender(List<LibraryRenderItem> list, bool hideMaskOnDone)
        {
            if (_grid.FlowDirection != FlowDirection.LeftToRight || !_grid.WrapContents)
            {
                _grid.FlowDirection = FlowDirection.LeftToRight;
                _grid.WrapContents = true;
            }

            StopAnimatedLibraryVisuals();
            _progressiveList = list ?? new List<LibraryRenderItem>();
            _progressivePos = 0;
            _progressiveThumbToken = GetOrNewThumbCts().Token;
            _hideMaskWhenProgressiveDone = hideMaskOnDone;

            _grid.SuspendLayout();
            _grid.Controls.Clear();
            _grid.ResumeLayout();
            _grid.Visible = false;

            bool hasRenderableContent = _progressiveList.Any(item => item != null && !item.IsSectionHeader);
            if (_progressiveList.Count == 0 || !hasRenderableContent)
            {
                string emptyMessage = "Nessun elemento corrisponde ai filtri.";
                if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase))
                {
                    emptyMessage = !string.IsNullOrWhiteSpace(_selectedPlaylistKey)
                        ? "Questa playlist è vuota."
                        : (!string.IsNullOrWhiteSpace(_selectedPlaylistBucketKey)
                            ? "Nessuna playlist disponibile in questa categoria."
                            : "Nessuna playlist disponibile.");
                }
                else if (string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase))
                {
                    emptyMessage = !string.IsNullOrWhiteSpace(_selectedFavoritesBucketKey)
                        ? "Nessun preferito disponibile in questa categoria."
                        : "Non hai ancora aggiunto preferiti.";
                }

                _grid.Controls.Add(new InfoRow(emptyMessage));
                _grid.Visible = true;
                _grid.UpdateThemedScrollbar();
                try { LayoutHeader(); } catch { }
                if (_hideMaskWhenProgressiveDone)
                    HideMask();
                return;
            }

            _grid.Visible = true;

            if (ShouldRenderImmediately(_progressiveList))
            {
                RenderProgressiveBatch(_progressiveList.Count);
                PhotoPagingAfterProgressiveTick();
                _grid.UpdateThemedScrollbar();
                try { LayoutHeader(); } catch { }
                if (_hideMaskWhenProgressiveDone)
                    HideMask();
                return;
            }

            int initialBatch = EstimateInitialGridBatch();
            RenderProgressiveBatch(initialBatch);
            PhotoPagingAfterProgressiveTick();
            try { LayoutHeader(); } catch { }

            if (_progressivePos < _progressiveList.Count)
            {
                _progressiveTimer.Start();
            }
            else if (_hideMaskWhenProgressiveDone)
            {
                HideMask();
            }
        }

        private void ProgressiveTick()
        {
            if (_progressivePos >= _progressiveList.Count)
            {
                _progressiveTimer.Stop();
                _grid.UpdateThemedScrollbar();
                try { LayoutHeader(); } catch { }
                if (_hideMaskWhenProgressiveDone)
                    HideMask();
                return;
            }

            int cardsPerRow = EstimateCardsPerRow();
            if (cardsPerRow < 1) cardsPerRow = 1;

            RenderProgressiveBatch(Math.Max(cardsPerRow * 2, 1));
            try { LayoutHeader(); } catch { }

            if (_progressivePos >= _progressiveList.Count)
            {
                _progressiveTimer.Stop();
                if (_hideMaskWhenProgressiveDone)
                    HideMask();
            }
        }

        private void RenderProgressiveBatch(int count)
        {
            if (count < 1) count = 1;

            var layout = GetGridCardLayout();
            int cardWidth = layout.CardWidth;
            int cardHeight = layout.CardHeight;
            int imgHeight = layout.ImgHeight;

            _grid.SuspendLayout();
            try
            {
                for (int i = 0; i < count && _progressivePos < _progressiveList.Count; i++)
                {
                    var item = _progressiveList[_progressivePos++];
                    if (item == null)
                        continue;

                    if (item.IsSectionHeader && !string.IsNullOrWhiteSpace(item.SectionTitle))
                    {
                        int dividerWidth = Math.Max(cardWidth, _grid.ClientSize.Width - _grid.Padding.Left - _grid.Padding.Right - 20);
                        var divider = new LibrarySectionDivider(item.SectionTitle!, item.SectionBucket)
                        {
                            Width = dividerWidth,
                            LeftMargin = 12
                        };
                        _grid.Controls.Add(divider);
                        _grid.SetFlowBreak(divider, true);
                        continue;
                    }

                    if (item.IsCollectionBucket && item.CollectionBucket != null)
                    {
                        Action openAction = item.CollectionBucket.ActivateAction
                            ?? (() => NavigateToCollectionBucket(item.CollectionBucket.BucketKey));

                        if (item.CollectionBucket.CompactTile)
                        {
                            int availableWidth = Math.Max(220, _grid.ClientSize.Width - _grid.Padding.Left - _grid.Padding.Right - 12);
                            const int compactGap = 24;
                            int compactColumns = Math.Max(1, Math.Min(5, (availableWidth + compactGap) / (228 + compactGap)));
                            int compactWidth = Math.Max(178, Math.Min(232, (availableWidth - ((compactColumns - 1) * compactGap)) / compactColumns));

                            var tileCard = new CollectionHubTileCard(
                                item.CollectionBucket.BucketKey,
                                item.CollectionBucket.Title,
                                item.CollectionBucket.Subtitle,
                                openAction,
                                item.CollectionBucket.ArtworkKey)
                            {
                                Width = compactWidth
                            };

                            if (item.CollectionBucket.ContextTag != null)
                                tileCard.SetItemContextMenu(EnsurePlaylistHubMenu(), item.CollectionBucket.ContextTag);

                            _grid.Controls.Add(tileCard);
                            _grid.SetFlowBreak(tileCard, false);
                            continue;
                        }

                        int bucketWidth = Math.Max(
                            cardWidth,
                            Math.Min(
                                Math.Max(520, (cardWidth * 2) + 20),
                                Math.Max(cardWidth, _grid.ClientSize.Width - _grid.Padding.Left - _grid.Padding.Right - 20)));

                        var bucketCard = new CollectionBucketCard(
                            item.CollectionBucket.Title,
                            item.CollectionBucket.Subtitle,
                            openAction)
                        {
                            Width = bucketWidth,
                            BucketKey = item.CollectionBucket.BucketKey
                        };
                        bucketCard.SetQuickActions(
                            item.CollectionBucket.PrimaryActionLabel,
                            item.CollectionBucket.PrimaryAction,
                            item.CollectionBucket.SecondaryActionLabel,
                            item.CollectionBucket.SecondaryAction);

                        if (item.CollectionBucket.ContextTag != null)
                            bucketCard.SetItemContextMenu(EnsurePlaylistHubMenu(), item.CollectionBucket.ContextTag);

                        _grid.Controls.Add(bucketCard);
                        _grid.SetFlowBreak(bucketCard, true);
                        continue;
                    }

                    if (item.IsSeasonGroup && item.SeasonGroup != null)
                    {
                        var group = item.SeasonGroup;
                        var seasonCard = new SeasonSelectorCard(
                            group,
                            showEpisodePicker: (seasonGroup, displayTitle) => ShowSeasonEpisodeOverlay(seasonGroup, displayTitle),
                            cardWidth: cardWidth,
                            cardHeight: cardHeight,
                            imgHeight: imgHeight);

                        var cat = CategoryFromExt((Path.GetExtension(group.RepresentativePath) ?? string.Empty).ToLowerInvariant());
                        seasonCard.SetInitialPlaceholder(GetCategoryPlaceholder(cat, 520));
                        seasonCard.SetItemContextMenu(EnsureLibraryItemMenu(), LibraryItemContext.FromSeasonGroup(group));
                        _grid.Controls.Add(seasonCard);
                        QueueLibraryThumbLoadForSeasonCard(seasonCard, group, _progressiveThumbToken);
                        continue;
                    }

                    if (item.File == null)
                        continue;

                    var fi = item.File;
                    var path = fi.FullName;

                    var card = new FileCard(
                        path,
                        showFavorite: true,
                        favInit: _favs.IsFav(path),
                        onFavToggle: (p, fav) =>
                        {
                            _favs.Set(p, fav);
                            if (string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase) && !fav)
                                ApplyFilterAndRender();
                        },
                        clickOpen: () => SafeOpen(path),
                        cardWidth: cardWidth,
                        cardHeight: cardHeight,
                        imgHeight: imgHeight
                    );

                    if (string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var displayTitle = MovieMetadataService.GetBestKnownDisplayTitle(path);
                            if (!string.IsNullOrWhiteSpace(displayTitle))
                                card.SetDisplayName(displayTitle);
                        }
                        catch { }
                    }

                    var fileCat = CategoryFromExt((Path.GetExtension(path) ?? string.Empty).ToLowerInvariant());
                    card.SetInitialPlaceholder(GetCategoryPlaceholder(fileCat, 520));
                    card.SetItemContextMenu(EnsureLibraryItemMenu(), LibraryItemContext.FromFile(path));

                    _grid.Controls.Add(card);
                    QueueLibraryThumbLoadForFileCard(card, path, _progressiveThumbToken);
                }

                _grid.UpdateThemedScrollbar();
            }
            finally
            {
                _grid.ResumeLayout(true);
            }

            try { UpdateStickySectionDivider(); } catch { }
            try { TryFulfillPendingContentFocusAfterRender(); } catch { }
        }

        // quante card (300 + margini ~20 => ~320px) entrano in una riga disponibile
        private int EstimateCardsPerRow()
        {
            int w = _grid.ClientSize.Width;
            if (w <= 0) w = _grid.Width;
            if (w <= 0) return 2;

            int usable = w - _grid.Padding.Left - _grid.Padding.Right;
            if (usable <= 0) usable = w;

            int per = usable / Math.Max(1, GetGridCardLayout().OuterWidth);
            if (per < 1) per = 1;

            return per;
        }

        private int EstimateInitialGridBatch()
        {
            int perRow = EstimateCardsPerRow();
            if (perRow < 1) perRow = 1;

            int h = _grid.ClientSize.Height;
            if (h <= 0) h = _grid.Height;
            if (h <= 0) return Math.Max(1, perRow * 3);

            int rows = Math.Max(3, Math.Min(6, (int)Math.Ceiling(h / Math.Max(1.0, GetGridCardLayout().RowHeight)) + 1));
            return Math.Max(1, perRow * rows);
        }

        // Usa la larghezza del carosello (non della griglia) per stimare quante card stanno in riga
        private int EstimateCardsPerRowForCarousel()
        {
            int hostW = _carouselHost.ClientSize.Width;
            if (hostW <= 0)
                hostW = _carouselHost.Width;
            if (hostW <= 0 && _right != null)
                hostW = _right.ClientSize.Width - (_gridRightPad * 2);

            if (hostW <= 0)
                return 1;

            int itemOuter = _carouselViewport.GetItemOuterWidthEstimate();
            if (itemOuter <= 0)
                itemOuter = 320; // 300 card + 10+10 margini

            // piccolo margine per non appiccicare le card ai bordi
            int usable = Math.Max(0, hostW - 16);

            int cards = usable / itemOuter;
            if (cards < 1) cards = 1;
            if (cards > 6) cards = 6;

            return cards;
        }


    }
}
