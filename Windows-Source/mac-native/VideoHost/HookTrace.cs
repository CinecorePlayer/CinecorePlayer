using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace CinecoreVideoHost;

/// <summary>
/// Diagnostica degli stalli (CINECORE_HOOK_TRACE=1): ogni gancio annota il thread che ci entra e da quando.
/// Quando madVR smette di presentare, <see cref="Dump"/> elenca i thread rimasti dentro un gancio e i lock del ponte
/// occupati. Spenta, costa un controllo su un campo statico in sola lettura.
/// </summary>
internal static class HookTrace
{
    public static readonly bool On = Environment.GetEnvironmentVariable("CINECORE_HOOK_TRACE") == "1";
    /// <summary>Censimento (CINECORE_HOOK_COUNT=1): quante volte al secondo madVR entra in ogni gancio e per quanto.</summary>
    public static readonly bool Count = Environment.GetEnvironmentVariable("CINECORE_HOOK_COUNT") == "1";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long[]> Counts = new();
    private static int _countThread;
    private static void StartCountThread()
    {
        if (Interlocked.Exchange(ref _countThread, 1) != 0) return;
        new Thread(() =>
        {
            double f = 1000.0 / Stopwatch.Frequency;
            while (true)
            {
                Thread.Sleep(3000);
                var rows = new List<(string, long, double)>();
                foreach (var kv in Counts) { long n = Interlocked.Exchange(ref kv.Value[0], 0), t = Interlocked.Exchange(ref kv.Value[1], 0); if (n > 0) rows.Add((kv.Key, n, t * f)); }
                rows.Sort((x, y) => y.Item2.CompareTo(x.Item2));
                var sb = new StringBuilder("info ganci/s:");
                foreach (var (name, n, ms) in rows.GetRange(0, Math.Min(14, rows.Count))) sb.Append(System.Globalization.CultureInfo.InvariantCulture, $" {name}={n / 3.0:0}/s({ms / 3000 * 100:0.0}%)");
                Out.Line(sb.ToString());
            }
        }) { IsBackground = true, Name = "cinecore-hook-count" }.Start();
    }

    internal sealed class Slot { public string? Name; public long Since; public int Depth; public string? Outer; public long OuterSince; public int Tid; }
    [ThreadStatic] private static Slot? _mine;
    private static readonly List<Slot> All = new();

    public readonly struct Scope : IDisposable
    {
        private readonly Slot? _s;
        private readonly string? _prev;
        private readonly long _prevSince;
        private readonly long[]? _c; private readonly long _t0;
        internal Scope(Slot s, string? prev, long prevSince) { _s = s; _prev = prev; _prevSince = prevSince; _c = null; _t0 = 0; }
        internal Scope(long[] c) { _s = null; _prev = null; _prevSince = 0; _c = c; _t0 = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            if (_c != null) { Interlocked.Increment(ref _c[0]); Interlocked.Add(ref _c[1], Stopwatch.GetTimestamp() - _t0); return; }
            if (_s == null) return;
            _s.Depth--;
            _s.Name = _prev; _s.Since = _prevSince;
        }
    }

    public static Scope Enter(string name)
    {
        if (Count) { StartCountThread(); return new Scope(Counts.GetOrAdd(name, _ => new long[2])); }
        if (!On) return default;
        var s = _mine;
        if (s == null)
        {
            s = _mine = new Slot { Tid = Environment.CurrentManagedThreadId };
            lock (All) All.Add(s);
        }
        string? prev = s.Name; long prevSince = s.Since;
        long now = Stopwatch.GetTimestamp();
        if (s.Depth == 0) { s.Outer = name; s.OuterSince = now; }
        s.Depth++;
        s.Name = name; s.Since = now;
        return new Scope(s, prev, prevSince);
    }

    /// <summary>Thread dentro un gancio da almeno <paramref name="minMs"/> ms, più lo stato dei lock indicati.</summary>
    public static string Dump(double minMs, params (string Name, object Lock)[] locks)
    {
        var sb = new StringBuilder();
        long now = Stopwatch.GetTimestamp();
        double f = 1000.0 / Stopwatch.Frequency;
        lock (All)
            foreach (var s in All)
            {
                string? name = s.Name; long since = s.Since; int depth = s.Depth;
                if (depth <= 0 || name == null) continue;
                double ms = (now - since) * f;
                if (ms < minMs) continue;
                sb.Append($" [t{s.Tid} {name} {ms:0}ms");
                if (depth > 1) sb.Append($" dentro {s.Outer} {(now - s.OuterSince) * f:0}ms");
                sb.Append(']');
            }
        foreach (var (n, l) in locks)
        {
            bool free = Monitor.TryEnter(l, 0);
            if (free) Monitor.Exit(l);
            else sb.Append($" lock:{n}=OCCUPATO");
        }
        return sb.Length == 0 ? " nessun thread fermo nei ganci, lock liberi" : sb.ToString();
    }
}
