using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;

namespace CinecoreMac;

public sealed partial class HudView
{
    private bool _infoOpen;
    private DateTime _infoOpenStart, _infoCloseStart = DateTime.MinValue;
    private Rect _infoCloseRect;
    private bool _infoCloseHover;
    private Rect _infoBounds;
    private double _infoScroll, _infoScrollMax;
    private const double InfoOpenMs = 460, InfoCloseMs = 300;

    /// <summary>Dati del pannello: titolo, riga di riepilogo e sezioni.</summary>
    public Func<(string Title, string Summary, IReadOnlyList<(string Title, IReadOnlyList<(string Label, string Value)> Cells)> Sections)?>? InfoProvider { get; set; }

    /// <summary>Chiusura chiesta dal pannello stesso (freccia).</summary>
    public event Action? InfoCloseRequested;

    private (string Title, string Summary, IReadOnlyList<(string Title, IReadOnlyList<(string Label, string Value)> Cells)> Sections)? _infoData;

    public bool InfoOpen => _infoOpen;
    private bool Closing => _infoCloseStart != DateTime.MinValue;
    private bool InfoVisible => _infoOpen || Closing;

    private Rect InfoSheetBounds => _infoBounds;

    private void ResetInfo()
    {
        _infoOpen = false; _infoCloseStart = DateTime.MinValue;
        _infoBounds = _infoCloseRect = default; _infoCloseHover = false; _infoScroll = _infoScrollMax = 0;
    }

    public void OpenInfo()
    {
        _infoData = null;
        ResetCast();
        DismissPreview();
        if (_infoOpen && !Closing) { InvalidateVisual(); return; }
        _infoBounds = default;
        _infoScroll = 0;
        _infoOpen = true;
        _infoCloseStart = DateTime.MinValue;
        _infoOpenStart = DateTime.UtcNow;
        Poke();
        InvalidateVisual();
    }

    public void CloseInfo()
    {
        if (!_infoOpen) return;
        _infoOpen = false;
        _infoCloseStart = DateTime.UtcNow;
        _infoCloseRect = default;
        _infoCloseHover = false;
        Poke();
        InvalidateVisual();
    }

    /// <summary>I dati sono cambiati: si ridisegna il pannello.</summary>
    public void InfoChanged()
    {
        _infoData = null;
        if (InfoVisible) InvalidateVisual();
    }

    /// <summary>Animazione in corso (da ridisegnare)?</summary>
    private bool InfoTick()
    {
        var now = DateTime.UtcNow;
        if (Closing)
        {
            if ((now - _infoCloseStart).TotalMilliseconds >= InfoCloseMs) _infoCloseStart = DateTime.MinValue;
            return true;
        }
        return _infoOpen && (now - _infoOpenStart).TotalMilliseconds < InfoOpenMs + 60;
    }

    /// <summary>0 = nascosto, 1 = aperto.</summary>
    private double InfoProgress()
    {
        var now = DateTime.UtcNow;
        if (Closing)
        {
            double t = Math.Clamp((now - _infoCloseStart).TotalMilliseconds / InfoCloseMs, 0, 1);
            return 1 - t * t * (3 - 2 * t);
        }
        double o = Math.Clamp((now - _infoOpenStart).TotalMilliseconds / InfoOpenMs, 0, 1);
        return 1 - Math.Pow(1 - o, 3); // CastEase
    }

    private void InfoMouseMove(Point p)
    {
        bool hover = _infoCloseRect.Contains(p);
        if (hover != _infoCloseHover) { _infoCloseHover = hover; InvalidateVisual(); }
    }

    // Caratteri di CastFont: punti * 96 / 72. Segoe UI Semibold -> Inter SemiBold, Segoe UI -> Inter Regular.
    private static double Pt(double points) => points * 96 / 72;

