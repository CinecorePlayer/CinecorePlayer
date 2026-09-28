#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Normalized lyrics are only used for matching. The source text remains on
    /// MusicLyricsService.LyricsLine and is never replaced by recognized text.
    /// </summary>
    public sealed record NormalizedLyricsToken(
        int Index,
        int LineIndex,
        int TokenIndex,
        string Text,
        string? OriginalText = null);

    public sealed record NormalizedLyricsLine(
        int Index,
        string OriginalText,
        IReadOnlyList<NormalizedLyricsToken> Tokens);

    public sealed record NormalizedLyricsDocument(
        IReadOnlyList<NormalizedLyricsLine> Lines,
        IReadOnlyList<NormalizedLyricsToken> Tokens);

    public sealed class LyricsNormalizationOptions
    {
        public bool RemoveSectionLabels { get; init; } = true;
    }

    public static class LyricsTextNormalizer
    {
        private static readonly Regex SectionLabelRegex = new(
            @"\[(?:intro|verse|pre[- ]?chorus|chorus|hook|bridge|outro|refrain|interlude|instrumental|spoken|break|post[- ]?chorus)(?:\s*[^\]]*)?\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TokenRegex = new(
            @"[\p{L}\p{Nd}]+(?:['’][\p{L}\p{Nd}]+)*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static NormalizedLyricsDocument Normalize(
            string? lyrics,
            LyricsNormalizationOptions? options = null)
        {
            options ??= new LyricsNormalizationOptions();
            var lines = new List<NormalizedLyricsLine>();
            var tokens = new List<NormalizedLyricsToken>();
            string source = (lyrics ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

            int globalIndex = 0;
            string[] rawLines = source.Split('\n');
            for (int lineIndex = 0; lineIndex < rawLines.Length; lineIndex++)
            {
                string original = rawLines[lineIndex].Trim();
                string matchingText = options.RemoveSectionLabels
                    ? SectionLabelRegex.Replace(original, " ")
                    : original;

                var lineTokens = new List<NormalizedLyricsToken>();
                int tokenIndex = 0;
                foreach (Match match in TokenRegex.Matches(matchingText))
                {
                    foreach (string tokenText in SplitMatchingUnits(NormalizeToken(match.Value)))
                    {
                        if (tokenText.Length == 0)
                            continue;

                        // CJK and kana lyrics usually have no whitespace. Keep
                        // each character independently alignable so one ASR
                        // word can still anchor a whole sung phrase.
                        string originalText = tokenText.Length == match.Value.Length
                            ? match.Value
                            : tokenText;
                        var token = new NormalizedLyricsToken(globalIndex++, lineIndex, tokenIndex++, tokenText, originalText);
                        lineTokens.Add(token);
                        tokens.Add(token);
                    }
                }

                lines.Add(new NormalizedLyricsLine(lineIndex, original, lineTokens));
            }

            return new NormalizedLyricsDocument(lines, tokens);
        }

        public static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized = value.Normalize(NormalizationForm.FormKC)
                .Replace('\u2018', '\'')
                .Replace('\u2019', '\'')
                .Replace('\u02BC', '\'')
                .ToLowerInvariant();

            var builder = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                if (char.IsLetterOrDigit(c) || c == '\'')
                    builder.Append(c);
                else
                    builder.Append(' ');
            }

            return Regex.Replace(builder.ToString(), @"\s+", " ", RegexOptions.CultureInvariant)
                .Trim('\'', ' ');
        }

        internal static IReadOnlyList<string> SplitMatchingUnits(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<string>();

            var units = new List<string>();
            foreach (string part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!ContainsCjkLike(part))
                {
                    units.Add(part);
                    continue;
                }

                foreach (char character in part)
                {
                    if (!char.IsWhiteSpace(character))
                        units.Add(character.ToString());
                }
            }
            return units;
        }

        private static bool ContainsCjkLike(string value)
        {
            foreach (char character in value)
            {
                if ((character >= '\u3040' && character <= '\u30ff') || // Hiragana/Katakana
                    (character >= '\u3400' && character <= '\u4dbf') || // CJK extension A
                    (character >= '\u4e00' && character <= '\u9fff') || // CJK unified
                    (character >= '\uac00' && character <= '\ud7af'))   // Hangul syllables
                    return true;
            }
            return false;
        }
    }

    public sealed record LyricsRecognizedWord(
        string Text,
        double StartSeconds,
        double EndSeconds,
        double Confidence = 1.0);

    public sealed record LyricsRecognitionResult(
        string Backend,
        IReadOnlyList<LyricsRecognizedWord> Words,
        double? DurationSeconds = null,
        double ProcessingMilliseconds = 0);

    public sealed record LyricsRecognitionRequest(string AudioPath, string? CanonicalLyrics = null, Action<double>? Progress = null);

    public sealed record LyricsSynchronizationRequest(
        string AudioPath,
        string LyricsText,
        double? DurationSeconds = null,
        string? Provider = null,
        Action<double>? Progress = null);

    public enum LyricsSyncLogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    public interface ILyricsSyncLogger
    {
        void Log(
            LyricsSyncLogLevel level,
            string message,
            IReadOnlyDictionary<string, object?> data);
    }

    public sealed class NullLyricsSyncLogger : ILyricsSyncLogger
    {
        public static readonly NullLyricsSyncLogger Instance = new();

        private NullLyricsSyncLogger() { }

        public void Log(
            LyricsSyncLogLevel level,
            string message,
            IReadOnlyDictionary<string, object?> data)
        {
        }
    }

    internal sealed class CinecoreLyricsSyncLogger : ILyricsSyncLogger
    {
        public void Log(
            LyricsSyncLogLevel level,
            string message,
            IReadOnlyDictionary<string, object?> data)
        {
            string fields = data.Count == 0
                ? string.Empty
                : " | " + string.Join(", ", data.Select(x => $"{x.Key}={x.Value}"));
            string formatted = $"[LYRICS-SYNC] {message}{fields}";
            switch (level)
            {
                case LyricsSyncLogLevel.Error:
                    Dbg.Error(formatted);
                    break;
                case LyricsSyncLogLevel.Warning:
                    Dbg.Warn(formatted);
                    break;
                case LyricsSyncLogLevel.Debug:
                    Dbg.Log(formatted, Dbg.LogLevel.Verbose);
                    break;
                default:
                    Dbg.Log(formatted, Dbg.LogLevel.Info);
                    break;
            }
        }
    }

    public sealed class LyricsSynchronizationOptions
    {
        public double MinimumOverallConfidence { get; init; } = 0.65;
        public double MinimumCanonicalCoverage { get; init; } = 0.48;
        public int MinimumMatchedWords { get; init; } = 4;
        public double MinimumTokenSimilarity { get; init; } = 0.42;
        public double SuspiciousGapSeconds { get; init; } = 7.0;
        public long MaximumAlignmentCells { get; init; } = 14_000_000;
        public double DefaultWordDurationSeconds { get; init; } = 0.22;
    }

    public sealed record LyricsSyncDiagnostics
    {
        public string Backend { get; init; } = string.Empty;
        public int CanonicalWordCount { get; init; }
        public int RecognizedWordCount { get; init; }
        public int MatchedWordCount { get; init; }
        public int UnmatchedCanonicalWordCount { get; init; }
        public int ExtraRecognizedWordCount { get; init; }
        public double AlignmentScore { get; init; }
        public double OverallConfidence { get; init; }
        public int SuspiciousGapCount { get; init; }
        public double ProcessingMilliseconds { get; init; }
        public bool CacheHit { get; init; }
        public string? FailureReason { get; init; }
    }

    public sealed record LyricsSynchronizationResult(
        bool IsReliable,
        double Confidence,
        IReadOnlyList<MusicLyricsService.LyricsLine> Lines,
        LyricsSyncDiagnostics Diagnostics,
        string? FailureReason = null);

    public interface ILyricsRecognitionBackend
    {
        string Name { get; }

        bool IsAvailable(string audioPath);

        Task<LyricsRecognitionResult?> RecognizeAsync(
            LyricsRecognitionRequest request,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Development/local backend. A sidecar named &lt;audio&gt;.lyrics-recognition.json
    /// or &lt;audio-without-extension&gt;.lyrics-recognition.json contains timestamped
    /// words. This keeps deterministic alignment tests independent of an ASR model.
    /// </summary>
    public sealed class SidecarLyricsRecognitionBackend : ILyricsRecognitionBackend
    {
        public string Name => "sidecar-json";

        public bool IsAvailable(string audioPath)
            => FindSidecar(audioPath) != null;

        public async Task<LyricsRecognitionResult?> RecognizeAsync(
            LyricsRecognitionRequest request,
            CancellationToken cancellationToken)
        {
            string? sidecar = FindSidecar(request.AudioPath);
            if (sidecar == null)
                return null;

            var stopwatch = Stopwatch.StartNew();
            string json = await File.ReadAllTextAsync(sidecar, cancellationToken).ConfigureAwait(false);
            return LyricsRecognitionJson.Parse(json, Name, stopwatch.Elapsed.TotalMilliseconds);
        }

        private static string? FindSidecar(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
                return null;

            string appended = audioPath + ".lyrics-recognition.json";
            if (File.Exists(appended))
                return appended;

            try
            {
                string withoutExtension = Path.ChangeExtension(audioPath, null) ?? audioPath;
                string adjacent = withoutExtension + ".lyrics-recognition.json";
                return File.Exists(adjacent) ? adjacent : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed record ExternalLyricsRecognitionOptions(
        string ExecutablePath,
        string ArgumentsTemplate = "{audio}",
        int TimeoutSeconds = 180)
    {
        public static ExternalLyricsRecognitionOptions? FromEnvironment()
        {
            string? executable = Environment.GetEnvironmentVariable("CINECORE_LYRICS_RECOGNIZER");
            if (string.IsNullOrWhiteSpace(executable))
                return null;

            string arguments = Environment.GetEnvironmentVariable("CINECORE_LYRICS_RECOGNIZER_ARGS") ?? "{audio}";
            return new ExternalLyricsRecognitionOptions(executable, arguments);
        }
    }

    /// <summary>
    /// Adapter for a local word-timestamp recognizer. The executable receives the
    /// configured argument template, where {audio} is replaced with the quoted
    /// audio path, and writes the same JSON shape as the sidecar backend to stdout.
    /// </summary>
    public sealed class ExternalLyricsRecognitionBackend : ILyricsRecognitionBackend
    {
        private readonly ExternalLyricsRecognitionOptions _options;

        public ExternalLyricsRecognitionBackend(ExternalLyricsRecognitionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public string Name => "external-process";

        public bool IsAvailable(string audioPath)
            => File.Exists(_options.ExecutablePath) && File.Exists(audioPath);

        public async Task<LyricsRecognitionResult?> RecognizeAsync(
            LyricsRecognitionRequest request,
            CancellationToken cancellationToken)
        {
            if (!IsAvailable(request.AudioPath))
                return null;

            var start = Stopwatch.StartNew();
            string arguments = (_options.ArgumentsTemplate ?? "{audio}")
                .Replace("{audio}", QuoteWindowsArgument(request.AudioPath), StringComparison.OrdinalIgnoreCase);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _options.ExecutablePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath) ?? AppContext.BaseDirectory
                }
            };

            if (!process.Start())
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.TimeoutSeconds > 0)
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var killRegistration = cancellationToken.Register(() => TryKill(process));
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                    return null;

                return LyricsRecognitionJson.Parse(stdout, Name, start.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return null;
            }
            finally
            {
                TryKill(process, onlyIfRunning: false);
            }
        }

        private static void TryKill(Process process, bool onlyIfRunning = true)
        {
            try
            {
                if (onlyIfRunning && process.HasExited)
                    return;
                process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        private static string QuoteWindowsArgument(string value)
            => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    public sealed class CompositeLyricsRecognitionBackend : ILyricsRecognitionBackend
    {
        private readonly IReadOnlyList<ILyricsRecognitionBackend> _backends;

        public CompositeLyricsRecognitionBackend(IEnumerable<ILyricsRecognitionBackend> backends)
        {
            _backends = (backends ?? throw new ArgumentNullException(nameof(backends))).ToList();
        }

        public string Name => string.Join(",", _backends.Select(x => x.Name));

        public bool IsAvailable(string audioPath)
            => _backends.Any(x => SafeIsAvailable(x, audioPath));

        public async Task<LyricsRecognitionResult?> RecognizeAsync(
            LyricsRecognitionRequest request,
            CancellationToken cancellationToken)
        {
            foreach (var backend in _backends)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SafeIsAvailable(backend, request.AudioPath))
                    continue;

                try
                {
                    var result = await backend.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
                    if (result?.Words.Count > 0)
                    {
                        Dbg.Log($"[LYRICS-SYNC] recognition backend selected: '{backend.Name}', words={result.Words.Count}, duration={(result.DurationSeconds?.ToString("0.000", CultureInfo.InvariantCulture) ?? "n/a")}s, backendMs={result.ProcessingMilliseconds:0}", Dbg.LogLevel.Info);
                        return result with { Backend = string.IsNullOrWhiteSpace(result.Backend) ? backend.Name : result.Backend };
                    }
                    Dbg.Warn($"[LYRICS-SYNC] recognition backend returned no words: '{backend.Name}'");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Dbg.Warn($"[LYRICS-SYNC] recognition backend failed: '{backend.Name}': {ex.GetType().Name}: {ex.Message}");
                    // Another backend may still be able to service the request.
                }
            }

            return null;
        }

        private static bool SafeIsAvailable(ILyricsRecognitionBackend backend, string audioPath)
        {
            try { return backend.IsAvailable(audioPath); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Uses the Windows SAPI in-process recognizer against a temporary 16 kHz WAV
    /// generated by the bundled FFmpeg. SAPI returns phrase elements with
    /// 100-nanosecond offsets, so this backend supplies real audio-derived word
    /// evidence without requiring a model download or a sidecar file. Singing and
    /// heavily processed vocals can still be rejected by the confidence gate; a
    /// Whisper-compatible executable can be selected through the external backend
    /// when a music-optimized recognizer is installed.
    /// </summary>
    public sealed class SapiLyricsRecognitionBackend : ILyricsRecognitionBackend
    {
        private static readonly Guid RecognitionEventsGuid = new("B6D6F79F-2158-4E50-B5BC-9A9CCD852A09");
        private const int RecognitionEventId = 7;
        private const int EndStreamEventId = 2;

        public string Name => "windows-sapi";

        public bool IsAvailable(string audioPath)
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(audioPath))
                return false;
            if (string.Equals(Environment.GetEnvironmentVariable("CINECORE_DISABLE_SAPI_LYRICS"), "1", StringComparison.Ordinal))
                return false;

            try
            {
                return Type.GetTypeFromProgID("SAPI.SpInprocRecognizer", throwOnError: false) != null
                    && Type.GetTypeFromProgID("SAPI.SpFileStream", throwOnError: false) != null;
            }
            catch
            {
                return false;
            }
        }

        public Task<LyricsRecognitionResult?> RecognizeAsync(
            LyricsRecognitionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => RecognizeCore(request.AudioPath, cancellationToken), cancellationToken);
        }

        private LyricsRecognitionResult? RecognizeCore(string audioPath, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            string? ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
                return null;

            string wavPath = Path.Combine(Path.GetTempPath(), "cinecore-lyrics-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DecodeToSpeechWav(ffmpeg, audioPath, wavPath, cancellationToken))
                    return null;
                return RecognizeWav(wavPath, cancellationToken, stopwatch);
            }
            finally
            {
                try { File.Delete(wavPath); } catch { }
            }
        }

        private static bool DecodeToSpeechWav(
            string ffmpeg,
            string input,
            string output,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };
            foreach (string argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-i", input,
                "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-sample_fmt", "s16", "-f", "wav", output
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    return false;
                while (!process.WaitForExit(250))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                string error = process.StandardError.ReadToEnd();
                return process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 44
                    && string.IsNullOrWhiteSpace(error);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            catch
            {
                TryKill(process);
                return false;
            }
        }

        private static LyricsRecognitionResult? RecognizeWav(
            string wavPath,
            CancellationToken cancellationToken,
            Stopwatch stopwatch)
        {
            object? recognizer = null;
            object? context = null;
            object? grammar = null;
            object? fileStream = null;
            var words = new List<LyricsRecognizedWord>();
            using var ended = new ManualResetEventSlim(false);

            RecognitionEventHandler? recognitionHandler = null;
            EndStreamEventHandler? endHandler = null;
            try
            {
                Type? recognizerType = Type.GetTypeFromProgID("SAPI.SpInprocRecognizer", throwOnError: true);
                Type? streamType = Type.GetTypeFromProgID("SAPI.SpFileStream", throwOnError: true);
                if (recognizerType == null || streamType == null)
                    return null;

                recognizer = Activator.CreateInstance(recognizerType);
                fileStream = Activator.CreateInstance(streamType);
                if (recognizer == null || fileStream == null)
                    return null;

                dynamic dynamicStream = fileStream;
                dynamicStream.Open(wavPath, 1, false); // SSFMOpenForRead
                dynamic dynamicRecognizer = recognizer;
                dynamicRecognizer.AudioInputStream = fileStream;
                context = dynamicRecognizer.CreateRecoContext();
                if (context == null)
                    return null;

                dynamic dynamicContext = context;
                grammar = dynamicContext.CreateGrammar(0);
                if (grammar == null)
                    return null;

                recognitionHandler = (streamNumber, streamPosition, recognitionType, result) =>
                {
                    try { AppendSapiResult(words, result); } catch { }
                };
                endHandler = (streamNumber, streamPosition, streamReleased) => ended.Set();

                ComEventsHelper.Combine(context, RecognitionEventsGuid, RecognitionEventId, recognitionHandler);
                ComEventsHelper.Combine(context, RecognitionEventsGuid, EndStreamEventId, endHandler);

                dynamic dynamicGrammar = grammar;
                dynamicGrammar.DictationLoad("", 0); // SLOStatic
                dynamicGrammar.DictationSetState(1); // SGDSActive

                using var cancellationRegistration = cancellationToken.Register(() =>
                {
                    try { dynamicGrammar.DictationSetState(0); } catch { }
                    ended.Set();
                });

                long bytes = new FileInfo(wavPath).Length;
                double duration = Math.Max(1, (bytes - 44) / 32000.0);
                while (!ended.Wait(250))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stopwatch.Elapsed > TimeSpan.FromSeconds(Math.Clamp(duration + 45, 45, 300)))
                        break;
                }
                cancellationToken.ThrowIfCancellationRequested();
                try { dynamicGrammar.DictationSetState(0); } catch { }

                var usable = words
                    .Where(x => !string.IsNullOrWhiteSpace(x.Text) && double.IsFinite(x.StartSeconds))
                    .OrderBy(x => x.StartSeconds)
                    .ToList();
                return usable.Count == 0
                    ? null
                    : new LyricsRecognitionResult("windows-sapi", usable, duration, stopwatch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                try { if (context != null && recognitionHandler != null) ComEventsHelper.Remove(context, RecognitionEventsGuid, RecognitionEventId, recognitionHandler); } catch { }
                try { if (context != null && endHandler != null) ComEventsHelper.Remove(context, RecognitionEventsGuid, EndStreamEventId, endHandler); } catch { }
                try { if (grammar != null) Marshal.FinalReleaseComObject(grammar); } catch { }
                try { if (context != null) Marshal.FinalReleaseComObject(context); } catch { }
                try { if (fileStream != null) { ((dynamic)fileStream).Close(); Marshal.FinalReleaseComObject(fileStream); } } catch { }
                try { if (recognizer != null) Marshal.FinalReleaseComObject(recognizer); } catch { }
            }
        }

        private static void AppendSapiResult(List<LyricsRecognizedWord> destination, object result)
        {
            dynamic dynamicResult = result;
            double phraseStart = ReadHundredNanoseconds(dynamicResult.Times.OffsetFromStart);
            double phraseLength = ReadHundredNanoseconds(dynamicResult.Times.Length);
            double phraseEnd = phraseStart + Math.Max(0.1, phraseLength);

            dynamic elements = dynamicResult.PhraseInfo.Elements;
            int count = Convert.ToInt32(elements.Count, CultureInfo.InvariantCulture);
            var extracted = new List<LyricsRecognizedWord>();
            for (int i = 0; i < count; i++)
            {
                object? element = TryGetComItem(elements, i);
                if (element == null)
                    continue;
                dynamic dynamicElement = element;
                string text = Convert.ToString(dynamicElement.DisplayText, CultureInfo.InvariantCulture) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                    text = Convert.ToString(dynamicElement.LexicalForm, CultureInfo.InvariantCulture) ?? string.Empty;
                if (text.Length == 0 || !Regex.IsMatch(text, @"[\p{L}\p{Nd}]", RegexOptions.CultureInvariant))
                    continue;

                double start = ReadHundredNanoseconds(dynamicElement.AudioTimeOffset);
                double length = ReadHundredNanoseconds(dynamicElement.AudioSizeTime);
                if (start <= 0 && extracted.Count > 0)
                    start = extracted[^1].EndSeconds;
                if (start <= 0)
                    start = phraseStart;
                double end = length > 0 ? start + length : Math.Min(phraseEnd, start + 0.35);
                double confidence = NormalizeSapiConfidence(ReadDouble(dynamicElement.EngineConfidence, 0.5));
                extracted.Add(new LyricsRecognizedWord(text, start, Math.Max(start, end), confidence));
            }

            if (extracted.Count == 0)
            {
                string phrase = string.Empty;
                try { phrase = Convert.ToString(dynamicResult.PhraseInfo.GetText(), CultureInfo.InvariantCulture) ?? string.Empty; } catch { }
                string[] fallbackWords = Regex.Split(phrase, @"[^\p{L}\p{Nd}']+", RegexOptions.CultureInvariant)
                    .Where(x => x.Length > 0)
                    .ToArray();
                for (int i = 0; i < fallbackWords.Length; i++)
                {
                    double start = phraseStart + ((phraseEnd - phraseStart) * i / Math.Max(1, fallbackWords.Length));
                    double end = phraseStart + ((phraseEnd - phraseStart) * (i + 1) / Math.Max(1, fallbackWords.Length));
                    extracted.Add(new LyricsRecognizedWord(fallbackWords[i], start, Math.Max(start, end), 0.35));
                }
            }

            destination.AddRange(extracted);
        }

        private static object? TryGetComItem(dynamic collection, int index)
        {
            try { return collection.Item(index); }
            catch
            {
                try { return collection.Item(index + 1); }
                catch { return null; }
            }
        }

        private static double ReadHundredNanoseconds(object value)
            => Math.Max(0, ReadDouble(value, 0) / 10_000_000.0);

        private static double ReadDouble(object value, double fallback)
        {
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static double NormalizeSapiConfidence(double value)
        {
            if (!double.IsFinite(value))
                return 0.25;
            if (value < 0)
                return Math.Clamp((value + 1) / 2, 0, 1);
            return Math.Clamp(value, 0, 1);
        }

        private static string? FindFfmpeg()
        {
            foreach (string root in Ancestors(AppContext.BaseDirectory).Concat(Ancestors(Environment.CurrentDirectory)))
            {
                string candidate = Path.Combine(root, "third-parties", "ffmpeg", "win-x64", "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            return "ffmpeg.exe";
        }

        private static IEnumerable<string> Ancestors(string start)
        {
            DirectoryInfo? directory = new(Path.GetFullPath(start));
            for (int i = 0; directory != null && i < 8; i++, directory = directory.Parent)
                yield return directory.FullName;
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }

        private delegate void RecognitionEventHandler(int streamNumber, object streamPosition, int recognitionType, object result);
        private delegate void EndStreamEventHandler(int streamNumber, object streamPosition, bool streamReleased);
    }

    public static class LyricsRecognitionBackendFactory
    {
        public static ILyricsRecognitionBackend CreateDefault()
        {
            // An explicitly configured recognizer takes priority. The bundled
            // faster-whisper worker is the normal music-capable path when Python
            // and its optional model package are installed. SAPI is deliberately
            // opt-in because it is a speech recognizer, not a singing model.
            // The sidecar remains last for deterministic tests and diagnostics.
            var backends = new List<ILyricsRecognitionBackend>();
            var options = ExternalLyricsRecognitionOptions.FromEnvironment();
            if (options != null)
                backends.Add(new ExternalLyricsRecognitionBackend(options));

            backends.Add(new FasterWhisperLyricsRecognitionBackend());

            if (FasterWhisperLyricsRecognitionOptions.ReadBooleanEnvironment("CINECORE_ENABLE_SAPI_LYRICS", false))
                backends.Add(new SapiLyricsRecognitionBackend());

            backends.Add(new SidecarLyricsRecognitionBackend());
            Dbg.Log($"[LYRICS-SYNC] backend strategy created: {string.Join(" -> ", backends.Select(x => x.Name))}", Dbg.LogLevel.Info);
            return new CompositeLyricsRecognitionBackend(backends);
        }
    }

    public sealed class LyricsSynchronizationCache
    {
        private const int SchemaVersion = 1;
        private readonly string? _directory;

        public LyricsSynchronizationCache(string? directory = null, bool enabled = true)
        {
            _directory = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CinecorePlayer2025",
                "LyricsSynchronization");
            Enabled = enabled && !string.IsNullOrWhiteSpace(_directory);
        }

        public bool Enabled { get; }

        public bool TryRead(string key, out LyricsSynchronizationResult result)
        {
            result = null!;
            if (!Enabled || string.IsNullOrWhiteSpace(key))
                return false;

            try
            {
                string path = Path.Combine(_directory!, key + ".json");
                if (!File.Exists(path))
                    return false;

                var document = JsonSerializer.Deserialize<CachedSynchronization>(File.ReadAllText(path));
                if (document == null || document.SchemaVersion != SchemaVersion || document.Lines == null)
                    return false;

                var lines = document.Lines.Select(line => new MusicLyricsService.LyricsLine(
                    line.TimeSeconds,
                    line.Text ?? string.Empty,
                    line.EndTimeSeconds,
                    line.Words?.Select(word => new MusicLyricsService.LyricsWord(
                        word.Text ?? string.Empty,
                        word.StartSeconds,
                        word.EndSeconds,
                        word.Confidence)).ToList(),
                    line.Confidence)).ToList();

                var diagnostics = new LyricsSyncDiagnostics
                {
                    Backend = "disk-cache",
                    OverallConfidence = document.Confidence,
                    CacheHit = true
                };
                result = new LyricsSynchronizationResult(
                    document.IsReliable,
                    document.Confidence,
                    lines,
                    diagnostics);
                Dbg.Log($"[LYRICS-SYNC] disk cache hit: path='{path}', lines={lines.Count}, timedWords={lines.Sum(x => x.Words?.Count ?? 0)}, confidence={document.Confidence:0.000}", Dbg.LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Dbg.Warn($"[LYRICS-SYNC] disk cache read failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public void Write(string key, LyricsSynchronizationResult result)
        {
            if (!Enabled || !result.IsReliable || string.IsNullOrWhiteSpace(key))
                return;

            try
            {
                Directory.CreateDirectory(_directory!);
                var document = new CachedSynchronization
                {
                    SchemaVersion = SchemaVersion,
                    IsReliable = result.IsReliable,
                    Confidence = result.Confidence,
                    Lines = result.Lines.Select(line => new CachedLine
                    {
                        TimeSeconds = line.TimeSeconds,
                        EndTimeSeconds = line.EndTimeSeconds,
                        Text = line.Text,
                        Confidence = line.Confidence,
                        Words = line.Words?.Select(word => new CachedWord
                        {
                            Text = word.Text,
                            StartSeconds = word.StartSeconds,
                            EndSeconds = word.EndSeconds,
                            Confidence = word.Confidence
                        }).ToList()
                    }).ToList()
                };

                string path = Path.Combine(_directory!, key + ".json");
                string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporary, JsonSerializer.Serialize(document));
                File.Move(temporary, path, overwrite: true);
                Dbg.Log($"[LYRICS-SYNC] disk cache written: path='{path}', lines={document.Lines.Count}, timedWords={document.Lines.Sum(x => x.Words?.Count ?? 0)}, confidence={result.Confidence:0.000}", Dbg.LogLevel.Info);
            }
            catch (Exception ex)
            {
                Dbg.Warn($"[LYRICS-SYNC] disk cache write failed: {ex.GetType().Name}: {ex.Message}");
                // Cache failure must never affect playback or lyrics fallback.
            }
        }

        private sealed class CachedSynchronization
        {
            public int SchemaVersion { get; set; }
            public bool IsReliable { get; set; }
            public double Confidence { get; set; }
            public List<CachedLine>? Lines { get; set; }
        }

        private sealed class CachedLine
        {
            public double TimeSeconds { get; set; }
            public double? EndTimeSeconds { get; set; }
            public string? Text { get; set; }
            public double Confidence { get; set; }
            public List<CachedWord>? Words { get; set; }
        }

        private sealed class CachedWord
        {
            public string? Text { get; set; }
            public double StartSeconds { get; set; }
            public double EndSeconds { get; set; }
            public double Confidence { get; set; }
        }
    }

    public sealed class LyricsSynchronizer
    {
        private readonly ILyricsRecognitionBackend _recognitionBackend;
        private readonly LyricsSynchronizationOptions _options;
        private readonly LyricsSynchronizationCache _cache;
        private readonly ILyricsSyncLogger _logger;

        public LyricsSynchronizer(
            ILyricsRecognitionBackend? recognitionBackend = null,
            LyricsSynchronizationOptions? options = null,
            LyricsSynchronizationCache? cache = null,
            ILyricsSyncLogger? logger = null)
        {
            _recognitionBackend = recognitionBackend ?? LyricsRecognitionBackendFactory.CreateDefault();
            _options = options ?? new LyricsSynchronizationOptions();
            _cache = cache ?? new LyricsSynchronizationCache();
            _logger = logger ?? new CinecoreLyricsSyncLogger();
        }

        public bool CanSynchronize(string audioPath)
        {
            if (!File.Exists(audioPath))
                return false;

            try { return _recognitionBackend.IsAvailable(audioPath); }
            catch { return false; }
        }

        public string RecognitionBackendName => _recognitionBackend.Name;

        public async Task<LyricsSynchronizationResult> SynchronizeAsync(
            LyricsSynchronizationRequest request,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var normalized = LyricsTextNormalizer.Normalize(request.LyricsText);
            int canonicalCount = normalized.Tokens.Count;

            _logger.Log(LyricsSyncLogLevel.Info, "Synchronization started.", new Dictionary<string, object?>
            {
                ["audio"] = request.AudioPath,
                ["backendStrategy"] = _recognitionBackend.Name,
                ["canonicalWords"] = canonicalCount,
                ["duration"] = request.DurationSeconds
            });

            if (string.IsNullOrWhiteSpace(request.AudioPath) || !File.Exists(request.AudioPath))
                return Failure("Audio file is unavailable.", canonicalCount, stopwatch.Elapsed.TotalMilliseconds);
            if (canonicalCount == 0)
                return Failure("Lyrics contain no matchable words.", canonicalCount, stopwatch.Elapsed.TotalMilliseconds);

            cancellationToken.ThrowIfCancellationRequested();
            string cacheKey = BuildCacheKey(request);
            if (_cache.TryRead(cacheKey, out var cached))
            {
                _logger.Log(LyricsSyncLogLevel.Info, "Synchronization cache hit.", new Dictionary<string, object?>
                {
                    ["confidence"] = cached.Confidence,
                    ["lines"] = cached.Lines.Count
                });
                return cached;
            }

            LyricsRecognitionResult? recognition;
            try
            {
                recognition = await _recognitionBackend.RecognizeAsync(
                    new LyricsRecognitionRequest(request.AudioPath, request.LyricsText, request.Progress),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Failure("Recognition backend failed: " + ex.GetType().Name, canonicalCount, stopwatch.Elapsed.TotalMilliseconds, _recognitionBackend.Name);
            }

            if (recognition?.Words == null || recognition.Words.Count == 0)
                return Failure("No timestamped recognition output is available.", canonicalCount, stopwatch.Elapsed.TotalMilliseconds, _recognitionBackend.Name);

            _logger.Log(LyricsSyncLogLevel.Info, "Recognition output received.", new Dictionary<string, object?>
            {
                ["backend"] = recognition.Backend,
                ["words"] = recognition.Words.Count,
                ["duration"] = recognition.DurationSeconds,
                ["backendMs"] = recognition.ProcessingMilliseconds,
                ["sample"] = string.Join(" | ", recognition.Words.Take(12).Select(x => $"{x.Text}@{x.StartSeconds:0.00}-{x.EndSeconds:0.00}({x.Confidence:0.00})"))
            });

            cancellationToken.ThrowIfCancellationRequested();
            var recognitionWords = NormalizeRecognitionWords(recognition.Words);
            if (recognitionWords.Count == 0)
                return Failure("Recognition output contained no usable words.", canonicalCount, stopwatch.Elapsed.TotalMilliseconds, recognition.Backend);

            double duration = ResolveDuration(request.DurationSeconds, recognition.DurationSeconds, recognitionWords);
            normalized = RestoreRecognizedOpening(normalized, recognitionWords);
            canonicalCount = normalized.Tokens.Count;
            AlignmentResult alignment;
            try
            {
                alignment = Align(normalized.Tokens, recognitionWords, duration, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Failure("Alignment failed: " + ex.GetType().Name, canonicalCount, stopwatch.Elapsed.TotalMilliseconds, recognition.Backend);
            }

            var lines = ReconstructLines(normalized, recognitionWords, alignment.Matches, duration);
            double coverage = canonicalCount == 0 ? 0 : alignment.Matches.Count / (double)canonicalCount;
            double recognizedCoverage = recognitionWords.Count == 0 ? 0 : alignment.Matches.Count / (double)recognitionWords.Count;
            double averageSimilarity = alignment.Matches.Count == 0
                ? 0
                : alignment.Matches.Average(x => x.Similarity * (0.6 + (0.4 * recognitionWords[x.RecognizedIndex].Confidence)));
            int suspiciousGaps = CountSuspiciousGaps(alignment.Matches, recognitionWords);
            double confidence = Math.Clamp(
                (coverage * 0.50) + (averageSimilarity * 0.30) + (Math.Min(1.0, recognizedCoverage) * 0.20) - Math.Min(0.12, suspiciousGaps * 0.01),
                0,
                1);

            var diagnostics = new LyricsSyncDiagnostics
            {
                Backend = recognition.Backend,
                CanonicalWordCount = canonicalCount,
                RecognizedWordCount = recognitionWords.Count,
                MatchedWordCount = alignment.Matches.Count,
                UnmatchedCanonicalWordCount = Math.Max(0, canonicalCount - alignment.Matches.Count),
                ExtraRecognizedWordCount = Math.Max(0, recognitionWords.Count - alignment.Matches.Count),
                AlignmentScore = alignment.Score,
                OverallConfidence = confidence,
                SuspiciousGapCount = suspiciousGaps,
                ProcessingMilliseconds = stopwatch.Elapsed.TotalMilliseconds
            };

            bool reliable = alignment.Matches.Count >= _options.MinimumMatchedWords
                && coverage >= _options.MinimumCanonicalCoverage
                && confidence >= _options.MinimumOverallConfidence
                && ValidateOutput(lines, duration)
                && HasReliableOpening(normalized, recognitionWords, alignment.Matches);

            var result = new LyricsSynchronizationResult(
                reliable,
                confidence,
                lines,
                diagnostics,
                reliable ? null : "Alignment confidence is below the replacement threshold.");

            _logger.Log(
                reliable ? LyricsSyncLogLevel.Info : LyricsSyncLogLevel.Warning,
                "Synchronization decision.",
                new Dictionary<string, object?>
                {
                    ["accepted"] = reliable,
                    ["backend"] = recognition.Backend,
                    ["matched"] = alignment.Matches.Count,
                    ["canonical"] = canonicalCount,
                    ["recognized"] = recognitionWords.Count,
                    ["confidence"] = confidence,
                    ["suspiciousGaps"] = suspiciousGaps,
                    ["lines"] = lines.Count,
                    ["timedWords"] = lines.Sum(line => line.Words?.Count ?? 0)
                });

            _logger.Log(
                reliable ? LyricsSyncLogLevel.Info : LyricsSyncLogLevel.Warning,
                reliable ? "Lyrics synchronized." : "Lyrics synchronization rejected.",
                new Dictionary<string, object?>
                {
                    ["backend"] = recognition.Backend,
                    ["canonicalWords"] = canonicalCount,
                    ["recognizedWords"] = recognitionWords.Count,
                    ["matchedWords"] = alignment.Matches.Count,
                    ["confidence"] = confidence.ToString("0.000", CultureInfo.InvariantCulture),
                    ["processingMs"] = stopwatch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)
                });

            if (result.IsReliable)
                _cache.Write(cacheKey, result);

            return result;
        }

        public static string BuildCacheKey(LyricsSynchronizationRequest request)
        {
            // v2 intentionally invalidates caches created by the earlier SAPI
            // experiment. Recognition model/configuration is part of the timing
            // evidence, so changing model size, VAD or Demucs must not silently
            // reuse a lower-quality alignment.
            var identity = new StringBuilder("lyrics-sync-v3|");
            try
            {
                string path = Path.GetFullPath(request.AudioPath);
                identity.Append(path.ToUpperInvariant());
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    identity.Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
                }
            }
            catch
            {
                identity.Append(request.AudioPath);
            }

            identity.Append('|').Append(request.DurationSeconds?.ToString("R", CultureInfo.InvariantCulture));
            identity.Append('|').Append(request.Provider);
            identity.Append('|').Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.LyricsText ?? string.Empty))));
            identity.Append('|').Append(Environment.GetEnvironmentVariable("CINECORE_LYRICS_RECOGNIZER"));
            identity.Append('|').Append(Environment.GetEnvironmentVariable("CINECORE_FASTER_WHISPER_MODEL") ?? "auto");
            identity.Append('|').Append(Environment.GetEnvironmentVariable("CINECORE_FASTER_WHISPER_DEVICE") ?? "auto");
            identity.Append('|').Append(Environment.GetEnvironmentVariable("CINECORE_FASTER_WHISPER_COMPUTE_TYPE") ?? "auto");
            identity.Append('|').Append(Environment.GetEnvironmentVariable("CINECORE_FASTER_WHISPER_VAD") ?? "0");
            identity.Append('|').Append(Environment.GetEnvironmentVariable("CINECORE_LYRICS_USE_DEMUCS") ?? "0");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))).ToLowerInvariant();
        }

        private LyricsSynchronizationResult Failure(
            string reason,
            int canonicalWordCount,
            double processingMilliseconds,
            string backend = "")
        {
            var diagnostics = new LyricsSyncDiagnostics
            {
                Backend = backend,
                CanonicalWordCount = canonicalWordCount,
                ProcessingMilliseconds = processingMilliseconds,
                FailureReason = reason
            };
            _logger.Log(LyricsSyncLogLevel.Warning, reason, new Dictionary<string, object?>
            {
                ["backend"] = backend,
                ["canonicalWords"] = canonicalWordCount,
                ["reason"] = reason,
                ["processingMs"] = processingMilliseconds.ToString("0", CultureInfo.InvariantCulture)
            });
            return new LyricsSynchronizationResult(false, 0, Array.Empty<MusicLyricsService.LyricsLine>(), diagnostics, reason);
        }

        private static NormalizedLyricsDocument RestoreRecognizedOpening(NormalizedLyricsDocument document, IReadOnlyList<LyricsRecognizedWord> words)
        {
            var first = document.Lines.FirstOrDefault(line => line.Tokens.Count >= 3);
            if (first == null) return document;
            double Score(NormalizedLyricsLine line, int offset)
            {
                if (offset + line.Tokens.Count > words.Count) return 0;
                double total = 0; int exact = 0;
                for (int i = 0; i < line.Tokens.Count; i++)
                {
                    double similarity = TokenSimilarity(line.Tokens[i].Text, words[offset+i].Text);
                    if (similarity >= .99) exact++;
                    total += similarity;
                }
                return exact >= Math.Ceiling(line.Tokens.Count * .7) ? total / line.Tokens.Count : 0;
            }
            var prefix = new List<string>(); int covered = 0, cursor = 0, skipped = 0;
            while (cursor < words.Count/2)
            {
                NormalizedLyricsLine? match = null; double quality = .89;
                foreach (var line in document.Lines.Where(line => line.Tokens.Count >= 3))
                {
                    double score = Score(line, cursor);
                    if (score > quality + .001) { match = line; quality = score; }
                }
                if (match == null)
                {
                    // A prefix must be a coherent sequence, not scattered matches elsewhere in the audio.
                    if (prefix.Count > 0 || ++skipped > 2) break;
                    cursor++; continue;
                }
                if (match.Index == first.Index) break;
                prefix.Add(match.OriginalText); covered += match.Tokens.Count; cursor += match.Tokens.Count;
            }
            // Only restore a sung opening already present in the provider's lyrics.
            // ASR noise or a different recording must not invent new lyric text.
            if (covered < 6 || covered < cursor * .85) return document;
            return LyricsTextNormalizer.Normalize(string.Join("\n", prefix.Concat(document.Lines.Select(line => line.OriginalText))));
        }

        private static bool HasReliableOpening(NormalizedLyricsDocument document, IReadOnlyList<LyricsRecognizedWord> words, IReadOnlyList<AlignmentMatch> matches)
        {
            var first = document.Lines.FirstOrDefault(line => line.Tokens.Count > 0);
            if (first == null) return false;
            var anchors = matches.Where(match => document.Tokens[match.CanonicalIndex].LineIndex == first.Index).ToList();
            if (anchors.Count < Math.Ceiling(first.Tokens.Count * .5)) return false;
            // Reject a skipped opening phrase even when the rest of the song scores highly.
            if (anchors[0].RecognizedIndex >= 4) return false;
            for (int i=1;i<anchors.Count;i++)
            {
                int missing = anchors[i].CanonicalIndex-anchors[i-1].CanonicalIndex;
                double gap = words[anchors[i].RecognizedIndex].StartSeconds-words[anchors[i-1].RecognizedIndex].EndSeconds;
                if (gap > Math.Max(5, missing*2.5)) return false;
            }
            return true;
        }

        private AlignmentResult Align(
            IReadOnlyList<NormalizedLyricsToken> canonical,
            IReadOnlyList<LyricsRecognizedWord> recognized,
            double duration,
            CancellationToken cancellationToken)
        {
            int n = canonical.Count;
            int m = recognized.Count;
            long cells = (long)(n + 1) * (m + 1);
            if (cells > _options.MaximumAlignmentCells)
                throw new InvalidOperationException("Lyrics alignment input is too large.");

            int width = m + 1;
            int cellCount = checked((int)cells);
            var scores = new float[cellCount];
            var operations = new byte[cellCount];
            Array.Fill(scores, float.NegativeInfinity);
            scores[0] = 0;

            for (int i = 1; i <= n; i++)
            {
                scores[i * width] = scores[(i - 1) * width] - 1.35f;
                operations[i * width] = 1; // canonical word is absent from recognition
            }
            for (int j = 1; j <= m; j++)
            {
                scores[j] = scores[j - 1] - 0.95f;
                operations[j] = 2; // recognition inserted an extra word
            }

            for (int i = 1; i <= n; i++)
            {
                if ((i & 31) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                for (int j = 1; j <= m; j++)
                {
                    int index = (i * width) + j;
                    float best = scores[((i - 1) * width) + j] - 1.35f;
                    byte operation = 1;

                    float insertion = scores[(i * width) + j - 1] - 0.95f;
                    if (insertion > best)
                    {
                        best = insertion;
                        operation = 2;
                    }

                    double similarity = TokenSimilarity(canonical[i - 1].Text, recognized[j - 1].Text);
                    float diagonal = scores[((i - 1) * width) + j - 1] + (float)MatchScore(
                        similarity,
                        recognized[j - 1],
                        i - 1,
                        j - 1,
                        n,
                        m,
                        canonical,
                        recognized,
                        duration);
                    if (diagonal >= best)
                    {
                        best = diagonal;
                        operation = 3;
                    }

                    scores[index] = best;
                    operations[index] = operation;
                }
            }

            var matches = new List<AlignmentMatch>();
            int canonicalIndex = n;
            int recognizedIndex = m;
            while (canonicalIndex > 0 || recognizedIndex > 0)
            {
                byte operation = operations[(canonicalIndex * width) + recognizedIndex];
                if (operation == 3 && canonicalIndex > 0 && recognizedIndex > 0)
                {
                    double similarity = TokenSimilarity(canonical[canonicalIndex - 1].Text, recognized[recognizedIndex - 1].Text);
                    if (similarity >= _options.MinimumTokenSimilarity)
                    {
                        matches.Add(new AlignmentMatch(canonicalIndex - 1, recognizedIndex - 1, similarity));
                    }
                    canonicalIndex--;
                    recognizedIndex--;
                }
                else if (operation == 1 && canonicalIndex > 0)
                {
                    canonicalIndex--;
                }
                else if (recognizedIndex > 0)
                {
                    recognizedIndex--;
                }
                else
                {
                    break;
                }
            }

            matches.Reverse();
            return new AlignmentResult(scores[(n * width) + m], matches);
        }

        private double MatchScore(
            double similarity,
            LyricsRecognizedWord recognized,
            int canonicalIndex,
            int recognizedIndex,
            int canonicalCount,
            int recognizedCount,
            IReadOnlyList<NormalizedLyricsToken> canonical,
            IReadOnlyList<LyricsRecognizedWord> recognizedWords,
            double duration)
        {
            if (similarity < _options.MinimumTokenSimilarity)
                return -2.35;

            double score = (2.35 * similarity) - 0.28;
            score *= 0.6 + (0.4 * recognized.Confidence);

            // A small position prior helps prevent a repeated chorus from jumping
            // to an earlier occurrence, while remaining weak enough for intros and
            // instrumental gaps where recognition contains no words.
            if (canonicalCount > 1 && recognizedCount > 1 && duration > 0)
            {
                double lyricPosition = canonicalIndex / (double)(canonicalCount - 1);
                double audioPosition = Math.Clamp(recognized.StartSeconds / duration, 0, 1);
                score -= Math.Min(0.22, Math.Abs(lyricPosition - audioPosition) * 0.22);
            }

            if (canonicalIndex > 0 && recognizedIndex > 0)
            {
                double previousSimilarity = TokenSimilarity(
                    canonical[canonicalIndex - 1].Text,
                    recognizedWords[recognizedIndex - 1].Text);
                if (previousSimilarity >= 0.72)
                    score += 0.12;
            }
            if (canonicalIndex + 1 < canonicalCount && recognizedIndex + 1 < recognizedCount)
            {
                double nextSimilarity = TokenSimilarity(
                    canonical[canonicalIndex + 1].Text,
                    recognizedWords[recognizedIndex + 1].Text);
                if (nextSimilarity >= 0.72)
                    score += 0.12;
            }

            return score;
        }

        private List<LyricsRecognizedWord> NormalizeRecognitionWords(IReadOnlyList<LyricsRecognizedWord> source)
        {
            var words = new List<LyricsRecognizedWord>(source.Count);
            foreach (var word in source.OrderBy(x => x.StartSeconds))
            {
                string text = LyricsTextNormalizer.NormalizeToken(word.Text);
                if (text.Length == 0 || !double.IsFinite(word.StartSeconds))
                    continue;

                double start = Math.Max(0, word.StartSeconds);
                double end = double.IsFinite(word.EndSeconds) && word.EndSeconds > start
                    ? word.EndSeconds
                    : start + _options.DefaultWordDurationSeconds;
                double confidence = double.IsFinite(word.Confidence) ? Math.Clamp(word.Confidence, 0, 1) : 0;
                IReadOnlyList<string> units = LyricsTextNormalizer.SplitMatchingUnits(text);
                if (units.Count == 0)
                    continue;

                double span = Math.Max(0.001, end - start);
                for (int unitIndex = 0; unitIndex < units.Count; unitIndex++)
                {
                    double unitStart = start + (span * unitIndex / units.Count);
                    double unitEnd = start + (span * (unitIndex + 1) / units.Count);
                    words.Add(new LyricsRecognizedWord(
                        units[unitIndex],
                        unitStart,
                        Math.Max(unitStart, unitEnd),
                        confidence));
                }
            }
            return words;
        }

        private double ResolveDuration(
            double? requestDuration,
            double? recognitionDuration,
            IReadOnlyList<LyricsRecognizedWord> words)
        {
            if (requestDuration is > 0 and double requestValue && double.IsFinite(requestValue))
                return requestValue;
            if (recognitionDuration is > 0 and double recognitionValue && double.IsFinite(recognitionValue))
                return recognitionValue;
            return words.Count == 0 ? 0 : Math.Max(0, words.Max(x => x.EndSeconds) + 0.25);
        }

        private List<MusicLyricsService.LyricsLine> ReconstructLines(
            NormalizedLyricsDocument document,
            IReadOnlyList<LyricsRecognizedWord> recognized,
            IReadOnlyList<AlignmentMatch> matches,
            double duration)
        {
            int tokenCount = document.Tokens.Count;
            var matchedByCanonical = new Dictionary<int, AlignmentMatch>();
            foreach (var match in matches)
                matchedByCanonical[match.CanonicalIndex] = match;

            var timings = new WordTiming[tokenCount];
            int previousMatched = -1;
            var nextMatched = new int[tokenCount];
            int next = -1;
            for (int i = tokenCount - 1; i >= 0; i--)
            {
                if (matchedByCanonical.ContainsKey(i))
                    next = i;
                nextMatched[i] = next;
            }

            // Populate all direct anchors before interpolating unmatched words;
            // an unmatched word near the beginning may refer to an anchor later
            // in the canonical sequence.
            foreach (var match in matches)
            {
                var word = recognized[match.RecognizedIndex];
                timings[match.CanonicalIndex] = new WordTiming(
                    ClampTime(word.StartSeconds, duration),
                    ClampTime(Math.Max(word.StartSeconds, word.EndSeconds), duration),
                    match.Similarity * (0.6 + (0.4 * word.Confidence)));
            }

            for (int i = 0; i < tokenCount; i++)
            {
                if (matchedByCanonical.TryGetValue(i, out var match))
                {
                    previousMatched = i;
                    continue;
                }

                int nextIndex = nextMatched[i];
                if (previousMatched >= 0 && nextIndex >= 0)
                {
                    double fraction = (i - previousMatched) / (double)Math.Max(1, nextIndex - previousMatched);
                    double start = Lerp(timings[previousMatched].Start, timings[nextIndex].Start, fraction);
                    timings[i] = new WordTiming(start, start, 0.25);
                }
                else if (previousMatched >= 0)
                {
                    double start = timings[previousMatched].End + ((i - previousMatched) * _options.DefaultWordDurationSeconds);
                    timings[i] = new WordTiming(start, start, 0.20);
                }
                else if (nextIndex >= 0)
                {
                    double start = timings[nextIndex].Start - ((nextIndex - i) * _options.DefaultWordDurationSeconds);
                    timings[i] = new WordTiming(Math.Max(0, start), Math.Max(0, start), 0.20);
                }
                else
                {
                    double start = duration > 0
                        ? (i / (double)Math.Max(1, tokenCount)) * duration
                        : i * _options.DefaultWordDurationSeconds;
                    timings[i] = new WordTiming(start, start, 0.10);
                }
            }

            for (int i = 0; i < timings.Length; i++)
            {
                double nextStart = i + 1 < timings.Length ? timings[i + 1].Start : double.PositiveInfinity;
                double end = timings[i].End > timings[i].Start
                    ? timings[i].End
                    : Math.Min(nextStart, timings[i].Start + _options.DefaultWordDurationSeconds);
                if (!double.IsFinite(end) || end <= timings[i].Start)
                    end = timings[i].Start + _options.DefaultWordDurationSeconds;
                timings[i] = timings[i] with
                {
                    Start = ClampTime(timings[i].Start, duration),
                    End = ClampTime(Math.Max(timings[i].Start, end), duration)
                };
            }

            var lines = new List<MusicLyricsService.LyricsLine>();
            foreach (var line in document.Lines)
            {
                if (line.OriginalText.Length == 0)
                    continue;

                if (line.Tokens.Count == 0)
                {
                    double start = lines.Count == 0 ? 0 : lines[^1].EndTimeSeconds ?? lines[^1].TimeSeconds;
                    lines.Add(new MusicLyricsService.LyricsLine(start, line.OriginalText, start + 0.6, null, 0.20));
                    continue;
                }

                var wordTimings = new List<MusicLyricsService.LyricsWord>(line.Tokens.Count);
                int matched = 0;
                double similaritySum = 0;
                foreach (var token in line.Tokens)
                {
                    WordTiming timing = timings[token.Index];
                    string originalWord = token.OriginalText ?? token.Text;
                    if (matchedByCanonical.TryGetValue(token.Index, out var match))
                    {
                        matched++;
                        similaritySum += match.Similarity;
                    }
                    wordTimings.Add(new MusicLyricsService.LyricsWord(
                        originalWord,
                        timing.Start,
                        Math.Max(timing.Start, timing.End),
                        timing.Confidence));
                }

                double startTime = wordTimings.Min(x => x.StartSeconds);
                double endTime = wordTimings.Max(x => x.EndSeconds);
                double lineConfidence = matched == 0
                    ? 0.20
                    : (matched / (double)line.Tokens.Count) * (similaritySum / matched);
                lines.Add(new MusicLyricsService.LyricsLine(
                    ClampTime(startTime, duration),
                    line.OriginalText,
                    ClampTime(Math.Max(startTime, endTime), duration),
                    wordTimings,
                    Math.Clamp(lineConfidence, 0, 1)));
            }

            for (int i = 0; i < lines.Count; i++)
            {
                var current = lines[i];
                double start = i == 0 ? current.TimeSeconds : Math.Max(current.TimeSeconds, lines[i - 1].TimeSeconds);
                double end = Math.Max(start, current.EndTimeSeconds ?? start);
                if (i + 1 < lines.Count)
                {
                    double nextStart = Math.Max(start, lines[i + 1].TimeSeconds);
                    end = Math.Min(end, nextStart);
                }
                end = ClampTime(Math.Max(start, end), duration);
                lines[i] = current with { TimeSeconds = ClampTime(start, duration), EndTimeSeconds = end };
            }

            return lines;
        }

        private bool ValidateOutput(IReadOnlyList<MusicLyricsService.LyricsLine> lines, double duration)
        {
            double previous = 0;
            foreach (var line in lines)
            {
                if (!double.IsFinite(line.TimeSeconds) || line.TimeSeconds < 0 ||
                    (duration > 0 && line.TimeSeconds > duration + 0.001))
                    return false;
                double end = line.EndTimeSeconds ?? line.TimeSeconds;
                if (!double.IsFinite(end) || end < line.TimeSeconds ||
                    (duration > 0 && end > duration + 0.001) || line.TimeSeconds + 0.001 < previous)
                    return false;
                previous = line.TimeSeconds;
                if (line.Words != null)
                {
                    double previousWord = line.TimeSeconds;
                    foreach (var word in line.Words)
                    {
                        if (word.StartSeconds + 0.001 < previousWord || word.EndSeconds < word.StartSeconds)
                            return false;
                        previousWord = word.StartSeconds;
                    }
                }
            }
            return lines.Count > 0;
        }

        private int CountSuspiciousGaps(
            IReadOnlyList<AlignmentMatch> matches,
            IReadOnlyList<LyricsRecognizedWord> words)
        {
            int count = 0;
            for (int i = 1; i < matches.Count; i++)
            {
                double gap = words[matches[i].RecognizedIndex].StartSeconds
                    - words[matches[i - 1].RecognizedIndex].EndSeconds;
                if (gap > _options.SuspiciousGapSeconds)
                    count++;
            }
            return count;
        }

        private static double TokenSimilarity(string left, string right)
        {
            if (left == right)
                return 1.0;
            if (left.Length == 0 || right.Length == 0)
                return 0;

            // A one-letter word inside an unrelated word is not phonetic evidence.
            // In particular "I"/"times" and "an"/"can" used to anchor entire verses.
            if (Math.Min(left.Length, right.Length) <= 2) return 0;
            if (Math.Min(left.Length, right.Length) >= 4 &&
                Math.Min(left.Length, right.Length) / (double)Math.Max(left.Length, right.Length) >= .7 &&
                (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)))
                return Math.Min(0.94, 0.76 + (Math.Min(left.Length, right.Length) * 0.02));

            int distance = LevenshteinDistance(left, right);
            double similarity = 1.0 - (distance / (double)Math.Max(left.Length, right.Length));
            if (PhoneticSkeleton(left) == PhoneticSkeleton(right) && left.Length >= 4 && right.Length >= 4)
                similarity = Math.Max(similarity, 0.78);
            return Math.Clamp(similarity, 0, 1);
        }

        private static int LevenshteinDistance(string left, string right)
        {
            if (left.Length == 0) return right.Length;
            if (right.Length == 0) return left.Length;
            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (int j = 0; j <= right.Length; j++) previous[j] = j;
            for (int i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= right.Length; j++)
                {
                    int substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
                }
                (previous, current) = (current, previous);
            }
            return previous[right.Length];
        }

        private static string PhoneticSkeleton(string value)
        {
            var builder = new StringBuilder(value.Length);
            char previous = '\0';
            foreach (char c in value)
            {
                if ("aeiouy".Contains(c))
                    continue;
                if (c != previous)
                    builder.Append(c);
                previous = c;
            }
            return builder.ToString();
        }

        private static double Lerp(double left, double right, double fraction)
            => left + ((right - left) * Math.Clamp(fraction, 0, 1));

        private static double ClampTime(double value, double duration)
        {
            if (!double.IsFinite(value))
                return 0;
            return duration > 0 ? Math.Clamp(value, 0, duration) : Math.Max(0, value);
        }

        private sealed record AlignmentMatch(int CanonicalIndex, int RecognizedIndex, double Similarity);

        private sealed record AlignmentResult(double Score, IReadOnlyList<AlignmentMatch> Matches);

        private readonly record struct WordTiming(double Start, double End, double Confidence);
    }

    internal static class LyricsRecognitionJson
    {
        public static LyricsRecognitionResult? Parse(string json, string backend, double processingMilliseconds)
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            double? duration = null;
            JsonElement wordsElement = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetDouble(root, out double durationValue, "durationSeconds", "duration"))
                    duration = durationValue;
                if (root.TryGetProperty("words", out var words))
                    wordsElement = words;
            }

            if (wordsElement.ValueKind != JsonValueKind.Array)
                return null;

            var wordsList = new List<LyricsRecognizedWord>();
            foreach (var item in wordsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("text", out var textElement))
                    continue;
                string? text = textElement.GetString();
                if (string.IsNullOrWhiteSpace(text) || !TryGetDouble(item, out double start, "startSeconds", "start"))
                    continue;
                double end = TryGetDouble(item, out double endValue, "endSeconds", "end") ? endValue : start + 0.22;
                double confidence = TryGetDouble(item, out double confidenceValue, "confidence", "probability") ? confidenceValue : 1.0;
                if (!double.IsFinite(start) || !double.IsFinite(end))
                    continue;
                wordsList.Add(new LyricsRecognizedWord(text, start, Math.Max(start, end), Math.Clamp(confidence, 0, 1)));
            }

            if (wordsList.Count == 0)
                return null;
            return new LyricsRecognitionResult(backend, wordsList.OrderBy(x => x.StartSeconds).ToList(), duration, processingMilliseconds);
        }

        private static bool TryGetDouble(JsonElement element, out double value, params string[] names)
        {
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
                    return true;
            }
            value = 0;
            return false;
        }
    }
}
