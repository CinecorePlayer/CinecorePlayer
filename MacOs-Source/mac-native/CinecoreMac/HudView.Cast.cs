using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CinecoreMac;

// Traduzione di HUD/HudOverlay.Cast.cs. Il cast del film in riproduzione: ritratti grandi, nomi e personaggi
// direttamente sulla vignettatura del video (nessuna fascia opaca), frecce ai lati per scorrere e la freccia di
// chiusura al centro. Mentre e' aperto prende il posto di timeline e comandi; si chiude anche con il tasto del cast,
// con Esc o freccia giu'. Misure in unita' di base (dentro la scala dell'HUD, come CastD su Windows).
public sealed partial class HudView
{
    private bool _castOpen;
    private string _castTitle = "", _castMeta = "";
    private readonly List<(string Name, string Role, string? Photo)> _castMembers = new();
    private int _castFirst, _castVisibleCount;
    private DateTime _castOpenStart, _castScrollStart, _castCloseStart = DateTime.MinValue;
    private double _castScrollFrom;
    private const double CastOpenMs = 520, CastScrollMs = 380, CastCloseMs = 300;
    private Rect _castCloseRect, _castLeftRect, _castRightRect;
    private bool _castCloseHover;
    private double _castW = 1280, _castH = 720;

    /// <summary>Il tasto del cast compare solo se del film si conoscono gli interpreti (SyncCastButton).</summary>
    public bool CastAvailable { get; set; }
    /// <summary>Il tasto della qualita' (flussi Jellyfin): sceglie la velocita' della transcodifica.</summary>
    public bool QualityAvailable { get; set; }
    public string QualityLabel { get; set; } = "Qualità";
    public event Action? CastClicked;
    public event Action<Point>? QualityClicked; // punto sotto il tasto, in coordinate dell'HUD
    /// <summary>Ritratti: percorso o indirizzo -> immagine gia' pronta (null finche' si scarica).</summary>
    public Func<string?, double, Bitmap?>? ImageLoader { get; set; }

    public bool CastOpen => _castOpen;
    private bool CastClosing => _castCloseStart != DateTime.MinValue;
    private bool CastVisible => _castOpen || CastClosing;

    private static double CastEase(double t) { t = Math.Clamp(t, 0, 1); return 1 - Math.Pow(1 - t, 3); }

    private double CastScrollNow()
    {
        double t = (DateTime.UtcNow - _castScrollStart).TotalMilliseconds / CastScrollMs;
        return t >= 1 ? _castFirst : _castScrollFrom + (_castFirst - _castScrollFrom) * CastEase(t);
    }

    private Rect CastSheetBounds
    {
        get
        {
            double height = Math.Min(528, Math.Max(1, Math.Floor(_castH * .58)));
            return new Rect(0, Math.Max(0, _castH - height), _castW, height);
        }
    }

    public void OpenCast(string title, string meta, IEnumerable<(string Name, string Role, string? Photo)> members)
    {
        ResetInfo();
        DismissPreview();
        _castMembers.Clear();
        _castMembers.AddRange(members.Where(m => !string.IsNullOrWhiteSpace(m.Name)).Take(20));
        if (_castMembers.Count == 0) return;
        _castTitle = title ?? ""; _castMeta = meta ?? "";
        _castOpen = true;
        _castCloseStart = DateTime.MinValue;
        _castFirst = 0; _castScrollFrom = 0; _castScrollStart = DateTime.MinValue;
        _castOpenStart = DateTime.UtcNow;
        Poke();
        InvalidateVisual();
    }

    public void CloseCast()
    {
        if (!_castOpen) return;
        _castOpen = false;
        _castCloseStart = DateTime.UtcNow;
        _castCloseRect = _castLeftRect = _castRightRect = default;
        Poke();
        InvalidateVisual();
    }

    private void ResetCast()
    {
        _castOpen = false; _castCloseStart = DateTime.MinValue;
        _castMembers.Clear(); _castCloseRect = _castLeftRect = _castRightRect = default;
        _castCloseHover = false;
    }

    public void ResetPanels()
    {
        ResetCast(); ResetInfo(); DismissPreview();
        InvalidateVisual();
    }

    public void MoveCast(int delta)
    {
        if (!_castOpen || delta == 0) return;
        int maximum = Math.Max(0, _castMembers.Count - Math.Max(1, _castVisibleCount));
        int next = Math.Clamp(_castFirst + Math.Sign(delta) * Math.Max(1, _castVisibleCount - 1), 0, maximum);
        if (next == _castFirst) return;
        _castScrollFrom = CastScrollNow();
        _castScrollStart = DateTime.UtcNow;
        _castFirst = next;
        Poke();
        InvalidateVisual();
    }

