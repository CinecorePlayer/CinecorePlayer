using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using SkiaSharp;

namespace CinecorePlayer2025.Utilities;

/// <summary>
/// MusicLandscapeArtworkService di Windows con SkiaSharp al posto di GDI (che sul Mac non c'e'): stesse soglie per le
/// foto larghe degli artisti e stesso sfondo panoramico ricavato dalla copertina (sfocata e allargata, con grana).
/// </summary>
internal static class MusicLandscapeArtworkService
{
    private sealed record Dimensions(long Length, DateTime Written, int Width, int Height);
    private static readonly ConcurrentDictionary<string, Dimensions> Sizes = new(StringComparer.OrdinalIgnoreCase);

    internal static int ShortSide(string? path) => ReadDimensions(path) is { } d ? Math.Min(d.Width, d.Height) : 0;

    private static Dimensions? ReadDimensions(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (Sizes.TryGetValue(path, out var known) && known.Length == file.Length && known.Written == file.LastWriteTimeUtc) return known;
            using var codec = SKCodec.Create(path);
            if (codec == null) return null;
            var result = new Dimensions(file.Length, file.LastWriteTimeUtc, codec.Info.Width, codec.Info.Height);
            if (Sizes.Count > 2048) Sizes.Clear();
            Sizes[path] = result;
            return result;
        }
        catch { return null; }
    }

    internal const int MinimumWidth = 1920;
    internal const int MinimumHeight = 1080;
    internal const int MinimumBannerFallbackSide = 750;

    public static async Task<string?> ResolveAsync(string title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null;
        var artwork = await MusicArtistArtworkService.ResolveAsync(artist, title).ConfigureAwait(false);
        try { return IsLandscapeFile(artwork.Landscape) ? artwork.Landscape : null; }
        catch { return null; }
    }

    internal static bool IsLandscape(int width, int height) => width >= MinimumWidth && height >= 1000 && width / (double)height >= 1.45;

    internal static bool IsLandscapeFile(string? path)
    {
        var size = ReadDimensions(path);
        return size != null && IsLandscape(size.Width, size.Height);
    }

    internal static string CoverBackdropPath(string cover)
    {
        var file = new FileInfo(cover);
        string key = $"cover-backdrop-v3|{file.FullName}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}".ToLowerInvariant();
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "AlbumBackdrops", hash + ".jpg");
    }

    internal static bool CreateCoverBackdrop(string cover, string target)
    {
        try
        {
            using var source = SKBitmap.Decode(cover);
            if (source == null || source.Width < 32 || source.Height < 32) return false;
            const int W = MinimumWidth, H = MinimumHeight;
            using var sampling = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            int cropH = Math.Min(source.Height, (int)Math.Round(source.Width * 9 / 16.0));
            using var tiny = new SKBitmap(64, 36);
            using (var c = new SKCanvas(tiny))
                c.DrawBitmap(source, new SKRect(0, (source.Height - cropH) / 2f, source.Width, (source.Height - cropH) / 2f + cropH), new SKRect(0, 0, 64, 36), sampling);
            using var mid = new SKBitmap(256, 144);
            using (var c = new SKCanvas(mid))
                c.DrawBitmap(tiny, new SKRect(0, 0, 64, 36), new SKRect(0, 0, 256, 144), sampling);
            using var result = new SKBitmap(W, H);
            using (var c = new SKCanvas(result))
            {
                c.DrawBitmap(mid, new SKRect(0, 0, 256, 144), new SKRect(0, 0, W, H), sampling);
                // Grana leggera: sui gradienti cosi' ampi il JPEG altrimenti fa le fasce.
                var random = new Random(cover.GetHashCode());
                using var black = new SKPaint { Color = new SKColor(0, 0, 0, 10) };
                using var white = new SKPaint { Color = new SKColor(255, 255, 255, 10) };
                for (int i = 0; i < 9000; i++) c.DrawRect(random.Next(W), random.Next(H), 2, 2, random.Next(2) == 0 ? black : white);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var data = result.Encode(SKEncodedImageFormat.Jpeg, 92))
                using (var fs = File.Create(temp)) data.SaveTo(fs);
                File.Move(temp, target, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return true;
        }
        catch (Exception ex) { Dbg.Warn("[MUSIC ART] cover backdrop: " + ex.Message); return false; }
    }

    internal static bool IsBannerArtworkFile(string? path)
    {
        var size = ReadDimensions(path);
        return size != null && (IsLandscape(size.Width, size.Height) || size.Width >= MinimumBannerFallbackSide && size.Height >= MinimumBannerFallbackSide);
    }
}

/// <summary>Miniatura 32x32 RGB per riconoscere due foto uguali (PhotoPixels di MusicArtistArtworkService).</summary>
internal static class ArtworkPixels
{
    public static byte[] Rgb32(byte[] data)
    {
        using var source = SKBitmap.Decode(data) ?? throw new InvalidDataException("immagine illeggibile");
        using var small = new SKBitmap(32, 32);
        using (var c = new SKCanvas(small))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
            c.DrawBitmap(source, new SKRect(0, 0, source.Width, source.Height), new SKRect(0, 0, 32, 32), paint);
        var pixels = new byte[32 * 32 * 3]; int i = 0;
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) { var p = small.GetPixel(x, y); pixels[i++] = p.Red; pixels[i++] = p.Green; pixels[i++] = p.Blue; }
        return pixels;
    }
}
