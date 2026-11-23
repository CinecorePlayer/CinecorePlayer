#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace CinecoreVideoHost;

/// <summary>Solo prove (CINECORE_THREAD_CENSUS=1): conta chi crea thread durante la riproduzione. Sotto Wine/FEX ogni
/// thread nuovo costa caro (pila, DllMain di tutte le DLL): un modulo che ne crea decine al secondo brucia CPU.</summary>
internal static unsafe class ThreadCensus
{
    internal static readonly bool On = Environment.GetEnvironmentVariable("CINECORE_THREAD_CENSUS") == "1";
    private static IntPtr _orig;
    private static readonly ConcurrentDictionary<long, int> Starts = new();
    private static (long Base, long End, string Name)[] _mods = Array.Empty<(long, long, string)>();
    private static int _total;

    internal static void Install()
    {
        if (!On) return;
        try
        {
            var mods = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().ToArray();
            _mods = mods.Select(m => ((long)m.BaseAddress, (long)m.BaseAddress + m.ModuleMemorySize, m.ModuleName ?? "?")).ToArray();
            int hooked = 0;
            IntPtr hook = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, nuint, IntPtr, IntPtr, uint, uint*, IntPtr>)&CreateThreadHook;
            foreach (var m in mods)
            {
                string n = (m.ModuleName ?? "").ToLowerInvariant();
                if (n is "kernel32.dll" or "kernelbase.dll" or "ntdll.dll") continue;
                if (MadVrThreadPool.On && n == "madvr64.ax") continue;
                foreach (string dll in new[] { "KERNEL32.dll", "kernelbase.dll", "api-ms-win-core-processthreads-l1-1-0.dll", "api-ms-win-core-processthreads-l1-1-1.dll", "api-ms-win-core-processthreads-l1-1-2.dll" })
                {
                    try
                    {
                        if (MadVrImports.HookImport(m.BaseAddress, dll, "CreateThread", hook, out IntPtr orig) > 0) { hooked++; if (_orig == IntPtr.Zero) _orig = orig; }
                    }
                    catch { }
                }
            }
            Out.Line($"info censimento thread: CreateThread agganciata in {hooked} moduli su {mods.Length}");
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(3000);
                    int tot = Interlocked.Exchange(ref _total, 0);
                    var top = Starts.ToArray().OrderByDescending(k => k.Value).Take(6).ToArray();
                    Starts.Clear();
                    Out.Line($"info censimento thread: {tot} creati in 3 s | " + string.Join(", ", top.Select(k => $"{Where(k.Key)}={k.Value}")));
                }
            }) { IsBackground = true, Name = "censimento" }.Start();
        }
        catch (Exception ex) { Out.Line("info censimento thread: " + ex.Message); }
    }

    private static string Where(long a)
    {
        foreach (var m in _mods) if (a >= m.Base && a < m.End) return $"{m.Name}+0x{a - m.Base:X}";
        return $"0x{a:X}";
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static IntPtr CreateThreadHook(IntPtr attr, nuint stack, IntPtr start, IntPtr param, uint flags, uint* tid)
    {
        Interlocked.Increment(ref _total);
        Starts.AddOrUpdate((long)start, 1, (_, c) => c + 1);
        return ((delegate* unmanaged[Stdcall]<IntPtr, nuint, IntPtr, IntPtr, uint, uint*, IntPtr>)_orig)(attr, stack, start, param, flags, tid);
    }
}
