using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Spotlight (HUD/NetflixModePage*.cs di Windows): interfaccia a tutto schermo in stile sala. Ingresso con la scritta
/// illuminata, sfondo del titolo con la vignettatura cinematografica, a sinistra generi/titolo/dati/sinossi e i pulsanti,
/// in basso la fila delle locandine; frecce per scorrere, Invio per vedere, Esc per uscire.
/// </summary>
internal sealed class SpotlightView : Control
{
    private static readonly FontFamily Display = new(AppAssets.Root + "Assets/Fonts#Inter Display");
    private static readonly FontFamily Reading = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Semibold = new(Display, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Bold = new(Display, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface Regular = new(Reading, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface ReadingSemibold = new(Reading, FontStyle.Normal, FontWeight.SemiBold);

    private const double EntryMs = 640, LoadingMinimumMs = 650, BrowseMs = 380;

    public LibraryView? Library { get; set; }
    public Canvas? InputLayer { get; set; }
    public event Action<LibraryItem, double?>? PlayRequested;
    public event Action<LibraryItem>? QueueRequested;
    /// <summary>Tasto destro: titolo sotto il puntatore (null sul fondo) e punto del clic.</summary>
    public event Action<LibraryItem?, Point>? ContextMenuRequested;
    public event Action? CloseRequested;
    /// <summary>Libreria (false) o Rete (true) dalle due icone sopra la fila.</summary>
    public event Action<bool>? SourceRequested;
    /// <summary>Impostazioni dall'icona in alto (come nella Home).</summary>
    public event Action? SettingsRequested;
    /// <summary>Lo imposta la finestra: vero se un livello sopra Spotlight copre quel rettangolo (finestra).</summary>
    public Func<Rect, bool>? GlassBlocked { get; set; }
    private GlassSearchField? _searchField;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible) _searchField?.Hide();
    }

    private List<LibraryItem> _items = new();
    private readonly List<int> _filtered = new();
    private int _index, _first;
    private double _firstAnim = double.NaN, _rowAt;
    private bool _network;
    private string _query = "";
    private int _filterMode; // 0 tutti, 1 film, 2 serie
    private string? _queuedPath;
    private readonly Stopwatch _clock = new();
    private double _entryStartMs = -1, _browseStartMs = -1;
    private RenderTargetBitmap? _browseFrom;
    private int _browseShift;
    private Point _mouse = new(-1000, -1000);
    private int _hoverCard = -1;
    private readonly List<(Rect Rect, string Zone, int Item)> _hits = new();
    private bool _capturing;
    private TextBox? _searchBox;
    private bool _searchOpen;
    private double _searchAnimAt = -1;

    public SpotlightView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        LibraryView.ImageReady += () => { if (IsVisible) InvalidateVisual(); };
        UiTheme.Changed += () => { _composed.Clear(); InvalidateVisual(); };
    }

    public bool IsOpen => IsVisible;

    public void Open(List<LibraryItem> items, bool network)
    {
        _items = items; _network = network;
        _query = ""; _filterMode = 0; _index = 0; _first = 0; _firstAnim = double.NaN; _actionFocus = -1; _topFocus = -1;
        if (_searchBox != null) { _searchBox.Text = ""; _searchBox.IsVisible = false; }
        _searchOpen = false; _searchAnimAt = -1;
        Refilter();
        _clock.Restart(); _entryStartMs = -1; _browseStartMs = -1;
        _browseFrom?.Dispose(); _browseFrom = null;
        IsVisible = true;
        Focus();
        InvalidateVisual();
    }

