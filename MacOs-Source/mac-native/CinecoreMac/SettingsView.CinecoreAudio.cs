using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using CinecorePlayer2025.Audio;

namespace CinecoreMac;

// Impostazioni › Cinecore Audio (SettingsHudPage.CinecoreAudio di Windows): equalizzatore a 10 bande con i preset,
// protezione dalla saturazione, ReplayGain, dissolvenza e gapless, e la catena DSP (crossfeed, immagine stereo,
// loudness, AutoEQ dinamico). Ogni scelta si salva e il motore la applica subito.
internal sealed partial class SettingsView
{
    private static readonly (string Key, string Name, double[] Gains)[] EqPresets =
    {
        ("flat", "Piatto", new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        ("bass", "Bassi", new double[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 }),
        ("warm", "Caldo", new double[] { 3, 3, 2, 1, 0, -1, -1, -1, -2, -2 }),
        ("bright", "Brillante", new double[] { 0, 0, 0, 0, 0, 1, 2, 3, 4, 4 }),
        ("vocal", "Voce", new double[] { -2, -2, -1, 1, 3, 3, 2, 1, 0, -1 }),
        ("loudness", "Loudness", new double[] { 5, 4, 2, 0, -1, 0, 0, 2, 4, 4 }),
        ("vshape", "A V", new double[] { 5, 4, 2, 0, -2, -2, 0, 2, 4, 5 }),
        ("harman", "Cuffie (Harman)", new double[] { 4, 4, 2, 0, -1, 0, 1, 2, -1, -3 }),
    };

    /// <summary>Preset a 10 bande portato sulle 31 bande (interpolazione in scala logaritmica, come Windows).</summary>
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

    private static string Db(double v) => (v > 0 ? "+" : "") + v.ToString("0.#", CultureInfo.CurrentCulture) + " dB";
    private static string Hz(double hz) => hz >= 1000 ? (hz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " kHz" : hz.ToString("0", CultureInfo.InvariantCulture) + " Hz";

    private static string TypeName(string type) => type switch
    {
        "LowShelf" => "Shelf bassi", "HighShelf" => "Shelf alti", "LowPass" => "Passa-basso", "HighPass" => "Passa-alto", _ => "Campana",
    };

    private double DrawAudioRows(DrawingContext ctx, Rect body)
    {
        var s = CinecoreAudioSettings.Current;
        double width = body.Width;
        double controlW = Math.Min(330, Math.Max(210, Math.Floor(width * 2 / 5)));
        double controlX = body.X + width - controlW;
        double y = body.Y;
        double labelW = Math.Max(90, controlX - body.X - 28);
        void Section(string title)
        {
            ctx.DrawLine(new Pen(Brush(A(UiTheme.Border, 50)), 1), new Point(body.X, y + .5), new Point(body.X + width, y + .5));
            Text(ctx, title.ToUpperInvariant(), 9, true, new Rect(body.X, y + 20, width, 24), UiTheme.Muted);
            y += 56;
        }
        Rect Row(string title, string description = "")
        {
            double h = description.Length > 0 ? 66 : 56;
            Text(ctx, title, 10.5, false, new Rect(body.X, y, labelW, 30), UiTheme.Text);
            if (description.Length > 0) h += RowDescription(ctx, title, description, new Rect(body.X, y + 29, labelW, 23));
            var r = new Rect(controlX, y + 1, controlW, 36);
            y += h;
            return r;
        }
        void Choice(Rect r, string text, string key) { DrawValueChip(ctx, r, text, Hover(r)); _hits.Add(new Hit(r, "audio", key)); }
        void Toggle(Rect r, bool value, string key, string on = "Attivo", string off = "Disattivato")
        {
            DrawSwitch(ctx, r, value ? on : off, value);
            _hits.Add(new Hit(r, "audio", key));
        }

        Section("Motore");
        Toggle(Row("Cinecore Audio Engine", "Se è spento, la musica passa dal lettore di sistema (senza equalizzatore, dissolvenze e strumenti)."), s.Enabled, "engine", "Attivo", "Lettore di sistema");
        if (s.Enabled)
        {
            Toggle(Row("Uscita esclusiva", "Alla frequenza del file, con l’uscita riservata alla musica (gli altri programmi non suonano finché è in riproduzione)."), s.Exclusive, "exclusive");
            Toggle(Row("Bit perfect", "I campioni del file arrivano all’uscita identici: niente EQ, ReplayGain, DSP, limiter o dissolvenza. Regola il volume dall’amplificatore: sotto il 100% non è più bit perfect."), s.BitPerfect, "bitperfect");
        }

        Section("Equalizzatore");
        Toggle(Row("Equalizzatore", "Solo sulla musica (i film non cambiano)."), s.EqEnabled, "eq");
        Choice(Row("Tipo", "Grafico a 10 o 31 bande (terzi d’ottava), oppure parametrico con filtri a scelta."),
            s.EqMode switch { "graphic31" => "31 bande", "parametric" => "Parametrico", _ => "10 bande" }, "eqmode");
        if (s.EqMode != "parametric")
        {
            string preset = EqPresets.FirstOrDefault(p => p.Key == s.Preset).Name ?? "Personalizzato";
            Choice(Row("Preset", "Le curve pronte di Windows; cambiando una banda diventa personalizzato."), preset, "preset");
        }
        Choice(Row("Preamplificazione", "Abbassa il segnale prima dell’equalizzatore, per lasciare spazio alle frequenze alzate."), Db(s.PreampDb), "preamp");
        // Editor della curva (come Windows): i nodi si trascinano, le righe sotto restano per i valori esatti.
        y += DrawEqCurve(ctx, new Rect(body.X, y, width, 0)) + 8;
        if (s.EqMode == "graphic31")
        {
            var f31 = AudioDspSettings.ThirdOctaveFrequencies;
            for (int i = 0; i < f31.Length && i < s.Graphic31.Count; i++)
                Choice(Row(Hz(f31[i])), Db(s.Graphic31[i]), "b31:" + i.ToString(CultureInfo.InvariantCulture));
        }
        else if (s.EqMode == "parametric")
        {
            for (int i = 0; i < s.Parametric.Count; i++)
            {
                var b = s.Parametric[i];
                string k = i.ToString(CultureInfo.InvariantCulture);
                Text(ctx, $"Filtro {i + 1}", 10.5, true, new Rect(body.X, y, labelW, 30), UiTheme.Text);
                y += 34;
                Choice(Row("Tipo"), TypeName(b.Type), "ptype:" + k);
                Choice(Row("Frequenza"), Hz(b.Frequency), "pfreq:" + k);
                if (b.Type is not ("LowPass" or "HighPass")) Choice(Row("Guadagno"), Db(b.Gain), "pgain:" + k);
                Choice(Row("Q (larghezza)"), b.Q.ToString("0.##", CultureInfo.CurrentCulture), "pq:" + k);
            }
        }
        else
            for (int i = 0; i < s.Bands.Count && i < 10; i++)
                Choice(Row(Hz(s.Bands[i].Frequency)), Db(s.Bands[i].Gain), "band:" + i.ToString(CultureInfo.InvariantCulture));

        Section("Volume e protezione");
        Choice(Row("Protezione dalla saturazione", "Limiter: trattiene i picchi oltre il tetto. Margine: abbassa quanto serve."),
            s.Protection switch { "Headroom" => "Margine", "Off" => "Nessuna", _ => "Limiter" }, "protection");
        Choice(Row("ReplayGain", "Livella i brani con i tag ReplayGain (del brano o dell’album)."), s.ReplayGain switch { "track" => "Brano", "album" => "Album", _ => "Spento" }, "replaygain");
        Toggle(Row("Loudness", "Più bassi e alti a volume basso, come i vecchi amplificatori."), s.Loudness, "loudness");
        Toggle(Row("AutoEQ dinamico", "Come il Dynamic EQ dei ricevitori: compensa l’orecchio secondo il volume vero."), s.DynamicEq, "dynamiceq");

        Section("Passaggi fra i brani");
        Toggle(Row("Senza pause (gapless)", "Gli album dal vivo e i concept scorrono senza silenzi fra un brano e l’altro."), s.Gapless, "gapless");
        Choice(Row("Dissolvenza incrociata", "Automix: il passaggio parte e arriva su punti musicali dei due brani, in battute intere se i tempi coincidono, con volume, EQ a tre bande e filtri."),
            s.CrossfadeSeconds < 0 ? "Automix" : s.CrossfadeSeconds == 0 ? "Spenta" : s.CrossfadeSeconds + " s", "crossfade");
        if (s.CrossfadeSeconds < 0)
        {
            Toggle(Row("Salta i silenzi", "Il brano successivo parte dal primo suono, e quello che finisce non aspetta il silenzio in coda."), s.AutomixTrimSilence, "trim");
            Toggle(Row("Automix anche negli album", "Fra brani consecutivi dello stesso album, solo se il primo sfuma o finisce nel silenzio."), s.AutomixWithinAlbum, "albummix");
            Choice(Row("Stile", "Brani interi, oppure DJ edit: si esce e si entra sulle frasi musicali, anche saltando delle parti."), s.AutomixMode == "dj" ? "DJ edit" : "Brani interi", "mixmode");
            Choice(Row("Intensità", "Solo dissolvenza; Delicata (a tempo quando si può, mai effetti); Bilanciata (filtri, riverbero); DJ (anche eco, brake e loop, solo fra brani da ballo)."), s.AutomixIntensity switch { "crossfade" => "Solo dissolvenza", "gentle" => "Delicata", "dj" => "DJ", _ => "Bilanciata" }, "mixintensity");
            Toggle(Row("Sincronizzazione tempo", "Il brano che entra prende il tempo di quello che esce, senza cambiare intonazione. Spento: a tempo solo i brani che lo sono già."), s.AutomixTempoSync, "temposync");
        }

        Section("Immagine stereo");
        Choice(Row("Crossfeed per le cuffie", "Mescola un po’ i canali, come quando si ascolta dalle casse."), s.Crossfeed switch { "Low" => "Leggero", "Medium" => "Medio", "High" => "Forte", _ => "Spento" }, "crossfeed");
        Choice(Row("Bilanciamento"), s.Balance == 0 ? "Centro" : s.Balance < 0 ? $"Sinistra {-s.Balance:0}" : $"Destra {s.Balance:0}", "balance");
        Choice(Row("Ampiezza stereo", "100% = originale."), s.Width.ToString("0", CultureInfo.InvariantCulture) + "%", "width");
        Toggle(Row("Mono"), s.Mono, "mono");
        return y - body.Y + 40;
    }

    private void ChooseAudio(Hit hit)
    {
        var s = CinecoreAudioSettings.Current;
        void Done() { CinecoreAudioSettings.Save(); InvalidateVisual(); }
        IEnumerable<(string, bool, Action)> Values(IEnumerable<double> values, double current, Func<double, string> label, Action<double> set) =>
            values.Select(v => (label(v), Math.Abs(v - current) < 0.01, (Action)(() => { set(v); Done(); })));
        IEnumerable<double> Range(double from, double to, double step) { for (double v = from; v <= to + 1e-9; v += step) yield return Math.Round(v, 1); }
        switch (hit.Key)
        {
            case "engine": s.Enabled = !s.Enabled; Done(); return;
            case "eq": s.EqEnabled = !s.EqEnabled; Done(); return;
            case "exclusive": s.Exclusive = !s.Exclusive; Done(); return;
            case "bitperfect": s.BitPerfect = !s.BitPerfect; Done(); return;
            case "loudness": s.Loudness = !s.Loudness; Done(); return;
            case "dynamiceq": s.DynamicEq = !s.DynamicEq; Done(); return;
            case "gapless": s.Gapless = !s.Gapless; Done(); return;
            case "mono": s.Mono = !s.Mono; Done(); return;
            case "eqmode":
                ShowChoices(hit.Bounds, new[] { ("graphic10", "10 bande"), ("graphic31", "31 bande"), ("parametric", "Parametrico") }
                    .Select(o => (o.Item2, s.EqMode == o.Item1, (Action)(() =>
                    {
                        // Passando a 31 bande si parte dalla curva a 10 bande portata sui terzi d'ottava.
                        if (o.Item1 == "graphic31" && s.EqMode != "graphic31" && s.Graphic31.All(g => Math.Abs(g) < 0.01))
                            s.Graphic31 = Interpolate31(s.Bands.Select(b => b.Gain).ToArray());
                        s.EqMode = o.Item1; s.EqEnabled = true; Done();
                    }))));
                return;
            case "preset":
                ShowChoices(hit.Bounds, EqPresets.Select(p => (p.Name, p.Key == s.Preset, (Action)(() =>
                {
                    s.Preset = p.Key; s.EqEnabled = true;
                    s.Bands = AudioDspSettings.GraphicBands(p.Gains).Select(CinecoreAudioSettings.BandDto.From).ToList();
                    if (s.EqMode == "graphic31") s.Graphic31 = Interpolate31(p.Gains);
                    Done();
                }))));
                return;
            case "preamp": ShowChoices(hit.Bounds, Values(Range(-12, 0, 1), s.PreampDb, Db, v => s.PreampDb = v)); return;
            case "protection":
                ShowChoices(hit.Bounds, new[] { ("Limiter", "Limiter"), ("Headroom", "Margine"), ("Off", "Nessuna") }
                    .Select(o => (o.Item2, s.Protection == o.Item1, (Action)(() => { s.Protection = o.Item1; Done(); }))));
                return;
            case "replaygain":
                ShowChoices(hit.Bounds, new[] { ("off", "Spento"), ("track", "Brano"), ("album", "Album") }
                    .Select(o => (o.Item2, s.ReplayGain == o.Item1, (Action)(() => { s.ReplayGain = o.Item1; Done(); }))));
                return;
            case "crossfade": ShowChoices(hit.Bounds, Values(new double[] { 0, -1, 3, 6, 10 }, s.CrossfadeSeconds, v => v < 0 ? "Automix" : v == 0 ? "Spenta" : v + " s", v => s.SetCrossfade((int)v))); return;
            case "trim": s.AutomixTrimSilence = !s.AutomixTrimSilence; Done(); return;
            case "albummix": s.AutomixWithinAlbum = !s.AutomixWithinAlbum; Done(); return;
            case "temposync": s.AutomixTempoSync = !s.AutomixTempoSync; Done(); return;
            case "mixmode":
                ShowChoices(hit.Bounds, new[] { ("preserve", "Brani interi"), ("dj", "DJ edit") }.Select(o => (o.Item2, s.AutomixMode == o.Item1, (Action)(() => { s.AutomixMode = o.Item1; Done(); }))));
                return;
            case "mixintensity":
                ShowChoices(hit.Bounds, new[] { ("crossfade", "Solo dissolvenza"), ("gentle", "Delicata"), ("balanced", "Bilanciata"), ("dj", "DJ") }.Select(o => (o.Item2, s.AutomixIntensity == o.Item1, (Action)(() => { s.AutomixIntensity = o.Item1; Done(); }))));
                return;
            case "crossfeed":
                ShowChoices(hit.Bounds, new[] { ("Off", "Spento"), ("Low", "Leggero"), ("Medium", "Medio"), ("High", "Forte") }
                    .Select(o => (o.Item2, s.Crossfeed == o.Item1, (Action)(() => { s.Crossfeed = o.Item1; Done(); }))));
                return;
            case "balance": ShowChoices(hit.Bounds, Values(Range(-50, 50, 10), s.Balance, v => v == 0 ? "Centro" : v < 0 ? $"Sinistra {-v:0}" : $"Destra {v:0}", v => s.Balance = v)); return;
            case "width": ShowChoices(hit.Bounds, Values(new double[] { 0, 50, 75, 100, 125, 150, 200 }, s.Width, v => v.ToString("0", CultureInfo.InvariantCulture) + "%", v => s.Width = v)); return;
            default:
                int idx = hit.Key.IndexOf(':') is int colon && colon > 0 && int.TryParse(hit.Key[(colon + 1)..], out int parsed) ? parsed : -1;
                string kind = hit.Key.Split(':')[0];
                if (kind == "b31" && idx >= 0 && idx < s.Graphic31.Count)
                {
                    ShowChoices(hit.Bounds, Values(Range(-12, 12, 1), s.Graphic31[idx], Db, v => { s.Graphic31[idx] = v; s.EqEnabled = true; s.Preset = "custom"; }));
                    return;
                }
                if (idx >= 0 && idx < s.Parametric.Count && kind is "ptype" or "pfreq" or "pgain" or "pq")
                {
                    var pb = s.Parametric[idx];
                    switch (kind)
                    {
                        case "ptype":
                            ShowChoices(hit.Bounds, new[] { "Peak", "LowShelf", "HighShelf", "LowPass", "HighPass" }
                                .Select(t => (TypeName(t), pb.Type == t, (Action)(() => { pb.Type = t; s.EqEnabled = true; Done(); }))));
                            break;
                        case "pfreq":
                            ShowChoices(hit.Bounds, Values(new double[] { 20, 30, 40, 50, 60, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000 },
                                pb.Frequency, Hz, v => { pb.Frequency = v; s.EqEnabled = true; }));
                            break;
                        case "pgain": ShowChoices(hit.Bounds, Values(Range(-15, 15, 1), pb.Gain, Db, v => { pb.Gain = v; s.EqEnabled = true; })); break;
                        default: ShowChoices(hit.Bounds, Values(new double[] { 0.3, 0.5, 0.71, 1.0, 1.41, 2.0, 3.0, 4.32, 6.0, 10.0 }, pb.Q, v => v.ToString("0.##", CultureInfo.CurrentCulture), v => { pb.Q = v; s.EqEnabled = true; })); break;
                    }
                    return;
                }
                if (hit.Key.StartsWith("band:", StringComparison.Ordinal) && int.TryParse(hit.Key[5..], out int band) && band < s.Bands.Count)
                    ShowChoices(hit.Bounds, Values(Range(-12, 12, 1), s.Bands[band].Gain, Db, v =>
                    {
                        s.Bands[band].Gain = v; s.EqEnabled = true;
                        // Una banda cambiata a mano: la curva non e' piu' quella del preset.
                        if (EqPresets.FirstOrDefault(p => p.Key == s.Preset).Gains is { } g && Math.Abs(g[band] - v) > 0.01) s.Preset = "custom";
                    }));
                return;
        }
    }
}
