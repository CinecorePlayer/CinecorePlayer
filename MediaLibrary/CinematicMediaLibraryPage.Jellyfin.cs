#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    // Jellyfin nella pagina Rete: i server compaiono accanto a quelli DLNA, ma il catalogo
    // arriva dall'API del server (titoli, trame, generi, locandine, punto di ripresa) e non
    // da una visita delle cartelle. Una volta letto, il catalogo e' un normale elenco di
    // elementi di rete: categorie, ricerca, schede e coda funzionano come per gli altri server.
    internal sealed partial class CinematicMediaLibraryPage
    {
        private const string JellyfinProtocol = "Jellyfin";
        private const string JellyfinIdPrefix = "jellyfin:";

        /// <summary>Mostra la scheda di accesso (per il server dato, o chiedendo l'indirizzo se null)
        /// e restituisce l'account, oppure null se l'utente rinuncia. La imposta la finestra del player.</summary>
        internal Func<JellyfinClient.Server?, JellyfinClient.Account?>? JellyfinSignIn;

        private static bool IsJellyfinServer(NetworkServerViewItem? server) =>
            server != null && string.Equals(server.Protocol, JellyfinProtocol, StringComparison.OrdinalIgnoreCase);

        private static string JellyfinServerId(NetworkServerViewItem server) =>
            server.DeviceId.StartsWith(JellyfinIdPrefix, StringComparison.OrdinalIgnoreCase) ? server.DeviceId[JellyfinIdPrefix.Length..] : server.DeviceId;

        private static NetworkServerViewItem JellyfinServerItem(JellyfinClient.Server server, bool available)
        {
            string host = Uri.TryCreate(server.Address, UriKind.Absolute, out Uri? uri) ? uri.Host : server.Address;
            return new NetworkServerViewItem
            {
                DeviceId = JellyfinIdPrefix + server.Id,
                Name = server.Name,
                Model = string.IsNullOrWhiteSpace(server.Version) ? "Jellyfin" : "Jellyfin " + server.Version,
                Host = host,
                Location = server.Address,
                ResourceBaseUrl = server.Address,
                ContentDirectoryControlUrl = string.Empty,
                Protocol = JellyfinProtocol,
                Available = available,
                SeenAt = DateTime.Now
            };
        }

        /// <summary>Server che rispondono all'annuncio in rete, piu' quelli a cui si e' gia' collegati
        /// (anche fuori dalla rete locale, aggiunti a mano), se raggiungibili.</summary>
        private static async Task<List<NetworkServerViewItem>> DiscoverJellyfinServersAsync(TimeSpan timeout)
        {
            var result = new Dictionary<string, JellyfinClient.Server>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (JellyfinClient.Server found in await JellyfinClient.DiscoverAsync(timeout).ConfigureAwait(false))
                    result[found.Id] = found;
            }
            catch { }

            var probes = result.Values.Select(server => server.Address)
                .Concat(JellyfinClient.Accounts.Where(account => !result.ContainsKey(account.ServerId)).Select(account => account.Address))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(async address =>
                {
                    try { return await JellyfinClient.GetServerAsync(address, CancellationToken.None).ConfigureAwait(false); }
                    catch { return null; }
                }).ToArray();
            foreach (JellyfinClient.Server? server in await Task.WhenAll(probes).ConfigureAwait(false))
            {
                if (server == null) continue;
                result[server.Id] = server;
                JellyfinClient.UpdateAddress(server.Id, server.Address);
            }
            return result.Values.Select(server => JellyfinServerItem(server, available: true)).ToList();
        }

        /// <summary>Accesso a Jellyfin: dal pulsante "Aggiungi Jellyfin" (server null: si chiede l'indirizzo)
        /// o da Connetti su un server senza account. Dopo l'accesso il server viene selezionato e letto.</summary>
        private void SignInToJellyfin(NetworkServerViewItem? server)
        {
            if (JellyfinSignIn == null || _networkContentLoading) return;
            JellyfinClient.Server? known = server == null ? null
                : new JellyfinClient.Server(JellyfinServerId(server), server.Name, server.Location, server.Model.Replace("Jellyfin", "", StringComparison.OrdinalIgnoreCase).Trim());
            JellyfinClient.Account? account;
            try { account = JellyfinSignIn(known); }
            catch (Exception ex) { Dbg.Warn("[JELLYFIN] sign-in: " + ex.Message); return; }
            if (account == null || IsDisposed) return;

            var item = JellyfinServerItem(new JellyfinClient.Server(account.ServerId, account.ServerName, account.Address, known?.Version ?? ""), available: true);
            string key = NetworkServerKey(item);
            int index = _networkServers.FindIndex(existing => string.Equals(NetworkServerKey(existing), key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _networkServers[index] = item;
            else { _networkServers.Add(item); index = _networkServers.Count - 1; }
            _networkSelectionIndex = index;
            SaveNetworkServers();
            ConnectSelectedNetworkServer(force: true);
        }

        private static async Task<List<LibraryItem>> BrowseJellyfinServerItemsAsync(NetworkServerViewItem server, CancellationToken ct, Action<List<LibraryItem>>? progress)
        {
            JellyfinClient.Account account = JellyfinClient.FindAccount(JellyfinServerId(server)) ?? throw new JellyfinClient.UnauthorizedException();
            // L'indirizzo scoperto adesso vale piu' di quello salvato (il server puo' aver cambiato IP).
            // A saved network entry can carry the previous DHCP address. Only replace the
            // account endpoint with an address confirmed by the current discovery pass.
            if (server.Available && !string.IsNullOrWhiteSpace(server.Location) && !string.Equals(server.Location, account.Address, StringComparison.OrdinalIgnoreCase))
            {
                JellyfinClient.UpdateAddress(account.ServerId, server.Location);
                account = JellyfinClient.FindAccount(account.ServerId) ?? account;
            }

            long lastProgress = 0;
            List<JellyfinClient.Item> items = await JellyfinClient.LoadItemsAsync(account, partial =>
            {
                if (progress == null || Environment.TickCount64 - lastProgress < 700) return;
                lastProgress = Environment.TickCount64;
                progress(partial.Select((item, order) => BuildJellyfinLibraryItem(account, item, order)).ToList());
            }, ct).ConfigureAwait(false);

            var result = new List<LibraryItem>(items.Count);
            var titles = new List<(string Url, string Title, string Category, string FileName, int? MovieTmdbId)>(items.Count);
            string prefix = account.Address.TrimEnd('/') + "/";
            foreach (string stale in JellyfinResume.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                JellyfinResume.TryRemove(stale, out _);
            for (int order = 0; order < items.Count; order++)
            {
                JellyfinClient.Item item = items[order];
                LibraryItem built = BuildJellyfinLibraryItem(account, item, order);
                result.Add(built);
                string display = JellyfinDisplayTitle(item);
                titles.Add((built.Path, display, built.PlaybackCategory, item.FileName, built.TmdbId));
                PlaybackTitleHints.Set(built.Path, display, built.PlaybackCategory, item.Width, item.Height);
                if (item.Type != "Audio" && !item.Played && item.ResumeSeconds > 0 && item.DurationSeconds > 0)
                    JellyfinResume[built.Path] = (item.ResumeSeconds, item.DurationSeconds, (item.LastPlayedUtc ?? DateTime.UtcNow).ToLocalTime());
            }
            JellyfinClient.RememberTitles(account.Address, titles);
            Dbg.Log($"[JELLYFIN] catalog from '{account.ServerName}': {result.Count} items", Dbg.LogLevel.Info);
            return result;
        }

        private static string JellyfinDisplayTitle(JellyfinClient.Item item)
        {
            if (item.Type == "Episode" && !string.IsNullOrWhiteSpace(item.SeriesName))
            {
                string number = item.SeasonNumber.HasValue && item.EpisodeNumber.HasValue
                    ? $" S{item.SeasonNumber.Value:00}E{item.EpisodeNumber.Value:00}" : string.Empty;
                return string.IsNullOrWhiteSpace(item.Name) ? item.SeriesName + number : $"{item.SeriesName}{number} – {item.Name}";
            }
            return item.Name;
        }

        // Punto di ripresa dei titoli Jellyfin: lo tiene il server (il registro locale delle riprese
        // riguarda solo i file), cosi' un film iniziato sulla TV si riprende da li' anche sul PC.
        // Qui se ne tiene una copia in memoria: arriva con il catalogo e si aggiorna quando il
        // player finisce di riprodurre, con le stesse regole del server.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double Position, double Duration, DateTime SavedAt)> JellyfinResume =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Il player ha smesso di riprodurre un titolo Jellyfin a questa posizione.</summary>
        internal static void NoteJellyfinPlayback(string url, double position, double duration)
        {
            if (string.IsNullOrWhiteSpace(url) || duration <= 0) return;
            // Come fa il server: niente ripresa per i video sotto i cinque minuti, ne' prima del 5% o dopo il 90%.
            double share = position / duration;
            if (duration < 300 || share < 0.05 || share > 0.90) JellyfinResume.TryRemove(url, out _);
            else JellyfinResume[url] = (position, duration, DateTime.Now);
        }

        private static void AddJellyfinResumeItems(IReadOnlyDictionary<string, LibraryItem> byPath, List<ResumeItem> result)
        {
            foreach (var pair in JellyfinResume)
                if (byPath.TryGetValue(pair.Key, out LibraryItem? item) && !result.Any(existing => string.Equals(existing.Item.Path, pair.Key, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new ResumeItem { Item = item, PositionSeconds = pair.Value.Position, DurationSeconds = pair.Value.Duration, SavedAt = pair.Value.SavedAt });
        }

        // Scheda dettagli: di un titolo Jellyfin si mostrano il nome vero del file e il server, non l'indirizzo del flusso.
        private static string ItemFileName(LibraryItem item) => JellyfinClient.RememberedFileName(item.Path) ?? FileNameForDisplay(item.Path);

        private static string ItemLocation(LibraryItem item)
        {
            if (JellyfinClient.TryParseStream(item.Path, out JellyfinClient.Account account, out _, out _))
                return "Jellyfin · " + account.ServerName;
            return CompactPath(item.Path, 38);
        }

        private static string ItemSizeText(LibraryItem item)
        {
            if (item.Bytes <= 0 || !IsNetworkPath(item.Path)) return FormatFileSize(item.Path);
            double bytes = item.Bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return $"{bytes:0.#} {units[unit]}";
        }

        private static string? JellyfinAudioLabel(JellyfinClient.Item item, bool music)
        {
            string codec = (item.AudioCodec ?? string.Empty).ToLowerInvariant();
            if (music)
            {
                string name = codec switch
                {
                    "" => "Jellyfin",
                    "mp3" or "flac" or "aac" or "alac" or "opus" or "wav" or "dsd" => codec.ToUpperInvariant(),
                    "vorbis" => "Vorbis",
                    _ when codec.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
                    _ => codec.ToUpperInvariant()
                };
                if (item.AudioSampleRate > 0 && codec is "flac" or "alac" or "wav" || codec.StartsWith("pcm", StringComparison.Ordinal))
                {
                    string rate = (item.AudioSampleRate / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " kHz";
                    return item.AudioBitDepth > 0 ? $"{name} · {rate} · {item.AudioBitDepth}-bit" : $"{name} · {rate}";
                }
                return name;
            }
            string? family = codec switch
            {
                "truehd" => item.ObjectAudio ? "Dolby Atmos" : "Dolby TrueHD",
                "eac3" => item.ObjectAudio ? "Dolby Atmos" : "Dolby Digital+",
                "ac3" => "Dolby Digital",
                "dts" => item.ObjectAudio ? "DTS:X"
                    : (item.AudioProfile ?? "").Contains("MA", StringComparison.OrdinalIgnoreCase) ? "DTS-HD MA"
                    : (item.AudioProfile ?? "").Contains("HD", StringComparison.OrdinalIgnoreCase) ? "DTS-HD"
                    : "DTS",
                "dca" => "DTS",
                "flac" => "FLAC",
                "aac" => "AAC",
                "opus" => "Opus",
                "mp3" => "MP3",
                _ when codec.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
                _ => null
            };
            string? layout = item.AudioChannels switch { >= 8 => "7.1", >= 6 => "5.1", 2 => "2.0", 1 => "1.0", _ => null };
            return family != null && layout != null && !item.ObjectAudio ? family + " " + layout : family ?? layout;
        }

        private static LibraryItem BuildJellyfinLibraryItem(JellyfinClient.Account account, JellyfinClient.Item item, int order)
        {
            string category = item.Type switch { "Movie" => "Movies", "Episode" => "TV Series", "Audio" => "Music", _ => "Videos" };
            bool music = category == "Music";
            string Image(string id, string kind, int width) => JellyfinClient.ImageUrl(account.Address, id, kind, width);

            string? poster = category switch
            {
                "TV Series" when item.SeriesHasPrimaryImage && item.SeriesId != null => Image(item.SeriesId, "Primary", 600),
                "Music" when item.AlbumHasPrimaryImage && item.AlbumId != null => Image(item.AlbumId, "Primary", 600),
                _ => item.HasPrimaryImage ? Image(item.Id, "Primary", 600) : null
            };
            string? wide = item.BackdropItemId != null ? Image(item.BackdropItemId, "Backdrop", 1920)
                : category is "TV Series" or "Videos" && item.HasPrimaryImage ? Image(item.Id, "Primary", 1280)
                : null;
            var size = (item.Width, item.Height);

            return new LibraryItem
            {
                Path = JellyfinClient.StreamUrl(account.Address, item),
                Title = string.IsNullOrWhiteSpace(item.Name) ? "Jellyfin" : item.Name,
                Category = category,
                PlaybackCategory = PlaybackCategoryForDisplayCategory(category),
                Year = item.Year,
                DurationMinutes = item.DurationSeconds > 0 ? item.DurationSeconds / 60.0 : null,
                SortDateUtc = item.DateCreatedUtc > DateTime.MinValue.AddYears(1) ? item.DateCreatedUtc : DateTime.UtcNow.AddSeconds(-order),
                ArtPath = poster ?? (music ? null : wide),
                WideArtPath = wide ?? poster,
                Overview = item.Overview ?? string.Empty,
                Genres = item.Genres.ToList(),
                Tagline = item.Tagline,
                Rating = item.Rating,
                // L'identificativo TMDb di un episodio non e' quello della serie: vale solo per i film.
                TmdbId = category == "Movies" ? item.TmdbId : null,
                ImdbId = category == "Movies" ? item.ImdbId : null,
                AudioLabel = JellyfinAudioLabel(item, music),
                ResolutionLabel = music ? null : DlnaResolutionLabel(size),
                SeriesTitle = category == "TV Series" ? item.SeriesName : null,
                SeasonNumber = category == "TV Series" ? item.SeasonNumber : null,
                EpisodeNumber = item.EpisodeNumber,
                AlbumTitle = music ? item.Album : null,
                ArtistName = music ? item.Artist ?? item.AlbumArtist : null,
                AlbumArtist = music ? item.AlbumArtist ?? item.Artist : null,
                TrackNumber = item.TrackNumber,
                Is4K = !music && IsDlna4K(size),
                IsHdr = item.Hdr,
                HasAtmos = item.ObjectAudio,
                Bytes = Math.Max(0, item.Bytes)
            };
        }
    }
}
