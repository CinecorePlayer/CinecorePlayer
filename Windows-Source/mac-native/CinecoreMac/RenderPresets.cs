using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CinecoreMac;

/// <summary>
/// Preset dei renderer creati dall'utente (Impostazioni › Componenti › Preset). Ogni preset appartiene a un renderer
/// (madVR, MPC Video Renderer o MPV) e contiene solo le voci che cambia rispetto alle Impostazioni normali; le altre
/// restano come sono. Si attiva da solo all'apertura di un film quando valgono TUTTE le sue condizioni (il primo
/// dell'elenco che va bene), oppure a mano dal menu del tasto destro (Video › Preset). Non modifica mai le
/// Impostazioni salvate: vale solo per la riproduzione (vedi ComponentSettings.ActivePreset).
/// Salvati in settings.json, voce "renderPresets" (JSON). Porting Windows: stesso formato e stesse condizioni.
/// </summary>
internal static class RenderPresets
{
    internal sealed class Preset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "Nuovo preset";
        /// <summary>"madvr", "mpcvr" o "mpv".</summary>
        public string Renderer { get; set; } = "madvr";
        /// <summary>Si attiva da solo quando valgono le condizioni; altrimenti solo dal menu del tasto destro.</summary>
        public bool Automatic { get; set; } = true;
        /// <summary>Condizioni: "any" = qualsiasi. Vedi le liste ...Choices.</summary>
        public string Resolution { get; set; } = "any";
        public string Hdr { get; set; } = "any";
        public string FrameRate { get; set; } = "any";
        public string Power { get; set; } = "any";
        public string Screen { get; set; } = "any";
        /// <summary>Voci cambiate: chiave dell'impostazione (come in ComponentSettings, scheda del renderer) -> valore.</summary>
        public Dictionary<string, string> Values { get; set; } = new();
    }

    /// <summary>
    /// Automazione (come le Automazioni dell'iPhone): QUANDO valgono tutte le condizioni, ALLORA usa questo profilo.
    /// Le regole si provano in ordine: vince la prima che vale per il renderer del film.
    /// </summary>
    internal sealed class Rule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public bool Enabled { get; set; } = true;
        public string Resolution { get; set; } = "any";
        public string Hdr { get; set; } = "any";
        public string FrameRate { get; set; } = "any";
        public string Power { get; set; } = "any";
        public string Screen { get; set; } = "any";
        /// <summary>Profilo da usare (il suo renderer decide per quali film vale).</summary>
        public string PresetId { get; set; } = "";
    }

    /// <summary>Quello che si sa del film e del Mac all'apertura.</summary>
    internal sealed record Context(int Width, int Height, bool? Hdr, double Fps, bool LowPower, bool OnBattery, bool? InternalScreen);

    public static readonly (string Value, string Label)[] Renderers = { ("madvr", "madVR"), ("mpcvr", "MPC Video Renderer"), ("mpv", "MPV") };
    public static readonly (string Value, string Label)[] ResolutionChoices =
        { ("any", "Qualsiasi"), ("sd", "SD (DVD)"), ("hd", "HD 720p"), ("fhd", "Full HD 1080p"), ("uhd", "4K"), ("lt-uhd", "Sotto il 4K") };
    public static readonly (string Value, string Label)[] HdrChoices = { ("any", "Qualsiasi"), ("hdr", "Solo HDR"), ("sdr", "Solo SDR") };
    public static readonly (string Value, string Label)[] FrameRateChoices = { ("any", "Qualsiasi"), ("film", "Fino a 30 fps"), ("high", "Oltre 30 fps") };
    public static readonly (string Value, string Label)[] PowerChoices =
        { ("any", "Qualsiasi"), ("lowpower", "Risparmio energetico attivo"), ("normal", "Risparmio energetico spento"), ("battery", "A batteria"), ("ac", "Alimentatore collegato") };
    public static readonly (string Value, string Label)[] ScreenChoices = { ("any", "Qualsiasi"), ("internal", "Schermo del Mac"), ("external", "Schermo esterno") };

    public static string Label((string Value, string Label)[] list, string value) => list.FirstOrDefault(x => x.Value == value).Label ?? value;

    /// <summary>Scheda di ComponentSettings con le voci del renderer.</summary>
    public static string Tab(string renderer) => renderer switch { "mpcvr" => "MPC Video Renderer", "mpv" => "MPV", _ => "madVR" };
    public static string RendererLabel(string renderer) => Label(Renderers, renderer);

    private const string StoreKey = "renderPresets";
    private const string SeededKey = "renderPresetsBuiltIn";

    private static Preset Make(string id, string name, string renderer, params (string Key, string Value)[] values) =>
        new() { Id = id, Name = name, Renderer = renderer, Automatic = false, Values = values.ToDictionary(v => v.Key, v => v.Value) };

    /// <summary>
    /// Livelli come nei videogiochi. madVR tarato sulle misure del 23/11/2026 (MacBook M4, film 4K in risparmio
    /// energetico): Alto resta fluido (NGU alto senza SuperChromaRes, Jinc in riduzione), Ultra e' per la piena potenza.
    /// </summary>
    private const int BuiltInVersion = 2;

    private static IEnumerable<(Preset Preset, int Since)> BuiltInWithVersion() =>
        BuiltIn().Select(p => (p, p.Id.EndsWith("-dvd", StringComparison.Ordinal) ? 2 : 1));

    public static IEnumerable<Preset> BuiltIn()
    {
        yield return Make("madvr-low", "Basso", "madvr", ("chromaUp", "Bicubic60"), ("superChromaRes", "0"), ("lumaUp", "Lanczos3"),
            ("lumaDown", "Bicubic150"), ("lumaDownLinear", "0"));
        yield return Make("madvr-medium", "Medio", "madvr", ("chromaUp", "NGUStandardMed"), ("superChromaRes", "0"), ("lumaUp", "NGUStandard"),
            ("lumaUpNguLumaDoubleQuality", "low"), ("lumaDown", "Catmull-Rom"), ("lumaDownLinear", "0"));
        yield return Make("madvr-high", "Alto", "madvr", ("chromaUp", "NGUStandardHigh"), ("superChromaRes", "0"), ("lumaUp", "NGUSharp"),
            ("lumaUpNguLumaDoubleQuality", "med"), ("lumaDown", "Jinc3"), ("lumaDownLinear", "0"));
        yield return Make("madvr-ultra", "Ultra", "madvr", ("chromaUp", "NGUStandardVeryHigh"), ("superChromaRes", "1"), ("lumaUp", "NGUSharp"),
            ("lumaUpNguLumaDoubleQuality", "veryHigh"), ("lumaDown", "SSIM2D100"), ("lumaDownLinear", "1"));
        // DVD e sorgenti povere (SD, web a bassa risoluzione): ingrandimento che non esalta i difetti e pulizia degli
        // artefatti tipici (blocchi MPEG-2, aloni, banding, rumore). Su sorgenti SD costa poco anche in risparmio energetico.
        yield return Make("madvr-dvd", "DVD e bassa qualità", "madvr", ("lumaUp", "NGUAntiAlias"), ("lumaUpNguLumaDoubleQuality", "high"),
            ("lumaUpNguDoubleActivate", "1.2x"), ("lumaUpNguLumaQuadQuality", "auto"), ("chromaUp", "NGUAntiAliasHigh"), ("superChromaRes", "1"),
            ("lumaUpSigmoidal", "1"), ("deblockLumaActive", "1"), ("deblockStrength", "2"), ("deringActive", "1"),
            ("debandActive", "1"), ("debandLevel", "1"), ("denoiseLumaActive", "1"), ("denoiseStrength", "1"));
        yield return Make("mpcvr-low", "Basso", "mpcvr", ("Upscaling", "1"), ("Downscaling", "1"), ("ChromaUpsampling", "1"));
        yield return Make("mpcvr-medium", "Medio", "mpcvr", ("Upscaling", "2"), ("Downscaling", "3"), ("ChromaUpsampling", "2"));
        yield return Make("mpcvr-high", "Alto", "mpcvr", ("Upscaling", "4"), ("Downscaling", "5"), ("ChromaUpsampling", "2"));
        yield return Make("mpcvr-ultra", "Ultra", "mpcvr", ("Upscaling", "5"), ("Downscaling", "5"), ("ChromaUpsampling", "2"), ("Dither", "1"));
        yield return Make("mpcvr-dvd", "DVD e bassa qualità", "mpcvr", ("Upscaling", "5"), ("Downscaling", "5"), ("ChromaUpsampling", "2"),
            ("InterpolateAt50pct", "1"), ("Dither", "1"));
        yield return Make("mpv-low", "Basso", "mpv", ("Profile", "fast"), ("Scale", "bilinear"), ("CScale", "bilinear"), ("DScale", "bilinear"),
            ("LinearDownscaling", "0"), ("SigmoidUpscaling", "0"), ("Deband", "0"));
        yield return Make("mpv-medium", "Medio", "mpv", ("Profile", "default"), ("Scale", "spline36"), ("CScale", "bicubic"), ("DScale", "catmull_rom"),
            ("LinearDownscaling", "0"), ("Deband", "0"));
        yield return Make("mpv-high", "Alto", "mpv", ("Profile", "high-quality"), ("Scale", "ewa_lanczos"), ("CScale", "ewa_lanczos"), ("DScale", "mitchell"),
            ("LinearDownscaling", "1"), ("SigmoidUpscaling", "1"));
        yield return Make("mpv-ultra", "Ultra", "mpv", ("Profile", "high-quality"), ("Scale", "ewa_lanczossharp"), ("CScale", "ewa_lanczos"), ("DScale", "mitchell"),
            ("LinearDownscaling", "1"), ("SigmoidUpscaling", "1"), ("Deband", "1"));
        yield return Make("mpv-dvd", "DVD e bassa qualità", "mpv", ("Profile", "high-quality"), ("Scale", "ewa_lanczos"), ("ScaleAntiring", "0.5"),
            ("CScale", "ewa_lanczos"), ("CScaleAntiring", "0.5"), ("SigmoidUpscaling", "1"), ("Deband", "1"));
    }
    private static List<Preset>? _all;

    public static IReadOnlyList<Preset> All
    {
        get
        {
            if (_all != null) return _all;
            try { _all = JsonSerializer.Deserialize<List<Preset>>(MacSettings.Get(StoreKey, "[]")) ?? new(); }
            catch (JsonException) { _all = new(); }
            foreach (var p in _all) { p.Values ??= new(); if (Renderers.All(r => r.Value != p.Renderer)) p.Renderer = "madvr"; }
            // Profili nostri (Basso, Medio, Alto, Ultra per ogni renderer): si aggiungono una volta sola, poi sono preset
            // come gli altri (modificabili, eliminabili); di serie solo dal menu del tasto destro.
            // Versione dei profili nostri: ogni versione nuova aggiunge solo quelli che mancano (mai rimettere quelli
            // che l'utente ha gia' avuto, modificato o eliminato nelle versioni prima).
            int seeded = int.TryParse(MacSettings.Get(SeededKey, "0"), out int v) ? v : 0;
            if (seeded < BuiltInVersion)
            {
                foreach (var (preset, since) in BuiltInWithVersion())
                    if (since > seeded && _all.All(x => x.Id != preset.Id)) _all.Add(preset);
                MacSettings.Set(SeededKey, BuiltInVersion.ToString());
                MacSettings.Set(StoreKey, JsonSerializer.Serialize(_all));
            }
            return _all;
        }
    }

    private const string RulesKey = "renderRules";
    private static List<Rule>? _rules;

    public static IReadOnlyList<Rule> Rules
    {
        get
        {
            if (_rules != null) return _rules;
            try { _rules = JsonSerializer.Deserialize<List<Rule>>(MacSettings.Get(RulesKey, "[]")) ?? new(); }
            catch (JsonException) { _rules = new(); }
            // Preset di prima con condizioni proprie: diventano automazioni (una volta).
            foreach (var p in All.Where(p => p.Automatic).ToList())
            {
                _rules.Add(new Rule { Resolution = p.Resolution, Hdr = p.Hdr, FrameRate = p.FrameRate, Power = p.Power, Screen = p.Screen, PresetId = p.Id });
                p.Automatic = false;
            }
            // Automazioni pronte di una prova del 23/11 (ora la scelta interna e' Automatico, RenderAuto): via.
            if (_rules.RemoveAll(r => r.Id.StartsWith("auto-madvr-", StringComparison.Ordinal)) > 0)
                MacSettings.Set(RulesKey, JsonSerializer.Serialize(_rules));
            return _rules;
        }
    }

    public static Rule AddRule()
    {
        _ = Rules;
        var rule = new Rule { PresetId = All.FirstOrDefault(p => p.Id == "madvr-high")?.Id ?? All.FirstOrDefault()?.Id ?? "" };
        _rules!.Add(rule);
        Save();
        return rule;
    }

    public static void RemoveRule(Rule rule) { _ = Rules; _rules!.Remove(rule); Save(); }

    public static void MoveRule(Rule rule, int delta)
    {
        _ = Rules;
        int i = _rules!.IndexOf(rule), j = Math.Clamp(i + delta, 0, _rules.Count - 1);
        if (i < 0 || i == j) return;
        _rules.RemoveAt(i);
        _rules.Insert(j, rule);
        Save();
    }

    public static Rule? FindRule(string? id) => id == null ? null : Rules.FirstOrDefault(r => r.Id == id);

    public static event Action? Changed;

    public static void Save()
    {
        MacSettings.Set(StoreKey, JsonSerializer.Serialize(_all ?? new List<Preset>()));
        if (_rules != null) MacSettings.Set(RulesKey, JsonSerializer.Serialize(_rules));
        Changed?.Invoke();
    }

    public static Preset Add(string renderer)
    {
        _ = All;
        int n = 1;
        while (_all!.Any(p => p.Name == "Preset " + n)) n++;
        var preset = new Preset { Name = "Preset " + n, Renderer = renderer };
        _all.Add(preset);
        Save();
        return preset;
    }

    public static void Remove(Preset preset)
    {
        _ = All;
        _all!.Remove(preset);
        // Le automazioni che lo usavano restano, senza profilo (spente finche' non se ne sceglie un altro).
        foreach (var r in Rules.Where(r => r.PresetId == preset.Id)) r.PresetId = "";
        Save();
    }

    /// <summary>Sposta il preset nell'elenco (l'ordine decide quale vince quando ne valgono piu' d'uno).</summary>
    public static void Move(Preset preset, int delta)
    {
        _ = All;
        int i = _all!.IndexOf(preset), j = Math.Clamp(i + delta, 0, _all.Count - 1);
        if (i < 0 || i == j) return;
        _all.RemoveAt(i);
        _all.Insert(j, preset);
        Save();
    }

    public static Preset? Find(string? id) => id == null ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>La prima automazione accesa che vale per questo film (e per il suo renderer): il suo profilo.</summary>
    public static Preset? Match(string renderer, Context c)
    {
        foreach (var rule in Rules)
            if (rule.Enabled && Find(rule.PresetId) is { } p && p.Renderer == renderer && Matches(rule, c)) return p;
        return null;
    }

    public static bool HasRules(string renderer) => Rules.Any(r => r.Enabled && Find(r.PresetId)?.Renderer == renderer);

    /// <summary>Qualche automazione ha bisogno dei dati del film (risoluzione, HDR, fotogrammi) per decidere.</summary>
    public static bool NeedsSource(string renderer) =>
        Rules.Any(r => r.Enabled && Find(r.PresetId)?.Renderer == renderer && (r.Resolution != "any" || r.Hdr != "any" || r.FrameRate != "any"));

    public static bool Matches(Rule p, Context c)
    {
        if (p.Resolution != "any")
        {
            if (c.Width <= 0 && c.Height <= 0) return false;
            string res = Resolution(c.Width, c.Height);
            if (p.Resolution == "lt-uhd" ? res == "uhd" : res != p.Resolution) return false;
        }
        if (p.Hdr != "any" && (c.Hdr is not { } hdr || hdr != (p.Hdr == "hdr"))) return false;
        if (p.FrameRate != "any" && (c.Fps <= 0 || (c.Fps > 31) != (p.FrameRate == "high"))) return false;
        switch (p.Power)
        {
            case "lowpower" when !c.LowPower: return false;
            case "normal" when c.LowPower: return false;
            case "battery" when !c.OnBattery: return false;
            case "ac" when c.OnBattery: return false;
        }
        if (p.Screen != "any" && (c.InternalScreen is not { } inside || inside != (p.Screen == "internal"))) return false;
        return true;
    }

    /// <summary>Classe di risoluzione dalla larghezza (i film scope hanno meno righe: 3840x1600 e' 4K).</summary>
    public static string Resolution(int width, int height) =>
        width >= 3000 || height >= 1700 ? "uhd" : width >= 1600 || height >= 900 ? "fhd" : width >= 1100 || height >= 650 ? "hd" : "sd";

    /// <summary>Le condizioni di un'automazione come frase: "Quando il film è 4K e il risparmio energetico è attivo".</summary>
    public static string When(Rule r)
    {
        // Le condizioni del film in una sola frase ("il film è 4K e HDR, su uno schermo esterno"), poi l'alimentazione.
        var film = new List<string>();
        // [2026-11-23] Ogni pezzo si traduce da solo (L10n non traduce una frase montata a pezzi).
        if (r.Resolution != "any") film.Add(r.Resolution == "lt-uhd" ? L10n.T("sotto il 4K") : L10n.T(Label(ResolutionChoices, r.Resolution)));
        if (r.Hdr != "any") film.Add(r.Hdr == "hdr" ? "HDR" : "SDR");
        if (r.FrameRate != "any") film.Add(L10n.T(r.FrameRate == "high" ? "oltre 30 fps" : "fino a 30 fps"));
        if (r.Screen != "any") film.Add(L10n.T(r.Screen == "internal" ? "sullo schermo del Mac" : "su uno schermo esterno"));
        var parts = new List<string>();
        if (film.Count > 0) parts.Add(L10n.T("il film è") + " " + Join(film));
        if (r.Power != "any") parts.Add(L10n.T(r.Power switch
        {
            "lowpower" => "il risparmio energetico è attivo", "normal" => "il risparmio energetico è spento",
            "battery" => "il Mac va a batteria", _ => "l’alimentatore è collegato",
        }));
        return parts.Count == 0 ? L10n.T("Sempre") : L10n.T("Quando") + " " + Join(parts);
    }

    private static string Join(List<string> items) => items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " " + L10n.T("e") + " " + items[^1];

    public static string Then(Rule r) => Find(r.PresetId) is { } p ? $"{RendererLabel(p.Renderer)}: {L10n.T(p.Name)}" : L10n.T("nessun preset scelto");

    /// <summary>Riassunto di un profilo per l'elenco.</summary>
    public static string Summary(Preset p, bool withRenderer = true)
    {
        string changes = p.Values.Count == 0 ? "Nessuna voce cambiata" : p.Values.Count == 1 ? "1 voce cambiata" : p.Values.Count + " voci cambiate";
        int used = Rules.Count(r => r.PresetId == p.Id);
        string rules = used == 0 ? "" : used == 1 ? ", usato da 1 automazione" : $", usato da {used} automazioni";
        changes = L10n.T(changes);
        return (withRenderer ? RendererLabel(p.Renderer) + ": " + changes.ToLowerInvariant() : changes) + L10n.T(rules);
    }
}
