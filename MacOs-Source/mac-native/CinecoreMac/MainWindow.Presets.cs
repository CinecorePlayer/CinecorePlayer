using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// Preset dei renderer, scelti all'apertura di ogni film prima di avviare il motore, e dal menu del tasto destro
/// (Video › Preset):
/// - Automatico: sceglie il player (RenderAuto: renderer, Mac, risparmio energetico, film). Predefinito.
/// - Personalizzato: le automazioni dell'utente (Impostazioni › Componenti › Preset); se nessuna vale, Impostazioni.
/// - Disattiva preset: valgono sempre e solo le Impostazioni (resta anche ai prossimi avvii).
/// - Un preset preciso: solo finche' l'app resta aperta.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>"auto", "custom", "" = Impostazioni normali, altrimenti l'id del preset scelto dal menu.</summary>
    private string _presetMode = MacSettings.Get("presetMode", "auto") switch { "custom" => "custom", "off" => "", _ => "auto" };
    private string? _announcedPresetId;
    /// <summary>Cosa ha scelto Automatico per il film in corso (per il menu).</summary>
    private string? _autoPresetName;

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGDisplayIsBuiltin(uint display);

    /// <summary>Preset per il film che sta per partire con questo motore (null = Impostazioni normali).</summary>
    private async Task<RenderPresets.Preset?> ChoosePresetAsync(string file, string renderer)
    {
        _autoPresetName = null;
        if (_presetMode == "") return null;
        if (_presetMode != "auto" && _presetMode != "custom")
            return RenderPresets.Find(_presetMode) is { } manual && manual.Renderer == renderer ? manual : null;
        if (_presetMode == "custom" && !RenderPresets.HasRules(renderer)) return null;
        ProbeData? probe = _probedFile == file ? _probe : null;
        // Le condizioni sul film (risoluzione, HDR, fotogrammi) aspettano la lettura del file, al massimo 2 secondi.
        bool needsSource = _presetMode == "auto" || RenderPresets.NeedsSource(renderer);
        if (probe == null && needsSource && _probedFile == file && _probeTask is { } task)
        {
            if (await Task.WhenAny(task, Task.Delay(2000)) == task) probe = task.Result;
        }
        bool? hdr = probe is { HasVideo: true } ? probe.Transfer is "smpte2084" or "arib-std-b67" || probe.DolbyVisionProfile > 0 : null;
        bool? internalScreen = null;
        try
        {
            uint display = MacDisplayRate.DisplayAt(Position.X + ClientSize.Width / 2, Position.Y + ClientSize.Height / 2);
            if (display != 0) internalScreen = CGDisplayIsBuiltin(display) != 0;
        }
        catch (Exception) { }
        var context = new RenderPresets.Context(probe?.Width ?? 0, probe?.Height ?? 0, hdr, probe?.Fps ?? 0,
            MacPower.LowPowerMode, MacPower.OnBattery, internalScreen);
        if (_presetMode == "custom") return RenderPresets.Match(renderer, context);
        // Automatico: l'area del video in pixel veri (quelli che il renderer disegna).
        double scale = RenderScaling;
        var choice = RenderAuto.Choose(renderer, context, probe, ClientSize.Width * scale, ClientSize.Height * scale);
        if (choice == null) return null;
        Console.Error.WriteLine("[preset] automatico: " + choice.Preset.Name + " (" + renderer + ") - " + choice.Reason);
        _autoPresetName = choice.Preset.Name;
        return choice.Preset;
    }

    /// <summary>Applica il preset per il motore e lo annuncia quando cambia.</summary>
    private void ApplyPreset(RenderPresets.Preset? preset)
    {
        ComponentSettings.ActivePreset = preset;
        if (preset?.Id != _announcedPresetId)
        {
            _announcedPresetId = preset?.Id;
            if (preset != null) _osd.Show((_presetMode == "auto" ? "Automatico: " : "Preset: ") + preset.Name, 2200);
        }
        Console.Error.WriteLine("[preset] " + (preset == null ? "nessuno" : preset.Name + " (" + preset.Renderer + ")"));
    }

    private MenuEntry BuildPresetMenu(bool hasEngine)
    {
        string engine = _renderer;
        var presets = RenderPresets.All.Where(p => p.Renderer == engine).ToList();
        var root = new MenuEntry("Preset", "sliders-horizontal") { Children = new() };
        // Accanto ad Automatico, in grigio, il livello scelto per il film in corso.
        root.Children.Add(new MenuEntry("Automatico", "level-auto", () => ChoosePreset("auto"))
            { Checked = _presetMode == "auto", Value = _presetMode == "auto" && hasEngine ? _autoPresetName : null });
        root.Children.Add(new MenuEntry("Personalizzato", "square-pen", () => ChoosePreset("custom"))
            { Checked = _presetMode == "custom", Value = _presetMode == "custom" && hasEngine ? ComponentSettings.ActivePreset?.Name ?? "Impostazioni" : null });
        root.Children.Add(new MenuEntry("Disattiva preset", "power", () => ChoosePreset("")) { Checked = _presetMode == "" });
        if (presets.Count > 0) root.Children.Add(MenuEntry.Sep());
        foreach (var preset in presets)
        {
            string id = preset.Id;
            root.Children.Add(new MenuEntry(preset.Name, PresetIcon(preset), () => ChoosePreset(id)) { Checked = _presetMode == id });
        }
        root.Children.Add(MenuEntry.Sep());
        root.Children.Add(new MenuEntry("Gestisci preset…", "settings", () => OpenSettings("Preset")));
        return root;
    }

    /// <summary>
    /// Icona della voce: tacche che crescono con il livello (Basso 1 … Ultra 4), disco per "DVD e bassa qualità",
    /// cursori per i preset creati dall'utente.
    /// </summary>
    private static string PresetIcon(RenderPresets.Preset preset)
    {
        string id = preset.Id;
        if (id.EndsWith("-low", StringComparison.Ordinal)) return "level-low";
        if (id.EndsWith("-medium", StringComparison.Ordinal)) return "level-medium";
        if (id.EndsWith("-high", StringComparison.Ordinal)) return "level-high";
        if (id.EndsWith("-ultra", StringComparison.Ordinal)) return "level-ultra";
        if (id.EndsWith("-dvd", StringComparison.Ordinal)) return "disc";
        return "sliders-horizontal";
    }

    private void ChoosePreset(string mode)
    {
        _presetMode = mode;
        // Automatico, Personalizzato e Disattiva restano anche ai prossimi avvii dell'app; un preset preciso solo per questa volta.
        if (mode is "auto" or "custom" or "") MacSettings.Set("presetMode", mode == "" ? "off" : mode);
        string text = mode switch
        {
            "auto" => "Preset automatico", "custom" => "Preset personalizzati", "" => "Preset disattivati: valgono le Impostazioni",
            _ => "Preset: " + (RenderPresets.Find(mode)?.Name ?? ""),
        };
        _announcedPresetId = "-";
        _osd.Show(text, 2000);
        // Il motore riparte dallo stesso punto con le voci del preset (come al cambio di renderer).
        if (_file != null && _video.IsRunning) { _announcedPresetId = null; SetRenderer(_renderer, restart: true); }
    }
}
