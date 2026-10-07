using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CinecorePlayer2025.Utilities
{
    internal static class ScrollbarChrome
    {
        /// <summary>
        /// One rule for every scrollbar thumb: proportional to what is visible, never shorter
        /// than a comfortable grip and never longer than 40% of the track. Without the upper
        /// limit a list that barely overflowed (Music, Diary) showed one long bar from top to
        /// bottom while Movies had a short one: they looked like different controls.
        /// </summary>
        public static int ThumbLength(int trackLength, double visibleFraction)
        {
            if (trackLength <= 0) return 0;
            int minimum = Math.Min(trackLength, Math.Max(34, trackLength / 14));
            int maximum = Math.Max(minimum, (int)Math.Round(trackLength * 0.40));
            double fraction = double.IsFinite(visibleFraction) ? Math.Clamp(visibleFraction, 0, 1) : 1;
            return Math.Clamp((int)Math.Round(trackLength * fraction), minimum, maximum);
        }

        public static void Draw(Graphics graphics, Rectangle track, int thumbTop, int thumbHeight)
        {
            if (track.Width <= 0 || track.Height <= 0)
                return;

            int top = Math.Clamp(thumbTop, track.Top, track.Bottom);
            int height = Math.Clamp(thumbHeight, 0, track.Bottom - top);
            if (height <= 0) return;
            float width = Math.Min(4, track.Width);
            using var pen = new Pen(Color.FromArgb(90, HUD.Theme.Text), width)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float x = track.Left + track.Width / 2f;
            graphics.DrawLine(pen, x, top + width / 2, x, top + Math.Max(width / 2, height - width / 2));
        }
    }
}
