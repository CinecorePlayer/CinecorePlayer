using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace CinecoreMac;

internal enum StereoMode { None, Sbs, Tab }

/// <summary>
/// 3D Auto (Stereo3DAutoDetector di Windows, senza il confronto visivo delle due meta'): il nome del file, il metadato
/// stereo_mode del contenitore (ffprobe, tramite ProcessLauncher) e i formati Full-SBS / Full-TAB inequivocabili.
/// </summary>
internal static class MacStereo
{
    private static readonly Regex SbsHint = new(@"(?ix)(?:^|[\s._\-\[\(])(?:sbs|h(?:alf)?[\s._-]*sbs|f(?:ull)?[\s._-]*sbs|side[\s._-]*by[\s._-]*side|left[\s._-]*right|right[\s._-]*left)(?:$|[\s._\-\]\)])");
    private static readonly Regex TabHint = new(@"(?ix)(?:^|[\s._\-\[\(])(?:ou|h(?:alf)?[\s._-]*ou|tab|h(?:alf)?[\s._-]*tab|f(?:ull)?[\s._-]*tab|top[\s._-]*(?:and[\s._-]*)?bottom|bottom[\s._-]*top|over[\s._-]*under)(?:$|[\s._\-\]\)])");

    public static (StereoMode Mode, string Reason) Detect(string path, int width, int height)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (SbsHint.IsMatch(name)) return (StereoMode.Sbs, "nome del file SBS");
        if (TabHint.IsMatch(name)) return (StereoMode.Tab, "nome del file Top/Bottom");
        if (File.Exists(path) && MacTools.Ffprobe is { } ffprobe)
        {
            try
            {
                var (_, stdout, _) = ProcessLauncher.Run(ffprobe, new[] { "-v", "error", "-select_streams", "v:0", "-show_streams", "-of", "json", path }, true, 6000, CancellationToken.None);
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(stdout));
                foreach (var stream in doc.RootElement.GetProperty("streams").EnumerateArray())
                {
                    string text = stream.ToString().ToLowerInvariant();
                    if (text.Contains("side by side") || text.Contains("\"left_right\"") || text.Contains("\"right_left\"") || text.Contains("sidebyside")) return (StereoMode.Sbs, "metadato stereo: side-by-side");
                    if (text.Contains("top and bottom") || text.Contains("\"top_bottom\"") || text.Contains("\"bottom_top\"") || text.Contains("topbottom")) return (StereoMode.Tab, "metadato stereo: top/bottom");
                }
            }
            catch { }
        }
        if (width > 0 && height > 0)
        {
            double aspect = width / (double)height;
            if (width >= 3000 && height >= 900 && aspect >= 3.0) return (StereoMode.Sbs, $"frame Full-SBS ({width}x{height})");
            if (width >= 1440 && height >= 1800 && aspect <= 0.98) return (StereoMode.Tab, $"frame Full-TAB ({width}x{height})");
        }
        return (StereoMode.None, "contenuto 2D");
    }

    public static string Command(StereoMode mode) => "stereo " + mode switch { StereoMode.Sbs => "sbs", StereoMode.Tab => "tab", _ => "none" };

    /// <summary>Proporzioni di un occhio (StereoEyeAspect di Windows): Half-SBS/TAB mostrano l'occhio con il formato del frame.</summary>
    public static double EyeAspect(StereoMode mode, double frame) => mode switch
    {
        StereoMode.Sbs => frame >= 2.6 ? frame / 2 : frame,
        StereoMode.Tab => frame <= 1.2 ? frame * 2 : frame,
        _ => frame
    };
}
