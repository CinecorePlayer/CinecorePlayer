using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace CinecoreMac;

/// <summary>
/// Liquid Glass di AppKit dove disponibile, materiale menu con sfocatura dal vivo sui sistemi precedenti.
/// </summary>
internal sealed class MacGlass
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string LiquidBridge = "libCinecoreGlass.dylib";
    [DllImport(LiquidBridge)] private static extern IntPtr CinecoreGlassCreate(IntPtr window);
    [DllImport(LiquidBridge)] private static extern void CinecoreGlassSetPanels(IntPtr overlay, CGRect[] frames, nuint count, double radius);
    [DllImport(LiquidBridge)] private static extern void CinecoreGlassSetForeground(IntPtr overlay, int index, IntPtr pixels, int width, int height, int stride);
    [DllImport(LiquidBridge)] private static extern void CinecoreGlassClose(IntPtr overlay);
    [DllImport(LiquidBridge)] private static extern void CinecoreGlassSetTheme(IntPtr overlay, int dark);
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, long a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, double a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, CGRect a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a, long b, IntPtr c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr obj, IntPtr sel);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect { public double X, Y, W, H; public CGRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; } }

    private static IntPtr S(string name) => sel_registerName(name);

    // NSVisualEffectMaterialMenu = 5, NSVisualEffectBlendingModeBehindWindow = 0, NSVisualEffectStateActive = 1.
    private const long MaterialMenu = 5, BehindWindow = 0, StateActive = 1, Below = -1;

    private readonly TopLevel _top;
    private readonly List<IntPtr> _views = new();
    private IntPtr _liquid;
    private bool _liquidUnavailable;

    public MacGlass(TopLevel top) { _top = top; }

    public static bool Available => OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("CINECORE_MENU_GLASS") != "frame";
    public bool HasLiquid => _liquid != IntPtr.Zero;

    private IntPtr Window()
    {
        if (_top.TryGetPlatformHandle() is not { } handle) return IntPtr.Zero;
        // Avalonia.Native da' la NSWindow (o la sua NSView).
        return handle.HandleDescriptor == "NSWindow" ? handle.Handle : Send(handle.Handle, S("window"));
    }

    /// <summary>
    /// Su macOS 26 il vetro è sopra Avalonia e ospita le righe del menu. Il materiale precedente
    /// può stare sotto l'HUD trasparente, con le righe disegnate direttamente da Avalonia.
    /// </summary>
    public bool Show(IReadOnlyList<Rect> panels, double radius, bool legacyGlass)
    {
        IntPtr window = Window();
        if (window == IntPtr.Zero) return false;
        if (OperatingSystem.IsMacOSVersionAtLeast(26) && !_liquidUnavailable)
        {
            if (!HasLiquid)
            {
                try { _liquid = CinecoreGlassCreate(window); }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _liquidUnavailable = true; }
            }
            if (HasLiquid)
            {
                var frames = new CGRect[panels.Count];
                for (int i = 0; i < panels.Count; i++)
                {
                    var r = panels[i];
                    frames[i] = new CGRect(r.X, r.Y, r.Width, r.Height);
                }
                CinecoreGlassSetPanels(_liquid, frames, (nuint)frames.Length, radius);
                try { CinecoreGlassSetTheme(_liquid, UiTheme.IsLight ? 0 : 1); } catch (EntryPointNotFoundException) { }
                return true;
            }
        }

        if (HasLiquid) { CinecoreGlassClose(_liquid); _liquid = IntPtr.Zero; }
        if (!legacyGlass) { Clear(); return false; }
        IntPtr content = Send(window, S("contentView"));
        IntPtr host = Send(content, S("superview"));
        if (host == IntPtr.Zero) { Clear(); return false; }
        var frame = SendRect(content, S("frame"));
        bool flipped = SendBool(host, S("isFlipped"));
        // Le viste gia' presenti si spostano e basta: un vetro nuovo impiega qualche fotogramma a mostrare la
        // sfocatura, e ricrearli tutti a ogni sottomenu faceva lampeggiare anche i pannelli rimasti fermi (l'effetto
        // "sdoppiato" segnalato).
        if (Environment.GetEnvironmentVariable("CINECORE_GLASS_REUSE") == "0") Clear();
        while (_views.Count > panels.Count)
        {
            IntPtr gone = _views[^1];
            _views.RemoveAt(_views.Count - 1);
            Send(gone, S("removeFromSuperview"));
            Send(gone, S("release"));
        }
        IntPtr fxClass = objc_getClass("NSVisualEffectView");
        IntPtr name = Send(objc_getClass("NSString"), S("stringWithUTF8String:"), DarkAquaName);
        IntPtr dark = Send(objc_getClass("NSAppearance"), S("appearanceNamed:"), name);
        for (int i = 0; i < panels.Count; i++)
        {
            var r = panels[i];
            double y = flipped ? r.Top : frame.H - r.Bottom;
            var rect = new CGRect(frame.X + r.X, frame.Y + y, r.Width, r.Height);
            if (i < _views.Count) { Send(_views[i], S("setFrame:"), rect); continue; }
            IntPtr fx = Send(Send(fxClass, S("alloc")), S("initWithFrame:"), rect);
            if (dark != IntPtr.Zero) Send(fx, S("setAppearance:"), dark);
            Send(fx, S("setMaterial:"), MaterialMenu);
            Send(fx, S("setBlendingMode:"), BehindWindow);
            Send(fx, S("setState:"), StateActive);
            Send(fx, S("setWantsLayer:"), true);
            IntPtr layer = Send(fx, S("layer"));
            if (layer != IntPtr.Zero)
            {
                Send(layer, S("setCornerRadius:"), radius);
                Send(layer, S("setMasksToBounds:"), true);
            }
            Send(host, S("addSubview:positioned:relativeTo:"), fx, Below, content);
            _views.Add(fx);
        }
        return true;
    }

    public void SetForeground(int index, ILockedFramebuffer pixels) =>
        CinecoreGlassSetForeground(_liquid, index, pixels.Address, pixels.Size.Width, pixels.Size.Height, pixels.RowBytes);

    private static readonly IntPtr DarkAquaName = Marshal.StringToCoTaskMemUTF8("NSAppearanceNameDarkAqua");

    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGPoint SendPoint(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong SendULong(IntPtr obj, IntPtr sel);
    [DllImport(CoreGraphics)] private static extern uint CGEventSourceCounterForEventType(int state, uint type);

    /// <summary>
    /// Clic (di qualunque tasto) fuori dalla nostra finestra, letto dallo stato globale del mouse come il timer da 45 ms
    /// di ContextOverlayMenuForm: un clic su un'altra applicazione chiude il menu, un cambio di focus da solo no.
    /// </summary>
    public bool MouseDownOutsideWindow()
    {
        IntPtr ev = objc_getClass("NSEvent");
        if (SendULong(ev, S("pressedMouseButtons")) == 0) return false;
        IntPtr window = Window();
        if (window == IntPtr.Zero) return false;
        var p = SendPoint(ev, S("mouseLocation"));
        var f = SendRect(window, S("frame"));
        return p.X < f.X || p.Y < f.Y || p.X > f.X + f.W || p.Y > f.Y + f.H;
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendWindowNumberAt(IntPtr obj, IntPtr sel, CGPoint point, long below);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern long CFArrayGetCount(IntPtr array);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string text, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, long size, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern bool CFNumberGetValue(IntPtr number, long type, out long value);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr obj);

    /// <summary>
    /// Proprietario della finestra piu' in alto sotto il puntatore (processo e nome), per capire se un clic destro
    /// "perso" e' caduto sul video del player (finestra nostra, di mpv o di Wine/VideoHost) o su un'altra app.
    /// </summary>
    public static (long Pid, string Owner)? WindowOwnerAtMouse()
    {
        try
        {
            var mouse = SendPoint(objc_getClass("NSEvent"), S("mouseLocation"));
            long number = SendWindowNumberAt(objc_getClass("NSWindow"), S("windowNumberAtPoint:belowWindowWithWindowNumber:"), new CGPoint { X = mouse.X, Y = mouse.Y }, 0);
            if (number <= 0) return null;
            IntPtr list = CGWindowListCopyWindowInfo(8 /* kCGWindowListOptionIncludingWindow */, (uint)number);
            if (list == IntPtr.Zero) return null;
            try
            {
                if (CFArrayGetCount(list) < 1) return null;
                IntPtr info = CFArrayGetValueAtIndex(list, 0);
                IntPtr pidKey = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowOwnerPID", 0x08000100);
                IntPtr nameKey = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowOwnerName", 0x08000100);
                try
                {
                    long pid = CFNumberGetValue(CFDictionaryGetValue(info, pidKey), 4 /* kCFNumberSInt64Type */, out long v) ? v : -1;
                    IntPtr name = CFDictionaryGetValue(info, nameKey);
                    var buffer = new byte[512];
                    string owner = name != IntPtr.Zero && CFStringGetCString(name, buffer, buffer.Length, 0x08000100)
                        ? System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is int z and >= 0 ? z : buffer.Length) : "";
                    return (pid, owner);
                }
                finally { CFRelease(pidKey); CFRelease(nameKey); }
            }
            finally { CFRelease(list); }
        }
        catch { return null; }
    }

    public static uint SecondaryPressCount() =>
        OperatingSystem.IsMacOS() ? CGEventSourceCounterForEventType(0, 3) : 0; // combined session, right mouse down

    public static bool SecondaryButtonDown() =>
        (SendULong(objc_getClass("NSEvent"), S("pressedMouseButtons")) & 2) != 0;

    public Point? PointerInWindow()
    {
        IntPtr window = Window();
        if (window == IntPtr.Zero || !SendBool(window, S("isOnActiveSpace"))) return null;
        var mouse = SendPoint(objc_getClass("NSEvent"), S("mouseLocation"));
        var frame = SendRect(window, S("frame"));
        var at = new Point(mouse.X - frame.X, frame.Y + frame.H - mouse.Y);
        return new Rect(0, 0, frame.W, frame.H).Contains(at) ? at : null;
    }

    public void Clear()
    {
        if (HasLiquid)
        {
            CinecoreGlassClose(_liquid);
            _liquid = IntPtr.Zero;
        }
        foreach (var glass in _views)
        {
            Send(glass, S("removeFromSuperview"));
            Send(glass, S("release"));
        }
        _views.Clear();
    }
}
