namespace Cinecore.AudioVideoSync;

internal static class SyntheticSelfTest
{
    public static void Run()
    {
        foreach (int expectedDelayMs in new[] { 1000, -640, 180 })
        {
            (short[] reference, short[] target) = CreateSignals(expectedDelayMs);
            var options = new AnalysisOptions(
                "synthetic", 0, 1, 0, 150, 2, null, null, null, null, false);
            SyncAnalysisResult result = SyncDetector.Analyze(reference, target, options);
            if (Math.Abs(result.DetectedTargetDelayMs - expectedDelayMs) > 40)
            {
                throw new InvalidOperationException(
                    $"Self-test fallito: atteso {expectedDelayMs} ms, rilevato {result.DetectedTargetDelayMs} ms.");
            }
            Console.WriteLine(
                $"OK: delay target {expectedDelayMs:+0;-0;0} ms -> correzione {result.RecommendedCorrectionMs:+0;-0;0} ms " +
                $"(confidenza {result.Confidence:P0})");
        }
    }

    private static (short[] Reference, short[] Target) CreateSignals(int delayMilliseconds)
    {
        const int durationSeconds = 150;
        int sampleRate = FfmpegAudioExtractor.SampleRate;
        int length = durationSeconds * sampleRate;
        var reference = new short[length];
        var random = new Random(81723);

        int cursor = 0;
        while (cursor < length)
        {
            int silence = random.Next(sampleRate / 5, sampleRate * 2);
            cursor += silence;
            int burst = random.Next(sampleRate / 8, sampleRate * 3);
            int end = Math.Min(length, cursor + burst);
            double amplitude = random.NextDouble() * 9000 + 1500;
            for (int i = cursor; i < end; i++)
            {
                double carrier = Math.Sin(i * 2 * Math.PI * (120 + random.Next(0, 240)) / sampleRate);
                reference[i] = (short)Math.Clamp(carrier * amplitude, short.MinValue, short.MaxValue);
            }
            cursor = end;
        }

        int delaySamples = delayMilliseconds * sampleRate / 1000;
        var target = new short[length];
        for (int source = 0; source < length; source++)
        {
            int destination = source + delaySamples;
            if ((uint)destination < (uint)length)
                target[destination] = reference[source];
        }
        return (reference, target);
    }
}
