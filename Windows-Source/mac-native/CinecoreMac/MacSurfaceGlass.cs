using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CinecoreMac;

/// <summary>
/// Vetro vero di macOS per superfici sopra la pagina (barra della musica, coda): una finestra figlia trasparente sopra
/// quella dell'app, che non prende il mouse. macOS 26 e successivi: Liquid Glass (NSGlassEffectView); prima:
/// NSVisualEffectView con la sfocatura dal vivo della pagina sotto. Il contenuto (testi, icone) lo disegna Avalonia in
/// un'immagine che sta sopra il vetro. Senza la libreria nativa (prove senza pacchetto) Available e' falso e chi la usa
/// torna al vetro disegnato.
/// </summary>
internal sealed class MacSurfaceGlass : IDisposable
{
    private const string Bridge = "libCinecoreGlass.dylib";
    [DllImport(Bridge)] private static extern IntPtr CinecoreSurfaceCreate(IntPtr parent);
    [DllImport(Bridge)] private static extern void CinecoreSurfaceSetPanels(IntPtr surface, CGRect[] frames, double[] radii, nuint count, int dark, double alpha);
    [DllImport(Bridge)] private static extern void CinecoreSurfaceSetForeground(IntPtr surface, int index, IntPtr pixels, int width, int height, int stride);
    [DllImport(Bridge)] private static extern void CinecoreSurfaceClose(IntPtr surface);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect { public double X, Y, W, H; }

    private static bool _unavailable = !OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("CINECORE_SURFACE_GLASS") == "0";
    private readonly TopLevel _top;
    private IntPtr _surface;
    private string _panelsKey = "";
    private readonly Dictionary<int, string> _foregroundKeys = new();

    public MacSurfaceGlass(TopLevel top) { _top = top; }

    /// <summary>Vero se il vetro nativo e' attivo (dopo il primo Show riuscito).</summary>
    public bool Active => _surface != IntPtr.Zero && !_unavailable;
    public static bool Supported => !_unavailable;

    private bool Ensure()
    {
        if (_unavailable) return false;
        if (_surface != IntPtr.Zero) return true;
        if (_top.TryGetPlatformHandle() is not { } handle) return false;
        IntPtr window = handle.HandleDescriptor == "NSWindow" ? handle.Handle : Send(handle.Handle, sel_registerName("window"));
        if (window == IntPtr.Zero) return false;
        try { _surface = CinecoreSurfaceCreate(window); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine("vetro nativo non disponibile: " + ex.Message);
            _unavailable = true;
        }
        return _surface != IntPtr.Zero;
    }

    /// <summary>Pannelli in coordinate della finestra (punti, origine in alto a sinistra); alpha 0 nasconde.</summary>
    public bool Show(IReadOnlyList<(Rect Bounds, double Radius)> panels, bool dark, double alpha)
    {
        if (panels.Count == 0) { Hide(); return Active; }
        if (!Ensure()) return false;
        var frames = new CGRect[panels.Count];
        var radii = new double[panels.Count];
        var key = new System.Text.StringBuilder();
        for (int i = 0; i < panels.Count; i++)
        {
            var r = panels[i].Bounds;
            frames[i] = new CGRect { X = Math.Round(r.X), Y = Math.Round(r.Y), W = Math.Round(r.Width), H = Math.Round(r.Height) };
            radii[i] = panels[i].Radius;
            key.Append(frames[i].X).Append(',').Append(frames[i].Y).Append(',').Append(frames[i].W).Append(',').Append(frames[i].H).Append(';');
        }
        key.Append(dark).Append('|').Append(Math.Round(alpha, 3)).Append('|').Append(_top.Bounds.Size);
        string k = key.ToString();
        if (k == _panelsKey) return true;
        _panelsKey = k;
        // Coordinate AppKit capovolte: la vista contenitore e' "flipped", quindi l'origine resta in alto.
        CinecoreSurfaceSetPanels(_surface, frames, radii, (nuint)frames.Length, dark ? 1 : 0, alpha);
        // Un pannello nuovo o ridimensionato rivuole il suo contenuto.
        if (panels.Count < _foregroundKeys.Count) _foregroundKeys.Clear();
        return true;
    }

    public void Hide()
    {
        if (_surface == IntPtr.Zero || _panelsKey == "hidden") return;
        _panelsKey = "hidden";
        CinecoreSurfaceSetPanels(_surface, Array.Empty<CGRect>(), Array.Empty<double>(), 0, 1, 0);
        _foregroundKeys.Clear();
    }

    /// <summary>Contenuto del pannello <paramref name="index"/>: lo si ridisegna solo se <paramref name="key"/> cambia.</summary>
    public void SetForeground(int index, string key, PixelSize size, Action<DrawingContextAction> draw)
    {
        if (!Active || size.Width < 1 || size.Height < 1) return;
        if (_foregroundKeys.TryGetValue(index, out var old) && old == key) return;
        _foregroundKeys[index] = key;
        var dpi = new Vector(96, 96);
        using var bitmap = new RenderTargetBitmap(size, dpi);
        using (var ctx = bitmap.CreateDrawingContext()) draw(new DrawingContextAction(ctx));
        using var foreground = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var pixels = foreground.Lock();
        bitmap.CopyPixels(pixels, AlphaFormat.Premul);
        CinecoreSurfaceSetForeground(_surface, index, pixels.Address, pixels.Size.Width, pixels.Size.Height, pixels.RowBytes);
    }

    public void Dispose()
    {
        if (_surface == IntPtr.Zero) return;
        try { CinecoreSurfaceClose(_surface); } catch { }
        _surface = IntPtr.Zero;
    }
}

/// <summary>Il contesto di disegno passato a chi prepara il contenuto di un pannello.</summary>
internal readonly record struct DrawingContextAction(Avalonia.Media.DrawingContext Context);
