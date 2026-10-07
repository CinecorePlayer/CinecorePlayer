namespace IntroOutroDetector.Audio;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IntroOutroDetector.Models;

/// <summary>
/// Extracts raw PCM audio from video files using FFmpeg.
/// Output: mono low-rate signed 16-bit PCM.
/// </summary>
public class AudioExtractor
{
    public const int SampleRate = 4000;
    private const int ChromaprintSampleRate = 11025;
    private readonly AnalysisOptions _options;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _audioMapCache = new();

    public AudioExtractor(AnalysisOptions options) => _options = options;

    /// <summary>Everything the analysis needs to know about a file, read with a single ffprobe run
    /// (duration, chapters and the audio stream to use). It used to take three runs per episode.</summary>
    public async Task<(double Duration, List<ChapterInfo> Chapters)> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration:stream=index,codec_type,codec_name");
        psi.ArgumentList.Add("-show_chapters");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(filePath);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var errors = process.StandardError.ReadToEndAsync(ct);
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await errors;

        double duration = 0;
        var chapters = new List<ChapterInfo>();
        try
        {
            using var doc = JsonDocument.Parse(output);
            var rootElement = doc.RootElement;
            if (rootElement.TryGetProperty("format", out var format))
                duration = ParseJsonDouble(format, "duration");

            if (rootElement.TryGetProperty("chapters", out var chapterList))
                chapters = chapterList.EnumerateArray()
                    .Select(chapter => new ChapterInfo
                    {
                        StartSeconds = ParseJsonDouble(chapter, "start_time"),
                        EndSeconds = ParseJsonDouble(chapter, "end_time"),
                        Title = chapter.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var title)
                            ? title.GetString() ?? ""
                            : ""
                    })
                    .Where(chapter => chapter.EndSeconds > chapter.StartSeconds)
                    .ToList();

