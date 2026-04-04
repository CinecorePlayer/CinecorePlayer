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
    internal sealed partial class MediaLibraryPage : UserControl
    {
        // events esterni
        public event Action<string>? OpenRequested;
        public event Action<string, double?>? OpenWithResumeRequested;
        public event Action? CloseRequested;
        public event Action<List<string>, int, bool>? QueuePlayRequested;
        public event Action<List<string>>? QueueAppendRequested;
        public event Action<List<string>>? QueueRemoveRequested;
        public event Action? QueueClearRequested;
        public event Action<string>? QueuePlayPathRequested;
        public event Action<string, int>? QueueMoveRequested;
        public event Action? QueueEditorRequested;
        internal Func<string, bool>? QueueContainsPathResolver;
        internal Func<IReadOnlyList<PlaybackQueueViewItem>>? QueueSnapshotResolver;

        internal sealed class PlaybackQueueViewItem
        {
            public string Path { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;
            public int Index { get; set; }
            public bool IsCurrent { get; set; }
        }

        // URL pane
        private UrlPane? _urlPane;
        // YouTube pane
        private YouTubePane? _ytPane;
        // DLNA state
        private DlnaDevice? _dlnaSel;
        private readonly Stack<string> _dlnaStack = new(); // breadcrumb containerId
        private CancellationTokenSource? _dlnaCts;

        // HttpClient condiviso (keep-alive)
        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        // layout shared state
        private int _contentSidePad = 104;      // padding sinistro dinamico allineato al carosello
        private readonly int _gridRightPad = 24; // padding destro fisso per non stringere le card

        // pannelli principali
        private readonly Panel _left = new()
        {
            Dock = DockStyle.Left,
            Width = 260,
            BackColor = Theme.Nav
        };

        private readonly Panel _leftFooter = new()
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            BackColor = Theme.Nav,
            Padding = new Padding(12, 10, 12, 12)
        };

        private readonly Panel _leftBody = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Nav
        };

        private readonly RightHostPanel _right = new();

        // header in alto a destra
        private readonly HeaderBar _header = new()
        {
            Dock = DockStyle.Top,
            Height = 70,
            Padding = new Padding(16, 8, 16, 8)
        };

        // section headers
        private readonly SectionHeader _secRecenti = new("Riprendi");
        private readonly SectionHeader _secAll = new("Tutti i file");

        // carosello Recenti
        private readonly Panel _carouselHost = new()
        {
            Dock = DockStyle.Top,
            Height = 260,
            BackColor = Color.Black,
            Padding = new Padding(0, 8, 0, 4),
            Visible = true
        };

        private readonly CarouselViewport _carouselViewport = new()
        {
            BackColor = Color.Black
        };

        // carosello frecce
        private IconButton _carPrev = null!;
        private IconButton _carNext = null!;

        // messaggio quando non c'è nulla da riprendere
        private Label _resumeEmptyLabel = null!;

        // griglia contenuti
        private readonly SkinnedFlow _grid = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.Black,
            WrapContents = true,
            Padding = new Padding(0, 8, 0, 8), // verrà rimesso con ApplyContentSidePad
            Margin = new Padding(0),
            UseThemedVScroll = false
        };

        // maschera visiva per coprire in modo definitivo l'eventuale scrollbar nativa a destra
        private readonly Panel _gridScrollbarMask = new()
        {
            BackColor = Color.Black,
            Visible = false,
            Enabled = false,
            TabStop = false
        };

        // separatore sticky Film / Serie TV dentro la griglia
        private LibrarySectionDivider? _stickySectionDivider;
        private string _stickySectionDividerTitle = string.Empty;
        private string? _stickySectionDividerBucket;

        // auto-aggiornamento libreria locale (debounce + watcher per root configurate)
        private readonly List<FileSystemWatcher> _libraryWatchers = new();
        private readonly System.Windows.Forms.Timer _libraryWatcherDebounce = new() { Interval = 1400 };
        private bool _libraryWatcherRefreshQueued = false;

        // header widgets
        private SearchBox _search = null!;
        private Chip _chipExt = null!;
        private Chip _chipSort = null!;
        private HeaderActionButton _btnBrowse = null!;
        private HeaderActionButton _btnYtTrending = null!;
        private HeaderActionButton _btnYtPersonal = null!;
        private HeaderActionButton _btnYtLogin = null!;
        private HeaderActionButton _btnYtLogout = null!;
        private HeaderActionButton _btnAddFolder = null!;
        private HeaderActionButton _btnManageFolders = null!;
        private HeaderActionButton _btnRefresh = null!;
        private HeaderActionButton _btnCollectionBack = null!;
        private HeaderActionButton _btnCreatePlaylist = null!;
        private HeaderActionButton _btnPlayCollection = null!;
        private HeaderActionButton _btnShuffleCollection = null!;

        // loading mask overlay
        private readonly LoadingMask _mask = new() { Dock = DockStyle.Fill, Visible = false };

        // overlay gestione cartelle (sopra al pannello destro)
        private Panel _rootsOverlay = null!;
        private FlowLayoutPanel _rootsOverlayList = null!;
        private string _rootsOverlayDraftCategory = string.Empty;
        private List<string> _rootsOverlayWorkingRoots = new();
        private bool _rootsOverlayHasPendingChanges = false;

        // nav model
        private readonly string[] _catOrder = { "Film", "Video", "Foto", "Musica", "Playlist", "Preferiti" };
        private readonly string[] _srcOrder = { "Il mio computer", "Rete domestica", "YouTube", "URL" };
        private readonly List<NavButton> _catButtons = new();
        private readonly List<NavButton> _srcButtons = new();

        // left footer (Chiudi)
        private FlatButton? _btnClose;

        // =========================
        // DPAD / Remote navigation state (for TV/remote-style control)
        // =========================
        private enum RemoteZone { LeftNav, Content }
        private RemoteZone _remoteZone = RemoteZone.LeftNav;
        private Control? _remoteLastMenuFocus;


        // Ultima interazione DPAD: true se arriva dal Web Remote, false se arriva
        // da tastiera locale. Serve per differenziare comportamenti (es. OSK solo da remote).
        private bool _dpadInputIsRemote = false;

        internal void SetDpadInputIsRemote(bool isRemote)
        {
            _dpadInputIsRemote = isRemote;
        }

        // NOTE:
        // La nostra OSK (tastiera a schermo) deve aprirsi SOLO quando l'utente preme OK sul campo
        // di ricerca usando il Web Remote. Con DPAD da tastiera locale/mouse non deve comparire.
        // Per questo NON usiamo un trigger su GotFocus (che può restare "appeso"), ma apriamo
        // l'OSK direttamente in TryRemoteOk.

        // Compat: alcune versioni di Header.cs aprivano l'OSK su GotFocus ma solo se "armata" dal remote.
        // Tenere questo meccanismo evita regressioni e permette di distinguere in modo netto
        // ingresso da Web Remote vs tastiera/mouse.
        private bool _remoteOskArmedForSearch = false;

        private void ArmRemoteOnScreenKeyboardForSearch()
        {
            _remoteOskArmedForSearch = true;
        }

        private bool ConsumeRemoteOnScreenKeyboardForSearch()
        {
            if (!_remoteOskArmedForSearch) return false;
            _remoteOskArmedForSearch = false;
            return true;
        }

        // Quando entri nei contenuti via OK ma la griglia non ha ancora card (render progressivo),
        // mettiamo un "autofocus" sulla prima card non appena viene aggiunta.
        private bool _pendingFocusFirstGridItem = false;

        // Versione incrementale per invalidare richieste di autofocus che arrivano da render vecchi.
        private int _remoteFocusRenderVersion = 0;

        // Prewarm iniziale dei contenuti: deve partire una sola volta, anche se la pagina viene
        // creata in anticipo mentre e' ancora nascosta.
        private bool _initialContentPrepared = false;

        // FOTO/VIDEO/MUSICA: paging (banner "Mostra altre...")
        private const int PhotoPageSize = 100;
        private const int VideoPageSize = 100;
        private const int MusicPageSize = 200;
        private int _photoMaxVisible = int.MaxValue;
        private LoadMoreBanner? _photoLoadMoreBanner;
        // stato nav selezionato
        private string _selCat = "Film";
        private string _selSrc = "Il mio computer";

        internal string SelectedCategory => _selCat;
        internal string SelectedSource => _selSrc;

        // filtro / sort
        private string _selExt = "Tutte";
        private int _sortIndex = 0; // 0: Recenti, 1: Nome A–Z, 2: Dimensione
        private ContextMenuStrip? _menuSort;
        private ContextMenuStrip? _menuExt;
        private ContextMenuStrip? _itemMenu;
        private ContextMenuStrip? _playlistHubMenu;
        private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 220 };

        // scan / thumb infra
        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _thumbCts;
        private CancellationTokenSource? _filterCts;
        private List<FileInfo> _cache = new();
        private readonly object _cacheLock = new();
        // cache durata media (in minuti) letta dalle proprietà shell di Windows
        private readonly Dictionary<string, double?> _durationCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _durationLock = new();
        // cache persistente delle durate (su disco, JSON)
        private readonly DurationIndexStore _durationIndex = new();

        // progressive render griglia
        private readonly System.Windows.Forms.Timer _progressiveTimer = new() { Interval = 22 };
        private List<LibraryRenderItem> _progressiveList = new();
        private int _progressivePos = 0;
        private CancellationToken _progressiveThumbToken;
        private bool _hideMaskWhenProgressiveDone = false;
        // debounce per ricaricare il carosello quando arrivano nuove copertine
        private readonly System.Windows.Forms.Timer _carouselPosterRefresh = new() { Interval = 500 };

        // NEW: reset totale del render progressivo
        private void ResetProgressiveRender()
        {
            _progressiveTimer.Stop();
            _progressiveList = new List<LibraryRenderItem>();
            _progressivePos = 0;
            _hideMaskWhenProgressiveDone = false;

            unchecked { _remoteFocusRenderVersion++; }
            _pendingFocusFirstGridItem = false;

            HideStickySectionDivider();
            ResetPhotoPagingState();
        }

        // persistenza recenti / preferiti / radici / indice libreria
        private readonly RecentsStore _recents = new();
        private readonly FavoritesStore _favs = new();
        private readonly PlaylistBucketsStore _playlistBuckets = new();
        private string? _selectedPlaylistKey;
        private string? _selectedPlaylistBucketKey;
        private string? _selectedFavoritesBucketKey;
        private readonly RootsStore _roots = new();
        private readonly LibraryIndexStore _libraryIndex = new();
        private readonly MusicRecentsStore _musicRecents = new();

        public MediaLibraryPage()
        {
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            BackColor = Color.Black;

            Controls.Add(_right);
            Controls.Add(_left);

            // left column
            _left.Controls.Add(_leftBody);
            _left.Controls.Add(_leftFooter);
            BuildLeftBody();
            BuildLeftFooter();

            // right column
            BuildHeader();

            _carouselHost.Controls.Add(_carouselViewport);

            _right.Controls.Add(_grid);          // Fill
            _right.Controls.Add(_secAll);        // Top
            _right.Controls.Add(_carouselHost);  // Top
            _right.Controls.Add(_secRecenti);    // Top
            _right.Controls.Add(_header);        // Top
            _right.Controls.Add(_gridScrollbarMask); // copertura scrollbar nativa
            _right.Controls.Add(_mask);          // overlay caricamento

            // overlay gestione cartelle
            BuildRootsOverlay();
            _right.Controls.Add(_rootsOverlay);
            _rootsOverlay.BringToFront();

            // overlay tastiera a schermo (solo DPAD)
            BuildAppOskOverlay();
            if (_appOskOverlay != null)
            {
                _right.Controls.Add(_appOskOverlay);
                _appOskOverlay.BringToFront();
            }

            _carouselHost.VisibleChanged += (_, __) => AlignCarouselViewport();
            BuildCarouselChrome();

            // padding iniziale
            ApplyContentSidePad();
            LayoutGridScrollbarMask();

            _libraryWatcherDebounce.Tick += (_, __) =>
            {
                try { _libraryWatcherDebounce.Stop(); } catch { }

                if (!_libraryWatcherRefreshQueued)
                    return;

                _libraryWatcherRefreshQueued = false;

                if (IsDisposed || !IsHandleCreated)
                    return;

                if (!string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                    return;

                if (!IsLocalLibraryCategory(_selCat))
                    return;

                try
                {
                    if (_mask != null && _mask.Visible)
                    {
                        _libraryWatcherRefreshQueued = true;
                        _libraryWatcherDebounce.Start();
                        return;
                    }
                }
                catch { }

                try { RefreshContent(); } catch { }
            };

            Disposed += (_, __) =>
            {
                try { _libraryWatcherDebounce.Stop(); } catch { }
                try { DisposeLibraryWatchers(); } catch { }
            };

            void InitializeLibraryShell()
            {
                try { RefreshNavPaint(); } catch { }
                try { BuildHeaderFilters(); } catch { }
                try { LayoutHeader(); } catch { }
                try { AlignCarouselViewport(); } catch { }
                try { ReconfigureLibraryWatchers(); } catch { }

                // sposta il focus iniziale sul primo pulsante di catalogo (niente caret nel search)
                var firstCat = _catButtons.FirstOrDefault();
                if (firstCat != null)
                    BeginInvoke(new Action(() => firstCat.Focus()));
            }

            // shell iniziale: menu subito disponibile, contenuti caricati solo on-demand.
            if (IsHandleCreated)
            {
                InitializeLibraryShell();
            }
            else HandleCreated += (_, __) =>
            {
                if (IsDisposed) return;
                InitializeLibraryShell();
            };

            // debounce search
            _searchDebounce.Tick += (_, __) =>
            {
                _searchDebounce.Stop();

                try
                {
                    if (string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase))
                    {
                        _ytPane?.HostSetQuery(_search?.Inner?.Text);
                    }
                    else if (string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_dlnaSel != null && !_dlnaShowServerPicker)
                            RenderDlnaIndexedCategoryUi(_selCat);
                    }
                    else if (!string.Equals(_selSrc, "URL", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyFilterAndRender();
                    }
                }
                catch { }

                // Se la tastiera a schermo e' aperta mentre cambiano i risultati,
                // alcuni aggiornamenti UI possono alterare lo z-order e "nasconderla".
                // Forziamo il BringToFront per evitare che sembri sparita pur essendo ancora aperta.
                if (IsAppOskVisible)
                {
                    try { _appOskOverlay?.BringToFront(); } catch { }
                }
            };

            _header.Resize += (_, __) =>
            {
                LayoutHeader();
                LayoutGridScrollbarMask();
            };

            // scrollbar custom + sync carosello
            _grid.ScrollStateChanged += (_, __) =>
            {
                _grid.UpdateThemedScrollbar();
                UpdateStickySectionDivider();
            };

            // Quando entri nei contenuti con il telecomando, il render può essere ancora in corso.
            // Se in quel momento il focus rimbalza sull'header (perché la griglia è vuota),
            // appena arriva la PRIMA FileCard la spostiamo automaticamente.
            _grid.ControlAdded += (_, e) =>
            {
                try
                {
                    if (!_pendingFocusFirstGridItem) return;
                    if (e == null || e.Control == null || e.Control.IsDisposed) return;
                    if (!IsGridPrimaryCardControl(e.Control)) return;

                    BeginInvoke(new Action(() =>
                    {
                        try { TryFulfillPendingContentFocusAfterRender(); } catch { }
                    }));
                }
                catch { }
            };

            _grid.SizeChanged += (_, __) =>
            {
                _grid.UpdateThemedScrollbar();
                AlignCarouselViewport();
                LayoutPhotoLoadMoreBanner();
                LayoutGridScrollbarMask();
                UpdateStickySectionDivider();
            };
            _grid.Layout += (_, __) =>
            {
                _grid.UpdateThemedScrollbar();
                AlignCarouselViewport();
                LayoutPhotoLoadMoreBanner();
                LayoutGridScrollbarMask();
                UpdateStickySectionDivider();
            };
            _grid.Resize += (_, __) =>
            {
                _grid.UpdateThemedScrollbar();
                AlignCarouselViewport();
                LayoutPhotoLoadMoreBanner();
                LayoutGridScrollbarMask();
                UpdateStickySectionDivider();
            };
            _grid.VisibleChanged += (_, __) =>
            {
                LayoutGridScrollbarMask();
                UpdateStickySectionDivider();
            };
            _right.SizeChanged += (_, __) =>
            {
                LayoutGridScrollbarMask();
                UpdateStickySectionDivider();
            };

            _carouselHost.Resize += (_, __) => AlignCarouselViewport();
            _carouselViewport.Resize += (_, __) => LayoutCarouselArrows();

            _progressiveTimer.Tick += (_, __) => ProgressiveTick();
            _progressiveTimer.Tick += (_, __) => PhotoPagingAfterProgressiveTick();

            // debounce per aggiornare il carosello quando il servizio poster salva nuove copertine
            _carouselPosterRefresh.Tick += (_, __) =>
            {
                _carouselPosterRefresh.Stop();

                if (IsDisposed || !IsHandleCreated)
                    return;

                // aggiorna solo se siamo in Film + "Il mio computer" e il carosello è visibile
                if (!string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!_carouselHost.Visible)
                    return;

                ForceCarouselRefresh();
            };

            // ascolta le notifiche del servizio metadata film
            MovieMetadataService.PostersChanged += OnPostersChanged;

            ShowMask(string.Empty, showSpinner: false);

            Load += (_, __) =>
            {
                if (IsDisposed) return;

                // Se la pagina viene solo pre-scaldata da PlayerForm mentre è nascosta,
                // non carichiamo ancora i contenuti: ci basta avere lo shell già creato.
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || !Visible) return;
                    try { EnsureInitialContentPrepared(); } catch { }
                }));
            };
        }


        private void DisposeLibraryWatchers()
        {
            foreach (var watcher in _libraryWatchers.ToList())
            {
                try { watcher.EnableRaisingEvents = false; } catch { }
                try { watcher.Created -= LibraryWatcher_OnChange; } catch { }
                try { watcher.Changed -= LibraryWatcher_OnChange; } catch { }
                try { watcher.Deleted -= LibraryWatcher_OnChange; } catch { }
                try { watcher.Renamed -= LibraryWatcher_OnRename; } catch { }
                try { watcher.Error -= LibraryWatcher_OnError; } catch { }
                try { watcher.Dispose(); } catch { }
            }

            _libraryWatchers.Clear();
        }

        private void ReconfigureLibraryWatchers()
        {
            try { DisposeLibraryWatchers(); } catch { }

            if (IsDisposed)
                return;

            if (!string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                return;

            if (!IsLocalLibraryCategory(_selCat))
                return;

            foreach (var rawRoot in AllRootsForCategory(_selCat).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var root = NormalizeRootPath(rawRoot);
                    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                        continue;

                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName
                                     | NotifyFilters.DirectoryName
                                     | NotifyFilters.LastWrite
                                     | NotifyFilters.CreationTime
                                     | NotifyFilters.Size
                    };

                    watcher.Created += LibraryWatcher_OnChange;
                    watcher.Changed += LibraryWatcher_OnChange;
                    watcher.Deleted += LibraryWatcher_OnChange;
                    watcher.Renamed += LibraryWatcher_OnRename;
                    watcher.Error += LibraryWatcher_OnError;
                    watcher.EnableRaisingEvents = true;
                    _libraryWatchers.Add(watcher);
                }
                catch { }
            }
        }

        private void LibraryWatcher_OnError(object sender, ErrorEventArgs e)
        {
            try { QueueLibraryWatcherRefresh(); } catch { }
        }

        private void LibraryWatcher_OnRename(object sender, RenamedEventArgs e)
        {
            try
            {
                if (ShouldWatcherTriggerForPath(e.OldFullPath) || ShouldWatcherTriggerForPath(e.FullPath))
                    QueueLibraryWatcherRefresh();
            }
            catch { }
        }

        private void LibraryWatcher_OnChange(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (ShouldWatcherTriggerForPath(e.FullPath))
                    QueueLibraryWatcherRefresh();
            }
            catch { }
        }

        private bool ShouldWatcherTriggerForPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            try
            {
                if (Directory.Exists(path))
                    return true;
            }
            catch { }

            string ext = string.Empty;
            try { ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant(); } catch { }

            return ExtsForCategory(_selCat).Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        private void QueueLibraryWatcherRefresh()
        {
            if (IsDisposed)
                return;

            _libraryWatcherRefreshQueued = true;

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            _libraryWatcherDebounce.Stop();
                            _libraryWatcherDebounce.Start();
                        }
                        catch { }
                    }));
                    return;
                }

                _libraryWatcherDebounce.Stop();
                _libraryWatcherDebounce.Start();
            }
            catch { }
        }


        // ------------ open file/url ------------
        // overload base: senza ripresa
        private void SafeOpen(string pathOrUrl)
            => SafeOpen(pathOrUrl, resumeSeconds: null);

        // overload con posizione di ripresa (per il carosello)
        private void SafeOpen(string pathOrUrl, double? resumeSeconds)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pathOrUrl)) return;

                bool isUrl = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var u) &&
                             (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

                if (!isUrl && !File.Exists(pathOrUrl))
                {
                    HandleMissingMediaPath(pathOrUrl);
                    return;
                }

                // NEW: se è un file "musica" (locale o URL con estensione audio), salvalo nei recenti musica
                if (IsMusicFilePath(pathOrUrl))
                {
                    _musicRecents.RegisterPlay(pathOrUrl);
                }

                try { _thumbCts?.Cancel(); } catch { }

                if (resumeSeconds.HasValue && OpenWithResumeRequested != null)
                {
                    OpenWithResumeRequested(pathOrUrl, resumeSeconds.Value);
                }
                else
                {
                    OpenRequested?.Invoke(pathOrUrl);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show(this,
                        $"Impossibile aprire la sorgente:\n{ex.Message}",
                        "Errore riproduzione",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch { }
            }
        }

        // gestisce il caso in cui un file locale indicizzato non esiste più
        private void HandleMissingMediaPath(string path)
        {
            try
            {
                MessageBox.Show(this,
                    "Il file non esiste più.\nLo rimuovo dalla libreria.",
                    "File non trovato",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch { }

            // ci interessa solo per la libreria locale ("Tutti i file")
            bool isLocalGridContext =
                string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase) &&
                IsLocalLibraryCategory(_selCat);

            if (!isLocalGridContext)
                return;

            // 1) aggiorna la cache in memoria
            List<FileInfo> newCache;
            lock (_cacheLock)
            {
                newCache = _cache
                    .Where(fi => !string.Equals(fi.FullName, path, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _cache = newCache;
            }

            // 2) aggiorna l'indice JSON su disco per la categoria corrente
            _libraryIndex.ReplacePaths(_selCat, newCache.Select(fi => fi.FullName));

            // 3) togli dai preferiti (se presente)
            _favs.Set(path, fav: false);

            // 4) rimuovi la card dalla griglia "Tutti i file"
            var toRemove = _grid.Controls
                .OfType<Control>()
                .FirstOrDefault(c =>
                    (c is FileCard fc && string.Equals(fc.FilePath, path, StringComparison.OrdinalIgnoreCase)) ||
                    (c is SeasonSelectorCard sc && string.Equals(sc.RepresentativePath, path, StringComparison.OrdinalIgnoreCase)));

            if (toRemove != null)
            {
                _grid.Controls.Remove(toRemove);
                toRemove.Dispose();
                _grid.UpdateThemedScrollbar();
                _grid.Invalidate(true);
                _grid.Update();
            }
        }


        // =========================
        // DPAD: quale root usare quando ci sono overlay interni (manage folders / mask / pane URL/YT)
        // =========================
        internal Control GetRemoteFocusRoot()
        {
            // Overlay creazione playlist: cattura il focus qui finché è aperto.
            if (_playlistEditorOverlay != null && _playlistEditorOverlay.Visible) return _playlistEditorOverlay;

            // Overlay selezione episodi: quando è aperto deve essere il vero root DPAD.
            if (_seasonEpisodeOverlay != null && _seasonEpisodeOverlay.Visible) return _seasonEpisodeOverlay;

            // Overlay gestione cartelle: cattura il focus qui
            // (la tastiera a schermo invece viene gestita dalla logica DPAD della pagina,
            // così possiamo avere una navigazione deterministica tra i tasti).
            if (_rootsOverlay != null && _rootsOverlay.Visible) return _rootsOverlay;

            // Nota: URL / YouTube / DLNA NON sono overlay: devono restare dentro
            // la navigazione complessiva (menu sinistro + contenuti).
            return this;
        }


        // =========================
        // DPAD / Remote navigation (library page specific)
        // =========================

        // Richiesta di "remote focus" verso il PlayerForm (focus ring / ensure visible).
        // Qui non disegniamo/gestiamo il ring: notifichiamo solo l'host.
        internal event Action<Control>? RemoteFocusRequested;

        internal void RequestRemoteFocus(Control c)
        {
            try { RemoteFocusRequested?.Invoke(c); } catch { }
        }

        internal bool IsRemoteContentFocusCandidate(Control? c)
        {
            try
            {
                var norm = NormalizeRemoteTarget(c);
                if (norm == null || norm.IsDisposed || !norm.Visible || !norm.Enabled)
                    return false;

                var seasonOverlayFocus = NormalizeSeasonEpisodeOverlayFocus(norm);
                if (seasonOverlayFocus != null && ReferenceEquals(norm, seasonOverlayFocus))
                    return true;

                var inlineCta = GetInlineRootsCallToActionFocusTarget();
                if (inlineCta != null && ReferenceEquals(norm, inlineCta))
                    return true;

                if (_grid != null && IsDescendant(_grid, norm) && IsGridPrimaryCardControl(norm))
                    return true;

                if (_carouselHost != null && _carouselHost.Visible && IsDescendant(_carouselHost, norm))
                    return true;
            }
            catch { }

            return false;
        }

        internal void EnsureInitialContentPrepared()
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(EnsureInitialContentPrepared)); } catch { }
                return;
            }

            if (_initialContentPrepared)
                return;

            _initialContentPrepared = true;

            try { ShowMask(string.Empty, showSpinner: false); } catch { }
            try { RefreshContent(); } catch { }
            try
            {
                if (_carouselHost.Visible)
                    ForceCarouselRefresh();
            }
            catch { }
        }

        internal void SuspendInitialPointerHoverUntilMouseMove()
        {
            try { _grid?.SuspendHoverTrackingUntilMouseMove(); } catch { }
        }

        private Control? GetInlineRootsCallToActionFocusTarget()
        {
            try
            {
                if (_inlineRootsCallToActionHost == null || !_inlineRootsCallToActionHost.Visible)
                    return null;

                if (_inlineRootsCallToActionButton != null && !_inlineRootsCallToActionButton.IsDisposed && _inlineRootsCallToActionButton.Visible && _inlineRootsCallToActionButton.Enabled)
                    return _inlineRootsCallToActionButton;
            }
            catch { }

            return null;
        }

        private Control? FindGridCardByRepresentativePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                return _grid.Controls
                    .OfType<Control>()
                    .FirstOrDefault(c =>
                        (c is FileCard fc && string.Equals(fc.FilePath, path, StringComparison.OrdinalIgnoreCase)) ||
                        (c is SeasonSelectorCard sc && string.Equals(sc.RepresentativePath, path, StringComparison.OrdinalIgnoreCase)));
            }
            catch { }

            return null;
        }

        private Control? NormalizeSeasonEpisodeOverlayFocus(Control? current)
        {
            try
            {
                if (_seasonEpisodeOverlay?.Visible != true)
                    return null;

                var norm = NormalizeRemoteTarget(current);
                if (norm != null && !norm.IsDisposed && IsDescendant(_seasonEpisodeOverlay, norm) && norm.Visible && norm.Enabled)
                    return norm;
            }
            catch { }

            return GetSeasonEpisodeOverlayFocusTarget();
        }

        private bool TryRemoteMoveInSeasonEpisodeOverlay(Control cur, string dir, out Control? next)
        {
            next = null;

            if (_seasonEpisodeOverlay?.Visible != true)
                return false;

            var active = NormalizeSeasonEpisodeOverlayFocus(cur) ?? GetSeasonEpisodeOverlayFocusTarget();
            if (active == null || active.IsDisposed)
                return false;

            bool onList = ReferenceEquals(active, _seasonEpisodeOverlayList) || (_seasonEpisodeOverlayList != null && IsDescendant(_seasonEpisodeOverlayList, active));
            bool onOpen = ReferenceEquals(active, _seasonEpisodeOverlayOpenButton);
            bool onClose = ReferenceEquals(active, _seasonEpisodeOverlayCloseButton);

            switch ((dir ?? string.Empty).ToLowerInvariant())
            {
                case "up":
                    if (onOpen || onClose)
                    {
                        next = _seasonEpisodeOverlayList ?? active;
                        return true;
                    }
                    MoveSeasonEpisodeOverlaySelection(-1);
                    next = _seasonEpisodeOverlayList ?? active;
                    return true;

                case "down":
                    if (onOpen || onClose)
                    {
                        next = _seasonEpisodeOverlayList ?? active;
                        return true;
                    }
                    MoveSeasonEpisodeOverlaySelection(+1);
                    next = _seasonEpisodeOverlayList ?? active;
                    return true;

                case "right":
                    if (onList)
                        next = _seasonEpisodeOverlayOpenButton ?? _seasonEpisodeOverlayCloseButton ?? _seasonEpisodeOverlayList ?? active;
                    else if (onOpen)
                        next = _seasonEpisodeOverlayCloseButton ?? _seasonEpisodeOverlayOpenButton ?? _seasonEpisodeOverlayList ?? active;
                    else
                        next = _seasonEpisodeOverlayList ?? _seasonEpisodeOverlayOpenButton ?? _seasonEpisodeOverlayCloseButton ?? active;
                    return true;

                case "left":
                    if (onClose)
                        next = _seasonEpisodeOverlayOpenButton ?? _seasonEpisodeOverlayList ?? _seasonEpisodeOverlayCloseButton ?? active;
                    else if (onOpen)
                        next = _seasonEpisodeOverlayList ?? _seasonEpisodeOverlayOpenButton ?? _seasonEpisodeOverlayCloseButton ?? active;
                    else
                        next = _seasonEpisodeOverlayCloseButton ?? _seasonEpisodeOverlayOpenButton ?? _seasonEpisodeOverlayList ?? active;
                    return true;

                default:
                    next = active;
                    return true;
            }
        }

        private Control? FindFirstPrimaryContentControl()
        {
            try
            {
                var inlineCta = GetInlineRootsCallToActionFocusTarget();
                if (inlineCta != null && !inlineCta.IsDisposed && inlineCta.Visible && inlineCta.Enabled)
                    return inlineCta;
            }
            catch { }

            try
            {
                var contentStart = GetRemoteContentStart();
                if (contentStart != null && !contentStart.IsDisposed && contentStart.Visible && contentStart.Enabled)
                    return contentStart;
            }
            catch { }

            try
            {
                foreach (Control c in _grid.Controls)
                {
                    var norm = NormalizeRemoteTarget(c);
                    if (norm == null || norm.IsDisposed || !norm.Visible || !norm.Enabled)
                        continue;

                    if (IsGridPrimaryCardControl(norm))
                        return norm;
                }
            }
            catch { }

            return null;
        }

        private void TryFulfillPendingContentFocusAfterRender()
        {
            if (!_pendingFocusFirstGridItem)
                return;

            if (IsDisposed)
                return;

            try
            {
                if (_mask != null && _mask.Visible)
                    return;
                if (IsAppOskVisible)
                    return;
                if (_seasonEpisodeOverlay != null && _seasonEpisodeOverlay.Visible)
                    return;
                if (_rootsOverlay != null && _rootsOverlay.Visible)
                    return;
            }
            catch { }

            var target = FindFirstPrimaryContentControl();
            if (target == null || target.IsDisposed || !target.Visible || !target.Enabled)
                return;

            int version = _remoteFocusRenderVersion;
            _pendingFocusFirstGridItem = false;
            _remoteZone = RemoteZone.Content;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (IsDisposed) return;
                        if (version != _remoteFocusRenderVersion) return;
                        if (target == null || target.IsDisposed || !target.Visible || !target.Enabled) return;
                        if (_mask != null && _mask.Visible) return;

                        try { target.Focus(); } catch { }
                        RequestRemoteFocus(target);
                    }
                    catch { }
                }));
            }
            catch { }
        }

        internal Control? GetRemoteDefaultFocusTarget()
        {
            _remoteZone = RemoteZone.LeftNav;

            var sel = _catButtons.FirstOrDefault(b => b.Selected)
                      ?? _catButtons.FirstOrDefault(b => string.Equals(b.Text, _selCat, StringComparison.OrdinalIgnoreCase))
                      ?? _catButtons.FirstOrDefault();

            _remoteLastMenuFocus = sel;
            return sel;
        }

        private bool IsRemoteContentControl(Control? c)
        {
            try
            {
                if (c == null || c.IsDisposed)
                    return false;

                if (_rootsOverlay != null && _rootsOverlay.Visible && IsDescendant(_rootsOverlay, c))
                    return true;

                if (_seasonEpisodeOverlay != null && _seasonEpisodeOverlay.Visible && IsDescendant(_seasonEpisodeOverlay, c))
                    return true;

                if (IsAppOskVisible && _appOskOverlay != null && IsDescendant(_appOskOverlay, c))
                    return true;

                if (_carouselHost != null && IsDescendant(_carouselHost, c))
                    return true;

                if (_grid != null && IsDescendant(_grid, c))
                    return true;

                if (_header != null && IsDescendant(_header, c))
                    return true;
            }
            catch { }

            return false;
        }

        internal void SyncRemoteZoneFromExternalFocus(Control? current)
        {
            try
            {
                if (current == null || current.IsDisposed || !IsDescendant(this, current))
                    return;

                var norm = NormalizeRemoteTarget(current);
                if (norm == null || norm.IsDisposed)
                    return;

                if (IsInLeftNav(norm))
                {
                    _remoteZone = RemoteZone.LeftNav;
                    _remoteLastMenuFocus = norm;
                    return;
                }

                if (IsRemoteContentControl(norm))
                    _remoteZone = RemoteZone.Content;
            }
            catch { }
        }

        internal Control? CoerceRemoteFocus(Control? current)
        {
            // Se il controllo attuale non è valido (disposto, cambiata griglia, ecc.),
            // riparti da un target coerente con lo stato di navigazione.
            try
            {
                if (current != null && !current.IsDisposed && IsDescendant(this, current))
                {
                    var norm = NormalizeRemoteTarget(current);

                    // Se siamo già in zona contenuti, ma il focus è finito su un controllo “non contenuto”
                    // (tipicamente header/search), proviamo a riportarlo al primo elemento utile.
                    if (_remoteZone == RemoteZone.Content && norm != null && !norm.IsDisposed && !IsInLeftNav(norm))
                    {
                        bool inRoots = _rootsOverlay?.Visible == true && IsDescendant(_rootsOverlay, norm);
                        bool inCarousel = _carouselHost?.Visible == true && IsDescendant(_carouselHost, norm);
                        bool inGrid = _grid != null && IsDescendant(_grid, norm);
                        bool inHeader = _header != null && IsDescendant(_header, norm);

                        // Tastiera a schermo: non forzare il focus fuori dall'OSK.
                        bool inOsk = IsAppOskVisible && _appOskOverlay != null && IsDescendant(_appOskOverlay, norm);

                        if (!inRoots && !inCarousel && !inGrid && !inHeader && !inOsk)
                        {
                            var startCtrl = GetRemoteContentStart();
                            if (startCtrl != null) return startCtrl;
                        }
                    }

                    return norm;
                }
            }
            catch { }

            if (_remoteZone == RemoteZone.LeftNav)
                return GetRemoteDefaultFocusTarget();

            var startCtrlFallback = GetRemoteContentStart();
            return startCtrlFallback ?? GetRemoteDefaultFocusTarget();
        }

        internal bool TryRemoteMove(Control? current, string dir, out Control? next)
        {
            next = null;
            if (IsDisposed) return false;

            var seasonOverlayFocus = NormalizeSeasonEpisodeOverlayFocus(current);
            if (seasonOverlayFocus != null)
            {
                _remoteZone = RemoteZone.Content;
                return TryRemoteMoveInSeasonEpisodeOverlay(seasonOverlayFocus, dir, out next);
            }

            // Tastiera a schermo: navigazione DPAD deterministica tra i tasti.
            // Va gestita PRIMA della mask, perché durante il filtro la mask può attivarsi
            // e altrimenti bloccherebbe le frecce facendo sembrare la tastiera 'bloccata/scomparsa'.
            if (IsAppOskVisible && _appOsk != null)
            {
                _remoteZone = RemoteZone.Content;

                Control? curOsk = current;
                try
                {
                    if (curOsk == null || curOsk.IsDisposed || _appOskOverlay == null || !IsDescendant(_appOskOverlay, curOsk))
                        curOsk = _appOsk.GetDefaultFocusTarget();
                }
                catch { }

                if (curOsk == null)
                    curOsk = _search ?? GetRemoteContentStart() ?? GetRemoteMenuFocusFallback();

                try
                {
                    if (_appOsk.TryDpadMove(curOsk, dir, out var nextOsk) && nextOsk != null)
                    {
                        next = nextOsk;
                        return true;
                    }
                }
                catch { }

                next = curOsk;
                return true;
            }

            var cur = CoerceRemoteFocus(current);
            if (cur == null) return false;

            SyncRemoteZoneFromExternalFocus(cur);

            // Mask di caricamento: evitiamo di "spostare" il focus nei contenuti mentre la UI
            // è in aggiornamento (ma lasciamo comunque muovere il menu sinistro).
            if (_mask != null && _mask.Visible && !IsInLeftNav(cur))
            {
                next = cur;
                return true;
            }

            // Se il focus è nel menu sinistro (o siamo in modalità menu),
            // blocchiamo la navigazione SOLO lì finché non si preme OK.
            if (IsInLeftNav(cur) || _remoteZone == RemoteZone.LeftNav)
            {
                _remoteZone = RemoteZone.LeftNav;
                return TryRemoteMoveInLeftNav(cur, dir, out next);
            }

            _remoteZone = RemoteZone.Content;
            return TryRemoteMoveInContent(cur, dir, out next);
        }

        internal bool TryRemotePostOkFocus(Control? previouslyFocused, out Control? next)
        {
            next = null;
            if (IsDisposed) return false;

            if (previouslyFocused == null || previouslyFocused.IsDisposed)
                return false;

            var seasonOverlayFocus = GetSeasonEpisodeOverlayFocusTarget();
            if (seasonOverlayFocus != null)
            {
                _pendingFocusFirstGridItem = false;
                _remoteZone = RemoteZone.Content;
                next = seasonOverlayFocus;
                return true;
            }

            // OK sul menu sinistro: dopo il click, porta il focus ai contenuti.
            var prev = NormalizeRemoteTarget(previouslyFocused);
            if (IsInLeftNav(prev))
            {
                _remoteLastMenuFocus = prev;

                // Prova a portare subito il focus sul primo elemento reale dei contenuti.
                // IMPORTANT: non "atterrare" mai sull'header (SearchBox) quando si entra
                // dai menu: in contesti senza carousel il focus finiva per rimanere sulla barra.
                var start = GetRemoteContentStart();

                // Se abbiamo già un focusable nei contenuti (FileCard o altre card/pannelli come YouTube),
                // portiamo subito il focus lì.
                if (start != null && !IsInHeader(start))
                {
                    _pendingFocusFirstGridItem = false;
                    _remoteZone = RemoteZone.Content;
                    next = start;
                    return true;
                }

                // YouTube: se non ci sono ancora risultati, almeno entra sulla SearchBox
                // (così si può digitare subito senza restare "bloccati" sul catalogo).
                if (string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (_search != null && !_search.IsDisposed && _search.Visible && _search.Enabled)
                        {
                            _pendingFocusFirstGridItem = false;
                            _remoteZone = RemoteZone.Content;
                            next = _search;
                            return true;
                        }
                    }
                    catch { }
                }

                // Se il render è ancora in corso (o la griglia sta per essere rinfrescata),
                // NON spostare il focus sull'header: resta nel menu e auto-focalizza la prima
                // card appena viene aggiunta dal render progressivo.
                try
                {
                    bool isDeferredCollectionContext =
                        string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase);

                    bool isLocalContext = (IsLocalLibraryCategory(_selCat) || isDeferredCollectionContext || string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase))
                                          && !string.Equals(_selSrc, "URL", StringComparison.OrdinalIgnoreCase)
                                          && !string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase);

                    if (isLocalContext)
                    {
                        _pendingFocusFirstGridItem = true;
                        try { TryFulfillPendingContentFocusAfterRender(); } catch { }
                    }
                }
                catch { _pendingFocusFirstGridItem = true; }

                _remoteZone = RemoteZone.LeftNav;
                next = prev;
                return next != null;
            }

            return false;
        }

        internal bool TryRemoteBack(Control? current, out Control? next)
        {
            next = null;
            if (IsDisposed) return false;

            // Tastiera a schermo: BACK deve SOLO chiuderla (non cambiare zona / non chiudere la libreria)
            if (IsAppOskVisible)
            {
                try { HideAppOsk(); } catch { }

                _remoteZone = RemoteZone.Content;
                next = _search ?? GetRemoteContentStart() ?? GetPreferredMenuBackTarget();
                return true;
            }

            // Editor playlist: BACK lo chiude senza uscire dalla libreria.
            if (_playlistEditorOverlay != null && _playlistEditorOverlay.Visible)
            {
                try { ClosePlaylistEditorOverlay(); } catch { }
                _remoteZone = RemoteZone.Content;
                next = (_btnCollectionBack != null && _btnCollectionBack.Visible)
                    ? _btnCollectionBack
                    : ((_btnPlayCollection != null && _btnPlayCollection.Visible)
                        ? _btnPlayCollection
                        : ((_btnCreatePlaylist != null && _btnCreatePlaylist.Visible)
                            ? _btnCreatePlaylist
                            : (GetRemoteContentStart() ?? GetRemoteMenuFocusFallback())));
                return true;
            }

            if (_seasonEpisodeOverlay != null && _seasonEpisodeOverlay.Visible)
            {
                string? focusPath = null;
                try { focusPath = _seasonEpisodeOverlayGroup?.RepresentativePath; } catch { }
                try { CloseSeasonEpisodeOverlay(); } catch { }
                _pendingFocusFirstGridItem = false;
                _remoteZone = RemoteZone.Content;
                next = FindGridCardByRepresentativePath(focusPath) ?? GetRemoteContentStart() ?? GetPreferredMenuBackTarget();
                return true;
            }

            // Se è aperto l'overlay delle root, BACK lo chiude (prima di uscire dalla libreria)
            if (_rootsOverlay != null && _rootsOverlay.Visible)
            {
                try { CloseRootsOverlay(commit: false, refreshAfterCommit: false); } catch { }
                _remoteZone = RemoteZone.LeftNav;
                next = GetPreferredMenuBackTarget();
                return true;
            }

            var cur = CoerceRemoteFocus(current);
            if (cur == null) return false;

            SyncRemoteZoneFromExternalFocus(cur);

            if (_mask != null && _mask.Visible)
            {
                _pendingFocusFirstGridItem = false;
                _remoteZone = RemoteZone.LeftNav;
                next = GetPreferredMenuBackTarget();
                return true;
            }

            // Se siamo nei contenuti, BACK torna al menu sinistro.
            if (_remoteZone == RemoteZone.Content && !IsInLeftNav(cur))
            {
                try
                {
                    if (HandleCollectionBackRequested())
                    {
                        _remoteZone = RemoteZone.Content;
                        next = (_btnCollectionBack != null && _btnCollectionBack.Visible)
                            ? _btnCollectionBack
                            : ((_btnPlayCollection != null && _btnPlayCollection.Visible)
                                ? _btnPlayCollection
                                : ((_btnCreatePlaylist != null && _btnCreatePlaylist.Visible)
                                    ? _btnCreatePlaylist
                                    : GetRemoteContentStart()));
                        return true;
                    }
                }
                catch { }

                // Se avevamo pianificato l'autofocus sulla prima card (render progressivo),
                // annulliamolo: l'utente è tornato al menu.
                _pendingFocusFirstGridItem = false;
                _remoteZone = RemoteZone.LeftNav;
                next = GetPreferredMenuBackTarget();
                return true;
            }

            // Nel menu sinistro: non gestiamo qui (PlayerForm chiuderà la libreria).
            _remoteZone = RemoteZone.LeftNav;
            return false;
        }


        // -------------------------
        // SearchBox (header) OK / ESC handling
        // -------------------------

        internal bool IsSearchEditor(Control? c)
        {
            try
            {
                return _search?.Inner != null && c != null && ReferenceEquals(c, _search.Inner);
            }
            catch { return false; }
        }

        /// <summary>
        /// Gestione OK su controlli speciali in header. Serve per entrare nel campo ricerca
        /// solo su OK/ENTER (non solo passando sopra con le frecce).
        /// </summary>
        internal bool TryRemoteOk(Control? current, out Control? next)
        {
            next = null;
            if (IsDisposed) return false;

            try
            {
                var cur = CoerceRemoteFocus(current);
                if (cur == null || cur.IsDisposed) return false;

                cur = NormalizeRemoteTarget(cur);
                SyncRemoteZoneFromExternalFocus(cur);

                var seasonOverlayFocus = NormalizeSeasonEpisodeOverlayFocus(cur);
                if (seasonOverlayFocus != null)
                {
                    _remoteZone = RemoteZone.Content;
                    if (ReferenceEquals(seasonOverlayFocus, _seasonEpisodeOverlayCloseButton))
                    {
                        string? focusPath = null;
                        try { focusPath = _seasonEpisodeOverlayGroup?.RepresentativePath; } catch { }
                        try { CloseSeasonEpisodeOverlay(); } catch { }
                        next = FindGridCardByRepresentativePath(focusPath) ?? GetRemoteContentStart() ?? GetPreferredMenuBackTarget();
                        return true;
                    }

                    OpenSelectedSeasonEpisode();
                    next = seasonOverlayFocus;
                    return true;
                }

                // OK sulla search box:
                // - Se arriva dal Web Remote → apri la nostra OSK (senza dare focus al TextBox, così non
                //   compare la tastiera di Windows e non perdiamo il focus DPAD).
                // - Se arriva da tastiera locale → entra in edit del TextBox (niente OSK).
                if (_search != null && ReferenceEquals(cur, _search) && _search.Inner != null)
                {
                    _remoteZone = RemoteZone.Content;

                    if (_dpadInputIsRemote)
                    {
                        try
                        {
                            // arma anche la compat su GotFocus (nel caso Header.cs usi quel path)
                            ArmRemoteOnScreenKeyboardForSearch();
                            ShowAppOsk(_search.Inner);
                            next = _appOsk?.GetDefaultFocusTarget() ?? _search;
                        }
                        catch
                        {
                            next = _search;
                        }
                        return true;
                    }

                    // Tastiera fisica / mouse: focus sul textbox e stop.
                    next = _search.Inner;
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            _search.Inner.Focus();
                            _search.Inner.SelectAll();
                        }
                        catch { }
                    }));
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// ESC/BACK mentre si sta editando la ricerca: esce dall'editing senza chiudere la libreria.
        /// </summary>
        internal bool TryRemoteExitSearchEdit(Control? current, out Control? next)
        {
            next = null;
            if (IsDisposed) return false;

            try
            {
                if (_search?.Inner != null && current != null && ReferenceEquals(current, _search.Inner))
                {
                    try { _search.Inner.SelectionLength = 0; } catch { }
                    try { _search.Focus(); } catch { }

                    _remoteZone = RemoteZone.Content;
                    next = _search;
                    return true;
                }
            }
            catch { }

            return false;
        }


        // -------------------------
        // Left nav movement
        // -------------------------

        private bool TryRemoteMoveInLeftNav(Control cur, string dir, out Control? next)
        {
            next = null;

            var items = GetLeftNavFocusableItems();
            if (items.Count == 0)
            {
                next = cur;
                return true;
            }

            cur = NormalizeRemoteTarget(cur);

            int idx = items.IndexOf(cur);
            if (idx < 0)
            {
                if (_remoteLastMenuFocus != null)
                {
                    var n = NormalizeRemoteTarget(_remoteLastMenuFocus);
                    idx = items.IndexOf(n);
                }
                if (idx < 0)
                {
                    // fallback: categoria selezionata o primo
                    var sel = GetRemoteDefaultFocusTarget();
                    if (sel != null)
                        idx = Math.Max(0, items.IndexOf(sel));
                }
            }

            if (idx < 0) idx = 0;

            int newIdx = idx;
            switch ((dir ?? "").ToLowerInvariant())
            {
                case "up":
                    newIdx = Math.Max(0, idx - 1);
                    break;
                case "down":
                    newIdx = Math.Min(items.Count - 1, idx + 1);
                    break;
                case "left":
                case "right":
                default:
                    newIdx = idx; // lock nel menu
                    break;
            }

            next = items[newIdx];
            _remoteLastMenuFocus = next;
            return true;
        }

        private List<Control> GetLeftNavFocusableItems()
        {
            var list = new List<Control>();

            foreach (var b in _catButtons)
                if (b != null && !b.IsDisposed && b.Visible && b.Enabled)
                    list.Add(b);

            foreach (var b in _srcButtons)
                if (b != null && !b.IsDisposed && b.Visible && b.Enabled)
                    list.Add(b);

            if (_btnClose != null && !_btnClose.IsDisposed && _btnClose.Visible && _btnClose.Enabled)
                list.Add(_btnClose);

            return list;
        }


        // -------------------------
        // Content movement (carousel + grid)
        // -------------------------

        private bool TryRemoteMoveInContent(Control cur, string dir, out Control? next)
        {
            next = null;
            cur = NormalizeRemoteTarget(cur);

            var carouselItems = GetCarouselFocusableItems();
            var gridRows = BuildGridRows();
            var headerItems = GetHeaderFocusableItems();

            // 0) Header focus
            int hdrIdx = headerItems.IndexOf(cur);
            if (hdrIdx >= 0)
            {
                int cx = GetCenterX(cur);
                switch ((dir ?? "").ToLowerInvariant())
                {
                    case "right":
                        next = (hdrIdx < headerItems.Count - 1) ? headerItems[hdrIdx + 1] : cur;
                        return true;
                    case "left":
                        next = (hdrIdx > 0) ? headerItems[hdrIdx - 1] : cur;
                        return true;
                    case "down":
                        if (carouselItems.Count > 0)
                            next = PickClosestByX(carouselItems, cx) ?? cur;
                        else
                            next = PickClosestByX(GetFirstGridRowItems(gridRows), cx) ?? cur;
                        return true;
                    case "up":
                    default:
                        next = cur;
                        return true;
                }
            }

            // 1) Carousel focus
            int carIdx = carouselItems.IndexOf(cur);
            if (carIdx >= 0)
            {
                int cx = GetCenterX(cur);
                switch ((dir ?? "").ToLowerInvariant())
                {
                    case "right":
                        next = (carIdx < carouselItems.Count - 1) ? carouselItems[carIdx + 1] : cur; // stop
                        return true;
                    case "left":
                        if (carIdx > 0)
                        {
                            next = carouselItems[carIdx - 1];
                            return true;
                        }
                        // prima card -> CLAMP (uscita dal contenuto solo con BACK/ESC)
                        next = cur;
                        return true;
                    case "down":
                        next = PickClosestByX(GetFirstGridRowItems(gridRows), cx) ?? cur;
                        return true;
                    case "up":
                        next = (headerItems.Count > 0) ? (PickClosestByX(headerItems, cx) ?? cur) : cur;
                        return true;
                    default:
                        next = cur;
                        return true;
                }
            }

            // 2) Grid focus
            if (TryFindInGridRows(gridRows, cur, out var rowIndex, out var colIndex, out var curCenterX))
            {
                switch ((dir ?? "").ToLowerInvariant())
                {
                    case "right":
                        {
                            var row = gridRows[rowIndex];
                            if (colIndex < row.Count - 1)
                            {
                                next = row[colIndex + 1];
                            }
                            else
                            {
                                // fine riga -> prima della riga sotto
                                if (rowIndex < gridRows.Count - 1 && gridRows[rowIndex + 1].Count > 0)
                                    next = gridRows[rowIndex + 1][0];
                                else
                                    next = cur;
                            }
                            return true;
                        }
                    case "left":
                        {
                            var row = gridRows[rowIndex];
                            if (colIndex > 0)
                            {
                                next = row[colIndex - 1];
                            }
                            else
                            {
                                // prima colonna -> CLAMP (uscita dal contenuto solo con BACK/ESC)
                                next = cur;
                            }
                            return true;
                        }
                    case "up":
                        {
                            if (rowIndex > 0)
                            {
                                next = PickClosestByX(gridRows[rowIndex - 1], curCenterX) ?? cur;
                                return true;
                            }

                            // prima riga -> prova a salire al carosello (linea d'aria), altrimenti header
                            if (carouselItems.Count > 0)
                            {
                                next = PickClosestByX(carouselItems, curCenterX) ?? cur;
                                return true;
                            }

                            if (headerItems.Count > 0)
                            {
                                next = PickClosestByX(headerItems, curCenterX) ?? cur;
                                return true;
                            }

                            next = cur;
                            return true;
                        }
                    case "down":
                        {
                            if (rowIndex < gridRows.Count - 1)
                            {
                                next = PickClosestByX(gridRows[rowIndex + 1], curCenterX) ?? cur;
                                return true;
                            }
                            next = cur;
                            return true;
                        }
                    default:
                        next = cur;
                        return true;
                }
            }

            // 3) Fallback robusto: durante render/resize il target corrente può risultare
            // momentaneamente non mappato. In quel caso NON ripartire dal primo elemento,
            // ma resta nella stessa zona visiva del controllo corrente.
            try
            {
                int cx = GetCenterX(cur);

                if (_carouselHost != null && IsDescendant(_carouselHost, cur))
                {
                    next = PickClosestByX(carouselItems, cx) ?? carouselItems.FirstOrDefault() ?? cur;
                    return true;
                }

                if (_grid != null && IsDescendant(_grid, cur))
                {
                    var flattened = gridRows.SelectMany(r => r).ToList();
                    next = PickClosestByX(flattened, cx) ?? flattened.FirstOrDefault() ?? cur;
                    return true;
                }
            }
            catch { }

            next = cur;
            return true;
        }

        private Control? GetRemoteContentStart()
        {
            try
            {
                // Durante un refresh/cambio categoria non vogliamo mai riusare target vecchi
                // della griglia/carousel: il focus viene assegnato solo a caricamento finito.
                if (_mask != null && _mask.Visible)
                    return null;
            }
            catch { }

            try
            {
                var inlineCta = GetInlineRootsCallToActionFocusTarget();
                if (inlineCta != null)
                    return inlineCta;
            }
            catch { }

            var car = GetCarouselFocusableItems();
            if (car.Count > 0) return car[0];

            // FIX (2): quando non c'è il carosello, il primo focus NON deve finire
            // sull'header/search per via della geometria non ancora pronta.
            // Proviamo prima a prendere il 1° elemento focusabile nella griglia
            // senza dipendere da RectangleToScreen()/layout.
            try
            {
                var focusables = new List<Control>();
                CollectGridFocusables(_grid, focusables);

                var firstCard = focusables
                    .Select(NormalizeRemoteTarget)
                    .FirstOrDefault(c => c != null && !c.IsDisposed && c.Visible && c.Enabled && IsGridPrimaryCardControl(c));

                if (firstCard != null)
                    return firstCard;

                var firstAny = focusables
                    .Select(NormalizeRemoteTarget)
                    .FirstOrDefault(c => c != null && !c.IsDisposed && c.Visible && c.Enabled);

                if (firstAny != null)
                    return firstAny;
            }
            catch { }

            var rows = BuildGridRows();
            foreach (var r in rows)
                if (r.Count > 0)
                    return r[0];
            // Nessun contenuto focusable ancora disponibile: non forziamo il focus sul
            // contenitore (griglia/header). In contesto remoto, l'autofocus "pending"
            // spostera' il focus sulla prima FileCard appena viene renderizzata.
            return null;
        }

        private Control? GetPreferredMenuBackTarget()
        {
            if (string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var selectedCategoryButton = _catButtons.FirstOrDefault(b =>
                        b != null && !b.IsDisposed && b.Visible && b.Enabled && string.Equals(b.Text, _selCat, StringComparison.OrdinalIgnoreCase));
                    if (selectedCategoryButton != null)
                    {
                        _remoteLastMenuFocus = selectedCategoryButton;
                        return selectedCategoryButton;
                    }
                }
                catch { }
            }

            return GetRemoteMenuFocusFallback();
        }

        private Control? GetRemoteMenuFocusFallback()
        {
            if (_remoteLastMenuFocus != null)
            {
                try
                {
                    var n = NormalizeRemoteTarget(_remoteLastMenuFocus);
                    if (!n.IsDisposed && n.Visible && n.Enabled && IsInLeftNav(n))
                        return n;
                }
                catch { }
            }

            return GetRemoteDefaultFocusTarget();
        }


        // -------------------------
        // Helpers (focusable items + geometry)
        // -------------------------

        private List<Control> GetCarouselFocusableItems()
        {
            try
            {
                if (!_carouselHost.Visible || !_carouselViewport.Visible)
                    return new List<Control>();

                // _carouselViewport contiene un FlowLayoutPanel interno
                var flow = _carouselViewport.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
                if (flow == null) return new List<Control>();

                return flow.Controls
                    .OfType<Control>()
                    .Select(NormalizeRemoteTarget)
                    .Where(c => c != null && !c.IsDisposed && c.Visible && c.Enabled)
                    .Distinct()
                    .ToList();
            }
            catch
            {
                return new List<Control>();
            }
        }

        private List<List<Control>> BuildGridRows()
        {
            var items = new List<(Control c, Rectangle rc, int cx)>();

            try { _grid.PerformLayout(); } catch { }

            try
            {
                // raccogliamo tutti i focusable dentro la griglia, includendo pannelli sorgente (URL/YT/DLNA)
                var focusables = new List<Control>();
                CollectGridFocusables(_grid, focusables);

                foreach (var c0 in focusables)
                {
                    var c = NormalizeRemoteTarget(c0);
                    if (c == null) continue;
                    if (c.IsDisposed || !c.Visible || !c.Enabled) continue;

                    Rectangle rc;
                    try
                    {
                        Point screen = c.PointToScreen(Point.Empty);
                        Point local = _grid.PointToClient(screen);
                        rc = new Rectangle(local, c.Size);
                    }
                    catch
                    {
                        continue;
                    }

                    if (rc.Width <= 0 || rc.Height <= 0)
                        continue;

                    int cx = rc.Left + rc.Width / 2;
                    items.Add((c, rc, cx));
                }
            }
            catch { }

            // niente elementi
            if (items.Count == 0)
                return new List<List<Control>>();

            // ordinamento top-left
            items = items
                .OrderBy(t => t.rc.Top)
                .ThenBy(t => t.rc.Left)
                .ToList();

            // raggruppamento per righe con tolleranza più generosa per il FlowLayoutPanel
            const int rowTol = 40;
            var rows = new List<List<(Control c, Rectangle rc, int cx)>>();
            foreach (var it in items)
            {
                List<(Control c, Rectangle rc, int cx)>? targetRow = null;
                for (int i = rows.Count - 1; i >= 0; i--)
                {
                    int y0 = rows[i][0].rc.Top;
                    if (Math.Abs(it.rc.Top - y0) <= rowTol)
                    {
                        targetRow = rows[i];
                        break;
                    }
                }

                if (targetRow == null)
                    rows.Add(new List<(Control, Rectangle, int)> { it });
                else
                    targetRow.Add(it);
            }

            // sort each row left->right and project
            var outRows = new List<List<Control>>();
            foreach (var r in rows)
            {
                var rr = r
                    .OrderBy(t => t.rc.Left)
                    .ThenBy(t => t.rc.Top)
                    .Select(t => t.c)
                    .Distinct()
                    .ToList();
                if (rr.Count > 0)
                    outRows.Add(rr);
            }
            return outRows;
        }

        private List<Control> GetFirstGridRowItems(List<List<Control>> rows)
        {
            if (rows.Count == 0) return new List<Control>();
            return rows[0];
        }

        private static Control? PickClosestByX(IReadOnlyList<Control> row, int x)
        {
            if (row == null || row.Count == 0) return null;

            Control? best = null;
            long bestDist = long.MaxValue;
            int bestCx = int.MaxValue;

            foreach (var c in row)
            {
                if (c == null || c.IsDisposed || !c.Visible || !c.Enabled)
                    continue;

                int cx = GetCenterX(c);
                long d = Math.Abs((long)cx - x);
                if (d < bestDist || (d == bestDist && cx < bestCx))
                {
                    bestDist = d;
                    bestCx = cx;
                    best = c;
                }
            }
            return best;
        }

        // True se il controllo appartiene alla barra header (search/chip/buttons).
        // Serve per evitare che l'auto-focus "entri" nell'header quando si passa
        // dalla nav sinistra ai contenuti (soprattutto quando non c'è carousel).
        private bool IsInHeader(Control? c)
        {
            try
            {
                if (c == null || c.IsDisposed) return false;

                Control? p = c;
                while (p != null)
                {
                    if (ReferenceEquals(p, _header))
                        return true;
                    p = p.Parent;
                }
            }
            catch { }

            return false;
        }

        private static int GetCenterX(Control c)
        {
            try
            {
                var rc = c.RectangleToScreen(c.ClientRectangle);
                return rc.Left + rc.Width / 2;
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryFindInGridRows(List<List<Control>> rows, Control cur, out int rowIndex, out int colIndex, out int centerX)
        {
            rowIndex = -1;
            colIndex = -1;
            centerX = GetCenterX(cur);

            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                for (int c = 0; c < row.Count; c++)
                {
                    if (ReferenceEquals(row[c], cur))
                    {
                        rowIndex = r;
                        colIndex = c;
                        return true;
                    }
                }
            }

            return false;
        }

        private void CollectGridFocusables(Control parent, List<Control> list)
        {
            foreach (Control c in parent.Controls)
            {
                if (c == null || c.IsDisposed || !c.Visible || !c.Enabled)
                    continue;

                // Card principali della libreria: unità atomica
                // (evita di entrare nei child e beccare pulsanti/icone/combobox).
                if (IsGridPrimaryCardControl(c))
                {
                    list.Add(c);
                    continue;
                }

                // evita componenti puramente decorativi/strutturali
                if (c is PictureBox) { /* skip + descend? */ }

                if (IsRemoteFocusable(c))
                {
                    list.Add(c);
                    continue;
                }

                // continua la ricerca nei figli
                if (c.HasChildren)
                    CollectGridFocusables(c, list);
            }
        }

        private void ResetSearchOnNavigationChange()
        {
            try
            {
                if (_search?.Inner != null)
                {
                    _search.Inner.Text = "";
                    _search.Inner.SelectionStart = 0;
                    _search.Inner.SelectionLength = 0;
                }
            }
            catch { }

            // Se stavamo editando, sposta il focus fuori in modo che non "resti selezionato" su cambio categoria/sorgente.
            try
            {
                if (_search?.Inner != null && _search.Inner.Focused)
                {
                    try { _search.Focus(); } catch { }
                }
            }
            catch { }
        }

        private List<Control> GetHeaderFocusableItems()
        {
            var items = new List<Control>();

            try
            {
                // evidenziamo il contenitore (SearchBox) per non disegnare il focus sul TextBox interno
                if (_search != null && _search.Visible && _search.Enabled)
                    items.Add(_search);

                void add(Control? c)
                {
                    if (c == null) return;
                    if (c.IsDisposed) return;
                    if (!c.Visible || !c.Enabled) return;
                    if (!IsRemoteFocusable(c)) return;
                    items.Add(c);
                }

                add(_chipExt);
                add(_chipSort);
                add(_btnPlayCollection);
                add(_btnShuffleCollection);
                add(_btnCreatePlaylist);
                add(_btnCollectionBack);
                add(_btnBrowse);
                // YouTube header actions (Tendenze / Per te / Accedi / Esci)
                add(_btnYtTrending);
                add(_btnYtPersonal);
                add(_btnYtLogin);
                add(_btnYtLogout);
                add(_btnRefresh);
                add(_btnManageFolders);
                add(_btnAddFolder);
            }
            catch { }

            // Ordina per posizione sullo schermo (top→bottom, poi left→right)
            try
            {
                items = items
                    .Distinct()
                    .OrderBy(c => { try { return c.PointToScreen(Point.Empty).Y; } catch { return 0; } })
                    .ThenBy(c => { try { return c.PointToScreen(Point.Empty).X; } catch { return 0; } })
                    .ToList();
            }
            catch { }

            return items;
        }

        // =========================
        // FOTO/VIDEO/MUSICA: paging + banner ("Mostra altre...")
        // =========================
        private bool IsPhotoPagingContext()
        {
            // Usiamo questa logica anche per Video e Musica: evita liste infinite
            // e rende omogeneo il comportamento "Mostra altre".
            bool catOk = string.Equals(_selCat, "Foto", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(_selCat, "Video", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(_selCat, "Musica", StringComparison.OrdinalIgnoreCase);

            if (!catOk)
                return false;

            // in URL/YouTube non esiste una griglia locale
            if (string.Equals(_selSrc, "URL", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private int GetPagingPageSize()
        {
            if (string.Equals(_selCat, "Musica", StringComparison.OrdinalIgnoreCase)) return MusicPageSize;
            if (string.Equals(_selCat, "Video", StringComparison.OrdinalIgnoreCase)) return VideoPageSize;
            return PhotoPageSize;
        }

        private void ResetPhotoPagingState()
        {
            _photoMaxVisible = IsPhotoPagingContext() ? GetPagingPageSize() : int.MaxValue;
            RemovePhotoLoadMoreBanner();
        }

        private void RemovePhotoLoadMoreBanner()
        {
            try
            {
                if (_photoLoadMoreBanner != null && !_photoLoadMoreBanner.IsDisposed)
                {
                    if (_grid != null && _grid.Controls.Contains(_photoLoadMoreBanner))
                        _grid.Controls.Remove(_photoLoadMoreBanner);
                    _photoLoadMoreBanner.Dispose();
                }
            }
            catch { }
            _photoLoadMoreBanner = null;
        }

        private void EnsurePhotoLoadMoreBanner()
        {
            if (!IsPhotoPagingContext() || _grid == null)
            {
                RemovePhotoLoadMoreBanner();
                return;
            }

            int pageSize = GetPagingPageSize();

            if (_photoLoadMoreBanner == null || _photoLoadMoreBanner.IsDisposed)
            {
                _photoLoadMoreBanner = new LoadMoreBanner();
                _photoLoadMoreBanner.Margin = new Padding(0, 10, 0, 24);
                _photoLoadMoreBanner.Click += (_, __) =>
                {
                    try
                    {
                        if (!IsPhotoPagingContext()) return;
                        _photoMaxVisible += pageSize;
                        RemovePhotoLoadMoreBanner();
                        if (!_progressiveTimer.Enabled) _progressiveTimer.Start();
                    }
                    catch { }
                };
            }

            UpdatePhotoLoadMoreBannerText();

            if (!_grid.Controls.Contains(_photoLoadMoreBanner))
                _grid.Controls.Add(_photoLoadMoreBanner);

            LayoutPhotoLoadMoreBanner();
        }

        private void UpdatePhotoLoadMoreBannerText()
        {
            if (_photoLoadMoreBanner == null || _photoLoadMoreBanner.IsDisposed)
                return;

            int remaining = 0;
            try { remaining = Math.Max(0, _progressiveList.Count - _progressivePos); } catch { remaining = 0; }
            int pageSize = GetPagingPageSize();
            int toLoad = Math.Min(pageSize, remaining);

            if (toLoad <= 0)
            {
                _photoLoadMoreBanner.Title = "Nessun altro risultato";
                _photoLoadMoreBanner.Subtitle = "Hai già caricato tutto il contenuto disponibile";
                _photoLoadMoreBanner.Enabled = false;
                return;
            }

            _photoLoadMoreBanner.Enabled = true;

            // Testo dinamico per categoria
            if (string.Equals(_selCat, "Foto", StringComparison.OrdinalIgnoreCase))
            {
                _photoLoadMoreBanner.Title = $"Mostra altre {toLoad} foto";
                _photoLoadMoreBanner.Subtitle = "Carica altre foto nella galleria";
            }
            else if (string.Equals(_selCat, "Video", StringComparison.OrdinalIgnoreCase))
            {
                _photoLoadMoreBanner.Title = $"Mostra altri {toLoad} video";
                _photoLoadMoreBanner.Subtitle = "Carica altri video nella libreria";
            }
            else
            {
                _photoLoadMoreBanner.Title = $"Mostra altri {toLoad} brani";
                _photoLoadMoreBanner.Subtitle = "Carica altri brani nella libreria";
            }
        }



        private void LayoutGridScrollbarMask()
        {
            try
            {
                if (_gridScrollbarMask == null || _grid == null || _right == null)
                    return;

                bool show = _grid.Visible && _grid.Parent == _right && _grid.Width > 0 && _grid.Height > 0;
                if (!show)
                {
                    _gridScrollbarMask.Visible = false;
                    return;
                }

                int maskWidth = Math.Max(18, SystemInformation.VerticalScrollBarWidth + 6);
                var b = _grid.Bounds;
                _gridScrollbarMask.Bounds = new Rectangle(
                    Math.Max(0, b.Right - maskWidth),
                    Math.Max(0, b.Top),
                    maskWidth,
                    Math.Max(0, b.Height));

                _gridScrollbarMask.Visible = true;
                _gridScrollbarMask.BringToFront();
                try { _mask?.BringToFront(); } catch { }
                try { _rootsOverlay?.BringToFront(); } catch { }
                try { _appOskOverlay?.BringToFront(); } catch { }
            }
            catch { }
        }

        private void HideStickySectionDivider()
        {
            try
            {
                if (_stickySectionDivider != null && !_stickySectionDivider.IsDisposed)
                    _stickySectionDivider.Visible = false;
            }
            catch { }
        }

        private void EnsureStickySectionDivider(string title, string? bucket, int leftMargin)
        {
            bool needsRebuild = _stickySectionDivider == null ||
                                _stickySectionDivider.IsDisposed ||
                                !string.Equals(_stickySectionDividerTitle, title ?? string.Empty, StringComparison.Ordinal) ||
                                !string.Equals(_stickySectionDividerBucket ?? string.Empty, bucket ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            if (needsRebuild)
            {
                try
                {
                    if (_stickySectionDivider != null && !_stickySectionDivider.IsDisposed)
                    {
                        try { _right.Controls.Remove(_stickySectionDivider); } catch { }
                        try { _stickySectionDivider.Dispose(); } catch { }
                    }
                }
                catch { }

                _stickySectionDividerTitle = title ?? string.Empty;
                _stickySectionDividerBucket = bucket;
                _stickySectionDivider = new LibrarySectionDivider(_stickySectionDividerTitle, _stickySectionDividerBucket)
                {
                    Visible = false,
                    Enabled = false,
                    TabStop = false,
                    LeftMargin = Math.Max(0, leftMargin),
                    Margin = new Padding(0)
                };
                _right.Controls.Add(_stickySectionDivider);
            }

            if (_stickySectionDivider != null && !_stickySectionDivider.IsDisposed)
                _stickySectionDivider.LeftMargin = Math.Max(0, leftMargin);
        }

        private void UpdateStickySectionDivider()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated || _grid == null || _right == null || !_grid.Visible || _mask.Visible)
                {
                    HideStickySectionDivider();
                    return;
                }

                if (!string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase))
                {
                    HideStickySectionDivider();
                    return;
                }

                var dividers = _grid.Controls
                    .OfType<LibrarySectionDivider>()
                    .Where(d => d != null && !d.IsDisposed && d.Visible && d.Width > 0 && d.Height > 0)
                    .Select(d =>
                    {
                        Rectangle rect;
                        try { rect = _right.RectangleToClient(d.RectangleToScreen(d.ClientRectangle)); }
                        catch { rect = Rectangle.Empty; }
                        return new { Divider = d, Rect = rect };
                    })
                    .Where(x => !x.Rect.IsEmpty && x.Rect.Width > 0 && x.Rect.Height > 0)
                    .OrderBy(x => x.Rect.Top)
                    .ThenBy(x => x.Rect.Left)
                    .ToList();

                if (dividers.Count <= 1)
                {
                    HideStickySectionDivider();
                    return;
                }

                int stickyTop = _grid.Top;
                int activeIndex = -1;
                for (int i = 0; i < dividers.Count; i++)
                {
                    if (dividers[i].Rect.Top <= stickyTop + 1)
                        activeIndex = i;
                    else
                        break;
                }

                if (activeIndex < 0)
                {
                    HideStickySectionDivider();
                    return;
                }

                var active = dividers[activeIndex];
                int offsetY = 0;
                if (activeIndex + 1 < dividers.Count)
                {
                    var nextRect = dividers[activeIndex + 1].Rect;
                    int currentBottom = stickyTop + active.Rect.Height;
                    if (nextRect.Top < currentBottom)
                        offsetY = nextRect.Top - currentBottom;
                }

                EnsureStickySectionDivider(active.Divider.Title, active.Divider.Bucket, active.Divider.LeftMargin);
                if (_stickySectionDivider == null || _stickySectionDivider.IsDisposed)
                    return;

                _stickySectionDivider.Bounds = new Rectangle(
                    Math.Max(0, active.Rect.Left),
                    stickyTop + offsetY,
                    Math.Max(120, active.Rect.Width),
                    active.Rect.Height);
                _stickySectionDivider.Visible = true;
                _stickySectionDivider.BringToFront();
                try { _gridScrollbarMask?.BringToFront(); } catch { }
                try { _mask?.BringToFront(); } catch { }
                try { _rootsOverlay?.BringToFront(); } catch { }
                try { _appOskOverlay?.BringToFront(); } catch { }
            }
            catch
            {
                HideStickySectionDivider();
            }
        }
        private void LayoutPhotoLoadMoreBanner()
        {
            if (_photoLoadMoreBanner == null || _photoLoadMoreBanner.IsDisposed || _grid == null)
                return;

            try
            {
                // Allinea la larghezza del banner alla stessa "riga" delle FileCard,
                // usando la disposizione reale (Top/Left) invece della stima avail/outer
                // che puo' sovrastimare le colonne e rendere il banner troppo lungo a destra.

                int avail = _grid.ClientSize.Width - _grid.Padding.Left - _grid.Padding.Right;
                try
                {
                    if (_grid.VerticalScroll != null && _grid.VerticalScroll.Visible)
                        avail -= SystemInformation.VerticalScrollBarWidth;
                }
                catch { }
                avail = Math.Max(200, avail);

                var cards = _grid.Controls.OfType<FileCard>().Where(c => c.Visible).ToList();
                cards.Sort((a, b) =>
                {
                    int dy = a.Top.CompareTo(b.Top);
                    return dy != 0 ? dy : a.Left.CompareTo(b.Left);
                });

                if (cards.Count > 0)
                {
                    var first = cards[0];
                    int ml = first.Margin.Left;
                    int mr = first.Margin.Right;
                    int gap = ml + mr;

                    // margin come le card (sinistra/destra) per allineare anche l'inizio
                    _photoLoadMoreBanner.Margin = new Padding(ml, 10, mr, 24);

                    // Conta quante card stanno effettivamente sulla prima riga (stesso Top)
                    int y0 = first.Top;
                    int tol = 6;
                    int cols = 0;
                    foreach (var c in cards)
                    {
                        if (Math.Abs(c.Top - y0) <= tol) cols++;
                        else break;
                    }
                    cols = Math.Max(1, cols);

                    // Larghezza della riga in termini di "riquadri" (bordi delle card)
                    int targetW = cols * first.Width + Math.Max(0, cols - 1) * gap;

                    // Safety clamp dentro l'area disponibile (considerando i margin esterni)
                    int maxW = Math.Max(120, avail - (ml + mr));
                    targetW = Math.Min(targetW, maxW);
                    targetW = Math.Max(120, targetW);

                    _photoLoadMoreBanner.Width = targetW;
                }
                else
                {
                    // fallback: non esistono card (es. lista vuota)
                    _photoLoadMoreBanner.Margin = new Padding(10, 10, 10, 24);
                    _photoLoadMoreBanner.Width = Math.Max(120, Math.Min(760, avail - 20));
                }
                _photoLoadMoreBanner.Height = 76;
            }
            catch { }
        }

        private int CountGridFileCards()
        {
            int n = 0;
            foreach (Control c in _grid.Controls)
                if (c is FileCard) n++;
            return n;
        }

        private int RemoveLastFileCards(int count)
        {
            int removed = 0;
            for (int i = _grid.Controls.Count - 1; i >= 0 && removed < count; i--)
            {
                if (_grid.Controls[i] is FileCard fc)
                {
                    _grid.Controls.RemoveAt(i);
                    try { fc.Dispose(); } catch { }
                    removed++;
                }
            }
            return removed;
        }

        private void PhotoPagingAfterProgressiveTick()
        {
            if (_grid == null) return;

            if (!IsPhotoPagingContext())
            {
                RemovePhotoLoadMoreBanner();
                return;
            }

            int pageSize = GetPagingPageSize();
            if (_photoMaxVisible <= 0) _photoMaxVisible = pageSize;

            int cards = CountGridFileCards();

            // se in un singolo tick sono entrate più card del limite (batch),
            // rimuovi l'eccesso e fai rollback del cursore così verranno riaggiunte alla prossima pagina
            if (cards > _photoMaxVisible)
            {
                int excess = cards - _photoMaxVisible;
                int removed = RemoveLastFileCards(excess);
                if (removed > 0)
                {
                    _progressivePos = Math.Max(0, _progressivePos - removed);
                    cards -= removed;
                }
            }

            bool hasMore = false;
            try { hasMore = _progressivePos < _progressiveList.Count; } catch { hasMore = false; }

            if (hasMore && cards >= _photoMaxVisible)
            {
                if (_progressiveTimer.Enabled)
                    _progressiveTimer.Stop();

                EnsurePhotoLoadMoreBanner();
            }
            else if (!hasMore)
            {
                RemovePhotoLoadMoreBanner();
            }
        }


        private static bool IsCursorOnlyRemoteContainer(Control c)
        {
            if (c == null)
                return false;

            // I contenitori generici con Cursor.Hand finiscono spesso per catturare
            // tutto il canvas e producono una focus ring enorme invece del controllo reale.
            if (c is Panel || c is UserControl || c is FlowLayoutPanel || c is TableLayoutPanel)
            {
                string tn = c.GetType().Name;
                if (string.Equals(tn, "SearchBox", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }

            return false;
        }

        private static bool LooksLikeOversizedCursorOnlySurface(Control c)
        {
            try
            {
                if (c == null || c.Parent == null)
                    return false;

                if (c.TabStop || c is ButtonBase || c is CheckBox || c is ComboBox)
                    return false;

                int parentW = Math.Max(1, c.Parent.ClientSize.Width);
                int parentH = Math.Max(1, c.Parent.ClientSize.Height);
                if (parentW <= 1 || parentH <= 1 || c.Width <= 0 || c.Height <= 0)
                    return false;

                double coverage = (double)(c.Width * c.Height) / (double)(parentW * parentH);
                return coverage >= 0.65d;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRemoteFocusable(Control c)
        {
            if (c == null || c.IsDisposed) return false;
            if (!c.Visible || !c.Enabled) return false;

            // Opt-out esplicito per DPAD
            if (c.Tag is string tag && string.Equals(tag, "nodpad", StringComparison.OrdinalIgnoreCase)) return false;

            // esclusioni (come PlayerForm)
            if (c.GetType().Name == "FocusAdorner") return false;
            if (c is PictureBox) return false;
            if (c is LoadingMask) return false;
            if (c is SkinnedFlow) return false;
            if (c.GetType().Name.Contains("ThemedVScroll", StringComparison.OrdinalIgnoreCase)) return false;

            // clickable / interactable
            if (c.TabStop) return true;
            if (c is ButtonBase || c is CheckBox || c is ComboBox) return true;
            if (c.Cursor == Cursors.Hand)
                return !IsCursorOnlyRemoteContainer(c) && !LooksLikeOversizedCursorOnlySurface(c);

            return false;
        }

        private static bool IsDescendant(Control root, Control c)
        {
            var p = c;
            while (p != null)
            {
                if (ReferenceEquals(p, root)) return true;
                p = p.Parent;
            }
            return false;
        }

        private bool IsInLeftNav(Control c) => IsDescendant(_left, c);

        private static bool IsGridPrimaryCardControl(Control? c)
        {
            return c is FileCard || c is SeasonSelectorCard || c is CollectionBucketCard || c is CollectionHubTileCard || c is RemoteTile;
        }

        private static Control NormalizeRemoteTarget(Control c)
        {
            if (IsGridPrimaryCardControl(c)) return c;
            if (c is SearchBox) return c;

            var p = c.Parent;
            while (p != null)
            {
                if (IsGridPrimaryCardControl(p)) return p;
                if (p is SearchBox) return p;
                p = p.Parent;
            }

            return c;
        }

        private sealed class PlaylistBucketsStore
        {
            private sealed class PlaylistDefinition
            {
                public string Key { get; set; } = string.Empty;
                public string Name { get; set; } = string.Empty;
                public string Bucket { get; set; } = "Video";
                public List<string> Items { get; set; } = new();
            }

            private sealed class Model
            {
                public Dictionary<string, PlaylistDefinition> Playlists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
                public Dictionary<string, List<string>> Memberships { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            }

            private sealed class LegacyModel
            {
                public Dictionary<string, string> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly object _lock = new();
            private readonly string _file;
            private Model _data;

            public PlaylistBucketsStore()
            {
                string baseDir;
                try
                {
                    baseDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "CinecorePlayer2025");
                    if (string.IsNullOrWhiteSpace(baseDir))
                        baseDir = AppContext.BaseDirectory;
                }
                catch
                {
                    baseDir = AppContext.BaseDirectory;
                }

                try { Directory.CreateDirectory(baseDir); } catch { }
                _file = Path.Combine(baseDir, "playlists.json");
                _data = Load();
            }

            public bool PlaylistExists(string playlistKey)
            {
                if (string.IsNullOrWhiteSpace(playlistKey))
                    return false;

                lock (_lock)
                    return _data.Playlists.ContainsKey(playlistKey);
            }

            public List<string> All()
            {
                lock (_lock)
                {
                    return _data.Playlists.Values
                        .SelectMany(def => NormalizePathList(def?.Items))
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }

            public List<(string Key, string Name, string Bucket)> GetPlaylists(string? bucket = null)
            {
                string normalizedBucket = NormalizeBucket(bucket);
                bool filterByBucket = !string.IsNullOrWhiteSpace(bucket);

                lock (_lock)
                {
                    return _data.Playlists.Values
                        .Where(def => def != null && !string.IsNullOrWhiteSpace(def.Key))
                        .Where(def => !filterByBucket || string.Equals(def.Bucket, normalizedBucket, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(def => GetBucketOrder(def.Bucket))
                        .ThenBy(def => def.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(def => (def.Key, def.Name, def.Bucket))
                        .ToList();
                }
            }

            public int GetPlaylistItemCount(string playlistKey)
            {
                if (string.IsNullOrWhiteSpace(playlistKey))
                    return 0;

                lock (_lock)
                {
                    if (!_data.Playlists.TryGetValue(playlistKey, out var def) || def == null)
                        return 0;

                    return NormalizePathList(def.Items).Count;
                }
            }

            public string? GetPlaylistBucket(string playlistKey)
            {
                if (string.IsNullOrWhiteSpace(playlistKey))
                    return null;

                lock (_lock)
                {
                    return _data.Playlists.TryGetValue(playlistKey, out var def)
                        ? NormalizeBucket(def?.Bucket)
                        : null;
                }
            }

            public string? GetPlaylistName(string playlistKey)
            {
                if (string.IsNullOrWhiteSpace(playlistKey))
                    return null;

                lock (_lock)
                {
                    return _data.Playlists.TryGetValue(playlistKey, out var def)
                        ? NormalizePlaylistName(def?.Name)
                        : null;
                }
            }

            public string EnsurePlaylist(string name, string bucket)
            {
                lock (_lock)
                {
                    string key = EnsurePlaylistNoLock(name, bucket);
                    SaveNoLock();
                    return key;
                }
            }

            private string EnsurePlaylistNoLock(string name, string bucket)
            {
                string normalizedBucket = NormalizeBucket(bucket);
                string normalizedName = NormalizePlaylistName(name);
                if (string.IsNullOrWhiteSpace(normalizedName))
                    normalizedName = DefaultPlaylistNameForBucket(normalizedBucket);

                foreach (var def in _data.Playlists.Values)
                {
                    if (string.Equals(def.Bucket, normalizedBucket, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(def.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return def.Key;
                    }
                }

                string keyBase = BuildPlaylistKey(normalizedName, normalizedBucket);
                string key = keyBase;
                int suffix = 2;
                while (_data.Playlists.ContainsKey(key))
                {
                    key = keyBase + "-" + suffix.ToString();
                    suffix++;
                }

                _data.Playlists[key] = new PlaylistDefinition
                {
                    Key = key,
                    Name = normalizedName,
                    Bucket = normalizedBucket,
                    Items = new List<string>()
                };

                return key;
            }

            public List<string> GetPaths(string playlistKey)
            {
                if (string.IsNullOrWhiteSpace(playlistKey))
                    return new List<string>();

                lock (_lock)
                {
                    if (!_data.Playlists.TryGetValue(playlistKey, out var def) || def == null)
                        return new List<string>();

                    return NormalizePathList(def.Items);
                }
            }

            public List<string> GetPlaylistKeysForPath(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return new List<string>();

                lock (_lock)
                {
                    if (!_data.Memberships.TryGetValue(path, out var membership) || membership == null)
                        return new List<string>();

                    return membership
                        .Where(key => !string.IsNullOrWhiteSpace(key) && _data.Playlists.ContainsKey(key))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }

            public bool Contains(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                lock (_lock)
                {
                    return _data.Memberships.TryGetValue(path, out var membership) &&
                           membership != null &&
                           membership.Any(key => !string.IsNullOrWhiteSpace(key) && _data.Playlists.ContainsKey(key));
                }
            }

            public bool Contains(string path, string playlistKey)
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(playlistKey))
                    return false;

                lock (_lock)
                {
                    return _data.Memberships.TryGetValue(path, out var membership) &&
                           membership != null &&
                           membership.Contains(playlistKey, StringComparer.OrdinalIgnoreCase);
                }
            }

            public void Set(string path, string playlistKey, bool present)
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(playlistKey))
                    return;

                lock (_lock)
                {
                    if (!_data.Playlists.TryGetValue(playlistKey, out var def) || def == null)
                    {
                        string normalizedBucket = NormalizeBucket(playlistKey);
                        playlistKey = EnsurePlaylistNoLock(DefaultPlaylistNameForBucket(normalizedBucket), normalizedBucket);
                        def = _data.Playlists[playlistKey];
                    }

                    if (!_data.Memberships.TryGetValue(path, out var membership) || membership == null)
                    {
                        membership = new List<string>();
                        _data.Memberships[path] = membership;
                    }

                    def.Items = NormalizePathList(def.Items);

                    if (present)
                    {
                        if (!membership.Contains(playlistKey, StringComparer.OrdinalIgnoreCase))
                            membership.Add(playlistKey);

                        if (!def.Items.Contains(path, StringComparer.OrdinalIgnoreCase))
                            def.Items.Add(path);
                    }
                    else
                    {
                        membership.RemoveAll(key => string.Equals(key, playlistKey, StringComparison.OrdinalIgnoreCase));
                        if (membership.Count == 0)
                            _data.Memberships.Remove(path);

                        def.Items.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
                    }

                    SaveNoLock();
                }
            }

            public void Remove(string path, string playlistKey)
            {
                Set(path, playlistKey, present: false);
            }

            public void RemoveEverywhere(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                lock (_lock)
                {
                    foreach (var def in _data.Playlists.Values)
                    {
                        def.Items.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
                    }

                    _data.Memberships.Remove(path);
                    SaveNoLock();
                }
            }

            public bool DeletePlaylist(string playlistKey)
            {
                if (string.IsNullOrWhiteSpace(playlistKey))
                    return false;

                lock (_lock)
                {
                    if (!_data.Playlists.Remove(playlistKey))
                        return false;

                    var emptyPaths = new List<string>();
                    foreach (var kvp in _data.Memberships)
                    {
                        kvp.Value?.RemoveAll(key => string.Equals(key, playlistKey, StringComparison.OrdinalIgnoreCase));
                        if (kvp.Value == null || kvp.Value.Count == 0)
                            emptyPaths.Add(kvp.Key);
                    }

                    foreach (var path in emptyPaths)
                        _data.Memberships.Remove(path);

                    SaveNoLock();
                    return true;
                }
            }

            public bool CanMove(string playlistKey, string path, int delta)
            {
                if (string.IsNullOrWhiteSpace(playlistKey) || string.IsNullOrWhiteSpace(path) || delta == 0)
                    return false;

                lock (_lock)
                {
                    if (!_data.Playlists.TryGetValue(playlistKey, out var def) || def == null)
                        return false;

                    def.Items = NormalizePathList(def.Items);
                    int idx = def.Items.FindIndex(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0)
                        return false;

                    int target = idx + delta;
                    return target >= 0 && target < def.Items.Count;
                }
            }

            public bool Move(string playlistKey, string path, int delta)
            {
                if (string.IsNullOrWhiteSpace(playlistKey) || string.IsNullOrWhiteSpace(path) || delta == 0)
                    return false;

                lock (_lock)
                {
                    if (!_data.Playlists.TryGetValue(playlistKey, out var def) || def == null)
                        return false;

                    def.Items = NormalizePathList(def.Items);
                    int idx = def.Items.FindIndex(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0)
                        return false;

                    int target = idx + delta;
                    if (target < 0 || target >= def.Items.Count)
                        return false;

                    string item = def.Items[idx];
                    def.Items.RemoveAt(idx);
                    def.Items.Insert(target, item);
                    SaveNoLock();
                    return true;
                }
            }

            private static int GetBucketOrder(string? bucket)
            {
                return NormalizeBucket(bucket) switch
                {
                    "Film" => 0,
                    "Video" => 1,
                    "Foto" => 2,
                    "Musica" => 3,
                    _ => 9
                };
            }

            private static string DefaultPlaylistNameForBucket(string? bucket)
            {
                return NormalizeBucket(bucket) switch
                {
                    "Film" => "Film",
                    "Foto" => "Foto",
                    "Musica" => "Musica",
                    _ => "Video"
                };
            }

            private static string NormalizePlaylistName(string? name)
            {
                string value = Regex.Replace((name ?? string.Empty).Trim(), @"\s+", " ");
                value = value.Trim(' ', '.', '-', '_');
                if (value.Length > 80)
                    value = value.Substring(0, 80).Trim();
                return value;
            }

            private static string BuildPlaylistKey(string name, string bucket)
            {
                string normalizedName = NormalizePlaylistName(name).ToLowerInvariant();
                string slug = Regex.Replace(normalizedName, @"[^a-z0-9]+", "-").Trim('-');
                if (string.IsNullOrWhiteSpace(slug))
                    slug = "playlist";
                return NormalizeBucket(bucket).ToLowerInvariant() + "-" + slug;
            }

            private static string NormalizeBucket(string? bucket)
            {
                string value = (bucket ?? string.Empty).Trim();
                if (value.StartsWith("film", StringComparison.OrdinalIgnoreCase))
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

            private Model Load()
            {
                try
                {
                    if (!File.Exists(_file))
                        return new Model();

                    var json = File.ReadAllText(_file, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                        return new Model();

                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return new Model();

                    if (doc.RootElement.TryGetProperty("Playlists", out _) || doc.RootElement.TryGetProperty("Memberships", out _))
                    {
                        var model = JsonSerializer.Deserialize<Model>(json);
                        return NormalizeModel(model);
                    }

                    if (doc.RootElement.TryGetProperty("Items", out _))
                    {
                        var legacy = JsonSerializer.Deserialize<LegacyModel>(json);
                        return ConvertLegacyModel(legacy);
                    }
                }
                catch { }

                return new Model();
            }

            private static List<string> NormalizePathList(IEnumerable<string>? items)
            {
                return (items ?? Enumerable.Empty<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            private static Model NormalizeModel(Model? model)
            {
                var normalized = new Model
                {
                    Playlists = new Dictionary<string, PlaylistDefinition>(StringComparer.OrdinalIgnoreCase),
                    Memberships = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                };

                if (model?.Playlists != null)
                {
                    foreach (var kvp in model.Playlists)
                    {
                        string key = (kvp.Key ?? string.Empty).Trim();
                        var def = kvp.Value;
                        if (string.IsNullOrWhiteSpace(key) || def == null)
                            continue;

                        string name = NormalizePlaylistName(def.Name);
                        string bucket = NormalizeBucket(def.Bucket);
                        if (string.IsNullOrWhiteSpace(name))
                            name = DefaultPlaylistNameForBucket(bucket);

                        normalized.Playlists[key] = new PlaylistDefinition
                        {
                            Key = key,
                            Name = name,
                            Bucket = bucket,
                            Items = NormalizePathList(def.Items)
                        };
                    }
                }

                if (model?.Memberships != null)
                {
                    foreach (var kvp in model.Memberships)
                    {
                        if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                            continue;

                        var keys = kvp.Value
                            .Where(key => !string.IsNullOrWhiteSpace(key) && normalized.Playlists.ContainsKey(key))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (keys.Count > 0)
                            normalized.Memberships[kvp.Key] = keys;
                    }
                }

                foreach (var def in normalized.Playlists.Values)
                {
                    foreach (var path in def.Items)
                    {
                        if (!normalized.Memberships.TryGetValue(path, out var membership))
                        {
                            membership = new List<string>();
                            normalized.Memberships[path] = membership;
                        }

                        if (!membership.Contains(def.Key, StringComparer.OrdinalIgnoreCase))
                            membership.Add(def.Key);
                    }
                }

                foreach (var def in normalized.Playlists.Values)
                {
                    var ordered = NormalizePathList(def.Items);

                    foreach (var kvp in normalized.Memberships)
                    {
                        if (kvp.Value != null && kvp.Value.Contains(def.Key, StringComparer.OrdinalIgnoreCase) &&
                            !ordered.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            ordered.Add(kvp.Key);
                        }
                    }

                    def.Items = ordered;
                }

                return normalized;
            }

            private static Model ConvertLegacyModel(LegacyModel? legacy)
            {
                var model = new Model
                {
                    Playlists = new Dictionary<string, PlaylistDefinition>(StringComparer.OrdinalIgnoreCase),
                    Memberships = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                };

                if (legacy?.Items == null)
                    return model;

                var bucketToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in legacy.Items)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key))
                        continue;

                    string bucket = NormalizeBucket(kvp.Value);
                    if (!bucketToKey.TryGetValue(bucket, out var playlistKey))
                    {
                        string name = DefaultPlaylistNameForBucket(bucket);
                        playlistKey = BuildPlaylistKey(name, bucket);
                        model.Playlists[playlistKey] = new PlaylistDefinition
                        {
                            Key = playlistKey,
                            Name = name,
                            Bucket = bucket,
                            Items = new List<string>()
                        };
                        bucketToKey[bucket] = playlistKey;
                    }

                    model.Memberships[kvp.Key] = new List<string> { playlistKey };
                    model.Playlists[playlistKey].Items.Add(kvp.Key);
                }

                return NormalizeModel(model);
            }

            private void SaveNoLock()
            {
                string? tempFile = null;
                try
                {
                    string? dir = Path.GetDirectoryName(_file);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);

                    var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    tempFile = _file + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(tempFile, json, new UTF8Encoding(false));

                    if (File.Exists(_file))
                        File.Replace(tempFile, _file, null, true);
                    else
                        File.Move(tempFile, _file);
                }
                catch { }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(tempFile))
                    {
                        try
                        {
                            if (File.Exists(tempFile))
                                File.Delete(tempFile);
                        }
                        catch { }
                    }
                }
            }
        }
    }
}
