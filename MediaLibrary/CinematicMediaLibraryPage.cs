using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

#nullable enable

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage : UserControl
    {
        protected override bool ShowFocusCues => false;
        private int _musicTransportInset;
        internal int MusicTransportInset
        {
            get => _musicTransportInset;
            // Invalidate: la sidebar (selettore del tema) e le schede aperte dipendono dall'inset.
            set { if (_musicTransportInset == value) return; _musicTransportInset = value; LayoutMusicWorkspaceContent(); LayoutQueueDrawer(); DropSheetPage(); Invalidate(); }
        }

        private sealed class BorderlessActionButton : Control, IButtonControl
        {
            protected override bool ShowFocusCues => false;
            private bool _hover;
            private bool _pressed;

            public BorderlessActionButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
                TabStop = true;
                Cursor = Cursors.Hand;
                AccessibleRole = AccessibleRole.PushButton;
            }

            public DialogResult DialogResult { get; set; }
            public string? Subtitle { get; set; }
            public string? IconKey { get; set; }
            public bool CloseGlyph { get; set; }
            /// <summary>Azione testuale senza riquadro (es. "Aggiungi cartella").</summary>
            public bool LinkStyle { get; set; }
            public Color HoverColor { get; set; }
            public Color PressedColor { get; set; }
            public void NotifyDefault(bool value) { }
            public void PerformClick() { if (CanSelect && Enabled) OnClick(EventArgs.Empty); }
            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                if (DialogResult != DialogResult.None && FindForm() is Form form)
                    form.DialogResult = DialogResult;
            }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); e.Handled = true; }
                base.OnKeyDown(e);
            }
            protected override void OnKeyUp(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space && _pressed) { _pressed = false; Invalidate(); PerformClick(); e.Handled = true; }
                base.OnKeyUp(e);
            }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { _pressed = false; Invalidate(); base.OnLostFocus(e); }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

            protected override void OnPaintBackground(PaintEventArgs pevent)
            {
                using var background = new SolidBrush(Parent?.BackColor ?? BackColor);
                pevent.Graphics.FillRectangle(background, ClientRectangle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color fill = _pressed ? PressedColor
                    : _hover ? HoverColor
                    : BackColor;
                if (!Enabled)
                    fill = Color.FromArgb(92, fill);

                if (CloseGlyph)
                {
                    // Stesso riquadro della X delle schede dettagli, coda e recensioni.
                    // Rientro di 1 px: il cerchio a filo del controllo veniva tagliato sui bordi.
                    HUD.Theme.DrawCloseButton(e.Graphics, new Rectangle(1, 1, Width - 2, Height - 2), _hover || _pressed);
                    return;
                }
                if (LinkStyle)
                {
                    Color ink = !Enabled ? HUD.Theme.Muted : _pressed ? ControlPaint.Dark(HUD.Theme.Accent, .1f) : _hover ? ControlPaint.Light(HUD.Theme.Accent, .25f) : HUD.Theme.Accent;
                    int size = 18, gap = 8;
                    int textW = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                    int x = 2;
                    if (!string.IsNullOrEmpty(IconKey))
                    {
                        AssetIconService.DrawCustom(e.Graphics, new Rectangle(x, (Height - size) / 2, size, size), IconKey, ink);
                        x += size + gap;
                    }
                    TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(x, 0, Width - x, Height), ink,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    if (_hover) using (var line = new Pen(Color.FromArgb(120, ink))) e.Graphics.DrawLine(line, x, Height / 2 + 11, x + textW, Height / 2 + 11);
                    return;
                }
                if (Subtitle != null)
                {
                    using var circle = new SolidBrush(fill); e.Graphics.FillEllipse(circle,0,0,Height,Height);
                    using var plus = new Pen(HUD.Theme.Muted,1.5f); int c=Height/2;
                    e.Graphics.DrawLine(plus,c-10,c,c+10,c); e.Graphics.DrawLine(plus,c,c-10,c,c+10);
                    using var heading = LibraryFont("Segoe UI",12f); using var caption = LibraryFont("Segoe UI",9.5f);
                    TextRenderer.DrawText(e.Graphics,Text,heading,new Rectangle(Height+16,3,Width-Height-16,26),ForeColor,MusicText);
                    TextRenderer.DrawText(e.Graphics,Subtitle,caption,new Rectangle(Height+16,29,Width-Height-16,22),HUD.Theme.Muted,MusicText);
                    return;
                }
                using var shape = Round(new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3)), Math.Max(7, (Height - 3) / 2));
                using var brush = new SolidBrush(fill);
                e.Graphics.FillPath(brush, shape);
                if (Focused)
                {
                    using var focus = new Pen(Color.FromArgb(125, HUD.Theme.Accent), 1);
                    e.Graphics.DrawPath(focus, shape);
                }
                var textRect = ClientRectangle;
                if (!string.IsNullOrEmpty(IconKey))
                {
                    int size = 18, gap = 10;
                    int group = Math.Min(Width - 16, TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + size + gap);
                    int x = (Width - group) / 2;
                    AssetIconService.DrawCustom(e.Graphics, new Rectangle(x, (Height - size) / 2, size, size), IconKey, Enabled ? ForeColor : HUD.Theme.Muted);
                    textRect = new Rectangle(x + size + gap, 0, group - size - gap, Height);
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, textRect,
                    Enabled ? ForeColor : Color.FromArgb(118, ForeColor),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

        }

        public event Action<string>? OpenRequested;
        public event Action<string, double?>? OpenWithResumeRequested;
        public event Action? CloseRequested;
        public event Action<string>? ExternalNavigationRequested;
        public event Action<string>? ExternalUrlRequested;
        public event Action<List<string>>? QueueAddRequested;
        public event Action<List<string>>? QueueRemoveRequested;
        public event Action? QueueClearRequested;
        public event Action<string>? QueuePlayPathRequested;
        public event Action<string, int>? QueueMoveRequested;
        public event Action<List<string>>? PlaylistPlayRequested;
        public event Action<bool>? ThemeModeRequested;
        internal Func<IReadOnlyList<PlaybackQueueViewItem>>? QueueSnapshotResolver;
        internal string LyricsLibraryStatus = string.Empty;
        // Lo stato cambia a ogni brano elaborato in background e si vede solo nella pagina
        // Musica: la libreria si ridisegna solo li', e solo se il testo e' davvero diverso
        // (prima si ridisegnava per intero, in qualunque pagina, a ogni notifica).
        internal void SetLyricsLibraryStatus(string status)
        {
            if (string.Equals(LyricsLibraryStatus, status, StringComparison.Ordinal)) return;
            LyricsLibraryStatus = status;
            if (Visible && _category == "Music") Invalidate();
        }
        internal event Action? MusicLibraryChanged;
        internal event Action? MusicEntered;
        internal static string[] MusicPathsForLyrics()
        {
            var index = LoadLibraryIndex();
            return index.Categories.TryGetValue("Music", out var tracks)
                ? tracks.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
        }

        private enum HitKind
        {
            None,
            Nav,
            Header,
            HeroResume,
            HeroRestart,
            HeroDetails,
            HeroMore,
            Continue,
            Poster,
            Prev,
            Next,
            SeeAll,
            LoadMore,
            GroupSelect,
            GroupPlay,
            GroupQueue,
            GroupPlaylist,
            GroupMore,
            GroupFavorite,
            GroupClose,
            PlaylistSelect,
            PlaylistPlay,
            PlaylistDelete,
            NetworkSelect,
            NetworkRefresh,
            NetworkRemove,
            NetworkConnect,
            NetworkAddJellyfin,
            NetworkFavorite,
            QueueClose,
            QueueClear,
            QueuePlay,
            QueueMoveUp,
            QueueMoveDown,
            QueueRemove,
            DetailBack,
            DetailPlay,
            DetailQueue,
            DetailPlaylist,
            DetailFavorite,
            DetailReviews,
            DetailReviewSource,
            DetailReviewsPage,
            DetailReviewsFilter,
            DetailCast,
            DetailCastPrevious,
            DetailCastNext,
            PosterPlaylist,
            MusicAlbumQueue,
            PosterFavorite,
            DiaryRate,
            DiaryClose,
            WebPaste,
            WebOpen,
            YouTubeVideo,
            YouTubeRefresh
            , MusicTab, MusicResume, MusicResumePage, MusicResumePrevious, MusicResumeNext, HomeResumePage
        }

        private enum PageView
        {
            Home,
            Collection,
            Network,
            WebInput
        }

        private sealed class NetworkServerViewItem
        {
            public string DeviceId { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string Model { get; init; } = string.Empty;
            public string Host { get; init; } = string.Empty;
            public string Location { get; init; } = string.Empty;
            public string ResourceBaseUrl { get; init; } = string.Empty;
            public string ContentDirectoryControlUrl { get; init; } = string.Empty;
            public string ContentDirectoryServiceType { get; init; } = "urn:schemas-upnp-org:service:ContentDirectory:1";
            public string Protocol { get; init; } = "DLNA / UPnP";
            public bool Available { get; init; } = true;
            public DateTime SeenAt { get; init; } = DateTime.Now;
        }

        private sealed class HitZone
        {
            public Rectangle Bounds;
            public HitKind Kind;
            public string Key = string.Empty;
            public LibraryItem? Item;
            public ResumeItem? Resume;
            public int Index = -1;
        }

        private sealed class LibraryFocusSink : Control
        {
            public LibraryFocusSink()
            {
                SetStyle(ControlStyles.Selectable, true);
                TabStop = false;
                Size = new Size(1, 1);
            }

            protected override bool IsInputKey(Keys keyData) => true;
        }

        private sealed class LibraryItem
        {
            public string Path { get; init; } = string.Empty;
            public string Title { get; init; } = string.Empty;
            public string Category { get; init; } = "Movies";
            public string PlaybackCategory { get; init; } = "Film";
            public int? Year { get; init; }
            public double? DurationMinutes { get; init; }
            public DateTime SortDateUtc { get; init; }
            public string? ArtPath { get; set; }
            public string? WideArtPath { get; set; }
            public string? Overview { get; set; }
            public List<string> Genres { get; set; } = new();
            public string? Tagline { get; set; }
            public string? Director { get; set; }
            public double? Rating { get; set; }
            public List<MovieMetadataService.RichCastMember> CastMembers { get; set; } = new();
            public List<MovieMetadataService.RichReview> Reviews { get; set; } = new();
            public int? TmdbId { get; set; }
            public string? ImdbId { get; set; }
            public string? ReviewMediaType { get; set; }
            public bool RichDetailsResolved { get; set; }
            public string? AudioLabel { get; init; }
            public string? ResolutionLabel { get; init; }
            public bool IsGroup { get; init; }
            public string GroupKind { get; init; } = string.Empty;
            public string? GroupSubtitle { get; init; }
            public List<LibraryItem> Children { get; init; } = new();
            public string? SeriesTitle { get; init; }
            public int? SeasonNumber { get; init; }
            public int? EpisodeNumber { get; init; }
            public string? AlbumTitle { get; init; }
            public string? ArtistName { get; init; }
            public string? AlbumArtist { get; init; }
            public int? TrackNumber { get; init; }
            public bool Is4K { get; init; }
            public bool IsHdr { get; init; }
            public bool HasAtmos { get; init; }
            public long Bytes { get; init; }
            public bool IsLightweight { get; init; }
        }

        private sealed class ResumeItem
        {
            public LibraryItem Item { get; init; } = new();
            public double PositionSeconds { get; init; }
            public double DurationSeconds { get; init; }
            public DateTime SavedAt { get; init; }
            public double Progress => DurationSeconds <= 0 ? 0 : Math.Max(0, Math.Min(1, PositionSeconds / DurationSeconds));
        }

        private sealed class TemporalSection
        {
            public string Title { get; init; } = string.Empty;
            public DateTime SortDateUtc { get; init; }
            public List<LibraryItem> Items { get; init; } = new();
        }

        private sealed class PlaylistViewItem
        {
            public string Key { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string Bucket { get; init; } = string.Empty;
            public string CoverPath { get; init; } = string.Empty;
            public List<LibraryItem> Items { get; init; } = new();
            public string Description { get; init; } = string.Empty;
            public string TypeLabel { get; init; } = string.Empty;
            public double DurationMinutes { get; init; }
        }

        private sealed class GroupBucket
        {
            public string Title { get; init; } = string.Empty;
            public string Subtitle { get; init; } = string.Empty;
            public string Kind { get; init; } = string.Empty;
            public List<GroupChildSort> Children { get; } = new();
        }

        private sealed class GroupChildSort
        {
            public LibraryItem Item { get; init; } = new();
            public int Number { get; init; } = int.MaxValue;
            public string Title { get; init; } = string.Empty;
        }

        private sealed class MusicApiInfo
        {
            public MusicApiInfo() { }
            public string TrackName { get; init; } = string.Empty;
            public string ArtistName { get; init; } = string.Empty;
            public string AlbumName { get; init; } = string.Empty;
            public string Genre { get; init; } = string.Empty;
            public string ReleaseDate { get; init; } = string.Empty;
            public string TrackNumber { get; init; } = string.Empty;
            public string TrackCount { get; init; } = string.Empty;
            public string DiscNumber { get; init; } = string.Empty;
            public string Duration { get; init; } = string.Empty;
            public string Country { get; init; } = string.Empty;
        }

        private sealed class VideoQualityInfo
        {
            public DateTime LastWriteUtc { get; init; }
            public long Bytes { get; init; }
            public string? Resolution { get; init; }
            public bool Is4K { get; init; }
            public bool IsHdr { get; init; }
            public string? AudioLabel { get; init; }
            public bool HasAtmos { get; init; }
            public double? DurationMinutes { get; init; }
        }

        private sealed class RootModel
        {
            public Dictionary<string, List<string>> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class IndexModel
        {
            public Dictionary<string, List<string>> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class FavoritesModel
        {
            public HashSet<string> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PlaylistDefinition
        {
            public string Key { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Bucket { get; set; } = "Video";
            public string Description { get; set; } = string.Empty;
            public string CoverPath { get; set; } = string.Empty;
            public string ArtworkPath { get; set; } = string.Empty;
            public List<string> Items { get; set; } = new();
        }

        private sealed class PlaylistModel
        {
            public Dictionary<string, PlaylistDefinition> Playlists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string>> Memberships { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class NetworkServersModel
        {
            public List<NetworkServerViewItem> Servers { get; set; } = new();
            public string ConnectedServerKey { get; set; } = string.Empty;
            public List<string> FavoriteServerKeys { get; set; } = new();
        }

        private sealed class NetworkItemMetadata
        {
            public string Title { get; init; } = string.Empty;
            public int? Year { get; init; }
            public string? SeriesTitle { get; init; }
            public int? SeasonNumber { get; init; }
            public int? EpisodeNumber { get; init; }
            public string? AlbumTitle { get; init; }
            public string? ArtistName { get; init; }
            public string? AlbumArtist { get; init; }
            public int? TrackNumber { get; init; }
        }

        private readonly TextBox _searchBox;
        private readonly TextBox _webAddressBox;
        private readonly LibraryFocusSink _focusSink;
        private bool _allowSearchFocus;
        private Panel? _playlistCreateOverlay;
        private TextBox? _playlistCreateNameBox;
        private TextBox? _playlistCreateDescriptionBox;
        private string? _playlistCreateCoverPath;
        private string _playlistCreateBucketValue = "Music";
        private bool _queueEditorVisible;
        private int _queueEditorSelectionIndex;
        private int _queueEditorScroll;
        private int _queueEditorVisibleRows;
        private int _queueEditorScrollMax;
        private int _queueDragIndex = -1;
        private int _queueDropIndex = -1;
        private string? _queueDragPath;
        private Point _queueDragStart;
        private bool _queueDragging;
        private Rectangle _queueListRect;
        private string _queueToastText = string.Empty;
        private DateTime _queueToastUntilUtc = DateTime.MinValue;
        private string _source = "Computer";
        private string _webInputKind = "YouTube";
        private string _webInputStatus = string.Empty;
        private Rectangle _webInputShellRect;
        private readonly List<NetworkServerViewItem> _networkServers = new();
        private int _networkSelectionIndex;
        private bool _networkDiscoveryInProgress;
        private bool _networkContentLoading;
        private string _networkContentStatus = string.Empty;
        private CancellationTokenSource? _networkBrowseCts;
        private int _networkBrowseVersion;
        private string _networkConnectedServerKey = string.Empty;
        private DateTime _networkLastRefresh = DateTime.MinValue;
        private volatile List<LibraryItem> _networkItems = new();
        private List<HitZone> _hits = new();
        private readonly List<LibraryItem> _items = new();
        private readonly List<ResumeItem> _resumeItems = new();
        private readonly List<(string Icon, string Text)> _statsCells = new();
        private readonly Dictionary<string, Image> _imageCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _imageCacheSync = new();
        private readonly HashSet<string> _imageLoadRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _imageLoadFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Path, int SizePx, int Argb), Bitmap> _svgIconCache = new();
        private readonly ConcurrentDictionary<string, LibraryItem> _itemCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _networkArtDownloads = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _networkArtSync = new();
        private readonly HashSet<string> _videoThumbnailRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _videoThumbnailFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _videoQualityRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _videoQualityFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _overviewRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _overviewFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _genreRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _genreFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _detailMetadataRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _detailMetadataFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _castImageRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MusicApiInfo> _musicInfoCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _musicInfoRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _musicInfoFailures = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _musicInfoActiveRequest;
        private string _musicInfoActiveKey = string.Empty;
        private readonly Dictionary<string, string> _musicQualityCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _musicQualityRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _episodeStillCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _episodeStillRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _episodeStillFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _tmdbArtworkSync = new();
        private readonly HashSet<string> _tmdbArtworkRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _tmdbArtworkRetryAfterUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 180 };
        private readonly System.Windows.Forms.Timer _heroTimer = new() { Interval = 45000 };
        private readonly System.Windows.Forms.Timer _queueToastTimer = new() { Interval = 140 };
        private readonly System.Windows.Forms.Timer _contentLoadingTimer = new() { Interval = 80 };
        private readonly Random _heroRandom = new();

        private PageView _view = PageView.Home;
        private string _category = "Movies";
        private string _filter = "All";
        private int _sortMode;
        private int _resumeOffset;
        private int _gridScroll;
        private int _gridScrollMax;
        private Rectangle _gridViewport = Rectangle.Empty;
        private Rectangle _gridScrollbarTrack = Rectangle.Empty;
        private Rectangle _gridScrollbarThumb = Rectangle.Empty;
        private Rectangle _youtubeScrollbarTrack = Rectangle.Empty;
        private Rectangle _youtubeScrollbarThumb = Rectangle.Empty;
        private Rectangle _groupScrollbarTrack = Rectangle.Empty;
        private Rectangle _groupScrollbarThumb = Rectangle.Empty;
        private Rectangle _queueScrollbarTrack = Rectangle.Empty;
        private Rectangle _queueScrollbarThumb = Rectangle.Empty;
        private bool _draggingScrollbar;
        private int _scrollbarDragOffset;
        private Rectangle _activeScrollbarTrack = Rectangle.Empty;
        private Rectangle _activeScrollbarThumb = Rectangle.Empty;
        private Action<int>? _activeScrollbarUpdate;
        private string _keyboardFocusIdentity = string.Empty;
        private bool _keyboardFocusVisible;
        private int _photoVisibleLimit = PhotoPageSize;
        private int _photoTotalAvailable;
        private static readonly object VideoQualityCacheSync = new();
        private static readonly Dictionary<string, VideoQualityInfo> VideoQualityCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim VideoQualityGate = new(1, 1);
        private LibraryItem? _activeGroup;
        private LibraryItem? _detailItem;
        private bool _diaryRatingMode;
        private LibraryItem? _diaryRatingItem;
        private bool _detailCastVisible;
        private bool _detailReviewsVisible;
        private int _detailCastFirst;
        private int _detailCastVisibleCount;
        private int _groupSelectionIndex;
        private int _groupListScroll;
        private int _groupWheelRemainder;
        private int _groupVisibleRows;
        private int _playlistSelectionIndex;
        private List<PlaylistViewItem>? _playlistViewCache;
        private long _playlistViewCacheWriteTicks = long.MinValue;
        private HashSet<string>? _favoritePathsCache;
        private long _favoritePathsCacheWriteTicks = long.MinValue;
        private Dictionary<string, double>? _watchRatingsCache;
        private string? _playlistCreatePendingItemPath;
        private LibraryItem? _heroItem;
        private string? _heroRotationKey;
        private DateTime _heroRotationUtc = DateTime.MinValue;
        private Rectangle _searchShellRect;
        private Point _lastMouse = new(-1000, -1000);
        private string _hoverSignature = string.Empty;
        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _contentRefreshCts;
        private int _contentRefreshVersion;
        private bool _contentPrepared;
        private bool _contentLoading;
        private string _contentLoadingLabel = string.Empty;
        private int _posterRefreshQueued;
        private int _imageUiRefreshQueued;
        private string _uiLanguage = "it";

        public string SelectedCategory => _category;
        public bool IsHomeView => _view == PageView.Home;
        public bool IsWebInputView => _view == PageView.WebInput;
        public string WebInputKind => _webInputKind;

        internal string RemoteCaptionFor(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            _itemCache.TryGetValue(path, out var item);
            item ??= _items.SelectMany<LibraryItem,LibraryItem>(a => a.IsGroup ? a.Children : new[]{a})
                .Concat(_networkItems)
                .FirstOrDefault(a => string.Equals(a.Path,path,StringComparison.OrdinalIgnoreCase));
            if(item == null) return "";
            return string.Join(" · ",new[]{item.Category == "Music" ? item.ArtistName : item.Director,item.Year?.ToString(CultureInfo.InvariantCulture)}.Where(x=>!string.IsNullOrWhiteSpace(x)));
        }

        internal string? RemoteArtworkFor(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            LibraryItem? item = null;
            if (!_itemCache.TryGetValue(path, out item) || item == null)
            {
                item = _items.SelectMany<LibraryItem, LibraryItem>(entry => entry.IsGroup ? entry.Children : new[] { entry })
                    .Concat(_networkItems)
                    .FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
            }

            string title = FirstNonEmpty(item?.SeriesTitle, item?.Title, PlaybackTitleHints.GetTitle(path), JellyfinClient.RememberedTitle(path)?.Title);
            string metadataKey = item != null ? ResolveMetadataLookupKey(path, title, item.Year) : title;
            string? tmdbPoster = ResolvePosterPath(path, metadataKey, title, allowNearbyArt: false);
            if (MovieMetadataService.IsUsablePosterImage(tmdbPoster))
                return tmdbPoster;

            if (item != null)
            {
                foreach (string? candidate in new[] { item.ArtPath, item.WideArtPath }
                             .Where(value => !string.IsNullOrWhiteSpace(value))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(candidate))
                        return candidate;
                    string? local = ResolveDisplayImagePath(candidate);
                    if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
                        return local;
                }
            }

            if (JellyfinClient.TryParseStream(path, out JellyfinClient.Account account, out string itemId, out _))
            {
                string imageUrl = JellyfinClient.ImageUrl(account.Address, itemId, "Primary", 600);
                string? local = ResolveDisplayImagePath(imageUrl);
                if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
                    return local;
            }
            return null;
        }

        internal void PopulateSpotlightItemFromCache(HUD.NetflixModeItem target)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.Path))
                return;

            LibraryItem? item = null;
            if (!_itemCache.TryGetValue(target.Path, out item) || item == null)
            {
                item = _items
                    .SelectMany<LibraryItem, LibraryItem>(candidate => candidate.IsGroup ? candidate.Children : new[] { candidate })
                    .FirstOrDefault(candidate => string.Equals(candidate.Path, target.Path, StringComparison.OrdinalIgnoreCase));
            }
            if (item == null)
                return;

            if (IsNetworkPath(target.Path))
            {
                string cachedKey = ResolveMetadataLookupKey(item.Path, FirstNonEmpty(item.SeriesTitle, item.Title), item.Year);
                bool titleMatches = string.Equals(cachedKey, target.MetadataKey, StringComparison.OrdinalIgnoreCase);
                bool yearConflicts = item.Year.HasValue && int.TryParse(target.YearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetYear) &&
                                     Math.Abs(item.Year.Value - targetYear) > 1;
                if (!titleMatches || yearConflicts)
                    return;
            }

            if (!string.IsNullOrWhiteSpace(item.Title)) target.Title = item.Title;
            if (item.Year.HasValue) target.YearText = item.Year.Value.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(item.Overview)) target.Overview = item.Overview;
            if (IsNetworkPath(target.Path))
            {
                string lookupKey = ResolveMetadataLookupKey(target.Path, target.Title, item.Year);
                try
                {
                    if (item.Year.HasValue)
                    {
                        target.BackdropPath = MovieMetadataService.GetCachedBackdropPath(lookupKey) ?? target.BackdropPath;
                        target.PosterPath = MovieMetadataService.GetCachedPosterPath(lookupKey) ?? target.PosterPath;
                    }
                    else
                    {
                        target.BackdropPath = ResolveBackdropPath(target.Path, lookupKey, target.Title, allowNearbyArt: false) ?? target.BackdropPath;
                        target.PosterPath = ResolvePosterPath(target.Path, lookupKey, target.Title, allowNearbyArt: false) ?? target.PosterPath;
                    }
                }
                catch { }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(item.WideArtPath)) target.BackdropPath = item.WideArtPath;
                if (!string.IsNullOrWhiteSpace(item.ArtPath)) target.PosterPath = item.ArtPath;
            }
            if (item.Genres.Count > 0) target.Genres = item.Genres.ToList();
            if (!string.IsNullOrWhiteSpace(item.Tagline)) target.Tagline = item.Tagline;
            if (!string.IsNullOrWhiteSpace(item.Director)) target.Director = item.Director;
            target.Rating ??= item.Rating;
            target.QualityLabel = QualityLabel(item);
            target.AudioLabel = item.AudioLabel;
            target.FormatLabel = VideoFormatLabel(item.Path);
            if (target.DurationSeconds <= 0 && item.DurationMinutes is > 0)
                target.DurationSeconds = item.DurationMinutes.Value * 60;
            if (item.CastMembers.Count > 0)
            {
                target.CastMembers = item.CastMembers
                    .Select(member => new MovieMetadataService.RichCastMember
                    {
                        Name = member.Name,
                        Character = member.Character,
                        ProfilePath = member.ProfilePath
                    })
                    .ToList();
                target.CastLine = string.Join(", ", target.CastMembers.Select(member => member.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Take(5));
            }

            // Una descrizione già risolta elimina il placeholder iniziale, ma lasciamo
            // comunque a Spotlight la possibilità di completare cast e recensioni.
            target.MetadataLoaded = item.RichDetailsResolved &&
                                    !string.IsNullOrWhiteSpace(target.Overview) &&
                                    target.CastMembers.Count > 0;
        }

        internal IReadOnlyList<RemoteLibraryCategoryView> GetRemoteLibrarySnapshot()
        {
            var result = new List<RemoteLibraryCategoryView>();

            RemoteLibraryItemView ToRemoteItem(LibraryItem item, string key, string kind = "item")
            {
                string subtitle = item.Year?.ToString(CultureInfo.InvariantCulture) ?? LocalizedCategoryName(key);
                if (item.DurationMinutes is > 0)
                    subtitle += " · " + FormatDuration(item.DurationMinutes.Value);

                string? artPath = FirstNonEmpty(item.ArtPath, item.WideArtPath);
                if (!string.Equals(key, "Music", StringComparison.OrdinalIgnoreCase) &&
                    !MovieMetadataService.IsUsablePosterImage(artPath))
                {
                    string metadataKey = ResolveMetadataLookupKey(item.Path, item.Title, item.Year);
                    artPath = ResolvePosterPath(item.Path, metadataKey, item.Title, allowNearbyArt: false)
                              ?? artPath;
                }
                if (!string.IsNullOrWhiteSpace(artPath) && Uri.TryCreate(artPath, UriKind.Absolute, out Uri? artUri) &&
                    (artUri.Scheme == Uri.UriSchemeHttp || artUri.Scheme == Uri.UriSchemeHttps))
                    artPath = ResolveDisplayImagePath(artPath);

                return new RemoteLibraryItemView
                {
                    Path = item.Path,
                    Title = FirstNonEmpty(item.Title, CleanTitle(Path.GetFileNameWithoutExtension(item.Path) ?? item.Path)),
                    Subtitle = subtitle,
                    ArtPath = artPath ?? string.Empty,
                    Kind = kind,
                    SeasonNumber = item.SeasonNumber,
                    EpisodeNumber = item.EpisodeNumber
                };
            }

            LibraryItem LightweightItem(string path, string key)
            {
                try
                {
                    if (_itemCache.TryGetValue(path, out LibraryItem? cached) && cached != null)
                        return cached;
                    LibraryItem? resolved = ToItem(path, allowMediaFileIo: false);
                    if (resolved != null)
                        return resolved;
                }
                catch { }

                return new LibraryItem
                {
                    Path = path,
                    Title = CleanTitle(Path.GetFileNameWithoutExtension(path) ?? path),
                    Category = key,
                    PlaybackCategory = PlaybackCategoryForDisplayCategory(key),
                    SortDateUtc = SafeSortDate(path),
                    IsLightweight = true
                };
            }

            try
            {
                var resume = PlaybackResumeStore.LoadAll()
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.MediaPath))
                    .Where(entry => RemoteMediaPathAvailable(entry.MediaPath))
                    .Where(entry => entry.DurationSeconds > 0 && entry.PositionSeconds / entry.DurationSeconds is > 0.01 and < 0.95)
                    .OrderByDescending(entry => entry.SavedAt)
                    .Select(entry =>
                    {
                        string key = IsMusicPath(entry.MediaPath) ? "Music" :
                            IsTvEpisodePath(entry.MediaPath) ? "TV Series" : "Movies";
                        string resumeKind = key == "Music" ? "resume-music" : key == "TV Series" ? "resume-series" : "resume-movie";
                        var view = ToRemoteItem(LightweightItem(entry.MediaPath, key), key, resumeKind);
                        view.Subtitle = $"{FormatDuration(entry.PositionSeconds / 60.0)} / {FormatDuration(entry.DurationSeconds / 60.0)}";
                        return view;
                    })
                    .ToList();
                if (resume.Count > 0)
                {
                    result.Add(new RemoteLibraryCategoryView
                    {
                        Key = "Resume",
                        Label = L("Riprendi", "Resume"),
                        Count = resume.Count,
                        Items = resume
                    });
                }
            }
            catch { }

            // Collegati a un server di rete (DLNA o Jellyfin) il telecomando mostra il catalogo di
            // quel server, come la libreria sullo schermo. Prima l'elenco veniva sempre dall'indice
            // dei file locali: dal telefono la libreria di rete risultava vuota o era quella sbagliata.
            List<LibraryItem> networkCatalogue = new();
            try
            {
                if (IsNetworkSourceActive() && _networkItems.Count > 0)
                    networkCatalogue = _networkItems.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path)).ToList();
            }
            catch { networkCatalogue = new List<LibraryItem>(); }

            foreach (string key in new[] { "Movies", "TV Series", "Music" })
            {
                List<LibraryItem> items = networkCatalogue.Count > 0
                    ? networkCatalogue
                        .Where(item => string.Equals(item.Category, key, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .OrderByDescending(item => item.SortDateUtc)
                        .ToList()
                    : AllPathsForCategory(key)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && PathBelongsToCategory(path, key))
                    .Where(RemoteMediaPathAvailable)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => LightweightItem(path, key))
                    .OrderByDescending(item => item.SortDateUtc)
                    .ToList();

                List<RemoteLibraryItemView> remoteItems;
                if (string.Equals(key, "TV Series", StringComparison.OrdinalIgnoreCase))
                {
                    remoteItems = items
                        .GroupBy(item => FirstNonEmpty(
                            IsNetworkPath(item.Path) ? item.SeriesTitle : InferSeriesTitleFromPath(item.Path),
                            IsNetworkPath(item.Path) ? ExtractSeriesTitleFromDisplay(item.Title) : item.SeriesTitle,
                            TryExtractMediaTitleInfo(item.Path)?.SeriesTitle,
                            IsNetworkPath(item.Path) ? InferSeriesTitleFromPath(item.Path) : TvSeriesKey(item.Path),
                            L("Serie TV", "TV Series")), StringComparer.OrdinalIgnoreCase)
                        .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                        .Select(series =>
                        {
                            var episodes = series.ToList();
                            var seriesView = new RemoteLibraryItemView
                            {
                                Path = episodes.FirstOrDefault()?.Path ?? string.Empty,
                                Title = series.Key,
                                Subtitle = L($"{episodes.Count:N0} episodi", $"{episodes.Count:N0} episodes"),
                                ArtPath = FirstGroupedPosterPath(episodes.Select(item => FirstNonEmpty(item.ArtPath, item.WideArtPath))) ?? string.Empty,
                                Kind = "series"
                            };

                            seriesView.Children = episodes
                                .GroupBy(item => IsNetworkPath(item.Path)
                                    ? item.SeasonNumber ?? TryParseSeasonNumber(item.Title) ?? TryExtractMediaTitleInfo(item.Path)?.SeasonNumber ?? 1
                                    : TryParseSeasonNumber(item.Path) ?? TryExtractMediaTitleInfo(item.Path)?.SeasonNumber ?? item.SeasonNumber ?? 1)
                                .OrderBy(group => group.Key)
                                .Select(season => new RemoteLibraryItemView
                                {
                                    Path = season.First().Path,
                                    Title = L($"Stagione {season.Key}", $"Season {season.Key}"),
                                    Subtitle = L($"{season.Count():N0} episodi", $"{season.Count():N0} episodes"),
                                    ArtPath = FirstGroupedPosterPath(season.Select(item => FirstNonEmpty(item.ArtPath, item.WideArtPath))) ?? string.Empty,
                                    Kind = "season",
                                    SeasonNumber = season.Key,
                                    Children = season
                                        .OrderBy(item => item.EpisodeNumber ?? TryExtractMediaTitleInfo(item.Path)?.EpisodeNumber ?? TryParseEpisodeNumber(item.Path) ?? int.MaxValue)
                                        .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                                        .Select(item => ToRemoteItem(item, key, "episode"))
                                        .ToList()
                                })
                                .ToList();
                            return seriesView;
                        })
                        .ToList();
                }
                else if (string.Equals(key, "Music", StringComparison.OrdinalIgnoreCase))
                {
                    remoteItems = GroupMusicAlbums(items)
                        .OrderBy(album => album.Title, StringComparer.CurrentCultureIgnoreCase)
                        .Select(album =>
                        {
                            RemoteLibraryItemView view = ToRemoteItem(album, key, "album");
                            view.Subtitle = album.GroupSubtitle ?? L($"{album.Children.Count:N0} brani", $"{album.Children.Count:N0} tracks");
                            view.Children = album.Children
                                .OrderBy(track => track.TrackNumber ?? TryParseTrackNumber(track.Path) ?? int.MaxValue)
                                .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
                                .Select(track => ToRemoteItem(track, key, "track"))
                                .ToList();
                            return view;
                        })
                        .ToList();
                }
                else
                {
                    remoteItems = items.Select(item => ToRemoteItem(item, key, "movie")).ToList();
                }

                result.Add(new RemoteLibraryCategoryView
                {
                    Key = key,
                    Label = LocalizedCategoryName(key),
                    Count = remoteItems.Count,
                    Items = remoteItems
                });
            }
            return result;
        }

        private static bool RemoteMediaPathAvailable(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (IsNetworkPath(path)) return true;
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return true;
            try { return File.Exists(path); } catch { return false; }
        }

        internal void ShowCategoryFromRemote(string? category)
        {
            string normalized = NormalizeCategoryKey(category ?? string.Empty);
            if (normalized is not ("Movies" or "TV Series" or "Videos" or "Music" or "Photos" or "Favourites" or "WatchHistory" or "Playlists"))
                normalized = "Movies";
            ShowCategoryPage(normalized, clearSearch: true);
        }

        internal void SetRemoteSearchText(string? text)
        {
            string value = (text ?? string.Empty).Length > 120 ? (text ?? string.Empty)[..120] : text ?? string.Empty;
            if (!string.Equals(_searchBox.Text, value, StringComparison.Ordinal))
                _searchBox.Text = value;
            try
            {
                _allowSearchFocus = true;
                _searchBox.Focus();
                _searchBox.SelectionStart = _searchBox.TextLength;
            }
            catch { }
            finally { _allowSearchFocus = false; }
        }

        public string ResolvePlaybackCategoryForPath(string? path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out Uri? webUri) &&
                (webUri.Scheme == Uri.UriSchemeHttp || webUri.Scheme == Uri.UriSchemeHttps))
            {
                var networkItem = _networkItems.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
                if (networkItem != null)
                    return PlaybackCategoryForDisplayCategory(networkItem.Category);
                if (IsMusicPath(webUri.AbsolutePath)) return "Musica";
                if (IsPhotoPath(webUri.AbsolutePath)) return "Foto";
                return "Video";
            }

            string category = !string.IsNullOrWhiteSpace(path) ? CategoryForPath(path!) : _category;
            return PlaybackCategoryForDisplayCategory(category);
        }

        private bool UiEnglish => string.Equals(_uiLanguage, "en", StringComparison.OrdinalIgnoreCase);
        private string L(string italian, string english) => UiEnglish ? english : italian;

        private static string AppDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CinecorePlayer2025");

        private static string RootsPath => Path.Combine(AppDataDir, "cinematicLibraryRoots.json");
        private static string IndexPath => Path.Combine(AppDataDir, "cinematicLibraryIndex.json");
        private static string FavoritesPath => Path.Combine(AppDataDir, "favorites.json");
        private static string PlaylistsPath => Path.Combine(AppDataDir, "playlists.json");
        private static string NetworkServersPath => Path.Combine(AppDataDir, "dlnaServers.json");
        private static string OverviewIndexPath => Path.Combine(AppDataDir, "cinematicOverviewIndex.json");
        private static string GenreIndexPath => Path.Combine(AppDataDir, "cinematicGenreIndex.json");

        private static readonly object DurationIndexCacheSync = new();
        private static Dictionary<string, double>? DurationIndexCache;
        private static DateTime DurationIndexWriteUtc = DateTime.MinValue;
        private static readonly object LibraryIndexCacheSync = new();
        private static IndexModel? LibraryIndexCache;
        private static DateTime LibraryIndexWriteUtc = DateTime.MinValue;
        private static readonly object OverviewIndexCacheSync = new();
        private static Dictionary<string, string>? OverviewIndexCache;
        private static DateTime OverviewIndexWriteUtc = DateTime.MinValue;
        private static readonly object GenreIndexCacheSync = new();
        private static Dictionary<string, List<string>>? GenreIndexCache;
        private static DateTime GenreIndexWriteUtc = DateTime.MinValue;
        private static readonly SemaphoreSlim VideoThumbnailGate = new(1, 1);
        private static readonly HttpClient ItunesHttp = new() { Timeout = TimeSpan.FromSeconds(6) };
        private static readonly HttpClient NetworkArtworkHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
        private const int PhotoPageSize = 100;

        private static Color Accent => HUD.Theme.Accent;
        private static Color Selection => HUD.Theme.Selection;
        private static Color BorderAccent => HUD.Theme.BorderAccent;
        private static Color Back => _darkSurfaceText ? HUD.Theme.DefaultPanel : HUD.Theme.Panel;
        private static Color NavTop => HUD.Theme.Nav;
        private static Color NavBottom => HUD.Theme.Panel;
        private static Color Panel => _darkSurfaceText ? HUD.Theme.DefaultPanel : HUD.Theme.Panel;
        private static Color Chrome => HUD.Theme.Card;
        private static Color ChromeHover => HUD.Theme.Nav;
        private static Color Border => Color.FromArgb(82, HUD.Theme.Border);
        // La scheda dettagli resta scura anche nel tema chiaro: mentre la si disegna i
        // testi usano la palette scura, altrimenti risultano scuri su scuro.
        [ThreadStatic] private static bool _darkSurfaceText;
        private static Color Muted => _darkSurfaceText ? HUD.Theme.DefaultMuted : HUD.Theme.Muted;
        private static Color TextMain => _darkSurfaceText ? HUD.Theme.DefaultText : HUD.Theme.Text;
        private static Color Subtle => _darkSurfaceText ? HUD.Theme.DefaultSubtleText : HUD.Theme.SubtleText;

        public CinematicMediaLibraryPage()
        {
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            BackColor = Back;
            TabStop = true;

            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);

            _focusSink = new LibraryFocusSink
            {
                Location = Point.Empty,
                BackColor = Back
            };
            _focusSink.KeyDown += (_, e) => OnKeyDown(e);
            Controls.Add(_focusSink);
            _focusSink.SendToBack();

            _searchBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                AutoSize = false,
                BackColor = Chrome,
                ForeColor = TextMain,
                Font = LibraryFont("Segoe UI", 11.2f),
                PlaceholderText = "Search movies, TV shows, people...",
                TabStop = false,
                Visible = false
            };
            _searchBox.TextChanged += (_, __) =>
            {
                if (IsNativeYouTubeView)
                {
                    QueueYouTubeSearch(_searchBox.Text);
                    return;
                }
                // Una ricerca scritta nella Home si svolge nella pagina a cui appartengono i
                // risultati (Film, Serie TV, Musica, Video), non in mezzo alla Home.
                // La pagina segue i risultati a ogni lettera; le altre sezioni che ne hanno restano raggiungibili.
                if (RouteGlobalSearch())
                    return;
                _gridScroll = 0;
                _photoVisibleLimit = PhotoPageSize;
                RefreshFilteredOnly();
            };
            _searchBox.MouseEnter += (_, __) => _allowSearchFocus = true;
            _searchBox.MouseLeave += (_, __) => _allowSearchFocus = false;
            _searchBox.Enter += (_, __) =>
            {
                if (IsNativeYouTubeView || _allowSearchFocus)
                    return;
                try { BeginInvoke(new Action(FocusLibrarySurface)); } catch { }
            };
            _searchBox.KeyDown += (_, e) =>
            {
                if (IsNativeYouTubeView && e.KeyCode == Keys.Enter)
                {
                    StartYouTubeSearchNow(_searchBox.Text);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode is Keys.Escape or Keys.Enter)
                {
                    FocusLibrarySurface();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            _searchBox.MouseWheel += (_, e) => ApplyMouseWheelDelta(e.Delta);
            Controls.Add(_searchBox);

            _webAddressBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Chrome,
                ForeColor = TextMain,
                Font = LibraryFont("Segoe UI", 12f),
                PlaceholderText = "https://...",
                TabStop = true,
                Visible = false
            };
            _webAddressBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    OpenWebInput();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    FocusLibrarySurface();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            Controls.Add(_webAddressBox);

            _refreshTimer.Tick += (_, __) =>
            {
                _refreshTimer.Stop();
                RefreshContent();
            };
            ConfigureNavigationAnimation();

            // In Musica l'header e' fisso (solo placeholder): niente rotazione ne' ridisegni periodici.
            _heroTimer.Tick += (_, __) => { if (_category != "Music") RotateHero(force: true); };
            _queueToastTimer.Tick += (_, __) =>
            {
                if (DateTime.UtcNow >= _queueToastUntilUtc)
                {
                    _queueToastTimer.Stop();
                    _queueToastText = string.Empty;
                }
                Invalidate();
            };
            _contentLoadingTimer.Tick += (_, __) =>
            {
                bool detailLoading = _detailItem != null &&
                    (_detailCastVisible || _detailReviewsVisible) &&
                    !_detailItem.RichDetailsResolved;
                if (_contentLoading || detailLoading || _youtubeLoading)
                    Invalidate();
                else
                    _contentLoadingTimer.Stop();
            };
            try { ItunesHttp.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer2025/1.0"); } catch { }
            try { MovieMetadataService.PostersChanged += OnPostersChanged; } catch { }
            LoadSavedNetworkServers();
            UpdateSearchPlaceholder();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _surfaceEntranceTimer?.Dispose();
                _panelEntranceTimer?.Dispose();
                _sheetTimer?.Dispose(); _sheetLayer?.Dispose(); _sheetExitLayer?.Dispose(); _sheetPage?.Dispose(); _detailBackdrop?.Dispose(); _pageFadeTimer?.Dispose(); _pageFadeFrom?.Dispose(); _pageFadeTo?.Dispose();
                _queueSlideTimer?.Dispose(); _queueSheet?.Dispose();
                _queueBackdrop?.Dispose(); _queueBackdrop = null;
                _queueGlassCache?.Dispose(); _queueGlassCache = null;
                try { MovieMetadataService.PostersChanged -= OnPostersChanged; } catch { }
                try { _scanCts?.Cancel(); } catch { }
                try { _scanCts?.Dispose(); } catch { }
                try { _contentRefreshCts?.Cancel(); } catch { }
                try { _contentRefreshCts?.Dispose(); } catch { }
                try { _networkBrowseCts?.Cancel(); } catch { }
                try { _networkBrowseCts?.Dispose(); } catch { }
                try { _refreshTimer.Stop(); } catch { }
                try { _refreshTimer.Dispose(); } catch { }
                try { _heroTimer.Stop(); } catch { }
                try { _heroTimer.Dispose(); } catch { }
                try { _queueToastTimer.Stop(); } catch { }
                try { _queueToastTimer.Dispose(); } catch { }
                try { _contentLoadingTimer.Stop(); } catch { }
                try { _contentLoadingTimer.Dispose(); } catch { }
                CancelYouTubeWork();
                try { _gridScrollAnimationTimer.Stop(); } catch { }
                try { _gridScrollAnimationTimer.Dispose(); } catch { }
                List<Image> cachedImages;
                lock (_imageCacheSync)
                {
                    cachedImages = _imageCache.Values.ToList();
                    _imageCache.Clear();
                }
                foreach (var img in cachedImages)
                {
                    try { img.Dispose(); } catch { }
                }
                foreach (var img in _svgIconCache.Values.ToList())
                {
                    try { img.Dispose(); } catch { }
                }
                _svgIconCache.Clear();
                ClearRenderedCaches();
            }
            base.Dispose(disposing);
        }

        public void SetLanguage(string? language)
        {
            string next = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
            bool changed = next != _uiLanguage;
            _uiLanguage = next;
            UpdateSearchPlaceholder();
            if (changed && IsHandleCreated)
            {
                // Didascalie delle card e statistiche vengono generate al caricamento:
                // al cambio di lingua vanno rigenerate, non solo ridisegnate.
                foreach (var card in _posterCards.Values) card.Bitmap.Dispose();
                _posterCards.Clear(); _posterCardBytes = 0; foreach (var card in _provisionalCards.Values) card.Bitmap.Dispose(); _provisionalCards.Clear();
                _posterCardOrder.Clear();
                _contentSnapshots.Clear();
                try { RefreshContent(); } catch { }
            }
            Invalidate();
        }

        public void ApplyTheme()
        {
            BackColor = Back;
            try
            {
                _searchBox.BackColor = Chrome;
                _searchBox.ForeColor = TextMain;
            }
            catch { }
            Invalidate();
        }

        private void OnPostersChanged()
        {
            try
            {
                if (IsDisposed || Disposing)
                    return;
                if (Interlocked.Exchange(ref _posterRefreshQueued, 1) != 0)
                    return;

                _ = Task.Run(async () =>
                {
                    await Task.Delay(120).ConfigureAwait(false);
                    try
                    {
                        if (IsDisposed || !IsHandleCreated)
                        {
                            Interlocked.Exchange(ref _posterRefreshQueued, 0);
                            return;
                        }
                        BeginInvoke(new Action(() =>
                        {
                            Interlocked.Exchange(ref _posterRefreshQueued, 0);
                            RefreshVisibleArtworkFromCache();
                        }));
                    }
                    catch { Interlocked.Exchange(ref _posterRefreshQueued, 0); }
                });
            }
            catch { Interlocked.Exchange(ref _posterRefreshQueued, 0); }
        }

        public void ShowQueueAddedFeedback(IReadOnlyList<string>? paths)
        {
            int count = paths?.Count ?? 0;
            string detail = string.Empty;
            if (count == 1 && !string.IsNullOrWhiteSpace(paths![0]))
            {
                string path = paths[0];
                detail = FirstNonEmpty(ToItem(path)?.Title, Path.GetFileNameWithoutExtension(path), path);
            }
            else if (count > 1)
            {
                detail = L($"{count:N0} elementi", $"{count:N0} items");
            }

            _queueToastText = string.IsNullOrWhiteSpace(detail)
                ? L("Aggiunto alla coda", "Added to queue")
                : L("Aggiunto alla coda: ", "Added to queue: ") + detail;
            _queueToastUntilUtc = DateTime.UtcNow.AddSeconds(2.4);
            _queueToastTimer.Stop();
            _queueToastTimer.Start();
            Invalidate();
        }

        public void EnsureInitialContentPrepared()
        {
            if (_contentPrepared)
                return;

            Form? owner = FindForm();
            if (owner != null && !owner.Visible)
            {
                PrepareInitialContentForFirstFrame();
                return;
            }

            _contentPrepared = true;
            RefreshContent();
        }

        public Control GetRemoteFocusRoot() => this;
        public bool IsRemoteNavigationReady => true;
        public bool IsRemoteContentFocusCandidate(Control? c) => c != null && (ReferenceEquals(c, this) || IsDescendant(this, c));
        public Control? GetRemoteDefaultFocusTarget() => _focusSink;
        public Control? CoerceRemoteFocus(Control? current) => _focusSink;
        public void SyncRemoteZoneFromExternalFocus(Control? current) { }
        public void SetDpadInputIsRemote(bool isRemote) { }
        public void SuspendInitialPointerHoverUntilMouseMove() => SuppressPointerHoverUntilMouseMove();

        // Navigando da tastiera la card sotto il cursore fermo non deve restare evidenziata:
        // l'hover riparte solo quando il mouse si sposta davvero sullo schermo.
        private bool _pointerHoverSuppressed;
        private Point _pointerHoverSuppressedAt;

        private void SuppressPointerHoverUntilMouseMove()
        {
            _pointerHoverSuppressed = true;
            _pointerHoverSuppressedAt = Control.MousePosition;
            if (_lastMouse.X <= -1000 && _hoverBounds.IsEmpty) return;
            Rectangle previous = _hoverBounds;
            _lastMouse = new Point(-1000, -1000);
            _hoverSignature = string.Empty;
            _hoverBounds = Rectangle.Empty;
            InvalidateHoverTransition(previous, Rectangle.Empty);
            if (!previous.IsEmpty) Invalidate();
        }
        public bool IsSearchEditor(Control? c) => ReferenceEquals(c, _searchBox);

        internal void FocusLibrarySurface()
        {
            try
            {
                ActiveControl = _focusSink;
                _focusSink.Focus();
            }
            catch { }
        }

        public bool TryRemoteMove(Control? current, string dir, out Control? next)
        {
            next = _focusSink;
            string d = (dir ?? string.Empty).ToLowerInvariant();
            SuppressPointerHoverUntilMouseMove();
            if (TryMoveKeyboardFocus(d))
                return true;

            if (_activeGroup != null && (d == "up" || d == "down"))
            {
                MoveGroupSelection(d == "down" ? 1 : -1);
            }
            else if (_view == PageView.Collection && (d == "up" || d == "down"))
            {
                int step = Math.Max(72, Height / 5);
                int scroll = d == "down" ? step : -step;
                int target = Math.Max(0, Math.Min(_gridScrollMax, _gridScroll + scroll));
                if (target != _gridScroll)
                {
                    _gridScroll = target;
                    Invalidate();
                }
            }
            else if (d == "left" && _resumeOffset > 0)
            {
                _resumeOffset--;
                Invalidate();
            }
            else if (d == "right" && _resumeOffset < Math.Max(0, _resumeItems.Count - 1))
            {
                _resumeOffset++;
                Invalidate();
            }
            return true;
        }

        public bool TryRemoteBack(Control? current, out Control? next)
        {
            next = _focusSink;
            if (_detailItem != null)
            {
                CloseDetailOverlay();
                return true;
            }
            if (_activeGroup != null)
            {
                CloseGroupPicker();
                return true;
            }
            return false;
        }

        public bool TryRemoteOk(Control? current, out Control? next)
        {
            next = _focusSink;
            HitZone? focusedHit = FindKeyboardFocusedHit();
            if (focusedHit != null)
            {
                Activate(focusedHit);
                return true;
            }
            if (_detailItem != null)
            {
                PlayDetailItem(resume: false);
                return true;
            }
            if (_activeGroup != null)
                OpenSelectedGroupChild();
            else if (_heroItem != null)
                ShowDetailOverlay(_heroItem);
            return true;
        }

        public bool TryRemotePostOkFocus(Control? previouslyFocused, out Control? next)
        {
            next = _focusSink;
            return true;
        }

        private IReadOnlyList<HitZone> KeyboardHits()
        {
            if (_queueEditorVisible)
                return Array.Empty<HitZone>();

            // Le frecce nella libreria servono a scegliere un film. Pulsanti,
            // sidebar, filtri e azioni rapide restano disponibili con il mouse,
            // ma non entrano piu' nella sequenza di evidenziazione.
            Rectangle navigationBounds = _view == PageView.Collection && !_gridViewport.IsEmpty
                ? Rectangle.Inflate(_gridViewport, 0, Math.Max(80, _gridViewport.Height / 2))
                : ClientRectangle;
            return _hits.Where(hit => ((hit.Kind == HitKind.Poster &&
                    hit.Item != null &&
                    string.Equals(hit.Item.Category, "Movies", StringComparison.OrdinalIgnoreCase) &&
                    IsMoviePath(hit.Item.Path)) ||
                    // "Continua a guardare" fa parte della navigazione della home.
                    (hit.Kind == HitKind.Continue && hit.Resume != null)) &&
                    hit.Bounds.Width > 3 && hit.Bounds.Height > 3 &&
                    hit.Bounds.IntersectsWith(navigationBounds))
                .ToList();
        }

        private static string KeyboardHitIdentity(HitZone hit)
            => $"{hit.Kind}|{hit.Key}|{hit.Item?.Path}|{hit.Resume?.Item.Path}|{hit.Index}";

        private HitZone? FindKeyboardFocusedHit()
        {
            if (!_keyboardFocusVisible || string.IsNullOrWhiteSpace(_keyboardFocusIdentity))
                return null;
            return KeyboardHits().LastOrDefault(hit => string.Equals(KeyboardHitIdentity(hit), _keyboardFocusIdentity, StringComparison.Ordinal));
        }

        private bool TryMoveKeyboardFocus(string direction)
        {
            if (direction is not ("left" or "right" or "up" or "down"))
                return false;

            var candidates = KeyboardHits();
            if (candidates.Count == 0)
                return false;

            HitZone? current = FindKeyboardFocusedHit();
            if (current == null)
            {
                HitZone first = candidates
                    .OrderBy(hit => hit.Bounds.Top)
                    .ThenBy(hit => hit.Bounds.Left)
                    .First();
                SetKeyboardFocus(first);
                return true;
            }

            // Navigazione per righe: sinistra/destra restano nella riga corrente (a fine riga
            // il focus si ferma invece di saltare nella fila sopra, che prima scorreva
            // "Continua a guardare" dall'interno di "Film"); su/giu' cambiano riga.
            Rectangle cur = current.Bounds;
            PointF origin = new(cur.Left + cur.Width / 2f, cur.Top + cur.Height / 2f);
            bool horizontal = direction is "left" or "right";
            HitZone? best = null;
            double bestScore = double.MaxValue;
            foreach (HitZone candidate in candidates)
            {
                if (ReferenceEquals(candidate, current))
                    continue;
                Rectangle b = candidate.Bounds;
                PointF center = new(b.Left + b.Width / 2f, b.Top + b.Height / 2f);
                double dx = center.X - origin.X;
                double dy = center.Y - origin.Y;
                double score;
                if (horizontal)
                {
                    int overlap = Math.Min(cur.Bottom, b.Bottom) - Math.Max(cur.Top, b.Top);
                    if (overlap < Math.Min(cur.Height, b.Height) * 0.4) continue; // altra riga
                    if (direction == "left" ? dx >= -1 : dx <= 1) continue;
                    score = Math.Abs(dx) * 1000 + Math.Abs(dy);
                }
                else
                {
                    if (direction == "up" ? dy >= -1 : dy <= 1) continue;
                    int overlap = Math.Min(cur.Bottom, b.Bottom) - Math.Max(cur.Top, b.Top);
                    if (overlap > Math.Min(cur.Height, b.Height) * 0.4) continue; // stessa riga
                    // Prima la riga piu' vicina, poi l'elemento piu' allineato in orizzontale.
                    double rowDistance = Math.Max(0, direction == "up" ? cur.Top - b.Bottom : b.Top - cur.Bottom);
                    int hOverlap = Math.Min(cur.Right, b.Right) - Math.Max(cur.Left, b.Left);
                    double hDistance = hOverlap > 0 ? 0 : Math.Abs(dx);
                    score = Math.Round(rowDistance / 24.0) * 100000 + hDistance * 10 + Math.Abs(dx);
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best == null)
            {
                // Fine della fila "Continua a guardare": scorre di una card e il focus la segue.
                if (horizontal && current.Kind == HitKind.Continue && current.Resume != null)
                {
                    int index = _resumeItems.FindIndex(r => string.Equals(r.Item.Path, current.Resume.Item.Path, StringComparison.OrdinalIgnoreCase));
                    int target = index + (direction == "right" ? 1 : -1);
                    if (index >= 0 && target >= 0 && target < _resumeItems.Count)
                    {
                        _resumeOffset = Math.Max(0, _resumeOffset + (direction == "right" ? 1 : -1));
                        string targetPath = _resumeItems[target].Item.Path;
                        Invalidate();
                        Update();
                        var next = _hits.FirstOrDefault(h => h.Kind == HitKind.Continue && h.Resume != null &&
                            string.Equals(h.Resume.Item.Path, targetPath, StringComparison.OrdinalIgnoreCase));
                        if (next != null) SetKeyboardFocus(next);
                    }
                }
                return horizontal; // fine riga: il tasto e' gestito, il focus non salta di riga
            }
            SetKeyboardFocus(best);
            return true;
        }

        private void SetKeyboardFocus(HitZone hit)
        {
            Rectangle old = FindKeyboardFocusedHit()?.Bounds ?? Rectangle.Empty;
            _keyboardFocusIdentity = KeyboardHitIdentity(hit);
            _keyboardFocusVisible = true;
            SuppressPointerHoverUntilMouseMove();
            int previousScroll = _gridScroll;
            if (_view == PageView.Collection && !_gridViewport.IsEmpty && _gridScrollMax > 0)
            {
                int margin = 6;
                if (hit.Bounds.Top < _gridViewport.Top + margin)
                    _gridScroll = Math.Max(0, _gridScroll - (_gridViewport.Top + margin - hit.Bounds.Top));
                else if (hit.Bounds.Bottom > _gridViewport.Bottom - margin)
                    _gridScroll = Math.Min(_gridScrollMax, _gridScroll + (hit.Bounds.Bottom - (_gridViewport.Bottom - margin)));
            }
            if (_gridScroll != previousScroll)
            {
                Invalidate();
                return;
            }
            Rectangle dirty = old.IsEmpty ? hit.Bounds : Rectangle.Union(old, hit.Bounds);
            dirty.Inflate(8, 8);
            Invalidate(Rectangle.Intersect(ClientRectangle, dirty));
        }

        public bool TryRemoteExitSearchEdit(Control? current, out Control? next)
        {
            next = _focusSink;
            FocusLibrarySurface();
            return true;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                Form? owner = FindForm();
                if (owner?.Visible == true)
                {
                    EnsureInitialContentPrepared();
                    if (_items.Count == 0)
                        QueueRefresh();
                    else
                        RefreshVisibleArtworkFromCache();
                }
                try { BeginInvoke(new Action(() => { try { Focus(); } catch { } })); } catch { }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            FocusLibrarySurface();
                            if (FindForm() is Form owner)
                                owner.ActiveControl = this;
                        }
                        catch { }
                    }));
                }
                catch { }
                try { if (!_heroTimer.Enabled) _heroTimer.Start(); } catch { }
            }
            else
            {
                try { _heroTimer.Stop(); } catch { }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutMusicWorkspaceContent();
            LayoutQueueDrawer();
            LayoutSearchBox();
            LayoutWebInputBox();
            LayoutPlaylistCreateOverlay();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            LayoutSearchBox();
            LayoutWebInputBox();
            LayoutPlaylistCreateOverlay();
            Invalidate(true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pointerHoverSuppressed)
            {
                Point now = Control.MousePosition;
                if (Math.Abs(now.X - _pointerHoverSuppressedAt.X) <= 2 && Math.Abs(now.Y - _pointerHoverSuppressedAt.Y) <= 2)
                    return;
                _pointerHoverSuppressed = false;
            }
            _lastMouse = e.Location;
            if (_draggingScrollbar)
            {
                _activeScrollbarUpdate?.Invoke(e.Y - _scrollbarDragOffset);
                Cursor = Cursors.Hand;
                return;
            }
            if (_queueDragIndex >= 0)
            {
                if (!_queueDragging)
                {
                    int dx = e.Location.X - _queueDragStart.X;
                    int dy = e.Location.Y - _queueDragStart.Y;
                    if (Math.Abs(dx) + Math.Abs(dy) >= 8)
                        _queueDragging = true;
                }

                if (_queueDragging)
                {
                    _queueDropIndex = QueueIndexAtPoint(e.Location);
                    if (_queueDropIndex < 0)
                        _queueDropIndex = _queueEditorSelectionIndex;
                    Invalidate();
                    Cursor = Cursors.SizeAll;
                    return;
                }
            }

            var hit = HitTest(e.Location);
            Cursor desired = hit != null && hit.Kind != HitKind.None ? Cursors.Hand : Cursors.Default;
            if (!ReferenceEquals(Cursor, desired))
                Cursor = desired;

            string signature = hit == null
                ? string.Empty
                : $"{hit.Kind}:{hit.Key}:{hit.Bounds.Left},{hit.Bounds.Top},{hit.Bounds.Width},{hit.Bounds.Height}";
            if (!string.Equals(_hoverSignature, signature, StringComparison.Ordinal))
            {
                Rectangle previous = _hoverBounds;
                _hoverSignature = signature;
                _hoverBounds = hit?.Bounds ?? Rectangle.Empty;
                InvalidateHoverTransition(previous, _hoverBounds);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetPressedFeedback(null);
            _lastMouse = new Point(-1000, -1000);
            _hoverSignature = string.Empty;
            Rectangle previous = _hoverBounds;
            _hoverBounds = Rectangle.Empty;
            Cursor = Cursors.Default;
            InvalidateHoverTransition(previous, Rectangle.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            try { Focus(); } catch { }

            if (e.Button == MouseButtons.Right)
            {
                var contextHit = HitTest(e.Location);
                if (contextHit?.Kind == HitKind.Header && contextHit.Key == "filter")
                    ShowFilterChoices();
                else if (contextHit?.Kind == HitKind.Continue && contextHit.Resume?.Item is LibraryItem resumed && !string.IsNullOrWhiteSpace(resumed.Path))
                {
                    // Tasto destro su un titolo di "Continua a guardare": lo si puo' togliere dalla riga.
                    string path = resumed.Path;
                    ShowItemMenu(e.Location, (L("Rimuovi da questa riga", "Remove from this row"), "trash", () =>
                    {
                        try { PlaybackResumeStore.Hide(path); } catch { }
                        _resumeItems.RemoveAll(item => string.Equals(item.Item?.Path, path, StringComparison.OrdinalIgnoreCase));
                        Invalidate();
                    }));
                }
                else if (!string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase) && contextHit?.Kind == HitKind.Poster && CanEditMetadata(contextHit.Item))
                {
                    LibraryItem edited = contextHit.Item!;
                    ShowItemMenu(e.Location, (L("Correggi titolo e copertina…", "Fix title and cover…"), "edit", () => EditItemMetadata(edited)));
                }
                else if (string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase) && contextHit?.Kind == HitKind.Poster &&
                         (contextHit.Item?.Path ?? contextHit.Key) is string watched && !string.IsNullOrWhiteSpace(watched))
                {
                    ShowItemMenu(e.Location, (L("Rimuovi dal diario", "Remove from the diary"), "trash", () =>
                    {
                        try { WatchHistoryStore.Remove(watched); } catch { }
                        _watchRatingsCache = null;
                        ShowCategoryPage(_category, clearSearch: false);
                    }));
                }
                return;
            }

            if (e.Button != MouseButtons.Left)
                return;

            _keyboardFocusVisible = false;
            if (TryBeginScrollbarDrag(e.Location, _queueScrollbarTrack, _queueScrollbarThumb, UpdateQueueScrollFromScrollbar) ||
                TryBeginScrollbarDrag(e.Location, _groupScrollbarTrack, _groupScrollbarThumb, UpdateGroupScrollFromScrollbar) ||
                TryBeginScrollbarDrag(e.Location, _youtubeScrollbarTrack, _youtubeScrollbarThumb, UpdateYouTubeScrollFromScrollbar) ||
                TryBeginScrollbarDrag(e.Location, _gridScrollbarTrack, _gridScrollbarThumb, UpdateGridScrollFromScrollbar))
                return;

            var hit = HitTest(e.Location);
            if (hit == null)
                return;
            SetPressedFeedback(hit);

            if (_queueEditorVisible && hit.Kind == HitKind.QueuePlay && hit.Index >= 0)
            {
                _queueEditorSelectionIndex = Math.Max(0, hit.Index);
                EnsureQueueSelectionVisible();
                if (e.Clicks >= 2 && !string.IsNullOrWhiteSpace(hit.Key))
                {
                    QueuePlayPathRequested?.Invoke(hit.Key);
                    RefreshQueueEditorOverlay();
                }
                else
                {
                    _queueDragIndex = hit.Index;
                    _queueDropIndex = hit.Index;
                    _queueDragPath = hit.Key;
                    _queueDragStart = e.Location;
                    _queueDragging = false;
                    Invalidate();
                }
                return;
            }

            Activate(hit);
        }

        // Feedback di pressione per i pulsanti disegnati (frecce, azioni, chiudi…):
        // finché il tasto è giù il pulsante si scurisce leggermente.
        private Rectangle _pressedBounds;

        private void SetPressedFeedback(HitZone? hit)
        {
            // Le schede con la barra colorata (Album/Artisti/Brani/Playlist) e la sidebar hanno gia'
            // il loro stato selezionato: niente velo grigio al clic.
            bool plain = hit != null && hit.Kind is HitKind.MusicTab or HitKind.Nav;
            Rectangle next = hit != null && !plain && hit.Kind != HitKind.None && hit.Bounds.Width <= 460 && hit.Bounds.Height <= 140 ? hit.Bounds : Rectangle.Empty;
            if (next == _pressedBounds) return;
            Rectangle previous = _pressedBounds;
            _pressedBounds = next;
            if (!previous.IsEmpty) Invalidate(Rectangle.Inflate(previous, 4, 4));
            if (!next.IsEmpty) Invalidate(Rectangle.Inflate(next, 4, 4));
        }

        private void DrawPressedFeedback(Graphics g)
        {
            if (_pressedBounds.IsEmpty) return;
            using var path = Round(_pressedBounds, Math.Min(12, Math.Min(_pressedBounds.Width, _pressedBounds.Height) / 2));
            using var shade = new SolidBrush(HUD.Theme.IsLight ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(46, 0, 0, 0));
            g.FillPath(shade, path);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            SetPressedFeedback(null);
            if (_draggingScrollbar)
            {
                _draggingScrollbar = false;
                _activeScrollbarUpdate = null;
                _activeScrollbarTrack = Rectangle.Empty;
                _activeScrollbarThumb = Rectangle.Empty;
                Capture = false;
                Cursor = Cursors.Default;
                return;
            }
            if (_queueDragIndex < 0)
                return;

            try
            {
                if (_queueDragging && !string.IsNullOrWhiteSpace(_queueDragPath))
                {
                    int target = _queueDropIndex >= 0 ? _queueDropIndex : QueueIndexAtPoint(e.Location);
                    if (target >= 0 && target != _queueDragIndex)
                    {
                        QueueMoveRequested?.Invoke(_queueDragPath, target - _queueDragIndex);
                        _queueEditorSelectionIndex = target;
                        RefreshQueueEditorOverlay();
                    }
                }
            }
            finally
            {
                _queueDragIndex = -1;
                _queueDropIndex = -1;
                _queueDragPath = null;
                _queueDragging = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        private void UpdateGridScrollFromScrollbar(int requestedThumbTop)
        {
            if (_gridScrollbarTrack.IsEmpty || _gridScrollbarThumb.IsEmpty || _gridScrollMax <= 0)
                return;
            int travel = Math.Max(1, _gridScrollbarTrack.Height - _gridScrollbarThumb.Height);
            int top = Math.Clamp(requestedThumbTop, _gridScrollbarTrack.Top, _gridScrollbarTrack.Top + travel);
            _gridScrollAnimationTimer.Stop();
            _gridScroll = (int)Math.Round((top - _gridScrollbarTrack.Top) / (double)travel * _gridScrollMax);
            _gridScrollTarget = _gridScroll;
            Invalidate();
        }

        private bool TryBeginScrollbarDrag(Point point, Rectangle track, Rectangle thumb, Action<int> update)
        {
            if (track.IsEmpty || thumb.IsEmpty || (!thumb.Contains(point) && !track.Contains(point)))
                return false;
            _draggingScrollbar = true;
            _activeScrollbarTrack = track;
            _activeScrollbarThumb = thumb;
            _activeScrollbarUpdate = update;
            _scrollbarDragOffset = thumb.Contains(point) ? point.Y - thumb.Top : Math.Max(0, thumb.Height / 2);
            Capture = true;
            update(point.Y - _scrollbarDragOffset);
            return true;
        }

        private int ScrollValueFromActiveThumb(int requestedTop, int maximum)
        {
            int travel = Math.Max(1, _activeScrollbarTrack.Height - _activeScrollbarThumb.Height);
            int top = Math.Clamp(requestedTop, _activeScrollbarTrack.Top, _activeScrollbarTrack.Top + travel);
            return (int)Math.Round((top - _activeScrollbarTrack.Top) / (double)travel * Math.Max(0, maximum));
        }

        private void UpdateYouTubeScrollFromScrollbar(int requestedTop)
        {
            _youtubeScroll = ScrollValueFromActiveThumb(requestedTop, _youtubeScrollMax);
            Invalidate();
        }

        private void UpdateGroupScrollFromScrollbar(int requestedTop)
        {
            int maximum = Math.Max(0, (_activeGroup?.Children.Count ?? 0) - Math.Max(1, _groupVisibleRows));
            _groupListScroll = ScrollValueFromActiveThumb(requestedTop, maximum);
            _groupSelectionIndex = Math.Clamp(_groupSelectionIndex, _groupListScroll,
                Math.Min(Math.Max(0, (_activeGroup?.Children.Count ?? 1) - 1), _groupListScroll + Math.Max(1, _groupVisibleRows) - 1));
            Invalidate();
        }

        private void UpdateQueueScrollFromScrollbar(int requestedTop)
        {
            _queueEditorScroll = ScrollValueFromActiveThumb(requestedTop, _queueEditorScrollMax);
            _queueOverlay?.Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (ApplyMouseWheelDelta(e.Delta))
                return;

            base.OnMouseWheel(e);
        }

        private bool ApplyMouseWheelDelta(int delta)
        {
            if (_view == PageView.Network && _networkListViewport.Contains(_lastMouse))
            {
                _networkListScroll = Math.Clamp(_networkListScroll + (delta < 0 ? 1 : -1), 0, _networkListScrollMax);
                Invalidate(_networkListViewport);
                return true;
            }
            if (_detailItem != null)
            {
                if (_detailCastVisible)
                    MoveDetailCastCarousel(delta < 0 ? 1 : -1);
                else if (_detailReviewsVisible && _reviewsScrollMax > 0)
                    GoToReviewsPage(_reviewsPage + (delta < 0 ? 1 : -1));
                return true;
            }

            if (_queueEditorVisible)
            {
                var snapshot = QueueSnapshotResolver?.Invoke() ?? Array.Empty<PlaybackQueueViewItem>();
                int lines = SystemInformation.MouseWheelScrollLines;
                if (lines <= 0)
                    lines = 3;
                int notches = Math.Max(1, Math.Abs(delta) / Math.Max(1, SystemInformation.MouseWheelScrollDelta));
                int step = 2 * notches;
                int visible = Math.Max(1, _queueEditorVisibleRows);
                _queueEditorScrollMax = Math.Max(0, snapshot.Count - visible);
                int nextScroll = Math.Max(0, Math.Min(_queueEditorScrollMax, _queueEditorScroll + (delta < 0 ? step : -step)));
                if (nextScroll != _queueEditorScroll)
                {
                    _queueEditorScroll = nextScroll;
                    // Solo il cassetto: ridisegnare anche la pagina sotto rendeva lo scorrimento lento.
                    if (_queueOverlay != null) { _queueOverlay.Invalidate(); _queueOverlay.Update(); }
                    else Invalidate();
                }
                return true;
            }

            if (_playlistCreateOverlay?.Visible == true)
                return true;

            if (_activeGroup != null)
            {
                int wheelDelta = Math.Max(1, SystemInformation.MouseWheelScrollDelta);
                int accumulated = _groupWheelRemainder + delta;
                int notches = accumulated / wheelDelta;
                _groupWheelRemainder = accumulated % wheelDelta;
                if (notches == 0)
                    return true;
                int nextIndex = Math.Clamp(_groupSelectionIndex - notches, 0, Math.Max(0, _activeGroup.Children.Count - 1));
                if (nextIndex != _groupSelectionIndex)
                {
                    _groupSelectionIndex = nextIndex;
                    EnsureGroupSelectionVisible();
                    QueueOverviewResolve(SelectedGroupChild());
                    QueueMusicInfoResolve(SelectedGroupChild());
                    Invalidate();
                }
                return true;
            }

            if (_view == PageView.Collection)
            {
                int notches = Math.Max(1, Math.Abs(delta) / Math.Max(1, SystemInformation.MouseWheelScrollDelta));
                int wheelStep = Math.Clamp((int)Math.Round(Height * 0.32), 190, 430);
                int step = wheelStep * notches;
                int origin = _gridScrollAnimationTimer.Enabled ? _gridScrollTarget : _gridScroll;
                SetSmoothGridScrollTarget(origin + (delta < 0 ? step : -step));
                if (_category == "Photos" && delta < 0 && !_contentLoading &&
                    _items.Count >= _photoVisibleLimit && _photoVisibleLimit < _photoTotalAvailable &&
                    _gridScrollTarget >= _gridScrollMax - Height)
                {
                    _photoVisibleLimit += PhotoPageSize;
                    RefreshContent();
                }
                return true;
            }

            if (IsNativeYouTubeView)
            {
                int step = Math.Clamp((int)Math.Round(Height * 0.34), 220, 460);
                int youtubeTarget = Math.Clamp(_youtubeScroll + (delta < 0 ? step : -step), 0, Math.Max(0, _youtubeScrollMax));
                if (youtubeTarget != _youtubeScroll)
                {
                    _youtubeScroll = youtubeTarget;
                    Invalidate();
                }
                return true;
            }

            int resumeDelta = delta < 0 ? 1 : -1;
            int next = Math.Max(0, Math.Min(Math.Max(0, _resumeItems.Count - 1), _resumeOffset + resumeDelta));
            if (next != _resumeOffset)
            {
                _resumeOffset = next;
                Invalidate();
                return true;
            }

            return false;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Enter or Keys.Space or Keys.Escape)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_diaryRatingItem != null || _diaryRatingMode)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    _diaryRatingItem = null;
                    _diaryRatingMode = false;
                    Invalidate();
                    e.Handled = true;
                }
                return;
            }
            if (_detailItem != null)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    CloseDetailOverlay();
                    e.Handled = true;
                }
                else if (e.KeyCode is Keys.Enter or Keys.Space)
                {
                    PlayDetailItem(resume: false);
                    e.Handled = true;
                }
                else if (_detailCastVisible && e.KeyCode is Keys.Left or Keys.Right)
                {
                    MoveDetailCastCarousel(e.KeyCode == Keys.Right ? 1 : -1);
                    e.Handled = true;
                }
                return;
            }

            if (_queueEditorVisible)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    CloseQueueEditorOverlay();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                {
                    var snapshot = QueueSnapshotResolver?.Invoke() ?? Array.Empty<PlaybackQueueViewItem>();
                    if (snapshot.Count > 0)
                    {
                        _queueEditorSelectionIndex = Math.Max(0, Math.Min(snapshot.Count - 1, _queueEditorSelectionIndex + (e.KeyCode == Keys.Down ? 1 : -1)));
                        EnsureQueueSelectionVisible();
                        Invalidate();
                    }
                    e.Handled = true;
                }
                else if (e.KeyCode is Keys.Enter or Keys.Space)
                {
                    var snapshot = QueueSnapshotResolver?.Invoke() ?? Array.Empty<PlaybackQueueViewItem>();
                    if (_queueEditorSelectionIndex >= 0 && _queueEditorSelectionIndex < snapshot.Count)
                    {
                        QueuePlayPathRequested?.Invoke(snapshot[_queueEditorSelectionIndex].Path);
                        RefreshQueueEditorOverlay();
                    }
                    e.Handled = true;
                }
                return;
            }

            if (_activeGroup != null)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    CloseGroupPicker();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                {
                    MoveGroupSelection(e.KeyCode == Keys.Down ? 1 : -1);
                    e.Handled = true;
                }
                else if (e.KeyCode is Keys.Enter or Keys.Space)
                {
                    OpenSelectedGroupChild();
                    e.Handled = true;
                }
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                CloseRequested?.Invoke();
                e.Handled = true;
            }
            else if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                if (_heroItem != null)
                    ShowDetailOverlay(_heroItem);
                e.Handled = true;
            }
        }

        private bool IsNetworkSourceActive()
        {
            return string.Equals(_source, "Network", StringComparison.OrdinalIgnoreCase);
        }

        private List<LibraryItem> BuildNetworkItemsForCurrentCategory(int maxItems)
        {
            IEnumerable<LibraryItem> items = _networkItems.ToArray();
            HashSet<string> favouritePaths = new(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(_category, "Favourites", StringComparison.OrdinalIgnoreCase))
            {
                favouritePaths = GetFavoritePathsSnapshot();
            }

            items = _category switch
            {
                "Movies" => items.Where(item => string.Equals(item.Category, "Movies", StringComparison.OrdinalIgnoreCase)),
                "TV Series" => items.Where(item => string.Equals(item.Category, "TV Series", StringComparison.OrdinalIgnoreCase)),
                "Videos" => items.Where(item => string.Equals(item.Category, "Videos", StringComparison.OrdinalIgnoreCase)),
                "Music" => items.Where(item => string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase)),
                "Photos" => items.Where(item => string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase)),
                "Favourites" => favouritePaths.Count > 0
                    ? items.Where(item => favouritePaths.Contains(item.Path))
                    : Enumerable.Empty<LibraryItem>(),
                _ => items
            };

            var paths = items
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            paths = GroupCollectionItems(paths);

            paths = ApplyActiveSort(paths);

            if (string.Equals(_category, "Photos", StringComparison.OrdinalIgnoreCase))
            {
                paths = ApplyActiveFilter(paths).ToList();
                _photoTotalAvailable = paths.Count;
            }

            return paths.Take(maxItems).ToList();
        }

        private List<(string Icon, string Text)> BuildNetworkStatsCells(IReadOnlyList<LibraryItem> loadedItems)
        {
            int movies = _networkItems.Count(item => string.Equals(item.Category, "Movies", StringComparison.OrdinalIgnoreCase));
            int tv = _networkItems
                .Where(item => string.Equals(item.Category, "TV Series", StringComparison.OrdinalIgnoreCase))
                .Select(item => FirstNonEmpty(item.SeriesTitle, TvSeriesKey(item.Path)))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            int videos = _networkItems.Count(item => string.Equals(item.Category, "Videos", StringComparison.OrdinalIgnoreCase));
            int music = _networkItems.Count(item => string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase));
            int photos = _networkItems.Count(item => string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase));
            long bytes = _networkItems.Sum(item => item.Bytes);

            var connected = _networkServers.FirstOrDefault(server => string.Equals(NetworkServerKey(server), _networkConnectedServerKey, StringComparison.OrdinalIgnoreCase));
            string protocol = IsJellyfinServer(connected) ? "Jellyfin" : "DLNA";
            var cells = new List<(string Icon, string Text)>
            {
                ("network", string.IsNullOrWhiteSpace(connected?.Name) ? L("Server collegato", "Server connected") : connected!.Name),
                ("movie", L($"{movies:N0} film", $"{movies:N0} movies")),
                ("tv", L($"{tv:N0} serie TV", $"{tv:N0} TV shows")),
                ("play", L($"{videos:N0} video", $"{videos:N0} videos")),
                ("music", L($"{music:N0} brani", $"{music:N0} tracks")),
                ("photo", L($"{photos:N0} foto", $"{photos:N0} photos"))
            };
            cells.Add(("storage", bytes > 0 ? $"{FormatBytes(bytes)} {protocol}" : L($"{loadedItems.Count:N0} elementi", $"{loadedItems.Count:N0} items")));
            return cells;
        }

        private List<LibraryItem> GroupCollectionItems(List<LibraryItem> items)
        {
            if (items.Count == 0)
                return items;

            if (string.Equals(_category, "TV Series", StringComparison.OrdinalIgnoreCase))
                return GroupTvSeasons(items);
            if (string.Equals(_category, "Music", StringComparison.OrdinalIgnoreCase))
                return GroupMusicAlbums(items);

            return items;
        }

        private List<LibraryItem> GroupTvSeasons(IReadOnlyList<LibraryItem> episodes)
        {
            var buckets = new Dictionary<string, GroupBucket>(StringComparer.OrdinalIgnoreCase);

            foreach (var episode in episodes)
            {
                var info = TryExtractMediaTitleInfo(episode.Path);
                bool networkEpisode = IsNetworkPath(episode.Path);
                string series = networkEpisode
                    ? FirstNonEmpty(
                        episode.SeriesTitle,
                        ExtractSeriesTitleFromDisplay(episode.Title),
                        info?.SeriesTitle,
                        L("Serie TV", "TV Series"))
                    : FirstNonEmpty(
                        InferSeriesTitleFromPath(episode.Path),
                        episode.SeriesTitle,
                        info?.SeriesTitle,
                        ExtractSeriesTitleFromDisplay(episode.Title),
                        TvSeriesKey(episode.Path),
                        L("Serie TV", "TV Series"));
                // The filename/folder is the source of truth for grouping. Cached TMDb
                // fields can belong to a previous lookup and were able to move S01 into S02.
                int season = networkEpisode
                    ? episode.SeasonNumber ?? TryParseSeasonNumber(episode.Title) ?? info?.SeasonNumber ?? 1
                    : TryParseSeasonNumber(episode.Path) ?? info?.SeasonNumber ?? episode.SeasonNumber ?? 1;
                int episodeNumber = networkEpisode
                    ? episode.EpisodeNumber ?? TryParseEpisodeNumber(episode.Title) ?? info?.EpisodeNumber ?? int.MaxValue
                    : TryParseEpisodeNumber(episode.Path) ?? info?.EpisodeNumber ?? episode.EpisodeNumber ?? int.MaxValue;
                string key = NormalizeGroupKey(series) + "|s" + season.ToString("00", CultureInfo.InvariantCulture);

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new GroupBucket
                    {
                        Title = BuildSeasonGroupTitle(series, season),
                        Kind = "Season"
                    };
                    buckets[key] = bucket;
                }

                bucket.Children.Add(new GroupChildSort
                {
                    Item = episode,
                    Number = episodeNumber,
                    Title = episode.Title
                });
            }

            return buckets.Values
                .Select(bucket =>
                {
                    var children = bucket.Children
                        .OrderBy(child => child.Number)
                        .ThenBy(child => child.Title, StringComparer.OrdinalIgnoreCase)
                        .Select(child => child.Item)
                        .ToList();
                    string subtitle = L($"{children.Count:N0} episodi", $"{children.Count:N0} episodes");
                    return BuildGroupedItem(bucket.Title, subtitle, "Season", children);
                })
                .Where(item => item != null)
                .Cast<LibraryItem>()
                .ToList();
        }

        private static string MusicTrackDedupKey(LibraryItem track)
        {
            int? number = track.TrackNumber ?? TryParseTrackNumber(track.Path);
            // Within one album the track index is the canonical identity. Some
            // scanners expose the same file twice with two differently formatted
            // titles ("01. Artist - Title" and "01. Title"); title based keys kept
            // both rows visible.
            if (number.HasValue)
                return NormalizeRootPath(Path.GetDirectoryName(track.Path) ?? "") + "|track|" + number.Value.ToString("000", CultureInfo.InvariantCulture);

            string title = CleanMusicTrackTitle(FirstRawNonEmpty(track.Title, Path.GetFileNameWithoutExtension(track.Path)));
            string artist = CleanMusicTitle(track.ArtistName);
            if (!string.IsNullOrWhiteSpace(artist) && title.StartsWith(artist, StringComparison.OrdinalIgnoreCase))
                title = title[artist.Length..].Trim(' ', '-', '_', '.');
            title = Regex.Replace(title, @"^\s*(?:\d{1,3}\s+){1,2}", string.Empty).Trim();
            string normalizedTitle = NormalizeGroupKey(title);
            return normalizedTitle.Length > 0 ? normalizedTitle : NormalizeGroupKey(track.Path);
        }

        private List<LibraryItem> GroupMusicAlbums(IReadOnlyList<LibraryItem> tracks)
        {
            var buckets = new Dictionary<string, GroupBucket>(StringComparer.OrdinalIgnoreCase);
            var sharedDirectories = tracks.Where(t => !string.IsNullOrWhiteSpace(t.AlbumTitle))
                .GroupBy(t => InferAlbumInfo(t.Path).DirectoryKey, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(t => MusicArtistArtworkService.Identity(t.AlbumTitle)).Distinct().Count() > 1)
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var track in tracks
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()))
            {
                var album = InferAlbumInfo(track.Path);
                string albumTitle = FirstNonEmpty(track.AlbumTitle, album.Album, L("Album sconosciuto", "Unknown album"));
                string artist = FirstNonEmpty(track.AlbumArtist, album.Artist, track.ArtistName);
                string metadataKey = !string.IsNullOrWhiteSpace(track.AlbumTitle) || !string.IsNullOrWhiteSpace(track.ArtistName)
                    ? NormalizeGroupKey(artist + "|" + albumTitle)
                    : string.Empty;
                string key = FirstRawNonEmpty(album.DirectoryKey, metadataKey, NormalizeGroupKey(artist + "|" + albumTitle), NormalizeGroupKey(albumTitle));
                if (IsNetworkPath(track.Path) && !string.IsNullOrWhiteSpace(track.AlbumTitle) &&
                    Uri.TryCreate(track.Path, UriKind.Absolute, out var networkUri))
                {
                    // DLNA resource URLs describe transport endpoints, not album
                    // folders. Group tracks by the server's album metadata instead.
                    key = "dlna|" + networkUri.Authority.ToLowerInvariant() + "|" + NormalizeGroupKey(albumTitle);
                }
                else if (sharedDirectories.Contains(album.DirectoryKey))
                    key += "|album|" + MusicArtistArtworkService.Identity(albumTitle);

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new GroupBucket
                    {
                        Title = albumTitle,
                        Subtitle = artist,
                        Kind = "Album"
                    };
                    buckets[key] = bucket;
                }

                bucket.Children.Add(new GroupChildSort
                {
                    Item = track,
                    Number = track.TrackNumber ?? TryParseTrackNumber(track.Path) ?? int.MaxValue,
                    Title = track.Title
                });
            }

            return buckets.Values
                .Select(bucket =>
                {
                    var children = bucket.Children
                        .OrderBy(child => Path.GetDirectoryName(child.Item.Path), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(child => child.Number)
                        .ThenBy(child => child.Title, StringComparer.OrdinalIgnoreCase)
                        .GroupBy(child => MusicTrackDedupKey(child.Item), StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .Select(child => child.Item)
                        .ToList();
                    string count = L($"{children.Count:N0} brani", $"{children.Count:N0} tracks");
                    string[] albumArtists = children.Select(t => t.AlbumArtist).Where(a => !string.IsNullOrWhiteSpace(a)).Cast<string>().ToArray();
                    string[] performers = children.Select(t => t.ArtistName).Where(a => !string.IsNullOrWhiteSpace(a)).Cast<string>().DistinctBy(MusicArtistArtworkService.Identity).ToArray();
                    string artist = albumArtists.FirstOrDefault() ?? (performers.Length == 1 ? performers[0] : performers.Length > 1 ? L("Artisti vari", "Various artists") : bucket.Subtitle);
                    string subtitle = string.IsNullOrWhiteSpace(artist) ? count : $"{artist} · {count}";
                    return BuildGroupedItem(bucket.Title, subtitle, "Album", children, artist);
                })
                .Where(item => item != null)
                .Cast<LibraryItem>()
                .ToList();
        }

        private LibraryItem? BuildGroupedItem(string title, string subtitle, string kind, List<LibraryItem> children, string? artist = null)
        {
            if (children.Count == 0)
                return null;

            var representative = children
                .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.ArtPath))
                .ThenByDescending(item => item.SortDateUtc)
                .First();
            string category = representative.Category;
            string? art = string.Equals(kind, "Season", StringComparison.OrdinalIgnoreCase)
                ? FirstGroupedPosterPath(children.Select(item => item.ArtPath))
                : FirstExistingPath(children.Select(item => item.ArtPath));
            string? wideArt = FirstExistingPath(children.Select(item => item.WideArtPath));

            if (string.Equals(kind, "Album", StringComparison.OrdinalIgnoreCase))
            {
                art ??= FindNearbyArt(representative.Path, poster: true);
                art ??= MusicArtworkService.GetCachedArtworkPath(representative.Path);
                wideArt ??= art;
                // Le tracce (vista "Brani", coda) mostrano la copertina dell'album.
                if (!string.IsNullOrWhiteSpace(art))
                    foreach (var child in children)
                        if (string.IsNullOrWhiteSpace(child.ArtPath))
                            child.ArtPath = art;
            }

            if (string.Equals(kind, "Season", StringComparison.OrdinalIgnoreCase))
            {
                string? nearbyPoster = FindNearbyArt(representative.Path, poster: true);
                if (MovieMetadataService.IsUsablePosterImage(nearbyPoster))
                    art ??= nearbyPoster;
                wideArt ??= FindNearbyArt(representative.Path, poster: false);
            }

            double duration = children.Sum(item => Math.Max(0, item.DurationMinutes ?? 0));
            long bytes = children.Sum(item => item.Bytes);
            string overview = string.Equals(kind, "Season", StringComparison.OrdinalIgnoreCase)
                ? L($"Stagione con {children.Count:N0} episodi. Apri il riquadro per scegliere un episodio e vedere i dati TMDb.", $"Season with {children.Count:N0} episodes. Open it to choose an episode and view TMDb details.")
                : L($"Album con {children.Count:N0} brani. Apri il riquadro per scegliere la traccia.", $"Album with {children.Count:N0} tracks. Open it to choose a track.");

            return new LibraryItem
            {
                Path = representative.Path,
                Title = title,
                Category = category,
                PlaybackCategory = representative.PlaybackCategory,
                Year = children.Select(item => item.Year).FirstOrDefault(year => year.HasValue),
                DurationMinutes = duration > 0 ? duration : null,
                SortDateUtc = children.Max(item => item.SortDateUtc),
                ArtPath = string.Equals(kind, "Season", StringComparison.OrdinalIgnoreCase)
                    ? art
                    : art ?? representative.ArtPath,
                WideArtPath = wideArt ?? representative.WideArtPath ?? art,
                Overview = overview,
                AudioLabel = representative.AudioLabel,
                ResolutionLabel = representative.ResolutionLabel,
                IsGroup = true,
                GroupKind = kind,
                ArtistName = artist,
                AlbumArtist = artist,
                GroupSubtitle = subtitle,
                Children = children,
                Is4K = children.Any(item => item.Is4K),
                IsHdr = children.Any(item => item.IsHdr),
                HasAtmos = children.Any(item => item.HasAtmos),
                Bytes = bytes
            };
        }

        private List<ResumeItem> BuildResumeItems(List<LibraryItem> known)
        {
            var byPath = known.ToDictionary(item => item.Path, item => item, StringComparer.OrdinalIgnoreCase);
            var result = new List<ResumeItem>();

            try
            {
                // Con un server di rete in uso, "Continua a guardare" riguarda i titoli del server
                // (aggiunti sotto): quelli lasciati a meta' sul PC appartengono alla libreria del PC.
                foreach (var entry in IsNetworkSourceActive() ? Array.Empty<PlaybackResumeStore.Entry>() : PlaybackResumeStore.LoadAll())
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.MediaPath))
                        continue;
                    if (!IsRemoteStoragePath(entry.MediaPath) && !File.Exists(entry.MediaPath))
                        continue;
                    if (ResumeCompleted(entry))
                        continue;
                    if (!PathBelongsToCurrentCategory(entry.MediaPath))
                        continue;

                    if (!byPath.TryGetValue(entry.MediaPath, out var item))
                    {
                        item = ToItem(entry.MediaPath, allowMediaFileIo: !IsRemoteStoragePath(entry.MediaPath));
                        if (item == null)
                            continue;
                    }

                    result.Add(new ResumeItem
                    {
                        Item = item,
                        PositionSeconds = Math.Max(0, entry.PositionSeconds),
                        DurationSeconds = Math.Max(0, entry.DurationSeconds),
                        SavedAt = entry.SavedAt
                    });
                }
            }
            catch { }

            AddJellyfinResumeItems(byPath, result);

            return result
                .Where(r => !PlaybackResumeStore.IsHidden(r.Item.Path, r.SavedAt))
                .OrderByDescending(r => r.SavedAt)
                .Take(30)
                .ToList();
        }

        private LibraryItem? ToItem(
            string path,
            bool allowMediaFileIo = true,
            IReadOnlyDictionary<string, PlaybackResumeStore.Entry>? resumeLookup = null)
        {
            try
            {
                if (_itemCache.TryGetValue(path, out var cached))
                {
                    if (allowMediaFileIo && cached.IsLightweight)
                    {
                        _itemCache.TryRemove(path, out _);
                    }
                    else
                    {
                    VideoQualityInfo? cachedQuality = TryGetCachedVideoQuality(path, allowMediaFileIo);
                    if ((cachedQuality != null && CachedItemNeedsQualityRefresh(cached, cachedQuality)) ||
                        CachedItemNeedsImmediateQualityRefresh(cached))
                    {
                        _itemCache.TryRemove(path, out _);
                    }
                    else
                    {
                        RefreshCachedArtwork(cached, allowNearbyArt: allowMediaFileIo && !IsRemoteStoragePath(path));
                        if (CachedItemNeedsBackgroundQualityProbe(cached))
                            QueueVideoQualityProbe(path);
                        return cached;
                    }
                    }
                }

                FileInfo? fi = allowMediaFileIo ? new FileInfo(path) : null;
                string category = CategoryForPath(path);
                string title = Path.GetFileNameWithoutExtension(path) ?? Path.GetFileName(path);
                int? year = null;
                MovieMetadataService.MediaTitleInfo? parsedMediaInfo = null;
                MediaProbe.AudioTags? musicTags = null;
                // Lightweight items still use tags already indexed: without them some tracks
                // of an album showed the artist and others (built earlier) did not.
                if (category == "Music") { try { musicTags = allowMediaFileIo ? ReadIndexedAudioTags(path) : TryGetIndexedAudioTags(path); } catch { } }

                if (string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase))
                {
                    title = MusicTextIdentity.Title(FirstNonEmpty(musicTags?.Title, title), filename: string.IsNullOrWhiteSpace(musicTags?.Title));
                    year = fi != null ? SafeYear(fi) : null;
                }
                else if (string.Equals(category, "Photos", StringComparison.OrdinalIgnoreCase))
                {
                    title = CleanTitle(title);
                    year = fi != null ? SafeYear(fi) : null;
                }
                else if (string.Equals(category, "Videos", StringComparison.OrdinalIgnoreCase))
                {
                    // I video generici non hanno bisogno del catalogo TMDb: il titolo dal
                    // filename è immediato e la miniatura viene risolta dalla pipeline video.
                    title = CleanTitle(title);
                    year = fi != null ? SafeYear(fi) : null;
                }
                else
                {
                    try
                    {
                        parsedMediaInfo = MovieMetadataService.ExtractMediaTitleInfoFromPath(path);
                        if (!string.IsNullOrWhiteSpace(parsedMediaInfo.NormalizedTitle))
                            title = parsedMediaInfo.NormalizedTitle;
                        year = parsedMediaInfo.Year;
                    }
                    catch { }

                    try
                    {
                        string best = MovieMetadataService.GetBestKnownDisplayTitle(path);
                        if (!string.IsNullOrWhiteSpace(best))
                            title = best;
                    }
                    catch { }
                }

                string hay = (path + " " + title).ToLowerInvariant();
                string cleanTitle = CleanTitle(title);
                string metadataKey = cleanTitle;
                if (ShouldResolveRichOverview(category))
                {
                    try { year = MovieMetadataService.GetCachedYear(path) ?? MovieMetadataService.GetCachedYear(metadataKey) ?? year; } catch { }
                }
                // Il Property Store di Windows e ffprobe possono impiegare centinaia di ms
                // per file: la griglia usa subito i dati cache e completa la qualita' in background.
                double? duration = IsPhotoPath(path)
                    ? null
                    : EstimateDurationMinutes(
                        path,
                        allowShellDuration: false,
                        resumeLookup,
                        allowMediaFileIo: allowMediaFileIo);
                if (string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase) && musicTags?.DurationMinutes is > 0)
                    duration = musicTags.DurationMinutes;

                string? art = string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase)
                    ? (allowMediaFileIo ? FindNearbyArt(path, poster: true) : null)
                    : (string.Equals(category, "Photos", StringComparison.OrdinalIgnoreCase)
                        ? path
                        : (string.Equals(category, "Videos", StringComparison.OrdinalIgnoreCase)
                            ? GetCachedVideoThumbnailPath(path)
                            : ResolvePosterPath(path, metadataKey, cleanTitle, allowNearbyArt: allowMediaFileIo)));
                if (string.IsNullOrWhiteSpace(art) && IsPhotoPath(path))
                    art = path;

                string? wideArt = string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase)
                    ? art
                    : (string.Equals(category, "Photos", StringComparison.OrdinalIgnoreCase)
                        ? path
                        : (string.Equals(category, "Videos", StringComparison.OrdinalIgnoreCase)
                            ? art
                            : ResolveBackdropPath(path, metadataKey, cleanTitle, allowNearbyArt: allowMediaFileIo)));
                if (string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(wideArt))
                    wideArt = art;
                if (string.IsNullOrWhiteSpace(wideArt) && IsPhotoPath(path))
                    wideArt = path;

                string? inferredResolution = InferResolutionLabel(hay);
                bool textIs4K = Has4KToken(hay);
                bool textHdr = HasHdrToken(hay);
                bool textSdr = HasSdrToken(hay);
                bool shouldInspectVideoQuality = !string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(category, "Photos", StringComparison.OrdinalIgnoreCase) &&
                    IsVideoPath(path);
                VideoQualityInfo? probedQuality = shouldInspectVideoQuality ? TryGetCachedVideoQuality(path, allowMediaFileIo, fi) : null;
                if (allowMediaFileIo && shouldInspectVideoQuality && probedQuality == null)
                    QueueVideoQualityProbe(path);

                if (!duration.HasValue && probedQuality?.DurationMinutes is double probedMinutes && probedMinutes > 0)
                    duration = probedMinutes;

                string? resolutionLabel = probedQuality?.Resolution ?? inferredResolution;
                bool is4K = probedQuality?.Is4K == true || textIs4K;
                bool isHdr = probedQuality?.IsHdr == true || (!textSdr && textHdr);

                var item = new LibraryItem
                {
                    Path = path,
                    Title = cleanTitle,
                    ArtistName = FirstNonEmpty(musicTags?.Artist, musicTags?.AlbumArtist),
                    AlbumArtist = musicTags?.AlbumArtist,
                    AlbumTitle = musicTags?.Album,
                    TrackNumber = musicTags?.TrackNumber ?? (string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase) ? TryParseTrackNumber(path) : null),
                    Category = category,
                    PlaybackCategory = PlaybackCategoryForDisplayCategory(category),
                    Year = year,
                    DurationMinutes = duration,
                    SortDateUtc = fi != null ? SafeSortDate(fi) : DateTime.MinValue,
                    ArtPath = art,
                    WideArtPath = wideArt,
                    Overview = ShouldResolveRichOverview(category)
                        ? ResolveOverview(path, cleanTitle, category, metadataKey)
                        : DefaultOverviewForCategory(category),
                    Genres = TryGetGenresFromIndex(path, cleanTitle, metadataKey) ?? new List<string>(),
                    AudioLabel = FirstRawNonEmpty(probedQuality?.AudioLabel, InferAudioLabel(hay)),
                    ResolutionLabel = resolutionLabel,
                    Is4K = is4K,
                    IsHdr = isHdr,
                    HasAtmos = probedQuality?.HasAtmos == true || hay.Contains("atmos") ||
                               hay.Contains("dts:x") || hay.Contains("dts-x") || hay.Contains("dtsx"),
                    SeriesTitle = parsedMediaInfo?.SeriesTitle,
                    SeasonNumber = TryParseSeasonNumber(path) ?? parsedMediaInfo?.SeasonNumber,
                    EpisodeNumber = TryParseEpisodeNumber(path) ?? parsedMediaInfo?.EpisodeNumber,
                    Bytes = fi != null ? SafeLength(fi) : 0,
                    IsLightweight = !allowMediaFileIo
                };
                // Regista e voto gia' salvati: servono a filtri e righe secondarie senza aprire la scheda.
                if (ShouldResolveRichOverview(category))
                {
                    try
                    {
                        var savedRich = MovieMetadataService.TryGetSavedRichMetadata(path, UiEnglish ? "en-US" : "it-IT");
                        if (savedRich != null)
                        {
                            item.Director = CleanLine(savedRich.Director);
                            item.Rating = savedRich.Rating;
                            if (item.Genres.Count == 0) item.Genres = CleanGenreList(savedRich.Genres);
                            item.CastMembers = savedRich.CastMembers.Where(member => !string.IsNullOrWhiteSpace(member.Name)).Take(20).Select(CloneCastMember).ToList();
                        }
                    }
                    catch { }
                }
                _itemCache[path] = item;
                return item;
            }
            catch
            {
                return null;
            }
        }

        private static bool CachedItemNeedsQualityRefresh(LibraryItem item, VideoQualityInfo quality)
        {
            if (item == null || item.IsGroup || !IsVideoPath(item.Path))
                return false;
            if (string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase))
                return false;

            string? currentResolution = NormalizeQualityLabel(item.ResolutionLabel);
            string? probedResolution = NormalizeQualityLabel(quality.Resolution);
            if (!string.IsNullOrWhiteSpace(probedResolution) &&
                !string.Equals(currentResolution, probedResolution, StringComparison.OrdinalIgnoreCase))
                return true;
            if (quality.Is4K && !item.Is4K)
                return true;
            if (quality.IsHdr && !item.IsHdr)
                return true;
            if (!string.IsNullOrWhiteSpace(quality.AudioLabel) &&
                !string.Equals(item.AudioLabel, quality.AudioLabel, StringComparison.OrdinalIgnoreCase))
                return true;
            if (quality.HasAtmos && !item.HasAtmos)
                return true;

            return false;
        }

        private static string? InferSeriesTitleFromPath(string path)
        {
            try
            {
                string fileStem = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                string normalizedFile = Regex.Replace(fileStem, @"[._]+", " ");
                var episodeMatch = Regex.Match(
                    normalizedFile,
                    @"^(?<series>.+?)\s+(?:S\d{1,2}\s*E\d{1,3}|\d{1,2}x\d{1,3})\b",
                    RegexOptions.IgnoreCase);
                if (episodeMatch.Success)
                {
                    string fromFile = CleanTitle(episodeMatch.Groups["series"].Value).Trim(' ', '-', '.');
                    if (!string.IsNullOrWhiteSpace(fromFile))
                        return fromFile;
                }

                string? directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory)) return null;
                string leaf = Path.GetFileName(directory) ?? string.Empty;
                string parent = Path.GetFileName(Path.GetDirectoryName(directory)) ?? string.Empty;
                string candidate = Regex.IsMatch(leaf, @"(?:\bS\d{1,2}\b|\b(?:season|stagione)\s*\d{1,2}\b)", RegexOptions.IgnoreCase) &&
                                   !string.IsNullOrWhiteSpace(parent)
                    ? parent
                    : leaf;
                candidate = Regex.Replace(candidate, @"\b(?:S\d{1,2}|season|stagione)\s*\d{0,2}\b.*$", string.Empty, RegexOptions.IgnoreCase);
                candidate = CleanTitle(candidate).Trim(' ', '-', '.');
                return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
            }
            catch { return null; }
        }

        private static void RefreshCachedArtwork(LibraryItem item, bool allowNearbyArt = true)
        {
            if (item == null || item.IsGroup || !ShouldResolveRichOverview(item.Category))
                return;

            try
            {
                string metadataKey = ResolveMetadataLookupKey(item.Path, item.Title, item.Year);
                if (!MovieMetadataService.IsUsablePosterImage(item.ArtPath))
                {
                    string? cachedPoster = ResolvePosterPath(item.Path, metadataKey, item.Title, allowNearbyArt: false);
                    if (MovieMetadataService.IsUsablePosterImage(cachedPoster))
                        item.ArtPath = cachedPoster;
                    else if (!IsNetworkPath(item.Path))
                        item.ArtPath = ResolvePosterPath(item.Path, metadataKey, item.Title, allowNearbyArt);
                }
                if (string.IsNullOrWhiteSpace(item.WideArtPath) || !LooksLikeUsableBackdrop(item.WideArtPath))
                {
                    string? cachedBackdrop = ResolveBackdropPath(item.Path, metadataKey, item.Title, allowNearbyArt: false);
                    if (LooksLikeUsableBackdrop(cachedBackdrop))
                        item.WideArtPath = cachedBackdrop;
                    else if (!IsNetworkPath(item.Path))
                        item.WideArtPath = ResolveBackdropPath(item.Path, metadataKey, item.Title, allowNearbyArt);
                }
            }
            catch { }
        }

        private static bool CachedItemNeedsImmediateQualityRefresh(LibraryItem item)
        {
            if (item == null || item.IsGroup || !IsVideoPath(item.Path))
                return false;
            if (string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase))
                return false;

            string hay = item.Path + " " + item.Title + " " + item.ResolutionLabel;
            bool textSdr = HasSdrToken(hay);
            if (IsRawPixelResolution(item.ResolutionLabel))
                return true;
            if (Has4KToken(hay) && (!item.Is4K || string.IsNullOrWhiteSpace(item.ResolutionLabel)))
                return true;
            if (HasHdrToken(hay) && !textSdr && !item.IsHdr)
                return true;
            if (textSdr && item.IsHdr)
                return true;

            return false;
        }

        private static bool CachedItemNeedsBackgroundQualityProbe(LibraryItem item)
        {
            if (item == null || item.IsGroup || !IsVideoPath(item.Path))
                return false;
            if (string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase))
                return false;

            return string.IsNullOrWhiteSpace(item.ResolutionLabel) ||
                   IsRawPixelResolution(item.ResolutionLabel) ||
                   string.IsNullOrWhiteSpace(item.AudioLabel);
        }

        private IEnumerable<LibraryItem> VisibleItems()
        {
            IEnumerable<LibraryItem> items = _items;

            string query = (_searchBox.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(query))
            {
                string[] tokens = Regex.Split(query.ToLowerInvariant(), "\\s+").Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
                if (tokens.Length > 0)
                {
                    items = items.Where(item =>
                    {
                        string hay = SearchHaystack(item).ToLowerInvariant();
                        return tokens.All(hay.Contains);
                    });
                }
            }

            return ApplyActiveFilter(items);
        }

        private static string SearchHaystack(LibraryItem item)
        {
            if (item == null)
                return string.Empty;

            // Oltre a titolo e percorso: regista, interpreti, anno, genere, artista e album.
            static string People(LibraryItem entry) => string.Join(" ", new[]
            {
                entry.Director, entry.ArtistName, entry.AlbumArtist, entry.AlbumTitle,
                entry.Year?.ToString(CultureInfo.InvariantCulture),
                string.Join(" ", entry.Genres),
                string.Join(" ", entry.CastMembers.Select(member => member.Name))
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (!item.IsGroup || item.Children.Count == 0)
                return item.Title + " " + item.Path + " " + People(item);

            return item.Title + " " + item.GroupSubtitle + " " + People(item) + " " +
                   string.Join(" ", item.Children.Select(child => child.Title + " " + child.Path + " " + People(child)));
        }

        private IEnumerable<string> AllPathsForCategory(string category)
        {
            var list = new List<string>();
            var index = LoadLibraryIndex();

            void AddIndex(string key)
            {
                if (index.Categories.TryGetValue(key, out var own))
                    list.AddRange(own);
            }

            if (category == "Movies" || category == "TV Series")
            {
                AddIndex(category);
                AddIndex("Film");
            }
            else if (category == "Music")
            {
                AddIndex("Music");
                AddIndex("Musica");
            }
            else if (category == "Videos")
            {
                AddIndex("Videos");
                AddIndex("Video");
            }
            else if (category == "Photos")
            {
                AddIndex("Photos");
                AddIndex("Foto");
            }
            else if (category == "Favourites")
            {
                list.AddRange(GetFavoritePathsSnapshot());
            }
            else if (category == "WatchHistory")
            {
                list.AddRange(WatchHistoryStore.LoadAll()
                    .Select(entry => entry.MediaPath)
                    .Where(IsDiaryEligiblePath));
            }
            else if (category == "Playlists")
            {
                var playlists = LoadJson<PlaylistModel>(PlaylistsPath);
                if (playlists?.Playlists != null)
                    list.AddRange(playlists.Playlists.Values.SelectMany(p => p.Items ?? new List<string>()));
            }

            return list;
        }

        private IEnumerable<string> AllKnownPaths()
        {
            var index = LoadLibraryIndex();
            return index.Categories.Values.SelectMany(x => x)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private bool PathBelongsToCurrentCategory(string path)
        {
            return _category switch
            {
                "Movies" => IsMoviePath(path),
                "TV Series" => IsTvEpisodePath(path) && IsVideoPath(path),
                "Videos" => IsVideoPath(path),
                "Music" => IsMusicPath(path),
                "Photos" => IsPhotoPath(path),
                "Favourites" => true,
                "WatchHistory" => IsDiaryEligiblePath(path),
                "Playlists" => true,
                _ => true
            };
        }

        private static bool IsDiaryEligiblePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            // Il Diario contiene solo film ed episodi. Musica, foto e pagine web
            // restano nelle rispettive sezioni anche se esiste un vecchio record.
            return IsVideoPath(path);
        }

        private string CategoryForPath(string path)
        {
            if (IsMusicPath(path)) return "Music";
            if (IsPhotoPath(path)) return "Photos";
            if (IsTvEpisodePath(path) && IsVideoPath(path)) return "TV Series";
            if (string.Equals(_category, "Videos", StringComparison.OrdinalIgnoreCase) && IsVideoPath(path)) return "Videos";
            if (IsVideoPath(path)) return "Movies";
            return _category;
        }

        private static string PlaybackCategoryForDisplayCategory(string? category)
        {
            return category switch
            {
                "Movies" => "Film",
                "TV Series" => "Serie",
                "Videos" => "Video",
                "Music" => "Musica",
                "Photos" => "Foto",
                _ => "Video"
            };
        }

        private bool IsMoviePath(string path)
        {
            if (!IsVideoPath(path))
                return false;
            if (IsTvEpisodePath(path))
                return false;
            return true;
        }

        private void Activate(HitZone hit)
        {
            switch (hit.Kind)
            {
                case HitKind.MusicTab:
                    _musicTab = hit.Index; _gridScroll = 0; Invalidate(); break;
                case HitKind.MusicResume:
                    if (hit.Item != null) OpenItem(hit.Item, ResumeSecondsFor(hit.Item.Path));
                    break;
                case HitKind.MusicResumePage:
                    _musicResumeOffset = hit.Index; Invalidate(); break;
                case HitKind.MusicResumePrevious:
                    _musicResumeOffset = Math.Max(0, _musicResumeOffset - _musicResumePageSize); Invalidate(); break;
                case HitKind.MusicResumeNext:
                    _musicResumeOffset += _musicResumePageSize; Invalidate(); break;
                case HitKind.HomeResumePage:
                    _resumeOffset = hit.Index; Invalidate(); break;
                case HitKind.Nav:
                    // Il cambio di tema sta nella barra laterale ma non e' una navigazione: dai grafici
                    // audio (o dai testi) non deve riportare alla libreria.
                    bool themeSwitch = string.Equals(hit.Key, "ThemeLight", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(hit.Key, "ThemeDark", StringComparison.OrdinalIgnoreCase);
                    if (_musicWorkspaceContent != null && !themeSwitch) MusicWorkspaceNavigationRequested?.Invoke();
                    ActivateNav(hit.Key);
                    break;
                case HitKind.Header:
                    ActivateHeader(hit.Key);
                    break;
                case HitKind.HeroResume:
                    if (hit.Item != null)
                        OpenItem(hit.Item, ResumeSecondsFor(hit.Item.Path));
                    break;
                case HitKind.HeroRestart:
                    if (hit.Item != null) OpenItem(hit.Item, 0);
                    break;
                case HitKind.HeroDetails:
                    if (hit.Item != null) ShowDetailOverlay(hit.Item);
                    break;
                case HitKind.HeroMore:
                    if (hit.Item != null) ShowHeroMoreMenu(hit.Item, hit.Bounds);
                    break;
                case HitKind.Continue:
                    if (hit.Resume != null)
                        OpenItem(hit.Resume.Item, hit.Resume.PositionSeconds > 0 ? hit.Resume.PositionSeconds : ResumeSecondsFor(hit.Resume.Item.Path));
                    break;
                case HitKind.Poster:
                    if (hit.Item != null)
                    {
                        if (_diaryRatingMode && string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase))
                        {
                            _diaryRatingMode = false;
                            _diaryRatingItem = hit.Item;
                            Invalidate();
                            break;
                        }
                        if (hit.Item.IsGroup && hit.Item.Children.Count > 0)
                            ShowGroupPicker(hit.Item);
                        else if (ShouldOpenDirectFromPoster(hit.Item))
                            OpenItem(hit.Item, ResumeSecondsFor(hit.Item.Path));
                        else
                            ShowDetailOverlay(hit.Item);
                    }
                    break;
                case HitKind.Prev:
                    _resumeOffset = Math.Max(0, _resumeOffset - 1);
                    Invalidate();
                    break;
                case HitKind.Next:
                    _resumeOffset = Math.Min(Math.Max(0, _resumeItems.Count - 1), _resumeOffset + 1);
                    Invalidate();
                    break;
                case HitKind.SeeAll:
                    ShowCategoryPage(_category, clearSearch: true);
                    break;
                case HitKind.LoadMore:
                    _photoVisibleLimit += PhotoPageSize;
                    RefreshContent();
                    break;
                case HitKind.GroupSelect:
                    SelectGroupChild(hit.Index);
                    if (_activeGroup != null && IsMusicSheetGroup(_activeGroup))
                        OpenSelectedGroupChild();
                    break;
                case HitKind.GroupPlay:
                    OpenSelectedGroupChild();
                    break;
                case HitKind.GroupQueue:
                    {
                        LibraryItem? selected = SelectedGroupChild();
                        if (selected != null)
                            QueueAddRequested?.Invoke(new List<string> { selected.Path });
                    }
                    break;
                case HitKind.GroupMore:
                    {
                        // Solo azioni che non hanno gia' un pulsante accanto ("Aggiungi alla coda"
                        // del brano e' il tasto a fianco): playlist e l'album intero in coda.
                        LibraryItem? selected = SelectedGroupChild();
                        var albumPaths = _activeGroup?.Children.Select(child => child.Path)
                            .Where(path => !string.IsNullOrWhiteSpace(path)).ToList() ?? new List<string>();
                        if (selected != null)
                            HUD.ChoicePopup.Show(this, new Point(hit.Bounds.Left,hit.Bounds.Bottom+6), new[] {
                                new HUD.ChoicePopup.Option(L("Aggiungi a playlist","Add to playlist"),()=>AddItemToPlaylist(selected.Path), Icon: "playlist-add"),
                                new HUD.ChoicePopup.Option(L("Aggiungi tutto l'album alla coda","Add the whole album to queue"),()=>{ if (albumPaths.Count > 0) QueueAddRequested?.Invoke(albumPaths); }, Icon: "queue-add")
                            }, width: 300);
                    }
                    break;
                case HitKind.GroupPlaylist:
                    {
                        LibraryItem? selected = SelectedGroupChild();
                        if (selected != null)
                            AddItemToPlaylist(selected.Path);
                    }
                    break;
                case HitKind.GroupFavorite:
                    {
                        LibraryItem? selected = SelectedGroupChild();
                        if (selected != null)
                            ToggleFavorite(selected.Path);
                    }
                    break;
                case HitKind.GroupClose:
                    CloseGroupPicker();
                    break;
                case HitKind.PlaylistSelect:
                    _playlistSelectionIndex = Math.Max(0, hit.Index);
                    Invalidate();
                    break;
                case HitKind.PlaylistPlay:
                    var playlist = BuildPlaylistViewItems().FirstOrDefault(item =>
                        string.Equals(item.Key, hit.Key, StringComparison.OrdinalIgnoreCase));
                    var playlistPaths = playlist?.Items
                        .Select(item => item.Path)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (playlistPaths?.Count > 0)
                        PlaylistPlayRequested?.Invoke(playlistPaths);
                    break;
                case HitKind.PlaylistDelete:
                    DeletePlaylist(hit.Key);
                    break;
                case HitKind.NetworkSelect:
                    _networkSelectionIndex = Math.Max(0, hit.Index);
                    Invalidate();
                    break;
                case HitKind.NetworkRefresh:
                    if (_networkContentLoading)
                        CancelNetworkBrowse();
                    else
                        RefreshNetworkServers(force: true);
                    break;
                case HitKind.NetworkRemove:
                    RemoveSelectedNetworkServer();
                    break;
                case HitKind.NetworkConnect:
                    ConnectSelectedNetworkServer();
                    break;
                case HitKind.NetworkAddJellyfin:
                    SignInToJellyfin(null);
                    break;
                case HitKind.NetworkFavorite:
                    ToggleNetworkServerFavorite(hit.Index);
                    break;
                case HitKind.QueueClose:
                    CloseQueueEditorOverlay();
                    break;
                case HitKind.QueueClear:
                    QueueClearRequested?.Invoke();
                    RefreshQueueEditorOverlay();
                    break;
                case HitKind.QueuePlay:
                    if (!string.IsNullOrWhiteSpace(hit.Key))
                    {
                        QueuePlayPathRequested?.Invoke(hit.Key);
                        RefreshQueueEditorOverlay();
                    }
                    break;
                case HitKind.QueueMoveUp:
                    if (!string.IsNullOrWhiteSpace(hit.Key))
                    {
                        QueueMoveRequested?.Invoke(hit.Key, -1);
                        RefreshQueueEditorOverlay();
                    }
                    break;
                case HitKind.QueueMoveDown:
                    if (!string.IsNullOrWhiteSpace(hit.Key))
                    {
                        QueueMoveRequested?.Invoke(hit.Key, +1);
                        RefreshQueueEditorOverlay();
                    }
                    break;
                case HitKind.QueueRemove:
                    if (!string.IsNullOrWhiteSpace(hit.Key))
                    {
                        QueueRemoveRequested?.Invoke(new List<string> { hit.Key });
                        RefreshQueueEditorOverlay();
                    }
                    break;
                case HitKind.DetailBack:
                    CloseDetailOverlay();
                    break;
                case HitKind.DetailPlay:
                    PlayDetailItem(resume: false);
                    break;
                case HitKind.DetailQueue:
                    if (_detailItem != null)
                        QueueAddRequested?.Invoke(new List<string> { _detailItem.Path });
                    break;
                case HitKind.DetailPlaylist:
                    if (_detailItem != null)
                        AddItemToPlaylist(_detailItem.Path);
                    break;
                case HitKind.DetailFavorite:
                    if (_detailItem != null)
                        ToggleFavorite(_detailItem.Path);
                    break;
                case HitKind.DetailCast:
                    _detailCastVisible = !_detailCastVisible;
                    if (_detailCastVisible && _detailItem != null)
                    {
                        _detailReviewsVisible = false;
                        _detailCastFirst = 0;
                        QueueRichDetailResolve(_detailItem);
                        QueueCastProfileDownloads(_detailItem.Path, _detailItem.CastMembers);
                        if (!_detailItem.RichDetailsResolved)
                            _contentLoadingTimer.Start();
                    }
                    Invalidate();
                    break;
                case HitKind.DetailReviews:
                    _detailReviewsVisible = !_detailReviewsVisible;
                    ResetReviewsPaging();
                    if (_detailReviewsVisible && _detailItem != null)
                    {
                        _detailCastVisible = false;
                        QueueRichDetailResolve(_detailItem);
                        if (!_detailItem.RichDetailsResolved)
                            _contentLoadingTimer.Start();
                    }
                    Invalidate();
                    break;
                case HitKind.DetailReviewsFilter:
                    if (!string.IsNullOrWhiteSpace(hit.Key)) SetReviewsFilter(hit.Key);
                    break;
                case HitKind.DetailReviewsPage:
                    GoToReviewsPage(hit.Index);
                    break;
                case HitKind.DetailReviewSource:
                    if (!string.IsNullOrWhiteSpace(hit.Key))
                        ExternalUrlRequested?.Invoke(hit.Key);
                    break;
                case HitKind.DetailCastPrevious:
                    MoveDetailCastCarousel(-1);
                    break;
                case HitKind.DetailCastNext:
                    MoveDetailCastCarousel(1);
                    break;
                case HitKind.PosterPlaylist:
                    if (hit.Item != null)
                        AddItemsToPlaylist(hit.Item.IsGroup ? hit.Item.Children.Select(t => t.Path) : new[] { hit.Item.Path });
                    break;
                case HitKind.MusicAlbumQueue:
                    if (hit.Item != null) QueueAddRequested?.Invoke(hit.Item.IsGroup ? hit.Item.Children.Select(t => t.Path).ToList() : new List<string> { hit.Item.Path });
                    break;
                case HitKind.PosterFavorite:
                    if (hit.Item != null) ToggleMusicFavorite(hit.Item);
                    break;
                case HitKind.DiaryRate:
                    if (!string.IsNullOrWhiteSpace(hit.Key))
                    {
                        SetDiaryRating(hit.Key, hit.Index);
                        _diaryRatingItem = null;
                        _diaryRatingMode = false;
                    }
                    break;
                case HitKind.DiaryClose:
                    _diaryRatingItem = null;
                    _diaryRatingMode = false;
                    Invalidate();
                    break;
                case HitKind.WebPaste:
                    PasteWebInputFromClipboard();
                    break;
                case HitKind.WebOpen:
                    OpenWebInput();
                    break;
                case HitKind.YouTubeVideo:
                    if (!string.IsNullOrWhiteSpace(hit.Key))
                    {
                        try
                        {
                            var video = _youtubeItems.FirstOrDefault(item => string.Equals(item.Url, hit.Key, StringComparison.OrdinalIgnoreCase));
                            if (video != null)
                                PlaybackTitleHints.Set(hit.Key, video.Title, "YouTube");
                        }
                        catch { }
                        OpenRequested?.Invoke(hit.Key);
                    }
                    break;
                case HitKind.YouTubeRefresh:
                    StartYouTubeSearchNow(_searchBox.Text);
                    break;
            }
        }

        private static bool ShouldOpenDirectFromPoster(LibraryItem item)
        {
            if (item.IsGroup)
                return false;
            return string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.Category, "Videos", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.PlaybackCategory, "Foto", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.PlaybackCategory, "Video", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Computer o Rete, come le due voci della barra laterale (usato dal telecomando sul telefono).</summary>
        /// <summary>
        /// Vero se il tasto destro, dove si trova ora il puntatore, apre un menu della libreria (rimuovi,
        /// correggi, filtri): in quel caso il menu del player non deve aprirsi sopra.
        /// </summary>
        /// <summary>Chi ospita la libreria mostra il menu in vetro; senza ospite si usa il riquadro semplice.</summary>
        internal Action<Point, IReadOnlyList<(string Text, string? Icon, Action Run)>>? ItemMenuPresenter { get; set; }

        private void ShowItemMenu(Point at, params (string Text, string? Icon, Action Run)[] entries)
        {
            if (ItemMenuPresenter != null)
            {
                try { ItemMenuPresenter(PointToScreen(at), entries); return; } catch { }
            }
            HUD.ChoicePopup.Show(this, at, entries.Select(entry => new HUD.ChoicePopup.Option(entry.Text, entry.Run, Icon: entry.Icon)).ToArray(), width: 280);
        }

        internal bool OwnsRightClickAtCursor()
        {
            try
            {
                if (!Visible || !IsHandleCreated) return false;
                var hit = HitTest(PointToClient(MousePosition));
                if (hit == null) return false;
                if (hit.Kind == HitKind.Header && hit.Key == "filter") return true;
                if (hit.Kind == HitKind.Continue && hit.Resume?.Item != null) return true;
                if (hit.Kind != HitKind.Poster) return false;
                return string.Equals(_category, "WatchHistory", StringComparison.OrdinalIgnoreCase)
                    ? !string.IsNullOrWhiteSpace(hit.Item?.Path ?? hit.Key)
                    : CanEditMetadata(hit.Item);
            }
            catch { return false; }
        }

        internal void SetLibrarySource(bool network) => ActivateNav(network ? "Network" : "Computer");
        internal bool RemoteNetworkSourceActive => IsNetworkSourceActive();

        private void ActivateNav(string key)
        {
            if (string.Equals(key, "ThemeLight", StringComparison.OrdinalIgnoreCase))
            {
                ThemeModeRequested?.Invoke(true);
                return;
            }

            if (string.Equals(key, "ThemeDark", StringComparison.OrdinalIgnoreCase))
            {
                ThemeModeRequested?.Invoke(false);
                return;
            }

            if (string.Equals(key, "Home", StringComparison.OrdinalIgnoreCase))
            {
                ShowHomePage();
                return;
            }

            if (IsCinematicCategoryKey(key))
            {
                if (string.Equals(NormalizeCategoryKey(key), "WatchHistory", StringComparison.OrdinalIgnoreCase))
                    _watchRatingsCache = null;
                _source = string.Equals(NormalizeCategoryKey(key), "WatchHistory", StringComparison.OrdinalIgnoreCase)
                    ? "Computer"
                    : (IsNetworkSourceActive() ? "Network" : "Computer");
                ShowCategoryPage(key, clearSearch: false);
                return;
            }

            if (string.Equals(key, "Computer", StringComparison.OrdinalIgnoreCase))
            {
                _source = "Computer";
                if (_view == PageView.Network)
                    ShowHomePage();
                else if (_view == PageView.Collection)
                    ShowCategoryPage(_category, clearSearch: false);
                else
                    ShowHomePage();
                return;
            }

            if (string.Equals(key, "Network", StringComparison.OrdinalIgnoreCase))
            {
                if (_networkItems.Count > 0 && !string.IsNullOrWhiteSpace(_networkConnectedServerKey))
                {
                    _source = "Network";
                    string category = IsCinematicCategoryKey(_category) && !string.Equals(_category, "Favourites", StringComparison.OrdinalIgnoreCase) && !string.Equals(_category, "Playlists", StringComparison.OrdinalIgnoreCase)
                        ? _category
                        : BestNetworkStartCategory(_networkItems);
                    ShowCategoryPage(category, clearSearch: false);
                    return;
                }
                ShowNetworkPage();
                return;
            }

            ExternalNavigationRequested?.Invoke(key);
        }

        private void ShowHomePage()
        {
            bool enteringHome = _view != PageView.Home;
            EndGlobalSearch();
            if (enteringHome) BeginPageCrossfade();
            int transitionVersion = ++_categoryTransitionVersion;
            ReleaseNativeSearchFocus();
            // La casella ricompare al primo disegno della nuova pagina, gia' allineata alla
            // sua barra: niente frame con la TextBox nella posizione/colore della pagina precedente.
            if (enteringHome)
            {
                _searchShellRect = Rectangle.Empty;
                try { _searchBox.Visible = false; } catch { }
            }
            CancelYouTubeWork();
            CloseGroupPicker(invalidate: false);
            _view = PageView.Home;
            // La Home e' quella della sorgente in uso: collegati a un server di rete resta la rete
            // (prima tornava sempre a Computer, e la Home non veniva mai costruita con i titoli del server).
            if (!(IsNetworkSourceActive() && _networkItems.Count > 0))
                _source = "Computer";
            _category = "Movies";
            _filter = "All";
            _resumeOffset = 0;
            ResetSmoothGridScroll();
            _photoVisibleLimit = PhotoPageSize;
            if (!string.IsNullOrEmpty(_searchBox.Text))
                _searchBox.Text = string.Empty;
            UpdateSearchPlaceholder();
            if (enteringHome)
            {
                _homeTransitionStartedUtc = DateTime.UtcNow;
                _homeTransitionContentReady = false;
                SetContentLoading(true, L("Home", "Home"));
                Invalidate();
            }
            RefreshContent();
            if (enteringHome)
                _ = FinishHomeTransitionAsync(transitionVersion);
        }

        public void NavigateToCategory(string? category)
        {
            string normalized = NormalizeCategoryKey(category);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            ShowCategoryPage(normalized, clearSearch: true);
        }

        public void NavigateHome()
        {
            ShowHomePage();
        }

        public void NavigateToSource(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return;
            string value = source.Trim();
            if (value.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("URL", StringComparison.OrdinalIgnoreCase))
            {
                ShowWebInputPage(value.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ? "YouTube" : "URL");
                return;
            }
            if (value.Equals("Rete domestica", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Network", StringComparison.OrdinalIgnoreCase))
            {
                ShowNetworkPage();
                return;
            }
            if (value.Equals("Il mio computer", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Computer", StringComparison.OrdinalIgnoreCase))
            {
                _source = "Computer";
                if (_view == PageView.Network)
                    ShowHomePage();
                else if (_view == PageView.Collection)
                    ShowCategoryPage(_category, clearSearch: false);
                else
                    ShowHomePage();
            }
        }

        // Two-argument form kept as is: the QA harness calls it by name and argument count.
        private void ShowCategoryPage(string category, bool clearSearch) => ShowCategoryPage(category, clearSearch, keepSearchFocus: false);

        private void ShowCategoryPage(string category, bool clearSearch, bool keepSearchFocus)
        {
            _homeTransitionStartedUtc = default;
            EndGlobalSearch();
            string nextCategory = NormalizeCategoryKey(category);
            bool categoryChanged = _view != PageView.Collection ||
                !string.Equals(_category, nextCategory, StringComparison.OrdinalIgnoreCase);
            if (categoryChanged) BeginPageCrossfade();
            int transitionVersion = ++_categoryTransitionVersion;
            // Chi sta scrivendo nella casella continua a scrivere: il cursore resta dov'e'.
            if (!keepSearchFocus) ReleaseNativeSearchFocus();
            // La casella ricompare al primo disegno della nuova pagina, gia' allineata alla
            // sua barra: niente frame con la TextBox nella posizione/colore della pagina precedente.
            if (categoryChanged && !keepSearchFocus)
            {
                _searchShellRect = Rectangle.Empty;
                try { _searchBox.Visible = false; } catch { }
            }
            CancelYouTubeWork();
            CloseGroupPicker(invalidate: false);
            _view = PageView.Collection;
            _source = IsNetworkSourceActive() ? "Network" : "Computer";
            _category = nextCategory;
            if (nextCategory == "Music")
                MusicEntered?.Invoke();
            _filter = "All";
            _resumeOffset = 0;
            ResetSmoothGridScroll();
            _photoVisibleLimit = PhotoPageSize;
            if (clearSearch && !string.IsNullOrEmpty(_searchBox.Text))
                _searchBox.Text = string.Empty;
            UpdateSearchPlaceholder();

            // Se la categoria è già stata costruita e i file sorgente non sono
            // cambiati, ripristinala direttamente. In particolare Video non deve
            // svuotarsi e ricomparire a ogni ingresso.
            string snapshotKey = BuildContentSnapshotKey();
            if (_contentSnapshots.TryGetValue(snapshotKey, out ContentSnapshot? readySnapshot) &&
                readySnapshot.IsComplete)
            {
                ApplyContentItems(readySnapshot.Items, selectHero: true);
                _visibleContentRouteKey = BuildContentRouteKey();
                ApplyContentAuxiliary(readySnapshot.Resume, readySnapshot.Stats);
                // Il contenuto è già pronto: una maschera di caricamento di 75 ms
                // faceva solo lampeggiare titolo e corpo della pagina (es. Playlist).
                SetContentLoading(false);
                Invalidate();
                return;
            }

            ApplyContentItems(Array.Empty<LibraryItem>(), selectHero: false);
            ApplyContentAuxiliary(Array.Empty<ResumeItem>(), Array.Empty<(string Icon, string Text)>());
            _heroItem = null;
            SetContentLoading(true, LoadingCategoryLabel(_category));
            Invalidate();
            RefreshContent();
        }

        private int _categoryTransitionVersion;

        private async Task FinishCachedCategoryTransitionAsync(int version, string category)
        {
            await Task.Delay(75);
            if (IsDisposed || Disposing || version != _categoryTransitionVersion ||
                _view != PageView.Collection || !string.Equals(_category, category, StringComparison.OrdinalIgnoreCase))
                return;
            SetContentLoading(false);
            Invalidate();
        }

        private void UpdateSearchPlaceholder()
        {
            try
            {
                _searchBox.TabStop = false;
                if (_searchBox.Focused && !IsNativeYouTubeView)
                    Focus();
                _searchBox.PlaceholderText = _category switch
                {
                    "Music" => L("Cerca brani, album, artisti...", "Search tracks, albums, artists..."),
                    "Photos" => L("Cerca foto, cartelle, date...", "Search photos, folders, dates..."),
                    "Videos" => L("Cerca video, clip, date...", "Search videos, clips, dates..."),
                    "TV Series" => L("Cerca serie, episodi, stagioni...", "Search series, episodes, seasons..."),
                    "Favourites" => L("Cerca nei preferiti...", "Search favourites..."),
                    "WatchHistory" => L("Cerca nel diario...", "Search diary..."),
                    "Playlists" => L("Cerca nelle playlist...", "Search playlists..."),
                    "Movies" when _view != PageView.Home => L("Cerca film, registi, attori...", "Search movies, directors, actors..."),
                    _ => L("Cerca in tutta la libreria...", "Search the whole library...")
                };
            }
            catch { }
        }

        private static bool IsCinematicCategoryKey(string? key)
        {
            string normalized = NormalizeCategoryKey(key);
            return normalized is "Movies" or "Videos" or "TV Series" or "Music" or "Photos" or "Favourites" or "WatchHistory" or "Playlists";
        }

        private static string NormalizeCategoryKey(string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return string.Empty;

            string value = category.Trim();
            return value switch
            {
                "Film" or "Movie" or "Movies" => "Movies",
                "Video" or "Videos" => "Videos",
                "Musica" or "Music" => "Music",
                "Foto" or "Photo" or "Photos" => "Photos",
                "Serie" or "Serie TV" or "TV" or "TV Series" => "TV Series",
                "Preferiti" or "Favourites" or "Favorites" => "Favourites",
                "Diario" or "Visti" or "Diary" or "WatchHistory" => "WatchHistory",
                "Playlist" or "Playlists" => "Playlists",
                _ => value
            };
        }

        private void ActivateHeader(string key)
        {
            if (key.StartsWith("also-in:", StringComparison.Ordinal))
            {
                OpenGlobalSearchSection(key["also-in:".Length..]);
            }
            else if (key == "diary-rate")
            {
                _diaryRatingMode = !_diaryRatingMode;
                _diaryRatingItem = null;
                Invalidate();
            }
            else if (key == "diary-ranking")
            {
                _diaryRatingMode = false;
                _diaryRatingItem = null;
                _filter = string.Equals(_filter, "TopRated", StringComparison.OrdinalIgnoreCase)
                    ? "All"
                    : "TopRated";
                ResetSmoothGridScroll();
                RefreshContent();
            }
            else if (key == "filter")
            {
                // Un clic passa al filtro successivo, come le scelte delle Impostazioni. I filtri con un
                // elenco (regista, decennio, genere, cartella) si aprono con il tasto destro.
                var filters = FilterOptionsForCurrentCategory();
                int index = -1;
                for (int i = 0; i < filters.Count; i++) if (filters[i] == _filter) index = i;
                _filter = filters[(index + 1) % filters.Count];
                ResetSmoothGridScroll();
                if (_category == "Photos") RefreshContent(); else Invalidate();
            }
            else if (key == "sort")
            {
                _sortMode = (_sortMode + 1) % 4;
                ResetSmoothGridScroll();
                RefreshContent();
            }
            else if (key == "scan")
            {
                ScanCurrentCategory();
            }
            else if (key == "new-playlist")
            {
                ShowCreatePlaylistOverlay();
            }
            else if (key == "network-servers")
            {
                ShowNetworkPage();
            }
            else if (key == "add")
            {
                AddSourceForCurrentCategory();
            }
        }

        /// <summary>A title picked on the phone: same item, hints and music queue as a click on its card.</summary>
        internal void OpenFromRemote(string path, string? title, double? resumeSeconds)
        {
            LibraryItem? item = null;
            try
            {
                if (!_itemCache.TryGetValue(path, out item) || item == null)
                    item = ToItem(path, allowMediaFileIo: false);
            }
            catch { }
            if (item != null)
            {
                OpenItem(item, resumeSeconds);
                return;
            }

            try { PlaybackTitleHints.Set(path, string.IsNullOrWhiteSpace(title) ? CleanTitle(Path.GetFileNameWithoutExtension(path) ?? path) : title, ResolvePlaybackCategoryForPath(path)); } catch { }
            if (resumeSeconds.HasValue && OpenWithResumeRequested != null)
                OpenWithResumeRequested(path, resumeSeconds.Value);
            else
                OpenRequested?.Invoke(path);
        }

        private void OpenItem(LibraryItem item, double? resumeSeconds)
        {
            if (TryOpenMusicQueue(item, resumeSeconds)) return;
            try { PlaybackTitleHints.Set(item.Path, item.Title, item.PlaybackCategory); } catch { }
            if (resumeSeconds.HasValue && OpenWithResumeRequested != null)
                OpenWithResumeRequested(item.Path, resumeSeconds.Value);
            else
                OpenRequested?.Invoke(item.Path);
        }

        private bool IsFavorite(string path)
        {
            try { return GetFavoritePathsSnapshot().Contains(path); }
            catch { return false; }
        }

        private HashSet<string> GetFavoritePathsSnapshot()
        {
            // Tutte le scritture ai preferiti passano da questa pagina e aggiornano la
            // cache. Evitiamo una chiamata al filesystem per ogni card e ogni frame.
            if (_favoritePathsCache != null)
                return _favoritePathsCache;

            long writeTicks;
            try { writeTicks = File.Exists(FavoritesPath) ? File.GetLastWriteTimeUtc(FavoritesPath).Ticks : 0; }
            catch { writeTicks = 0; }

            if (_favoritePathsCache != null && _favoritePathsCacheWriteTicks == writeTicks)
                return _favoritePathsCache;

            var model = LoadJson<FavoritesModel>(FavoritesPath) ?? new FavoritesModel();
            _favoritePathsCache = new HashSet<string>(model.Paths ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            _favoritePathsCacheWriteTicks = writeTicks;
            return _favoritePathsCache;
        }

        private void ToggleFavorite(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var model = new FavoritesModel
            {
                Paths = new HashSet<string>(GetFavoritePathsSnapshot(), StringComparer.OrdinalIgnoreCase)
            };
            if (!model.Paths.Add(path)) model.Paths.Remove(path);
            SaveJson(FavoritesPath, model);
            _favoritePathsCache = model.Paths;
            try { _favoritePathsCacheWriteTicks = File.GetLastWriteTimeUtc(FavoritesPath).Ticks; }
            catch { _favoritePathsCacheWriteTicks = long.MinValue; }
            if (string.Equals(_category, "Favourites", StringComparison.OrdinalIgnoreCase))
                RefreshContent();
            else
                Invalidate();
        }

        private static string CleanDetailSubtitle(LibraryItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.GroupSubtitle))
                return item.GroupSubtitle!;
            if (!string.IsNullOrWhiteSpace(item.Tagline))
                return item.Tagline!;
            if (item.Category is "Movies" or "TV Series")
            {
                // Titolo originale pulito (senza anno e tag di release), mai il nome file grezzo.
                try
                {
                    string parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(item.Path).NormalizedTitle ?? string.Empty;
                    return string.Equals(CleanTitle(parsed), CleanTitle(item.Title), StringComparison.OrdinalIgnoreCase) ? string.Empty : parsed;
                }
                catch { return string.Empty; }
            }
            try
            {
                string name = Path.GetFileNameWithoutExtension(item.Path);
                return string.IsNullOrWhiteSpace(name) ? item.PlaybackCategory : name;
            }
            catch { return item.PlaybackCategory; }
        }

        private static string VideoFormatLabel(string path)
        {
            try
            {
                string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                return string.IsNullOrWhiteSpace(ext) ? "-" : ext;
            }
            catch { return "-"; }
        }

        private static string CompactPath(string path, int max)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length <= max)
                return path;
            int tail = Math.Max(8, max - 3);
            return "..." + path.Substring(Math.Max(0, path.Length - tail));
        }

        private static string FileNameForDisplay(string path)
        {
            try
            {
                string name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? path : name;
            }
            catch { return path; }
        }

        // Dimensione e data del file compaiono nella scheda dettagli, che si ridisegna spesso:
        // chiederle al disco a ogni ridisegno (sei accessi, su M: via rete) rallentava la scheda.
        private static readonly Dictionary<string, (long Stamp, string Size, string Modified)> FileFacts = new(StringComparer.OrdinalIgnoreCase);

        private static (string Size, string Modified) FileFactsFor(string path)
        {
            long now = Environment.TickCount64;
            lock (FileFacts)
                if (FileFacts.TryGetValue(path ?? string.Empty, out var known) && now - known.Stamp < 60000)
                    return (known.Size, known.Modified);
            string size = ReadFileSize(path!), modified = ReadFileModified(path!);
            lock (FileFacts)
            {
                if (FileFacts.Count >= 512) FileFacts.Clear();
                FileFacts[path ?? string.Empty] = (now, size, modified);
            }
            return (size, modified);
        }

        private static string FormatFileSize(string path) => FileFactsFor(path).Size;
        private static string FormatFileModified(string path) => FileFactsFor(path).Modified;

        private static string ReadFileSize(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return "-";
                double bytes = new FileInfo(path).Length;
                string[] units = { "B", "KB", "MB", "GB", "TB" };
                int unit = 0;
                while (bytes >= 1024 && unit < units.Length - 1)
                {
                    bytes /= 1024;
                    unit++;
                }
                return $"{bytes:0.#} {units[unit]}";
            }
            catch { return "-"; }
        }

        private static string ReadFileModified(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return "-";
                return File.GetLastWriteTime(path).ToString("dd/MM/yyyy, HH:mm", CultureInfo.InvariantCulture);
            }
            catch { return "-"; }
        }

        private void AddSourceForCurrentCategory()
        {
            var model = LoadJson<RootModel>(RootsPath) ?? new RootModel();
            bool changed = false;
            // Scheda disegnata al 100% e ingrandita da Windows alla scala dello schermo, come tutte le altre.
            using var sheetLayout = SheetPresenter.Layout(this);
            using var dialog = new LibraryDialogForm
            {
                Text = L("Cartelle della libreria", "Library folders"),
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(640, 460),
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                BackColor = HUD.Theme.Sheet,
                ForeColor = HUD.Theme.Text,
                Font = LibraryFont("Segoe UI", 9.5f),
                KeyPreview = true
            };
            // Bordo di sistema dello stesso colore del fondo: Windows 11 altrimenti disegna un
            // contorno grigio attorno alle finestre arrotondate.
            WindowCorners.Apply(dialog, 12, border: dialog.BackColor);
            dialog.Paint += (_, e) =>
            {

                using var shape = Round(new Rectangle(1, 1, dialog.ClientSize.Width - 3, dialog.ClientSize.Height - 3), 12);
                using var border = new Pen(Color.FromArgb(86, HUD.Theme.Border), 1f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                // The compositor draws the outline when it rounds the window.
                if (!WindowCorners.SystemRounded(dialog))
                    { } // senza contorno

            };
            using var title = new Label
            {
                Text = L("Cartelle ", "Folders · ") + (AppLanguage.English ? LocalizedCategoryName(_category) : L("di ", "") + LocalizedCategoryName(_category).ToLowerInvariant()),
                Bounds = new Rectangle(36, 28, 568, 40),
                Font = LibraryFont("Segoe UI Semibold", 19f),
                ForeColor = HUD.Theme.Text,
                BackColor = Color.Transparent
            };
            using var sectionLabel = new Label
            {
                Text = L("I contenuti delle cartelle aggiunte appariranno nella libreria.", "Contents from these folders appear in your library."),
                AutoEllipsis = true,
                Bounds = new Rectangle(37, 72, 566, 26),
                Font = LibraryFont("Segoe UI", 10f),
                ForeColor = HUD.Theme.Muted,
                BackColor = Color.Transparent
            };
            using var dismiss = new BorderlessActionButton
            {
                CloseGlyph = true, AccessibleName = L("Chiudi", "Close"), Bounds = new Rectangle(748, 24, 40, 40),
                Font = LibraryFont("Segoe UI", 22f), ForeColor = HUD.Theme.Muted,
                BackColor = dialog.BackColor,
                DialogResult = DialogResult.OK, Cursor = Cursors.Hand, TabStop = false
            };
            using var listShell = new Panel
            {
                Bounds = new Rectangle(36, 112, 568, 252),
                BackColor = dialog.BackColor
            };
            using var list = new FolderSourceList
            {
                Bounds = new Rectangle(0, 0, 568, 252),
                BackColor = dialog.BackColor,
                ForeColor = HUD.Theme.Text,
                ItemHeight = 74
            };
            // Per ogni cartella: contenuti in libreria, spazio occupato, stato e spazio libero del
            // disco. Calcolato alla (ri)apertura dell'elenco, non a ogni disegno.
            var folderStats = new Dictionary<string, (int Count, long Bytes, bool Online, long Free)>(StringComparer.OrdinalIgnoreCase);
            var libraryFiles = _items.SelectMany(item => item.IsGroup ? item.Children : new List<LibraryItem> { item })
                .Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            void ComputeFolderStats()
            {
                folderStats.Clear();
                foreach (string root in list.Items)
                {
                    string prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    int count = 0; long bytes = 0;
                    foreach (string file in libraryFiles)
                    {
                        if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        count++;
                        try { bytes += new FileInfo(file).Length; } catch { }
                    }
                    bool online = false; long free = -1;
                    try { online = Directory.Exists(root); } catch { }
                    if (online)
                        try { free = new DriveInfo(Path.GetPathRoot(root) ?? root).AvailableFreeSpace; } catch { }
                    folderStats[root] = (count, bytes, online, free);
                }
            }
            static string Size(long bytes)
            {
                string[] units = { "B", "KB", "MB", "GB", "TB" };
                double value = bytes; int unit = 0;
                while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
                return value.ToString(unit >= 3 ? "0.#" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
            }
            string ItemsLabel(int n) => _category switch
            {
                "Movies" => n == 1 ? L("1 film", "1 movie") : string.Format(L("{0} film", "{0} movies"), n),
                "TV Series" => n == 1 ? L("1 episodio", "1 episode") : string.Format(L("{0} episodi", "{0} episodes"), n),
                "Music" => n == 1 ? L("1 brano", "1 track") : string.Format(L("{0} brani", "{0} tracks"), n),
                "Photos" => n == 1 ? L("1 foto", "1 photo") : string.Format(L("{0} foto", "{0} photos"), n),
                _ => n == 1 ? L("1 elemento", "1 item") : string.Format(L("{0} elementi", "{0} items"), n)
            };
            list.DrawItem += (_, e) =>
            {
                if (e.Index < 0 || e.Index >= list.Items.Count) return;
                using (var rowBackground = new SolidBrush(list.BackColor)) e.Graphics.FillRectangle(rowBackground, e.Bounds);
                bool selected = (e.State & DrawItemState.Selected) != 0;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                // Righe senza riquadro, senza fili: l'icona parte dal margine del titolo, la X arriva al margine destro.
                Rectangle row = Rectangle.Inflate(e.Bounds, 0, -3);
                Rectangle badge = new(row.Left, row.Top + (row.Height - 42) / 2, 42, 42);
                using (var badgeFill = new SolidBrush(Color.FromArgb(34, HUD.Theme.Accent))) e.Graphics.FillEllipse(badgeFill, badge);
                DrawIcon(e.Graphics, Rectangle.Inflate(badge, -11, -11), "folder", ControlPaint.Light(HUD.Theme.Accent, .25f));
                string path = list.Items[e.Index]?.ToString() ?? string.Empty;
                folderStats.TryGetValue(path, out var stats);
                // Punto di stato sul badge: verde raggiungibile, grigio scollegata.
                {
                    var dot = new Rectangle(badge.Right - 12, badge.Bottom - 12, 12, 12);
                    using var ring = new SolidBrush(list.BackColor);
                    using var fill = new SolidBrush(stats.Online ? Color.FromArgb(64, 200, 120) : Color.FromArgb(130, 136, 146));
                    e.Graphics.FillEllipse(ring, Rectangle.Inflate(dot, 2, 2));
                    e.Graphics.FillEllipse(fill, dot);
                }
                int statsWidth = 170;
                var statsRect = new Rectangle(list.RemoveBounds(e.Bounds).Left - 14 - statsWidth, row.Top, statsWidth, row.Height);
                using (var countFont = LibraryFont("Segoe UI Semibold", 10.5f))
                    TextRenderer.DrawText(e.Graphics, stats.Online || stats.Count > 0 ? ItemsLabel(stats.Count) : L("Non raggiungibile", "Unavailable"), countFont,
                        new Rectangle(statsRect.Left, row.Top + 15, statsRect.Width, 25), stats.Online ? HUD.Theme.Text : HUD.Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                string detail = stats.Online
                    ? (stats.Bytes > 0 ? Size(stats.Bytes) : "") + (stats.Free >= 0 ? (stats.Bytes > 0 ? "     " : "") + Size(stats.Free) + L(" liberi", " free") : "")
                    : L("Disco scollegato", "Drive disconnected");
                using (var detailFont = LibraryFont("Segoe UI", 9f))
                    TextRenderer.DrawText(e.Graphics, detail, detailFont,
                        new Rectangle(statsRect.Left, row.Top + 43, statsRect.Width, 20), HUD.Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                using var pathFont = LibraryFont("Segoe UI Semibold", 11.5f);
                using var kindFont = LibraryFont("Segoe UI", 9.5f);
                string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(name) || name.EndsWith(":")) name = L("Disco ", "Drive ") + path.TrimEnd(Path.DirectorySeparatorChar);
                TextRenderer.DrawText(e.Graphics, name, pathFont,
                    new Rectangle(row.Left + 58, row.Top + 15, Math.Max(1, statsRect.Left - row.Left - 74), 25),
                    HUD.Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, path, kindFont,
                    new Rectangle(row.Left + 58, row.Top + 43, Math.Max(1, statsRect.Left - row.Left - 74), 20), HUD.Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPadding);
                // Rimozione: la stessa piccola × delle righe della coda; cerchio solo al passaggio.
                {
                    var removeRect = list.RemoveBounds(e.Bounds);
                    bool hot = e.Index == list.HoverIndex && list.HoverOnRemove;
                    // La X solita delle schede, sempre con il suo cerchio: prima era una crocetta nuda, piccola e fuori asse.
                    HUD.Theme.DrawCloseButton(e.Graphics, removeRect, hot);
                }
            };
            using var empty = new Label
            {
                Text = L("Nessuna cartella ancora.\nAggiungi la cartella dove tieni i tuoi file: Cinecore li trova da solo.", "No folders yet.\nAdd the folder where you keep your files: Cinecore finds them for you."),
                Font = LibraryFont("Segoe UI", 10.5f),
                Bounds = new Rectangle(24, 88, 630, 52),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = HUD.Theme.Muted,
                BackColor = Color.Transparent,
                Visible = false
            };
            void RefreshRoots()
            {
                dialog.SuspendLayout();
                listShell.SuspendLayout();
                try
                {
                list.Items.Clear();
                foreach (string path in RootKeysForCategory(_category)
                    .Where(key => model.Roots.TryGetValue(key, out _))
                    .SelectMany(key => model.Roots[key] ?? new List<string>())
                    .Select(NormalizeRootPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                    list.Items.Add(path);
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                ComputeFolderStats();
                int totalItems = folderStats.Values.Sum(v => v.Count);
                long totalBytes = folderStats.Values.Sum(v => v.Bytes);
                sectionLabel.Text = list.Items.Count == 0
                    ? L("I contenuti delle cartelle aggiunte appariranno nella libreria.", "Contents from these folders appear in your library.")
                    : (list.Items.Count == 1 ? L("1 cartella", "1 folder") : string.Format(L("{0} cartelle", "{0} folders"), list.Items.Count))
                      + ", " + ItemsLabel(totalItems) + (totalBytes > 0 ? ", " + Size(totalBytes) : "");
                // La scheda e' alta quanto serve: una riga per cartella (prima ne riservava sempre due,
                // e con una sola cartella meta' scheda restava vuota).
                int listHeight = Math.Clamp(list.Items.Count, 1, 5) * 74 + 8;
                listShell.Height = list.Height = listHeight;
                dialog.ClientSize = new Size(640, 112 + listHeight + 88);
                foreach (var button in dialog.Controls.OfType<BorderlessActionButton>().Where(button => !button.CloseGlyph))
                    button.Top = dialog.ClientSize.Height - 68;
                empty.Bounds = new Rectangle(12, 12, 536, Math.Max(40, listHeight - 24));
                WindowCorners.Refresh(dialog, 12);
                empty.Visible = list.Items.Count == 0;
                if (list.Items.Count == 0) empty.BringToFront();
                }
                finally { listShell.ResumeLayout(true); dialog.ResumeLayout(true); list.Invalidate(); dialog.Invalidate(true); }
            }


            // Una sola azione piena ("Fatto"); aggiungere è un'azione testuale e ogni cartella
            // ha il suo cestino: il pulsante "Rimuovi" separato non serve più.
            // Le due azioni stanno insieme a destra: prima la secondaria, poi "Fatto".
            using var add = new BorderlessActionButton { Text = L("Aggiungi cartella", "Add folder"), IconKey = "folder-plus", Bounds = new Rectangle(270, 406, 202, 42) };
            using var remove = new BorderlessActionButton { Text = L("Rimuovi", "Remove"), Bounds = new Rectangle(474, 406, 144, 44), Visible = false };
            using var close = new BorderlessActionButton { Text = L("Fatto", "Done"), Bounds = new Rectangle(484, 406, 120, 42), DialogResult = DialogResult.OK };
            foreach (BorderlessActionButton button in new[] { add, remove, close })
            {
                button.ForeColor = HUD.Theme.Text;
                button.Font = LibraryFont("Segoe UI Semibold", 10.5f);
                button.BackColor = Color.FromArgb(13, 24, 34);
                button.HoverColor = Color.FromArgb(21, 37, 50);
                button.PressedColor = Color.FromArgb(27, 46, 61);
            }
            // Secondario: pillola tenue con icona, accanto al principale "Fatto".
            static Color Mix(Color a, Color b, double t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
            add.BackColor = Mix(dialog.BackColor, HUD.Theme.Text, .07);
            add.HoverColor = Mix(dialog.BackColor, HUD.Theme.Text, .12);
            add.PressedColor = Mix(dialog.BackColor, HUD.Theme.Text, .17);
            close.ForeColor = Color.White;
            remove.BackColor = dialog.BackColor;
            remove.HoverColor = Color.FromArgb(60, 27, 35);
            remove.PressedColor = Color.FromArgb(80, 31, 40);
            remove.ForeColor = HUD.Theme.Muted;
            close.BackColor = HUD.Theme.Accent;
            close.HoverColor = ControlPaint.Light(HUD.Theme.Accent, .12f);
            close.PressedColor = ControlPaint.Dark(HUD.Theme.Accent, .08f);
            dialog.Shown += (_, __) =>
            {
                try { dialog.Activate(); close.Focus(); } catch { }
            };

            listShell.Controls.Add(list);
            listShell.Controls.Add(empty);
            RefreshRoots();
            remove.Enabled = list.Items.Count > 0;
            list.SelectedIndexChanged += (_, __) => remove.Enabled = list.SelectedIndex >= 0;
            add.Click += (_, __) =>
            {
                using var picker = new FolderBrowserDialog
                {
                    Description = L($"Seleziona una cartella per {LocalizedCategoryName(_category)}.", $"Choose a folder for {LocalizedCategoryName(_category)}."),
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false,
                    RootFolder = Environment.SpecialFolder.MyComputer
                };
                if (picker.ShowDialog(dialog) != DialogResult.OK) return;
                string root = NormalizeRootPath(picker.SelectedPath);
                if (string.IsNullOrWhiteSpace(root)) return;
                if (!model.Roots.TryGetValue(_category, out List<string>? roots) || roots == null)
                    model.Roots[_category] = roots = new List<string>();
                if (!roots.Any(path => string.Equals(NormalizeRootPath(path), root, StringComparison.OrdinalIgnoreCase)))
                {
                    roots.Add(root);
                    changed = true;
                    RefreshRoots();
                }
            };
            list.RemoveRequested += index => { list.SelectedIndex = index; RemoveSelectedRoot(); };
            remove.Click += (_, __) => RemoveSelectedRoot();
            void RemoveSelectedRoot()
            {
                if (list.SelectedItem is not string selected) return;
                foreach (string key in RootKeysForCategory(_category).ToArray())
                {
                    if (!model.Roots.TryGetValue(key, out List<string>? roots) || roots == null) continue;
                    changed |= roots.RemoveAll(path => string.Equals(NormalizeRootPath(path), selected, StringComparison.OrdinalIgnoreCase)) > 0;
                    if (roots.Count == 0) model.Roots.Remove(key);
                }
                RefreshRoots();
                remove.Enabled = list.Items.Count > 0;
            }
            // Niente X: la scheda si chiude con "Fatto" (o Esc).
            dialog.Controls.AddRange(new Control[] { title, sectionLabel, listShell, add, remove, close });
            RefreshRoots();
            dialog.AcceptButton = close;
            dialog.CancelButton = close;
            SheetPresenter.ShowDialog(dialog, FindForm());
            if (!changed) return;
            SaveJson(RootsPath, model);
            ScanCurrentCategory();
        }

        private void ScanCurrentCategory()
        {
            if (IsNetworkSourceActive())
            {
                int connected = _networkServers.FindIndex(server => string.Equals(NetworkServerKey(server), _networkConnectedServerKey, StringComparison.OrdinalIgnoreCase));
                if (connected >= 0) _networkSelectionIndex = connected;
                ConnectSelectedNetworkServer(force: true);
                return;
            }
            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;
            string category = _category;

            Task.Run(() =>
            {
                var roots = GetRoots(category);
                var files = new List<string>();
                foreach (string root in roots)
                {
                    if (token.IsCancellationRequested)
                        return;

                    int discoveredAtRoot = 0;
                    int acceptedFromRoot = 0;
                    string normalizedRoot = NormalizeRootPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    foreach (string file in EnumerateFilesSafe(root, ExtensionsForCategory(category), token))
                    {
                        if (token.IsCancellationRequested)
                            return;
                        bool directChild = string.Equals(
                            (Path.GetDirectoryName(file) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            normalizedRoot,
                            StringComparison.OrdinalIgnoreCase);
                        if (directChild)
                            discoveredAtRoot++;
                        if (!ShouldIgnoreMediaPath(file) && PathBelongsToCategory(file, category))
                        {
                            files.Add(file);
                            if (directChild)
                                acceptedFromRoot++;
                        }
                    }
                    Dbg.Log($"[LIBRARY] Scan '{category}' root='{root}': file diretti trovati={discoveredAtRoot}, accettati={acceptedFromRoot}.", Dbg.LogLevel.Info);
                }

                if (token.IsCancellationRequested)
                    return;

                var index = LoadLibraryIndex();
                index.Categories[category] = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                SaveJson(IndexPath, index);
                if (category == "Music")
                {
                    InvalidateAudioTagIndex();
                    MusicLibraryChanged?.Invoke();
                }

                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(() =>
                        {
                            RefreshContent();
                            try { (FindForm() as PlayerForm)?.InvalidateRemoteLibrarySnapshot(); } catch { }
                        }));
                }
                catch { }
            }, token);
        }

        private List<string> GetRoots(string category)
        {
            var model = LoadJson<RootModel>(RootsPath) ?? new RootModel();
            var all = new List<string>();
            foreach (string key in RootKeysForCategory(category))
            {
                if (model.Roots.TryGetValue(key, out var roots) && roots != null)
                    all.AddRange(roots);
            }
            return all.Select(NormalizeRootPath).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> RootKeysForCategory(string category)
        {
            yield return category;
            if (category == "Music") yield return "Musica";
            else if (category == "Photos") yield return "Foto";
            else if (category == "Videos") yield return "Video";
            else if (category == "Movies") yield return "Film";
        }

        private static bool PathBelongsToCategory(string path, string category)
        {
            return category switch
            {
                "TV Series" => IsVideoPath(path) && IsTvEpisodePath(path),
                "Movies" => IsVideoPath(path) && !IsTvEpisodePath(path),
                "Videos" => IsVideoPath(path),
                "Music" => IsMusicPath(path),
                "Photos" => IsPhotoPath(path),
                "WatchHistory" => IsDiaryEligiblePath(path),
                _ => true
            };
        }

        private void LayoutSearchBox()
        {
            if ((_musicWorkspaceContent == null && _view == PageView.WebInput && !IsNativeYouTubeView) ||
                // La coda nasconde la ricerca solo se la copre: prima il testo spariva sempre.
                _detailItem != null || _activeGroup != null ||
                (_queueEditorVisible && _searchShellRect.IntersectsWith(QueueDrawerBaseBounds())) ||
                _playlistCreateOverlay?.Visible == true)
            {
                if (_searchBox.Visible)
                    _searchBox.Visible = false;
                return;
            }

            if (_searchShellRect.Width <= 0)
            {
                // Nessuna barra disegnata in questo frame: la TextBox nativa non deve restare
                // sospesa sopra il contenuto (appariva come un riquadro colorato).
                if (_searchBox.Visible && !_searchBox.Focused)
                    _searchBox.Visible = false;
                return;
            }

            // Durante la dissolvenza fra due pagine la casella nativa resterebbe sopra la pagina
            // vecchia, nella posizione di quella nuova: un rettangolo di un altro colore. Il testo
            // e' gia' dipinto nella pagina (DrawSearchShellText), quindi la casella puo' sparire
            // finche' la dissolvenza non e' finita senza che si noti.
            if (_pageFadeFrom != null && !_searchBox.Focused)
            {
                if (_searchBox.Visible)
                    _searchBox.Visible = false;
                return;
            }

            Rectangle textRect = SearchTextRect();
            if (_searchBox.Bounds != textRect)
                _searchBox.Bounds = textRect;
            if (_searchBox.BackColor != Chrome) _searchBox.BackColor = Chrome;
            if (!_searchBox.Visible) _searchBox.Visible = true;
        }

        private Rectangle SearchTextRect()
        {
            float scale = Math.Max(0.74f, Math.Min(1.90f, _searchShellRect.Height / 44f));
            // Un TextBox borderless disegna la baseline in cima alla propria area:
            // gonfiarne l'altezza lo faceva sembrare spostato verso l'alto su YouTube.
            // PreferredHeight includes native edit padding even without a border.
            // Centre the actual glyph line, with no invisible padding above it.
            int textHeight = _searchBox.Font.Height + 1;
            return new Rectangle(
                _searchShellRect.Left + S(scale, 38),
                _searchShellRect.Top + (_searchShellRect.Height - textHeight) / 2,
                Math.Max(S(scale, 20), _searchShellRect.Width - S(scale, 52)),
                textHeight);
        }

        /// <summary>Il testo della ricerca (o il suggerimento) dipinto nella pagina, identico a quello
        /// della casella nativa che gli sta sopra: quando la casella e' nascosta (dissolvenza fra
        /// pagine, istantanee) la barra non resta vuota e non cambia aspetto.</summary>
        private void DrawSearchShellText(Graphics g)
        {
            if (_searchShellRect.Width <= 0) return;
            try
            {
                Rectangle text = SearchTextRect();
                bool empty = string.IsNullOrEmpty(_searchBox.Text);
                // Stesso punto e stessi flag con cui WinForms disegna il suggerimento della TextBox.
                text.Offset(1, empty ? 1 : 0);
                TextRenderer.DrawText(g, empty ? _searchBox.PlaceholderText : _searchBox.Text, _searchBox.Font, text,
                    empty ? SystemColors.GrayText : TextMain, Chrome,
                    TextFormatFlags.NoPadding | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | (empty ? TextFormatFlags.EndEllipsis : TextFormatFlags.Default));
            }
            catch { }
        }

        private void ReleaseNativeSearchFocus()
        {
            try
            {
                _allowSearchFocus = false;
                _searchBox.TabStop = false;
                if (_searchBox.Focused)
                    FocusLibrarySurface();
                if (FindForm() is Form owner && ReferenceEquals(owner.ActiveControl, _searchBox))
                    owner.ActiveControl = this;
            }
            catch { }
        }

        private HitZone? HitTest(Point p)
        {
            var hits = _queueEditorVisible ? _queueHits : _hits;
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                if (hits[i].Bounds.Contains(p))
                    return hits[i];
            }
            return null;
        }

        private double? ResumeSecondsFor(string path)
        {
            return _resumeItems.FirstOrDefault(x => string.Equals(x.Item.Path, path, StringComparison.OrdinalIgnoreCase))?.PositionSeconds;
        }

        private string GridTitle() => LocalizedCategoryName(_category);

        private string LocalizedCategoryName(string? category) => category switch
        {
            "TV Series" => L("Serie TV", "TV Series"),
            "Videos" => L("Video", "Videos"),
            "Music" => L("Musica", "Music"),
            "Photos" => L("Foto", "Photos"),
            "Favourites" => L("Preferiti", "Favourites"),
            "WatchHistory" => L("Diario", "Diary"),
            "Playlists" => L("Playlist", "Playlists"),
            "Movies" => L("Film", "Movies"),
            _ => L("Altro", "Other")
        };

        private int TotalCountForVisibleLabel(int visibleCount)
        {
            if (string.Equals(_category, "Photos", StringComparison.OrdinalIgnoreCase))
            {
                return Math.Max(_photoTotalAvailable, visibleCount);
            }
            return visibleCount;
        }

        private string CollectionCountLabel(int count, int totalCount)
        {
            string noun = _category switch
            {
                "TV Series" => count == 1 ? L("stagione", "season") : L("stagioni", "seasons"),
                "Videos" => "video",
                "Music" => "album",
                "Photos" => count == 1 ? L("foto", "photo") : L("foto", "photos"),
                "Favourites" => count == 1 ? L("preferito", "favourite") : L("preferiti", "favourites"),
                "WatchHistory" => count == 1 ? L("titolo visto", "watched title") : L("titoli visti", "watched titles"),
                "Playlists" => count == 1 ? L("elemento playlist", "playlist item") : L("elementi playlist", "playlist items"),
                _ => count == 1 ? L("film", "movie") : L("film", "movies")
            };
            if (string.Equals(_category, "Photos", StringComparison.OrdinalIgnoreCase))
                return $"{totalCount:N0} {noun}";
            return $"{count:N0} {noun}";
        }

        private static bool UsesTemporalSections(string? category)
        {
            return string.Equals(category, "Photos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, "Videos", StringComparison.OrdinalIgnoreCase);
        }

        private string CategorySectionTitle(string? category)
        {
            return LocalizedCategoryName(category);
        }

        private List<TemporalSection> BuildTemporalSections(IReadOnlyList<LibraryItem> items)
        {
            if (_category == "Photos" && _sortMode is 1 or 3)
                return new() { new TemporalSection { Title = L("Tutte le foto", "All photos"), Items = ApplyActiveSort(items) } };
            var culture = UiEnglish ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("it-IT");
            var sections = items
                .GroupBy(item =>
                {
                    DateTime date = item.SortDateUtc == DateTime.MinValue ? DateTime.MinValue : item.SortDateUtc.ToLocalTime();
                    return date == DateTime.MinValue ? DateTime.MinValue : new DateTime(date.Year, date.Month, 1);
                })
                .OrderByDescending(group => group.Key)
                .Select(group =>
                {
                    string title = group.Key == DateTime.MinValue
                        ? L("Senza data", "Undated")
                        : Capitalize(group.Key.ToString("MMMM yyyy", culture), culture);
                    return new TemporalSection
                    {
                        Title = title,
                        SortDateUtc = group.Max(item => item.SortDateUtc),
                        Items = group.OrderByDescending(item => item.SortDateUtc).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList()
                    };
                })
                .ToList();
            if (_category == "Photos" && _sortMode == 2)
            {
                sections.Reverse();
                foreach (var section in sections) section.Items.Reverse();
            }
            return sections;
        }

        private static string Capitalize(string value, CultureInfo culture)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;
            string trimmed = value.Trim();
            return trimmed.Length == 1
                ? trimmed.ToUpper(culture)
                : char.ToUpper(trimmed[0], culture) + trimmed.Substring(1);
        }

        private IEnumerable<string> HeroChips(LibraryItem item)
        {
            if (item.IsGroup && !string.IsNullOrWhiteSpace(item.GroupKind))
                yield return LocalizedGroupKind(item.GroupKind);
            if (item.Year.HasValue) yield return item.Year.Value.ToString();
            if (item.DurationMinutes.HasValue && item.DurationMinutes.Value > 0) yield return FormatDuration(item.DurationMinutes.Value);
            yield return QualityLabel(item);
            if (!string.IsNullOrWhiteSpace(item.AudioLabel)) yield return item.AudioLabel!;
            if (!string.Equals(item.Category, "Movies", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Category, "Film", StringComparison.OrdinalIgnoreCase))
                yield return item.Category;
        }

        private string HeroText(LibraryItem item)
        {
            string value = CleanLine(item.Overview);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
            if (string.Equals(item.Category, "TV Series", StringComparison.OrdinalIgnoreCase))
                return L("Episodio dalla tua libreria. Aggiorna i metadati TMDb per mostrare una sinossi completa.", "Episode from your library. Refresh TMDb metadata to show a full synopsis.");
            if (string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase))
                return L("Brano dalla tua libreria musicale, pronto per la riproduzione con grafici audio e dati tecnici.", "Track from your music library, ready to play with audio charts and technical data.");
            if (string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase))
                return L("Elemento fotografico dalla tua libreria, pronto per la visualizzazione a schermo intero.", "Photo from your library, ready for full-screen viewing.");
            if (string.Equals(item.Category, "Videos", StringComparison.OrdinalIgnoreCase))
                return L("Video dalla tua libreria, pronto per la riproduzione.", "Video from your library, ready to play.");
            return L("Sinossi TMDb non ancora disponibile per questo titolo. Aggiorna i metadati per mostrare qui la descrizione completa.", "TMDb synopsis is not available for this title yet. Refresh metadata to show the full description here.");
        }

        private static string DurationText(LibraryItem item)
        {
            return item.DurationMinutes.HasValue && item.DurationMinutes.Value > 0 ? FormatDuration(item.DurationMinutes.Value) : string.Empty;
        }

        private static string CardMeta(LibraryItem item)
        {
            string year = item.Year.HasValue ? item.Year.Value.ToString() : FormatItemDate(item.SortDateUtc);
            if (item.DurationMinutes.HasValue && item.DurationMinutes.Value > 0)
                return $"{year} · {FormatDuration(item.DurationMinutes.Value)}";
            return year;
        }

        private static string CardMetaText(LibraryItem item)
        {
            if (item.IsGroup && !string.IsNullOrWhiteSpace(item.GroupSubtitle))
                return item.GroupSubtitle!;
            if (string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase))
                return FormatItemDate(item.SortDateUtc);
            if (string.Equals(item.Category, "Videos", StringComparison.OrdinalIgnoreCase))
            {
                string date = FormatItemDate(item.SortDateUtc);
                string duration = DurationText(item);
                return string.IsNullOrWhiteSpace(duration) ? date : $"{date} - {duration}";
            }

            string year = item.Year.HasValue ? item.Year.Value.ToString() : FormatItemDate(item.SortDateUtc);
            if (item.DurationMinutes.HasValue && item.DurationMinutes.Value > 0)
                return $"{year} - {FormatDuration(item.DurationMinutes.Value)}";
            return year;
        }

        private string QualityLabel(LibraryItem item)
        {
            if (item.IsGroup && !string.IsNullOrWhiteSpace(item.GroupKind))
                return LocalizedGroupKind(item.GroupKind);
            if (string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase))
                return "Photo";
            if (string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(item.AudioLabel) ? "Audio" : item.AudioLabel!;

            bool networkItem = IsNetworkPath(item.Path);
            string sample = item.Path + " " + item.Title + " " + item.ResolutionLabel;
            bool knownSdr = HasSdrToken(sample);
            string dynamicRange = item.IsHdr ? "HDR" : (networkItem && !knownSdr ? string.Empty : "SDR");
            string? resolution = NormalizeQualityLabel(item.ResolutionLabel);
            if (!string.IsNullOrWhiteSpace(resolution))
                return string.IsNullOrWhiteSpace(dynamicRange) ? resolution : $"{resolution} {dynamicRange}";
            if (item.Is4K)
                return string.IsNullOrWhiteSpace(dynamicRange) ? "4K" : $"4K {dynamicRange}";
            if (networkItem)
            {
                string origin = JellyfinClient.TryParseStream(item.Path, out _, out _, out _) ? "Jellyfin" : "DLNA";
                return string.IsNullOrWhiteSpace(dynamicRange) ? origin : $"{origin} {dynamicRange}";
            }
            return dynamicRange;
        }

        private static string? NormalizeQualityLabel(string? resolution)
        {
            if (string.IsNullOrWhiteSpace(resolution))
                return null;

            string value = resolution.Trim();
            if (Regex.IsMatch(value, @"(?<!\d)(?:2160p?|4k|uhd)(?![a-z0-9])", RegexOptions.IgnoreCase))
                return "4K";
            if (Regex.IsMatch(value, @"(?<!\d)1080p?(?![a-z0-9])", RegexOptions.IgnoreCase))
                return "Full HD";
            return value;
        }

        private static bool IsRawPixelResolution(string? resolution)
        {
            return !string.IsNullOrWhiteSpace(resolution) &&
                   Regex.IsMatch(resolution.Trim(), @"^\d{2,5}\s*[x×]\s*\d{2,5}$", RegexOptions.IgnoreCase);
        }

        private static bool IsQualityChip(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return text.Contains("HDR", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("SDR", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("4K", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Full HD", StringComparison.OrdinalIgnoreCase);
        }

        private string TemporalMetaText(LibraryItem item)
        {
            string date = FormatItemDate(item.SortDateUtc);
            if (string.Equals(item.Category, "Videos", StringComparison.OrdinalIgnoreCase))
            {
                string duration = DurationText(item);
                string quality = QualityLabel(item);
                var parts = new[] { date, duration, quality }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
                return string.Join(" - ", parts);
            }

            return string.IsNullOrWhiteSpace(date) ? FormatBytes(item.Bytes) : $"{date} - {FormatBytes(item.Bytes)}";
        }

        private static string FormatItemDate(DateTime utc)
        {
            if (utc == DateTime.MinValue)
                return "Data n/d";

            try
            {
                return utc.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
            }
            catch
            {
                return "Data n/d";
            }
        }

        private static string CleanLine(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return Regex.Replace(value.Trim(), "\\s+", " ");
        }

        // Il taglio con i puntini cerca la lunghezza giusta misurando il testo una decina di
        // volte: rifarlo a ogni ridisegno (sinossi, titoli lunghi) costava millisecondi per
        // scritta. Stesso testo, carattere e spazio danno lo stesso risultato: si ricorda.
        private static readonly Dictionary<(string Text, string Font, float FontSize, FontStyle Style, Size Size, TextFormatFlags Flags), string> FittedText = new();

        private static string FitTextWithEllipsis(string? value, Font font, Size size, TextFormatFlags flags)
        {
            if (string.IsNullOrWhiteSpace(value) || size.Width <= 0 || size.Height <= 0)
                return string.Empty;
            var key = (value, font.Name, font.Size, font.Style, size, flags);
            lock (FittedText)
                if (FittedText.TryGetValue(key, out string? known)) return known;
            string fitted = FitTextWithEllipsisCore(value, font, size, flags);
            lock (FittedText)
            {
                if (FittedText.Count >= 2048) FittedText.Clear();
                FittedText[key] = fitted;
            }
            return fitted;
        }

        private static string FitTextWithEllipsisCore(string? value, Font font, Size size, TextFormatFlags flags)
        {
            string text = CleanLine(value);
            if (string.IsNullOrWhiteSpace(text) || size.Width <= 0 || size.Height <= 0)
                return string.Empty;

            try
            {
                if (TextRenderer.MeasureText(text, font, size, flags).Height <= size.Height)
                    return text;

                const string ellipsis = "...";
                int lo = 0;
                int hi = text.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    string candidate = text.Substring(0, Math.Min(mid, text.Length)).TrimEnd() + ellipsis;
                    if (TextRenderer.MeasureText(candidate, font, size, flags).Height <= size.Height)
                        lo = mid;
                    else
                        hi = mid - 1;
                }

                if (lo <= 0)
                    return ellipsis;
                string result = text.Substring(0, Math.Min(lo, text.Length)).TrimEnd(' ', '.', ',', ';', ':', '-') + ellipsis;
                return result;
            }
            catch
            {
                return text.Length > 260 ? text.Substring(0, 257).TrimEnd() + "..." : text;
            }
        }


        private static string? InferAudioLabel(string hay)
        {
            string sample = hay ?? string.Empty;
            if (sample.Contains("atmos")) return "Dolby Atmos";
            if (sample.Contains("dts:x") || sample.Contains("dts-x") || sample.Contains("dtsx")) return "DTS:X";
            if (sample.Contains("truehd")) return "Dolby TrueHD";
            if (sample.Contains("dts-hd") || sample.Contains("dtshd") || sample.Contains("dts hd")) return "DTS-HD";
            if (sample.Contains("eac3") || sample.Contains("e-ac-3") || sample.Contains("ddp")) return "Dolby Digital+";
            if (sample.Contains("ac3") || sample.Contains("ac-3")) return "Dolby Digital";
            if (sample.Contains("dts")) return "DTS";
            if (Regex.IsMatch(sample, @"(?<!\d)7[._ ]1(?!\d)")) return "7.1";
            if (Regex.IsMatch(sample, @"(?<!\d)5[._ ]1(?!\d)")) return "5.1";
            if (Regex.IsMatch(sample, @"(?<!\d)2[._ ]0(?!\d)")) return "2.0";
            return null;
        }

        private static string? InferResolutionLabel(string hay)
        {
            string sample = hay ?? string.Empty;
            if (Has4KToken(sample))
                return "4K";
            if (Regex.IsMatch(sample, @"(?<!\d)1080p?(?![a-z0-9])", RegexOptions.IgnoreCase))
                return "Full HD";
            if (Regex.IsMatch(sample, @"(?<!\d)720p?(?![a-z0-9])", RegexOptions.IgnoreCase))
                return "720p";
            if (Regex.IsMatch(sample, @"(?<!\d)576p?(?![a-z0-9])", RegexOptions.IgnoreCase))
                return "576p";
            if (Regex.IsMatch(sample, @"(?<!\d)480p?(?![a-z0-9])", RegexOptions.IgnoreCase))
                return "480p";
            return null;
        }

        private static bool Has4KToken(string sample)
        {
            return !string.IsNullOrWhiteSpace(sample) &&
                   Regex.IsMatch(sample, @"(?<!\d)(?:2160p?|4k|uhd)(?![a-z0-9])", RegexOptions.IgnoreCase);
        }

        private static bool HasHdrToken(string sample)
        {
            return !string.IsNullOrWhiteSpace(sample) &&
                   Regex.IsMatch(sample, @"(?<![a-z0-9])(?:hdr10\+?|hdr|dolby[ ._-]?vision|dovi|dv)(?![a-z0-9])", RegexOptions.IgnoreCase);
        }

        private static bool HasSdrToken(string sample)
        {
            return !string.IsNullOrWhiteSpace(sample) &&
                   Regex.IsMatch(sample, @"(?<![a-z0-9])sdr(?![a-z0-9])", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Cached probe result for a video. With <paramref name="allowFileIo"/> off the entry
        /// is returned unvalidated: lightweight grid builds must not stat every file (on an
        /// external or network drive that cost several ms per title, on every visit).
        /// The next full build revalidates it against size and modification time.
        /// </summary>
        private static VideoQualityInfo? TryGetCachedVideoQuality(string path, bool allowFileIo = true, FileInfo? known = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            EnsureVideoQualityCacheLoaded();
            if (!allowFileIo)
            {
                lock (VideoQualityCacheSync)
                    return VideoQualityCache.TryGetValue(path, out var unvalidated) ? unvalidated : null;
            }

            DateTime writeUtc;
            long bytes;
            try
            {
                // One stat: FileInfo caches the attributes after the first query.
                var fi = known ?? new FileInfo(path);
                if (!fi.Exists)
                    return null;
                writeUtc = fi.LastWriteTimeUtc;
                bytes = fi.Length;
            }
            catch
            {
                return null;
            }

            lock (VideoQualityCacheSync)
            {
                if (VideoQualityCache.TryGetValue(path, out var cached) &&
                    cached.LastWriteUtc == writeUtc &&
                    cached.Bytes == bytes)
                {
                    return cached;
                }
            }

            return null;
        }

        private static VideoQualityInfo? TryProbeVideoQuality(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            EnsureVideoQualityCacheLoaded();

            DateTime writeUtc;
            long bytes;
            try
            {
                var fi = new FileInfo(path);
                writeUtc = fi.LastWriteTimeUtc;
                bytes = fi.Length;
            }
            catch
            {
                writeUtc = DateTime.MinValue;
                bytes = 0;
            }

            lock (VideoQualityCacheSync)
            {
                if (VideoQualityCache.TryGetValue(path, out var cached) &&
                    cached.LastWriteUtc == writeUtc &&
                    cached.Bytes == bytes)
                {
                    return cached;
                }
            }

            try
            {
                var probe = MediaProbe.Probe(path);
                if (probe == null || !probe.HasVideo)
                    return null;

                int longSide = Math.Max(probe.Width, probe.Height);
                int shortSide = Math.Min(probe.Width, probe.Height);
                bool is4K = longSide >= 3500 || shortSide >= 2000;
                string? resolution = is4K
                    ? "4K"
                    : (longSide >= 1900 || shortSide >= 1000
                        ? "Full HD"
                        : (longSide >= 1200 || shortSide >= 700
                            ? "HD"
                            : (probe.Width > 0 && probe.Height > 0
                                ? $"{probe.Width}x{probe.Height}"
                                : null)));

                var info = new VideoQualityInfo
                {
                    LastWriteUtc = writeUtc,
                    Bytes = bytes,
                    Resolution = resolution,
                    Is4K = is4K,
                    IsHdr = probe.IsHdr,
                    AudioLabel = AudioLabelFromProbe(probe),
                    HasAtmos = probe.AudioLooksObjectBased ||
                                (!string.IsNullOrWhiteSpace(probe.AudioCodecDisplayName) &&
                                 probe.AudioCodecDisplayName.Contains("Atmos", StringComparison.OrdinalIgnoreCase)),
                    DurationMinutes = probe.Duration > 0 ? probe.Duration / 60.0 : null
                };

                lock (VideoQualityCacheSync)
                    VideoQualityCache[path] = info;
                QueueVideoQualityCacheSave();
                return info;
            }
            catch
            {
                return null;
            }
        }

        // Resolution, HDR, audio and duration of each file survive a restart. They were kept in
        // memory only: every launch probed the whole library again (slowly on a network or cloud
        // drive), so durations and quality badges were missing until each probe finished.
        private static string VideoQualityIndexPath => Path.Combine(AppDataDir, "videoQualityIndex.json");
        private static bool VideoQualityCacheLoaded;
        private static System.Threading.Timer? VideoQualityCacheSaveTimer;

        private static void EnsureVideoQualityCacheLoaded()
        {
            if (Volatile.Read(ref VideoQualityCacheLoaded)) return;
            lock (VideoQualityCacheSync)
            {
                if (VideoQualityCacheLoaded) return;
                try
                {
                    var saved = LoadJson<Dictionary<string, VideoQualityInfo>>(VideoQualityIndexPath);
                    if (saved != null)
                        foreach (var pair in saved)
                            if (pair.Value != null && !VideoQualityCache.ContainsKey(pair.Key))
                                VideoQualityCache[pair.Key] = pair.Value;
                }
                catch { }
                Volatile.Write(ref VideoQualityCacheLoaded, true);
            }
        }

        private static void QueueVideoQualityCacheSave()
        {
            lock (VideoQualityCacheSync)
            {
                // Una sola scrittura dopo una raffica di analisi.
                VideoQualityCacheSaveTimer ??= new System.Threading.Timer(_ =>
                {
                    Dictionary<string, VideoQualityInfo> snapshot;
                    lock (VideoQualityCacheSync)
                        snapshot = new Dictionary<string, VideoQualityInfo>(VideoQualityCache, StringComparer.OrdinalIgnoreCase);
                    SaveJson(VideoQualityIndexPath, snapshot);
                }, null, Timeout.Infinite, Timeout.Infinite);
                VideoQualityCacheSaveTimer.Change(4000, Timeout.Infinite);
            }
        }

        private string LocalizedGroupKind(string? kind)
        {
            if (string.Equals(kind, "Season", StringComparison.OrdinalIgnoreCase))
                return L("Stagione", "Season");
            if (string.Equals(kind, "Album", StringComparison.OrdinalIgnoreCase))
                return "Album";
            return kind ?? string.Empty;
        }

        private static string? AudioLabelFromProbe(MediaProbe.Result? probe)
        {
            if (probe == null || probe.AudioCodec == 0)
                return null;

            string codec = FirstRawNonEmpty(probe.AudioCodecDisplayName, probe.AudioStreamTitle, probe.AudioCodec.ToString().Replace("AV_CODEC_ID_", string.Empty));
            string channels = AudioChannelsLabel(probe.AudioChannels, probe.AudioLooksObjectBased);

            var parts = new[] { codec, channels }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return parts.Count == 0 ? null : string.Join(" - ", parts);
        }

        private static string AudioChannelsLabel(int channels, bool objectBased)
        {
            if (channels <= 0)
                return objectBased ? "object based" : string.Empty;
            return channels switch
            {
                8 => "7.1",
                7 => "6.1",
                6 => "5.1",
                3 => "2.1",
                2 => "2.0",
                1 => "mono",
                _ => channels.ToString(CultureInfo.InvariantCulture) + " ch"
            };
        }

        private static string FormatDuration(double minutes)
        {
            int total = Math.Max(0, (int)Math.Round(minutes));
            int h = total / 60;
            int m = total % 60;
            return h > 0 ? $"{h}h {m:00}m" : $"{Math.Max(1, m)}m";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
                return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1)
            {
                v /= 1024;
                u++;
            }
            return u >= 3 ? $"{v:0.0} {units[u]}" : $"{v:0} {units[u]}";
        }

        private static string CleanTitle(string title)
        {
            string value = Regex.Replace((title ?? string.Empty).Trim(), "\\s+", " ");
            return string.IsNullOrWhiteSpace(value) ? "Untitled" : value;
        }

        private static int? SafeYear(FileInfo fi)
        {
            try
            {
                if (fi.LastWriteTime.Year is >= 1980 and <= 2100)
                    return fi.LastWriteTime.Year;
            }
            catch { }
            return null;
        }

        private static DateTime SafeSortDate(FileInfo fi)
        {
            try { return fi.LastWriteTimeUtc; } catch { return DateTime.MinValue; }
        }

        private static DateTime SafeSortDate(string path)
        {
            try { return new FileInfo(path).LastWriteTimeUtc; } catch { return DateTime.MinValue; }
        }

        private static long SafeLength(FileInfo fi)
        {
            try { return fi.Length; } catch { return 0; }
        }

        private static double? EstimateDurationMinutes(
            string path,
            bool allowShellDuration = true,
            IReadOnlyDictionary<string, PlaybackResumeStore.Entry>? resumeLookup = null,
            bool allowMediaFileIo = true)
        {
            try
            {
                PlaybackResumeStore.Entry? entry = null;
                if (resumeLookup != null)
                    resumeLookup.TryGetValue(path, out entry);
                else
                    entry = PlaybackResumeStore.Load(path);
                if (entry != null && entry.DurationSeconds > 0)
                    return entry.DurationSeconds / 60.0;
            }
            catch { }

            string title = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            double? runtime = TryReadDurationIndex(path);
            if (!runtime.HasValue && allowMediaFileIo)
                runtime = TryReadSidecarRuntimeMinutes(path);
            if (!runtime.HasValue && allowShellDuration)
                runtime = ShellDurationUtil.TryGetDurationMinutes(path);
            if (!runtime.HasValue)
                runtime = TryParseDurationFromText(title);

            return runtime.HasValue && runtime.Value > 0 ? runtime : null;
        }

        private static double? TryReadDurationIndex(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var index = LoadDurationIndexCache();
                if (index.TryGetValue(path, out double minutes) && minutes > 0)
                    return minutes;
            }
            catch { }

            return null;
        }

        private static Dictionary<string, double> LoadDurationIndexCache()
        {
            string file = Path.Combine(AppDataDir, "durationIndex.json");
            DateTime writeUtc = DateTime.MinValue;
            try
            {
                if (File.Exists(file))
                    writeUtc = File.GetLastWriteTimeUtc(file);
            }
            catch { }

            lock (DurationIndexCacheSync)
            {
                if (DurationIndexCache != null && DurationIndexWriteUtc == writeUtc)
                    return DurationIndexCache;

                var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(file))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                        if (doc.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var property in items.EnumerateObject())
                            {
                                if (property.Value.ValueKind == JsonValueKind.Number &&
                                    property.Value.TryGetDouble(out var minutes) &&
                                    minutes > 0)
                                {
                                    result[property.Name] = minutes;
                                }
                            }
                        }
                    }
                }
                catch { }

                DurationIndexCache = result;
                DurationIndexWriteUtc = writeUtc;
                return result;
            }
        }

        private static class ShellDurationUtil
        {
            private enum HResult : int
            {
                S_OK = 0
            }

            [Flags]
            private enum GetPropertyStoreFlags
            {
                GPS_BESTEFFORT = 0x40
            }

            [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IPropertyStore
            {
                HResult GetCount(out uint propertyCount);
                HResult GetAt(uint propertyIndex, out PropertyKey key);
                HResult GetValue(ref PropertyKey key, out PropVariant pv);
                HResult SetValue(ref PropertyKey key, ref PropVariant pv);
                HResult Commit();
            }

            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            private readonly struct PropertyKey
            {
                private readonly Guid _fmtid;
                private readonly uint _pid;

                public PropertyKey(Guid fmtid, uint pid)
                {
                    _fmtid = fmtid;
                    _pid = pid;
                }
            }

            [StructLayout(LayoutKind.Explicit, Pack = 1)]
            private struct PropVariant
            {
                [FieldOffset(0)] public ushort VarType;
                [FieldOffset(8)] public ulong ULongValue;
            }

            private enum VarEnum
            {
                VT_UI8 = 21
            }

            [DllImport("Shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern HResult SHGetPropertyStoreFromParsingName(
                string pszPath,
                IntPtr pbc,
                GetPropertyStoreFlags flags,
                ref Guid iid,
                [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

            [DllImport("ole32.dll")]
            private static extern int PropVariantClear(ref PropVariant pvar);

            private static readonly PropertyKey MediaDurationKey =
                new(new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);

            public static double? TryGetDurationMinutes(string path)
            {
                IPropertyStore? store = null;
                PropVariant pv = default;

                try
                {
                    Guid iid = typeof(IPropertyStore).GUID;
                    var hr = SHGetPropertyStoreFromParsingName(
                        path,
                        IntPtr.Zero,
                        GetPropertyStoreFlags.GPS_BESTEFFORT,
                        ref iid,
                        out store);

                    if (hr != HResult.S_OK || store == null)
                        return null;

                    var key = MediaDurationKey;
                    hr = store.GetValue(ref key, out pv);
                    if (hr != HResult.S_OK || pv.VarType != (ushort)VarEnum.VT_UI8 || pv.ULongValue == 0)
                        return null;

                    return (pv.ULongValue / 10000000.0) / 60.0;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    try { PropVariantClear(ref pv); } catch { }
                    if (store != null)
                    {
                        try { Marshal.ReleaseComObject(store); } catch { }
                    }
                }
            }
        }

        private static bool ResumeCompleted(PlaybackResumeStore.Entry entry)
        {
            if (entry.DurationSeconds <= 0)
                return false;
            double remaining = entry.DurationSeconds - entry.PositionSeconds;
            double threshold = Math.Min(12.0, Math.Max(5.0, entry.DurationSeconds * 0.03));
            return remaining <= threshold;
        }

        private static bool IsVideoPath(string path)
        {
            string ext = MediaPathExtension(path);
            return ext is ".mkv" or ".mp4" or ".m4v" or ".mov" or ".avi" or ".wmv" or ".webm" or ".flv" or ".m2ts" or ".ts" or ".iso";
        }

        private static bool IsMusicPath(string path)
        {
            string ext = MediaPathExtension(path);
            return ext is ".mp3" or ".flac" or ".mka" or ".aac" or ".ogg" or ".wav" or ".wma" or ".m4a" or ".opus" or ".dts" or ".ac3" or ".eac3";
        }

        private static bool IsPhotoPath(string path)
        {
            string ext = MediaPathExtension(path);
            return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tiff" or ".webp";
        }

        private static string MediaPathExtension(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            try
            {
                string probe = path;
                if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    probe = Uri.UnescapeDataString(uri.AbsolutePath);
                return (Path.GetExtension(probe) ?? string.Empty).ToLowerInvariant();
            }
            catch { return string.Empty; }
        }

        private static readonly Regex TvEpisodeRegex = new(
            @"(?:\bS\d{1,2}E\d{1,3}\b)|(?:\b\d{1,2}x\d{1,3}\b)|(?:\b(?:season|stagione)\s*\d{1,2}\b)|(?:\b(?:episode|episodio|ep)\s*\d{1,3}\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool IsTvEpisodePath(string path)
        {
            string sample = path.Replace(Path.DirectorySeparatorChar, ' ').Replace(Path.AltDirectorySeparatorChar, ' ').Replace('.', ' ').Replace('_', ' ');
            return TvEpisodeRegex.IsMatch(sample);
        }

        private static string[] ExtensionsForCategory(string category)
        {
            if (category is "Movies" or "TV Series")
                return new[] { ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m2ts", ".mts", ".m2v", ".ts", ".mpg", ".mpeg", ".vob", ".3gp", ".ogv", ".iso" };
            if (category == "Videos")
                return new[] { ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m2ts", ".mts", ".m2v", ".ts", ".mpg", ".mpeg", ".vob", ".3gp", ".ogv" };
            if (category == "Music")
                return new[] { ".mp3", ".flac", ".mka", ".aac", ".ogg", ".wav", ".wma", ".m4a", ".opus", ".dts", ".ac3", ".eac3" };
            if (category == "Photos")
                return new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".webp" };
            return Array.Empty<string>();
        }

        private static bool ShouldIgnoreMediaPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;
            try
            {
                string normalized = path.Replace('/', '\\');
                if (Regex.IsMatch(normalized, @"(?i)\\(?:sample(?:,screens)?|samples|screens?|screen ?caps?|trailers?|teasers?|extras?|featurettes?|proofs?)\\"))
                    return true;
                string name = Path.GetFileNameWithoutExtension(normalized) ?? string.Empty;
                if (Regex.IsMatch(name, @"(?ix)(?:^|[\s._\-\(\[])(sample|trailer|teaser|promo|clip|demo\s*disc)(?:$|[\s._\-\)\]])"))
                    return true;
                if (Regex.IsMatch(name, @"^\$[A-Z0-9]{5,}$", RegexOptions.IgnoreCase))
                    return true;
            }
            catch { }
            return false;
        }

        private static IEnumerable<string> EnumerateFilesSafe(string root, IEnumerable<string> extensions, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(root))
                yield break;

            var allowed = new HashSet<string>(extensions ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            bool any = allowed.Count == 0;

            var stack = new Stack<string>();
            if (File.Exists(root))
            {
                string ext = Path.GetExtension(root) ?? string.Empty;
                if (any || allowed.Contains(ext))
                    yield return root;
                yield break;
            }

            if (!Directory.Exists(root))
                yield break;

            // La root viene letta esplicitamente e per prima. Questo evita che i file
            // liberi sul disco dipendano dal buon esito della successiva ricorsione.
            string[] rootFiles;
            try { rootFiles = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                Dbg.Warn($"[LIBRARY] Lettura file nella root fallita '{root}': {ex.Message}");
                rootFiles = Array.Empty<string>();
            }
            foreach (string file in rootFiles)
            {
                if (ct.IsCancellationRequested)
                    yield break;
                string ext = Path.GetExtension(file) ?? string.Empty;
                if (any || allowed.Contains(ext))
                    yield return file;
            }

            string[] rootDirectories;
            try { rootDirectories = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                Dbg.Warn($"[LIBRARY] Lettura cartelle nella root fallita '{root}': {ex.Message}");
                rootDirectories = Array.Empty<string>();
            }
            foreach (string dir in rootDirectories)
            {
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch { continue; }
                stack.Push(dir);
            }

            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested)
                    yield break;

                string current = stack.Pop();
                // Materializza ogni livello dentro al try. EnumerateFiles/Directories e'
                // lazy: prima l'eccezione poteva uscire nel foreach e interrompere tutta
                // la scansione del disco (in particolare gia' sulla radice).
                IEnumerable<string> files = Array.Empty<string>();
                try { files = Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly); } catch { }
                foreach (string file in files)
                {
                    if (ct.IsCancellationRequested)
                        yield break;
                    string ext = Path.GetExtension(file) ?? string.Empty;
                    if (any || allowed.Contains(ext))
                        yield return file;
                }

                IEnumerable<string> dirs = Array.Empty<string>();
                try { dirs = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly); } catch { }
                foreach (string dir in dirs)
                {
                    try
                    {
                        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                            continue;
                    }
                    catch { continue; }
                    stack.Push(dir);
                }
            }
        }

        private static string NormalizeRootPath(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;
            string path = input.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (Regex.IsMatch(path, @"^[A-Za-z]:$"))
                path += Path.DirectorySeparatorChar;
            try
            {
                if (Path.IsPathRooted(path))
                    path = Path.GetFullPath(path);
            }
            catch { }
            return path;
        }

        private static T? LoadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                string json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<T>(json);
            }
            catch
            {
                return null;
            }
        }

        private static IndexModel LoadLibraryIndex()
        {
            DateTime writeUtc = DateTime.MinValue;
            try
            {
                if (File.Exists(IndexPath))
                    writeUtc = File.GetLastWriteTimeUtc(IndexPath);
            }
            catch { }

            lock (LibraryIndexCacheSync)
            {
                if (LibraryIndexCache != null && LibraryIndexWriteUtc == writeUtc)
                    return LibraryIndexCache;

                LibraryIndexCache = LoadJson<IndexModel>(IndexPath) ?? new IndexModel();
                LibraryIndexCache.Categories ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                LibraryIndexWriteUtc = writeUtc;
                return LibraryIndexCache;
            }
        }

        private static void SaveJson<T>(string path, T value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppDataDir);
                string json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch { }
        }

        private static Image? LoadFromFileNoLock(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var src = Image.FromStream(fs);
                return new Bitmap(src);
            }
            catch { return null; }
        }

        private static Rectangle CoverSource(Size image, Size viewport)
        {
            return CoverSource(image, viewport, 0.5f, 0.5f);
        }

        private static Rectangle CoverSource(Size image, Size viewport, float horizontalFocus, float verticalFocus)
        {
            if (image.Width <= 0 || image.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
                return new Rectangle(Point.Empty, image);

            horizontalFocus = Math.Max(0f, Math.Min(1f, horizontalFocus));
            verticalFocus = Math.Max(0f, Math.Min(1f, verticalFocus));

            double srcRatio = image.Width / (double)image.Height;
            double dstRatio = viewport.Width / (double)viewport.Height;
            if (srcRatio > dstRatio)
            {
                int w = Math.Max(1, (int)Math.Round(image.Height * dstRatio));
                int maxX = Math.Max(0, image.Width - w);
                int x = Math.Max(0, Math.Min(maxX, (int)Math.Round(maxX * horizontalFocus)));
                return new Rectangle(x, 0, Math.Min(w, image.Width - x), image.Height);
            }

            int h = Math.Max(1, (int)Math.Round(image.Width / dstRatio));
            int maxY = Math.Max(0, image.Height - h);
            int y = Math.Max(0, Math.Min(maxY, (int)Math.Round(maxY * verticalFocus)));
            return new Rectangle(0, y, image.Width, Math.Min(h, image.Height - y));
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            int max = Math.Max(1, Math.Min(Math.Max(1, r.Width), Math.Max(1, r.Height)) / 2);
            int rad = Math.Max(1, Math.Min(radius, max));
            int d = rad * 2;
            var gp = new GraphicsPath();
            gp.AddArc(r.Left, r.Top, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        private static void DrawStar(Graphics g, Rectangle r, Color color)
        {
            var pts = new List<PointF>();
            float cx = r.Left + r.Width / 2f;
            float cy = r.Top + r.Height / 2f;
            for (int i = 0; i < 10; i++)
            {
                double ang = -Math.PI / 2 + i * Math.PI / 5;
                double rad = i % 2 == 0 ? r.Width / 2.2 : r.Width / 4.7;
                pts.Add(new PointF(cx + (float)(Math.Cos(ang) * rad), cy + (float)(Math.Sin(ang) * rad)));
            }
            using var path = new GraphicsPath();
            path.AddPolygon(pts.ToArray());
            using var pen = new Pen(color, 1.6f);
            g.DrawPath(pen, path);
        }

        private static bool IsDescendant(Control root, Control c)
        {
            Control? p = c;
            while (p != null)
            {
                if (ReferenceEquals(p, root))
                    return true;
                p = p.Parent;
            }
            return false;
        }
    }
}
