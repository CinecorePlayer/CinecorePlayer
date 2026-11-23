using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CinecoreMac;

/// <summary>
/// Le pagine essenziali dei componenti (SettingsHudPage.NativeProviders.cs e .MadVrPage.cs): stesse voci, stessi
/// valori del registro e di IMadVRSettings. Sul Mac i filtri vivono nel registro di Wine: le scelte si salvano nelle
/// preferenze dell'app e il VideoHost le scrive nel registro (o le passa a madVR) prima di aprire il video.
/// Come nella versione Linux si tolgono le voci che sotto Wine non fanno nulla (decodifica hardware DXVA/D3D11,
/// bitstream, video processor D3D11 di MPC-VR, cambio frequenza e D3D11 di madVR, che gestisce il player).
/// </summary>
internal static class ComponentSettings
{
    // Funzioni dei driver NVIDIA RTX con MPC-VR (telecomando di Windows): sul Mac non ci sono, restano spente.
    public static bool RtxVideoHdr { get; set; }
    public static int RtxSuperResolution { get; set; }

    public enum Kind { Toggle, Choice, IntChoice }
    public sealed record Choice(string Value, string Label);
    public sealed record Setting(string Key, string Label, Kind Kind, string Default, string Description = "", Choice[]? Choices = null);
    public sealed record Section(string Title, Setting[] Settings, string Group = "");

    public const string HdrProfileKey = "hdr:profile";
    public static readonly string[] Tabs = { "madVR", "LAV Video", "LAV Audio", "MPC Video Renderer", "Sottotitoli", "MPV" };

    private static Choice C(string value, string label) => new(value, label);
    private static Choice[] Numbers(params int[] values) => values.Select(v => C(v.ToString(CultureInfo.InvariantCulture), v.ToString(CultureInfo.InvariantCulture))).ToArray();
    private static Setting T(string key, string label, string def, string desc = "") => new(key, label, Kind.Toggle, def, desc);
    private static Setting P(string key, string label, string def, string desc, params Choice[] choices) => new(key, label, Kind.Choice, def, desc, choices);
    private static Setting N(string key, string label, string def, string desc, params Choice[] choices) => new(key, label, Kind.IntChoice, def, desc, choices);

    public static string Status(string tab) => tab switch
    {
        "madVR" => "Le modifiche si applicano subito al video in riproduzione con madVR.",
        "MPV" => "Le modifiche valgono dal prossimo video aperto con MPV.",
        _ => "Le modifiche valgono dal prossimo video aperto.",
    };

