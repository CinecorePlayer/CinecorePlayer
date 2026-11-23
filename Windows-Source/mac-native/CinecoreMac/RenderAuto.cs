using System;
using System.Collections.Generic;
using System.Linq;

namespace CinecoreMac;

/// <summary>
/// "Automatico" del menu Video › Preset: il player compone da solo le voci del renderer per il film che parte, in base
/// al renderer, al Mac (MacHardware), all'alimentazione (risparmio energetico, batteria), allo stato termico e al film
/// (risoluzione rispetto all'area a schermo, fotogrammi al secondo, qualita' della sorgente dal bitrate e dal codec).
///
/// Idee guida:
/// - Un film va prima di tutto ingrandito bene: se va ingrandito, la potenza va all'ingrandimento NGU (la luminanza, dove
///   sta il dettaglio), poi, se avanza, alla crominanza e a SuperChromaRes.
/// - Sorgente povera (DVD, risoluzione bassa, bitrate basso per il codec): NGU Anti-Alias (non esalta blocchi e
///   rumore) + pulizia del banding, dei contorni e, se molto compressa, dei blocchi. Crominanza semplice: nei file
///   poveri il colore non ha dettaglio da recuperare.
/// - Sorgente buona: NGU Sharp.
/// - Film piu' grande dello schermo (4K in una finestra): livelli Basso…Ultra misurati.
///
/// Modello di costo madVR in "ms di GPU al secondo a piena potenza" sul Mac delle misure (MacBook Air M4, GPU 10
/// core, a batteria, finestra 2588x1576 = 4,08 MP, 24 fps, 60 presentazioni/s). Misure del 23/11/2026 (Easy Rider
/// 1080p, Una moglie 696x378):
///   Basso 444 · NGU AA bassa 543 · alta 563-669 · massima 752 (0,1 persi/s) · NGU Sharp media + crominanza NGU alta
///   (Alto) 589 · NGU massima + crominanza NGU massima + SuperChromaRes 858 (0,8 persi/s, 44 presentati/s).
///   Una moglie: Basso 480, NGU AA massima 488: NGU lavora alla risoluzione del film.
/// In risparmio energetico (stesso Mac): Basso 545 regge, Alto (589 a piena potenza) fa 9 persi/s.
/// </summary>
internal static class RenderAuto
{
    internal sealed record Choice(RenderPresets.Preset Preset, string Reason);

    // ---------------------------------------------------------------- qualita' della sorgente

    internal enum SourceQuality { Good, Poor, VeryPoor }

    /// <summary>
    /// Bit per pixel per fotogramma, pesati per l'efficienza del codec (H.264 = 1): un Blu-ray 1080p H.264 sta su
    /// 0,3-0,5, un buon encode 0,2; sotto 0,15 si vedono blocchi e banding (Easy Rider: 0,11).
    /// </summary>
    public static double EffectiveBitsPerPixel(ProbeData? p)
    {
        if (p is not { Width: > 0, Height: > 0 }) return 0;
        double kbps = p.VideoKbps > 0 ? p.VideoKbps : p.OverallKbps > 0 ? p.OverallKbps - Math.Max(0, p.AudioKbps) : 0;
        if (kbps <= 0 && p.SizeBytes > 0 && p.Duration > 0) kbps = p.SizeBytes * 8 / p.Duration / 1000 * 0.9; // ~10% audio
        if (kbps <= 0) return 0;
        double fps = p.Fps > 0 ? p.Fps : 24;
        string codec = p.VideoCodec.ToLowerInvariant();
        double efficiency = codec switch
        {
            "hevc" or "h265" or "av1" => 1.6,
            "vp9" => 1.4,
            "vc1" or "wmv3" => 0.8,
            "mpeg2video" or "mpeg1video" or "mpeg4" or "msmpeg4v3" or "xvid" or "divx" => 0.5,
            _ => 1.0,
        };
        return kbps * 1000 / (p.Width * (double)p.Height * fps) * efficiency;
    }

