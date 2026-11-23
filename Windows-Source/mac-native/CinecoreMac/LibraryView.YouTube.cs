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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CinecoreMac;

// Pagina YouTube della libreria come su Windows (CinematicMediaLibraryPage.WebInput.cs, DrawYouTubePage): una pagina
// sotto la barra in alto (non una finestra a parte), la ricerca e' quella della barra ("Cerca su YouTube", gia' aperta),
// "In evidenza" o "Risultati per …" con "Aggiorna" a destra, griglia di schede 16:9 (2-5 colonne, almeno 250 unita'),
// durata in basso a destra, al passaggio velo e disco con il triangolo, titolo su due righe, canale · visualizzazioni.
// Durante la ricerca le sagome pulsanti; senza risultati il motivo. Un link di YouTube scritto nella casella parte subito.
internal sealed partial class LibraryView
{
    private sealed class YouTubeItem
    {
        public string Id = "", Url = "", Title = "", Channel = "", Duration = "", ThumbnailUrl = "";
        public long Views;
        public bool IsLive;
        public Bitmap? Thumbnail;
    }

    private static readonly HttpClient YouTubeHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly List<YouTubeItem> _ytItems = new();
    private CancellationTokenSource? _ytCts;
    private bool _ytLoading;
    private string _ytStatus = "", _ytQuery = "";
    private double _ytScroll, _ytScrollMax;
    private DispatcherTimer? _ytTyping;

    /// <summary>Un video scelto dalla pagina YouTube: lo apre la finestra (indirizzo, titolo).</summary>
    public event Action<string, string>? YouTubeVideoChosen;

    public bool YouTubeOpen => _view == "YouTube";