    private void DrawInfoSheet(DrawingContext ctx, double width, double height)
    {
        var data = _infoData ??= InfoProvider?.Invoke();
        double progress = InfoProgress();
        if (progress <= .001) return;

        double margin = Math.Clamp(width * .04, 24, 58);
        double contentWidth = width - 2 * margin;
        double top = 20 + Math.Round(TopChromeInset / _scale) - Math.Round((1 - progress) * 14);
        const double gap = 24, headingH = 27, cellH = 32, rowGap = 14;
        double gridTop = top + 66;
        var sections = data?.Sections;
        int count = sections?.Count ?? 0;
        int columns = Math.Clamp((int)((contentWidth + gap) / 205), 1, Math.Max(1, count));
        double columnWidth = (contentWidth - gap * (columns - 1)) / columns;
        double bodyHeight = 0;
        for (int i = 0; i < count; i += columns)
        {
            int cells = sections!.Skip(i).Take(columns).Max(section => section.Cells.Count);
            bodyHeight += headingH + cells * cellH + (i == 0 ? 0 : rowGap);
        }

        double bottomLimit = Math.Min(height * .57, _rcTimeline.Top - 16);
        double viewportHeight = Math.Max(32, Math.Min(bodyHeight, bottomLimit - gridTop - 34));
        _infoScrollMax = Math.Max(0, bodyHeight - viewportHeight);
        _infoScroll = Math.Clamp(_infoScroll, 0, _infoScrollMax);
        double closeY = gridTop + viewportHeight;
        _infoBounds = new Rect(0, 0, width, closeY + 34);
        _infoCloseRect = _infoOpen ? new Rect((width - 60) / 2, closeY, 60, 34) : default;

        using var fade = ctx.PushOpacity(progress);
        var shadeHeight = Math.Min(height, _infoBounds.Bottom + 106);
        var shade = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(228, 5, 8, 13), 0),
                new GradientStop(Color.FromArgb(203, 5, 8, 13), closeY / shadeHeight),
                new GradientStop(Color.FromArgb(0, 5, 8, 13), 1),
            },
        };
        ctx.FillRectangle(shade, new Rect(0, 0, width, shadeHeight));
        var ink = Color.FromRgb(248, 250, 252);
        var quiet = Color.FromRgb(190, 201, 215);

        ShadowText(ctx, data?.Title ?? Title, 22, new Rect(margin, top, contentWidth, 32), ink);
        ShadowText(ctx, data?.Summary ?? "Lettura delle informazioni…", 13,
            new Rect(margin, top + 35, contentWidth, 22), quiet, face: Regular);

        var grid = new Rect(margin, gridTop, contentWidth, viewportHeight);
        using (ctx.PushClip(grid))
        {
            double rowTop = gridTop - _infoScroll, rowBottom = rowTop;
            for (int i = 0; i < count; i++)
            {
                int column = i % columns;
                if (i > 0 && column == 0) rowTop = rowBottom + rowGap;
                var (title, cells) = sections![i];
                double x = margin + column * (columnWidth + gap), y = rowTop;
                ShadowText(ctx, title, 14, new Rect(x, y, columnWidth, 19), ink);
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(53, 222, 232, 242)), new Rect(x, y + 21, columnWidth, 1));
                y += headingH;
                foreach (var (label, value) in cells)
                {
                    ShadowText(ctx, label, 11, new Rect(x, y, columnWidth, 14), quiet, face: Regular);
                    ShadowText(ctx, value, 13, new Rect(x, y + 13, columnWidth, 17), ink);
                    y += cellH;
                }
                rowBottom = Math.Max(rowBottom, y);
            }
        }

        if (_infoScrollMax > 0)
        {
            double thumb = Math.Max(12, viewportHeight * viewportHeight / bodyHeight);
            double offset = (viewportHeight - thumb) * _infoScroll / _infoScrollMax;
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(100, 221, 231, 242)),
                new Rect(grid.Right + 8, grid.Top + offset, 2, thumb), 1);
        }
        if (_infoCloseRect.Width > 0)
        {
            var center = _infoCloseRect.Center;
            var pen = new Pen(new SolidColorBrush(_infoCloseHover ? Accent : ink), 1.8, lineCap: PenLineCap.Round);
            ctx.DrawLine(pen, new Point(center.X - 8, center.Y + 3), new Point(center.X, center.Y - 3));
            ctx.DrawLine(pen, new Point(center.X, center.Y - 3), new Point(center.X + 8, center.Y + 3));
        }
    }
}
