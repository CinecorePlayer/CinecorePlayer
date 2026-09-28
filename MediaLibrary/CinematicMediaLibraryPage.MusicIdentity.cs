#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private readonly Dictionary<string, MusicArtistArtworkService.Artwork> _artistArtwork = new();
        private readonly HashSet<string> _artistArtworkPending = new();
        private int _artistArtworkProviderVersion;
        internal void RefreshArtistArtworkAfterProviderChange()
        {
            _artistArtworkProviderVersion++;
            _artistArtwork.Clear();
            _artistArtworkPending.Clear();
            _musicBannerAttempts.Clear();
            _musicBannerPaths.Clear();
            Invalidate();
        }
        private static IEnumerable<string> MusicArtists(string? name, string? albumArtist = null) => Regex.Split(name ?? "",
            !string.IsNullOrWhiteSpace(albumArtist) && MusicArtistArtworkService.Identity(name) == MusicArtistArtworkService.Identity(albumArtist)
                ? @"\s*(?:;|\b(?:feat|ft|featuring)\.?\s)\s*"
                : @"\s*(?:;|\b(?:feat|ft|featuring)\.?\s|,\s+(?!the\s+creator\b))\s*", RegexOptions.IgnoreCase)
            .Select(n => Regex.Replace(MusicTextIdentity.Artist(n), @"\s+", " ").Trim(' ','(',')'))
            .Where(n => n.Length > 0 && !new[] { "VARIOUSARTISTS", "ARTISTIVARI", "UNKNOWNARTIST", "ARTISTASCONOSCIUTO", "VARIOUS" }.Contains(MusicArtistArtworkService.Identity(n)));

        private List<LibraryItem> GroupMusicArtists(List<LibraryItem> tracks) => tracks
            .DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .SelectMany(t => MusicArtists(t.ArtistName, t.AlbumArtist).Select(name => (Name: name, Track: t)))
            .GroupBy(x => MusicArtistArtworkService.Identity(x.Name))
            .Select(group => BuildGroupedItem(group.First().Name, L($"{group.Count()} brani", $"{group.Count()} tracks"), "Artist", group.Select(x => x.Track).DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList())!)
            .OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        private MusicArtistArtworkService.Artwork ArtistArtwork(string name, string path)
        {
            string key = MusicArtistArtworkService.Identity(name);
            if (_artistArtwork.TryGetValue(key, out var known)) return known;
            if (_artistArtworkPending.Add(key))
            {
                string? portrait = null, landscape = null;
                try
                {
                    string? album = Path.GetDirectoryName(path), artist = Path.GetDirectoryName(album);
                    foreach (var dir in new[] { artist, album }.Where(p => p != null))
                        foreach (var ext in new[] { ".jpg", ".png", ".jpeg" })
                        {
                            foreach (string candidate in new[] { "artist", "artist-thumb" })
                            { var file = Path.Combine(dir!, candidate + ext); if (portrait == null && File.Exists(file)) portrait = file; }
                            foreach (string candidate in new[] { "fanart", "backdrop", "background" })
                            { var file = Path.Combine(dir!, candidate + ext); if (landscape == null && File.Exists(file)) { using var img = Image.FromFile(file); if (img.Width >= 1000 && img.Width > img.Height * 1.5) landscape = file; } }
                        }
                }
                catch { }
                var local = new MusicArtistArtworkService.Artwork(portrait, landscape);
                _artistArtwork[key] = local;
                int providerVersion = _artistArtworkProviderVersion;
                if (MusicArtists(name).Any() && (portrait == null || landscape == null)) _ = Resolve();
                async Task Resolve()
                {
                    string? albumTitle = _items.SelectMany(t => t.IsGroup ? t.Children : new List<LibraryItem> { t })
                        .FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))?.AlbumTitle;
                    albumTitle ??= InferAlbumInfo(path).Album;
                    var remote = await MusicArtistArtworkService.ResolveAsync(name, albumTitle).ConfigureAwait(false);
                    if (IsDisposed || !IsHandleCreated) return;
                    try { BeginInvoke(new Action(() => { if (IsDisposed || providerVersion != _artistArtworkProviderVersion) return; _artistArtwork[key] = new(portrait ?? remote.Portrait, landscape ?? remote.Landscape); Invalidate(); })); } catch { }
                }
                return local;
            }
            return new(null, null);
        }

        private void ToggleMusicFavorite(LibraryItem item)
        {
            if (!item.IsGroup) { ToggleFavorite(item.Path); return; }
            var paths = item.Children.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var model = new FavoritesModel { Paths = new HashSet<string>(GetFavoritePathsSnapshot(), StringComparer.OrdinalIgnoreCase) };
            bool remove = paths.All(p => model.Paths.Contains(p));
            foreach (var path in paths) { if (remove) model.Paths.Remove(path); else model.Paths.Add(path); }
            SaveJson(FavoritesPath, model); _favoritePathsCache = model.Paths; Invalidate();
        }
        internal bool IsMusicTrackFavorite(string path) => IsFavorite(path);
        internal void ToggleMusicTrackFavorite(string path) => ToggleFavorite(path);
        internal void AddMusicTrackToPlaylist(string path) => AddItemsToPlaylist(new[] { path });

        private void DrawMusicQuickActions(Graphics g, Rectangle art, LibraryItem item, float scale, bool hovered)
        {
            int size = Math.Clamp(S(scale, 28), 24, 32), gap = 5;
            bool favorite = item.IsGroup ? item.Children.Count > 0 && item.Children.All(t => IsFavorite(t.Path)) : IsFavorite(item.Path);
            int i = 0;
            foreach (var action in new[] { ("star", HitKind.PosterFavorite), ("queue-add", HitKind.MusicAlbumQueue), ("playlist", HitKind.PosterPlaylist) })
            {
                var rect = new Rectangle(art.Right - 7 - size - i++ * (size + gap), art.Top + 7, size, size);
                bool active = action.Item2 == HitKind.PosterFavorite && favorite;
                using var fill = new SolidBrush(Color.FromArgb(hovered ? 170 : 100, 0, 0, 0)); g.FillEllipse(fill, rect);
                HUD.MusicTransportBar.DrawSymbol(g, Rectangle.Inflate(rect, -6, -6), action.Item1, active ? Accent : Color.White);
                _hits.Add(new HitZone { Bounds = rect, Kind = action.Item2, Item = item, Key = item.Path });
            }
        }
    }
}
