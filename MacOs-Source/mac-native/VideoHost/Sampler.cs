#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace CinecoreVideoHost;

/// <summary>Solo prove (CINECORE_SAMPLER=1): campiona la pila x64 del thread che disegna in D3D9 e stampa dove sta.</summary>
internal static class Sampler
{
    internal static readonly bool On = Environment.GetEnvironmentVariable("CINECORE_SAMPLER") == "1";
    // CINECORE_SAMPLER=all: tutti i thread del processo, per sapere chi consuma CPU (sotto Wine i tempi per thread
    // non esistono). Per ogni thread: partenza e dove sta il suo rip nei campioni (modulo; "JIT" = codice .NET).
    internal static readonly bool All = Environment.GetEnvironmentVariable("CINECORE_SAMPLER") == "all";

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry32 { public uint Size, Usage, ThreadId, OwnerProcessId; public int BasePri, DeltaPri; public uint Flags; }
    [DllImport("kernel32")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32")] private static extern bool Thread32First(IntPtr snap, ref ThreadEntry32 e);
    [DllImport("kernel32")] private static extern bool Thread32Next(IntPtr snap, ref ThreadEntry32 e);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32")] private static extern uint GetCurrentProcessId();

    internal static void StartAll()
    {
        if (!All) return;
        uint self = GetCurrentThreadId();
        new Thread(() =>
        {
            try
            {
                if (!LoaderProbe.Ready) LoaderProbe.Init();
                Thread.Sleep(16000);
                LoaderProbe.RefreshModules();
                uint pid = GetCurrentProcessId(), me = GetCurrentThreadId();
                var tids = new List<uint>();
                IntPtr snap = CreateToolhelp32Snapshot(4, 0);
                var e = new ThreadEntry32 { Size = (uint)Marshal.SizeOf<ThreadEntry32>() };
                for (bool ok = Thread32First(snap, ref e); ok; ok = Thread32Next(snap, ref e))
                    if (e.OwnerProcessId == pid && e.ThreadId != me) tids.Add(e.ThreadId);
                CloseHandle(snap);
                var per = new Dictionary<uint, Dictionary<string, int>>(); var start = new Dictionary<uint, string>();
                var rip = new Regex(@" rip=0x[0-9A-F]+ ?(\S*)"); var part = new Regex(@" partenza=(\S+)");
                var sw = Stopwatch.StartNew(); int rounds = 0;
                while (sw.ElapsedMilliseconds < 8000)
                {
                    foreach (uint tid in tids)
                    {
                        string s = LoaderProbe.Thread(tid, 0);
                        if (!start.ContainsKey(tid)) { var pm = part.Match(s); start[tid] = pm.Success ? pm.Groups[1].Value : "?"; }
                        var m = rip.Match(s);
                        string where = m.Success ? (m.Groups[1].Value.Length > 0 && !m.Groups[1].Value.StartsWith("pila") ? m.Groups[1].Value.Split('+')[0] : "JIT") : "?";
                        if (!per.TryGetValue(tid, out var d)) per[tid] = d = new Dictionary<string, int>();
                        d[where] = d.GetValueOrDefault(where) + 1;
                    }
                    rounds++;
                    Thread.Sleep(4);
                }
                Out.Line($"sampall ---- {rounds} giri su {tids.Count} thread in {sw.ElapsedMilliseconds} ms (fuori da ntdll/win32u = sta lavorando)");
                foreach (var kv in per.OrderByDescending(k => k.Value.Where(x => x.Key is not ("ntdll.dll" or "win32u.dll")).Sum(x => x.Value)).Take(14))
                {
                    int busy = kv.Value.Where(x => x.Key is not ("ntdll.dll" or "win32u.dll")).Sum(x => x.Value);
                    Out.Line($"sampall {100.0 * busy / rounds,5:0.0}% fuori  0x{kv.Key:X} partenza={start[kv.Key]} | " + string.Join(" ", kv.Value.OrderByDescending(x => x.Value).Take(5).Select(x => $"{x.Key}={100.0 * x.Value / rounds:0}%")));
                }
            }
            catch (Exception ex) { Out.Line("sampall errore " + ex.Message); }
        }) { IsBackground = true }.Start();
    }
    private static int _started;
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();

    internal static void NoteRenderThread()
    {
        if (!On || Interlocked.Exchange(ref _started, 1) != 0) return;
        uint tid = GetCurrentThreadId();
        new Thread(() => Run(tid)) { IsBackground = true }.Start();
    }

    private static void Run(uint tid)
    {
        try
        {
            if (!LoaderProbe.Ready) LoaderProbe.Init();
            Thread.Sleep(14000);
            LoaderProbe.RefreshModules();
            var agg = new Dictionary<string, int>(); var top = new Dictionary<string, int>();
            int n = 0; var sw = Stopwatch.StartNew();
            var rsp = new Regex(@" rsp=0x[0-9A-F]+"); var rip = new Regex(@" rip=0x[0-9A-F]+ ?(\S*)");
            while (sw.ElapsedMilliseconds < 8000)
            {
                string s = LoaderProbe.Thread(tid, 9);
                int at = s.IndexOf(" rip=", StringComparison.Ordinal);
                if (at >= 0)
                {
                    s = rsp.Replace(s[at..], "");
                    var m = rip.Match(s);
                    string where = m.Success && m.Groups[1].Value.Length > 0 && !m.Groups[1].Value.StartsWith("pila") ? m.Groups[1].Value : "JIT";
                    string mod = where.Split('+')[0];
                    string stack = s[(s.IndexOf("pila:", StringComparison.Ordinal) + 5)..];
                    string key = mod + " |" + stack;
                    agg[key] = agg.GetValueOrDefault(key) + 1;
                    top[mod] = top.GetValueOrDefault(mod) + 1;
                    n++;
                }
                Thread.Sleep(2);
            }
            Out.Line($"samp ---- {n} campioni del thread 0x{tid:X} in {sw.ElapsedMilliseconds} ms");
            foreach (var kv in top.OrderByDescending(k => k.Value)) Out.Line($"samp modulo {kv.Value,5} {100.0 * kv.Value / n,5:0.0}%  {kv.Key}");
            foreach (var kv in agg.OrderByDescending(k => k.Value).Take(45)) Out.Line($"samp pila {kv.Value,5} {100.0 * kv.Value / n,5:0.0}%  {kv.Key}");
        }
        catch (Exception ex) { Out.Line("samp errore " + ex.Message); }
    }
}
