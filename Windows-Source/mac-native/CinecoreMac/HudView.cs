using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// HUD di riproduzione: traduzione di HUD/HudOverlay.cs (app Windows). Stesse misure (in punti, come i pixel a 96 dpi
/// su Windows), stessi colori e comportamento: binari sottili e bianchi a riposo, spessi e d'accento sotto il
/// puntatore; al passaggio del mouse le icone prendono il colore d'accento, senza cerchi; dissolvenza 280 ms in
/// entrata e 240 ms in uscita; durante la riproduzione l'HUD sparisce dopo 2 s senza movimenti.
/// </summary>
public sealed partial class HudView : Control
{
    // --- costanti di HudOverlay.cs
    private const int TopBarHeight = 60, BtnSize = 34, PlayBtnSize = 58, GapDesired = 68, ExtraBtnVsVolPad = 2, TimelineHeight = 6;
    private const int VolBtnSize = 34, VolTrackThickness = 4, VolKnobRadius = 5;
    private const int IdleHideDelayMs = 2000, HideGraceMs = 900, FadeInMs = 280, FadeOutMs = 240;

    public static readonly Color Accent = Color.FromRgb(50, 128, 235);          // Theme.DefaultAccent
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface SemiBold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Regular = new(Inter, FontStyle.Normal, FontWeight.Normal);

    // --- stato fornito dall'app
    public string Title { get; set; } = "";
    public string InfoLine { get; set; } = "";
    // La posizione arriva dal motore 4 volte al secondo: fra un arrivo e l'altro la timeline avanza da sola col tempo
    // (al massimo 0,6 s), cosi' scorre continua invece che a scatti.
    private double _position;
    private long _positionStamp;
    public double Position
    {
        get => _position;
        set { _position = value; _positionStamp = System.Diagnostics.Stopwatch.GetTimestamp(); }
    }
    private double ShownPosition => IsPlaying && !_dragTimeline && Duration > 0
        ? Math.Min(Duration, _position + Math.Min(0.6, System.Diagnostics.Stopwatch.GetElapsedTime(_positionStamp).TotalSeconds))
        : _position;
    private int _lastProgressPixel = -1, _lastProgressSecond = -1;
    public double Duration { get; set; }
    // Pausa/play cambia l'icona: si ridisegna subito (prima restava l'icona vecchia finche' il mouse non passava sopra).
    public bool IsPlaying { get => _isPlaying; set { if (_isPlaying != value) { _isPlaying = value; _positionStamp = System.Diagnostics.Stopwatch.GetTimestamp(); InvalidateVisual(); } } }
    private bool _isPlaying;
    public float Volume { get; set; } = 1f;
    public bool IsMuted { get; set; }
    /// <summary>Tasti capitolo precedente/successivo: sempre presenti come su Windows (spazio permettendo).</summary>
    public bool ShowChapters { get; set; } = true;
    /// <summary>Il video e' a schermo sotto la finestra: fondo trasparente. Senza video, fondo nero come il player.</summary>
    public bool VideoVisible { get; set; }
    /// <summary>Fondo nero pieno per un momento (cambio di schermo intero: la finestra video non segue l'animazione).</summary>
    public bool Blackout { get => _blackout; set { if (_blackout != value) { _blackout = value; InvalidateVisual(); } } }
    private bool _blackout;
    /// <summary>Solo per le prove (catture dello schermo): l'HUD non sparisce mai.</summary>
    public static readonly bool AlwaysVisible = Environment.GetEnvironmentVariable("CINECORE_HUD_PIN") == "1";

    public event Action? PlayPauseClicked, CloseClicked, OpenClicked, FullscreenClicked, SettingsClicked, InfoClicked, PrevChapterClicked, NextChapterClicked, MuteClicked;
    public event Action<int>? SkipClicked;       // secondi, -10 / +10
    /// <summary>Pressione lunga su -10/+10 (420 ms, come Windows): avvia o accelera lo scan (-1 indietro, +1 avanti).</summary>
    public event Action<int>? ScanStepRequested;
    private int _scanHoldDir;
    private bool _scanHoldTriggered;
    private Avalonia.Threading.DispatcherTimer? _scanHold;
    /// <summary>Scan in corso (-1/0/+1) e velocita' scritta sopra il pulsante attivo.</summary>
    public int ScanDirection { get; private set; }
    private string? _scanLabel;
    public void SetScanState(int direction, string? label)
    {
        if (direction == 0) label = null;
        if (ScanDirection == direction && _scanLabel == label) return;
        ScanDirection = direction; _scanLabel = label;
        InvalidateVisual();
    }
    public event Action<double>? SeekRequested;  // secondi
    /// <summary>Clic sul video fuori dai comandi (menu dei DVD): true se e' stato usato. Punti in coordinate del controllo.</summary>
    public Func<Point, bool>? VideoClick;
    public Action<Point>? VideoHover;
    public event Action<float>? VolumeChanged;   // 0..1
    /// <summary>Centro della riga in alto, in unita' della finestra (allineamento dei pulsanti della finestra).</summary>
    internal double TopBarCenter => (_rcTopBar.Top + _rcTopBar.Height / 2) * _scale;
    public event Action<PointerPressedEventArgs>? DragWindowRequested; // trascinamento dalla barra in alto (finestra senza bordi)
    /// <summary>Tasto destro rilasciato (o Ctrl+clic, il tasto destro del Mac): punto in coordinate dell'HUD.</summary>
    /// <summary>L'HUD compare o sparisce (0..1): i pulsanti della finestra lo seguono.</summary>
    public event Action<float>? OpacityChanged;
    public float HudOpacity => _opacity;

