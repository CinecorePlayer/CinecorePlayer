#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CinecorePlayer2025.Utilities;

// Optical 24 px audio glyphs, shared by tabs, chart titles and compact metrics.
// Ogni glifo rappresenta la misura: lancetta per la panoramica, inviluppo per la forma
// d'onda, barre e curva per lo spettro, scala LU per la loudness, bilancia L/R, cerchi
// sovrapposti per la correlazione, meter per l'RMS, picco sopra l'RMS per la cresta.
internal static class AnalysisIconPainter
{
    internal static void Draw(Graphics graphics, Rectangle bounds, int icon, Color color)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(bounds.Left, bounds.Top);
        graphics.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var thin = new Pen(Color.FromArgb(Math.Min(255, color.A * 110 / 255), color), 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(color);
        void Line(float x, float y, float x2, float y2) => graphics.DrawLine(pen, x, y, x2, y2);
        switch (icon)
        {
            case 20: // Strumenti: VU meter con spia
                graphics.DrawRectangle(thin, 2.5f, 5, 19, 14);
                graphics.DrawArc(pen, 6, 9, 12, 12, 205, 130);
                Line(12, 17, 15.5f, 10.5f);
                graphics.FillEllipse(fill, 17.2f, 6.5f, 2.6f, 2.6f);
                break;
            case 0: // Panoramica: quadrante con lancetta
                graphics.DrawArc(pen, 3, 5, 18, 18, 180, 180);
                for (int i = 0; i <= 4; i++)
                {
                    double a = Math.PI + i * Math.PI / 4;
                    graphics.DrawLine(thin, 12 + (float)Math.Cos(a) * 9, 14 + (float)Math.Sin(a) * 9, 12 + (float)Math.Cos(a) * 7, 14 + (float)Math.Sin(a) * 7);
                }
                Line(12, 14, 16.5f, 8.5f);
                graphics.FillEllipse(fill, 10.4f, 12.4f, 3.2f, 3.2f);
                Line(4, 19, 20, 19);
                break;
            case 1: // Forma d'onda: oscillazione con inviluppo
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(2, 12, 3.5f, 9, 4.5f, 9, 6, 12);
                    path.AddBezier(6, 12, 7.5f, 16, 8.5f, 16, 10, 12);
                    path.AddBezier(10, 12, 11, 4, 13, 4, 14, 12);
                    path.AddBezier(14, 12, 15, 20, 17, 20, 18, 12);
                    path.AddBezier(18, 12, 19.3f, 9.5f, 20.7f, 9.5f, 22, 12);
                    graphics.DrawPath(pen, path);
                }
                graphics.DrawLine(thin, 2, 12, 22, 12);
                break;
            case 2: // Spettro: barre e curva d'inviluppo
                foreach (var (x, h) in new[] { (4f, 9f), (8f, 14f), (12f, 11f), (16f, 7f), (20f, 4f) })
                    graphics.FillRectangle(fill, x - 1.2f, 21 - h, 2.4f, h);
                graphics.DrawCurve(pen, new[] { new PointF(2, 13), new PointF(8, 5), new PointF(12, 8), new PointF(16, 12), new PointF(22, 16) }, .5f);
                break;
            case 3: // Loudness: scala LU con lancetta e zona di riferimento
                graphics.DrawArc(pen, 2, 6, 20, 20, 200, 140);
                using (var target = new Pen(color, 3f)) graphics.DrawArc(target, 2, 6, 20, 20, 262, 18);
                Line(12, 16, 9, 9);
                graphics.FillEllipse(fill, 10.5f, 14.5f, 3, 3);
                graphics.DrawLine(thin, 6, 21, 18, 21);
                break;
            case 4: // Bilanciamento: bilancia L/R con fulcro
                Line(3, 10, 21, 10);
                graphics.FillPolygon(fill, new[] { new PointF(12, 11.5f), new PointF(9.5f, 16), new PointF(14.5f, 16) });
                Line(8, 19, 16, 19);
                graphics.FillEllipse(fill, 14, 6.5f, 4, 4);
                graphics.DrawLine(thin, 3, 7, 3, 13);
                graphics.DrawLine(thin, 21, 7, 21, 13);
                break;
            case 5: // Correlazione di fase: due segnali che si sovrappongono
                graphics.DrawEllipse(pen, 3, 6, 12, 12);
                graphics.DrawEllipse(pen, 9, 6, 12, 12);
                using (var overlap = new SolidBrush(Color.FromArgb(Math.Min(255, color.A * 90 / 255), color)))
                using (var a = new GraphicsPath())
                using (var b = new GraphicsPath())
                {
                    a.AddEllipse(3, 6, 12, 12); b.AddEllipse(9, 6, 12, 12);
                    using var region = new Region(a); region.Intersect(b);
                    graphics.FillRegion(overlap, region);
                }
                break;
            case 6: // Livelli RMS: tre meter
                foreach (var (x, h) in new[] { (5f, 10f), (12f, 15f), (19f, 7f) })
                {
                    graphics.DrawLine(thin, x, 3, x, 21);
                    using var bar = new Pen(color, 3.2f) { StartCap = LineCap.Flat, EndCap = LineCap.Round };
                    graphics.DrawLine(bar, x, 21, x, 21 - h);
                }
                break;
            case 7: // Fattore di cresta: picco sopra la linea RMS
                graphics.DrawLines(pen, new[] { new PointF(2, 17), new PointF(7, 17), new PointF(9.5f, 12), new PointF(12, 3), new PointF(14.5f, 12), new PointF(17, 17), new PointF(22, 17) });
                using (var rms = new Pen(Color.FromArgb(Math.Min(255, color.A * 160 / 255), color), 1.4f) { DashStyle = DashStyle.Dash }) graphics.DrawLine(rms, 2, 21, 22, 21);
                graphics.DrawLine(thin, 12, 4.5f, 12, 20);
                break;
        }
        graphics.Restore(state);
    }
}
