using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// Scheda album / artista come su Windows (CinematicMediaLibraryPage.AlbumSheet.cs), nella scheda grande dei film:
// - sotto tutto la foto: la panoramica dell'artista se c'e', altrimenti il fondo fatto con i colori della copertina,
//   sfumata da sinistra e verso il basso;
// - in alto la copertina a sinistra e accanto tipo, titolo, artista · brani, anno / genere / formato e le azioni
//   (Riproduci chiaro e i cerchi Preferiti, Aggiungi alla coda, Aggiungi a playlist, Altro);
// - sotto a sinistra "Tracce" con i numeri grandi e la riga scelta evidenziata, a destra "Potrebbe piacerti".
// Icone Lucide (Assets/Icons.Lucide): quelle di Windows erano troppo simili fra loro.
internal sealed partial class LibraryView
{
    private int _albumSelected;
    private int _albumRecPage;
    private Rect _albumMoreAnchor;
    private string? _albumBackdropKey;
    private IImage? _albumBackdrop;
    private bool _albumBackdropOwned;
    private long _albumBackdropAt;
    private int _albumBackdropJob;
    private static readonly ConcurrentDictionary<string, string> AlbumQuality = new(StringComparer.Ordinal);
    private static readonly HashSet<string> AlbumQualityAsked = new(StringComparer.Ordinal);

    private static bool IsArtistSheet(Album album) => album.Key.StartsWith("artist:", StringComparison.Ordinal);

    private LibraryItem? AlbumSelectedTrack(Album album) =>
        album.Tracks.Count == 0 ? null : album.Tracks[Math.Clamp(_albumSelected, 0, album.Tracks.Count - 1)];

