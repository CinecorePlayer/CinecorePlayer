#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace CinecoreVideoHost;

/// <summary>
/// Condivisione D3D9 -> D3D11 che sotto Wine manca: il Direct3D 9 di Wine non crea risorse condivise e DXMT le
/// condivide solo tramite D3DKMT, che questa Wine non ha. madVR elabora in D3D9 (wined3d/OpenGL) e presenta in
/// D3D11 (DXMT/Metal) aprendo con OpenSharedResource le texture D3D9 condivise.
///
/// Qui: la texture D3D9 condivisa riceve un handle nostro; l'OpenSharedResource di DXMT (agganciato) crea per
/// quell'handle una texture normale sul dispositivo D3D11 di madVR. Quando madVR ha finito un fotogramma in D3D9
/// (query evento completata) lo leggiamo in un buffer; quando madVR usa la texture in D3D11 (viste shader, copie)
/// la carichiamo dal buffer sullo stesso contesto e thread di madVR.
/// </summary>
internal static unsafe class D3D9SharedBridge
{
    private sealed class AsyncSlot
    {
        public IntPtr Dst9, DstSurf9, Staging, Mem, XMad, ConsumedQuery, SrvMad;
        public ID3D11Texture2D? SharedTexture;
        public volatile int State;
        public long Seq, QueuedAt;
        public double SubmitMs;
        public Pair? Owner;
    }
    private sealed class PboSlot { public IntPtr Tex9, Surf9; public volatile int Busy; public long Seq; }

    private sealed class Pair
    {
        public IntPtr Handle, Device9, Texture9, Surface9, System9;
        public uint Width, Height; public int Format9; public int Dxgi;
        public IntPtr Texture11, Device11;  // texture sul dispositivo di madVR (dopo OpenSharedResource)
        private IntPtr _scratch;
        public int Pitch; public long Version, Uploaded;
        // Le texture caricate direttamente da D3D9 non richiedono un secondo buffer CPU.
        public byte* GetScratch()
        {
            IntPtr scratch = Volatile.Read(ref _scratch);
            if (scratch != IntPtr.Zero) return (byte*)scratch;
            var allocation = (IntPtr)NativeMemory.Alloc((nuint)Pitch * Height);
            scratch = Interlocked.CompareExchange(ref _scratch, allocation, IntPtr.Zero);
            if (scratch == IntPtr.Zero) return (byte*)allocation;
            NativeMemory.Free((void*)allocation);
            return (byte*)scratch;
        }
        public long Reads, Uploads;
        public bool Native;                 // texture DXMT condivisa vera (Wine >= 10.18): madVR la apre da se'
        public long Written;                // disegni D3D9 in questa texture dall'ultima copia
        public bool Pending; public long PendingSince; // download accodato, da caricare al primo uso in D3D11
        public long Seq;                                // numero del fotogramma dell'ultimo download accodato
        public bool HasDownload;
        public readonly object SysLock = new();
        public IntPtr ZcStaging, ZcSurface9, ZcTexture9, ZcContextDevice; public bool ZcTried;
        public bool Opened;                 // aperta da madVR in D3D11
        public IntPtr RevSys9, RevPlain9, RevTex9; public byte* RevMem; // superfici intermedie D3D9 della copia inversa
        public long Reverses;
        public Box Valid9;                  // area in cui la copia D3D9 e' aggiornata rispetto a D3D11
        public long RenderEpoch, FailedReverseEpoch = -1;
        public Box WBox; public bool WFull; // scritture D3D9 dall'ultimo download (area o tutta la texture)
        public Box DlBox;                   // area dell'ultimo download accodato (vuota = tutta la texture)
        public IntPtr DlRT, DlSys; public uint DlW, DlH; // superfici del download per area
        public Box Full => new(0, 0, (int)Width, (int)Height);
        // Percorso PBO: superfici di sistema a rotazione (download asincroni di wined3d) e texture D3D11 a rotazione.
        public PboSlot[]? Slots; public int SlotNext;
        public IntPtr[] X11 = new IntPtr[3], XMad = new IntPtr[3], XHandle = new IntPtr[3]; public IntPtr XStaging, XMadDevice; public int XNext;
        public volatile int ReadyIdx = -1; public long ReadySeq, UsedSeq;
        public volatile AsyncSlot[]? ARing;
        public int ANext; public bool ATried; public long ALastNew, ALastSeq, ALastQueued, ALastUse;
        // Texture 16F non piu' usata da madVR da 2 secondi (ricreata): i suoi posti nell'anello si liberano.
        public bool Alive() => System.Diagnostics.Stopwatch.GetTimestamp() - Math.Max(ALastQueued, ALastUse) < System.Diagnostics.Stopwatch.Frequency * 2;
        public IntPtr PollRt, PollDst; public IntPtr PollMem; public bool PollNeedsContent;
        public Pair? PollOwner;
    }

    internal struct Box
    {
        public int L, T, R, B;
        public Box(int l, int t, int r, int b) { L = l; T = t; R = r; B = b; }
        public readonly bool Empty => R <= L || B <= T;
        public readonly int W => R - L;
        public readonly int H => B - T;
        public readonly bool Contains(Box o) => !Empty && o.L >= L && o.T >= T && o.R <= R && o.B <= B;
        public static Box Union(Box a, Box b) => a.Empty ? b : b.Empty ? a : new(Math.Min(a.L, b.L), Math.Min(a.T, b.T), Math.Max(a.R, b.R), Math.Max(a.B, b.B));
        public readonly Box Clip(Box o) => new(Math.Max(L, o.L), Math.Max(T, o.T), Math.Min(R, o.R), Math.Min(B, o.B));
        public override readonly string ToString() => Empty ? "vuota" : $"{L},{T}-{R},{B}";
    }

    private static readonly object Sync = new();
    private static readonly List<Pair> Pairs = new();
    /// <summary>Un fotogramma 16F e' passato davvero da D3D9 al dispositivo D3D11, anche nel percorso asincrono.</summary>
    internal static bool HasVideoFrame
    {
        get
        {
            lock (Sync)
                return Pairs.Any(p => p.Format9 == 113 && Interlocked.Read(ref p.Uploads) > 0);
        }
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Pair> ByResource11 = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Pair> BySrv = new();
    private static readonly bool NoRead = Environment.GetEnvironmentVariable("CINECORE_BRIDGE_NOREAD") == "1";
    private static readonly bool NoUpload = Environment.GetEnvironmentVariable("CINECORE_BRIDGE_NOUPLOAD") == "1";
    private static readonly object OwnContextSync = new();

    /// <summary>Diagnostica degli stalli (CINECORE_HOOK_TRACE=1): ganci occupati e lock del ponte tenuti.</summary>
    public static string StallDump()
    {
        var locks = new List<(string, object)> { ("Sync", Sync), ("OwnContext", OwnContextSync), ("LastVideo", LastVideoSync), ("Metric", MetricSync), ("ZcRings", ZcRings) };
        if (Monitor.TryEnter(Sync, 0))
        {
            try { foreach (var p in Pairs) { locks.Add(($"SysLock{p.Format9}:{p.Width}x{p.Height}", p.SysLock)); locks.Add(($"Pair{p.Format9}", p)); } }
            finally { Monitor.Exit(Sync); }
        }
        return HookTrace.Dump(300, locks.ToArray()) + $" present={Interlocked.Read(ref _dxgiPresents)}";
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, IntPtr> SrvResource = new();
    private static readonly bool Deferred = Environment.GetEnvironmentVariable("CINECORE_BRIDGE_DEFERRED") != "0";
    // Un fotogramma di ritardo costante: al fotogramma N si carica N-1, gia' scaricato (nessuna attesa della GPU).
    private static readonly bool Lag = Environment.GetEnvironmentVariable("CINECORE_BRIDGE_LAG") != "0";
    internal static bool UsesFrameDelay => Lag && Deferred && !NoRead && !NoUpload
        && Environment.GetEnvironmentVariable("CINECORE_D3D9_BRIDGE") != "0";
    private static long _frameSeq;
    /// <summary>Fotogrammi di ritardo del ponte per la texture video: 1 (copia diretta) o attesa PBO + 1.</summary>
    internal static int FrameDelayFrames => Async && GpuZeroCopy ? 1 : Async ? 2 : Pbo ? PboHold + 1 : 1;
    private static Pair? _lagSource;
    // Lettura asincrona (wined3d Cinecore con PBO per SYSTEMMEM+DYNAMIC): niente memoria condivisa, che costringe
    // wined3d alla copia sincrona.
    private static readonly bool Pbo = Environment.GetEnvironmentVariable("CINECORE_BRIDGE_PBO") == "1";
    private static readonly bool ZeroCopy = !Pbo && Environment.GetEnvironmentVariable("CINECORE_BRIDGE_ZEROCOPY") != "0";

    private static IntPtr ContextDevice(IntPtr ctx)
    {
        IntPtr dev; ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)V(ctx, 3))(ctx, &dev);
        Release(dev); return dev;
    }