    public static Section[] Sections(string tab) => tab switch
    {
        "madVR" => MadVr(),
        "LAV Video" => new[]
        {
            new Section("Decodifica", new[]
            {
                N("NumThreads", "Thread software", "0", "", C("0", "Automatico"), C("2", "2"), C("4", "4"), C("8", "8"), C("16", "16")),
            }),
            new Section("Immagine", new[]
            {
                N("DeintMode", "Deinterlacciamento", "0", "", C("0", "Automatico"), C("1", "Aggressivo"), C("2", "Sempre"), C("3", "Disattivato")),
                N("RGBRange", "Gamma RGB in uscita", "0", "", C("0", "Come l’origine"), C("1", "TV (16-235)"), C("2", "PC (0-255)")),
                N("DitherMode", "Dithering", "1", "", C("0", "Ordinato"), C("1", "Casuale")),
            }),
        },
        "LAV Audio" => new[]
        {
            new Section("Elaborazione", new[]
            {
                T("DRCEnabled", "Compressione dinamica", "0", "Dialoghi più udibili a basso volume."),
                N("DRCLevel", "Intensità compressione", "100", "", Numbers(25, 50, 75, 100)),
                T("Mixing", "Downmix", "0", "Adatta le tracce multicanale all’impianto."),
                N("MixingLayout", "Canali in uscita", "3", "", C("3", "Stereo"), C("1551", "5.1"), C("1599", "7.1")),
                T("AutoAVSync", "Correzione automatica A/V", "1"),
            }),
        },
        "MPC Video Renderer" => new[]
        {
            new Section("Scaling", new[]
            {
                N("Upscaling", "Upscaling", "4", "", C("0", "Nearest neighbor"), C("1", "Mitchell-Netravali"), C("2", "Catmull-Rom"), C("3", "Lanczos 2"), C("4", "Lanczos 3"), C("5", "Jinc 2")),
                N("Downscaling", "Downscaling", "5", "", C("0", "Box"), C("1", "Bilinear"), C("2", "Hamming"), C("3", "Bicubic"), C("4", "Bicubic sharp"), C("5", "Lanczos")),
                N("ChromaUpsampling", "Upscaling crominanza", "2", "", C("0", "Nearest neighbor"), C("1", "Bilinear"), C("2", "Catmull-Rom")),
                T("InterpolateAt50pct", "Interpola al 50%", "1"),
                T("Dither", "Dithering", "1"),
            }, "Immagine"),
            new Section("Video processor", new[]
            {
                N("TextureFormat", "Formato texture", "0", "", C("0", "Automatico (8/10 bit)"), C("8", "8 bit"), C("10", "10 bit"), C("16", "16 bit float")),
            }, "Immagine"),
            new Section("HDR", new[]
            {
                T("ConvertToSdr", "Converti HDR in SDR", "1", "Quando lo schermo non mostra l’HDR."),
                N("DisplayNits", "Luminanza display SDR", "125", "", C("80", "80 nit"), C("100", "100 nit"), C("125", "125 nit"), C("160", "160 nit"), C("200", "200 nit"), C("250", "250 nit"), C("300", "300 nit")),
                T("HdrPreferDoVi", "Preferisci Dolby Vision", "0"),
            }, "HDR"),
            new Section("Presentazione", new[]
            {
                T("VBlankBeforePresent", "Attendi il VBlank", "0"),
                T("AdjustPresentationTime", "Correggi i tempi di presentazione", "0"),
                T("ReinitWhenChangingDisplay", "Reinizializza al cambio schermo", "0"),
                T("ShowStatistics", "Statistiche a schermo", "0", "Il riquadro di testo in alto a sinistra sul video."),
            }, "Presentazione"),
        },
        "Sottotitoli" => new[]
        {
            new Section("Caricamento", new[]
            {
                T(@"General\ext_load", "File esterni", "1", ".srt, .ass e simili accanto al video."),
                T(@"General\emb_load", "Tracce incorporate", "1"),
                T(@"General\hide", "Nascondi sottotitoli", "0"),
            }),
            new Section("Testo", new[]
            {
                P("style:font", "Carattere", "Arial", "Vale per i sottotitoli senza stile proprio (SRT).",
                    C("Arial", "Arial"), C("Segoe UI", "Segoe UI"), C("Calibri", "Calibri"), C("Verdana", "Verdana"), C("Tahoma", "Tahoma"), C("Trebuchet MS", "Trebuchet MS")),
                N("style:size", "Dimensione", "18", "", Numbers(14, 16, 18, 20, 22, 24, 28, 32)),
                T("style:bold", "Grassetto", "1"),
                P("style:color", "Colore", "ffffff", "", C("ffffff", "Bianco"), C("f5f0dc", "Avorio"), C("ffff00", "Giallo"), C("d0d0d0", "Grigio chiaro")),
                N("style:outline", "Bordo", "2", "", C("0", "Nessuno"), C("1", "Sottile"), C("2", "Normale"), C("3", "Spesso"), C("4", "Molto spesso")),
                N("style:shadow", "Ombra", "3", "", C("0", "Nessuna"), C("1", "Leggera"), C("2", "Media"), C("3", "Marcata")),
                N("style:margin", "Distanza dal bordo", "20", "", Numbers(10, 20, 30, 40, 60)),
            }),
            new Section("Posizione", new[]
            {
                T(@"Text\override_placement", "Posizione personalizzata", "0", "Sposta anche i sottotitoli con posizione propria."),
                N(@"Text\y_perc", "Altezza sullo schermo", "90", "", C("80", "80%"), C("85", "85%"), C("90", "90%"), C("95", "95%"), C("98", "98%")),
            }),
        },
        "MPV" => Mpv(),
        _ => Array.Empty<Section>(),
    };

