using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>Dati del file letti da FFmpeg (ffprobe per macOS).</summary>
public sealed class ProbeData
{
    public string Format = "";
    public double Duration;
    public long SizeBytes;
    public int OverallKbps;
    public bool HasVideo;
    public string VideoCodec = "", VideoProfile = "", PixFmt = "", Primaries = "", Transfer = "", ColorSpace = "", ColorRange = "";
    /// <summary>Capitoli del contenitore: inizio in secondi e titolo (puo' essere vuoto).</summary>
    public List<(double Start, string Title)> Chapters = new();
    public int Width, Height, VideoBits, DolbyVisionProfile, VideoKbps;
    public double Fps;
    public string AudioCodec = "", AudioProfile = "", AudioLayout = "";
    public int AudioChannels, AudioRate, AudioKbps;
}

/// <summary>Dati di riproduzione dal VideoHost (riga "media" e statistiche).</summary>
public sealed class RuntimeInfo
{
    public string Renderer = "", RendererVersion = "", VideoDecoder = "", VideoOut = "";
    public int OutWidth, OutHeight, AudioOutChannels, AudioOutRate, AudioOutBits;
    public bool AudioOutFloat;
    public int Dropped = -1;

    public void ParseMedia(string line)
    {
        foreach (var kv in line.Split(';'))
        {
            int eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            string k = kv[..eq], v = kv[(eq + 1)..];
            switch (k)
            {
                case "vdec": VideoDecoder = v; break;
                case "vout": VideoOut = v; break;
                case "voutsize":
                    var wh = v.Split('x');
                    if (wh.Length == 2) { int.TryParse(wh[0], out OutWidth); int.TryParse(wh[1], out OutHeight); }
                    break;
                case "aout": int.TryParse(v, out AudioOutChannels); break;
                case "arate": int.TryParse(v, out AudioOutRate); break;
                case "abits": int.TryParse(v, out AudioOutBits); break;
                case "afloat": AudioOutFloat = v == "1"; break;
                case "rver": RendererVersion = v; break;
            }
        }
    }
}

