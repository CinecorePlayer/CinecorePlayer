#nullable enable
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;

namespace CinecoreVideoHost;

/// <summary>
/// Strato nostro sopra il Direct3D 9 di Wine, nello stesso processo di madVR. Si aggancia alle tabelle delle
/// funzioni (vtable) condivise da tutti gli oggetti di d3d9.dll. Fase 1: registra cosa fa madVR (dispositivi,
/// risorse condivise, Present) per capire come emulare la condivisione che Wine non implementa.
/// </summary>
internal static unsafe class D3D9Shim
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PresentParams
    {
        public uint W, H; public int Fmt; public uint Count; public int Ms; public uint MsQ; public int Swap;
        public IntPtr Hwnd; public int Windowed, AutoDS, DSFmt; public uint Flags, Refresh, Interval;
    }

    [DllImport("d3d9.dll")] private static extern int Direct3DCreate9Ex(uint sdk, out IntPtr d3d);
    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr addr, nuint size, uint prot, out uint old);

    private static IntPtr V(IntPtr obj, int i) => (*(IntPtr**)obj)[i];

    private static void Patch(IntPtr obj, int index, IntPtr hook, out IntPtr original)
    {
        IntPtr* vtbl = *(IntPtr**)obj;
        original = vtbl[index];
        VirtualProtect((IntPtr)(&vtbl[index]), (nuint)IntPtr.Size, 0x04 /*PAGE_READWRITE*/, out uint old);
        vtbl[index] = hook;
        VirtualProtect((IntPtr)(&vtbl[index]), (nuint)IntPtr.Size, old, out _);
    }

    private static IntPtr _origCreateDeviceEx, _origCreateDevice, _origGetMode, _origGetModeEx, _origCheckType;
    // Formato dello schermo dichiarato a madVR: X8R8G8B8 (22) di Wine, oppure A2R10G10B10 (35) con CINECORE_D3D9_MODE10=1.
    private static readonly bool Mode10 = Environment.GetEnvironmentVariable("CINECORE_D3D9_MODE10") == "1";
    private static readonly ConcurrentDictionary<string, int> Logged = new();
    private static void Once(string s) { if (Logged.TryAdd(s, 1)) Out.Line(s); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int GetAdapterDisplayMode(IntPtr d3d, uint adapter, uint* mode)
    { using var __hook = HookTrace.Enter("GetAdapterDisplayMode");
        Out.Tid("GetAdapterDisplayMode");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, int>)_origGetMode)(d3d, adapter, mode);
        // D3DDISPLAYMODE: Width, Height, RefreshRate, Format
        if (hr == 0 && mode != null) { Once($"info shim: GetAdapterDisplayMode {mode[0]}x{mode[1]}@{mode[2]} fmt={mode[3]}"); if (Mode10 && mode[3] == 22) mode[3] = 35; }
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int GetAdapterDisplayModeEx(IntPtr d3d, uint adapter, uint* mode, int* rotation)
    { using var __hook = HookTrace.Enter("GetAdapterDisplayModeEx");
        Out.Tid("GetAdapterDisplayModeEx");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, int*, int>)_origGetModeEx)(d3d, adapter, mode, rotation);
        // D3DDISPLAYMODEEX: Size, Width, Height, RefreshRate, Format, ScanLineOrdering
        if (hr == 0 && mode != null) { Once($"info shim: GetAdapterDisplayModeEx {mode[1]}x{mode[2]}@{mode[3]} fmt={mode[4]}"); if (Mode10 && mode[4] == 22) mode[4] = 35; }
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CheckDeviceType(IntPtr d3d, uint adapter, int type, int displayFmt, int backFmt, int windowed)
    { using var __hook = HookTrace.Enter("CheckDeviceType");
        Out.Tid("CheckDeviceType");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, int, int, int>)_origCheckType)(d3d, adapter, type, displayFmt == 35 ? 22 : displayFmt, backFmt, windowed);
        Once($"info shim: CheckDeviceType display={displayFmt} back={backFmt} windowed={windowed} hr=0x{hr:X8}");
        return hr;
    }
    private static IntPtr _origCreateTexture, _origCreateRenderTarget, _origCreateOffscreen, _origPresent, _origPresentEx;
    private static int _deviceHooked;
    private static IntPtr _origDeviceQI, _origSwapChainQI, _origSwapChainPresent;
    private static IntPtr _d3d;
    /// <summary>L'IDirect3D9Ex agganciato, vivo per tutto il processo (riusato da MadVrImports).</summary>
    internal static IntPtr SharedD3D9Ex => _d3d;
    private static IntPtr _origCreateQuery, _origQueryGetData, _origQueryIssue;
    private static IntPtr _origSetRT, _origStretch, _origDraw, _origDrawUP, _origClear, _origSetTexture, _origDrawIndexed, _origUpdateSurface, _origColorFill;
    [ThreadStatic] private static IntPtr _currentRT;
    private static int _queryHooked;
    private static readonly ConcurrentDictionary<IntPtr, long> QueryStart = new();
    private static long _fakedEvents, _realEvents;
    private static int _diag, _diag2;
    internal static readonly ConcurrentDictionary<string, long> Calls = new();
    internal static readonly bool Diag = Environment.GetEnvironmentVariable("CINECORE_SHIM_DIAG") == "1";
    private static void Count(string what) => Calls.AddOrUpdate(what, 1, (_, n) => n + 1);
    private static long _presentCount, _lastPresentQpc;
    private static readonly ConcurrentDictionary<IntPtr, int> Presents = new();
    private static int _deviceIndex;
    private static readonly ConcurrentDictionary<IntPtr, int> DeviceIds = new();

    public static void Install()
    {
        D3D9SharedBridge.StepPause("avvio");
        if (Direct3DCreate9Ex(32, out IntPtr d3d) != 0 || d3d == IntPtr.Zero) { Out.Line("info shim: Direct3DCreate9Ex fallito"); return; }
        D3D9SharedBridge.StepPause("Direct3DCreate9Ex");
        Patch(d3d, 20, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, PresentParams*, IntPtr, IntPtr*, int>)&CreateDeviceEx, out _origCreateDeviceEx);
        Patch(d3d, 16, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, PresentParams*, IntPtr*, int>)&CreateDevice, out _origCreateDevice);
        _d3d = d3d;
        Patch(d3d, 8, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, int>)&GetAdapterDisplayMode, out _origGetMode);
        Patch(d3d, 19, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, int*, int>)&GetAdapterDisplayModeEx, out _origGetModeEx);
        Patch(d3d, 10, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, int, int, int>)&CheckDeviceType, out _origCheckType);
        Out.Line("info shim: installato su IDirect3D9Ex");
        if (Environment.GetEnvironmentVariable("CINECORE_D3D9_BRIDGE") != "0") D3D9SharedBridge.Install();
        // L'oggetto resta vivo: la vtable e' statica in d3d9.dll, il modulo non deve essere scaricato.
    }

    private static int Id(IntPtr dev) => DeviceIds.GetOrAdd(dev, _ => Interlocked.Increment(ref _deviceIndex));

    private static void HookDevice(IntPtr dev)
    {
        if (Interlocked.Exchange(ref _deviceHooked, 1) != 0) return;
        Patch(dev, 23, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)&CreateTexture, out _origCreateTexture);
        Patch(dev, 28, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, int>)&CreateRenderTarget, out _origCreateRenderTarget);
        Patch(dev, 36, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)&CreateOffscreen, out _origCreateOffscreen);
        Patch(dev, 17, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)&Present, out _origPresent);
        Patch(dev, 121, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, uint, int>)&PresentEx, out _origPresentEx);
        Patch(dev, 0, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&DeviceQueryInterface, out _origDeviceQI);
        Patch(dev, 118, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)&CreateQuery, out _origCreateQuery);
        Patch(dev, 37, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)&SetRenderTarget, out _origSetRT);
        Patch(dev, 34, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int>)&StretchRect, out _origStretch);
        Patch(dev, 81, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, uint, uint, int>)&DrawPrimitive, out _origDraw);
        Patch(dev, 83, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr, uint, int>)&DrawPrimitiveUP, out _origDrawUP);
        Patch(dev, 43, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, uint, float, uint, int>)&ClearHook, out _origClear);
        Patch(dev, 65, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)&SetTexture, out _origSetTexture);
        Patch(dev, 82, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, uint, uint, uint, uint, int>)&DrawIndexedPrimitive, out _origDrawIndexed);
        Patch(dev, 30, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)&UpdateSurfaceHook, out _origUpdateSurface);
        Patch(dev, 35, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, int>)&ColorFillHook, out _origColorFill);
        if (JincSkip) Patch(dev, 106, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr*, int>)&CreatePixelShaderHook, out _origCreatePs);
        IntPtr chain;
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(dev, 14))(dev, 0, &chain) == 0 && chain != IntPtr.Zero)
        {
            Patch(chain, 0, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&SwapChainQueryInterface, out _origSwapChainQI);
            Patch(chain, 3, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, uint, int>)&SwapChainPresent, out _origSwapChainPresent);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(chain, 2))(chain);
        }
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(3000);
                foreach (var kv in Presents) Out.Line($"info shim: device#{Id(kv.Key)} present={kv.Value}");
            }
        }) { IsBackground = true };
        t.Start();
    }

    private static string Pp(PresentParams* p) => p == null ? "null" :
        $"{p->W}x{p->H} fmt={p->Fmt} bb={p->Count} swap={p->Swap} hwnd=0x{p->Hwnd:X} windowed={p->Windowed} interval=0x{p->Interval:X} flags=0x{p->Flags:X}";

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CreateDeviceEx(IntPtr self, uint adapter, int type, IntPtr focus, uint flags, PresentParams* pp, IntPtr mode, IntPtr* dev)
    { using var __hook = HookTrace.Enter("CreateDeviceEx");
        Out.Tid("CreateDeviceEx");
        string before = Pp(pp);
        // Wine non implementa FLIPEX (5) ne' piu' back buffer: la swapchain diventa DISCARD con un back buffer.
        // madVR legge solo le statistiche (IDirect3DSwapChain9Ex emulata qui sotto), non il modello di flip.
        int origSwap = 0; uint origCount = 0;
        if (pp != null && pp->Swap == 5 && Environment.GetEnvironmentVariable("CINECORE_D3D9_KEEP_FLIPEX") != "1")
        {
            origSwap = pp->Swap; origCount = pp->Count;
            pp->Swap = 1; pp->Count = 1;
        }
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, PresentParams*, IntPtr, IntPtr*, int>)_origCreateDeviceEx)(self, adapter, type, focus, flags, pp, mode, dev);
        if (origSwap != 0) { Out.Line($"info shim: FLIPEX x{origCount} -> DISCARD x1"); pp->Swap = origSwap; pp->Count = origCount; }
        if (hr == 0 && dev != null && *dev != IntPtr.Zero) HookDevice(*dev);
        Out.Line($"info shim: CreateDeviceEx#{(hr == 0 ? Id(*dev) : 0)} thread={Environment.CurrentManagedThreadId} focus=0x{focus:X} flags=0x{flags:X} pp={before} hr=0x{hr:X8}");
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CreateDevice(IntPtr self, uint adapter, int type, IntPtr focus, uint flags, PresentParams* pp, IntPtr* dev)
    { using var __hook = HookTrace.Enter("CreateDevice");
        Out.Tid("CreateDevice");
        string before = Pp(pp);
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, PresentParams*, IntPtr*, int>)_origCreateDevice)(self, adapter, type, focus, flags, pp, dev);
        if (hr == 0 && dev != null && *dev != IntPtr.Zero) HookDevice(*dev);
        Out.Line($"info shim: CreateDevice#{(hr == 0 ? Id(*dev) : 0)} focus=0x{focus:X} flags=0x{flags:X} pp={before} hr=0x{hr:X8}");
        return hr;
    }

    // Solo prove: CINECORE_TEX_LOG=1 registra tutte le texture e superfici create da madVR (non solo le condivise).
    private static readonly bool TexLog = Environment.GetEnvironmentVariable("CINECORE_TEX_LOG") == "1";
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CreateTexture(IntPtr dev, uint w, uint h, uint levels, uint usage, int fmt, int pool, IntPtr* tex, IntPtr* shared)
    { using var __hook = HookTrace.Enter("CreateTexture");
        Out.Tid("CreateTexture");
        IntPtr inHandle = shared == null ? IntPtr.Zero : *shared;
        bool bridge = shared != null && inHandle == IntPtr.Zero && Environment.GetEnvironmentVariable("CINECORE_D3D9_BRIDGE") != "0";
        // wined3d su OpenGL macOS non rende in A2R10G10B10 (ordine BGR): A2B10G10R10 (= GL_RGB10_A2, = DXGI R10G10B10A2).
        int realFmt = bridge && fmt == 31 && Environment.GetEnvironmentVariable("CINECORE_D3D9_KEEP_A2R10") != "1" ? 32 : fmt;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)_origCreateTexture)(dev, w, h, levels, usage, realFmt, pool, tex, bridge ? null : shared);
        if (bridge && hr == 0) *shared = D3D9SharedBridge.Register(dev, *tex, w, h, realFmt);
        if (shared != null || TexLog)
            Out.Line($"info shim: CreateTexture dev#{Id(dev)} {w}x{h} lv={levels} usage=0x{usage:X} fmt={fmt} pool={pool} sharedIn=0x{inHandle:X} sharedOut=0x{(shared == null ? 0 : *shared):X} tex=0x{(tex == null ? 0 : *tex):X} hr=0x{hr:X8}");
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CreateRenderTarget(IntPtr dev, uint w, uint h, int fmt, int ms, uint msq, int lockable, IntPtr* surf, IntPtr* shared)
    { using var __hook = HookTrace.Enter("CreateRenderTarget");
        Out.Tid("CreateRenderTarget");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, int>)_origCreateRenderTarget)(dev, w, h, fmt, ms, msq, lockable, surf, shared);
        if (shared != null) Out.Line($"info shim: CreateRenderTarget dev#{Id(dev)} {w}x{h} fmt={fmt} shared=0x{*shared:X} hr=0x{hr:X8}");
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CreateOffscreen(IntPtr dev, uint w, uint h, int fmt, int pool, IntPtr* surf, IntPtr* shared)
    { using var __hook = HookTrace.Enter("CreateOffscreen");
        Out.Tid("CreateOffscreen");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)_origCreateOffscreen)(dev, w, h, fmt, pool, surf, shared);
        if (shared != null || TexLog) Out.Line($"info shim: CreateOffscreenPlainSurface dev#{Id(dev)} {w}x{h} fmt={fmt} pool={pool} shared=0x{(shared == null ? 0 : *shared):X} hr=0x{hr:X8}");
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int Present(IntPtr dev, IntPtr src, IntPtr dst, IntPtr wnd, IntPtr dirty)
    { using var __hook = HookTrace.Enter("Present");
        Out.Tid("Present");
        Presents.AddOrUpdate(dev, 1, (_, n) => n + 1);
        NotePresent();
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)_origPresent)(dev, src, dst, wnd, dirty);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int PresentEx(IntPtr dev, IntPtr src, IntPtr dst, IntPtr wnd, IntPtr dirty, uint flags)
    { using var __hook = HookTrace.Enter("PresentEx");
        Out.Tid("PresentEx");
        Presents.AddOrUpdate(dev, 1, (_, n) => n + 1);
        NotePresent();
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, uint, int>)_origPresentEx)(dev, src, dst, wnd, dirty, flags);
    }

    // ------------------------------------------------------------------ IDirect3DSwapChain9Ex emulata
    private static readonly Guid IID_SwapChain9Ex = new("91886CAF-1C3D-4D2E-A0AB-3E4C7D8D3303");
    private static readonly Guid IID_SwapChain9Ex_Alt = new("580CA87E-1D3C-4D54-991D-B7D3E3C298CE");
    private static readonly ConcurrentDictionary<Guid, int> MissingInterfaces = new();
    private static IntPtr _exVtbl;

    private static void NotePresent()
    {
        Interlocked.Increment(ref _presentCount);
        Interlocked.Exchange(ref _lastPresentQpc, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int DeviceQueryInterface(IntPtr self, Guid* iid, IntPtr* obj)
    { using var __hook = HookTrace.Enter("DeviceQueryInterface");
        Out.Tid("DeviceQueryInterface");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)_origDeviceQI)(self, iid, obj);
        if (hr != 0 && MissingInterfaces.TryAdd(*iid, 1)) Out.Line($"info shim: device QI mancante {*iid}");
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int SwapChainPresent(IntPtr self, IntPtr src, IntPtr dst, IntPtr wnd, IntPtr dirty, uint flags)
    { using var __hook = HookTrace.Enter("SwapChainPresent");
        Out.Tid("SwapChainPresent");
        NotePresent();
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, uint, int>)_origSwapChainPresent)(self, src, dst, wnd, dirty, flags);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int SwapChainQueryInterface(IntPtr self, Guid* iid, IntPtr* obj)
    { using var __hook = HookTrace.Enter("SwapChainQueryInterface");
        Out.Tid("SwapChainQueryInterface");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)_origSwapChainQI)(self, iid, obj);
        if (hr == 0) return hr;
        if (*iid == IID_SwapChain9Ex || *iid == IID_SwapChain9Ex_Alt)
        {
            *obj = CreateSwapChainEx(self);
            return 0;
        }
        if (MissingInterfaces.TryAdd(*iid, 1)) Out.Line($"info shim: swapchain QI mancante {*iid}");
        return hr;
    }

    // Oggetto COM: [vtbl*][swapchain reale][refcount]
    [StructLayout(LayoutKind.Sequential)] private struct ExObj { public IntPtr Vtbl; public IntPtr Inner; public int Refs; }

    private static IntPtr CreateSwapChainEx(IntPtr inner)
    {
        if (_exVtbl == IntPtr.Zero)
        {
            var v = (IntPtr*)NativeMemory.Alloc(13, (nuint)IntPtr.Size);
            v[0] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, Guid*, IntPtr*, int>)&ExQI;
            v[1] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, uint>)&ExAddRef;
            v[2] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, uint>)&ExRelease;
            v[3] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, IntPtr, IntPtr, IntPtr, IntPtr, uint, int>)&ExPresent;
            v[4] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, IntPtr, int>)&ExGetFrontBufferData;
            v[5] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, uint, int, IntPtr*, int>)&ExGetBackBuffer;
            v[6] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, IntPtr, int>)&ExGetRasterStatus;
            v[7] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, IntPtr, int>)&ExGetDisplayMode;
            v[8] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, IntPtr*, int>)&ExGetDevice;
            v[9] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, IntPtr, int>)&ExGetPresentParameters;
            v[10] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, uint*, int>)&ExGetLastPresentCount;
            v[11] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, PresentStats*, int>)&ExGetPresentStats;
            v[12] = (IntPtr)(delegate* unmanaged[Stdcall]<ExObj*, IntPtr, int*, int>)&ExGetDisplayModeEx;
            _exVtbl = (IntPtr)v;
        }
        var o = (ExObj*)NativeMemory.Alloc((nuint)sizeof(ExObj));
        o->Vtbl = _exVtbl; o->Inner = inner; o->Refs = 1;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(inner, 1))(inner); // AddRef sul reale
        Out.Line("info shim: IDirect3DSwapChain9Ex emulata consegnata");
        return (IntPtr)o;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PresentStats { public uint PresentCount, PresentRefreshCount, SyncRefreshCount; public long SyncQPCTime, SyncGPUTime; }

    private const double RefreshHz = 60.0;
    private static uint RefreshCountAt(long qpc) => (uint)(qpc / (double)System.Diagnostics.Stopwatch.Frequency * RefreshHz);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExQI(ExObj* self, Guid* iid, IntPtr* obj)
    { using var __hook = HookTrace.Enter("ExQI");
        Out.Tid("ExQI");
        if (*iid == IID_SwapChain9Ex || *iid == IID_SwapChain9Ex_Alt || *iid == new Guid("00000000-0000-0000-C000-000000000046") || *iid == new Guid("794950F2-ADFC-458A-905E-10A10B0B503B"))
        { *obj = (IntPtr)self; self->Refs++; return 0; }
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)_origSwapChainQI)(self->Inner, iid, obj);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static uint ExAddRef(ExObj* self) { using var __hook = HookTrace.Enter("ExAddRef"); return (uint)Interlocked.Increment(ref self->Refs); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static uint ExRelease(ExObj* self)
    { using var __hook = HookTrace.Enter("ExRelease");
        Out.Tid("ExRelease");
        int n = Interlocked.Decrement(ref self->Refs);
        if (n == 0) { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(self->Inner, 2))(self->Inner); NativeMemory.Free(self); }
        return (uint)Math.Max(0, n);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExPresent(ExObj* self, IntPtr src, IntPtr dst, IntPtr wnd, IntPtr dirty, uint flags)
    { using var __hook = HookTrace.Enter("ExPresent"); NotePresent(); return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, uint, int>)_origSwapChainPresent)(self->Inner, src, dst, wnd, dirty, flags); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExGetFrontBufferData(ExObj* self, IntPtr surf) { using var __hook = HookTrace.Enter("ExGetFrontBufferData"); return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)V(self->Inner, 4))(self->Inner, surf); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExGetBackBuffer(ExObj* self, uint i, int type, IntPtr* surf) { using var __hook = HookTrace.Enter("ExGetBackBuffer"); return ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr*, int>)V(self->Inner, 5))(self->Inner, i, type, surf); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExGetRasterStatus(ExObj* self, IntPtr rs) { using var __hook = HookTrace.Enter("ExGetRasterStatus"); if (Diag) Count("SC.GetRasterStatus"); return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)V(self->Inner, 6))(self->Inner, rs); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExGetDisplayMode(ExObj* self, IntPtr mode) { using var __hook = HookTrace.Enter("ExGetDisplayMode"); return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)V(self->Inner, 7))(self->Inner, mode); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExGetDevice(ExObj* self, IntPtr* dev) { using var __hook = HookTrace.Enter("ExGetDevice"); return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(self->Inner, 8))(self->Inner, dev); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExGetPresentParameters(ExObj* self, IntPtr pp) { using var __hook = HookTrace.Enter("ExGetPresentParameters"); return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)V(self->Inner, 9))(self->Inner, pp); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExGetLastPresentCount(ExObj* self, uint* count) { using var __hook = HookTrace.Enter("ExGetLastPresentCount"); if (Diag) Count("GetLastPresentCount"); *count = (uint)Interlocked.Read(ref _presentCount); return 0; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExGetPresentStats(ExObj* self, PresentStats* st)
    { using var __hook = HookTrace.Enter("ExGetPresentStats");
        Out.Tid("ExGetPresentStats");
        if (Diag) Count("GetPresentStats");
        // Emulazione: ogni Present e' arrivato allo schermo al vsync successivo (60 Hz).
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastPresentQpc);
        uint sync = RefreshCountAt(now);
        st->PresentCount = (uint)Interlocked.Read(ref _presentCount);
        st->PresentRefreshCount = last == 0 ? sync : RefreshCountAt(last) + 1;
        st->SyncRefreshCount = sync;
        st->SyncQPCTime = (long)(sync / RefreshHz * System.Diagnostics.Stopwatch.Frequency);
        st->SyncGPUTime = 0;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ExGetDisplayModeEx(ExObj* self, IntPtr mode, int* rotation)
        { using var __hook = HookTrace.Enter("ExGetDisplayModeEx"); return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int*, int>)V(_d3d, 19))(_d3d, 0, mode, rotation); }

    // ------------------------------------------------------------------ query evento
    // Il fence OpenGL di wined3d può restare non segnalato. Dai alle query che si completano
    // sotto Wine 11 il tempo di segnalare il lavoro reale prima di emulare il completamento.
    // A 2 ms l'emulazione anticipava il download delle texture nelle scene 4K più pesanti.
    private const double LegacyEventTimeoutMs = 2;
    private static readonly double? EventTimeoutOverrideMs =
        double.TryParse(Environment.GetEnvironmentVariable("CINECORE_D3D9_EVENT_MS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
        && double.IsFinite(v) && v >= 0 ? v : null;
    private static double _eventTimeoutMs = EventTimeoutOverrideMs ?? (D3D9SharedBridge.Pipeline ? 0 : LegacyEventTimeoutMs);

    internal static void SetFrameDuration(long ticks)
    {
        // Con 50/60 fps conserva la soglia storica: la prova 60p non ha beneficiato dei 12 ms.
        // In catena la fine del fotogramma D3D9 non si aspetta: la consegna avviene un fotogramma dopo, gia' pronta.
        double timeout = EventTimeoutOverrideMs ?? (D3D9SharedBridge.Pipeline ? 0 : ticks >= 333_333 ? 12 : LegacyEventTimeoutMs);
        Volatile.Write(ref _eventTimeoutMs, timeout);
        D3D9SharedBridge.SetFrameDurationForBridge(ticks);
        D3D9SharedBridge.SetFrameDurationForFlip(ticks);
        Out.Line($"info shim: soglia query evento {timeout:0.###} ms, durata frame {ticks / 10000.0:0.###} ms");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CreateQuery(IntPtr dev, int type, IntPtr* query)
    { using var __hook = HookTrace.Enter("CreateQuery");
        Out.Tid("CreateQuery");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)_origCreateQuery)(dev, type, query);
        if (hr != 0 || query == null || *query == IntPtr.Zero) return hr;
        QueryStart.TryRemove(*query, out _); // D3D9 può riusare l'indirizzo di una query rilasciata.
        if (Interlocked.Exchange(ref _queryHooked, 1) == 0)
        {
            Patch(*query, 6, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&QueryIssue, out _origQueryIssue);
            Patch(*query, 7, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int>)&QueryGetData, out _origQueryGetData);
            var t = new Thread(() => { while (true) { Thread.Sleep(3000); Out.Line($"info shim: clear aggiunti={ClearsAdded} allargati={ClearsWidened} saltati={ClearsSkipped} osd disegnati={Interlocked.Read(ref _osdDrawn)} osd saltati={Interlocked.Read(ref _osdSkipped)} query evento reali={Interlocked.Read(ref _realEvents)} emulate={Interlocked.Read(ref _fakedEvents)} present={Interlocked.Read(ref _presentCount)} {D3D9SharedBridge.DxgiStats()} | " + string.Join(" ", Calls.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value))); } }) { IsBackground = true };
            t.Start();
        }
        return hr;
    }

    private static void SyncQueryDevice(IntPtr q)
    {
        IntPtr dev;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(q, 3))(q, &dev) != 0 || dev == IntPtr.Zero) return;
        try { D3D9SharedBridge.SyncDevice(dev); }
        catch (Exception ex) { Out.Line("info bridge: sync " + ex.Message); }
        finally { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(dev, 2))(dev); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int QueryIssue(IntPtr q, uint flags)
    { using var __hook = HookTrace.Enter("QueryIssue");
        Out.Tid("QueryIssue");
        if (Diag || (flags & 1) != 0)
        {
            int type = ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(q, 4))(q);
            if (Diag) Count($"Issue(t{type},f{flags})");
            if (type == 8 && (flags & 1) != 0)
            {
                QueryStart[q] = System.Diagnostics.Stopwatch.GetTimestamp(); // D3DISSUE_END
                if (Tl.On) { Spins[q] = 0; Tl.Ev($"d9 Issue q={q:X}"); }
            }
        }
        long tli = Tl.Now;
        int hri = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)_origQueryIssue)(q, flags);
        if (Tl.On && Tl.Ms(tli) >= 0.2) Tl.Ev($"d9 Issue lenta call={Tl.Ms(tli):0.00}");
        return hri;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int QueryGetData(IntPtr q, IntPtr data, uint size, uint flags)
    { using var __hook = HookTrace.Enter("QueryGetData");
        Out.Tid("QueryGetData");
        bool known = QueryStart.TryGetValue(q, out long start);
        // Una query conclusa è immutabile fino alla prossima Issue. Conserva le chiamate
        // che richiedono un flush al driver e quelle con diagnostica attiva.
        if (known && start == long.MaxValue && flags == 0 && !Diag)
        {
            if (data != IntPtr.Zero && size >= 4) *(int*)data = 1;
            return 0;
        }

        if (known && start != long.MaxValue && D3D9SharedBridge.Pipeline && !Diag && Volatile.Read(ref _eventTimeoutMs) <= 0)
        {
            // Completamento immediato, senza interrogare il driver (ogni interrogazione invia un buffer di comandi).
            if (data != IntPtr.Zero && size >= 4) *(int*)data = 1;
            if (QueryStart.TryUpdate(q, long.MaxValue, start))
            {
                Interlocked.Increment(ref _fakedEvents);
                if (Tl.On) Tl.Ev($"d9 QueryDone IMMEDIATA q={q:X}");
                SyncQueryDevice(q);
            }
            return 0;
        }
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int>)_origQueryGetData)(q, data, size, flags);
        if (Tl.On && known && start != long.MaxValue)
        {
            int n = Spins.AddOrUpdate(q, 1, (_, c) => c + 1);
            double w = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (hr == 0) Tl.Ev($"d9 QueryDone reale q={q:X} attesa={w:0.00} giri={n} flags={flags}");
            else if (hr == 1 && w >= Volatile.Read(ref _eventTimeoutMs)) Tl.Ev($"d9 QueryDone EMULATA q={q:X} attesa={w:0.00} giri={n} flags={flags}");
        }
        if (Diag) Count($"GetData(t{((delegate* unmanaged[Stdcall]<IntPtr, int>)V(q, 4))(q)})=0x{hr:X}");
        if (!known) return hr;
        if (start == long.MaxValue) { if (data != IntPtr.Zero && size >= 4) *(int*)data = 1; return 0; } // gia' completata
        if (hr == 0)
        {
            if (QueryStart.TryUpdate(q, long.MaxValue, start))
            {
                Interlocked.Increment(ref _realEvents);
                SyncQueryDevice(q);
            }
            return hr;
        }
        if (hr != 1) return hr;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms < Volatile.Read(ref _eventTimeoutMs)) return hr;
        if (data != IntPtr.Zero && size >= 4) *(int*)data = 1;
        if (QueryStart.TryUpdate(q, long.MaxValue, start))
        {
            Interlocked.Increment(ref _fakedEvents);
            SyncQueryDevice(q);
        }
        return 0;
    }

    // ------------------------------------------------------------------ niente ricarica delle superfici intermedie
    // Il driver OpenGL di Apple (sopra Metal) apre un render pass a ogni cambio di destinazione e, se non c'e' un
    // glClear, ricarica dalla memoria tutto il contenuto precedente della superficie. madVR riscrive per intero le sue
    // superfici intermedie (4K, 16 bit float) senza Clear: ~9 ricariche inutili a fotogramma. Un Clear prima del primo
    // disegno, solo quando il disegno copre comunque tutto (niente blending/scissor, viewport intero, RGBA), diventa
    // "nessun caricamento" nel render pass. CINECORE_D3D9_CLEAR_RT=0 lo spegne.
    // Misurato l'11/11 con il profilo per passaggio: nessun guadagno. Rimisurato il 12/11 con il censimento delle
    // passate Metal (CINECORE_PASS_CENSUS): le riletture scendono da 4,7 a 0,9 GB/s; attivo con la consegna in catena.
    internal static readonly bool ClearRt = D3D9SharedBridge.FastOption("CINECORE_D3D9_CLEAR_RT");
    private static bool _rtFresh;
    private static readonly bool RtDiag = Environment.GetEnvironmentVariable("CINECORE_RT_DIAG") == "1"; private static int _rtDiagLogs;
    internal static long ClearsAdded, ClearsSkipped, ClearsWidened;

    private static void MaybeClearFreshTarget(IntPtr dev)
    {
        if (!ClearRt || !_rtFresh) return;
        _rtFresh = false;
        IntPtr rt = _currentRT;
        if (rt == IntPtr.Zero) return;
        if (D3D9SharedBridge.SurfaceFormat(rt) is int sharedFmt)
        {
            // Diagnostica (CINECORE_RT_DIAG=1): come madVR disegna nelle superfici condivise (zona, miscelazione).
            if (RtDiag && _rtDiagLogs < 40)
            {
                _rtDiagLogs++;
                uint a1 = 0, a2 = 0; uint* vq = stackalloc uint[6]; byte* dq = stackalloc byte[32];
                var grs = (delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)V(dev, 58);
                grs(dev, 27, &a1); grs(dev, 174, &a2);
                ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)V(dev, 48))(dev, vq);
                ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)V(rt, 12))(rt, dq);
                Out.Line($"info rt-diag: primo disegno su condivisa fmt={sharedFmt} rt=0x{rt:X} {*(uint*)(dq + 24)}x{*(uint*)(dq + 28)} viewport={vq[0]},{vq[1]} {vq[2]}x{vq[3]} blend={a1} scissor={a2}");
            }
            return;
        }
        uint v;
        var getRs = (delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)V(dev, 58);
        if (getRs(dev, 27, &v) != 0 || v != 0) { ClearsSkipped++; return; }          // ALPHABLENDENABLE
        if (getRs(dev, 174, &v) != 0 || v != 0) { ClearsSkipped++; return; }         // SCISSORTESTENABLE
        if (getRs(dev, 168, &v) != 0 || (v & 0xF) != 0xF) { ClearsSkipped++; return; } // COLORWRITEENABLE
        byte* desc = stackalloc byte[32];
        if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)V(rt, 12))(rt, desc) != 0) return; // IDirect3DSurface9::GetDesc
        uint w = *(uint*)(desc + 24), h = *(uint*)(desc + 28);
        uint* vp = stackalloc uint[6];
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)V(dev, 48))(dev, vp) != 0) return; // GetViewport
        if (vp[0] != 0 || vp[1] != 0 || vp[2] != w || vp[3] != h) { ClearsSkipped++; return; }
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, uint, float, uint, int>)_origClear)(dev, 0, IntPtr.Zero, 1 /*TARGET*/, 0, 1f, 0);
        ClearsAdded++;
    }


    // ------------------------------------------------------------------ Jinc in riduzione: niente letture a peso zero
    // Lo shader Jinc di madVR (riduzione, es. 4K -> finestra) scorre un quadrato di texel e per ognuno legge la tabella
    // dei pesi e la sorgente, anche quando il texel e' fuori dal raggio 3 e il peso viene poi messo a zero (~21% dei
    // texel). Il blocco "leggi peso, leggi sorgente, accumula" viene racchiuso in un if (distanza < 3): il risultato e'
    // identico (quei termini valevano zero). Misurato 11/11: guadagno entro il rumore (18,5 contro 19,2 ms). Rimisurato il 23/11/2026 con 2 coppie alternate
    // (risparmio energetico, finestra 2588x1456, catena): GL 44,8-45,0 -> 41,1-42,2 ms/fotogramma, fotogrammi 17,4 -> 18,9/s,
    // immagine uguale (differenza massima 1 livello = dithering dinamico). Acceso; CINECORE_JINC_SKIP=0 lo spegne.
    internal static readonly bool JincSkip = Environment.GetEnvironmentVariable("CINECORE_JINC_SKIP") != "0";
    private static IntPtr _origCreatePs;
    private static long _osdDrawn, _osdSkipped;
    private static int _jincPatched;

    private static uint[]? PatchJinc(uint* code)
    {
        if (code == null || code[0] != 0xFFFF0300) return null;
        var starts = new System.Collections.Generic.List<int>(); var ops = new System.Collections.Generic.List<uint>();
        int i = 1;
        for (; i < 4096; )
        {
            uint t = code[i];
            if (t == 0x0000FFFF) break;
            uint op = t & 0xFFFF;
            int len = op == 0xFFFE ? (int)((t >> 16) & 0x7FFF) : (int)((t >> 24) & 0xF);
            starts.Add(i); ops.Add(op);
            i += 1 + len;
        }
        if (i >= 4096) return null;
        int total = i + 1;
        int tex = 0; foreach (uint o in ops) if (o == 0x5F) tex++;
        if (tex != 2) return null;
        for (int k = 0; k + 4 < ops.Count; k++)
        {
            // texldl (peso), cmp (peso = distanza-3 >= 0 ? 0 : tabella), texldl (sorgente), mad, add
            if (ops[k] != 0x5F || ops[k + 1] != 0x58 || ops[k + 2] != 0x5F || ops[k + 3] != 0x04 || ops[k + 4] != 0x02) continue;
            int cmp = starts[k + 1];
            if (((code[cmp] >> 24) & 0xF) != 4) return null;
            uint src0 = code[cmp + 2], src1 = code[cmp + 3];
            // src0: registro temporaneo (tipo 0), src1: costante (tipo 2), entrambi senza modificatori e con un solo canale replicato.
            static bool Replicated(uint tok) { uint sw = (tok >> 16) & 0xFF; uint c = sw & 3; return sw == (c | c << 2 | c << 4 | c << 6) && ((tok >> 24) & 0xF) == 0; }
            static uint RegType(uint tok) => ((tok >> 28) & 7) | ((tok >> 8) & 0x18);
            if (!Replicated(src0) || !Replicated(src1) || RegType(src0) != 0 || RegType(src1) != 2) return null;
            int begin = starts[k], end = k + 5 < starts.Count ? starts[k + 5] : i;
            var res = new uint[total + 4];
            int w = 0;
            for (int r = 0; r < begin; r++) res[w++] = code[r];
            res[w++] = 0x29u | (2u << 24) | (4u << 16); // ifc_lt
            res[w++] = src0; res[w++] = src1;
            for (int r = begin; r < end; r++) res[w++] = code[r];
            res[w++] = 0x2Bu; // endif
            for (int r = end; r < total; r++) res[w++] = code[r];
            return res;
        }
        return null;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CreatePixelShaderHook(IntPtr dev, uint* code, IntPtr* shader)
    { using var __hook = HookTrace.Enter("CreatePixelShader");
        try
        {
            if (PatchJinc(code) is { } patched)
            {
                int hrp;
                fixed (uint* pp = patched) hrp = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr*, int>)_origCreatePs)(dev, pp, shader);
                if (hrp == 0)
                {
                    if (Interlocked.Increment(ref _jincPatched) <= 3) Out.Line("info shim: Jinc in riduzione ottimizzato (letture a peso zero saltate)");
                    return 0;
                }
                Out.Line($"info shim: Jinc ottimizzato rifiutato hr=0x{hrp:X8}, uso l'originale");
            }
        }
        catch (Exception ex) { Out.Line("info shim: Jinc non modificato: " + ex.Message); }
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr*, int>)_origCreatePs)(dev, code, shader);
    }

    // ------------------------------------------------------------------ profilo per passaggio (solo prove)
    // CINECORE_D3D9_PROFILE=1: dopo ogni disegno di madVR aspetta la GPU (query evento nostra) e somma il tempo per
    // destinazione + shader. Rallenta tutto: serve solo a sapere quale passaggio costa.
    internal static readonly bool Profile = Environment.GetEnvironmentVariable("CINECORE_D3D9_PROFILE") == "1";
    private static readonly System.Collections.Generic.Dictionary<string, (long N, double Ms, double Max)> Prof = new();
    private static readonly ConcurrentDictionary<IntPtr, IntPtr> ProfQueries = new();
    private static long _profLast = System.Diagnostics.Stopwatch.GetTimestamp(), _profT0;
    private static string SurfDesc(IntPtr surf)
    {
        if (surf == IntPtr.Zero) return "null";
        byte* desc = stackalloc byte[32];
        if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)V(surf, 12))(surf, desc) != 0) return "?";
        return $"{*(uint*)(desc + 24)}x{*(uint*)(desc + 28)}f{*(int*)desc}";
    }
    private static uint PsSize(IntPtr dev)
    {
        IntPtr ps; uint size = 0;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(dev, 108))(dev, &ps);
        if (ps != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)V(ps, 4))(ps, IntPtr.Zero, &size);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(ps, 2))(ps);
        }
        return size;
    }
    private static readonly ConcurrentDictionary<IntPtr, int> Spins = new();
    private static void ProfBegin() { if (Profile || Tl.On) _profT0 = System.Diagnostics.Stopwatch.GetTimestamp(); }
    private static void ProfEnd(IntPtr dev, string kind, IntPtr src, IntPtr dst)
    {
        if (!Profile || D3D9SharedBridge.InReverse) return;
        if (!ProfQueries.TryGetValue(dev, out IntPtr q))
        {
            if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)(_origCreateQuery != IntPtr.Zero ? _origCreateQuery : V(dev, 118)))(dev, 8, &q) != 0) return;
            ProfQueries[dev] = q;
        }
        var issue = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)(_origQueryIssue != IntPtr.Zero ? _origQueryIssue : V(q, 6));
        var get = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int>)(_origQueryGetData != IntPtr.Zero ? _origQueryGetData : V(q, 7));
        issue(q, 1);
        int done = 0; long tw = System.Diagnostics.Stopwatch.GetTimestamp();
        while (get(q, (IntPtr)(&done), 4, 1) != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(tw).TotalMilliseconds < 400) Thread.SpinWait(200);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(_profT0).TotalMilliseconds;
        string key;
        if (kind == "Stretch") key = $"Stretch {SurfDesc(src)}->{SurfDesc(dst)}";
        else
        {
            IntPtr ps; uint size = 0;
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(dev, 108))(dev, &ps);
            if (ps != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)V(ps, 4))(ps, IntPtr.Zero, &size);
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(ps, 2))(ps);
            }
            uint* vp = stackalloc uint[6];
            ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)V(dev, 48))(dev, vp);
            key = $"{kind} rt={SurfDesc(_currentRT)}{(D3D9SharedBridge.SurfaceFormat(_currentRT) != null ? "(ponte)" : "")} vp={vp[2]}x{vp[3]} ps={size}b";
        }
        lock (Prof)
        {
            Prof.TryGetValue(key, out var e);
            Prof[key] = (e.N + 1, e.Ms + ms, Math.Max(e.Max, ms));
            double sec = System.Diagnostics.Stopwatch.GetElapsedTime(_profLast).TotalSeconds;
            if (sec >= 5)
            {
                _profLast = System.Diagnostics.Stopwatch.GetTimestamp();
                double tot = Prof.Values.Sum(v => v.Ms);
                Out.Line($"prof ---- {sec:0.0}s totale={tot:0}ms ({tot / sec / 10:0.0}% del tempo)");
                foreach (var kv in Prof.OrderByDescending(k => k.Value.Ms))
                    Out.Line($"prof {kv.Value.Ms,8:0.0}ms n={kv.Value.N,5} media={kv.Value.Ms / kv.Value.N,6:0.00}ms max={kv.Value.Max,6:0.0} | {kv.Key}");
                Prof.Clear();
            }
        }
    }

    // ------------------------------------------------------------------ diagnostica: chi scrive le texture condivise
    private static string Kind(IntPtr surface) => D3D9SharedBridge.SurfaceFormat(surface) is int f ? $"ponte{f}" : "altro";

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int SetRenderTarget(IntPtr dev, uint index, IntPtr surface)
    { using var __hook = HookTrace.Enter("SetRenderTarget");
        Out.Tid("SetRenderTarget");
        if (index == 0) { _currentRT = surface; _rtFresh = true; }
        long tls = Tl.Now;
        int hrs = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)_origSetRT)(dev, index, surface);
        if (Tl.On && Tl.Ms(tls) >= 0.2) Tl.Ev($"d9 SetRT lenta call={Tl.Ms(tls):0.00}");
        return hrs;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int StretchRect(IntPtr dev, IntPtr src, IntPtr srcRect, IntPtr dst, IntPtr dstRect, int filter)
    { using var __hook = HookTrace.Enter("StretchRect");
        Out.Tid("StretchRect");
        bool own = D3D9SharedBridge.InReverse;
        if (!own)
        {
            D3D9SharedBridge.BeforeD3D9ReadSurface(src);
            if (dstRect == IntPtr.Zero) D3D9SharedBridge.D3D9Overwrote(dst); // copia su tutta la texture
            else D3D9SharedBridge.BeforeD3D9Draw(dst);
        }
        ProfBegin();
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int>)_origStretch)(dev, src, srcRect, dst, dstRect, filter);
        ProfEnd(dev, "Stretch", src, dst);
        if (Tl.On && !own) Tl.Ev($"d9 Stretch {SurfDesc(src)}->{SurfDesc(dst)} call={Tl.Ms(_profT0):0.00}");
        if (!own) D3D9SharedBridge.MarkWritten(dst);
        if (Diag) Count($"StretchRect {Kind(src)}->{Kind(dst)} hr=0x{hr:X}");
        if (Diag && !own && D3D9SharedBridge.SurfaceFormat(src) == 32 && Interlocked.Increment(ref _diag) < 40) Out.Line($"diag madVR StretchRect {D3D9SharedBridge.Handle9(src)} -> {D3D9SharedBridge.Handle9(dst)} present={Interlocked.Read(ref _presentCount)}");
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int DrawPrimitive(IntPtr dev, int type, uint start, uint count)
    { using var __hook = HookTrace.Enter("DrawPrimitive");
        Out.Tid("DrawPrimitive");
        if (Sampler.On) Sampler.NoteRenderThread();
        D3D9SharedBridge.BeforeD3D9Draw(_currentRT);
        MaybeClearFreshTarget(dev);
        ProfBegin();
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, uint, int>)_origDraw)(dev, type, start, count);
        ProfEnd(dev, "Draw", IntPtr.Zero, IntPtr.Zero);
        if (Tl.On) Tl.Ev($"d9 Draw rt={SurfDesc(_currentRT)}{(D3D9SharedBridge.SurfaceFormat(_currentRT) != null ? "(ponte)" : "")} ps={PsSize(dev)} call={Tl.Ms(_profT0):0.00}");
        D3D9SharedBridge.MarkWritten(_currentRT);
        if (Diag) Count($"Draw->{Kind(_currentRT)} hr=0x{hr:X}");
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int DrawPrimitiveUP(IntPtr dev, int type, uint count, IntPtr data, uint stride)
    { using var __hook = HookTrace.Enter("DrawPrimitiveUP");
        Out.Tid("DrawPrimitiveUP");
        // Vertici gia' in pixel (XYZRHW, come l'OSD di madVR): area esatta del disegno.
        D3D9SharedBridge.Box box = default;
        bool known = false;
        if (_currentRT != IntPtr.Zero && D3D9SharedBridge.SurfaceFormat(_currentRT) is int f9 && f9 != 113)
        {
            uint fvf;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)V(dev, 90))(dev, &fvf);
            uint n = type switch { 1 => count, 2 => count * 2, 3 => count + 1, 4 => count * 3, 5 or 6 => count + 2, _ => 0 };
            if ((fvf & 0xE) == 0x4 && n > 0 && stride >= 16)
            {
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (uint i = 0; i < n; i++)
                {
                    float* v = (float*)((byte*)data + i * stride);
                    if (v[0] < minX) minX = v[0]; if (v[0] > maxX) maxX = v[0];
                    if (v[1] < minY) minY = v[1]; if (v[1] > maxY) maxY = v[1];
                }
                box = new D3D9SharedBridge.Box((int)Math.Floor(minX) - 1, (int)Math.Floor(minY) - 1, (int)Math.Ceiling(maxX) + 1, (int)Math.Ceiling(maxY) + 1);
                known = true;
            }
        }
        if (known)
        {
            // Se il video sotto l'OSD non si riesce a riportare in D3D9, il disegno si salta (sopra uno sfondo vecchio
            // tornerebbe sul video un rettangolo sporco): si conta, cosi' i frame con l'OSD mancante si vedono nei log.
            if (!D3D9SharedBridge.BeforeD3D9DrawRect(_currentRT, box)) { Interlocked.Increment(ref _osdSkipped); return 0; }
            Interlocked.Increment(ref _osdDrawn);
        }
        else D3D9SharedBridge.BeforeD3D9Draw(_currentRT);
        if (Diag && D3D9SharedBridge.SurfaceFormat(_currentRT) == 32 && Interlocked.Increment(ref _diag2) < 12)
        {
            uint fvf; IntPtr vs;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)V(dev, 90))(dev, &fvf);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(dev, 93))(dev, &vs);
            if (vs != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(vs, 2))(vs);
            float* f = (float*)data;
            Out.Line($"diag DrawUP osd type={type} count={count} stride={stride} fvf=0x{fvf:X} vs={(vs != IntPtr.Zero)} v0=({f[0]:0.#},{f[1]:0.#},{f[2]:0.##},{f[3]:0.##}) v1=({((float*)((byte*)data + stride))[0]:0.#},{((float*)((byte*)data + stride))[1]:0.#}) v2=({((float*)((byte*)data + 2 * stride))[0]:0.#},{((float*)((byte*)data + 2 * stride))[1]:0.#})");
        }
        MaybeClearFreshTarget(dev);
        ProfBegin();
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr, uint, int>)_origDrawUP)(dev, type, count, data, stride);
        ProfEnd(dev, "DrawUP", IntPtr.Zero, IntPtr.Zero);
        if (Tl.On) Tl.Ev($"d9 DrawUP rt={SurfDesc(_currentRT)}{(D3D9SharedBridge.SurfaceFormat(_currentRT) != null ? "(ponte)" : "")} call={Tl.Ms(_profT0):0.00}");
        if (known) D3D9SharedBridge.MarkWrittenRect(_currentRT, box); else D3D9SharedBridge.MarkWritten(_currentRT);
        if (Diag) Count($"DrawUP->{Kind(_currentRT)} hr=0x{hr:X}");
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int SetTexture(IntPtr dev, uint stage, IntPtr texture)
    { using var __hook = HookTrace.Enter("SetTexture");
        Out.Tid("SetTexture");
        // madVR in D3D9 legge le texture finali (OSD): prima ci riporta il contenuto disegnato in D3D11.
        if (texture != IntPtr.Zero) D3D9SharedBridge.BeforeD3D9Read(texture);
        if (Diag && texture != IntPtr.Zero) Count($"SetTexture s{stage}<-{D3D9SharedBridge.TextureKind(texture)}");
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)_origSetTexture)(dev, stage, texture);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int DrawIndexedPrimitive(IntPtr dev, int type, int baseVertex, uint minIndex, uint numVertices, uint startIndex, uint count)
    { using var __hook = HookTrace.Enter("DrawIndexedPrimitive");
        Out.Tid("DrawIndexedPrimitive");
        D3D9SharedBridge.BeforeD3D9Draw(_currentRT);
        MaybeClearFreshTarget(dev);
        ProfBegin();
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, uint, uint, uint, uint, int>)_origDrawIndexed)(dev, type, baseVertex, minIndex, numVertices, startIndex, count);
        ProfEnd(dev, "DrawIdx", IntPtr.Zero, IntPtr.Zero);
        D3D9SharedBridge.MarkWritten(_currentRT);
        if (Diag) Count($"DrawIdx->{Kind(_currentRT)} hr=0x{hr:X}");
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int UpdateSurfaceHook(IntPtr dev, IntPtr src, IntPtr srcRect, IntPtr dst, IntPtr dstPoint)
    { using var __hook = HookTrace.Enter("UpdateSurfaceHook");
        Out.Tid("UpdateSurfaceHook");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)_origUpdateSurface)(dev, src, srcRect, dst, dstPoint);
        if (!D3D9SharedBridge.InReverse) D3D9SharedBridge.MarkWritten(dst);
        if (Diag) Count($"UpdateSurface->{Kind(dst)} hr=0x{hr:X}");
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ColorFillHook(IntPtr dev, IntPtr surface, IntPtr rect, uint color)
    { using var __hook = HookTrace.Enter("ColorFillHook");
        Out.Tid("ColorFillHook");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, int>)_origColorFill)(dev, surface, rect, color);
        D3D9SharedBridge.MarkWritten(surface);
        if (Diag) Count($"ColorFill->{Kind(surface)} hr=0x{hr:X}");
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ClearHook(IntPtr dev, uint count, IntPtr rects, uint flags, uint color, float z, uint stencil)
    { using var __hook = HookTrace.Enter("ClearHook");
        Out.Tid("ClearHook");
        // Superficie finale condivisa (16 bit float): madVR pulisce solo le bande nere (due rettangoli) e poi disegna il
        // video nel resto, cioe' la riscrive tutta a ogni fotogramma. Una pulizia a rettangoli pero' diventa in Metal una
        // passata che rilegge l'intera superficie, e cosi' anche il disegno che segue. Pulendo tutto (stesso colore) le
        // bande restano identiche, il video viene comunque ridisegnato sopra e nessuna passata rilegge.
        // Con CINECORE_D3D9_CLEAR_RT=1 (prova); solo colore, niente scissor.
        if (ClearRt && count > 0 && rects != IntPtr.Zero && flags == 1 && D3D9SharedBridge.SurfaceFormat(_currentRT) == 113)
        {
            uint sc = 0;
            if (((delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)V(dev, 58))(dev, 174, &sc) == 0 && sc == 0) { count = 0; rects = IntPtr.Zero; ClearsWidened++; }
        }
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, uint, float, uint, int>)_origClear)(dev, count, rects, flags, color, z, stencil);
        D3D9SharedBridge.MarkWritten(_currentRT);
        if (RtDiag && _rtDiagLogs < 40 && D3D9SharedBridge.SurfaceFormat(_currentRT) is int cf) { _rtDiagLogs++; Out.Line($"info rt-diag: Clear di madVR su condivisa fmt={cf} rt=0x{_currentRT:X} rettangoli={count} flags=0x{flags:X} colore=0x{color:X8}"); }
        if (Diag) Count($"Clear->{Kind(_currentRT)} hr=0x{hr:X}");
        return hr;
    }
}
