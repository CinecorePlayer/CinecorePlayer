#nullable enable
using CinecorePlayer2025.HUD;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed class MpcvrOverlayStagingForm : Form
    {
        public Panel Surface { get; } = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(1, 1, 1)
        };

        public MpcvrOverlayStagingForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(1, 1, 1);
            Location = new Point(-32000, -32000);
            Size = new Size(16, 16);
            Controls.Add(Surface);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_POPUP = unchecked((int)0x80000000);
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;

                var cp = base.CreateParams;
                cp.Style |= WS_POPUP;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void EnsureReady(Form owner, Size size)
        {
            int w = Math.Max(2, size.Width);
            int h = Math.Max(2, size.Height);
            var bounds = new Rectangle(-32000, -32000, w, h);

            if (!Visible)
            {
                Bounds = bounds;
                try { Show(owner); }
                catch { Show(); }
            }

            if (Bounds != bounds)
                Bounds = bounds;

            if (Surface.Bounds != new Rectangle(Point.Empty, bounds.Size))
                Surface.Bounds = new Rectangle(Point.Empty, bounds.Size);

            try
            {
                Win32.SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | SWP_NOACTIVATE);
            }
            catch { }
        }

        public void HideStaging()
        {
            try
            {
                if (Visible)
                    Hide();
            }
            catch { }
        }

        private const uint SWP_NOACTIVATE = 0x0010;
    }
}
