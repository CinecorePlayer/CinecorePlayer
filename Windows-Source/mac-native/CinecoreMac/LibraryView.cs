using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Libreria (CinematicMediaLibraryPage di Windows), prima parte: barra in alto (Shell.TopBar.cs), Home con il
/// banner a rotazione e "Aggiunti di recente" (Shell.cs), griglie di Film e Serie TV, pagine vuote, menu della barra.
/// Misure di Windows: scala = min(larghezza/1688, altezza/1080); caratteri Inter Display come AppFonts.Display*.
/// </summary>
internal sealed partial class LibraryView : Control
{
    /// <summary>Un titolo da riprodurre e, se va ripreso, da dove (secondi).</summary>
    public event Action<LibraryItem, double?>? PlayRequested;
    public event Action? SettingsRequested;
    public event Action? PairingRequested;
    /// <summary>Tasto destro fuori dalle locandine: il menu del player.</summary>
    public event Action<Point>? MainMenuRequested;
    public Func<Task<string?>>? PickFolder;

    private static readonly FontFamily Display = new(AppAssets.Root + "Assets/Fonts#Inter Display");
    private static readonly Typeface DisplayRegular = new(Display, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface DisplayMedium = new(Display, FontStyle.Normal, FontWeight.Medium);
    private static readonly Typeface DisplaySemibold = new(Display, FontStyle.Normal, FontWeight.SemiBold);

    private sealed record Hit(Rect Bounds, string Kind, string Key = "", LibraryItem? Item = null);
    /// <summary>Altezza della barra in alto (Home, Film...) e se un punto cade su un suo controllo (trascinamento della finestra).</summary>
    internal double TopBarHeight => BarH;
    internal bool HasHitAt(Point p) => _hits.Exists(h => h.Bounds.Contains(p));
    private readonly List<Hit> _hits = new();
    // Prima zona cliccabile valida: con una scheda aperta solo le sue (quelle della pagina sotto non contano).
    private int _hitFloor;
    private bool SheetOpen => _detail != null || _seriesName != null || _foldersOpen || _signIn != null || PhotoOpen || _album != null || _playlistOpen != null || _playlistCreate != null;
    private Hit? HitAt(Point p)
    {
        for (int i = _hits.Count - 1; i >= Math.Min(_hitFloor, _hits.Count); i--)
            if (_hits[i].Bounds.Contains(p)) return _hits[i];
        return null;
    }
    private Point _mouse = new(-1000, -1000);

    private string _view = "Home";       // Home | Collection
    private string _category = "Movies"; // Movies | TV Series | Music | Videos | Photos | Favourites | WatchHistory | Playlists
    private List<LibraryItem> _items = new();
    private double _scroll, _scrollTarget, _scrollMax;
    private readonly DispatcherTimer _frames;
    private readonly DispatcherTimer _heroTimer;
    private readonly SurfaceCrossfade _pageTransition;

    public LibraryView()
    {
        _pageTransition = new SurfaceCrossfade(this);
        Focusable = true;
        ClipToBounds = true;
        // Livelli figli (PaintLayer): prima le barrette del brano in corso, sopra la barra della musica.
        _spotLayer = new PaintLayer(RenderPlayingSpots);
        VisualChildren.Add(_spotLayer);
        _barLayer = new PaintLayer(RenderBarLayer);
        VisualChildren.Add(_barLayer);
        _frames = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => StepFrames());
        _heroTimer = new DispatcherTimer(TimeSpan.FromSeconds(45), DispatcherPriority.Background, (_, _) => { if (_view == "Home" && _menu == null) NextHero(); });
        _heroTimer.Start();
        MacNetwork.Restored += OnNetworkRestored;
        LibraryStore.Changed += () =>
        {
            if (MusicFullscreenOpen && MusicBarVisible && _queue[_queueIndex].ArtPath is { } art && art != _fsBackgroundFor)
                RequestMusicBackground();
            InvalidateVisual();
        };
        MacAlbumInfo.Changed += InvalidateVisual;
        MacAlbumInfo.TrackAlbumChanged += RefreshTrackAlbums;
        LibraryStore.RemoteFilesMissing += Reload;
        NetworkStore.ArtRejected += url =>
        {
            foreach (string key in Images.Keys.Where(k => k.StartsWith(url + "|", StringComparison.Ordinal)).ToList())
                if (Images.TryRemove(key, out var gone)) { ImageUse.Remove(key); ImageRetry[key] = long.MaxValue; }
            InvalidateVisual();
        };
        UiTheme.Changed += () => { var oldGlass = _barGlass; _barGlass = null; oldGlass?.Dispose(); InvalidateVisual(); };
        Reload();
        // All'apertura le cartelle si rileggono in sottofondo: file nuovi o tolti compaiono da soli.
        _ = Task.WhenAll(LibraryStore.FolderCategories.Where(c => LibraryStore.GetRoots(c).Count > 0)
            .Select(c => ScanCategoryAsync(c)));
    }

