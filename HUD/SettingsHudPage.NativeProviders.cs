#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;
using Microsoft.Win32;

namespace CinecorePlayer2025.HUD
{
    // Pagine "native" per le dipendenze esterne: le opzioni che contano, disegnate nello
    // stile delle impostazioni interne. Nessuna ha un'API di interfaccia, ma tutte hanno
    // un archivio leggibile: madVR tramite IMadVRSettings, LAV e XySubFilter tramite le
    // loro chiavi di registro (lette alla creazione del filtro). Il pannello originale
    // resta disponibile per tutto il resto.
    internal sealed partial class SettingsHudPage
    {
        private enum NativeKind { Toggle, Choice, IntChoice, Action }

        private sealed record NativeChoice(string Value, string It, string En);

        private sealed record NativeSetting(string Key, string It, string En, NativeKind Kind, string Default = "",
            string DescIt = "", string DescEn = "", NativeChoice[]? Choices = null);

        private sealed record NativeSection(string It, string En, NativeSetting[] Settings, string GroupIt = "", string GroupEn = "");

        private readonly HashSet<string> _showOriginalProvider = new(StringComparer.OrdinalIgnoreCase);
        private int _nativeMeasuredHeight;
        private readonly Dictionary<string, string> _nativeGroup = new(StringComparer.OrdinalIgnoreCase);
        private string _nativeError = string.Empty;

        private static bool HasNativeProviderPage(string tab)
            => tab is "madVR" or "Sottotitoli" or "LAV Video" or "LAV Audio" or "MPC Video Renderer" or "MPC Audio Renderer" or CinecoreAudioTab;

        private bool ShowingNativeProvider => HasNativeProviderPage(_tab) && !_showOriginalProvider.Contains(_tab);

        private static NativeChoice C(string value, string it, string? en = null) => new(value, it, en ?? it);

        private static NativeChoice[] Numbers(params int[] values)
            => values.Select(v => C(v.ToString(CultureInfo.InvariantCulture), v.ToString(CultureInfo.InvariantCulture))).ToArray();

        // Pagina del telecomando per le scelte del player che non appartengono a un componente:
        // qualita' dei flussi Jellyfin e dimensione dell'immagine a schermo intero.
        private const string PlayerTab = "Player";

        private static NativeSection[] PlayerSections() => new[]
        {
            new NativeSection("Avvio", "Startup", new[]
            {
                new NativeSetting("start-fullscreen", "Avvia a schermo intero", "Start full screen", NativeKind.Toggle, "0",
                    "Il player si apre senza finestra, sullo schermo dove si trova il mouse. Vale dal prossimo avvio.",
                    "The player opens without a window, on the screen where the mouse is. It applies from the next start.")
            }),
            new NativeSection("Jellyfin ed Emby", "Jellyfin and Emby", new[]
            {
                new NativeSetting("jellyfin-quality", "Qualità video", "Video quality", NativeKind.Choice, "0",
                    "Con un limite il server converte il film al volo: utile se la rete non regge il file originale.",
                    "With a limit the server converts the film on the fly: useful when the network cannot carry the original file.",
                    JellyfinClient.TranscodeChoices.Select(bitrate => new NativeChoice(bitrate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        JellyfinClient.TranscodeLabel(bitrate, false), JellyfinClient.TranscodeLabel(bitrate, true))).ToArray())
            }),
            new NativeSection("Immagine a schermo intero", "Full-screen image", new[]
            {
                new NativeSetting("sizing-mode", "Dimensione", "Size", NativeKind.Choice, "Fill",
                    "Altezza costante: tutti i film alti uguali, come in una sala con schermo panoramico.",
                    "Constant height: every film the same height, like a cinema with a scope screen.",
                    new[] { C("Fill", "Riempi", "Fill"), C("ConstantHeight", "Altezza costante", "Constant height"), C("ConstantArea", "Area costante", "Constant area"), C("Custom", "Personalizzata", "Custom") }),
                new NativeSetting("sizing-detect", "Riconosci le bande nere nel film", "Detect black bars in the film", NativeKind.Toggle, "1"),
                new NativeSetting("sizing-expand", "Scene più alte a tutto schermo", "Taller scenes on the whole screen", NativeKind.Toggle, "1"),
                new NativeSetting("sizing-transition", "Transizione al cambio di formato", "Transition on format change", NativeKind.Choice, "400", "", "",
                    new[] { C("0", "Nessuna", "None"), C("200", "200 ms"), C("400", "400 ms"), C("800", "800 ms"), C("1200", "1200 ms") }),
                new NativeSetting("sizing-bias", "Personalizzata: altezza ↔ area", "Custom: height ↔ area", NativeKind.Choice, "0", "", "",
                    new[] { C("0", "0% (altezza)", "0% (height)"), C("25", "25%"), C("50", "50%"), C("75", "75%"), C("100", "100% (area)", "100% (area)") })
            })
        };

