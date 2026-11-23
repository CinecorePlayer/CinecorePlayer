using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace CinecoreMac;

/// <summary>
/// Modifica di una foto (PhotoEditForm di Windows), a tutta finestra: la foto al centro, in alto "indietro" e "salva
/// copia", in basso la barra degli strumenti (penna, evidenziatore, linea, freccia, rettangolo, ellisse, testo, filtri,
/// ruota, annulla, ripristina) e sopra le opzioni dello strumento (colori, contagocce, spessore, oppure i filtri).
/// L'originale non si tocca mai: si salva una copia accanto. A schermo disegna Avalonia, nel file SkiaSharp.
/// </summary>
internal sealed class PhotoEditCanvas : Control
{
    internal enum Tool { Pen, Marker, Line, Arrow, Box, Ellipse, Text, Picker, Filter, Rotate, Undo, Redo, Adjust, Crop, Pixelate, Eraser }
    private sealed class Mark { public Tool Kind; public List<Point> Points = new(); public Color Color; public double Width; public string Text = ""; public double Size; }

    // Le matrici dei filtri di Windows (righe = canale d'ingresso), qui in forma Skia (righe = canale d'uscita).
    /// <summary>I filtri di Foto: Vivace, Drammatico (ognuno anche caldo e freddo), Mono, Argento, Noir.</summary>
    internal static readonly (string Name, float[]? Gdi)[] Filters =
    {
        ("Originale", null),
        ("Vivace", Look(1.35, 0, 1.08, 0.01)),
        ("Vivace caldo", Look(1.3, 0.05, 1.08, 0.01)),
        ("Vivace freddo", Look(1.3, -0.05, 1.08, 0.01)),
        ("Drammatico", Look(0.8, 0, 1.3, -0.05)),
        ("Drammatico caldo", Look(0.85, 0.06, 1.3, -0.05)),
        ("Drammatico freddo", Look(0.8, -0.06, 1.3, -0.05)),
        ("Mono", Look(0, 0, 1.05, 0)),
        ("Argento", Look(0, 0, 0.85, 0.06)),
        ("Noir", Look(0, 0, 1.55, -0.08)),
        ("Luminoso", Look(1.08, 0.01, 0.96, 0.07)),
        ("Pellicola", Look(0.88, 0.035, 0.9, 0.035)),
        ("Sbiadito", Look(0.7, 0.01, 0.78, 0.08)),
        ("Cinema", Look(1.12, 0.025, 1.2, -0.03)),
        ("Notte", Look(0.8, -0.085, 1.12, -0.04)),
        ("Seppia", Look(0.12, 0.085, 1.02, 0.015)),
    };

    /// <summary>
    /// Matrice GDI (righe = canale d'ingresso, traslazione 0..1 nell'ultima riga) da saturazione, calore,
    /// contrasto attorno al grigio medio e luminosita'.
    /// </summary>
    private static float[] Look(double sat, double warm, double contrast, double bright)
    {
        double lr = .2126 * (1 - sat), lg = .7152 * (1 - sat), lb = .0722 * (1 - sat);
        double[,] m = { { lr + sat, lg, lb }, { lr, lg + sat, lb }, { lr, lg, lb + sat } }; // [uscita, ingresso]
        var g = new float[25];
        for (int o = 0; o < 3; o++)
        {
            for (int i = 0; i < 3; i++) g[i * 5 + o] = (float)(m[o, i] * contrast);
            g[20 + o] = (float)(0.5 - 0.5 * contrast + bright + (o == 0 ? warm : o == 2 ? -warm : 0));
        }
        g[18] = 1; g[24] = 1;
        return g;
    }

    private static float[] Saturation(float s)
    {
        float r = .299f * (1 - s), g = .587f * (1 - s), b = .114f * (1 - s);
        return new[] { r + s, r, r, 0, 0, g, g + s, g, 0, 0, b, b, b + s, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 };
    }

    /// <summary>Matrice 5x5 di GDI (vettore riga) -> 4x5 di Skia (colonne d'uscita); la traslazione resta in 0..1.</summary>
    private static float[] ToSkia(float[] m)
    {
        var r = new float[20];
        for (int o = 0; o < 4; o++)
        {
            for (int i = 0; i < 4; i++) r[o * 5 + i] = m[i * 5 + o];
            // In SkiaSharp la traslazione della matrice colore e' in scala 0..1 (non 0..255): moltiplicata per 255
            // saturava tutto e Caldo, Freddo, Contrasto e Luminoso uscivano come colori pieni (anche nelle copie salvate).
            r[o * 5 + 4] = m[4 * 5 + o];
        }
        return r;
    }

