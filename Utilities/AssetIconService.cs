#nullable enable
using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Unico punto di accesso agli asset grafici. Evita che ogni pagina usi una
    /// propria lista di cartelle e che le bitmap SVG dipendano da stream gia' chiusi.
    /// </summary>
    internal static class AssetIconService
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, string> ResolvedPaths = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Bitmap> CustomBitmaps = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string?> CustomPaths = new(StringComparer.OrdinalIgnoreCase);

        private static string? CustomPath(string key)
        {
            if (!Regex.IsMatch(key, @"^[a-z0-9_-]+$", RegexOptions.IgnoreCase)) return null;
            foreach (string assets in AssetDirectories())
            {
                string file = Path.Combine(assets, "Icons.Custom", key + ".svg");
                if (File.Exists(file)) return file;
            }
            return null;
        }

        internal static bool DrawCustom(Graphics graphics, Rectangle bounds, string key, Color tint)
        {
            lock (Sync)
            {
                if (!CustomPaths.TryGetValue(key, out var path))
                {
                    path = Resolve(key);
                    CustomPaths[key] = path;
                }
                if (path == null) return false;
                string cacheKey = path + "|" + bounds.Width + "|" + bounds.Height + "|" + tint.ToArgb();
                try
                {
                    if (!CustomBitmaps.TryGetValue(cacheKey, out var bitmap))
                        CustomBitmaps[cacheKey] = bitmap = RenderSvg(path, Math.Max(bounds.Width, bounds.Height), tint);
                    graphics.DrawImage(bitmap, bounds);
                    if (CustomBitmaps.Count > 384)
                    {
                        foreach (string stale in CustomBitmaps.Keys.Where(x => x != cacheKey).Take(64).ToArray())
                        {
                            CustomBitmaps[stale].Dispose(); CustomBitmaps.Remove(stale);
                        }
                    }
                    return true;
                }
                catch { return false; }
            }
        }

        private static readonly Dictionary<string, string> KnownIcons = new(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = "hud_icons/home.svg",
            ["home-2"] = "icons/home-2.svg",
            ["library"] = "icons/library.svg",
            ["grid"] = "hud_icons/grid.svg",
            ["cube"] = "hud_icons/grid.svg",
            ["movie"] = "hud_icons/movie.svg",
            ["movies"] = "hud_icons/movie.svg",
            ["film"] = "hud_icons/movie.svg",
            ["video"] = "hud_icons/movie.svg",
            ["tv"] = "hud_icons/tv.svg",
            ["monitor"] = "hud_icons/tv.svg",
            ["renderer"] = "hud_icons/tv.svg",
            ["music"] = "hud_icons/music.svg",
            ["audio"] = "hud_icons_new/music-bars-svgrepo-com.svg",
            ["photo"] = "hud_icons/photo.svg",
            ["photos"] = "hud_icons/photo.svg",
            ["image"] = "hud_icons/photo.svg",
            ["star"] = "hud_icons/star.svg",
            ["favourite"] = "hud_icons/star.svg",
            ["favourites"] = "hud_icons/star.svg",
            ["favorite"] = "hud_icons/star.svg",
            ["favorites"] = "hud_icons/star.svg",
            ["playlist"] = "hud_icons/playlist.svg",
            ["playlists"] = "hud_icons/playlist.svg",
            ["queue"] = "hud_icons_new/queue-svgrepo-com.svg",
            ["search"] = "hud_icons/search.svg",
            ["filter"] = "hud_icons/filter.svg",
            ["sort"] = "hud_icons/sort.svg",
            ["scan"] = "hud_icons/scan.svg",
            ["storage"] = "hud_icons/storage.svg",
            ["computer"] = "hud_icons/computer.svg",
            ["network"] = "hud_icons/network.svg",
            ["youtube"] = "hud_icons/youtube.svg",
            ["web"] = "hud_icons/youtube.svg",
            ["cloud"] = "hud_icons/cloud.svg",
            ["link"] = "hud_icons/link.svg",
            ["clipboard"] = "hud_icons/list.svg",
            ["info"] = "icons/info-circle.svg",
            ["add"] = "hud_icons/add.svg",
            ["plus"] = "hud_icons/add.svg",
            ["folder"] = "hud_icons_new/folder-svgrepo-com.svg",
            ["folder-plus"] = "hud_icons_new/folder-add-svgrepo-com.svg",
            ["settings"] = "hud_icons/settings.svg",
            ["gear"] = "hud_icons/settings.svg",
            ["remote"] = "hud_icons_new/remote-control-tv-svgrepo-com.svg",
            ["pip"] = "hud_icons_new/pip-svgrepo-com.svg",
            ["spotlight"] = "hud_icons_new/sparkles.svg",
            ["netflix"] = "hud_icons_new/netflix-1-logo-svgrepo-com.svg",
            ["language"] = "hud_icons_new/language-translate-svgrepo-com.svg",
            ["translate"] = "hud_icons_new/language-translate-svgrepo-com.svg",
            ["globe"] = "hud_icons_new/language-translate-svgrepo-com.svg",
            ["calendar"] = "hud_icons_new/calendar-svgrepo-com.svg",
            ["time"] = "hud_icons_new/clock-0500-svgrepo-com.svg",
            ["clock"] = "hud_icons_new/clock-0500-svgrepo-com.svg",
            ["quality"] = "hud_icons_new/cinema-film-svgrepo-com.svg",
            ["resolution"] = "hud_icons_new/cinema-film-svgrepo-com.svg",
            ["light"] = "hud_icons_new/clock-0500-svgrepo-com.svg",
            ["cinema"] = "hud_icons_new/cinema-film-svgrepo-com.svg",
            ["exit"] = "hud_icons_new/exit-svgrepo-com.svg",
            ["play"] = "icons/player-play.svg",
            ["player-play"] = "icons/player-play.svg",
            ["pause"] = "icons/player-pause.svg",
            ["stop"] = "icons/player-stop.svg",
            ["eject"] = "icons/player-eject.svg",
            ["skip-back"] = "icons/player-skip-back.svg",
            ["skip-forward"] = "icons/player-skip-forward.svg",
            ["previous"] = "icons/player-track-prev.svg",
            ["prev"] = "icons/player-track-prev.svg",
            ["next"] = "icons/player-track-next.svg",
            ["shuffle"] = "icons/player-shuffle.svg",
            ["repeat-one"] = "icons/player-repeat-one.svg",
            ["volume"] = "icons/volume.svg",
            ["volume-low"] = "icons/volume-2.svg",
            ["volume-high"] = "icons/volume-4.svg",
            ["mute"] = "icons/volume-off.svg",
            ["back"] = "icons/arrow-back.svg",
            ["arrow-back"] = "icons/arrow-back.svg",
            ["reset"] = "icons/arrow-back.svg",
            ["maximize"] = "icons/maximize.svg",
            ["subtitles"] = "hud_icons/list.svg",
            ["cc"] = "hud_icons/list.svg",
            ["chapters"] = "hud_icons/list.svg",
            ["list"] = "hud_icons/list.svg",
            ["loop"] = "icons/player-repeat-one.svg",
            ["repeat"] = "icons/player-repeat-one.svg",
            ["wave"] = "hud_icons_new/music-bars-svgrepo-com.svg",
            ["palette"] = "hud_icons/settings.svg",
            ["key"] = "hud_icons/settings.svg",
            ["trash"] = "hud_icons_new/exit-svgrepo-com.svg",
            ["image-add"] = "hud_icons/add.svg",
            ["check"] = "hud_icons/add.svg",
            ["close"] = "hud_icons_new/exit-svgrepo-com.svg",
            ["heart"] = "Icons.Uniform/heart.svg",
            ["more"] = "Icons.Uniform/more.svg",
            ["queue-add"] = "Icons.Uniform/queue-add.svg",
            ["playlist-add"] = "Icons.Uniform/playlist-add.svg",
            ["keyboard"] = "Icons.Uniform/keyboard.svg",
            ["power"] = "Icons.Uniform/power.svg"
        };

        // Resolve the meaning before choosing an asset, so aliases cannot select
        // a different glyph, weight or custom override in another part of the UI.
        private static readonly Dictionary<string, string> IconAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["home-2"] = "home",
            ["movies"] = "movie", ["film"] = "movie", ["films"] = "movie",
            ["videos"] = "video", 
            ["monitor"] = "tv",  ["series"] = "tv",
            ["tv-series"] = "tv", ["tvseries"] = "tv", ["serie-tv"] = "tv",
            
            ["photos"] = "photo", ["image"] = "photo",
             ["favorite"] = "star", ["favorites"] = "star",
            ["favourite"] = "star", ["favourites"] = "star",
            ["playlists"] = "playlist", 
            ["web"] = "youtube", ["plus"] = "add", ["gear"] = "settings",
            ["translate"] = "language", ["globe"] = "language",
            ["year"] = "calendar", ["date"] = "calendar",
            ["clock"] = "time", ["duration"] = "time", ["length"] = "time",
             
            ["player-play"] = "play", ["prev"] = "previous",
            ["arrow-back"] = "back", ["cc"] = "subtitles",
            ["loop"] = "repeat",
            ["x"] = "close", ["chevron"] = "chevron-right",
            ["fullscreen"] = "maximize", ["system"] = "computer",
            ["cast"] = "person", ["profile"] = "person", ["artist"] = "person"
        };

        private static readonly Dictionary<string, string> LegacyIconKeys = KnownIcons
            .GroupBy(icon => icon.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);

        private static string CanonicalIconKey(string requested)
        {
            string relative = requested.Replace('\\', '/');
            if (relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) relative = relative[7..];
            string key = LegacyIconKeys.TryGetValue(relative, out string? legacy) ? legacy : (relative.StartsWith("Icons.Uniform/", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(relative) : requested);
            key = Regex.Replace(key.ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
            return IconAliases.TryGetValue(key, out string? canonical) ? canonical : key;
        }

        public static string? Resolve(string? keyOrRelativePath)
        {
            if (string.IsNullOrWhiteSpace(keyOrRelativePath))
                return null;

            string requested = keyOrRelativePath.Trim();
            string normalized = CanonicalIconKey(requested);

            lock (Sync)
            {
                // Resolved paths point at the app's own icon files; checking the disk
                // on every draw cost ~13% of a library frame.
                if (ResolvedPaths.TryGetValue(requested, out string? cached))
                    return cached;
                if (ResolvedPaths.TryGetValue(normalized, out cached))
                    return cached;
            }

            IEnumerable<string> relativeCandidates = BuildRelativeCandidates(requested, normalized);
            string? custom = CustomPath(normalized);
            if (custom != null)
            {
                lock (Sync) ResolvedPaths[requested] = ResolvedPaths[normalized] = custom;
                return custom;
            }
            foreach (string assetsDir in AssetDirectories())
            {
                string uniform = Path.Combine(assetsDir, "Icons.Uniform", normalized + ".svg");
                if (File.Exists(uniform))
                {
                    lock (Sync) ResolvedPaths[requested] = ResolvedPaths[normalized] = uniform;
                    return uniform;
                }
                foreach (string relative in relativeCandidates)
                {
                    try
                    {
                        string candidate = Path.GetFullPath(Path.Combine(assetsDir, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!candidate.StartsWith(Path.GetFullPath(assetsDir), StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                            continue;

                        lock (Sync)
                        {
                            ResolvedPaths[requested] = candidate;
                            ResolvedPaths[normalized] = candidate;
                        }
                        return candidate;
                    }
                    catch { }
                }
            }

            return null;
        }

        public static Bitmap RenderSvg(string svgPath, int targetPx, Color tint)
        {
            if (string.IsNullOrWhiteSpace(svgPath) || !File.Exists(svgPath))
                throw new FileNotFoundException("SVG asset not found.", svgPath);

            targetPx = Math.Max(1, targetPx);
            using var svg = new SKSvg();
            svg.Load(svgPath);
            if (svg.Picture == null)
                throw new InvalidOperationException("Invalid SVG asset: " + svgPath);

            SKRect bounds = svg.Picture.CullRect;
            float width = Math.Max(1, bounds.Width);
            float height = Math.Max(1, bounds.Height);
            float scale = targetPx / Math.Max(width, height);
            int outputWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
            int outputHeight = Math.Max(1, (int)Math.Ceiling(height * scale));

            using var surface = SKSurface.Create(new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);

            using var paint = new SKPaint
            {
                ColorFilter = SKColorFilter.CreateBlendMode(
                    new SKColor(tint.R, tint.G, tint.B, tint.A),
                    SKBlendMode.SrcIn)
            };
            // Apply tint while drawing the SVG picture itself. Applying SrcIn while
            // compositing a saved layer can produce a fully transparent result on
            // some Skia backends, which made callers fall back to hand-drawn glyphs.
            canvas.DrawPicture(svg.Picture, paint);
            canvas.Flush();

            using SKImage image = surface.Snapshot();
            using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray());
            using Image decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return new Bitmap(decoded); // clone autonomo: non dipende dal MemoryStream
        }

        public static Icon? CreateWindowIcon(string key = "movie", int size = 64)
        {
            string? path = Resolve(key);
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                using Bitmap bitmap = RenderSvg(path, Math.Max(32, size), Color.FromArgb(35, 164, 255));
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using Icon borrowed = Icon.FromHandle(handle);
                    return (Icon)borrowed.Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
            catch
            {
                return null;
            }
        }

        public static string? EnsureShellIconFile()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025",
                    "shell");
                string iconPath = Path.Combine(directory, "cinecore-player.ico");
                string? source = Resolve("movie");

                if (File.Exists(iconPath) &&
                    (string.IsNullOrWhiteSpace(source) || File.GetLastWriteTimeUtc(iconPath) >= File.GetLastWriteTimeUtc(source)))
                {
                    return iconPath;
                }

                Directory.CreateDirectory(directory);
                using Icon? icon = CreateWindowIcon("movie", 128);
                if (icon == null)
                    return null;

                string temporaryPath = iconPath + ".tmp";
                using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    icon.Save(stream);

                File.Move(temporaryPath, iconPath, overwrite: true);
                return iconPath;
            }
            catch (Exception ex)
            {
                Dbg.Warn("Shell icon generation failed: " + ex.Message);
                return null;
            }
        }

        private static IEnumerable<string> BuildRelativeCandidates(string requested, string normalized)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> Raw()
            {
                if (requested.Contains('/') || requested.Contains('\\'))
                    yield return requested;
                if (KnownIcons.TryGetValue(normalized, out string? mapped))
                    yield return mapped;

                foreach (string folder in new[] { "hud_icons", "hud_icons_new", "icons", string.Empty })
                {
                    foreach (string name in new[] { normalized + ".svg", "icon-" + normalized + ".svg", normalized + "-icon.svg", normalized + ".png" })
                        yield return string.IsNullOrEmpty(folder) ? name : Path.Combine(folder, name);
                }
            }

            foreach (string candidate in Raw())
            {
                if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                    yield return candidate;
            }
        }

        private static IEnumerable<string> AssetDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string seed in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                DirectoryInfo? directory = null;
                try { directory = new DirectoryInfo(seed); } catch { }
                for (int depth = 0; depth < 7 && directory != null; depth++, directory = directory.Parent)
                {
                    string candidate = Path.Combine(directory.FullName, "Assets");
                    if (Directory.Exists(candidate) && seen.Add(candidate))
                        yield return candidate;
                }
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
