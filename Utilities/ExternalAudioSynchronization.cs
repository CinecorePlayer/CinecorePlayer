#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cinecore.AudioVideoSync;

namespace CinecorePlayer2025.Utilities;

internal static class ExternalAudioSynchronization
{
    /// <param name="CorrectionMs">Delay to give the track. With a speed difference: the delay left once the speed is corrected.</param>
    /// <param name="SpeedRatio">How much faster than the reference the track runs (1 = same speed).</param>
    /// <param name="SpeedReliable">The speed difference was measured on the scenes, not only guessed from the durations.</param>
    internal sealed record Result(int CorrectionMs, double Confidence, string Method, string Detail, bool Reliable, double SpeedRatio = 1.0, bool SpeedReliable = false, string Note = "",
        IReadOnlyList<(double Time, int Ms)>? Curve = null);

    /// <summary>Progress of the analysis: 0..1 and what is being done.</summary>
    internal readonly record struct Step(double Fraction, string Text);

    private static readonly double PalFast = 25 / (24000 / 1001.0);

    // Rapporti di velocita' tipici fra edizioni: PAL (25 fps) contro cinema (23,976/24 fps).
    private static readonly (double Ratio, string It, string En)[] KnownSpeeds =
    {
        (25 / (24000 / 1001.0), "25 fps contro 23,976 fps", "25 fps vs 23.976 fps"),
        (25 / 24.0, "25 fps contro 24 fps", "25 fps vs 24 fps"),
        (24 / (24000 / 1001.0), "24 fps contro 23,976 fps", "24 fps vs 23.976 fps"),
        ((24000 / 1001.0) / 25, "23,976 fps contro 25 fps", "23.976 fps vs 25 fps"),
        (24 / 25.0, "24 fps contro 25 fps", "24 fps vs 25 fps"),
        ((24000 / 1001.0) / 24, "23,976 fps contro 24 fps", "23.976 fps vs 24 fps"),
    };