    public void OpenYouTube()
    {
        if (_view != "YouTube") BeginPageTransition();
        _view = "YouTube";
        CloseDetail(); CloseSeries();
        _ytScroll = 0;
        // La ricerca della barra, gia' aperta (YouTube vive di ricerca).
        var box = EnsureSearchBox();
        box.Text = _ytQuery;
        SetSearchOpen(true);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || !YouTubeOpen) return;
            box.Focus(); box.CaretIndex = (box.Text ?? "").Length;
        }, DispatcherPriority.Input);
        if (_ytItems.Count == 0 && !_ytLoading) YouTubeSearch(_ytQuery);
        InvalidateVisual();
    }

    /// <summary>Testo della barra cambiato sulla pagina YouTube: si cerca poco dopo l'ultima lettera.</summary>
    private void YouTubeQueryChanged(string text)
    {
        _ytTyping ??= new DispatcherTimer(TimeSpan.FromMilliseconds(650), DispatcherPriority.Background, (_, _) =>
        {
            _ytTyping!.Stop();
            if (IsVisible && YouTubeOpen && (_searchBox?.Text ?? "").Trim() != _ytQuery) YouTubeSearch(_searchBox?.Text);
        });
        _ytTyping.Stop();
        _ytTyping.Start();
    }

    internal void YouTubeSearch(string? query)
    {
        _ytTyping?.Stop();
        string value = (query ?? "").Trim();
        if (MacWebResolver.IsWebAddress(value) && MacWebResolver.IsYouTube(value)) { YouTubeVideoChosen?.Invoke(value, ""); return; }
        _ytQuery = value;
        _ytCts?.Cancel();
        var cts = _ytCts = new CancellationTokenSource();
        _ytLoading = true; _ytStatus = ""; _ytScroll = 0;
        _ytItems.Clear();
        _frames.Start();
        InvalidateVisual();
        string effective = value.Length == 0 ? "trailer film italiani 4K" : value;
        _ = Task.Run(() =>
        {
            List<YouTubeItem>? items = null;
            string? error = null;
            try
            {
                if (MacYtDlp.Ensure(CancellationToken.None) is not { } ytdlp) error = MacYtDlp.MissingMessage;
                else
                {
                    string search = "ytsearch24:" + new string(effective.Where(c => c >= 0x20 && c != '"').ToArray());
                    // Il primo giro dopo l'avvio (o dopo il risveglio del Mac) a volte fallisce: rete non ancora pronta,
                    // yt-dlp che si prepara. Un secondo tentativo prima di dare l'errore.
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        var started = DateTime.UtcNow;
                        var (exit, stdout, stderr) = ProcessLauncher.Run(ytdlp, new[] { "--no-warnings", "--skip-download", "--flat-playlist", "--playlist-end", "24", "--socket-timeout", "15", "--dump-json", "--", search }, false, 45000, cts.Token);
                        items = ParseYouTube(Encoding.UTF8.GetString(stdout));
                        if (items.Count > 0) { error = null; break; }
                        error = exit != 0 ? (stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Replace("ERROR: ", "") ?? "Ricerca non riuscita") : "Nessun video trovato";
                        Console.Error.WriteLine($"[youtube] ricerca fallita (tentativo {attempt}, uscita {exit}, {(DateTime.UtcNow - started).TotalSeconds:0.0} s): {stderr.Trim().Replace('\n', ' ')}");
                        // Uscita 0 senza risultati (successo nel log dell'utente dopo 3 s, con un menu aperto sopra la pagina):
                        // non e' una risposta vera, si riprova come per gli errori. Solo la home ("trailer…") puo' davvero
                        // essere vuota per una ricerca dell'utente, e allora dopo tre giri resta "Nessun video trovato".
                        cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(attempt * 1.5));
                        cts.Token.ThrowIfCancellationRequested();
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { error = ex.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                if (_ytCts != cts) return;
                _ytLoading = false;
                _ytItems.Clear();
                if (items != null) _ytItems.AddRange(items);
                _ytStatus = error ?? "";
                InvalidateVisual();
                LoadYouTubeThumbnails(items ?? new(), cts.Token);
            });
        });
    }

    private static List<YouTubeItem> ParseYouTube(string output)
    {
        var result = new List<YouTubeItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
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
                        if (area >= best && area <= 1280L * 720 && Str(t, "url") is { Length: > 0 } u) { best = area; thumb = u; }
                    }
                result.Add(new YouTubeItem
                {
                    Id = id, Url = url, Title = title.Trim(), Channel = Str(root, "channel") is { Length: > 0 } c ? c : Str(root, "uploader"),
                    Duration = duration > 0 ? (duration >= 3600 ? TimeSpan.FromSeconds(duration).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(duration).ToString(@"m\:ss")) : "",
                    IsLive = Str(root, "live_status") == "is_live", ThumbnailUrl = thumb,
                    Views = root.TryGetProperty("view_count", out var vc) && vc.TryGetInt64(out long views) ? views : 0,
                });
            }
            catch { }
        }
        return result;
    }

    private static string ThumbFolder => Path.Combine(LibraryStore.AppDataDir, "youtube-thumbs");
    private static string ThumbFile(string thumbnailUrl) =>
        Path.Combine(ThumbFolder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(thumbnailUrl)))[..20] + ".jpg");

    /// <summary>ID del video da un indirizzo di YouTube (watch?v=, youtu.be/, shorts/, live/, embed/).</summary>
    private static string? YouTubeVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        string? id = null;
        if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase)) id = uri.AbsolutePath.Trim('/').Split('/')[0];
        else
        {
            foreach (string part in uri.Query.TrimStart('?').Split('&'))
                if (part.StartsWith("v=", StringComparison.Ordinal)) { id = Uri.UnescapeDataString(part[2..]); break; }
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (id == null && segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed") id = segments[1];
        }
        return id != null && System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$") ? id : null;
    }

    /// <summary>
    /// Copertina del video di YouTube per la schermata di caricamento: la miniatura gia' scaricata per la lista o quella
    /// ricavata dall'indirizzo. Se il file c'e' gia' lo restituisce subito; altrimenti lo scarica e chiama
    /// <paramref name="downloaded"/> sul thread dell'interfaccia.
    /// </summary>
    public string? YouTubeCover(string url, Action<string> downloaded)
    {
        string? listed = _ytItems.FirstOrDefault(i => i.Url == url && i.ThumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))?.ThumbnailUrl;
        string? id = YouTubeVideoId(url);
        // A tutto schermo serve la versione grande (1280×720): maxresdefault, poi sddefault (640×480). La miniatura
        // della lista (piccola) e hqdefault solo se le grandi non esistono.
        string[] candidates = (id == null ? new[] { listed ?? "" } : new[]
        {
            "https://i.ytimg.com/vi/" + id + "/maxresdefault.jpg", "https://i.ytimg.com/vi/" + id + "/sddefault.jpg",
            listed ?? "", "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg",
        }).Where(c => c.Length > 0).Distinct().ToArray();
        if (candidates.Length == 0) return null;
        if (File.Exists(ThumbFile(candidates[0]))) return ThumbFile(candidates[0]);
        // Intanto quella gia' in cache (anche piccola), poi la grande appena arriva.
        string? interim = candidates.Skip(1).Where(c => File.Exists(ThumbFile(c))).Select(ThumbFile).FirstOrDefault();
        _ = Task.Run(async () =>
        {
            foreach (string thumb in candidates)
            {
                try
                {
                    Directory.CreateDirectory(ThumbFolder);
                    string file = ThumbFile(thumb), temp = file + ".part";
                    if (file == interim) return; // le piu' grandi non ci sono: resta quella gia' a schermo
                    await File.WriteAllBytesAsync(temp, await YouTubeHttp.GetByteArrayAsync(thumb));
                    File.Move(temp, file, true);
                    Dispatcher.UIThread.Post(() => downloaded(file));
                    return;
                }
                catch { } // maxresdefault non c'e' per tutti i video: si prova la successiva
            }
        });
        return interim;
    }

    private void LoadYouTubeThumbnails(List<YouTubeItem> items, CancellationToken ct)
    {
        string folder = ThumbFolder;
        foreach (var item in items.Where(i => i.ThumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            _ = Task.Run(async () =>
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    string file = ThumbFile(item.ThumbnailUrl);
                    if (!File.Exists(file)) await File.WriteAllBytesAsync(file, await YouTubeHttp.GetByteArrayAsync(item.ThumbnailUrl, ct), ct);
                    await using var stream = File.OpenRead(file);
                    var bitmap = Bitmap.DecodeToWidth(stream, 640, BitmapInterpolationMode.HighQuality);
                    Dispatcher.UIThread.Post(() => { item.Thumbnail = bitmap; InvalidateVisual(); });
                }
                catch { }
            }, ct);
    }

    /// <summary>FormatYouTubeViews di Windows: 1,2 Mln di visualizzazioni, 35.000 visualizzazioni.</summary>
    private static string YouTubeViews(long views)
    {
        if (views <= 0) return "";
        if (views >= 1_000_000_000) return (views / 1e9).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + " Mrd di visualizzazioni";
        if (views >= 1_000_000) return (views / 1e6).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + " Mln di visualizzazioni";
        if (views >= 1000) return (views / 1000).ToString(CultureInfo.InvariantCulture) + "K visualizzazioni";
        return views.ToString(CultureInfo.InvariantCulture) + " visualizzazioni";
    }

    // ----- disegno -----
    private void DrawYouTubePage(DrawingContext ctx, Rect page)
    {
        var content = new Rect(PadX, BarH, Math.Max(1, page.Width - PadX * 2), Math.Max(1, page.Height - BarH - MusicBarReserve - S(20)));
        double sectionTop = content.Y + S(40);
        string sectionTitle = _ytQuery.Length == 0 ? "In evidenza" : $"Risultati per “{_ytQuery}”";
        TextAt(ctx, sectionTitle, Math.Max(12, 16 * _s), DisplaySemibold, Text, new Rect(content.X, sectionTop, Math.Max(100, content.Width - S(180)), S(34)));
        // "Aggiorna" in solo testo, a destra sulla stessa riga.
        string refreshText = _ytLoading ? "Aggiorno…" : "Aggiorna";
        double bodyPt = Math.Max(9, 11 * _s), refreshW = Measure(refreshText, bodyPt, DisplayRegular);
        var refresh = new Rect(content.Right - refreshW, sectionTop, refreshW + 2, S(34));
        var refreshHit = refresh.Inflate(new Thickness(S(10), 0));
        TextAt(ctx, refreshText, bodyPt, DisplayRegular, A(Text, Hover(refreshHit) ? 255 : 175), refresh);
        if (!_ytLoading) _hits.Add(new Hit(refreshHit, "yt-refresh"));

        var viewport = new Rect(content.X, sectionTop + S(40), content.Width, Math.Max(1, content.Bottom - sectionTop - S(44)));
        if (_ytLoading && _ytItems.Count == 0) { DrawYouTubeSkeletons(ctx, viewport); _ytScrollMax = 0; return; }
        if (_ytItems.Count == 0) { DrawYouTubeEmpty(ctx, viewport); _ytScrollMax = 0; return; }

        double minCardW = S(250), gapX = S(20);
        int columns = Math.Clamp((int)((viewport.Width + gapX) / Math.Max(1, minCardW + gapX)), 2, 5);
        double cardW = (viewport.Width - gapX * (columns - 1)) / columns;
        double imageH = Math.Max(S(126), Math.Round(cardW * 9 / 16));
        double cardH = imageH + S(78), gapY = S(28);
        int rows = (int)Math.Ceiling(_ytItems.Count / (double)columns);
        double total = Math.Max(0, rows * cardH + Math.Max(0, rows - 1) * gapY);
        _ytScrollMax = Math.Max(0, total - viewport.Height);
        _ytScroll = Math.Clamp(_ytScroll, 0, _ytScrollMax);
        using (ctx.PushClip(viewport))
            for (int i = 0; i < _ytItems.Count; i++)
            {
                var card = new Rect(viewport.X + (i % columns) * (cardW + gapX), viewport.Y + (i / columns) * (cardH + gapY) - _ytScroll, cardW, cardH);
                if (card.Bottom < viewport.Y - gapY || card.Y > viewport.Bottom + gapY) continue;
                DrawYouTubeCard(ctx, card, imageH, _ytItems[i], viewport, i);
            }
    }

    private void DrawYouTubeCard(DrawingContext ctx, Rect card, double imageH, YouTubeItem item, Rect viewport, int index)
    {
        var image = new Rect(card.X, card.Y, card.Width, imageH);
        bool hover = Hover(card) && Hover(viewport);
        using (ctx.PushClip(new RoundedRect(image, S(6))))
        {
            if (item.Thumbnail is { } bmp) DrawCover(ctx, bmp, image);
            else
            {
                ctx.FillRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromRgb(30, 38, 47), 0), new GradientStop(Color.FromRgb(10, 14, 20), 1) },
                }, image);
            }
            if (hover)
            {
                ctx.FillRectangle(B(Color.FromArgb(20, 255, 255, 255)), image);
                double play = S(42);
                var disc = new Rect(image.Center.X - play / 2, image.Center.Y - play / 2, play, play);
                ctx.DrawEllipse(B(Color.FromArgb(220, 7, 12, 18)), null, disc);
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(new Point(disc.X + play * .42, disc.Y + play * .30), true);
                    g.LineTo(new Point(disc.X + play * .42, disc.Bottom - play * .30));
                    g.LineTo(new Point(disc.Right - play * .27, disc.Y + play * .50));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(Brushes.White, null, tri);
            }
        }
        if (hover) ctx.DrawRectangle(null, new Pen(B(A(Text, 70)), 1), image, S(6), S(6));
        string duration = item.IsLive ? "LIVE" : item.Duration;
        if (duration.Length > 0)
        {
            double dpt = Math.Max(7.2, 7.8 * _s), width = Measure(duration, dpt, DisplaySemibold) + S(12);
            var chip = new Rect(image.Right - width - S(7), image.Bottom - S(27), width, S(21));
            ctx.DrawRectangle(B(item.IsLive ? Color.FromArgb(224, 210, 36, 48) : Color.FromArgb(215, 3, 8, 12)), null, chip, S(4), S(4));
            TextAt(ctx, duration, dpt, DisplaySemibold, Colors.White, chip, TextAlignment.Center);
        }
        double textTop = image.Bottom + S(10);
        ctx.DrawText(Format(item.Title, Math.Max(9.4, 11 * _s), DisplayRegular, Text, card.Width, 2), new Point(card.X, textTop));
        string meta = item.Channel;
        string views = YouTubeViews(item.Views);
        if (views.Length > 0) meta = meta.Length == 0 ? views : meta + " · " + views;
        TextAt(ctx, meta, Math.Max(8.2, 9.6 * _s), DisplayRegular, A(Text, 150), new Rect(card.X, textTop + S(43), card.Width, S(24)));
        var visible = card.Intersect(viewport);
        if (visible.Height > 0) _hits.Add(new Hit(visible, "yt-video", index.ToString(CultureInfo.InvariantCulture)));
    }

    private void DrawYouTubeSkeletons(DrawingContext ctx, Rect viewport)
    {
        double gap = S(20);
        int columns = viewport.Width > S(1080) ? 4 : viewport.Width > S(720) ? 3 : 2;
        double width = (viewport.Width - gap * (columns - 1)) / columns, imageH = Math.Round(width * 9 / 16);
        int pulse = 22 + (int)Math.Round((Math.Sin(Environment.TickCount64 / 280d) + 1) * 10);
        for (int i = 0; i < columns * 2; i++)
        {
            var image = new Rect(viewport.X + (i % columns) * (width + gap), viewport.Y + (i / columns) * (imageH + S(96)), width, imageH);
            ctx.DrawRectangle(B(A(Text, pulse)), null, image, S(7), S(7));
            ctx.FillRectangle(B(A(Text, Math.Max(12, pulse - 7))), new Rect(image.X, image.Bottom + S(11), image.Width * 4 / 5, S(9)));
            ctx.FillRectangle(B(A(Text, Math.Max(12, pulse - 7))), new Rect(image.X, image.Bottom + S(27), image.Width * 3 / 5, S(7)));
        }
        DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(50));
    }

    private void DrawYouTubeEmpty(DrawingContext ctx, Rect viewport)
    {
        TextAt(ctx, _ytStatus.Length == 0 ? "Nessun video trovato" : _ytStatus, Math.Max(12, 14 * _s), DisplaySemibold, Text,
            new Rect(viewport.X, viewport.Y + S(98), viewport.Width, S(34)), TextAlignment.Center);
        TextAt(ctx, "Prova un'altra ricerca o aggiorna la pagina.", Math.Max(8.5, 9.4 * _s), DisplayRegular, A(Text, 150),
            new Rect(viewport.X, viewport.Y + S(134), viewport.Width, S(30)), TextAlignment.Center);
    }

    private bool YouTubeActivate(Hit hit)
    {
        switch (hit.Kind)
        {
            case "yt-refresh": YouTubeSearch(_ytQuery); return true;
            case "yt-video" when int.TryParse(hit.Key, out int i) && i >= 0 && i < _ytItems.Count:
                YouTubeVideoChosen?.Invoke(_ytItems[i].Url, _ytItems[i].Title);
                return true;
        }
        return false;
    }

    internal void TestYouTubeChoose(int index) { if (index >= 0 && index < _ytItems.Count) YouTubeVideoChosen?.Invoke(_ytItems[index].Url, _ytItems[index].Title); }
    internal string YouTubeQa => $"risultati={_ytItems.Count} miniature={_ytItems.Count(i => i.Thumbnail != null)} carico={_ytLoading} stato={_ytStatus}";
}
