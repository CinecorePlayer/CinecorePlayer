using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Aggiornamenti (UpdateForm di Windows), nello stile del tutorial iniziale: pagina velata, titolo e testo al centro,
/// pulsanti a pillola. Controlla il canale, mostra le novita', scarica e installa riaprendo l'app.
/// </summary>
internal sealed class UpdateView : Control
{
    private static readonly FontFamily Display = new(AppAssets.Root + "Assets/Fonts#Inter Display");
    private static readonly Typeface Regular = new(Display), Semibold = new(Display, FontStyle.Normal, FontWeight.SemiBold);

    private enum Phase { Checking, UpToDate, NotConfigured, NotInstalled, Available, Downloading, Ready, Failed }

    public event Action? Closed;
    /// <summary>Lo script di sostituzione e' partito: l'app deve chiudersi.</summary>
    public event Action? QuitRequested;

    private Phase _phase;
    private MacUpdate.Release? _release;
    private string _error = "";
    private double _progress;
    private string? _script;
    private CancellationTokenSource? _cts;
    private Point _mouse;
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private DateTime _openedAt;
    private readonly List<(Rect Rect, string Key)> _hits = new();

    public UpdateView() { IsVisible = false; Focusable = true; ClipToBounds = true; }

    public bool IsOpen => IsVisible;

    /// <summary>Apre e controlla subito il canale (Strumenti › Controlla aggiornamenti…).</summary>
    public void Open() { Show(); _ = CheckAsync(); }

    /// <summary>Apre gia' con la versione trovata dal controllo all'avvio.</summary>
    public void Offer(MacUpdate.Release release) { _release = release; Show(); _phase = Phase.Available; }

