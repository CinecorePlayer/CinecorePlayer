using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// "Potrebbe piacerti" della scheda album (AlbumSheet di Windows): prima gli altri album dello stesso artista che hai,
// poi quelli degli artisti simili secondo Last.fm (artist.getSimilar) presenti in libreria, infine i dischi piu'
// ascoltati degli artisti simili che non hai (copertine di Last.fm; aprono la pagina). Niente consigli inventati.
internal sealed partial class LibraryView
{
    private bool _recsPollQueued;
    private sealed record AlbumRec(string Title, string Artist, Album? Album, string? ImageUrl, string? Url);
    private string? _albumRecsKey;
    private List<AlbumRec> _albumRecs = new();
    private bool _lastFmHooked;

    internal string RecommendationsState => _album is { } album
        ? $"artist={RecommendationArtist(album)} items={AlbumRecsFor(album).Count}" : "closed";

    private static string RecommendationArtist(Album album) => IsArtistSheet(album) ? album.Title : album.Artist;

    private List<AlbumRec> AlbumRecsFor(Album album)
    {
        if (!_lastFmHooked)
        {
            _lastFmHooked = true;
            LastFmSimilar.ImageReady += () => Dispatcher.UIThread.Post(() => { if (_album != null) { _albumRecsKey = null; InvalidateVisual(); } });
        }
        bool artistSheet = IsArtistSheet(album);
        string artist = RecommendationArtist(album);
        var similar = artist.Length > 0 ? LastFmSimilar.TryGetSimilar(artist) : Array.Empty<LastFmSimilar.SimilarArtist>();
        string key = album.Key + "|" + (similar == null ? "wait" : similar.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_albumRecsKey == key) return _albumRecs;
        static string Id(string? s) => MusicArtistArtworkService.Identity(s ?? "");
        var pool = Albums(_items.Where(i => i.Category == "Music")).Where(a => a.Key != album.Key).ToList();
        var recs = new List<AlbumRec>();
        string self = Id(artist);
        foreach (var other in pool.Where(a => !artistSheet && self.Length > 0 && Id(a.Artist) == self).Take(3))
            recs.Add(new AlbumRec(other.Title, other.Artist, other, null, null));
        bool pending = similar == null;
        if (similar != null)
        {
            var byArtist = pool.GroupBy(a => Id(a.Artist)).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var s in similar)
                if (byArtist.TryGetValue(Id(s.Name), out var albums))
                    foreach (var a in albums.Take(2))
                        if (!recs.Any(r => r.Album == a)) recs.Add(new AlbumRec(a.Title, a.Artist, a, null, null));
            foreach (var s in similar.Take(12))
            {
                if (recs.Count >= 12) break;
                if (byArtist.ContainsKey(Id(s.Name))) continue;
                var top = LastFmSimilar.TryGetTopAlbums(s.Name);
                if (top == null) pending = true;
                if (top?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ImageUrl)) is { } pick) recs.Add(new AlbumRec(pick.Title, s.Name, null, pick.ImageUrl, pick.Url));
            }
        }
        _albumRecs = recs;
        _albumRecsKey = pending ? null : key;
        // Le risposte di Last.fm arrivano poco alla volta: si ricalcola fra poco finche' non sono tutte.
        if (pending && !_recsPollQueued)
        {
            _recsPollQueued = true; // uno alla volta (si accumulavano a ogni disegno)
            DispatcherTimer.RunOnce(() => { _recsPollQueued = false; if (_album != null) InvalidateVisual(); }, TimeSpan.FromMilliseconds(400));
        }
        return recs;
    }

    private void DrawAlbumRecs(DrawingContext ctx, Rect area, Album album, double k)
    {
        double K(double v) => Math.Round(v * k);
        double Pt(double px) => Math.Max(7, px * .75);
        TextAt(ctx, "Potrebbe piacerti", Pt(Math.Max(14, K(26))), DisplaySemibold, Text, new Rect(area.X, area.Y, area.Width, K(40)));
        var recs = AlbumRecsFor(album);
        if (recs.Count == 0)
        {
            // Niente consigli inventati, ma si dice perche': in attesa, senza connessione o nessun risultato.
            bool waiting = _albumRecsKey == null && !LastFmSimilar.Unreachable;
            bool offline = !waiting && LastFmSimilar.Unreachable;
            string title = waiting ? "Cerco qualcosa che potrebbe piacerti…" : offline ? "Consigli non disponibili"
                : IsArtistSheet(album) ? "Nessun consiglio per questo artista" : "Nessun consiglio per questo album";
            string body = waiting ? "" : offline
                ? "Per i consigli serve la connessione a Internet: si aggiornano da soli appena torna."
                : "Last.fm non conosce artisti simili a questo e in libreria non ci sono altri suoi album.";
            var block = new Rect(area.X, area.Y + K(58), area.Width, Math.Max(1, area.Height - K(58)));
            double y = block.Y + Math.Max(0, (block.Height - K(110)) / 2);
            TextAt(ctx, title, Pt(Math.Max(12, K(20))), DisplaySemibold, waiting ? A(Text, 200) : Text, new Rect(block.X, y, block.Width, K(32)), TextAlignment.Center);
            if (body.Length > 0)
                ctx.DrawText(Format(body, Pt(Math.Max(10, K(16))), DisplayRegular, A(Text, 185), block.Width - K(40), 3, TextAlignment.Center), new Point(block.X + K(20), y + K(42)));
            return;
        }
        // Due file da tre quando c'e' posto; una fila sola solo se non c'e' davvero spazio.
        int columns = 3;
        double gap = K(26);
        double widthTile = (area.Width - gap * (columns - 1)) / columns;
        int rows = (area.Height - K(58) - 2 * K(70) - gap) / 2 >= K(80) ? 2 : 1;
        int perPage = columns * rows;
        int pages = (recs.Count + perPage - 1) / perPage;
        double namePt = Pt(Math.Max(10, K(17)));
        if (pages > 1)
        {
            const string more = "Vedi altri";
            double morePt = Pt(Math.Max(11, K(18)));
            double mw = Measure(more, morePt, DisplayRegular);
            var moreRect = new Rect(area.Right - mw - K(20), area.Y, mw + K(20), K(40));
            bool hot = Hover(moreRect);
            Color ink = hot ? Text : A(Text, 225);
            TextAt(ctx, more, morePt, DisplayRegular, ink, new Rect(moreRect.X, moreRect.Y, mw + 2, moreRect.Height));
            var pen = new Pen(B(ink), Math.Max(1.4, K(2)), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            double ax = moreRect.Right - K(6), ay = moreRect.Center.Y, d = K(5);
            ctx.DrawLine(pen, new Point(ax - d, ay - d), new Point(ax, ay));
            ctx.DrawLine(pen, new Point(ax, ay), new Point(ax - d, ay + d));
            _hits.Add(new Hit(moreRect, "album-rec-more"));
        }
        int page = pages == 0 ? 0 : _albumRecPage % pages;
        double tile = Math.Min(widthTile, (area.Height - K(58) - rows * K(70) - (rows - 1) * gap) / rows);
        if (tile < K(60)) return;
        double top = area.Y + K(58);
        foreach (var (rec, i) in recs.Skip(page * perPage).Take(perPage).Select((r, i) => (r, i)))
        {
            var art = new Rect(area.X + (i % columns) * (tile + gap), top + (i / columns) * (tile + K(70) + gap), tile, tile);
            var hitBox = new Rect(art.X, art.Y, art.Width, art.Height + K(66));
            bool hover = Hover(hitBox);
            using (ctx.PushClip(new RoundedRect(art, K(10))))
            {
                string? image = rec.Album?.Art ?? LastFmSimilar.CachedImage(rec.ImageUrl);
                if (Image(image, tile) is { } bmp) DrawCover(ctx, bmp, art); else DrawMusicPlaceholder(ctx, art);
                if (hover) ctx.FillRectangle(B(Color.FromArgb(26, 255, 255, 255)), art);
            }
            TextAt(ctx, rec.Title, namePt, DisplayRegular, hover ? Text : A(Text, 235), new Rect(art.X, art.Bottom + K(12), tile, K(26)));
            TextAt(ctx, rec.Artist, namePt, DisplayRegular, A(Text, 160), new Rect(art.X, art.Bottom + K(40), tile, K(24)));
            if (rec.Album != null) _hits.Add(new Hit(hitBox, "album", rec.Album.Key, rec.Album.Tracks[0]));
            else if (rec.Url != null) _hits.Add(new Hit(hitBox, "album-rec-web", rec.Url));
        }
    }

    private static void OpenRecPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; psi.ArgumentList.Add(uri.AbsoluteUri); Process.Start(psi)?.Dispose(); } catch { }
    }
}
