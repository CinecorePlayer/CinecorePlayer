#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private void QueueRichDetailResolve(LibraryItem? item)
        {
            if (item == null || item.IsGroup || !ShouldResolveRichOverview(item.Category))
                return;
            if (item.RichDetailsResolved)
                return;

            string requestKey = NormalizeOverviewKey(item.Path);
            if (string.IsNullOrWhiteSpace(requestKey) ||
                _detailMetadataRequests.Contains(requestKey) ||
                MetadataRetryPending(_detailMetadataFailures, requestKey))
                return;

            _detailMetadataRequests.Add(requestKey);
            string metadataKey = ResolveMetadataLookupKey(item.Path, item.Title);
            string language = UiEnglish ? "en-US" : "it-IT";
            string path = item.Path;
            string title = item.Title;
            double? durationSeconds = item.DurationMinutes.HasValue ? item.DurationMinutes.Value * 60.0 : null;

            Task.Run(() =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    MovieMetadataService.RichMetadata? best = null;
                    foreach (string lookup in MetadataLookupCandidates(path, metadataKey, title))
                    {
                        var rich = MovieMetadataService.ResolveRichMetadata(lookup, durationSeconds, language, cts.Token);
                        best = rich;
                        if (rich.CastMembers.Count > 0 || rich.Reviews.Count > 0 || rich.TmdbId.HasValue || !string.IsNullOrWhiteSpace(rich.Director))
                            break;
                    }

                    if (best == null)
                        return null;

                    string overview = CleanLine(best.Overview);
                    var genres = CleanGenreList(best.Genres);
                    if (!string.IsNullOrWhiteSpace(overview))
                        SaveOverviewToIndex(overview, path, title, metadataKey);
                    if (genres.Count > 0)
                        SaveGenresToIndex(genres, path, title, metadataKey);
                    return best;
                }
                catch
                {
                    return null;
                }
            }).ContinueWith(task =>
            {
                MovieMetadataService.RichMetadata? rich = null;
                try { rich = task.Result; } catch { }
                PostToUi(() =>
                {
                    _detailMetadataRequests.Remove(requestKey);
                    if (rich == null)
                    {
                        _detailMetadataFailures[requestKey] = DateTime.UtcNow.AddMinutes(1);
                        Invalidate();
                        return;
                    }

                    ApplyRichDetailsToItemCopies(path, rich);
                    QueueCastProfileDownloads(path, rich.CastMembers);
                    Invalidate();
                });
            }, TaskScheduler.Default);
        }

        private void ApplyRichDetailsToItemCopies(string path, MovieMetadataService.RichMetadata rich)
        {
            void Apply(LibraryItem target)
            {
                string overview = CleanLine(rich.Overview);
                if (!string.IsNullOrWhiteSpace(overview))
                    target.Overview = overview;
                var genres = CleanGenreList(rich.Genres);
                if (genres.Count > 0)
                    target.Genres = genres;
                target.Tagline = CleanLine(rich.Tagline);
                target.Director = CleanLine(rich.Director);
                target.Rating = rich.Rating;
                target.CastMembers = rich.CastMembers
                    .Where(member => !string.IsNullOrWhiteSpace(member.Name))
                    .Take(20)
                    .Select(CloneCastMember)
                    .ToList();
                target.Reviews = rich.Reviews
                    .Where(review => !string.IsNullOrWhiteSpace(review.Content))
                    .Take(4)
                    .Select(review => new MovieMetadataService.RichReview
                    {
                        Author = review.Author,
                        Content = review.Content,
                        Rating = review.Rating
                    })
                    .ToList();
                target.TmdbId = rich.TmdbId;
                target.ImdbId = rich.ImdbId;
                target.ReviewMediaType = rich.MediaType;
                target.RichDetailsResolved = true;
            }

            foreach (var existing in _items.Where(existing => string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)))
                Apply(existing);
            foreach (var group in _items.Where(existing => existing.IsGroup))
            {
                foreach (var child in group.Children.Where(child => string.Equals(child.Path, path, StringComparison.OrdinalIgnoreCase)))
                    Apply(child);
            }
            if (_detailItem != null && string.Equals(_detailItem.Path, path, StringComparison.OrdinalIgnoreCase))
                Apply(_detailItem);
        }

        private void QueueCastProfileDownloads(string mediaPath, IEnumerable<MovieMetadataService.RichCastMember> members)
        {
            foreach (var member in members.Where(member => !string.IsNullOrWhiteSpace(member.ProfilePath)).Take(20))
            {
                string remotePath = member.ProfilePath!;
                if (File.Exists(remotePath) || !Uri.TryCreate(remotePath, UriKind.Absolute, out var remoteUri) ||
                    (remoteUri.Scheme != Uri.UriSchemeHttp && remoteUri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }
                string requestKey = remotePath.Trim();
                lock (_castImageRequests)
                {
                    if (!_castImageRequests.Add(requestKey))
                        continue;
                }

                string personName = member.Name;
                string character = member.Character;
                Task.Run(() =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                        return MovieMetadataService.CacheCastProfileImage(remotePath, cts.Token);
                    }
                    catch { return null; }
                }).ContinueWith(task =>
                {
                    string? localPath = null;
                    try { localPath = task.Result; } catch { }
                    try
                    {
                        lock (_castImageRequests)
                            _castImageRequests.Remove(requestKey);
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(localPath))
                        return;

                    try
                    {
                        if (IsDisposed)
                            return;
                        BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                UpdateCastProfilePath(mediaPath, personName, character, localPath);
                                Invalidate();
                            }
                            catch { }
                        }));
                    }
                    catch { }
                }, TaskScheduler.Default);
            }
        }

        private void UpdateCastProfilePath(string mediaPath, string name, string character, string localPath)
        {
            lock (_imageCacheSync)
            {
                _imageLoadFailures.RemoveWhere(key => key.EndsWith(":" + localPath, StringComparison.OrdinalIgnoreCase));
            }

            void Update(LibraryItem target)
            {
                foreach (var member in target.CastMembers.Where(member =>
                             string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(member.Character, character, StringComparison.OrdinalIgnoreCase)))
                    member.ProfilePath = localPath;
            }

            foreach (var existing in _items.Where(existing => string.Equals(existing.Path, mediaPath, StringComparison.OrdinalIgnoreCase)))
                Update(existing);
            foreach (var group in _items.Where(existing => existing.IsGroup))
            {
                foreach (var child in group.Children.Where(child => string.Equals(child.Path, mediaPath, StringComparison.OrdinalIgnoreCase)))
                    Update(child);
            }
            if (_detailItem != null && string.Equals(_detailItem.Path, mediaPath, StringComparison.OrdinalIgnoreCase))
                Update(_detailItem);
        }

        private static MovieMetadataService.RichCastMember CloneCastMember(MovieMetadataService.RichCastMember member)
        {
            return new MovieMetadataService.RichCastMember
            {
                Name = member.Name,
                Character = member.Character,
                ProfilePath = member.ProfilePath
            };
        }
    }
}
