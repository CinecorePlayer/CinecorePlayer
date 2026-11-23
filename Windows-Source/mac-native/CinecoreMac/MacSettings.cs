using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CinecoreMac;

/// <summary>
/// Preferenze dell'app (Impostazioni), in ~/Library/Application Support/Cinecore/settings.json. Solo scelte, mai
/// segreti: chiavi e password vanno nel Portachiavi.
/// </summary>
internal static class MacSettings
{
    // CINECORE_SETTINGS: file separato per le prove automatiche (mai le impostazioni vere dell'utente).
    private static readonly string FilePath = Environment.GetEnvironmentVariable("CINECORE_SETTINGS") is { Length: > 0 } qa ? qa : Path.Combine(PlatformPaths.CinecoreData, "settings.json");
    private static Dictionary<string, string>? _values;

    private static Dictionary<string, string> Values
    {
        get
        {
            if (_values != null) return _values;
            try
            {
                _values = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch { _values = new(); }
            return _values;
        }
    }

    public static string Get(string key, string fallback) => Values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
    public static bool Get(string key, bool fallback) => Values.TryGetValue(key, out var v) ? v == "1" : fallback;

    public static void Set(string key, string value)
    {
        Values[key] = value;
        Save();
    }

    public static void Set(string key, bool value) => Set(key, value ? "1" : "0");

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Values, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    // Scelte usate dal player.
    public static string Renderer { get => Get("renderer", "Auto"); set => Set("renderer", value); }
    /// <summary>Uscita audio "Auto (bitstream se conviene)": Dolby e DTS passano originali al ricevitore. Sul Mac spento
    /// di serie, perche' tiene l'uscita in esclusiva durante il film (gli altri suoni del Mac tacciono). Su Windows acceso di serie.</summary>
    public static bool Bitstream { get => Get("bitstream", OperatingSystem.IsWindows()); set => Set("bitstream", value); }
    public static bool StartFullscreen { get => Get("startFullscreen", false); set => Set("startFullscreen", value); }
    public static string ThemeMode { get => Get("themeMode", "system"); set => Set("themeMode", value); }
    public static string Accent { get => Get("accent", ""); set => Set("accent", value); }
    public static string Background { get => Get("background", ""); set => Set("background", value); }
    public static string SubtitleLanguage { get => Get("subtitleLanguage", ""); set => Set("subtitleLanguage", value); }
    /// <summary>
    /// madVR con il Mac in risparmio energetico: "ask" = avviso a schermo con "Passa a MPV / MPC-VR" (con le
    /// impostazioni piu' pesanti la GPU non regge i 24 fotogrammi), "light" = profilo leggero (Bicubic al posto di NGU
    /// e Jinc, SuperChromaRes spento; gli originali tornano alla chiusura), "asis" = nessun avviso. Il motore non
    /// cambia mai da solo.
    /// </summary>
    public static string MadVrLowPower
    {
        get
        {
            string v = Get("madvrLowPower", "");
            if (v is "ask" or "asis" or "light") return v;
            // Versioni precedenti: "mpcvr"/"mpv" cambiavano motore da sole; ora si chiede a schermo.
            if (v is "mpcvr" or "mpv") return "ask";
            return Get("madvrLowPowerLight", "0") == "1" ? "light" : "ask";
        }
        set => Set("madvrLowPower", value);
    }
    public static bool MadVrLowPowerLight => MadVrLowPower == "light";
    /// <summary>Uscita audio scelta (UID di CoreAudio e nome); vuoti = predefinita del Mac, che segue i cambi di macOS.</summary>
    public static string AudioDeviceUid { get => Get("audioDeviceUid", ""); set => Set("audioDeviceUid", value); }
    public static string AudioDeviceName { get => Get("audioDeviceName", ""); set => Set("audioDeviceName", value); }
    /// <summary>Ricorda il volume di ogni film (menu Audio, come Windows).</summary>
    public static bool RememberFilmVolume { get => Get("rememberFilmVolume", true); set => Set("rememberFilmVolume", value); }

    /// <summary>
    /// Motore scelto dall'avviso "madVR in risparmio energetico" (Passa a MPV / MPC-VR): vale finche' dura il
    /// risparmio energetico in questa sessione, non cambia la preferenza salvata. Null = nessuna scelta.
    /// </summary>
    public static string? LowPowerSessionRenderer;

    /// <summary>Scelta esplicita del motore dall'utente (menu o Impostazioni): annulla quella dell'avviso.</summary>
    public static void ChooseRenderer(string preference)
    {
        Renderer = preference;
        LowPowerSessionRenderer = null;
    }

    /// <summary>
    /// "Automatico" usa madVR quando il runtime Wine è disponibile.
    /// </summary>
    public static string EffectiveRenderer
    {
        get
        {
            string renderer = Renderer;
            if (MpvEngine.Available && (renderer.Equals("MPV", StringComparison.OrdinalIgnoreCase) ||
                !VideoHostClient.WineAvailable)) return "mpv";
            if (renderer.Equals("MPCVR", StringComparison.OrdinalIgnoreCase)) return "mpcvr";
            // Dall'avviso a schermo l'utente puo' aver scelto un altro motore finche' dura il risparmio energetico.
            if (MacPower.LowPowerMode && LowPowerSessionRenderer is { } chosen && (chosen != "mpv" || MpvEngine.Available)) return chosen;
            return "madvr";
        }
    }

}
