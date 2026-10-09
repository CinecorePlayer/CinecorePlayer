#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Drawing;
using System.IO;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private bool DrawIcon(Graphics graphics, Rectangle bounds, string key, Color color)
        {
            string? path = ResolveIconPath(key);
            if (string.IsNullOrWhiteSpace(path))
                return false;

            Image? icon = string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase)
                ? GetSvgIconBitmap(path, Math.Max(bounds.Width, bounds.Height), color)
                : LoadImage(path);
            if (icon == null)
                return false;

            graphics.DrawImage(icon, ContainDestination(icon.Size, bounds));
            return true;
        }

        private Bitmap? GetSvgIconBitmap(string svgPath, int sizePx, Color tint)
        {
            try
            {
                sizePx = Math.Max(1, sizePx);
                var cacheKey = (Path: svgPath, SizePx: sizePx, Argb: tint.ToArgb());
                if (_svgIconCache.TryGetValue(cacheKey, out Bitmap? cached) && cached != null)
                    return cached;

                Bitmap rendered = AssetIconService.RenderSvg(svgPath, sizePx, tint);
                _svgIconCache[cacheKey] = rendered;
                return rendered;
            }
            catch
            {
                return null;
            }
        }

        private static bool HasIconAsset(string key)
            => !string.IsNullOrWhiteSpace(ResolveIconPath(key));

        private static string? ResolveIconPath(string key)
            => AssetIconService.Resolve(key);
    }
}
