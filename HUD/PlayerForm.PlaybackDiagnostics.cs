#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HDRMode = global::CinecorePlayer2025.Utilities.HdrMode;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm : Form
    {
        private static (int W, int H, string Label) NormalizeViewport(int w, int h)
        {
            if (w <= 0 || h <= 0) return (w, h, $"{w}x{h}");
            var cand = new (int W, int H, string Label)[]
            {
                (3840,2160,"3840x2160"), (4096,2160,"4096x2160"), (2560,1440,"2560x1440"),
                (1920,1080,"1920x1080"), (1600,900,"1600x900"), (1280,720,"1280x720"),
            };
            foreach (var c in cand)
            {
                double dw = Math.Abs(w - c.W) / (double)c.W;
                double dh = Math.Abs(h - c.H) / (double)c.H;
                if (dw <= 0.02 && dh <= 0.02) return c;
            }
            return (w, h, $"{w}x{h}");
        }
        /// <summary>Position of the playing track among the file's own audio streams, or -1.</summary>
        private int SelectedInternalAudioOrdinal()
        {
            try
            {
                var audio = _engine?.EnumerateStreams().Where(x => x.IsAudio && !x.IsExternal).ToList();
                return audio == null ? -1 : audio.FindIndex(x => x.Selected);
            }
            catch { return -1; }
        }

        private static string FmtKbps(int kbps) => kbps > 0 ? $"{kbps:n0} kbps" : "n/d";
        private static double GetVideoFps(MediaProbe.Result? info)
        {
            if (info == null) return 0;

            try
            {
                var t = info.GetType();

                object? GetMemberValue(string[] names)
                {
                    const BindingFlags flags =
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.IgnoreCase;

                    foreach (var name in names)
                    {
                        var p = t.GetProperty(name, flags);
                        if (p != null)
                            return p.GetValue(info);

                        var f = t.GetField(name, flags);
                        if (f != null)
                            return f.GetValue(info);
                    }
                    return null;
                }

                // 1) proprietà/field numerici "classici"
                var numNames = new[]
                {
                    "VideoFps", "VideoFPS",
                    "Fps", "FPS",
                    "VideoFrameRate", "FrameRate",
                    "FrameRateDouble"
                };

                var rawNum = GetMemberValue(numNames);
                double val = ToDouble(rawNum);
                if (val > 0.1 && val < 1000) return val;

                // 2) stringhe tipo "24000/1001" / "23.976"
                var strNames = new[]
                {
                    "AvgFrameRate", "AverageFrameRate",
                    "RFrameRate",
                    "VideoAvgFrameRate", "VideoAverageFrameRate",
                    "VideoRFrameRate"
                };

                var rawStr = GetMemberValue(strNames);
                if (rawStr is string s)
                {
                    double f = ParseFpsString(s);
                    if (f > 0.1 && f < 1000) return f;
                }

                // 3) coppie numeratore/denominatore
                double num = 0, den = 0;

                var numProps = new[] { "FrameRateNum", "FrameRateNumerator", "RFrameRateNum", "VideoFrameRateNum" };
                var denProps = new[] { "FrameRateDen", "FrameRateDenominator", "RFrameRateDen", "VideoFrameRateDen" };

                var rawNum2 = GetMemberValue(numProps);
                var rawDen2 = GetMemberValue(denProps);

                num = ToDouble(rawNum2);
                den = ToDouble(rawDen2);

                if (num > 0 && den > 0)
                {
                    double f = num / den;
                    if (f > 0.1 && f < 1000) return f;
                }
            }
            catch
            {
                // best-effort, niente eccezioni da qui
            }

            return 0;

            static double ToDouble(object? v)
            {
                if (v == null) return 0;

                // gestisce anche int, long, float, double, ecc.
                if (v is IConvertible conv)
                {
                    try { return conv.ToDouble(System.Globalization.CultureInfo.InvariantCulture); }
                    catch { }
                }

                if (v is double d) return d;
                if (v is float f) return f;
                if (v is int i) return i;
                if (v is long l) return l;
                return 0;
            }

            static double ParseFpsString(string s)
            {
                s = s.Trim();
                if (s.Length == 0) return 0;

                // tipo "24000/1001"
                int slash = s.IndexOf('/');
                if (slash > 0)
                {
                    var numStr = s[..slash];
                    var denStr = s[(slash + 1)..];

                    if (double.TryParse(numStr, System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture, out var num) &&
                        double.TryParse(denStr, System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture, out var den) &&
                        den != 0)
                    {
                        return num / den;
                    }
                }

                // tipo "23.976"
                if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var val))
                    return val;

                return 0;
            }
        }

        // ======= INFO OVERLAY =======
        private bool IsRtxHdrLikelyActive(VRChoice renderer, bool fileHdr)
        {
            try
            {
                if (fileHdr || _engine == null || !_currentMediaHasVideo || _hdrProfile != HdrUiProfile.RtxVideoHdr)
                    return false;

                if (renderer != VRChoice.MPCVR)
                    return false;

                var now = DateTime.UtcNow;
                if ((now - _lastRtxHdrProbeUtc).TotalSeconds < 3)
                    return _lastRtxHdrProbeResult;

                _lastRtxHdrProbeUtc = now;
                _lastRtxHdrProbeResult = IsRtxVideoHdrAvailable() && HasAnyActiveHdrDisplay();
                return _lastRtxHdrProbeResult;
            }
            catch { return false; }
        }

        private static readonly Lazy<bool> NvidiaDriverPresent = new(ProbeNvidiaDriverPresent);
        private static bool HasNvidiaDriverPresent() => NvidiaDriverPresent.Value;

        private static bool ProbeNvidiaDriverPresent()
        {
            try
            {
                using var nv = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\NVIDIA Corporation");
                if (nv != null)
                    return true;
            }
            catch { }

            try
            {
                string nvapi = Path.Combine(Environment.SystemDirectory, Environment.Is64BitProcess ? "nvapi64.dll" : "nvapi.dll");
                if (File.Exists(nvapi))
                    return true;
            }
            catch { }

            return false;
        }

        private static bool HasAnyActiveHdrDisplay()
        {
            try
            {
                using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore");
                if (root == null)
                    return false;

                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var monitor = root.OpenSubKey(name);
                        if (monitor == null)
                            continue;

                        if (IsRegistryFlagEnabled(monitor.GetValue("HDREnabled")) ||
                            IsRegistryFlagEnabled(monitor.GetValue("AdvancedColorEnabled")))
                            return true;
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        private static bool IsRegistryFlagEnabled(object? value)
        {
            try
            {
                return value switch
                {
                    int i => i != 0,
                    uint u => u != 0,
                    long l => l != 0,
                    string s when int.TryParse(s, out var i) => i != 0,
                    byte[] bytes when bytes.Length > 0 => bytes.Any(b => b != 0),
                    _ => false
                };
            }
            catch { return false; }
        }

        private void RefreshInfoOverlayNow()
        {
            try
            {
                // Senza l'analisi del file (YouTube, flusso di rete che FFmpeg non ha aperto) il
                // pannello si aggiorna lo stesso, con cio' che dicono il motore e i componenti.
                if (_engine == null) return;
                bool hdr = _info?.IsHdr ?? false;
                UpdateInfoOverlay(ResolveRendererForInfo(hdr), hdr);
            }
            catch { }
        }

        // ===== Dati dichiarati dai componenti (LAV, madVR, MPC Video Renderer, mpv) =====

        private PlaybackComponentReport? ReadComponentReport()
        {
            try
            {
                return _engine switch
                {
                    DirectShowUnifiedEngine directShow => directShow.GetComponentReport(),
                    LibMpvPlaybackEngine mpv => mpv.GetComponentReport(),
                    _ => null
                };
            }
            catch { return null; }
        }

        private string DescribeVideoDecoder(PlaybackComponentReport report)
        {
            string name = (report.VideoDecoder ?? string.Empty).Trim();
            if (name.Length == 0 || name.Equals("inactive", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            string lower = name.ToLowerInvariant();
            string software = Tx("software (CPU)", "software (CPU)");
            if (_engine is LibMpvPlaybackEngine)
                return "mpv · " + (lower == "no" ? software : "hardware " + name);
            string how =
                lower.Contains("d3d11") ? "D3D11" + (lower.Contains("native") ? Tx(" nativo", " native") : lower.Contains("cb") ? " copy-back" : string.Empty) :
                lower.Contains("dxva2n") ? "DXVA2" + Tx(" nativo", " native") :
                lower.Contains("dxva2") ? "DXVA2 copy-back" :
                lower.Contains("cuvid") ? "NVIDIA CUVID" :
                lower.Contains("quicksync") || lower.Contains("msdk") ? "Intel QuickSync" :
                lower.Contains("avcodec") ? software : name;
            // La riga e' stretta: della scheda video basta il modello.
            string device = (report.VideoDecoderDevice ?? string.Empty).Replace("NVIDIA ", string.Empty).Replace("(R)", string.Empty).Replace("(TM)", string.Empty).Trim();
            if (device.Length > 30) device = device[..30].TrimEnd() + "…";
            return "LAV Video · " + how + (device.Length > 0 && how != software ? " · " + device : string.Empty);
        }

        private string DescribeFrames(PlaybackComponentReport report)
        {
            var parts = new List<string>();
            if (report.FramesDrawn >= 0 && !report.CountsDisplayRefreshes) parts.Add(report.FramesDrawn.ToString("N0", CultureInfo.CurrentCulture));
            if (report.FramesDropped >= 0) parts.Add(Tx("persi ", "dropped ") + report.FramesDropped.ToString("N0", CultureInfo.CurrentCulture));
            if (report.DecoderDroppedFrames > 0) parts.Add(Tx("saltati in decodifica ", "skipped decoding ") + report.DecoderDroppedFrames.ToString("N0", CultureInfo.CurrentCulture));
            if (report.PresentedFrameRate > 0 && !report.CountsDisplayRefreshes) parts.Add(report.PresentedFrameRate.ToString("0.###", CultureInfo.CurrentCulture) + Tx(" fps reali", " fps actual"));
            if (report.JitterMs >= 0 && report.FramesDrawn > 0) parts.Add("jitter " + report.JitterMs + " ms");
            return string.Join(" · ", parts);
        }

        private string DescribeAudioDecoder(PlaybackComponentReport report, bool bitstream)
        {
            string codec = (report.AudioCodec ?? string.Empty).Trim();
            string format = (report.AudioDecodeFormat ?? string.Empty).Trim();
            if (codec.Length == 0 && format.Length == 0) return string.Empty;
            var parts = new List<string> { _engine is LibMpvPlaybackEngine ? "mpv" : "LAV Audio" };
            if (codec.Length > 0) parts.Add(codec.ToUpperInvariant());
            if (bitstream) parts.Add(Tx("passante, non decodificato", "passed through, not decoded"));
            else
            {
                if (format.Length > 0 && !format.Equals("Not Running", StringComparison.OrdinalIgnoreCase)) parts.Add(format);
                if (report.AudioDecodeChannels > 0) parts.Add(report.AudioDecodeChannels + Tx(" can.", " ch"));
                if (report.AudioDecodeRate > 0) parts.Add((report.AudioDecodeRate / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " kHz");
            }
            return string.Join(" · ", parts);
        }

        private string DescribeMatrix(string? matrix)
        {
            string value = (matrix ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            if (value.Equals("None", StringComparison.OrdinalIgnoreCase)) return "RGB";
            if (value.StartsWith("mpv:", StringComparison.Ordinal))
            {
                // mpv: "bt.2020-ncl|limited", "dolbyvision|limited", "rgb|full".
                string[] parts = value[4..].Split('|');
                string name = parts[0].ToLowerInvariant();
                string range = parts.Length > 1 && parts[1].Equals("full", StringComparison.OrdinalIgnoreCase) ? Tx("livelli PC", "PC levels")
                    : parts.Length > 1 && parts[1].Length > 0 ? Tx("livelli video", "video levels") : string.Empty;
                string space = name == "rgb" ? "RGB" : name.StartsWith("dolbyvision", StringComparison.Ordinal) ? "Dolby Vision (IPT)"
                    : "YCbCr " + name.Replace("bt.", string.Empty).Replace("-ncl", string.Empty).Replace("-cl", " CL").ToUpperInvariant();
                return range.Length > 0 && name != "rgb" ? space + " · " + range : space;
            }
            int dot = value.IndexOf('.');
            if (dot > 0)
            {
                // madVR: "TV.2020" = livelli video, matrice BT.2020.
                string levels = value[..dot].Equals("PC", StringComparison.OrdinalIgnoreCase) ? Tx("livelli PC", "PC levels") : Tx("livelli video", "video levels");
                return "YCbCr " + value[(dot + 1)..] + " · " + levels;
            }
            return "YCbCr " + value;
        }

        private string? _infoOriginPath, _infoOriginText;

        private string DescribeOrigin(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            if (string.Equals(_infoOriginPath, path, StringComparison.Ordinal) && _infoOriginText != null) return _infoOriginText;
            string text;
            try
            {
                if (IsCurrentYouTube()) text = "YouTube";
                else if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    string host = uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port);
                    bool lan = !uri.Host.Contains('.') || uri.HostNameType == UriHostNameType.IPv6 ||
                               (System.Net.IPAddress.TryParse(uri.Host, out var address) && address.GetAddressBytes() is { Length: 4 } b &&
                                (b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 169 && b[1] == 254)));
                    text = JellyfinClient.TryParseStream(path, out _, out _, out _) ? JellyfinClient.ProductOfStream(path) + " · " + host
                        : lan ? Tx("Rete locale (DLNA/HTTP) · ", "Local network (DLNA/HTTP) · ") + host
                        : uri.Scheme.ToUpperInvariant() + " · " + host;
                }
                else if (path.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    string server = path.TrimStart('\\').Split('\\')[0];
                    text = Tx("Condivisione di rete · ", "Network share · ") + server;
                }
                else
                {
                    string root = Path.GetPathRoot(path) ?? string.Empty;
                    DriveType type = root.Length > 0 ? new DriveInfo(root).DriveType : DriveType.Unknown;
                    text = type switch
                    {
                        DriveType.Network => Tx("Unità di rete ", "Network drive ") + root.TrimEnd('\\'),
                        DriveType.CDRom => Tx("Disco ottico ", "Optical disc ") + root.TrimEnd('\\'),
                        DriveType.Removable => Tx("Unità rimovibile ", "Removable drive ") + root.TrimEnd('\\'),
                        _ => Tx("File locale · ", "Local file · ") + root.TrimEnd('\\')
                    };
                }
            }
            catch { text = string.Empty; }
            _infoOriginPath = path;
            return _infoOriginText = text;
        }

        private static string? UrlFileName(string path)
        {
            try
            {
                if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)) return null;
                string name = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? string.Empty).Trim('/');
                string stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                if (name.Length is < 3 or > 120 || !name.Contains('.') || stem is "file" or "stream" or "video" or "download" or "index" or "master" or "playlist") return null;
                return name;
            }
            catch { return null; }
        }

        private string? _infoFileSizePath, _infoFileSizeText;

        private void FillInfoExtras(ref InfoOverlay.Stats s, int width, int height, bool hdr, VRChoice renderer)
        {
            string path = _currentPath ?? string.Empty;
            bool local = _isLocalFile && !string.IsNullOrWhiteSpace(path);
            s.FileName = local ? Path.GetFileName(path)
                : IsCurrentYouTube() ? "YouTube"
                : JellyfinClient.RememberedFileName(path) ?? UrlFileName(path) ?? "Stream";
            if (local && !string.Equals(_infoFileSizePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _infoFileSizePath = path;
                try { _infoFileSizeText = FormatBytes(new FileInfo(path).Length); } catch { _infoFileSizeText = null; }
            }
            // Flussi di rete: dimensione e contenitore li dice la sorgente (analisi FFmpeg), non il nome.
            s.FileSize = local ? _infoFileSizeText ?? string.Empty : _info is { SizeBytes: > 0 } probed ? FormatBytes(probed.SizeBytes) : string.Empty;
            string ext = local ? Path.GetExtension(path).TrimStart('.').ToUpperInvariant() : string.Empty;
            s.Container = ext switch { "MKV" => "Matroska", "MP4" or "M4V" => "MP4", "TS" or "M2TS" => "MPEG-TS", "" => _info?.Format ?? string.Empty, _ => ext };
            s.Origin = DescribeOrigin(path);

            s.ResolutionTag = !_currentMediaHasVideo || width <= 0 || height <= 0 ? string.Empty
                : width >= 3200 || height >= 2000 ? "4K"
                : width >= 1800 || height >= 1000 ? "1080p"
                : width >= 1200 || height >= 700 ? "720p" : "SD";
            string transfer = (s.VideoTransfer ?? string.Empty).ToUpperInvariant();
            bool dolbyVision = path.IndexOf(".DV.", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf(" DV ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               path.IndexOf("DoVi", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("Dolby Vision", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               _info?.DolbyVisionProfile > 0; // dichiarato dal flusso: vale anche quando il nome non lo dice
            s.DynamicRangeTag = !_currentMediaHasVideo ? string.Empty
                : transfer.Contains("HLG") || transfer.Contains("B67") ? "HLG"
                : hdr || transfer.Contains("PQ") || transfer.Contains("2084") ? (dolbyVision ? "Dolby Vision" : "HDR10")
                : "SDR";
            string audio = s.AudioIn ?? string.Empty;
            int cut = audio.IndexOfAny(new[] { '•', '·', ',' });
            s.AudioTag = (cut > 0 ? audio[..cut] : audio).Trim();
            if (s.AudioTag.Length > 22 || s.AudioTag == "n/d") s.AudioTag = string.Empty;

            s.DurationSec = GetTimelineDurationSeconds();
            try { s.PositionSec = Math.Max(0, _engine?.PositionSeconds ?? 0); } catch { s.PositionSec = _lastKnownPlaybackPosition; }
            s.DroppedFrames = renderer == VRChoice.MADVR && _madVrPerfPreviousFrames is { } frames ? Math.Max(0, frames.Dropped) : -1;
            s.Display = DescribeCurrentDisplayMode();
            s.Renderer = renderer switch
            {
                VRChoice.MADVR => "madVR",
                VRChoice.MPV => "mpv (libmpv)",
                VRChoice.EVR => "Enhanced Video Renderer",
                _ => renderer.ToString().Contains("MPC", StringComparison.OrdinalIgnoreCase) ? "MPC Video Renderer" : renderer.ToString()
            };
        }

        private string DescribeCurrentDisplayMode()
        {
            try
            {
                var screen = Screen.FromControl(this);
                var mode = new InfoDevMode { dmSize = (short)Marshal.SizeOf<InfoDevMode>() };
                if (EnumDisplaySettingsW(screen.DeviceName, -1, ref mode) && mode.dmPelsWidth > 0)
                    return $"{mode.dmPelsWidth}×{mode.dmPelsHeight} · {mode.dmDisplayFrequency} Hz" + (mode.dmBitsPerPel > 0 ? $" · {mode.dmBitsPerPel}-bit" : string.Empty);
                return $"{screen.Bounds.Width}×{screen.Bounds.Height}";
            }
            catch { return string.Empty; }
        }

        private static string FormatBytes(long bytes)
        {
            double gb = bytes / 1024d / 1024d / 1024d;
            return gb >= 1 ? gb.ToString("0.0#", CultureInfo.CurrentCulture) + " GB" : (bytes / 1024d / 1024d).ToString("0", CultureInfo.CurrentCulture) + " MB";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct InfoDevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
        private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref InfoDevMode devMode);

        private void UpdateInfoOverlay(VRChoice renderer, bool fileHdr)
        {
            if (_engine == null) return;

            // Video OUT (negoziato) + fps
            int outW = 0, outH = 0;
            bool runtimeHdr = fileHdr;
            bool runtimeUpscaling = false;
            string runtimeVideoCodec = string.Empty;
            string runtimePrimaries = string.Empty;
            string runtimeTransfer = string.Empty;
            string runtimeVideoFormat = string.Empty;

            try
            {
                var negotiated = _engine.GetNegotiatedVideoFormat(); // (int width, int height, string subtype)
                outW = negotiated.Item1;
                outH = negotiated.Item2;
            }
            catch
            {
                // se l'engine non fornisce ancora il formato negoziato, lasceremo i default
            }

            if (_engine is LibMpvPlaybackEngine mpvVideoEngine)
            {
                try
                {
                    var mpvVideo = mpvVideoEngine.GetVideoOverlayDetails();
                    if (mpvVideo.OutWidth > 0) outW = mpvVideo.OutWidth;
                    if (mpvVideo.OutHeight > 0) outH = mpvVideo.OutHeight;
                    runtimeHdr = runtimeHdr || mpvVideo.IsHdr;
                    runtimeUpscaling = runtimeUpscaling || mpvVideo.Upscaling;
                    runtimeVideoCodec = mpvVideo.Codec;
                    runtimePrimaries = mpvVideo.Primaries;
                    runtimeTransfer = mpvVideo.Transfer;
                    runtimeVideoFormat = mpvVideo.Format;
                }
                catch { }
            }

            // Cio' che dichiarano i componenti: dove c'e', vale piu' di quanto ricaviamo noi.
            var components = ReadComponentReport();
            // Uscita = il rettangolo in cui il video viene disegnato a schermo: l'immagine adattata
            // all'area video mantenendo le proporzioni. Il formato negoziato dal renderer e' quello
            // della sorgente (un 720p a schermo intero risultava "uscita 1280x720"), e il rettangolo
            // dichiarato da madVR, alla prova, non seguiva lo scaling.
            if (_engine is not LibMpvPlaybackEngine)
            {
                try
                {
                    Size host = _videoHost.ClientSize;
                    int sourceW = _info?.Width > 0 ? _info.Width : components?.SourceWidth ?? 0;
                    int sourceH = _info?.Height > 0 ? _info.Height : components?.SourceHeight ?? 0;
                    if (host.Width > 0 && host.Height > 0 && sourceW > 0 && sourceH > 0)
                    {
                        double aspect = sourceW * (_info?.SampleAspect > 0 ? _info.SampleAspect : 1) / sourceH;
                        if (host.Width / (double)host.Height > aspect) { outH = host.Height; outW = (int)Math.Round(host.Height * aspect); }
                        else { outW = host.Width; outH = (int)Math.Round(host.Width / aspect); }
                    }
                }
                catch { }
            }

            // If the renderer does not expose its negotiated size, the actual output
            // surface is the video viewport (not the whole monitor in windowed mode).
            if (outW <= 0 || outH <= 0)
            {
                try
                {
                    outW = _videoHost.ClientSize.Width;
                    outH = _videoHost.ClientSize.Height;
                }
                catch
                {
                    outW = 0;
                    outH = 0;
                }
            }

            // fps dal probe (container)
            double fps = GetVideoFps(_info);
            if (fps <= 0 && IsCurrentYouTube() && _currentWebFps > 0)
                fps = _currentWebFps;
            // Senza analisi del file (flusso di rete non apribile da FFmpeg): la cadenza la dice il renderer.
            if (fps <= 0 && components is { SourceFrameRate: > 0 })
                fps = components.SourceFrameRate;
            string fpsStr = fps > 0
                ? (Math.Abs(fps - 23.976) < 0.01 ? "23.976" :
                   Math.Abs(fps - 29.97) < 0.01 ? "29.970" :
                   fps.ToString("0.###"))
                : "n/d";

            var norm = NormalizeViewport(outW, outH);
            outW = norm.W;
            outH = norm.H;
            string outStr = $"{norm.Label} • {fpsStr} fps";
            if (components is { RefreshRate: > 0 })
                outStr += " • " + Tx("schermo ", "display ") + components.RefreshRate.ToString("0.###", CultureInfo.CurrentCulture) + " Hz";

            // Stima bitrate medio dal container (fallback)
            int avgContainerKbps = 0;
            try
            {
                if (!string.IsNullOrEmpty(_currentPath) && File.Exists(_currentPath) && _duration > 1)
                {
                    var fi = new FileInfo(_currentPath);
                    avgContainerKbps = (int)Math.Round((fi.Length * 8.0 / 1000.0) / _duration);
                }
            }
            catch { }
            // Flussi di rete: la media del contenitore la dichiara la sorgente.
            if (avgContainerKbps <= 0 && _info is { OverallBitrateKbps: > 0 })
                avgContainerKbps = _info.OverallBitrateKbps;

            // ===== Audio da LAV Audio (IN/OUT + bitstream + kbpsNow) =====
            var selAudio = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
            string selName = selAudio?.Name ?? "";
            var lav = GetLavAudioIODetails(selName);
            bool bitstream = IsBitstream();
            if (_audioOutPref == AudioOutPref.ForcePcm)
                bitstream = false;
            bool engineHasVideo = _engine.HasDisplayControl();

            // "ora" audio: SOLO misura live (niente fallback su probe per evitare numeri statici)
            int audioNowKbps = _audioBitrateNowKbps > 0
                ? _audioBitrateNowKbps
                : ParseKbpsFromName(selName);

            // === MEDIE ===
            // 1) The container's declared per-stream average (MKV statistics tags, MP4/TS
            //    bit rate) is the real average of the whole stream: prefer it.
            // 2) Otherwise the live average integrated during playback.
            // 3) Otherwise estimates, which are labelled as such.
            var declared = _pktRateOk ? _pktRate.GetMetadataAverages() : (AudioKbps: 0, VideoKbps: 0);
            int audioAvgKbps = declared.AudioKbps > 0 ? declared.AudioKbps : (_audioAvgLiveKbps > 0) ? (int)Math.Round(_audioAvgLiveKbps) : 0;
            int videoAvgKbps = declared.VideoKbps > 0 ? declared.VideoKbps : (_videoAvgLiveKbps > 0) ? (int)Math.Round(_videoAvgLiveKbps) : 0;
            bool audioAvgEstimated = false;
            bool videoAvgEstimated = false;

            if (IsCurrentYouTube())
            {
                if (audioAvgKbps <= 0 && _currentWebAudioBitrateKbps > 0)
                {
                    audioAvgKbps = _currentWebAudioBitrateKbps;
                    audioAvgEstimated = true;
                }
                if (videoAvgKbps <= 0 && _currentWebVideoBitrateKbps > 0)
                {
                    videoAvgKbps = _currentWebVideoBitrateKbps;
                    videoAvgEstimated = true;
                }
            }

            if (audioAvgKbps <= 0)
            {
                // Nominal rates (track name, probe) are declared values, not estimates.
                if (!string.IsNullOrWhiteSpace(selName))
                    audioAvgKbps = ParseKbpsFromName(selName);
                if (audioAvgKbps <= 0)
                    audioAvgKbps = ProbeAudioAvgKbps();
                if (audioAvgKbps <= 0 && bitstream && lav.AudioNowKbps > 0)
                {
                    audioAvgKbps = lav.AudioNowKbps;
                    audioAvgEstimated = true;
                }
                if (audioAvgKbps <= 0 && avgContainerKbps > 0 && !engineHasVideo)
                    audioAvgKbps = avgContainerKbps; // audio-only file: the container is the track
            }

            if (videoAvgKbps <= 0 && avgContainerKbps > 0 && audioAvgKbps > 0)
            {
                // Container minus the selected audio also contains the other audio and
                // subtitle tracks: an upper bound, shown as an estimate.
                videoAvgKbps = Math.Max(0, avgContainerKbps - audioAvgKbps);
                videoAvgEstimated = videoAvgKbps > 0;
            }

            // Video "ora": calcolato altrove come residuo container-now – audio-now
            int videoNowKbps = _videoBitrateNowKbps > 0 ? _videoBitrateNowKbps : 0;

            if (!engineHasVideo)
            {
                videoNowKbps = 0;
                videoAvgKbps = 0;
            }

            string hdrTag = DescribeHdrOverlayMode(runtimeHdr);

            // Audio IN/OUT prettificato
            string audioIn = !string.IsNullOrWhiteSpace(lav.InDetail) && lav.InDetail != "n/d"
                ? lav.InDetail
                : PrettyAudioInFromProbe(_info);
            if ((string.IsNullOrWhiteSpace(audioIn) || audioIn == "n/d") && IsCurrentYouTube() && !string.IsNullOrWhiteSpace(_currentWebAudioCodec))
                audioIn = _currentWebAudioCodec!.ToUpperInvariant();
            string audioOut = lav.OutDetail;
            if (_engine is LibMpvPlaybackEngine mpvAudioEngine)
            {
                try
                {
                    var mpvAudio = mpvAudioEngine.GetAudioOverlayDetails();
                    if (!string.IsNullOrWhiteSpace(mpvAudio.InDetail) && mpvAudio.InDetail != "n/d")
                        audioIn = mpvAudio.InDetail;
                    if (!string.IsNullOrWhiteSpace(mpvAudio.OutDetail) && mpvAudio.OutDetail != "n/d")
                        audioOut = mpvAudio.OutDetail;
                    if (audioNowKbps <= 0 && mpvAudio.AudioNowKbps > 0)
                        audioNowKbps = mpvAudio.AudioNowKbps;
                    if (mpvAudio.AudioAvgKbps > 0)
                        audioAvgKbps = mpvAudio.AudioAvgKbps;
                    bitstream = mpvAudio.Bitstream;
                }
                catch { }
            }

            int inW = _info?.Width ?? 0;
            int inH = _info?.Height ?? 0;
            if ((inW <= 0 || inH <= 0) && components is { SourceWidth: > 0, SourceHeight: > 0 })
            {
                inW = components.SourceWidth;
                inH = components.SourceHeight;
            }
            if (IsCurrentYouTube())
            {
                if (_currentWebWidth > 0) inW = _currentWebWidth;
                if (_currentWebHeight > 0) inH = _currentWebHeight;
                if (string.IsNullOrWhiteSpace(runtimeVideoCodec) && !string.IsNullOrWhiteSpace(_currentWebVideoCodec))
                    runtimeVideoCodec = _currentWebVideoCodec!.ToUpperInvariant();
            }
            try
            {
                if (!_isLocalFile)
                {
                    var hintedDims = PlaybackTitleHints.GetDimensions(_currentPath ?? string.Empty);
                    if (hintedDims.Width > 0 && hintedDims.Height > 0)
                    {
                        inW = hintedDims.Width;
                        inH = hintedDims.Height;
                    }
                }
            }
            catch { }

            bool overlayUpscaling = renderer switch
            {
                VRChoice.MPV => runtimeUpscaling,
                VRChoice.MADVR => _enableUpscaling,
                _ => false
            };

            var identity = CurrentFilmIdentity();
            var s = new InfoOverlay.Stats
            {
                Title = identity.Title.Length > 0 ? identity.Title : InfoTitleForCurrent(),
                PosterPath = identity.Poster, MetaLine = identity.Meta, Overview = identity.Overview, CastLine = identity.Cast,

                VideoIn = _info != null || (inW > 0 && inH > 0) || !string.IsNullOrWhiteSpace(runtimeVideoCodec)
                    ? $"{(inW > 0 ? inW : (_info?.Width ?? 0))}x{(inH > 0 ? inH : (_info?.Height ?? 0))} • {fpsStr} fps"
                      + $" • {(!string.IsNullOrWhiteSpace(runtimeVideoCodec) ? runtimeVideoCodec : (_info != null ? CodecName(_info.VideoCodec) : "n/d"))}"
                      + $" • {(_info != null ? (_info.VideoBits > 0 ? _info.VideoBits + "-bit" : "n/d") : (!string.IsNullOrWhiteSpace(runtimeVideoFormat) ? runtimeVideoFormat : "n/d"))}"
                    : "n/d",
                VideoOut = outStr,
                VideoCodec = !string.IsNullOrWhiteSpace(runtimeVideoCodec) ? runtimeVideoCodec : (_info != null ? CodecName(_info.VideoCodec) : "n/d"),
                VideoPrimaries = !string.IsNullOrWhiteSpace(runtimePrimaries) ? runtimePrimaries : (_info != null ? PrimName(_info.Primaries) : "n/d"),
                VideoTransfer = !string.IsNullOrWhiteSpace(runtimeTransfer) ? runtimeTransfer : (_info != null ? TrcName(_info.Transfer) : "n/d"),

                VideoBitrateNow = videoNowKbps > 0 ? FmtKbps(videoNowKbps) : "n/d",
                VideoBitrateAvg = videoAvgKbps > 0 ? FmtKbps(videoAvgKbps) + (videoAvgEstimated ? Tx(" (stima)", " (estimate)") : string.Empty) : "n/d",

                AudioIn = string.IsNullOrWhiteSpace(audioIn) ? "n/d" : audioIn,
                AudioOut = string.IsNullOrWhiteSpace(audioOut) ? "n/d" : audioOut,
                AudioSync = string.IsNullOrEmpty(_currentPath) || ExternalAudioStore.Get(_currentPath) is not { } syncBinding ? "" : SyncStatus.Describe(syncBinding).Text,
                AudioBitrateNow = audioNowKbps > 0 ? FmtKbps(audioNowKbps) : "n/d",
                AudioBitrateAvg = audioAvgKbps > 0 ? FmtKbps(audioAvgKbps) + (audioAvgEstimated ? Tx(" (stima)", " (estimate)") : string.Empty) : "n/d",

                Renderer = renderer.ToString(),
                HdrMode = hdrTag,
                Upscaling = overlayUpscaling,
                Bitstream = bitstream,
                RtxHdr = IsRtxHdrLikelyActive(renderer, runtimeHdr)
            };
            FillInfoExtras(ref s, inW > 0 ? inW : (_info?.Width ?? 0), inH > 0 ? inH : (_info?.Height ?? 0), runtimeHdr, renderer);

            if (components != null)
            {
                s.VideoDecode = engineHasVideo ? DescribeVideoDecoder(components) : string.Empty;
                s.VideoFrames = engineHasVideo ? DescribeFrames(components) : string.Empty;
                s.VideoMatrix = engineHasVideo ? DescribeMatrix(components.YuvMatrix) : string.Empty;
                s.AudioDecode = DescribeAudioDecoder(components, bitstream);
                s.AudioDevice = _engine is LibMpvPlaybackEngine
                    ? (components.AudioOutputDriver.Length > 0 ? "mpv · " + components.AudioOutputDriver : string.Empty)
                    : components.AudioRenderer;
                if (components.FramesDropped >= 0) s.DroppedFrames = components.FramesDropped;

                // Renderer: versione e modo dichiarati dal renderer stesso.
                if (_engine is LibMpvPlaybackEngine)
                    s.Renderer = "mpv" + (components.PlayerVersion.Length > 0 ? " " + components.PlayerVersion : string.Empty) +
                                 (components.VideoOutputDriver.Length > 0 && engineHasVideo ? " · " + components.VideoOutputDriver : string.Empty);
                else if (components.RendererVersion.Length > 0)
                    s.Renderer += " " + components.RendererVersion;
                if (components.Exclusive == true)
                    s.Renderer += " · " + Tx("schermo intero esclusivo", "exclusive fullscreen");

                var processing = new List<string>();
                if (components.DxvaScaling == true) processing.Add(Tx("scaling DXVA", "DXVA scaling"));
                if (components.DxvaDeinterlacing == true) processing.Add(Tx("deinterlacciamento DXVA", "DXVA deinterlacing"));
                if (components.Ivtc == true) processing.Add("IVTC");
                s.ProcessingDetail = string.Join(" · ", processing);

                // Flussi di rete con mpv: quanto c'e' gia' in memoria e a che velocita' arriva.
                if (!_isLocalFile && components.CacheSeconds >= 0)
                    s.Buffer = components.CacheSeconds.ToString("0.#", CultureInfo.CurrentCulture) + " s" +
                               (components.CacheSpeedBytes > 0 ? " · " + (components.CacheSpeedBytes * 8 / 1000.0 / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " Mbit/s" : string.Empty);
            }

            _infoOverlay.SetStats(s);

            static string CodecName(AVCodecID id) => id switch
            {
                AVCodecID.AV_CODEC_ID_HEVC => "HEVC",
                AVCodecID.AV_CODEC_ID_H264 => "H.264",
                AVCodecID.AV_CODEC_ID_VP9 => "VP9",
                AVCodecID.AV_CODEC_ID_AV1 => "AV1",
                AVCodecID.AV_CODEC_ID_TRUEHD => "Dolby TrueHD",
                AVCodecID.AV_CODEC_ID_EAC3 => "Dolby Digital Plus",
                AVCodecID.AV_CODEC_ID_AC3 => "Dolby Digital",
                AVCodecID.AV_CODEC_ID_DTS => "DTS",
                _ => id.ToString().Replace("AV_CODEC_ID_", "")
            };
            static string PrimName(AVColorPrimaries p) =>
                p == AVColorPrimaries.AVCOL_PRI_BT2020 ? "BT.2020" :
                p == AVColorPrimaries.AVCOL_PRI_BT709 ? "BT.709" :
                p == AVColorPrimaries.AVCOL_PRI_SMPTE170M ? "SMPTE 170M" :
                p is AVColorPrimaries.AVCOL_PRI_UNSPECIFIED or AVColorPrimaries.AVCOL_PRI_RESERVED or AVColorPrimaries.AVCOL_PRI_RESERVED0 ? "n/d" : // non dichiarato dal file
                p.ToString().Replace("AVCOL_PRI_", "");
            static string TrcName(AVColorTransferCharacteristic t) =>
                t == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 ? "PQ" :
                t == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 ? "HLG" :
                t == AVColorTransferCharacteristic.AVCOL_TRC_BT709 ? "BT.709" :
                t is AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED or AVColorTransferCharacteristic.AVCOL_TRC_RESERVED or AVColorTransferCharacteristic.AVCOL_TRC_RESERVED0 ? "n/d" :
                t.ToString().Replace("AVCOL_TRC_", "");
        }

        // --- helper locali ---
        private static string PrettyChannels(int ch)
        {
            return ch switch
            {
                1 => "1.0",
                2 => "2.0",
                3 => "2.1",
                4 => "4.0",
                5 => "4.1",
                6 => "5.1",
                7 => "6.1",
                8 => "7.1",
                _ => $"{ch}ch"
            };
        }

        private static string LocalCodecName(AVCodecID id) => id switch
        {
            AVCodecID.AV_CODEC_ID_TRUEHD => "Dolby TrueHD",
            AVCodecID.AV_CODEC_ID_EAC3 => "Dolby Digital Plus",
            AVCodecID.AV_CODEC_ID_AC3 => "Dolby Digital",
            AVCodecID.AV_CODEC_ID_DTS => "DTS",
            AVCodecID.AV_CODEC_ID_FLAC => "FLAC",
            AVCodecID.AV_CODEC_ID_AAC => "AAC",
            _ => id.ToString().Replace("AV_CODEC_ID_", "")
        };

        private string PrettyAudioInFromProbe(MediaProbe.Result? r)
        {
            if (r == null || r.AudioCodec == 0) return "n/d";
            string c = !string.IsNullOrWhiteSpace(r.AudioCodecDisplayName)
                ? r.AudioCodecDisplayName
                : LocalCodecName(r.AudioCodec);
            string ch = (!r.AudioLooksObjectBased && r.AudioChannels > 0)
                ? " • " + PrettyChannels(r.AudioChannels)
                : "";
            string sr = r.AudioRate > 0 ? $" • {r.AudioRate / 1000.0:0.#} kHz" : "";
            return c + ch + sr;
        }
        private bool TryGetLavInAvgBytesPerSec(out int avgBps)
        {
            avgBps = 0;
            try
            {
                if (!TryGetFilterGraph(out var fg) || fg == null) return false;
                if (!TryFindFilter(fg, "LAV Audio", out var lav) || lav == null) return false;

                if (lav.EnumPins(out IEnumPins? ep) != 0 || ep == null) return false;
                var pins = new IPin[1];

                while (ep.Next(1, pins, IntPtr.Zero) == 0)
                {
                    var p = pins[0];
                    p.QueryPinInfo(out var pi);
                    try
                    {
                        if (pi.dir == PinDirection.Input)
                        {
                            var mt = new AMMediaType();
                            if (p.ConnectionMediaType(mt) == 0)
                            {
                                try
                                {
                                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                                    {
                                        var wfx = Marshal.PtrToStructure<Engines.WaveFormatEx>(mt.formatPtr);
                                        if (wfx.nAvgBytesPerSec > 0)
                                        {
                                            avgBps = (int)wfx.nAvgBytesPerSec;
                                            return true;
                                        }
                                    }
                                }
                                finally { DsUtils.FreeAMMediaType(mt); }
                            }
                        }
                    }
                    finally
                    {
                        if (pi.filter != null) Marshal.ReleaseComObject(pi.filter);
                        Marshal.ReleaseComObject(p);
                    }
                }
            }
            catch { }
            return false;
        }

        // ======= LAV Audio I/O Details (unica fonte per overlay audio) =======
        private (string InDetail, string OutDetail, bool Bitstream, int AudioNowKbps) GetLavAudioIODetails(string? selectedStreamName)
        {
            string inStr = "n/d";
            string outStr = "n/d";
            bool bitstream = IsBitstream(); // unica fonte di verità
            int kbpsNow = 0;

            try
            {
                // 1) prova a ottenere direttamente il filtro LAV Audio dall’engine
                IBaseFilter? lavAudio = null;
                try
                {
                    var t = _engine?.GetType();
                    if (t != null)
                    {
                        var pLav = t.GetProperty("LavAudioFilter",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (pLav?.GetValue(_engine) is IBaseFilter lav) lavAudio = lav;
                    }
                }
                catch { /* best-effort */ }

                // 2) fallback: cerca "LAV Audio" nel graph
                IFilterGraph2? fg = null;
                if (lavAudio == null)
                {
                    if (!TryGetFilterGraph(out fg) || fg == null) return (inStr, outStr, bitstream, kbpsNow);
                    if (!TryFindFilter(fg, "LAV Audio", out lavAudio) || lavAudio == null) return (inStr, outStr, bitstream, kbpsNow);
                }

                // 3) pin in/out connessi
                if (!TryGetLavPinsConnected(lavAudio, out var pinIn, out var pinOut))
                    return (inStr, outStr, bitstream, kbpsNow);

                AMMediaType mtIn = new AMMediaType();
                AMMediaType mtOut = new AMMediaType();
                AMMediaType mtDown = new AMMediaType();

                try
                {
                    // IN (a LAV)
                    if (pinIn != null && pinIn.ConnectionMediaType(mtIn) == 0)
                        inStr = PrettyFromIn(mtIn, selectedStreamName);

                    // OUT (da LAV) e DOWNSTREAM (ingresso renderer)
                    string detail = "n/d";
                    AMMediaType? mtChosen = null;

                    bool haveOut = (pinOut != null && pinOut.ConnectionMediaType(mtOut) == 0);
                    (bool? outIsPcm, string? outPretty) = (null, null);
                    if (haveOut)
                    {
                        var clsOut = ClassifyByWave(mtOut);
                        outIsPcm = clsOut?.isPcm;
                        outPretty = clsOut?.pretty;
                        (_, string detailOut) = PrettyOutFromLav(mtOut, selectedStreamName);
                        detail = detailOut;
                        mtChosen = mtOut;
                    }

                    (bool haveDown, bool? downIsPcm, string? downPretty) = (false, null, null);
                    if (pinOut != null && pinOut.ConnectedTo(out IPin? rIn) == 0 && rIn != null)
                    {
                        try
                        {
                            if (rIn.ConnectionMediaType(mtDown) == 0)
                            {
                                haveDown = true;
                                var clsDown = ClassifyByWave(mtDown);
                                downIsPcm = clsDown?.isPcm;
                                downPretty = clsDown?.pretty;

                                (_, string detailDown) = PrettyOutFromLav(mtDown, selectedStreamName);

                                // Se siamo in PCM: preferisci SEMPRE il downstream (canali reali del device)
                                if (downIsPcm == true)
                                {
                                    detail = detailDown;
                                    mtChosen = mtDown;
                                }
                                else
                                {
                                    // Bitstream o caso non PCM → scegli il più specifico come prima
                                    detail = PreferMoreSpecific(detailDown, detail);
                                    mtChosen = (detail == detailDown) ? mtDown : mtChosen;
                                }
                            }
                        }
                        finally { Marshal.ReleaseComObject(rIn); }
                    }

                    // Componi OutDetail coerente col flag dell’engine
                    if (bitstream)
                    {
                        string pretty = detail;
                        if (string.IsNullOrWhiteSpace(pretty) || pretty.Equals("n/d", StringComparison.OrdinalIgnoreCase))
                            pretty = "IEC61937";
                        if (!pretty.StartsWith("Bitstream", StringComparison.OrdinalIgnoreCase))
                            outStr = $"Bitstream {pretty}";
                        else
                            outStr = pretty;
                    }
                    else
                    {
                        // Se non abbiamo ancora scelto, prova a preferire mtDown se PCM, altrimenti mtOut
                        if (mtChosen == null)
                        {
                            if (haveDown && downIsPcm == true) mtChosen = mtDown;
                            else if (haveOut && outIsPcm == true) mtChosen = mtOut;
                        }

                        if (mtChosen != null)
                        {
                            var (_, rate, ch, bps, vbits, _) = ReadWave(mtChosen);
                            int validBits = vbits > 0 ? vbits : bps;

                            string rateStr = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "n/d";
                            string chStr = ch > 0 ? $"{ch}ch" : "n/d";

                            string bitStr = "n/d";
                            if (validBits > 0)
                            {
                                bitStr = $"{validBits}-bit";
                            }

                            outStr = $"PCM {rateStr} • {bitStr} • {chStr}";
                        }
                        else
                        {
                            outStr = "PCM";
                        }
                    }

                    // ===== Bitrate "ora" =====
                    if (!bitstream && mtChosen != null)
                    {
                        var (tag, rate, ch, bps, _, avgBytes) = ReadWave(mtChosen);

                        if (avgBytes > 0)
                        {
                            // ✅ data rate reale
                            kbpsNow = (int)Math.Round(avgBytes * 8 / 1000.0);
                        }
                        else
                        {
                            // fallback: usa container bits (non valid bits)
                            int containerBits = bps;
                            if (tag == 3 && containerBits <= 0) containerBits = 32; // float
                            if (rate > 0 && ch > 0 && containerBits > 0)
                                kbpsNow = (int)Math.Round(rate * containerBits * ch / 1000.0);
                        }
                    }
                    else
                    {
                        // BITSTREAM: stima il payload (non il trasporto IEC61937)
                        kbpsNow = ProbeAudioAvgKbps();

                        if (kbpsNow <= 0)
                        {
                            // se è AC-3/E-AC3/DTS core prova nAvgBytesPerSec dell’IN di LAV
                            bool likelyCore =
                                (inStr.IndexOf("Dolby Digital Plus", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                (inStr.IndexOf("Dolby Digital", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                (inStr.IndexOf("DTS-HD", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 inStr.IndexOf("DTS", StringComparison.OrdinalIgnoreCase) >= 0);

                            if (likelyCore && TryGetLavInAvgBytesPerSec(out int avgBps) && avgBps > 0)
                                kbpsNow = (int)Math.Round(avgBps * 8 / 1000.0);
                        }

                        if (kbpsNow <= 0)
                            kbpsNow = ParseKbpsFromName(selectedStreamName);
                    }
                }
                finally
                {
                    try { DsUtils.FreeAMMediaType(mtIn); } catch { }
                    try { DsUtils.FreeAMMediaType(mtOut); } catch { }
                    try { DsUtils.FreeAMMediaType(mtDown); } catch { }
                    try { if (pinIn != null) Marshal.ReleaseComObject(pinIn); } catch { }
                    try { if (pinOut != null) Marshal.ReleaseComObject(pinOut); } catch { }
                }
            }
            catch { /* lascia n/d */ }

            return (inStr, outStr, bitstream, kbpsNow);

            // ----------------- Helpers locali -----------------

            static bool TryGetLavPinsConnected(IBaseFilter lav, out IPin? pinIn, out IPin? pinOut)
            {
                pinIn = null; pinOut = null;
                if (lav.EnumPins(out IEnumPins? ep) != 0 || ep == null) return false;
                var pins = new IPin[1];
                while (ep.Next(1, pins, IntPtr.Zero) == 0)
                {
                    var p = pins[0];
                    p.QueryPinInfo(out var pi);
                    try
                    {
                        if (p.ConnectedTo(out IPin? other) == 0 && other != null)
                        {
                            if (pi.dir == PinDirection.Input && pinIn == null) pinIn = p;
                            if (pi.dir == PinDirection.Output && pinOut == null) pinOut = p;
                            Marshal.ReleaseComObject(other);
                            if (pinIn != null && pinOut != null) return true;
                        }
                        else
                        {
                            Marshal.ReleaseComObject(p);
                        }
                    }
                    finally
                    {
                        if (pi.filter != null) Marshal.ReleaseComObject(pi.filter);
                    }
                }
                return pinIn != null || pinOut != null;
            }

            static string PrettyFromIn(AMMediaType mtIn, string? selectedName)
            {
                string? pretty = PrettyFromWaveOrSubtype(mtIn);
                string? prettyFromName = PrettyFromName(selectedName);

                if (!string.IsNullOrWhiteSpace(prettyFromName)
                    && (string.IsNullOrWhiteSpace(pretty)
                        || pretty.Equals("IEC61937", StringComparison.OrdinalIgnoreCase)
                        || LooksObjectBasedFromName(selectedName)))
                {
                    pretty = prettyFromName;
                }

                if (string.IsNullOrEmpty(pretty))
                    pretty = prettyFromName;

                var (_, rate, ch, _, _, _) = ReadWave(mtIn);
                bool objectBased = LooksObjectBasedFromName(selectedName)
                    || (!string.IsNullOrWhiteSpace(pretty)
                        && (pretty.IndexOf("Atmos", StringComparison.OrdinalIgnoreCase) >= 0
                            || pretty.IndexOf("DTS:X", StringComparison.OrdinalIgnoreCase) >= 0));
                string rateStr = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "";
                string chStr = (!objectBased && ch > 0) ? $"{ch}ch" : "";
                string extra = string.Join(" • ", new[] { rateStr, chStr }.Where(s => !string.IsNullOrEmpty(s)));

                return string.IsNullOrEmpty(extra) ? (pretty ?? "n/d") : $"{(pretty ?? "n/d")} • {extra}";
            }

            // Ritorna (isPcmStimato, dettaglioHuman); il flag PCM qui è solo “descrittivo”.
            static (bool isPcm, string detail) PrettyOutFromLav(AMMediaType mtOut, string? selectedName)
            {
                (ushort tag, int rate, int ch, int bps, int vbits, int _) = ReadWave(mtOut);

                // 1) priorità a WaveEx/WaveExtensible
                var waveClass = ClassifyByWave(mtOut);
                if (waveClass.HasValue)
                {
                    bool isPcmWave = waveClass.Value.isPcm;
                    string? prettyWave = waveClass.Value.pretty;
                    string? prettyFromName = PrettyFromName(selectedName);

                    if (!string.IsNullOrWhiteSpace(prettyFromName)
                        && (string.IsNullOrWhiteSpace(prettyWave)
                            || prettyWave.Equals("IEC61937", StringComparison.OrdinalIgnoreCase)
                            || LooksObjectBasedFromName(selectedName)))
                    {
                        prettyWave = prettyFromName;
                    }

                    prettyWave ??= PrettyFromWaveOrSubtype(mtOut)
                                   ?? prettyFromName
                                   ?? "IEC61937";

                    if (isPcmWave)
                    {
                        int validBitsW = vbits > 0 ? vbits : bps;
                        string rateStrW = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "n/d";
                        string chStrW = ch > 0 ? $"{ch}ch" : "n/d";
                        string bitStrW = validBitsW > 0 ? $"{validBitsW}-bit" : "n/d";
                        return (true, $"PCM {rateStrW} • {bitStrW} • {chStrW}");
                    }

                    return (false, $"Bitstream {prettyWave}");
                }

                // 2) fallback: subType/tag
                bool isPcmBySubtype = (mtOut.subType == MediaSubType.PCM || mtOut.subType == MediaSubType.IEEE_FLOAT);
                bool isPcmByTag = (tag == 1 /*PCM*/ || tag == 3 /*IEEE_FLOAT*/);

                string rateStr = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "n/d";
                string chStr = ch > 0 ? $"{ch}ch" : "n/d";
                int validBits = vbits > 0 ? vbits : bps;
                string bitStr = validBits > 0 ? $"{validBits}-bit" : "n/d";

                if (isPcmBySubtype || isPcmByTag)
                    return (true, $"PCM {rateStr} • {bitStr} • {chStr}");

                string? pretty = PrettyFromWaveOrSubtype(mtOut);
                string? prettyFromFallbackName = PrettyFromName(selectedName);
                if (!string.IsNullOrWhiteSpace(prettyFromFallbackName)
                    && (string.IsNullOrWhiteSpace(pretty)
                        || pretty.Equals("IEC61937", StringComparison.OrdinalIgnoreCase)
                        || LooksObjectBasedFromName(selectedName)))
                {
                    pretty = prettyFromFallbackName;
                }
                pretty ??= "IEC61937";
                return (false, $"Bitstream {pretty}");
            }

            static (bool isPcm, string? pretty)? ClassifyByWave(AMMediaType mt)
            {
                try
                {
                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfex = Marshal.PtrToStructure<Engines.WaveFormatEx>(mt.formatPtr);

                        if (wfex.wFormatTag == 1 || wfex.wFormatTag == 3)
                            return (true, "PCM");

                        if (wfex.wFormatTag == 0x0092) // WAVE_FORMAT_DOLBY_AC3_SPDIF
                            return (false, "Dolby Digital");

                        if (wfex.wFormatTag == 0xFFFE && wfex.cbSize >= 22)
                        {
                            var ext = Marshal.PtrToStructure<WaveFormatExtensibleLocal>(mt.formatPtr);

                            var subStr = ext.SubFormat.ToString().ToUpperInvariant();
                            if (subStr.Contains("61937") || subStr.Contains("SPDIF"))
                                return (false, "IEC61937");

                            var s = ext.SubFormat.ToString().ToUpperInvariant();
                            string? pretty =
                                s.Contains("TRUEHD") || s.Contains("MLP") ? "Dolby TrueHD" :
                                s.Contains("EAC3") || s.Contains("DDPLUS") || s.Contains("DD+") ? "Dolby Digital Plus" :
                                s.Contains("AC3") || s.Contains("DOLBY_AC3") ? "Dolby Digital" :
                                (s.Contains("DTS_HD") && s.Contains("MA")) ? "DTS-HD MA" :
                                (s.Contains("DTS_HD") && (s.Contains("HRA") || s.Contains("HIGH"))) ? "DTS-HD HRA" :
                                s.Contains("DTS") ? "DTS" : null;

                            return (false, pretty);
                        }

                        return (false, null);
                    }
                }
                catch { }
                return null;
            }

            static (ushort wFormatTag, int nSamplesPerSec, int nChannels, int wBitsPerSample, int validBitsPerSample, int avgBytesPerSec) ReadWave(AMMediaType mt)
            {
                ushort tag = 0; int rate = 0; int ch = 0; int bps = 0; int vbits = 0; int avg = 0;
                try
                {
                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfex = Marshal.PtrToStructure<Engines.WaveFormatEx>(mt.formatPtr);
                        tag = wfex.wFormatTag;
                        rate = unchecked((int)wfex.nSamplesPerSec);
                        ch = unchecked((int)wfex.nChannels);
                        bps = wfex.wBitsPerSample;
                        avg = unchecked((int)wfex.nAvgBytesPerSec);

                        if (tag == 0xFFFE /*WAVE_FORMAT_EXTENSIBLE*/ && wfex.cbSize >= 22)
                        {
                            var ext = Marshal.PtrToStructure<WaveFormatExtensibleLocal>(mt.formatPtr);
                            if (ext.wValidBitsPerSample != 0) vbits = ext.wValidBitsPerSample;
                        }
                    }
                }
                catch { }
                return (tag, rate, ch, bps, vbits, avg);
            }

            static string? PrettyFromWaveOrSubtype(AMMediaType mt)
            {
                try
                {
                    var sub = mt.subType;
                    string g = sub.ToString().ToUpperInvariant();
                    if (g.Contains("AC3") || g.Contains("DOLBY_AC3")) return "Dolby Digital";
                    if (g.Contains("EAC3") || g.Contains("DDPLUS") || g.Contains("DD+")) return "Dolby Digital Plus";
                    if (g.Contains("TRUEHD") || g.Contains("MLP")) return "Dolby TrueHD";
                    if (g.Contains("DTS_HD") && g.Contains("MA")) return "DTS-HD MA";
                    if (g.Contains("DTS_HD") && (g.Contains("HRA") || g.Contains("HIGH"))) return "DTS-HD HRA";
                    if (g.Contains("DTS")) return "DTS";
                    if (g.Contains("AAC")) return "AAC";
                    if (g.Contains("OPUS")) return "Opus";
                    if (g.Contains("FLAC")) return "FLAC";
                    if (g.Contains("PCM")) return "PCM";
                    if (g.Contains("IEEE_FLOAT")) return "PCM float";

                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfex = Marshal.PtrToStructure<Engines.WaveFormatEx>(mt.formatPtr);
                        ushort tag = wfex.wFormatTag;
                        if (tag == 1) return "PCM";
                        if (tag == 3) return "PCM float";
                        if (tag == 0x0092) return "IEC61937";
                        if (tag == 0x2000) return "Dolby/DTS";
                    }

                    var sg = mt.subType.ToString().ToUpperInvariant();
                    if (sg.Contains("61937") || sg.Contains("SPDIF"))
                        return "IEC61937";
                }
                catch { }
                return null;
            }

            static string? PrettyFromName(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                string n = name.ToUpperInvariant();
                if (n.Contains("TRUEHD")) return n.Contains("ATMOS") || n.Contains("JOC") ? "Dolby TrueHD Atmos" : "Dolby TrueHD";
                if (n.Contains("E-AC3") || n.Contains("EAC3") || n.Contains("DDP") || n.Contains("DD+"))
                    return n.Contains("ATMOS") || n.Contains("JOC") ? "Dolby Digital Plus Atmos" : "Dolby Digital Plus";
                if (n.Contains("AC3") || n.Contains("DOLBY DIGITAL")) return "Dolby Digital";
                if (n.Contains("DTS:X") || n.Contains("DTS X")) return "DTS:X";
                if (n.Contains("DTS-HD MA") || n.Contains("DTS HD MA") || n.Contains("MASTER AUDIO")) return "DTS-HD MA";
                if (n.Contains("DTS-HD HRA") || n.Contains("HIGH RES")) return "DTS-HD HRA";
                if (n.Contains("DTS")) return "DTS";
                if (n.Contains("AAC")) return "AAC";
                if (n.Contains("OPUS")) return "Opus";
                if (n.Contains("FLAC")) return "FLAC";
                if (n.Contains("PCM")) return "PCM";
                return null;
            }

            static bool LooksObjectBasedFromName(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return false;
                string n = name.ToUpperInvariant();
                return n.Contains("ATMOS") || n.Contains("JOC") || n.Contains("DTS:X") || n.Contains("DTS X");
            }

            // preferisci stringhe non generiche (es. "DTS-HD MA" batte "IEC61937")
            static string PreferMoreSpecific(string a, string b)
            {
                bool AIsGeneric = a.IndexOf("IEC61937", StringComparison.OrdinalIgnoreCase) >= 0;
                bool BIsGeneric = b.IndexOf("IEC61937", StringComparison.OrdinalIgnoreCase) >= 0;
                if (AIsGeneric && !BIsGeneric) return b;
                if (BIsGeneric && !AIsGeneric) return a;
                return a.Length >= b.Length ? a : b;
            }
        }


        // Graph helpers
    }
}
