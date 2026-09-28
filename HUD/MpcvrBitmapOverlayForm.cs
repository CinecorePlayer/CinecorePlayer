#nullable enable
using CinecorePlayer2025.Utilities;
using CinecorePlayer2025.HUD;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed class MpcvrBitmapOverlayForm : Form
    {
        private bool _clickThrough = true;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool PassThroughAllInput { get; set; }
        private DateTime _lastContextMenuRequestUtc = DateTime.MinValue;
        private Bitmap? _cachedLayerSource;
        private nint _cachedLayerBitmap;

        public event Action<Point>? ContextMenuRequested;

        public MpcvrBitmapOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_POPUP = unchecked((int)0x80000000);
                const int WS_EX_LAYERED = 0x00080000;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_TOPMOST = 0x00000008;
                const int WS_EX_NOACTIVATE = 0x08000000;

                var cp = base.CreateParams;
                cp.Style |= WS_POPUP;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_RBUTTONUP = 0x0205;
            const int WM_CONTEXTMENU = 0x007B;
            const int HTCLIENT = 1;
            const int HTTRANSPARENT = -1;

            if (_clickThrough && m.Msg == WM_NCHITTEST)
            {
                m.Result = !PassThroughAllInput && IsRightButtonDown()
                    ? new IntPtr(HTCLIENT)
                    : new IntPtr(HTTRANSPARENT);
                return;
            }

            if (m.Msg == WM_CONTEXTMENU)
            {
                Point screen = DecodeContextMenuPoint(m.LParam);
                RequestContextMenu(screen);
                m.Result = IntPtr.Zero;
                return;
            }

            if (m.Msg == WM_RBUTTONDOWN)
            {
                m.Result = IntPtr.Zero;
                return;
            }

            if (m.Msg == WM_RBUTTONUP)
            {
                int x = unchecked((short)((long)m.LParam & 0xFFFF));
                int y = unchecked((short)(((long)m.LParam >> 16) & 0xFFFF));
                RequestContextMenu(PointToScreen(new Point(x, y)));
                m.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref m);
        }

        public void SetClickThrough(bool clickThrough)
        {
            if (_clickThrough == clickThrough)
                return;

            _clickThrough = clickThrough;
            if (IsHandleCreated)
                RecreateHandle();
        }

        public bool ShowBitmap(Form owner, Rectangle screenRect, Bitmap source, Color transparentKey, bool clickThrough, byte opacity = 255)
        {
            if (source.Width <= 0 || source.Height <= 0 || screenRect.Width <= 0 || screenRect.Height <= 0)
                return false;

            SetClickThrough(clickThrough);

            try
            {
                if (!IsHandleCreated) { Bounds = screenRect; _ = Handle; }
                if (Owner != owner) Owner = owner;
                // Populate the alpha surface before exposing a new window.
                // Updating opacity must not repeatedly change z-order or frame style.
                bool updated = UpdateLayeredBitmap(screenRect, source, transparentKey, opacity);
                if (updated && !Visible) Show(owner);
                return updated;
            }
            catch
            {
                return false;
            }
        }

        public void HideOverlay()
        {
            try
            {
                if (Visible)
                    Hide();
            }
            catch { }
        }

        private bool UpdateLayeredBitmap(Rectangle screenRect, Bitmap source, Color transparentKey, byte opacity)
        {
            nint screenDc = nint.Zero;
            nint memDc = nint.Zero;
            nint hBitmap = nint.Zero;
            nint oldBitmap = nint.Zero;

            try
            {
                screenDc = GetDC(nint.Zero);
                if (screenDc == nint.Zero)
                    return false;

                memDc = CreateCompatibleDC(screenDc);
                if (memDc == nint.Zero)
                    return false;

                if (PassThroughAllInput && ReferenceEquals(source, _cachedLayerSource) && _cachedLayerBitmap != 0)
                    hBitmap = _cachedLayerBitmap;
                else
                {
                    using var prepared = PreparePremultipliedBitmap(source, transparentKey);
                    hBitmap = prepared.GetHbitmap(Color.FromArgb(0));
                    if (PassThroughAllInput)
                    {
                        if (_cachedLayerBitmap != 0) DeleteObject(_cachedLayerBitmap);
                        _cachedLayerSource = source; _cachedLayerBitmap = hBitmap;
                    }
                }
                if (hBitmap == nint.Zero)
                    return false;

                oldBitmap = SelectObject(memDc, hBitmap);

                var dst = new POINT(screenRect.Left, screenRect.Top);
                var size = new SIZE(screenRect.Width, screenRect.Height);
                var src = new POINT(0, 0);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = opacity,
                    AlphaFormat = AC_SRC_ALPHA
                };

                bool ok = UpdateLayeredWindow(
                    Handle,
                    screenDc,
                    ref dst,
                    ref size,
                    memDc,
                    ref src,
                    0,
                    ref blend,
                    ULW_ALPHA);

                return ok;
            }
            finally
            {
                if (memDc != nint.Zero && oldBitmap != nint.Zero)
                    SelectObject(memDc, oldBitmap);
                if (hBitmap != nint.Zero && hBitmap != _cachedLayerBitmap)
                    DeleteObject(hBitmap);
                if (memDc != nint.Zero)
                    DeleteDC(memDc);
                if (screenDc != nint.Zero)
                    ReleaseDC(nint.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_cachedLayerBitmap != 0) { DeleteObject(_cachedLayerBitmap); _cachedLayerBitmap = 0; }
            _cachedLayerSource = null;
            base.Dispose(disposing);
        }

        private void RequestContextMenu(Point screenPoint)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastContextMenuRequestUtc).TotalMilliseconds < 250)
                return;

            _lastContextMenuRequestUtc = now;
            try { ContextMenuRequested?.Invoke(screenPoint); } catch { }
        }

        private static Point DecodeContextMenuPoint(IntPtr lParam)
        {
            long raw = lParam.ToInt64();
            int x = unchecked((short)(raw & 0xFFFF));
            int y = unchecked((short)((raw >> 16) & 0xFFFF));
            if (x == -1 && y == -1)
                return Cursor.Position;

            return new Point(x, y);
        }

        private static bool IsRightButtonDown()
        {
            const int VK_RBUTTON = 0x02;
            try { return (GetAsyncKeyState(VK_RBUTTON) & unchecked((short)0x8000)) != 0; }
            catch { return false; }
        }

        private static Bitmap PreparePremultipliedBitmap(Bitmap source, Color transparentKey)
        {
            using var keyed = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(keyed))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImageUnscaled(source, 0, 0);
            }

            keyed.MakeTransparent(transparentKey);

            var prepared = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(prepared))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
                g.DrawImageUnscaled(keyed, 0, 0);
            }

            return prepared;
        }

        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int Cx;
            public int Cy;
            public SIZE(int cx, int cy) { Cx = cx; Cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint GetDC(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(nint hWnd, nint hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(nint hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern nint SelectObject(nint hdc, nint hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(nint hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            nint hwnd,
            nint hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            nint hdcSrc,
            ref POINT pptSrc,
            int crKey,
            ref BLENDFUNCTION pblend,
            int dwFlags);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}
