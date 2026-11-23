using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Pagina Musica in stile Home (CinematicMediaLibraryPage.MusicOverview di Windows): in alto gli artisti a rotazione su
/// una foto grande (Spotify, TIDAL, Deezer o la cartella), poi "Continua ad ascoltare", una fila di album e una di
/// artisti, ognuna con "Vedi tutti" che apre la griglia completa. Nella griglia il selettore "Album ⌄" passa ad
/// Artisti, Brani e Playlist. La ricerca usa sempre la griglia.
/// </summary>
internal sealed partial class LibraryView
{
    internal sealed record ArtistGroup(string Name, List<LibraryItem> Tracks);

    private bool _musicOverview = true;
    private int _musicTab;            // 0 Album, 1 Artisti, 2 Brani, 3 Playlist
    private int _musicHeroIndex;
    private int _musicHeroFrom = -1;
    private long _musicHeroSwitchAt = -1, _musicHeroShownAt;
    private DispatcherTimer? _musicHeroTimer;
    private const double MusicHeroFadeMs = 900, MusicHeroIntervalMs = 9000;

    private bool MusicOverviewActive => _view == "Collection" && _category == "Music" && _musicOverview && _query.Trim().Length == 0;

    private static bool IsVariousArtists(string? name) =>
        name != null && (name.Equals("Artisti vari", StringComparison.OrdinalIgnoreCase) || name.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) || name.Equals("VA", StringComparison.OrdinalIgnoreCase));

    private static List<ArtistGroup> MusicArtists(IEnumerable<LibraryItem> tracks) =>
        tracks.Select(t => (Name: TrackArtist(t), Track: t))
            .Where(x => !UnknownTag(x.Name) && x.Name.Length > 0)
            .GroupBy(x => MusicTextIdentity.ArtistKey(x.Name))
            .Select(g => new ArtistGroup(MusicTextIdentity.PreferredArtistName(g.Select(x => x.Name)), g.Select(x => x.Track).ToList()))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    // ------------------------------------------------------------------ foto degli artisti
    private static readonly ConcurrentDictionary<string, MusicArtistArtworkService.Artwork> ArtistArt = new();
    private static readonly ConcurrentDictionary<string, byte> ArtistArtPending = new();
    private static bool _artistArtHooked;

    /// <summary>Prima la cartella (artist.jpg, fanart.jpg…), poi i servizi in sottofondo; all'arrivo la pagina si ridisegna.</summary>
    private MusicArtistArtworkService.Artwork ArtistArtwork(ArtistGroup artist)
    {
        if (!_artistArtHooked)
        {
            _artistArtHooked = true;
            MusicArtistArtworkService.ArtworkUpdated += (name, art) => { ArtistArt[MusicArtistArtworkService.Identity(name)] = art; Dispatcher.UIThread.Post(InvalidateVisual); };
        }
        string key = MusicArtistArtworkService.Identity(artist.Name);
        if (ArtistArt.TryGetValue(key, out var known)) return known;
        string? portrait = null, landscape = null;
        var first = artist.Tracks.FirstOrDefault(t => !t.IsNetwork);
        if (first != null)
            try
            {
                string? album = Path.GetDirectoryName(first.Path), parent = album == null ? null : Path.GetDirectoryName(album);
                // [2026-11-22] Una lettura della cartella invece di 30 domande file per file (artist.jpg, fanart.png...):
                // con 24 artisti nel banner erano ~720 accessi al disco sul thread dell'interfaccia, ~33 ms alla prima
                // apertura di Musica (molto di piu' su un disco di rete). Stesso ordine di prima: cartella, estensione, nome.
                foreach (var dir in new[] { parent, album }.Where(p => p != null))
                {
                    HashSet<string> present;
                    try { present = new HashSet<string>(Directory.EnumerateFiles(dir!).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase); }
                    catch { continue; }
                    foreach (var ext in new[] { ".jpg", ".png", ".jpeg" })
                    {
                        foreach (string name in new[] { "artist", "artist-thumb" }) { if (portrait == null && present.Contains(name + ext)) portrait = Path.Combine(dir!, name + ext); }
                        foreach (string name in new[] { "fanart", "backdrop", "background" }) { var f = Path.Combine(dir!, name + ext); if (landscape == null && present.Contains(name + ext) && MusicLandscapeArtworkService.IsLandscapeFile(f)) landscape = f; }
                    }
                }
            }
            catch { }
        var local = new MusicArtistArtworkService.Artwork(portrait, landscape);
        ArtistArt[key] = local;
        if ((portrait == null || landscape == null) && ArtistArtPending.TryAdd(key, 0))
        {
            string album = artist.Tracks.Select(t => t.AlbumTitle).FirstOrDefault(a => !UnknownTag(a)) ?? "";
            _ = Task.Run(async () =>
            {
                try
                {
                    var resolved = await MusicArtistArtworkService.ResolveAsync(artist.Name, album.Length > 0 ? album : null);
                    ArtistArt[key] = new MusicArtistArtworkService.Artwork(local.Portrait ?? resolved.Portrait, local.Landscape ?? resolved.Landscape, resolved.Source);
                    Dispatcher.UIThread.Post(InvalidateVisual);
                }
                catch { }
            });
        }
        return local;
    }

    private static string? MusicHeroImage(MusicArtistArtworkService.Artwork art)
    {
        if (MusicLandscapeArtworkService.IsLandscapeFile(art.Landscape)) return art.Landscape;
        if (!string.IsNullOrWhiteSpace(art.Portrait) && File.Exists(art.Portrait)) return art.Portrait;
        return null;
    }

    private List<ArtistGroup> MusicHeroDeck(List<ArtistGroup> artists) =>
        artists.Where(a => !IsVariousArtists(a.Name)).OrderByDescending(a => a.Tracks.Count).Take(24)
            .Select(a => (Artist: a, Image: MusicHeroImage(ArtistArtwork(a))))
            .Where(x => x.Image != null)
            .OrderByDescending(x => MusicLandscapeArtworkService.IsLandscapeFile(x.Image)).Select(x => x.Artist).Take(8).ToList();

    private void EnsureMusicHeroTimer()
    {
        _musicHeroTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            if (!IsVisible || !MusicOverviewActive) { _musicHeroTimer?.Stop(); return; }
            long now = Environment.TickCount64;
            if (_musicHeroSwitchAt >= 0)
            {
                if (now - _musicHeroSwitchAt >= MusicHeroFadeMs) { _musicHeroSwitchAt = -1; _musicHeroFrom = -1; }
                InvalidateVisual();
                return;
            }
            // Solo con il banner in vista e senza schede aperte.
            if (SheetOpen || _menu != null || _scroll > Bounds.Height / 3) { _musicHeroShownAt = now; HeroIdle(MusicHeroIntervalMs); return; }
            if (now - _musicHeroShownAt >= MusicHeroIntervalMs) { AdvanceMusicHero(1); _musicHeroTimer!.Interval = TimeSpan.FromMilliseconds(16); }
            else HeroIdle(MusicHeroIntervalMs - (now - _musicHeroShownAt));
        });
        // [2026-11-22] Fra una dissolvenza e l'altra il timer dorme fino al cambio (al massimo 1 s, per accorgersi di
        // schede e scorrimento): prima girava ogni 16 ms senza disegnare nulla, 60 risvegli al secondo per niente.
        void HeroIdle(double ms) => _musicHeroTimer!.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, 16, 1000));
        if (!_musicHeroTimer.IsEnabled) { _musicHeroShownAt = Environment.TickCount64; _musicHeroTimer.Start(); }
    }

    private List<ArtistGroup> _musicHeroDeck = new();

    private void AdvanceMusicHero(int step)
    {
        if (_musicHeroDeck.Count < 2) return;
        _musicHeroFrom = _musicHeroIndex;
        _musicHeroIndex = (_musicHeroIndex + step + _musicHeroDeck.Count) % _musicHeroDeck.Count;
        _musicHeroSwitchAt = _musicHeroShownAt = Environment.TickCount64;
        if (_musicHeroTimer != null) _musicHeroTimer.Interval = TimeSpan.FromMilliseconds(16);
    }

    // ------------------------------------------------------------------ disegno della panoramica
    private void DrawMusicOverview(DrawingContext ctx, Rect page, Rect content, IReadOnlyList<LibraryItem> tracks)
    {
        EnsureMusicHeroTimer();
        var albums = MusicMemo("albums", tracks, null, tracks.Count, () => Albums(tracks));
        var artists = MusicMemo("artists", tracks, null, tracks.Count, () => MusicArtists(tracks));
        if (_musicHeroDeck.Count == 0 || _musicHeroDeck.Any(a => artists.All(x => x.Name != a.Name)) || _musicHeroDeck.Count < Math.Min(3, artists.Count))
            _musicHeroDeck = MusicHeroDeck(artists);
        _musicHeroIndex = _musicHeroDeck.Count == 0 ? 0 : Math.Clamp(_musicHeroIndex, 0, _musicHeroDeck.Count - 1);
        var heroArtist = _musicHeroDeck.Count > 0 ? _musicHeroDeck[_musicHeroIndex] : null;

        double bar = BarH, viewBottom = page.Bottom - MusicBarH, viewH = Math.Max(1, viewBottom - page.Y);
        double rowsTop = Math.Max(bar + S(250), Math.Round(viewH * 0.42));
        double gap = S(20), titleH = S(34), titleGap = S(10), rowGap = S(18), bottomPad = S(10);
        double titleLine = S(24), textLine = S(22);
        var resume = MusicMemo("resume", albums, null, albums.Count, () => MusicResume(albums, MusicListeningStore.Snapshot()));
        bool hasResume = resume.Count > 0;
        double fixedH = (hasResume ? titleH + titleGap + S(10) + titleLine + textLine + rowGap : 0)
            + titleH + titleGap + S(10) + titleLine + textLine + rowGap + titleH + titleGap + S(12) + titleLine + bottomPad;
        double rows = (hasResume ? 2 : 1) + 0.8;
        double tileMax = Math.Min(Math.Max(S(176), Math.Floor((viewH - rowsTop - fixedH) / rows)), S(220));
        int cols = Math.Max(3, (int)Math.Ceiling((content.Width + gap) / (tileMax + gap)));
        double tile = Math.Max(1, Math.Floor((content.Width - gap * (cols - 1)) / cols));

        double scroll = _scroll;
        var hero = new Rect(page.X, page.Y - scroll, page.Width, rowsTop + S(96));
        double textLeft = content.X, actionH = S(48), actionTop = rowsTop - S(56) - S(44) - actionH;
        if (heroArtist != null)
        {
            double p = _musicHeroSwitchAt < 0 ? 1 : Math.Clamp((Environment.TickCount64 - _musicHeroSwitchAt) / MusicHeroFadeMs, 0, 1);
            double e = p * p * (3 - 2 * p);
            if (p < 1 && _musicHeroFrom >= 0 && _musicHeroFrom < _musicHeroDeck.Count) DrawMusicHeroPhoto(ctx, _musicHeroDeck[_musicHeroFrom], hero, textLeft, 1);
            DrawMusicHeroPhoto(ctx, heroArtist, hero, textLeft, e);
            // Testo: il vecchio sparisce nella prima parte, il nuovo entra nella seconda. Mai sovrapposti.
            if (p < 1 && _musicHeroFrom >= 0 && _musicHeroFrom < _musicHeroDeck.Count)
            {
                double outA = 1 - Math.Clamp(p / .38, 0, 1), inA = Math.Clamp((p - .42) / .58, 0, 1);
                if (outA > .01) DrawMusicHeroText(ctx, _musicHeroDeck[_musicHeroFrom], hero, textLeft, hero.Y + actionTop, outA * outA * (3 - 2 * outA));
                if (inA > .01) DrawMusicHeroText(ctx, heroArtist, hero, textLeft, hero.Y + actionTop, inA * inA * (3 - 2 * inA));
            }
            else DrawMusicHeroText(ctx, heroArtist, hero, textLeft, hero.Y + actionTop, 1);
            double bx = DrawMusicHeroButton(ctx, textLeft, hero.Y + actionTop, actionH, "Riproduci", true, "music-hero-play", heroArtist) + S(14);
            DrawMusicHeroButton(ctx, bx, hero.Y + actionTop, actionH, "Scegli un brano", false, "music-hero-info", heroArtist);
        }
        else
        {
            var ft = Format("Musica", Math.Max(24, 50 * _s), DisplayMedium, Text);
            ctx.DrawText(ft, new Point(textLeft, hero.Y + actionTop - S(34) - ft.Height));
        }

        // ----- File: continua ad ascoltare, album, artisti -----
        double y = rowsTop - scroll;
        using var clip = ctx.PushClip(new Rect(0, bar, page.Width, Math.Max(1, viewBottom - bar)));
        if (hasResume)
        {
            TextAt(ctx, "Continua ad ascoltare", Math.Max(11, 15 * _s), DisplaySemibold, Text, new Rect(content.X, y, content.Width, titleH));
            y += titleH + titleGap;
            var listened = MusicListeningStore.Snapshot();
            for (int i = 0; i < Math.Min(cols, resume.Count); i++)
            {
                var album = resume[i].Album;
                var r = new Rect(content.X + i * (tile + gap), y, tile, tile + S(10) + titleLine + textLine);
                DrawMusicTile(ctx, r, album.Art, album.Title, album.Artist, "music-resume", album.Key, album.Tracks[0]);
                DrawResumeProgress(ctx, new Rect(r.X, r.Y, tile, tile), AlbumProgress(resume[i], listened));
            }
            y += tile + S(10) + titleLine + textLine + rowGap;
        }
        RowTitle(ctx, "Album", new Rect(content.X, y, content.Width, titleH), "music-see-albums");
        y += titleH + titleGap;
        var recentAlbums = albums.Take(cols).ToList();
        for (int i = 0; i < recentAlbums.Count; i++)
        {
            var r = new Rect(content.X + i * (tile + gap), y, tile, tile + S(10) + titleLine + textLine);
            DrawMusicTile(ctx, r, recentAlbums[i].Art, recentAlbums[i].Title, recentAlbums[i].Artist, "album", recentAlbums[i].Key, recentAlbums[i].Tracks[0]);
        }
        y += tile + S(10) + titleLine + textLine + rowGap;

        double artistD = Math.Max(1, Math.Round(tile * 0.72));
        RowTitle(ctx, "Artisti", new Rect(content.X, y, content.Width, titleH), "music-see-artists");
        y += titleH + titleGap;
        var topArtists = artists.Where(a => !IsVariousArtists(a.Name)).OrderByDescending(a => a.Tracks.Count).Take(cols).ToList();
        for (int i = 0; i < topArtists.Count; i++)
        {
            var cell = new Rect(content.X + i * (tile + gap), y, tile, artistD + S(12) + titleLine);
            DrawArtistCircle(ctx, cell, new Rect(cell.X + (cell.Width - artistD) / 2, cell.Y, artistD, artistD), topArtists[i]);
        }
        y += artistD + S(12) + titleLine + bottomPad;
        double contentH = y + scroll - page.Y;
        _scrollMax = Math.Max(0, contentH - viewH);
        _scroll = Math.Clamp(_scroll, 0, _scrollMax); _scrollTarget = Math.Clamp(_scrollTarget, 0, _scrollMax);
    }

    private readonly Dictionary<string, (object? Bmp, long At)> _heroPhotos = new(StringComparer.OrdinalIgnoreCase);
    private long _heroPhotoAt;

    private void DrawMusicHeroPhoto(DrawingContext ctx, ArtistGroup artist, Rect hero, double textLeft, double opacity)
    {
        string? path = MusicHeroImage(ArtistArtwork(artist));
        if (path == null || Image(path, hero.Width) is not { } bmp) return;
        // La foto arriva (o passa dalla misura piccola gia' in cache a quella giusta): dissolvenza invece dello scatto
        // che si vedeva entrando in Musica la prima volta.
        long nowMs = Environment.TickCount64;
        if (!_heroPhotos.TryGetValue(path, out var seen) || !ReferenceEquals(seen.Bmp, bmp))
        {
            // La prima volta che la foto si vede con il deck gia' in dissolvenza non serve un'altra dissolvenza.
            seen = (bmp, seen.Bmp != null || opacity < 1 ? nowMs - 1000 : nowMs);
            _heroPhotos[path] = seen;
            _heroPhotoAt = Math.Max(_heroPhotoAt, seen.At);
        }
        double arrive = Math.Clamp((nowMs - seen.At) / 320.0, 0, 1);
        if (arrive < 1) { opacity *= 0.35 + 0.65 * arrive * arrive * (3 - 2 * arrive); _frames.Start(); }
        using var fade = ctx.PushOpacity(opacity);
        using var clip = ctx.PushClip(hero);
        Color back = Back;
        ctx.FillRectangle(B(back), hero);
        bool landscape = bmp.Size.Width >= bmp.Size.Height * 1.3;
        // Foto larga: tutto il banner. Ritratto: la meta' destra, con il viso in alto.
        var area = landscape ? hero : new Rect(hero.X + hero.Width * 2 / 5, hero.Y, hero.Width - hero.Width * 2 / 5, hero.Height);
        double cover = Math.Max(area.Width / bmp.Size.Width, area.Height / bmp.Size.Height);
        double w = bmp.Size.Width * cover, h = bmp.Size.Height * cover;
        double top = landscape ? area.Y + (area.Height - h) / 3 : area.Y - (h - area.Height) * 0.18;
        using (ctx.PushClip(area)) ctx.DrawImage(bmp, new Rect(area.X + (area.Width - w) / 2, top, w, h));
        if (!landscape) ctx.FillRectangle(Horizontal(back, (0, 255), (1, 0)), new Rect(area.X - 1, area.Y, Math.Max(1, area.Width / 3), area.Height));
        double textW = Math.Min(S(720), Math.Max(S(340), Math.Round(hero.Width * 0.46)));
        double textEnd = Math.Clamp((textLeft + textW) / hero.Width, .25, .62);
        ctx.FillRectangle(Horizontal(back, (0, 235), (textEnd * .55, 190), (textEnd, 90), (Math.Min(.95, textEnd + .25), 0), (1, 0)), hero);
        ctx.FillRectangle(Vertical(back, (0, 170), (1, 0)), new Rect(hero.X, hero.Y, hero.Width, Math.Max(1, hero.Height / 4)));
        double bottomH = Math.Max(1, Math.Round(hero.Height * 0.42));
        ctx.FillRectangle(Vertical(back, (0, 0), (.4, 90), (.75, 205), (1, 255)), new Rect(hero.X, hero.Bottom - bottomH, hero.Width, bottomH + 1));
    }

    private void DrawMusicHeroText(DrawingContext ctx, ArtistGroup artist, Rect hero, double textLeft, double actionTop, double opacity)
    {
        double textW = Math.Min(S(720), Math.Max(S(340), Math.Round(hero.Width * 0.46)));
        var name = Format(artist.Name, Math.Max(22, 46 * _s), DisplayMedium, A(Text, 255 * opacity), textW);
        var line = Format(MusicHeroSummary(artist), Math.Max(10, 14 * _s), DisplayRegular, A(Text, (UiTheme.IsLight ? 210 : 220) * opacity), textW);
        double ty = actionTop - S(34) - S(14) - line.Height - name.Height;
        ctx.DrawText(name, new Point(textLeft, ty));
        ctx.DrawText(line, new Point(textLeft, ty + name.Height + S(14)));
    }

    private static string MusicHeroSummary(ArtistGroup artist)
    {
        int albums = artist.Tracks.Select(t => UnknownTag(t.AlbumTitle) ? (t.IsNetwork ? null : Path.GetFileName(Path.GetDirectoryName(t.Path))) : t.AlbumTitle)
            .Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int tracks = artist.Tracks.Count;
        double minutes = artist.Tracks.Sum(t => t.DurationMinutes ?? 0);
        string length = minutes >= 1 ? "   ·   " + (minutes >= 60 ? $"{(int)(minutes / 60)} h {(int)(minutes % 60)} min" : $"{(int)minutes} min") : "";
        return (albums > 0 ? (albums == 1 ? "1 album" : albums + " album") + "   ·   " : "") + (tracks == 1 ? "1 brano" : tracks + " brani") + length;
    }

    private double DrawMusicHeroButton(DrawingContext ctx, double x, double y, double h, string text, bool primary, string kind, ArtistGroup artist)
    {
        bool light = UiTheme.IsLight;
        double pt = Math.Max(9.4, 11.8 * _s);
        double tw = Measure(text, pt, DisplaySemibold);
        double glyph = primary ? S(13) : 0, glyphGap = primary ? S(10) : 0, pad = S(28);
        var r = new Rect(x, y, pad * 2 + glyph + glyphGap + tw, h);
        DrawHeroButtonShape(ctx, r, primary, _menu == null && Hover(r));
        Color ink = primary ? (light ? Colors.White : Colors.Black) : (light ? Text : Colors.White);
        double cx = r.X + pad;
        if (primary)
        {
            double gy = r.Y + (r.Height - glyph) / 2;
            ctx.DrawGeometry(B(ink), null, new PolylineGeometry(new[] { new Point(cx + 1, gy), new Point(cx + glyph, gy + glyph / 2), new Point(cx + 1, gy + glyph) }, true));
            cx += glyph + glyphGap;
        }
        TextAt(ctx, text, pt, DisplaySemibold, ink, new Rect(cx, r.Y, tw + 2, r.Height));
        _hits.Add(new Hit(r, kind, artist.Name, artist.Tracks.FirstOrDefault()));
        return r.Right;
    }

    private void RowTitle(DrawingContext ctx, string title, Rect row, string seeAll)
    {
        TextAt(ctx, title, Math.Max(11, 15 * _s), DisplaySemibold, Text, row);
        double pt = Math.Max(9, 11.4 * _s), tw = Measure("Vedi tutti", pt, DisplayRegular), chev = S(14);
        var r = new Rect(row.Right - tw - chev - S(6), row.Y, tw + chev + S(6), row.Height);
        Color ink = A(Text, Hover(r) ? 255 : 200);
        TextAt(ctx, "Vedi tutti", pt, DisplayRegular, ink, new Rect(r.X, r.Y, tw + 2, r.Height));
        Chevron(ctx, r.Right - chev / 2, r.Y + r.Height / 2, S(4), ink, Math.Max(1.4, S(1.6)), right: true);
        _hits.Add(new Hit(r, seeAll));
    }

    private void DrawMusicTile(DrawingContext ctx, Rect r, string? art, string title, string subtitle, string kind, string key, LibraryItem item)
    {
        var cover = new Rect(r.X, r.Y, r.Width, r.Width);
        double radius = S(6);
        using (ctx.PushGeometryClip(new RectangleGeometry(cover, radius, radius)))
        {
            if (Image(art, cover.Width) is { } bmp) DrawCover(ctx, bmp, cover);
            else DrawMusicPlaceholder(ctx, cover);
        }
        bool hover = Hover(r), light = UiTheme.IsLight;
        ctx.DrawRectangle(null, hover ? new Pen(B(light ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(215, 255, 255, 255)), 2)
            : new Pen(B(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255)), 1), cover.Deflate(hover ? 1 : .5), radius, radius);
        TextAt(ctx, title, Math.Max(9.4, 11.2 * _s), DisplaySemibold, Text, new Rect(r.X + 1, cover.Bottom + S(10), r.Width - 2, S(24)));
        TextAt(ctx, subtitle, Math.Max(8.4, 10 * _s), DisplayRegular, A(Text, 160), new Rect(r.X + 1, cover.Bottom + S(10) + S(24), r.Width - 2, S(20)));
        if (kind == "music-resume")
        {
            _hits.Add(new Hit(new Rect(r.X, cover.Bottom, r.Width, r.Height - cover.Height), "album", key, item));
            _hits.Add(new Hit(cover, kind, key, item));
        }
        else _hits.Add(new Hit(r, kind, key, item));
    }

    private void DrawArtistCircle(DrawingContext ctx, Rect cell, Rect circle, ArtistGroup artist)
    {
        string? portrait = ArtistArtwork(artist).Portrait;
        var shape = new EllipseGeometry(circle);
        bool drawn = false;
        using (ctx.PushGeometryClip(shape))
        {
            if (portrait != null && Image(portrait, circle.Width) is { } bmp) { DrawCover(ctx, bmp, circle); drawn = true; }
            else
            {
                ctx.FillRectangle(B(UiTheme.IsLight ? Color.FromRgb(226, 230, 235) : Color.FromRgb(24, 30, 37)), circle);
                string initials = string.Concat(artist.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(n => n[0])).ToUpperInvariant();
                TextAt(ctx, initials, Math.Max(14, circle.Width / 5 * 0.7), DisplaySemibold, A(Text, 150), circle, TextAlignment.Center);
            }
        }
        bool hover = Hover(cell);
        ctx.DrawGeometry(null, new Pen(B(hover ? A(Text, 215) : A(Text, drawn ? 18 : 30)), hover ? 2 : 1), new EllipseGeometry(circle.Deflate(hover ? 1 : .5)));
        TextAt(ctx, artist.Name, Math.Max(9.4, 11.2 * _s), DisplaySemibold, Text, new Rect(cell.X, circle.Bottom + S(12), cell.Width, S(24)), TextAlignment.Center);
        _hits.Add(new Hit(cell, "music-artist", artist.Name, artist.Tracks.FirstOrDefault()));
    }

    // ------------------------------------------------------------------ continua ad ascoltare (MusicListeningStore)
    /// <summary>Una voce per album, dalle posizioni d'ascolto vere; album finito (ultimo brano ascoltato fino in fondo) = tolto.</summary>
    private sealed record MusicResumeEntry(Album Album, LibraryItem Next, double Position, DateTime Updated);

    private static List<MusicResumeEntry> MusicResume(List<Album> albums, IReadOnlyDictionary<string, MusicListeningStore.Progress> history)
    {
        var entries = new List<MusicResumeEntry>();
        foreach (var album in albums)
        {
            var last = album.Tracks.Where(t => history.ContainsKey(t.Path)).MaxBy(t => history[t.Path].UpdatedUtc);
            if (last == null) continue;
            var saved = history[last.Path];
            int index = album.Tracks.IndexOf(last) + (saved.AtEnd ? 1 : 0);
            if (index < album.Tracks.Count)
                entries.Add(new(album, album.Tracks[index], saved.AtEnd ? 0 : saved.Position, saved.UpdatedUtc));
        }
        return entries.OrderByDescending(entry => entry.Updated).ToList();
    }

    /// <summary>Quanto dell'album si e' ascoltato: i brani prima di quello a cui si e' arrivati, piu' la posizione in quello.</summary>
    private static double AlbumProgress(MusicResumeEntry entry, IReadOnlyDictionary<string, MusicListeningStore.Progress> history)
    {
        double Length(LibraryItem t) => history.TryGetValue(t.Path, out var h) && h.Duration > 0 ? h.Duration : (t.DurationMinutes ?? 0) * 60;
        var tracks = entry.Album.Tracks;
        double total = tracks.Sum(Length);
        int index = Math.Max(0, tracks.IndexOf(entry.Next));
        double elapsed = tracks.Take(index).Sum(Length) + entry.Position;
        return total > 0 ? Math.Clamp(elapsed / total, 0, 1) : 0;
    }

    /// <summary>
    /// Avanzamento sulla copertina, come la versione Windows (DrawThinProgress): un filo bianco in fondo alla copertina,
    /// su un'ombra leggera che lo stacca da qualsiasi immagine.
    /// </summary>
    private void DrawResumeProgress(DrawingContext ctx, Rect cover, double progress)
    {
        double radius = S(6);
        using (ctx.PushGeometryClip(new RectangleGeometry(cover, radius, radius)))
        {
            var foot = new Rect(cover.X, cover.Bottom - S(34), cover.Width, S(34) + 1);
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(150, 0, 0, 0), 1) },
            }, foot);
        }
        double h = Math.Max(3, S(3));
        var track = new Rect(cover.X + S(10), cover.Bottom - S(12), Math.Max(1, cover.Width - S(20)), h);
        ctx.FillRectangle(B(Color.FromArgb(80, 255, 255, 255)), track, (float)(h / 2));
        if (progress > 0)
            ctx.FillRectangle(B(Color.FromArgb(245, 255, 255, 255)), new Rect(track.X, track.Y, Math.Max(h, track.Width * progress), h), (float)(h / 2));
    }

    private double _listenRecordedAt;

    /// <summary>Dal timer della musica: la posizione del brano finisce nello storico ogni 5 s e alla fine.</summary>
    private void RecordListening(bool ended)
    {
        if (!MusicBarVisible) return;
        double now = Environment.TickCount64 / 1000.0;
        if (!ended && now - _listenRecordedAt < 5) return;
        _listenRecordedAt = now;
        string path = _queue[_queueIndex].Path;
        double pos = ended ? _audio.Duration : _audio.Position, dur = _audio.Duration;
        if (dur <= 0 || pos < 1) return;
        _ = Task.Run(() => { try { MusicListeningStore.Record(path, pos, dur); } catch { } });
    }

    // ------------------------------------------------------------------ griglia completa (Album ⌄)
    private static readonly string[] MusicTabs = { "Album", "Artisti", "Brani", "Playlist" };

    /// <summary>Selettore della griglia, in alto a sinistra: "Album ⌄" con il menu delle quattro viste e "‹ Musica" per tornare.</summary>
    private double DrawMusicGridHeader(DrawingContext ctx, Rect content, double top)
    {
        double pt = Math.Max(12, 17 * _s), tw = Measure(MusicTabs[_musicTab], pt, DisplaySemibold);
        var kind = new Rect(content.X, top, tw + S(30), S(40));
        Color ink = A(Text, Hover(kind) ? 255 : 230);
        TextAt(ctx, MusicTabs[_musicTab], pt, DisplaySemibold, ink, new Rect(kind.X, kind.Y, tw + 2, kind.Height));
        Chevron(ctx, kind.X + tw + S(16), kind.Center.Y + S(1), S(5), ink, Math.Max(1.4, S(1.8)));
        _hits.Add(new Hit(kind, "music-kind"));
        if (_query.Trim().Length == 0)
        {
            double bpt = Math.Max(9, 11.4 * _s), bw = Measure("Panoramica", bpt, DisplayRegular);
            var back = new Rect(content.Right - bw - S(20), top, bw + S(20), S(40));
            TextAt(ctx, "Panoramica", bpt, DisplayRegular, A(Text, Hover(back) ? 255 : 190), back, TextAlignment.Right);
            _hits.Add(new Hit(back, "music-overview"));
        }
        return top + S(52);
    }

    private void OpenMusicKindMenu()
    {
        var hit = _hits.LastOrDefault(h => h.Kind == "music-kind");
        if (hit == null) return;
        var entries = MusicTabs.Select((label, i) => new MenuEntry(label, null, () =>
        {
            if (i == 3) { Navigate("Playlists"); return; }
            BeginPageTransition(); _musicTab = i; _scroll = _scrollTarget = 0; InvalidateVisual();
        }) { Checked = i == _musicTab }).ToList<MenuEntry>();
        ShowTextMenu(entries, new Point(hit.Bounds.X, hit.Bounds.Bottom + S(6)));
    }

    private void DrawArtistGrid(DrawingContext ctx, Rect viewport, IReadOnlyList<LibraryItem> tracks)
    {
        var artists = MusicMemo("artists-grid", tracks, null, tracks.Count, () => MusicArtists(tracks).Where(a => !IsVariousArtists(a.Name)).ToList());
        double gap = S(24);
        int columns = (int)Math.Clamp(Math.Floor((viewport.Width + gap) / Math.Max(1, S(170) + gap)), 3, 10);
        double cellW = Math.Max(1, Math.Floor((viewport.Width - gap * (columns - 1)) / columns));
        double d = Math.Round(cellW * 0.86), cellH = d + S(12) + S(24), rowGap = S(24);
        int rows = (artists.Count + columns - 1) / columns;
        double bottom = viewport.Bottom - MusicBarH;
        _scrollMax = Math.Max(0, rows * (cellH + rowGap) - rowGap + S(24) - (bottom - viewport.Y));
        _scroll = Math.Clamp(_scroll, 0, _scrollMax); _scrollTarget = Math.Clamp(_scrollTarget, 0, _scrollMax);
        using (ctx.PushClip(new Rect(0, viewport.Y - S(6), Bounds.Width, Math.Max(1, bottom - viewport.Y + S(6)))))
            for (int i = 0; i < artists.Count; i++)
            {
                double y = viewport.Y + i / columns * (cellH + rowGap) - _scroll;
                if (y + cellH < viewport.Y) continue;
                if (y > bottom) break;
                var cell = new Rect(viewport.X + i % columns * (cellW + gap), y, cellW, cellH);
                DrawArtistCircle(ctx, cell, new Rect(cell.X + (cell.Width - d) / 2, cell.Y, d, d), artists[i]);
            }
    }

    private void DrawTrackList(DrawingContext ctx, Rect viewport, IReadOnlyList<LibraryItem> tracks)
    {
        var list = MusicMemo("tracks-sorted", tracks, null, tracks.Count, () => tracks.OrderBy(t => AlbumArtist(t), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.AlbumTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => TrackNumber(t) ?? 999).ToList());
        double rowH = S(52), bottom = viewport.Bottom - MusicBarH;
        _scrollMax = Math.Max(0, list.Count * rowH + S(24) - (bottom - viewport.Y));
        _scroll = Math.Clamp(_scroll, 0, _scrollMax); _scrollTarget = Math.Clamp(_scrollTarget, 0, _scrollMax);
        double pt = Math.Max(9.4, 11.2 * _s), small = Math.Max(8.4, 10 * _s);
        using (ctx.PushClip(new Rect(0, viewport.Y, Bounds.Width, Math.Max(1, bottom - viewport.Y))))
            for (int i = Math.Max(0, (int)(_scroll / rowH)); i < list.Count; i++)
            {
                var row = new Rect(viewport.X, viewport.Y + i * rowH - _scroll, viewport.Width, rowH - S(4));
                if (row.Y > bottom) break;
                var track = list[i];
                LibraryStore.QueueMusicArtwork(track);
                bool current = MusicBarVisible && _queue[_queueIndex].Path == track.Path;
                if (Hover(row)) ctx.FillRectangle(B(A(Text, 14)), row, (float)S(6));
                var art = new Rect(row.X + S(8), row.Y + S(5), row.Height - S(10), row.Height - S(10));
                using (ctx.PushGeometryClip(new RectangleGeometry(art, S(3), S(3))))
                {
                    if (Image(track.ArtPath, art.Width) is { } bmp) DrawCover(ctx, bmp, art);
                    else DrawMusicPlaceholder(ctx, art);
                }
                double x = art.Right + S(14), w = row.Width * .42;
                TextAt(ctx, TrackTitle(track), pt, current ? DisplaySemibold : DisplayRegular, current ? UiTheme.Accent : Text, new Rect(x, row.Y, w, row.Height));
                TextAt(ctx, TrackArtist(track), small, DisplayRegular, A(Text, 160), new Rect(x + w + S(16), row.Y, row.Width * .24, row.Height));
                TextAt(ctx, UnknownTag(track.AlbumTitle) ? "" : track.AlbumTitle!, small, DisplayRegular, A(Text, 160), new Rect(x + w + row.Width * .24 + S(32), row.Y, row.Right - (x + w + row.Width * .24 + S(32)) - S(70), row.Height));
                if (track.DurationMinutes is > 0)
                    TextAt(ctx, TimeSpan.FromMinutes(track.DurationMinutes.Value).ToString(@"m\:ss", CultureInfo.InvariantCulture), small, DisplayRegular, A(Text, 150), new Rect(row.Right - S(60), row.Y, S(50), row.Height), TextAlignment.Right);
                _hits.Add(new Hit(row, "music-track", i.ToString(CultureInfo.InvariantCulture), track));
            }
        _trackListOrder = list;
    }

    private List<LibraryItem> _trackListOrder = new();

    /// <summary>La scheda dell'artista: come quella dell'album, con tutti i suoi brani.</summary>
    private void OpenArtist(string name)
    {
        var artist = MusicArtists(_items.Where(i => i.Category == "Music")).FirstOrDefault(a => a.Name == name);
        if (artist == null) return;
        var tracks = artist.Tracks.OrderBy(t => t.AlbumTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => TrackNumber(t) ?? 999).ToList();
        string? art = ArtistArtwork(artist).Portrait ?? tracks.Select(t => t.ArtPath).FirstOrDefault(a => a != null);
        if (art != null && tracks.All(t => t.ArtPath == null)) foreach (var t in tracks) t.ArtPath ??= art;
        _album = new Album("artist:" + artist.Name, artist.Name, MusicHeroSummary(artist), tracks);
        _albumOpenedAt = DateTime.UtcNow; _albumScroll = 0; _albumSelected = 0; _albumRecPage = 0; _albumRowArmed = false;
        _frames.Start();
        InvalidateVisual();
    }

    private bool MusicOverviewActivate(Hit hit)
    {
        switch (hit.Kind)
        {
            case "music-hero-play":
            {
                var artist = MusicArtists(_items.Where(i => i.Category == "Music")).FirstOrDefault(a => a.Name == hit.Key);
                if (artist != null) PlayQueue(artist.Tracks.OrderBy(t => t.AlbumTitle).ThenBy(t => TrackNumber(t) ?? 999).ToList(), 0);
                return true;
            }
            case "music-hero-info": case "music-artist": OpenArtist(hit.Key); return true;
            case "music-see-albums": BeginPageTransition(); _musicOverview = false; _musicTab = 0; _scroll = _scrollTarget = 0; InvalidateVisual(); return true;
            case "music-see-artists": BeginPageTransition(); _musicOverview = false; _musicTab = 1; _scroll = _scrollTarget = 0; InvalidateVisual(); return true;
            case "music-overview": BeginPageTransition(); _musicOverview = true; _filter = "All"; _scroll = _scrollTarget = 0; InvalidateVisual(); return true;
            case "music-kind": OpenMusicKindMenu(); return true;
            case "music-resume":
            {
                var album = Albums(_items.Where(i => i.Category == "Music")).FirstOrDefault(a => a.Key == hit.Key);
                if (album != null && MusicResume([album], MusicListeningStore.Snapshot()).FirstOrDefault() is { } resume)
                    PlayQueue(album.Tracks.ToList(), album.Tracks.IndexOf(resume.Next), resume.Position);
                return true;
            }
            case "music-track":
                if (int.TryParse(hit.Key, out int index) && index < _trackListOrder.Count) PlayQueue(_trackListOrder.ToList(), index);
                return true;
        }
        return false;
    }
}
