#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using FFmpeg.AutoGen;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;
using HDRMode = global::CinecorePlayer2025.Utilities.HdrMode;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private static Image? LoadBrandLogoImageCopy(int size)
        {
            try
            {
                string[] roots =
                {
                    AppContext.BaseDirectory,
                    Environment.CurrentDirectory,
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."))
                };

                string[] names = { "logo.png", "logo.jpg", "logo.jpeg", "logo.bmp" };
                foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string name in names)
                    {
                        try
                        {
                            string path = Path.Combine(root, "Assets", name);
                            if (!File.Exists(path))
                                continue;

                            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var src = Image.FromStream(fs);
                            return new Bitmap(src);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return CreateFallbackBrandLogo(size);
        }

        private static Image CreateFallbackBrandLogo(int size)
        {
            int s = Math.Max(32, size);
            var bmp = new Bitmap(s, s, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var outer = new Rectangle(2, 2, s - 4, s - 4);
            using (var glow = new SolidBrush(Color.FromArgb(46, 0, 136, 255)))
                g.FillEllipse(glow, outer);

            var inner = Rectangle.Inflate(outer, -Math.Max(2, s / 18), -Math.Max(2, s / 18));
            using (var fill = new LinearGradientBrush(inner, Color.FromArgb(32, 90, 190), Color.FromArgb(6, 24, 56), LinearGradientMode.ForwardDiagonal))
                g.FillEllipse(fill, inner);
            using (var pen = new Pen(Color.FromArgb(190, 150, 205, 255), Math.Max(1.2f, s / 64f)))
                g.DrawEllipse(pen, inner);

            PointF[] triangle =
            {
                new(inner.Left + inner.Width * 0.41f, inner.Top + inner.Height * 0.31f),
                new(inner.Left + inner.Width * 0.41f, inner.Top + inner.Height * 0.69f),
                new(inner.Left + inner.Width * 0.72f, inner.Top + inner.Height * 0.50f)
            };
            using var play = new SolidBrush(Color.FromArgb(244, 250, 255));
            g.FillPolygon(play, triangle);

            return bmp;
        }

        private void CycleHdrProfile()
        {
            HdrUiProfile next = _hdrProfile switch
            {
                HdrUiProfile.Auto => HdrUiProfile.RtxVideoHdr,
                HdrUiProfile.RtxVideoHdr => HdrUiProfile.Passthrough,
                HdrUiProfile.Passthrough => HdrUiProfile.ToneMapSdr,
                HdrUiProfile.ToneMapSdr => HdrUiProfile.LutSdr,
                _ => HdrUiProfile.Auto
            };
            SetHdrProfile(next, "cycle");
        }

        private string DescribeRequestedHdrOutput()
        {
            return _hdrProfile switch
            {
                HdrUiProfile.Auto => "RendererDefault",
                HdrUiProfile.RtxVideoHdr => "RTX Video HDR SDR->HDR10 requested through MPCVR",
                HdrUiProfile.Passthrough => "HDR passthrough requested",
                HdrUiProfile.ToneMapSdr => "SDR requested (madVR tone-map HDR->SDR)",
                HdrUiProfile.LutSdr => "SDR requested (madVR 3DLUT HDR->SDR)",
                _ => _hdr.ToString()
            };
        }

        private bool TryMapMadVrHdrMode(out MadVrHdrMode mode)
        {
            switch (_hdrProfile)
            {
                case HdrUiProfile.Passthrough:
                    mode = MadVrHdrMode.PassthroughHdr;
                    return true;
                case HdrUiProfile.ToneMapSdr:
                    mode = MadVrHdrMode.ToneMapHdrToSdr;
                    return true;
                case HdrUiProfile.LutSdr:
                    mode = MadVrHdrMode.LutHdrToSdr;
                    return true;
                default:
                    mode = MadVrHdrMode.Auto;
                    return true;
            }
        }

        private void ApplyHdrProfileChangeLiveOrReopen()
        {
            try { SaveExtrasConfig(); } catch { }

            if (_engine != null &&
                _activeRendererChoice == VRChoice.MADVR &&
                _currentMediaHasVideo &&
                TryMapMadVrHdrMode(out var mode))
            {
                try
                {
                    LogVideoPipelineDiagnostics("Before live madVR HDR profile apply", VRChoice.MADVR, _info?.IsHdr == true, _currentMediaHasVideo);
                    _engine.SetMadVrHdrMode(mode);
                    Dbg.Log($"[HDR] Live madVR HDR profile applied: mode={mode}, profile={_hdrProfile}, requestedOutput={DescribeRequestedHdrOutput()}");
                    ScheduleRendererDiagnosticLog("After live madVR HDR profile apply", _engine, VRChoice.MADVR, _info?.IsHdr == true, _currentMediaHasVideo, 900);
                    return;
                }
                catch (Exception ex)
                {
                    Dbg.Warn($"[HDR] Live madVR HDR profile apply failed, reopening graph: {ex.Message}");
                }
            }

            if (_engine != null)
                ReopenSame();
        }

        private bool IsRequestedOutputHdr()
        {
            if (_hdrProfile == HdrUiProfile.RtxVideoHdr)
                return _currentMediaHasVideo;

            if (_info?.IsHdr != true) return false;

            return _hdrProfile != HdrUiProfile.ToneMapSdr &&
                   _hdrProfile != HdrUiProfile.LutSdr;
        }

        private string DescribeHdrOverlayMode(bool fileHdr)
        {
            if (_hdrProfile == HdrUiProfile.RtxVideoHdr)
                return fileHdr ? "HDR passthrough (RTX profile)" : "RTX Video HDR";

            if (!fileHdr)
                return "SDR";

            return _hdrProfile switch
            {
                HdrUiProfile.Passthrough => "HDR passthrough",
                HdrUiProfile.ToneMapSdr => "HDR to SDR",
                HdrUiProfile.LutSdr => "HDR to SDR LUT",
                _ => "HDR auto"
            };
        }

        private void LogVideoPipelineDiagnostics(string stage, VRChoice renderer, bool fileHdr, bool hasVideo)
        {
            try
            {
                var screen = Screen.FromControl(this);
                string formMode = FormBorderStyle == FormBorderStyle.None ? "fullscreen-borderless" : "windowed";
                string displayControl = "n/d";
                try { displayControl = (_engine?.HasDisplayControl() == true).ToString(); } catch { }

                Dbg.Log(
                    $"[VIDEO] {stage}: renderer={renderer}, sourceHdr={fileHdr}, hasVideo={hasVideo}, " +
                    $"playerHdrProfile={_hdrProfile}, playerHdrRequest={DescribeRequestedHdrOutput()}, displayControl={displayControl}, " +
                    $"windowMode={formMode}, screen={screen.DeviceName} {screen.Bounds.Width}x{screen.Bounds.Height}@{screen.Bounds.X},{screen.Bounds.Y}, " +
                    $"formBounds={Bounds.Width}x{Bounds.Height}@{Bounds.X},{Bounds.Y}, videoHost={_videoHost.ClientSize.Width}x{_videoHost.ClientSize.Height}");

                Dbg.Log("[HDR] Player-side Windows HDR/swap-chain/HDR metadata override: none. Renderer/madVR owns HDR output unless an explicit madVR profile hotkey is requested.");
                Dbg.Log("[VIDEO] Renderer snapshot: " + (_engine?.GetRendererDiagnosticSnapshot() ?? "n/d"));
            }
            catch (Exception ex)
            {
                Dbg.Warn("[VIDEO] diagnostics failed: " + ex.Message);
            }
        }

        private void ScheduleMadVrHdrProfileApply(IPlaybackEngine engine, VRChoice renderer, bool fileHdr, bool hasVideo, int delayMs = 700)
        {
            if (renderer != VRChoice.MADVR)
            {
                Dbg.Log($"[HDR] madVR profile apply skipped: renderer={renderer}, profile={_hdrProfile}", Dbg.LogLevel.Verbose);
                return;
            }

            Dbg.Log("[HDR] madVR automatic profile hotkey skipped during startup to keep playback stable.", Dbg.LogLevel.Info);
        }

        private void ScheduleRendererDiagnosticLog(string stage, IPlaybackEngine engine, VRChoice renderer, bool fileHdr, bool hasVideo, int delayMs)
        {
            try
            {
                var scheduledEngine = engine;
                var timer = new System.Windows.Forms.Timer { Interval = Math.Max(100, delayMs) };
                timer.Tick += (_, __) =>
                {
                    try
                    {
                        timer.Stop();
                        if (ReferenceEquals(_engine, scheduledEngine))
                            LogVideoPipelineDiagnostics(stage, renderer, fileHdr, hasVideo);
                    }
                    catch { }
                    finally
                    {
                        try { timer.Dispose(); } catch { }
                    }
                };
                timer.Start();
            }
            catch { }
        }

        private void TryApplyMadVrRefreshForPlayback(VRChoice renderer, bool hasVideo)
        {
            if (renderer != VRChoice.MADVR || !hasVideo)
                return;

            double fps = GetVideoFps(_info);
            // Prefer the cadence actually arriving at the renderer once preroll
            // has completed; nominal container rates can differ after IVTC.
            if (_engine is DirectShowUnifiedEngine rendererEngine)
            {
                double presentedFps = rendererEngine.GetMadVrPresentedFrameRate();
                if (presentedFps > 0) fps = presentedFps;
            }
            _targetFps = IsCinema24Fps(fps) ? (fps < 23.99 ? 23 : 24) : 0;
            if (_pipModeActive) return;
            var screen = Screen.FromControl(this);
            bool matched = FormBorderStyle == FormBorderStyle.None
                ? _refresh.SwitchToMatching(screen, fps)
                : _refresh.HasMatchingCadence(screen, fps);
            if (_engine is DirectShowUnifiedEngine ds)
                ds.ConfigureMadVrCadenceFallback(!matched && RationalDisplayModeSwitcher.GetFrameRate(fps).Numerator > 0);
        }

        private static bool IsCinema24Fps(double fps)
        {
            if (fps <= 0)
                return false;

            return Math.Abs(fps - 23.976) <= 0.08 ||
                   Math.Abs(fps - (24000.0 / 1001.0)) <= 0.08 ||
                   Math.Abs(fps - 24.0) <= 0.08;
        }

        private int ProbeAudioAvgKbps()
        {
            try
            {
                if (_info == null) return 0;
                var t = _info.GetType();
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                object? GetMemberValue(string name)
                {
                    try
                    {
                        var p = t.GetProperty(name, flags);
                        if (p != null) return p.GetValue(_info);
                    }
                    catch { }

                    try
                    {
                        var f = t.GetField(name, flags);
                        if (f != null) return f.GetValue(_info);
                    }
                    catch { }

                    return null;
                }

                foreach (var name in new[] { "AudioBitrateKbps", "AudioAvgKbps" })
                {
                    var v = GetMemberValue(name);
                    if (v is int ik && ik > 0) return ik;
                    if (v is long lk && lk > 0) return (int)lk;
                    if (v is double dk && dk > 0) return (int)Math.Round(dk);
                }

                foreach (var name in new[] { "AudioBitrate", "AudioAvgBitrate" })
                {
                    var v = GetMemberValue(name);
                    if (v is long lbps && lbps > 0) return (int)Math.Round(lbps / 1000.0);
                    if (v is int ibps && ibps > 0) return (int)Math.Round(ibps / 1000.0);
                    if (v is double dbps && dbps > 0) return (int)Math.Round(dbps / 1000.0);
                }
            }
            catch { }
            return 0;
        }
        private void RecheckAudioNow()
        {
            if (_engine == null) return;

            // Media container avg per fallback
            int avgContainerKbpsLocal = 0;
            try
            {
                if (!string.IsNullOrEmpty(_currentPath) && File.Exists(_currentPath) && _duration > 1)
                {
                    var fi = new FileInfo(_currentPath);
                    avgContainerKbpsLocal = (int)Math.Round((fi.Length * 8.0 / 1000.0) / _duration);
                }
            }
            catch { }

            var sel = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
            var lav = GetLavAudioIODetails(sel?.Name);
            _bitstreamNow = IsBitstream();

            int kbps = 0;
            if (lav.AudioNowKbps > 0) kbps = lav.AudioNowKbps;
            if (kbps <= 0 && sel != null) kbps = ParseKbpsFromName(sel.Name);
            if (kbps <= 0 && avgContainerKbpsLocal > 0) kbps = (int)(avgContainerKbpsLocal * 0.30);
            _audioBitrateNowKbps = kbps;

            // refresh immediato dell’overlay info se presente
            if (_info != null)
            {
                UpdateInfoOverlay(ResolveRendererForInfo(_info.IsHdr), _info.IsHdr);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (_iconBig != null) SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_BIG, _iconBig.Handle);
                if (_iconSmall != null)
                {
                    SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL, _iconSmall.Handle);
                    SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL2, _iconSmall.Handle);
                }
            }
            catch { }
        }
    }
}
