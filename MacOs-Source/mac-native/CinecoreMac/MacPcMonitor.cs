using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// Telecomando › Impostazioni › Questo Mac: CPU, memoria, GPU (Apple), batteria, rete e dischi, letti dal sistema
/// (mach, IOKit, .NET) senza avviare processi. Stesso formato di PcMonitor di Windows; temperature e consumi macOS
/// non li espone senza privilegi, quindi mancano.
/// </summary>
internal static class MacPcMonitor
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(LibSystem)] private static extern uint mach_host_self();
    [DllImport(LibSystem)] private static extern int host_statistics(uint host, int flavor, int[] info, ref int count);
    [DllImport(LibSystem)] private static extern int host_statistics64(uint host, int flavor, int[] info, ref int count);
    [DllImport(LibSystem)] private static extern int sysctlbyname(string name, out long value, ref IntPtr size, IntPtr newp, IntPtr newlen);
    [DllImport(LibSystem)] private static extern int sysctlbyname(string name, byte[] value, ref IntPtr size, IntPtr newp, IntPtr newlen);

    [DllImport(IOKit)] private static extern IntPtr IOServiceMatching(string name);
    [DllImport(IOKit)] private static extern int IOServiceGetMatchingServices(uint port, IntPtr matching, out uint iterator);
    [DllImport(IOKit)] private static extern uint IOIteratorNext(uint iterator);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);
    [DllImport(IOKit)] private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint encoding);
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);
    [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CF)] private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);
    [DllImport(CF)] private static extern IntPtr CFGetTypeID(IntPtr obj);
    [DllImport(CF)] private static extern IntPtr CFNumberGetTypeID();
    [DllImport(CF)] private static extern IntPtr CFBooleanGetTypeID();
    [DllImport(CF)] private static extern IntPtr CFStringGetTypeID();
    [DllImport(CF)] private static extern IntPtr CFDataGetTypeID();
    [DllImport(CF)] private static extern bool CFBooleanGetValue(IntPtr b);
    [DllImport(CF)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, long size, uint encoding);
    [DllImport(CF)] private static extern long CFDataGetLength(IntPtr d);
    [DllImport(CF)] private static extern IntPtr CFDataGetBytePtr(IntPtr d);

    private const uint Utf8 = 0x08000100;
    private static readonly object Gate = new();
    private static long[]? _cpuPrev;
    private static (long Rx, long Tx, long Ticks)? _netPrev;
    private static string? _machine;

    public static object? Snapshot()
    {
        lock (Gate)
        {
            try
            {
                long total = SysLong("hw.memsize");
                return new
                {
                    available = true,
                    machine = _machine ??= MachineName(),
                    cpu = new { load = CpuLoad(), cores = Environment.ProcessorCount },
                    memory = MemoryUsed(total) is var used ? new { used, total, load = total > 0 ? used * 100.0 / total : 0 } : null,
                    gpu = Gpu(total),
                    battery = Battery(),
                    network = Network(),
                    disks = Disks(),
                };
            }
            catch (Exception ex) { Console.Error.WriteLine("[pc] " + ex.Message); return new { available = false }; }
        }
    }

    private static long SysLong(string name)
    {
        IntPtr size = 8;
        return sysctlbyname(name, out long value, ref size, IntPtr.Zero, IntPtr.Zero) == 0 ? value : 0;
    }

    private static string? SysString(string name)
    {
        IntPtr size = 256;
        var buffer = new byte[256];
        return sysctlbyname(name, buffer, ref size, IntPtr.Zero, IntPtr.Zero) == 0
            ? System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Max(0, (int)size - 1)).TrimEnd('\0') : null;
    }

    private static string MachineName()
    {
        string chip = SysString("machdep.cpu.brand_string") ?? "";
        string name = Environment.MachineName.Replace(".local", "", StringComparison.OrdinalIgnoreCase);
        return chip.Length > 0 ? name + " · " + chip : name;
    }

    /// <summary>Carico della CPU fra due letture (HOST_CPU_LOAD_INFO: user, system, idle, nice).</summary>
    private static double CpuLoad()
    {
        var info = new int[4];
        int count = 4;
        if (host_statistics(mach_host_self(), 3, info, ref count) != 0) return 0;
        var now = info.Select(v => (long)(uint)v).ToArray();
        var prev = _cpuPrev;
        _cpuPrev = now;
        if (prev == null) return 0;
        long busy = now[0] - prev[0] + now[1] - prev[1] + now[3] - prev[3], idle = now[2] - prev[2];
        return busy + idle > 0 ? Math.Round(busy * 100.0 / (busy + idle), 1) : 0;
    }

    /// <summary>Memoria usata come in Monitoraggio Attività: app (interne meno eliminabili) + wired + compressa.</summary>
    private static long MemoryUsed(long total)
    {
        var info = new int[40];
        int count = 38;
        if (host_statistics64(mach_host_self(), 4, info, ref count) != 0) return 0;
        long page = SysLong("hw.pagesize");
        if (page <= 0) page = 16384;
        long wired = (uint)info[3], purgeable = (uint)info[22], compressor = (uint)info[32], internalPages = (uint)info[35];
        return Math.Clamp((internalPages - purgeable + wired + compressor) * page, 0, total > 0 ? total : long.MaxValue);
    }

    private static IntPtr Key(string s) => CFStringCreateWithCString(IntPtr.Zero, s, Utf8);

    private static IntPtr Property(uint entry, string name)
    {
        IntPtr key = Key(name);
        try { return IORegistryEntryCreateCFProperty(entry, key, IntPtr.Zero, 0); }
        finally { CFRelease(key); }
    }

    private static long? Number(IntPtr value)
    {
        if (value == IntPtr.Zero) return null;
        if (CFGetTypeID(value) == CFBooleanGetTypeID()) return CFBooleanGetValue(value) ? 1 : 0;
        return CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, 4, out long v) ? v : null; // kCFNumberSInt64Type
    }

    private static long? DictNumber(IntPtr dict, string name)
    {
        IntPtr key = Key(name);
        try { return Number(CFDictionaryGetValue(dict, key)); }
        finally { CFRelease(key); }
    }

    private static string? Text(IntPtr value)
    {
        if (value == IntPtr.Zero) return null;
        if (CFGetTypeID(value) == CFStringGetTypeID())
        {
            var buffer = new byte[256];
            return CFStringGetCString(value, buffer, buffer.Length, Utf8) ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0') : null;
        }
        if (CFGetTypeID(value) == CFDataGetTypeID())
        {
            var bytes = new byte[Math.Min(256, CFDataGetLength(value))];
            Marshal.Copy(CFDataGetBytePtr(value), bytes, 0, bytes.Length);
            return System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        return null;
    }

    private static IEnumerable<uint> Services(string className)
    {
        if (IOServiceGetMatchingServices(0, IOServiceMatching(className), out uint iterator) != 0) yield break;
        try
        {
            for (uint s = IOIteratorNext(iterator); s != 0; s = IOIteratorNext(iterator)) yield return s;
        }
        finally { IOObjectRelease(iterator); }
    }

    /// <summary>GPU Apple: utilizzo e memoria in uso (PerformanceStatistics dell'acceleratore). Memoria unificata: il totale e' la RAM.</summary>
    private static object? Gpu(long totalMemory)
    {
        foreach (uint service in Services("IOAccelerator"))
        {
            try
            {
                IntPtr stats = Property(service, "PerformanceStatistics");
                if (stats == IntPtr.Zero) continue;
                try
                {
                    long? load = DictNumber(stats, "Device Utilization %") ?? DictNumber(stats, "GPU Activity(%)");
                    if (load == null) continue;
                    long inUse = DictNumber(stats, "In use system memory") ?? 0;
                    IntPtr modelRef = Property(service, "model");
                    string model = Text(modelRef) ?? "Apple GPU";
                    if (modelRef != IntPtr.Zero) CFRelease(modelRef);
                    long cores = 0;
                    IntPtr coresRef = Property(service, "gpu-core-count");
                    if (coresRef != IntPtr.Zero) { cores = Number(coresRef) ?? 0; CFRelease(coresRef); }
                    return new
                    {
                        name = model + (cores > 0 ? $" ({cores} core)" : ""),
                        load = (double)load.Value,
                        memUsed = inUse / 1048576.0,
                        memTotal = totalMemory / 1048576.0,
                    };
                }
                finally { CFRelease(stats); }
            }
            finally { IOObjectRelease(service); }
        }
        return null;
    }

    private static object? Battery()
    {
        foreach (uint service in Services("AppleSmartBattery"))
        {
            try
            {
                long? Read(string name)
                {
                    IntPtr v = Property(service, name);
                    if (v == IntPtr.Zero) return null;
                    try { return Number(v); } finally { CFRelease(v); }
                }
                long current = Read("CurrentCapacity") ?? -1, max = Read("MaxCapacity") ?? 100;
                if (current < 0 || max <= 0) return null;
                // Sui Mac Apple Silicon MaxCapacity e' 100 (percentuale); sugli altri sono mAh.
                int percent = (int)Math.Clamp(Math.Round(current * 100.0 / max), 0, 100);
                bool charging = (Read("IsCharging") ?? 0) != 0 || (Read("ExternalConnected") ?? 0) != 0;
                long minutes = Read(charging ? "AvgTimeToFull" : "AvgTimeToEmpty") ?? 0;
                return new { percent, charging, minutesLeft = minutes is > 0 and < 65535 ? minutes : 0 };
            }
            finally { IOObjectRelease(service); }
        }
        return null;
    }

    private static object Network()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived; tx += stats.BytesSent;
            }
        }
        catch { }
        long ticks = Environment.TickCount64;
        double rxMbps = 0, txMbps = 0;
        if (_netPrev is { } prev && ticks > prev.Ticks)
        {
            double seconds = (ticks - prev.Ticks) / 1000.0;
            rxMbps = Math.Max(0, rx - prev.Rx) * 8 / 1e6 / seconds;
            txMbps = Math.Max(0, tx - prev.Tx) * 8 / 1e6 / seconds;
        }
        _netPrev = (rx, tx, ticks);
        return new { rxMbps, txMbps };
    }

    /// <summary>Il disco del sistema e quelli in /Volumes (niente volumi di sistema nascosti).</summary>
    private static object[] Disks()
    {
        var list = new List<object>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                string root = drive.RootDirectory.FullName;
                bool system = root == "/";
                if (!system && !root.StartsWith("/Volumes/", StringComparison.Ordinal)) continue;
                if (!drive.IsReady || drive.TotalSize <= 0) continue;
                // Volumi di servizio di macOS (Recovery, Preboot, VM, Update) e immagini disco di sistema: non sono dischi.
                string leaf = Path.GetFileName(root.TrimEnd('/'));
                if (!system && (leaf is "Recovery" or "Preboot" or "VM" or "Update" or "xarts" or "iSCPreboot" or "Hardware" || drive.DriveType == DriveType.Ram)) continue;
                string name = system ? "Macintosh HD" : Path.GetFileName(root.TrimEnd('/'));
                list.Add(new { name, label = system ? "" : drive.DriveFormat, total = drive.TotalSize, free = drive.AvailableFreeSpace });
            }
            catch { }
        }
        return list.ToArray();
    }
}
