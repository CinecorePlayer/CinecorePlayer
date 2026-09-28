#nullable enable
using SkiaSharp;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CinecorePlayer2025.Utilities
{
    // Both overlays use the actual page behind them. Blur at a reduced resolution,
    // with a halo around the crop, so the edges neither smear nor need a full-screen filter.
    internal static class GlassSurface
    {
        internal static Bitmap Create(Bitmap background, Rectangle bounds, Image? artwork = null)
        {
            Rectangle crop = Rectangle.Intersect(Rectangle.Inflate(bounds, 48, 48), new Rectangle(Point.Empty, background.Size));
            if (crop.Width < 1 || crop.Height < 1 || bounds.Width < 1 || bounds.Height < 1)
                throw new ArgumentOutOfRangeException(nameof(bounds));
            const int reduction = 6;
            var size = new Size(Math.Max(1, (crop.Width + reduction - 1) / reduction), Math.Max(1, (crop.Height + reduction - 1) / reduction));
            using var small = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var canvas = Graphics.FromImage(small))
            {
                canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                canvas.DrawImage(background, new Rectangle(Point.Empty, size), crop, GraphicsUnit.Pixel);
                if (artwork != null)
                {
                    // Diffuse colours from the playing album remain visible even when
                    // the page behind the glass is empty, as in the Tidal reference.
                    using var colours = new Bitmap(8, 8);
                    using (var palette = Graphics.FromImage(colours))
                    { palette.InterpolationMode = InterpolationMode.HighQualityBicubic; palette.DrawImage(artwork, new Rectangle(0,0,8,8)); }
                    using var opacity = new ImageAttributes();
                    opacity.SetColorMatrix(new ColorMatrix { Matrix33 = .38f });
                    canvas.DrawImage(colours, new Rectangle(Point.Empty,size), 0,0,8,8,GraphicsUnit.Pixel,opacity);
                }
            }
            var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var data = small.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                using var pixels = new SKPixmap(info, data.Scan0, data.Stride);
                using var image = SKImage.FromPixels(pixels);
                using var filter = SKImageFilter.CreateBlur(3.4f, 3.4f, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { ImageFilter = filter };
                surface.Canvas.DrawImage(image, 0, 0, paint);
            }
            finally { small.UnlockBits(data); }
            using var blurred = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using var snapshot = surface.Snapshot();
            var output = blurred.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { snapshot.ReadPixels(info, output.Scan0, output.Stride, 0, 0); }
            finally { blurred.UnlockBits(output); }
            var result = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (var canvas = Graphics.FromImage(result))
            {
                canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var source = new RectangleF((bounds.X-crop.X)*size.Width/(float)crop.Width, (bounds.Y-crop.Y)*size.Height/(float)crop.Height,
                    bounds.Width*size.Width/(float)crop.Width, bounds.Height*size.Height/(float)crop.Height);
                using var attributes = new ImageAttributes(); attributes.SetWrapMode(WrapMode.TileFlipXY);
                canvas.DrawImage(blurred, new Rectangle(Point.Empty, result.Size), source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                // A light neutral dark tint preserves the background's colours. No grey/white slab.
                using var tint = new SolidBrush(HUD.Theme.IsLight ? Color.FromArgb(92, 247, 248, 250) : Color.FromArgb(48, 20, 24, 30));
                canvas.FillRectangle(tint, new Rectangle(Point.Empty, result.Size));
            }
            return result;
        }
    }
}
