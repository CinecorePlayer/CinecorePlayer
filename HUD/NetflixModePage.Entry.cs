#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CinecorePlayer2025.HUD
{
    internal sealed partial class NetflixModePage
    {
        private void DrawEntryReveal(Graphics g)
        {
            if (_entryStartedTimestamp <= 0) _entryStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            float t = _entryProgress;
            float reveal = Math.Clamp((t - .58f) / .42f, 0, 1);
            reveal = reveal * reveal * (3 - 2 * reveal);
            g.Clear(Color.FromArgb(3, 9, 16));
            if (reveal > 0)
            {
                if (_entryFrame == null) CreateEntryFrame();
                if (_entryFrame != null) DrawFrameOpacity(g, _entryFrame, reveal);
            }

            float enter = Math.Clamp(t / .22f, 0, 1);
            enter = 1 - MathF.Pow(1 - enter, 3);
            float opacity = enter * (1 - Math.Clamp((t - .52f) / .24f, 0, 1));
            if (opacity <= 0) return;
            float scale = .96f + .04f * enter;
            int w = (int)(D(330) * scale), h = w / 3;
            int x = (Width - w) / 2, y = Height / 2 - h / 2 - D(24);

            using (var haloPath = new GraphicsPath())
            {
                haloPath.AddEllipse(x - D(60), y - D(80), w + D(120), h + D(160));
                using var halo = new PathGradientBrush(haloPath)
                {
                    CenterColor = Color.FromArgb((int)(23 * opacity), Theme.Accent),
                    SurroundColors = new[] { Color.FromArgb(0, Theme.Accent) }
                };
                g.FillPath(halo, haloPath);
            }
            var logo = SettingsHudPage.LoadBrandLogo();
            if (logo != null)
            {
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
                g.DrawImage(logo, new Rectangle(x, y, w, h), 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, attributes);
            }
            using var font = UiFont("Segoe UI Semibold", 10f);
            using var text = new SolidBrush(Color.FromArgb((int)(210 * opacity), 220, 231, 242));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("S P O T L I G H T", font, text, new RectangleF(0, y + h + D(22), Width, D(28)), format);

            // A quiet light sweep below the brand, driven by elapsed time.
            int lineW = D(132), lineY = y + h + D(70), lineX = (Width - lineW) / 2;
            using var track = new Pen(Color.FromArgb((int)(35 * opacity), Theme.Accent), Math.Max(1, UiScale));
            g.DrawLine(track, lineX, lineY, lineX + lineW, lineY);
            int beamW = D(32), beamX = lineX - beamW + (int)((lineW + beamW) * Math.Clamp(t / .68f, 0, 1));
            var saved = g.Save();
            g.SetClip(new Rectangle(lineX, lineY - D(2), lineW, D(5)), CombineMode.Intersect);
            using var beam = new Pen(Color.FromArgb((int)(210 * opacity), Theme.Accent), Math.Max(1.5f, UiScale * 1.5f));
            g.DrawLine(beam, beamX, lineY, beamX + beamW, lineY);
            g.Restore(saved);
        }
    }
}
