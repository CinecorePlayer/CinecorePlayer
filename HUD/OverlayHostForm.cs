#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HDRMode = global::CinecorePlayer2025.Utilities.HdrMode;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{

    // ======= OverlayHostForm – top-level davvero trasparente =======
    internal sealed class OverlayHostForm : Form
    {
        public Panel Surface { get; } = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
        private bool _interactive;
        private bool? _clickThrough;
        private bool _regionOverlayMode;
        private Region? _visibleRegion;

        public event Action<Point>? ContextMenuRequested;

        public OverlayHostForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = false;

            BackColor = Color.Black;
            TransparencyKey = Color.Black;

            Controls.Add(Surface);
            Surface.BackColor = TransparencyKey;
            Surface.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    RequestContextMenu(Surface.PointToScreen(e.Location));
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _visibleRegion?.Dispose(); } catch { }
                _visibleRegion = null;
            }

            base.Dispose(disposing);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryApplyLayeredColorKey(this.TransparencyKey);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x0014;
            const int WM_RBUTTONUP = 0x0205;
            const int WM_CONTEXTMENU = 0x007B;
            if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
            if (m.Msg == WM_CONTEXTMENU)
            {
                RequestContextMenu(DecodeContextMenuPoint(m.LParam));
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

        private void RequestContextMenu(Point screenPoint)
        {
            try { ContextMenuRequested?.Invoke(screenPoint); } catch { }
        }

        private static Point DecodeContextMenuPoint(IntPtr lParam)
        {
            long raw = lParam.ToInt64();
            int x = unchecked((short)(raw & 0xFFFF));
            int y = unchecked((short)((raw >> 16) & 0xFFFF));
            return x == -1 && y == -1 ? Cursor.Position : new Point(x, y);
        }

        private void TryApplyLayeredColorKey(Color key)
        {
            if (!IsHandleCreated) return;
            if (_regionOverlayMode) return;
            uint rgb = (uint)(key.R | (key.G << 8) | (key.B << 16));
            try { SetLayeredWindowAttributes(this.Handle, rgb, 255, LWA_COLORKEY); } catch { }
            try
            {
                IntPtr insertAfter = TopMost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST;
                Win32.SetWindowPos(this.Handle, insertAfter, 0, 0, 0, 0,
                    Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | SWP_NOACTIVATE);
            }
            catch { }
        }

        // L'overlay deve poter ricevere click nelle proprie regioni attive senza
        // diventare la foreground window. Se si attiva, i richiami periodici a
        // BringToFront sottraggono continuamente l'attivazione al PlayerForm e la
        // chrome nativa (chiudi/minimizza/massimizza) smette di ricevere il click.
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_POPUP = unchecked((int)0x80000000);
                const int WS_CLIPCHILDREN = 0x02000000;
                const int WS_CLIPSIBLINGS = 0x04000000;

                var cp = base.CreateParams;
                cp.Style |= WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
                cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
                if (TopMost)
                    cp.ExStyle |= WS_EX_TOPMOST;
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
                if (!_regionOverlayMode)
                    cp.ExStyle |= WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(this.TransparencyKey);
        }

        public void SyncTo(Form owner)
        {
            if (!owner.Visible) return;
            var rc = owner.RectangleToScreen(owner.ClientRectangle);
            SyncToScreen(rc);
        }

        private const int SWP_NOACTIVATE = 0x0010;

        public void SyncToScreen(Rectangle screenRect)
        {
            if (screenRect.Width <= 0 || screenRect.Height <= 0) return;
            // Le callback del renderer possono invocare questo metodo molte volte al
            // secondo. Un SetWindowPos identico rimescola comunque le owned window e
            // fa lampeggiare il bordo DWM della form in modalità finestra.
            if (Bounds == screenRect)
                return;

            try
            {
                IntPtr insertAfter = TopMost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST;
                // Sincronizzare la geometria non deve rendere visibile la host. Un
                // callback tardivo del renderer poteva altrimenti resuscitare questa
                // finestra trasparente dopo lo stop e farle intercettare tutta la HUD.
                uint flags = SWP_NOACTIVATE;

                Win32.SetWindowPos(this.Handle, insertAfter,
                    screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height,
                    flags);
            }
            catch
            {
                Bounds = screenRect; // fallback
            }
        }

        public void RaiseAboveOwner(bool noActivate = true)
        {
            if (!IsHandleCreated || !Visible) return;
            try
            {
                IntPtr insertAfter = TopMost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST;
                uint flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE;
                flags |= SWP_NOACTIVATE;

                Win32.SetWindowPos(this.Handle, insertAfter, 0, 0, 0, 0, flags);
            }
            catch { }
        }

        public void SetRegionOverlayMode(bool enabled)
        {
            if (_regionOverlayMode == enabled)
                return;

            _regionOverlayMode = enabled;

            try
            {
                if (enabled)
                {
                    TransparencyKey = Color.Empty;
                    BackColor = Color.Black;
                    Surface.BackColor = Color.Black;
                    ClearVisibleRegion();
                    UpdateLayeredStyle(removeLayered: true);
                }
                else
                {
                    ClearVisibleRegion();
                    BackColor = Color.Black;
                    TransparencyKey = Color.Black;
                    Surface.BackColor = TransparencyKey;
                    UpdateLayeredStyle(removeLayered: false);
                    TryApplyLayeredColorKey(TransparencyKey);
                }

                Invalidate(true);
            }
            catch { }
        }

        public void SetVisibleRegion(IEnumerable<Rectangle>? rectangles)
        {
            if (!_regionOverlayMode)
            {
                ClearVisibleRegion();
                return;
            }

            try
            {
                using var path = new GraphicsPath();
                var clip = ClientRectangle;
                bool any = false;

                if (rectangles != null)
                {
                    foreach (var raw in rectangles)
                    {
                        var rc = Rectangle.Intersect(clip, raw);
                        if (rc.Width <= 0 || rc.Height <= 0)
                            continue;

                        path.AddRectangle(rc);
                        any = true;
                    }
                }

                if (!any)
                {
                    path.AddRectangle(new Rectangle(0, 0, 1, 1));
                }

                var old = _visibleRegion;
                _visibleRegion = new Region(path);
                Region = _visibleRegion;
                try { old?.Dispose(); } catch { }
            }
            catch
            {
                ClearVisibleRegion();
            }
        }

        private void ClearVisibleRegion()
        {
            try { Region = null; } catch { }
            try { _visibleRegion?.Dispose(); } catch { }
            _visibleRegion = null;
        }

        private void UpdateLayeredStyle(bool removeLayered)
        {
            if (!IsHandleCreated) return;

            try
            {
                int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
                if (removeLayered)
                    ex &= ~WS_EX_LAYERED;
                else
                    ex |= WS_EX_LAYERED;

                ex |= WS_EX_NOACTIVATE;

                SetWindowLong(this.Handle, GWL_EXSTYLE, ex);
                Win32.SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    Win32.SWP_FRAMECHANGED | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);
            }
            catch { }
        }

        public void SetClickThrough(bool passThrough)
        {
            if (!IsHandleCreated) return;
            int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
            bool alreadyApplied = (ex & WS_EX_TRANSPARENT) != 0;
            if (_clickThrough == passThrough && alreadyApplied == passThrough)
                return;

            if (passThrough) ex |= WS_EX_TRANSPARENT; else ex &= ~WS_EX_TRANSPARENT;
            ex |= WS_EX_NOACTIVATE;
            SetWindowLong(this.Handle, GWL_EXSTYLE, ex);
            _clickThrough = passThrough;
            Win32.SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | SWP_NOACTIVATE);
        }

        [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_TOPMOST = 0x00000008;
        const uint LWA_COLORKEY = 0x00000001;

        public void SetInteractive(bool on)
        {
            if (_interactive == on && IsHandleCreated &&
                (GetWindowLong(this.Handle, GWL_EXSTYLE) & WS_EX_NOACTIVATE) != 0)
                return;

            _interactive = on;
            if (!IsHandleCreated) return;
            int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
            if ((ex & WS_EX_NOACTIVATE) == 0)
            {
                ex |= WS_EX_NOACTIVATE;
                SetWindowLong(this.Handle, GWL_EXSTYLE, ex);
                Win32.SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
    }

    internal sealed class PopupOverlayHost : IDisposable
    {
        private sealed class OverlaySurfacePanel : Panel
        {
            public OverlaySurfacePanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw, true);
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_CLIPCHILDREN = 0x02000000;
                    const int WS_CLIPSIBLINGS = 0x04000000;

                    var cp = base.CreateParams;
                    cp.Style |= WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
                    return cp;
                }
            }
        }

        private readonly ToolStripDropDown _dropDown;
        private readonly ToolStripControlHost _host;
        private Region? _dropDownRegion;
        private Region? _surfaceRegion;
        private bool _interactive;
        private bool _clickThrough;

        public Panel Surface { get; }
        public bool Visible => !IsDisposed && _dropDown.Visible;
        public bool IsDisposed { get; private set; }

        public PopupOverlayHost()
        {
            Surface = new OverlaySurfacePanel
            {
                BackColor = Color.Black,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            _host = new ToolStripControlHost(Surface)
            {
                AutoSize = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            _dropDown = new ToolStripDropDown
            {
                AutoClose = false,
                AutoSize = false,
                BackColor = Color.Black,
                DropShadowEnabled = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _dropDown.Items.Add(_host);
            _dropDown.Opened += (_, __) => ApplyWindowStyles();
        }

        public void SyncToScreen(Rectangle screenRect, bool showIfHidden = false)
        {
            if (IsDisposed || screenRect.Width <= 0 || screenRect.Height <= 0)
                return;

            var size = screenRect.Size;
            try
            {
                _host.Size = size;
                _host.Control.Size = size;
                Surface.Bounds = new Rectangle(Point.Empty, size);
                _dropDown.MinimumSize = size;
                _dropDown.Size = size;

                if (!_dropDown.Visible && showIfHidden)
                    _dropDown.Show(screenRect.Location);

                if (!_dropDown.Visible)
                    return;

                ApplyWindowStyles();

                Win32.SetWindowPos(
                    _dropDown.Handle,
                    Win32.HWND_TOPMOST,
                    screenRect.X,
                    screenRect.Y,
                    screenRect.Width,
                    screenRect.Height,
                    Win32.SWP_FRAMECHANGED | SWP_NOACTIVATE);

                try { _dropDown.Bounds = screenRect; } catch { }
            }
            catch { }
        }

        public void BringToFront()
        {
            if (IsDisposed || !_dropDown.Visible || !_dropDown.IsHandleCreated)
                return;

            try
            {
                ApplyWindowStyles();
                Win32.SetWindowPos(
                    _dropDown.Handle,
                    Win32.HWND_TOPMOST,
                    0,
                    0,
                    0,
                    0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        public void Hide()
        {
            if (IsDisposed)
                return;

            try
            {
                if (_dropDown.Visible)
                    _dropDown.Hide();
            }
            catch { }
        }

        public void SetVisibleRegion(IEnumerable<Rectangle>? rectangles)
        {
            if (IsDisposed)
                return;

            try
            {
                using var path = new GraphicsPath();
                var clip = Surface.ClientRectangle;
                bool any = false;

                if (rectangles != null)
                {
                    foreach (var raw in rectangles)
                    {
                        var rc = Rectangle.Intersect(clip, raw);
                        if (rc.Width <= 0 || rc.Height <= 0)
                            continue;

                        path.AddRectangle(rc);
                        any = true;
                    }
                }

                if (!any)
                    path.AddRectangle(new Rectangle(0, 0, 1, 1));

                var oldDrop = _dropDownRegion;
                var oldSurface = _surfaceRegion;
                _dropDownRegion = new Region(path);
                _surfaceRegion = new Region(path);
                _dropDown.Region = _dropDownRegion;
                Surface.Region = _surfaceRegion;
                try { oldDrop?.Dispose(); } catch { }
                try { oldSurface?.Dispose(); } catch { }
            }
            catch
            {
                ClearVisibleRegion();
            }
        }

        public void ClearVisibleRegion()
        {
            try { _dropDown.Region = null; } catch { }
            try { Surface.Region = null; } catch { }
            try { _dropDownRegion?.Dispose(); } catch { }
            try { _surfaceRegion?.Dispose(); } catch { }
            _dropDownRegion = null;
            _surfaceRegion = null;
        }

        public void SetInteractive(bool on)
        {
            _interactive = on;
            ApplyWindowStyles();
        }

        public void SetClickThrough(bool passThrough)
        {
            _clickThrough = passThrough;
            ApplyWindowStyles();
        }

        private void ApplyWindowStyles()
        {
            if (IsDisposed || !_dropDown.IsHandleCreated)
                return;

            try
            {
                int ex = GetWindowLong(_dropDown.Handle, GWL_EXSTYLE);
                ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;

                if (_clickThrough)
                    ex |= WS_EX_TRANSPARENT;
                else
                    ex &= ~WS_EX_TRANSPARENT;

                SetWindowLong(_dropDown.Handle, GWL_EXSTYLE, ex);
                Win32.SetWindowPos(
                    _dropDown.Handle,
                    Win32.HWND_TOPMOST,
                    0,
                    0,
                    0,
                    0,
                    Win32.SWP_FRAMECHANGED | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { }
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            IsDisposed = true;
            ClearVisibleRegion();
            try { _dropDown.Close(ToolStripDropDownCloseReason.CloseCalled); } catch { }
            try { _dropDown.Dispose(); } catch { }
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint SWP_NOACTIVATE = 0x0010;
    }
}