    /// <summary>Animazione in corso (da ridisegnare)?</summary>
    private bool CastTick()
    {
        var now = DateTime.UtcNow;
        if (CastClosing)
        {
            if ((now - _castCloseStart).TotalMilliseconds >= CastCloseMs) { _castCloseStart = DateTime.MinValue; _castMembers.Clear(); }
            return true;
        }
        return _castOpen && ((now - _castOpenStart).TotalMilliseconds < CastOpenMs + 400 || (now - _castScrollStart).TotalMilliseconds < CastScrollMs + 20);
    }

    /// <summary>Clic nella scheda: true se l'ha gestito lei.</summary>
    private bool CastMouseDown(Point p)
    {
        if (!_castOpen) return false;
        if (_castCloseRect.Contains(p)) { CloseCast(); return true; }
        if (_castLeftRect.Contains(p)) { MoveCast(-1); return true; }
        if (_castRightRect.Contains(p)) { MoveCast(+1); return true; }
        return CastSheetBounds.Contains(p);
    }

    private void CastMouseMove(Point p)
    {
        bool hover = _castCloseRect.Contains(p);
        if (hover != _castCloseHover) { _castCloseHover = hover; InvalidateVisual(); }
    }

    private static CachedText CastText(string text, double points, bool semibold, Color color, double maxWidth, int lines = 1, TextAlignment align = TextAlignment.Left) =>
        UiText.Get(L10n.T(text), semibold ? SemiBold : Regular, Pt(points), color, maxWidth, lines, align);

    private void DrawCastSheet(DrawingContext ctx, double width, double height)
    {
        _castW = width; _castH = height;
        if (!CastVisible || _castMembers.Count == 0) return;
        if (CastClosing)
        {
            // In chiusura la scheda scende (accelerando) fino a uscire dallo schermo.
            double t = Math.Clamp((DateTime.UtcNow - _castCloseStart).TotalMilliseconds / CastCloseMs, 0, 1);
            double drop = Math.Round(t * t * (CastSheetBounds.Height + 40));
            var sheet = CastSheetBounds;
            using (ctx.PushClip(new Rect(0, sheet.Top, width, height - sheet.Top)))
            using (ctx.PushTransform(Matrix.CreateTranslation(0, drop)))
                DrawCastSheetCore(ctx, width);
            return;
        }
        DrawCastSheetCore(ctx, width);
    }