    /// <summary>Rilegge l'indice della categoria aperta (dopo una scansione o all'apertura).</summary>
    public void Reload()
    {
        string cat = _category is "Movies" or "TV Series" or "Videos" or "Photos" or "Music" ? _category : "Movies";
        bool favourites = _view == "Collection" && _category == "Favourites";
        if (_view == "Collection" && _category == "WatchHistory") _items = DiaryItems();
        else if (ActiveServer is { } server)
        {
            var catalog = NetworkStore.Catalog(server.Key);
            _items = catalog?.Where(i => favourites ? Favorites.Contains(i.Path) : i.Category == cat).ToList() ?? new List<LibraryItem>();
            if (catalog == null && !_networkLoading && _networkStatus.Length == 0) _ = ConnectAsync(server);
            // Catalogo dal disco: subito a schermo, il server lo aggiorna in sottofondo (una volta per sessione).
            else if (catalog != null && !_networkLoading && NetworkStore.NeedsRefresh(server.Key)) _ = ConnectAsync(server, quiet: true);
        }
        else _items = favourites ? LibraryStore.Items("Movies").Concat(LibraryStore.Items("TV Series")).Where(i => Favorites.Contains(i.Path)).ToList() : LibraryStore.Items(cat);
        if (_heroDeck.Count == 0 || _heroDeck.Any(h => !_items.Contains(h))) BuildHeroDeck();
        else TopUpHeroDeck();
        RefreshResume();
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ misure e testo
    private double _s = 1;
    private static double LayoutScale(Size size) => size.Width <= 0 || size.Height <= 0 ? 1 : Math.Clamp(Math.Min(size.Width / 1688, size.Height / 1080) * Density, .45, 4);

    /// <summary>
    /// Le misure di Windows sono in pixel di uno schermo da scrivania (~96 per pollice). Sul MacBook un punto e'
    /// piu' piccolo (~130 per pollice) e la libreria usciva minuta: si ingrandisce di quanto e' piu' fitto lo schermo.
    /// </summary>
    private static readonly double Density = ScreenDensity();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct CGSize { public double W, H; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern uint CGMainDisplayID();
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern CGSize CGDisplayScreenSize(uint id);
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern CGRect CGDisplayBounds(uint id);

    private static double ScreenDensity()
    {
        if (double.TryParse(Environment.GetEnvironmentVariable("CINECORE_LIBRARY_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture, out double forced)) return forced;
#if CINECORE_WINDOWS
        // Su Windows le unita' di Avalonia sono gia' pixel da scrivania (~96 per pollice, scala di Windows applicata):
        // le misure dell'app Windows valgono cosi' come sono.
        return 1.0;
#endif
        try
        {
            uint id = CGMainDisplayID();
            var mm = CGDisplayScreenSize(id); var pt = CGDisplayBounds(id);
            if (mm.W < 50 || pt.W < 100) return 1.25;
            return Math.Clamp(pt.W / (mm.W / 25.4) / 96, 1.0, 1.5);
        }
        catch { return 1.25; }
    }
    private double S(double v) => Math.Max(1, Math.Round(v * _s));
    private static double Px(double pt) => pt * 96 / 72 * 0.92;
    private double PadX => Math.Max(S(44), Math.Round(Bounds.Width * 0.036));
    private double BarH => Math.Max(1, Math.Round(76 * _s * HeaderScale));

    /// <summary>
    /// Dimensione della barra in alto (Home, Film, Serie TV...), Impostazioni › Generali › Interfaccia. "Automatica" la
    /// ingrandisce sugli schermi piccoli (portatili sotto ~15", larghi meno di 340 mm): su un 27" di Windows va bene cosi',
    /// su un MacBook da 13" usciva piccola.
    /// </summary>
    internal static double HeaderScale => MacSettings.Get("headerSize", "auto") switch
    {
        "normal" => 1.0, "large" => 1.15, "xlarge" => 1.3,
        _ => SmallScreen ? 1.15 : 1.0,
    };
    private static readonly bool SmallScreen = DetectSmallScreen();
    private static bool DetectSmallScreen()
    {
#if CINECORE_WINDOWS
        return false;
#else
        try { var mm = CGDisplayScreenSize(CGMainDisplayID()); return mm.W > 50 && mm.W < 340; } catch { return false; }
#endif
    }
    private static Color Text => UiTheme.Text;
    private static Color Back => UiTheme.Panel;
    private static Color A(Color c, double a) => UiTheme.WithAlpha(c, a);
    private static IBrush B(Color c) => UiText.Brush(c);

    /// <summary>
    /// Testo mai sotto ~10,5 punti dello schermo (segnalazione GitHub: rimpicciolendo la finestra la libreria si riduce
    /// tutta insieme e le scritte diventavano illeggibili). A finestra normale nessun testo e' cosi' piccolo: la soglia
    /// conta solo quando la finestra si stringe; le scritte troppo lunghe finiscono con i puntini.
    /// </summary>
    private const double MinTextPx = 10.5;

    // Impaginato una volta e riusato nei fotogrammi successivi (UiText): prima ogni scritta si reimpaginava a ogni disegno.
    private static CachedText Format(string text, double pt, Typeface face, Color color, double maxWidth = double.PositiveInfinity, int lines = 1, TextAlignment align = TextAlignment.Left)
        => UiText.Get(L10n.T(text), face, Math.Max(MinTextPx, Px(pt)), color, maxWidth, lines, align);

    private static void TextAt(DrawingContext ctx, string text, double pt, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = Format(text, pt, face, color, r.Width, 1, align);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    private static double Measure(string text, double pt, Typeface face) => Format(text, pt, face, Colors.White).WidthIncludingTrailingWhitespace;

    private static void Chevron(DrawingContext ctx, double cx, double cy, double z, Color ink, double thick, bool up = false, bool left = false, bool right = false)
    {
        Point[] pts = right
            ? new[] { new Point(cx - z / 2, cy - z), new Point(cx + z / 2, cy), new Point(cx - z / 2, cy + z) }
            : left ? new[] { new Point(cx + z / 2, cy - z), new Point(cx - z / 2, cy), new Point(cx + z / 2, cy + z) }
            : new[] { new Point(cx - z, cy - z / 2 * (up ? -1 : 1)), new Point(cx, cy + z / 2 * (up ? -1 : 1)), new Point(cx + z, cy - z / 2 * (up ? -1 : 1)) };
        ctx.DrawGeometry(null, new Pen(B(ink), thick, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), new PolylineGeometry(pts, false));
    }

    // ------------------------------------------------------------------ immagini (caricate in sottofondo, alla misura giusta)
    private static readonly ConcurrentDictionary<string, Bitmap?> Images = new();
    private static readonly ConcurrentDictionary<string, byte> Loading = new();
    private static readonly System.Threading.SemaphoreSlim ImageDecodes = new(4);
    private static readonly Dictionary<string, long> ImageUse = new();
    // Percorso -> chiave di una misura gia' pronta: prima ogni immagine in arrivo scorreva tutta la cache a ogni fotogramma.
    private static readonly Dictionary<string, string> ReadySize = new();
    private static readonly Dictionary<string, long> ImageRetry = new();
    private static long _imageBytes;

    private int _pendingImages;

    private Bitmap? Image(string? path, double widthPoints)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 2;
        int width = (int)Math.Clamp(Math.Ceiling(widthPoints * scaling / 128) * 128, 128, 3840);
        string key = path + "|" + width;
        if (Images.TryGetValue(key, out var bmp)) { ImageUse[key] = Environment.TickCount64; return bmp; }
        // Mentre la misura giusta arriva, se c'e' un'altra misura gia' pronta si usa quella.
        var near = ReadySize.TryGetValue(path, out var nearKey) && Images.TryGetValue(nearKey, out var nearBmp) ? nearBmp : null;
        bool start = (!ImageRetry.TryGetValue(key, out long retry) || Environment.TickCount64 >= retry) && Loading.TryAdd(key, 0);
        // Un'immagine ancora in arrivo tiene ferma la pagina vecchia del cambio pagina (SurfaceCrossfade).
        if (start || Loading.ContainsKey(key)) _pendingImages++;
        if (start)
            Task.Run(async () =>
            {
                Bitmap? loaded = null;
                // Immagini di un server di rete: scaricate una volta (con il token, solo in memoria) e tenute in cache.
                // Il download sta fuori dal semaforo della decodifica: prima le miniature degli episodi in arrivo dal
                // server tenevano occupati tutti i posti e sfondo e locandina della serie aspettavano dietro di loro.
                string? file = null;
                try { file = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await NetworkStore.CachedArtAsync(path) : path; } catch { }
                if (file != null)
                {
                    await ImageDecodes.WaitAsync();
                    try { using var fs = File.OpenRead(file); loaded = Bitmap.DecodeToWidth(fs, width, BitmapInterpolationMode.HighQuality); }
                    catch { }
                    finally { ImageDecodes.Release(); }
                }
                Dispatcher.UIThread.Post(() =>
                {
                    Loading.TryRemove(key, out _);
                    if (loaded != null)
                    {
                        Images[key] = loaded; ImageUse[key] = Environment.TickCount64; ReadySize[path] = key;
                        _imageBytes += (long)loaded.PixelSize.Width * loaded.PixelSize.Height * 4;
                        // Mai scartare cio' che e' a schermo (usato negli ultimi 2 s): con il limite di prima le copertine
                        // visibili venivano tolte, richieste di nuovo e scambiate con i segnaposto (lampi nella pagina).
                        long recent = Environment.TickCount64 - 2000;
                        foreach (string old in ImageUse.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToList())
                        {
                            if (Images.Count <= 384 && _imageBytes <= 512L * 1024 * 1024) break;
                            if (old == key || ImageUse[old] >= recent) continue;
                            if (Images.TryRemove(old, out var unused) && unused != null)
                            {
                                string oldPath = old[..old.LastIndexOf('|')];
                                if (ReadySize.TryGetValue(oldPath, out var kept) && kept == old) ReadySize.Remove(oldPath);
                                _imageBytes -= (long)unused.PixelSize.Width * unused.PixelSize.Height * 4;
                                unused.Dispose();
                            }
                            ImageUse.Remove(old);
                        }
                    }
                    else ImageRetry[key] = Environment.TickCount64 + 30000;
                    InvalidateVisual();
                    RaiseImageReady();
                });
            });
        return near;
    }

    /// <summary>Immagine che copre il riquadro senza deformarsi, con il punto di fuoco indicato.</summary>
    private static void DrawCover(DrawingContext ctx, Bitmap bmp, Rect dest, double fx = .5, double fy = .5)
    {
        double sw = bmp.Size.Width, sh = bmp.Size.Height;
        if (sw <= 0 || sh <= 0 || dest.Width <= 0 || dest.Height <= 0) return;
        double k = Math.Max(dest.Width / sw, dest.Height / sh);
        // La divisione può superare sw/sh di un epsilon (es. 2.84e-14): Clamp(…, 0, negativo) abortiva lo scroll.
        double cw = Math.Min(sw, dest.Width / k), ch = Math.Min(sh, dest.Height / k);
        var src = new Rect(Math.Clamp((sw - cw) * fx, 0, sw - cw), Math.Clamp((sh - ch) * fy, 0, sh - ch), cw, ch);
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            ctx.DrawImage(bmp, src, dest);
    }

    /// <summary>
    /// Copertina mancante o in arrivo: solo una superficie neutra. Prima tono casuale con il titolo dentro (ripeteva
    /// quello sotto), poi un'icona tipo Esplora risorse: all'utente non piacevano. Le copertine vere arrivano dal
    /// server o, se Jellyfin non le ha, da TMDb (LibraryStore.QueueNetworkArtwork).
    /// </summary>
    private void DrawGeneratedArt(DrawingContext ctx, Rect r, string seed, bool hero, string category)
    {
        if (r.Width <= 2 || r.Height <= 2) return;
        bool light = UiTheme.IsLight;
        Color top = light ? Color.FromRgb(226, 229, 233) : Color.FromRgb(30, 33, 39);
        Color bottom = light ? Color.FromRgb(214, 218, 223) : Color.FromRgb(21, 23, 28);
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        }, r);
        // Niente icone ne' titolo: e' solo l'attesa della copertina (server o TMDb).
    }

    // ------------------------------------------------------------------ disegno
    private static readonly bool QaFrames = Environment.GetEnvironmentVariable("CINECORE_QA_FRAMES") == "1";
    internal static readonly bool QaNoGlass = Environment.GetEnvironmentVariable("CINECORE_QA_NOGLASS") == "1";
    private int _qaFrameCount; private long _qaFrameSecond;

    public override void Render(DrawingContext ctx)
    {
        bool capture = _glassCapture || _fsPageCapture || _pageTransition.Capturing;
        // Le foto della pagina (vetro, schermo intero, cambio pagina) disegnano le barrette da se': l'elenco resta.
        if (!capture) _spots.Clear();
        if (!QaFrames) { RenderCore(ctx); if (!capture) UpdateSpotTimer(); return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderCore(ctx);
        if (!capture) UpdateSpotTimer();
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (ms > 12) Console.Error.WriteLine($"[qa-frame] libreria {ms:0.0} ms vista={_musicView} mini={_miniPlayer} {Bounds.Width:0}x{Bounds.Height:0}");
        _qaFrameCount++;
        long sec = Environment.TickCount64 / 1000;
        if (sec != _qaFrameSecond) { if (_qaFrameSecond > 0) Console.Error.WriteLine($"[qa-fps] {_qaFrameCount} vista={_musicView} scheda={_fsTab} barra={QaBarLayerRenders} barrette={_spots.Count}"); _qaFrameSecond = sec; _qaFrameCount = 0; }
    }

    private void RenderCore(DrawingContext ctx)
    {
        _hits.Clear();
        // Le foto della pagina (vetro della barra, schermo intero della musica, cambio pagina) disegnano con un mouse
        // finto: non toccano le zone del disegno vero.
        if (!_glassCapture && !_fsPageCapture && !_pageTransition.Capturing) _hoverZones.Begin(_mouse);
        _pendingImages = 0;
        var page = new Rect(Bounds.Size);
        if (page.Width < 10 || page.Height < 10) return;
        _s = LayoutScale(page.Size);
        // La Home (banner, Continua a guardare, Aggiunti di recente) e' pensata per ~1000 unita' d'altezza: l'ingrandimento
        // per lo schermo del Mac si ferma dove la pagina non ci starebbe piu' (il banner si schiacciava sotto le file).
        _s = Math.Max(.45, Math.Min(_s, page.Height / 1000));
        // Brano a schermo intero (anche nel mini player): la vista e' opaca, quindi la libreria sotto non si ridisegna
        // a ogni fotogramma (testi, VU, grafici, animazioni). Prima con una libreria grande entrare, uscire e il mini
        // player scattavano. In uscita si usa la foto della pagina presa una volta.
        if (_miniPlayer && MusicBarVisible && !_fsPageCapture)
        {
            // Mini player: sotto c'e' solo il vetro (la finestra e' trasparente). Un velo quasi invisibile tiene i clic
            // nella finestra (macOS fa passare quelli sui punti del tutto trasparenti); senza vetro nativo, il fondo pieno.
            ctx.FillRectangle(B(MacSurfaceGlass.Supported || MiniAcrylic ? Color.FromArgb(3, 0, 0, 0) : Back), page);
            _hitFloor = 0;
            goto fullscreen;
        }
        if (!_fsPageCapture && MusicFullscreenOpen && MusicBarVisible)
        {
            bool covering = _fsExitStart < 0 && (_fsEntryStart < 0 || (NowMs - _fsEntryStart) / 520.0 >= .36);
            bool cached = !covering && _fsExitStart >= 0 && _fsPageCache != null && Math.Abs(_fsPageCacheSize.Width - page.Width) < 1 && Math.Abs(_fsPageCacheSize.Height - page.Height) < 1;
            if (covering || cached)
            {
                ctx.FillRectangle(B(Back), page);
                if (cached) ctx.DrawImage(_fsPageCache!, new Rect(_fsPageCache!.Size), page);
                _hitFloor = 0;
                goto fullscreen;
            }
        }
        ctx.FillRectangle(B(Back), page);
        // Con una scheda aperta la pagina sotto non reagisce al mouse (niente evidenziazioni ne' clic che passano).
        bool sheet = SheetOpen;
        var realMouse = _mouse;
        // Il mouse sulla barra della musica non evidenzia le locandine che le passano sotto (la striscia e' una zona:
        // entrando o uscendo si ridisegna).
        bool onMusicBar = false;
        if (MusicBarVisible && !_miniPlayer && MusicBarH > 0)
        {
            var strip = new Rect(0, page.Bottom - MusicBarH, page.Width, MusicBarH);
            onMusicBar = Hover(strip);
        }
        if (sheet || onMusicBar) _mouse = new Point(-10000, -10000);
        if (_view == "YouTube") DrawYouTubePage(ctx, page);
        else if (_view == "Network") DrawNetworkPage(ctx, page);
        else if (_view == "Collection") DrawCollection(ctx, page);
        else if (_resume.Count > 0) DrawHome(ctx, page);
        else if (DrawNetworkState(ctx, new Rect(page.X + PadX, BarH, Math.Max(1, page.Width - PadX * 2), Math.Max(1, page.Height - BarH - S(20))))) { }
        else if (_items.Count == 0 && ActiveServer is { } emptyServer)
        {
            var (title, body, _, _) = NetworkEmptyTexts("Movies", emptyServer.Name);
            DrawEmptyHome(ctx, new Rect(page.X + PadX, BarH, Math.Max(1, page.Width - PadX * 2), Math.Max(1, page.Height - BarH - S(20))), title, body, "", null);
        }
        else if (_items.Count == 0) DrawEmptyHome(ctx, new Rect(page.X + PadX, BarH, Math.Max(1, page.Width - PadX * 2), Math.Max(1, page.Height - BarH - S(20))), null, null, "Aggiungi cartella", "add");
        else DrawHome(ctx, page);
        // Tutta la pagina, anche sotto la barra: sulla Home la foto del banner passa sotto il menu e li' cambiava di colpo.
        bool pageLoading = _networkLoading && ActiveServer != null;
        // Copia della pagina per il vetro della barra della musica: senza transizioni, menu e barra stessa.
        if (!_glassCapture) _pageTransition.Draw(ctx, page, !pageLoading && _pendingImages == 0, !pageLoading);
        if (_pageTransition.Capturing && !_glassCapture)
        {
            // Nella foto anche il fondo della barra (non le voci): se la pagina nuova ha la barra trasparente sopra la
            // foto (Home), il fondo vecchio sfuma invece di sparire di colpo.
            if (!BarTransparent) ctx.FillRectangle(B(Back), new Rect(0, 0, page.Width, BarH + Math.Round(TopShift)));
            _mouse = realMouse; return;
        }
        // Schermo intero: con la barra di sistema scesa dall'alto il menu scivola sotto di lei.
        using (ctx.PushTransform(Matrix.CreateTranslation(0, Math.Round(TopShift))))
            DrawTopBar(ctx, new Rect(0, 0, page.Width, BarH), page);
        _mouse = realMouse;
        _hitFloor = sheet ? _hits.Count : 0;
        DrawOnboarding(ctx, page);
        DrawDetail(ctx, page);
        DrawSeries(ctx, page);
        // Prima la playlist: un album aperto dai suoi consigli le sta sopra, e chiudendolo si torna alla playlist.
        DrawPlaylistSheet(ctx, page);
        DrawAlbumSheet(ctx, page);
        DrawFolders(ctx, page);
        if (_fsPageCapture) return;
    fullscreen:
        DrawMusicFullscreen(ctx, page);
        if (_glassCapture) return;
        DrawMusicBar(ctx, page, paint: !BarLayered(page));
        DrawQueuePanel(ctx, page);
        DrawMenu(ctx, page);
        DrawSignIn(ctx, page);
        DrawPlaylistCreate(ctx, page);
        DrawPhotoViewer(ctx, page);
        Dispatcher.UIThread.Post(() => { PlaceSignInInputs(); PlacePlaylistInputs(); PlaceSearchBox(); }, DispatcherPriority.Render);
    }

    // ----- barra in alto -----
    // La pagina di partenza aveva gia' la barra piena (Film, Serie...): resta piena durante il cambio.
    private bool _barWasOpaque;
    private void BeginPageTransition()
    {
        _barWasOpaque = !BarTransparent;
        _pageTransition.Begin();
    }
    private Rect _moreAnchor, _sourcesAnchor, _optionsAnchor;
    private double _navRight;
    public double TopShift { get => _topShift; set { if (_topShift != value) { _topShift = value; InvalidateVisual(); } } }
    private double _topShift;

    private static readonly string[] MoreKeys = { "Videos", "Photos", "Favourites", "WatchHistory", "Playlists" };
    private static string NavLabel(string key) => key switch
    {
        "Videos" => "Video", "Photos" => "Foto", "Favourites" => "Preferiti", "WatchHistory" => "Diario", "Playlists" => "Playlist", _ => key,
    };

    private bool CategoryOpen(string key) => _view == "Collection" && _category == key;

    private void DrawTopBar(DrawingContext ctx, Rect bar, Rect page)
    {
        // La barra si disegna con la sua scala (HeaderScale): tutte le misure S() dentro crescono insieme.
        double saved = _s;
        _s *= HeaderScale;
        try { DrawTopBarScaled(ctx, bar, page); }
        finally { _s = saved; }
    }

    private void DrawTopBarScaled(DrawingContext ctx, Rect bar, Rect page)
    {
        // Sulla Home la barra e' trasparente sopra il banner e si riempie man mano che la pagina scorre.
        // Il fondo pieno entra con la pagina (Reveal): prima compariva di colpo sopra la Home che sfumava (lampo nero).
        if (!BarTransparent) ctx.FillRectangle(B(A(Back, _barWasOpaque ? 255 : 255 * _pageTransition.Reveal)), bar);

        string? moreOpen = MoreKeys.FirstOrDefault(CategoryOpen);
        var sections = new List<(string Key, string Label, bool Selected)>
        {
            ("Home", "Home", _view == "Home"),
            ("Movies", "Film", CategoryOpen("Movies")),
            ("TV Series", "Serie TV", CategoryOpen("TV Series")),
            ("Music", "Musica", CategoryOpen("Music")),
            // Altro e' una sezione come le altre; la fonte e' un'altra cosa e sta in fondo (scelta dell'utente).
            ("More", YouTubeOpen ? "YouTube" : moreOpen != null ? NavLabel(moreOpen) : "Altro", moreOpen != null || YouTubeOpen),
            ("Sources", ActiveServer?.Name ?? "Mac", true),
        };
        double pt = Math.Max(9.5, 13.6 * _s);
        double chevronW = S(16);
        double gap = S(page.Width < S(1100) ? 26 : 36);
        var widths = sections.Select(x => Measure(x.Label, pt, x.Selected ? DisplaySemibold : DisplayRegular) + (x.Key is "More" or "Sources" ? chevronW : 0)).ToList();
        double navW = widths.Sum() + gap * (sections.Count - 1);
        double x = bar.X + Math.Round((bar.Width - navW) / 2);
        bool light = UiTheme.IsLight;
        for (int i = 0; i < sections.Count; i++)
        {
            var (key, label, selected) = sections[i];
            double w = widths[i];
            var hit = new Rect(x - gap / 2, bar.Y + S(10), w + gap, bar.Height - S(20));
            bool hover = (_menu == null && Hover(hit)) || _menuKey == key;
            Color ink = selected || hover ? Text : A(Text, light ? 135 : 140);
            var textRect = new Rect(x, bar.Y, w - (key is "More" or "Sources" ? chevronW : 0), bar.Height);
            BarText(ctx, label, pt, selected ? DisplaySemibold : DisplayRegular, textRect, ink);
            if (key is "More" or "Sources")
            {
                if (key == "More") _moreAnchor = textRect; else _sourcesAnchor = textRect;
                Chevron(ctx, textRect.Right + chevronW / 2 + S(2), bar.Y + bar.Height / 2 + S(1), S(4), ink, Math.Max(1.3, 1.6 * _s), _menuKey == key);
            }
            _hits.Add(new Hit(hit, "nav", key));
            x += w + gap;
        }
        _navRight = x - gap;

        // A destra l'ingranaggio sul bordo e, alla sua sinistra, la lente della ricerca.
        double box = S(40);
        var gear = new Rect(bar.Right - PadX - box + S(8), bar.Y + (bar.Height - box) / 2, box, box);
        _optionsAnchor = gear;
        BarIcon(ctx, gear, "settings", (_menu == null && Hover(gear)) || _menuKey == "Options");
        _hits.Add(new Hit(gear, "nav", "Options"));
        DrawShellSearch(ctx, bar, gear.X - S(6));
    }

    private void BarText(DrawingContext ctx, string text, double pt, Typeface face, Rect r, Color ink)
    {
        // Sopra il banner un'ombra appena percettibile, cosi' resta leggibile sulle foto chiare.
        if (BarTransparent && !UiTheme.IsLight) TextAt(ctx, text, pt, face, Colors.Black, r.Translate(new Vector(0, 1)));
        TextAt(ctx, text, pt, face, ink, r);
    }

    private void BarIcon(DrawingContext ctx, Rect r, string icon, bool hot)
    {
        double side = Math.Round(r.Width * 0.5);
        var glyph = new Rect(r.X + (r.Width - side) / 2, r.Y + (r.Height - side) / 2, side, side);
        SvgIcon.Get(icon)?.Draw(ctx, glyph, hot ? Text : A(Text, UiTheme.IsLight ? 150 : 175));
    }

    // ----- Home -----
    private List<LibraryItem> _heroDeck = new();
    private int _heroIndex;
    private int _heroPrevIndex = -1;
    private DateTime _heroChangedAt = DateTime.MinValue;
    private const double HeroFadeMs = 700;
    private readonly Random _random = new();

    private LibraryItem? Hero => _heroDeck.Count == 0 ? null : _heroDeck[Math.Clamp(_heroIndex, 0, _heroDeck.Count - 1)];

    private const int HeroDeckSize = 6;
    private bool _heroPlaceholders;
    private static readonly Bitmap?[] HeroPlaceholderPhotos = new Bitmap?[3];

    // Locandine sfocate per il fondo delle schede orizzontali (una per file, piccole: 240 px).
    private static readonly ConcurrentDictionary<string, Bitmap?> BlurredCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> BlurredLoading = new(StringComparer.Ordinal);

    private Bitmap? BlurredArt(string path)
    {
        if (BlurredCache.TryGetValue(path, out var ready)) return ready;
        if (!BlurredLoading.TryAdd(path, 0)) return null;
        _ = Task.Run(async () =>
        {
            Bitmap? result = null;
            try
            {
                string? file = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await NetworkStore.CachedArtAsync(path) : path;
                if (file != null)
                {
                    using var source = SkiaSharp.SKBitmap.Decode(file);
                    if (source != null)
                    {
                        int w = 240, h = Math.Max(1, (int)Math.Round(source.Height * 240.0 / source.Width));
                        using var small = source.Resize(new SkiaSharp.SKImageInfo(w, h), SkiaSharp.SKFilterQuality.Medium);
                        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(w, h));
                        using var paint = new SkiaSharp.SKPaint { ImageFilter = SkiaSharp.SKImageFilter.CreateBlur(14, 14, SkiaSharp.SKShaderTileMode.Clamp) };
                        surface.Canvas.DrawBitmap(small, 0, 0, paint);
                        using var data = surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                        using var stream = data.AsStream();
                        result = new Bitmap(stream);
                    }
                }
            }
            catch { }
            BlurredCache[path] = result;
            Dispatcher.UIThread.Post(InvalidateVisual);
        });
        return null;
    }

    private static Bitmap? HeroPlaceholder(LibraryItem item)
    {
        int i = (int)((uint)StringComparer.Ordinal.GetHashCode(item.Path) % (uint)HeroPlaceholderPhotos.Length);
        if (HeroPlaceholderPhotos[i] == null)
            try { HeroPlaceholderPhotos[i] = new Bitmap(Path.Combine(MacTools.AssetsDirectory, "HeroPlaceholders", $"hero-placeholder-{i + 1}.jpg")); } catch { }
        return HeroPlaceholderPhotos[i];
    }
    internal string QaHeroDeck => string.Join(" | ", _heroDeck.Select(h => h.Title));

    private void BuildHeroDeck()
    {
        // Un piccolo mazzo mescolato una volta e poi percorso in ordine. Solo titoli con una vera foto orizzontale:
        // niente locandina verticale allargata, niente immagine generata.
        var shuffled = _items.OrderBy(_ => _random.Next()).ToList();
        _heroDeck = shuffled.Where(i => HeroPhoto(i) == true).Take(HeroDeckSize).ToList();
        // Nessuno sfondo vero (senza rete o senza TMDb): i film restano nel banner su una foto di riserva della sala.
        _heroPlaceholders = _heroDeck.Count == 0 && _category == "Movies" && ActiveServer == null;
        if (_heroPlaceholders) _heroDeck = shuffled.Take(HeroDeckSize).ToList();
        _heroIndex = 0; _heroPrevIndex = -1;
        if (_heroDeck.Count < HeroDeckSize) PrefetchHeroPhotos(shuffled);
    }

    /// <summary>Foto arrivate dopo: i titoli che ora hanno uno sfondo adatto completano il mazzo, senza cambiare quello a schermo.</summary>
    private void TopUpHeroDeck()
    {
        // Arrivato il primo sfondo vero: il mazzo di riserva lascia il posto a quelli veri.
        if (_heroPlaceholders) { if (_items.Any(i => HeroPhoto(i) == true)) BuildHeroDeck(); return; }
        if (_heroDeck.Count >= HeroDeckSize) return;
        var inDeck = _heroDeck.Select(h => h.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var item in _items)
        {
            if (_heroDeck.Count >= HeroDeckSize) break;
            if (!inDeck.Contains(item.Path) && HeroPhoto(item) == true) { _heroDeck.Add(item); inDeck.Add(item.Path); }
        }
    }

    // Esito per file (true = foto orizzontale abbastanza grande per il banner).
    private static readonly ConcurrentDictionary<string, bool> HeroPhotoFiles = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> HeroPhotoRequests = new(StringComparer.Ordinal);

    /// <summary>true: lo sfondo e' una foto orizzontale vera; false: no; null: non ancora sul disco.</summary>
    private static bool? HeroPhoto(LibraryItem item)
    {
        string? wide = item.WideArtPath;
        if (string.IsNullOrWhiteSpace(wide)) return null;
        string? file = wide.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? NetworkStore.CachedArtFileIfPresent(wide) : wide;
        if (file == null) return null;
        return HeroPhotoFiles.GetOrAdd(file, static f =>
        {
            try
            {
                using var codec = SkiaSharp.SKCodec.Create(f);
                if (codec == null) return false;
                int w = codec.Info.Width, h = codec.Info.Height;
                if (codec.EncodedOrigin is SkiaSharp.SKEncodedOrigin.LeftTop or SkiaSharp.SKEncodedOrigin.RightTop
                    or SkiaSharp.SKEncodedOrigin.RightBottom or SkiaSharp.SKEncodedOrigin.LeftBottom) (w, h) = (h, w);
                return w >= 1200 && h > 0 && w / (double)h >= 1.45;
            }
            catch { return false; }
        });
    }

    /// <summary>Scarica in sottofondo gli sfondi che mancano (server) o li chiede a TMDb (file locali), pochi alla volta.</summary>
    private void PrefetchHeroPhotos(IEnumerable<LibraryItem> candidates)
    {
        int started = 0;
        foreach (var item in candidates)
        {
            if (started >= 12) break;
            if (HeroPhoto(item) != null) continue;
            string? wide = item.WideArtPath;
            if (wide == null)
            {
                if (item.IsNetwork || item.Category is "Music" or "Photos") continue;
                if (!HeroPhotoRequests.TryAdd(item.Path, 0)) continue;
                LibraryStore.QueueArtwork(item, includeBackdrop: true);   // Changed -> Reload -> TopUpHeroDeck
                started++;
            }
            else if (wide.StartsWith("http", StringComparison.OrdinalIgnoreCase) && HeroPhotoRequests.TryAdd(wide, 0))
            {
                started++;
                _ = Task.Run(async () =>
                {
                    try { await NetworkStore.CachedArtAsync(wide); } catch { }
                    Dispatcher.UIThread.Post(() => { if (_items.Contains(item)) { TopUpHeroDeck(); InvalidateVisual(); } });
                });
            }
        }
    }

    private void NextHero() => SetHero((_heroIndex + 1) % Math.Max(1, _heroDeck.Count));

    private void SetHero(int index)
    {
        if (_heroDeck.Count < 2 || index == _heroIndex) return;
        _heroPrevIndex = _heroIndex;
        _heroIndex = index;
        _heroChangedAt = DateTime.UtcNow;
        _frames.Start();
    }

    // "Aggiunti di recente": ordinava tutta la libreria a ogni fotogramma della Home. Si riusa finche' la lista e' la
    // stessa (al massimo mezzo secondo, come CollectionItems).
    private List<LibraryItem>? _recentMemo;
    private (List<LibraryItem> Items, int Count) _recentMemoKey;
    private long _recentMemoAt;

    private List<LibraryItem> RecentItems()
    {
        long now = Environment.TickCount64;
        if (_recentMemo != null && now - _recentMemoAt < CollectionMemoMs && ReferenceEquals(_recentMemoKey.Items, _items) && _recentMemoKey.Count == _items.Count)
            return _recentMemo;
        _recentMemo = _items.OrderByDescending(i => i.SortDateUtc).Take(60).ToList();
        _recentMemoKey = (_items, _items.Count); _recentMemoAt = now;
        return _recentMemo;
    }

    private void DrawHome(DrawingContext ctx, Rect main)
    {
        double s = _s, padX = PadX, bar = BarH;
        double left = main.X + padX, width = Math.Max(1, main.Width - padX * 2), viewH = main.Height;
        double titleH = S(34), titleGap = S(14), rowGap = S(36), gap = S(20), bottomPad = S(16);
        var visible = RecentItems();
        var resume = ResumeItems();
        int resumeCols = (int)Math.Clamp(Math.Floor((width + gap) / Math.Max(1, S(260) + gap)), 3, 8);
        double resumeW = Math.Max(1, Math.Floor((width - gap * (resumeCols - 1)) / resumeCols));
        double resumeH = Math.Round(resumeW * 9 / 16);
        double resumeExtent = resume.Count > 0 ? titleH + titleGap + resumeH + rowGap : 0;

        // Hero compatto: le file restano nella parte visibile della finestra.
        double rowsTop = Math.Max(bar + S(250), Math.Round(viewH * 0.42));
        double maxRowsTop = viewH - resumeExtent - titleH - titleGap - PosterCardHeight(S(120)) - bottomPad;
        rowsTop = Math.Min(rowsTop, Math.Max(bar + S(180), maxRowsTop));
        if (Hero == null) rowsTop = bar + S(16);
        double posterRoom = Math.Max(1, viewH - rowsTop - resumeExtent - titleH - titleGap - bottomPad);
        int posterCols = (int)Math.Clamp(Math.Floor((width + gap) / Math.Max(1, S(176) + gap)), 4, 10);
        double posterW = Math.Max(1, Math.Floor((width - gap * (posterCols - 1)) / posterCols));
        // Conserva il rapporto delle locandine e il titolo sotto: con poca altezza, piu' schede piccole.
        while (PosterCardHeight(posterW) > posterRoom && posterW > 1)
        {
            posterCols++;
            posterW = Math.Max(1, Math.Floor((width - gap * (posterCols - 1)) / posterCols));
        }
        // Pochi film: schede orizzontali 16:9 che si dividono la larghezza, ma solo se restano grandi (almeno un quarto
        // della fila); con uno o due film la scheda ha un limite, per non diventare enorme.
        double shareW = visible.Count > 0 ? (width - gap * (visible.Count - 1)) / visible.Count : 0;
        bool wideRow = visible.Count > 0 && visible.Count < posterCols && shareW >= Math.Max(S(320), width * 0.24);
        // Altrimenti, se mancano poche caselle, le locandine si allargano fino a riempire la fila (se ci stanno in altezza).
        if (!wideRow && visible.Count > 0 && visible.Count < posterCols)
        {
            double fillW = Math.Floor(shareW);
            // Se non ci stanno (es. con "Continua a guardare"), prendono la misura piu' grande che ci sta in altezza;
            // il banner non si tocca.
            double w = fillW;
            while (w > posterW && PosterCardHeight(w) > posterRoom) w--;
            if (w > posterW) { posterCols = visible.Count; posterW = w; }
        }
        double posterH = PosterCardHeight(posterW);
        double wideW = wideRow ? Math.Floor(Math.Min((width - gap * (visible.Count - 1)) / visible.Count, Math.Max(S(420), width * 0.4))) : 0;
        double wideH = Math.Round(wideW * 9 / 16);
        double rowH = wideRow ? wideH : posterH;
        // Con poche file resta spazio vuoto sotto: le file scendono in fondo alla finestra e il banner si allarga.
        if (Hero != null && visible.Count > 0)
            rowsTop = Math.Max(rowsTop, Math.Round(viewH - bottomPad - resumeExtent - titleH - titleGap - rowH));
        var hero = new Rect(main.X, main.Y, main.Width, rowsTop + S(96));
        // Senza "Continua a guardare" le file scendono in fondo e il testo le seguiva troppo in basso: resta al centro
        // fra la barra e la fila (misurato: il fondo dei pulsanti a meta' strada + S(118) mette il blocco al centro).
        double heroTextBottom = rowsTop - S(56);
        if (resume.Count == 0) heroTextBottom = Math.Min(heroTextBottom, Math.Round((bar + rowsTop) / 2 + S(118)));
        if (Hero != null) DrawHero(ctx, hero, left, bar + S(8), heroTextBottom);

        double y = rowsTop;
        // La ripresa e' la prima azione utile: deve essere visibile senza scorrere.
        if (resume.Count > 0)
        {
            TextAt(ctx, "Continua a guardare", 16 * s, DisplaySemibold, Text, new Rect(left, y, width, titleH));
            y += titleH + titleGap;
            DrawResumeRow(ctx, new Rect(left, y, width, resumeH), resume, resumeCols, resumeW, gap, padX);
            y += resumeH + rowGap;
        }
        if (visible.Count == 0)
        {
            var area = new Rect(left, y, width, Math.Max(1, main.Bottom - y - bottomPad));
            if (DrawNetworkState(ctx, area)) return;
            if (ActiveServer is { } emptyServer)
            {
                var (title, body, _, _) = NetworkEmptyTexts("Movies", emptyServer.Name);
                DrawEmptyHome(ctx, area, title, body, "", null);
            }
            else DrawEmptyHome(ctx, area, null, null, "Aggiungi cartella", "add");
            return;
        }
        var gridTitle = new Rect(left, y, width, titleH);
        TextAt(ctx, "Aggiunti di recente", 16 * s, DisplaySemibold, Text, gridTitle);
        if (_category == "Movies") DrawSeeAll(ctx, gridTitle);
        y += titleH + titleGap;
        if (wideRow)
            for (int k = 0; k < visible.Count; k++)
                DrawWideCard(ctx, new Rect(left + k * (wideW + gap), y, wideW, wideH), visible[k]);
        else
            for (int k = 0; k < Math.Min(posterCols, visible.Count); k++)
                DrawPosterCard(ctx, new Rect(left + k * (posterW + gap), y, posterW, posterH), visible[k]);
    }

    /// <summary>
    /// Scheda orizzontale della Home con pochi film: lo sfondo del film (come "Continua a guardare") e il titolo.
    /// Senza sfondo: la locandina molto ingrandita e morbida come fondo, e la locandina vera a sinistra.
    /// </summary>
    private void DrawWideCard(DrawingContext ctx, Rect r, LibraryItem item)
    {
        LibraryStore.QueueArtwork(item, includeBackdrop: true);
        double scale = Math.Max(0.8, Math.Min(2.6, r.Height / 160));
        bool hover = _menu == null && Hover(r);
        double radius = Math.Max(1, Math.Round(6 * scale));
        var shade = Color.FromRgb(6, 10, 16);
        double pad = Math.Round(16 * scale), textLeft = r.X + pad;
        using (ctx.PushClip(new RoundedRect(r, radius)))
        {
            ctx.FillRectangle(B(Color.FromRgb(20, 24, 30)), r);
            bool backdrop = HeroPhoto(item) == true;
            if (backdrop && Image(item.WideArtPath, r.Width) is { } wide) DrawCover(ctx, wide, r, .5, .4);
            else if (Image(item.ArtPath, r.Height) is { } posterArt)
            {
                // Fondo: la locandina sfocata, una macchia di colore morbida.
                if (BlurredArt(item.ArtPath!) is { } soft)
                    using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                        DrawCover(ctx, soft, r, .5, .5);
                ctx.FillRectangle(B(Color.FromArgb(70, 0, 0, 0)), r);
                if (posterArt is { } poster)
                {
                    double ph = Math.Round(r.Height - pad * 2), pw = Math.Round(ph * 2 / 3);
                    var pr = new Rect(r.X + pad, r.Y + pad, pw, ph);
                    using (ctx.PushClip(new RoundedRect(pr, Math.Max(1, radius * 0.7)))) DrawCover(ctx, poster, pr, .5, .5);
                    textLeft = pr.Right + pad;
                }
            }
            // Nessuna immagine: la stessa luce morbida delle foto di riserva del banner.
            else if (HeroPlaceholder(item) is { } placeholder) DrawCover(ctx, placeholder, r, .5, .5);
            else DrawGeneratedArt(ctx, r, item.Title, false, item.Category);
            double floorH = Math.Max(Math.Round(64 * scale), Math.Round(r.Height * 0.5));
            ctx.FillRectangle(Vertical(shade, (0, 0), (0.25, 40), (0.6, 150), (1, 235)), new Rect(r.X, r.Bottom - floorH, r.Width, floorH + 1));
        }
        var ring = hover ? new Pen(B(UiTheme.IsLight ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(215, 255, 255, 255)), 2)
                         : new Pen(B(UiTheme.IsLight ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255)), 1);
        ctx.DrawRectangle(null, ring, r.Deflate(hover ? 1 : .5), radius, radius);
        double tw = Math.Max(1, r.Right - pad - textLeft);
        string meta = item.Year is int year ? year.ToString(CultureInfo.InvariantCulture) : "";
        TextAt(ctx, item.Title, Math.Max(8.4, 10.4 * scale), DisplaySemibold, Colors.White,
            new Rect(textLeft, r.Bottom - Math.Round((meta.Length > 0 ? 52 : 40) * scale), tw, Math.Round(24 * scale)));
        if (meta.Length > 0)
            TextAt(ctx, meta, Math.Max(7.6, 8.8 * scale), DisplayRegular, Color.FromArgb(205, 255, 255, 255),
                new Rect(textLeft, r.Bottom - Math.Round(30 * scale), tw, Math.Round(18 * scale)));
        _hits.Add(new Hit(r, "poster", item.Path, item));
    }

    private void DrawSeeAll(DrawingContext ctx, Rect row)
    {
        double s = Math.Max(0.8, row.Height / 34);
        double pt = 11.4 * s;
        string text = "Vedi tutti";
        double tw = Measure(text, pt, DisplayRegular), chevron = Math.Round(14 * s);
        var r = new Rect(row.Right - tw - chevron - Math.Round(6 * s), row.Y, tw + chevron + Math.Round(6 * s), row.Height);
        bool hover = _menu == null && Hover(r);
        Color ink = A(Text, hover ? 255 : 200);
        TextAt(ctx, text, pt, DisplayRegular, ink, new Rect(r.X, r.Y, tw + 1, r.Height));
        double z = Math.Max(3, Math.Floor(chevron / 4));
        Chevron(ctx, r.Right - chevron / 2, r.Y + r.Height / 2, z, ink, Math.Max(1.4, 1.7 * s), right: true);
        _hits.Add(new Hit(r, "see-all", "Movies"));
    }

    private void DrawHero(DrawingContext ctx, Rect r, double textLeft, double textTop, double textBottom)
    {
        // Dissolvenza fra un titolo e il successivo.
        double t = _heroPrevIndex >= 0 ? Math.Clamp((DateTime.UtcNow - _heroChangedAt).TotalMilliseconds / HeroFadeMs, 0, 1) : 1;
        if (t >= 1) _heroPrevIndex = -1;
        using (ctx.PushClip(r))
        {
            if (_heroPrevIndex >= 0 && _heroPrevIndex < _heroDeck.Count) DrawHeroLayer(ctx, r, _heroDeck[_heroPrevIndex], textLeft, 1);
            DrawHeroLayer(ctx, r, Hero, textLeft, t);
        }
        if (Hero is not { } item) return;
        using (ctx.PushOpacity(t)) DrawHeroText(ctx, item, textLeft, textTop, Math.Min(textBottom, r.Bottom), r);
    }

    // Il banner (fondo, foto, sfumature sotto e sopra) composto una volta in un'immagine (2026-11-21). Prima a ogni
    // ridisegno della Home erano 7-8 passate sulla zona piu' grande della finestra: su una scheda video con poca banda
    // (es. Radeon R5 220) bastava questo a far scattare la Home. Si rifa' solo se cambiano titolo, foto, misura o tema.
    private readonly record struct HeroLayerKey(string? Path, Bitmap? Art, Size Size, double TextLeft, Color Back, bool Light, double S, double RenderScale);
    private readonly List<(HeroLayerKey Key, RenderTargetBitmap Bitmap)> _heroLayers = new();

    private void DrawHeroLayer(DrawingContext ctx, Rect r, LibraryItem? item, double textLeft, double opacity)
    {
        if (opacity <= 0) { ResolveHeroArt(r, item, out _); return; }
        var layer = HeroLayer(r, item, textLeft, out double scale);
        if (layer == null)
        {
            // Senza immagine (memoria, superficie non disponibile): si disegna direttamente come prima.
            var bmp = ResolveHeroArt(r, item, out var art);
            using (ctx.PushOpacity(opacity)) { PaintHeroBackground(ctx, r, item, bmp, art); DrawHeroOverlays(ctx, r, textLeft); }
            return;
        }
        using (ctx.PushOpacity(opacity))
            ctx.DrawImage(layer, new Rect(layer.Size), new Rect(r.X, r.Y, layer.PixelSize.Width / scale, layer.PixelSize.Height / scale));
        if (ReferenceEquals(item, Hero)) PrepareNextHeroLayer(r, textLeft);
    }

    private RenderTargetBitmap? HeroLayer(Rect r, LibraryItem? item, double textLeft, out double scale)
    {
        var bmp = ResolveHeroArt(r, item, out var art);
        scale = Math.Clamp(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1, 1, 4);
        var key = new HeroLayerKey(item?.Path, bmp, r.Size, textLeft - r.X, Back, UiTheme.IsLight, _s, scale);
        foreach (var (k, b) in _heroLayers) if (k.Equals(key)) return b;
        var px = new PixelSize(Math.Max(1, (int)Math.Ceiling(r.Width * scale)), Math.Max(1, (int)Math.Ceiling(r.Height * scale)));
        RenderTargetBitmap? layer = null;
        try
        {
            layer = new RenderTargetBitmap(px, new Vector(96, 96));
            // Come SurfaceCrossfade: CreateDrawingContext e scala a 96 dpi (RenderTargetBitmap.Render applica i DPI due volte).
            using (var dc = layer.CreateDrawingContext())
            using (dc.PushTransform(Matrix.CreateTranslation(-r.X, -r.Y) * Matrix.CreateScale(scale, scale)))
            {
                PaintHeroBackground(dc, r, item, bmp, art);
                DrawHeroOverlays(dc, r, textLeft);
            }
        }
        catch { layer?.Dispose(); return null; }
        _heroLayers.Add((key, layer));
        // Il precedente (dissolvenza), quello a schermo e il successivo gia' pronto.
        while (_heroLayers.Count > 3) { _heroLayers[0].Bitmap.Dispose(); _heroLayers.RemoveAt(0); }
        return layer;
    }

    // Il banner successivo si compone a riposo, un paio di secondi dopo che e' comparso quello attuale: al cambio
    // (ogni 45 s) l'immagine e' gia' pronta e la dissolvenza parte senza il costo della composizione (~15 ms).
    private bool _heroPrepareQueued;

    private void PrepareNextHeroLayer(Rect r, double textLeft)
    {
        if (_heroPrepareQueued || _heroPrevIndex >= 0 || _heroDeck.Count < 2) return;
        _heroPrepareQueued = true;
        DispatcherTimer.RunOnce(() =>
        {
            _heroPrepareQueued = false;
            if (_view != "Home" || _heroPrevIndex >= 0 || _heroDeck.Count < 2 || !IsVisible) return;
            var next = _heroDeck[(_heroIndex + 1) % _heroDeck.Count];
            // Solo con la foto gia' arrivata (altrimenti la richiesta parte ora e il prossimo disegno riprova).
            if (ResolveHeroArt(r, next, out _) == null) return;
            Dispatcher.UIThread.Post(() => { if (_view == "Home" && _heroPrevIndex < 0) HeroLayer(r, next, textLeft, out _); }, DispatcherPriority.Background);
        }, TimeSpan.FromSeconds(2));
    }

    /// <summary>La foto del banner per <paramref name="item"/> (chiede copertina e immagine come a ogni disegno).</summary>
    private Bitmap? ResolveHeroArt(Rect r, LibraryItem? item, out Rect art)
    {
        if (item != null) LibraryStore.QueueArtwork(item, includeBackdrop: true);
        // L'immagine parte sotto la vignettatura (dal 20% della larghezza), non dal bordo sinistro.
        double start = r.X + Math.Round(r.Width * 0.20);
        double minW = Math.Max(360, Math.Round(r.Height * 2.45));
        if (r.Right - start < minW) start = Math.Max(r.X + r.Width / 4, r.Right - minW);
        art = new Rect(start, r.Y, Math.Max(1, r.Right - start), r.Height);
        return item == null ? null : _heroPlaceholders && HeroPhoto(item) != true ? HeroPlaceholder(item) : Image(item.WideArtPath, art.Width);
    }

    private void PaintHeroBackground(DrawingContext ctx, Rect r, LibraryItem? item, Bitmap? bmp, Rect art)
    {
        ctx.FillRectangle(B(Back), r);
        if (bmp != null)
        {
            DrawCover(ctx, bmp, art, 0.52, 0.23);
            // DrawHeroArtworkEntryFade: maschera morbida dove comincia la foto (niente riga verticale), e in scuro
            // una lieve ombra in alto che la lega al banner.
            double fadeW = Math.Min(Math.Max(96, Math.Round(r.Width * 0.24)), art.Width);
            ctx.FillRectangle(Horizontal(Back, (0, 255), (0.15, 231), (0.3, 178), (0.5, 108), (0.7, 45), (0.85, 12), (1, 0)), new Rect(art.X - 1, art.Y, fadeW + 1, art.Height));
            if (!UiTheme.IsLight) ctx.FillRectangle(Vertical(Back, (0, 86), (1, 0)), new Rect(art.X, r.Y, art.Width, Math.Max(1, r.Height / 5)));
        }
        else DrawGeneratedArt(ctx, r, item?.Title ?? "Cinecore", hero: true, item?.Category ?? "Movies");
    }

    private void DrawHeroOverlays(DrawingContext ctx, Rect r, double textLeft)
    {
        Color back = Back;
        double topFade = Math.Min(r.Height / 3, S(150));
        if (UiTheme.IsLight)
        {
            double textEnd = Math.Clamp((textLeft - r.X + HeroTextWidth(r) + S(24)) / r.Width, .25, .62);
            double fadeEnd = Math.Min(.94, textEnd + .30);
            ctx.FillRectangle(Horizontal(back, (0, 255), (textEnd, 215), (fadeEnd, 35), (1, 0)), r);
            ctx.FillRectangle(Vertical(back, (0, 170), (1, 0)), new Rect(r.X, r.Y, r.Width, topFade));
            double bh = Math.Round(r.Height * 0.42);
            ctx.FillRectangle(Vertical(back, (0, 0), (.5, 110), (1, 255)), new Rect(r.X, r.Bottom - bh, r.Width, bh));
            return;
        }
        // Vignettatura a sinistra (dove sta il testo); in alto un velo per la barra, in basso la foto si chiude nel fondo.
        ctx.FillRectangle(Horizontal(back, (0, 255), (0.18, 248), (0.34, 210), (0.52, 110), (0.74, 28), (1, 0)), r);
        ctx.FillRectangle(Vertical(back, (0, 190), (1, 0)), new Rect(r.X, r.Y, r.Width, topFade));
        double fadeH = Math.Round(r.Height * 0.48);
        ctx.FillRectangle(Vertical(back, (0, 0), (.35, 60), (.7, 170), (1, 255)), new Rect(r.X, r.Bottom - fadeH, r.Width, fadeH + 1));
    }

    private static LinearGradientBrush Gradient(Color c, bool vertical, params (double Pos, double Alpha)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = vertical ? new RelativePoint(0, 1, RelativeUnit.Relative) : new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        foreach (var (pos, alpha) in stops) brush.GradientStops.Add(new GradientStop(A(c, alpha), pos));
        return brush;
    }
    private static LinearGradientBrush Horizontal(Color c, params (double, double)[] stops) => Gradient(c, false, stops);
    private static LinearGradientBrush Vertical(Color c, params (double, double)[] stops) => Gradient(c, true, stops);

    private double HeroTextWidth(Rect r) => Math.Min(S(760), Math.Max(S(340), Math.Round(r.Width * 0.46)));

    private void DrawHeroText(DrawingContext ctx, LibraryItem item, double left, double top, double bottom, Rect hero)
    {
        double s = _s, textW = HeroTextWidth(hero);
        double actionH = S(48);
        double actionTop = bottom - (_heroDeck.Count > 1 ? S(44) : 0) - actionH;
        // Titolo grande, una riga di presentazione, due pulsanti. Il titolo non si taglia: se non sta in due righe si riduce.
        string tagline = HeroTagline(item);
        double linePt = Math.Max(10, 14 * s);
        double lineH = tagline.Length == 0 ? 0 : Format("Ag", linePt, DisplayRegular, Text).Height + S(2);
        double gapLine = S(14), gapActions = S(34);
        double roomForTitle = Math.Max(S(30), actionTop - top - gapActions - (lineH > 0 ? gapLine + lineH : 0));
        CachedText title;
        for (double size = Math.Max(22, 46 * s); ; size *= 0.92)
        {
            title = UiText.Get(L10n.T(item.Title), DisplayMedium, Px(size), Text, textW, 0, trimming: TextTrimming.None);
            double oneLine = Format("Ag", size, DisplayMedium, Text).Height;
            if ((title.Height <= oneLine * 2 + 2 && title.Height <= roomForTitle) || size < 14) break;
        }
        double y = Math.Max(top, actionTop - gapActions - (lineH > 0 ? gapLine + lineH : 0) - title.Height);
        ctx.DrawText(title, new Point(left, y));
        y += title.Height;
        if (lineH > 0)
        {
            y += gapLine;
            ctx.DrawText(Format(tagline, linePt, DisplayRegular, A(Text, UiTheme.IsLight ? 210 : 220), textW), new Point(left, y));
        }
        bool hasResume = ResumeOf(item) is { } r0 && r0.Position > 0;
        double bx = DrawHeroButton(ctx, left, actionTop, actionH, hasResume ? "Riprendi" : "Riproduci", primary: true, play: true, "hero-play", item) + S(14);
        DrawHeroButton(ctx, bx, actionTop, actionH, "Altre info", primary: false, play: false, "hero-info", item);
    }

    /// <summary>Una riga di presentazione: la prima frase della trama.</summary>
    private static string HeroTagline(LibraryItem item)
    {
        string text = (item.Overview ?? "").Trim();
        if (text.Length == 0)
            text = item.Category == "TV Series"
                ? "Episodio dalla tua libreria. Aggiorna i metadati TMDb per mostrare una sinossi completa."
                : "Sinossi TMDb non ancora disponibile per questo titolo. Aggiorna i metadati per mostrare qui la descrizione completa.";
        int end = -1;
        for (int i = 25; i < text.Length - 1; i++)
            if (text[i] is '.' or '!' or '?' && text[i + 1] == ' ') { end = i + 1; break; }
        return end > 0 && end <= 160 ? text[..end] : text;
    }

    private double DrawHeroButton(DrawingContext ctx, double x, double y, double h, string text, bool primary, bool play, string kind, LibraryItem item)
    {
        bool light = UiTheme.IsLight;
        double pt = Math.Max(9.4, 11.8 * _s);
        double tw = Measure(text, pt, DisplaySemibold);
        double glyph = play ? S(13) : 0, glyphGap = play ? S(10) : 0, pad = S(28);
        var r = new Rect(x, y, pad * 2 + glyph + glyphGap + tw, h);
        bool hover = _menu == null && Hover(r);
        DrawHeroButtonShape(ctx, r, primary, hover);
        Color ink = primary ? (light ? Colors.White : Colors.Black) : (light ? Text : Colors.White);
        double cx = r.X + pad;
        if (play)
        {
            double gy = r.Y + (r.Height - glyph) / 2;
            var tri = new PolylineGeometry(new[] { new Point(cx + 1, gy), new Point(cx + glyph, gy + glyph / 2), new Point(cx + 1, gy + glyph) }, true);
            ctx.DrawGeometry(B(ink), null, tri);
            cx += glyph + glyphGap;
        }
        TextAt(ctx, text, pt, DisplaySemibold, ink, new Rect(cx, r.Y, tw + 2, r.Height));
        _hits.Add(new Hit(r, kind, item.Path, item));
        return r.Right;
    }

    private static void DrawHeroButtonShape(DrawingContext ctx, Rect r, bool primary, bool hover)
    {
        bool light = UiTheme.IsLight;
        Color fill = primary
            ? (light ? Color.FromArgb(255, (byte)(hover ? 60 : 20), 24, 30) : Color.FromArgb((byte)(hover ? 228 : 255), 255, 255, 255))
            : (light ? Color.FromArgb((byte)(hover ? 30 : 16), 0, 0, 0) : Color.FromArgb((byte)(hover ? 70 : 46), 255, 255, 255));
        ctx.DrawRectangle(B(fill), null, r, r.Height / 2, r.Height / 2);
    }

    // ----- Continua a guardare (DrawResumeRow / DrawResumeCard) -----
    private int _resumeOffset;

    private Dictionary<string, (double Position, double Duration, DateTime SavedAt)> _localResume = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Position, double Duration, DateTime SavedAt)> _cachedNetworkResume = new(StringComparer.OrdinalIgnoreCase);
    private List<(LibraryItem Item, double Position, double Duration, DateTime SavedAt)> _resume = new();

    private (double Position, double Duration, DateTime SavedAt)? ResumeOf(LibraryItem item)
    {
        if (item.IsNetwork)
        {
            if (!NetworkStore.Resume.TryGetValue(item.Path, out var r) && !_cachedNetworkResume.TryGetValue(item.Path, out r)) return null;
            return r.Position > 0 && !CinecorePlayer2025.Engines.PlaybackResumeStore.IsHidden(item.Path, r.SavedAt) ? r : null;
        }
        return _localResume.TryGetValue(item.Path, out var l) ? l : null;
    }

    private List<(LibraryItem Item, double Position, double Duration, DateTime SavedAt)> ResumeItems() => _resume;
    internal int QaVisibleResumeCards => _hits.Count(hit => hit.Kind == "resume");

    /// <summary>La Home mostra le riprese della fonte selezionata.</summary>
    private void RefreshResume()
    {
        _localResume = new(StringComparer.Ordinal);
        try
        {
            foreach (var e in CinecorePlayer2025.Engines.PlaybackResumeStore.LoadAll())
                if (e.PositionSeconds > 0 && e.DurationSeconds > 0 && !CinecorePlayer2025.Engines.PlaybackResumeStore.IsHidden(e.MediaPath, e.SavedAt))
                    _localResume[e.MediaPath] = (e.PositionSeconds, e.DurationSeconds, e.SavedAt);
        }
        catch { }
        var result = new List<(LibraryItem, double, double, DateTime)>();
        var server = ActiveServer;
        if (server == null && _localResume.Count > 0)
        {
            var indexed = LibraryStore.Items("Movies").Concat(LibraryStore.Items("TV Series"))
                .GroupBy(item => item.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var (path, resume) in _localResume)
            {
                var item = indexed.GetValueOrDefault(path) ?? LibraryStore.ResumeItem(path);
                if (item != null) result.Add((item, resume.Position, resume.Duration, resume.SavedAt));
            }
        }
        _cachedNetworkResume.Clear();
        if (server != null && NetworkStore.Catalog(server.Key) is { } networkItems)
        {
            var networkHistory = CinecorePlayer2025.Engines.WatchHistoryStore.LoadInProgress()
                .Where(e => NetworkStore.CanResume(e.PositionSeconds, e.DurationSeconds))
                .OrderByDescending(e => e.LastWatchedAtUtc)
                .DistinctBy(e => e.MediaPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(e => e.MediaPath, StringComparer.OrdinalIgnoreCase);
            foreach (var item in networkItems.Where(i => i.Category is "Movies" or "TV Series" or "Videos").DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
            {
                // I cataloghi delle build precedenti non salvavano il punto di ripresa. Il diario lo conosce già.
                if (item.ResumeSavedAt == default && networkHistory.TryGetValue(item.Path, out var saved))
                    _cachedNetworkResume[item.Path] = (saved.PositionSeconds, saved.DurationSeconds, saved.LastWatchedAtUtc);
                if (ResumeOf(item) is { } r) result.Add((item, r.Position, r.Duration, r.SavedAt));
            }
        }
        _resume = result.OrderByDescending(x => x.Item4).Take(20).ToList();
    }

    private void DrawResumeRow(DrawingContext ctx, Rect r, List<(LibraryItem Item, double Position, double Duration, DateTime SavedAt)> items, int cols, double cardW, double gap, double padX)
    {
        int maxStart = Math.Max(0, items.Count - cols);
        int start = Math.Clamp(_resumeOffset, 0, maxStart);
        for (int k = start; k < Math.Min(items.Count, start + cols); k++)
            DrawResumeCard(ctx, new Rect(r.X + (k - start) * (cardW + gap), r.Y, cardW, r.Height), items[k]);
        if (items.Count > cols)
        {
            double cy = r.Y + r.Height / 2;
            DrawSideArrow(ctx, r.X - padX / 2, cy, true, start > 0, Math.Max(0, start - cols));
            DrawSideArrow(ctx, r.Right + padX / 2, cy, false, start < maxStart, Math.Min(maxStart, start + cols));
        }
    }

    /// <summary>Freccia di un carosello nel margine laterale.</summary>
    private void DrawSideArrow(DrawingContext ctx, double cx, double cy, bool left, bool enabled, int target)
    {
        double box = S(40);
        var r = new Rect(cx - box / 2, cy - box / 2, box, box);
        bool hover = enabled && _menu == null && Hover(r);
        Color ink = !enabled ? A(Text, UiTheme.IsLight ? 45 : 40) : A(Text, hover ? 255 : 190);
        double z = (box - 2 * S(6)) / 2;
        Chevron(ctx, cx, cy, z, ink, Math.Max(1.6, 2.2 * _s), left: left, right: !left);
        if (enabled) _hits.Add(new Hit(r, "resume-page", target.ToString(CultureInfo.InvariantCulture)));
    }

    private void DrawResumeCard(DrawingContext ctx, Rect r, (LibraryItem Item, double Position, double Duration, DateTime SavedAt) resume)
    {
        var item = resume.Item;
        LibraryStore.QueueArtwork(item, includeBackdrop: true);
        double scale = Math.Max(0.8, Math.Min(2.6, r.Height / 160));
        bool hover = _menu == null && Hover(r);
        double radius = Math.Max(1, Math.Round(6 * scale));
        var shade = Color.FromRgb(6, 10, 16);
        using (ctx.PushClip(new RoundedRect(r, radius)))
        {
            var bmp = Image(item.WideArtPath, r.Width) ?? Image(item.ArtPath, r.Width);
            if (bmp != null) DrawCover(ctx, bmp, r, .5, item.WideArtPath != null ? .46 : .30);
            else DrawGeneratedArt(ctx, r, item.Title, false, item.Category);
            // Vignettatura in basso: la foto si scurisce sotto titolo e avanzamento.
            var lower = new Rect(r.X, r.Y + Math.Round(r.Height * 0.18), r.Width, Math.Round(r.Height * 0.82) + 1);
            ctx.FillRectangle(Vertical(shade, (0, 0), (0.18, 24), (0.36, 70), (0.54, 128), (0.72, 184), (0.88, 228), (1, 250)), lower);
            double floorH = Math.Max(Math.Round(64 * scale), Math.Round(r.Height * 0.5));
            ctx.FillRectangle(Vertical(shade, (0, 0), (0.20, 30), (0.42, 96), (0.66, 172), (0.84, 226), (1, 252)), new Rect(r.X, r.Bottom - floorH, r.Width, floorH + 1));
            // Bordi e angoli piu' scuri, il centro della foto resta pulito; ai lati un'ombra lieve.
            ctx.FillRectangle(new RadialGradientBrush
            {
                RadiusX = new RelativeScalar(0.72, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.82, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.6), new GradientStop(Color.FromArgb(150, 0, 0, 0), 1) },
            }, r);
            ctx.FillRectangle(Horizontal(Colors.Black, (0, 70), (0.14, 0), (0.86, 0), (1, 70)), r);
        }
        var ring = hover ? new Pen(B(UiTheme.IsLight ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(215, 255, 255, 255)), 2)
                         : new Pen(B(UiTheme.IsLight ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255)), 1);
        ctx.DrawRectangle(null, ring, r.Deflate(hover ? 1 : .5), radius, radius);
        double pad = Math.Round(16 * scale);
        TextAt(ctx, item.Category == "TV Series" && item.SeriesTitle != null ? EpisodeTitle(item) : item.Title,
            Math.Max(8.4, 10.4 * scale), DisplaySemibold, Colors.White, new Rect(r.X + pad, r.Bottom - Math.Round(60 * scale), r.Width - pad * 2, Math.Round(24 * scale)));
        TextAt(ctx, FormatMinutes(Math.Max(0, (resume.Duration - resume.Position) / 60)) + " rimanenti", Math.Max(7.6, 8.8 * scale), DisplayRegular,
            Color.FromArgb(205, 255, 255, 255), new Rect(r.X + pad, r.Bottom - Math.Round(37 * scale), r.Width - pad * 2, Math.Round(18 * scale)));
        // Avanzamento: pista chiara velata, parte vista bianca.
        var track = new Rect(r.X + pad, r.Bottom - Math.Round(13 * scale), r.Width - pad * 2, Math.Max(3, Math.Round(3 * scale)));
        ctx.DrawRectangle(B(Color.FromArgb(70, 255, 255, 255)), null, track, track.Height / 2, track.Height / 2);
        double w = Math.Round(track.Width * Math.Clamp(resume.Position / resume.Duration, 0, 1));
        if (w >= track.Height) ctx.DrawRectangle(B(Colors.White), null, new Rect(track.X, track.Y, w, track.Height), track.Height / 2, track.Height / 2);
        _hits.Add(new Hit(r, "resume", item.Path, item));
    }

    private static string EpisodeTitle(LibraryItem item) =>
        item.SeasonNumber.HasValue && item.EpisodeNumber.HasValue ? $"{item.SeriesTitle} S{item.SeasonNumber:00}E{item.EpisodeNumber:00}" : item.Title;

    private static string FormatMinutes(double minutes)
    {
        int total = Math.Max(0, (int)Math.Round(minutes)), h = total / 60, m = total % 60;
        return h > 0 ? $"{h}h {m:00}m" : $"{Math.Max(1, m)}m";
    }

    // ----- locandine -----
    private static double PosterCardScale(double w) => Math.Clamp(w / 170, .78, 2.6);
    private static double PosterCaptionHeight(double w) => Math.Max(1, Math.Round(54 * PosterCardScale(w)));
    private static double PosterCardHeight(double w) => Math.Round(w * 1.5) + PosterCaptionHeight(w);

    private void DrawPosterCard(DrawingContext ctx, Rect r, LibraryItem item)
    {
        LibraryStore.QueueArtwork(item, includeBackdrop: false);
        double scale = PosterCardScale(r.Width);
        bool hover = _menu == null && Hover(r);
        double capH = Math.Min(PosterCaptionHeight(r.Width), Math.Max(0, r.Height / 3));
        var poster = new Rect(r.X, r.Y, r.Width, Math.Max(1, r.Height - capH));
        double radius = Math.Max(1, Math.Round(6 * scale));
        using (ctx.PushClip(new RoundedRect(poster, radius)))
        {
            var bmp = Image(item.ArtPath, poster.Width);
            if (bmp != null) DrawCover(ctx, bmp, poster, .5, .5);
            else DrawGeneratedArt(ctx, poster, item.Category == "TV Series" ? item.SeriesTitle ?? item.Title : item.Title, false, item.Category);
        }
        // Filo appena visibile; sotto il puntatore un contorno chiaro di 2 px.
        bool light = UiTheme.IsLight;
        var pen = hover ? new Pen(B(light ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(215, 255, 255, 255)), 2)
                        : new Pen(B(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255)), 1);
        ctx.DrawRectangle(null, pen, poster.Deflate(hover ? 1 : .5), radius, radius);
        // Solo il titolo sotto la locandina, su una o due righe.
        string caption = item.Category == "TV Series" ? item.SeriesTitle ?? item.Title : item.Title;
        bool diary = _category == "WatchHistory" && _view == "Collection";
        var ft = Format(caption, Math.Max(9.4, 11.4 * scale), DisplayRegular, Text, r.Width - 2, diary ? 1 : 2);
        ctx.DrawText(ft, new Point(r.X + 1, poster.Bottom + Math.Round(10 * scale)));
        _hits.Add(new Hit(r, "poster", item.Path, item));
        if (diary) DrawDiaryStars(ctx, new Rect(r.X, poster.Bottom + Math.Round(10 * scale) + ft.Height + Math.Round(4 * scale), r.Width, Math.Round(18 * scale)), item);
        if (diary) DrawDiaryRank(ctx, poster, item);
    }

