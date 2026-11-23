using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// In alto la foto dell'ultimo film guardato, sfumata nel fondo come il banner della Home (DrawSettingsBanner di Windows).
/// Senza film recenti con una foto: una dei backdrop della libreria, la stessa per tutto il giorno.
/// </summary>
internal sealed partial class SettingsView
{
    private Bitmap? _banner;
    private string? _bannerFile;
    private bool _bannerLoading;

    private static string? SettingsBannerPhoto()
    {
        try
        {
            foreach (var entry in CinecorePlayer2025.Engines.PlaybackResumeStore.LoadAll().OrderByDescending(e => e.SavedAt).Take(12))
            {
                string? backdrop = null;
                try { backdrop = CinecorePlayer2025.Utilities.MovieMetadataService.GetCachedBackdropPath(entry.MediaPath); } catch { }
                if (!string.IsNullOrWhiteSpace(backdrop) && File.Exists(backdrop)) return backdrop;
            }
            string folder = Path.Combine(LibraryStore.AppDataDir, "backdrops");
            var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.jpg") : Array.Empty<string>();
            Array.Sort(files, StringComparer.Ordinal);
            return files.Length > 0 ? files[(int)(DateTime.Today.Ticks / TimeSpan.TicksPerDay % files.Length)] : null;
        }
        catch { return null; }
    }

    /// <summary>Ad ogni apertura si ricontrolla l'ultimo film: la foto cambia se nel frattempo se ne e' guardato un altro.</summary>
    private void RefreshBanner()
    {
        if (_bannerLoading) return;
        _bannerLoading = true;
        Task.Run(() =>
        {
            string? file = SettingsBannerPhoto();
            Bitmap? loaded = null;
            if (file != null && file != _bannerFile)
                try { using var fs = File.OpenRead(file); loaded = Bitmap.DecodeToWidth(fs, 2560, BitmapInterpolationMode.HighQuality); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                _bannerLoading = false;
                if (file == null) { _banner?.Dispose(); _banner = null; _bannerFile = null; }
                else if (loaded != null) { _banner?.Dispose(); _banner = loaded; _bannerFile = file; }
                InvalidateVisual();
            });
        });
    }

    private void DrawSettingsBanner(DrawingContext ctx, Rect b)
    {
        if (_banner is not { } source) return;
        double h = Math.Max(1, b.Height * 0.62);
        var r = new Rect(b.X, b.Y, b.Width, h);
        Color back = UiTheme.Panel;
        static Color C(byte a, Color c) => Color.FromArgb(a, c.R, c.G, c.B);
        using (ctx.PushClip(r))
        {
            // La foto a destra, coprendo l'area; a sinistra e in basso si scioglie nel fondo.
            double k = Math.Max(r.Width / source.Size.Width, r.Height / source.Size.Height);
            double w = source.Size.Width * k, hh = source.Size.Height * k;
            ctx.DrawImage(source, new Rect(r.Right - w, r.Y + (r.Height - hh) * 0.3, w, hh));
            // Il testo ci passa davanti: nel tema chiaro la foto diventa chiara, nello scuro resta scura.
            ctx.FillRectangle(new SolidColorBrush(C((byte)(UiTheme.IsLight ? 175 : 120), back)), r);
            var left = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(C(255, back), 0), new GradientStop(C(235, back), .16), new GradientStop(C(170, back), .32),
                    new GradientStop(C(90, back), .5), new GradientStop(C(25, back), .72), new GradientStop(C(0, back), 1),
                },
            };
            ctx.FillRectangle(left, r);
            double topH = Math.Min(r.Height / 3, 220);
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(C(200, back), 0), new GradientStop(C(0, back), 1) },
            }, new Rect(r.X, r.Y, r.Width, topH));
            double fadeH = r.Height * 0.62;
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(C(0, back), 0), new GradientStop(C(70, back), .3), new GradientStop(C(170, back), .62),
                    new GradientStop(C(235, back), .85), new GradientStop(back, 1),
                },
            }, new Rect(r.X, r.Bottom - fadeH, r.Width, fadeH + 1));
        }
    }
}
