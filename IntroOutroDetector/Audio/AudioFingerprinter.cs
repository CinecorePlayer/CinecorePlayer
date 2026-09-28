namespace IntroOutroDetector.Audio;

/// <summary>
/// Converts raw PCM audio into a compact fingerprint: an array of 32-bit hashes,
/// one per audio frame. Similar in spirit to Chromaprint but much lighter weight.
///
/// Settings:
///   SampleRate  = 8000 Hz  (extracted by FFmpeg)
///   FrameSize   = 4096 samples  ≈ 512 ms per frame
///   HopSize     = 4096 samples  (no overlap → ~2 frames/sec)
///   Bands       = 32 spectral bands across 0–4000 Hz
///   Hash bits   = 32 (spectral flux: is energy increasing band-to-band?)
/// </summary>
public static class AudioFingerprinter
{
    public const int FrameSize = 2048;
    public const int HopSize  = 2048;
    public const int NumBands = 32;
    private const int SampleRate = AudioExtractor.SampleRate; // 8000

    // Seconds per fingerprint frame
    public static double SecondsPerFrame => (double)HopSize / SampleRate;

    public static double FramesToSeconds(int frames) => frames * SecondsPerFrame;
    public static int    SecondsToFrames(double sec)  => (int)(sec / SecondsPerFrame);

    /// <summary>
    /// Compute a fingerprint from raw 16-bit mono PCM at SampleRate Hz.
    /// Returns an array of 32-bit hashes, one per audio frame.
    /// </summary>
    public static uint[] Compute(short[] pcm)
    {
        if (pcm.Length < FrameSize) return [];

        int frameCount = (pcm.Length - FrameSize) / HopSize + 1;
        var fingerprint = new uint[frameCount];

        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * HopSize;
            fingerprint[f] = ComputeFrameHash(pcm, offset, FrameSize);
        }

        return fingerprint;
    }

    /// <summary>
    /// Reverse a fingerprint array (used for outro matching from episode end).
    /// </summary>
    public static uint[] Reverse(uint[] fp)
    {
        var rev = (uint[])fp.Clone();
        Array.Reverse(rev);
        return rev;
    }

    // ──────────────────────────────────────────────
    // Internal helpers
    // ──────────────────────────────────────────────

    private static uint ComputeFrameHash(short[] pcm, int offset, int length)
    {
        const int bins = NumBands;
        int samplesPerBin = Math.Max(1, length / bins);

        Span<long> energy = stackalloc long[bins];
        for (int b = 0; b < bins; b++)
        {
            int start = offset + b * samplesPerBin;
            int end = Math.Min(offset + (b + 1) * samplesPerBin, Math.Min(offset + length, pcm.Length));

            long sum = 0;
            for (int i = start; i < end; i++)
                sum += Math.Abs((int)pcm[i]);

            energy[b] = sum;
        }

        uint hash = 0;
        for (int i = 1; i < bins; i++)
        {
            if (energy[i] > energy[i - 1])
                hash |= 1u << (i - 1);
        }

        long total = 0;
        for (int b = 0; b < bins; b++) total += energy[b];
        if (total > 1000) hash |= 0x80000000u;

        return hash;
    }

    private static uint ComputeFrameHash(float[] magnitudes)
    {
        // FFT gives FrameSize/2 = 2048 bins covering 0–4000 Hz.
        int totalBins = magnitudes.Length; // 2048
        int binsPerBand = totalBins / NumBands; // 64 bins per band

        Span<float> bandEnergy = stackalloc float[NumBands];
        for (int b = 0; b < NumBands; b++)
        {
            float energy = 0f;
            int start = b * binsPerBand;
            int end   = Math.Min(start + binsPerBand, totalBins);
            for (int i = start; i < end; i++)
                energy += magnitudes[i];
            bandEnergy[b] = energy;
        }

        // Hash: bit i = 1 if  E[i] - E[i-1]  >  E[i+1] - E[i]
        // (local spectral "peak" test — robust against gain changes)
        uint hash = 0;
        for (int i = 1; i < NumBands - 1; i++)
        {
            float d1 = bandEnergy[i]     - bandEnergy[i - 1];
            float d2 = bandEnergy[i + 1] - bandEnergy[i];
            if (d1 > d2) hash |= 1u << (i - 1);
        }

        // Extra bit: is total energy above the long-run mean? (silence detection)
        float total = 0f;
        for (int b = 0; b < NumBands; b++) total += bandEnergy[b];
        if (total > 0.01f) hash |= 0x80000000u;

        return hash;
    }
}
