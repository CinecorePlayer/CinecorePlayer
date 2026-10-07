#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    // ======= DEBUG CORE (file + ring buffer + batch writer) =======
    internal static class Dbg
    {
        public enum LogLevel { Error = 0, Warn = 1, Info = 2, Verbose = 3 }
        public static LogLevel Level = LogLevel.Info;

        static readonly object _lock = new();
        static readonly Queue<string> _ring = new();
        static readonly int _maxLines = 1500;

        static readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "cinecore_debug.log");
        static readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
        static readonly Thread _writer;

        static Dbg()
        {
            try { File.AppendAllText(_logPath, $"\r\n==== RUN {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\r\n"); } catch { }
            _writer = new Thread(WriterProc) { IsBackground = true, Name = "DbgWriter" };
            _writer.Start();
        }

        static void WriterProc()
        {
            var batch = new List<string>(128);
            while (true)
            {
                try
                {
                    batch.Clear();
                    if (_queue.TryTake(out var first, 200))
                    {
                        batch.Add(first);
                        while (_queue.TryTake(out var line))
                        {
                            batch.Add(line);
                            if (batch.Count >= 256) break;
                        }
                    }
                    if (batch.Count > 0) File.AppendAllLines(_logPath, batch);
                }
                catch { Thread.Sleep(300); }
            }
        }

        static void Enqueue(string line)
        {
            lock (_lock)
            {
                _ring.Enqueue(line);
                while (_ring.Count > _maxLines) _ring.Dequeue();
            }
            try { Debug.WriteLine(line); } catch { }
            try { _queue.Add(line); } catch { }
        }

        static string Stamp(string msg) => $"{DateTime.Now:HH:mm:ss.fff} | {msg}";
        public static void Log(string msg, LogLevel lvl = LogLevel.Info)
        {
            if (lvl > Level) return;
            // Un indirizzo con una chiave d'accesso (flussi Jellyfin) non finisce mai in chiaro nel registro.
            if (msg.Contains("api_key=", StringComparison.OrdinalIgnoreCase)) msg = SecretVault.Redact(msg);
            Enqueue(Stamp(msg));
        }
        public static void Warn(string msg) => Log("WARN: " + msg, LogLevel.Warn);
        public static void Error(string msg) => Log("ERROR: " + msg, LogLevel.Error);
        public static string[] Snapshot() { lock (_lock) return _ring.ToArray(); }

        public static string Hex(Guid g) => g.ToString("B").ToUpperInvariant();
    }

    // ======= ENUM & MODALITÀ =======
    public enum HdrMode { Auto, Off }         // Off = forza SDR (tone-map con madVR/MPCVR)
    public enum Stereo3DMode { None, SBS, TAB }
    public enum VideoRendererChoice { MADVR, MPCVR, EVR, MPV }
    public enum MpvRuntimeChoice { Auto, X64, X64V3 }

    public sealed class MpvPlaybackSettings
    {
        public string Profile { get; set; } = "default";
        public string Hwdec { get; set; } = "auto-safe";
        public string VideoOutput { get; set; } = "gpu-next";
        public string GpuApi { get; set; } = "auto";
        public string GpuContext { get; set; } = "auto";
        public string VideoSync { get; set; } = "audio";
        public bool Interpolation { get; set; }
        public string ToneMapping { get; set; } = "auto";
        public bool HdrComputePeak { get; set; } = true;
        public string TargetPrim { get; set; } = "auto";
        public string TargetTrc { get; set; } = "auto";
        public int TargetPeak { get; set; }
        public string GamutMappingMode { get; set; } = "auto";
        public bool TargetColorspaceHint { get; set; } = true;
        public bool IccProfileAuto { get; set; } = true;
        public string BlendSubtitles { get; set; } = "auto";
        public string FboFormat { get; set; } = "auto";
        public string VideoOutputLevels { get; set; } = "auto";
        public string Scale { get; set; } = "ewa_lanczossharp";
        public string CScale { get; set; } = "ewa_lanczossoft";
        public string DScale { get; set; } = "mitchell";
        public string TScale { get; set; } = "oversample";
        public double ScaleAntiring { get; set; }
        public double CScaleAntiring { get; set; }
        public double DScaleAntiring { get; set; }
        public bool CorrectDownscaling { get; set; } = true;
        public bool LinearDownscaling { get; set; } = true;
        public bool SigmoidUpscaling { get; set; } = true;
        public double ToneMappingParam { get; set; }
        public double HdrContrastRecovery { get; set; }
        public double HdrContrastSmoothness { get; set; }
        public bool Deband { get; set; }
        public int DebandIterations { get; set; } = 1;
        public int DebandThreshold { get; set; } = 48;
        public int DebandRange { get; set; } = 16;
        public int DebandGrain { get; set; }
        public string Dither { get; set; } = "auto";
        public string DitherDepth { get; set; } = "auto";
        public int DitherSizeFruit { get; set; } = 6;
        public string ErrorDiffusion { get; set; } = "sierra-lite";
        public bool TemporalDither { get; set; } = true;
        public bool Deinterlace { get; set; }
        public double InterpolationThreshold { get; set; }
        public string Cache { get; set; } = "auto";
        public int DemuxerReadaheadSeconds { get; set; }
        public int DemuxerMaxBytesMb { get; set; }
        public int VideoThreads { get; set; }
        public bool VideoLavcDr { get; set; } = true;
        public string SubAuto { get; set; } = "fuzzy";
        public string SubAssOverride { get; set; } = "no";
        public double SubScale { get; set; } = 1.0;
        public int SubFontSize { get; set; } = 55;
        public double SubBorderSize { get; set; } = 3.0;
        public double SubShadowOffset { get; set; }
        public bool AudioExclusive { get; set; }
        public string AudioChannels { get; set; } = "auto";
        public bool AudioNormalizeDownmix { get; set; }
        public int VolumeMax { get; set; } = 100;
        public string GaplessAudio { get; set; } = "weak";
        public string? ExtraOptions { get; set; }

        public MpvPlaybackSettings Clone() => new()
        {
            Profile = string.IsNullOrWhiteSpace(Profile) ? "default" : Profile.Trim(),
            Hwdec = string.IsNullOrWhiteSpace(Hwdec) ? "auto-safe" : Hwdec.Trim(),
            VideoOutput = string.IsNullOrWhiteSpace(VideoOutput) ? "gpu-next" : VideoOutput.Trim(),
            GpuApi = string.IsNullOrWhiteSpace(GpuApi) ? "auto" : GpuApi.Trim(),
            GpuContext = string.IsNullOrWhiteSpace(GpuContext) ? "auto" : GpuContext.Trim(),
            VideoSync = string.IsNullOrWhiteSpace(VideoSync) ? "audio" : VideoSync.Trim(),
            Interpolation = Interpolation,
            ToneMapping = string.IsNullOrWhiteSpace(ToneMapping) ? "auto" : ToneMapping.Trim(),
            HdrComputePeak = HdrComputePeak,
            TargetPrim = string.IsNullOrWhiteSpace(TargetPrim) ? "auto" : TargetPrim.Trim(),
            TargetTrc = string.IsNullOrWhiteSpace(TargetTrc) ? "auto" : TargetTrc.Trim(),
            TargetPeak = Math.Clamp(TargetPeak, 0, 10000),
            GamutMappingMode = string.IsNullOrWhiteSpace(GamutMappingMode) ? "auto" : GamutMappingMode.Trim(),
            TargetColorspaceHint = TargetColorspaceHint,
            IccProfileAuto = IccProfileAuto,
            BlendSubtitles = string.IsNullOrWhiteSpace(BlendSubtitles) ? "auto" : BlendSubtitles.Trim(),
            FboFormat = string.IsNullOrWhiteSpace(FboFormat) ? "auto" : FboFormat.Trim(),
            VideoOutputLevels = string.IsNullOrWhiteSpace(VideoOutputLevels) ? "auto" : VideoOutputLevels.Trim(),
            Scale = string.IsNullOrWhiteSpace(Scale) ? "ewa_lanczossharp" : Scale.Trim(),
            CScale = string.IsNullOrWhiteSpace(CScale) ? "ewa_lanczossoft" : CScale.Trim(),
            DScale = string.IsNullOrWhiteSpace(DScale) ? "mitchell" : DScale.Trim(),
            TScale = string.IsNullOrWhiteSpace(TScale) ? "oversample" : TScale.Trim(),
            ScaleAntiring = Math.Clamp(ScaleAntiring, 0, 1),
            CScaleAntiring = Math.Clamp(CScaleAntiring, 0, 1),
            DScaleAntiring = Math.Clamp(DScaleAntiring, 0, 1),
            CorrectDownscaling = CorrectDownscaling,
            LinearDownscaling = LinearDownscaling,
            SigmoidUpscaling = SigmoidUpscaling,
            ToneMappingParam = Math.Clamp(ToneMappingParam, 0, 10),
            HdrContrastRecovery = Math.Clamp(HdrContrastRecovery, 0, 10),
            HdrContrastSmoothness = Math.Clamp(HdrContrastSmoothness, 0, 100),
            Deband = Deband,
            DebandIterations = Math.Clamp(DebandIterations, 0, 16),
            DebandThreshold = Math.Clamp(DebandThreshold, 0, 4096),
            DebandRange = Math.Clamp(DebandRange, 0, 4096),
            DebandGrain = Math.Clamp(DebandGrain, 0, 4096),
            Dither = string.IsNullOrWhiteSpace(Dither) ? "auto" : Dither.Trim(),
            DitherDepth = string.IsNullOrWhiteSpace(DitherDepth) ? "auto" : DitherDepth.Trim(),
            DitherSizeFruit = Math.Clamp(DitherSizeFruit, 2, 8),
            ErrorDiffusion = string.IsNullOrWhiteSpace(ErrorDiffusion) ? "sierra-lite" : ErrorDiffusion.Trim(),
            TemporalDither = TemporalDither,
            Deinterlace = Deinterlace,
            InterpolationThreshold = Math.Clamp(InterpolationThreshold, 0, 1),
            Cache = string.IsNullOrWhiteSpace(Cache) ? "auto" : Cache.Trim(),
            DemuxerReadaheadSeconds = Math.Clamp(DemuxerReadaheadSeconds, 0, 600),
            DemuxerMaxBytesMb = Math.Clamp(DemuxerMaxBytesMb, 0, 4096),
            VideoThreads = Math.Clamp(VideoThreads, 0, 32),
            VideoLavcDr = VideoLavcDr,
            SubAuto = string.IsNullOrWhiteSpace(SubAuto) ? "fuzzy" : SubAuto.Trim(),
            SubAssOverride = string.IsNullOrWhiteSpace(SubAssOverride) ? "no" : SubAssOverride.Trim(),
            SubScale = Math.Clamp(SubScale, 0.25, 4),
            SubFontSize = Math.Clamp(SubFontSize, 16, 120),
            SubBorderSize = Math.Clamp(SubBorderSize, 0, 12),
            SubShadowOffset = Math.Clamp(SubShadowOffset, 0, 12),
            AudioExclusive = AudioExclusive,
            AudioChannels = string.IsNullOrWhiteSpace(AudioChannels) ? "auto" : AudioChannels.Trim(),
            AudioNormalizeDownmix = AudioNormalizeDownmix,
            VolumeMax = Math.Clamp(VolumeMax, 50, 200),
            GaplessAudio = string.IsNullOrWhiteSpace(GaplessAudio) ? "weak" : GaplessAudio.Trim(),
            ExtraOptions = ExtraOptions
        };
    }
}
