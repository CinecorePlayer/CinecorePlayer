using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// Schermi collegati, con il nome che mostra macOS (NSScreen.localizedName) e la cornice in punti con l'origine in alto a
/// sinistra dello schermo principale, come le posizioni delle finestre di Avalonia sul Mac.
/// </summary>
internal static class MacScreens
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, ulong index);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong SendULong(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Rect SendRect(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendUtf8(IntPtr obj, IntPtr sel);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public double X, Y, W, H; }

    public sealed record Screen(string Name, double X, double Y, double Width, double Height, double VisibleX, double VisibleY, double VisibleWidth, double VisibleHeight);

    public static List<Screen> All()
    {
        var list = new List<Screen>();
        if (!OperatingSystem.IsMacOS()) return list;
        try
        {
            IntPtr screens = Send(objc_getClass("NSScreen"), sel_registerName("screens"));
            ulong count = SendULong(screens, sel_registerName("count"));
            if (count == 0) return list;
            double mainTop = SendRect(Send(screens, sel_registerName("objectAtIndex:"), 0UL), sel_registerName("frame")).H;
            for (ulong i = 0; i < count; i++)
            {
                IntPtr screen = Send(screens, sel_registerName("objectAtIndex:"), i);
                var f = SendRect(screen, sel_registerName("frame"));
                var v = SendRect(screen, sel_registerName("visibleFrame"));
                IntPtr nameObj = Send(screen, sel_registerName("localizedName"));
                string name = nameObj == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(SendUtf8(nameObj, sel_registerName("UTF8String"))) ?? "";
                if (name.Length == 0) name = "Schermo " + (i + 1);
                list.Add(new Screen(name, f.X, mainTop - f.Y - f.H, f.W, f.H, v.X, mainTop - v.Y - v.H, v.W, v.H));
            }
        }
        catch { }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NsPoint { public double X, Y; }
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NsPoint SendPoint(IntPtr obj, IntPtr sel);

    /// <summary>Schermo con il puntatore del mouse.</summary>
    public static Screen? MouseScreen()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var all = All();
            if (all.Count == 0) return null;
            var p = SendPoint(objc_getClass("NSEvent"), sel_registerName("mouseLocation"));
            double y = all[0].Height - p.Y; // stesse coordinate di All() (origine in alto a sinistra dello schermo principale)
            return all.Find(s => p.X >= s.X && p.X < s.X + s.Width && y >= s.Y && y < s.Y + s.Height);
        }
        catch { return null; }
    }

    /// <summary>Schermo scelto per i film (Impostazioni › Riproduzione), se e' collegato.</summary>
    public static Screen? FilmScreen()
    {
        string wanted = MacSettings.Get("filmScreen", "");
        return wanted.Length == 0 ? null : All().Find(s => s.Name == wanted);
    }
}
