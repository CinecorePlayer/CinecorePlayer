#nullable enable
using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    internal sealed class BitstreamPcmAnalysisSampler : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int BytesPerSample = sizeof(float);
        private const int ChunkFrames = 2400; // 50 ms
        private const int ChunkBytes = ChunkFrames * Channels * BytesPerSample;

        private readonly object _lock = new();
        private CancellationTokenSource? _cts;
        private Task? _worker;
        private Process? _process;

        public bool Start(string path, Func<double> getPositionSeconds, Func<bool> isPaused, Action<float[], int, int, int> pushPcm)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string? ffmpeg = ResolveFfmpegExe();
            if (string.IsNullOrWhiteSpace(ffmpeg))
                return false;

            Stop();

            var cts = new CancellationTokenSource();
            lock (_lock) _cts = cts;

            _worker = Task.Run(() => RunAsync(ffmpeg, path, getPositionSeconds, isPaused, pushPcm, cts.Token), CancellationToken.None);
            return true;
        }

        public void Stop()
        {
            CancellationTokenSource? oldCts;
            lock (_lock)
            {
                oldCts = _cts;
                _cts = null;
            }

            try { oldCts?.Cancel(); } catch { }
            KillCurrentProcess();
            try { oldCts?.Dispose(); } catch { }
        }

        public void Dispose() => Stop();

        private async Task RunAsync(string ffmpeg, string path, Func<double> getPositionSeconds, Func<bool> isPaused, Action<float[], int, int, int> pushPcm, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (isPaused())
                    {
                        await Task.Delay(120, ct).ConfigureAwait(false);
                        continue;
                    }

                    double startPos = Math.Max(0, SafePosition(getPositionSeconds));
                    await DecodeFromAsync(ffmpeg, path, startPos, getPositionSeconds, isPaused, pushPcm, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Dbg.Warn("[Meters] Bitstream PCM analysis EX: " + ex.Message);
                    await DelayNoThrow(350, ct).ConfigureAwait(false);
                }
            }

            KillCurrentProcess();
        }

        private async Task DecodeFromAsync(string ffmpeg, string path, double startPos, Func<double> getPositionSeconds, Func<bool> isPaused, Action<float[], int, int, int> pushPcm, CancellationToken ct)
        {
            using var process = StartProcess(ffmpeg, path, startPos);
            lock (_lock) _process = process;

            _ = Task.Run(() =>
            {
                try { process.StandardError.ReadToEnd(); } catch { }
            }, CancellationToken.None);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes + 32);
            int buffered = 0;
            var elapsed = Stopwatch.StartNew();
            long lastDriftCheck = 0;

            try
            {
                var stream = process.StandardOutput.BaseStream;
                while (!ct.IsCancellationRequested)
                {
                    if (isPaused())
                        break;

                    long now = elapsed.ElapsedMilliseconds;
                    if (now - lastDriftCheck >= 500)
                    {
                        lastDriftCheck = now;
                        double expected = startPos + elapsed.Elapsed.TotalSeconds;
                        double actual = SafePosition(getPositionSeconds);
                        if (Math.Abs(actual - expected) > 1.25)
                            break;
                    }

                    int need = Math.Min(ChunkBytes - buffered, buffer.Length - buffered);
                    int read = await stream.ReadAsync(buffer, buffered, need, ct).ConfigureAwait(false);
                    if (read <= 0)
                        break;

                    buffered += read;
                    int fullBytes = buffered - (buffered % (Channels * BytesPerSample));
                    if (fullBytes >= ChunkBytes)
                    {
                        PushBytes(buffer, ChunkBytes, pushPcm);
                        buffered -= ChunkBytes;
                        if (buffered > 0)
                            Buffer.BlockCopy(buffer, ChunkBytes, buffer, 0, buffered);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }

                lock (_lock)
                {
                    if (ReferenceEquals(_process, process))
                        _process = null;
                }
            }
        }

        private static Process StartProcess(string ffmpeg, string path, double startPos)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-re");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startPos.ToString("0.###", CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:a:0");
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-sn");
            psi.ArgumentList.Add("-dn");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add(Channels.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add(SampleRate.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("f32le");
            psi.ArgumentList.Add("pipe:1");

            var p = new Process { StartInfo = psi, EnableRaisingEvents = false };
            if (!p.Start())
                throw new InvalidOperationException("ffmpeg analysis process failed to start.");
            return p;
        }

        private static void PushBytes(byte[] bytes, int byteCount, Action<float[], int, int, int> pushPcm)
        {
            int sampleCount = byteCount / BytesPerSample;
            if (sampleCount <= 0)
                return;

            var samples = new float[sampleCount];
            Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * BytesPerSample);
            pushPcm(samples, sampleCount, SampleRate, Channels);
        }

        private void KillCurrentProcess()
        {
            Process? p;
            lock (_lock)
            {
                p = _process;
                _process = null;
            }

            try
            {
                if (p != null && !p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { }

            try { p?.Dispose(); } catch { }
        }

        private static async Task DelayNoThrow(int ms, CancellationToken ct)
        {
            try { await Task.Delay(ms, ct).ConfigureAwait(false); }
            catch { }
        }

        private static double SafePosition(Func<double> getPositionSeconds)
        {
            try
            {
                double v = getPositionSeconds();
                return double.IsFinite(v) ? v : 0;
            }
            catch { return 0; }
        }

        private static string? ResolveFfmpegExe()
        {
            string baseDir = AppContext.BaseDirectory;
            string arch = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            string[] candidates =
            {
                Path.Combine(baseDir, "third-parties", "ffmpeg", arch, "ffmpeg.exe"),
                Path.Combine(baseDir, "third-parties", "ffmpeg", "win-x64", "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg", arch, "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg", "win-x64", "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg.exe")
            };

            foreach (var p in candidates)
                if (File.Exists(p))
                    return p;

            return null;
        }
    }
}