    private static string T(string it, string en) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(AppLanguage.T(it, en));
    private static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"h\:mm\:ss");

    /// <summary>Durata del flusso audio scelto (0 se non leggibile).</summary>
    private static double AudioDuration(string path, int ordinal)
    {
        try { using var decoder = new CinecorePlayer2025.Audio.CinecoreAudioDecoder(path, 48000, 2, ordinal); return decoder.StreamDurationSeconds(); }
        catch { return 0; }
    }

    /// <summary>Name of a known frame-rate pair when the ratio matches one, e.g. "25 fps vs 23.976 fps".</summary>
    internal static string? SpeedName(double ratio)
    {
        foreach (var known in KnownSpeeds)
            if (Math.Abs(ratio - known.Ratio) < 0.0003) return T(known.It, known.En);
        return null;
    }

    private static double Snap(double ratio)
    {
        if (Math.Abs(ratio - 1) < 0.0003) return 1;
        foreach (var known in KnownSpeeds)
            if (Math.Abs(ratio - known.Ratio) < 0.0003) return known.Ratio;
        return ratio;
    }

    /// <summary>Where a speed-corrected copy of the track is written: next to the track, or in the player's folder when that is read-only.</summary>
    internal static string ConvertedPath(string audio, int ordinal)
    {
        string name = Path.GetFileNameWithoutExtension(audio) + (ordinal > 0 ? ".a" + (ordinal + 1) : "") + ".sync.mka";
        string? folder = Path.GetDirectoryName(audio);
        try
        {
            if (!string.IsNullOrEmpty(folder))
            {
                string probe = Path.Combine(folder, ".cinecore-write-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return Path.Combine(folder, name);
            }
        }
        catch { }
        folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "audio-converted");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    /// <summary>
    /// Writes a copy of the track at the video's speed (lossless FLAC, same channels). The speed is
    /// changed the way the PAL transfer changed it, by playing the samples slower or faster: pitch
    /// goes back to the original together with the duration.
    /// </summary>
    internal static async Task<string> ConvertSpeedAsync(string audio, int ordinal, double speedRatio, IProgress<Step> progress, CancellationToken ct)
    {
        string ffmpeg = ExternalToolLocator.FindFfmpeg(null);
        string output = ConvertedPath(audio, ordinal);
        string partial = output + ".part";
        double sourceDuration = await Task.Run(() => AudioDuration(audio, ordinal), ct).ConfigureAwait(false);
        double expected = sourceDuration * speedRatio;
        int rate = (int)Math.Round(48000 / speedRatio);
        var startInfo = new System.Diagnostics.ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", audio, "-map", $"0:a:{ordinal}", "-vn", "-sn", "-dn",
            "-af", $"aresample=48000,asetrate={rate.ToString(CultureInfo.InvariantCulture)},aresample=48000",
            "-c:a", "flac", "-sample_fmt", "s16", "-f", "matroska", "-progress", "pipe:1", "-nostats", partial
        })
            startInfo.ArgumentList.Add(argument);
        string label = T("Conversione della traccia alla velocità del video", "Converting the track to the video's speed");
        progress.Report(new Step(0, label));
        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("FFmpeg");
        using var cancellation = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        Task<string> errors = process.StandardError.ReadToEndAsync(ct);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                // "out_time_ms" is in microseconds despite its name.
                if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long micro) && expected > 0)
                    progress.Report(new Step(Math.Clamp(micro / 1e6 / expected, 0, 0.99), label));
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            string stderr = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "FFmpeg " + process.ExitCode : stderr.Trim());
            File.Move(partial, output, true);
            progress.Report(new Step(1, label));
            return output;
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }

    internal static async Task<Result> AnalyzeAsync(string video, string audio, int referenceOrdinal, double duration, bool speech, IProgress<Step> progress, CancellationToken ct, int targetOrdinal = 0)
    {
        string ffmpeg = ExternalToolLocator.FindFfmpeg(null);
        bool external = !string.Equals(audio, video, StringComparison.OrdinalIgnoreCase);
        progress.Report(new Step(0, T("Lettura delle tracce…", "Reading the tracks…")));
        double referenceDuration = await Task.Run(() => AudioDuration(video, referenceOrdinal), ct).ConfigureAwait(false);
        double targetDuration = await Task.Run(() => AudioDuration(audio, targetOrdinal), ct).ConfigureAwait(false);
        if (referenceDuration <= 30) referenceDuration = duration;
        if (referenceDuration <= 0) throw new InvalidOperationException(T("Durata della traccia di riferimento non leggibile.", "The reference track's duration cannot be read."));
        double durationRatio = targetDuration > 30 ? referenceDuration / targetDuration : 1;

        // Si provano prima le velocita' che le durate rendono probabili; la stessa velocita' resta
        // la prima scelta finche' le durate non dicono altro (i titoli di coda cambiano di poco la durata).
        var hypotheses = new List<double> { 1 };
        if (external)
        {
            hypotheses.Add(PalFast);
            hypotheses.Add(1 / PalFast);
            if (Math.Abs(durationRatio - PalFast) < 0.012) hypotheses = new List<double> { PalFast, 1, 1 / PalFast };
            else if (Math.Abs(durationRatio - 1 / PalFast) < 0.012) hypotheses = new List<double> { 1 / PalFast, 1, PalFast };
        }
        // Un file esterno puo' partire molti secondi prima o dopo (loghi, edizioni diverse);
        // due tracce dello stesso file stanno sempre vicine.
        double maxOffset = Math.Min(external ? 45 : 12, Math.Max(2, referenceDuration / 3));
        var reference = new Dictionary<double, object>();
        AudioTrackAligner.Fit? best = null;
        for (int h = 0; h < hypotheses.Count; h++)
        {
            double speed = hypotheses[h];
            string phase = h == 0 ? T("Confronto delle scene", "Comparing scenes")
                : T($"Prova a velocità diversa ({Math.Abs(speed - 1) * 100:0.0}% {(speed > 1 ? "più veloce" : "più lenta")})", $"Trying a different speed ({Math.Abs(speed - 1) * 100:0.0}% {(speed > 1 ? "faster" : "slower")})");
            var fit = await AudioTrackAligner.MeasureAsync(ffmpeg, video, referenceOrdinal, audio, targetOrdinal, referenceDuration, speed, maxOffset, reference,
                (done, total, at) => progress.Report(new Step(done / (double)Math.Max(1, total), done < total ? $"{phase}, {done + 1}/{total}, {Clock(at)}" : phase)), ct).ConfigureAwait(false);
            Dbg.Log($"[AUDIOSYNC] speed {speed:0.00000}: reliable={fit.Reliable}, inliers {fit.Inliers}/{fit.Valid}/{fit.Total}, delay {fit.Delay * 1000:0} ms, fitted speed {fit.Speed:0.000000}, residual {fit.ResidualMs:0} ms, " +
                    string.Join(" ", fit.Scenes.Select(s => string.Create(CultureInfo.InvariantCulture, $"[{s.Time:0}s {s.Delay * 1000:0}ms p{s.Peak:0.00} m{s.Margin:0.00}{(s.Side ? " side" : "")}{(s.Valid ? "" : " x")}]"))), Dbg.LogLevel.Info);
            if (best == null || (fit.Reliable && !best.Reliable) || (fit.Reliable == best.Reliable && fit.Inliers > best.Inliers)) best = fit;
            if (fit.Reliable) break;
        }

        // Nessuna risposta sicura: analisi approfondita. Il doppio delle scene, piu' lunghe, cercate
        // molto piu' lontano (minuti, non secondi) e a tutte le velocita' anche fra tracce dello stesso file.
        AudioTrackAligner.Fit? widest = null;
        // (Non serve quando le scene sono gia' state riconosciute quasi tutte e solo non concordano
        // su un valore: quello e' un ritardo variabile, misurato piu' sotto.)
        bool recognised = Snap(best!.Speed) == 1 && best.Valid >= Math.Max(4, (int)Math.Ceiling(best.Total * 0.6));
        if (!best.Reliable && !recognised && referenceDuration >= 240)
        {
            double deepOffset = Math.Min(external ? 300 : 30, referenceDuration / 4);
            var deepReference = new Dictionary<double, object>();
            var deepHypotheses = hypotheses.Concat(new[] { 1.0, PalFast, 1 / PalFast }).Distinct().ToList();
            for (int h = 0; h < deepHypotheses.Count; h++)
            {
                double speed = deepHypotheses[h];
                string phase = T($"Analisi approfondita {h + 1}/{deepHypotheses.Count}", $"In-depth analysis {h + 1}/{deepHypotheses.Count}") +
                    (speed == 1 ? "" : T($", velocità {(speed > 1 ? "+" : "−")}{Math.Abs(speed - 1) * 100:0.0}%", $", speed {(speed > 1 ? "+" : "−")}{Math.Abs(speed - 1) * 100:0.0}%"));
                var fit = await AudioTrackAligner.MeasureAsync(ffmpeg, video, referenceOrdinal, audio, targetOrdinal, referenceDuration, speed, deepOffset, deepReference,
                    (done, total, at) => progress.Report(new Step(done / (double)Math.Max(1, total), done < total ? $"{phase}, {done + 1}/{total}, {Clock(at)}" : phase)), ct,
                    sceneCount: 16, windowSeconds: 60).ConfigureAwait(false);
                Dbg.Log($"[AUDIOSYNC] deep, speed {speed:0.00000}: reliable={fit.Reliable}, inliers {fit.Inliers}/{fit.Valid}/{fit.Total}, delay {fit.Delay * 1000:0} ms, fitted speed {fit.Speed:0.000000}, " +
                        string.Join(" ", fit.Scenes.Select(s => string.Create(CultureInfo.InvariantCulture, $"[{s.Time:0}s {s.Delay * 1000:0}ms p{s.Peak:0.00} m{s.Margin:0.00}{(s.Valid ? "" : " x")}]"))), Dbg.LogLevel.Info);
                if ((fit.Reliable && !best.Reliable) || (fit.Reliable == best.Reliable && fit.Inliers > best.Inliers)) best = fit;
                if (widest == null || fit.Valid > widest.Valid) widest = fit;
                if (fit.Reliable) break;
            }
        }

        // Scene riconosciute bene ma con ritardi diversi fra loro (doppiaggio sincronizzato a rulli,
        // montaggio diverso): nessun valore unico e' giusto. Si misura come cambia il ritardo lungo
        // il film e lo si consegna come curva, che il player segue durante la visione.
        var basis = best.Reliable ? best : widest != null && widest.Valid > best.Valid ? widest : best;
        if (Snap(basis.Speed) == 1 && basis.Total >= 5 && basis.Valid >= Math.Max(4, (int)Math.Ceiling(basis.Total * 0.6)) && (basis.Varies || !basis.Reliable))
        {
            var known = basis.Scenes.Where(scene => scene.Valid).ToList();
            var delays = known.Select(scene => scene.Delay).OrderBy(value => value).ToList();
            double middle = delays[delays.Count / 2], reach = (delays[^1] - delays[0]) / 2 + 3;
            string phase = T("Ritardo variabile, misuro altri punti del film", "Variable delay, measuring more points of the film");
            var more = await AudioTrackAligner.MeasureAsync(ffmpeg, video, referenceOrdinal, audio, targetOrdinal, referenceDuration, 1, reach, new Dictionary<double, object>(),
                (done, total, at) => progress.Report(new Step(0.6 * done / Math.Max(1, total), done < total ? $"{phase}, {done + 1}/{total}, {Clock(at)}" : phase)), ct,
                sceneCount: 32, windowSeconds: 30, centreDelay: (delays[0] + delays[^1]) / 2).ConfigureAwait(false);
            string refine = T("Ritardo variabile, cerco i punti in cui cambia", "Variable delay, locating where it changes");
            var points = await AudioTrackAligner.RefineCurveAsync(ffmpeg, video, referenceOrdinal, audio, targetOrdinal, known.Concat(more.Scenes),
                (done, total) => progress.Report(new Step(0.6 + 0.4 * done / Math.Max(1, total), refine)), ct).ConfigureAwait(false);
            Dbg.Log("[AUDIOSYNC] curve: " + string.Join(" ", points.Select(p => string.Create(CultureInfo.InvariantCulture, $"[{p.Time:0}s {p.Delay * 1000:0}ms]"))), Dbg.LogLevel.Info);
            if (points.Count >= 6)
            {
                var curve = points.Select(point => (point.Time, Ms: (int)Math.Round(-point.Delay * 1000))).ToList();
                var ordered = curve.Select(point => point.Ms).OrderBy(value => value).ToList();
                int central = ordered[ordered.Count / 2], low = ordered[0], high = ordered[^1];
                int changes = Enumerable.Range(0, curve.Count - 1).Count(i => Math.Abs(curve[i + 1].Ms - curve[i].Ms) > 120);
                bool edits = Enumerable.Range(0, curve.Count - 1).Any(i => Math.Abs(curve[i + 1].Ms - curve[i].Ms) > 400);
                double found = points.Count / (double)Math.Max(1, known.Count + more.Total);
                // Misurato piu' fitto, il ritardo sta tutto in una fascia stretta: e' costante.
                if (high - low < 80)
                    return new(central, Math.Clamp(0.5 + 0.45 * found, 0, 0.95), T("Musica ed effetti confrontati su più scene", "Music and effects compared across several scenes"),
                        T($"{points.Count} punti del film concordano entro {(high - low + 1) / 2} ms. Ritardo costante verificato.", $"{points.Count} points of the film agree within {(high - low + 1) / 2} ms. Constant offset verified."),
                        true, Note: T("costante in tutte le scene", "constant in every scene"));
                return new(central, Math.Clamp(0.5 + 0.45 * found, 0, 0.95), T("Ritardo variabile", "Variable delay"),
                    (edits
                        ? T($"Le due tracce non hanno lo stesso montaggio: il ritardo passa da {low:+0;-0;0} a {high:+0;-0;0} ms in {changes} punti.", $"The two tracks are not the same edit: the delay goes from {low:+0;-0;0} to {high:+0;-0;0} ms at {changes} points.")
                        : T($"La traccia è stata sincronizzata a tratti: il ritardo va da {low:+0;-0;0} a {high:+0;-0;0} ms lungo il film.", $"The track was synchronised in sections: the delay goes from {low:+0;-0;0} to {high:+0;-0;0} ms along the film.")) + " " +
                    T($"Misurato in {points.Count} punti: il player lo segue da solo durante la visione.", $"Measured at {points.Count} points: the player follows it by itself while watching."),
                    true, Note: T($"variabile, da {low:+0;-0;0} a {high:+0;-0;0} ms", $"variable, from {low:+0;-0;0} to {high:+0;-0;0} ms"), Curve: curve);
            }
        }

        // Tante scene riconosciute ma a gruppi con ritardi diversi: le due tracce hanno montaggi
        // diversi (una scena in piu', un logo tagliato a meta' film).
        if (!best.Reliable && widest != null && AudioTrackAligner.Segments(widest.Scenes) is { Count: >= 2 } segments)
        {
            string list = string.Join("; ", segments.Select(segment =>
                T($"da {Clock(segment.From)} a {Clock(segment.To)} ritardo {-segment.Delay * 1000:+0;-0;0} ms", $"from {Clock(segment.From)} to {Clock(segment.To)} delay {-segment.Delay * 1000:+0;-0;0} ms")));
            var longest = segments.OrderByDescending(segment => segment.Count).First();
            return new((int)Math.Round(-longest.Delay * 1000), Math.Min(0.49, widest.Confidence), T("Montaggio diverso", "Different edit"),
                T($"Le tracce non hanno lo stesso montaggio: {list}. Un solo ritardo allinea solo un tratto; proposto quello del tratto più lungo.",
                  $"The tracks are not the same edit: {list}. One delay aligns only one stretch; the one of the longest stretch is suggested."),
                false, Note: T("montaggi diversi", "different edits"));
        }

        var chosen = best!;
        double ratio = Snap(chosen.Speed);
        int correction = (int)Math.Round(-chosen.Delay * 1000);
        string agreement = T($"{chosen.Inliers} scene su {chosen.Total} concordano entro {Math.Max(1, chosen.ResidualMs):0} ms.", $"{chosen.Inliers} of {chosen.Total} scenes agree within {Math.Max(1, chosen.ResidualMs):0} ms.");
        if (chosen.Varies)
            agreement = T($"{chosen.Inliers} scene su {chosen.Total} trovate, ma il ritardo cambia dall’una all’altra di {chosen.SpreadMs:0} ms: la traccia è stata sincronizzata a tratti e nessun valore le allinea tutte.",
                          $"{chosen.Inliers} of {chosen.Total} scenes found, but the delay changes by {chosen.SpreadMs:0} ms from one to another: the track was synchronised in sections and no single value fits them all.");
        if (chosen.Reliable && ratio == 1)
            return new(correction, chosen.Confidence, T("Musica ed effetti confrontati su più scene", "Music and effects compared across several scenes"),
                agreement + " " + (chosen.Varies ? T($"Proposto il valore centrale: resta uno scarto fino a {chosen.ResidualMs:0} ms in alcune scene.", $"The middle value is suggested: up to {chosen.ResidualMs:0} ms remain in some scenes.")
                                                 : T("Ritardo costante verificato.", "Constant offset verified.")), true,
                Note: chosen.Varies ? T($"varia di ±{chosen.ResidualMs:0} ms fra le scene", $"varies by ±{chosen.ResidualMs:0} ms between scenes") : T("costante in tutte le scene", "constant in every scene"));

        if (chosen.Reliable)
        {
            string name = SpeedName(ratio) is { } known ? " (" + known + ")" : "";
            string direction = ratio > 1 ? T("più veloce", "faster") : T("più lenta", "slower");
            return new(correction, chosen.Confidence, T("Velocità diversa", "Different speed"),
                T($"La traccia è {direction} del {Math.Abs(ratio - 1) * 100:0.00}%{name}: il ritardo cambia di {Math.Abs(1 - 1 / ratio) * 60:0.00} s al minuto e nessun ritardo fisso può allinearla. {agreement} Si può convertire alla velocità del video.",
                  $"The track is {Math.Abs(ratio - 1) * 100:0.00}% {direction}{name}: the offset changes by {Math.Abs(1 - 1 / ratio) * 60:0.00} s per minute and no fixed delay can align it. {agreement} It can be converted to the video's speed."),
                false, ratio, true);
        }

        string uncertain = chosen.Valid == 0
            ? T("Nessuna scena con abbastanza musica o effetti in comune: le tracce potrebbero avere montaggi diversi.", "No scene with enough shared music or effects: the tracks may be different edits.")
            : agreement + " " + T("Troppo poche per fidarsi: verifica a orecchio.", "Too few to trust: check by ear.");
        if (!speech)
            return new(chosen.Inliers >= 2 ? correction : 0, chosen.Confidence, T("Musica ed effetti confrontati su più scene", "Music and effects compared across several scenes"), uncertain, false);

        // Recognition is a second, explicit method. Matching just the first word
        // is not evidence: opening logos, silence and translations can differ.
        progress.Report(new Step(0, T("Trascrizione locale di una scena, ricerca di parole e pause comuni…", "Local transcription of a scene, looking for shared words and pauses…")));
        string directory = Path.Combine(Path.GetTempPath(), "CinecoreAudioSync", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            double start = AudioTrackAligner.SceneStarts(referenceDuration, AudioTrackAligner.WindowSeconds(referenceDuration))[0];
            async Task<string> Clip(string source, int ordinal, string name)
            {
                short[] pcm = await FfmpegAudioExtractor.ExtractMonoPcmAsync(ffmpeg, source, ordinal, start, Math.Min(90, Math.Max(20, referenceDuration / 3)), ct).ConfigureAwait(false);
                string path = Path.Combine(directory, name + ".wav");
                using var writer = new BinaryWriter(File.Create(path));
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length * 2); foreach (short sample in pcm) writer.Write(sample);
                return path;
            }
            var backend = new FasterWhisperLyricsRecognitionBackend(new() { Model = "small", UseVoiceActivityDetection = true, TimeoutSeconds = 600 });
            string a = await Clip(video, referenceOrdinal, "reference").ConfigureAwait(false);
            string b = await Clip(audio, targetOrdinal, "target").ConfigureAwait(false);
            if (!backend.IsAvailable(a)) throw new InvalidOperationException(T("Il riconoscitore locale non è disponibile.", "The local recogniser is not available."));
            progress.Report(new Step(0.3, T("Trascrizione della traccia di riferimento…", "Transcribing the reference track…")));
            var wordsA = await backend.RecognizeAsync(new(a), ct).ConfigureAwait(false);
            progress.Report(new Step(0.65, T("Trascrizione della traccia da allineare…", "Transcribing the track to align…")));
            var wordsB = await backend.RecognizeAsync(new(b), ct).ConfigureAwait(false);
            if (wordsA == null || wordsB == null) return new(0, chosen.Confidence, T("Confronto audio", "Audio comparison"), T("Trascrizione senza parole utilizzabili; regola il ritardo manualmente.", "Transcription without usable words; adjust the delay manually."), false);
            static string Token(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            var anchors = (from x in wordsA.Words from y in wordsB.Words
                           where Token(x.Text).Length >= 4 && Token(x.Text) == Token(y.Text) && x.Confidence >= .6 && y.Confidence >= .6 && Math.Abs(x.StartSeconds - y.StartSeconds) <= 10
                           select (Ms: (x.StartSeconds - y.StartSeconds) * 1000, Time: x.StartSeconds)).ToList();
            if (anchors.Count >= 5)
            {
                var center = anchors.OrderByDescending(x => anchors.Count(y => Math.Abs(x.Ms - y.Ms) < 160)).First();
                var cluster = anchors.Where(x => Math.Abs(x.Ms - center.Ms) < 160).DistinctBy(x => x.Time).ToList();
                if (cluster.Count >= 5 && cluster.Max(x => x.Time) - cluster.Min(x => x.Time) >= 15)
                    return new((int)Math.Round(cluster.Average(x => x.Ms)), .65, T("Allineamento della trascrizione", "Transcription alignment"), T($"{cluster.Count} parole comuni distribuite nella scena. Verifica il risultato: lingue o montaggi diversi possono richiedere altre correzioni.", $"{cluster.Count} shared words across the scene. Check the result: different languages or edits may need further correction."), false);
            }
            return new(chosen.Inliers >= 2 ? correction : 0, chosen.Confidence, T("Confronto audio + trascrizione", "Audio comparison + transcription"), T("Nessun allineamento verificabile. Le prime parole da sole non dimostrano il sincronismo fra doppiaggi diversi.", "No verifiable alignment. The first words alone do not prove sync between different dubs."), false);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) { try { File.Delete(file); } catch { } }
            try { Directory.Delete(directory); } catch { }
        }
    }
}
