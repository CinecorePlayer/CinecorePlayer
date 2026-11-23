using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Video › 3D › Schermo intero esteso (ToggleExtendedFullscreen di Windows, Maiusc+F): la finestra copre tutti gli
/// schermi e un film 3D affiancato si stira intero, cosi' ogni occhio riempie il proprio schermo (due proiettori o due
/// monitor). Su macOS una finestra sta su piu' schermi solo con "Gli schermi usano spazi separati" spento.
/// </summary>
public sealed partial class MainWindow
{
    private bool _extendedFs;
    private PixelPoint _extSavedPos;
    private double _extSavedW, _extSavedH;
    private SystemDecorations _extSavedDecorations;
    private long _extSavedStyleMask = -1;

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr ObjcSendLong(IntPtr obj, IntPtr sel, long value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool ObjcSendBool(IntPtr obj, IntPtr sel);

    [StructLayout(LayoutKind.Sequential)] private struct ScreenRect { public double X, Y, W, H; }
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern ScreenRect ObjcSendRect(IntPtr obj, IntPtr sel);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern void ObjcSendFrame(IntPtr obj, IntPtr sel, ScreenRect frame, [MarshalAs(UnmanagedType.I1)] bool display);

    /// <summary>La cornice su tutti gli schermi in coordinate di macOS: Avalonia la spostava sotto la barra dei menu.</summary>
    private void CoverAllScreens()
    {
        if (!OperatingSystem.IsMacOS() || TryGetPlatformHandle() is not { } h) return;
        IntPtr window = h.HandleDescriptor == "NSWindow" ? h.Handle : ObjcSend(h.Handle, sel_registerName("window"));
        IntPtr screens = ObjcSend(objc_getClass("NSScreen"), sel_registerName("screens"));
        ulong count = ObjcSendCount(screens, sel_registerName("count"));
        if (window == IntPtr.Zero || count == 0) return;
        // Con la barra del titolo macOS tiene la finestra sotto la barra dei menu: senza bordi finche' dura.
        if (_extSavedStyleMask < 0) _extSavedStyleMask = (long)ObjcSendCount(window, sel_registerName("styleMask"));
        ObjcSendLong(window, sel_registerName("setStyleMask:"), 0);
        SetWindowLevel(25);
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        for (ulong i = 0; i < count; i++)
        {
            var f = ObjcSendRect(ObjcSend(screens, sel_registerName("objectAtIndex:"), i), sel_registerName("frame"));
            l = Math.Min(l, f.X); b = Math.Min(b, f.Y); r = Math.Max(r, f.X + f.W); t = Math.Max(t, f.Y + f.H);
        }
        ObjcSendFrame(window, sel_registerName("setFrame:display:"), new ScreenRect { X = l, Y = b, W = r - l, H = t - b }, true);
        var now = ObjcSendRect(window, sel_registerName("frame"));
        Console.Error.WriteLine($"[3D] cornice chiesta {l},{b} {r - l}x{t - b} -> {now.X},{now.Y} {now.W}x{now.H}");
    }

    private static bool ScreensHaveSeparateSpaces()
    {
        // Solo macOS (su Windows la chiamata falliva e compariva sempre l'avviso del Mac).
        if (!OperatingSystem.IsMacOS()) return false;
        try { return ObjcSendBool(objc_getClass("NSScreen"), sel_registerName("screensHaveSeparateSpaces")); }
        catch { return true; }
    }

    private bool CanExtendFullscreen => Screens.All.Count > 1;

    private void ToggleExtendedFullscreen()
    {
        if (_extendedFs) { ExitExtendedFullscreen(); return; }
        if (!CanExtendFullscreen) { _osd.Show("Schermo intero esteso: serve un secondo schermo", 2600); return; }
        // Dallo schermo intero di macOS si esce prima (vive in uno Space suo, su un solo schermo).
        if (IsFullscreen) { ToggleFullscreen(); DispatcherTimer.RunOnce(EnterExtendedFullscreen, TimeSpan.FromMilliseconds(1200)); }
        else EnterExtendedFullscreen();
    }

    private void EnterExtendedFullscreen()
    {
        if (_extendedFs || IsFullscreen) return;
        var all = Screens.All;
        int left = all.Min(s => s.Bounds.X), top = all.Min(s => s.Bounds.Y);
        int right = all.Max(s => s.Bounds.Right), bottom = all.Max(s => s.Bounds.Bottom);
        _extSavedPos = Position; _extSavedW = Width; _extSavedH = Height; _extSavedDecorations = SystemDecorations;
        _extendedFs = true;
        SystemDecorations = SystemDecorations.None;
#if CINECORE_WINDOWS
        // Misure degli schermi in pixel: Width/Height di Avalonia sono unita' logiche, quindi con la scala di Windows al
        // 125-150% la finestra usciva dagli schermi. Pixel esatti con SetWindowPos, sopra la barra delle applicazioni
        // (la finestra del video segue il player nell'ordine, quindi sta sopra anche lei).
        if (TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
            WinNative.SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, left, top, right - left, bottom - top, 0x0020 /* FRAMECHANGED */ | 0x0040 /* SHOWWINDOW */);
#else
        Position = new PixelPoint(left, top);
        Width = right - left; Height = bottom - top;
        SetWindowLevel(25); // sopra la barra dei menu di ogni schermo
        Dispatcher.UIThread.Post(CoverAllScreens, DispatcherPriority.Background);
#endif
        Console.Error.WriteLine($"[3D] schermo esteso {right - left}x{bottom - top} da {left},{top}, schermi={all.Count}, spazi separati={ScreensHaveSeparateSpaces()}");
        SendStereoOutput();
        _hud.Poke();
        if (OperatingSystem.IsMacOS() && ScreensHaveSeparateSpaces())
            _osd.Show("Spegni “Gli schermi usano spazi separati” (Desktop e Dock)", 9000);
        else _osd.Show("Schermo intero esteso · Maiusc+F per uscire", 2600);
    }

    private void ExitExtendedFullscreen()
    {
        if (!_extendedFs) return;
        _extendedFs = false;
        if (_extSavedStyleMask >= 0 && TryGetPlatformHandle() is { } h)
        {
            IntPtr window = h.HandleDescriptor == "NSWindow" ? h.Handle : ObjcSend(h.Handle, sel_registerName("window"));
            if (window != IntPtr.Zero) ObjcSendLong(window, sel_registerName("setStyleMask:"), _extSavedStyleMask);
            _extSavedStyleMask = -1;
        }
        SetWindowLevel(0);
#if CINECORE_WINDOWS
        if (TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
            WinNative.SetWindowPos(hwnd, new IntPtr(-2) /* HWND_NOTOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
#endif
        Console.Error.WriteLine("[3D] schermo esteso: uscita");
        SystemDecorations = _extSavedDecorations;
        Width = _extSavedW; Height = _extSavedH; Position = _extSavedPos;
        SendStereoOutput();
        _hud.Poke();
    }

    private void SetWindowLevel(long level)
    {
        if (!OperatingSystem.IsMacOS() || TryGetPlatformHandle() is not { } h) return;
        IntPtr window = h.HandleDescriptor == "NSWindow" ? h.Handle : ObjcSend(h.Handle, sel_registerName("window"));
        if (window != IntPtr.Zero) ObjcSendLong(window, sel_registerName("setLevel:"), level);
    }

    /// <summary>Al motore: nello schermo esteso il fotogramma intero stirato, altrimenti la scelta 3D (un occhio o nativo).</summary>
    private void SendStereoOutput()
    {
        if (!_video.IsRunning) return;
        bool keepBoth = _extendedFs && CanExtendFullscreen;
        _video.Send("stretch " + (keepBoth ? "1" : "0"));
        _video.Send(MacStereo.Command(keepBoth ? StereoMode.None : _stereo));
    }
}