public static class MediaProbe
{
    public static async Task<ProbeData?> RunAsync(string file)
    {
        try
        {
            if (MacTools.Ffprobe is not { } exe) return null;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            string output = Path.Combine(Path.GetTempPath(), $"cinecore-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
            foreach (var a in new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", "-o", output, file }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { proc.Kill(); } catch { } Console.Error.WriteLine("ffprobe: tempo scaduto"); return null; }
            if (!File.Exists(output)) { Console.Error.WriteLine($"ffprobe: nessun risultato (uscita {proc.ExitCode})"); return null; }
            string json = await File.ReadAllTextAsync(output);
            try { File.Delete(output); } catch { }
            return Parse(json);
        }
        catch (Exception ex) { Console.Error.WriteLine("ffprobe: " + ex.Message); return null; }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(v.GetString(), out int n) ? n : 0 : 0;
    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && double.TryParse(v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;

    private static double Rate(string r)
    {
        var p = r.Split('/');
        if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > 0) return a / b;
        return 0;
    }

    /// <summary>Media dichiarata dal contenitore (statistiche MKV "BPS"), altrimenti il bit_rate del flusso.</summary>
    private static int StreamKbps(JsonElement s)
    {
        if (s.TryGetProperty("tags", out var tags))
            foreach (var t in tags.EnumerateObject())
                if ((t.Name.Equals("BPS", StringComparison.OrdinalIgnoreCase) || t.Name.StartsWith("BPS-", StringComparison.OrdinalIgnoreCase))
                    && long.TryParse(t.Value.GetString(), out long bps) && bps > 0) return (int)(bps / 1000);
        double br = Dbl(s, "bit_rate");
        return br > 0 ? (int)(br / 1000) : 0;
    }

    private static ProbeData? Parse(string json)
    {
        // Sotto Wine possono arrivare righe di log prima del JSON (MoltenVK): si parte dalla prima graffa a inizio riga.
        int start = json.StartsWith('{') ? 0 : json.IndexOf("\n{", StringComparison.Ordinal) + 1;
        if (start < 0 || start >= json.Length) return null;
        json = json[start..];
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var r = new ProbeData();
        if (root.TryGetProperty("format", out var f))
        {
            r.Format = Str(f, "format_name");
            r.Duration = Dbl(f, "duration");
            r.SizeBytes = (long)Dbl(f, "size");
            r.OverallKbps = (int)(Dbl(f, "bit_rate") / 1000);
        }
        if (root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            foreach (var c in chapters.EnumerateArray())
                r.Chapters.Add((Dbl(c, "start_time"), c.TryGetProperty("tags", out var ct) ? Str(ct, "title") : ""));
        if (!root.TryGetProperty("streams", out var streams)) return r;
        JsonElement? video = null, audio = null, firstAudio = null;
        foreach (var s in streams.EnumerateArray())
        {
            string type = Str(s, "codec_type");
            bool attached = s.TryGetProperty("disposition", out var d) && Int(d, "attached_pic") == 1;
            bool isDefault = s.TryGetProperty("disposition", out var d2) && Int(d2, "default") == 1;
            if (type == "video" && !attached && video == null) video = s;
            if (type == "audio") { firstAudio ??= s; if (isDefault && audio == null) audio = s; }
        }
        audio ??= firstAudio;
        if (video is { } v)
        {
            r.HasVideo = true;
            r.VideoCodec = Str(v, "codec_name"); r.VideoProfile = Str(v, "profile");
            r.Width = Int(v, "width"); r.Height = Int(v, "height");
            r.PixFmt = Str(v, "pix_fmt");
            r.Primaries = Str(v, "color_primaries"); r.Transfer = Str(v, "color_transfer");
            r.ColorSpace = Str(v, "color_space"); r.ColorRange = Str(v, "color_range");
            r.Fps = Rate(Str(v, "avg_frame_rate")) is > 0 and var avg ? avg : Rate(Str(v, "r_frame_rate"));
            int raw = Int(v, "bits_per_raw_sample");
            r.VideoBits = raw > 0 ? raw : Regex.Match(r.PixFmt, @"p(\d{2})") is { Success: true } m ? int.Parse(m.Groups[1].Value) : r.PixFmt.Length > 0 ? 8 : 0;
            r.VideoKbps = StreamKbps(v);
            if (v.TryGetProperty("side_data_list", out var side))
                foreach (var sd in side.EnumerateArray())
                    if (Str(sd, "side_data_type").Contains("DOVI", StringComparison.OrdinalIgnoreCase)) r.DolbyVisionProfile = Math.Max(1, Int(sd, "dv_profile"));
        }
        if (audio is { } a)
        {
            r.AudioCodec = Str(a, "codec_name"); r.AudioProfile = Str(a, "profile");
            r.AudioChannels = Int(a, "channels"); r.AudioLayout = Str(a, "channel_layout");
            r.AudioRate = Int(a, "sample_rate");
            r.AudioKbps = StreamKbps(a);
        }
        return r;
    }
}

/// <summary>
/// Contenuto della scheda Info: stessa scelta e stesso ordine di InfoOverlay.Sections() su Windows
/// (File, Video, Visualizzazione, Audio, Uscita audio, Sistema), stessi testi e formati.
/// </summary>
public static class InfoBuilder
{
    // Formato italiano senza dati di cultura (l'app gira in modalita' globalizzazione invariante).
    internal static readonly NumberFormatInfo It = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NumberGroupSizes = new[] { 3 } };

    public static (string Title, string Summary, IReadOnlyList<(string Title, IReadOnlyList<(string Label, string Value)> Cells)> Sections)
        Build(string title, string? file, ProbeData? p, RuntimeInfo rt, double durationSec, (double X, double Y) windowCenter,
            int liveVideoKbps = 0, int liveAudioKbps = 0)
    {
        p ??= new ProbeData();
        // --- campi come InfoOverlay.Stats
        string fps = p.Fps > 0 ? (Math.Abs(p.Fps - 23.976) < 0.01 ? "23.976" : Math.Abs(p.Fps - 29.97) < 0.01 ? "29.970" : p.Fps.ToString("0.###", CultureInfo.InvariantCulture)) + " fps" : "";
        string codec = VideoCodecName(p.VideoCodec);
        string resolution = p.Width > 0 && p.Height > 0 ? $"{p.Width}×{p.Height}" : "";
        string resTag = p.Width <= 0 || p.Height <= 0 ? "" : p.Width >= 3200 || p.Height >= 2000 ? "4K" : p.Width >= 1800 || p.Height >= 1000 ? "1080p" : p.Width >= 1200 || p.Height >= 700 ? "720p" : "SD";
        string resolutionCell = resolution.Length == 0 ? resTag : resTag.Length > 0 && !resolution.Contains(resTag) ? $"{resolution} ({resTag})" : resolution;
        string primaries = p.Primaries switch { "bt2020" => "BT.2020", "bt709" => "BT.709", "smpte170m" => "SMPTE 170M", "" or "unknown" or "reserved" => "", var x => x.ToUpperInvariant() };
        string transfer = p.Transfer switch { "smpte2084" => "PQ", "arib-std-b67" => "HLG", "bt709" => "BT.709", "" or "unknown" or "reserved" => "", var x => x.ToUpperInvariant() };
        string fileName = file != null ? Path.GetFileName(file) : "";
        bool dolbyVision = p.DolbyVisionProfile > 0 || fileName.Contains(".DV.", StringComparison.OrdinalIgnoreCase) || fileName.Contains(" DV ", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("DoVi", StringComparison.OrdinalIgnoreCase) || fileName.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase);
        string dynamicRange = !p.HasVideo ? "" : transfer == "HLG" ? "HLG" : transfer == "PQ" ? (dolbyVision ? "Dolby Vision" : "HDR10") : "SDR";

        var videoOut = new List<string>();
        int ow = rt.OutWidth > 0 ? rt.OutWidth : p.Width, oh = rt.OutHeight > 0 ? rt.OutHeight : p.Height;
        if (ow > 0 && oh > 0) videoOut.Add($"{ow}×{oh}");
        if (fps.Length > 0) videoOut.Add(fps);

        string audioName = AudioCodecName(p.AudioCodec, p.AudioProfile);
        bool objectBased = p.AudioProfile.Contains("Atmos", StringComparison.OrdinalIgnoreCase) || p.AudioProfile.Contains("DTS:X", StringComparison.OrdinalIgnoreCase);
        string channels = p.AudioChannels > 0 ? PrettyChannels(p.AudioChannels) : "";
        string sampling = p.AudioRate > 0 ? (p.AudioRate / 1000.0).ToString("0.#", It) + " kHz" : "";
        string audioTag = audioName.Length is > 0 and <= 22 ? audioName : "";

        var audioOut = new List<string>();
        if (rt.AudioOutRate > 0)
        {
            audioOut.Add("PCM " + (rt.AudioOutRate / 1000.0).ToString("0.0", It) + " kHz");
            if (rt.AudioOutBits > 0) audioOut.Add(rt.AudioOutBits + " bit" + (rt.AudioOutFloat ? " float" : ""));
            if (rt.AudioOutChannels > 0) audioOut.Add(rt.AudioOutChannels + "ch");
        }

        string audioDecoder = p.AudioCodec.Length == 0 ? "" :
            rt.Renderer.StartsWith("mpv", StringComparison.OrdinalIgnoreCase) ? "mpv" : "LAV Audio";

        string decoder = rt.VideoDecoder.Trim();
        string decodeText = "";
        if (decoder.Length > 0 && !decoder.Equals("inactive", StringComparison.OrdinalIgnoreCase))
        {
            string lower = decoder.ToLowerInvariant();
            string how = lower.Contains("d3d11") ? "D3D11" + (lower.Contains("native") ? " nativo" : lower.Contains("cb") ? " copy-back" : "")
                : lower.Contains("dxva2n") ? "DXVA2 nativo" : lower.Contains("dxva2") ? "DXVA2 copy-back" : lower.Contains("avcodec") ? "software (CPU)" : decoder;
            decodeText = rt.Renderer.StartsWith("mpv", StringComparison.OrdinalIgnoreCase)
                ? "mpv   " + (lower.Contains("videotoolbox") ? "VideoToolbox (hardware)" : "software (CPU)")
                : "LAV Video   " + how;
        }

        string renderer = rt.Renderer.StartsWith("madvr", StringComparison.OrdinalIgnoreCase) ? "madVR"
            : rt.Renderer.StartsWith("mpcvr", StringComparison.OrdinalIgnoreCase) ? "MPC Video Renderer"
            : rt.Renderer.StartsWith("mpv", StringComparison.OrdinalIgnoreCase) ? "MPV (libmpv)" : rt.Renderer;
        if (renderer.Length > 0 && rt.RendererVersion.Length > 0) renderer += " " + rt.RendererVersion;

        double duration = p.Duration > 1 ? p.Duration : durationSec;
        string durationText = !(duration > 1) ? "" : duration >= 3600 ? $"{(int)(duration / 3600)}h {(int)(duration % 3600 / 60):00}m" : $"{(int)(duration / 60)}m {(int)(duration % 60):00}s";

        // Medie: quella dichiarata dal contenitore; senza, il contenitore meno l'audio (stima).
        int audioAvg = p.AudioKbps, videoAvg = p.VideoKbps;
        bool videoEstimated = false;
        bool web = file != null && (file.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || file.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        long size = p.SizeBytes > 0 ? p.SizeBytes : file != null && File.Exists(file) ? new FileInfo(file).Length : 0;
        // Una playlist HLS "pesa" pochi kB: dimensione e bitrate complessivo sarebbero 0 MB e 2 kbps. Meglio niente.
        bool playlist = p.Format.Contains("hls", StringComparison.OrdinalIgnoreCase);
        if (playlist || (web && size < 1024 * 1024)) size = 0;
        int containerKbps = playlist ? 0 : p.OverallKbps > 0 ? p.OverallKbps : size > 0 && duration > 1 ? (int)Math.Round(size * 8.0 / 1000.0 / duration) : 0;
        if (containerKbps < 16) containerKbps = 0;
        if (videoAvg <= 0 && containerKbps > 0 && audioAvg > 0) { videoAvg = containerKbps - audioAvg; videoEstimated = videoAvg > 0; }
        string videoAvgText = videoAvg > 0 ? Kbps(videoAvg) + (videoEstimated ? " (stima)" : "") : "";

        string sizeText = size <= 0 ? "" : size / 1024d / 1024d / 1024d >= 1 ? (size / 1024d / 1024d / 1024d).ToString("0.0#", It) + " GB" : (size / 1024d / 1024d).ToString("0", It) + " MB";
        string ext = file != null && !web ? Path.GetExtension(file).TrimStart('.').ToUpperInvariant() : "";
        // Indirizzi web: l'"estensione" e' un pezzo dell'URL; vale il formato letto da ffprobe.
        string container = web ? WebContainer(p.Format)
            : ext switch { "MKV" => "Matroska", "MP4" or "M4V" => "MP4", "TS" or "M2TS" => "MPEG-TS", "" => p.Format, _ => ext };

        var sections = new List<(string, IReadOnlyList<(string, string)>)>();
        void Add(string name, params (string Label, string Value)[] cells)
        {
            var list = cells.Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToList();
            if (list.Count > 0) sections.Add((name, list));
        }
        Add("File",
            ("Contenitore", container),
            ("Dimensione", sizeText),
            ("Durata", durationText),
            ("Bitrate complessivo", containerKbps > 0 ? Kbps(containerKbps) : ""),
            ("Origine", file != null ? Origin(file) : ""));
        if (p.HasVideo || rt.VideoDecoder.Length > 0)
        {
            Add("Video",
                ("Codec", string.Join("   ", new[] { codec, p.VideoBits > 0 ? p.VideoBits + " bit" : "" }.Where(x => x.Length > 0))),
                ("Risoluzione", resolutionCell),
                ("Frequenza fotogrammi", fps),
                ("Colore", string.Join("   ", new[] { dynamicRange, primaries, transfer }.Where(x => x.Length > 0).Distinct())),
                ("Bitrate medio", videoAvgText),
                ("Bitrate attuale", liveVideoKbps > 0 ? Kbps(liveVideoKbps) : ""));
            Add("Visualizzazione",
                ("Renderer", renderer),
                ("Decodifica", decodeText),
                ("Uscita", string.Join("   ", videoOut)),
                ("Fotogrammi persi", rt.Dropped >= 0 ? rt.Dropped.ToString("N0", It) : ""),
                ("Schermo", MacSystem.DisplayMode(windowCenter.X, windowCenter.Y)));
        }
        // Senza traccia audio non c'è nulla da dire sull'uscita.
        string device = p.AudioCodec.Length > 0 || rt.AudioOutRate > 0 ? MacSystem.DefaultOutputDeviceName() : "";
        Add("Audio",
            ("Codec", audioName),
            ("Sorgente", string.Join("   ", new[] { objectBased ? "" : channels, sampling, audioAvg > 0 ? Kbps(audioAvg) : "" }.Where(x => x.Length > 0))),
            ("Bitrate attuale", liveAudioKbps > 0 ? Kbps(liveAudioKbps) : ""),
            ("Uscita", string.Join("   ", audioOut)),
            ("Decodifica", audioDecoder),
            ("Dispositivo", device));

        string summary = string.Join("    ", new[] { resolutionCell, dynamicRange, audioTag, durationText }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return (title, summary, sections);
    }

    private static string WebContainer(string format) =>
        format.Contains("hls", StringComparison.OrdinalIgnoreCase) ? "HLS"
        : format.Contains("dash", StringComparison.OrdinalIgnoreCase) ? "DASH"
        : format.Contains("webm", StringComparison.OrdinalIgnoreCase) || format.Contains("matroska", StringComparison.OrdinalIgnoreCase) ? "WebM"
        : format.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? "MP4"
        : format.Contains("mpegts", StringComparison.OrdinalIgnoreCase) ? "MPEG-TS" : format.ToUpperInvariant();

    private static string Kbps(int kbps) => kbps >= 1000 ? (kbps / 1000d).ToString("0.0", It) + " Mbps" : kbps.ToString("0", It) + " kbps";

    private static string PrettyChannels(int ch) => ch switch { 1 => "1.0", 2 => "2.0", 3 => "2.1", 4 => "4.0", 5 => "4.1", 6 => "5.1", 7 => "6.1", 8 => "7.1", _ => $"{ch}ch" };

    private static string VideoCodecName(string c) => c switch
    {
        "" => "", "hevc" => "HEVC", "h264" => "H.264", "vp9" => "VP9", "av1" => "AV1", "mpeg2video" => "MPEG-2", "vc1" => "VC-1", "mpeg4" => "MPEG-4",
        _ => c.ToUpperInvariant(),
    };

    private static string AudioCodecName(string c, string profile) => c switch
    {
        "" => "",
        "truehd" => profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase) ? "Dolby TrueHD Atmos" : "Dolby TrueHD",
        "eac3" => profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase) ? "Dolby Digital Plus Atmos" : "Dolby Digital Plus",
        "ac3" => "Dolby Digital",
        "dts" => profile.Length > 0 && profile != "DTS" ? profile : "DTS",
        "aac" => "AAC", "flac" => "FLAC", "opus" => "Opus", "vorbis" => "Vorbis", "mp3" => "MP3", "alac" => "ALAC",
        _ when c.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
        _ => c.ToUpperInvariant(),
    };

    /// <summary>Come DescribeOrigin: il tipo di disco da cui arriva il file (sul Mac il volume).</summary>
    private static string Origin(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return "Streaming di rete";
        try
        {
            string full = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives().Where(d => full.StartsWith(d.Name, PathText.Comparison)).OrderByDescending(d => d.Name.Length).FirstOrDefault();
#if CINECORE_WINDOWS
            string name = drive == null ? "" : drive.Name.TrimEnd('\\');
#else
            string name = drive == null || drive.Name == "/" ? "Macintosh HD" : Path.GetFileName(drive.Name.TrimEnd('/'));
#endif
            return drive?.DriveType switch
            {
                DriveType.Network => "Unità di rete " + name,
                DriveType.CDRom => "Disco ottico " + name,
                DriveType.Removable => "Unità rimovibile " + name,
                _ => "File locale",
            };
        }
        catch { return ""; }
    }
}

/// <summary>Schermo e uscita audio del Mac (CoreGraphics, CoreAudio).</summary>
internal static class MacSystem
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CA = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
#if CINECORE_WINDOWS
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, X, Y, Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int Bits, Width, Height, DisplayFlags, Frequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);
#endif
    [DllImport(CG)] private static extern int CGGetDisplaysWithPoint(CGPoint point, uint max, uint[] displays, out uint count);
    [DllImport(CG)] private static extern uint CGMainDisplayID();
    [DllImport(CG)] private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
    [DllImport(CG)] private static extern void CGDisplayModeRelease(IntPtr mode);
    [DllImport(CG)] private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);
    [DllImport(CG)] private static extern nuint CGDisplayModeGetPixelHeight(IntPtr mode);
    [DllImport(CG)] private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);

    /// <summary>"2940×1912 · 60 Hz" come DescribeCurrentDisplayMode (lo schermo sotto la finestra).</summary>
    public static string DisplayMode(double x, double y)
    {
#if CINECORE_WINDOWS
        try
        {
            uint display = MacDisplayRate.DisplayAt(x, y);
            if (MacDisplayRate.DeviceName(display) is not { } device) return "";
            var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(device, -1 /* ENUM_CURRENT_SETTINGS */, ref mode)) return "";
            double hz = MacDisplayRate.CurrentHz(display);
            if (hz <= 0) hz = mode.Frequency;
            return $"{mode.Width}×{mode.Height} · {hz.ToString("0.###", InfoBuilder.It)} Hz";
        }
        catch { return ""; }
