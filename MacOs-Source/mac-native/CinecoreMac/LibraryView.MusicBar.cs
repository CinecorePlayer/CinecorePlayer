using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Barra della musica tradotta da HUD/MusicTransportBar.cs: un'isola di vetro al centro, staccata dal fondo e tutta
/// arrotondata. A sinistra copertina, titolo, artista, preferito e "altro"; al centro casuale, precedente, play,
/// successivo, ripeti con la timeline sotto; a destra coda, grafici, testo, brano a schermo intero, libreria,
/// mini player, stop, muto e volume. Su finestre strette i comandi vanno su piu' righe, come su Windows.
/// Entra con una dissolvenza e un leggero scorrimento (360 ms) ed esce al contrario (320 ms).
/// </summary>
internal sealed partial class LibraryView
{
    private readonly Dictionary<string, Rect> _barTargets = new();
    private Rect _barCard, _barArt, _barTitle, _barArtist, _barElapsed, _barEnd;
    private string _barHover = "", _barDrag = "";
    private double _barDragValue, _barHoverSeek = -1;
    private double _seekHoldTarget; private long _seekHoldUntil;

    private const int BarRevealMs = 360, BarHideMs = 320, BarSeekRevealMs = 300;
    private long _barRevealStart = -1, _barHideStart = -1, _barSeekRevealStart = -1;
    private Action? _barHidden;
    private DispatcherTimer? _barAnimTimer;
    private LibraryItem? _barLastTrack;
    private double _barLastDuration;

    private static long NowMs => Environment.TickCount64;
    private static double EaseOut(long start, int ms) => start < 0 ? 1 : 1 - Math.Pow(1 - Math.Clamp((NowMs - start) / (double)ms, 0, 1), 3);
    private static double EaseInOut(long start, int ms)
    {
        if (start < 0) return 1;
        double t = Math.Clamp((NowMs - start) / (double)ms, 0, 1);
        return t * t * (3 - 2 * t);
    }
    private bool BarHiding => _barHideStart >= 0;
    private double BarReveal => BarHiding ? 1 - EaseInOut(_barHideStart, BarHideMs) : EaseOut(_barRevealStart, BarRevealMs);
    private double BarSeekReveal => _audio.Duration > 0 ? EaseOut(_barSeekRevealStart, BarSeekRevealMs) : 0;

    // Misure dell'isola (CardMargin, CardTopSpace, CardWidthFor, CardRadius, PreferredBarHeight di Windows).
    // Come su Windows la barra non si rimpicciolisce con la finestra: in una finestra piccola passa ai comandi su piu' righe.
    private double BarScale => Math.Max(_s, 1);
    private double U(double v) => v * BarScale;
    // Sul Mac (finestre e schermi del portatile) l'isola usa quasi tutta la larghezza fino a 1700 punti: con la larghezza
    // fissa di Windows (1040) in finestra i comandi finivano appiccicati e la timeline era corta. Oltre, come Windows.
    private double CardWidthFor(double width) => Math.Max(1, width <= U(1700) ? width - U(16) * 2 : Math.Max(U(1240), width * .74));
    private double BarHeightFor(double width)
    {
        int rows = BarRows(CardWidthFor(width) / BarScale);
        return U(14) + U(8) + (rows == 3 ? U(174) : rows == 2 ? U(96) : U(68));
    }

    // Spaziatura che segue lo spazio: icone a passo 32-40, volume 90-120, brano 220-340 (in unita' della barra).
    private static double BarActionGap(double w) => Math.Clamp(32 + (w - 1300) / 40, 32, 40);
    private static double BarVolumeW(double w) => Math.Clamp(90 + (w - 1300) / 10, 90, 120);
    private static double BarSongW(double w) => Math.Clamp(w * .25, 220, 340);
    // Volume solo icona: il cursore compare al passaggio sopra le icone vicine, che sfumano.
    private static bool VolumeCollapsed(double w) => true; // scelta dell'utente (14/11): sempre a scomparsa nella barra della musica
    private static double BarActionsW(double w) => (MusicActionKeys.Length - 1) * BarActionGap(w) + 26 + (VolumeCollapsed(w) ? 0 : 14 + BarVolumeW(w));
    /// <summary>Righe della barra: una sola se la timeline fra brano e comandi resta lunga almeno 220.</summary>
    private static int BarRows(double w)
    {
        // Timeline simmetrica attorno al centro dell'isola (sotto i comandi, centrati come su Windows).
        double seek = 2 * Math.Min(w / 2 - (BarSongW(w) + 64), (w - 24 - BarActionsW(w) - 70) - w / 2);
        // Due righe: brano, comandi e icone sulla prima (comandi nel mezzo con almeno 16 di margine), timeline sotto.
        return seek >= 220 ? 1 : w - 24 - BarActionsW(w) - BarSongW(w) >= 186 + 32 ? 2 : 3;
    }
    private double CardRadius => _miniPlayer ? MiniRadius : _barCard.Height <= U(72) ? _barCard.Height / 2 : U(26);
    // Raggio degli angoli della finestra di macOS 26 (il vetro del mini player la riempie tutta).
    private const double MiniRadius = 16;

