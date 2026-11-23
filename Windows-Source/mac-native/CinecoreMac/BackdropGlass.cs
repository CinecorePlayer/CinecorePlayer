using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace CinecoreMac;

/// <summary>
/// Vetro dal vivo: sfoca cio' che e' gia' disegnato sotto il riquadro, nello stesso fotogramma (come il vetro di
/// sistema del Mac). Prima il vetro di Windows era una copia della pagina rifatta 220 ms dopo ogni cambio: arrivava in
/// ritardo e cambiava di colpo a seconda di cosa si apriva. Se la superficie non e' leggibile (cattura in una bitmap)
/// disegna solo la tinta.
/// </summary>
/// <summary>
/// [2026-11-22] Il vetro gia' calcolato di un riquadro che sta fermo (un pannello del menu): mentre si passa sulle voci
/// si ridisegna solo l'immagine, senza rifare istantanea e sfocatura a ogni movimento. Vale finche' il riquadro resta
/// nello stesso posto in pixel; chi lo crea lo libera quando il riquadro sparisce.
/// </summary>
internal sealed class GlassCache : IDisposable
{
    public SKImage? Image;
    public SKRectI Device;
    public void Dispose() { Image?.Dispose(); Image = null; }
}

internal sealed class BackdropGlassOp : ICustomDrawOperation
{
    private GlassCache? _cache;
    private readonly Rect _rect;
    private readonly double _radius, _blur;
    private readonly float[] _matrix;
    private readonly Color _fallback;
    private readonly byte _alpha;
    private readonly bool _liquid;

    private readonly double _elevation;
    private readonly bool _reflect;

    /// <summary>
    /// Il vetro del vecchio player Windows .NET (Utilities/GlassSurface.CreateLiquid, "come la barra e la coda di Tidal"),
    /// rifatto fedele il 22/11: la pagina sotto sfocata a fondo (30 px), saturazione 1,5, una curva morbida che nel tema
    /// scuro schiaccia le luci (oltre ~165 non sale: una copertina bianca sotto non acceca il testo) e nel chiaro le ombre,
    /// una velatura neutra (grafite 40,42,46 al 46% nello scuro, latte 238,242,248 al 69% nel chiaro), un filo di 1 px
    /// sul bordo e un rumore di un livello contro il banding. Niente riflesso, filo di luce o ombra grande: la versione
    /// del 21/11 li aggiungeva (con saturazione 1,8) ed era molto peggio a vedersi.
    /// <paramref name="elevation"/> resta per compatibilita' e non disegna nulla.
    /// </summary>
    /// <param name="reflect">La "luce dietro" della barra della musica del vecchio player (MusicTransportBar.
    /// BuildReflectedBackdrop): dietro la barra la pagina e' vuota, quindi il vetro non sfoca quello che ha sotto ma
    /// una fascia alta della pagina sopra di lui (fino a ~560 px: copertine, locandine), schiacciata e specchiata. Il
    /// "uno" della fascia (la riga piu' lontana) finisce sul fondo della barra, la riga appena sopra in cima.</param>
    public static BackdropGlassOp Liquid(Rect rect, double radius, double opacity = 1, double elevation = 1, bool reflect = false, GlassCache? cache = null)
        => new(rect, radius, opacity, elevation, reflect) { _cache = cache };

    private BackdropGlassOp(Rect rect, double radius, double opacity, double elevation, bool reflect)
    {
        _reflect = reflect;
        bool light = UiTheme.IsLight;
        _rect = rect; _radius = radius; _blur = 30; _liquid = true; _elevation = 0;
        _alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        // Se la superficie non si legge (cattura in una bitmap): la velatura del vetro, quasi piena.
        _fallback = light ? Color.FromArgb(240, 238, 242, 248) : Color.FromArgb(240, 34, 36, 40);
        const float sat = 1.5f, lr = .2126f * (1 - sat), lg = .7152f * (1 - sat), lb = .0722f * (1 - sat);
        _matrix = new[]
        {
            lr + sat, lg, lb, 0, 0,
            lr, lg + sat, lb, 0, 0,
            lr, lg, lb + sat, 0, 0,
            0, 0, 0, 1, 0,
        };
    }

