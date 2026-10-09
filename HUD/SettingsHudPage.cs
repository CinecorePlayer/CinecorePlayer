#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    internal sealed partial class SettingsHudPage : UserControl
    {
        public event Action? CloseRequested;
        public event Action? ApplyRequested;
        public event Action<bool>? PreferBitstreamChanged;
        public event Action<string>? LanguageChanged;
        public event Action<string>? RendererChanged;
        public event Action<bool>? MpvRuntimeV3Changed;
        public event Action<string>? MpvOptionCycleRequested;
        public event Action<string, string>? MpvOptionSelected;
        public event Action<string, bool>? MpvToggleChanged;
        public event Action? MpvAdvancedOptionsRequested;
        public event Action<string>? ProviderPanelRequested;
        public event Action? NetflixOpenRequested;
        public event Action<bool>? NetflixHomeChanged;
        public event Action<bool>? SpotlightAtWindowsStartupChanged;
        public event Action<string>? SpotlightSourceChanged;
        public event Action<string>? SpotlightServerChanged;
        public event Action? SpotlightServerRefreshRequested;
        public event Action<string>? ThemeColorRequested;
        public event Action<Color>? AccentSelected;
        public event Action? ThemeResetRequested;
        public event Action? TmdbApiKeyRequested;
        public event Action? AmplifierRequested;
        public event Action? SpotifyCredentialsRequested;
        public event Action<string>? ThemeModeChanged;
        public event Action? UpdateCheckRequested;
        public event Action? ComponentsRequested;
        /// <summary>Opzioni di dimensionamento dell'immagine (condivise con il player) e avviso di modifica.</summary>
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public ImageSizingSettings? ImageSizing { get; set; }
        public event Action? ImageSizingChanged;
        /// <summary>"system", "light" or "dark".</summary>
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string ThemeMode { get; set; } = "system";

        private enum HitKind { Tab, Close, Apply, Bitstream, Language, AccentColor, ResetTheme, TmdbApiKey, SpotifyCredentials, Renderer, Runtime, NetflixHome, SpotlightAtWindowsStartup, SpotlightSource, SpotlightServer, OpenNetflix, MpvSection, MpvOption, MpvStep, MpvToggle, MpvAdvanced, ProviderPanel, GeneralChoice, GeneralAccent, Amplifier, CredentialSave, CredentialRemove, NativeChoice, NativeToggle, NativeMode, UpdateAuto, UpdateCheck }

        private sealed class Hit
        {
            public Rectangle Bounds;
            public HitKind Kind;
            public string Key = string.Empty;
        }

        private readonly List<Hit> _hits = new();
        private readonly Panel _providerSurface;
        private readonly Panel _providerPlaceholder;
        private readonly Label _providerPlaceholderTitle;
        private readonly Label _providerPlaceholderText;
        private DsPropPageHost _providerDsHost;
        private readonly Dictionary<string, DsPropPageHost> _providerHosts = new(StringComparer.OrdinalIgnoreCase);
        private readonly MadVrSettingsEmbedder _madVrSettingsHost;
        private Point _lastMouse = new(-1000, -1000);
        private Rectangle _contentViewport = Rectangle.Empty;
        private int _contentScroll;
        private int _contentScrollMax;
        private int _contentSurfaceRight;
        private Rectangle _settingsScrollbarTrack = Rectangle.Empty;
        private Rectangle _settingsScrollbarThumb = Rectangle.Empty;
        private bool _draggingSettingsScrollbar;
        private int _settingsScrollbarDragOffset;
        private string _tab = "Generali";
        private string _providerLoadedTab = string.Empty;
        private string _providerPendingTab = string.Empty;
        private bool _preferBitstream = true;
        private string _language = "it";
        private string _renderer = "Auto";
        private bool _mpvRuntimeV3;
        private string _mpvSection = "Essenziali";
        private bool _netflixHome;
        private bool _spotlightAtWindowsStartup;
        private string _spotlightSource = "Library";
        private string _spotlightServerKey = string.Empty;
        private readonly List<(string Key, string Name)> _spotlightServers = new();
        private Color _accentColor = Theme.Accent;
        private Color _accentSoftColor = Theme.AccentSoft;
        private Color _selectionColor = Theme.Selection;
        private Color _borderAccentColor = Theme.BorderAccent;
        private Color _panelColor = Theme.Panel;
        private Color _cardColor = Theme.Card;
        private Color _navColor = Theme.Nav;
        private string _introOutroStatus = "100% - 5/5 stagioni completate";
        private string _tmdbStatus = "TMDb: Attivo (chiave integrata)";
        private readonly Dictionary<string, string> _mpvValues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _mpvToggles = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<(string Path, int Size, int Argb), Bitmap> SvgIconCache = new();
        private static Image? CachedLogo;
        private static bool LogoLookupDone;

        private static Color Back => Theme.Panel;
        private static Color Border => Theme.Border;
        private static Color TextColor => Theme.Text;
        private static Color Muted => Theme.Muted;
        private static Color Accent => Theme.Accent;
        private static Color Selection => Theme.Selection;
        private bool UiEnglish => string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);
        private string L(string italian, string english) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(UiEnglish ? english : italian);

        // This page is entirely custom-drawn with pixel geometry. Point-sized fonts scale
        // a second time on high-DPI displays and used to overflow otherwise valid cards.
        private float _contentFontScale = 1f;
        private Font UiFont(string family, float pointSize, FontStyle style = FontStyle.Regular)
            => global::CinecorePlayer2025.AppFonts.Create(family, pointSize * _contentFontScale * (96f / 72f), style, GraphicsUnit.Pixel);

        public SettingsHudPage()
        {
            DoubleBuffered = true;
            BackColor = Back;
            Cursor = Cursors.Default;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _providerSurface = new Panel
            {
                Visible = false,
                BackColor = Theme.Panel,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _providerDsHost = new DsPropPageHost
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Visible = false
            };
            _madVrSettingsHost = new MadVrSettingsEmbedder
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Visible = false
            };
            _providerPlaceholder = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Visible = false
            };
            _providerPlaceholderTitle = new Label
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = TextColor,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 14.2f),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _providerPlaceholderText = new Label
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = Muted,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10.2f),
                TextAlign = ContentAlignment.TopCenter
            };
            _providerPlaceholder.Controls.Add(_providerPlaceholderText);
            _providerPlaceholder.Controls.Add(_providerPlaceholderTitle);
            _providerSurface.Controls.Add(_providerPlaceholder);
            _providerSurface.Controls.Add(_providerDsHost);
            _providerSurface.Controls.Add(_madVrSettingsHost);
            _providerSurface.Resize += (_, __) => LayoutProviderPlaceholder();
            Controls.Add(_providerSurface);
        }

        public void SetState(
            bool preferBitstream,
            string language,
            string renderer,
            bool mpvRuntimeV3,
            bool netflixHome,
            bool spotlightAtWindowsStartup,
            string spotlightSource,
            string spotlightServerKey,
            IReadOnlyList<(string Key, string Name)>? spotlightServers,
            string introOutroStatus,
            string tmdbStatus,
            IReadOnlyDictionary<string, string>? mpvValues = null,
            IReadOnlyDictionary<string, bool>? mpvToggles = null)
        {
            _preferBitstream = preferBitstream;
            _language = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
            _providerDsHost.SetLanguage(_language);
            foreach (var host in _providerHosts.Values) host.SetLanguage(_language);
            _madVrSettingsHost.SetLanguage(_language);
            _renderer = string.IsNullOrWhiteSpace(renderer) ? "Auto" : renderer;
            _mpvRuntimeV3 = mpvRuntimeV3;
            _netflixHome = netflixHome;
            _spotlightAtWindowsStartup = spotlightAtWindowsStartup;
            _spotlightSource = string.Equals(spotlightSource, "DLNA", StringComparison.OrdinalIgnoreCase) ? "DLNA" : "Library";
            _spotlightServerKey = spotlightServerKey?.Trim() ?? string.Empty;
            _spotlightServers.Clear();
            if (spotlightServers != null)
                _spotlightServers.AddRange(spotlightServers.Where(server => !string.IsNullOrWhiteSpace(server.Key)));
            _introOutroStatus = string.IsNullOrWhiteSpace(introOutroStatus) ? "-" : introOutroStatus;
            _tmdbStatus = string.IsNullOrWhiteSpace(tmdbStatus) ? "-" : tmdbStatus;
            _mpvValues.Clear();
            if (mpvValues != null)
            {
                foreach (var pair in mpvValues)
                    _mpvValues[pair.Key] = pair.Value;
            }
            _mpvToggles.Clear();
            if (mpvToggles != null)
            {
                foreach (var pair in mpvToggles)
                    _mpvToggles[pair.Key] = pair.Value;
            }
            Invalidate();
        }

        public void SetAccentColor(Color color)
        {
            _accentColor = Color.FromArgb(255, color.R, color.G, color.B);
            Invalidate();
        }

        public void SetThemeColors(Color accent, Color accentSoft, Color selection, Color borderAccent, Color panel, Color card, Color nav)
        {
            _accentColor = Color.FromArgb(255, accent.R, accent.G, accent.B);
            _accentSoftColor = Color.FromArgb(255, accentSoft.R, accentSoft.G, accentSoft.B);
            _selectionColor = Color.FromArgb(255, selection.R, selection.G, selection.B);
            _borderAccentColor = Color.FromArgb(255, borderAccent.R, borderAccent.G, borderAccent.B);
            _panelColor = Color.FromArgb(255, panel.R, panel.G, panel.B);
            _cardColor = Color.FromArgb(255, card.R, card.G, card.B);
            _navColor = Color.FromArgb(255, nav.R, nav.G, nav.B);
            BackColor = Back;
            try { _providerSurface.BackColor = Theme.Panel; } catch { }
            try { _providerDsHost.BackColor = Theme.Panel; } catch { }
            try { _madVrSettingsHost.BackColor = Theme.Panel; } catch { }
            try { _providerPlaceholder.BackColor = Theme.Panel; } catch { }
            try { _providerPlaceholderTitle.ForeColor = TextColor; } catch { }
            try { _providerPlaceholderText.ForeColor = Muted; } catch { }
            Invalidate();
        }

        public void OpenMpvSettings()
        {
            _tab = "MPV";
            _mpvSection = "Essenziali";
            HideProviderSurface();
            Invalidate();
        }

        public void OpenSubtitlesSettings()
        {
            _tab = "Sottotitoli";
            HideProviderSurface();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (UpdateEqDrag(e.Location)) { _lastMouse = e.Location; base.OnMouseMove(e); return; }
            if (_draggingSettingsScrollbar)
            {
                UpdateSettingsScrollFromThumb(e.Y - _settingsScrollbarDragOffset);
                Cursor = Cursors.Hand;
                base.OnMouseMove(e);
                return;
            }
            Hit? previousHit = _hits.LastOrDefault(hit => hit.Bounds.Contains(_lastMouse));
            _lastMouse = e.Location;
            Hit? currentHit = _hits.LastOrDefault(hit => hit.Bounds.Contains(_lastMouse));
            if (!ReferenceEquals(previousHit, currentHit))
            {
                if (previousHit != null) Invalidate(Rectangle.Inflate(previousHit.Bounds, 3, 3));
                if (currentHit != null) Invalidate(Rectangle.Inflate(currentHit.Bounds, 3, 3));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _lastMouse = new Point(-1000, -1000);
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (EqWheel(e.Location, e.Delta)) return; // rotella su un nodo del parametrico = Q
            Hit? spotlightSource = _hits.LastOrDefault(hit => hit.Kind == HitKind.SpotlightSource && hit.Bounds.Contains(e.Location));
            if (spotlightSource != null)
            {
                string direction = e.Delta > 0 ? "left" : "right";
                ChangeSpotlightSource(direction, spotlightSource.Bounds, e.Location);
                return;
            }
            Hit? spotlightServer = _hits.LastOrDefault(hit => hit.Kind == HitKind.SpotlightServer && hit.Bounds.Contains(e.Location));
            if (spotlightServer != null)
            {
                ChangeSpotlightServer(e.Delta > 0 ? "left" : "right");
                return;
            }
            bool overSettingsContent = _contentViewport.Contains(e.Location) || _settingsScrollbarTrack.Contains(e.Location) ||
                                       (e.Location.X >= SidebarWidth(ClientRectangle) && ClientRectangle.Contains(e.Location));
            if (_contentScrollMax > 0 && overSettingsContent)
            {
                int notches = Math.Max(1, Math.Abs(e.Delta) / Math.Max(1, SystemInformation.MouseWheelScrollDelta));
                int step = Math.Max(54, Height / 12) * notches;
                int next = Math.Max(0, Math.Min(_contentScrollMax, _contentScroll + (e.Delta < 0 ? step : -step)));
                if (next != _contentScroll)
                {
                    _contentScroll = next;
                    // Tutta la pagina: la barra sta fuori dall'area del contenuto e restava ferma.
                    Invalidate();
                }
                return;
            }

            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                base.OnMouseDown(e);
                return;
            }

            if (!_settingsScrollbarThumb.IsEmpty && _settingsScrollbarThumb.Contains(e.Location))
            {
                _draggingSettingsScrollbar = true;
                _settingsScrollbarDragOffset = e.Y - _settingsScrollbarThumb.Top;
                Capture = true;
                return;
            }
            if (!_settingsScrollbarTrack.IsEmpty && _settingsScrollbarTrack.Contains(e.Location))
            {
                _draggingSettingsScrollbar = true;
                _settingsScrollbarDragOffset = Math.Max(0, _settingsScrollbarThumb.Height / 2);
                Capture = true;
                UpdateSettingsScrollFromThumb(e.Y - _settingsScrollbarDragOffset);
                return;
            }

            var hit = _hits.LastOrDefault(h => h.Bounds.Contains(e.Location));
            if (hit == null)
            {
                base.OnMouseDown(e);
                return;
            }

            switch (hit.Kind)
            {
                case HitKind.Tab:
                    SelectTab(hit.Key);
                    break;
                case HitKind.Close:
                case HitKind.Apply:
                    // Azione al rilascio, come un pulsante vero: cosi lo stato premuto si vede.
                    _pressedAction = hit.Kind;
                    _pressedActionBounds = hit.Bounds;
                    Capture = true;
                    Invalidate(Rectangle.Inflate(hit.Bounds, 4, 4));
                    Update();
                    return;
                case HitKind.Bitstream:
                    PreferBitstreamChanged?.Invoke(!_preferBitstream);
                    break;
                case HitKind.Language:
                    LanguageChanged?.Invoke(hit.Key);
                    break;
                case HitKind.GeneralChoice:
                    ShowGeneralChoice(hit.Key, hit.Bounds, e.Location);
                    break;
                case HitKind.SpotlightSource:
                    ChangeSpotlightSource(hit.Key, hit.Bounds, e.Location);
                    break;
                case HitKind.SpotlightServer:
                    string serverDirection = string.Equals(hit.Key, "left", StringComparison.Ordinal) ||
                                             string.Equals(hit.Key, "right", StringComparison.Ordinal)
                        ? hit.Key
                        : e.Location.X < hit.Bounds.Left + hit.Bounds.Width / 2 ? "left" : "right";
                    ChangeSpotlightServer(serverDirection);
                    break;
                case HitKind.GeneralAccent:
                    if (int.TryParse(hit.Key, out int argb)) AccentSelected?.Invoke(Color.FromArgb(argb));
                    break;
                case HitKind.AccentColor:
                    ThemeColorRequested?.Invoke(string.IsNullOrWhiteSpace(hit.Key) ? "accent" : hit.Key);
                    break;
                case HitKind.ResetTheme:
                    ThemeResetRequested?.Invoke();
                    break;
                case HitKind.Amplifier:
                    AmplifierRequested?.Invoke();
                    break;
                case HitKind.UpdateAuto:
                    AppUpdate.State.AutoCheck = !AppUpdate.State.AutoCheck;
                    AppUpdate.SaveState();
                    Invalidate();
                    break;
                case HitKind.UpdateCheck:
                    UpdateCheckRequested?.Invoke();
                    break;
                case HitKind.TmdbApiKey:
                    TmdbApiKeyRequested?.Invoke();
                    break;
                case HitKind.SpotifyCredentials:
                    SpotifyCredentialsRequested?.Invoke();
                    break;
                case HitKind.CredentialSave:
                    SaveCredentials(hit.Key);
                    break;
                case HitKind.CredentialRemove:
                    RemoveCredentials(hit.Key);
                    break;
                case HitKind.Renderer:
                    RendererChanged?.Invoke(hit.Key);
                    break;
                case HitKind.Runtime:
                    MpvRuntimeV3Changed?.Invoke(string.Equals(hit.Key, "V3", StringComparison.OrdinalIgnoreCase));
                    break;
                case HitKind.NativeChoice:
                    ShowNativeChoice(hit.Key, hit.Bounds, e.Location);
                    break;
                case HitKind.NativeToggle:
                    ToggleNativeSetting(hit.Key);
                    break;
                case HitKind.NativeMode:
                    if (BeginEqDrag(hit.Key, e.Location)) return;
                    if (hit.Key.StartsWith("action:", StringComparison.Ordinal)) { RunNativeAction(hit.Key[7..]); break; }
                    if (hit.Key.StartsWith("group:", StringComparison.Ordinal)) SetNativeGroup(hit.Key[6..]);
                    else SetNativeMode(hit.Key == "original");
                    break;
                case HitKind.MpvSection:
                    _mpvSection = string.IsNullOrWhiteSpace(hit.Key) ? "Essenziali" : hit.Key;
                    Invalidate();
                    break;
                case HitKind.MpvOption:
                    ShowMpvOptionDropDown(hit.Key, hit.Bounds, e.Location);
                    break;
                case HitKind.MpvStep:
                    var parts = hit.Key.Split('|');
                    if (parts.Length == 2)
                    {
                        var choices = MpvChoices(parts[0]);
                        int currentIndex = Array.IndexOf(choices, MpvValue(parts[0]));
                        int index = Math.Clamp(Math.Max(0, currentIndex) + (parts[1] == "+" ? 1 : -1), 0, choices.Length - 1);
                        MpvOptionSelected?.Invoke(parts[0], choices[index]);
                    }
                    break;
                case HitKind.MpvToggle:
                    MpvToggleChanged?.Invoke(hit.Key, !MpvToggle(hit.Key));
                    break;
                case HitKind.MpvAdvanced:
                    MpvAdvancedOptionsRequested?.Invoke();
                    break;
                case HitKind.ProviderPanel:
                    ProviderPanelRequested?.Invoke(hit.Key);
                    break;
                case HitKind.NetflixHome:
                    _netflixHome = !_netflixHome;
                    Invalidate();
                    NetflixHomeChanged?.Invoke(_netflixHome);
                    break;
                case HitKind.SpotlightAtWindowsStartup:
                    _spotlightAtWindowsStartup = !_spotlightAtWindowsStartup;
                    Invalidate();
                    SpotlightAtWindowsStartupChanged?.Invoke(_spotlightAtWindowsStartup);
                    break;
                case HitKind.OpenNetflix:
                    NetflixOpenRequested?.Invoke();
                    break;
            }

            base.OnMouseDown(e);
        }

        private HitKind? _pressedAction;
        private Rectangle _pressedActionBounds;
        private DateTime _appliedUntil = DateTime.MinValue;
        private System.Windows.Forms.Timer? _appliedTimer;

        private void RunPressedAction(HitKind kind)
        {
            if (kind == HitKind.Close)
            {
                CloseRequested?.Invoke();
                return;
            }
            foreach (var host in _providerHosts.Values) host.Apply();
            ApplyRequested?.Invoke();
            _appliedUntil = DateTime.UtcNow.AddMilliseconds(1800);
            _appliedTimer ??= new System.Windows.Forms.Timer { Interval = 1850 };
            _appliedTimer.Tick -= OnAppliedTimerTick;
            _appliedTimer.Tick += OnAppliedTimerTick;
            _appliedTimer.Stop();
            _appliedTimer.Start();
            Invalidate();
        }

        private void OnAppliedTimerTick(object? sender, EventArgs e)
        {
            _appliedTimer?.Stop();
            Invalidate();
        }

        private bool AppliedFeedbackVisible => DateTime.UtcNow < _appliedUntil;

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (EndEqDrag()) { base.OnMouseUp(e); return; }
            if (_pressedAction is HitKind pressed)
            {
                _pressedAction = null;
                Capture = false;
                Invalidate(Rectangle.Inflate(_pressedActionBounds, 4, 4));
                if (_pressedActionBounds.Contains(e.Location))
                    RunPressedAction(pressed);
                base.OnMouseUp(e);
                return;
            }
            if (_draggingSettingsScrollbar)
            {
                _draggingSettingsScrollbar = false;
                Capture = false;
                Cursor = Cursors.Default;
                Invalidate();
                base.OnMouseUp(e);
                return;
            }
            base.OnMouseUp(e);
        }

        private void UpdateSettingsScrollFromThumb(int requestedTop)
        {
            if (_settingsScrollbarTrack.IsEmpty || _settingsScrollbarThumb.IsEmpty || _contentScrollMax <= 0)
                return;
            int travel = Math.Max(1, _settingsScrollbarTrack.Height - _settingsScrollbarThumb.Height);
            int top = Math.Clamp(requestedTop, _settingsScrollbarTrack.Top, _settingsScrollbarTrack.Top + travel);
            _contentScroll = (int)Math.Round((top - _settingsScrollbarTrack.Top) / (double)travel * _contentScrollMax);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            _hits.Clear();
            Rectangle b = ClientRectangle;
            if (b.Width <= 10 || b.Height <= 10)
                return;

            // Stessa struttura della libreria: sidebar sulla cornice scura, contenuto su una
            // superficie arrotondata sollevata. Nessuna linea di separazione.
            using (var fill = new SolidBrush(Theme.SidebarSurface))
                g.FillRectangle(fill, b);

            int sidebarW = SidebarWidth(b);
            Rectangle sidebar = new Rectangle(b.Left, b.Top, sidebarW, b.Height);
            float surfaceScale = Math.Clamp(Math.Min(b.Width / 1920f, b.Height / 1080f), .45f, 4f);
            int margin = Math.Max(6, (int)Math.Round(10 * surfaceScale));
            Rectangle content = new Rectangle(sidebar.Right, b.Top + margin, Math.Max(1, b.Right - sidebar.Right - margin), Math.Max(1, b.Height - margin * 2));
            using (var surface = Round(content, Math.Max(8, (int)Math.Round(14 * surfaceScale))))
            {
                using (var bg = new SolidBrush(Theme.Panel)) g.FillPath(bg, surface);
                using var rim = new Pen(Color.FromArgb(Theme.IsLight ? 36 : 20, Theme.IsLight ? Color.Black : Color.White), 1f);
                g.DrawPath(rim, surface);
            }

            DrawSidebar(g, sidebar);
            _contentFontScale = Math.Clamp(Math.Min(b.Width / 1920f, b.Height / 1080f), 1f, 1.28f);
            try { DrawContent(g, content); }
            finally { _contentFontScale = 1f; }
        }

        // Separatore sidebar/contenuto: niente linea piena da un capo all'altro, ma una
        // linea che sfuma alle estremita e una leggera ombra che sfuma verso il contenuto.
        internal static void DrawSoftDivider(Graphics g, int x, int top, int bottom)
        {
            int height = Math.Max(2, bottom - top);
            var edge = Color.FromArgb(Theme.IsLight ? 70 : 110, Theme.Border);
            using (var line = new LinearGradientBrush(new Rectangle(x - 1, top - 1, 1, height + 2), edge, edge, LinearGradientMode.Vertical))
            {
                line.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0, edge), edge, edge, Color.FromArgb(0, edge) },
                    Positions = new[] { 0f, .18f, .82f, 1f }
                };
                g.FillRectangle(line, new Rectangle(x - 1, top, 1, height));
            }
            using var shadow = new LinearGradientBrush(new Rectangle(x - 1, top, 20, height),
                Color.FromArgb(Theme.IsLight ? 14 : 34, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), LinearGradientMode.Horizontal);
            g.FillRectangle(shadow, new Rectangle(x, top, 18, height));
        }

        private void DrawSidebar(Graphics g, Rectangle r)
        {
            float brandScale = Math.Clamp(Math.Min(r.Width / 242f, r.Height / 1080f), .45f, 4f);
            int B(int v) => (int)Math.Round(v * brandScale);
            Rectangle logo = new Rectangle(r.Left + B(13), r.Top + B(10), r.Width - B(26), B(56));
            DrawBrandLogo(g, logo);

            string[] tabs =
            {
                "Generali",
                "MPV",
                "Sottotitoli",
                "Cinecore Audio",
                "madVR",
                "LAV Video",
                "LAV Audio",
                "MPC Video Renderer",
                "MPC Audio Renderer"
            };

            string[] icons = { "gear", "play", "cc", "equalizer", "grid", "film", "audio", "monitor", "wave" };
            int navTop = logo.Bottom + B(14);
            int navGap = B(4);
            int sectionH = B(22);
            int rowH = B(40);
            int rowPad = B(14);
            int y = navTop;
            for (int i = 0; i < tabs.Length; i++)
            {
                if (i == 0 || i == 4)
                {
                    // Sezioni separate dallo spazio, etichetta allineata alle icone.
                    if (i == 4) y += B(18);
                    string section = i == 0 ? L("PLAYER", "PLAYER") : L("COMPONENTI AVANZATI", "ADVANCED COMPONENTS");
                    using var sectionFont = UiFont("Segoe UI Semibold", Math.Max(7.6f, 8.2f * brandScale));
                    TextRenderer.DrawText(g, section, sectionFont,
                        new Rectangle(r.Left + rowPad + B(16), y, r.Width - rowPad * 2 - B(20), sectionH),
                        Color.FromArgb(Theme.IsLight ? 200 : 170, Theme.Muted), TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
                    y += sectionH + B(4);
                }

                Rectangle row = new Rectangle(r.Left + rowPad, y, r.Width - rowPad * 2, rowH);
                bool selected = string.Equals(_tab, tabs[i], StringComparison.OrdinalIgnoreCase);
                bool hover = row.Contains(_lastMouse);
                if (selected || hover)
                    Theme.DrawHighlight(g, row, selected, B(3));

                int iconSize = B(20);
                DrawTinyIcon(g, new Rectangle(row.Left + B(11), row.Top + (row.Height - iconSize) / 2, iconSize, iconSize), icons[i], selected ? Accent : hover ? TextColor : Theme.SubtleText);
                using var font = UiFont("Segoe UI", Math.Max(9.6f, 10.2f * brandScale));
                TextRenderer.DrawText(g, TabLabel(tabs[i]), font, new Rectangle(row.Left + B(42), row.Top, row.Width - B(54), row.Height), selected || hover ? TextColor : Theme.SubtleText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
                _hits.Add(new Hit { Bounds = row, Kind = HitKind.Tab, Key = tabs[i] });
                y += rowH + navGap;
            }

            int bottomY = r.Bottom - B(54);
            int sidePad = B(14);
            int buttonGap = B(8);
            int buttonW = Math.Max(1, (r.Width - sidePad * 2 - buttonGap) / 2);
            Rectangle close = new Rectangle(r.Left + sidePad, bottomY, buttonW, B(40));
            Rectangle apply = new Rectangle(close.Right + buttonGap, bottomY, buttonW, B(40));
            DrawActionButton(g, close, L("Chiudi", "Close"), buttonW < 112 ? string.Empty : "x", primary: false);
            DrawActionButton(g, apply, L("Applica", "Apply"), buttonW < 112 ? string.Empty : "check", primary: true);
            _hits.Add(new Hit { Bounds = close, Kind = HitKind.Close });
            _hits.Add(new Hit { Bounds = apply, Kind = HitKind.Apply });
        }

        private void DrawContent(Graphics g, Rectangle r)
        {
            HideCredentialInputs();
            _contentSurfaceRight = r.Right;
            r = CenterContentColumn(r);
            int contentPad = Math.Clamp(r.Width / 16, 16, 42);
            Rectangle top = new Rectangle(r.Left + contentPad, r.Top + 20, r.Width - contentPad * 2, 58);
            using var hFont = UiFont("Segoe UI Semibold", 22f);
            TextRenderer.DrawText(g, TabLabel(_tab), hFont, top, TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);

            Rectangle body = new Rectangle(r.Left + contentPad, r.Top + 80, r.Width - contentPad * 2, r.Height - 96);
            if (_tab == "Aspetto" || string.Equals(_tab, "Generali", StringComparison.OrdinalIgnoreCase))
            {
                HideProviderSurface();
                DrawScrollableSettingsContent(g, body, general: true);
            }
            else if (string.Equals(_tab, "MPV", StringComparison.OrdinalIgnoreCase))
            {
                HideProviderSurface();
                // Separatore fra il titolo "mpv" e le schede/opzioni sottostanti.
                using (var divider = new Pen(Color.FromArgb(70, Border)))
                    g.DrawLine(divider, body.Left, body.Top + 4, body.Right, body.Top + 4);
                body = new Rectangle(body.Left, body.Top + 20, body.Width, Math.Max(1, body.Height - 20));
                DrawScrollableSettingsContent(g, body, general: false);
            }
            else if (ShowingNativeProvider)
            {
                HideProviderSurface();
                DrawScrollableSettingsContent(g, body, general: false, native: true);
            }
            else
            {
                _contentViewport = Rectangle.Empty;
                _contentScroll = 0;
                _contentScrollMax = 0;
                if (HasNativeProviderPage(_tab))
                {
                    var back = new Rectangle(body.Left, body.Top, 230, 32);
                    DrawTextAction(g, back, L("‹  Impostazioni essenziali", "‹  Essential settings"), "");
                    _hits.Add(new Hit { Bounds = back, Kind = HitKind.NativeMode, Key = "native" });
                }
                DrawProviderPage(g, body);
            }
        }

        private static int SidebarWidth(Rectangle bounds)
        {
            float scale = Math.Clamp(Math.Min(bounds.Width / 1920f, bounds.Height / 1080f), .45f, 4f);
            return Math.Min(Math.Max(1, bounds.Width / 3), Math.Max(220, (int)Math.Round(242 * scale)));
        }

        private Rectangle CenterContentColumn(Rectangle content)
        {
            float scale = Math.Clamp(Math.Min(ClientSize.Width / 1920f, ClientSize.Height / 1080f), .85f, 1.4f);
            int width = Math.Min((int)Math.Round(980 * scale), content.Width);
            return new Rectangle(content.Left + (content.Width - width) / 2, content.Top, width, content.Height);
        }

        private static Rectangle ProviderHostBounds(Rectangle body)
            => new(body.Left, body.Top + 44, Math.Max(1, body.Width), Math.Max(1, body.Height - 44));

        private void DrawScrollableSettingsContent(Graphics g, Rectangle viewport, bool general, bool native = false)
        {
            int virtualHeight = native ? Math.Max(600, _nativeMeasuredHeight) : general
                ? (_tab == "Aspetto" ? 480 : Math.Max(1060, _generalMeasuredHeight))
                : _mpvSection switch { "Essenziali" => 980, "Video" => 1520, "HDR" => 1070, "Qualita" => 1320, "Audio/Sub" => 980, _ => 460 };
            virtualHeight = Math.Max(viewport.Height, virtualHeight);
            _contentViewport = viewport;
            _contentScrollMax = Math.Max(0, virtualHeight - viewport.Height);
            _contentScroll = Math.Max(0, Math.Min(_contentScrollMax, _contentScroll));

            int firstHit = _hits.Count;
            Point physicalMouse = _lastMouse;
            var graphicsState = g.Save();
            try
            {
                g.SetClip(viewport, CombineMode.Intersect);
                g.TranslateTransform(0, -_contentScroll);
                _lastMouse = new Point(physicalMouse.X, physicalMouse.Y + _contentScroll);
                Rectangle virtualBody = new Rectangle(viewport.Left, viewport.Top, viewport.Width, virtualHeight);
                if (native)
                    DrawNativeProviderRows(g, virtualBody);
                else if (general)
                    { if (_tab == "Aspetto") DrawAppearanceRows(g, virtualBody); else DrawGeneralRows(g, virtualBody); }
                else
                    DrawMpv(g, virtualBody);
            }
            finally
            {
                _lastMouse = physicalMouse;
                g.Restore(graphicsState);
            }

            for (int i = _hits.Count - 1; i >= firstHit; i--)
            {
                Rectangle adjusted = _hits[i].Bounds;
                adjusted.Offset(0, -_contentScroll);
                if (!adjusted.IntersectsWith(viewport))
                    _hits.RemoveAt(i);
                else
                    _hits[i].Bounds = Rectangle.Intersect(adjusted, viewport);
            }

            DrawSettingsScrollbar(g, viewport);
        }

        private void DrawSettingsScrollbar(Graphics g, Rectangle viewport)
        {
            if (_contentScrollMax <= 0 || viewport.Height <= 0)
            {
                _settingsScrollbarTrack = Rectangle.Empty;
                _settingsScrollbarThumb = Rectangle.Empty;
                return;
            }

            int trackX = Math.Max(viewport.Right + 12, _contentSurfaceRight - 30); // staccata dal bordo della superficie
            Rectangle track = new Rectangle(trackX, viewport.Top, 4, viewport.Height);
            int contentHeight = viewport.Height + _contentScrollMax;
            int thumbH = ScrollbarChrome.ThumbLength(viewport.Height, viewport.Height / (double)contentHeight);
            int thumbY = viewport.Top + (int)Math.Round((viewport.Height - thumbH) * (_contentScroll / (double)_contentScrollMax));
            _settingsScrollbarTrack = Rectangle.Inflate(track, 8, 0);
            _settingsScrollbarThumb = new Rectangle(_settingsScrollbarTrack.Left, thumbY, _settingsScrollbarTrack.Width, thumbH);
            ScrollbarChrome.Draw(g, track, thumbY, thumbH);
        }

        private void DrawMpv(Graphics g, Rectangle body)
        {
            DrawMpvSectioned(g, body);
        }
        private void DrawMpvSectioned(Graphics g, Rectangle body)
        {
            string[] sections = { "Essenziali", "Video", "HDR", "Qualita", "Audio/Sub", "Avanzate" };
            if (!sections.Any(s => string.Equals(s, _mpvSection, StringComparison.OrdinalIgnoreCase)))
                _mpvSection = "Essenziali";

            int sectionGap = body.Width < 620 ? 3 : 7;
            int sectionH = body.Width < 620 ? 32 : 34;
            int sectionW = Math.Max(58, (body.Width - sectionGap * (sections.Length - 1)) / sections.Length);
            int sx = body.Left;
            for (int i = 0; i < sections.Length; i++)
            {
                int w = i == sections.Length - 1 ? body.Right - sx : sectionW;
                Rectangle tab = new Rectangle(sx, body.Top, Math.Max(1, w), sectionH);
                bool selected = string.Equals(_mpvSection, sections[i], StringComparison.OrdinalIgnoreCase);
                using (var font = UiFont("Segoe UI", 10f))
                    TextRenderer.DrawText(g, MpvSectionLabel(sections[i]), font, tab, selected ? TextColor : Muted, SettingsText | TextFormatFlags.HorizontalCenter);
                using (var line = new Pen(selected ? Accent : Color.FromArgb(55, Border), selected ? 3 : 1))
                    g.DrawLine(line, tab.Left, tab.Bottom, tab.Right, tab.Bottom);
                _hits.Add(new Hit { Bounds = tab, Kind = HitKind.MpvSection, Key = sections[i] });
                sx += sectionW + sectionGap;
            }


            Rectangle content = new Rectangle(body.Left, body.Top + sectionH + 10, body.Width, Math.Max(1, body.Height - sectionH - 10));
            int gap = 10;
            int colGap = 40;
            int colCount = 1;
            int colW = Math.Max(320, (content.Width - colGap * (colCount - 1)) / colCount);
            Rectangle[] columns = new Rectangle[colCount];
            int[] ys = new int[colCount];
            for (int i = 0; i < colCount; i++)
            {
                int x = content.Left + i * (colW + colGap);
                int w = i == colCount - 1 ? content.Right - x : colW;
                columns[i] = new Rectangle(x, content.Top, Math.Max(1, w), content.Height);
                ys[i] = content.Top;
            }

            Rectangle Take(int preferredColumn, int height)
            {
                int col = colCount == 1 ? 0 : Math.Clamp(preferredColumn, 0, colCount - 1);
                Rectangle r = new Rectangle(columns[col].Left, ys[col], columns[col].Width, height);
                ys[col] += height + gap;
                return r;
            }

            int SectionHeight(int rows, bool split = false, int extra = 0)
            {
                return 68 + extra + rows * 56 + 16;
            }

            void Section(int preferredColumn, string title, string subtitle, string icon, bool split, params (string Label, string Key, bool Toggle)[] rows)
            {
                Rectangle card = Take(preferredColumn, SectionHeight(rows.Length, split));
                using var line = new Pen(Color.FromArgb(50, Border));
                if (card.Top > content.Top) g.DrawLine(line, card.Left, card.Top, card.Right, card.Top);
                using var heading = UiFont("Segoe UI Semibold", 9f);
                TextRenderer.DrawText(g, title.ToUpperInvariant(), heading, new Rectangle(card.Left, card.Top + 20, card.Width, 24), Muted, SettingsText);
                for (int i = 0; i < rows.Length; i++)
                {
                    var row = new Rectangle(card.Left, card.Top + 64 + i * 56, card.Width, 36);
                    if (rows[i].Toggle) DrawMpvToggleCell(g, row, rows[i].Label, rows[i].Key);
                    else DrawMpvOptionCell(g, row, rows[i].Label, rows[i].Key);
                }
            }

            void EngineCard(int preferredColumn)
            {
                Rectangle engine = Take(preferredColumn, SectionHeight(1, extra: 36));
                using var line = new Pen(Color.FromArgb(50, Border));
                if (engine.Top > content.Top) g.DrawLine(line, engine.Left, engine.Top, engine.Right, engine.Top);
                using var heading = UiFont("Segoe UI Semibold", 9f);
                using var label = UiFont("Segoe UI", 10.5f);
                TextRenderer.DrawText(g, L("MOTORE E PROFILO", "ENGINE AND PROFILE"), heading, new Rectangle(engine.Left,engine.Top+20,engine.Width,24),Muted,SettingsText);
                int controlW = Math.Min(330, Math.Max(210, engine.Width * 2 / 5));
                var runtime = new Rectangle(engine.Right-controlW,engine.Top+64,controlW,36);
                TextRenderer.DrawText(g,L("Versione MPV","MPV version"),label,new Rectangle(engine.Left,runtime.Top,engine.Width-controlW-28,36),TextColor,SettingsText);
                DrawValueChip(g,runtime,_mpvRuntimeV3 ? "V3 experimental" : "Standard",runtime.Contains(_lastMouse));
                _hits.Add(new Hit { Bounds = runtime, Kind = HitKind.GeneralChoice, Key = "runtime" });
                DrawMpvOptionCell(g,new Rectangle(engine.Left,engine.Top+120,engine.Width,36),L("Profilo","Profile"),"Profile");
            }

            if (string.Equals(_mpvSection, "Essenziali", StringComparison.OrdinalIgnoreCase))
            {
                EngineCard(0);
                Section(1, L("Output video", "Video output"), L("Backend, API grafica e decoder hardware.", "Backend, graphics API and hardware decoder."), "monitor", false,
                    ("VO", "VideoOutput", false),
                    ("GPU API", "GpuApi", false),
                    ("GPU context", "GpuContext", false),
                    ("HW decode", "Hwdec", false),
                    ("Video DR", "VideoLavcDr", true));

                Section(0, L("Cache e thread", "Cache and threads"), L("Buffering e decodifica.", "Buffering and decoding."), "settings", false,
                    ("Cache", "Cache", false),
                    ("Readahead", "DemuxerReadaheadSeconds", false),
                    ("Cache MB", "DemuxerMaxBytesMb", false),
                    ("Threads", "VideoThreads", false));
                return;
            }

            if (string.Equals(_mpvSection, "Video", StringComparison.OrdinalIgnoreCase))
            {
                Section(0, L("Output video", "Video output"), L("Backend, API grafica e decoder hardware.", "Backend, graphics API and hardware decoder."), "monitor", false,
                    ("VO", "VideoOutput", false),
                    ("GPU API", "GpuApi", false),
                    ("GPU context", "GpuContext", false),
                    ("HW decode", "Hwdec", false),
                    ("Video DR", "VideoLavcDr", true),
                    ("Deinterlace", "Deinterlace", true));

                Section(1, L("Sincronizzazione", "Synchronization"), L("Frame pacing e interpolazione.", "Frame pacing and interpolation."), "settings", false,
                    ("Sync", "VideoSync", false),
                    ("Interpolation", "Interpolation", true),
                    ("Interp. threshold", "InterpolationThreshold", false));

                Section(0, "Scaler", L("Algoritmi di resize e filtri antiringing.", "Resize algorithms and anti-ringing filters."), "grid", true,
                    ("Scale", "Scale", false),
                    ("Scale antiring", "ScaleAntiring", false),
                    ("CScale", "CScale", false),
                    ("CScale antiring", "CScaleAntiring", false),
                    ("DScale", "DScale", false),
                    ("DScale antiring", "DScaleAntiring", false),
                    ("TScale", "TScale", false),
                    ("Correct downscaling", "CorrectDownscaling", true),
                    ("Linear downscaling", "LinearDownscaling", true),
                    ("Sigmoid upscaling", "SigmoidUpscaling", true));
                return;
            }

            if (string.Equals(_mpvSection, "HDR", StringComparison.OrdinalIgnoreCase))
            {
                Section(0, "Tone mapping HDR", L("Mappa luminanza e recupero contrasto.", "Luminance mapping and contrast recovery."), "cube", false,
                    ("Tone mapping", "ToneMapping", false),
                    ("Tone map param", "ToneMappingParam", false),
                    ("Target peak", "TargetPeak", false),
                    ("HDR recovery", "HdrContrastRecovery", false),
                    ("HDR smoothness", "HdrContrastSmoothness", false),
                    ("HDR compute peak", "HdrComputePeak", true));

                Section(1, L("Colore", "Color"), L("Primarie, curva, gamut e output.", "Primaries, transfer curve, gamut and output."), "film", true,
                    ("Target prim", "TargetPrim", false),
                    ("Target TRC", "TargetTrc", false),
                    ("Gamut map", "GamutMappingMode", false),
                    ("Levels", "VideoOutputLevels", false),
                    ("FBO format", "FboFormat", false),
                    ("ICC profile auto", "IccProfileAuto", true),
                    ("Colorspace hint", "TargetColorspaceHint", true));
                return;
            }

            if (string.Equals(_mpvSection, "Qualita", StringComparison.OrdinalIgnoreCase))
            {
                Section(0, L("Deband e dither", "Deband and dither"), L("Banding, dithering e diffusione errore.", "Banding, dithering and error diffusion."), "cube", true,
                    ("Deband", "Deband", true),
                    ("Deband iter", "DebandIterations", false),
                    ("Deband threshold", "DebandThreshold", false),
                    ("Deband range", "DebandRange", false),
                    ("Deband grain", "DebandGrain", false),
                    ("Dither", "Dither", false),
                    ("Dither depth", "DitherDepth", false),
                    ("Fruit size", "DitherSizeFruit", false),
                    ("Error diffusion", "ErrorDiffusion", false),
                    ("Temporal dither", "TemporalDither", true));

                Section(1, L("Scaler fine", "Fine scaling"), L("Filtri di qualita e resize percettivo.", "Quality filters and perceptual resizing."), "grid", true,
                    ("Scale", "Scale", false),
                    ("CScale", "CScale", false),
                    ("DScale", "DScale", false),
                    ("TScale", "TScale", false),
                    ("Correct downscaling", "CorrectDownscaling", true),
                    ("Linear downscaling", "LinearDownscaling", true),
                    ("Sigmoid upscaling", "SigmoidUpscaling", true));
                return;
            }

            if (string.Equals(_mpvSection, "Avanzate", StringComparison.OrdinalIgnoreCase))
            {
                Rectangle advanced = Take(0, Math.Min(248, Math.Max(190, content.Height)));
                using var heading = UiFont("Segoe UI Semibold", 12.2f);
                using var description = UiFont("Segoe UI", 9f);
                TextRenderer.DrawText(g, L("Configurazione MPV completa", "Complete MPV configuration"), heading,
                    new Rectangle(advanced.Left, advanced.Top + 14, advanced.Width, 30), TextColor, SettingsText);
                TextRenderer.DrawText(g,
                    L("Aggiungi qualsiasi opzione supportata da libmpv, una per riga nel formato nome=valore.", "Add any option supported by libmpv, one per line as name=value."),
                    description, new Rectangle(advanced.Left, advanced.Top + 48, advanced.Width, 28), Muted, SettingsText);
                Rectangle edit = new Rectangle(advanced.Left, advanced.Top + 94, Math.Min(248, advanced.Width), 34);
                DrawTextAction(g, edit, L("Modifica opzioni libere", "Edit custom options"), "settings");
                _hits.Add(new Hit { Bounds = edit, Kind = HitKind.MpvAdvanced });

                using var noteFont = UiFont("Segoe UI", 8.4f);
                string note = L(
                    "Le opzioni libere vengono applicate dopo quelle visuali e possono quindi sovrascriverle. Sono accettati anche commenti che iniziano con #.",
                    "Custom options are applied after visual settings, so they can override them. Lines beginning with # are accepted as comments.");
                TextRenderer.DrawText(g, note, noteFont,
                    new Rectangle(advanced.Left, edit.Bottom + 18, advanced.Width, Math.Max(38, advanced.Bottom - edit.Bottom - 30)),
                    Muted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
                return;
            }

            Section(0, "Audio MPV", L("Canali, modalita esclusiva e volume interno.", "Channels, exclusive mode and internal volume."), "audio", false,
                ("Exclusive mode", "AudioExclusive", true),
                ("Channels", "AudioChannels", false),
                ("Normalize downmix", "AudioNormalizeDownmix", true),
                ("Volume max", "VolumeMax", false),
                ("Gapless audio", "GaplessAudio", false));

            Section(1, L("Sottotitoli", "Subtitles"), L("Caricamento automatico, ASS e stile testo.", "Auto-load, ASS and text style."), "cc", true,
                ("Sub auto", "SubAuto", false),
                ("ASS override", "SubAssOverride", false),
                ("Sub blend", "BlendSubtitles", false),
                ("Sub scale", "SubScale", false),
                ("Font size", "SubFontSize", false),
                ("Border", "SubBorderSize", false),
                ("Shadow", "SubShadowOffset", false));
        }

        private void DrawProviderPage(Graphics g, Rectangle body)
        {
            LayoutProviderSurface(ProviderHostBounds(body));
            if (_providerLoadedTab != _tab)
            {
                if (_providerPendingTab != _tab) ScheduleProviderLoad(_tab);
                using var font = UiFont("Segoe UI", 10f);
                TextRenderer.DrawText(g, L("Caricamento impostazioni…", "Loading settings…"), font,
                    new Rectangle(body.Left, body.Top + 44, body.Width, 42), Muted, SettingsText);
            }
        }

        private string MpvValue(string key)
            => _mpvValues.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : "auto";

        private bool MpvToggle(string key)
            => _mpvToggles.TryGetValue(key, out bool value) && value;

        private static string[] MpvChoices(string key) => key switch
        {
            "Profile" => new[] { "default", "gpu-hq", "fast", "low-latency" },
            "Hwdec" => new[] { "auto-safe", "auto", "d3d11va", "d3d11va-copy", "no" },
            "VideoOutput" => new[] { "gpu-next", "gpu", "libmpv", "auto" },
            "GpuApi" => new[] { "auto", "d3d11", "vulkan", "opengl" },
            "GpuContext" => new[] { "auto", "d3d11", "winvk", "wgl" },
            "VideoSync" => new[] { "audio", "display-resample", "display-vdrop", "display-adrop", "desync" },
            "ToneMapping" => new[] { "auto", "bt.2446a", "mobius", "hable", "reinhard", "clip" },
            "TargetPrim" => new[] { "auto", "bt.709", "bt.2020", "display-p3" },
            "TargetTrc" => new[] { "auto", "srgb", "gamma2.2", "pq", "hlg" },
            "TargetPeak" => new[] { "0", "100", "203", "400", "600", "1000" },
            "GamutMappingMode" => new[] { "auto", "clip", "perceptual", "relative", "saturation" },
            "BlendSubtitles" => new[] { "auto", "yes", "no", "video" },
            "FboFormat" => new[] { "auto", "rgba16f", "rgba32f", "rgb10_a2" },
            "VideoOutputLevels" => new[] { "auto", "limited", "full" },
            "Scale" => new[] { "ewa_lanczossharp", "ewa_lanczos", "spline36", "lanczos", "bicubic", "bilinear" },
            "ScaleAntiring" => new[] { "0", "0.3", "0.5", "0.7", "1" },
            "CScale" => new[] { "ewa_lanczossoft", "ewa_lanczos", "spline36", "bicubic", "bilinear" },
            "CScaleAntiring" => new[] { "0", "0.3", "0.5", "0.7", "1" },
            "DScale" => new[] { "mitchell", "catmull_rom", "lanczos", "bicubic", "bilinear" },
            "DScaleAntiring" => new[] { "0", "0.3", "0.5", "0.7", "1" },
            "TScale" => new[] { "oversample", "linear", "catmull_rom", "mitchell" },
            "ToneMappingParam" => new[] { "0", "0.25", "0.5", "0.75", "1", "1.5", "2" },
            "HdrContrastRecovery" => new[] { "0", "0.3", "0.5", "1", "2", "4" },
            "HdrContrastSmoothness" => new[] { "0", "25", "50", "75", "100" },
            "DebandIterations" => new[] { "0", "1", "2", "3", "4" },
            "DebandThreshold" => new[] { "0", "32", "48", "64", "96", "128" },
            "DebandRange" => new[] { "0", "16", "24", "32", "48" },
            "DebandGrain" => new[] { "0", "4", "8", "12", "16" },
            "Dither" => new[] { "auto", "ordered", "error-diffusion", "no" },
            "DitherDepth" => new[] { "auto", "8", "10", "no" },
            "DitherSizeFruit" => new[] { "2", "4", "6", "8" },
            "ErrorDiffusion" => new[] { "sierra-lite", "floyd-steinberg", "simple", "no" },
            "InterpolationThreshold" => new[] { "0", "0.001", "0.01", "0.05", "0.1" },
            "Cache" => new[] { "auto", "yes", "no" },
            "DemuxerReadaheadSeconds" => new[] { "0", "10", "30", "60", "120", "300" },
            "DemuxerMaxBytesMb" => new[] { "0", "64", "128", "256", "512", "1024" },
            "VideoThreads" => new[] { "0", "2", "4", "8", "16" },
            "SubAuto" => new[] { "no", "exact", "fuzzy", "all" },
            "SubAssOverride" => new[] { "no", "yes", "force", "strip" },
            "SubScale" => new[] { "0.75", "0.9", "1", "1.15", "1.3", "1.5", "2" },
            "SubFontSize" => new[] { "42", "48", "55", "62", "70", "84" },
            "SubBorderSize" => new[] { "0", "1.5", "2", "3", "4", "5" },
            "SubShadowOffset" => new[] { "0", "1", "2", "3", "4" },
            "AudioChannels" => new[] { "auto", "stereo", "5.1", "7.1" },
            "VolumeMax" => new[] { "100", "120", "150", "200" },
            "GaplessAudio" => new[] { "weak", "yes", "no" },
            _ => Array.Empty<string>()
        };

        private void ShowMpvOptionDropDown(string key, Rectangle source, Point click)
        {
            string[] choices = MpvChoices(key);
            if (choices.Length == 0)
            {
                MpvOptionCycleRequested?.Invoke(key);
                return;
            }

            int index = Array.FindIndex(choices, value => string.Equals(MpvValue(key), value, StringComparison.OrdinalIgnoreCase));
            int direction = click.X < source.Left + 30 ? -1 : 1;
            if (index < 0) index = direction > 0 ? -1 : 0;
            MpvOptionSelected?.Invoke(key, choices[(index + direction + choices.Length) % choices.Length]);
        }

        private void DrawMpvOptionCell(Graphics g, Rectangle row, string label, string key)
        {
            int chipW = Math.Min(330, Math.Max(210, row.Width * 2 / 5));
            int chipX = row.Right - chipW;
            int labelW = Math.Max(54, chipX - row.Left - 14);
            Rectangle chip = new Rectangle(chipX, row.Top + 1, chipW, row.Height - 2);
            bool hover = row.Contains(_lastMouse);
            if (hover)
                Theme.DrawHighlight(g, row, selected: false);
            using var labelFont = UiFont("Segoe UI", 10f);
            TextRenderer.DrawText(g, label, labelFont, new Rectangle(row.Left, row.Top, labelW, row.Height), hover ? TextColor : Theme.SubtleText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
            bool numeric = key is "DemuxerReadaheadSeconds" or "DemuxerMaxBytesMb" or "VideoThreads";
            if (numeric)
            {
                using var shape = Round(chip, 6); using var fill = new SolidBrush(Theme.Card); using var border = new Pen(Color.FromArgb(65, Border));
                g.FillPath(fill, shape); g.DrawPath(border, shape);
                var minus = new Rectangle(chip.Left + 4, chip.Top, 36, chip.Height);
                var plus = new Rectangle(chip.Right - 40, chip.Top, 36, chip.Height);
                var valueRect = new Rectangle(minus.Right + 4, chip.Top, Math.Max(1, plus.Left - minus.Right - 8), chip.Height);
                TextRenderer.DrawText(g, MpvValue(key) == "auto" ? "0" : MpvValue(key), labelFont, valueRect, TextColor, SettingsText | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, "−", labelFont, minus, Muted, SettingsText | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, "+", labelFont, plus, Muted, SettingsText | TextFormatFlags.HorizontalCenter);
                _hits.Add(new Hit { Bounds = valueRect, Kind = HitKind.MpvOption, Key = key });
                _hits.Add(new Hit { Bounds = minus, Kind = HitKind.MpvStep, Key = key + "|-" });
                _hits.Add(new Hit { Bounds = plus, Kind = HitKind.MpvStep, Key = key + "|+" });
            }
            else
            {
                DrawValueChip(g, chip, MpvValue(key), hover);
                _hits.Add(new Hit { Bounds = chip, Kind = HitKind.MpvOption, Key = key });
            }
        }

        private void DrawMpvToggleCell(Graphics g, Rectangle row, string label, string key)
        {
            bool selected = MpvToggle(key);
            int controlW = Math.Min(330, Math.Max(210, row.Width * 2 / 5));
            var control = new Rectangle(row.Right-controlW,row.Top,controlW,row.Height);
            using var font = UiFont("Segoe UI",10.5f);
            TextRenderer.DrawText(g,label,font,new Rectangle(row.Left,row.Top,row.Width-controlW-28,row.Height),TextColor,SettingsText);
            DrawSettingsSwitch(g,control,selected ? L("Attivo","Enabled") : L("Disattivato","Disabled"),selected);

            _hits.Add(new Hit { Bounds = row, Kind = HitKind.MpvToggle, Key = key });
        }

        private void DrawValueChip(Graphics g, Rectangle r, string value, bool hover)
        {
            using (var path = Round(r, 6))
            using (var fill = new SolidBrush(hover ? Theme.Nav : Theme.Card))
            using (var border = new Pen(hover ? Color.FromArgb(110, Accent) : Color.FromArgb(58, 78, 104, 132)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            using var font = UiFont("Segoe UI", 10f);
            TextRenderer.DrawText(g, value, font, new Rectangle(r.Left + 29, r.Top, Math.Max(1,r.Width - 58), r.Height), TextColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
            using var chevron = new Pen(Theme.Muted, 1.5f);
            int cy = r.Top + r.Height / 2;
            int left = r.Left + 15, right = r.Right - 15;
            g.DrawLines(chevron, new[] { new Point(left + 3, cy - 5), new Point(left - 2, cy), new Point(left + 3, cy + 5) });
            g.DrawLines(chevron, new[] { new Point(right - 3, cy - 5), new Point(right + 2, cy), new Point(right - 3, cy + 5) });
        }

        private bool RendererMatches(string key)
        {
            if (string.Equals(key, "Auto", StringComparison.OrdinalIgnoreCase))
                return string.Equals(_renderer, "Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(_renderer);
            return string.Equals(_renderer, key, StringComparison.OrdinalIgnoreCase);
        }

        private void DrawActionButton(Graphics g, Rectangle r, string text, string icon, bool primary)
        {
            bool hover = r.Contains(_lastMouse);
            bool pressed = _pressedAction != null && _pressedActionBounds == r && hover;
            if (pressed) r = Rectangle.Inflate(r, -1, -1);
            if (primary && AppliedFeedbackVisible && text == L("Applica", "Apply"))
            {
                text = L("Applicato", "Applied");
                icon = "check";
            }
            using (var path = Round(r, 7))
            using (Brush fill = primary
                ? new SolidBrush(pressed ? ControlPaint.Dark(Accent, .08f) : hover ? ControlPaint.Light(Accent, .12f) : Accent)
                : new SolidBrush(pressed ? Color.FromArgb(70, Accent) : hover ? Color.FromArgb(Theme.IsLight ? 30 : 42, TextColor) : Theme.Card))
            using (var border = new Pen(primary ? Color.FromArgb(160, Accent) : Color.FromArgb(72, 92, 116, 140)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using var font = UiFont("Segoe UI Semibold", 9.8f);
            int textW = TextRenderer.MeasureText(g,text,font,Size.Empty,TextFormatFlags.NoPadding).Width;
            int iconW = string.IsNullOrWhiteSpace(icon) ? 0 : 26;
            int x = r.Left + Math.Max(6,(r.Width-textW-iconW)/2);
            if(iconW>0) DrawTinyIcon(g,new Rectangle(x,r.Top+(r.Height-18)/2,18,18),icon,Color.White);
            TextRenderer.DrawText(g,text,font,new Rectangle(x+iconW,r.Top,Math.Max(1,r.Right-x-iconW-6),r.Height),primary?Color.White:TextColor,SettingsText);
        }

        private void DrawTextAction(Graphics g, Rectangle r, string label, string icon)
        {
            bool hover = r.Contains(_lastMouse);
            using var font = UiFont("Segoe UI Semibold", 9.8f);
            int iconWidth = string.IsNullOrWhiteSpace(icon) ? 0 : 24;
            int textWidth = TextRenderer.MeasureText(g, label, font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int x = r.Left + Math.Max(4, (r.Width - textWidth - iconWidth) / 2);
            Color color = hover ? ControlPaint.Light(Accent, .18f) : Accent;
            if (iconWidth > 0)
                DrawTinyIcon(g, new Rectangle(x, r.Top + (r.Height - 18) / 2, 18, 18), icon, color);
            TextRenderer.DrawText(g, label, font,
                new Rectangle(x + iconWidth, r.Top, Math.Max(1, r.Right - x - iconWidth), r.Height),
                color, SettingsText);
        }

        private sealed class SettingsDropDownColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Card;
            public override Color MenuItemSelected => Color.FromArgb(58, Theme.Selection);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(58, Theme.Selection);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(42, Theme.AccentSoft);
            public override Color MenuItemBorder => Color.Transparent;
            public override Color ImageMarginGradientBegin => Theme.Card;
            public override Color ImageMarginGradientMiddle => Theme.Card;
            public override Color ImageMarginGradientEnd => Theme.Card;
        }

        private sealed class SettingsDropDownRenderer : ToolStripProfessionalRenderer
        {
            public SettingsDropDownRenderer() : base(new SettingsDropDownColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                Rectangle r = new Rectangle(Point.Empty, e.ToolStrip.Size);
                r.Width -= 1;
                r.Height -= 1;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Round(r, 8);
                using var fill = new SolidBrush(Color.FromArgb(252, 4, 11, 18));
                using var border = new Pen(Color.FromArgb(92, Theme.BorderAccent));
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item is ToolStripMenuItem { Checked: true })
                    e.TextColor = Accent;
                else
                    e.TextColor = Color.White;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected)
                    return;

                Rectangle r = new Rectangle(3, 2, Math.Max(1, e.Item.Width - 6), Math.Max(1, e.Item.Height - 4));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Round(r, 6);
                using var fill = new LinearGradientBrush(r, Color.FromArgb(58, Theme.Selection), Color.FromArgb(42, Theme.AccentSoft), LinearGradientMode.Vertical);
                e.Graphics.FillPath(fill, path);
            }
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            int rad = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
            int d = rad * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawLogoMark(Graphics g, Rectangle r)
        {
            using var ring = new Pen(Color.FromArgb(118, Accent), 2f);
            using var fill = new LinearGradientBrush(r, Color.FromArgb(150, Accent), Color.FromArgb(150, Theme.AccentSoft), LinearGradientMode.ForwardDiagonal);
            g.FillEllipse(fill, r);
            g.DrawEllipse(ring, r);
            Point[] play =
            {
                new Point(r.Left + r.Width / 2 - 4, r.Top + r.Height / 2 - 10),
                new Point(r.Left + r.Width / 2 - 4, r.Top + r.Height / 2 + 10),
                new Point(r.Right - 10, r.Top + r.Height / 2)
            };
            using var brush = new SolidBrush(Color.FromArgb(230, 245, 250, 255));
            g.FillPolygon(brush, play);
        }

        private void SelectTab(string tab)
        {
            if (string.IsNullOrWhiteSpace(tab))
                return;

            _tab = tab;
            _contentScroll = 0;
            _contentScrollMax = 0;
            _nativeError = string.Empty;
            if (IsProviderTab(_tab) && !ShowingNativeProvider)
                ScheduleProviderLoad(_tab);
            else
                HideProviderSurface();
            Invalidate();
        }

        private static bool IsProviderTab(string tab)
        {
            return !string.Equals(tab, "Generali", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(tab, "Aspetto", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(tab, "MPV", StringComparison.OrdinalIgnoreCase);
        }

        private void LayoutProviderSurface(Rectangle host)
        {
            Rectangle inner = host;
            if (_providerSurface.Bounds != inner)
                _providerSurface.Bounds = inner;
            if (!_providerSurface.Visible)
            {
                _providerSurface.Visible = true;
                _providerSurface.BringToFront();
            }
            if (_providerPlaceholder.Visible)
                LayoutProviderPlaceholder();
        }

        private void HideProviderSurface()
        {
            if (_providerSurface.Visible)
                _providerSurface.Visible = false;
        }

        private void ScheduleProviderLoad(string tab)
        {
            if (!IsProviderTab(tab)) { HideProviderSurface(); return; }
            _providerPendingTab = tab;
            if (!IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || !Visible || _tab != tab || _providerPendingTab != tab) return;
                _providerLoadedTab = string.Empty;
                LoadProviderTab(tab);
                Invalidate();
            }));
        }

        private void LoadProviderTab(string tab)
        {
            if (!IsProviderTab(tab) || !string.Equals(_tab, tab, StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(_providerLoadedTab, tab, StringComparison.OrdinalIgnoreCase))
                return;

            _providerLoadedTab = tab;
            // SelectTab schedules this callback before the next paint. Layout and
            // show the native host now; IPropertyPage.Activate needs a visible parent.
            Rectangle bounds = ClientRectangle;
            int sidebarWidth = SidebarWidth(bounds);
            Rectangle content = CenterContentColumn(new Rectangle(bounds.Left + sidebarWidth, bounds.Top,
                Math.Max(1, bounds.Width - sidebarWidth), bounds.Height));
            int contentPad = Math.Clamp(content.Width / 16, 16, 42);
            Rectangle body = new Rectangle(content.Left + contentPad, content.Top + 80,
                Math.Max(1, content.Width - contentPad * 2), Math.Max(1, content.Height - 96));
            LayoutProviderSurface(ProviderHostBounds(body));
            _providerDsHost.Visible = false;
            _madVrSettingsHost.Visible = false;
            _providerPlaceholder.Visible = false;

            try
            {
                if (string.Equals(tab, "madVR", StringComparison.OrdinalIgnoreCase))
                {
                    _madVrSettingsHost.Visible = true;
                    _madVrSettingsHost.BringToFront();
                    _madVrSettingsHost.EnsureStarted();
                    return;
                }

                if (_providerHosts.TryGetValue(tab, out var cachedHost))
                {
                    _providerDsHost = cachedHost;
                    cachedHost.Visible = true;
                    cachedHost.BringToFront();
                    return;
                }
                if (_providerHosts.Count > 0)
                {
                    _providerDsHost = new DsPropPageHost { Dock = DockStyle.Fill, BackColor = Theme.Panel };
                    _providerDsHost.SetLanguage(_language);
                    _providerSurface.Controls.Add(_providerDsHost);
                }
                _providerHosts[tab] = _providerDsHost;

                if (string.Equals(tab, "MPC Video Renderer", StringComparison.OrdinalIgnoreCase))
                {
                    // MPCVR is shipped with Cinecore and is intentionally usable reg-free.
                    // Loading by the real CLSID guarantees that these are the renderer's
                    // own property pages, even when Windows has an older/different build
                    // registered under a similar friendly name.
                    _providerDsHost.Visible = true;
                    _providerDsHost.BringToFront();
                    _providerDsHost.LoadFromClsid(DsHelpers.CLSID_MpcVideoRenderer);
                    return;
                }

                string[] names = ProviderFriendlyNames(tab);
                if (names.Length == 0)
                    throw new InvalidOperationException(L("Provider non configurato.", "Provider is not configured."));

                Exception? last = null;
                foreach (string name in names)
                {
                    try
                    {
                        // Native IPropertyPage.Activate needs a visible parent HWND;
                        // activating under a hidden host can leave focus and mouse
                        // capture on the previous WinForms page.
                        _providerDsHost.Visible = true;
                        _providerDsHost.BringToFront();
                        _providerDsHost.LoadFromFriendlyName(name);
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }

                throw last ?? new InvalidOperationException(L("Scheda non disponibile.", "Panel is not available."));
            }
            catch (Exception ex)
            {
                _providerHosts.Remove(tab);
                ShowProviderPlaceholder(tab, ex.Message);
            }
        }

        private void ShowProviderPlaceholder(string tab, string detail)
        {
            _providerDsHost.Visible = false;
            _madVrSettingsHost.Visible = false;
            _providerPlaceholderTitle.Text = L(tab + " non disponibile", tab + " unavailable");
            _providerPlaceholderText.Text = string.IsNullOrWhiteSpace(detail)
                ? L("La scheda proprietaria non e stata trovata in questo sistema.", "The vendor settings panel was not found on this system.")
                : detail;
            _providerPlaceholder.Visible = true;
            _providerPlaceholder.BringToFront();
            LayoutProviderPlaceholder();
        }

        private void LayoutProviderPlaceholder()
        {
            if (_providerPlaceholder == null || _providerPlaceholderTitle == null || _providerPlaceholderText == null)
                return;

            int w = Math.Max(1, _providerPlaceholder.Width);
            int h = Math.Max(1, _providerPlaceholder.Height);
            int boxW = Math.Min(620, Math.Max(260, w - 80));
            int boxH = Math.Min(180, Math.Max(120, h - 80));
            int x = (w - boxW) / 2;
            int y = (h - boxH) / 2;
            _providerPlaceholderTitle.Bounds = new Rectangle(x, y + 20, boxW, 38);
            _providerPlaceholderText.Bounds = new Rectangle(x + 24, y + 66, boxW - 48, boxH - 76);
        }

        private static string[] ProviderFriendlyNames(string tab)
        {
            return tab switch
            {
                "LAV Video" => new[] { "LAV Video Decoder", "LAV Video" },
                "LAV Audio" => new[] { "LAV Audio Decoder", "LAV Audio" },
                "MPC Audio Renderer" => new[] { "MPC Audio Renderer" },
                "Sottotitoli" => new[] { "XySubFilter", "xy-SubFilter", "XySubFilterAutoLoader" },
                _ => Array.Empty<string>()
            };
        }

        private string TabLabel(string tab)
        {
            return tab switch
            {
                "Generali" => L("Generali", "General"),
                "Sottotitoli" => L("Sottotitoli", "Subtitles"),
                _ => tab
            };
        }

        private string MpvSectionLabel(string section)
        {
            return section switch
            {
                "Essenziali" => L("Essenziali", "Essentials"),
                "Qualita" => L("Qualita", "Quality"),
                "Audio/Sub" => L("Audio/Sott.", "Audio/Sub"),
                "Avanzate" => L("Avanzate", "Advanced"),
                _ => section
            };
        }

        private static void DrawBrandLogo(Graphics g, Rectangle r)
        {
            Image? logo = LoadBrandLogoForTheme();
            if (logo != null)
            {
                Rectangle dest = ContainDestination(logo.Size, r);
                dest.X = r.Left;
                g.DrawImage(logo, dest);
                return;
            }

            int mark = Math.Clamp(r.Height - 14, 30, 52);
            Rectangle icon = new Rectangle(r.Left, r.Top + (r.Height - mark) / 2, mark, mark);
            if (!TryDrawAssetIcon(g, icon, "cinema", Accent))
                DrawLogoMark(g, icon);

            Rectangle text = new Rectangle(icon.Right + 10, r.Top, Math.Max(1, r.Right - icon.Right - 10), r.Height);
            using var nameFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", (r.Height < 68 ? 11f : 13f) * (96f / 72f), GraphicsUnit.Pixel);
            using var subFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 8f * (96f / 72f), GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "CINECORE", nameFont, new Rectangle(text.Left, text.Top + 5, text.Width, Math.Max(20, text.Height / 2)), TextColor,
                TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
            TextRenderer.DrawText(g, "PLAYER", subFont, new Rectangle(text.Left, text.Top + text.Height / 2, text.Width, Math.Max(16, text.Height / 2 - 4)), Color.FromArgb(160, 187, 210),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
        }

        private static Bitmap? _lightLogo;
        private static Color _lightLogoInk;

        /// <summary>
        /// Logo adatto al tema corrente: nel tema chiaro la scritta bianca diventa del colore
        /// del testo (il simbolo colorato resta invariato), altrimenti era invisibile.
        /// </summary>
        internal static Image? LoadBrandLogoForTheme()
        {
            var logo = LoadBrandLogo() as Bitmap;
            if (logo == null || !Theme.IsLight) return logo;
            Color ink = Theme.Text;
            if (_lightLogo != null && _lightLogoInk == ink) return _lightLogo;
            var recolored = new Bitmap(logo.Width, logo.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            // Solo la scritta (a destra del simbolo) cambia colore: prima anche i riflessi
            // argentati dell'anello diventavano scuri e i bordi sfumati delle lettere restavano
            // bianchi (alone). Nella scritta si tiene l'alfa originale per l'antialiasing, pesata
            // sulla luminosita' del pixel (la scritta e' bianca su trasparente).
            int textStart = FindLogoTextStart(logo);
            for (int y = 0; y < logo.Height; y++)
                for (int x = 0; x < logo.Width; x++)
                {
                    Color c = logo.GetPixel(x, y);
                    if (x < textStart || c.A == 0) { recolored.SetPixel(x, y, c); continue; }
                    int max = Math.Max(c.R, Math.Max(c.G, c.B));
                    int alpha = (int)Math.Round(c.A * Math.Clamp(max / 255.0, 0, 1));
                    recolored.SetPixel(x, y, Color.FromArgb(alpha, ink));
                }
            _lightLogo?.Dispose();
            _lightLogo = recolored;
            _lightLogoInk = ink;
            return recolored;
        }

        /// <summary>Prima colonna della scritta: dopo il simbolo c'e' una fascia trasparente.</summary>
        private static int FindLogoTextStart(Bitmap logo)
        {
            bool Empty(int x)
            {
                for (int y = 0; y < logo.Height; y++) if (logo.GetPixel(x, y).A > 24) return false;
                return true;
            }
            int start = Math.Min(logo.Width - 1, (int)(logo.Height * 0.6));
            for (int x = start; x < logo.Width; x++)
                if (Empty(x)) { while (x < logo.Width && Empty(x)) x++; return x; }
            return (int)(logo.Height * 1.05);
        }

        internal static Image? LoadBrandLogo()
        {
            if (LogoLookupDone)
                return CachedLogo;

            LogoLookupDone = true;
            foreach (string path in CandidateLogoPaths())
            {
                try
                {
                    if (!File.Exists(path))
                        continue;
                    using var img = Image.FromFile(path);
                    CachedLogo = new Bitmap(img);
                    return CachedLogo;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> CandidateLogoPaths()
        {
            string baseDir = AppContext.BaseDirectory;
            string cwd = Environment.CurrentDirectory;
            string workspace = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
            foreach (string root in new[] { baseDir, cwd, workspace })
            {
                foreach (string name in new[] { "cinecore-logo.png", "cinecore-logo-full.png", "cinecore-player-logo.png", "brand-logo.png", "logo.png", "Logo.png", "app-logo.png" })
                {
                    yield return Path.Combine(root, "Assets", name);
                    yield return Path.Combine(root, "Assets", "Icons", name);
                    yield return Path.Combine(root, name);
                }
            }
        }

        private static Rectangle ContainDestination(Size source, Rectangle bounds)
        {
            if (source.Width <= 0 || source.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
                return bounds;

            float scale = Math.Min(bounds.Width / (float)source.Width, bounds.Height / (float)source.Height);
            int w = Math.Max(1, (int)Math.Round(source.Width * scale));
            int h = Math.Max(1, (int)Math.Round(source.Height * scale));
            return new Rectangle(bounds.Left + (bounds.Width - w) / 2, bounds.Top + (bounds.Height - h) / 2, w, h);
        }

        private static void DrawTinyIcon(Graphics g, Rectangle r, string key, Color color)
        {
            if (TryDrawAssetIcon(g, r, key, color))
                return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, Math.Max(1.7f, r.Width / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var brush = new SolidBrush(color);
            string k = key.ToLowerInvariant();
            if (k is "equalizer")
            {
                // Tre cursori verticali con le manopole a altezze diverse.
                float[] knobs = { .62f, .30f, .52f };
                for (int i = 0; i < 3; i++)
                {
                    float x = r.Left + r.Width * (0.2f + i * 0.3f);
                    g.DrawLine(pen, x, r.Top + 2, x, r.Bottom - 2);
                    float y = r.Top + r.Height * knobs[i];
                    g.FillEllipse(brush, x - r.Width * .13f, y - r.Width * .13f, r.Width * .26f, r.Width * .26f);
                }
                return;
            }
            if (k is "chevron")
            {
                int cy = r.Top + r.Height / 2;
                g.FillPolygon(brush, new[]
                {
                    new Point(r.Right - 1, cy),
                    new Point(r.Left + 1, r.Top + 1),
                    new Point(r.Left + 1, r.Bottom - 1)
                });
                return;
            }
            if (k is "check")
            {
                g.DrawLines(pen, new[] { new Point(r.Left + 2, r.Top + r.Height / 2), new Point(r.Left + r.Width / 2 - 1, r.Bottom - 3), new Point(r.Right - 2, r.Top + 3) });
                return;
            }
            if (k is "x")
            {
                g.DrawLine(pen, r.Left + 4, r.Top + 4, r.Right - 4, r.Bottom - 4);
                g.DrawLine(pen, r.Right - 4, r.Top + 4, r.Left + 4, r.Bottom - 4);
                return;
            }
            if (k is "reset")
            {
                g.DrawArc(pen, Rectangle.Inflate(r, -4, -4), 35, 285);
                g.FillPolygon(brush, new[]
                {
                    new Point(r.Left + 5, r.Top + 8),
                    new Point(r.Left + 4, r.Top + 2),
                    new Point(r.Left + 10, r.Top + 5)
                });
                return;
            }
            if (k is "key")
            {
                g.DrawEllipse(pen, new Rectangle(r.Left + 3, r.Top + 6, r.Width / 2, r.Height / 2));
                g.DrawLine(pen, r.Left + r.Width / 2, r.Top + r.Height / 2, r.Right - 3, r.Top + r.Height / 2);
                g.DrawLine(pen, r.Right - 7, r.Top + r.Height / 2, r.Right - 7, r.Bottom - 5);
                return;
            }
            if (k is "play" or "netflix")
            {
                g.FillPolygon(brush, new[] { new Point(r.Left + 6, r.Top + 4), new Point(r.Left + 6, r.Bottom - 4), new Point(r.Right - 4, r.Top + r.Height / 2) });
                return;
            }
            if (k is "cc")
            {
                using var font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", Math.Max(7f, r.Width * 0.42f), GraphicsUnit.Pixel);
                TextRenderer.DrawText(g, "CC", font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
                return;
            }
            if (k is "audio" or "wave")
            {
                g.DrawLine(pen, r.Left + 3, r.Top + r.Height / 2, r.Left + 8, r.Top + r.Height / 2);
                g.DrawLine(pen, r.Left + 8, r.Top + r.Height / 2, r.Left + 12, r.Top + 5);
                g.DrawLine(pen, r.Left + 12, r.Top + 5, r.Left + 16, r.Bottom - 5);
                g.DrawLine(pen, r.Left + 16, r.Bottom - 5, r.Right - 3, r.Top + r.Height / 2);
                return;
            }
            if (k is "monitor" or "film")
            {
                g.DrawRectangle(pen, Rectangle.Inflate(r, -3, -5));
                return;
            }
            if (k is "globe")
            {
                g.DrawEllipse(pen, Rectangle.Inflate(r, -3, -3));
                g.DrawLine(pen, r.Left + r.Width / 2, r.Top + 3, r.Left + r.Width / 2, r.Bottom - 3);
                g.DrawLine(pen, r.Left + 4, r.Top + r.Height / 2, r.Right - 4, r.Top + r.Height / 2);
                return;
            }
            if (k is "cube" or "grid")
            {
                g.DrawRectangle(pen, Rectangle.Inflate(r, -4, -4));
                g.DrawLine(pen, r.Left + r.Width / 2, r.Top + 4, r.Left + r.Width / 2, r.Bottom - 4);
                g.DrawLine(pen, r.Left + 4, r.Top + r.Height / 2, r.Right - 4, r.Top + r.Height / 2);
                return;
            }
            if (k is "info")
            {
                g.DrawEllipse(pen, Rectangle.Inflate(r, -3, -3));
                g.FillEllipse(brush, r.Left + r.Width / 2 - 1, r.Top + 6, 3, 3);
                g.DrawLine(pen, r.Left + r.Width / 2, r.Top + 11, r.Left + r.Width / 2, r.Bottom - 5);
                return;
            }
            if (k is "palette")
            {
                Rectangle body = Rectangle.Inflate(r, -2, -3);
                g.DrawEllipse(pen, body);
                int dot = Math.Max(2, r.Width / 7);
                g.FillEllipse(brush, r.Left + r.Width / 3 - dot / 2, r.Top + r.Height / 3 - dot / 2, dot, dot);
                g.FillEllipse(brush, r.Left + r.Width / 2 - dot / 2, r.Top + r.Height / 2 - dot / 2, dot, dot);
                g.FillEllipse(brush, r.Right - r.Width / 3 - dot / 2, r.Top + r.Height / 3 - dot / 2, dot, dot);
                using var notch = new SolidBrush(Color.FromArgb(245, 6, 17, 28));
                g.FillEllipse(notch, r.Right - r.Width / 3, r.Bottom - r.Height / 3, dot + 2, dot + 2);
                return;
            }
            g.DrawEllipse(pen, Rectangle.Inflate(r, -4, -4));
            g.DrawLine(pen, r.Left + r.Width / 2, r.Top + 3, r.Left + r.Width / 2, r.Bottom - 3);
            g.DrawLine(pen, r.Left + 3, r.Top + r.Height / 2, r.Right - 3, r.Top + r.Height / 2);
        }

        private static bool TryDrawAssetIcon(Graphics g, Rectangle r, string key, Color tint)
        {
            try
            {
                string? path = ResolveAssetIconPath(key);
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                int size = Math.Max(12, Math.Max(r.Width, r.Height));
                var cacheKey = (path, size, tint.ToArgb());
                if (!SvgIconCache.TryGetValue(cacheKey, out Bitmap? bmp) || bmp == null)
                {
                    bmp = RenderSvgIcon(path, size, tint);
                    SvgIconCache[cacheKey] = bmp;
                }

                g.DrawImage(bmp, r);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? ResolveAssetIconPath(string key)
        {
            return AssetIconService.Resolve(key);
        }

        private static Bitmap RenderSvgIcon(string svgPath, int targetPx, Color tint)
        {
            return AssetIconService.RenderSvg(svgPath, targetPx, tint);
        }
    }
}
