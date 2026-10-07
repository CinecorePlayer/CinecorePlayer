#nullable enable
using CinecorePlayer2025;
using CinecorePlayer2025.Utilities;
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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    internal sealed class HudOverlay : Control
    {
        private string _uiLanguage = "it";
        private bool UiEnglish => string.Equals(_uiLanguage, "en", StringComparison.OrdinalIgnoreCase);
        private string L(string italian, string english) => UiEnglish ? english : italian;

        public void SetLanguage(string? language)
        {
            _uiLanguage = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
            Invalidate();
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<bool>? CanChangeVolume { get; set; }
        public Func<float, bool>? ExternalVolumeRequested { get; set; }
        public Func<int, bool>? ExternalVolumeStepRequested { get; set; }
        public Func<bool>? ExternalMuteRequested { get; set; }
        /// <summary>Volume as the linked receiver reports it (e.g. "-35 dB"); null when the player owns the volume.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<string?>? ExternalVolumeText { get; set; }
        private bool VolumeEditable => CanChangeVolume?.Invoke() ?? true;
        private float _externalVolume = 1f;
        public event Action? OpenClicked;
        public event Action? PlayPauseClicked;
        public event Action? StopClicked;
        public event Action? FullscreenClicked;
        public event Action? SkipBack10Clicked;
        public event Action? SkipForward10Clicked;
        public event Action? SkipIntroPromptClicked;
        public event Action? NextEpisodePromptClicked;
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
            if (fireEvents && !VolumeEditable) return;
            if (fireEvents && ExternalMuteRequested?.Invoke() == true) return;
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
            if (!VolumeEditable) return;
            v = Math.Clamp(v, 0f, 1f);
            if (ExternalVolumeRequested?.Invoke(v) == true) return;

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

        public event Action? TopInfoClicked;
        public event Action? TopSettingsClicked;

        public event Action<float>? VolumeChanged;
        public event Action<double>? SeekRequested;
        public event Action<double, Point>? PreviewRequested;
        public event Action? PreviewDismissed;
        public event Action? PreviewLayoutChanged;

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
        [DefaultValue(240)] public int FadeOutMs { get; set; } = 240;
        public Func<bool>? InteractionBlocked { get; set; }
        private bool _timelineVisible;
        [DefaultValue(false)]
        public bool TimelineVisible
        {
            get => _timelineVisible;
            set
            {
                if (_timelineVisible == value)
                    return;

                _timelineVisible = value;
                Invalidate();
            }
        }
        private bool _infoOverlayMode;
        private int _infoOverlayReservedHeight;

        [DefaultValue(false)]
        public bool InfoOverlayMode
        {
            get => _infoOverlayMode;
            set
            {
                if (_infoOverlayMode == value)
                    return;

                _infoOverlayMode = value;
                if (value)
                {
                    CancelScanHold();
                }
                Invalidate();
            }
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int InfoOverlayReservedHeight
        {
            get => _infoOverlayReservedHeight;
            set
            {
                int next = Math.Max(0, value);
                if (_infoOverlayReservedHeight == next)
                    return;

                _infoOverlayReservedHeight = next;
                Invalidate();
            }
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Rectangle TopOverlayBounds
        {
            get
            {
                RecalcLayout();
                return new Rectangle(0, 0, Width, Math.Min((int)Math.Round(140 * HudLayoutScale), Height / 3));
            }
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Rectangle BottomOverlayBounds
        {
            get
            {
                RecalcLayout();
                int y = Math.Max(0, Height - (int)Math.Round(220 * HudLayoutScale));
                if (_preview != null && GetTime != null)
                {
                    double anchor = _drag ? _dragPosSec : (_remoteDrag ? _remoteDragPosSec : _timelineHoverPreviewSec);
                    var card = PreviewCardBounds(_preview, anchor, GetTime().Item2);
                    if (!card.IsEmpty) y = Math.Min(y, Math.Max(0, card.Top - 8));
                }
                return Rectangle.Intersect(new Rectangle(0, 0, Width, Height), new Rectangle(0, y, Width, Height - y));
            }
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Rectangle PromptOverlayBounds
        {
            get
            {
                RecalcLayout();
                if (_rcPromptBar.Width <= 0 || _rcPromptBar.Height <= 0)
                    return Rectangle.Empty;

                var rc = Rectangle.Inflate(_rcPromptBar, 0, 10);
                return Rectangle.Intersect(new Rectangle(0, 0, Width, Height), rc);
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Image? IconInfo { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Image? IconSettings { get; set; }

        private const int TopBarHeight = 60;
        private const int BottomBackdropHeight = 122;
        private const int PromptBarHeight = 54;
        private const int PromptBarGap = 8;
        private const int BtnSize = 34;
        private const int PlayBtnSize = 58;
        private const int GapDesired = 68;
        private const int ExtraBtnVsVolPad = 2;
        private const int TimelineHeight = 6;
        private const int TimelinePreviewIntervalMs = 90;
        private const int VolBtnSize = 34;

        private const int VolPanelW = 40;
        private const int VolPanelH = 170;
        private const int VolPanelPad = 8;
        private const int VolPanelBottomExtra = 8;
        private const int VolLabelHeight = 18;
        private const int VolTrackThickness = 4;
        private const int VolKnobRadius = 5;

        private Font _fInfo = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f);
        private Font _fTime = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9f, FontStyle.Bold);
        private Font _fTopTitle = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 12.5f);
        private Font _fPrompt = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 10f);
        private Font _fSymbol = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 11f, FontStyle.Bold);
        private int _fontDpi;

        // I caratteri seguono lo schermo su cui si trova la finestra: spostando il player su uno schermo
        // con un'altra scala (dal televisore al proiettore) testi e orari restavano della misura di prima.
        private void RebuildFontsForDpi()
        {
            int dpi = Math.Max(48, DeviceDpi);
            if (dpi == _fontDpi) return;
            _fontDpi = dpi;
            Font Swap(Font old, string family, float points, FontStyle style)
            {
                Font next = global::CinecorePlayer2025.AppFonts.CreateForDpi(family, points, style, dpi);
                try { old.Dispose(); } catch { }
                return next;
            }
            _fInfo = Swap(_fInfo, "Segoe UI", 9f, FontStyle.Regular);
            _fTime = Swap(_fTime, "Segoe UI", 9f, FontStyle.Bold);
            _fTopTitle = Swap(_fTopTitle, "Segoe UI Semibold", 12.5f, FontStyle.Regular);
            _fPrompt = Swap(_fPrompt, "Segoe UI Semibold", 10f, FontStyle.Regular);
            _fSymbol = Swap(_fSymbol, "Segoe UI", 11f, FontStyle.Bold);
        }

        private readonly System.Windows.Forms.Timer _fade;
        // Long-press su Back10/Fwd10: avvia lo scan (0.5x, 1x, 2x, 3x, 4x...) come telecomando
        private readonly System.Windows.Forms.Timer _scanHold;
        private ButtonId _scanHoldBtn = ButtonId.None;
        private bool _scanHoldTriggered = false;
        private DateTime _scanHoldStartAt = DateTime.MinValue;
        private const int ScanHoldTriggerMs = 420;
        private float _opacity = 1f;
        private long _fadeStartedAt;
        private float _fadeFrom = 1f;
        private float _fadeTarget = 1f;
        private bool _manualFadeOut;
        // Il puntatore sopra i comandi tiene aperto l'HUD solo se si e' mosso da quando l'HUD e'
        // comparso. Un cursore lasciato fermo (e nascosto) in basso teneva l'HUD aperto per sempre
        // quando lo si richiamava da tastiera o dal telecomando: sembrava chiudersi "a caso",
        // secondo dove era rimasto il mouse.
        private bool _pointerSeenSinceShown;
        public void NotePhysicalPointer() => _pointerSeenSinceShown = true;
        public bool IsPointerInteractionActive => _drag || _dragVol || _remoteDrag || _scanHoldBtn != ButtonId.None ||
            (_pointerSeenSinceShown && Visible && _opacity > .05f && IsHandleCreated && IsHudInteractive(PointToClient(Control.MousePosition)));
        public bool HasVisibilityLease => DateTime.UtcNow < _forceShowUntil ||
            (DateTime.UtcNow - _lastMove).TotalMilliseconds < IdleHideDelayMs;
        public void BeginFadeOut()
        {
            if (_manualFadeOut || IsPointerInteractionActive || HasVisibilityLease) return;
            _manualFadeOut = true;
            RequestFade(0f);
        }
        private void AdvanceFadeIn()
        {
            _manualFadeOut = false;
            RequestFade(1f);
        }
        private void RequestFade(float target)
        {
            // Repeated wake/idle requests must not restart an animation midway.
            if (_fadeTarget == target) return;
            _fadeFrom = _opacity;
            _fadeTarget = target;
            _fadeStartedAt = Stopwatch.GetTimestamp();
            _fadeElapsedMs = 0; _fadeLastTick = _fadeStartedAt;
        }
        // Tempo di dissolvenza contato a passi di al massimo due fotogrammi: se l'interfaccia resta
        // ferma per qualche centinaio di millisecondi (finestra dell'HUD che si apre, video in 4K) la
        // dissolvenza riprende da dove era, invece di trovarsi gia' finita al primo ridisegno.
        private double _fadeElapsedMs;
        private long _fadeLastTick;
        private void AdvanceFade()
        {
            if (_opacity == _fadeTarget) return;
            double duration = _fadeTarget > 0 ? 280 : Math.Max(1, FadeOutMs);
            long tick = Stopwatch.GetTimestamp();
            _fadeElapsedMs += Math.Min(34, Stopwatch.GetElapsedTime(_fadeLastTick, tick).TotalMilliseconds);
            _fadeLastTick = tick;
            float t = (float)Math.Clamp(_fadeElapsedMs / duration, 0, 1);
            _opacity = t >= 1 ? _fadeTarget : _fadeFrom + (_fadeTarget - _fadeFrom) * t * t * (3 - 2 * t);
            // Durante la dissolvenza si ridisegnano solo le fasce dove ci sono i comandi (in 4K ridisegnare
            // tutta la finestra a ogni passo costava troppo); a dissolvenza finita un ridisegno completo.
            if (_paintedHostFade && HostFadeNow && _paintedOpacity >= 1f)
            {
                // Gia' disegnato a piena opacita': cambia solo l'alfa della finestra.
                if (_opacity == 0) _paintedOpacity = 0;
                HostAlphaChanged?.Invoke();
            }
            else if (_opacity == _fadeTarget) Invalidate(); else InvalidateHudBands();
            PumpFade();
            // Passi regolari durante il fade (~120/s), cadenza normale a riposo.
            if (_fade != null) _fade.Interval = _opacity == _fadeTarget ? 15 : global::CinecorePlayer2025.Utilities.AnimationClock.FrameIntervalMs;
            if (_opacity == 0) _paintedOpacity = 0;
            if (_opacity == 0 && (_manualFadeOut || AutoHide) && !PromptActive)
                Visible = false;
        }
        private void InvalidateHudBands()
        {
            if (Width <= 0 || Height <= 0) return;
            RecalcLayout();
            Rectangle top = ActiveZoneTop, bottom = ActiveZoneBottom;
            if (!top.IsEmpty) Invalidate(Rectangle.Inflate(top, 0, 24));
            int bandTop = Math.Min(bottom.IsEmpty ? Height : bottom.Top, _rcTimelineHit.IsEmpty ? Height : _rcTimelineHit.Top) - 48;
            Invalidate(Rectangle.FromLTRB(0, Math.Max(0, bandTop), Width, Height));
            Rectangle prompt = PromptOverlayBounds, preview = PreviewInteractionBounds();
            if (!prompt.IsEmpty) Invalidate(Rectangle.Inflate(prompt, 12, 12));
            if (!preview.IsEmpty) Invalidate(Rectangle.Inflate(preview, 12, 12));
        }

        // I timer di Windows passano dopo mouse e ridisegni: muovendo il mouse senza sosta la dissolvenza
        // restava ferma e l'HUD non compariva finche' il mouse non si fermava. Mentre una dissolvenza e' in
        // corso i passi arrivano anche come messaggi accodati, che hanno la precedenza sull'input.
        private System.Threading.Timer? _fadePump;
        private int _fadePumpQueued;
        private bool _fadePumping;
        private void PumpFade()
        {
            bool needed = Visible && IsHandleCreated && !IsDisposed && _opacity != _fadeTarget;
            if (needed == _fadePumping) return;
            _fadePumping = needed;
            if (!needed) { try { _fadePump?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { } return; }
            _fadePump ??= new System.Threading.Timer(_ =>
            {
                if (System.Threading.Interlocked.Exchange(ref _fadePumpQueued, 1) != 0) return;
                try
                {
                    if (IsDisposed || !IsHandleCreated) { _fadePumpQueued = 0; return; }
                    BeginInvoke(new Action(() =>
                    {
                        _fadePumpQueued = 0;
                        if (IsDisposed) return;
                        if (Visible && _opacity != _fadeTarget) AdvanceFade(); else PumpFade();
                    }));
                }
                catch { _fadePumpQueued = 0; }
            });
            try { _fadePump.Change(16, 16); } catch { }
        }

        /// <summary>Tiene aperto l'HUD gia' visibile senza ridisegnarlo: per il movimento continuo del mouse.</summary>
        public void KeepAlive(int ms)
        {
            var now = DateTime.UtcNow;
            _lastMove = now;
            var until = now.AddMilliseconds(Math.Max(250, ms));
            if (until > _forceShowUntil) _forceShowUntil = until;
            AdvanceFadeIn();
            PumpFade();
        }

        private DateTime _nextTimelinePaintUtc;
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
        private bool _timelinePreviewHover;
        private double _timelineHoverPreviewSec;
        private Point _lastPhysicalMouseScreenPos = Point.Empty;
        private bool _hasPhysicalMouseScreenPos = false;
        private const int PhysicalMouseDeadzonePx = 2;

        private Rectangle _rcTopBar, _rcBottomBar, _rcBottomPanel, _rcPromptBar;
        private Rectangle _rcTimeline, _rcTimelineHit;
        private Rectangle _rcBtnRemove, _rcBtnOpen, _rcBtnPlay, _rcBtnBack, _rcBtnFwd, _rcBtnPrev, _rcBtnNext, _rcBtnFull;
        private Rectangle _rcSkipIntroPrompt, _rcNextEpisodePrompt;
        private Rectangle _rcVolTrack;
        private Rectangle _rcTopInfo;
        private Rectangle _rcTopSettings;
        private bool _showPrevNext = true, _showBackFwd = true;
        private bool _showSkipIntroPrompt = false;
        private bool _showNextEpisodePrompt = false;

        public void SetIntroOutroPrompts(bool showSkipIntro, bool showNextEpisode)
        {
            if (_showSkipIntroPrompt == showSkipIntro && _showNextEpisodePrompt == showNextEpisode)
                return;

            _showSkipIntroPrompt = showSkipIntro;
            _showNextEpisodePrompt = showNextEpisode;
            if (showSkipIntro || showNextEpisode)
                ShowOnce(3500);
            // Il pulsante teneva visibile il controllo anche a comandi svaniti: finito lui, si chiude.
            else if (_opacity == 0 && (_manualFadeOut || AutoHide))
                Visible = false;
            Invalidate();
        }

        /// <summary>"Salta intro" / "Prossimo episodio" a schermo: resta anche quando i comandi svaniscono.</summary>
        public bool PromptActive => _showSkipIntroPrompt || _showNextEpisodePrompt;

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
            TopInfo,
            TopSettings
        }
        private ButtonId _pulseBtn = ButtonId.None;
        private DateTime _pulseUntil = DateTime.MinValue;
        private ButtonId _lastInvokedButton = ButtonId.None;
        private DateTime _lastButtonInvokeAt = DateTime.MinValue;
        public void Pulse(ButtonId btn, int ms = 180)
        {
            _pulseBtn = btn;
            _pulseUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(60, ms));
            Invalidate();
        }
        private bool IsPulsing(ButtonId btn) => _pulseBtn == btn && DateTime.UtcNow < _pulseUntil;

        private void InvokeHudButton(ButtonId btn, Action? action, int minIntervalMs = 120)
        {
            if (action == null)
                return;

            var now = DateTime.UtcNow;
            if (_lastInvokedButton == btn && (now - _lastButtonInvokeAt).TotalMilliseconds < minIntervalMs)
                return;

            _lastInvokedButton = btn;
            _lastButtonInvokeAt = now;

            try { action(); } catch { }
            try { Pulse(btn); } catch { }
            try { ShowOnce(1400); } catch { }
        }

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

                    _dpadSel = ButtonId.TopInfo;
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
                case ButtonId.Remove: InvokeHudButton(ButtonId.Remove, StopClicked); break;
                case ButtonId.Open: InvokeHudButton(ButtonId.Open, OpenClicked, 450); break;
                case ButtonId.PlayPause: InvokeHudButton(ButtonId.PlayPause, PlayPauseClicked); break;
                case ButtonId.Back10: InvokeHudButton(ButtonId.Back10, SkipBack10Clicked); break;
                case ButtonId.Fwd10: InvokeHudButton(ButtonId.Fwd10, SkipForward10Clicked); break;
                case ButtonId.PrevChapter: InvokeHudButton(ButtonId.PrevChapter, PrevChapterClicked); break;
                case ButtonId.NextChapter: InvokeHudButton(ButtonId.NextChapter, NextChapterClicked); break;
                case ButtonId.Fullscreen: InvokeHudButton(ButtonId.Fullscreen, FullscreenClicked, 450); break;
                case ButtonId.TopInfo: InvokeHudButton(ButtonId.TopInfo, TopInfoClicked, 180); break;
                case ButtonId.TopSettings: InvokeHudButton(ButtonId.TopSettings, TopSettingsClicked, 220); break;
                case ButtonId.Volume:
                    ToggleMuteFromUser();
                    break;
            }

            ShowOnce(2500);
        }

        public void ToggleMuteFromUser()
        {
            if (!VolumeEditable) return;
            SetMutedInternal(!IsMuted);
        }

        private void NormalizeDpadSelection()
        {
            // se un bottone non è disegnato (prev/next/back/fwd), saltalo
            if (IsTopButton(_dpadSel))
            {
                return;
            }

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
            new() { ButtonId.TopSettings, ButtonId.TopInfo };

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

            if (VolumeEditable) row.Add(ButtonId.Volume);
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
                case ButtonId.TopInfo: return _rcTopInfo;
                case ButtonId.TopSettings: return _rcTopSettings;
                case ButtonId.Volume: return VolumeEditable ? _rcVolIcon : Rectangle.Empty;
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

            bool overVolume = VolumeEditable && (_rcVolIconHit.Contains(pt) || _rcVolPanelHit.Contains(pt));
            if (_dragVol || overVolume)
            {
                if (!_volUiHot)
                {
                    _volUiHot = true;
                    Invalidate();
                }
                _volHotUntil = now.AddMilliseconds(VolHoverLingerMs);
                return;
            }

            if (_volUiHot && now >= _volHotUntil)
            {
                _volUiHot = false;
                Invalidate();
            }
        }

        // Control.Visible includes the parent window's state. Keep the requested
        // state separately so a hidden host can be woken by its child HUD.
        [Browsable(false)]
        public bool RequestedVisible { get; private set; }
        protected override void SetVisibleCore(bool value)
        {
            bool opening = value && !RequestedVisible;
            RequestedVisible = value;
            if (opening) { _opacity = 0f; _fadeTarget = 0f; _manualFadeOut = false; _pointerSeenSinceShown = false; }
            base.SetVisibleCore(value);
            if (opening) ShowOnce(1800);
            if (value) _fade?.Start();
            else _fade?.Stop();
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
                SvgPathRemove ??= AssetIconService.Resolve("close");
                SvgPathOpen ??= AssetIconService.Resolve("library");
                SvgPathPlay ??= AssetIconService.Resolve("play");
                SvgPathPause ??= AssetIconService.Resolve("pause");
                SvgPathBack10 ??= AssetIconService.Resolve("skip-back");
                SvgPathFwd10 ??= AssetIconService.Resolve("skip-forward");
                SvgPathPrevChapter ??= AssetIconService.Resolve("previous");
                SvgPathNextChapter ??= AssetIconService.Resolve("next");
                SvgPathFullscreen ??= AssetIconService.Resolve("maximize");
                SvgPathTopInfo ??= AssetIconService.Resolve("info");
                SvgPathTopSettings ??= AssetIconService.Resolve("settings");
                SvgPathVolMute ??= AssetIconService.Resolve("mute");
                SvgPathVolZero ??= AssetIconService.Resolve("volume-high");
                SvgPathVolLow ??= AssetIconService.Resolve("volume-low");
                SvgPathVolHigh ??= AssetIconService.Resolve("volume");
            }
            catch { }

            _fade = new System.Windows.Forms.Timer { Interval = 15 };
            _fade.Tick += (_, __) =>
            {
                if (!Visible) return;
                // Blocking input (e.g. a context menu) must not freeze a half-faded HUD.
                bool blocked = InteractionBlocked?.Invoke() == true;
                UpdateVolHotState();
                var now = DateTime.UtcNow;

                // Remote scrub: se non riceviamo più update per un po', rilascia la ghost knob e (best-effort) chiudi l'anteprima
                if (_remoteDrag && now >= _remoteDragUntil)
                {
                    _remoteDrag = false;
                    if (!_drag && _preview != null)
                    {
                        try { SetPreview(null, _previewSec); } catch { }
                        try { PreviewDismissed?.Invoke(); } catch { }
                    }
                    Invalidate();
                }

                if (IsPointerInteractionActive || HasVisibilityLease)
                    AdvanceFadeIn();
                if (!_manualFadeOut && (blocked || now < _forceShowUntil || !AutoHide || IsPointerInteractionActive ||
                    (now - _lastMove).TotalMilliseconds < Math.Max(IdleHideDelayMs, HideGraceMs)))
                {
                    AdvanceFadeIn();
                }
                else
                {
                    RequestFade(0f);
                }
                AdvanceFade();
                if (Visible && TimelineVisible && IsPlaying && _opacity > 0 && now >= _nextTimelinePaintUtc)
                {
                    _nextTimelinePaintUtc = now.AddMilliseconds(50);
                    Invalidate(_rcTimelineHit);
                }
            };

            _scanHold = new System.Windows.Forms.Timer { Interval = 30 };
            _scanHold.Tick += (_, __) => TickScanHold();
            Disposed += (_, __) =>
            {
                try { _fade.Stop(); _fade.Dispose(); } catch { }
                try { _fadePump?.Dispose(); } catch { }
                try { _scanHold.Stop(); _scanHold.Dispose(); } catch { }
                _preview?.Dispose();
                foreach (var bitmap in _svgCache.Values) bitmap.Dispose();
                foreach (var bitmap in _svgCache2.Values) bitmap.Dispose();
                _svgCache.Clear();
                _svgCache2.Clear();
                _fInfo.Dispose(); _fTime.Dispose(); _fTopTitle.Dispose(); _fPrompt.Dispose(); _fSymbol.Dispose();
            };

            try
            {
                _lastPhysicalMouseScreenPos = Control.MousePosition;
                _hasPhysicalMouseScreenPos = true;
            }
            catch { }

            MouseMove += (_, e) =>
            {
                if (InteractionBlocked?.Invoke() == true) return;
                RecalcLayout();
                var now = DateTime.UtcNow;

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
                    _pointerSeenSinceShown = true;
                    _lastMove = now;
                    AdvanceFadeIn();
                }

                if (_dragVol)
                {
                    float v = VolumeFromX(e.X);
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
                        double previous = _dragPosSec;
                        _dragPosSec = ratio * dur;
                        InvalidateTimelinePreview(previous, _dragPosSec, dur);

                        if ((now - _lastPreviewAt).TotalMilliseconds >= TimelinePreviewIntervalMs)
                        {
                            _lastPreviewAt = now;
                            PreviewRequested?.Invoke(_dragPosSec,
                                PointToScreen(new Point(e.X, _rcTimeline.Y)));
                        }
                    }
                }
                else if (TimelineVisible && GetTime != null && _rcTimelineHit.Contains(e.Location))
                {
                    var (_, dur) = GetTime();
                    if (dur > 0)
                    {
                        double ratio = (e.X - _rcTimeline.X) / (double)_rcTimeline.Width;
                        ratio = Math.Clamp(ratio, 0, 1);
                        double previous = _timelineHoverPreviewSec;
                        _timelineHoverPreviewSec = ratio * dur;
                        _timelinePreviewHover = true;
                        InvalidateTimelinePreview(previous, _timelineHoverPreviewSec, dur);
                        if ((now - _lastPreviewAt).TotalMilliseconds >= TimelinePreviewIntervalMs)
                        {
                            _lastPreviewAt = now;
                            PreviewRequested?.Invoke(_timelineHoverPreviewSec,
                                PointToScreen(new Point(e.X, _rcTimeline.Y)));
                        }
                    }
                }
                else if (!PreviewInteractionBounds().Contains(e.Location))
                {
                    DismissPreviewFromHud(_previewSec);
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

                    // Parent-host visibility changes must not restart the fade.
                    // Only SetVisibleCore's requested false -> true transition does.
                    Invalidate();
                }
            };
        }

        /// <summary>
        /// Fa ripartire la dissolvenza d'ingresso da adesso. Serve quando l'HUD era nascosto: mostrare la sua
        /// finestra e allinearla al video richiede qualche centinaio di millisecondi, e la dissolvenza partita
        /// prima era gia' finita al primo ridisegno (l'HUD compariva di colpo).
        /// </summary>
        public void RestartFadeIn()
        {
            _manualFadeOut = false;
            _opacity = 0; _paintedOpacity = 0;
            _fadeFrom = 0; _fadeTarget = 1f;
            _fadeStartedAt = Stopwatch.GetTimestamp();
            _fadeElapsedMs = 0; _fadeLastTick = _fadeStartedAt;
            Invalidate();
            PumpFade();
        }

        public void ShowOnce(int ms = 2000)
        {
            _lastMove = DateTime.UtcNow;
            _forceShowUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(250, ms));
            AdvanceFadeIn();
            // A HUD gia' pieno non c'e' nulla da ridisegnare: in 4K un ridisegno completo a ogni richiamo pesava.
            if (_opacity < 1f) Invalidate();
            PumpFade();
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

        /// <summary>
        /// Repaints only the progress row (bar, knob and clock). The HUD is a colour-keyed
        /// layered window over the video: a full repaint on every progress tick made the
        /// compositor redo the whole overlay four times a second while the renderer presents.
        /// </summary>
        public void InvalidateProgress()
        {
            if (!TimelineVisible || _rcTimeline.IsEmpty)
            {
                Invalidate();
                return;
            }
            int band = Math.Max(_rcTimeline.Height, Math.Max(18, _fTime.Height)) + 24;
            Invalidate(new Rectangle(0, _rcTimeline.Top + _rcTimeline.Height / 2 - band / 2, Width, band));
        }

        public void SetPreview(Bitmap? bmp, double seconds)
        {
            Bitmap? previous = _preview;
            double anchor = _drag ? _dragPosSec : (_remoteDrag ? _remoteDragPosSec : _timelineHoverPreviewSec);
            double duration = 0;
            try { if (GetTime != null) duration = GetTime().dur; } catch { }
            Rectangle oldCard = PreviewCardBounds(previous, anchor, duration);
            _preview?.Dispose();
            _preview = bmp;
            _previewSec = seconds;
            Rectangle newCard = PreviewCardBounds(_preview, anchor, duration);
            Invalidate(_rcTimelineHit);
            if (!oldCard.IsEmpty) Invalidate(Rectangle.Inflate(oldCard, 8, 8));
            if (!newCard.IsEmpty) Invalidate(Rectangle.Inflate(newCard, 8, 8));
            PreviewLayoutChanged?.Invoke();
        }

        private Rectangle PreviewCardBounds(Bitmap? frame, double seconds, double duration)
        {
            if (frame == null || duration <= 0 || _rcTimeline.Width <= 0 || Width <= 24)
                return Rectangle.Empty;

            int imageWidth = Math.Min((int)Math.Round(frame.Width * HudLayoutScale), Width - 24);
            int imageHeight = Math.Max(1, (int)Math.Round(frame.Height * imageWidth / (double)frame.Width));
            int cardWidth = imageWidth;
            int cardHeight = imageHeight + 28;
            int previewX = _rcTimeline.X + _rcTimeline.Width / 2;
            int x = Math.Clamp(previewX - cardWidth / 2, 8, Math.Max(8, Width - cardWidth - 8));
            int y = Math.Max(_rcTopBar.Bottom + 8, _rcTimeline.Y - cardHeight - 16);
            return new Rectangle(x, y, cardWidth, cardHeight);
        }

        private Rectangle PreviewInteractionBounds()
        {
            if (_preview == null || GetTime == null) return Rectangle.Empty;
            var card = PreviewCardBounds(_preview, _previewSec, GetTime().dur);
            if (card.IsEmpty) return card;
            // Retain the strip while crossing the gap to the timeline.
            return Rectangle.FromLTRB(card.Left - 8, card.Top - 8, card.Right + 8, _rcTimelineHit.Bottom);
        }

        internal Rectangle[] PointerRegions()
        {
            RecalcLayout();
            return new[] { ActiveZoneTop, ActiveZoneBottom, PreviewInteractionBounds(), PromptOverlayBounds };
        }

        private void InvalidateTimelinePreview(double previousSeconds, double currentSeconds, double duration)
        {
            Invalidate(_rcTimelineHit);
            Rectangle previous = PreviewCardBounds(_preview, previousSeconds, duration);
            Rectangle current = PreviewCardBounds(_preview, currentSeconds, duration);
            if (!previous.IsEmpty) Invalidate(Rectangle.Inflate(previous, 8, 8));
            if (!current.IsEmpty) Invalidate(Rectangle.Inflate(current, 8, 8));
        }

        private void DrawTimelinePreviewCard(Graphics g, double seconds, double duration)
        {
            Bitmap? frame = _preview;
            Rectangle card = PreviewCardBounds(frame, seconds, duration);
            if (frame == null || card.IsEmpty)
                return;

            int alpha = (int)Math.Round(255 * Math.Clamp(_opacity, 0f, 1f));
            int imageHeight = card.Height - 28;
            var image = new Rectangle(card.X, card.Y, card.Width, imageHeight);
            using var imagePath = DrawHelpers.RoundRect(image, 7);

            using (var smooth = CinecorePlayer2025.Utilities.SmoothClip.Begin(g, imagePath))
            {
                var sg = smooth.Graphics;
                sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // La finestra dell'HUD rende trasparente il nero puro: le zone nere di un fotogramma diventavano
                // buchi attraverso cui si vedeva il film. I neri si alzano di un soffio (3 su 255), invisibile.
                using (var attributes = new ImageAttributes())
                {
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(_opacity, 0f, 1f), Matrix40 = 0.012f, Matrix41 = 0.012f, Matrix42 = 0.012f });
                    sg.DrawImage(frame, image, 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, attributes);
                }
                // Striscia di cinque fotogrammi: quello al centro e' il punto sotto il puntatore. I quattro
                // ai lati (dieci e venti secondi prima e dopo) restano in ombra, cosi' si capisce subito
                // dove si andra' e cosa c'e' attorno.
                if (frame.Width > frame.Height * 4)
                {
                    float tile = image.Width / 5f;
                    using var shade = new SolidBrush(Color.FromArgb((int)(120 * _opacity), 6, 8, 10));
                    sg.FillRectangle(shade, image.Left, image.Top, tile * 2, image.Height);
                    sg.FillRectangle(shade, image.Left + tile * 3, image.Top, image.Width - tile * 3, image.Height);
                }
            }
            if (frame.Width > frame.Height * 4)
            {
                float tile = image.Width / 5f;
                var centre = Rectangle.Round(new RectangleF(image.Left + tile * 2, image.Top, tile, image.Height));
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var ring = new Pen(Color.FromArgb(alpha, Theme.Accent), Math.Max(2f, 2f * HudLayoutScale));
                using var ringPath = DrawHelpers.RoundRect(Rectangle.Inflate(centre, -1, -1), 5);
                g.DrawPath(ring, ringPath);
                g.SmoothingMode = old;
            }

            using var timeBrush = new SolidBrush(Color.FromArgb(alpha, 236, 243, 250));
            string time = FormatTimelineTime(_previewSec);
            SizeF timeSize = g.MeasureString(time, _fTime);
            float timeX = card.Left + (card.Width - timeSize.Width) / 2f;
            using (var shadow = new SolidBrush(Color.FromArgb((int)(160 * _opacity), 0, 0, 0)))
                g.DrawString(time, _fTime, shadow, timeX + 1, image.Bottom + 5);
            g.DrawString(time, _fTime, timeBrush, timeX, image.Bottom + 4);
        }

        private void DismissPreviewFromHud(double seconds)
        {
            bool hadPreview = _timelinePreviewHover || _drag || _preview != null;
            _timelinePreviewHover = false;
            if (!_remoteDrag && _preview != null)
                SetPreview(null, seconds);
            if (hadPreview && !_remoteDrag)
            {
                try { PreviewDismissed?.Invoke(); } catch { }
            }
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
            if (!VolumeEditable) return;
            if (ExternalVolumeStepRequested?.Invoke(Math.Sign(delta)) == true) return;
            _vol = Math.Clamp(_vol + delta, 0f, 1f);
            _externalVolume = _vol;
            apply(_vol);
            ShowVolumeOsd(1200);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!Capture && !PreviewInteractionBounds().Contains(PointToClient(Control.MousePosition))) StopDragging();
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
            bool hadPreview = _drag || _timelinePreviewHover || _preview != null;
            double previewSeconds = _drag ? _dragPosSec : _previewSec;
            if (_drag || _dragVol)
            {
                _drag = false;
                _dragVol = false;
                Capture = false;
                Invalidate();
            }
            _timelinePreviewHover = false;

            if (!_remoteDrag && _preview != null)
            {
                SetPreview(null, previewSeconds);
            }

            if (hadPreview && !_remoteDrag)
            {
                try { PreviewDismissed?.Invoke(); } catch { }
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (_fade == null) return;
            if (Visible) _fade.Start();
            else _fade.Stop();
        }

        public void ReleaseStalePointerCapture()
        {
            try
            {
                if ((Control.MouseButtons & (MouseButtons.Left | MouseButtons.Right | MouseButtons.Middle | MouseButtons.XButton1 | MouseButtons.XButton2)) != MouseButtons.None)
                    return;

                if (!_drag && !_dragVol && !Capture) return;
                CancelScanHold();
                _drag = false;
                _dragVol = false;
                _timelinePreviewHover = false;
                if (Capture)
                    Capture = false;
                Invalidate();
            }
            catch { }
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
            if (e.Button == MouseButtons.Right)
            {
                return;
            }

            if (e.Button != MouseButtons.Left) return;
            CancelScanHold();

            RecalcLayout();

            if (_opacity <= 0.05f && IsHudPassiveWakeZone(e.Location))
            {
                ShowOnce(1800);
                return;
            }

            if (!IsHudInteractive(e.Location))
            {
                ForwardMouseToUnderlying(WinMsg.WM_LBUTTONDOWN, e.Location, nint.Zero);
                return;
            }

            ShowOnce(2000);

            if (_showSkipIntroPrompt && _rcSkipIntroPrompt.Contains(e.Location))
            {
                InvokeHudButton(ButtonId.PlayPause, SkipIntroPromptClicked, 450);
                return;
            }

            if (_showNextEpisodePrompt && _rcNextEpisodePrompt.Contains(e.Location))
            {
                InvokeHudButton(ButtonId.NextChapter, NextEpisodePromptClicked, 450);
                return;
            }

            if (_rcTopSettings.Contains(e.Location)) { InvokeHudButton(ButtonId.TopSettings, TopSettingsClicked, 220); return; }
            if (_rcTopInfo.Contains(e.Location)) { InvokeHudButton(ButtonId.TopInfo, TopInfoClicked, 180); return; }

            if (_rcBtnRemove.Contains(e.Location)) { InvokeHudButton(ButtonId.Remove, StopClicked, 220); return; }
            if (_rcBtnOpen.Contains(e.Location)) { InvokeHudButton(ButtonId.Open, OpenClicked, 450); return; }
            if (_rcBtnPlay.Contains(e.Location)) { InvokeHudButton(ButtonId.PlayPause, PlayPauseClicked); return; }
            if (_showBackFwd && _rcBtnBack.Contains(e.Location)) { StartScanHold(ButtonId.Back10); return; }
            if (_showBackFwd && _rcBtnFwd.Contains(e.Location)) { StartScanHold(ButtonId.Fwd10); return; }
            if (_showPrevNext && _rcBtnPrev.Contains(e.Location)) { InvokeHudButton(ButtonId.PrevChapter, PrevChapterClicked); return; }
            if (_showPrevNext && _rcBtnNext.Contains(e.Location)) { InvokeHudButton(ButtonId.NextChapter, NextChapterClicked); return; }
            if (_rcBtnFull.Contains(e.Location)) { InvokeHudButton(ButtonId.Fullscreen, FullscreenClicked, 450); return; }

            if (_rcVolIconHit.Contains(e.Location))
            {
                SetMutedInternal(!IsMuted);
                return;
            }

            if (VolumeEditable && _rcVolPanelHit.Contains(e.Location))
            {
                _volHotUntil = DateTime.UtcNow.AddMilliseconds(VolHoverLingerMs);

                _dragVol = true;
                Capture = true;

                float v = VolumeFromX(e.X);
                SetVolumeFromUser(v);
                return;
            }

            if (_preview != null && GetTime != null)
            {
                var card = PreviewCardBounds(_preview, _previewSec, GetTime().dur);
                if (card.Contains(e.Location))
                {
                    int tile = Math.Clamp((e.X - card.Left) * 5 / Math.Max(1, card.Width), 0, 4);
                    double seconds = Math.Clamp(_previewSec + (tile - 2) * 10, 0, Math.Max(0, GetTime().dur - .05));
                    SeekRequested?.Invoke(seconds);
                    DismissPreviewFromHud(seconds);
                    return;
                }
            }

            if (TimelineVisible && _rcTimelineHit.Contains(e.Location) && GetTime != null)
            {
                _drag = true;
                Capture = true;
                _lastPreviewAt = DateTime.MinValue;
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
                        if (btn == ButtonId.Back10) InvokeHudButton(ButtonId.Back10, SkipBack10Clicked);
                        else InvokeHudButton(ButtonId.Fwd10, SkipForward10Clicked);
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
                _rcVolPanelHit.Contains(e.Location);

            if (!overVol)
            {
                int wParam = (short)e.Delta << 16;
                ForwardMouseToUnderlying(WinMsg.WM_MOUSEWHEEL, e.Location, wParam);
                return;
            }

            _volUiHot = true;
            _volHotUntil = DateTime.UtcNow.AddMilliseconds(VolHoverLingerMs);

            float step = e.Delta > 0 ? 0.05f : -0.05f;
            SetVolumeFromUser(_vol + step);
        }

        private Rectangle ActiveZoneTop => Rectangle.Intersect(new Rectangle(0, 0, Width, Height), Rectangle.Inflate(_rcTopBar, 0, 10));
        private Rectangle ActiveZoneBottom => GetBottomInteractiveBounds();

        private bool IsHudPassiveWakeZone(Point p)
        {
            RecalcLayout();
            if (ActiveZoneTop.Contains(p)) return true;
            if (ActiveZoneBottom.Contains(p)) return true;
            if (PreviewInteractionBounds().Contains(p)) return true;
            if (IsIntroOutroPromptHit(p)) return true;
            if (_rcTopSettings.Contains(p)) return true;
            if (_rcTopInfo.Contains(p)) return true;
            if (TimelineVisible && (_rcTimelineHit.Contains(p) || _rcTimeline.Contains(p))) return true;
            return false;
        }

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

            UnionRect(_rcBottomPanel);
            UnionRect(_rcTimelineHit);
            UnionRect(_rcBtnRemove);
            UnionRect(_rcBtnOpen);
            UnionRect(_rcBtnPlay);
            UnionRect(_rcBtnFull);
            UnionRect(_rcVolIconHit);
            if (_showSkipIntroPrompt)
                UnionRect(_rcSkipIntroPrompt);
            if (_showNextEpisodePrompt)
                UnionRect(_rcNextEpisodePrompt);

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
            if (PreviewInteractionBounds().Contains(p)) return true;
            if (IsIntroOutroPromptHit(p)) return true;
            if (TimelineVisible && (_rcTimelineHit.Contains(p) || _rcTimeline.Contains(p))) return true;
            if (_rcVolPanelHit.Contains(p) || _rcVolIconHit.Contains(p)) return true;
            if (_rcTopSettings.Contains(p)) return true;
            if (_rcTopInfo.Contains(p)) return true;
            return false;
        }

        private bool IsIntroOutroPromptHit(Point p)
        {
            return (_showSkipIntroPrompt && _rcSkipIntroPrompt.Contains(p)) ||
                   (_showNextEpisodePrompt && _rcNextEpisodePrompt.Contains(p));
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

                RecalcLayout();

                int x = (short)((uint)m.LParam & 0xFFFF);
                int y = (short)(((uint)m.LParam >> 16) & 0xFFFF);
                Point client = PointToClient(new Point(x, y));

                if (_opacity <= 0.05f)
                {
                    m.Result = IsHudPassiveWakeZone(client) ? (nint)1 : -1;
                    return;
                }

                var now = DateTime.UtcNow;

                bool hitVol =
                    _rcVolIconHit.Contains(client) ||
                    _rcVolPanelHit.Contains(client);

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

        private float VolumeFromX(int x)
        {
            if (_rcVolTrack.Width <= 1) return _vol;

            int xx = Math.Clamp(x, _rcVolTrack.Left, _rcVolTrack.Right);
            float t = (xx - _rcVolTrack.Left) / (float)Math.Max(1, _rcVolTrack.Width);
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

        private float HudLayoutScale => Math.Max(.5f, Math.Min(DeviceDpi / 96f,
            Math.Min(Width / 900f, Height / 480f)));

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            RebuildFontsForDpi();
            RecalcLayout();
            Invalidate();
        }

        private void RecalcLayout()
        {
            RebuildFontsForDpi();
            float scale = HudLayoutScale;
            int w = (int)Math.Round(Width / scale), h = (int)Math.Round(Height / scale);
            if (w <= 0 || h <= 0) return;

            int topPanelH = Math.Min(50, Math.Max(42, TopBarHeight - 12));
            _rcTopBar = new Rectangle(12, 8, Math.Max(1, w - 24), topPanelH);
            _rcBottomBar = new Rectangle(0, Math.Max(0, h - BottomBackdropHeight), w, BottomBackdropHeight);
            int panelH = Math.Min(86, Math.Max(70, h / 9));
            _rcBottomPanel = new Rectangle(12, Math.Max(_rcTopBar.Bottom + 12, h - panelH - 14), Math.Max(1, w - 24), panelH);

            int timelineY = Math.Max(_rcTopBar.Bottom + 18, _rcBottomPanel.Top - 24);
            int timeReserve = Math.Min(82, Math.Max(58, w / 16));
            _rcTimeline = new Rectangle(timeReserve, timelineY, Math.Max(40, w - timeReserve * 2), TimelineHeight);
            _rcTimelineHit = Rectangle.Inflate(_rcTimeline, 16, 32);

            int btnTop = _rcBottomPanel.Top + (_rcBottomPanel.Height - BtnSize) / 2;
            int playTop = _rcBottomPanel.Top + (_rcBottomPanel.Height - PlayBtnSize) / 2;
            int sidePad = 26;
            _rcBtnFull = new Rectangle(_rcBottomPanel.Right - sidePad - BtnSize, btnTop, BtnSize, BtnSize);

            // --- VOLUME: pannello unico (icona + track + label) ---
            _rcVolIcon = new Rectangle(_rcBtnFull.X - 58 - VolBtnSize, btnTop, VolBtnSize, VolBtnSize);

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

            int inlineTrackW = Math.Min(164, Math.Max(104, w / 10));
            int inlineTrackX = _rcBtnFull.Left - 26 - inlineTrackW;
            int inlineTrackY = btnTop + BtnSize / 2 - VolTrackThickness / 2;
            _rcVolTrack = new Rectangle(inlineTrackX, inlineTrackY, inlineTrackW, VolTrackThickness);
            _rcVolIcon = new Rectangle(_rcVolTrack.Left - 12 - VolBtnSize, btnTop, VolBtnSize, VolBtnSize);
            _rcVolIconHit = Rectangle.Inflate(_rcVolIcon, 4, 4);
            _rcVolPanel = Rectangle.Union(_rcVolIcon, Rectangle.Inflate(_rcVolTrack, 0, 12));
            _rcVolPanelHit = Rectangle.Inflate(_rcVolPanel, 8, 10);
            _rcVolHoverOpen = _rcVolPanelHit;
            int inlineKnobX = _rcVolTrack.Left + (int)Math.Round(shownVol * Math.Max(1, _rcVolTrack.Width));
            inlineKnobX = Math.Clamp(inlineKnobX, _rcVolTrack.Left, _rcVolTrack.Right);
            int inlineKnobY = _rcVolTrack.Top + _rcVolTrack.Height / 2;
            _rcVolKnob = new Rectangle(inlineKnobX - VolKnobRadius, inlineKnobY - VolKnobRadius, VolKnobRadius * 2, VolKnobRadius * 2);

            _rcBtnRemove = new Rectangle(_rcBottomPanel.Left + sidePad, btnTop, BtnSize, BtnSize);
            _rcBtnOpen = new Rectangle(_rcBtnRemove.Right + 22, btnTop, BtnSize, BtnSize);

            int leftBound = _rcBtnOpen.Right + 36;
            int rightBound = _rcVolIcon.X - 28 - ExtraBtnVsVolPad;
            int usable = Math.Max(0, rightBound - leftBound);
            int gap = Math.Clamp(GapDesired, 44, Math.Max(44, usable / 5));

            int centerX = w / 2;
            centerX = Math.Max(leftBound + PlayBtnSize / 2, Math.Min(centerX, rightBound - PlayBtnSize / 2));
            _rcBtnPlay = new Rectangle(centerX - PlayBtnSize / 2, playTop, PlayBtnSize, PlayBtnSize);
            _rcBtnBack = new Rectangle(centerX - gap - BtnSize / 2, btnTop, BtnSize, BtnSize);
            _rcBtnFwd = new Rectangle(centerX + gap - BtnSize / 2, btnTop, BtnSize, BtnSize);
            _rcBtnPrev = new Rectangle(centerX - gap * 2 - BtnSize / 2, btnTop, BtnSize, BtnSize);
            _rcBtnNext = new Rectangle(centerX + gap * 2 - BtnSize / 2, btnTop, BtnSize, BtnSize);

            _showBackFwd = _rcBtnBack.X >= leftBound && _rcBtnFwd.Right <= rightBound;
            _showPrevNext = _rcBtnPrev.X >= leftBound && _rcBtnNext.Right <= rightBound;

            int topBtnTop = _rcTopBar.Top + (_rcTopBar.Height - BtnSize) / 2;
            _rcTopInfo = new Rectangle(Math.Max(_rcTopBar.Left + 16, _rcTopBar.Right - 16 - BtnSize), topBtnTop, BtnSize, BtnSize);
            _rcTopSettings = new Rectangle(Math.Max(_rcTopBar.Left + 16, _rcTopInfo.Left - 8 - BtnSize), topBtnTop, BtnSize, BtnSize);
            RecalcIntroOutroPromptLayout(w, h);
            if (Math.Abs(scale - 1f) > .001f)
            {
                Rectangle S(Rectangle r) => r.IsEmpty ? r : Rectangle.FromLTRB(
                    (int)Math.Round(r.Left * scale), (int)Math.Round(r.Top * scale),
                    (int)Math.Round(r.Right * scale), (int)Math.Round(r.Bottom * scale));
                _rcTopBar = S(_rcTopBar); _rcBottomBar = S(_rcBottomBar); _rcBottomPanel = S(_rcBottomPanel); _rcPromptBar = S(_rcPromptBar);
                _rcTimeline = S(_rcTimeline); _rcTimelineHit = S(_rcTimelineHit);
                _rcBtnRemove = S(_rcBtnRemove); _rcBtnOpen = S(_rcBtnOpen); _rcBtnPlay = S(_rcBtnPlay);
                _rcBtnBack = S(_rcBtnBack); _rcBtnFwd = S(_rcBtnFwd); _rcBtnPrev = S(_rcBtnPrev); _rcBtnNext = S(_rcBtnNext); _rcBtnFull = S(_rcBtnFull);
                _rcSkipIntroPrompt = S(_rcSkipIntroPrompt); _rcNextEpisodePrompt = S(_rcNextEpisodePrompt);
                _rcVolIcon = S(_rcVolIcon); _rcVolPanel = S(_rcVolPanel); _rcVolHoverOpen = S(_rcVolHoverOpen); _rcVolKnob = S(_rcVolKnob);
                _rcVolIconHit = S(_rcVolIconHit); _rcVolPanelHit = S(_rcVolPanelHit); _rcVolTrack = S(_rcVolTrack);
                _rcTopInfo = S(_rcTopInfo); _rcTopSettings = S(_rcTopSettings);
            }
        }

        private void RecalcIntroOutroPromptLayout(int width, int height)
        {
            _rcSkipIntroPrompt = Rectangle.Empty;
            _rcNextEpisodePrompt = Rectangle.Empty;
            _rcPromptBar = Rectangle.Empty;

            int count = (_showSkipIntroPrompt ? 1 : 0) + (_showNextEpisodePrompt ? 1 : 0);
            if (count <= 0 || width <= 0 || height <= 0)
                return;

            int gap = count > 1 ? 12 : 0;
            int available = Math.Max(104, width - 32 - gap);
            int buttonW = Math.Min(214, Math.Max(150, available / count));
            int buttonH = 44;
            int totalW = (buttonW * count) + gap;
            int x = Math.Max(16, width - totalW - 30);

            int barTop = _rcBottomBar.Top - PromptBarGap - PromptBarHeight;
            barTop = Math.Max(_rcTopBar.Bottom + 8, barTop);
            _rcPromptBar = new Rectangle(0, barTop, width, PromptBarHeight);

            int y = _rcPromptBar.Top + (_rcPromptBar.Height - buttonH) / 2;

            if (_showSkipIntroPrompt)
            {
                _rcSkipIntroPrompt = new Rectangle(x, y, buttonW, buttonH);
                x += buttonW + gap;
            }

            if (_showNextEpisodePrompt)
                _rcNextEpisodePrompt = new Rectangle(x, y, buttonW, buttonH);
        }

        /// <summary>
        /// Vero quando la finestra che ospita l'HUD puo' sfumare da sola (alfa di sistema). In quel caso
        /// l'HUD si disegna una volta a piena opacita' e la dissolvenza la fa il compositore: nessun
        /// ridisegno a ogni passo (in 4K era la parte lenta) e la vignettatura segue lo stesso valore.
        /// </summary>
        public Func<bool>? HostFadeAvailable { get; set; }
        /// <summary>Opacita' da dare alla finestra ospite; 1 quando l'HUD ha gia' l'opacita' nel disegno.</summary>
        public float HostAlpha => _paintedHostFade ? RenderOpacity : 1f;
        public event Action? HostAlphaChanged;
        private bool _paintedHostFade;
        private bool HostFadeNow => HostFadeAvailable?.Invoke() == true && !PromptActive;

        protected override void OnPaint(PaintEventArgs e)
        {
            bool hostFade = HostFadeNow;
            if (hostFade != _paintedHostFade && e.ClipRectangle != ClientRectangle && _opacity > 0.01f)
            {
                // Il modo cambia solo con un ridisegno completo, altrimenti resterebbero parti disegnate nell'altro.
                hostFade = _paintedHostFade;
                Invalidate();
            }
            float real = _opacity;
            bool appearing = hostFade && Visible && (_fadeTarget > 0 || real > 0.001f);
            if (appearing) _opacity = 1f;
            try { PaintHud(e); }
            finally { _opacity = real; }
            if (real > 0.01f || appearing)
            {
                _paintedHostFade = hostFade;
                _paintedOpacity = hostFade ? 1f : real;
            }
            else if (!hostFade) _paintedHostFade = false;
            HostAlphaChanged?.Invoke();
        }

        private void PaintHud(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_opacity <= 0.01f)
            {
                // Comandi svaniti: del pannello resta solo il pulsante di salto, per tutta la sigla.
                if (PromptActive)
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    RecalcLayout();
                    DrawIntroOutroPrompts(e.Graphics);
                }
                return;
            }

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RecalcLayout();
            _paintedOpacity = _opacity;

            DrawHudBackplates(g);

            DrawTopBar(g);

            // --- TIMELINE invariata ---
            if (TimelineVisible && GetTime != null)
            {
                var (pos, dur) = GetTime();
                bool dragging = _drag || _remoteDrag;
                double dragSec = _drag ? _dragPosSec : _remoteDragPosSec;
                double paintedPosition = dragging ? dragSec : pos;

                int tlCy = _rcTimeline.Top + _rcTimeline.Height / 2;
                using var tlBg = new Pen(Color.FromArgb((int)(80 * _opacity), 126, 144, 166), Math.Max(4, _rcTimeline.Height))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                using var tlFg = new Pen(Color.FromArgb((int)(255 * _opacity), Theme.Accent), Math.Max(4, _rcTimeline.Height))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLine(tlBg, _rcTimeline.Left, tlCy, _rcTimeline.Right, tlCy);

                if (dur > 0)
                {
                    int wProg = (int)(_rcTimeline.Width * (Math.Clamp(paintedPosition, 0, dur) / Math.Max(0.0001, dur)));
                    if (wProg > 0)
                        g.DrawLine(tlFg, _rcTimeline.Left, tlCy, _rcTimeline.Left + Math.Min(wProg, _rcTimeline.Width), tlCy);
                }

                bool previewActive = (_drag || _remoteDrag || _timelinePreviewHover) && _preview != null;
                double previewSec = _drag ? _dragPosSec : (_remoteDrag ? _remoteDragPosSec : _timelineHoverPreviewSec);

                {
                    double knobSec = dragging ? dragSec : pos;
                    knobSec = dur > 0 ? Math.Clamp(knobSec, 0, dur) : 0;
                    int knobX = _rcTimeline.X + (dur > 0 ? (int)(_rcTimeline.Width * (knobSec / dur)) : 0);
                    int d = dragging ? 14 : 12;
                    using var kn = new SolidBrush(Color.FromArgb((int)(255 * _opacity), Theme.Accent));
                    g.FillEllipse(kn,
                        knobX - d / 2,
                        _rcTimeline.Y + _rcTimeline.Height / 2 - d / 2,
                        d, d);
                    using var knobBorder = new Pen(Color.FromArgb((int)(230 * _opacity), 214, 225, 255), 1.4f);
                    g.DrawEllipse(knobBorder,
                        knobX - d / 2,
                        _rcTimeline.Y + _rcTimeline.Height / 2 - d / 2,
                        d, d);

                    if ((_timelinePreviewHover || _remoteDrag) && !_drag && dur > 0)
                    {
                        // Il punto in cui si andrebbe con un clic: un segno bianco sulla barra, sotto il puntatore.
                        double hoverSec = Math.Clamp(_remoteDrag ? _remoteDragPosSec : _timelineHoverPreviewSec, 0, dur);
                        int hoverX = _rcTimeline.X + (int)(_rcTimeline.Width * (hoverSec / dur));
                        int reach = Math.Max(7, (int)Math.Round(8 * HudLayoutScale));
                        using var mark = new Pen(Color.FromArgb((int)(235 * _opacity), 255, 255, 255), Math.Max(2f, 2f * HudLayoutScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLine(mark, hoverX, tlCy - reach, hoverX, tlCy + reach);
                    }

                    if (previewActive && _preview != null)
                        DrawTimelinePreviewCard(g, previewSec, dur);
                }

                {
                    using var brTime = new SolidBrush(Color.FromArgb((int)(230 * _opacity), 255, 255, 255));
                    string posText = FormatTimelineTime(dragging ? dragSec : pos);
                    string durText = dur > 0 ? FormatTimelineTime(dur) : string.Empty;
                    var posSz = g.MeasureString(posText, _fTime);
                    var durSz = g.MeasureString(durText, _fTime);
                    float ty = _rcTimeline.Y + _rcTimeline.Height / 2f - posSz.Height / 2f;
                    g.DrawString(posText, _fTime, brTime, Math.Max(10, _rcTimeline.Left - posSz.Width - 18), ty);
                    if (!string.IsNullOrWhiteSpace(durText))
                        g.DrawString(durText, _fTime, brTime, Math.Min(Width - durSz.Width - 10, _rcTimeline.Right + 18), ty);
                }
            }

            // --- BOTTONI: ora SVG (niente testo disegnato) ---
            DrawRoundButtonSvg(g, _rcBtnRemove, SvgPathRemove, IsPulsing(ButtonId.Remove));
            DrawRoundButtonSvg(g, _rcBtnOpen, SvgPathOpen, IsPulsing(ButtonId.Open));
            var playIcon = IsPlaying ? SvgPathPause : SvgPathPlay;
            DrawRoundButtonSvg(g, _rcBtnPlay, playIcon, IsPulsing(ButtonId.PlayPause), primary: true);

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

            DrawVolumePanelUnified(g);

            DrawIntroOutroPrompts(g);

            // DPAD focus (telecomando)
            DrawDpadFocus(g);


        }

        private static string FormatTimelineTime(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            var time = TimeSpan.FromSeconds(seconds);
            return time.TotalHours >= 1
                ? time.ToString(@"hh\:mm\:ss")
                : time.ToString(@"mm\:ss");
        }

        private void DrawHudBackplates(Graphics g)
        {
            if (ExternalVignette) return;
            float opacity = Math.Clamp(_opacity, 0f, 1f);
            var top = new Rectangle(0, 0, Width, Math.Min(140, Height / 3));
            using (var fade = new LinearGradientBrush(top, Color.FromArgb((int)(160 * opacity), 0, 6, 10), Color.Transparent, LinearGradientMode.Vertical))
                g.FillRectangle(fade, top);
            int backdropHeight = (int)Math.Round(220 * HudLayoutScale);
            var bottom = new Rectangle(0, Math.Max(0, Height - backdropHeight), Width, Math.Min(backdropHeight, Height));
            using var shade = new LinearGradientBrush(bottom, Color.Transparent, Color.FromArgb((int)(118 * opacity), 0, 7, 10), LinearGradientMode.Vertical);
            g.FillRectangle(shade, bottom);
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ExternalVignette { get; set; }
        // Opacita' che l'HUD ha davvero a schermo. La vignettatura e' una finestra a parte che la segue:
        // se seguisse il valore in calcolo arriverebbe prima dei comandi, perche' cambiare la trasparenza di
        // una finestra e' immediato mentre ridisegnare l'HUD a 4K richiede alcuni fotogrammi (si vedeva la
        // vignettatura piena e, un attimo dopo, comparire la timeline).
        private float _paintedOpacity;
        [Browsable(false)]
        public float RenderOpacity => Math.Min(_opacity, _paintedOpacity);

        private void DrawIntroOutroPrompts(Graphics g)
        {
            if (!_showSkipIntroPrompt && !_showNextEpisodePrompt)
                return;

            if (_showSkipIntroPrompt)
                DrawIntroOutroPromptButton(g, _rcSkipIntroPrompt, L("Salta intro", "Skip intro"), nextTrack: false);

            if (_showNextEpisodePrompt)
                DrawIntroOutroPromptButton(g, _rcNextEpisodePrompt, L("Prossimo episodio", "Next episode"), nextTrack: true);
        }

        private Font? _promptFont;
        private float _promptFontSize;

        // Pillola scura con testo e simbolo di avanzamento: grande quanto serve per leggerla dal
        // divano, sempre piena (non svanisce con i comandi) e con il bordo acceso sotto il puntatore.
        private void DrawIntroOutroPromptButton(Graphics g, Rectangle rc, string text, bool nextTrack)
        {
            if (rc.Width <= 0 || rc.Height <= 0)
                return;

            bool hover = false;
            try { hover = IsHandleCreated && rc.Contains(PointToClient(Control.MousePosition)); } catch { }
            int radius = rc.Height / 2;
            using (var shadow = new SolidBrush(Color.FromArgb(96, 0, 0, 0)))
            using (var shadowPath = DrawHelpers.RoundRect(new Rectangle(rc.X, rc.Y + Math.Max(2, rc.Height / 14), rc.Width, rc.Height), radius))
                g.FillPath(shadow, shadowPath);

            using (var fillPath = DrawHelpers.RoundRect(rc, radius))
            using (var fill = new SolidBrush(hover ? Color.FromArgb(240, 245, 247, 250) : Color.FromArgb(214, 14, 18, 24)))
            using (var border = new Pen(hover ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255), Math.Max(1f, rc.Height / 30f)))
            {
                g.FillPath(fill, fillPath);
                g.DrawPath(border, fillPath);
            }

            Color ink = hover ? Color.FromArgb(255, 12, 16, 22) : Color.FromArgb(255, 245, 247, 250);
            // Simbolo a destra: due triangoli (avanti veloce), con la barra per "prossimo episodio".
            int glyph = (int)Math.Round(rc.Height * 0.30);
            int glyphRight = rc.Right - (int)Math.Round(rc.Height * 0.52);
            int centre = rc.Top + rc.Height / 2;
            using (var brush = new SolidBrush(ink))
            {
                int bar = nextTrack ? Math.Max(2, glyph / 4) : 0;
                int x = glyphRight - bar - glyph * 2;
                for (int k = 0; k < 2; k++)
                    g.FillPolygon(brush, new[] { new PointF(x + k * glyph, centre - glyph * 0.62f), new PointF(x + (k + 1) * glyph, centre), new PointF(x + k * glyph, centre + glyph * 0.62f) });
                if (nextTrack) g.FillRectangle(brush, glyphRight - bar, centre - glyph * 0.62f, bar, glyph * 1.24f);
            }

            float fontSize = Math.Max(11f, rc.Height * 0.37f);
            if (_promptFont == null || Math.Abs(_promptFontSize - fontSize) > 0.01f)
            {
                _promptFont?.Dispose();
                _promptFont = new Font("Segoe UI Semibold", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                _promptFontSize = fontSize;
            }
            Font font = _promptFont;
            int textLeft = rc.Left + (int)Math.Round(rc.Height * 0.52);
            TextRenderer.DrawText(
                g,
                text,
                font,
                new Rectangle(textLeft, rc.Top, Math.Max(10, glyphRight - glyph * 2 - (int)Math.Round(rc.Height * 0.25) - textLeft), rc.Height),
                ink,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPadding);
        }
        private void DrawVolumePanelUnified(Graphics g)
        {
            if (!VolumeEditable)
            {
                _dragVol = false;
                _volUiHot = false;
                DrawSvgOnly(g, _rcVolIcon, SvgPathVolHigh, Color.FromArgb(72, 84, 96));
                using var disabled = new Pen(Color.FromArgb(65, 79, 94), Math.Max(3, VolTrackThickness)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                int center = _rcVolTrack.Top + _rcVolTrack.Height / 2;
                g.DrawLine(disabled, _rcVolTrack.Left, center, _rcVolTrack.Right, center);
                return;
            }
            float opacity = Math.Clamp(_opacity, 0f, 1f);
            float shownVol = IsMuted ? 0f : Math.Clamp(_vol, 0f, 1f);

            bool hot = false;
            try { hot = _rcVolPanelHit.Contains(PointToClient(Cursor.Position)); } catch { }
            if (hot || _dragVol)
            {
                using var iconHot = new SolidBrush(Color.FromArgb((int)((_dragVol ? 52 : 32) * opacity), 255, 255, 255));
                g.FillEllipse(iconHot, _rcVolIcon);
            }

            DrawSvgOnly(g, _rcVolIcon, GetVolumeIconPath(), Color.FromArgb(222, 232, 242));

            int cy = _rcVolTrack.Top + _rcVolTrack.Height / 2;
            using (var trkBg = new Pen(Color.FromArgb((int)(86 * opacity), 116, 136, 158), Math.Max(4, VolTrackThickness)))
            {
                trkBg.StartCap = LineCap.Round;
                trkBg.EndCap = LineCap.Round;
                g.DrawLine(trkBg, _rcVolTrack.Left, cy, _rcVolTrack.Right, cy);
            }

            int fillRight = _rcVolTrack.Left + (int)Math.Round(shownVol * Math.Max(1, _rcVolTrack.Width));
            fillRight = Math.Clamp(fillRight, _rcVolTrack.Left, _rcVolTrack.Right);
            if (fillRight > _rcVolTrack.Left)
            {
                using var trkFill = new Pen(Color.FromArgb((int)(240 * opacity), Theme.Accent), Math.Max(4, VolTrackThickness))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLine(trkFill, _rcVolTrack.Left, cy, fillRight, cy);
            }

            int r = _dragVol || hot ? VolKnobRadius + 1 : VolKnobRadius;
            using (var glow = new SolidBrush(Color.FromArgb((int)(52 * opacity), Theme.Accent)))
                g.FillEllipse(glow, fillRight - r - 4, cy - r - 4, (r + 4) * 2, (r + 4) * 2);
            using (var knob = new SolidBrush(Color.FromArgb((int)(255 * opacity), Theme.Accent)))
                g.FillEllipse(knob, fillRight - r, cy - r, r * 2, r * 2);

            using var knobOutline = new Pen(Color.FromArgb((int)(210 * opacity), 166, 212, 255), 1.2f);
            g.DrawEllipse(knobOutline, fillRight - r, cy - r, r * 2, r * 2);

            // Receiver volume: the slider only shows a position, the real level is this value.
            string? external = null;
            try { external = ExternalVolumeText?.Invoke(); } catch { }
            if (!string.IsNullOrWhiteSpace(external))
            {
                SizeF size = g.MeasureString(external, _fTime);
                float x = _rcVolIcon.Left - 4 - size.Width, y = cy - size.Height / 2f;
                using var shadow = new SolidBrush(Color.FromArgb((int)(160 * opacity), 0, 0, 0));
                using var ink = new SolidBrush(Color.FromArgb((int)((IsMuted ? 130 : 236) * opacity), 236, 243, 250));
                g.DrawString(external, _fTime, shadow, x + 1, y + 1);
                g.DrawString(external, _fTime, ink, x, y);
            }
        }

        private void DrawSvgOnly(Graphics g, Rectangle r, string? svgPath, Color tint)
        {
            if (string.IsNullOrWhiteSpace(svgPath) || !File.Exists(svgPath))
                return;

            int s = Math.Max(16, Math.Min(r.Width, r.Height) - 10);
            int x = r.X + (r.Width - s) / 2;
            int y = r.Y + (r.Height - s) / 2;
            var dest = new Rectangle(x, y, s, s);

            var bmp = GetSvgBitmap(svgPath, s, tint);

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
            string title = !string.IsNullOrWhiteSpace(NowPlayingTitle)
                ? NowPlayingTitle
                : GetTitle?.Invoke() ?? string.Empty;

            int textLeft = _rcTopBar.Left + 18;
            int textRight = _rcTopSettings.X - 12;
            string info = GetInfoLine?.Invoke() ?? string.Empty;
            bool hasInfo = !string.IsNullOrWhiteSpace(info);
            if (hasInfo)
            {
                var flags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                Size infoSize = TextRenderer.MeasureText(g, info, _fInfo, new Size(Math.Max(120, _rcTopBar.Width / 3), 0), flags);
                int infoW = Math.Min(Math.Max(92, infoSize.Width + 4), Math.Max(110, _rcTopBar.Width / 4));
                Rectangle rcInfo = new Rectangle(
                    Math.Max(textLeft, _rcTopSettings.Left - 12 - infoW),
                    _rcTopBar.Top + (_rcTopBar.Height - 24) / 2,
                    infoW,
                    24);
                textRight = Math.Min(textRight, rcInfo.Left - 12);

                TextRenderer.DrawText(
                    g,
                    info,
                    _fInfo,
                    rcInfo,
                    Color.FromArgb((int)(212 * _opacity), 184, 203, 222),
                    flags);
            }

            if (!string.IsNullOrWhiteSpace(title) && textRight - textLeft > 10)
            {
                int h = Math.Max(_fTopTitle.Height + 2, 20);
                var rcTitle = new Rectangle(
                    textLeft,
                    _rcTopBar.Top + (_rcTopBar.Height - h) / 2,
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
            DrawTopRoundButtonSvg(g, _rcTopSettings, SvgPathTopSettings, IsPulsing(ButtonId.TopSettings));
            DrawTopRoundButtonSvg(g, _rcTopInfo, SvgPathTopInfo, IsPulsing(ButtonId.TopInfo));
        }
        private void DrawTopRoundButtonSvg(Graphics g, Rectangle r, string? svgPath, bool pulse)
        {
            float opacity = Math.Clamp(_opacity, 0f, 1f);
            bool hot = false;
            try { hot = r.Contains(PointToClient(Cursor.Position)); } catch { }
            if (hot || pulse)
            {
                using var underline = new Pen(Color.FromArgb((int)((pulse ? 220 : 150) * opacity), Theme.Accent), 2f);
                g.DrawLine(underline, r.Left + 8, r.Bottom - 3, r.Right - 8, r.Bottom - 3);
            }

            if (string.IsNullOrWhiteSpace(svgPath) || !File.Exists(svgPath))
                return; // niente fallback disegnato

            int s = Math.Max(18, Math.Min(r.Width, r.Height) - 10);
            int x = r.X + (r.Width - s) / 2;
            int y = r.Y + (r.Height - s) / 2;
            var dest = new Rectangle(x, y, s, s);

            var bmp = GetSvgBitmap(svgPath, s, Color.FromArgb(232, 240, 248));

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
        private void DrawRoundButtonSvg(Graphics gg, Rectangle r, string? svgPath, bool pulse = false, bool primary = false)
        {
            float opacity = Math.Clamp(_opacity, 0f, 1f);
            bool hot = false;
            try { hot = r.Contains(PointToClient(Cursor.Position)); } catch { }

            if (hot || pulse)
            {
                using var underline = new Pen(Color.FromArgb((int)((pulse ? 220 : 150) * opacity), Theme.Accent), 2f);
                gg.DrawLine(underline, r.Left + 7, r.Bottom - 2, r.Right - 7, r.Bottom - 2);
            }

            if (string.IsNullOrWhiteSpace(svgPath) || !File.Exists(svgPath))
                return; // niente fallback disegnato

            int s = Math.Max(16, Math.Min(r.Width, r.Height) - 10);
            int x = r.X + (r.Width - s) / 2;
            int y = r.Y + (r.Height - s) / 2;
            var dest = new Rectangle(x, y, s, s);

            var bmp = GetSvgBitmap(svgPath, s, primary ? Color.FromArgb(224, 232, 255) : Color.FromArgb(222, 232, 242));

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
                return cached;

            Bitmap rendered = RenderSvgSkia(svgPath, sizePx, tint);

            try
            {
                if (_svgCache2.TryGetValue((svgPath, sizePx, argb), out var old) && old != null)
                    old.Dispose();
                _svgCache2[(svgPath, sizePx, argb)] = rendered;
            }
            catch { }

            return rendered;
        }

        private static Bitmap RenderSvgSkia(string svgPath, int targetPx, Color tint)
            => AssetIconService.RenderSvg(svgPath, targetPx, tint);
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
        [PreserveSig] int TranslateAccelerator(ref MSG pMsg);
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
}