    public static SourceQuality Quality(ProbeData? p)
    {
        if (p is not { Width: > 0, Height: > 0 }) return SourceQuality.Good;
        double bpp = EffectiveBitsPerPixel(p);
        bool sd = p.Width <= 1024 && p.Height <= 600;
        if ((bpp > 0 && bpp < 0.07) || (sd && bpp > 0 && bpp < 0.12) || p.Height <= 400) return SourceQuality.VeryPoor;
        if (sd || (p.Width <= 1280 && p.Height <= 720) || (bpp > 0 && bpp < 0.15)) return SourceQuality.Poor;
        return SourceQuality.Good;
    }

    // ---------------------------------------------------------------- quanto regge il Mac adesso

    /// <summary>Budget in ms di GPU al secondo, nelle unita' del Mac delle misure a piena potenza.</summary>
    public static double Budget(bool lowPower, bool onBattery, int thermal)
    {
        // Piena potenza: Ultra (858) perdeva fotogrammi, NGU massima (752) no. Risparmio energetico: Basso (444) regge,
        // Alto (589) no; gli stati della GPU sono piu' bassi (memoria: P1/P2), quindi il limite e' ~500.
        double budget = lowPower ? 500 : 780;
        budget *= MacHardware.GpuScore;
        // Mac con ventola e alimentatore: tengono la frequenza piu' a lungo dell'Air delle misure.
        if (!lowPower && !MacHardware.Fanless) budget *= onBattery ? 1.05 : 1.15;
        budget *= thermal switch { 1 => 0.9, 2 => 0.7, 3 => 0.5, _ => 1.0 };
        return budget;
    }

    // ---------------------------------------------------------------- madVR

    private const double BasePerOutputMp = 109;           // presentazione, scaling semplice, dithering (444 / 4,08 MP)
    private static readonly Dictionary<string, double> NguPerSourceMp = new()
        { ["low"] = 48, ["med"] = 62, ["high"] = 82, ["veryHigh"] = 150 };   // per MP del film a 24 fps
    private const double JincPerSourceMp = 12, ChromaNguHighPerSourceMp = 12, ChromaNguVeryHighPerSourceMp = 17;
    private const double SuperChromaResPerOutputMp = 17, DebandPerOutputMp = 5, DeringPerSourceMp = 20, DeblockPerSourceMp = 15;

    private static readonly string[] NguLevels = { "veryHigh", "high", "med", "low" };
    private static string NguLabel(string q) => q switch { "veryHigh" => "massima", "high" => "alta", "med" => "media", _ => "bassa" };

