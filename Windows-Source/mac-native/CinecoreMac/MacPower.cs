using System;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>Risparmio energetico di macOS (NSProcessInfo.lowPowerModeEnabled): GPU e core rallentati.</summary>
internal static class MacPower
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool MsgBool(IntPtr target, IntPtr selector);

    public static bool LowPowerMode
    {
        get
        {
            // CINECORE_LOW_POWER=0/1 lo impone (prove).
            string? forced = Environment.GetEnvironmentVariable("CINECORE_LOW_POWER");
            if (forced is "0" or "1") return forced == "1";
            try
            {
                IntPtr info = Msg(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
                return info != IntPtr.Zero && MsgBool(info, sel_registerName("isLowPowerModeEnabled"));
            }
            catch { return false; }
        }
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long MsgLong(IntPtr target, IntPtr selector);

    /// <summary>
    /// Stato termico di macOS (NSProcessInfo.thermalState): 0 normale, 1 caldo, 2 serio (il sistema rallenta),
    /// 3 critico. Sui Mac senza ventola sale dopo minuti di carico pesante. CINECORE_THERMAL=0..3 lo impone (prove).
    /// </summary>
    public static int ThermalState
    {
        get
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("CINECORE_THERMAL"), out int forced)) return Math.Clamp(forced, 0, 3);
            try
            {
                IntPtr info = Msg(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
                return info == IntPtr.Zero ? 0 : (int)Math.Clamp(MsgLong(info, sel_registerName("thermalState")), 0, 3);
            }
            catch { return 0; }
        }
    }

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(IOKit)] private static extern IntPtr IOPSCopyPowerSourcesInfo();
    [DllImport(IOKit)] private static extern IntPtr IOPSGetProvidingPowerSourceType(IntPtr snapshot);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, long size, uint encoding);
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);

    /// <summary>Il Mac va a batteria (nessun alimentatore). Senza batteria (Mac desktop) e' sempre falso.</summary>
    public static bool OnBattery
    {
        get
        {
            string? forced = Environment.GetEnvironmentVariable("CINECORE_ON_BATTERY");
            if (forced is "0" or "1") return forced == "1";
            IntPtr info = IntPtr.Zero;
            try
            {
                info = IOPSCopyPowerSourcesInfo();
                if (info == IntPtr.Zero) return false;
                IntPtr type = IOPSGetProvidingPowerSourceType(info);
                var buffer = new byte[64];
                return type != IntPtr.Zero && CFStringGetCString(type, buffer, buffer.Length, 0x08000100 /* UTF-8 */)
                    && System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0') == "Battery Power";
            }
            catch { return false; }
            finally { if (info != IntPtr.Zero) CFRelease(info); }
        }
    }
}
