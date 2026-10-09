#nullable enable
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Livello fuori schermo su un contesto GDI (come il doppio buffer dei controlli), per le
    /// animazioni che dipingono un pannello una volta e poi lo compongono a ogni fotogramma.
    /// Rispetto a una Bitmap di GDI+:
    /// - disegnarci il testo non costa una copia dell'intera immagine a ogni scritta
    ///   (TextRenderer su una Bitmap a 1440p: centinaia di millisecondi per una scheda);
    /// - la composizione con trasparenza uniforme e' un AlphaBlend di GDI, non una
    ///   DrawImage con matrice di colore.
    /// </summary>
    internal sealed class GdiLayer : IDisposable
    {
        private readonly BufferedGraphicsContext _context = new();
        private readonly BufferedGraphics _buffer;

        public Size Size { get; }
        public Graphics Graphics => _buffer.Graphics;

        public GdiLayer(Control owner, Size size)
        {
            Size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
            _context.MaximumBuffer = new Size(Size.Width + 1, Size.Height + 1);
            using var target = owner.CreateGraphics();
            _buffer = _context.Allocate(target, new Rectangle(Point.Empty, Size));
        }

        /// <summary>Copia <paramref name="source"/> del livello in <paramref name="destination"/>,
        /// con opacita' uniforme 0..1 (1 = copia piena).</summary>
        public void DrawTo(Graphics g, Rectangle source, Point destination, double opacity = 1)
        {
            source.Intersect(new Rectangle(Point.Empty, Size));
            if (source.Width <= 0 || source.Height <= 0) return;
            byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
            if (alpha == 0) return;
            IntPtr from = _buffer.Graphics.GetHdc();
            try
            {
                IntPtr to = g.GetHdc();
                try
                {
                    if (alpha == 255)
                        BitBlt(to, destination.X, destination.Y, source.Width, source.Height, from, source.X, source.Y, SrcCopy);
                    else
                        AlphaBlend(to, destination.X, destination.Y, source.Width, source.Height, from, source.X, source.Y, source.Width, source.Height,
                            new BlendFunction { SourceConstantAlpha = alpha });
                }
                finally { g.ReleaseHdc(to); }
            }
            finally { _buffer.Graphics.ReleaseHdc(from); }
        }

        /// <summary>Copia nel livello cio' che e' appena stato dipinto su <paramref name="source"/> (stesse coordinate).</summary>
        public void CopyFrom(Graphics source, Rectangle area)
        {
            area.Intersect(new Rectangle(Point.Empty, Size));
            if (area.Width <= 0 || area.Height <= 0) return;
            IntPtr to = _buffer.Graphics.GetHdc();
            try
            {
                IntPtr from = source.GetHdc();
                try { BitBlt(to, area.X, area.Y, area.Width, area.Height, from, area.X, area.Y, SrcCopy); }
                finally { source.ReleaseHdc(from); }
            }
            finally { _buffer.Graphics.ReleaseHdc(to); }
        }

        public void Dispose()
        {
            _buffer.Dispose();
            _context.Dispose();
        }

        private const int SrcCopy = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct BlendFunction
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, int rop);

        [DllImport("msimg32.dll")]
        private static extern bool AlphaBlend(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, int sw, int sh, BlendFunction blend);
    }
}
