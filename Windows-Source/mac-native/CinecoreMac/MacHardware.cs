using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CinecoreMac;

/// <summary>
/// Che Mac e': chip (M1…M5), core della GPU, modello senza ventola (MacBook Air). Letto una volta da sysctl e dal
/// registro di IOKit (AGXAccelerator "gpu-core-count"), senza avviare processi (vedi ProcessLauncher: durante un film
/// un fork dell'app ferma libmpv).
/// </summary>
internal static class MacHardware
{
    private const string LibC = "/usr/lib/libc.dylib";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(LibC)] private static extern int sysctlbyname(string name, byte[]? value, ref nint size, IntPtr newValue, nint newSize);
    [DllImport(IOKit)] private static extern IntPtr IOServiceMatching(string name);
    [DllImport(IOKit)] private static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);
    [DllImport(IOKit)] private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);

    private static readonly Lazy<(string Chip, string Model, int GpuCores)> Info = new(Read);

    /// <summary>Es. "Apple M4".</summary>
    public static string Chip => Info.Value.Chip;
    /// <summary>Es. "Mac16,12".</summary>
    public static string Model => Info.Value.Model;
    /// <summary>Core della GPU (0 = non letto).</summary>
    public static int GpuCores => Info.Value.GpuCores;

    /// <summary>MacBook Air: niente ventola, sotto carico lungo rallenta (prestazioni sostenute piu' basse).</summary>
    public static bool Fanless =>
        Model.StartsWith("MacBookAir", StringComparison.Ordinal) ||
        // Mac con chip Apple: i modelli "MacNN,x" non dicono il nome; gli Air M2/M3/M4 noti.
        Model is "Mac14,2" or "Mac14,15" or "Mac15,12" or "Mac15,13" or "Mac16,12" or "Mac16,13";

    /// <summary>
    /// Potenza grafica rispetto al Mac delle misure (MacBook Air M4, GPU a 10 core) = 1. Core della GPU per una stima
    /// della generazione del chip; Pro/Max/Ultra lo dicono gia' i core.
    /// </summary>
    public static double GpuScore
    {
        get
        {
            int cores = GpuCores > 0 ? GpuCores : 8;
            string chip = Chip;
            double generation = chip.Contains("M1") ? 0.7 : chip.Contains("M2") ? 0.82 : chip.Contains("M3") ? 0.92
                : chip.Contains("M4") ? 1.0 : chip.Contains("M5") ? 1.2 : 1.0;
            return cores / 10.0 * generation;
        }
    }

    public static string Describe() => $"{Chip} ({Model}), GPU {GpuCores} core{(Fanless ? ", senza ventola" : "")}";

    private static (string, string, int) Read()
    {
        string chip = Sysctl("machdep.cpu.brand_string"), model = Sysctl("hw.model");
        int cores = 0;
        try
        {
            uint service = IOServiceGetMatchingService(0, IOServiceMatching("AGXAccelerator"));
            if (service != 0)
            {
                IntPtr key = CFStringCreateWithCString(IntPtr.Zero, "gpu-core-count", 0x08000100);
                IntPtr value = IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
                if (value != IntPtr.Zero)
                {
                    if (CFNumberGetValue(value, 4 /* kCFNumberSInt64Type */, out long n)) cores = (int)n;
                    CFRelease(value);
                }
                CFRelease(key);
                IOObjectRelease(service);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[hardware] " + ex.Message); }
        return (chip, model, cores);
    }

    private static string Sysctl(string name)
    {
        try
        {
            nint size = 0;
            if (sysctlbyname(name, null, ref size, IntPtr.Zero, 0) != 0 || size <= 0) return "";
            var buffer = new byte[size];
            if (sysctlbyname(name, buffer, ref size, IntPtr.Zero, 0) != 0) return "";
            return Encoding.UTF8.GetString(buffer, 0, (int)size).TrimEnd('\0');
        }
        catch (Exception) { return ""; }
    }
}
