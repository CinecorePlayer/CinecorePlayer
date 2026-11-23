namespace IntroOutroDetector.Audio;

/// <summary>
/// Lightweight in-place Cooley-Tukey FFT for power-of-2 sizes.
/// </summary>
public static class FftProcessor
{
    /// <summary>
    /// Compute the magnitude spectrum of a PCM frame.
    /// Applies a Hann window before transform.
    /// </summary>
    /// <param name="samples">PCM buffer</param>
    /// <param name="offset">Start sample index in buffer</param>
    /// <param name="length">Number of samples (should be a power of 2)</param>
    /// <returns>Magnitude array of length/2 bins</returns>
    public static float[] ComputeMagnitudes(short[] samples, int offset, int length)
    {
        // Next power of 2
        int n = 1;
        while (n < length) n <<= 1;

        var re = new float[n];
        var im = new float[n];

        int limit = Math.Min(length, samples.Length - offset);
        for (int i = 0; i < limit; i++)
        {
            // Hann window
            float w = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (length - 1)));
            re[i] = samples[offset + i] / 32768f * w;
        }

        ComputeFFT(re, im);

        int half = n / 2;
        var mags = new float[half];
        for (int i = 0; i < half; i++)
            mags[i] = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);

        return mags;
    }

    private static void ComputeFFT(float[] re, float[] im)
    {
        int n = re.Length;

        // Bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // Butterfly stages
        for (int len = 2; len <= n; len <<= 1)
        {
            float ang = -2f * MathF.PI / len;
            float wBaseRe = MathF.Cos(ang);
            float wBaseIm = MathF.Sin(ang);
            int half = len >> 1;

            for (int i = 0; i < n; i += len)
            {
                float wRe = 1f, wIm = 0f;

                for (int j = 0; j < half; j++)
                {
                    float uRe = re[i + j];
                    float uIm = im[i + j];
                    float vRe = re[i + j + half] * wRe - im[i + j + half] * wIm;
                    float vIm = re[i + j + half] * wIm + im[i + j + half] * wRe;

                    re[i + j] = uRe + vRe;
                    im[i + j] = uIm + vIm;
                    re[i + j + half] = uRe - vRe;
                    im[i + j + half] = uIm - vIm;

                    float newWRe = wRe * wBaseRe - wIm * wBaseIm;
                    wIm = wRe * wBaseIm + wIm * wBaseRe;
                    wRe = newWRe;
                }
            }
        }
    }
}
