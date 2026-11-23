#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CinecoreVideoHost;

/// <summary>
/// Ganci nella tabella delle importazioni di madVR64.ax verso mvrSettings64.dll. madVR decide i bit di uscita in
/// finestra con mvrSettings_GetMonitorBitdepth sullo schermo corrente (impostazioni per schermo): sotto Wine lo
/// schermo del Mac e' un dispositivo nuovo, a 8 bit. CINECORE_MONITOR_BITS forza il valore restituito.
/// </summary>
internal static unsafe class MadVrImports
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32")] private static extern bool VirtualProtect(IntPtr addr, nuint size, uint prot, out uint old);

    private static IntPtr _origBits, _origSetMonitor;
    // Predefinito 10: il Mac compone in virgola mobile (EDR) e DXMT presenta R10G10B10A2. CINECORE_MONITOR_BITS=0 lascia il valore di madVR.
    private static readonly int ForceBits = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_MONITOR_BITS"), out int b) ? b : 10;
    private static int _logs;

    public static void Install()
    {
        IntPtr mod = GetModuleHandleW("madVR64.ax");
        if (mod == IntPtr.Zero) { Out.Line("info madVR: modulo non trovato per i ganci"); return; }
        int n = 0;
        n += Hook(mod, "MVRSETTINGS64.DLL", "mvrSettings_GetMonitorBitdepth", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&GetMonitorBitdepth, out _origBits);
        n += Hook(mod, "MVRSETTINGS64.DLL", "mvrSettings_SetMonitor", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&SetMonitor, out _origSetMonitor);
        Out.Line($"info madVR: {n} ganci su mvrSettings64 installati");
        if (Tl.On)
        {
            int w = Hook(mod, "KERNEL32.dll", "WaitForSingleObject", (IntPtr)(delegate* unmanaged<IntPtr, uint, uint>)&TlWaitSingle, out _origWaitSingle);
            w += Hook(mod, "KERNEL32.dll", "WaitForMultipleObjects", (IntPtr)(delegate* unmanaged<uint, IntPtr, int, uint, uint>)&TlWaitMulti, out _origWaitMulti);
            w += Hook(mod, "KERNEL32.dll", "Sleep", (IntPtr)(delegate* unmanaged<uint, void>)&TlSleep, out _origSleep);
            w += Hook(mod, "USER32.dll", "MsgWaitForMultipleObjects", (IntPtr)(delegate* unmanaged<uint, IntPtr, int, uint, uint, uint>)&TlMsgWait, out _origMsgWait);
            Out.Line($"info madVR: {w} ganci sulle attese (linea temporale)");
        }
        if (Environment.GetEnvironmentVariable("CINECORE_D3D9_REUSE") != "0")
        {
            int d = Hook(mod, "d3d9.dll", "Direct3DCreate9", (IntPtr)(delegate* unmanaged<uint, IntPtr>)&Create9, out _origCreate9);
            d += Hook(mod, "KERNEL32.dll", "GetProcAddress", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&GetProcAddressHook, out _origGetProc);
            Out.Line($"info madVR: {d} ganci Direct3DCreate9 installati (oggetti riusati)");
        }
    }

    private static IntPtr _origWaitSingle, _origWaitMulti, _origSleep, _origMsgWait;
    [UnmanagedCallersOnly]
    private static uint TlWaitSingle(IntPtr h, uint ms)
    { long t = Tl.Now; uint r = ((delegate* unmanaged<IntPtr, uint, uint>)_origWaitSingle)(h, ms); double d = Tl.Ms(t); if (d >= 0.2) Tl.Ev($"ATTESA WaitSingle h={h:X} max={(int)ms} durata={d:0.00} r={r}"); return r; }
    [UnmanagedCallersOnly]
    private static uint TlWaitMulti(uint n, IntPtr hs, int all, uint ms)
    { long t = Tl.Now; uint r = ((delegate* unmanaged<uint, IntPtr, int, uint, uint>)_origWaitMulti)(n, hs, all, ms); double d = Tl.Ms(t); if (d >= 0.2) Tl.Ev($"ATTESA WaitMulti n={n} h0={(*(IntPtr*)hs):X} max={(int)ms} durata={d:0.00} r={r}"); return r; }
    [UnmanagedCallersOnly]
    private static void TlSleep(uint ms)
    { long t = Tl.Now; ((delegate* unmanaged<uint, void>)_origSleep)(ms); double d = Tl.Ms(t); if (d >= 0.2) Tl.Ev($"ATTESA Sleep({ms}) durata={d:0.00}"); }
    [UnmanagedCallersOnly]
    private static uint TlMsgWait(uint n, IntPtr hs, int all, uint ms, uint mask)
    { long t = Tl.Now; uint r = ((delegate* unmanaged<uint, IntPtr, int, uint, uint, uint>)_origMsgWait)(n, hs, all, ms, mask); double d = Tl.Ms(t); if (d >= 0.2) Tl.Ev($"ATTESA MsgWait n={n} max={(int)ms} durata={d:0.00} r={r}"); return r; }

    // ------------------------------------------------------------------ oggetti Direct3D 9 riusati
    // Ogni Direct3DCreate9(Ex) di Wine crea un adattatore wined3d nuovo: finestra nascosta + contesto OpenGL per
    // leggere le capacita', poi DestroyWindow. madVR lo fa anche durante la riproduzione. Sotto MetalSharp Wine ARM64
    // quel DestroyWindow si blocca contro il thread che disegna (visto con winedbg: NtUserDestroyWindow da
    // wined3d_caps_gl_ctx_destroy, 3 aperture su 8 del 4K H.264). Un solo oggetto per tipo, tenuto vivo dal VideoHost:
    // l'adattatore si crea una volta. Gli oggetti Direct3D 9 non hanno stato proprio oltre all'adattatore.
    // CINECORE_D3D9_REUSE=0 torna al comportamento di Wine.
    private static IntPtr _origCreate9, _origGetProc, _cached9, _cached9Ex;
    private static readonly object CreateLock = new();
    private static int _reuseLogs;
    private static readonly IntPtr Create9ExName = Marshal.StringToHGlobalAnsi("Direct3DCreate9Ex");
    [DllImport("d3d9.dll", EntryPoint = "Direct3DCreate9Ex")] private static extern int RealCreate9Ex(uint sdk, out IntPtr d3d);

    private static void AddRef(IntPtr obj) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)obj)[1])(obj);

    [UnmanagedCallersOnly]
    private static IntPtr Create9(uint sdk)
    { using var __hook = HookTrace.Enter("Create9");
        lock (CreateLock)
        {
            if (_cached9 == IntPtr.Zero)
            {
                _cached9 = ((delegate* unmanaged<uint, IntPtr>)_origCreate9)(sdk);
                if (_cached9 == IntPtr.Zero) return IntPtr.Zero;
                // Riferimento del VideoHost: l'oggetto non scende mai a zero.
            }
            else AddRef(_cached9);
            if (Interlocked.Increment(ref _reuseLogs) < 8) Out.Line($"info d3d9: Direct3DCreate9 sdk={sdk} thread={Environment.CurrentManagedThreadId} oggetto=0x{_cached9:X}");
            return _cached9;
        }
    }

    [UnmanagedCallersOnly]
    private static int Create9Ex(uint sdk, IntPtr* result)
    { using var __hook = HookTrace.Enter("Create9Ex");
        if (result == null) return unchecked((int)0x8876086C); // D3DERR_INVALIDCALL
        lock (CreateLock)
        {
            if (_cached9Ex == IntPtr.Zero)
            {
                // Lo stesso oggetto su cui D3D9Shim ha agganciato le tabelle, se c'e'.
                _cached9Ex = D3D9Shim.SharedD3D9Ex;
                if (_cached9Ex == IntPtr.Zero)
                {
                    int hr = RealCreate9Ex(sdk, out IntPtr created);
                    if (hr != 0 || created == IntPtr.Zero) { *result = IntPtr.Zero; return hr; }
                    _cached9Ex = created;
                }
            }
            AddRef(_cached9Ex);
            *result = _cached9Ex;
            if (Interlocked.Increment(ref _reuseLogs) < 8) Out.Line($"info d3d9: Direct3DCreate9Ex sdk={sdk} thread={Environment.CurrentManagedThreadId} oggetto=0x{_cached9Ex:X}");
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr GetProcAddressHook(IntPtr module, IntPtr name)
    { using var __hook = HookTrace.Enter("GetProcAddressHook");
        // Nome per ordinale (parola alta zero): passa diretto.
        if ((ulong)name > 0xFFFF)
        {
            try
            {
                if (Marshal.PtrToStringAnsi(name) == "Direct3DCreate9Ex")
                    return (IntPtr)(delegate* unmanaged<uint, IntPtr*, int>)&Create9Ex;
            }
            catch { }
        }
        return ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)_origGetProc)(module, name);
    }

    internal static int HookImport(IntPtr mod, string dll, string func, IntPtr hook, out IntPtr original) => Hook(mod, dll, func, hook, out original);

    private static int Hook(IntPtr mod, string dll, string func, IntPtr hook, out IntPtr original)
    {
        original = IntPtr.Zero;
        byte* b = (byte*)mod;
        int nt = *(int*)(b + 0x3C);
        // IMAGE_OPTIONAL_HEADER64: DataDirectory[1] (import) a offset 0x78 dall'inizio dell'header opzionale (+0x18).
        uint impRva = *(uint*)(b + nt + 0x18 + 0x78);
        if (impRva == 0) return 0;
        for (uint* d = (uint*)(b + impRva); d[3] != 0; d += 5)
        {
            string name = Marshal.PtrToStringAnsi((IntPtr)(b + d[3])) ?? "";
            if (!name.Equals(dll, StringComparison.OrdinalIgnoreCase)) continue;
            ulong* names = (ulong*)(b + (d[0] != 0 ? d[0] : d[4]));
            IntPtr* slots = (IntPtr*)(b + d[4]);
            for (int i = 0; names[i] != 0; i++)
            {
                if ((names[i] & 0x8000000000000000UL) != 0) continue;
                string fn = Marshal.PtrToStringAnsi((IntPtr)(b + (uint)names[i] + 2)) ?? "";
                if (fn != func) continue;
                original = slots[i];
                VirtualProtect((IntPtr)(&slots[i]), (nuint)IntPtr.Size, 0x04, out uint old);
                slots[i] = hook;
                VirtualProtect((IntPtr)(&slots[i]), (nuint)IntPtr.Size, old, out _);
                return 1;
            }
        }
        return 0;
    }

    private static readonly bool BitsCacheOn = Environment.GetEnvironmentVariable("CINECORE_BITDEPTH_CACHE") != "0";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(IntPtr, IntPtr), (IntPtr R, long At)> BitsCache = new();
    [UnmanagedCallersOnly]
    private static IntPtr GetMonitorBitdepth(IntPtr a, IntPtr b, IntPtr c, IntPtr d)
    { using var __hook = HookTrace.Enter("GetMonitorBitdepth");
        // madVR lo chiede centinaia di volte al secondo: la risposta si ricorda per mezzo secondo.
        long nowMs = Environment.TickCount64;
        if (BitsCacheOn && _logs >= 12)
        {
            if (ForceBits > 0) return (IntPtr)ForceBits; // risposta imposta: inutile chiedere
            if (BitsCache.TryGetValue((a, b), out var hit) && nowMs - hit.At < 500) return hit.R;
        }
        IntPtr r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)_origBits)(a, b, c, d);
        if (BitsCacheOn) BitsCache[(a, b)] = (r, nowMs);
        long v = (long)r & 0xFFFFFFFF;
        if (Interlocked.Increment(ref _logs) < 12) Out.Line($"info madVR: GetMonitorBitdepth(0x{a:X}, 0x{b:X}) -> {v}{(ForceBits > 0 ? $" (forzato {ForceBits})" : "")}");
        return ForceBits > 0 ? (IntPtr)ForceBits : r;
    }

    [UnmanagedCallersOnly]
    private static IntPtr SetMonitor(IntPtr a, IntPtr b, IntPtr c, IntPtr d)
    { using var __hook = HookTrace.Enter("SetMonitor");
        IntPtr r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)_origSetMonitor)(a, b, c, d);
        string s = "";
        try { s = a != IntPtr.Zero ? Marshal.PtrToStringUni(a) ?? "" : ""; } catch { }
        if (Interlocked.Increment(ref _logs) < 12) Out.Line($"info madVR: SetMonitor('{(s.Length > 80 ? s[..80] : s)}', 0x{b:X}) -> 0x{r:X}");
        return r;
    }
}