    private static Choice? MadVr(RenderPresets.Context c, ProbeData? probe, double outW, double outH, double budget, out string detail)
    {
        double srcW = c.Width > 0 ? c.Width : 3840, srcH = c.Height > 0 ? c.Height : 2160;
        double ratio = Math.Min(outW / srcW, outH / srcH);
        double outMp = outW * outH / 1e6, srcMp = srcW * srcH / 1e6;
        double fps = c.Fps > 0 ? Math.Clamp(c.Fps / 24.0, 1, 2.6) : 1;
        var quality = Quality(probe);
        double basis = BasePerOutputMp * outMp;
        detail = "";

        // Film piu' grande dello schermo (o uguale): livelli misurati in riduzione.
        if (ratio <= 1.0)
        {
            // Unita' del vecchio modello: 1 = risparmio energetico dell'Air a 2588x1576; piena potenza circa il doppio.
            double[] levelCost = { 0.30, 0.60, 0.90, 1.30 };
            string[] ids = { "madvr-low", "madvr-medium", "madvr-high", "madvr-ultra" };
            double pixels = 0.6 * (srcMp / 8.29) + 0.4 * (outMp / 4.08);
            double capacity = budget / 500.0;
            int pick = 0;
            for (int i = levelCost.Length - 1; i >= 0; i--) if (levelCost[i] * pixels * fps <= capacity) { pick = i; break; }
            var level = RenderPresets.BuiltIn().First(p => p.Id == ids[pick]);
            detail = $"riduzione, costo {levelCost[pick] * pixels * fps * 500:0} su {budget:0}";
            return new Choice(Clone(level, "auto-madvr", level.Name), detail);
        }

        var values = new Dictionary<string, string>
        {
            // Voci pesanti che non scegliamo noi: spente, altrimenti quelle delle Impostazioni si sommerebbero.
            ["superRes"] = "0", ["adaptiveSharpen"] = "0", ["upRefAdaptiveSharpen"] = "0", ["denoiseLumaActive"] = "0",
            ["lumaDown"] = "Bicubic150", ["lumaDownLinear"] = "0", ["lumaUpSigmoidal"] = "1",
            ["chromaUp"] = "Bicubic60", ["superChromaRes"] = "0",
            ["debandActive"] = "0", ["deringActive"] = "0", ["deblockLumaActive"] = "0",
        };
        double cost = basis;
        var parts = new List<string>();
        bool poor = quality != SourceQuality.Good;

        // Pulizia per i file poveri, prima dell'ingrandimento: il banding si vede di piu' quando si ingrandisce.
        if (poor && cost + DebandPerOutputMp * outMp <= budget)
        {
            values["debandActive"] = "1"; values["debandLevel"] = quality == SourceQuality.VeryPoor ? "1" : "0"; // bassa, media se molto compresso
            cost += DebandPerOutputMp * outMp; parts.Add("banding");
        }

        // Ingrandimento: NGU alla qualita' piu' alta che ci sta, altrimenti Jinc, altrimenti Lanczos.
        string flavor = poor ? "NGUAntiAlias" : "NGUSharp";
        string? ngu = NguLevels.FirstOrDefault(q => cost + NguPerSourceMp[q] * srcMp * fps <= budget);
        string upscale;
        if (ratio >= 1.2 && ngu != null)
        {
            values["lumaUp"] = flavor; values["lumaUpNguLumaDoubleQuality"] = ngu;
            values["lumaUpNguDoubleActivate"] = "auto"; values["lumaUpNguLumaQuadQuality"] = "auto";
            cost += NguPerSourceMp[ngu] * srcMp * fps;
            upscale = (poor ? "NGU Anti-Alias " : "NGU Sharp ") + L10n.T(NguLabel(ngu));
        }
        else if (cost + JincPerSourceMp * srcMp * fps <= budget)
        {
            values["lumaUp"] = "Jinc3"; cost += JincPerSourceMp * srcMp * fps; upscale = "Jinc";
        }
        else { values["lumaUp"] = "Lanczos3"; upscale = "Lanczos"; }

        if (poor)
        {
            // Contorni (aloni da nitidezza della sorgente) e blocchi: lavorano alla risoluzione del film, costano poco
            // proprio sui file piccoli che ne hanno bisogno.
            if (cost + DeringPerSourceMp * srcMp * fps <= budget) { values["deringActive"] = "1"; cost += DeringPerSourceMp * srcMp * fps; parts.Add("aloni"); }
            if (quality == SourceQuality.VeryPoor && cost + DeblockPerSourceMp * srcMp * fps <= budget)
            {
                values["deblockLumaActive"] = "1"; values["deblockStrength"] = "2"; cost += DeblockPerSourceMp * srcMp * fps; parts.Add("blocchi");
            }
        }
        else
        {
            // Sorgente buona e potenza avanzata: crominanza NGU, poi SuperChromaRes.
            if (cost + ChromaNguVeryHighPerSourceMp * srcMp * fps + SuperChromaResPerOutputMp * outMp <= budget)
            {
                values["chromaUp"] = "NGUStandardVeryHigh"; values["superChromaRes"] = "1";
                cost += ChromaNguVeryHighPerSourceMp * srcMp * fps + SuperChromaResPerOutputMp * outMp; parts.Add("colore massimo");
            }
            else if (cost + ChromaNguHighPerSourceMp * srcMp * fps <= budget)
            {
                values["chromaUp"] = "NGUStandardHigh"; cost += ChromaNguHighPerSourceMp * srcMp * fps; parts.Add("colore NGU");
            }
        }

        // Nome tradotto pezzo per pezzo (compare nel menu e nell'avviso a schermo).
        string name = upscale + (parts.Count > 0 ? " + " + string.Join(", ", parts.Select(L10n.T)) : "");
        detail = $"ingrandimento x{ratio:0.00}, sorgente {QualityLabel(quality)} ({EffectiveBitsPerPixel(probe):0.00} bpp), costo {cost:0} su {budget:0}";
        return new Choice(new RenderPresets.Preset { Id = "auto-madvr", Name = name, Renderer = "madvr", Automatic = false, Values = values }, detail);
    }

