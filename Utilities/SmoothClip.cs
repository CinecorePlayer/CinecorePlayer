#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CinecorePlayer2025.Utilities
{
    // SetClip con un percorso arrotondato non ha antialias: gli angoli di poster, copertine
    // e anteprime risultavano seghettati. Qui il contenuto viene disegnato su un livello a
    // parte e ritagliato con una maschera alfa antialias, poi composto sulla destinazione.
    // Funziona su qualsiasi sfondo. Da usare per immagini e sfumature, non per il testo GDI.
    internal sealed class SmoothClip : IDisposable
    {
        private readonly Graphics _target;
        private readonly GraphicsPath _path;
        private readonly Rectangle _bounds;
        private readonly Bitmap? _layer;

        public Graphics Graphics { get; }

        private SmoothClip(Graphics target, GraphicsPath path)
        {
            _target = target;
            _path = path;
            RectangleF b = path.GetBounds();
            _bounds = Rectangle.FromLTRB((int)Math.Floor(b.Left) - 1, (int)Math.Floor(b.Top) - 1, (int)Math.Ceiling(b.Right) + 1, (int)Math.Ceiling(b.Bottom) + 1);
            if (_bounds.Width > 1 && _bounds.Height > 1 && _bounds.Width * (long)_bounds.Height < 40_000_000)
            {
                _layer = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppPArgb);
                Graphics = Graphics.FromImage(_layer);
                Graphics.SmoothingMode = target.SmoothingMode;
                Graphics.InterpolationMode = target.InterpolationMode;
                Graphics.PixelOffsetMode = target.PixelOffsetMode;
                Graphics.CompositingQuality = target.CompositingQuality;
                Graphics.TranslateTransform(-_bounds.X, -_bounds.Y);
            }
            else
            {
                // Fallback: ritaglio classico sulla destinazione.
                Graphics = target;
                _savedState = target.Save();
                target.SetClip(path, CombineMode.Intersect);
            }
        }

        private readonly GraphicsState? _savedState;

        public static SmoothClip Begin(Graphics target, GraphicsPath path) => new(target, path);

        public void Dispose()
        {
            if (_layer == null)
            {
                if (_savedState != null) _target.Restore(_savedState);
                return;
            }
            Graphics.Dispose();
            try
            {
                ApplyMask(_layer, _path, _bounds);
                _target.DrawImage(_layer, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height);
            }
            finally { _layer.Dispose(); }
        }

        private static unsafe void ApplyMask(Bitmap layer, GraphicsPath path, Rectangle bounds)
        {
            using var mask = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var mg = Graphics.FromImage(mask))
            {
                mg.SmoothingMode = SmoothingMode.AntiAlias;
                mg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                mg.TranslateTransform(-bounds.X, -bounds.Y);
                mg.FillPath(Brushes.White, path);
            }
            var area = new Rectangle(0, 0, bounds.Width, bounds.Height);
            BitmapData l = layer.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            BitmapData m = mask.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < bounds.Height; y++)
                {
                    byte* lp = (byte*)l.Scan0 + y * l.Stride;
                    byte* mp = (byte*)m.Scan0 + y * m.Stride;
                    for (int x = 0; x < bounds.Width; x++, lp += 4, mp += 4)
                    {
                        int a = mp[3];
                        if (a == 255) continue;
                        if (a == 0) { *(uint*)lp = 0; continue; }
                        // Premoltiplicato: tutti i canali scalano con la copertura.
                        lp[0] = (byte)(lp[0] * a / 255);
                        lp[1] = (byte)(lp[1] * a / 255);
                        lp[2] = (byte)(lp[2] * a / 255);
                        lp[3] = (byte)(lp[3] * a / 255);
                    }
                }
            }
            finally
            {
                layer.UnlockBits(l);
                mask.UnlockBits(m);
            }
        }
    }
}
