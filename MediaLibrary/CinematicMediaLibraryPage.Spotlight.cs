#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        internal IReadOnlyList<(string Key, string Name)> GetSpotlightServerChoices()
        {
            var networkChoices = _networkServers
                .Where(server => IsJellyfinServer(server)
                    ? JellyfinClient.FindAccount(JellyfinServerId(server)) != null
                    : !string.IsNullOrWhiteSpace(server.ContentDirectoryControlUrl))
                .Select(server => (Key: NetworkServerKey(server), Name: IsJellyfinServer(server) ? JellyfinClient.ProductOfLabel(server.Model) + ": " + server.Name : server.Name));
            var savedJellyfinChoices = JellyfinClient.Accounts.Select(account =>
            {
                var server = JellyfinServerItem(
                    new JellyfinClient.Server(account.ServerId, account.ServerName, account.Address, string.Empty, account.Product),
                    available: true);
                return (Key: NetworkServerKey(server), Name: account.Product + ": " + server.Name);
            });

            return networkChoices
                .Concat(savedJellyfinChoices)
                .Where(server => !string.IsNullOrWhiteSpace(server.Key) && !string.IsNullOrWhiteSpace(server.Name))
                .DistinctBy(server => server.Key, StringComparer.OrdinalIgnoreCase)
                .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal void RefreshSpotlightServerChoices(Action completed, bool force = false)
        {
            if (_networkDiscoveryInProgress)
            {
                var wait = new System.Windows.Forms.Timer { Interval = 180 };
                wait.Tick += (_, __) =>
                {
                    if (IsDisposed)
                    {
                        wait.Stop();
                        wait.Dispose();
                        return;
                    }
                    if (_networkDiscoveryInProgress)
                        return;
                    wait.Stop();
                    wait.Dispose();
                    try { completed(); } catch { }
                };
                wait.Start();
                return;
            }

            if (!force && _networkServers.Count > 0 && DateTime.Now - _networkLastRefresh < TimeSpan.FromMinutes(2))
            {
                try { completed(); } catch { }
                return;
            }

            RefreshNetworkServers(force: true, afterMerge: completed);
        }

        /// <summary>
        /// Il catalogo del server di rete per il telecomando sul telefono: gli stessi titoli che
        /// Spotlight mostra in modalita' Rete, divisi in film e serie.
        /// </summary>
        internal async Task<IReadOnlyList<RemoteLibraryCategoryView>> BuildRemoteNetworkLibraryAsync(string serverKey, CancellationToken cancellationToken)
        {
            List<NetflixModeItem> items = await LoadSpotlightItemsFromNetworkServerAsync(serverKey, cancellationToken).ConfigureAwait(false);
            // Titolo pulito e locandina gia' noti alla libreria (il server spesso espone il nome del file).
            foreach (NetflixModeItem item in items)
                try { PopulateSpotlightItemFromCache(item); } catch { }
            var result = new List<RemoteLibraryCategoryView>();
            foreach (string key in new[] { "Movies", "TV Series" })
            {
                bool wantSeries = key == "TV Series";
                var views = items
                    .Where(item => (item.Category.Contains("Serie", StringComparison.OrdinalIgnoreCase) || item.Category.Contains("TV", StringComparison.OrdinalIgnoreCase)) == wantSeries)
                    .Select(item =>
                    {
                        string art = item.PosterPath ?? string.Empty;
                        try
                        {
                            if (Uri.TryCreate(art, UriKind.Absolute, out Uri? artUri) && (artUri.Scheme == Uri.UriSchemeHttp || artUri.Scheme == Uri.UriSchemeHttps))
                                art = ResolveDisplayImagePath(art) ?? string.Empty;
                        }
                        catch { art = string.Empty; }
                        return new RemoteLibraryItemView
                        {
                            Path = item.Path,
                            Title = FirstNonEmpty(item.Title, item.Path),
                            Subtitle = item.YearText ?? string.Empty,
                            ArtPath = art,
                            Kind = "item"
                        };
                    })
                    .OrderBy(view => view.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                result.Add(new RemoteLibraryCategoryView { Key = key, Label = LocalizedCategoryName(key), Count = views.Count, Items = views });
            }
            result.Add(new RemoteLibraryCategoryView { Key = "Music", Label = LocalizedCategoryName("Music"), Count = 0, Items = new List<RemoteLibraryItemView>() });
            return result;
        }

        internal List<NetflixModeItem> GetLoadedSpotlightNetworkItems(string serverKey)
        {
            if (string.IsNullOrWhiteSpace(serverKey) ||
                !string.Equals(_networkConnectedServerKey, serverKey, StringComparison.OrdinalIgnoreCase))
                return new List<NetflixModeItem>();
            return CreateSpotlightNetworkItems(_networkItems);
        }

        internal async Task<List<NetflixModeItem>> LoadSpotlightItemsFromNetworkServerAsync(string serverKey, CancellationToken cancellationToken)
        {
            NetworkServerViewItem? server = _networkServers.FirstOrDefault(candidate =>
                string.Equals(NetworkServerKey(candidate), serverKey, StringComparison.OrdinalIgnoreCase));
            const string jellyfinKeyPrefix = "udn:jellyfin:";
            if (server == null && serverKey.StartsWith(jellyfinKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                JellyfinClient.Account? account = JellyfinClient.FindAccount(serverKey[jellyfinKeyPrefix.Length..]);
                if (account != null)
                    server = JellyfinServerItem(new JellyfinClient.Server(account.ServerId, account.ServerName, account.Address, string.Empty, account.Product), available: true);
            }
            if (server == null)
                throw new InvalidOperationException(L("Il server selezionato non è più disponibile.", "The selected server is no longer available."));

            List<LibraryItem> items;
            if (IsJellyfinServer(server))
                items = await BrowseJellyfinServerItemsAsync(server, cancellationToken, progress: null).ConfigureAwait(false);
            else if (string.IsNullOrWhiteSpace(server.ContentDirectoryControlUrl))
                throw new InvalidOperationException(L("Il server DLNA selezionato non espone un catalogo leggibile.", "The selected DLNA server does not expose a readable catalogue."));
            else if (string.Equals(_networkConnectedServerKey, serverKey, StringComparison.OrdinalIgnoreCase) && _networkItems.Count > 0)
                items = _networkItems.ToList();
            else
                items = await BrowseDlnaServerItemsAsync(server, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return CreateSpotlightNetworkItems(items);
        }

        private List<NetflixModeItem> CreateSpotlightNetworkItems(IEnumerable<LibraryItem> source)
        {
            var result = new List<NetflixModeItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LibraryItem item in source)
            {
                if (!IsNetworkVideoCategory(item.Category) || string.IsNullOrWhiteSpace(item.Path))
                    continue;

                bool series = string.Equals(item.Category, "TV Series", StringComparison.OrdinalIgnoreCase) ||
                              !string.IsNullOrWhiteSpace(item.SeriesTitle);
                string title = FirstNonEmpty(item.SeriesTitle, item.Title, item.Path);
                string metadataKey = ResolveMetadataLookupKey(item.Path, title, item.Year);
                string key = series
                    ? "series:" + metadataKey.Trim()
                    : "movie:" + item.Path + "|" + metadataKey.Trim();
                if (!seen.Add(key))
                    continue;

                string formatPath = Uri.TryCreate(item.Path, UriKind.Absolute, out Uri? mediaUri) &&
                                    (mediaUri.Scheme == Uri.UriSchemeHttp || mediaUri.Scheme == Uri.UriSchemeHttps)
                    ? mediaUri.AbsolutePath
                    : item.Path;
                // Spotlight deve usare artwork TMDb; il thumb DLNA è solo una
                // sorgente a bassa risoluzione e può perfino riferirsi a un altro
                // elemento quando il server riusa gli URL delle immagini.
                string? tmdbPoster = null;
                string? tmdbBackdrop = null;
                try
                {
                    if (item.Year.HasValue)
                    {
                        tmdbPoster = MovieMetadataService.GetCachedPosterPath(metadataKey);
                        tmdbBackdrop = MovieMetadataService.GetCachedBackdropPath(metadataKey);
                    }
                    else
                    {
                        tmdbPoster = ResolvePosterPath(item.Path, metadataKey, title, allowNearbyArt: false);
                        tmdbBackdrop = ResolveBackdropPath(item.Path, metadataKey, title, allowNearbyArt: false);
                    }
                }
                catch { }
                bool fromJellyfin = JellyfinClient.TryParseStream(item.Path, out _, out _, out _);
                // Di una serie la scheda e' la serie intera: la trama del singolo episodio non la descrive.
                bool hasServerText = fromJellyfin && !series && !string.IsNullOrWhiteSpace(item.Overview);
                result.Add(new NetflixModeItem
                {
                    Path = item.Path,
                    MetadataKey = metadataKey,
                    Category = series ? L("Serie TV", "TV Series") : L("Film", "Movie"),
                    Title = title,
                    YearText = item.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    QualityLabel = QualityLabel(item),
                    AudioLabel = item.AudioLabel,
                    FormatLabel = VideoFormatLabel(formatPath),
                    // Un server Jellyfin descrive gia' i suoi titoli (trama, generi, voto, immagini): si
                    // mostrano subito quelli. TMDb resta la riserva per cio' che manca e per i server
                    // DLNA, che di solito danno solo il nome del file.
                    Overview = hasServerText ? item.Overview
                        : series
                            ? L("Serie TV dal server selezionato. Dettagli TMDb in caricamento.", "TV series from the selected server. Loading TMDb details.")
                            : L("Film dal server selezionato. Dettagli TMDb in caricamento.", "Movie from the selected server. Loading TMDb details."),
                    Genres = fromJellyfin ? item.Genres.Take(4).ToList() : new List<string>(),
                    Tagline = fromJellyfin ? item.Tagline : null,
                    Rating = fromJellyfin ? item.Rating : null,
                    ServerMetadata = hasServerText,
                    BackdropPath = tmdbBackdrop ?? (fromJellyfin && !series ? item.WideArtPath : null),
                    PosterPath = tmdbPoster ?? (fromJellyfin && !series ? item.ArtPath : null),
                    DurationSeconds = item.DurationMinutes is > 0 ? item.DurationMinutes.Value * 60 : 0
                });
            }
            return result;
        }
    }
}