    // ----- Film, Serie TV e le altre raccolte -----
    // Ultimo risultato di CollectionItems: si chiamava a ogni fotogramma (scorrimento, animazioni) e ogni volta
    // filtrava, ordinava e per le serie raggruppava tutta la libreria. Si riusa con gli stessi ingressi per al massimo
    // mezzo secondo: i filtri che dipendono da altro (Riprendi, Diario, Recenti) restano aggiornati entro quel tempo.
    private List<LibraryItem>? _collectionMemo;
    private (List<LibraryItem> Items, int Count, string Query, string Filter, int Sort, string Category, string View) _collectionMemoKey;
    private long _collectionMemoAt;
    private const long CollectionMemoMs = 500;

    private List<LibraryItem> CollectionItems()
    {
        var key = (_items, _items.Count, _query, _filter, _sortMode, _category, _view);
        long now = Environment.TickCount64;
        if (_collectionMemo != null && now - _collectionMemoAt < CollectionMemoMs && ReferenceEquals(key.Item1, _collectionMemoKey.Items)
            && key.Equals(_collectionMemoKey)) return _collectionMemo;
        var result = CollectionItemsCore();
        _collectionMemo = result; _collectionMemoKey = key; _collectionMemoAt = now;
        return result;
    }

    private List<LibraryItem> CollectionItemsCore()
    {
        // Diario: l'ordine e' quello delle visioni, non quello della libreria.
        // Con un filtro (Classifica, Valutati, 4K) vale il filtro, con il suo ordine (la Classifica dal voto piu' alto).
        if (_category == "WatchHistory")
        {
            if (_filter != "All") return ApplyFilterSortSearch(_items).ToList();
            var tokens = Tokens(_query); return _items.Where(i => tokens.All(Haystack(i).Contains)).ToList();
        }
        var items = ApplyFilterSortSearch(_items).ToList();
        if (_category != "TV Series") return items;
        // Serie: una locandina per serie (il primo episodio in ordine), come i gruppi di Windows.
        return items.GroupBy(i => (i.SeriesTitle ?? i.Title).Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.OrderBy(e => e.SeasonNumber ?? 0).ThenBy(e => e.EpisodeNumber ?? 0).First()).ToList();
    }

