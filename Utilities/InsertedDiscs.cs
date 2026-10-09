#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// I dischi nel lettore visti dalla libreria: finche' un disco e' inserito compare tra i film (DVD, Blu-ray)
    /// o tra gli album (CD audio), e sparisce quando esce. Ogni disco video ha un piccolo file ".cinedisc" con il
    /// titolo nel nome, cosi' la libreria lo tratta come un film qualsiasi (titolo, locandina, ricerca); i CD usano
    /// i file ".cdda" delle loro tracce. La lettura dei lettori avviene in secondo piano: chi chiede l'elenco
    /// riceve subito l'ultimo noto.
    /// </summary>
    internal static class InsertedDiscs
    {
        public const string Extension = ".cinedisc";

        private static readonly object Sync = new();
        private static List<string> _movies = new(), _music = new();
        private static long _refreshedAt = -100000;
        private static int _refreshing;

        /// <summary>L'elenco e' cambiato (disco inserito, tolto, o CD appena riconosciuto).</summary>
        public static event Action? Changed;

        private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "inserted-discs");

        /// <summary>Percorsi da aggiungere alla categoria ("Movies" o "Music"); vuoto per le altre.</summary>
        public static IReadOnlyList<string> Paths(string category)
        {
            bool movies = string.Equals(category, "Movies", StringComparison.OrdinalIgnoreCase);
            bool music = string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase);
            if (!movies && !music) return Array.Empty<string>();
            EnsureFirstRefresh();
            lock (Sync) return movies ? _movies.ToList() : _music.ToList();
        }

        private static (string Title, string Root)? _first;

        /// <summary>Il disco nel lettore, per la voce della barra laterale: titolo e radice dell'unita'; null se non c'e'.</summary>
        public static (string Title, string Root)? Current
        {
            get
            {
                EnsureFirstRefresh();
                lock (Sync) return _first;
            }
        }

        private static int _started;
        private static int _pending;

        // Il lettore si interroga una volta all'avvio e poi solo quando Windows segnala un cambiamento: interrogarlo
        // a intervalli lo teneva sveglio e disturbava il disco in riproduzione.
        private static void EnsureFirstRefresh()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0) RefreshSoon();
        }

        /// <summary>Da chiamare quando Windows segnala un cambiamento dei dispositivi (arriva a raffiche: se ne fa una sola lettura).</summary>
        public static void Invalidate()
        {
            Interlocked.Exchange(ref _started, 1);
            if (Interlocked.Exchange(ref _pending, 1) != 0) return;
            Task.Run(async () =>
            {
                // Collegare un dispositivo qualsiasi (una scheda audio, una chiavetta) manda piu' avvisi in pochi istanti.
                await Task.Delay(1500).ConfigureAwait(false);
                Interlocked.Exchange(ref _pending, 0);
                RefreshSoon();
            });
        }

        private static void RefreshSoon()
        {
            if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
            Task.Run(() =>
            {
                try { Refresh(); }
                catch (Exception ex) { Dbg.Warn("[DISC] library: " + ex.Message); }
                finally { Interlocked.Exchange(ref _refreshedAt, Environment.TickCount64); Interlocked.Exchange(ref _refreshing, 0); }
            });
        }

        private static void Refresh()
        {
            var movies = new List<string>();
            var music = new List<string>();
            (string Title, string Root)? first = null;
            Directory.CreateDirectory(Folder);
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.CDRom || !drive.IsReady) continue;
                    string root = drive.RootDirectory.FullName;
                    if (AudioCd.IsAudioCdPath(root, out char letter, out _))
                    {
                        // La prima volta chiede titoli e copertina a MusicBrainz; poi e' immediato.
                        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        var cdTracks = AudioCd.Prepare(letter, limit.Token);
                        music.AddRange(cdTracks);
                        if (first == null && cdTracks.Count > 0)
                        {
                            string album = MediaProbe.ReadAudioTags(cdTracks[0]).Album;
                            first = (string.IsNullOrWhiteSpace(album) ? (AppLanguage.English ? "Audio CD" : "CD audio") : album, root);
                        }
                        continue;
                    }
                    if (!DiscMedia.TryResolve(root, out _)) continue;
                    string title = TitleOf(root, drive);
                    string file = Path.Combine(Folder, title + Extension);
                    File.WriteAllText(file, root);
                    movies.Add(file);
                    first ??= (title, root);
                }
                catch (Exception ex) { Dbg.Log("[DISC] library drive: " + ex.Message, Dbg.LogLevel.Info); }
            }
            // I file dei dischi non piu' presenti si tolgono: in libreria restano solo quelli nel lettore.
            try
            {
                foreach (string old in Directory.EnumerateFiles(Folder, "*" + Extension))
                    if (!movies.Contains(old, StringComparer.OrdinalIgnoreCase)) { try { File.Delete(old); } catch { } }
            }
            catch { }

            bool changed;
            lock (Sync)
            {
                changed = !_movies.SequenceEqual(movies, StringComparer.OrdinalIgnoreCase) || !_music.SequenceEqual(music, StringComparer.OrdinalIgnoreCase) || _first != first;
                _movies = movies; _music = music; _first = first;
            }
            if (changed)
            {
                Dbg.Log($"[DISC] in the library: {movies.Count} video disc(s), {music.Count} CD track(s)", Dbg.LogLevel.Info);
                try { Changed?.Invoke(); } catch { }
            }
        }

        /// <summary>L'etichetta del disco scritta come un titolo ("THE_LEGO_MOVIE_2" diventa "The Lego Movie 2").</summary>
        private static string TitleOf(string root, DriveInfo drive)
        {
            string label = (DiscMedia.DisplayName(root) ?? "").Replace('_', ' ').Trim();
            if (label.EndsWith(":\\", StringComparison.Ordinal)) label = "";
            if (label.Length > 0 && label == label.ToUpperInvariant())
                label = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label.ToLowerInvariant());
            if (label.Length == 0) label = (AppLanguage.English ? "Disc " : "Disco ") + drive.Name.TrimEnd('\\').TrimEnd(':');
            return string.Concat(label.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? ' ' : c)).Trim();
        }

        /// <summary>Per un file ".cinedisc" restituisce il disco a cui punta (la radice del lettore).</summary>
        public static bool TryResolve(string? path, out string target)
        {
            target = "";
            try
            {
                if (path == null || !path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
                target = File.ReadAllText(path).Trim();
                return target.Length > 0;
            }
            catch { return false; }
        }
    }
}
