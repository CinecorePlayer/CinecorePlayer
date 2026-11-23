using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities;

/// <summary>
/// Analisi HDR del Mac. Stessi conti e stessi risultati dell'HdrAnalyzer di Windows (FFmpeg.AutoGen), ma i
/// fotogrammi arrivano dal programma ffmpeg: il FFmpeg di Homebrew (8) non e' quello per cui e' scritto
/// FFmpeg.AutoGen (7.1). Per ogni campione ffmpeg salta al fotogramma chiave, lo decodifica (VideoToolbox se
/// c'e') e lo riduce per campionamento (pixel veri, nessuna media) in R'G'B' a 16 bit pieni; qui si ricavano
/// picco, media, percentili, istogramma, gamut e gli strumenti. La riproduzione non viene toccata.
/// </summary>
internal static class HdrAnalyzer
{
    public const int HistogramBins = 64;
    private const int CurrentVersion = 3;

    public sealed class Sample
    {
        public double Time { get; set; }
        /// <summary>Picco del fotogramma (99,99° percentile: un pixel isolato non conta).</summary>
        public float PeakNits { get; set; }
        /// <summary>Media del fotogramma (frame-average light level).</summary>
        public float AverageNits { get; set; }
    }

    /// <summary>Misura di un solo fotogramma (analisi in tempo reale).</summary>
    public sealed class FrameStats
    {
        public double Time { get; set; }
        public float PeakNits { get; set; }
        public float AverageNits { get; set; }
        public double[] Histogram { get; set; } = new double[HistogramBins];
        public double ShareAbove100 { get; set; }
        public double ShareAbove400 { get; set; }
        public double ShareAbove1000 { get; set; }
        public double GamutRec709 { get; set; }
        public double GamutP3 { get; set; }
        public double GamutRec2020 { get; set; }
        public bool GamutMeasured { get; set; }
        public float MaxNits { get; set; }
        public float LuminanceNits { get; set; }
        public float BlackNits { get; set; }
        public float MedianNits { get; set; }
        public float P90Nits { get; set; }
        public float P99Nits { get; set; }
        public int ActiveWidth { get; set; }
        public int ActiveHeight { get; set; }
    }

    public sealed class Result
    {
        public int Version { get; set; }
        public int RequestedSamples { get; set; }
        public bool IsHdr { get; set; }
        /// <summary>"PQ", "HLG" oppure "SDR".</summary>
        public string Transfer { get; set; } = "SDR";
        public string Primaries { get; set; } = "";
        public string ColorSpace { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int BitDepth { get; set; }
        public double Duration { get; set; }
        public double StartTime { get; set; }

        public int? DeclaredMaxCll { get; set; }
        public int? DeclaredMaxFall { get; set; }
        public double? MasteringMaxNits { get; set; }
        public double? MasteringMinNits { get; set; }
        public int? DolbyVisionProfile { get; set; }
        public int? DolbyVisionCompatibility { get; set; }
        public bool Hdr10Plus { get; set; }
        public bool BaseLayerNotMeasurable { get; set; }

        public List<Sample> Samples { get; set; } = new();
        public float MeasuredPeakNits { get; set; }
        public double MeasuredPeakTime { get; set; }
        public float MeasuredMaxAverageNits { get; set; }
        public float MedianPeakNits { get; set; }
        public float OverallAverageNits { get; set; }
        public double[] Histogram { get; set; } = new double[HistogramBins];
        public double ShareAbove100 { get; set; }
        public double ShareAbove400 { get; set; }
        public double ShareAbove1000 { get; set; }
        public double GamutRec709 { get; set; }
        public double GamutP3 { get; set; }
        public double GamutRec2020 { get; set; }
        public bool GamutMeasured { get; set; }
        public float MeasuredMaxNits { get; set; }
        public double MeasuredMaxTime { get; set; }
        public float LuminanceAverageNits { get; set; }
        public float BlackNits { get; set; }
        public float MedianNits { get; set; }
        public float P90Nits { get; set; }
        public float P99Nits { get; set; }
        public int ActiveWidth { get; set; }
        public int ActiveHeight { get; set; }
        public string? Error { get; set; }

        public Result Snapshot()
        {
            var copy = (Result)MemberwiseClone();
            copy.Samples = new List<Sample>(Samples);
            copy.Histogram = (double[])Histogram.Clone();
            return copy;
        }
    }

    // ===== Scala PQ (SMPTE ST 2084) =====
    private const double M1 = 2610 / 16384.0, M2 = 2523 / 4096.0 * 128, C1 = 3424 / 4096.0, C2 = 2413 / 4096.0 * 32, C3 = 2392 / 4096.0 * 32;

    public static double PqToNits(double signal)
    {
        double e = Math.Pow(Math.Clamp(signal, 0, 1), 1 / M2);
        return 10000 * Math.Pow(Math.Max(e - C1, 0) / (C2 - C3 * e), 1 / M1);
    }

    public static double NitsToPq(double nits)
    {
        double y = Math.Pow(Math.Clamp(nits, 0, 10000) / 10000, M1);
        return Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2);
    }

    private const int LutSize = 4096;
    private static float[]? _pqNits, _hlgNits;

    private static float[] PqNitsLut()
    {
        if (_pqNits != null) return _pqNits;
        var lut = new float[LutSize];
        for (int i = 0; i < LutSize; i++) lut[i] = (float)PqToNits(i / (double)(LutSize - 1));
        return _pqNits = lut;
    }

    private static float[]? _sdrNits;

    /// <summary>SDR: BT.1886 (gamma 2,4) con il bianco di riferimento a 100 nit, il riferimento dei master SDR.</summary>
    private static float[] SdrNitsLut()
    {
        if (_sdrNits != null) return _sdrNits;
        var lut = new float[LutSize];
        for (int i = 0; i < LutSize; i++) lut[i] = (float)(SdrReferenceWhite * Math.Pow(i / (double)(LutSize - 1), 2.4));
        return _sdrNits = lut;
    }

    public const double SdrReferenceWhite = 100;

