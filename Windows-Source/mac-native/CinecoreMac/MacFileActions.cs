using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace CinecoreMac;

/// <summary>
/// Azioni di macOS su un file (al posto di quelle di Windows: pannello Condividi, Apri con, Cestino, appunti):
/// NSSharingServicePicker, NSPasteboard, NSFileManager.trashItemAtURL e le app che Launch Services propone.
/// </summary>
internal static class MacFileActions
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendStr(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendRectEdge(IntPtr obj, IntPtr sel, CGRect rect, IntPtr view, ulong edge);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendUtf8(IntPtr obj, IntPtr sel);
    [DllImport(CoreServices)] private static extern IntPtr LSCopyApplicationURLsForURL(IntPtr url, uint roles);
    [DllImport(CoreServices)] private static extern IntPtr LSCopyDefaultApplicationURLForURL(IntPtr url, uint roles, IntPtr error);
    [DllImport(CoreFoundation)] private static extern long CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }

    private static IntPtr S(string name) => sel_registerName(name);
    private static IntPtr NSString(string text) => SendStr(objc_getClass("NSString"), S("stringWithUTF8String:"), text);
    private static IntPtr FileUrl(string path) => Send(objc_getClass("NSURL"), S("fileURLWithPath:"), NSString(path));
    private static string? Utf8(IntPtr nsString) => nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(SendUtf8(nsString, S("UTF8String")));

    private static IntPtr Window(TopLevel top)
    {
        if (top.TryGetPlatformHandle() is not { } handle) return IntPtr.Zero;
        return handle.HandleDescriptor == "NSWindow" ? handle.Handle : Send(handle.Handle, S("window"));
    }

    /// <summary>Il pannello Condividi di macOS (AirDrop, Messaggi, Mail, Note…), aperto accanto a <paramref name="anchor"/>.</summary>
    public static bool Share(TopLevel top, string path, Rect anchor)
    {
        try
        {
            IntPtr window = Window(top);
            if (window == IntPtr.Zero) return false;
            IntPtr view = Send(window, S("contentView"));
            IntPtr items = Send(objc_getClass("NSArray"), S("arrayWithObject:"), FileUrl(path));
            IntPtr picker = Send(Send(objc_getClass("NSSharingServicePicker"), S("alloc")), S("initWithItems:"), items);
            var frame = SendRect(view, S("bounds"));
            bool flipped = SendBool(view, S("isFlipped"));
            var rect = new CGRect { X = anchor.X, Y = flipped ? anchor.Y : frame.H - anchor.Bottom, W = Math.Max(1, anchor.Width), H = Math.Max(1, anchor.Height) };
            SendRectEdge(picker, S("showRelativeToRect:ofView:preferredEdge:"), rect, view, 1); // NSMinYEdge (sopra, in coordinate non capovolte)
            return true;
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] share: " + ex.Message); return false; }
    }

    /// <summary>Negli appunti il file e l'immagine: si incolla sia in Finder sia nelle app di immagini.</summary>
    public static bool CopyImage(string path)
    {
        try
        {
            IntPtr pb = Send(objc_getClass("NSPasteboard"), S("generalPasteboard"));
            Send(pb, S("clearContents"));
            IntPtr url = FileUrl(path);
            IntPtr image = Send(Send(objc_getClass("NSImage"), S("alloc")), S("initWithContentsOfURL:"), url);
            GCHandle handle = default;
            IntPtr objects = image != IntPtr.Zero
                ? Send(objc_getClass("NSArray"), S("arrayWithObjects:count:"), PinArray(url, image, out handle), (IntPtr)2)
                : Send(objc_getClass("NSArray"), S("arrayWithObject:"), url);
            bool ok = SendBool(pb, S("writeObjects:"), objects);
            if (image != IntPtr.Zero) { handle.Free(); Send(image, S("release")); }
            return ok;
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] copy: " + ex.Message); return false; }
    }

    private static IntPtr PinArray(IntPtr a, IntPtr b, out GCHandle handle)
    {
        var array = new[] { a, b };
        handle = GCHandle.Alloc(array, GCHandleType.Pinned);
        return handle.AddrOfPinnedObject();
    }

    /// <summary>Sposta nel Cestino (si recupera da li'), come il Cestino di Windows.</summary>
    public static bool Trash(string path)
    {
        try
        {
            IntPtr fm = Send(objc_getClass("NSFileManager"), S("defaultManager"));
            return SendBool(fm, S("trashItemAtURL:resultingItemURL:error:"), FileUrl(path), IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] trash: " + ex.Message); return false; }
    }

    /// <summary>Le app che macOS propone per aprire il file (Apri con), la predefinita per prima.</summary>
    public static List<(string Name, string AppPath)> OpenWithApps(string path)
    {
        var result = new List<(string, string)>();
        try
        {
            IntPtr url = FileUrl(path);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr def = LSCopyDefaultApplicationURLForURL(url, 0xFFFFFFFF, IntPtr.Zero);
            if (def != IntPtr.Zero) { Add(def); CFRelease(def); }
            IntPtr apps = LSCopyApplicationURLsForURL(url, 0xFFFFFFFF);
            if (apps != IntPtr.Zero)
            {
                for (long i = 0; i < CFArrayGetCount(apps); i++) Add(CFArrayGetValueAtIndex(apps, i));
                CFRelease(apps);
            }
            void Add(IntPtr appUrl)
            {
                string? app = Utf8(Send(appUrl, S("path")));
                if (app == null || !seen.Add(app)) return;
                string name = Path.GetFileNameWithoutExtension(app);
                IntPtr fm = Send(objc_getClass("NSFileManager"), S("defaultManager"));
                name = Utf8(Send(fm, S("displayNameAtPath:"), NSString(app))) ?? name;
                if (name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                result.Add((name, app));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] open with: " + ex.Message); }
        return result;
    }

    public static void OpenWith(string appPath, string file) => Run("/usr/bin/open", "-a", appPath, Path.GetFullPath(file));

    public static void OpenFolder(string folder)
    {
        try { Directory.CreateDirectory(folder); } catch { }
        Run("/usr/bin/open", Path.GetFullPath(folder));
    }

    public static void RevealInFinder(string file) => Run("/usr/bin/open", "-R", Path.GetFullPath(file));

    /// <summary>Stampa con la finestra di stampa di Anteprima (come il verbo "print" di Windows, che apre la procedura di stampa).</summary>
    public static void Print(string file)
    {
        string escaped = Path.GetFullPath(file).Replace("\\", "\\\\").Replace("\"", "\\\"");
        Run("/usr/bin/osascript", "-e", $"tell application \"Preview\" to print POSIX file \"{escaped}\" with print dialog");
    }

    private static void Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Console.Error.WriteLine("[PHOTO] " + exe + ": " + ex.Message); }
    }
}
