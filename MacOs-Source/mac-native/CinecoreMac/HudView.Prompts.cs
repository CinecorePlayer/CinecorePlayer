using System.Linq;
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace CinecoreMac;

// "Salta intro" e "Prossimo episodio" (HudOverlay.RecalcIntroOutroPromptLayout / DrawIntroOutroPromptButton): capsule
// in basso a destra sopra la fascia dei comandi. Restano a schermo anche quando i comandi spariscono.
public sealed partial class HudView
{
    private const int PromptBarHeight = 54, PromptBarGap = 8, BottomBackdropHeight = 122;
    private bool _showSkipIntro, _showNextEpisode;
    private Rect _rcSkipIntro, _rcNextEpisode;
    private bool _promptHotSkip, _promptHotNext;

    public event Action? SkipIntroClicked, NextEpisodeClicked;

    // Avviso "madVR in risparmio energetico": scheda con titolo, testo e i pulsanti (Passa a MPV, Passa a MPC-VR, Resta su madVR).
    private (string Key, string Text)[] _rendererChoices = Array.Empty<(string, string)>();
    private Rect[] _rcRendererChoices = Array.Empty<Rect>();
    private int _rendererHot = -1;
    private int _rendererSelected;
    private Rect _rcRendererCard;
    public event Action<string>? RendererPromptChosen;
    public bool RendererPromptVisible => _rendererChoices.Length > 0;

    /// <summary>
    /// Menu del tasto destro aperto: l'avviso resta in sospeso (non si disegna, non prende clic ne' tasti) e
    /// ricompare quando il menu si chiude. Il menu sta sempre sopra.
    /// </summary>
    public bool PromptsHeld
    {
        get => _promptsHeld;
        set { if (_promptsHeld == value) return; _promptsHeld = value; _rendererHot = -1; InvalidateVisual(); }
    }
    private bool _promptsHeld;

    internal bool HandleChoicePromptKey(Key key)
    {
        if (!RendererPromptVisible || _promptsHeld) return false;
        switch (key)
        {
            case Key.Left: case Key.Up:
                _rendererSelected = Math.Max(0, _rendererSelected - 1);
                break;
            case Key.Right: case Key.Down:
                _rendererSelected = Math.Min(_rendererChoices.Length - 1, _rendererSelected + 1);
                break;
            case Key.Enter:
                ActivatePromptChoice(_rendererSelected);
                return true;
            default:
                return false;
        }
        InvalidateVisual();
        return true;
    }

    private void ActivatePromptChoice(int index)
    {
        string key = _rendererChoices[index].Key;
        HideRendererPrompt();
        RendererPromptChosen?.Invoke(key);
    }

    private string _promptTitle = "madVR in risparmio energetico";
    private string _promptBody = "Con il risparmio energetico attivo madVR può andare a scatti. Vuoi usare un altro motore finché resta attivo?";

    public void ShowRendererPrompt(params (string Key, string Text)[] choices) =>
        ShowChoicePrompt("madVR in risparmio energetico", "Con il risparmio energetico attivo madVR può andare a scatti. Vuoi usare un altro motore finché resta attivo?", choices);

    /// <summary>Scheda con titolo, testo e pulsanti (il primo, in fondo a destra, e' quello consigliato).</summary>
    public void ShowChoicePrompt(string title, string body, params (string Key, string Text)[] choices)
    {
        _promptTitle = title; _promptBody = body;
        _rendererChoices = choices; _rendererHot = -1; _rendererSelected = 0;
        if (choices.Length > 0) Poke();
        InvalidateVisual();
    }

    public void HideRendererPrompt() => ShowRendererPrompt();

    /// <summary>Prova (QA): sceglie la voce <paramref name="index"/> dell'avviso aperto.</summary>
    internal void TestChoosePrompt(int index) { if (index >= 0 && index < _rendererChoices.Length) ActivatePromptChoice(index); }

    public bool PromptActive => _showSkipIntro || _showNextEpisode || RendererPromptVisible;

    public void SetIntroOutroPrompts(bool skipIntro, bool nextEpisode)
    {
        if (_showSkipIntro == skipIntro && _showNextEpisode == nextEpisode) return;
        bool appearing = (skipIntro && !_showSkipIntro) || (nextEpisode && !_showNextEpisode);
        _showSkipIntro = skipIntro; _showNextEpisode = nextEpisode;
        // Quando compare, i comandi si mostrano una volta; poi svaniscono come sempre e resta il pulsante da solo.
        if (appearing) Poke();
        InvalidateVisual();
    }

