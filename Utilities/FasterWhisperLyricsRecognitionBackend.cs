#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Configuration for the optional Python music recognizer. The .NET player
    /// does not ship model weights or a Python runtime; when this backend is
    /// installed it is discovered automatically and otherwise the synchronizer
    /// safely leaves the provider's plain lyrics untouched.
    /// </summary>
    public sealed class FasterWhisperLyricsRecognitionOptions
    {
        public string Model { get; init; } = Environment.GetEnvironmentVariable("CINECORE_FASTER_WHISPER_MODEL") ?? "auto";
        public string Device { get; init; } = Environment.GetEnvironmentVariable("CINECORE_FASTER_WHISPER_DEVICE") ?? "cpu";
        public string ComputeType { get; init; } = Environment.GetEnvironmentVariable("CINECORE_FASTER_WHISPER_COMPUTE_TYPE") ?? "auto";
        public bool UseVoiceActivityDetection { get; init; } = ReadBooleanEnvironment("CINECORE_FASTER_WHISPER_VAD", defaultValue: false);
        public bool UseDemucsSeparation { get; init; } = ReadBooleanEnvironment("CINECORE_LYRICS_USE_DEMUCS", defaultValue: false);
        public string DemucsModel { get; init; } = Environment.GetEnvironmentVariable("CINECORE_DEMUCS_MODEL") ?? "htdemucs";
        public int TimeoutSeconds { get; init; } = ReadIntegerEnvironment("CINECORE_FASTER_WHISPER_TIMEOUT_SECONDS", 5400);
        public string? PythonExecutable { get; init; } = Environment.GetEnvironmentVariable("CINECORE_PYTHON");
        public string? ScriptPath { get; init; } = Environment.GetEnvironmentVariable("CINECORE_FASTER_WHISPER_SCRIPT");

        internal static bool ReadBooleanEnvironment(string name, bool defaultValue)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;
            return value == "1" ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadIntegerEnvironment(string name, int defaultValue)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, out int parsed) && parsed > 0
                ? parsed
                : defaultValue;
        }
    }

    /// <summary>
    /// Music-capable recognition adapter using faster-whisper word timestamps.
    /// The companion Python runner disables speech VAD by default because slow
    /// sustained singing is often classified as silence. Demucs vocal isolation
    /// is available as an explicit, cached high-quality mode because it is much
    /// more expensive than direct recognition.
    /// </summary>
    public sealed class FasterWhisperLyricsRecognitionBackend : ILyricsRecognitionBackend
    {
        private readonly FasterWhisperLyricsRecognitionOptions _options;
        private readonly string? _pythonExecutable;
        private readonly string? _scriptPath;
        private int _availabilityLogged;

        public FasterWhisperLyricsRecognitionBackend(FasterWhisperLyricsRecognitionOptions? options = null)
        {
            _options = options ?? new FasterWhisperLyricsRecognitionOptions();
            _scriptPath = ResolveScriptPath(_options.ScriptPath);
            _pythonExecutable = ResolvePythonExecutable(_options.PythonExecutable, _scriptPath);
        }

        public string Name => _options.UseDemucsSeparation
            ? "faster-whisper-music+demucs"
            : "faster-whisper-music";

        public bool IsAvailable(string audioPath)
        {
            bool audioAvailable = File.Exists(audioPath);
            bool scriptAvailable = !string.IsNullOrWhiteSpace(_scriptPath) && File.Exists(_scriptPath);
            bool pythonAvailable = !string.IsNullOrWhiteSpace(_pythonExecutable);
            bool available = audioAvailable && scriptAvailable && pythonAvailable;
            if (Interlocked.Exchange(ref _availabilityLogged, 1) == 0)
            {
                if (available)
                {
                    Dbg.Log($"[LYRICS-ASR] faster-whisper discoverable: python='{_pythonExecutable}', script='{_scriptPath}', model='{_options.Model}', device='{_options.Device}', computeType='{_options.ComputeType}', vad={_options.UseVoiceActivityDetection}, demucs={_options.UseDemucsSeparation}", Dbg.LogLevel.Info);
                }
                else
                {
                    Dbg.Warn($"[LYRICS-ASR] faster-whisper unavailable: audio={audioAvailable}, python='{_pythonExecutable ?? "<not found>"}', script='{_scriptPath ?? "<not found>"}', scriptExists={scriptAvailable}. Install faster-whisper in the discovered Python environment or set CINECORE_PYTHON.");
                }
            }
            return available;
        }

        public async Task<LyricsRecognitionResult?> RecognizeAsync(
            LyricsRecognitionRequest request,
            CancellationToken cancellationToken)
        {
            if (!IsAvailable(request.AudioPath))
                return null;

            var stopwatch = Stopwatch.StartNew();
            Dbg.Log($"[LYRICS-ASR] launching faster-whisper: audio='{request.AudioPath}', python='{_pythonExecutable}', script='{_scriptPath}', model='{_options.Model}', device='{_options.Device}', computeType='{_options.ComputeType}', vad={_options.UseVoiceActivityDetection}, demucs={_options.UseDemucsSeparation}", Dbg.LogLevel.Info);
            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutable!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(_scriptPath!) ?? AppContext.BaseDirectory
            };
            startInfo.Environment["PYTHONUTF8"] = "1";
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HF_HOME")))
                startInfo.Environment["HF_HOME"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "LyricsModels");

            // The Python launcher needs an explicit version selector; python.exe
            // accepts the script directly. ArgumentList avoids quoting bugs for
            // media paths containing apostrophes or parentheses.
            if (Path.GetFileName(_pythonExecutable!).Equals("py.exe", StringComparison.OrdinalIgnoreCase))
                startInfo.ArgumentList.Add("-3");
            startInfo.ArgumentList.Add(_scriptPath!);
            startInfo.ArgumentList.Add("--audio");
            startInfo.ArgumentList.Add(request.AudioPath);
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(_options.Model) ? "auto" : _options.Model);
            startInfo.ArgumentList.Add("--device");
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(_options.Device) ? "auto" : _options.Device);
            startInfo.ArgumentList.Add("--compute-type");
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(_options.ComputeType) ? "auto" : _options.ComputeType);
            if (!string.IsNullOrWhiteSpace(request.CanonicalLyrics))
            {
                string prompt = string.Join(" ", LyricsTextNormalizer.Normalize(request.CanonicalLyrics)
                    .Tokens.Take(110).Select(token => token.OriginalText ?? token.Text));
                if (prompt.Length > 800) prompt = prompt[..800];
                if (prompt.Length > 0)
                {
                    startInfo.ArgumentList.Add("--initial-prompt");
                    startInfo.ArgumentList.Add(prompt);
                }
            }
            startInfo.ArgumentList.Add(_options.UseVoiceActivityDetection ? "--vad" : "--no-vad");
            if (_options.UseDemucsSeparation)
            {
                startInfo.ArgumentList.Add("--separate");
                startInfo.ArgumentList.Add("--demucs-model");
                startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(_options.DemucsModel) ? "htdemucs" : _options.DemucsModel);
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    Dbg.Error("[LYRICS-ASR] Python process refused to start.");
                    return null;
                }
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            }
            catch (Exception ex)
            {
                Dbg.Error($"[LYRICS-ASR] Python process start failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.TimeoutSeconds > 0)
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var killRegistration = cancellationToken.Register(() => TryKill(process));

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            async Task<string> ReadDiagnosticsAsync()
            {
                var diagnostics = new System.Text.StringBuilder();
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is string line)
                {
                    const string marker = "progress=";
                    int offset = line.IndexOf(marker, StringComparison.Ordinal);
                    if (offset >= 0 && double.TryParse(line.Substring(offset + marker.Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fraction))
                    { try { request.Progress?.Invoke(fraction); } catch { } }
                    else { diagnostics.AppendLine(line); if (diagnostics.Length > 8192) diagnostics.Remove(0, diagnostics.Length - 4096); }
                }
                return diagnostics.ToString();
            }
            Task<string> stderrTask = ReadDiagnosticsAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                Dbg.Warn($"[LYRICS-ASR] faster-whisper timed out after {_options.TimeoutSeconds}s for '{request.AudioPath}'.");
                return null;
            }
            finally
            {
                TryKill(process);
            }

            string stdout;
            string stderr;
            try
            {
                stdout = await stdoutTask.ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dbg.Warn($"[LYRICS-ASR] failed reading Python output: {ex.GetType().Name}: {ex.Message}");
                return null;
            }

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                string detail = NormalizeProcessError(stderr);
                Dbg.Warn($"[LYRICS-ASR] faster-whisper exited with code {process.ExitCode}; reason='{detail}'");
                return null;
            }

            try
            {
                var result = LyricsRecognitionJson.Parse(stdout, Name, stopwatch.Elapsed.TotalMilliseconds);
                if (result == null || result.Words.Count == 0)
                {
                    Dbg.Warn($"[LYRICS-ASR] faster-whisper returned no usable timestamped words; stderr='{NormalizeProcessError(stderr)}'");
                    return null;
                }

                Dbg.Log($"[LYRICS-ASR] faster-whisper completed: words={result.Words.Count}, duration={(result.DurationSeconds?.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")}s, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:0}", Dbg.LogLevel.Info);
                return result;
            }
            catch (Exception ex)
            {
                // A missing Python package, failed model download, or malformed
                // recognizer response must never replace valid plain lyrics.
                Dbg.Warn($"[LYRICS-ASR] faster-whisper JSON parse failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static string NormalizeProcessError(string? value)
        {
            string text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 900 ? text : text[..900] + "…";
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        private static string? ResolvePythonExecutable(string? configured, string? scriptPath)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured) ? Path.GetFullPath(configured) : FindOnPath(configured);

            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(scriptPath))
            {
                try { roots.Add(Path.GetDirectoryName(scriptPath!) ?? string.Empty); } catch { }
            }
            roots.AddRange(Ancestors(AppContext.BaseDirectory));
            roots.AddRange(Ancestors(Environment.CurrentDirectory));
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(root, ".venv-cinecore-lyrics", "Scripts", "python.exe"),
                    Path.Combine(root, ".venv", "Scripts", "python.exe"),
                    Path.Combine(root, "venv", "Scripts", "python.exe")
                })
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return FindOnPath("python.exe") ?? FindOnPath("py.exe");
        }

        private static string? ResolveScriptPath(string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return Path.GetFullPath(configured);

            foreach (string root in Ancestors(AppContext.BaseDirectory).Concat(Ancestors(Environment.CurrentDirectory)))
            {
                string candidate = Path.Combine(root, "LyricsSynchronization", "music_lyrics_backend.py");
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static string? FindOnPath(string executable)
        {
            if (Path.IsPathRooted(executable) && File.Exists(executable))
                return executable;

            string[] names = Path.HasExtension(executable)
                ? new[] { executable }
                : new[] { executable, executable + ".exe" };

            string[] pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (string entry in pathEntries)
            {
                try
                {
                    foreach (string name in names)
                    {
                        string candidate = Path.Combine(entry.Trim(), name);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> Ancestors(string start)
        {
            DirectoryInfo? directory;
            try { directory = new DirectoryInfo(Path.GetFullPath(start)); }
            catch { yield break; }

            for (int i = 0; directory != null && i < 10; i++, directory = directory.Parent)
                yield return directory.FullName;
        }
    }
}
