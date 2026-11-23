#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace CinecoreVideoHost;

/// <summary>
/// Griglia dei vsync usata dalla coda flip emulata. Con il modulo di presentazione nel runtime (wined3d.so,
/// cinecore_present.m) e CINECORE_VSYNC_FILE impostata, la griglia e' quella VERA dello schermo: il modulo pubblica
/// nel file l'istante di un vsync reale nello stesso orologio di QueryPerformanceCounter. Senza, e' una griglia
/// nominale (frequenza dello schermo, fase arbitraria).
/// Gli "slot" sono vsync numerati in modo continuo; Start(slot) e' l'istante (tick di Stopwatch) in cui inizia.
/// </summary>
internal static class Vs
{
    private static readonly object Sync = new();
    private static double _nominalHz = 60, _period;          // periodo in tick di Stopwatch
    private static double _anchorT;                           // tick: inizio dello slot _anchorSlot
    private static long _anchorSlot;
    private static FileStream? _file; private static bool _fileFailed, _logged;
    private static long _nextPoll, _firstV, _lastV; private static ulong _hookedAtStart = ulong.MaxValue;
    private static readonly byte[] Buf = new byte[64];
    /// <summary>Vero quando la griglia segue i vsync reali pubblicati dal modulo nativo.</summary>
    internal static bool Real { get; private set; }
    /// <summary>Presentazioni programmate dal modulo nativo da quando lo si osserva (0 = il gancio non sta lavorando).</summary>
    internal static ulong Hooked { get; private set; }
    internal static ulong Late { get; private set; }
    internal static double PeriodTicks { get { lock (Sync) { Init(); return _period; } } }
    internal static double Hz { get { lock (Sync) { Init(); return Stopwatch.Frequency / _period; } } }

    internal static void SetNominal(double hz) { lock (Sync) { _nominalHz = hz; if (!Real) _period = 0; } }

    private static void Init()
    {
        if (_period > 0) return;
        _period = Stopwatch.Frequency / _nominalHz; _anchorT = 0; _anchorSlot = 0;
    }

    internal static uint Now()
    {
        long t = Stopwatch.GetTimestamp();
        lock (Sync) { Init(); if (t >= _nextPoll) Poll(t); return (uint)(_anchorSlot + (long)Math.Floor((t - _anchorT) / _period)); }
    }
    internal static long Start(uint slot) { lock (Sync) { Init(); return (long)(_anchorT + ((long)slot - _anchorSlot) * _period); } }

    private static ulong _targets;
    private static readonly byte[] W = new byte[8];
    /// <summary>
    /// Comunica al modulo nativo il vsync (assoluto) a cui deve apparire la prossima presentazione programmata.
    /// Va chiamato subito prima di ogni Present con intervallo di sincronia >= 1, dallo stesso thread.
    /// </summary>
    internal static bool PushTarget(uint slot)
    {
        lock (Sync)
        {
            if (!Real || _file == null) return false;
            try
            {
                long ns = (long)((_anchorT + ((long)slot - _anchorSlot) * _period) * (1e9 / Stopwatch.Frequency));
                BitConverter.TryWriteBytes(W, ns);
                _file.Seek(72 + 8 * (long)(_targets % 16), SeekOrigin.Begin); _file.Write(W, 0, 8);
                _targets++;
                BitConverter.TryWriteBytes(W, _targets);
                _file.Seek(64, SeekOrigin.Begin); _file.Write(W, 0, 8);
                _file.Flush();
                return true;
            }
            catch { return false; }
        }
    }

    private static void Poll(long t)
    {
        _nextPoll = t + Stopwatch.Frequency / 4;
        if (_fileFailed) return;
        try
        {
            if (_file == null)
            {
                string? unix = Environment.GetEnvironmentVariable("CINECORE_VSYNC_FILE");
                if (string.IsNullOrEmpty(unix)) { _fileFailed = true; return; }
                string win = unix.StartsWith('/') ? "Z:" + unix.Replace('/', '\\') : unix;
                if (!File.Exists(win)) return; // il modulo lo crea quando parte: si riprova
                _file = new FileStream(win, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            }
            ulong seq = 0, seq2 = 1; long v = 0; double p = 0; ulong hooked = 0, magic = 0, late = 0;
            for (int i = 0; i < 4 && seq != seq2; i++)
            {
                _file.Seek(0, SeekOrigin.Begin);
                if (_file.Read(Buf, 0, 64) < 64) return;
                seq = BitConverter.ToUInt64(Buf, 0); v = BitConverter.ToInt64(Buf, 8); p = BitConverter.ToDouble(Buf, 16);
                hooked = BitConverter.ToUInt64(Buf, 24); magic = BitConverter.ToUInt64(Buf, 32); late = BitConverter.ToUInt64(Buf, 48);
                _file.Seek(0, SeekOrigin.Begin); _file.Read(Buf, 0, 8); seq2 = BitConverter.ToUInt64(Buf, 0);
            }
            if (seq != seq2 || (seq & 1) != 0 || magic != 0x31434e5953564343UL || v == 0) return;
            if (_hookedAtStart == ulong.MaxValue) _hookedAtStart = hooked;
            Hooked = hooked - _hookedAtStart; Late = late;
            // Stesso orologio? L'istante pubblicato deve essere vicino ad adesso.
            double ticksPerNs = Stopwatch.Frequency / 1e9;
            double vt = v * ticksPerNs;
            if (Math.Abs(t - vt) > Stopwatch.Frequency * 2.0)
            {
                if (!_logged) { _logged = true; Out.Line(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"info flip: orologio dei vsync non confrontabile (scarto {(t - vt) / Stopwatch.Frequency:0.0} s), griglia nominale")); }
                return;
            }
            double nominal = Stopwatch.Frequency / _nominalHz;
            if (!Real)
            {
                // Passaggio alla griglia vera senza cambiare la numerazione: lo slot che contiene v inizia in v.
                long slotAtV = _anchorSlot + (long)Math.Round((vt - _anchorT) / _period);
                _anchorT = vt; _anchorSlot = slotAtV; _period = nominal; _firstV = v; _lastV = v; Real = true;
                Out.Line(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"info flip: griglia agganciata ai vsync veri dello schermo ({_nominalHz:0.###} Hz nominali)"));
                return;
            }
            if (v == _lastV) return;
            double steps = Math.Round((vt - _anchorT) / _period);
            double err = (vt - _anchorT) - steps * _period;
            if (Math.Abs(err) > _period * 0.3)
            {
                // Salto (sospensione, cambio di schermo): si riparte da qui con il periodo nominale.
                _anchorSlot += (long)steps; _anchorT = vt; _period = nominal; _firstV = v; _lastV = v;
                return;
            }
            _anchorSlot += (long)steps; _anchorT = vt; _lastV = v;
            // Periodo reale su una base lunga (il valore istantaneo del display link e' rumoroso).
            double span = (v - _firstV) * ticksPerNs; double n = Math.Round(span / _period);
            if (n >= 120) { double pe = span / n; if (Math.Abs(pe / nominal - 1) < 0.01) _period = pe; }
        }
        catch (Exception ex)
        {
            _fileFailed = true; Real = false;
            Out.Line("info flip: orologio dei vsync non leggibile (" + ex.Message + "), griglia nominale");
        }
    }
}