    /// <summary>Stessi titoli dopo un cambio di fonte, senza rifare l'ingresso.</summary>
    public void Replace(List<LibraryItem> items, bool network)
    {
        _items = items; _network = network; _index = 0; _first = 0; _firstAnim = double.NaN;
        Refilter(); _composed.Clear(); InvalidateVisual();
    }

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        if (_searchBox != null) _searchBox.IsVisible = false;
        _browseFrom?.Dispose(); _browseFrom = null;
        CloseRequested?.Invoke();
    }

    private LibraryItem? Current => _filtered.Count == 0 ? null : _items[_filtered[Math.Clamp(_index, 0, _filtered.Count - 1)]];

    private void Refilter()
    {
        string selected = Current?.Path ?? "";
        _filtered.Clear();
        string[] tokens = Fold(_query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (_filterMode == 1 && item.Category != "Movies") continue;
            if (_filterMode == 2 && item.Category != "TV Series") continue;
            if (tokens.Length > 0)
            {
                string hay = Fold(string.Join(" ", Title(item), item.Year?.ToString() ?? "", string.Join(" ", item.Genres)));
                if (!tokens.All(hay.Contains)) continue;
            }
            _filtered.Add(i);
        }
        int keep = _filtered.FindIndex(i => _items[i].Path == selected);
        _index = keep >= 0 ? keep : 0;
        _first = Math.Clamp(_first, 0, Math.Max(0, _filtered.Count - 1)); _firstAnim = double.NaN; // nuovi risultati: niente scorrimento
        KeepSelectionVisible();
    }

    private static string Fold(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in text.Normalize(System.Text.NormalizationForm.FormD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string Title(LibraryItem item) => item.Category == "TV Series" ? item.SeriesTitle ?? item.Title : item.Title;

    // ------------------------------------------------------------------ misure (NetflixModePage.Layout.cs)
    // Stesse proporzioni del Mac anche su Windows (l'utente: "controlla com'e' fatta la versione mac os").
    private double UiScale => Math.Clamp(Math.Min(Bounds.Width / 1330, Bounds.Height / 787) * 1.18, 0.78, 2.8);
    private double D(double v) => Math.Max(1, Math.Round(v * UiScale));
    private double Pt(double pt) => Math.Max(7, pt * UiScale) * 96 / 72 * 0.92;
    private double HudMargin => Math.Clamp(Bounds.Width * .04, D(24), D(64));
    private double LibraryTop => Math.Max(D(380), Bounds.Height - D(250));
    private int VisibleColumns() => CardLayout().Columns;

    /// <summary>
    /// Fila di Spotlight come sul Mac: le colonne dipendono solo dalla larghezza e le card riempiono la riga da margine
    /// a margine. Con pochi titoli (un filtro con una serie sola) restano della stessa misura, allineate a sinistra:
    /// prima si ingrandivano e andavano al centro.
    /// </summary>
    private (int Columns, double CardW, double CardH, double Offset) CardLayout()
    {
        double margin = HudMargin, gap = D(14);
        int columns = (int)Math.Clamp((Bounds.Width - margin * 2 + gap) / Math.Max(210, D(224)), 2, 7);
        double cardW = (Bounds.Width - 2 * margin - (columns - 1) * gap) / columns, cardH = Math.Min(D(160), Math.Round(cardW * 9 / 16));
        return (columns, cardW, cardH, 0);
    }
    private static Color SpotInk => UiTheme.IsLight ? UiTheme.Text : Color.FromRgb(244, 248, 251);
    private static Color SpotBody => UiTheme.IsLight ? UiTheme.SubtleText : Color.FromRgb(218, 227, 235);
    private static Color SpotSoft => UiTheme.IsLight ? UiTheme.Muted : Color.FromRgb(172, 190, 206);
    private static Color A(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a, 0, 255), c.R, c.G, c.B);
    private static IBrush B(Color c) => UiText.Brush(c);

    private CachedText Text(string text, double pt, Typeface face, Color color, double maxWidth = double.PositiveInfinity, int lines = 1, TextAlignment align = TextAlignment.Left) =>
        UiText.Get(L10n.T(text), face, Pt(pt), color, maxWidth, lines, align);

    private void TextAt(DrawingContext ctx, string text, double pt, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = Text(text, pt, face, color, r.Width, 1, align);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    // ------------------------------------------------------------------ disegno
    public override void Render(DrawingContext ctx)
    {
        _hits.Clear();
        var page = new Rect(Bounds.Size);
        if (page.Width < 10 || page.Height < 10) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        ctx.FillRectangle(Brushes.Black, page);
        var item = Current;
        // Caricamento: la scritta finche' lo sfondo del primo titolo non e' pronto (almeno 650 ms, al massimo 3 s).
        if (_entryStartMs < 0)
        {
            bool ready = item == null || ComposedBackdrop(item) != null || now > 3000;
            if (now >= LoadingMinimumMs && ready) _entryStartMs = now;
            else
            {
                DrawQuietBackground(ctx, page);
                double intro = Math.Clamp(now / 450, 0, 1); intro = 1 - (1 - intro) * (1 - intro);
                DrawLoadingWord(ctx, page, now, intro, Math.Clamp(now / Math.Max(LoadingMinimumMs, 900), 0, .92));
                RequestFrame();
                return;
            }
        }
        double entry = Math.Clamp((now - _entryStartMs) / EntryMs, 0, 1);
        if (entry < 1 && !_capturing)
        {
            // Ingresso: la scritta si spegne in fretta, la pagina sale con un'uscita morbida.
            double caption = 1 - Math.Clamp(entry / .22, 0, 1); caption *= caption;
            double reveal = Math.Clamp((entry - .10) / .90, 0, 1); reveal = 1 - Math.Pow(1 - reveal, 3);
            DrawQuietBackground(ctx, page);
            if (caption > 0) DrawLoadingWord(ctx, page, now, caption * caption, null);
            using (ctx.PushOpacity(reveal)) DrawSurface(ctx, page, item);
            RequestFrame();
            return;
        }
        // Passaggio fra due titoli: si dissolvono solo sfondo e titolo (fotografati senza barra ne' fila); barra in alto e
        // fila delle copertine restano vive sopra e la fila scorre fino alla nuova posizione (prima dissolveva tutto e la
        // fila saltava di una colonna: sembrava tremare).
        DrawSurface(ctx, page, item, chrome: false);
        if (_browseFrom != null && !_capturing)
        {
            double t = Math.Clamp((now - _browseStartMs) / BrowseMs, 0, 1);
            if (t >= 1) { _browseFrom.Dispose(); _browseFrom = null; }
            else
            {
                double enter = Math.Clamp((t - .08) / .84, 0, 1); enter = enter * enter * (3 - 2 * enter);
                using (ctx.PushOpacity(1 - enter)) ctx.DrawImage(_browseFrom, new Rect(_browseFrom.Size), page);
                double dip = .08 * Math.Sin(Math.PI * t);
                if (dip > .01) ctx.FillRectangle(B(A(UiTheme.IsLight ? UiTheme.Panel : Color.FromRgb(3, 9, 16), 255 * dip)), page);
                RequestFrame();
            }
        }
        if (!_capturing && item != null) DrawChrome(ctx, page, item);
        if (!_capturing) Dispatcher.UIThread.Post(PrefetchNeighbours, DispatcherPriority.Background);
    }

    // [2026-11-23] Prima un timer da 16 ms ridisegnava tutta la pagina per tutto il tempo in cui Spotlight era aperto
    // (56% di un core da fermo). Ora ogni animazione in corso (ingresso, cambio titolo, fila, ricerca, cast) chiede
    // il fotogramma successivo; finite, la pagina resta ferma.
    private bool _framePosted;
    private void RequestFrame()
    {
        if (_framePosted) return;
        _framePosted = true;
        Dispatcher.UIThread.Post(() => { _framePosted = false; InvalidateVisual(); }, DispatcherPriority.Render);
    }

    private void DrawSurface(DrawingContext ctx, Rect page, LibraryItem? item, bool chrome = true)
    {
        if (item == null) { DrawEmpty(ctx, page); return; }
        DrawBackdrop(ctx, page, item);
        DrawHero(ctx, page, item);
        if (chrome) DrawChrome(ctx, page, item);
    }

    /// <summary>Barra in alto, fila delle copertine e scheda del cast: sopra la dissolvenza fra i titoli.</summary>
    private void DrawChrome(DrawingContext ctx, Rect page, LibraryItem item)
    {
        DrawTopBar(ctx, page);
        DrawLibrary(ctx, page);
        if (_castOpen) DrawCastSheet(ctx, page, item);
    }

    private void DrawQuietBackground(DrawingContext ctx, Rect page)
    {
        bool light = UiTheme.IsLight;
        Color edge = light ? UiTheme.Panel : Color.FromRgb(4, 8, 13);
        Color mid = light ? UiTheme.Mix(UiTheme.Panel, Colors.White, .04) : Color.FromRgb(14, 22, 32);
        ctx.FillRectangle(new RadialGradientBrush
        {
            Center = new RelativePoint(.5, .32, RelativeUnit.Relative), GradientOrigin = new RelativePoint(.5, .32, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(.7, RelativeUnit.Relative), RadiusY = new RelativeScalar(.6, RelativeUnit.Relative),
            GradientStops = { new GradientStop(mid, 0), new GradientStop(edge, 1) },
        }, page);
    }

    /// <summary>La scritta "Spotlight" accesa da una luce che scorre, il messaggio e la barra d'avanzamento.</summary>
    private void DrawLoadingWord(DrawingContext ctx, Rect page, double t, double alpha, double? progress)
    {
        if (alpha <= .005) return;
        double w = page.Width, h = page.Height, cy = h * .46;
        double em = Math.Max(D(40), h * .085);
        var word = new FormattedText(L10n.T("Spotlight"), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, em * 1.06, Brushes.White);
        var origin = new Point((w - word.Width) / 2, cy - word.Height / 2);
        var geometry = word.BuildGeometry(origin);
        if (geometry == null) return;
        ctx.DrawGeometry(B(A(SpotInk, (UiTheme.IsLight ? 46 : 40) * alpha)), null, geometry);
        double spotX = w / 2 + Math.Sin(t / 1100.0) * w * .20, spotY = cy + Math.Cos(t / 1500.0) * h * .06;
        double radius = Math.Max(D(140), h * .34);
        ctx.DrawGeometry(new RadialGradientBrush
        {
            Center = new RelativePoint(spotX, spotY, RelativeUnit.Absolute), GradientOrigin = new RelativePoint(spotX, spotY, RelativeUnit.Absolute),
            RadiusX = new RelativeScalar(radius * .95, RelativeUnit.Absolute), RadiusY = new RelativeScalar(radius * .8, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(A(SpotInk, 255 * alpha), 0), new GradientStop(A(SpotInk, 120 * alpha), .45), new GradientStop(A(SpotInk, 0), 1) },
        }, null, geometry);
        var message = new Rect(D(40), cy + h * .085, Math.Max(1, w - D(80)), D(36));
        TextAt(ctx, _network ? "Preparo i titoli del server…" : "Preparo i titoli della libreria…", 10, Regular, A(SpotSoft, 255 * alpha), message, TextAlignment.Center);
        if (progress is not double p) return;
        double barW = Math.Min(D(360), w / 3), barH = Math.Max(2, D(3));
        var track = new Rect((w - barW) / 2, message.Bottom + D(16), barW, barH);
        ctx.FillRectangle(B(A(SpotInk, (UiTheme.IsLight ? 34 : 40) * alpha)), track, (float)(barH / 2));
        if (barW * p >= barH) ctx.FillRectangle(B(A(SpotInk, 235 * alpha)), new Rect(track.X, track.Y, barW * p, barH), (float)(barH / 2));
    }

    // ----- sfondo del titolo con la vignettatura (CinematicVignette.Apply, card: false), composto una volta -----
    private readonly Dictionary<string, (PixelSize Size, WriteableBitmap? Bitmap)> _composed = new();
    private readonly HashSet<string> _composing = new();

    private WriteableBitmap? ComposedBackdrop(LibraryItem item)
    {
        var top = TopLevel.GetTopLevel(this);
        double scaling = Math.Min(2, top?.RenderScaling ?? 1);
        var size = new PixelSize(Math.Max(1, (int)(Bounds.Width * scaling)), Math.Max(1, (int)(Bounds.Height * scaling)));
        string key = item.Path + "|" + UiTheme.IsLight;
        if (_composed.TryGetValue(key, out var ready) && ready.Size == size) return ready.Bitmap;
        if (_composing.Contains(key)) return null;
        // Tutto in sottofondo con Skia (decodifica, ritaglio, sfocatura, vignettatura): prima si disegnava a tutta
        // finestra sul thread dell'interfaccia e si rileggeva dalla scheda video, 100-150 ms fermi a ogni titolo.
        if (StartComposeInBackground(item, key, size, scaling)) return null;
        string? art = item.WideArtPath ?? item.ArtPath;
        var source = Library?.LoadImage(art, Bounds.Width);
        if (source == null || _composing.Contains(key)) return null;
        bool landscape = source.PixelSize.Width >= source.PixelSize.Height * 1.25;
        // Si disegna l'immagine alla misura della finestra (sul thread grafico, pochi ms) e la vignettatura si calcola
        // pixel per pixel fuori, come su Windows.
        var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var dc = rtb.CreateDrawingContext())
        using (dc.PushTransform(Matrix.CreateScale(scaling, scaling)))
        {
            var bounds = new Rect(Bounds.Size);
            dc.FillRectangle(B(UiTheme.IsLight ? UiTheme.Panel : Color.FromRgb(3, 9, 16)), bounds);
            if (landscape) DrawCoverImage(dc, source, bounds, .5, .46);
            else DrawPosterBackdrop(dc, source, bounds);
        }
        var pixels = new byte[size.Width * size.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { rtb.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, size.Width * 4); }
        finally { handle.Free(); rtb.Dispose(); }
        Color? shade = UiTheme.IsLight ? UiTheme.Panel : null;
        _composing.Add(key);
        Task.Run(() =>
        {
            ApplyVignette(pixels, size.Width, size.Height, shade);
            Dispatcher.UIThread.Post(() =>
            {
                _composing.Remove(key);
                var bmp = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = bmp.Lock())
                    for (int y = 0; y < size.Height; y++) Marshal.Copy(pixels, y * size.Width * 4, fb.Address + y * fb.RowBytes, size.Width * 4);
                if (_composed.Count > 12) _composed.Clear();
                _composed[key] = (size, bmp);
                InvalidateVisual();
            });
        });
        return null;
    }

    /// <summary>Sfondo del titolo composto fuori dal thread dell'interfaccia (falso se l'immagine non e' un file locale).</summary>
    private bool StartComposeInBackground(LibraryItem item, string key, PixelSize size, double scaling)
    {
        string? art = item.WideArtPath ?? item.ArtPath;
        if (string.IsNullOrEmpty(art) || !System.IO.File.Exists(art)) return false;
        // Misure prese qui (dipendono dalla vista), il lavoro in sottofondo.
        double pageW = Bounds.Width, pageH = Bounds.Height, margin = HudMargin, d60 = D(60), d6 = D(6);
        bool light = UiTheme.IsLight;
        var bgColor = light ? UiTheme.Panel : Color.FromRgb(3, 9, 16);
        Color? shade = light ? UiTheme.Panel : null;
        _composing.Add(key);
        Task.Run(() =>
        {
            byte[]? pixels = null;
            try
            {
                using var src = SkiaSharp.SKBitmap.Decode(art);
                if (src != null)
                {
                    var info = new SkiaSharp.SKImageInfo(size.Width, size.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                    using var surface = SkiaSharp.SKSurface.Create(info);
                    var c = surface.Canvas;
                    c.Clear(new SkiaSharp.SKColor(bgColor.R, bgColor.G, bgColor.B));
                    c.Scale((float)scaling);
                    using var high = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.High, IsAntialias = true };
                    void Cover(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect dest, double fx, double fy, SkiaSharp.SKPaint paint)
                    {
                        double sw = src.Width, sh = src.Height, k = Math.Max(dest.Width / sw, dest.Height / sh);
                        double cw = Math.Min(sw, dest.Width / k), ch = Math.Min(sh, dest.Height / k);
                        float sx = (float)Math.Clamp((sw - cw) * fx, 0, sw - cw), sy = (float)Math.Clamp((sh - ch) * fy, 0, sh - ch);
                        canvas.DrawBitmap(src, new SkiaSharp.SKRect(sx, sy, sx + (float)cw, sy + (float)ch), dest, paint);
                    }
                    var page = new SkiaSharp.SKRect(0, 0, (float)pageW, (float)pageH);
                    if (src.Width >= src.Height * 1.25) Cover(c, page, .5, .46, high);
                    else
                    {
                        // Locandina: sfocata e scura a tutta pagina (ridotta a pochi pixel e riallargata), quella vera a destra.
                        int smallH = Math.Max(2, (int)Math.Round(24 * pageH / Math.Max(1, pageW)));
                        using var small = new SkiaSharp.SKBitmap(24, smallH);
                        using (var sc = new SkiaSharp.SKCanvas(small)) Cover(sc, new SkiaSharp.SKRect(0, 0, 24, smallH), .5, .4, high);
                        using var veil = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.High, Color = new SkiaSharp.SKColor(255, 255, 255, 140) };
                        c.DrawBitmap(small, page, veil);
                        double h = pageH * .54, w = h * src.Width / Math.Max(1, src.Height);
                        var rect = SkiaSharp.SKRect.Create((float)(pageW - margin - w - d60), (float)(pageH * .1), (float)w, (float)h);
                        c.Save();
                        c.ClipRoundRect(new SkiaSharp.SKRoundRect(rect, (float)d6, (float)d6), antialias: true);
                        Cover(c, rect, .5, .5, high);
                        c.Restore();
                    }
                    pixels = new byte[size.Width * size.Height * 4];
                    var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try { surface.ReadPixels(info, handle.AddrOfPinnedObject(), size.Width * 4, 0, 0); }
                    finally { handle.Free(); }
                    ApplyVignette(pixels, size.Width, size.Height, shade);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("[spotlight] sfondo: " + ex.Message); pixels = null; }
            Dispatcher.UIThread.Post(() =>
            {
                _composing.Remove(key);
                if (pixels == null) return;
                var bmp = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = bmp.Lock())
                    for (int y = 0; y < size.Height; y++) Marshal.Copy(pixels, y * size.Width * 4, fb.Address + y * fb.RowBytes, size.Width * 4);
                if (_composed.Count > 12) _composed.Clear();
                _composed[key] = (size, bmp);
                InvalidateVisual();
            }, DispatcherPriority.Background);
        });
        return true;
    }

    /// <summary>Gli sfondi dei titoli vicini si preparano in anticipo: il passo al successivo e' immediato.</summary>
    private void PrefetchNeighbours()
    {
        if (_filtered.Count == 0 || _capturing) return;
        foreach (int d in new[] { 1, -1, 2 })
        {
            int pos = _index + d;
            if (pos < 0 || pos >= _filtered.Count) continue;
            ComposedBackdrop(_items[_filtered[pos]]);
        }
    }

    private static float Smooth(float a, float b, float v) { float t = Math.Clamp((v - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    private static void ApplyVignette(byte[] p, int width, int height, Color? shade)
    {
        var horizontal = new float[width]; var vertical = new float[height];
        for (int x = 0; x < width; x++) { float t = x / (float)Math.Max(1, width - 1); horizontal[x] = .01f + .87f * Smooth(.23f, .87f, t); }
        for (int y = 0; y < height; y++) { float t = y / (float)Math.Max(1, height - 1); vertical[y] = (1f - .42f * (1f - Smooth(0, .18f, t))) * (1f - Smooth(.42f, .97f, t)); }
        float[] tone = shade is { } s ? new float[] { s.B, s.G, s.R } : new float[] { 16, 9, 3 };
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (p[i + 3] == 0) continue;
                float transmission = horizontal[x] * vertical[y];
                uint hash = unchecked((uint)(x * 374761393 + y * 668265263));
                hash = unchecked((hash ^ (hash >> 13)) * 1274126177u); hash ^= hash >> 16;
                float noise = ((hash & 65535) / 65535f - .5f) * 1.6f, alpha = p[i + 3] / 255f;
                for (int c = 0; c < 3; c++)
                {
                    float tn = tone[c] * alpha, value = tn + (p[i + c] - tn) * transmission + noise * alpha;
                    p[i + c] = (byte)Math.Clamp((int)(value + .5f), 0, p[i + 3]);
                }
            }
    }

    private static void DrawCoverImage(DrawingContext ctx, Bitmap bmp, Rect dest, double fx, double fy)
    {
        double sw = bmp.Size.Width, sh = bmp.Size.Height;
        if (sw <= 0 || sh <= 0) return;
        double k = Math.Max(dest.Width / sw, dest.Height / sh), cw = Math.Min(sw, dest.Width / k), ch = Math.Min(sh, dest.Height / k);
        var src = new Rect(Math.Clamp((sw - cw) * fx, 0, sw - cw), Math.Clamp((sh - ch) * fy, 0, sh - ch), cw, ch);
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            ctx.DrawImage(bmp, src, dest);
    }

    /// <summary>Senza immagine orizzontale: la locandina sfocata e scura riempie, quella vera a destra come una copertina.</summary>
    private void DrawPosterBackdrop(DrawingContext ctx, Bitmap poster, Rect page)
    {
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality }))
        using (ctx.PushOpacity(.55))
        {
            // Sfocatura economica: la locandina ridotta a pochi pixel e riallargata.
            var small = new RenderTargetBitmap(new PixelSize(24, Math.Max(2, (int)Math.Round(24 * page.Height / Math.Max(1, page.Width)))), new Vector(96, 96));
            using (var sg = small.CreateDrawingContext()) DrawCoverImage(sg, poster, new Rect(small.Size), .5, .4);
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(small, new Rect(small.Size), page);
        }
        double h = page.Height * .54, w = h * poster.Size.Width / Math.Max(1, poster.Size.Height);
        var rect = new Rect(page.Width - HudMargin - w - D(60), page.Height * .1, w, h);
        using (ctx.PushGeometryClip(new RectangleGeometry(rect, D(6), D(6))))
            DrawCoverImage(ctx, poster, rect, .5, .5);
    }

    private void DrawBackdrop(DrawingContext ctx, Rect page, LibraryItem item)
    {
        var composed = ComposedBackdrop(item);
        if (composed != null) { ctx.DrawImage(composed, new Rect(composed.Size), page); return; }
        // In preparazione: il fondo tranquillo (mai un'immagine senza velo sotto i testi).
        DrawQuietBackground(ctx, page);
    }

    // ----- barra in alto: ricerca, filtri e preferito (NetflixModePage.Search.cs) -----
    private Rect _searchIcon, _favoriteRect, _searchPill, _settingsRect;

    private void DrawTopBar(DrawingContext ctx, Rect page)
    {
        double top = D(16), size = D(40), margin = HudMargin;
        // Da destra: Impostazioni (come nella Home), preferito, ricerca.
        _settingsRect = new Rect(page.Width - margin - size, top, size, size);
        DrawIcon(ctx, _settingsRect, "settings", Hot("settings"));
        DrawTopRing(ctx, _settingsRect, "settings");
        _hits.Add((_settingsRect, "settings", -1));
        _favoriteRect = new Rect(_settingsRect.X - D(8) - size, top, size, size);
        _searchIcon = new Rect(_favoriteRect.X - D(8) - size, top, size, size);
        var item = Current;
        bool favourite = item != null && Favorites.Contains(item.Path);
        DrawStar(ctx, _favoriteRect, favourite, Hot("favorite"));
        DrawTopRing(ctx, _favoriteRect, "favorite");
        _hits.Add((_favoriteRect, "favorite", -1));
        // Chiusa all'ingresso (solo la lente); si apre e si chiude in 220 ms.
        double progress = _searchAnimAt < 0 ? 1 : Math.Clamp((_clock.Elapsed.TotalMilliseconds - _searchAnimAt) / 220, 0, 1);
        if (progress < 1) RequestFrame();
        double a = _searchOpen ? progress : 1 - progress;
        double eased = 1 - Math.Pow(1 - a, 3);
        if (eased < .01)
        {
            _searchField?.Hide();
            DrawIcon(ctx, _searchIcon, "search", _query.Length > 0 || Hot("search"));
            DrawTopRing(ctx, _searchIcon, "search");
            _hits.Add((_searchIcon, "search", -1));
            _searchPlace = null;
            QueuePlaceSearch();
            return;
        }
        // Pillola che cresce verso sinistra dal bordo destro, con i filtri accanto.
        double fullW = Math.Min(D(420), page.Width * .36);
        double growW = Math.Max(size, fullW * (.55 + .45 * eased));
        _searchPill = new Rect(_searchIcon.Right - growW, top, growW, size);
        // Vetro vero di macOS: lente, testo e cursore sopra il vetro (GlassSearchField); altrimenti la pillola disegnata.
        _searchField ??= new GlassSearchField(this);
        bool native = !_capturing && _searchField.ToWindow(_searchPill) is { } pw && !(GlassBlocked?.Invoke(pw) ?? false) &&
            _searchField.Show(_searchPill, eased, _searchBox?.IsVisible == true ? _searchBox : null,
                new Rect(_searchPill.X + D(44), _searchPill.Y, Math.Max(1, _searchPill.Width - D(58)), size),
                new Rect(_searchPill.X + D(14), _searchPill.Y + (size - D(18)) / 2, D(18), D(18)), "Cerca titolo o genere",
                Reading, Pt(10.2), SpotInk, A(SpotSoft, 220));
        if (!native)
        {
            _searchField.Hide();
            // Vetro dal vivo come la barra della musica; il velo pieno solo nelle foto della pagina.
            bool dark = !UiTheme.IsLight;
            Color fallback = dark ? Color.FromRgb(24, 27, 33) : Color.FromRgb(246, 247, 249);
            if (_capturing || LibraryView.QaNoGlass) ctx.DrawRectangle(B(A(fallback, 230 * eased)), null, _searchPill, size / 2, size / 2);
#if CINECORE_WINDOWS
            else ctx.Custom(BackdropGlassOp.Liquid(_searchPill, size / 2, eased, .5));
#else
            else ctx.Custom(new BackdropGlassOp(_searchPill, size / 2, 30, dark ? .8 : .85, dark ? .6 : .38, dark ? .045 : .6, fallback, eased));
            using (ctx.PushOpacity(eased)) LibraryView.DrawGlassRim(ctx, _searchPill, size / 2, !dark);
#endif
            if (_searchBox?.IsFocused == true)
                ctx.DrawRectangle(null, new Pen(B(A(SpotInk, 70 * eased)), 1), _searchPill.Deflate(.5), size / 2, size / 2);
            SvgIcon.Get("search")?.Draw(ctx, new Rect(_searchPill.X + D(14), _searchPill.Y + (size - D(18)) / 2, D(18), D(18)), A(SpotInk, 200 * eased));
            if (_query.Length == 0)
                TextAt(ctx, "Cerca titolo o genere", 10.2, Regular, A(SpotSoft, 220 * eased), new Rect(_searchPill.X + D(44), _searchPill.Y, _searchPill.Width - D(58), size));
        }
        DrawTopRing(ctx, _searchPill, "search-pill");
        _hits.Add((_searchPill, "search-pill", -1));
        // Filtri: Tutti, Film, Serie TV.
        double fx = _searchPill.X - D(12);
        string[] labels = { "Serie TV", "Film", "Tutti" };
        for (int i = 0; i < labels.Length; i++)
        {
            int mode = 2 - i;
            var ft = Text(labels[i], 9.4, ReadingSemibold, A(_filterMode == mode ? SpotInk : SpotSoft, (_filterMode == mode ? 255 : 200) * eased));
            var r = new Rect(fx - ft.Width - D(24), top + (size - D(32)) / 2, ft.Width + D(24), D(32));
            if (_filterMode == mode) ctx.DrawRectangle(B(A(SpotInk, (UiTheme.IsLight ? 22 : 34) * eased)), null, r, r.Height / 2, r.Height / 2);
            ctx.DrawText(ft, new Point(r.X + D(12), r.Y + (r.Height - ft.Height) / 2));
            DrawTopRing(ctx, r, "filter:" + mode);
            _hits.Add((r, "filter", mode));
            fx = r.X - D(4);
        }
        _searchPlace = eased > .9 ? _searchPill : null;
        QueuePlaceSearch();
    }

    // La casella vera (TextBox) non si tocca durante il disegno (Avalonia lo vieta: era il crash di Spotlight):
    // il disegno annota dove va e la si sposta subito dopo.
    private Rect? _searchPlace;
    private bool _placeQueued;

    private void QueuePlaceSearch()
    {
        if (_placeQueued || _capturing) return;
        _placeQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _placeQueued = false;
            if (_searchBox == null) return;
            bool show = IsVisible && _searchPlace.HasValue;
            if (_searchBox.IsVisible != show) _searchBox.IsVisible = show;
            if (!show) return;
            var pill = _searchPlace!.Value;
            double h = Pt(10.2) * 1.6;
            Canvas.SetLeft(_searchBox, pill.X + D(44)); Canvas.SetTop(_searchBox, pill.Y + (pill.Height - h) / 2);
            _searchBox.Width = Math.Max(1, pill.Width - D(58)); _searchBox.Height = h;
            bool glass = _searchField?.On == true;
            _searchBox.FontSize = Pt(10.2);
            _searchBox.Foreground = glass ? Brushes.Transparent : B(SpotInk); _searchBox.CaretBrush = glass ? Brushes.Transparent : B(SpotInk);
            _searchBox.SelectionBrush = glass ? Brushes.Transparent : null;
        }, DispatcherPriority.Render);
    }

    private bool Hot(string zone) => _hits.Count >= 0 && (_hoverZone == zone || _actionFocus >= 0 && ActionZones().ElementAtOrDefault(_actionFocus) == zone || TopFocused(zone));

    // Tastiera e telecomando: dal carosello (non dentro un film) su porta alla riga in alto: ricerca, filtri (con la
    // ricerca aperta), preferito, impostazioni. Sinistra/destra la scorrono, OK preme, giu' o Indietro tornano giu'.
    private int _topFocus = -1;
    private List<string> TopZones()
    {
        var zones = new List<string>();
        if (_searchOpen) { zones.Add("search-pill"); zones.Add("filter:0"); zones.Add("filter:1"); zones.Add("filter:2"); }
        else zones.Add("search");
        zones.Add("favorite"); zones.Add("settings");
        return zones;
    }
    private bool TopFocused(string zone) => _topFocus >= 0 && TopZones().ElementAtOrDefault(_topFocus) == zone;

    /// <summary>Contorno a capsula (come i pulsanti) attorno all'elemento in alto scelto con tastiera o telecomando.</summary>
    private void DrawTopRing(DrawingContext ctx, Rect r, string zone)
    {
        if (!TopFocused(zone)) return;
        double ring = Math.Max(2, D(2.4));
        var outline = r.Deflate(ring / 2);
        ctx.DrawRectangle(null, new Pen(B(SpotInk), ring), outline, outline.Height / 2, outline.Height / 2);
    }

    private void ActivateTop(string zone)
    {
        switch (zone)
        {
            case "search": ToggleSearch(true); _topFocus = 0; break;
            case "search-pill": _topFocus = -1; _searchBox?.Focus(); break;
            case "favorite": if (Current is { } f) Favorites.Toggle(f.Path); break;
            case "settings": SettingsRequested?.Invoke(); break;
            default:
                if (zone.StartsWith("filter:", StringComparison.Ordinal)) { _filterMode = int.Parse(zone[7..]); Refilter(); }
                break;
        }
        InvalidateVisual();
    }

    /// <summary>Dove sta il tasto Impostazioni (per aprire li' sotto lo stesso menu della Home).</summary>
    public Rect SettingsButtonBounds => _settingsRect;

    // Telecomando e tastiera: OK sulla locandina porta alla fila dei pulsanti (Riproduci, Ricomincia, In coda, Cast);
    // sinistra/destra li scorrono, OK li preme, giu' (o Indietro) torna al carosello. -1 = carosello.
    private int _actionFocus = -1;
    private List<string> ActionZones()
    {
        var zones = new List<string> { "play" };
        if (Current is { } item && Library?.ResumeFor(item) != null) zones.Add("restart");
        zones.Add("queue"); zones.Add("cast");
        return zones;
    }

    private void ActivateAction(string zone)
    {
        switch (zone)
        {
            case "play": OpenSelected(); break;
            case "restart": OpenSelected(restart: true); break;
            case "queue": if (Current is { } q) { _queuedPath = q.Path; QueueRequested?.Invoke(q); } break;
            case "cast": OpenCast(); break;
        }
    }
    private string _hoverZone = "";

    /// <summary>DrawTopIcon di Windows: icona senza riquadro, piena se attiva o sotto il puntatore.</summary>
    private void DrawIcon(DrawingContext ctx, Rect rect, string icon, bool active)
    {
        double size = D(22);
        WinSymbol.Draw(ctx, new Rect(rect.Center.X - size / 2, rect.Center.Y - size / 2, size, size), icon, A(SpotInk, active ? 255 : 150));
    }

    private void DrawStar(DrawingContext ctx, Rect rect, bool filled, bool hot)
    {
        double size = D(22);
        var pts = new Point[10];
        for (int i = 0; i < 10; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / 5, r = (i % 2 == 0 ? .5 : .22) * size;
            pts[i] = new Point(rect.Center.X + Math.Cos(a) * r, rect.Center.Y + .04 * size + Math.Sin(a) * r);
        }
        var star = new PolylineGeometry(pts, true);
        Color ink = A(SpotInk, filled || hot ? 255 : 150);
        // Come Windows: stella piena se preferito, altrimenti l'icona "star".
        if (filled) ctx.DrawGeometry(B(ink), null, star);
        else WinSymbol.Draw(ctx, new Rect(rect.Center.X - size / 2, rect.Center.Y - size / 2, size, size), "star", ink);
    }

    // ----- blocco del titolo (DrawHero) -----
    private Rect _playRect, _restartRect, _queueRect, _castRect;

    private void DrawHero(DrawingContext ctx, Rect page, LibraryItem item)
    {
        double left = HudMargin;
        bool compact = page.Height < D(720);
        double width = Math.Min(D(640), page.Width * (page.Width < page.Height * 1.35 ? .84 : .5));
        string raw = Title(item);
        var title = Text(raw, compact ? 28 : 34, Semibold, SpotInk, width, 2);
        string bodyText = string.IsNullOrWhiteSpace(item.Overview) ? "Sinossi non disponibile per questo titolo." : item.Overview!.Trim();
        var body = Text(bodyText, 11.6, Regular, SpotBody, Math.Min(width, D(560)), 4);
        // Il blocco si centra fra la barra in alto e la fila delle locandine, con la sua altezza vera: prima contava
        // sempre la riga di qualita' e audio e la trama intera, il testo saliva e sopra la fila restava un vuoto.
        bool hasTech = !string.IsNullOrWhiteSpace(item.Is4K || item.IsHdr ? "x" : item.ResolutionLabel) || !string.IsNullOrWhiteSpace(item.AudioLabel);
        double bodyRoom = Math.Max(0, LibraryTop - D(96) - D(26) - (D(92) + title.Height + D(14) + D(28) + (hasTech ? D(32) : 0) + D(20)));
        double bodyShown = Math.Min(body.Height, bodyRoom);
        double block = (item.Genres.Count > 0 ? D(30) : 0) + title.Height + D(14) + D(28) + (hasTech ? D(32) : 0) + D(20) + bodyShown + D(30) + D(48);
        double top = D(92) + Math.Max(0, (LibraryTop - D(44) - D(92) - block) / 2) + (item.Genres.Count > 0 ? D(30) : 0);
        if (item.Genres.Count > 0)
            TextAt(ctx, string.Join("  ·  ", item.Genres.Take(3)).ToUpperInvariant(), 8.6, ReadingSemibold, SpotSoft, new Rect(left, top - D(30), width, D(22)));
        ctx.DrawText(title, new Point(left - D(2), top));
        // Dati: anno, durata, generi e voto con un punto fra le voci; sotto, qualita' e audio.
        double metaTop = top + title.Height + D(14), metaH = D(28), mx = left, gap = D(14);
        var parts = new List<string>();
        if (item.Year is int year) parts.Add(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (item.DurationMinutes is > 0) { int m = (int)Math.Ceiling(item.DurationMinutes.Value); parts.Add(m >= 60 ? $"{m / 60}h {m % 60:00}m" : $"{m}m"); }
        if (item.Category == "TV Series") parts.Add("Serie TV");
        if (item.Rating is > 0) parts.Add("TMDb " + item.Rating.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ','));
        for (int i = 0; i < parts.Count; i++)
        {
            var ft = Text(parts[i], 10.6, Regular, SpotBody);
            if (mx + ft.Width > left + width) break;
            if (i > 0) { double dot = Math.Max(3, D(4)); ctx.DrawEllipse(B(A(SpotSoft, 150)), null, new Point(mx + gap, metaTop + metaH / 2), dot / 2, dot / 2); mx += gap * 2; }
            ctx.DrawText(ft, new Point(mx, metaTop + (metaH - ft.Height) / 2));
            mx += ft.Width;
        }
        double techTop = metaTop + metaH + D(8), techH = D(24), tx = left;
        string? quality = item.Is4K ? (item.IsHdr ? "4K HDR" : "4K") : item.IsHdr ? "HDR" : item.ResolutionLabel;
        if (!string.IsNullOrWhiteSpace(quality))
        {
            var ft = Text(quality!, 8.2, ReadingSemibold, SpotInk);
            var badge = new Rect(tx, techTop, ft.Width + D(16), techH);
            ctx.DrawRectangle(B(A(SpotInk, UiTheme.IsLight ? 22 : 36)), null, badge, D(5), D(5));
            ctx.DrawText(ft, new Point(badge.X + D(8), badge.Y + (techH - ft.Height) / 2));
            tx = badge.Right + D(14);
        }
        if (!string.IsNullOrWhiteSpace(item.AudioLabel))
        {
            var ft = Text(item.AudioLabel!, 8.2, ReadingSemibold, SpotSoft);
            if (tx + ft.Width <= left + width) { ctx.DrawText(ft, new Point(tx, techTop + (techH - ft.Height) / 2)); tx += ft.Width; }
        }
        if (tx > left) metaH += D(8) + techH;
        double bodyTop = metaTop + metaH + D(20);
        double actionLimit = LibraryTop - D(96);
        if (bodyTop + body.Height <= actionLimit - D(26)) ctx.DrawText(body, new Point(left, bodyTop));
        else
        {
            body = Text(bodyText, 11.6, Regular, SpotBody, Math.Min(width, D(560)), Math.Max(1, (int)((actionLimit - D(26) - bodyTop) / Math.Max(1, Pt(11.6) * 1.25))));
            if (actionLimit - D(26) - bodyTop > Pt(11.6)) ctx.DrawText(body, new Point(left, bodyTop));
        }
        DrawHeroActions(ctx, Math.Min(actionLimit, bodyTop + body.Height + D(30)), item);
    }

    private void DrawHeroActions(DrawingContext ctx, double top, LibraryItem item)
    {
        double bh = D(42), gap = D(10);
        bool resume = Library?.ResumeFor(item) != null;
        _playRect = new Rect(HudMargin, top, D(136), bh);
        _restartRect = resume ? new Rect(_playRect.Right + gap, top, D(128), bh) : default;
        _queueRect = new Rect((resume ? _restartRect.Right : _playRect.Right) + gap, top, D(118), bh);
        _castRect = new Rect(_queueRect.Right + gap, top, D(104), bh);
        Button(ctx, _playRect, resume ? "Riprendi" : "Riproduci", "play", true, null);
        if (resume) Button(ctx, _restartRect, "Ricomincia", "restart", false, "reset");
        bool queued = _queuedPath == item.Path;
        Button(ctx, _queueRect, queued ? "Aggiunto" : "In coda", "queue", false, queued ? "check" : "add");
        Button(ctx, _castRect, "Cast", "cast", false, "person");
    }

    private void Button(DrawingContext ctx, Rect rect, string text, string zone, bool primary, string? icon, bool playGlyph = true)
    {
        bool hot = Hot(zone);
        var inner = rect.Deflate(1);
        Color primaryInk = UiTheme.IsLight ? Colors.White : Color.FromRgb(14, 16, 20);
        if (primary)
        {
            Color fill = UiTheme.IsLight ? (hot ? Color.FromRgb(40, 44, 50) : Color.FromRgb(22, 24, 28)) : (hot ? Colors.White : Color.FromRgb(236, 240, 244));
            ctx.DrawRectangle(B(fill), null, inner, inner.Height / 2, inner.Height / 2);
            double x = rect.X + D(24), cy = rect.Center.Y;
            if (playGlyph)
            {
                ctx.DrawGeometry(B(primaryInk), null, new PolylineGeometry(new[] { new Point(x, cy - D(7)), new Point(x, cy + D(7)), new Point(x + D(11), cy) }, true));
                TextAt(ctx, text, 9.6, Semibold, primaryInk, new Rect(rect.X + D(20), rect.Y, rect.Width - D(20), rect.Height), TextAlignment.Center);
            }
            else TextAt(ctx, text, 9.6, Semibold, primaryInk, rect, TextAlignment.Center);
        }
        else
        {
            Color ink = A(SpotInk, hot ? 255 : 205);
            var ft = Text(text, 9.6, Semibold, ink);
            double size = D(17), g = D(9), x = rect.X + (rect.Width - size - g - ft.Width) / 2;
            if (icon != null) WinSymbol.Draw(ctx, new Rect(x, rect.Center.Y - size / 2, size, size), icon, ink);
            ctx.DrawText(ft, new Point(x + size + g, rect.Y + (rect.Height - ft.Height) / 2));
        }
        // Tasto selezionato come sul Mac: una capsula col contorno pieno attorno (si capisce subito quale e').
        if (hot)
        {
            double ring = Math.Max(2, D(2.4));
            var outline = primary ? rect.Inflate(D(4)) : rect.Deflate(ring / 2);
            ctx.DrawRectangle(null, new Pen(B(SpotInk), ring), outline, outline.Height / 2, outline.Height / 2);
        }
        _hits.Add((rect, zone, -1));
    }

    /// <summary>I simboli di MusicTransportBar.DrawSymbol usati qui (24 unita'), disegnati a tratto.</summary>
    private static void DrawSymbol(DrawingContext ctx, Rect box, string icon, Color color)
    {
        double k = box.Width / 24;
        Point P(double x, double y) => new(box.X + x * k, box.Y + y * k);
        var pen = new Pen(B(color), Math.Max(1.2, 1.6 * k), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        switch (icon)
        {
            case "add": ctx.DrawLine(pen, P(12, 5), P(12, 19)); ctx.DrawLine(pen, P(5, 12), P(19, 12)); break;
            case "check": ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(5, 12.5), P(10, 17), P(19, 7) }, false)); break;
            case "reset":
                ctx.DrawGeometry(null, pen, new EllipseGeometry(new Rect(P(5, 5), P(19, 19))));
                ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(4, 4), P(5, 9), P(10, 8) }, false));
                break;
            case "person":
                ctx.DrawGeometry(null, pen, new EllipseGeometry(new Rect(P(8.5, 4), P(15.5, 11))));
                ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(4.5, 20), P(6, 15.5), P(9.5, 13.5), P(14.5, 13.5), P(18, 15.5), P(19.5, 20) }, false));
                break;
        }
    }

    // ----- fila delle locandine (DrawLibrary / DrawCard) -----
    private Rect _sourceRect, _stripRect, _leftRect, _rightRect;

    private void DrawLibrary(DrawingContext ctx, Rect page, bool sourceOnly = false)
    {
        double margin = HudMargin;
        _sourceRect = new Rect(page.Width - margin - D(84) + D(8), LibraryTop - D(8), D(84), D(38));
        string filterName = _filterMode == 1 ? "   Film" : _filterMode == 2 ? "   Serie TV" : "";
        string rowTitle = _query.Length > 0 ? "Risultati per «" + _query + "»" : _filterMode != 0 ? "Risultati" : "Film e serie TV";
        if (!sourceOnly)
        {
            TextAt(ctx, rowTitle + filterName, 10.7, Semibold, SpotInk, new Rect(margin, _sourceRect.Y, Math.Max(1, _sourceRect.X - D(240) - margin), _sourceRect.Height));
            TextAt(ctx, $"{_filtered.Count} titoli", 8.5, Regular, SpotSoft, new Rect(_sourceRect.X - D(220), _sourceRect.Y, D(206), _sourceRect.Height), TextAlignment.Right);
        }
        // Fonte: Libreria (griglia) e Rete, due icone; quella attiva piena.
        for (int option = 0; option < 2; option++)
        {
            var half = new Rect(_sourceRect.X + option * _sourceRect.Width / 2, _sourceRect.Y, _sourceRect.Width / 2, _sourceRect.Height);
            bool active = (option == 1) == _network;
            string zone = option == 0 ? "source-library" : "source-network";
            DrawIcon(ctx, half, option == 0 ? "grid" : "network", active || Hot(zone));
            _hits.Add((half, zone, -1));
        }
        if (sourceOnly) return;
        var (columns, cardW, cardH, rowOffset) = CardLayout();
        double gap = D(14);
        _stripRect = new Rect(margin, LibraryTop + D(44), page.Width - 2 * margin, cardH);
        if (_filtered.Count == 0)
        {
            TextAt(ctx, "Nessun risultato. Prova un altro titolo o cambia filtro.", 8.5, Regular, SpotSoft, _stripRect, TextAlignment.Center);
            return;
        }
        // La fila scorre con continuita' verso _first (inseguimento morbido, ~120 ms), invece di saltare di una colonna.
        double nowMs = _clock.Elapsed.TotalMilliseconds;
        double dt = _rowAt > 0 ? Math.Clamp((nowMs - _rowAt) / 1000, 0, .1) : 0;
        _rowAt = nowMs;
        if (double.IsNaN(_firstAnim) || _capturing) _firstAnim = _first;
        else if (Math.Abs(_first - _firstAnim) > .002) { _firstAnim += (_first - _firstAnim) * (1 - Math.Exp(-dt / .07)); RequestFrame(); }
        else _firstAnim = _first;
        double stride = cardW + gap;
        using (ctx.PushClip(new Rect(margin - gap, _stripRect.Y - D(6), page.Width - 2 * margin + 2 * gap, cardH + D(12))))
            for (int pos = Math.Max(0, (int)Math.Floor(_firstAnim) - 1); pos < Math.Min(_filtered.Count, (int)Math.Ceiling(_firstAnim) + columns + 1); pos++)
            {
                var rect = new Rect(margin + rowOffset + (pos - _firstAnim) * stride, _stripRect.Y, cardW, cardH);
                if (rect.Right < margin - gap || rect.X > page.Width - margin + gap) continue;
                DrawCard(ctx, _items[_filtered[pos]], rect, pos == _index, pos == _hoverCard && pos != _index);
                if (rect.X >= margin - cardW / 2 && rect.Right <= page.Width - margin + cardW / 2) _hits.Add((rect, "card", pos));
            }
        // Frecce ai lati della fila.
        double w = Math.Max(30, margin - D(14));
        _leftRect = new Rect(D(4), _stripRect.Y, w, _stripRect.Height);
        _rightRect = new Rect(page.Width - w - D(4), _stripRect.Y, w, _stripRect.Height);
        if (_filtered.Count > 1)
        {
            foreach (var (r, zone, right) in new[] { (_leftRect, "left", false), (_rightRect, "right", true) })
            {
                double z = D(9), cx = r.Center.X, cy = r.Center.Y;
                var pen = new Pen(B(A(SpotInk, Hot(zone) ? 255 : 150)), Math.Max(2, D(2.2)), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                var pts = right ? new[] { new Point(cx - z / 2, cy - z), new Point(cx + z / 2, cy), new Point(cx - z / 2, cy + z) }
                                : new[] { new Point(cx + z / 2, cy - z), new Point(cx - z / 2, cy), new Point(cx + z / 2, cy + z) };
                ctx.DrawGeometry(null, pen, new PolylineGeometry(pts, false));
                _hits.Add((r, zone, -1));
            }
        }
    }

    private void DrawGrid(DrawingContext ctx, Rect rect, Color ink)
    {
        double s = D(8), g = D(4), x = rect.Center.X - s - g / 2, y = rect.Center.Y - s - g / 2;
        var pen = new Pen(B(ink), Math.Max(1.2, D(1.5)));
        for (int i = 0; i < 4; i++) ctx.DrawRectangle(null, pen, new Rect(x + (i % 2) * (s + g), y + (i / 2) * (s + g), s, s), D(2), D(2));
    }

    private void DrawCard(DrawingContext ctx, LibraryItem item, Rect rect, bool selected, bool hovered)
    {
        double radius = D(6);
        using (ctx.PushGeometryClip(new RectangleGeometry(rect, radius, radius)))
        {
            if (Library?.LoadImage(item.WideArtPath ?? item.ArtPath, rect.Width) is { } bmp) DrawCoverImage(ctx, bmp, rect, .5, .42);
            else ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(30, 55, 74), 0), new GradientStop(Color.FromRgb(4, 14, 23), 1) },
            }, rect);
            // Vignettatura delle schede (CinematicVignette card: true): bordi un po' scuri e fondo che scende al nero per il titolo.
            Color tone = Color.FromRgb(3, 9, 16);
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(A(tone, 0), .12), new GradientStop(A(tone, 150), .6), new GradientStop(A(tone, 255), .96) },
            }, rect);
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(A(tone, 102), 0), new GradientStop(A(tone, 0), .28), new GradientStop(A(tone, 0), .72), new GradientStop(A(tone, 87), 1) },
            }, rect);
            if (hovered && !selected) ctx.FillRectangle(B(Color.FromArgb(26, 255, 255, 255)), rect);
        }
        if (selected) ctx.DrawRectangle(null, new Pen(B(A(SpotInk, 235)), Math.Max(2, UiScale * 2)), rect.Deflate(1), radius, radius);
        TextAt(ctx, Title(item), 8.9, Regular, Color.FromRgb(244, 248, 250), new Rect(rect.X + D(12), rect.Bottom - D(34), rect.Width - D(24), D(28)));
    }

    /// <summary>Spotlight senza titoli: tutto al centro, un titolo, una riga; Libreria e Rete restano sotto.</summary>
    /// <summary>
    /// Spotlight senza titoli (DrawEmpty di Windows): libreria vuota, server vuoto, ricerca o filtri senza risultati. Al
    /// centro un'icona in un cerchio velato, un titolo, una riga e un pulsante ("Apri la libreria" / "Azzera ricerca e
    /// filtri"); sotto, Libreria e Rete come icone.
    /// </summary>
    private void DrawEmpty(DrawingContext ctx, Rect page)
    {
        DrawQuietBackground(ctx, page);
        bool noResults = _items.Count > 0;
        string title = noResults ? (_query.Length == 0 ? "Nessun titolo con questi filtri" : "Nessun risultato per «" + _query + "»")
            : _network ? "Il server non ha titoli" : "Il tuo cinema, qui";
        string body = noResults ? "Prova un'altra parola o azzera i filtri."
            : _network ? "Controlla che il server sia acceso, oppure torna alla tua libreria."
            : "Aggiungi una cartella di film o serie nella libreria e comparirà qui.";
        double cx = page.Width / 2, top = Math.Max(D(150), page.Height / 2 - D(150));
        double disc = D(84);
        var circle = new Rect(cx - disc / 2, top, disc, disc);
        ctx.DrawEllipse(B(A(SpotInk, UiTheme.IsLight ? 16 : 22)), null, circle.Center, disc / 2, disc / 2);
        WinSymbol.Draw(ctx, circle.Deflate(D(26)), noResults ? "search" : _network ? "network" : "movie", SpotSoft);
        var titleRect = new Rect(HudMargin, circle.Bottom + D(26), Math.Max(1, page.Width - 2 * HudMargin), D(36));
        TextAt(ctx, title, 20, Semibold, SpotInk, titleRect, TextAlignment.Center);
        double bodyW = Math.Min(page.Width - 2 * HudMargin, D(560));
        var bodyRect = new Rect(cx - bodyW / 2, titleRect.Bottom + D(8), bodyW, D(28));
        TextAt(ctx, body, 11, Regular, SpotSoft, bodyRect, TextAlignment.Center);
        string action = noResults ? "Azzera ricerca e filtri" : "Apri la libreria";
        double bw = Text(action, 9.6, Semibold, SpotInk).Width + D(48);
        var button = new Rect(cx - bw / 2, bodyRect.Bottom + D(22), bw, D(42));
        Button(ctx, button, action, noResults ? "empty-clear" : "empty-library", true, null, playGlyph: false);
        if (!noResults)
        {
            _sourceRect = new Rect(cx - D(42), button.Bottom + D(28), D(84), D(38));
            for (int option = 0; option < 2; option++)
            {
                var half = new Rect(_sourceRect.X + option * _sourceRect.Width / 2, _sourceRect.Y, _sourceRect.Width / 2, _sourceRect.Height);
                string zone = option == 0 ? "source-library" : "source-network";
                DrawIcon(ctx, half, option == 0 ? "grid" : "network", (option == 1) == _network || Hot(zone));
                _hits.Add((half, zone, -1));
            }
        }
        if (_items.Count > 0) DrawTopBar(ctx, page);
    }

    // ----- scheda del cast (DrawCastSheet): un foglio che sale dal basso -----
    private bool _castOpen, _castLoading;
    private double _castOpenedMs;
    private int _castFirst, _castVisible;
    private List<(string Name, string Role, string? Photo)> _cast = new();
    private Rect _castClose, _castLeft, _castRight;

    private void OpenCast()
    {
        if (Current is not { } item) return;
        _castOpen = true; _castFirst = 0; _castOpenedMs = _clock.Elapsed.TotalMilliseconds;
        _cast = item.Cast.ToList();
        _castLoading = _cast.Count == 0;
        if (_castLoading)
            Task.Run(async () =>
            {
                List<(string, string, string?)> found = new();
                try
                {
                    if (item.IsNetwork) { await NetworkStore.LoadPeopleAsync(item); found = item.Cast.ToList(); }
                    else
                    {
                        var rich = CinecorePlayer2025.Utilities.MovieMetadataService.TryGetSavedRichMetadata(item.Path, CinecorePlayer2025.Utilities.AppLanguage.TmdbLocale(CinecoreMac.L10n.Language));
                        if (rich != null) found = rich.CastMembers.Select(c => (c.Name, c.Character, c.ProfilePath)).ToList();
                    }
                }
                catch { }
                Dispatcher.UIThread.Post(() => { if (Current == item) { _cast = found; _castLoading = false; InvalidateVisual(); } });
            });
        InvalidateVisual();
    }

    private void CloseCast() { _castOpen = false; InvalidateVisual(); }

    private void DrawCastSheet(DrawingContext ctx, Rect page, LibraryItem item)
    {
        double t = Math.Clamp((_clock.Elapsed.TotalMilliseconds - _castOpenedMs) / 260, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        if (t < 1 || _castLoading) RequestFrame();
        // Velo leggero (prima 126/255 di nero: "violentissimo"); in modalita' chiara e' bianco.
        bool light = UiTheme.IsLight;
        ctx.FillRectangle(B(light ? Color.FromArgb((byte)(70 * e), 255, 255, 255) : Color.FromArgb((byte)(64 * e), 0, 3, 7)), page);
        double sheetH = D(340), rise = (1 - e) * D(60);
        var sheet = new Rect(0, page.Height - sheetH + rise, page.Width, sheetH);
        Color deep = light ? UiTheme.Panel : Color.FromRgb(3, 9, 16);
        var fade = new Rect(0, Math.Max(0, sheet.Y - D(52)), page.Width, D(52));
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(A(deep, 0), 0), new GradientStop(deep, 1) },
        }, fade);
        ctx.FillRectangle(B(deep), new Rect(0, fade.Bottom, page.Width, page.Height - fade.Bottom + D(60)));
        _hits.Add((page, "cast-outside", -1));
        _hits.Add((sheet, "cast-sheet", -1));
        double left = HudMargin, top = sheet.Y + D(24);
        TextAt(ctx, "Cast   " + Title(item), 13, Semibold, SpotInk, new Rect(left, top, page.Width - left - D(180), D(32)));
        string meta = string.Join("     ", new[] { item.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(item.Director) ? null : "Regia: " + item.Director,
            item.Rating is > 0 ? $"TMDb {item.Rating.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',')}/10" : null }.Where(v => !string.IsNullOrWhiteSpace(v)));
        TextAt(ctx, meta, 8.5, Regular, A(SpotBody, 174), new Rect(left, top + D(29), page.Width - left - D(180), D(22)));
        // Freccia giu' al centro: il foglio si chiude spingendolo giu'.
        _castClose = new Rect((page.Width - D(44)) / 2, top - D(2), D(44), D(36));
        double dx = Math.Max(8, D(9)), dy = Math.Max(4, D(5));
        ctx.DrawGeometry(null, new Pen(B(A(SpotInk, _hoverZone == "cast-close" ? 255 : 170)), Math.Max(1.35, 1.48 * UiScale), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
            new PolylineGeometry(new[] { new Point(_castClose.Center.X - dx, _castClose.Center.Y - dy), new Point(_castClose.Center.X, _castClose.Center.Y + dy), new Point(_castClose.Center.X + dx, _castClose.Center.Y - dy) }, false));
        _hits.Add((_castClose, "cast-close", -1));
        var content = new Rect(sheet.X + D(12), top + D(65), sheet.Width - D(24), sheet.Height - D(95));
        if (_castLoading || _cast.Count == 0)
        {
            if (_castLoading)
            {
                int phase = (int)(_clock.Elapsed.TotalMilliseconds / 130 % 3);
                double dot = Math.Max(5, D(7)), pulse = D(14), x = content.X + (content.Width - (dot * 3 + pulse * 2)) / 2, y = content.Y + content.Height / 2 - D(28);
                for (int i = 0; i < 3; i++)
                    ctx.DrawEllipse(B(A(SpotInk, i == phase ? 255 : i == (phase + 2) % 3 ? 142 : 68)), null, new Rect(x + i * (dot + pulse), y, dot, dot));
            }
            TextAt(ctx, _castLoading ? "Caricamento di cast e fotografie…" : "Cast non disponibile per questo titolo", 11, Regular, A(SpotBody, 188),
                new Rect(content.X, content.Y + content.Height / 2 + D(3), content.Width, D(42)), TextAlignment.Center);
            _castVisible = 0;
            return;
        }
        double arrowW = D(42), viewLeft = content.X + arrowW + D(14), viewRight = content.Right - arrowW - D(14), viewW = Math.Max(1, viewRight - viewLeft);
        double gap = D(15);
        int visible = (int)Math.Clamp((viewW + gap) / Math.Max(130, D(142) + gap), 2, 10);
        visible = Math.Min(visible, _cast.Count);
        _castVisible = visible;
        int maxFirst = Math.Max(0, _cast.Count - visible);
        _castFirst = Math.Clamp(_castFirst, 0, maxFirst);
        double cardW = Math.Min(D(142), (viewW - gap * Math.Max(0, visible - 1)) / Math.Max(1, visible));
        double total = cardW * visible + gap * Math.Max(0, visible - 1), cardX = viewLeft + Math.Max(0, (viewW - total) / 2);
        double portraitTop = content.Y + D(6), portraitH = Math.Min(content.Height - D(76), Math.Round(cardW * 1.2));
        for (int slot = 0; slot < visible; slot++)
        {
            var (name, role, photo) = _cast[_castFirst + slot];
            var portrait = new Rect(cardX + slot * (cardW + gap), portraitTop, cardW, portraitH);
            double r = Math.Max(4, D(5));
            using (ctx.PushGeometryClip(new RectangleGeometry(portrait, r, r)))
            {
                if (Library?.LoadImage(photo, cardW) is { } bmp) DrawCoverImage(ctx, bmp, portrait, .5, .18);
                else
                {
                    ctx.FillRectangle(new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops = light ? new GradientStops { new GradientStop(Color.FromRgb(214, 220, 226), 0), new GradientStop(Color.FromRgb(236, 239, 242), 1) } : new GradientStops { new GradientStop(Color.FromRgb(33, 47, 59), 0), new GradientStop(Color.FromRgb(7, 14, 21), 1) },
                    }, portrait);
                    string initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
                    TextAt(ctx, initials, 20, Semibold, A(SpotBody, 172), portrait, TextAlignment.Center);
                }
            }
            ctx.DrawRectangle(null, new Pen(B(A(SpotInk, 42)), Math.Max(1, UiScale)), portrait, r, r);
            TextAt(ctx, name, 9.2, Semibold, SpotInk, new Rect(portrait.X, portrait.Bottom + D(8), portrait.Width, D(23)), TextAlignment.Center);
            var roleText = Text(string.IsNullOrWhiteSpace(role) ? "Interprete" : role, 8, Regular, A(SpotBody, 166), portrait.Width - D(6), 2, TextAlignment.Center);
            ctx.DrawText(roleText, new Point(portrait.X + D(3), portrait.Bottom + D(31)));
        }
        _castLeft = new Rect(content.X, portraitTop + Math.Max(0, (portraitH - arrowW) / 2), arrowW, arrowW);
        _castRight = new Rect(content.Right - arrowW, _castLeft.Y, arrowW, arrowW);
        foreach (var (rect, right, enabled, zone) in new[] { (_castLeft, false, _castFirst > 0, "cast-left"), (_castRight, true, _castFirst < maxFirst, "cast-right") })
        {
            double z = D(9), cx = rect.Center.X, cy = rect.Center.Y;
            var pts = right ? new[] { new Point(cx - z / 2, cy - z), new Point(cx + z / 2, cy), new Point(cx - z / 2, cy + z) }
                            : new[] { new Point(cx + z / 2, cy - z), new Point(cx - z / 2, cy), new Point(cx + z / 2, cy + z) };
            ctx.DrawGeometry(null, new Pen(B(A(SpotInk, !enabled ? 50 : _hoverZone == zone ? 255 : 170)), Math.Max(2, D(2.2)), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), new PolylineGeometry(pts, false));
            if (enabled) _hits.Add((rect, zone, -1));
        }
    }

    // ------------------------------------------------------------------ scelte
    private void KeepSelectionVisible()
    {
        int visible = Math.Max(1, Math.Min(VisibleColumns(), Math.Max(1, _filtered.Count)));
        if (_index < _first) _first = _index;
        if (_index >= _first + visible) _first = _index - visible + 1;
        _first = Math.Clamp(_first, 0, Math.Max(0, _filtered.Count - visible));
    }

    /// <summary>Altro titolo: si fissa la pagina di adesso e si dissolve sulla nuova.</summary>
    /// <summary>Ritorno da un film: Spotlight riparte dal titolo appena visto.</summary>
    internal void SelectPath(string path)
    {
        int i = _filtered.FindIndex(x => string.Equals(_items[x].Path, path, StringComparison.Ordinal));
        if (i <= 0) return;
        _index = i; _first = Math.Max(0, i - 1); _firstAnim = double.NaN;
        InvalidateVisual();
    }

    private void Select(int position)
    {
        _actionFocus = -1;
        if (_filtered.Count == 0 || _castOpen) return;
        position = Math.Clamp(position, 0, _filtered.Count - 1);
        if (position == _index) return;
        CaptureFrame();
        _browseShift = Math.Sign(position - _index);
        _index = position;
        KeepSelectionVisible();
        InvalidateVisual();
    }

    /// <summary>La pagina disegnata in una bitmap (vetro del menu del tasto destro), senza la ricerca vera.</summary>
    internal void RenderForCapture(DrawingContext dc)
    {
        _capturing = true;
        try { Render(dc); } finally { _capturing = false; }
    }

    private void CaptureFrame()
    {
        if (Bounds.Width < 10 || _entryStartMs < 0) return;
        double scale = 1; // solo sfondo e titolo, che si dissolvono: a 1x costa un quarto del 2x
        var rtb = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(Bounds.Width * scale), (int)Math.Ceiling(Bounds.Height * scale)), new Vector(96, 96));
        _capturing = true;
        try
        {
            // Come SurfaceCrossfade: niente RenderTargetBitmap.Render (DPI applicati due volte).
            using var dc = rtb.CreateDrawingContext();
            using (dc.PushTransform(Matrix.CreateScale(scale, scale))) Render(dc);
        }
        finally { _capturing = false; }
        _browseFrom?.Dispose();
        _browseFrom = rtb;
        _browseStartMs = _clock.Elapsed.TotalMilliseconds;
    }

    private void OpenSelected(bool restart = false)
    {
        if (Current is not { } item) return;
        double? resume = restart ? null : Library?.ResumeFor(item)?.Position;
        PlayRequested?.Invoke(item, resume);
    }

    private void ToggleSearch(bool open)
    {
        if (_searchOpen == open) return;
        _searchOpen = open;
        _searchAnimAt = _clock.Elapsed.TotalMilliseconds;
        if (open)
        {
            if (_searchBox == null && InputLayer != null)
            {
                _searchBox = new TextBox
                {
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0,
                    FontFamily = Reading, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center, IsVisible = false,
                };
                foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                             "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
                    _searchBox.Resources[k] = Brushes.Transparent;
                _searchBox.TextChanged += (_, _) => { _query = (_searchBox.Text ?? "").Trim(); Refilter(); InvalidateVisual(); };
                _searchBox.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape) { if (_searchBox.Text?.Length > 0) _searchBox.Text = ""; else { ToggleSearch(false); Focus(); } e.Handled = true; }
                    else if (e.Key is Key.Enter or Key.Down) { Focus(); e.Handled = true; }
                };
                InputLayer.Children.Add(_searchBox);
            }
            DispatcherTimer.RunOnce(() => _searchBox?.Focus(), TimeSpan.FromMilliseconds(230));
        }
        else if (_query.Length == 0 && _searchBox != null) _searchBox.IsVisible = false;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ input
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _mouse = e.GetPosition(this);
        var hit = _hits.LastOrDefault(h => h.Rect.Contains(_mouse));
        string zone = hit.Zone ?? "";
        int card = zone == "card" ? hit.Item : -1;
        if (zone != _hoverZone || card != _hoverCard) { _hoverZone = zone; _hoverCard = card; InvalidateVisual(); }
        Cursor = zone.Length > 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        if (_entryStartMs < 0) return;
        var p = e.GetPosition(this);
        var hit = _hits.LastOrDefault(h => h.Rect.Contains(p));
        if (hit.Zone != "search-pill") Focus();
        if (_topFocus >= 0) { _topFocus = -1; InvalidateVisual(); }
        // Tasto destro: lo stesso menu del tasto destro della libreria (prima valeva anche come clic sinistro e
        // sceglieva o apriva il film mentre si apriva il menu: il menu restava bloccato).
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            if (_castOpen) return;
            LibraryItem? target = null;
            if (hit.Zone == "card" && hit.Item >= 0 && hit.Item < _filtered.Count) { if (hit.Item != _index) Select(hit.Item); target = _items[_filtered[hit.Item]]; }
            else if (hit.Zone is "play" or "restart" or "queue" or "cast") target = Current;
            ContextMenuRequested?.Invoke(target, p);
            return;
        }
        switch (hit.Zone)
        {
            case "card":
                if (hit.Item == _index || e.ClickCount >= 2) OpenSelected(); else Select(hit.Item);
                break;
            case "left": Select(_index - 1); break;
            case "right": Select(_index + 1); break;
            case "play": OpenSelected(); break;
            case "restart": OpenSelected(restart: true); break;
            case "queue":
                if (Current is { } q) { _queuedPath = q.Path; QueueRequested?.Invoke(q); InvalidateVisual(); }
                break;
            case "cast": OpenCast(); break;
            case "cast-close": case "cast-outside": CloseCast(); break;
            case "cast-sheet": break;
            case "cast-left": _castFirst = Math.Max(0, _castFirst - Math.Max(1, _castVisible)); InvalidateVisual(); break;
            case "cast-right": _castFirst += Math.Max(1, _castVisible); InvalidateVisual(); break;
            case "favorite": if (Current is { } f) { Favorites.Toggle(f.Path); InvalidateVisual(); } break;
            case "settings": SettingsRequested?.Invoke(); break;
            case "search": ToggleSearch(true); break;
            case "search-pill": _searchBox?.Focus(); break;
            case "filter": _filterMode = hit.Item; Refilter(); InvalidateVisual(); break;
            case "empty-library": Close(); break;
            case "empty-clear":
                _filterMode = 0;
                if (_searchBox != null) _searchBox.Text = "";
                _query = ""; Refilter();
                if (_searchOpen) ToggleSearch(false);
                InvalidateVisual();
                break;
            case "source-library": if (_network) SourceRequested?.Invoke(false); break;
            case "source-network": if (!_network) SourceRequested?.Invoke(true); break;
            default: if (_searchOpen && _query.Length == 0) ToggleSearch(false); break;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        // Un titolo per scatto della rotella; col trackpad (tanti eventi piccoli, poi l'inerzia) si accumula il gesto e
        // si avanza al massimo di un titolo ogni 260 ms: prima ogni evento spostava di un titolo e la fila volava via.
        double d = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : -e.Delta.Y;
        long now = Environment.TickCount64;
        if (now - _wheelAt > 350 || Math.Sign(d) != Math.Sign(_wheelAcc)) _wheelAcc = 0;
        _wheelAt = now;
        _wheelAcc += d;
        if (Math.Abs(_wheelAcc) < 1.0 || now - _wheelStepAt < 260) return;
        _wheelStepAt = now;
        Select(_index + Math.Sign(_wheelAcc));
        _wheelAcc = 0;
    }
    private double _wheelAcc;
    private long _wheelAt, _wheelStepAt;

    public void HandleKey(KeyEventArgs e)
    {
        if (_searchBox?.IsFocused == true) return;
        if (_castOpen)
        {
            if (e.Key is Key.Escape or Key.Down or Key.Back) CloseCast();
            else if (e.Key == Key.Left) { _castFirst = Math.Max(0, _castFirst - 1); InvalidateVisual(); }
            else if (e.Key == Key.Right) { _castFirst++; InvalidateVisual(); }
            else return;
            e.Handled = true;
            return;
        }
        if (_topFocus >= 0)
        {
            var top = TopZones();
            _topFocus = Math.Min(_topFocus, top.Count - 1);
            switch (e.Key)
            {
                case Key.Left: _topFocus = Math.Max(0, _topFocus - 1); break;
                case Key.Right: _topFocus = Math.Min(top.Count - 1, _topFocus + 1); break;
                case Key.Enter: case Key.Space: ActivateTop(top[_topFocus]); break;
                case Key.Down: case Key.Escape: case Key.Back: _topFocus = -1; break;
                case Key.Up: break;
                default: return;
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_actionFocus >= 0)
        {
            var zones = ActionZones();
            switch (e.Key)
            {
                case Key.Left: _actionFocus = Math.Max(0, _actionFocus - 1); break;
                case Key.Right: _actionFocus = Math.Min(zones.Count - 1, _actionFocus + 1); break;
                case Key.Enter: case Key.Space: ActivateAction(zones[Math.Min(_actionFocus, zones.Count - 1)]); break;
                case Key.Down: case Key.Escape: case Key.Back: case Key.Up: _actionFocus = -1; break;
                default: return;
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Enter: case Key.Space when Current != null: _actionFocus = 0; InvalidateVisual(); break;
            case Key.Left: Select(_index - 1); break;
            case Key.Right: Select(_index + 1); break;
            case Key.Up: _topFocus = 0; InvalidateVisual(); break;
            case Key.Home: Select(0); break;
            case Key.End: Select(_filtered.Count - 1); break;
            case Key.F when e.KeyModifiers.HasFlag(PlatformKeys.Command): ToggleSearch(true); break;
            case Key.Escape: case Key.Back:
                if (_searchOpen) { if (_searchBox != null) _searchBox.Text = ""; ToggleSearch(false); }
                else Close();
                break;
            default: return;
        }
        e.Handled = true;
    }

    internal void ApplyRemoteSearch(string text)
    {
        if (text.Length == 0 && !_searchOpen) return;
        ToggleSearch(true);
        if (_searchBox != null) _searchBox.Text = text;
    }

    internal void TestSearch(string text) { ToggleSearch(true); Dispatcher.UIThread.Post(() => { if (_searchBox != null) { _searchBox.Text = text; _searchBox.Focus(); _searchBox.CaretIndex = text.Length; } }, DispatcherPriority.Background); }
    internal void TestStep(int delta) => Select(_index + delta);
    internal void TestOpenCast() => OpenCast();
    internal void TestContextMenu() => ContextMenuRequested?.Invoke(Current, new Point(Bounds.Width / 2, Bounds.Height / 2));
    internal void QueueFromMenu(LibraryItem item) { _queuedPath = item.Path; QueueRequested?.Invoke(item); InvalidateVisual(); }
    internal void PlayFromMenu(LibraryItem item, bool restart) { int i = _filtered.FindIndex(x => _items[x].Path == item.Path); if (i >= 0) { _index = i; OpenSelected(restart); } }
    internal void CastFromMenu() => OpenCast();
    internal void TestSource(bool network) => SourceRequested?.Invoke(network);
    internal string QaSummary => $"network={_network} items={_items.Count} filtered={_filtered.Count} current={Current?.Title} focus={(_topFocus >= 0 ? "alto:" + TopZones().ElementAtOrDefault(_topFocus) : _actionFocus >= 0 ? ActionZones().ElementAtOrDefault(_actionFocus) : "carosello")} open={IsOpen}";
}