    private static float[] HlgNitsLut()
    {
        if (_hlgNits != null) return _hlgNits;
        const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
        var lut = new float[LutSize];
        for (int i = 0; i < LutSize; i++)
        {
            double e = i / (double)(LutSize - 1);
            double scene = e <= 0.5 ? e * e / 3 : (Math.Exp((e - c) / a) + b) / 12;
            lut[i] = (float)(1000 * Math.Pow(scene, 1.2));
        }
        return _hlgNits = lut;
    }

    // ===== Cache su disco (stesso formato JSON di Windows) =====
    private static string CacheFolder => Path.Combine(CinecoreMac.LibraryStore.AppDataDir, "hdr-analysis");

    private static string? CachePath(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            // "|keyframes-v2": dal 18/11 si misurano solo fotogrammi chiave interi (prima nei TS anche fotogrammi rovinati).
            string identity = info.FullName.ToUpperInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|keyframes-v2";
            string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
            return Path.Combine(CacheFolder, hash + ".json");
        }
        catch { return null; }
    }

    public static Result? TryLoadCached(string path, int minimumSamples)
    {
        try
        {
            string? file = CachePath(path);
            if (file == null || !File.Exists(file)) return null;
            var result = JsonSerializer.Deserialize<Result>(File.ReadAllText(file));
            return result != null && result.Version == CurrentVersion && result.Error == null &&
                   result.RequestedSamples >= minimumSamples ? result : null;
        }
        catch { return null; }
    }

    private static void SaveCache(string path, Result result)
    {
        try
        {
            string? file = CachePath(path);
            if (file == null) return;
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(file, JsonSerializer.Serialize(result));
        }
        catch { }
    }

    // ===== Cio' che il file dichiara (ffprobe) =====

    /// <summary>Formato, dimensioni, durata e metadati HDR, compresi quelli del primo fotogramma (HDR10+, MaxCLL nei SEI).</summary>
    public static Result ReadDeclared(string path)
    {
        var result = new Result { Version = CurrentVersion };
        try
        {
            if (CinecoreMac.MacTools.Ffprobe is not { } exe) { result.Error = "ffprobe"; return result; }
            string json = RunText(exe, 20000, "-v", "quiet", "-print_format", "json", "-select_streams", "v:0", "-read_intervals", "%+#1",
                "-show_streams", "-show_format", "-show_frames", path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) { result.Error = "no-video"; return result; }
            var v = streams[0];
            result.Width = (int)Num(v, "width"); result.Height = (int)Num(v, "height");
            result.Duration = Num(v, "duration");
            if (root.TryGetProperty("format", out var format))
            {
                if (result.Duration <= 0) result.Duration = Num(format, "duration");
                result.StartTime = Num(format, "start_time");
            }
            int bits = (int)Num(v, "bits_per_raw_sample");
            string pix = Str(v, "pix_fmt");
            result.BitDepth = bits > 0 ? bits : pix.Contains("p10") || pix.Contains("10le") ? 10 : pix.Contains("p12") || pix.Contains("12le") ? 12 : pix.Length > 0 ? 8 : 0;
            string transfer = Str(v, "color_transfer");
            result.Transfer = transfer == "smpte2084" ? "PQ" : transfer == "arib-std-b67" ? "HLG" : "SDR";
            result.Primaries = Str(v, "color_primaries") switch
            {
                "bt2020" => "BT.2020",
                "bt709" => "Rec.709",
                "smpte432" => "P3-D65",
                "smpte431" => "DCI-P3",
                _ => ""
            };
            // SDR senza primari dichiarati: Rec.709 (prima il misuratore ripiegava sul BT.2020).
            if (result.Primaries.Length == 0 && result.Transfer == "SDR") result.Primaries = "Rec.709";
            result.ColorSpace = Str(v, "color_space");
            if (v.TryGetProperty("side_data_list", out var side)) ApplySideData(side, result);
            if (root.TryGetProperty("frames", out var frames))
                foreach (var frame in frames.EnumerateArray())
                    if (frame.TryGetProperty("side_data_list", out var frameSide)) ApplySideData(frameSide, result);
            if (result.DolbyVisionProfile != null && result.Transfer == "SDR") result.Transfer = "PQ";
            result.IsHdr = result.Transfer != "SDR" || result.DolbyVisionProfile != null;
            result.BaseLayerNotMeasurable = result.DolbyVisionProfile == 5;
        }
        catch (Exception ex) { result.Error = ex.Message; }
        return result;
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";

    private static double Num(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var x)) return 0;
        if (x.ValueKind == JsonValueKind.Number) return x.GetDouble();
        string s = x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
        var parts = s.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b != 0) return a / b;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
    }

    private static void ApplySideData(JsonElement list, Result result)
    {
        foreach (var sd in list.EnumerateArray())
        {
            string type = Str(sd, "side_data_type");
            if (type.Contains("Content light", StringComparison.OrdinalIgnoreCase))
            {
                if (result.DeclaredMaxCll == null && Num(sd, "max_content") > 0) result.DeclaredMaxCll = (int)Num(sd, "max_content");
                if (result.DeclaredMaxFall == null && Num(sd, "max_average") > 0) result.DeclaredMaxFall = (int)Num(sd, "max_average");
            }
            else if (type.Contains("Mastering display", StringComparison.OrdinalIgnoreCase) && result.MasteringMaxNits == null && Num(sd, "max_luminance") > 0)
            {
                result.MasteringMaxNits = Num(sd, "max_luminance");
                result.MasteringMinNits = Num(sd, "min_luminance");
            }
            else if (type.Contains("DOVI", StringComparison.OrdinalIgnoreCase))
            {
                result.DolbyVisionProfile = Math.Max(1, (int)Num(sd, "dv_profile"));
                result.DolbyVisionCompatibility = (int)Num(sd, "dv_bl_signal_compatibility_id");
            }
            else if (type.Contains("2094-40", StringComparison.OrdinalIgnoreCase) || type.Contains("HDR10+", StringComparison.OrdinalIgnoreCase))
                result.Hdr10Plus = true;
        }
    }

    private static string RunText(string exe, int timeoutMs, params string[] args)
    {
        var (exit, stdout, _) = CinecoreMac.ProcessLauncher.Run(exe, args, false, timeoutMs, CancellationToken.None);
        if (exit == -1 && stdout.Length == 0) throw new TimeoutException("ffprobe");
        return Encoding.UTF8.GetString(stdout);
    }

    // ===== Fotogrammi dal programma ffmpeg =====

    public sealed record Grab(double Time, byte[] Pixels, int Width, int Height, bool Hdr10Plus);

    private static (int W, int H, int Step) MeterSize(int srcW, int srcH)
    {
        int step = Math.Max(1, (int)Math.Round(srcW / 960.0));
        return (Math.Max(16, srcW / step), Math.Max(16, srcH / step), step);
    }

    /// <summary>
    /// Un fotogramma ridotto, in tre piani G, B, R a 16 bit (gbrp16le, gamma piena). Con <paramref name="accurate"/>
    /// falso e' il fotogramma chiave alla posizione o subito prima (veloce, come l'analisi di Windows); vero, quello
    /// esatto (in pausa, per gli strumenti).
    /// </summary>
    private static Grab? GrabFrame(string path, Result format, double seconds, bool accurate, CancellationToken ct, object? gpu = null)
    {
        if (format.Width <= 0 || format.Height <= 0) return null;
        var (w, h, _) = MeterSize(format.Width, format.Height);
#if CINECORE_WINDOWS
        // Windows: il film resta aperto nel decoder della scheda video (GpuFrameMeter), ~40 ms a campione invece di un
        // ffmpeg nuovo ogni volta. Se un campione non riesce, quel campione lo fa ffmpeg.
        if (gpu is CinecoreMac.GpuFrameMeter meter && CinecoreMac.GpuFrameMeter.Usable)
        {
            if (meter.Grab(seconds, accurate, ct) is { } frame) return new Grab(Math.Max(0, frame.Time), frame.Planes, w, h, false);
            ct.ThrowIfCancellationRequested();
        }
#endif
        if (CinecoreMac.MacTools.Ffmpeg is not { } exe) return null;
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "info" };
        // Con il film che scorre il decodificatore hardware resta tutto alla riproduzione (condividerlo la rallentava):
        // decodifica software con pochi thread, in sottofondo. A film fermo, VideoToolbox.
        bool gentle = Gentle();
        if (gentle) args.AddRange(new[] { "-threads", GentleThreads });
