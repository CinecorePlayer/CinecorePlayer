#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Resolves packed stereoscopic video without changing renderer state. Detection is
    /// deliberately conservative: explicit container metadata wins, release-name hints
    /// come second, and image comparison is used only for an otherwise ambiguous 3D file.
    /// </summary>
    internal static class Stereo3DAutoDetector
    {
        internal sealed record DetectionResult(
            Stereo3DMode Mode,
            double Confidence,
            string Reason,
            bool UsedContentAnalysis = false);

        private sealed record CacheEntry(long Length, DateTime LastWriteUtc, DetectionResult Result);

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex SbsHint = new(
            @"(?ix)(?:^|[\s._\-\[\(])(?:sbs|h(?:alf)?[\s._-]*sbs|f(?:ull)?[\s._-]*sbs|side[\s._-]*by[\s._-]*side|left[\s._-]*right|right[\s._-]*left)(?:$|[\s._\-\]\)])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex TabHint = new(
            @"(?ix)(?:^|[\s._\-\[\(])(?:ou|h(?:alf)?[\s._-]*ou|tab|h(?:alf)?[\s._-]*tab|f(?:ull)?[\s._-]*tab|top[\s._-]*(?:and[\s._-]*)?bottom|bottom[\s._-]*top|over[\s._-]*under)(?:$|[\s._\-\]\)])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ThreeDHint = new(
            @"(?ix)(?:^|[\s._\-\[\(])3d(?:$|[\s._\-\]\)])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SsimValue = new(
            @"\bAll:(?<value>\d+(?:\.\d+)?)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static async Task<DetectionResult> DetectAsync(
            string path,
            int width,
            int height,
            double durationSeconds,
            CancellationToken cancellationToken)
        {
            string displayName = GetDisplayName(path);
            if (SbsHint.IsMatch(displayName))
                return new DetectionResult(Stereo3DMode.SBS, 0.99, "nome file SBS");
            if (TabHint.IsMatch(displayName))
                return new DetectionResult(Stereo3DMode.TAB, 0.99, "nome file Top/Bottom");

            bool localFile = IsLocalFile(path);
            if (!localFile)
                return new DetectionResult(Stereo3DMode.None, 0.45, "nessun metadato 3D disponibile sul flusso remoto");

            FileInfo info;
            try { info = new FileInfo(path); }
            catch { return new DetectionResult(Stereo3DMode.None, 0.2, "percorso non analizzabile"); }

            if (Cache.TryGetValue(path, out CacheEntry? cached) &&
                cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            {
                return cached.Result;
            }

            DetectionResult? metadata = await ProbeContainerAsync(path, cancellationToken).ConfigureAwait(false);
            if (metadata != null)
                return Remember(path, info, metadata);

            bool marked3D = ThreeDHint.IsMatch(displayName);
            if (width > 0 && height > 0)
            {
                double aspect = width / (double)height;
                // Full-SBS (tipicamente 3840x1080) e Full-TAB (1920x2160) sono
                // geometricamente non ambigui anche quando il nome del file non
                // contiene "3D". Riconoscerli qui evita che Auto mostri il frame
                // doppio, larghissimo e schiacciato.
                bool unmistakableFullSbs = width >= 3000 && height >= 900 && aspect >= 3.0;
                bool unmistakableFullTab = width >= 1440 && height >= 1800 && aspect <= 0.98;
                if (unmistakableFullSbs || (marked3D && aspect >= 2.75))
                    return Remember(path, info, new DetectionResult(Stereo3DMode.SBS, unmistakableFullSbs ? 0.96 : 0.88, $"frame Full-SBS ({width}x{height})"));
                if (unmistakableFullTab || (marked3D && aspect <= 1.05))
                    return Remember(path, info, new DetectionResult(Stereo3DMode.TAB, unmistakableFullTab ? 0.95 : 0.86, $"frame Full-TAB ({width}x{height})"));
            }

            if (marked3D)
            {
                DetectionResult? visual = await ComparePackedViewsAsync(path, durationSeconds, cancellationToken).ConfigureAwait(false);
                if (visual != null)
                    return Remember(path, info, visual);
            }

            return Remember(path, info, new DetectionResult(
                Stereo3DMode.None,
                marked3D ? 0.72 : 0.94,
                marked3D ? "3D indicato, layout SBS/TAB non determinabile" : "contenuto 2D: nessun indicatore stereoscopico"));
        }

        private static DetectionResult Remember(string path, FileInfo info, DetectionResult result)
        {
            Cache[path] = new CacheEntry(info.Length, info.LastWriteTimeUtc, result);
            return result;
        }

        private static async Task<DetectionResult?> ProbeContainerAsync(string path, CancellationToken cancellationToken)
        {
            string? ffprobe = ResolveBundledTool("ffprobe.exe");
            if (ffprobe == null)
                return null;

            var psi = NewToolStartInfo(ffprobe);
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add("v:0");
            psi.ArgumentList.Add("-show_streams");
            psi.ArgumentList.Add("-show_format");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add(path);

            string? json = await RunForStdOutAsync(psi, TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("streams", out JsonElement streams) && streams.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement stream in streams.EnumerateArray())
                    {
                        if (TryReadStereoTag(stream, out Stereo3DMode tagMode, out string tagReason))
                            return new DetectionResult(tagMode, 1.0, tagReason);

                        if (stream.TryGetProperty("side_data_list", out JsonElement sideData) && sideData.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement entry in sideData.EnumerateArray())
                            {
                                string sideType = ReadString(entry, "side_data_type");
                                if (!sideType.Contains("stereo", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                string packedType = ReadString(entry, "type");
                                if (TryParseStereoValue(packedType, out Stereo3DMode sideMode, out bool recognized) && recognized)
                                    return new DetectionResult(sideMode, 1.0, "metadato FFmpeg Stereo 3D: " + packedType);
                            }
                        }
                    }
                }

                if (root.TryGetProperty("format", out JsonElement format) &&
                    TryReadStereoTag(format, out Stereo3DMode formatMode, out string formatReason))
                {
                    return new DetectionResult(formatMode, 1.0, formatReason);
                }
            }
            catch (JsonException ex)
            {
                Dbg.Warn("[3D AUTO] ffprobe JSON non valido: " + ex.Message);
            }

            return null;
        }

        private static bool TryReadStereoTag(JsonElement owner, out Stereo3DMode mode, out string reason)
        {
            mode = Stereo3DMode.None;
            reason = string.Empty;
            if (!owner.TryGetProperty("tags", out JsonElement tags) || tags.ValueKind != JsonValueKind.Object)
                return false;

            foreach (JsonProperty property in tags.EnumerateObject())
            {
                if (!property.Name.Equals("stereo_mode", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.Equals("stereomode", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.Equals("stereo-mode", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = property.Value.ToString();
                if (TryParseStereoValue(value, out mode, out bool recognized) && recognized)
                {
                    reason = "metadato stereo_mode: " + value;
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseStereoValue(string? value, out Stereo3DMode mode, out bool recognized)
        {
            mode = Stereo3DMode.None;
            recognized = false;
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant()
                .Replace('_', ' ').Replace('-', ' ');

            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            {
                if (numeric is 1 or 11)
                {
                    mode = Stereo3DMode.SBS;
                    recognized = true;
                    return true;
                }
                if (numeric is 2 or 3)
                {
                    mode = Stereo3DMode.TAB;
                    recognized = true;
                    return true;
                }
                if (numeric == 0)
                {
                    recognized = true;
                    return true;
                }
            }

            if (normalized.Contains("side by side") || normalized.Contains("left right") ||
                normalized.Contains("right left") || normalized is "sbs" or "hsbs" or "fsbs")
            {
                mode = Stereo3DMode.SBS;
                recognized = true;
                return true;
            }

            if (normalized.Contains("top and bottom") || normalized.Contains("top bottom") ||
                normalized.Contains("bottom top") || normalized.Contains("over under") ||
                normalized is "tab" or "htab" or "ftab" or "hou")
            {
                mode = Stereo3DMode.TAB;
                recognized = true;
                return true;
            }

            if (normalized is "2d" or "mono" or "monoscopic")
            {
                recognized = true;
                return true;
            }

            return false;
        }

        private static async Task<DetectionResult?> ComparePackedViewsAsync(
            string path,
            double durationSeconds,
            CancellationToken cancellationToken)
        {
            string? ffmpeg = ResolveBundledTool("ffmpeg.exe");
            if (ffmpeg == null)
                return null;

            double seek = durationSeconds > 20 ? Math.Clamp(durationSeconds * 0.37, 8, Math.Max(8, durationSeconds - 5)) : 0;
            double? sbs = await MeasureHalfSimilarityAsync(ffmpeg, path, seek, horizontal: true, cancellationToken).ConfigureAwait(false);
            double? tab = await MeasureHalfSimilarityAsync(ffmpeg, path, seek, horizontal: false, cancellationToken).ConfigureAwait(false);
            if (!sbs.HasValue || !tab.HasValue)
                return null;

            Dbg.Log($"[3D AUTO] confronto visivo SBS={sbs.Value:0.000}, TAB={tab.Value:0.000}", Dbg.LogLevel.Info);
            double best = Math.Max(sbs.Value, tab.Value);
            double separation = Math.Abs(sbs.Value - tab.Value);
            if (best < 0.62 || separation < 0.055)
                return new DetectionResult(Stereo3DMode.None, 0.62, $"confronto visivo non conclusivo (SBS {sbs:0.00}, TAB {tab:0.00})", true);

            Stereo3DMode mode = sbs > tab ? Stereo3DMode.SBS : Stereo3DMode.TAB;
            return new DetectionResult(mode, Math.Clamp(0.68 + separation, 0.68, 0.93),
                $"layout 3D riconosciuto dal contenuto (SBS {sbs:0.00}, TAB {tab:0.00})", true);
        }

        private static async Task<double?> MeasureHalfSimilarityAsync(
            string ffmpeg,
            string path,
            double seek,
            bool horizontal,
            CancellationToken cancellationToken)
        {
            string filter = horizontal
                ? "[0:v]split=2[a][b];[a]crop=iw/2:ih:0:0,scale=320:180[x];[b]crop=iw/2:ih:iw/2:0,scale=320:180[y];[x][y]ssim"
                : "[0:v]split=2[a][b];[a]crop=iw:ih/2:0:0,scale=320:180[x];[b]crop=iw:ih/2:0:ih/2,scale=320:180[y];[x][y]ssim";

            var psi = NewToolStartInfo(ffmpeg);
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            if (seek > 0)
            {
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add(seek.ToString("0.###", CultureInfo.InvariantCulture));
            }
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-filter_complex");
            psi.ArgumentList.Add(filter);
            psi.ArgumentList.Add("-an");
            psi.ArgumentList.Add("-sn");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            string? output = await RunForStdErrAsync(psi, TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            MatchCollection matches = SsimValue.Matches(output ?? string.Empty);
            if (matches.Count == 0)
                return null;

            string raw = matches[^1].Groups["value"].Value;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? Math.Clamp(value, 0, 1)
                : null;
        }

        private static ProcessStartInfo NewToolStartInfo(string toolPath) => new()
        {
            FileName = toolPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory
        };

        private static async Task<string?> RunForStdOutAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = await RunToolAsync(psi, timeout, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StdOut : null;
        }

        private static async Task<string?> RunForStdErrAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = await RunToolAsync(psi, timeout, cancellationToken).ConfigureAwait(false);
            return result.StdErr;
        }

        private static async Task<(int ExitCode, string StdOut, string StdErr)> RunToolAsync(
            ProcessStartInfo psi,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return (-1, string.Empty, string.Empty);

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                cancellationToken.ThrowIfCancellationRequested();
                return (-1, string.Empty, string.Empty);
            }
        }

        private static string? ResolveBundledTool(string fileName)
        {
            foreach (string root in CandidateRoots())
            {
                string[] candidates =
                {
                    Path.Combine(root, "third-parties", "ffmpeg", "win-x64", fileName),
                    Path.Combine(root, "ffmpeg", "win-x64", fileName)
                };
                string? match = candidates.FirstOrDefault(File.Exists);
                if (match != null)
                    return match;
            }
            return null;
        }

        private static string[] CandidateRoots()
        {
            string[] seeds = { AppContext.BaseDirectory, Environment.CurrentDirectory };
            return seeds.SelectMany(seed =>
            {
                var roots = new System.Collections.Generic.List<string>();
                string? current = seed;
                for (int i = 0; i < 7 && !string.IsNullOrWhiteSpace(current); i++)
                {
                    try { roots.Add(Path.GetFullPath(current)); }
                    catch { break; }
                    current = Directory.GetParent(current)?.FullName;
                }
                return roots;
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static bool IsLocalFile(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
                return false;
            return File.Exists(path);
        }

        private static string GetDisplayName(string path)
        {
            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
                    return Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? path);
                return Path.GetFileNameWithoutExtension(path) ?? path;
            }
            catch { return path; }
        }

        private static string ReadString(JsonElement owner, string name)
        {
            return owner.TryGetProperty(name, out JsonElement value) ? value.ToString() : string.Empty;
        }
    }
}
