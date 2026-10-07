#nullable enable
using CinecorePlayer2025.Utilities;
using CinecorePlayer2025.HUD;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;
using HDRMode = global::CinecorePlayer2025.Utilities.HdrMode;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private static string NormalizeUpscalingBackend(string? value) =>
            (value ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "madvr" => "madvr",
                "mpcvr" or "nvidia" or "rtx" => "mpcvr",
                _ => "off"
            };

        private bool? _madVrInstalled;
        private bool? _mpcVrInstalled;

        private bool IsMadVrFeatureAvailable()
            => _activeRendererChoice == VRChoice.MADVR || _manualRendererChoice == VRChoice.MADVR
                || (_madVrInstalled ??= ProbeMadVrFeatureAvailable());

        private bool ProbeMadVrFeatureAvailable()
        {
            if (_activeRendererChoice == VRChoice.MADVR || _manualRendererChoice == VRChoice.MADVR)
                return true;

            try
            {
                string ax = Environment.Is64BitProcess ? "madVR64.ax" : "madVR.ax";
                string[] roots =
                {
                    AppContext.BaseDirectory,
                    Environment.CurrentDirectory,
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."))
                };
                foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string thirdParties = Path.Combine(root, "third-parties");
                    if (!Directory.Exists(thirdParties))
                        continue;
                    foreach (string folder in Directory.EnumerateDirectories(thirdParties, "madVR*", SearchOption.TopDirectoryOnly))
                    {
                        if (File.Exists(Path.Combine(folder, ax)) ||
                            File.Exists(Path.Combine(folder, "madVR.ax")) ||
                            File.Exists(Path.Combine(folder, "madVR64.ax")))
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private bool IsMpcVrFeatureAvailable()
            => _activeRendererChoice == VRChoice.MPCVR || _manualRendererChoice == VRChoice.MPCVR
                || (_mpcVrInstalled ??= ProbeMpcVrFeatureAvailable());

        private bool ProbeMpcVrFeatureAvailable()
        {
            if (_activeRendererChoice == VRChoice.MPCVR || _manualRendererChoice == VRChoice.MPCVR)
                return true;

            try
            {
                string ax = Environment.Is64BitProcess ? "MpcVideoRenderer64.ax" : "MpcVideoRenderer.ax";
                string[] roots =
                {
                    AppContext.BaseDirectory,
                    Environment.CurrentDirectory,
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."))
                };
                foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string thirdParties = Path.Combine(root, "third-parties");
                    if (!Directory.Exists(thirdParties))
                        continue;
                    foreach (string folder in Directory.EnumerateDirectories(thirdParties, "MpcVideoRenderer*", SearchOption.TopDirectoryOnly))
                    {
                        if (File.Exists(Path.Combine(folder, ax)))
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private bool IsRtxVideoHdrAvailable()
            => Environment.Is64BitProcess && HasNvidiaDriverPresent() && IsMpcVrFeatureAvailable();

        private string RemoteRendererName =>
            (_activeRendererChoice ?? _manualRendererChoice)?.ToString() ?? "Auto";

        private string RemoteRendererSelection => _hasRuntimeRendererOverride
            ? _runtimeRendererChoiceOverride?.ToString() ?? "Auto"
            : _activeRendererChoice?.ToString() ?? _manualRendererChoice?.ToString() ?? "Auto";

        private string RemoteDefaultRendererName => _manualRendererChoice?.ToString() ?? "Auto";

        private static VRChoice? ParseRemoteRendererChoice(string? value) =>
            (value ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "evr" => VRChoice.EVR,
                "mpcvr" => VRChoice.MPCVR,
                "madvr" => VRChoice.MADVR,
                "mpv" => VRChoice.MPV,
                _ => null
            };

        private void SetRemoteRendererChoice(string? value, bool saveAsDefault)
        {
            VRChoice? choice = ParseRemoteRendererChoice(value);
            if (choice == VRChoice.MADVR && !IsMadVrFeatureAvailable()) return;
            if (choice == VRChoice.MPCVR && !IsMpcVrFeatureAvailable()) return;

            if (saveAsDefault)
            {
                _manualRendererChoice = choice;
                try { SaveExtrasConfig(); } catch { }
                try { _lblStatus.Text = Tx("Renderer predefinito: ", "Default renderer: ") + (choice?.ToString() ?? "Auto"); } catch { }
            }
            else
            {
                _hasRuntimeRendererOverride = true;
                _runtimeRendererChoiceOverride = choice;
                bool supportsSelectedUpscaling =
                    (choice == VRChoice.MADVR && string.Equals(_videoUpscalingBackend, "madvr", StringComparison.OrdinalIgnoreCase)) ||
                    (choice == VRChoice.MPCVR && string.Equals(_videoUpscalingBackend, "mpcvr", StringComparison.OrdinalIgnoreCase));
                if (_enableUpscaling && !supportsSelectedUpscaling)
                {
                    _enableUpscaling = false;
                    _videoUpscalingBackend = "off";
                }
                try { _lblStatus.Text = Tx("Renderer corrente: ", "Current renderer: ") + (choice?.ToString() ?? "Auto"); } catch { }
                if (_engine != null && !string.IsNullOrWhiteSpace(_currentPath))
                    ReopenSame();
            }
            try { PublishRemoteState(); } catch { }
        }

        private void SetRemoteUpscaling(string? backend, string? preset)
        {
            string normalized = NormalizeUpscalingBackend(backend);
            if (normalized == "madvr" && !IsMadVrFeatureAvailable()) return;
            if (normalized == "mpcvr" && (!IsMpcVrFeatureAvailable() || !HasNvidiaDriverPresent())) return;

            if (normalized == "madvr")
            {
                int profile = int.TryParse(preset, out int parsed) ? Math.Clamp(parsed, 0, 6) : (int)_madVrUpscalePreset;
                _madVrUpscalePreset = (MadVrCategoryPreset)profile;
                _videoUpscalingBackend = "madvr";
                _enableUpscaling = true;
                _hasRuntimeRendererOverride = true;
                _runtimeRendererChoiceOverride = VRChoice.MADVR;
            }
            else if (normalized == "mpcvr")
            {
                int threshold = int.TryParse(preset, out int parsed) ? Math.Clamp(parsed, 1, 4) : _mpcvrSuperResolutionMode;
                _mpcvrSuperResolutionMode = threshold;
                _videoUpscalingBackend = "mpcvr";
                _enableUpscaling = true;
                _hasRuntimeRendererOverride = true;
                _runtimeRendererChoiceOverride = VRChoice.MPCVR;
            }
            else
            {
                _videoUpscalingBackend = "off";
                _enableUpscaling = false;
            }

            try { SaveExtrasConfig(); } catch { }
            if (_engine != null && !string.IsNullOrWhiteSpace(_currentPath))
                ReopenSame();
            try { PublishRemoteState(); } catch { }
        }

        private string RemoteHdrMode => _hdrProfile switch
        {
            HdrUiProfile.RtxVideoHdr => "rtx",
            HdrUiProfile.Passthrough => "passthrough",
            HdrUiProfile.ToneMapSdr => "tonemap",
            HdrUiProfile.LutSdr => "lut",
            _ => "auto"
        };

        private string RemoteStereoMode => _stereoAutoEnabled
            ? "auto"
            : _stereo switch
            {
                Stereo3DMode.SBS => "sbs",
                Stereo3DMode.TAB => "tab",
                _ => "off"
            };

        private void SetHdrProfile(HdrUiProfile profile, string source)
        {
            bool needsMadVr = profile is HdrUiProfile.Passthrough or HdrUiProfile.ToneMapSdr or HdrUiProfile.LutSdr;
            bool needsRtxVideoHdr = profile == HdrUiProfile.RtxVideoHdr;
            if (needsMadVr && !IsMadVrFeatureAvailable())
            {
                try { _lblStatus.Text = Tx("Profilo HDR non disponibile: madVR non trovato.", "HDR profile unavailable: madVR was not found."); } catch { }
                return;
            }

            if (needsRtxVideoHdr && !IsRtxVideoHdrAvailable())
            {
                try { _lblStatus.Text = Tx("RTX Video HDR non disponibile: servono MPCVR x64 e una GPU NVIDIA RTX.", "RTX Video HDR unavailable: MPCVR x64 and an NVIDIA RTX GPU are required."); } catch { }
                return;
            }

            if ((needsMadVr || needsRtxVideoHdr) && (_stereo != Stereo3DMode.None || _stereoAutoEnabled))
            {
                _stereoAutoEnabled = false;
                _stereo = Stereo3DMode.None;
                _savedRendererFor3D = null;
                _hasSavedRendererFor3D = false;
            }

            if (needsMadVr && _manualRendererChoice != VRChoice.MADVR)
                _manualRendererChoice = VRChoice.MADVR;
            else if (needsRtxVideoHdr && _manualRendererChoice != VRChoice.MPCVR)
                _manualRendererChoice = VRChoice.MPCVR;

            if (needsRtxVideoHdr)
            {
                _enableUpscaling = false;
                _videoUpscalingBackend = "off";
                try { _engine?.SetUpscaling(false); } catch { }
            }

            _hdrProfile = profile;
            _hdr = profile is HdrUiProfile.ToneMapSdr or HdrUiProfile.LutSdr ? HDRMode.Off : HDRMode.Auto;
            try
            {
                _lblStatus.Text = profile switch
                {
                    HdrUiProfile.RtxVideoHdr when _info?.IsHdr == true => Tx("RTX Video HDR pronto: la sorgente è già HDR, MPCVR usa il passthrough nativo", "RTX Video HDR ready: the source is already HDR, so MPCVR uses native passthrough"),
                    HdrUiProfile.RtxVideoHdr when !HasAnyActiveHdrDisplay() => Tx("RTX Video HDR selezionato: attiva HDR in Windows sul display di riproduzione", "RTX Video HDR selected: enable HDR in Windows on the playback display"),
                    HdrUiProfile.RtxVideoHdr => Tx("RTX Video HDR attivo via MPC Video Renderer", "RTX Video HDR enabled through MPC Video Renderer"),
                    HdrUiProfile.Passthrough => "HDR: passthrough via madVR",
                    HdrUiProfile.ToneMapSdr => "HDR: tone-map in SDR via madVR",
                    HdrUiProfile.LutSdr => "HDR: 3DLUT in SDR via madVR",
                    _ => Tx("HDR: automatico", "HDR: automatic")
                };
            }
            catch { }

            Dbg.Log($"[HDR] Profile source={source}, profile={profile}, renderer={RemoteRendererName}");
            ApplyHdrProfileChangeLiveOrReopen();
            try { PublishRemoteState(); } catch { }
        }

        private void SetRemoteHdrProfile(string? mode)
        {
            HdrUiProfile profile = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "rtx" or "rtxhdr" => HdrUiProfile.RtxVideoHdr,
                "passthrough" => HdrUiProfile.Passthrough,
                "tonemap" => HdrUiProfile.ToneMapSdr,
                "lut" => HdrUiProfile.LutSdr,
                _ => HdrUiProfile.Auto
            };
            SetHdrProfile(profile, "remote");
        }

        private void Enable3DAuto(string source)
        {
            if (_hasSavedRendererFor3D)
            {
                _manualRendererChoice = _savedRendererFor3D;
                _savedRendererFor3D = null;
                _hasSavedRendererFor3D = false;
            }
            RestoreRuntimeRendererAfter3D();

            _stereo = Stereo3DMode.None;
            _stereoAutoEnabled = true;
            _lastStereoAutoDetection = null;
            try { SaveExtrasConfig(); } catch { }
            try { _lblStatus.Text = Tx("3D Auto: rilevamento SBS / Top-Bottom attivo", "3D Auto: SBS / Top-Bottom detection enabled"); } catch { }
            Dbg.Log($"[3D AUTO] enabled source={source}", Dbg.LogLevel.Info);

            if (_engine != null && !string.IsNullOrWhiteSpace(_currentPath))
                ReopenSame();
            try { PublishRemoteState(); } catch { }
        }

        private string LocalizeStereoDetectionReason(string? reason)
        {
            string value = reason?.Trim() ?? string.Empty;
            if (!UiEnglish || value.Length == 0)
                return value;

            return value
                .Replace("nome file SBS", "SBS file name", StringComparison.OrdinalIgnoreCase)
                .Replace("nome file Top/Bottom", "Top/Bottom file name", StringComparison.OrdinalIgnoreCase)
                .Replace("nessun metadato 3D disponibile sul flusso remoto", "no 3D metadata is available for the remote stream", StringComparison.OrdinalIgnoreCase)
                .Replace("percorso non analizzabile", "path could not be analyzed", StringComparison.OrdinalIgnoreCase)
                .Replace("3D indicato, layout SBS/TAB non determinabile", "3D indicated, but the SBS/TAB layout could not be determined", StringComparison.OrdinalIgnoreCase)
                .Replace("contenuto 2D: nessun indicatore stereoscopico", "2D content: no stereoscopic indicator", StringComparison.OrdinalIgnoreCase)
                .Replace("metadato FFmpeg Stereo 3D", "FFmpeg Stereo 3D metadata", StringComparison.OrdinalIgnoreCase)
                .Replace("metadato stereo_mode", "stereo_mode metadata", StringComparison.OrdinalIgnoreCase)
                .Replace("confronto visivo non conclusivo", "inconclusive visual comparison", StringComparison.OrdinalIgnoreCase)
                .Replace("layout 3D riconosciuto dal contenuto", "3D layout recognized from the content", StringComparison.OrdinalIgnoreCase);
        }

        private async Task PrepareAutoStereoForOpenAsync(string path, MediaProbe.Result? probe, CancellationToken cancellationToken)
        {
            if (!_stereoAutoEnabled)
                return;

            Stereo3DAutoDetector.DetectionResult result = await Stereo3DAutoDetector.DetectAsync(
                path,
                probe?.Width ?? 0,
                probe?.Height ?? 0,
                probe?.Duration ?? 0,
                cancellationToken);

            _lastStereoAutoDetection = result;
            if (result.Mode == Stereo3DMode.None)
            {
                _stereo = Stereo3DMode.None;
                if (_hasSavedRendererFor3D)
                {
                    _manualRendererChoice = _savedRendererFor3D;
                    _savedRendererFor3D = null;
                    _hasSavedRendererFor3D = false;
                }
                RestoreRuntimeRendererAfter3D();
            }
            else
            {
                // Ogni motore converte in 2D mantenendo le proprie elaborazioni:
                // nessun cambio di renderer.
                _stereo = result.Mode;
                if (_hasSavedRendererFor3D)
                {
                    _manualRendererChoice = _savedRendererFor3D;
                    _savedRendererFor3D = null;
                    _hasSavedRendererFor3D = false;
                }
                RestoreRuntimeRendererAfter3D();
            }

            string resolved = result.Mode switch
            {
                Stereo3DMode.SBS => "SBS → 2D",
                Stereo3DMode.TAB => "Top/Bottom → 2D",
                _ => Tx("2D nativo", "Native 2D")
            };
            try { _lblStatus.Text = $"3D Auto: {resolved} · {LocalizeStereoDetectionReason(result.Reason)}"; } catch { }
            Dbg.Log($"[3D AUTO] path='{path}', mode={result.Mode}, confidence={result.Confidence:0.00}, reason={result.Reason}", Dbg.LogLevel.Info);
        }

        private void ForceStereoCompatibleRuntimeRenderer()
        {
            if (!_hasRuntimeRendererOverride || _runtimeRendererChoiceOverride is VRChoice.EVR or VRChoice.MPCVR)
                return;

            if (!_hasSavedRuntimeRendererFor3D)
            {
                _savedHadRuntimeRendererOverrideFor3D = true;
                _savedRuntimeRendererFor3D = _runtimeRendererChoiceOverride;
                _hasSavedRuntimeRendererFor3D = true;
            }

            // BuildRendererOpenOrder gives the runtime choice precedence over the
            // default renderer. Force an actually supported graph while 3D is active.
            _hasRuntimeRendererOverride = true;
            _runtimeRendererChoiceOverride = VRChoice.EVR;
        }

        private bool RestoreRuntimeRendererAfter3D()
        {
            if (!_hasSavedRuntimeRendererFor3D)
                return false;

            _hasRuntimeRendererOverride = _savedHadRuntimeRendererOverrideFor3D;
            _runtimeRendererChoiceOverride = _savedRuntimeRendererFor3D;
            _savedHadRuntimeRendererOverrideFor3D = false;
            _savedRuntimeRendererFor3D = null;
            _hasSavedRuntimeRendererFor3D = false;
            return true;
        }

        private void SetVideoUpscaling(bool enabled, string source)
        {
            if (enabled && !IsMadVrFeatureAvailable())
            {
                try { _lblStatus.Text = Tx("Upscaling non disponibile: madVR non trovato.", "Upscaling unavailable: madVR was not found."); } catch { }
                return;
            }

            if (enabled && _hdrProfile == HdrUiProfile.RtxVideoHdr)
            {
                _hdrProfile = HdrUiProfile.Auto;
                _hdr = HDRMode.Auto;
            }

            bool rendererChanged = false;
            if (enabled && _hasRuntimeRendererOverride)
            {
                // The legacy/context-menu toggle explicitly requests madVR. Do not let a
                // renderer selected earlier from the remote silently win on the next open.
                _hasRuntimeRendererOverride = false;
                rendererChanged = _activeRendererChoice != VRChoice.MADVR;
            }
            if (enabled && _activeRendererChoice != VRChoice.MADVR && _manualRendererChoice != VRChoice.MADVR)
            {
                if (_stereo != Stereo3DMode.None)
                {
                    _stereo = Stereo3DMode.None;
                    _savedRendererFor3D = null;
                    _hasSavedRendererFor3D = false;
                }
                _manualRendererChoice = VRChoice.MADVR;
                rendererChanged = true;
            }

            _enableUpscaling = enabled;
            _videoUpscalingBackend = enabled ? "madvr" : "off";
            try { SaveExtrasConfig(); } catch { }
            try { _lblStatus.Text = enabled ? Tx("Upscaling madVR attivo", "madVR upscaling enabled") : Tx("Upscaling disattivato", "Upscaling disabled"); } catch { }
            Dbg.Log($"[VIDEO] Upscaling source={source}, enabled={enabled}, renderer={RemoteRendererName}");

            if (_engine != null && rendererChanged)
                ReopenSame();
            else
                try { _engine?.SetUpscaling(enabled); } catch { }
            try { PublishRemoteState(); } catch { }
        }
    }
}
