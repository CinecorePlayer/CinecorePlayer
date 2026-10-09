#nullable enable
using DirectShowLib;
using System;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025.Engines
{
    /// <summary>
    /// Cio' che dichiarano i componenti della catena di riproduzione (decodificatori LAV,
    /// renderer video, renderer audio), letto dalle loro interfacce: non e' una nostra stima.
    /// I campi restano vuoti (o negativi) quando il componente non c'e' o non risponde.
    /// </summary>
    public sealed class PlaybackComponentReport
    {
        // Renderer video.
        public string RendererVersion = "";
        public bool? Exclusive, DxvaDecoding, DxvaScaling, DxvaDeinterlacing, Ivtc;
        public double RefreshRate, SourceFrameRate, PresentedFrameRate;
        public int SourceWidth, SourceHeight, OutputWidth, OutputHeight;
        /// <summary>madVR: "TV.2020", "PC.709", "None" (RGB)...</summary>
        public string YuvMatrix = "";
        public int FramesDrawn = -1, FramesDropped = -1, JitterMs = -1, SyncDeviationMs = -1;
        /// <summary>madVR conta le presentazioni a schermo (ripetizioni comprese), non i fotogrammi del
        /// video: i suoi "disegnati" e la sua cadenza media non si possono leggere come fps del film.</summary>
        public bool CountsDisplayRefreshes;
        public int? SyncOffsetMs;

        // Decodificatore video.
        public string VideoDecoder = "", VideoDecoderDevice = "";

        // Decodificatore e uscita audio.
        public string AudioCodec = "", AudioDecodeFormat = "", AudioOutputFormat = "", AudioRenderer = "";
        public int AudioDecodeChannels, AudioDecodeRate, AudioOutputChannels, AudioOutputRate;

        // mpv.
        public string PlayerVersion = "", VideoOutputDriver = "", AudioOutputDriver = "";
        public long DecoderDroppedFrames = -1;
        public double CacheSeconds = -1;
        public long CacheSpeedBytes = -1;
    }

    public sealed partial class DirectShowUnifiedEngine
    {
        // LAV Audio (LAVAudioSettings.h). Le stringhe sono const char* del filtro: non vanno liberate.
        [ComImport, Guid("A668B8F2-BA87-4F63-9D41-768F7DE9C50E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ILavAudioStatus
        {
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool IsSampleFormatSupported(int sampleFormat);
            [PreserveSig] int GetDecodeDetails(out IntPtr codec, out IntPtr decodeFormat, out int channels, out int sampleRate, out uint channelMask);
            [PreserveSig] int GetOutputDetails(out IntPtr outputFormat, out int channels, out int sampleRate, out uint channelMask);
            [PreserveSig] int EnableVolumeStats();
            [PreserveSig] int DisableVolumeStats();
            [PreserveSig] int GetChannelVolumeAverage(ushort channel, out float db);
        }

        private static string Ansi(IntPtr text)
        {
            try { return text == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(text) ?? ""; }
            catch { return ""; }
        }

        private static string MadVrText(IMadVRInfo info, string field)
        {
            string value = ReadMadVrString(info, field);
            return value.StartsWith("n/d", StringComparison.Ordinal) || value.StartsWith("err:", StringComparison.Ordinal) ? "" : value.Trim();
        }

        private static bool? MadVrFlag(IMadVRInfo info, string field)
        {
            try { return info.GetBool(field, out bool value) == 0 ? value : null; }
            catch { return null; }
        }

        public PlaybackComponentReport GetComponentReport()
        {
            var report = new PlaybackComponentReport();

            // --- Decodificatore video (LAV Video) ---
            try
            {
                if (_lavVideo is ILavVideoStatus video)
                {
                    report.VideoDecoder = (Marshal.PtrToStringUni(video.GetActiveDecoderName()) ?? "").Trim();
                    if (video.GetHWAccelActiveDevice(out string device) >= 0 && !string.IsNullOrWhiteSpace(device))
                        report.VideoDecoderDevice = device.Trim();
                }
            }
            catch { }

            // --- Decodificatore audio (LAV Audio) ---
            try
            {
                if (_lavAudio is ILavAudioStatus audio)
                {
                    if (audio.GetDecodeDetails(out IntPtr codec, out IntPtr format, out int channels, out int rate, out _) >= 0)
                    {
                        report.AudioCodec = Ansi(codec);
                        report.AudioDecodeFormat = Ansi(format);
                        report.AudioDecodeChannels = Math.Max(0, channels);
                        report.AudioDecodeRate = Math.Max(0, rate);
                    }
                    if (audio.GetOutputDetails(out IntPtr output, out int outChannels, out int outRate, out _) >= 0)
                    {
                        report.AudioOutputFormat = Ansi(output);
                        report.AudioOutputChannels = Math.Max(0, outChannels);
                        report.AudioOutputRate = Math.Max(0, outRate);
                    }
                }
            }
            catch { }

            // --- Renderer audio: il dispositivo scelto (o, se non lo si conosce, il nome del filtro) ---
            try
            {
                if (_audioRenderer != null)
                {
                    static bool Known(string? name) => !string.IsNullOrWhiteSpace(name) && name != "?";
                    report.AudioRenderer = (Known(_audioRendererDeviceName) ? _audioRendererDeviceName : Known(_audioRendererName) ? _audioRendererName : "").Trim();
                }
            }
            catch { }

            // --- Renderer video: contatori standard di DirectShow (madVR, MPCVR, EVR) ---
            try
            {
                if (_videoRenderer is IQualProp quality)
                {
                    if (quality.get_FramesDrawn(out int drawn) >= 0) report.FramesDrawn = drawn;
                    if (quality.get_FramesDroppedInRenderer(out int dropped) >= 0) report.FramesDropped = dropped;
                    if (quality.get_AvgFrameRate(out int rate) >= 0 && rate > 0) report.PresentedFrameRate = rate / 100.0;
                    if (quality.get_Jitter(out int jitter) >= 0) report.JitterMs = jitter;
                    if (quality.get_AvgSyncOffset(out int offset) >= 0) report.SyncOffsetMs = offset;
                    if (quality.get_DevSyncOffset(out int deviation) >= 0) report.SyncDeviationMs = deviation;
                }
            }
            catch { }

            // --- madVR ---
            try
            {
                var info = TryGetMadVrInfoInterface();
                if (info != null)
                {
                    report.CountsDisplayRefreshes = true;
                    report.RendererVersion = MadVrText(info, "version");
                    report.YuvMatrix = MadVrText(info, "yuvMatrix");
                    report.Exclusive = MadVrFlag(info, "exclusiveModeActive");
                    report.DxvaDecoding = MadVrFlag(info, "dxvaDecodingActive");
                    report.DxvaScaling = MadVrFlag(info, "dxvaScalingActive");
                    report.DxvaDeinterlacing = MadVrFlag(info, "dxvaDeinterlacingActive");
                    report.Ivtc = MadVrFlag(info, "ivtcActive");
                    if (info.GetDouble("refreshRate", out double refresh) == 0 && refresh > 1 && refresh < 1000) report.RefreshRate = refresh;
                    if (info.GetSize("originalVideoSize", out var original) == 0 && original.cx > 0 && original.cy > 0)
                    { report.SourceWidth = original.cx; report.SourceHeight = original.cy; }
                    if (info.GetRect("videoOutputRect", out var output) == 0 && output.right > output.left && output.bottom > output.top)
                    { report.OutputWidth = output.right - output.left; report.OutputHeight = output.bottom - output.top; }
                    report.SourceFrameRate = GetMadVrPresentedFrameRate();
                }
            }
            catch { }

            // --- MPC Video Renderer: versione (quattro campi da 16 bit) ---
            try
            {
                var config = TryGetMpcvrConfig();
                if (config != null && config.Flt_GetInt64("version", out long version) == 0 && version > 0)
                {
                    int major = (int)((version >> 48) & 0xFFFF), minor = (int)((version >> 32) & 0xFFFF), build = (int)((version >> 16) & 0xFFFF), revision = (int)(version & 0xFFFF);
                    if (major < 100 && minor < 1000)
                        report.RendererVersion = $"{major}.{minor}.{build}.{revision}";
                }
            }
            catch { }

            return report;
        }
    }
}
