#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    // Shared by the homepage hero and Spotlight: one treatment for technical metadata.
    internal static class MediaTechnicalPill
    {
        internal static void Draw(Graphics g, Rectangle bounds, string text, Color textColor, Color accentColor, bool accent)
        {
            float scale = Math.Clamp(bounds.Height / 25f, .45f, 4f);
            int diameter = Math.Max(2, (int)Math.Round(10 * scale));
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(accent ? Color.FromArgb(48, accentColor) : Color.FromArgb(152, 13, 20, 29));
            using var border = new Pen(accent ? Color.FromArgb(92, accentColor) : Color.FromArgb(45, 255, 255, 255));
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            using var font = new Font("Segoe UI", 8.7f * scale * 96f / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, text, font, Rectangle.Inflate(bounds, -(int)Math.Round(8*scale), 0), textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}