    // ------------------------------------------------------------------ MPV (SettingsHudPage.DrawMpvSectioned)
    // Stesse sezioni e voci di Windows (MpvPlaybackSettings). Scelte adattate al Mac: VideoToolbox al posto di D3D11VA,
    // Vulkan/MoltenVK al posto di D3D11. Predefiniti = quelli già usati dal motore mpv del Mac.
    private static Choice[] Same(params string[] values) => values.Select(v => C(v, v)).ToArray();
    private static Section[] Mpv()
    {
        Section G(string group, string title, params Setting[] s) => new(title, s, group);
        Setting O(string key, string label, string def, params string[] values) => P(key, label, def, "", Same(values));
        var vo = new[]
        {
            O("VideoOutput", "VO", "gpu-next", "gpu-next", "gpu"),
            O("GpuApi", "GPU API", "auto", "auto", "vulkan"),
            O("GpuContext", "GPU context", "auto", "auto", "macvk"),
            O("Hwdec", "HW decode", "videotoolbox", "videotoolbox", "videotoolbox-copy", "auto-safe", "auto", "no"),
            T("VideoLavcDr", "Video DR", "1"),
        };
        var scalers = new[]
        {
            O("Scale", "Scale", "ewa_lanczossharp", "ewa_lanczossharp", "ewa_lanczos", "spline36", "lanczos", "bicubic", "bilinear"),
            O("ScaleAntiring", "Scale antiring", "0", "0", "0.3", "0.5", "0.7", "1"),
            O("CScale", "CScale", "ewa_lanczos", "ewa_lanczos", "ewa_lanczossoft", "spline36", "bicubic", "bilinear"),
            O("CScaleAntiring", "CScale antiring", "0", "0", "0.3", "0.5", "0.7", "1"),
            O("DScale", "DScale", "mitchell", "mitchell", "catmull_rom", "lanczos", "bicubic", "bilinear"),
            O("DScaleAntiring", "DScale antiring", "0", "0", "0.3", "0.5", "0.7", "1"),
            O("TScale", "TScale", "oversample", "oversample", "linear", "catmull_rom", "mitchell"),
            T("CorrectDownscaling", "Correct downscaling", "1"),
            T("LinearDownscaling", "Linear downscaling", "1"),
            T("SigmoidUpscaling", "Sigmoid upscaling", "1"),
        };
        return new[]
        {
            G("Essenziali", "Motore e profilo", O("Profile", "Profilo", "default", "default", "high-quality", "fast", "low-latency")),
            G("Essenziali", "Output video", vo),
            G("Essenziali", "Cache e thread",
                O("Cache", "Cache", "yes", "auto", "yes", "no"),
                O("DemuxerReadaheadSeconds", "Readahead", "30", "0", "10", "30", "60", "120", "300"),
                O("DemuxerMaxBytesMb", "Cache MB", "400", "0", "64", "128", "256", "400", "512", "1024"),
                O("VideoThreads", "Threads", "0", "0", "2", "4", "8", "16")),
            G("Video", "Output video", vo.Append(T("Deinterlace", "Deinterlace", "0")).ToArray()),
            G("Video", "Sincronizzazione",
                O("VideoSync", "Sync", "audio", "audio", "display-resample", "display-vdrop", "display-adrop", "desync"),
                T("Interpolation", "Interpolation", "0"),
                O("InterpolationThreshold", "Interp. threshold", "0", "0", "0.001", "0.01", "0.05", "0.1")),
            G("Video", "Scaler", scalers),
            G("HDR", "Tone mapping HDR",
                O("ToneMapping", "Tone mapping", "auto", "auto", "bt.2446a", "mobius", "hable", "reinhard", "clip"),
                O("ToneMappingParam", "Tone map param", "0", "0", "0.25", "0.5", "0.75", "1", "1.5", "2"),
                O("TargetPeak", "Target peak", "0", "0", "100", "203", "400", "600", "1000"),
                O("HdrContrastRecovery", "HDR recovery", "0", "0", "0.3", "0.5", "1", "2", "4"),
                O("HdrContrastSmoothness", "HDR smoothness", "0", "0", "25", "50", "75", "100"),
                T("HdrComputePeak", "HDR compute peak", "1")),
            G("HDR", "Colore",
                O("TargetPrim", "Target prim", "auto", "auto", "bt.709", "bt.2020", "display-p3"),
                O("TargetTrc", "Target TRC", "auto", "auto", "srgb", "gamma2.2", "pq", "hlg"),
                O("GamutMappingMode", "Gamut map", "auto", "auto", "clip", "perceptual", "relative", "saturation"),
                O("VideoOutputLevels", "Levels", "auto", "auto", "limited", "full"),
                O("FboFormat", "FBO format", "auto", "auto", "rgba16f", "rgba32f", "rgb10_a2"),
                T("IccProfileAuto", "ICC profile auto", "0"),
                T("TargetColorspaceHint", "Colorspace hint", "1", "Serve all'HDR in EDR sullo schermo del Mac.")),
            G("Qualità", "Deband e dither",
                T("Deband", "Deband", "0"),
                O("DebandIterations", "Deband iter", "1", "0", "1", "2", "3", "4"),
                O("DebandThreshold", "Deband threshold", "48", "0", "32", "48", "64", "96", "128"),
                O("DebandRange", "Deband range", "16", "0", "16", "24", "32", "48"),
                O("DebandGrain", "Deband grain", "0", "0", "4", "8", "12", "16"),
                O("Dither", "Dither", "fruit", "fruit", "ordered", "error-diffusion", "no"),
                O("DitherDepth", "Dither depth", "auto", "auto", "8", "10", "no"),
                O("DitherSizeFruit", "Fruit size", "6", "2", "4", "6", "8"),
                O("ErrorDiffusion", "Error diffusion", "sierra-lite", "sierra-lite", "floyd-steinberg", "simple"),
                T("TemporalDither", "Temporal dither", "1")),
            G("Qualità", "Ridimensionamento fine", scalers.Where(x => !x.Key.EndsWith("Antiring", StringComparison.Ordinal)).ToArray()),
            G("Audio/Sub", "Audio MPV",
                T("AudioExclusive", "Exclusive mode", "0", "Accesso esclusivo al dispositivo CoreAudio."),
                O("AudioChannels", "Channels", "auto", "auto", "stereo", "5.1", "7.1"),
                T("AudioNormalizeDownmix", "Normalize downmix", "0"),
                O("VolumeMax", "Volume max", "100", "100", "120", "150", "200"),
                O("GaplessAudio", "Gapless audio", "weak", "weak", "yes", "no")),
            G("Audio/Sub", "Sottotitoli",
                O("SubAuto", "Sub auto", "fuzzy", "no", "exact", "fuzzy", "all"),
                O("SubAssOverride", "ASS override", "no", "no", "yes", "force", "strip"),
                O("BlendSubtitles", "Sub blend", "no", "auto", "yes", "no", "video"),
                O("SubScale", "Sub scale", "1", "0.75", "0.9", "1", "1.15", "1.3", "1.5", "2"),
                O("SubFontSize", "Font size", "55", "42", "48", "55", "62", "70", "84"),
                O("SubBorderSize", "Border", "3", "0", "1.5", "2", "3", "4", "5"),
                O("SubShadowOffset", "Shadow", "0", "0", "1", "2", "3", "4")),
        };
    }