    private void DrawAlbumSheet(DrawingContext ctx, Rect page)
    {
        if (_album is not { } album || album.Tracks.Count == 0) return;
        double t = Math.Clamp((DateTime.UtcNow - _albumOpenedAt).TotalMilliseconds / 220, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        var bounds = new Rect(0, 0, page.Width, page.Height - MusicBarH);
        ctx.FillRectangle(B(Color.FromArgb((byte)(178 * e), 0, 0, 0)), new Rect(page.Size));
        _hitFloor = _hits.Count;
        _hits.Add(new Hit(bounds, "album-close"));
        double scale = DetailScale(bounds);
        var panel = DetailSheetBounds(bounds, scale);
        panel = panel.Translate(new Vector(0, Math.Round(S(22) * (1 - e))));
        using var fade = ctx.PushOpacity(e);
        double k = Math.Clamp(panel.Height / 930, .45, 4);
        double K(double v) => Math.Round(v * k);
        // Le misure di Windows sono pixel di GDI+: il carattere da "px" diventa punti come altrove (0,75).
        double Pt(double px) => Math.Max(7, px * .75);
        var first = album.Tracks[0];
        var selected = AlbumSelectedTrack(album) ?? first;
        bool byArtist = IsArtistSheet(album);
        Color back = UiTheme.Panel;

        using (ctx.PushClip(new RoundedRect(panel, K(14))))
            DrawAlbumBackdrop(ctx, panel, album, back);
        _hits.Add(new Hit(panel, "album-panel"));

        // X di chiusura in alto a destra, come la scheda dei film.
        var close = new Rect(panel.Right - K(64), panel.Y + K(18), K(42), K(42));
        bool closeHot = Hover(close);
        double arm = Math.Max(7, close.Width * .40) / 2, cx = close.Center.X, cy = close.Center.Y;
        var xPen = new Pen(B(closeHot ? Text : A(Text, 165)), Math.Max(1.6, close.Width * .055), lineCap: PenLineCap.Round);
        ctx.DrawLine(xPen, new Point(cx - arm, cy - arm), new Point(cx + arm, cy + arm));
        ctx.DrawLine(xPen, new Point(cx + arm, cy - arm), new Point(cx - arm, cy + arm));
        _hits.Add(new Hit(close, "album-close"));

        // ----- intestazione -----
        double pad = K(48);
        double coverSide = Math.Min(K(320), Math.Round(panel.Height * 0.34));
        var cover = new Rect(panel.X + pad, panel.Y + K(52), coverSide, coverSide);
        using (ctx.PushClip(new RoundedRect(cover, K(12))))
        {
            if (Image(album.Art, coverSide) is { } bmp) DrawCover(ctx, bmp, cover);
            else DrawMusicPlaceholder(ctx, cover);
        }

        double x = cover.Right + K(46);
        double textW = Math.Max(K(200), Math.Min(K(820), panel.Right - x - K(90)));
        double y = cover.Y + K(4);
        string kind = byArtist ? "ARTISTA" : IsCollection(album) ? "RACCOLTA" : "ALBUM";
        TextAt(ctx, string.Join(" ", kind.ToCharArray()), Pt(Math.Max(10, K(15))), DisplaySemibold, A(Text, 170), new Rect(x, y, textW, K(22)));
        y += K(36);
        // Il titolo non si taglia: se non sta in due righe il carattere si riduce.
        CachedText title;
        for (double px = Math.Max(20, K(46)); ; px *= 0.9)
        {
            title = Format(album.Title, Pt(px), DisplaySemibold, Text, textW, 3);
            double one = Format("Ag", Pt(px), DisplaySemibold, Text).Height;
            if (title.Height <= one * 2.1 || px < 16) break;
        }
        ctx.DrawText(title, new Point(x, y));
        y += title.Height + K(10);
        int count = album.Tracks.Count;
        string countText = count + (count == 1 ? " brano" : " brani");
        string artistText = byArtist ? "" : album.Artist;
        TextAt(ctx, artistText.Length > 0 ? artistText + "  ·  " + countText : countText, Pt(Math.Max(12, K(20))), DisplayRegular, A(Text, 170),
            new Rect(x, y, textW, K(30)));
        y += K(44);

        // anno, genere, formato
        double chipH = K(30), chipPt = Pt(Math.Max(11, K(18))), cxp = x;
        int? year = album.Tracks.Select(c => c.Year).FirstOrDefault(v => v is > 0);
        string? genre = album.Tracks.SelectMany(c => c.Genres).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !UnknownTag(s));
        // Tag senza anno o genere: il catalogo online, come Windows (risposta in sottofondo, poi si ridisegna).
        if ((year == null || genre == null) && !byArtist && MacAlbumInfo.Get(album.Artist, album.Title, first.Title, first.DurationMinutes * 60) is { } online)
        {
            year ??= online.Year;
            genre ??= online.Genre;
        }
        void Chip(string icon, string text)
        {
            double icn = K(22);
            SvgIcon.Named(icon)?.Draw(ctx, new Rect(cxp, y + (chipH - icn) / 2, icn, icn), A(Text, 205));
            double tw = Measure(text, chipPt, DisplayRegular);
            TextAt(ctx, text, chipPt, DisplayRegular, A(Text, 220), new Rect(cxp + icn + K(10), y, tw + 4, chipH));
            cxp += icn + K(10) + tw + K(30);
        }
        if (year is int yv) Chip("calendar", yv.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(genre)) Chip("music-2", genre!);
        if (AlbumQualityLabel(first) is { Length: > 0 } quality)
        {
            var parts = quality.Split('·').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            string codec = parts.Count > 0 ? parts[0] : quality;
            string rest = string.Join("  ·  ", parts.Skip(1).Where(p => p != "2.0"));
            double bw = Measure(codec, chipPt, DisplayRegular) + K(20);
            var badge = new Rect(cxp, y, bw, chipH);
            ctx.DrawRectangle(B(A(Text, UiTheme.IsLight ? 22 : 34)), null, badge, K(6), K(6));
            TextAt(ctx, codec, chipPt, DisplayRegular, Text, badge, TextAlignment.Center);
            cxp = badge.Right + K(20);
            if (rest.Length > 0) TextAt(ctx, rest, chipPt, DisplayRegular, A(Text, 220), new Rect(cxp, y, Math.Max(10, panel.Right - cxp - pad), chipH));
        }

        // azioni: Riproduci chiaro e quattro cerchi di vetro, poco sotto le informazioni (allineate al fondo della
        // copertina restava un vuoto troppo grande, segnalato il 18/11)
        double actH = K(60);
        y += chipH + K(40);
        var play = new Rect(x, y, K(200), actH);
        DrawHeroButtonShape(ctx, play, true, Hover(play));
        Color playInk = UiTheme.IsLight ? Colors.White : Color.FromRgb(14, 16, 20);
        double playPt = Pt(Math.Max(11, K(19))), glyph = K(16), labelW = Measure("Riproduci", playPt, DisplaySemibold);
        double start = play.X + (play.Width - glyph - K(12) - labelW) / 2, gy = play.Y + (play.Height - glyph) / 2;
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(start + 1, gy), true);
            g.LineTo(new Point(start + glyph, gy + glyph / 2));
            g.LineTo(new Point(start + 1, gy + glyph));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(B(playInk), null, tri);
        TextAt(ctx, "Riproduci", playPt, DisplaySemibold, playInk, new Rect(start + glyph + K(12), play.Y, labelW + 2, play.Height));
        // Riproduci parte dal brano scelto (all'apertura il primo: l'album dall'inizio).
        _hits.Add(new Hit(play, "album-play", _albumSelected.ToString(CultureInfo.InvariantCulture)));
        double bx = play.Right + K(26);
        bool favOn = Favorites.Contains(selected.Path);
        foreach (var (icon, hit, tip) in new[]
        {
            ("star", "album-favorite", favOn ? "Rimuovi dai preferiti" : "Aggiungi ai preferiti"),
            ("list-end", "album-queue", "Aggiungi alla coda"),
            ("list-plus", "album-playlist", "Aggiungi a playlist"),
            ("ellipsis", "album-more", "Altro"),
        })
        {
            var button = new Rect(bx, y, actH, actH);
            bool hot = Hover(button);
            Color glass = UiTheme.IsLight ? Color.FromArgb((byte)(hot ? 34 : 18), 0, 0, 0) : Color.FromArgb((byte)(hot ? 54 : 30), 255, 255, 255);
            ctx.DrawEllipse(B(glass), null, button);
            bool on = hit == "album-favorite" && favOn;
            var inner = button.Deflate(K(18));
            if (on) SvgIcon.Named("star")?.Draw(ctx, inner, Color.FromRgb(255, 214, 92));
            else SvgIcon.Named(icon)?.Draw(ctx, inner, hot ? Text : A(Text, 225));
            _hits.Add(new Hit(button, hit, tip));
            if (hit == "album-more") _albumMoreAnchor = button;
            bx += actH + K(26);
        }
        // Al passaggio, il nome del pulsante sotto il cerchio (su Windows c'era il suggerimento).
        if (_hits.LastOrDefault(h => h.Kind is "album-favorite" or "album-queue" or "album-playlist" or "album-more" && Hover(h.Bounds)) is { } tipHit)
        {
            double tpt = Pt(Math.Max(10, K(15)));
            double tw = Measure(tipHit.Key, tpt, DisplayRegular);
            TextAt(ctx, tipHit.Key, tpt, DisplayRegular, A(Text, 200),
                new Rect(tipHit.Bounds.Center.X - tw / 2 - 2, tipHit.Bounds.Bottom + K(8), tw + 4, K(22)), TextAlignment.Center);
        }