    // --- disposizione (unita' di base, prima della scala)
    private Rect _rcTopBar, _rcBottomPanel, _rcTimeline, _rcTimelineHit;
    private Rect _rcRemove, _rcOpen, _rcPlay, _rcBack, _rcFwd, _rcPrev, _rcNext, _rcFull, _rcVolIcon, _rcVolTrack, _rcVolPanelHit;
    private Rect _rcTopInfo, _rcTopSettings, _rcTopCast, _rcTopQuality;
    private bool _showBackFwd, _showPrevNext;
    private double _scale = 1;

    // --- interazione
    private Rect? _hot;
    private bool _timelineHover, _dragTimeline, _dragVol, _volHot;
    // Anteprima sopra la barra del tempo: cinque riquadri (null dove il fotogramma non c'e' ancora).
    private Bitmap?[]? _previewTiles;
    private double _previewSec, _hoverSec;
    private bool _previewing;
    public bool IsFullscreen { get => _isFullscreen; set { if (_isFullscreen != value) { _isFullscreen = value; InvalidateVisual(); } } }
    private bool _isFullscreen;
    private double _topChromeInset;
    public double TopChromeInset
    {
        get => _topChromeInset;
        set { if (_topChromeInset != value) { _topChromeInset = value; InvalidateVisual(); } }
    }

    public event Action<double>? PreviewRequested;
    public event Action? PreviewDismissed;