    private void Show()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _error = ""; _progress = 0; _script = null;
        _openedAt = DateTime.UtcNow;
        IsVisible = true; Focus();
        InvalidateVisual();
    }

    public void Close()
    {
        if (!IsVisible) return;
        // Lo scaricamento si ferma; uno script gia' pronto non parte (l'app non si chiude da sola).
        _cts?.Cancel();
        IsVisible = false;
        Closed?.Invoke();
    }

    private async Task CheckAsync()
    {
        if (!MacUpdate.Configured) { _phase = Phase.NotConfigured; InvalidateVisual(); return; }
        _phase = Phase.Checking; InvalidateVisual();
        var token = _cts!.Token;
        try
        {
            var release = await Task.Run(() => MacUpdate.LatestAsync(token), token);
            if (token.IsCancellationRequested) return;
            _release = release;
            _phase = release == null || release.Version <= MacUpdate.Current ? Phase.UpToDate : Phase.Available;
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { _error = ex.Message; _phase = Phase.Failed; }
        catch { return; }
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private async Task InstallAsync()
    {
        if (_release is not { } release) return;
        if (MacUpdate.InstalledBundle == null) { _phase = Phase.NotInstalled; InvalidateVisual(); return; }
        _phase = Phase.Downloading; _progress = 0; InvalidateVisual();
        var token = _cts!.Token;
        var progress = new Progress<double>(p => { _progress = p; InvalidateVisual(); });
        try
        {
            _script = await Task.Run(() => MacUpdate.PrepareAsync(release, progress, token), token);
            _phase = Phase.Ready;
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { _error = ex.Message; _phase = Phase.Failed; }
        catch { return; }
        InvalidateVisual();
    }

    private void Activate(string key)
    {
        switch (key)
        {
            case "close": Close(); break;
            case "retry": _ = CheckAsync(); break;
            case "install": _ = InstallAsync(); break;
            case "skip":
                if (_release != null) MacUpdate.SkippedVersion = _release.Version.ToString();
                Close(); break;
            case "auto": MacUpdate.AutoCheck = !MacUpdate.AutoCheck; InvalidateVisual(); break;
            case "page":
                if (_release?.PageUrl is { } page && Uri.TryCreate(page, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    try { PlatformShell.OpenUrl(uri.AbsoluteUri); } catch { }
                break;
            case "restart":
                if (_script == null) return;
                try { MacUpdate.Launch(_script); QuitRequested?.Invoke(); }
                catch (Exception ex) { _error = ex.Message; _phase = Phase.Failed; InvalidateVisual(); }
                break;
        }
    }

    public void HandleKey(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape && _phase != Phase.Downloading) Close();
        else if (e.Key == Key.Enter)
        {
            if (_phase == Phase.Available) _ = InstallAsync();
            else if (_phase == Phase.Ready) Activate("restart");
            else if (_phase != Phase.Downloading && _phase != Phase.Checking) Close();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(now); _mouse = now; if (changed) InvalidateVisual(); e.Handled = true; }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        var p = e.GetPosition(this);
        foreach (var (rect, key) in _hits) if (rect.Contains(p)) { Activate(key); return; }
    }

    // ------------------------------------------------------------------ disegno

    private static IBrush B(Color c) => new SolidColorBrush(c);
    private static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static CachedText Format(string text, double pt, Typeface face, Color color, double maxWidth, int maxLines, TextAlignment align)
        => UiText.Get(L10n.T(text), face, pt * 96 / 72, color, maxWidth, maxLines, align);

    private static double Measure(string text, double pt, Typeface face) =>
        UiText.Measure(L10n.T(text), face, pt * 96 / 72);

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        _hits.Clear();
        var page = new Rect(Bounds.Size);
        bool light = UiTheme.IsLight;
        Color text = UiTheme.Text;
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 380.0, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        // Come il tutorial: la pagina resta appena visibile dietro.
        ctx.FillRectangle(B(A(UiTheme.Panel, light ? (byte)242 : (byte)244)), page);
        _hits.Add((page, "")); // i clic non passano sotto

        double s = Math.Clamp(Math.Min(page.Width / 1280, page.Height / 800), 0.85, 1.4);
        double width = Math.Min(page.Width - 80 * s, 640 * s), left = page.X + (page.Width - width) / 2;
        double slide = Math.Round(14 * s * (1 - e));
        byte alpha = (byte)Math.Round(255 * e);
        Color ink = A(text, alpha), soft = A(text, (byte)(alpha * 0.72));

        string version = _release != null ? "Cinecore Player " + _release.Version : "";
        (string Title, string Body) copy = _phase switch
        {
            Phase.Checking => ("Cerco aggiornamenti…", "Controllo se c'è una versione nuova di Cinecore Player."),
            Phase.UpToDate => ("Sei aggiornato", "Hai già l'ultima versione: " + MacUpdate.DisplayName + "."),
            Phase.NotConfigured => ("Aggiornamenti in arrivo", "Il canale degli aggiornamenti per Mac non è ancora attivo. Quando lo sarà, Cinecore controllerà da solo all'avvio."),
            Phase.NotInstalled => ("Aggiornamento non installabile qui", "Questa copia non è un'app installata (build di sviluppo): scarica la nuova versione a mano."),
            Phase.Available => ("È disponibile " + version, "Stai usando " + MacUpdate.DisplayName + ". L'aggiornamento si scarica, si controlla e Cinecore si riapre da solo."),
            Phase.Downloading => ("Scarico " + version, "Non chiudere Cinecore: dopo lo scaricamento il pacchetto viene controllato."),
            Phase.Ready => ("Pronto da installare", version + " è scaricato e verificato. Cinecore si chiude, si aggiorna e si riapre."),
            _ => ("Aggiornamento non riuscito", _error.Length > 0 ? _error : "Riprova fra poco."),
        };

        double y = page.Y + Math.Max(90 * s, (page.Height - 420 * s) / 2);
        var title = Format(copy.Title, Math.Max(20, 30 * s), Semibold, ink, width, 2, TextAlignment.Center);
        ctx.DrawText(title, new Point(left, y + slide));
        y += title.Height + 22 * s;
        var body = Format(copy.Body, Math.Max(10.5, 13 * s), Regular, soft, width, 5, TextAlignment.Center);
        ctx.DrawText(body, new Point(left, y + slide));
        y += body.Height + 30 * s;

        // Novita' della versione (testo della release), poche righe.
        if (_phase == Phase.Available && _release is { Notes.Length: > 0 } rel)
        {
            var notes = Format(rel.Notes.Replace("\r", "").Replace("#", "").Trim(), Math.Max(10, 12 * s), Regular, A(text, (byte)(alpha * 0.6)), width, 8, TextAlignment.Center);
            ctx.DrawText(notes, new Point(left, y + slide));
            y += notes.Height + 30 * s;
        }

        if (_phase == Phase.Downloading)
        {
            var track = new Rect(left + width * .15, y + 8 * s, width * .7, 6 * s);
            ctx.DrawRectangle(B(A(text, 40)), null, track, track.Height / 2, track.Height / 2);
            ctx.DrawRectangle(B(A(text, 235)), null, track.WithWidth(Math.Max(track.Height, track.Width * _progress)), track.Height / 2, track.Height / 2);
            var pct = Format(Math.Round(_progress * 100) + "%", Math.Max(10, 11.5 * s), Regular, soft, width, 1, TextAlignment.Center);
            ctx.DrawText(pct, new Point(left, track.Bottom + 12 * s));
            y = track.Bottom + 60 * s;
        }

        // Pulsanti: il primo e' quello pieno (bianco), gli altri tenui; come il tutorial.
        var buttons = _phase switch
        {
            Phase.Available => new List<(string, string)> { ("Scarica e installa", "install"), ("Più tardi", "close"), ("Salta questa versione", "skip") },
            Phase.Ready => new() { ("Riavvia e aggiorna", "restart"), ("Più tardi", "close") },
            Phase.Failed => new() { ("Riprova", "retry"), ("Chiudi", "close") },
            Phase.Downloading or Phase.Checking => new() { ("Annulla", "close") },
            _ => new() { ("Chiudi", "close") },
        };
        if (_phase == Phase.Available && _release?.PageUrl != null) buttons.Insert(2, ("Novità complete", "page"));
        double h = 48 * s, pt = Math.Max(9.4, 11.8 * s), gap = 16 * s;
        var widths = buttons.ConvertAll(b => Measure(b.Item1, pt, Semibold) + 56 * s);
        double total = gap * (buttons.Count - 1);
        foreach (double w in widths) total += w;
        double x = page.X + (page.Width - total) / 2;
        for (int i = 0; i < buttons.Count; i++)
        {
            var r = new Rect(x, y, widths[i], h);
            bool primary = i == 0 && _phase is not (Phase.Downloading or Phase.Checking or Phase.UpToDate or Phase.NotConfigured or Phase.NotInstalled);
            bool hover = Hot(r);
            Color fill = primary
                ? (light ? Color.FromArgb(255, (byte)(hover ? 60 : 20), 24, 30) : Color.FromArgb((byte)(hover ? 228 : 255), 255, 255, 255))
                : (light ? Color.FromArgb((byte)(hover ? 30 : 16), 0, 0, 0) : Color.FromArgb((byte)(hover ? 70 : 46), 255, 255, 255));
            ctx.DrawRectangle(B(A(fill, (byte)(fill.A * e))), null, r, h / 2, h / 2);
            var label = Format(buttons[i].Item1, pt, Semibold, primary ? (light ? Colors.White : Colors.Black) : ink, r.Width, 1, TextAlignment.Center);
            ctx.DrawText(label, new Point(r.X, r.Y + (h - label.Height) / 2));
            _hits.Add((r, buttons[i].Item2));
            x += widths[i] + gap;
        }
        y += h + 34 * s;

        // Interruttore del controllo all'avvio, sotto, discreto (miUpdateAuto di Windows).
        if (_phase is not (Phase.Downloading or Phase.Ready))
        {
            string auto = (MacUpdate.AutoCheck ? "✓ " : "") + L10n.T("Cerca aggiornamenti all'avvio");
            double aw = Measure(auto, Math.Max(9.5, 11.4 * s), Regular);
            var ar = new Rect(page.X + (page.Width - aw) / 2, y, aw, 28 * s);
            var label = Format(auto, Math.Max(9.5, 11.4 * s), Regular, A(text, Hot(ar.Inflate(8)) ? (byte)(alpha) : (byte)(alpha * 0.6)), aw + 2, 1, TextAlignment.Left);
            ctx.DrawText(label, new Point(ar.X, ar.Y + (ar.Height - label.Height) / 2));
            _hits.Add((ar.Inflate(8), "auto"));
        }
        // Ordine: i pulsanti vincono sul fondo (ultimo aggiunto prima).
        _hits.Reverse();
    }
}
