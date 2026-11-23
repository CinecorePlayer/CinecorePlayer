using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Brano a schermo intero, tradotto da HUD/MusicFullscreenView.cs. Vista 3: copertina grande al centro su uno sfondo
/// fatto dei colori della copertina (come Apple Music). Vista 4: copertina a sinistra e testo sincronizzato a destra,
/// che scorre da solo; un clic su una riga salta li'. Vista 5: a destra le informazioni del file (su Windows anche i
/// grafici del Cinecore Audio Engine, che sul Mac non c'e'). Si apre allargando la copertina della barra e si chiude
/// riportandola li'. La barra della musica resta sopra.
/// </summary>
internal sealed partial class LibraryView
{
    private int _musicView;            // 0 chiusa, 3 brano, 4 testo, 5 informazioni
    private int _musicViewShown;       // contenuto della colonna mentre la vista si chiude
    private float _modeT, _modeFrom;   // 0 = copertina sola, 1 = con la colonna
    private long _modeStart = -1, _fsEntryStart = -1, _fsExitStart = -1;
    private Rect _fsEntryFrom, _fsExitTo;
    private DispatcherTimer? _fsFrame;
    private long _fsLastFrame;
    private Rect _fsClose;

    // Sfondo dai colori della copertina, preparato fuori dal thread dell'interfaccia.
    private WriteableBitmap? _fsBackground, _fsPreviousBackground;
    private string _fsBackgroundFor = "";
    private long _fsBackgroundFade = -1;
    private int _fsBackgroundJob;

    // Testo.
    private static MusicLyricsService? _lyricsService;
    private string _lyricsFor = "";
    private IReadOnlyList<MusicLyricsService.LyricsLine> _lyricLines = Array.Empty<MusicLyricsService.LyricsLine>();
    private string _lyricPlain = "";
    private bool _lyricsLoading;
    private int _lyricActive = -1, _lyricHot = -1;
    private double _lyricScroll = double.NaN, _lyricManual;
    private long _lyricManualUntil;
    private readonly List<(Rect Bounds, int Line)> _lyricHits = new();
    private CancellationTokenSource? _lyricsCts;

    // Informazioni del file (vista 5).
    private string _infoFor = "";
    private List<(string Label, string Value)> _infoRows = new();

    public bool MusicFullscreenOpen => _musicView != 0 || _fsExitStart >= 0;

    private double FsUnit => Math.Max(.5, Math.Min(Bounds.Width / 1920, Math.Max(1, Bounds.Height) / 1080));
    private double FU(double v) => Math.Round(v * FsUnit);

    private void ShowMusicFullscreen(int view)
    {
        if (!MusicBarVisible) return;
        bool wasOpen = _musicView != 0 && _fsExitStart < 0;
        int previous = _musicView;
        _fsExitStart = -1;
        if (view == 5 && previous != 5) ResetCharts(); // come Windows: entrando nei grafici si parte dalla prima scheda
        _musicView = view; _musicViewShown = view;
        if (view != 5) _audio.AnalysisEnabled = false;
        float target = view == 3 ? 0 : 1;
        if (!wasOpen)
        {
            _modeT = target; _modeStart = -1;
            _fsEntryFrom = _barArt; _fsEntryStart = NowMs;
            _queueOpen = false;
        }
        else if (previous != view && Math.Abs(_modeT - target) > .001)
        {
            _modeFrom = _modeT; _modeStart = NowMs; _lyricScroll = double.NaN;
        }
        if (view == 4) LoadMusicLyricsIfNeeded();
        if (view == 5) LoadMusicInfoIfNeeded();
        RequestMusicBackground();
        StartFsFrames();
        InvalidateVisual();
    }

    private void ToggleMusicFullscreen(int view)
    {
        if (_musicView == view && _fsExitStart < 0) CloseMusicFullscreen();
        else ShowMusicFullscreen(view);
    }

    // Foto della libreria per l'uscita dallo schermo intero (la pagina compare sotto la vista che sfuma).
    private Avalonia.Media.Imaging.RenderTargetBitmap? _fsPageCache;
    private Size _fsPageCacheSize;
    private bool _fsPageCapture;

