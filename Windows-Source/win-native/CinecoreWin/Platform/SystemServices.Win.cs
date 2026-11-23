using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CinecoreMac;

/// <summary>Risparmio energetico di Windows (Risparmio batteria acceso): GPU e CPU rallentate come sul Mac.</summary>
internal static class MacPower
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static bool LowPowerMode
    {
        get
        {
            string? forced = Environment.GetEnvironmentVariable("CINECORE_LOW_POWER");
            if (forced is "0" or "1") return forced == "1";
            try { return GetSystemPowerStatus(out var s) && s.SystemStatusFlag == 1; }
            catch { return false; }
        }
    }

    /// <summary>Il PC va a batteria (alimentatore scollegato). Senza batteria (fisso) e' sempre falso.</summary>
    public static bool OnBattery
    {
        get
        {
            string? forced = Environment.GetEnvironmentVariable("CINECORE_ON_BATTERY");
            if (forced is "0" or "1") return forced == "1";
            try { return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0 && s.BatteryFlag != 128; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Stato termico come quello di macOS (0 normale … 3 critico). Windows non lo dice alle app: 0, salvo
    /// CINECORE_THERMAL=0..3 per le prove.
    /// </summary>
    public static int ThermalState =>
        int.TryParse(Environment.GetEnvironmentVariable("CINECORE_THERMAL"), out int forced) ? Math.Clamp(forced, 0, 3) : 0;
}

/// <summary>
/// Archivi lenti da leggere a salti: unita' di rete (SMB, NFS, WebDAV), percorsi UNC e montaggi FUSE (rclone con
/// WinFsp, come M: della libreria). Il giudizio per unita' si tiene in cache per 30 secondi.
/// </summary>
internal static class MacStorage
{
    private static readonly ConcurrentDictionary<string, (bool Slow, DateTime At)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Disco rclone o FUSE (WinFsp): li' ogni lettura va su Internet, a differenza di una cartella di rete SMB.</summary>
    public static bool IsRcloneMount(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("rclone", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return false;
            string format = drive.DriveFormat ?? "";
            return format.Contains("FUSE", StringComparison.OrdinalIgnoreCase) || format.Contains("rclone", StringComparison.OrdinalIgnoreCase)
                || drive.VolumeLabel.Contains("rclone", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsSlowMount(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("://", StringComparison.Ordinal)) return true;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        string? root;
        try { root = Path.GetPathRoot(path); } catch { return false; }
        if (string.IsNullOrEmpty(root)) return false;
        if (Cache.TryGetValue(root, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(30)) return hit.Slow;
        bool slow = false;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network) slow = true;
            else if (drive.IsReady)
            {
                string format = drive.DriveFormat ?? "";
                slow = format.Contains("FUSE", StringComparison.OrdinalIgnoreCase) || format.Contains("rclone", StringComparison.OrdinalIgnoreCase)
                    || drive.VolumeLabel.Contains("rclone", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        Cache[root] = (slow, DateTime.UtcNow);
        return slow;
    }

    public static string Describe()
    {
        var parts = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
            parts.Add(d.Name + (IsSlowMount(d.Name) ? " (lento)" : ""));
        return string.Join("; ", parts);
    }
}

/// <summary>
/// Avvia programmi esterni (ffmpeg, ffprobe…). Su Windows non c'e' il costo del fork del Mac: si avvia il programma
/// direttamente, senza finestra di console, a priorita' bassa quando lavora in sottofondo.
/// </summary>
internal static class ProcessLauncher
{
    public static (int Exit, byte[] Stdout, string Stderr) Run(string exe, IEnumerable<string> args, bool background,
        int timeoutMs, CancellationToken ct, bool adjustPriority = true)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) { return (-1, Array.Empty<byte>(), ex.Message); }
        if (p == null) return (-1, Array.Empty<byte>(), "avvio non riuscito");
        using (p)
        {
            try { p.StandardInput.Close(); } catch { }
            if (adjustPriority)
                try { p.PriorityClass = background ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal; } catch { }
            var output = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(output);
            var error = p.StandardError.ReadToEndAsync();
            bool killed = false;
            using (ct.Register(() => { killed = true; try { p.Kill(true); } catch { } }))
            {
                if (!p.WaitForExit(timeoutMs)) { killed = true; try { p.Kill(true); } catch { } }
                try { copy.Wait(5000); } catch { }
                try { error.Wait(2000); } catch { }
            }
            ct.ThrowIfCancellationRequested();
            string stderr = error.IsCompletedSuccessfully ? error.Result : "";
            return (killed ? -1 : SafeExit(p), output.ToArray(), stderr);
        }
    }

    private static int SafeExit(Process p) { try { return p.ExitCode; } catch { return -1; } }
}

/// <summary>
/// Programmi usati dall'app (FFmpeg, ffprobe): quelli dei componenti di Windows (third-parties\ffmpeg\win-x64),
/// poi quelli nel PATH.
/// </summary>
internal static class MacTools
{
    public static readonly System.Globalization.NumberFormatInfo Italian = new()
    {
        NumberDecimalSeparator = ",", NumberGroupSeparator = ".", PercentDecimalSeparator = ",", PercentGroupSeparator = ".",
        CurrencyDecimalSeparator = ",", CurrencyGroupSeparator = ".", NegativeSign = "-",
    };

    public static string AssetsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Assets");
    public static string? Ffmpeg { get; } = Find("ffmpeg.exe");
    public static string? Ffprobe { get; } = Find("ffprobe.exe");

    private static string? Find(string name)
    {
        if (WinNative.ThirdParty("ffmpeg", "win-x64", name) is { } bundled) return bundled;
        string local = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(local)) return local;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { string p = Path.Combine(dir.Trim(), name); if (File.Exists(p)) return p; } catch { }
        }
        Console.Error.WriteLine($"{name} non trovato (third-parties\\ffmpeg\\win-x64)");
        return null;
    }
}

internal static class NativeDiagnostics
{
    private static System.Threading.Timer? _pulse;
    private static int _pending;
    public static volatile string Context = "";

    public static void WatchDispatcher()
    {
        if (_pulse != null) return;
        double threshold = double.TryParse(Environment.GetEnvironmentVariable("CINECORE_QA_UI_MS"), out var qaMs) ? qaMs : Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") == null ? 250 : 100;
        _pulse = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _pending, 1) != 0) return;
            long start = Stopwatch.GetTimestamp();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (ms > threshold) Console.Error.WriteLine($"[ui] attesa dispatcher {ms:0} ms" + (Context.Length > 0 ? " (" + Context + ")" : ""));
                Volatile.Write(ref _pending, 0);
            }, Avalonia.Threading.DispatcherPriority.Input);
        }, null, 100, threshold < 100 ? 20 : 100);
    }

    public static void Crash(Exception error)
    {
        try
        {
            string details = DateTimeOffset.Now.ToString("O") + " " + error.GetType().FullName;
            if (error is ArgumentException argument) details += " parameter=" + argument.ParamName;
            details += " " + error.Message.Replace('\n', ' ');
            details = System.Text.RegularExpressions.Regex.Replace(details, @"(api_key|X-Emby-Token|token)=[^&\s""]+", "$1=…", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            details += Environment.NewLine + error.StackTrace + Environment.NewLine;
            File.AppendAllText(Path.Combine(WinNative.LogDir, "native-crash.log"), details);
        }
        catch { }
    }
}
