#nullable enable
using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CinecorePlayer2025;

public sealed partial class PlayerForm
{
    private HudInputSurface? _hudInputSurface;

    private void UpdateHudInputSurface()
    {
        bool show = !_closingForExit && !_nativeChromeInteraction && !_contextMenuActive && !_contextMenuPending &&
            _currentMediaHasVideo && _hud?.Visible == true && _hud.RenderOpacity > .05f &&
            !_useInlineOverlay && !ShouldSuppressExternalPlaybackOverlays() && IsCinecoreForeground();
        if (!show) { _hudInputSurface?.Hide(); return; }
        _hudInputSurface ??= new HudInputSurface(_hud!);
        _hudInputSurface.Sync(this);
    }

    // Color-keyed HWNDs pass clicks through their transparent pixels, including
    // the spaces inside button hit boxes. A nearly transparent input surface
    // covers only the HUD regions and forwards complete gestures to the HUD.
    private sealed class HudInputSurface : Form
    {
        private readonly HUD.HudOverlay _target;
        private Rectangle[] _regions = Array.Empty<Rectangle>();
        internal HudInputSurface(HUD.HudOverlay target)
        {
            _target = target;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            Opacity = 1d / 255;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; }
        }
        internal void Sync(PlayerForm owner)
        {
            var bounds = _target.RectangleToScreen(_target.ClientRectangle);
            var regions = _target.PointerRegions().Where(r => !r.IsEmpty).ToArray();
            if (!_regions.SequenceEqual(regions))
            {
                using var region = new Region(); region.MakeEmpty();
                foreach (var rectangle in regions) region.Union(rectangle);
                var old = Region; Region = region.Clone(); old?.Dispose();
                _regions = regions;
            }
            if (Bounds != bounds) Bounds = bounds;
            if (!Visible) Show(owner);
            // Keep the input region above the color-key visual, without activation.
            if (owner._overlayHost?.IsHandleCreated != true || GetWindow(owner._overlayHost.Handle, 3) != Handle)
            SetWindowPos(Handle, owner.TopMost ? new IntPtr(-1) : IntPtr.Zero,
                0, 0, 0, 0, 0x0013);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            if (m.Msg == 0x84) { m.Result = new IntPtr(1); return; } // HTCLIENT
            if (m.Msg >= 0x200 && m.Msg <= 0x20E && _target.IsHandleCreated && !_target.IsDisposed)
            {
                // The input surface has the same client origin and dimensions as HUD.
                m.Result = SendMessage(_target.Handle, m.Msg, m.WParam, m.LParam);
                return;
            }
            base.WndProc(ref m);
        }
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    }
}
