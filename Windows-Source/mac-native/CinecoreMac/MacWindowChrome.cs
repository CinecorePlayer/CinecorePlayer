using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace CinecoreMac;

/// <summary>
/// La barra del titolo trasparente e senza scritta, anche in schermo intero.
/// </summary>
internal static class MacWindowChrome
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendUtf8(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, long a);
    /// <summary>Prove in secondo piano: restituisce il primo piano all'app che l'utente stava usando.</summary>
    public static void DeactivateApp()
    {
        try { Send(Send(objc_getClass("NSApplication"), S("sharedApplication")), S("deactivate")); } catch { }
    }
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendColor(IntPtr obj, IntPtr sel, double r, double g, double b, double a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong SendULong(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIndex(IntPtr obj, IntPtr sel, ulong index);
    private static IntPtr S(string name) => sel_registerName(name);

    public static void SetApplicationIcon()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Cinecore.icns");
        if (!System.IO.File.Exists(path)) return;
        IntPtr name = SendUtf8(objc_getClass("NSString"), S("stringWithUTF8String:"), path);
        IntPtr icon = Send(Send(objc_getClass("NSImage"), S("alloc")), S("initWithContentsOfFile:"), name);
        if (icon == IntPtr.Zero) return;
        Send(Send(objc_getClass("NSApplication"), S("sharedApplication")), S("setApplicationIconImage:"), icon);
        Send(icon, S("release"));
    }

    /// <summary>
    /// La barra del titolo, in finestra e in schermo intero (quella che macOS fa scendere dall'alto): trasparente e
    /// senza scritta, solo i pulsanti sopra l'HUD. Via il materiale grigio di sistema.
    /// </summary>
    public static void MakeTitlebarClear(TopLevel top)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            if (top.TryGetPlatformHandle() is not { } handle) return;
            IntPtr window = handle.HandleDescriptor == "NSWindow" ? handle.Handle : Send(handle.Handle, S("window"));
            if (window == IntPtr.Zero) return;
            // Sempre: niente scritta e barra trasparente, solo i pulsanti sopra il contenuto (un tutt'uno con l'HUD).
            Send(window, S("setTitleVisibility:"), 1L); // NSWindowTitleHidden
            Send(window, S("setTitlebarAppearsTransparent:"), true);
            // Via i contorni: la linea sotto la barra (macOS 11+) e quella della barra degli strumenti di Avalonia.
            if (SendBool(window, S("respondsToSelector:"), S("setTitlebarSeparatorStyle:"))) Send(window, S("setTitlebarSeparatorStyle:"), 1L); // None
            IntPtr toolbar = Send(window, S("toolbar"));
            if (toolbar != IntPtr.Zero) Send(toolbar, S("setShowsBaselineSeparator:"), false);
            IntPtr close = Send(window, S("standardWindowButton:"), 0L); // NSWindowCloseButton
            IntPtr titlebar = close == IntPtr.Zero ? IntPtr.Zero : Send(close, S("superview"));
            if (titlebar == IntPtr.Zero) return;
            IntPtr effectClass = objc_getClass("NSVisualEffectView");
            // La barra e il suo contenitore (NSTitlebarContainerView): durante il film il contenitore restava grigio
            // scuro e opaco, una fascia sopra il video con i pulsanti della finestra e l'HUD a cavallo del bordo.
            // Avalonia mette nella vista contenuto una sua fascia di materiale alta quanto la barra (e una linea
            // NSBox) sotto il nostro disegno: in libreria la pagina la copre, durante il film l'HUD e' trasparente e
            // restava una striscia grigia con i pulsanti della finestra "incollata" sopra il video.
            IntPtr contentView = Send(window, S("contentView"));
            if (contentView != IntPtr.Zero)
            {
                IntPtr boxClass = objc_getClass("NSBox");
                IntPtr items = Send(contentView, S("subviews"));
                ulong n = SendULong(items, S("count"));
                for (ulong i = 0; i < n; i++)
                {
                    IntPtr view = SendIndex(items, S("objectAtIndex:"), i);
                    bool effect = SendBool(view, S("isKindOfClass:"), effectClass), box = SendBool(view, S("isKindOfClass:"), boxClass);
                    if ((effect && SendRectD(view, S("frame")).H <= 60) || box) Send(view, S("setHidden:"), true);
                }
            }
            // In fullscreen AppKit ospita i pulsanti in una sua finestra, con la barra a scomparsa.
            // Lascia ad AppKit il materiale e la posizione di quella barra.
            IntPtr chrome = Send(close, S("window"));
            if (chrome != IntPtr.Zero && chrome != window) return;
            IntPtr container = Send(titlebar, S("superview"));
            foreach (IntPtr host in new[] { titlebar, container })
            {
                if (host == IntPtr.Zero) continue;
                IntPtr subviews = Send(host, S("subviews"));
                ulong count = SendULong(subviews, S("count"));
                for (ulong i = 0; i < count; i++)
                {
                    IntPtr view = SendIndex(subviews, S("objectAtIndex:"), i);
                    if (SendBool(view, S("isKindOfClass:"), effectClass)) Send(view, S("setHidden:"), true);
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[chrome] " + ex.Message); }
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSetDouble(IntPtr obj, IntPtr sel, double v);
    /// <summary>Prove: opacita' dell'intera finestra dell'app (0 = WindowServer non la compone sopra il video).</summary>
    public static void SetWindowAlpha(TopLevel top, double alpha)
    {
        IntPtr w = WindowOf(top);
        if (w != IntPtr.Zero) SendSetDouble(w, S("setAlphaValue:"), alpha);
    }
    private static IntPtr WindowOf(TopLevel top) =>
        top.TryGetPlatformHandle() is { } h ? (h.HandleDescriptor == "NSWindow" ? h.Handle : Send(h.Handle, S("window"))) : IntPtr.Zero;

    internal static void SetWindowShadow(TopLevel top, bool visible)
    {
        if (!OperatingSystem.IsMacOS()) return;
        IntPtr window = WindowOf(top);
        if (window != IntPtr.Zero) Send(window, S("setHasShadow:"), visible);
    }

    internal static bool IsNativeFullscreen(TopLevel top) =>
        WindowOf(top) is var window && window != IntPtr.Zero && (SendULong(window, S("styleMask")) & (1UL << 14)) != 0;

    [StructLayout(LayoutKind.Sequential)] private struct EdgeInsets { public double Top, Left, Bottom, Right; }
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern EdgeInsets SendInsets(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr obj, IntPtr sel);

    /// <summary>
    /// Quanto scende dall'alto in schermo intero: barra dei menu (piu' alta con la tacca) piu' quella della finestra.
    /// </summary>
    public static double FullscreenRevealHeight(TopLevel top)
    {
        double menu = 24;
        if (OperatingSystem.IsMacOS())
            try
            {
                IntPtr app = Send(objc_getClass("NSApplication"), S("sharedApplication"));
                IntPtr mainMenu = Send(app, S("mainMenu"));
                if (mainMenu != IntPtr.Zero) menu = Math.Max(menu, SendDouble(mainMenu, S("menuBarHeight")));
                IntPtr window = WindowOf(top);
                IntPtr screen = window != IntPtr.Zero ? Send(window, S("screen")) : IntPtr.Zero;
                if (screen != IntPtr.Zero && SendBool(screen, S("respondsToSelector:"), S("safeAreaInsets")))
                    menu = Math.Max(menu, SendInsets(screen, S("safeAreaInsets")).Top);
            }
            catch { }
        return menu + 28;
    }

    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGPoint SendPoint(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr obj, IntPtr sel);

    /// <summary>Bordo inferiore della barra nativa rispetto alla finestra del film, in punti.</summary>
    public static double VisibleFullscreenTitlebarBottom(TopLevel top, bool diagnostics = false, bool matchLibrary = false)
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        IntPtr window = WindowOf(top);
        if (window == IntPtr.Zero) return 0;
        IntPtr button = Send(window, S("standardWindowButton:"), 0L);
        if (button == IntPtr.Zero) return 0;
        IntPtr bar = Send(button, S("window"));
        ulong barNumber = bar == IntPtr.Zero ? 0 : SendULong(bar, S("windowNumber"));
        bool shown = bar != window && WineWindowOrder.IsWindowShown(barNumber);
        if (diagnostics)
            Console.Error.WriteLine($"[qa] nativeTitlebar={barNumber} mainWindow={SendULong(window, S("windowNumber"))} shown={shown} pointerY={PointerYFromTop(top):0}");
        if (!shown) return 0;
        if (matchLibrary)
        {
            // La stessa tinta della barra della libreria, anche quando sotto c'è il film.
            var tint = UiTheme.Panel;
            var titlebar = Send(button, S("superview"));
            if (titlebar != IntPtr.Zero)
            {
                Send(titlebar, S("setWantsLayer:"), true);
                var color = SendColor(objc_getClass("NSColor"), S("colorWithSRGBRed:green:blue:alpha:"),
                    tint.R / 255.0, tint.G / 255.0, tint.B / 255.0, .94);
                var layer = Send(titlebar, S("layer"));
                if (layer != IntPtr.Zero) Send(layer, S("setBackgroundColor:"), Send(color, S("CGColor")));
                var views = Send(titlebar, S("subviews"));
                var background = objc_getClass("NSTitlebarBackgroundView");
                for (ulong i = 0, n = SendULong(views, S("count")); i < n; i++)
                {
                    var view = SendIndex(views, S("objectAtIndex:"), i);
                    if (background != IntPtr.Zero && SendBool(view, S("isKindOfClass:"), background)) Send(view, S("setHidden:"), true);
                }
            }
        }
        var content = SendRect(window, S("frame"));
        double bottom = content.Y + content.H - SendRect(bar, S("frame")).Y;
        return bottom is > 0 and <= 120 ? bottom : 0;
    }

    /// <summary>Il mouse dal bordo in alto della finestra, in punti (anche quando e' sopra la barra di sistema).</summary>
    public static double? PointerYFromTop(TopLevel top)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            IntPtr window = WindowOf(top);
            if (window == IntPtr.Zero) return null;
            var mouse = SendPoint(objc_getClass("NSEvent"), S("mouseLocation")); // origine in basso a sinistra
            var frame = SendRect(window, S("frame"));
            return frame.Y + frame.H - mouse.Y;
        }
        catch { return null; }
    }

    [DllImport(ObjC)] private static extern IntPtr object_getClassName(IntPtr obj);
    [StructLayout(LayoutKind.Sequential)] private struct CGRectD { public double X, Y, W, H; }
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRectD SendRectD(IntPtr obj, IntPtr sel);

    /// <summary>Solo per le prove: l'albero delle viste dalla finestra in giu' (classe, misura, nascosta, opaca).</summary>
    public static void DumpViews(TopLevel top)
    {
        IntPtr window = WindowOf(top);
        if (window == IntPtr.Zero) return;
        IntPtr content = Send(window, S("contentView"));
        IntPtr frame = content == IntPtr.Zero ? IntPtr.Zero : Send(content, S("superview"));
        Console.Error.WriteLine($"[viste] window opaque={SendBool(window, S("isOpaque"), IntPtr.Zero)} shadow={SendBool(window, S("hasShadow"), IntPtr.Zero)}");
        void Walk(IntPtr view, int depth)
        {
            if (view == IntPtr.Zero || depth > 6) return;
            var r = SendRectD(view, S("frame"));
            string name = Marshal.PtrToStringAnsi(object_getClassName(view)) ?? "?";
            bool hidden = SendBool(view, S("isHidden"), IntPtr.Zero);
            Console.Error.WriteLine($"[viste] {new string(' ', depth * 2)}{name} {r.X:0},{r.Y:0} {r.W:0}x{r.H:0}{(hidden ? " nascosta" : "")}");
            IntPtr subviews = Send(view, S("subviews"));
            ulong count = SendULong(subviews, S("count"));
            for (ulong i = 0; i < count; i++) Walk(SendIndex(subviews, S("objectAtIndex:"), i), depth + 1);
        }
        Walk(frame, 0);
    }

    /// <summary>Opacita' dei tre pulsanti della finestra (seguono l'HUD durante il film; nascosti nei cambi di schermo intero).</summary>
    internal static void SetWindowButtonsAlpha(TopLevel top, double alpha)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            IntPtr window = WindowOf(top);
            if (window == IntPtr.Zero) return;
            for (long kind = 0; kind <= 2; kind++)
            {
                IntPtr button = Send(window, S("standardWindowButton:"), kind);
                if (button != IntPtr.Zero) SendSetDouble(button, S("setAlphaValue:"), alpha);
            }
        }
        catch { }
    }

    /// <summary>
    /// Schermo intero durante il film: la barra a scomparsa di macOS (una finestra a parte, con i pulsanti) era una
    /// fascia grigia opaca con l'ombra sopra il video. Qui diventa trasparente: restano solo i tre pulsanti.
    /// Rende quanto scende sotto il bordo alto della finestra, 0 se non e' a schermo.
    /// </summary>
    private static bool BarDump = Environment.GetEnvironmentVariable("CINECORE_BAR_DUMP") == "1";
    internal static double ClearFullscreenBar(TopLevel top)
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        try
        {
            IntPtr window = WindowOf(top);
            if (window == IntPtr.Zero) return 0;
            IntPtr button = Send(window, S("standardWindowButton:"), 0L);
            if (button == IntPtr.Zero) return 0;
            IntPtr bar = Send(button, S("window"));
            if (bar == IntPtr.Zero || bar == window) return 0;
            if (!WineWindowOrder.IsWindowShown(SendULong(bar, S("windowNumber")))) return 0;
            Send(bar, S("setOpaque:"), false);
            Send(bar, S("setBackgroundColor:"), Send(objc_getClass("NSColor"), S("clearColor")));
            Send(bar, S("setHasShadow:"), false);
            Send(bar, S("invalidateShadow"));
            IntPtr effectClass = objc_getClass("NSVisualEffectView");
            IntPtr content = Send(bar, S("contentView"));
            IntPtr root = content == IntPtr.Zero ? IntPtr.Zero : Send(content, S("superview"));
            if (root == IntPtr.Zero) root = content;
            void Walk(IntPtr view, int depth)
            {
                if (view == IntPtr.Zero || depth > 8) return;
                string name = Marshal.PtrToStringAnsi(object_getClassName(view)) ?? "";
                if (BarDump) { var f = SendRectD(view, S("frame")); Console.Error.WriteLine($"[barra] {new string(' ', depth * 2)}{name} {f.X:0},{f.Y:0} {f.W:0}x{f.H:0}"); }
                if (SendBool(view, S("isKindOfClass:"), effectClass) || name.Contains("TitlebarBackground", StringComparison.Ordinal)
                    || name.Contains("TitlebarDecoration", StringComparison.Ordinal))
                { Send(view, S("setHidden:"), true); return; }
                IntPtr layer = Send(view, S("layer"));
                if (layer != IntPtr.Zero && !name.Contains("Button", StringComparison.Ordinal)) Send(layer, S("setBackgroundColor:"), IntPtr.Zero);
                IntPtr subviews = Send(view, S("subviews"));
                for (ulong i = 0, n = SendULong(subviews, S("count")); i < n; i++) Walk(SendIndex(subviews, S("objectAtIndex:"), i), depth + 1);
            }
            Walk(root, 0);
            BarDump = false;
            var frame = SendRect(window, S("frame"));
            double bottom = frame.Y + frame.H - SendRect(bar, S("frame")).Y;
            return bottom is > 0 and <= 120 ? bottom : 0;
        }
        catch { return 0; }
    }

    internal static string QaButtonState(TopLevel top)
    {
        IntPtr window = WindowOf(top);
        if (window == IntPtr.Zero) return "unavailable";
        string state = "";
        for (long kind = 0; kind <= 2; kind++)
        {
            IntPtr button = Send(window, S("standardWindowButton:"), kind);
            state += button == IntPtr.Zero ? "-" : SendBool(button, S("isHidden"), IntPtr.Zero) ? "H" : "V";
        }
        return state;
    }

}