#endif
        try
        {
            var ids = new uint[1];
            uint display = CGGetDisplaysWithPoint(new CGPoint { X = x, Y = y }, 1, ids, out uint n) == 0 && n > 0 ? ids[0] : CGMainDisplayID();
            IntPtr mode = CGDisplayCopyDisplayMode(display);
            if (mode == IntPtr.Zero) return "";
            try
            {
                double hz = CGDisplayModeGetRefreshRate(mode);
                // I pannelli ProMotion/Liquid Retina dichiarano 0: la frequenza fissa e' 60 Hz.
                if (hz <= 0) hz = 60;
                return $"{CGDisplayModeGetPixelWidth(mode)}×{CGDisplayModeGetPixelHeight(mode)} · {hz.ToString("0.###", InfoBuilder.It)} Hz";
            }
            finally { CGDisplayModeRelease(mode); }
        }
        catch { return ""; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyAddress { public uint Selector, Scope, Element; }
    [DllImport(CA)] private static extern int AudioObjectGetPropertyData(uint id, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint size, out uint data);
    [DllImport(CA, EntryPoint = "AudioObjectGetPropertyData")] private static extern int AudioObjectGetPropertyDataPtr(uint id, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint size, out IntPtr data);
    [DllImport(CF)] private static extern long CFStringGetLength(IntPtr s);
    [DllImport(CF)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, long size, uint encoding);
    [DllImport(CF)] private static extern void CFRelease(IntPtr p);

    private static uint FourCC(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    /// <summary>
    /// Il dispositivo su cui suona l'audio: il VideoHost usa DirectSound di Wine, che va sull'uscita predefinita di macOS.
    /// Testo come su Windows ("DirectSound: nome"), che la scheda riduce al solo nome.
    /// </summary>
    public static string DefaultOutputDeviceName()
    {
#if CINECORE_WINDOWS
        string uid = MacAudioDevices.ConfiguredOutputUid;
        var output = MacAudioDevices.Outputs().Find(d => uid.Length > 0 ? d.Uid == uid : d.IsDefault);
        return output?.Name ?? "";
#endif
        try
        {
            var addr = new PropertyAddress { Selector = FourCC("dOut"), Scope = FourCC("glob"), Element = 0 };
            uint size = 4;
            if (AudioObjectGetPropertyData(1, ref addr, 0, IntPtr.Zero, ref size, out uint device) != 0 || device == 0) return "";
            var nameAddr = new PropertyAddress { Selector = FourCC("lnam"), Scope = FourCC("glob"), Element = 0 };
            size = (uint)IntPtr.Size;
            if (AudioObjectGetPropertyDataPtr(device, ref nameAddr, 0, IntPtr.Zero, ref size, out IntPtr cf) != 0 || cf == IntPtr.Zero) return "";
            try
            {
                var buf = new byte[1024];
                return CFStringGetCString(cf, buf, buf.Length, 0x08000100) ? System.Text.Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0) is var z and >= 0 ? z : buf.Length) : "";
            }
            finally { CFRelease(cf); }
        }
        catch { return ""; }
    }
}
