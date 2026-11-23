using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace CinecoreMac;

/// <summary>
/// Puntatore nascosto durante il film. Avalonia su macOS chiama [NSCursor hide] a ogni impostazione di un cursore
/// "nessuno" e macOS conta le chiamate: una in piu' e il puntatore non torna. Qui lo stato e' uno solo (nascosto o no),
/// quindi le chiamate restano sempre in pari, e il movimento si legge dalla posizione vera del mouse
/// (NSEvent.mouseLocation), non dagli eventi che la finestra puo' non ricevere.
/// </summary>
internal static class MacCursor
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGPoint SendPoint(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr obj, IntPtr sel);

    private static bool _hidden;
    public static bool Hidden => _hidden;

    /// <summary>Nasconde o mostra il puntatore (solo dal thread dell'interfaccia). Ripetere lo stesso stato non fa nulla.</summary>
    public static void SetHidden(bool hidden)
    {
        if (hidden == _hidden || !OperatingSystem.IsMacOS()) return;
        try
        {
            Send(objc_getClass("NSCursor"), sel_registerName(hidden ? "hide" : "unhide"));
            _hidden = hidden;
        }
        catch { }
    }

    /// <summary>Posizione del mouse sullo schermo, in punti (origine in basso a sinistra).</summary>
    public static Point? ScreenLocation()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try { var p = SendPoint(objc_getClass("NSEvent"), sel_registerName("mouseLocation")); return new Point(p.X, p.Y); }
        catch { return null; }
    }

    /// <summary>La stessa posizione dentro la finestra (origine in alto a sinistra), o null se il mouse e' fuori.</summary>
    public static Point? InWindow(TopLevel top, Point screen)
    {
        try
        {
            if (top.TryGetPlatformHandle() is not { } h) return null;
            IntPtr window = h.HandleDescriptor == "NSWindow" ? h.Handle : Send(h.Handle, sel_registerName("window"));
            if (window == IntPtr.Zero) return null;
            var f = SendRect(window, sel_registerName("frame"));
            double x = screen.X - f.X, y = f.Y + f.H - screen.Y;
            return x >= 0 && y >= 0 && x <= f.W && y <= f.H ? new Point(x, y) : null;
        }
        catch { return null; }
    }
}
