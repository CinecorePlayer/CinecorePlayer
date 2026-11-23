#if CINECORE_WINDOWS
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace CinecoreMac;

/// <summary>
/// Entrata e uscita animate da PiP e mini player (Windows, 2026-11-21), con la stessa tecnica dello schermo intero:
/// si fotografa lo schermo, la foto della finestra vola fino al posto nuovo (rimpicciolendo o allargandosi) mentre
/// sotto le finestre vere cambiano, nascoste a DWM (cloak); quando la foto arriva le finestre vere ricompaiono e la
/// foto sfuma. Prima il passaggio era di colpo (la finestra spariva e l'altra compariva altrove).
/// </summary>
public sealed partial class MainWindow
{
    private bool _morphing, _morphHideVideo;
    private readonly List<IntPtr> _morphCloaked = new();

    /// <summary>Le finestre aperte durante il cambio (il PiP) nascono nascoste: si mostrano a fine animazione.</summary>
    private void MorphCloakEarly(Window window)
    {
        if (!_morphing) return;
        IntPtr h = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (h != IntPtr.Zero && Cloak(h, true)) _morphCloaked.Add(h);
    }

    private static bool Cloak(IntPtr h, bool on)
    {
        int v = on ? 1 : 0;
        return WinNative.DwmSetWindowAttribute(h, 13 /* DWMWA_CLOAK */, ref v, 4) == 0;
    }

    private PixelRect PlayerRect()
    {
        IntPtr h = WinNative.Handle(this);
        var r = WinNative.VisibleRect(h);
        return new PixelRect(r.Left, r.Top, Math.Max(1, r.Width), Math.Max(1, r.Height));
    }

    private double PlayerCorner => WindowState == WindowState.Normal ? 8 : 0;

    /// <param name="from">Dove sta ora la finestra (pixel dello schermo).</param>
    /// <param name="change">Il cambio vero (PiP, mini player), fatto sotto la foto.</param>
    /// <param name="to">Dove sara' la finestra dopo il cambio.</param>
    /// <param name="target">L'aspetto d'arrivo (la finestra nuova in un'immagine), se c'e'.</param>
    private async Task MorphAsync(PixelRect from, double cornerFrom, Func<Task> change, Func<PixelRect?> to, double cornerTo,
        Func<Bitmap?>? target = null, Func<(Bitmap Frame, PixelRect At)?>? video = null, bool hideVideo = false)
    {
        if (_morphing || _fsFading || Environment.GetEnvironmentVariable("CINECORE_NO_MORPH") == "1") { await change(); return; }
        IntPtr main = WinNative.Handle(this);
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (main == IntPtr.Zero || screen == null) { await change(); return; }
        var m = screen.Bounds;
        var monitor = new PixelRect(m.X, m.Y, m.Width, m.Height);
        if (!monitor.Contains(from.Center)) { await change(); return; }
        if (_menu.IsOpen || _menu.HasGhost) { _menu.CloseNow(); await WaitFrames(2); }
        var shot = ScreenShot.Capture(monitor);
        if (shot == null) { await change(); return; }
        _morphing = true;
        _morphCloaked.Clear();
        Window? host = null;
        try
        {
            var zoom = new FsZoomOverlay { IsHitTestVisible = false };
            zoom.Start(shot, monitor, Relative(from, monitor), from, from, false, cornerFrom, cornerTo);
            host = await ShowZoomHost(zoom, monitor, screen.Scaling);
            // Il player sotto la foto si nasconde: cambia misura o si riduce a icona senza farsi vedere.
            if (Cloak(main, true)) _morphCloaked.Add(main);
            // La finestra del video non segue il cloak del player: con MPV (nostra) la si nasconde, con madVR (VideoHost)
            // aspetta fuori schermo. Senza, saltava subito al posto finale mentre la foto era ancora per strada.
            if (hideVideo && _video.IsRunning && _videoReady)
            {
                if (Engine == "mpv") { if (MpvEngine.CurrentWindow is var mw && mw != IntPtr.Zero && Cloak(mw, true)) _morphCloaked.Add(mw); }
                else { _morphHideVideo = true; SyncVideoRect(); }
            }
            await change();
            await WaitFrames(3);
            var arrive = to();
            if (arrive is not { } dest || dest.Width < 8 || dest.Height < 8) return;
            zoom.Retarget(dest);
            if (video?.Invoke() is { } v) zoom.SetVideo(v.Frame, v.At);
            if (target?.Invoke() is { } image) zoom.SetTarget(image, .35, .85);
            await Animate(zoom, host);
            if (_morphHideVideo)
            {
                _morphHideVideo = false;
                SyncVideoRect(); SyncVideoWindows();
                await WaitVideoPresented(main);
            }
            // Le finestre vere tornano visibili sotto la foto ferma, poi la foto sfuma.
            foreach (var h in _morphCloaked) Cloak(h, false);
            _morphCloaked.Clear();
            await NextFrame(host); await NextFrame(host);
            await FadeOut(zoom, host, 130);
        }
        catch (Exception ex) { Console.Error.WriteLine("[animazione finestra] " + ex.Message); }
        finally
        {
            if (_morphHideVideo) { _morphHideVideo = false; SyncVideoRect(); SyncVideoWindows(); }
            foreach (var h in _morphCloaked) Cloak(h, false);
            _morphCloaked.Clear();
            if (host != null) { (host.Content as FsZoomOverlay)?.Stop(); host.Close(); }
            _morphing = false;
        }
    }

    /// <summary>Il contenuto di una finestra in un'immagine (aspetto d'arrivo dell'animazione).</summary>
    private static Bitmap? RenderWindow(TopLevel top)
    {
        try
        {
            double s = top.RenderScaling > 0 ? top.RenderScaling : 1;
            var size = new PixelSize((int)Math.Round(top.ClientSize.Width * s), (int)Math.Round(top.ClientSize.Height * s));
            if (size.Width <= 0 || size.Height <= 0) return null;
            var bitmap = new RenderTargetBitmap(size, new Vector(96 * s, 96 * s));
            bitmap.Render(top);
            return bitmap;
        }
        catch { return null; }
    }

    private PixelRect ClientRectOf(TopLevel top)
    {
        var c = WinNative.ClientScreenRect(WinNative.Handle(top));
        return new PixelRect(c.Left, c.Top, Math.Max(1, c.Width), Math.Max(1, c.Height));
    }

    // ------------------------------------------------------------------ mini player

    private async void ToggleMiniPlayerAnimated()
    {
        var from = PlayerRect();
        bool leaving = _beforeMini != null;
        var back = _beforeMini?.State ?? WindowState.Normal;
        double corner = leaving ? 8 : PlayerCorner;
        await MorphAsync(from, corner, async () =>
        {
            if (leaving) { ToggleMiniPlayer(); if (back == WindowState.FullScreen) await Task.Delay(200); }
            else
            {
                var state = WindowState;
                if (state != WindowState.Normal) { WindowState = WindowState.Normal; await WaitFrames(2); }
                EnterMiniPlayer(state);
            }
        }, () => PlayerRect(), leaving ? (back == WindowState.Normal ? 8 : 0) : 8, () => RenderWindow(this));
    }
}
#endif
