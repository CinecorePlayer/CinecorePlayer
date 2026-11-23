using System;
using System.Threading;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// Cambio frequenza dello schermo (RationalDisplayModeSwitcher di Windows): se la frequenza attuale non e' un multiplo
/// esatto dei fotogrammi del film (24 fps su 60 Hz, 24 su 23,976...) lo schermo passa al multiplo piu' alto disponibile
/// alla stessa risoluzione, senza superare la frequenza attuale, e torna com'era quando si chiude il film.
/// Sotto Wine il cambio di modo di madVR non arriva a macOS: qui lo fa l'app, per tutti i motori.
/// </summary>
internal static class MacDisplayRate
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
    [DllImport(CG)] private static extern int CGGetDisplaysWithPoint(CGPoint point, uint max, [Out] uint[] displays, out uint count);
    [DllImport(CG)] private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
    [DllImport(CG)] private static extern IntPtr CGDisplayCopyAllDisplayModes(uint display, IntPtr options);
    [DllImport(CG)] private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);
    [DllImport(CG)] private static extern nuint CGDisplayModeGetWidth(IntPtr mode);
    [DllImport(CG)] private static extern nuint CGDisplayModeGetHeight(IntPtr mode);
    [DllImport(CG)] private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);
    [DllImport(CG)] private static extern void CGDisplayModeRelease(IntPtr mode);
    [DllImport(CG)] private static extern IntPtr CGDisplayModeRetain(IntPtr mode);
    [DllImport(CG)] private static extern int CGBeginDisplayConfiguration(out IntPtr config);
    [DllImport(CG)] private static extern int CGConfigureDisplayWithDisplayMode(IntPtr config, uint display, IntPtr mode, IntPtr options);
    [DllImport(CG)] private static extern int CGCompleteDisplayConfiguration(IntPtr config, int option);
    [DllImport(CG)] private static extern int CGCancelDisplayConfiguration(IntPtr config);
    [DllImport(CF)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CF)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    /// <summary>
    /// Opzioni per CGDisplayCopyAllDisplayModes con anche i modi HiDPI "duplicati": senza, su uno schermo 4K usato a
    /// 1920x1080 HiDPI (3840 pixel) i modi alla stessa misura non comparivano e il cambio di frequenza non trovava nulla.
    /// </summary>
    private static readonly Lazy<IntPtr> AllModesOptions = new(() =>
    {
        try
        {
            IntPtr cg = NativeLibrary.Load(CG), cf = NativeLibrary.Load(CF);
            IntPtr key = Marshal.ReadIntPtr(NativeLibrary.GetExport(cg, "kCGDisplayShowDuplicateLowResolutionModes"));
            IntPtr yes = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue"));
            return CFDictionaryCreate(IntPtr.Zero, new[] { key }, new[] { yes }, 1,
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
        }
        catch (Exception ex) { Console.Error.WriteLine("[frequenza] opzioni dei modi: " + ex.Message); return IntPtr.Zero; }
    });
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);
    private const int ConfigureForSession = 1;

    private static readonly object Gate = new();
    private static uint _changedDisplay;
    private static IntPtr _originalMode;
    private static long _settlingUntilTicks;

    /// <summary>
    /// Subito dopo un cambio di frequenza lo schermo HDMI si rinegozia: con un ricevitore (Marantz) schermo e uscita
    /// audio spariscono e ricompaiono per qualche secondo. Fino a questo istante l'app non deve cambiare uscita audio.
    /// </summary>
    public static bool Settling => DateTime.UtcNow.Ticks < Interlocked.Read(ref _settlingUntilTicks);
    private static void MarkSettling(double seconds) => Interlocked.Exchange(ref _settlingUntilTicks, DateTime.UtcNow.AddSeconds(seconds).Ticks);

    /// <summary>
    /// Aspetta che lo schermo cambiato sia di nuovo stabile alla frequenza voluta e che l'uscita audio scelta (se c'e')
    /// sia ricomparsa, per non avviare madVR / MPC-VR durante la rinegoziazione HDMI (D3D11 sotto Wine restava bloccato
    /// e l'apertura scadeva a ogni tentativo, 21/11). Al massimo ~8 s; vero se si e' stabilizzato.
    /// </summary>
    public static bool WaitSettled(uint display, double hz, string audioUid)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int stable = 0;
        Thread.Sleep(800); // il primo istante dopo il cambio sembra stabile ma lo schermo non si e' ancora spento
        while (clock.ElapsedMilliseconds < 8000)
        {
            double now = CurrentHz(display);
            bool screenOk = now > 0 && Math.Abs(now - hz) < 0.6;
            bool audioOk = audioUid.Length == 0 || MacAudioDevices.Outputs().Exists(d => d.Uid == audioUid);
            stable = screenOk && audioOk ? stable + 1 : 0;
            if (stable >= 4) { MarkSettling(1.5); return true; }
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>Frequenza dell'ultimo cambio riuscito (per WaitSettled).</summary>
    public static double LastTargetHz { get; private set; }

    public static bool Enabled { get => MacSettings.Get("matchRefresh", true); set => MacSettings.Set("matchRefresh", value); }

    /// <summary>Frequenze dei film riconosciute (come GetFrameRate di Windows): le altre (VFR) le gestisce il renderer.</summary>
    private static double ExactFps(double fps)
    {
        foreach (double rate in new[] { 24000 / 1001.0, 24, 25, 30000 / 1001.0, 30, 50, 60000 / 1001.0, 60 })
            if (Math.Abs(fps - rate) < .01) return rate;
        return 0;
    }

    private static bool Matches(double hz, double fps)
    {
        if (hz <= 0 || fps <= 0) return false;
        double multiple = Math.Round(hz / fps);
        return multiple >= 1 && Math.Abs(hz / multiple - fps) < .002 * Math.Max(1, fps / 24);
    }

    /// <summary>Multiplo entro l'1,2 per mille (24,000 Hz per 23,976 fps e simili).</summary>
    private static bool Close(double hz, double fps)
    {
        if (hz <= 0 || fps <= 0) return false;
        double multiple = Math.Round(hz / fps);
        return multiple >= 1 && Math.Abs(hz / multiple - fps) / fps < .0012;
    }

    /// <summary>Schermo sotto il punto (coordinate globali in punti, origine in alto a sinistra dello schermo principale).</summary>
    public static uint DisplayAt(double x, double y)
    {
        var ids = new uint[4];
        return CGGetDisplaysWithPoint(new CGPoint { X = x, Y = y }, 4, ids, out uint n) == 0 && n > 0 ? ids[0] : 0;
    }

    /// <summary>Porta lo schermo a una frequenza adatta al film; testo per il registro, null se non serviva o non c'era.</summary>
    public static string? Match(uint display, double fps)
    {
        double exact = ExactFps(fps);
        if (display == 0 || exact <= 0 || !Enabled) return null;
        lock (Gate)
        {
            IntPtr current = CGDisplayCopyDisplayMode(display);
            if (current == IntPtr.Zero) return null;
            try
            {
                double hz = CGDisplayModeGetRefreshRate(current);
                if (Matches(hz, exact) || Close(hz, exact)) return null;
                nuint w = CGDisplayModeGetWidth(current), h = CGDisplayModeGetHeight(current), pw = CGDisplayModeGetPixelWidth(current);
                IntPtr modes = CGDisplayCopyAllDisplayModes(display, AllModesOptions.Value);
                if (modes == IntPtr.Zero) return null;
                try
                {
                    IntPtr best = IntPtr.Zero; double bestHz = 0;
                    // Prima la frequenza esatta; se lo schermo non l'ha, quella che differisce di un millesimo (24 per
                    // 23,976, 30 per 29,97, 60 per 59,94: un fotogramma ripetuto ogni ~40 s, come fa Windows).
                    foreach (bool near in new[] { false, true })
                    {
                        for (nint i = 0, n = CFArrayGetCount(modes); i < n; i++)
                        {
                            IntPtr m = CFArrayGetValueAtIndex(modes, i);
                            double r = CGDisplayModeGetRefreshRate(m);
                            if (CGDisplayModeGetWidth(m) != w || CGDisplayModeGetHeight(m) != h || CGDisplayModeGetPixelWidth(m) != pw) continue;
                            // Il multiplo piu' alto, senza alzare la frequenza del desktop.
                            if ((near ? Close(r, exact) : Matches(r, exact)) && r <= hz + .1 && r > bestHz) { best = m; bestHz = r; }
                        }
                        if (best != IntPtr.Zero) break;
                    }
                    if (best == IntPtr.Zero) return $"nessuna frequenza adatta a {exact:0.###} fps (schermo a {hz:0.###} Hz)";
                    if (CGBeginDisplayConfiguration(out IntPtr config) != 0) return null;
                    if (CGConfigureDisplayWithDisplayMode(config, display, best, IntPtr.Zero) != 0 || CGCompleteDisplayConfiguration(config, ConfigureForSession) != 0)
                    {
                        CGCancelDisplayConfiguration(config);
                        return "cambio di frequenza rifiutato";
                    }
                    LastTargetHz = bestHz;
                    MarkSettling(10);
                    // Si ricorda il modo di partenza solo al primo cambio (piu' film di fila tornano a quello originale).
                    if (_changedDisplay != display)
                    {
                        RestoreCore();
                        _changedDisplay = display; _originalMode = CGDisplayModeRetain(current);
                    }
                    return $"schermo da {hz:0.###} a {bestHz:0.###} Hz per {exact:0.###} fps";
                }
                finally { CFRelease(modes); }
            }
            finally { CGDisplayModeRelease(current); }
        }
    }

    /// <summary>Frequenza attuale dello schermo (0 se non si sa).</summary>
    public static double CurrentHz(uint display)
    {
        if (display == 0) return 0;
        IntPtr mode = CGDisplayCopyDisplayMode(display);
        if (mode == IntPtr.Zero) return 0;
        try { return CGDisplayModeGetRefreshRate(mode); } finally { CGDisplayModeRelease(mode); }
    }

    /// <summary>Torna alla frequenza di prima (chiusura del film, uscita dall'app).</summary>
    public static string? Restore() { lock (Gate) return RestoreCore(); }

    private static string? RestoreCore()
    {
        if (_changedDisplay == 0 || _originalMode == IntPtr.Zero) return null;
        uint display = _changedDisplay; IntPtr mode = _originalMode;
        _changedDisplay = 0; _originalMode = IntPtr.Zero;
        try
        {
            if (CGBeginDisplayConfiguration(out IntPtr config) != 0) return null;
            CGConfigureDisplayWithDisplayMode(config, display, mode, IntPtr.Zero);
            CGCompleteDisplayConfiguration(config, ConfigureForSession);
            MarkSettling(8);
            return $"schermo tornato a {CGDisplayModeGetRefreshRate(mode):0.###} Hz";
        }
        finally { CGDisplayModeRelease(mode); }
    }
}
