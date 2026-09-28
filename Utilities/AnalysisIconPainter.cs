#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CinecorePlayer2025.Utilities;

// Optical 24 px audio glyphs, shared by tabs, chart titles and compact metrics.
internal static class AnalysisIconPainter
{
    internal static void Draw(Graphics graphics, Rectangle bounds, int icon, Color color)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(bounds.Left, bounds.Top);
        graphics.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        void Line(float x, float y, float x2, float y2) => graphics.DrawLine(pen, x, y, x2, y2);
        void Box(float x, float y, float w, float h)
        {
            using var path = new GraphicsPath();
            const float r = 2;
            path.AddArc(x,y,r*2,r*2,180,90); path.AddArc(x+w-r*2,y,r*2,r*2,270,90);
            path.AddArc(x+w-r*2,y+h-r*2,r*2,r*2,0,90); path.AddArc(x,y+h-r*2,r*2,r*2,90,90);
            path.CloseFigure(); graphics.DrawPath(pen,path);
        }
        switch (icon)
        {
            case 0:
                Box(3,3,7,7); Box(14,3,7,7); Box(3,14,7,7); Box(14,14,7,7);
                break;
            case 1:
                foreach (var (x,h) in new[] { (3,3),(7,7),(12,10),(17,6),(21,2) }) Line(x,12-h,x,12+h);
                break;
            case 2:
                foreach (var (x,h) in new[] { (3,5),(6,10),(9,17),(12,13),(15,9),(18,6),(21,3) }) Line(x,21-h,x,21);
                break;
            case 3:
                graphics.DrawPolygon(pen,new[] { new Point(3,9),new Point(7,9),new Point(11,5),new Point(11,19),new Point(7,15),new Point(3,15) });
                graphics.DrawArc(pen,11,7,7,10,-65,130); graphics.DrawArc(pen,10,3,12,18,-65,130);
                break;
            case 4:
                Line(3,12,21,12); Line(3,7,3,17); Line(21,7,21,17);
                Line(8,9,5,12); Line(5,12,8,15); Line(16,9,19,12); Line(19,12,16,15);
                break;
            case 5:
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(2,12,5,-1,8,-1,12,12);
                    path.AddBezier(12,12,16,25,19,25,22,12);
                    graphics.DrawPath(pen,path);
                }
                using (var axis = new Pen(Color.FromArgb(100,color),1)) graphics.DrawLine(axis,2,12,22,12);
                break;
            case 6:
                Line(5,3,5,21); Line(12,3,12,21); Line(19,3,19,21);
                using (var fill = new SolidBrush(color))
                { graphics.FillRectangle(fill,2,7,6,3); graphics.FillRectangle(fill,9,14,6,3); graphics.FillRectangle(fill,16,5,6,3); }
                break;
            case 7:
                graphics.DrawLines(pen,new[] { new Point(2,17),new Point(7,17),new Point(11,4),new Point(15,17),new Point(22,17) });
                using (var rms = new Pen(Color.FromArgb(150,color),1.5f) { DashStyle = DashStyle.Dash }) graphics.DrawLine(rms,3,21,21,21);
                break;
        }
        graphics.Restore(state);
    }
}
