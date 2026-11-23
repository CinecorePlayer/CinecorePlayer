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
    [DllImport("kernel32")] private static extern int DuplicateHandle(IntPtr sp, IntPtr h, IntPtr tp, out IntPtr dup, uint access, int inherit, uint options);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] private static extern int CloseHandle(IntPtr h);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();

    private static IntPtr _orig;
    private static readonly ConcurrentDictionary<IntPtr, int> Seen = new();
    private static readonly object Gate = new();
    private static readonly Queue<(IntPtr Start, IntPtr Param, IntPtr Done)> Jobs = new();
    private static int _workers, _idle;
    private static long _pooled, _passed, _maxWorkers;

    internal static void Install()
    {
        if (!On) return;
        try
        {
            IntPtr mod = GetModuleHandleW("madVR64.ax");
            if (mod == IntPtr.Zero) return;
            int n = MadVrImports.HookImport(mod, "KERNEL32.dll", "CreateThread",
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, nuint, IntPtr, IntPtr, uint, uint*, IntPtr>)&Hook, out _orig);
            Out.Line(n > 0 ? "info madVR: thread a raffica serviti da un gruppo riusato" : "info madVR: CreateThread non trovata nelle importazioni");
            if (n == 0 || !HookTrace.Count) return;
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(3000);
                    Out.Line($"info madVR thread: dal gruppo={Interlocked.Exchange(ref _pooled, 0)} veri={Interlocked.Exchange(ref _passed, 0)} lavoratori={Volatile.Read(ref _workers)}");
                }
            }) { IsBackground = true, Name = "cinecore-pool-stats" }.Start();
        }
        catch (Exception ex) { Out.Line("info madVR gruppo di thread: " + ex.Message); }
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
        IntPtr done = CreateEventW(IntPtr.Zero, 1, 0, IntPtr.Zero);
        // Il lavoratore segnala una copia sua: madVR puo' chiudere la propria maniglia quando vuole.
        if (done == IntPtr.Zero || DuplicateHandle(GetCurrentProcess(), done, GetCurrentProcess(), out IntPtr mine, 0, 0, 2) == 0)
        {
            if (done != IntPtr.Zero) CloseHandle(done);
            Interlocked.Increment(ref _passed);
            return real(attr, stack, start, param, flags, tid);
        }
        bool spawn = false;
        lock (Gate)
        {
            Jobs.Enqueue((start, param, mine));
            if (_idle < Jobs.Count && _workers < MaxWorkers) { _workers++; spawn = true; }
            Monitor.Pulse(Gate);
        }
        if (spawn) new Thread(Work, 1 << 20) { IsBackground = true, Name = "cinecore-madvr-pool", Priority = ThreadPriority.AboveNormal }.Start();
        if (tid != null) *tid = 0x7FFF0000u + (uint)(Interlocked.Increment(ref _pooled) & 0xFFFF);
        return done;
    }

    private static void Work()
    {
        while (true)
        {
            (IntPtr Start, IntPtr Param, IntPtr Done) j;
            lock (Gate)
            {
                _idle++;
                while (Jobs.Count == 0) Monitor.Wait(Gate);
                _idle--;
                j = Jobs.Dequeue();
            }
            try { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)j.Start)(j.Param); }
            catch (Exception ex) { Out.Line("info madVR gruppo di thread: lavoro fallito " + ex.Message); }
            SetEvent(j.Done); CloseHandle(j.Done);
        }
    }
}
