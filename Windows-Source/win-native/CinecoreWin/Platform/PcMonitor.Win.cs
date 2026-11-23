using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// Monitoraggio del PC per la pagina "PC" del telecomando: CPU, memoria, GPU (nvidia-smi), dischi, rete, batteria,
/// consumo e temperature. Si misura solo mentre qualcuno guarda la pagina (le richieste tengono vivo il campionamento).
/// </summary>
internal static class PcMonitor
{
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong Value => (ulong)High << 32 | Low; }
    [DllImport("kernel32")] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLine, BatteryFlag, BatteryPercent, SystemStatus; public int LifeTime, FullLifeTime; }
    [DllImport("kernel32")] private static extern bool GetSystemPowerStatus(out PowerStatus status);

    private static readonly object Gate = new();
    private static ulong _idle, _total;
    private static double _cpu;
    private static long _netRx, _netTx, _netAt;
    private static double _rxMbps, _txMbps;
    private static long _lastRequest, _lastGpuRun, _lastTempRun;
    private static Gpu? _gpu;
    private static double? _cpuTemp;
    private static bool _tempUnavailable, _gpuUnavailable, _sampling;

    private sealed record Gpu(string Name, double Load, double MemUsed, double MemTotal, double Temp, double Power, double PowerLimit, double Fan, double Clock);

    public static object Snapshot()
    {
        Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
        EnsureSampling();
        lock (Gate)
        {
            var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            GlobalMemoryStatusEx(ref mem);
            var disks = new List<object>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    disks.Add(new { name = d.Name.TrimEnd('\\'), label = d.VolumeLabel, total = d.TotalSize, free = d.AvailableFreeSpace });
                }
                catch { }
            }
            object? battery = null;
            if (GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.BatteryPercent <= 100)
                battery = new { percent = (int)ps.BatteryPercent, charging = ps.AcLine == 1, minutesLeft = ps.LifeTime > 0 ? ps.LifeTime / 60 : -1 };
            var g = _gpu;
            return new
            {
                available = true,
                machine = Environment.MachineName,
                uptimeMinutes = Environment.TickCount64 / 60000,
                cpu = new { load = Math.Round(_cpu, 1), cores = Environment.ProcessorCount, temp = _cpuTemp },
                memory = new { total = mem.TotalPhys, used = mem.TotalPhys - mem.AvailPhys, load = mem.Load },
                gpu = g == null ? null : new { name = g.Name, load = g.Load, memUsed = g.MemUsed, memTotal = g.MemTotal, temp = g.Temp, power = g.Power, powerLimit = g.PowerLimit, fan = g.Fan, clock = g.Clock },
                disks,
                network = new { rxMbps = Math.Round(_rxMbps, 2), txMbps = Math.Round(_txMbps, 2) },
                battery,
                // Consumo: la GPU lo dichiara (nvidia-smi); la CPU no senza un driver del kernel.
                power = new { gpu = g?.Power, onBattery = battery != null && ps.AcLine == 0 },
            };
        }
    }

    private static void EnsureSampling()
    {
        lock (Gate)
        {
            if (_sampling) return;
            _sampling = true;
        }
        new Thread(() =>
        {
            try
            {
                // Campiona finche' la pagina chiede (poi si ferma da solo dopo 15 s di silenzio).
                while (Environment.TickCount64 - Interlocked.Read(ref _lastRequest) < 15000)
                {
                    SampleCpu();
                    SampleNetwork();
                    long now = Environment.TickCount64;
                    if (!_gpuUnavailable && now - _lastGpuRun > 2000) { _lastGpuRun = now; SampleGpu(); }
                    if (!_tempUnavailable && now - _lastTempRun > 10000) { _lastTempRun = now; SampleCpuTemp(); }
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("[pc] " + ex.Message); }
            finally { lock (Gate) _sampling = false; }
        }) { IsBackground = true, Name = "monitor PC", Priority = ThreadPriority.BelowNormal }.Start();
    }

    private static void SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;
        ulong i = idle.Value, t = kernel.Value + user.Value;
        lock (Gate)
        {
            if (_total != 0 && t > _total)
                _cpu = Math.Clamp(100.0 * (1 - (double)(i - _idle) / (t - _total)), 0, 100);
            _idle = i; _total = t;
        }
    }

    private static void SampleNetwork()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var st = n.GetIPStatistics();
                rx += st.BytesReceived; tx += st.BytesSent;
            }
        }
        catch { return; }
        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (_netAt != 0 && now > _netAt)
            {
                double sec = (now - _netAt) / 1000.0;
                _rxMbps = Math.Max(0, (rx - _netRx) * 8 / 1e6 / sec);
                _txMbps = Math.Max(0, (tx - _netTx) * 8 / 1e6 / sec);
            }
            _netRx = rx; _netTx = tx; _netAt = now;
        }
    }

    private static string? _smi;

    private static void SampleGpu()
    {
        _smi ??= new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" }.FirstOrDefault(File.Exists) ?? "";
        if (_smi.Length == 0) { _gpuUnavailable = true; return; }
        try
        {
            var psi = new ProcessStartInfo(_smi, "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,power.limit,fan.speed,clocks.gr --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string line = p.StandardOutput.ReadLine() ?? "";
            p.WaitForExit(3000);
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length < 9) { _gpuUnavailable = true; return; }
            double N(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
            _gpu = new Gpu(f[0], N(f[1]), N(f[2]), N(f[3]), N(f[4]), N(f[5]), N(f[6]), N(f[7]), N(f[8]));
        }
        catch { _gpuUnavailable = true; }
    }

    private static void SampleCpuTemp()
    {
        // Zona termica ACPI: molte schede madri la espongono solo agli amministratori; se manca la si toglie.
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"(Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction Stop | Select-Object -First 1).CurrentTemperature\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tenthsKelvin) && tenthsKelvin > 2000)
                _cpuTemp = Math.Round(tenthsKelvin / 10 - 273.15, 1);
            else _tempUnavailable = true;
        }
        catch { _tempUnavailable = true; }
    }
}
