using System;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// Cambio di renderer che portava su un'altra Scrivania: mpv crea la sua finestra e la porta davanti (rendendola chiave)
/// prima che il player la agganci come figlia (MpvEngine.AdoptWindow). Con l'app in primo piano e il player a schermo
/// intero, quella finestra nasceva sulla Scrivania normale e macOS ci saltava sopra. Qui, prima che una finestra che non
/// e' di Avalonia venga mostrata, le si danno "nello Spazio attivo" e "ausiliaria dello schermo intero": resta dove si
/// trova il player. Le finestre del player (Avalonia, AvnWindow) non vengono toccate.
/// </summary>
internal static unsafe class MacSpaceGuard
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr class_getInstanceMethod(IntPtr cls, IntPtr sel);
    [DllImport(ObjC)] private static extern IntPtr method_getImplementation(IntPtr method);
    [DllImport(ObjC)] private static extern IntPtr method_setImplementation(IntPtr method, IntPtr imp);
    [DllImport(ObjC)] private static extern IntPtr object_getClass(IntPtr obj);
    [DllImport(ObjC)] private static extern IntPtr class_getName(IntPtr cls);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong SendULong(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSetULong(IntPtr obj, IntPtr sel, ulong value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendIsKind(IntPtr obj, IntPtr sel, IntPtr cls);

    private const ulong MoveToActiveSpace = 1 << 1, FullScreenAuxiliary = 1 << 8, CanJoinAllSpaces = 1 << 0, FullScreenPrimary = 1 << 7;

    private static IntPtr _origMakeKey, _origOrderFront;
    private static bool _installed;

    public static void Install()
    {
        if (_installed || !OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("CINECORE_SPACE_GUARD") == "0") return;
        _installed = true;
        try
        {
            IntPtr window = objc_getClass("NSWindow");
            IntPtr makeKey = class_getInstanceMethod(window, sel_registerName("makeKeyAndOrderFront:"));
            IntPtr orderFront = class_getInstanceMethod(window, sel_registerName("orderFront:"));
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> mk = &MakeKeyAndOrderFront;
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> of = &OrderFront;
            if (makeKey != IntPtr.Zero) _origMakeKey = method_setImplementation(makeKey, (IntPtr)mk);
            if (orderFront != IntPtr.Zero) _origOrderFront = method_setImplementation(orderFront, (IntPtr)of);
        }
        catch (Exception ex) { Console.Error.WriteLine("[spazi] protezione non installata: " + ex.Message); }
    }

    private static void Prepare(IntPtr self)
    {
        try
        {
            string name = Marshal.PtrToStringUTF8(class_getName(object_getClass(self))) ?? "";
            // Finestre di Avalonia (il player, i menu) e pannelli di sistema (Colori, Apri/Salva): come sono.
            if (name.StartsWith("Avn", StringComparison.Ordinal) || name.Contains("Panel", StringComparison.Ordinal) || name.StartsWith("NS", StringComparison.Ordinal)) return;
            ulong behavior = SendULong(self, sel_registerName("collectionBehavior"));
            ulong wanted = (behavior & ~(FullScreenPrimary | CanJoinAllSpaces)) | MoveToActiveSpace | FullScreenAuxiliary;
            if (wanted != behavior) SendSetULong(self, sel_registerName("setCollectionBehavior:"), wanted);
        }
        catch { }
    }

    [UnmanagedCallersOnly]
    private static void MakeKeyAndOrderFront(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        Prepare(self);
        if (_origMakeKey != IntPtr.Zero) ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)_origMakeKey)(self, cmd, sender);
    }

    [UnmanagedCallersOnly]
    private static void OrderFront(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        Prepare(self);
        if (_origOrderFront != IntPtr.Zero) ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)_origOrderFront)(self, cmd, sender);
    }
}