        private string PlayerGet(NativeSetting s)
        {
            var sizing = ImageSizing;
            return s.Key switch
            {
                "start-fullscreen" => StartupPreferences.StartFullscreen ? "1" : "0",
                "jellyfin-quality" => JellyfinClient.TranscodeBitrate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "sizing-mode" => (sizing?.Mode ?? ImageSizingMode.Fill).ToString(),
                "sizing-detect" => sizing?.DetectBars == true ? "1" : "0",
                "sizing-expand" => sizing?.ExpandTallScenes == true ? "1" : "0",
                "sizing-transition" => new[] { 0, 200, 400, 800, 1200 }.OrderBy(value => Math.Abs(value - (sizing?.TransitionMs ?? 400))).First().ToString(),
                "sizing-bias" => new[] { 0, 25, 50, 75, 100 }.OrderBy(value => Math.Abs(value - (sizing?.Bias ?? 0) * 100)).First().ToString(),
                _ => ""
            };
        }

        private bool PlayerSet(NativeSetting s, string value)
        {
            if (s.Key == "start-fullscreen")
            {
                StartupPreferences.StartFullscreen = value == "1";
                Invalidate();
                return true;
            }
            if (s.Key == "jellyfin-quality")
            {
                if (!int.TryParse(value, out int bitrate) || !JellyfinClient.TranscodeChoices.Contains(bitrate)) return false;
                JellyfinClient.TranscodeBitrate = bitrate;
                Invalidate();
                return true;
            }
            if (ImageSizing is not { } sizing) return false;
            switch (s.Key)
            {
                case "sizing-mode": if (!Enum.TryParse(value, out ImageSizingMode mode)) return false; sizing.Mode = mode; break;
                case "sizing-detect": sizing.DetectBars = value is "1" or "true"; break;
                case "sizing-expand": sizing.ExpandTallScenes = value is "1" or "true"; break;
                case "sizing-transition": if (!int.TryParse(value, out int ms)) return false; sizing.TransitionMs = Math.Clamp(ms, 0, 1200); break;
                case "sizing-bias": if (!int.TryParse(value, out int bias)) return false; sizing.Bias = Math.Clamp(bias / 100.0, 0, 1); break;
                default: return false;
            }
            Invalidate();
            ImageSizingChanged?.Invoke();
            return true;
        }

