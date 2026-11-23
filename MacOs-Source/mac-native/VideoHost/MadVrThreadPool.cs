#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace CinecoreVideoHost;

/// <summary>
/// madVR divide un lavoro CPU per fotogramma in parti e per ognuna crea un thread che vive pochi millisecondi
/// (misurato: ~200 CreateThread al secondo a 24 fps, tutti dalla stessa funzione). Su Windows costa poco; sotto
/// Wine + emulazione ogni thread nuovo e' caro (pila, DllMain di tutte le DLL, traduzione), e in risparmio energetico
/// la CPU spesa toglie potenza alla GPU. Qui le funzioni che madVR lancia a raffica vengono eseguite da thread
/// riusati: a madVR torna un evento che si segnala alla fine (WaitForSingleObject/Multiple e CloseHandle funzionano
/// come con un thread). Attivo con la consegna in catena; CINECORE_MADVR_THREAD_POOL=0 lo spegne.
/// </summary>
internal static unsafe class MadVrThreadPool
{
    internal static readonly bool On = D3D9SharedBridge.FastOption("CINECORE_MADVR_THREAD_POOL");
    private const int HotAfter = 40;      // creazioni della stessa funzione prima di servirla dal gruppo
    private const int MaxWorkers = 32;
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32")] private static extern IntPtr CreateEventW(IntPtr attr, int manual, int initial, IntPtr name);
    [DllImport("kernel32")] private static extern int SetEvent(IntPtr h);
    [DllImport("kernel32")] private static extern int CloseHandle(IntPtr h);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();

    private static IntPtr _orig, _origClose;
    private static readonly ConcurrentDictionary<IntPtr, int> Seen = new();
    // Lavori in coda e semaforo nativo (msync, dentro il processo): i lavoratori non passano dal wineserver.
    private static readonly ConcurrentQueue<(IntPtr Start, IntPtr Param, IntPtr Done)> Jobs = new();
    private static IntPtr _jobsReady;
    private static int _workers, _idle;
    private static long _pooled, _passed;
    // Eventi riusati: un evento torna libero solo quando il lavoro e' finito (1) E madVR l'ha chiuso (2).
    private static readonly ConcurrentBag<IntPtr> FreeEvents = new();
    private static readonly ConcurrentDictionary<IntPtr, int> Lent = new();
    [DllImport("kernel32")] private static extern IntPtr CreateSemaphoreW(IntPtr attr, int initial, int max, IntPtr name);
    [DllImport("kernel32")] private static extern int ReleaseSemaphore(IntPtr h, int count, IntPtr previous);
    [DllImport("kernel32")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32")] private static extern int ResetEvent(IntPtr h);

    internal static void Install()
    {
        if (!On) return;
        try
        {
            IntPtr mod = GetModuleHandleW("madVR64.ax");
            if (mod == IntPtr.Zero) return;
            _jobsReady = CreateSemaphoreW(IntPtr.Zero, 0, int.MaxValue, IntPtr.Zero);
            if (_jobsReady == IntPtr.Zero) return;
            int n = MadVrImports.HookImport(mod, "KERNEL32.dll", "CreateThread",
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, nuint, IntPtr, IntPtr, uint, uint*, IntPtr>)&Hook, out _orig);
            if (n > 0 && MadVrImports.HookImport(mod, "KERNEL32.dll", "CloseHandle",
                    (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&CloseHook, out _origClose) == 0)
                _origClose = IntPtr.Zero; // senza il gancio di chiusura gli eventi non si riusano (se ne crea uno per lavoro)
            Out.Line(n > 0 ? $"info madVR: thread a raffica serviti da un gruppo riusato (eventi riusati: {_origClose != IntPtr.Zero})" : "info madVR: CreateThread non trovata nelle importazioni");
            if (n == 0 || !HookTrace.Count) return;
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(3000);
                    Out.Line($"info madVR thread: dal gruppo={Interlocked.Exchange(ref _pooled, 0)} veri={Interlocked.Exchange(ref _passed, 0)} lavoratori={Volatile.Read(ref _workers)} eventi={FreeEvents.Count + Lent.Count}");
                }
            }) { IsBackground = true, Name = "cinecore-pool-stats" }.Start();
        }
        catch (Exception ex) { Out.Line("info madVR gruppo di thread: " + ex.Message); }
    }

    private static IntPtr TakeEvent()
    {
        if (_origClose != IntPtr.Zero && FreeEvents.TryTake(out IntPtr e)) { ResetEvent(e); Lent[e] = 0; return e; }
        e = CreateEventW(IntPtr.Zero, 1, 0, IntPtr.Zero);
        if (e != IntPtr.Zero && _origClose != IntPtr.Zero) Lent[e] = 0;
        return e;
    }

    /// <summary>Lavoro finito (1) o maniglia chiusa da madVR (2): con entrambi l'evento torna nella riserva.</summary>
    private static void Release(IntPtr e, int flag)
    {
        while (true)
        {
            if (!Lent.TryGetValue(e, out int st)) return;
            int now = st | flag;
            if (!Lent.TryUpdate(e, now, st)) continue;
            if (now == 3 && Lent.TryRemove(e, out _)) FreeEvents.Add(e);
            return;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int CloseHook(IntPtr h)
    {
        if (Lent.ContainsKey(h)) { Release(h, 2); return 1; }
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)_origClose)(h);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static IntPtr Hook(IntPtr attr, nuint stack, IntPtr start, IntPtr param, uint flags, uint* tid)
    {
        var real = (delegate* unmanaged[Stdcall]<IntPtr, nuint, IntPtr, IntPtr, uint, uint*, IntPtr>)_orig;
        // Sospeso alla nascita (va ripreso con ResumeThread) o funzione vista poche volte: thread vero.
        if ((flags & 4) != 0 || Seen.AddOrUpdate(start, 1, (_, c) => c < int.MaxValue ? c + 1 : c) <= HotAfter)
        {
            Interlocked.Increment(ref _passed);
            return real(attr, stack, start, param, flags, tid);
        }
        IntPtr done = TakeEvent();
        if (done == IntPtr.Zero)
        {
            Interlocked.Increment(ref _passed);
            return real(attr, stack, start, param, flags, tid);
        }
        Jobs.Enqueue((start, param, done));
        // Nessun lavoratore libero: uno in piu' (fino a MaxWorkers).
        if (Volatile.Read(ref _idle) == 0 && Volatile.Read(ref _workers) < MaxWorkers && Interlocked.Increment(ref _workers) <= MaxWorkers)
            new Thread(Work, 1 << 20) { IsBackground = true, Name = "cinecore-madvr-pool", Priority = ThreadPriority.AboveNormal }.Start();
        ReleaseSemaphore(_jobsReady, 1, IntPtr.Zero);
        if (tid != null) *tid = 0x7FFF0000u + (uint)(Interlocked.Increment(ref _pooled) & 0xFFFF);
        return done;
    }

    private static void Work()
    {
        while (true)
        {
            Interlocked.Increment(ref _idle);
            WaitForSingleObject(_jobsReady, 0xFFFFFFFF);
            Interlocked.Decrement(ref _idle);
            if (!Jobs.TryDequeue(out var j)) continue;
            try { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)j.Start)(j.Param); }
            catch (Exception ex) { Out.Line("info madVR gruppo di thread: lavoro fallito " + ex.Message); }
            SetEvent(j.Done);
            if (_origClose != IntPtr.Zero) Release(j.Done, 1); else CloseHandle(j.Done);
        }
    }
}
