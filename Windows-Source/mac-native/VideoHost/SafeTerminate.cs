#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CinecoreVideoHost;

/// <summary>
/// TerminateThread senza lock del loader orfano (stallo madVR dell'11/11).
/// madVR e XySubFilter terminano a volte i propri thread. Se il thread ucciso è dentro il loader di Wine (avvio o fine
/// del thread: DllMain di tutte le DLL, lentissimo sotto FEX) il lock del loader resta posseduto da un thread che non
/// esiste più: nessun thread nuovo parte, madVR smette di presentare e il processo non si chiude.
/// Il gancio sospende il thread, controlla nel PEB chi possiede il lock e lo termina solo fuori dal loader (un thread
/// sospeso non può rientrarci). Se lo tiene, lo lascia andare 1 ms e riprova.
/// </summary>
internal static unsafe class SafeTerminate
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandleA(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("ntdll")] private static extern int NtQueryInformationProcess(IntPtr process, int cls, void* info, uint size, uint* ret);

    private static IntPtr _loaderLock;
    private static delegate* unmanaged[Stdcall]<IntPtr, uint> _suspend, _resume, _threadId;
    private static delegate* unmanaged[Stdcall]<uint, void> _sleep;
    private static delegate* unmanaged[Stdcall]<uint> _currentThreadId;
    private static IntPtr _orig;
    private static int _calls, _waited, _logs;
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("CINECORE_SAFE_TERMINATE") != "0";

    /// <summary>Proprietario attuale del lock del loader (id del thread), 0 se libero.</summary>
    public static uint LoaderOwner => _loaderLock == IntPtr.Zero ? 0 : (uint)*(IntPtr*)(_loaderLock + 0x10);

    private static bool Init()
    {
        if (_loaderLock != IntPtr.Zero) return true;
        IntPtr k = GetModuleHandleA("kernel32.dll");
        _suspend = (delegate* unmanaged[Stdcall]<IntPtr, uint>)GetProcAddress(k, "SuspendThread");
        _resume = (delegate* unmanaged[Stdcall]<IntPtr, uint>)GetProcAddress(k, "ResumeThread");
        _threadId = (delegate* unmanaged[Stdcall]<IntPtr, uint>)GetProcAddress(k, "GetThreadId");
        _sleep = (delegate* unmanaged[Stdcall]<uint, void>)GetProcAddress(k, "Sleep");
        _currentThreadId = (delegate* unmanaged[Stdcall]<uint>)GetProcAddress(k, "GetCurrentThreadId");
        byte* pbi = stackalloc byte[48]; uint ret;
        if (NtQueryInformationProcess(GetCurrentProcess(), 0, pbi, 48, &ret) != 0) return false;
        _loaderLock = *(IntPtr*)(*(IntPtr*)(pbi + 8) + 0x110); // PEB.LoaderLock (x64)
        return _loaderLock != IntPtr.Zero && _suspend != null && _resume != null && _threadId != null && _sleep != null;
    }

    /// <summary>Aggancia TerminateThread nella tabella delle importazioni del modulo, se caricato.</summary>
    public static void Install(string module)
    {
        if (!Enabled) return;
        try
        {
            if (!Init()) { Out.Line("info terminazione sicura non disponibile"); return; }
            IntPtr mod = GetModuleHandleW(module);
            if (mod == IntPtr.Zero) return;
            int n = MadVrImports.HookImport(mod, "KERNEL32.dll", "TerminateThread",
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&TerminateThreadHook, out IntPtr orig);
            if (n == 0) return;
            if (_orig == IntPtr.Zero) _orig = orig;
            Out.Line($"info terminazione sicura dei thread attiva in {module}");
        }
        catch (Exception ex) { Out.Line("info terminazione sicura: " + ex.Message); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int TerminateThreadHook(IntPtr thread, uint exitCode)
    {
        using var __hook = HookTrace.Enter("TerminateThread");
        var terminate = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)_orig;
        uint tid = _threadId(thread);
        Interlocked.Increment(ref _calls);
        // Se stesso o thread sconosciuto: comportamento normale.
        if (tid == 0 || tid == _currentThreadId()) return terminate(thread, exitCode);
        int waits = 0;
        long start = Environment.TickCount64;
        while (true)
        {
            uint prev = _suspend(thread);
            if (prev == 0xFFFFFFFF) return terminate(thread, exitCode); // già finito o non sospendibile
            if (LoaderOwner != tid || Environment.TickCount64 - start > 10000)
            {
                int r = terminate(thread, exitCode);
                _resume(thread); // innocuo se il thread non c'è più
                if (waits > 0) Interlocked.Increment(ref _waited);
                if (Interlocked.Increment(ref _logs) <= 20 || waits > 0)
                    Out.Line($"info TerminateThread tid=0x{tid:X} attese={waits} ({Environment.TickCount64 - start} ms) chiamate={_calls} rinviate={_waited}");
                return r;
            }
            _resume(thread);
            waits++;
            _sleep(1);
        }
    }
}
