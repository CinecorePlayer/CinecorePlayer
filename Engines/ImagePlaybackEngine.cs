#nullable enable
using CinecorePlayer2025;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
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
    /// Photo playback with an owned, repaintable surface and bounded adjacent cache.
    /// </summary>
    internal sealed class ImagePlaybackEngine : IPlaybackEngine
    {
        private Bitmap? _bitmap;
        private string? _path;
        private Rectangle _lastDest;
        private Action? _updateCb;
        private ImageViewMode _viewMode = ImageViewMode.Contain;
        private PhotoSurface? _surface;
        private Control? _photoOverlay, _overlayParent;
        private Color _overlayBackColor;
        private double _zoom = 1;
        private PointF _pan;
        internal event Action<int>? NavigateRequested;
        internal event Action? Interaction;
        internal event Action? ViewChanged;
        internal double Zoom => _zoom;
        private readonly object _preloadSync = new();
        private readonly Dictionary<string, Bitmap> _preloaded = new(StringComparer.OrdinalIgnoreCase);
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
            DisposeBitmap();

            _path = mediaPath;

            lock (_preloadSync)
            {
                if (_preloaded.Remove(mediaPath, out Bitmap? ready))
                    _bitmap = ready;
            }

            // La prima immagine viene decodificata normalmente; la navigazione
            // successiva usa la piccola cache dei due elementi adiacenti.
            _bitmap ??= LoadBitmapFile(mediaPath);

            OnStatusSafe("Immagine: " + Path.GetFileName(mediaPath));
            // Nessun tempo di riproduzione, niente timer di progress
            _updateCb?.Invoke();
        }

        public void Play() { /* no-op per le foto */ }

        internal Task<Bitmap> PrepareImageAsync(string path)
        {
            lock (_preloadSync)
                if (_preloaded.Remove(path, out var ready)) return Task.FromResult(ready);
            return Task.Run(() => LoadBitmapFile(path));
        }

        internal void OpenPrepared(string path, Bitmap bitmap)
        {
            DisposeBitmap(); _path = path; _bitmap = bitmap;
            _zoom = 1; _pan = PointF.Empty;
            _surface?.Invalidate();
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
                _surface.Invalidate();
            }
        }

        private void PaintImage(Graphics g, Rectangle ownerClient)
        {
            g.Clear(Color.Black);
            if (_bitmap == null || ownerClient.Width < 1 || ownerClient.Height < 1) return;
            {
                var clientRect = new Rectangle(0, 0, ownerClient.Width, ownerClient.Height);

                int imgW = _bitmap.Width;
                int imgH = _bitmap.Height;

                double scale = _viewMode switch
                {
                    ImageViewMode.Fill => Math.Max(ownerClient.Width / (double)imgW, ownerClient.Height / (double)imgH),
                    ImageViewMode.ActualSize => 1.0,
                    _ => Math.Min(ownerClient.Width / (double)imgW, ownerClient.Height / (double)imgH)
                };
                if (!double.IsFinite(scale) || scale <= 0)
                    scale = 1.0;
                scale *= _zoom;

                int dstW = Math.Max(1, (int)Math.Round(imgW * scale));
                int dstH = Math.Max(1, (int)Math.Round(imgH * scale));

                // Centro l'immagine nel pannello
                float maxX = Math.Max(0, (dstW - ownerClient.Width) / 2f);
                float maxY = Math.Max(0, (dstH - ownerClient.Height) / 2f);
                _pan = new PointF(Math.Clamp(_pan.X, -maxX, maxX), Math.Clamp(_pan.Y, -maxY, maxY));
                int dx = (ownerClient.Width - dstW) / 2 + (int)_pan.X;
                int dy = (ownerClient.Height - dstH) / 2 + (int)_pan.Y;

                // Coordinate client (0,0 è l'angolo in alto a sinistra del pannello)
                var dest = new Rectangle(dx, dy, dstW, dstH);

                g.InterpolationMode = scale < 1.0
                    ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear
                    : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                g.DrawImage(_bitmap, dest);
                // Draw the vignette with the photograph, once per real repaint.
                int edge = Math.Min(140, ownerClient.Height / 4);
                if (edge > 0)
                {
                    using var top = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, ownerClient.Width, edge), Color.FromArgb(125, 0, 0, 0), Color.Transparent, 90f);
                    using var bottom = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, ownerClient.Height - edge, ownerClient.Width, edge), Color.Transparent, Color.FromArgb(145, 0, 0, 0), 90f);
                    g.FillRectangle(top, 0, 0, ownerClient.Width, edge);
                    g.FillRectangle(bottom, 0, ownerClient.Height - edge, ownerClient.Width, edge);
                }


                // Salvo la dest rect in coordinate client del pannello, clippata ai bordi
                var visible = Rectangle.Intersect(dest, clientRect);
                _lastDest = visible.Width > 0 && visible.Height > 0 ? visible : clientRect;
            }
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
            _updateCb?.Invoke();
            _surface?.Invalidate();
            return _viewMode;
        }

        internal void ZoomBy(double factor)
        {
            _zoom = Math.Clamp(_zoom * factor, 0.25, 16);
            _surface?.Invalidate(); ViewChanged?.Invoke();
        }

        internal void ResetView()
        {
            _zoom = 1; _pan = PointF.Empty; _viewMode = ImageViewMode.Contain;
            _surface?.Invalidate(); ViewChanged?.Invoke();
        }

        internal void AttachOverlay(Control overlay)
        {
            if (_surface == null || overlay.Parent == _surface) return;
            _photoOverlay = overlay; _overlayParent = overlay.Parent; _overlayBackColor = overlay.BackColor;
            overlay.BackColor = Color.Transparent;
            _surface.Controls.Add(overlay); overlay.Dock = DockStyle.Fill; overlay.BringToFront();
        }

        private sealed class PhotoSurface : Control
        {
            private readonly ImagePlaybackEngine _owner;
            private Point _drag;
            private bool _dragging;
            public PhotoSurface(ImagePlaybackEngine owner)
            {
                _owner = owner; DoubleBuffered = true; TabStop = true;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
                AccessibleName = "Foto: frecce per navigare, Ctrl+rotella per zoom, trascina per spostare";
            }
            protected override void OnPaint(PaintEventArgs e) => _owner.PaintImage(e.Graphics, ClientRectangle);
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                _owner.Interaction?.Invoke();
                if ((ModifierKeys & Keys.Control) != 0) _owner.ZoomBy(e.Delta > 0 ? 1.2 : 1 / 1.2);
                else if (e.Delta != 0) _owner.NavigateRequested?.Invoke(e.Delta > 0 ? -1 : 1);
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e); Focus(); _owner.Interaction?.Invoke();
                if (e.Button != MouseButtons.Left) return;
                _drag = e.Location; _dragging = true; Capture = true; Cursor = Cursors.SizeAll;
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e); _owner.Interaction?.Invoke();
                if (!_dragging) return;
                _owner._pan.X += e.X - _drag.X; _owner._pan.Y += e.Y - _drag.Y;
                _drag = e.Location; Invalidate();
            }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; Capture = false; Cursor = Cursors.Default; }
            protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { _dragging = false; Cursor = Cursors.Default; } }
            protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (e.Button == MouseButtons.Left) { if (_owner._zoom > 1) _owner.ResetView(); else _owner.ZoomBy(2); } }
        }

        public ImageViewMode ViewMode => _viewMode;

        public void RotateClockwise()
        {
            if (_bitmap == null)
                return;
            _bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
            _pan = PointF.Empty;
            _surface?.Invalidate();
            _updateCb?.Invoke();
        }

        public void QueuePreload(IEnumerable<string> mediaPaths)
        {
            if (_disposed)
                return;

            string[] wanted = (mediaPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || IsRemoteImage(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();

            lock (_preloadSync)
            {
                _wantedPreloads.Clear();
                foreach (string path in wanted)
                    _wantedPreloads.Add(path);

                foreach (string stale in _preloaded.Keys.Where(path => !_wantedPreloads.Contains(path)).ToList())
                {
                    try { _preloaded[stale].Dispose(); } catch { }
                    _preloaded.Remove(stale);
                }
            }

            foreach (string path in wanted)
            {
                lock (_preloadSync)
                {
                    if (_preloaded.ContainsKey(path) || !_preloading.Add(path))
                        continue;
                }

                _ = Task.Run(() =>
                {
                    Bitmap? decoded = null;
                    try { decoded = LoadBitmapFile(path); } catch { }
                    lock (_preloadSync)
                    {
                        _preloading.Remove(path);
                        if (!_disposed && decoded != null && _wantedPreloads.Contains(path))
                        {
                            if (_preloaded.Remove(path, out Bitmap? old))
                            {
                                try { old.Dispose(); } catch { }
                            }
                            _preloaded[path] = decoded;
                            decoded = null;
                        }
                    }
                    try { decoded?.Dispose(); } catch { }
                });
            }
        }

        private static Bitmap LoadBitmapFile(string path)
        {
            if (IsRemoteImage(path))
            {
                using var stream = PhotoHttp.GetStreamAsync(path).GetAwaiter().GetResult();
                using var buffer = new MemoryStream(); stream.CopyTo(buffer); buffer.Position = 0;
                return ImageAssetDecoder.Load(buffer);
            }
            return ImageAssetDecoder.Load(path);
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
            return (_bitmap.Width, _bitmap.Height, "image");
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
                int h = (int)Math.Round(_bitmap.Height * scale);

                var thumb = new Bitmap(w, h);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
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
            DisposeBitmap();
            lock (_preloadSync)
            {
                _disposed = true;
                foreach (Bitmap bitmap in _preloaded.Values)
                {
                    try { bitmap.Dispose(); } catch { }
                }
                _preloaded.Clear();
                _preloading.Clear();
                _wantedPreloads.Clear();
            }
        }

        private void DisposeBitmap()
        {
            try { _bitmap?.Dispose(); } catch { }
            _bitmap = null;
            _path = null;
            _lastDest = Rectangle.Empty;
        }

        private void OnStatusSafe(string s)
        {
            try { OnStatus?.Invoke(s); }
            catch { }
        }
    }
}
