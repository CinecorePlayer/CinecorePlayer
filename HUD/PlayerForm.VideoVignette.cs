#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private MpcvrBitmapOverlayForm? _videoVignette;
        private Rectangle _vignetteBounds;
        private int _vignetteOpacity = -1;
        private Bitmap? _vignetteBitmap;
        private Size _vignetteBuilding;

        // Mentre un film e' a schermo la finestra della vignettatura resta sempre presente, trasparente
        // quando l'HUD e' nascosto. Farla comparire e sparire a ogni apertura dell'HUD cambiava il modo in
        // cui Windows presenta il video (diretto / composto) e il film scattava in quel momento.
        private bool VignetteStaysWarm()
        {
            try
            {
                return !_closingForExit && !_nativeChromeInteraction && _engine is Engines.DirectShowUnifiedEngine &&
                    _currentMediaHasVideo && !IsPhotoMode && !_useInlineOverlay && !_pipModeActive &&
                    !ShouldSuppressExternalPlaybackOverlays() && _settingsHudPage?.Visible != true &&
                    !IsAnyLibraryVisible() && WindowState != System.Windows.Forms.FormWindowState.Minimized && IsCinecoreForeground();
            }
            catch { return false; }
        }

        private void UpdateVideoVignette()
        {
            bool warm = VignetteStaysWarm();
            if (_closingForExit || !_currentMediaHasVideo || (_hud?.RequestedVisible != true && !warm))
            {
                if (_vignetteOpacity >= 0) _videoVignette?.HideOverlay();
                _vignetteOpacity = -1;
                return;
            }
            if (_nativeChromeInteraction || _audioSyncDialogOpen || _contextMenuActive || _contextMenuPending) return;
            bool show = !_closingForExit && !_nativeChromeInteraction && _engine != null && _currentMediaHasVideo &&
                !IsPhotoMode && _hud?.RequestedVisible == true && !ShouldSuppressExternalPlaybackOverlays() &&
                _settingsHudPage?.Visible != true && IsCinecoreForeground();
            int opacity = show ? Math.Max(0, (int)Math.Round(_hud!.RenderOpacity * 64)) : 0;
            if (opacity <= 0 && !warm)
            {
                _videoVignette?.HideOverlay();
                _vignetteOpacity = -1;
                return;
            }
            var bounds = GetMpcvrOverlayScreenRect();
            if (bounds.Width < 2 || bounds.Height < 2) return;
            _videoVignette ??= new MpcvrBitmapOverlayForm { PassThroughAllInput = true };
            if (_vignetteBounds == bounds && _vignetteOpacity == opacity && _videoVignette.Visible) return;
            if (_vignetteBitmap == null || _vignetteBitmap.Size != bounds.Size)
            {
                // L'immagine della vignettatura si calcola pixel per pixel (a 4K sono centinaia di
                // millisecondi): fuori dal thread dell'interfaccia. Finche' non e' pronta non c'e'.
                if (_vignetteBuilding == bounds.Size) return;
                _vignetteBuilding = bounds.Size;
                Size wanted = bounds.Size;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    Bitmap? built = null;
                    try { built = CreateVideoVignette(wanted, 1f); } catch { }
                    TryBeginInvokeOnUi(() =>
                    {
                        if (built == null || _vignetteBuilding != wanted || _closingForExit) { built?.Dispose(); return; }
                        _vignetteBitmap?.Dispose();
                        _vignetteBitmap = built;
                        _vignetteBuilding = Size.Empty;
                        _vignetteOpacity = -1;
                        UpdateVideoVignette();
                    });
                });
                return;
            }
            bool firstFrame = !_videoVignette.Visible || _vignetteBounds != bounds;
            _vignetteBounds = bounds;
            _vignetteOpacity = opacity;
            _videoVignette.ShowBitmap(this, bounds, _vignetteBitmap, Color.Magenta, true, (byte)(opacity * 255 / 64));
            // The translucent surface sits underneath all interactive controls. Solo alla
            // comparsa: rialzare l'HUD a ogni passo di fade causava sfarfallio.
            if (firstFrame && _overlayHost?.Visible == true) _overlayHost.RaiseAboveOwner();
        }

        internal static unsafe Bitmap CreateVideoVignette(Size size, float opacity)
        {
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < size.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    // Vignettatura più presente: i comandi restano leggibili anche su scene chiare.
                    float top = Math.Clamp(1f - y / Math.Max(1f, size.Height * .38f), 0, 1);
                    float bottom = Math.Clamp((y / (float)size.Height - .38f) / .62f, 0, 1);
                    float shade = Math.Max(.44f * top * top, .72f * bottom * bottom * (3 - 2 * bottom));
                    for (int x = 0; x < size.Width; x++)
                    {
                        float side = Math.Clamp(1f - Math.Min(x, size.Width - x - 1) / Math.Max(1f, size.Width * .08f), 0, 1);
                        uint hash = unchecked((uint)(x * 374761393 + y * 668265263));
                        float noise = ((hash ^ (hash >> 13)) & 255) / 255f - .5f;
                        int alpha = (int)Math.Clamp((shade + .16f * side * side * (1 - shade)) * opacity * 255 + noise, 0, 255);
                        row[x * 4] = 0; row[x * 4 + 1] = 0; row[x * 4 + 2] = 0; row[x * 4 + 3] = (byte)alpha;
                    }
                }
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
    }
}
