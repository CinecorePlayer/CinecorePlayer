using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CinecoreMac;

/// <summary>Versioni Homebrew opzionali. Il pacchetto firmato resta disponibile come riserva.</summary>
internal static class NativeComponentUpdates
{
    public static readonly string? Brew = new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew" }.FirstOrDefault(File.Exists);
    private static readonly string? Prefix = Brew == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(Brew));

    // Le librerie native vengono risolte una volta per processo, quindi i cambi di origine valgono al riavvio.
    public static readonly bool UseBrewFfmpeg = MacSettings.Get("nativeHomebrewFfmpeg", false);
    public static readonly bool UseBrewMpv = MacSettings.Get("nativeHomebrewMpv", false);
    public static readonly bool UseBrewVulkan = MacSettings.Get("nativeHomebrewVulkan", false);

    public static string? BrewFile(string relative)
    {
        if (Prefix == null) return null;
        string path = Path.Combine(Prefix, relative);
        return File.Exists(path) ? path : null;
    }

    public static string? BrewMpv => BrewFile("opt/mpv/lib/libmpv.2.dylib");
    public static string? BrewVulkan => BrewFile("opt/molten-vk/etc/vulkan/icd.d/MoltenVK_icd.json");

    public static Dictionary<string, string> InstalledVersions()
    {
        if (Brew == null) return new();
        var (_, output, _) = ProcessLauncher.Run(Brew, new[] { "list", "--versions", "ffmpeg", "mpv", "libplacebo", "molten-vk" },
            true, 20000, CancellationToken.None);
        return ReadVersions(Encoding.UTF8.GetString(output));
    }

    public static Dictionary<string, string> ReadVersions(string list)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) result[parts[0]] = parts[^1];
        }
        return result;
    }

    /// <summary>Installa o aggiorna la formula selezionata. Per libplacebo serve anche libmpv collegata da Homebrew.</summary>
    public static (string? Version, string? ActivationKey, string? Error) Update(string formula)
    {
        string? key = formula switch
        {
            "ffmpeg" => "nativeHomebrewFfmpeg",
            "mpv" or "libplacebo" => "nativeHomebrewMpv",
            "molten-vk" => "nativeHomebrewVulkan",
            _ => null,
        };
        if (key == null || Brew == null) return (null, null, "Homebrew richiesto");
        var args = new List<string> { "install", "--formula", formula };
        if (formula == "libplacebo" && BrewMpv == null) args.Add("mpv");
        var (exit, _, error) = ProcessLauncher.Run(Brew, args, true, 30 * 60_000, CancellationToken.None);
        if (exit != 0)
        {
            Console.Error.WriteLine("[componenti] Homebrew " + formula + ": " + error);
            return (null, null, "Aggiornamento non riuscito");
        }
        bool ready = formula switch
        {
            "ffmpeg" => BrewFile("opt/ffmpeg/bin/ffmpeg") != null && BrewFile("opt/ffmpeg/bin/ffprobe") != null,
            "mpv" or "libplacebo" => BrewMpv != null,
            "molten-vk" => BrewVulkan != null,
            _ => false,
        };
        var versions = InstalledVersions();
        return ready && versions.TryGetValue(formula, out var version)
            ? (version, key, null) : (null, null, "Componente non trovato dopo l’installazione");
    }
}