    /// <summary>Barra in alto senza fondo: sopra il banner della Home, della Rete e della pagina Musica.</summary>
    private bool BarTransparent => _view is "Home" or "Network" || (MusicOverviewActive && _filter == "All");

    private void DrawCollection(DrawingContext ctx, Rect page)
    {
        var content = new Rect(page.X + PadX, BarH - S(80), Math.Max(1, page.Width - PadX * 2), Math.Max(1, page.Bottom - (BarH - S(80)) - S(20)));
        double titleY = content.Y + S(55);
        double gridTop = titleY + Math.Max(74, S(74));
        bool hasFolders = _category is "Movies" or "TV Series" or "Videos" or "Favourites" or "Photos" or "Music" or "WatchHistory";
        var visible = hasFolders ? CollectionItems() : new List<LibraryItem>();
        DrawAlsoIn(ctx, new Rect(content.X, titleY, content.Width, S(34)));
        if (_category == "WatchHistory") DrawDiaryHeader(ctx, new Rect(content.X, BarH + S(4), content.Width, S(34)));
        if (_category == "Playlists") { DrawPlaylistsPage(ctx, new Rect(content.X, gridTop, content.Width, Math.Max(1, page.Bottom - gridTop))); return; }
        if (visible.Count == 0 && (_query.Trim().Length > 0 || _filter != "All") && _items.Count > 0)
        {
            _scroll = _scrollTarget = _scrollMax = 0;
            DrawEmptyHome(ctx, new Rect(content.X, gridTop, content.Width, Math.Max(1, page.Height - gridTop - S(20))), "Nessun risultato",
                _query.Trim().Length > 0 ? $"Niente corrisponde a “{_query.Trim()}”. Prova con un'altra parola o togli i filtri." : "Nessun titolo con questo filtro.", "", null);
            return;
        }
        // Catalogo del server in caricamento o bloccato: lo stesso stato della Home (prima qui diceva "nessun film").
        if (visible.Count == 0 && DrawNetworkState(ctx, new Rect(content.X, BarH, content.Width, Math.Max(1, page.Height - BarH - S(20)))))
        {
            _scroll = _scrollTarget = _scrollMax = 0;
            return;
        }
        if (visible.Count == 0)
        {
            _scroll = _scrollTarget = _scrollMax = 0;
            var (title, body, action, key) = ActiveServer is { } source ? NetworkEmptyTexts(_category, source.Name) : EmptyTexts(_category);
            DrawEmptyHome(ctx, new Rect(content.X, BarH, content.Width, Math.Max(1, page.Height - BarH - S(20))), title, body, action, key);
            return;
        }
        if (_category == "Music")
        {
            // Panoramica in stile Home; "Vedi tutti" (o una ricerca) apre la griglia con Album, Artisti, Brani.
            if (MusicOverviewActive && _filter == "All") { DrawMusicOverview(ctx, page, new Rect(page.X + PadX, 0, Math.Max(1, page.Width - PadX * 2), page.Height), visible); return; }
            double musicTop = DrawMusicGridHeader(ctx, content, gridTop - S(52));
            var area = new Rect(content.X, musicTop, content.Width, Math.Max(1, page.Bottom - musicTop));
            if (_musicTab == 1) DrawArtistGrid(ctx, area, visible);
            else if (_musicTab == 2) DrawTrackList(ctx, area, visible);
            else DrawMusicGrid(ctx, area, visible);
            return;
        }
        if (UsesTemporalSections(_category))
        {
            DrawTemporalGrid(ctx, new Rect(content.X, gridTop, content.Width, Math.Max(1, page.Bottom - gridTop)), visible);
            return;
        }
        double gap = S(20);
        int columns = (int)Math.Clamp(Math.Floor((content.Width + gap) / Math.Max(1, S(176) + gap)), 4, 12);
        double cardW = Math.Max(1, Math.Floor((content.Width - gap * (columns - 1)) / columns));
        double cardH = PosterCardHeight(cardW), rowGap = S(28);
        int rows = (visible.Count + columns - 1) / columns;
        var grid = new Rect(content.X, gridTop, content.Width, Math.Max(1, page.Bottom - gridTop));
        _scrollMax = Math.Max(0, rows * (cardH + rowGap) - rowGap + S(24) - grid.Height);
        _scroll = Math.Clamp(_scroll, 0, _scrollMax); _scrollTarget = Math.Clamp(_scrollTarget, 0, _scrollMax);
        using (ctx.PushClip(new Rect(page.X, gridTop - S(6), page.Width, page.Bottom - gridTop + S(6))))
        {
            int first = Math.Max(0, (int)Math.Floor(_scroll / (cardH + rowGap)) * columns);
            for (int i = first; i < visible.Count; i++)
            {
                int row = i / columns, col = i % columns;
                double y = gridTop + row * (cardH + rowGap) - _scroll;
                if (y > page.Bottom) break;
                var card = new Rect(content.X + col * (cardW + gap), y, cardW, cardH);
                int hitsBefore = _hits.Count;
                DrawPosterCard(ctx, card, visible[i]);
                // Le zone di clic sotto la barra non contano.
                for (int h = hitsBefore; h < _hits.Count; h++)
                    if (_hits[h].Bounds.Y < gridTop - S(6)) _hits[h] = _hits[h] with { Bounds = _hits[h].Bounds.Intersect(new Rect(0, gridTop, page.Width, page.Height)) };
            }
        }
    }

