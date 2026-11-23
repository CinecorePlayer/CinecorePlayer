using System;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// "Apri Cinecore all'avvio del Mac" (Avvio con Windows su Windows): SMAppService.mainAppService, la stessa voce di
/// Impostazioni di Sistema › Generali › Elementi login. Funziona solo dall'app impacchettata (.app).
/// </summary>
internal static class LoginItem
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendLong(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern bool SendBoolError(IntPtr target, IntPtr selector, out IntPtr error);
    [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

    private static IntPtr Service()
    {
        dlopen("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement", 1);
        IntPtr cls = objc_getClass("SMAppService");
        return cls == IntPtr.Zero ? IntPtr.Zero : Send(cls, sel_registerName("mainAppService"));
    }

    private static bool InBundle => AppContext.BaseDirectory.Contains(".app/Contents/MacOS", StringComparison.Ordinal);

    /// <summary>Stato reale (non quello salvato): l'utente puo' toglierla anche da Impostazioni di Sistema.</summary>
    public static bool Enabled
    {
        get
        {
            try { IntPtr s = InBundle ? Service() : IntPtr.Zero; return s != IntPtr.Zero && SendLong(s, sel_registerName("status")) == 1; }
            catch { return false; }
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            if (!InBundle) return false;
            IntPtr s = Service();
            if (s == IntPtr.Zero) return false;
            return SendBoolError(s, sel_registerName(enabled ? "registerAndReturnError:" : "unregisterAndReturnError:"), out _) && Enabled == enabled;
        }
        catch { return false; }
    }
}
