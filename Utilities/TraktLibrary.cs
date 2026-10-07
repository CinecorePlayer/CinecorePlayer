#nullable enable
using CinecorePlayer2025.Engines;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>Il ponte fra Trakt e la libreria locale: consigli riconosciuti fra i propri file e diario inviato a Trakt.</summary>
    internal static class TraktLibrary
    {
        public sealed record LibraryTitle(string Path, string Title, string? FileTitle, int? Year, bool IsSeries);

        /// <param name="LibraryPath">File della libreria che corrisponde al consiglio, se c'e'.</param>
        public sealed record Recommendation(TraktClient.Title Trakt, string DisplayTitle, int? Year, string? PosterFile, string? LibraryPath);

        private sealed class TitleEntry
        {
            public string Title { get; set; } = "";
            public string? OriginalTitle { get; set; }
            public int? Year { get; set; }
            public string? PosterPath { get; set; }
        }

        private sealed class IdEntry
        {
            public bool Episode { get; set; }
            public int Tmdb { get; set; }
            public int Season { get; set; }
            public int Number { get; set; }
        }

        private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025");
        private static string PosterFolder => Path.Combine(Folder, "trakt-posters");
        private static readonly object Gate = new();

        private static Dictionary<string, T> Load<T>(string file)
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(Path.Combine(Folder, file)));
                if (loaded != null) return new Dictionary<string, T>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        }

        private static void Store<T>(string file, Dictionary<string, T> entries)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string target = Path.Combine(Folder, file), temp = target + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(entries));
                File.Move(temp, target, true);
            }
            catch (Exception ex) { Dbg.Warn("[TRAKT] cache save failed: " + ex.Message); }
        }

        // ----- Consigli -----

        /// <summary>Titolo ridotto a lettere e cifre, senza accenti: "Il Petroliere" e "il petroliere!" coincidono.</summary>
        internal static string Normalize(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            var builder = new StringBuilder(title.Length);
            foreach (char c in title.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
                else if (c == '&') builder.Append("and");
            }
            return builder.ToString();
        }

        internal static string? Match(IReadOnlyList<LibraryTitle> library, bool series, int? year, params string?[] titles)
        {
            var wanted = titles.Select(Normalize).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (wanted.Count == 0) return null;
            foreach (var item in library)
            {
                if (item.IsSeries != series) continue;
                if (!wanted.Contains(Normalize(item.Title)) && !wanted.Contains(Normalize(item.FileTitle))) continue;
                // Film: stesso titolo e anni diversi sono film diversi (remake). Le serie si riconoscono dal solo titolo.
                if (!series && year.HasValue && item.Year.HasValue && Math.Abs(year.Value - item.Year.Value) > 1) continue;
                return item.Path;
            }
            return null;
        }

        /// <param name="kind">"movies" oppure "shows".</param>
        public static async Task<List<Recommendation>> RecommendationsAsync(string kind, IReadOnlyList<LibraryTitle> library, string language, CancellationToken ct) =>
            await ResolveAsync(await TraktClient.RecommendationsAsync(kind, 60, ct).ConfigureAwait(false), library, language, null, ct).ConfigureAwait(false);

        /// <summary>Elenco provvisorio, pronto subito: i titoli come li da' Trakt, senza locandina.</summary>
        public static List<Recommendation> Provisional(IReadOnlyList<TraktClient.Title> titles, IReadOnlyList<LibraryTitle> library) =>
            titles.Select(title => new Recommendation(title, title.Name, title.Year, null, Match(library, title.Kind == "tv", title.Year, title.Name))).ToList();

        /// <summary>Per ogni titolo di Trakt: nome nella lingua del player, locandina e, se c'e', il file della libreria.
        /// <paramref name="progress"/> riceve ogni titolo appena e' pronto.</summary>
        internal static async Task<List<Recommendation>> ResolveAsync(IReadOnlyList<TraktClient.Title> titles, IReadOnlyList<LibraryTitle> library, string language, IProgress<Recommendation>? progress, CancellationToken ct)
        {
            Dictionary<string, TitleEntry> cache;
            lock (Gate) cache = Load<TitleEntry>("trakt-titles.json");
            var resolved = new ConcurrentDictionary<int, Recommendation>();
            bool cacheChanged = false;

            await Parallel.ForEachAsync(titles, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (title, token) =>
            {
                string key = title.Kind + ":" + title.TmdbId.ToString(CultureInfo.InvariantCulture) + ":" + language;
                TitleEntry? entry;
                lock (Gate) cache.TryGetValue(key, out entry);
                if (entry == null)
                {
                    try
                    {
                        var summary = MovieMetadataService.GetTmdbSummary(title.Kind, title.TmdbId, language, token);
                        if (summary != null)
                        {
                            entry = new TitleEntry { Title = summary.Title, OriginalTitle = summary.OriginalTitle, Year = summary.Year, PosterPath = summary.PosterPath };
                            lock (Gate) { cache[key] = entry; cacheChanged = true; }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Dbg.Warn("[TRAKT] title: " + ex.Message); }
                }

                bool series = title.Kind == "tv";
                int? year = entry?.Year ?? title.Year;
                string? path = Match(library, series, year, title.Name, entry?.Title, entry?.OriginalTitle);
                string? poster = path != null ? MovieMetadataService.GetCachedPosterPath(path) : null;
                if ((poster == null || !File.Exists(poster)) && entry?.PosterPath != null)
                {
                    string file = Path.Combine(PosterFolder, title.Kind + "-" + title.TmdbId.ToString(CultureInfo.InvariantCulture) + ".jpg");
                    poster = MovieMetadataService.TryDownloadTmdbPoster(entry.PosterPath, file, token) ? file : null;
                }
                var ready = new Recommendation(title, string.IsNullOrWhiteSpace(entry?.Title) ? title.Name : entry!.Title, year, poster, path);
                resolved[title.TmdbId] = ready;
                progress?.Report(ready);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            if (cacheChanged) lock (Gate) Store("trakt-titles.json", cache);
            // L'ordine di Trakt e' l'ordine di rilevanza: va mantenuto.
            return titles.Where(t => resolved.ContainsKey(t.TmdbId)).Select(t => resolved[t.TmdbId]).ToList();
        }

        // ----- Film affini a un film (scheda dettagli) -----

        private sealed class RelatedEntry
        {
            public string Name { get; set; } = "";
            public int? Year { get; set; }
            public DateTime FetchedUtc { get; set; }
        }

        /// <summary>I film della libreria che Trakt considera affini al film dato, dal piu' affine. Il risultato di
        /// Trakt resta in cache un mese; senza rete o senza chiavi l'elenco e' vuoto.</summary>
        public static async Task<List<string>> RelatedInLibraryAsync(int tmdbId, IReadOnlyList<LibraryTitle> library, CancellationToken ct)
        {
            Dictionary<string, List<RelatedEntry>> cache;
            lock (Gate) cache = Load<List<RelatedEntry>>("trakt-related.json");
            string key = tmdbId.ToString(CultureInfo.InvariantCulture);
            if (!cache.TryGetValue(key, out var related) || related.Count == 0 || DateTime.UtcNow - related[0].FetchedUtc > TimeSpan.FromDays(30))
            {
                if (!TraktClient.IsAvailable) return new List<string>();
                var titles = await TraktClient.RelatedMoviesAsync(tmdbId, ct).ConfigureAwait(false);
                related = titles.Select(t => new RelatedEntry { Name = t.Name, Year = t.Year, FetchedUtc = DateTime.UtcNow }).ToList();
                if (related.Count > 0)
                    lock (Gate)
                    {
                        cache = Load<List<RelatedEntry>>("trakt-related.json");
                        cache[key] = related;
                        Store("trakt-related.json", cache);
                    }
            }
            return related.Select(entry => Match(library, false, entry.Year, entry.Name)).Where(path => path != null).Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ----- Diario -----

        // Il diario contiene anche musica e flussi web: a Trakt interessano solo i file video.
        private static readonly HashSet<string> NotVideo = new(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma", ".alac", ".ape", ".aiff", ".dsf", ".jpg", ".jpeg", ".png", ".gif", ".webp", "" };

        private static bool IsVideoFile(string path) =>
            !path.Contains("://", StringComparison.Ordinal) && !NotVideo.Contains(Path.GetExtension(path));

        private static IEnumerable<WatchHistoryStore.Entry> Candidates(DateTime? onlyAfterUtc, Dictionary<string, long> sent) =>
            WatchHistoryStore.LoadAll().Where(entry => IsVideoFile(entry.MediaPath) &&
                (onlyAfterUtc == null || entry.LastWatchedAtUtc > onlyAfterUtc) &&
                !(sent.TryGetValue(entry.MediaPath, out long ticks) && ticks >= entry.LastWatchedAtUtc.Ticks));

        /// <summary>Quanti titoli finiti del diario non sono ancora stati inviati a Trakt da questo PC.</summary>
        public static int PendingCount()
        {
            lock (Gate) return Candidates(null, Load<long>("trakt-sent.json")).Count();
        }

        /// <summary>Invia a Trakt i titoli finiti del diario (con il voto, se c'e'). Cio' che Trakt segna gia' come visto
        /// non viene ripetuto. <paramref name="onlyAfterUtc"/> limita l'invio alle visioni piu' recenti di quel momento.</summary>
        public static async Task<TraktClient.SyncResult> SendDiaryAsync(DateTime? onlyAfterUtc, string language, CancellationToken ct)
        {
            Dictionary<string, long> sent;
            Dictionary<string, IdEntry> ids;
            lock (Gate)
            {
                sent = Load<long>("trakt-sent.json");
                ids = Load<IdEntry>("trakt-ids.json");
            }
            var candidates = Candidates(onlyAfterUtc, sent).ToList();
            if (candidates.Count == 0) return new TraktClient.SyncResult(0, 0, 0);

            bool idsChanged = false;
            var known = new List<(WatchHistoryStore.Entry Entry, IdEntry Id)>();
            foreach (var entry in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!ids.TryGetValue(entry.MediaPath, out IdEntry? id))
                {
                    id = ResolveId(entry, language, ct);
                    if (id == null) continue;
                    ids[entry.MediaPath] = id;
                    idsChanged = true;
                }
                known.Add((entry, id));
            }
            if (idsChanged) lock (Gate) Store("trakt-ids.json", ids);

            var watchedMovies = known.Any(k => !k.Id.Episode) ? await TraktClient.WatchedMoviesAsync(ct).ConfigureAwait(false) : new HashSet<int>();
            var watchedEpisodes = known.Any(k => k.Id.Episode) ? await TraktClient.WatchedEpisodesAsync(ct).ConfigureAwait(false) : new HashSet<TraktClient.WatchedEpisode>();

            var items = new List<TraktClient.HistoryItem>();
            foreach (var (entry, id) in known)
            {
                // Mai inviato da qui ma gia' visto su Trakt: e' la stessa visione, non una nuova.
                bool alreadyThere = !sent.ContainsKey(entry.MediaPath) &&
                    (id.Episode ? watchedEpisodes.Contains(new TraktClient.WatchedEpisode(id.Tmdb, id.Season, id.Number)) : watchedMovies.Contains(id.Tmdb));
                if (!alreadyThere)
                    items.Add(new TraktClient.HistoryItem(id.Episode, id.Tmdb, id.Season, id.Number, entry.LastWatchedAtUtc, (int)Math.Round(entry.Rating * 2)));
            }
            // Lo stesso film in due file (due edizioni) conta una volta.
            items = items.GroupBy(i => (i.IsEpisode, i.TmdbId, i.Season, i.Episode)).Select(g => g.OrderByDescending(i => i.WatchedAtUtc).First()).ToList();

            var result = await TraktClient.AddHistoryAsync(items, ct).ConfigureAwait(false);
            // Anche i titoli non riconosciuti si considerano fatti: riproporli a ogni invio non li farebbe riconoscere.
            foreach (var entry in candidates) sent[entry.MediaPath] = entry.LastWatchedAtUtc.Ticks;
            lock (Gate) Store("trakt-sent.json", sent);
            Dbg.Log($"[TRAKT] diary: {known.Count} recognised of {candidates.Count}, added {result.Movies} movies, {result.Episodes} episodes, {result.Ratings} ratings", Dbg.LogLevel.Info);
            return result;
        }

        private static IdEntry? ResolveId(WatchHistoryStore.Entry entry, string language, CancellationToken ct)
        {
            try
            {
                // Un indirizzo di rete non ha un nome di file da cui riconoscere il titolo ("stream.mkv",
                // "file.mkv"): cercarlo per nome segnerebbe su Trakt un film qualunque. Vale solo cio' che
                // il server dichiara: per i film Jellyfin l'identificativo TMDb arriva dal catalogo.
                if (Uri.TryCreate(entry.MediaPath, UriKind.Absolute, out Uri? address) && !address.IsFile)
                    return JellyfinClient.RememberedMovieTmdbId(entry.MediaPath) is int known ? new IdEntry { Tmdb = known } : null;
                var rich = MovieMetadataService.ResolveRichMetadata(entry.MediaPath, entry.DurationSeconds > 1 ? entry.DurationSeconds : null, language, ct);
                if (rich.TmdbId is not int tmdb) return null;
                if (!string.Equals(rich.MediaType, "tv", StringComparison.OrdinalIgnoreCase)) return new IdEntry { Tmdb = tmdb };
                var parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(entry.MediaPath);
                return parsed.SeasonNumber is int season && parsed.EpisodeNumber is int number
                    ? new IdEntry { Episode = true, Tmdb = tmdb, Season = season, Number = number }
                    : null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn("[TRAKT] id: " + ex.Message); return null; }
        }
    }
}