/// <summary>
/// La finestra video di Wine (madVR, MPC-VR) e' di un altro processo: su Windows sta dentro il player e l'HUD e' sopra
/// per costruzione, qui no. Se Wine la porta davanti (all'avvio, o quando madVR rifa' la superficie) copre HUD, schede
/// e schermata di caricamento: si rimette subito la nostra finestra appena sopra, senza attivarla.
/// </summary>
internal static class WineWindowOrder
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(CG)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(CF)] private static extern long CFArrayGetCount(IntPtr array);
    [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
    [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(IntPtr boolean);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint encoding);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, long size, uint encoding);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, long type, out long value);
    [DllImport(CF, EntryPoint = "CFNumberGetValue")] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetDouble(IntPtr number, long type, out double value);
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendLong(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendOrder(IntPtr obj, IntPtr sel, long place, long relativeTo);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);

    private const uint OnScreenAboveWindow = 1 << 1, ExcludeDesktop = 1 << 4;
    private const uint Utf8 = 0x08000100;
    private static readonly IntPtr KeyOwnerName = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowOwnerName", Utf8);
    private static readonly IntPtr KeyNumber = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowNumber", Utf8);
    private static readonly IntPtr KeyLayer = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowLayer", Utf8);
    private static readonly IntPtr KeyAlpha = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowAlpha", Utf8);
    private static readonly IntPtr KeyOnScreen = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowIsOnscreen", Utf8);
    private static readonly IntPtr KeyPid = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowOwnerPID", Utf8);
    private static readonly IntPtr KeyBounds = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowBounds", Utf8);
    [StructLayout(LayoutKind.Sequential)] private struct WindowBounds { public double X, Y, W, H; }
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dictionary, out WindowBounds bounds);

    public static long WindowNumber(TopLevel top)
    {
        if (top.TryGetPlatformHandle() is not { } h) return 0;
        IntPtr window = h.HandleDescriptor == "NSWindow" ? h.Handle : Send(h.Handle, sel_registerName("window"));
        return window == IntPtr.Zero ? 0 : SendLong(window, sel_registerName("windowNumber"));
    }

    public static bool IsWindowShown(ulong window)
    {
        const uint includingWindow = 1 << 3;
        if (window == 0 || window > uint.MaxValue) return false;
        IntPtr list = CGWindowListCopyWindowInfo(includingWindow, (uint)window);
        if (list == IntPtr.Zero) return false;
        try
        {
            if (CFArrayGetCount(list) == 0) return false;
            IntPtr info = CFArrayGetValueAtIndex(list, 0);
            IntPtr alpha = CFDictionaryGetValue(info, KeyAlpha);
            IntPtr onScreen = CFDictionaryGetValue(info, KeyOnScreen);
            return onScreen != IntPtr.Zero && CFBooleanGetValue(onScreen) && alpha != IntPtr.Zero &&
                CFNumberGetDouble(alpha, 13 /* kCFNumberDoubleType */, out double opacity) && opacity > .05;
        }
        finally { CFRelease(list); }
    }

    public static bool VideoCoversWindow(long ours, int hostPid)
    {
        const uint onScreenOnly = 1;
        IntPtr list = CGWindowListCopyWindowInfo(onScreenOnly | ExcludeDesktop, 0);
        if (list == IntPtr.Zero) return false;
        try
        {
            WindowBounds expected = default;
            for (long i = 0, n = CFArrayGetCount(list); i < n; i++)
            {
                IntPtr info = CFArrayGetValueAtIndex(list, i);
                if (!CFNumberGetValue(CFDictionaryGetValue(info, KeyNumber), 4, out long number) ||
                    !CGRectMakeWithDictionaryRepresentation(CFDictionaryGetValue(info, KeyBounds), out var bounds))
                    continue;
                if (number == ours)
                {
                    expected = bounds;
                    continue;
                }
                if (expected.W <= 0) continue;
                IntPtr alpha = CFDictionaryGetValue(info, KeyAlpha);
                if (alpha != IntPtr.Zero && CFNumberGetDouble(alpha, 13 /* kCFNumberDoubleType */, out double opacity) && opacity < .99)
                    continue;
                CFNumberGetValue(CFDictionaryGetValue(info, KeyPid), 4, out long pid);
                CFNumberGetValue(CFDictionaryGetValue(info, KeyLayer), 4, out long level);
                if (pid == hostPid && Math.Abs(bounds.X - expected.X) <= 2 && Math.Abs(bounds.Y - expected.Y) <= 2 &&
                    Math.Abs(bounds.W - expected.W) <= 2 && Math.Abs(bounds.H - expected.H) <= 2)
                    return true;
                if (level == 0 && bounds.X < expected.X + expected.W && bounds.X + bounds.W > expected.X &&
                    bounds.Y < expected.Y + expected.H && bounds.Y + bounds.H > expected.Y)
                    return false;
            }
            return false;
        }
        finally { CFRelease(list); }
    }

    /// <summary>Numero della finestra di Wine che sta sopra la nostra (0 = nessuna).</summary>
    private static (long Number, long Level) WineAbove(long ours, int hostPid)
    {
        IntPtr list = CGWindowListCopyWindowInfo(OnScreenAboveWindow | ExcludeDesktop, (uint)ours);
        if (list == IntPtr.Zero) return default;
        try
        {
            var name = new byte[256];
            for (long i = 0, n = CFArrayGetCount(list); i < n; i++)
            {
                IntPtr info = CFArrayGetValueAtIndex(list, i);
                if (!CFNumberGetValue(CFDictionaryGetValue(info, KeyPid), 4, out long pid) || pid != hostPid) continue;
                CFNumberGetValue(CFDictionaryGetValue(info, KeyLayer), 4 /* kCFNumberSInt64Type */, out long layer);
                IntPtr owner = CFDictionaryGetValue(info, KeyOwnerName);
                if (owner == IntPtr.Zero || !CFStringGetCString(owner, name, name.Length, Utf8)) continue;
                string ownerName = System.Text.Encoding.UTF8.GetString(name, 0, Array.IndexOf(name, (byte)0) is int z and >= 0 ? z : name.Length);
                if (!ownerName.StartsWith("wine", StringComparison.OrdinalIgnoreCase) && !ownerName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (CFNumberGetValue(CFDictionaryGetValue(info, KeyNumber), 4, out long number)) return (number, layer);
            }
        }
        finally { CFRelease(list); }
        return default;
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendLongArg(IntPtr obj, IntPtr sel, long a);

    /// <summary>
    /// Durante il caricamento la finestra video di Wine nasce davanti a tutto: per quell'istante la nostra sta un
    /// livello sopra le finestre normali (schermata di caricamento sempre visibile), poi torna normale.
    /// </summary>
    public static void SetRaised(Avalonia.Controls.TopLevel top, bool raised)
    {
        if (!OperatingSystem.IsMacOS() || top.TryGetPlatformHandle() is not { } h) return;
        IntPtr window = h.HandleDescriptor == "NSWindow" ? h.Handle : Send(h.Handle, sel_registerName("window"));
        if (window == IntPtr.Zero) return;
        SendLongArg(window, sel_registerName("setLevel:"), raised ? 3 /* NSFloatingWindowLevel */ : 0);
    }

    /// <summary>Se una finestra di Wine e' sopra la nostra, la nostra torna subito sopra di lei. Vero se l'ha fatto.</summary>
    public static bool KeepAbove(Avalonia.Controls.TopLevel top, int hostPid)
    {
        if (hostPid == 0) return false;
        if (!OperatingSystem.IsMacOS() || top.TryGetPlatformHandle() is not { } h) return false;
        IntPtr window = h.HandleDescriptor == "NSWindow" ? h.Handle : Send(h.Handle, sel_registerName("window"));
        if (window == IntPtr.Zero) return false;
        long ours = SendLong(window, sel_registerName("windowNumber"));
        if (ours <= 0) return false;
        var wine = WineAbove(ours, hostPid);
        if (wine.Number == 0) return false;
        // Wine alza le finestre che coprono il monitor. Cambiare soltanto l'ordine non supera un livello diverso:
        // in schermo intero il vecchio timer faceva la stessa chiamata inutilmente 20 volte al secondo.
        if (top is Window owner && owner.IsActive)
        {
            long level = SendLong(window, sel_registerName("level"));
            if (wine.Level > level && wine.Level < 100)
            {
                SendLongArg(window, sel_registerName("setLevel:"), wine.Level + 1);
                if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null) Console.Error.WriteLine($"[windows] HUD livello {level}->{wine.Level + 1}, video={wine.Level}");
            }
        }
        SendOrder(window, sel_registerName("orderWindow:relativeTo:"), 1 /* NSWindowAbove */, wine.Number);
        return true;
    }
}
