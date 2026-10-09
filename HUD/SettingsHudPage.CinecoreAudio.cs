#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CinecorePlayer2025.Audio;

namespace CinecorePlayer2025.HUD
{
    // Pagina "Cinecore Audio": motore musicale, equalizzatore (grafico 10 o 31 bande, oppure
    // parametrico) e DSP. Usa le stesse righe delle pagine native (quindi compare anche nel
    // telecomando) piu' un editor della curva con i nodi trascinabili.
    internal sealed partial class SettingsHudPage
    {
        private const string CinecoreAudioTab = "Cinecore Audio";

        private static readonly (string Key, string It, string En, double[] Gains)[] EqPresets =
        {
            ("flat", "Piatto", "Flat", new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            ("bass", "Bassi", "Bass", new double[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 }),
            ("warm", "Caldo", "Warm", new double[] { 3, 3, 2, 1, 0, -1, -1, -1, -2, -2 }),
            ("bright", "Brillante", "Bright", new double[] { 0, 0, 0, 0, 0, 1, 2, 3, 4, 4 }),
            ("vocal", "Voce", "Vocal", new double[] { -2, -2, -1, 1, 3, 3, 2, 1, 0, -1 }),
            ("loudness", "Loudness", "Loudness", new double[] { 5, 4, 2, 0, -1, 0, 0, 2, 4, 4 }),
            ("vshape", "A V", "V-shape", new double[] { 5, 4, 2, 0, -2, -2, 0, 2, 4, 5 }),
            ("harman", "Cuffie (Harman)", "Headphones (Harman)", new double[] { 4, 4, 2, 0, -1, 0, 1, 2, -1, -3 }),
        };

        private static readonly EqBandType[] ParametricTypes = { EqBandType.Peak, EqBandType.LowShelf, EqBandType.HighShelf, EqBandType.LowPass, EqBandType.HighPass };
        private static readonly double[] QSteps = { 0.3, 0.5, 0.71, 1.0, 1.41, 2.0, 3.0, 4.32, 6.0, 10.0 };

        private static string FrequencyLabel(double hz) => hz >= 1000
            ? (hz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " kHz"
            : hz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz";

        private static string TypeLabel(EqBandType type, bool english) => type switch
        {
            EqBandType.LowShelf => english ? "Low shelf" : "Shelf bassi",
            EqBandType.HighShelf => english ? "High shelf" : "Shelf alti",
            EqBandType.LowPass => english ? "Low-pass" : "Passa-basso",
            EqBandType.HighPass => english ? "High-pass" : "Passa-alto",
            _ => english ? "Bell" : "Campana"
        };

        private static EqBandType ParseType(string type) => Enum.TryParse<EqBandType>(type, out var t) ? t : EqBandType.Peak;
        private static bool IsPass(string type) => type is nameof(EqBandType.LowPass) or nameof(EqBandType.HighPass);

        /// <summary>Preset a 10 bande portato sulle 31 bande (interpolazione in scala logaritmica).</summary>
        private static List<double> Interpolate31(double[] g10)
        {
            var f10 = AudioDspSettings.GraphicFrequencies;
            return AudioDspSettings.ThirdOctaveFrequencies.Select(f =>
            {
                if (f <= f10[0]) return g10[0];
                if (f >= f10[^1]) return g10[^1];
                int i = Array.FindLastIndex(f10, x => x <= f);
                double t = Math.Log(f / f10[i]) / Math.Log(f10[i + 1] / f10[i]);
                return Math.Round(g10[i] + (g10[i + 1] - g10[i]) * t, 1);
            }).ToList();
        }

        private static NativeChoice[] Db(int from, int to, double step = 1)
        {
            var list = new List<NativeChoice>();
            for (double v = from; v <= to + 1e-9; v += step)
            {
                string value = v.ToString("0.#", CultureInfo.InvariantCulture);
                string label = (v > 0 ? "+" : "") + v.ToString("0.#", CultureInfo.CurrentCulture) + " dB";
                list.Add(C(value, label));
            }
            return list.ToArray();
        }

        // Riga azione: Default = etichetta IT del pulsante, DescEn = etichetta EN.
        private static NativeSetting ActionRow(string key, string it, string en, string actionIt, string actionEn)
            => new(key, it, en, NativeKind.Action, actionIt, "", actionEn);

        private static NativeSection[] CinecoreAudioSections()
        {
            var s = CinecoreAudioSettings.Current;
            var sections = new List<NativeSection>
            {
                new("Motore", "Engine", new[]
                {
                    new NativeSetting("ca:enabled", "Cinecore Audio Engine per la musica", "Cinecore Audio Engine for music", NativeKind.Toggle, "1",
                        "Decodifica, EQ, DSP e protezione dal clipping del player. Spento: si usa mpv.", "The player's own decoding, EQ, DSP and clip protection. Off: mpv is used."),
                    new NativeSetting("ca:exclusive", "WASAPI esclusivo", "Exclusive WASAPI", NativeKind.Toggle, "0",
                        "Alla frequenza del file, senza il mixer di Windows. Vale dal prossimo brano.", "At the file's sample rate, bypassing the Windows mixer. From the next track."),
                    new NativeSetting("ca:bitperfect", "Bit perfect", "Bit perfect", NativeKind.Toggle, "0",
                        "I campioni del file arrivano al dispositivo identici: uscita esclusiva, niente EQ, ReplayGain, DSP, limiter o dissolvenza. Regola il volume dall'amplificatore: sotto il 100% non è più bit perfect. Vale dal prossimo brano.",
                        "The file's samples reach the device unchanged: exclusive output, no EQ, ReplayGain, DSP, limiter or crossfade. Set the volume on the amplifier: below 100% it is no longer bit perfect. From the next track."),
                    new NativeSetting("ca:crossfade", "Dissolvenza tra i brani", "Crossfade between tracks", NativeKind.Choice, "0",
                        "Il brano che finisce sfuma mentre entra il successivo. Non tra brani consecutivi dello stesso album, né con l'uscita esclusiva.",
                        "The ending track fades out while the next one comes in. Not between consecutive tracks of the same album, nor with exclusive output.",
                        new[] { C("0", "Spenta", "Off"), C("3", "3 s"), C("6", "6 s"), C("10", "10 s") }),
                }, "Motore", "Engine"),
                new("Protezione dal clipping", "Clip protection", new[]
                {
                    new NativeSetting("ca:protection", "Protezione", "Protection", NativeKind.Choice, nameof(ClipProtection.Limiter),
                        "Il limiter true-peak interviene solo sui picchi; l'headroom abbassa il livello quanto il boost di EQ e DSP.", "The true-peak limiter only touches peaks; headroom lowers the level by the EQ and DSP boost.",
                        new[] { C(nameof(ClipProtection.Limiter), "Limiter true-peak + headroom", "True-peak limiter + headroom"), C(nameof(ClipProtection.Headroom), "Solo headroom automatico", "Automatic headroom only"), C(nameof(ClipProtection.Off), "Disattivata", "Off") }),
                    new NativeSetting("ca:ceiling", "Soffitto", "Ceiling", NativeKind.Choice, "-1",
                        "Livello massimo in uscita, misurato fra un campione e l'altro (dBTP).", "Maximum output level, measured between samples (dBTP).",
                        new[] { C("-0.1", "-0,1 dBTP", "-0.1 dBTP"), C("-0.3", "-0,3 dBTP", "-0.3 dBTP"), C("-0.5", "-0,5 dBTP", "-0.5 dBTP"), C("-1", "-1,0 dBTP", "-1.0 dBTP"), C("-2", "-2,0 dBTP", "-2.0 dBTP") }),
                }, "Motore", "Engine"),
            };

            var eq = new List<NativeSetting>
            {
                new("ca:eq", "Equalizzatore", "Equalizer", NativeKind.Toggle, "0"),
                new("ca:mode", "Tipo", "Type", NativeKind.Choice, "graphic10",
                    "Grafico: bande fisse, trascina i punti. Parametrico: frequenza, guadagno, Q e forma liberi.", "Graphic: fixed bands, drag the points. Parametric: free frequency, gain, Q and shape.",
                    new[] { C("graphic10", "Grafico · 10 bande", "Graphic · 10 bands"), C("graphic31", "Grafico · 31 bande", "Graphic · 31 bands"), C("parametric", "Parametrico", "Parametric") }),
            };
            if (s.Mode != EqMode.Parametric)
                eq.Add(new("ca:preset", "Preset", "Preset", NativeKind.Choice, "flat", "", "",
                    EqPresets.Select(p => C(p.Key, p.It, p.En)).Append(C("custom", "Personalizzato", "Custom")).ToArray()));
            eq.Add(new("ca:preamp", "Preamplificazione", "Preamp", NativeKind.Choice, "0",
                "In aggiunta all'headroom automatico.", "On top of the automatic headroom.", Db(-12, 6)));
            sections.Add(new("Equalizzatore", "Equalizer", eq.ToArray(), "Equalizzatore", "Equalizer"));

            switch (s.Mode)
            {
                case EqMode.Graphic10:
                    sections.Add(new("Bande", "Bands", AudioDspSettings.GraphicFrequencies.Select((f, i) =>
                        new NativeSetting("ca:band" + i, FrequencyLabel(f), FrequencyLabel(f), NativeKind.Choice, "0", "", "", Db(-12, 12, 0.5))).ToArray(),
                        "Equalizzatore", "Equalizer"));
                    break;
                case EqMode.Graphic31:
                    sections.Add(new("Bande", "Bands", new[]
                    {
                        ActionRow("ca:reset", "31 bande a terzi d'ottava: trascina i punti sul grafico", "31 third-octave bands: drag the points on the graph", "Azzera tutte", "Reset all"),
                    }, "Equalizzatore", "Equalizer"));
                    break;
                default:
                {
                    int count = s.Parametric.Count;
                    int sel = Math.Clamp(s.SelectedBand, 0, Math.Max(0, count - 1));
                    var bandChoices = s.Parametric.Select((b, i) => C(i.ToString(CultureInfo.InvariantCulture),
                        $"{i + 1} · {TypeLabel(ParseType(b.Type), false)} {FrequencyLabel(b.Frequency)}",
                        $"{i + 1} · {TypeLabel(ParseType(b.Type), true)} {FrequencyLabel(b.Frequency)}")).ToArray();
                    var freqChoices = AudioDspSettings.ThirdOctaveFrequencies.Select(f => C(f.ToString(CultureInfo.InvariantCulture), FrequencyLabel(f))).ToArray();
                    var qChoices = QSteps.Select(q => C(q.ToString(CultureInfo.InvariantCulture), "Q " + q.ToString("0.##", CultureInfo.GetCultureInfo("it-IT")), "Q " + q.ToString("0.##", CultureInfo.InvariantCulture))).ToArray();
                    var rows = new List<NativeSetting>
                    {
                        new("ca:pb:select", "Banda", "Band", NativeKind.Choice, "0",
                            "Clic su un punto per sceglierlo · trascina per frequenza e guadagno · rotella per il Q · doppio clic per aggiungere.",
                            "Click a point to pick it · drag for frequency and gain · wheel for Q · double-click to add.", bandChoices),
                        new("ca:pb:type", "Forma", "Shape", NativeKind.Choice, nameof(EqBandType.Peak), "", "",
                            ParametricTypes.Select(t => C(t.ToString(), TypeLabel(t, false), TypeLabel(t, true))).ToArray()),
                        new("ca:pb:freq", "Frequenza", "Frequency", NativeKind.Choice, "1000", "", "", freqChoices),
                        new("ca:pb:gain", "Guadagno", "Gain", NativeKind.Choice, "0", "", "", Db(-15, 15, 0.5)),
                        new("ca:pb:q", "Larghezza (Q)", "Width (Q)", NativeKind.Choice, "1", "", "", qChoices),
                        new("ca:pb:on", "Banda attiva", "Band enabled", NativeKind.Toggle, "1"),
                    };
                    if (count < 10) rows.Add(ActionRow("ca:pb:add", $"{count} bande su 10", $"{count} of 10 bands", "Aggiungi banda", "Add band"));
                    if (count > 1) rows.Add(ActionRow("ca:pb:remove", $"Banda {sel + 1}", $"Band {sel + 1}", "Rimuovi banda", "Remove band"));
                    rows.Add(ActionRow("ca:reset", "Guadagni", "Gains", "Azzera tutte", "Reset all"));
                    sections.Add(new($"Banda {sel + 1}", $"Band {sel + 1}", rows.ToArray(), "Equalizzatore", "Equalizer"));
                    break;
                }
            }

            sections.Add(new("Cuffie e immagine stereo", "Headphones and stereo image", new[]
            {
                new NativeSetting("ca:crossfeed", "Crossfeed", "Crossfeed", NativeKind.Choice, nameof(CrossfeedLevel.Off),
                    "In cuffia porta un po' di bassi nell'orecchio opposto, come con le casse: meno fatica con i mix molto separati.",
                    "On headphones, blends some bass into the opposite ear as speakers do: less fatigue with hard-panned mixes.",
                    new[] { C(nameof(CrossfeedLevel.Off), "Disattivato", "Off"), C(nameof(CrossfeedLevel.Low), "Leggero", "Light"), C(nameof(CrossfeedLevel.Medium), "Medio", "Medium"), C(nameof(CrossfeedLevel.High), "Forte", "Strong") }),
                new NativeSetting("ca:width", "Ampiezza stereo", "Stereo width", NativeKind.Choice, "100",
                    "Allarga o restringe il palco (mid/side). 100% = originale.", "Widens or narrows the stage (mid/side). 100% = original.",
                    new[] { 0, 25, 50, 75, 100, 115, 130, 150, 200 }.Select(w => C(w.ToString(CultureInfo.InvariantCulture), w + "%")).ToArray()),
                new NativeSetting("ca:balance", "Bilanciamento", "Balance", NativeKind.Choice, "0", "", "",
                    Enumerable.Range(-10, 21).Select(v => C((v * 10).ToString(CultureInfo.InvariantCulture),
                        v == 0 ? "Centro" : v < 0 ? $"S {-v * 10}%" : $"D {v * 10}%",
                        v == 0 ? "Center" : v < 0 ? $"L {-v * 10}%" : $"R {v * 10}%")).ToArray()),
                new NativeSetting("ca:mono", "Mono", "Mono", NativeKind.Toggle, "0",
                    "Somma i due canali: utile con un solo auricolare o per verificare la compatibilità mono.", "Sums both channels: useful with a single earbud or to check mono compatibility."),
            }, "DSP", "DSP"));
            sections.Add(new("Livello", "Level", new[]
            {
                new NativeSetting("ca:loudness", "Loudness", "Loudness", NativeKind.Toggle, "0",
                    "A volume basso rinforza bassi e alti che l'orecchio percepisce meno (curve isofoniche). Non può saturare.",
                    "At low volume, lifts the bass and treble the ear perceives less (equal-loudness curves). It cannot clip."),
                new NativeSetting("ca:rg", "ReplayGain", "ReplayGain", NativeKind.Choice, "off",
                    "Uniforma il volume fra brani e album usando i tag del file.", "Evens out loudness across tracks and albums using the file's tags.",
                    new[] { C("off", "Disattivato", "Off"), C("track", "Per brano", "Per track"), C("album", "Per album", "Per album") }),
                new NativeSetting("ca:rgpre", "Preamp ReplayGain", "ReplayGain preamp", NativeKind.Choice, "0", "", "", Db(-6, 6)),
            }, "DSP", "DSP"));
            return sections.ToArray();
        }

        private static CinecoreAudioSettings.BandDto? SelectedParametric(CinecoreAudioSettings s)
            => s.Parametric.Count == 0 ? null : s.Parametric[Math.Clamp(s.SelectedBand, 0, s.Parametric.Count - 1)];

        private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string CinecoreAudioGet(NativeSetting setting)
        {
            var c = CinecoreAudioSettings.Current;
            var pb = SelectedParametric(c);
            switch (setting.Key)
            {
                case "ca:enabled": return c.Enabled ? "1" : "0";
                case "ca:exclusive": return c.Exclusive ? "1" : "0";
                case "ca:bitperfect": return c.BitPerfect ? "1" : "0";
                case "ca:crossfade": return c.CrossfadeSeconds.ToString(CultureInfo.InvariantCulture);
                case "ca:protection": return c.Protection;
                case "ca:ceiling": return c.CeilingDbTp.ToString("0.#", CultureInfo.InvariantCulture);
                case "ca:eq": return c.EqEnabled ? "1" : "0";
                case "ca:mode": return c.EqMode;
                case "ca:preset": return c.Preset;
                case "ca:preamp": return Num(Math.Round(c.PreampDb));
                case "ca:pb:select": return Math.Clamp(c.SelectedBand, 0, Math.Max(0, c.Parametric.Count - 1)).ToString(CultureInfo.InvariantCulture);
                case "ca:pb:type": return pb?.Type ?? setting.Default;
                case "ca:pb:freq": return pb == null ? setting.Default : Num(AudioDspSettings.ThirdOctaveFrequencies.OrderBy(f => Math.Abs(Math.Log(f / Math.Max(1, pb.Frequency)))).First());
                case "ca:pb:gain": return pb == null ? "0" : Num(Math.Round(pb.Gain * 2) / 2);
                case "ca:pb:q": return pb == null ? "1" : Num(QSteps.OrderBy(q => Math.Abs(Math.Log(q / Math.Max(0.1, pb.Q)))).First());
                case "ca:pb:on": return pb?.Enabled == false ? "0" : "1";
                case "ca:crossfeed": return c.Crossfeed;
                case "ca:width": return Num(c.Width);
                case "ca:balance": return Num(Math.Round(c.Balance / 10) * 10);
                case "ca:mono": return c.Mono ? "1" : "0";
                case "ca:loudness": return c.Loudness ? "1" : "0";
                case "ca:rg": return c.ReplayGain;
                case "ca:rgpre": return Num(Math.Round(c.ReplayGainPreampDb));
            }
            if (setting.Key.StartsWith("ca:band", StringComparison.Ordinal) && int.TryParse(setting.Key[7..], out int i) && i < c.Bands.Count)
                return Num(Math.Round(c.Bands[i].Gain * 2) / 2);
            return setting.Default;
        }

        private static bool CinecoreAudioSet(NativeSetting setting, string value)
        {
            var c = CinecoreAudioSettings.Current;
            double number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
            var pb = SelectedParametric(c);
            switch (setting.Key)
            {
                case "ca:enabled": c.Enabled = value == "1"; break;
                case "ca:exclusive": c.Exclusive = value == "1"; break;
                case "ca:bitperfect": c.BitPerfect = value == "1"; break;
                case "ca:crossfade": c.CrossfadeSeconds = number is 3 or 6 or 10 ? (int)number : 0; break;
                case "ca:protection": if (!Enum.TryParse<ClipProtection>(value, out _)) return false; c.Protection = value; break;
                case "ca:ceiling": c.CeilingDbTp = Math.Clamp(number, -6, 0); break;
                case "ca:eq": c.EqEnabled = value == "1"; break;
                case "ca:mode": if (value is not ("graphic10" or "graphic31" or "parametric")) return false; c.EqMode = value; c.EqEnabled = true; break;
                case "ca:preamp": c.PreampDb = Math.Clamp(number, -12, 6); break;
                case "ca:preset":
                    var preset = EqPresets.FirstOrDefault(p => p.Key == value);
                    if (preset.Gains != null)
                    {
                        for (int i = 0; i < c.Bands.Count && i < preset.Gains.Length; i++) c.Bands[i].Gain = preset.Gains[i];
                        c.Graphic31 = Interpolate31(preset.Gains);
                        c.EqEnabled = true;
                    }
                    c.Preset = value;
                    break;
                case "ca:pb:select": c.SelectedBand = Math.Clamp((int)number, 0, Math.Max(0, c.Parametric.Count - 1)); break;
                case "ca:pb:type": if (pb == null || !Enum.TryParse<EqBandType>(value, out _)) return false; pb.Type = value; c.EqEnabled = true; break;
                case "ca:pb:freq": if (pb == null) return false; pb.Frequency = Math.Clamp(number, 20, 20000); c.EqEnabled = true; break;
                case "ca:pb:gain": if (pb == null) return false; pb.Gain = Math.Clamp(number, -15, 15); c.EqEnabled = true; break;
                case "ca:pb:q": if (pb == null) return false; pb.Q = Math.Clamp(number, 0.1, 24); c.EqEnabled = true; break;
                case "ca:pb:on": if (pb == null) return false; pb.Enabled = value == "1"; break;
                case "ca:crossfeed": if (!Enum.TryParse<CrossfeedLevel>(value, out _)) return false; c.Crossfeed = value; break;
                case "ca:width": c.Width = Math.Clamp(number, 0, 200); break;
                case "ca:balance": c.Balance = Math.Clamp(number, -100, 100); break;
                case "ca:mono": c.Mono = value == "1"; break;
                case "ca:loudness": c.Loudness = value == "1"; break;
                case "ca:rg": if (value is not ("off" or "track" or "album")) return false; c.ReplayGain = value; break;
                case "ca:rgpre": c.ReplayGainPreampDb = Math.Clamp(number, -12, 12); break;
                default:
                    if (setting.Key.StartsWith("ca:band", StringComparison.Ordinal) && int.TryParse(setting.Key[7..], out int band) && band < c.Bands.Count)
                    {
                        c.Bands[band].Gain = Math.Clamp(number, -12, 12);
                        c.Preset = "custom"; c.EqEnabled = true;
                        break;
                    }
                    return false;
            }
            CinecoreAudioSettings.Save();
            return true;
        }

        /// <summary>Azioni della pagina (anche dal telecomando): azzera, aggiungi, rimuovi banda.</summary>
        internal static bool CinecoreAudioAction(string key)
        {
            var c = CinecoreAudioSettings.Current;
            switch (key)
            {
                case "ca:reset":
                    if (c.Mode == EqMode.Graphic31) c.Graphic31 = new double[31].ToList();
                    else if (c.Mode == EqMode.Parametric) foreach (var b in c.Parametric) b.Gain = 0;
                    else foreach (var b in c.Bands) b.Gain = 0;
                    c.Preset = "flat";
                    break;
                case "ca:pb:add":
                    if (c.Parametric.Count >= 10) return false;
                    c.Parametric.Add(new CinecoreAudioSettings.BandDto { Type = nameof(EqBandType.Peak), Frequency = 1000, Gain = 0, Q = 1.0 });
                    c.SelectedBand = c.Parametric.Count - 1; c.EqEnabled = true;
                    break;
                case "ca:pb:remove":
                    if (c.Parametric.Count <= 1) return false;
                    c.Parametric.RemoveAt(Math.Clamp(c.SelectedBand, 0, c.Parametric.Count - 1));
                    c.SelectedBand = Math.Clamp(c.SelectedBand, 0, c.Parametric.Count - 1);
                    break;
                default: return false;
            }
            CinecoreAudioSettings.Save();
            return true;
        }

        private void RunNativeAction(string key)
        {
            if (CinecoreAudioAction(key)) Invalidate();
        }

        // ---- editor della curva ----
        // Il grafico e' disegnato nel contenuto scorrevole: senza questi flag TextRenderer ignora
        // la traslazione e le etichette restavano ferme sopra le righe durante lo scorrimento.
        private const TextFormatFlags Scrolled = TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping;
        private int _eqDragBand = -1;
        private Rectangle _eqPlot;
        private string _eqCurveKey = "";
        private (double Hz, double Db)[] _eqCurve = Array.Empty<(double, double)>();
        private double _eqMaxBoost;

        private static float EqX(Rectangle plot, double hz) => plot.Left + (float)(Math.Log10(Math.Clamp(hz, 20, 20000) / 20) / 3 * plot.Width);
        private static float EqY(Rectangle plot, double db) => plot.Top + (float)((15 - Math.Clamp(db, -17, 17)) / 30 * plot.Height);
        private static double EqHz(Rectangle plot, double x) => 20 * Math.Pow(1000, Math.Clamp((x - plot.Left) / plot.Width, 0, 1));
        private static double EqDb(Rectangle plot, double y) => 15 - (y - plot.Top) / plot.Height * 30;

        /// <summary>Nodi dell'editor: frequenza, guadagno mostrato e se la banda e' attiva.</summary>
        private static List<(double Hz, double Db, bool On)> EqNodes(CinecoreAudioSettings s) => s.Mode switch
        {
            EqMode.Graphic31 => AudioDspSettings.ThirdOctaveFrequencies.Select((f, i) => (f, i < s.Graphic31.Count ? s.Graphic31[i] : 0, true)).ToList(),
            EqMode.Parametric => s.Parametric.Select(b => (b.Frequency, IsPass(b.Type) ? 0 : b.Gain, b.Enabled)).ToList(),
            _ => s.Bands.Select(b => (b.Frequency, b.Gain, true)).ToList(),
        };

        /// <summary>Curva e headroom in cache: si ricalcolano solo quando cambiano le impostazioni
        /// (prima si ricalcolava a ogni frame di scorrimento).</summary>
        private void EnsureEqCurve(CinecoreAudioSettings s)
        {
            var dsp = s.ToDsp();
            string key = string.Join("|", s.EqMode, s.EqEnabled, s.PreampDb, s.Protection, s.Width, s.ReplayGain,
                string.Join(";", dsp.Bands.Select(b => $"{(int)b.Type},{b.FrequencyHz:0.##},{b.GainDb:0.##},{b.Q:0.###},{b.Enabled}")));
            if (key == _eqCurveKey) return;
            _eqCurveKey = key;
            var bands = dsp.EffectiveBands(48000).Where(b => b.Enabled).ToList();
            _eqCurve = new (double, double)[181];
            for (int i = 0; i <= 180; i++)
            {
                double hz = 20 * Math.Pow(1000, i / 180.0), sum = 0;
                foreach (var b in bands) sum += Biquad.ResponseDb(b, 48000, hz);
                _eqCurve[i] = (hz, sum);
            }
            _eqMaxBoost = AudioDspChain.MaxBoostDb(dsp with { EqEnabled = true }, 48000);
        }

        /// <summary>Disegna il grafico e restituisce l'altezza usata.</summary>
        private int DrawEqCurve(Graphics g, Rectangle area)
        {
            var settings = CinecoreAudioSettings.Current;
            EnsureEqCurve(settings);
            var plot = new Rectangle(area.Left + 44, area.Top + 16, area.Width - 60, 220);
            _eqPlot = plot;
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var small = UiFont("Segoe UI", 8.2f);

            using (var grid = new Pen(Color.FromArgb(Theme.IsLight ? 34 : 26, TextColor)))
            using (var zero = new Pen(Color.FromArgb(Theme.IsLight ? 80 : 60, TextColor)))
            {
                foreach (int db in new[] { 12, 6, 0, -6, -12 })
                {
                    float y = EqY(plot, db);
                    g.DrawLine(db == 0 ? zero : grid, plot.Left, y, plot.Right, y);
                    TextRenderer.DrawText(g, (db > 0 ? "+" : "") + db, small, new Rectangle(area.Left, (int)y - 9, 38, 18), Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Scrolled);
                }
                foreach (double hz in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
                {
                    float x = EqX(plot, hz);
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    TextRenderer.DrawText(g, FrequencyLabel(hz), small, new Rectangle((int)x - 30, plot.Bottom + 4, 60, 16), Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | Scrolled);
                }
            }

            bool active = settings.EqEnabled;
            Color curveColor = active ? Accent : Color.FromArgb(120, Muted);
            var points = _eqCurve.Select(p => new PointF(EqX(plot, p.Hz), EqY(plot, p.Db))).ToArray();
            using (var fillPath = new GraphicsPath())
            {
                float zeroY = EqY(plot, 0);
                fillPath.AddLine(points[0].X, zeroY, points[0].X, points[0].Y);
                fillPath.AddLines(points);
                fillPath.AddLine(points[^1].X, points[^1].Y, points[^1].X, zeroY);
                fillPath.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(active ? 40 : 16, curveColor));
                g.FillPath(fill, fillPath);
            }
            using (var curve = new Pen(curveColor, 2.4f) { LineJoin = LineJoin.Round })
                g.DrawLines(curve, points);

            var nodes = EqNodes(settings);
            bool parametric = settings.Mode == EqMode.Parametric;
            bool dense = settings.Mode == EqMode.Graphic31;
            int nodeSize = dense ? 9 : 14;
            int selected = parametric ? Math.Clamp(settings.SelectedBand, 0, Math.Max(0, nodes.Count - 1)) : -1;
            var mouse = new Point(_lastMouse.X, _lastMouse.Y);
            int hotIndex = -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                var (hz, db, on) = nodes[i];
                float x = EqX(plot, hz), y = EqY(plot, db);
                var node = new RectangleF(x - nodeSize / 2f, y - nodeSize / 2f, nodeSize, nodeSize);
                var hit = parametric
                    ? Rectangle.Round(RectangleF.Inflate(node, 8, 8))
                    : new Rectangle((int)(x - (dense ? 5 : 13)), plot.Top - 6, dense ? 10 : 26, plot.Height + 12);
                bool hot = _eqDragBand == i || (_eqDragBand < 0 && hit.Contains(mouse));
                if (hot) hotIndex = i;
                bool sel = i == selected;
                Color fillColor = !on ? Color.FromArgb(90, Muted) : hot || sel ? Color.White : curveColor;
                using (var dot = new SolidBrush(fillColor)) g.FillEllipse(dot, node);
                using (var ring = new Pen(Color.FromArgb(active && on ? 255 : 120, Accent), sel ? 3f : dense ? 1.6f : 2f)) g.DrawEllipse(ring, node);
                if (parametric)
                {
                    using var num = UiFont("Segoe UI Semibold", 7.4f);
                    TextRenderer.DrawText(g, (i + 1).ToString(CultureInfo.InvariantCulture), num, new Rectangle((int)x - 10, (int)node.Bottom + 3, 20, 14), Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | Scrolled);
                }
                _hits.Add(new Hit { Bounds = hit, Kind = HitKind.NativeMode, Key = "eqnode:" + i });
            }
            if (hotIndex >= 0)
            {
                var (hz, db, _) = nodes[hotIndex];
                float x = EqX(plot, hz), y = EqY(plot, db);
                string label = $"{FrequencyLabel(hz)}  {(db > 0 ? "+" : "")}{db.ToString("0.#", CultureInfo.InvariantCulture)} dB";
                if (parametric) label += $"  Q {settings.Parametric[hotIndex].Q.ToString("0.##", CultureInfo.InvariantCulture)}";
                int top = y - nodeSize / 2f - 24 < plot.Top ? (int)(y + nodeSize / 2f + 18) : (int)(y - nodeSize / 2f - 22);
                TextRenderer.DrawText(g, label, small, new Rectangle((int)x - 100, top, 200, 18), TextColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | Scrolled);
            }
            g.Restore(state);

            double boost = _eqMaxBoost;
            string note = !active ? L("Equalizzatore spento: la curva è solo un'anteprima.", "Equalizer off: the curve is only a preview.")
                : settings.ToDsp().Protection == ClipProtection.Off ? L($"Boost massimo +{boost:0.0} dB · protezione disattivata: possibile clipping.", $"Max boost +{boost:0.0} dB · protection off: clipping possible.")
                : boost > 0.05 ? L($"Headroom automatico −{boost:0.0} dB: nessun campione oltre il soffitto.", $"Automatic headroom −{boost:0.0} dB: no sample above the ceiling.")
                : L("Nessun boost: il livello resta invariato.", "No boost: level unchanged.");
            if (parametric) note += L("   ·   Doppio clic per aggiungere una banda, rotella per il Q.", "   ·   Double-click to add a band, wheel for Q.");
            TextRenderer.DrawText(g, note, small, new Rectangle(plot.Left, plot.Bottom + 24, plot.Width, 18), Muted, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | Scrolled);
            return plot.Bottom + 48 - area.Top;
        }

        private bool BeginEqDrag(string key, Point location)
        {
            if (!key.StartsWith("eqnode:", StringComparison.Ordinal) || !int.TryParse(key[7..], out int band)) return false;
            var s = CinecoreAudioSettings.Current;
            if (s.Mode == EqMode.Parametric && band < s.Parametric.Count) s.SelectedBand = band;
            _eqDragBand = band;
            Capture = true;
            // Nel grafico a bande fisse il clic porta subito il punto li'; nel parametrico no,
            // altrimenti selezionare un nodo lo sposterebbe.
            if (s.Mode != EqMode.Parametric) UpdateEqDrag(location);
            Invalidate();
            return true;
        }

        private bool UpdateEqDrag(Point location)
        {
            if (_eqDragBand < 0 || _eqPlot.IsEmpty) return false;
            // Il grafico e' disegnato nel contenuto scorrevole: riporta il punto nello stesso spazio.
            double y = location.Y + _contentScroll;
            var c = CinecoreAudioSettings.Current;
            double db = Math.Round(EqDb(_eqPlot, y) * 2) / 2;
            bool changed = false;
            switch (c.Mode)
            {
                case EqMode.Graphic31:
                    db = Math.Clamp(db, -12, 12);
                    if (_eqDragBand < c.Graphic31.Count && Math.Abs(c.Graphic31[_eqDragBand] - db) > 0.01) { c.Graphic31[_eqDragBand] = db; changed = true; }
                    break;
                case EqMode.Parametric:
                    if (_eqDragBand < c.Parametric.Count)
                    {
                        var b = c.Parametric[_eqDragBand];
                        double hz = EqHz(_eqPlot, location.X);
                        double rounded = hz < 100 ? Math.Round(hz) : hz < 1000 ? Math.Round(hz / 5) * 5 : Math.Round(hz / 50) * 50;
                        rounded = Math.Clamp(rounded, 20, 20000);
                        if (Math.Abs(b.Frequency - rounded) > 0.5) { b.Frequency = rounded; changed = true; }
                        if (!IsPass(b.Type))
                        {
                            db = Math.Clamp(db, -15, 15);
                            if (Math.Abs(b.Gain - db) > 0.01) { b.Gain = db; changed = true; }
                        }
                    }
                    break;
                default:
                    db = Math.Clamp(db, -12, 12);
                    if (_eqDragBand < c.Bands.Count && Math.Abs(c.Bands[_eqDragBand].Gain - db) > 0.01) { c.Bands[_eqDragBand].Gain = db; changed = true; }
                    break;
            }
            if (changed)
            {
                if (c.Mode != EqMode.Parametric) c.Preset = "custom";
                c.EqEnabled = true;
                CinecoreAudioSettings.Touch(); // il motore applica subito; il file si scrive al rilascio
                Invalidate();
            }
            return true;
        }

        private bool EndEqDrag()
        {
            if (_eqDragBand < 0) return false;
            _eqDragBand = -1;
            Capture = false;
            CinecoreAudioSettings.Save();
            Invalidate();
            return true;
        }

        /// <summary>Rotella sul grafico del parametrico: regola il Q del nodo piu' vicino.</summary>
        private bool EqWheel(Point location, int delta)
        {
            var c = CinecoreAudioSettings.Current;
            if (!string.Equals(_tab, CinecoreAudioTab, StringComparison.OrdinalIgnoreCase) || c.Mode != EqMode.Parametric || _eqPlot.IsEmpty || c.Parametric.Count == 0) return false;
            var p = new Point(location.X, location.Y + _contentScroll);
            if (!_eqPlot.Contains(p)) return false;
            int index = -1; double best = 22;
            for (int i = 0; i < c.Parametric.Count; i++)
            {
                var b = c.Parametric[i];
                double dx = EqX(_eqPlot, b.Frequency) - p.X, dy = EqY(_eqPlot, IsPass(b.Type) ? 0 : b.Gain) - p.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best) { best = d; index = i; }
            }
            if (index < 0) return false; // lontano dai nodi: la rotella scorre la pagina
            var band = c.Parametric[index];
            band.Q = Math.Clamp(Math.Round(band.Q * (delta > 0 ? 1.15 : 1 / 1.15), 2), 0.1, 24);
            c.SelectedBand = index; c.EqEnabled = true;
            CinecoreAudioSettings.Save();
            Invalidate();
            return true;
        }

