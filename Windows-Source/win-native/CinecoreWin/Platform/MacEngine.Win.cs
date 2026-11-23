using System;
using System.Collections.Generic;
using System.IO;

namespace CinecoreMac;

/// <summary>
/// Il "motore Windows" (madVR, MPC Video Renderer, LAV Filters, XySubFilter): sul Mac va scaricato con Wine, su
/// Windows sono i componenti di Cinecore Player (third-parties) e i filtri installati nel sistema. Niente da scaricare:
/// stessa interfaccia, sempre installato quando c'e' il VideoHost.
/// </summary>
internal static class MacEngine
{
    public sealed record Part(string Name, long Size, string Sha256);
    public sealed record Manifest(string Version, string BaseUrl, string ArchiveSha256, long ArchiveSize, long InstalledSize, List<Part> Parts);

    public static event Action? Changed;

    public static string Dir => AppContext.BaseDirectory;
    public static bool Dev => false;
    public static string WineApp => "";
    public static string Prefix => "";
    public static string VtBridge => "";
    public static string VideoHostExe => Path.Combine(AppContext.BaseDirectory, "VideoHost", "VideoHost.exe");
    public static bool FirstStart => false;
    public static void FirstFrameShown() { }

    public static bool HasMadVr => WinNative.ThirdParty("madVR09217", "madVR64.ax") != null;
    public static bool HasMpcVr => WinNative.ThirdParty("MpcVideoRenderer-0.9.7.2387", "MpcVideoRenderer64.ax") != null;

    public static bool Installed => File.Exists(VideoHostExe) && (HasMadVr || HasMpcVr);
    public static string? InstalledVersion => Installed ? "Windows" : null;
    public static Manifest? Available => null;
    public static bool CanInstall => false;
    public static bool Busy => false;
    public static string State => "";
    public static double Progress => 0;

    public static string SizeLabel(long bytes) => (bytes / 1e9).ToString(bytes >= 10e9 ? "0" : "0.0", System.Globalization.CultureInfo.CurrentCulture) + " GB";

    public static string Status =>
        Installed ? "Componenti di Windows: " + string.Join(", ", Parts()) + "."
        : File.Exists(VideoHostExe) ? "madVR e MPC Video Renderer non trovati nei componenti (third-parties): i film usano mpv."
        : "VideoHost non trovato accanto all'app: i film usano mpv.";

    private static IEnumerable<string> Parts()
    {
        if (HasMadVr) yield return "madVR";
        if (HasMpcVr) yield return "MPC Video Renderer";
        yield return "LAV Filters";
        if (WinNative.ThirdParty("XySubFilter_3.1.0.752_x64", "XySubFilter.dll") != null) yield return "XySubFilter";
    }

    public static void Cancel() { }
    public static void StartInstall() => Changed?.Invoke();
    internal static void Personalize(string engineDir, string finalDir) { }
}