        // ----- parte bassa: tracce a sinistra, consigli a destra -----
        double bodyTop = Math.Max(cover.Bottom, y + actH) + K(44);
        double divider = panel.X + Math.Round(panel.Width * 0.605);
        var tracksArea = new Rect(panel.X + pad - K(12), bodyTop, Math.Max(1, divider - K(32) - (panel.X + pad - K(12))), Math.Max(1, panel.Bottom - K(28) - bodyTop));
        var recArea = new Rect(divider + K(42), bodyTop, Math.Max(1, panel.Right - pad - divider - K(42)), Math.Max(1, panel.Bottom - K(28) - bodyTop));
        ctx.DrawLine(new Pen(B(A(Text, UiTheme.IsLight ? 30 : 34)), Math.Max(1, K(1.2))), new Point(divider, bodyTop + K(6)), new Point(divider, panel.Bottom - K(60)));
        DrawAlbumTracks(ctx, tracksArea, album, k, back);
        DrawAlbumRecs(ctx, recArea, album, k);
    }

    private static bool IsCollection(Album album) =>
        album.Tracks.Select(t => MusicArtistArtworkService.Identity(t.ArtistName ?? "")).Where(s => s.Length > 0).Distinct().Count() > 2;

    private void DrawAlbumTracks(DrawingContext ctx, Rect area, Album album, double k, Color back)
    {
        double K(double v) => Math.Round(v * k);
        double Pt(double px) => Math.Max(7, px * .75);
        double inner = area.X + K(12);
        TextAt(ctx, "Tracce", Pt(Math.Max(14, K(26))), DisplaySemibold, Text, new Rect(inner, area.Y, area.Width / 2, K(40)));
        double minutes = album.Tracks.Sum(c => c.DurationMinutes ?? 0);
        int count = album.Tracks.Count;
        string summary = count + (count == 1 ? " brano" : " brani") + (minutes > 0 ? "  ·  " + FormatTrackTime(minutes) : "");
        double metaPt = Pt(Math.Max(11, K(18)));
        TextAt(ctx, summary, metaPt, DisplayRegular, A(Text, 170), new Rect(area.X, area.Y, area.Width - K(12), K(40)), TextAlignment.Right);

        var list = new Rect(area.X, area.Y + K(58), area.Width, Math.Max(1, area.Height - K(58)));
        _albumListHeight = list.Height;
        double rowH = K(80);
        double maxScroll = Math.Max(0, count * rowH - list.Height);
        _albumScroll = Math.Clamp(_albumScroll, 0, maxScroll);
        double numberPt = Pt(Math.Max(14, K(26))), titlePt = Pt(Math.Max(12, K(20))), artistPt = Pt(Math.Max(10, K(17)));
        using (ctx.PushClip(list))
            for (int i = 0; i < count; i++)
            {
                double top = list.Y + i * rowH - _albumScroll;
                if (top + rowH < list.Y || top > list.Bottom) continue;
                var child = album.Tracks[i];
                var row = new Rect(list.X, top + K(2), list.Width, rowH - K(4));
                bool selectedRow = i == _albumSelected;
                bool hover = Hover(row) && Hover(list);
                bool playing = MusicBarVisible && _queue[_queueIndex].Path == child.Path;
                // Una sola evidenziazione chiara per la riga scelta; il passaggio del mouse e' piu' tenue.
                if (selectedRow || hover)
                    ctx.DrawRectangle(B(A(Text, selectedRow ? (UiTheme.IsLight ? 20 : 26) : (UiTheme.IsLight ? 10 : 13))), null, row, K(14), K(14));
                var numberRect = new Rect(row.X + K(18), row.Y, K(56), row.Height);
                string number = (TrackNumber(child) ?? i + 1).ToString(CultureInfo.InvariantCulture);
                if (playing)
                {
                    double eq = K(20);
                    DrawPlayingBars(ctx, new Rect(numberRect.Center.X - eq / 2, numberRect.Center.Y - eq / 2, eq, eq), UiTheme.Accent, (DateTime.UtcNow - _albumOpenedAt).TotalMilliseconds >= 220,
                        _albumScroll < maxScroll - 0.5 ? new Rect(list.X, list.Y, list.Width, Math.Max(0, list.Height - K(70))) : list);
                }
                else TextAt(ctx, number, numberPt, DisplayRegular, selectedRow ? Text : A(Text, UiTheme.IsLight ? 130 : 140), numberRect, TextAlignment.Center);
                double tx = row.X + K(100), right = row.Right - K(150);
                TextAt(ctx, TrackTitle(child), titlePt, playing ? DisplaySemibold : DisplaySemibold, playing ? UiTheme.Accent : Text,
                    new Rect(tx, row.Y + row.Height / 2 - K(26), Math.Max(1, right - tx), K(28)));
                TextAt(ctx, TrackArtist(child), artistPt, DisplayRegular, A(Text, 160), new Rect(tx, row.Y + row.Height / 2 + K(2), Math.Max(1, right - tx), K(24)));
                string duration = child.DurationMinutes is > 0 ? TimeSpan.FromMinutes(child.DurationMinutes.Value).ToString(@"m\:ss", CultureInfo.InvariantCulture) : "";
                TextAt(ctx, duration, metaPt, DisplayRegular, A(Text, 220), new Rect(row.Right - K(150), row.Y, K(70), row.Height), TextAlignment.Right);
                var more = new Rect(row.Right - K(64), row.Y + (row.Height - K(36)) / 2, K(36), K(36));
                SvgIcon.Named("ellipsis")?.Draw(ctx, more.Deflate(K(6)), Hover(more) ? Text : A(Text, selectedRow || hover ? 220 : 150));
                var visibleRow = row.Intersect(list);
                if (visibleRow.Height > K(20))
                {
                    // Prima la riga, poi i tre puntini: nel clic vince l'ultima zona aggiunta.
                    _hits.Add(new Hit(visibleRow, "album-row", i.ToString(CultureInfo.InvariantCulture), child));
                    _hits.Add(new Hit(more.Inflate(K(6)).Intersect(list), "album-track-more", i.ToString(CultureInfo.InvariantCulture), child));
                }
            }
        // Altre righe sotto: l'ultima sfuma nel fondo invece di essere tagliata.
        if (_albumScroll < maxScroll - 0.5)
        {
            var veil = new Rect(list.X, list.Bottom - K(70), list.Width, K(70));
            ctx.FillRectangle(Vertical(back, (0, 0), (1, 235)), veil);
        }
    }

    // Barrette nel loro livello (PaintLayer): prima ogni movimento (ogni 60 ms) ridisegnava tutta la libreria, e ogni
    // ridisegno in piu' (mouse, scorrimento) faceva partire un'altra catena di ridisegni che si sommava alle altre.
    private PaintLayer? _spotLayer;
    private readonly List<(Rect Box, Color Color)> _spots = new();
    private DispatcherTimer? _spotTimer;

    /// <summary>Niente sopra le schede album e playlist (menu, coda, cartelle, schermo intero, accesso, foto).</summary>
    private bool SpotsUncovered(Rect page) =>
        _spotLayer != null && _menu == null && QueueDrawerRect(page) == null && _signIn == null && _playlistCreate == null
        && _photoList == null && !_foldersOpen && !MusicFullscreenOpen && !_miniPlayer;

    /// <summary>Tre barrette che si muovono: il brano in corso al posto del numero.</summary>
    /// <param name="layerOk">la scheda e' aperta del tutto, nient'altro la copre e <paramref name="safe"/> e' la parte
    /// dell'elenco senza sfumature sopra: le barrette dentro <paramref name="safe"/> vanno nel livello.</param>
    private void DrawPlayingBars(DrawingContext ctx, Rect box, Color color, bool layerOk = false, Rect safe = default)
    {
        bool capture = _glassCapture || _fsPageCapture || _pageTransition.Capturing;
        if (layerOk && !capture && safe.Contains(box) && SpotsUncovered(new Rect(Bounds.Size))) { _spots.Add((box, color)); return; }
        PaintPlayingBars(ctx, box, color);
        if (_audio.IsPlaying && !capture) DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(60));
    }

    private void RenderPlayingSpots(DrawingContext ctx)
    {
        foreach (var (box, color) in _spots) PaintPlayingBars(ctx, box, color);
    }

    private void UpdateSpotTimer()
    {
        if (_spots.Count > 0 && _audio.IsPlaying)
        {
            _spotTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Render, (_, _) =>
            {
                if (_spots.Count == 0 || !_audio.IsPlaying || !IsVisible) { _spotTimer!.Stop(); return; }
                _spotLayer?.InvalidateVisual();
            });
            if (!_spotTimer.IsEnabled) _spotTimer.Start();
        }
        else _spotTimer?.Stop();
    }

    private void PaintPlayingBars(DrawingContext ctx, Rect box, Color color)
    {
        double now = Environment.TickCount64 / 1000.0;
        bool moving = _audio.IsPlaying;
        double w = box.Width / 5;
        for (int i = 0; i < 3; i++)
        {
            double h = moving ? box.Height * (.35 + .65 * Math.Abs(Math.Sin(now * (2.6 + i * .9) + i * 1.3))) : box.Height * (.35 + i % 2 * .3);
            ctx.DrawRectangle(B(color), null, new Rect(box.X + i * 2 * w, box.Bottom - h, w, h), w / 2, w / 2);
        }
    }

    private static string FormatTrackTime(double minutes)
    {
        var span = TimeSpan.FromMinutes(minutes);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }

    /// <summary>Formato del primo brano come su Windows (MusicQualityLabel): codifica · canali · campionamento · bitrate.</summary>
    private string? AlbumQualityLabel(LibraryItem track)
    {
        if (AlbumQuality.TryGetValue(track.Path, out var label)) return label;
        if (track.IsNetwork || !AlbumQualityAsked.Add(track.Path)) return null;
        string path = track.Path;
        _ = Task.Run(async () =>
        {
            var probe = await MediaProbe.RunAsync(path);
            if (probe == null || probe.AudioCodec.Length == 0) return;
            var it = CultureInfo.InvariantCulture;
            string codec = probe.AudioCodec.ToLowerInvariant() switch
            {
                "flac" => "FLAC", "alac" => "ALAC", "aac" => "AAC", "mp3" => "MP3", "opus" => "Opus", "vorbis" => "Vorbis",
                var c when c.StartsWith("pcm", StringComparison.Ordinal) => "PCM", "dsd_lsbf" or "dsd_msbf" => "DSD", var c => c.ToUpperInvariant(),
            };
            string channels = probe.AudioChannels switch { 1 => "1.0", 2 => "2.0", 6 => "5.1", 8 => "7.1", > 0 => probe.AudioChannels.ToString(it), _ => "" };
            string rate = probe.AudioRate > 0 ? (probe.AudioRate / 1000.0).ToString("0.#", it).Replace('.', ',') + " kHz" : "";
            int kbps = probe.AudioKbps > 0 ? probe.AudioKbps : probe.OverallKbps;
            string bitrate = kbps > 0 ? kbps.ToString("N0", MacTools.Italian) + " kb/s" : "";
            AlbumQuality[path] = string.Join(" · ", new[] { codec, channels, rate, bitrate }.Where(s => s.Length > 0));
            Dispatcher.UIThread.Post(() => { if (_album != null) InvalidateVisual(); });
        });
        return null;
    }

    // ----- foto sotto la scheda -----
    private static readonly System.Text.RegularExpressions.Regex SpecificRelease = new(
        @"\b(?:soundtrack|ost|score|complete(?:\s+edition)?|concert)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// MusicSelectionBackdrop di Windows: solo immagini abbastanza grandi (una panoramica dell'artista); per colonne
    /// sonore e raccolte niente foto dell'artista. Senza, il fondo della musica a schermo intero fatto con la copertina.
    /// </summary>
    private string? AlbumBackdropPhoto(Album album)
    {
        if (IsCollection(album) || SpecificRelease.IsMatch(album.Title)) return null;
        string name = IsArtistSheet(album) ? album.Title : album.Artist;
        if (name.Length == 0) return null;
        var artist = MusicArtists(_items.Where(i => i.Category == "Music"))
            .FirstOrDefault(a => MusicArtistArtworkService.Identity(a.Name) == MusicArtistArtworkService.Identity(name));
        if (artist == null) return null;
        string? landscape = ArtistArtwork(artist).Landscape;
        return MusicLandscapeArtworkService.IsLandscapeFile(landscape) ? landscape : null;
    }

    private void DrawAlbumBackdrop(DrawingContext ctx, Rect panel, Album album, Color back) =>
        DrawSheetBackdrop(ctx, panel, album.Key, AlbumBackdropPhoto(album), album.Art, back);

    /// <summary>
    /// Fondo delle schede (album, artista, playlist): una foto larga se c'e', altrimenti lo sfondo fatto con i colori
    /// della copertina; sopra le velature da sinistra e verso il basso.
    /// </summary>
    private void DrawSheetBackdrop(DrawingContext ctx, Rect panel, string sheetKey, string? photo, string? coverArt, Color back)
    {
        ctx.FillRectangle(B(back), panel);
        string key = sheetKey + "|" + (photo ?? "cover:" + coverArt);
        if (key != _albumBackdropKey)
        {
            _albumBackdropKey = key;
            if (_albumBackdropOwned) (_albumBackdrop as IDisposable)?.Dispose();
            _albumBackdrop = null; _albumBackdropOwned = false; _albumBackdropAt = 0;
            if (photo == null)
            {
                int job = ++_albumBackdropJob;
                double aspect = panel.Height > 0 ? panel.Width / panel.Height : 16 / 9.0;
                _ = Task.Run(async () =>
                {
                    float[]? seed = null; const int side = 48;
                    try
                    {
                        string? file = coverArt == null ? null : coverArt.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await NetworkStore.CachedArtAsync(coverArt) : coverArt;
                        if (file != null && File.Exists(file)) seed = ReadSeed(file, side);
                    }
                    catch { }
                    if (seed == null) return;
                    var built = BuildMusicBackground(seed, side, aspect);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (job != _albumBackdropJob || _albumBackdropKey != key) { built.Dispose(); return; }
                        _albumBackdrop = built; _albumBackdropOwned = true; _albumBackdropAt = Environment.TickCount64;
                        _frames.Start();
                        InvalidateVisual();
                    });
                });
            }
        }
        IImage? image = _albumBackdrop;
        double opacity = 1;
        if (photo != null)
        {
            image = Image(photo, panel.Width);
            if (image != null && _albumBackdropAt == 0) _albumBackdropAt = Environment.TickCount64;
        }
        if (image != null)
        {
            // La foto arrivata dopo l'apertura entra in dissolvenza, non di colpo.
            double since = Environment.TickCount64 - _albumBackdropAt;
            if (since < 360) { double f = since / 360.0; opacity = 1 - (1 - f) * (1 - f); DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(16)); }
            using (ctx.PushOpacity(opacity))
            {
                if (image is Bitmap bmp && photo != null) DrawCover(ctx, bmp, panel, .5, .3);
                else
                    using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                        ctx.DrawImage(image, new Rect(image.Size), panel);
            }
        }
        // Velature di Windows: da sinistra (li' stanno copertina e testo) e verso il basso (le tracce).
        ctx.FillRectangle(Horizontal(back, (0, 250), (.22, 238), (.43, 202), (.64, 132), (.83, 58), (1, 20)), panel);
        ctx.FillRectangle(Vertical(back, (0, 0), (.18, 5), (.38, 30), (.58, 88), (.78, 176), (1, 250)), panel);
    }

    // ----- azioni -----
    private bool AlbumActivate(Hit hit, int clicks)
    {
        if (_album is not { } album) return false;
        var tracks = album.Tracks;
        switch (hit.Kind)
        {
            case "album-row" when int.TryParse(hit.Key, out int row) && row >= 0 && row < tracks.Count:
                // Primo clic sceglie la riga, il secondo (o il doppio clic) la suona.
                if (row == _albumSelected && (clicks >= 2 || _albumRowArmed)) { _albumRowArmed = false; PlayQueue(tracks.ToList(), row); }
                else { _albumSelected = row; _albumRowArmed = true; }
                InvalidateVisual();
                return true;
            case "album-rec-more":
                _albumRecPage++;
                InvalidateVisual();
                return true;
            case "album-favorite":
                if (AlbumSelectedTrack(album) is { } fav) { Favorites.Toggle(fav.Path); if (_category == "Favourites") Reload(); }
                InvalidateVisual();
                return true;
            case "album-queue":
                AddToMusicQueue(tracks);
                return true;
            case "album-playlist":
                if (AddToPlaylistMenu(tracks).Children is { Count: > 0 } choices) ShowTextMenu(choices, new Point(hit.Bounds.X, hit.Bounds.Bottom + S(6)));
                return true;
            case "album-more":
                ShowAlbumTrackMenu(album, _albumSelected, new Point(Math.Max(0, hit.Bounds.Right - S(240)), hit.Bounds.Bottom + S(6)), albumActions: true);
                return true;
            case "album-track-more" when int.TryParse(hit.Key, out int index) && index >= 0 && index < tracks.Count:
                _albumSelected = index; _albumRowArmed = false;
                ShowAlbumTrackMenu(album, index, new Point(Math.Max(0, hit.Bounds.Right - S(240)), hit.Bounds.Bottom + S(6)));
                InvalidateVisual();
                return true;
        }
        return false;
    }

    private bool _albumRowArmed;
    /// <summary>Messaggio breve in alto (la capsula OSD della finestra).</summary>
    public event Action<string>? Toast;

    /// <summary>Menu del brano come su Windows (GroupMore): lista di testo, non il menu a tendina.</summary>
    private void ShowAlbumTrackMenu(Album album, int index, Point at, bool albumActions = false)
    {
        var tracks = album.Tracks.ToList();
        if (index < 0 || index >= tracks.Count) return;
        var track = tracks[index];
        var entries = new List<MenuEntry>
        {
            new("Riproduci", "play", () => PlayQueue(tracks, index)),
            new("Aggiungi alla coda", "queue", () => AddToMusicQueue(new[] { track })),
            AddToPlaylistMenu(new[] { track }),
            new("Aggiungi tutto l’album alla coda", "queue", () => AddToMusicQueue(tracks)),
            new(Favorites.Contains(track.Path) ? "Rimuovi dai preferiti" : "Aggiungi ai preferiti", "star", () => { Favorites.Toggle(track.Path); InvalidateVisual(); }),
        };
        if (albumActions && !IsArtistSheet(album) && AlbumHideEntry(album) is { } hide) entries.Add(hide);
        ShowTextMenu(entries, at);
    }

    /// <summary>In fondo alla coda della musica; senza musica in corso parte subito.</summary>
    private void AddToMusicQueue(IReadOnlyList<LibraryItem> tracks)
    {
        if (tracks.Count == 0) return;
        if (!MusicBarVisible) { PlayQueue(tracks.ToList(), 0); return; }
        _queue = _queue.Concat(tracks).ToList();
        _queueOriginal = _queueOriginal.Concat(tracks).ToList();
        UpdateNextSource();
        Toast?.Invoke(tracks.Count == 1 ? "Aggiunto alla coda: " + TrackTitle(tracks[0]) : $"{tracks.Count} brani aggiunti alla coda");
        InvalidateVisual();
    }

    /// <summary>Frecce e Invio nella scheda (anche dal telecomando): su e giu' scelgono il brano, Invio lo suona.</summary>
    private bool HandleAlbumKey(Avalonia.Input.KeyEventArgs e)
    {
        if (_album is not { } album || album.Tracks.Count == 0) return false;
        switch (e.Key)
        {
            case Avalonia.Input.Key.Down: _albumSelected = Math.Min(album.Tracks.Count - 1, _albumSelected + 1); break;
            case Avalonia.Input.Key.Up: _albumSelected = Math.Max(0, _albumSelected - 1); break;
            case Avalonia.Input.Key.Enter: PlayQueue(album.Tracks.ToList(), _albumSelected); break;
            default: return false;
        }
        // La riga scelta resta visibile.
        double k = Math.Clamp(DetailSheetBounds(new Rect(0, 0, Bounds.Width, Bounds.Height - MusicBarH), DetailScale(Bounds)).Height / 930, .45, 4);
        double rowH = Math.Round(80 * k), top = _albumSelected * rowH, viewH = Math.Max(rowH, _albumListHeight);
        if (top < _albumScroll) _albumScroll = top;
        else if (top + rowH > _albumScroll + viewH) _albumScroll = top + rowH - viewH;
        e.Handled = true;
        InvalidateVisual();
        return true;
    }

    private double _albumListHeight;
}