    private void LayoutPrompts(double width, double height)
    {
        _rcSkipIntro = _rcNextEpisode = default;
        _rcRendererCard = default;
        _rcRendererChoices = new Rect[_promptsHeld ? 0 : _rendererChoices.Length];
        if (_rendererChoices.Length > 0)
        {
            if (_promptsHeld) return;
            // Ha la precedenza: intro e prossimo episodio non compaiono nei primi secondi del film.
            // Scheda come le finestre dell'app ("Vai a"): titolo, testo, pulsanti; il primo (in fondo a destra) e' quello consigliato.
            int n = _rendererChoices.Length;
            // Al centro della finestra (libero e velato): titolo, testo e scelte centrati, le scelte in riga come gruppo.
            double cw = Math.Min(560, Math.Max(260, width - 48)), ch = 170;
            double cx = Math.Round((width - cw) / 2);
            double cy = Math.Round((height - ch) / 2);
            _rcRendererCard = new Rect(cx, cy, cw, ch);
            double bh = 34, gapB = 28, by = cy + ch - bh;
            var widths = _rendererChoices.Select(c => Math.Ceiling(MeasureText(c.Text, 13) + 24)).ToArray();
            double total = widths.Sum() + gapB * (n - 1), bx = cx + (cw - total) / 2;
            for (int i = 0; i < n; i++) { _rcRendererChoices[i] = new Rect(bx, by, widths[i], bh); bx += widths[i] + gapB; }
            return;
        }
        int count = (_showSkipIntro ? 1 : 0) + (_showNextEpisode ? 1 : 0);
        if (count == 0) return;
        double gap = count > 1 ? 12 : 0;
        double available = Math.Max(104, width - 32 - gap);
        double buttonW = Math.Min(214, Math.Max(150, Math.Floor(available / count))), buttonH = 44;
        double x = Math.Max(16, width - (buttonW * count + gap) - 30);
        double barTop = Math.Max(_rcTopBar.Bottom + 8, height - BottomBackdropHeight - PromptBarGap - PromptBarHeight);
        double y = barTop + (PromptBarHeight - buttonH) / 2;
        if (_showSkipIntro) { _rcSkipIntro = new Rect(x, y, buttonW, buttonH); x += buttonW + gap; }
        if (_showNextEpisode) _rcNextEpisode = new Rect(x, y, buttonW, buttonH);
    }

