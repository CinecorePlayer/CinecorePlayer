using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
/// Scheda "Rapporto della libreria" (LibraryReportForm di Windows): in alto i numeri, sotto l'elenco di cio' che merita
/// un'occhiata, a sezioni (doppioni, HDR da controllare, bitrate basso, risoluzione bassa, file che non si aprono).
/// Un clic su una riga mostra il file nel Finder. La lettura dei file (LibraryReport condiviso, ffprobe sul Mac) va in
/// sottofondo con la barra di avanzamento.
/// </summary>
internal sealed class LibraryReportView : Control
{
    private static readonly FontFamily Inter = new("avares://CinecoreMac/Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);
    private const double RowHeight = 34, HeaderHeight = 44, ListTop = 176, ListLeft = 30;

    public event Action? Closed;

    private IReadOnlyList<LibraryReport.Source> _sources = Array.Empty<LibraryReport.Source>();
    private LibraryReport.Result? _result;
    private double _progress, _scroll, _contentHeight;
    private CancellationTokenSource? _cts;
    private Point _mouse = new(-1, -1);
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private readonly List<(Rect Rect, string Kind, string Path)> _hits = new();
    private DateTime _openedAt;
    private Rect _list;

    public LibraryReportView() { IsVisible = false; Focusable = true; ClipToBounds = true; }

    public bool IsOpen => IsVisible;

    /// <summary>Film ed episodi della libreria sul Mac (i titoli dei server di rete non hanno un file da leggere).</summary>
    public static IReadOnlyList<LibraryReport.Source> CollectSources() =>
        LibraryStore.Items("Movies").Concat(LibraryStore.Items("TV Series"))
            .Where(i => !i.IsNetwork && !string.IsNullOrWhiteSpace(i.Path))
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(i => new LibraryReport.Source(i.Path, i.Title, i.Year)).ToList();

    public void Open(IReadOnlyList<LibraryReport.Source> sources)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _sources = sources; _result = null; _progress = 0; _scroll = 0; _openedAt = DateTime.UtcNow;
        IsVisible = true; Focus();
        InvalidateVisual();
        _ = Task.Run(() =>
        {
            LibraryReport.Result? result = null;
            try
            {
                result = LibraryReport.Build(sources, false, p => Dispatcher.UIThread.Post(() => { if (_cts == cts) { _progress = p; InvalidateVisual(); } }), cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Console.Error.WriteLine("[REPORT] " + ex.Message); }
            Dispatcher.UIThread.Post(() => { if (_cts != cts) return; _result = result ?? new LibraryReport.Result(); InvalidateVisual(); });
        });
    }

    public void Close()
    {
        if (!IsVisible) return;
        _cts?.Cancel();
        IsVisible = false;
        Closed?.Invoke();
    }

    private static string SectionTitle(string key) => key switch
    {
        "duplicates" => "Doppioni",
        "hdr" => "HDR con metadati di luminosità da controllare",
        "bitrate" => "Bitrate basso per la risoluzione",
        "resolution" => "Risoluzione sotto il 720p",
        "unreadable" => "File che non si aprono",
        _ => key,
    };

    // ------------------------------------------------------------------ disegno
    private static double P(double pt) => pt * 96 / 72 * 0.92;
    private static IBrush B(Color c) => new SolidColorBrush(c);

    private static void Line(DrawingContext ctx, string text, double pt, Typeface face, Color color, Rect r, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = UiText.Get(L10n.T(text), face, P(pt), color, r.Width, 1, align);
        ctx.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    private static double Measure(string text, double pt, Typeface face) =>
        UiText.Get(L10n.T(text), face, P(pt), Colors.White).Width;

    private double _f = 1;

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        _hits.Clear();
        var page = new Rect(Bounds.Size);
        if (page.Width < 200 || page.Height < 200) return;
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 380, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        byte alpha = (byte)Math.Round(255 * e);
        // Stile delle schede nuove (WizardSheet): niente riquadro, pagina velata, titolo grande, Chiudi in alto a destra.
        double u = WizardSheet.Scale(page.Size), f = _f = 1.5 * u;
        double F(double v) => Math.Round(v * f);
        WizardSheet.DrawVeil(ctx, page, e);
        var close = WizardSheet.Cancel(page.Size);
        WizardSheet.DrawCancel(ctx, close, Hot(close), u, "Chiudi");
        _hits.Add((close, "close", ""));
        double w = Math.Min(page.Width - 120 * u, 1240 * u);
        var area = new Rect(Math.Round((page.Width - w) / 2), Math.Round(page.Height * 0.12), w, Math.Round(page.Height * 0.82));
        double slide = Math.Round(26 * u * (1 - e));
        using var opacity = ctx.PushOpacity(e);
        ctx.DrawText(WizardSheet.Text("Rapporto della libreria", 44 * u, WizardSheet.Semibold, WizardSheet.Ink(255), w), new Point(area.X + slide, area.Y));
        double top = area.Y + 72 * u;

        var result = _result;
        if (result == null)
        {
            ctx.DrawText(WizardSheet.Text(string.Format(CultureInfo.CurrentCulture, "Leggo {0} file…", _sources.Count), 19 * u, WizardSheet.Regular, WizardSheet.Soft(255), w), new Point(area.X, top));
            var track = new Rect(area.X, top + 46 * u, w, Math.Max(2, 3 * u));
            ctx.DrawRectangle(B(UiTheme.WithAlpha(UiTheme.Text, UiTheme.IsLight ? 40 : 34)), null, track, track.Height / 2, track.Height / 2);
            ctx.DrawRectangle(B(UiTheme.Text), null, new Rect(track.X, track.Y, track.Width * Math.Clamp(_progress, 0, 1), track.Height), track.Height / 2, track.Height / 2);
            return;
        }

        int findings = result.Sections.Sum(s => s.Findings.Count);
        ctx.DrawText(WizardSheet.Text(findings == 0 ? "Film e serie sul Mac: niente da segnalare."
                : string.Format(CultureInfo.CurrentCulture, "Film e serie sul Mac, {0} cose da guardare. Un clic su una riga mostra il file nel Finder.", findings),
            19 * u, WizardSheet.Regular, WizardSheet.Soft(255), w, 2), new Point(area.X, top));
        top += 56 * u;

        string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');
        var stats = new (string Label, string Value)[]
        {
            ("Titoli", N(result.Titles)), ("Spazio", LibraryReport.Size(result.Bytes).Replace('.', ',')), ("4K", N(result.Uhd)), ("1080p", N(result.FullHd)),
            ("720p e meno", N(result.Hd + result.Sd)), ("HDR", N(result.Hdr)), ("Dolby Vision", N(result.DolbyVision)),
        };
        double column = w / stats.Length;
        var bar = new Pen(B(UiTheme.WithAlpha(UiTheme.Text, UiTheme.IsLight ? 34 : 24)), 1);
        for (int i = 0; i < stats.Length; i++)
        {
            double left = area.X + i * column + (i > 0 ? F(18) : 0), room = column - F(22) - (i > 0 ? F(18) : 0);
            if (i > 0) ctx.DrawLine(bar, new Point(area.X + i * column, top + F(4)), new Point(area.X + i * column, top + F(48)));
            Line(ctx, stats[i].Label, 9 * f, Regular, UiTheme.Muted, new Rect(left, top, room, F(18)));
            Line(ctx, stats[i].Value, 16 * f, Semibold, UiTheme.Text, new Rect(left, top + F(18), room, F(32)));
        }
        top += F(76);

        double rowH = F(RowHeight), headH = F(HeaderHeight);
        _list = new Rect(area.X, top, w, Math.Max(1, area.Bottom - top));
        var list = _list;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentHeight - list.Height));
        double y = list.Y - _scroll;
        using (ctx.PushClip(list))
            foreach (var section in result.Sections)
            {
                if (y + headH > list.Y && y < list.Bottom)
                {
                    string heading = SectionTitle(section.Key);
                    double hw = Measure(heading, 10.5 * f, Semibold);
                    Line(ctx, heading, 10.5 * f, Semibold, UiTheme.Text, new Rect(list.X, y + F(14), hw + 4, F(22)));
                    Line(ctx, section.Findings.Count.ToString(CultureInfo.InvariantCulture), 10.5 * f, Semibold, UiTheme.Muted, new Rect(list.X + hw + F(10), y + F(14), F(60), F(22)));
                }
                y += headH;
                foreach (var finding in section.Findings)
                {
                    if (y + rowH > list.Y && y < list.Bottom)
                    {
                        var row = new Rect(list.X, y, list.Width - F(14), rowH);
                        bool hot = Hot(row) && Hot(list);
                        if (hot) ctx.DrawRectangle(B(UiTheme.WithAlpha(UiTheme.Text, 12)), null, row.Inflate(new Thickness(F(8), 0)), F(8), F(8));
                        double nameWidth = Math.Min(F(300), row.Width * 2 / 5);
                        Line(ctx, finding.Title, 10 * f, Regular, UiTheme.WithAlpha(UiTheme.Text, hot ? 255 : 225), new Rect(row.X, row.Y, nameWidth - F(16), row.Height));
                        Line(ctx, finding.Detail, 9 * f, Regular, UiTheme.SubtleText, new Rect(row.X + nameWidth + F(16), row.Y, row.Width - nameWidth - F(16), row.Height));
                        _hits.Add((row.Intersect(list), "row", finding.Path));
                    }
                    y += rowH;
                }
                y += F(10);
            }
        _contentHeight = y + _scroll - list.Y;
        if (_contentHeight > list.Height)
        {
            double thumb = Math.Max(28, list.Height * (list.Height / _contentHeight));
            double thumbTop = list.Y + (list.Height - thumb) * (_scroll / Math.Max(1, _contentHeight - list.Height));
            ctx.FillRectangle(B(UiTheme.WithAlpha(UiTheme.Text, 70)), new Rect(list.Right - 3, thumbTop, 3, thumb), 1.5f);
        }
    }

    // ------------------------------------------------------------------ input
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var now = e.GetPosition(this);
        bool changed = _zones.Changed(now);
        _mouse = now;
        var hit = _hits.LastOrDefault(h => h.Rect.Contains(_mouse));
        Cursor = hit.Kind is "row" or "close" ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        if (changed) InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        var hit = _hits.LastOrDefault(h => h.Rect.Contains(e.GetPosition(this)));
        switch (hit.Kind)
        {
            case "close": Close(); break;
            case "row": RevealInFinder(hit.Path); break;
        }
    }

    /// <summary>"Mostra nel Finder" (explorer /select su Windows). Percorso assoluto in ArgumentList: non puo' diventare un'opzione.</summary>
    private static void RevealInFinder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            open.ArgumentList.Add("-R"); open.ArgumentList.Add(Path.GetFullPath(path));
            Process.Start(open)?.Dispose();
        }
        catch { }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _scroll -= e.Delta.Y * RowHeight * _f * 2;
        InvalidateVisual();
        e.Handled = true;
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    internal string QaSummary => _result == null ? $"leggo {_sources.Count} file, {_progress:P0}" : $"titoli={_result.Titles} sezioni={string.Join(",", _result.Sections.Select(s => s.Key + ":" + s.Findings.Count))}";
}
