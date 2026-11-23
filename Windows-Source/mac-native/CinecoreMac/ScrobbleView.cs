using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Scheda "Ascolti" (ScrobbleForm di Windows): Last.fm con l'autorizzazione nel browser (nessuna password passa dal
/// player) e ListenBrainz con il token del profilo. Due righe, un filo fra loro, Chiudi in basso a destra.
/// </summary>
internal sealed class ScrobbleView : Control
{
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);
    private const double W = 600, Gutter = 30, Span = 540, LastFmTop = 118, ListenBrainzTop = 206;

    public event Action? Closed;

    private readonly TextBox _token;
    private string _lastFmNote = "", _listenBrainzNote = "";
    private bool _lastFmBusy, _listenBrainzBusy;
    private Point _mouse;
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private readonly System.Collections.Generic.List<(Rect Rect, string Kind)> _hits = new();
    private DateTime _openedAt;

    public ScrobbleView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        _token = new TextBox { Watermark = "Token del profilo ListenBrainz", PasswordChar = '•' };
        WizardSheet.StyleBox(_token);
        _token.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = ListenBrainzClicked(); } };
        VisualChildren.Add(_token);
        LogicalChildren.Add(_token);
        MusicScrobbler.DeliveryChanged += () => Dispatcher.UIThread.Post(InvalidateVisual);
    }

    public bool IsOpen => IsVisible;

    public void Open()
    {
        _lastFmNote = _listenBrainzNote = "";
        _token.Text = "";
        // Colori del tema: qui, mai dentro Render (Avalonia non vuole controlli cambiati durante il disegno).
        _token.Foreground = B(UiTheme.Text); _token.CaretBrush = B(UiTheme.Text);
        _token.Resources["TextControlPlaceholderForeground"] = B(UiTheme.WithAlpha(UiTheme.Text, 90));
        _token.Resources["TextControlPlaceholderForegroundFocused"] = B(UiTheme.WithAlpha(UiTheme.Text, 90));
        _openedAt = DateTime.UtcNow;
        IsVisible = true; Focus();
        InvalidateVisual();
    }

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        Closed?.Invoke();
    }

    /// <summary>Tornando all'app dopo l'autorizzazione nel browser il collegamento si completa da solo (Activated di Windows).</summary>
    public void AppActivated()
    {
        if (IsVisible && !MusicScrobbler.LastFmConnected && MusicScrobbler.LastFmPendingToken != null && !_lastFmBusy) _ = LastFmClicked();
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            PlatformShell.OpenUrl(uri.AbsoluteUri);
        }
        catch { }
    }

    private async Task LastFmClicked()
    {
        if (_lastFmBusy) return;
        _lastFmBusy = true; InvalidateVisual();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            if (MusicScrobbler.LastFmConnected)
            {
                // Per qualche secondo dopo il collegamento il clic non scollega (il clic di ritorno dal browser).
                if (DateTime.UtcNow - MusicScrobbler.LastFmConnectedAtUtc > TimeSpan.FromSeconds(4)) { MusicScrobbler.LastFmDisconnect(); _lastFmNote = ""; }
            }
            else if (MusicScrobbler.LastFmPendingToken == null)
            {
                var step = await MusicScrobbler.LastFmBeginAsync(timeout.Token);
                if (step == null) _lastFmNote = "Last.fm non risponde: riprova fra poco.";
                else
                {
                    MusicScrobbler.LastFmPendingToken = step.Value.Token;
                    OpenUrl(step.Value.Url);
                    _lastFmNote = "Autorizza Cinecore Player nella pagina che si è aperta: il collegamento si completa da solo.";
                }
            }
            else
            {
                string pending = MusicScrobbler.LastFmPendingToken;
                bool connected = await MusicScrobbler.LastFmCompleteAsync(pending, timeout.Token);
                if (connected) MusicScrobbler.LastFmPendingToken = null;
                _lastFmNote = connected ? ""
                    : MusicScrobbler.LastFmPendingToken != null ? "Non ancora autorizzato: consenti l'accesso nel browser, poi premi di nuovo."
                    : "L'autorizzazione non risulta: riprova da Collega.";
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[SCROBBLE] Last.fm connect: " + ex.GetType().Name); _lastFmNote = "Collegamento non riuscito: controlla la rete."; }
        _lastFmBusy = false; InvalidateVisual();
    }

    private async Task ListenBrainzClicked()
    {
        if (_listenBrainzBusy) return;
        _listenBrainzBusy = true; InvalidateVisual();
        try
        {
            if (MusicScrobbler.ListenBrainzConnected) { MusicScrobbler.ListenBrainzDisconnect(); _listenBrainzNote = ""; }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                string? user = await MusicScrobbler.ListenBrainzConnectAsync(_token.Text ?? "", timeout.Token);
                _listenBrainzNote = user == null ? "Token non valido." : "";
                if (user != null) _token.Text = "";
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[SCROBBLE] ListenBrainz connect: " + ex.GetType().Name); _listenBrainzNote = "Collegamento non riuscito: controlla la rete."; }
        _listenBrainzBusy = false; InvalidateVisual();
    }

    // ------------------------------------------------------------------ disegno
    private static double P(double pt) => pt * 96 / 72 * 0.92;
    private static IBrush B(Color c) => new SolidColorBrush(c);

    private static CachedText Text(string text, double pt, Typeface face, Color color, double width, int lines = 1) =>
        UiText.Get(L10n.T(text), face, P(pt), color, width, lines);

    private static void Line(DrawingContext ctx, string text, double pt, Typeface face, Color color, Rect r)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = Text(text, pt, face, color, r.Width);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    /// <summary>Azione di una riga: pillola chiara (come "Avanti" delle schede) o solo testo per "Scollega".</summary>
    private void Action(DrawingContext ctx, string label, double right, double top, string kind, bool busy, bool primary, byte alpha)
    {
        double w = WizardSheet.Measure(label, 18 * _u, WizardSheet.Semibold) + (primary ? 56 : 16) * _u;
        var r = new Rect(right - w, top, w, 48 * _u);
        if (primary) WizardSheet.DrawNext(ctx, r, label, Hot(r), !busy, alpha, _u);
        else WizardSheet.DrawBack(ctx, r, label, Hot(r) && !busy, (byte)(busy ? alpha / 2 : alpha), _u);
        if (!busy) _hits.Add((r, kind));
    }

    private double _u = 1;

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        _hits.Clear();
        var page = new Rect(Bounds.Size);
        if (page.Width < 200 || page.Height < 200) return;
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 380, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        byte alpha = (byte)Math.Round(255 * e);
        double slide = Math.Round(26 * _u * (1 - e));
        // Stile delle schede nuove (WizardSheet): niente riquadro, pagina velata, colonna, Chiudi in alto a destra.
        _u = WizardSheet.Scale(page.Size);
        var column = WizardSheet.Column(page.Size);
        column = new Rect(column.X, Math.Round(page.Height * 0.16), column.Width, column.Height);
        WizardSheet.DrawVeil(ctx, page, e);
        var close = WizardSheet.Cancel(page.Size);
        WizardSheet.DrawCancel(ctx, close, Hot(close), _u, "Chiudi");
        _hits.Add((close, "close"));
        double y = WizardSheet.DrawHeader(ctx, column, _u, 0, 1, "Ascolti",
            "I brani che ascolti finiscono nel tuo profilo. Partono solo artista, titolo, album e durata; se manca la rete vengono inviati più tardi.", alpha, slide);
        double X = column.X + slide, span = column.Width;
        Color ink = WizardSheet.Ink(alpha), soft = WizardSheet.Soft(alpha);
        void Title(string text, double top) => ctx.DrawText(WizardSheet.Text(text, 24 * _u, WizardSheet.Semibold, ink, span * .6), new Point(X, top));
        void Note(string text, double top, double width) =>
            ctx.DrawText(WizardSheet.Text(text, 16 * _u, WizardSheet.Regular, soft, width, 2), new Point(X, top));

        // Last.fm
        string pendingLastFm = MusicScrobbler.LastFmPendingToken ?? "";
        string lastFm = !MusicScrobbler.LastFmAvailable ? "Non disponibile in questa copia del player."
            : _lastFmNote.Length > 0 ? _lastFmNote
            : MusicScrobbler.LastFmConnected ? "Collegato come " + MusicScrobbler.LastFmUser
            : MusicScrobbler.LastFmDeliveryStatus.Length > 0 ? MusicScrobbler.LastFmDeliveryStatus : "Non collegato";
        Title("Last.fm", y);
        Note(lastFm, y + 40 * _u, span - 240 * _u);
        if (MusicScrobbler.LastFmConnected && MusicScrobbler.LastFmDeliveryStatus.Length > 0)
            Note(MusicScrobbler.LastFmDeliveryStatus, y + 66 * _u, span - 240 * _u);
        if (MusicScrobbler.LastFmAvailable)
            Action(ctx, MusicScrobbler.LastFmConnected ? "Scollega" : pendingLastFm.Length > 0 ? "Ho autorizzato" : "Collega", X + span, y + 4 * _u, "lastfm", _lastFmBusy,
                !MusicScrobbler.LastFmConnected, alpha);
        y += 120 * _u;

        // ListenBrainz
        bool listenBrainz = MusicScrobbler.ListenBrainzConnected;
        string lb = _listenBrainzNote.Length > 0 ? _listenBrainzNote
            : listenBrainz ? "Collegato come " + MusicScrobbler.ListenBrainzUser
            : "Il token è nella pagina delle impostazioni del profilo, su listenbrainz.org.";
        Title("ListenBrainz", y);
        Note(lb, y + 40 * _u, span - 240 * _u);
        if (listenBrainz && MusicScrobbler.ListenBrainzDeliveryStatus.Length > 0)
            Note(MusicScrobbler.ListenBrainzDeliveryStatus, y + 66 * _u, span - 240 * _u);
        string lbLabel = listenBrainz ? "Scollega" : "Collega";
        double lbWidth = WizardSheet.Measure(lbLabel, 18 * _u, WizardSheet.Semibold) + 56 * _u;
        var tokenShell = new Rect(X, y + 92 * _u, span - lbWidth - 18 * _u, 56 * _u);
        Action(ctx, lbLabel, X + span, listenBrainz ? y + 4 * _u : tokenShell.Y + 4 * _u, "listenbrainz", _listenBrainzBusy, !listenBrainz, alpha);
        bool showToken = !listenBrainz;
        if (showToken) WizardSheet.DrawUnderline(ctx, tokenShell, _token.IsFocused, _listenBrainzNote == "Token non valido.", alpha, _u);
        var tokenRect = WizardSheet.BoxIn(tokenShell, 22 * _u * 1.5, _u);
        if (_tokenRect != tokenRect || _token.IsVisible != showToken || Math.Abs(_token.FontSize - 22 * _u) > .1)
        {
            _tokenRect = tokenRect;
            double size = 22 * _u;
            Dispatcher.UIThread.Post(() => { _token.IsVisible = showToken; _token.FontSize = size; InvalidateArrange(); }, DispatcherPriority.Render);
        }
        y = (showToken ? tokenShell.Bottom : y + 92 * _u) + 34 * _u;

        int queued = MusicScrobbler.QueuedCount;
        if (queued > 0) Note(queued == 1 ? "1 ascolto in attesa di invio" : queued + " ascolti in attesa di invio", y, span);
    }

    private Rect _tokenRect;

    protected override Size MeasureOverride(Size availableSize)
    {
        _token.Measure(new Size(Math.Max(1, _tokenRect.Width), Math.Max(1, _tokenRect.Height)));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _token.Arrange(_tokenRect);
        return finalSize;
    }

    // ------------------------------------------------------------------ input
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(now); _mouse = now; if (changed) InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Source is TextBox) return;
        e.Handled = true;
        var hit = _hits.LastOrDefault(h => h.Rect.Contains(e.GetPosition(this)));
        switch (hit.Kind)
        {
            case "close": Close(); break;
            case "lastfm": _ = LastFmClicked(); break;
            case "listenbrainz": _ = ListenBrainzClicked(); break;
        }
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
