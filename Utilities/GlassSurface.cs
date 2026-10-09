#nullable enable
using SkiaSharp;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CinecorePlayer2025.Utilities
{
    // Materiale come la barra e la coda di Tidal: sfocatura profonda della pagina dietro,
    // velatura scura quasi coprente, niente riflessi. La sfocatura si fa a 1/4 della
    // risoluzione con un alone attorno al ritaglio (i bordi non si impastano), si riporta a
    // piena risoluzione con un filtro cubico (niente blocchi) e si rompono i gradini con il rumore.
    internal static class GlassSurface
    {
        private const int Margin = 64, Reduction = 4;
        private const float BlurSigma = 30f; // in pixel dello schermo
        private const byte LiquidTintAlpha = 118;

        /// <summary>Il vetro di barra musicale, coda e menu a tendina: un solo materiale.</summary>
        internal static Bitmap CreateLiquid(Bitmap background, Rectangle bounds, bool hairline, int noise = 2)
            => Create(background, bounds, null, hairline, noise: noise, saturation: 1.5f, liquid: true);

        /// <param name="tintAlpha">Opacita' della velatura (default 184 scuro / 196 chiaro).</param>
        /// <param name="artworkWash">Quanto i colori della copertina tingono il vetro.</param>
        /// <param name="noise">Ampiezza del dithering in livelli.</param>
        /// <param name="liquid">Vetro piu' trasparente ("liquid glass"): prima della velatura le
        /// luci (tema scuro) o le ombre (tema chiaro) dello sfondo sfocato vengono compresse con
        /// una curva morbida, cosi' una velatura leggera lascia vedere i colori senza togliere
        /// contrasto a testo e icone; il bordo superiore ha un filo d'ombra e uno di luce.</param>
        internal static Bitmap Create(Bitmap background, Rectangle bounds, Image? artwork = null, bool hairline = true,
            byte? tintAlpha = null, float artworkWash = .30f, int noise = 2, float saturation = 1.2f, bool liquid = false)
        {
            Rectangle crop = Rectangle.Intersect(Rectangle.Inflate(bounds, Margin, Margin), new Rectangle(Point.Empty, background.Size));
            if (crop.Width < 1 || crop.Height < 1 || bounds.Width < 1 || bounds.Height < 1)
                throw new ArgumentOutOfRangeException(nameof(bounds));
            var size = new Size(Math.Max(1, (crop.Width + Reduction - 1) / Reduction), Math.Max(1, (crop.Height + Reduction - 1) / Reduction));
            using var small = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var canvas = Graphics.FromImage(small))
            using (var wrap = new ImageAttributes())
            {
                // TileFlipXY: senza, il bicubico di GDI+ sfuma i bordi verso il trasparente e il
                // vetro prendeva un alone scuro lungo i lati.
                wrap.SetWrapMode(WrapMode.TileFlipXY);
                canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
                canvas.DrawImage(background, new Rectangle(Point.Empty, size), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, wrap);
                if (artwork != null && !liquid)
                {
                    // I colori della copertina restano appena percepibili dietro al vetro, anche
                    // quando la pagina dietro e' vuota.
                    using var wash = new ImageAttributes();
                    wash.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(artworkWash, 0f, 1f) });
                    wash.SetWrapMode(WrapMode.TileFlipXY);
                    canvas.DrawImage(artwork, new Rectangle(Point.Empty, size), 0, 0, artwork.Width, artwork.Height, GraphicsUnit.Pixel, wash);
                }
            }

            var smallInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var finalInfo = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var blurSurface = SKSurface.Create(smallInfo);
            var data = small.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                using var pixels = new SKPixmap(smallInfo, data.Scan0, data.Stride);
                using var image = SKImage.FromPixels(pixels);
                using var blur = SKImageFilter.CreateBlur(BlurSigma / Reduction, BlurSigma / Reduction, SKShaderTileMode.Clamp);
                // Saturazione +20%: dietro al vetro i colori restano vivi invece di ingrigire.
                float s = saturation, lr = .2126f * (1 - s), lg = .7152f * (1 - s), lb = .0722f * (1 - s);
                using var saturate = SKColorFilter.CreateColorMatrix(new[]
                {
                    lr + s, lg, lb, 0, 0,
                    lr, lg + s, lb, 0, 0,
                    lr, lg, lb + s, 0, 0,
                    0, 0, 0, 1, 0
                });
                using var paint = new SKPaint { ImageFilter = blur, ColorFilter = saturate };
                blurSurface.Canvas.Clear(SKColors.Black);
                blurSurface.Canvas.DrawImage(image, 0, 0, paint);
                blurSurface.Canvas.Flush();
            }
            finally { small.UnlockBits(data); }

            using var blurred = blurSurface.Snapshot();
            using var finalSurface = SKSurface.Create(finalInfo);
            var c = finalSurface.Canvas;
            var source = new SKRect(
                (bounds.X - crop.X) * size.Width / (float)crop.Width,
                (bounds.Y - crop.Y) * size.Height / (float)crop.Height,
                (bounds.Right - crop.X) * size.Width / (float)crop.Width,
                (bounds.Bottom - crop.Y) * size.Height / (float)crop.Height);
            Color panel = HUD.Theme.Panel;
            bool light = HUD.Theme.IsLight;
            c.Clear(SKColors.Black);
            if (liquid)
            {
                // Curva morbida per canale: nel tema scuro le luci non superano ~130 (una
                // copertina bianca dietro la barra non acceca il testo), nel chiaro le ombre non
                // scendono sotto ~125. I toni medi e i colori restano quasi intatti.
                const double Limit = 165;
                var curve = new byte[256];
                var identity = new byte[256];
                for (int v = 0; v < 256; v++)
                {
                    identity[v] = (byte)v;
                    double input = light ? 255 - v : v;
                    double soft = Limit * (1 - Math.Exp(-input / Limit));
                    curve[v] = (byte)Math.Clamp(Math.Round(light ? 255 - soft : soft), 0, 255);
                }
                using var tone = SKColorFilter.CreateTable(identity, curve, curve, curve);
                using var tonePaint = new SKPaint { ColorFilter = tone };
                c.DrawImage(blurred, source, new SKRect(0, 0, bounds.Width, bounds.Height), new SKSamplingOptions(SKCubicResampler.Mitchell), tonePaint);
            }
            else
                c.DrawImage(blurred, source, new SKRect(0, 0, bounds.Width, bounds.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));

            // Velatura: nel vetro "liquid" e' neutra (grafite, come Tidal) e leggera, cosi' il vetro
            // e' dello stesso colore su tutta la lastra e lascia passare i colori della pagina;
            // altrimenti e' nel colore del tema e quasi coprente.
            byte alpha = tintAlpha ?? (liquid ? LiquidTintAlpha : (byte)(light ? 196 : 184));
            if (liquid) panel = light ? Color.FromArgb(238, 242, 248) : Color.FromArgb(40, 42, 46);
            if (liquid && light && tintAlpha == null) alpha = 92;
            using (var tint = new SKPaint { Color = new SKColor(panel.R, panel.G, panel.B, alpha) })
                c.DrawRect(0, 0, bounds.Width, bounds.Height, tint);
            if (hairline && liquid)
            {
                // Bordo del vetro: un filo d'ombra che lo stacca dalla pagina e, subito sotto,
                // un filo di luce. Linee piene da un pixel, uniformi su tutta la larghezza.
                using var shade = new SKPaint { Color = light ? new SKColor(0, 0, 0, 30) : new SKColor(0, 0, 0, 90) };
                using var shine = new SKPaint { Color = light ? new SKColor(255, 255, 255, 190) : new SKColor(255, 255, 255, 34) };
                c.DrawRect(0, 0, bounds.Width, 1, shade);
                c.DrawRect(0, 1, bounds.Width, 1, shine);
            }
            else if (hairline)
            {
                // Un solo filo sottile in alto separa il vetro dalla pagina, come in Tidal.
                using var line = new SKPaint { Color = light ? new SKColor(0, 0, 0, 22) : new SKColor(255, 255, 255, 20) };
                c.DrawRect(0, 0, bounds.Width, 1, line);
            }
            c.Flush();

            var result = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (var snapshot = finalSurface.Snapshot())
            {
                var output = result.LockBits(new Rectangle(Point.Empty, result.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try { snapshot.ReadPixels(finalInfo, output.Scan0, output.Stride, 0, 0); }
                finally { result.UnlockBits(output); }
            }
            if (noise > 0) VisualDither.AddNoise(result, noise); // ±2 di default: con ±1 il banding del vetro scuro restava visibile
            return result;
        }
    }
}
