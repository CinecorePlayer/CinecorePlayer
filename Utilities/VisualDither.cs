#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Dithering leggerissimo condiviso per i gradienti scuri GDI+.
    /// La texture è deterministica, non anima e quindi non produce flicker.
    /// </summary>
    internal static class VisualDither
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<int, Bitmap> Tiles = new();

        public static void Overlay(Graphics graphics, Rectangle bounds, int strength = 3)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            strength = Math.Clamp(strength, 1, 5);
            Bitmap tile;
            lock (Sync)
            {
                if (!Tiles.TryGetValue(strength, out tile!))
                {
                    tile = CreateTile(strength);
                    Tiles[strength] = tile;
                }
            }

            GraphicsState state = graphics.Save();
            try
            {
                graphics.CompositingMode = CompositingMode.SourceOver;
                using var brush = new TextureBrush(tile, WrapMode.Tile);
                brush.TranslateTransform(bounds.Left, bounds.Top);
                graphics.FillRectangle(brush, bounds);
            }
            finally { graphics.Restore(state); }
        }

        /// <summary>
        /// Rumore triangolare di ±<paramref name="amplitude"/> livelli sui pixel opachi di una
        /// bitmap 32bpp gia' disegnata: rompe i gradini delle sfumature scure e delle sfocature
        /// senza sovrapporre una trama. Stesso valore sui tre canali, cosi' non compaiono puntini colorati.
        /// </summary>
        public static unsafe void AddNoise(Bitmap bitmap, int amplitude = 1, byte floor = 0)
        {
            if (bitmap.Width <= 0 || bitmap.Height <= 0) return;
            amplitude = Math.Clamp(amplitude, 1, 4);
            var rect = new Rectangle(Point.Empty, bitmap.Size);
            var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < rect.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                    for (int x = 0; x < rect.Width; x++)
                    {
                        byte* p = row + x * 4;
                        if (p[3] != 255) continue;
                        uint hash = unchecked((uint)(x * 374761393 + y * 668265263));
                        hash ^= hash >> 15; hash *= 0x2c1b3c6du; hash ^= hash >> 12; hash *= 0x297a2d39u; hash ^= hash >> 15;
                        int d = 0;
                        for (int k = 0; k < amplitude; k++)
                            d += (int)((hash >> (2 * k)) & 1u) - (int)((hash >> (2 * k + 1)) & 1u);
                        if (d == 0 && floor == 0) continue;
                        p[0] = (byte)Math.Clamp(p[0] + d, floor, 255);
                        p[1] = (byte)Math.Clamp(p[1] + d, floor, 255);
                        p[2] = (byte)Math.Clamp(p[2] + d, floor, 255);
                    }
                }
            }
            finally { bitmap.UnlockBits(data); }
        }

        private static Bitmap CreateTile(int strength)
        {
            // Dimensione non potenza di due: evita che il tiling si riallinei con
            // griglie di pixel, scaling e blocchi di compressione dello screenshot.
            const int size = 127;
            var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    uint hash = unchecked((uint)(x * 374761393 + y * 668265263));
                    hash ^= hash >> 16;
                    hash *= 0x7feb352du;
                    hash ^= hash >> 15;
                    hash *= 0x846ca68bu;
                    hash ^= hash >> 16;
                    int bucket = (int)((hash >> 24) & 7u);
                    if (bucket is 3 or 4)
                    {
                        bitmap.SetPixel(x, y, Color.Transparent);
                        continue;
                    }
                    bool lift = bucket < 3;
                    // 4-5: per le sfumature verso il chiaro, dove un solo livello non basta a
                    // rompere i gradini (±1-2 livelli, invisibile come grana).
                    int alpha = strength switch { >= 5 => 4, 4 => 3, _ => 1 };
                    bitmap.SetPixel(x, y, Color.FromArgb(alpha, lift ? 255 : 0, lift ? 255 : 0, lift ? 255 : 0));
                }
            }
            return bitmap;
        }
    }
}