    private void StartBarAnimation()
    {
        _barAnimTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => BarAnimationTick());
        if (!_barAnimTimer.IsEnabled) _barAnimTimer.Start();
    }

    private void BarAnimationTick()
    {
        // Volume a scomparsa e comparsa della timeline toccano solo la barra; entrata e uscita anche le zone cliccabili.
        if (BarHiding || BarReveal < 1) InvalidateVisual(); else InvalidateBar();
        if (BarHiding)
        {
            if (EaseInOut(_barHideStart, BarHideMs) < 1) return;
            _barAnimTimer?.Stop();
            _barHideStart = -1;
            var done = _barHidden; _barHidden = null;
            done?.Invoke();
            return;
        }
        if (BarReveal >= 1 && (_audio.Duration <= 0 || BarSeekReveal >= 1) && !VolumeAnimating) { _barAnimTimer?.Stop(); InvalidateVisual(); }
    }

    /// <summary>Comparsa: solo all'avvio della riproduzione (non a ogni cambio di brano o di pagina).</summary>
    private void PlayBarReveal()
    {
        _barHideStart = -1; _barHidden = null;
        _barRevealStart = NowMs;
        _barSeekRevealStart = -1;
        _barGlassAt = 0;
        StartBarAnimation();
    }

    /// <summary>Uscita: la comparsa al contrario, poi <paramref name="hidden"/> (che svuota la coda).</summary>
    private void PlayBarHide(Action hidden)
    {
        if (!MusicBarVisible) { hidden(); return; }
        if (BarHiding) { _barHidden = hidden; return; }
        _barHidden = hidden;
        _barHideStart = NowMs;
        StartBarAnimation();
    }

    private void LayoutMusicBar(Rect page)
    {
        _barTargets.Clear();
        if (_miniPlayer) { LayoutMiniPlayer(page); return; }
        double barH = MusicBarH;
        var bar = new Rect(0, page.Bottom - barH, page.Width, barH);
        double cardW = CardWidthFor(page.Width);
        _barCard = new Rect(Math.Round((page.Width - cardW) / 2), bar.Y + U(8), Math.Round(cardW), Math.Max(1, barH - U(14) - U(8)));
        double w = _barCard.Width / BarScale, h = _barCard.Height / BarScale;
        int rows = BarRows(w);
        bool compact = rows == 3, narrow = rows == 2 || compact;
        double gapA = BarActionGap(w), volumeW = BarVolumeW(w), actionsW = BarActionsW(w);
        Rect R(double x, double y, double ww, double hh) => new(_barCard.X + U(x), _barCard.Y + U(y), U(ww), U(hh));
        double mid = rows == 2 ? 34 : h / 2; // due righe: brano sulla prima
        _barArt = R(compact ? 16 : 24, compact ? 9 : mid - 22, 44, 44);
        double songW = compact ? w - 16 : BarSongW(w);
        _barTitle = R(compact ? 76 : 84, compact ? 8 : mid - 22, Math.Max(40, songW - 158), 22);
        _barArtist = R(compact ? 76 : 84, compact ? 31 : mid + 1, Math.Max(40, songW - 158), 22);
        // Stella e "..." subito dopo il titolo (come Apple Music), non in fondo alla colonna del brano.
        double textMax = Math.Max(40, songW - 158), textW = textMax;
        if (_queueIndex >= 0 && _queueIndex < _queue.Count)
        {
            var t = _queue[_queueIndex];
            LibraryStore.QueueMusicArtwork(t);
            string artistText = UnknownTag(t.ArtistName) ? (UnknownTag(t.AlbumTitle) ? "" : t.AlbumTitle!) : t.ArtistName!;
            textW = Math.Min(textMax, Math.Max(Measure(TrackTitle(t), 10 * BarScale, DisplaySemibold), Measure(artistText, 9 * BarScale, DisplayRegular)) / BarScale + 2);
        }
        double favX = compact ? songW - 60 : 84 + textW + 14;
        _barTargets["favorite"] = R(favX, compact ? 16 : mid - 15, 28, 30);
        _barTargets["more"] = R(favX + 32, compact ? 16 : mid - 15, 28, 30);
        double actionsX = compact ? Math.Max(12, (w - actionsW) / 2) : w - 24 - actionsW;
        double half = Math.Min(w / 2 - (songW + 64), actionsX - 70 - w / 2);
        double seekLeft = narrow ? 70 : w / 2 - half;
        double seekRight = narrow ? w - 70 : w / 2 + half;
        // Comandi al centro dell'isola; su due righe, se al centro toccherebbero brano o icone, nel mezzo fra i due.
        bool centerFits = w / 2 - 93 >= songW + 8 && w / 2 + 93 <= actionsX - 8;
        double transportX = compact || centerFits ? (w - 186) / 2 : (songW + actionsX) / 2 - 93;
        double transportY = compact ? 59 : narrow ? 18 : Math.Max(7, (h - 15) - 40);
        int i = 0;
        foreach (string key in new[] { "shuffle", "previous", "play", "next", "repeat" }) _barTargets[key] = R(transportX + i++ * 38, transportY, 32, 32);
        double actionsY = compact ? 97 : 20;
        i = 0;
        foreach (string key in MusicActionKeys) _barTargets[key] = R(actionsX + i++ * gapA, actionsY, 26, 28);
        _volumeCollapsed = VolumeCollapsed(w);
        double muteX = actionsX + (MusicActionKeys.Length - 1) * gapA;
        _barTargets["volume"] = _volumeCollapsed ? R(muteX - 12 - Math.Max(110, volumeW), actionsY + 2, Math.Max(110, volumeW), 24) : R(muteX + 26 + 14, actionsY + 2, volumeW, 24);
        double seekY = narrow ? h - 17 : h - 15;
        _barTargets["seek"] = R(seekLeft, seekY - 10, Math.Max(20, seekRight - seekLeft), 20);
        // Tempi alla stessa distanza dai due capi della timeline e sullo stesso centro verticale.
        var seek = _barTargets["seek"];
        double timeH = U(20), timeW = U(54), timeGap = U(10), center = seek.Y + seek.Height / 2;
        _barElapsed = new Rect(seek.X - timeGap - timeW, center - timeH / 2, timeW, timeH);
        _barEnd = new Rect(seek.Right + timeGap, center - timeH / 2, timeW, timeH);
    }

    /// <summary>
    /// Mini player compatto: copertina grande in alto, sotto titolo e artista (stella e "..." a destra), timeline con i
    /// tempi sotto, i cinque comandi e la fila di icone. Niente fasce vuote, niente titolo ripetuto.
    /// </summary>
    private void LayoutMiniPlayer(Rect page)
    {
        _barCard = page;
        double pad = 16, w = page.Width, h = page.Height;
        // Copertina centrata, non a tutta larghezza (scelta dell'utente: "e' piu' figa"), con aria sopra e ai lati.
        double top = 30, below = 18 + 46 + 10 + 20 + 18 + 44 + 8 + 30 + pad;
        double side = Math.Max(80, Math.Min(Math.Round(w * .64), h - top - below));
        _barArt = new Rect(Math.Round((w - side) / 2), top, side, side);
        double y = _barArt.Bottom + 18;
        _barTitle = new Rect(pad, y, w - pad * 2 - 72, 24);
        _barArtist = new Rect(pad, y + 24, w - pad * 2 - 72, 20);
        _barTargets["favorite"] = new Rect(w - pad - 64, y + 7, 30, 30);
        _barTargets["more"] = new Rect(w - pad - 30, y + 7, 30, 30);
        y += 46 + 10;
        _barTargets["seek"] = new Rect(pad, y, w - pad * 2, 20);
        _barElapsed = new Rect(pad, y + 18, 70, 16);
        _barEnd = new Rect(w - pad - 70, y + 18, 70, 16);
        y += 20 + 18;
        double step = Math.Min(54, (w - pad * 2) / 5);
        int i = 0;
        foreach (string key in new[] { "shuffle", "previous", "play", "next", "repeat" })
            _barTargets[key] = new Rect(w / 2 + (i++ - 2) * step - 20, y, 40, 40);
        y += 44 + 8;
        var keys = MusicActionKeys.Where(k => k != "nowplaying").ToArray();
        double gap = (w - pad * 2) / keys.Length;
        i = 0;
        foreach (string key in keys) _barTargets[key] = new Rect(pad + i++ * gap + (gap - 26) / 2, y, 26, 28);
        _volumeCollapsed = true;
        var mute = _barTargets["mute"];
        _barTargets["volume"] = new Rect(Math.Max(pad, mute.X - 12 - 130), y + 2, 130, 24);
    }

    private bool _volumeCollapsed, _volumeOpen;
    private long _volumeOpenAt = -1;
    /// <summary>0 = solo l'icona, 1 = cursore aperto (sempre 1 se il volume non e' a scomparsa).</summary>
    private double VolumeExpand
    {
        get
        {
            if (!_volumeCollapsed) return 1;
            double t = _volumeOpenAt < 0 ? 1 : Math.Clamp((NowMs - _volumeOpenAt) / 160.0, 0, 1);
            double e = 1 - Math.Pow(1 - t, 3);
            return _volumeOpen ? e : 1 - e;
        }
    }
    private bool VolumeAnimating => _volumeCollapsed && _volumeOpenAt >= 0 && NowMs - _volumeOpenAt < 180;

    private void UpdateVolumeHover(Point p)
    {
        if (!_volumeCollapsed || !_barTargets.TryGetValue("volume", out var vol) || !_barTargets.TryGetValue("mute", out var mute)) return;
        var zone = (_volumeOpen ? vol.Union(mute) : mute).Inflate(new Thickness(8, 10));
        bool open = zone.Contains(p) || _barDrag == "volume";
        if (open == _volumeOpen) return;
        double now = VolumeExpand;
        _volumeOpen = open;
        // Riparte dal punto in cui era (niente salti se il puntatore entra ed esce in fretta).
        _volumeOpenAt = NowMs - (long)(160 * (open ? 1 - Math.Cbrt(1 - now) : 1 - Math.Cbrt(now)));
        StartBarAnimation();
    }

    private static readonly string[] MusicActionKeys = { "queue", "analysis", "lyrics", "nowplaying", "library", "pip", "stop", "mute" };

    private PaintLayer? _barLayer;

    /// <summary>La barra sta nel suo livello (vedi <see cref="PaintLayer"/>): niente sopra di lei nella libreria.</summary>
    private bool BarLayered(Rect page) =>
        _barLayer != null && MusicBarVisible && !_miniPlayer && QueueDrawerRect(page) == null && _menu == null
        && _signIn == null && _playlistCreate == null && _photoList == null;

    /// <summary>Ridisegna solo la barra quando sta nel suo livello e nient'altro nella pagina dipende dal brano.</summary>
    private void InvalidateBar()
    {
        if (_barLayer != null && !MusicFullscreenOpen && BarLayered(new Rect(Bounds.Size))) _barLayer.InvalidateVisual();
        else InvalidateVisual();
    }

    /// <summary>Ogni ridisegno della libreria ridisegna anche il livello della barra (stato condiviso).</summary>
    public new void InvalidateVisual()
    {
        base.InvalidateVisual();
        _barLayer?.InvalidateVisual();
        _spotLayer?.InvalidateVisual();
    }

    private void RenderBarLayer(DrawingContext ctx)
    {
        var page = new Rect(Bounds.Size);
        QaBarLayerRenders++;
        if (page.Width < 10 || page.Height < 10 || !BarLayered(page)) return;
        // Stessa scala della libreria (RenderCore la calcola prima di disegnare).
        _s = Math.Max(.45, Math.Min(LayoutScale(page.Size), page.Height / 1000));
        DrawMusicBar(ctx, page, paint: true, hits: false);
    }

    /// <param name="paint">disegna la barra (falso: la disegna il livello).</param>
    /// <param name="hits">registra le zone cliccabili (solo dal disegno della libreria).</param>
    private void DrawMusicBar(DrawingContext ctx, Rect page, bool paint = true, bool hits = true)
    {
        if (_glassCapture) return;
        if (!MusicBarVisible) { HideNativeGlass(); return; }
        var track = _queue[_queueIndex];
        if (!ReferenceEquals(track, _barLastTrack)) { _barLastTrack = track; _barLastDuration = 0; }
        double duration = _audio.Duration;
        // La timeline aspetta la durata vera invece di mostrare 0:00 / 0:00 e poi saltare.
        if (_barLastDuration <= 0 && duration > 0) { _barSeekRevealStart = NowMs; StartBarAnimation(); }
        _barLastDuration = duration;
        LayoutMusicBar(page);
        double reveal = BarReveal;
        var bar = new Rect(0, page.Bottom - MusicBarH, page.Width, MusicBarH);
        if (hits)
        {
            // La barra resta cliccabile anche sopra le schede.
            _hitFloor = Math.Min(_hitFloor, _hits.Count);
            _hits.Add(new Hit(bar, "music-bar"));
            if (reveal >= 1)
            {
                // Il cursore del volume a scomparsa sta sopra le icone vicine (stop, mini player...): conta solo quando e' aperto.
                foreach (var (key, r) in _barTargets)
                    if (key != "volume" || !_volumeCollapsed || VolumeExpand >= .5) _hits.Add(new Hit(r, "music-" + key));
                if (!_miniPlayer) _hits.Add(new Hit(_barArt, "music-art"));
            }
        }
        if (reveal <= 0 || !paint) return;
        if (_overlayAboveBar) ctx.FillRectangle(B(Back), bar);
        bool native = SyncNativeGlass(track, reveal, page);
        // Dissolvenza quasi sul posto (come le schede): solo un accenno di movimento.
        using var opacity = ctx.PushOpacity(reveal);
        using var shift = ctx.PushTransform(Matrix.CreateTranslation(0, Math.Round((1 - reveal) * U(12))));
        if (native) { if (!_miniPlayer) DrawBarShadow(ctx); }
        else
        {
            DrawBarSurface(ctx);
            DrawBarContent(ctx, track);
        }
    }

    /// <summary>Ombra morbida sotto l'isola: solo nel tema scuro, e appena accennata.</summary>
    private void DrawBarShadow(DrawingContext ctx)
    {
        double radius = CardRadius;
        if (UiTheme.IsLight)
        {
            // Tema chiaro: sul fondo uniforme il vetro non ha nulla da piegare; un'ombra morbida lo stacca dalla pagina.
            for (int i = 10; i >= 1; i--)
                ctx.DrawRectangle(B(Color.FromArgb((byte)(3 + (10 - i) / 3), 20, 30, 45)), null,
                    _barCard.Translate(new Vector(0, U(3))).Inflate(i), radius + i, radius + i);
            return;
        }
        for (int i = 2; i >= 1; i--)
            ctx.DrawRectangle(null, new Pen(B(Color.FromArgb(16, 0, 0, 0)), 1), _barCard.Inflate(i - .5), radius + i, radius + i);
    }

    /// <summary>Vetro disegnato (senza il vetro di macOS): ombra, la pagina sopra specchiata e sfocata, filo del bordo.</summary>
    /// <summary>
    /// Luce sui bordi come il Liquid Glass di Apple: un filo chiaro che si accende in alto a sinistra e, piu' debole, in
    /// basso a destra (la luce che entra e quella riflessa), quasi spento sui lati. Sopra il vetro vero e su quello disegnato.
    /// </summary>
    internal static void DrawGlassRim(DrawingContext ctx, Rect r, double radius, bool light, double scale = 1)
    {
        byte a1 = (byte)(light ? 150 : 120), a2 = (byte)(light ? 70 : 55);
        var rim = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(a1, 255, 255, 255), 0), new GradientStop(Color.FromArgb(18, 255, 255, 255), .32),
                new GradientStop(Color.FromArgb(10, 255, 255, 255), .68), new GradientStop(Color.FromArgb(a2, 255, 255, 255), 1),
            },
        };
        ctx.DrawRectangle(null, new Pen(rim, 1.2 / scale), r.Deflate(.6 / scale), radius, radius);
        // Riflesso morbido lungo il bordo alto, appena dentro.
        var sheen = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb((byte)(light ? 40 : 26), 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
        };
        ctx.DrawRectangle(sheen, null, new Rect(r.X, r.Y, r.Width, Math.Min(r.Height * .45, 26)), radius, radius);
    }

    private void DrawBarSurface(DrawingContext ctx)
    {
        bool dark = !UiTheme.IsLight;
        double radius = CardRadius;
        if (dark)
            for (int i = 2; i >= 1; i--)
                ctx.DrawRectangle(B(Color.FromArgb(16, 0, 0, 0)), null, _barCard.Inflate(i), radius + i, radius + i);
        var shape = new RectangleGeometry(_barCard, radius, radius);
        using (ctx.PushGeometryClip(shape))
        {
            if (_barGlass is { } glass && _barGlassCard.Size == _barCard.Size)
            {
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    ctx.DrawImage(glass, new Rect(glass.Size), _barCard);
            }
            else ctx.FillRectangle(B(dark ? Color.FromRgb(24, 27, 33) : Color.FromRgb(246, 247, 249)), _barCard);
        }
        DrawGlassRim(ctx, _barCard, radius, !dark);
        if (_barGlass == null || NowMs - _barGlassAt > 1500) ScheduleBarGlass();
    }

    private Color BarInk => UiTheme.IsLight ? Color.FromRgb(16, 20, 26) : Colors.White;

    private void DrawBarContent(DrawingContext ctx, LibraryItem track)
    {
        // Copertina con angoli appena arrotondati, come nella coda.
        using (ctx.PushGeometryClip(new RectangleGeometry(_barArt, _miniPlayer ? 10 : U(2), _miniPlayer ? 10 : U(2))))
        {
            if (Image(track.ArtPath, _barArt.Width) is { } bmp) DrawCover(ctx, bmp, _barArt);
            else DrawMusicPlaceholder(ctx, _barArt);
        }
        string artist = UnknownTag(track.ArtistName) ? (UnknownTag(track.AlbumTitle) ? "" : track.AlbumTitle!) : track.ArtistName!;
        TextAt(ctx, TrackTitle(track), _miniPlayer ? 14.5 : 10 * BarScale, DisplaySemibold, UiTheme.Text, _barTitle);
        TextAt(ctx, artist, _miniPlayer ? 12.5 : 9 * BarScale, DisplayRegular, UiTheme.Muted, _barArtist);

        double position = _audio.Position, duration = _audio.Duration;
        // Dopo un clic sulla timeline AVPlayer riporta ancora per un attimo la posizione vecchia.
        if (_seekHoldUntil > 0)
        {
            if (Math.Abs(position - _seekHoldTarget) < 1.5 || NowMs > _seekHoldUntil) _seekHoldUntil = 0;
            else position = _seekHoldTarget;
        }
        double seekReveal = BarSeekReveal;
        bool preview = SeekPreviewActive(duration);
        double previewValue = _barDrag == "seek" ? _barDragValue : _barHoverSeek;
        if (seekReveal > 0)
        {
            Color time = A(UiTheme.Muted, 255 * seekReveal);
            // Mini player: tempi sotto i due capi della timeline (a sinistra e a destra), non ai lati.
            TextAt(ctx, Clock(preview ? previewValue * duration : position), 9 * BarScale, DisplayRegular, preview ? A(UiTheme.Accent, 255 * seekReveal) : time, _barElapsed, _miniPlayer ? TextAlignment.Left : TextAlignment.Right);
            TextAt(ctx, Clock(duration), 9 * BarScale, DisplayRegular, time, _barEnd, _miniPlayer ? TextAlignment.Right : TextAlignment.Left);
        }

        Color ink = BarInk;
        double volumeExpand = VolumeExpand;
        _barTargets.TryGetValue("volume", out var volumeRect);
        foreach (var (key, bounds) in _barTargets)
        {
            // Volume a scomparsa: le icone sotto il cursore sfumano mentre si apre.
            double fade = _volumeCollapsed && key != "volume" && key != "mute" && bounds.Intersects(volumeRect) ? 1 - volumeExpand : 1;
            if (key == "volume" && volumeExpand <= 0.01) continue;
            if (fade <= 0.01) continue;
            using var keyFade = ctx.PushOpacity(key == "volume" ? volumeExpand : fade);
            if (key is "seek" or "volume")
            {
                double y = Math.Round(bounds.Y + bounds.Height / 2);
                if (key == "seek" && seekReveal < 1)
                {
                    // Prima della durata solo il binario; poi il riempimento cresce fino alla posizione.
                    var railPen = new Pen(B(Color.FromArgb(60, 153, 181, 205)), U(2));
                    ctx.DrawLine(railPen, new Point(bounds.X, y), new Point(bounds.Right, y));
                    if (seekReveal <= 0) continue;
                    double target = duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0;
                    ctx.DrawLine(new Pen(B(A(UiTheme.Accent, 235 * seekReveal)), U(2)), new Point(bounds.X, y), new Point(bounds.X + bounds.Width * target * seekReveal, y));
                    continue;
                }
                double value = _barDrag == key ? _barDragValue : key == "seek" ? (duration > 0 ? position / duration : 0) : _audio.Muted ? 0 : _audio.Volume;
                double x = bounds.X + bounds.Width * Math.Clamp(value, 0, 1);
                // Binario sottile e arrotondato; la manopola solo sotto il puntatore o durante il trascinamento.
                bool active = _barHover == key || _barDrag == key;
                double thick = Math.Max(3, U(active ? 5 : 4));
                ctx.DrawLine(new Pen(B(A(ink, UiTheme.IsLight ? 52 : 58)), thick, lineCap: PenLineCap.Round), new Point(bounds.X, y), new Point(bounds.Right, y));
                if (key == "seek" && _barDrag == "" && preview)
                {
                    double hx = bounds.X + bounds.Width * Math.Clamp(_barHoverSeek, 0, 1);
                    if (Math.Abs(hx - x) > .5)
                        ctx.DrawLine(new Pen(B(A(ink, UiTheme.IsLight ? 70 : 90)), thick, lineCap: PenLineCap.Round), new Point(Math.Min(x, hx), y), new Point(Math.Max(x, hx), y));
                }
                if (x > bounds.X)
                    ctx.DrawLine(new Pen(B(active ? UiTheme.Accent : A(ink, UiTheme.IsLight ? 235 : 245)), thick, lineCap: PenLineCap.Round), new Point(bounds.X, y), new Point(x, y));
                if (active)
                {
                    double r = U(_barDrag == key ? 7.5 : 6.5);
                    ctx.DrawEllipse(B(Color.FromArgb((byte)(UiTheme.IsLight ? 40 : 90), 0, 0, 0)), null, new Point(x, y + U(1)), r, r);
                    ctx.DrawEllipse(Brushes.White, UiTheme.IsLight ? new Pen(B(Color.FromArgb(60, 0, 0, 0)), 1) : null, new Point(x, y), r, r);
                }
                continue;
            }
            bool favorite = key == "favorite" && Favorites.Contains(_queue[_queueIndex].Path);
            string icon = key switch
            {
                "play" => _audio.IsPlaying ? "pause" : "play", "mute" => _audio.Muted ? "mute" : "volume", "analysis" => "wave",
                "favorite" => "star", _ => key,
            };
            // La libreria e' lo stato di riposo: niente evidenziazione.
            bool selected = key switch
            {
                "shuffle" => _shuffle, "repeat" => _repeat, "favorite" => favorite, "queue" => _queueOpen,
                "analysis" => _musicView == 5, "lyrics" => _musicView == 4, "nowplaying" => _musicView == 3, "pip" => _miniPlayer, _ => false,
            };
            bool hot = _barHover == key;
            Color color = selected ? UiTheme.Accent : A(ink, hot ? 255 : 215);
            double glyph = U(20);
            // Pulsante principale: solo il simbolo, piu' grande, senza cerchio attorno.
            if (key == "play") { color = A(ink, hot ? 255 : 235); glyph = U(26); }
            else if (hot && !selected) color = UiTheme.Accent;
            WinSymbol.Draw(ctx, new Rect(bounds.X + (bounds.Width - glyph) / 2, bounds.Y + (bounds.Height - glyph) / 2, glyph, glyph), icon, color);
        }
    }

    private static string Clock(double s) =>
        TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss", System.Globalization.CultureInfo.InvariantCulture);

    private bool SeekPreviewActive(double duration) => duration > 0 && (_barDrag == "seek" || (_barHover == "seek" && _barDrag == "" && _barHoverSeek >= 0));

    private static double ValueAt(double x, Rect r) => Math.Clamp((x - r.X) / Math.Max(1, r.Width), 0, 1);

    // ------------------------------------------------------------------ mouse
    /// <summary>Movimento sopra la barra: evidenziazione del tasto, anteprima del punto della timeline, trascinamento.</summary>
    private bool MusicBarPointerMoved(Point p)
    {
        if (!MusicBarVisible) { _barHover = ""; return false; }
        if (_barDrag != "" && _barTargets.TryGetValue(_barDrag, out var dragged))
        {
            _barDragValue = ValueAt(p.X, dragged);
            if (_barDrag == "volume") SetMusicVolume(_barDragValue);
            InvalidateVisual();
            return true;
        }
        UpdateVolumeHover(p);
        string key = BarReveal >= 1 ? BarTargetAt(p) : "";
        _barHoverSeek = key == "seek" ? ValueAt(p.X, _barTargets["seek"]) : -1;
        _barHover = key;
        return false;
    }

    /// <summary>Comando sotto il puntatore, tenendo conto del volume a scomparsa.</summary>
    private string BarTargetAt(Point p)
    {
        bool open = VolumeExpand > .5;
        _barTargets.TryGetValue("volume", out var vol);
        foreach (var (k, r) in _barTargets)
        {
            if (!r.Contains(p)) continue;
            if (k == "volume" && !open) continue;
            if (_volumeCollapsed && open && k != "volume" && k != "mute" && r.Intersects(vol)) continue;
            return k;
        }
        return "";
    }

    private bool MusicBarPointerPressed(Point p, IPointer pointer)
    {
        if (!MusicBarVisible || BarReveal < 1) return false;
        foreach (string key in new[] { "seek", "volume" })
        {
            if (key == "volume" && VolumeExpand < .5) continue;
            if (!_barTargets.TryGetValue(key, out var r) || !r.Inflate(new Thickness(0, U(4))).Contains(p)) continue;
            if (key == "seek" && _audio.Duration <= 0) return true;
            _barDrag = key; _barDragValue = ValueAt(p.X, r);
            pointer.Capture(this);
            if (key == "volume") SetMusicVolume(_barDragValue);
            InvalidateVisual();
            return true;
        }
        return false;
    }

    private bool MusicBarPointerReleased(Point p, IPointer pointer)
    {
        if (_barDrag == "") return false;
        string key = _barDrag;
        _barDrag = "";
        pointer.Capture(null);
        if (_barTargets.TryGetValue(key, out var r))
        {
            double value = ValueAt(p.X, r);
            if (key == "seek")
            {
                _seekHoldTarget = value * _audio.Duration; _seekHoldUntil = NowMs + 1500;
                _audio.Seek(_seekHoldTarget);
            }
            else SetMusicVolume(value);
        }
        InvalidateVisual();
        return true;
    }

    private void SetMusicVolume(double value)
    {
        _audio.Volume = (float)value;
        if (value > 0 && _audio.Muted) _audio.Muted = false;
        MacSettings.Set("musicVolume", Math.Round(value, 3).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ comandi (PlayerForm.MusicWorkspace di Windows)
    public event Action? FullscreenToggleRequested;
    public event Action? MiniPlayerRequested;

    private bool MusicBarCommand(string key)
    {
        if (!MusicBarVisible) return false;
        // Mini player: testi, analisi, brano, coda e libreria si aprono nel player grande. Prima la finestra torna com'era,
        // poi la vista (aperte dentro la finestrella la rovinavano e la misura di prima non tornava piu').
        if (_miniPlayer && key is "lyrics" or "analysis" or "nowplaying" or "art" or "queue" or "library" or "more")
        {
            MiniPlayerRequested?.Invoke();
            Dispatcher.UIThread.Post(() => MusicBarCommand(key), DispatcherPriority.Background);
            return true;
        }
        switch (key)
        {
            case "play": TogglePlayMusic(); break;
            case "previous": PreviousTrack(); break;
            case "next": NextTrack(); break;
            case "shuffle": ToggleShuffle(); break;
            case "repeat": _repeat = !_repeat; break;
            case "mute": _audio.Muted = !_audio.Muted; break;
            case "favorite": Favorites.Toggle(_queue[_queueIndex].Path); break;
            case "queue": _queueOpen = !_queueOpen; break;
            case "stop": StopMusicAnimated(); break;
            case "pip": MiniPlayerRequested?.Invoke(); break;
            case "library": CloseMusicFullscreen(); if (!(_view == "Collection" && _category == "Music")) Navigate("Music"); break;
            case "more": ShowMusicMoreMenu(); break;
            // Testo, grafici e brano a schermo intero vivono nella stessa vista: lo stesso tasto la chiude.
            case "art": case "nowplaying": ToggleMusicFullscreen(3); break;
            case "lyrics": ToggleMusicFullscreen(4); break;
            case "analysis": ToggleMusicFullscreen(5); break;
            default: return false;
        }
        InvalidateVisual();
        return true;
    }

    /// <summary>Prova: clic al centro di un tasto della barra, con lo stesso percorso del mouse (senza trascinamenti).</summary>
    internal string TestBarTargets => string.Join(" ", _barTargets.Select(kv => $"{kv.Key}={kv.Value.Center.X:0},{kv.Value.Center.Y:0}"));
    internal int QaBarLayerRenders;

    internal string TestClickBar(string key)
    {
        if (!_barTargets.TryGetValue(key, out var r)) return "tasto assente: " + string.Join(",", _barTargets.Keys);
        var p = r.Center;
        _mouse = p;
        CloseSearchIfOutside(p);
        if (PhotoPressed(p)) return "preso dalle foto";
        if (QueuePointerPressed(p, 1)) return "preso dalla coda";
        var hit = HitAt(p);
        if (hit == null) return "nessuna zona";
        Activate(hit);
        return "zona " + hit.Kind;
    }

    private void StopMusicAnimated()
    {
        _audio.Pause();
        CloseMusicFullscreen();
        _queueOpen = false;
        PlayBarHide(StopMusic);
    }

    /// <summary>Mix manuale fra il brano in corso e il successivo della coda.</summary>
    public event Action<LibraryItem, LibraryItem>? ManualMixRequested;
    public float MusicPlayerVolume => _audio.Muted ? 0 : _audio.Volume;
    public void ManualMixChanged(string from, string to) => _audio.ManualMixChanged(from, to);

    internal void TestPlayOpenAlbum(int index) { if (_album != null) PlayQueue(_album.Tracks.ToList(), index); }
    internal void TestManualMix() { if (MusicBarVisible && UpcomingLocalTrack() is { } next) ManualMixRequested?.Invoke(_queue[_queueIndex], next); else Console.Error.WriteLine("[qa] mix: niente brano successivo"); }
    internal (string? From, string? To) TestMusicPaths => MusicBarVisible ? (_queue[_queueIndex].Path, UpcomingLocalTrack()?.Path) : (null, null);
    /// <summary>Prova: il brano salta 6 s prima del punto d'uscita salvato.</summary>
    internal void TestMusicSeekBeforeMix()
    {
        var (from, to) = TestMusicPaths;
        if (CinecorePlayer2025.Audio.ManualMixStore.Get(from, to) is { } mix) { _audio.Seek(Math.Max(0, mix.OutAt - 6)); _audio.Play(); }
    }

    /// <summary>Il brano dopo quello in corso nella coda, se e' un file audio sul Mac.</summary>
    private LibraryItem? UpcomingLocalTrack()
    {
        if (!MusicBarVisible || _queueIndex + 1 >= _queue.Count) return null;
        var next = _queue[_queueIndex + 1];
        return !next.IsNetwork && File.Exists(next.Path) ? next : null;
    }

    /// <summary>Lista di testo sopra la barra della musica.</summary>
    private void ShowMusicMoreMenu()
    {
        if (!MusicBarVisible) return;
        var track = _queue[_queueIndex];
        var settings = CinecorePlayer2025.Audio.CinecoreAudioSettings.Current;
        bool automix = settings.CrossfadeSeconds < 0;
        var entries = new List<MenuEntry>
        {
            new(automix ? "Disattiva Automix" : "Attiva Automix", null, () =>
            {
                settings.SetCrossfade(automix ? Math.Max(0, settings.CrossfadeBeforeAutomix) : -1);
                CinecorePlayer2025.Audio.CinecoreAudioSettings.Save();
            }) { Checked = automix, Enabled = settings.Enabled && !settings.BitPerfect },
            UpcomingLocalTrack() is { } upcoming && !track.IsNetwork && File.Exists(track.Path)
                ? new("Mix manuale…", null, () => ManualMixRequested?.Invoke(track, upcoming))
                : new("Mix manuale…") { Enabled = false },
            new("Ordina la coda per il mix", "queue", OrderQueueForMixing) { Enabled = !_mixOrderRunning && _queueIndex + 2 < _queue.Count },
            AddToPlaylistMenu(new[] { track }),
            new("Gestisci coda", "queue", () => { _queueOpen = true; InvalidateVisual(); }),
            new("Continua con brani simili", "spectrum", () => MusicRadio = !MusicRadio) { Checked = MusicRadio },
            MenuEntry.Sep(),
            new("Schermo intero del brano", "maximize", () => ShowMusicFullscreen(3)),
            new("Testo a schermo intero", "lyrics", () => ShowMusicFullscreen(4)),
            LyricSyncMenuEntry(),
            new("Mini player", "pip", () => MiniPlayerRequested?.Invoke()),
            new(IsWindowFullscreen ? "Esci da schermo intero" : "Finestra a schermo intero", "maximize", () => FullscreenToggleRequested?.Invoke()),
            MenuEntry.Sep(),
            new("Interrompi riproduzione", "stop", StopMusicAnimated),
        };
        var more = _barTargets.TryGetValue("more", out var r) ? r : _barCard;
        ShowTextMenu(entries, new Point(more.Right, _barCard.Top - U(8)), above: true, showDisabled: true);
    }

    /// <summary>Altezza da lasciare libera in fondo alla finestra per la barra (0 senza musica).</summary>
    public double MusicBarReserve => MusicBarVisible ? MusicBarH : 0;
    /// <summary>Un livello a pagina intera e' aperto sopra la libreria: la striscia della barra prende il fondo.</summary>
    public bool OverlayAboveBar { get => _overlayAboveBar; set { if (_overlayAboveBar == value) return; _overlayAboveBar = value; InvalidateVisual(); } }
    private bool _overlayAboveBar;

    /// <summary>Lo imposta la finestra: serve per il testo della voce "schermo intero".</summary>
    public bool IsWindowFullscreen { get; set; }

    // ------------------------------------------------------------------ vetro
    // Vetro "vivo" (VividGlass di Windows): la pagina sopra la barra, specchiata e sfocata, un poco piu' satura e
    // piu' scura nel tema scuro (il testo bianco si legge); nel tema chiaro un vetro latte con appena i colori sotto.
    private bool _glassCapture, _barGlassQueued;
    private WriteableBitmap? _barGlass;
    private Rect _barGlassCard;
    private long _barGlassAt;

    private void ScheduleBarGlass()
    {
        if (_barGlassQueued) return;
        _barGlassQueued = true;
        // Dopo il disegno in corso: rifare la pagina dentro Render non si puo'.
        DispatcherTimer.RunOnce(() => { _barGlassQueued = false; RebuildBarGlass(); }, TimeSpan.FromMilliseconds(_barGlass == null ? 30 : 220));
    }

    private void RebuildBarGlass()
    {
        long qaStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try { RebuildBarGlassCore(); }
        finally { if (QaFrames) Console.Error.WriteLine($"[qa-glass] vetro della barra {System.Diagnostics.Stopwatch.GetElapsedTime(qaStart).TotalMilliseconds:0.0} ms album={_album != null}"); }
    }

    private void RebuildBarGlassCore()
    {
        if (!MusicBarVisible || _barCard.Width < 8 || Bounds.Width < 10) return;
        _barGlassAt = NowMs;
        const double k = 0.1;
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(Bounds.Width * k)), Math.Max(1, (int)Math.Ceiling(Bounds.Height * k)));
        byte[] data;
        var savedHits = _hits.ToList(); int savedFloor = _hitFloor; var savedMouse = _mouse; int savedPending = _pendingImages;
        try
        {
            _glassCapture = true;
            _mouse = new Point(-10000, -10000);
            // Non RenderTargetBitmap.Render: applica i DPI due volte (vedi SurfaceCrossfade).
            using var capture = new RenderTargetBitmap(size, new Vector(96, 96));
            using (var dc = capture.CreateDrawingContext())
            using (dc.PushTransform(Matrix.CreateScale(k, k)))
                Render(dc);
            using var converted = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var pixels = converted.Lock())
            {
                capture.CopyPixels(pixels, AlphaFormat.Premul);
                data = new byte[size.Width * size.Height * 4];
                for (int y = 0; y < size.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(pixels.Address, y * pixels.RowBytes), data, y * size.Width * 4, size.Width * 4);
            }
        }
        catch { return; }
        finally
        {
            _glassCapture = false;
            _hits.Clear(); _hits.AddRange(savedHits); _hitFloor = savedFloor; _mouse = savedMouse; _pendingImages = savedPending;
        }

        int sw = Math.Max(4, (int)Math.Round(_barCard.Width * k)), sh = Math.Max(3, (int)Math.Round(_barCard.Height * k));
        int x0 = (int)Math.Round(_barCard.X * k), top = (int)Math.Round(_barCard.Y * k);
        // Specchio: la riga subito sopra la barra diventa la prima dietro di essa; piu' giu', righe piu' lontane.
        var rgb = new double[sw * sh * 3];
        for (int gy = 0; gy < sh; gy++)
        {
            int sy = Math.Clamp(top - 1 - gy * 3, 0, size.Height - 1);
            for (int gx = 0; gx < sw; gx++)
            {
                int sx = Math.Clamp(x0 + gx, 0, size.Width - 1);
                int o = (sy * size.Width + sx) * 4, d = (gy * sw + gx) * 3;
                rgb[d] = data[o + 2]; rgb[d + 1] = data[o + 1]; rgb[d + 2] = data[o];
            }
        }
        for (int pass = 0; pass < 2; pass++) BoxBlur(rgb, sw, sh, 2);
        bool light = UiTheme.IsLight;
        double s = light ? 1.1 : 1.3, b = light ? 0.16 : 0.74, off = (light ? .80 : .02) * 255;
        const double lr = .2126, lg = .7152, lb = .0722;
        var glass = new WriteableBitmap(new PixelSize(sw, sh), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = glass.Lock())
        {
            var row = new byte[sw * 4];
            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    int i = (y * sw + x) * 3;
                    double r = rgb[i], g = rgb[i + 1], bl = rgb[i + 2];
                    double outR = r * (lr * (1 - s) + s) * b + g * lg * (1 - s) * b + bl * lb * (1 - s) * b + off;
                    double outG = r * lr * (1 - s) * b + g * (lg * (1 - s) + s) * b + bl * lb * (1 - s) * b + off;
                    double outB = r * lr * (1 - s) * b + g * lg * (1 - s) * b + bl * (lb * (1 - s) + s) * b + off + 1.3;
                    row[x * 4] = (byte)Math.Clamp(outB, 0, 255); row[x * 4 + 1] = (byte)Math.Clamp(outG, 0, 255);
                    row[x * 4 + 2] = (byte)Math.Clamp(outR, 0, 255); row[x * 4 + 3] = 255;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), row.Length);
            }
        }
        var old = _barGlass;
        _barGlass = glass; _barGlassCard = _barCard;
        old?.Dispose();
        InvalidateVisual();
    }

    private static void BoxBlur(double[] rgb, int w, int h, int radius)
    {
        var tmp = new double[rgb.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    double sum = 0; int n = 0;
                    for (int dx = -radius; dx <= radius; dx++) { int xx = Math.Clamp(x + dx, 0, w - 1); sum += rgb[(y * w + xx) * 3 + c]; n++; }
                    tmp[(y * w + x) * 3 + c] = sum / n;
                }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    double sum = 0; int n = 0;
                    for (int dy = -radius; dy <= radius; dy++) { int yy = Math.Clamp(y + dy, 0, h - 1); sum += tmp[(yy * w + x) * 3 + c]; n++; }
                    rgb[(y * w + x) * 3 + c] = sum / n;
                }
    }

    // ------------------------------------------------------------------ vetro di macOS
    // Barra e coda su vetro vero (MacSurfaceGlass: Liquid Glass da macOS 26, prima NSVisualEffectView). Il vetro sta in
    // una finestra sopra quella dell'app: quando qualcosa di Avalonia ci passa sopra (menu, schede, impostazioni,
    // Spotlight) si torna al vetro disegnato, che sta sotto come il resto.
    private MacSurfaceGlass? _surfaceGlass;
    /// <summary>Lo imposta la finestra: vero se un suo livello (menu, impostazioni...) copre quel rettangolo.</summary>
    public Func<Rect, bool>? NativeGlassBlocked { get; set; }
    private bool _nativeGlassOn;
    public bool NativeGlassOn => _nativeGlassOn;

    private void HideNativeGlass()
    {
        _nativeGlassOn = false;
        _surfaceGlass?.Hide();
    }

    /// <summary>Controllo periodico dalla finestra: un livello che si apre o si chiude sopra la barra.</summary>
    public void RefreshNativeGlass()
    {
        if (!IsVisible || !MusicBarVisible) { if (_nativeGlassOn) HideNativeGlass(); return; }
        if (!MacSurfaceGlass.Supported) return;
        bool blocked = NativeBlocked(_barCard);
        if (blocked == _nativeGlassOn) InvalidateVisual();
    }

    // I menu della barra in alto (Altro, Fonte, ingranaggio...) non toccano la barra della musica: il vetro resta (prima
    // si toglieva e si rimetteva a ogni menu e la barra lampeggiava alla chiusura della vignettatura).
    private bool NativeBlocked(Rect card) =>
        _signIn != null || _playlistCreate != null || PhotoOpen || _pageTransition.Capturing ||
        (_menu != null && (_queueOpen || _menuAnchor.Y + _menu.Count * MenuRowH > _barCard.Top)) ||
        (NativeGlassBlocked?.Invoke(card) ?? false);

    private bool SyncNativeGlass(LibraryItem track, double reveal, Rect page)
    {
        if (!MacSurfaceGlass.Supported || TopLevel.GetTopLevel(this) is not { } top) return _nativeGlassOn = false;
        var origin = this.TranslatePoint(default, top) ?? default;
        double shift = Math.Round((1 - reveal) * U(12));
        var card = new Rect(_barCard.X + origin.X, _barCard.Y + origin.Y + shift, _barCard.Width, _barCard.Height);
        if (NativeBlocked(card)) { HideNativeGlass(); return false; }
        _surfaceGlass ??= new MacSurfaceGlass(top);
        var panels = new List<(Rect, double)> { (card, CardRadius) };
        var queue = QueueDrawerRect(page);
        if (queue is { } q) panels.Add((q.Translate(new Vector(origin.X, origin.Y)), Math.Round(13 * QueueScale(page))));
        bool dark = !UiTheme.IsLight;
        if (!_surfaceGlass.Show(panels, dark, reveal)) return _nativeGlassOn = false;
        _nativeGlassOn = true;
        double scaling = top.RenderScaling;
        double duration = _audio.Duration, position = _audio.Position;
        var seek = _barTargets.TryGetValue("seek", out var sr) ? sr : default;
        int pixel = duration > 0 ? (int)(seek.Width * scaling * Math.Clamp(position / duration, 0, 1)) : -1;
        string key = string.Join("|", track.Path, _audio.IsPlaying, pixel, (int)position, (int)duration, _barHover, _barDrag,
            Math.Round(_barDragValue, 3), Math.Round(_barHoverSeek, 3), Math.Round(VolumeExpand, 2), Math.Round(_audio.Volume, 3), _audio.Muted, _shuffle, _repeat,
            Favorites.Contains(track.Path), _musicView, _miniPlayer, _queueOpen, Math.Round(BarSeekReveal, 2), _barCard.Size, dark, UiTheme.Accent,
            Image(track.ArtPath, _barArt.Width) != null, scaling);
        var size = new PixelSize((int)Math.Ceiling(_barCard.Width * scaling), (int)Math.Ceiling(_barCard.Height * scaling));
        _surfaceGlass.SetForeground(0, key, size, a =>
        {
            using (a.Context.PushTransform(Matrix.CreateTranslation(-_barCard.X, -_barCard.Y) * Matrix.CreateScale(scaling, scaling)))
            {
                DrawGlassRim(a.Context, _barCard, CardRadius, !dark, scaling);
                DrawBarContent(a.Context, track);
            }
        });
        return true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Libreria nascosta (film, foto a schermo intero...): il vetro, che sta in un'altra finestra, va via con lei.
        if (change.Property == IsVisibleProperty && !IsVisible) { HideNativeGlass(); HideSearchSurface(); }
    }
}
