#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private void DrawCollectionPage(Graphics g, Rectangle content, float scale)
        {
            if (_category == "Music") { DrawMusicCollection(g, content, scale); return; }
            if (string.Equals(_category, "Playlists", StringComparison.OrdinalIgnoreCase))
            {
                DrawPlaylistsCollectionPage(g, content, scale);
                return;
            }

            var visible = VisibleItems().ToList();
            int titleY = content.Top + S(scale, 55);
            int gridTop = titleY + Math.Max(74, S(scale, 74));
            if (visible.Count == 0)
            {
                _gridScroll = 0;
                _gridScrollMax = 0;
                DrawEmptyGrid(g, CollectionEmptyViewport(content, scale));
                DrawCollectionHeader(g, content, visible.Count, titleY, gridTop, scale);
                return;
            }

            int scrollbarGutter = S(scale, 68);
            Rectangle grid = new Rectangle(content.Left, gridTop, Math.Max(1, content.Width - scrollbarGutter), Math.Max(1, content.Bottom - gridTop - S(scale, 24)));
            DrawCollectionGrid(g, grid, visible, scale);
            DrawCollectionHeader(g, content, visible.Count, titleY, gridTop, scale);
        }

        private Rectangle CollectionEmptyViewport(Rectangle content, float scale)
        {
            int bodyTop = content.Top + S(scale, 55 + 74);
            return new Rectangle(content.Left, bodyTop, content.Width,
                Math.Max(1, content.Bottom - bodyTop - S(scale, 24)));
        }

        private void DrawCollectionHeader(Graphics g, Rectangle content, int visibleCount, int titleY, int gridTop, float scale)
        {
            // Clip hit regions too: the opaque title band must not activate scrolled cards.
            foreach (var hit in _hits.Where(hit => hit.Kind is HitKind.Poster or HitKind.PosterPlaylist or HitKind.PlaylistSelect or HitKind.MusicAlbumQueue))
                hit.Bounds = Rectangle.Intersect(hit.Bounds, new Rectangle(content.Left, gridTop, content.Width, Math.Max(1, content.Bottom - gridTop)));
            // Paint the header last and on an opaque band. This is a defensive boundary
            // against cards being drawn above the viewport during animated/high-DPI scroll.
            using (var band = new SolidBrush(Back))
                g.FillRectangle(band, new Rectangle(content.Left, titleY - S(scale, 7), content.Width, Math.Max(1, gridTop - titleY + S(scale, 3))));

            int titleH = Math.Max(30, S(scale, 38));
            using (var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(19f, 24f * scale)))
                TextRenderer.DrawText(g, GridTitle(), titleFont, new Rectangle(content.Left, titleY, content.Width / 2, titleH), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            using var countFont = LibraryFont("Segoe UI", Math.Max(8.8f, 10.2f * scale));
            string label = CollectionCountLabel(visibleCount, TotalCountForVisibleLabel(visibleCount));
            if (_category == "Music" && !string.IsNullOrWhiteSpace(LyricsLibraryStatus))
            {
                using var statusFont = LibraryFont("Segoe UI", Math.Max(9.5f, 10f * scale));
                int statusW = Math.Min(S(scale, 510), content.Width * 2 / 3);
                var status = new Rectangle(content.Right - statusW - S(scale, 104), titleY + S(scale, 9), statusW, S(scale, 38));
                using var shape = Round(status, S(scale, 8));
                using var fill = new SolidBrush(HUD.Theme.Card);
                g.FillPath(fill, shape);
                status.Inflate(-S(scale, 16), 0);
                TextRenderer.DrawText(g, LyricsLibraryStatus, statusFont, status, HUD.Theme.Accent,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            var countRow = new Rectangle(content.Left, titleY + titleH + 4, content.Width / (_category == "Music" ? 3 : 2) - S(scale, 12), Math.Max(20, S(scale, 22)));
            TextRenderer.DrawText(g, label, countFont, countRow, Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            int labelW = TextRenderer.MeasureText(g, label, countFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            DrawGlobalSearchLinks(g, countRow.Left + labelW + S(scale, 22), new Rectangle(countRow.Left, countRow.Top, content.Width - S(scale, 80), countRow.Height), scale);
        }

        private void DrawPlaylistsCollectionPage(Graphics g, Rectangle content, float scale)
        {
            var playlists = BuildPlaylistViewItems().Where(p => _category != "Music" || (p.Bucket == "Music" && p.Items.All(i => i.Category == "Music"))).ToList();
            _playlistSelectionIndex = Math.Max(0, Math.Min(_playlistSelectionIndex, Math.Max(0, playlists.Count - 1)));

            int titleY = content.Top + S(scale, 55);
            using (var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(19f, 24f * scale)))
                TextRenderer.DrawText(g, L("Playlist", "Playlists"), titleFont, new Rectangle(content.Left, titleY, content.Width / 2, S(scale, 38)), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            using (var countFont = LibraryFont("Segoe UI", Math.Max(8.8f, 10.2f * scale)))
                TextRenderer.DrawText(g, L($"{playlists.Count:N0} playlist", $"{playlists.Count:N0} playlists"), countFont, new Rectangle(content.Left, titleY + S(scale, 34), content.Width / 2, S(scale, 22)), Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int bottomStatsH = S(scale, 56);
            int bottomPad = S(scale, 24);
            int bodyTop = titleY + S(scale, 74);
            Rectangle stats = new Rectangle(
                content.Left + Math.Max(0, (content.Width - Math.Min(content.Width, S(scale, 1180))) / 2),
                content.Bottom - bottomStatsH - bottomPad,
                Math.Min(content.Width, S(scale, 1180)),
                bottomStatsH);

            int gap = S(scale, 48);
            int detailW = Math.Min(S(scale, 430), Math.Max(S(scale, 350), (int)Math.Round(content.Width * 0.30)));
            Rectangle list = new Rectangle(content.Left, bodyTop, Math.Max(1, content.Width - detailW - gap - S(scale, 18)), Math.Max(1, stats.Top - bodyTop - S(scale, 22)));
            Rectangle detail = new Rectangle(list.Right + gap, bodyTop - S(scale, 4), detailW, Math.Max(1, stats.Top - bodyTop - S(scale, 18)));

            if (playlists.Count == 0)
            {
                _gridScroll = 0;
                _gridScrollMax = 0;
                // Stessa viewport delle altre categorie vuote: non riservare uno
                // spazio statistiche che qui non viene disegnato, altrimenti il
                // messaggio appare sensibilmente più in alto.
                DrawEmptyGrid(g, CollectionEmptyViewport(content, scale));
                return;
            }

            DrawPlaylistRows(g, list, playlists, scale);
            DrawPlaylistDetail(g, detail, playlists[_playlistSelectionIndex], scale);
            DrawPlaylistStats(g, stats, playlists, scale);
        }

        private List<PlaylistViewItem> BuildPlaylistViewItems()
        {
            long writeTicks;
            try { writeTicks = File.Exists(PlaylistsPath) ? File.GetLastWriteTimeUtc(PlaylistsPath).Ticks : 0; }
            catch { writeTicks = 0; }
            if (_playlistViewCache != null && _playlistViewCacheWriteTicks == writeTicks)
                return _playlistViewCache;

            var model = LoadJson<PlaylistModel>(PlaylistsPath);
            if (model?.Playlists == null || model.Playlists.Count == 0)
            {
                _playlistViewCache = new List<PlaylistViewItem>();
                _playlistViewCacheWriteTicks = writeTicks;
                return _playlistViewCache;
            }

            static int BucketOrder(string bucket) => NormalizePlaylistBucket(bucket) switch
            {
                "Movies" => 0,
                "TV" => 1,
                "Videos" => 2,
                "Music" => 3,
                "Photos" => 4,
                _ => 5
            };

            _playlistViewCache = model.Playlists.Values
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p =>
                {
                    var items = (p.Items ?? new List<string>())
                        .Where(path =>
                        {
                            try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && !ShouldIgnoreMediaPath(path); }
                            catch { return false; }
                        })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(path => ToItem(path))
                        .Where(item => item != null)
                        .Cast<LibraryItem>()
                        .ToList();

                    string bucket = NormalizePlaylistBucket(FirstNonEmpty(p.Bucket, InferPlaylistBucket(items), "Mixed"));
                    string type = PlaylistTypeLabel(bucket, items);
                    double duration = items.Sum(item => Math.Max(0, item.DurationMinutes ?? 0));
                    return new PlaylistViewItem
                    {
                        Key = FirstNonEmpty(p.Key, p.Name),
                        Name = p.Name,
                        Bucket = bucket,
                        CoverPath = FirstExistingPath(new[] { p.CoverPath, p.ArtworkPath }) ?? string.Empty,
                        Items = items,
                        TypeLabel = type,
                        DurationMinutes = duration,
                        Description = FirstRawNonEmpty(p.Description, PlaylistDescription(bucket, items))
                    };
                })
                .OrderBy(p => BucketOrder(p.Bucket))
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _playlistViewCacheWriteTicks = writeTicks;
            return _playlistViewCache;
        }

        private void DeletePlaylist(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            var model = LoadJson<PlaylistModel>(PlaylistsPath) ?? new PlaylistModel();
            if (!model.Playlists.TryGetValue(key, out var playlist) || playlist == null)
                return;

            string name = FirstRawNonEmpty(playlist.Name, key);
            var owner = FindForm();
            var result = owner != null
                ? MessageBox.Show(owner,
                    L($"Eliminare la playlist \"{name}\"?", $"Delete playlist \"{name}\"?"),
                    L("Elimina playlist", "Delete playlist"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning)
                : MessageBox.Show(
                    L($"Eliminare la playlist \"{name}\"?", $"Delete playlist \"{name}\"?"),
                    L("Elimina playlist", "Delete playlist"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
                return;

            if (!model.Playlists.Remove(key))
                return;

            SaveJson(PlaylistsPath, model);
            _playlistViewCache = null;
            _playlistViewCacheWriteTicks = long.MinValue;
            _playlistSelectionIndex = Math.Max(0, Math.Min(_playlistSelectionIndex, Math.Max(0, model.Playlists.Count - 1)));
            RefreshContent();
        }

        private void DrawPlaylistRows(Graphics g, Rectangle viewport, IReadOnlyList<PlaylistViewItem> playlists, float scale)
        {
            int rowH = S(scale, 104);
            int gap = S(scale, 10);
            int groupHeaderH = S(scale, 38);
            int groupCount = playlists
                .Select(playlist => NormalizePlaylistBucket(playlist.Bucket))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            int contentH = playlists.Count * (rowH + gap) + groupCount * groupHeaderH;
            if (playlists.Count > 0)
                contentH -= gap;
            _gridScrollMax = Math.Max(0, contentH - viewport.Height);
            _gridScroll = Math.Max(0, Math.Min(_gridScroll, _gridScrollMax));

            using Region previousClip = g.Clip.Clone();
            using Region clip = new(viewport);
            g.SetClip(clip, CombineMode.Intersect);

            int contentY = 0;
            string previousBucket = string.Empty;
            for (int i = 0; i < playlists.Count; i++)
            {
                string bucket = NormalizePlaylistBucket(playlists[i].Bucket);
                if (!string.Equals(bucket, previousBucket, StringComparison.OrdinalIgnoreCase))
                {
                    Rectangle header = new(viewport.Left, viewport.Top + contentY - _gridScroll, viewport.Width, groupHeaderH);
                    if (header.Bottom >= viewport.Top && header.Top <= viewport.Bottom)
                        DrawPlaylistGroupHeader(g, header, bucket, scale);
                    contentY += groupHeaderH;
                    previousBucket = bucket;
                }

                Rectangle row = new(viewport.Left, viewport.Top + contentY - _gridScroll, viewport.Width, rowH);
                if (row.Bottom >= viewport.Top && row.Top <= viewport.Bottom)
                {
                    DrawPlaylistRow(g, row, playlists[i], i, i == _playlistSelectionIndex, scale);
                    g.SetClip(clip, CombineMode.Replace);
                }
                contentY += rowH + gap;
            }

            g.Clip = previousClip;
            DrawGridScrollbar(g, viewport, contentH);
        }

        private void DrawPlaylistGroupHeader(Graphics g, Rectangle r, string bucket, float scale)
        {
            string label = bucket switch
            {
                "Movies" => L("Film", "Movies"),
                "TV" => L("Serie TV", "TV Series"),
                "Videos" => L("Video", "Videos"),
                "Music" => L("Musica", "Music"),
                "Photos" => L("Foto", "Photos"),
                _ => L("Miste", "Mixed")
            };

            using var font = LibraryFont("Segoe UI Semibold", Math.Max(8.6f, 9.8f * scale));
            using var line = new Pen(Color.FromArgb(52, Border));
            Size measured = TextRenderer.MeasureText(label, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            int textW = Math.Min(r.Width / 2, measured.Width + S(scale, 6));
            TextRenderer.DrawText(g, label, font, new Rectangle(r.Left, r.Top, textW, r.Height), Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            int lineY = r.Top + r.Height / 2;
            g.DrawLine(line, r.Left + textW + S(scale, 10), lineY, r.Right, lineY);
        }

        private void DrawPlaylistRow(Graphics g, Rectangle r, PlaylistViewItem playlist, int index, bool selected, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            using (var path = Round(r, S(scale, 7)))
            using (var fill = new LinearGradientBrush(r,
                selected ? Color.FromArgb(112, Selection) : (hover ? Color.FromArgb(70, 7, 21, 34) : Color.FromArgb(42, 5, 14, 22)),
                selected ? Color.FromArgb(70, 1, 18, 32) : Color.FromArgb(28, 2, 9, 16),
                LinearGradientMode.Vertical))
            using (var border = new Pen(selected ? BorderAccent : Color.Transparent, selected ? 1.4f : 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.PlaylistSelect, Index = index, Key = playlist.Key });

            int x = r.Left + S(scale, 14);
            int thumb = S(scale, 76);
            int thumbGap = S(scale, 6);
            int slots = Math.Min(5, playlist.Items.Count);
            if (slots <= 0)
                slots = 1;
            for (int i = 0; i < slots; i++)
            {
                Rectangle t = new Rectangle(x + i * (thumb + thumbGap), r.Top + (r.Height - thumb) / 2, thumb, thumb);
                DrawPlaylistThumb(g, t, playlist.Items.ElementAtOrDefault(i), scale);
            }

            int textX = x + slots * thumb + Math.Max(0, slots - 1) * thumbGap + S(scale, 24);
            int actionsW = S(scale, 18);
            Rectangle titleRect = new Rectangle(textX, r.Top + S(scale, 22), Math.Max(1, r.Right - textX - actionsW - S(scale, 16)), S(scale, 24));
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(10f, 11.8f * scale));
            using var meta = LibraryFont("Segoe UI", Math.Max(8.0f, 9.0f * scale));
            int titleW = Math.Min(Math.Max(S(scale, 70), titleRect.Width - S(scale, 84)), TextRenderer.MeasureText(playlist.Name, title, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + S(scale, 14));
            Rectangle tag = new Rectangle(titleRect.Left + Math.Max(S(scale, 88), titleW), titleRect.Top + S(scale, 1), S(scale, 74), S(scale, 22));
            Rectangle clippedTitle = new Rectangle(titleRect.Left, titleRect.Top, Math.Max(S(scale, 20), tag.Left - titleRect.Left - S(scale, 10)), titleRect.Height);
            TextRenderer.DrawText(g, playlist.Name, title, clippedTitle, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            DrawPlaylistTag(g, tag, playlist.TypeLabel, scale);
            string metaText = $"{PlaylistItemCountText(playlist)} - {FormatDuration(playlist.DurationMinutes)}";
            TextRenderer.DrawText(g, metaText, meta, new Rectangle(textX, titleRect.Bottom + S(scale, 2), titleRect.Width, S(scale, 20)), Color.FromArgb(172, 188, 202), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, playlist.Description, meta, new Rectangle(textX, titleRect.Bottom + S(scale, 23), titleRect.Width, S(scale, 20)), Color.FromArgb(142, 158, 174), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawPlaylistThumb(Graphics g, Rectangle r, LibraryItem? item, float scale)
        {
            DrawPlaylistThumbPath(g, r, item?.ArtPath ?? item?.WideArtPath, item?.Title ?? "Playlist", scale, item?.Category ?? "Playlists");
        }

        private void DrawPlaylistThumbPath(Graphics g, Rectangle r, string? artPath, string fallbackTitle, float scale, string category = "Playlists")
        {
            using var path = Round(r, S(scale, 4));
            using (var fill = new SolidBrush(Color.FromArgb(96, 3, 9, 14)))
                g.FillPath(fill, path);

            using var smooth = CinecorePlayer2025.Utilities.SmoothClip.Begin(g, path);
            bool drawn = DrawImagePath(smooth.Graphics, r, artPath, requireLandscape: false, tint: false, preserveAspectWhenWide: false, verticalFocus: 0.5f, destinationBleed: 1);
            if (!drawn)
                DrawGeneratedArt(smooth.Graphics, r, fallbackTitle, hero: false, category: category);
        }

        private void DrawPlaylistDeleteButton(Graphics g, Rectangle r, string key, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            using (var path = Round(r, S(scale, 7)))
            using (var fill = new SolidBrush(hover ? Color.FromArgb(64, 54, 17, 28) : Color.FromArgb(32, 22, 12, 20)))
            using (var border = new Pen(hover ? Color.FromArgb(210, 255, 94, 112) : Color.FromArgb(98, 255, 94, 112)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            Rectangle glyph = new Rectangle(r.Left + S(scale, 16), r.Top + (r.Height - S(scale, 20)) / 2, S(scale, 20), S(scale, 20));
            if (!DrawIcon(g, glyph, "trash", Color.FromArgb(255, 116, 130)))
                DrawQueueActionGlyph(g, glyph, "trash", Color.FromArgb(255, 116, 130), scale);
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(8.4f, 9.4f * scale));
            TextRenderer.DrawText(g, L("Elimina", "Delete"), font,
                new Rectangle(glyph.Right + S(scale, 9), r.Top, Math.Max(1, r.Right - glyph.Right - S(scale, 15)), r.Height),
                Color.FromArgb(255, 178, 186), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.PlaylistDelete, Key = key });
        }

        private void DrawPlaylistDetail(Graphics g, Rectangle r, PlaylistViewItem playlist, float scale)
        {
            using (var path = Round(r, S(scale, 8)))
            using (var fill = new LinearGradientBrush(r, Color.FromArgb(118, 5, 15, 24), Color.FromArgb(62, 2, 8, 14), LinearGradientMode.Vertical))
            using (var border = new Pen(Color.FromArgb(30, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                { } // senza contorno
            }

            int pad = S(scale, 18);
            Rectangle hero = new Rectangle(r.Left + pad, r.Top + pad, r.Width - pad * 2, Math.Max(S(scale, 150), (int)Math.Round((r.Width - pad * 2) * 9 / 16.0)));
            LibraryItem? lead = playlist.Items.FirstOrDefault(item => LooksLikeUsableBackdrop(item.WideArtPath)) ?? playlist.Items.FirstOrDefault();
            using Region old = g.Clip.Clone();
            using (var clip = Round(hero, S(scale, 7)))
                g.SetClip(clip, CombineMode.Intersect);
            bool drawn = !string.IsNullOrWhiteSpace(playlist.CoverPath)
                ? DrawImagePath(g, hero, playlist.CoverPath, requireLandscape: false, tint: false, preserveAspectWhenWide: false, verticalFocus: 0.5f)
                : lead != null && DrawMediaContainBlack(g, hero, lead.WideArtPath ?? lead.ArtPath);
            if (!drawn)
                DrawGeneratedArt(g, hero, playlist.Name, hero: false, category: "Playlists");
            g.Clip = old;

            int thumb = Math.Max(S(scale, 48), Math.Min(S(scale, 66), (hero.Width - S(scale, 16)) / 5));
            int thumbTop = hero.Bottom - thumb / 2;
            int startX = hero.Left;
            int detailSlots = Math.Min(5, playlist.Items.Count);
            if (detailSlots <= 0)
                detailSlots = 1;
            for (int i = 0; i < detailSlots; i++)
            {
                var t = new Rectangle(startX + i * (thumb + S(scale, 7)), thumbTop, thumb, thumb);
                DrawPlaylistThumb(g, t, playlist.Items.ElementAtOrDefault(i), scale);
            }

            int y = thumbTop + thumb + S(scale, 24);
            using var title = LibraryFont("Segoe UI Semibold", Math.Max(15f, 18.2f * scale));
            using var meta = LibraryFont("Segoe UI", Math.Max(8.5f, 9.5f * scale));
            TextRenderer.DrawText(g, playlist.Name, title, new Rectangle(r.Left + pad, y, r.Width - pad * 2, S(scale, 58)), Color.White, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += S(scale, 64);

            Rectangle tag = new Rectangle(r.Left + pad, y, S(scale, 84), S(scale, 24));
            DrawPlaylistTag(g, tag, playlist.TypeLabel, scale);
            y += S(scale, 38);

            int deleteW = S(scale, 124);
            Rectangle play = new Rectangle(r.Left + pad, y, Math.Max(S(scale, 156), r.Width - pad * 2 - deleteW - S(scale, 12)), S(scale, 42));
            DrawPlaylistPlayButton(g, play, playlist, scale);
            DrawPlaylistDeleteButton(g, new Rectangle(play.Right + S(scale, 12), play.Top, deleteW, S(scale, 42)), playlist.Key, scale);
            y += S(scale, 64);

            TextRenderer.DrawText(g, playlist.Description, meta, new Rectangle(r.Left + pad, y, r.Width - pad * 2, S(scale, 58)), Color.FromArgb(186, 198, 210), TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawPlaylistPlayButton(Graphics g, Rectangle r, PlaylistViewItem playlist, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            using (var path = Round(r, S(scale, 6)))
            using (var fill = new SolidBrush(hover ? Color.FromArgb(150, 0, 108, 210) : Color.FromArgb(126, 0, 92, 190)))
            using (var border = new Pen(Color.FromArgb(190, Accent)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            using var font = LibraryFont("Segoe UI Semibold", Math.Max(8.4f, 9.5f * scale));
            DrawTinyPlayGlyph(g, new Rectangle(r.Left + S(scale, 58), r.Top + (r.Height - S(scale, 17)) / 2, S(scale, 15), S(scale, 17)), Color.White);
            TextRenderer.DrawText(g, L("Riproduci tutto", "Play all"), font, new Rectangle(r.Left + S(scale, 82), r.Top, r.Width - S(scale, 96), r.Height), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _hits.Add(new HitZone { Bounds = r, Kind = HitKind.PlaylistPlay, Item = playlist.Items.FirstOrDefault(), Key = playlist.Key });
        }

        private void DrawPlaylistTag(Graphics g, Rectangle r, string text, float scale)
        {
            using (var path = Round(r, S(scale, 5)))
            using (var fill = new SolidBrush(Color.FromArgb(64, 0, 78, 132)))
            using (var border = new Pen(Color.FromArgb(94, Accent)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            using var font = LibraryFont("Segoe UI", Math.Max(7.2f, 8.1f * scale));
            TextRenderer.DrawText(g, text, font, r, Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawPlaylistStats(Graphics g, Rectangle r, IReadOnlyList<PlaylistViewItem> playlists, float scale)
        {
            int items = playlists.Sum(p => p.Items.Count);
            double duration = playlists.Sum(p => p.DurationMinutes);
            var old = _statsCells.ToList();
            _statsCells.Clear();
            _statsCells.Add(("playlist", L($"{playlists.Count:N0} playlist", $"{playlists.Count:N0} playlists")));
            _statsCells.Add(("grid", L($"{items:N0} elementi", $"{items:N0} items")));
            _statsCells.Add(("time", FormatDuration(duration)));
            _statsCells.Add(("cloud", L("TMDb sincronizzato", "TMDb synced")));
            DrawStats(g, r);
            _statsCells.Clear();
            foreach (var cell in old)
                _statsCells.Add(cell);
        }

        private static string InferPlaylistBucket(IReadOnlyList<LibraryItem> items)
        {
            if (items.Count == 0)
                return "Mixed";
            if (items.Count(item => string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase)) >= items.Count / 2.0)
                return "Music";
            if (items.Count(item => string.Equals(item.Category, "TV Series", StringComparison.OrdinalIgnoreCase)) >= items.Count / 2.0)
                return "TV";
            if (items.Count(item => string.Equals(item.Category, "Movies", StringComparison.OrdinalIgnoreCase)) >= items.Count / 2.0)
                return "Movies";
            if (items.Count(item => string.Equals(item.Category, "Videos", StringComparison.OrdinalIgnoreCase)) >= items.Count / 2.0)
                return "Videos";
            if (items.Count(item => string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase)) >= items.Count / 2.0)
                return "Photos";
            return "Mixed";
        }

        private static string PlaylistTypeLabel(string bucket, IReadOnlyList<LibraryItem> items)
        {
            string value = (bucket ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Contains("music") || items.Any(item => string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase)))
                return "Music";
            if (value.Contains("movie") || value.Contains("film"))
                return "Movies";
            if (value.Contains("tv") || value.Contains("serie"))
                return "TV";
            if (value.Contains("photo") || value.Contains("foto"))
                return "Photos";
            if (value.Contains("video"))
                return "Videos";
            return "Mixed";
        }

        private string PlaylistDescription(string bucket, IReadOnlyList<LibraryItem> items)
        {
            string type = PlaylistTypeLabel(bucket, items);
            return type switch
            {
                "Music" => L("Una selezione musicale pronta da riprodurre.", "A music selection ready to play."),
                "Movies" => L("Una raccolta di film dalla libreria.", "A movie collection from the library."),
                "TV" => L("Episodi e stagioni raccolti in una playlist.", "Episodes and seasons collected in a playlist."),
                "Videos" => L("Video personali raccolti in un unico spazio.", "Personal videos collected in one place."),
                "Photos" => L("Una selezione di fotografie pronta da sfogliare.", "A photo selection ready to browse."),
                _ => L("Una raccolta mista dalla libreria.", "A mixed collection from the library.")
            };
        }

        private string PlaylistItemCountText(PlaylistViewItem playlist)
        {
            string type = PlaylistTypeLabel(playlist.Bucket, playlist.Items);
            int count = playlist.Items.Count;
            return type switch
            {
                "Music" => L($"{count:N0} brani", $"{count:N0} tracks"),
                "Movies" => L($"{count:N0} film", $"{count:N0} movies"),
                "TV" => L($"{count:N0} episodi", $"{count:N0} episodes"),
                "Videos" => L($"{count:N0} video", $"{count:N0} videos"),
                "Photos" => L($"{count:N0} foto", $"{count:N0} photos"),
                _ => L($"{count:N0} elementi", $"{count:N0} items")
            };
        }

    }
}