    private void CaptureFullscreenPage()
    {
        _fsPageCache?.Dispose(); _fsPageCache = null;
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        try
        {
            _fsPageCapture = true;
            double scale = Math.Min(2, Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
            var shot = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Math.Ceiling(Bounds.Width * scale), (int)Math.Ceiling(Bounds.Height * scale)), new Vector(96, 96));
            // Come SurfaceCrossfade: CreateDrawingContext + scala a 96 dpi (RenderTargetBitmap.Render applica i DPI due volte).
            using (var dc = shot.CreateDrawingContext())
            using (dc.PushTransform(Matrix.CreateScale(scale, scale)))
                Render(dc);
            _fsPageCache = shot; _fsPageCacheSize = Bounds.Size;
        }
        catch { _fsPageCache = null; }
        finally { _fsPageCapture = false; }
    }

    /// <summary>La copertina torna in quella della barra, il resto sfuma.</summary>
    private void CloseMusicFullscreen()
    {
        _audio.AnalysisEnabled = false; // l'analisi (FFT, loudness) serve solo ai grafici aperti
        if (_musicView == 0 || _fsExitStart >= 0) return;
        CaptureFullscreenPage();
        _musicView = 0;
        _fsExitTo = _barArt; _fsExitStart = NowMs; _fsEntryStart = -1;
        StartFsFrames();
        InvalidateVisual();
    }

    private void StartFsFrames()
    {
        _fsFrame ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => FsFrame());
        _fsLastFrame = NowMs;
        if (!_fsFrame.IsEnabled) _fsFrame.Start();
    }

    private void FsFrame()
    {
        long now = NowMs;
        double dt = Math.Clamp((now - _fsLastFrame) / 1000.0, 0, .1);
        _fsLastFrame = now;
        bool animating = false;
        if (_modeStart >= 0)
        {
            float t = (float)Math.Clamp((now - _modeStart) / 520.0, 0, 1);
            float target = _musicView == 3 ? 0 : 1;
            _modeT = _modeFrom + (target - _modeFrom) * t;
            if (t >= 1) { _modeT = target; _modeStart = -1; }
            animating = true;
        }
        if (_fsEntryStart >= 0) { if (now - _fsEntryStart >= 520) _fsEntryStart = -1; animating = true; }
        if (_fsExitStart >= 0)
        {
            animating = true;
            if (now - _fsExitStart >= 360) { _fsExitStart = -1; animating = false; _fsPageCache?.Dispose(); _fsPageCache = null; }
        }
        if (_fsBackgroundFade >= 0) { if (now - _fsBackgroundFade >= 600) { _fsBackgroundFade = -1; _fsPreviousBackground?.Dispose(); _fsPreviousBackground = null; } animating = true; }
        if (_musicView == 4)
        {
            // Attesa del testo: i puntini cambiano ogni 260 ms, li ridisegna _lyricWatch quando cambiano.
            if (_lyricLines.Count > 0)
            {
                int active = FindActiveLyric(_audio.Position);
                if (active != _lyricActive) _lyricActive = active;
                if (_lyricManualUntil > 0 && now > _lyricManualUntil) _lyricManualUntil = 0;
                if (_lyricManualUntil == 0 && Math.Abs(_lyricManual) > .5)
                {
                    _lyricManual *= Math.Exp(-dt / .12);
                    if (Math.Abs(_lyricManual) <= .5) _lyricManual = 0;
                }
                double target = LyricScrollTarget();
                // Inseguimento morbido, come lo scorrimento di Apple Music.
                if (double.IsNaN(_lyricScroll)) _lyricScroll = target;
                else if (Math.Abs(target - _lyricScroll) > .4) _lyricScroll += (target - _lyricScroll) * (1 - Math.Exp(-dt / .11));
                else _lyricScroll = target;
                // Solo mentre il testo si muove (scorrimento, puntini prima del primo verso). A riga ferma i fotogrammi si
                // fermano e _lyricWatch li riavvia quando arriva il verso dopo (voce 18 di Windows): prima si ridisegnava
                // tutta la vista a ogni refresh dello schermo per tutto il brano.
                animating = Math.Abs(target - _lyricScroll) > .4 || _lyricActive < 0 || Math.Abs(_lyricManual) > .5 || _lyricManualUntil > 0;
            }
        }
        if (_musicView == 5 && StepVu()) animating = true;
        if (_musicView == 5 && ((_fsTab != TabInfo && _audio.IsPlaying) || ChartsAnimating)) animating = true;
        InvalidateVisual();
        if (!animating)
        {
            _fsFrame?.Stop();
            if (_musicView == 4) StartLyricWatch();
        }
    }

    private bool _fsRunning => _fsFrame?.IsEnabled == true;
    /// <summary>Brano di cui si sta sincronizzando il testo con la voce (null = nessuno).</summary>
    private string? _lyricSyncFor => _lyricsService?.SynchronizationProgress is { Path: { } path } ? path : null;

    /// <summary>
    /// Con il testo fermo controlla ogni 30 ms se e' arrivato il verso dopo (o se un salto o la finestra hanno cambiato
    /// il punto da mostrare) e solo allora riavvia i fotogrammi. Durante l'attesa del testo e la sincronizzazione
    /// ridisegna solo quando cambia il puntino acceso o la percentuale.
    /// </summary>
    private DispatcherTimer? _lyricWatch;
    private long _lyricWatchState = -1;

    private void StartLyricWatch()
    {
        if (_lyricWatch == null)
        {
            // Creato senza gestore: con il gestore nel costruttore Avalonia lo farebbe partire subito.
            _lyricWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
            _lyricWatch.Tick += (_, _) => LyricWatchTick();
        }
        if (!_lyricWatch.IsEnabled) _lyricWatch.Start();
    }

    private void LyricWatchTick()
    {
        if (_musicView != 4 || !MusicFullscreenOpen || _fsRunning) { _lyricWatch?.Stop(); return; }
        int? percent = LyricSyncPercent();
        if (_lyricsLoading || percent != null)
        {
            // Stessi valori che usa il disegno: fase dei puntini e percentuale.
            long state = _lyricsLoading ? NowMs / 260 % 3 : 10 + percent!.Value;
            if (state != _lyricWatchState) { _lyricWatchState = state; InvalidateVisual(); }
            if (_lyricsLoading) return;
        }
        else _lyricWatchState = -1;
        if (_lyricLines.Count == 0) { if (percent == null) _lyricWatch?.Stop(); return; }
        if (FindActiveLyric(_audio.Position) != _lyricActive || Math.Abs(LyricScrollTarget() - _lyricScroll) > .4)
        {
            _lyricWatch?.Stop();
            StartFsFrames();
        }
    }

    // ------------------------------------------------------------------ misure (LayoutFor, LyricsColumn)
    private Rect FsContentArea(Rect page) => new(0, FU(40), page.Width, Math.Max(1, page.Height - MusicBarH - FU(40) - FU(24)));

    private (Rect Cover, Rect Title, Rect Sub) FsLayout(Rect page, double t)
    {
        var area = FsContentArea(page);
        double titleH = FU(40), subH = FU(34), gap = FU(30), w = page.Width;
        double sideA = Math.Max(FU(120), Math.Min(area.Height - titleH - subH - gap - FU(20), w * .40));
        double blockA = sideA + gap + titleH + subH;
        var coverA = new Rect((w - sideA) / 2, area.Y + (area.Height - blockA) / 2, sideA, sideA);
        double sideB = Math.Max(FU(120), Math.Min(area.Height - titleH - subH - gap - FU(60), w * .30));
        double blockB = sideB + gap + titleH + subH;
        var coverB = new Rect(w * .27 - sideB / 2, area.Y + (area.Height - blockB) / 2, sideB, sideB);
        double e = 1 - Math.Pow(1 - t, 3);
        var cover = Lerp(coverA, coverB, e);
        double textW = Math.Max(cover.Width, FU(760) + (cover.Width - FU(760)) * e);
        double textX = cover.X + cover.Width / 2 - textW / 2;
        var title = new Rect(textX, cover.Bottom + gap, textW, titleH);
        return (cover, title, new Rect(textX, title.Bottom, textW, subH));
    }

    private static Rect Lerp(Rect a, Rect b, double e) =>
        new(a.X + (b.X - a.X) * e, a.Y + (b.Y - a.Y) * e, a.Width + (b.Width - a.Width) * e, a.Height + (b.Height - a.Height) * e);

    private Rect FsColumn(Rect page)
    {
        var area = FsContentArea(page);
        double left = page.Width * .51, right = page.Width * .93;
        return new Rect(left, area.Y + FU(20), Math.Max(1, right - left), Math.Max(1, area.Height - FU(40)));
    }

    // ------------------------------------------------------------------ disegno
    private void DrawMusicFullscreen(DrawingContext ctx, Rect page)
    {
        if (!MusicFullscreenOpen || !MusicBarVisible) return;
        var track = _queue[_queueIndex];
        double exit = _fsExitStart >= 0 ? Math.Clamp((NowMs - _fsExitStart) / 360.0, 0, 1) : 0;
        double exitK = exit * exit * (3 - 2 * exit);
        double entry = _fsEntryStart >= 0 ? Math.Clamp((NowMs - _fsEntryStart) / 520.0, 0, 1) : 1;
        double ease = 1 - Math.Pow(1 - entry, 3);
        // Il fondo entra con la copertina ed esce con lei.
        double fade = _fsExitStart >= 0 ? 1 - exitK : Math.Clamp(entry / .35, 0, 1);
        using var opacity = ctx.PushOpacity(fade);
        // Sotto la vista la pagina non reagisce (come una scheda); la barra in basso resta viva.
        _hitFloor = _hits.Count;
        _hits.Add(new Hit(page, "musicfs-panel"));

        if (_fsBackground is { } background)
        {
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                ctx.DrawImage(background, new Rect(background.Size), page);
                if (_fsPreviousBackground is { } previous && _fsBackgroundFade >= 0)
                    using (ctx.PushOpacity(1 - Math.Clamp((NowMs - _fsBackgroundFade) / 600.0, 0, 1)))
                        ctx.DrawImage(previous, new Rect(previous.Size), page);
            }
            // Vignettatura in basso (dove sta la barra) e appena ai bordi.
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(72, 0, 0, 0), 1) },
            }, new Rect(0, page.Height * .62, page.Width, page.Height * .38));
        }
        else ctx.FillRectangle(B(Color.FromRgb(18, 20, 26)), page);

        var (cover, titleRect, subRect) = FsLayout(page, _modeT);
        if (entry < 1 && _fsEntryFrom.Width > 0) cover = Lerp(_fsEntryFrom, cover, ease);
        double textAlpha = Math.Clamp((entry - .45) / .55, 0, 1);
        if (_fsExitStart >= 0 && _fsExitTo.Width > 0)
        {
            cover = Lerp(cover, _fsExitTo, exitK);
            textAlpha = Math.Clamp(1 - exit * 3, 0, 1);
        }
        DrawFsCover(ctx, track, cover, titleRect, subRect, textAlpha);

        _lyricHits.Clear();
        int shown = _musicView != 0 ? _musicView : _musicViewShown;
        if (_modeT > .01)
        {
            double columnAlpha = Math.Clamp((_modeT - .25) / .75, 0, 1) * textAlpha;
            var column = FsColumn(page);
            if (shown == 5) DrawFsAnalysis(ctx, column, columnAlpha);
            else DrawFsLyrics(ctx, column, columnAlpha);
        }

        // Chiudi in alto a destra (Theme.DrawCloseButton su fondo scuro).
        _fsClose = new Rect(page.Width - FU(48) - FU(24), FU(22) + Math.Round(TopShift), FU(48), FU(48));
        bool hot = Hover(_fsClose);
        // Come tutte le X: al passaggio solo piu' bianca, senza cerchio.
        double c = Math.Max(5, Math.Floor(_fsClose.Width * 21 / 100));
        var pen = new Pen(B(Color.FromArgb((byte)(hot ? 255 : 210), 255, 255, 255)), Math.Max(1.6, FU(2.2)), lineCap: PenLineCap.Round);
        ctx.DrawLine(pen, new Point(_fsClose.Center.X - c, _fsClose.Center.Y - c), new Point(_fsClose.Center.X + c, _fsClose.Center.Y + c));
        ctx.DrawLine(pen, new Point(_fsClose.Center.X + c, _fsClose.Center.Y - c), new Point(_fsClose.Center.X - c, _fsClose.Center.Y + c));
        if (_fsExitStart < 0)
        {
            _hits.Add(new Hit(_fsClose, "musicfs-close"));
            foreach (var (bounds, line) in _lyricHits) _hits.Add(new Hit(bounds, "musicfs-line", line.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private void DrawFsCover(DrawingContext ctx, LibraryItem track, Rect cover, Rect titleRect, Rect subRect, double textAlpha)
    {
        if (cover.Width < 4) return;
        double radius = Math.Max(4, cover.Width / 60);
        // Ombra morbida sotto la copertina (gaussiana).
        double sigma = Math.Max(1, 22.0 * cover.Width / 400);
        var shadow = new BoxShadows(new BoxShadow { OffsetY = FU(10), Blur = sigma * 2, Color = Color.FromArgb(122, 0, 0, 0) });
        ctx.DrawRectangle(B(Color.FromArgb(255, 0, 0, 0)), null, new RoundedRect(cover, radius), shadow);
        using (ctx.PushGeometryClip(new RectangleGeometry(cover, radius, radius)))
        {
            if (Image(track.ArtPath, cover.Width) is { } bmp) DrawCover(ctx, bmp, cover);
            else DrawMusicPlaceholder(ctx, cover);
        }
        if (textAlpha <= 0) return;
        string artist = UnknownTag(track.ArtistName) ? "" : track.ArtistName!;
        string album = UnknownTag(track.AlbumTitle) ? "" : track.AlbumTitle!;
        string sub = string.IsNullOrWhiteSpace(album) || string.Equals(album, artist, StringComparison.OrdinalIgnoreCase) ? artist
            : string.IsNullOrWhiteSpace(artist) ? album : artist + " — " + album;
        FsText(ctx, TrackTitle(track), Math.Max(14, FU(28)), DisplaySemibold, Color.FromArgb((byte)(246 * textAlpha), 255, 255, 255), titleRect, TextAlignment.Center);
        FsText(ctx, sub, Math.Max(12, FU(24)), DisplayRegular, Color.FromArgb((byte)(185 * textAlpha), 255, 255, 255), subRect, TextAlignment.Center);
    }

    /// <summary>Testo in pixel (le misure di MusicFullscreenView sono in pixel, non in punti).</summary>
    private static void FsText(DrawingContext ctx, string text, double px, Typeface face, Color color, Rect r, TextAlignment align)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = UiText.Get(L10n.T(text), face, px, color, r.Width, 1, align);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    // ------------------------------------------------------------------ testo
    private double LyricPx => Math.Max(18, FU(50));
    private double WaitBlock => FU(96);

    private static readonly Typeface LyricFace = new(DisplaySemibold.FontFamily, FontStyle.Normal, FontWeight.Bold);
    private CachedText LyricText(string text, double width, Color color) =>
        UiText.Get(text, LyricFace, LyricPx, color, width, 0, trimming: TextTrimming.None);

    private readonly Dictionary<int, double> _lyricHeights = new();
    private double _lyricHeightsWidth;
    private object? _lyricHeightsFor;

    private double LyricLineHeight(int i, double width)
    {
        if (_lyricHeightsWidth != width || !ReferenceEquals(_lyricHeightsFor, _lyricLines)) { _lyricHeights.Clear(); _lyricHeightsWidth = width; _lyricHeightsFor = _lyricLines; }
        if (_lyricHeights.TryGetValue(i, out double h)) return h;
        string text = _lyricLines[i].Text.Trim();
        double measured = string.IsNullOrWhiteSpace(text) ? LyricPx * 1.2 * .6 : LyricText(text, width, Colors.White).Height;
        h = measured + FU(34);
        _lyricHeights[i] = h;
        return h;
    }

    private int FindActiveLyric(double position)
    {
        int best = -1;
        for (int i = 0; i < _lyricLines.Count; i++) { if (_lyricLines[i].TimeSeconds <= position + .05) best = i; else break; }
        return best;
    }

    private double LyricScrollTarget()
    {
        if (_lyricLines.Count == 0) return 0;
        if (_lyricActive < 0) return _lyricManual;
        double width = FsColumn(new Rect(Bounds.Size)).Width, y = WaitBlock;
        for (int i = 0; i < _lyricActive && i < _lyricLines.Count; i++) y += LyricLineHeight(i, width);
        return y + _lyricManual;
    }

    private void DrawFsLyrics(DrawingContext ctx, Rect column, double alpha)
    {
        byte Ink(double value) => (byte)Math.Clamp(Math.Round(value * alpha), 0, 255);
        using var clip = ctx.PushClip(column);
        if (_lyricsLoading)
        {
            double dot = FU(12), gap = FU(16);
            int phase = (int)(NowMs / 260 % 3);
            double y = column.Y + column.Height * .38;
            for (int i = 0; i < 3; i++)
                ctx.DrawEllipse(B(Color.FromArgb(Ink(i == phase ? 235 : 90), 255, 255, 255)), null, new Point(column.X + i * (dot + gap) + dot / 2, y + dot / 2), dot / 2, dot / 2);
            return;
        }
        if (_lyricLines.Count > 0)
        {
            double anchor = column.Y + column.Height * .30;
            double y = anchor - (double.IsNaN(_lyricScroll) ? 0 : _lyricScroll);
            // Prima del primo verso: tre puntini che respirano.
            double dotsAlpha = _lyricActive < 0 ? 1 : Math.Clamp(1 - (anchor - y) / WaitBlock, 0, 1);
            if (dotsAlpha > .01)
            {
                double dot = Math.Max(6, FU(14)), gap = FU(16), phase = NowMs / 900.0;
                for (int i = 0; i < 3; i++)
                {
                    double pulse = .5 + .5 * Math.Sin(phase * Math.PI * 2 - i * .9);
                    double size = dot * (.8 + .3 * pulse);
                    ctx.DrawEllipse(B(Color.FromArgb(Ink((90 + 150 * pulse) * dotsAlpha), 255, 255, 255)), null,
                        new Point(column.X + i * (dot + gap) + dot / 2, y + FU(24) + dot / 2), size / 2, size / 2);
                }
            }
            y += WaitBlock;
            for (int i = 0; i < _lyricLines.Count; i++)
            {
                double h = LyricLineHeight(i, column.Width);
                var bounds = new Rect(column.X, y, column.Width, h);
                y += h;
                if (bounds.Bottom < column.Y - h || bounds.Y > column.Bottom) continue;
                int distance = _lyricActive < 0 ? i + 1 : Math.Abs(i - _lyricActive);
                // Riga corrente piena; le altre si attenuano con la distanza, come in Apple Music.
                double ink = i == _lyricActive ? 255 : i < _lyricActive ? 70 : Math.Max(50, 125 - distance * 15);
                if (i == _lyricHot && i != _lyricActive) ink = Math.Min(255, ink + 70);
                double edge = FU(90);
                double fadeTop = Math.Clamp((bounds.Y + h * .5 - column.Y) / edge, 0, 1);
                double fadeBottom = Math.Clamp((column.Bottom - (bounds.Y + h * .5)) / edge, 0, 1);
                ink *= Math.Min(fadeTop, fadeBottom);
                if (ink <= 2) continue;
                string text = _lyricLines[i].Text.Trim();
                if (text.Length == 0) text = "♪";
                ctx.DrawText(LyricText(text, column.Width, Color.FromArgb(Ink(ink), 255, 255, 255)), bounds.TopLeft);
                _lyricHits.Add((bounds.Intersect(column), i));
            }
            return;
        }
        // Riconoscimento in corso su questo brano: solo una riga piccola con la percentuale sopra il testo.
        double syncLine = 0;
        if (LyricSyncPercent() is int percent)
        {
            syncLine = FU(44);
            var note = new FormattedText(L10n.T("Sincronizzo il testo con la voce") + $" · {percent}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                DisplayRegular, Math.Max(11, FU(17)), B(Color.FromArgb(Ink(140), 255, 255, 255))) { MaxTextWidth = column.Width };
            ctx.DrawText(note, new Point(column.X, column.Y + FU(6)));
        }
        bool message = _lyricPlain.Length == 0;
        string body = message ? (_lyricsFor.StartsWith("net:", StringComparison.Ordinal) ? "Il testo si cerca per i brani sul Mac." : "Nessun testo per questo brano.") : _lyricPlain;
        var ft = UiText.Get(message ? L10n.T(body) : body, message ? DisplayRegular : DisplaySemibold,
            message ? Math.Max(14, FU(30)) : Math.Max(14, FU(34)), Color.FromArgb(Ink(message ? 150 : 215), 255, 255, 255), column.Width, 0, trimming: TextTrimming.None);
        double maxScroll = Math.Max(0, ft.Height - column.Height);
        _lyricManual = Math.Clamp(_lyricManual, 0, maxScroll);
        double top = message ? column.Y + column.Height * .30 : column.Y + Math.Max(syncLine, (column.Height - ft.Height) / 2) - _lyricManual;
        ctx.DrawText(ft, new Point(column.X, top));
    }

    /// <summary>Il testo del brano in corso: file .lrc accanto, archivio, poi LRCLIB e gli altri fornitori (MusicLyricsService).</summary>
    private void LoadMusicLyricsIfNeeded()
    {
        if (!MusicBarVisible) return;
        var track = _queue[_queueIndex];
        string key = track.IsNetwork ? "net:" + track.Path : track.Path;
        if (key == _lyricsFor) return;
        _lyricsFor = key;
        _lyricsCts?.Cancel();
        _lyricLines = Array.Empty<MusicLyricsService.LyricsLine>(); _lyricPlain = "";
        _lyricActive = -1; _lyricScroll = double.NaN; _lyricManual = 0; _lyricHeights.Clear();
        if (track.IsNetwork || !File.Exists(track.Path)) { _lyricsLoading = false; return; }
        _lyricsLoading = true;
        // Fotogrammi fermi: i puntini dell'attesa li anima il controllo leggero.
        if (_musicView == 4 && !_fsRunning) StartLyricWatch();
        var cts = _lyricsCts = new CancellationTokenSource();
        void Apply(MusicLyricsService.LyricsResult? result)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || _lyricsFor != key) return;
                _lyricsLoading = false;
                _lyricLines = result?.Lines?.Where(l => double.IsFinite(l.TimeSeconds) && l.TimeSeconds >= 0).OrderBy(l => l.TimeSeconds).ToList()
                              ?? (IReadOnlyList<MusicLyricsService.LyricsLine>)Array.Empty<MusicLyricsService.LyricsLine>();
                if (result is { IsSynced: false }) _lyricLines = Array.Empty<MusicLyricsService.LyricsLine>();
                _lyricPlain = (result?.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
                _lyricActive = -1; _lyricScroll = double.NaN; _lyricManual = 0; _lyricHeights.Clear();
                if (_musicView == 4) StartFsFrames();
                InvalidateVisual();
            });
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await LyricsService.FindForMediaAsync(track.Path, cts.Token, provider => Apply(provider), synced => Apply(synced));
                Apply(result);
            }
            catch (Exception ex) { Console.Error.WriteLine("testi: " + ex.Message); Apply(null); }
        });
    }

    private MusicLyricsService LyricsService
    {
        get
        {
            if (_lyricsService != null) return _lyricsService;
            lock (typeof(LibraryView))
            {
                if (_lyricsService == null)
                {
                    var service = new MusicLyricsService(new FasterWhisperLyricsRecognitionBackend());
                    // La riga con la percentuale sopra il testo segue il riconoscimento.
                    service.SynchronizationStatusChanged += () => Dispatcher.UIThread.Post(() => { if (_musicView == 4) InvalidateVisual(); });
                    _lyricsService = service;
                }
            }
            return _lyricsService;
        }
    }

    /// <summary>Percentuale della sincronizzazione del brano in corso, se sta lavorando proprio su questo.</summary>
    private int? LyricSyncPercent()
    {
        if (_lyricsService?.SynchronizationProgress is not { Path: { } path } progress || !MusicBarVisible) return null;
        return string.Equals(path, _queue[_queueIndex].Path, StringComparison.OrdinalIgnoreCase) ? progress.Percent : null;
    }

    /// <summary>Tre puntini della barra della musica: sincronizza adesso il testo del brano (anche se era stato scartato).</summary>
    private MenuEntry LyricSyncMenuEntry()
    {
        var track = _queue[_queueIndex];
        bool possible = !track.IsNetwork && File.Exists(track.Path) && _lyricLines.Count == 0 && LyricSyncPercent() == null;
        return new MenuEntry("Sincronizza il testo", "lyrics", RequestLyricSync) { Enabled = possible };
    }

    private void RequestLyricSync()
    {
        var track = _queue[_queueIndex];
        string path = track.Path;
        void Done(MusicLyricsService.LyricsResult result) => Dispatcher.UIThread.Post(() =>
        {
            if (_lyricsFor != path) return;
            if (result.IsSynced) { _lyricsFor = ""; LoadMusicLyricsIfNeeded(); }
            InvalidateVisual();
        });
        _ = Task.Run(async () =>
        {
            var service = LyricsService;
            // Testo non ancora cercato per questo brano: prima lo si cerca (la sincronizzazione parte da sola).
            if (!service.SynchronizeNow(path, Done))
                await service.FindForMediaAsync(path, CancellationToken.None, null, Done).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => { if (_musicView != 4) ShowMusicFullscreen(4); InvalidateVisual(); });
        });
    }

    // ------------------------------------------------------------------ informazioni (vista 5)
    private void LoadMusicInfoIfNeeded()
    {
        var track = _queue[_queueIndex];
        if (_infoFor == track.Path) return;
        _infoFor = track.Path;
        _infoRows = new();
        if (track.IsNetwork) { _infoRows.Add(("Fonte", "Server di rete")); return; }
        string path = track.Path;
        _ = Task.Run(async () =>
        {
            var probe = await MediaProbe.RunAsync(path);
            var rows = new List<(string, string)>();
            var it = CultureInfo.InvariantCulture;
            if (probe != null)
            {
                if (probe.AudioCodec.Length > 0) rows.Add(("Formato", probe.AudioCodec.ToUpperInvariant() + (probe.AudioProfile.Length > 0 ? " " + probe.AudioProfile : "")));
                int kbps = probe.AudioKbps > 0 ? probe.AudioKbps : probe.OverallKbps;
                if (kbps > 0) rows.Add(("Bitrate", kbps >= 1000 ? (kbps / 1000.0).ToString("0.00", it).Replace('.', ',') + " Mbps" : kbps + " kbps"));
                if (probe.AudioRate > 0) rows.Add(("Campionamento", (probe.AudioRate / 1000.0).ToString("0.0##", it).Replace('.', ',') + " kHz"));
                if (probe.AudioChannels > 0) rows.Add(("Canali", probe.AudioChannels switch { 1 => "Mono", 2 => "Stereo", 6 => "5.1", 8 => "7.1", var n => n.ToString(it) }));
                if (probe.Duration > 0) rows.Add(("Durata", Clock(probe.Duration)));
                if (probe.SizeBytes > 0) rows.Add(("Dimensione", (probe.SizeBytes / (double)(1 << 20)).ToString("0.0", it).Replace('.', ',') + " MB"));
                // ffprobe elenca i nomi della famiglia (mov,mp4,m4a...): si mostra l'estensione del file.
                string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                if (ext.Length > 0) rows.Add(("Contenitore", ext));
            }
            rows.Add(("Uscita", "Cinecore Audio Engine  ·  " + (MacSettings.AudioDeviceName is { Length: > 0 } device ? device : "dispositivo di sistema")));
            Dispatcher.UIThread.Post(() => { if (_infoFor == path) { _infoRows = rows; InvalidateVisual(); } });
        });
    }

    private void DrawFsInfo(DrawingContext ctx, Rect column, double alpha, bool title = true)
    {
        byte Ink(double v) => (byte)Math.Clamp(Math.Round(v * alpha), 0, 255);
        double y = column.Y + column.Height * .18;
        if (title) FsText(ctx, "Informazioni", Math.Max(16, FU(34)), DisplaySemibold, Color.FromArgb(Ink(240), 255, 255, 255), new Rect(column.X, y, column.Width, FU(50)), TextAlignment.Left);
        y += FU(74);
        double rowH = FU(54), labelW = Math.Min(column.Width * .42, FU(300));
        foreach (var (label, value) in _infoRows)
        {
            FsText(ctx, label, Math.Max(12, FU(22)), DisplayRegular, Color.FromArgb(Ink(150), 255, 255, 255), new Rect(column.X, y, labelW, rowH), TextAlignment.Left);
            FsText(ctx, value, Math.Max(12, FU(22)), DisplaySemibold, Color.FromArgb(Ink(230), 255, 255, 255), new Rect(column.X + labelW, y, column.Width - labelW, rowH), TextAlignment.Left);
            ctx.FillRectangle(B(Color.FromArgb(Ink(26), 255, 255, 255)), new Rect(column.X, y + rowH - 1, column.Width, 1));
            y += rowH;
        }
    }

    // ------------------------------------------------------------------ sfondo (BuildBackground)
    private void RequestMusicBackground()
    {
        if (!MusicBarVisible) return;
        string? art = _queue[_queueIndex].ArtPath;
        string key = art ?? "";
        if (key == _fsBackgroundFor && _fsBackground != null) return;
        _fsBackgroundFor = key;
        int job = ++_fsBackgroundJob;
        double aspect = Bounds.Height > 0 ? Bounds.Width / Bounds.Height : 16 / 9.0;
        _ = Task.Run(async () =>
        {
            float[]? seed = null; int side = 48;
            try
            {
                string? file = art == null ? null : art.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await NetworkStore.CachedArtAsync(art) : art;
                if (file != null && File.Exists(file)) seed = ReadSeed(file, side);
            }
            catch { }
            var built = BuildMusicBackground(seed, side, aspect);
            Dispatcher.UIThread.Post(() =>
            {
                if (job != _fsBackgroundJob) { built.Dispose(); return; }
                bool fade = _fsBackground != null && MusicFullscreenOpen;
                _fsPreviousBackground?.Dispose();
                _fsPreviousBackground = fade ? _fsBackground : null;
                if (!fade) _fsBackground?.Dispose();
                _fsBackground = built;
                _fsBackgroundFade = fade ? NowMs : -1;
                if (fade) StartFsFrames();
                _barGlassAt = 0; // il vetro della barra prende i colori nuovi
                InvalidateVisual();
            });
        });
    }

    /// <summary>La copertina ridotta a side x side, in RGB 0..1.</summary>
    private static float[]? ReadSeed(string file, int side)
    {
        using var fs = File.OpenRead(file);
        using var decoded = Bitmap.DecodeToWidth(fs, side, BitmapInterpolationMode.HighQuality);
        var size = decoded.PixelSize;
        if (size.Width < 1 || size.Height < 1) return null;
        var buffer = new byte[size.Width * size.Height * 4];
        unsafe { fixed (byte* p = buffer) decoded.CopyPixels(new PixelRect(size), (IntPtr)p, buffer.Length, size.Width * 4); }
        bool rgba = decoded.Format == PixelFormat.Rgba8888;
        var seed = new float[side * side * 3];
        // Ritaglio quadrato al centro.
        int crop = Math.Min(size.Width, size.Height), ox = (size.Width - crop) / 2, oy = (size.Height - crop) / 2;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int sx = ox + x * crop / side, sy = oy + y * crop / side;
                int o = (sy * size.Width + sx) * 4, d = (y * side + x) * 3;
                float a = Math.Max(1, (int)buffer[o + 3]) / 255f;
                float r = buffer[o + (rgba ? 0 : 2)] / 255f / a, g = buffer[o + 1] / 255f / a, b = buffer[o + (rgba ? 2 : 0)] / 255f / a;
                seed[d] = Math.Clamp(r, 0, 1); seed[d + 1] = Math.Clamp(g, 0, 1); seed[d + 2] = Math.Clamp(b, 0, 1);
            }
        return seed;
    }

    /// <summary>
    /// Sfondo dai colori della copertina, come Apple Music: la copertina ridotta a poche celle (e, sopra, la stessa girata),
    /// sfocata in virgola mobile, con la luce media portata verso 0,30 perche' il testo bianco si legga. Si disegna
    /// allargata con interpolazione: niente bande.
    /// </summary>
    private static WriteableBitmap BuildMusicBackground(float[]? seed, int side, double aspect)
    {
        int cw = 40, ch = Math.Max(4, (int)Math.Round(cw / Math.Max(.2, aspect)));
        var cells = new float[cw * ch * 3];
        if (seed != null)
        {
            double srcH = Math.Max(1, side / (cw / (double)ch));
            float Sample(double u, double v, int c)
            {
                int x = Math.Clamp((int)(u * side), 0, side - 1);
                int y = Math.Clamp((int)((side - srcH) / 2 + v * srcH), 0, side - 1);
                return seed[(y * side + x) * 3 + c];
            }
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    double u = (x + .5) / cw, v = (y + .5) / ch;
                    for (int c = 0; c < 3; c++)
                        cells[(y * cw + x) * 3 + c] = Sample(u, v, c) * .65f + Sample(1 - u, 1 - v, c) * .35f;
                }
        }
        else for (int i = 0; i < cells.Length; i += 3) { cells[i] = .12f; cells[i + 1] = .13f; cells[i + 2] = .17f; }
        GaussBlur(cells, cw, ch, 2.6);
        double mean = 0;
        for (int i = 0; i < cells.Length; i += 3) mean += .2126 * cells[i] + .7152 * cells[i + 1] + .0722 * cells[i + 2];
        mean /= cells.Length / 3.0;
        float k = (float)Math.Clamp(0.30 / Math.Max(0.02, mean), 0.35, 2.2);
        var bitmap = new WriteableBitmap(new PixelSize(cw, ch), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
        {
            var row = new byte[cw * 4];
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int i = (y * cw + x) * 3;
                    float r = cells[i] * k, g = cells[i + 1] * k, b = cells[i + 2] * k;
                    float l = .2126f * r + .7152f * g + .0722f * b;
                    const float sat = 1.12f;
                    r = l + (r - l) * sat; g = l + (g - l) * sat; b = l + (b - l) * sat;
                    if (l > .42f) { float soft = (.42f + (l - .42f) * .35f) / l; r *= soft; g *= soft; b *= soft; }
                    float edge = 1 - .12f * Math.Abs(x / (float)cw - .5f) * 2;
                    row[x * 4] = (byte)Math.Clamp(b * edge * 255, 0, 255); row[x * 4 + 1] = (byte)Math.Clamp(g * edge * 255, 0, 255);
                    row[x * 4 + 2] = (byte)Math.Clamp(r * edge * 255, 0, 255); row[x * 4 + 3] = 255;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), row.Length);
            }
        }
        return bitmap;
    }

    private static void GaussBlur(float[] cells, int w, int h, double sigma)
    {
        int radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new double[radius * 2 + 1]; double sum = 0;
        for (int i = -radius; i <= radius; i++) { kernel[i + radius] = Math.Exp(-i * i / (2 * sigma * sigma)); sum += kernel[i + radius]; }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        var tmp = new float[cells.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            var src = pass == 0 ? cells : tmp; var dst = pass == 0 ? tmp : cells;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        double acc = 0;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int xx = pass == 0 ? Math.Clamp(x + k, 0, w - 1) : x, yy = pass == 0 ? y : Math.Clamp(y + k, 0, h - 1);
                            acc += src[(yy * w + xx) * 3 + c] * kernel[k + radius];
                        }
                        dst[(y * w + x) * 3 + c] = (float)acc;
                    }
        }
    }

    // ------------------------------------------------------------------ input
    private bool MusicFullscreenActivate(Hit hit)
    {
        switch (hit.Kind)
        {
            case "musicfs-panel": return true;
            case "musicfs-tab": if (int.TryParse(hit.Key, out int tab)) ChooseFsTab(tab); return true;
            case "musicfs-picker": SetFsMenu(!_fsMenuOpen); return true;
            case "musicfs-menu-close": SetFsMenu(false); return true;
            case "musicfs-close": CloseMusicFullscreen(); return true;
            case "musicfs-line":
                if (int.TryParse(hit.Key, out int line) && line < _lyricLines.Count)
                {
                    _lyricManual = 0; _lyricManualUntil = 0;
                    _audio.Seek(Math.Max(0, _lyricLines[line].TimeSeconds));
                    StartFsFrames();
                }
                return true;
        }
        return false;
    }

    private bool MusicFullscreenWheel(double deltaY)
    {
        if (_musicView == 0) return false;
        if (_modeT >= 1)
        {
            _lyricManual -= deltaY * FU(110);
            _lyricManualUntil = _lyricLines.Count > 0 ? NowMs + 3000 : 0;
            StartFsFrames();
        }
        return true;
    }

    private void MusicFullscreenHover(Point p)
    {
        int hot = -1;
        if (_musicView == 4 && _modeT >= 1)
            foreach (var (bounds, line) in _lyricHits) if (bounds.Contains(p)) { hot = line; break; }
        _lyricHot = hot;
    }

    private bool _miniPlayer;

    /// <summary>Mini player: la vista del brano occupa la finestra piccola; all'uscita torna la pagina di prima.</summary>
    public void SetMiniPlayer(bool on)
    {
        _miniPlayer = on;
        // Il mini player non e' piu' il brano a schermo intero rimpicciolito con la barra sotto (titolo e copertina
        // comparivano due volte): e' la barra stessa, a tutta finestra, su vetro.
        if (_musicView != 0 || _fsExitStart >= 0) { _musicView = 0; _fsExitStart = -1; _fsEntryStart = -1; _fsPageCache?.Dispose(); _fsPageCache = null; }
        _volumeOpen = false;
        InvalidateVisual();
    }
}