    private void DrawCastSheetCore(DrawingContext ctx, double width)
    {
        Rect sheet = CastSheetBounds;
        double left = 64, top = sheet.Top + 28;
        // Freccia di chiusura al centro, sulla riga del titolo.
        _castCloseRect = _castOpen ? new Rect(Math.Floor((width - 72) / 2), top + 18, 72, 44) : default;
        {
            var color = _castCloseHover ? Colors.White : Color.FromArgb(225, 248, 251, 253);
            double cx = Math.Floor((width - 72) / 2) + 36, cy = top + 18 + 22;
            DrawPolyline(ctx, color, 2.2, new Point(cx - 14, cy - 7), new Point(cx, cy + 7), new Point(cx + 14, cy - 7));
        }

        // Intestazione: "Cast" tenue sopra, titolo grande, riga dei dati.
        double now = (DateTime.UtcNow - _castOpenStart).TotalMilliseconds;
        double headRise = Math.Round((1 - CastEase(now / CastOpenMs)) * 26);
        using (ctx.PushClip(sheet))
        {
            ctx.DrawText(CastText("Cast", 9, false, Color.FromRgb(205, 217, 226), width / 2), new Point(left, top + headRise + 2));
            var titleText = CastText(_castTitle, 17, true, Colors.White, width - left * 2);
            ctx.DrawText(titleText, new Point(left, top + 20 + headRise + (40 - titleText.Height) / 2));
            if (_castMeta.Length > 0)
                ctx.DrawText(CastText(_castMeta, 9, false, Color.FromRgb(200, 212, 222), width - left * 2), new Point(left, top + 60 + headRise + 2));
        }

        var content = new Rect(16, top + 96, width - 32, Math.Max(1, sheet.Bottom - (top + 96) - 14));
        const double arrowW = 52, gap = 22;
        double viewportLeft = content.Left + arrowW + 18, viewportRight = content.Right - arrowW - 18;
        double viewportWidth = Math.Max(1, viewportRight - viewportLeft);
        // Ritratti grandi: l'altezza disponibile decide la larghezza, poi quanti ne stanno.
        double portraitHeightWanted = Math.Max(120, content.Height - 82);
        double preferredWidth = Math.Max(110, Math.Round(portraitHeightWanted / 1.32));
        int visible = Math.Clamp((int)((viewportWidth + gap) / Math.Max(60, preferredWidth + gap)), 2, 12);
        visible = Math.Min(visible, _castMembers.Count);
        _castVisibleCount = visible;
        int maxFirst = Math.Max(0, _castMembers.Count - visible);
        _castFirst = Math.Clamp(_castFirst, 0, maxFirst);
        double cardWidth = Math.Floor(Math.Min(preferredWidth, (viewportWidth - gap * Math.Max(0, visible - 1)) / Math.Max(1, visible)));
        double totalWidth = cardWidth * visible + gap * Math.Max(0, visible - 1);
        double cardX = viewportLeft + Math.Max(0, Math.Floor((viewportWidth - totalWidth) / 2));
        double portraitTop = content.Top;
        double portraitHeight = Math.Min(portraitHeightWanted, Math.Round(cardWidth * 1.32));

        double scroll = Math.Clamp(CastScrollNow(), 0, maxFirst);
        using (ctx.PushClip(sheet.Intersect(new Rect(viewportLeft - 6, sheet.Top, viewportWidth + 12, sheet.Height))))
        {
            int firstIndex = Math.Max(0, (int)Math.Floor(scroll)), lastIndex = Math.Min(_castMembers.Count - 1, (int)Math.Ceiling(scroll) + visible - 1);
            for (int index = firstIndex; index <= lastIndex; index++)
            {
                var person = _castMembers[index];
                // All'apertura ogni ritratto sale in posizione poco dopo il precedente.
                double cardT = (now - 60 - Math.Max(0, index - firstIndex) * 45) / (CastOpenMs - 80);
                double rise = Math.Round((1 - CastEase(cardT)) * 46);
                double x = cardX + Math.Round((index - scroll) * (cardWidth + gap));
                var portrait = new Rect(x, portraitTop + rise, cardWidth, portraitHeight);
                var shape = new RoundedRect(portrait, 8);
                using (ctx.PushClip(shape))
                {
                    if (ImageLoader?.Invoke(person.Photo, cardWidth) is { } photo)
                    {
                        double pw = photo.Size.Width, ph = photo.Size.Height;
                        double scale = Math.Max(portrait.Width / pw, portrait.Height / ph);
                        double sw = portrait.Width / scale, sh = portrait.Height / scale;
                        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                            ctx.DrawImage(photo, new Rect((pw - sw) / 2, (ph - sh) * .18, sw, sh), portrait);
                    }
                    else
                    {
                        ctx.FillRectangle(new LinearGradientBrush
                        {
                            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                            GradientStops = { new GradientStop(Color.FromRgb(40, 54, 66), 0), new GradientStop(Color.FromRgb(14, 20, 27), 1) },
                        }, portrait);
                        string initials = string.Concat(person.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => char.ToUpperInvariant(part[0])));
                        var it = CastText(initials, 24, true, Color.FromRgb(202, 216, 227), portrait.Width, align: TextAlignment.Center);
                        ctx.DrawText(it, new Point(portrait.X, portrait.Y + (portrait.Height - it.Height) / 2));
                    }
                }
                // Testi senza riquadro di fondo: stanno sulla vignettatura.
                var name = CastText(person.Name, 10.5, true, Colors.White, portrait.Width + 8, align: TextAlignment.Center);
                ctx.DrawText(name, new Point(portrait.Left - 4, portrait.Bottom + 10 + (26 - name.Height) / 2));
                var role = CastText(string.IsNullOrWhiteSpace(person.Role) ? "Interprete" : person.Role, 9, false, Color.FromRgb(196, 210, 221), portrait.Width, 2, TextAlignment.Center);
                ctx.DrawText(role, new Point(portrait.Left, portrait.Bottom + 36));
            }
        }

        _castLeftRect = new Rect(content.Left, portraitTop + Math.Max(0, (portraitHeight - arrowW) / 2), arrowW, arrowW);
        _castRightRect = new Rect(content.Right - arrowW, _castLeftRect.Top, arrowW, arrowW);
        DrawCastChevron(ctx, _castLeftRect, false, _castFirst > 0);
        DrawCastChevron(ctx, _castRightRect, true, _castFirst < maxFirst);
    }

    private static void DrawCastChevron(DrawingContext ctx, Rect rect, bool right, bool enabled)
    {
        var color = enabled ? Color.FromArgb(235, 248, 251, 253) : Color.FromArgb(70, 186, 202, 214);
        double cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2, sign = right ? 1 : -1;
        DrawPolyline(ctx, color, 2.2, new Point(cx - sign * 8, cy - 15), new Point(cx + sign * 8, cy), new Point(cx - sign * 8, cy + 15));
    }

    private static void DrawPolyline(DrawingContext ctx, Color color, double thickness, params Point[] points)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(points[0], false);
            for (int i = 1; i < points.Length; i++) c.LineTo(points[i]);
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(color), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
    }
}
