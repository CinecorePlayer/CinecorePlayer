#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Dimensionamento dell'immagine a schermo intero (altezza costante / area costante).
    // Il calcolo e' in Utilities/ImageSizing.cs; qui ci sono solo il collegamento con il motore,
    // il rilevamento periodico del formato attivo e la transizione.
    public sealed partial class PlayerForm
    {
        private ImageSizingSettings _imageSizing = ImageSizingSettings.Load();
        private AspectStabilizer? _sizingAspect;
        private string? _sizingPath;
        private System.Windows.Forms.Timer? _sizingDetectTimer, _sizingAnimation;
        private Thumbnailer? _sizingFrames;
        private int _sizingDetectBusy;
        private double _sizingScale = 1, _sizingFrom = 1, _sizingTo = 1;
        private long _sizingAnimationStart;

        internal ImageSizingSettings ImageSizingOptions => _imageSizing;

        private static string ImageSizingModeName(ImageSizingMode mode, bool english) => mode switch
        {
            ImageSizingMode.ConstantHeight => english ? "Constant height" : "Altezza costante",
            ImageSizingMode.ConstantArea => english ? "Constant area" : "Area costante",
            ImageSizingMode.Custom => english ? "Custom" : "Personalizzata",
            _ => english ? "Fill" : "Riempi"
        };

        /// <summary>Formato del fotogramma codificato (con le eventuali bande dentro).</summary>
        private double SizingFrameAspect()
        {
            if (_info is { Width: > 0, Height: > 0 } info)
                return info.Width * (info.SampleAspect > 0 ? info.SampleAspect : 1) / info.Height;
            if (_currentWebWidth > 0 && _currentWebHeight > 0) return _currentWebWidth / (double)_currentWebHeight;
            return 16 / 9.0;
        }

        // Solo a schermo intero, con un film vero: in finestra, in PiP, in 3D e durante il segnaposto
        // di pausa resta il comportamento normale.
        private bool ImageSizingApplies() =>
            _imageSizing.Mode != ImageSizingMode.Fill && _engine is IVideoScaleEngine && _currentMediaHasVideo &&
            IsFullscreenOverlayMode() && !_pipModeActive && _stereo == Stereo3DMode.None && !_videoDetachedForPausePlaceholder && !_playingPreRoll;

        private long _sizingHoldUntil;
        private int _sizingSamples;
        private bool SizingOnHold => _sizingHoldUntil != 0 && _sizingSamples < 2 && Environment.TickCount64 < _sizingHoldUntil;

        private double ImageSizingTargetScale()
        {
            if (!ImageSizingApplies() || _engine is not IVideoScaleEngine scaler) return 1;
            if (SizingOnHold) return _sizingScale;
            Size screen = _videoHost.ClientSize;
            if (screen.Width < 16 || screen.Height < 16) return 1;
            double frame = SizingFrameAspect();
            double active = _sizingAspect?.Current ?? frame;
            var picture = ImageSizing.Compute(screen, active, _imageSizing, _sizingAspect?.Expanded == true);
            return Math.Clamp(ImageSizing.FrameScale(screen, frame, active, picture), 0.3, scaler.MaxVideoScale);
        }

        /// <summary>Ricalcola e applica. Da chiamare all'apertura, al cambio schermo intero/finestra e quando cambiano le opzioni.</summary>
        private void ApplyImageSizing(bool animate = false)
        {
            if (_engine is not IVideoScaleEngine)
            {
                StopImageSizing();
                return;
            }
            if (!string.Equals(_sizingPath, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                _sizingPath = _currentPath;
                _sizingFastSamples = 6;
                // Finche' non si sa il formato vero (due misure, o un secondo e mezzo) l'immagine resta
                // com'e': un 2.39 con le bande nel fotogramma non parte piu' rimpicciolito per poi allargarsi.
                _sizingSamples = 0;
                _sizingHoldUntil = _imageSizing.DetectBars && _isLocalFile ? Environment.TickCount64 + 1500 : 0;
                _sizingAspect = new AspectStabilizer(SizingFrameAspect());
                try { _sizingFrames?.Dispose(); } catch { }
                _sizingFrames = null;
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
            _sizingFrom = _sizingScale;
            _sizingTo = target;
            _sizingAnimationStart = Environment.TickCount64;
            if (_sizingAnimation == null)
            {
                _sizingAnimation = new System.Windows.Forms.Timer { Interval = 16 };
                _sizingAnimation.Tick += (_, __) =>
                {
                    double t = Math.Clamp((Environment.TickCount64 - _sizingAnimationStart) / (double)Math.Max(1, _imageSizing.TransitionMs), 0, 1);
                    double eased = t * t * (3 - 2 * t);
                    SetImageSizingScale(_sizingFrom + (_sizingTo - _sizingFrom) * eased);
                    if (t >= 1) _sizingAnimation?.Stop();
                };
            }
            _sizingAnimation.Start();
        }

        private void SetImageSizingScale(double scale)
        {
            if (_engine is not IVideoScaleEngine scaler) return;
            _sizingScale = scale;
            try
            {
                scaler.SetVideoScale(scale);
                UpdateVideoWindowForCurrentHost();
                // Fuori dal nuovo rettangolo resterebbe l'immagine di prima: l'host la ricopre di nero.
                // Solo la cornice, non l'area del video, altrimenti a ogni passo della transizione
                // il fotogramma sparirebbe per un istante.
                if (scale < 0.9995)
                {
                    Rectangle picture = _engine.GetLastDestRectAsClient(_videoHost.ClientRectangle);
                    using var frame = new Region(_videoHost.ClientRectangle);
                    if (picture.Width > 0 && picture.Height > 0 && _engine is not LibMpvPlaybackEngine) frame.Exclude(picture);
                    if (_engine is not LibMpvPlaybackEngine) { _videoHost.Invalidate(frame); _videoHost.Update(); }
                }
                SyncOverlayToVideoRect();
            }
            catch (Exception ex) { Dbg.Warn("[SIZING] apply: " + ex.Message); }
        }

        private void UpdateImageSizingDetection()
        {
            // Il timer gira per tutto il film quando una modalita' e' scelta: oltre a misurare il formato
            // si accorge di PiP, 3D, finestra e segnaposto di pausa, che riportano l'immagine a "Riempi".
            bool wanted = _imageSizing.Mode != ImageSizingMode.Fill && _engine is IVideoScaleEngine && _currentMediaHasVideo;
            if (!wanted) { _sizingDetectTimer?.Stop(); return; }
            if (_sizingDetectTimer == null)
            {
                _sizingDetectTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                _sizingDetectTimer.Tick += (_, __) => SampleActiveAspect();
            }
            // All'inizio del film si misura fitto: un 2.39 con le bande nel fotogramma partiva alla misura
            // di un 16:9 e si allargava solo dopo sette secondi.
            _sizingDetectTimer.Interval = _sizingFastSamples > 0 ? 600 : 2000;
            if (!_sizingDetectTimer.Enabled) _sizingDetectTimer.Start();
        }

        private int _sizingFastSamples;

        // Ogni due secondi: prima il renderer (madVR con il suo rilevamento delle bande), altrimenti un
        // fotogramma piccolo decodificato a parte nella posizione corrente. Il fotogramma si legge fuori
        // dal thread dell'interfaccia; il formato cambia solo dopo due misure concordi.
        private void SampleActiveAspect()
        {
            if (_sizingFastSamples > 0 && --_sizingFastSamples == 0 && _sizingDetectTimer != null) _sizingDetectTimer.Interval = 2000;
            if (Math.Abs(ImageSizingTargetScale() - (_sizingAnimation?.Enabled == true ? _sizingTo : _sizingScale)) > 0.002) ApplyImageSizing(animate: true);
            if (!ImageSizingApplies() || !_imageSizing.DetectBars || _paused || _engine is not IVideoScaleEngine scaler || _sizingAspect is not AspectStabilizer stabilizer)
                return;
            if (scaler.RendererActiveAspect() is double fromRenderer)
            {
                if (stabilizer.Offer(fromRenderer) | SizingExpandedChanged(stabilizer)) ApplyImageSizing(animate: true);
                return;
            }
            string? path = _currentPath;
            if (string.IsNullOrEmpty(path) || !_isLocalFile || DiscMedia.TryResolve(path, out _)) { stabilizer.Offer(null); return; }
            if (Interlocked.Exchange(ref _sizingDetectBusy, 1) != 0) return;
            double position = _lastKnownPlaybackPosition, frameAspect = SizingFrameAspect();
            try { position = _engine.PositionSeconds; } catch { }
            _ = Task.Run(() =>
            {
                double? sample = null;
                try
                {
                    var frames = _sizingFrames;
                    if (frames == null) { frames = new Thumbnailer(); frames.Open(path); _sizingFrames = frames; }
                    using var frame = frames.Get(position, 256, CancellationToken.None, realtime: true);
                    if (frame != null) sample = ImageSizing.DetectActiveAspect(frame, frameAspect);
                }
                catch (Exception ex) { Dbg.Warn("[SIZING] sample: " + ex.Message); }
                finally { Interlocked.Exchange(ref _sizingDetectBusy, 0); }
                TryBeginInvokeOnUi(() =>
                {
                    if (!ReferenceEquals(stabilizer, _sizingAspect)) return;
                    if (sample != null) _sizingSamples++;
                    if (stabilizer.Offer(sample) | SizingExpandedChanged(stabilizer))
                    {
                        Dbg.Log($"[SIZING] active aspect {stabilizer.Current:0.00} (usual {stabilizer.Usual:0.00}, expanded {stabilizer.Expanded})", Dbg.LogLevel.Info);
                        ApplyImageSizing(animate: true);
                    }
                });
            });
        }

        private bool _sizingWasExpanded;
        private bool SizingExpandedChanged(AspectStabilizer stabilizer)
        {
            bool expanded = stabilizer.Expanded;
            if (expanded == _sizingWasExpanded) return false;
            _sizingWasExpanded = expanded;
            return true;
        }

        private void StopImageSizing()
        {
            _sizingDetectTimer?.Stop();
            _sizingAnimation?.Stop();
            var frames = _sizingFrames;
            _sizingFrames = null;
            _sizingPath = null;
            _sizingAspect = null;
            _sizingScale = 1;
            _sizingWasExpanded = false;
            if (frames != null) _ = Task.Run(() => { try { frames.Dispose(); } catch { } });
        }

        /// <summary>Tasto rapido (Z): passa alla modalita' successiva e la mostra per un momento.</summary>
        private void CycleImageSizingMode()
        {
            _imageSizing.Mode = (ImageSizingMode)(((int)_imageSizing.Mode + 1) % 4);
            _imageSizing.Save();
            ApplyImageSizing(animate: true);
            string note = _imageSizing.Mode != ImageSizingMode.Fill && !IsFullscreenOverlayMode()
                ? Tx("  ·  attiva a schermo intero", "  ·  active in full screen") : string.Empty;
            ShowRemoteOsd(null, null, 1600, Tx("Immagine: ", "Image: ") + ImageSizingModeName(_imageSizing.Mode, UiEnglish) + note);
            try { _settingsHudPage?.Invalidate(); } catch { }
        }

        /// <summary>Le opzioni sono cambiate dalla pagina Impostazioni.</summary>
        internal void ImageSizingChanged()
        {
            _imageSizing.Save();
            ApplyImageSizing(animate: true);
        }
    }
}
