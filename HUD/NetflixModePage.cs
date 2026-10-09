#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    internal sealed class NetflixModeItem
    {
        public string Path { get; set; } = string.Empty;
        public string MetadataKey { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? YearText { get; set; }
        public string? QualityLabel { get; set; }
        public string? AudioLabel { get; set; }
        public string? FormatLabel { get; set; }
        public string? Overview { get; set; }
        public string? CastLine { get; set; }
        public List<MovieMetadataService.RichCastMember> CastMembers { get; set; } = new();
        public List<string> Genres { get; set; } = new();
        public string? Tagline { get; set; }
        public string? Director { get; set; }
        public double? Rating { get; set; }
        public double ResumePositionSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public string? BackdropPath { get; set; }
        public string? PosterPath { get; set; }
        public bool MetadataLoaded { get; set; }
        /// <summary>Titolo, trama e generi arrivano dal server (Jellyfin): TMDb completa solo cio' che manca.</summary>
        public bool ServerMetadata { get; set; }
    }

    /// <summary>
    /// Cinematic library surface with proportional layout and cached transitions.
    /// </summary>
    internal sealed partial class NetflixModePage : Control
    {
        private const int ImageCacheLimit = 24;
        private const double EntryDurationMilliseconds = 700d;
        private const double BrowseDurationMilliseconds = 260d;
        private static readonly HttpClient SpotlightImageHttp = CreateSpotlightImageHttp();
        private static readonly ConcurrentDictionary<string, byte> SpotlightMetadataInFlight = new(StringComparer.OrdinalIgnoreCase);

        public event Action<string>? OpenRequested;
        public event Action<string>? RestartRequested;
        public event Action<string>? QueueRequested;
        public event Action? CloseRequested;
        /// <summary>Scelta della fonte dalla pagina: "Library" o "DLNA" (le stesse voci delle Impostazioni).</summary>
        public event Action<string>? SourceChangeRequested;
        private bool _networkSource;
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool NetworkSource { get => _networkSource; set { if (_networkSource == value) return; _networkSource = value; Invalidate(); } }
        private Rectangle _sourceRect;

        private readonly List<NetflixModeItem> _items = new();
        private readonly List<int> _filtered = new();
        private readonly List<(Rectangle Bounds, int ItemIndex)> _cardHits = new();
        private readonly object _imageSync = new();
        private readonly Dictionary<string, Image> _images = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _imageOrder = new();
        private readonly Dictionary<string, long> _imageRetryAfter = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _imageRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _imageGate = new(3, 3);
        private readonly System.Windows.Forms.Timer _entryTimer;
        private readonly System.Windows.Forms.Timer _browseTimer;
        private readonly System.Windows.Forms.Timer _castPulseTimer;
        private readonly System.Windows.Forms.Timer _searchDebounceTimer;
        private readonly System.Windows.Forms.Timer _metadataDebounceTimer;
        private readonly TextBox _searchBox;
        private SpotlightFrame? _entryFrame;
        private SpotlightFrame? _browseFromFrame;
        private SpotlightFrame? _browseToFrame;
        private readonly List<MovieMetadataService.RichCastMember> _castMembers = new();
        private CancellationTokenSource? _metadataCts;
        private CancellationTokenSource? _castCts;
        private CancellationTokenSource? _networkMetadataWarmupCts;
        private int _workGeneration;
        private int _networkMetadataWarmupVersion;
        private int _networkMetadataWarmupCompleted;
        private int _networkMetadataWarmupTotal;
        private int _networkMetadataWarmupCurrent;
        private bool _networkMetadataWarmupActive;
        private int _index;
        private int _firstVisible;
        private string _query = string.Empty;
        private int _filterMode;
        private int _hoverItemIndex = -1;
        private string _hoverZone = string.Empty;
        private string _language = "it";
        private string? _emptyMessageOverride;
        private bool _backgroundPaused;
        private bool _castSheetOpen;
        private bool _castLoading;
        private int _castFirst;
        private int _castVisibleCount;
        private string _castMediaPath = string.Empty;
        private float _entryProgress = 1f;
        private long _entryStartedTimestamp;
        private long _browseStartedTimestamp;
        private float _browseProgress = 1f;
        private Rectangle _playRect;
        private Rectangle _restartRect;
        private Rectangle _queueRect;
        private Rectangle _moreRect;
        private Rectangle _closeRect;
        private Rectangle _leftRect;
        private Rectangle _rightRect;
        private Rectangle _searchRect;
        private Rectangle _filterRect;
        private Rectangle _castCloseRect;
        private Rectangle _castLeftRect;
        private Rectangle _castRightRect;
        private Rectangle _castSheetBounds;

        public NetflixModePage()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            DoubleBuffered = true;
            BackColor = Color.Black;
            TabStop = true;

            _searchBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(4, 10, 16),
                ForeColor = Color.White,
                PlaceholderText = AppLanguage.T("Cerca titolo, serie o cast", "Search title, series or cast"),
                AutoSize = false,
                // A native single-line editor supports a real cue banner and text navigation.
                Multiline = false,
                AcceptsReturn = false,
                WordWrap = false,
                TabStop = true,
                MaxLength = 120,
                AccessibleName = "Cerca nella libreria / Search library",
                Visible = false
            };
            _searchBox.TextChanged += (_, __) =>
            {
                if (string.Equals(_query, _searchBox.Text, StringComparison.Ordinal))
                    return;
                _query = _searchBox.Text;
                FinishBrowseAnimation(queueWork: false);
                _searchDebounceTimer?.Stop();
                _searchDebounceTimer?.Start();
            };
            _searchBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    if (_searchBox.TextLength > 0)
                        _searchBox.Clear();
                    else
                        CloseRequested?.Invoke();
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    OpenSelected();
                    e.SuppressKeyPress = true;
                }
            };
            Controls.Add(_searchBox);
            _searchBox.GotFocus += (_, _) => Invalidate(_searchRect);
            _searchBox.LostFocus += (_, _) => { Invalidate(_searchRect); QueueVisibleWork(); };

            _searchDebounceTimer = new System.Windows.Forms.Timer { Interval = 115 };
            _searchDebounceTimer.Tick += (_, __) =>
            {
                _searchDebounceTimer.Stop();
                RebuildFilter(keepCurrent: true);
            };
            _metadataDebounceTimer = new System.Windows.Forms.Timer { Interval = 70 };
            _metadataDebounceTimer.Tick += (_, __) =>
            {
                _metadataDebounceTimer.Stop();
                BeginLoadCurrentMetadata();
            };

            _entryTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _entryTimer.Tick += (_, __) =>
            {
                if (_entryStartedTimestamp <= 0) { Invalidate(); return; }
                // Tie the transition to real time. Frame-count progression made a 620 ms
                // reveal last several seconds whenever WinForms coalesced timer ticks.
                double elapsedMs = _entryStartedTimestamp <= 0
                    ? EntryDurationMilliseconds
                    : Stopwatch.GetElapsedTime(_entryStartedTimestamp).TotalMilliseconds;
                _entryProgress = Math.Clamp((float)(elapsedMs / EntryDurationMilliseconds), 0f, 1f);
                if (_entryProgress >= 1f)
                {
                    _entryTimer.Stop();
                    DisposeEntryFrame();
                    _searchBox.Visible = !_castSheetOpen;
                    QueueVisibleWork();
                }
                Invalidate();
            };
            _browseTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _browseTimer.Tick += (_, __) =>
            {
                double elapsed = _browseStartedTimestamp <= 0
                    ? BrowseDurationMilliseconds
                    : Stopwatch.GetElapsedTime(_browseStartedTimestamp).TotalMilliseconds;
                _browseProgress = Math.Clamp((float)(elapsed / BrowseDurationMilliseconds), 0f, 1f);
                if (_browseProgress >= 1f)
                    FinishBrowseAnimation(queueWork: true);
                Invalidate();
            };
            _castPulseTimer = new System.Windows.Forms.Timer { Interval = 90 };
            _castPulseTimer.Tick += (_, __) =>
            {
                if (_castSheetOpen && _castLoading)
                    Invalidate(_castSheetBounds);
                else
                    _castPulseTimer.Stop();
            };
        }

        public void SetItems(IEnumerable<NetflixModeItem>? items)
        {
            string? selectedIdentity = CurrentItem == null ? null : SpotlightItemIdentity(CurrentItem);
            var retained = _items.ToDictionary(SpotlightItemIdentity, StringComparer.OrdinalIgnoreCase);
            var incoming = (items ?? Enumerable.Empty<NetflixModeItem>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path))
                .GroupBy(SpotlightItemIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                // Prima quelli iniziati e da riprendere (l'ordinamento e' stabile: il resto resta com'era).
                .OrderByDescending(item => item.ResumePositionSeconds > 0)
                .ToList();
            foreach (NetflixModeItem item in incoming)
            {
                if (!retained.TryGetValue(SpotlightItemIdentity(item), out NetflixModeItem? old))
                    continue;
                item.MetadataLoaded = old.MetadataLoaded;
                item.Title = FirstNonEmpty(item.Title, old.Title);
                item.YearText = FirstNonEmpty(item.YearText, old.YearText);
                item.QualityLabel = FirstNonEmpty(item.QualityLabel, old.QualityLabel);
                item.AudioLabel = FirstNonEmpty(item.AudioLabel, old.AudioLabel);
                item.FormatLabel = FirstNonEmpty(item.FormatLabel, old.FormatLabel);
                item.Overview = item.ServerMetadata ? FirstNonEmpty(item.Overview, old.Overview) : FirstNonEmpty(old.Overview, item.Overview);
                item.CastLine = FirstNonEmpty(old.CastLine, item.CastLine);
                if (old.CastMembers.Count > 0) item.CastMembers = old.CastMembers.Select(CloneCastMember).ToList();
                if (old.Genres.Count > 0) item.Genres = old.Genres.ToList();
                item.Tagline = FirstNonEmpty(old.Tagline, item.Tagline);
                item.Director = FirstNonEmpty(old.Director, item.Director);
                item.Rating = old.Rating ?? item.Rating;
                item.BackdropPath = PreferArtwork(old.BackdropPath, item.BackdropPath);
                item.PosterPath = PreferArtwork(old.PosterPath, item.PosterPath);
            }
            CancelBackgroundWork(clearImages: false);
            _items.Clear();
            _items.AddRange(incoming);
            int selected = selectedIdentity == null ? -1 : _items.FindIndex(item => string.Equals(SpotlightItemIdentity(item), selectedIdentity, StringComparison.OrdinalIgnoreCase));
            _index = selected >= 0 ? selected : Math.Clamp(_index, 0, Math.Max(0, _items.Count - 1));
            RebuildFilter(keepCurrent: true);
            if (Visible && _entryProgress >= 1f)
                QueueVisibleWork();
            Invalidate();
        }

        private static string SpotlightItemIdentity(NetflixModeItem item)
            => string.Join("\u001f", item.Path.Trim(), item.MetadataKey.Trim(), item.Category.Trim());

        // Schermata unica di caricamento: copre la pagina finche' il catalogo di rete e la prima immagine
        // non sono pronti, invece di mostrare la pagina che si costruisce a pezzi.
        private string? _loadingText;
        private bool _loadingFinishing;
        private long _loadingDeadline;
        private readonly Stopwatch _loadingClock = Stopwatch.StartNew();
        private System.Windows.Forms.Timer? _loadingTimer;
        public bool IsLoading => _loadingText != null;

        public void SetLoading(string? text)
        {
            _loadingText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            _loadingFinishing = false;
            if (_loadingText == null) { _loadingTimer?.Stop(); Invalidate(); return; }
            _searchBox.Visible = false;
            _loadingTimer ??= CreateLoadingTimer();
            _loadingTimer.Start();
            Invalidate();
        }

        /// <summary>I titoli sono arrivati: la schermata resta finche' c'e' l'immagine del primo (al massimo due secondi e mezzo).</summary>
        public void FinishLoading()
        {
            if (_loadingText == null) return;
            _loadingFinishing = true;
            _loadingDeadline = Environment.TickCount64 + 2500;
        }

        private System.Windows.Forms.Timer CreateLoadingTimer()
        {
            var timer = new System.Windows.Forms.Timer { Interval = 33 };
            timer.Tick += (_, _) =>
            {
                if (_loadingText == null || IsDisposed) { timer.Stop(); return; }
                if (_loadingFinishing)
                {
                    NetflixModeItem? current = CurrentItem;
                    bool ready = current == null || CachedImage(current.BackdropPath) != null || CachedImage(current.PosterPath) != null;
                    if (ready || Environment.TickCount64 >= _loadingDeadline) { SetLoading(null); StartEntryAnimation(); return; }
                }
                Invalidate();
            };
            return timer;
        }

        private void DrawLoading(Graphics g)
        {
            // La casella di ricerca e' un controllo vero sopra la pagina: durante il caricamento va nascosta.
            if (_searchBox.Visible) _searchBox.Visible = false;
            g.Clear(Color.FromArgb(5, 9, 14));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Math.Max(26, D(34));
            var ring = new Rectangle((Width - d) / 2, Height / 2 - d, d, d);
            using (var back = new Pen(Color.FromArgb(36, 255, 255, 255), Math.Max(2f, 2.4f * UiScale)))
                g.DrawEllipse(back, ring);
            using (var arc = new Pen(Theme.Accent, Math.Max(2f, 2.4f * UiScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(arc, ring, (float)(_loadingClock.Elapsed.TotalMilliseconds * 0.36 % 360), 96);
            using var font = UiFont("Segoe UI", 11);
            TextRenderer.DrawText(g, _loadingText, font, new Rectangle(HudMargin, ring.Bottom + D(18), Width - 2 * HudMargin, D(30)), Color.FromArgb(178, 199, 216),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        public void SetEmptyMessage(string? message)
        {
            _emptyMessageOverride = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
            Invalidate();
        }

        public void SetLanguage(string? language)
        {
            string normalized = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
            if (string.Equals(normalized, _language, StringComparison.OrdinalIgnoreCase)) return;
            _language = normalized;
            UpdateHudLayout();
            foreach (var item in _items) item.MetadataLoaded = false;
            CancelBackgroundWork(clearImages: false);
            if (Visible && _entryProgress >= 1f)
                QueueVisibleWork();
            Invalidate();
        }

        public void SetRemoteSearchText(string? text)
        {
            string value = text ?? string.Empty;
            if (value.Length > 120) value = value[..120];
            _searchBox.Text = value;
            _searchBox.SelectionStart = _searchBox.TextLength;
            _searchBox.Visible = Visible && _entryProgress >= 1f && !_castSheetOpen && SearchUseful;
            try { _searchBox.Focus(); } catch { }
        }

        public bool IsSearchEditing => _searchBox.Focused;

        public void ApplyTheme() { BackColor = Color.Black; Invalidate(); }

        public void SetBackgroundPaused(bool paused)
        {
            if (_backgroundPaused == paused) return;
            _backgroundPaused = paused;
            if (paused) CancelBackgroundWork(clearImages: false);
            else QueueVisibleWork();
        }

        public void SuspendBackgroundWork() { _backgroundPaused = true; CancelBackgroundWork(clearImages: false); }

        public void CloseCompletely()
        {
            _backgroundPaused = true;
            CloseCastSheet();
            CancelBackgroundWork(clearImages: true);
            _items.Clear();
            _filtered.Clear();
            _cardHits.Clear();
            _query = string.Empty;
            _filterMode = 0;
            _searchBox.Text = string.Empty;
            _searchBox.Visible = false;
            _index = 0;
            _firstVisible = 0;
        }

        public void MoveNext()
        {
            if (_castSheetOpen) MoveCastSheet(1);
            else MoveSelection(1);
        }

        public void MovePrevious()
        {
            if (_castSheetOpen) MoveCastSheet(-1);
            else MoveSelection(-1);
        }

        // Senza titoli non c'e' nulla da cercare: la casella non compare.
        private bool SearchUseful => _items.Count > 0;

        public void OpenSelected()
        {
            if (_castSheetOpen)
                return;
            string? path = CurrentItem?.Path;
            if (!string.IsNullOrWhiteSpace(path)) OpenRequested?.Invoke(path);
            // Spotlight vuoto: il pulsante porta alla libreria, dove si aggiungono i titoli.
            else if (_items.Count == 0 && _loadingText == null) CloseRequested?.Invoke();
        }

        public bool TryHandleBackKey()
        {
            if (_castSheetOpen)
            {
                CloseCastSheet();
                return true;
            }
            if (string.IsNullOrEmpty(_query)) return false;
            _searchBox.Text = string.Empty;
            _searchBox.SelectionStart = _searchBox.TextLength;
            return true;
        }

        private bool UiEnglish => string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);
        private string L(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(UiEnglish ? english : italian);

        private string? NetworkMetadataWarmupLabel
        {
            get
            {
                if (!_networkMetadataWarmupActive || _networkMetadataWarmupTotal <= 0)
                    return null;
                return L(
                    $"Metadati TMDb {_networkMetadataWarmupCurrent}/{_networkMetadataWarmupTotal}",
                    $"TMDb metadata {_networkMetadataWarmupCurrent}/{_networkMetadataWarmupTotal}");
            }
        }
        private static string FirstNonEmpty(string? primary, string? fallback)
            => !string.IsNullOrWhiteSpace(primary) ? primary.Trim() : fallback?.Trim() ?? string.Empty;
        private bool FilterActive => !string.IsNullOrWhiteSpace(_query) || _filterMode != 0;
        private int FilteredCount => FilterActive ? _filtered.Count : _items.Count;
        private NetflixModeItem? CurrentItem => FilteredCount == 0 ? null : _items[Math.Clamp(_index, 0, _items.Count - 1)];

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                ResetRemoteFocus();
                _backgroundPaused = false;
                // Hidden docked controls can briefly report a 0x0 client area. Starting
                // synchronously in that state produced no entry frame on the first try.
                _entryProgress = 0f;
                _searchBox.Visible = false;
                try { Focus(); } catch { }
                // Se il controllo ha già una misura valida, l'animazione deve
                // cominciare nello stesso frame in cui Spotlight viene mostrato.
                if (ClientSize.Width > 4 && ClientSize.Height > 4)
                {
                    StartEntryAnimation();
                }
                else
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (Visible && !IsDisposed)
                                StartEntryAnimation();
                        }));
                    }
                    catch { StartEntryAnimation(); }
                }
            }
            else
            {
                _entryTimer.Stop();
                FinishBrowseAnimation(queueWork: false);
                DisposeEntryFrame();
                CloseCastSheet();
                _searchBox.Visible = false;
                CancelBackgroundWork(clearImages: false);
            }
        }

        internal void StartEntryAnimation()
        {
            FinishBrowseAnimation(queueWork: false);
            _entryProgress = 0f;
            UpdateHudLayout();
            _searchBox.Visible = false;
            _entryTimer.Stop();
            DisposeEntryFrame();

            QueueVisibleWork();
            _entryTimer.Interval = Width * (long)Height > 4_000_000 ? 31 : 15; // 15 ms: 16 rounds up to two 15.6 ms system ticks (32 fps)
            _entryStartedTimestamp = 0;
            if (_entryProgress < 1f) _entryTimer.Start();
            else _searchBox.Visible = Visible && !_castSheetOpen;
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FinishBrowseAnimation(queueWork: false);
            DisposeEntryFrame();
            UpdateHudLayout();
            KeepSelectionVisible();

            Invalidate();
        }

        private static HttpClient CreateSpotlightImageHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer2025/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd("image/jpeg, image/png;q=0.9, image/*;q=0.5");
            }
            catch { }
            return client;
        }

        private void CreateEntryFrame()
        {
            DisposeEntryFrame();
            _entryFrame = CaptureSurfaceFrame();
        }

        private SpotlightFrame? CaptureSurfaceFrame()
        {
            if (Width < 4 || Height < 4) return null;
            try { return SpotlightFrame.Capture(ClientSize, graphics => { ConfigureSurfaceGraphics(graphics, true); DrawSurface(graphics); }); }
            catch { return null; }
        }

        private void StartBrowseAnimation(SpotlightFrame? from, SpotlightFrame? to, int direction)
        {
            FinishBrowseAnimation(queueWork: false);
            if (!SystemInformation.IsMenuAnimationEnabled || from == null || to == null)
            {
                from?.Dispose(); to?.Dispose(); QueueVisibleWork(); Invalidate(); return;
            }
            _browseFromFrame = from;
            _browseToFrame = to;
            _browseProgress = 0f;
            _searchBox.Visible = false;
            // Keep a predictable cadence when the software compositor fills a 4K surface.
            _browseTimer.Interval = Width * (long)Height > 4_000_000 ? 31 : 15; // 15 ms: 16 rounds up to two 15.6 ms system ticks (32 fps)
            _browseStartedTimestamp = Stopwatch.GetTimestamp();
            _browseTimer.Start();
            Invalidate();
        }

        private void FinishBrowseAnimation(bool queueWork)
        {
            _browseTimer.Stop();
            _browseProgress = 1f;
            _browseStartedTimestamp = 0;
            try { _browseFromFrame?.Dispose(); } catch { }
            try { _browseToFrame?.Dispose(); } catch { }
            _browseFromFrame = null;
            _browseToFrame = null;
            _searchBox.Visible = Visible && _entryProgress >= 1f && !_castSheetOpen && SearchUseful;
            if (queueWork) QueueVisibleWork();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Enter or Keys.Space or
                Keys.Escape or Keys.BrowserBack or Keys.Back or Keys.Delete or Keys.Home or Keys.End || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (HandleHudCommand(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; return; }
            if (_castSheetOpen)
            {
                if (e.KeyCode is Keys.Escape or Keys.BrowserBack or Keys.Back or Keys.Down)
                    CloseCastSheet();
                else if (e.KeyCode == Keys.Left)
                    MoveCastSheet(-1);
                else if (e.KeyCode == Keys.Right)
                    MoveCastSheet(1);
                e.Handled = true;
                return;
            }

            switch (e.KeyCode)
            {
                // Le frecce scorrono i titoli: l'evidenziazione torna sulla locandina.
                case Keys.Left: _keyboardFocus = false; ClearPointerHover(); MoveSelection(-1); e.Handled = true; return;
                case Keys.Right: _keyboardFocus = false; ClearPointerHover(); MoveSelection(1); e.Handled = true; return;
                case Keys.Home: SelectFiltered(0); e.Handled = true; return;
                case Keys.End: SelectFiltered(FilteredCount - 1); e.Handled = true; return;
                case Keys.Enter:
                case Keys.Space: OpenSelected(); e.Handled = true; return;
                case Keys.Delete:
                    if (!string.IsNullOrEmpty(_query)) { _searchBox.Clear(); e.Handled = true; }
                    return;
                case Keys.Back:
                    if (!string.IsNullOrEmpty(_query)) { _searchBox.Text = _query.Length > 1 ? _query[..^1] : string.Empty; _searchBox.SelectionStart = _searchBox.TextLength; }
                    e.Handled = true;
                    return;
                case Keys.Escape:
                case Keys.BrowserBack:
                    if (!string.IsNullOrEmpty(_query)) { _searchBox.Clear(); }
                    e.Handled = true;
                    return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (_castSheetOpen) { e.Handled = true; return; }
            if (char.IsControl(e.KeyChar) || _query.Length >= 80) return;
            _searchBox.Text += e.KeyChar;
            _searchBox.SelectionStart = _searchBox.TextLength;
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_castSheetOpen)
            {
                if (e.Delta != 0) MoveCastSheet(e.Delta < 0 ? 1 : -1);
                return;
            }
            if (e.Delta != 0) MoveSelection(e.Delta < 0 ? 1 : -1);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            // Windows manda un "movimento" anche a mouse fermo, quando la finestra sotto il
            // puntatore si ridisegna: non deve togliere il fuoco al telecomando ne' riaccendere
            // l'evidenziazione del punto in cui il mouse e' rimasto.
            if (e.Location == _lastMouseLocation) return;
            _lastMouseLocation = e.Location;
            _keyboardFocus = false;
            _remoteFocus = false;
            UpdateHoverTip(e.Location);
            if (_castSheetOpen)
            {
                bool castHot = _castCloseRect.Contains(e.Location) || _castLeftRect.Contains(e.Location) || _castRightRect.Contains(e.Location);
                string castZone = _castLeftRect.Contains(e.Location) ? "cast-left" :
                    _castRightRect.Contains(e.Location) ? "cast-right" :
                    _castCloseRect.Contains(e.Location) ? "cast-close" : string.Empty;
                if (!string.Equals(_hoverZone, castZone, StringComparison.Ordinal))
                {
                    _hoverZone = castZone;
                    Invalidate();
                }
                Cursor = castHot ? Cursors.Hand : Cursors.Default;
                return;
            }
            int hoveredItem = _cardHits.FirstOrDefault(card => card.Bounds.Contains(e.Location)).ItemIndex;
            if (!_cardHits.Any(card => card.Bounds.Contains(e.Location))) hoveredItem = -1;
            string zone = _restartRect.Contains(e.Location) ? "restart" : _playRect.Contains(e.Location) ? "play"
                : _queueRect.Contains(e.Location) ? "queue"
                : _moreRect.Contains(e.Location) ? "more"
                : _closeRect.Contains(e.Location) ? "close"
                : _leftRect.Contains(e.Location) ? "left"
                : _rightRect.Contains(e.Location) ? "right"
                : _filterRect.Contains(e.Location) ? "filter"
                : _searchRect.Contains(e.Location) ? "search"
                : _sourceRect.Contains(e.Location) ? (e.X >= _sourceRect.Left + _sourceRect.Width / 2 ? "source-network" : "source-library")
                : hoveredItem >= 0 ? "card"
                : string.Empty;
            if (_hoverItemIndex != hoveredItem || !string.Equals(_hoverZone, zone, StringComparison.Ordinal))
            {
                _hoverItemIndex = hoveredItem;
                _hoverZone = zone;
                Invalidate();
            }
            bool hot = _restartRect.Contains(e.Location) || _playRect.Contains(e.Location) || _queueRect.Contains(e.Location) || _moreRect.Contains(e.Location) ||
                       _closeRect.Contains(e.Location) || _leftRect.Contains(e.Location) || _rightRect.Contains(e.Location) ||
                       _searchRect.Contains(e.Location) || _filterRect.Contains(e.Location) || _sourceRect.Contains(e.Location) ||
                       _cardHits.Any(card => card.Bounds.Contains(e.Location));
            Cursor = hot ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverItemIndex != -1 || !string.IsNullOrEmpty(_hoverZone))
            {
                _hoverItemIndex = -1;
                _hoverZone = string.Empty;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _keyboardFocus = false;
            try { Focus(); } catch { }
            if (_castSheetOpen)
            {
                if (_castCloseRect.Contains(e.Location)) CloseCastSheet();
                else if (_castLeftRect.Contains(e.Location)) MoveCastSheet(-1);
                else if (_castRightRect.Contains(e.Location)) MoveCastSheet(1);
                return;
            }
            if (_closeRect.Contains(e.Location)) { CloseRequested?.Invoke(); return; }
            if (_sourceRect.Contains(e.Location))
            {
                bool network = e.X >= _sourceRect.Left + _sourceRect.Width / 2;
                if (network != _networkSource) { NetworkSource = network; SourceChangeRequested?.Invoke(network ? "DLNA" : "Library"); }
                return;
            }
            if (_leftRect.Contains(e.Location)) { MoveSelection(-1); return; }
            if (_rightRect.Contains(e.Location)) { MoveSelection(1); return; }
            if (_restartRect.Contains(e.Location)) { if (CurrentItem != null) RestartRequested?.Invoke(CurrentItem.Path); return; }
            if (_playRect.Contains(e.Location)) { OpenSelected(); return; }
            if (_queueRect.Contains(e.Location))
            {
                QueueCurrent();
                return;
            }
            if (_moreRect.Contains(e.Location))
            {
                OpenCastSheet();
                return;
            }
            if (_filterRect.Contains(e.Location))
            {
                ShowFilterMenu();
                return;
            }
            if (_searchRect.Contains(e.Location))
            {
                FinishBrowseAnimation(queueWork: false);
                _searchBox.Visible = true;
                _searchBox.Focus();
                _searchBox.SelectionStart = _searchBox.TextLength;
                return;
            }
            foreach (var card in _cardHits)
            {
                if (!card.Bounds.Contains(e.Location)) continue;
                SelectItemIndex(card.ItemIndex);
                if (e.Clicks >= 2) OpenSelected();
                return;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            ConfigureSurfaceGraphics(g, highQualityImages: _entryProgress >= 1f);
            g.Clear(Color.Black);
            if (Width < 4 || Height < 4) return;

            if (_loadingText != null)
            {
                DrawLoading(g);
            }
            else if (_entryProgress < 1f)
            {
                DrawEntryReveal(g);
            }
            else if (_browseProgress < 1f)
            {
                DrawBrowseAccent(g);
            }
            else
            {
                DrawSurface(g);
            }
        }

        private void DrawBrowseAccent(Graphics g)
        {
            if (_browseFromFrame == null || _browseToFrame == null) { DrawSurface(g); return; }
            float opacity = _browseProgress * _browseProgress * (3 - 2 * _browseProgress);
            SpotlightFrame.Draw(g, _browseFromFrame, _browseToFrame, opacity);
        }

        private void DrawFrameOpacity(Graphics g, SpotlightFrame frame, float opacity)
            => SpotlightFrame.Draw(g, null, frame, opacity);

        private static void ConfigureSurfaceGraphics(Graphics g, bool highQualityImages)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = highQualityImages ? InterpolationMode.HighQualityBicubic : InterpolationMode.HighQualityBilinear;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        private void DrawSurface(Graphics g)
        {
            NetflixModeItem? item = CurrentItem;
            if (item == null)
            {
                DrawEmpty(g);
                return;
            }

            DrawBackdrop(g, item);
            DrawTopChrome(g);
            DrawHero(g, item);
            DrawLibrary(g);
            DrawNavigation(g);
            if (_castSheetOpen)
                DrawCastSheet(g, item);
        }

        private float UiScale => Math.Clamp(Math.Min(Width / 1330f, Height / 787f), 0.78f, 2.80f);
        // In pixel, non in punti: la pagina scala gia' con la finestra (UiScale). Con i punti Windows
        // moltiplicava ancora per la scala dello schermo (175% = testi quasi doppi, titoli tagliati).
        private Font UiFont(string family, float size, FontStyle style = FontStyle.Regular) => global::CinecorePlayer2025.AppFonts.Create(family, Math.Max(7f, size * UiScale) * (96f / 72f), style, GraphicsUnit.Pixel);

        private Bitmap? _composedBackdrop;
        private Image? _composedSource;
        private string? _composedItemIdentity;
        private int _composedPalette;

        private void DrawBackdrop(Graphics g, NetflixModeItem item)
        {
            Image? source = CachedBackdropImage(item);
            string identity = SpotlightItemIdentity(item);
            if (_composedBackdrop == null || _composedBackdrop.Size != ClientSize ||
                !ReferenceEquals(_composedSource, source) || _composedItemIdentity != identity || _composedPalette != HUD.Theme.Panel.ToArgb())
            {
                _composedBackdrop?.Dispose();
                _composedBackdrop = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                using var bg = Graphics.FromImage(_composedBackdrop);
                ConfigureSurfaceGraphics(bg, highQualityImages: true);
                bg.Clear(Color.FromArgb(3, 9, 16));
                DrawBackdropLayer(bg, item, ClientRectangle, 1);
                bg.Dispose();
                CinematicVignette.Apply(_composedBackdrop, card: false);
                _composedSource = source;
                _composedItemIdentity = identity;
                _composedPalette = HUD.Theme.Panel.ToArgb();
            }
            var compositing = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(_composedBackdrop, 0, 0);
            g.CompositingMode = compositing;
        }

        private bool DrawBackdropLayer(Graphics g, NetflixModeItem item, Rectangle bounds, float opacity)
        {
            if (opacity <= 0.001f) return false;
            Image? image = CachedBackdropImage(item);
            if (image == null) return false;
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            attrs.SetColorMatrix(new ColorMatrix { Matrix00 = 1, Matrix11 = 1, Matrix22 = 1, Matrix33 = Math.Clamp(opacity, 0f, 1f), Matrix44 = 1 });
            Rectangle src = CoverSource(image.Size, bounds.Size, 0.5f, 0.46f);
            g.DrawImage(image, bounds, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            return true;
        }

        private Image? CachedBackdropImage(NetflixModeItem item)
        {
            Image? image = CachedImage(item.BackdropPath);
            return IsLandscapeBackdrop(image) ? image : null;
        }

        private static bool IsLandscapeBackdrop(Image? image)
            => image != null && image.Width >= 640 && image.Height >= 300 &&
               image.Width / (double)Math.Max(1, image.Height) >= 1.25d;

        private readonly Dictionary<(string Identity, Size Size, int Palette), (Image? Source, Bitmap Frame)> _cardFrames = new();

        private void DrawCard(Graphics g, NetflixModeItem item, Rectangle rect, bool selected, bool hovered)
        {
            Image? source = CachedImage(item.BackdropPath) ?? CachedImage(item.PosterPath);
            var key = (SpotlightItemIdentity(item), rect.Size, HUD.Theme.Panel.ToArgb());
            if (!_cardFrames.TryGetValue(key, out var cached) || !ReferenceEquals(cached.Source, source))
            {
                if (cached.Frame != null) cached.Frame.Dispose();
                if (_cardFrames.Count >= 28)
                {
                    foreach (var entry in _cardFrames.Values) entry.Frame.Dispose();
                    _cardFrames.Clear();
                }
                var frame = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppPArgb);
                using (var cg = Graphics.FromImage(frame))
                {
                    ConfigureSurfaceGraphics(cg, highQualityImages: true);
                    DrawCardArtwork(cg, item, new Rectangle(Point.Empty, rect.Size));
                }
                CinematicVignette.Apply(frame, card: true);
                cached = (source, frame);
                _cardFrames[key] = cached;
            }
            g.DrawImageUnscaled(cached.Frame, rect.Location);
            using var path = Rounded(rect, D(6));
            if (hovered)
            {
                using var veil = new SolidBrush(Color.FromArgb(14, 255, 255, 255));
                g.FillPath(veil, path);
            }
            if (selected)
            {
                // Feedback senza il vecchio contorno/underline bianco: una luce
                // interna appena percettibile non altera la sagoma della card.
                using var selection = new SolidBrush(Color.FromArgb(24, Theme.Accent));
                g.FillPath(selection, path);
                if (!FocusOnControls)
                {
                    using var selectedBorder = new Pen(Theme.Accent, Math.Max(2, UiScale * 2));
                    g.DrawPath(selectedBorder, path);
                }
            }
            using var font = UiFont("Segoe UI", 8.9f);
            string title = string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileNameWithoutExtension(item.Path) ?? string.Empty : item.Title;
            TextRenderer.DrawText(g, title, font, new Rectangle(rect.Left + D(12), rect.Bottom - D(34), rect.Width - D(24), D(28)), Color.FromArgb(244, 248, 250), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawCardArtwork(Graphics g, NetflixModeItem item, Rectangle rect)
        {
            using var path = Rounded(rect, D(6));
            using var smooth = CinecorePlayer2025.Utilities.SmoothClip.Begin(g, path);
            var sg = smooth.Graphics;
            Image? image = CachedImage(item.BackdropPath) ?? CachedImage(item.PosterPath);
            if (image != null)
            {
                Rectangle src = CoverSource(image.Size, rect.Size, 0.5f, 0.42f);
                using var attrs = new ImageAttributes(); attrs.SetWrapMode(WrapMode.TileFlipXY);
                sg.DrawImage(image, rect, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            }
            else { using var fill = new LinearGradientBrush(rect, Color.FromArgb(30, 55, 74), Color.FromArgb(4, 14, 23), LinearGradientMode.Vertical); sg.FillRectangle(fill, rect); }
        }

        private void OpenCastSheet()
        {
            NetflixModeItem? item = CurrentItem;
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
                return;

            CloseCastSheet();
            SpotlightFrame? beforeSheet = CaptureSurfaceFrame();
            _castSheetOpen = true;
            _castLoading = true;
            _castPulseTimer.Start();
            _castFirst = 0;
            _castVisibleCount = 0;
            _castMediaPath = item.Path;
            _castMembers.Clear();
            _searchBox.Visible = false;
            Cursor = Cursors.Default;
            Invalidate();

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(16));
            _castCts = cts;

            // Cast gia' salvato su disco: il foglio si apre completo, senza attesa ne' secondo ridisegno.
            if (item.CastMembers.Count == 0)
            {
                try
                {
                    bool savedTv = item.Category.Contains("Serie", StringComparison.OrdinalIgnoreCase) || item.Category.Contains("TV", StringComparison.OrdinalIgnoreCase);
                    string savedLanguage = UiEnglish ? "en-US" : "it-IT";
                    var saved = MovieMetadataService.TryGetSavedRichMetadata(string.IsNullOrWhiteSpace(item.MetadataKey) ? item.Path : item.MetadataKey, savedLanguage, savedTv ? "tv" : "movie")
                                ?? MovieMetadataService.TryGetSavedRichMetadata(item.Path, savedLanguage, savedTv ? "tv" : "movie");
                    if (saved != null && saved.CastMembers.Count > 0)
                    {
                        item.CastMembers = saved.CastMembers.Select(CloneCastMember).ToList();
                        if (string.IsNullOrWhiteSpace(item.Director)) item.Director = saved.Director ?? string.Empty;
                        item.Rating ??= saved.Rating;
                    }
                }
                catch { }
            }

            if (item.CastMembers.Count > 0)
            {
                _castMembers.AddRange(item.CastMembers
                    .Where(person => !string.IsNullOrWhiteSpace(person.Name))
                    .Take(20)
                    .Select(CloneCastMember));
                _castLoading = false;
                _castPulseTimer.Stop();
                QueueCastSheetPhotos();
                StartBrowseAnimation(beforeSheet, CaptureSurfaceFrame(), 0);
                return;
            }

            StartBrowseAnimation(beforeSheet, CaptureSurfaceFrame(), 0);
            string path = item.Path;
            string key = string.IsNullOrWhiteSpace(item.MetadataKey) ? path : item.MetadataKey;
            string title = item.Title;
            double? duration = item.DurationSeconds > 1 ? item.DurationSeconds : null;
            string language = UiEnglish ? "en-US" : "it-IT";
            bool isTvSeries = item.Category.Contains("Serie", StringComparison.OrdinalIgnoreCase) ||
                              item.Category.Contains("TV", StringComparison.OrdinalIgnoreCase);
            string mediaType = isTvSeries ? "tv" : "movie";

            _ = Task.Run(() =>
            {
                MovieMetadataService.RichMetadata? best = ResolveRichMetadataCached(key, duration, language, mediaType, cts.Token);
                if (best.CastMembers.Count == 0 && !string.Equals(key, path, StringComparison.OrdinalIgnoreCase))
                    best = ResolveRichMetadataCached(path, duration, language, mediaType, cts.Token);
                return best;
            }, cts.Token).ContinueWith(task =>
            {
                if (IsDisposed || cts.IsCancellationRequested) return;
                MovieMetadataService.RichMetadata? rich = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || cts.IsCancellationRequested || !_castSheetOpen ||
                            !string.Equals(_castMediaPath, path, StringComparison.OrdinalIgnoreCase))
                            return;

                        _castMembers.Clear();
                        if (rich != null)
                        {
                            foreach (var person in rich.CastMembers
                                         .Where(person => !string.IsNullOrWhiteSpace(person.Name))
                                         .Take(20))
                            {
                                _castMembers.Add(new MovieMetadataService.RichCastMember
                                {
                                    Name = person.Name,
                                    Character = person.Character,
                                    ProfilePath = person.ProfilePath
                                });
                            }
                        }
                        _castLoading = false;
                        _castPulseTimer.Stop();
                        QueueCastSheetPhotos();
                        Invalidate(_castSheetBounds.IsEmpty ? ClientRectangle : _castSheetBounds);
                    }));
                }
                catch { }
            }, TaskScheduler.Default);
        }

        private void QueueCastSheetPhotos()
        {
            CancellationToken token = _castCts?.Token ?? CancellationToken.None;
            foreach (var member in _castMembers.Where(person => !string.IsNullOrWhiteSpace(person.ProfilePath)).ToList())
            {
                string path = member.ProfilePath!;
                if (File.Exists(path))
                {
                    QueueImage(path);
                    continue;
                }
                string name = member.Name;
                string character = member.Character;
                _ = Task.Run(async () =>
                {
                    bool entered = false;
                    try
                    {
                        await _imageGate.WaitAsync(token).ConfigureAwait(false);
                        entered = true;
                        return MovieMetadataService.CacheCastProfileImage(path, token);
                    }
                    catch { return null; }
                    finally { if (entered) { try { _imageGate.Release(); } catch (ObjectDisposedException) { } } }
                }, token).ContinueWith(task =>
                {
                    if (task.IsCanceled || task.IsFaulted || token.IsCancellationRequested) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (!_castSheetOpen || token.IsCancellationRequested) return;
                            var target = _castMembers.FirstOrDefault(person =>
                                string.Equals(person.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(person.Character, character, StringComparison.OrdinalIgnoreCase));
                            if (target == null) return;
                            // Se la cache robusta non riesce, lascia alla cache immagini di
                            // Spotlight un secondo tentativo diretto sull'URL TMDb.
                            target.ProfilePath = string.IsNullOrWhiteSpace(task.Result) ? path : task.Result;
                            QueueImage(target.ProfilePath);
                            Invalidate(_castSheetBounds);
                        }));
                    }
                    catch { }
                }, TaskScheduler.Default);
            }
        }

        private static MovieMetadataService.RichCastMember CloneCastMember(MovieMetadataService.RichCastMember person)
            => new() { Name = person.Name, Character = person.Character, ProfilePath = person.ProfilePath };

        private void CloseCastSheet()
        {
            SpotlightFrame? beforeSheet = _castSheetOpen && Visible && !_backgroundPaused ? CaptureSurfaceFrame() : null;
            try { _castCts?.Cancel(); _castCts?.Dispose(); } catch { }
            _castCts = null;
            _castSheetOpen = false;
            _castLoading = false;
            _castPulseTimer.Stop();
            _castFirst = 0;
            _castVisibleCount = 0;
            _castMediaPath = string.Empty;
            _castMembers.Clear();
            _searchBox.Visible = Visible && _entryProgress >= 1f && SearchUseful;
            _castCloseRect = _castLeftRect = _castRightRect = Rectangle.Empty;
            Cursor = Cursors.Default;
            if (beforeSheet != null) StartBrowseAnimation(beforeSheet, CaptureSurfaceFrame(), 0);
            else Invalidate();
        }

        private void MoveCastSheet(int delta)
        {
            if (!_castSheetOpen || delta == 0)
                return;
            int visible = Math.Max(1, _castVisibleCount);
            int maximum = Math.Max(0, _castMembers.Count - visible);
            int next = Math.Clamp(_castFirst + Math.Sign(delta), 0, maximum);
            if (next == _castFirst)
                return;
            _castFirst = next;
            Invalidate();
        }

        private void DrawCastSheet(Graphics g, NetflixModeItem item)
        {
            using (var veil = new SolidBrush(Color.FromArgb(126, 0, 3, 7)))
                g.FillRectangle(veil, ClientRectangle);

            Rectangle sheet = new(0, Height - D(340), Width, D(340));
            _castSheetBounds = sheet;
            Rectangle fadeArea = new(0, Math.Max(0, sheet.Top - D(52)), Width, D(52));
            using (var fade = new LinearGradientBrush(fadeArea, Color.FromArgb(0, 3, 9, 16), Color.FromArgb(255, 3, 9, 16), LinearGradientMode.Vertical))
                g.FillRectangle(fade, fadeArea);
            using (var fill = new SolidBrush(Color.FromArgb(3, 9, 16)))
                g.FillRectangle(fill, 0, fadeArea.Bottom, Width, Height - fadeArea.Bottom);
            VisualDither.Overlay(g, fadeArea, 3);
            int left = HudMargin;
            int top = sheet.Top + D(24);
            using var titleFont = UiFont("Segoe UI Semibold", 13f);
            using var metaFont = UiFont("Segoe UI", 8.5f);
            TextRenderer.DrawText(g, L("Cast", "Cast") + "  ·  " + item.Title, titleFont,
                new Rectangle(left, top, Width - left - D(180), D(32)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string meta = string.Join("   •   ", new[]
            {
                item.YearText,
                string.IsNullOrWhiteSpace(item.Director) ? null : L("Regia: ", "Director: ") + item.Director,
                item.Rating is > 0 ? $"TMDb {item.Rating.Value:0.0}/10" : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            TextRenderer.DrawText(g, meta, metaFont,
                new Rectangle(left, top + D(29), Width - left - D(180), D(22)), Color.FromArgb(174, 205, 217, 226),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Centrata sul bordo superiore del foglio, come la maniglia di un pannello a scomparsa.
            _castCloseRect = new Rectangle((Width - D(44)) / 2, sheet.Top - D(8), D(44), D(36));

            // Freccia verso il basso al posto della X: il foglio si chiude "spingendolo giu'"
            // (freccia giu' del telecomando o della tastiera), come e' salito.
            DrawCastSheetDismissChevron(g, _castCloseRect, _hoverZone == "cast-close" || (_keyboardFocus && _keyboardZone == "cast-close"));

            Rectangle content = new(sheet.Left + D(12), top + D(65), sheet.Width - D(24), sheet.Height - D(95));
            if (_castLoading || _castMembers.Count == 0)
            {
                using var statusFont = UiFont("Segoe UI", 11f);
                string status = _castLoading
                    ? L("Caricamento di cast e fotografie…", "Loading cast and photographs…")
                    : L("Cast non disponibile per questo titolo", "Cast is not available for this title");
                Rectangle statusRect = new(content.Left, content.Top + content.Height / 2 + D(3), content.Width, D(42));
                if (_castLoading)
                {
                    int phase = (int)((Environment.TickCount64 / 130) % 3);
                    int dot = Math.Max(5, D(7));
                    int pulseGap = D(14);
                    int total = dot * 3 + pulseGap * 2;
                    int x = content.Left + (content.Width - total) / 2;
                    int y = content.Top + content.Height / 2 - D(28);
                    for (int i = 0; i < 3; i++)
                    {
                        int alpha = i == phase ? 255 : i == (phase + 2) % 3 ? 142 : 68;
                        using var brush = new SolidBrush(Color.FromArgb(alpha, Theme.Accent));
                        g.FillEllipse(brush, x + i * (dot + pulseGap), y, dot, dot);
                    }
                }
                TextRenderer.DrawText(g, status, statusFont, statusRect, Color.FromArgb(188, 207, 219, 228),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                _castVisibleCount = 0;
                _castLeftRect = _castRightRect = Rectangle.Empty;
                return;
            }

            int arrowW = D(42);
            int viewportLeft = content.Left + arrowW + D(14);
            int viewportRight = content.Right - arrowW - D(14);
            int viewportWidth = Math.Max(1, viewportRight - viewportLeft);
            int gap = D(15);
            int preferredWidth = D(142);
            int visible = Math.Clamp((viewportWidth + gap) / Math.Max(130, preferredWidth + gap), 2, 10);
            visible = Math.Min(visible, _castMembers.Count);
            _castVisibleCount = visible;
            int maxFirst = Math.Max(0, _castMembers.Count - visible);
            _castFirst = Math.Clamp(_castFirst, 0, maxFirst);
            int cardWidth = Math.Min(D(142), (viewportWidth - gap * Math.Max(0, visible - 1)) / Math.Max(1, visible));
            int totalWidth = cardWidth * visible + gap * Math.Max(0, visible - 1);
            int cardX = viewportLeft + Math.Max(0, (viewportWidth - totalWidth) / 2);
            int portraitTop = content.Top + D(6);
            int portraitHeight = Math.Min(content.Height - D(76), (int)Math.Round(cardWidth * 1.20));

            using var nameFont = UiFont("Segoe UI Semibold", 9.2f);
            using var roleFont = UiFont("Segoe UI", 8f);
            for (int slot = 0; slot < visible; slot++)
            {
                var person = _castMembers[_castFirst + slot];
                Rectangle portrait = new(cardX + slot * (cardWidth + gap), portraitTop, cardWidth, portraitHeight);
                using Region oldClip = g.Clip.Clone();
                using (var portraitPath = Rounded(portrait, Math.Max(4, D(5))))
                    g.SetClip(portraitPath, CombineMode.Intersect);
                Image? image = CachedImage(person.ProfilePath);
                if (image != null)
                {
                    Rectangle source = CoverSource(image.Size, portrait.Size, 0.5f, 0.18f);
                    using var attrs = new ImageAttributes();
                    attrs.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(image, portrait, source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
                }
                else
                {
                    using var fallback = new LinearGradientBrush(portrait, Color.FromArgb(33, 47, 59), Color.FromArgb(7, 14, 21), LinearGradientMode.Vertical);
                    g.FillRectangle(fallback, portrait);
                    string initials = string.Concat(person.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => char.ToUpperInvariant(part[0])));
                    using var initialsFont = UiFont("Segoe UI Semibold", 20f);
                    TextRenderer.DrawText(g, initials, initialsFont, portrait, Color.FromArgb(172, 202, 216, 227),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                g.Clip = oldClip;
                using (var frame = new Pen(Color.FromArgb(42, 221, 229, 235), Math.Max(1f, UiScale)))
                using (var portraitPath = Rounded(portrait, Math.Max(4, D(5))))
                    g.DrawPath(frame, portraitPath);

                TextRenderer.DrawText(g, person.Name, nameFont,
                    new Rectangle(portrait.Left, portrait.Bottom + D(8), portrait.Width, D(23)), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(person.Character) ? L("Interprete", "Cast") : person.Character, roleFont,
                    new Rectangle(portrait.Left + D(3), portrait.Bottom + D(31), portrait.Width - D(6), D(32)), Color.FromArgb(166, 194, 209, 220),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            _castLeftRect = new Rectangle(content.Left, portraitTop + Math.Max(0, (portraitHeight - arrowW) / 2), arrowW, arrowW);
            _castRightRect = new Rectangle(content.Right - arrowW, _castLeftRect.Top, arrowW, arrowW);
            DrawCastSheetChevron(g, _castLeftRect, false, _castFirst > 0);
            DrawCastSheetChevron(g, _castRightRect, true, _castFirst < maxFirst);
        }

        private void DrawCastSheetDismissChevron(Graphics g, Rectangle rect, bool hovered)
        {
            using var pen = new Pen(Color.FromArgb(hovered ? 255 : 170, 248, 251, 253), Math.Max(1.35f, 1.48f * UiScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            int cx = rect.Left + rect.Width / 2, cy = rect.Top + rect.Height / 2, dx = Math.Max(8, D(9)), dy = Math.Max(4, D(5));
            g.DrawLines(pen, new[] { new Point(cx - dx, cy - dy), new Point(cx, cy + dy), new Point(cx + dx, cy - dy) });
        }

        private void DrawCastSheetChevron(Graphics g, Rectangle rect, bool right, bool enabled)
        {
            DrawCinecoreChevron(g, rect, right, enabled, enabled && _hoverZone == (right ? "cast-right" : "cast-left"));
        }

        private void DrawCinecoreChevron(Graphics g, Rectangle rect, bool right, bool enabled, bool hovered)
        {
            if (hovered && enabled)
            {
                int d = Math.Max(28, D(34));
                Rectangle halo = new(rect.Left + (rect.Width - d) / 2, rect.Top + (rect.Height - d) / 2, d, d);
                using var haloBrush = new SolidBrush(Color.FromArgb(76, 2, 8, 13));
                g.FillEllipse(haloBrush, halo);
            }
            using var pen = new Pen(enabled ? Color.FromArgb(hovered ? 250 : 214, 248, 251, 253) : Color.FromArgb(42, 186, 202, 214), Math.Max(1.35f, 1.48f * UiScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            int cx = rect.Left + rect.Width / 2, cy = rect.Top + rect.Height / 2, dx = Math.Max(6, D(7)), dy = Math.Max(10, D(11));
            if (right) g.DrawLines(pen, new[] { new Point(cx - dx, cy - dy), new Point(cx + dx, cy), new Point(cx - dx, cy + dy) });
            else g.DrawLines(pen, new[] { new Point(cx + dx, cy - dy), new Point(cx - dx, cy), new Point(cx + dx, cy + dy) });
        }

        private void MoveSelection(int delta)
        {
            if (FilteredCount <= 0 || delta == 0) return;
            int next = (CurrentFilteredPosition() + delta) % FilteredCount;
            if (next < 0) next += FilteredCount;
            SelectFiltered(next, Math.Sign(delta));
        }

        private void SelectFiltered(int filteredPosition, int direction = 0)
        {
            NetflixModeItem? item = ItemAtFilteredPosition(Math.Clamp(filteredPosition, 0, Math.Max(0, FilteredCount - 1)), out int itemIndex);
            if (item != null && itemIndex >= 0) SelectItemIndex(itemIndex, direction);
        }

        private void SelectItemIndex(int itemIndex, int direction = 0)
        {
            if (itemIndex < 0 || itemIndex >= _items.Count) return;
            if (itemIndex == _index) return;

            bool animate = SystemInformation.IsMenuAnimationEnabled && Visible && _entryProgress >= 1f && Width > 3 && Height > 3;
            if (_browseProgress < 1f)
                FinishBrowseAnimation(queueWork: false);
            SpotlightFrame? previousFrame = animate ? CaptureSurfaceFrame() : null;
            int oldFiltered = CurrentFilteredPosition();
            _index = itemIndex;
            KeepSelectionVisible();
            int resolvedDirection = direction != 0 ? direction : Math.Sign(CurrentFilteredPosition() - oldFiltered);
            if (animate)
            {
                StartBrowseAnimation(previousFrame, CaptureSurfaceFrame(), resolvedDirection);
            }
            else
            {
                QueueVisibleWork();
                Invalidate();
            }
        }

        private int CurrentFilteredPosition()
        {
            if (_filtered.Count == 0) return Math.Clamp(_index, 0, Math.Max(0, _items.Count - 1));
            int found = _filtered.IndexOf(_index);
            return found >= 0 ? found : 0;
        }

        private NetflixModeItem? ItemAtFilteredPosition(int position, out int itemIndex)
        {
            itemIndex = -1;
            if (position < 0 || position >= FilteredCount) return null;
            itemIndex = _filtered.Count > 0 ? _filtered[position] : position;
            return itemIndex >= 0 && itemIndex < _items.Count ? _items[itemIndex] : null;
        }

        private void RebuildFilter(bool keepCurrent)
        {
            FinishBrowseAnimation(queueWork: false);
            int previous = _index;
            _filtered.Clear();
            string needle = _query.Trim();
            if (FilterActive)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    NetflixModeItem item = _items[i];
                    bool categoryMatch = _filterMode switch
                    {
                        1 => IsMovieCategory(item.Category),
                        2 => IsTvCategory(item.Category),
                        _ => true
                    };
                    bool textMatch = string.IsNullOrWhiteSpace(needle) ||
                        Contains(item.Title, needle) ||
                        Contains(item.Category, needle) ||
                        Contains(item.CastLine, needle) ||
                        Contains(Path.GetFileNameWithoutExtension(item.Path), needle);
                    if (categoryMatch && textMatch)
                        _filtered.Add(i);
                }
            }
            if (FilteredCount <= 0) { _index = 0; _firstVisible = 0; }
            else if (keepCurrent && previous >= 0 && previous < _items.Count && (!FilterActive || _filtered.Contains(previous))) _index = previous;
            else _index = _filtered.Count > 0 ? _filtered[0] : 0;
            KeepSelectionVisible();
            QueueVisibleWork();
            Invalidate();
            static bool Contains(string? value, string n) => !string.IsNullOrWhiteSpace(value) && value.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0;
            static bool IsTvCategory(string? value) =>
                !string.IsNullOrWhiteSpace(value) &&
                (value.Contains("tv", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("serie", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("season", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("stagione", StringComparison.OrdinalIgnoreCase));
            static bool IsMovieCategory(string? value) => !IsTvCategory(value);
        }

        private int VisibleColumns()
        {
            return Math.Clamp((Width - HudMargin * 2 + D(14)) / Math.Max(210, D(224)), 2, 7);
        }

        private void KeepSelectionVisible()
        {
            int visible = Math.Max(1, Math.Min(VisibleColumns(), Math.Max(1, FilteredCount))), selected = CurrentFilteredPosition();
            if (selected < _firstVisible) _firstVisible = selected;
            else if (selected >= _firstVisible + visible) _firstVisible = selected - visible + 1;
            _firstVisible = Math.Clamp(_firstVisible, 0, Math.Max(0, FilteredCount - visible));
        }

        private void QueueVisibleWork()
        {
            if (_backgroundPaused || !Visible || IsDisposed || _items.Count == 0) return;
            // Durante la digitazione la ricerca deve restare esclusivamente locale:
            // le richieste metadati partono quando si torna a navigare i risultati.
            if (!_searchBox.Focused && !_networkMetadataWarmupActive)
            {
                _metadataDebounceTimer.Stop();
                _metadataDebounceTimer.Start();
            }
            NetflixModeItem? current = CurrentItem;
            if (current != null) { QueueImage(current.BackdropPath); QueueImage(current.PosterPath); }
            int visible = Math.Min(VisibleColumns(), FilteredCount), first = Math.Max(0, _firstVisible - 1), last = Math.Min(FilteredCount - 1, _firstVisible + visible);
            for (int i = first; i <= last; i++)
            {
                NetflixModeItem? item = ItemAtFilteredPosition(i, out _);
                if (item != null) { QueueImage(item.BackdropPath); QueueImage(item.PosterPath); }
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> SpotlightBackdropRetried = new(StringComparer.OrdinalIgnoreCase);

        private void BeginLoadCurrentMetadata()
        {
            NetflixModeItem? item = CurrentItem;
            if (_backgroundPaused || _networkMetadataWarmupActive || item == null || !Visible || !IsHandleCreated) return;
            // Dati gia' noti ma nessuno sfondo: un tentativo per sessione di trovarlo (prima restava senza per sempre).
            if (item.MetadataLoaded && (HasUsableBackdrop(item.BackdropPath) || !SpotlightBackdropRetried.TryAdd(SpotlightItemIdentity(item), 0))) return;
            int generation = Volatile.Read(ref _workGeneration);
            string path = item.Path, identity = SpotlightItemIdentity(item), key = string.IsNullOrWhiteSpace(item.MetadataKey) ? path : item.MetadataKey, language = UiEnglish ? "en-US" : "it-IT";
            bool isTvSeries = item.Category.Contains("Serie", StringComparison.OrdinalIgnoreCase) ||
                              item.Category.Contains("TV", StringComparison.OrdinalIgnoreCase);
            string mediaType = isTvSeries ? "tv" : "movie";
            string requestKey = language + "|" + identity;
            if (!SpotlightMetadataInFlight.TryAdd(requestKey, 0)) return;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(28));
            _metadataCts = cts;
            // Una ricerca TMDb individua i metadati; poster e backdrop poi usano
            // quell'ID, evitando ricerche duplicate per titolo.
            bool remoteMedia = Uri.TryCreate(path, UriKind.Absolute, out Uri? mediaUri) &&
                               (mediaUri.Scheme == Uri.UriSchemeHttp || mediaUri.Scheme == Uri.UriSchemeHttps);
            double? metadataDuration = !remoteMedia && item.DurationSeconds > 1 ? item.DurationSeconds : null;
            Task<MovieMetadataService.RichMetadata> richTask = Task.Run(
                () => ResolveRichMetadataCached(key, metadataDuration, language, mediaType, cts.Token),
                cts.Token);
            // Spotlight backgrounds require a real landscape image. Resolve the
            // TMDb backdrop independently of the poster, using the shared cache.
            Task<string?> backdropTask = HasUsableBackdrop(item.BackdropPath)
                ? Task.FromResult(item.BackdropPath)
                : Task.Run(async () =>
                {
                    MovieMetadataService.RichMetadata rich = await richTask.ConfigureAwait(false);
                    return rich.TmdbId.HasValue
                        ? MovieMetadataService.ResolveBackdropByTmdbId(path, key, rich.MediaType ?? mediaType, rich.TmdbId.Value, rich.Year, cts.Token)
                        : null;
                }, cts.Token);
            Task<string?> posterTask = HasUsableArtwork(item.PosterPath)
                ? Task.FromResult(item.PosterPath)
                : Task.Run(async () =>
                {
                    MovieMetadataService.RichMetadata rich = await richTask.ConfigureAwait(false);
                    return rich.TmdbId.HasValue
                        ? MovieMetadataService.ResolvePosterByTmdbId(path, key, rich.MediaType ?? mediaType, rich.TmdbId.Value, rich.Year, cts.Token)
                        : null;
                }, cts.Token);
            _ = ApplySpotlightArtworkWhenReady(identity, backdropTask, true, generation, cts);
            _ = ApplySpotlightArtworkWhenReady(identity, posterTask, false, generation, cts);
            _ = ApplySpotlightRichMetadataWhenReady(identity, richTask, generation, cts);
            _ = Task.WhenAll(richTask, backdropTask, posterTask).ContinueWith(completedTasks =>
            {
                SpotlightMetadataInFlight.TryRemove(requestKey, out byte ignored);
                cts.Dispose();
            }, TaskScheduler.Default);
        }

        private async Task ApplySpotlightArtworkWhenReady(
            string identity,
            Task<string?> artworkTask,
            bool isBackdrop,
            int generation,
            CancellationTokenSource cts)
        {
            try
            {
                string? artwork = await artworkTask.ConfigureAwait(false);
                if (cts.IsCancellationRequested || IsDisposed || string.IsNullOrWhiteSpace(artwork)) return;
                if (isBackdrop ? !HasUsableBackdrop(artwork) : !HasUsableArtwork(artwork)) return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || cts.IsCancellationRequested || generation != Volatile.Read(ref _workGeneration)) return;
                    NetflixModeItem? target = _items.FirstOrDefault(candidate => string.Equals(SpotlightItemIdentity(candidate), identity, StringComparison.OrdinalIgnoreCase));
                    if (target == null) return;
                    if (isBackdrop) target.BackdropPath = artwork;
                    else target.PosterPath = artwork;
                    QueueImage(target.BackdropPath);
                    QueueImage(target.PosterPath);
                    Invalidate();
                }));
            }
            catch { }
        }

        private async Task ApplySpotlightRichMetadataWhenReady(
            string identity,
            Task<MovieMetadataService.RichMetadata> richTask,
            int generation,
            CancellationTokenSource cts)
        {
            try
            {
                MovieMetadataService.RichMetadata rich = await richTask.ConfigureAwait(false);
                if (cts.IsCancellationRequested || IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || cts.IsCancellationRequested || generation != Volatile.Read(ref _workGeneration)) return;
                    NetflixModeItem? target = _items.FirstOrDefault(candidate => string.Equals(SpotlightItemIdentity(candidate), identity, StringComparison.OrdinalIgnoreCase));
                    if (target == null) return;
                    ApplyRichMetadata(target, rich);
                    Invalidate();
                }));
            }
            catch { }
        }

        private static MovieMetadataService.RichMetadata ResolveRichMetadataCached(string key, double? duration, string language, string mediaType, CancellationToken token)
        {
            // Spotlight and the library share the same TMDb resolver and persistent
            // poster index. Keep only this UI's in-flight guard; do not maintain a
            // second metadata cache here.
            return MovieMetadataService.ResolveRichMetadata(key, duration, language, token, mediaType, includeReviews: false);
        }

        public void WarmNetworkMetadataInOrder(int maxItems = 5)
        {
            CancelNetworkMetadataWarmup();
            if (_backgroundPaused || !Visible || IsDisposed || !IsHandleCreated || maxItems <= 0)
                return;

            var work = _items
                .Where(item => Uri.TryCreate(item.Path, UriKind.Absolute, out Uri? uri) &&
                               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                .Take(Math.Clamp(maxItems, 1, 12))
                .Select(item =>
                {
                    bool isTv = item.Category.Contains("Serie", StringComparison.OrdinalIgnoreCase) ||
                                item.Category.Contains("TV", StringComparison.OrdinalIgnoreCase);
                    return (Identity: SpotlightItemIdentity(item), Path: item.Path,
                        Key: string.IsNullOrWhiteSpace(item.MetadataKey) ? item.Path : item.MetadataKey,
                        MediaType: isTv ? "tv" : "movie", PosterPath: item.PosterPath, BackdropPath: item.BackdropPath);
                })
                .ToList();
            if (work.Count == 0)
                return;

            int version = Interlocked.Increment(ref _networkMetadataWarmupVersion);
            int generation = Volatile.Read(ref _workGeneration);
            string language = UiEnglish ? "en-US" : "it-IT";
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            Interlocked.Exchange(ref _networkMetadataWarmupCts, cts);
            _networkMetadataWarmupCompleted = 0;
            _networkMetadataWarmupTotal = work.Count;
            _networkMetadataWarmupCurrent = 1;
            _networkMetadataWarmupActive = true;
            _metadataDebounceTimer.Stop();
            Invalidate();
            _ = RunNetworkMetadataWarmupAsync(work, language, version, generation, cts);
        }

        private async Task RunNetworkMetadataWarmupAsync(
            List<(string Identity, string Path, string Key, string MediaType, string? PosterPath, string? BackdropPath)> work,
            string language,
            int version,
            int generation,
            CancellationTokenSource cts)
        {
            CancellationToken token = cts.Token;
            try
            {
                for (int i = 0; i < work.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var item = work[i];
                    string requestKey = language + "|" + item.Identity;
                    PostNetworkWarmupUpdate(version, generation, i, i + 1, work.Count, item.Identity, null, null, null, active: true);

                    try
                    {
                        while (!SpotlightMetadataInFlight.TryAdd(requestKey, 0))
                            await Task.Delay(100, token).ConfigureAwait(false);

                        try
                        {
                            MovieMetadataService.RichMetadata rich = await Task.Run(
                                () => ResolveRichMetadataCached(item.Key, null, language, item.MediaType, token), token)
                                .ConfigureAwait(false);
                            Task<string?> backdropTask = HasUsableBackdrop(item.BackdropPath)
                                ? Task.FromResult(item.BackdropPath)
                                : Task.Run(() => rich.TmdbId.HasValue
                                    ? MovieMetadataService.ResolveBackdropByTmdbId(item.Path, item.Key, rich.MediaType ?? item.MediaType, rich.TmdbId.Value, rich.Year, token)
                                    : null, token);
                            Task<string?> posterTask = HasUsableArtwork(item.PosterPath)
                                ? Task.FromResult(item.PosterPath)
                                : Task.Run(() => rich.TmdbId.HasValue
                                    ? MovieMetadataService.ResolvePosterByTmdbId(item.Path, item.Key, rich.MediaType ?? item.MediaType, rich.TmdbId.Value, rich.Year, token)
                                    : null, token);
                            await Task.WhenAll(backdropTask, posterTask).ConfigureAwait(false);
                            PostNetworkWarmupUpdate(version, generation, i + 1, Math.Min(i + 2, work.Count), work.Count,
                                item.Identity, rich, backdropTask.Result, posterTask.Result, active: true);
                        }
                        finally
                        {
                            SpotlightMetadataInFlight.TryRemove(requestKey, out byte ignored);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Dbg.Warn($"Spotlight TMDb warmup skipped one network item ({ex.GetType().Name}).");
                        PostNetworkWarmupUpdate(version, generation, i + 1, Math.Min(i + 2, work.Count), work.Count,
                            item.Identity, null, null, null, active: true);
                    }

                    if (i + 1 < work.Count)
                        await Task.Delay(80, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                PostNetworkWarmupUpdate(version, generation, work.Count, work.Count, work.Count,
                    null, null, null, null, active: false);
                Interlocked.CompareExchange(ref _networkMetadataWarmupCts, null, cts);
                cts.Dispose();
            }
        }

        private void PostNetworkWarmupUpdate(
            int version,
            int generation,
            int completed,
            int current,
            int total,
            string? identity,
            MovieMetadataService.RichMetadata? rich,
            string? backdrop,
            string? poster,
            bool active)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || version != Volatile.Read(ref _networkMetadataWarmupVersion) ||
                        generation != Volatile.Read(ref _workGeneration)) return;

                    if (!string.IsNullOrWhiteSpace(identity))
                    {
                        NetflixModeItem? target = _items.FirstOrDefault(candidate =>
                            string.Equals(SpotlightItemIdentity(candidate), identity, StringComparison.OrdinalIgnoreCase));
                        if (target != null)
                        {
                            if (rich != null)
                            {
                                ApplyRichMetadata(target, rich);
                            }
                            if (HasUsableBackdrop(backdrop)) target.BackdropPath = backdrop;
                            if (HasUsableArtwork(poster)) target.PosterPath = poster;
                            QueueImage(target.BackdropPath);
                            QueueImage(target.PosterPath);
                        }
                    }

                    _networkMetadataWarmupCompleted = completed;
                    _networkMetadataWarmupCurrent = Math.Clamp(current, 1, Math.Max(1, total));
                    _networkMetadataWarmupTotal = total;
                    _networkMetadataWarmupActive = active;
                    Invalidate();
                    if (!active)
                    {
                        _networkMetadataWarmupTotal = 0;
                        QueueVisibleWork();
                    }
                }));
            }
            catch { }
        }

        private void CancelNetworkMetadataWarmup()
        {
            Interlocked.Increment(ref _networkMetadataWarmupVersion);
            CancellationTokenSource? cts = Interlocked.Exchange(ref _networkMetadataWarmupCts, null);
            try { cts?.Cancel(); } catch { }
            _networkMetadataWarmupActive = false;
            _networkMetadataWarmupCompleted = 0;
            _networkMetadataWarmupTotal = 0;
            _networkMetadataWarmupCurrent = 0;
        }

        /// <summary>
        /// Metadati TMDb su una scheda. Per un titolo che il server ha gia' descritto (Jellyfin)
        /// TMDb fa da riserva: riempie i campi vuoti e aggiunge cast e regia, ma non sostituisce
        /// titolo, trama e generi scelti da chi cura quella libreria.
        /// </summary>
        private static void ApplyRichMetadata(NetflixModeItem target, MovieMetadataService.RichMetadata rich)
        {
            bool keep = target.ServerMetadata;
            if (!string.IsNullOrWhiteSpace(rich.Title) && !(keep && !string.IsNullOrWhiteSpace(target.Title))) target.Title = rich.Title!;
            if (rich.Year.HasValue && !(keep && !string.IsNullOrWhiteSpace(target.YearText))) target.YearText = rich.Year.Value.ToString();
            if (!string.IsNullOrWhiteSpace(rich.Overview) && !(keep && !string.IsNullOrWhiteSpace(target.Overview))) target.Overview = rich.Overview;
            if (rich.Cast.Count > 0) target.CastLine = string.Join(", ", rich.Cast.Take(5));
            if (rich.CastMembers.Count > 0)
                target.CastMembers = rich.CastMembers.Select(CloneCastMember).ToList();
            if (rich.Genres.Count > 0 && !(keep && target.Genres.Count > 0)) target.Genres = rich.Genres.Take(4).ToList();
            if (!(keep && !string.IsNullOrWhiteSpace(target.Tagline))) target.Tagline = rich.Tagline;
            target.Director = rich.Director;
            if (!(keep && target.Rating.HasValue)) target.Rating = rich.Rating;
            target.MetadataLoaded = true;
        }

        private static bool HasUsableArtwork(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { return File.Exists(path); } catch { return false; }
        }

        private static bool HasUsableBackdrop(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                if (!File.Exists(path)) return false;
                using var image = Image.FromFile(path);
                return image.Width >= 640 && image.Height >= 300 &&
                       image.Width / (double)Math.Max(1, image.Height) >= 1.25d;
            }
            catch { return false; }
        }

        private static string? PreferArtwork(string? existing, string? incoming)
        {
            // TMDb artwork is stored locally. Prefer it over a DLNA server URL while
            // retaining the server image as a fallback when no local artwork exists.
            try
            {
                if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing)) return existing;
                if (!string.IsNullOrWhiteSpace(incoming) && File.Exists(incoming)) return incoming;
            }
            catch { }
            return FirstNonEmpty(existing, incoming);
        }

        private Image? CachedImage(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            lock (_imageSync) if (_images.TryGetValue(path, out Image? image)) return image;
            QueueImage(path);
            return null;
        }

        private void QueueImage(string? path)
        {
            if (_backgroundPaused || string.IsNullOrWhiteSpace(path)) return;
            string imageKey = path.Trim();
            bool remote = Uri.TryCreate(imageKey, UriKind.Absolute, out Uri? remoteUri) &&
                          (remoteUri.Scheme == Uri.UriSchemeHttp || remoteUri.Scheme == Uri.UriSchemeHttps);
            string imagePath = remote ? SpotlightRemoteImageCachePath(imageKey) : imageKey;
            int generation = Volatile.Read(ref _workGeneration);
            lock (_imageSync)
            {
                if (_images.ContainsKey(imageKey) || (_imageRetryAfter.TryGetValue(imageKey, out long retry) && retry > Environment.TickCount64) || !_imageRequests.Add(imageKey)) return;
            }
            _ = Task.Run(async () =>
            {
                Image? loaded = null; bool entered = false;
                try
                {
                    await _imageGate.WaitAsync().ConfigureAwait(false);
                    entered = true;
                    if (remote && !File.Exists(imagePath))
                        await DownloadSpotlightImageAsync(imageKey, imagePath).ConfigureAwait(false);
                    if (!_backgroundPaused && generation == Volatile.Read(ref _workGeneration) && File.Exists(imagePath))
                        loaded = LoadDisplayImage(imagePath, 2300);
                }
                catch { }
                finally
                {
                    if (entered) _imageGate.Release();
                    Image? completed = loaded;
                    loaded = null;
                    try
                    {
                        if (IsDisposed || !IsHandleCreated) { completed?.Dispose(); completed = null; }
                        else
                        BeginInvoke(new Action(() =>
                        {
                            var current = CurrentItem;
                            bool affectsCurrent = current != null && (imageKey == current.BackdropPath || imageKey == current.PosterPath);
                            SpotlightFrame? beforeImage = completed != null && affectsCurrent && Visible && !_backgroundPaused && _entryProgress >= 1 && _browseProgress >= 1 && !_searchBox.Focused ? CaptureSurfaceFrame() : null;
                            bool changed = false;
                            lock (_imageSync)
                            {
                                _imageRequests.Remove(imageKey);
                                if (completed == null && generation == Volatile.Read(ref _workGeneration)) _imageRetryAfter[imageKey] = Environment.TickCount64 + 30_000;
                                if (completed != null && !_backgroundPaused && generation == Volatile.Read(ref _workGeneration) && !IsDisposed)
                                {
                                    if (_images.Remove(imageKey, out Image? replaced)) replaced.Dispose();
                                    _images[imageKey] = completed; _imageOrder.Enqueue(imageKey); completed = null;
                                    _imageRetryAfter.Remove(imageKey);
                                    changed = true;
                                    while (_imageOrder.Count > ImageCacheLimit)
                                    {
                                        string stale = _imageOrder.Dequeue();
                                        if (_images.Remove(stale, out Image? old)) old.Dispose();
                                    }
                                }
                            }
                            completed?.Dispose();
                            if (beforeImage != null) StartBrowseAnimation(beforeImage, CaptureSurfaceFrame(), 0);
                            // Una foto del cast che arriva ridisegna solo il foglio, non tutta la pagina.
                            else if (changed && _castSheetOpen && !_castSheetBounds.IsEmpty) Invalidate(_castSheetBounds);
                            else if (changed) Invalidate();
                        }));
                    }
                    catch { completed?.Dispose(); }
                }
            });
        }

        private static string SpotlightRemoteImageCachePath(string url)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
            string extension = ".jpg";
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                {
                    string candidate = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                    if (candidate is ".jpg" or ".jpeg" or ".png" or ".bmp") extension = candidate;
                }
            }
            catch { }
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "spotlight-images");
            return Path.Combine(folder, Convert.ToHexString(hash).ToLowerInvariant() + extension);
        }

        private static async Task DownloadSpotlightImageAsync(string url, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var response = await SpotlightImageHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length < 128) return;
            using (var stream = new MemoryStream(bytes, writable: false))
            using (var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true))
            {
                if (image.Width < 32 || image.Height < 32) return;
            }
            string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
        }

        private static Image? LoadDisplayImage(string path, int maxSide)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var source = Image.FromStream(stream, false, false);
                double scale = Math.Min(1.0, maxSide / (double)Math.Max(source.Width, source.Height));
                int width = Math.Max(1, (int)Math.Round(source.Width * scale)), height = Math.Max(1, (int)Math.Round(source.Height * scale));
                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(bitmap);
                g.CompositingQuality = CompositingQuality.HighSpeed; g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(source, new Rectangle(0, 0, width, height));
                return bitmap;
            }
            catch { return null; }
        }

        private void CancelBackgroundWork(bool clearImages)
        {
            _metadataDebounceTimer.Stop();
            _searchDebounceTimer.Stop();
            CancelNetworkMetadataWarmup();
            Interlocked.Increment(ref _workGeneration);
            try { _metadataCts?.Cancel(); _metadataCts?.Dispose(); } catch { }
            _metadataCts = null;
            try { _castCts?.Cancel(); _castCts?.Dispose(); } catch { }
            _castCts = null;
            lock (_imageSync)
            {
                _imageRequests.Clear();
                if (clearImages)
                {
                    foreach (Image image in _images.Values) try { image.Dispose(); } catch { }
                    _images.Clear(); _imageOrder.Clear(); _imageRetryAfter.Clear();
                    foreach (var entry in _cardFrames.Values) entry.Frame.Dispose();
                    _cardFrames.Clear();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _backgroundPaused = true;
                _entryTimer.Stop();
                _entryTimer.Dispose();
                FinishBrowseAnimation(queueWork: false);
                _browseTimer.Dispose();
                _castPulseTimer.Stop();
                _castPulseTimer.Dispose();
                _hudTip.Dispose();
                _composedBackdrop?.Dispose();
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Dispose();
                _metadataDebounceTimer.Stop();
                _metadataDebounceTimer.Dispose();
                DisposeEntryFrame();
                CancelBackgroundWork(true);
                try { _imageGate.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private void DisposeEntryFrame()
        {
            try { _entryFrame?.Dispose(); } catch { }
            _entryFrame = null;
        }

        private static Rectangle CoverSource(Size source, Size target, float focusX, float focusY)
        {
            if (source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0) return new Rectangle(Point.Empty, source);
            double sa = source.Width / (double)source.Height, ta = target.Width / (double)target.Height;
            if (sa > ta)
            {
                int width = Math.Max(1, (int)Math.Round(source.Height * ta)), maxX = Math.Max(0, source.Width - width);
                return new Rectangle(Math.Clamp((int)Math.Round(maxX * Math.Clamp(focusX, 0f, 1f)), 0, maxX), 0, width, source.Height);
            }
            int height = Math.Max(1, (int)Math.Round(source.Width / ta)), maxY = Math.Max(0, source.Height - height);
            return new Rectangle(0, Math.Clamp((int)Math.Round(maxY * Math.Clamp(focusY, 0f, 1f)), 0, maxY), source.Width, height);
        }

        private static GraphicsPath Rounded(Rectangle rectangle, int radius)
        {
            int r = Math.Max(1, Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2)), d = r * 2;
            var path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, d, d, 180, 90); path.AddArc(rectangle.Right - d, rectangle.Top, d, d, 270, 90);
            path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90); path.AddArc(rectangle.Left, rectangle.Bottom - d, d, d, 90, 90); path.CloseFigure();
            return path;
        }

    }
}
