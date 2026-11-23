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