        private static NativeSection[] NativeSections(string tab) => tab switch
        {
            PlayerTab => PlayerSections(),
            "madVR" => MadVrSections(),
            CinecoreAudioTab => CinecoreAudioSections(),
            "LAV Video" => new[]
            {
                new NativeSection("Decodifica", "Decoding", new[]
                {
                    new NativeSetting(@"HWAccel\HWAccel", "Accelerazione hardware", "Hardware acceleration", NativeKind.IntChoice, "5",
                        "D3D11 è la scelta migliore con madVR e MPC Video Renderer.", "D3D11 is the best choice with madVR and MPC Video Renderer.",
                        new[] { C("0", "Nessuna (software)", "None (software)"), C("1", "NVIDIA CUVID"), C("2", "Intel QuickSync"), C("3", "DXVA2 copy-back"), C("4", "DXVA2 nativo", "DXVA2 native"), C("5", "D3D11") }),
                    new NativeSetting(@"HWAccel\hevc", "HEVC in hardware", "HEVC in hardware", NativeKind.Toggle, "1"),
                    new NativeSetting(@"HWAccel\h264", "H.264 in hardware", "H.264 in hardware", NativeKind.Toggle, "1"),
                    new NativeSetting(@"HWAccel\av1", "AV1 in hardware", "AV1 in hardware", NativeKind.Toggle, "1"),
                    new NativeSetting(@"HWAccel\vp9", "VP9 in hardware", "VP9 in hardware", NativeKind.Toggle, "1"),
                    new NativeSetting("NumThreads", "Thread software", "Software threads", NativeKind.IntChoice, "0", "", "",
                        new[] { C("0", "Automatico", "Automatic"), C("2", "2"), C("4", "4"), C("8", "8"), C("16", "16") }),
                }),
                new NativeSection("Immagine", "Picture", new[]
                {
                    new NativeSetting("DeintMode", "Deinterlacciamento", "Deinterlacing", NativeKind.IntChoice, "0", "", "",
                        new[] { C("0", "Automatico", "Automatic"), C("1", "Aggressivo", "Aggressive"), C("2", "Sempre", "Always"), C("3", "Disattivato", "Disabled") }),
                    new NativeSetting("RGBRange", "Gamma RGB in uscita", "RGB output range", NativeKind.IntChoice, "0", "", "",
                        new[] { C("0", "Come l’origine", "Same as source"), C("1", "TV (16-235)"), C("2", "PC (0-255)") }),
                    new NativeSetting("DitherMode", "Dithering", "Dithering", NativeKind.IntChoice, "1", "", "",
                        new[] { C("0", "Ordinato", "Ordered"), C("1", "Casuale", "Random") }),
                }),
            },
            "LAV Audio" => new[]
            {
                new NativeSection("Bitstream", "Bitstream", new[]
                {
                    new NativeSetting("Bitstreaming_ac3", "Dolby Digital", "Dolby Digital", NativeKind.Toggle, "1"),
                    new NativeSetting("Bitstreaming_eac3", "Dolby Digital Plus", "Dolby Digital Plus", NativeKind.Toggle, "1"),
                    new NativeSetting("Bitstreaming_truehd", "Dolby TrueHD / Atmos", "Dolby TrueHD / Atmos", NativeKind.Toggle, "1"),
                    new NativeSetting("Bitstreaming_dts", "DTS", "DTS", NativeKind.Toggle, "1"),
                    new NativeSetting("Bitstreaming_dtshd", "DTS-HD / DTS:X", "DTS-HD / DTS:X", NativeKind.Toggle, "1"),
                }),
                new NativeSection("Elaborazione", "Processing", new[]
                {
                    new NativeSetting("DRCEnabled", "Compressione dinamica", "Dynamic range compression", NativeKind.Toggle, "0",
                        "Dialoghi più udibili a basso volume.", "Clearer dialogue at low volume."),
                    new NativeSetting("DRCLevel", "Intensità compressione", "Compression amount", NativeKind.IntChoice, "100", "", "", Numbers(25, 50, 75, 100)),
                    new NativeSetting("Mixing", "Downmix", "Downmix", NativeKind.Toggle, "0",
                        "Adatta le tracce multicanale all’impianto.", "Fits multichannel tracks to your speakers."),
                    new NativeSetting("MixingLayout", "Canali in uscita", "Output channels", NativeKind.IntChoice, "3", "", "",
                        new[] { C("3", "Stereo"), C("1551", "5.1"), C("1599", "7.1") }),
                    new NativeSetting("AutoAVSync", "Correzione automatica A/V", "Automatic A/V correction", NativeKind.Toggle, "1"),
                }),
            },
            "MPC Video Renderer" => MpcVideoRendererSections(),
            "MPC Audio Renderer" => MpcAudioRendererSections(),
            "Sottotitoli" => new[]
            {
                new NativeSection("Caricamento", "Loading", new[]
                {
                    new NativeSetting(@"General\ext_load", "File esterni", "External files", NativeKind.Toggle, "1",
                        ".srt, .ass e simili accanto al video.", ".srt, .ass and similar files next to the video."),
                    new NativeSetting(@"General\emb_load", "Tracce incorporate", "Embedded tracks", NativeKind.Toggle, "1"),
                    new NativeSetting(@"General\hide", "Nascondi sottotitoli", "Hide subtitles", NativeKind.Toggle, "0"),
                }),
                new NativeSection("Testo", "Text", new[]
                {
                    new NativeSetting("style:font", "Carattere", "Font", NativeKind.Choice, "Arial",
                        "Vale per i sottotitoli senza stile proprio (SRT).", "Applies to subtitles without their own style (SRT).",
                        new[] { C("Arial", "Arial"), C("Segoe UI", "Segoe UI"), C("Calibri", "Calibri"), C("Verdana", "Verdana"), C("Tahoma", "Tahoma"), C("Trebuchet MS", "Trebuchet MS") }),
                    new NativeSetting("style:size", "Dimensione", "Size", NativeKind.IntChoice, "18", "", "", Numbers(14, 16, 18, 20, 22, 24, 28, 32)),
                    new NativeSetting("style:bold", "Grassetto", "Bold", NativeKind.Toggle, "1"),
                    new NativeSetting("style:color", "Colore", "Color", NativeKind.Choice, "ffffff", "", "",
                        new[] { C("ffffff", "Bianco", "White"), C("f5f0dc", "Avorio", "Ivory"), C("ffff00", "Giallo", "Yellow"), C("d0d0d0", "Grigio chiaro", "Light gray") }),
                    new NativeSetting("style:outline", "Bordo", "Outline", NativeKind.IntChoice, "2", "", "",
                        new[] { C("0", "Nessuno", "None"), C("1", "Sottile", "Thin"), C("2", "Normale", "Normal"), C("3", "Spesso", "Thick"), C("4", "Molto spesso", "Heavy") }),
                    new NativeSetting("style:shadow", "Ombra", "Shadow", NativeKind.IntChoice, "3", "", "",
                        new[] { C("0", "Nessuna", "None"), C("1", "Leggera", "Light"), C("2", "Media", "Medium"), C("3", "Marcata", "Strong") }),
                    new NativeSetting("style:margin", "Distanza dal bordo", "Distance from edge", NativeKind.IntChoice, "20", "", "", Numbers(10, 20, 30, 40, 60)),
                }),
                new NativeSection("Posizione", "Position", new[]
                {
                    new NativeSetting(@"Text\override_placement", "Posizione personalizzata", "Custom position", NativeKind.Toggle, "0",
                        "Sposta anche i sottotitoli con posizione propria.", "Also moves subtitles that set their own position."),
                    new NativeSetting(@"Text\y_perc", "Altezza sullo schermo", "Height on screen", NativeKind.IntChoice, "90", "", "",
                        new[] { C("80", "80%"), C("85", "85%"), C("90", "90%"), C("95", "95%"), C("98", "98%") }),
                }),
            },
            _ => Array.Empty<NativeSection>()
        };