    private static (string Title, string Body, string Action, string? Key) EmptyTexts(string category) => category switch
    {
        "Movies" => ("Ancora nessun film", "Aggiungi la cartella dove tieni i tuoi film: locandine, trame e cast compariranno qui.", "Aggiungi cartella", "add"),
        "TV Series" => ("Ancora nessuna serie TV", "Aggiungi la cartella dove tieni le tue serie: stagioni, episodi e copertine compariranno qui.", "Aggiungi cartella", "add"),
        "Videos" => ("Ancora nessun video", "Aggiungi la cartella dove tieni i tuoi video: compariranno qui, ordinati per data.", "Aggiungi cartella", "add"),
        "Photos" => ("Ancora nessuna foto", "Aggiungi la cartella dove tieni le tue foto: compariranno qui, ordinate per data.", "Aggiungi cartella", "add"),
        "Music" => ("Ancora nessun brano", "Aggiungi la cartella dove tieni la tua musica: album, artisti e copertine compariranno qui.", "Aggiungi cartella", "add"),
        "WatchHistory" => ("Ancora nessuna visione", "I film e gli episodi che guardi compariranno qui, dal più recente.", "", null),
        "Favourites" => ("Ancora nessun preferito", "Tocca la stella su un film, una serie o un album per ritrovarlo qui.", "", null),
        "Playlists" => ("Ancora nessuna playlist", "Raccogli film, episodi o brani da riprodurre di fila. Puoi aggiungerli anche dalle loro schede.", "Nuova playlist", "new-playlist"),
        _ => ("Niente da mostrare", "Aggiungi una cartella o scegli un'altra categoria.", "Aggiungi cartella", "add"),
    };