    /// <summary>Opzioni di libmpv dalla scheda MPV (Windows: LibMpvPlaybackEngine.ApplyOptions).</summary>
    /// <summary>
    /// Il decoder di mpv scelto in Componenti, pronto per l'opzione hwdec. [2026-11-22] Su Windows "nvdec" (CUDA) funziona
    /// solo con le uscite OpenGL e Vulkan: con D3D11, quella predefinita, mpv scriveva "CUDA hwdec only works with OpenGL
    /// or Vulkan backends" e decodificava con la CPU. Si aggiunge nvdec-copy come ripiego.
    /// </summary>
    public static string MpvHwdec(string? value = null)
    {
        value ??= Get("MPV", Mpv().SelectMany(x => x.Settings).First(x => x.Key == "Hwdec"));
#if CINECORE_WINDOWS
        if (value == "nvdec") return "nvdec,nvdec-copy";
#endif
        return value;
    }

    public static IEnumerable<(string Name, string Value)> MpvOptions()
    {
        var seen = new HashSet<string>();
        string vo = "gpu-next";
        foreach (var setting in Mpv().SelectMany(x => x.Settings))
        {
            if (!seen.Add(setting.Key)) continue;
            string v = Get("MPV", setting);
            string yn = v == "1" ? "yes" : "no";
            switch (setting.Key)
            {
                case "Profile": if (v != "default") yield return ("profile", v); break;
                case "VideoOutput": vo = v; yield return ("vo", v); break;
                case "GpuApi": yield return ("gpu-api", v); break;
                case "GpuContext": yield return ("gpu-context", v); break;
                case "Hwdec": yield return ("hwdec", MpvHwdec(v)); break;
                case "VideoLavcDr": yield return ("vd-lavc-dr", yn); break;
                case "Deinterlace": yield return ("deinterlace", yn); break;
                case "Cache": yield return ("cache", v); break;
                case "DemuxerReadaheadSeconds": if (v != "0") yield return ("demuxer-readahead-secs", v); break;
                case "DemuxerMaxBytesMb": if (v != "0") yield return ("demuxer-max-bytes", v + "MiB"); break;
                case "VideoThreads": if (v != "0") yield return ("vd-lavc-threads", v); break;
                case "VideoSync": yield return ("video-sync", v); break;
                case "Interpolation": yield return ("interpolation", yn); break;
                case "InterpolationThreshold": yield return ("interpolation-threshold", v); break;
                case "Scale": yield return ("scale", v); break;
                // gpu-next (libplacebo) non ha ewa_lanczossoft e lo scarta: vale solo per il VO gpu.
                case "CScale": yield return ("cscale", v == "ewa_lanczossoft" && vo == "gpu-next" ? "ewa_lanczos" : v); break;
                case "DScale": yield return ("dscale", v); break;
                case "TScale": yield return ("tscale", v); break;
                case "ScaleAntiring": yield return ("scale-antiring", v); break;
                case "CScaleAntiring": yield return ("cscale-antiring", v); break;
                case "DScaleAntiring": yield return ("dscale-antiring", v); break;
                case "CorrectDownscaling": yield return ("correct-downscaling", yn); break;
                case "LinearDownscaling": yield return ("linear-downscaling", yn); break;
                case "SigmoidUpscaling": yield return ("sigmoid-upscaling", yn); break;
                case "ToneMapping": yield return ("tone-mapping", v); break;
                case "ToneMappingParam": if (v != "0") yield return ("tone-mapping-param", v); break;
                case "TargetPeak": yield return ("target-peak", v == "0" ? "auto" : v); break;
                case "HdrContrastRecovery": yield return ("hdr-contrast-recovery", v); break;
                case "HdrContrastSmoothness": if (v != "0") yield return ("hdr-contrast-smoothness", v); break;
                case "HdrComputePeak": yield return ("hdr-compute-peak", yn); break;
                case "TargetPrim": yield return ("target-prim", v); break;
                case "TargetTrc": yield return ("target-trc", v); break;
                case "GamutMappingMode": yield return ("gamut-mapping-mode", v); break;
                case "VideoOutputLevels": yield return ("video-output-levels", v); break;
                case "FboFormat": yield return ("fbo-format", v); break;
                case "IccProfileAuto": yield return ("icc-profile-auto", yn); break;
                case "TargetColorspaceHint": yield return ("target-colorspace-hint", yn); break;
                case "Deband": yield return ("deband", yn); break;
                case "DebandIterations": yield return ("deband-iterations", v); break;
                case "DebandThreshold": yield return ("deband-threshold", v); break;
                case "DebandRange": yield return ("deband-range", v); break;
                case "DebandGrain": yield return ("deband-grain", v); break;
                case "Dither": yield return ("dither", v); break;
                case "DitherDepth": yield return ("dither-depth", v); break;
                case "DitherSizeFruit": yield return ("dither-size-fruit", v); break;
                case "ErrorDiffusion": yield return ("error-diffusion", v); break;
                case "TemporalDither": yield return ("temporal-dither", yn); break;
                case "AudioExclusive": yield return ("audio-exclusive", yn); break;
                case "AudioChannels": yield return ("audio-channels", v); break;
                case "AudioNormalizeDownmix": yield return ("audio-normalize-downmix", yn); break;
                case "VolumeMax": yield return ("volume-max", v); break;
                case "GaplessAudio": yield return ("gapless-audio", v); break;
                case "SubAuto": yield return ("sub-auto", v); break;
                case "SubAssOverride": yield return ("sub-ass-override", v); break;
                case "BlendSubtitles": yield return ("blend-subtitles", v); break;
                case "SubScale": yield return ("sub-scale", v); break;
                case "SubFontSize": yield return ("sub-font-size", v); break;
                case "SubBorderSize": yield return ("sub-border-size", v); break;
                case "SubShadowOffset": yield return ("sub-shadow-offset", v); break;
            }
        }
    }

