#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025.Engines
{
    /// <summary>
    /// Alternative playback backend built directly on libmpv.
    /// The native dependency is optional at build time: drop libmpv-2.dll next to the exe
    /// or under third-parties\mpv\x86_64 / x86_64-v3 to make this engine available at runtime.
    /// </summary>
    public sealed class LibMpvPlaybackEngine : IPlaybackEngine
    {
        private readonly bool _preferBitstream;
        private readonly bool _forcePcmToggle;
        private readonly string? _preferredAudioDeviceName;
        private readonly bool _fileIsHdr;
        private readonly AVCodecID _srcAudioCodec;
        private readonly MpvRuntimeChoice _runtimeChoice;
        private readonly MpvPlaybackSettings _settings;
        private readonly object _sync = new();

        private nint _mpv;
        private nint _ownerHwnd;
        private Rectangle _ownerClient;
        private Rectangle _lastDest;
        private bool _hasVideo;
        private bool _disposed;
        private bool _opened;
        private bool _playbackStarted;
        private bool _ended;
        private bool _bitstreamActive;
        private string? _mediaPath;
        private string? _externalAudioUrl;
        private int? _initialInternalAudioOrdinal;
        private double _durationCache;
        private int _lastSnapshotBytes;
        private DateTime _lastSnapshotAt = DateTime.MinValue;
        private readonly Thumbnailer _thumb = new();
        private System.Windows.Forms.Timer? _timer;
        private CancellationTokenSource? _eventCts;
        private Task? _eventTask;
        private Action? _updateCb;

        public event Action<double>? OnProgressSeconds;
        public event Action<string>? OnStatus;
        public event Action<bool>? OnBitstreamChanged;

        public LibMpvPlaybackEngine(
            bool preferBitstream,
            bool forcePcmToggle,
            string? preferredAudioDeviceName,
            bool fileIsHdr,
            AVCodecID srcAudioCodec,
            MpvRuntimeChoice runtimeChoice = MpvRuntimeChoice.X64,
            MpvPlaybackSettings? settings = null)
        {
            _preferBitstream = preferBitstream;
            _forcePcmToggle = forcePcmToggle;
            _preferredAudioDeviceName = preferredAudioDeviceName;
            _fileIsHdr = fileIsHdr;
            _srcAudioCodec = srcAudioCodec;
            _runtimeChoice = runtimeChoice;
            _settings = settings?.Clone() ?? new MpvPlaybackSettings();
        }

        public void SetInitialVideoHost(nint ownerHwnd, Rectangle ownerClient)
        {
            _ownerHwnd = ownerHwnd;
            _ownerClient = ownerClient;
            _lastDest = ownerClient.Width > 0 && ownerClient.Height > 0
                ? new Rectangle(0, 0, ownerClient.Width, ownerClient.Height)
                : Rectangle.Empty;
        }

        public void SetExternalAudioUrl(string url)
        {
            _externalAudioUrl = string.IsNullOrWhiteSpace(url) ? null : url;
            if (_mpv != nint.Zero && !string.IsNullOrWhiteSpace(_externalAudioUrl))
                TryCommand("audio-add", _externalAudioUrl!, "select");
        }

        public bool SetAudioDelayMilliseconds(int milliseconds)
            => TrySetProperty("audio-delay", (milliseconds / 1000d).ToString("0.###", CultureInfo.InvariantCulture));

        public void SetInitialInternalAudioOrdinal(int ordinal)
            => _initialInternalAudioOrdinal = Math.Max(0, ordinal);

        public void Open(string mediaPath, bool hasVideo)
        {
            ThrowIfDisposed();

            if (!LibMpvNative.TryEnsureLoaded(_runtimeChoice, out var loadError))
                throw new ApplicationException(loadError);

            _mediaPath = mediaPath;
            _hasVideo = hasVideo;
            _ended = false;
            _durationCache = 0;

            try
            {
                _mpv = LibMpvNative.Create();
                if (_mpv == nint.Zero)
                    throw new ApplicationException("libmpv non ha creato il contesto di riproduzione.");

                ConfigureBeforeInitialize();
                LibMpvNative.ThrowIfError(LibMpvNative.Initialize(_mpv), "mpv_initialize");

                StartEventPump();

                // Load external audio as part of the same file transaction, before
                // decoding starts; audio-add immediately after loadfile races it.
                if (!string.IsNullOrWhiteSpace(_externalAudioUrl))
                    LibMpvNative.ThrowIfError(LibMpvNative.SetPropertyString(_mpv, "audio-files", _externalAudioUrl!), "external audio");
                LibMpvNative.ThrowIfError(Command("loadfile", mediaPath, "replace"), "mpv loadfile");

                _opened = true;
                _bitstreamActive = ShouldUseSpdifPassthrough();
                StartTimer();

                if (hasVideo) { try { _thumb.Open(mediaPath); } catch { } }

                SafeStatus(_bitstreamActive ? "MPV: Bitstream" : "MPV: PCM");
                try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { }
                _updateCb?.Invoke();
            }
            catch (DllNotFoundException ex)
            {
                Dispose();
                throw new ApplicationException("libmpv non disponibile: mpv-2.dll non trovata o dipendenze native mancanti.", ex);
            }
            catch (BadImageFormatException ex)
            {
                Dispose();
                throw new ApplicationException("libmpv non compatibile con il processo x64. Usa una build x64 di mpv-2.dll.", ex);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void ConfigureBeforeInitialize()
        {
            TrySetOption("terminal", "no");
            TrySetOption("msg-level", "all=warn");
            TrySetOption("input-default-bindings", "yes");
            TrySetOption("input-vo-keyboard", "yes");
            TrySetOption("osc", "no");
            TrySetOption("osd-level", "0");
            TrySetOption("keep-open", "no");
            TrySetOption("pause", "yes");
            TrySetOption("ytdl", "yes");
            TrySetOption("ytdl-format", BuildYtdlFormatSelector());
            TrySetYtdlPath();
            TrySetOption("audio-client-name", "Cinecore Player");
            TrySetOption("ao", "wasapi");
            if (_hasVideo) ApplyVideoOutputOptions();
            else
            {
                ApplyAudioOptions();
                ApplyPerformanceOptions();
                ApplyExtraOptions(_settings.ExtraOptions);
                TrySetOption("audio-display", "no");
                TrySetOption("vid", "no");
                TrySetOption("vo", "null");
                TrySetOption("force-window", "no");
            }

            if (_ownerHwnd != nint.Zero)
                TrySetOption("wid", _ownerHwnd.ToInt64().ToString(CultureInfo.InvariantCulture));

            if (ShouldUseSpdifPassthrough())
                TrySetOption("audio-spdif", "ac3,dts,dts-hd,eac3,truehd");

            if (!string.IsNullOrWhiteSpace(_preferredAudioDeviceName))
            {
                Dbg.Log($"MPV: requested audio device '{_preferredAudioDeviceName}', using mpv default WASAPI device mapping.", Dbg.LogLevel.Info);
            }
        }

        private static string BuildYtdlFormatSelector()
        {
            return "bv*[height<=2160]+ba/bv*+ba/b";
        }

        private void TrySetYtdlPath()
        {
            try
            {
                string? ytdlp = YtDlpLocator.Find();
                if (string.IsNullOrWhiteSpace(ytdlp))
                    return;

                TrySetOption("script-opts-append", "ytdl_hook-ytdl_path=" + ytdlp);
            }
            catch { }
        }

        private void ApplyVideoOutputOptions()
        {
            string profile = NormalizeOptionValue(_settings.Profile, "default");
            if (!string.Equals(profile, "default", StringComparison.OrdinalIgnoreCase))
                TrySetOption("profile", profile);

            string vo = NormalizeOptionValue(_settings.VideoOutput, "gpu-next");
            if (!string.Equals(vo, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("vo", vo);

            string gpuApi = NormalizeOptionValue(_settings.GpuApi, "auto");
            if (!string.Equals(gpuApi, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("gpu-api", gpuApi);

            string gpuContext = NormalizeOptionValue(_settings.GpuContext, "auto");
            if (!string.Equals(gpuContext, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("gpu-context", gpuContext);

            string hwdec = NormalizeOptionValue(_settings.Hwdec, "auto-safe");
            TrySetOption("hwdec", hwdec);

            string videoSync = NormalizeOptionValue(_settings.VideoSync, "audio");
            TrySetOption("video-sync", videoSync);
            TrySetOption("interpolation", _settings.Interpolation ? "yes" : "no");

            ApplyScalerOptions();
            ApplyHdrOptions();
            ApplyDebandOptions();
            ApplySubtitleOptions();
            ApplyAudioOptions();
            ApplyPerformanceOptions();
            ApplyExtraOptions(_settings.ExtraOptions);
        }

        private void ApplyScalerOptions()
        {
            TrySetOption("scale", NormalizeOptionValue(_settings.Scale, "ewa_lanczossharp"));
            TrySetOption("cscale", NormalizeOptionValue(_settings.CScale, "ewa_lanczossoft"));
            TrySetOption("dscale", NormalizeOptionValue(_settings.DScale, "mitchell"));
            TrySetOption("tscale", NormalizeOptionValue(_settings.TScale, "oversample"));
            TrySetOption("scale-antiring", ClampDouble(_settings.ScaleAntiring, 0, 1).ToString("0.###", CultureInfo.InvariantCulture));
            TrySetOption("cscale-antiring", ClampDouble(_settings.CScaleAntiring, 0, 1).ToString("0.###", CultureInfo.InvariantCulture));
            TrySetOption("dscale-antiring", ClampDouble(_settings.DScaleAntiring, 0, 1).ToString("0.###", CultureInfo.InvariantCulture));
            TrySetOption("correct-downscaling", _settings.CorrectDownscaling ? "yes" : "no");
            TrySetOption("linear-downscaling", _settings.LinearDownscaling ? "yes" : "no");
            TrySetOption("sigmoid-upscaling", _settings.SigmoidUpscaling ? "yes" : "no");

            string fboFormat = NormalizeOptionValue(_settings.FboFormat, "auto");
            if (!string.Equals(fboFormat, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("fbo-format", fboFormat);

            string outputLevels = NormalizeOptionValue(_settings.VideoOutputLevels, "auto");
            if (!string.Equals(outputLevels, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("video-output-levels", outputLevels);
        }

        private void ApplyHdrOptions()
        {
            string toneMapping = NormalizeOptionValue(_settings.ToneMapping, "auto");
            if (!string.Equals(toneMapping, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("tone-mapping", toneMapping);

            TrySetOption("hdr-compute-peak", _settings.HdrComputePeak ? "yes" : "no");
            if (_settings.ToneMappingParam > 0)
                TrySetOption("tone-mapping-param", ClampDouble(_settings.ToneMappingParam, 0, 10).ToString("0.###", CultureInfo.InvariantCulture));
            if (_settings.HdrContrastRecovery > 0)
                TrySetOption("hdr-contrast-recovery", ClampDouble(_settings.HdrContrastRecovery, 0, 10).ToString("0.###", CultureInfo.InvariantCulture));
            if (_settings.HdrContrastSmoothness > 0)
                TrySetOption("hdr-contrast-smoothness", ClampDouble(_settings.HdrContrastSmoothness, 0, 100).ToString("0.###", CultureInfo.InvariantCulture));

            string targetPrim = NormalizeOptionValue(_settings.TargetPrim, "auto");
            if (!string.Equals(targetPrim, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("target-prim", targetPrim);

            string targetTrc = NormalizeOptionValue(_settings.TargetTrc, "auto");
            if (!string.Equals(targetTrc, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("target-trc", targetTrc);

            if (_settings.TargetPeak > 0)
                TrySetOption("target-peak", ClampOption(_settings.TargetPeak, 0, 10000).ToString(CultureInfo.InvariantCulture));

            string gamutMapping = NormalizeOptionValue(_settings.GamutMappingMode, "auto");
            if (!string.Equals(gamutMapping, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("gamut-mapping-mode", gamutMapping);

            TrySetOption("target-colorspace-hint", _settings.TargetColorspaceHint ? "yes" : "no");
            TrySetOption("icc-profile-auto", _settings.IccProfileAuto ? "yes" : "no");

            string blendSubtitles = NormalizeOptionValue(_settings.BlendSubtitles, "auto");
            if (!string.Equals(blendSubtitles, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("blend-subtitles", blendSubtitles);
        }

        private void ApplyDebandOptions()
        {
            TrySetOption("deband", _settings.Deband ? "yes" : "no");
            if (_settings.Deband)
            {
                TrySetOption("deband-iterations", ClampOption(_settings.DebandIterations, 0, 16).ToString(CultureInfo.InvariantCulture));
                TrySetOption("deband-threshold", ClampOption(_settings.DebandThreshold, 0, 4096).ToString(CultureInfo.InvariantCulture));
                TrySetOption("deband-range", ClampOption(_settings.DebandRange, 0, 4096).ToString(CultureInfo.InvariantCulture));
                TrySetOption("deband-grain", ClampOption(_settings.DebandGrain, 0, 4096).ToString(CultureInfo.InvariantCulture));
            }

            string dither = NormalizeOptionValue(_settings.Dither, "auto");
            if (!string.Equals(dither, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("dither", dither);
            TrySetOption("dither-depth", NormalizeOptionValue(_settings.DitherDepth, "auto"));
            TrySetOption("dither-size-fruit", ClampOption(_settings.DitherSizeFruit, 2, 8).ToString(CultureInfo.InvariantCulture));
            string errorDiffusion = NormalizeOptionValue(_settings.ErrorDiffusion, "sierra-lite");
            if (!string.Equals(errorDiffusion, "auto", StringComparison.OrdinalIgnoreCase))
                TrySetOption("error-diffusion", errorDiffusion);
            TrySetOption("temporal-dither", _settings.TemporalDither ? "yes" : "no");
        }

        private void ApplySubtitleOptions()
        {
            TrySetOption("sub-auto", NormalizeOptionValue(_settings.SubAuto, "fuzzy"));
            TrySetOption("sub-ass-override", NormalizeOptionValue(_settings.SubAssOverride, "no"));
            TrySetOption("sub-scale", ClampDouble(_settings.SubScale, 0.25, 4).ToString("0.###", CultureInfo.InvariantCulture));
            TrySetOption("sub-font-size", ClampOption(_settings.SubFontSize, 16, 120).ToString(CultureInfo.InvariantCulture));
            TrySetOption("sub-border-size", ClampDouble(_settings.SubBorderSize, 0, 12).ToString("0.###", CultureInfo.InvariantCulture));
            TrySetOption("sub-shadow-offset", ClampDouble(_settings.SubShadowOffset, 0, 12).ToString("0.###", CultureInfo.InvariantCulture));
        }

        private void ApplyAudioOptions()
        {
            TrySetOption("audio-exclusive", _settings.AudioExclusive ? "yes" : "no");
            TrySetOption("audio-channels", NormalizeOptionValue(_settings.AudioChannels, "auto"));
            TrySetOption("audio-normalize-downmix", _settings.AudioNormalizeDownmix ? "yes" : "no");
            TrySetOption("volume-max", ClampOption(_settings.VolumeMax, 50, 200).ToString(CultureInfo.InvariantCulture));
            TrySetOption("gapless-audio", NormalizeOptionValue(_settings.GaplessAudio, "weak"));
        }

        private void ApplyPerformanceOptions()
        {
            TrySetOption("deinterlace", _settings.Deinterlace ? "yes" : "no");
            if (_settings.InterpolationThreshold > 0)
                TrySetOption("interpolation-threshold", ClampDouble(_settings.InterpolationThreshold, 0, 1).ToString("0.###", CultureInfo.InvariantCulture));
            TrySetOption("vd-lavc-dr", _settings.VideoLavcDr ? "yes" : "no");
            TrySetOption("vd-lavc-threads", ClampOption(_settings.VideoThreads, 0, 32).ToString(CultureInfo.InvariantCulture));

            string cache = NormalizeOptionValue(_settings.Cache, "auto");
            TrySetOption("cache", cache);

            if (_settings.DemuxerReadaheadSeconds > 0)
                TrySetOption("demuxer-readahead-secs", ClampOption(_settings.DemuxerReadaheadSeconds, 0, 600).ToString(CultureInfo.InvariantCulture));

            if (_settings.DemuxerMaxBytesMb > 0)
                TrySetOption("demuxer-max-bytes", ClampOption(_settings.DemuxerMaxBytesMb, 0, 4096).ToString(CultureInfo.InvariantCulture) + "MiB");
        }

        private static string NormalizeOptionValue(string? value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static int ClampOption(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
        private static double ClampDouble(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

        private void ApplyExtraOptions(string? extraOptions)
        {
            if (string.IsNullOrWhiteSpace(extraOptions))
                return;

            foreach (var rawLine in extraOptions.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                while (line.StartsWith("--", StringComparison.Ordinal))
                    line = line.Substring(2).TrimStart();

                int eq = line.IndexOf('=');
                if (eq <= 0 || eq >= line.Length - 1)
                {
                    Dbg.Warn("MPV option ignored, expected name=value: " + line);
                    continue;
                }

                var name = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                if (name.Length == 0 || value.Length == 0)
                    continue;

                TrySetOption(name, value);
            }
        }

        private bool ShouldUseSpdifPassthrough()
        {
            if (_forcePcmToggle || !_preferBitstream)
                return false;

            return _srcAudioCodec switch
            {
                AVCodecID.AV_CODEC_ID_AC3 => true,
                AVCodecID.AV_CODEC_ID_EAC3 => true,
                AVCodecID.AV_CODEC_ID_DTS => true,
                AVCodecID.AV_CODEC_ID_TRUEHD => true,
                _ => true
            };
        }

        public void Play()
        {
            ThrowIfDisposed();
            if (_mpv == nint.Zero) return;
            _ended = false;
            _playbackStarted = true;
            TrySetProperty("pause", "no");
            StartTimer();
            SafeStatus("MPV: Riproduzione.");
            _updateCb?.Invoke();
        }

        public void Pause()
        {
            ThrowIfDisposed();
            if (_mpv == nint.Zero) return;
            TrySetProperty("pause", "yes");
            SafeStatus("MPV: Pausa.");
        }

        public void Stop()
        {
            try
            {
                if (_mpv != nint.Zero)
                    TryCommand("stop");
            }
            catch { }
            StopTimer();
            SafeStatus("MPV: Stop.");
        }

        public double DurationSeconds
        {
            get
            {
                if (TryGetDouble("duration", out var value) && value > 0 && !double.IsNaN(value))
                    _durationCache = value;
                return _durationCache;
            }
        }

        public double PositionSeconds
        {
            get
            {
                if (_ended && _durationCache > 0)
                    return _durationCache;
                if (TryGetDouble("time-pos", out var value) && value >= 0 && !double.IsNaN(value))
                    return value;
                return 0;
            }
            set
            {
                if (_mpv == nint.Zero) return;
                double target = Math.Max(0, value);
                _ended = false;
                TryCommand("seek", target.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
            }
        }

        public void SetVolume(float volume)
        {
            float clamped = Math.Clamp(volume, 0f, 1f);
            if (!_bitstreamActive)
                TrySetProperty("volume", (clamped * 100f).ToString("0.###", CultureInfo.InvariantCulture));
            try { CoreAudioSessionVolume.Set(clamped); } catch { }
        }

        public void UpdateVideoWindow(nint ownerHwnd, Rectangle ownerClient)
        {
            if (ownerHwnd == nint.Zero || ownerClient.Width <= 0 || ownerClient.Height <= 0)
                return;

            bool ownerChanged = _ownerHwnd != ownerHwnd;
            _ownerHwnd = ownerHwnd;
            _ownerClient = ownerClient;
            _lastDest = new Rectangle(0, 0, ownerClient.Width, ownerClient.Height);

            if (_mpv != nint.Zero && ownerChanged)
                TrySetProperty("wid", ownerHwnd.ToInt64().ToString(CultureInfo.InvariantCulture));
        }

        public Rectangle GetLastDestRectAsClient(Rectangle ownerClient)
        {
            if (_lastDest.Width <= 0 || _lastDest.Height <= 0)
                return new Rectangle(0, 0, ownerClient.Width, ownerClient.Height);
            return _lastDest;
        }

        public void SetStereo3D(Stereo3DMode mode)
        {
            // The current UI forces EVR for the 3D-to-2D path. MPV stays neutral here.
        }

        public void SetUpscaling(bool enable)
        {
            // MPV owns its scaler pipeline; Cinecore-side madVR profiles are not applied here.
        }

        public void BindUpdateCallback(Action? cb) => _updateCb = cb;

        public bool IsBitstreamActive() => _bitstreamActive;

        public bool HasDisplayControl() => _hasVideo && _mpv != nint.Zero && _ownerHwnd != nint.Zero;

        public (string InDetail, string OutDetail, int AudioNowKbps, int AudioAvgKbps, bool Bitstream) GetAudioOverlayDetails()
        {
            string codec = FirstNonEmpty(
                GetSelectedTrackString("audio", "codec"),
                TryGetString("audio-codec-name"),
                TryGetString("audio-codec"),
                CodecNameFromSource(_srcAudioCodec));

            string prettyCodec = PrettyAudioCodec(codec, GetSelectedTrackString("audio", "title"));

            int inRate = FirstPositiveLong(
                GetSelectedTrackLong("audio", "demux-samplerate"),
                GetSelectedTrackLong("audio", "samplerate"),
                TryGetLongValue("audio-params/samplerate"),
                _settings != null ? 0 : 0);
            int inChannels = FirstPositiveLong(
                GetSelectedTrackLong("audio", "demux-channel-count"),
                GetSelectedTrackLong("audio", "audio-channels"),
                TryGetLongValue("audio-params/channel-count"));

            int outRate = FirstPositiveLong(TryGetLongValue("audio-out-params/samplerate"), TryGetLongValue("audio-params/samplerate"), inRate);
            int outChannels = FirstPositiveLong(TryGetLongValue("audio-out-params/channel-count"), TryGetLongValue("audio-params/channel-count"), inChannels);
            string outFormat = FirstNonEmpty(TryGetString("audio-out-params/format"), TryGetString("audio-params/format"));
            int outBits = GuessAudioBitsFromMpvFormat(outFormat, _srcAudioCodec);

            int avgKbps = FirstPositiveLong(
                GetSelectedTrackLong("audio", "demux-bitrate") / 1000,
                TryGetLongValue("audio-bitrate") / 1000);

            string inDetail = BuildAudioDetail(prettyCodec, inRate, inChannels, 0);

            string outDetail;
            int nowKbps;
            if (_bitstreamActive)
            {
                outDetail = "Bitstream " + prettyCodec;
                nowKbps = avgKbps;
            }
            else
            {
                outDetail = "PCM " + BuildAudioDetail(string.Empty, outRate, outChannels, outBits).TrimStart(' ', '•');
                nowKbps = (outRate > 0 && outChannels > 0 && outBits > 0)
                    ? (int)Math.Round(outRate * outChannels * outBits / 1000.0)
                    : 0;
            }

            return (
                string.IsNullOrWhiteSpace(inDetail) ? "n/d" : inDetail,
                string.IsNullOrWhiteSpace(outDetail) ? "n/d" : outDetail,
                nowKbps,
                avgKbps,
                _bitstreamActive);
        }

        public (int InWidth, int InHeight, int OutWidth, int OutHeight, string Codec, string Primaries, string Transfer, string Format, bool IsHdr, bool Upscaling) GetVideoOverlayDetails()
        {
            int inW = FirstPositiveLong(TryGetLongValue("video-params/w"), TryGetLongValue("width"));
            int inH = FirstPositiveLong(TryGetLongValue("video-params/h"), TryGetLongValue("height"));
            int outW = FirstPositiveLong(TryGetLongValue("osd-dimensions/w"), TryGetLongValue("dwidth"), inW);
            int outH = FirstPositiveLong(TryGetLongValue("osd-dimensions/h"), TryGetLongValue("dheight"), inH);

            string codec = FirstNonEmpty(TryGetString("video-codec"), TryGetString("video-codec-name"), "mpv");
            string primaries = FirstNonEmpty(TryGetString("video-params/primaries"), TryGetString("video-params/prim"), string.Empty);
            string transfer = FirstNonEmpty(TryGetString("video-params/gamma"), TryGetString("video-params/transfer"), TryGetString("video-params/trc"), string.Empty);
            string format = FirstNonEmpty(TryGetString("video-params/pixelformat"), TryGetString("video-format"), string.Empty);

            bool hdr = _fileIsHdr || LooksHdr(primaries, transfer, format);
            bool upscaling = inW > 0 && inH > 0 &&
                             (outW > (int)Math.Round(inW * 1.02) || outH > (int)Math.Round(inH * 1.02));

            return (inW, inH, outW, outH, codec, primaries, transfer, format, hdr, upscaling);
        }

        public (string text, DateTime when) GetLastVideoMTDump()
            => ("libmpv backend - DirectShow MediaType non disponibile", DateTime.MinValue);

        public (int width, int height, string subtype) GetNegotiatedVideoFormat()
        {
            int w = TryGetLong("video-params/w", out var vw) ? (int)vw : 0;
            int h = TryGetLong("video-params/h", out var vh) ? (int)vh : 0;

            if (w <= 0 && TryGetLong("width", out var ww)) w = (int)ww;
            if (h <= 0 && TryGetLong("height", out var hh)) h = (int)hh;

            string subtype =
                TryGetString("video-codec") ??
                TryGetString("video-format") ??
                "mpv";

            return (Math.Max(0, w), Math.Max(0, h), subtype);
        }

        public (int bytes, DateTime when) GetLastSnapshotInfo()
            => (_lastSnapshotBytes, _lastSnapshotAt);

        public List<DsStreamItem> EnumerateStreams()
        {
            var result = new List<DsStreamItem>();
            if (!TryGetLong("track-list/count", out var countLong))
                return result;

            int count = Math.Max(0, (int)Math.Min(countLong, 512));
            for (int i = 0; i < count; i++)
            {
                string type = TryGetString($"track-list/{i}/type") ?? "";
                bool isAudio = string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase);
                bool isSub = string.Equals(type, "sub", StringComparison.OrdinalIgnoreCase);
                if (!isAudio && !isSub)
                    continue;

                if (!TryGetLong($"track-list/{i}/id", out var idLong))
                    continue;

                string lang = TryGetString($"track-list/{i}/lang") ?? "";
                string title = TryGetString($"track-list/{i}/title") ?? "";
                string codec = TryGetString($"track-list/{i}/codec") ?? "";
                string selectedText = TryGetString($"track-list/{i}/selected") ?? "";
                bool selected = selectedText.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                                selectedText.Equals("true", StringComparison.OrdinalIgnoreCase);

                string label = BuildTrackLabel(idLong, isAudio, lang, title, codec);

                result.Add(new DsStreamItem
                {
                    GlobalIndex = i,
                    NativeIndex = (int)idLong,
                    LanguageKey = SubtitleNameNormalizer.TryDetectLanguageKey(lang),
                    IsForced = TryGetString($"track-list/{i}/forced") is "yes" or "true",
                    Group = isAudio ? 1 : 2,
                    IsAudio = isAudio,
                    IsExternal = TryGetString($"track-list/{i}/external") is "yes" or "true",
                    IsSubtitle = isSub,
                    Name = label,
                    Selected = selected
                });
            }

            return result;
        }

        private static string BuildTrackLabel(long id, bool audio, string lang, string title, string codec)
        {
            var parts = new List<string>
            {
                audio ? $"Audio #{id}" : $"Sub #{id}"
            };
            if (!string.IsNullOrWhiteSpace(lang)) parts.Add(lang);
            if (!string.IsNullOrWhiteSpace(title)) parts.Add(title);
            if (!string.IsNullOrWhiteSpace(codec)) parts.Add(codec);
            return string.Join(" - ", parts);
        }

        public bool EnableByGlobalIndex(int globalIndex)
        {
            var item = EnumerateStreams().FirstOrDefault(s => s.GlobalIndex == globalIndex);
            if (item == null)
                return false;

            string property = item.IsAudio ? "aid" : item.IsSubtitle ? "sid" : "";
            if (string.IsNullOrWhiteSpace(property))
                return false;

            bool ok = TrySetProperty(property, item.NativeIndex.ToString(CultureInfo.InvariantCulture));
            if (ok) _updateCb?.Invoke();
            return ok;
        }

        public bool DisableSubtitlesIfPossible()
        {
            bool ok = TrySetProperty("sid", "no");
            if (ok) _updateCb?.Invoke();
            return ok;
        }

        public bool TrySnapshot(out int byteCount)
        {
            byteCount = 0;
            if (_mpv == nint.Zero || !_hasVideo)
                return false;

            string file = Path.Combine(Path.GetTempPath(), "cinecore_mpv_snapshot_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                if (!TryCommand("screenshot-to-file", file, "video"))
                    return false;

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 600)
                {
                    if (File.Exists(file))
                        break;
                    Thread.Sleep(25);
                }

                if (!File.Exists(file))
                    return false;

                byteCount = (int)Math.Min(int.MaxValue, new FileInfo(file).Length);
                _lastSnapshotBytes = byteCount;
                _lastSnapshotAt = DateTime.Now;
                return byteCount > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
        }

        public Bitmap? GetPreviewFrame(double seconds, int maxW = 360)
        {
            try
            {
                if (!_hasVideo || _thumb == null)
                    return null;
                return _thumb.Get(seconds, maxW, realtime: true);
            }
            catch { return null; }
        }

        public void SetMadVrChroma(MadVrCategoryPreset preset) { }
        public void SetMadVrImageUpscale(MadVrCategoryPreset preset) { }
        public void SetMadVrImageDownscale(MadVrCategoryPreset preset) { }
        public void SetMadVrRefinement(MadVrCategoryPreset preset) { }
        public void SetMadVrFps(MadVrFpsChoice choice) { }
        public void SetMadVrHdrMode(MadVrHdrMode mode) { }

        public bool TrySendRendererKey(Keys keyData)
        {
            string? key = ToMpvKeyName(keyData);
            return !string.IsNullOrWhiteSpace(key) && TryCommand("keypress", key);
        }

        public bool TrySetMadVrExclusiveModeDisabled(bool disabled) => false;

        public string GetRendererDiagnosticSnapshot()
        {
            var fmt = GetNegotiatedVideoFormat();
            string version = TryGetString("mpv-version") ?? "libmpv";
            return $"{version}; video={fmt.width}x{fmt.height}/{fmt.subtype}; hdr={_fileIsHdr}; bitstream={_bitstreamActive}";
        }

        private static string? ToMpvKeyName(Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.None)
                return null;

            string? key = code switch
            {
                Keys.Space => "SPACE",
                Keys.Enter => "ENTER",
                Keys.Escape => "ESC",
                Keys.Left => "LEFT",
                Keys.Right => "RIGHT",
                Keys.Up => "UP",
                Keys.Down => "DOWN",
                Keys.PageUp => "PGUP",
                Keys.PageDown => "PGDWN",
                Keys.Back => "BS",
                Keys.Delete => "DEL",
                Keys.Home => "HOME",
                Keys.End => "END",
                _ when code >= Keys.A && code <= Keys.Z => ((char)('a' + ((int)code - (int)Keys.A))).ToString(),
                _ when code >= Keys.D0 && code <= Keys.D9 => ((char)('0' + ((int)code - (int)Keys.D0))).ToString(),
                _ when code >= Keys.F1 && code <= Keys.F24 => "F" + (1 + ((int)code - (int)Keys.F1)).ToString(CultureInfo.InvariantCulture),
                _ => null
            };

            if (key == null)
                return null;

            var mods = new List<string>(3);
            if ((keyData & Keys.Control) == Keys.Control) mods.Add("Ctrl");
            if ((keyData & Keys.Alt) == Keys.Alt) mods.Add("Alt");
            if ((keyData & Keys.Shift) == Keys.Shift) mods.Add("Shift");
            return mods.Count == 0 ? key : string.Join("+", mods) + "+" + key;
        }

        private void StartTimer()
        {
            StopTimer();
            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            _timer.Tick += (_, __) =>
            {
                try
                {
                    double dur = DurationSeconds;
                    double pos = PositionSeconds;
                    if (_ended && dur > 0)
                        pos = dur;
                    OnProgressSeconds?.Invoke(pos);
                }
                catch { }
            };
            _timer.Start();
        }

        private void StopTimer()
        {
            if (_timer == null) return;
            try { _timer.Stop(); _timer.Dispose(); } catch { }
            _timer = null;
        }

        private void StartEventPump()
        {
            StopEventPump();
            _eventCts = new CancellationTokenSource();
            var token = _eventCts.Token;
            _eventTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    nint ctx;
                    lock (_sync) ctx = _mpv;
                    if (ctx == nint.Zero)
                        return;

                    try
                    {
                        nint ptr = LibMpvNative.WaitEvent(ctx, 0.10);
                        if (ptr == nint.Zero)
                            continue;

                        var ev = Marshal.PtrToStructure<LibMpvNative.MpvEvent>(ptr);
                        if (ev.EventId == LibMpvNative.MpvEventId.None)
                            continue;

                        HandleMpvEvent(ev);

                        if (ev.EventId == LibMpvNative.MpvEventId.Shutdown)
                            return;
                    }
                    catch
                    {
                        return;
                    }
                }
            }, token);
        }

        private void StopEventPump()
        {
            var cts = Interlocked.Exchange(ref _eventCts, null);
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { _eventTask?.Wait(350); } catch { }
                try { cts.Dispose(); } catch { }
            }
            _eventTask = null;
        }

        private void HandleMpvEvent(LibMpvNative.MpvEvent ev)
        {
            switch (ev.EventId)
            {
                case LibMpvNative.MpvEventId.FileLoaded:
                    if (_initialInternalAudioOrdinal is int ordinal)
                    {
                        var track = EnumerateStreams().Where(x => x.IsAudio && !x.IsExternal).ElementAtOrDefault(ordinal);
                        if (track != null) EnableByGlobalIndex(track.GlobalIndex);
                        _initialInternalAudioOrdinal = null;
                    }
                    _durationCache = DurationSeconds;
                    SafeStatus(_bitstreamActive ? "MPV: Bitstream" : "MPV: Pronto");
                    _updateCb?.Invoke();
                    break;

                case LibMpvNative.MpvEventId.EndFile:
                    _ended = true;
                    if (_durationCache <= 0)
                        _durationCache = DurationSeconds;
                    try { OnProgressSeconds?.Invoke(_durationCache > 0 ? _durationCache : PositionSeconds); } catch { }
                    break;

                case LibMpvNative.MpvEventId.AudioReconfig:
                case LibMpvNative.MpvEventId.VideoReconfig:
                    _updateCb?.Invoke();
                    break;
            }
        }

        private int Command(params string[] args)
        {
            ThrowIfDisposed();
            if (_mpv == nint.Zero)
                return LibMpvNative.ErrorUninitialized;
            return LibMpvNative.Command(_mpv, args);
        }

        private bool TryCommand(params string[] args)
        {
            try
            {
                int err = Command(args);
                if (err < 0)
                    Dbg.Warn("MPV command failed: " + string.Join(" ", args) + " -> " + LibMpvNative.ErrorString(err));
                return err >= 0;
            }
            catch (Exception ex)
            {
                Dbg.Warn("MPV command EX: " + ex.Message);
                return false;
            }
        }

        private bool TrySetOption(string name, string value)
        {
            try
            {
                int err = LibMpvNative.SetOptionString(_mpv, name, value);
                if (err < 0)
                    Dbg.Warn($"MPV option '{name}'='{value}' failed: {LibMpvNative.ErrorString(err)}");
                return err >= 0;
            }
            catch (Exception ex)
            {
                Dbg.Warn($"MPV option '{name}' EX: {ex.Message}");
                return false;
            }
        }

        private bool TrySetProperty(string name, string value)
        {
            try
            {
                if (_mpv == nint.Zero)
                    return false;
                int err = LibMpvNative.SetPropertyString(_mpv, name, value);
                if (err < 0)
                    Dbg.Warn($"MPV property '{name}'='{value}' failed: {LibMpvNative.ErrorString(err)}");
                return err >= 0;
            }
            catch (Exception ex)
            {
                Dbg.Warn($"MPV property '{name}' EX: {ex.Message}");
                return false;
            }
        }

        private bool TryGetDouble(string name, out double value)
        {
            value = 0;
            try
            {
                if (_mpv == nint.Zero)
                    return false;
                int err = LibMpvNative.GetPropertyDouble(_mpv, name, LibMpvNative.MpvFormat.Double, out value);
                return err >= 0;
            }
            catch { return false; }
        }

        private bool TryGetLong(string name, out long value)
        {
            value = 0;
            try
            {
                if (_mpv == nint.Zero)
                    return false;
                int err = LibMpvNative.GetPropertyInt64(_mpv, name, LibMpvNative.MpvFormat.Int64, out value);
                if (err >= 0)
                    return true;

                string? text = TryGetString(name);
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }
            catch { return false; }
        }

        private string? TryGetString(string name)
        {
            try
            {
                if (_mpv == nint.Zero)
                    return null;
                nint ptr = LibMpvNative.GetPropertyString(_mpv, name);
                if (ptr == nint.Zero)
                    return null;
                try { return Marshal.PtrToStringUTF8(ptr); }
                finally { LibMpvNative.Free(ptr); }
            }
            catch { return null; }
        }

        private long TryGetLongValue(string name)
        {
            return TryGetLong(name, out var value) ? value : 0;
        }

        private string? GetSelectedTrackString(string type, string field)
        {
            if (!TryGetLong("track-list/count", out var countLong))
                return null;

            int count = Math.Max(0, (int)Math.Min(countLong, 512));
            for (int i = 0; i < count; i++)
            {
                string trackType = TryGetString($"track-list/{i}/type") ?? string.Empty;
                if (!string.Equals(trackType, type, StringComparison.OrdinalIgnoreCase))
                    continue;

                string selectedText = TryGetString($"track-list/{i}/selected") ?? string.Empty;
                bool selected = selectedText.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                                selectedText.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (!selected)
                    continue;

                return TryGetString($"track-list/{i}/{field}");
            }

            return null;
        }

        private long GetSelectedTrackLong(string type, string field)
        {
            string? text = GetSelectedTrackString(type, field);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return value;
            return 0;
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return string.Empty;
        }

        private static int FirstPositiveLong(params long[] values)
        {
            foreach (var value in values)
            {
                if (value > 0)
                    return (int)Math.Min(int.MaxValue, value);
            }
            return 0;
        }

        private static string CodecNameFromSource(AVCodecID id) => id switch
        {
            AVCodecID.AV_CODEC_ID_TRUEHD => "truehd",
            AVCodecID.AV_CODEC_ID_EAC3 => "eac3",
            AVCodecID.AV_CODEC_ID_AC3 => "ac3",
            AVCodecID.AV_CODEC_ID_DTS => "dts",
            AVCodecID.AV_CODEC_ID_FLAC => "flac",
            AVCodecID.AV_CODEC_ID_AAC => "aac",
            AVCodecID.AV_CODEC_ID_OPUS => "opus",
            AVCodecID.AV_CODEC_ID_MP3 => "mp3",
            _ => string.Empty
        };

        private static string PrettyAudioCodec(string codec, string? title)
        {
            string hay = ((codec ?? string.Empty) + " " + (title ?? string.Empty)).ToUpperInvariant();
            if (hay.Contains("TRUEHD")) return hay.Contains("ATMOS") ? "Dolby TrueHD Atmos" : "Dolby TrueHD";
            if (hay.Contains("EAC3") || hay.Contains("E-AC3") || hay.Contains("DDP") || hay.Contains("DD+"))
                return hay.Contains("ATMOS") ? "Dolby Digital Plus Atmos" : "Dolby Digital Plus";
            if (hay.Contains("AC3")) return "Dolby Digital";
            if (hay.Contains("DTS:X") || hay.Contains("DTS X")) return "DTS:X";
            if (hay.Contains("DTSHD") || hay.Contains("DTS-HD") || hay.Contains("DTS_HD")) return "DTS-HD";
            if (hay.Contains("DTS")) return "DTS";
            if (hay.Contains("FLAC")) return "FLAC";
            if (hay.Contains("AAC")) return "AAC";
            if (hay.Contains("OPUS")) return "Opus";
            if (hay.Contains("MP3")) return "MP3";
            if (hay.Contains("PCM")) return "PCM";
            return string.IsNullOrWhiteSpace(codec) ? "Audio" : codec.Trim();
        }

        private static int GuessAudioBitsFromMpvFormat(string? format, AVCodecID codec)
        {
            string f = (format ?? string.Empty).ToLowerInvariant();
            if (f.Contains("u8")) return 8;
            if (f.Contains("s16")) return 16;
            if (f.Contains("s24")) return 24;
            if (f.Contains("s32") || f.Contains("float") || f.Contains("f32")) return 32;

            int bits = ffmpeg.av_get_bits_per_sample(codec);
            return bits > 0 ? bits : 0;
        }

        private static string BuildAudioDetail(string codec, int rate, int channels, int bits)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(codec)) parts.Add(codec);
            if (rate > 0) parts.Add((rate / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " kHz");
            if (bits > 0) parts.Add(bits + "-bit");
            if (channels > 0) parts.Add(PrettyChannels(channels));
            return string.Join(" • ", parts);
        }

        private static string PrettyChannels(int channels) => channels switch
        {
            1 => "1.0",
            2 => "2.0",
            3 => "2.1",
            4 => "4.0",
            5 => "4.1",
            6 => "5.1",
            7 => "6.1",
            8 => "7.1",
            _ => channels + "ch"
        };

        private static bool LooksHdr(string primaries, string transfer, string format)
        {
            string hay = (primaries + " " + transfer + " " + format).ToUpperInvariant();
            return hay.Contains("BT.2020") || hay.Contains("BT2020") ||
                   hay.Contains("PQ") || hay.Contains("SMPTE2084") ||
                   hay.Contains("HLG") || hay.Contains("ARIB") ||
                   hay.Contains("P010") || hay.Contains("P016");
        }

        private void SafeStatus(string s)
        {
            try { OnStatus?.Invoke(s); }
            catch (Exception ex) { Dbg.Warn("MPV OnStatus handler EX: " + ex.Message); }
        }

        private void SaveResumePointIfNeeded()
        {
            try
            {
                if (!_opened || !_playbackStarted || string.IsNullOrWhiteSpace(_mediaPath))
                    return;
                PlaybackResumeStore.SaveOrClear(_mediaPath!, PositionSeconds, DurationSeconds);
            }
            catch (Exception ex)
            {
                Dbg.Warn("MPV SaveResumePointIfNeeded EX: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            StopTimer();
            SaveResumePointIfNeeded();
            StopEventPump();

            try { _thumb.Close(); } catch { }

            nint ctx;
            lock (_sync)
            {
                ctx = _mpv;
                _mpv = nint.Zero;
            }

            if (ctx != nint.Zero)
            {
                try { LibMpvNative.TerminateDestroy(ctx); } catch { }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LibMpvPlaybackEngine));
        }
    }

    internal static class LibMpvNative
    {
        private const string DllImportName = "libmpv-2.dll";
        internal const int ErrorUninitialized = -3;

        private static readonly object LoadLock = new();
        private static readonly string[] LibraryFileNames = { "libmpv-2.dll", "mpv-2.dll" };
        private static nint _loadedHandle;
        private static string? _loadError;
        private static MpvRuntimeChoice? _loadedRuntime;
        private static string? _loadedRuntimeLabel;
        private static string? _loadedPath;

        static LibMpvNative()
        {
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(LibMpvNative).Assembly, ResolveDllImport);
            }
            catch
            {
                // If another resolver is already registered, the explicit pre-load still gives
                // the default resolver a fair chance when the selected DLL is on the search path.
            }
        }

        internal static string LoadedRuntimeLabel => _loadedRuntimeLabel ?? "Standard";

        internal static bool TryEnsureLoaded(MpvRuntimeChoice requestedRuntime, out string error)
        {
            lock (LoadLock)
            {
                if (_loadedHandle != nint.Zero)
                {
                    if (IsLoadedRuntimeCompatible(requestedRuntime))
                    {
                        error = "";
                        return true;
                    }

                    error =
                        $"libmpv e' gia' caricata come {LoadedRuntimeLabel}. " +
                        $"Per passare a {DescribeRuntime(requestedRuntime)} chiudi e riapri Cinecore Player.";
                    return false;
                }

                if (requestedRuntime == MpvRuntimeChoice.X64V3 && !IsX64V3Supported())
                {
                    error = "La build libmpv x86_64-v3 e' selezionata, ma questa CPU non espone tutte le istruzioni richieste. " +
                            "Scegli Standard nelle impostazioni.";
                    return false;
                }

                string baseDir = AppContext.BaseDirectory;
                var candidates = BuildCandidates(baseDir, requestedRuntime).ToArray();

                foreach (var candidate in candidates)
                {
                    try
                    {
                        if (File.Exists(candidate.Path) && NativeLibrary.TryLoad(candidate.Path, out _loadedHandle))
                        {
                            _loadedRuntime = candidate.Runtime;
                            _loadedRuntimeLabel = candidate.Label;
                            _loadedPath = candidate.Path;
                            error = "";
                            Dbg.Log($"MPV native library loaded ({candidate.Label}): {candidate.Path}", Dbg.LogLevel.Info);
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _loadError = ex.Message;
                    }
                }

                if (requestedRuntime != MpvRuntimeChoice.X64V3)
                {
                    foreach (var fileName in LibraryFileNames)
                    {
                        try
                        {
                            if (NativeLibrary.TryLoad(fileName, out _loadedHandle))
                            {
                                _loadedRuntime = requestedRuntime == MpvRuntimeChoice.X64 ? MpvRuntimeChoice.X64 : MpvRuntimeChoice.Auto;
                                _loadedRuntimeLabel = "system";
                                _loadedPath = fileName;
                                error = "";
                                Dbg.Log("MPV native library loaded from system search path: " + fileName, Dbg.LogLevel.Info);
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            _loadError = ex.Message;
                        }
                    }
                }

                error = "libmpv non disponibile: copia libmpv-2.dll in third-parties\\mpv\\x86_64 oppure third-parties\\mpv\\x86_64-v3. " +
                        "Se la DLL e' gia' presente, verifica che sia x64 e che le dipendenze native di mpv siano nella stessa cartella." +
                        (string.IsNullOrWhiteSpace(_loadError) ? "" : " Dettagli: " + _loadError);
                return false;
            }
        }

        private static nint ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!IsMpvLibraryName(libraryName))
                return nint.Zero;

            lock (LoadLock)
                return _loadedHandle;
        }

        private static bool IsMpvLibraryName(string? libraryName)
        {
            if (string.IsNullOrWhiteSpace(libraryName))
                return false;

            return libraryName.Equals("libmpv-2.dll", StringComparison.OrdinalIgnoreCase) ||
                   libraryName.Equals("mpv-2.dll", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLoadedRuntimeCompatible(MpvRuntimeChoice requestedRuntime)
        {
            if (requestedRuntime == MpvRuntimeChoice.Auto)
                return true;

            return _loadedRuntime == requestedRuntime;
        }

        private static IEnumerable<MpvCandidate> BuildCandidates(string baseDir, MpvRuntimeChoice requestedRuntime)
        {
            foreach (var runtime in RuntimeProbeOrder(requestedRuntime))
            {
                foreach (var relativeDir in RuntimeDirectories(runtime))
                {
                    foreach (var fileName in LibraryFileNames)
                    {
                        yield return new MpvCandidate(
                            Path.Combine(baseDir, "third-parties", "mpv", relativeDir, fileName),
                            runtime,
                            DescribeRuntime(runtime));
                        yield return new MpvCandidate(
                            Path.Combine(baseDir, "mpv", relativeDir, fileName),
                            runtime,
                            DescribeRuntime(runtime));
                    }
                }
            }

            if (requestedRuntime == MpvRuntimeChoice.X64V3)
                yield break;

            foreach (var fileName in LibraryFileNames)
            {
                yield return new MpvCandidate(Path.Combine(baseDir, fileName), MpvRuntimeChoice.X64, "Standard");
                yield return new MpvCandidate(Path.Combine(baseDir, "mpv", "win-x64", fileName), MpvRuntimeChoice.X64, "Standard");
                yield return new MpvCandidate(Path.Combine(baseDir, "libmpv", fileName), MpvRuntimeChoice.X64, "Standard");
                yield return new MpvCandidate(Path.Combine(baseDir, "libmpv", "win-x64", fileName), MpvRuntimeChoice.X64, "Standard");
            }
        }

        private static IEnumerable<MpvRuntimeChoice> RuntimeProbeOrder(MpvRuntimeChoice requestedRuntime)
        {
            if (requestedRuntime == MpvRuntimeChoice.X64)
            {
                yield return MpvRuntimeChoice.X64;
                yield break;
            }

            if (requestedRuntime == MpvRuntimeChoice.X64V3)
            {
                yield return MpvRuntimeChoice.X64V3;
                yield break;
            }

            yield return MpvRuntimeChoice.X64;

            if (IsX64V3Supported())
                yield return MpvRuntimeChoice.X64V3;
        }

        private static IEnumerable<string> RuntimeDirectories(MpvRuntimeChoice runtime)
        {
            if (runtime == MpvRuntimeChoice.X64V3)
            {
                yield return "x86_64-v3";
                yield return "win-x64-v3";
                yield break;
            }

            yield return "x86_64";
            yield return "win-x64";
        }

        private static string DescribeRuntime(MpvRuntimeChoice runtime) => runtime switch
        {
            MpvRuntimeChoice.X64V3 => "V3 (experimental)",
            MpvRuntimeChoice.X64 => "Standard",
            _ => "Standard"
        };

        private static bool IsX64V3Supported()
        {
            try
            {
                return Environment.Is64BitProcess &&
                       Sse41.IsSupported &&
                       Sse42.IsSupported &&
                       Avx.IsSupported &&
                       Avx2.IsSupported &&
                       Fma.IsSupported &&
                       Bmi1.IsSupported &&
                       Bmi2.IsSupported &&
                       Lzcnt.IsSupported &&
                       Popcnt.IsSupported;
            }
            catch
            {
                return false;
            }
        }

        private readonly struct MpvCandidate
        {
            public MpvCandidate(string path, MpvRuntimeChoice runtime, string label)
            {
                Path = path;
                Runtime = runtime;
                Label = label;
            }

            public string Path { get; }
            public MpvRuntimeChoice Runtime { get; }
            public string Label { get; }
        }

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_create")]
        internal static extern nint Create();

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_initialize")]
        internal static extern int Initialize(nint ctx);

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_terminate_destroy")]
        internal static extern void TerminateDestroy(nint ctx);

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_option_string")]
        internal static extern int SetOptionString(
            nint ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property_string")]
        internal static extern int SetPropertyString(
            nint ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
        internal static extern int GetPropertyDouble(
            nint ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            MpvFormat format,
            out double data);

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
        internal static extern int GetPropertyInt64(
            nint ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            MpvFormat format,
            out long data);

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property_string")]
        internal static extern nint GetPropertyString(
            nint ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_wait_event")]
        internal static extern nint WaitEvent(nint ctx, double timeout);

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_free")]
        internal static extern void Free(nint data);

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_error_string")]
        private static extern nint ErrorStringNative(int error);

        [DllImport(DllImportName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_command")]
        private static extern int CommandNative(nint ctx, nint args);

        internal static int Command(nint ctx, IReadOnlyList<string> args)
        {
            if (ctx == nint.Zero)
                return ErrorUninitialized;

            nint argv = nint.Zero;
            var strings = new nint[args.Count + 1];
            try
            {
                for (int i = 0; i < args.Count; i++)
                    strings[i] = Marshal.StringToCoTaskMemUTF8(args[i]);

                argv = Marshal.AllocHGlobal(IntPtr.Size * strings.Length);
                for (int i = 0; i < strings.Length; i++)
                    Marshal.WriteIntPtr(argv, i * IntPtr.Size, strings[i]);

                return CommandNative(ctx, argv);
            }
            finally
            {
                if (argv != nint.Zero)
                    Marshal.FreeHGlobal(argv);
                foreach (var ptr in strings)
                {
                    if (ptr != nint.Zero)
                        Marshal.FreeCoTaskMem(ptr);
                }
            }
        }

        internal static string ErrorString(int error)
        {
            try
            {
                nint ptr = ErrorStringNative(error);
                return ptr == nint.Zero ? "unknown" : (Marshal.PtrToStringUTF8(ptr) ?? "unknown");
            }
            catch { return "unknown"; }
        }

        internal static void ThrowIfError(int error, string operation)
        {
            if (error < 0)
                throw new ApplicationException($"{operation} fallito: {ErrorString(error)} ({error}).");
        }

        internal enum MpvFormat
        {
            None = 0,
            String = 1,
            OsdString = 2,
            Flag = 3,
            Int64 = 4,
            Double = 5,
            Node = 6,
            NodeArray = 7,
            NodeMap = 8,
            ByteArray = 9
        }

        internal enum MpvEventId
        {
            None = 0,
            Shutdown = 1,
            LogMessage = 2,
            GetPropertyReply = 3,
            SetPropertyReply = 4,
            CommandReply = 5,
            StartFile = 6,
            EndFile = 7,
            FileLoaded = 8,
            TracksChanged = 9,
            TrackSwitched = 10,
            Idle = 11,
            Pause = 12,
            Unpause = 13,
            Tick = 14,
            ScriptInputDispatch = 15,
            ClientMessage = 16,
            VideoReconfig = 17,
            AudioReconfig = 18,
            Seek = 20,
            PlaybackRestart = 21,
            PropertyChange = 22,
            ChapterChange = 23,
            QueueOverflow = 24,
            Hook = 25
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MpvEvent
        {
            public MpvEventId EventId;
            public int Error;
            public ulong ReplyUserData;
            public nint Data;
        }
    }
}
