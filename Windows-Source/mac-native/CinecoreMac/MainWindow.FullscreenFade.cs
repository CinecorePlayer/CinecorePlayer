#if CINECORE_WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Entrata e uscita dallo schermo intero su Windows, come su macOS: si fotografa lo schermo, la finestra cambia misura
/// una volta sola (nascosta sotto la foto) e ad allargarsi o restringersi e' solo l'immagine, disegnata a ogni
/// refresh del monitor. Ridimensionare davvero la finestra a ogni passo faceva tremare tutto: Windows e il contenuto
/// non stanno dietro a 60 cambi di misura al secondo. Alla fine la foto sfuma nel contenuto vero.
/// Entrando la foto copre tutto lo schermo (fuori dal riquadro c'e' il desktop di un attimo prima); uscendo la
/// finestra resta a schermo intero ma trasparente fuori dal riquadro, cosi' si vede il desktop vero.
/// </summary>
public sealed partial class MainWindow
{
    private const double RevealMs = 170, VeilMaxWaitMs = 700;
    // Rallentabile per le prove (CINECORE_QA_ZOOM_MS).
    private static readonly double ZoomMs = double.TryParse(Environment.GetEnvironmentVariable("CINECORE_QA_ZOOM_MS"), out var qaZoom) ? qaZoom : 450;
    private Border? _fsVeil;
    private Grid? _fsSurface;
    private bool _fsFading, _fsFreezeVideo;
    private WinNative.RECT? _fsWindowedVisible;
    private PixelRect? _fsWindowedClient;

    /// <summary>Velo nero (riserva) e livello dello zoom, sopra tutto il contenuto.</summary>
    private void AddFullscreenVeil(Grid surface)
    {
        _fsSurface = surface;
        _fsVeil = new Border { Background = Brushes.Black, Opacity = 0, IsVisible = false, IsHitTestVisible = false };
        surface.Children.Add(_fsVeil);
    }

    private async void ToggleFullscreenWithFade()
    {
        IntPtr hwnd = WinNative.Handle(this);
        if (hwnd == IntPtr.Zero) { WindowState = IsFullscreen ? WindowState.Normal : WindowState.FullScreen; return; }
        if (_fsFading) return;
        _fsFading = true;
        // Il menu del tasto destro sparisce subito: la sua dissolvenza finiva nella foto e restava sopra lo zoom.
        if (_menu.IsOpen || _menu.HasGhost) { _menu.CloseNow(); await WaitFrames(2); }
        bool entering = !IsFullscreen;
        var total = Stopwatch.StartNew();
        Window? host = null;
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null) { await FallbackFade(entering); return; }
            var m = screen.Bounds;
            var monitorRect = new PixelRect(m.X, m.Y, m.Width, m.Height);
            var shot = ScreenShot.Capture(monitorRect);
            if (shot == null) { await FallbackFade(entering); return; }
            // Un fotogramma vero del video, preso dalla sua finestra (senza l'HUD sopra): va sotto la schermata nuova.
            // Senza, durante lo zoom al posto del film c'era nero pieno per ~300 ms (il "flash" entrando e uscendo).
            var videoFrame = CaptureVideoFrame(hwnd);

            // La foto sta in una finestra a parte, ferma sopra tutto lo schermo: il player cambia misura sotto di lei
            // senza che l'immagine si sposti (disegnata dentro il player, a ogni cambio di misura saltava).
            var zoom = new FsZoomOverlay { IsHitTestVisible = false };
            PixelRect from, to, source;
            bool backdrop;
            if (entering)
            {
                var visible = WinNative.VisibleRect(hwnd);
                _fsWindowedVisible = visible;
                var wc0 = WinNative.ClientScreenRect(hwnd);
                _fsWindowedClient = new PixelRect(wc0.Left, wc0.Top, wc0.Width, wc0.Height);
                from = new PixelRect(visible.Left, visible.Top, visible.Width, visible.Height);
                to = monitorRect; source = Relative(from, monitorRect); backdrop = true;
            }
            else
            {
                from = monitorRect; source = new PixelRect(0, 0, m.Width, m.Height); backdrop = false;
                to = _fsWindowedVisible is { } w && w.Width > 100 ? new PixelRect(w.Left, w.Top, w.Width, w.Height)
                    : new PixelRect(m.X + m.Width / 6, m.Y + m.Height / 6, m.Width * 2 / 3, m.Height * 2 / 3);
            }
            zoom.Start(shot, monitorRect, source, from, to, backdrop, entering ? 8 : 0, entering ? 0 : 8);
            if (videoFrame != null)
            {
                // Dove sara' il video alla fine: tutto lo schermo entrando, l'area della finestra di prima uscendo.
                var videoTo = entering ? monitorRect : _fsWindowedClient is { Width: > 100 } wc ? wc : to;
                zoom.SetVideo(videoFrame, videoTo);
            }
            host = await ShowZoomHost(zoom, monitorRect, screen.Scaling);
            Console.Error.WriteLine($"[schermo intero] {DateTime.Now:HH:mm:ss.fff} {(entering ? "entrata" : "uscita")}: foto pronta in {total.ElapsedMilliseconds} ms");

