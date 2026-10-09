#nullable enable
using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities;

// Music providers publish artist fanart separately from square album covers.
// Never read the legacy MusicLandscape cache, which contained game screenshots.
internal static class MusicLandscapeArtworkService
{
    public static async Task<string?> ResolveAsync(string title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null;
        var artwork = await MusicArtistArtworkService.ResolveAsync(artist, title).ConfigureAwait(false);
        try
        {
            if (!File.Exists(artwork.Landscape)) return null;
            using var image = Image.FromFile(artwork.Landscape!);
            return IsLandscape(image.Width, image.Height) ? artwork.Landscape : null;
        }
        catch { return null; }
    }
    internal static bool IsLandscape(int width, int height) => width >= 1000 && height > 0 && width / (double)height >= 1.65;
}