    /// <summary>Server come fonte, catalogo vuoto perche' in caricamento o non raggiungibile/bloccato: messaggio e azione. False se non e' il caso.</summary>
    private bool DrawNetworkState(DrawingContext ctx, Rect area)
    {
        if (ActiveServer is not { } server || _items.Count > 0) return false;
        if (_networkLoading) { DrawEmptyHome(ctx, area, "Caricamento del catalogo…", server.Name, "", null); return true; }
        if (_networkStatus.Length == 0) return false;
        bool locked = Keychain.AccessRequired;
        DrawEmptyHome(ctx, area, locked ? "Accesso salvato protetto" : "Non riesco ad aprire il catalogo", _networkStatus,
            locked ? (Keychain.Authorizing ? "In attesa di macOS…" : "Autorizza accesso") : "Riprova", locked ? "keychain-authorize" : "net-retry");
        return true;
    }

    /// <summary>Su un server di rete (Jellyfin, Emby, DLNA) le cartelle non c'entrano: niente "Aggiungi cartella", come su Windows.</summary>
    private static (string Title, string Body, string Action, string? Key) NetworkEmptyTexts(string category, string server)
    {
        (string None, string Plural)? nouns = category switch
        {
            "Movies" => ("Nessun film", "film"), "TV Series" => ("Nessuna serie TV", "serie TV"),
            "Videos" => ("Nessun video", "video"), "Photos" => ("Nessuna foto", "foto"), "Music" => ("Nessun brano", "musica"),
            _ => null,
        };
        if (category is "WatchHistory" or "Favourites" or "Playlists") { var local = EmptyTexts(category); return (local.Title, local.Body, local.Key == "add" ? "" : local.Action, local.Key == "add" ? null : local.Key); }
        if (nouns is { } n)
            return ($"{n.None} su {server}", $"Questo server non condivide {n.Plural}. Scegli un'altra fonte dal menu in alto.", "", null);
        return ("Nessun contenuto in questa categoria", "Questo server non condivide nulla qui. Scegli un'altra fonte dal menu in alto.", "", null);
    }