            // Sotto la foto: cambio di misura (una volta sola) e il video che lo segue.
            WindowState = entering ? WindowState.FullScreen : WindowState.Normal;
            // Il player a schermo intero puo' passare davanti: la foto torna sopra prima del prossimo fotogramma.
            IntPtr hostHandle = WinNative.Handle(host);
            if (hostHandle != IntPtr.Zero) WinNative.SetWindowPos(hostHandle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            if (!entering)
            {
                await WaitFrames(4); await WaitVideoSettled();
                // Anche uscendo: la finestra vera in un'immagine, che entra verso la fine del restringimento. Senza, la foto
                // a schermo intero sfumava sopra la finestra con un'altra disposizione (scritte e riquadri doppi).
                if (RenderSelf() is { } windowed) zoom.SetTarget(windowed, .8, .95);
            }
            else
            {
                // La schermata vera a schermo intero (disposizione nuova) in un'immagine: durante l'ingrandimento la foto
                // della finestra sfuma in lei, cosi' alla fine non c'e' nessun cambio quando la foto se ne va. Prima la
                // foto ingrandita aveva ancora la disposizione della finestra e alla fine "si riaggiornava".
                await WaitClientSize(m.Width, m.Height);
                if (RenderSelf() is { } target) zoom.SetTarget(target, .02, .18);
            }
            Console.Error.WriteLine($"[schermo intero] {DateTime.Now:HH:mm:ss.fff} zoom parte");
            await Animate(zoom, host);
            Console.Error.WriteLine($"[schermo intero] {DateTime.Now:HH:mm:ss.fff} zoom finito");
            if (entering) await WaitVideoSettled();
            else await WaitFrames(2);
            // Dopo il cambio di misura madVR resta nero per qualche fotogramma: la foto se ne va solo quando il video
            // si vede davvero (prima: nero per ~150-300 ms appena finito lo zoom).
            if (videoFrame != null) await WaitVideoPresented(hwnd);
            Console.Error.WriteLine($"[schermo intero] {DateTime.Now:HH:mm:ss.fff} dissolvenza parte");
            await FadeOut(zoom, host, entering ? RevealMs : 110);
            if (!entering) _fsWindowedVisible = null;
            Console.Error.WriteLine($"[schermo intero] {DateTime.Now:HH:mm:ss.fff} fatto in {total.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[schermo intero] transizione: " + ex.Message);
            if (IsFullscreen != entering) WindowState = entering ? WindowState.FullScreen : WindowState.Normal;
        }
        finally
        {
            if (host != null) { (host.Content as FsZoomOverlay)?.Stop(); host.Close(); }
            _fsFading = false;
        }
    }

    private async Task<Window> ShowZoomHost(FsZoomOverlay zoom, PixelRect monitor, double scaling)
    {
        var host = new Window
        {
            SystemDecorations = SystemDecorations.None,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = monitor.Position,
            Width = monitor.Width / scaling,
            Height = monitor.Height / scaling,
            Content = zoom,
            Title = "Cinecore",
        };
        var opened = new TaskCompletionSource();
        host.Opened += (_, _) =>
        {
            IntPtr h = WinNative.Handle(host);
            if (h != IntPtr.Zero)
            {
                int square = 1 /* DWMWCP_DONOTROUND */, none = unchecked((int)0xFFFFFFFE);
                WinNative.DwmSetWindowAttribute(h, 33, ref square, 4);
                WinNative.DwmSetWindowAttribute(h, 34, ref none, 4);
                // Misura esatta in pixel (la scala dello schermo puo' differire da quella del player).
                WinNative.SetWindowPos(h, IntPtr.Zero, monitor.X, monitor.Y, monitor.Width, monitor.Height, 0x0010 | 0x0004);
            }
            opened.TrySetResult();
        };
        // Nascosta per DWM finche' non ha disegnato la foto: il primo fotogramma di una finestra nuova e' vuoto
        // (lampo nero o bianco).
        IntPtr early = host.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        int cloak = 1;
        bool cloaked = early != IntPtr.Zero && WinNative.DwmSetWindowAttribute(early, 13 /* DWMWA_CLOAK */, ref cloak, 4) == 0;
        host.Show();
        await opened.Task;
        for (int i = 0; i < 3; i++) await NextFrame(host);
        if (cloaked)
        {
            int off = 0;
            WinNative.DwmSetWindowAttribute(WinNative.Handle(host), 13, ref off, 4);
            await NextFrame(host); await NextFrame(host);
        }
        return host;
    }

    /// <summary>
    /// Il fotogramma nella finestra del video (madVR/MPC-VR nel VideoHost, o quella di MPV), con PrintWindow e
    /// PW_RENDERFULLCONTENT: esce pulito, senza l'HUD che sta nella finestra sopra. Solo la parte sotto l'area client
    /// del player (in finestra la finestra del video parte da sotto la barra del titolo).
    /// </summary>
    private Bitmap? CaptureVideoFrame(IntPtr player)
    {
        try
        {
            if (!_video.IsRunning || !_videoReady) return null;
            IntPtr video = VideoWindowHandle();
            if (video == IntPtr.Zero || !WinNative.GetWindowRect(video, out var vr)) return null;
            var client = WinNative.ClientScreenRect(player);
            int left = Math.Max(vr.Left, client.Left), top = Math.Max(vr.Top, client.Top);
            int right = Math.Min(vr.Right, client.Left + client.Width), bottom = Math.Min(vr.Bottom, client.Top + client.Height);
            if (right - left < 16 || bottom - top < 16) return null;
            var clock = Stopwatch.StartNew();
            var frame = ScreenShot.CaptureWindow(video, vr.Right - vr.Left, vr.Bottom - vr.Top, new PixelRect(left - vr.Left, top - vr.Top, right - left, bottom - top));
            Console.Error.WriteLine($"[schermo intero] {DateTime.Now:HH:mm:ss.fff} fotogramma del video {(frame != null ? "preso" : "non riuscito")} in {clock.ElapsedMilliseconds} ms");
            return frame;
        }
        catch (Exception ex) { Console.Error.WriteLine("[schermo intero] fotogramma del video: " + ex.Message); return null; }
    }

    private IntPtr VideoWindowHandle() => Engine == "mpv"
        ? MpvEngine.CurrentWindow
        : _video.ProcessId > 0 ? WinNative.TopWindowOf(_video.ProcessId, "Cinecore Video") : IntPtr.Zero;

    /// <summary>Fino a quando la finestra del video mostra qualcosa (non tutta nera), al massimo 600 ms.</summary>
    private async Task WaitVideoPresented(IntPtr player)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 600 && !_closing)
        {
            IntPtr video = VideoWindowHandle();
            if (video == IntPtr.Zero || !_video.IsRunning) return;
            if (ScreenShot.WindowHasImage(video)) break;
            await Task.Delay(40);
        }
        Console.Error.WriteLine($"[schermo intero] {DateTime.Now:HH:mm:ss.fff} video visibile dopo {clock.ElapsedMilliseconds} ms");
    }

    private static PixelRect Relative(PixelRect r, PixelRect monitor) => new(r.X - monitor.X, r.Y - monitor.Y, r.Width, r.Height);

    private async Task Animate(FsZoomOverlay zoom, TopLevel host)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ZoomMs);
            // Accelera e frena in modo morbido. Prima partiva di scatto (70% del movimento nei primi 100 ms): con lo
            // schermo a 24 Hz durante il film erano due o tre fotogrammi e sembrava un salto, non un'animazione.
            zoom.Progress = t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            zoom.InvalidateVisual();
            if (t >= 1) break;
            await NextFrame(host);
        }
        await NextFrame(host);
    }

    private async Task FadeOut(FsZoomOverlay zoom, TopLevel host, double ms)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            zoom.Opacity = 1 - t * t * (3 - 2 * t);
            if (t >= 1) break;
            await NextFrame(host);
        }
    }

    private async Task WaitVideoSettled()
    {
        if (!_video.IsRunning || !_videoReady) return;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < VeilMaxWaitMs && !_closing)
        {
            await Task.Delay(30);
            if (clock.ElapsedMilliseconds < 90) continue;
            if (!_video.IsRunning || !_videoReady || _lastRect == VideoRect()) break;
        }
        await Task.Delay(40);
    }

    private Task NextFrame(TopLevel? top = null)
    {
        var done = new TaskCompletionSource();
        (top ?? this).RequestAnimationFrame(_ => done.TrySetResult());
        return done.Task;
    }

    private async Task WaitFrames(int n) { for (int i = 0; i < n; i++) await NextFrame(); }

    /// <summary>Fino a quando la finestra ha davvero la misura nuova e il contenuto e' stato impaginato.</summary>
    private async Task WaitClientSize(int width, int height)
    {
        for (int i = 0; i < 20; i++)
        {
            await NextFrame();
            double s = RenderScaling > 0 ? RenderScaling : 1;
            if (Math.Abs(ClientSize.Width * s - width) < 4 && Math.Abs(ClientSize.Height * s - height) < 4) break;
        }
        await NextFrame();
    }

    /// <summary>Il contenuto del player, a pixel pieni (senza il video, che e' in un'altra finestra).</summary>
    private Bitmap? RenderSelf()
    {
        try
        {
            double s = RenderScaling > 0 ? RenderScaling : 1;
            var size = new PixelSize((int)Math.Round(ClientSize.Width * s), (int)Math.Round(ClientSize.Height * s));
            if (size.Width <= 0 || size.Height <= 0) return null;
            var clock = Stopwatch.StartNew();
            var bitmap = new RenderTargetBitmap(size, new Vector(96 * s, 96 * s));
            bitmap.Render(this);
            Console.Error.WriteLine($"[schermo intero] {DateTime.Now:HH:mm:ss.fff} schermata nuova pronta in {clock.ElapsedMilliseconds} ms");
            return bitmap;
        }
        catch (Exception ex) { Console.Error.WriteLine("[schermo intero] schermata nuova: " + ex.Message); return null; }
    }

    /// <summary>Riserva se la foto non riesce: dissolvenza al nero e cambio di misura nascosto.</summary>
    private async Task FallbackFade(bool entering)
    {
        if (_fsVeil is not { } veil) { WindowState = entering ? WindowState.FullScreen : WindowState.Normal; return; }
        veil.IsVisible = true;
        for (double t = 0; t <= 1; t += .12) { veil.Opacity = t; await NextFrame(); }
        veil.Opacity = 1;
        WindowState = entering ? WindowState.FullScreen : WindowState.Normal;
        await WaitFrames(6);
        await WaitVideoSettled();
        for (double t = 1; t >= 0; t -= .08) { veil.Opacity = t; await NextFrame(); }
        veil.Opacity = 0; veil.IsVisible = false;
    }

    /// <summary>La foto che si allarga o si restringe. Le misure sono in pixel dello schermo e si convertono a ogni
    /// fotogramma con la posizione vera della finestra, cosi' il cambio di misura sotto non la sposta.</summary>
    private sealed class FsZoomOverlay : Control
    {
        private Bitmap? _shot, _target, _video;
        private PixelRect _videoTo;
        public void SetVideo(Bitmap frame, PixelRect to) { _video?.Dispose(); _video = frame; _videoTo = to; }
        private PixelRect _monitor, _source, _from, _to;
        private bool _backdrop;
        private double _cornerFrom, _cornerTo;
        public double Progress;

        public void Start(Bitmap shot, PixelRect monitor, PixelRect source, PixelRect from, PixelRect to, bool backdrop, double cornerFrom, double cornerTo)
        {
            _shot?.Dispose();
            _shot = shot; _monitor = monitor; _source = source; _from = from; _to = to; _backdrop = backdrop;
            _cornerFrom = cornerFrom; _cornerTo = cornerTo; Progress = 0; Opacity = 1;
            IsVisible = true;
            InvalidateVisual();
        }

        private double _mixFrom, _mixTo = 1;
        /// <summary>Il riquadro d'arrivo, quando si sa solo dopo il cambio (PiP, mini player).</summary>
        public void Retarget(PixelRect to) { _to = to; InvalidateVisual(); }
        /// <summary>L'immagine d'arrivo e il tratto dell'animazione in cui prende il posto della foto.</summary>
        public void SetTarget(Bitmap target, double from, double to) { _target?.Dispose(); _target = target; _mixFrom = from; _mixTo = to; InvalidateVisual(); }

        public void Stop()
        {
            IsVisible = false;
            Opacity = 1;
            _shot?.Dispose(); _shot = null;
            _target?.Dispose(); _target = null;
            _video?.Dispose(); _video = null;
        }

        /// <summary>Il fotogramma del video, intero e centrato (le bande nere restano nere) nel suo riquadro.</summary>
        private void DrawVideo(DrawingContext ctx, Rect dst)
        {
            if (_video is not { } video || _to.Width <= 0 || _to.Height <= 0) return;
            double kx = dst.Width / _to.Width, ky = dst.Height / _to.Height;
            var area = new Rect(dst.X + (_videoTo.X - _to.X) * kx, dst.Y + (_videoTo.Y - _to.Y) * ky, _videoTo.Width * kx, _videoTo.Height * ky);
            double vw = video.PixelSize.Width, vh = video.PixelSize.Height;
            double k = Math.Min(area.Width / vw, area.Height / vh);
            var fit = new Rect(area.Center.X - vw * k / 2, area.Center.Y - vh * k / 2, vw * k, vh * k);
            ctx.DrawImage(video, new Rect(0, 0, vw, vh), fit);
        }

        public override void Render(DrawingContext ctx)
        {
            if (_shot is not { } shot) return;
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            IntPtr hwnd = WinNative.Handle(top);
            if (hwnd == IntPtr.Zero) return;
            var client = WinNative.ClientScreenRect(hwnd);
            double s = top.RenderScaling > 0 ? top.RenderScaling : 1;
            Rect Dip(double x, double y, double w, double h) => new((x - client.Left) / s, (y - client.Top) / s, w / s, h / s);
            using var quality = ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality });
            double p = Math.Clamp(Progress, 0, 1);
            // Dalla foto della finestra alla schermata nuova subito, a riquadro ancora piccolo: a meta' ingrandimento le
            // due disposizioni sovrapposte facevano "sminchiare" gli elementi.
            double mix = _target == null ? 0 : Math.Clamp((p - _mixFrom) / Math.Max(.01, _mixTo - _mixFrom), 0, 1);
            mix = mix * mix * (3 - 2 * mix);
            bool settled = p >= 1 && mix >= 1;
            // Finito l'ingrandimento si disegna solo la schermata nuova: l'opacita' della dissolvenza vale per ogni disegno,
            // e la foto del desktop sotto riaffiorava (finestra piccola e barra delle applicazioni in trasparenza).
            if (_backdrop && !settled) ctx.DrawImage(shot, new Rect(0, 0, shot.PixelSize.Width, shot.PixelSize.Height), Dip(_monitor.X, _monitor.Y, _monitor.Width, _monitor.Height));
            double L(double a, double b) => a + (b - a) * p;
            var dst = Dip(L(_from.X, _to.X), L(_from.Y, _to.Y), L(_from.Width, _to.Width), L(_from.Height, _to.Height));
            double radius = L(_cornerFrom, _cornerTo) / s;
            // Un'ombra leggera stacca il riquadro dal desktop mentre si muove.
            if (p is > 0 and < 1 && !settled)
                ctx.DrawRectangle(Brushes.Black, null, new RoundedRect(dst, radius),
                    new BoxShadows(new BoxShadow { Blur = 28, OffsetY = 6, Color = Color.FromArgb(90, 0, 0, 0) }));
            using (ctx.PushClip(new RoundedRect(dst, radius)))
            {
                if (mix < 1) ctx.DrawImage(shot, new Rect(_source.X, _source.Y, _source.Width, _source.Height), dst);
                if (_target is { } target && mix > 0)
                {
                    if (settled)
                    {
                        // Sotto l'interfaccia trasparente: nero e il fotogramma del video (prima solo nero).
                        if (_video != null) { ctx.DrawRectangle(Brushes.Black, null, dst); DrawVideo(ctx, dst); }
                        ctx.DrawImage(target, new Rect(0, 0, target.PixelSize.Width, target.PixelSize.Height), dst);
                    }
                    else
                        using (ctx.PushOpacity(mix))
                        {
                            ctx.DrawRectangle(Brushes.Black, null, dst);
                            DrawVideo(ctx, dst);
                            ctx.DrawImage(target, new Rect(0, 0, target.PixelSize.Width, target.PixelSize.Height), dst);
                        }
                }
            }
        }
    }

    /// <summary>Foto di un pezzo di schermo (GDI), in una bitmap di Avalonia.</summary>
    private static class ScreenShot
    {
        [DllImport("user32")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
        }

        [DllImport("user32")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

        /// <summary>Il contenuto di una finestra (anche D3D) e, di quello, solo il pezzo <paramref name="crop"/>.</summary>
        public static Bitmap? CaptureWindow(IntPtr hwnd, int width, int height, PixelRect crop)
        {
            if (width <= 0 || height <= 0) return null;
            IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(screen);
                var info = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
                dib = CreateDIBSection(screen, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
                old = SelectObject(mem, dib);
                if (!PrintWindow(hwnd, mem, 2 /* PW_RENDERFULLCONTENT */)) return null;
                var bmp = new WriteableBitmap(new PixelSize(crop.Width, crop.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using (var fb = bmp.Lock())
                {
                    unsafe
                    {
                        for (int y = 0; y < crop.Height; y++)
                        {
                            uint* src = (uint*)((byte*)bits + ((long)(y + crop.Y) * width + crop.X) * 4);
                            uint* dst = (uint*)((byte*)fb.Address + (long)y * fb.RowBytes);
                            for (int x = 0; x < crop.Width; x++) dst[x] = src[x] | 0xFF000000;
                        }
                    }
                }
                return bmp;
            }
            catch { return null; }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>La finestra mostra un'immagine: almeno un punto non nero nella parte centrale (campionata).</summary>
        public static bool WindowHasImage(IntPtr hwnd)
        {
            if (!WinNative.GetWindowRect(hwnd, out var r)) return false;
            int width = r.Right - r.Left, height = r.Bottom - r.Top;
            if (width <= 0 || height <= 0) return false;
            IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(screen);
                var info = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
                dib = CreateDIBSection(screen, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
                old = SelectObject(mem, dib);
                if (!PrintWindow(hwnd, mem, 2)) return false;
                unsafe
                {
                    for (int y = height / 5; y < height * 4 / 5; y += 12)
                    {
                        uint* row = (uint*)((byte*)bits + (long)y * width * 4);
                        for (int x = width / 5; x < width * 4 / 5; x += 12)
                        {
                            uint c = row[x];
                            if ((c & 0xFF) > 10 || ((c >> 8) & 0xFF) > 10 || ((c >> 16) & 0xFF) > 10) return true;
                        }
                    }
                }
                return false;
            }
            catch { return true; }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public static Bitmap? Capture(PixelRect r)
        {
            if (r.Width <= 0 || r.Height <= 0) return null;
            IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(screen);
                var info = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = r.Width, Height = -r.Height, Planes = 1, BitCount = 32 };
                dib = CreateDIBSection(screen, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
                old = SelectObject(mem, dib);
                if (!BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020 /* SRCCOPY */)) return null;
                var bmp = new WriteableBitmap(new PixelSize(r.Width, r.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using (var fb = bmp.Lock())
                {
                    unsafe
                    {
                        for (int y = 0; y < r.Height; y++)
                        {
                            uint* src = (uint*)((byte*)bits + (long)y * r.Width * 4);
                            uint* dst = (uint*)((byte*)fb.Address + (long)y * fb.RowBytes);
                            for (int x = 0; x < r.Width; x++) dst[x] = src[x] | 0xFF000000;
                        }
                    }
                }
                return bmp;
            }
            catch { return null; }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
#endif