#if !CINECORE_WINDOWS
        // Su Windows videotoolbox non esiste: ffmpeg si fermava subito e nessun campione veniva misurato.
        else args.AddRange(new[] { "-hwaccel", "videotoolbox" });
#endif
        // Veloce: il fotogramma chiave dal punto in poi (come skip_frame di Windows). Senza, nei TS il salto cade a meta'
        // di un GOP e il primo fotogramma usciva senza riferimenti (zone grigie o rovinate finivano nelle misure).
        if (!accurate) { args.Add("-noaccurate_seek"); args.AddRange(new[] { "-skip_frame", "nokey" }); }
        args.AddRange(new[] { "-ss", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture), "-copyts", "-i", path,
            "-map", "0:v:0", "-frames:v", "1", "-an", "-sn", "-dn",
            "-vf", $"scale={w}:{h}:flags=neighbor+accurate_rnd+full_chroma_int:out_range=full,format=gbrp16le,showinfo",
            "-f", "rawvideo", "pipe:1" });
        var (_, buffer, log) = CinecoreMac.ProcessLauncher.Run(exe, args, gentle, 30000, ct);
        int expected = w * h * 6;
        if (buffer.Length < expected) return null;
        double time = seconds;
        int at = log.IndexOf("pts_time:", StringComparison.Ordinal);
        if (at >= 0)
        {
            int end = at + 9;
            while (end < log.Length && (char.IsDigit(log[end]) || log[end] == '.' || log[end] == '-')) end++;
            if (double.TryParse(log.AsSpan(at + 9, end - at - 9), NumberStyles.Float, CultureInfo.InvariantCulture, out double pts))
                time = Math.Max(0, pts - format.StartTime);
        }
        bool hdr10Plus = log.Contains("2094-40", StringComparison.Ordinal) || log.Contains("HDR10+", StringComparison.Ordinal);
        return new Grab(time, buffer, w, h, hdr10Plus);
    }

    /// <summary>Vero mentre un film scorre: l'analisi rallenta per non toglierle nulla (la imposta chi apre la scheda).</summary>
    public static Func<bool>? IsPlaybackRunning;
    private static readonly bool Debug = Environment.GetEnvironmentVariable("CINECORE_HDR_DEBUG") == "1";
    private static int GentlePauseMs => int.TryParse(Environment.GetEnvironmentVariable("CINECORE_HDR_PAUSE"), out int ms) ? ms : 0;
    private static int GentleParallel => int.TryParse(Environment.GetEnvironmentVariable("CINECORE_HDR_GPAR"), out int n) ? n : 3;
    private static string GentleThreads => Environment.GetEnvironmentVariable("CINECORE_HDR_THREADS") is { Length: > 0 } th ? th : "2";
    private static bool Gentle() { try { return IsPlaybackRunning?.Invoke() == true; } catch { return false; } }

    /// <summary>Analisi dell'intero film: <paramref name="sampleCount"/> fotogrammi chiave distribuiti sulla durata.</summary>
    /// <param name="hold">Vero mentre l'analisi deve stare ferma (scheda in tempo reale): non chiede nuovi fotogrammi
    /// e riprende da dove era. Le due misure insieme si contendevano il decodificatore e rallentavano entrambe.</param>
    public static Result Analyze(string path, int sampleCount, Action<double, Result>? progress, CancellationToken ct, Func<bool>? hold = null)
    {
        var result = ReadDeclared(path);
        // Anche l'SDR si misura (scala BT.1886 a 100 nit): luminosita', distribuzione e colori del film.
        if (result.Error != null || result.BaseLayerNotMeasurable) return result;
        if (CinecoreMac.MacTools.Ffmpeg == null) { result.Error = "ffmpeg"; return result; }
        double duration = result.Duration;
        if (duration <= 0) { result.Error = "duration"; return result; }

        sampleCount = Math.Clamp(sampleCount, 8, 2000);
        result.RequestedSamples = sampleCount;
        bool measureGamut = result.Primaries == "BT.2020";
        var meter = new FrameMeter(result.Transfer, result.Primaries);
        var totals = new Totals();
        double lastTime = double.NaN;
        long lastProgress = Stopwatch.GetTimestamp();

        // Piu' ffmpeg insieme (ognuno decodifica un fotogramma chiave), misurati in ordine.
        int parallel = int.TryParse(Environment.GetEnvironmentVariable("CINECORE_HDR_PAR"), out int par) ? par : 3;
        int gentleParallel = GentleParallel;
        object?[] decoders = { null };
#if CINECORE_WINDOWS
        // Con la scheda video: due decoder a film fermo, uno mentre il film scorre (la decodifica e' della GPU, la
        // riproduzione non ne risente).
        if (CinecoreMac.GpuFrameMeter.Usable)
        {
            var (mw, mh, _) = MeterSize(result.Width, result.Height);
            int count = Math.Clamp(int.TryParse(Environment.GetEnvironmentVariable("CINECORE_HDR_PAR"), out int gp) ? gp : 2, 1, 4);
            decoders = new object?[count];
            for (int i = 0; i < count; i++) decoders[i] = new CinecoreMac.GpuFrameMeter(path, mw, mh);
            parallel = count; gentleParallel = 1;
        }
#endif
        var pending = new Queue<Task<Grab?>>();
        int next = 0;
        bool Held() { try { return hold?.Invoke() == true; } catch { return false; } }
        void Fill()
        {
            if (Held()) return;
            // Film in riproduzione: un campione alla volta, con una pausa fra l'uno e l'altro.
            bool gentle = Gentle();
            // Al massimo 3 decodifiche VideoToolbox insieme: con 8 (HEVC 4K) il motore video della GPU si saturava e
            // l'interfaccia, che disegna con Metal, restava ferma ad aspettarlo.
            while (pending.Count < (gentle ? gentleParallel : parallel) && next < sampleCount)
            {
                double target = (next + 0.5) * duration / sampleCount;
                object? decoder = decoders[next % decoders.Length];
                // Thread dedicati: ogni campione aspetta il suo ffmpeg bloccando il thread, e nel pool di .NET otto attese
                // insieme facevano lavorare solo 2-3 ffmpeg (il pool aggiunge thread lentamente): ~2,7 campioni al secondo.
                pending.Enqueue(Task.Factory.StartNew(() => { try { return GrabFrame(path, result, target, false, ct, decoder); } catch (OperationCanceledException) { return null; } catch { return null; } },
                    ct, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                next++;
            }
        }
        try
        {
            Fill();
            for (int index = 0; index < sampleCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                // In pausa: finiti i fotogrammi gia' chiesti, si aspetta di poter ripartire.
                while (pending.Count == 0 && Held()) ct.WaitHandle.WaitOne(250);
                if (pending.Count == 0) Fill();
                long waitStart = Stopwatch.GetTimestamp();
                var grab = pending.Dequeue().GetAwaiter().GetResult();
                if (Debug) Console.Error.WriteLine($"[HDR] campione {index} t={grab?.Time:0.00} attesa={Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds:0} ms delicata={Gentle()}");
                if (Gentle() && GentlePauseMs > 0) ct.WaitHandle.WaitOne(GentlePauseMs);
                Fill();
                ct.ThrowIfCancellationRequested();
                // Lo stesso fotogramma chiave del campione prima (o nessun fotogramma) non si misura due volte.
                if (grab != null && grab.Time != lastTime)
                {
                    lastTime = grab.Time;
                    if (grab.Hdr10Plus) result.Hdr10Plus = true;
                    meter.Measure(grab, result.Width, result.Height, result.ColorSpace == "bt709");
                    totals.Add(meter);
                    result.ActiveWidth = meter.ActiveWidth; result.ActiveHeight = meter.ActiveHeight;
                    float peak = meter.PeakNits, average = meter.AverageNits;
                    double time = grab.Time;
                    result.Samples.Add(new Sample { Time = time, PeakNits = peak, AverageNits = average });
                    if (meter.MaxNits > result.MeasuredMaxNits) { result.MeasuredMaxNits = meter.MaxNits; result.MeasuredMaxTime = time; }
                    if (peak > result.MeasuredPeakNits) { result.MeasuredPeakNits = peak; result.MeasuredPeakTime = time; }
                    if (average > result.MeasuredMaxAverageNits) result.MeasuredMaxAverageNits = average;
                }
                // Al massimo 5 aggiornamenti al secondo: con i campioni in raffica (fotogrammi chiave ripetuti) ogni
                // aggiornamento ricalcolava grafici e riepilogo e l'interfaccia restava sommersa (non rispondeva piu').
                if (progress != null && (index == sampleCount - 1 || Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 200))
                {
                    lastProgress = Stopwatch.GetTimestamp();
                    Summarize(result, totals, measureGamut);
                    progress((index + 1) / (double)sampleCount, result.Snapshot());
                }
            }
            // I campioni arrivano in ordine, ma due chiavi vicine possono uscire scambiate.
            result.Samples.Sort((a, b) => a.Time.CompareTo(b.Time));
            Summarize(result, totals, measureGamut);
            if (result.Samples.Count == 0) result.Error = "no-frames";
            else SaveCache(path, result);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { result.Error = ex.GetType().Name; return result; }
        finally
        {
            // I campioni gia' chiesti finiscono prima di chiudere i decoder.
            foreach (var task in pending) { try { task.Wait(5000); } catch { } }
            foreach (var decoder in decoders) (decoder as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Un fotogramma con gli strumenti (tempo reale). Il misuratore resta lo stesso fra una chiamata e l'altra,
    /// cosi' l'area attiva (bande nere) si accumula come su Windows.
    /// </summary>
    public sealed class LiveMeter : IDisposable
    {
        private readonly string _path;
        private readonly Result _format;
        private readonly FrameMeter _meter;
        private readonly object? _gpu;
        public ScopeData Scopes { get; } = new();

        public LiveMeter(string path, Result format)
        {
            _path = path; _format = format;
            _meter = new FrameMeter(format.Transfer, format.Primaries);
#if CINECORE_WINDOWS
            if (CinecoreMac.GpuFrameMeter.Usable)
            {
                var (w, h, _) = MeterSize(format.Width, format.Height);
                _gpu = new CinecoreMac.GpuFrameMeter(path, w, h);
            }
#endif
        }

        public void Dispose() => (_gpu as IDisposable)?.Dispose();

        public FrameStats? Measure(double seconds, bool exact, CancellationToken ct) =>
            Fetch(seconds, exact, ct) is { } grab ? Analyze(grab) : null;

        /// <summary>
        /// Prima meta' della misura: il fotogramma dal decoder. Separata da <see cref="Analyze"/> perche' la scheda
        /// chieda il fotogramma dopo mentre questo si misura (i due passi hanno lucchetti diversi).
        /// </summary>
        public Grab? Fetch(double seconds, bool exact, CancellationToken ct) => GrabFrame(_path, _format, seconds, exact, ct, _gpu);

        public FrameStats Analyze(Grab grab)
        {
            lock (Scopes)
            {
                _meter.Measure(grab, _format.Width, _format.Height, _format.ColorSpace == "bt709", Scopes);
                var m = _meter;
                double count = Math.Max(1, m.Count);
                var stats = new FrameStats
                {
                    Time = grab.Time, PeakNits = m.PeakNits, AverageNits = m.AverageNits, MaxNits = m.MaxNits, LuminanceNits = m.LuminanceNits,
                    BlackNits = m.BlackNits, MedianNits = m.MedianNits, P90Nits = m.P90Nits, P99Nits = m.P99Nits,
                    ShareAbove100 = m.Above100 / count, ShareAbove400 = m.Above400 / count, ShareAbove1000 = m.Above1000 / count,
                    ActiveWidth = m.ActiveWidth, ActiveHeight = m.ActiveHeight,
                    GamutMeasured = _format.Primaries == "BT.2020" && m.GamutPixels > 0,
                };
                for (int i = 0; i < HistogramBins; i++) stats.Histogram[i] = m.Bins[i] / count;
                if (stats.GamutMeasured)
                {
                    stats.GamutRec709 = m.In709 / (double)m.GamutPixels;
                    stats.GamutP3 = m.InP3 / (double)m.GamutPixels;
                    stats.GamutRec2020 = Math.Max(0, 1 - stats.GamutRec709 - stats.GamutP3);
                }
                return stats;
            }
        }
    }

    /// <summary>
    /// Strumenti di un fotogramma: forma d'onda dei tre canali, cromaticita' CIE 1931, vettorscopio,
    /// falsi colori e l'immagine portata su uno schermo SDR.
    /// </summary>
    public sealed class ScopeData
    {
        public const int WaveColumns = 256, WaveLevels = 256, ChromaSize = 160, VectorSize = 160;
        public const double ChromaMaxX = 0.80, ChromaMaxY = 0.90;
        public const float VectorRange = 0.15f;

        public readonly int[] WaveR = new int[WaveColumns * WaveLevels], WaveG = new int[WaveColumns * WaveLevels], WaveB = new int[WaveColumns * WaveLevels];
        public readonly int[] Chroma = new int[ChromaSize * ChromaSize];
        public readonly int[] Vector = new int[VectorSize * VectorSize];
        /// <summary>Immagine ridotta in falsi colori, BGRA.</summary>
        public byte[] FalseColor = Array.Empty<byte>();
        /// <summary>Lo stesso fotogramma come lo si vede (HDR portato su uno schermo SDR), BGRA.</summary>
        public byte[] Picture = Array.Empty<byte>();
        public int PictureWidth, PictureHeight, SamplesPerColumn = 1, ChromaPixels, Pixels;
        public int PictureActiveX, PictureActiveY, PictureActiveWidth, PictureActiveHeight;

        internal void Prepare(int pictureWidth, int pictureHeight)
        {
            Array.Clear(WaveR); Array.Clear(WaveG); Array.Clear(WaveB); Array.Clear(Chroma); Array.Clear(Vector);
            if (PictureWidth != pictureWidth || PictureHeight != pictureHeight || FalseColor.Length != pictureWidth * pictureHeight * 4)
            {
                PictureWidth = pictureWidth; PictureHeight = pictureHeight;
                FalseColor = new byte[pictureWidth * pictureHeight * 4];
                Picture = new byte[pictureWidth * pictureHeight * 4];
            }
        }
    }

    /// <summary>Fasce dei falsi colori: da quanti nit parte ciascuna e il suo colore (ARGB).</summary>
    public static readonly (double FromNits, uint Argb)[] FalseColorBands =
    {
        (0, 0xFF161A26), (0.5, 0xFF2B3A8C), (5, 0xFF1F7FA6), (25, 0xFF2E9E62), (100, 0xFF9DB53A),
        (203, 0xFFE6C63A), (400, 0xFFEC8B2E), (1000, 0xFFE0453A), (2000, 0xFFE44FBF), (4000, 0xFFF4F4F4)
    };

    private static uint[]? _falseColor;
    private static byte[]? _displayGamma;

    private static byte[] DisplayGammaLut()
    {
        if (_displayGamma != null) return _displayGamma;
        var lut = new byte[1024];
        for (int i = 0; i < lut.Length; i++)
        {
            double v = i / 1023.0;
            double s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            lut[i] = (byte)Math.Clamp((int)Math.Round(s * 255), 0, 255);
        }
        return _displayGamma = lut;
    }

    private static uint[] FalseColorLut()
    {
        if (_falseColor != null) return _falseColor;
        var lut = new uint[1024];
        for (int code = 0; code < 1024; code++)
        {
            double nits = PqToNits(code / 1023.0);
            int band = 0;
            while (band + 1 < FalseColorBands.Length && nits >= FalseColorBands[band + 1].FromNits) band++;
            double from = NitsToPq(FalseColorBands[band].FromNits), to = band + 1 < FalseColorBands.Length ? NitsToPq(FalseColorBands[band + 1].FromNits) : 1;
            double shade = 0.62 + 0.38 * Math.Clamp((code / 1023.0 - from) / Math.Max(1e-6, to - from), 0, 1);
            if (band == 0) shade = 0.25 + 0.75 * Math.Clamp((code / 1023.0 - from) / Math.Max(1e-6, to - from), 0, 1);
            uint argb = FalseColorBands[band].Argb;
            uint r = (uint)(((argb >> 16) & 255) * shade), g = (uint)(((argb >> 8) & 255) * shade), b = (uint)((argb & 255) * shade);
            lut[code] = 0xFF000000 | (r << 16) | (g << 8) | b;
        }
        return _falseColor = lut;
    }

    /// <summary>Misura di un fotogramma ridotto (pixel veri, R'G'B' a 16 bit): picco, massimo, media, percentili,
    /// istogramma e gamut sulla sola area attiva; con <see cref="ScopeData"/> anche gli strumenti.</summary>
    private sealed class FrameMeter
    {
        private readonly float[] _nitsLut;
        private readonly byte[] _binOfLut = new byte[LutSize];
        private readonly short[] _codeOfLut = new short[LutSize];
        private readonly bool _measureGamut;
        private readonly float _xr, _xg, _xb, _yr, _yg, _yb, _zr, _zg, _zb;
        private readonly int _barIndex;
        private int _outW, _outH, _srcW, _srcH, _step = 1;
        private float _kr = 0.2627f, _kb = 0.0593f;
        private int _top = int.MaxValue, _bottom = -1, _left = int.MaxValue, _right = -1;
        private int[] _columnOf = Array.Empty<int>();
        private int _columnWidth = -1;
        private ushort[] _planeG = Array.Empty<ushort>(), _planeB = Array.Empty<ushort>(), _planeR = Array.Empty<ushort>();

        public readonly int[] Bins = new int[HistogramBins];
        public readonly int[] Codes = new int[1024];
        public int Count;
        public long Above100, Above400, Above1000, GamutPixels, In709, InP3;
        public float PeakNits, AverageNits, MaxNits, LuminanceNits, BlackNits, MedianNits, P90Nits, P99Nits;
        public int ActiveWidth, ActiveHeight;

        public FrameMeter(string transfer, string primaries)
        {
            _nitsLut = transfer == "HLG" ? HlgNitsLut() : transfer == "SDR" ? SdrNitsLut() : PqNitsLut();
            _measureGamut = primaries == "BT.2020";
            float[] m = primaries switch
            {
                "Rec.709" => new[] { 0.412391f, 0.357584f, 0.180481f, 0.212639f, 0.715169f, 0.072192f, 0.019331f, 0.119195f, 0.950532f },
                "P3-D65" or "DCI-P3" => new[] { 0.486571f, 0.265668f, 0.198217f, 0.228975f, 0.691739f, 0.079287f, 0f, 0.045113f, 1.043944f },
                _ => new[] { 0.636958f, 0.144617f, 0.168881f, 0.262700f, 0.677998f, 0.059302f, 0f, 0.028073f, 1.060985f }
            };
            _xr = m[0]; _xg = m[1]; _xb = m[2]; _yr = m[3]; _yg = m[4]; _yb = m[5]; _zr = m[6]; _zg = m[7]; _zb = m[8];
            _barIndex = LutSize - 1;
            for (int i = 0; i < LutSize; i++)
            {
                _binOfLut[i] = (byte)Math.Min(HistogramBins - 1, (int)(NitsToPq(_nitsLut[i]) * HistogramBins));
                _codeOfLut[i] = (short)Math.Min(1023, (int)(NitsToPq(_nitsLut[i]) * 1023));
                if (_barIndex == LutSize - 1 && _nitsLut[i] > 0.3f) _barIndex = i;
            }
        }

        private bool RowLit(int y, int from, int to)
        {
            int bar = _barIndex, o = y * _outW;
            for (int x = from; x <= to; x++)
                if ((_planeR[o + x] >> 4) >= bar || (_planeG[o + x] >> 4) >= bar || (_planeB[o + x] >> 4) >= bar) return true;
            return false;
        }

        private bool ColumnLit(int x, int from, int to)
        {
            int bar = _barIndex;
            for (int y = from; y <= to; y++)
            {
                int i = y * _outW + x;
                if ((_planeR[i] >> 4) >= bar || (_planeG[i] >> 4) >= bar || (_planeB[i] >> 4) >= bar) return true;
            }
            return false;
        }

        public void Measure(Grab grab, int srcW, int srcH, bool bt709, ScopeData? scopes = null)
        {
            if (_outW != grab.Width || _outH != grab.Height || _srcW != srcW || _srcH != srcH)
            {
                _outW = grab.Width; _outH = grab.Height; _srcW = srcW; _srcH = srcH;
                _step = MeterSize(srcW, srcH).Step;
                _top = int.MaxValue; _bottom = -1; _left = int.MaxValue; _right = -1; _columnWidth = -1;
                _planeG = new ushort[_outW * _outH]; _planeB = new ushort[_outW * _outH]; _planeR = new ushort[_outW * _outH];
            }
            _kr = bt709 ? 0.2126f : 0.2627f; _kb = bt709 ? 0.0722f : 0.0593f;
            int plane = _outW * _outH * 2;
            var bytes = grab.Pixels.AsSpan();
            MemoryMarshal.Cast<byte, ushort>(bytes.Slice(0, plane)).CopyTo(_planeG);
            MemoryMarshal.Cast<byte, ushort>(bytes.Slice(plane, plane)).CopyTo(_planeB);
            MemoryMarshal.Cast<byte, ushort>(bytes.Slice(plane * 2, plane)).CopyTo(_planeR);

            int lit = 0;
            while (lit < _outH && !RowLit(lit, 0, _outW - 1)) lit++;
            if (lit < _outH)
            {
                int last = _outH - 1;
                while (last > lit && !RowLit(last, 0, _outW - 1)) last--;
                int first = 0, end = _outW - 1;
                while (first < end && !ColumnLit(first, lit, last)) first++;
                while (end > first && !ColumnLit(end, lit, last)) end--;
                if (lit < _top) _top = lit;
                if (last > _bottom) _bottom = last;
                if (first < _left) _left = first;
                if (end > _right) _right = end;
            }
            int y0 = 0, y1 = _outH - 1, x0 = 0, x1 = _outW - 1;
            if (_bottom >= _top && _right >= _left) { y0 = _top; y1 = _bottom; x0 = _left; x1 = _right; }
            int width = x1 - x0 + 1, count = width * (y1 - y0 + 1);
            ActiveWidth = Math.Min(_srcW, width * _step);
            ActiveHeight = Math.Min(_srcH, (y1 - y0 + 1) * _step);

            Array.Clear(Codes);
            Array.Clear(Bins);
            Above100 = Above400 = Above1000 = GamutPixels = In709 = InP3 = 0;
            float[] nitsLut = _nitsLut;
            short[] codeOf = _codeOfLut;
            byte[] binOf = _binOfLut;
            int[] codes = Codes, bins = Bins;
            ushort[] pr = _planeR, pg = _planeG, pb = _planeB;
            double frameSum = 0, lumSum = 0;
            int topMax = 0, chromaPixels = 0;
            float yr = _yr, yg = _yg, yb = _yb;

            int[]? waveR = null, waveG = null, waveB = null, chroma = null, vector = null;
            int pictureStep = Math.Max(1, _outW / 640);
            if (scopes != null)
            {
                scopes.Prepare(_outW / pictureStep, _outH / pictureStep);
                scopes.PictureActiveX = Math.Min(scopes.PictureWidth - 1, x0 / pictureStep);
                scopes.PictureActiveY = Math.Min(scopes.PictureHeight - 1, y0 / pictureStep);
                scopes.PictureActiveWidth = Math.Min(scopes.PictureWidth - scopes.PictureActiveX, Math.Max(1, width / pictureStep));
                scopes.PictureActiveHeight = Math.Min(scopes.PictureHeight - scopes.PictureActiveY, Math.Max(1, (y1 - y0 + 1) / pictureStep));
                waveR = scopes.WaveR; waveG = scopes.WaveG; waveB = scopes.WaveB; chroma = scopes.Chroma; vector = scopes.Vector;
                if (_columnWidth != width)
                {
                    _columnWidth = width;
                    _columnOf = new int[width];
                    for (int i = 0; i < width; i++) _columnOf[i] = Math.Min(ScopeData.WaveColumns - 1, i * ScopeData.WaveColumns / width) * ScopeData.WaveLevels;
                }
            }
            int[] columnOf = _columnOf;
            const int ChromaN = ScopeData.ChromaSize, VectorN = ScopeData.VectorSize;
            float chromaX = ChromaN / (float)ScopeData.ChromaMaxX, chromaY = ChromaN / (float)ScopeData.ChromaMaxY;
            float vectorScale = VectorN / (2 * ScopeData.VectorRange);
            float kr = _kr, kb = _kb, kg = 1 - _kr - _kb, cbScale = 1 / (2 * (1 - _kb)), crScale = 1 / (2 * (1 - _kr));

            for (int y = y0; y <= y1; y++)
            {
                int row = y * _outW;
                for (int x = x0; x <= x1; x++)
                {
                    int ri = pr[row + x] >> 4, gi = pg[row + x] >> 4, bi = pb[row + x] >> 4;
                    int top = ri > gi ? (ri > bi ? ri : bi) : (gi > bi ? gi : bi);
                    float nits = nitsLut[top];
                    frameSum += nits;
                    codes[codeOf[top]]++;
                    bins[binOf[top]]++;
                    if (top > topMax) topMax = top;
                    if (nits > 100) { Above100++; if (nits > 400) { Above400++; if (nits > 1000) Above1000++; } }

                    float lr = nitsLut[ri], lg = nitsLut[gi], lb = nitsLut[bi];
                    float luminance = yr * lr + yg * lg + yb * lb;
                    lumSum += luminance;

                    if (_measureGamut && nits > 1f)
                    {
                        float tolerance = -0.02f * nits;
                        GamutPixels++;
                        if (1.6605f * lr - 0.5876f * lg - 0.0728f * lb >= tolerance &&
                            -0.1246f * lr + 1.1329f * lg - 0.0083f * lb >= tolerance &&
                            -0.0182f * lr - 0.1006f * lg + 1.1187f * lb >= tolerance)
                            In709++;
                        else if (1.3436f * lr - 0.2822f * lg - 0.0614f * lb >= tolerance &&
                                 -0.0653f * lr + 1.0758f * lg - 0.0105f * lb >= tolerance &&
                                 0.0028f * lr - 0.0196f * lg + 1.0168f * lb >= tolerance)
                            InP3++;
                    }

                    if (waveR != null)
                    {
                        int column = columnOf[x - x0];
                        waveR[column + (codeOf[ri] >> 2)]++;
                        waveG![column + (codeOf[gi] >> 2)]++;
                        waveB![column + (codeOf[bi] >> 2)]++;
                        if (nits > 2f)
                        {
                            float cieX = _xr * lr + _xg * lg + _xb * lb, sum = cieX + luminance + _zr * lr + _zg * lg + _zb * lb;
                            if (sum > 0)
                            {
                                int cx = (int)(cieX / sum * chromaX), cy = (int)(luminance / sum * chromaY);
                                if ((uint)cx < ChromaN && (uint)cy < ChromaN) { chroma![cy * ChromaN + cx]++; chromaPixels++; }
                            }
                        }
                        float rp = ri * (1f / 4095f), gp = gi * (1f / 4095f), bp = bi * (1f / 4095f);
                        float luma = kr * rp + kg * gp + kb * bp;
                        int vx = (int)(((bp - luma) * cbScale + ScopeData.VectorRange) * vectorScale);
                        int vy = (int)((ScopeData.VectorRange - (rp - luma) * crScale) * vectorScale);
                        if (vx < 0) vx = 0; else if (vx >= VectorN) vx = VectorN - 1;
                        if (vy < 0) vy = 0; else if (vy >= VectorN) vy = VectorN - 1;
                        vector![vy * VectorN + vx]++;
                    }
                }
            }
            Count = count;

            if (scopes != null)
            {
                uint[] colors = FalseColorLut();
                byte[] gammaLut = DisplayGammaLut();
                var picture = MemoryMarshal.Cast<byte, uint>(scopes.FalseColor.AsSpan());
                var view = scopes.Picture;
                int p = 0, v = 0;
                for (int ty = 0; ty < scopes.PictureHeight; ty++)
                {
                    int row = ty * pictureStep * _outW;
                    for (int tx = 0; tx < scopes.PictureWidth; tx++)
                    {
                        int x = row + tx * pictureStep;
                        int ri = pr[x] >> 4, gi = pg[x] >> 4, bi = pb[x] >> 4;
                        int top = ri > gi ? (ri > bi ? ri : bi) : (gi > bi ? gi : bi);
                        picture[p++] = colors[codeOf[top]];
                        // Immagine "normale": BT.2020 -> Rec.709, poi le luci compresse (Reinhard esteso,
                        // bianco a ~1500 nit) sul canale piu' alto, cosi' la tinta non cambia.
                        float lr = nitsLut[ri], lg = nitsLut[gi], lb = nitsLut[bi];
                        float r7 = 1.6605f * lr - 0.5876f * lg - 0.0728f * lb;
                        float g7 = -0.1246f * lr + 1.1329f * lg - 0.0083f * lb;
                        float b7 = -0.0182f * lr - 0.1006f * lg + 1.1187f * lb;
                        if (r7 < 0) r7 = 0; if (g7 < 0) g7 = 0; if (b7 < 0) b7 = 0;
                        float m = r7 > g7 ? (r7 > b7 ? r7 : b7) : (g7 > b7 ? g7 : b7);
                        float k = 0;
                        if (m > 1e-4f)
                        {
                            float t = m * (1f / 120f);
                            k = t * (1 + t * (1f / 156f)) / (1 + t) / m;
                        }
                        int vr = (int)(r7 * k * 1023f), vg = (int)(g7 * k * 1023f), vb = (int)(b7 * k * 1023f);
                        view[v] = gammaLut[vb > 1023 ? 1023 : vb];
                        view[v + 1] = gammaLut[vg > 1023 ? 1023 : vg];
                        view[v + 2] = gammaLut[vr > 1023 ? 1023 : vr];
                        view[v + 3] = 255;
                        v += 4;
                    }
                }
                scopes.SamplesPerColumn = Math.Max(1, count / ScopeData.WaveColumns);
                scopes.ChromaPixels = chromaPixels;
                scopes.Pixels = count;
            }

            int skip = Math.Max(1, count / 10000), seen = 0, code = 1023;
            for (; code > 0; code--) { seen += codes[code]; if (seen >= skip) break; }
            PeakNits = (float)PqToNits(code / 1023.0);
            MaxNits = nitsLut[topMax];
            AverageNits = (float)(frameSum / Math.Max(1, count));
            LuminanceNits = (float)(lumSum / Math.Max(1, count));
            Percentiles(codes, count, out BlackNits, out MedianNits, out P90Nits, out P99Nits);
        }
    }

    private static void Percentiles<T>(T[] codes, double count, out float black, out float median, out float p90, out float p99) where T : INumber<T>
    {
        black = median = p90 = p99 = 0;
        if (count <= 0) return;
        double seen = 0;
        int stage = 0;
        double[] targets = { count * 0.001, count * 0.5, count * 0.9, count * 0.99 };
        for (int code = 0; code < codes.Length && stage < 4; code++)
        {
            seen += double.CreateTruncating(codes[code]);
            while (stage < 4 && seen >= targets[stage])
            {
                float nits = (float)PqToNits(code / 1023.0);
                if (stage == 0) black = nits; else if (stage == 1) median = nits; else if (stage == 2) p90 = nits; else p99 = nits;
                stage++;
            }
        }
    }

    private sealed class Totals
    {
        public readonly double[] Histogram = new double[HistogramBins];
        public readonly long[] Codes = new long[1024];
        public double Pixels, Above100, Above400, Above1000, SumAverage, SumLuminance, GamutPixels, In709, InP3;

        public void Add(FrameMeter meter)
        {
            for (int i = 0; i < HistogramBins; i++) Histogram[i] += meter.Bins[i];
            for (int i = 0; i < Codes.Length; i++) Codes[i] += meter.Codes[i];
            Pixels += meter.Count;
            Above100 += meter.Above100; Above400 += meter.Above400; Above1000 += meter.Above1000;
            GamutPixels += meter.GamutPixels; In709 += meter.In709; InP3 += meter.InP3;
            SumAverage += meter.AverageNits; SumLuminance += meter.LuminanceNits;
        }
    }

    private static void Summarize(Result result, Totals totals, bool measureGamut)
    {
        double pixels = totals.Pixels;
        if (pixels <= 0) return;
        var shares = new double[HistogramBins];
        for (int i = 0; i < HistogramBins; i++) shares[i] = totals.Histogram[i] / pixels;
        result.Histogram = shares;
        result.ShareAbove100 = totals.Above100 / pixels;
        result.ShareAbove400 = totals.Above400 / pixels;
        result.ShareAbove1000 = totals.Above1000 / pixels;
        int frames = Math.Max(1, result.Samples.Count);
        result.OverallAverageNits = (float)(totals.SumAverage / frames);
        result.LuminanceAverageNits = (float)(totals.SumLuminance / frames);
        Percentiles(totals.Codes, pixels, out float black, out float median, out float p90, out float p99);
        result.BlackNits = black; result.MedianNits = median; result.P90Nits = p90; result.P99Nits = p99;
        var peaks = result.Samples.Select(s => s.PeakNits).OrderBy(p => p).ToList();
        result.MedianPeakNits = peaks.Count > 0 ? peaks[peaks.Count / 2] : 0;
        result.GamutMeasured = measureGamut && totals.GamutPixels > 0;
        if (result.GamutMeasured)
        {
            result.GamutRec709 = totals.In709 / totals.GamutPixels;
            result.GamutP3 = totals.InP3 / totals.GamutPixels;
            result.GamutRec2020 = Math.Max(0, 1 - result.GamutRec709 - result.GamutP3);
        }
    }
}
