#nullable enable
using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025.HUD
{
    // Opaque, already composed frames. GDI's constant-alpha blit avoids GDI+
    // resampling/color conversion on every timer tick, including on 4K displays.
    internal sealed class SpotlightFrame : IDisposable
    {
        private IntPtr _dc, _bitmap, _previous;
        private readonly Size _size;
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Blend { public byte Operation, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public uint Size; public int Width, Height; public ushort Planes, BitCount;
            public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter;
            public uint ColorsUsed, ColorsImportant;
        }
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
        [DllImport("msimg32.dll")] private static extern bool AlphaBlend(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, int sw, int sh, Blend blend);

        private SpotlightFrame(Size size)
        {
            _size = size;
            try
            {
                _dc = CreateCompatibleDC(IntPtr.Zero);
                var info = new BitmapInfo { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
                _bitmap = CreateDIBSection(_dc, ref info, 0, out _, IntPtr.Zero, 0);
                if (_dc == IntPtr.Zero || _bitmap == IntPtr.Zero) throw new Win32Exception();
                _previous = SelectObject(_dc, _bitmap);
                if (_previous == IntPtr.Zero || _previous == new IntPtr(-1)) throw new Win32Exception();
            }
            catch { Dispose(); throw; }
        }

        internal static SpotlightFrame Capture(Size size, Action<Graphics> paint)
        {
            var frame = new SpotlightFrame(size);
            try
            {
                // A real memory DC lets TextRenderer draw directly; Graphics.FromImage
                // would copy the entire bitmap for every GDI text operation.
                using var graphics = Graphics.FromHdc(frame._dc);
                graphics.Clear(Color.Black);
                paint(graphics);
                graphics.Flush();
                return frame;
            }
            catch { frame.Dispose(); throw; }
        }

        internal static void Draw(Graphics graphics, SpotlightFrame? from, SpotlightFrame to, float opacity)
        {
            IntPtr target = graphics.GetHdc();
            try
            {
                if (from != null) BitBlt(target, 0, 0, from._size.Width, from._size.Height, from._dc, 0, 0, 0x00CC0020);
                byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
                if (alpha == 255) BitBlt(target, 0, 0, to._size.Width, to._size.Height, to._dc, 0, 0, 0x00CC0020);
                else if (alpha > 0) AlphaBlend(target, 0, 0, to._size.Width, to._size.Height, to._dc, 0, 0, to._size.Width, to._size.Height, new Blend { Alpha = alpha });
            }
            finally { graphics.ReleaseHdc(target); }
        }

        public void Dispose()
        {
            if (_dc != IntPtr.Zero && _previous != IntPtr.Zero && _previous != new IntPtr(-1)) SelectObject(_dc, _previous);
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            if (_dc != IntPtr.Zero) DeleteDC(_dc);
            _bitmap = _dc = _previous = IntPtr.Zero;
        }
    }
}