    private static string QualityLabel(SourceQuality q) => q switch { SourceQuality.VeryPoor => "molto povera", SourceQuality.Poor => "povera", _ => "buona" };

    private static RenderPresets.Preset Clone(RenderPresets.Preset p, string id, string name) =>
        new() { Id = id, Name = name, Renderer = p.Renderer, Automatic = false, Values = new Dictionary<string, string>(p.Values) };

    // ---------------------------------------------------------------- MPC-VR e MPV

    /// <summary>
    /// Costano un ordine di grandezza meno di madVR (MPC-VR 4K in risparmio energetico: GPU 14%; MPV nativo ~20% CPU):
    /// sorgenti povere -> il preset "DVD e bassa qualita'" (antiringing, deband), buone -> Ultra; Alto solo quando il
    /// Mac e' davvero stretto (caldo serio o risparmio energetico con film a 50/60 fps su schermi grandi).
    /// </summary>
    private static Choice? Light(string renderer, RenderPresets.Context c, ProbeData? probe, double outW, double outH, double budget)
    {
        var quality = Quality(probe);
        double fps = c.Fps > 0 ? Math.Clamp(c.Fps / 24.0, 1, 2.6) : 1;
        double load = outW * outH / 1e6 * fps * (renderer == "mpv" ? 25 : 35); // ms/s stimati per Ultra
        string level = quality != SourceQuality.Good ? "dvd" : load <= budget * 0.5 ? "ultra" : "high";
        var preset = RenderPresets.BuiltIn().FirstOrDefault(p => p.Id == renderer + "-" + level);
        if (preset == null) return null;
        string name = level == "dvd" ? "Pulizia bassa qualità" : preset.Name;
        return new Choice(Clone(preset, "auto-" + renderer, name), $"sorgente {QualityLabel(quality)}, carico stimato {load:0} su {budget:0}");
    }

    // ---------------------------------------------------------------- ingresso

    public static Choice? Choose(string renderer, RenderPresets.Context c, ProbeData? probe, double outWidth, double outHeight)
    {
        double outW = Math.Max(640, outWidth), outH = Math.Max(360, outHeight);
        int thermal = MacPower.ThermalState;
        double budget = Budget(c.LowPower, c.OnBattery, thermal);
        string detail = "";
        var choice = renderer == "madvr" ? MadVr(c, probe, outW, outH, budget, out detail) : Light(renderer, c, probe, outW, outH, budget);
        if (choice == null) return null;
        string power = c.LowPower ? "risparmio energetico" : c.OnBattery ? "batteria" : "alimentatore";
        string thermalText = thermal switch { 1 => ", caldo", 2 => ", molto caldo", 3 => ", critico", _ => "" };
        string reason = $"{(c.Width > 0 ? $"{c.Width}x{c.Height}" : "film non letto")} {(c.Fps > 0 ? $"{c.Fps:0.###} fps" : "")} -> {outW:0}x{outH:0}, " +
                        $"{power}{thermalText}, {MacHardware.Describe()}; {(detail.Length > 0 ? detail : choice.Reason)}";
        return choice with { Reason = reason };
    }
}