        // MPC Video Renderer (registro, letto all'apertura del video). I valori seguono gli
        // elenchi del filtro: upscaling 0..5, downscaling 0..5, crominanza 0..2, texture 0/8/10/16.
        private static NativeSection[] MpcVideoRendererSections()
        {
            NativeSetting Toggle(string key, string it, string en, string def, string descIt = "", string descEn = "")
                => new(key, it, en, NativeKind.Toggle, def, descIt, descEn);
            NativeSetting Pick(string key, string it, string en, string def, string descIt, string descEn, params NativeChoice[] choices)
                => new(key, it, en, NativeKind.IntChoice, def, descIt, descEn, choices);
            return new[]
            {
                new NativeSection("Scaling", "Scaling", new[]
                {
                    Pick("Upscaling", "Upscaling", "Upscaling", "4", "", "",
                        C("0", "Nearest neighbor"), C("1", "Mitchell-Netravali"), C("2", "Catmull-Rom"), C("3", "Lanczos 2"), C("4", "Lanczos 3"), C("5", "Jinc 2")),
                    Pick("Downscaling", "Downscaling", "Downscaling", "5", "", "",
                        C("0", "Box"), C("1", "Bilinear"), C("2", "Hamming"), C("3", "Bicubic"), C("4", "Bicubic sharp"), C("5", "Lanczos")),
                    Pick("ChromaUpsampling", "Upscaling crominanza", "Chroma upscaling", "2", "", "",
                        C("0", "Nearest neighbor"), C("1", "Bilinear"), C("2", "Catmull-Rom")),
                    Toggle("InterpolateAt50pct", "Interpola al 50%", "Interpolate at 50%", "1"),
                    Toggle("Dither", "Dithering", "Dithering", "1"),
                }, "Immagine", "Picture"),
                new NativeSection("Video processor", "Video processor", new[]
                {
                    Toggle("UseD3D11", "Direct3D 11", "Direct3D 11", "1", "Necessario per RTX Super Resolution e RTX Video HDR.", "Required for RTX Super Resolution and RTX Video HDR."),
                    Toggle("VPScaling", "Scaling con il video processor", "Scale with the video processor", "1"),
                    Pick("VPSuperResolution", "RTX / Intel Super Resolution", "RTX / Intel Super Resolution", "0",
                        "Fino a quale risoluzione di partenza usarla.", "Up to which source resolution to use it.",
                        C("0", "Disattivata", "Off"), C("1", "SD"), C("2", "Fino a 720p", "Up to 720p"), C("3", "Fino a 1080p", "Up to 1080p"), C("4", "Fino a 1440p", "Up to 1440p")),
                    Pick("TextureFormat", "Formato texture", "Texture format", "0", "", "",
                        C("0", "Automatico (8/10 bit)", "Automatic (8/10-bit)"), C("8", "8 bit"), C("10", "10 bit"), C("16", "16 bit float")),
                }, "Immagine", "Picture"),
                new NativeSection("Deinterlacciamento", "Deinterlacing", new[]
                {
                    Toggle("VPDeinterlacing", "Deinterlacciamento", "Deinterlacing", "1"),
                    Toggle("DoubleFramerateDeinterlace", "Frequenza doppia", "Double frame rate", "1"),
                    Toggle("DeinterlaceBlend", "Fusione (blend)", "Blend", "0"),
                }, "Immagine", "Picture"),
                new NativeSection("HDR", "HDR", new[]
                {
                    Toggle("HdrPassthrough", "Passthrough HDR", "HDR passthrough", "1", "Invia l’HDR al display quando Windows è in HDR.", "Sends HDR to the display when Windows is in HDR mode."),
                    Toggle("ConvertToSdr", "Converti HDR in SDR", "Convert HDR to SDR", "1", "Quando il display non è in HDR.", "When the display is not in HDR mode."),
                    Pick("DisplayNits", "Luminanza display SDR", "SDR display luminance", "125", "", "",
                        C("80", "80 nit"), C("100", "100 nit"), C("125", "125 nit"), C("160", "160 nit"), C("200", "200 nit"), C("250", "250 nit"), C("300", "300 nit")),
                    Toggle("VPRTXVideoHDR", "RTX Video HDR", "RTX Video HDR", "0", "Converte i video SDR in HDR (GPU NVIDIA RTX).", "Converts SDR video to HDR (NVIDIA RTX GPU)."),
                    Toggle("HdrPreferDoVi", "Preferisci Dolby Vision", "Prefer Dolby Vision", "0"),
                }, "HDR", "HDR"),
                new NativeSection("Presentazione", "Presentation", new[]
                {
                    Pick("SwapEffect", "Presentazione", "Presentation", "0", "", "", C("0", "Discard"), C("1", "Flip")),
                    Toggle("VBlankBeforePresent", "Attendi il VBlank", "Wait for VBlank", "0"),
                    Toggle("AdjustPresentationTime", "Correggi i tempi di presentazione", "Adjust presentation time", "0"),
                    Toggle("ReinitWhenChangingDisplay", "Reinizializza al cambio schermo", "Reinitialize on display change", "0"),
                    Toggle("ShowStatistics", "Statistiche a schermo", "On-screen statistics", "0", "Il riquadro di testo in alto a sinistra sul video.", "The text box in the top-left corner of the video."),
                }, "Presentazione", "Presentation"),
            };
        }

