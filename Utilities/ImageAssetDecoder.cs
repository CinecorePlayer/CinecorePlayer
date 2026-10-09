using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using SkiaSharp;

namespace CinecorePlayer2025.Utilities;

internal static class ImageAssetDecoder
{
    // GDI+ supports JPEG/TIFF/PNG; Skia adds WebP artwork used by DLNA servers.
    internal static Bitmap Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Load(stream);
    }

    internal static Bitmap Load(Stream stream)
    {
        using var source = Read(stream);
        return new Bitmap(source);
    }

    internal static Image Read(Stream stream)
    {
        try
        {
            var source = Image.FromStream(stream, false, true);
            try
            {
                if (source.GetPropertyItem(0x0112)?.Value is { Length: >= 2 } orientation)
                {
                    var transform = orientation[0] switch
                    {
                        2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone,
                        4 => RotateFlipType.Rotate180FlipX, 5 => RotateFlipType.Rotate90FlipX,
                        6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
                        8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone
                    };
                    if (transform != RotateFlipType.RotateNoneFlipNone) source.RotateFlip(transform);
                }
            }
            catch (System.ArgumentException) { }
            return source;
        }
        catch (System.ArgumentException) when (stream.CanSeek) { stream.Position = 0; }
        using var decoded = SKBitmap.Decode(stream) ?? throw new InvalidDataException("Immagine non valida.");
        return ToGdiBitmap(decoded);
    }

    /// <summary>
    /// Decodes a photo for on-screen display: EXIF orientation applied, never larger than
    /// <paramref name="fit"/> (contain) unless <paramref name="fit"/> is empty, and stored as
    /// 32bpp premultiplied ARGB, the only format GDI+ blits without per-pixel conversion.
    /// JPEG sources are decoded directly at a reduced DCT scale, which is several times faster
    /// than decoding the full sensor resolution and shrinking it afterwards.
    /// </summary>
    internal static Bitmap LoadForDisplay(byte[]? data, string? path, Size fit, out Size sourceSize)
    {
        using SKCodec? codec = data != null
            ? SKCodec.Create(SKData.CreateCopy(data))
            : path != null ? SKCodec.Create(path) : null;
        if (codec != null)
        {
            var bitmap = DecodeWithSkia(codec, fit, out sourceSize);
            if (bitmap != null) return bitmap;
        }

        // TIFF and other formats Skia does not read: GDI+ decode, then a single resample.
        using Stream stream = data != null
            ? new MemoryStream(data, writable: false)
            : new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        using var source = Read(stream);
        sourceSize = source.Size;
        Size target = FitWithin(source.Size, fit);
        var result = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(result))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = target == source.Size ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(Point.Empty, target), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }

    private static Bitmap? DecodeWithSkia(SKCodec codec, Size fit, out Size sourceSize)
    {
        SKEncodedOrigin origin = codec.EncodedOrigin;
        bool swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        SKSizeI raw = codec.Info.Size;
        sourceSize = swapsAxes ? new Size(raw.Height, raw.Width) : new Size(raw.Width, raw.Height);
        if (raw.Width <= 0 || raw.Height <= 0) return null;

        Size target = FitWithin(sourceSize, fit);
        float wanted = sourceSize.Width > 0 ? target.Width / (float)sourceSize.Width : 1f;
        SKSizeI decodeSize = raw;
        if (wanted < 1f)
        {
            // JPEG rounds the requested scale up to the next 1/8 step; never accept less
            // than the display needs, otherwise the photo would be upscaled and soft.
            SKSizeI scaled = codec.GetScaledDimensions(wanted);
            if (scaled.Width >= Math.Ceiling(raw.Width * wanted) && scaled.Height >= Math.Ceiling(raw.Height * wanted))
                decodeSize = scaled;
        }

        var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        SKCodecResult status = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (status is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

        var result = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppPArgb);
        var locked = result.LockBits(new Rectangle(Point.Empty, target), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var targetInfo = new SKImageInfo(target.Width, target.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(targetInfo, locked.Scan0, locked.Stride)
                ?? throw new InvalidOperationException("Superficie Skia non disponibile.");
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            int orientedW = swapsAxes ? decodeSize.Height : decodeSize.Width;
            int orientedH = swapsAxes ? decodeSize.Width : decodeSize.Height;
            canvas.Scale(target.Width / (float)orientedW, target.Height / (float)orientedH);
            canvas.Concat(OriginMatrix(origin, decodeSize.Width, decodeSize.Height));
            float ratio = Math.Max(orientedW / (float)target.Width, orientedH / (float)target.Height);
            // The JPEG DCT scale already lands within 2x of the target, where bilinear is
            // clean; larger ratios (PNG, WebP) need mipmaps to avoid aliasing.
            var sampling = ratio <= 1.001f
                ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
                : ratio <= 2f
                    ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
                    : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            using var image = SKImage.FromBitmap(decoded);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(image, 0, 0, sampling, paint);
            canvas.Flush();
        }
        finally
        {
            result.UnlockBits(locked);
        }
        return result;
    }

    private static SKMatrix OriginMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity
    };

    private static Size FitWithin(Size source, Size fit)
    {
        if (fit.Width <= 0 || fit.Height <= 0 || source.Width <= 0 || source.Height <= 0) return source;
        double scale = Math.Min(1.0, Math.Min(fit.Width / (double)source.Width, fit.Height / (double)source.Height));
        return new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
    }

    private static Bitmap ToGdiBitmap(SKBitmap decoded)
    {
        var result = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppPArgb);
        var locked = result.LockBits(new Rectangle(0, 0, decoded.Width, decoded.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var pixmap = decoded.PeekPixels();
            if (pixmap == null || !pixmap.ReadPixels(info, locked.Scan0, locked.Stride, 0, 0))
                throw new InvalidDataException("Immagine non valida.");
        }
        finally
        {
            result.UnlockBits(locked);
        }
        return result;
    }
}
