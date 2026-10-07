#nullable enable
using System;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CinecorePlayer2025.Utilities
{
    internal static class BitstreamReleasePulse
    {
        private const int DefaultDurationMs = 480;

        public static string? GetDefaultRenderDeviceName()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return device.FriendlyName;
            }
            catch
            {
                return null;
            }
        }

        public static void Pulse(string? preferredRendererName, string? negotiatedRendererName, int durationMs = DefaultDurationMs)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = ResolveDevice(enumerator, preferredRendererName, negotiatedRendererName)
                    ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                var format = device.AudioClient.MixFormat;
                if (format.AverageBytesPerSecond <= 0 || format.BlockAlign <= 0)
                    format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

                using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 80);
                var silence = new TimedSilenceProvider(format, Math.Clamp(durationMs, 120, 1500));

                output.Init(silence);
                output.Play();

                var deadline = DateTime.UtcNow.AddMilliseconds(durationMs + 600);
                while (output.PlaybackState == PlaybackState.Playing && DateTime.UtcNow < deadline)
                    Thread.Sleep(20);

                try { output.Stop(); } catch { }

                Dbg.Log($"Audio release pulse: PCM silence sent to '{device.FriendlyName}'.", Dbg.LogLevel.Info);
            }
            catch (Exception ex)
            {
                Dbg.Warn("Audio release pulse failed: " + ex.Message);
            }
        }

        private static MMDevice? ResolveDevice(MMDeviceEnumerator enumerator, params string?[] names)
        {
            try
            {
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                foreach (string name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()))
                {
                    if (LooksLikeVirtualRenderer(name))
                        continue;

                    string clean = CleanRendererName(name);
                    var exact = devices.FirstOrDefault(d =>
                        string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.FriendlyName, clean, StringComparison.OrdinalIgnoreCase));
                    if (exact != null)
                        return exact;

                    var contained = devices.FirstOrDefault(d =>
                        ContainsEitherWay(d.FriendlyName, clean) ||
                        ContainsEitherWay(d.FriendlyName, name));
                    if (contained != null)
                        return contained;
                }
            }
            catch { }

            return null;
        }

        private static bool LooksLikeVirtualRenderer(string name)
        {
            return name.Contains("MPC Audio Renderer", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Default DirectSound Device", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Default WaveOut Device", StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanRendererName(string name)
        {
            string clean = name.Trim();
            int colon = clean.IndexOf(':');
            if (colon >= 0 && colon + 1 < clean.Length)
                clean = clean[(colon + 1)..].Trim();
            return clean;
        }

        private static bool ContainsEitherWay(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;

            return a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
                   b.Contains(a, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class TimedSilenceProvider : IWaveProvider
        {
            private long _remainingBytes;

            public TimedSilenceProvider(WaveFormat waveFormat, int durationMs)
            {
                WaveFormat = waveFormat;

                long bytes = (long)Math.Round(waveFormat.AverageBytesPerSecond * (durationMs / 1000.0));
                int blockAlign = Math.Max(1, waveFormat.BlockAlign);
                _remainingBytes = Math.Max(blockAlign, bytes - (bytes % blockAlign));
            }

            public WaveFormat WaveFormat { get; }

            public int Read(byte[] buffer, int offset, int count)
            {
                int bytes = (int)Math.Min(count, _remainingBytes);
                if (bytes <= 0)
                    return 0;

                Array.Clear(buffer, offset, bytes);
                _remainingBytes -= bytes;
                return bytes;
            }
        }
    }
}
