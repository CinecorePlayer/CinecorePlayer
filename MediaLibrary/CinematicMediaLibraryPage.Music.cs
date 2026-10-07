#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private int _musicTab, _musicResumeOffset, _musicArtworkIndex, _musicResumePageSize = 1;
        public event Action<IReadOnlyList<string>, int, double?>? MusicPlayRequested;

        private bool TryOpenMusicQueue(LibraryItem item, double? resumeSeconds)
        {
            if (item.Category != "Music" || item.IsGroup || MusicPlayRequested == null) return false;
            var album = _activeGroup ?? _items.FirstOrDefault(a => a.IsGroup && a.Children.Any(t => string.Equals(t.Path, item.Path, StringComparison.OrdinalIgnoreCase)));
            var tracks = album?.Children ?? new List<LibraryItem> { item };
            int index = tracks.FindIndex(t => string.Equals(t.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            if (index < 0) { tracks = new List<LibraryItem> { item }; index = 0; }
            MusicPlayRequested(tracks.Select(t => t.Path).ToList(), index, resumeSeconds);
            return true;
        }
        private const TextFormatFlags MusicText = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping;

        private int _musicSummaryWidth;

        private void DrawMusicCollection(Graphics g, Rectangle content, float scale)
        {
            // Gli elenchi (album, tutti i brani, artisti raggruppati) si ricalcolano solo quando
            // cambiano libreria, ricerca o filtro. Prima si rifacevano a ogni ridisegno fuori
            // dall'animazione, cioe' a ogni scatto della rotella: Artisti e Brani scattavano.
            // Un rinfresco periodico, a scorrimento fermo, raccoglie i metadati arrivati dopo.
            bool scrolling = _gridScrollAnimationTimer.Enabled;
            long nowMs = Environment.TickCount64;
            if (_gridScroll != _musicLastScroll) { _musicLastScroll = _gridScroll; _musicLastScrollMs = nowMs; }
            var stamp = (Items: (object)_items, Count: _items.Count, Query: (_searchBox.Text ?? string.Empty).Trim(), Filter: _filter);
            bool stale = !stamp.Equals(_musicCacheStamp) ||
                (!scrolling && nowMs - _musicCacheBuiltMs > 5000 && nowMs - _musicLastScrollMs > 1200);
            if (_musicAlbumsCache == null || stale)
            {
                _musicCacheStamp = stamp;
                _musicCacheBuiltMs = nowMs;
                _musicAlbumsCache = VisibleItems().ToList();
                _musicTracksCache = _musicAlbumsCache.SelectMany(a => a.IsGroup ? a.Children : new List<LibraryItem> { a }).ToList();
                _musicArtistsCache = null;
                _musicMinutesCache = _musicTracksCache.Sum(t => t.DurationMinutes ?? 0);
            }
            var albums = _musicAlbumsCache;
            var tracks = _musicTracksCache!;
            int top = content.Top + S(scale, 58);
            var banner = new Rectangle(content.Left, top, content.Width, Math.Max(128, S(scale, 144)));
            // Il banner resta fermo mentre la griglia scorre: se non e' nell'area da ridisegnare
            // si registrano solo le zone cliccabili delle schede.
            bool paintBanner = NeedsPaint(new Rectangle(content.Left, content.Top, content.Width, banner.Bottom - content.Top + 3));
            if (!paintBanner)
            {
                string[] quietTabs = { "Album", L("Artisti", "Artists"), L("Brani", "Tracks"), "Playlist" };
                for (int i = 0; i < quietTabs.Length; i++)
                    _hits.Add(new HitZone { Bounds = new Rectangle(banner.Left + S(scale, i * 106), banner.Bottom - S(scale, 34), S(scale, 96), S(scale, 34)), Kind = HitKind.MusicTab, Index = i });
            }
            else
            {
            var candidates = albums.Take(24).ToList();
            var artwork = candidates.Count == 0 ? null : candidates[_musicArtworkIndex % candidates.Count];
            if (artwork != null)
            {
                // Per ora l'header mostra solo il placeholder, non le foto degli artisti.
                string? bannerPath = MusicPlaceholderBackdrop();
                var art = new Rectangle(banner.Left + banner.Width / 4, banner.Top, banner.Width * 3 / 4, banner.Height);
                DrawImagePath(g, art, bannerPath, requireLandscape: true, tint: false, preserveAspectWhenWide: false, verticalFocus: .30f);
                if (bannerPath == null)
                {
                    using var ambience = new LinearGradientBrush(banner, Back, Color.FromArgb(14, 28, 38), 0f);
                    g.FillRectangle(ambience, banner);
                }
                using var shade = new LinearGradientBrush(banner, Back, Color.Transparent, LinearGradientMode.Horizontal);
                shade.InterpolationColors = new ColorBlend { Positions = new[] {0f, .28f, .5f, .8f, 1f}, Colors = new[] {Back, Back, Color.FromArgb(125, Back), Color.FromArgb(24, Back), Color.FromArgb(110, Back)} };
                g.FillRectangle(shade, banner);
                using var bottom = new LinearGradientBrush(banner, Color.Transparent, Back, LinearGradientMode.Vertical);
                bottom.InterpolationColors = new ColorBlend { Positions = new[] {0f, .55f, 1f}, Colors = new[] {Color.FromArgb(35, Back), Color.Transparent, Back} };
                g.FillRectangle(bottom, banner);
                // Rumore leggero: i gradienti lunghi su fondo scuro mostravano bande.
                VisualDither.Overlay(g, banner, HUD.Theme.IsLight ? 2 : 3);
            }
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(23, 29 * scale));
            TextRenderer.DrawText(g, L("Musica", "Music"), title, new Rectangle(banner.Left, banner.Top + S(scale, 8), banner.Width / 2, S(scale, 48)), TextMain, MusicText);
            double minutes = _musicMinutesCache;
            using var bannerText = LibraryFont("Segoe UI", Math.Max(9, 10 * scale));
            string summary = L($"{albums.Count} album · {tracks.Count} brani", $"{albums.Count} albums · {tracks.Count} tracks") + (minutes > 0 ? " · " + FormatDuration(minutes) : "");
            TextRenderer.DrawText(g, summary, bannerText, new Rectangle(banner.Left, banner.Top + S(scale, 59), banner.Width, S(scale, 23)), Muted, MusicText);
            _musicSummaryWidth = TextRenderer.MeasureText(g, summary, bannerText, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            string[] tabs = { "Album", L("Artisti", "Artists"), L("Brani", "Tracks"), "Playlist" };
            int tabY = banner.Bottom - S(scale, 34);
            using (var divider = new Pen(Color.FromArgb(42, 128, 161, 184))) g.DrawLine(divider, banner.Left, banner.Bottom, banner.Right, banner.Bottom);
            for (int i = 0; i < tabs.Length; i++)
            {
                var r = new Rectangle(banner.Left + S(scale, i * 106), tabY, S(scale, 96), S(scale, 34));
                TextRenderer.DrawText(g, tabs[i], bannerText, r, _musicTab == i ? TextMain : Muted, MusicText | TextFormatFlags.HorizontalCenter);
                if (_musicTab == i) { using var line = new Pen(Accent, 3); g.DrawLine(line, r.Left + 7, r.Bottom, r.Right - 7, r.Bottom); }
                _hits.Add(new HitZone { Bounds = r, Kind = HitKind.MusicTab, Index = i });
            }
            }
            DrawGlobalSearchLinks(g, banner.Left + _musicSummaryWidth + S(scale, 22), new Rectangle(banner.Left, banner.Top + S(scale, 59), banner.Width - S(scale, 80), S(scale, 23)), scale);
            using var normal = LibraryFont("Segoe UI", Math.Max(9, 10 * scale));
            using var bold = LibraryFont("Segoe UI Semibold", Math.Max(9, 10 * scale));
            int gridTop = banner.Bottom + S(scale, 18);
            if (_musicTab == 3)
            {
                var playlistContent = new Rectangle(content.Left, gridTop - S(scale, 48), content.Width, content.Bottom - gridTop + S(scale, 48));
                DrawPlaylistsCollectionPage(g, playlistContent, scale); return;
            }
            if (_musicTab == 0 && content.Bottom - gridTop > S(scale, 370))
                gridTop = DrawMusicResume(g, new Rectangle(content.Left, gridTop, content.Width, S(scale, 160)), albums, scale, normal, bold);

            var items = _musicTab == 2 ? tracks : _musicTab == 1
                ? (_musicArtistsCache ??= GroupMusicArtists(tracks))
                : albums;
            string label = _musicTab == 2 ? L("Tutti i brani", "All tracks") : _musicTab == 1 ? L("Tutti gli artisti", "All artists") : L("Tutti gli album", "All albums");
            if (items.Count > 0) DrawTitle(g, label, new Rectangle(content.Left, gridTop, content.Width, S(scale, 30)), 12 * scale);
            gridTop += S(scale, 39);
            var viewport = new Rectangle(content.Left, gridTop, content.Width - S(scale, 64), Math.Max(1, content.Bottom - gridTop - S(scale, 18)));
            if (items.Count == 0) { _gridScroll = _gridScrollMax = 0; DrawEmptyGrid(g, viewport); return; }
            DrawMusicGrid(g, viewport, items, scale, normal, bold);
        }

        private readonly Dictionary<string, DateTime> _musicBannerAttempts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _musicBannerPaths = new(StringComparer.OrdinalIgnoreCase);
        private static string? MusicPlaceholderBackdrop()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Music", "music-placeholder.jpg");
            return File.Exists(path) ? path : null;
        }
        private static string? AlbumArtworkArtist(LibraryItem album) => album.Children.SelectMany(t => MusicArtists(t.AlbumArtist))
            .Concat(album.Children.SelectMany(t => MusicArtists(t.ArtistName,t.AlbumArtist))).Concat(MusicArtists(album.AlbumArtist)).Concat(MusicArtists(album.ArtistName))
            .Concat(MusicArtists(InferAlbumInfo(album.Children.FirstOrDefault()?.Path ?? album.Path).Artist)).FirstOrDefault();
        private static string MusicBannerKey(LibraryItem album) => album.Path + "|" + MusicArtistArtworkService.Identity(AlbumArtworkArtist(album)) + "|" + MusicArtistArtworkService.Identity(album.Title);
        private string? MusicBannerArtwork(LibraryItem album)
        {
            string? artist = AlbumArtworkArtist(album);
            string identity = MusicBannerKey(album);
            if (_musicBannerPaths.TryGetValue(identity, out var cached))
            {
                if (cached != null && File.Exists(cached)) return cached;
                if (_musicBannerAttempts.TryGetValue(identity, out var lastAttempt) &&
                    DateTime.UtcNow - lastAttempt < TimeSpan.FromMinutes(15)) return MusicPlaceholderBackdrop();
            }
            string? result = null;
            try
            {
                string? folder = Path.GetDirectoryName(album.Children.FirstOrDefault()?.Path ?? album.Path);
                var candidates = new List<string?> { album.WideArtPath };
                if (folder != null)
                    foreach (string name in new[] { "fanart", "backdrop", "banner", "background" })
                        foreach (string ext in new[] { ".jpg", ".png", ".webp" }) candidates.Add(Path.Combine(folder, name + ext));
                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
                    if (string.Equals(candidate, album.ArtPath, StringComparison.OrdinalIgnoreCase) ||
                        album.Children.Any(track => string.Equals(candidate, track.ArtPath, StringComparison.OrdinalIgnoreCase))) continue;
                    using var img = Image.FromFile(candidate!);
                    if (MusicLandscapeArtworkService.IsLandscape(img.Width, img.Height)) { result = candidate; break; }
                }
            }
            catch { }
            if (result == null && !string.IsNullOrWhiteSpace(artist))
            {
                _musicBannerAttempts[identity] = DateTime.UtcNow;
                string title = album.Title;
                int providerVersion = _artistArtworkProviderVersion;
                _ = ResolveLandscape();
                async System.Threading.Tasks.Task ResolveLandscape()
                {
                    var remote = await MusicLandscapeArtworkService.ResolveAsync(title, artist).ConfigureAwait(false);
                    if (IsDisposed || !IsHandleCreated) return;
                    try { BeginInvoke(new Action(() => { if (!IsDisposed && providerVersion == _artistArtworkProviderVersion && remote != null) { _musicBannerPaths[identity] = remote; Invalidate(); } })); } catch { }
                }
            }
            else if (result == null)
                _musicBannerAttempts[identity] = DateTime.UtcNow;
            _musicBannerPaths[identity] = result;
            return result ?? MusicPlaceholderBackdrop();
        }

        private List<LibraryItem>? _musicAlbumsCache, _musicTracksCache, _musicArtistsCache;
        private (object Items, int Count, string Query, string Filter) _musicCacheStamp;
        private long _musicCacheBuiltMs, _musicLastScrollMs;
        private int _musicLastScroll = -1;
        private double _musicMinutesCache;

        private int DrawMusicResume(Graphics g, Rectangle area, List<LibraryItem> albums, float scale, Font normal, Font bold)
        {
            var history = MusicListeningStore.Snapshot();
            // Merge older resume data; keep completed tracks in the dedicated music history.
            foreach (var r in _resumeItems.Where(r => r.Item.Category == "Music"))
                if (!history.ContainsKey(r.Item.Path)) history[r.Item.Path] = new MusicListeningStore.Progress(r.PositionSeconds, r.DurationSeconds, r.SavedAt.ToUniversalTime());
            // One entry per album, from real saved listening positions.
            var recent = albums.Where(a => a.Children.Any(t => history.ContainsKey(t.Path)))
                .Select(album =>
                {
                    var last = album.Children.Where(t => history.ContainsKey(t.Path)).OrderByDescending(t => history[t.Path].UpdatedUtc).First();
                    var saved = history[last.Path];
                    bool completed = saved.Duration > 0 && saved.Position >= saved.Duration * .98;
                    int index = album.Children.IndexOf(last);
                    var next = completed ? album.Children.Skip(index + 1).FirstOrDefault() ?? last : last;
                    var resume = new ResumeItem { Item = next, PositionSeconds = completed ? 0 : saved.Position, DurationSeconds = next.DurationMinutes.GetValueOrDefault() * 60, SavedAt = saved.UpdatedUtc };
                    return (Resume: resume, Album: album, Updated: saved.UpdatedUtc);
                }).OrderByDescending(x => x.Updated).ToList();
            if (recent.Count == 0) return area.Top;
            DrawTitle(g, L("Continua ad ascoltare", "Continue listening"), new Rectangle(area.Left, area.Top, area.Width, S(scale, 28)), 12 * scale);
            int gap = S(scale, 16), side = S(scale, 32);
            int count = Math.Max(1, (area.Width - side * 2 + gap) / (S(scale, 290) + gap));
            _musicResumePageSize = count;
            _musicResumeOffset = Math.Clamp(_musicResumeOffset, 0, Math.Max(0, (recent.Count - 1) / count * count));
            int rowHeight = Math.Max(112, S(scale, 108));
            int width = (area.Width - side * 2 - gap * (count - 1)) / count;
            for (int i = 0; i < count && i + _musicResumeOffset < recent.Count; i++)
            {
                var entry = recent[i + _musicResumeOffset];
                var r = new Rectangle(area.Left + side + i * (width + gap), area.Top + S(scale, 37), width, rowHeight);
                var cover = new Rectangle(r.Left + 8, r.Top + 8, r.Height - 16, r.Height - 16);
                if (!DrawPosterArt(g, cover, entry.Album!)) DrawGeneratedArt(g, cover, entry.Album!.Title, false, category: "Music");
                int left = cover.Right + S(scale, 14), textW = Math.Max(20, r.Right - left - 12);
                TextRenderer.DrawText(g, entry.Album!.Title, bold, new Rectangle(left, r.Top + 10, textW, S(scale, 32)), TextMain, MusicText);
                // Artista: tag della traccia, poi quelli dell'album, poi la cartella (es. cartella AURORA/UNRELASED).
                string resumeArtist = FirstRawNonEmpty(entry.Resume.Item.ArtistName, AlbumArtworkArtist(entry.Album), entry.Album.GroupSubtitle, InferAlbumInfo(entry.Resume.Item.Path).Artist);
                TextRenderer.DrawText(g, resumeArtist, normal, new Rectangle(left, r.Top + 43, textW, S(scale, 24)), Muted, MusicText);
                var play = new Rectangle(r.Right - 42, r.Bottom - 48, 32, 32);
                using var playFill = new SolidBrush(Color.FromArgb(play.Contains(_lastMouse) ? 60 : 34, 255, 255, 255)); g.FillEllipse(playFill, play);
                DrawIcon(g, Rectangle.Inflate(play, -8, -8), "play", TextMain);
                var albumTracks = entry.Album.Children;
                int listened = albumTracks.Count(t => history.TryGetValue(t.Path, out var h) && h.Position >= Math.Min(30, h.Duration * .5));
                double total = albumTracks.Sum(t => history.TryGetValue(t.Path, out var h) ? Math.Max(h.Duration, (t.DurationMinutes ?? 0) * 60) : (t.DurationMinutes ?? 0) * 60);
                double elapsed = albumTracks.Sum(t => history.TryGetValue(t.Path, out var h) ? h.Position : 0);
                double progressValue = total > 0 ? Math.Clamp(elapsed / total, 0, 1) : 0;
                int lineY = r.Bottom - 37, lineEnd = Math.Max(left, play.Left - 12);
                using var line = new Pen(Color.FromArgb(47, 67, 81), 2); g.DrawLine(line, left, lineY, lineEnd, lineY);
                using var progress = new Pen(Accent, 2); g.DrawLine(progress, left, lineY, left + (int)((lineEnd - left) * progressValue), lineY);
                using var small = LibraryFont("Segoe UI", Math.Max(8, 8.3f * scale));
                string remaining = total > 0 ? L($" · {Math.Ceiling(Math.Max(0, total - elapsed) / 60)} min", $" · {Math.Ceiling(Math.Max(0, total - elapsed) / 60)} min left") : "";
                TextRenderer.DrawText(g, L($"{listened}/{albumTracks.Count} brani", $"{listened}/{albumTracks.Count} tracks") + remaining, small, new Rectangle(left, r.Bottom - 27, Math.Max(1, r.Right - left - 8), 23), Muted, MusicText);
                _hits.Add(new HitZone { Bounds = r, Kind = HitKind.Poster, Item = entry.Album });
                _hits.Add(new HitZone { Bounds = play, Kind = HitKind.Continue, Resume = entry.Resume });
            }
            void Arrow(bool left, bool enabled)
            {
                var r = new Rectangle(left ? area.Left : area.Right - side, area.Top + S(scale, 76), side, side);
                DrawArrow(g, r, left, enabled, left ? HitKind.MusicResumePrevious : HitKind.MusicResumeNext);
            }
            Arrow(true, _musicResumeOffset > 0); Arrow(false, _musicResumeOffset + count < recent.Count);
            int pages = Math.Max(1, (recent.Count + count - 1) / count);
            int dotY = area.Top + S(scale, 37) + rowHeight + S(scale, 24);
            int first = Math.Max(0, Math.Min(_musicResumeOffset / count - 5, pages - 11));
            int shown = Math.Min(11, pages);
            for (int page = first; page < first + shown; page++)
            {
                var dot = new Rectangle(area.Left + area.Width / 2 - shown * 9 + (page - first) * 18, dotY, 6, 6);
                using var fill = new SolidBrush(page == _musicResumeOffset / count ? Accent : Color.FromArgb(63, 80, 98));
                g.FillEllipse(fill, dot);
                _hits.Add(new HitZone { Bounds = Rectangle.Inflate(dot, 5, 5), Kind = HitKind.MusicResumePage, Index = page * count });
            }
            return dotY + S(scale, 28);
        }

        private void DrawMusicGrid(Graphics g, Rectangle viewport, List<LibraryItem> items, float scale, Font normal, Font bold)
        {
            _gridViewport = viewport; // lo scorrimento ridisegna solo la griglia, non banner e sidebar
            int gap = S(scale, 24), columns = Math.Max(1, (viewport.Width + gap) / (Math.Max(140, S(scale, 165)) + gap));
            int w = (viewport.Width - gap * (columns - 1)) / columns, h = w + S(scale, 78), stride = h + S(scale, 16);
            int rows = (items.Count + columns - 1) / columns, fullH = Math.Max(0, rows * stride - S(scale, 16));
            _gridScrollMax = Math.Max(0, fullH - viewport.Height); _gridScroll = Math.Clamp(_gridScroll, 0, _gridScrollMax);
            using var oldClip = g.Clip.Clone(); g.SetClip(viewport, CombineMode.Intersect);
            for (int i = Math.Max(0, _gridScroll / stride * columns); i < items.Count; i++)
            {
                int y = viewport.Top + i / columns * stride - _gridScroll; if (y >= viewport.Bottom) break;
                var r = new Rectangle(viewport.Left + i % columns * (w + gap), y, w, h);
                var art = new Rectangle(r.Left, r.Top, w, w); var item = items[i];
                string subtitle = _musicTab == 1 ? item.GroupSubtitle ?? "" : item.ArtistName ?? item.GroupSubtitle ?? InferAlbumInfo(item.Path).Artist;
                string format = _musicTab == 1 ? string.Empty : Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant();
                string metaLine = string.Join(" · ", new[] { _musicTab == 1 ? null : item.Year?.ToString(), format }.Where(v => !string.IsNullOrWhiteSpace(v)));
                string? portrait = _musicTab == 1 ? ArtistArtwork(item.Title, item.Path).Portrait : null;
                // Solo la copertina e' composta una volta in bitmap (gia' ritagliata): il testo resta
                // disegnato dal vivo in ClearType, nitido come nel resto dell'interfaccia.
                string tileKey = $"m|{_musicTab}|{item.ArtPath}|{portrait}|{w}|{(_musicTab == 1 ? item.Title : string.Empty)}";
                Bitmap? tile = GetPosterCardBitmap(tileKey, new Size(w, w), local =>
                {
                    var lArt = new Rectangle(0, 0, w, w);
                    using var cg = Graphics.FromImage(local);
                    cg.SmoothingMode = SmoothingMode.AntiAlias;
                    cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    bool drawn;
                    if (_musicTab == 1)
                    {
                        drawn = portrait != null && DrawImagePath(cg, lArt, portrait, requireLandscape: false, tint: false, preserveAspectWhenWide: false);
                        if (!drawn)
                        {
                            using var fill = new SolidBrush(Color.FromArgb(20, 27, 32)); cg.FillEllipse(fill, lArt);
                            using var initialsFont = LibraryFont("Segoe UI Semibold", Math.Max(22, w / 5));
                            CardText(cg, string.Concat(item.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(n => n[0])).ToUpperInvariant(), initialsFont, lArt, Muted, StringAlignment.Center);
                        }
                    }
                    else
                    {
                        drawn = DrawPosterArt(cg, lArt, item);
                        if (!drawn) DrawGeneratedArt(cg, lArt, item.Title, false, category: "Music");
                    }
                    using (var maskPath = Round(lArt, _musicTab == 1 ? w / 2 : 5))
                        ApplyRoundedAlphaMask(local, maskPath);
                    string? source = _musicTab == 1 ? portrait : item.ArtPath;
                    return drawn && IsDisplayImageExact(ResolveDisplayImagePath(source), w + 4);
                });
                if (tile != null)
                {
                    var mode = g.InterpolationMode; var offset = g.PixelOffsetMode;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(tile, r.X, r.Y, w, w);
                    g.InterpolationMode = mode; g.PixelOffsetMode = offset;
                }
                else
                {
                    using (var shape = Round(art, _musicTab == 1 ? w / 2 : 5))
                    {
                        g.SetClip(shape, CombineMode.Intersect);
                        if (_musicTab == 1)
                        {
                            if (portrait == null || !DrawImagePath(g, art, portrait, requireLandscape: false, tint: false, preserveAspectWhenWide: false))
                            {
                                using var fill = new SolidBrush(Color.FromArgb(20,27,32)); g.FillEllipse(fill, art);
                                using var initials = LibraryFont("Segoe UI Semibold", Math.Max(22,w/5));
                                TextRenderer.DrawText(g, string.Concat(item.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(n => n[0])).ToUpperInvariant(), initials, art, Muted, MusicText | TextFormatFlags.HorizontalCenter);
                            }
                        }
                        else if (!DrawPosterArt(g, art, item)) DrawGeneratedArt(g, art, item.Title, false, category: "Music");
                        g.Clip = oldClip; g.SetClip(viewport, CombineMode.Intersect); SmoothClipEdge(g, shape, Back);
                    }
                }
                TextRenderer.DrawText(g, item.Title, bold, new Rectangle(r.Left, art.Bottom + 7, w, S(scale, 24)), TextMain, MusicText);
                TextRenderer.DrawText(g, subtitle, normal, new Rectangle(r.Left, art.Bottom + S(scale, 31), w, S(scale, 22)), Muted, MusicText);
                TextRenderer.DrawText(g, metaLine, normal, new Rectangle(r.Left, art.Bottom + S(scale, 54), w, S(scale, 22)), Muted, MusicText);
                if (r.Contains(_lastMouse))
                    using (var hoverShape = Round(art, _musicTab == 1 ? w / 2 : 5))
                    using (var veil = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                    { var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias; g.FillPath(veil, hoverShape); g.SmoothingMode = mode; }
                var hit = Rectangle.Intersect(r, viewport);
                _hits.Add(new HitZone { Bounds = hit, Kind = HitKind.Poster, Item = item, Key = item.Path });
                if (_musicTab != 1)
                {
                    DrawMusicQuickActions(g, art, item, scale, r.Contains(_lastMouse));
                }
            }
            g.Clip = oldClip;
            DrawGridScrollbar(g, viewport, fullH);
        }
    }
}
