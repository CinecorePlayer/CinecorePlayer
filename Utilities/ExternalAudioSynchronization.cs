#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cinecore.AudioVideoSync;

namespace CinecorePlayer2025.Utilities;

internal static class ExternalAudioSynchronization
{
    internal sealed record Result(int CorrectionMs, double Confidence, string Method, string Detail, bool Reliable);

    internal static async Task<Result> AnalyzeAsync(string video, string audio, int referenceOrdinal, double duration, bool speech, IProgress<string> progress, CancellationToken ct, int targetOrdinal = 0)
    {
        string ffmpeg = ExternalToolLocator.FindFfmpeg(null);
        double segment = duration >= 45 ? Math.Min(90, duration / 3) : Math.Min(100, duration > 0 ? duration : 100);
        double last = Math.Max(0, duration - segment);
        double[] starts = duration >= 45 ? new[] { 0d, last * .5, last } : new[] { 0d };
        var results = new List<SyncAnalysisResult>();
        foreach (double start in starts.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"Analisi audio {results.Count + 1}/{starts.Length} · {TimeSpan.FromSeconds(start):hh\\:mm\\:ss}");
            var reference = FfmpegAudioExtractor.ExtractMonoPcmAsync(ffmpeg, video, referenceOrdinal, start, segment, ct);
            var target = FfmpegAudioExtractor.ExtractMonoPcmAsync(ffmpeg, audio, targetOrdinal, start, segment, ct);
            await Task.WhenAll(reference, target).ConfigureAwait(false);
            var options = new AnalysisOptions(video, referenceOrdinal, targetOrdinal, start, segment, 10, ffmpeg, null, null, null, false);
            try { results.Add(await Task.Run(() => SyncDetector.Analyze(reference.Result, target.Result, options, ct), ct).ConfigureAwait(false)); }
            catch (InvalidOperationException) { progress.Report("Scena senza riferimenti sufficienti · proseguo con le altre scene…"); }
        }
        var best = results.OrderByDescending(r => results.Count(o => Math.Abs(o.RecommendedCorrectionMs - r.RecommendedCorrectionMs) <= 100)).ThenByDescending(r => r.Confidence).FirstOrDefault();
        var supporting = best == null ? new List<SyncAnalysisResult>() : results.Where(r => Math.Abs(r.RecommendedCorrectionMs - best.RecommendedCorrectionMs) <= 100).ToList();
        double confidence = supporting.Count == 0 ? 0 : supporting.Average(r => r.Confidence) * supporting.Count / starts.Length;
        bool reliable = supporting.Count >= 2 && supporting.Count == starts.Length && supporting.All(r => r.ConfidenceLevel != ConfidenceLevel.Low) && confidence >= .72;
        int correction = supporting.Count == 0 ? 0 : (int)Math.Round(supporting.Average(r => r.RecommendedCorrectionMs));
        if (reliable || !speech)
            return new(correction, confidence, "Correlazione dell’attività audio su più scene", $"{supporting.Count}/{results.Count} scene concordanti. " + (reliable ? "Ritardo costante verificato." : "Risultato incerto o possibile differenza di montaggio: correzione da verificare."), reliable);

        // Recognition is a second, explicit method. Matching just the first word
        // is not evidence: opening logos, silence and translations can differ.
        progress.Report("Trascrizione locale di una scena · ricerca di parole e pause comuni…");
        string directory = Path.Combine(Path.GetTempPath(), "CinecoreAudioSync", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            double start = starts[0];
            async Task<string> Clip(string source, int ordinal, string name)
            {
                short[] pcm = await FfmpegAudioExtractor.ExtractMonoPcmAsync(ffmpeg, source, ordinal, start, Math.Min(90, segment), ct).ConfigureAwait(false);
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
            if (!backend.IsAvailable(a)) throw new InvalidOperationException("Il riconoscitore locale non è disponibile.");
            var wordsA = await backend.RecognizeAsync(new(a), ct).ConfigureAwait(false);
            var wordsB = await backend.RecognizeAsync(new(b), ct).ConfigureAwait(false);
            if (wordsA == null || wordsB == null) return new(correction, confidence, "Correlazione audio", "Trascrizione senza parole utilizzabili; regola il ritardo manualmente.", false);
            static string Token(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            var anchors = (from x in wordsA.Words from y in wordsB.Words
                           where Token(x.Text).Length >= 4 && Token(x.Text) == Token(y.Text) && x.Confidence >= .6 && y.Confidence >= .6 && Math.Abs(x.StartSeconds - y.StartSeconds) <= 10
                           select (Ms: (x.StartSeconds - y.StartSeconds) * 1000, Time: x.StartSeconds)).ToList();
            if (anchors.Count >= 5)
            {
                var center = anchors.OrderByDescending(x => anchors.Count(y => Math.Abs(x.Ms - y.Ms) < 160)).First();
                var cluster = anchors.Where(x => Math.Abs(x.Ms - center.Ms) < 160).DistinctBy(x => x.Time).ToList();
                if (cluster.Count >= 5 && cluster.Max(x => x.Time) - cluster.Min(x => x.Time) >= 15)
                    return new((int)Math.Round(cluster.Average(x => x.Ms)), .65, "Allineamento della trascrizione", $"{cluster.Count} parole comuni distribuite nella scena. Verifica il risultato: lingue o montaggi diversi possono richiedere altre correzioni.", false);
            }
            return new(correction, confidence, "Correlazione audio + trascrizione", "Nessun allineamento verificabile. Le prime parole da sole non dimostrano il sincronismo fra doppiaggi diversi.", false);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) { try { File.Delete(file); } catch { } }
            try { Directory.Delete(directory); } catch { }
        }
    }
}
