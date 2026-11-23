namespace IntroOutroDetector.Audio;

/// <summary>
/// Converts raw PCM audio into a compact fingerprint: an array of 32-bit hashes,
/// one per audio frame. Similar in spirit to Chromaprint but much lighter weight.
///
/// Settings:
///   SampleRate  = 8000 Hz  (extracted by FFmpeg)
///   FrameSize   = 4096 samples  ≈ 512 ms per frame
///   HopSize     = 2048 samples  ≈ 256 ms between frames
///   Bands       = 32 spectral bands across 0–4000 Hz
///   Hash bits   = 32 (spectral flux: is energy increasing band-to-band?)
/// </summary>
public static class AudioFingerprinter
{
#if CINECORE_MAC_BUNDLE
    // Impronta alla Haitsma-Kalker (quella dei riconoscitori musicali): 32 bande logaritmiche fra 250 e 1950 Hz,
    // 31 bit = segno della differenza fra bande vicine confrontata con il fotogramma prima (robusta a volume, EQ e
    // codifica), bit alto = c'e' segnale. Fotogramma 0,512 s, passo 0,128 s: i bordi di sigla e titoli di coda
    // si trovano entro ~0,2 s invece che entro qualche secondo.
    public const int FrameSize = 2048;
    public const int HopSize  = 512;
    public const int NumBands = 32;
#else
    public const int FrameSize = 4096;
    public const int HopSize  = 2048;
    public const int NumBands = 32;
#endif
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
#if CINECORE_MAC_BUNDLE
        return ComputeDifferential(pcm);
#endif

        int frameCount = (pcm.Length - FrameSize) / HopSize + 1;
        var fingerprint = new uint[frameCount];

        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * HopSize;
            fingerprint[f] = ComputeFrameHash(FftProcessor.ComputeMagnitudes(pcm, offset, FrameSize));
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

#if CINECORE_MAC_BUNDLE
    private static int[]? _bandEdges;

    private static int[] BandEdges(int bins)
    {
        if (_bandEdges != null) return _bandEdges;
        var edges = new int[NumBands + 1];
        double lo = 250, hi = 1950, binHz = SampleRate / 2.0 / bins;
        for (int b = 0; b <= NumBands; b++)
            edges[b] = Math.Clamp((int)Math.Round(lo * Math.Pow(hi / lo, b / (double)NumBands) / binHz), 1, bins - 1);
        for (int b = 1; b <= NumBands; b++) if (edges[b] <= edges[b - 1]) edges[b] = Math.Min(bins - 1, edges[b - 1] + 1);
        return _bandEdges = edges;
    }

    private static uint[] ComputeDifferential(short[] pcm)
    {
        int frameCount = (pcm.Length - FrameSize) / HopSize + 1;
        var energies = new float[frameCount, NumBands];
        var totals = new float[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            var mags = FftProcessor.ComputeMagnitudes(pcm, f * HopSize, FrameSize);
            var edges = BandEdges(mags.Length);
            float total = 0;
            for (int b = 0; b < NumBands; b++)
            {
                float e = 0;
                for (int i = edges[b]; i < edges[b + 1]; i++) e += mags[i] * mags[i];
                energies[f, b] = e;
                total += e;
            }
            totals[f] = total;
        }

        // Segnale presente: sopra una frazione dell'energia tipica della zona (il silenzio e il rumore di fondo non
        // devono combaciare fra episodi diversi: prima due pause qualsiasi sembravano la stessa sigla).
        var sorted = (float[])totals.Clone();
        Array.Sort(sorted);
        float typical = sorted[(int)(sorted.Length * 0.75)];
        float floor = Math.Max(typical * 0.003f, 1e-7f);

        var fingerprint = new uint[frameCount];
        for (int f = 1; f < frameCount; f++)
        {
            uint hash = 0;
            for (int b = 0; b < NumBands - 1; b++)
            {
                float d = energies[f, b] - energies[f, b + 1] - (energies[f - 1, b] - energies[f - 1, b + 1]);
                if (d > 0) hash |= 1u << b;
            }
            if (totals[f] > floor && totals[f - 1] > floor) hash |= 0x80000000u;
            fingerprint[f] = hash;
        }
        fingerprint[0] = fingerprint.Length > 1 ? fingerprint[1] & 0x7FFFFFFFu : 0;
        return fingerprint;
    }
#endif

    // ──────────────────────────────────────────────
    // Internal helpers
    // ──────────────────────────────────────────────

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
