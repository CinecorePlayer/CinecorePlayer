#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace CinecorePlayer2025.Utilities
{
    // Applied once to cached artwork, never on an animation frame. Blend in float
    // and dither BEFORE the final 8-bit quantization, avoiding GDI+'s alpha bands.
    internal static class CinematicVignette
    {
        public static unsafe void Apply(Bitmap bitmap, bool card)
        {
            int width = bitmap.Width, height = bitmap.Height;
            var horizontal = new float[width];
            var vertical = new float[height];
            for (int x = 0; x < width; x++)
            {
                float t = x / (float)Math.Max(1, width - 1);
                horizontal[x] = card
                    ? 1f - .40f * (1f - Smooth(0, .28f, t)) - .34f * Smooth(.72f, 1, t)
                    : .01f + .87f * Smooth(.23f, .87f, t);
            }
            for (int y = 0; y < height; y++)
            {
                float t = y / (float)Math.Max(1, height - 1);
                vertical[y] = card
                    ? 1f - Smooth(.12f, .86f, t)
                    : (1f - .42f * (1f - Smooth(0, .18f, t))) * (1f - Smooth(.42f, .97f, t));
            }
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < width; x++)
                    {
                        byte* p = row + x * 4;
                        if (p[3] == 0) continue;
                        float transmission = horizontal[x] * vertical[y];
                        uint hash = unchecked((uint)(x * 374761393 + y * 668265263));
                        hash = unchecked((hash ^ (hash >> 13)) * 1274126177u);
                        hash ^= hash >> 16;
                        float noise = ((hash & 65535) / 65535f - .5f) * 1.6f;
                        float alpha = p[3] / 255f;
                        for (int c = 0; c < 3; c++)
                        {
                            float shade = (c == 0 ? 16 : c == 1 ? 9 : 3) * alpha;
                            float value = shade + (p[c] - shade) * transmission + noise * alpha;
                            p[c] = (byte)Math.Clamp((int)(value + .5f), 0, p[3]);
                        }
                    }
                }
            }
            finally { bitmap.UnlockBits(data); }
        }

        private static float Smooth(float start, float end, float value)
        {
            float t = Math.Clamp((value - start) / (end - start), 0, 1);
            return t * t * (3 - 2 * t);
        }
    }
}
