using System.Drawing;
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
        using var image = SKImage.FromBitmap(decoded);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var png = new MemoryStream(data.ToArray());
        using var bitmap = Image.FromStream(png);
        return new Bitmap(bitmap);
    }
}