        /// <summary>Doppio clic su un punto vuoto del grafico parametrico: nuova banda li'.</summary>
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var c = CinecoreAudioSettings.Current;
            if (!string.Equals(_tab, CinecoreAudioTab, StringComparison.OrdinalIgnoreCase) || c.Mode != EqMode.Parametric || _eqPlot.IsEmpty || c.Parametric.Count >= 10) return;
            var p = new Point(e.X, e.Y + _contentScroll);
            if (!_eqPlot.Contains(p)) return;
            foreach (var b in c.Parametric)
                if (Math.Abs(EqX(_eqPlot, b.Frequency) - p.X) < 12 && Math.Abs(EqY(_eqPlot, IsPass(b.Type) ? 0 : b.Gain) - p.Y) < 12) return;
            double hz = EqHz(_eqPlot, p.X), db = Math.Clamp(Math.Round(EqDb(_eqPlot, p.Y) * 2) / 2, -15, 15);
            c.Parametric.Add(new CinecoreAudioSettings.BandDto { Type = nameof(EqBandType.Peak), Frequency = Math.Round(hz), Gain = db, Q = 1.0 });
            c.SelectedBand = c.Parametric.Count - 1; c.EqEnabled = true;
            CinecoreAudioSettings.Save();
            Invalidate();
        }
    }
}