    /// <summary>Curva morbida per canale di GlassSurface (Limit 165): luci compresse nello scuro, ombre nel chiaro.</summary>
    private static byte[] ToneCurve(bool light)
    {
        const double Limit = 165;
        var curve = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            double input = light ? 255 - v : v;
            double soft = Limit * (1 - Math.Exp(-input / Limit));
            curve[v] = (byte)Math.Clamp(Math.Round(light ? 255 - soft : soft), 0, 255);
        }
        return curve;
    }
    private static readonly byte[] DarkCurve = ToneCurve(false), LightCurve = ToneCurve(true), Identity = BuildIdentity();
    private static byte[] BuildIdentity() { var t = new byte[256]; for (int i = 0; i < 256; i++) t[i] = (byte)i; return t; }

    /// <summary>
    /// Rumore di un livello (VisualDither.AddNoise di Windows, ampiezza 1): una tessera 128x128 di punti bianchi e neri
    /// quasi trasparenti, ripetuta sul vetro. Rompe i gradini della sfocatura scura.
    /// </summary>
    private static SKImage? _noise;
    private static SKImage Noise()
    {
        if (_noise != null) return _noise;
        const int n = 128;
        var info = new SKImageInfo(n, n, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = new byte[n * n * 4];
        var rng = new Random(20261122);
        for (int i = 0; i < n * n; i++)
        {
            // Alfa 0-2 su 255: circa +-1 livello dopo la fusione; premoltiplicato, bianco = alfa, nero = 0.
            int a = rng.Next(3);
            byte c = rng.Next(2) == 0 ? (byte)0 : (byte)a;
            pixels[i * 4] = c; pixels[i * 4 + 1] = c; pixels[i * 4 + 2] = c; pixels[i * 4 + 3] = (byte)a;
        }
        using var data = SKData.CreateCopy(pixels);
        return _noise = SKImage.FromPixels(info, data, n * 4);
    }

    /// <param name="saturation">1 = colori pieni, 0 = grigio.</param>
    /// <param name="brightness">moltiplica la luce di cio' che sta sotto.</param>
    /// <param name="lift">luce aggiunta (0..1), schiarisce il vetro nel tema chiaro.</param>
    /// <param name="opacity">trasparenza del vetro: le operazioni Skia non seguono PushOpacity, va passata qui.</param>
    public BackdropGlassOp(Rect rect, double radius, double blur, double saturation, double brightness, double lift, Color fallback, double opacity = 1)
    {
        _rect = rect; _radius = radius; _blur = blur; _fallback = fallback;
        _alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        const float lr = .2126f, lg = .7152f, lb = .0722f;
        float s = (float)saturation, b = (float)brightness, o = (float)lift;
        _matrix = new[]
        {
            b * (lr * (1 - s) + s), b * lg * (1 - s), b * lb * (1 - s), 0, o,
            b * lr * (1 - s), b * (lg * (1 - s) + s), b * lb * (1 - s), 0, o,
            b * lr * (1 - s), b * lg * (1 - s), b * (lb * (1 - s) + s), 0, o + .005f,
            0, 0, 0, 1, 0,
        };
    }

    // L'ombra esce dal riquadro: va dichiarata, se no Avalonia non ridisegna quella striscia.
    public Rect Bounds => _liquid && _elevation > 0 ? _rect.Inflate(new Thickness(ShadowReach, ShadowReach, ShadowReach, ShadowReach + 8)) : _rect;
    private const double ShadowReach = 30;
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature == null) return;
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        var local = new SKRect((float)_rect.X, (float)_rect.Y, (float)_rect.Right, (float)_rect.Bottom);
        var shape = new SKRoundRect(local, (float)_radius, (float)_radius);
        if (_alpha == 0) return;
        bool light = UiTheme.IsLight;
        canvas.Save();
        try
        {
            canvas.ClipRoundRect(shape, SKClipOperation.Intersect, true);
            var matrix = canvas.TotalMatrix;
            var device = SKRectI.Round(matrix.MapRect(local));
            if (_cache is { Image: { } cached } && _cache.Device == device && _alpha == 255)
            {
                canvas.ResetMatrix();
                canvas.DrawImage(cached, device.Left, device.Top);
                canvas.SetMatrix(matrix);
            }
            else
            {
                bool live = DrawBackdrop(lease, canvas, local);
                if (!live)
                {
                    using var fill = new SKPaint { Color = new SKColor(_fallback.R, _fallback.G, _fallback.B, (byte)(_fallback.A * _alpha / 255)), IsAntialias = true };
                    canvas.DrawRect(local, fill);
                }
                if (_liquid) DrawNoise(canvas, local);
                // Pieno e fermo (non in dissolvenza o in crescita): si tiene il risultato per i prossimi disegni.
                if (_cache != null && live && _alpha == 255 && lease.SkSurface is { } surface)
                    try { _cache.Dispose(); _cache.Image = surface.Snapshot(device); _cache.Device = device; } catch { _cache.Image = null; }
            }
        }
        finally { canvas.Restore(); }
        if (_liquid) DrawEdge(canvas, local, light);
    }

    private void DrawNoise(SKCanvas canvas, SKRect r)
    {
        if (_alpha < 8) return;
        using var shader = Noise().ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(r, paint);
    }

    /// <summary>Il filo di 1 px attorno all'isola del vecchio player (MusicTransportBar.PaintSurface).</summary>
    private void DrawEdge(SKCanvas canvas, SKRect r, bool light)
    {
        float hair = 1f / Math.Max(1, Math.Abs(canvas.TotalMatrix.ScaleX));
        byte k(int v) => (byte)(v * _alpha / 255);
        using var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = hair, IsAntialias = true,
            Color = light ? new SKColor(20, 30, 45, k(26)) : new SKColor(255, 255, 255, k(20)) };
        var inner = r; inner.Inflate(-hair / 2, -hair / 2);
        float rad = (float)Math.Max(0, _radius - hair / 2);
        canvas.DrawRoundRect(new SKRoundRect(inner, rad, rad), edge);
    }

    /// <summary>
    /// La fascia di pagina sopra il vetro (alta 7 volte il vetro, almeno 560 px, fino a 1,5 volte il vetro sopra di
    /// lui), rimpicciolita di 8 volte con una sfocatura che fa da filtro e specchiata in verticale: l'immagine che il
    /// vetro sfoca al posto di cio' che ha sotto. Null se sopra non c'e' abbastanza pagina.
    /// </summary>
    private static SKImage? Reflection(ISkiaSharpApiLease lease, SKSurface surface, SKRect device, float scale)
    {
        // La pagina sopra sta fuori dal ritaglio quando si ridisegna solo la barra: i limiti sono quelli di tutta la
        // superficie (l'istantanea intera su GPU non copia nulla finche' non si scrive).
        SKRectI all;
        using (var whole = surface.Snapshot()) { if (whole == null) return null; all = new SKRectI(0, 0, whole.Width, whole.Height); }
        float above = Math.Min(device.Top - all.Top, Math.Max(device.Height * 7, 560 * scale));
        if (above < Math.Max(24 * scale, device.Height * 2)) return null;
        var src = SKRectI.Round(new SKRect(device.Left, device.Top - above, device.Right, device.Top - 1.5f * device.Height));
        src.Intersect(all);
        if (src.Width < 8 || src.Height < 8) return null;
        // Subset di un'immagine su GPU restituisce null in SkiaSharp 2.88: l'istantanea si prende gia' ritagliata.
        using var snap = surface.Snapshot(src);
        if (snap == null) return null;
        int w = Math.Max(4, (int)Math.Ceiling(device.Width / 8)), h = Math.Max(4, (int)Math.Ceiling(device.Height / 8));
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var small = lease.GrContext is GRContext gr ? SKSurface.Create(gr, false, info) : SKSurface.Create(info);
        if (small == null) return null;
        var c = small.Canvas;
        c.Clear(SKColors.Black);
        float sx = w / (float)src.Width, sy = h / (float)src.Height;
        c.Translate(0, h);
        c.Scale(sx, -sy);
        // Sfocatura nelle coordinate della fascia: circa un pixel dell'immagine piccola, niente scalini o sfarfallii
        // quando la pagina sopra scorre.
        using var soften = SKImageFilter.CreateBlur(0.7f / sx, 0.7f / sy, SKShaderTileMode.Clamp);
        using (var paint = new SKPaint { ImageFilter = soften, FilterQuality = SKFilterQuality.Low })
            c.DrawImage(snap, 0, 0, paint);
        c.Flush();
        return small.Snapshot();
    }

    private bool DrawBackdrop(ISkiaSharpApiLease lease, SKCanvas canvas, SKRect local)
    {
        var surface = lease.SkSurface;
        var matrix = canvas.TotalMatrix;
        if (surface == null || matrix.SkewX != 0 || matrix.SkewY != 0) return false;
        float scale = Math.Abs(matrix.ScaleX);
        float sigma = (float)(_blur * scale);
        var device = matrix.MapRect(local);
        var limit = canvas.DeviceClipBounds;
        var all = surface.Canvas.DeviceClipBounds;
        // Un margine attorno perche' i bordi del vetro prendano i colori vicini e non si scuriscano.
        int pad = (int)Math.Ceiling(sigma * 2);
        var src = SKRectI.Round(new SKRect(device.Left - pad, device.Top - pad, device.Right + pad, device.Bottom + pad));
        src.Intersect(all);
        if (src.Width <= 2 || src.Height <= 2 || limit.IsEmpty) return false;
        using var reflection = _reflect ? Reflection(lease, surface, device, scale) : null;
        if (reflection != null)
        {
            // Il riflesso copre il vetro e il margine della sfocatura (i bordi prendono gli stessi colori).
            var dest = new SKRect(device.Left - pad, device.Top - pad, device.Right + pad, device.Bottom + pad);
            src = SKRectI.Round(dest);
        }
        using var image = reflection == null ? surface.Snapshot(src) : null;
        if (image == null && reflection == null) return false;
        using var blur = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var saturate = SKColorFilter.CreateColorMatrix(_matrix);
        bool lightTheme = UiTheme.IsLight;
        var table = lightTheme ? LightCurve : DarkCurve;
        using var curve = _liquid ? SKColorFilter.CreateTable(Identity, table, table, table) : null;
        using var tone = curve != null ? SKColorFilter.CreateCompose(curve, saturate) : null;
        // [2026-11-22] Il riflesso e' la media di una fascia alta (copertine e spazi scuri insieme): da solo arrivava
        // spento, "il vetro si vede solo nella coda". Luce x1,9 prima della curva (che resta il tetto per il testo).
        using var boost = reflection != null && !lightTheme
            ? SKColorFilter.CreateColorMatrix(new float[] { 1.9f, 0, 0, 0, 0, 0, 1.9f, 0, 0, 0, 0, 0, 1.9f, 0, 0, 0, 0, 0, 1, 0 }) : null;
        using var lit = boost != null && tone != null ? SKColorFilter.CreateCompose(tone, boost) : null;
        using (var paint = new SKPaint { ImageFilter = blur, ColorFilter = lit ?? tone ?? saturate, IsAntialias = true, Color = new SKColor(255, 255, 255, _alpha) })
        {
            canvas.ResetMatrix();
            if (reflection != null)
            {
                paint.FilterQuality = SKFilterQuality.Low; // bilineare
                canvas.DrawImage(reflection, new SKRect(src.Left, src.Top, src.Right, src.Bottom), paint);
            }
            else canvas.DrawImage(image, src.Left, src.Top, paint);
            canvas.SetMatrix(matrix);
        }
        if (_liquid)
        {
            // Velatura neutra di GlassSurface: latte al 69% nel chiaro (sotto passava il contorno della pagina), grafite al 46%.
            // Con il riflesso nello scuro la grafite scende al 30%: sopra c'e' gia' la luce delle copertine da far vedere.
            int veilAlpha = UiTheme.IsLight ? 176 : reflection != null ? 76 : 118;
            var tint = UiTheme.IsLight ? new SKColor(238, 242, 248, (byte)(veilAlpha * _alpha / 255)) : new SKColor(40, 42, 46, (byte)(veilAlpha * _alpha / 255));
            using var veil = new SKPaint { Color = tint };
            canvas.DrawRect(local, veil);
        }
        return true;
    }
}
