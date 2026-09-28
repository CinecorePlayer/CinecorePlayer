#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private void LoadExtrasConfig()
        {
            try
            {
                if (!File.Exists(ExtrasConfigPath)) return;
                var json = File.ReadAllText(ExtrasConfigPath);
                var cfg = JsonSerializer.Deserialize<ExtrasConfig>(json);
                if (cfg == null) return;

                _pausePlaceholderEnabled = cfg.PausePlaceholderEnabled;
                _pausePlaceholderPath = ResolvePausePlaceholderPath(cfg.PausePlaceholderFile);
                _pausePlaceholderUseTmdbBackdrop = cfg.PausePlaceholderUseTmdbBackdrop;
                _preRollEnabled = cfg.PreRollEnabled;
                _preRollDemoPath = ResolveDemoPath(cfg.PreRollDemoFile);
                _wledEnabled = cfg.WledEnabled;
                _wledBaseUrl = NormalizeWledBaseUrl(cfg.WledBaseUrl) ?? _wledBaseUrl;
                _cinemaModeEnabled = cfg.CinemaModeEnabled || (_wledEnabled && _pausePlaceholderEnabled && _preRollEnabled);
                _uiLanguage = string.Equals(cfg.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
                bool hasExtendedTheme = cfg.UiAccentSoftArgb.HasValue || cfg.UiSelectionArgb.HasValue || cfg.UiBorderAccentArgb.HasValue ||
                                        cfg.UiPanelArgb.HasValue || cfg.UiCardArgb.HasValue || cfg.UiNavArgb.HasValue;
                if (cfg.UiAccentArgb.HasValue && !hasExtendedTheme)
                    ApplyUiAccentColor(Color.FromArgb(cfg.UiAccentArgb.Value), save: false);
                else if (cfg.UiAccentArgb.HasValue || hasExtendedTheme)
                {
                    if (cfg.UiAccentArgb.HasValue) _uiAccentColor = Color.FromArgb(cfg.UiAccentArgb.Value);
                    if (cfg.UiAccentSoftArgb.HasValue) _uiAccentSoftColor = Color.FromArgb(cfg.UiAccentSoftArgb.Value);
                    if (cfg.UiSelectionArgb.HasValue) _uiSelectionColor = Color.FromArgb(cfg.UiSelectionArgb.Value);
                    if (cfg.UiBorderAccentArgb.HasValue) _uiBorderAccentColor = Color.FromArgb(cfg.UiBorderAccentArgb.Value);
                    if (cfg.UiPanelArgb.HasValue) _uiPanelColor = Color.FromArgb(cfg.UiPanelArgb.Value);
                    if (cfg.UiCardArgb.HasValue) _uiCardColor = Color.FromArgb(cfg.UiCardArgb.Value);
                    if (cfg.UiNavArgb.HasValue) _uiNavColor = Color.FromArgb(cfg.UiNavArgb.Value);

                    bool legacyDarkDefaults =
                        _uiAccentColor.ToArgb() == Color.FromArgb(0, 132, 255).ToArgb() &&
                        _uiAccentSoftColor.ToArgb() == Color.FromArgb(0, 84, 190).ToArgb() &&
                        _uiPanelColor.ToArgb() == Color.FromArgb(3, 10, 18).ToArgb() &&
                        _uiCardColor.ToArgb() == Color.FromArgb(7, 18, 31).ToArgb() &&
                        _uiNavColor.ToArgb() == Color.FromArgb(5, 15, 26).ToArgb();
                    bool previousDarkDefaults =
                        _uiAccentColor.ToArgb() == Color.FromArgb(69, 132, 232).ToArgb() &&
                        _uiAccentSoftColor.ToArgb() == Color.FromArgb(34, 76, 142).ToArgb() &&
                        _uiPanelColor.ToArgb() == Color.FromArgb(10, 15, 21).ToArgb() &&
                        _uiCardColor.ToArgb() == Color.FromArgb(16, 23, 31).ToArgb() &&
                        _uiNavColor.ToArgb() == Color.FromArgb(12, 18, 25).ToArgb();
                    bool previousLightDefaults =
                        _uiAccentColor.ToArgb() == Color.FromArgb(38, 102, 184).ToArgb() &&
                        _uiPanelColor.ToArgb() == Color.FromArgb(171, 179, 187).ToArgb() &&
                        _uiCardColor.ToArgb() == Color.FromArgb(192, 199, 206).ToArgb() &&
                        _uiNavColor.ToArgb() == Color.FromArgb(153, 163, 173).ToArgb();
                    bool lowContrastLightDefaults =
                        _uiAccentColor.ToArgb() == Color.FromArgb(28, 96, 184).ToArgb() &&
                        _uiPanelColor.ToArgb() == Color.FromArgb(88, 98, 110).ToArgb() &&
                        _uiCardColor.ToArgb() == Color.FromArgb(112, 122, 133).ToArgb() &&
                        _uiNavColor.ToArgb() == Color.FromArgb(76, 86, 98).ToArgb();
                    bool currentLightDefaults =
                        _uiAccentColor.ToArgb() == Color.FromArgb(22, 87, 174).ToArgb() &&
                        _uiPanelColor.ToArgb() == Color.FromArgb(146, 154, 164).ToArgb() &&
                        _uiCardColor.ToArgb() == Color.FromArgb(166, 174, 183).ToArgb() &&
                        _uiNavColor.ToArgb() == Color.FromArgb(130, 139, 150).ToArgb();
                    if (legacyDarkDefaults || previousDarkDefaults)
                    {
                        _uiAccentColor = Theme.DefaultAccent;
                        _uiAccentSoftColor = Theme.DefaultAccentSoft;
                        _uiSelectionColor = Theme.DefaultSelection;
                        _uiBorderAccentColor = Theme.DefaultBorderAccent;
                        _uiPanelColor = Theme.DefaultPanel;
                        _uiCardColor = Theme.DefaultCard;
                        _uiNavColor = Theme.DefaultNav;
                    }
                    else if (previousLightDefaults || lowContrastLightDefaults || currentLightDefaults)
                    {
                        _uiAccentColor = Color.FromArgb(28, 95, 186);
                        _uiAccentSoftColor = Color.FromArgb(72, 118, 178);
                        _uiSelectionColor = Color.FromArgb(59, 119, 198);
                        _uiBorderAccentColor = Color.FromArgb(156, 168, 181);
                        _uiPanelColor = Color.FromArgb(226, 230, 235);
                        _uiCardColor = Color.FromArgb(246, 247, 249);
                        _uiNavColor = Color.FromArgb(211, 217, 224);
                    }

                    ApplyUiThemePalette(save: false, statusText: string.Empty);
                }
                _manualRendererChoice = ParseRendererChoice(cfg.PreferredRenderer);
                _videoUpscalingBackend = NormalizeUpscalingBackend(cfg.VideoUpscalingBackend);
                _mpcvrSuperResolutionMode = Math.Clamp(cfg.MpcvrSuperResolutionMode <= 0 ? 3 : cfg.MpcvrSuperResolutionMode, 1, 4);
                _madVrUpscalePreset = Enum.IsDefined(typeof(MadVrCategoryPreset), cfg.MadVrUpscalePreset)
                    ? (MadVrCategoryPreset)cfg.MadVrUpscalePreset
                    : MadVrCategoryPreset.RendererDefault;
                _enableUpscaling = !string.Equals(_videoUpscalingBackend, "off", StringComparison.OrdinalIgnoreCase);
                _stereoAutoEnabled = cfg.StereoAutoEnabled;
                _mpvRuntimeV3Manual = cfg.MpvRuntimeV3Manual && IsMpvRuntimeV3Value(cfg.MpvRuntime);
                _mpvRuntimeChoice = _mpvRuntimeV3Manual ? MpvRuntimeChoice.X64V3 : MpvRuntimeChoice.X64;
                _mpvSettings = new MpvPlaybackSettings
                {
                    Profile = NormalizeMpvOption(cfg.MpvProfile, "default"),
                    Hwdec = NormalizeMpvOption(cfg.MpvHwdec, "auto-safe"),
                    VideoOutput = NormalizeMpvOption(cfg.MpvVideoOutput, "gpu-next"),
                    GpuApi = NormalizeMpvOption(cfg.MpvGpuApi, "auto"),
                    GpuContext = NormalizeMpvOption(cfg.MpvGpuContext, "auto"),
                    VideoSync = NormalizeMpvOption(cfg.MpvVideoSync, "audio"),
                    Interpolation = cfg.MpvInterpolation,
                    ToneMapping = NormalizeMpvOption(cfg.MpvToneMapping, "auto"),
                    HdrComputePeak = cfg.MpvHdrComputePeak,
                    TargetPrim = NormalizeMpvOption(cfg.MpvTargetPrim, "auto"),
                    TargetTrc = NormalizeMpvOption(cfg.MpvTargetTrc, "auto"),
                    TargetPeak = cfg.MpvTargetPeak,
                    GamutMappingMode = NormalizeMpvOption(cfg.MpvGamutMappingMode, "auto"),
                    TargetColorspaceHint = cfg.MpvTargetColorspaceHint,
                    IccProfileAuto = cfg.MpvIccProfileAuto,
                    BlendSubtitles = NormalizeMpvOption(cfg.MpvBlendSubtitles, "auto"),
                    FboFormat = NormalizeMpvOption(cfg.MpvFboFormat, "auto"),
                    VideoOutputLevels = NormalizeMpvOption(cfg.MpvVideoOutputLevels, "auto"),
                    Scale = NormalizeMpvOption(cfg.MpvScale, "ewa_lanczossharp"),
                    CScale = NormalizeMpvOption(cfg.MpvCScale, "ewa_lanczossoft"),
                    DScale = NormalizeMpvOption(cfg.MpvDScale, "mitchell"),
                    TScale = NormalizeMpvOption(cfg.MpvTScale, "oversample"),
                    ScaleAntiring = cfg.MpvScaleAntiring,
                    CScaleAntiring = cfg.MpvCScaleAntiring,
                    DScaleAntiring = cfg.MpvDScaleAntiring,
                    CorrectDownscaling = cfg.MpvCorrectDownscaling,
                    LinearDownscaling = cfg.MpvLinearDownscaling,
                    SigmoidUpscaling = cfg.MpvSigmoidUpscaling,
                    ToneMappingParam = cfg.MpvToneMappingParam,
                    HdrContrastRecovery = cfg.MpvHdrContrastRecovery,
                    HdrContrastSmoothness = cfg.MpvHdrContrastSmoothness,
                    Deband = cfg.MpvDeband,
                    DebandIterations = cfg.MpvDebandIterations,
                    DebandThreshold = cfg.MpvDebandThreshold,
                    DebandRange = cfg.MpvDebandRange,
                    DebandGrain = cfg.MpvDebandGrain,
                    Dither = NormalizeMpvOption(cfg.MpvDither, "auto"),
                    DitherDepth = NormalizeMpvOption(cfg.MpvDitherDepth, "auto"),
                    DitherSizeFruit = cfg.MpvDitherSizeFruit <= 0 ? 6 : cfg.MpvDitherSizeFruit,
                    ErrorDiffusion = NormalizeMpvOption(cfg.MpvErrorDiffusion, "sierra-lite"),
                    TemporalDither = cfg.MpvTemporalDither,
                    Deinterlace = cfg.MpvDeinterlace,
                    InterpolationThreshold = cfg.MpvInterpolationThreshold,
                    Cache = NormalizeMpvOption(cfg.MpvCache, "auto"),
                    DemuxerReadaheadSeconds = cfg.MpvDemuxerReadaheadSeconds,
                    DemuxerMaxBytesMb = cfg.MpvDemuxerMaxBytesMb,
                    VideoThreads = cfg.MpvVideoThreads,
                    VideoLavcDr = cfg.MpvVideoLavcDr,
                    SubAuto = NormalizeMpvOption(cfg.MpvSubAuto, "fuzzy"),
                    SubAssOverride = NormalizeMpvOption(cfg.MpvSubAssOverride, "no"),
                    SubScale = cfg.MpvSubScale <= 0 ? 1.0 : cfg.MpvSubScale,
                    SubFontSize = cfg.MpvSubFontSize <= 0 ? 55 : cfg.MpvSubFontSize,
                    SubBorderSize = cfg.MpvSubBorderSize <= 0 ? 3.0 : cfg.MpvSubBorderSize,
                    SubShadowOffset = cfg.MpvSubShadowOffset,
                    AudioExclusive = cfg.MpvAudioExclusive,
                    AudioChannels = NormalizeMpvOption(cfg.MpvAudioChannels, "auto"),
                    AudioNormalizeDownmix = cfg.MpvAudioNormalizeDownmix,
                    VolumeMax = cfg.MpvVolumeMax <= 0 ? 100 : cfg.MpvVolumeMax,
                    GaplessAudio = NormalizeMpvOption(cfg.MpvGaplessAudio, "weak"),
                    ExtraOptions = cfg.MpvExtraOptions
                };
            }
            catch
            {
                // ignore
            }
        }

        private static string NormalizeMpvOption(string? value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static VRChoice? ParseRendererChoice(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Enum.TryParse<VRChoice>(value, ignoreCase: true, out var parsed)
                ? parsed
                : null;
        }

        private static MpvRuntimeChoice ParseMpvRuntimeChoice(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                return MpvRuntimeChoice.X64;
            }

            if (string.Equals(value, "x86_64", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "X64", StringComparison.OrdinalIgnoreCase))
            {
                return MpvRuntimeChoice.X64;
            }

            if (string.Equals(value, "x86_64-v3", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "X64V3", StringComparison.OrdinalIgnoreCase))
            {
                return MpvRuntimeChoice.X64V3;
            }

            return Enum.TryParse<MpvRuntimeChoice>(value, ignoreCase: true, out var parsed)
                ? parsed
                : MpvRuntimeChoice.X64;
        }

        private static bool IsMpvRuntimeV3Value(string? value)
        {
            return string.Equals(value, "x86_64-v3", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "X64V3", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, nameof(MpvRuntimeChoice.X64V3), StringComparison.OrdinalIgnoreCase);
        }

        private MpvRuntimeChoice GetEffectiveMpvRuntimeChoice()
            => (_mpvRuntimeV3Manual && _mpvRuntimeChoice == MpvRuntimeChoice.X64V3)
                ? MpvRuntimeChoice.X64V3
                : MpvRuntimeChoice.X64;

        private void SaveExtrasConfig()
        {
            string? tempFile = null;
            try
            {
                var dir = Path.GetDirectoryName(ExtrasConfigPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                var cfg = new ExtrasConfig
                {
                    PausePlaceholderEnabled = _pausePlaceholderEnabled,
                    PausePlaceholderFile = _pausePlaceholderPath != null ? Path.GetFileName(_pausePlaceholderPath) : null,
                    PausePlaceholderUseTmdbBackdrop = _pausePlaceholderUseTmdbBackdrop,
                    PreRollEnabled = _preRollEnabled,
                    PreRollDemoFile = _preRollDemoPath != null ? Path.GetFileName(_preRollDemoPath) : null,
                    WledEnabled = _wledEnabled,
                    WledBaseUrl = _wledBaseUrl,
                    CinemaModeEnabled = _cinemaModeEnabled,
                    NetflixModeEnabled = false,
                    Language = _uiLanguage,
                    UiAccentArgb = _uiAccentColor.ToArgb(),
                    UiAccentSoftArgb = _uiAccentSoftColor.ToArgb(),
                    UiSelectionArgb = _uiSelectionColor.ToArgb(),
                    UiBorderAccentArgb = _uiBorderAccentColor.ToArgb(),
                    UiPanelArgb = _uiPanelColor.ToArgb(),
                    UiCardArgb = _uiCardColor.ToArgb(),
                    UiNavArgb = _uiNavColor.ToArgb(),
                    PreferredRenderer = (_hasSavedRendererFor3D ? _savedRendererFor3D : _manualRendererChoice)?.ToString() ?? "Auto",
                    VideoUpscalingBackend = _videoUpscalingBackend,
                    MpcvrSuperResolutionMode = _mpcvrSuperResolutionMode,
                    MadVrUpscalePreset = (int)_madVrUpscalePreset,
                    StereoAutoEnabled = _stereoAutoEnabled,
                    MpvRuntime = _mpvRuntimeChoice.ToString(),
                    MpvRuntimeV3Manual = _mpvRuntimeV3Manual && _mpvRuntimeChoice == MpvRuntimeChoice.X64V3,
                    MpvProfile = _mpvSettings.Profile,
                    MpvHwdec = _mpvSettings.Hwdec,
                    MpvVideoOutput = _mpvSettings.VideoOutput,
                    MpvGpuApi = _mpvSettings.GpuApi,
                    MpvGpuContext = _mpvSettings.GpuContext,
                    MpvVideoSync = _mpvSettings.VideoSync,
                    MpvInterpolation = _mpvSettings.Interpolation,
                    MpvToneMapping = _mpvSettings.ToneMapping,
                    MpvHdrComputePeak = _mpvSettings.HdrComputePeak,
                    MpvTargetPrim = _mpvSettings.TargetPrim,
                    MpvTargetTrc = _mpvSettings.TargetTrc,
                    MpvTargetPeak = _mpvSettings.TargetPeak,
                    MpvGamutMappingMode = _mpvSettings.GamutMappingMode,
                    MpvTargetColorspaceHint = _mpvSettings.TargetColorspaceHint,
                    MpvIccProfileAuto = _mpvSettings.IccProfileAuto,
                    MpvBlendSubtitles = _mpvSettings.BlendSubtitles,
                    MpvFboFormat = _mpvSettings.FboFormat,
                    MpvVideoOutputLevels = _mpvSettings.VideoOutputLevels,
                    MpvScale = _mpvSettings.Scale,
                    MpvCScale = _mpvSettings.CScale,
                    MpvDScale = _mpvSettings.DScale,
                    MpvTScale = _mpvSettings.TScale,
                    MpvScaleAntiring = _mpvSettings.ScaleAntiring,
                    MpvCScaleAntiring = _mpvSettings.CScaleAntiring,
                    MpvDScaleAntiring = _mpvSettings.DScaleAntiring,
                    MpvCorrectDownscaling = _mpvSettings.CorrectDownscaling,
                    MpvLinearDownscaling = _mpvSettings.LinearDownscaling,
                    MpvSigmoidUpscaling = _mpvSettings.SigmoidUpscaling,
                    MpvToneMappingParam = _mpvSettings.ToneMappingParam,
                    MpvHdrContrastRecovery = _mpvSettings.HdrContrastRecovery,
                    MpvHdrContrastSmoothness = _mpvSettings.HdrContrastSmoothness,
                    MpvDeband = _mpvSettings.Deband,
                    MpvDebandIterations = _mpvSettings.DebandIterations,
                    MpvDebandThreshold = _mpvSettings.DebandThreshold,
                    MpvDebandRange = _mpvSettings.DebandRange,
                    MpvDebandGrain = _mpvSettings.DebandGrain,
                    MpvDither = _mpvSettings.Dither,
                    MpvDitherDepth = _mpvSettings.DitherDepth,
                    MpvDitherSizeFruit = _mpvSettings.DitherSizeFruit,
                    MpvErrorDiffusion = _mpvSettings.ErrorDiffusion,
                    MpvTemporalDither = _mpvSettings.TemporalDither,
                    MpvDeinterlace = _mpvSettings.Deinterlace,
                    MpvInterpolationThreshold = _mpvSettings.InterpolationThreshold,
                    MpvCache = _mpvSettings.Cache,
                    MpvDemuxerReadaheadSeconds = _mpvSettings.DemuxerReadaheadSeconds,
                    MpvDemuxerMaxBytesMb = _mpvSettings.DemuxerMaxBytesMb,
                    MpvVideoThreads = _mpvSettings.VideoThreads,
                    MpvVideoLavcDr = _mpvSettings.VideoLavcDr,
                    MpvSubAuto = _mpvSettings.SubAuto,
                    MpvSubAssOverride = _mpvSettings.SubAssOverride,
                    MpvSubScale = _mpvSettings.SubScale,
                    MpvSubFontSize = _mpvSettings.SubFontSize,
                    MpvSubBorderSize = _mpvSettings.SubBorderSize,
                    MpvSubShadowOffset = _mpvSettings.SubShadowOffset,
                    MpvAudioExclusive = _mpvSettings.AudioExclusive,
                    MpvAudioChannels = _mpvSettings.AudioChannels,
                    MpvAudioNormalizeDownmix = _mpvSettings.AudioNormalizeDownmix,
                    MpvVolumeMax = _mpvSettings.VolumeMax,
                    MpvGaplessAudio = _mpvSettings.GaplessAudio,
                    MpvExtraOptions = _mpvSettings.ExtraOptions
                };
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });

                tempFile = ExtrasConfigPath + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(tempFile, json, new UTF8Encoding(false));

                if (File.Exists(ExtrasConfigPath))
                    File.Replace(tempFile, ExtrasConfigPath, null, true);
                else
                    File.Move(tempFile, ExtrasConfigPath);
            }
            catch
            {
                // ignore
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempFile))
                {
                    try
                    {
                        if (File.Exists(tempFile))
                            File.Delete(tempFile);
                    }
                    catch { }
                }
            }
        }


        private void RefreshCinemaModeMenuState()
        {
            bool cinema = _wledEnabled && _pausePlaceholderEnabled && _preRollEnabled;
            _cinemaModeEnabled = cinema;

            _syncingCinemaModeUi = true;
            try
            {
                if (_miCinemaMode != null) _miCinemaMode.Checked = cinema;
                if (_miWledEnable != null) _miWledEnable.Checked = _wledEnabled;
                if (_miPausePlaceholderEnable != null) _miPausePlaceholderEnable.Checked = _pausePlaceholderEnabled;
                if (_miPreRollEnable != null) _miPreRollEnable.Checked = _preRollEnabled;
                if (_miPausePlaceholderUseTmdbBackdrop != null) _miPausePlaceholderUseTmdbBackdrop.Checked = _pausePlaceholderUseTmdbBackdrop;
            }
            finally
            {
                _syncingCinemaModeUi = false;
            }
        }

        private void ApplyCinemaModeFromMenu(bool enabled)
        {
            _cinemaModeEnabled = enabled;
            _wledEnabled = enabled;
            _pausePlaceholderEnabled = enabled;
            _preRollEnabled = enabled;

            if (!enabled)
            {
                bool startedPendingFromGate = false;
                try
                {
                    if (_preOpenPlaceholderGateActive)
                        startedPendingFromGate = TryConsumePreOpenPlaceholderGate(startNow: true, fromRemote: false);
                }
                catch { }

                try { CancelPlaceholderBackdropFetch(); } catch { }
                if (!startedPendingFromGate)
                {
                    try { HidePreOpenPlaceholderGate(clearPending: true); } catch { }
                }

                try { HidePausePlaceholderNow(); } catch { }
                TrySkipActivePreRollToMainContent();
                CancelPendingWledPauseRestore();
                CancelPendingWledTransition();
                _ = RestoreWledInitialStateAsync(WLED_FADE_MS);
            }
            else
            {
                ApplyAmbientLightingForCurrentState();
            }

            RefreshCinemaModeMenuState();
            try { SaveExtrasConfig(); } catch { }
        }

        private void TrySkipActivePreRollToMainContent()
        {
            try
            {
                if (!_playingPreRoll || string.IsNullOrWhiteSpace(_pendingMainPathAfterPreRoll))
                    return;

                var next = _pendingMainPathAfterPreRoll;
                var nextResume = _pendingMainResumeAfterPreRoll;
                var nextPaused = _pendingMainStartPausedAfterPreRoll;

                _pendingMainPathAfterPreRoll = null;
                _pendingMainResumeAfterPreRoll = 0;
                _pendingMainStartPausedAfterPreRoll = false;
                _playingPreRoll = false;
                _suppressPreRollOnce = true;
                _suppressVideoLoadingOnce = true;

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try { OpenPath(next!, nextResume, nextPaused, allowPlaceholderGate: false); } catch { }
                    }));
                }
                catch { }
            }
            catch { }
        }

        private string? NormalizeWledBaseUrl(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                s = "http://" + s;
            }
            return s.TrimEnd('/');
        }

        private void ConfigureWledFromMenu()
        {
            try
            {
                string current = NormalizeWledBaseUrl(_wledBaseUrl) ?? "http://wled.local";
                string? value = PromptForTextValue(
                    "Configura WLED",
                    "Inserisci host o URL del dispositivo WLED (es. http://wled.local o 192.168.1.50)",
                    current);
                if (value == null) return;

                string? normalized = NormalizeWledBaseUrl(value);
                if (string.IsNullOrWhiteSpace(normalized)) return;

                _wledBaseUrl = normalized;
                try { SaveExtrasConfig(); } catch { }
                _lblStatus.Text = "WLED: " + _wledBaseUrl;

                if (_wledEnabled)
                    ApplyAmbientLightingForCurrentState();
            }
            catch { }
        }

        private string? PromptForTextValue(string title, string label, string initialValue, bool password = false)
        {
            using var form = new SettingsTextPromptForm(title, label, initialValue, UiEnglish, password)
            {
                StartPosition = (FormBorderStyle == FormBorderStyle.None)
                    ? FormStartPosition.CenterScreen
                    : FormStartPosition.CenterParent,
                TopMost = FormBorderStyle == FormBorderStyle.None
            };

            bool restoreTopMost = false;
            bool prevTopMost = false;
            bool suspendKeepAlive = false;
            try
            {
                if (FormBorderStyle == FormBorderStyle.None)
                {
                    _suspendFullscreenActivationKeepAlive++;
                    suspendKeepAlive = true;
                }

                if (FormBorderStyle == FormBorderStyle.None && TopMost)
                {
                    restoreTopMost = true;
                    prevTopMost = TopMost;
                    TopMost = false;
                    try { _overlayHost.TopMost = false; } catch { }
                }

                var result = form.ShowDialog(this);
                return result == DialogResult.OK ? form.Value : null;
            }
            finally
            {
                if (restoreTopMost)
                {
                    try { TopMost = prevTopMost; } catch { }
                    try { _overlayHost.TopMost = prevTopMost; } catch { }
                    try { Activate(); } catch { }
                }

                if (suspendKeepAlive && _suspendFullscreenActivationKeepAlive > 0)
                    _suspendFullscreenActivationKeepAlive--;
            }
        }

        private void CancelPendingWledPauseRestore()
        {
            var old = Interlocked.Exchange(ref _wledPauseRestoreCts, null);
            try { old?.Cancel(); } catch { }
            try { old?.Dispose(); } catch { }
        }

        private void CancelPendingWledTransition()
        {
            var old = Interlocked.Exchange(ref _wledTransitionCts, null);
            try { old?.Cancel(); } catch { }
            try { old?.Dispose(); } catch { }
        }

        private static int ClampWledBrightness(int bri)
            => Math.Max(WLED_MIN_BRI, Math.Min(WLED_DEFAULT_BRI, bri));

        private void RememberWorkingWledBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return;

            if (!string.Equals(_wledBaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                _wledBaseUrl = baseUrl;
                try { SaveExtrasConfig(); } catch { }
            }
        }

        private async Task<(bool ok, bool on, int bri)> TryGetWledStateAsync(string baseUrl, CancellationToken ct)
        {
            try
            {
                using var resp = await _wledHttp.GetAsync(baseUrl + "/json/state", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return (false, false, WLED_DEFAULT_BRI);

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Non basta ricevere un 200: sui fallback localhost potremmo aver colpito
                // un servizio diverso. Lo stato WLED valido deve avere almeno on + bri.
                if (!root.TryGetProperty("on", out var onProp) ||
                    (onProp.ValueKind != JsonValueKind.True && onProp.ValueKind != JsonValueKind.False) ||
                    !root.TryGetProperty("bri", out var briProp) ||
                    briProp.ValueKind != JsonValueKind.Number)
                {
                    return (false, false, WLED_DEFAULT_BRI);
                }

                bool on = onProp.GetBoolean();
                int bri = ClampWledBrightness(briProp.GetInt32());
                return (true, on, bri);
            }
            catch (OperationCanceledException) { throw; }
            catch { return (false, false, WLED_DEFAULT_BRI); }
        }

        private async Task EnsureWledRestoreStateAsync(CancellationToken ct)
        {
            if (_wledInitialStateCaptured)
                return;

            foreach (var baseUrl in GetWledBaseUrlCandidates())
            {
                ct.ThrowIfCancellationRequested();

                var state = await TryGetWledStateAsync(baseUrl, ct).ConfigureAwait(false);
                if (!state.ok)
                    continue;

                _wledInitialOn = state.on;
                _wledInitialBrightness = ClampWledBrightness(state.bri);
                _wledLastBrightness = state.on ? ClampWledBrightness(state.bri) : _wledInitialBrightness;
                _wledLastSentOn = state.on;
                _wledInitialStateCaptured = true;
                _wledRestoreOnExit = true;
                RememberWorkingWledBaseUrl(baseUrl);
                return;
            }

            // Nessun dispositivo raggiungibile: non inventiamo uno stato iniziale.
            // Cosi' un WLED che torna online piu' tardi puo' ancora essere catturato correttamente.
            _wledRestoreOnExit = false;
        }

        private int GetWledRestoreBrightness()
        {
            if (_wledInitialStateCaptured)
                return ClampWledBrightness(_wledInitialBrightness);

            if (_wledLastBrightness > 0)
                return ClampWledBrightness(_wledLastBrightness);

            return WLED_DEFAULT_BRI;
        }

        private async Task RestoreWledInitialStateAsync(int fadeMs, CancellationToken ct = default)
        {
            try
            {
                CancelPendingWledTransition();
                await EnsureWledRestoreStateAsync(ct).ConfigureAwait(false);

                if (!_wledInitialStateCaptured)
                {
                    // Se non conosciamo lo stato precedente, la scelta meno distruttiva e'
                    // riaccendere senza forzare la luminosita' memorizzata dal dispositivo.
                    await SendImmediateWledStateAsync(true, null, ct,
                        Math.Max(0, (int)Math.Round(Math.Max(0, fadeMs) / 100.0))).ConfigureAwait(false);
                    _wledLastRequestedOn = true;
                    _wledLastCommandUtc = DateTime.UtcNow;
                    return;
                }

                int fadeDs = Math.Max(0, (int)Math.Round(Math.Max(0, fadeMs) / 100.0));
                if (_wledInitialOn)
                {
                    await SendImmediateWledStateAsync(true, ClampWledBrightness(_wledInitialBrightness), ct, fadeDs).ConfigureAwait(false);
                }
                else
                {
                    // Importante: non inviare bri=0 insieme a on=false. WLED conserva da solo
                    // l'ultima luminosita' valida e la riutilizza alla prossima accensione.
                    await SendImmediateWledStateAsync(false, null, ct, fadeDs).ConfigureAwait(false);
                }

                _wledLastRequestedOn = _wledInitialOn;
                _wledLastCommandUtc = DateTime.UtcNow;
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private void ApplyAmbientLightingForCurrentState()
        {
            if (!_wledEnabled) return;

            if (_engine == null || _paused || _preOpenPlaceholderGateActive)
            {
                CancelPendingWledPauseRestore();
                CancelPendingWledTransition();
                _ = RestoreWledInitialStateAsync(WLED_FADE_MS);
                return;
            }

            NotifyPlaybackStartedForWled();
        }

        private void NotifyPlaybackStartedForWled()
        {
            if (!_wledEnabled) return;
            CancelPendingWledPauseRestore();
            _ = SendWledPowerAsync(false, WLED_FADE_OUT_MS);
        }

        private void NotifyPlaybackPausedForWled()
        {
            if (!_wledEnabled) return;

            CancelPendingWledPauseRestore();
            var cts = new CancellationTokenSource();
            _wledPauseRestoreCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(WLED_PAUSE_RESTORE_DELAY_MS, cts.Token).ConfigureAwait(false);
                    await RestoreWledInitialStateAsync(WLED_FADE_MS, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    if (ReferenceEquals(_wledPauseRestoreCts, cts))
                        _wledPauseRestoreCts = null;
                    try { cts.Dispose(); } catch { }
                }
            });
        }

        private void NotifyPlaybackStoppedForWled(bool forceRestore = false)
        {
            CancelPendingWledPauseRestore();

            if (_suppressNextWledRestore && !forceRestore)
            {
                _suppressNextWledRestore = false;
                return;
            }

            _suppressNextWledRestore = false;
            if (_wledEnabled || forceRestore)
            {
                CancelPendingWledTransition();
                _ = RestoreWledInitialStateAsync(WLED_FADE_MS);
            }
        }

        private List<string> GetWledBaseUrlCandidates()
        {
            var list = new List<string>();

            void AddCandidate(string? raw)
            {
                string? normalized = NormalizeWledBaseUrl(raw);
                if (string.IsNullOrWhiteSpace(normalized)) return;
                if (list.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase))) return;
                list.Add(normalized);
            }

            AddCandidate(_wledBaseUrl);

            string? configured = NormalizeWledBaseUrl(_wledBaseUrl);
            bool looksDefault =
                string.IsNullOrWhiteSpace(configured) ||
                string.Equals(configured, "http://wled.local", StringComparison.OrdinalIgnoreCase);

            if (looksDefault)
            {
                AddCandidate("http://127.0.0.1:9090");
                AddCandidate("http://localhost:9090");
                AddCandidate("http://127.0.0.1:8080");
                AddCandidate("http://localhost:8080");
                AddCandidate("http://127.0.0.1");
                AddCandidate("http://localhost");
                AddCandidate("http://wled.local");
            }

            return list;
        }

        private async Task<bool> TryPostWledStateAsync(string url, string payload, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await _wledHttp.SendAsync(req, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private async Task<bool> TrySendLegacyWledStateAsync(string baseUrl, bool on, int? bri, int? transitionDeciseconds, CancellationToken ct)
        {
            try
            {
                var url = new StringBuilder();
                url.Append(baseUrl).Append("/win&T=").Append(on ? "1" : "0");
                if (bri.HasValue)
                    url.Append("&A=").Append(Math.Max(0, Math.Min(255, bri.Value)));
                if (transitionDeciseconds.HasValue)
                    url.Append("&TT=").Append(Math.Max(0, transitionDeciseconds.Value));

                using var resp = await _wledHttp.GetAsync(url.ToString(), ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private async Task<bool> TrySendWledPayloadAsync(Dictionary<string, object> state, CancellationToken ct)
        {
            string payload = JsonSerializer.Serialize(state);

            bool desiredOn = true;
            int? desiredBri = null;
            int? desiredTransition = null;
            try
            {
                if (state.TryGetValue("on", out var onObj) && onObj is bool onBool)
                    desiredOn = onBool;
                if (state.TryGetValue("bri", out var briObj))
                    desiredBri = Convert.ToInt32(briObj);
                if (state.TryGetValue("tt", out var ttObj))
                    desiredTransition = Convert.ToInt32(ttObj);
            }
            catch { }

            foreach (var baseUrl in GetWledBaseUrlCandidates())
            {
                ct.ThrowIfCancellationRequested();

                bool ok = await TryPostWledStateAsync(baseUrl + "/json/state", payload, ct).ConfigureAwait(false);
                if (!ok)
                    ok = await TryPostWledStateAsync(baseUrl + "/json", payload, ct).ConfigureAwait(false);
                if (!ok)
                    ok = await TrySendLegacyWledStateAsync(baseUrl, desiredOn, desiredBri, desiredTransition, ct).ConfigureAwait(false);

                if (!ok)
                    continue;

                RememberWorkingWledBaseUrl(baseUrl);
                return true;
            }

            return false;
        }

        private async Task<bool> SendImmediateWledStateAsync(bool on, int? bri, CancellationToken ct, int? transitionDeciseconds = null)
        {
            var state = new Dictionary<string, object>
            {
                ["on"] = on
            };

            if (transitionDeciseconds.HasValue)
                state["tt"] = Math.Max(0, transitionDeciseconds.Value);
            else
                state["tt"] = 0;

            int? normalizedBri = null;
            if (bri.HasValue)
            {
                normalizedBri = on
                    ? ClampWledBrightness(Math.Max(WLED_MIN_BRI, bri.Value))
                    : Math.Max(0, Math.Min(WLED_DEFAULT_BRI, bri.Value));

                state["bri"] = normalizedBri.Value;
            }

            bool ok = await TrySendWledPayloadAsync(state, ct).ConfigureAwait(false);
            if (ok)
            {
                _wledLastSentOn = on;
                if (normalizedBri.HasValue && normalizedBri.Value > 0)
                    _wledLastBrightness = normalizedBri.Value;
                // Quando spegniamo non azzeriamo il ricordo della luminosita': WLED conserva
                // il valore di accensione precedente e noi dobbiamo fare lo stesso.
            }

            return ok;
        }

        private async Task PerformWledFadeAsync(bool on, int fadeMs, CancellationToken ct)
        {
            await EnsureWledRestoreStateAsync(ct).ConfigureAwait(false);

            if (on)
            {
                int targetBri = GetWledRestoreBrightness();
                int fadeDs = Math.Max(0, (int)Math.Round(Math.Max(0, fadeMs) / 100.0));

                if (_wledLastSentOn != true)
                {
                    if (!await SendImmediateWledStateAsync(true, WLED_MIN_BRI, ct, 0).ConfigureAwait(false))
                        return;
                }

                await SendImmediateWledStateAsync(true, targetBri, ct, fadeDs).ConfigureAwait(false);
                return;
            }

            int offFadeMs = Math.Max(0, fadeMs);
            int currentBri = ClampWledBrightness(_wledLastBrightness > 0 ? _wledLastBrightness : _wledInitialBrightness);
            int fadeDsOff = Math.Max(1, (int)Math.Round(Math.Max(1, offFadeMs) / 100.0));

            if (_wledLastSentOn == false)
                return;

            if (_wledLastSentOn != true)
            {
                // Stato non noto: non accendiamo le luci solo per costruire artificialmente
                // una dissolvenza. Chiediamo direttamente a WLED di spegnersi.
                await SendImmediateWledStateAsync(false, null, ct, fadeDsOff).ConfigureAwait(false);
                return;
            }

            if (!await SendImmediateWledStateAsync(true, WLED_MIN_BRI, ct, fadeDsOff).ConfigureAwait(false))
                return;

            await Task.Delay(Math.Max(offFadeMs + 150, 300), ct).ConfigureAwait(false);
            if (!await SendImmediateWledStateAsync(false, null, ct, 0).ConfigureAwait(false))
                return;

            await Task.Delay(120, ct).ConfigureAwait(false);
            await SendImmediateWledStateAsync(false, null, ct, 0).ConfigureAwait(false);
        }

        private async Task SendWledPowerAsync(bool on, int fadeMs, CancellationToken ct = default)
        {
            CancellationTokenSource? linked = null;
            CancellationTokenSource? previous = null;

            try
            {
                var now = DateTime.UtcNow;
                if (_wledLastRequestedOn == on && (now - _wledLastCommandUtc).TotalMilliseconds < 300)
                    return;

                _wledLastRequestedOn = on;
                _wledLastCommandUtc = now;

                linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                previous = Interlocked.Exchange(ref _wledTransitionCts, linked);
                try { previous?.Cancel(); } catch { }
                try { previous?.Dispose(); } catch { }

                await PerformWledFadeAsync(on, fadeMs, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                if (linked != null)
                {
                    if (ReferenceEquals(_wledTransitionCts, linked))
                        _wledTransitionCts = null;
                    try { linked.Dispose(); } catch { }
                }
            }
        }


    }
}