        private static NativeSection[] MpcAudioRendererSections()
        {
            NativeSetting Toggle(string key, string it, string en, string def, string descIt = "", string descEn = "")
                => new(key, it, en, NativeKind.Toggle, def, descIt, descEn);
            return new[]
            {
                new NativeSection("Uscita", "Output", new[]
                {
                    new NativeSetting("DeviceMode", "Modalità WASAPI", "WASAPI mode", NativeKind.IntChoice, "0",
                        "Esclusiva: il player usa il dispositivo da solo, senza il mixer di Windows.", "Exclusive: the player takes the device alone, bypassing the Windows mixer.",
                        new[] { C("0", "Condivisa", "Shared"), C("1", "Esclusiva", "Exclusive") }),
                    new NativeSetting("WasapiMethod", "Metodo", "Method", NativeKind.IntChoice, "0", "", "",
                        new[] { C("0", "Evento", "Event"), C("1", "Push") }),
                    new NativeSetting("BufferDuration", "Buffer", "Buffer", NativeKind.IntChoice, "50", "", "",
                        new[] { C("0", "Predefinito", "Default"), C("50", "50 ms"), C("100", "100 ms"), C("200", "200 ms") }),
                    Toggle("UseBitExactOutput", "Uscita bit-exact", "Bit-exact output", "1"),
                    Toggle("UseSystemLayoutChannels", "Canali come in Windows", "Channels as in Windows", "1"),
                    Toggle("ReleaseDeviceIdle", "Libera il dispositivo in pausa", "Release device when idle", "1"),
                }),
                new NativeSection("Elaborazione", "Processing", new[]
                {
                    Toggle("CrossFeed", "Crossfeed per cuffie", "Headphone crossfeed", "0", "Suono più naturale in cuffia per i mix stereo.", "More natural headphone sound for stereo mixes."),
                    Toggle("DummyChannels", "Canali fittizi", "Dummy channels", "0"),
                }),
            };
        }

        private static string? NativeRegistryBase(string tab) => tab switch
        {
            "LAV Video" => @"Software\LAV\Video",
            "MPC Video Renderer" => @"Software\MPC-BE Filters\MPC Video Renderer",
            "MPC Audio Renderer" => @"Software\MPC-BE Filters\MPC Audio Renderer",
            "LAV Audio" => @"Software\LAV\Audio",
            "Sottotitoli" => @"Software\Gabest\XySubFilter",
            _ => null
        };

        private string NativeGet(string tab, NativeSetting s)
        {
            try
            {
                if (tab == PlayerTab) return PlayerGet(s);
                if (tab == CinecoreAudioTab) return CinecoreAudioGet(s);
                if (tab == "madVR" && s.Key == MadVrHdrProfileKey)
                    return ReadMadVrHdrProfile();
                if (tab == "madVR")
                {
                    return s.Kind switch
                    {
                        NativeKind.Toggle => MadVrSettingsStore.GetBool(s.Key) is bool b ? (b ? "1" : "0") : s.Default,
                        NativeKind.IntChoice => MadVrSettingsStore.GetInt(s.Key)?.ToString(CultureInfo.InvariantCulture) ?? s.Default,
                        _ => MadVrSettingsStore.GetString(s.Key) ?? s.Default
                    };
                }
                if (s.Key.StartsWith("style:", StringComparison.Ordinal))
                    return XySubStyle.Read().Get(s.Key[6..]) ?? s.Default;
                string? root = NativeRegistryBase(tab);
                if (root == null) return s.Default;
                (string sub, string name) = SplitRegistryKey(root, s.Key);
                using var key = Registry.CurrentUser.OpenSubKey(sub);
                return key?.GetValue(name) is int dword ? dword.ToString(CultureInfo.InvariantCulture) : s.Default;
            }
            catch { return s.Default; }
        }

        private bool NativeSet(string tab, NativeSetting s, string value)
        {
            try
            {
                if (tab == PlayerTab) return PlayerSet(s, value);
                if (tab == CinecoreAudioTab) return CinecoreAudioSet(s, value);
                if (tab == "madVR" && s.Key == MadVrHdrProfileKey)
                {
                    bool written = WriteMadVrHdrProfile(value);
                    if (written) MadVrHdrProfileChanged?.Invoke(value);
                    return written;
                }
                if (tab == "madVR")
                {
                    return s.Kind switch
                    {
                        NativeKind.Toggle => MadVrSettingsStore.SetBool(s.Key, value == "1"),
                        NativeKind.IntChoice => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && MadVrSettingsStore.SetInt(s.Key, n),
                        _ => MadVrSettingsStore.SetString(s.Key, value)
                    };
                }
                if (s.Key.StartsWith("style:", StringComparison.Ordinal))
                {
                    var style = XySubStyle.Read();
                    style.Set(s.Key[6..], value);
                    return style.Write();
                }
                string? root = NativeRegistryBase(tab);
                if (root == null || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dword)) return false;
                (string sub, string name) = SplitRegistryKey(root, s.Key);
                using var key = Registry.CurrentUser.CreateSubKey(sub);
                key.SetValue(name, dword, RegistryValueKind.DWord);
                return true;
            }
            catch { return false; }
        }

