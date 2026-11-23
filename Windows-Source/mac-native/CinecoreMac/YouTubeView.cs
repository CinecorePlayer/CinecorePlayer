using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Pagina YouTube della libreria (CinematicMediaLibraryPage.WebInput.cs di Windows): ricerca con yt-dlp (24 risultati,
/// senza scaricare nulla), griglia di miniature con titolo, canale e durata; un clic apre il video nel player. Senza
/// ricerca propone i trailer, come su Windows. Un link di YouTube scritto nella casella si apre subito.
/// </summary>
internal sealed class YouTubeView : Control
{
    private static readonly FontFamily Inter = new(AppAssets.Root + "Assets/Fonts#Inter");
    private static readonly Typeface Regular = new(Inter), Semibold = new(Inter, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private sealed class Item
    {
        public string Id = "", Url = "", Title = "", Channel = "", Duration = "", ThumbnailUrl = "";
        public bool IsLive;
        public Bitmap? Thumbnail;
    }

    public event Action? Closed;
    public event Action<string, string>? VideoChosen;

    private readonly TextBox _search;
    private readonly List<Item> _items = new();
    private readonly List<(Rect Rect, string Kind, int Index)> _hits = new();
    private CancellationTokenSource? _cts;
    private bool _loading;
    private string _status = "";
    private double _scroll, _maxScroll;
    private Point _mouse = new(-1, -1);
    // [2026-11-22] Ridisegno solo quando il mouse entra o esce da un elemento che reagisce (prima a ogni movimento).
    private readonly HoverZones _zones = new();
    private bool Hot(Rect r) { _zones.Track(r); return r.Contains(_mouse); }
    private Rect _searchRect;
    private DateTime _openedAt;

    public YouTubeView()
    {
        IsVisible = false; Focusable = true; ClipToBounds = true;
        _search = new TextBox
        {
            Watermark = "Cerca su YouTube", FontFamily = Inter, FontSize = 11 * 96 / 72 * 0.92,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6), MinHeight = 0,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Search(_search.Text); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        VisualChildren.Add(_search);
        LogicalChildren.Add(_search);
    }

    public bool IsOpen => IsVisible;
    internal string QaSummary => $"risultati={_items.Count} miniature={_items.Count(i => i.Thumbnail != null)} carico={_loading} stato={_status}";

    public void Open()
    {
        _search.Foreground = B(UiTheme.Text); _search.BorderBrush = B(UiTheme.WithAlpha(UiTheme.Text, 90)); _search.CaretBrush = B(UiTheme.Text);
        _openedAt = DateTime.UtcNow;
        IsVisible = true;
        InvalidateMeasure(); InvalidateVisual();
        Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Input);
        if (_items.Count == 0 && !_loading) Search("");
    }

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        Closed?.Invoke();
    }

    internal void Search(string? query)
    {
        string value = (query ?? "").Trim();
        if (MacWebResolver.IsWebAddress(value) && MacWebResolver.IsYouTube(value)) { VideoChosen?.Invoke(value, ""); return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _loading = true; _status = ""; _scroll = 0;
        InvalidateVisual();
        string effective = value.Length == 0 ? "trailer film italiani 4K" : value;
        _ = Task.Run(() =>
        {
            List<Item>? items = null;
            string? error = null;
            try
            {
                if (MacYtDlp.Ensure(CancellationToken.None) is not { } ytdlp) error = MacYtDlp.MissingMessage;
                else
                {
                    string search = "ytsearch24:" + new string(effective.Where(c => c >= 0x20).ToArray());
                    var (exit, stdout, stderr) = ProcessLauncher.Run(ytdlp, new[] { "--no-warnings", "--skip-download", "--flat-playlist", "--playlist-end", "24", "--dump-json", "--", search }, false, 30000, cts.Token);
                    items = Parse(Encoding.UTF8.GetString(stdout));
                    if (items.Count == 0) error = exit != 0 ? (stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Replace("ERROR: ", "") ?? "Ricerca non riuscita") : "Nessun risultato";
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { error = ex.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                if (_cts != cts) return;
                _loading = false;
                _items.Clear();
                if (items != null) _items.AddRange(items);
                _status = error ?? "";
                InvalidateVisual();
                LoadThumbnails(items ?? new(), cts.Token);
            });
        });
    }

    private static List<Item> Parse(string output)
    {
        var result = new List<Item>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string id = Str(root, "id"), title = Str(root, "title");
                if (id.Length == 0 || title.Length == 0 || !seen.Add(id)) continue;
                string url = Str(root, "webpage_url");
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://www.youtube.com/watch?v=" + id;
                double duration = root.TryGetProperty("duration", out var d) && d.TryGetDouble(out double dv) ? dv : 0;
                string thumb = Str(root, "thumbnail");
                long best = -1;
                if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
                    foreach (var t in thumbs.EnumerateArray())
                    {
                        long area = (t.TryGetProperty("width", out var w) && w.TryGetInt64(out long wv) ? wv : 0) * Math.Max(1, t.TryGetProperty("height", out var h) && h.TryGetInt64(out long hv) ? hv : 0);
                        // La miniatura piu' grande fino a ~720p: piu' grandi sono solo peso in piu'.
                        if (area >= best && area <= 1280L * 720 && Str(t, "url") is { Length: > 0 } u) { best = area; thumb = u; }
                    }
                var item = new Item
                {
                    Id = id, Url = url, Title = title.Trim(), Channel = Str(root, "channel") is { Length: > 0 } c ? c : Str(root, "uploader"),
                    Duration = duration > 0 ? (duration >= 3600 ? TimeSpan.FromSeconds(duration).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(duration).ToString(@"m\:ss")) : "",
                    IsLive = Str(root, "live_status") == "is_live", ThumbnailUrl = thumb,
                };
                result.Add(item);
            }
            catch { }
        }
        return result;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private static string ThumbFolder => Path.Combine(LibraryStore.AppDataDir, "youtube-thumbs");

    private void LoadThumbnails(List<Item> items, CancellationToken ct)
    {
        foreach (var item in items.Where(i => i.ThumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            _ = Task.Run(async () =>
            {
                try
                {
                    Directory.CreateDirectory(ThumbFolder);
                    string file = Path.Combine(ThumbFolder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(item.ThumbnailUrl)))[..20] + ".jpg");
                    if (!File.Exists(file)) await File.WriteAllBytesAsync(file, await Http.GetByteArrayAsync(item.ThumbnailUrl, ct), ct);
                    await using var stream = File.OpenRead(file);
                    var bitmap = Bitmap.DecodeToWidth(stream, 480, BitmapInterpolationMode.HighQuality);
                    Dispatcher.UIThread.Post(() => { item.Thumbnail = bitmap; InvalidateVisual(); });
                }
                catch { }
            }, ct);
    }

    // ------------------------------------------------------------------ disegno
    private static IBrush B(Color c) => new SolidColorBrush(c);
    private static double P(double pt) => pt * 96 / 72 * 0.92;

    private static void Line(DrawingContext ctx, string text, double pt, Typeface face, Color color, Rect r, int lines = 1, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width < 2) return;
        var ft = UiText.Get(L10n.T(text), face, P(pt), color, r.Width, lines, align, TextTrimming.CharacterEllipsis);
        ctx.DrawText(ft, new Point(r.X, lines == 1 ? r.Y + (r.Height - ft.Height) / 2 : r.Y));
    }

    private const double Margin = 48, Top = 76;

    protected override Size MeasureOverride(Size availableSize)
    {
        _search.Measure(new Size(Math.Max(1, _searchRect.Width), Math.Max(1, _searchRect.Height)));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _searchRect = new Rect(Margin + 150, 22, Math.Max(100, Math.Min(520, finalSize.Width - Margin * 2 - 210)), 34);
        _search.Arrange(_searchRect);
        return finalSize;
    }

    public override void Render(DrawingContext ctx)
    {
        _zones.Begin(_mouse);
        _hits.Clear();
        var page = new Rect(Bounds.Size);
        double t = Math.Clamp((DateTime.UtcNow - _openedAt).TotalMilliseconds / 170, 0, 1);
        if (t < 1) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        using var fade = ctx.PushOpacity(t);
        ctx.FillRectangle(B(UiTheme.Panel), page);
        Line(ctx, "YouTube", 17, Semibold, UiTheme.Text, new Rect(Margin, 20, 140, 38));
        var close = new Rect(page.Width - Margin - 32, 22, 32, 32);
        bool closeHot = Hot(close);
        var pen = new Pen(B(UiTheme.WithAlpha(UiTheme.Text, closeHot ? 255 : 170)), 1.6, lineCap: PenLineCap.Round);
        ctx.DrawLine(pen, new Point(close.Center.X - 6, close.Center.Y - 6), new Point(close.Center.X + 6, close.Center.Y + 6));
        ctx.DrawLine(pen, new Point(close.Center.X + 6, close.Center.Y - 6), new Point(close.Center.X - 6, close.Center.Y + 6));
        _hits.Add((close, "close", -1));

        var area = new Rect(Margin, Top, page.Width - Margin * 2, page.Height - Top);
        if (_loading || _items.Count == 0)
        {
            Line(ctx, _loading ? "Cerco su YouTube…" : _status, 11, Regular, UiTheme.Muted, new Rect(area.X, area.Y + 40, area.Width, 30), 1, TextAlignment.Center);
            return;
        }
        int columns = Math.Max(1, (int)((area.Width + 20) / 300));
        double gap = 20, cardW = (area.Width - gap * (columns - 1)) / columns, imageH = cardW * 9 / 16, cardH = imageH + 70;
        int rows = (_items.Count + columns - 1) / columns;
        _maxScroll = Math.Max(0, rows * (cardH + gap) - area.Height + 20);
        _scroll = Math.Clamp(_scroll, 0, _maxScroll);
        using (ctx.PushClip(area))
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                double x = area.X + (i % columns) * (cardW + gap), y = area.Y + (i / columns) * (cardH + gap) - _scroll;
                if (y + cardH < area.Y || y > area.Bottom) continue;
                var image = new Rect(x, y, cardW, imageH);
                bool hot = Hot(new Rect(x, y, cardW, cardH));
                using (ctx.PushClip(new RoundedRect(image, 10)))
                {
                    ctx.FillRectangle(B(UiTheme.WithAlpha(UiTheme.Text, 20)), image);
                    if (item.Thumbnail is { } bmp)
                    {
                        double cover = Math.Max(image.Width / bmp.Size.Width, image.Height / bmp.Size.Height);
                        double w = bmp.Size.Width * cover, h = bmp.Size.Height * cover;
                        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                            ctx.DrawImage(bmp, new Rect(bmp.Size), new Rect(image.Center.X - w / 2, image.Center.Y - h / 2, w, h));
                    }
                    if (hot) ctx.FillRectangle(B(Color.FromArgb(40, 255, 255, 255)), image);
                }
                string badge = item.IsLive ? "LIVE" : item.Duration;
                if (badge.Length > 0)
                {
                    var ft = UiText.Get(L10n.T(badge), Semibold, P(8.5), Colors.White);
                    var chip = new Rect(image.Right - ft.Width - 18, image.Bottom - ft.Height - 14, ft.Width + 10, ft.Height + 4);
                    ctx.DrawRectangle(B(item.IsLive ? Color.FromRgb(220, 38, 38) : Color.FromArgb(200, 0, 0, 0)), null, chip, 5, 5);
                    ctx.DrawText(ft, new Point(chip.X + 5, chip.Y + 2));
                }
                Line(ctx, item.Title, 10, Semibold, UiTheme.Text, new Rect(x, image.Bottom + 8, cardW, 40), 2);
                Line(ctx, item.Channel, 9, Regular, UiTheme.Muted, new Rect(x, image.Bottom + 48, cardW, 18));
                _hits.Add((new Rect(x, y, cardW, cardH), "video", i));
            }
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); var now = e.GetPosition(this); bool changed = _zones.Changed(now); _mouse = now; if (changed) InvalidateVisual(); }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _scroll = Math.Clamp(_scroll - e.Delta.Y * 60, 0, _maxScroll);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        foreach (var (rect, kind, index) in _hits)
        {
            if (!rect.Contains(p)) continue;
            e.Handled = true;
            if (kind == "close") Close();
            else if (kind == "video" && index < _items.Count) VideoChosen?.Invoke(_items[index].Url, _items[index].Title);
            return;
        }
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    internal void TestChoose(int index) { if (index < _items.Count) VideoChosen?.Invoke(_items[index].Url, _items[index].Title); }
}
