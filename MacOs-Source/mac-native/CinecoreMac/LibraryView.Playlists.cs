using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// Playlist (QueueAndPlaylists.cs / PlaylistCreateSheet.cs di Windows), stesso file playlists.json: video, musica o
// miste. Pagina con le schede, creazione (nome, descrizione, tipo), scheda con i titoli, "Aggiungi a playlist" dal menu.
internal sealed partial class LibraryView
{
    internal sealed class PlaylistDefinition
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Bucket { get; set; } = "Video";
        public string Description { get; set; } = "";
        public string CoverPath { get; set; } = "";
        public string ArtworkPath { get; set; } = "";
        public List<string> Items { get; set; } = new();
        /// <summary>File .wpl da cui e' stata importata (riaprendolo si aggiorna questa playlist).</summary>
        public string SourceFile { get; set; } = "";
    }

    internal sealed class PlaylistModel
    {
        public Dictionary<string, PlaylistDefinition> Playlists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Memberships { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string PlaylistsPath => Path.Combine(LibraryStore.AppDataDir, "playlists.json");

    // Il file si rilegge solo quando cambia: prima la scheda aperta lo decodificava a ogni fotogramma (scatti).
    private static PlaylistModel? _playlistsCache;
    private static DateTime _playlistsStamp;

    private static PlaylistModel LoadPlaylists()
    {
        try
        {
            if (!File.Exists(PlaylistsPath)) return _playlistsCache = new PlaylistModel();
            var stamp = File.GetLastWriteTimeUtc(PlaylistsPath);
            if (_playlistsCache != null && stamp == _playlistsStamp) return _playlistsCache;
            if (JsonSerializer.Deserialize<PlaylistModel>(File.ReadAllText(PlaylistsPath)) is { } model)
            {
                model.Playlists = new(model.Playlists ?? new(), StringComparer.OrdinalIgnoreCase);
                _playlistsStamp = stamp;
                return _playlistsCache = model;
            }
        }
        catch { }
        return new PlaylistModel();
    }

    private static void SavePlaylists(PlaylistModel model)
    {
        try
        {
            Directory.CreateDirectory(LibraryStore.AppDataDir);
            File.WriteAllText(PlaylistsPath + ".tmp", JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(PlaylistsPath + ".tmp", PlaylistsPath, overwrite: true);
            _playlistsCache = model; _playlistsStamp = File.GetLastWriteTimeUtc(PlaylistsPath);
        }
        catch { _playlistsCache = null; }
    }

    private static string MakePlaylistKey(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        string key = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return key.Length == 0 ? "playlist" : key;
    }

    /// <summary>Musica, Video o Misto (PlaylistBucketForItem/PlaylistAcceptsBucket di Windows).</summary>
    private static string BucketOf(LibraryItem item) => item.Category == "Music" ? "Music" : "Video";
    private static bool Accepts(string bucket, string itemBucket) => bucket is "Mixed" or "Misto" || string.Equals(bucket, itemBucket, StringComparison.OrdinalIgnoreCase);
    private static string BucketLabel(string bucket) => bucket switch { "Music" => "Musica", "Mixed" => "Mista", _ => "Video" };

    private Dictionary<string, string>? _trackAlbumArt;

    /// <summary>
    /// Copertina di un brano nella playlist: la sua, altrimenti quella del suo album (molti brani non hanno ArtPath:
    /// la copertina e' dell'album o arriva dopo). Senza nessuna delle due si chiede in sottofondo.
    /// </summary>
    private string? TrackArt(LibraryItem track)
    {
        if (!string.IsNullOrWhiteSpace(track.ArtPath)) return track.ArtPath;
        // La mappa brano -> copertina dell'album si rifa' al massimo ogni 10 s (con la libreria musicale gia' letta
        // per KnownItems): prima rileggeva tutta la musica per ogni riga a ogni fotogramma.
        long now = Environment.TickCount64;
        if (_trackAlbumArt == null || now - _trackAlbumArtAt > 10_000)
        {
            var music = _knownMusic ?? LibraryStore.Items("Music");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var album in Albums(music))
                if (album.Tracks.Select(t => t.ArtPath).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) is { } art)
                    foreach (var t in album.Tracks) map.TryAdd(t.Path, art);
            _trackAlbumArt = map; _trackAlbumArtAt = now;
        }
        if (_trackAlbumArt.TryGetValue(track.Path, out var found)) return found;
        if (_artAsked.Add(track.Path)) LibraryStore.QueueMusicArtwork(track);
        return null;
    }

    /// <summary>Per il telecomando (coda): la copertina del brano o del suo album.</summary>
    internal string? TrackArtFor(LibraryItem track) => TrackArt(track);

    private long _trackAlbumArtAt;
    private readonly HashSet<string> _artAsked = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LibraryItem>? _knownItems;
    private List<LibraryItem>? _knownMusic;
    private long _knownAt;

    /// <summary>
    /// Ogni percorso con i dati gia' noti: libreria del Mac (anche la musica) e cataloghi dei server. Si ricostruisce
    /// al massimo ogni 10 s: prima la scheda aperta rileggeva tutta la libreria a ogni fotogramma.
    /// </summary>
    private Dictionary<string, LibraryItem> KnownItems()
    {
        long now = Environment.TickCount64;
        if (_knownItems != null && now - _knownAt < 10_000) return _knownItems;
        _knownItems = BuildKnownItems(); _knownAt = now;
        return _knownItems;
    }

    private Dictionary<string, LibraryItem> BuildKnownItems()
    {
        _knownMusic = LibraryStore.Items("Music");
        var known = new Dictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (string category in new[] { "Movies", "TV Series", "Videos" })
            foreach (var item in LibraryStore.Items(category)) known.TryAdd(item.Path, item);
        foreach (var item in _knownMusic) known.TryAdd(item.Path, item);
        foreach (var server in NetworkStore.Servers)
            if (NetworkStore.Catalog(server.Key) is { } catalog)
                foreach (var item in catalog) known.TryAdd(item.Path, item);
        return known;
    }

    private List<LibraryItem> PlaylistItems(PlaylistDefinition playlist, Dictionary<string, LibraryItem>? known = null)
    {
        known ??= KnownItems();
        return playlist.Items.Select(path => known.TryGetValue(path, out var item) ? item : new LibraryItem
        {
            Path = path,
            Title = Path.GetFileNameWithoutExtension(Uri.TryCreate(path, UriKind.Absolute, out var u) && !u.IsFile ? Uri.UnescapeDataString(u.AbsolutePath) : path),
            Category = MediaFileKindsIsAudio(path) ? "Music" : "Movies",
            IsNetwork = path.StartsWith("http", StringComparison.OrdinalIgnoreCase),
        }).ToList();
    }

    private static bool MediaFileKindsIsAudio(string path) =>
        CinecorePlayer2025.Utilities.MediaFileKinds.IsAudioExtension(Path.GetExtension(path).ToLowerInvariant());

    // ----- pagina -----
    private void DrawPlaylistsPage(DrawingContext ctx, Rect viewport)
    {
        var playlists = LoadPlaylists().Playlists.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (playlists.Count == 0)
        {
            var (title, body, action, key) = EmptyTexts("Playlists");
            DrawEmptyHome(ctx, new Rect(viewport.X, BarH, viewport.Width, Math.Max(1, Bounds.Height - BarH - S(20))), title, body, action, key);
            return;
        }
        var known = KnownItems();
        double gap = S(24);
        int columns = (int)Math.Clamp(Math.Floor((viewport.Width + gap) / Math.Max(1, S(200) + gap)), 3, 10);
        double cardW = Math.Max(1, Math.Floor((viewport.Width - gap * (columns - 1)) / columns));
        double cardH = cardW + S(52), rowGap = S(22);
        int count = playlists.Count + 1; // in fondo la scheda "Nuova playlist"
        int rows = (count + columns - 1) / columns;
        double bottom = viewport.Bottom - MusicBarH;
        _scrollMax = Math.Max(0, rows * (cardH + rowGap) - rowGap + S(24) - (bottom - viewport.Y));
        _scroll = Math.Clamp(_scroll, 0, _scrollMax); _scrollTarget = Math.Clamp(_scrollTarget, 0, _scrollMax);
        using (ctx.PushClip(new Rect(0, viewport.Y - S(6), Bounds.Width, Math.Max(1, bottom - viewport.Y + S(6)))))
            for (int i = 0; i < count; i++)
            {
                double y = viewport.Y + (i / columns) * (cardH + rowGap) - _scroll;
                if (y > bottom) break;
                if (y + cardH < viewport.Y) continue;
                var r = new Rect(viewport.X + (i % columns) * (cardW + gap), y, cardW, cardH);
                var cover = new Rect(r.X, r.Y, cardW, cardW);
                double radius = S(6);
                bool hover = Hover(r), light = UiTheme.IsLight;
                if (i == playlists.Count)
                {
                    // "Nuova playlist": riquadro tratteggiato con il +.
                    var dash = new Pen(B(A(Text, hover ? 150 : 70)), 1.5) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
                    ctx.DrawRectangle(null, dash, cover.Deflate(1), radius, radius);
                    SvgIcon.Get("add")?.Draw(ctx, new Rect(cover.Center.X - S(16), cover.Center.Y - S(16), S(32), S(32)), A(Text, hover ? 220 : 140));
                    TextAt(ctx, "Nuova playlist", Math.Max(9.4, 11.4 * _s), DisplaySemibold, Text, new Rect(r.X + 1, cover.Bottom + S(8), r.Width - 2, S(20)));
                    _hits.Add(new Hit(r, "playlist-new"));
                    continue;
                }
                var playlist = playlists[i];
                var items = PlaylistItems(playlist, known);
                using (ctx.PushGeometryClip(new RectangleGeometry(cover, radius, radius)))
                    DrawPlaylistCover(ctx, cover, playlist, items);
                ctx.DrawRectangle(null, hover ? new Pen(B(light ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(215, 255, 255, 255)), 2)
                                              : new Pen(B(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255)), 1), cover.Deflate(hover ? 1 : .5), radius, radius);
                TextAt(ctx, playlist.Name, Math.Max(9.4, 11.4 * _s), DisplaySemibold, Text, new Rect(r.X + 1, cover.Bottom + S(8), r.Width - 2, S(20)));
                string info = (items.Count == 1 ? "1 elemento" : $"{items.Count} elementi") + "  ·  " + BucketLabel(playlist.Bucket);
                TextAt(ctx, info, Math.Max(9, 10.6 * _s), DisplayRegular, A(Text, 160), new Rect(r.X + 1, cover.Bottom + S(28), r.Width - 2, S(18)));
                var hit = r.Intersect(new Rect(0, viewport.Y, Bounds.Width, Math.Max(1, bottom - viewport.Y)));
                if (hit.Width > 0 && hit.Height > 0) _hits.Add(new Hit(hit, "playlist", playlist.Key));
            }
    }

    // ----- scheda della playlist -----
    private string? _playlistOpen;
    private DateTime _playlistOpenedAt;
    private double _playlistScroll;

    /// <summary>
    /// Scheda della playlist come la scheda dell'album: copertina grande a sinistra (quella scelta dall'utente, altrimenti
    /// le copertine dei primi quattro titoli in una griglia 2x2), "PLAYLIST", titolo, dati, Riproduci e i cerchi,
    /// poi l'elenco numerato a tutta larghezza.
    /// </summary>
    private void DrawPlaylistSheet(DrawingContext ctx, Rect page)
    {
        if (_playlistOpen == null) return;
        if (!LoadPlaylists().Playlists.TryGetValue(_playlistOpen, out var playlist)) { _playlistOpen = null; return; }
        var items = PlaylistItems(playlist);
        double t = Math.Clamp((DateTime.UtcNow - _playlistOpenedAt).TotalMilliseconds / 220, 0, 1), e = 1 - Math.Pow(1 - t, 3);
        var bounds = new Rect(0, 0, page.Width, page.Height - MusicBarH);
        ctx.FillRectangle(B(Color.FromArgb((byte)(178 * e), 0, 0, 0)), new Rect(page.Size));
        _hitFloor = _hits.Count;
        _hits.Add(new Hit(bounds, "playlist-close"));
        double scale = DetailScale(bounds);
        var panel = DetailSheetBounds(bounds, scale).Translate(new Vector(0, Math.Round(S(22) * (1 - e))));
        using var fade = ctx.PushOpacity(e);
        double k = Math.Clamp(panel.Height / 930, .45, 4);
        double K(double v) => Math.Round(v * k);
        double Pt(double px) => Math.Max(7, px * .75);
        Color back = UiTheme.Panel;
        // Come la scheda dell'album: sotto una foto (l'artista principale della playlist, o per i video l'immagine larga
        // del primo titolo), altrimenti il fondo fatto con i colori della copertina.
        var musicTracks = items.Where(i => i.Category == "Music").ToList();
        var sheetAlbum = PlaylistAsAlbum(playlist, musicTracks);
        string? coverArt = File.Exists(playlist.CoverPath) ? playlist.CoverPath
            : items.Select(i => i.Category == "Music" ? TrackArt(i) : i.ArtPath ?? i.WideArtPath).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        string? photo = sheetAlbum != null ? PlaylistArtistPhoto(sheetAlbum, musicTracks)
            : items.Select(i => i.WideArtPath).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        using (ctx.PushClip(new RoundedRect(panel, K(14))))
            DrawSheetBackdrop(ctx, panel, "playlist:" + _playlistOpen, photo, coverArt, back);
        _hits.Add(new Hit(panel, "album-panel"));

        var close = new Rect(panel.Right - K(64), panel.Y + K(18), K(42), K(42));
        double arm = Math.Max(7, close.Width * .40) / 2, ccx = close.Center.X, ccy = close.Center.Y;
        var xPen = new Pen(B(Hover(close) ? Text : A(Text, 165)), Math.Max(1.6, close.Width * .055), lineCap: PenLineCap.Round);
        ctx.DrawLine(xPen, new Point(ccx - arm, ccy - arm), new Point(ccx + arm, ccy + arm));
        ctx.DrawLine(xPen, new Point(ccx + arm, ccy - arm), new Point(ccx - arm, ccy + arm));
        _hits.Add(new Hit(close, "playlist-close"));

        // ----- intestazione -----
        double pad = K(48);
        double coverSide = Math.Min(K(320), Math.Round(panel.Height * 0.34));
        var cover = new Rect(panel.X + pad, panel.Y + K(52), coverSide, coverSide);
        using (ctx.PushClip(new RoundedRect(cover, K(12))))
            DrawPlaylistCover(ctx, cover, playlist, items);

        double x = cover.Right + K(46);
        double textW = Math.Max(K(200), Math.Min(K(820), panel.Right - x - K(90)));
        double y = cover.Y + K(4);
        TextAt(ctx, string.Join(" ", "PLAYLIST".ToCharArray()), Pt(Math.Max(10, K(15))), DisplaySemibold, A(Text, 170), new Rect(x, y, textW, K(22)));
        y += K(36);
        CachedText title;
        for (double px = Math.Max(20, K(46)); ; px *= 0.9)
        {
            title = Format(playlist.Name, Pt(px), DisplaySemibold, Text, textW, 3);
            double one = Format("Ag", Pt(px), DisplaySemibold, Text).Height;
            if (title.Height <= one * 2.1 || px < 16) break;
        }
        ctx.DrawText(title, new Point(x, y));
        y += title.Height + K(10);
        double minutes = items.Sum(i => i.DurationMinutes ?? 0);
        string info = BucketLabel(playlist.Bucket) + "  ·  " + (items.Count == 1 ? "1 elemento" : $"{items.Count} elementi")
                      + (minutes > 0 ? "  ·  " + FormatTrackTime(minutes) : "");
        TextAt(ctx, info, Pt(Math.Max(12, K(20))), DisplayRegular, A(Text, 170), new Rect(x, y, textW, K(30)));
        y += K(40);
        if (!string.IsNullOrWhiteSpace(playlist.Description))
        {
            var description = Format(playlist.Description.Trim(), Pt(Math.Max(11, K(18))), DisplayRegular, A(Text, 200), textW, 2);
            ctx.DrawText(description, new Point(x, y));
            y += description.Height + K(8);
        }

        // Dati come la scheda dell'album: anni, genere prevalente e formato audio (codifica · canali · kHz · kb/s).
        if (musicTracks.Count > 0)
        {
            y += K(4);
            double chipH = K(30), chipPt = Pt(Math.Max(11, K(18))), cxp = x;
            bool chips = false;
            void Chip(string icon, string text)
            {
                double icn = K(22);
                SvgIcon.Named(icon)?.Draw(ctx, new Rect(cxp, y + (chipH - icn) / 2, icn, icn), A(Text, 205));
                double tw = Measure(text, chipPt, DisplayRegular);
                TextAt(ctx, text, chipPt, DisplayRegular, A(Text, 220), new Rect(cxp + icn + K(10), y, tw + 4, chipH));
                cxp += icn + K(10) + tw + K(30);
                chips = true;
            }
            var years = musicTracks.Select(t => t.Year).Where(v => v is > 0).Select(v => v!.Value).ToList();
            if (years.Count > 0)
                Chip("calendar", years.Min() == years.Max() ? years.Min().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : $"{years.Min()}–{years.Max()}");
            if (musicTracks.SelectMany(t => t.Genres).Where(s => !string.IsNullOrWhiteSpace(s) && !UnknownTag(s))
                    .GroupBy(s => s.Trim(), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault() is { } genre)
                Chip("music-2", genre.Key);
            // Il formato del primo brano (ffprobe in sottofondo, come la scheda album); con formati diversi lo si dice.
            if (AlbumQualityLabel(musicTracks[0]) is { Length: > 0 } quality)
            {
                var parts = quality.Split('·').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                string codec = parts.Count > 0 ? parts[0] : quality;
                bool mixed = musicTracks.Select(t => Path.GetExtension(t.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
                string rest = mixed ? "formati misti" : string.Join("  ·  ", parts.Skip(1).Where(p => p != "2.0"));
                double bw = Measure(codec, chipPt, DisplayRegular) + K(20);
                var badge = new Rect(cxp, y, bw, chipH);
                ctx.DrawRectangle(B(A(Text, UiTheme.IsLight ? 22 : 34)), null, badge, K(6), K(6));
                TextAt(ctx, codec, chipPt, DisplayRegular, Text, badge, TextAlignment.Center);
                cxp = badge.Right + K(20);
                if (rest.Length > 0) TextAt(ctx, rest, chipPt, DisplayRegular, A(Text, 220), new Rect(cxp, y, Math.Max(10, panel.Right - cxp - pad), chipH));
                chips = true;
            }
            if (chips) y += chipH + K(10);
        }
        // Film e serie: i dati tecnici come la scheda del film (anni, genere prevalente, 4K / HDR / Dolby Vision, durata).
        else if (items.Count > 0)
        {
            y += K(4);
            double chipH = K(30), chipPt = Pt(Math.Max(11, K(18))), cxp = x;
            bool chips = false;
            void Chip(string icon, string text)
            {
                double icn = K(22);
                SvgIcon.Named(icon)?.Draw(ctx, new Rect(cxp, y + (chipH - icn) / 2, icn, icn), A(Text, 205));
                double tw = Measure(text, chipPt, DisplayRegular);
                TextAt(ctx, text, chipPt, DisplayRegular, A(Text, 220), new Rect(cxp + icn + K(10), y, tw + 4, chipH));
                cxp += icn + K(10) + tw + K(30);
                chips = true;
            }
            void Badge(string text)
            {
                double bw = Measure(text, chipPt, DisplayRegular) + K(20);
                var badge = new Rect(cxp, y, bw, chipH);
                ctx.DrawRectangle(B(A(Text, UiTheme.IsLight ? 22 : 34)), null, badge, K(6), K(6));
                TextAt(ctx, text, chipPt, DisplayRegular, Text, badge, TextAlignment.Center);
                cxp = badge.Right + K(14);
                chips = true;
            }
            var years = items.Select(i => i.Year).Where(v => v is > 0).Select(v => v!.Value).ToList();
            if (years.Count > 0)
                Chip("calendar", years.Min() == years.Max() ? years.Min().ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{years.Min()}–{years.Max()}");
            if (items.SelectMany(i => i.Genres).Where(s => !string.IsNullOrWhiteSpace(s))
                    .GroupBy(s => s.Trim(), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault() is { } genre)
                Chip("movie", genre.Key);
            // Quanti titoli in 4K, HDR e Dolby Vision (su tutti: "4K" da solo; su alcuni: "4K 2/5").
            string Count(int n) => n == items.Count ? "" : $" {n}/{items.Count}";
            int uhd = items.Count(i => i.Is4K), hdr = items.Count(i => i.IsHdr && !i.DolbyVision), dv = items.Count(i => i.DolbyVision);
            if (uhd > 0) Badge("4K" + Count(uhd));
            if (dv > 0) Badge("Dolby Vision" + Count(dv));
            if (hdr > 0) Badge("HDR" + Count(hdr));
            if (uhd == 0 && hdr == 0 && dv == 0 && items.Select(i => i.ResolutionLabel).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) is { } res) Badge(res);
            if (chips) y += chipH + K(10);
        }

        double actH = K(60);
        y += K(30);
        bool any = items.Count > 0;
        var play = new Rect(x, y, K(200), actH);
        DrawHeroButtonShape(ctx, play, true, any && Hover(play));
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
        if (any) _hits.Add(new Hit(play, "playlist-play", "0"));
        double bx = play.Right + K(26);
        foreach (var (icon, hit, tip) in new[] { ("list-end", "playlist-queue", "Aggiungi alla coda"), ("pencil", "playlist-edit", "Modifica nome e descrizione"), ("trash-2", "playlist-delete", "Elimina playlist") })
        {
            if (hit == "playlist-queue" && !any) continue;
            var button = new Rect(bx, y, actH, actH);
            bool hot = Hover(button);
            ctx.DrawEllipse(B(UiTheme.IsLight ? Color.FromArgb((byte)(hot ? 34 : 18), 0, 0, 0) : Color.FromArgb((byte)(hot ? 54 : 30), 255, 255, 255)), null, button);
            SvgIcon.Named(icon)?.Draw(ctx, button.Deflate(K(18)), hot ? Text : A(Text, 225));
            _hits.Add(new Hit(button, hit, tip));
            if (hot)
            {
                double tpt = Pt(Math.Max(10, K(15))), tw = Measure(tip, tpt, DisplayRegular);
                TextAt(ctx, tip, tpt, DisplayRegular, A(Text, 200), new Rect(button.Center.X - tw / 2 - 2, button.Bottom + K(8), tw + 4, K(22)), TextAlignment.Center);
            }
            bx += actH + K(26);
        }

        // ----- elenco a sinistra, consigli a destra (come l'album): brani e "Potrebbe piacerti", oppure locandine
        // dei film e "Consigliati" -----
        double bodyTop = Math.Max(cover.Bottom, y + actH) + K(44);
        var area = new Rect(panel.X + pad - K(12), bodyTop, panel.Width - 2 * pad + K(24), Math.Max(1, panel.Bottom - K(28) - bodyTop));
        if (sheetAlbum != null)
        {
            double divider = panel.X + Math.Round(panel.Width * 0.605);
            area = new Rect(area.X, bodyTop, Math.Max(1, divider - K(32) - area.X), area.Height);
            var recArea = new Rect(divider + K(42), bodyTop, Math.Max(1, panel.Right - pad - divider - K(42)), area.Height);
            ctx.DrawLine(new Pen(B(A(Text, UiTheme.IsLight ? 30 : 34)), Math.Max(1, K(1.2))), new Point(divider, bodyTop + K(6)), new Point(divider, panel.Bottom - K(60)));
            DrawAlbumRecs(ctx, recArea, sheetAlbum, k);
        }
        // Film e serie: tutte le locandine a tutta larghezza, una fila sotto l'altra; sotto, i Consigliati a carosello.
        else if (any)
        {
            DrawPlaylistPosters(ctx, area, items, k);
            return;
        }
        double innerX = area.X + K(12);
        TextAt(ctx, playlist.Bucket == "Music" ? "Tracce" : "Titoli", Pt(Math.Max(14, K(26))), DisplaySemibold, Text, new Rect(innerX, area.Y, area.Width / 2, K(40)));
        double metaPt = Pt(Math.Max(11, K(18)));
        if (!any)
        {
            TextAt(ctx, "Aggiungi film, episodi o brani con il tasto destro › Aggiungi a playlist.", metaPt, DisplayRegular, A(Text, 150),
                new Rect(innerX, area.Y + K(58), area.Width - K(24), K(30)));
            return;
        }
        var list = new Rect(area.X, area.Y + K(58), area.Width, Math.Max(1, area.Height - K(58)));
        double rowH = K(80);
        double maxScroll = Math.Max(0, items.Count * rowH - list.Height);
        _playlistScroll = Math.Clamp(_playlistScroll, 0, maxScroll);
        double numberPt = Pt(Math.Max(14, K(26))), titlePt = Pt(Math.Max(12, K(20))), subPt = Pt(Math.Max(10, K(17)));
        using (ctx.PushClip(list))
            for (int i = 0; i < items.Count; i++)
            {
                double top = list.Y + i * rowH - _playlistScroll;
                if (top + rowH < list.Y || top > list.Bottom) continue;
                var item = items[i];
                var row = new Rect(list.X, top + K(2), list.Width, rowH - K(4));
                bool hover = Hover(row) && Hover(list);
                bool playing = MusicBarVisible && _queue[_queueIndex].Path == item.Path;
                if (hover) ctx.DrawRectangle(B(A(Text, UiTheme.IsLight ? 14 : 18)), null, row, K(14), K(14));
                var numberRect = new Rect(row.X + K(18), row.Y, K(56), row.Height);
                if (playing)
                {
                    double eq = K(20);
                    // Un album aperto dai consigli sta sopra la playlist: allora le barrette restano qui.
                    DrawPlayingBars(ctx, new Rect(numberRect.Center.X - eq / 2, numberRect.Center.Y - eq / 2, eq, eq), UiTheme.Accent, t >= 1 && _album == null,
                        _playlistScroll < maxScroll - 0.5 ? new Rect(list.X, list.Y, list.Width, Math.Max(0, list.Height - K(70))) : list);
                }
                else TextAt(ctx, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), numberPt, DisplayRegular, A(Text, UiTheme.IsLight ? 130 : 140), numberRect, TextAlignment.Center);
                // Miniatura del titolo accanto al numero (film ed episodi a 16:9, brani quadrati).
                bool music = item.Category == "Music";
                double thumbH = rowH - K(24), thumbW = music ? thumbH : Math.Round(thumbH * 16 / 9);
                var thumb = new Rect(row.X + K(90), row.Y + (row.Height - thumbH) / 2, thumbW, thumbH);
                using (ctx.PushGeometryClip(new RectangleGeometry(thumb, K(8), K(8))))
                {
                    if (Image(music ? TrackArt(item) : item.WideArtPath ?? item.ArtPath, thumbW) is { } bmp) DrawCover(ctx, bmp, thumb);
                    else if (music) DrawMusicPlaceholder(ctx, thumb);
                    else DrawGeneratedArt(ctx, thumb, item.Title, true, item.Category);
                }
                double tx = thumb.Right + K(22), right = row.Right - K(150);
                string name = music ? TrackTitle(item) : item.Category == "TV Series" && item.SeriesTitle != null ? item.SeriesTitle : item.Title;
                string sub = music ? TrackArtist(item)
                    : item.Category == "TV Series" && item.EpisodeNumber.HasValue ? $"Stagione {item.SeasonNumber ?? 1}, episodio {item.EpisodeNumber}" + (item.SeriesTitle != null && item.Title != item.SeriesTitle ? "  ·  " + item.Title : "")
                    : item.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                TextAt(ctx, name, titlePt, DisplaySemibold, playing ? UiTheme.Accent : Text, new Rect(tx, row.Y + row.Height / 2 - K(26), Math.Max(1, right - tx), K(28)));
                TextAt(ctx, sub, subPt, DisplayRegular, A(Text, 160), new Rect(tx, row.Y + row.Height / 2 + K(2), Math.Max(1, right - tx), K(24)));
                string duration = item.DurationMinutes is > 0
                    ? (item.DurationMinutes.Value >= 60 && !music ? $"{(int)(item.DurationMinutes.Value / 60)} h {(int)(item.DurationMinutes.Value % 60):00} min" : TimeSpan.FromMinutes(item.DurationMinutes.Value).ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture))
                    : "";
                TextAt(ctx, duration, metaPt, DisplayRegular, A(Text, 220), new Rect(row.Right - K(190), row.Y, K(110), row.Height), TextAlignment.Right);
                var remove = new Rect(row.Right - K(64), row.Y + (row.Height - K(36)) / 2, K(36), K(36));
                if (hover)
                {
                    double c = K(7);
                    var pen = new Pen(B(Hover(remove) ? Text : A(Text, 150)), Math.Max(1.4, K(2)), lineCap: PenLineCap.Round);
                    ctx.DrawLine(pen, new Point(remove.Center.X - c, remove.Center.Y - c), new Point(remove.Center.X + c, remove.Center.Y + c));
                    ctx.DrawLine(pen, new Point(remove.Center.X + c, remove.Center.Y - c), new Point(remove.Center.X - c, remove.Center.Y + c));
                }
                var rowHit = row.Intersect(list);
                if (rowHit.Height <= K(20)) continue;
                _hits.Add(new Hit(rowHit, "playlist-play", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (hover) _hits.Add(new Hit(remove.Inflate(K(6)).Intersect(list), "playlist-remove", item.Path));
            }
        if (_playlistScroll < maxScroll - 0.5)
            ctx.FillRectangle(Vertical(back, (0, 0), (1, 235)), new Rect(list.X, list.Bottom - K(70), list.Width, K(70)));
    }

    /// <summary>Copertina della playlist: quella scelta; senza, le copertine dei primi quattro titoli in una griglia 2x2
    /// (con meno di quattro copertine diverse, la prima intera).</summary>
    private void DrawPlaylistCover(DrawingContext ctx, Rect cover, PlaylistDefinition playlist, List<LibraryItem> items)
    {
        if (File.Exists(playlist.CoverPath) && Image(playlist.CoverPath, cover.Width) is { } chosen) { DrawCover(ctx, chosen, cover); return; }
        var arts = items.Select(i => i.Category == "Music" ? TrackArt(i) : i.ArtPath ?? i.WideArtPath)
            .Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        if (arts.Count >= 4)
        {
            double half = cover.Width / 2;
            for (int i = 0; i < 4; i++)
            {
                var cell = new Rect(cover.X + (i % 2) * half, cover.Y + (i / 2) * half, half, half);
                if (Image(arts[i], half) is { } bmp) DrawCover(ctx, bmp, cell);
                else ctx.FillRectangle(B(A(Text, 20)), cell);
            }
            return;
        }
        if (arts.Count > 0 && Image(arts[0], cover.Width) is { } single) { DrawCover(ctx, single, cover); return; }
        if (playlist.Bucket == "Music") DrawMusicPlaceholder(ctx, cover);
        else DrawGeneratedArt(ctx, cover, playlist.Name, true, "Playlists");
    }

    private string? _playlistRecsKey;
    private List<LibraryItem> _playlistRecs = new();
    private int _playlistRecPage;

    /// <summary>
    /// Playlist di film e serie: tutte le locandine verticali (come nella Home) a tutta larghezza, una fila sotto
    /// l'altra; sotto, i Consigliati in una fila a carosello con le frecce. Tutta la parte bassa scorre con la rotella.
    /// </summary>
    private void DrawPlaylistPosters(DrawingContext ctx, Rect area, List<LibraryItem> items, double k)
    {
        double K(double v) => Math.Round(v * k);
        double Pt(double px) => Math.Max(7, px * .75);
        double inner = area.X + K(12), width = area.Width - K(24);
        double headH = K(40), gap = K(22);
        // Le colonne riempiono tutta la larghezza (locandine attorno ai 190 punti): niente vuoto a destra.
        int cols = Math.Max(3, (int)Math.Floor((width + gap) / (K(190) + gap)));
        double cardW = Math.Floor((width - gap * (cols - 1)) / cols);
        double cardH = Math.Round(cardW * 1.5) + PosterCaptionHeight(cardW);
        var recs = PlaylistRecommendations(items);
        int rows = (items.Count + cols - 1) / cols;
        double gridH = rows * (cardH + gap) - gap;
        double contentH = headH + K(18) + gridH + (recs.Count > 0 ? K(40) + headH + K(18) + cardH : 0);
        double maxScroll = Math.Max(0, contentH - area.Height);
        _playlistScroll = Math.Clamp(_playlistScroll, 0, maxScroll);

        void Card(Rect r, LibraryItem item, int index)
        {
            int before = _hits.Count;
            DrawPosterCard(ctx, r, item);
            _hits.RemoveRange(before, _hits.Count - before); // nella playlist il clic fa partire da qui, non apre la scheda
            var visible = r.Intersect(area);
            if (visible.Height < K(20)) return;
            _hits.Add(new Hit(visible, index >= 0 ? "playlist-play" : "playlist-rec",
                index >= 0 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : item.Path, item));
            if (index < 0 || !Hover(r) || !Hover(area)) return;
            // Al passaggio, la X in alto a destra della locandina toglie il titolo dalla playlist.
            double d = K(30);
            var x = new Rect(r.Right - d - K(8), r.Y + K(8), d, d);
            if (!area.Contains(x.Center)) return;
            ctx.DrawEllipse(B(Color.FromArgb((byte)(Hover(x) ? 220 : 160), 0, 0, 0)), null, x);
            double c = K(5);
            var pen = new Pen(B(Colors.White), Math.Max(1.4, K(2)), lineCap: PenLineCap.Round);
            ctx.DrawLine(pen, new Point(x.Center.X - c, x.Center.Y - c), new Point(x.Center.X + c, x.Center.Y + c));
            ctx.DrawLine(pen, new Point(x.Center.X + c, x.Center.Y - c), new Point(x.Center.X - c, x.Center.Y + c));
            _hits.Add(new Hit(x, "playlist-remove", item.Path));
        }

        using (ctx.PushClip(area))
        {
            double top = area.Y - _playlistScroll;
            TextAt(ctx, "Titoli", Pt(Math.Max(14, K(26))), DisplaySemibold, Text, new Rect(inner, top, width, headH));
            double gridY = top + headH + K(18);
            for (int i = 0; i < items.Count; i++)
            {
                var r = new Rect(inner + (i % cols) * (cardW + gap), gridY + (i / cols) * (cardH + gap), cardW, cardH);
                if (r.Bottom < area.Y || r.Y > area.Bottom) continue;
                Card(r, items[i], i);
            }
            if (recs.Count > 0)
            {
                // Consigliati: una fila a carosello; le frecce in alto a destra passano alla pagina accanto.
                double recY = gridY + gridH + K(40);
                TextAt(ctx, "Consigliati", Pt(Math.Max(14, K(26))), DisplaySemibold, Text, new Rect(inner, recY, width / 2, headH));
                int pages = (recs.Count + cols - 1) / cols;
                _playlistRecPage = Math.Clamp(_playlistRecPage, 0, Math.Max(0, pages - 1));
                if (pages > 1)
                {
                    double ab = K(36);
                    foreach (var (prev, ax) in new[] { (true, inner + width - ab * 2 - K(10)), (false, inner + width - ab) })
                    {
                        var arrow = new Rect(ax, recY + (headH - ab) / 2, ab, ab);
                        bool enabled = prev ? _playlistRecPage > 0 : _playlistRecPage < pages - 1;
                        bool hot = enabled && Hover(arrow);
                        // Solo la freccia, senza cerchio dietro (come "Vedi altri" degli album); al passaggio diventa piena.
                        var pen = new Pen(B(!enabled ? A(Text, 70) : hot ? Text : A(Text, 190)), Math.Max(1.6, K(2.4)), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                        double cx = arrow.Center.X + (prev ? K(2) : -K(2)), cy = arrow.Center.Y, d = K(6);
                        double tip = prev ? cx - d / 2 : cx + d / 2, tail = prev ? cx + d / 2 : cx - d / 2;
                        ctx.DrawLine(pen, new Point(tail, cy - d), new Point(tip, cy));
                        ctx.DrawLine(pen, new Point(tip, cy), new Point(tail, cy + d));
                        if (enabled) _hits.Add(new Hit(arrow, prev ? "playlist-rec-prev" : "playlist-rec-next"));
                    }
                }
                double rowY = recY + headH + K(18);
                var page = recs.Skip(_playlistRecPage * cols).Take(cols).ToList();
                for (int i = 0; i < page.Count; i++)
                    Card(new Rect(inner + i * (cardW + gap), rowY, cardW, cardH), page[i], -1);
            }
        }
        // Altre locandine sotto: il bordo basso sfuma invece di tagliarle.
        if (_playlistScroll < maxScroll - 0.5)
            ctx.FillRectangle(Vertical(UiTheme.Panel, (0, 0), (1, 235)), new Rect(area.X, area.Bottom - K(70), area.Width, K(70)));
    }


    /// <summary>
    /// Consigliati della playlist (come quelli della scheda film): film affini secondo Trakt per i titoli della playlist,
    /// poi generi in comune e anni vicini, pesati su tutta la playlist; fuori i titoli gia' presenti.
    /// </summary>
    private List<LibraryItem> PlaylistRecommendations(List<LibraryItem> items)
    {
        var trakt = items.Where(i => i.Category == "Movies").Select(TraktRelatedFor).ToList();
        string key = _playlistOpen + "|" + string.Join(",", items.Select(i => i.Path)) + "|" + trakt.Count(t => t != null);
        if (key == _playlistRecsKey) return _playlistRecs;
        _playlistRecsKey = key;
        var inside = new HashSet<string>(items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var titles = new HashSet<string>(items.Select(i => (i.SeriesTitle ?? i.Title).Trim()), StringComparer.CurrentCultureIgnoreCase);
        var genres = items.SelectMany(i => i.Genres).GroupBy(g => g, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var years = items.Select(i => i.Year).Where(y => y is > 0).Select(y => y!.Value).ToList();
        double year = years.Count > 0 ? years.Average() : 0;
        var candidates = items.Select(i => i.Category).Distinct().Where(c => c is "Movies" or "TV Series").SelectMany(LibraryStore.Items)
            .Where(c => !inside.Contains(c.Path) && !titles.Contains((c.SeriesTitle ?? c.Title).Trim()))
            .GroupBy(c => c.Category == "TV Series" ? "s|" + (c.SeriesTitle ?? c.Title) : c.Title.Trim() + "|" + c.Year, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
        _playlistRecs = candidates
            .Select(c => (Item: c, Score:
                trakt.Sum(t => t?.FindIndex(p => string.Equals(p, c.Path, StringComparison.OrdinalIgnoreCase)) is int ri && ri >= 0 ? 1000 - ri * 5 : 0)
                + c.Genres.Sum(g => genres.TryGetValue(g, out int n) ? n * 100 : 0)
                + (year > 0 && c.Year.HasValue ? Math.Max(0, 18 - Math.Abs(year - c.Year.Value) * 2) : 0)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Item).Take(12).ToList();
        if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null)
            Console.Error.WriteLine($"[playlist] consigliati={_playlistRecs.Count} categorie={string.Join(",", items.Select(i => i.Category).Distinct())} generi={genres.Count} anno={year:0}");
        return _playlistRecs;
    }

    /// <summary>
    /// La playlist musicale vista come un album per i consigli: l'artista e' quello che compare piu' spesso.
    /// Null per le playlist senza musica (film e serie: elenco a tutta larghezza).
    /// </summary>
    private Album? PlaylistAsAlbum(PlaylistDefinition playlist, List<LibraryItem> music)
    {
        if (music.Count == 0) return null;
        string artist = music.Select(t => t.ArtistName).Where(a => !string.IsNullOrWhiteSpace(a))
            .GroupBy(a => MusicArtistArtworkService.Identity(a!)).OrderByDescending(g => g.Count()).Select(g => g.First()!).FirstOrDefault() ?? "";
        return new Album("playlist:" + _playlistOpen, playlist.Name, artist, music);
    }

    /// <summary>Foto larga dell'artista principale, solo se e' davvero lui la playlist (almeno meta' dei brani).</summary>
    private string? PlaylistArtistPhoto(Album sheet, List<LibraryItem> music)
    {
        // Calcolata una volta per playlist (e rifatta ogni 10 s): cercare la foto scorre tutti gli artisti.
        string cacheKey = sheet.Key + "|" + music.Count;
        long now = Environment.TickCount64;
        if (cacheKey == _photoKey && now - _photoAt < 10_000) return _photo;
        _photoKey = cacheKey; _photoAt = now;
        return _photo = FindPlaylistArtistPhoto(sheet, music);
    }

    private string? _photoKey, _photo;
    private long _photoAt;

    private string? FindPlaylistArtistPhoto(Album sheet, List<LibraryItem> music)
    {
        if (sheet.Artist.Length == 0) return null;
        string id = MusicArtistArtworkService.Identity(sheet.Artist);
        var own = music.Where(t => MusicArtistArtworkService.Identity(t.ArtistName ?? "") == id).ToList();
        if (own.Count * 2 < music.Count) return null;
        return AlbumBackdropPhoto(new Album(sheet.Key, sheet.Title, sheet.Artist, own));
    }

    /// <summary>Riproduce dalla voce indicata: musica nella barra in basso, video uno dopo l'altro nel player.</summary>
    public event Action<List<LibraryItem>>? VideoQueueRequested;

    private void PlayPlaylist(int from)
    {
        if (_playlistOpen == null || !LoadPlaylists().Playlists.TryGetValue(_playlistOpen, out var playlist)) return;
        var items = PlaylistItems(playlist);
        if (from < 0 || from >= items.Count) return;
        var start = items[from];
        if (start.Category == "Music")
        {
            var tracks = items.Skip(from).Where(i => i.Category == "Music").ToList();
            PlayQueue(tracks, 0);
        }
        else
        {
            var videos = items.Skip(from).Where(i => i.Category != "Music").ToList();
            _playlistOpen = null;
            VideoQueueRequested?.Invoke(videos);
        }
        InvalidateVisual();
    }

    private bool PlaylistActivate(Hit hit)
    {
        switch (hit.Kind)
        {
            case "playlist": _playlistOpen = hit.Key; _playlistOpenedAt = DateTime.UtcNow; _playlistScroll = 0; _playlistRecPage = 0; _frames.Start(); InvalidateVisual(); return true;
            case "playlist-new": OpenPlaylistCreate(null); return true;
            case "playlist-close": _playlistOpen = null; InvalidateVisual(); return true;
            case "album-rec-more" when _album == null && _playlistOpen != null: _albumRecPage++; InvalidateVisual(); return true;
            case "playlist-edit": if (_playlistOpen != null) OpenPlaylistEdit(_playlistOpen); return true;
            case "playlist-rec-prev": _playlistRecPage = Math.Max(0, _playlistRecPage - 1); InvalidateVisual(); return true;
            case "playlist-rec-next": _playlistRecPage++; InvalidateVisual(); return true;
            case "playlist-rec":
                // Un consigliato: si chiude la playlist e si apre la sua scheda (come un titolo della Home).
                if (hit.Item is { } rec) { _playlistOpen = null; OpenDetail(rec); }
                return true;
            case "playlist-play": if (int.TryParse(hit.Key, out int index)) PlayPlaylist(index); return true;
            case "playlist-remove":
            {
                var model = LoadPlaylists();
                if (_playlistOpen != null && model.Playlists.TryGetValue(_playlistOpen, out var p)) { p.Items.RemoveAll(x => string.Equals(x, hit.Key, StringComparison.OrdinalIgnoreCase)); SavePlaylists(model); }
                InvalidateVisual();
                return true;
            }
            case "playlist-queue":
            {
                if (_playlistOpen == null || !LoadPlaylists().Playlists.TryGetValue(_playlistOpen, out var pq)) return true;
                var all = PlaylistItems(pq);
                var music = all.Where(i => i.Category == "Music").ToList();
                if (music.Count > 0) AddToMusicQueue(music);
                foreach (var video in all.Where(i => i.Category != "Music")) QueueAddRequested?.Invoke(video);
                return true;
            }
            case "playlist-delete":
            {
                var model = LoadPlaylists();
                if (_playlistOpen != null && model.Playlists.Remove(_playlistOpen)) SavePlaylists(model);
                _playlistOpen = null; InvalidateVisual();
                return true;
            }
        }
        return false;
    }

    /// <summary>Voce "Aggiungi a playlist" per il menu del tasto destro: le playlist adatte (spuntate se gia' contengono
    /// tutto) e in fondo "Nuova playlist…".</summary>
    private MenuEntry AddToPlaylistMenu(IReadOnlyList<LibraryItem> items)
    {
        var root = new MenuEntry("Aggiungi a playlist") { Icon = "playlist", Children = new() };
        if (items.Count == 0) { root.Enabled = false; return root; }
        string bucket = BucketOf(items[0]);
        var paths = items.Select(i => i.Path).ToList();
        foreach (var playlist in LoadPlaylists().Playlists.Values.Where(p => Accepts(p.Bucket, bucket)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string key = playlist.Key;
            bool all = paths.All(p => playlist.Items.Contains(p, StringComparer.OrdinalIgnoreCase));
            root.Children.Add(new MenuEntry(playlist.Name, null, () =>
            {
                var model = LoadPlaylists();
                if (!model.Playlists.TryGetValue(key, out var target)) return;
                foreach (string path in paths) if (!target.Items.Contains(path, StringComparer.OrdinalIgnoreCase)) target.Items.Add(path);
                SavePlaylists(model);
                InvalidateVisual();
            }) { Checked = all });
        }
        if (root.Children.Count > 0) root.Children.Add(MenuEntry.Sep());
        root.Children.Add(new MenuEntry("Nuova playlist…", "add", () => OpenPlaylistCreate(items)));
        return root;
    }

    // ----- scheda "Nuova playlist" (PlaylistCreateSheet di Windows): quattro passi su pagina velata -----
    private sealed class PlaylistCreateState
    {
        public TextBox Name = null!, Description = null!;
        public string Bucket = "Music";
        public List<string> Pending = new();
        public Rect NameRect, DescriptionRect, Cancel, Next, Back, CoverTile, CoverRemove;
        public List<(Rect Rect, string Bucket)> Buckets = new();
        /// <summary>Copertina scelta (facoltativa), come "Una copertina?" della scheda di Windows.</summary>
        public string? Cover;
        /// <summary>Playlist che si sta modificando (nome, tipo, copertina, descrizione); null per una nuova.</summary>
        public string? EditKey;
        public int Step, Direction = 1;
        public bool NameError;
        public DateTime OpenedAt = DateTime.UtcNow, StepAt = DateTime.UtcNow;
    }
    private PlaylistCreateState? _playlistCreate;
    private const int PlaylistSteps = 4;
    private static readonly (string Bucket, string Label)[] PlaylistBuckets = { ("Music", "Musica"), ("Video", "Film, serie e video"), ("Mixed", "Un po' di tutto") };

    private void OpenPlaylistCreate(IReadOnlyList<LibraryItem>? pending)
    {
        if (_playlistCreate != null || InputLayer == null) return;
        var st = new PlaylistCreateState
        {
            Name = PlaylistInput(), Description = PlaylistInput(),
            Bucket = pending is { Count: > 0 } ? BucketOf(pending[0]) : _category == "Music" ? "Music" : "Video",
            Pending = pending?.Select(i => i.Path).ToList() ?? new(),
        };
        st.Description.IsVisible = false;
        st.Name.TextChanged += (_, _) => { if (st.NameError) { st.NameError = false; InvalidateVisual(); } };
        _playlistCreate = st;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => st.Name.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        InvalidateVisual();
    }

    /// <summary>Modifica una playlist gia' creata: la stessa scheda della creazione, con i dati di adesso.</summary>
    private void OpenPlaylistEdit(string key)
    {
        if (!LoadPlaylists().Playlists.TryGetValue(key, out var playlist)) return;
        OpenPlaylistCreate(null);
        if (_playlistCreate is not { } st) return;
        st.EditKey = key;
        st.Name.Text = playlist.Name;
        st.Description.Text = playlist.Description;
        st.Bucket = playlist.Bucket is "Music" or "Mixed" ? playlist.Bucket : "Video";
        st.Cover = File.Exists(playlist.CoverPath) ? playlist.CoverPath : null;
        InvalidateVisual();
    }

    private TextBox PlaylistInput()
    {
        var box = new TextBox();
        WizardSheet.StyleBox(box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { PlaylistNext(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ClosePlaylistCreate(); e.Handled = true; }
        };
        box.GotFocus += (_, _) => InvalidateVisual();
        box.LostFocus += (_, _) => InvalidateVisual();
        InputLayer?.Children.Add(box);
        return box;
    }

    private void ClosePlaylistCreate()
    {
        if (_playlistCreate is not { } st) return;
        InputLayer?.Children.Remove(st.Name); InputLayer?.Children.Remove(st.Description);
        _playlistCreate = null;
        Focus();
        InvalidateVisual();
    }

    private void PlaylistGoTo(int step)
    {
        if (_playlistCreate is not { } st) return;
        step = Math.Clamp(step, 0, PlaylistSteps - 1);
        if (step == st.Step) return;
        st.Direction = step > st.Step ? 1 : -1;
        st.Step = step;
        st.StepAt = DateTime.UtcNow;
        st.Name.IsVisible = step == 0;
        st.Description.IsVisible = step == PlaylistSteps - 1;
        if (step == 0) Avalonia.Threading.Dispatcher.UIThread.Post(() => st.Name.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        else if (step == PlaylistSteps - 1) Avalonia.Threading.Dispatcher.UIThread.Post(() => st.Description.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        else Focus();
        InvalidateVisual();
    }

    /// <summary>Avanti (Invio): il nome serve; all'ultimo passo crea.</summary>
    private void PlaylistNext()
    {
        if (_playlistCreate is not { } st) return;
        if (st.Step == 0 && (st.Name.Text ?? "").Trim().Length == 0) { st.NameError = true; st.Name.Focus(); InvalidateVisual(); return; }
        if (st.Step == PlaylistSteps - 1) { CreatePlaylist(); return; }
        PlaylistGoTo(st.Step + 1);
    }

    private void CreatePlaylist()
    {
        if (_playlistCreate is not { } st) return;
        string name = (st.Name.Text ?? "").Trim();
        if (name.Length == 0) { PlaylistGoTo(0); st.NameError = true; return; }
        var model = LoadPlaylists();
        // Modifica: stessa playlist (stessa chiave e stessi titoli), cambiano nome, tipo, copertina e descrizione.
        if (st.EditKey != null && model.Playlists.TryGetValue(st.EditKey, out var edited))
        {
            edited.Name = name;
            edited.Description = (st.Description.Text ?? "").Trim();
            edited.Bucket = st.Bucket;
            edited.CoverPath = st.Cover ?? "";
            SavePlaylists(model);
            _playlistRecsKey = null;
            ClosePlaylistCreate();
            return;
        }
        string key = MakePlaylistKey(name), baseKey = key;
        for (int n = 2; model.Playlists.ContainsKey(key); n++) key = baseKey + "-" + n;
        model.Playlists[key] = new PlaylistDefinition
        {
            Key = key, Name = name, Description = (st.Description.Text ?? "").Trim(), Bucket = st.Bucket, Items = st.Pending.ToList(),
            CoverPath = st.Cover ?? "",
        };
        SavePlaylists(model);
        ClosePlaylistCreate();
    }

    private bool PlaylistCreatePressed(Point p)
    {
        if (_playlistCreate is not { } st) return false;
        if (st.Cancel.Contains(p)) ClosePlaylistCreate();
        else if (st.Next.Contains(p)) PlaylistNext();
        else if (st.Back.Width > 0 && st.Back.Contains(p)) PlaylistGoTo(st.Step - 1);
        else if (st.Step == 0 && st.NameRect.Contains(p)) st.Name.Focus();
        else if (st.Step == 3 && st.DescriptionRect.Contains(p)) st.Description.Focus();
        else if (st.Step == 1 && st.Buckets.FirstOrDefault(b => b.Rect.Contains(p)) is { Bucket: { } bucket }) { st.Bucket = bucket; InvalidateVisual(); }
        else if (st.Step == 2 && st.Cover != null && st.CoverRemove.Contains(p)) { st.Cover = null; InvalidateVisual(); }
        else if (st.Step == 2 && st.CoverTile.Contains(p)) _ = ChoosePlaylistCoverAsync(st);
        return true;
    }

    /// <summary>Tasti quando il fuoco non e' in una casella (passi del tipo e della copertina).</summary>
    private bool PlaylistCreateKey(KeyEventArgs e)
    {
        if (_playlistCreate is not { } st) return false;
        if (e.Key == Key.Escape) ClosePlaylistCreate();
        else if (e.Key == Key.Enter) PlaylistNext();
        else if (st.Step == 1 && e.Key is Key.Up or Key.Down)
        {
            int i = Array.FindIndex(PlaylistBuckets, b => b.Bucket == st.Bucket);
            st.Bucket = PlaylistBuckets[Math.Clamp(i + (e.Key == Key.Down ? 1 : -1), 0, PlaylistBuckets.Length - 1)].Bucket;
            InvalidateVisual();
        }
        else return false;
        e.Handled = true;
        return true;
    }

    private string PlaylistPendingSummary(PlaylistCreateState st) =>
        st.Pending.Count == 0 ? "Una playlist raccoglie film, serie, musica o foto in un'unica lista."
        : st.Pending.Count == 1 ? "Dentro ci sarà il titolo che hai scelto."
        : $"Dentro ci saranno i {st.Pending.Count} titoli che hai scelto.";

    /// <summary>Come PaintStep di Windows: punti del passo, "Passo n di 4", titolo, testo, il controllo del passo, Avanti/Indietro.</summary>
    private void DrawPlaylistCreate(DrawingContext ctx, Rect page)
    {
        if (_playlistCreate is not { } st) return;
        _hitFloor = _hits.Count;
        double open = Math.Clamp((DateTime.UtcNow - st.OpenedAt).TotalMilliseconds / 380.0, 0, 1);
        double t = Math.Clamp((DateTime.UtcNow - st.StepAt).TotalMilliseconds / 380.0, 0, 1);
        if (open < 1 || t < 1) Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual, Avalonia.Threading.DispatcherPriority.Background);
        double eOpen = 1 - Math.Pow(1 - open, 3), e = 1 - Math.Pow(1 - t, 3);
        WizardSheet.DrawVeil(ctx, page, eOpen);

        double u = WizardSheet.Scale(page.Size);
        var column = WizardSheet.Column(page.Size).Translate(new Vector(page.X, page.Y));
        byte alpha = (byte)Math.Round(255 * Math.Min(e, eOpen));
        double slide = Math.Round(26 * u * (1 - e)) * st.Direction;
        st.Cancel = WizardSheet.Cancel(page.Size).Translate(new Vector(page.X, page.Y));
        WizardSheet.DrawCancel(ctx, st.Cancel, Hover(st.Cancel), u);

        bool editing = st.EditKey != null;
        string finish = editing ? "Salva" : "Crea playlist";
        (string Title, string Body) text = st.Step switch
        {
            0 => (editing ? "Il nome della playlist" : "Come la chiamiamo?", editing ? "Cambialo quanto vuoi: i titoli dentro restano." : PlaylistPendingSummary(st)),
            1 => ("Che cosa ci metterai?", "Serve a mostrarla nella sezione giusta. Puoi sempre mettere insieme cose diverse."),
            2 => ("Una copertina?", "Scegli un'immagine. Senza, la copertina si fa da sola con i titoli dentro."),
            _ => (editing ? "La descrizione" : "Ultima cosa: una descrizione", editing ? "Facoltativa. Invio per salvare." : "Facoltativa. Invio per creare la playlist."),
        };
        double top = WizardSheet.DrawHeader(ctx, column, u, st.Step, PlaylistSteps, text.Title, text.Body, alpha, slide);
        double x = column.X + slide, bottom;
        switch (st.Step)
        {
            case 0:
                st.NameRect = new Rect(column.X, top, column.Width, 64 * u);
                WizardSheet.DrawUnderline(ctx, st.NameRect.Translate(new Vector(slide, 0)), st.Name.IsFocused, st.NameError, alpha, u);
                if (st.NameError) WizardSheet.DrawError(ctx, st.NameRect, "Dai un nome alla playlist", u);
                bottom = st.NameRect.Bottom;
                break;
            case 1:
            {
                st.Buckets.Clear();
                double rowH = 48 * u;
                for (int i = 0; i < PlaylistBuckets.Length; i++)
                {
                    var (bucket, label) = PlaylistBuckets[i];
                    var r = new Rect(column.X, top + i * rowH, 420 * u, rowH);
                    bool selected = st.Bucket == bucket, hover = Hover(r);
                    Color ink = selected ? WizardSheet.Ink(alpha) : hover ? WizardSheet.Ink((byte)(alpha * 220 / 255)) : WizardSheet.Soft(alpha);
                    var ft = WizardSheet.Text(label, 22 * u, selected ? WizardSheet.Semibold : WizardSheet.Regular, ink, r.Width);
                    ctx.DrawText(ft, new Point(x, r.Y + (r.Height - ft.Height) / 2));
                    st.Buckets.Add((r, bucket));
                }
                bottom = top + PlaylistBuckets.Length * rowH;
                break;
            }
            case 2:
            {
                double side = Math.Min(300 * u, Math.Max(160 * u, page.Bottom - top - 220 * u));
                st.CoverTile = new Rect(column.X, top, side, side);
                st.CoverRemove = new Rect(st.CoverTile.Right - 40 * u, st.CoverTile.Y + 12 * u, 28 * u, 28 * u);
                var tile = st.CoverTile.Translate(new Vector(slide, 0));
                bool hover = Hover(tile);
                if (st.Cover != null && Image(st.Cover, (int)Math.Ceiling(side)) is { } coverBmp)
                {
                    using (ctx.PushOpacity(alpha / 255.0))
                    using (ctx.PushClip(new RoundedRect(tile, 18 * u))) DrawCover(ctx, coverBmp, tile);
                    var rm = st.CoverRemove.Translate(new Vector(slide, 0));
                    ctx.DrawEllipse(B(Color.FromArgb((byte)(alpha * (Hover(rm) ? 220 : 150) / 255), 0, 0, 0)), null, rm);
                    var pen = new Pen(B(Color.FromArgb(alpha, 255, 255, 255)), Math.Max(1.4, 1.8 * u), lineCap: PenLineCap.Round);
                    var c = rm.Deflate(9 * u);
                    ctx.DrawLine(pen, c.TopLeft, c.BottomRight); ctx.DrawLine(pen, c.TopRight, c.BottomLeft);
                }
                else
                {
                    ctx.DrawRectangle(B(A(UiTheme.Text, (byte)(alpha * (hover ? 22 : 12) / 255))), null, tile, 18 * u, 18 * u);
                    double cx = tile.Center.X, cy = tile.Center.Y - 14 * u;
                    var plus = new Pen(B(A(UiTheme.Text, (byte)(alpha * (hover ? 255 : 200) / 255))), Math.Max(1.8, 2.4 * u), lineCap: PenLineCap.Round);
                    ctx.DrawLine(plus, new Point(cx - 14 * u, cy), new Point(cx + 14 * u, cy));
                    ctx.DrawLine(plus, new Point(cx, cy - 14 * u), new Point(cx, cy + 14 * u));
                    var cap = WizardSheet.Text("Clic per scegliere un'immagine", 16 * u, WizardSheet.Regular, WizardSheet.Soft(alpha), tile.Width - 24 * u, 1, TextAlignment.Center);
                    ctx.DrawText(cap, new Point(tile.X + 12 * u, cy + 30 * u));
                }
                bottom = st.CoverTile.Bottom;
                break;
            }
            default:
                st.DescriptionRect = new Rect(column.X, top, column.Width, 120 * u);
                WizardSheet.DrawUnderline(ctx, st.DescriptionRect.Translate(new Vector(slide, 0)), st.Description.IsFocused, false, alpha, u);
                bottom = st.DescriptionRect.Bottom;
                break;
        }
        (st.Next, st.Back) = WizardSheet.Buttons(column, bottom + 48 * u, u, st.Step == PlaylistSteps - 1 ? finish : "Avanti", st.Step > 0 ? "Indietro" : null);
        WizardSheet.DrawNext(ctx, st.Next.Translate(new Vector(slide, 0)), st.Step == PlaylistSteps - 1 ? finish : "Avanti", Hover(st.Next), true, alpha, u);
        if (st.Back.Width > 0) WizardSheet.DrawBack(ctx, st.Back.Translate(new Vector(slide, 0)), "Indietro", Hover(st.Back), alpha, u);
    }

    private async System.Threading.Tasks.Task ChoosePlaylistCoverAsync(PlaylistCreateState st)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Copertina della playlist", AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Immagini") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.heic", "*.bmp" } } },
        });
        if (files.Count > 0 && Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(files[0]) is { } path && ReferenceEquals(_playlistCreate, st)) { st.Cover = path; InvalidateVisual(); }
    }

    private void PlacePlaylistInputs()
    {
        if (_playlistCreate is not { } st || Bounds.Width <= 0) return;
        double u = WizardSheet.Scale(Bounds.Size);
        // Durante il passaggio fra un passo e l'altro la casella resta nascosta (sul vetro scorre solo il disegno).
        bool settled = (DateTime.UtcNow - st.StepAt).TotalMilliseconds > 380;
        st.Name.FontSize = 34 * u; st.Description.FontSize = 22 * u;
        st.Description.TextWrapping = TextWrapping.Wrap;
        foreach (var (box, r, h) in new[] { (st.Name, st.NameRect, 34 * u * 1.4), (st.Description, st.DescriptionRect, 120 * u - 20 * u) })
        {
            if (r.Width <= 0) continue;
            var inner = box == st.Description ? new Rect(r.X + WizardSheet.FieldInset(u), r.Y + 14 * u, r.Width - 2 * WizardSheet.FieldInset(u), r.Height - 28 * u) : WizardSheet.BoxIn(r, h, u);
            Canvas.SetLeft(box, inner.X); Canvas.SetTop(box, inner.Y);
            box.Width = inner.Width; box.Height = inner.Height;
            box.Foreground = B(UiTheme.Text); box.CaretBrush = B(UiTheme.Text);
            box.SelectionBrush = B(A(UiTheme.Text, 70));
            box.Opacity = settled ? 1 : 0;
        }
    }

    /// <summary>
    /// Playlist .wpl di Windows Media Player (dal Finder, trascinata o da Apri › Apri playlist…): diventa una playlist
    /// di Cinecore con lo stesso nome (riaprendo lo stesso file si aggiorna, non si duplica), si apre la sua scheda e
    /// parte dal primo elemento.
    /// </summary>
    internal void ImportPlaylistFile(string file)
    {
        WplPlaylist.Result result;
        try { result = WplPlaylist.Read(file, KnownItems().Keys); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("wpl: " + ex.GetType().Name);
            Toast?.Invoke("Playlist non leggibile: " + Path.GetFileName(file));
            return;
        }
        if (result.Paths.Count == 0)
        {
            Toast?.Invoke(result.Total == 0 ? "La playlist è vuota" : "Nessun file della playlist trovato su questo computer");
            return;
        }
        var model = LoadPlaylists();
        string source = Path.GetFullPath(file);
        var playlist = model.Playlists.Values.FirstOrDefault(p => string.Equals(p.SourceFile, source, StringComparison.OrdinalIgnoreCase));
        if (playlist == null)
        {
            string key = MakePlaylistKey(result.Title), unique = key;
            for (int n = 2; model.Playlists.ContainsKey(unique); n++) unique = key + "-" + n;
            playlist = new PlaylistDefinition { Key = unique, Name = result.Title, SourceFile = source };
            model.Playlists[unique] = playlist;
        }
        playlist.Items = result.Paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool anyAudio = playlist.Items.Any(MediaFileKindsIsAudio), anyVideo = playlist.Items.Any(p => !MediaFileKindsIsAudio(p));
        playlist.Bucket = anyAudio && anyVideo ? "Mixed" : anyAudio ? "Music" : "Video";
        SavePlaylists(model);

        Navigate("Playlists");
        _playlistOpen = playlist.Key; _playlistOpenedAt = DateTime.UtcNow; _playlistScroll = 0;
        _frames.Start();
        if (result.Missing > 0) Toast?.Invoke(result.Missing == 1 ? "1 file della playlist non trovato" : $"{result.Missing} file della playlist non trovati");
        PlayPlaylist(0);
        InvalidateVisual();
    }

    /// <summary>Playlist per il telecomando: chiave, nome, tipo.</summary>
    internal List<object> RemotePlaylists() => LoadPlaylists().Playlists.Values
        .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(p => (object)new { key = p.Key, name = p.Name, bucket = p.Bucket }).ToList();

    internal string RemoteAddToPlaylist(string key, IReadOnlyList<string> paths)
    {
        var model = LoadPlaylists();
        if (paths.Count == 0 || !model.Playlists.TryGetValue(key, out var target)) return "Playlist non trovata";
        int added = 0;
        foreach (string path in paths)
            if (!target.Items.Contains(path, StringComparer.OrdinalIgnoreCase)) { target.Items.Add(path); added++; }
        SavePlaylists(model);
        InvalidateVisual();
        return added == 0 ? "Già in " + target.Name : added == 1 ? "Aggiunto a " + target.Name : $"{added} titoli aggiunti a {target.Name}";
    }

    internal void RemoteAddMusicToQueue(IReadOnlyList<LibraryItem> tracks) => AddToMusicQueue(tracks);

    internal void TestPlaylistNext() => PlaylistNext();
    internal void TestPlaylistSheet() { OpenPlaylistCreate(LibraryStore.Items("Movies").Take(2).ToList()); if (_playlistCreate != null) _playlistCreate.Name.Text = "Serata sci-fi"; }
    internal void TestMakePlaylist(string name) { if (_playlistCreate == null) TestPlaylistSheet(); if (_playlistCreate != null) { _playlistCreate.Name.Text = name; CreatePlaylist(); } }
    internal void TestOpenPlaylist(string name)
    {
        var p = LoadPlaylists().Playlists.Values.FirstOrDefault(x => x.Name == name);
        if (p != null) { _playlistOpen = p.Key; _playlistOpenedAt = DateTime.UtcNow; _frames.Start(); InvalidateVisual(); }
    }
}