        private static (string Sub, string Name) SplitRegistryKey(string root, string key)
        {
            int slash = key.LastIndexOf('\\');
            return slash < 0 ? (root, key) : (root + "\\" + key[..slash], key[(slash + 1)..]);
        }

        private string NativeStatusText(string tab) => tab switch
        {
            PlayerTab => L("La qualità vale dal prossimo film; la dimensione si applica subito.", "Quality applies from the next film; the size applies at once."),
            "madVR" when !MadVrSettingsStore.Available => L("madVR non è disponibile su questo sistema.", "madVR is not available on this system."),
            "madVR" when MadVrSettingsStore.IsLive => L("Le modifiche si applicano subito al video in riproduzione.", "Changes apply immediately to the playing video."),
            "madVR" => L("Le modifiche vengono salvate in madVR.", "Changes are saved to madVR."),
            CinecoreAudioTab when Audio.CinecoreAudioEngine.Active is { } engine => L("In riproduzione: ", "Playing: ") + engine.SourceDescription + " → " + engine.OutputDescription,
            CinecoreAudioTab => L("Le modifiche si applicano subito al brano in riproduzione.", "Changes apply immediately to the playing track."),
            _ => L("Le modifiche valgono dal prossimo video aperto.", "Changes take effect from the next video you open.")
        };

        private void DrawNativeProviderRows(Graphics g, Rectangle body)
        {
            int width = Math.Min(1120, body.Width);
            int controlW = Math.Min(330, Math.Max(210, width * 2 / 5));
            int controlX = body.Left + width - controlW;
            int y = body.Top;
            using var heading = UiFont("Segoe UI Semibold", 9f);
            using var label = UiFont("Segoe UI", 10.5f);
            using var caption = UiFont("Segoe UI", 9f);

            // Riga di stato + accesso al pannello originale.
            bool ownPage = _tab == CinecoreAudioTab; // nessun pannello di terze parti
            var original = ownPage ? new Rectangle(body.Left + width, y, 0, 32) : new Rectangle(body.Left + width - 200, y, 200, 32);
            TextRenderer.DrawText(g, NativeStatusText(_tab), caption, new Rectangle(body.Left, y, Math.Max(1, original.Left - body.Left - 16), 32), Muted, SettingsText);
            if (!ownPage)
            {
                DrawTextAction(g, original, L("Pannello originale", "Original panel"), "settings");
                _hits.Add(new Hit { Bounds = original, Kind = HitKind.NativeMode, Key = "original" });
            }
            y += 44;
            if (_nativeError.Length > 0)
            {
                TextRenderer.DrawText(g, _nativeError, caption, new Rectangle(body.Left, y - 10, width, 22), Color.FromArgb(232, 110, 110), SettingsText);
                y += 16;
            }

            bool available = _tab != "madVR" || MadVrSettingsStore.Available;
            var sections = NativeSections(_tab);
            var groups = sections.Select(section => section.GroupIt).Where(group => group.Length > 0).Distinct().ToList();
            if (available && groups.Count > 1)
            {
                // Schede come quelle di MPV: una riga di testo con la linea d'accento sotto.
                string current = _nativeGroup.TryGetValue(_tab, out string? chosen) && groups.Contains(chosen) ? chosen : groups[0];
                using var tabFont = UiFont("Segoe UI", 10f);
                int gap = 6, tabW = Math.Max(60, (width - gap * (groups.Count - 1)) / groups.Count);
                for (int i = 0; i < groups.Count; i++)
                {
                    var section = sections.First(sec => sec.GroupIt == groups[i]);
                    var tabRect = new Rectangle(body.Left + i * (tabW + gap), y, i == groups.Count - 1 ? body.Left + width - (body.Left + i * (tabW + gap)) : tabW, 34);
                    bool selectedGroup = groups[i] == current;
                    bool hoverGroup = tabRect.Contains(_lastMouse);
                    TextRenderer.DrawText(g, L(section.GroupIt, section.GroupEn), tabFont, tabRect, selectedGroup || hoverGroup ? TextColor : Muted, SettingsText | TextFormatFlags.HorizontalCenter);
                    using (var line = new Pen(selectedGroup ? Accent : Color.FromArgb(hoverGroup ? 110 : 55, Border), selectedGroup ? 3 : 1))
                        g.DrawLine(line, tabRect.Left, tabRect.Bottom, tabRect.Right, tabRect.Bottom);
                    _hits.Add(new Hit { Bounds = tabRect, Kind = HitKind.NativeMode, Key = "group:" + groups[i] });
                }
                y += 50;
                sections = sections.Where(section => section.GroupIt == current).ToArray();
                if (ownPage && current == "Equalizzatore")
                    y += DrawEqCurve(g, new Rectangle(body.Left, y, width, 260)) + 8;
            }
            if (available)
            {
                foreach (var section in sections)
                {
                    using (var line = new Pen(Color.FromArgb(50, Border)))
                        g.DrawLine(line, body.Left, y, body.Left + width, y);
                    TextRenderer.DrawText(g, L(section.It, section.En).ToUpperInvariant(), heading, new Rectangle(body.Left, y + 20, width, 24), Muted, SettingsText);
                    y += 56;
                    foreach (var setting in section.Settings)
                    {
                        string description = L(setting.DescIt, setting.DescEn);
                        int textW = Math.Max(90, controlX - body.Left - 28);
                        TextRenderer.DrawText(g, L(setting.It, setting.En), label, new Rectangle(body.Left, y, textW, 30), TextColor, SettingsText);
                        if (description.Length > 0)
                            TextRenderer.DrawText(g, description, caption, new Rectangle(body.Left, y + 29, textW, 23), Muted, SettingsText);
                        var control = new Rectangle(controlX, y + 1, controlW, 36);
                        string value = NativeGet(_tab, setting);
                        if (setting.Kind == NativeKind.Action)
                        {
                            // Azione testuale allineata a destra (es. "Aggiungi banda").
                            var action = new Rectangle(control.Right - 220, control.Top, 220, control.Height);
                            DrawTextAction(g, action, L(setting.Default, setting.DescEn.Length > 0 ? setting.DescEn : setting.Default), "");
                            _hits.Add(new Hit { Bounds = action, Kind = HitKind.NativeMode, Key = "action:" + setting.Key });
                        }
                        else if (setting.Kind == NativeKind.Toggle)
                        {
                            bool on = value == "1";
                            DrawSettingsSwitch(g, control, on ? L("Attivo", "Enabled") : L("Disattivato", "Disabled"), on);
                            _hits.Add(new Hit { Bounds = control, Kind = HitKind.NativeToggle, Key = setting.Key });
                        }
                        else
                        {
                            var choice = setting.Choices?.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
                            DrawValueChip(g, control, choice != null ? L(choice.It, choice.En) : value, control.Contains(_lastMouse));
                            _hits.Add(new Hit { Bounds = control, Kind = HitKind.NativeChoice, Key = setting.Key });
                        }
                        y += description.Length > 0 ? 66 : 56;
                    }
                    y += 12;
                }
            }

            int measured = y - body.Top + 40;
            if (measured != _nativeMeasuredHeight)
            {
                _nativeMeasuredHeight = measured;
                if (IsHandleCreated) BeginInvoke(new Action(Invalidate));
            }
        }

