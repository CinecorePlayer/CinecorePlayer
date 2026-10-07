using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace CinecorePlayer2025.Utilities
{
    internal sealed class IntroOutroSegment
    {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public double LengthSeconds => Math.Max(0, EndSeconds - StartSeconds);
    }

    internal sealed class IntroOutroEpisodeMarkers
    {
        public IntroOutroSegment? Intro { get; set; }
        public IntroOutroSegment? Outro { get; set; }
        public string SourceFileName { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public bool HasIntro => Intro != null && Intro.LengthSeconds > 0.5;
        public bool HasOutro => Outro != null && Outro.LengthSeconds > 0.5;
    }

    internal sealed class IntroOutroScanSnapshot
    {
        public int TotalSeasons { get; set; }
        public int CompletedSeasons { get; set; }
        public int FailedSeasons { get; set; }
        public int RunningSeasons { get; set; }
        public int QueuedSeasons { get; set; }
        public double PercentComplete { get; set; }
        public string CurrentSeason { get; set; } = string.Empty;
        public string LastError { get; set; } = string.Empty;

        public string SummaryText
        {
            get
            {
                if (TotalSeasons <= 0)
                    return global::CinecorePlayer2025.Utilities.AppLanguage.T("0% - nessuna stagione locale rilevata.", "0% - no local seasons found.");

                string suffix = FailedSeasons > 0
                    ? global::CinecorePlayer2025.Utilities.AppLanguage.T($" ({FailedSeasons} con errore)", $" ({FailedSeasons} failed)")
                    : string.Empty;

                if (RunningSeasons > 0 && !string.IsNullOrWhiteSpace(CurrentSeason))
                    return $"{PercentComplete:0}% - scansione in corso: {CurrentSeason}{suffix}";

                if (QueuedSeasons > 0)
                    return $"{PercentComplete:0}% - {CompletedSeasons}/{TotalSeasons} stagioni completate, {QueuedSeasons} in coda{suffix}";

                return $"{PercentComplete:0}% - {CompletedSeasons}/{TotalSeasons} stagioni completate{suffix}";
            }
        }
    }

    internal static class IntroOutroScanService
    {
        private const string StatusQueued = "Queued";
        private const string StatusRunning = "Running";
        private const string StatusCompleted = "Completed";
        private const string StatusFailed = "Failed";
        private const string CurrentLaunchProfile = "manual-compatible-threads1-chaptertail-v6";
        private static readonly TimeSpan FailedRetryDelay = TimeSpan.FromMinutes(30);
        private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts" };

        private static readonly object _sync = new();
        private static readonly Queue<SeasonScanJob> _queue = new();
        private static readonly HashSet<string> _queuedOrRunning = new(StringComparer.OrdinalIgnoreCase);
        private static readonly IntroOutroIndexStore _store = new();
        private static bool _workerActive;
        private static CancellationTokenSource? _workerCts;
        private static Process? _currentProcess;

        public static event Action? StatusChanged;

        public static void QueueSeasonScan(string? seriesTitle, int? seasonNumber, IEnumerable<string>? episodePaths)
        {
            try
            {
                var localPaths = (episodePaths ?? Enumerable.Empty<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (localPaths.Count < 2)
                    return;

                string? seasonPath = TryResolveSingleSeasonDirectory(localPaths);
                if (string.IsNullOrWhiteSpace(seasonPath))
                    return;

                if (!DirectoryVideoSetMatchesGroup(seasonPath, localPaths))
                    return;

                string fingerprint = BuildSeasonFingerprint(localPaths);
                string key = BuildSeasonKey(seasonPath, seriesTitle, seasonNumber);
                string label = BuildSeasonLabel(seriesTitle, seasonNumber, seasonPath);

                lock (_sync)
                {
                    var existing = _store.GetSeason(key);
                    if (existing != null && string.Equals(existing.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(existing.Status, StatusQueued, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(existing.Status, StatusRunning, StringComparison.OrdinalIgnoreCase))
                        {
                            if (_queuedOrRunning.Contains(key))
                                return;
                        }

                        if (string.Equals(existing.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.LaunchProfile, CurrentLaunchProfile, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        if (string.Equals(existing.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.LaunchProfile, CurrentLaunchProfile, StringComparison.OrdinalIgnoreCase) &&
                            existing.CompletedUtc.HasValue &&
                            DateTime.UtcNow - existing.CompletedUtc.Value < FailedRetryDelay)
                        {
                            return;
                        }
                    }

                    if (_queuedOrRunning.Contains(key))
                        return;

                    var job = new SeasonScanJob
                    {
                        Key = key,
                        Label = label,
                        SeasonPath = seasonPath,
                        SeriesTitle = seriesTitle?.Trim() ?? string.Empty,
                        SeasonNumber = seasonNumber,
                        Fingerprint = fingerprint,
                        EpisodePaths = localPaths
                    };

                    _store.MarkQueued(job);
                    _queue.Enqueue(job);
                    _queuedOrRunning.Add(key);
                    StartWorkerNoLock();
                }

                RaiseStatusChanged();
            }
            catch { }
        }

        public static void Shutdown()
        {
            CancellationTokenSource? cts;

            lock (_sync)
            {
                cts = _workerCts;
                _workerCts = null;
                _queue.Clear();
                _queuedOrRunning.Clear();
                _workerActive = false;
            }

            try { cts?.Cancel(); } catch { }
            KillCurrentProcess();
            try { _store.MarkInterruptedScans(); } catch { }
            RaiseStatusChanged();
        }

        public static IntroOutroEpisodeMarkers? TryGetEpisodeMarkers(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try { return _store.TryGetEpisodeMarkers(path.Trim()); }
            catch { return null; }
        }

        public static string? TryGetNextEpisodePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try { return _store.TryGetNextEpisodePath(path.Trim()); }
            catch { return null; }
        }

        public static IntroOutroScanSnapshot GetStatusSnapshot()
        {
            try
            {
                var seasons = _store.GetSeasons();
                int total = seasons.Count;
                int completed = seasons.Count(s => string.Equals(s.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase));
                int failed = seasons.Count(s => string.Equals(s.Status, StatusFailed, StringComparison.OrdinalIgnoreCase));
                int running = seasons.Count(s => string.Equals(s.Status, StatusRunning, StringComparison.OrdinalIgnoreCase));
                int queued = seasons.Count(s => string.Equals(s.Status, StatusQueued, StringComparison.OrdinalIgnoreCase));
                string current = seasons
                    .Where(s => string.Equals(s.Status, StatusRunning, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Label)
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
                string error = seasons
                    .Where(s => string.Equals(s.Status, StatusFailed, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.CompletedUtc ?? DateTime.MinValue)
                    .Select(s => s.Error ?? string.Empty)
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;

                return new IntroOutroScanSnapshot
                {
                    TotalSeasons = total,
                    CompletedSeasons = completed,
                    FailedSeasons = failed,
                    RunningSeasons = running,
                    QueuedSeasons = queued,
                    PercentComplete = total <= 0 ? 0 : (completed * 100.0 / total),
                    CurrentSeason = current,
                    LastError = error
                };
            }
            catch
            {
                return new IntroOutroScanSnapshot();
            }
        }

        private static void StartWorkerNoLock()
        {
            if (_workerActive)
                return;

            _workerCts?.Dispose();
            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;
            _workerActive = true;
            _ = Task.Run(() => WorkerLoopAsync(token));
        }

        private static async Task WorkerLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    SeasonScanJob? job = null;
                    lock (_sync)
                    {
                        if (_queue.Count > 0)
                        {
                            job = _queue.Dequeue();
                        }
                        else
                        {
                            _workerActive = false;
                            return;
                        }
                    }

                    if (job == null)
                        continue;

                    try
                    {
                        _store.MarkRunning(job);
                        RaiseStatusChanged();

                        var markers = await RunDetectorAsync(job, cancellationToken).ConfigureAwait(false);
                        _store.MarkCompleted(job, markers);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        _store.MarkInterrupted(job);
                    }
                    catch (Exception ex)
                    {
                        _store.MarkFailed(job, ex.Message);
                    }
                    finally
                    {
                        lock (_sync)
                            _queuedOrRunning.Remove(job.Key);

                        RaiseStatusChanged();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_sync)
                {
                    if (_queue.Count == 0)
                        _workerActive = false;
                }
            }
        }

        private static async Task<Dictionary<string, IntroOutroEpisodeMarkers>> RunDetectorAsync(SeasonScanJob job, CancellationToken cancellationToken)
        {
            var detector = ResolveDetectorCommand()
                ?? throw new FileNotFoundException("IntroOutroDetector non trovato. Compila il progetto parallelo in Release.");

            string resultsDir = Path.Combine(GetAppDataFolder(), "introOutroResults");
            Directory.CreateDirectory(resultsDir);
            string outputPath = Path.Combine(resultsDir, job.Key + ".json");

            try
            {
                return await RunDetectorOnceAsync(job, detector, outputPath, useBundledTools: false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRetryWithBundledTools(ex))
            {
                return await RunDetectorOnceAsync(job, detector, outputPath, useBundledTools: true, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<Dictionary<string, IntroOutroEpisodeMarkers>> RunDetectorOnceAsync(
            SeasonScanJob job,
            DetectorCommand detector,
            string outputPath,
            bool useBundledTools,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { }
            }

            var psi = new ProcessStartInfo
            {
                FileName = detector.FileName,
                WorkingDirectory = detector.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (detector.UseDotnet)
                psi.ArgumentList.Add(detector.DetectorPath);

            psi.ArgumentList.Add(job.SeasonPath);
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(outputPath);
            // Tre episodi alla volta (era uno) e impronte tenute su disco: quando arriva un episodio nuovo
            // la stagione non viene riletta da capo. Il profilo di lancio non cambia perche' i risultati sono
            // gli stessi: cambiarlo farebbe rianalizzare tutta la libreria.
            psi.ArgumentList.Add("--threads");
            psi.ArgumentList.Add("3");
            try
            {
                string fingerprints = Path.Combine(GetAppDataFolder(), "introOutroFingerprints");
                Directory.CreateDirectory(fingerprints);
                psi.ArgumentList.Add("--cache");
                psi.ArgumentList.Add(fingerprints);
            }
            catch { }

            if (useBundledTools)
            {
                string? ffmpeg = ResolveBundledTool("ffmpeg.exe");
                string? ffprobe = ResolveBundledTool("ffprobe.exe");
                if (!string.IsNullOrWhiteSpace(ffmpeg))
                {
                    psi.ArgumentList.Add("--ffmpeg");
                    psi.ArgumentList.Add(ffmpeg);
                }
                if (!string.IsNullOrWhiteSpace(ffprobe))
                {
                    psi.ArgumentList.Add("--ffprobe");
                    psi.ArgumentList.Add(ffprobe);
                }
            }

            Process? process = null;
            try
            {
                process = Process.Start(psi)
                    ?? throw new InvalidOperationException("Impossibile avviare IntroOutroDetector.");

                lock (_sync)
                    _currentProcess = process;

                // Priorita' ridotta (la ereditano anche gli ffmpeg che lancia): con piu' lavori insieme
                // l'analisi non deve togliere tempo al film in riproduzione.
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                    throw new InvalidOperationException(BuildProcessError(process.ExitCode, stdout, stderr));

                if (!File.Exists(outputPath))
                    throw new FileNotFoundException("IntroOutroDetector non ha prodotto il file JSON dei risultati.");

                return ParseDetectorJson(outputPath, job.EpisodePaths);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                KillProcessTree(process);
                try
                {
                    if (process != null)
                        await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch { }
                throw;
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_currentProcess, process))
                        _currentProcess = null;
                }

                try { process?.Dispose(); } catch { }
            }
        }

        private static void KillCurrentProcess()
        {
            Process? process;
            lock (_sync)
                process = _currentProcess;

            KillProcessTree(process);
        }

        private static void KillProcessTree(Process? process)
        {
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        private static bool ShouldRetryWithBundledTools(Exception ex)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ResolveBundledTool("ffmpeg.exe")) ||
                    string.IsNullOrWhiteSpace(ResolveBundledTool("ffprobe.exe")))
                {
                    return false;
                }

                string message = ex.Message ?? string.Empty;
                return message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("ffprobe", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, IntroOutroEpisodeMarkers> ParseDetectorJson(string outputPath, IReadOnlyList<string> episodePaths)
        {
            var result = new Dictionary<string, IntroOutroEpisodeMarkers>(StringComparer.OrdinalIgnoreCase);
            var byFileName = episodePaths
                .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("episodes", out var episodes) ||
                episodes.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var ep in episodes.EnumerateArray())
            {
                string filename = ReadString(ep, "filename");
                if (string.IsNullOrWhiteSpace(filename))
                    continue;

                if (!byFileName.TryGetValue(filename, out var fullPath))
                    continue;

                double duration = ReadDouble(ep, "duration");
                var markers = new IntroOutroEpisodeMarkers
                {
                    SourceFileName = filename,
                    UpdatedUtc = DateTime.UtcNow,
                    Intro = ReadSegment(ep, "intro", duration),
                    Outro = ReadSegment(ep, "outro", duration)
                };

                if (markers.HasIntro || markers.HasOutro)
                    result[fullPath] = markers;
            }

            return result;
        }

        private static IntroOutroSegment? ReadSegment(JsonElement episode, string property, double episodeDuration)
        {
            if (!episode.TryGetProperty(property, out var segment) ||
                segment.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            double start = ReadDouble(segment, "start");
            double end = ReadDouble(segment, "end");
            if (episodeDuration > 0)
            {
                start = Math.Clamp(start, 0, episodeDuration);
                end = Math.Clamp(end, 0, episodeDuration);
            }

            if (end <= start + 0.5)
                return null;

            return new IntroOutroSegment
            {
                StartSeconds = Math.Max(0, start),
                EndSeconds = Math.Max(0, end)
            };
        }

        private static string ReadString(JsonElement element, string property)
        {
            try
            {
                return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static double ReadDouble(JsonElement element, string property)
        {
            try
            {
                if (!element.TryGetProperty(property, out var value))
                    return 0;

                return value.ValueKind switch
                {
                    JsonValueKind.Number => value.GetDouble(),
                    JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => 0
                };
            }
            catch { return 0; }
        }

        private static string? TryResolveSingleSeasonDirectory(IReadOnlyList<string> episodePaths)
        {
            var dirs = episodePaths
                .Select(p =>
                {
                    try { return Path.GetDirectoryName(Path.GetFullPath(p)) ?? string.Empty; }
                    catch { return string.Empty; }
                })
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return dirs.Count == 1 && Directory.Exists(dirs[0]) ? dirs[0] : null;
        }

        private static bool DirectoryVideoSetMatchesGroup(string seasonPath, IReadOnlyList<string> episodePaths)
        {
            try
            {
                var expected = new HashSet<string>(
                    episodePaths.Select(p => Path.GetFullPath(p)),
                    StringComparer.OrdinalIgnoreCase);

                var actual = Directory.EnumerateFiles(seasonPath)
                    .Where(IsSupportedVideoFile)
                    .Where(p => !ShouldIgnoreVideoCandidate(p))
                    .Select(p => Path.GetFullPath(p))
                    .ToList();

                if (actual.Count == 0)
                    return false;

                foreach (var file in actual)
                {
                    if (!expected.Contains(file))
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSupportedVideoFile(string path)
        {
            string ext = Path.GetExtension(path) ?? string.Empty;
            return VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        private static bool ShouldIgnoreVideoCandidate(string path)
        {
            try
            {
                string normalized = path.Replace('/', '\\');
                if (Regex.IsMatch(normalized, @"(?i)\\(?:sample(?:,screens)?|samples|screens?|screen ?caps?|trailers?|teasers?|extras?|featurettes?|proofs?)\\"))
                    return true;

                string name = Path.GetFileNameWithoutExtension(normalized) ?? string.Empty;
                return Regex.IsMatch(name, @"(?ix)(?:^|[\s._\-\(\[])(sample|trailer|teaser|promo|clip)(?:$|[\s._\-\)\]])");
            }
            catch
            {
                return false;
            }
        }

        private static string BuildSeasonFingerprint(IEnumerable<string> paths)
        {
            var sb = new StringBuilder();
            foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var fi = new FileInfo(path);
                    sb.Append(Path.GetFullPath(path).ToLowerInvariant());
                    sb.Append('|').Append(fi.Length.ToString(CultureInfo.InvariantCulture));
                    sb.Append('|').Append(fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine();
                }
                catch { }
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        private static string BuildSeasonKey(string seasonPath, string? seriesTitle, int? seasonNumber)
        {
            string raw = string.Join("|",
                Path.GetFullPath(seasonPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant(),
                seriesTitle?.Trim().ToLowerInvariant() ?? string.Empty,
                seasonNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).Substring(0, 24).ToLowerInvariant();
        }

        private static string BuildSeasonLabel(string? seriesTitle, int? seasonNumber, string seasonPath)
        {
            string title = !string.IsNullOrWhiteSpace(seriesTitle)
                ? seriesTitle!.Trim()
                : (Path.GetFileName(seasonPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "Serie TV");

            string season = seasonNumber.HasValue ? $" S{seasonNumber.Value:00}" : string.Empty;
            return title + season;
        }

        private static string BuildProcessError(int exitCode, string stdout, string stderr)
        {
            string detail = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
            detail = detail.Trim();
            if (detail.Length > 600)
                detail = detail[^600..];

            return string.IsNullOrWhiteSpace(detail)
                ? $"IntroOutroDetector terminato con codice {exitCode}."
                : $"IntroOutroDetector terminato con codice {exitCode}: {detail}";
        }

        private static DetectorCommand? ResolveDetectorCommand()
        {
            foreach (var root in CandidateRoots())
            {
                string[] exeCandidates =
                {
                    Path.Combine(root, "IntroOutroDetector", "IntroOutroDetector.exe"),
                    Path.Combine(root, "IntroOutroDetector", "bin", "Release", "net8.0", "IntroOutroDetector.exe"),
                    Path.Combine(root, "IntroOutroDetector", "bin", "Debug", "net8.0", "IntroOutroDetector.exe")
                };

                foreach (var exe in exeCandidates)
                {
                    if (File.Exists(exe))
                    {
                        return new DetectorCommand
                        {
                            FileName = exe,
                            DetectorPath = exe,
                            WorkingDirectory = Path.GetDirectoryName(exe) ?? root
                        };
                    }
                }

                string[] dllCandidates =
                {
                    Path.Combine(root, "IntroOutroDetector", "IntroOutroDetector.dll"),
                    Path.Combine(root, "IntroOutroDetector", "bin", "Release", "net8.0", "IntroOutroDetector.dll"),
                    Path.Combine(root, "IntroOutroDetector", "bin", "Debug", "net8.0", "IntroOutroDetector.dll")
                };

                foreach (var dll in dllCandidates)
                {
                    if (File.Exists(dll))
                    {
                        return new DetectorCommand
                        {
                            FileName = "dotnet",
                            DetectorPath = dll,
                            WorkingDirectory = Path.GetDirectoryName(dll) ?? root,
                            UseDotnet = true
                        };
                    }
                }
            }

            return null;
        }

        private static string? ResolveBundledTool(string fileName)
        {
            foreach (var root in CandidateRoots())
            {
                string[] candidates =
                {
                    Path.Combine(root, "third-parties", "ffmpeg", "win-x64", fileName),
                    Path.Combine(root, "ffmpeg", "win-x64", fileName),
                    Path.Combine(root, "bin", "Debug", "net9.0-windows", "ffmpeg", "win-x64", fileName),
                    Path.Combine(root, "bin", "Release", "net9.0-windows", "ffmpeg", "win-x64", fileName)
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> CandidateRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var seed in new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                string? current = seed;
                for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
                {
                    string full;
                    try { full = Path.GetFullPath(current); }
                    catch { break; }

                    if (seen.Add(full))
                        yield return full;

                    current = Directory.GetParent(full)?.FullName;
                }
            }
        }

        private static string GetAppDataFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void RaiseStatusChanged()
        {
            try { StatusChanged?.Invoke(); } catch { }
        }

        private sealed class SeasonScanJob
        {
            public string Key { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;
            public string SeasonPath { get; set; } = string.Empty;
            public string SeriesTitle { get; set; } = string.Empty;
            public int? SeasonNumber { get; set; }
            public string Fingerprint { get; set; } = string.Empty;
            public List<string> EpisodePaths { get; set; } = new();
        }

        private sealed class DetectorCommand
        {
            public string FileName { get; set; } = string.Empty;
            public string DetectorPath { get; set; } = string.Empty;
            public string WorkingDirectory { get; set; } = string.Empty;
            public bool UseDotnet { get; set; }
        }

        private sealed class IntroOutroIndexStore
        {
            private readonly object _lock = new();
            private readonly string _file = Path.Combine(GetAppDataFolder(), "introOutroIndex.json");
            private IndexModel _data;

            public IntroOutroIndexStore()
            {
                _data = Load();
            }

            public SeasonEntry? GetSeason(string key)
            {
                lock (_lock)
                {
                    return _data.Seasons.TryGetValue(key, out var entry) ? Clone(entry) : null;
                }
            }

            public List<SeasonEntry> GetSeasons()
            {
                lock (_lock)
                {
                    return _data.Seasons.Values.Select(Clone).ToList();
                }
            }

            public IntroOutroEpisodeMarkers? TryGetEpisodeMarkers(string path)
            {
                lock (_lock)
                {
                    string normalizedPath = NormalizePathForLookup(path);
                    foreach (var season in _data.Seasons.Values)
                    {
                        if (season.Episodes.TryGetValue(path, out var markers))
                            return Clone(markers);

                        if (!string.IsNullOrWhiteSpace(normalizedPath))
                        {
                            foreach (var pair in season.Episodes)
                            {
                                if (string.Equals(NormalizePathForLookup(pair.Key), normalizedPath, StringComparison.OrdinalIgnoreCase))
                                    return Clone(pair.Value);
                            }
                        }
                    }
                }

                return null;
            }

            public string? TryGetNextEpisodePath(string path)
            {
                lock (_lock)
                {
                    string normalizedPath = NormalizePathForLookup(path);
                    foreach (var season in _data.Seasons.Values)
                    {
                        var ordered = GetOrderedEpisodePathsNoLock(season);
                        int currentIndex = ordered.FindIndex(p =>
                            string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(NormalizePathForLookup(p), normalizedPath, StringComparison.OrdinalIgnoreCase));
                        if (currentIndex >= 0 && currentIndex + 1 < ordered.Count)
                            return ordered[currentIndex + 1];
                    }
                }

                return null;
            }

            public void MarkQueued(SeasonScanJob job)
            {
                lock (_lock)
                {
                    var entry = GetOrCreateNoLock(job);
                    entry.Status = StatusQueued;
                    entry.Error = null;
                    entry.Fingerprint = job.Fingerprint;
                    entry.QueuedUtc = DateTime.UtcNow;
                    entry.StartedUtc = null;
                    entry.CompletedUtc = null;
                    SaveNoLock();
                }
            }

            public void MarkRunning(SeasonScanJob job)
            {
                lock (_lock)
                {
                    var entry = GetOrCreateNoLock(job);
                    entry.Status = StatusRunning;
                    entry.StartedUtc = DateTime.UtcNow;
                    entry.CompletedUtc = null;
                    SaveNoLock();
                }
            }

            public void MarkCompleted(SeasonScanJob job, Dictionary<string, IntroOutroEpisodeMarkers> markers)
            {
                lock (_lock)
                {
                    var entry = GetOrCreateNoLock(job);
                    entry.Status = StatusCompleted;
                    entry.Error = null;
                    entry.CompletedUtc = DateTime.UtcNow;
                    entry.Episodes = new Dictionary<string, IntroOutroEpisodeMarkers>(markers, StringComparer.OrdinalIgnoreCase);
                    SaveNoLock();
                }
            }

            public void MarkFailed(SeasonScanJob job, string error)
            {
                lock (_lock)
                {
                    var entry = GetOrCreateNoLock(job);
                    entry.Status = StatusFailed;
                    entry.Error = string.IsNullOrWhiteSpace(error) ? global::CinecorePlayer2025.Utilities.AppLanguage.T("Errore sconosciuto.", "Unknown error.") : error.Trim();
                    entry.CompletedUtc = DateTime.UtcNow;
                    SaveNoLock();
                }
            }

            public void MarkInterrupted(SeasonScanJob job)
            {
                lock (_lock)
                {
                    var entry = GetOrCreateNoLock(job);
                    MarkInterruptedNoLock(entry);
                    SaveNoLock();
                }
            }

            public void MarkInterruptedScans()
            {
                lock (_lock)
                {
                    bool changed = false;
                    foreach (var entry in _data.Seasons.Values)
                    {
                        if (entry == null)
                            continue;

                        if (string.Equals(entry.Status, StatusQueued, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry.Status, StatusRunning, StringComparison.OrdinalIgnoreCase))
                        {
                            MarkInterruptedNoLock(entry);
                            changed = true;
                        }
                    }

                    if (changed)
                        SaveNoLock();
                }
            }

            private static void MarkInterruptedNoLock(SeasonEntry entry)
            {
                entry.Status = StatusQueued;
                entry.Error = null;
                entry.QueuedUtc = DateTime.UtcNow;
                entry.StartedUtc = null;
                entry.CompletedUtc = null;
            }

            private SeasonEntry GetOrCreateNoLock(SeasonScanJob job)
            {
                if (!_data.Seasons.TryGetValue(job.Key, out var entry) || entry == null)
                {
                    entry = new SeasonEntry();
                    _data.Seasons[job.Key] = entry;
                }

                entry.Key = job.Key;
                entry.Label = job.Label;
                entry.SeriesTitle = job.SeriesTitle;
                entry.SeasonNumber = job.SeasonNumber;
                entry.SeasonPath = job.SeasonPath;
                entry.Fingerprint = job.Fingerprint;
                entry.LaunchProfile = CurrentLaunchProfile;
                entry.EpisodeCount = job.EpisodePaths.Count;
                entry.EpisodePaths = job.EpisodePaths
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return entry;
            }

            private IndexModel Load()
            {
                try
                {
                    if (!File.Exists(_file))
                        return new IndexModel();

                    var json = File.ReadAllText(_file, Encoding.UTF8);
                    var model = JsonSerializer.Deserialize<IndexModel>(json);
                    return Normalize(model);
                }
                catch
                {
                    return new IndexModel();
                }
            }

            private static IndexModel Normalize(IndexModel? model)
            {
                var normalized = new IndexModel();
                if (model?.Seasons == null)
                    return normalized;

                foreach (var kvp in model.Seasons)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                        continue;

                    var entry = kvp.Value;
                    entry.Key = string.IsNullOrWhiteSpace(entry.Key) ? kvp.Key : entry.Key;
                    entry.Episodes = new Dictionary<string, IntroOutroEpisodeMarkers>(
                        entry.Episodes ?? new Dictionary<string, IntroOutroEpisodeMarkers>(),
                        StringComparer.OrdinalIgnoreCase);
                    entry.EpisodePaths = (entry.EpisodePaths ?? new List<string>())
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => p.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (string.Equals(entry.Status, StatusQueued, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(entry.Status, StatusRunning, StringComparison.OrdinalIgnoreCase))
                    {
                        MarkInterruptedNoLock(entry);
                    }

                    normalized.Seasons[kvp.Key] = entry;
                }

                return normalized;
            }

            private void SaveNoLock()
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file) ?? GetAppDataFolder());
                    var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, new UTF8Encoding(false));
                }
                catch { }
            }

            private static SeasonEntry Clone(SeasonEntry entry)
            {
                return new SeasonEntry
                {
                    Key = entry.Key,
                    Label = entry.Label,
                    SeriesTitle = entry.SeriesTitle,
                    SeasonNumber = entry.SeasonNumber,
                    SeasonPath = entry.SeasonPath,
                    Fingerprint = entry.Fingerprint,
                    LaunchProfile = entry.LaunchProfile,
                    Status = entry.Status,
                    Error = entry.Error,
                    QueuedUtc = entry.QueuedUtc,
                    StartedUtc = entry.StartedUtc,
                    CompletedUtc = entry.CompletedUtc,
                    EpisodeCount = entry.EpisodeCount,
                    EpisodePaths = entry.EpisodePaths?.ToList() ?? new List<string>(),
                    Episodes = entry.Episodes.ToDictionary(k => k.Key, v => Clone(v.Value), StringComparer.OrdinalIgnoreCase)
                };
            }

            private static IntroOutroEpisodeMarkers Clone(IntroOutroEpisodeMarkers markers)
            {
                return new IntroOutroEpisodeMarkers
                {
                    SourceFileName = markers.SourceFileName,
                    UpdatedUtc = markers.UpdatedUtc,
                    Intro = markers.Intro == null ? null : new IntroOutroSegment
                    {
                        StartSeconds = markers.Intro.StartSeconds,
                        EndSeconds = markers.Intro.EndSeconds
                    },
                    Outro = markers.Outro == null ? null : new IntroOutroSegment
                    {
                        StartSeconds = markers.Outro.StartSeconds,
                        EndSeconds = markers.Outro.EndSeconds
                    }
                };
            }

            private static List<string> GetOrderedEpisodePathsNoLock(SeasonEntry season)
            {
                return (season.EpisodePaths ?? new List<string>())
                    .Concat(season.Episodes?.Keys ?? Enumerable.Empty<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => TryReadEpisodeNumber(p) ?? int.MaxValue)
                    .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            private static int? TryReadEpisodeNumber(string path)
            {
                try
                {
                    string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                    var match = Regex.Match(name, @"(?i)\bS\d{1,2}[\s._-]*E(\d{1,3})\b");
                    if (!match.Success)
                        match = Regex.Match(name, @"(?i)\b\d{1,2}x(\d{1,3})\b");
                    if (!match.Success)
                        match = Regex.Match(name, @"(?i)(?:^|[\s._-])E(\d{1,3})(?:$|[\s._-])");

                    return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode)
                        ? episode
                        : null;
                }
                catch
                {
                    return null;
                }
            }

            private static string NormalizePathForLookup(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return string.Empty;

                try { return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
                catch { return path.Trim().Replace('/', '\\'); }
            }
        }

        private sealed class IndexModel
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, SeasonEntry> Seasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class SeasonEntry
        {
            public string Key { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;
            public string SeriesTitle { get; set; } = string.Empty;
            public int? SeasonNumber { get; set; }
            public string SeasonPath { get; set; } = string.Empty;
            public string Fingerprint { get; set; } = string.Empty;
            public string LaunchProfile { get; set; } = string.Empty;
            public string Status { get; set; } = StatusQueued;
            public string? Error { get; set; }
            public DateTime? QueuedUtc { get; set; }
            public DateTime? StartedUtc { get; set; }
            public DateTime? CompletedUtc { get; set; }
            public int EpisodeCount { get; set; }
            public List<string> EpisodePaths { get; set; } = new();
            public Dictionary<string, IntroOutroEpisodeMarkers> Episodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