    public void SetPreview(Bitmap?[]? tiles, double seconds)
    {
        _previewTiles = tiles;
        _previewSec = seconds;
        InvalidateVisual();
    }
    private double _dragSec;
    private DateTime _lastMove = DateTime.UtcNow;
    private float _opacity, _fadeFrom, _fadeTarget = 1f;
    private double _fadeElapsed;
    private DateTime _fadeLast = DateTime.UtcNow;
    private DateTime _lastProgressPaint;
    private readonly DispatcherTimer _timer;
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public HudView()
    {
        ClipToBounds = true;
        Focusable = true;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && !_timer.IsEnabled) { _fadeLast = DateTime.UtcNow; _timer.Start(); }
    }

    /// <summary>Mostra l'HUD subito (nuovo file, tasto premuto).</summary>
    public void Poke() { if (DiscMenuActive?.Invoke() == true) return; if (!_menuBlock) { _forcedHide = false; _wakeAnchor = null; } _lastMove = DateTime.UtcNow; }

    /// <summary>
    /// Menu di un DVD con pulsanti: l'HUD resta chiuso (tasti, Invio e mouse vanno al disco); il puntatore resta visibile.
    /// </summary>
    public Func<bool>? DiscMenuActive { get; set; }
    private bool InDiscMenu => DiscMenuActive?.Invoke() == true;

    // Menu del tasto destro: durante il film prende il posto della barra (FadeOutNow) e, chiuso il menu, l'HUD non
    // torna da solo: riappare a un movimento voluto del mouse (HUD_WAKE_AFTER_AUTOHIDE_PX = 18 su Windows).
    private bool _forcedHide, _menuBlock;
    private Point? _wakeAnchor;
    private const double WakeAfterHidePx = 18;

    /// <summary>Si apre il menu: l'HUD si chiude con la sua dissolvenza.</summary>
    public void FadeOutNow() { _forcedHide = true; _menuBlock = true; _wakeAnchor = null; _hot = null; InvalidateVisual(); }

    /// <summary>Il menu si e' chiuso nel punto <paramref name="anchor"/>: l'HUD aspetta un movimento voluto.</summary>
    public void MenuClosed(Point anchor) { _menuBlock = false; _wakeAnchor = anchor; }

    // Puntatore: posizione vera del mouse (vedi MacCursor). Torna visibile a qualsiasi movimento, anche quando
    // l'HUD aspetta un movimento voluto o gli eventi non arrivano alla finestra; si nasconde solo a mouse fermo.
    private Point? _cursorAt;
    private DateTime _cursorMoved = DateTime.UtcNow;

    private void CursorTick()
    {
        var top = TopLevel.GetTopLevel(this);
        var at = MacCursor.ScreenLocation();
        bool moved = at is { } now && (_cursorAt is not { } before || Math.Abs(now.X - before.X) >= 1 || Math.Abs(now.Y - before.Y) >= 1);
        if (moved) { _cursorAt = at; _cursorMoved = DateTime.UtcNow; }
        Point? inside = top != null && at is { } screen ? MacCursor.InWindow(top, screen) : null;
        bool active = top is Window { IsActive: true };
        // Movimento dentro la finestra che non e' arrivato come evento (es. finestra video di Wine sopra): vale lo stesso.
        if (moved && IsVisible && active && inside is { } p && top != null) PointerActivity(top.TranslatePoint(p, this) ?? p);
        bool hide = IsVisible && IsPlaying && active && inside != null && !_menuBlock && _opacity <= 0.01f && !InDiscMenu
            && (DateTime.UtcNow - _cursorMoved).TotalMilliseconds >= 400;
        MacCursor.SetHidden(hide);
    }

    /// <summary>Il mouse si e' mosso sull'HUD nel punto <paramref name="at"/>: falso se l'HUD deve restare chiuso.</summary>
    private bool PointerActivity(Point at)
    {
        // Menu del disco: l'HUD resta bloccato (i pulsanti del disco sono spesso proprio in basso).
        if (InDiscMenu && _opacity <= .01f) return false;
        if (_forcedHide)
        {
            if (_menuBlock || _wakeAnchor is not { } anchor || (Math.Abs(at.X - anchor.X) < WakeAfterHidePx && Math.Abs(at.Y - anchor.Y) < WakeAfterHidePx)) return false;
            _forcedHide = false; _wakeAnchor = null;
        }
        _lastMove = DateTime.UtcNow;
        return true;
    }

    private void Tick()
    {
        CursorTick();
        // HUD nascosto (libreria, impostazioni): il timer si ferma (prima 66 risvegli al secondo anche a riposo);
        // CursorTick qui sopra ha gia' rimesso il puntatore. Riparte quando l'HUD torna visibile.
        if (!IsVisible) { _timer.Stop(); return; }
        bool pointerBusy = _dragTimeline || _dragVol;
        bool keep = !_forcedHide && (AlwaysVisible || !IsPlaying || pointerBusy || _infoOpen || _castOpen || (DateTime.UtcNow - _lastMove).TotalMilliseconds < Math.Max(IdleHideDelayMs, HideGraceMs));
        // Menu del disco: l'HUD si chiude subito e resta chiuso finche' il puntatore non va sul bordo basso.
        if (keep && !pointerBusy && !_infoOpen && !_castOpen && InDiscMenu) keep = false;
        float target = keep ? 1f : 0f;
        if (target != _fadeTarget) { _fadeFrom = _opacity; _fadeTarget = target; _fadeElapsed = 0; _fadeLast = DateTime.UtcNow; }
        if (_opacity != _fadeTarget)
        {
            var now = DateTime.UtcNow;
            _fadeElapsed += Math.Min(34, (now - _fadeLast).TotalMilliseconds);
            _fadeLast = now;
            double duration = _fadeTarget > 0 ? FadeInMs : FadeOutMs;
            float t = (float)Math.Clamp(_fadeElapsed / duration, 0, 1);
            _opacity = t >= 1 ? _fadeTarget : _fadeFrom + (_fadeTarget - _fadeFrom) * t * t * (3 - 2 * t);
            OpacityChanged?.Invoke(_opacity);
            InvalidateVisual();
        }
        else if (_opacity > 0 && IsPlaying)
        {
            // Si ridisegna quando la timeline si sposta di un pixel o cambia il secondo mostrato (non a ogni giro).
            int pixel = Duration > 0 ? (int)(_rcTimeline.Width * Math.Clamp(ShownPosition / Duration, 0, 1) * 2) : -1;
            int second = (int)ShownPosition;
            if (pixel != _lastProgressPixel || second != _lastProgressSecond || (DateTime.UtcNow - _lastProgressPaint).TotalMilliseconds >= 1000)
            {
                _lastProgressPixel = pixel; _lastProgressSecond = second;
                _lastProgressPaint = DateTime.UtcNow;
                InvalidateVisual();
            }
        }
        if (InfoTick() | CastTick()) InvalidateVisual();
        // Senza HUD si nasconde anche il puntatore, come su Windows: lo fa CursorTick, mai un cursore "nessuno" di Avalonia.
        var cursor = _infoCloseHover ? HandCursor : Cursor.Default;
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
    }

    // ------------------------------------------------------------------ disposizione (RecalcLayout)
    private void Layout(Size size)
    {
        _scale = Math.Max(.5, Math.Min(1, Math.Min(size.Width / 900, size.Height / 480)));
        double w = Math.Round(size.Width / _scale), h = Math.Round(size.Height / _scale);
        int topPanelH = Math.Min(50, Math.Max(42, TopBarHeight - 12));
        _rcTopBar = new Rect(12, 8 + Math.Round(TopChromeInset / _scale), Math.Max(1, w - 24), topPanelH);
        double panelH = Math.Min(86, Math.Max(70, Math.Floor(h / 9)));
        _rcBottomPanel = new Rect(12, Math.Max(_rcTopBar.Bottom + 12, h - panelH - 14), Math.Max(1, w - 24), panelH);

        double timelineY = Math.Max(_rcTopBar.Bottom + 18, _rcBottomPanel.Top - 24);
        double timeReserve = Math.Max(64, Math.Max(MeasureText(FormatTime(Duration), TimeFont), MeasureText(FormatTime(Position), TimeFont)) + 36);
        _rcTimeline = new Rect(timeReserve, timelineY, Math.Max(40, w - timeReserve * 2), TimelineHeight);
        _rcTimelineHit = _rcTimeline.Inflate(new Thickness(16, 32));

        double btnTop = _rcBottomPanel.Top + Math.Floor((_rcBottomPanel.Height - BtnSize) / 2);
        double playTop = _rcBottomPanel.Top + Math.Floor((_rcBottomPanel.Height - PlayBtnSize) / 2);
        const int sidePad = 26;
        _rcFull = new Rect(_rcBottomPanel.Right - sidePad - BtnSize, btnTop, BtnSize, BtnSize);

        double inlineTrackW = Math.Min(164, Math.Max(104, Math.Floor(w / 10)));
        double inlineTrackX = _rcFull.Left - 26 - inlineTrackW;
        double inlineTrackY = btnTop + BtnSize / 2 - VolTrackThickness / 2;
        _rcVolTrack = new Rect(inlineTrackX, inlineTrackY, inlineTrackW, VolTrackThickness);
        _rcVolIcon = new Rect(_rcVolTrack.Left - 12 - VolBtnSize, btnTop, VolBtnSize, VolBtnSize);
        _rcVolPanelHit = _rcVolIcon.Union(_rcVolTrack.Inflate(new Thickness(0, 12))).Inflate(new Thickness(8, 10));

        _rcRemove = new Rect(_rcBottomPanel.Left + sidePad, btnTop, BtnSize, BtnSize);
        _rcOpen = new Rect(_rcRemove.Right + 22, btnTop, BtnSize, BtnSize);

        double leftBound = _rcOpen.Right + 36;
        double rightBound = _rcVolIcon.X - 28 - ExtraBtnVsVolPad;
        double usable = Math.Max(0, rightBound - leftBound);
        double gap = Math.Clamp(GapDesired, 44, Math.Max(44, Math.Floor(usable / 5)));
        double centerX = Math.Floor(w / 2);
        centerX = Math.Max(leftBound + PlayBtnSize / 2, Math.Min(centerX, rightBound - PlayBtnSize / 2));
        _rcPlay = new Rect(centerX - PlayBtnSize / 2, playTop, PlayBtnSize, PlayBtnSize);
        _rcBack = new Rect(centerX - gap - BtnSize / 2, btnTop, BtnSize, BtnSize);
        _rcFwd = new Rect(centerX + gap - BtnSize / 2, btnTop, BtnSize, BtnSize);
        _rcPrev = new Rect(centerX - gap * 2 - BtnSize / 2, btnTop, BtnSize, BtnSize);
        _rcNext = new Rect(centerX + gap * 2 - BtnSize / 2, btnTop, BtnSize, BtnSize);
        _showBackFwd = _rcBack.X >= leftBound && _rcFwd.Right <= rightBound;
        _showPrevNext = ShowChapters && _rcPrev.X >= leftBound && _rcNext.Right <= rightBound;

        double topBtnTop = _rcTopBar.Top + Math.Floor((_rcTopBar.Height - BtnSize) / 2);
        _rcTopInfo = new Rect(Math.Max(_rcTopBar.Left + 16, _rcTopBar.Right - 16 - BtnSize), topBtnTop, BtnSize, BtnSize);
        _rcTopSettings = new Rect(Math.Max(_rcTopBar.Left + 16, _rcTopInfo.Left - 8 - BtnSize), topBtnTop, BtnSize, BtnSize);
        // Cast: a sinistra delle impostazioni, solo quando c'e' qualcosa da mostrare; qualita' ancora a sinistra.
        _rcTopCast = CastAvailable ? new Rect(Math.Max(_rcTopBar.Left + 16, _rcTopSettings.Left - 8 - BtnSize), topBtnTop, BtnSize, BtnSize) : default;
        Rect qualityAnchor = _rcTopCast.Width <= 0 ? _rcTopSettings : _rcTopCast;
        double qualityWidth = Math.Max(46, MeasureText(QualityLabel, TimeFont) + 24);
        _rcTopQuality = QualityAvailable ? new Rect(Math.Max(_rcTopBar.Left + 16, qualityAnchor.Left - 8 - qualityWidth), topBtnTop, qualityWidth, BtnSize) : default;
    }

    /// <summary>Il tasto piu' a sinistra della fascia in alto (titolo e scritta si fermano prima).</summary>
    private Rect LeftmostTop => _rcTopQuality.Width > 0 ? _rcTopQuality : _rcTopCast.Width > 0 ? _rcTopCast : _rcTopSettings;

    // ------------------------------------------------------------------ disegno (PaintHud)
    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        // Fondo quasi trasparente: su macOS le zone del tutto trasparenti di una finestra lasciano passare il mouse
        // alla finestra sotto (il video), e l'HUD non saprebbe piu' quando ricomparire.
        // Mantiene la superficie cliccabile anche con l'HUD nascosto e il video in un'altra finestra.
        ctx.FillRectangle(new SolidColorBrush(VideoVisible && !_blackout ? Color.FromArgb(4, 0, 0, 0) : Colors.Black), new Rect(size));
        Layout(size);
        LayoutPrompts(Math.Round(size.Width / _scale), Math.Round(size.Height / _scale));
        if (_opacity <= 0.01f)
        {
            // Comandi nascosti: "Salta intro" / "Prossimo episodio" restano da soli.
            if (PromptActive) using (ctx.PushTransform(Matrix.CreateScale(_scale, _scale))) DrawPrompts(ctx);
            return;
        }
        float op = Math.Clamp(_opacity, 0f, 1f);

        DrawVignette(ctx, size, op);
        using var scaled = ctx.PushTransform(Matrix.CreateScale(_scale, _scale));

        // Info aperto: prende il posto della fascia del titolo in alto.
        if (InfoVisible) DrawInfoSheet(ctx, Math.Round(size.Width / _scale), Math.Round(size.Height / _scale));
        if (!InfoVisible) DrawTopBar(ctx, op);
        // Cast aperto: prende il posto di timeline e comandi.
        if (CastVisible) { DrawCastSheet(ctx, Math.Round(size.Width / _scale), Math.Round(size.Height / _scale)); return; }
        DrawTimeline(ctx, op);
        DrawTimelinePreview(ctx, op, size);

        DrawButton(ctx, _rcRemove, "close", op);
        DrawButton(ctx, _rcOpen, "library", op);
        DrawButton(ctx, _rcPlay, IsPlaying && ScanDirection == 0 ? "pause" : "play", op, primary: true);
        if (_showBackFwd)
        {
            DrawButton(ctx, _rcBack, "skip-back", op); DrawButton(ctx, _rcFwd, "skip-forward", op);
            // Scan: la velocita' (x0,5 … x4) sopra il pulsante della direzione, in colore d'accento.
            if (ScanDirection != 0 && !string.IsNullOrEmpty(_scanLabel))
            {
                var active = ScanDirection < 0 ? _rcBack : _rcFwd;
                ShadowText(ctx, _scanLabel, TimeFont, new Rect(active.X - 20, active.Y - 22, active.Width + 40, 20), Color.FromArgb((byte)(255 * op), UiTheme.Accent.R, UiTheme.Accent.G, UiTheme.Accent.B), TextAlignment.Center);
            }
        }
        if (_showPrevNext) { DrawButton(ctx, _rcPrev, "previous", op); DrawButton(ctx, _rcNext, "next", op); }
        DrawButton(ctx, _rcFull, IsFullscreen ? "minimize" : "maximize", op);
        DrawVolume(ctx, op);
        if (!_infoOpen) DrawPrompts(ctx);
    }

    /// <summary>
    /// Vignettatura del video (PlayerForm.VideoVignette su Windows, dove con madVR e MPC-VR sostituisce i veli
    /// dell'HUD): piu' scura dietro titolo e comandi, lati appena velati; con Info aperto quella che scende dall'alto.
    /// </summary>
    private static readonly bool _qaVignette = Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null;
    private (Vignette.Kind, object?) _qaLastVignette;
    private void DrawVignette(DrawingContext ctx, Size size, float op)
    {
        if (!VideoVisible) return; // fondo gia' nero
        // La vignettatura è una sfumatura: la stessa mappa va bene in finestra e a schermo intero.
        var px = new PixelSize(640, 360);
        var kind = CastVisible ? Vignette.Kind.Cast : Vignette.Kind.Normal;
        var bitmap = Vignette.Get(kind, px, InvalidateVisual)
                     ?? (kind != Vignette.Kind.Normal ? Vignette.Get(Vignette.Kind.Normal, px, InvalidateVisual) : null);
        if (bitmap == null) return;
        if (_qaVignette && (kind, bitmap) != _qaLastVignette) { _qaLastVignette = (kind, bitmap); Console.Error.WriteLine($"[qa] vignette {kind} bmp={bitmap.GetHashCode()} px={px} op={op:0.00}"); }
        using (ctx.PushOpacity(op))
            ctx.DrawImage(bitmap, new Rect(size));
    }

    /// <summary>HudShadowText: testo con un'ombra appena accennata 1 punto sotto.</summary>
    private static void ShadowText(DrawingContext ctx, string text, double size, Rect bounds, Color color, TextAlignment align = TextAlignment.Left, Typeface? face = null)
    {
        if (string.IsNullOrWhiteSpace(text) || color.A == 0) return;
        CachedText Make(Color c) => UiText.Get(L10n.T(text), face ?? SemiBold, size, c, bounds.Width, 1, align);
        var main = Make(color);
        double y = bounds.Y + (bounds.Height - main.Height) / 2;
        ctx.DrawText(Make(Color.FromArgb((byte)(color.A * 150 / 255), 6, 8, 10)), new Point(bounds.X, y + 1));
        ctx.DrawText(main, new Point(bounds.X, y));
    }

    private static double MeasureText(string text, double size) =>
        UiText.Get(L10n.T(text), SemiBold, size, Colors.White).Width;

    // Caratteri di Windows: Segoe UI Semibold 10 e 14 punti a 96 dpi = 13,3 e 18,7 pixel; qui Inter SemiBold.
    private const double TimeFont = 13.3, TitleFont = 18.7;

    private void DrawTopBar(DrawingContext ctx, float op)
    {
        double textLeft = _rcTopBar.Left + 18;
        double textRight = LeftmostTop.X - 12;
        if (!string.IsNullOrWhiteSpace(InfoLine))
        {
            double infoW = Math.Min(Math.Max(92, MeasureText(InfoLine, TimeFont) + 4), Math.Max(110, _rcTopBar.Width / 4));
            var rcInfo = new Rect(Math.Max(textLeft, LeftmostTop.Left - 12 - infoW), _rcTopBar.Top + (_rcTopBar.Height - 28) / 2, infoW, 28);
            textRight = Math.Min(textRight, rcInfo.Left - 12);
            ShadowText(ctx, InfoLine, TimeFont, rcInfo, Color.FromArgb((byte)(225 * op), 232, 238, 244), TextAlignment.Right);
        }
        if (!string.IsNullOrWhiteSpace(Title) && textRight - textLeft > 10)
            ShadowText(ctx, Title, TitleFont, new Rect(textLeft, _rcTopBar.Top, Math.Max(20, textRight - textLeft), _rcTopBar.Height),
                Color.FromArgb((byte)(245 * op), 252, 252, 253));
        DrawIcon(ctx, _rcTopSettings, "settings", _hot == _rcTopSettings ? Color.FromRgb(255, 255, 255) : Color.FromRgb(200, 210, 220), op, 18);
        DrawIcon(ctx, _rcTopInfo, "info", _hot == _rcTopInfo ? Color.FromRgb(255, 255, 255) : Color.FromRgb(200, 210, 220), op, 18);
        if (_rcTopCast.Width > 0) DrawIcon(ctx, _rcTopCast, "person", _hot == _rcTopCast || _castOpen ? Color.FromRgb(255, 255, 255) : Color.FromRgb(200, 210, 220), op, 18);
        if (_rcTopQuality.Width > 0)
        {
            Color ink = _hot == _rcTopQuality ? Accent : Color.FromRgb(230, 236, 244);
            ink = WithAlpha(ink, 245 * op);
            var r = _rcTopQuality;
            ShadowText(ctx, QualityLabel, TimeFont, new Rect(r.X, r.Y, r.Width - 15, r.Height), ink, TextAlignment.Right);
            var pen = new Pen(new SolidColorBrush(ink), 1.4, lineCap: PenLineCap.Round);
            ctx.DrawLine(pen, new Point(r.Right - 11, r.Center.Y - 1), new Point(r.Right - 7, r.Center.Y + 3));
            ctx.DrawLine(pen, new Point(r.Right - 7, r.Center.Y + 3), new Point(r.Right - 3, r.Center.Y - 1));
        }
    }

    private void DrawTimeline(DrawingContext ctx, float op)
    {
        bool dragging = _dragTimeline;
        double pos = dragging ? _dragSec : ShownPosition, dur = Duration;
        double cy = _rcTimeline.Top + _rcTimeline.Height / 2;
        bool active = dragging || _timelineHover;
        double thick = active ? Math.Max(4, _rcTimeline.Height) : Math.Max(2, _rcTimeline.Height * .6);
        var bg = new Pen(new SolidColorBrush(Color.FromArgb((byte)(64 * op), 236, 243, 250)), thick, lineCap: PenLineCap.Round);
        ctx.DrawLine(bg, new Point(_rcTimeline.Left, cy), new Point(_rcTimeline.Right, cy));
        if (dur > 0)
        {
            double wProg = _rcTimeline.Width * Math.Clamp(pos / dur, 0, 1);
            var fg = new Pen(new SolidColorBrush(active ? WithAlpha(Accent, 255 * op) : Color.FromArgb((byte)(245 * op), 246, 248, 250)), thick, lineCap: PenLineCap.Round);
            if (wProg > 0) ctx.DrawLine(fg, new Point(_rcTimeline.Left, cy), new Point(_rcTimeline.Left + wProg, cy));
            if (active)
            {
                double knobX = _rcTimeline.X + _rcTimeline.Width * Math.Clamp(pos / dur, 0, 1);
                double d = dragging ? 16 : 14;
                ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(90 * op), 4, 6, 8)), null, new Point(knobX, cy + 1), d / 2, d / 2);
                ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * op), 250, 251, 252)), null, new Point(knobX, cy), d / 2, d / 2);
            }
        }
        string posText = FormatTime(pos), durText = dur > 0 ? FormatTime(dur) : "";
        var ink = Color.FromArgb((byte)(235 * op), 250, 251, 252);
        double posW = MeasureText(posText, TimeFont), rowH = 22;
        ShadowText(ctx, posText, TimeFont, new Rect(Math.Max(10, _rcTimeline.Left - posW - 18), cy - rowH / 2, posW + 2, rowH), ink);
        if (durText.Length > 0)
        {
            double durW = MeasureText(durText, TimeFont);
            double right = Math.Round(Bounds.Width / _scale) - 18;
            ShadowText(ctx, durText, TimeFont, new Rect(Math.Min(_rcTimeline.Right + 18, right - durW - 2), cy - rowH / 2, durW + 2, rowH), ink);
        }
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private void DrawButton(DrawingContext ctx, Rect r, string icon, float op, bool primary = false)
    {
        // DrawRoundButtonSvg: sotto il puntatore l'icona prende il colore d'accento, niente segni attorno.
        bool hot = _hot == r;
        var tint = hot ? Accent : primary ? Color.FromRgb(224, 232, 255) : Color.FromRgb(222, 232, 242);
        DrawIcon(ctx, r, icon, tint, op, 16);
    }

    private static void DrawIcon(DrawingContext ctx, Rect r, string icon, Color tint, float op, double minSize)
    {
        var svg = SvgIcon.Get(icon);
        if (svg == null) return;
        double s = Math.Max(minSize, Math.Min(r.Width, r.Height) - 10);
        var dest = new Rect(r.X + (r.Width - s) / 2, r.Y + (r.Height - s) / 2, s, s);
        svg.Draw(ctx, dest, WithAlpha(tint, tint.A * op));
    }

    private string VolumeIcon => IsMuted || Volume <= 0.001f ? "mute" : Volume < .5f ? "volume-low" : "volume";

    private void DrawVolume(DrawingContext ctx, float op)
    {
        float shown = IsMuted ? 0f : Math.Clamp(Volume, 0f, 1f);
        bool active = _volHot || _dragVol;
        DrawIcon(ctx, _rcVolIcon, VolumeIcon, active ? Accent : Color.FromRgb(222, 232, 242), op, 16);
        double thick = active ? Math.Max(4, VolTrackThickness) : Math.Max(2, VolTrackThickness * .6);
        double cy = _rcVolTrack.Top + _rcVolTrack.Height / 2;
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(64 * op), 236, 243, 250)), thick, lineCap: PenLineCap.Round),
            new Point(_rcVolTrack.Left, cy), new Point(_rcVolTrack.Right, cy));
        double fillRight = _rcVolTrack.Left + shown * _rcVolTrack.Width;
        if (fillRight > _rcVolTrack.Left)
            ctx.DrawLine(new Pen(new SolidColorBrush(active ? WithAlpha(Accent, 255 * op) : Color.FromArgb((byte)(245 * op), 246, 248, 250)), thick, lineCap: PenLineCap.Round),
                new Point(_rcVolTrack.Left, cy), new Point(fillRight, cy));
        if (active)
        {
            double r = _dragVol ? VolKnobRadius + 2 : VolKnobRadius + 1;
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(90 * op), 4, 6, 8)), null, new Point(fillRight, cy + 1), r, r);
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * op), 250, 251, 252)), null, new Point(fillRight, cy), r, r);
        }
    }

    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a, 0, 255), c.R, c.G, c.B);

    // ------------------------------------------------------------------ mouse
    private Point Base(PointerEventArgs e) { var p = e.GetPosition(this); return new Point(p.X / _scale, p.Y / _scale); }

    /// <summary>Il punto cade su un comando visibile dell'HUD (pulsanti, timeline, volume)?</summary>
    private bool OnControl(Point p, bool hidden) => !hidden &&
        (Buttons.Any(r => r.Width > 0 && r.Contains(p)) || _rcTimelineHit.Contains(p) || _rcVolTrack.Inflate(new Thickness(6, 12)).Contains(p) || _rcVolIcon.Contains(p));

    private Rect[] Buttons => _infoOpen ? TopExtras.Concat(ButtonsAll).Where(r => r != _rcTopSettings && r != _rcTopInfo && r.Width > 0).ToArray()
        : _castOpen ? new[] { _rcTopSettings, _rcTopInfo, _rcTopCast, _rcTopQuality }.Where(r => r.Width > 0).ToArray()
        : TopExtras.Concat(ButtonsAll).ToArray();
    private Rect[] TopExtras => new[] { _rcTopCast, _rcTopQuality }.Where(r => r.Width > 0).ToArray();
    private Rect[] ButtonsAll => _showPrevNext
        ? new[] { _rcRemove, _rcOpen, _rcPlay, _rcBack, _rcFwd, _rcPrev, _rcNext, _rcFull, _rcTopSettings, _rcTopInfo }
        : _showBackFwd ? new[] { _rcRemove, _rcOpen, _rcPlay, _rcBack, _rcFwd, _rcFull, _rcTopSettings, _rcTopInfo }
        : new[] { _rcRemove, _rcOpen, _rcPlay, _rcFull, _rcTopSettings, _rcTopInfo };

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        VideoHover?.Invoke(e.GetPosition(this));
        if (!PointerActivity(e.GetPosition(this))) return;
        if (PromptActive) PromptMouseMove(Base(e));
        if (_opacity <= 0.01f) { InvalidateVisual(); return; }
        var p = Base(e);
        if (_dragTimeline) { _dragSec = TimelineSeconds(p.X); RequestPreview(_dragSec); InvalidateVisual(); return; }
        if (_infoOpen) InfoMouseMove(p);
        if (_castOpen) CastMouseMove(p);
        if (_dragVol) { SetVolumeAt(p.X); return; }
        Rect? hot = null;
        foreach (var r in Buttons) if (r.Contains(p)) { hot = r; break; }
        bool tl = _rcTimelineHit.Contains(p) && Duration > 0;
        bool vol = _rcVolPanelHit.Contains(p);
        if (hot != _hot || tl != _timelineHover || vol != _volHot) { _hot = hot; _timelineHover = tl; _volHot = vol; InvalidateVisual(); }
        if (tl) RequestPreview(TimelineSeconds(p.X));
        else if (_previewing && !PreviewCardBounds().Contains(p)) DismissPreview();
    }

    private void RequestPreview(double seconds)
    {
        _previewing = true;
        _hoverSec = seconds;
        PreviewRequested?.Invoke(seconds);
        InvalidateVisual();
    }

    /// <summary>Solo per le prove: anteprima come col puntatore sulla barra in quel punto.</summary>
    public void TestPreview(double seconds) { _timelineHover = true; RequestPreview(seconds); }

    private void DismissPreview()
    {
        if (!_previewing) return;
        _previewing = false;
        _previewTiles = null;
        PreviewDismissed?.Invoke();
        InvalidateVisual();
    }

    // Striscia di Windows: 5 riquadri da 256 con 2 di spazio, alta 144; centrata sulla barra, sopra di 16.
    private const double TileW = 256, TileH = 144, TileGap = 2, StripW = TileW * 5 + TileGap * 6;

    private Rect PreviewCardBounds()
    {
        if (_previewTiles == null || Duration <= 0 || _rcTimeline.Width <= 0) return default;
        double width = Bounds.Width / _scale;
        double stripW = _previewTiles.Length == 1 ? TileW + TileGap * 2 : StripW;
        double imageW = Math.Min(stripW, width - 24), imageH = Math.Max(1, Math.Round(TileH * imageW / stripW));
        double cardH = imageH + 28;
        double x = Math.Clamp(_rcTimeline.X + _rcTimeline.Width / 2 - imageW / 2, 8, Math.Max(8, width - imageW - 8));
        double y = Math.Max(_rcTopBar.Bottom + 8, _rcTimeline.Y - cardH - 16);
        return new Rect(x, y, imageW, cardH);
    }

    /// <summary>DrawTimelinePreviewCard: la striscia di fotogrammi, o almeno l'orario sopra il puntatore.</summary>
    private void DrawTimelinePreview(DrawingContext ctx, float op, Size size)
    {
        if (!_previewing || Duration <= 0) return;
        var card = PreviewCardBounds();
        if (card.Width <= 0)
        {
            string label = FormatTime(_hoverSec);
            double w = MeasureText(label, TimeFont) + 2, h = 20;
            double x = _rcTimeline.Left + Math.Clamp(_hoverSec / Duration, 0, 1) * _rcTimeline.Width - w / 2;
            x = Math.Clamp(x, 8, Math.Max(8, size.Width / _scale - w - 8));
            ShadowText(ctx, label, TimeFont, new Rect(x, _rcTimeline.Top - h - 10, w, h), Color.FromArgb((byte)(255 * op), 236, 243, 250));
            return;
        }
        var image = new Rect(card.X, card.Y, card.Width, card.Height - 28);
        // Disco di rete / rclone: un fotogramma solo (TimelinePreview.Single), senza i vicini in ombra.
        int count = _previewTiles!.Length;
        double k = image.Width / (count == 1 ? TileW + TileGap * 2 : StripW);
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(255 * op), 0, 0, 0)), image);
        for (int i = 0; i < count; i++)
        {
            var tile = new Rect(image.X + (TileGap + i * (TileW + TileGap)) * k, image.Y, TileW * k, image.Height);
            if (_previewTiles![i] is { } frame)
            {
                // Riquadro 16:9 riempito senza deformare: si taglia solo l'eccesso.
                double fw = frame.PixelSize.Width, fh = frame.PixelSize.Height, ratio = TileW / TileH;
                double cw = fw, ch = fh;
                if (cw / ch > ratio) cw = ch * ratio; else ch = cw / ratio;
                using (ctx.PushOpacity(op))
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    ctx.DrawImage(frame, new Rect((fw - cw) / 2, (fh - ch) / 2, cw, ch), tile);
            }
            else
            {
                var dot = new SolidColorBrush(Color.FromArgb((byte)(255 * op), 130, 150, 170));
                for (int d = 0; d < 3; d++) ctx.DrawEllipse(dot, null, new Point(tile.X + tile.Width / 2 - 10 + d * 10, tile.Y + tile.Height / 2 + 2), 2, 2);
            }
        }
        if (count == 1)
        {
            ShadowText(ctx, FormatTime(_previewSec), TimeFont, new Rect(card.X, image.Bottom + 2, card.Width, Math.Max(20, card.Bottom - image.Bottom)),
                Color.FromArgb((byte)(255 * op), 250, 251, 252), TextAlignment.Center);
            return;
        }
        // I quattro ai lati (dieci e venti secondi prima e dopo) in ombra; quello scelto con il riquadro bianco.
        double t = image.Width / 5;
        var shade = new SolidColorBrush(Color.FromArgb((byte)(120 * op), 6, 8, 10));
        ctx.FillRectangle(shade, new Rect(image.X, image.Y, t * 2, image.Height));
        ctx.FillRectangle(shade, new Rect(image.X + t * 3, image.Y, image.Width - t * 3, image.Height));
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * op), 250, 251, 252)), 2), new Rect(image.X + t * 2 + 1, image.Y + 1, t - 2, image.Height - 2));
        ShadowText(ctx, FormatTime(_previewSec), TimeFont, new Rect(card.X, image.Bottom + 2, card.Width, Math.Max(20, card.Bottom - image.Bottom)),
            Color.FromArgb((byte)(255 * op), 250, 251, 252), TextAlignment.Center);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hot = null; _timelineHover = false; _volHot = false;
        if (!_dragTimeline) DismissPreview();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        // MainWindow intercetta il clic secondario a livello della finestra.
        if (props.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        _lastMove = DateTime.UtcNow;
        if (!props.IsLeftButtonPressed) return;
        bool wasHidden = _opacity <= 0.01f;
        var p = Base(e);
        if (!_infoOpen && !_castOpen && PromptMouseDown(p)) return;
        if (_infoOpen && InfoSheetBounds.Contains(p)) { if (_infoCloseRect.Contains(p)) InfoCloseRequested?.Invoke(); return; }
        if (!_infoOpen && _rcTopCast.Width > 0 && _rcTopCast.Contains(p)) { CastClicked?.Invoke(); return; }
        if (!_infoOpen && _rcTopQuality.Width > 0 && _rcTopQuality.Contains(p)) { QualityClicked?.Invoke(new Point(_rcTopQuality.Right * _scale, _rcTopQuality.Bottom * _scale + 8)); return; }
        if (CastMouseDown(p)) return;
        if (_castOpen) { if (_rcTopSettings.Contains(p)) SettingsClicked?.Invoke(); else if (_rcTopInfo.Contains(p)) InfoClicked?.Invoke(); return; }
        if (VideoClick != null && !OnControl(p, wasHidden) && VideoClick(e.GetPosition(this))) return;
        if (e.ClickCount == 2 && !_rcBottomPanel.Contains(p) && !_rcTopBar.Contains(p)) { FullscreenClicked?.Invoke(); return; }
        if (wasHidden) return; // il primo clic sull'HUD nascosto lo fa solo ricomparire
        if (_rcTimelineHit.Contains(p) && Duration > 0)
        {
            _dragTimeline = true; _dragSec = TimelineSeconds(p.X);
            e.Pointer.Capture(this); InvalidateVisual(); return;
        }
        if (_rcVolTrack.Inflate(new Thickness(6, 12)).Contains(p))
        {
            _dragVol = true; e.Pointer.Capture(this); SetVolumeAt(p.X); return;
        }
        if (_rcVolIcon.Contains(p)) { MuteClicked?.Invoke(); return; }
        if (_rcPlay.Contains(p)) PlayPauseClicked?.Invoke();
        else if (_rcRemove.Contains(p)) CloseClicked?.Invoke();
        else if (_rcOpen.Contains(p)) OpenClicked?.Invoke();
        else if (_rcFull.Contains(p)) FullscreenClicked?.Invoke();
        else if (_showBackFwd && (_rcBack.Contains(p) || _rcFwd.Contains(p)))
        {
            // Il salto parte al rilascio; tenendo premuto 420 ms parte invece lo scan.
            _scanHoldDir = _rcBack.Contains(p) ? -1 : 1;
            _scanHoldTriggered = false;
            _scanHold ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(420), Avalonia.Threading.DispatcherPriority.Input, (_, _) =>
            {
                _scanHold!.Stop();
                if (_scanHoldDir == 0) return;
                _scanHoldTriggered = true;
                ScanStepRequested?.Invoke(_scanHoldDir);
            });
            _scanHold.Stop(); _scanHold.Start();
            e.Pointer.Capture(this);
        }
        else if (_showPrevNext && _rcPrev.Contains(p)) PrevChapterClicked?.Invoke();
        else if (_showPrevNext && _rcNext.Contains(p)) NextChapterClicked?.Invoke();
        else if (!_infoOpen && _rcTopSettings.Contains(p)) SettingsClicked?.Invoke();
        else if (!_infoOpen && _rcTopInfo.Contains(p)) InfoClicked?.Invoke();
        else if (_rcTopBar.Contains(p)) DragWindowRequested?.Invoke(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_infoOpen && InfoSheetBounds.Contains(Base(e)))
        {
            _infoScroll = Math.Clamp(_infoScroll - e.Delta.Y * 56, 0, _infoScrollMax);
            InvalidateVisual(); e.Handled = true; return;
        }
        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (_scanHoldDir != 0)
        {
            _scanHold?.Stop();
            if (!_scanHoldTriggered) SkipClicked?.Invoke(_scanHoldDir * 10);
            _scanHoldDir = 0;
        }
        if (_dragTimeline)
        {
            _dragTimeline = false;
            Position = _dragSec;
            SeekRequested?.Invoke(_dragSec);
            DismissPreview();
        }
        _dragVol = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragTimeline || _dragVol) CancelPointerInteraction();
    }

    public void CancelPointerInteraction()
    {
        _dragTimeline = _dragVol = _timelineHover = _volHot = false;
        _hot = null;
        DismissPreview();
        InvalidateVisual();
    }

    private double TimelineSeconds(double x) =>
        Duration <= 0 ? 0 : Math.Clamp((x - _rcTimeline.Left) / Math.Max(1, _rcTimeline.Width), 0, 1) * Duration;

    private void SetVolumeAt(double x)
    {
        float v = (float)Math.Clamp((x - _rcVolTrack.Left) / Math.Max(1, _rcVolTrack.Width), 0, 1);
        Volume = v; IsMuted = false;
        VolumeChanged?.Invoke(v);
        InvalidateVisual();
    }
}