    private static Section[] MadVr()
    {
        Section G(string group, string title, params Setting[] s) => new(title, s, group);
        Choice[] Offsets() => new[] { C("-20", "−20"), C("-10", "−10"), C("-5", "−5"), C("0", "0"), C("5", "+5"), C("10", "+10"), C("20", "+20") };
        return new[]
        {
            G("Immagine", "Crominanza",
                P("chromaUp", "Upscaling crominanza", "Bicubic60", "NGU è il più nitido ma anche il più pesante per la GPU.",
                    C("Bicubic60", "Bicubic 60"), C("Lanczos3", "Lanczos 3"), C("Jinc3", "Jinc 3"), C("ReconSharp", "Reconstruction sharp"),
                    C("NGUAntiAliasLow", "NGU Anti-Alias · basso"), C("NGUAntiAliasMed", "NGU Anti-Alias · medio"),
                    C("NGUAntiAliasHigh", "NGU Anti-Alias · alto"), C("NGUAntiAliasVeryHigh", "NGU Anti-Alias · massimo"),
                    C("NGUStandardMed", "NGU Standard · medio"), C("NGUStandardHigh", "NGU Standard · alto"), C("NGUStandardVeryHigh", "NGU Standard · massimo")),
                T("superChromaRes", "SuperChromaRes", "0", "Rende più netti i bordi di colore.")),
            G("Immagine", "Upscaling",
                P("lumaUp", "Algoritmo", "Lanczos3", "Usato quando il video è più piccolo dello schermo. NGU raddoppia la risoluzione.",
                    C("Bicubic60", "Bicubic 60"), C("Lanczos3", "Lanczos 3"), C("Lanczos4", "Lanczos 4"), C("Spline36", "Spline 36"), C("Jinc3", "Jinc 3"),
                    C("NGUAntiAlias", "NGU Anti-Alias"), C("NGUSoft", "NGU Soft"), C("NGUStandard", "NGU Standard"), C("NGUSharp", "NGU Sharp")),
                P("lumaUpNguLumaDoubleQuality", "Qualità NGU", "med", "", C("low", "Bassa"), C("med", "Media"), C("high", "Alta"), C("veryHigh", "Massima")),
                P("lumaUpNguDoubleActivate", "Raddoppia da", "auto", "Fattore di ingrandimento oltre il quale entra in gioco NGU.",
                    C("auto", "Automatico"), C("1.2x", "1,2×"), C("1.5x", "1,5×"), C("2.0x", "2,0×"), C("always", "Sempre")),
                P("lumaUpNguLumaQuadQuality", "Quadruplicazione", "auto", "",
                    C("auto", "Automatica"), C("disabled", "Disattivata"), C("2xmed", "2× media"), C("2xhigh", "2× alta"), C("4xhigh", "4× alta")),
                P("lumaUpNguChromaQuality", "Crominanza nel raddoppio", "auto", "", C("auto", "Automatica"), C("normal", "Normale"), C("high", "Alta"), C("veryHigh", "Massima")),
                T("lumaUpSigmoidal", "Upscaling sigmoidale", "1", "Riduce gli aloni attorno ai bordi.")),
            G("Immagine", "Downscaling",
                P("lumaDown", "Algoritmo", "Catmull-Rom", "Usato quando il video è più grande dello schermo (es. 4K su 1080p).",
                    C("Catmull-Rom", "Catmull-Rom"), C("Bicubic150", "Bicubic 150"), C("Lanczos3", "Lanczos 3"), C("Jinc3", "Jinc 3"), C("SSIM1D100", "SSIM 1D"), C("SSIM2D100", "SSIM 2D")),
                T("lumaDownLinear", "Scaling in luce lineare", "0")),
            G("Miglioramenti", "Dopo l’upscaling",
                T("superRes", "SuperRes", "0", "Recupera nitidezza dopo l’upscaling."),
                N("superResStrength", "Intensità SuperRes", "2", "", C("1", "1"), C("2", "2"), C("3", "3"), C("4", "4")),
                T("upRefAdaptiveSharpen", "Nitidezza adattiva", "0"),
                T("upRefLumaSharpen", "LumaSharpen", "0"),
                T("upRefCrispenEdges", "Bordi più netti", "0"),
                T("upRefThinEdges", "Assottiglia i bordi", "0"),
                T("upRefEnhanceDetail", "Migliora i dettagli", "0"),
                T("upRefAddGrain", "Aggiungi grana", "0")),
            G("Miglioramenti", "Prima dello scaling",
                T("adaptiveSharpen", "Nitidezza adattiva", "0"),
                T("lumaSharpen", "LumaSharpen", "0"),
                T("crispenEdges", "Bordi più netti", "0"),
                T("sharpenEdges", "Nitidezza bordi", "0"),
                T("enhanceDetail", "Migliora i dettagli", "0")),
            G("Artefatti", "Banding",
                T("debandActive", "Deband", "0", "Riduce le bande nei gradienti (cielo, ombre)."),
                N("debandLevel", "Intensità", "0", "", C("0", "Bassa"), C("1", "Media"), C("2", "Alta")),
                N("debandFadeLevel", "Durante le dissolvenze", "2", "", C("0", "Bassa"), C("1", "Media"), C("2", "Alta"))),
            G("Artefatti", "Compressione e rumore",
                T("deringActive", "Rimuovi aloni (dering)", "0"),
                T("deblockLumaActive", "Deblocking", "0", "Attenua i blocchi dei file molto compressi."),
                N("deblockStrength", "Intensità deblocking", "1", "", C("1", "Bassa"), C("2", "Media"), C("3", "Alta")),
                T("denoiseLumaActive", "Riduzione rumore", "0"),
                N("denoiseStrength", "Intensità riduzione rumore", "1", "", C("1", "1"), C("2", "2"), C("3", "3"), C("4", "4"))),
            // Il Mac mostra l'HDR vero (EDR): passthrough e tone mapping con uscita HDR funzionano; la 3DLUT no (serve un file).
            G("HDR", "Contenuti HDR",
                P(HdrProfileKey, "Gestione HDR", "auto", "Passthrough: l’HDR arriva allo schermo del Mac (EDR).",
                    C("auto", "Automatico (decide madVR)"), C("passthrough", "Passthrough al display"),
                    C("tonemap", "Tone mapping → SDR"), C("tonemapHdr", "Tone mapping con uscita HDR"), C("lut", "HDR → SDR con 3DLUT")),
                N("hdrNits", "Luminanza di picco del display", "200", "Nit usati dal tone mapping: più bassi = immagine più luminosa.",
                    Numbers(100, 120, 140, 160, 200, 250, 300, 400, 600, 1000)),
                N("hdrHighlightRecovery", "Recupero alte luci", "0", "", C("0", "Disattivato"), C("1", "Basso"), C("2", "Medio"), C("3", "Alto"), C("4", "Massimo")),
                T("hdrMeasureFrameNits", "Misura ogni fotogramma", "0", "Tone mapping dinamico, più pesante per la GPU."),
                T("hdrLimitGamutBool", "Limita il gamut", "1"),
                P("hdrLimitGamutStr", "Gamut massimo", "DCI-P3", "", C("BT.709", "BT.709"), C("DCI-P3", "DCI-P3"), C("BT.2020", "BT.2020")),
                T("sendHdrMetadata", "Invia metadati HDR", "1")),
            G("Colore", "Display",
                P("levels", "Livelli del display", "PC Levels", "PC (0-255) per monitor, TV (16-235) per molti TV.", C("PC Levels", "PC (0-255)"), C("TV Levels", "TV (16-235)")),
                N("displayBitdepth", "Profondità colore", "0", "", C("0", "Automatica"), C("8", "8 bit"), C("10", "10 bit")),
                P("displayPrimaries", "Primarie del display", "BT.709 (HD)", "", C("BT.709 (HD)", "BT.709 (HD)"), C("DCI-P3", "DCI-P3"), C("BT.2020 (UHD)", "BT.2020 (UHD)"))),
            G("Colore", "Regolazioni",
                N("brightness", "Luminosità", "0", "", Offsets()),
                N("contrast", "Contrasto", "0", "", Offsets()),
                N("saturation", "Saturazione", "0", "", Offsets())),
            G("Colore", "Dithering",
                P("ditheringAlgo", "Algoritmo", "errorDifMedNoise", "",
                    C("ordered", "Ordinato"), C("random", "Casuale"), C("errorDifLowNoise", "Diffusione errore · basso rumore"), C("errorDifMedNoise", "Diffusione errore · medio")),
                T("coloredDither", "Rumore colorato", "1"),
                T("dynamicDither", "Cambia a ogni fotogramma", "1")),
            G("Schermo e 3D", "3D",
                P("3dFormat", "Formato 3D in uscita", "auto", "Per i Blu-ray 3D (MVC). Per i file SBS/TAB usa il menu 3D del player.",
                    C("auto", "Automatico"), C("none", "Disattivato (2D)"), C("side-by-side", "Side-by-side"), C("top-and-bottom", "Top-and-bottom"),
                    C("line alternative", "Righe alternate"), C("column alternative", "Colonne alternate")),
                T("swapEyes", "Inverti occhi", "0")),
            G("Schermo e 3D", "Bande nere",
                T("detectBars", "Rileva bande nere", "1"),
                T("cropBars", "Ritaglia bande nere", "0"),
                T("moveSubs", "Sottotitoli nelle bande", "1")),
            G("Movimento", "Fluidità",
                T("smoothMotionEnabled", "Smooth motion", "0", "Fonde i fotogrammi se lo schermo non è a una frequenza multipla del film."),
                P("smoothMotionMode", "Quando usarlo", "avoidJudder", "“Sempre” su schermi a 24 Hz crea ghosting.",
                    C("avoidJudder", "Solo se serve"), C("almostAlways", "Quasi sempre"), C("always", "Sempre")),
                P("contentType", "Deinterlacciamento", "auto", "Tipo di contenuto dei video interlacciati.", C("auto", "Automatico"), C("film", "Film"), C("video", "Video"))),
            G("Movimento", "Prestazioni",
                N("preRenderFramesWindowed", "Fotogrammi pre-renderizzati", "8", "Più alto = più stabile, più basso = pausa e ricerca più reattive.", Numbers(3, 4, 6, 8, 12, 16)),
                N("gpuQueueSize", "Coda GPU", "12", "", Numbers(6, 8, 12, 16, 24)),
                N("cpuQueueSize", "Coda CPU", "20", "", Numbers(8, 16, 20, 32)),
                // [2026-11-22] Con le due voci spente madVR perdeva 5-7 fotogrammi nel primo secondo e dopo ogni salto.
                T("delayPlaybackStart2", "Parti con la coda piena", "1", "Il film parte quando madVR ha già pronti i fotogrammi: niente fotogrammi persi all’avvio."),
                T("delaySeek", "Anche dopo un salto", "1")),
            // "Trade quality for performance" di madVR (le voci DXVA e 3DLUT sotto Wine non servono). Valori di partenza
            // letti dal prefisso il 23/11/2026.
            G("Prestazioni", "Compromessi qualità/prestazioni",
                T("downscalePath", "Scala la crominanza a parte", "1", "Quando fa risparmiare lavoro alla GPU."),
                T("10bitLuma", "Immagine a 10 bit invece di 16", "0", "Meno memoria e meno lavoro per la GPU, un po’ meno precisione."),
                T("10bitChroma", "Crominanza a 10 bit invece di 16", "0", "Come sopra, solo per il colore."),
                T("gammaDithering", "Dithering senza luce lineare", "0"),
                T("gammaBlending", "Smooth Motion senza luce lineare", "0", "Fusione dei fotogrammi più leggera."),
                T("dontRerenderFades", "Non ridisegnare le dissolvenze", "1", "Niente secondo passaggio quando madVR riconosce una dissolvenza."),
                T("noGradientAngles", "Deband senza analisi degli angoli", "1"),
                T("fastSubtitles", "Sottotitoli veloci", "1", "Sottotitoli ottimizzati per le prestazioni invece che per la qualità."),
                T("dumbToneMapping", "Tone mapping meno preciso", "0", "Compromesso su tone mapping e gamut mapping HDR."),
                T("customShaders16f", "Shader personalizzati a 16 bit", "0", "Invece di 32 bit."),
                T("customShadersTv", "Shader personalizzati in livelli video", "0", "Invece dei livelli PC."),
                T("mayLoseBtb", "Taglia sotto il nero e sopra il bianco", "0", "Perde i livelli BTB e WTW se migliora le prestazioni.")),
        };
    }