    /// <summary>Messaggio grande e centrato con una sola azione (DrawEmptyHome).</summary>
    private void DrawEmptyHome(DrawingContext ctx, Rect area, string? titleText, string? bodyText, string actionLabel, string? actionKey)
    {
        double width = Math.Min(area.Width, S(680));
        double left = area.X + (area.Width - width) / 2;
        double y = area.Y + Math.Max(0, (area.Height - S(230)) / 2 - S(30));
        var title = Format(titleText ?? "Iniziamo dalle tue cartelle", Math.Max(20, 30 * _s), DisplaySemibold, Text, width, 2, TextAlignment.Center);
        ctx.DrawText(title, new Point(left, y));
        var body = Format(bodyText ?? "Aggiungi la cartella dove tieni film, serie TV o musica: Cinecore riconosce i file e li ordina da solo.",
            Math.Max(10.5, 13 * _s), DisplayRegular, A(Text, 185), width, 3, TextAlignment.Center);
        ctx.DrawText(body, new Point(left, y + S(64)));
        if (actionKey == null) return;
        double pt = Math.Max(9.4, 11.8 * _s);
        double bw = Measure(actionLabel, pt, DisplaySemibold) + S(56);
        var button = new Rect(area.X + (area.Width - bw) / 2, y + S(150), bw, S(48));
        DrawHeroButtonShape(ctx, button, true, _menu == null && Hover(button));
        TextAt(ctx, actionLabel, pt, DisplaySemibold, UiTheme.IsLight ? Colors.White : Colors.Black, button, TextAlignment.Center);
        _hits.Add(new Hit(button, "action", actionKey));
    }

    // ------------------------------------------------------------------ menu della barra (elenco che scende sotto la voce, pagina velata)
    private sealed record MenuItem(string Label, bool Selected, Action? Run);
    private List<MenuItem>? _menu;
    private string? _menuKey;
    private Point _menuAnchor;
    private DateTime _menuOpenAt, _menuCloseAt;
    private Action? _menuAfter;
    private int _menuHover = -1;
    private const double InMs = 190, OutMs = 150, Stagger = 25;

    private double MenuPt => Math.Max(10, 13.6 * _s);
    private double MenuRowH => Math.Round(Px(MenuPt) * 1.95);

    private void OpenMenu(string key, IEnumerable<MenuItem> items, Point anchor)
    {
        _menu = items.ToList(); _menuKey = key; _menuHover = -1; _menuAfter = null; _menuCloseAt = default;
        double textW = _menu.Count == 0 ? 0 : _menu.Max(i => Measure(i.Label, MenuPt, DisplaySemibold));
        double margin = Math.Max(12, MenuRowH / 2);
        double left = key == "Options" ? anchor.X - textW : anchor.X;
        _menuAnchor = new Point(Math.Clamp(left, margin, Math.Max(margin, Bounds.Width - textW - margin)), anchor.Y);
        _menuOpenAt = DateTime.UtcNow;
        _frames.Start();
        InvalidateVisual();
    }

    private void CloseMenu(Action? after = null)
    {
        if (_menu == null || _menuCloseAt != default) return;
        _menuAfter = after; _menuCloseAt = DateTime.UtcNow;
        _frames.Start();
    }

    private Rect MenuRow(int i)
    {
        double w = _menu!.Max(item => Measure(item.Label, MenuPt, DisplaySemibold));
        return new Rect(_menuAnchor.X - 14, _menuAnchor.Y + i * MenuRowH, w + 28, MenuRowH);
    }

    private double MenuProgress(int index)
    {
        if (_menuCloseAt != default) return 1 - Math.Clamp((DateTime.UtcNow - _menuCloseAt).TotalMilliseconds / OutMs, 0, 1);
        return Math.Clamp(((DateTime.UtcNow - _menuOpenAt).TotalMilliseconds - index * Stagger) / InMs, 0, 1);
    }

    private void DrawMenu(DrawingContext ctx, Rect page)
    {
        if (_menu == null) return;
        double veil = _menuCloseAt != default ? MenuProgress(0) : Math.Clamp((DateTime.UtcNow - _menuOpenAt).TotalMilliseconds / InMs, 0, 1);
        veil = 1 - (1 - veil) * (1 - veil);
        // La pagina sotto la barra si vela (niente vignetta ovale), con un ingresso morbido senza bordo netto.
        double barBottom = BarH + Math.Round(TopShift);
        var surface = new Rect(0, barBottom, page.Width, Math.Max(1, page.Height - barBottom));
        // Menu nel punto del clic (titoli, voto del diario): velo piu' scuro, le voci stanno sopra le locandine.
        double alpha = (_menuKey == "Item" ? (UiTheme.IsLight ? 228 : 222) : (UiTheme.IsLight ? 205 : 180)) * veil;
        double fadeEnd = Math.Clamp(S(90) / surface.Height, 0.01, 0.9);
        ctx.FillRectangle(Vertical(Back, (0, 0), (fadeEnd, alpha), (1, alpha)), surface);
        for (int i = 0; i < _menu.Count; i++)
        {
            double p = MenuProgress(i), eased = 1 - Math.Pow(1 - p, 3);
            if (eased <= 0.01) continue;
            var r = MenuRow(i);
            bool enabled = _menu[i].Run != null;
            bool strong = enabled && (_menu[i].Selected || i == _menuHover);
            var face = strong ? DisplaySemibold : DisplayRegular;
            double textX = _menuKey == "Options" ? r.Right - 14 - Measure(_menu[i].Label, MenuPt, face) : r.X + 14;
            TextAt(ctx, _menu[i].Label, MenuPt, face, A(Text, (strong ? 255 : enabled ? 150 : 78) * eased),
                new Rect(textX, r.Y + Math.Round(10 * (1 - eased)), r.Width, r.Height));
        }
    }

    private int MenuHit(Point p)
    {
        if (_menu == null) return -1;
        for (int i = 0; i < _menu.Count; i++) if (MenuRow(i).Contains(p)) return i;
        return -1;
    }

    private void StepFrames()
    {
        bool busy = false;
        if (_menu != null)
        {
            if (_menuCloseAt != default && (DateTime.UtcNow - _menuCloseAt).TotalMilliseconds >= OutMs)
            {
                var after = _menuAfter;
                _menu = null; _menuKey = null; _menuAfter = null;
                after?.Invoke();
            }
            else busy = _menuCloseAt != default || (DateTime.UtcNow - _menuOpenAt).TotalMilliseconds < InMs + _menu.Count * Stagger;
        }
        if (_heroPrevIndex >= 0) busy = true;
        if ((DateTime.UtcNow - _searchAnimAt).TotalMilliseconds < SearchAnimMs + 20) busy = true;
        if (_detail != null && (DateTime.UtcNow - _detailOpenedAt).TotalMilliseconds < 260) busy = true;
        if (_foldersOpen && (DateTime.UtcNow - _foldersOpenedAt).TotalMilliseconds < 260) busy = true;
        if (PhotoOpen && (DateTime.UtcNow - _photoOpenedAt).TotalMilliseconds < 220) busy = true;
        if (_album != null && (DateTime.UtcNow - _albumOpenedAt).TotalMilliseconds < 240) busy = true;
        if (_seriesName != null && ((DateTime.UtcNow - _seriesOpenedAt).TotalMilliseconds < 260 || (DateTime.UtcNow - _seriesScrollAt).TotalMilliseconds < SeriesScrollMs + 20)) busy = true;
        if (Math.Abs(_scroll - _scrollTarget) > 0.5) { _scroll += (_scrollTarget - _scroll) * 0.25; busy = true; }
        else _scroll = _scrollTarget;
        InvalidateVisual();
        if (Environment.TickCount64 - _heroPhotoAt < 340) busy = true;
        if (!busy) _frames.Stop();
    }

    // ------------------------------------------------------------------ azioni
    private void Navigate(string key)
    {
        // Lasciando YouTube la ricerca della barra torna quella della libreria (vuota e chiusa).
        if (YouTubeOpen && key is "Home" or "Movies" or "TV Series" or "Music" or "Videos" or "Photos" or "Favourites" or "WatchHistory" or "Playlists" or "Network")
        {
            _view = "Home";
            _ytTyping?.Stop();
            if (_searchBox != null) _searchBox.Text = "";
            _query = ""; _alsoIn.Clear();
            SetSearchOpen(false);
        }
        if (key is "Home" or "Movies" or "TV Series" or "Music" or "Videos" or "Photos" or "Favourites" or "WatchHistory" or "Playlists" or "Network") BeginPageTransition();
        if (key is "Home" or "Movies" or "TV Series" or "Music" or "Videos" or "Photos" or "Favourites" or "WatchHistory" or "Playlists" && _query.Length > 0)
        {
            _query = ""; _alsoIn.Clear();
            if (_searchBox != null) _searchBox.Text = "";
            SetSearchOpen(false);
        }
        switch (key)
        {
            case "Home": _view = "Home"; _category = "Movies"; break;
            case "Movies" or "TV Series" or "Music" or "Videos" or "Photos" or "Favourites" or "WatchHistory" or "Playlists":
                if (key == "Music") _musicOverview = true;
                // Le scelte rapide (Valutati, Lossless…) valgono per la sezione in cui sono state scelte.
                if (_category != key || _view != "Collection") _filter = "All";
                _view = "Collection"; _category = key; break;
            case "Spotlight": SpotlightRequested?.Invoke(); return;
            case "More":
                // Come ShellMoreItems di Windows: le sezioni, poi YouTube e "Apri link".
                OpenMenu("More", MoreKeys.Select(k => new MenuItem(NavLabel(k), CategoryOpen(k), () => Navigate(k)))
                    // Disco inserito ed Espelli, come su Windows (ShellMoreItems).
                    .Concat(MacDisc.Inserted().SelectMany(d => new[] { new MenuItem(d.Label, false, () => DiscOpenRequested?.Invoke(d.Root)), new MenuItem("Espelli disco", false, () => DiscEjectRequested?.Invoke(d.Root)) }))
                    .Concat(MacAudioCd.Inserted().SelectMany(cd => new[] { new MenuItem("CD audio · " + MacDisc.LabelAsTitle(System.IO.Path.GetFileName(cd)), false, () => DiscOpenRequested?.Invoke(cd)), new MenuItem("Espelli CD", false, () => DiscEjectRequested?.Invoke(cd)) }))
                    .Append(new MenuItem("YouTube", YouTubeOpen, OpenYouTube))
                    .Append(new MenuItem("Apri link", false, () => OpenLinkRequested?.Invoke())), new Point(_moreAnchor.X, _moreAnchor.Y + _moreAnchor.Height / 2 + S(26)));
                return;
            case "Sources":
            {
                // ShellSourceItems: il Mac, la pagina Rete e i server gia' usati.
                var items = new List<MenuItem>
                {
                    new("Mac", ActiveServer == null, () => SetSource(null)),
                    new("Rete", _view == "Network", () => Navigate("Network")),
                };
                foreach (var server in NetworkStore.Servers.Where(x => x.Account != null || NetworkStore.IsFavorite(x)).OrderBy(x => x.Name))
                    items.Add(new MenuItem(server.Name, ActiveServer?.Key == server.Key, () => OpenServer(server)));
                OpenMenu("Sources", items, new Point(_sourcesAnchor.X, _sourcesAnchor.Y + _sourcesAnchor.Height / 2 + S(26)));
                return;
            }
            case "Network":
                _view = "Network";
                if (!_discovering && (DateTime.UtcNow - _lastDiscovery).TotalSeconds > 30) _ = DiscoverAsync();
                InvalidateVisual();
                return;
            case "Options":
                var options = OptionsMenu().Select(o => new MenuItem(o.Label, false, o.Run)).ToList();
                OpenMenu("Options", options, new Point(_optionsAnchor.Center.X + S(10), _optionsAnchor.Bottom + Math.Round(TopShift) + S(10)));
                return;
        }
        _scroll = _scrollTarget = 0;
        Reload();
    }

    public event Action<bool>? ThemeToggleRequested;

    /// <summary>Voci del tasto Impostazioni in alto nella Home (anche quello di Spotlight apre queste).</summary>
    private List<(string Label, Action Run)> OptionsMenu()
    {
        bool light = UiTheme.IsLight;
        var options = new List<(string Label, Action Run)>
        {
            (light ? "Tema scuro" : "Tema chiaro", () => ThemeToggleRequested?.Invoke(!light)),
            ("Aggiungi cartella", OpenFolders),
            ("Scansiona", () => _ = ScanAsync()),
            ("Guida rapida", StartOnboarding),
            ("Abbina il telefono", () => PairingRequested?.Invoke()),
            ("Impostazioni", () => SettingsRequested?.Invoke()),
        };
        // Con un server come fonte le cartelle del Mac non c'entrano: niente Aggiungi cartella ne' Scansiona.
        if (ActiveServer != null) options.RemoveRange(1, 2);
        if (CinecorePlayer2025.Utilities.HiddenLibraryStore.Count > 0)
            options.Insert(options.Count - 1, ("Elementi nascosti", ShowHiddenItems));
        return options;
    }

    public List<MenuEntry> OptionsMenuEntries() => OptionsMenu().Select(o => new MenuEntry(o.Label, null, o.Run)).ToList();

    /// <summary>La cartella scelta va nella categoria aperta (Film se si e' nella Home), poi la scansione.</summary>
    private async Task AddFolderAsync()
    {
        if (PickFolder == null) return;
        string? folder = await PickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;
        string category = _view == "Collection" && LibraryStore.FolderCategories.Contains(_category) ? _category : "Movies";
        LibraryStore.AddRoot(category, folder);
        await ScanCategoryAsync(category);
    }

    private async Task ScanAsync()
    {
        string current = _category;
        foreach (var category in LibraryStore.FolderCategories.OrderByDescending(c => c == current))
            await ScanCategoryAsync(category);
        Reload();
    }

    private async Task ScanCategoryAsync(string category, string? volume = null, System.Threading.CancellationToken ct = default)
    {
        void Refresh()
        {
            if (ct.IsCancellationRequested) return;
            if (ActiveServer == null && (category == _category || category is "Movies" or "TV Series")) Reload();
            if (_foldersOpen && category == _foldersCategory) RefreshFolders();
        }
        await LibraryStore.ScanAsync(category, ct, volume,
            category == "Music" ? () => Dispatcher.UIThread.Post(Refresh) : null);
        Refresh();
    }