    private static void EnsureZeroCopy(IntPtr ctx, Pair p)
    {
        if (p.ZcTried) return;
        p.ZcTried = true;
        try
        {
            IntPtr dev = ContextDevice(ctx);
            var desc = new Tex2DDesc { Width = p.Width, Height = p.Height, MipLevels = 1, ArraySize = 1, Format = p.Dxgi, SampleCount = 1, Usage = 3 /*STAGING*/, BindFlags = 0, CpuAccess = 0x10000 | 0x20000 };
            IntPtr staging;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)V(dev, 5))(dev, &desc, IntPtr.Zero, &staging);
            if (hr != 0) { Out.Line($"info bridge: zero-copy staging hr=0x{hr:X8}"); return; }
            MappedSub m;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSub*, int>)V(ctx, 14))(ctx, staging, 0, 3 /*READ_WRITE*/, 0, &m);
            if (hr != 0) { Out.Line($"info bridge: zero-copy Map hr=0x{hr:X8}"); Release(staging); return; }
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(ctx, 15))(ctx, staging, 0);
            int pitch = (int)p.Width * BytesPerPixel(p.Dxgi);
            if (m.RowPitch != pitch) { Out.Line($"info bridge: zero-copy pitch {m.RowPitch} != {pitch}, disattivato"); Release(staging); return; }
            IntPtr userMem = m.Data, tex9;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(p.Device9, 23))(p.Device9, p.Width, p.Height, 1, 0, p.Format9, 2 /*SYSTEMMEM*/, &tex9, &userMem);
            if (hr != 0) { Out.Line($"info bridge: zero-copy texture D3D9 hr=0x{hr:X8}"); Release(staging); return; }
            IntPtr surf;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(tex9, 18))(tex9, 0, &surf);
            p.ZcStaging = staging; p.ZcTexture9 = tex9; p.ZcSurface9 = surf; p.ZcContextDevice = dev;
            Out.Line($"info bridge: zero-copy attivo per {p.Width}x{p.Height} fmt={p.Format9}");
        }
        catch (Exception ex) { Out.Line("info bridge: zero-copy " + ex.Message); }
    }
    private static readonly bool WorkerUpload = Environment.GetEnvironmentVariable("CINECORE_BRIDGE_WORKER") == "1";
    private static readonly AutoResetEvent _workerSignal = new(false);
    private static Thread? _worker;

    private static void EnsureWorker()
    {
        if (_worker != null) return;
        lock (Sync)
        {
            if (_worker != null) return;
            _worker = new Thread(UploadWorker) { IsBackground = true, Name = "cinecore-bridge-upload", Priority = ThreadPriority.AboveNormal };
            _worker.Start();
        }
    }

    /// <summary>
    /// Thread nostro: attende il download del fotogramma appena accodato (il lock aspetta wined3d, ma non su un thread di
    /// madVR) e lo carica con il nostro contesto in tutte le texture DXMT condivise compatibili: al prossimo uso madVR
    /// trova gia' il fotogramma precedente (ritardo costante di un fotogramma), senza attese sui suoi thread.
    /// </summary>
    private static void UploadWorker()
    {
        Out.Tid("UploadWorker");
        while (true)
        {
            _workerSignal.WaitOne();
            Pair? src = _lagSource;
            if (src == null || OwnContext is not { } own) continue;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (src.SysLock)
            {
                LockedRect lr;
                if (((delegate* unmanaged[Stdcall]<IntPtr, LockedRect*, IntPtr, uint, int>)V(src.System9, 13))(src.System9, &lr, IntPtr.Zero, 0x10) != 0) continue;
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                Pair[] targets; lock (Sync) targets = Pairs.FindAll(o => o.Native && o.Format9 == src.Format9 && o.Width == src.Width && o.Height == src.Height).ToArray();
                lock (OwnContextSync)
                {
                    foreach (var t in targets)
                        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, IntPtr, uint, uint, void>)V(own.NativePointer, 48))(own.NativePointer, t.Texture11, 0, IntPtr.Zero, lr.Bits, (uint)lr.Pitch, 0);
                    own.Flush();
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(src.System9, 14))(src.System9);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp(); double ff = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                lock (MetricSync) { _cCount++; _cSum += (t2 - t0) * ff; if ((t2 - t0) * ff > _cMax) _cMax = (t2 - t0) * ff; _cpySum += (t1 - t0) * ff; }
            }
        }
    } // solo il nostro contesto D3D11: mai preso dai thread di madVR
    private static long _nextHandle = 0x7C100000;
    private static readonly HashSet<int> SkipFormats = new(((Environment.GetEnvironmentVariable("CINECORE_BRIDGE_SKIP") ?? "")).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse));

    private static IntPtr V(IntPtr obj, int i) => (*(IntPtr**)obj)[i];
    private static void Release(IntPtr o) { if (o != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(o, 2))(o); }

    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr addr, nuint size, uint prot, out uint old);
    private static void Patch(IntPtr obj, int index, IntPtr hook, out IntPtr original)
    {
        IntPtr* vtbl = *(IntPtr**)obj;
        original = vtbl[index];
        VirtualProtect((IntPtr)(&vtbl[index]), (nuint)IntPtr.Size, 0x04, out uint old);
        vtbl[index] = hook;
        VirtualProtect((IntPtr)(&vtbl[index]), (nuint)IntPtr.Size, old, out _);
    }

    private static int ToDxgi(int d3dfmt) => d3dfmt switch
    {
        31 or 32 => 24,  // A2R10G10B10 / A2B10G10R10 -> R10G10B10A2_UNORM (31: R e B scambiati nella copia)
        113 => 10,       // A16B16G16R16F -> R16G16B16A16_FLOAT
        116 => 2,        // A32B32G32R32F -> R32G32B32A32_FLOAT
        36 => 11,        // A16B16G16R16 -> R16G16B16A16_UNORM
        21 => 87,        // A8R8G8B8 -> B8G8R8A8_UNORM
        22 => 88,        // X8R8G8B8 -> B8G8R8X8_UNORM
        _ => 0
    };

    private static int BytesPerPixel(int dxgi) => dxgi switch { 10 or 11 => 8, 2 => 16, _ => 4 };

    // ------------------------------------------------------------------ ganci D3D11 (DXMT)
    private static IntPtr _origCreateRtv, _origCreateUav, _origOMSetRT, _origOMSetRTUav, _origCSSetUav;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Pair> ByView11 = new();
    private static IntPtr _origOpenShared, _origOpenShared1, _origCreateSrv, _origPSSetSrv, _origCSSetSrv, _origVSSetSrv, _origCopyRegion, _origCopyResource;
    private static int _installed;
    private static object? _keep;

    // Solo diagnostica (CINECORE_STEP_PAUSE=ms): pausa dopo ogni passo dell'avvio grafico, per vedere da fuori quale
    // passo cambia lo stato del sistema (es. lo schermo che scende a 30 fps in risparmio energetico).
    private static readonly int StepPauseMs = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_STEP_PAUSE"), out int spm) ? spm : 0;
    internal static void StepPause(string step) { if (StepPauseMs <= 0) return; Out.Line("passo: " + step); Thread.Sleep(StepPauseMs); }

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        try
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, new[] { FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null) { Out.Line($"info bridge: D3D11CreateDevice hr=0x{hr.Code:X8}"); return; }
            StepPause("D3D11CreateDevice");
            IntPtr dev = device.NativePointer, ctx = context.NativePointer;
            // Le vtable di DXMT sono statiche nella DLL: valgono anche per il dispositivo che creera' madVR.
            Patch(dev, 28, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)&OpenSharedResource, out _origOpenShared);
            Patch(dev, 45, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)&OpenSharedResource1, out _origOpenShared1);
            Patch(dev, 7, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)&CreateShaderResourceView, out _origCreateSrv);
            Patch(dev, 29, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)&CheckFormatSupport, out _origCheckFmt);
            Patch(dev, 33, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, int>)&CheckFeatureSupport, out _origCheckFeat);
            Patch(ctx, 8, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&PSSetShaderResources, out _origPSSetSrv);
            Patch(ctx, 25, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&VSSetShaderResources, out _origVSSetSrv);
            Patch(ctx, 67, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&CSSetShaderResources, out _origCSSetSrv);
            Patch(ctx, 46, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, void>)&CopySubresourceRegion, out _origCopyRegion);
            Patch(ctx, 47, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)&CopyResource, out _origCopyResource);
            Patch(dev, 9, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)&CreateRenderTargetView, out _origCreateRtv);
            Patch(dev, 8, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)&CreateUnorderedAccessView, out _origCreateUav);
            Patch(ctx, 33, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)&OMSetRenderTargets, out _origOMSetRT);
            Patch(ctx, 34, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, uint, uint, IntPtr*, uint*, void>)&OMSetRenderTargetsAndUavs, out _origOMSetRTUav);
            Patch(ctx, 68, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, void>)&CSSetUnorderedAccessViews, out _origCSSetUav);
            if (Tl.On)
            {
                Patch(ctx, 13, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)&TlDraw, out _origTlDraw);
                Patch(ctx, 41, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)&TlDispatch, out _origTlDispatch);
                Patch(ctx, 29, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, int>)&TlGetData, out _origTlGetData);
                Patch(ctx, 28, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&TlEnd, out _origTlEnd);
                Patch(ctx, 111, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void>)&TlFlush, out _origTlFlush);
            }
            if (!Tl.On && (HookTrace.Count || D11QueryWaitMs > 0))
            {
                // Censimento / attesa vera sulle query D3D11 (vedi QwGetData).
                Patch(ctx, 29, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, int>)&QwGetData, out _origQwGetData);
                Patch(ctx, 28, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&QwEnd, out _origQwEnd);
                Patch(ctx, 111, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void>)&QwFlush, out _origQwFlush);
                if (HookTrace.Count)
                {
                    Patch(ctx, 13, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)&QwDraw, out _origQwDraw);
                    Patch(ctx, 41, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)&QwDispatch, out _origQwDispatch);
                }
                Out.Line($"info d3d11: query agganciate (attesa vera fino a {D11QueryWaitMs:0.##} ms, passo {D11QueryStepMs:0.##} ms)");
                if (HookTrace.Count) new Thread(() =>
                {
                    while (true)
                    {
                        Thread.Sleep(3000);
                        long q = Interlocked.Exchange(ref _qwDone, 0), calls = Interlocked.Exchange(ref _qwCalls, 0), notReady = Interlocked.Exchange(ref _qwNotReady, 0), held = Interlocked.Exchange(ref _qwHeld, 0), us = Interlocked.Exchange(ref _qwWaitUs, 0), mx = Interlocked.Exchange(ref _qwWaitMaxUs, 0), noflush = Interlocked.Exchange(ref _qwNoFlush, 0);
                        Out.Line(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"info d3d11 query: completate={q / 3.0:0}/s GetData={calls / 3.0:0}/s nonPronte={notReady / 3.0:0}/s (senzaFlush={noflush / 3.0:0}/s) trattenute={held / 3.0:0}/s attesaMedia={(q > 0 ? us / 1000.0 / q : 0):0.00} ms max={mx / 1000.0:0.0} ms"));
                    }
                }) { IsBackground = true, Name = "cinecore-d11-query" }.Start();
            }
            HookSwapChain(device);
            StepPause("swapchain di servizio 64x64");
            _keep = (device, context);
            Out.Line("info bridge: ganci D3D11 (DXMT) installati");
        }
        catch (Exception ex) { Out.Line("info bridge: install " + ex.Message); }
    }

    // ------------------------------------------------------------------ lato D3D9
    /// <summary>Dopo la creazione reale della texture D3D9 condivisa: restituisce l'handle da dare a madVR.</summary>
    public static IntPtr Register(IntPtr device9, IntPtr texture9, uint w, uint h, int fmt)
    {
        int dxgi = ToDxgi(fmt);
        if (dxgi == 0) { Out.Line($"info bridge: formato D3D9 {fmt} non gestito"); return IntPtr.Zero; }
        IntPtr surf, sys = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(texture9, 18))(texture9, 0, &surf);
        if (hr != 0) { Out.Line($"info bridge: GetSurfaceLevel hr=0x{hr:X8}"); return IntPtr.Zero; }
        if (Pbo)
        {
            // SYSTEMMEM + DYNAMIC: con il wined3d Cinecore diventa un PBO e GetRenderTargetData non blocca la coda.
            IntPtr st;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(device9, 23))(device9, w, h, 1, 0x200 /*DYNAMIC*/, fmt, 2 /*SYSTEMMEM*/, &st, null);
            if (hr == 0) { ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(st, 18))(st, 0, &sys); Release(st); }
            else sys = IntPtr.Zero;
        }
        else if (!(Arm64Wine && fmt == 113 && Async && GpuZeroCopy && Deferred && Lag))
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(device9, 36))(device9, w, h, fmt, 2 /*SYSTEMMEM*/, &sys, null);
        if (hr != 0) { Out.Line($"info bridge: superficie di sistema hr=0x{hr:X8}"); Release(surf); return IntPtr.Zero; }
        int pitch = (int)w * BytesPerPixel(dxgi);
        var p = new Pair
        {
            Handle = (IntPtr)Interlocked.Add(ref _nextHandle, 4), Device9 = device9, Texture9 = texture9, Surface9 = surf, System9 = sys,
            Width = w, Height = h, Format9 = fmt, Dxgi = dxgi, Pitch = pitch
        };
        if (TryCreateNative(p)) p.Native = true;
        lock (Sync) Pairs.Add(p);
        Out.Line($"info bridge: texture D3D9 condivisa {w}x{h} fmt={fmt} -> handle 0x{p.Handle:X} (DXGI {dxgi}, {(p.Native ? "DXMT nativa" : "emulata")})");
        return p.Handle;
    }

    private static ID3D11Device? OwnDevice => (_keep as (ID3D11Device, ID3D11DeviceContext)?)?.Item1;
    private static ID3D11DeviceContext? OwnContext => (_keep as (ID3D11Device, ID3D11DeviceContext)?)?.Item2;

    /// <summary>Con Wine >= 10.18 DXMT condivide davvero le texture (Metal) fra dispositivi D3D11.</summary>
    private static bool TryCreateNative(Pair p)
    {
        if (Environment.GetEnvironmentVariable("CINECORE_BRIDGE_NATIVE") == "0" || OwnDevice is not { } dev) return false;
        try
        {
            var desc = new Texture2DDescription((Vortice.DXGI.Format)p.Dxgi, p.Width, p.Height, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget,
                ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.Shared);
            var tex = dev.CreateTexture2D(desc);
            using var res = tex.QueryInterface<Vortice.DXGI.IDXGIResource>();
            IntPtr handle = res.SharedHandle;
            if (handle == IntPtr.Zero) { tex.Dispose(); return false; }
            p.Handle = handle;
            p.Texture11 = tex.NativePointer; p.Device11 = dev.NativePointer;
            NativeTextures.Add(tex);
            return true;
        }
        catch (Exception ex) { if (Interlocked.Exchange(ref _nativeFailLogged, 1) == 0) Out.Line("info bridge: condivisione DXMT nativa non disponibile: " + ex.Message.Split('\n')[0]); return false; }
    }
    private static readonly List<ID3D11Texture2D> NativeTextures = new();
    private static int _nativeFailLogged;

    private static int DownloadSystemSurface(Pair p)
    {
        lock (p.SysLock)
        {
            // Le superfici video servite da IOSurface non hanno bisogno della copia CPU di ripiego.
            if (p.System9 == IntPtr.Zero)
            {
                IntPtr sys;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(p.Device9, 36))(
                    p.Device9, p.Width, p.Height, p.Format9, 2 /*SYSTEMMEM*/, &sys, null);
                if (hr != 0) return hr;
                p.System9 = sys;
            }
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)V(p.Device9, 32))(p.Device9, p.Surface9, p.System9);
        }
    }

    /// <summary>madVR ha disegnato in questa superficie D3D9 (render target o destinazione di StretchRect).</summary>
    public static void MarkWritten(IntPtr surface)
    {
        if (surface == IntPtr.Zero) return;
        lock (Sync) foreach (var p in Pairs) if (p.Surface9 == surface) { p.Written++; p.WFull = true; if (p.Format9 != 113) { _flipDirty = 1; _fdOsd++; PqNoteOsd(); } return; }
    }

    /// <summary>D3D9 ha disegnato in un'area nota (vertici in pixel, XYZRHW): basta rileggere quella.</summary>
    public static void MarkWrittenRect(IntPtr surface, Box box)
    {
        if (surface == IntPtr.Zero) return;
        lock (Sync) foreach (var p in Pairs) if (p.Surface9 == surface) { p.Written++; p.WBox = Box.Union(p.WBox, box.Clip(p.Full)); if (p.Format9 != 113) { _flipDirty = 1; _fdOsd++; PqNoteOsd(); } return; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct LockedRect { public int Pitch; public IntPtr Bits; }

    /// <summary>Fotogramma finito in D3D9: copia delle texture condivise di quel dispositivo nei buffer.</summary>
    public static void SyncDevice(IntPtr device9)
    {
        Pair[] list;
        lock (Sync) list = Pairs.ToArray();
        foreach (var p in list)
        {
            if (p.Device9 != device9 || p.Texture11 == IntPtr.Zero) continue; // nessuno l'ha ancora aperta in D3D11
            if (SkipFormats.Contains(p.Format9)) continue;
            lock (Sync) { if (p.Written == 0) continue; p.Written = 0; } // copia solo cio' che D3D9 ha disegnato
            if (NoRead) continue;
            if (p.Native && Deferred && p.Format9 != 31)
            {
                if (UseLag(p) && Async && p.ARing != null) { AsyncQueue(device9, p); continue; }
                if (UseLag(p) && Pbo) { PboQueue(device9, p); continue; }
                if (UseLag(p)) { lock (Sync) { p.Pending = true; p.PendingSince = System.Diagnostics.Stopwatch.GetTimestamp(); p.Seq = ++_frameSeq; } continue; }
                // Solo accodato: il download avviene sul thread dei comandi di wined3d mentre madVR prosegue.
                Box w; bool full;
                lock (Sync) { w = p.WBox; full = p.WFull || w.Empty; p.WBox = default; p.WFull = false; }
                int hrq;
                if (!full && NoOsdDownload && Reversible(p)) continue;
                if (!full && QueueRegionDownload(p, w)) hrq = 0;
                else { lock (p.SysLock) { hrq = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)V(device9, 32))(device9, p.Surface9, p.System9); p.DlBox = default; } }
                if (hrq == 0) { lock (Sync) { p.Pending = true; p.PendingSince = System.Diagnostics.Stopwatch.GetTimestamp(); p.Seq = ++_frameSeq; } }
                continue;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int hr = DownloadSystemSurface(p);
            if (hr != 0) { if (p.Reads == 0) Out.Line($"info bridge: GetRenderTargetData hr=0x{hr:X8}"); continue; }
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            LockedRect lr;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, LockedRect*, IntPtr, uint, int>)V(p.System9, 13))(p.System9, &lr, IntPtr.Zero, 0x10 /*READONLY*/);
            if (hr != 0) continue;
            long tLock = System.Diagnostics.Stopwatch.GetTimestamp();
            bool direct = p.Native && p.Format9 != 31 && OwnContext != null;
            if (NoUpload) { ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(p.System9, 14))(p.System9); direct = false; goto measured; }
            if (direct)
            {
                // Caricamento diretto dalla memoria bloccata di D3D9: nessuna copia intermedia.
                var own = OwnContext!;
                lock (OwnContextSync)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, IntPtr, uint, uint, void>)V(own.NativePointer, 48))(own.NativePointer, p.Texture11, 0, IntPtr.Zero, lr.Bits, (uint)lr.Pitch, 0);
                    own.Flush();
                    p.Version++; p.Uploaded = p.Version; p.Uploads++;
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(p.System9, 14))(p.System9);
            }
            else
            {
                byte* scratch = p.GetScratch();
                lock (Sync)
                {
                    for (uint y = 0; y < p.Height; y++)
                    {
                        byte* src = (byte*)lr.Bits + y * lr.Pitch, dst = scratch + y * p.Pitch;
                        if (p.Format9 == 31)
                        {
                            uint* s = (uint*)src, d = (uint*)dst;
                            for (uint x = 0; x < p.Width; x++) { uint v = s[x]; d[x] = (v & 0xC00FFC00u) | ((v >> 20) & 0x3FFu) | ((v & 0x3FFu) << 20); }
                        }
                        else Buffer.MemoryCopy(src, dst, p.Pitch, p.Pitch);
                    }
                    p.Version++;
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(p.System9, 14))(p.System9);
                if (p.Native && OwnContext is { } own)
                    lock (OwnContextSync)
                    {
                        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, IntPtr, uint, uint, void>)V(own.NativePointer, 48))(own.NativePointer, p.Texture11, 0, IntPtr.Zero, (IntPtr)scratch, (uint)p.Pitch, 0);
                        own.Flush();
                        p.Uploaded = p.Version; p.Uploads++;
                    }
            }
            measured:
            long t2 = tLock;
            double copyMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lock (MetricSync) { _cCount++; _cSum += copyMs; if (copyMs > _cMax) _cMax = copyMs; _rtSum += (t1 - t0) * f; _cpySum += (t2 - t1) * f; }
            p.Reads++;
        }
    }

    /// <summary>
    /// Il ritardo di un fotogramma vale solo per la texture video intermedia (16 bit float), riscritta a ogni fotogramma.
    /// Le texture finali della coda (10 bit) le disegna D3D11; D3D9 le ritocca solo per l'OSD (Ctrl+J, messaggi):
    /// quelle vanno copiate subito, altrimenti l'OSD finisce su un fotogramma vecchio o su un'altra texture.
    /// </summary>
    private static bool UseLag(Pair p) => Lag && p.Format9 == 113;
    private static bool SameShape(Pair a, Pair b) => a.Format9 == b.Format9 && a.Width == b.Width && a.Height == b.Height;
    private static readonly bool ReverseTest = Environment.GetEnvironmentVariable("CINECORE_REVERSE_TEST") == "1";
    // "0" = mai; "osd" = solo l'area dell'OSD di madVR (disegni con vertici in pixel): senza, sul runtime ARM64 le cifre
    // nuove si sovrapponevano alle vecchie (D3D9 disegnava sulla sua copia ferma). Le altre copie inverse restano spente
    // li' perche' potevano bloccare XySubFilter.
    private static readonly string? ReverseMode = Environment.GetEnvironmentVariable("CINECORE_BRIDGE_REVERSE");
    private static readonly bool NoReverse = ReverseMode is "0" or "osd";
    private static readonly bool NoOsdReverse = ReverseMode == "0";

    /// <summary>
    /// D3D9 sta per disegnare in una texture condivisa (SetRenderTarget). Se e' una texture finale che madVR ha gia'
    /// riempito in D3D11, prima si riporta quel contenuto in D3D9: cosi' l'OSD si disegna sopra il fotogramma vero.
    /// </summary>
    // Prima di ogni uso in D3D9 di una texture finale si riporta da D3D11 solo l'area che serve e non e' gia' aggiornata.
    public static void BeforeD3D9Draw(IntPtr surface)
    {
        if (surface == IntPtr.Zero || NoReverse) return;
        Pair? p = FindBySurface(surface);
        if (p != null) EnsureValid(p, p.Full);
    }
    public static void BeforeD3D9ReadSurface(IntPtr surface) => BeforeD3D9Draw(surface);

    public static void BeforeD3D9Read(IntPtr texture9)
    {
        if (NoReverse) return;
        Pair? p = null;
        lock (Sync) foreach (var x in Pairs) if (x.Texture9 == texture9) { p = x; break; }
        if (p != null) EnsureValid(p, p.Full);
    }

    /// <summary>Disegno D3D9 in un'area nota (OSD di madVR): si riporta l'area dell'OSD vista finora piu' questa.</summary>
    public static bool BeforeD3D9DrawRect(IntPtr surface, Box box)
    {
        if (surface == IntPtr.Zero || NoOsdReverse) return true;
        Pair? p = FindBySurface(surface);
        if (p == null || !Reversible(p)) return true;
        box = new Box(box.L - 2, box.T - 2, box.R + 2, box.B + 2).Clip(p.Full);
        Box need;
        lock (Sync)
        {
            if (_osdWidth != p.Width || _osdHeight != p.Height)
            {
                _osdWidth = p.Width; _osdHeight = p.Height;
                _osdArea = default;
            }
            _osdArea = Box.Union(_osdArea, box);
            need = _osdArea;
        }
        return EnsureValid(p, need);
    }

    private static Box _osdArea;
    private static uint _osdWidth, _osdHeight;
    private static Pair? FindBySurface(IntPtr surface)
    {
        lock (Sync) foreach (var x in Pairs) if (x.Surface9 == surface) return x;
        return null;
    }

    public static string TextureKind(IntPtr texture9)
    {
        lock (Sync) foreach (var x in Pairs) if (x.Texture9 == texture9) return $"ponte{x.Format9}";
        return "altro";
    }

    [ThreadStatic] public static bool InReverse;

    private static bool Reversible(Pair p) => p.Native && p.Opened && p.Format9 != 113 && p.Format9 != 31 && p.Texture11 != IntPtr.Zero;

    private static int _evLogs;
    private static bool EnsureValid(Pair p, Box need)
    {
        if (D3D9Shim.Diag && p.Format9 != 113 && _evLogs++ < 30)
            Out.Line($"diag EnsureValid h=0x{p.Handle:X} fmt={p.Format9} native={p.Native} opened={p.Opened} tex11={(p.Texture11 != IntPtr.Zero)} need={need} valid={p.Valid9} own={(OwnDevice != null)}");
        if (!Reversible(p) || need.Empty) return true;
        Box old;
        long epoch;
        lock (Sync)
        {
            if (p.Valid9.Contains(need)) return true;
            epoch = p.RenderEpoch;
            if (p.FailedReverseEpoch == epoch) return false;
            old = p.Valid9;
        }
        Box u = Box.Union(old, need);
        // Le parti gia' valide possono contenere caratteri dell'OSD che non vanno sovrascritti.
        bool copied = old.Empty ? ReverseRect(p, u) :
            (u.T >= old.T || ReverseRect(p, new Box(u.L, u.T, u.R, old.T))) &&
            (u.B <= old.B || ReverseRect(p, new Box(u.L, old.B, u.R, u.B))) &&
            (u.L >= old.L || ReverseRect(p, new Box(u.L, old.T, old.L, old.B))) &&
            (u.R <= old.R || ReverseRect(p, new Box(old.R, old.T, u.R, old.B)));
        lock (Sync)
        {
            if (p.RenderEpoch != epoch) return false;
            if (copied) p.Valid9 = u;
            else p.FailedReverseEpoch = epoch;
        }
        return copied;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect9 { public int L, T, R, B; public Rect9(Box b) { L = b.L; T = b.T; R = b.R; B = b.B; } }
    [StructLayout(LayoutKind.Sequential)] private struct Point9 { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Box11 { public uint L, T, F, R, B, K; public Box11(Box b) { L = (uint)b.L; T = (uint)b.T; F = 0; R = (uint)b.R; B = (uint)b.B; K = 1; } }

    // CINECORE_RGB10_AS8: rgba (predefinito), bgra, 0 = copia grezza (solo per prove).
    // Le texture A2B10G10R10 di wined3d su macOS sono in realta' a 8 bit per canale sulla GPU (i byte passano intatti,
    // la GPU li legge come RGBA8): nella copia inversa si convertono 10 -> 8 bit, nel download 8 -> 10. Il caricamento
    // passa da una superficie intermedia e lo copia D3D9 stesso (StretchRect). Solo con l'OSD di madVR attivo.
    private static readonly string? As8 = Environment.GetEnvironmentVariable("CINECORE_RGB10_AS8") is { } e ? (e == "0" ? null : e) : "rgba";
    private static bool Is8(Pair p) => As8 != null && p.Format9 == 32;
    private static int _revLogs;
    public static string Handle9(IntPtr surface) { lock (Sync) foreach (var x in Pairs) if (x.Surface9 == surface) return $"0x{x.Handle:X}"; return "-"; }

    private static bool ReverseRect(Pair p, Box b)
    {
        if (b.Empty) return true;
        if (OwnDevice is not { } dev || OwnContext is not { } own) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (p.RevSys9 == IntPtr.Zero)
        {
            // Superficie di sistema sulla nostra memoria (D3D9Ex): ci si scrive senza LockRect, che aspetterebbe la
            // coda dei comandi di wined3d (fino a 20 ms nelle scene 4K pesanti). L'ultimo uso di questa superficie
            // risale al giro precedente della coda di madVR (8 fotogrammi), gia' eseguito.
            IntPtr sys, plain, tex;
            IntPtr mem = (IntPtr)NativeMemory.AlignedAlloc((nuint)(p.Width * p.Height * 4), 4096);
            int hrc = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(p.Device9, 23))(p.Device9, p.Width, p.Height, 1, 0, p.Format9, 2 /*SYSTEMMEM*/, &tex, &mem);
            if (hrc != 0) { Out.Line($"info bridge: superficie inversa di sistema hr=0x{hrc:X8}"); NativeMemory.AlignedFree((void*)mem); return false; }
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(tex, 18))(tex, 0, &sys);
            p.RevTex9 = tex; p.RevMem = (byte*)mem;
            hrc = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(p.Device9, 36))(p.Device9, p.Width, p.Height, p.Format9, 0 /*DEFAULT*/, &plain, null);
            if (hrc != 0)
            {
                Out.Line($"info bridge: superficie inversa GPU hr=0x{hrc:X8}");
                Release(sys); Release(tex); NativeMemory.AlignedFree((void*)mem);
                p.RevTex9 = IntPtr.Zero; p.RevMem = null;
                return false;
            }
            p.RevSys9 = sys; p.RevPlain9 = plain;
        }
        var r9 = new Rect9(b);
        long tA = t0, tB = t0, tU = t0;
        lock (OwnContextSync)
        {
            IntPtr c = own.NativePointer;
            IntPtr staging = ReadBack(dev.NativePointer, c, p, b, out MappedSub m, out bool late);
            tA = System.Diagnostics.Stopwatch.GetTimestamp();
            if (staging == IntPtr.Zero) return false;
            if (late) lock (MetricSync) _oLate++;
            try
            {
                LockedRect lr; lr.Pitch = (int)p.Width * 4; lr.Bits = (IntPtr)(p.RevMem + b.T * lr.Pitch + b.L * 4);
                tB = System.Diagnostics.Stopwatch.GetTimestamp();
                bool bgra = As8 == "bgra", conv = Is8(p);
                for (int y = 0; y < b.H; y++)
                {
                    uint* src = (uint*)((byte*)m.Data + (b.T + y) * m.RowPitch) + b.L;
                    uint* d = (uint*)((byte*)lr.Bits + y * lr.Pitch);
                    if (ReverseTest) for (int x = 0; x < b.W; x++) { uint v = (uint)((b.L + x) * 1023 / (int)p.Width); src[x] = v | (v << 10) | (v << 20) | (3u << 30); }
                    if (conv)
                    {
                        // 10 bit -> 8 bit a quattro pixel per volta (sotto emulazione x64 il ciclo scalare costava
                        // ~6 ms per l'area dell'OSD, nel thread di disegno di madVR).
                        int x = 0;
                        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
                        {
                            var m8 = System.Runtime.Intrinsics.Vector128.Create(0xFFu);
                            var m16 = System.Runtime.Intrinsics.Vector128.Create(0xFF00u);
                            var m24 = System.Runtime.Intrinsics.Vector128.Create(0xFF0000u);
                            var k85 = System.Runtime.Intrinsics.Vector128.Create(0x55000000u);
                            for (; x + 4 <= b.W; x += 4)
                            {
                                var v = System.Runtime.Intrinsics.Vector128.Load(src + x);
                                var g = (v >> 4) & m16;
                                var a = (v >> 30) * k85;
                                var lo = bgra ? (v >> 22) & m8 : (v >> 2) & m8;
                                var hi = bgra ? (v << 14) & m24 : (v >> 6) & m24;
                                System.Runtime.Intrinsics.Vector128.Store(lo | g | hi | a, d + x);
                            }
                        }
                        for (; x < b.W; x++)
                        {
                            uint v = src[x];
                            uint r = (v >> 2) & 0xFF, g = (v >> 12) & 0xFF, bl = (v >> 22) & 0xFF, a = (v >> 30) * 85;
                            d[x] = bgra ? bl | (g << 8) | (r << 16) | (a << 24) : r | (g << 8) | (bl << 16) | (a << 24);
                        }
                    }
                    else Buffer.MemoryCopy(src, d, b.W * 4, b.W * 4);
                }
            }
            finally { tU = System.Diagnostics.Stopwatch.GetTimestamp(); ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(c, 15))(c, staging, 0); }
        }
        long tC = System.Diagnostics.Stopwatch.GetTimestamp();
        InReverse = true;
        int hru, hrs;
        try
        {
            var pt = new Point9 { X = b.L, Y = b.T };
            hru = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Rect9*, IntPtr, Point9*, int>)V(p.Device9, 30))(p.Device9, p.RevSys9, &r9, p.RevPlain9, &pt);
            hrs = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Rect9*, IntPtr, Rect9*, int, int>)V(p.Device9, 34))(p.Device9, p.RevPlain9, &r9, p.Surface9, &r9, 0 /*NONE*/);
        }
        finally { InReverse = false; }
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double fq = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        lock (MetricSync) { _oRev++; _oRevMs += ms; _oA += (tA - t0) * fq; _oB += (tB - tA) * fq; _oC += (tU - tB) * fq; _oU += (tC - tU) * fq; _oD += (System.Diagnostics.Stopwatch.GetTimestamp() - tC) * fq; }
        if (p.Reverses++ == 0 || hru != 0 || hrs != 0 || (D3D9Shim.Diag && _revLogs++ < 20))
            Out.Line($"info bridge: copia D3D11->D3D9 (OSD) h=0x{p.Handle:X} area {b} {ms:0.0} ms UpdateSurface=0x{hru:X8} StretchRect=0x{hrs:X8}");
        return hru == 0 && hrs == 0;
    }

    // Lettura D3D11 -> CPU senza bloccare madVR: anello di texture di appoggio. Se il fotogramma richiesto non e' ancora
    // pronto (GPU satura nelle scene 4K pesanti) si usa la stessa area letta al giro precedente: sotto il riquadro
    // semitrasparente dell'OSD il video ha al piu' un fotogramma di ritardo, ma madVR non perde fotogrammi.
    private sealed class Stg { public IntPtr Tex; public Box Box; public long Seq; public bool Done; }
    private static readonly Stg[] Ring = { new(), new(), new(), new() };
    private static int _ringNext; private static long _ringSeq; private static uint _ringW, _ringH; private static int _ringFmt;
    private static readonly double ReadWaitMs = double.TryParse(Environment.GetEnvironmentVariable("CINECORE_OSD_WAIT_MS"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rw) ? rw : 2.0;

    private static IntPtr ReadBack(IntPtr dev, IntPtr c, Pair p, Box b, out MappedSub m, out bool late)
    {
        m = default; late = false;
        if (_ringW != p.Width || _ringH != p.Height || _ringFmt != p.Dxgi)
        {
            // Finestra ridimensionata: madVR ha ricreato le texture, l'anello si rifa' alla nuova misura.
            foreach (var r in Ring) { Release(r.Tex); r.Tex = IntPtr.Zero; r.Seq = 0; r.Box = default; }
            _ringW = p.Width; _ringH = p.Height; _ringFmt = p.Dxgi;
        }
        foreach (var r in Ring)
            if (r.Tex == IntPtr.Zero)
            {
                var desc = new Tex2DDesc { Width = p.Width, Height = p.Height, MipLevels = 1, ArraySize = 1, Format = p.Dxgi, SampleCount = 1, Usage = 3 /*STAGING*/, CpuAccess = 0x20000 /*READ*/ };
                IntPtr st;
                if (((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)V(dev, 5))(dev, &desc, IntPtr.Zero, &st) != 0) { Out.Line("info bridge: anello di lettura non creato"); return IntPtr.Zero; }
                r.Tex = st;
            }
        var cur = Ring[_ringNext]; _ringNext = (_ringNext + 1) % Ring.Length;
        var box = new Box11(b);
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box11*, void>)_origCopyRegion)(c, cur.Tex, 0, (uint)b.L, (uint)b.T, 0, p.Texture11, 0, &box);
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)V(c, 111))(c); // Flush
        cur.Box = b; cur.Seq = ++_ringSeq; cur.Done = false;
        MappedSub mm;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool full = b.L == 0 && b.T == 0 && b.R == (int)p.Width && b.B == (int)p.Height;
        while (true)
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSub*, int>)V(c, 14))(c, cur.Tex, 0, 1 /*READ*/, 0x100000 /*DO_NOT_WAIT*/, &mm);
            if (hr == 0) { cur.Done = true; m = mm; return cur.Tex; }
            if (hr != unchecked((int)0x887A000A)) break;
            if (full || (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= ReadWaitMs)
            {
                // Area gia' letta prima, che copra quella richiesta: si usa quella (gia' pronta) invece di aspettare.
                if (!full)
                {
                    // Evita un Map bloccante mentre la GPU sta ancora preparando il fotogramma.
                    for (int age = 1; age < Ring.Length; age++)
                    {
                        var prev = Ring[(_ringNext + Ring.Length - 1 - age) % Ring.Length];
                        if (prev.Seq <= 0 || !prev.Box.Contains(b)) continue;
                        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSub*, int>)V(c, 14))(c, prev.Tex, 0, 1, 0x100000, &mm) != 0) continue;
                        late = true; m = mm; return prev.Tex;
                    }
                    return IntPtr.Zero;
                }
                break;
            }
            Thread.SpinWait(200);
        }
        if (!full) return IntPtr.Zero;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSub*, int>)V(c, 14))(c, cur.Tex, 0, 1, 0, &mm) != 0) { Out.Line("info bridge: Map inverso fallito"); return IntPtr.Zero; }
        cur.Done = true; m = mm; return cur.Tex;
    }

    // ------------------------------------------------------------------ lettura asincrona (wined3d Cinecore)
    // Fine fotogramma in D3D9: GetRenderTargetData della 16F verso una superficie SYSTEMMEM+DYNAMIC sulla memoria di
    // una staging D3D11 di madVR (anello di 3). Il wined3d Cinecore accoda glReadPixels in un suo PBO e, quando la
    // GPU ha finito, lo fa copiare da un suo thread nella nostra memoria: nessuna attesa ne' per wined3d ne' per il
    // mutex di d3d9. Al primo uso in D3D11 madVR riceve il fotogramma piu' recente gia' pronto (CopyResource, GPU).
    // Predefiniti (servono il wined3d Cinecore: senza i suoi export si torna da soli al percorso classico).
    private static readonly bool Async = Environment.GetEnvironmentVariable("CINECORE_BRIDGE_ASYNC") != "0";
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandleA(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    private static delegate* unmanaged[Cdecl]<IntPtr, uint, int> _dlWait;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _dlExpect, _dlStatus;
    private static long _aQueued, _aUsed, _aStale, _aSkip, _aPolls, _aLagSum, _aLagN, _aWaited;
    private static long _aBusy, _aCatchup, _aFailed;
    private static double _aAgeMs;
    private static readonly object LastVideoSync = new();
    private static IntPtr _lastVideoResource, _lastVideoDevice;
    private static uint _lastVideoWidth, _lastVideoHeight;
    private static int _lastVideoFormat;
    private static long _aHeld;

    private static void RememberVideo(IntPtr ctx, Pair p, IntPtr resource)
    {
        lock (LastVideoSync)
        {
            if (_lastVideoResource == resource) return;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(resource, 1))(resource); // AddRef
            IntPtr old = _lastVideoResource;
            _lastVideoResource = resource; _lastVideoDevice = ContextDevice(ctx);
            _lastVideoWidth = p.Width; _lastVideoHeight = p.Height; _lastVideoFormat = p.Dxgi;
            Release(old);
        }
    }

    private static void HoldVideo(IntPtr ctx, Pair p, IntPtr resource)
    {
        // madVR ricrea le texture anche durante seek/cambio scena. La nuova texture e' vuota finche' la lettura
        // asincrona non e' pronta: prima veniva consegnata cosi', producendo un nero. Mantieni l'ultima immagine
        // valida su una risorsa compatibile, senza aspettare la GPU ne' svuotare la coda del video.
        lock (LastVideoSync)
        {
            if (_lastVideoResource == IntPtr.Zero || _lastVideoWidth != p.Width || _lastVideoHeight != p.Height
                || _lastVideoFormat != p.Dxgi || _lastVideoDevice != ContextDevice(ctx)) return;
            if (_lastVideoResource != resource)
                { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, resource, _lastVideoResource); Interlocked.Increment(ref _cpHold); }
            Interlocked.Increment(ref _aHeld);
        }
    }
    private static volatile uint AsyncWaitMs = 2;
    private static readonly int AsyncWaitOverride = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_ASYNC_WAIT_MS"), out int aw) ? aw : -1;
    private static readonly bool FixedLag = Environment.GetEnvironmentVariable("CINECORE_ZC_LAG") == "1"; // prova: N-1 fisso (peggiore a 60p)
    private static long FrameTicksQpc = System.Diagnostics.Stopwatch.Frequency / 24;
    internal static void SetAsyncFrameDuration(long ticks) { if (ticks > 0) FrameTicksQpc = ticks * System.Diagnostics.Stopwatch.Frequency / 10_000_000; if (AsyncWaitOverride >= 0) AsyncWaitMs = (uint)AsyncWaitOverride; else if (ticks > 0) AsyncWaitMs = (uint)Math.Clamp(ticks / 10000 / 4, 2, 6); }

    // Copia zero (CINECORE_BRIDGE_ZC=1): la 16F viene copiata da wined3d sulla GPU in IOSurface condivise con Metal.
    private static readonly bool GpuZeroCopy = Environment.GetEnvironmentVariable("CINECORE_BRIDGE_ZC") != "0";
    private static readonly bool Arm64Wine = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CINECORE_WINE_ARM64"));
    // Consegna in catena (wined3d Cinecore con CINECORE_PIPELINE=1): il fotogramma N viene avviato senza aspettare la
    // GPU, D3D11 riceve N-1 gia' pronto. madVR non resta fermo fra la parte D3D9 e la parte D3D11 del fotogramma.
    internal static readonly bool Pipeline = Environment.GetEnvironmentVariable("CINECORE_PIPELINE") == "1";
    /// <summary>
    /// Riduzioni misurate il 12/11/2026 insieme alla consegna in catena (madVR pesante in risparmio energetico: da 3
    /// fotogrammi persi al secondo a zero). Sono attive quando lo e' la catena; ogni variabile le forza con 1 o le
    /// spegne con 0.
    /// </summary>
    internal static bool FastOption(string name)
    {
        // 18/11/2026: accese anche fuori dal risparmio energetico (4K HDR pesante a piena potenza: CPU del VideoHost da
        // 95% a 74%, stessa GPU, 0 persi, immagine identica). La catena resta solo in risparmio energetico.
        string? v = Environment.GetEnvironmentVariable(name);
        return v != "0";
    }

    private static void CreatePollPair(Pair p)
    {
        // Coppia 1x1 per far raccogliere a wined3d le letture in sospeso quando madVR non produce fotogrammi (pausa).
        IntPtr pr, pd, prs, pds; IntPtr pmem = (IntPtr)NativeMemory.AlignedAlloc(64, 64); NativeMemory.Clear((void*)pmem, 64);
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(p.Device9, 23))(p.Device9, 1, 1, 1, 0x1, p.Format9, 0, &pr, null) == 0
            && ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(p.Device9, 23))(p.Device9, 1, 1, 1, 0x200, p.Format9, 2, &pd, &pmem) == 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(pr, 18))(pr, 0, &prs);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(pd, 18))(pd, 0, &pds);
            p.PollRt = prs; p.PollDst = pds; p.PollMem = pmem;
            p.PollNeedsContent = true; // va disegnata una volta, altrimenti wined3d scarta la lettura
        }
    }

    // Un solo anello per misura, condiviso dalle texture 16F che madVR ricrea (pausa, ricerca): niente IOSurface nuove
    // a ogni ricreazione. Ogni posto ricorda la texture (Owner) del fotogramma che contiene.
    private static readonly Dictionary<(uint, uint, IntPtr, IntPtr), (AsyncSlot[] Slots, Pair PollOwner)> ZcRings = new();

    private static bool AsyncSetupZeroCopy(IntPtr madDev, Pair p, delegate* unmanaged[Cdecl]<uint, uint, uint> create, ID3D11Device own11)
    {
        lock (ZcRings)
        {
            var key = (p.Width, p.Height, p.Device9, madDev);
            if (ZcRings.TryGetValue(key, out var shared))
            {
                p.PollOwner = shared.PollOwner;
                p.ARing = shared.Slots;
                Out.Line($"info bridge: copia zero, anello esistente riusato per {p.Width}x{p.Height}");
                return true;
            }
            var ring = new AsyncSlot[Pipeline ? 5 : Arm64Wine ? 3 : 6];
            try
            {
                for (int i = 0; i < ring.Length; i++)
                {
                    uint id = create(p.Width, p.Height);
                    if (id == 0) { Out.Line("info bridge: IOSurface non creata"); return false; }
                    var slot = ring[i] = new AsyncSlot();
                    // La prossima texture condivisa RGBA16F di questa misura nasce sulla IOSurface.
                    slot.SharedTexture = own11.CreateTexture2D(new Texture2DDescription((Vortice.DXGI.Format)p.Dxgi,
                        p.Width, p.Height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None,
                        1, 0, ResourceOptionFlags.Shared));
                    using var dxgi = slot.SharedTexture.QueryInterface<Vortice.DXGI.IDXGIResource>();
                    IntPtr mad; Guid iid = IID_Tex2D;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)_origOpenShared)(madDev, dxgi.SharedHandle, &iid, &mad) != 0)
                    { Out.Line("info bridge: madVR non apre la texture IOSurface"); return false; }
                    slot.XMad = mad;

                    // La destinazione D3D9 identifica la IOSurface con la firma 'CCIO' e il suo ID.
                    IntPtr mem = slot.Mem = (IntPtr)NativeMemory.AlignedAlloc((nuint)p.Width * p.Height * 8, 4096);
                    ((uint*)mem)[0] = 0x4f494343;
                    ((uint*)mem)[1] = id;
                    IntPtr tex9, surf9;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(p.Device9, 23))(p.Device9, p.Width, p.Height, 1, 0x200 /*DYNAMIC*/, p.Format9, 2 /*SYSTEMMEM*/, &tex9, &mem) != 0)
                    { Out.Line("info bridge: texture di destinazione non creata"); return false; }
                    slot.Dst9 = tex9;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(tex9, 18))(tex9, 0, &surf9) != 0)
                    { Out.Line("info bridge: superficie di destinazione non creata"); return false; }
                    slot.DstSurf9 = surf9;
                }
                if (!Arm64Wine) CreatePollPair(p);
                ZcRings.Add(key, (ring, p));
                p.ARing = ring;
                Out.Line($"info bridge: copia zero via IOSurface attiva per {p.Width}x{p.Height} ({ring.Length} superfici)");
                return true;
            }
            catch (Exception ex)
            {
                Out.Line($"info bridge: preparazione IOSurface fallita ({ex.GetType().Name})");
                return false;
            }
            finally
            {
                if (p.ARing != ring)
                    foreach (var slot in ring)
                    {
                        if (slot == null) continue;
                        Release(slot.DstSurf9);
                        Release(slot.Dst9);
                        Release(slot.XMad);
                        slot.SharedTexture?.Dispose();
                        NativeMemory.AlignedFree((void*)slot.Mem);
                    }
            }
        }
    }

    private static bool AsyncSetup(IntPtr ctx, Pair p)
    {
        if (p.ATried) return p.ARing != null;
        p.ATried = true;
        IntPtr wined3d = GetModuleHandleA("wined3d.dll");
        IntPtr fn = wined3d == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(wined3d, "cinecore_download_wait");
        if (fn == IntPtr.Zero) { Out.Line("info bridge: wined3d senza lettura asincrona (cinecore_download_wait assente)"); return false; }
        _dlWait = (delegate* unmanaged[Cdecl]<IntPtr, uint, int>)fn;
        _dlExpect = (delegate* unmanaged[Cdecl]<IntPtr, int>)GetProcAddress(wined3d, "cinecore_download_expect");
        _dlStatus = (delegate* unmanaged[Cdecl]<IntPtr, int>)GetProcAddress(wined3d, "cinecore_download_status");
        if (Environment.GetEnvironmentVariable("CINECORE_ASYNC_LEGACY") == "1") { _dlExpect = null; _dlStatus = null; }
        IntPtr dev = ContextDevice(ctx);
        IntPtr fnSurf = GetProcAddress(wined3d, "cinecore_surface_create");
        if (GpuZeroCopy && fnSurf != IntPtr.Zero && OwnDevice is { } own11 && p.Format9 == 113 && AsyncSetupZeroCopy(dev, p, (delegate* unmanaged[Cdecl]<uint, uint, uint>)fnSurf, own11))
            return true;
        if (Arm64Wine) return false;
        var ring = new AsyncSlot[6];
        int pitch = (int)p.Width * BytesPerPixel(p.Dxgi);
        for (int i = 0; i < ring.Length; i++)
        {
            var desc = new Tex2DDesc { Width = p.Width, Height = p.Height, MipLevels = 1, ArraySize = 1, Format = p.Dxgi, SampleCount = 1, Usage = 3 /*STAGING*/, CpuAccess = 0x10000 | 0x20000 };
            IntPtr st;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)V(dev, 5))(dev, &desc, IntPtr.Zero, &st) != 0) { Out.Line("info bridge: asincrono, staging non creata"); return false; }
            MappedSub m;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSub*, int>)V(ctx, 14))(ctx, st, 0, 3, 0, &m) != 0) { Release(st); return false; }
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(ctx, 15))(ctx, st, 0);
            if (m.RowPitch != pitch) { Out.Line($"info bridge: asincrono, pitch {m.RowPitch} != {pitch}"); Release(st); return false; }
            IntPtr mem = m.Data, tex, surf;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(p.Device9, 23))(p.Device9, p.Width, p.Height, 1, 0x200 /*DYNAMIC*/, p.Format9, 2 /*SYSTEMMEM*/, &tex, &mem) != 0)
            { Out.Line("info bridge: asincrono, superficie di sistema non creata"); Release(st); return false; }
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(tex, 18))(tex, 0, &surf);
            ring[i] = new AsyncSlot { Staging = st, Mem = mem, Dst9 = tex, DstSurf9 = surf };
        }
        CreatePollPair(p);
        p.ARing = ring;
        Out.Line($"info bridge: lettura asincrona attiva per {p.Width}x{p.Height} fmt={p.Format9}");
        return true;
    }

    /// <summary>Lettura fittizia 1x1: fa raccogliere a wined3d le letture asincrone finite (pausa, anello pieno).</summary>
    private static void AsyncPoll(IntPtr device9, Pair p)
    {
        p = p.PollOwner ?? p;
        if (p.PollRt == IntPtr.Zero) return;
        if (p.PollNeedsContent)
        {
            InReverse = true;
            try { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int>)V(device9, 34))(device9, p.Surface9, IntPtr.Zero, p.PollRt, IntPtr.Zero, 0); }
            finally { InReverse = false; }
            p.PollNeedsContent = false;
        }
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)V(device9, 32))(device9, p.PollRt, p.PollDst);
        Interlocked.Increment(ref _aPolls);
    }

    private static void AsyncQueue(IntPtr device9, Pair p)
    {
        var ring = p.ARing!;
        AsyncSlot? slot = null; int idx = -1;
        for (int n = 0; n < ring.Length; n++)
        {
            int i = (p.ANext + n) % ring.Length;
            if (Interlocked.CompareExchange(ref ring[i].State, 3, 0) == 0) { slot = ring[i]; idx = i; break; }
        }
        if (slot == null)
        {
            Interlocked.Increment(ref _aSkip);
            Interlocked.Increment(ref _aBusy);
            if (!Arm64Wine) AsyncPoll(device9, p);
            return;
        }
        p.ANext = (idx + 1) % ring.Length;
        slot.QueuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        // In un solo contesto GL la lettura precede i comandi successivi di madVR: si legge la 16F direttamente.
        if (_dlExpect != null && _dlExpect(slot.Mem) == 0) { slot.State = 0; Interlocked.Increment(ref _aFailed); return; }
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)V(device9, 32))(device9, p.Surface9, slot.DstSurf9) != 0) { slot.State = 0; Interlocked.Increment(ref _aFailed); return; }
        slot.SubmitMs = System.Diagnostics.Stopwatch.GetElapsedTime(slot.QueuedAt).TotalMilliseconds;
        slot.Seq = Interlocked.Increment(ref _frameSeq);
        slot.Owner = p;
        p.ALastQueued = System.Diagnostics.Stopwatch.GetTimestamp();
        slot.State = 1;
        lock (Sync) p.Pending = true;
        Interlocked.Increment(ref _aQueued);
        if (Tl.On) Tl.Ev($"br AsyncQueue seq={slot.Seq} invio={slot.SubmitMs:0.00}");
    }

    private static bool AsyncUse(IntPtr ctx, Pair p, IntPtr resource)
    {
        if (!AsyncSetup(ctx, p)) return false;
        var ring = p.ARing!;
        // Un posto torna al produttore soltanto dopo che Metal ha finito di leggerlo. Prima si liberava al
        // successivo uso della stessa texture: cambiando texture i posti restavano occupati o venivano riusati
        // senza una conferma GPU. La query appartiene al contesto D3D11 del consumatore, mai al thread D3D9.
        foreach (var a in ring)
        {
            if (a.State != 2 || a.ConsumedQuery == IntPtr.Zero) continue;
            uint complete = 0;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, uint, int>)V(ctx, 29))(ctx, a.ConsumedQuery, &complete, 4, 1);
            if (hr == 0 && complete != 0) a.State = 0;
        }
        // Ritardo fisso di un fotogramma (copia zero): si consegna N-1, mai l'ultimo accodato, salvo pausa (nessun
        // fotogramma nuovo da 1,5 durate). In ordine; con coda accumulata si recupera saltando avanti.
        var cands = new List<AsyncSlot>(6);
        long maxSeq = 0;
        foreach (var a in ring)
        {
            if (a.State != 1) continue;
            if (a.Owner != p)
            {
                // La texture precedente può essere inattiva mentre OpenGL sta ancora scrivendo nel suo slot.
                if (a.Owner != null && !a.Owner.Alive() && (_dlStatus != null ? _dlStatus(a.Mem) : _dlWait(a.Mem, 0)) != 0)
                    a.State = 0;
                continue;
            }
            if (a.Seq <= p.ALastSeq)
            {
                // Scartato durante il recupero: resta occupato finche' il produttore GPU ha finito di scriverlo.
                if ((_dlStatus != null ? _dlStatus(a.Mem) : _dlWait(a.Mem, 0)) != 0) a.State = 0;
                continue;
            }
            cands.Add(a);
            if (a.Seq > maxSeq) maxSeq = a.Seq;
        }
        cands.Sort((x, y) => x.Seq.CompareTo(y.Seq));
        bool paused = System.Diagnostics.Stopwatch.GetTimestamp() - p.ALastQueued > FrameTicksQpc * 3 / 2;
        if (FixedLag && !paused && cands.Count > 0 && cands[^1].Seq == maxSeq) cands.RemoveAt(cands.Count - 1);
        var ready = cands.FindAll(a =>
        {
            int status = _dlStatus != null ? _dlStatus(a.Mem) : _dlWait(a.Mem, 0);
            if (status < 0) { a.State = 0; Interlocked.Increment(ref _aFailed); }
            return status > 0;
        });
        AsyncSlot? best = null;
        if (ready.Count > 1) { best = ready[^1]; Interlocked.Add(ref _aSkip, ready.Count - 1); Interlocked.Increment(ref _aCatchup); }
        else if (ready.Count > 0) best = ready[0];
        else if (cands.Count > 0 && !Arm64Wine)
        {
            AsyncPoll(p.Device9, p);
            if (_dlWait(cands[0].Mem, AsyncWaitMs) != 0) { best = cands[0]; Interlocked.Increment(ref _aWaited); }
        }
        if (Tl.On) Tl.Ev($"br AsyncUse pronti={ready.Count} candidati={cands.Count} scelto={(best == null ? "NESSUNO" : best.Seq.ToString())}");
        if (best != null)
        {
            // Fotogramma video nuovo in arrivo: la prossima Present non e' una ripetizione. In catena pero' madVR prepara
            // il fotogramma successivo mentre ripete ancora quello corrente: segnalando qui, la ripetizione veniva
            // trattata come fotogramma nuovo (misurato: 29 "nuovi" al secondo per un film a 24), con copie e
            // presentazione doppie e un posto di cadenza occupato a vuoto. Quando madVR consegna copiando la texture
            // finale (sorgente che cambia a ogni fotogramma), il segnale giusto e' quella copia: questo si tace.
            _fdAsync++; _fdSignals++;
            long at = Interlocked.Increment(ref _fdAsyncTot);
            if (at > 400) { Interlocked.Exchange(ref _fdAsyncTot, at / 2); Interlocked.Exchange(ref _fdCopyTot, Interlocked.Read(ref _fdCopyTot) / 2); }
            bool byCopy = FlipDirtyByCopy && at > 24 && Interlocked.Read(ref _fdCopyTot) * 10 >= at * 9;
            if (!byCopy) _flipDirty = 1; else Interlocked.Increment(ref _fdAsyncQuiet);
        }
        if (FlipTrace) Ft($"ponte pronti={ready.Count} candidati={cands.Count} scelto={(best == null ? "NESSUNO" : best.Seq.ToString())} ultimo={p.ALastSeq} ris={resource:X}");
        if (best == null) { Interlocked.Increment(ref _aStale); if (!(SrvSubst && _srvCur != null)) HoldVideo(ctx, p, resource); return true; }
        if (best.ConsumedQuery == IntPtr.Zero)
        {
            // D3D11_QUERY_EVENT: due uint (tipo Event=0, flags=0).
            uint* desc = stackalloc uint[2]; desc[0] = desc[1] = 0;
            IntPtr query;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr*, int>)V(ContextDevice(ctx), 24))(ContextDevice(ctx), desc, &query);
            if (hr != 0) { Out.Line($"error bridge: query completamento Metal hr=0x{hr:X8}"); return false; }
            best.ConsumedQuery = query;
        }
        if (SrvSubst && best.XMad != IntPtr.Zero)
        {
            // Lettura diretta: madVR campionera' questo posto (vedi SubstViews), nessuna copia. Il posto precedente torna
            // libero quando la GPU ha finito il lavoro inviato finora (che poteva ancora leggerlo).
            var prev = _srvCur;
            if (prev != null && prev != best && prev.State == 3)
            {
                if (prev.ConsumedQuery != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)V(ctx, 28))(ctx, prev.ConsumedQuery); prev.State = 2; }
                else prev.State = 0;
            }
            _srvCur = best;
            Interlocked.Increment(ref _srvSubstN);
        }
        else
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, resource, best.XMad != IntPtr.Zero ? best.XMad : best.Staging);
            Interlocked.Increment(ref _cpBridge);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)V(ctx, 28))(ctx, best.ConsumedQuery);
            RememberVideo(ctx, p, resource);
        }
        p.ALastUse = System.Diagnostics.Stopwatch.GetTimestamp();
        p.ALastSeq = best.Seq;
        best.State = SrvSubst && _srvCur == best ? 3 : 2; // 2 = libera dopo la query di completamento GPU; 3 = in lettura diretta da madVR
        p.ALastNew = System.Diagnostics.Stopwatch.GetTimestamp();
        bool newer = false; foreach (var a in ring) if (a.State == 1) newer = true;
        lock (Sync) p.Pending = newer;
        p.Uploads++; p.Reads++;
        long lag = Interlocked.Read(ref _frameSeq) - best.Seq;
        lock (MetricSync)
        {
            _cCount++;
            _cSum += best.SubmitMs;
            _cMax = Math.Max(_cMax, best.SubmitMs);
            _aLagSum += lag;
            _aAgeMs += System.Diagnostics.Stopwatch.GetElapsedTime(best.QueuedAt).TotalMilliseconds;
            _aLagN++;
        }
        Interlocked.Increment(ref _aUsed);
        return true;
    }

    // ------------------------------------------------------------------ percorso PBO (wined3d Cinecore)
    // D3D9: a fine fotogramma GetRenderTargetData in una superficie SYSTEMMEM+DYNAMIC (PBO: la coda di wined3d non si
    // ferma). Thread nostro: blocca la superficie (dati gia' pronti), copia in parallelo in una staging del nostro
    // dispositivo D3D11, CopyResource in una texture condivisa a rotazione e attende la GPU. madVR, al primo uso della
    // texture 16F, fa solo una copia GPU dall'ultima texture pronta.
    private static readonly System.Collections.Concurrent.BlockingCollection<(Pair P, PboSlot S, long Seq)> PboJobs = new(new System.Collections.Concurrent.ConcurrentQueue<(Pair, PboSlot, long)>());
    private static Thread? _pboWorker;
    private static int _pboProbe, _pboUseProbe;
    // Letture tenute in attesa prima di bloccare la superficie: 2 sopra i 40 fps (la GPU e' piu' carica), 1 sotto.
    private static readonly int? PboHoldOverride = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_PBO_HOLD"), out int ph) ? ph : null;
    internal static volatile int PboHold = PboHoldOverride ?? 1;
    internal static void SetFrameDurationForBridge(long ticks) { SetAsyncFrameDuration(ticks); if (PboHoldOverride == null) PboHold = ticks > 0 && ticks < 250_000 ? 2 : 1; }
    private static long _pboLagSum, _pboLagN; private static double _pboLockMs, _pboLockMax;
    private static long _pboSkipped, _pboStale, _pboDone; private static double _pboMs, _pboCopyMs;

    private static void PboQueue(IntPtr device9, Pair p)
    {
        if (p.Slots == null)
        {
            p.Slots = new PboSlot[4];
            for (int i = 0; i < 4; i++)
            {
                IntPtr tex, surf;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(device9, 23))(device9, p.Width, p.Height, 1, 0x200 /*DYNAMIC*/, p.Format9, 2 /*SYSTEMMEM*/, &tex, null) != 0)
                { Out.Line("info bridge: superficie PBO non creata"); p.Slots = null; return; }
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(tex, 18))(tex, 0, &surf);
                p.Slots[i] = new PboSlot { Tex9 = tex, Surf9 = surf };
            }
            Out.Line($"info bridge: percorso PBO attivo per {p.Width}x{p.Height} fmt={p.Format9}");
        }
        var slot = p.Slots[p.SlotNext];
        if (Interlocked.CompareExchange(ref slot.Busy, 1, 0) != 0) { Interlocked.Increment(ref _pboSkipped); return; } // il thread e' indietro
        p.SlotNext = (p.SlotNext + 1) % p.Slots.Length;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)V(device9, 32))(device9, p.Surface9, slot.Surf9) != 0) { slot.Busy = 0; return; }
        slot.Seq = Interlocked.Increment(ref _frameSeq);
        if (_pboWorker == null)
            lock (Sync)
                if (_pboWorker == null) { _pboWorker = new Thread(PboWork) { IsBackground = true, Name = "cinecore-pbo", Priority = ThreadPriority.AboveNormal }; _pboWorker.Start(); }
        PboJobs.Add((p, slot, slot.Seq));
    }

    private static void PboWork()
    {
        IntPtr query = IntPtr.Zero;
        // Si elabora una lettura solo quando arriva la successiva (o dopo 40 ms): bloccare subito la superficie
        // farebbe eseguire a wined3d glMapBuffer mentre la GPU sta ancora leggendo, fermando di nuovo la sua coda.
        var held = new Queue<(Pair, PboSlot, long)>();
        while (true)
        {
            bool got = PboJobs.TryTake(out var next, 40);
            if (got) held.Enqueue(next);
            if (held.Count == 0 || (got && held.Count <= PboHold)) continue;
            var (p, slot, seq) = held.Dequeue();
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (OwnDevice is not { } dev || OwnContext is not { } own) continue;
                IntPtr d = dev.NativePointer, c = own.NativePointer;
                if (p.XStaging == IntPtr.Zero)
                {
                    var sd = new Tex2DDesc { Width = p.Width, Height = p.Height, MipLevels = 1, ArraySize = 1, Format = p.Dxgi, SampleCount = 1, Usage = 3 /*STAGING*/, CpuAccess = 0x10000 /*WRITE*/ };
                    IntPtr st;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)V(d, 5))(d, &sd, IntPtr.Zero, &st) != 0) { Out.Line("info bridge: staging PBO non creata"); continue; }
                    p.XStaging = st;
                    for (int i = 0; i < 3; i++)
                    {
                        var tex = dev.CreateTexture2D(new Texture2DDescription((Vortice.DXGI.Format)p.Dxgi, p.Width, p.Height, 1, 1, BindFlags.ShaderResource,
                            ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.Shared));
                        NativeTextures.Add(tex);
                        p.X11[i] = tex.NativePointer;
                        using (var dr = tex.QueryInterface<Vortice.DXGI.IDXGIResource>()) p.XHandle[i] = dr.SharedHandle;
                    }
                    var qd = stackalloc uint[2]; qd[0] = 0 /*EVENT*/; qd[1] = 0;
                    IntPtr q; if (((delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr*, int>)V(d, 24))(d, qd, &q) == 0) query = q;
                }
                LockedRect lr;
                long tl = System.Diagnostics.Stopwatch.GetTimestamp();
                if (((delegate* unmanaged[Stdcall]<IntPtr, LockedRect*, IntPtr, uint, int>)V(slot.Surf9, 13))(slot.Surf9, &lr, IntPtr.Zero, 0x10) != 0) continue;
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                double lockMs = (t1 - tl) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                lock (MetricSync) { _pboLockMs += lockMs; if (lockMs > _pboLockMax) _pboLockMax = lockMs; }
                if (_pboProbe++ < 4)
                {
                    ulong sum = 0; byte* b0 = (byte*)lr.Bits; long tot = (long)lr.Pitch * p.Height;
                    for (long o = 0; o < tot; o += tot / 997) sum += b0[o];
                    Out.Line($"diag pbo: job {seq} pitch={lr.Pitch} media byte={sum / 997.0:0.0}");
                }
                int k = p.XNext; p.XNext = (p.XNext + 1) % 3;
                lock (OwnContextSync)
                {
                    MappedSub m;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSub*, int>)V(c, 14))(c, p.XStaging, 0, 2 /*WRITE*/, 0, &m) == 0)
                    {
                        byte* src = (byte*)lr.Bits, dst = (byte*)m.Data; int sp = lr.Pitch; uint dp = m.RowPitch; int row = (int)p.Width * BytesPerPixel(p.Dxgi); int h = (int)p.Height;
                        // Copia a strisce su piu' core: un solo core sotto Rosetta arriva a pochi GB/s.
                        System.Threading.Tasks.Parallel.For(0, 4, part =>
                        {
                            int y0 = h * part / 4, y1 = h * (part + 1) / 4;
                            for (int y = y0; y < y1; y++) Buffer.MemoryCopy(src + (long)y * sp, dst + (long)y * dp, row, row);
                        });
                        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(c, 15))(c, p.XStaging, 0);
                    }
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(c, p.X11[k], p.XStaging);
                    if (query != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)V(c, 28))(c, query); // End
                    ((delegate* unmanaged[Stdcall]<IntPtr, void>)V(c, 111))(c);
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(slot.Surf9, 14))(slot.Surf9);
                slot.Busy = 0;
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                // Attesa della GPU sul nostro contesto: la texture k e' completa prima di passarla a madVR.
                if (query != IntPtr.Zero)
                    while (true)
                    {
                        int done = 0; int hr;
                        lock (OwnContextSync) hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, uint, uint, int>)V(c, 29))(c, query, &done, 4, 0);
                        if (hr == 0 && done != 0) break;
                        if (hr < 0) break;
                        Thread.Sleep(1); // attesa vera: Sleep(0) sotto Wine gira a vuoto e consuma un core
                    }
                lock (p) { p.ReadyIdx = k; p.ReadySeq = seq; }
                lock (Sync) p.Pending = true; // al prossimo uso in D3D11 madVR riceve questo fotogramma
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                lock (MetricSync) { _pboDone++; _pboMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * f; _pboCopyMs += (t2 - t1) * f; }
            }
            catch (Exception ex) { slot.Busy = 0; Out.Line("info bridge: PBO " + ex.Message); }
        }
    }

    private static void PboUse(IntPtr ctx, Pair p, IntPtr resource)
    {
        int k; long seq;
        lock (p) { k = p.ReadyIdx; seq = p.ReadySeq; }
        if (k < 0) return;
        if (seq == p.UsedSeq) { Interlocked.Increment(ref _pboStale); return; } // nessun fotogramma nuovo pronto
        IntPtr dev = ContextDevice(ctx);
        if (p.XMadDevice != dev)
        {
            for (int i = 0; i < 3; i++)
            {
                Release(p.XMad[i]); p.XMad[i] = IntPtr.Zero;
                IntPtr res; Guid iid = IID_Tex2D;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)_origOpenShared)(dev, p.XHandle[i], &iid, &res) == 0) p.XMad[i] = res;
                else Out.Line("info bridge: madVR non apre la texture PBO");
            }
            p.XMadDevice = dev;
        }
        if (p.XMad[k] == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, resource, p.XMad[k]);
        if (_pboUseProbe++ < 3) Out.Line($"diag pbo: uso k={k} seq={seq} madVR={GreenAvg16(dev, ctx, p.XMad[k])} nostro={GreenAvg16Own(p.X11[k])} destinazione={GreenAvg16(dev, ctx, resource)}");
        p.UsedSeq = seq; p.Uploads++; p.Reads++;
        lock (Sync) if (p.ReadySeq == seq) p.Pending = false;
        long lag = Interlocked.Read(ref _frameSeq) - seq;
        lock (MetricSync) { _cCount++; _pboLagSum += lag; _pboLagN++; }
    }

    /// <summary>Download di sola un'area: StretchRect in una superficie piccola, poi GetRenderTargetData di quella.</summary>
    private static readonly bool NoOsdDownload = Environment.GetEnvironmentVariable("CINECORE_OSD_NODL") == "1"; // solo prove
    private static long _regionLogs;
    private static bool QueueRegionDownload(Pair p, Box w)
    {
        if (!Reversible(p)) return false;
        if (Interlocked.Increment(ref _regionLogs) is <= 8 or 200 or 1000) Out.Line($"diag area OSD {w} ({w.W}x{w.H}) su {p.Width}x{p.Height} fmt={p.Format9}");
        if (p.DlRT == IntPtr.Zero || p.DlW < w.W || p.DlH < w.H)
        {
            Release(p.DlRT); Release(p.DlSys); p.DlRT = p.DlSys = IntPtr.Zero;
            uint dw = (uint)Math.Max(w.W, (int)Math.Min(p.Width, 512u)), dh = (uint)Math.Max(w.H, (int)Math.Min(p.Height, 768u));
            IntPtr rt, sys;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, int>)V(p.Device9, 28))(p.Device9, dw, dh, p.Format9, 0, 0, 0, &rt, null) != 0) return false;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(p.Device9, 36))(p.Device9, dw, dh, p.Format9, 2 /*SYSTEMMEM*/, &sys, null) != 0) { Release(rt); return false; }
            p.DlRT = rt; p.DlSys = sys; p.DlW = dw; p.DlH = dh;
        }
        var src = new Rect9(w); var dst = new Rect9(new Box(0, 0, w.W, w.H));
        int hr;
        InReverse = true;
        try { hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Rect9*, IntPtr, Rect9*, int, int>)V(p.Device9, 34))(p.Device9, p.Surface9, &src, p.DlRT, &dst, 0); }
        finally { InReverse = false; }
        if (hr != 0) return false;
        lock (p.SysLock)
        {
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)V(p.Device9, 32))(p.Device9, p.DlRT, p.DlSys) != 0) return false;
            p.DlBox = w;
        }
        return true;
    }

    // ------------------------------------------------------------------ scritture D3D11 nelle texture condivise
    // Serve a sapere quale versione e' piu' recente: se D3D11 ha scritto dopo l'ultima sincronizzazione, prima che
    // D3D9 la usi va riportata in D3D9; se no la copia D3D9 e' gia' quella buona (e ricopiarla la cancellerebbe).
    private static void NoteD3D11Write(IntPtr view, string how)
    {
        if (view == IntPtr.Zero || !ByView11.TryGetValue(view, out var p)) return;
        lock (Sync) { p.Valid9 = default; p.RenderEpoch++; }
        if (D3D9Shim.Diag && Interlocked.Read(ref _dxgiPresents) >= DiagFrom && _w11Logs++ < 80) Out.Line($"diag D3D11 scrive h=0x{p.Handle:X} via {how} present={Interlocked.Read(ref _dxgiPresents)}");
    }
    private static void NoteD3D11WriteResource(IntPtr res, string how)
    {
        if (res == IntPtr.Zero || !ByResource11.TryGetValue(res, out var p)) return;
        lock (Sync) { p.Valid9 = default; p.RenderEpoch++; }
        if (D3D9Shim.Diag && Interlocked.Read(ref _dxgiPresents) >= DiagFrom && _w11Logs++ < 80) Out.Line($"diag D3D11 scrive h=0x{p.Handle:X} via {how} present={Interlocked.Read(ref _dxgiPresents)}");
    }
    private static int _w11Logs, _probeLogs;

    /// <summary>
    /// madVR riempie le texture finali con CopyResource e non sempre invia subito i comandi (in pausa mai): il nostro
    /// dispositivo le leggerebbe ancora vuote. Dopo una copia in una texture condivisa si inviano i comandi alla GPU.
    /// </summary>
    private static void FlushIfShared(IntPtr ctx, IntPtr dst)
    {
        if (NoOsdReverse || ByResource11.IsEmpty || !ByResource11.TryGetValue(dst, out var p) || p.Format9 == 113) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)V(ctx, 111))(ctx);
    }

    /// <summary>D3D9 ha sovrascritto tutta la texture: la versione D3D11 precedente non serve piu'.</summary>
    public static void D3D9Overwrote(IntPtr surface)
    {
        if (surface == IntPtr.Zero) return;
        lock (Sync) foreach (var p in Pairs) if (p.Surface9 == surface) { p.Valid9 = p.Full; return; }
    }
    private static string GreenAvg16Own(IntPtr tex) { lock (OwnContextSync) return GreenAvg16(OwnDevice!.NativePointer, OwnContext!.NativePointer, tex); }
    private static string GreenAvg16(IntPtr dev, IntPtr ctx, IntPtr tex)
    {
        IntPtr staging = IntPtr.Zero;
        try
        {
            Tex2DDesc d; ((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, void>)V(tex, 10))(tex, &d);
            var sd = d; sd.Usage = 3; sd.BindFlags = 0; sd.CpuAccess = 0x20000; sd.MiscFlags = 0;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)V(dev, 5))(dev, &sd, IntPtr.Zero, &staging) != 0) return "staging?";
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, staging, tex);
            MappedSub m;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSub*, int>)V(ctx, 14))(ctx, staging, 0, 1, 0, &m) != 0) return "map?";
            ulong sum = 0; long tot = (long)m.RowPitch * d.Height;
            for (long o = 0; o < tot; o += tot / 997) sum += ((byte*)m.Data)[o];
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(ctx, 15))(ctx, staging, 0);
            return (sum / 997.0).ToString("0.0");
        }
        finally { Release(staging); }
    }
    private static string GreenAvgOwn(Pair p) { lock (OwnContextSync) return GreenAvg(OwnDevice!.NativePointer, OwnContext!.NativePointer, p.Texture11); }
    private static string GreenAvg(IntPtr dev, IntPtr ctx, IntPtr tex)
    {
        IntPtr staging = IntPtr.Zero;
        try
        {
            Tex2DDesc d; ((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, void>)V(tex, 10))(tex, &d);
            if (d.Format != 24) return $"fmt{d.Format}";
            var sd = d; sd.Usage = 3; sd.BindFlags = 0; sd.CpuAccess = 0x20000; sd.MiscFlags = 0;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)V(dev, 5))(dev, &sd, IntPtr.Zero, &staging) != 0) return "staging?";
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, staging, tex);
            MappedSub m;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSub*, int>)V(ctx, 14))(ctx, staging, 0, 1, 0, &m) != 0) return "map?";
            ulong sum = 0; int n = 0;
            for (uint y = d.Height / 8; y < d.Height; y += d.Height / 8)
                for (uint x = d.Width / 8; x < d.Width; x += d.Width / 8) { sum += (((uint*)((byte*)m.Data + y * m.RowPitch))[x] >> 10) & 0x3FF; n++; }
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(ctx, 15))(ctx, staging, 0);
            return (sum / (ulong)n).ToString();
        }
        finally { Release(staging); }
    }
    private static readonly long DiagFrom = long.TryParse(Environment.GetEnvironmentVariable("CINECORE_DIAG_FROM"), out long df) ? df : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CreateRenderTargetView(IntPtr device, IntPtr resource, IntPtr desc, IntPtr* view)
    { using var __hook = HookTrace.Enter("CreateRenderTargetView");
        Out.Tid("CreateRenderTargetView");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)_origCreateRtv)(device, resource, desc, view);
        if (hr == 0 && view != null && ByResource11.TryGetValue(resource, out var p)) ByView11[*view] = p;
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CreateUnorderedAccessView(IntPtr device, IntPtr resource, IntPtr desc, IntPtr* view)
    { using var __hook = HookTrace.Enter("CreateUnorderedAccessView");
        Out.Tid("CreateUnorderedAccessView");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)_origCreateUav)(device, resource, desc, view);
        if (hr == 0 && view != null && ByResource11.TryGetValue(resource, out var p)) ByView11[*view] = p;
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void OMSetRenderTargets(IntPtr ctx, uint count, IntPtr* views, IntPtr dsv)
    { using var __hook = HookTrace.Enter("OMSetRenderTargets");
        Out.Tid("OMSetRenderTargets");
        if (views != null && !ByView11.IsEmpty) for (uint i = 0; i < count; i++) NoteD3D11Write(views[i], "RTV");
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)_origOMSetRT)(ctx, count, views, dsv);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void OMSetRenderTargetsAndUavs(IntPtr ctx, uint count, IntPtr* views, IntPtr dsv, uint uavStart, uint uavCount, IntPtr* uavs, uint* initial)
    { using var __hook = HookTrace.Enter("OMSetRenderTargetsAndUavs");
        Out.Tid("OMSetRenderTargetsAndUavs");
        if (!ByView11.IsEmpty)
        {
            if (views != null && count != 0xFFFFFFFF) for (uint i = 0; i < count; i++) NoteD3D11Write(views[i], "RTV");
            if (uavs != null && uavCount != 0xFFFFFFFF) for (uint i = 0; i < uavCount; i++) NoteD3D11Write(uavs[i], "UAV");
        }
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, uint, uint, IntPtr*, uint*, void>)_origOMSetRTUav)(ctx, count, views, dsv, uavStart, uavCount, uavs, initial);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void CSSetUnorderedAccessViews(IntPtr ctx, uint start, uint count, IntPtr* uavs, uint* initial)
    { using var __hook = HookTrace.Enter("CSSetUnorderedAccessViews");
        Out.Tid("CSSetUnorderedAccessViews");
        if (uavs != null && !ByView11.IsEmpty) for (uint i = 0; i < count; i++) NoteD3D11Write(uavs[i], "CS-UAV");
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, void>)_origCSSetUav)(ctx, start, count, uavs, initial);
    }

    // ------------------------------------------------------------------ lato D3D11 (dispositivo di madVR)
    [StructLayout(LayoutKind.Sequential)]
    private struct Tex2DDesc { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality; public int Usage; public uint BindFlags, CpuAccess, MiscFlags; }

    private static readonly bool OpenCacheOn = Environment.GetEnvironmentVariable("CINECORE_OPEN_CACHE") != "0";
    private static readonly Dictionary<(IntPtr, IntPtr, Guid), IntPtr> OpenCache = new();
    private static readonly Queue<(IntPtr, IntPtr, Guid)> OpenCacheOrder = new();
    private static long _openCacheHits;
    private static int Open(IntPtr device, IntPtr handle, Guid* riid, IntPtr* resource, IntPtr original)
    {
        Pair? p;
        lock (Sync) p = Pairs.Find(x => x.Handle == handle);
        if (p != null && p.Native)
        {
            // Texture DXMT condivisa vera: la apre DXMT; ci annotiamo la risorsa di madVR per caricarla al momento giusto.
            // madVR riapre lo stesso handle a ogni Present (40 volte al secondo) e sotto Wine ogni apertura costa ~6 ms
            // dentro il suo thread di disegno: la risorsa aperta si tiene da parte e si riconsegna (stessa texture).
            // CINECORE_OPEN_CACHE=0 lo spegne.
            if (OpenCacheOn && resource != null && riid != null)
            {
                var key = (device, handle, *riid);
                lock (OpenCache)
                    if (OpenCache.TryGetValue(key, out IntPtr kept))
                    {
                        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(kept, 1))(kept); // AddRef per madVR
                        *resource = kept; _openCacheHits++;
                        return 0;
                    }
            }
            int hrn = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)original)(device, handle, riid, resource);
            if (hrn == 0 && resource != null && *resource != IntPtr.Zero)
            {
                ByResource11[*resource] = p; p.Opened = true;
                if (OpenCacheOn && riid != null)
                    lock (OpenCache)
                    {
                        var key = (device, handle, *riid);
                        if (!OpenCache.ContainsKey(key))
                        {
                            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(*resource, 1))(*resource); // la nostra referenza
                            OpenCache[key] = *resource; OpenCacheOrder.Enqueue(key);
                            // Le texture vecchie (cambio di misura) non vengono piu' riaperte: se ne tengono poche.
                            while (OpenCacheOrder.Count > 12)
                            {
                                var old = OpenCacheOrder.Dequeue();
                                if (OpenCache.Remove(old, out IntPtr dead)) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(dead, 2))(dead);
                            }
                            if (OpenCache.Count <= 12) Out.Line($"info bridge: handle 0x{handle:X} aperto in D3D11 e tenuto da parte ({OpenCache.Count})");
                        }
                    }
            }
            return hrn;
        }
        if (p == null) return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)original)(device, handle, riid, resource);
        if (p.Texture11 != IntPtr.Zero && p.Device11 == device)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(p.Texture11, 0))(p.Texture11, riid, resource); // gia' aperta: stessa texture
        var desc = new Tex2DDesc { Width = p.Width, Height = p.Height, MipLevels = 1, ArraySize = 1, Format = p.Dxgi, SampleCount = 1, Usage = 0, BindFlags = 0x8 | 0x20 /*SRV|RTV*/ };
        IntPtr tex;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)V(device, 5))(device, &desc, IntPtr.Zero, &tex);
        if (hr != 0) { Out.Line($"info bridge: CreateTexture2D per madVR hr=0x{hr:X8}"); return hr; }
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(tex, 0))(tex, riid, resource);
        lock (Sync)
        {
            p.Texture11 = tex; p.Device11 = device; // la nostra referenza resta: la texture vive quanto il ponte
            ByResource11[tex] = p;
            if (hr == 0) ByResource11[*resource] = p;
        }
        Out.Line($"info bridge: madVR apre l'handle 0x{handle:X} in D3D11 -> texture {p.Width}x{p.Height} (riid {*riid}) hr=0x{hr:X8}");
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int OpenSharedResource(IntPtr device, IntPtr handle, Guid* riid, IntPtr* resource) { using var __hook = HookTrace.Enter("OpenSharedResource"); return Open(device, handle, riid, resource, _origOpenShared); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int OpenSharedResource1(IntPtr device, IntPtr handle, Guid* riid, IntPtr* resource) { using var __hook = HookTrace.Enter("OpenSharedResource1"); return Open(device, handle, riid, resource, _origOpenShared1); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CreateShaderResourceView(IntPtr device, IntPtr resource, IntPtr desc, IntPtr* srv)
    { using var __hook = HookTrace.Enter("CreateShaderResourceView");
        Out.Tid("CreateShaderResourceView");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)_origCreateSrv)(device, resource, desc, srv);
        if (hr == 0 && srv != null && !ByResource11.IsEmpty && ByResource11.TryGetValue(resource, out var p)) { BySrv[*srv] = p; SrvResource[*srv] = resource; }
        return hr;
    }

    /// <summary>Al primo uso in D3D11 del fotogramma: lettura della copia scaricata e caricamento con il contesto di madVR.</summary>
    private static void UploadPending(IntPtr ctx, Pair p, IntPtr resource)
    {
        lock (p)
        {
            if (UseLag(p) && Async && AsyncUse(ctx, p, resource)) return;
            if (UseLag(p) && Pbo) { PboUse(ctx, p, resource); return; }
            if (!p.Pending) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Pair source = p;
            if (UseLag(p))
            {
                // Ordine: prima si legge il download del fotogramma precedente (coda di wined3d vuota: nessuna attesa),
                // poi si accoda quello di questo fotogramma, che servira' al prossimo.
                if (ZeroCopy && !WorkerUpload)
                {
                    // Zero-copy: il download D3D9 finisce direttamente nella memoria di una texture D3D11 di appoggio
                    // del dispositivo di madVR; qui basta una copia GPU->GPU.
                    EnsureZeroCopy(ctx, p);
                    Pair? prevZ = _lagSource;
                    if (prevZ != null && prevZ.HasDownload && SameShape(prevZ, p) && prevZ.ZcStaging != IntPtr.Zero && prevZ.ZcContextDevice == ContextDevice(ctx))
                    {
                        LockedRect zl;
                        if (((delegate* unmanaged[Stdcall]<IntPtr, LockedRect*, IntPtr, uint, int>)V(prevZ.ZcSurface9, 13))(prevZ.ZcSurface9, &zl, IntPtr.Zero, 0x10) == 0)
                            ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(prevZ.ZcSurface9, 14))(prevZ.ZcSurface9); // solo per attendere la fine del download
                        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, resource, prevZ.ZcStaging);
                        long te = System.Diagnostics.Stopwatch.GetTimestamp(); double ff = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        lock (MetricSync) { _cCount++; _cSum += (te - t0) * ff; if ((te - t0) * ff > _cMax) _cMax = (te - t0) * ff; }
                    }
                    if (p.ZcSurface9 != IntPtr.Zero)
                    {
                        lock (p.SysLock) ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)V(p.Device9, 32))(p.Device9, p.Surface9, p.ZcSurface9);
                        p.HasDownload = true; _lagSource = p;
                        p.Pending = false; p.Uploads++; p.Reads++;
                        return;
                    }
                }
                if (!WorkerUpload)
                {
                    Pair? prev = _lagSource;
                    if (prev != null && prev.HasDownload && SameShape(prev, p))
                    {
                        LockedRect plr;
                        if (((delegate* unmanaged[Stdcall]<IntPtr, LockedRect*, IntPtr, uint, int>)V(prev.System9, 13))(prev.System9, &plr, IntPtr.Zero, 0x10) == 0)
                        {
                            long tl = System.Diagnostics.Stopwatch.GetTimestamp();
                            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, IntPtr, uint, uint, void>)V(ctx, 48))(ctx, resource, 0, IntPtr.Zero, plr.Bits, (uint)plr.Pitch, 0);
                            ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(prev.System9, 14))(prev.System9);
                            long te = System.Diagnostics.Stopwatch.GetTimestamp(); double ff = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                            lock (MetricSync) { _cCount++; _cSum += (te - t0) * ff; if ((te - t0) * ff > _cMax) _cMax = (te - t0) * ff; _cpySum += (tl - t0) * ff; }
                        }
                    }
                }
                p.HasDownload = DownloadSystemSurface(p) == 0;
                if (p.HasDownload) _lagSource = p;
                if (WorkerUpload) { EnsureWorker(); _workerSignal.Set(); }
                p.Pending = false; p.Uploads++; p.Reads++;
                return;
            }
            long t1, t2;
            lock (p.SysLock)
            {
                if (!p.DlBox.Empty)
                {
                    // Solo l'area disegnata da D3D9 (l'OSD), letta dalla superficie piccola del download.
                    Box w = p.DlBox;
                    LockedRect dl;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, LockedRect*, IntPtr, uint, int>)V(p.DlSys, 13))(p.DlSys, &dl, IntPtr.Zero, 0x10) != 0) { p.Pending = false; return; }
                    try
                    {
                        var box11 = new Box11(w);
                        IntPtr pixels = dl.Bits;
                        int pitch = dl.Pitch;
                        if (Is8(p))
                        {
                            bool bgra8 = As8 == "bgra";
                            byte* scratch = p.GetScratch();
                            pixels = (IntPtr)scratch;
                            pitch = w.W * 4;
                            for (int y = 0; y < w.H; y++)
                            {
                                Expand8To10((uint*)((byte*)dl.Bits + y * dl.Pitch), (uint*)(scratch + y * pitch), w.W, bgra8);
                            }
                        }
                        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, Box11*, IntPtr, uint, uint, void>)V(ctx, 48))(ctx, resource, 0, &box11, pixels, (uint)pitch, 0);
                    }
                    finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(p.DlSys, 14))(p.DlSys); }
                    lock (MetricSync) { _oDl++; _oDlMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
                    p.Pending = false; p.Uploads++; p.Reads++;
                    return;
                }
                LockedRect lr;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, LockedRect*, IntPtr, uint, int>)V(source.System9, 13))(source.System9, &lr, IntPtr.Zero, 0x10 /*READONLY*/);
                if (hr != 0) { p.Pending = false; return; }
                t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (Is8(p))
                {
                    bool bgra = As8 == "bgra";
                    byte* scratch = p.GetScratch();
                    for (uint y = 0; y < p.Height; y++)
                    {
                        Expand8To10((uint*)((byte*)lr.Bits + y * lr.Pitch), (uint*)(scratch + y * p.Pitch), (int)p.Width, bgra);
                    }
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, IntPtr, uint, uint, void>)V(ctx, 48))(ctx, resource, 0, IntPtr.Zero, (IntPtr)scratch, (uint)p.Pitch, 0);
                }
                else
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, IntPtr, uint, uint, void>)V(ctx, 48))(ctx, resource, 0, IntPtr.Zero, lr.Bits, (uint)lr.Pitch, 0);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(source.System9, 14))(source.System9);
            }
            p.Pending = false; p.Uploads++; p.Reads++;
            t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lock (MetricSync) { _cCount++; _cSum += (t2 - t0) * f; if ((t2 - t0) * f > _cMax) _cMax = (t2 - t0) * f; _cpySum += (t1 - t0) * f; _rtSum += (t0 - p.PendingSince) * f; }
        }
    }

    /// <summary>8 -> 10 bit per canale (la GPU tiene le texture 10 bit a 8), quattro pixel per volta: sotto emulazione
    /// x64 il ciclo scalare costava ~6 ms per l'area dell'OSD.</summary>
    private static void Expand8To10(uint* src, uint* dst, int n, bool bgra)
    {
        int x = 0;
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            var m8 = System.Runtime.Intrinsics.Vector128.Create(0xFFu);
            for (; x + 4 <= n; x += 4)
            {
                var v = System.Runtime.Intrinsics.Vector128.Load(src + x);
                var c0 = v & m8; var c1 = (v >> 8) & m8; var c2 = (v >> 16) & m8;
                var r = bgra ? c2 : c0; var b = bgra ? c0 : c2;
                var o = ((r << 2) | (r >> 6)) | (((c1 << 2) | (c1 >> 6)) << 10) | (((b << 2) | (b >> 6)) << 20) | ((v >> 30) << 30);
                System.Runtime.Intrinsics.Vector128.Store(o, dst + x);
            }
        }
        for (; x < n; x++)
        {
            uint v = src[x];
            uint c0 = v & 0xFF, c1 = (v >> 8) & 0xFF, c2 = (v >> 16) & 0xFF, a = v >> 30;
            uint r = bgra ? c2 : c0, b = bgra ? c0 : c2;
            dst[x] = ((r << 2) | (r >> 6)) | (((c1 << 2) | (c1 >> 6)) << 10) | (((b << 2) | (b >> 6)) << 20) | (a << 30);
        }
    }

    private static void UploadIfStale(IntPtr ctx, Pair p)
    {
        lock (Sync)
        {
            if (p.Native || p.Version == p.Uploaded || p.Texture11 == IntPtr.Zero) return;
            // UpdateSubresource sul contesto di madVR (stesso thread che sta per usare la texture).
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, IntPtr, uint, uint, void>)V(ctx, 48))(ctx, p.Texture11, 0, IntPtr.Zero, (IntPtr)p.GetScratch(), (uint)p.Pitch, 0);
            p.Uploaded = p.Version;
            if (p.Uploads++ == 0) Out.Line($"info bridge: primo fotogramma caricato in D3D11 ({p.Width}x{p.Height})");
        }
    }

    // ------------------------------------------------------------------ lettura diretta del fotogramma 16 bit
    // Il fotogramma finito da D3D9 arriva in una IOSurface dell'anello e veniva copiato per intero (45 MB) nella
    // texture che madVR ha aperto. In risparmio energetico il traffico di memoria abbassa la frequenza della GPU:
    // qui, quando madVR collega quella texture a uno shader, gli si passa la vista del posto dell'anello che contiene
    // il fotogramma. Stesso contenuto, nessuna copia. Attivo con la consegna in catena; CINECORE_BRIDGE_SRV=0 lo spegne.
    private static readonly bool SrvSubst = FastOption("CINECORE_BRIDGE_SRV");
    private static volatile AsyncSlot? _srvCur;
    private static long _srvSubstN, _srvBound;

    /// <summary>Vista (sul dispositivo di madVR) del posto in lettura diretta, se vale per questa texture.</summary>
    private static IntPtr SubstSrv(IntPtr ctx, Pair p)
    {
        var c = _srvCur;
        if (!SrvSubst || c == null || c.XMad == IntPtr.Zero || !p.Native || !UseLag(p) || p.ARing == null || Array.IndexOf(p.ARing, c) < 0) return IntPtr.Zero;
        if (c.SrvMad == IntPtr.Zero)
        {
            IntPtr srv;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)_origCreateSrv)(ContextDevice(ctx), c.XMad, IntPtr.Zero, &srv);
            if (hr != 0) { Out.Line($"info bridge: vista del posto dell'anello non creata hr=0x{hr:X8}"); return IntPtr.Zero; }
            c.SrvMad = srv;
        }
        return c.SrvMad;
    }

    /// <summary>Come CheckViews, e in piu' sostituisce in <paramref name="tmp"/> le viste del fotogramma 16 bit. Torna l'elenco da usare.</summary>
    private static IntPtr* SubstViews(IntPtr ctx, uint count, IntPtr* views, IntPtr* tmp, uint tmpLen)
    {
        CheckViews(ctx, count, views);
        if (!SrvSubst || views == null || _srvCur == null || BySrv.IsEmpty || count > tmpLen) return views;
        bool any = false;
        for (uint i = 0; i < count; i++)
        {
            IntPtr v = views[i];
            if (v != IntPtr.Zero && BySrv.TryGetValue(v, out var p))
            {
                IntPtr sub = SubstSrv(ctx, p);
                if (sub != IntPtr.Zero)
                {
                    if (!any) { for (uint k = 0; k < count; k++) tmp[k] = views[k]; any = true; }
                    tmp[i] = sub; Interlocked.Increment(ref _srvBound);
                }
            }
        }
        return any ? tmp : views;
    }

    private static void CheckViews(IntPtr ctx, uint count, IntPtr* views)
    {
        if (views == null) return;
        if (BySrv.IsEmpty) return;
        for (uint i = 0; i < count; i++)
        {
            if (!BySrv.TryGetValue(views[i], out var p)) continue;
            if (p.Native) { if (p.Pending && SrvResource.TryGetValue(views[i], out var res)) UploadPending(ctx, p, res); }
            else UploadIfStale(ctx, p);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void PSSetShaderResources(IntPtr ctx, uint start, uint count, IntPtr* views)
    { using var __hook = HookTrace.Enter("PSSetShaderResources"); IntPtr* tmp = stackalloc IntPtr[16]; views = SubstViews(ctx, count, views, tmp, 16); ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)_origPSSetSrv)(ctx, start, count, views); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void VSSetShaderResources(IntPtr ctx, uint start, uint count, IntPtr* views)
    { using var __hook = HookTrace.Enter("VSSetShaderResources"); IntPtr* tmp = stackalloc IntPtr[16]; views = SubstViews(ctx, count, views, tmp, 16); ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)_origVSSetSrv)(ctx, start, count, views); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void CSSetShaderResources(IntPtr ctx, uint start, uint count, IntPtr* views)
    { using var __hook = HookTrace.Enter("CSSetShaderResources"); IntPtr* tmp = stackalloc IntPtr[16]; views = SubstViews(ctx, count, views, tmp, 16); ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)_origCSSetSrv)(ctx, start, count, views); }

    // ------------------------------------------------------------------ query D3D11: attesa vera al posto del martellamento
    // madVR, finito di inviare un fotogramma, chiede in continuazione "hai finito?" (GetData). Ogni domanda attraversa
    // Wine e DXMT. Con CINECORE_D11_QUERY_WAIT=<ms> la domanda senza risposta viene trattenuta qui: il thread dorme a
    // passi brevi e richiede a DXMT solo ogni CINECORE_D11_QUERY_STEP ms, al massimo per <ms>; poi torna "non pronto"
    // come prima (nessun blocco possibile: il tempo e' limitato e le query non ancora chiuse con End non si toccano).
    private static readonly double D11QueryWaitMs = double.TryParse(Environment.GetEnvironmentVariable("CINECORE_D11_QUERY_WAIT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double qwm) ? Math.Clamp(qwm, 0, 50) : 20;
    private static readonly double D11QueryStepMs = double.TryParse(Environment.GetEnvironmentVariable("CINECORE_D11_QUERY_STEP"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double qws) && qws > 0 ? qws : 1;
    private static IntPtr _origQwGetData, _origQwEnd, _origQwFlush, _origQwDraw, _origQwDispatch;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, long> QwEnded = new();
    private static long _qwDone, _qwCalls, _qwNotReady, _qwHeld, _qwWaitUs, _qwWaitMaxUs, _qwNoFlush;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void QwDraw(IntPtr ctx, uint count, uint start)
    { using var __hook = HookTrace.Enter("d11 Draw"); ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)_origQwDraw)(ctx, count, start); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void QwDispatch(IntPtr ctx, uint x, uint y, uint z)
    { using var __hook = HookTrace.Enter("d11 Dispatch"); ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)_origQwDispatch)(ctx, x, y, z); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void QwFlush(IntPtr ctx)
    { using var __hook = HookTrace.Enter("d11 Flush"); ((delegate* unmanaged[Stdcall]<IntPtr, void>)_origQwFlush)(ctx); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void QwEnd(IntPtr ctx, IntPtr q)
    { using var __hook = HookTrace.Enter("d11 End"); ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)_origQwEnd)(ctx, q); QwEnded[q] = System.Diagnostics.Stopwatch.GetTimestamp(); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int QwGetData(IntPtr ctx, IntPtr q, IntPtr data, uint size, uint flags)
    { using var __hook = HookTrace.Enter("d11 GetData");
        var get = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, int>)_origQwGetData;
        Interlocked.Increment(ref _qwCalls);
        int hr = get(ctx, q, data, size, flags);
        bool ended = QwEnded.TryGetValue(q, out long endedAt);
        if (hr == 1)
        {
            Interlocked.Increment(ref _qwNotReady);
            if ((flags & 1) != 0) Interlocked.Increment(ref _qwNoFlush);
            // Trattenuta: solo query gia' chiuse con End, per un tempo limitato.
            if (ended && D11QueryWaitMs > 0)
            {
                Interlocked.Increment(ref _qwHeld);
                long freq = System.Diagnostics.Stopwatch.Frequency, t0 = System.Diagnostics.Stopwatch.GetTimestamp(), limit = t0 + (long)(D11QueryWaitMs * freq / 1000);
                int step = (int)Math.Max(1, Math.Round(D11QueryStepMs));
                while (hr == 1 && System.Diagnostics.Stopwatch.GetTimestamp() < limit)
                {
                    Thread.Sleep(step);
                    hr = get(ctx, q, data, size, flags);
                }
            }
        }
        if (hr == 0 && ended && QwEnded.TryRemove(q, out _))
        {
            long us = (System.Diagnostics.Stopwatch.GetTimestamp() - endedAt) * 1000000 / System.Diagnostics.Stopwatch.Frequency;
            Interlocked.Increment(ref _qwDone); Interlocked.Add(ref _qwWaitUs, us);
            if (us > Interlocked.Read(ref _qwWaitMaxUs)) Interlocked.Exchange(ref _qwWaitMaxUs, us);
        }
        return hr;
    }

    private static IntPtr _origTlDraw, _origTlDispatch, _origTlGetData, _origTlEnd, _origTlFlush;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, (long T, int N)> TlQ = new();
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void TlDraw(IntPtr ctx, uint count, uint start)
    { long t = Tl.Now; ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)_origTlDraw)(ctx, count, start); Tl.Ev($"d11 Draw call={Tl.Ms(t):0.00}"); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void TlDispatch(IntPtr ctx, uint x, uint y, uint z)
    { long t = Tl.Now; ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)_origTlDispatch)(ctx, x, y, z); Tl.Ev($"d11 Dispatch {x}x{y}x{z} call={Tl.Ms(t):0.00}"); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void TlEnd(IntPtr ctx, IntPtr q)
    { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)_origTlEnd)(ctx, q); TlQ[q] = (Tl.Now, 0); Tl.Ev($"d11 End q={q:X}"); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void TlFlush(IntPtr ctx)
    { long t = Tl.Now; ((delegate* unmanaged[Stdcall]<IntPtr, void>)_origTlFlush)(ctx); Tl.Ev($"d11 Flush call={Tl.Ms(t):0.00}"); }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int TlGetData(IntPtr ctx, IntPtr q, IntPtr data, uint size, uint flags)
    {
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, int>)_origTlGetData)(ctx, q, data, size, flags);
        if (TlQ.TryGetValue(q, out var e))
        {
            if (hr == 0) { TlQ.TryRemove(q, out _); Tl.Ev($"d11 QueryDone q={q:X} attesa={Tl.Ms(e.T):0.00} giri={e.N + 1}"); }
            else TlQ[q] = (e.T, e.N + 1);
        }
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void CopySubresourceRegion(IntPtr ctx, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, IntPtr box)
    { using var __hook = HookTrace.Enter("CopySubresourceRegion");
        if (Flip && dst == _flipBack)
        {
            bool fresh = src != _flipLastSrc;
            if (fresh) { _flipDirty = 1; _fdCopy++; Interlocked.Increment(ref _fdCopyTot); } else _fdSame++;
            _fdSignals++; _flipLastSrc = src;
            // Stessa sorgente, stessa zona, nessuna novita': nel back buffer ombra c'e' gia' (ripetizione).
            Box11 rb = box != IntPtr.Zero ? *(Box11*)box : default;
            var region = (dstSub, x, y, srcSub, box != IntPtr.Zero, rb.L, rb.T, rb.R, rb.B);
            bool same = !_pqLastWhole && region == _pqLastRegion;
            _pqLastRegion = region; _pqLastWhole = false;
            if (_pqTex.Length > 0 && !fresh && _flipDirty == 0 && same)
            {
                if (!ByResource11.IsEmpty && ByResource11.TryGetValue(src, out var pr)) { if (!pr.Native) UploadIfStale(ctx, pr); else if (pr.Pending) UploadPending(ctx, pr, src); }
                Interlocked.Increment(ref _pqSkipped);
                if (FlipTrace) Ft($"copiaR saltata src={src:X}");
                return;
            }
        }
        Out.Tid("CopySubresourceRegion");
        if (!ByResource11.IsEmpty && ByResource11.TryGetValue(src, out var p)) { if (!p.Native) UploadIfStale(ctx, p); else if (p.Pending) UploadPending(ctx, p, src); if (SrvSubst && SubstSrv(ctx, p) != IntPtr.Zero) src = _srvCur!.XMad; }
        if (Flip && dst == _flipBack && PqDirect)
        {
            Box11 db = box != IntPtr.Zero ? *(Box11*)box : default;
            IntPtr direct = PqDirectTarget(dst, src, (dstSub, x, y, srcSub, box != IntPtr.Zero, db.L, db.T, db.R, db.B));
            if (direct != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, void>)_origCopyRegion)(ctx, direct, dstSub, x, y, z, src, srcSub, box);
                Interlocked.Increment(ref _cpDirect);
                if (FlipTrace) Ft($"copiaR diretta posto={_pqDirect} src={src:X} sporco={_flipDirty}");
                return;
            }
            if (FlipTrace) Ft($"copiaR ombra src={src:X} sporco={_flipDirty} attesa={_pqDirectHold}");
            lock (FlipSync) _pqShadowStale = false;
        }
        if (!ByResource11.IsEmpty) NoteD3D11WriteResource(dst, "CopyRegion");
        if (dst == _flipBack) Interlocked.Increment(ref _cpShadow); else { Interlocked.Increment(ref _cpOther); NoteOtherCopy(dst); }
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, void>)_origCopyRegion)(ctx, dst, dstSub, x, y, z, src, srcSub, box);
        FlushIfShared(ctx, dst);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void CopyResource(IntPtr ctx, IntPtr dst, IntPtr src)
    { using var __hook = HookTrace.Enter("CopyResource");
        // Coda flip: madVR ricopia la texture finale nel back buffer prima di ogni Present; stessa sorgente = ripetizione.
        bool skipCopy = false;
        if (Flip && dst == _flipBack)
        {
            bool fresh = src != _flipLastSrc;
            if (fresh) { _flipDirty = 1; _fdCopy++; Interlocked.Increment(ref _fdCopyTot); } else _fdSame++;
            _fdSignals++; _flipLastSrc = src;
            // Presentazione su thread dedicato: il fotogramma nuovo va in una texture dell'anello (il back buffer lo
            // riempie il thread che presenta, al momento giusto); la ricopia di una ripetizione non serve.
            // Con il back buffer ombra il contenuto resta: ricopiare la stessa sorgente senza novita' non serve.
            if (_pqTex.Length > 0 && !fresh && _flipDirty == 0 && _pqLastWhole) skipCopy = true;
            _pqLastWhole = true;
        }
        Out.Tid("CopyResource");
        // Con uscita a 10 bit madVR copia la texture finale nel back buffer (stesso formato) invece di campionarla.
        if (!ByResource11.IsEmpty && ByResource11.TryGetValue(src, out var p)) { if (!p.Native) UploadIfStale(ctx, p); else if (p.Pending) UploadPending(ctx, p, src); if (SrvSubst && SubstSrv(ctx, p) != IntPtr.Zero) src = _srvCur!.XMad; }
        if (skipCopy) { Interlocked.Increment(ref _pqSkipped); return; }
        if (!ByResource11.IsEmpty) NoteD3D11WriteResource(dst, "CopyResource");
        long tlc = Tl.Now;
        if (Flip && dst == _flipBack && PqDirect)
        {
            IntPtr direct = PqDirectTarget(dst, src, (0, 0, 0, 0, false, 0, 0, uint.MaxValue, uint.MaxValue));
            if (direct != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, direct, src); Interlocked.Increment(ref _cpDirect); if (FlipTrace) Ft($"copia diretta posto={_pqDirect} src={src:X} sporco={_flipDirty}"); return; }
            if (FlipTrace) Ft($"copia ombra src={src:X} sporco={_flipDirty} attesa={_pqDirectHold}");
            lock (FlipSync) _pqShadowStale = false;
        }
        if (dst == _flipBack) Interlocked.Increment(ref _cpShadow); else { Interlocked.Increment(ref _cpOther); NoteOtherCopy(dst); }
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, dst, src);
        FlushIfShared(ctx, dst);
        if (Tl.On) Tl.Ev($"d11 CopyResource call={Tl.Ms(tlc):0.00}");
        if (D3D9Shim.Diag && Interlocked.Read(ref _dxgiPresents) >= DiagFrom && _probeLogs < 30 && ByResource11.TryGetValue(dst, out var pd))
        {
            _probeLogs++;
            Out.Line($"diag  contenuto h=0x{pd.Handle:X}: madVR={GreenAvg(ContextDevice(ctx), ctx, dst)} nostro={(OwnContext is { } oc ? GreenAvgOwn(pd) : "-")} sorgente={GreenAvg(ContextDevice(ctx), ctx, src)}");
        }
    }

    private static int BySrvCount(Pair p) { int n = 0; foreach (var kv in BySrv) if (kv.Value == p) n++; return n; }

    public static int? SurfaceFormat(IntPtr surface)
    {
        if (surface == IntPtr.Zero) return null;
        lock (Sync) foreach (var p in Pairs) if (p.Surface9 == surface) return p.Format9;
        return null;
    }

    public static string Stats()
    {
        lock (Sync)
        {
            var parts = new List<string>();
            foreach (var p in Pairs) parts.Add($"{p.Width}x{p.Height}/{p.Format9}:letti={p.Reads},caricati={p.Uploads},aperta={(p.Texture11 != IntPtr.Zero ? "si" : "no")}");
            return string.Join(" ", parts);
        }
    }

    // ------------------------------------------------------------------ statistiche della swapchain DXGI
    // In DXMT GetFrameStatistics e' uno stub (tempi a zero, contatori tutti uguali): madVR non aggancia il vsync
    // e non presenta. Emulazione: Present arrivati al vsync successivo, refresh a 60 Hz, tempi QPC veri.
    private static IntPtr _origScPresent, _origScStats, _origScPresent1;
    private static long _dxgiPresents, _lastDxgiPresentQpc, _statsCalls;
    private static System.Windows.Forms.Form? _dummyForm;

    [StructLayout(LayoutKind.Sequential)] private struct FrameStats { public uint PresentCount, PresentRefreshCount, SyncRefreshCount; public long SyncQPCTime, SyncGPUTime; }

    private static void HookSwapChain(ID3D11Device device)
    {
        try
        {
            _dummyForm = new System.Windows.Forms.Form { ShowInTaskbar = false, Width = 64, Height = 64 };
            IntPtr hwnd = _dummyForm.Handle;
            using var dxgiDevice = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            HookOutput(adapter);
            using var factory = adapter.GetParent<Vortice.DXGI.IDXGIFactory2>();
            Patch(factory.NativePointer, 15, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint*, IntPtr, IntPtr, IntPtr*, int>)&FactCreateScHwnd, out _origCreateScHwnd);
            Patch(factory.NativePointer, 10, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, IntPtr*, int>)&FactCreateSc, out _origCreateSc);
            var desc = new Vortice.DXGI.SwapChainDescription1
            {
                Width = 64, Height = 64, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm, BufferCount = 2,
                BufferUsage = Vortice.DXGI.Usage.RenderTargetOutput, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                SwapEffect = Vortice.DXGI.SwapEffect.FlipDiscard
            };
            StepPause("finestra di servizio creata");
            var chain = factory.CreateSwapChainForHwnd(device, hwnd, desc);
            IntPtr sc = chain.NativePointer;
            Patch(sc, 8, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)&ScPresent, out _origScPresent);
            Patch(sc, 16, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, FrameStats*, int>)&ScGetFrameStatistics, out _origScStats);
            Patch(sc, 22, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int>)&ScPresent1, out _origScPresent1);
            Patch(sc, 37, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)&ScCheckColorSpace, out _origScCheckCs);
            Patch(sc, 38, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&ScSetColorSpace1, out _origScSetCs);
            Patch(sc, 40, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr, int>)&ScSetHdrMetaData, out _origScHdr);
            Patch(sc, 31, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&ScSetMaxLatency, out _origScSetLatency);
            Patch(sc, 17, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&ScGetLastPresentCount, out _origScLastCount);
            if (Flip)
            {
                Patch(sc, 9, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)&ScGetBuffer, out _origScGetBuffer);
                Patch(sc, 13, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int>)&ScResizeBuffers, out _origScResize);
            }
            Patch(dxgiDevice.NativePointer, 12, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&DevSetMaxLatency, out _origDevSetLatency);
            _keepChain = chain;
            Out.Line("info bridge: statistiche DXGI emulate");
        }
        catch (Exception ex) { Out.Line("info bridge: swapchain " + ex.Message); }
    }
    private static object? _keepChain;
    private static IntPtr _origScCheckCs, _origScSetCs, _origScHdr, _origOutDesc1;
    private static readonly int ForceBits = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_DISPLAY_BITS"), out int fb) ? fb : 10;

    /// <summary>
    /// madVR presenta a 10 bit in "fullscreen windowed" solo se l'uscita DXGI dichiara almeno 10 bit per colore.
    /// DXMT ne dichiara 8 anche sugli schermi Mac (che compongono in EDR a virgola mobile): si riportano 10 bit.
    /// </summary>
    private static void HookOutput(Vortice.DXGI.IDXGIAdapter adapter)
    {
        try
        {
            if (adapter.EnumOutputs(0, out Vortice.DXGI.IDXGIOutput? output).Failure || output == null) { Out.Line("info bridge: nessuna uscita DXGI"); return; }
            IntPtr o6;
            Guid iid = new("068346E8-AAEC-4B84-ADD7-137F513F77A1"); // IDXGIOutput6
            if (Marshal.QueryInterface(output.NativePointer, ref iid, out o6) != 0) { Out.Line("info bridge: IDXGIOutput6 assente"); return; }
            Patch(o6, 27, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)&OutGetDesc1, out _origOutDesc1);
            Patch(o6, 8, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, uint, uint*, byte*, int>)&OutGetModes, out _origOutModes);
            Patch(o6, 19, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, uint, uint*, byte*, int>)&OutGetModes1, out _origOutModes1);
            byte* d = stackalloc byte[160];
            ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)_origOutDesc1)(o6, d);
            Out.Line($"info bridge: uscita DXGI bit={*(uint*)(d + 96)} spazioColore={*(int*)(d + 100)} lum={*(float*)(d + 136):0.###}-{*(float*)(d + 140):0} nit (pieno schermo {*(float*)(d + 144):0}) -> bit dichiarati {Math.Max(*(uint*)(d + 96), (uint)ForceBits)}");
            Marshal.Release(o6);
            _keepOutput = output;
        }
        catch (Exception ex) { Out.Line("info bridge: uscita DXGI " + ex.Message); }
    }
    private static object? _keepOutput;
    private static IntPtr _origCheckFmt, _origCheckFeat;
    private static readonly bool Force10Bit = Environment.GetEnvironmentVariable("CINECORE_FORCE_10BIT") == "1";
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CheckFormatSupport(IntPtr dev, int fmt, uint* support)
    { using var __hook = HookTrace.Enter("CheckFormatSupport");
        Out.Tid("CheckFormatSupport");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)_origCheckFmt)(dev, fmt, support);
        LogOnce($"info d3d11: CheckFormatSupport(fmt={fmt}) -> 0x{(support != null ? *support : 0):X} hr=0x{hr:X8}");
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CheckFeatureSupport(IntPtr dev, int feature, void* data, uint size)
    { using var __hook = HookTrace.Enter("CheckFeatureSupport");
        Out.Tid("CheckFeatureSupport");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, int>)_origCheckFeat)(dev, feature, data, size);
        if (feature == 2 /*FORMAT_SUPPORT*/ && size >= 8) LogOnce($"info d3d11: CheckFeatureSupport(FORMAT_SUPPORT fmt={((int*)data)[0]}) -> 0x{((uint*)data)[1]:X}");
        else if (feature == 3 && size >= 12) LogOnce($"info d3d11: CheckFeatureSupport(FORMAT_SUPPORT2 fmt={((int*)data)[0]}) -> 0x{((uint*)data)[1]:X}");
        else LogOnce($"info d3d11: CheckFeatureSupport({feature}) hr=0x{hr:X8}");
        return hr;
    }
    private static IntPtr _origOutModes, _origOutModes1, _origCreateSc, _origCreateScHwnd;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int OutGetModes(IntPtr o, int fmt, uint flags, uint* n, byte* modes)
    { using var __hook = HookTrace.Enter("OutGetModes");
        Out.Tid("OutGetModes");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, uint*, byte*, int>)_origOutModes)(o, fmt, flags, n, modes);
        LogOnce($"info dxgi: GetDisplayModeList(fmt={fmt}, flags=0x{flags:X}) -> {(n != null ? *n : 0)} modi hr=0x{hr:X8}");
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int OutGetModes1(IntPtr o, int fmt, uint flags, uint* n, byte* modes)
    { using var __hook = HookTrace.Enter("OutGetModes1");
        Out.Tid("OutGetModes1");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, uint*, byte*, int>)_origOutModes1)(o, fmt, flags, n, modes);
        LogOnce($"info dxgi: GetDisplayModeList1(fmt={fmt}, flags=0x{flags:X}) -> {(n != null ? *n : 0)} modi hr=0x{hr:X8}");
        return hr;
    }
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, RECT* rc);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    // IDXGIFactory2::CreateSwapChainForHwnd (15) e IDXGIFactory::CreateSwapChain (10): formato chiesto da madVR.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int FactCreateScHwnd(IntPtr f, IntPtr dev, IntPtr hwnd, uint* desc, IntPtr fs, IntPtr output, IntPtr* sc)
    { using var __hook = HookTrace.Enter("FactCreateScHwnd");
        Out.Tid("FactCreateScHwnd");
        // madVR crea in finestra una swapchain a 8 bit anche con displayBitdepth=10: si chiede R10G10B10A2.
        if (desc != null && desc[2] == 87 && desc[0] > 64 && Force10Bit) { desc[2] = 24; Out.Line("info dxgi: swapchain di madVR portata a 10 bit (R10G10B10A2)"); }
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint*, IntPtr, IntPtr, IntPtr*, int>)_origCreateScHwnd)(f, dev, hwnd, desc, fs, output, sc);
        if (desc != null && desc[0] > 64) ApplyFrameLatency(dev, hr);
        if (desc != null)
        {
            RECT rc = default; GetClientRect(hwnd, &rc);
            Out.Line($"info dxgi: CreateSwapChainForHwnd {desc[0]}x{desc[1]} fmt={desc[2]} buffer={desc[7]} effetto={desc[9]} flags=0x{desc[11]:X} hr=0x{hr:X8} hwnd=0x{hwnd:X} client={rc.Right - rc.Left}x{rc.Bottom - rc.Top} parent=0x{GetParent(hwnd):X} visibile={IsWindowVisible(hwnd)}");
        }
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int FactCreateSc(IntPtr f, IntPtr dev, uint* desc, IntPtr* sc)
    { using var __hook = HookTrace.Enter("FactCreateSc");
        Out.Tid("FactCreateSc");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, IntPtr*, int>)_origCreateSc)(f, dev, desc, sc);
        if (desc != null) Out.Line($"info dxgi: CreateSwapChain {desc[0]}x{desc[1]} fmt={desc[4]} hr=0x{hr:X8}");
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int OutGetDesc1(IntPtr output, byte* desc)
    { using var __hook = HookTrace.Enter("OutGetDesc1");
        Out.Tid("OutGetDesc1");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)_origOutDesc1)(output, desc);
        if (hr == 0 && desc != null && *(uint*)(desc + 96) < ForceBits) *(uint*)(desc + 96) = (uint)ForceBits;
        // Banco: lo schermo virtuale di prova e' SDR; dichiararlo HDR10 fa fare a madVR lo stesso lavoro (passante)
        // che fa sullo schermo interno.
        if (hr == 0 && desc != null && QaForceHdrOutput && *(int*)(desc + 100) != 12)
        {
            LogOnce($"info bridge: uscita DXGI spazioColore={*(int*)(desc + 100)} dichiarata HDR10 (CINECORE_QA_FORCE_HDR_OUTPUT)");
            *(int*)(desc + 100) = 12; if (*(uint*)(desc + 96) < 10) *(uint*)(desc + 96) = 10;
            *(float*)(desc + 136) = 0f; *(float*)(desc + 140) = 200f; *(float*)(desc + 144) = 200f;
        }
        return hr;
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ScLogged = new();
    private static readonly bool QaForceHdrOutput = Environment.GetEnvironmentVariable("CINECORE_QA_FORCE_HDR_OUTPUT") == "1";
    private static void LogOnce(string s) { if (ScLogged.TryAdd(s, 1)) Out.Line(s); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ScCheckColorSpace(IntPtr sc, int cs, uint* support)
    { using var __hook = HookTrace.Enter("ScCheckColorSpace");
        Out.Tid("ScCheckColorSpace");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)_origScCheckCs)(sc, cs, support);
        LogOnce($"info swapchain: CheckColorSpaceSupport({cs}) -> 0x{(support != null ? *support : 0):X} hr=0x{hr:X8}");
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ScSetColorSpace1(IntPtr sc, int cs)
    { using var __hook = HookTrace.Enter("ScSetColorSpace1");
        Out.Tid("ScSetColorSpace1");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)_origScSetCs)(sc, cs);
        LogOnce($"info swapchain: SetColorSpace1({cs}{(cs == 12 ? " = HDR10 PQ BT.2020" : cs == 0 ? " = SDR" : "")}) hr=0x{hr:X8}");
        return hr;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ScSetHdrMetaData(IntPtr sc, int type, uint size, IntPtr data)
    { using var __hook = HookTrace.Enter("ScSetHdrMetaData");
        Out.Tid("ScSetHdrMetaData");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr, int>)_origScHdr)(sc, type, size, data);
        if (type == 1 && size >= 24 && data != IntPtr.Zero)
        {
            ushort* d = (ushort*)data;
            uint maxL = *(uint*)((byte*)data + 16), maxCll = d[12], maxFall = d[13];
            LogOnce($"info swapchain: SetHDRMetaData HDR10 maxMastering={maxL / 10000.0:0} nit maxCLL={maxCll} maxFALL={maxFall} hr=0x{hr:X8}");
        }
        else LogOnce($"info swapchain: SetHDRMetaData tipo={type} hr=0x{hr:X8}");
        return hr;
    }

    // Metriche di cadenza (finestra corrente, azzerata da TakeMetrics).
    private static readonly object MetricSync = new();
    private static long _mCount, _mLate25, _mLate50; private static double _mSum, _mMax;
    private static long _cCount; private static double _cSum, _cMax, _rtSum, _cpySum;
    private static long _oRev, _oDl, _oLate; private static double _oRevMs, _oDlMs, _oA, _oB, _oC, _oU, _oD;

    private static void NoteDxgiPresent()
    {
        long n = Interlocked.Increment(ref _dxgiPresents);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long prev = Interlocked.Exchange(ref _lastDxgiPresentQpc, now);
        {
            double periodV = System.Diagnostics.Stopwatch.Frequency / RefreshHz;
            uint target = (uint)(now / periodV) + 1;
            lock (MetricSync) _lastAssignedVsync = Math.Max(target, _lastAssignedVsync + 1);
        }
        if (prev != 0)
        {
            double ms = (now - prev) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lock (MetricSync) { _mCount++; _mSum += ms; if (ms > _mMax) _mMax = ms; if (ms > 25) _mLate25++; if (ms > 50) _mLate50++; }
            if (ms > 50 && SpikeLog) Out.Line(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"spike present={n} t={now * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms={ms:0.0}"));
        }
        if (n == 1) Out.Line("info bridge: primo Present D3D11 di madVR");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ScPresent(IntPtr sc, uint sync, uint flags)
    { using var __hook = HookTrace.Enter("ScPresent");
        Out.Tid("ScPresent");
        if (Flip && (flags & 1) == 0) return FlipPresent(sc, sync, flags, IntPtr.Zero, false);
        if ((flags & 1) == 0) { NoteDxgiPresent(); TakeSnapshot(sc); long n = Interlocked.Read(ref _dxgiPresents); if (ProbeOn && (n == 3 || n % 150 == 0)) { Out.Line($"info bridge: sonda al Present #{n}"); ProbeBackBuffer(sc, n); } } // DXGI_PRESENT_TEST non conta
        long tlp = Tl.Now;
        if (PresentSyncOverride >= 0 && (flags & 1) == 0) sync = (uint)PresentSyncOverride;
        int hrp = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)_origScPresent)(sc, sync, flags);
        if (Tl.On) Tl.Ev($"d11 Present sync={sync} flags=0x{flags:X} call={Tl.Ms(tlp):0.00}");
        return hrp;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ScPresent1(IntPtr sc, uint sync, uint flags, IntPtr parameters)
    { using var __hook = HookTrace.Enter("ScPresent1");
        Out.Tid("ScPresent1");
        if (Flip && (flags & 1) == 0) return FlipPresent(sc, sync, flags, parameters, true);
        if ((flags & 1) == 0) { NoteDxgiPresent(); TakeSnapshot(sc); long n = Interlocked.Read(ref _dxgiPresents); if (ProbeOn && (n == 3 || n % 150 == 0)) { Out.Line($"info bridge: sonda al Present1 #{n} sync={sync} flags=0x{flags:X}"); ProbeBackBuffer(sc, n); } }
        long tlp = Tl.Now;
        if (PresentSyncOverride >= 0 && (flags & 1) == 0) sync = (uint)PresentSyncOverride;
        int hrp = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int>)_origScPresent1)(sc, sync, flags, parameters);
        if (Tl.On) Tl.Ev($"d11 Present1 sync={sync} flags=0x{flags:X} call={Tl.Ms(tlp):0.00}");
        return hrp;
    }

    // Latenza massima dei fotogrammi (quante Present possono restare in coda prima che la chiamata si blocchi).
    private static readonly int FrameLatencyOverride = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_FRAME_LATENCY"), out int flv) ? flv : -1;
    private static IntPtr _origScSetLatency, _origDevSetLatency;
    private static readonly Guid IID_DxgiDevice1 = new("77db970f-6276-48ba-ba28-070143b4392c");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ScSetMaxLatency(IntPtr sc, uint n)
    {
        Out.Line($"info dxgi: swapchain SetMaximumFrameLatency({n}){(FrameLatencyOverride >= 0 ? $" -> {FrameLatencyOverride}" : "")}");
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)_origScSetLatency)(sc, FrameLatencyOverride >= 0 ? (uint)FrameLatencyOverride : n);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int DevSetMaxLatency(IntPtr dev, uint n)
    {
        Out.Line($"info dxgi: dispositivo SetMaximumFrameLatency({n}){(FrameLatencyOverride >= 0 ? $" -> {FrameLatencyOverride}" : "")}");
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)_origDevSetLatency)(dev, FrameLatencyOverride >= 0 ? (uint)FrameLatencyOverride : n);
    }
    private static void ApplyFrameLatency(IntPtr dev, int hr)
    {
        if (hr != 0 || dev == IntPtr.Zero) return;
        Guid iid = IID_DxgiDevice1; IntPtr d1;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(dev, 0))(dev, &iid, &d1) != 0 || d1 == IntPtr.Zero) return;
        uint cur = 0;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)V(d1, 13))(d1, &cur);
        int set = -1;
        if (FrameLatencyOverride >= 0 && _origDevSetLatency != IntPtr.Zero)
            set = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)_origDevSetLatency)(d1, (uint)FrameLatencyOverride);
        Out.Line($"info dxgi: latenza massima del dispositivo di madVR = {cur}{(FrameLatencyOverride >= 0 ? $", impostata a {FrameLatencyOverride} (hr=0x{set:X8})" : "")}");
        Release(d1);
    }
    // ------------------------------------------------------------------ coda flip emulata (CINECORE_DXGI_STATS=flip)
    // madVR in D3D11 presenta ogni fotogramma una volta per vsync in cui deve restare a schermo (24p su 60 Hz: 2 o 3
    // Present di seguito, senza ridisegnare) e conta sulla coda flip di Windows: la Present torna subito e, a coda
    // piena, con DXGI_PRESENT_DO_NOT_WAIT risponde "ancora occupato". Lo strato Metal non ha quella coda: ogni Present
    // in piu' restava ferma ~31 ms sul thread che disegna anche (misurato sullo schermo interno: 30 Present/s e 12
    // fotogrammi nuovi/s con la GPU al 70%). Qui la coda e' emulata: ogni Present accettata occupa un vsync; le
    // ripetizioni non arrivano a Metal (il fotogramma precedente resta a schermo); un fotogramma nuovo viene
    // presentato con durata minima = vsync occupati dal precedente, cosi' Metal lo mostra al momento giusto anche se
    // la chiamata arriva in anticipo. Coda piena o fotogramma precedente non ancora a schermo: "ancora occupato".
    private static readonly bool Flip = StatsModeIsFlip();
    // Predefinita dall'11/11/2026 (schermo interno, risparmio energetico, impostazioni piu' pesanti: da ~12 persi/s a 0).
    // CINECORE_DXGI_STATS=simple torna all'emulazione precedente.
    private static bool StatsModeIsFlip() => (Environment.GetEnvironmentVariable("CINECORE_DXGI_STATS") ?? "flip") == "flip";
    private static readonly int FlipDepth = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_FLIP_DEPTH"), out int fd) ? fd : 8;
    private static readonly int FlipAhead = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_FLIP_AHEAD"), out int fa) ? fa : 1;
    private static readonly bool FlipJit = Environment.GetEnvironmentVariable("CINECORE_FLIP_JIT") != "0";
    private static readonly object FlipSync = new();
    private static readonly uint[] _flipSlots = new uint[64];
    private static long _flipIssued;
    private static uint _flipLastSlot, _flipLastRealSlot;
    private static readonly Queue<uint> _flipRealSlots = new();
    private static volatile int _flipDirty = 1;
    private static IntPtr _flipBack, _flipLastSrc, _flipChain;
    private static long _fdCopy, _fdSame, _fdAsync, _fdOsd, _fdSignals;
    // Attivo con la consegna in catena (CINECORE_FLIP_DIRTY_COPY=0 lo spegne): vedi AsyncUse.
    private static readonly bool FlipDirtyByCopy = FastOption("CINECORE_FLIP_DIRTY_COPY");
    private static long _fdAsyncTot, _fdCopyTot, _fdAsyncQuiet;
    private static long _flipReal, _flipRepeat, _flipBusy, _flipBusyNew;
    private static IntPtr _origScLastCount;
    internal static string FlipStats() => Flip ? $"flip: veri={Interlocked.Exchange(ref _flipReal, 0)} ripetuti={Interlocked.Exchange(ref _flipRepeat, 0)} occupato={Interlocked.Exchange(ref _flipBusy, 0)} (nuovi {Interlocked.Exchange(ref _flipBusyNew, 0)}) sporco: copiaNuova={Interlocked.Exchange(ref _fdCopy, 0)} copiaUguale={Interlocked.Exchange(ref _fdSame, 0)} video={Interlocked.Exchange(ref _fdAsync, 0)} osd={Interlocked.Exchange(ref _fdOsd, 0)}{PqStats()}" : "";

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ScGetLastPresentCount(IntPtr sc, uint* count)
    {
        if (!Flip) return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)_origScLastCount)(sc, count);
        if (count == null) return unchecked((int)0x80004003);
        lock (FlipSync) *count = (uint)_flipIssued;
        return 0;
    }

    private static IntPtr _origScGetBuffer, _origScResize;
    private static IntPtr GetBufferFn(IntPtr sc) => _origScGetBuffer != IntPtr.Zero ? _origScGetBuffer : V(sc, 9);

    // Il back buffer che madVR riceve e' quello in cui poi copia il fotogramma: lo si segue da qui, cosi' resta giusto
    // anche dopo un ridimensionamento o un passaggio a schermo intero (ResizeBuffers ne crea di nuovi).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ScGetBuffer(IntPtr sc, uint index, Guid* iid, IntPtr* surface)
    {
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)_origScGetBuffer)(sc, index, iid, surface);
        if (hr == 0 && index == 0 && surface != null && *surface != IntPtr.Zero && iid != null && *iid == IID_Tex2D && !IsDummyChain(sc))
        {
            IntPtr bb = *surface;
            IntPtr shadow = PqSetup(sc, bb);
            if (shadow != IntPtr.Zero)
            {
                // madVR riceve l'ombra (un riferimento suo); quello vero lo tiene il thread che presenta.
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(shadow, 1))(shadow);
                Release(bb);
                *surface = shadow; bb = shadow;
            }
            if (bb != _flipBack || sc != _flipChain)
            {
                _flipChain = sc; _flipBack = bb; _flipLastSrc = IntPtr.Zero; _flipDirty = 1;
                Out.Line($"info flip: back buffer 0x{bb:X} (GetBuffer di madVR{(shadow != IntPtr.Zero ? ", ombra" : "")})");
            }
        }
        return hr;
    }
    private static bool IsDummyChain(IntPtr sc) => _keepChain is Vortice.DXGI.IDXGISwapChain1 k && k.NativePointer == sc;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ScResizeBuffers(IntPtr sc, uint count, uint w, uint h, uint fmt, uint flags)
    {
        if (!IsDummyChain(sc)) PqTeardown("ResizeBuffers");
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int>)_origScResize)(sc, count, w, h, fmt, flags);
        if (!IsDummyChain(sc))
        {
            // Buffer nuovi: quello vecchio non vale piu' e il prossimo fotogramma e' di sicuro da presentare.
            if (sc == _flipChain || _flipChain == IntPtr.Zero) { _flipBack = IntPtr.Zero; _flipLastSrc = IntPtr.Zero; }
            _flipDirty = 1;
            Out.Line($"info flip: ResizeBuffers {w}x{h} buffer={count} fmt={fmt} hr=0x{hr:X8}");
        }
        return hr;
    }

    private static int FlipPresent(IntPtr sc, uint sync, uint flags, IntPtr parameters, bool one)
    {
        const int StillDrawing = unchecked((int)0x887A000A);
        if (_flipBack == IntPtr.Zero || _flipChain != sc)
        {
            // Il back buffer si legge una volta per swapchain (riletto a ogni Present cambiava puntatore e ogni
            // Present sembrava nuova). Dopo ResizeBuffers o un GetBuffer di madVR lo aggiornano i ganci qui sotto.
            _flipChain = sc;
            Guid iid = IID_Tex2D; IntPtr bb;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)GetBufferFn(sc))(sc, 0, &iid, &bb) == 0 && bb != IntPtr.Zero)
            {
                Release(bb);
                if (bb != _flipBack) { _flipBack = bb; _flipLastSrc = IntPtr.Zero; _flipDirty = 1; Out.Line($"info flip: back buffer 0x{bb:X}"); }
            }
        }
        double period = System.Diagnostics.Stopwatch.Frequency / RefreshHz;
        if (_pqTex.Length > 0 && sc == _pqChain) return PqPresent(sc, sync, flags, period);
        bool isNew; uint interval = 1;
        while (true)
        {
            uint now = Vs.Now();
            bool busy = false;
            lock (FlipSync)
            {
                // Senza alcun segnale (ne' fotogrammi video dal ponte ne' copie nel back buffer) non si sa distinguere
                // le ripetizioni: tutte le Present passano, come prima.
                isNew = _flipDirty != 0 || _flipIssued == 0 || (_fdSignals == 0 && _flipIssued > 30);
                while (_flipRealSlots.Count > 0 && _flipRealSlots.Peek() <= now) _flipRealSlots.Dequeue();
                if (_flipLastSlot > now && _flipLastSlot - now >= (uint)FlipDepth) busy = true;
                // Lo strato Metal non accoda: una Present resta ferma finche' il fotogramma precedente non e' a schermo.
                // Un fotogramma nuovo si accetta quindi solo quando tocca a lui (tutti i vsync gia' assegnati sono
                // passati) e parte senza sincronia: arriva al vsync successivo e la chiamata torna subito.
                else if (isNew && (FlipJit ? _flipLastSlot > now : _flipRealSlots.Count >= FlipAhead)) { busy = true; Interlocked.Increment(ref _flipBusyNew); }
                if (!busy)
                {
                    uint slot = Math.Max(now + 1, _flipLastSlot + 1);
                    _flipLastSlot = slot;
                    _flipSlots[++_flipIssued & 63] = slot;
                    if (isNew)
                    {
                        _flipDirty = 0;
                        interval = _flipLastRealSlot == 0 || slot <= _flipLastRealSlot ? 1 : Math.Clamp(slot - _flipLastRealSlot, 1, 4);
                        _flipLastRealSlot = slot;
                        _flipRealSlots.Enqueue(slot);
                    }
                }
            }
            if (!busy) break;
            Interlocked.Increment(ref _flipBusy);
            if (Tl.On) Tl.Ev($"d11 Present RIFIUTATA sync={sync} flags=0x{flags:X} nuovo={isNew}");
            if ((flags & 8) != 0) return StillDrawing; // DXGI_PRESENT_DO_NOT_WAIT: madVR riprova
            Thread.Sleep(1);
        }
        NoteDxgiPresent();
        if (!isNew)
        {
            Interlocked.Increment(ref _flipRepeat);
            if (Tl.On) Tl.Ev($"d11 Present ripetuta (non inviata) sync={sync} slot={_flipLastSlot}");
            return 0;
        }
        Interlocked.Increment(ref _flipReal);
        TakeSnapshot(sc);
        long t = Tl.Now;
        uint f = flags & ~8u;
        if (FlipJit) interval = 0;
        int hr = one
            ? ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int>)_origScPresent1)(sc, interval, f, parameters)
            : ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)_origScPresent)(sc, interval, f);
        if (Tl.On) Tl.Ev($"d11 Present vera sync={sync} slot={_flipLastSlot} call={Tl.Ms(t):0.00}");
        return hr;
    }

    // ------------------------------------------------------------------ presentazione su thread dedicato
    // La coda flip emulata qui sopra presentava dal thread di madVR "appena tocca": il momento dipendeva da quando
    // madVR finiva di disegnare, e a schermo il 17% dei fotogrammi durava 1 o 4 vsync invece di 2-3 (misurato con
    // dist/debug-20261111/prestazioni/cadenza). Ora il fotogramma nuovo viene messo da parte in una texture
    // dell'anello e un thread dedicato lo presenta un vsync prima del suo turno, con durata minima = vsync
    // assegnati al precedente: il vsync esatto lo sceglie Metal, e se la Present deve aspettare aspetta quel thread,
    // non quello che disegna. madVR puo' cosi' anche presentare in anticipo (coda vera, fino a PqMax fotogrammi).
    // CINECORE_FLIP_THREAD=0 torna alla presentazione dal thread di madVR.
    private static readonly bool PqOn = Environment.GetEnvironmentVariable("CINECORE_FLIP_THREAD") != "0";
    private static readonly int PqMax = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_FLIP_QUEUE"), out int pqm) ? Math.Clamp(pqm, 1, 8) : 4;
    private static readonly double PqLead = double.TryParse(Environment.GetEnvironmentVariable("CINECORE_FLIP_LEAD"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pql) ? pql : 1.0;
    private static readonly int PqK = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_FLIP_K"), out int pqk) ? Math.Clamp(pqk, 1, 4) : 2;
    private static readonly int PqSync = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_FLIP_SYNC"), out int pqs) ? pqs : -1;
    private static readonly Guid IID_Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    private static readonly object PqGate = new();
    private static IntPtr _pqCtx, _pqDev, _pqMt, _pqBack, _pqChain;
    private static IntPtr[] _pqTex = Array.Empty<IntPtr>();
    private static bool[] _pqUsed = Array.Empty<bool>();
    private static readonly Queue<(int Ring, uint Slot)> _pq = new();
    private static int _pqGen;
    private static IntPtr _pqShadow;
    private static bool _pqLastWhole;
    private static (uint, uint, uint, uint, bool, uint, uint, uint, uint) _pqLastRegion;
    private static long _pqSkipped;
    private static bool _pqFailed;
    private static Thread? _pqThread;
    private static uint _pqLastDisp;
    private static long _pqSent, _pqLate, _pqDropped, _pqMaxQ, _pqWaitUs, _pqWaitMaxUs;

    /// <summary>
    /// Prepara per la swapchain di madVR: back buffer ombra (quello che madVR riceve e in cui compone), anello di
    /// fotogrammi in attesa, protezione multithread e thread che presenta. Solo dal thread di madVR (GetBuffer).
    /// Torna l'ombra, o zero se non si puo' (si resta alla presentazione dal thread di madVR).
    /// </summary>
    private static IntPtr PqSetup(IntPtr sc, IntPtr real)
    {
        if (!PqOn || _pqFailed) return IntPtr.Zero;
        if (_pqTex.Length > 0 && _pqChain == sc && _pqBack == real) return _pqShadow;
        lock (PqGate)
        {
            PqTeardownLocked();
            IntPtr dev = IntPtr.Zero, ctx = IntPtr.Zero, mt = IntPtr.Zero, shadow = IntPtr.Zero; var tex = new IntPtr[PqMax + 1];
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)V(real, 3))(real, &dev);
                if (dev == IntPtr.Zero) throw new InvalidOperationException("GetDevice");
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)V(dev, 40))(dev, &ctx);
                if (ctx == IntPtr.Zero) throw new InvalidOperationException("GetImmediateContext");
                Guid iid = IID_Multithread;
                if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(ctx, 0))(ctx, &iid, &mt) != 0 || mt == IntPtr.Zero)
                    if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(dev, 0))(dev, &iid, &mt) != 0 || mt == IntPtr.Zero) throw new InvalidOperationException("ID3D11Multithread assente");
                ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)V(mt, 5))(mt, 1);
                Tex2DDesc d; ((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, void>)V(real, 10))(real, &d);
                var rd = d; rd.MipLevels = 1; rd.ArraySize = 1; rd.SampleCount = 1; rd.SampleQuality = 0; rd.Usage = 0; rd.BindFlags = d.BindFlags | 0x8 | 0x20; rd.CpuAccess = 0; rd.MiscFlags = 0;
                for (int i = -1; i < tex.Length; i++)
                {
                    IntPtr t = IntPtr.Zero;
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)V(dev, 5))(dev, &rd, IntPtr.Zero, &t);
                    if (hr != 0 || t == IntPtr.Zero) throw new InvalidOperationException($"CreateTexture2D hr=0x{hr:X8} bind=0x{rd.BindFlags:X}");
                    if (i < 0) shadow = t; else tex[i] = t;
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(real, 1))(real);
                lock (FlipSync)
                {
                    _pqTex = tex; _pqUsed = new bool[tex.Length]; _pq.Clear(); _pqGen++;
                    _pqFull = new bool[tex.Length]; _pqDirect = -1; _pqShadowStale = false; _pqDirectRegionSet = false;
                    _pqDev = dev; _pqMt = mt; _pqCtx = ctx; _pqBack = real; _pqShadow = shadow; _pqChain = sc; _pqLastDisp = 0;
                }
                Out.Line($"info flip: presentazione su thread dedicato, back buffer ombra + anello di {tex.Length} texture {d.Width}x{d.Height} fmt={d.Format} bind=0x{d.BindFlags:X}");
                if (_pqThread == null)
                {
                    _pqThread = new Thread(PqLoop) { IsBackground = true, Name = "cinecore-present", Priority = ThreadPriority.AboveNormal };
                    _pqThread.Start();
                }
                return shadow;
            }
            catch (Exception ex)
            {
                _pqFailed = true;
                foreach (var t in tex) Release(t);
                Release(shadow); Release(mt); Release(ctx); Release(dev);
                Out.Line("info flip: thread dedicato non disponibile (" + ex.Message + "), si presenta dal thread di madVR");
                return IntPtr.Zero;
            }
        }
    }

    private static void PqTeardown(string why)
    {
        if (_pqTex.Length == 0) return;
        lock (PqGate) PqTeardownLocked();
        Out.Line("info flip: anello liberato (" + why + ")");
    }
    private static void PqTeardownLocked()
    {
        IntPtr[] tex; IntPtr dev, mt, ctx, back, shadow;
        lock (FlipSync)
        {
            tex = _pqTex; dev = _pqDev; mt = _pqMt; ctx = _pqCtx; back = _pqBack; shadow = _pqShadow;
            _pqTex = Array.Empty<IntPtr>(); _pqUsed = Array.Empty<bool>(); _pq.Clear(); _pqGen++;
            _pqFull = Array.Empty<bool>(); _pqDirect = -1; _pqShadowStale = false; _pqDirectRegionSet = false;
            _pqDev = IntPtr.Zero; _pqMt = IntPtr.Zero; _pqCtx = IntPtr.Zero; _pqBack = IntPtr.Zero; _pqShadow = IntPtr.Zero; _pqChain = IntPtr.Zero;
            _flipDirty = 1; _flipLastSrc = IntPtr.Zero;
            if (_flipBack == shadow) _flipBack = IntPtr.Zero;
        }
        foreach (var t in tex) Release(t);
        Release(shadow); Release(back); Release(mt); Release(ctx); Release(dev);
    }

    // ------------------------------------------------------------------ copia diretta nell'anello
    // Il fotogramma finito veniva copiato tre volte: madVR -> ombra, ombra -> anello, anello -> back buffer. In
    // risparmio energetico il traffico di memoria abbassa la frequenza della GPU: la copia di madVR ora va dritta
    // nel posto dell'anello che sara' presentato (una copia in meno a fotogramma). Vale solo per la copia video
    // (texture finale di madVR, stessa zona di prima) in un posto gia' riempito per intero; OSD, cambio di zona o
    // posti non ancora interi tornano al percorso con l'ombra. Attivo con la consegna in catena; CINECORE_FLIP_DIRECT=0 lo spegne.
    private static readonly bool PqDirect = FastOption("CINECORE_FLIP_DIRECT");
    private static int _pqDirect = -1, _pqDirectHold;
    private static bool[] _pqFull = Array.Empty<bool>();
    private static bool _pqShadowStale, _pqDirectRegionSet;
    private static (uint, uint, uint, uint, bool, uint, uint, uint, uint) _pqDirectRegion;
    private static long _pqDirectN, _pqDirectMiss;
    private static long _cpDirect, _cpShadow, _cpOther, _cpRing, _cpBack, _cpBridge, _cpHold; private static int _cpOtherFmt; private static uint _cpOtherW;
    private static readonly long[] _pqWhy = new long[6]; private static int _pqWhyFmt;
    internal static void PqNoteOsd() { if (PqDirect) Volatile.Write(ref _pqDirectHold, 8); }

    /// <summary>Texture dell'anello in cui far finire la copia video di madVR al posto dell'ombra, o zero.</summary>
    private static IntPtr PqDirectTarget(IntPtr dst, IntPtr src, (uint, uint, uint, uint, bool, uint, uint, uint, uint) region)
    {
        if (!PqDirect || _pqTex.Length == 0 || dst != _pqShadow) { _pqWhy[0]++; return IntPtr.Zero; }
        // La sorgente video e' la texture finale a 10 bit (condivisa con D3D9, riempita da madVR in D3D11). L'OSD si
        // riconosce a parte: e' D3D9 a disegnarlo in quelle texture (MarkWritten -> PqNoteOsd).
        if (!ByResource11.IsEmpty && ByResource11.TryGetValue(src, out var sp) && sp.Format9 == 113) { _pqWhy[1]++; _pqWhyFmt = sp.Format9; return IntPtr.Zero; }
        lock (FlipSync)
        {
            if (!_pqDirectRegionSet || region != _pqDirectRegion)
            {
                // Zona nuova: i posti dell'anello hanno fuori zona il contenuto vecchio.
                _pqDirectRegion = region; _pqDirectRegionSet = true;
                Out.Line($"info flip: zona della copia video = sub {region.Item1}/{region.Item4} dest {region.Item2},{region.Item3} riquadro={(region.Item5 ? $"{region.Item6},{region.Item7}-{region.Item8},{region.Item9}" : "intero")}");
                for (int i = 0; i < _pqFull.Length; i++) _pqFull[i] = false;
                _pqWhy[2]++;
            }
            if (Volatile.Read(ref _pqDirectHold) > 0) { _pqWhy[3]++; return IntPtr.Zero; }
            if (_flipDirty == 0) { _pqWhy[4]++; return IntPtr.Zero; }
            if (_pqDirect < 0)
                for (int i = 0; i < _pqUsed.Length; i++) if (!_pqUsed[i] && _pqFull[i]) { _pqUsed[i] = true; _pqDirect = i; break; }
            if (_pqDirect < 0) { _pqWhy[5]++; return IntPtr.Zero; }
            _pqShadowStale = true;
            return _pqTex[_pqDirect];
        }
    }

    // Nota (12/11): la copia "texture privata di madVR -> texture finale condivisa" (una per fotogramma) NON si puo'
    // togliere. Provati e falliti: alias della sorgente (video nero), copia anticipata nell'anello (video nero),
    // salto dopo le prime volte (immagine sporca, sfarfallio). madVR scrive ancora nella texture finale dopo quella copia.
    private static void NoteOtherCopy(IntPtr dst)
    {
        try { Tex2DDesc d; ((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, void>)V(dst, 10))(dst, &d); _cpOtherFmt = (int)d.Format; _cpOtherW = d.Width; } catch { }
    }

    // Diagnostica (CINECORE_FLIP_TRACE=1): una riga per ogni copia nel back buffer, Present e invio a schermo.
    private static readonly bool FlipTrace = Environment.GetEnvironmentVariable("CINECORE_FLIP_TRACE") == "1";
    private static void Ft(string s) { if (FlipTrace) Out.Line($"ft {System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency % 1000000} {s}"); }

    private static int PqAllocLocked()
    {
        for (int i = 0; i < _pqUsed.Length; i++) if (!_pqUsed[i]) { _pqUsed[i] = true; return i; }
        return -1;
    }

    private static int PqPresent(IntPtr sc, uint sync, uint flags, double period)
    {
        const int StillDrawing = unchecked((int)0x887A000A);
        bool isNew; int ring = -1; uint slot = 0; bool direct = false;
        while (true)
        {
            uint now = Vs.Now();
            bool busy = false;
            lock (FlipSync)
            {
                isNew = _flipDirty != 0 || _flipIssued == 0 || (_fdSignals == 0 && _flipIssued > 30);
                direct = false;
                // Copia diretta: il fotogramma e' gia' nel suo posto dell'anello. Se l'ombra non e' aggiornata e nessuna
                // copia e' arrivata, non c'e' nulla di nuovo da mostrare (mai presentare l'ombra vecchia).
                if (isNew && _pqDirect < 0 && _pqShadowStale) { isNew = false; _flipDirty = 0; Interlocked.Increment(ref _pqDirectMiss); }
                if (_flipLastSlot > now && _flipLastSlot - now >= (uint)FlipDepth) busy = true;
                else if (isNew && _pqDirect >= 0) { if (_pq.Count >= PqMax) { busy = true; Interlocked.Increment(ref _flipBusyNew); } else { ring = _pqDirect; direct = true; } }
                else if (isNew && (_pq.Count >= PqMax || (ring = PqAllocLocked()) < 0)) { busy = true; Interlocked.Increment(ref _flipBusyNew); }
                if (!busy)
                {
                    if (direct) _pqDirect = -1;
                    if (isNew && PqDirect && Volatile.Read(ref _pqDirectHold) > 0) Interlocked.Decrement(ref _pqDirectHold);
                    slot = Math.Max(now + 1, _flipLastSlot + 1);
                    _flipLastSlot = slot;
                    _flipSlots[++_flipIssued & 63] = slot;
                    if (isNew) _flipDirty = 0;
                }
            }
            if (!busy) break;
            Interlocked.Increment(ref _flipBusy);
            if (Tl.On) Tl.Ev($"d11 Present RIFIUTATA sync={sync} flags=0x{flags:X} nuovo={isNew} coda={_pq.Count}");
            if ((flags & 8) != 0) return StillDrawing;
            Thread.Sleep(1);
        }
        NoteDxgiPresent();
        if (FlipTrace) Ft($"Present nuovo={isNew} diretto={direct} posto={ring} slot={slot} coda={_pq.Count}");
        if (!isNew)
        {
            Interlocked.Increment(ref _flipRepeat);
            if (Tl.On) Tl.Ev($"d11 Present ripetuta (non inviata) sync={sync} slot={slot}");
            return 0;
        }
        Interlocked.Increment(ref _flipReal);
        // Il fotogramma composto da madVR nell'ombra viene messo da parte: lo presentera' il thread dedicato.
        if (direct) Interlocked.Increment(ref _pqDirectN);
        else
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(_pqCtx, _pqTex[ring], _pqShadow);
            Interlocked.Increment(ref _cpRing);
            if (PqDirect) lock (FlipSync) if (ring < _pqFull.Length) _pqFull[ring] = true;
        }
        int depth;
        lock (FlipSync) { _pq.Enqueue((ring, slot)); depth = _pq.Count; Monitor.PulseAll(FlipSync); }
        if (depth > Interlocked.Read(ref _pqMaxQ)) Interlocked.Exchange(ref _pqMaxQ, depth);
        if (Tl.On) Tl.Ev($"d11 Present accodata sync={sync} slot={slot} coda={depth}");
        return 0;
    }

    private static void PqLoop()
    {
        long freq = System.Diagnostics.Stopwatch.Frequency;
        while (true)
        {
            try
            {
                (int Ring, uint Slot) e; int gen;
                lock (FlipSync)
                {
                    while (_pq.Count == 0) Monitor.Wait(FlipSync);
                    e = _pq.Peek(); gen = _pqGen;
                }
                // Con il modulo nativo (griglia vera): K vsync prima, a meta' intervallo; il modulo prepara il drawable
                // in anticipo e lo gira a schermo esattamente al vsync dello slot. Senza: un vsync prima, presentazione
                // immediata (arriva quando la GPU ha finito).
                double period = Vs.PeriodTicks;
                bool timed = Vs.Real && PqSync < 0;
                uint want = e.Slot;
                e.Slot = PqSmoothSlot(e.Slot, freq / period);
                long target = Vs.Start(e.Slot) - (long)((timed ? PqK - 0.5 : PqLead) * period);
                while (true)
                {
                    long left = target - System.Diagnostics.Stopwatch.GetTimestamp();
                    if (left <= 0 || Volatile.Read(ref _pqGen) != gen) break;
                    if (left * 1000 > freq * 2) Thread.Sleep(1); else Thread.Yield();
                }
                lock (PqGate)
                {
                    uint nowSlot = Vs.Now();
                    IntPtr tex, back, ctx, mt, chain; uint d;
                    lock (FlipSync)
                    {
                        if (_pqGen != gen || _pq.Count == 0 || _pq.Peek() != (e.Ring, want)) continue;
                        _pq.Dequeue();
                        // In ritardo e il successivo tocca gia': questo non si mostra piu'.
                        if (_pq.Count > 0 && _pq.Peek().Slot <= nowSlot + 1 && e.Slot <= nowSlot) { _pqUsed[e.Ring] = false; Interlocked.Increment(ref _pqDropped); if (FlipTrace) Ft($"scartato posto={e.Ring} slot={e.Slot}"); continue; }
                        tex = _pqTex[e.Ring]; back = _pqBack; ctx = _pqCtx; mt = _pqMt; chain = _pqChain;
                        uint est = Math.Max(e.Slot, nowSlot + 1);
                        // Intervallo di sincronia = fra quanti vsync va mostrato (lo interpreta il modulo nativo);
                        // senza modulo 0 = subito.
                        d = timed ? (uint)Math.Clamp((long)est - nowSlot, 1, 4) : 0;
                        if (timed && !Vs.PushTarget(est)) d = 0;
                        _pqLastDisp = est;
                        if (est > e.Slot) Interlocked.Increment(ref _pqLate);
                        if (_pqSeqSlot != 0) { uint ds = e.Slot - _pqSeqSlot; _pqSeq.Append(ds > 9 ? '+' : (char)('0' + ds)); }
                        _pqSeqSlot = e.Slot;
                    }
                    ((delegate* unmanaged[Stdcall]<IntPtr, void>)V(mt, 3))(mt);
                    try
                    {
                        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, back, tex);
                        Interlocked.Increment(ref _cpBack);
                        if (FlipTrace) Ft($"invio posto={e.Ring} slot={e.Slot} adesso={nowSlot}");
                        TakeSnapshot(chain);
                    }
                    finally { ((delegate* unmanaged[Stdcall]<IntPtr, void>)V(mt, 4))(mt); }
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)_origScPresent)(chain, PqSync >= 0 ? (uint)PqSync : d, 0);
                    long us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000000 / freq;
                    if (FlipTrace) Ft($"inviato posto={e.Ring} durataPresent={us / 1000.0:0.0}ms d={d}");
                    Interlocked.Add(ref _pqWaitUs, us); if (us > Interlocked.Read(ref _pqWaitMaxUs)) Interlocked.Exchange(ref _pqWaitMaxUs, us);
                    Interlocked.Increment(ref _pqSent);
                    if (hr != 0 && _pqSent < 50) Out.Line($"info flip: Present dal thread dedicato hr=0x{hr:X8}");
                    if (Tl.On) Tl.Ev($"pq Present slot={e.Slot} durata={d} call={us / 1000.0:0.00}");
                    lock (FlipSync) if (_pqGen == gen) _pqUsed[e.Ring] = false;
                }
            }
            catch (Exception ex) { Out.Line("info flip: thread dedicato " + ex.Message); Thread.Sleep(50); }
        }
    }
    private static readonly System.Text.StringBuilder _pqSeq = new();
    private static uint _pqSeqSlot;
    // Livellamento della cadenza. madVR, al limite della GPU, a volte consegna un fotogramma un vsync dopo il suo
    // turno e poi recupera (durate 3-1-3 o 4-1 invece di 3-2-3-2: scatti). Qui i fotogrammi vengono ridistribuiti
    // sulla cadenza ideale (durata del fotogramma in vsync, es. 2,5025 per 23,976 su 60 Hz): ognuno non prima dello
    // slot chiesto da madVR e al piu' PqSmoothMax vsync dopo; il ritardo accumulato si riassorbe piano.
    // CINECORE_FLIP_SMOOTH=0 lo spegne.
    private static readonly bool PqSmooth = Environment.GetEnvironmentVariable("CINECORE_FLIP_SMOOTH") != "0";
    private const double PqSmoothMax = 2.6;
    private static double _pqX; private static uint _pqXSlot, _pqXDisp; private static long _frameTicks100ns, _pqShifted;
    internal static void SetFrameDurationForFlip(long ticks) => Volatile.Write(ref _frameTicks100ns, ticks);
    // Distanze (in vsync) fra le ultime presentazioni chieste da madVR: dicono se presenta un fotogramma per volta.
    private static readonly uint[] _pqGaps = new uint[32];
    private static int _pqGapN;
    private static bool _pqOnePerFrame;
    private static uint PqSmoothSlot(uint slot, double hz)
    {
        if (_pqXSlot == slot) return _pqXDisp; // stesso fotogramma riesaminato
        double f = Volatile.Read(ref _frameTicks100ns) / 1e7 * hz; // durata del fotogramma in vsync
        uint disp = slot;
        if (_pqXSlot != 0 && slot > _pqXSlot && slot - _pqXSlot < 16) _pqGaps[_pqGapN++ & 31] = slot - _pqXSlot;
        // Il livellamento vale solo se madVR presenta un fotogramma per volta (il caso del limite della GPU, 3-1-3).
        // A 60 Hz madVR presenta anche le ripetizioni (~42 al secondo per un film a 24, molte distanze di 1 vsync):
        // distanziandole di 2,5 vsync il livellamento andava fuori passo e a schermo uscivano 4-1 al posto del 3:2
        // (misurato il 19/11 registrando lo schermo; spento: 3:2 perfetto come mpv). Si accende solo dopo 32
        // presentazioni con al massimo una distanza di 1 vsync, si spegne appena le distanze di 1 superano il 15%.
        int seen = Math.Min(_pqGapN, 32), ones = 0;
        for (int i = 0; i < seen; i++) if (_pqGaps[i] <= 1) ones++;
        if (_pqOnePerFrame) { if (seen > 0 && ones > seen * 0.15) { _pqOnePerFrame = false; _pqX = 0; } }
        else if (seen == 32 && ones <= 1) _pqOnePerFrame = true;
        bool oneFramePerPresent = _pqOnePerFrame;
        if (PqSmooth && f >= 1.5 && f <= 8 && oneFramePerPresent)
        {
            double x = _pqX + f;
            if (_pqX == 0 || slot > _pqXSlot + 3 * f || x > slot + PqSmoothMax) x = slot;   // inizio, pausa/ricerca, fuori passo
            else if (x < slot) x = slot;                                                   // consegnato tardi: da qui slitta tutto
            else x = Math.Max(slot, x - 0.004);                                            // riassorbe il ritardo (~1 vsync ogni 10 s)
            _pqX = x; disp = (uint)Math.Round(x);
            if (disp <= _pqXDisp && _pqXDisp - disp < 8) disp = _pqXDisp + 1;
            if (disp != slot) Interlocked.Increment(ref _pqShifted);
        }
        else _pqX = 0;
        _pqXSlot = slot; _pqXDisp = disp;
        return disp;
    }

    private static string PqStats()
    {
        if (_pqTex.Length == 0) return "";
        string seq; lock (FlipSync) { seq = _pqSeq.ToString(); _pqSeq.Clear(); }
        long sent = Interlocked.Exchange(ref _pqSent, 0), us = Interlocked.Exchange(ref _pqWaitUs, 0);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $" copie: diretta={Interlocked.Exchange(ref _cpDirect, 0)} ombra={Interlocked.Exchange(ref _cpShadow, 0)} anello={Interlocked.Exchange(ref _cpRing, 0)} back={Interlocked.Exchange(ref _cpBack, 0)} ponte16F={Interlocked.Exchange(ref _cpBridge, 0)} letturaDiretta16F={Interlocked.Exchange(ref _srvSubstN, 0)}/{Interlocked.Exchange(ref _srvBound, 0)} tenuta={Interlocked.Exchange(ref _cpHold, 0)} altreDiMadVR={Interlocked.Exchange(ref _cpOther, 0)}(fmt {_cpOtherFmt} larg {_cpOtherW}) taciuti={Interlocked.Exchange(ref _fdAsyncQuiet, 0)} thread: diretti={Interlocked.Exchange(ref _pqDirectN, 0)} mancati={Interlocked.Exchange(ref _pqDirectMiss, 0)}{(PqDirect ? $" [no: altraDest={Interlocked.Exchange(ref _pqWhy[0], 0)} sorgCondivisa={Interlocked.Exchange(ref _pqWhy[1], 0)}(fmt {_pqWhyFmt}) zonaNuova={Interlocked.Exchange(ref _pqWhy[2], 0)} attesa={Interlocked.Exchange(ref _pqWhy[3], 0)} nonNuovo={Interlocked.Exchange(ref _pqWhy[4], 0)} nessunPosto={Interlocked.Exchange(ref _pqWhy[5], 0)}]" : "")} inviati={sent} tardi={Interlocked.Exchange(ref _pqLate, 0)} scartati={Interlocked.Exchange(ref _pqDropped, 0)} codaMax={Interlocked.Exchange(ref _pqMaxQ, 0)} livellati={Interlocked.Exchange(ref _pqShifted, 0)} copieSaltate={Interlocked.Exchange(ref _pqSkipped, 0)} presentMedia={(sent > 0 ? us / 1000.0 / sent : 0):0.0}ms presentMax={Interlocked.Exchange(ref _pqWaitMaxUs, 0) / 1000.0:0.0}ms vsync={(Vs.Real ? "veri" : "nominali")} programmati={Vs.Hooked} gpuTardi={Vs.Late} seq={seq}");
    }

    // Prova: intervallo di sincronia imposto alle Present di madVR (0 = la chiamata non aspetta il vsync).
    private static readonly int PresentSyncOverride = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_PRESENT_SYNC"), out int psv) ? psv : -1;
    // Frequenza dello schermo su cui gira madVR: la coda flip emulata conta i vsync con questa (60 sul pannello
    // interno, ma 120 su un ProMotion o 24/50 su un proiettore). CINECORE_REFRESH_HZ la impone.
    private static readonly double RefreshHz = ReadRefreshHz();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra; public uint Fields;
        public int PosX, PosY; public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels; public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DevMode dm);
    private static double ReadRefreshHz()
    {
        if (double.TryParse(Environment.GetEnvironmentVariable("CINECORE_REFRESH_HZ"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v >= 20 && v <= 480) { Out.Line($"info flip: frequenza schermo imposta {v:0.###} Hz"); Vs.SetNominal(v); return v; }
        try
        {
            var dm = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(null, -1, ref dm) && dm.DisplayFrequency >= 20 && dm.DisplayFrequency <= 480)
            {
                // 59/23/29/119 sono le frequenze "NTSC" arrotondate per difetto.
                double hz = dm.DisplayFrequency switch { 59 => 59.94, 23 => 23.976, 29 => 29.97, 119 => 119.88, _ => dm.DisplayFrequency };
                Out.Line(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"info flip: frequenza schermo {hz:0.###} Hz"));
                Vs.SetNominal(hz);
                return hz;
            }
        }
        catch { }
        return 60.0;
    }
    private static readonly string StatsMode = Environment.GetEnvironmentVariable("CINECORE_DXGI_STATS") ?? "simple";
    private static uint _lastAssignedVsync;
    private static readonly bool ProbeOn = Environment.GetEnvironmentVariable("CINECORE_PROBE") == "1";
    private static readonly bool SpikeLog = Environment.GetEnvironmentVariable("CINECORE_SPIKE_LOG") == "1";

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int ScGetFrameStatistics(IntPtr sc, FrameStats* st)
    { using var __hook = HookTrace.Enter("ScGetFrameStatistics");
        Out.Tid("ScGetFrameStatistics");
        Interlocked.Increment(ref _statsCalls);
        if (StatsMode == "stub") return ((delegate* unmanaged[Stdcall]<IntPtr, FrameStats*, int>)_origScStats)(sc, st);
        if (st == null) return unchecked((int)0x80004003);
        if (Flip)
        {
            uint nowF = Vs.Now();
            lock (FlipSync)
            {
                // Ultima Present gia' arrivata a schermo: quella con lo slot piu' recente non oltre il vsync corrente.
                long shown = 0; uint shownSlot = nowF;
                for (long i = _flipIssued; i > 0 && i > _flipIssued - 64; i--)
                    if (_flipSlots[i & 63] <= nowF) { shown = i; shownSlot = _flipSlots[i & 63]; break; }
                if (shown == 0 && _flipIssued > 64) shown = _flipIssued - 64;
                st->PresentCount = (uint)shown;
                st->PresentRefreshCount = shownSlot;
            }
            st->SyncRefreshCount = nowF;
            st->SyncQPCTime = Vs.Start(nowF);
            st->SyncGPUTime = 0;
            return 0;
        }
        if (StatsMode == "queue")
        {
            // Coda virtuale a vsync: ogni Present occupa un vsync distinto, a partire da quello successivo al suo arrivo.
            long freqQ = System.Diagnostics.Stopwatch.Frequency, nowQ = System.Diagnostics.Stopwatch.GetTimestamp();
            double periodQ = freqQ / RefreshHz;
            uint syncQ = (uint)(nowQ / periodQ);
            uint lastVs;
            lock (MetricSync) lastVs = _lastAssignedVsync;
            st->PresentCount = (uint)Interlocked.Read(ref _dxgiPresents);
            st->PresentRefreshCount = lastVs == 0 ? syncQ : Math.Min(lastVs, syncQ);
            st->SyncRefreshCount = syncQ;
            st->SyncQPCTime = (long)(syncQ * periodQ);
            st->SyncGPUTime = 0;
            return 0;
        }
        long freq = System.Diagnostics.Stopwatch.Frequency, now = System.Diagnostics.Stopwatch.GetTimestamp();
        double period = freq / RefreshHz;
        uint sync = (uint)(now / period);
        long last = Interlocked.Read(ref _lastDxgiPresentQpc);
        st->PresentCount = (uint)Interlocked.Read(ref _dxgiPresents);
        st->PresentRefreshCount = last == 0 ? sync : (uint)(last / period) + 1;
        st->SyncRefreshCount = sync;
        st->SyncQPCTime = (long)(sync * period);
        st->SyncGPUTime = 0;
        return 0;
    }

    public static string TakeMetrics()
    {
        lock (MetricSync)
        {
            string r = $"present={_mCount} intMedio={(_mCount > 0 ? _mSum / _mCount : 0):0.0}ms intMax={_mMax:0.0}ms >25ms={_mLate25} >50ms={_mLate50} copie={_cCount} copiaMedia={(_cCount > 0 ? _cSum / _cCount : 0):0.0}ms copiaMax={_cMax:0.0}ms [attesaPrimaDelUso={(_cCount > 0 ? _rtSum / _cCount : 0):0.0} lock={(_cCount > 0 ? _cpySum / _cCount : 0):0.0} carico={(_cCount > 0 ? (_cSum - _rtSum - _cpySum) / _cCount : 0):0.0}]";
            if (_aQueued + _aUsed > 0)
            {
                r += $" async: accodati={_aQueued} usati={_aUsed} attesi={_aWaited} nonPronti={_aStale} anelloPieno={Interlocked.Exchange(ref _aBusy, 0)} recuperi={Interlocked.Exchange(ref _aCatchup, 0)} fallite={Interlocked.Exchange(ref _aFailed, 0)} mantenuti={Interlocked.Exchange(ref _aHeld, 0)} saltati={_aSkip} poll={_aPolls} etaMedia={(_aLagN > 0 ? _aAgeMs / _aLagN : 0):0.0}ms ritardo={(_aLagN > 0 ? (double)_aLagSum / _aLagN : 0):0.00}fot";
                _aQueued = _aUsed = _aStale = _aSkip = _aPolls = _aLagSum = _aLagN = _aWaited = 0; _aAgeMs = 0;
            }
            if (_pboDone + _pboSkipped + _pboStale > 0)
                r += $" pbo: pronti={_pboDone} ({(_pboDone > 0 ? _pboMs / _pboDone : 0):0.0}ms, copia {(_pboDone > 0 ? _pboCopyMs / _pboDone : 0):0.0}ms) saltati={_pboSkipped} ripetuti={_pboStale} ritardo={(_pboLagN > 0 ? (double)_pboLagSum / _pboLagN : 0):0.00}fot lock={(_pboDone > 0 ? _pboLockMs / _pboDone : 0):0.0}/{_pboLockMax:0.0}ms";
            _pboLagSum = _pboLagN = 0; _pboLockMs = _pboLockMax = 0;
            _pboDone = _pboSkipped = _pboStale = 0; _pboMs = _pboCopyMs = 0;
            if (_oRev + _oDl > 0) r += $" osd: inverse={_oRev} ({(_oRev > 0 ? _oRevMs / _oRev : 0):0.0}ms) riletture={_oDl} ({(_oDl > 0 ? _oDlMs / _oDl : 0):0.0}ms) inRitardo={_oLate} [lettura={(_oRev > 0 ? _oA / _oRev : 0):0.0} lock9={(_oRev > 0 ? _oB / _oRev : 0):0.0} conv={(_oRev > 0 ? _oC / _oRev : 0):0.0} unmap={(_oRev > 0 ? _oU / _oRev : 0):0.0} d3d9={(_oRev > 0 ? _oD / _oRev : 0):0.0}]";
            _oRev = _oDl = _oLate = 0; _oRevMs = _oDlMs = _oA = _oB = _oC = _oU = _oD = 0;
            _mCount = _mLate25 = _mLate50 = 0; _mSum = _mMax = 0; _cCount = 0; _cSum = _cMax = _rtSum = _cpySum = 0;
            return r;
        }
    }

    public static string DxgiStats() => $"dxgiPresent={Interlocked.Read(ref _dxgiPresents)} stats={Interlocked.Read(ref _statsCalls)} {FlipStats()}";

    private static readonly Guid IID_Tex2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>Comando "snap": il prossimo back buffer presentato da madVR finisce in un file PPM (8 bit).</summary>
    public static string? SnapPath;

    private static void TakeSnapshot(IntPtr sc)
    {
        string? path = Interlocked.Exchange(ref SnapPath, null);
        if (path == null) return;
        IntPtr tex = IntPtr.Zero, dev = IntPtr.Zero, ctx = IntPtr.Zero, staging = IntPtr.Zero;
        try
        {
            Guid iid = IID_Tex2D;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)GetBufferFn(sc))(sc, 0, &iid, &tex) != 0) { Out.Line("snap errore GetBuffer"); return; }
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)V(tex, 3))(tex, &dev);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)V(dev, 40))(dev, &ctx);
            Tex2DDesc d; ((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, void>)V(tex, 10))(tex, &d);
            var sd = d; sd.Usage = 3; sd.BindFlags = 0; sd.CpuAccess = 0x20000; sd.MiscFlags = 0;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)V(dev, 5))(dev, &sd, IntPtr.Zero, &staging) != 0) { Out.Line("snap errore staging"); return; }
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, staging, tex);
            MappedSub m;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSub*, int>)V(ctx, 14))(ctx, staging, 0, 1, 0, &m) != 0) { Out.Line("snap errore Map"); return; }
            if (path.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
            {
                // Grezzo: righe consecutive di Width*4 byte (per l'analisi dei bit).
                var raw = new byte[d.Width * d.Height * 4];
                for (uint y = 0; y < d.Height; y++) Marshal.Copy((IntPtr)((byte*)m.Data + y * m.RowPitch), raw, (int)(y * d.Width * 4), (int)d.Width * 4);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(ctx, 15))(ctx, staging, 0);
                System.IO.File.WriteAllBytes(path, raw);
                Out.Line($"snap {d.Width}x{d.Height} fmt={d.Format} {path}");
                return;
            }
            var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{d.Width} {d.Height}\n255\n");
            var img = new byte[header.Length + d.Width * d.Height * 3];
            header.CopyTo(img, 0);
            int o = header.Length;
            for (uint y = 0; y < d.Height; y++)
            {
                uint* row = (uint*)((byte*)m.Data + y * m.RowPitch);
                for (uint x = 0; x < d.Width; x++)
                {
                    uint v = row[x]; byte r, g, b;
                    if (d.Format == 24) { r = (byte)((v & 0x3FF) >> 2); g = (byte)(((v >> 10) & 0x3FF) >> 2); b = (byte)(((v >> 20) & 0x3FF) >> 2); }
                    else if (d.Format == 87 || d.Format == 88 || d.Format == 91) { b = (byte)v; g = (byte)(v >> 8); r = (byte)(v >> 16); }
                    else { r = (byte)v; g = (byte)(v >> 8); b = (byte)(v >> 16); }
                    img[o++] = r; img[o++] = g; img[o++] = b;
                }
            }
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(ctx, 15))(ctx, staging, 0);
            System.IO.File.WriteAllBytes(path, img);
            Out.Line($"snap {d.Width}x{d.Height} fmt={d.Format} {path}");
        }
        catch (Exception ex) { Out.Line("snap errore " + ex.Message); }
        finally { Release(staging); Release(ctx); Release(dev); Release(tex); }
    }

    /// <summary>Diagnostica: media di alcuni pixel del back buffer che madVR sta per presentare e delle texture ponte.</summary>
    private static void ProbeBackBuffer(IntPtr sc, long n)
    {
        IntPtr tex = IntPtr.Zero, dev = IntPtr.Zero, ctx = IntPtr.Zero;
        try
        {
            Guid iid = IID_Tex2D;
            int hr0 = ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)GetBufferFn(sc))(sc, 0, &iid, &tex);
            if (hr0 != 0) { Out.Line($"info bridge: probe GetBuffer hr=0x{hr0:X8}"); return; }
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)V(tex, 3))(tex, &dev);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)V(dev, 40))(dev, &ctx);
            Out.Line($"info bridge: Present #{n} backbuffer {ProbeTexture(dev, ctx, tex)}");
            Pair[] list; lock (Sync) list = Pairs.ToArray();
            int shown = 0;
            foreach (var p in list) if (p.Texture11 != IntPtr.Zero && p.Device11 == dev && (p.Format9 == 113 || shown++ < 2)) Out.Line($"info bridge:   ponte fmt={p.Format9} srv={BySrvCount(p)} caricati={p.Uploads} -> {ProbeTexture(dev, ctx, p.Texture11)}");
        }
        catch (Exception ex) { Out.Line("info bridge: probe " + ex.Message); }
        finally { Release(ctx); Release(dev); Release(tex); }
    }

    private static string ProbeTexture(IntPtr dev, IntPtr ctx, IntPtr tex)
    {
        IntPtr staging = IntPtr.Zero;
        try
        {
            Tex2DDesc d;
            ((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, void>)V(tex, 10))(tex, &d);
            var sd = d; sd.Usage = 3 /*STAGING*/; sd.BindFlags = 0; sd.CpuAccess = 0x20000 /*READ*/; sd.MiscFlags = 0;
            int hr1 = ((delegate* unmanaged[Stdcall]<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)V(dev, 5))(dev, &sd, IntPtr.Zero, &staging);
            if (hr1 != 0) return $"staging hr=0x{hr1:X8}";
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)_origCopyResource)(ctx, staging, tex);
            MappedSub m;
            int hr2 = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSub*, int>)V(ctx, 14))(ctx, staging, 0, 1 /*READ*/, 0, &m);
            if (hr2 != 0) return $"Map hr=0x{hr2:X8}";
            ulong sum = 0; int cnt = 0;
            for (uint y = d.Height / 4; y < d.Height; y += Math.Max(1u, d.Height / 4))
            {
                byte* row = (byte*)m.Data + y * m.RowPitch;
                for (uint i = 0; i < m.RowPitch; i += Math.Max(1u, m.RowPitch / 32)) { sum += row[i]; cnt++; }
            }
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(ctx, 15))(ctx, staging, 0);
            return $"{d.Width}x{d.Height} fmt={d.Format} media byte={sum / (ulong)Math.Max(1, cnt)}";
        }
        finally { Release(staging); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct MappedSub { public IntPtr Data; public uint RowPitch, DepthPitch; }
}
