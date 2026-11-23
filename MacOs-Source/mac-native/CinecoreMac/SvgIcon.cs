using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace CinecoreMac;

/// <summary>
/// Icone SVG dell'HUD (le stesse dei file in Assets), disegnate come geometrie vettoriali e tinte con un unico
/// colore come faceva GetSvgBitmap su Windows: ogni riempimento o tratto che non e' "none" prende la tinta.
/// </summary>
public sealed class SvgIcon
{
    private sealed record Part(Geometry Geometry, bool Fill, bool Stroke, double StrokeWidth, PenLineCap Cap, PenLineJoin Join, double Opacity = 1);

    private readonly List<Part> _parts = new();
    private readonly Rect _viewBox;
    private static readonly ConcurrentDictionary<string, SvgIcon?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Stessi nomi dell'app Windows (Utilities/AssetIconService.cs).
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["close"] = "hud_icons_new/exit-svgrepo-com.svg",
        ["library"] = "Icons.Lucide/library-big.svg",
        ["play"] = "icons/player-play.svg",
        ["pause"] = "icons/player-pause.svg",
        ["skip-back"] = "icons/player-skip-back.svg",
        ["skip-forward"] = "icons/player-skip-forward.svg",
        ["previous"] = "icons/player-track-prev.svg",
        ["next"] = "icons/player-track-next.svg",
        ["maximize"] = "icons/maximize.svg",
        ["info"] = "icons/info-circle.svg",
        ["settings"] = "hud_icons/settings.svg",
        ["stream-quality"] = "Icons.Lucide/sliders-horizontal.svg",
        ["search"] = "hud_icons/search.svg",
        ["folder"] = "hud_icons_new/folder-svgrepo-com.svg",
        ["calendar"] = "hud_icons_new/calendar-svgrepo-com.svg",
        ["time"] = "hud_icons_new/clock-0500-svgrepo-com.svg",
        ["queue-play"] = "Icons.Uniform/queue-play.svg",
        ["collection-add"] = "Icons.Uniform/collection-add.svg",
        ["star"] = "hud_icons/star.svg",
        ["music"] = "hud_icons/music.svg",
        ["scan"] = "hud_icons/scan.svg",
        ["computer"] = "hud_icons/computer.svg",
        ["network"] = "hud_icons/network.svg",
        ["add"] = "hud_icons/add.svg",
        ["trash"] = "hud_icons_new/exit-svgrepo-com.svg",
        ["mute"] = "icons/volume-off.svg",
        ["volume"] = "icons/volume.svg",
        ["volume-low"] = "icons/volume-2.svg",
        ["volume-high"] = "icons/volume-4.svg",
    };

    /// <summary>
    /// L'icona che l'app Windows usa per quel nome: stessa tabella e stessi alias (AssetIconService.Resolve, condiviso).
    /// </summary>
    public static SvgIcon? Win(string key)
    {
        return Cache.GetOrAdd("win:" + key, _ =>
        {
            string? path = CinecorePlayer2025.Utilities.AssetIconService.Resolve(key);
            if (path == null || !path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            try { return new SvgIcon(XDocument.Load(path)); } catch { return null; }
        });
    }

    /// <summary>Le icone per nome di file nelle cartelle Lucide e Uniform (come la barra delle foto di Windows).</summary>
    public static SvgIcon? Named(string name)
    {
        return Cache.GetOrAdd("file:" + name, _ =>
        {
            foreach (string set in new[] { "Icons.Lucide", "Icons.Uniform" })
            {
                string path = Path.Combine(MacTools.AssetsDirectory, set, name + ".svg");
                if (!File.Exists(path)) continue;
                try { return new SvgIcon(XDocument.Load(path)); } catch { return null; }
            }
            return null;
        });
    }

    public static SvgIcon? Get(string name)
    {
        // Prima l'icona che userebbe Windows (Icons.Uniform/Icons.Custom tramite la sua tabella): le icone del Mac
        // erano le vecchie hud_icons e non coincidevano con quelle dell'app Windows.
        if (Win(name) is { } same) return same;
        return Cache.GetOrAdd(name, n =>
        {
            string rel = Names.TryGetValue(n, out var r) ? r : n;
            string path = Path.Combine(MacTools.AssetsDirectory, rel);
            if (!File.Exists(path))
            {
                // Nomi dell'app Windows non ancora in tabella: si cerca il file per nome.
                string? found = Directory.Exists(MacTools.AssetsDirectory)
                    ? Directory.EnumerateFiles(MacTools.AssetsDirectory, n + ".svg", SearchOption.AllDirectories).FirstOrDefault()
                    : null;
                if (found == null) return null;
                path = found;
            }
            try { return new SvgIcon(XDocument.Load(path)); } catch { return null; }
        });
    }

    private SvgIcon(XDocument doc)
    {
        var root = doc.Root!;
        _viewBox = ParseViewBox(root) ?? new Rect(0, 0, 24, 24);
        var style = new Style();
        Walk(root, Inherit(style, root));
    }

    private sealed class Style
    {
        public string Fill = "#000", Stroke = "none";
        public double StrokeWidth = 1;
        /// <summary>opacity / stroke-opacity (le tacche spente delle icone di livello).</summary>
        public double Opacity = 1;
        public PenLineCap Cap = PenLineCap.Flat;
        public PenLineJoin Join = PenLineJoin.Miter;
        public Matrix Transform = Matrix.Identity;
        public Style Clone() => (Style)MemberwiseClone();
    }

    private static string? Attr(XElement e, string name)
    {
        var a = e.Attribute(name)?.Value;
        if (a != null) return a;
        // style="fill:none;stroke:#000;stroke-width:2"
        var style = e.Attribute("style")?.Value;
        if (style == null) return null;
        foreach (var part in style.Split(';'))
        {
            var kv = part.Split(':', 2);
            if (kv.Length == 2 && kv[0].Trim() == name) return kv[1].Trim();
        }
        return null;
    }

    private static Style Inherit(Style parent, XElement e)
    {
        var s = parent.Clone();
        if (Attr(e, "fill") is { } f) s.Fill = f;
        if (Attr(e, "stroke") is { } st) s.Stroke = st;
        if (Attr(e, "stroke-width") is { } sw && double.TryParse(sw.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double w)) s.StrokeWidth = w;
        if (Attr(e, "stroke-linecap") is { } cap) s.Cap = cap switch { "round" => PenLineCap.Round, "square" => PenLineCap.Square, _ => PenLineCap.Flat };
        if (Attr(e, "stroke-linejoin") is { } join) s.Join = join switch { "round" => PenLineJoin.Round, "bevel" => PenLineJoin.Bevel, _ => PenLineJoin.Miter };
        foreach (var key in new[] { "opacity", "stroke-opacity" })
            if (Attr(e, key) is { } op && double.TryParse(op, NumberStyles.Float, CultureInfo.InvariantCulture, out double o)) s.Opacity *= Math.Clamp(o, 0, 1);
        if (e.Attribute("transform")?.Value is { } t) s.Transform = ParseTransform(t) * s.Transform;
        return s;
    }

    private void Walk(XElement parent, Style style)
    {
        foreach (var e in parent.Elements())
        {
            var s = Inherit(style, e);
            string name = e.Name.LocalName;
            if (name is "g" or "svg") { Walk(e, s); continue; }
            if (name is "defs" or "title" or "desc" or "clipPath" or "mask") continue;
            Geometry? g = name switch
            {
                "path" => e.Attribute("d")?.Value is { } d ? StreamGeometry.Parse(d) : null,
                "line" => new LineGeometry(new Point(N(e, "x1"), N(e, "y1")), new Point(N(e, "x2"), N(e, "y2"))),
                "circle" => new EllipseGeometry(new Rect(N(e, "cx") - N(e, "r"), N(e, "cy") - N(e, "r"), 2 * N(e, "r"), 2 * N(e, "r"))),
                "ellipse" => new EllipseGeometry(new Rect(N(e, "cx") - N(e, "rx"), N(e, "cy") - N(e, "ry"), 2 * N(e, "rx"), 2 * N(e, "ry"))),
                "rect" => new RectangleGeometry(new Rect(N(e, "x"), N(e, "y"), N(e, "width"), N(e, "height")), N(e, "rx"), e.Attribute("ry") != null ? N(e, "ry") : N(e, "rx")),
                "polyline" => Poly(e.Attribute("points")?.Value, false),
                "polygon" => Poly(e.Attribute("points")?.Value, true),
                _ => null,
            };
            if (g == null) continue;
            if (!s.Transform.IsIdentity) g.Transform = new MatrixTransform(s.Transform);
            bool fill = !string.Equals(s.Fill, "none", StringComparison.OrdinalIgnoreCase) && name is not "line" and not "polyline";
            bool stroke = !string.Equals(s.Stroke, "none", StringComparison.OrdinalIgnoreCase);
            _parts.Add(new Part(g, fill, stroke, s.StrokeWidth, s.Cap, s.Join, s.Opacity));
        }
    }

    private static double N(XElement e, string a) =>
        double.TryParse(e.Attribute(a)?.Value?.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static Geometry? Poly(string? points, bool close)
    {
        if (string.IsNullOrWhiteSpace(points)) return null;
        var n = points.Split(new[] { ' ', ',', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (n.Length < 4) return null;
        var sb = new System.Text.StringBuilder("M");
        for (int i = 0; i + 1 < n.Length; i += 2) sb.Append(i == 0 ? " " : " L ").Append(n[i]).Append(',').Append(n[i + 1]);
        if (close) sb.Append(" Z");
        return StreamGeometry.Parse(sb.ToString());
    }

    private static Rect? ParseViewBox(XElement root)
    {
        var v = root.Attribute("viewBox")?.Value?.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (v is { Length: 4 } && v.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return new Rect(double.Parse(v[0], CultureInfo.InvariantCulture), double.Parse(v[1], CultureInfo.InvariantCulture),
                double.Parse(v[2], CultureInfo.InvariantCulture), double.Parse(v[3], CultureInfo.InvariantCulture));
        if (double.TryParse(root.Attribute("width")?.Value?.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
            && double.TryParse(root.Attribute("height")?.Value?.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
            return new Rect(0, 0, w, h);
        return null;
    }

    private static Matrix ParseTransform(string t)
    {
        var m = Matrix.Identity;
        foreach (var part in t.Split(')', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('(', 2);
            if (kv.Length != 2) continue;
            var a = kv[1].Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            Matrix step = kv[0].Trim() switch
            {
                "translate" => Matrix.CreateTranslation(a[0], a.Length > 1 ? a[1] : 0),
                "scale" => Matrix.CreateScale(a[0], a.Length > 1 ? a[1] : a[0]),
                "matrix" when a.Length == 6 => new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]),
                "rotate" => Matrix.CreateRotation(a[0] * Math.PI / 180),
                _ => Matrix.Identity,
            };
            m = step * m;
        }
        return m;
    }

    /// <summary>Disegna l'icona nel quadrato <paramref name="dest"/> con la tinta <paramref name="tint"/>.</summary>
    public void Draw(DrawingContext ctx, Rect dest, Color tint)
    {
        double s = Math.Min(dest.Width / _viewBox.Width, dest.Height / _viewBox.Height);
        double ox = dest.X + (dest.Width - _viewBox.Width * s) / 2 - _viewBox.X * s;
        double oy = dest.Y + (dest.Height - _viewBox.Height * s) / 2 - _viewBox.Y * s;
        var brush = new SolidColorBrush(tint);
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
            foreach (var p in _parts)
            {
                var b = p.Opacity >= 1 ? brush : new SolidColorBrush(tint, tint.A / 255.0 * p.Opacity);
                ctx.DrawGeometry(p.Fill ? b : null,
                    p.Stroke ? new Pen(b, p.StrokeWidth, lineCap: p.Cap, lineJoin: p.Join) : null, p.Geometry);
            }
    }
}
