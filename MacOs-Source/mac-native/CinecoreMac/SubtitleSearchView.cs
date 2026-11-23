using System;
using System.Collections.Generic;
using System.IO;
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
/// Cerca e scarica sottotitoli (SubtitleDownloadForm di Windows): YIFY per i film, Addic7ed (Gestdown) per le serie,
/// Subdl e OpenSubtitles con la chiave dell'utente. Il file scelto si salva accanto al video, riallineato sull'audio se
/// l'analisi e' affidabile (SubtitleWorkflow), e si carica subito. Ai servizi vanno solo titolo, anno e ID pubblici.
/// </summary>
internal sealed class SubtitleSearchView : Control
{
    private static readonly FontFamily Inter = new("avares://CinecoreMac/Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);

    public event Action<string>? Downloaded;
    public event Action? Closed;

    private string _video = "", _title = "";
    private double? _duration;
    private string _language = "it";
    private List<SubtitleSources.Candidate> _results = new();
    private string _status = "", _statusHint = "";
    private bool _busy, _searching;
    private int _downloading = -1;
    private CancellationTokenSource? _work;
    private Point _mouse;
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private double _scroll;
    private readonly List<(Rect Rect, string Kind, int Index)> _hits = new();
    private DateTime _openedAt;

    // Ricerca modificabile: parte da titolo/anno (o serie e episodio) letti dal file e dai metadati; se l'utente la
    // cambia, si cerca con quello che ha scritto (senza gli ID del film, che potrebbero essere sbagliati).
    private readonly TextBox _box;
    private Rect _boxRect, _searchRect, _langRect;
    private SubtitleSources.Query? _autoQuery;
    private string _autoText = "";

    // Lingua: menu a tendina con tutte le lingue (prima si scorreva una alla volta con ‹ ›).
    private bool _langOpen;
    private double _langScroll;
    private Rect _langPanel;
    private readonly DispatcherTimer _spin;

    public SubtitleSearchView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        _box = new TextBox { Watermark = "Titolo del film o della serie (es. Dune 2021, Lost S01E02)" };
        WizardSheet.StyleBox(_box);
        _box.FontFamily = Inter;
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _langOpen = false; _ = SearchAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; if (_langOpen) { _langOpen = false; InvalidateVisual(); } else Close(); }
        };
        _box.GotFocus += (_, _) => { _langOpen = false; InvalidateVisual(); };
        _box.LostFocus += (_, _) => InvalidateVisual();
        VisualChildren.Add(_box);
        LogicalChildren.Add(_box);
        _spin = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => InvalidateVisual());
        _spin.Stop();
    }

    public bool IsOpen => IsVisible;

    /// <summary>Aperta sopra il video: dietro una vignettatura (il film si vede) e testo chiaro, non il fondo pieno.</summary>
    public bool OverVideo { get => _overVideo || (VideoBehind?.Invoke() ?? false); set => _overVideo = value; }
    private bool _overVideo;
    /// <summary>Il video e' visibile sotto (si controlla a ogni disegno: all'apertura il film puo' essere ancora in caricamento).</summary>
    public Func<bool>? VideoBehind { get; set; }
    private Color Ink => OverVideo ? Colors.White : UiTheme.Text;
    private Color MutedInk => OverVideo ? Color.FromRgb(200, 208, 218) : UiTheme.Muted;

    private void DrawBackdrop(DrawingContext ctx, Rect page)
    {
        if (!OverVideo) { WizardSheet.DrawVeil(ctx, page, 1); return; }
        // Velo scuro leggero al centro, piu' fitto verso i bordi (come la vignettatura dell'HUD).
        ctx.FillRectangle(B(Color.FromArgb(120, 0, 0, 0)), page);
        var edge = Color.FromArgb(225, 0, 0, 0);
        ctx.FillRectangle(new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.72, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.78, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.35), new GradientStop(edge, 1) },
        }, page);
    }

    public void Open(string video, string title, double? duration)
    {
        _video = video; _title = title; _duration = duration;
        _language = SubtitleSources.DefaultLanguage is { Length: > 0 } saved ? saved : MacSettings.SubtitleLanguage is { Length: > 0 } mac ? mac : "it";
        _results.Clear(); _scroll = 0; _openedAt = DateTime.UtcNow; _langOpen = false; _downloading = -1;
        _autoQuery = null;
        // Subito il titolo letto dal nome del file; quando arrivano i metadati si completa (se l'utente non l'ha toccato).
        string quick;
        try
        {
            var parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(video);
            quick = DescribeQuery(new SubtitleSources.Query(parsed.NormalizedTitle ?? "", parsed.Year, null, null, parsed.IsTvEpisode,
                parsed.SeriesTitle, parsed.SeasonNumber, parsed.EpisodeNumber, _language));
        }
        catch { quick = Path.GetFileNameWithoutExtension(video); }
        if (string.IsNullOrWhiteSpace(quick)) quick = Path.GetFileNameWithoutExtension(video);
        _autoText = quick;
        ApplyBoxColors();
        _box.Text = quick;
        IsVisible = true; Focus();
        InvalidateMeasure();
        _ = SearchAsync();
        InvalidateVisual();
    }

    private Color? _boxInk;
    private void ApplyBoxColors()
    {
        _boxInk = Ink;
        _box.Foreground = B(Ink); _box.CaretBrush = B(Ink);
        _box.Resources["TextControlPlaceholderForeground"] = B(UiTheme.WithAlpha(Ink, 90));
        _box.Resources["TextControlPlaceholderForegroundFocused"] = B(UiTheme.WithAlpha(Ink, 90));
        _box.Resources["TextControlForeground"] = B(Ink);
        _box.Resources["TextControlForegroundFocused"] = B(Ink);
        _box.Resources["TextControlForegroundPointerOver"] = B(Ink);
        _box.SelectionBrush = B(UiTheme.WithAlpha(Ink, 70));
    }

    public void Close()
    {
        if (!IsVisible) return;
        _work?.Cancel();
        _spin.Stop();
        IsVisible = false;
        Closed?.Invoke();
    }

    /// <summary>Testo della ricerca come lo vede l'utente: "Titolo 2021" o "Serie S01E02".</summary>
    private static string DescribeQuery(SubtitleSources.Query q)
    {
        if (q.IsEpisode && q.Season is int season && q.Episode is int episode)
            return $"{(string.IsNullOrWhiteSpace(q.SeriesTitle) ? q.Title : q.SeriesTitle)} S{season:00}E{episode:00}".Trim();
        return q.Year is int year ? $"{q.Title} {year}".Trim() : q.Title.Trim();
    }

    private static readonly System.Text.RegularExpressions.Regex EpisodeRx = new(@"\b(?:s(\d{1,2})\s*e(\d{1,3})|(\d{1,2})x(\d{1,3}))\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex YearRx = new(@"[\(\[]?\b((?:19|20)\d{2})\b[\)\]]?\s*$");

    /// <summary>Ricerca scritta a mano: titolo, anno facoltativo in fondo, oppure SxxEyy / 1x02 per le serie.</summary>
    private static SubtitleSources.Query ParseTyped(string text, string language)
    {
        string t = text.Trim();
        var ep = EpisodeRx.Match(t);
        if (ep.Success)
        {
            int season = int.Parse(ep.Groups[1].Success ? ep.Groups[1].Value : ep.Groups[3].Value);
            int episode = int.Parse(ep.Groups[2].Success ? ep.Groups[2].Value : ep.Groups[4].Value);
            string series = t[..ep.Index].Trim(' ', '-', '.', '_');
            if (series.Length == 0) series = t.Remove(ep.Index, ep.Length).Trim(' ', '-', '.', '_');
            return new SubtitleSources.Query(series, null, null, null, true, series, season, episode, language);
        }
        int? year = null;
        var y = YearRx.Match(t);
        if (y.Success && y.Index > 0) { year = int.Parse(y.Groups[1].Value); t = t[..y.Index].Trim(' ', '-', '.', '_'); }
        return new SubtitleSources.Query(t, year, null, null, false, t, null, null, language);
    }

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>ID IMDb dal titolo (suggerimenti pubblici di IMDb, senza chiave): solo titolo e anno lasciano il Mac.</summary>
    private static async Task<string?> ImdbIdFromTitleAsync(string title, int? year, CancellationToken token)
    {
        string t = title.Trim();
        if (t.Length < 2) return null;
        try
        {
            string slug = Uri.EscapeDataString(t.ToLowerInvariant());
            string first = char.IsLetterOrDigit(t[0]) ? char.ToLowerInvariant(t[0]).ToString() : "x";
            using var doc = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync($"https://v3.sg.media-imdb.com/suggestion/{Uri.EscapeDataString(first)}/{slug}.json", token));
            if (!doc.RootElement.TryGetProperty("d", out var items)) return null;
            string? best = null;
            foreach (var item in items.EnumerateArray())
            {
                string id = item.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                string kind = item.TryGetProperty("qid", out var q) ? q.GetString() ?? "" : "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^tt\d{5,10}$") || kind is not ("movie" or "tvMovie" or "video")) continue;
                int? y = item.TryGetProperty("y", out var yy) && yy.ValueKind == System.Text.Json.JsonValueKind.Number ? yy.GetInt32() : null;
                if (year is int wanted) { if (y is int found && Math.Abs(found - wanted) <= 1) return id; }
                else return id;
                best ??= id;
            }
            return best;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private async Task SearchAsync()
    {
        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();
        _busy = _searching = true; _downloading = -1; _results.Clear(); _scroll = 0; _statusHint = "";
        _status = "Cerco i sottotitoli in " + SubtitleSources.LanguageLabel(_language, false).ToLowerInvariant() + "…";
        _spin.Start();
        InvalidateVisual();
        try
        {
            string language = _language;
            string typed = (_box.Text ?? "").Trim();
            bool useAuto = typed.Length == 0 || string.Equals(typed, _autoText, StringComparison.OrdinalIgnoreCase);
            var auto = _autoQuery;
            var found = await Task.Run(async () =>
            {
                SubtitleSources.Query query;
                if (!useAuto) query = ParseTyped(typed, language);
                else if (auto != null) query = auto with { Language = language };
                else
                {
                    query = BuildQuery(_video, _duration, language, cts.Token);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        _autoQuery = query;
                        string text = DescribeQuery(query);
                        // Si aggiorna il campo solo se l'utente non ci ha ancora scritto.
                        if (string.Equals((_box.Text ?? "").Trim(), _autoText, StringComparison.OrdinalIgnoreCase) && text.Length > 0)
                        { _autoText = text; _box.Text = text; }
                    });
                }
                // Film senza ID IMDb (niente metadati o ricerca scritta a mano): YIFY cerca solo per ID, lo si chiede dal titolo.
                if (!query.IsEpisode && string.IsNullOrWhiteSpace(query.ImdbId) && await ImdbIdFromTitleAsync(query.Title, query.Year, cts.Token) is { } imdb)
                    query = query with { ImdbId = imdb };
                if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null) Console.Error.WriteLine("[qa] subs query " + query);
                return await SubtitleSources.SearchAsync(query, cts.Token);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            _results = found;
            if (found.Count == 0)
            {
                _status = "Nessun sottotitolo trovato";
                _statusHint = "Controlla il titolo nella ricerca qui sopra (puoi aggiungere l'anno o S01E02) o prova un'altra lingua."
                    + (SubtitleSources.SubdlAvailable ? "" : " Con la chiave Subdl (Impostazioni › Sottotitoli) si cerca in più fonti.");
            }
            else _status = "";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _status = "Ricerca non riuscita"; _statusHint = ex.Message; }
        finally { if (_work == cts) { _busy = _searching = false; _spin.Stop(); } InvalidateVisual(); }
    }

    /// <summary>BuildSubtitleQuery di Windows: titolo, anno e ID pubblici (mai il percorso del file).</summary>
    private static SubtitleSources.Query BuildQuery(string video, double? duration, string language, CancellationToken token)
    {
        MovieMetadataService.MediaTitleInfo parsed;
        try { parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(video); }
        catch { parsed = new MovieMetadataService.MediaTitleInfo { NormalizedTitle = Path.GetFileNameWithoutExtension(video) }; }
        MovieMetadataService.RichMetadata? rich = null;
        try { rich = MovieMetadataService.ResolveRichMetadata(video, duration, AppLanguage.TmdbLocale(CinecoreMac.L10n.Language), token); }
        catch (OperationCanceledException) { throw; }
        catch { }
        bool episode = parsed.IsTvEpisode || string.Equals(rich?.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
        string title = !string.IsNullOrWhiteSpace(parsed.NormalizedTitle) ? parsed.NormalizedTitle : rich?.Title ?? Path.GetFileNameWithoutExtension(video);
        return new SubtitleSources.Query(title, rich?.Year ?? parsed.Year, rich?.ImdbId, rich?.TmdbId, episode,
            !string.IsNullOrWhiteSpace(parsed.SeriesTitle) ? parsed.SeriesTitle : title, parsed.SeasonNumber, parsed.EpisodeNumber, language);
    }

    private async Task DownloadAsync(int index)
    {
        if (_busy || index < 0 || index >= _results.Count) return;
        var candidate = _results[index];
        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();
        _busy = true; _downloading = index; _status = "Scarico da " + candidate.Source + "…"; _statusHint = "";
        _spin.Start(); InvalidateVisual();
        try
        {
            string text = await SubtitleSources.DownloadAsync(candidate, cts.Token);
            _status = "Allineo i tempi sull'audio del film…"; InvalidateVisual();
            var progress = new Progress<double>(v => { _status = $"Allineo i tempi sull'audio del film… {v * 100:0}%"; InvalidateVisual(); });
            string language = _language;
            var outcome = await Task.Run(() => SubtitleWorkflow.SaveDownloadedAsync(_video, language, text, progress, cts.Token), cts.Token);
            SubtitleSources.DefaultLanguage = language;
            Downloaded?.Invoke(outcome.Path);
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status = "Scaricamento non riuscito: " + ex.Message; }
        finally { _busy = false; _spin.Stop(); InvalidateVisual(); }
    }

    private void SetLanguage(string code)
    {
        _langOpen = false;
        if (code == _language) { InvalidateVisual(); return; }
        _language = code;
        _ = SearchAsync();
    }

    private void StepLanguage(int direction)
    {
        var codes = SubtitleSources.AllLanguages.Select(l => l.Code).ToList();
        int i = Math.Max(0, codes.IndexOf(_language));
        SetLanguage(codes[(i + direction + codes.Count) % codes.Count]);
    }

    /// <summary>Lingue del menu: le piu' usate in cima, poi le altre in ordine alfabetico.</summary>
    private static readonly string[] Common = { "it", "en", "es", "fr", "de", "pt-pt", "pt-br" };
    private static List<(string Code, string Label)> MenuLanguages()
    {
        var all = SubtitleSources.AllLanguages.Select(l => (l.Code, Label: SubtitleSources.LanguageLabel(l.Code, false))).ToList();
        var top = Common.Select(c => all.FirstOrDefault(a => a.Code == c)).Where(a => a.Code != null).ToList();
        var rest = all.Where(a => !Common.Contains(a.Code)).OrderBy(a => a.Label, StringComparer.CurrentCulture).ToList();
        return top.Concat(rest).ToList();
    }

    // ------------------------------------------------------------------ disegno (come la scheda di accesso)
    private static double P(double pt) => pt * 96 / 72 * 0.92;
    private static IBrush B(Color c) => new SolidColorBrush(c);
    private const double SheetW = 760, SheetH = 560;

    private void Line(DrawingContext ctx, string text, double pt, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = UiText.Get(L10n.T(text), face, P(pt), color, r.Width, 1, align, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    /// <summary>Testo su piu' righe centrato (stati vuoti).</summary>
    private static void Paragraph(DrawingContext ctx, string text, double pt, Typeface face, Color color, Rect r)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = UiText.Get(L10n.T(text), face, P(pt), color, r.Width, 4, TextAlignment.Center, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, r.Y));
    }

    private static void Spinner(DrawingContext ctx, Point c, double radius, Color color)
    {
        double a0 = (DateTime.UtcNow.TimeOfDay.TotalMilliseconds / 900.0 % 1) * Math.PI * 2;
        ctx.DrawEllipse(null, new Pen(B(UiTheme.WithAlpha(color, 40)), radius * 0.22), c, radius, radius);
        var g = new StreamGeometry();
        using (var gc = g.Open())
        {
            double a1 = a0 + Math.PI * 1.3;
            gc.BeginFigure(new Point(c.X + radius * Math.Cos(a0), c.Y + radius * Math.Sin(a0)), false);
            gc.ArcTo(new Point(c.X + radius * Math.Cos(a1), c.Y + radius * Math.Sin(a1)), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(B(color), radius * 0.22, lineCap: PenLineCap.Round), g);
    }

    private void LayoutRects() => LayoutRects(Bounds.Size);

    private void LayoutRects(Size page)
    {
        // Coordinate della scheda (la scheda intera si ingrandisce con la finestra tramite WizardSheet.Fit).
        // In ArrangeOverride Bounds e' ancora la misura vecchia: si usa quella nuova.
        double w = Math.Min(SheetW, page.Width - 24), h = Math.Min(SheetH, page.Height - 24);
        double sx = Math.Round((page.Width - w) / 2), sy = Math.Round((page.Height - h) / 2);
        double L = sx + 30, fw = w - 60, T = sy;
        double langW = 210;
        _searchRect = new Rect(L, T + 90, Math.Max(120, fw - langW - 12), 44);
        _langRect = new Rect(_searchRect.Right + 12, T + 90, langW, 44);
        _boxRect = new Rect(_searchRect.X + 44, _searchRect.Y + 6, Math.Max(20, _searchRect.Width - 44 - 14), _searchRect.Height - 12);
        _box.FontSize = P(10);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        LayoutRects(availableSize);
        _box.Measure(new Size(Math.Max(1, _boxRect.Width), Math.Max(1, _boxRect.Height)));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutRects(finalSize);
        _box.Arrange(_boxRect);
        return finalSize;
    }

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        _hits.Clear();
        LayoutRects();
        var page = new Rect(Bounds.Size);
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 170, 0, 1);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        using var fade = ctx.PushOpacity(t);
        if (_boxInk != Ink) ApplyBoxColors();
        DrawBackdrop(ctx, page);
        _hits.Add((page, "outside", -1));
        double w = Math.Min(SheetW, page.Width - 24), h = Math.Min(SheetH, page.Height - 24);
        var sheet = new Rect(Math.Round((page.Width - w) / 2), Math.Round((page.Height - h) / 2), w, h);
        // Stile delle schede nuove: niente riquadro, il contenuto sul velo, ingrandito con la finestra.
        WizardSheet.Fit(this, page.Size, new Size(SheetW, SheetH));
        _hits.Add((sheet, "sheet", -1));
        double L = sheet.X + 30, T = sheet.Y, fw = w - 60;
        Line(ctx, "Sottotitoli", 21, Semibold, Ink, new Rect(L, T + 8, fw - 110, 46));
        // Chiudi: la X delle Impostazioni, piu' luminosa sotto il puntatore.
        var close = new Rect(sheet.Right - 30 - 40 + 8, T + 11, 40, 40);
        {
            bool hot = Hot(close) && !_langOpen;
            double c = close.Center.X, m = close.Center.Y, d = Math.Max(5, Math.Floor(close.Width * 21 / 100));
            var pen = new Pen(B(hot ? Ink : UiTheme.WithAlpha(Ink, 170)), 2.2, lineCap: PenLineCap.Round);
            ctx.DrawLine(pen, new Point(c - d, m - d), new Point(c + d, m + d));
            ctx.DrawLine(pen, new Point(c + d, m - d), new Point(c - d, m + d));
        }
        _hits.Add((close, "close", -1));

        // Ricerca (modificabile) e lingua sulla stessa riga.
        Line(ctx, "Cerca", 9.5, Regular, MutedInk, new Rect(_searchRect.X, T + 64, 200, 22));
        Line(ctx, "Lingua", 9.5, Regular, MutedInk, new Rect(_langRect.X, T + 64, 200, 22));
        ctx.DrawRectangle(B(UiTheme.WithAlpha(Ink, _box.IsFocused ? 26 : 18)), _box.IsFocused ? new Pen(B(UiTheme.WithAlpha(Ink, 70)), 1) : null, _searchRect, 14, 14);
        {
            var c = new Point(_searchRect.X + 22, _searchRect.Center.Y - 1);
            var pen = new Pen(B(UiTheme.WithAlpha(Ink, 150)), 1.6, lineCap: PenLineCap.Round);
            ctx.DrawEllipse(null, pen, c, 6, 6);
            ctx.DrawLine(pen, new Point(c.X + 4.4, c.Y + 4.4), new Point(c.X + 8.5, c.Y + 8.5));
        }
        _hits.Add((_searchRect, "search", -1));
        bool langHot = Hot(_langRect) || _langOpen;
        ctx.DrawRectangle(B(UiTheme.WithAlpha(Ink, langHot ? 26 : 18)), null, _langRect, 14, 14);
        Line(ctx, SubtitleSources.LanguageLabel(_language, false), 10, Regular, Ink, new Rect(_langRect.X + 18, _langRect.Y, _langRect.Width - 52, _langRect.Height));
        {
            double cx = _langRect.Right - 22, cy = _langRect.Center.Y, z = 4.5;
            var pts = _langOpen ? new[] { new Point(cx - z, cy + z / 2), new Point(cx, cy - z / 2), new Point(cx + z, cy + z / 2) }
                                : new[] { new Point(cx - z, cy - z / 2), new Point(cx, cy + z / 2), new Point(cx + z, cy - z / 2) };
            ctx.DrawGeometry(null, new Pen(B(UiTheme.WithAlpha(Ink, 190)), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), new PolylineGeometry(pts, false));
        }
        _hits.Add((_langRect, "lang", -1));

        // Risultati; stati (ricerca in corso, nessun risultato, errori) al centro dell'elenco, non in fondo.
        var list = new Rect(L, T + 160, fw, h - 160 - 24);
        double rowH = 56;
        if (_results.Count == 0)
        {
            double cy = list.Y + list.Height * 0.38;
            if (_searching)
            {
                Spinner(ctx, new Point(list.Center.X, cy), 13, Ink);
                Paragraph(ctx, _status, 10, Regular, UiTheme.WithAlpha(Ink, 170), new Rect(list.X + 40, cy + 30, list.Width - 80, 60));
            }
            else
            {
                Paragraph(ctx, _status, 13, Semibold, Ink, new Rect(list.X + 40, cy - 18, list.Width - 80, 40));
                Paragraph(ctx, _statusHint, 9.4, Regular, UiTheme.WithAlpha(Ink, 150), new Rect(list.X + 60, cy + 20, list.Width - 120, 80));
            }
        }
        else
        {
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _results.Count * rowH - list.Height));
            using (ctx.PushClip(list))
                for (int i = 0; i < _results.Count; i++)
                {
                    var row = new Rect(list.X, list.Y + i * rowH - _scroll, list.Width, rowH - 6);
                    if (row.Bottom < list.Y || row.Y > list.Bottom) continue;
                    var c = _results[i];
                    bool mine = _downloading == i && _busy;
                    bool hover = Hot(row) && !_busy && !_langOpen;
                    using (ctx.PushOpacity(_busy && !mine ? 0.45 : 1))
                    {
                        ctx.DrawRectangle(B(UiTheme.WithAlpha(Ink, hover || mine ? 26 : 14)), null, row, 14, 14);
                        var badge = new Rect(row.X + 16, row.Y, 86, row.Height);
                        Line(ctx, c.Source, 8.4, Semibold, UiTheme.WithAlpha(Ink, 170), badge);
                        double textRight = mine ? 44 : 16;
                        Line(ctx, c.Name, 9.6, Semibold, Ink, new Rect(badge.Right + 14, row.Y + 6, row.Width - badge.Width - 30 - textRight, 22));
                        // Durante lo scaricamento lo stato sta nella riga scelta, al posto dei dettagli.
                        Line(ctx, mine ? _status : c.Detail, 8.4, Regular, mine ? UiTheme.WithAlpha(Ink, 200) : MutedInk,
                            new Rect(badge.Right + 14, row.Y + 27, row.Width - badge.Width - 30 - textRight, 18));
                        if (mine) Spinner(ctx, new Point(row.Right - 26, row.Center.Y), 8, Ink);
                    }
                    var hit = row.Intersect(list);
                    if (hit.Height > 0) _hits.Add((hit, "result", i));
                }
            // Errore di scaricamento: in alto sopra l'elenco, accanto alle etichette.
            if (!_busy && _status.Length > 0)
                Line(ctx, _status, 9, Regular, Color.FromRgb(255, 120, 110), new Rect(L, T + 138, fw, 22));
        }

        if (_langOpen) DrawLanguageMenu(ctx, sheet);
    }

    private void DrawLanguageMenu(DrawingContext ctx, Rect sheet)
    {
        var langs = MenuLanguages();
        const double itemH = 36;
        double maxH = Math.Max(itemH * 4, sheet.Bottom - 16 - (_langRect.Bottom + 6));
        double contentH = langs.Count * itemH + 12;
        double panelH = Math.Min(contentH, maxH);
        _langPanel = new Rect(_langRect.X, _langRect.Bottom + 6, _langRect.Width, panelH);
        _langScroll = Math.Clamp(_langScroll, 0, Math.Max(0, contentH - panelH));
        var back = OverVideo ? Color.FromArgb(245, 26, 28, 32) : UiTheme.IsLight ? Color.FromRgb(250, 251, 252) : UiTheme.Mix(UiTheme.Card, Colors.Black, 0.15);
        for (int i = 4; i >= 1; i--) ctx.DrawRectangle(B(Color.FromArgb(14, 0, 0, 0)), null, _langPanel.Inflate(i * 2.5), 16 + i * 2, 16 + i * 2);
        ctx.DrawRectangle(B(back), new Pen(B(UiTheme.WithAlpha(Ink, 30)), 1), _langPanel, 14, 14);
        _hits.Add((_langPanel, "lang-panel", -1));
        using (ctx.PushClip(new RoundedRect(_langPanel, 14)))
            for (int i = 0; i < langs.Count; i++)
            {
                var r = new Rect(_langPanel.X + 6, _langPanel.Y + 6 + i * itemH - _langScroll, _langPanel.Width - 12, itemH);
                if (r.Bottom < _langPanel.Y || r.Y > _langPanel.Bottom) continue;
                bool current = langs[i].Code == _language, hover = Hot(r);
                if (hover) ctx.DrawRectangle(B(UiTheme.WithAlpha(Ink, 22)), null, r, 10, 10);
                Line(ctx, langs[i].Label, 9.8, current ? Semibold : Regular, Ink, new Rect(r.X + 12, r.Y, r.Width - 44, r.Height));
                if (current)
                {
                    double cx = r.Right - 20, cy = r.Center.Y;
                    ctx.DrawGeometry(null, new Pen(B(Ink), 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                        new PolylineGeometry(new[] { new Point(cx - 5, cy), new Point(cx - 1.5, cy + 3.5), new Point(cx + 5, cy - 4) }, false));
                }
                // Linea sottile dopo le lingue piu' usate.
                if (i == Common.Length - 1 && i < langs.Count - 1)
                    ctx.DrawLine(new Pen(B(UiTheme.WithAlpha(Ink, 30)), 1), new Point(r.X + 10, r.Bottom), new Point(r.Right - 10, r.Bottom));
                var hit = r.Intersect(_langPanel);
                if (hit.Height > 0) _hits.Add((hit, "lang-item", i));
            }
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(now); _mouse = now; if (changed) InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        var hit = _hits.LastOrDefault(h => h.Rect.Contains(e.GetPosition(this)));
        if (_langOpen && hit.Kind is not ("lang-item" or "lang-panel" or "lang"))
        {
            // Clic fuori dal menu lingua: si chiude e basta.
            _langOpen = false; InvalidateVisual(); return;
        }
        switch (hit.Kind)
        {
            case "outside": case "close": Close(); break;
            case "search": _box.Focus(); break;
            case "lang": _langOpen = !_langOpen; _langScroll = Math.Max(0, MenuLanguages().FindIndex(l => l.Code == _language) * 36 - 72); Focus(); InvalidateVisual(); break;
            case "lang-item": { var langs = MenuLanguages(); if (hit.Index < langs.Count) SetLanguage(langs[hit.Index].Code); break; }
            case "result": _ = DownloadAsync(hit.Index); break;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_langOpen && _langPanel.Contains(e.GetPosition(this))) _langScroll -= e.Delta.Y * 40;
        else _scroll -= e.Delta.Y * 40;
        InvalidateVisual(); e.Handled = true;
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (_box.IsFocused) return; // i tasti vanno al campo di ricerca
        if (e.Key == Key.Escape) { if (_langOpen) { _langOpen = false; InvalidateVisual(); } else Close(); }
        else if (e.Key == Key.Enter) _ = SearchAsync();
        else return;
        e.Handled = true;
    }

    internal string QaSummary => $"busy={_busy} results={_results.Count} status={_status} query={_box.Text}";
    internal void TestLanguage(string code) { SetLanguage(code); }
    internal void TestQuery(string text) { _box.Text = text; _ = SearchAsync(); }
    internal void TestLanguageMenu(bool open) { _langOpen = open; InvalidateVisual(); }
    internal void TestDownloadFirst() { if (_results.Count > 0) _ = DownloadAsync(0); }
}
