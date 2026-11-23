using System;
using Avalonia;
using Avalonia.Media;

namespace CinecoreMac;

/// <summary>
/// MusicTransportBar.DrawSymbol di Windows: prima l'SVG che Windows trova per quel nome (AssetIconService), altrimenti
/// gli stessi simboli disegnati a tratto su una griglia di 24 unita'. Cosi' le icone sono le stesse dell'app Windows.
/// </summary>
internal static class WinSymbol
{
    public static void Draw(DrawingContext ctx, Rect box, string icon, Color color)
    {
        if (SvgIcon.Win(icon) is { } svg) { svg.Draw(ctx, box, color); return; }
        // Come la barra delle foto di Windows: per nome di file in Icons.Lucide e Icons.Uniform (tranne Condividi, disegnata).
        if (icon != "share" && SvgIcon.Named(icon) is { } named) { named.Draw(ctx, box, color); return; }
        double k = box.Width / 24;
        Point P(double x, double y) => new(box.X + x * k, box.Y + y * k);
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, Math.Max(1, 1.6 * k), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        void Line(double x1, double y1, double x2, double y2) => ctx.DrawLine(pen, P(x1, y1), P(x2, y2));
        void Poly(bool fill, params Point[] pts) => ctx.DrawGeometry(fill ? brush : null, fill ? null : pen, new PolylineGeometry(pts, true));
        void Arc(double x, double y, double w, double h, double start, double sweep)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                double cx = x + w / 2, cy = y + h / 2;
                Point At(double deg) => P(cx + w / 2 * Math.Cos(deg * Math.PI / 180), cy + h / 2 * Math.Sin(deg * Math.PI / 180));
                c.BeginFigure(At(start), false);
                for (int i = 1; i <= 24; i++) c.LineTo(At(start + sweep * i / 24));
                c.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, g);
        }
        switch (icon)
        {
            case "play": Poly(true, P(7, 3), P(20, 12), P(7, 21)); break;
            case "pause": ctx.FillRectangle(brush, new Rect(P(7, 4), P(10.5, 20))); ctx.FillRectangle(brush, new Rect(P(14, 4), P(17.5, 20))); break;
            case "previous": Poly(true, P(18, 5), P(7, 12), P(18, 19)); ctx.FillRectangle(brush, new Rect(P(5, 5), P(7, 19))); break;
            case "next": Poly(true, P(6, 5), P(17, 12), P(6, 19)); ctx.FillRectangle(brush, new Rect(P(17, 5), P(19, 19))); break;
            case "more": for (int x = 5; x <= 19; x += 7) ctx.DrawEllipse(brush, null, P(x, 12), k, k); break;
            case "shuffle":
                ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(3, 6), P(7, 6), P(17, 18), P(21, 18) }, false));
                ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(3, 18), P(7, 18), P(17, 6), P(21, 6) }, false));
                Line(18, 3, 21, 6); Line(18, 9, 21, 6); Line(18, 15, 21, 18); Line(18, 21, 21, 18);
                break;
            case "repeat": case "repeat-one": Arc(3, 5, 18, 14, 180, 160); Arc(3, 5, 18, 14, 0, 160); Line(3, 5, 3, 11); Line(3, 11, 8, 11); Line(21, 13, 21, 19); Line(16, 13, 21, 13); break;
            case "list": ctx.DrawRectangle(null, pen, new Rect(P(3, 4), P(21, 18))); Line(6, 21, 10, 18); Line(7, 8, 17, 8); Line(7, 12, 15, 12); break;
            case "queue": Line(4, 5, 20, 5); Line(4, 10, 20, 10); Line(4, 15, 20, 15); Line(4, 20, 14, 20); break;
            case "close": Line(6, 6, 18, 18); Line(18, 6, 6, 18); break;
            case "info": ctx.DrawEllipse(null, pen, P(12, 12), 9 * k, 9 * k); Line(12, 11, 12, 17); ctx.DrawEllipse(brush, null, P(12, 7.6), k, k); break;
            case "person": ctx.DrawEllipse(null, pen, P(12, 7), 4 * k, 4 * k); Arc(4, 13, 16, 14, 200, 140); break;
            case "add": Line(12, 5, 12, 19); Line(5, 12, 19, 12); break;
            case "heart":
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(P(12, 20), false);
                    c.CubicBezierTo(P(10, 18), P(3, 13), P(3, 8)); c.CubicBezierTo(P(3, 2), P(9, 2), P(12, 7));
                    c.CubicBezierTo(P(15, 2), P(21, 2), P(21, 8)); c.CubicBezierTo(P(21, 13), P(14, 18), P(12, 20));
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(null, pen, g);
                break;
            }
            case "wave": Line(3, 10, 3, 14); Line(7, 6, 7, 18); Line(12, 2, 12, 22); Line(17, 7, 17, 17); Line(21, 10, 21, 14); break;
            case "playlist-add": ctx.DrawRectangle(null, pen, new Rect(P(3, 3), P(17, 20))); Line(7, 7, 13, 7); Line(7, 11, 13, 11); Line(18, 14, 18, 22); Line(14, 18, 22, 18); break;
            case "queue-add": Line(3, 5, 18, 5); Line(3, 10, 18, 10); Line(3, 15, 11, 15); Line(16, 17, 22, 17); Line(19, 14, 19, 20); break;
            case "library": Line(4, 4, 4, 20); Line(9, 4, 9, 20); Line(14, 4, 14, 20); Line(18, 4, 21, 20); break;
            case "pip": ctx.DrawRectangle(null, pen, new Rect(P(3, 5), P(21, 19))); ctx.FillRectangle(brush, new Rect(P(12, 11), P(19, 17))); break;
            case "nowplaying": ctx.DrawRectangle(null, pen, new Rect(P(4, 4), P(20, 20))); Line(13, 8, 16, 8); Line(16, 8, 16, 11); Line(16, 8, 12, 12); Line(8, 13, 8, 16); Line(8, 16, 11, 16); Line(8, 16, 12, 12); break;
            case "stop": ctx.FillRectangle(brush, new Rect(P(6, 6), P(18, 18)), (float)(1.5 * k)); break;
            case "volume": case "mute":
                Poly(false, P(3, 9), P(7, 9), P(12, 5), P(12, 19), P(7, 15), P(3, 15));
                if (icon == "volume") Arc(11, 5, 10, 14, -65, 130); else { Line(16, 9, 22, 15); Line(22, 9, 16, 15); }
                break;
            case "lyrics": Line(4, 6, 20, 6); Line(4, 11, 20, 11); Line(4, 16, 13, 16); ctx.DrawEllipse(null, pen, P(17.5, 18), 2.5 * k, 2.5 * k); Line(20, 18, 20, 11); break;
            case "share": Line(12, 3.5, 12, 15); Poly(false, P(8, 7.5), P(12, 3.5), P(16, 7.5)); ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(5, 12), P(5, 20), P(19, 20), P(19, 12) }, false)); break;
            case "check": ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(5, 12.5), P(10, 17), P(19, 7) }, false)); break;
        }
    }
}
