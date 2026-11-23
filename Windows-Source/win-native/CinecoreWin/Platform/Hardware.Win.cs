using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CinecoreMac;

/// <summary>
/// Che PC e' (al posto di MacHardware.cs del Mac): la scheda video piu' potente (nome e memoria dedicata dal registro
/// della classe "Schede video", senza avviare processi) e se e' un portatile (ha una batteria). Serve a RenderAuto
/// (preset Automatico) per stimare quanto lavoro di madVR regge il PC.
/// </summary>
internal static class MacHardware
{
    private static readonly Lazy<(string Gpu, long Memory)> Info = new(Read);

    /// <summary>Nome della scheda video, es. "NVIDIA GeForce RTX 3080".</summary>
    public static string Chip => Info.Value.Gpu;
    public static string Model => "";
    /// <summary>Non si leggono i core: la stima usa il modello (GpuScore).</summary>
    public static int GpuCores => 0;
    /// <summary>Memoria video dedicata in byte (0 = integrata o non letta).</summary>
    public static long GpuMemory => Info.Value.Memory;

    /// <summary>Portatile (ha una batteria): come l'Air del Mac, sotto carico lungo puo' rallentare.</summary>
    public static bool Fanless => HasBattery();

    /// <summary>
    /// Potenza per madVR rispetto al Mac delle misure (MacBook Air M4 a 10 core, madVR sotto Wine) = 1. Su Windows madVR
    /// e' nativo (niente traduzione Direct3D -> Metal) e una scheda dedicata ha molta piu' banda: i valori sono stime
    /// per famiglia, da confermare con le misure (log "[preset] automatico" e fotogrammi persi del VideoHost).
    /// Misura 23/11/2026, RTX 3080: vedi PER-MAC/OTTIMIZZAZIONI.md, voce dei preset.
    /// </summary>
    public static double GpuScore
    {
        get
        {
            if (double.TryParse(Environment.GetEnvironmentVariable("CINECORE_GPU_SCORE"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double forced) && forced > 0) return forced;
            string n = Chip.ToUpperInvariant();
            static bool Has(string s, params string[] keys) => keys.Any(s.Contains);
            if (Has(n, "RTX 50", "RTX 4090", "RTX 4080")) return 8;
            if (Has(n, "RTX 40", "RTX 3090", "RTX 3080", "RX 79", "RX 78", "RX 69", "RX 68")) return 6;
            if (Has(n, "RTX 30", "RX 77", "RX 76", "RX 67", "RX 66", "ARC A7", "ARC B5")) return 4;
            if (Has(n, "RTX 20", "GTX 16", "RX 57", "RX 56", "ARC A5")) return 3;
            if (Has(n, "GTX 10", "RX 5", "RX 4", "ARC A3")) return 2;
            // Integrate (Intel UHD/Iris, Radeon Graphics dei processori): banda della memoria di sistema.
            if (GpuMemory < 1L << 30 || Has(n, "UHD", "IRIS", "HD GRAPHICS") || n.EndsWith("RADEON(TM) GRAPHICS") || n.EndsWith("RADEON GRAPHICS")) return 0.8;
            return 2;
        }
    }

    public static string Describe() => $"{(Chip.Length > 0 ? Chip : "scheda video non letta")}{(GpuMemory > 0 ? $" {GpuMemory / (1L << 30)} GB" : "")}{(Fanless ? ", portatile" : "")}";

    private static (string, long) Read()
    {
        string best = ""; long bestMemory = -1;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return ("", 0);
            foreach (string sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string name || name.Length == 0) continue;
                // Schermi virtuali (Parsec, Meta, Spacedesk...): non disegnano niente.
                if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
                long memory = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    int i => (uint)i, _ => 0,
                };
                if (memory > bestMemory) { best = name; bestMemory = memory; }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[hardware] " + ex.Message); }
        return (best, Math.Max(0, bestMemory));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private static bool HasBattery()
    {
        try { return GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryFlag != 255; }
        catch { return false; }
    }
}

/// <summary>Schermo interno del portatile (condizione "Schermo" delle automazioni dei preset), da DisplayConfig.</summary>
internal static class WinDisplayKind
{
    /// <summary>Lo schermo che contiene il punto (pixel dello schermo) e' quello interno; null se non si sa.</summary>
    public static bool? IsInternalAt(int x, int y)
    {
        IntPtr monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        if (GetDisplayConfigBufferSizes(2 /* QDC_ONLY_ACTIVE_PATHS */, out uint pathCount, out uint modeCount) != 0) return null;
        var paths = new PathInfo[pathCount];
        var modes = new byte[modeCount * 64];
        if (QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return null;
        for (int i = 0; i < pathCount; i++)
        {
            var request = new SourceName
            {
                Header = new DeviceHeader { Type = 1 /* GET_SOURCE_NAME */, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = paths[i].Source.Adapter, Id = paths[i].Source.Id },
            };
            if (DisplayConfigGetDeviceInfo(ref request) != 0 || !string.Equals(request.Name, info.Device, StringComparison.OrdinalIgnoreCase)) continue;
            // INTERNAL (0x80000000), LVDS (6), DisplayPort incorporato (11), UDI incorporato (13).
            uint tech = paths[i].Target.Technology;
            return tech is 0x80000000 or 6 or 11 or 13;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MONITORINFOEX
    {
        public int Size; public RECT Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct SourceInfo { public Luid Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential)] private struct TargetInfo
    {
        public Luid Adapter; public uint Id, ModeIndex, Technology, Rotation, Scaling;
        public Rational Refresh; public uint Scanline; public int Available; public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceHeader { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName
    {
        public DeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathArray, ref uint modes, [Out] byte[] modeArray, IntPtr topology);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DisplayConfigGetDeviceInfo(ref SourceName request);
}
