#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Rounded corners for top-level windows. A window Region cannot be anti-aliased, so
    /// Region-rounded dialogs and menus had stair-stepped corners and a clipped outline. On
    /// Windows 11 the compositor rounds the window itself (anti-aliased, with its border and
    /// shadow); the Region is kept only as the fallback for older systems.
    /// </summary>
    internal static class WindowCorners
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWCP_ROUNDSMALL = 3;

        private static readonly bool Supported = Environment.OSVersion.Version.Build >= 22000;

        /// <summary>True when the system draws the corners (no Region, no painted outline needed).</summary>
        public static bool SystemRounded(Control window) => Supported && window.IsHandleCreated && !window.IsDisposed;

        /// <summary>
        /// Rounds <paramref name="window"/> (a Form or a top-level popup such as a menu).
        /// Re-applied on handle creation; call again after resizing only on the fallback path.
        /// </summary>
        public static void Apply(Control window, int fallbackRadius, bool small = false, Color? border = null)
        {
            if (window.IsDisposed) return;
            if (!window.IsHandleCreated)
            {
                void OnCreated(object? sender, EventArgs e)
                {
                    window.HandleCreated -= OnCreated;
                    Apply(window, fallbackRadius, small, border);
                }
                window.HandleCreated += OnCreated;
                return;
            }

            if (Supported && TrySystemCorners(window.Handle, small, border))
            {
                var old = window.Region;
                if (old != null)
                {
                    window.Region = null;
                    old.Dispose();
                }
                return;
            }

            ApplyRegion(window, fallbackRadius);
        }

        /// <summary>Fallback path: refresh the Region after a resize (no-op with system corners).</summary>
        public static void Refresh(Control window, int fallbackRadius)
        {
            if (Supported || window.IsDisposed || window.Width < 4 || window.Height < 4) return;
            ApplyRegion(window, fallbackRadius);
        }

        private static bool TrySystemCorners(IntPtr handle, bool small, Color? border)
        {
            try
            {
                int preference = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
                if (DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)) != 0)
                    return false;
                if (border is Color color)
                {
                    int colorRef = color.R | color.G << 8 | color.B << 16;
                    DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
                }
                return true;
            }
            catch { return false; }
        }

        private static void ApplyRegion(Control window, int radius)
        {
            if (window.Width < 4 || window.Height < 4) return;
            try
            {
                using var path = new GraphicsPath();
                var r = new Rectangle(0, 0, window.Width, window.Height);
                int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                var old = window.Region;
                window.Region = new Region(path);
                old?.Dispose();
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