    // ------------------------------------------------------------------ valori salvati
    private static string StoreKey(string tab, string key) => "c:" + tab + "|" + key;

    /// <summary>
    /// Preset attivo per la riproduzione in corso (RenderPresets): le sue voci prevalgono su quelle salvate, solo per il
    /// motore. Le Impostazioni mostrano sempre i valori salvati (GetStored).
    /// </summary>
    public static RenderPresets.Preset? ActivePreset { get; set; }

    private static bool Overridden(string tab, Setting s, out string value)
    {
        value = "";
        return ActivePreset is { } p && RenderPresets.Tab(p.Renderer) == tab && p.Values.TryGetValue(s.Key, out value!) && value.Length > 0;
    }

    /// <summary>Valore per il motore: quello del preset attivo, se lo cambia, altrimenti quello salvato.</summary>
    public static string Get(string tab, Setting s) => Overridden(tab, s, out string v) ? v : GetStored(tab, s);

    /// <summary>Valore salvato nelle Impostazioni (senza preset).</summary>
    public static string GetStored(string tab, Setting s) => MacSettings.Get(StoreKey(tab, s.Key), s.Default);

    /// <summary>Etichetta del valore (per le scelte) o "Attivo/Disattivato".</summary>
    public static string ValueLabel(Setting s, string value) => s.Kind == Kind.Toggle ? (value == "1" ? "Attivo" : "Disattivato")
        : s.Choices?.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))?.Label ?? value;

    /// <summary>Salva la scelta; restituisce i comandi da mandare subito al VideoHost (solo madVR si regola dal vivo).</summary>
    public static IEnumerable<string> Set(string tab, Setting s, string value)
    {
        MacSettings.Set(StoreKey(tab, s.Key), value);
        if (tab != "madVR") yield break;
        foreach (var (name, v) in MadVrValues(s, value)) yield return $"madvr {name}={v}";
    }

    private static IEnumerable<(string Name, string Value)> MadVrValues(Setting s, string value)
    {
        if (s.Key == HdrProfileKey)
        {
            // Come WriteMadVrHdrProfile di Windows: la 3DLUT e' quella HDR→SDR configurata in madVR (Devices › calibration).
            string mode = value switch { "passthrough" => "passthrough", "tonemap" or "tonemapHdr" => "toneMapMath", "lut" => "toneMap3dlut", _ => "auto" };
            yield return ("hdrMode", mode);
            if (mode == "toneMapMath") yield return ("hdrOutputHdr", value == "tonemapHdr" ? "true" : "false");
            yield break;
        }
        yield return (s.Key, s.Kind == Kind.Toggle ? (value == "1" ? "true" : "false") : value);
    }

    private static readonly HashSet<string> AlwaysSentMadVr = new(StringComparer.Ordinal) { "delayPlaybackStart2", "delaySeek" };

    private static bool IsStored(string tab, Setting s) => MacSettings.Get(StoreKey(tab, s.Key), "") is { Length: > 0 };

    /// <summary>
    /// Argomenti per il VideoHost: le sole voci cambiate dall'utente (le altre restano come sono nel prefisso).
    /// --reg "chiave|nome|dword", --reg-sz "chiave|nome|testo", --madvr-set nome=valore.
    /// </summary>
    public static List<string> HostArguments()
    {
        var args = new List<string>();
        foreach (var tab in Tabs)
        {
            string? root = tab switch
            {
                "LAV Video" => @"Software\LAV\Video",
                "LAV Audio" => @"Software\LAV\Audio",
                "MPC Video Renderer" => @"Software\MPC-BE Filters\MPC Video Renderer",
                "Sottotitoli" => @"Software\Gabest\XySubFilter",
                _ => null,
            };
            bool style = false;
            foreach (var s in Sections(tab).SelectMany(x => x.Settings))
            {
                bool overridden = Overridden(tab, s, out string presetValue);
                // Voce che qualche preset cambia: nel registro si riscrive sempre il valore normale, cosi' il valore di un
                // preset usato prima non resta per i film dopo.
                bool touched = !overridden && tab != "madVR" && RenderPresets.All.Any(p => RenderPresets.Tab(p.Renderer) == tab && p.Values.ContainsKey(s.Key));
                // Le voci di madVR che il player vuole diverse da quelle di madVR si mandano anche senza una scelta salvata
                // (il VideoHost non riscrive un valore gia' uguale).
                if (!overridden && !touched && !IsStored(tab, s) && !(tab == "madVR" && AlwaysSentMadVr.Contains(s.Key))) continue;
                string value = overridden ? presetValue : GetStored(tab, s);
                if (tab == "madVR")
                {
                    // Preset: --madvr-try, che il VideoHost rimette com'era alla chiusura (anche dopo un blocco).
                    foreach (var (name, v) in MadVrValues(s, value)) { args.Add(overridden ? "--madvr-try" : "--madvr-set"); args.Add(name + "=" + v); }
                    continue;
                }
                if (s.Key.StartsWith("style:", StringComparison.Ordinal)) { style = true; continue; }
                if (root == null || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dword)) continue;
                int slash = s.Key.LastIndexOf('\\');
                string sub = slash < 0 ? root : root + "\\" + s.Key[..slash], name2 = slash < 0 ? s.Key : s.Key[(slash + 1)..];
                args.Add("--reg"); args.Add($"{sub}|{name2}|{dword}");
            }
            if (style) { args.Add("--reg-sz"); args.Add(@"Software\Gabest\XySubFilter\Text|style2|" + XySubStyle()); }
        }
        return args;
    }

    // Stile predefinito di XySubFilter (Text\style2), formato STSStyle: margini, allineamento, bordo, ombra, colori BGR,
    // alfa, charset, carattere, dimensione, ... Si parte dai valori predefiniti di Cinecore e si cambiano le voci scelte.
    private const string StyleDefaults = "20;20;20;20;2;0;2.000000;2.000000;3.000000;3.000000;0xffffff;0x00ffff;0x000000;0x000000;0x00;0x00;0x00;0x80;1;Arial;18.000000;100.000000;100.000000;0.000000;700;0;0;0;0.000000;0.000000;0.000000;0.000000;0.000000;2";

    private static string XySubStyle()
    {
        var f = StyleDefaults.Split(';');
        string F(string v) => double.Parse(v, CultureInfo.InvariantCulture).ToString("0.000000", CultureInfo.InvariantCulture);
        foreach (var s in Sections("Sottotitoli").SelectMany(x => x.Settings).Where(s => s.Key.StartsWith("style:", StringComparison.Ordinal)))
        {
            string v = Get("Sottotitoli", s);
            switch (s.Key[6..])
            {
                case "font": f[19] = v; break;
                case "size": f[20] = F(v); break;
                case "bold": f[24] = v == "1" ? "700" : "400"; break;
                case "outline": f[6] = f[7] = F(v); break;
                case "shadow": f[8] = f[9] = F(v); break;
                case "margin": f[3] = v; break;
                case "color":
                    int rgb = int.Parse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    f[10] = "0x" + (((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF)).ToString("x6", CultureInfo.InvariantCulture);
                    break;
            }
        }
        return string.Join(";", f);
    }
}
