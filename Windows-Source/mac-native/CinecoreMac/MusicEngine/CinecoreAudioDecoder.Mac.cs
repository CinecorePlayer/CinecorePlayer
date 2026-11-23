#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace CinecorePlayer2025.Audio
{
    /// <summary>
    /// Il decoder del Cinecore Audio Engine di Windows (FFmpeg.AutoGen) per il Mac, con la stessa forma: FFmpeg in un
    /// processo, campioni float interlacciati alla frequenza chiesta. Lo usa l'analisi dell'Automix (battute, sezioni,
    /// livelli); la ricerca riparte FFmpeg dal punto voluto.
    /// </summary>
    internal sealed class CinecoreAudioDecoder : IDisposable
    {
        public int OutputRate { get; }
        public int OutputChannels { get; }
        public string SourcePath { get; }
        public double DurationSeconds { get; private set; }
        public bool EndOfStream { get; private set; }
        private Process? _ffmpeg;
        private Stream? _stdout;
        private readonly byte[] _bytes = new byte[65536];
        private int _carry;
        private double _position;
        private readonly double _speed;

        public CinecoreAudioDecoder(string path, int outputRate, int outputChannels, int streamOrdinal = -1, double start = 0, double speed = 1)
        {
            SourcePath = path; OutputRate = outputRate; OutputChannels = Math.Max(1, outputChannels);
            DurationSeconds = ProbeDuration(path);
            _speed = speed;
            Start(start);
        }

        private static double ProbeDuration(string path)
        {
            try
            {
                if (CinecoreMac.MacTools.Ffprobe is not { } probe) return 0;
                var psi = new ProcessStartInfo(probe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "json", path }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                string json = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                using var doc = JsonDocument.Parse(json);
                return double.TryParse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
            }
            catch { return 0; }
        }

        private void Start(double seconds)
        {
            Stop();
            EndOfStream = false; _carry = 0; _position = seconds;
            if (CinecoreMac.MacTools.Ffmpeg is not { } ffmpeg) { EndOfStream = true; return; }
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-nostdin", "-v", "error" }) psi.ArgumentList.Add(a);
            if (seconds > 0) { psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(seconds.ToString("0.###", CultureInfo.InvariantCulture)); }
            foreach (var a in new[] { "-i", SourcePath, "-map", "0:a:0", "-vn" }) psi.ArgumentList.Add(a);
            if (_speed != 1)
            {
                psi.ArgumentList.Add("-af");
                psi.ArgumentList.Add("atempo=" + _speed.ToString("0.######", CultureInfo.InvariantCulture));
            }
            foreach (var a in new[] { "-ac", OutputChannels.ToString(CultureInfo.InvariantCulture),
                         "-ar", OutputRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1" })
                psi.ArgumentList.Add(a);
            try
            {
                _ffmpeg = Process.Start(psi);
                _ffmpeg!.ErrorDataReceived += (_, _) => { };
                _ffmpeg.BeginErrorReadLine();
                _stdout = _ffmpeg.StandardOutput.BaseStream;
            }
            catch { EndOfStream = true; }
        }

        private void Stop()
        {
            try { if (_ffmpeg is { HasExited: false } p) p.Kill(); } catch { }
            try { _ffmpeg?.Dispose(); } catch { }
            _ffmpeg = null; _stdout = null;
        }

        /// <summary>Posiziona la lettura (FFmpeg riparte da quel punto).</summary>
        public void Seek(double seconds) => Start(Math.Max(0, seconds));

        /// <summary>Riempie <paramref name="output"/> con fotogrammi interlacciati; 0 con <see cref="EndOfStream"/> a fine brano.</summary>
        public int Read(float[] output, out double startSeconds) => Read(output.AsSpan(), out startSeconds);

        public int Read(Span<float> output, out double startSeconds)
        {
            startSeconds = _position;
            if (_stdout == null) { EndOfStream = true; return 0; }
            int frameBytes = OutputChannels * 4;
            int wantBytes = Math.Min(_bytes.Length, (output.Length / OutputChannels) * frameBytes);
            int got = _carry;
            while (got < frameBytes && got < wantBytes)
            {
                int n = 0;
                try { n = _stdout.Read(_bytes, got, wantBytes - got); } catch { }
                if (n <= 0) break;
                got += n;
                if (got >= Math.Min(wantBytes, 16384)) break;
            }
            int frames = got / frameBytes;
            if (frames == 0) { EndOfStream = true; return 0; }
            MemoryMarshal.Cast<byte, float>(_bytes.AsSpan(0, frames * frameBytes)).CopyTo(output);
            _carry = got - frames * frameBytes;
            if (_carry > 0) Buffer.BlockCopy(_bytes, frames * frameBytes, _bytes, 0, _carry);
            _position += frames * _speed / OutputRate;
            return frames;
        }

        public void Dispose() => Stop();
    }
}
