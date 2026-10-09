#nullable enable
using CinecorePlayer2025;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025.Engines
{
    internal enum ImageViewMode
    {
        Contain,
        Fill,
        ActualSize
    }

    /// <summary>
    /// A decoded photo ready for display. <see cref="Bitmap"/> is screen-sized unless
    /// <see cref="IsFullResolution"/>; <see cref="SourceSize"/> is always the original size.
    /// </summary>
    internal sealed class PreparedPhoto : IDisposable
    {
        public PreparedPhoto(Bitmap bitmap, Size sourceSize)
        {
            Bitmap = bitmap;
            SourceSize = sourceSize;
        }

        public Bitmap Bitmap { get; }
        public Size SourceSize { get; }
        public bool IsFullResolution => Bitmap.Width >= SourceSize.Width && Bitmap.Height >= SourceSize.Height;
        public void Dispose() { try { Bitmap.Dispose(); } catch { } }
    }

    /// <summary>
    /// Photo playback with an owned, repaintable surface and bounded adjacent cache.
    /// Photos are decoded at screen size and upgraded to full resolution only when the
    /// view needs more pixels; the composed viewport is cached so that HUD fades and other
    /// overlay repaints are a single unscaled blit instead of a full-image resample.
    /// </summary>
    internal sealed class ImagePlaybackEngine : IPlaybackEngine
    {
        private const int CacheCapacity = 4;

        private Bitmap? _bitmap;
        private Size _sourceSize;
        private int _quarterTurns;
        private int _bitmapVersion;
        private bool _fullResolutionRequested;
        private string? _path;
        private Action? _updateCb;
        private ImageViewMode _viewMode = ImageViewMode.Contain;
        private PhotoSurface? _surface;
        private Control? _photoOverlay, _overlayParent;
        private Color _overlayBackColor;
        private double _zoom = 1;
        private PointF _pan;
        private bool _interactive;

        private Bitmap? _frame;
        private (int Version, Rectangle Dest, Size Client, bool Fast) _frameKey;

        internal event Action<int>? NavigateRequested;
        internal event Action? Interaction;
        internal event Action? ViewChanged;
        internal double Zoom => _zoom;

        private readonly object _preloadSync = new();
        // Most recently used photos (adjacent preloads and the ones just left), so that
        // going back and forth never decodes again.
        private readonly LinkedList<(string Path, PreparedPhoto Photo)> _cache = new();
        private readonly HashSet<string> _preloading = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _wantedPreloads = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;
        private static readonly HttpClient PhotoHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

        public event Action<double>? OnProgressSeconds { add { } remove { } }
        public event Action<string>? OnStatus;
        public event Action<bool>? OnBitstreamChanged { add { } remove { } }

        public static readonly string[] SupportedExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

        public static bool IsImageFile(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") path = uri.AbsolutePath;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(SupportedExtensions, ext) >= 0;
        }

        public void Open(string mediaPath, bool hasVideo)
        {
            var photo = TakeCached(mediaPath) ?? LoadPhoto(mediaPath, DisplayFit());
            OpenPrepared(mediaPath, photo);
        }

        public void Play() { /* no-op per le foto */ }

        internal Task<PreparedPhoto> PrepareImageAsync(string path)
        {
            var cached = TakeCached(path);
            if (cached != null) return Task.FromResult(cached);
            Size fit = DisplayFit();
            return Task.Run(() => LoadPhoto(path, fit));
        }

        internal void OpenPrepared(string path, PreparedPhoto photo)
        {
            RetireCurrent();
            _path = path;
            _bitmap = photo.Bitmap;
            _sourceSize = photo.SourceSize;
            _quarterTurns = 0;
            _fullResolutionRequested = photo.IsFullResolution;
            _bitmapVersion++;
            _zoom = 1; _pan = PointF.Empty;
            InvalidateSurface();
            OnStatusSafe("Immagine: " + Path.GetFileName(path)); _updateCb?.Invoke();
        }

        public void Pause() { /* no-op */ }
        public void Stop() { /* no-op */ }

        public double DurationSeconds => 0;

        public double PositionSeconds
        {
            get => 0;
            set { /* ignorato per le foto */ }
        }

        public void SetVolume(float volume)
        {
            // niente audio nelle foto → noop
        }

        /// <summary>
        /// Disegna l'immagine nel viewport usando la modalità scelta. Contain è il
        /// default perché evita il vecchio crop 1:1 sui display piccoli/HiDPI.
        /// </summary>
        public void UpdateVideoWindow(nint ownerHwnd, Rectangle ownerClient)
        {
            if (_bitmap == null || _disposed) return;
            if (ownerClient.Width <= 0 || ownerClient.Height <= 0) return;
            if (Control.FromHandle(ownerHwnd) is Control owner)
            {
                if (_surface == null || _surface.IsDisposed)
                    _surface = new PhotoSurface(this) { Dock = DockStyle.Fill, BackColor = Color.Black };
                if (_surface.Parent != owner)
                {
                    owner.Controls.Add(_surface);
                    _surface.BringToFront();
                }
                InvalidateSurface();
            }
        }

        private void InvalidateSurface()
        {
            // The HUD is a transparent child covering the whole surface: repaint it too.
            try { _surface?.Invalidate(true); } catch { }
        }

        private double BaseScale(Size client) => _viewMode switch
        {
            ImageViewMode.Fill => Math.Max(client.Width / (double)_sourceSize.Width, client.Height / (double)_sourceSize.Height),
            ImageViewMode.ActualSize => 1.0,
            _ => Math.Min(1.0, Math.Min(client.Width / (double)_sourceSize.Width, client.Height / (double)_sourceSize.Height))
        };

        private Rectangle ComputeDest(Size client)
        {
            double scale = BaseScale(client);
            if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;
            scale *= _zoom;

            int dstW = Math.Max(1, (int)Math.Round(_sourceSize.Width * scale));
            int dstH = Math.Max(1, (int)Math.Round(_sourceSize.Height * scale));
            float maxX = Math.Max(0, (dstW - client.Width) / 2f);
            float maxY = Math.Max(0, (dstH - client.Height) / 2f);
            _pan = new PointF(Math.Clamp(_pan.X, -maxX, maxX), Math.Clamp(_pan.Y, -maxY, maxY));
            return new Rectangle((client.Width - dstW) / 2 + (int)_pan.X, (client.Height - dstH) / 2 + (int)_pan.Y, dstW, dstH);
        }

        private void PaintImage(Graphics g, Rectangle ownerClient)
        {
            if (_bitmap == null || _sourceSize.IsEmpty || ownerClient.Width < 1 || ownerClient.Height < 1)
            {
                g.Clear(Color.Black);
                return;
            }

            Size client = ownerClient.Size;
            Rectangle dest = ComputeDest(client);
            RequestFullResolutionIfNeeded(dest);

            var key = (_bitmapVersion, dest, client, _interactive);
            if (_frame == null || _frameKey != key)
            {
                // A fast frame is good enough while dragging; the idle repaint replaces it.
                if (_frame != null && (_frame.Width != client.Width || _frame.Height != client.Height))
                {
                    _frame.Dispose();
                    _frame = null;
                }
                _frame ??= new Bitmap(client.Width, client.Height, PixelFormat.Format32bppPArgb);
                ComposeFrame(_frame, dest, client, _interactive);
                _frameKey = key;
            }

            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(_frame, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
        }

        private void ComposeFrame(Bitmap frame, Rectangle dest, Size client, bool fast)
        {
            using var g = Graphics.FromImage(frame);
            g.Clear(Color.Black);
            var visible = Rectangle.Intersect(dest, new Rectangle(Point.Empty, client));
            if (visible.IsEmpty || _bitmap == null) return;

            // Map only the visible part of the photo back to bitmap pixels: at high zoom
            // this resamples a viewport-sized region instead of the whole picture.
            double sx = _bitmap.Width / (double)dest.Width;
            double sy = _bitmap.Height / (double)dest.Height;
            var src = new RectangleF(
                (float)((visible.X - dest.X) * sx), (float)((visible.Y - dest.Y) * sy),
                (float)(visible.Width * sx), (float)(visible.Height * sy));

            bool identity = visible.Width == (int)Math.Round(src.Width) && visible.Height == (int)Math.Round(src.Height);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = identity ? InterpolationMode.NearestNeighbor
                : fast ? InterpolationMode.Bilinear
                : sx > 1.0 ? InterpolationMode.HighQualityBicubic
                : InterpolationMode.HighQualityBilinear;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(_bitmap, visible, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attributes);
        }

        private void RequestFullResolutionIfNeeded(Rectangle dest)
        {
            if (_bitmap == null || _fullResolutionRequested || _path == null) return;
            if (dest.Width <= _bitmap.Width * 1.05 && dest.Height <= _bitmap.Height * 1.05) return;

            _fullResolutionRequested = true;
            string path = _path;
            int version = _bitmapVersion;
            _ = Task.Run(() =>
            {
                PreparedPhoto? full = null;
                try { full = LoadPhoto(path, Size.Empty); } catch { }
                if (full == null) return;
                var surface = _surface;
                if (surface == null || surface.IsDisposed || !surface.IsHandleCreated) { full.Dispose(); return; }
                try
                {
                    surface.BeginInvoke(new Action(() =>
                    {
                        if (_disposed || version != _bitmapVersion || !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
                        {
                            full.Dispose();
                            return;
                        }
                        var bitmap = full.Bitmap;
                        for (int i = 0; i < _quarterTurns; i++) bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
                        try { _bitmap?.Dispose(); } catch { }
                        _bitmap = bitmap;
                        _bitmapVersion++;
                        InvalidateSurface();
                    }));
                }
                catch { full.Dispose(); }
            });
        }

        public Rectangle GetLastDestRectAsClient(Rectangle ownerClient)
        {
            // Photo controls belong to the viewport, including portrait letterboxing.
            return new Rectangle(0, 0, ownerClient.Width, ownerClient.Height);
        }

        public void SetStereo3D(Stereo3DMode mode)
        {
            // non ha senso per le foto, ignora
        }

        public void SetUpscaling(bool enable)
        {
            // Ignorato: le foto sono sempre renderizzate 1:1 (nessuno scaling).
        }

        public void BindUpdateCallback(Action? cb) => _updateCb = cb;

        public ImageViewMode CycleViewMode()
        {
            _zoom = 1; _pan = PointF.Empty;
            _viewMode = _viewMode switch
            {
                ImageViewMode.Contain => ImageViewMode.Fill,
                ImageViewMode.Fill => ImageViewMode.ActualSize,
                _ => ImageViewMode.Contain
            };
            InvalidateSurface();
            ViewChanged?.Invoke();
            return _viewMode;
        }

        internal void ZoomBy(double factor) => ZoomAt(factor, null);

        /// <summary>Zooms keeping the photo point under <paramref name="anchor"/> still.</summary>
        internal void ZoomAt(double factor, Point? anchor)
        {
            double next = Math.Clamp(_zoom * factor, 1, 16);
            if (Math.Abs(next - _zoom) < 0.0001) return;
            double k = next / _zoom;
            _zoom = next;
            if (_zoom <= 1.0001) _pan = PointF.Empty;
            else if (anchor is Point p && _surface != null)
            {
                float cx = _surface.ClientSize.Width / 2f, cy = _surface.ClientSize.Height / 2f;
                _pan = new PointF((float)(_pan.X * k + (p.X - cx) * (1 - k)), (float)(_pan.Y * k + (p.Y - cy) * (1 - k)));
            }
            else _pan = new PointF((float)(_pan.X * k), (float)(_pan.Y * k));
            InvalidateSurface(); ViewChanged?.Invoke();
        }

        internal void ResetView()
        {
            _zoom = 1; _pan = PointF.Empty; _viewMode = ImageViewMode.Contain;
            InvalidateSurface(); ViewChanged?.Invoke();
        }

        internal void AttachOverlay(Control overlay)
        {
            if (_surface == null || overlay.Parent == _surface) return;
            _photoOverlay = overlay; _overlayParent = overlay.Parent; _overlayBackColor = overlay.BackColor;
            overlay.BackColor = Color.Transparent;
            _surface.Controls.Add(overlay); overlay.Dock = DockStyle.Fill; overlay.BringToFront();
        }

        /// <summary>Mouse input forwarded by the transparent HUD (wheel, drag, double click).</summary>
        internal void HandleWheel(int delta, Point location, bool control)
        {
            Interaction?.Invoke();
            if (delta == 0) return;
            // Once zoomed, the wheel keeps zooming (so it can also zoom back out);
            // at fit size it browses the folder, like the arrow keys.
            if (control || _zoom > 1.0001) ZoomAt(delta > 0 ? 1.25 : 1 / 1.25, location);
            else NavigateRequested?.Invoke(delta > 0 ? -1 : 1);
        }

        internal void HandleDoubleClick(Point location)
        {
            Interaction?.Invoke();
            if (_zoom > 1.0001) ResetView();
            else ZoomAt(2.5, location);
        }

        internal bool CanPan => _zoom > 1.0001 || _viewMode != ImageViewMode.Contain;

        internal void BeginPan() { _interactive = true; }

        internal void PanBy(int dx, int dy)
        {
            _pan.X += dx; _pan.Y += dy;
            _surface?.Invalidate(true);
            _surface?.Update();
        }

        internal void EndPan()
        {
            if (!_interactive) return;
            _interactive = false;
            InvalidateSurface();
        }

        private sealed class PhotoSurface : Control
        {
            private readonly ImagePlaybackEngine _owner;
            private Point _drag;
            private bool _dragging;
            public PhotoSurface(ImagePlaybackEngine owner)
            {
                _owner = owner; DoubleBuffered = true; TabStop = true;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
                AccessibleName = global::CinecorePlayer2025.Utilities.AppLanguage.T("Foto: frecce per navigare, rotella o Ctrl+rotella per zoom, trascina per spostare", "Photo: arrows to navigate, wheel or Ctrl+wheel to zoom, drag to pan");
            }
            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e) => _owner.PaintImage(e.Graphics, ClientRectangle);
            protected override void OnMouseWheel(MouseEventArgs e) => _owner.HandleWheel(e.Delta, e.Location, (ModifierKeys & Keys.Control) != 0);
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e); Focus(); _owner.Interaction?.Invoke();
                if (e.Button != MouseButtons.Left || !_owner.CanPan) return;
                _drag = e.Location; _dragging = true; Capture = true; Cursor = Cursors.SizeAll;
                _owner.BeginPan();
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e); _owner.Interaction?.Invoke();
                if (!_dragging) return;
                _owner.PanBy(e.X - _drag.X, e.Y - _drag.Y);
                _drag = e.Location;
            }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); StopDrag(); }
            protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) StopDrag(); }
            protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (e.Button == MouseButtons.Left) _owner.HandleDoubleClick(e.Location); }
            private void StopDrag()
            {
                if (!_dragging) return;
                _dragging = false; Capture = false; Cursor = Cursors.Default;
                _owner.EndPan();
            }
        }

        public ImageViewMode ViewMode => _viewMode;

        public void RotateClockwise()
        {
            if (_bitmap == null)
                return;
            _bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
            _sourceSize = new Size(_sourceSize.Height, _sourceSize.Width);
            _quarterTurns = (_quarterTurns + 1) % 4;
            _bitmapVersion++;
            _pan = PointF.Empty;
            InvalidateSurface();
            ViewChanged?.Invoke();
        }

        public void QueuePreload(IEnumerable<string> mediaPaths)
        {
            if (_disposed)
                return;

            string[] wanted = (mediaPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path) && (IsRemoteImage(path) || File.Exists(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(CacheCapacity - 1)
                .ToArray();

            lock (_preloadSync)
            {
                _wantedPreloads.Clear();
                foreach (string path in wanted)
                    _wantedPreloads.Add(path);
                TrimCacheLocked();
            }

            Size fit = DisplayFit();
            foreach (string path in wanted)
            {
                lock (_preloadSync)
                {
                    if (FindCachedLocked(path) != null || !_preloading.Add(path))
                        continue;
                }

                _ = Task.Run(() =>
                {
                    PreparedPhoto? decoded = null;
                    try { decoded = LoadPhoto(path, fit); } catch { }
                    lock (_preloadSync)
                    {
                        _preloading.Remove(path);
                        if (!_disposed && decoded != null && _wantedPreloads.Contains(path) && FindCachedLocked(path) == null)
                        {
                            _cache.AddFirst((path, decoded));
                            decoded = null;
                            TrimCacheLocked();
                        }
                    }
                    decoded?.Dispose();
                });
            }
        }

        private PreparedPhoto? TakeCached(string path)
        {
            lock (_preloadSync)
            {
                var node = FindCachedLocked(path);
                if (node == null) return null;
                _cache.Remove(node);
                return node.Value.Photo;
            }
        }

        private LinkedListNode<(string Path, PreparedPhoto Photo)>? FindCachedLocked(string path)
        {
            for (var node = _cache.First; node != null; node = node.Next)
                if (string.Equals(node.Value.Path, path, StringComparison.OrdinalIgnoreCase)) return node;
            return null;
        }

        private void TrimCacheLocked()
        {
            // Keep wanted neighbours first, then the most recently viewed photos.
            while (_cache.Count > CacheCapacity)
            {
                var victim = _cache.Last;
                for (var node = _cache.Last; node != null; node = node.Previous)
                    if (!_wantedPreloads.Contains(node.Value.Path)) { victim = node; break; }
                _cache.Remove(victim!);
                victim!.Value.Photo.Dispose();
            }
        }

        /// <summary>Keeps the photo being left (unrotated, screen-sized) for a quick return.</summary>
        private void RetireCurrent()
        {
            var bitmap = _bitmap;
            string? path = _path;
            _bitmap = null;
            _path = null;
            if (bitmap == null) return;
            bool reusable = path != null && _quarterTurns == 0 && !_disposed && bitmap.Width * (long)bitmap.Height <= 16_000_000;
            if (!reusable)
            {
                try { bitmap.Dispose(); } catch { }
                return;
            }
            lock (_preloadSync)
            {
                if (FindCachedLocked(path!) is { } existing) { _cache.Remove(existing); existing.Value.Photo.Dispose(); }
                _cache.AddFirst((path!, new PreparedPhoto(bitmap, _sourceSize)));
                TrimCacheLocked();
            }
        }

        private static Size DisplayFit()
        {
            int w = 0, h = 0;
            try
            {
                foreach (var screen in Screen.AllScreens)
                {
                    w = Math.Max(w, screen.Bounds.Width);
                    h = Math.Max(h, screen.Bounds.Height);
                }
            }
            catch { }
            return w > 0 && h > 0 ? new Size(w, h) : new Size(3840, 2160);
        }

        private static PreparedPhoto LoadPhoto(string path, Size fit)
        {
            byte[]? data = null;
            if (IsRemoteImage(path))
                data = PhotoHttp.GetByteArrayAsync(path).GetAwaiter().GetResult();
            var bitmap = ImageAssetDecoder.LoadForDisplay(data, data == null ? path : null, fit, out Size sourceSize);
            return new PreparedPhoto(bitmap, sourceSize);
        }

        internal static bool IsRemoteImage(string path)
            => Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

        public bool IsBitstreamActive() => false;

        public bool HasDisplayControl() => true; // disegna lui stesso

        public (string text, DateTime when) GetLastVideoMTDump()
            => ("Image file - nessun MediaType DirectShow", DateTime.MinValue);

        public (int width, int height, string subtype) GetNegotiatedVideoFormat()
        {
            if (_bitmap == null) return (0, 0, "image");
            return (_sourceSize.Width, _sourceSize.Height, "image");
        }

        public (int bytes, DateTime when) GetLastSnapshotInfo()
            => (0, DateTime.MinValue);

        public List<DsStreamItem> EnumerateStreams() => new();

        public bool EnableByGlobalIndex(int globalIndex) => false;

        public bool DisableSubtitlesIfPossible() => false;

        /// <summary>
        /// Volendo potresti serializzare la bitmap a BMP/JPEG in memoria.
        /// Per ora ritorna sempre false.
        /// </summary>
        public bool TrySnapshot(out int byteCount)
        {
            byteCount = 0;
            return false;
        }

        public Bitmap? GetPreviewFrame(double seconds, int maxW = 360)
        {
            if (_bitmap == null) return null;
            try
            {
                double scale = maxW / (double)_bitmap.Width;
                int w = maxW;
                int h = Math.Max(1, (int)Math.Round(_bitmap.Height * scale));

                var thumb = new Bitmap(w, h);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_bitmap, new Rectangle(0, 0, w, h));
                }
                return thumb;
            }
            catch
            {
                return null;
            }
        }

        // madVR: per le foto non ha senso → noop
        public void SetMadVrChroma(MadVrCategoryPreset preset) { }
        public void SetMadVrImageUpscale(MadVrCategoryPreset preset) { }
        public void SetMadVrImageDownscale(MadVrCategoryPreset preset) { }
        public void SetMadVrRefinement(MadVrCategoryPreset preset) { }
        public void SetMadVrFps(MadVrFpsChoice choice) { }
        public void SetMadVrHdrMode(MadVrHdrMode mode) { }
        public bool TrySendRendererKey(Keys keyData) => false;
        public bool TrySetMadVrExclusiveModeDisabled(bool disabled) => false;
        public string GetRendererDiagnosticSnapshot() => "image renderer";

        public void Dispose()
        {
            if (_photoOverlay != null && !_photoOverlay.IsDisposed)
            {
                _photoOverlay.Parent = _overlayParent?.IsDisposed == false ? _overlayParent : null;
                _photoOverlay.BackColor = _overlayBackColor;
            }
            _photoOverlay = null; _overlayParent = null;
            _surface?.Dispose(); _surface = null;
            try { _bitmap?.Dispose(); } catch { }
            _bitmap = null;
            _path = null;
            try { _frame?.Dispose(); } catch { }
            _frame = null;
            lock (_preloadSync)
            {
                _disposed = true;
                foreach (var entry in _cache)
                    entry.Photo.Dispose();
                _cache.Clear();
                _preloading.Clear();
                _wantedPreloads.Clear();
            }
        }

        private void OnStatusSafe(string s)
        {
            try { OnStatus?.Invoke(s); }
            catch { }
        }
    }
}