    private void Activate(Hit hit)
    {
        if (hit.Kind == "onboarding") { OnboardingActivate(hit); return; }
        if (PhotoActivate(hit)) return;
        if (MusicFullscreenActivate(hit)) return;
        if (MusicOverviewActivate(hit)) return;
        if (YouTubeOpen && YouTubeActivate(hit)) return;
        if (MusicActivate(hit, _mouse)) return;
        if (PlaylistActivate(hit)) return;
        if (_foldersOpen && hit.Kind != "nav" && FoldersActivate(hit)) return;
        if (_detail == null && _seriesName == null && SearchActivate(hit)) return;
        if (_seriesName != null && hit.Kind != "nav" && SeriesActivate(hit)) return;
        if (_detail != null && hit.Kind != "nav" && DetailActivate(hit)) return;
        switch (hit.Kind)
        {
            case "nav": Navigate(hit.Key); break;
            case "see-all": Navigate("Movies"); break;
            case var rate when rate.StartsWith("diary-rate:", StringComparison.Ordinal):
                if (hit.Item != null) SetDiaryRating(hit.Item.Path, int.Parse(rate["diary-rate:".Length..]));
                break;
            case "diary-header": DiaryHeaderActivate(hit.Key); break;
            case "poster" when _diaryRatingMode && _category == "WatchHistory" && hit.Item != null:
                _diaryRatingMode = false;
                ShowDiaryRatingChoices(hit.Item, new Point(hit.Bounds.X + hit.Bounds.Width * .2, hit.Bounds.Y + hit.Bounds.Height * .25));
                break;
            case "poster": case "hero-info":
                if (hit.Item is { Category: "TV Series" } episode) OpenSeries(episode);
                else if (hit.Item != null) OpenDetail(hit.Item);
                break;
            case "hero-play": case "resume":
                if (hit.Item != null) PlayRequested?.Invoke(hit.Item, ResumeOf(hit.Item)?.Position);
                break;
            case "resume-page": if (int.TryParse(hit.Key, out int page)) { _resumeOffset = page; InvalidateVisual(); } break;
            case "action":
                if (hit.Key == "add") OpenFolders();
                else if (hit.Key == "new-playlist") OpenPlaylistCreate(null);
                else if (hit.Key == "keychain-authorize" && !Keychain.Authorizing) KeychainAuthorizationRequested?.Invoke();
                else if (hit.Key == "net-retry" && ActiveServer is { } retryServer) _ = ConnectAsync(retryServer);
                break;
            case "net-add": OpenSignIn(null); break;
            case "net-scan": _ = DiscoverAsync(); break;
            case "net-open": if (NetworkStore.Servers.FirstOrDefault(x => x.Key == hit.Key) is { } server) OpenServer(server); break;
            case "net-fav": if (NetworkStore.Servers.FirstOrDefault(x => x.Key == hit.Key) is { } fav) { NetworkStore.ToggleFavorite(fav); InvalidateVisual(); } break;
            case "net-remove": if (NetworkStore.Servers.FirstOrDefault(x => x.Key == hit.Key) is { } gone) { if (ActiveServer?.Key == gone.Key) SetSource(null); NetworkStore.Remove(gone); InvalidateVisual(); } break;
        }
    }

    // ------------------------------------------------------------------ input
    // Zone sensibili al mouse dell'ultimo disegno (HoverZones): si ridisegna solo quando il mouse entra o esce da una
    // di queste zone o da una voce di menu, non piu' a ogni movimento.
    private readonly HoverZones _hoverZones = new();
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    /// <summary>Il mouse e' sopra <paramref name="r"/>; la zona si ricorda per sapere quando serve ridisegnare.</summary>
    private bool Hover(Rect r)
    {
        // Mouse "spento" (scheda aperta sopra, mouse sulla barra della musica): la pagina sotto non reagisce, le sue
        // zone non servono.
        if (_mouse.X > -9999) _hoverZones.Track(r);
        return r.Contains(_mouse);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        PointerMovedTo(e.GetPosition(this), e.GetCurrentPoint(this).Properties.IsLeftButtonPressed);
    }

    /// <summary>Solo per le prove (azione "mouse" dello script QA): il mouse si sposta in <paramref name="p"/>.</summary>
    internal void TestMouse(Point p) => PointerMovedTo(p, false);
    internal int QaInvalidations { get; private set; }
    internal (bool Changed, bool Differs) QaHoverProbe(Point p) => HoverProbe.Run(this, _hoverZones, p, m => _mouse = m);

    private void PointerMovedTo(Point now, bool leftPressed)
    {
        bool seriesLive = _seriesPointerLive;
        if (Math.Abs(now.X - _mouse.X) > 1 || Math.Abs(now.Y - _mouse.Y) > 1) _seriesPointerLive = true;
        _mouse = now;
        if (PhotoDragged(now, leftPressed)) return;
        if (QueuePointerMoved(now)) return;
        string barHover = _barHover; double barHoverSeek = _barHoverSeek;
        if (MusicBarPointerMoved(now)) return;
        // Icone e anteprima della timeline: si ridisegna solo la barra (anche questo prima mancava).
        if (barHover != _barHover || barHoverSeek != _barHoverSeek) InvalidateBar();
        MusicFullscreenHover(now);
        bool hand;
        int menuHover = _menuHover;
        if (_menu != null) { _menuHover = MenuHit(_mouse); hand = _menuHover >= 0 && _menu[_menuHover].Run != null; }
        else hand = HitAt(_mouse) is { } h && !h.Kind.EndsWith("-outside") && !h.Kind.EndsWith("-sheet");
        PrefetchDetailArt(_detail == null && _menu == null ? HitAt(_mouse)?.Item : null);
        var cursor = hand ? HandCursor : Cursor.Default;
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
        MiniEdgeCursor(_mouse);
        // Le Cartelle disegnano con il mouse trasformato nella loro scala: li' si ridisegna sempre.
        if (_foldersOpen || menuHover != _menuHover || seriesLive != _seriesPointerLive || _hoverZones.Changed(now)) { QaInvalidations++; InvalidateVisual(); }
    }

    // Copertina sotto il mouse per un attimo: si preparano gia' la foto grande e la locandina della scheda dettagli,
    // cosi' la scheda si apre con le immagini (prima arrivavano dopo, scaricate solo all'apertura).
    private LibraryItem? _prefetchItem;

    private void PrefetchDetailArt(LibraryItem? item)
    {
        if (item == null || item.Category is "Music" or "Photos") { _prefetchItem = null; return; }
        if (ReferenceEquals(item, _prefetchItem)) return;
        _prefetchItem = item;
        DispatcherTimer.RunOnce(() =>
        {
            if (!ReferenceEquals(_prefetchItem, item) || _detail != null) return;
            var bounds = new Rect(Bounds.Size);
            double scale = DetailScale(bounds);
            var modal = DetailSheetBounds(bounds, scale);
            int pending = _pendingImages;
            Image(item.WideArtPath ?? item.ArtPath, modal.Width);
            Image(item.ArtPath, Math.Max(1, Math.Round(236 * scale)));
            _pendingImages = pending; // il precaricamento non deve trattenere i cambi pagina
        }, TimeSpan.FromMilliseconds(220));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _mouse = new Point(-1000, -1000);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        Focus();
        var button = e.GetCurrentPoint(this).Properties;
        if (button.IsRightButtonPressed || (button.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            _contextPress = true;
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (MiniEdgePressed(p, e)) return;
        if (_signIn != null) { SignInPressed(p); return; }
        if (PlaylistCreatePressed(p)) return;
        if (_menu != null)
        {
            if (_menuCloseAt != default) return;
            int i = MenuHit(p);
            if (i >= 0) { if (_menu[i].Run is { } run) CloseMenu(run); }
            else CloseMenu();
            return;
        }
        CloseSearchIfOutside(p);
        if (PhotoPressed(p)) return;
        if (MusicBarPointerPressed(p, e.Pointer)) return;
        if (QueuePointerPressed(p, e.ClickCount)) return;
        if (_menu == null && !_queueOpen && MiniDragPressed(p, e)) return;
        var hit = HitAt(p);
        if (hit != null && hit.Kind.StartsWith("album-", StringComparison.Ordinal) && AlbumActivate(hit, e.ClickCount)) return;
        if (hit != null) Activate(hit);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        if (_menu == null && QueueWheel(e.Delta.Y)) return;
        if (_menu == null && MusicFullscreenWheel(e.Delta.Y)) return;
        if (_menu == null && _detail != null && _detailReviews) { _reviewsScroll = Math.Clamp(_reviewsScroll - e.Delta.Y * 60, 0, _reviewsScrollMax); InvalidateVisual(); return; }
        if (_foldersOpen && _menu == null) { _foldersScroll -= e.Delta.Y * 40; InvalidateVisual(); return; }
        if (_album != null && _menu == null) { _albumScroll -= e.Delta.Y * 40; InvalidateVisual(); return; }
        if (YouTubeOpen && _menu == null && !SheetOpen) { _ytScroll = Math.Clamp(_ytScroll - e.Delta.Y * 60, 0, _ytScrollMax); InvalidateVisual(); return; }
        if (_playlistOpen != null && _menu == null) { _playlistScroll -= e.Delta.Y * 40; InvalidateVisual(); return; }
        if (PhotoWheel(e)) return;
        if (_menu != null || SheetOpen || OnboardingActive || _view != "Collection") return;
        ScrollCollection(-e.Delta.Y * 60);
    }

    internal void ScrollCollection(double delta)
    {
        _scrollTarget = Math.Clamp(_scrollTarget + delta, 0, _scrollMax);
        _frames.Start();
    }

    /// <summary>Solo per le prove: una Home con 60 film finti, banner con le foto di riserva della sala.</summary>
    internal void TestHome()
    {
        string root = VideoHostClient.RepoRoot;
        string hero(int i) => System.IO.Path.Combine(MacTools.AssetsDirectory, "HeroPlaceholders", $"hero-placeholder-{i % 3 + 1}.jpg");
        string[] posters = { System.IO.Path.Combine(MacTools.AssetsDirectory, "Network", "network-hero.jpg"), System.IO.Path.Combine(root, "Splash", "logo.png") };
        _view = "Home"; _category = "Movies";
        _items = Enumerable.Range(0, 60).Select(i => new LibraryItem { Path = "qa-home-" + i, Title = "Un film di prova con un titolo lungo " + i, Category = "Movies", Year = 1990 + i % 30,
            ArtPath = posters[i % posters.Length], WideArtPath = hero(i), SortDateUtc = DateTime.UtcNow.AddMinutes(-i) }).ToList();
        BuildHeroDeck();
        // Ordine fisso: le catture di due versioni devono mostrare gli stessi titoli.
        _heroDeck = _items.Take(HeroDeckSize).ToList(); _heroIndex = 0; _heroPrevIndex = -1;
        _scroll = _scrollTarget = 0; InvalidateVisual();
    }

    /// <summary>Solo per le prove: passa al banner successivo (con la dissolvenza).</summary>
    internal void TestNextHero() => NextHero();

    internal void TestArtworkGrid()
    {
        string root = VideoHostClient.RepoRoot;
        string[] images = { System.IO.Path.Combine(MacTools.AssetsDirectory, "Network", "network-hero.jpg"), System.IO.Path.Combine(root, "Splash", "logo.png") };
        _view = "Collection"; _category = "Movies";
        _items = Enumerable.Range(0, 300).Select(i => new LibraryItem { Path = "qa-" + i, Title = "Titolo " + i, Category = "Movies", ArtPath = images[i % images.Length] }).ToList();
        _scroll = _scrollTarget = 0; InvalidateVisual();
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (_signIn != null && e.Key == Key.Escape) { CloseSignIn(null); e.Handled = true; return; }
        if (HandlePhotoKey(e)) return;
        if (_playlistCreate != null) { PlaylistCreateKey(e); e.Handled = true; return; }
        // Con un album aperto sopra la playlist (dai consigli), Esc chiude prima l'album.
        if (_playlistOpen != null && _album == null && e.Key == Key.Escape) { _playlistOpen = null; InvalidateVisual(); e.Handled = true; return; }
        if (HandleMusicKey(e)) return;
        if (_menu != null && e.Key == Key.Escape) { CloseMenu(); e.Handled = true; return; }
        if (_foldersOpen && e.Key == Key.Escape) { CloseFolders(); e.Handled = true; return; }
        if (!SheetOpen && _menu == null && HandleOnboardingKey(e)) return;
        if (_seriesName != null && e.Key == Key.Escape) { CloseSeries(); e.Handled = true; return; }
        if (_detail != null && e.Key == Key.Escape) { CloseDetail(); e.Handled = true; }
    }

    /// <summary>Solo per le prove: apre una sezione o un menu (CINECORE_LIBRARY="Movies", "Options"...).</summary>
    public void TestNavigate(string key)
    {
        // I menu dopo il primo disegno: la posizione delle voci della barra si conosce solo allora.
        if (key is "Options" or "More" or "Sources") DispatcherTimer.RunOnce(() => Navigate(key), TimeSpan.FromMilliseconds(800));
        else if (key == "Detail") DispatcherTimer.RunOnce(() => { if (_items.Count > 0) OpenDetail(_items.OrderByDescending(i => i.SortDateUtc).First()); }, TimeSpan.FromMilliseconds(2500));
        else if (key.StartsWith("Search:", StringComparison.Ordinal))
            DispatcherTimer.RunOnce(() => { SearchActivate(new Hit(default, "nav", "SearchOpen")); DispatcherTimer.RunOnce(() => { if (_searchBox != null) _searchBox.Text = key[7..]; }, TimeSpan.FromMilliseconds(400)); }, TimeSpan.FromMilliseconds(2500));
        else if (key == "Series")
        {
            // Aspetta che il catalogo (anche quello del server) sia arrivato.
            int tries = 0;
            DispatcherTimer? t = null;
            t = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
            {
                var ep = (ActiveServer is { } srv && NetworkStore.Catalog(srv.Key) is { } cat ? cat : LibraryStore.Items("TV Series")).FirstOrDefault(i => i.Category == "TV Series");
                if (ep != null || ++tries > 40) { t!.Stop(); if (ep != null) OpenSeries(ep); }
            });
            t.Start();
        }
        else if (key == "Folders") DispatcherTimer.RunOnce(OpenFolders, TimeSpan.FromMilliseconds(1500));
        else if (key == "SignIn") DispatcherTimer.RunOnce(() => OpenSignIn(NetworkStore.Servers.FirstOrDefault()), TimeSpan.FromMilliseconds(800));
        else Dispatcher.UIThread.Post(() => Navigate(key), DispatcherPriority.Background);
    }
}
