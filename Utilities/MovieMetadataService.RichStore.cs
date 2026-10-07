#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    internal static partial class MovieMetadataService
    {
        /// <summary>
        /// Cast, director, genres, rating and reviews of each title, kept on disk. They lived in
        /// memory only: after every launch Spotlight, the detail sheet and the cast strip waited
        /// for several TMDb requests per film, and a failed request left a title without data.
        /// A saved answer is shown at once and refreshed in the background when it gets old.
        /// </summary>
        private static class RichMetadataDiskStore
        {
            private sealed class Entry
            {
                public DateTime SavedUtc { get; set; }
                public bool WithReviews { get; set; }
                public RichMetadata Data { get; set; } = new();
            }

            private static readonly object Gate = new();
            private static Dictionary<string, Entry>? _entries;
            private static Timer? _saveTimer;
            private const int MaxEntries = 4000;
            private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(10);

            private static string FilePath => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025", "richMetadataIndex.json");

            private static Dictionary<string, Entry> Entries()
            {
                if (_entries != null) return _entries;
                var loaded = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath, Encoding.UTF8));
                        if (saved != null)
                            foreach (var pair in saved)
                                if (pair.Value?.Data != null) loaded[pair.Key] = pair.Value;
                    }
                }
                catch (Exception ex) { Dbg.Warn("[TMDB] rich metadata cache unreadable: " + ex.Message); }
                return _entries = loaded;
            }

            public static string Key(string filePath, string language, string mediaType)
                => string.Join("|", filePath.Trim(), language, mediaType);

            /// <summary>Saved details for a title; <paramref name="stale"/> asks the caller to refresh them in the background.</summary>
            public static RichMetadata? TryGet(string key, bool needReviews, out bool stale)
            {
                stale = false;
                lock (Gate)
                {
                    if (!Entries().TryGetValue(key, out Entry? entry)) return null;
                    if (needReviews && !entry.WithReviews) return null;
                    stale = DateTime.UtcNow - entry.SavedUtc > RefreshAfter;
                    return Clone(entry.Data);
                }
            }

            public static void Put(string key, RichMetadata data, bool withReviews)
            {
                if (!IsUseful(data)) return;
                lock (Gate)
                {
                    var entries = Entries();
                    // Una risposta senza recensioni non cancella quelle gia' salvate.
                    if (!withReviews && entries.TryGetValue(key, out Entry? previous) && previous.WithReviews)
                    {
                        var merged = Clone(data);
                        merged.Reviews = previous.Data.Reviews;
                        entries[key] = new Entry { SavedUtc = DateTime.UtcNow, WithReviews = true, Data = merged };
                    }
                    else entries[key] = new Entry { SavedUtc = DateTime.UtcNow, WithReviews = withReviews, Data = Clone(data) };

                    if (entries.Count > MaxEntries)
                        foreach (string old in entries.OrderBy(pair => pair.Value.SavedUtc).Take(entries.Count - MaxEntries).Select(pair => pair.Key).ToArray())
                            entries.Remove(old);

                    _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
                    _saveTimer.Change(3000, Timeout.Infinite);
                }
            }

            public static bool IsUseful(RichMetadata? data)
                => data != null && (data.TmdbId.HasValue || data.CastMembers.Count > 0 || !string.IsNullOrWhiteSpace(data.Director));

            private static void Save()
            {
                try
                {
                    string json;
                    lock (Gate) json = JsonSerializer.Serialize(Entries());
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    string temp = FilePath + ".tmp";
                    File.WriteAllText(temp, json, new UTF8Encoding(false));
                    File.Move(temp, FilePath, true);
                }
                catch (Exception ex) { Dbg.Warn("[TMDB] rich metadata cache save failed: " + ex.Message); }
            }

            // Chi riceve i dettagli li modifica (percorsi locali delle foto del cast): ognuno ha la sua copia.
            private static RichMetadata Clone(RichMetadata source) => new()
            {
                Title = source.Title,
                Year = source.Year,
                Overview = source.Overview,
                Cast = source.Cast?.ToList() ?? new List<string>(),
                Genres = source.Genres?.ToList() ?? new List<string>(),
                Tagline = source.Tagline,
                Director = source.Director,
                Rating = source.Rating,
                CastMembers = (source.CastMembers ?? new List<RichCastMember>())
                    .Select(member => new RichCastMember { Name = member.Name, Character = member.Character, ProfilePath = member.ProfilePath }).ToList(),
                MediaType = source.MediaType,
                TmdbId = source.TmdbId,
                ImdbId = source.ImdbId,
                Reviews = (source.Reviews ?? new List<RichReview>()).Select(review => new RichReview
                {
                    Author = review.Author,
                    Content = review.Content,
                    Rating = review.Rating,
                    Language = review.Language,
                    Source = review.Source,
                    RatingScale = review.RatingScale,
                    Detail = review.Detail,
                    Critic = review.Critic
                }).ToList()
            };
        }

        /// <summary>Details already saved for a title, without any network request (null when unknown).</summary>
        public static RichMetadata? TryGetSavedRichMetadata(string filePath, string language, string? mediaType = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            string type = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" :
                          string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase) ? "movie" : "auto";
            return RichMetadataDiskStore.TryGet(RichMetadataDiskStore.Key(filePath, language, type), needReviews: false, out _);
        }
    }
}
