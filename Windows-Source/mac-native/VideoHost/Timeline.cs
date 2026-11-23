#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CinecoreVideoHost;

/// <summary>Solo prove (CINECORE_TIMELINE=1): registra per ~1,2 s gli eventi dei ganci con il tempo, poi li stampa una volta.</summary>
internal static class Tl
{
    internal static readonly bool On = Environment.GetEnvironmentVariable("CINECORE_TIMELINE") == "1";
    private static readonly double From = double.TryParse(Environment.GetEnvironmentVariable("CINECORE_TIMELINE_FROM"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double f) ? f : 16;
    private static readonly double Len = double.TryParse(Environment.GetEnvironmentVariable("CINECORE_TIMELINE_LEN"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double l) ? l : 0.6;
    private static readonly List<(long T, int Tid, string S)> L = new();
    private static long _start; private static bool _done;
    internal static long Now => Stopwatch.GetTimestamp();
    internal static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    [System.Runtime.InteropServices.DllImport("kernel32")] private static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32")] private static extern bool GetThreadTimes(IntPtr h, out long c, out long e, out long k, out long u);
    [ThreadStatic] private static long _lastCpu;
    internal static void Ev(string s)
    {
        if (!On || _done) return;
        long now = Stopwatch.GetTimestamp();
        if (_start != 0 && (now - _start) / (double)Stopwatch.Frequency >= From && GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u))
        {
            long cpu = k + u; s += string.Create(System.Globalization.CultureInfo.InvariantCulture, $" cpu=+{(cpu - _lastCpu) / 10000.0:0.00}"); _lastCpu = cpu;
        }
        List<(long, int, string)>? dump = null;
        lock (L)
        {
            if (_done) return;
            if (_start == 0) _start = now;
            double sec = (now - _start) / (double)Stopwatch.Frequency;
            if (sec < From) return;
            if (sec > From + Len) { _done = true; dump = new(L); }
            else L.Add((now, Environment.CurrentManagedThreadId, s));
        }
        if (dump == null) return;
        new System.Threading.Thread(() =>
        {
            long t0 = dump.Count > 0 ? dump[0].Item1 : 0, prev = t0;
            foreach (var (t, tid, text) in dump)
            {
                Out.Line(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tl {(t - t0) * 1000.0 / Stopwatch.Frequency,8:0.00} +{(t - prev) * 1000.0 / Stopwatch.Frequency,6:0.00} t{tid,-3} {text}"));
                prev = t;
            }
        }) { IsBackground = true }.Start();
    }
}
