using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CinecoreVideoHost;

/// <summary>
/// Diagnostica degli stalli (CINECORE_HOOK_TRACE=1). Quando il lock del loader di Wine resta occupato, nessun thread
/// nuovo parte, madVR compreso. Questa classe legge dal PEB chi possiede il lock, sospende un attimo quel thread e ne
/// elenca gli indirizzi di ritorno che cadono nei moduli caricati (scansione dello stack x64).
/// Tutte le funzioni di sistema sono risolte in <see cref="Init"/>: durante lo stallo una risoluzione pigra di una
/// P/Invoke prenderebbe il lock del loader e resterebbe ferma anche lei.
/// </summary>
internal static unsafe class LoaderProbe
{
    private static delegate* unmanaged[Stdcall]<uint, int, uint, IntPtr> _openThread;
    private static delegate* unmanaged[Stdcall]<IntPtr, uint> _suspend, _resume;
    private static delegate* unmanaged[Stdcall]<IntPtr, byte*, int> _getContext;
    private static delegate* unmanaged[Stdcall]<IntPtr, int> _close;
    private static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, nuint, nuint*, int> _readMem;
    private static delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint, uint*, int> _enumModules;
    private static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, char*, uint, uint> _moduleName;
    private static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, int> _moduleInfo;
    private static delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, uint*, int> _ntQueryThread;
    private static delegate* unmanaged[Stdcall]<IntPtr, char**, int> _threadDescription;
    private static delegate* unmanaged[Stdcall]<uint, uint, IntPtr> _snapshot;
    private static delegate* unmanaged[Stdcall]<IntPtr, byte*, int> _thFirst, _thNext;
    private static IntPtr _process, _loaderLock;
    private static uint _pid;
    private static (ulong Base, ulong End, string Name)[] _modules = Array.Empty<(ulong, ulong, string)>();
    public static bool Ready { get; private set; }

    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandleA(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("ntdll")] private static extern int NtQueryInformationProcess(IntPtr process, int cls, void* info, uint size, uint* ret);

