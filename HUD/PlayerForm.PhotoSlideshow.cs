using CinecorePlayer2025.Engines;
using System;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private const int PhotoSlideshowIntervalMs = 4500;
        private System.Windows.Forms.Timer? _photoSlideshowTimer;

        private bool IsPhotoSlideshowRunning => _photoSlideshowTimer?.Enabled == true;

        private void TogglePhotoSlideshow()
        {
            if (IsPhotoSlideshowRunning) StopPhotoSlideshow();
            else StartPhotoSlideshow();
        }

        private void StartPhotoSlideshow()
        {
            if (!IsPhotoMode || _imageFiles.Count < 2) return;
            if (_photoSlideshowTimer == null)
            {
                _photoSlideshowTimer = new System.Windows.Forms.Timer { Interval = PhotoSlideshowIntervalMs };
                _photoSlideshowTimer.Tick += (_, __) => AdvancePhotoSlideshow();
            }
            _photoSlideshowTimer.Start();
            try { _photoHud?.SetSlideshow(true); _photoHud?.Sleep(); } catch { }
        }

        private void StopPhotoSlideshow()
        {
            if (_photoSlideshowTimer == null) return;
            _photoSlideshowTimer.Stop();
            try { _photoHud?.SetSlideshow(false); } catch { }
        }

        /// <summary>Manual navigation during a slideshow restarts the interval.</summary>
        private void RestartPhotoSlideshowInterval()
        {
            if (!IsPhotoSlideshowRunning) return;
            _photoSlideshowTimer!.Stop();
            _photoSlideshowTimer.Start();
        }

        private void AdvancePhotoSlideshow()
        {
            if (!IsPhotoMode || _engine is not ImagePlaybackEngine || _imageFiles.Count < 2)
            {
                StopPhotoSlideshow();
                return;
            }
            // A zoomed-in photo is being inspected: wait for the next tick.
            if (_engine is ImagePlaybackEngine image && image.Zoom > 1.0001) return;
            ShowNextImage(fromSlideshow: true);
        }
    }
}
