#nullable enable
using CinecorePlayer2025;
using DirectShowLib;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DSFilterCategory = DirectShowLib.FilterCategory;
using Svg.Skia;
using SkiaSharp;

namespace CinecorePlayer2025.HUD
{
    // ===================== THEME =====================
    internal static class Theme
    {
        public static readonly Color BackdropDim = Color.FromArgb(185, 0, 0, 0);     // overlay dietro modali
        public static readonly Color Panel = Color.FromArgb(24, 24, 28);       // sfondo principale / card
        public static readonly Color Card = Color.FromArgb(24, 24, 28);
        public static readonly Color Nav = Color.FromArgb(26, 26, 30);       // colonna sinistra nav
        public static readonly Color PanelAlt = Color.FromArgb(34, 34, 40);       // highlight nav voce selezionata
        public static readonly Color Border = Color.FromArgb(76, 76, 82);       // linee 1px / bordini
        public static readonly Color Text = Color.White;
        public static readonly Color SubtleText = Color.FromArgb(208, 208, 214);
        public static readonly Color Muted = Color.FromArgb(170, 170, 178);
        public static readonly Color Accent = Color.FromArgb(40, 120, 255);
        public static readonly Color AccentSoft = Color.FromArgb(26, 90, 210);
        public static readonly Color Danger = Color.FromArgb(230, 80, 80);
    }

    // ===================== GRAFICA BASE =====================
    internal static class DrawHelpers
    {
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var gp = new GraphicsPath();
            gp.AddArc(r.Left, r.Top, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }
    }

    // pannello con bordo 1px Theme.Border
    internal sealed class OutlinePanel : Panel
    {
        public OutlinePanel()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var p = new Pen(Theme.Border, 1f);
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    // pannello "card" centrale della modale, con bordo 1px Theme.Border
    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var p = new Pen(Theme.Border, 1f);
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    // ===================== HUD OVERLAY (player OSD) =====================
    internal sealed class HudOverlay : Control
    {
        private float _externalVolume = 1f;
        public event Action? OpenClicked;
        public event Action? PlayPauseClicked;
        public event Action? StopClicked;
        public event Action? FullscreenClicked;
        public event Action? SkipBack10Clicked;
        public event Action? SkipForward10Clicked;
        // -1 back, +1 forward (long-press su Back10/Fwd10: avvia lo scan come telecomando)
        public event Action<int>? ScanStepRequested;
        public event Action? PrevChapterClicked;
        public event Action? NextChapterClicked;
        public event Action<bool>? MutedChanged;
        private Rectangle _rcVolIcon;
        private Rectangle _rcVolPanel;
        private Rectangle _rcVolHoverOpen;
        private Rectangle _rcVolKnob;
        private Rectangle _rcVolIconHit;
        private Rectangle _rcVolPanelHit;

