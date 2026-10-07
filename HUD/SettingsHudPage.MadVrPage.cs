#nullable enable
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025.HUD
{
    // Pagina madVR: le voci del pannello originale che contano, divise in schede.
    // Nomi e valori sono quelli di IMadVRSettings (verificati uno per uno: madVR rifiuta
    // i valori non validi, e un test controlla che ogni scelta proposta qui sia accettata).
    internal sealed partial class SettingsHudPage
    {
        private const string MadVrHdrProfileKey = "hdr:profile";

        private static NativeSection[] MadVrSections()
        {
            NativeSection Group(string groupIt, string groupEn, string it, string en, params NativeSetting[] settings)
                => new(it, en, settings, groupIt, groupEn);
            NativeSetting Toggle(string key, string it, string en, string def = "0", string descIt = "", string descEn = "")
                => new(key, it, en, NativeKind.Toggle, def, descIt, descEn);
            NativeSetting Pick(string key, string it, string en, string def, string descIt, string descEn, params NativeChoice[] choices)
                => new(key, it, en, NativeKind.Choice, def, descIt, descEn, choices);
            NativeSetting Number(string key, string it, string en, string def, string descIt, string descEn, params NativeChoice[] choices)
                => new(key, it, en, NativeKind.IntChoice, def, descIt, descEn, choices);
            NativeChoice[] Offsets() => new[] { C("-20", "−20"), C("-10", "−10"), C("-5", "−5"), C("0", "0"), C("5", "+5"), C("10", "+10"), C("20", "+20") };
            NativeChoice[] Levels(params (string Value, string It, string En)[] values) => System.Array.ConvertAll(values, v => C(v.Value, v.It, v.En));

            return new[]
            {
                // ---- Immagine
                Group("Immagine", "Image", "Crominanza", "Chroma",
                    Pick("chromaUp", "Upscaling crominanza", "Chroma upscaling", "Bicubic60",
                        "NGU è il più nitido ma anche il più pesante per la GPU.", "NGU is the sharpest and the heaviest on the GPU.",
                        C("Bicubic60", "Bicubic 60"), C("Lanczos3", "Lanczos 3"), C("Jinc3", "Jinc 3"), C("ReconSharp", "Reconstruction sharp"),
                        C("NGUAntiAliasLow", "NGU Anti-Alias · basso", "NGU Anti-Alias · low"), C("NGUAntiAliasMed", "NGU Anti-Alias · medio", "NGU Anti-Alias · medium"),
                        C("NGUAntiAliasHigh", "NGU Anti-Alias · alto", "NGU Anti-Alias · high"), C("NGUAntiAliasVeryHigh", "NGU Anti-Alias · massimo", "NGU Anti-Alias · very high"),
                        C("NGUStandardMed", "NGU Standard · medio", "NGU Standard · medium"), C("NGUStandardHigh", "NGU Standard · alto", "NGU Standard · high"),
                        C("NGUStandardVeryHigh", "NGU Standard · massimo", "NGU Standard · very high")),
                    Toggle("superChromaRes", "SuperChromaRes", "SuperChromaRes", "0", "Rende più netti i bordi di colore.", "Sharpens color edges.")),
                Group("Immagine", "Image", "Upscaling", "Upscaling",
                    Pick("lumaUp", "Algoritmo", "Algorithm", "Lanczos3",
                        "Usato quando il video è più piccolo dello schermo. NGU raddoppia la risoluzione.", "Used when the video is smaller than the screen. NGU doubles the resolution.",
                        C("Bicubic60", "Bicubic 60"), C("Lanczos3", "Lanczos 3"), C("Lanczos4", "Lanczos 4"), C("Spline36", "Spline 36"), C("Jinc3", "Jinc 3"),
                        C("NGUAntiAlias", "NGU Anti-Alias"), C("NGUSoft", "NGU Soft"), C("NGUStandard", "NGU Standard"), C("NGUSharp", "NGU Sharp")),
                    Pick("lumaUpNguLumaDoubleQuality", "Qualità NGU", "NGU quality", "med", "", "",
                        C("low", "Bassa", "Low"), C("med", "Media", "Medium"), C("high", "Alta", "High"), C("veryHigh", "Massima", "Very high")),
                    Pick("lumaUpNguDoubleActivate", "Raddoppia da", "Double from", "auto",
                        "Fattore di ingrandimento oltre il quale entra in gioco NGU.", "Scaling factor above which NGU kicks in.",
                        C("auto", "Automatico", "Automatic"), C("1.2x", "1,2×", "1.2×"), C("1.5x", "1,5×", "1.5×"), C("2.0x", "2,0×", "2.0×"), C("always", "Sempre", "Always")),
                    Pick("lumaUpNguLumaQuadQuality", "Quadruplicazione", "Quadrupling", "auto", "", "",
                        C("auto", "Automatica", "Automatic"), C("disabled", "Disattivata", "Disabled"), C("2xmed", "2× media", "2× medium"), C("2xhigh", "2× alta", "2× high"), C("4xhigh", "4× alta", "4× high")),
                    Pick("lumaUpNguChromaQuality", "Crominanza nel raddoppio", "Chroma when doubling", "auto", "", "",
                        C("auto", "Automatica", "Automatic"), C("normal", "Normale", "Normal"), C("high", "Alta", "High"), C("veryHigh", "Massima", "Very high")),
                    Toggle("lumaUpSigmoidal", "Upscaling sigmoidale", "Sigmoidal upscaling", "1", "Riduce gli aloni attorno ai bordi.", "Reduces halos around edges.")),
                Group("Immagine", "Image", "Downscaling", "Downscaling",
                    Pick("lumaDown", "Algoritmo", "Algorithm", "Catmull-Rom",
                        "Usato quando il video è più grande dello schermo (es. 4K su 1080p).", "Used when the video is larger than the screen (e.g. 4K on 1080p).",
                        C("Catmull-Rom", "Catmull-Rom"), C("Bicubic150", "Bicubic 150"), C("Lanczos3", "Lanczos 3"), C("Jinc3", "Jinc 3"), C("SSIM1D100", "SSIM 1D"), C("SSIM2D100", "SSIM 2D")),
                    Toggle("lumaDownLinear", "Scaling in luce lineare", "Scale in linear light", "0")),

                // ---- Miglioramenti
                Group("Miglioramenti", "Enhancements", "Dopo l’upscaling", "After upscaling",
                    Toggle("superRes", "SuperRes", "SuperRes", "0", "Recupera nitidezza dopo l’upscaling.", "Restores sharpness after upscaling."),
                    Number("superResStrength", "Intensità SuperRes", "SuperRes strength", "2", "", "", C("1", "1"), C("2", "2"), C("3", "3"), C("4", "4")),
                    Toggle("upRefAdaptiveSharpen", "Nitidezza adattiva", "Adaptive sharpen", "0"),
                    Toggle("upRefLumaSharpen", "LumaSharpen", "LumaSharpen", "0"),
                    Toggle("upRefCrispenEdges", "Bordi più netti", "Crispen edges", "0"),
                    Toggle("upRefThinEdges", "Assottiglia i bordi", "Thin edges", "0"),
                    Toggle("upRefEnhanceDetail", "Migliora i dettagli", "Enhance detail", "0"),
                    Toggle("upRefAddGrain", "Aggiungi grana", "Add grain", "0")),
                Group("Miglioramenti", "Enhancements", "Prima dello scaling", "Before scaling",
                    Toggle("adaptiveSharpen", "Nitidezza adattiva", "Adaptive sharpen", "0"),
                    Toggle("lumaSharpen", "LumaSharpen", "LumaSharpen", "0"),
                    Toggle("crispenEdges", "Bordi più netti", "Crispen edges", "0"),
                    Toggle("sharpenEdges", "Nitidezza bordi", "Sharpen edges", "0"),
                    Toggle("enhanceDetail", "Migliora i dettagli", "Enhance detail", "0")),

                // ---- Artefatti
                Group("Artefatti", "Artifacts", "Banding", "Banding",
                    Toggle("debandActive", "Deband", "Deband", "0", "Riduce le bande nei gradienti (cielo, ombre).", "Reduces banding in gradients (skies, shadows)."),
                    Number("debandLevel", "Intensità", "Strength", "0", "", "", C("0", "Bassa", "Low"), C("1", "Media", "Medium"), C("2", "Alta", "High")),
                    Number("debandFadeLevel", "Durante le dissolvenze", "During fades", "2", "", "", C("0", "Bassa", "Low"), C("1", "Media", "Medium"), C("2", "Alta", "High"))),
                Group("Artefatti", "Artifacts", "Compressione e rumore", "Compression and noise",
                    Toggle("deringActive", "Rimuovi aloni (dering)", "Remove ringing", "0"),
                    Toggle("deblockLumaActive", "Deblocking", "Deblocking", "0", "Attenua i blocchi dei file molto compressi.", "Smooths blocks in heavily compressed files."),
                    Number("deblockStrength", "Intensità deblocking", "Deblocking strength", "1", "", "", C("1", "Bassa", "Low"), C("2", "Media", "Medium"), C("3", "Alta", "High")),
                    Toggle("denoiseLumaActive", "Riduzione rumore", "Denoise", "0"),
                    Number("denoiseStrength", "Intensità riduzione rumore", "Denoise strength", "1", "", "", C("1", "1"), C("2", "2"), C("3", "3"), C("4", "4"))),

                // ---- HDR
                Group("HDR", "HDR", "Contenuti HDR", "HDR content",
                    Pick(MadVrHdrProfileKey, "Gestione HDR", "HDR handling", "auto",
                        "Stesse opzioni del menu Immagine / HDR. RTX Video HDR richiede MPC Video Renderer.", "Same options as the Picture / HDR menu. RTX Video HDR requires MPC Video Renderer.",
                        C("auto", "Automatico (decide madVR)", "Automatic (madVR decides)"), C("passthrough", "Passthrough al display", "Passthrough to display"),
                        C("tonemap", "Tone mapping → SDR", "Tone mapping → SDR"), C("tonemapHdr", "Tone mapping con uscita HDR", "Tone mapping with HDR output"),
                        C("lut", "HDR → SDR con 3DLUT", "HDR → SDR via 3DLUT")),
                    Number("hdrNits", "Luminanza di picco del display", "Display peak luminance", "200",
                        "Nit usati dal tone mapping: più bassi = immagine più luminosa.", "Nits used by tone mapping: lower = brighter picture.",
                        C("100", "100"), C("120", "120"), C("140", "140"), C("160", "160"), C("200", "200"), C("250", "250"), C("300", "300"), C("400", "400"), C("600", "600"), C("1000", "1000")),
                    Number("hdrHighlightRecovery", "Recupero alte luci", "Highlight recovery", "0", "", "",
                        C("0", "Disattivato", "Off"), C("1", "Basso", "Low"), C("2", "Medio", "Medium"), C("3", "Alto", "High"), C("4", "Massimo", "Very high")),
                    Toggle("hdrMeasureFrameNits", "Misura ogni fotogramma", "Measure each frame", "0", "Tone mapping dinamico, più pesante per la GPU.", "Dynamic tone mapping, heavier on the GPU."),
                    Toggle("hdrLimitGamutBool", "Limita il gamut", "Limit gamut", "1"),
                    Pick("hdrLimitGamutStr", "Gamut massimo", "Maximum gamut", "DCI-P3", "", "", C("BT.709", "BT.709"), C("DCI-P3", "DCI-P3"), C("BT.2020", "BT.2020")),
                    Toggle("sendHdrMetadata", "Invia metadati HDR", "Send HDR metadata", "1")),

                // ---- Colore
                Group("Colore", "Color", "Display", "Display",
                    Pick("levels", "Livelli del display", "Display levels", "PC Levels", "PC (0-255) per monitor, TV (16-235) per molti TV.", "PC (0-255) for monitors, TV (16-235) for many TVs.",
                        Levels(("PC Levels", "PC (0-255)", "PC (0-255)"), ("TV Levels", "TV (16-235)", "TV (16-235)"))),
                    Number("displayBitdepth", "Profondità colore", "Bit depth", "0", "", "",
                        C("0", "Automatica", "Automatic"), C("8", "8 bit"), C("10", "10 bit")),
                    Pick("displayPrimaries", "Primarie del display", "Display primaries", "BT.709 (HD)", "", "",
                        C("BT.709 (HD)", "BT.709 (HD)"), C("DCI-P3", "DCI-P3"), C("BT.2020 (UHD)", "BT.2020 (UHD)"))),
                Group("Colore", "Color", "Regolazioni", "Adjustments",
                    Number("brightness", "Luminosità", "Brightness", "0", "", "", Offsets()),
                    Number("contrast", "Contrasto", "Contrast", "0", "", "", Offsets()),
                    Number("saturation", "Saturazione", "Saturation", "0", "", "", Offsets())),
                Group("Colore", "Color", "Dithering", "Dithering",
                    Pick("ditheringAlgo", "Algoritmo", "Algorithm", "errorDifMedNoise", "", "",
                        C("ordered", "Ordinato", "Ordered"), C("random", "Casuale", "Random"), C("errorDifLowNoise", "Diffusione errore · basso rumore", "Error diffusion · low noise"),
                        C("errorDifMedNoise", "Diffusione errore · medio", "Error diffusion · medium")),
                    Toggle("coloredDither", "Rumore colorato", "Colored noise", "1"),
                    Toggle("dynamicDither", "Cambia a ogni fotogramma", "Change every frame", "1")),

                // ---- Schermo e 3D
                Group("Schermo e 3D", "Display & 3D", "Frequenza", "Refresh rate",
                    Toggle("enableDisplayModeChanger", "Cambio frequenza automatico", "Automatic refresh rate", "0",
                        "madVR porta lo schermo a 23,976/24 Hz per i film. Le modalità si scelgono nel pannello originale.", "madVR switches the display to 23.976/24 Hz for films. Modes are listed in the original panel."),
                    Toggle("restoreDisplayModeOnClose", "Ripristina alla chiusura", "Restore on close", "1")),
                Group("Schermo e 3D", "Display & 3D", "3D", "3D",
                    Pick("3dFormat", "Formato 3D in uscita", "3D output format", "auto",
                        "Per i Blu-ray 3D (MVC). Per i file SBS/TAB usa il menu 3D del player.", "For 3D Blu-rays (MVC). For SBS/TAB files use the player's 3D menu.",
                        C("auto", "Automatico", "Automatic"), C("none", "Disattivato (2D)", "Off (2D)"), C("side-by-side", "Side-by-side"), C("top-and-bottom", "Top-and-bottom"),
                        C("line alternative", "Righe alternate", "Line alternative"), C("column alternative", "Colonne alternate", "Column alternative")),
                    Toggle("swapEyes", "Inverti occhi", "Swap eyes", "0")),
                Group("Schermo e 3D", "Display & 3D", "Bande nere", "Black bars",
                    Toggle("detectBars", "Rileva bande nere", "Detect black bars", "1"),
                    Toggle("cropBars", "Ritaglia bande nere", "Crop black bars", "0"),
                    Toggle("moveSubs", "Sottotitoli nelle bande", "Subtitles into the bars", "1")),

                // ---- Movimento
                Group("Movimento", "Motion", "Fluidità", "Smoothness",
                    Toggle("smoothMotionEnabled", "Smooth motion", "Smooth motion", "0",
                        "Fonde i fotogrammi se lo schermo non è a una frequenza multipla del film.", "Blends frames when the display rate is not a multiple of the film."),
                    Pick("smoothMotionMode", "Quando usarlo", "When to use it", "avoidJudder",
                        "“Sempre” su schermi a 24 Hz crea ghosting.", "“Always” causes ghosting on 24 Hz displays.",
                        C("avoidJudder", "Solo se serve", "Only when needed"), C("almostAlways", "Quasi sempre", "Almost always"), C("always", "Sempre", "Always")),
                    Pick("contentType", "Deinterlacciamento", "Deinterlacing", "auto", "Tipo di contenuto dei video interlacciati.", "Content type of interlaced videos.",
                        C("auto", "Automatico", "Automatic"), C("film", "Film"), C("video", "Video"))),
                Group("Movimento", "Motion", "Prestazioni", "Performance",
                    Number("preRenderFramesWindowed", "Fotogrammi pre-renderizzati", "Pre-rendered frames", "8",
                        "Più alto = più stabile, più basso = pausa e ricerca più reattive.", "Higher is steadier, lower makes pause and seeking snappier.",
                        C("3", "3"), C("4", "4"), C("6", "6"), C("8", "8"), C("12", "12"), C("16", "16")),
                    Number("gpuQueueSize", "Coda GPU", "GPU queue", "12", "", "", C("6", "6"), C("8", "8"), C("12", "12"), C("16", "16"), C("24", "24")),
                    Number("cpuQueueSize", "Coda CPU", "CPU queue", "20", "", "", C("8", "8"), C("16", "16"), C("20", "20"), C("32", "32")),
                    Toggle("useD3d11", "Presentazione Direct3D 11", "Direct3D 11 presentation", "1")),
            };
        }

        // "Gestione HDR" riunisce due voci di madVR (hdrMode + hdrOutputHdr) nelle stesse
        // scelte del menu Immagine / HDR, e avvisa il player per tenerle allineate.
        public event System.Action<string>? MadVrHdrProfileChanged;

        internal static string ReadMadVrHdrProfile()
        {
            string mode = MadVrSettingsStore.GetString("hdrMode") ?? "auto";
            bool hdrOut = MadVrSettingsStore.GetBool("hdrOutputHdr") == true;
            return mode switch
            {
                "passthrough" => "passthrough",
                "toneMapMath" => hdrOut ? "tonemapHdr" : "tonemap",
                "toneMap3dlut" => "lut",
                _ => "auto"
            };
        }

        internal static bool WriteMadVrHdrProfile(string profile)
        {
            string mode = profile switch
            {
                "passthrough" => "passthrough",
                "tonemap" or "tonemapHdr" => "toneMapMath",
                "lut" => "toneMap3dlut",
                _ => "auto"
            };
            bool ok = MadVrSettingsStore.SetString("hdrMode", mode);
            if (ok && mode == "toneMapMath")
                ok = MadVrSettingsStore.SetBool("hdrOutputHdr", profile == "tonemapHdr");
            return ok;
        }
    }
}
