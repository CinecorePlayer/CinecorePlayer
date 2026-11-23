using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Dimensionamento dell'immagine a schermo intero (PlayerForm.ImageSizing.cs): altezza costante, area costante o
/// personalizzata, con il formato vero misurato ogni due secondi su un fotogramma piccolo (ffmpeg in sottofondo,
/// tramite ProcessLauncher) e le scene IMAX che si allargano. Al motore va solo una scala ("scale"): video-zoom in mpv,
/// rettangolo di destinazione nel VideoHost.
/// </summary>
public sealed partial class MainWindow
{
    private ImageSizingSettings _imageSizing = ImageSizingSettings.Load();
    private AspectStabilizer? _sizingAspect;
    private string? _sizingFile;
    private DispatcherTimer? _sizingDetectTimer, _sizingAnimation;
    private int _sizingDetectBusy, _sizingFastSamples, _sizingSamples;
    private double _sizingScale = 1, _sizingFrom = 1, _sizingTo = 1;
    private long _sizingAnimationStart, _sizingHoldUntil;
    private bool _sizingWasExpanded;

    internal ImageSizingSettings ImageSizingOptions => _imageSizing;

    private double SizingFrameAspect()
    {
        if (_cropAspect > 0) return _cropAspect;
        if (_probe is { Width: > 0, Height: > 0 } p) return p.Width / (double)p.Height;
        return 16 / 9.0;
    }

    // Solo a schermo intero, con un video vero (in finestra resta "Riempi", come su Windows).
    private bool ImageSizingApplies() =>
        _imageSizing.Mode != ImageSizingMode.Fill && _video.IsRunning && _probe?.HasVideo != false && IsFullscreen && _afterDemo == null && SizingSupported && _stereo == StereoMode.None;

    /// <summary>Tutti i renderer: madVR riceve la destinazione ridotta dal VideoHost come MPC-VR.</summary>
    private bool SizingSupported => true;

    private bool SizingOnHold => _sizingHoldUntil != 0 && _sizingSamples < 2 && Environment.TickCount64 < _sizingHoldUntil;

    private double ImageSizingTargetScale()
    {
        if (!ImageSizingApplies()) return 1;
        if (SizingOnHold) return _sizingScale;
        double w = ClientSize.Width, h = ClientSize.Height;
        if (w < 16 || h < 16) return 1;
        double frame = SizingFrameAspect(), active = _sizingAspect?.Current ?? frame;
        var picture = ImageSizing.Compute(w, h, active, _imageSizing, _sizingAspect?.Expanded == true);
        // madVR e MPC-VR (finestra del video ristretta) non vanno oltre 1; mpv si', con video-zoom.
        return Math.Clamp(ImageSizing.FrameScale(w, h, frame, active, picture), 0.3, Engine == "mpv" ? 3 : 1);
    }