    internal static readonly Color[] Inks = { Colors.White, Colors.Black, Color.FromRgb(232, 60, 50), Color.FromRgb(250, 205, 40), Color.FromRgb(62, 190, 120), Color.FromRgb(50, 128, 235) };
    private static readonly Tool[] Bar = { Tool.Pen, Tool.Marker, Tool.Line, Tool.Arrow, Tool.Box, Tool.Ellipse, Tool.Text, Tool.Filter, Tool.Rotate, Tool.Undo, Tool.Redo };
    private static readonly FontFamily Inter = new("avares://CinecoreMac/Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);

    public event Action<string?>? Closed;
    /// <summary>Cambio di stato (strumento, colore, annulla/ripristina, nota): la vista attorno aggiorna i suoi controlli.</summary>
    public event Action? StateChanged;
    public Tool CurrentTool => _tool;
    public Color CurrentColor => _color;
    public int CurrentSize => _size;
    public int CurrentFilter => _filter;
    public bool CanUndo => _actions.Count > 0;
    public bool CanRedo => _redoActions.Count > 0;
    // Azioni annullabili: un segno aggiunto o un segno tolto con la gomma (con la sua posizione).
    private readonly List<(Mark Mark, bool Erase, int Index)> _actions = new(), _redoActions = new();
    private void Did(Mark mark, bool erase = false, int index = -1) { _actions.Add((mark, erase, index)); _redoActions.Clear(); }
    public string Note => _note;
    public string FileName => Path.GetFileName(_path);
    public void SetSize(int size) { _size = Math.Clamp(size, 0, 2); if (_typing != null) _typing.Size = TextSize; Changed(); }
    public void SetFilter(int filter) { _filter = Math.Clamp(filter, 0, Filters.Length - 1); Changed(); }
    public void Cancel() => Close(null);
    /// <summary>Esc o X: la vista chiede se salvare (se c'e' qualcosa da salvare) invece di chiudere subito.</summary>
    public event Action? CloseRequested;
    private int _turns;
    private bool _mirrored;
    /// <summary>Qualcosa da salvare: regolazioni, filtro, ritaglio, rotazione o riflesso, segni.</summary>
    public bool HasChanges => _image != null && (_marks.Count > 0 || Adjusted || _filter != 0 || Cropped || _turns % 4 != 0 || _mirrored);
    /// <summary>Anteprima piccola della foto con un filtro (per i bottoni dei filtri).</summary>
    public Bitmap? FilterPreview(int filter, int width)
    {
        if (_preview == null) return null;
        try
        {
            double k = width / (double)Math.Max(1, _preview.Width);
            using var small = _preview.Resize(new SKImageInfo(width, Math.Max(1, (int)(_preview.Height * k))), SKFilterQuality.Medium);
            if (small == null) return null;
            using var filtered = Filtered(small, filter);
            return ToAvalonia(filtered);
        }
        catch { return null; }
    }
    private void Changed() { InvalidateVisual(); StateChanged?.Invoke(); }

    private string _path = "";
    private SKBitmap? _image;            // la foto, gia' ruotata
    private Bitmap? _shownImage;         // la stessa con il filtro, per lo schermo
    private int _shownFilter = -1;
    private readonly List<Mark> _marks = new();
    private Tool _tool = Tool.Pen, _toolBeforePicker = Tool.Pen;
    private Color _color = Inks[2];
    private int _filter, _size = 1;
    private Mark? _drawing, _typing;
    private Point _mouse = new(-1, -1);
    private string _note = "";

    public PhotoEditCanvas() { Focusable = true; ClipToBounds = true; }

    /// <summary>
    /// Apre subito e decodifica in sottofondo: prima la decodifica a piena risoluzione (e la conversione in PNG per lo
    /// schermo) bloccavano l'interfaccia per secondi. A schermo va una copia alla misura dello schermo.
    /// </summary>
    public bool Open(string path)
    {
        try { using var probe = SKCodec.Create(path); if (probe == null) return false; }
        catch { return false; }
        _image?.Dispose(); _image = null; _preview?.Dispose(); _preview = null; _shownImage?.Dispose(); _shownImage = null;
        _path = path; _marks.Clear(); _actions.Clear(); _redoActions.Clear(); _drawing = _typing = null; _filter = 0; _shownFilter = -1; _tool = Tool.Pen; _note = "";
        _adjust = new double[Adjustments.Length]; _shownAdjust = ""; _turns = 0; _mirrored = false;
        _loading = true;
        int job = ++_openJob;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var codec = SKCodec.Create(path);
                var decoded = SKBitmap.Decode(codec);
                if (decoded == null) return ((SKBitmap?)null, (SKBitmap?)null);
                var image = Orient(decoded, codec.EncodedOrigin);
                return (image, MakePreview(image));
            }
            catch { return ((SKBitmap?)null, (SKBitmap?)null); }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            var (image, preview) = t.Result;
            if (job != _openJob) { image?.Dispose(); preview?.Dispose(); return; }
            _loading = false;
            if (image == null) { _note = "Non riesco ad aprire la foto"; Changed(); return; }
            _image = image; _preview = preview; _shownFilter = -1; _crop = default; _view = default; _outside = 0;
            // Sfondo dell'editor: la foto rimpicciolita (ingrandita a tutto schermo diventa una sfocatura morbida).
            try
            {
                double k = 48.0 / Math.Max(1, preview!.Width);
                using var tiny = preview.Resize(new SKImageInfo(48, Math.Max(1, (int)(preview.Height * k))), SKFilterQuality.High);
                Backdrop = tiny != null ? ToAvalonia(tiny) : null;
            }
            catch { Backdrop = null; }
            Changed();
            Loaded?.Invoke();
        }));
        Focus();
        Changed();
        return true;
    }

    /// <summary>La foto e' pronta (le anteprime dei filtri si fanno da qui).</summary>
    public event Action? Loaded;
    /// <summary>La foto in piccolo, per lo sfondo sfocato dell'editor (sotto il vetro).</summary>
    public Bitmap? Backdrop { get; private set; }
    public bool IsLoading => _loading;
    private bool _loading;
    private int _openJob;
    private SKBitmap? _preview;          // copia alla misura dello schermo, per mostrare e per le anteprime

    private static SKBitmap MakePreview(SKBitmap image)
    {
        const int Max = 2560;
        double k = Math.Min(1, Max / (double)Math.Max(image.Width, image.Height));
        if (k >= 1) return image.Copy();
        return image.Resize(new SKImageInfo(Math.Max(1, (int)(image.Width * k)), Math.Max(1, (int)(image.Height * k))), SKFilterQuality.High) ?? image.Copy();
    }

    // ------------------------------------------------------------------ regolazioni come su iPhone
    /// <summary>Le regolazioni di Foto (iPhone e Mac), nell'ordine e nei gruppi di Foto; valori da -1 a 1.</summary>
    internal static readonly (string Name, string Section)[] Adjustments =
    {
        ("Esposizione", "Luce"), ("Brillantezza", "Luce"), ("Luci", "Luce"), ("Ombre", "Luce"), ("Contrasto", "Luce"), ("Luminosità", "Luce"), ("Punto di nero", "Luce"),
        ("Saturazione", "Colore"), ("Vividezza", "Colore"), ("Calore", "Colore"), ("Tinta", "Colore"),
        ("Nitidezza", "Dettagli"), ("Definizione", "Dettagli"), ("Vignettatura", "Dettagli"),
    };
    internal static readonly string[] AdjustNames = Adjustments.Select(a => a.Name).ToArray();
    private double[] _adjust = new double[Adjustments.Length];
    private string _shownAdjust = "";
    public double GetAdjust(int which) => _adjust[which];
    public void SetAdjust(int which, double value) { _adjust[which] = Math.Clamp(value, -1, 1); Changed(); }
    public void ResetAdjust() { _adjust = new double[Adjustments.Length]; Changed(); }
    public bool Adjusted => _adjust.Any(a => Math.Abs(a) > 0.001);

    /// <summary>
    /// "Migliora automaticamente": legge istogramma e saturazione e corregge solo cio' che serve, con misura (una foto
    /// gia' a posto resta quasi uguale). Esposizione verso il grigio medio solo se la foto e' davvero scura o chiara; luci
    /// recuperate in proporzione a quanto e' bruciato; ombre aperte in proporzione a quanto e' chiuso; punto di nero se la
    /// foto e' velata; contrasto se i toni sono compressi; vividezza se i colori sono spenti.
    /// </summary>
    public void AutoEnhance()
    {
        if (_preview == null) return;
        var hist = new int[256];
        int step = Math.Max(1, (int)Math.Sqrt(_preview.Width * (double)_preview.Height / 200_000)), n = 0;
        double satSum = 0;
        for (int y = 0; y < _preview.Height; y += step)
            for (int x = 0; x < _preview.Width; x += step)
            {
                var c = _preview.GetPixel(x, y);
                hist[(int)Math.Clamp(c.Red * .2126 + c.Green * .7152 + c.Blue * .0722, 0, 255)]++; n++;
                int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue)), min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                satSum += max == 0 ? 0 : (max - min) / (double)max;
            }
        if (n == 0) return;
        double Percentile(double q) { int acc = 0; for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= q * n) return i / 255.0; } return 1; }
        double Fraction(int from, int to) { int acc = 0; for (int i = from; i <= to; i++) acc += hist[i]; return acc / (double)n; }
        double median = Percentile(0.5), p1 = Percentile(0.01), p99 = Percentile(0.99), sat = satSum / n;
        double clipped = Fraction(250, 255), crushed = Fraction(0, 8), dark = Fraction(0, 50);
        var a = new double[Adjustments.Length];
        // Esposizione: solo fuori da 0,32-0,62 di mediana, e a meta' strada (niente foto "piatte" tutte uguali).
        if (median < 0.32 || median > 0.62) a[0] = Math.Clamp(Math.Log2(0.46 / Math.Max(0.03, median)) / 1.2 * 0.6, -0.6, 0.6);
        a[2] = -Math.Clamp(clipped * 6, 0, 0.6);                    // luci bruciate
        a[3] = Math.Clamp(crushed * 5 + Math.Max(0, dark - 0.25), 0, 0.5); // ombre chiuse
        if (p1 > 0.05) a[6] = Math.Clamp((p1 - 0.03) * 3, 0, 0.4);     // velatura: nero sollevato
        double spread = p99 - p1;
        if (spread < 0.75) a[4] = Math.Clamp((0.75 - spread) * 0.8, 0, 0.3); // toni compressi
        if (sat < 0.35) a[8] = Math.Clamp((0.35 - sat) * 1.2, 0, 0.35);     // colori spenti
        a[1] = Math.Clamp(a[3] * 0.4 - a[2] * 0.2, 0, 0.25);                 // un filo di brillantezza dove si e' corretto
        _adjust = a;
        Changed();
    }

    /// <summary>Curva dei toni (uguale sui tre canali): esposizione, brillantezza, luci, ombre, contrasto, luminosita', nero.</summary>
    private static byte[] ToneTable(double[] a)
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0;
            x *= Math.Pow(2, a[0] * 1.2);
            double shadow = 4 * x * (1 - x) * (1 - x), light = 4 * x * x * (1 - x);
            x += a[1] * (0.35 * shadow - 0.22 * light) + a[3] * 0.45 * shadow + a[2] * 0.45 * light;
            x = Math.Clamp(x, 0, 1);
            if (Math.Abs(a[4]) > 0.001) { double c = 1 + a[4] * 0.8; x = x < 0.5 ? 0.5 * Math.Pow(2 * x, c) : 1 - 0.5 * Math.Pow(2 * (1 - x), c); }
            if (Math.Abs(a[5]) > 0.001) x = Math.Pow(Math.Clamp(x, 0, 1), Math.Pow(2, -a[5] * 0.8));
            double k = a[6] * 0.12;
            x = k >= 0 ? (x - k) / (1 - k) : -k + x * (1 + k);
            t[i] = (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
        }
        return t;
    }

    /// <summary>Saturazione, vividezza, calore e tinta come matrice colore Skia (4x5, traslazione 0..1).</summary>
    private static float[] ColorMatrix(double[] a)
    {
        double sat = (1 + a[7]) * (1 + a[8] * 0.5), warm = a[9] * 0.08, tint = a[10] * 0.06;
        double lr = .2126 * (1 - sat), lg = .7152 * (1 - sat), lb = .0722 * (1 - sat);
        double[,] m = { { lr + sat, lg, lb }, { lr, lg + sat, lb }, { lr, lg, lb + sat } };
        var r = new float[20];
        for (int o = 0; o < 3; o++) for (int i = 0; i < 3; i++) r[o * 5 + i] = (float)m[o, i];
        r[4] = (float)(warm + tint / 2); r[9] = (float)-tint; r[14] = (float)(-warm + tint / 2);
        r[18] = 1;
        return r;
    }

    /// <summary>
    /// Sviluppo della foto: curva dei toni, colore, filtro, poi nitidezza e definizione (maschere di contrasto a raggio
    /// piccolo e grande) e vignettatura. Uguale a schermo (sulla copia piccola) e nel file (a piena risoluzione).
    /// </summary>
    private static SKBitmap Develop(SKBitmap image, int filter, double[]? a)
    {
        var result = new SKBitmap(image.Width, image.Height);
        using var c = new SKCanvas(result);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
        SKColorFilter? look = Filters[filter].Gdi is { } fm ? SKColorFilter.CreateColorMatrix(ToSkia(fm)) : null;
        SKColorFilter? tone = null, color = null;
        SKImageFilter? detail = null;
        bool adjusted = a != null && a.Any(v => Math.Abs(v) > 0.001);
        if (adjusted)
        {
            var table = ToneTable(a!);
            var identity = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            tone = SKColorFilter.CreateTable(identity, table, table, table);
            color = SKColorFilter.CreateColorMatrix(ColorMatrix(a!));
            float w = Math.Max(image.Width, image.Height);
            if (Math.Abs(a![12]) > 0.001)
            {
                var wide = SKImageFilter.CreateBlur(w / 90f, w / 90f);
                float amount = (float)(a[12] * 0.7);
                detail = SKImageFilter.CreateArithmetic(0, 1 + amount, -amount, 0, true, wide, null);
            }
            if (a[11] > 0.001)
            {
                var near = SKImageFilter.CreateBlur(Math.Max(0.6f, w / 1800f), Math.Max(0.6f, w / 1800f), detail);
                float amount = (float)(a[11] * 1.6);
                detail = SKImageFilter.CreateArithmetic(0, 1 + amount, -amount, 0, true, near, detail);
            }
            else if (a[11] < -0.001)
                detail = SKImageFilter.CreateBlur((float)(-a[11] * w / 700), (float)(-a[11] * w / 700), detail);
        }
        SKColorFilter? inner = tone != null && color != null ? SKColorFilter.CreateCompose(color, tone) : tone ?? color;
        paint.ColorFilter = look != null && inner != null ? SKColorFilter.CreateCompose(look, inner) : look ?? inner;
        paint.ImageFilter = detail;
        c.DrawBitmap(image, 0, 0, paint);
        if (adjusted && Math.Abs(a![13]) > 0.001)
        {
            float cx = image.Width / 2f, cy = image.Height / 2f, radius = (float)Math.Sqrt(cx * cx + cy * cy);
            byte alpha = (byte)Math.Round(Math.Abs(a[13]) * 200);
            var edge = a[13] > 0 ? new SKColor(0, 0, 0, alpha) : new SKColor(255, 255, 255, alpha);
            using var shade = new SKPaint
            {
                Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), radius, new[] { SKColors.Transparent, SKColors.Transparent, edge }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp),
            };
            c.DrawRect(0, 0, image.Width, image.Height, shade);
        }
        look?.Dispose(); tone?.Dispose(); color?.Dispose();
        return result;
    }

    /// <summary>L'orientamento EXIF applicato subito (come RotateFlip di Windows).</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        int turns = origin switch { SKEncodedOrigin.BottomRight => 2, SKEncodedOrigin.RightTop => 1, SKEncodedOrigin.LeftBottom => 3, _ => 0 };
        var result = source;
        for (int i = 0; i < turns; i++) { var next = RotateRight(result); if (!ReferenceEquals(result, source)) result.Dispose(); result = next; }
        if (!ReferenceEquals(result, source)) source.Dispose();
        return result;
    }

    private static SKBitmap RotateRight(SKBitmap source)
    {
        var rotated = new SKBitmap(source.Height, source.Width);
        using var c = new SKCanvas(rotated);
        c.Translate(rotated.Width, 0);
        c.RotateDegrees(90);
        c.DrawBitmap(source, 0, 0);
        return rotated;
    }

    private void Close(string? saved)
    {
        _openJob++;
        _shownImage?.Dispose(); _shownImage = null; _image?.Dispose(); _image = null; _preview?.Dispose(); _preview = null;
        Closed?.Invoke(saved);
    }

    // ------------------------------------------------------------------ misure
    private double K => Math.Clamp(Bounds.Height / 900, 1, 1.35);
    private double Px(double v) => Math.Round(v * K);
    private double StrokeWidth => new[] { .003, .006, .012 }[_size] * Math.Max(_image!.Width, _image.Height);
    private double TextSize => new[] { .03, .05, .08 }[_size] * Math.Min(_image!.Width, _image.Height);
    private bool DrawsWithColor => _tool is not (Tool.Filter or Tool.Adjust or Tool.Crop or Tool.Rotate or Tool.Undo or Tool.Redo);
    private Rect Canvas => new(Px(16), Px(16), Math.Max(1, Bounds.Width - Px(32)), Math.Max(1, Bounds.Height - Px(32)));

    // ------------------------------------------------------------------ ritaglio (in pixel della foto)
    private Rect _crop;
    private int _cropDrag = -1;          // 0..3 angoli, 4 spostamento
    private Point _cropFrom;
    private Rect _cropStart;
    private Rect FullCrop => _image == null ? default : new Rect(0, 0, _image.Width, _image.Height);
    private Rect Crop => _crop.Width > 0 ? _crop : FullCrop;
    public bool Cropped => _image != null && _crop.Width > 0 && _crop != FullCrop;
    public void ResetCrop() { _crop = FullCrop; _view = default; _outside = 0; Changed(); }

    // Ritaglio come su iPhone: mentre si trascina si vede tutta la foto (fuori scurita); lasciati gli angoli, dopo
    // un attimo la vista si allarga sul ritaglio e il resto sparisce, come se la foto fosse diventata quella.
    private Rect _view;                  // parte della foto che riempie il riquadro in Ritaglia (vuota = il ritaglio)
    private double _outside;             // 0 = si vede solo il ritaglio, 1 = tutta la foto con il fuori scurito
    private DateTime _settleAt;
    private Avalonia.Threading.DispatcherTimer? _cropAnim;
    private const double SettleDelaySeconds = 1.0;

    /// <summary>Solo per le prove: ritaglio in frazioni della foto, come se si fossero appena lasciati gli angoli.</summary>
    internal void QaCrop(double l, double t, double r, double b)
    {
        if (_image == null) return;
        if (_view.Width <= 0) _view = Crop;
        _crop = new Rect(l * _image.Width, t * _image.Height, (r - l) * _image.Width, (b - t) * _image.Height);
        _outside = 1; _settleAt = DateTime.UtcNow.AddSeconds(SettleDelaySeconds);
        StartCropAnimation(); Changed();
    }

    private void StartCropAnimation()
    {
        _cropAnim ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(16), Avalonia.Threading.DispatcherPriority.Render, (_, _) => CropAnimTick());
        if (!_cropAnim.IsEnabled) _cropAnim.Start();
    }

    private void CropAnimTick()
    {
        if (_tool != Tool.Crop || _image == null) { _cropAnim?.Stop(); return; }
        if (_cropDrag >= 0) { _outside = Math.Min(1, _outside + 0.2); InvalidateVisual(); if (_outside >= 1) _cropAnim?.Stop(); return; }
        if (DateTime.UtcNow < _settleAt) return;
        var target = Crop;
        var v = _view.Width > 0 ? _view : target;
        const double k = 0.2;
        v = new Rect(v.X + (target.X - v.X) * k, v.Y + (target.Y - v.Y) * k, v.Width + (target.Width - v.Width) * k, v.Height + (target.Height - v.Height) * k);
        _outside = Math.Max(0, _outside - 0.12);
        double eps = Math.Max(_image.Width, _image.Height) * 0.0015;
        bool done = Math.Abs(v.X - target.X) < eps && Math.Abs(v.Y - target.Y) < eps && Math.Abs(v.Right - target.Right) < eps && Math.Abs(v.Bottom - target.Bottom) < eps;
        _view = done ? default : v;
        if (done && _outside <= 0) _cropAnim?.Stop();
        InvalidateVisual();
    }

    /// <summary>Dove sta tutta la foto sullo schermo: in Ritaglia intera, altrimenti scalata sul ritaglio.</summary>
    private Rect ImageRect()
    {
        // In Ritaglia un po' di margine attorno: gli angoli si possono tirare in fuori.
        var canvas = _tool == Tool.Crop ? Canvas.Deflate(Px(18)) : Canvas;
        var crop = _tool == Tool.Crop && _view.Width > 0 ? _view : Crop;
        double scale = Math.Min(canvas.Width / crop.Width, canvas.Height / crop.Height);
        double vx = canvas.X + (canvas.Width - crop.Width * scale) / 2, vy = canvas.Y + (canvas.Height - crop.Height * scale) / 2;
        return new Rect(vx - crop.X * scale, vy - crop.Y * scale, _image!.Width * scale, _image.Height * scale);
    }

    /// <summary>La parte visibile della foto (il ritaglio) sullo schermo.</summary>
    private Rect Shown()
    {
        var full = ImageRect();
        double k = full.Width / _image!.Width;
        var c = Crop;
        return new Rect(full.X + c.X * k, full.Y + c.Y * k, c.Width * k, c.Height * k);
    }

    private Point ToImage(Point p)
    {
        var full = ImageRect();
        return new Point(Math.Clamp((p.X - full.X) / full.Width, 0, 1) * _image!.Width, Math.Clamp((p.Y - full.Y) / full.Height, 0, 1) * _image.Height);
    }

    /// <summary>Rifletti in orizzontale: foto, copia per lo schermo, segni e ritaglio.</summary>
    public void Flip()
    {
        if (_image == null) return;
        FinishTyping();
        static SKBitmap Mirror(SKBitmap source)
        {
            var mirrored = new SKBitmap(source.Width, source.Height);
            using var c = new SKCanvas(mirrored);
            c.Scale(-1, 1, source.Width / 2f, 0);
            c.DrawBitmap(source, 0, 0);
            return mirrored;
        }
        int width = _image.Width;
        var flipped = Mirror(_image); _image.Dispose(); _image = flipped;
        if (_preview != null) { var p = Mirror(_preview); _preview.Dispose(); _preview = p; }
        foreach (var mark in _marks) for (int i = 0; i < mark.Points.Count; i++) mark.Points[i] = new Point(width - mark.Points[i].X, mark.Points[i].Y);
        if (_crop.Width > 0) _crop = new Rect(width - _crop.Right, _crop.Y, _crop.Width, _crop.Height);
        _shownFilter = -1;
        _mirrored = !_mirrored;
        _view = default;
        Changed();
    }

    // ------------------------------------------------------------------ azioni
    public void Choose(Tool tool)
    {
        FinishTyping();
        switch (tool)
        {
            case Tool.Undo:
                if (_actions.Count > 0)
                {
                    var act = _actions[^1]; _actions.RemoveAt(_actions.Count - 1);
                    if (act.Erase) _marks.Insert(Math.Clamp(act.Index, 0, _marks.Count), act.Mark); else _marks.Remove(act.Mark);
                    _redoActions.Add(act);
                }
                break;
            case Tool.Redo:
                if (_redoActions.Count > 0)
                {
                    var act = _redoActions[^1]; _redoActions.RemoveAt(_redoActions.Count - 1);
                    if (act.Erase) _marks.Remove(act.Mark); else _marks.Add(act.Mark);
                    _actions.Add(act);
                }
                break;
            case Tool.Rotate:
            {
                int height = _image!.Height;
                if (_crop.Width > 0) _crop = new Rect(height - _crop.Bottom, _crop.X, _crop.Height, _crop.Width);
                var rotated = RotateRight(_image);
                _image.Dispose(); _image = rotated; _shownFilter = -1; _turns++;
                if (_preview != null) { var turned = RotateRight(_preview); _preview.Dispose(); _preview = turned; }
                foreach (var mark in _marks) for (int i = 0; i < mark.Points.Count; i++) mark.Points[i] = new Point(height - mark.Points[i].Y, mark.Points[i].X);
                _view = default;
                break;
            }
            case Tool.Picker: _toolBeforePicker = _tool == Tool.Picker ? _toolBeforePicker : _tool; _tool = Tool.Picker; break;
            default: _tool = tool; break;
        }
        if (_tool == Tool.Crop) { _view = default; _outside = 0; }
        Changed();
    }

    public void SetColor(Color color)
    {
        _color = Color.FromRgb(color.R, color.G, color.B);
        if (_typing != null) _typing.Color = _color;
        Changed();
    }

    private void FinishTyping()
    {
        if (_typing == null) return;
        if (_typing.Text.Trim().Length == 0) { _marks.Remove(_typing); _actions.RemoveAll(a => ReferenceEquals(a.Mark, _typing)); }
        _typing = null;
    }

    /// <summary>La foto con il filtro scelto, per lo schermo (rifatta solo quando cambiano filtro o rotazione).</summary>
    private Bitmap? ShownImage()
    {
        if (_preview == null) return null;
        string adjustKey = string.Join(",", _adjust.Select(a => a.ToString("0.000", CultureInfo.InvariantCulture))) + "|" + PixelateKey();
        if (_shownImage != null && _shownFilter == _filter && _shownAdjust == adjustKey) return _shownImage;
        _shownImage?.Dispose();
        using var filtered = Filtered(_preview, _filter, _adjust);
        Pixelate(filtered, _preview.Width / (double)_image!.Width);
        _shownImage = ToAvalonia(filtered);
        _shownFilter = _filter; _shownAdjust = adjustKey;
        return _shownImage;
    }

    private static SKBitmap Filtered(SKBitmap image, int filter, double[]? adjust = null) => Develop(image, filter, adjust);

    // ------------------------------------------------------------------ pixela e gomma
    private string PixelateKey() => string.Join(";", _marks.Where(m => m.Kind == Tool.Pixelate && !ReferenceEquals(m, _drawing) && m.Points.Count >= 2)
        .Select(m => $"{m.Points[0].X:0},{m.Points[0].Y:0},{m.Points[^1].X:0},{m.Points[^1].Y:0}"));

    /// <summary>Le zone "Pixela" a blocchi grandi (circa 1/60 del lato lungo della foto), su questa immagine.</summary>
    private void Pixelate(SKBitmap target, double scale)
    {
        foreach (var m in _marks)
        {
            if (m.Kind != Tool.Pixelate || ReferenceEquals(m, _drawing) || m.Points.Count < 2) continue;
            var a = m.Points[0]; var b = m.Points[^1];
            var r = SKRectI.Round(new SKRect((float)(Math.Min(a.X, b.X) * scale), (float)(Math.Min(a.Y, b.Y) * scale), (float)(Math.Max(a.X, b.X) * scale), (float)(Math.Max(a.Y, b.Y) * scale)));
            r.Intersect(new SKRectI(0, 0, target.Width, target.Height));
            if (r.Width < 2 || r.Height < 2) continue;
            int block = Math.Max(4, Math.Max(target.Width, target.Height) / 60);
            int sw = Math.Max(1, r.Width / block), sh = Math.Max(1, r.Height / block);
            using var small = new SKBitmap(sw, sh);
            using (var sc = new SKCanvas(small))
            using (var sp = new SKPaint { FilterQuality = SKFilterQuality.Medium })
                sc.DrawBitmap(target, r, new SKRect(0, 0, sw, sh), sp);
            using var c = new SKCanvas(target);
            using var p = new SKPaint { FilterQuality = SKFilterQuality.None };
            c.DrawBitmap(small, new SKRect(0, 0, sw, sh), new SKRect(r.Left, r.Top, r.Right, r.Bottom), p);
        }
    }

    /// <summary>Il segno sotto il punto (per la gomma), il piu' in alto.</summary>
    private Mark? MarkAt(Point screen)
    {
        if (_image == null) return null;
        var at = ToImage(screen);
        double tol = 14 / Math.Max(1e-6, ImageRect().Width / _image.Width);
        static double SegDist(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l = dx * dx + dy * dy;
            double t = l == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l, 0, 1);
            double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return Math.Sqrt(x * x + y * y);
        }
        for (int i = _marks.Count - 1; i >= 0; i--)
        {
            var m = _marks[i];
            if (m.Points.Count == 0) continue;
            double w = m.Width / 2 + tol;
            var a = m.Points[0]; var b = m.Points[^1];
            var box = new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
            bool hit = m.Kind switch
            {
                Tool.Pen or Tool.Marker => Enumerable.Range(1, Math.Max(1, m.Points.Count - 1)).Any(k => SegDist(at, m.Points[Math.Min(k - 1, m.Points.Count - 1)], m.Points[Math.Min(k, m.Points.Count - 1)]) <= w * (m.Kind == Tool.Marker ? 4 : 1)),
                Tool.Line or Tool.Arrow => SegDist(at, a, b) <= w,
                Tool.Box or Tool.Ellipse or Tool.Pixelate => box.Inflate(w).Contains(at),
                Tool.Text => new Rect(a.X - tol, a.Y - m.Size, Math.Max(m.Size, m.Text.Length * m.Size * 0.6) + tol * 2, m.Size * 2).Contains(at),
                _ => false,
            };
            if (hit) return m;
        }
        return null;
    }

    /// <summary>Da SkiaSharp ad Avalonia copiando i pixel (niente codifica in PNG).</summary>
    private static Bitmap ToAvalonia(SKBitmap source)
    {
        using var bgra = source.ColorType == SKColorType.Bgra8888 && source.AlphaType == SKAlphaType.Premul ? null : source.Copy(SKColorType.Bgra8888);
        var src = bgra ?? source;
        var bitmap = new WriteableBitmap(new PixelSize(src.Width, src.Height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
        {
            int rowBytes = src.RowBytes;
            for (int y = 0; y < src.Height; y++)
                unsafe { Buffer.MemoryCopy((byte*)src.GetPixels() + (long)y * rowBytes, (byte*)fb.Address + (long)y * fb.RowBytes, fb.RowBytes, Math.Min(rowBytes, fb.RowBytes)); }
        }
        return bitmap;
    }

    // ------------------------------------------------------------------ disegno della foto a schermo
    private void RenderMarks(DrawingContext ctx, Rect target, bool caret)
    {
        double k = target.Width / _image!.Width;
        Point Map(Point p) => new(target.X + p.X * k, target.Y + p.Y * k);
        foreach (var mark in _marks)
        {
            if (mark.Kind == Tool.Text)
            {
                string text = mark.Text + (caret && ReferenceEquals(mark, _typing) ? "|" : "");
                if (text.Length == 0) continue;
                double size = Math.Max(6, mark.Size * k);
                var at = Map(mark.Points[0]);
                bool lightInk = (mark.Color.R * .299 + mark.Color.G * .587 + mark.Color.B * .114) > 127;
                var shadow = UiText.Get(L10n.T(text), Semibold, size, Color.FromArgb(140, lightInk ? (byte)0 : (byte)255, lightInk ? (byte)0 : (byte)255, lightInk ? (byte)0 : (byte)255));
                var ink = UiText.Get(L10n.T(text), Semibold, size, mark.Color);
                double offset = Math.Max(1, size / 22);
                ctx.DrawText(shadow, new Point(at.X + offset, at.Y - size / 2 + offset));
                ctx.DrawText(ink, new Point(at.X, at.Y - size / 2));
                continue;
            }
            if (mark.Points.Count < 2) continue;
            bool marker = mark.Kind == Tool.Marker;
            var brush = new SolidColorBrush(marker ? Color.FromArgb(105, mark.Color.R, mark.Color.G, mark.Color.B) : mark.Color);
            var pen = new Pen(brush, Math.Max(1, mark.Width * k * (marker ? 4 : 1)), lineCap: marker ? PenLineCap.Flat : PenLineCap.Round, lineJoin: PenLineJoin.Round);
            Point a = Map(mark.Points[0]), b = Map(mark.Points[^1]);
            var box = new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
            switch (mark.Kind)
            {
                case Tool.Pen: case Tool.Marker: ctx.DrawGeometry(null, pen, new PolylineGeometry(mark.Points.Select(Map).ToList(), false)); break;
                case Tool.Line: ctx.DrawLine(pen, a, b); break;
                case Tool.Box: ctx.DrawRectangle(null, pen, box); break;
                case Tool.Ellipse: ctx.DrawEllipse(null, pen, box.Center, box.Width / 2, box.Height / 2); break;
                case Tool.Pixelate:
                    if (ReferenceEquals(mark, _drawing)) ctx.DrawRectangle(null, new Pen(B(Color.FromArgb(220, 255, 255, 255)), 1.5, new DashStyle(new double[] { 4, 3 }, 0)), box);
                    break;
                case Tool.Arrow:
                {
                    double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    if (length < 1) break;
                    double head = Math.Min(length * .45, pen.Thickness * 4.5 + 6), ux = (b.X - a.X) / length, uy = (b.Y - a.Y) / length;
                    var neck = new Point(b.X - ux * head, b.Y - uy * head);
                    ctx.DrawLine(pen, a, neck);
                    ctx.DrawGeometry(new SolidColorBrush(mark.Color), null, new PolylineGeometry(new[] { b, new Point(neck.X - uy * head * .5, neck.Y + ux * head * .5), new Point(neck.X + uy * head * .5, neck.Y - ux * head * .5) }, true));
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ interfaccia
    private static IBrush B(Color c) => new SolidColorBrush(c);

    private void Text(DrawingContext ctx, string text, double px, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Center)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = UiText.Get(L10n.T(text), face, px, color, Math.Max(1, r.Width), 1, align, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    /// <summary>Ombra morbida e superficie scura semitrasparente: la barra "galleggia" sulla foto.</summary>
    private static void Glass(DrawingContext ctx, Rect r)
    {
        for (int i = 4; i >= 1; i--)
            ctx.DrawRectangle(B(Color.FromArgb(14, 0, 0, 0)), null, r.Inflate(new Thickness(i * 2, i * 2 - 1)), r.Height / 2 + i * 2, r.Height / 2 + i * 2);
        ctx.DrawRectangle(B(Color.FromArgb(232, 22, 26, 32)), new Pen(B(Color.FromArgb(34, 255, 255, 255)), 1), r, r.Height / 2, r.Height / 2);
    }

    /// <summary>Icone degli strumenti dal set Lucide (come il resto dell'app), non piu' disegnate a mano.</summary>
    private static void Glyph(DrawingContext ctx, Tool tool, Rect r, Color color)
    {
        string name = tool switch
        {
            Tool.Pen => "pen-line", Tool.Marker => "highlighter", Tool.Line => "minus", Tool.Arrow => "arrow-up-right", Tool.Box => "square",
            Tool.Ellipse => "circle", Tool.Text => "type", Tool.Picker => "pipette", Tool.Filter => "sliders-horizontal", Tool.Rotate => "rotate-cw",
            Tool.Undo => "undo-2", _ => "redo-2",
        };
        if (SvgIcon.Get("Icons.Lucide/" + name + ".svg") is { } icon) { icon.Draw(ctx, r, color); return; }
        WinSymbol.Draw(ctx, r, name, color);
    }

    internal static string ToolName(Tool tool) => tool switch
    {
        Tool.Pen => "Penna", Tool.Marker => "Evidenziatore", Tool.Line => "Linea", Tool.Arrow => "Freccia", Tool.Box => "Rettangolo", Tool.Ellipse => "Ellisse",
        Tool.Text => "Testo", Tool.Picker => "Prendi un colore dalla foto", Tool.Filter => "Filtri", Tool.Adjust => "Regola luce e colore", Tool.Crop => "Trascina gli angoli per ritagliare", Tool.Pixelate => "Pixela una zona", Tool.Eraser => "Gomma", Tool.Rotate => "Ruota a destra",
        Tool.Redo => "Ripristina (⇧⌘Z)", _ => "Annulla ultimo (⌘Z)",
    };

    public override void Render(DrawingContext ctx)
    {
        var page = new Rect(Bounds.Size);
        if (_loading && page.Width >= 50)
        {
            var wait = UiText.Get(L10n.T("Apro la foto…"), Regular, Px(10.5), Color.FromArgb(170, 255, 255, 255));
            ctx.DrawText(wait, new Point((page.Width - wait.Width) / 2, (page.Height - wait.Height) / 2));
            return;
        }
        if (_image == null || page.Width < 50 || page.Height < 50) return;
        var full = ImageRect();
        var shown = Shown();
        bool cropping = _tool == Tool.Crop;
        for (int i = 6; i >= 1; i--) ctx.FillRectangle(B(Color.FromArgb(10, 0, 0, 0)), shown.Inflate(i * Px(3)));
        void DrawPhoto(Rect clip)
        {
            using (ctx.PushClip(clip))
            {
                if (ShownImage() is { } bmp)
                    using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                        ctx.DrawImage(bmp, new Rect(bmp.Size), full);
                RenderMarks(ctx, full, caret: true);
            }
        }
        // Ritaglia: il fuori dal riquadro si vede (scurito) solo mentre si trascina, poi sfuma e resta il ritaglio.
        if (cropping && _outside > 0.001)
        {
            var outer = full.Intersect(page);
            using (ctx.PushOpacity(_outside))
            {
                DrawPhoto(outer);
                var dim = B(Color.FromArgb(150, 0, 0, 0));
                ctx.FillRectangle(dim, new Rect(outer.X, outer.Y, outer.Width, Math.Max(0, shown.Y - outer.Y)));
                ctx.FillRectangle(dim, new Rect(outer.X, shown.Bottom, outer.Width, Math.Max(0, outer.Bottom - shown.Bottom)));
                ctx.FillRectangle(dim, new Rect(outer.X, shown.Y, Math.Max(0, shown.X - outer.X), shown.Height));
                ctx.FillRectangle(dim, new Rect(shown.Right, shown.Y, Math.Max(0, outer.Right - shown.Right), shown.Height));
            }
        }
        DrawPhoto(shown);
        if (!cropping) return;
        // Bordo, griglia dei terzi e angoli da trascinare.
        var thin = new Pen(B(Color.FromArgb(90, 255, 255, 255)), 1);
        for (int i = 1; i <= 2; i++)
        {
            ctx.DrawLine(thin, new Point(shown.X + shown.Width * i / 3, shown.Y), new Point(shown.X + shown.Width * i / 3, shown.Bottom));
            ctx.DrawLine(thin, new Point(shown.X, shown.Y + shown.Height * i / 3), new Point(shown.Right, shown.Y + shown.Height * i / 3));
        }
        ctx.DrawRectangle(null, new Pen(B(Colors.White), 1.5), shown);
        var corner = new Pen(B(Colors.White), Px(3.2), lineCap: PenLineCap.Square);
        double arm = Math.Min(Px(16), Math.Min(shown.Width, shown.Height) / 3);
        foreach (var (pt, dx, dy) in new[] { (shown.TopLeft, 1, 1), (shown.TopRight, -1, 1), (shown.BottomLeft, 1, -1), (shown.BottomRight, -1, -1) })
        {
            ctx.DrawLine(corner, pt, new Point(pt.X + dx * arm, pt.Y));
            ctx.DrawLine(corner, pt, new Point(pt.X, pt.Y + dy * arm));
        }
    }

    private int CropHandleAt(Point p)
    {
        var shown = Shown();
        double r = Px(18);
        var corners = new[] { shown.TopLeft, shown.TopRight, shown.BottomLeft, shown.BottomRight };
        for (int i = 0; i < 4; i++) if (Math.Abs(p.X - corners[i].X) <= r && Math.Abs(p.Y - corners[i].Y) <= r) return i;
        return shown.Contains(p) ? 4 : -1;
    }

    private void DragCrop(Point p)
    {
        var a = ToImage(_cropFrom); var b = ToImage(p);
        double dx = b.X - a.X, dy = b.Y - a.Y, min = Math.Max(24, Math.Min(_image!.Width, _image.Height) * 0.05);
        var s = _cropStart;
        double l = s.X, t = s.Y, r = s.Right, bo = s.Bottom;
        switch (_cropDrag)
        {
            case 0: l += dx; t += dy; break;
            case 1: r += dx; t += dy; break;
            case 2: l += dx; bo += dy; break;
            case 3: r += dx; bo += dy; break;
            case 4:
                dx = Math.Clamp(dx, -s.X, _image.Width - s.Right); dy = Math.Clamp(dy, -s.Y, _image.Height - s.Bottom);
                l += dx; r += dx; t += dy; bo += dy; break;
        }
        l = Math.Clamp(l, 0, _image.Width); r = Math.Clamp(r, 0, _image.Width); t = Math.Clamp(t, 0, _image.Height); bo = Math.Clamp(bo, 0, _image.Height);
        if (r - l < min) { if (_cropDrag is 0 or 2) l = r - min; else r = l + min; }
        if (bo - t < min) { if (_cropDrag is 0 or 1) t = bo - min; else bo = t + min; }
        _crop = new Rect(l, t, r - l, bo - t);
    }

    // ------------------------------------------------------------------ input
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        Focus();
        var p = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _image == null) return;
        if (_tool == Tool.Crop)
        {
            _cropDrag = CropHandleAt(p);
            if (_cropDrag >= 0)
            {
                // La vista resta ferma durante il trascinamento (i conti degli angoli la usano); il fuori ricompare.
                if (_view.Width <= 0) _view = Crop;
                _cropFrom = p; _cropStart = Crop; e.Pointer.Capture(this);
                StartCropAnimation();
            }
            return;
        }
        if (!Shown().Contains(p)) return;
        var at = ToImage(p);
        if (_tool == Tool.Picker)
        {
            // Il colore si prende dalla foto com'e' nel file (senza il filtro), nel punto cliccato.
            var c = _image.GetPixel(Math.Clamp((int)at.X, 0, _image.Width - 1), Math.Clamp((int)at.Y, 0, _image.Height - 1));
            _tool = _toolBeforePicker;
            SetColor(Color.FromRgb(c.Red, c.Green, c.Blue));
        }
        else if (_tool == Tool.Text)
        {
            FinishTyping();
            _typing = new Mark { Kind = Tool.Text, Color = _color, Size = TextSize };
            _typing.Points.Add(at);
            _marks.Add(_typing); Did(_typing);
        }
        else if (_tool == Tool.Eraser)
        {
            // Gomma: toglie il segno sotto il punto (il piu' in alto), annullabile.
            var hit = MarkAt(p);
            if (hit != null) { int index = _marks.IndexOf(hit); _marks.Remove(hit); Did(hit, erase: true, index); Changed(); }
        }
        else if (_tool is Tool.Pen or Tool.Marker or Tool.Line or Tool.Arrow or Tool.Box or Tool.Ellipse or Tool.Pixelate)
        {
            _drawing = new Mark { Kind = _tool, Color = _color, Width = StrokeWidth };
            _drawing.Points.Add(at); _drawing.Points.Add(at);
            _marks.Add(_drawing); Did(_drawing);
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _mouse = e.GetPosition(this);
        if (_tool == Tool.Crop && _image != null)
        {
            if (_cropDrag >= 0) DragCrop(_mouse);
            int over = _cropDrag >= 0 ? _cropDrag : CropHandleAt(_mouse);
            Cursor = over switch
            {
                0 or 3 => new Cursor(StandardCursorType.TopLeftCorner), 1 or 2 => new Cursor(StandardCursorType.TopRightCorner),
                4 => new Cursor(StandardCursorType.SizeAll), _ => Cursor.Default,
            };
            // [2026-11-23] Il disegno non dipende dal mouse: si ridisegna solo trascinando il ritaglio.
            if (_cropDrag >= 0) InvalidateVisual();
            return;
        }
        if (_drawing != null && _image != null)
        {
            var at = ToImage(_mouse);
            if (_drawing.Kind is Tool.Pen or Tool.Marker) _drawing.Points.Add(at); else _drawing.Points[1] = at;
        }
        else if (_image != null)
            Cursor = Shown().Contains(_mouse) ? new Cursor(_tool == Tool.Text ? StandardCursorType.Ibeam : StandardCursorType.Cross) : Cursor.Default;
        if (_drawing != null) InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_cropDrag >= 0)
        {
            _cropDrag = -1; e.Pointer.Capture(null);
            _settleAt = DateTime.UtcNow.AddSeconds(SettleDelaySeconds);
            StartCropAnimation();
            Changed(); return;
        }
        if (_drawing == null) return;
        // Un clic senza trascinare lascia un punto con la penna e niente con le forme.
        var a = _drawing.Points[0]; var b = _drawing.Points[1];
        double minSize = _drawing.Kind == Tool.Pixelate ? Math.Max(8, Math.Max(_image!.Width, _image.Height) * 0.01) : _drawing.Width;
        if (_drawing.Kind is not (Tool.Pen or Tool.Marker) && Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) < minSize)
        { _marks.Remove(_drawing); _actions.RemoveAll(x => ReferenceEquals(x.Mark, _drawing)); }
        _drawing = null;
        e.Pointer.Capture(null);
        Changed();
    }

    public void HandleKey(KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (_typing != null)
        {
            if (e.Key == Key.Enter) { FinishTyping(); }
            else if (e.Key == Key.Escape) { _marks.Remove(_typing); _typing = null; }
            else if (e.Key == Key.Back) { if (_typing.Text.Length > 0) _typing.Text = _typing.Text[..^1]; }
            else return;
            e.Handled = true; InvalidateVisual();
            return;
        }
        if (e.Key == Key.Escape) { if (CloseRequested != null) CloseRequested(); else Close(null); }
        else if (cmd && e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Choose(Tool.Redo);
        else if (cmd && e.Key == Key.Z) Choose(Tool.Undo);
        else if (cmd && e.Key == Key.Y) Choose(Tool.Redo);
        else if (cmd && e.Key == Key.S) Save();
        else return;
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_typing == null || string.IsNullOrEmpty(e.Text)) return;
        foreach (char ch in e.Text) if (!char.IsControl(ch) && _typing.Text.Length < 200) _typing.Text += ch;
        e.Handled = true;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ salvataggio (SkiaSharp, alla risoluzione della foto)
    public void Save()
    {
        FinishTyping();
        if (_image == null) return;
        try
        {
            string folder = Path.GetDirectoryName(_path) ?? "", name = Path.GetFileNameWithoutExtension(_path);
            bool png = string.Equals(Path.GetExtension(_path), ".png", StringComparison.OrdinalIgnoreCase);
            string extension = png ? ".png" : ".jpg";
            string target = Path.Combine(folder, name + " (modificata)" + extension);
            for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, name + " (modificata " + n + ")" + extension);
            using var rendered = RenderFull();
            using var output = Cropped ? CropBitmap(rendered, Crop) : rendered.Copy();
            try { Write(output, target, png); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Cartella di sola lettura (disco di rete): si salva in Immagini.
                string pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
                target = Path.Combine(pictures, Path.GetFileName(target));
                for (int n = 2; File.Exists(target); n++) target = Path.Combine(pictures, name + " (modificata " + n + ")" + extension);
                Write(output, target, png);
            }
            Close(target);
        }
        catch (Exception ex) { _note = "Salvataggio non riuscito: " + ex.Message; Changed(); }
    }

    private static SKBitmap CropBitmap(SKBitmap source, Rect r)
    {
        var rect = SKRectI.Round(new SKRect((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom));
        rect.Intersect(new SKRectI(0, 0, source.Width, source.Height));
        var result = new SKBitmap(Math.Max(1, rect.Width), Math.Max(1, rect.Height));
        using var c = new SKCanvas(result);
        c.DrawBitmap(source, rect, new SKRect(0, 0, result.Width, result.Height));
        return result;
    }

    private static void Write(SKBitmap image, string path, bool png)
    {
        using var data = image.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, png ? 100 : 93);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>Foto, filtro e segni alla risoluzione della foto (Render di Windows, con Skia).</summary>
    private SKBitmap RenderFull()
    {
        var image = _image!;
        var result = Filtered(image, _filter, _adjust);
        Pixelate(result, 1);
        using var c = new SKCanvas(result);
        foreach (var mark in _marks)
        {
            var color = new SKColor(mark.Color.R, mark.Color.G, mark.Color.B, mark.Color.A);
            if (mark.Kind == Tool.Text)
            {
                if (mark.Text.Length == 0) continue;
                using var font = new SKFont(SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Bold), (float)Math.Max(6, mark.Size));
                bool lightInk = (mark.Color.R * .299 + mark.Color.G * .587 + mark.Color.B * .114) > 127;
                float offset = Math.Max(1f, font.Size / 22f);
                var p = mark.Points[0];
                float baseline = (float)p.Y + font.Size * .35f;
                using var shadow = new SKPaint { Color = lightInk ? new SKColor(0, 0, 0, 140) : new SKColor(255, 255, 255, 140), IsAntialias = true };
                using var ink = new SKPaint { Color = color, IsAntialias = true };
                c.DrawText(mark.Text, (float)p.X + offset, baseline + offset, font, shadow);
                c.DrawText(mark.Text, (float)p.X, baseline, font, ink);
                continue;
            }
            if (mark.Points.Count < 2) continue;
            bool marker = mark.Kind == Tool.Marker;
            using var pen = new SKPaint
            {
                Color = marker ? color.WithAlpha(105) : color, IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1, mark.Width * (marker ? 4 : 1)), StrokeCap = marker ? SKStrokeCap.Butt : SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
            };
            var a = new SKPoint((float)mark.Points[0].X, (float)mark.Points[0].Y);
            var b = new SKPoint((float)mark.Points[^1].X, (float)mark.Points[^1].Y);
            var box = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            switch (mark.Kind)
            {
                case Tool.Pen: case Tool.Marker:
                {
                    using var path = new SKPath();
                    path.MoveTo(a);
                    foreach (var q in mark.Points.Skip(1)) path.LineTo((float)q.X, (float)q.Y);
                    c.DrawPath(path, pen);
                    break;
                }
                case Tool.Line: c.DrawLine(a, b, pen); break;
                case Tool.Box: c.DrawRect(box, pen); break;
                case Tool.Ellipse: c.DrawOval(box, pen); break;
                case Tool.Arrow:
                {
                    float length = SKPoint.Distance(a, b);
                    if (length < 1) break;
                    float head = Math.Min(length * .45f, pen.StrokeWidth * 4.5f + 6), ux = (b.X - a.X) / length, uy = (b.Y - a.Y) / length;
                    var neck = new SKPoint(b.X - ux * head, b.Y - uy * head);
                    c.DrawLine(a, neck, pen);
                    using var tip = new SKPath();
                    tip.MoveTo(b); tip.LineTo(neck.X - uy * head * .5f, neck.Y + ux * head * .5f); tip.LineTo(neck.X + uy * head * .5f, neck.Y - ux * head * .5f); tip.Close();
                    using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
                    c.DrawPath(tip, fill);
                    break;
                }
            }
        }
        return result;
    }
}