    public static void Init()
    {
        try
        {
            IntPtr k = GetModuleHandleA("kernel32.dll"), kb = GetModuleHandleA("kernelbase.dll"), nt = GetModuleHandleA("ntdll.dll");
            IntPtr P(string n) { IntPtr a = GetProcAddress(k, n); if (a == IntPtr.Zero) a = GetProcAddress(kb, n); return a; }
            _openThread = (delegate* unmanaged[Stdcall]<uint, int, uint, IntPtr>)P("OpenThread");
            _suspend = (delegate* unmanaged[Stdcall]<IntPtr, uint>)P("SuspendThread");
            _resume = (delegate* unmanaged[Stdcall]<IntPtr, uint>)P("ResumeThread");
            _getContext = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)P("GetThreadContext");
            _close = (delegate* unmanaged[Stdcall]<IntPtr, int>)P("CloseHandle");
            _readMem = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, nuint, nuint*, int>)P("ReadProcessMemory");
            _enumModules = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint, uint*, int>)P("K32EnumProcessModules");
            _moduleName = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, char*, uint, uint>)P("K32GetModuleBaseNameW");
            _moduleInfo = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, int>)P("K32GetModuleInformation");
            _threadDescription = (delegate* unmanaged[Stdcall]<IntPtr, char**, int>)P("GetThreadDescription");
            _ntQueryThread = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, uint*, int>)GetProcAddress(nt, "NtQueryInformationThread");
            _snapshot = (delegate* unmanaged[Stdcall]<uint, uint, IntPtr>)P("CreateToolhelp32Snapshot");
            _thFirst = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)P("Thread32First");
            _thNext = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)P("Thread32Next");
            _pid = (uint)Environment.ProcessId;
            _process = GetCurrentProcess();
            // PROCESS_BASIC_INFORMATION: PebBaseAddress a +8; PEB.LoaderLock a +0x110 (x64).
            byte* pbi = stackalloc byte[48]; uint ret;
            if (NtQueryInformationProcess(_process, 0, pbi, 48, &ret) == 0)
                _loaderLock = *(IntPtr*)(*(IntPtr*)(pbi + 8) + 0x110);
            Ready = _openThread != null && _getContext != null && _readMem != null && _loaderLock != IntPtr.Zero;
            RefreshModules();
            // prova a vuoto: compila il codice ora, non durante lo stallo
            Dump(); _ = Thread(0, 48); _ = $"0x{(ulong)1:X} {Where(1)} {1.5:0}";
            Out.Line("diag prova thread:" + AllThreads(0));
            Out.Line($"info sonda loader pronta={Ready} lock=0x{_loaderLock:X}");
        }
        catch (Exception ex) { Out.Line("info sonda loader non disponibile: " + ex.Message); }
    }

    /// <summary>Elenco dei moduli: da aggiornare quando tutto va bene (non serve il lock del loader, ma meglio prima).</summary>
    public static void RefreshModules()
    {
        if (_enumModules == null) return;
        try
        {
            var mods = new IntPtr[1024]; uint need;
            fixed (IntPtr* m = mods) if (_enumModules(_process, m, (uint)(mods.Length * IntPtr.Size), &need) == 0) return;
            int n = (int)Math.Min(need / (uint)IntPtr.Size, (uint)mods.Length);
            var list = new List<(ulong, ulong, string)>(n);
            char* name = stackalloc char[260]; byte* info = stackalloc byte[24];
            for (int i = 0; i < n; i++)
            {
                if (_moduleInfo(_process, mods[i], info, 24) == 0) continue;
                uint len = _moduleName(_process, mods[i], name, 260);
                ulong b = (ulong)*(IntPtr*)info; ulong size = *(uint*)(info + 8);
                list.Add((b, b + size, new string(name, 0, (int)len)));
            }
            _modules = list.ToArray();
        }
        catch { }
    }

    private static string Where(ulong a)
    {
        foreach (var (b, e, n) in _modules) if (a >= b && a < e) return $"{n}+0x{a - b:X}";
        return "";
    }

    /// <summary>Chi tiene il lock del loader e cosa sta facendo. Vuoto se il lock è libero.</summary>
    public static string Dump()
    {
        if (!Ready) return "";
        var sb = new StringBuilder();
        try
        {
            // RTL_CRITICAL_SECTION x64: LockCount +8, RecursionCount +0xC, OwningThread +0x10.
            int lockCount = *(int*)(_loaderLock + 8), recursion = *(int*)(_loaderLock + 0xC);
            uint owner = (uint)*(IntPtr*)(_loaderLock + 0x10);
            if (owner == 0) return "";
            sb.Append($" loader: proprietario=0x{owner:X} ricorsione={recursion} lockCount={lockCount}");
            sb.Append(Thread(owner, 48));
        }
        catch (Exception ex) { sb.Append(" sonda: " + ex.Message); }
        return sb.ToString();
    }

    /// <summary>Nome, indirizzo di partenza, RIP/RSP e indirizzi di ritorno sullo stack di un thread.</summary>
    public static string Thread(uint tid, int maxFrames)
    {
        var sb = new StringBuilder();
        IntPtr h = _openThread(0x1FFFFF /*THREAD_ALL_ACCESS*/, 0, tid);
        if (h == IntPtr.Zero) return " (thread non apribile)";
        try
        {
            char* desc = null;
            if (_threadDescription != null && _threadDescription(h, &desc) >= 0 && desc != null) sb.Append($" nome=\"{new string(desc)}\"");
            IntPtr start; uint r;
            if (_ntQueryThread != null && _ntQueryThread(h, 9 /*ThreadQuerySetWin32StartAddress*/, &start, (uint)IntPtr.Size, &r) == 0)
                sb.Append($" partenza={Where((ulong)start)}");
            // CONTEXT x64: 1232 byte allineati a 16; ContextFlags a +0x30; Rsp +0x98; Rip +0xF8.
            byte* raw = stackalloc byte[1232 + 16];
            byte* ctx = (byte*)(((ulong)raw + 15) & ~15UL);
            new Span<byte>(ctx, 1232).Clear();
            *(uint*)(ctx + 0x30) = 0x100003; // CONTEXT_AMD64 | CONTROL | INTEGER
            uint prev = _suspend(h);
            try
            {
                if (_getContext(h, ctx) == 0) { sb.Append(" (contesto non letto)"); return sb.ToString(); }
            }
            finally { if (prev != 0xFFFFFFFF) _resume(h); }
            ulong rip = *(ulong*)(ctx + 0xF8), rsp = *(ulong*)(ctx + 0x98);
            sb.Append($" rip=0x{rip:X} {Where(rip)} rsp=0x{rsp:X} pila:");
            // Scansione: valori sullo stack che cadono in un modulo (possibili indirizzi di ritorno), primi 48.
            const int bytes = 64 * 1024;
            var buf = new byte[bytes]; nuint got = 0;
            fixed (byte* b = buf) _readMem(_process, (IntPtr)rsp, b, bytes, &got);
            if (got == 0) fixed (byte* b = buf) _readMem(_process, (IntPtr)rsp, b, 8192, &got);
            int shown = 0; string last = "";
            for (int o = 0; o + 8 <= (int)got && shown < maxFrames; o += 8)
            {
                ulong v = BitConverter.ToUInt64(buf, o);
                string w = Where(v);
                if (w.Length == 0 || w == last) continue;
                sb.Append(' ').Append(w); last = w; shown++;
            }
        }
        catch (Exception ex) { sb.Append(" errore " + ex.Message); }
        finally { _close(h); }
        return sb.ToString();
    }

    /// <summary>Tutti i thread del processo tranne <paramref name="skip"/>: una riga per gruppo di pile uguali.</summary>
    public static string AllThreads(uint skip)
    {
        if (!Ready || _snapshot == null) return "";
        var groups = new Dictionary<string, List<uint>>();
        IntPtr snap = _snapshot(4 /*TH32CS_SNAPTHREAD*/, 0);
        if (snap == (IntPtr)(-1) || snap == IntPtr.Zero) return "";
        try
        {
            byte* te = stackalloc byte[28];
            *(uint*)te = 28;
            uint self = GetCurrentThreadId();
            for (int ok = _thFirst(snap, te); ok != 0; ok = _thNext(snap, te))
            {
                uint tid = *(uint*)(te + 8), owner = *(uint*)(te + 12);
                if (owner != _pid || tid == skip || tid == self) continue;
                string sig = Thread(tid, 10);
                // il nome e l'indirizzo di partenza distinguono i thread; per raggruppare si usa solo la pila
                int at = sig.IndexOf(" pila:", StringComparison.Ordinal);
                string key = at >= 0 ? sig[at..] : sig;
                int r = sig.IndexOf(" rip=", StringComparison.Ordinal);
                if (r >= 0) { int sp = sig.IndexOf(' ', sig.IndexOf(' ', r + 5) + 1); key = (sp > r ? sig[r..sp] : "") + key; }
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<uint>();
                l.Add(tid);
                *(uint*)te = 28;
            }
        }
        finally { _close(snap); }
        var sb = new StringBuilder();
        foreach (var kv in groups)
        {
            sb.Append("\ndiag   ").Append(kv.Value.Count).Append("x [");
            for (int i = 0; i < Math.Min(4, kv.Value.Count); i++) sb.Append(i > 0 ? " " : "").Append($"0x{kv.Value[i]:X}");
            sb.Append(']').Append(kv.Key);
        }
        return sb.ToString();
    }

    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
}
