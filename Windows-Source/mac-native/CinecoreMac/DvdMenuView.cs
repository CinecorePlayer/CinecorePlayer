using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Pulsanti dei menu DVD (sottoimmagine del disco con l'evidenziazione del pulsante scelto) disegnati sopra il video,
/// allineati al fotogramma come lo mostra il motore (16:9 o 4:3 al centro della finestra). Non riceve clic: frecce,
/// Invio e mouse li passa MainWindow al navigatore.
/// </summary>
internal sealed class DvdMenuView : Control
{
    private MacDvdNav? _nav;
    private Func<double>? _enginePosition;
    private MacDvdNav.Overlay? _shown;
    private WriteableBitmap? _bitmap;
    private readonly DispatcherTimer _timer;

    public DvdMenuView()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Render, (_, _) => Refresh());
        _timer.Stop(); // parte da Attach con un disco
    }

    public void Attach(MacDvdNav? nav, Func<double> enginePosition)
    {
        _nav = nav; _enginePosition = enginePosition;
        if (nav != null) _timer.Start(); else { _timer.Stop(); Clear(); }
    }

    public bool Active => _nav != null;

    private void Clear()
    {
        if (_shown == null) return;
        _shown = null; _bitmap?.Dispose(); _bitmap = null;
        InvalidateVisual();
    }

    private void Refresh()
    {
        if (_nav is not { } nav || _enginePosition == null) return;
        var overlay = nav.OverlayAt(_enginePosition());
        if (ReferenceEquals(overlay, _shown)) return;
        _shown = overlay;
        _bitmap?.Dispose(); _bitmap = null;
        if (overlay != null)
        {
            _bitmap = new WriteableBitmap(new PixelSize(overlay.W, overlay.H), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var fb = _bitmap.Lock();
            for (int y = 0; y < overlay.H; y++)
                System.Runtime.InteropServices.Marshal.Copy(overlay.Bgra, y * overlay.W * 4, fb.Address + y * fb.RowBytes, overlay.W * 4);
        }
        InvalidateVisual();
    }

    /// <summary>Riquadro del fotogramma del DVD nella finestra (come lo adatta il motore).</summary>
    private Rect FrameRect()
    {
        double aspect = _nav?.Wide == false ? 4.0 / 3 : 16.0 / 9;
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return default;
        double fw = w, fh = w / aspect;
        if (fh > h) { fh = h; fw = h * aspect; }
        return new Rect((w - fw) / 2, (h - fh) / 2, fw, fh);
    }

    /// <summary>Punto della finestra nelle coordinate del fotogramma del DVD (null se fuori dall'immagine).</summary>
    public (int X, int Y)? ToFrame(Point p)
    {
        if (_nav is not { } nav) return null;
        var r = FrameRect();
        if (r.Width <= 0 || !r.Contains(p)) return null;
        return ((int)((p.X - r.X) / r.Width * nav.FrameWidth), (int)((p.Y - r.Y) / r.Height * nav.FrameHeight));
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap == null || _shown is not { } o) return;
        var r = FrameRect();
        if (r.Width <= 0) return;
        double sx = r.Width / o.FrameW, sy = r.Height / o.FrameH;
        var dest = new Rect(r.X + o.X * sx, r.Y + o.Y * sy, o.W * sx, o.H * sy);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
            context.DrawImage(_bitmap, new Rect(0, 0, o.W, o.H), dest);
    }
}