            if (rootElement.TryGetProperty("streams", out var streams))
            {
                var audio = streams.EnumerateArray()
                    .Where(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio")
                    .Select((stream, audioIndex) => (Map: $"0:a:{audioIndex}",
                        Codec: stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : ""))
                    .ToList();
                string map = audio.FirstOrDefault(s => IsFastAudioCodec(s.Codec)).Map ?? audio.FirstOrDefault().Map ?? "0:a:0";
                _audioMapCache[filePath] = new Lazy<Task<string>>(() => Task.FromResult(map));
            }
        }
        catch
        {
            // A file ffprobe cannot describe is reported as unreadable (duration 0) and skipped.
        }
        return (duration, chapters);
    }

    /// <summary>
    /// The fingerprint of a region, from the cache when this exact file and region were already read.
    /// The key includes size and modification time: a replaced file is read again.
    /// </summary>
    public async Task<(uint[] Fingerprint, double SecondsPerFrame)> GetFingerprintAsync(
        string filePath, double startSeconds, double durationSeconds, CancellationToken ct = default)
    {
        string? cacheFile = CachePath(filePath, startSeconds, durationSeconds);
        if (cacheFile != null && File.Exists(cacheFile))
        {
            try
            {
                byte[] stored = await File.ReadAllBytesAsync(cacheFile, ct);
                if (stored.Length > sizeof(double) && (stored.Length - sizeof(double)) % sizeof(uint) == 0)
                {
                    double perFrame = BitConverter.ToDouble(stored, 0);
                    var cached = new uint[(stored.Length - sizeof(double)) / sizeof(uint)];
                    Buffer.BlockCopy(stored, sizeof(double), cached, 0, cached.Length * sizeof(uint));
                    if (perFrame > 0 && cached.Length > 0) return (cached, perFrame);
                }
            }
            catch (IOException) { }
        }

        var fresh = await ExtractChromaprintAsync(filePath, startSeconds, durationSeconds, ct);
        if (cacheFile != null && fresh.Fingerprint.Length > 0)
        {
            try
            {
                var bytes = new byte[sizeof(double) + fresh.Fingerprint.Length * sizeof(uint)];
                BitConverter.TryWriteBytes(bytes.AsSpan(0, sizeof(double)), fresh.SecondsPerFrame);
                Buffer.BlockCopy(fresh.Fingerprint, 0, bytes, sizeof(double), fresh.Fingerprint.Length * sizeof(uint));
                string temp = cacheFile + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, ct);
                File.Move(temp, cacheFile, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return fresh;
    }

    private string? CachePath(string filePath, double startSeconds, double durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(_options.CacheDirectory)) return null;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return null;
            Directory.CreateDirectory(_options.CacheDirectory);
            string identity = string.Join("|", info.FullName.ToUpperInvariant(), info.Length.ToString(CultureInfo.InvariantCulture),
                info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                startSeconds.ToString("F1", CultureInfo.InvariantCulture), durationSeconds.ToString("F1", CultureInfo.InvariantCulture),
                _options.AudioStream, ChromaprintSampleRate.ToString(CultureInfo.InvariantCulture));
            byte[] hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
            return Path.Combine(_options.CacheDirectory, Convert.ToHexString(hash) + ".fp");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract a region of audio from a video file as PCM samples.
    /// </summary>
    public async Task<short[]> ExtractAsync(
        string filePath,
        double startSeconds,
        double durationSeconds,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ExtractTimeoutSeconds));
        var extractCt = timeoutCts.Token;

        string audioMap = string.IsNullOrWhiteSpace(_options.AudioStream)
            ? await GetPreferredAudioMapAsync(filePath, extractCt)
            : _options.AudioStream;

        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Fast seek before input, precise after
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(startSeconds.ToString("F3", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(durationSeconds.ToString("F3", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add(audioMap);
        psi.ArgumentList.Add("-vn");           // no video
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-dn");
        psi.ArgumentList.Add("-threads");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");             // mono
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(SampleRate.ToString());
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");         // raw signed 16-bit little-endian
        psi.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var ms = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(ms, extractCt);

        try
        {
            await Task.WhenAll(copyTask, process.WaitForExitAsync(extractCt));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"ffmpeg extraction exceeded {_options.ExtractTimeoutSeconds}s for {Path.GetFileName(filePath)}");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed with exit code {process.ExitCode}");

        var bytes = ms.ToArray();
        if (bytes.Length < 2) return [];

        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    /// <summary>
    /// Extract a Chromaprint fingerprint for an audio region.
    /// FFmpeg/libchromaprint does the heavy lifting and returns raw 32-bit hashes.
    /// </summary>
    public async Task<(uint[] Fingerprint, double SecondsPerFrame)> ExtractChromaprintAsync(
        string filePath,
        double startSeconds,
        double durationSeconds,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ExtractTimeoutSeconds));
        var extractCt = timeoutCts.Token;

        string audioMap = string.IsNullOrWhiteSpace(_options.AudioStream)
            ? await GetPreferredAudioMapAsync(filePath, extractCt)
            : _options.AudioStream;

        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(startSeconds.ToString("F3", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(durationSeconds.ToString("F3", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add(audioMap);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-dn");
        psi.ArgumentList.Add("-threads");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(ChromaprintSampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("chromaprint");
        psi.ArgumentList.Add("-fp_format");
        psi.ArgumentList.Add("raw");
        psi.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var ms = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(ms, extractCt);

        try
        {
            await Task.WhenAll(copyTask, process.WaitForExitAsync(extractCt));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"chromaprint extraction exceeded {_options.ExtractTimeoutSeconds}s for {Path.GetFileName(filePath)}");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"chromaprint ffmpeg failed with exit code {process.ExitCode}");

        var bytes = ms.ToArray();
        int count = bytes.Length / sizeof(uint);
        if (count == 0) return ([], 0);

        var fingerprint = new uint[count];
        Buffer.BlockCopy(bytes, 0, fingerprint, 0, count * sizeof(uint));

        double secondsPerFrame = durationSeconds / Math.Max(1, count);
        return (fingerprint, secondsPerFrame);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have exited between the timeout and the kill attempt.
        }
    }

    private Task<string> GetPreferredAudioMapAsync(string filePath, CancellationToken ct)
    {
        return _audioMapCache
            .GetOrAdd(filePath, path => new Lazy<Task<string>>(() => ProbePreferredAudioMapAsync(path, ct)))
            .Value;
    }

    private async Task<string> ProbePreferredAudioMapAsync(string filePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("a");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=index,codec_name");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(filePath);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(output);
            var streams = doc.RootElement.GetProperty("streams")
                .EnumerateArray()
                .Select((stream, audioIndex) => new
                {
                    AudioMap = $"0:a:{audioIndex}",
                    Codec = stream.TryGetProperty("codec_name", out var codec)
                        ? codec.GetString() ?? ""
                        : ""
                })
                .ToList();

            var preferred = streams.FirstOrDefault(s => IsFastAudioCodec(s.Codec));
            return preferred?.AudioMap ?? streams.FirstOrDefault()?.AudioMap ?? "0:a:0";
        }
        catch
        {
            return "0:a:0";
        }
    }

    private static bool IsFastAudioCodec(string codec)
    {
        return codec.Equals("aac", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("ac3", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("eac3", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("mp3", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("opus", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("vorbis", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the duration of a media file using ffprobe.
    /// </summary>
    public async Task<double> GetDurationAsync(string filePath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(filePath);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return double.TryParse(
            output.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var d) ? d : 0.0;
    }

    public async Task<List<ChapterInfo>> GetChaptersAsync(string filePath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_chapters");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(filePath);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("chapters", out var chapters))
                return [];

            return chapters.EnumerateArray()
                .Select(chapter => new ChapterInfo
                {
                    StartSeconds = ParseJsonDouble(chapter, "start_time"),
                    EndSeconds = ParseJsonDouble(chapter, "end_time"),
                    Title = chapter.TryGetProperty("tags", out var tags)
                            && tags.TryGetProperty("title", out var title)
                        ? title.GetString() ?? ""
                        : ""
                })
                .Where(chapter => chapter.EndSeconds > chapter.StartSeconds)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static double ParseJsonDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                property.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => 0
        };
    }

    public static bool CheckFfmpeg(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool CheckFfprobe(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
