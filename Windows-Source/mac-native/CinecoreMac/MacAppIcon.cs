using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// L'icona dell'app (Cinecore.icns del pacchetto) rimessa al suo posto: libmpv, quando crea la sua finestra, imposta
/// la propria icona all'applicazione e nel Dock compariva quella di mpv.
/// </summary>
internal static class MacAppIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendStr(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);

    private static IntPtr _icon;

    /// <summary>Da qualunque thread: subito e dopo 1 e 4 secondi, sul thread principale.</summary>
    public static void RestoreSoon()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Restore();
            Avalonia.Threading.DispatcherTimer.RunOnce(Restore, TimeSpan.FromSeconds(1));
            Avalonia.Threading.DispatcherTimer.RunOnce(Restore, TimeSpan.FromSeconds(4));
        });
    }

    /// <summary>Solo dal thread principale.</summary>
    public static void Restore()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            if (_icon == IntPtr.Zero)
            {
                string file = Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Cinecore.icns");
                if (!File.Exists(file)) return; // avvii senza pacchetto (prove): l'icona e' quella di sistema
                IntPtr path = SendStr(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), Path.GetFullPath(file));
                _icon = Send(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithContentsOfFile:"), path);
                if (_icon == IntPtr.Zero) return;
            }
            IntPtr app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            Send(app, sel_registerName("setApplicationIconImage:"), _icon);
        }
        catch (Exception ex) { Console.Error.WriteLine("[icona] " + ex.Message); }
    }
}