        // --- SVG ICONS (nuovo) ---
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathRemove { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathOpen { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathPlay { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathPause { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathBack10 { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathFwd10 { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathPrevChapter { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathNextChapter { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathFullscreen { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathTopInfo { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathTopSettings { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathVolMute { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathVolZero { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathVolLow { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SvgPathVolHigh { get; set; }

        [DefaultValue(false)]
        public bool IsMuted { get; private set; } = false;

        public void SetMuted(bool muted)
        {
            IsMuted = muted;
            Invalidate();
        }
        private void SetMutedInternal(bool muted, bool fireEvents = true)
        {
            if (muted)
            {
                if (!IsMuted)
                    _volBeforeMute = Math.Clamp(_vol, 0f, 1f);

                IsMuted = true;
            }
            else
            {
                IsMuted = false;

                _vol = Math.Clamp(_volBeforeMute, 0f, 1f);
                if (_vol <= 0.0001f) _vol = 0.25f;
            }

            if (fireEvents)
            {
                VolumeChanged?.Invoke(IsMuted ? 0f : _vol);
                MutedChanged?.Invoke(IsMuted);
            }

            ShowVolumeOsd(1200);
        }
        private void SetVolumeFromUser(float v)
        {
            v = Math.Clamp(v, 0f, 1f);

            bool wasMuted = IsMuted;

            if (IsMuted && v > 0.0001f)
                IsMuted = false;

            _vol = v;
            _externalVolume = _vol;

            if (!IsMuted && _vol > 0.0001f)
                _volBeforeMute = _vol;

            VolumeChanged?.Invoke(IsMuted ? 0f : _vol);

            if (wasMuted != IsMuted)
                MutedChanged?.Invoke(IsMuted);

            ShowVolumeOsd(1200);
        }
        private static float NormalizeVolume01(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;

            if (v > 1.5f && v <= 100f)
                v /= 100f;

            return Math.Clamp(v, 0f, 1f);
        }

        private sealed class SvgCacheKeyComparer : IEqualityComparer<(string path, int sizePx)>
        {
            public bool Equals((string path, int sizePx) x, (string path, int sizePx) y)
                => x.sizePx == y.sizePx &&
                   string.Equals(x.path, y.path, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string path, int sizePx) obj)
                => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.path ?? string.Empty), obj.sizePx);
        }

        [DefaultValue(false)]
        public bool IsPlaying { get; private set; } = false;

        public void SetPlaying(bool playing)
        {
            IsPlaying = playing;
            Invalidate();
        }


        private readonly Dictionary<(string path, int sizePx), Bitmap> _svgCache =
            new(new SvgCacheKeyComparer());

        public event Action? TopSettingsClicked;
        public event Action? TopInfoClicked;

        public event Action<float>? VolumeChanged;
        public event Action<double>? SeekRequested;
        public event Action<double, Point>? PreviewRequested;

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<string>? GetInfoLine { get; set; }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<(double pos, double dur)>? GetTime { get; set; }

        [DefaultValue("")] public string NowPlayingTitle { get; private set; } = string.Empty;
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<string>? GetTitle { get; set; }

        public void UpdateTitle(string? t)
        {
            NowPlayingTitle = t ?? string.Empty;
            ShowOnce(1500);
            Invalidate();
        }
        public void UpdateTitleFromPath(string filePath, string? preferredTitle = null)
        {
            NowPlayingTitle = string.IsNullOrWhiteSpace(preferredTitle)
                ? Path.GetFileNameWithoutExtension(filePath) ?? string.Empty
                : preferredTitle!;
            ShowOnce(1500);
            Invalidate();
        }

        [DefaultValue(false)] public bool AutoHide { get; set; }
        [DefaultValue(2000)] public int IdleHideDelayMs { get; set; } = 2000;
        [DefaultValue(900)] public int HideGraceMs { get; set; } = 900;
        [DefaultValue(150)] public int FadeOutMs { get; set; } = 150;
        [DefaultValue(false)] public bool TimelineVisible { get; set; } = false;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Image? IconInfo { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Image? IconSettings { get; set; }

        private const int TopBarHeight = 60;
        private const int BottomBackdropHeight = 96;
        private const int BtnSize = 28;
        private const int GapDesired = 36;
        private const int ExtraBtnVsVolPad = 2;
        private const int TimelineHeight = 6;
        private const int TimelineYFromBottom = 60;
        private const int InfoYFromBottom = 88;
        private const int ControlYFromBottom = 38;
        private const int VolBtnSize = 28;

        private const int VolPanelW = 40;
        private const int VolPanelH = 170;
        private const int VolPanelPad = 8;
        private const int VolPanelBottomExtra = 8;
        private const int VolLabelHeight = 18;
        private const int VolTrackThickness = 4;
        private const int VolKnobRadius = 5;

        private readonly Font _fInfo = new("Segoe UI", 9f);
        private readonly Font _fTime = new("Segoe UI", 9f, FontStyle.Bold);
        private readonly Font _fTopTitle = new("Segoe UI Semibold", 12.5f);
        private readonly Font _fSymbol = new("Segoe UI", 11f, FontStyle.Bold);

        private readonly System.Windows.Forms.Timer _fade;
        // Long-press su Back10/Fwd10: avvia lo scan (0.5x, 1x, 2x, 3x, 4x...) come telecomando
        private readonly System.Windows.Forms.Timer _scanHold;
        private ButtonId _scanHoldBtn = ButtonId.None;
        private bool _scanHoldTriggered = false;
        private DateTime _scanHoldStartAt = DateTime.MinValue;
        private const int ScanHoldTriggerMs = 420;
        private float _opacity = 1f;
        private DateTime _fadeStartAt = DateTime.MinValue;
        private DateTime _lastMove = DateTime.UtcNow;
        private DateTime _forceShowUntil = DateTime.MinValue;

        private float _vol = 1.0f;
        private bool _drag, _dragVol;
        // Scrub comandato da remoto (telecomando web): muove manopola/ghost sulla timeline e mantiene l'anteprima
        private bool _remoteDrag;
        private double _remoteDragPosSec;
        private DateTime _remoteDragUntil = DateTime.MinValue;
        private float _volBeforeMute = 1.0f;
        private bool _volUiHot = false;
        private DateTime _volHotUntil = DateTime.MinValue;
        private const int VolHoverLingerMs = 300;
        private double _dragPosSec;
        private DateTime _lastPreviewAt = DateTime.MinValue;
        private Bitmap? _preview; private double _previewSec;
        private int _lastMouseX;
        private Point _lastPhysicalMouseScreenPos = Point.Empty;
        private bool _hasPhysicalMouseScreenPos = false;
        private const int PhysicalMouseDeadzonePx = 2;

        private Rectangle _rcTopBar, _rcBottomBar;
        private Rectangle _rcTimeline, _rcTimelineHit;
        private Rectangle _rcBtnRemove, _rcBtnOpen, _rcBtnPlay, _rcBtnBack, _rcBtnFwd, _rcBtnPrev, _rcBtnNext, _rcBtnFull;
        private Rectangle _rcVolTrack;
        private Rectangle _rcTopInfo, _rcTopSettings;
        private int _volCenterY;
        private bool _showPrevNext = true, _showBackFwd = true;

        public enum ButtonId
        {
            None,
            Remove,
            Open,
            PlayPause,
            Back10,
            Fwd10,
            PrevChapter,
            NextChapter,
            Volume,
            Fullscreen,
            TopSettings,
            TopInfo
        }
        private ButtonId _pulseBtn = ButtonId.None;
        private DateTime _pulseUntil = DateTime.MinValue;
        public void Pulse(ButtonId btn, int ms = 180)
        {
            _pulseBtn = btn;
            _pulseUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(60, ms));
            Invalidate();
        }
        private bool IsPulsing(ButtonId btn) => _pulseBtn == btn && DateTime.UtcNow < _pulseUntil;

        // =========================
        // DPAD navigation (telecomando): focus interno sui bottoni del HUD
        // =========================
        public bool DpadMode { get; private set; } = false;
        private ButtonId _dpadSel = ButtonId.PlayPause;
        private ButtonId _dpadLastBottom = ButtonId.PlayPause;

        public void DpadActivate(ButtonId start = ButtonId.PlayPause)
        {
            DpadMode = true;
            _dpadSel = start == ButtonId.None ? ButtonId.PlayPause : start;
            _dpadLastBottom = _dpadSel;
            ShowOnce(4000);
            Invalidate();
        }

        public void DpadDeactivate()
        {
            DpadMode = false;
            Invalidate();
        }

        public void DpadMove(string dir)
        {
            if (!DpadMode) DpadActivate();

            RecalcLayout();
            NormalizeDpadSelection();

            bool isTop = IsTopButton(_dpadSel);

            if (dir == "up")
            {
                if (!isTop)
                {
                    _dpadLastBottom = _dpadSel;

                    // scegli tra Info/Settings in base a dove sei (sx/dx)
                    var curRc = GetButtonRect(_dpadSel);
                    int cx = curRc.Left + curRc.Width / 2;
                    int mid = Width / 2;
                    _dpadSel = (cx < mid) ? ButtonId.TopInfo : ButtonId.TopSettings;
                    Invalidate();
                }
                return;
            }

            if (dir == "down")
            {
                if (isTop)
                {
                    _dpadSel = _dpadLastBottom;
                    NormalizeDpadSelection();
                    Invalidate();
                }
                return;
            }

            if (dir == "left" || dir == "right")
            {
                var row = isTop ? GetTopRow() : GetBottomRow();
                if (row.Count == 0) return;

                int i = row.IndexOf(_dpadSel);
                if (i < 0) i = 0;

                int step = (dir == "right") ? +1 : -1;
                i = (i + step) % row.Count;
                if (i < 0) i += row.Count;

                _dpadSel = row[i];
                if (!isTop) _dpadLastBottom = _dpadSel;

                ShowOnce(2500);
                Invalidate();
                return;
            }
        }

        public void DpadOk()
        {
            if (!DpadMode)
            {
                DpadActivate();
                return;
            }

            NormalizeDpadSelection();

            switch (_dpadSel)
            {
                case ButtonId.Remove: StopClicked?.Invoke(); Pulse(ButtonId.Remove); break;
                case ButtonId.Open: OpenClicked?.Invoke(); Pulse(ButtonId.Open); break;
                case ButtonId.PlayPause: PlayPauseClicked?.Invoke(); Pulse(ButtonId.PlayPause); break;
                case ButtonId.Back10: SkipBack10Clicked?.Invoke(); Pulse(ButtonId.Back10); break;
                case ButtonId.Fwd10: SkipForward10Clicked?.Invoke(); Pulse(ButtonId.Fwd10); break;
                case ButtonId.PrevChapter: PrevChapterClicked?.Invoke(); Pulse(ButtonId.PrevChapter); break;
                case ButtonId.NextChapter: NextChapterClicked?.Invoke(); Pulse(ButtonId.NextChapter); break;
                case ButtonId.Fullscreen: FullscreenClicked?.Invoke(); Pulse(ButtonId.Fullscreen); break;
                case ButtonId.TopSettings: TopSettingsClicked?.Invoke(); Pulse(ButtonId.TopSettings); break;
                case ButtonId.TopInfo: TopInfoClicked?.Invoke(); Pulse(ButtonId.TopInfo); break;
                case ButtonId.Volume:
                    ToggleMuteFromUser();
                    break;
            }

            ShowOnce(2500);
        }

        public void ToggleMuteFromUser()
        {
            SetMutedInternal(!IsMuted);
        }

        private void NormalizeDpadSelection()
        {
            // se un bottone non è disegnato (prev/next/back/fwd), saltalo
            if (IsTopButton(_dpadSel)) return;

            var bottom = GetBottomRow();
            if (bottom.Count == 0)
            {
                _dpadSel = ButtonId.PlayPause;
                return;
            }

            if (!bottom.Contains(_dpadSel))
                _dpadSel = bottom.Contains(_dpadLastBottom) ? _dpadLastBottom : ButtonId.PlayPause;

            if (!bottom.Contains(_dpadSel))
                _dpadSel = bottom[0];
        }

        private bool IsTopButton(ButtonId id) => id == ButtonId.TopInfo || id == ButtonId.TopSettings;

        private System.Collections.Generic.List<ButtonId> GetTopRow() =>
            new() { ButtonId.TopInfo, ButtonId.TopSettings };

        private System.Collections.Generic.List<ButtonId> GetBottomRow()
        {
            var row = new System.Collections.Generic.List<ButtonId>
            {
                ButtonId.Remove,
                ButtonId.Open
            };

            if (_showPrevNext) row.Add(ButtonId.PrevChapter);
            if (_showBackFwd) row.Add(ButtonId.Back10);

            row.Add(ButtonId.PlayPause);

            if (_showBackFwd) row.Add(ButtonId.Fwd10);
            if (_showPrevNext) row.Add(ButtonId.NextChapter);

            row.Add(ButtonId.Volume);
            row.Add(ButtonId.Fullscreen);

            return row;
        }

        private Rectangle GetButtonRect(ButtonId id)
        {
            // RecalcLayout() è già chiamato in OnPaint, ma qui ci serve anche da ProcessCmdKey
            switch (id)
            {
                case ButtonId.Remove: return _rcBtnRemove;
                case ButtonId.Open: return _rcBtnOpen;
                case ButtonId.PlayPause: return _rcBtnPlay;
                case ButtonId.Back10: return _rcBtnBack;
                case ButtonId.Fwd10: return _rcBtnFwd;
                case ButtonId.PrevChapter: return _rcBtnPrev;
                case ButtonId.NextChapter: return _rcBtnNext;
                case ButtonId.Fullscreen: return _rcBtnFull;
                case ButtonId.TopSettings: return _rcTopSettings;
                case ButtonId.TopInfo: return _rcTopInfo;
                case ButtonId.Volume: return _rcVolIcon;
                default: return Rectangle.Empty;
            }
        }

        private void DrawDpadFocus(Graphics g)
        {
            if (!DpadMode) return;

            var rc = GetButtonRect(_dpadSel);
            if (rc.Width <= 0 || rc.Height <= 0) return;

            rc = Rectangle.Inflate(rc, 4, 4);

            int a = (int)(220 * _opacity);
            if (a < 30) a = 30;

            using var pen = new Pen(Color.FromArgb(a, Theme.Accent), 3f);
            pen.Alignment = PenAlignment.Center;

            using var gp = DrawHelpers.RoundRect(rc, rc.Width / 2);
            g.DrawPath(pen, gp);
        }


        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var host = FindForm();
            if (host != null && host.TransparencyKey != Color.Empty)
            {
                e.Graphics.Clear(host.TransparencyKey);
                return;
            }
            base.OnPaintBackground(e);
        }
        private void UpdateVolHotState(Point? ptOverride = null)
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

            RecalcLayout();

            var now = DateTime.UtcNow;
            var pt = ptOverride ?? PointToClient(Cursor.Position);

            bool overIcon = _rcVolIconHit.Contains(pt);

            bool overPanel = _volUiHot && _rcVolPanelHit.Contains(pt);

            if (!_volUiHot)
            {
                if (overIcon)
                {
                    _volUiHot = true;
                    _volHotUntil = now.AddMilliseconds(VolHoverLingerMs);
                    Invalidate();
                }
                return;
            }

            if (_dragVol || overIcon || overPanel)
            {
                _volHotUntil = now.AddMilliseconds(VolHoverLingerMs);
                return;
            }

            if (now >= _volHotUntil)
            {
                _volUiHot = false;
                Invalidate();
            }
        }

        public HudOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;

            // --- default SVG paths (se presenti) ---
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string iconDir = Path.Combine(baseDir, "Assets", "icons");
                string P(string name) => Path.Combine(iconDir, name);

                SvgPathRemove ??= File.Exists(P("player-eject.svg")) ? P("player-eject.svg") : null;
                SvgPathOpen ??= File.Exists(P("library.svg")) ? P("library.svg") : null;
                SvgPathPlay ??= File.Exists(P("player-play.svg")) ? P("player-play.svg") : null;
                SvgPathPause ??= File.Exists(P("player-pause.svg")) ? P("player-pause.svg") : null;
                SvgPathBack10 ??= File.Exists(P("player-skip-back.svg")) ? P("player-skip-back.svg") : null;
                SvgPathFwd10 ??= File.Exists(P("player-skip-forward.svg")) ? P("player-skip-forward.svg") : null;
                SvgPathPrevChapter ??= File.Exists(P("player-track-prev.svg")) ? P("player-track-prev.svg") : null;
                SvgPathNextChapter ??= File.Exists(P("player-track-next.svg")) ? P("player-track-next.svg") : null;
                SvgPathFullscreen ??= File.Exists(P("maximize.svg")) ? P("maximize.svg") : null;
                SvgPathTopInfo ??= File.Exists(P("info-circle.svg")) ? P("info-circle.svg") : null;
                SvgPathTopSettings ??= File.Exists(P("settings.svg")) ? P("settings.svg") : null;

                SvgPathVolMute ??= File.Exists(P("volume-off.svg")) ? P("volume-off.svg") : null;
                SvgPathVolZero ??= File.Exists(P("volume-4.svg")) ? P("volume-4.svg") : null;
                SvgPathVolLow ??= File.Exists(P("volume-2.svg")) ? P("volume-2.svg") : null;
                SvgPathVolHigh ??= File.Exists(P("volume.svg")) ? P("volume.svg") : null;
            }
            catch { }

            _fade = new System.Windows.Forms.Timer { Interval = 30 };
            _fade.Tick += (_, __) =>
            {
                UpdateVolHotState();
                var now = DateTime.UtcNow;

                // Remote scrub: se non riceviamo più update per un po', rilascia la ghost knob e (best-effort) chiudi l'anteprima
                if (_remoteDrag && now >= _remoteDragUntil)
                {
                    _remoteDrag = false;
                    if (!_drag && _preview != null)
                    {
                        try { SetPreview(null, _previewSec); } catch { }
                    }
                    Invalidate();
                }

                if (now < _forceShowUntil || !AutoHide || _drag || _dragVol || _remoteDrag)
                {
                    _fadeStartAt = DateTime.MinValue;
                    if (_opacity != 1f) { _opacity = 1f; Invalidate(); }
                    return;
                }

                var idleMs = (now - _lastMove).TotalMilliseconds;
                if (idleMs < HideGraceMs)
                {
                    _fadeStartAt = DateTime.MinValue;
                    if (_opacity != 1f) { _opacity = 1f; Invalidate(); }
                    return;
                }

                if (_fadeStartAt == DateTime.MinValue) _fadeStartAt = now;
                double t = (now - _fadeStartAt).TotalMilliseconds / Math.Max(1, FadeOutMs);
                float target = (float)(1.0 - Math.Clamp(t, 0, 1));
                if (Math.Abs(_opacity - target) > 0.01f) { _opacity = target; Invalidate(); }
                else if (t >= 1.0 && _opacity != 0f) { _opacity = 0f; Invalidate(); }
            };
            _fade.Start();

            _scanHold = new System.Windows.Forms.Timer { Interval = 30 };
            _scanHold.Tick += (_, __) => TickScanHold();
            Disposed += (_, __) =>
            {
                try { _scanHold.Stop(); _scanHold.Dispose(); } catch { }
            };

            try
            {
                _lastPhysicalMouseScreenPos = Control.MousePosition;
                _hasPhysicalMouseScreenPos = true;
            }
            catch { }

            MouseMove += (_, e) =>
            {
                RecalcLayout();
                var now = DateTime.UtcNow;
                _lastMouseX = e.X;

                bool physicalMove = _drag || _dragVol;
                try
                {
                    var screenPos = Control.MousePosition;
                    if (!_hasPhysicalMouseScreenPos)
                    {
                        _lastPhysicalMouseScreenPos = screenPos;
                        _hasPhysicalMouseScreenPos = true;
                    }
                    else if (Math.Abs(screenPos.X - _lastPhysicalMouseScreenPos.X) >= PhysicalMouseDeadzonePx ||
                             Math.Abs(screenPos.Y - _lastPhysicalMouseScreenPos.Y) >= PhysicalMouseDeadzonePx)
                    {
                        physicalMove = true;
                        _lastPhysicalMouseScreenPos = screenPos;
                    }
                }
                catch
                {
                    physicalMove = true;
                }

                UpdateVolHotState(e.Location);

                if ((_drag || _dragVol) && Capture && (e.Button & MouseButtons.Left) == 0)
                {
                    StopDragging();
                    return;
                }

                if ((IsHudInteractive(e.Location) || _drag || _dragVol) && physicalMove)
                {
                    _lastMove = now;
                    if (_opacity != 1f) { _opacity = 1f; Invalidate(); }
                }

                if (_dragVol)
                {
                    float v = VolumeFromY(e.Y);
                    SetVolumeFromUser(v);
                    return;
                }

                if (_drag && TimelineVisible && GetTime != null)
                {
                    var (_, dur) = GetTime();
                    if (dur > 0)
                    {
                        double ratio = (e.X - _rcTimeline.X) / (double)_rcTimeline.Width;
                        ratio = Math.Clamp(ratio, 0, 1);
                        _dragPosSec = ratio * dur;
                        Invalidate();

                        if ((now - _lastPreviewAt).TotalMilliseconds >= 250)
                        {
                            _lastPreviewAt = now;
                            PreviewRequested?.Invoke(_dragPosSec,
                                PointToScreen(new Point(e.X, _rcTimeline.Y)));
                        }
                    }
                }
                else
                {
                    if (!_remoteDrag && _preview != null) { SetPreview(null, _previewSec); }
                }
            };

            VisibleChanged += (_, __) =>
            {
                if (Visible)
                {
                    try
                    {
                        _lastPhysicalMouseScreenPos = Control.MousePosition;
                        _hasPhysicalMouseScreenPos = true;
                    }
                    catch { }

                    ShowOnce(1800);
                    _opacity = 1f;
                    Invalidate();
                }
            };
        }

        public void ShowOnce(int ms = 2000)
        {
            _forceShowUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(250, ms));
            _opacity = 1f;
            Invalidate();
        }

        // === OSD helpers (volume / scrub remoto) ===
        public void ShowVolumeOsd(int ms = 1200)
        {
            var now = DateTime.UtcNow;
            int linger = Math.Max(250, ms);
            _volUiHot = true;
            _volHotUntil = now.AddMilliseconds(Math.Max(linger, VolHoverLingerMs));
            ShowOnce(linger);
            Invalidate();
        }

        public void SetRemoteScrub(double seconds, int lingerMs = 350)
        {
            var now = DateTime.UtcNow;
            _remoteDrag = true;
            _remoteDragPosSec = seconds;
            _remoteDragUntil = now.AddMilliseconds(Math.Max(120, lingerMs));
            ShowOnce(Math.Max(900, lingerMs + 600));
            Invalidate();
        }

        public void ClearRemoteScrub()
        {
            if (_remoteDrag)
            {
                _remoteDrag = false;
                Invalidate();
            }
        }

        public void SetPreview(Bitmap? bmp, double seconds)
        {
            _preview?.Dispose();
            _preview = bmp;
            _previewSec = seconds;
            Invalidate();
        }
        public void SetExternalVolume(float v)
        {
            var norm = NormalizeVolume01(v);
            _vol = norm;
            _externalVolume = norm;
            Invalidate();
        }

        // Volume corrente (0..1) usato dall'HUD.
        public float GetVolume()
        {
            return _vol;
        }

        public float GetExternalVolume()
        {
            return _externalVolume;
        }

        public void PerformVolumeDelta(float delta, Action<float> apply)
        {
            _vol = Math.Clamp(_vol + delta, 0f, 1f);
            _externalVolume = _vol;
            apply(_vol);
            ShowVolumeOsd(1200);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!Capture) StopDragging();
            if (!_dragVol)
            {
                _volHotUntil = DateTime.UtcNow.AddMilliseconds(VolHoverLingerMs);
                UpdateVolHotState();
                Invalidate();
            }
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) StopDragging();
        }

        private void StopDragging()
        {
            CancelScanHold();
            if (_drag || _dragVol)
            {
                _drag = false;
                _dragVol = false;
                Capture = false;
                Invalidate();
            }

            if (!_remoteDrag && _preview != null)
            {
                SetPreview(null, _dragPosSec);
            }
        }


        private void CancelScanHold()
        {
            try { _scanHold.Stop(); } catch { }
            _scanHoldBtn = ButtonId.None;
            _scanHoldTriggered = false;
            _scanHoldStartAt = DateTime.MinValue;
        }

        private void StartScanHold(ButtonId btn)
        {
            _scanHoldBtn = btn;
            _scanHoldTriggered = false;
            _scanHoldStartAt = DateTime.UtcNow;
            try { Capture = true; } catch { }
            try { _scanHold.Stop(); _scanHold.Start(); } catch { }
        }

        private void TickScanHold()
        {
            if (_scanHoldBtn != ButtonId.Back10 && _scanHoldBtn != ButtonId.Fwd10)
            {
                CancelScanHold();
                return;
            }
            if (_scanHoldTriggered)
            {
                try { _scanHold.Stop(); } catch { }
                return;
            }
            if (_scanHoldStartAt == DateTime.MinValue) return;
            if ((DateTime.UtcNow - _scanHoldStartAt).TotalMilliseconds < ScanHoldTriggerMs) return;

            _scanHoldTriggered = true;
            try { _scanHold.Stop(); } catch { }

            int dir = _scanHoldBtn == ButtonId.Back10 ? -1 : +1;
            try { ScanStepRequested?.Invoke(dir); } catch { }
            try { Pulse(_scanHoldBtn); } catch { }
            try { ShowOnce(1600); } catch { }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            CancelScanHold();

            RecalcLayout();

            if (!IsHudInteractive(e.Location))
            {
                ForwardMouseToUnderlying(WinMsg.WM_LBUTTONDOWN, e.Location, nint.Zero);
                return;
            }

            if (_rcTopSettings.Contains(e.Location)) { TopSettingsClicked?.Invoke(); Pulse(ButtonId.TopSettings); return; }
            if (_rcTopInfo.Contains(e.Location)) { TopInfoClicked?.Invoke(); Pulse(ButtonId.TopInfo); return; }

            if (_rcBtnRemove.Contains(e.Location)) { StopClicked?.Invoke(); Pulse(ButtonId.Remove); return; }
            if (_rcBtnOpen.Contains(e.Location)) { OpenClicked?.Invoke(); Pulse(ButtonId.Open); return; }
            if (_rcBtnPlay.Contains(e.Location)) { PlayPauseClicked?.Invoke(); Pulse(ButtonId.PlayPause); return; }
            if (_showBackFwd && _rcBtnBack.Contains(e.Location)) { StartScanHold(ButtonId.Back10); return; }
            if (_showBackFwd && _rcBtnFwd.Contains(e.Location)) { StartScanHold(ButtonId.Fwd10); return; }
            if (_showPrevNext && _rcBtnPrev.Contains(e.Location)) { PrevChapterClicked?.Invoke(); Pulse(ButtonId.PrevChapter); return; }
            if (_showPrevNext && _rcBtnNext.Contains(e.Location)) { NextChapterClicked?.Invoke(); Pulse(ButtonId.NextChapter); return; }
            if (_rcBtnFull.Contains(e.Location)) { FullscreenClicked?.Invoke(); Pulse(ButtonId.Fullscreen); return; }

            if (_rcVolIconHit.Contains(e.Location))
            {
                SetMutedInternal(!IsMuted);
                return;
            }

            if ((_volUiHot || _dragVol) && _rcVolPanelHit.Contains(e.Location))
            {
                _volHotUntil = DateTime.UtcNow.AddMilliseconds(VolHoverLingerMs);

                _dragVol = true;
                Capture = true;

                float v = VolumeFromY(e.Y);
                SetVolumeFromUser(v);
                return;
            }

            if (TimelineVisible && _rcTimelineHit.Contains(e.Location) && GetTime != null)
            {
                _drag = true;
                Capture = true;
                var (_, dur) = GetTime();
                double r = (e.X - _rcTimeline.X) / (double)_rcTimeline.Width;
                r = Math.Clamp(r, 0, 1);
                _dragPosSec = r * Math.Max(0, dur);
                PreviewRequested?.Invoke(_dragPosSec, PointToScreen(new Point(e.X, _rcTimeline.Y)));
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                // scan-hold: long-press su Back10/Fwd10 avvia lo scan; al rilascio non stoppare lo scan.
                if (_scanHoldBtn == ButtonId.Back10 || _scanHoldBtn == ButtonId.Fwd10)
                {
                    bool triggered = _scanHoldTriggered;
                    var btn = _scanHoldBtn;
                    CancelScanHold();
                    try { Capture = false; } catch { }

                    if (!triggered)
                    {
                        if (btn == ButtonId.Back10) { SkipBack10Clicked?.Invoke(); Pulse(ButtonId.Back10); }
                        else { SkipForward10Clicked?.Invoke(); Pulse(ButtonId.Fwd10); }
                    }
                    return;
                }

                RecalcLayout();

                if (_drag && TimelineVisible && GetTime != null)
                {
                    var (_, dur) = GetTime();
                    double r = (e.X - _rcTimeline.X) / (double)_rcTimeline.Width;
                    r = Math.Clamp(r, 0, 1);
                    _dragPosSec = r * Math.Max(0, dur);
                    SeekRequested?.Invoke(_dragPosSec);
                }

                if (!IsHudInteractive(e.Location))
                    ForwardMouseToUnderlying(WinMsg.WM_LBUTTONUP, e.Location, nint.Zero);

                StopDragging();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!IsHudInteractive(e.Location))
                ForwardMouseToUnderlying(WinMsg.WM_LBUTTONDBLCLK, e.Location, nint.Zero);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            RecalcLayout();

            bool overVol =
                _rcVolIconHit.Contains(e.Location) ||
                ((_volUiHot || _dragVol) && _rcVolPanelHit.Contains(e.Location));

            if (!overVol)
            {
                int wParam = (short)e.Delta << 16;
                ForwardMouseToUnderlying(WinMsg.WM_MOUSEWHEEL, e.Location, wParam);
                return;
            }

            // Mostra pannello mentre scrolli sull'icona
            _volUiHot = true;
            _volHotUntil = DateTime.UtcNow.AddMilliseconds(VolHoverLingerMs);

            float step = e.Delta > 0 ? 0.05f : -0.05f;
            SetVolumeFromUser(_vol + step);
        }

        private Rectangle ActiveZoneTop => new Rectangle(0, 0, Width, TopBarHeight + 10);
        private Rectangle ActiveZoneBottom => GetBottomInteractiveBounds();

        private Rectangle GetBottomInteractiveBounds()
        {
            if (Width <= 0 || Height <= 0)
                return Rectangle.Empty;

            Rectangle hot = Rectangle.Empty;

            void UnionRect(Rectangle rc)
            {
                if (rc.Width <= 0 || rc.Height <= 0)
                    return;

                hot = hot.IsEmpty ? rc : Rectangle.Union(hot, rc);
            }

            UnionRect(_rcTimelineHit);
            UnionRect(_rcBtnRemove);
            UnionRect(_rcBtnOpen);
            UnionRect(_rcBtnPlay);
            UnionRect(_rcBtnFull);
            UnionRect(_rcVolIconHit);

            if (_showBackFwd)
            {
                UnionRect(_rcBtnBack);
                UnionRect(_rcBtnFwd);
            }

            if (_showPrevNext)
            {
                UnionRect(_rcBtnPrev);
                UnionRect(_rcBtnNext);
            }

            if (_volUiHot || _dragVol)
                UnionRect(_rcVolPanelHit);

            if (hot.IsEmpty)
                return Rectangle.Empty;

            hot.Inflate(14, 14);
            return Rectangle.Intersect(new Rectangle(0, 0, Width, Height), hot);
        }

        private bool IsHudInteractive(Point p)
        {
            if (_opacity <= 0.05f) return false;

            RecalcLayout();

            if (ActiveZoneTop.Contains(p)) return true;
            if (ActiveZoneBottom.Contains(p)) return true;
            if (TimelineVisible && (_rcTimelineHit.Contains(p) || _rcTimeline.Contains(p))) return true;
            if (_volUiHot || _dragVol)
            {
                if (_rcVolPanelHit.Contains(p) || _rcVolIconHit.Contains(p)) return true;
            }
            else
            {
                if (_rcVolIconHit.Contains(p)) return true;
            }
            if (_rcTopSettings.Contains(p) || _rcTopInfo.Contains(p)) return true;
            return false;
        }
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            if (m.Msg == WM_NCHITTEST)
            {
                if (_drag || _dragVol)
                {
                    m.Result = (nint)1; // HTCLIENT
                    return;
                }

                if (_opacity <= 0.05f)
                {
                    m.Result = -1;
                    return;
                }

                RecalcLayout();

                int x = (short)((uint)m.LParam & 0xFFFF);
                int y = (short)(((uint)m.LParam >> 16) & 0xFFFF);
                Point client = PointToClient(new Point(x, y));

                var now = DateTime.UtcNow;

                bool hitVol =
                    _rcVolIconHit.Contains(client) ||
                    ((_volUiHot || _dragVol) && _rcVolPanelHit.Contains(client));

                if (hitVol)
                {
                    _volHotUntil = now.AddMilliseconds(VolHoverLingerMs);
                    m.Result = (nint)1;
                    return;
                }

                if (!IsHudInteractive(client))
                {
                    m.Result = -1;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        private enum WinMsg : uint { WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_MOUSEWHEEL = 0x020A }
        [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point p);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);
        private static nint MakeLParam(short low, short high) => high << 16 | low & 0xFFFF;
        private void ForwardMouseToUnderlying(WinMsg msg, Point clientPt, nint wParam)
        {
            Point screen = PointToScreen(clientPt);
            nint hTarget = WindowFromPoint(screen);
            if (hTarget == nint.Zero || hTarget == Handle) return;
            var pt = new POINT { X = screen.X, Y = screen.Y };
            if (!ScreenToClient(hTarget, ref pt)) return;
            nint lParam = MakeLParam((short)pt.X, (short)pt.Y);
            SendMessage(hTarget, (uint)msg, wParam, lParam);
        }
        private float VolumeFromY(int y)
        {
            if (_rcVolTrack.Height <= 1) return _vol;

            int top = _rcVolTrack.Top;
            int bottom = _rcVolTrack.Bottom;

            int yy = Math.Clamp(y, top, bottom);
            float t = (bottom - yy) / (float)Math.Max(1, bottom - top); // 0..1 (bottom=0, top=1)
            return Math.Clamp(t, 0f, 1f);
        }
        private string? GetVolumeIconPath()
        {
            // Priorità: mute
            if (IsMuted)
                return SvgPathVolMute;

            float v = Math.Clamp(_vol, 0f, 1f);

            // Se hai un'icona dedicata al "0", usala
            if (v <= 0.0001f)
                return SvgPathVolZero ?? SvgPathVolMute ?? SvgPathVolLow ?? SvgPathVolHigh;

            if (v < 0.33f)
                return SvgPathVolLow ?? SvgPathVolHigh ?? SvgPathVolZero;

            return SvgPathVolHigh ?? SvgPathVolLow ?? SvgPathVolZero;
        }

        private void RecalcLayout()
        {
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;

            _rcTopBar = new Rectangle(0, 0, w, TopBarHeight);
            _rcBottomBar = new Rectangle(0, Math.Max(0, h - BottomBackdropHeight), w, BottomBackdropHeight);

            int timelineY = h - TimelineYFromBottom;
            _rcTimeline = new Rectangle(16, timelineY, Math.Max(40, w - 32), TimelineHeight);
            _rcTimelineHit = Rectangle.Inflate(_rcTimeline, 0, 12);

            int btnTop = h - ControlYFromBottom;
            _rcBtnFull = new Rectangle(w - 16 - BtnSize, btnTop, BtnSize, BtnSize);

            // --- VOLUME: pannello unico (icona + track + label) ---
            _rcVolIcon = new Rectangle(_rcBtnFull.X - 12 - VolBtnSize, btnTop, VolBtnSize, VolBtnSize);

            _rcVolIconHit = Rectangle.Inflate(_rcVolIcon, 4, 4);

            // pannello unico centrato sull’icona e che la include
            int panelX = _rcVolIcon.X + (_rcVolIcon.Width - VolPanelW) / 2;
            int panelBottom = _rcVolIcon.Bottom + VolPanelBottomExtra;
            int panelY = panelBottom - VolPanelH;

            _rcVolPanel = new Rectangle(panelX, panelY, VolPanelW, VolPanelH);

            _rcVolPanelHit = Rectangle.Inflate(_rcVolPanel, 8, 8);

            _rcVolHoverOpen = _rcVolPanelHit;

            // track dentro al pannello: label in alto, icona in basso
            int trackTop = _rcVolPanel.Top + VolPanelPad + VolLabelHeight + 8;
            int trackBottom = _rcVolIcon.Top - 10;

            if (trackBottom < trackTop + 24) trackBottom = trackTop + 24;

            int trackH = Math.Max(24, trackBottom - trackTop);
            int trackX = _rcVolPanel.X + (_rcVolPanel.Width - VolTrackThickness) / 2;

            _rcVolTrack = new Rectangle(trackX, trackTop, VolTrackThickness, trackH);

            // knob
            float shownVol = IsMuted ? 0f : Math.Clamp(_vol, 0f, 1f);

            int cx = _rcVolTrack.X + _rcVolTrack.Width / 2;
            int yTop = _rcVolTrack.Top;
            int yBot = _rcVolTrack.Bottom;

            int knobY = yBot - (int)Math.Round(shownVol * Math.Max(1, (yBot - yTop)));
            knobY = Math.Clamp(knobY, yTop, yBot);

            _rcVolKnob = new Rectangle(cx - VolKnobRadius, knobY - VolKnobRadius, VolKnobRadius * 2, VolKnobRadius * 2);

            _rcBtnRemove = new Rectangle(16, btnTop, BtnSize, BtnSize);
            _rcBtnOpen = new Rectangle(_rcBtnRemove.Right + 8, btnTop, BtnSize, BtnSize);

            int leftBound = _rcBtnOpen.Right + 24;
            int rightBound = _rcVolIcon.X - 16 - ExtraBtnVsVolPad;
            int usable = Math.Max(0, rightBound - leftBound);
            int gap = Math.Clamp(GapDesired, 22, Math.Max(22, usable / 10));

            int playX = leftBound + (usable - BtnSize) / 2;
            playX = Math.Max(leftBound, Math.Min(playX, rightBound - BtnSize));
            _rcBtnPlay = new Rectangle(playX, btnTop, BtnSize, BtnSize);
            _rcBtnBack = new Rectangle(_rcBtnPlay.X - gap, btnTop, BtnSize, BtnSize);
            _rcBtnFwd = new Rectangle(_rcBtnPlay.Right + gap - BtnSize, btnTop, BtnSize, BtnSize);
            _rcBtnPrev = new Rectangle(_rcBtnBack.X - gap, btnTop, BtnSize, BtnSize);
            _rcBtnNext = new Rectangle(_rcBtnFwd.Right + (gap - BtnSize), btnTop, BtnSize, BtnSize);

            _showBackFwd = _rcBtnBack.X >= leftBound && _rcBtnFwd.Right <= rightBound;
            _showPrevNext = _rcBtnPrev.X >= leftBound && _rcBtnNext.Right <= rightBound;

            int topBtnTop = (_rcTopBar.Height - BtnSize) / 2;
            _rcTopSettings = new Rectangle(Math.Max(16, w - 16 - BtnSize), topBtnTop, BtnSize, BtnSize);
            _rcTopInfo = new Rectangle(Math.Max(16, _rcTopSettings.X - 16 - BtnSize), topBtnTop, BtnSize, BtnSize);
        }
        private static Color BakeOver(Color bg, Color fg, int alpha /*0..255*/)
        {
            float a = Math.Clamp(alpha, 0, 255) / 255f;
            int r = (int)Math.Round(bg.R * (1 - a) + fg.R * a);
            int g = (int)Math.Round(bg.G * (1 - a) + fg.G * a);
            int b = (int)Math.Round(bg.B * (1 - a) + fg.B * a);
            return Color.FromArgb(255, r, g, b); // OPACO
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_opacity <= 0.01f) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RecalcLayout();

            using (var bbTop = new LinearGradientBrush(
                _rcTopBar,
                Color.FromArgb((int)(118 * _opacity), 0, 0, 0),
                Color.FromArgb(0, 0, 0, 0),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(bbTop, _rcTopBar);
            }

            using (var bbBottom = new LinearGradientBrush(
                _rcBottomBar,
                Color.FromArgb(0, 0, 0, 0),
                Color.FromArgb((int)(150 * _opacity), 0, 0, 0),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(bbBottom, _rcBottomBar);
            }

            DrawTopBar(g);

            string info = GetInfoLine?.Invoke() ?? "";
            if (!string.IsNullOrWhiteSpace(info))
            {
                var infoFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                Size infoSize = TextRenderer.MeasureText(g, info, _fInfo, new Size(Math.Max(140, Width / 3), 0), infoFlags);
                int pillW = Math.Min(Math.Max(96, infoSize.Width + 20), Math.Max(120, Width / 3));
                int pillH = Math.Max(26, infoSize.Height + 8);
                int pillX = 12;
                int preferredY = _rcTimeline.Top - pillH - 10;
                int fallbackY = Height - InfoYFromBottom;
                int pillY = Math.Max(8, Math.Min(preferredY, fallbackY));
                var pillRect = new Rectangle(pillX, pillY, pillW, pillH);

                using (var pillPath = DrawHelpers.RoundRect(pillRect, 8))
                using (var pillFill = new SolidBrush(Color.FromArgb((int)(172 * _opacity), 0, 0, 0)))
                using (var pillPen = new Pen(Color.FromArgb((int)(72 * _opacity), 255, 255, 255)))
                {
                    g.FillPath(pillFill, pillPath);
                    g.DrawPath(pillPen, pillPath);
                }

                var textRect = Rectangle.Inflate(pillRect, -10, -4);
                TextRenderer.DrawText(
                    g,
                    info,
                    _fInfo,
                    textRect,
                    Color.FromArgb((int)(235 * _opacity), 235, 235, 238),
                    infoFlags);
            }

            // --- TIMELINE invariata ---
            if (TimelineVisible && GetTime != null)
            {
                var (pos, dur) = GetTime();

                using var tlBg = new SolidBrush(Color.FromArgb((int)(120 * _opacity), 200, 200, 200));
                using var tlFg = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                g.FillRectangle(tlBg, _rcTimeline);

                if (dur > 0)
                {
                    int wProg = (int)(_rcTimeline.Width * (pos / Math.Max(0.0001, dur)));
                    if (wProg > 0)
                        g.FillRectangle(tlFg, new Rectangle(_rcTimeline.X, _rcTimeline.Y,
                            Math.Min(wProg, _rcTimeline.Width),
                            _rcTimeline.Height));
                }

                bool dragging = (_drag || _remoteDrag);
                double dragSec = _drag ? _dragPosSec : _remoteDragPosSec;

                if (dragging && dur > 0)
                {
                    double clamped = Math.Clamp(dragSec, 0, dur);
                    int ghostW = (int)(_rcTimeline.Width * (clamped / dur));
                    using var ghost = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                    g.FillRectangle(ghost, new Rectangle(_rcTimeline.X, _rcTimeline.Y,
                        Math.Min(ghostW, _rcTimeline.Width),
                        _rcTimeline.Height));
                }

                {
                    double knobSec = dragging ? dragSec : pos;
                    knobSec = dur > 0 ? Math.Clamp(knobSec, 0, dur) : 0;
                    int knobX = _rcTimeline.X + (dur > 0 ? (int)(_rcTimeline.Width * (knobSec / dur)) : 0);
                    int d = dragging ? 14 : 12;
                    using var kn = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                    g.FillEllipse(kn,
                        knobX - d / 2,
                        _rcTimeline.Y + _rcTimeline.Height / 2 - d / 2,
                        d, d);

                    if (dragging && _preview != null)
                    {
                        int pw = _preview.Width, ph = _preview.Height;
                        int px = Math.Clamp(knobX - pw / 2, _rcTimeline.Left, _rcTimeline.Right - pw);
                        int py = _rcTimeline.Y - ph - 18;
                        var dest = new Rectangle(px, py, pw, ph);
                        if (_opacity < 1f)
                        {
                            var cm = new ColorMatrix { Matrix33 = Math.Clamp(_opacity, 0f, 1f) };
                            using var ia = new ImageAttributes();
                            ia.SetColorMatrix(cm);
                            g.DrawImage(_preview, dest, 0, 0, _preview.Width, _preview.Height,
                                GraphicsUnit.Pixel, ia);
                        }
                        else g.DrawImage(_preview, dest);

                        string pt = Fmt(dragSec);
                        var ptsz = g.MeasureString(pt, _fInfo);
                        using var bb2 = new SolidBrush(Color.FromArgb((int)(220 * _opacity), 0, 0, 0));
                        using var wb = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                        int boxW = Math.Max((int)(ptsz.Width + 10), pw);
                        g.FillRectangle(bb2, px, py - (int)ptsz.Height - 6, boxW, (int)ptsz.Height + 6);
                        g.DrawString(pt, _fInfo, wb, px + 5, py - ptsz.Height - 3);
                    }
                }

                {
                    var (pos2, dur2) = GetTime();
                    string tStr = dur2 > 0 ? $"{Fmt(pos2)} / {Fmt(dur2)}" : Fmt(pos2);
                    var tSz = g.MeasureString(tStr, _fTime);
                    using var brTime = new SolidBrush(Color.FromArgb((int)(230 * _opacity), 255, 255, 255));
                    float tx = _rcTimeline.Right - tSz.Width;
                    float ty = _rcTimeline.Y - tSz.Height - 6;
                    g.DrawString(tStr, _fTime, brTime, tx, ty);
                }
            }

            // --- BOTTONI: ora SVG (niente testo disegnato) ---
            DrawRoundButtonSvg(g, _rcBtnRemove, SvgPathRemove, IsPulsing(ButtonId.Remove));
            DrawRoundButtonSvg(g, _rcBtnOpen, SvgPathOpen, IsPulsing(ButtonId.Open));
            var playIcon = IsPlaying ? SvgPathPause : SvgPathPlay;
            DrawRoundButtonSvg(g, _rcBtnPlay, playIcon, IsPulsing(ButtonId.PlayPause));

            if (_showBackFwd)
            {
                DrawRoundButtonSvg(g, _rcBtnBack, SvgPathBack10, IsPulsing(ButtonId.Back10));
                DrawRoundButtonSvg(g, _rcBtnFwd, SvgPathFwd10, IsPulsing(ButtonId.Fwd10));
            }
            if (_showPrevNext)
            {
                DrawRoundButtonSvg(g, _rcBtnPrev, SvgPathPrevChapter, IsPulsing(ButtonId.PrevChapter));
                DrawRoundButtonSvg(g, _rcBtnNext, SvgPathNextChapter, IsPulsing(ButtonId.NextChapter));
            }
            DrawRoundButtonSvg(g, _rcBtnFull, SvgPathFullscreen, IsPulsing(ButtonId.Fullscreen));

            // --- VOLUME: pannello unico ---
            if (_volUiHot || _dragVol)
            {
                DrawVolumePanelUnified(g);
            }
            else
            {
                DrawRoundButtonSvg(g, _rcVolIcon, GetVolumeIconPath(), false);
            }

            // DPAD focus (telecomando)
            DrawDpadFocus(g);


            static string Fmt(double s)
            {
                if (double.IsNaN(s) || s < 0) s = 0;
                var ts = TimeSpan.FromSeconds(s);
                return ts.TotalHours >= 1
                    ? ts.ToString(@"hh\:mm\:ss")
                    : ts.ToString(@"mm\:ss");
            }
        }
        private void DrawVolumePanelUnified(Graphics g)
        {
            float opacity = Math.Clamp(_opacity, 0f, 1f);
            int fillAlpha = (int)(110 * opacity);
            int borderAlpha = (int)(90 * opacity);

            using (var gp = DrawHelpers.RoundRect(_rcVolPanel, _rcVolPanel.Width / 2))
            {
                using var fill = new SolidBrush(Color.FromArgb(fillAlpha, 255, 255, 255));
                using var border = new Pen(Color.FromArgb(borderAlpha, 255, 255, 255));
                g.FillPath(fill, gp);
                g.DrawPath(border, gp);
            }

            float shownVol = IsMuted ? 0f : Math.Clamp(_vol, 0f, 1f);
            string lbl = IsMuted ? "MUTO" : $"{(int)Math.Round(shownVol * 100)}%";
            var rcLbl = new Rectangle(_rcVolPanel.X, _rcVolPanel.Y + VolPanelPad - 1, _rcVolPanel.Width, VolLabelHeight);
            TextRenderer.DrawText(
                g, lbl, _fInfo, rcLbl,
                Color.FromArgb((int)(255 * opacity), 0, 0, 0),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            );

            int cx = _rcVolTrack.X + _rcVolTrack.Width / 2;
            int yTop = _rcVolTrack.Top;
            int yBot = _rcVolTrack.Bottom;

            using (var trkBg = new Pen(Color.FromArgb((int)(95 * opacity), 0, 0, 0), VolTrackThickness))
            {
                trkBg.StartCap = LineCap.Round;
                trkBg.EndCap = LineCap.Round;
                g.DrawLine(trkBg, cx, yTop, cx, yBot);
            }

            int knobY = yBot - (int)Math.Round(shownVol * Math.Max(1, (yBot - yTop)));
            knobY = Math.Clamp(knobY, yTop, yBot);

            using (var trkFill = new Pen(Color.FromArgb((int)(220 * opacity), 0, 0, 0), VolTrackThickness))
            {
                trkFill.StartCap = LineCap.Round;
                trkFill.EndCap = LineCap.Round;
                g.DrawLine(trkFill, cx, yBot, cx, knobY);
            }

            int r = VolKnobRadius;
            using (var knob = new SolidBrush(Color.FromArgb((int)(255 * opacity), 0, 0, 0)))
                g.FillEllipse(knob, cx - r, knobY - r, r * 2, r * 2);

            using (var knobOutline = new Pen(Color.FromArgb((int)(120 * opacity), 255, 255, 255)))
                g.DrawEllipse(knobOutline, cx - r, knobY - r, r * 2, r * 2);

            DrawSvgOnly(g, _rcVolIcon, GetVolumeIconPath(), Color.Black);
        }

        private void DrawSvgOnly(Graphics g, Rectangle r, string? svgPath, Color tint)
        {
            if (string.IsNullOrWhiteSpace(svgPath) || !File.Exists(svgPath))
                return;

            int s = Math.Max(16, Math.Min(r.Width, r.Height) - 10);
            int x = r.X + (r.Width - s) / 2;
            int y = r.Y + (r.Height - s) / 2;
            var dest = new Rectangle(x, y, s, s);

            using var bmp = GetSvgBitmap(svgPath, s, tint);

            float a = Math.Clamp(_opacity, 0f, 1f);
            if (a < 0.999f)
            {
                var cm = new ColorMatrix { Matrix33 = a };
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
            }
            else
            {
                g.DrawImage(bmp, dest);
            }
        }
        private void DrawTopBar(Graphics g)
        {
            int barH = 18;
            using (var barBr = new SolidBrush(Color.FromArgb((int)(255 * _opacity), Theme.Accent)))
                g.FillRectangle(barBr,
                    new Rectangle(16, (_rcTopBar.Height - barH) / 2, 4, barH));

            string title = !string.IsNullOrWhiteSpace(NowPlayingTitle)
                ? NowPlayingTitle
                : GetTitle?.Invoke() ?? string.Empty;

            int textLeft = 16 + 4 + 8;
            int textRight = _rcTopInfo.X - 12;
            if (!string.IsNullOrWhiteSpace(title) && textRight - textLeft > 10)
            {
                int h = Math.Max(_fTopTitle.Height + 2, 20);
                var rcTitle = new Rectangle(
                    textLeft,
                    (_rcTopBar.Height - h) / 2,
                    Math.Max(20, textRight - textLeft),
                    h);

                TextRenderer.DrawText(
                    g,
                    title,
                    _fTopTitle,
                    rcTitle,
                    Color.FromArgb((int)(240 * _opacity), 255, 255, 255),
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.VerticalCenter
                );
            }

            // TOP BUTTONS SVG
            DrawTopRoundButtonSvg(g, _rcTopInfo, SvgPathTopInfo, IsPulsing(ButtonId.TopInfo));
            DrawTopRoundButtonSvg(g, _rcTopSettings, SvgPathTopSettings, IsPulsing(ButtonId.TopSettings));
        }
        private void DrawTopRoundButtonSvg(Graphics g, Rectangle r, string? svgPath, bool pulse)
        {
            int aFill = (int)((pulse ? 170 : 110) * Math.Clamp(_opacity, 0f, 1f));
            using (var b = new SolidBrush(Color.FromArgb(aFill, 255, 255, 255)))
                g.FillEllipse(b, r);

            if (pulse)
            {
                using var glow = new Pen(Color.FromArgb((int)(220 * Math.Clamp(_opacity, 0f, 1f)), 255, 255, 255), 3f);
                g.DrawEllipse(glow, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
            }

            if (string.IsNullOrWhiteSpace(svgPath) || !File.Exists(svgPath))
                return; // niente fallback disegnato

            int s = Math.Max(18, Math.Min(r.Width, r.Height) - 10);
            int x = r.X + (r.Width - s) / 2;
            int y = r.Y + (r.Height - s) / 2;
            var dest = new Rectangle(x, y, s, s);

            using var bmp = GetSvgBitmap(svgPath, s, Color.Black);

            if (_opacity < 1f)
            {
                var cm = new ColorMatrix { Matrix33 = Math.Clamp(_opacity, 0f, 1f) };
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
            }
            else
            {
                g.DrawImage(bmp, dest);
            }
        }
        private void DrawRoundButtonSvg(Graphics gg, Rectangle r, string? svgPath, bool pulse = false)
        {
            int aFill = (int)((pulse ? 170 : 110) * Math.Clamp(_opacity, 0f, 1f));
            using (var b = new SolidBrush(Color.FromArgb(aFill, 255, 255, 255)))
                gg.FillEllipse(b, r);

            if (pulse)
            {
                using var glow = new Pen(Color.FromArgb((int)(220 * Math.Clamp(_opacity, 0f, 1f)), 255, 255, 255), 3f);
                gg.DrawEllipse(glow, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
            }

            if (string.IsNullOrWhiteSpace(svgPath) || !File.Exists(svgPath))
                return; // niente fallback disegnato

            int s = Math.Max(16, Math.Min(r.Width, r.Height) - 10);
            int x = r.X + (r.Width - s) / 2;
            int y = r.Y + (r.Height - s) / 2;
            var dest = new Rectangle(x, y, s, s);

            using var bmp = GetSvgBitmap(svgPath, s, Color.Black);

            if (_opacity < 1f)
            {
                var cm = new ColorMatrix { Matrix33 = Math.Clamp(_opacity, 0f, 1f) };
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                gg.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
            }
            else
            {
                gg.DrawImage(bmp, dest);
            }
        }
        private sealed class SvgCacheKeyComparer2 : IEqualityComparer<(string path, int sizePx, int argb)>
        {
            public bool Equals((string path, int sizePx, int argb) x, (string path, int sizePx, int argb) y)
                => x.sizePx == y.sizePx && x.argb == y.argb &&
                   string.Equals(x.path, y.path, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string path, int sizePx, int argb) obj)
                => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.path ?? string.Empty), obj.sizePx, obj.argb);
        }

        private readonly Dictionary<(string path, int sizePx, int argb), Bitmap> _svgCache2 =
            new(new SvgCacheKeyComparer2());

        private Bitmap GetSvgBitmap(string svgPath, int sizePx, Color tint)
        {
            int argb = tint.ToArgb();

            if (_svgCache2.TryGetValue((svgPath, sizePx, argb), out var cached) && cached != null)
                return (Bitmap)cached.Clone();

            Bitmap rendered = RenderSvgSkia(svgPath, sizePx, tint);

            try
            {
                if (_svgCache2.TryGetValue((svgPath, sizePx, argb), out var old) && old != null)
                    old.Dispose();
                _svgCache2[(svgPath, sizePx, argb)] = (Bitmap)rendered.Clone();
            }
            catch { }

            return rendered;
        }

        private static Bitmap RenderSvgSkia(string svgPath, int targetPx, Color tint)
        {
            var svg = new SKSvg();
            svg.Load(svgPath);
            if (svg.Picture == null) throw new InvalidOperationException("SVG Picture null: " + svgPath);

            var bounds = svg.Picture.CullRect;
            float srcW = bounds.Width;
            float srcH = bounds.Height;
            if (srcW <= 0 || srcH <= 0) throw new InvalidOperationException("SVG bounds invalid: " + svgPath);

            float scale = targetPx / Math.Max(srcW, srcH);
            int outW = Math.Max(1, (int)Math.Round(srcW * scale));
            int outH = Math.Max(1, (int)Math.Round(srcH * scale));

            using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);

            // tint (equivalente al tuo Color.Black). Se vuoi “as-is”, togli SaveLayer/paint e fai solo DrawPicture.
            using var paint = new SKPaint
            {
                ColorFilter = SKColorFilter.CreateBlendMode(
                    new SKColor(tint.R, tint.G, tint.B, 255),
                    SKBlendMode.SrcIn)
            };
            canvas.SaveLayer(paint);
            canvas.DrawPicture(svg.Picture);
            canvas.Restore();
            canvas.Flush();

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data.ToArray());
            using var tmp = Image.FromStream(ms);
            return new Bitmap(tmp);
        }
    }

    // ===================== DirectShow / COM interop =====================
    [ComImport, Guid("B196B28B-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpecifyPropertyPages { [PreserveSig] int GetPages(out CAUUID pPages); }

    [StructLayout(LayoutKind.Sequential)]
    struct CAUUID
    {
        public int cElems;
        public nint pElems;
    }

    [ComImport, Guid("B196B28D-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyPage
    {
        void SetPageSite(IPropertyPageSite pPageSite);
        void Activate(nint hWndParent, ref RECT pRect, int bModal);
        void Deactivate();
        void GetPageInfo(out PROPPAGEINFO pPageInfo);
        void SetObjects(uint cObjects, [MarshalAs(UnmanagedType.IUnknown)] ref object ppUnk);
        void Show(int nCmdShow);
        void Move(ref RECT pRect);
        [PreserveSig] int IsPageDirty();
        void Apply();
        void Help(string pszHelpDir);
        void TranslateAccelerator(ref MSG pMsg);
    }

    [ComImport, Guid("B196B28C-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyPageSite
    {
        [PreserveSig] int OnStatusChange(int dwFlags);
        [PreserveSig] int GetLocaleID(out int pLocaleID);
        [PreserveSig] int GetPageContainer([MarshalAs(UnmanagedType.IUnknown)] out object ppUnk);
        [PreserveSig] int TranslateAccelerator(ref MSG pMsg);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPPAGEINFO
    {
        public int cb;
        public nint pszTitle;
        public Size size;
        public nint pszDocString;
        public nint pszHelpFile;
        public int dwHelpContext;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint hWnd; public uint message; public nint wParam; public nint lParam; public uint time; public Point pt; }

    // ===================== Helpers DirectShow =====================
    internal static class DsHelpers
    {
        public static IBaseFilter? CreateFilterByFriendlyName(string? friendlyName)
        {
            if (string.IsNullOrWhiteSpace(friendlyName)) return null;
            Guid[] cats = {
                DSFilterCategory.LegacyAmFilterCategory,
                DSFilterCategory.AudioRendererCategory,
                DSFilterCategory.AudioCompressorCategory,
                DSFilterCategory.VideoCompressorCategory,
                DSFilterCategory.VideoInputDevice,
                DSFilterCategory.AudioInputDevice
            };
            foreach (var cat in cats)
            {
                foreach (var d in DsDevice.GetDevicesOfCat(cat))
                {
                    bool match =
                        d.Name.Equals(friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        d.Name.Contains(friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        friendlyName.Contains(d.Name, StringComparison.OrdinalIgnoreCase);
                    if (match)
                    {
                        var iid = typeof(IBaseFilter).GUID;
                        d.Mon.BindToObject(null, null, ref iid, out object obj);
                        return (IBaseFilter)obj;
                    }
                }
            }
            return null;
        }

        public static IBaseFilter? CreateFilterByClsid(Guid clsid)
        {
            try
            {
                var t = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
                var obj = Activator.CreateInstance(t);
                return obj as IBaseFilter;
            }
            catch { return null; }
        }
    }

    // ===================== Host property pages di DirectShow =====================
    internal sealed class DsPropPageHost : Panel, IPropertyPageSite, IDisposable
    {
        private readonly Panel _toolbar = new()
        {
            Height = 44,
            BackColor = Theme.PanelAlt,
            Dock = DockStyle.Top,
            Padding = new Padding(10, 8, 10, 8),
            Visible = false
        };
        private readonly ComboBox _pages = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        private readonly Panel _viewport = new()
        {
            BackColor = Theme.Panel,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 12, 12, 12),
            AutoScroll = true
        };

        private object? _filter;
        private IPropertyPage[] _pp = Array.Empty<IPropertyPage>();
        private int _active = -1;
        private bool _suspendNav;

        private IFilterGraph2? _tempGraph;
        private bool _addedToGraph;

        // offset top grosso per non tagliare la prima riga (LAV/madVR/MPC)
        public int ExtraTopPaddingDpiLogical = 64;

        public DsPropPageHost()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;
            Padding = new Padding(0);

            Controls.Add(_viewport);
            Controls.Add(_toolbar);

            _toolbar.Controls.Add(_pages);

            _pages.Left = 10; _pages.Top = 8;
            _pages.Width = Math.Max(220, _toolbar.Width - 20);
            _toolbar.Resize += (_, __) =>
            {
                _pages.Width = Math.Max(220, _toolbar.Width - 20);
            };
            _pages.SelectedIndexChanged += (_, __) =>
            {
                if (!_suspendNav) ActivateIndex(_pages.SelectedIndex);
            };

            Resize += (_, __) => RefitActivePage();
        }

        public void LoadFromFriendlyName(string friendlyName)
        {
            Clear();
            var f = DsHelpers.CreateFilterByFriendlyName(friendlyName);
            if (f == null) throw new ApplicationException($"Filtro \"{friendlyName}\" non trovato.");
            _filter = f;
            AttachToTempGraphIfNeeded();
            BuildPages();
        }
        public void LoadFromClsid(Guid clsid)
        {
            Clear();
            var f = DsHelpers.CreateFilterByClsid(clsid);
            if (f == null) throw new ApplicationException($"CLSID {clsid} non trovato/instanziabile.");
            _filter = f;
            AttachToTempGraphIfNeeded();
            BuildPages();
        }
        public void LoadFromFilter(IBaseFilter filter)
        {
            Clear();
            _filter = filter;
            AttachToTempGraphIfNeeded();
            BuildPages();
        }

        private void AttachToTempGraphIfNeeded()
        {
            try
            {
                if (_filter is not IBaseFilter bf) return;
                _tempGraph = (IFilterGraph2)new FilterGraph();
                int hr = _tempGraph.AddFilter(bf, "CfgTarget");
                _addedToGraph = hr >= 0;
            }
            catch
            {
                _addedToGraph = false;
                try { if (_tempGraph != null) Marshal.ReleaseComObject(_tempGraph); } catch { }
                _tempGraph = null;
            }
        }

        public void Apply()
        {
            foreach (var p in _pp)
            {
                try
                {
                    int hr = p.IsPageDirty();
                    if (hr != 0) // != S_OK
                        continue;

                    p.Apply();
                }
                catch
                {
                }
            }
        }

        public void Clear()
        {
            try
            {
                for (int i = 0; i < _pp.Length; i++)
                {
                    try { _pp[i].Show(0); } catch { }
                    try { _pp[i].Deactivate(); } catch { }
                    try { Marshal.ReleaseComObject(_pp[i]); } catch { }
                }
            }
            catch { }

            _pp = Array.Empty<IPropertyPage>();
            _pages.Items.Clear();
            _viewport.Controls.Clear();
            _active = -1;

            try
            {
                if (_addedToGraph && _tempGraph != null && _filter is IBaseFilter bf)
                {
                    try { _tempGraph.RemoveFilter(bf); } catch { }
                }
            }
            catch { }

            if (_filter != null && Marshal.IsComObject(_filter))
                try { Marshal.ReleaseComObject(_filter); } catch { }
            _filter = null;

            if (_tempGraph != null)
            {
                try { Marshal.ReleaseComObject(_tempGraph); } catch { }
                _tempGraph = null;
            }
            _addedToGraph = false;
        }

        private void BuildPages()
        {
            if (_filter == null) return;

            _suspendNav = true;
            _pages.Items.Clear();
            _pp = Array.Empty<IPropertyPage>();

            try
            {
                var spp = _filter as ISpecifyPropertyPages
                    ?? throw new ApplicationException("Il filtro non espone pagine di proprietà.");

                spp.GetPages(out var cauuid);
                try
                {
                    var okPages = new List<IPropertyPage>();
                    var okTitles = new List<string>();

                    if (cauuid.cElems > 0 && cauuid.pElems != nint.Zero)
                    {
                        for (int i = 0; i < cauuid.cElems; i++)
                        {
                            Guid clsid = Marshal.PtrToStructure<Guid>(
                                nint.Add(cauuid.pElems, i * Marshal.SizeOf<Guid>()));

                            try
                            {
                                var type = Type.GetTypeFromCLSID(clsid, true)!;
                                if (Activator.CreateInstance(type) is not IPropertyPage page)
                                    continue;

                                object unk = _filter!;
                                page.SetObjects(1, ref unk);
                                page.SetPageSite(this);
                                page.GetPageInfo(out var info);

                                string title = info.pszTitle != nint.Zero
                                    ? Marshal.PtrToStringUni(info.pszTitle) ?? $"Pagina {okPages.Count + 1}"
                                    : $"Pagina {okPages.Count + 1}";

                                okPages.Add(page);
                                okTitles.Add(title);
                            }
                            catch
                            {
                            }
                        }
                    }

                    if (okPages.Count == 0)
                    {
                        _pp = Array.Empty<IPropertyPage>();
                        _pages.Items.Add("(Nessuna property page disponibile)");
                        _pages.SelectedIndex = 0;
                        ShowPlaceholder("(Nessuna property page disponibile)");
                    }
                    else
                    {
                        _pp = okPages.ToArray();
                        foreach (var t in okTitles) _pages.Items.Add(t);
                        _pages.SelectedIndex = 0;
                    }
                }
                finally
                {
                    if (cauuid.pElems != nint.Zero) Marshal.FreeCoTaskMem(cauuid.pElems);
                }
            }
            catch (Exception ex)
            {
                _pp = Array.Empty<IPropertyPage>();
                _pages.Items.Add("Errore: " + ex.Message);
                _pages.SelectedIndex = 0;
                ShowPlaceholder("Errore: " + ex.Message);
            }
            finally
            {
                _suspendNav = false;
            }

            _toolbar.Visible = _pp.Length > 1;
            EnsureFirstPageActivated();
        }

        private void EnsureFirstPageActivated()
        {
            if (_pp.Length > 0)
                ActivateIndex(0);
        }

        private void ShowPlaceholder(string text)
        {
            _viewport.Controls.Clear();
            var lbl = new Label
            {
                Text = text,
                ForeColor = Theme.Muted,
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _viewport.Controls.Add(lbl);
        }

        private int TopEmbedOffset
        {
            get
            {
                float scale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
                int logical = ExtraTopPaddingDpiLogical;
                int px = (int)Math.Round(logical * scale);
                return Math.Max(32, px); // minimo 32px
            }
        }

        private RECT CalcRectToFit()
        {
            // diamo alla property page una rect virtuale MOLTO più alta.
            // così lei crea tutto il contenuto e noi scrolliamo.
            var view = _viewport.ClientRectangle;
            if (view.Width < 1 || view.Height < 1) view = new Rectangle(0, 0, 1, 1);

            int virtualHeight = view.Height + 1600;
            if (virtualHeight < 1000) virtualHeight = 1000;

            return new RECT
            {
                left = 0,
                top = TopEmbedOffset,
                right = view.Width - 1,
                bottom = virtualHeight
            };
        }

        private void ActivateIndex(int index)
        {
            if (_pp.Length == 0)
            {
                ShowPlaceholder("(Nessuna property page disponibile)");
                _active = -1;
                return;
            }
            if (index < 0 || index >= _pp.Length)
            {
                ShowPlaceholder("(Indice pagina non valido)");
                _active = -1;
                return;
            }

            var next = _pp[index];
            if (next == null)
            {
                ShowPlaceholder("(Pagina non disponibile)");
                _active = -1;
                return;
            }

            if (_active >= 0 && _active < _pp.Length && _pp[_active] != null)
            {
                try { _pp[_active].Show(0); } catch { }
                try { _pp[_active].Deactivate(); } catch { }
            }

            _viewport.Controls.Clear();
            _active = index;

            var rc = CalcRectToFit();
            try
            {
                next.Activate(_viewport.Handle, ref rc, 0);
                next.Show(5);
            }
            catch (Exception ex)
            {
                ShowPlaceholder("Errore nell'attivazione della pagina:\r\n" + ex.Message);
            }
        }

        private void RefitActivePage()
        {
            if (_active < 0 || _active >= _pp.Length) return;
            var rc = CalcRectToFit();
            try { _pp[_active].Move(ref rc); } catch { }
        }

        // IPropertyPageSite
        int IPropertyPageSite.OnStatusChange(int dwFlags) => 0;
        int IPropertyPageSite.GetLocaleID(out int pLocaleID) { pLocaleID = 0x0400; return 0; }
        int IPropertyPageSite.GetPageContainer(out object ppUnk) { ppUnk = this; return 0; }
        int IPropertyPageSite.TranslateAccelerator(ref MSG pMsg) => 1;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { Clear(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    // ===================== madVR settings embedder =====================
    internal sealed class MadVrSettingsEmbedder : Panel
    {
        private Process? _proc;
        private nint _wnd = nint.Zero;

        private readonly System.Windows.Forms.Timer _tick;
        private bool _embedded;
        private bool _started;

        private nint _oldParent = nint.Zero;
        private int _oldStyle = 0;
        private bool _savedOld = false;

        private uint _ctrlPid;

        private readonly Panel _placeholder = new() { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        private readonly Button _btnReopen = new() { Text = "Apri impostazioni madVR", Width = 240, Height = 28, FlatStyle = FlatStyle.System };

        private bool _sanitizedButtons;
        private bool _closedButtonRemoved;

        private static readonly Guid CLSID_madVR = new("E1A8B82A-32CE-4B0D-BE0D-AA68C772E423");

        public MadVrSettingsEmbedder()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;

            var lblInfo = new Label
            {
                Text = "Le impostazioni madVR non sono aperte.\r\nPremi il bottone per avviarle.",
                ForeColor = Theme.Muted,
                Dock = DockStyle.Top,
                Padding = new Padding(0, 0, 0, 10),
                Height = 68,
                TextAlign = ContentAlignment.MiddleCenter
            };

            _placeholder.Controls.Add(_btnReopen);
            _placeholder.Controls.Add(lblInfo);
            Controls.Add(_placeholder);

            Layout += (_, __) =>
            {
                _btnReopen.Left = (Width - _btnReopen.Width) / 2;
                _btnReopen.Top = (Height - _btnReopen.Height) / 2;
            };

            _btnReopen.Click += (_, __) => { EnsureStarted(); };

            _tick = new System.Windows.Forms.Timer { Interval = 250 };
            _tick.Tick += (_, __) =>
            {
                if (!_started) return;

                if (!_embedded)
                {
                    TryFindAndEmbed();
                }
                else
                {
                    if (_wnd == nint.Zero || !IsWindow(_wnd) || GetParent(_wnd) != Handle)
                    {
                        OnChildClosedOrLost();
                    }
                    else
                    {
                        MoveWindow(_wnd, 0, 0, ClientSize.Width, ClientSize.Height, true);
                        HardenCloseAndSanitizeButtons();
                    }
                }

                if (!_embedded && (_wnd == nint.Zero || !IsWindow(_wnd)))
                {
                    KickOpenSettingsViaPropPage();
                }
            };

            HandleDestroyed += (_, __) => Cleanup(true);
            Resize += (_, __) =>
            {
                if (_embedded && _wnd != nint.Zero && IsWindow(_wnd))
                    MoveWindow(_wnd, 0, 0, ClientSize.Width, ClientSize.Height, true);
            };
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsStarted => _started;

        public void EnsureStarted()
        {
            if (!_started)
            {
                _started = true;
                Start();
            }
            else
            {
                if (_wnd == nint.Zero || !IsWindow(_wnd) || !_embedded)
                {
                    KickOpenSettingsViaPropPage();
                    TryFindAndEmbed();
                }
                if (!_tick.Enabled) _tick.Start();
            }
        }

        private void Start()
        {
            string? folder = GetMadVrFolder();
            if (folder == null)
            {
                ShowPlaceholder("madVR non installato/registrato.");
                return;
            }

            string exe = Path.Combine(folder, "madHcCtrl.exe");
            if (!File.Exists(exe))
            {
                ShowPlaceholder($"madHcCtrl.exe non trovato in:\r\n{folder}");
                return;
            }

            try
            {
                var already = Process.GetProcessesByName("madHcCtrl").FirstOrDefault();
                if (already == null)
                {
                    _proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = false
                    });
                    _proc?.WaitForInputIdle(1000);
                    _ctrlPid = (uint)(_proc?.Id ?? 0);
                }
                else
                {
                    _ctrlPid = (uint)already.Id;
                }

                KickOpenSettingsViaPropPage();
                _tick.Start();
            }
            catch (Exception ex)
            {
                ShowPlaceholder("Errore avvio madVR: " + ex.Message);
            }
        }

        // madVR si apre da solo quando la tab madVR diventa visibile.
        // SettingsModal chiama host.EnsureStarted() quando entri in quella voce.

        private void TryFindAndEmbed()
        {
            if (_embedded) return;

            nint w = FindSettingsWindow();
            if (w == nint.Zero) return;

            _wnd = w;

            if (!_savedOld)
            {
                _oldParent = GetParent(_wnd);
                _oldStyle = GetWindowLong(_wnd, GWL_STYLE);
                _savedOld = true;
            }

            int style = _oldStyle;
            style = (style | WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS) &
                    ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX |
                      WS_MAXIMIZEBOX | WS_SYSMENU | WS_POPUP);
            SetWindowLong(_wnd, GWL_STYLE, style);
            SetParent(_wnd, Handle);
            MoveWindow(_wnd, 0, 0, ClientSize.Width, ClientSize.Height, true);
            ShowWindow(_wnd, SW_SHOW);

            _embedded = true;
            _placeholder.Visible = false;

            HardenCloseAndSanitizeButtons();
        }

        private void HardenCloseAndSanitizeButtons()
        {
            if (_wnd == nint.Zero || !IsWindow(_wnd)) return;

            if (!_closedButtonRemoved)
            {
                nint hMenu = GetSystemMenu(_wnd, false);
                if (hMenu != nint.Zero)
                {
                    RemoveMenu(hMenu, SC_CLOSE, MF_BYCOMMAND);
                    DrawMenuBar(_wnd);
                    _closedButtonRemoved = true;
                }
            }

            if (!_sanitizedButtons)
            {
                EnumChildWindows(_wnd, (h, l) =>
                {
                    if (!IsWindow(h)) return true;
                    string cls = GetClass(h).ToLowerInvariant();
                    if (cls != "button") return true;

                    int id = GetDlgCtrlID(h);
                    string txt = GetText(h).Trim().ToLowerInvariant();

                    // Disabilita "OK"
                    if (id == 1 || txt == "ok")
                    {
                        EnableWindow(h, false);
                        SetWindowText(h, "OK (disabilitato)");
                        return true;
                    }

                    // "Apply" -> stile push di default
                    if (txt.Contains("apply") || txt.Contains("applica"))
                    {
                        SendMessage(h, BM_SETSTYLE, BS_DEFPUSHBUTTON, 1);
                        return true;
                    }

                    return true;
                }, nint.Zero);

                _sanitizedButtons = true;
            }
        }

        private void OnChildClosedOrLost()
        {
            _embedded = false;
            _wnd = nint.Zero;
            _sanitizedButtons = false;
            _closedButtonRemoved = false;
            _placeholder.Visible = true;
        }

        private void KickOpenSettingsViaPropPage()
        {
            Panel? host = null;
            try
            {
                var filter = DsHelpers.CreateFilterByClsid(CLSID_madVR);
                if (filter == null) return;

                if (filter is not ISpecifyPropertyPages spp) { Release(filter); return; }

                spp.GetPages(out var cauuid);
                try
                {
                    if (cauuid.cElems <= 0 || cauuid.pElems == nint.Zero) return;

                    Guid pageClsid = Marshal.PtrToStructure<Guid>(cauuid.pElems);
                    var type = Type.GetTypeFromCLSID(pageClsid, true)!;
                    if (Activator.CreateInstance(type) is not IPropertyPage page) return;

                    object unk = filter;
                    page.SetObjects(1, ref unk);
                    page.SetPageSite(new DummySite());

                    host = new Panel
                    {
                        Visible = false,
                        Width = 5,
                        Height = 5,
                        Left = -10000,
                        Top = -10000
                    };
                    Controls.Add(host);
                    host.CreateControl();

                    var rc = new RECT { left = 0, top = 0, right = 320, bottom = 200 };
                    page.Activate(host.Handle, ref rc, 0);
                    page.Show(5);

                    nint btn = nint.Zero;
                    EnumChildWindows(host.Handle, (h, l) =>
                    {
                        if (!string.Equals(GetClass(h), "BUTTON", StringComparison.OrdinalIgnoreCase)) return true;
                        string txt = GetText(h).ToLowerInvariant();
                        if (txt.Contains("setting") || txt.Contains("impostaz"))
                        { btn = h; return false; }
                        return true;
                    }, nint.Zero);

                    if (btn != nint.Zero) SendMessage(btn, BM_CLICK, nint.Zero, nint.Zero);

                    page.Show(0);
                    page.Deactivate();
                    Release(page);
                }
                finally
                {
                    if (cauuid.pElems != nint.Zero) Marshal.FreeCoTaskMem(cauuid.pElems);
                    Release(filter);
                }
            }
            catch
            {
            }
            finally
            {
                if (host != null)
                {
                    try { Controls.Remove(host); } catch { }
                    try { host.Dispose(); } catch { }
                }
            }
        }

        private static void Release(object o)
        {
            try
            {
                if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
            }
            catch { }
        }

        private nint FindSettingsWindow()
        {
            nint found = nint.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                var title = GetText(h).ToLowerInvariant();
                if (title.Length == 0) return true;

                bool looksLike = title.Contains("madvr") &&
                                 (title.Contains("setting") || title.Contains("impostaz"));
                bool sameProc = _ctrlPid != 0 && pid == _ctrlPid && title.Contains("setting");

                if (looksLike || sameProc)
                {
                    found = h;
                    return false;
                }
                return true;
            }, nint.Zero);

            return found;
        }

        private void ShowPlaceholder(string text)
        {
            var lbl = _placeholder.Controls.OfType<Label>().FirstOrDefault();
            if (lbl != null)
                lbl.Text = text + "\r\nPremi il bottone per avviare.";
            _placeholder.Visible = true;
        }

        public void CloseSettingsWindow()
        {
            try
            {
                Cleanup(true);
                nint w = FindSettingsWindow();
                if (w != nint.Zero && IsWindow(w))
                {
                    PostMessage(w, WM_CLOSE, nint.Zero, nint.Zero);
                }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Cleanup(true);
            base.Dispose(disposing);
        }

        private void Cleanup(bool disposingControl)
        {
            try { _tick.Stop(); } catch { }

            try
            {
                if (_wnd != nint.Zero && IsWindow(_wnd))
                {
                    if (GetParent(_wnd) == Handle)
                    {
                        PostMessage(_wnd, WM_CLOSE, nint.Zero, nint.Zero);
                    }

                    if (_savedOld)
                    {
                        try { SetParent(_wnd, _oldParent); } catch { }
                        try { SetWindowLong(_wnd, GWL_STYLE, _oldStyle); } catch { }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _wnd = nint.Zero;
                _embedded = false;
                _savedOld = false;
                _oldParent = nint.Zero;
                _oldStyle = 0;
                _started = false;
                _sanitizedButtons = false;
                _closedButtonRemoved = false;
            }
        }

        private sealed class DummySite : IPropertyPageSite
        {
            public int OnStatusChange(int dwFlags) => 0;
            public int GetLocaleID(out int pLocaleID) { pLocaleID = 0x0400; return 0; }
            public int GetPageContainer(out object ppUnk) { ppUnk = this; return 0; }
            public int TranslateAccelerator(ref MSG pMsg) => 1;
        }

        // P/Invoke
        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int SW_SHOW = 5;
        private const int BM_CLICK = 0x00F5;
        private const int BM_SETSTYLE = 0x00F4;
        private const int BS_DEFPUSHBUTTON = 0x0001;
        private const int WM_CLOSE = 0x0010;

        private const int SC_CLOSE = 0xF060;
        private const int MF_BYCOMMAND = 0x0000;

        private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
        private delegate bool EnumChildProc(nint hWnd, nint lParam);

        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumChildWindows(nint hWndParent, EnumChildProc lpEnumFunc, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint hWndChild, nint hWndNewParent);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint GetParent(nint hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(nint hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool MoveWindow(nint hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindow(nint hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(nint hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessage(nint hWnd, int Msg, nint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint hWnd, int Msg, nint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnableWindow(nint hWnd, bool bEnable);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetDlgCtrlID(nint hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint hWnd, string lpString);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint GetSystemMenu(nint hWnd, bool bRevert);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveMenu(nint hMenu, int uPosition, int uFlags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool DrawMenuBar(nint hWnd);

        private static string GetText(nint hWnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        private static string GetClass(nint hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        private static string? GetMadVrFolder()
        {
            static string? ReadRegDefault(RegistryView view)
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                using var key = baseKey.OpenSubKey(@"CLSID\{E1A8B82A-32CE-4B0D-BE0D-AA68C772E423}\InprocServer32");
                return key?.GetValue(null) as string;
            }
            var axPath = ReadRegDefault(RegistryView.Registry64) ?? ReadRegDefault(RegistryView.Registry32);
            if (string.IsNullOrWhiteSpace(axPath)) return null;
            try { return Path.GetDirectoryName(axPath); } catch { return null; }
        }
    }

    // ===================== CONTESTO VIDEO SETTINGS / ENUM =====================
    public enum MadVrHdrMode { Auto = 0, PassthroughHdr, ToneMapHdrToSdr, LutHdrToSdr }
    public enum MadVrCategoryPreset { RendererDefault = 0, Profile1, Profile2, Profile3, Profile4, Profile5, Profile6 }
    public enum MadVrFpsChoice { Adapt = 0, Force60 = 60, Force24 = 24 }

    public sealed class VideoSettings
    {
        public int TargetFps { get; set; }
        public bool AllowUpscaling { get; set; }
        public bool PreferBitstream { get; set; }

        public MadVrHdrMode HdrMode { get; set; } = MadVrHdrMode.Auto;
        public MadVrCategoryPreset ChromaPreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset ImageUpscalePreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset ImageDownscalePreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset RefinementPreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrFpsChoice FpsChoice { get; set; } = MadVrFpsChoice.Adapt;
    }

    // ===================== UI KIT (bottoni stile screenshot) =====================
    internal static class UiKit
    {
        // bottone stile outline rettangolare 1px chiaro (sidebar e footer)
        public static Button MakeOutlineButton(string text, bool leftAlign = false, bool useNavBg = false)
        {
            var b = new Button
            {
                Text = text.ToUpperInvariant(),
                FlatStyle = FlatStyle.Flat,
                BackColor = useNavBg ? Theme.Nav : Theme.Panel,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Height = 32,
                Dock = DockStyle.Top,
                TextAlign = leftAlign ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter,
                Padding = leftAlign ? new Padding(8, 0, 8, 0) : new Padding(0),
                Margin = new Padding(0, 0, 0, 8),
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = useNavBg ? Theme.Nav : Theme.Panel;
            b.FlatAppearance.MouseDownBackColor = useNavBg ? Theme.Nav : Theme.Panel;
            return b;
        }

        // label titolo gruppo
        public static Label MakeGroupHeader(string text)
        {
            return new Label
            {
                Text = text,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };
        }

        // label descrizione gruppo
        public static Label MakeGroupSub(string text)
        {
            return new Label
            {
                Text = text,
                ForeColor = Theme.SubtleText,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8),
                MaximumSize = new Size(1000, 0)
            };
        }

        public static RadioButton MakeRadio(string txt)
        {
            return new RadioButton
            {
                Text = txt,
                AutoSize = true,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 2)
            };
        }

        public static CheckBox MakeCheck(string txt)
        {
            return new CheckBox
            {
                Text = txt,
                AutoSize = true,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 2)
            };
        }

        public static ComboBox MakePresetCombo()
        {
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.System,
                Font = new Font("Segoe UI", 9f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                MinimumSize = new Size(200, 0),
                Margin = new Padding(0, 2, 0, 8)
            };
            cb.Items.Add("Default (renderer)");
            cb.Items.Add("Profile 1");
            cb.Items.Add("Profile 2");
            cb.Items.Add("Profile 3");
            cb.Items.Add("Profile 4");
            cb.Items.Add("Profile 5");
            cb.Items.Add("Profile 6");
            cb.SelectedIndex = 0;
            return cb;
        }

        public static MadVrCategoryPreset ComboToPreset(ComboBox cb)
        {
            return cb.SelectedIndex switch
            {
                0 => MadVrCategoryPreset.RendererDefault,
                1 => MadVrCategoryPreset.Profile1,
                2 => MadVrCategoryPreset.Profile2,
                3 => MadVrCategoryPreset.Profile3,
                4 => MadVrCategoryPreset.Profile4,
                5 => MadVrCategoryPreset.Profile5,
                6 => MadVrCategoryPreset.Profile6,
                _ => MadVrCategoryPreset.RendererDefault
            };
        }
    }

    // ===================== SWITCH FREQUENZA MONITOR =====================
    internal sealed class DisplayModeSwitcher
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, nint hwnd, int dwflags, nint lParam);

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int EDS_RAWMODE = 0x00000002;
        private const int CDS_FULLSCREEN = 0x00000004;
        private const int CDS_UPDATEREGISTRY = 0x00000001;

        private string? _device;
        private DEVMODE? _original;

        public bool SwitchToNearest(Screen screen, int desiredFps)
        {
            try
            {
                _device = screen.DeviceName;
                var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettingsEx(_device, ENUM_CURRENT_SETTINGS, ref cur, 0))
                    return false;
                _original = cur;

                var best = cur;
                int target = desiredFps <= 25 ? 24 : 60;
                int alt = target == 24 ? 23 : 59;
                int bestHz = cur.dmDisplayFrequency;
                int bestDelta = int.MaxValue;

                DEVMODE mode = new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                for (int i = 0; EnumDisplaySettingsEx(_device, i, ref mode, EDS_RAWMODE); i++)
                {
                    if (mode.dmPelsWidth != cur.dmPelsWidth || mode.dmPelsHeight != cur.dmPelsHeight)
                        continue;
                    int hz = mode.dmDisplayFrequency;
                    if (hz <= 0) continue;
                    int delta = Math.Min(Math.Abs(hz - target), Math.Abs(hz - alt));
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        best = mode;
                        bestHz = hz;
                    }
                }

                if (bestHz == cur.dmDisplayFrequency) return true;
                int r = ChangeDisplaySettingsEx(_device, ref best, nint.Zero,
                    CDS_FULLSCREEN | CDS_UPDATEREGISTRY, nint.Zero);
                return r == 0;
            }
            catch { return false; }
        }

        public void RestoreIfChanged()
        {
            try
            {
                if (_device == null || _original == null) return;
                var orig = _original.Value;
                ChangeDisplaySettingsEx(_device, ref orig, nint.Zero,
                    CDS_FULLSCREEN | CDS_UPDATEREGISTRY, nint.Zero);
            }
            catch { }
            finally
            {
                _original = null;
                _device = null;
            }
        }
    }

    // ===================== WIN32 helper =====================
    internal static class Win32
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            nint hWnd,
            nint hWndInsertAfter,
            int X, int Y, int cx, int cy,
            uint uFlags);

        public static readonly nint HWND_TOPMOST = new nint(-1);
        public static readonly nint HWND_NOTOPMOST = new nint(-2);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;
    }
}