    private void DrawPrompts(DrawingContext ctx)
    {
        if (_rendererChoices.Length > 0 && _rcRendererChoices.Length == _rendererChoices.Length)
        {
            // Libero e velato come i menu della libreria (richiesta dell'utente): niente scheda, la pagina sotto si vela e
            // titolo, testo e scelte stanno direttamente sul velo; la prima scelta in evidenza, le altre tenui.
            var card = _rcRendererCard;
            var page = new Rect(Bounds.Size);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), page);
            var vignette = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.35), new GradientStop(Color.FromArgb(110, 0, 0, 0), 1) },
            };
            ctx.FillRectangle(vignette, page);
            CardLine(ctx, _promptTitle, 15 * 96 / 72 * 1.05, SemiBold, Colors.White, new Rect(card.X, card.Y, card.Width, 34), TextAlignment.Center);
            var body = UiText.Get(L10n.T(_promptBody), Regular, 9.5 * 96 / 72 * .98, Color.FromArgb(200, 255, 255, 255), card.Width, 3, TextAlignment.Center, TextTrimming.WordEllipsis);
            ctx.DrawText(body, new Point(card.X, card.Y + 44));
            for (int i = 0; i < _rendererChoices.Length; i++)
            {
                var rc = _rcRendererChoices[i];
                bool selected = _rendererSelected == i, hot = _rendererHot == i;
                if (selected)
                    ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(58, 255, 255, 255)),
                        new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), 1), rc, rc.Height / 2, rc.Height / 2);
                byte ink = (byte)(hot || selected ? 255 : 170);
                CardLine(ctx, _rendererChoices[i].Text, 10 * 96 / 72 * 1.02, selected || hot ? SemiBold : Regular, Color.FromArgb(ink, 255, 255, 255), rc, TextAlignment.Center);
            }
            return;
        }
        if (_showSkipIntro) DrawPromptButton(ctx, _rcSkipIntro, "Salta intro", false, _promptHotSkip);
        if (_showNextEpisode) DrawPromptButton(ctx, _rcNextEpisode, "Prossimo episodio", true, _promptHotNext);
    }

    private static void DrawPromptButton(DrawingContext ctx, Rect rc, string text, bool nextTrack, bool hover)
    {
        if (rc.Width <= 0) return;
        double radius = rc.Height / 2;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(96, 0, 0, 0)), null, rc.Translate(new Vector(0, Math.Max(2, rc.Height / 14))), radius, radius);
        ctx.DrawRectangle(new SolidColorBrush(hover ? Color.FromArgb(240, 245, 247, 250) : Color.FromArgb(214, 14, 18, 24)),
            new Pen(new SolidColorBrush(hover ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255)), Math.Max(1, rc.Height / 30)), rc, radius, radius);
        var ink = hover ? Color.FromRgb(12, 16, 22) : Color.FromRgb(245, 247, 250);
        // Simbolo a destra: due triangoli (avanti veloce), con la barra per "prossimo episodio".
        double glyph = Math.Round(rc.Height * .30), glyphRight = rc.Right - Math.Round(rc.Height * .52), centre = rc.Y + rc.Height / 2;
        double bar = nextTrack ? Math.Max(2, Math.Floor(glyph / 4)) : 0, gx = glyphRight - bar - glyph * 2;
        var brush = new SolidColorBrush(ink);
        for (int k = 0; k < 2; k++)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(gx + k * glyph, centre - glyph * .62), true);
                c.LineTo(new Point(gx + (k + 1) * glyph, centre));
                c.LineTo(new Point(gx + k * glyph, centre + glyph * .62));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, g);
        }
        if (nextTrack) ctx.FillRectangle(brush, new Rect(glyphRight - bar, centre - glyph * .62, bar, glyph * 1.24));
        double textLeft = rc.X + Math.Round(rc.Height * .52);
        double maxW = Math.Max(10, glyphRight - glyph * 2 - Math.Round(rc.Height * .25) - textLeft);
        // Inter e' piu' larga di Segoe UI Semibold: il carattere cala (fino all'80%) finche' la scritta entra intera.
        double size = Math.Max(11, rc.Height * .37);
        CachedText Make(double em) => UiText.Get(L10n.T(text), SemiBold, em, ink, maxW);
        double fit = size;
        for (double k = 1; k >= .8; k -= .04)
        {
            fit = size * k;
            if (UiText.Get(L10n.T(text), SemiBold, fit, ink).Width <= maxW) break;
        }
        var t = Make(fit);
        ctx.DrawText(t, new Point(textLeft, centre - t.Height / 2));
    }

    private static void CardLine(DrawingContext ctx, string text, double size, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        var ft = UiText.Get(L10n.T(text), face, size, color, r.Width, 1, align);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    /// <summary>Clic su un pulsante (anche con i comandi nascosti): true se gestito.</summary>
    private bool PromptMouseDown(Point p)
    {
        for (int i = 0; i < _rcRendererChoices.Length && i < _rendererChoices.Length; i++)
            if (_rcRendererChoices[i].Contains(p)) { ActivatePromptChoice(i); return true; }
        if (_rcRendererChoices.Length > 0 && _rcRendererCard.Contains(p)) return true;
        if (_showSkipIntro && _rcSkipIntro.Contains(p)) { SkipIntroClicked?.Invoke(); return true; }
        if (_showNextEpisode && _rcNextEpisode.Contains(p)) { NextEpisodeClicked?.Invoke(); return true; }
        return false;
    }

    private void PromptMouseMove(Point p)
    {
        int hot = -1;
        for (int i = 0; i < _rcRendererChoices.Length; i++) if (_rcRendererChoices[i].Contains(p)) hot = i;
        if (hot != _rendererHot) { _rendererHot = hot; InvalidateVisual(); }
        bool skip = _showSkipIntro && _rcSkipIntro.Contains(p), next = _showNextEpisode && _rcNextEpisode.Contains(p);
        if (skip != _promptHotSkip || next != _promptHotNext) { _promptHotSkip = skip; _promptHotNext = next; InvalidateVisual(); }
    }
}