        // ---- Telecomando: stesse pagine e stessi valori del player.
        private static readonly string[] RemoteProviderOrder = { PlayerTab, CinecoreAudioTab, "madVR", "LAV Video", "LAV Audio", "MPC Video Renderer", "MPC Audio Renderer", "Sottotitoli" };

        internal object DescribeForRemote()
        {
            var providers = new List<object>();
            foreach (string tab in RemoteProviderOrder)
            {
                bool available = tab != "madVR" || MadVrSettingsStore.Available;
                providers.Add(new
                {
                    id = tab,
                    title = TabLabel(tab),
                    note = NativeStatusText(tab),
                    available,
                    sections = !available ? Array.Empty<object>() : NativeSections(tab).Select(section => (object)new
                    {
                        group = L(section.GroupIt, section.GroupEn),
                        title = L(section.It, section.En),
                        settings = section.Settings.Select(setting => new
                        {
                            key = setting.Key,
                            label = L(setting.It, setting.En),
                            description = setting.Kind == NativeKind.Action ? "" : L(setting.DescIt, setting.DescEn),
                            kind = setting.Kind switch { NativeKind.Toggle => "toggle", NativeKind.Action => "action", _ => "choice" },
                            action = setting.Kind == NativeKind.Action ? L(setting.Default, setting.DescEn) : null,
                            // Le righe dipendono da altri valori (tipo di EQ, banda scelta): il telecomando ricarica la pagina.
                            reload = tab == CinecoreAudioTab && setting.Key is "ca:mode" or "ca:pb:select" or "ca:pb:type" or "ca:pb:freq" or "ca:preset",
                            value = setting.Kind == NativeKind.Action ? "" : NativeGet(tab, setting),
                            choices = setting.Choices?.Select(choice => new { value = choice.Value, label = L(choice.It, choice.En) }).ToArray()
                        }).ToArray()
                    }).ToArray()
                });
            }
            return new { language = _language, providers };
        }

        internal object ApplyRemoteSetting(string tab, string key, string value)
        {
            var setting = NativeSections(tab).SelectMany(section => section.Settings).FirstOrDefault(s => s.Key == key);
            if (setting == null) return new { ok = false, error = "unknown setting" };
            if (setting.Kind == NativeKind.Action)
            {
                bool done = tab == CinecoreAudioTab && CinecoreAudioAction(key);
                if (ShowingNativeProvider && _tab == tab) Invalidate();
                return new { ok = done, value = "", reload = true, error = done ? null : L("Azione non disponibile.", "Action not available.") };
            }
            bool ok = NativeSet(tab, setting, value);
            if (ShowingNativeProvider && _tab == tab) Invalidate();
            return new { ok, value = NativeGet(tab, setting), error = ok ? null : L("Valore non accettato.", "Value not accepted.") };
        }

        private NativeSetting? FindNativeSetting(string key)
            => NativeSections(_tab).SelectMany(section => section.Settings).FirstOrDefault(s => s.Key == key);

