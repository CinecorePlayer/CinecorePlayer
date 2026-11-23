using System;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// La ruota colori di macOS (NSColorPanel, pannello Colori di sistema) al posto del selettore disegnato: si apre sulla ruota
/// con il colore attuale, senza trasparenza; il colore scelto si applica dal vivo mentre si muove (come nelle app del Mac)
/// e il pannello si chiude con il suo pulsante. Si legge il colore ogni 80 ms finche' il pannello e' a schermo.
/// </summary>
internal static class MacColorPanel
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendLong(IntPtr target, IntPtr sel, long arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendBool(IntPtr target, IntPtr sel, bool arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern bool SendGetBool(IntPtr target, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendGetDouble(IntPtr target, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendRgba(IntPtr target, IntPtr sel, double r, double g, double b, double a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendString(IntPtr target, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    private const long NSColorPanelModeWheel = 6;
    private static DispatcherTimer? _poll;
    private static Action<Color>? _apply;
    private static Color _last;

    public static bool Available => OperatingSystem.IsMacOS() && objc_getClass("NSColorPanel") != IntPtr.Zero;

    /// <summary>Apre la ruota colori; <paramref name="apply"/> riceve ogni colore scelto (sul thread dell'interfaccia).</summary>
    public static bool Show(Color initial, string title, Action<Color> apply)
    {
        try
        {
            IntPtr panelClass = objc_getClass("NSColorPanel");
            if (panelClass == IntPtr.Zero) return false;
            IntPtr panel = Send(panelClass, sel_registerName("sharedColorPanel"));
            if (panel == IntPtr.Zero) return false;
            _apply = null; // il colore iniziale non va riapplicato
            SendBool(panel, sel_registerName("setShowsAlpha:"), false);
            SendLong(panel, sel_registerName("setMode:"), NSColorPanelModeWheel);
            SendBool(panel, sel_registerName("setContinuous:"), true);
            IntPtr nsTitle = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), title);
            Send(panel, sel_registerName("setTitle:"), nsTitle);
            IntPtr color = SendRgba(objc_getClass("NSColor"), sel_registerName("colorWithSRGBRed:green:blue:alpha:"),
                initial.R / 255.0, initial.G / 255.0, initial.B / 255.0, 1.0);
            Send(panel, sel_registerName("setColor:"), color);
            // Nelle prove in secondo piano il pannello non si mostra (non deve comparire sullo schermo dell'utente).
            if (Environment.GetEnvironmentVariable("CINECORE_QA_BACKGROUND") != "1") Send(panel, sel_registerName("makeKeyAndOrderFront:"), IntPtr.Zero);
            _last = Color.FromRgb(initial.R, initial.G, initial.B);
            _apply = apply;
            _poll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => Poll(panel));
            _poll.Start();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[colori] pannello Colori non disponibile: " + ex.Message);
            return false;
        }
    }

    private static void Poll(IntPtr panel)
    {
        try
        {
            if (!SendGetBool(panel, sel_registerName("isVisible"))) { _poll?.Stop(); _apply = null; return; }
            if (ReadColor(panel) is not { } c || c == _last) return;
            _last = c;
            _apply?.Invoke(c);
        }
        catch (Exception ex) { Console.Error.WriteLine("[colori] lettura: " + ex.Message); _poll?.Stop(); }
    }

    private static Color? ReadColor(IntPtr panel)
    {
        IntPtr color = Send(panel, sel_registerName("color"));
        if (color == IntPtr.Zero) return null;
        IntPtr srgb = Send(objc_getClass("NSColorSpace"), sel_registerName("sRGBColorSpace"));
        IntPtr converted = Send(color, sel_registerName("colorUsingColorSpace:"), srgb);
        if (converted == IntPtr.Zero) return null;
        static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return Color.FromRgb(B(SendGetDouble(converted, sel_registerName("redComponent"))),
            B(SendGetDouble(converted, sel_registerName("greenComponent"))), B(SendGetDouble(converted, sel_registerName("blueComponent"))));
    }

    /// <summary>Prova: imposta un colore nel pannello aperto come se lo scegliesse l'utente.</summary>
    internal static void TestPick(Color c)
    {
        IntPtr panel = Send(objc_getClass("NSColorPanel"), sel_registerName("sharedColorPanel"));
        Send(panel, sel_registerName("setColor:"), SendRgba(objc_getClass("NSColor"), sel_registerName("colorWithSRGBRed:green:blue:alpha:"), c.R / 255.0, c.G / 255.0, c.B / 255.0, 1.0));
    }

    internal static string TestRoundTrip(Color c)
    {
        IntPtr panel = Send(objc_getClass("NSColorPanel"), sel_registerName("sharedColorPanel"));
        TestPick(c);
        return ReadColor(panel)?.ToString() ?? "nessuno";
    }

    internal static void TestClose()
    {
        IntPtr panel = Send(objc_getClass("NSColorPanel"), sel_registerName("sharedColorPanel"));
        Send(panel, sel_registerName("orderOut:"), IntPtr.Zero);
    }
}