    /// <summary>All'apertura, al cambio di schermo intero e quando cambiano le opzioni o il ritaglio.</summary>
    private void ApplyImageSizing(bool animate = false)
    {
        if (!_video.IsRunning) { StopImageSizing(); return; }
        if (_sizingFile != _file)
        {
            _sizingFile = _file;
            _sizingFastSamples = 6;
            _sizingSamples = 0;
            // Finche' non si sa il formato vero l'immagine resta com'e' (un 2.39 con le bande non parte piccolo).
            _sizingHoldUntil = _imageSizing.DetectBars && _file != null && !IsStream(_file) ? Environment.TickCount64 + 1500 : 0;
            _sizingAspect = new AspectStabilizer(SizingFrameAspect());
            _sizingWasExpanded = false;
            _sizingScale = 1;
        }
        UpdateImageSizingDetection();
        double target = ImageSizingTargetScale();
        if (!animate || _imageSizing.TransitionMs <= 0 || Math.Abs(target - _sizingScale) < 0.002)
        {
            _sizingAnimation?.Stop();
            SetImageSizingScale(target);
            return;
        }
        _sizingFrom = _sizingScale; _sizingTo = target;
        _sizingAnimationStart = Environment.TickCount64;
        _sizingAnimation ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            double t = Math.Clamp((Environment.TickCount64 - _sizingAnimationStart) / (double)Math.Max(1, _imageSizing.TransitionMs), 0, 1);
            double eased = t * t * (3 - 2 * t);
            SetImageSizingScale(_sizingFrom + (_sizingTo - _sizingFrom) * eased);
            if (t >= 1) _sizingAnimation?.Stop();
        });
        _sizingAnimation.Start();
    }

    private double _sentScale = 1;
    private void SetImageSizingScale(double scale)
    {
        _sizingScale = scale;
        if (Math.Abs(scale - _sentScale) < 0.0005) return;
        _sentScale = scale;
        _video.Send("scale " + scale.ToString("0.####", CultureInfo.InvariantCulture));
    }

    private void UpdateImageSizingDetection()
    {
        bool wanted = _imageSizing.Mode != ImageSizingMode.Fill && _video.IsRunning;
        if (!wanted) { _sizingDetectTimer?.Stop(); return; }
        _sizingDetectTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => SampleActiveAspect());
        _sizingDetectTimer.Interval = TimeSpan.FromMilliseconds(_sizingFastSamples > 0 ? 600 : 2000);
        if (!_sizingDetectTimer.IsEnabled) _sizingDetectTimer.Start();
    }

    // Ogni due secondi un fotogramma piccolo nella posizione corrente: il formato cambia solo dopo due misure concordi.
    private void SampleActiveAspect()
    {
        if (_sizingFastSamples > 0 && --_sizingFastSamples == 0 && _sizingDetectTimer != null) _sizingDetectTimer.Interval = TimeSpan.FromSeconds(2);
        if (Math.Abs(ImageSizingTargetScale() - (_sizingAnimation?.IsEnabled == true ? _sizingTo : _sizingScale)) > 0.002) ApplyImageSizing(animate: true);
        if (!ImageSizingApplies() || !_imageSizing.DetectBars || !_hud.IsPlaying || _sizingAspect is not AspectStabilizer stabilizer) return;
        if (_file is not { } file || IsStream(file) || MacTools.Ffmpeg is not { } ffmpeg) { stabilizer.Offer(null); return; }
        if (Interlocked.Exchange(ref _sizingDetectBusy, 1) != 0) return;
        double position = _hud.Position, frameAspect = _probe is { Width: > 0, Height: > 0 } p ? p.Width / (double)p.Height : SizingFrameAspect();
        const int w = 256;
        int h = Math.Max(32, (int)Math.Round(w / frameAspect / 2) * 2);
        _ = Task.Run(() =>
        {
            double? sample = null;
            try
            {
                // Software e sottofondo: il decodificatore hardware resta alla riproduzione (vedi HdrAnalyzer).
                var (_, raw, _) = ProcessLauncher.Run(ffmpeg, new[]
                {
                    "-nostdin", "-v", "error", "-threads", "2", "-noaccurate_seek", "-ss", position.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", file, "-map", "0:v:0", "-frames:v", "1", "-an", "-sn", "-vf", $"scale={w}:{h}:flags=fast_bilinear,format=gray", "-f", "rawvideo", "pipe:1",
                }, true, 15000, CancellationToken.None);
                if (raw.Length >= w * h) sample = ImageSizing.DetectActiveAspect(raw, w, h, frameAspect);
            }
            catch (Exception ex) { Console.Error.WriteLine("[SIZING] " + ex.Message); }
            finally { Interlocked.Exchange(ref _sizingDetectBusy, 0); }
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(stabilizer, _sizingAspect)) return;
                if (sample != null) _sizingSamples++;
                bool expanded = stabilizer.Expanded, expandedChanged = expanded != _sizingWasExpanded;
                _sizingWasExpanded = expanded;
                if (stabilizer.Offer(sample) | expandedChanged)
                {
                    Console.Error.WriteLine($"[SIZING] formato {stabilizer.Current:0.00} (prevalente {stabilizer.Usual:0.00}, scena alta {stabilizer.Expanded})");
                    ApplyImageSizing(animate: true);
                }
                else if (_sizingSamples == 2) ApplyImageSizing(animate: true);
            });
        });
    }

    private void StopImageSizing()
    {
        _sizingDetectTimer?.Stop();
        _sizingAnimation?.Stop();
        _sizingFile = null;
        _sizingAspect = null;
        _sizingScale = 1; _sentScale = 1;
    }

    /// <summary>Tasto Z (come Windows): modalita' successiva, mostrata per un momento.</summary>
    private void CycleImageSizingMode()
    {
        _imageSizing.Mode = (ImageSizingMode)(((int)_imageSizing.Mode + 1) % 4);
        _imageSizing.Save();
        ApplyImageSizing(animate: true);
        string note = _imageSizing.Mode == ImageSizingMode.Fill ? "" : !SizingSupported ? "  ·  non con madVR (usa MPC-VR o MPV)" : !IsFullscreen ? "  ·  attiva a schermo intero" : "";
        _osd.Show("Immagine: " + ImageSizingSettings.ModeName(_imageSizing.Mode) + note, 1600);
    }

    /// <summary>Le opzioni sono cambiate dalla pagina Impostazioni.</summary>
    internal void ImageSizingChanged()
    {
        _imageSizing.Save();
        ApplyImageSizing(animate: true);
    }
}