        private void ApplyNativeValue(NativeSetting setting, string value)
        {
            string tab = _tab;
            _nativeError = NativeSet(tab, setting, value) ? string.Empty
                : L("Impossibile salvare “" + setting.It + "”.", "Could not save “" + setting.En + "”.");
            Invalidate();
        }

        private void ToggleNativeSetting(string key)
        {
            if (FindNativeSetting(key) is not { } setting) return;
            ApplyNativeValue(setting, NativeGet(_tab, setting) == "1" ? "0" : "1");
        }

        private void ShowNativeChoice(string key, Rectangle source, Point click)
        {
            if (FindNativeSetting(key) is not { Choices: { } choices } setting) return;
            string current = NativeGet(_tab, setting);
            // Le frecce ‹ › scorrono i valori come nelle altre impostazioni; il centro apre l'elenco.
            int step = click.X < source.Left + 34 ? -1 : click.X > source.Right - 34 ? 1 : 0;
            if (step != 0)
            {
                int index = Array.FindIndex(choices, c => string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase));
                if (index < 0) index = step > 0 ? -1 : 0;
                ApplyNativeValue(setting, choices[(index + step + choices.Length) % choices.Length].Value);
                return;
            }
            var options = choices.Select(choice => new ChoicePopup.Option(L(choice.It, choice.En),
                () => ApplyNativeValue(setting, choice.Value), string.Equals(choice.Value, current, StringComparison.OrdinalIgnoreCase)));
            ChoicePopup.Show(this, new Point(source.Left, source.Bottom + 4), options, source.Width);
        }

        private void SetNativeGroup(string group)
        {
            _nativeGroup[_tab] = group;
            _contentScroll = 0;
            Invalidate();
        }

        private void SetNativeMode(bool original)
        {
            _nativeError = string.Empty;
            _contentScroll = 0;
            if (original)
            {
                _showOriginalProvider.Add(_tab);
                ScheduleProviderLoad(_tab);
            }
            else
            {
                _showOriginalProvider.Remove(_tab);
                HideProviderSurface();
            }
            Invalidate();
        }

        // Stile predefinito di XySubFilter (Text\style2), stesso formato di STSStyle:
        // margini, allineamento, bordo, ombra, 4 colori BGR, 4 alfa, charset, font, dimensione…
        private sealed class XySubStyle
        {
            private const string KeyPath = @"Software\Gabest\XySubFilter\Text";
            private const string ValueName = "style2";
            private const string Defaults = "20;20;20;20;2;0;2.000000;2.000000;3.000000;3.000000;0xffffff;0x00ffff;0x000000;0x000000;0x00;0x00;0x00;0x80;1;Arial;18.000000;100.000000;100.000000;0.000000;700;0;0;0;0.000000;0.000000;0.000000;0.000000;0.000000;2";
            private readonly string[] _fields;

            private XySubStyle(string[] fields) => _fields = fields;

            public static XySubStyle Read()
            {
                string? stored = null;
                try { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); stored = key?.GetValue(ValueName) as string; } catch { }
                string[] fields = (stored ?? Defaults).Split(';');
                return new XySubStyle(fields.Length == 34 ? fields : Defaults.Split(';'));
            }

            public string? Get(string field)
            {
                switch (field)
                {
                    case "font": return _fields[19];
                    case "size": return double.TryParse(_fields[20], NumberStyles.Float, CultureInfo.InvariantCulture, out double size) ? Math.Round(size).ToString(CultureInfo.InvariantCulture) : null;
                    case "bold": return int.TryParse(_fields[24], out int weight) ? (weight >= 600 ? "1" : "0") : null;
                    case "outline": return Round(_fields[6]);
                    case "shadow": return Round(_fields[8]);
                    case "margin": return _fields[3];
                    case "color":
                        return int.TryParse(_fields[10].Replace("0x", string.Empty), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int bgr)
                            ? $"{bgr & 0xFF:x2}{(bgr >> 8) & 0xFF:x2}{(bgr >> 16) & 0xFF:x2}" : null;
                    default: return null;
                }
            }

            public void Set(string field, string value)
            {
                string F(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
                switch (field)
                {
                    case "font": _fields[19] = value; break;
                    case "size": _fields[20] = F(double.Parse(value, CultureInfo.InvariantCulture)); break;
                    case "bold": _fields[24] = value == "1" ? "700" : "400"; break;
                    case "outline": _fields[6] = _fields[7] = F(double.Parse(value, CultureInfo.InvariantCulture)); break;
                    case "shadow": _fields[8] = _fields[9] = F(double.Parse(value, CultureInfo.InvariantCulture)); break;
                    case "margin": _fields[3] = value; break;
                    case "color":
                        int rgb = int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        int bgr = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
                        _fields[10] = "0x" + bgr.ToString("x6", CultureInfo.InvariantCulture);
                        break;
                }
            }

            public bool Write()
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue(ValueName, string.Join(";", _fields), RegistryValueKind.String);
                return true;
            }

            private static string? Round(string text)
                => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? Math.Round(v).ToString(CultureInfo.InvariantCulture) : null;
        }
    }
}
