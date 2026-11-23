using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>Un server della pagina Rete (NetworkServerViewItem di Windows).</summary>
internal sealed class NetworkServer
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Host { get; set; } = "";
    public string Location { get; set; } = "";
    public string Protocol { get; set; } = "Jellyfin";
    // Server DLNA/UPnP (stessi campi di dlnaServers.json su Windows).
    public string ResourceBaseUrl { get; set; } = "";
    public string ContentDirectoryControlUrl { get; set; } = "";
    public string ContentDirectoryServiceType { get; set; } = "urn:schemas-upnp-org:service:ContentDirectory:1";
    public bool Available { get; set; } = true;
    public DateTime SeenAt { get; set; } = DateTime.Now;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => DeviceId;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsJellyfin => Protocol == "Jellyfin";
    /// <summary>Plex visto come server DLNA (come Windows: si riconosce dal nome o dal modello).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPlex => !IsJellyfin && !string.IsNullOrWhiteSpace(ContentDirectoryControlUrl) && (Name + " " + Model).Contains("Plex", StringComparison.OrdinalIgnoreCase);
    /// <summary>Ordine di Windows: Jellyfin/Emby e Plex prima degli altri server DLNA.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int KindOrder => IsJellyfin || IsPlex ? 0 : 1;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ServerId => DeviceId.StartsWith("jellyfin:", StringComparison.OrdinalIgnoreCase) ? DeviceId[9..] : DeviceId;
    [System.Text.Json.Serialization.JsonIgnore]
    public JellyfinClient.Account? Account => IsJellyfin ? JellyfinClient.FindAccount(ServerId) : null;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Product => Model.StartsWith("Emby", StringComparison.OrdinalIgnoreCase) ? JellyfinClient.Emby : JellyfinClient.Jellyfin;
}

/// <summary>
/// Server di rete (CinematicMediaLibraryPage.Jellyfin.cs / NetworkServers.cs): elenco salvato in dlnaServers.json
/// come su Windows, ricerca in rete, catalogo Jellyfin/Emby (API) e DLNA/UPnP (ContentDirectory, DlnaClient.cs)
/// come voci della libreria.
/// </summary>
internal static class NetworkStore
{
    private sealed class Model
    {
        public List<NetworkServer> Servers { get; set; } = new();
        public string ConnectedServerKey { get; set; } = "";
        public List<string> FavoriteServerKeys { get; set; } = new();
        public List<string>? QuickSourceServerKeys { get; set; }
    }

    private static string FilePath => Path.Combine(LibraryStore.AppDataDir, "dlnaServers.json");
    private static Model? _model;
    private static Model State
    {
        get
        {
            if (_model != null) return _model;
            try { _model = File.Exists(FilePath) ? JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath)) : null; } catch { }
            _model ??= new Model();
            // Server DLNA salvati con il nome grezzo (anche dalla versione Windows).
            foreach (var saved in _model.Servers.Where(x => !x.IsJellyfin && (x.Name.Contains(':') || x.Name.Contains("(condivisione")))) saved.Name = CleanDlnaName(saved.Name, saved.Model);
            return _model;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(LibraryStore.AppDataDir);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(State));
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        catch { }
    }

    public static IReadOnlyList<NetworkServer> Servers => State.Servers;
    public static string ConnectedKey { get => State.ConnectedServerKey; set { State.ConnectedServerKey = value; Save(); } }
    public static bool IsFavorite(NetworkServer s) => State.FavoriteServerKeys.Contains(s.Key);

    public static void ToggleFavorite(NetworkServer s)
    {
        if (!State.FavoriteServerKeys.Remove(s.Key)) State.FavoriteServerKeys.Add(s.Key);
        Save();
    }

    public static void Remove(NetworkServer s)
    {
        State.Servers.RemoveAll(x => x.Key == s.Key);
        State.FavoriteServerKeys.Remove(s.Key);
        if (State.ConnectedServerKey == s.Key) State.ConnectedServerKey = "";
        if (s.IsJellyfin) JellyfinClient.RemoveAccount(s.ServerId);
        Save();
    }

    /// <summary>I tuoi server: preferiti, quello in uso e quelli con un account.</summary>
    public static bool IsMine(NetworkServer s) => IsFavorite(s) || s.Key == State.ConnectedServerKey || s.Account != null;

    public static NetworkServer FromJellyfin(JellyfinClient.Server server, bool available)
    {
        string host = Uri.TryCreate(server.Address, UriKind.Absolute, out var uri) ? uri.Host : server.Address;
        return new NetworkServer
        {
            DeviceId = "jellyfin:" + server.Id,
            Name = server.Name,
            Model = (server.Product == JellyfinClient.Emby ? JellyfinClient.Emby : JellyfinClient.Jellyfin) + (string.IsNullOrWhiteSpace(server.Version) ? "" : " " + server.Version),
            Host = host,
            Location = server.Address,
            Protocol = "Jellyfin",
            Available = available,
        };
    }

    public static void Upsert(NetworkServer server)
    {
        int i = State.Servers.FindIndex(x => x.Key == server.Key);
        if (i >= 0) State.Servers[i] = server; else State.Servers.Add(server);
        Save();
    }

    /// <summary>Ricerca in rete: Jellyfin/Emby e, insieme, i server DLNA/UPnP (SSDP).</summary>
    public static async Task DiscoverAsync(TimeSpan timeout)
    {
        var dlna = DiscoverDlnaAsync(timeout);
        await DiscoverJellyfinAsync(timeout);
        await dlna;
    }

    private static async Task DiscoverDlnaAsync(TimeSpan timeout)
    {
        List<DlnaClient.NetworkServerViewItem> found;
        try { found = await DlnaClient.DiscoverDlnaServersAsync(timeout); } catch { return; }
        var jellyfinHosts = State.Servers.Where(s => s.IsJellyfin).Select(s => s.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in found)
        {
            // Solo chi ha una libreria da sfogliare (TV e ricevitori rispondono ma non hanno ContentDirectory).
            if (string.IsNullOrWhiteSpace(view.ContentDirectoryControlUrl)) continue;
            // Il DLNA di un server Jellyfin/Emby gia' collegato duplicherebbe lo stesso catalogo, piu' povero.
            if (jellyfinHosts.Contains(view.Host) && (view.Name + " " + view.Model).Contains("jellyfin", StringComparison.OrdinalIgnoreCase)) continue;
            var server = FromDlna(view);
            seen.Add(server.Key);
            Upsert(server);
        }
        foreach (var existing in State.Servers.Where(x => !x.IsJellyfin)) existing.Available = seen.Contains(existing.Key);
        Save();
    }

    /// <summary>
    /// Nome leggibile: la condivisione di Windows Media Player si annuncia come "PC: utente:" (qui "ROG: ROG:").
    /// Parti ripetute e due punti via: resta il nome del PC ("ROG").
    /// </summary>
    public static string CleanDlnaName(string name, string model)
    {
        name = name.Replace(" (condivisione Windows)", "");
        var parts = name.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string clean = parts.Count == 0 ? name.Trim() : string.Join(" · ", parts);
        return clean;
    }

    public static NetworkServer FromDlna(DlnaClient.NetworkServerViewItem view) => new()
    {
        DeviceId = DlnaClient.NetworkServerKey(view),
        Name = CleanDlnaName(view.Name, view.Model), Model = view.Model, Host = view.Host, Location = view.Location,
        Protocol = "DLNA / UPnP", Available = view.Available,
        ResourceBaseUrl = view.ResourceBaseUrl,
        ContentDirectoryControlUrl = view.ContentDirectoryControlUrl,
        ContentDirectoryServiceType = view.ContentDirectoryServiceType,
    };

    private static DlnaClient.NetworkServerViewItem ToView(NetworkServer s) => new()
    {
        DeviceId = s.DeviceId.StartsWith("udn:", StringComparison.OrdinalIgnoreCase) ? s.DeviceId[4..] : s.DeviceId,
        Name = s.Name, Model = s.Model, Host = s.Host, Location = s.Location, Available = s.Available,
        ResourceBaseUrl = s.ResourceBaseUrl,
        ContentDirectoryControlUrl = s.ContentDirectoryControlUrl,
        ContentDirectoryServiceType = s.ContentDirectoryServiceType,
    };

    /// <summary>DiscoverJellyfinServersAsync: chi risponde all'annuncio in rete, piu' quelli con un account se raggiungibili.</summary>
    private static async Task DiscoverJellyfinAsync(TimeSpan timeout)
    {
        var found = new Dictionary<string, JellyfinClient.Server>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var s in await JellyfinClient.DiscoverAsync(timeout)) found[s.Id] = s; } catch { }
        var probes = found.Values.Select(s => s.Address)
            .Concat(JellyfinClient.Accounts.Where(a => !found.ContainsKey(a.ServerId)).Select(a => a.Address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(async address => { try { return await JellyfinClient.GetServerAsync(address, CancellationToken.None); } catch { return null; } })
            .ToArray();
        foreach (var s in await Task.WhenAll(probes))
        {
            if (s == null) continue;
            found[s.Id] = s;
            JellyfinClient.UpdateAddress(s.Id, s.Address);
        }
        foreach (var existing in State.Servers.Where(x => x.IsJellyfin)) existing.Available = found.ContainsKey(existing.ServerId);
        foreach (var s in found.Values) Upsert(FromJellyfin(s, available: true));
        Save();
    }

    // ------------------------------------------------------------------ catalogo
    private static readonly Dictionary<string, List<LibraryItem>> Catalogs = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<LibraryItem>? Catalog(string key)
    {
        if (Catalogs.TryGetValue(key, out var list)) return list;
        // Come Windows: l'ultimo catalogo letto resta su disco e si mostra subito; il server lo aggiorna dopo.
        if (LoadCachedCatalog(key) is { } cached) { Catalogs[key] = cached; return cached; }
        return null;
    }

    public static LibraryItem? QueueDlnaIntroOutro(string path)
    {
        foreach (var server in Servers)
        {
            if (server.IsJellyfin || !Catalogs.TryGetValue(server.Key, out var catalog)) continue;
            var episode = catalog.Find(item => item.Path == path && item.Category == "TV Series");
            if (episode is not { SeriesTitle.Length: > 0, SeasonNumber: not null, EpisodeNumber: not null }) continue;

            var season = catalog.Where(item => item.Category == "TV Series" &&
                item.SeasonNumber == episode.SeasonNumber && item.EpisodeNumber.HasValue &&
                string.Equals(item.SeriesTitle, episode.SeriesTitle, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.EpisodeNumber).ToArray();
            if (season.Length >= 2)
                _ = Task.Run(() => IntroOutroScanService.QueueSeasonScan(episode.SeriesTitle, episode.SeasonNumber,
                    season.Select(item => item.Path), server.Key));
            return season.FirstOrDefault(item => item.EpisodeNumber > episode.EpisodeNumber);
        }
        return null;
    }

    private static readonly HashSet<string> RefreshedThisSession = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Il catalogo mostrato viene dal disco e il server non e' ancora stato riletto in questa sessione.</summary>
    // Server letti solo in parte (rete caduta a meta'): si rileggono al ritorno della connessione.
    private static readonly HashSet<string> Incomplete = new(StringComparer.OrdinalIgnoreCase);
    public static bool IsIncomplete(string key) => Incomplete.Contains(key);

    public static bool NeedsRefresh(string key) => !RefreshedThisSession.Contains(key);
    /// <summary>Il prossimo Reload rilegge il catalogo dal server (connessione tornata).</summary>
    public static void ForgetRefresh(string key) => RefreshedThisSession.Remove(key);
    public static void MarkRefreshed(string key) => RefreshedThisSession.Add(key);

    private static string CatalogCachePath(string key) =>
        Path.Combine(LibraryStore.AppDataDir, "networkCatalogs", Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json");

    private static List<LibraryItem>? LoadCachedCatalog(string key)
    {
        try
        {
            string path = CatalogCachePath(key);
            if (!File.Exists(path)) return null;
            var items = JsonSerializer.Deserialize<List<LibraryItem>>(File.ReadAllText(path));
            if (items == null || items.Count == 0) return null;
            foreach (var item in items)
            {
                item.IsNetwork = true;
                ServerData[item.Path] = (item.Title, item.Year, item.ArtPath, item.WideArtPath);
                ApplyManual(item);
                if (item.ResumeSeconds > 0 && item.ResumeDuration > 0)
                    Resume.TryAdd(item.Path, (item.ResumeSeconds, item.ResumeDuration, item.ResumeSavedAt));
            }
            return items;
        }
        catch { return null; }
    }

    private static void SaveCatalog(string key, List<LibraryItem> items)
    {
        Catalogs[key] = items;
        RefreshedThisSession.Add(key);
        if (items.Count == 0) return;
        // Su disco i dati del server, non le correzioni locali (quelle si riapplicano al caricamento).
        var snapshot = items.Select(i => ServerData.TryGetValue(i.Path, out var o) ? CloneWith(i, o) : i).ToList();
        _ = Task.Run(() =>
        {
            try
            {
                string path = CatalogCachePath(key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch { }
        });
    }

    private static LibraryItem CloneWith(LibraryItem item, (string Title, int? Year, string? Art, string? Wide) original)
    {
        var copy = JsonSerializer.Deserialize<LibraryItem>(JsonSerializer.Serialize(item))!;
        copy.Title = original.Title; copy.Year = original.Year; copy.ArtPath = original.Art; copy.WideArtPath = original.Wide;
        return copy;
    }

    /// <summary>BrowseJellyfinServerItemsAsync: il catalogo dall'API del server (titoli, trame, locandine, ripresa).</summary>
    public static async Task<List<LibraryItem>> LoadCatalogAsync(NetworkServer server, Action<List<LibraryItem>>? progress, CancellationToken ct)
    {
        if (!server.IsJellyfin)
        {
            // BrowseDlnaServerItemsAsync di Windows: cartelle del server, categorie dedotte da classi UPnP e percorsi.
            var report = new DlnaBrowseReport();
            var dlnaItems = await DlnaClient.BrowseDlnaServerItemsAsync(ToView(server), ct, progress, report);
            foreach (var item in dlnaItems) { item.IsNetwork = true; ApplyManual(item); }
            if (report.Incomplete && Catalog(server.Key) is { Count: > 0 } before)
            {
                // Connessione caduta durante la lettura: le voci gia' note restano (con i loro metadati) e il server
                // si rilegge al ritorno della rete, invece di salvare un catalogo a meta' come se fosse quello vero.
                var seen = new HashSet<string>(dlnaItems.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
                dlnaItems.AddRange(before.Where(i => seen.Add(i.Path)));
                Catalogs[server.Key] = dlnaItems;
                Incomplete.Add(server.Key);
                return dlnaItems;
            }
            if (report.Incomplete) Incomplete.Add(server.Key); else Incomplete.Remove(server.Key);
            SaveCatalog(server.Key, dlnaItems);
            return dlnaItems;
        }
        var account = JellyfinClient.FindAccount(server.ServerId) ?? throw new JellyfinClient.UnauthorizedException();
        if (server.Available && !string.IsNullOrWhiteSpace(server.Location) && !string.Equals(server.Location, account.Address, StringComparison.OrdinalIgnoreCase))
        {
            JellyfinClient.UpdateAddress(account.ServerId, server.Location);
            account = JellyfinClient.FindAccount(account.ServerId) ?? account;
        }
        long last = 0;
        var items = await JellyfinClient.LoadItemsAsync(account, partial =>
        {
            if (progress == null || Environment.TickCount64 - last < 700) return;
            last = Environment.TickCount64;
            progress(partial.Select((item, order) => Build(account, item, order)).ToList());
        }, ct);
        var result = items.Select((item, order) => Build(account, item, order)).ToList();
        // Punto di ripresa: lo tiene il server (un film iniziato sulla TV si riprende anche qui).
        string prefix = account.Address.TrimEnd('/') + "/";
        foreach (var stale in Resume.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) Resume.TryRemove(stale, out _);
        foreach (var item in result)
            if (item.ResumeSeconds > 0 && item.ResumeDuration > 0)
                Resume[item.Path] = (item.ResumeSeconds, item.ResumeDuration, item.ResumeSavedAt);
        JellyfinClient.RememberTitles(account.Address, result.Zip(items).Select(p => (p.First.Path, DisplayTitle(p.Second), p.First.Category == "Movies" ? "Film" : "Serie TV", p.Second.FileName, (int?)null)).ToList());
        Incomplete.Remove(server.Key);
        SaveCatalog(server.Key, result);
        return result;
    }

    private static string DisplayTitle(JellyfinClient.Item item)
    {
        if (item.Type == "Episode" && !string.IsNullOrWhiteSpace(item.SeriesName))
        {
            string number = item.SeasonNumber.HasValue && item.EpisodeNumber.HasValue ? $" S{item.SeasonNumber.Value:00}E{item.EpisodeNumber.Value:00}" : "";
            return string.IsNullOrWhiteSpace(item.Name) ? item.SeriesName + number : $"{item.SeriesName}{number} – {item.Name}";
        }
        return item.Name;
    }

    /// <summary>Dati del server prima delle correzioni dell'utente (per "Ripristina").</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Title, int? Year, string? Art, string? Wide)> ServerData = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Correzioni di titolo/copertina dell'editor metadati anche per i titoli del server (Jellyfin non sempre ha la
    /// locandina): restano solo su questo Mac, legate all'indirizzo del titolo, il server non viene toccato.
    /// </summary>
    public static void ApplyManual(LibraryItem item)
    {
        var original = ServerData.GetOrAdd(item.Path, _ => (item.Title, item.Year, item.ArtPath, item.WideArtPath));
        bool titled = CinecorePlayer2025.Utilities.MovieMetadataService.TryGetManualTitle(item.Path, out string title, out int? year);
        string? poster = CinecorePlayer2025.Utilities.MovieMetadataService.GetManualPoster(item.Path);
        item.Title = titled ? title : original.Title;
        item.Year = titled ? year : original.Year;
        item.ArtPath = poster ?? original.Art;
        item.WideArtPath = original.Wide ?? poster;
    }

    /// <summary>Dopo un salvataggio dell'editor: aggiorna le voci gia' in memoria.</summary>
    public static void ManualChanged(string path)
    {
        foreach (var list in Catalogs.Values)
            foreach (var item in list)
                if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) ApplyManual(item);
    }

    /// <summary>BuildJellyfinLibraryItem: locandina della serie per gli episodi, sfondo dal backdrop.</summary>
    private static LibraryItem Build(JellyfinClient.Account account, JellyfinClient.Item item, int order)
    {
        string category = item.Type switch { "Movie" => "Movies", "Episode" => "TV Series", "Audio" => "Music", _ => "Videos" };
        string Image(string id, string kind, int width) => JellyfinClient.ImageUrl(account.Address, id, kind, width);
        string? poster = category switch
        {
            "TV Series" when item.SeriesHasPrimaryImage && item.SeriesId != null => Image(item.SeriesId, "Primary", 600),
            "Music" when item.AlbumHasPrimaryImage && item.AlbumId != null => Image(item.AlbumId, "Primary", 600),
            _ => item.HasPrimaryImage ? Image(item.Id, "Primary", 600) : null,
        };
        string? wide = item.BackdropItemId != null ? Image(item.BackdropItemId, "Backdrop", 1920)
            : category is "TV Series" or "Videos" && item.HasPrimaryImage ? Image(item.Id, "Primary", 1280) : null;
        var built = new LibraryItem
        {
            Path = JellyfinClient.StreamUrl(account.Address, item),
            Title = string.IsNullOrWhiteSpace(item.Name) ? "Jellyfin" : item.Name,
            Category = category,
            Year = item.Year,
            ResumeSeconds = item.Type != "Audio" && !item.Played ? item.ResumeSeconds : 0,
            ResumeDuration = item.DurationSeconds,
            ResumeSavedAt = (item.LastPlayedUtc ?? DateTime.UtcNow).ToLocalTime(),
            SortDateUtc = item.DateCreatedUtc > DateTime.MinValue.AddYears(1) ? item.DateCreatedUtc : DateTime.UtcNow.AddSeconds(-order),
            ArtPath = poster ?? (category == "Music" ? null : wide),
            WideArtPath = wide ?? poster,
            Overview = item.Overview ?? "",
            SeriesTitle = category == "TV Series" ? item.SeriesName : null,
            SeasonNumber = category == "TV Series" ? item.SeasonNumber : null,
            EpisodeNumber = item.EpisodeNumber,
            IsNetwork = true,
            DurationMinutes = item.DurationSeconds > 0 ? item.DurationSeconds / 60.0 : null,
            Genres = item.Genres.ToList(),
            Tagline = item.Tagline,
            Rating = item.Rating,
            ResolutionLabel = item.Width >= 3200 || item.Height >= 1600 ? "2160p" : item.Width >= 1800 || item.Height >= 1000 ? "1080p" : item.Width >= 1200 || item.Height >= 700 ? "720p" : item.Width > 0 ? item.Height + "p" : null,
            VideoHeight = item.Height,
            Is4K = item.Width >= 3200 || item.Height >= 1600,
            IsHdr = item.Hdr,
            DolbyVision = item.DolbyVision,
            AudioLabel = AudioLabel(item),
            HasAtmos = item.ObjectAudio,
            Bytes = Math.Max(0, item.Bytes),
            FileName = item.FileName,
            SeriesId = item.SeriesId,
            Still = category == "TV Series" && item.HasPrimaryImage ? Image(item.Id, "Primary", 800) : null,
        };
        ServerData[built.Path] = (built.Title, built.Year, built.ArtPath, built.WideArtPath);
        ApplyManual(built);
        return built;
    }

    /// <summary>Riprese dei titoli di rete (indirizzo senza token → posizione, durata, quando).</summary>
    public static readonly ConcurrentDictionary<string, (double Position, double Duration, DateTime SavedAt)> Resume = new(StringComparer.OrdinalIgnoreCase);

    public static bool CanResume(double position, double duration) =>
        duration >= 300 && position / duration is >= 0.05 and <= 0.90;

    /// <summary>NoteJellyfinPlayback: come il server, niente ripresa sotto i cinque minuti, prima del 5% o dopo il 90%.</summary>
    public static void NotePlayback(string url, double position, double duration)
    {
        if (string.IsNullOrWhiteSpace(url) || duration <= 0) return;
        // Zero fa da punto di ripresa esplicitamente rimosso, anche se il catalogo sul disco è vecchio.
        Resume[url] = (CanResume(position, duration) ? position : 0, duration, DateTime.UtcNow);
    }

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<LibraryItem, Lazy<Task<bool>>> PeopleRequests = new();

    /// <summary>Regia e cast di un titolo dal server (People, con le foto), alla prima apertura della scheda.</summary>
    public static async Task<bool> LoadPeopleAsync(LibraryItem item)
    {
        if (!item.IsNetwork) return false;
        if (item.Cast.Count > 0) return true;
        var request = PeopleRequests.GetOrAdd(item, entry => new Lazy<Task<bool>>(() => FetchPeopleAsync(entry)));
        try { return await request.Value; }
        finally { ((ICollection<KeyValuePair<LibraryItem, Lazy<Task<bool>>>>)PeopleRequests).Remove(new(item, request)); }
    }

    private static async Task<bool> FetchPeopleAsync(LibraryItem item)
    {
        if (!JellyfinClient.TryParseStream(item.Path, out var account, out string itemId, out _)) return false;
        string? token = SecretVault.Unprotect(account.Token);
        if (string.IsNullOrEmpty(token)) return false;
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"{account.Address.TrimEnd('/')}/Users/{account.UserId}/Items/{itemId}?Fields=People");
            request.Headers.Add("X-Emby-Token", token); // il token solo nell'intestazione, mai nell'indirizzo
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("People", out var people)) return false;
            var cast = new List<(string, string, string?)>();
            string? director = null, directorPhoto = null;
            foreach (var p in people.EnumerateArray())
            {
                string name = p.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                string type = p.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
                string id = p.TryGetProperty("Id", out var i) ? i.GetString() ?? "" : "";
                bool photo = p.TryGetProperty("PrimaryImageTag", out var tag) && tag.ValueKind == JsonValueKind.String;
                string? image = photo && id.Length > 0 ? JellyfinClient.ImageUrl(account.Address, id, "Primary", 240) : null;
                if (type == "Director" && director == null) { director = name; directorPhoto = image; }
                else if (type == "Actor" && cast.Count < 12) cast.Add((name, p.TryGetProperty("Role", out var r) ? r.GetString() ?? "" : "", image));
            }
            item.Director = director;
            item.DirectorPhoto = directorPhoto;
            item.Cast = cast;
            return true;
        }
        catch { return false; }
    }

    public sealed class SeriesInfo
    {
        public string? Overview;
        public List<string> Genres = new();
        public string? Director, DirectorPhoto;
        public List<(string Name, string Role, string? Photo)> Cast = new();
    }
    private static readonly Dictionary<string, SeriesInfo> SeriesCache = new(StringComparer.OrdinalIgnoreCase);
    public static SeriesInfo? Series(string? seriesId) => seriesId != null && SeriesCache.TryGetValue(seriesId, out var s) ? s : null;

    /// <summary>La serie come elemento a se' (trama, generi, cast), dal server, alla prima apertura della scheda.</summary>
    public static async Task<bool> LoadSeriesAsync(LibraryItem episode)
    {
        if (!episode.IsNetwork || episode.SeriesId is not { } seriesId || SeriesCache.ContainsKey(seriesId)) return false;
        if (!JellyfinClient.TryParseStream(episode.Path, out var account, out _, out _)) return false;
        string? token = SecretVault.Unprotect(account.Token);
        if (string.IsNullOrEmpty(token)) return false;
        var info = SeriesCache[seriesId] = new SeriesInfo();
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"{account.Address.TrimEnd('/')}/Users/{account.UserId}/Items/{seriesId}?Fields=People,Overview,Genres");
            request.Headers.Add("X-Emby-Token", token);
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) { SeriesCache.Remove(seriesId); return false; }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("Overview", out var o)) info.Overview = o.GetString();
            if (root.TryGetProperty("Genres", out var g)) info.Genres = g.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            if (root.TryGetProperty("People", out var people))
                foreach (var p in people.EnumerateArray())
                {
                    string name = p.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    string type = p.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
                    string id = p.TryGetProperty("Id", out var i) ? i.GetString() ?? "" : "";
                    string? image = p.TryGetProperty("PrimaryImageTag", out var tag) && tag.ValueKind == JsonValueKind.String && id.Length > 0 ? JellyfinClient.ImageUrl(account.Address, id, "Primary", 240) : null;
                    if (type is "Director" or "Creator" && info.Director == null) { info.Director = name; info.DirectorPhoto = image; }
                    else if (type == "Actor" && info.Cast.Count < 12) info.Cast.Add((name, p.TryGetProperty("Role", out var r) ? r.GetString() ?? "" : "", image));
                }
            return true;
        }
        catch { SeriesCache.Remove(seriesId); return false; }
    }

    /// <summary>JellyfinAudioLabel: famiglia del codec e canali (Dolby Atmos, DTS-HD MA 7.1...).</summary>
    private static string? AudioLabel(JellyfinClient.Item item)
    {
        string codec = (item.AudioCodec ?? "").ToLowerInvariant();
        string? family = codec switch
        {
            "truehd" => item.ObjectAudio ? "Dolby Atmos" : "Dolby TrueHD",
            "eac3" => item.ObjectAudio ? "Dolby Atmos" : "Dolby Digital+",
            "ac3" => "Dolby Digital",
            "dts" => item.ObjectAudio ? "DTS:X" : (item.AudioProfile ?? "").Contains("MA", StringComparison.OrdinalIgnoreCase) ? "DTS-HD MA" : (item.AudioProfile ?? "").Contains("HD", StringComparison.OrdinalIgnoreCase) ? "DTS-HD" : "DTS",
            "dca" => "DTS", "flac" => "FLAC", "aac" => "AAC", "opus" => "Opus", "mp3" => "MP3",
            _ when codec.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
            _ => null,
        };
        string? layout = item.AudioChannels switch { >= 8 => "7.1", >= 6 => "5.1", 2 => "2.0", 1 => "1.0", _ => null };
        return family != null && layout != null && !item.ObjectAudio ? family + " " + layout : family ?? layout;
    }

    // ------------------------------------------------------------------ immagini del server (con il token, mai nel file)
    private static string ArtCacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "Cinecore", "network-art");

    /// <summary>Il file locale di un'immagine del server: scaricata una volta (TryDownloadArtworkAsync aggiunge il token) e tenuta in cache.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> ArtworkRequests = new();
    private static readonly SemaphoreSlim ArtworkDownloads = new(4);

    public static async Task<string?> CachedArtAsync(string url)
    {
        var request = ArtworkRequests.GetOrAdd(url, address => new Lazy<Task<string?>>(() => DownloadArtAsync(address)));
        try { return await request.Value; }
        finally { ((ICollection<KeyValuePair<string, Lazy<Task<string?>>>>)ArtworkRequests).Remove(new(url, request)); }
    }

    private static readonly System.Net.Http.HttpClient DlnaHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Copertine dei server DLNA: solo dagli indirizzi dei server conosciuti, niente richieste altrove.</summary>
    private static async Task<byte[]?> TryDownloadDlnaArtAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        if (!State.Servers.Any(s => !s.IsJellyfin && string.Equals(s.Host, uri.Host, StringComparison.OrdinalIgnoreCase))) return null;
        if (RejectedArt.ContainsKey(url)) return null;
        byte[] bytes;
        try { bytes = await DlnaHttp.GetByteArrayAsync(uri); } catch { return null; }
        return IsGenericArt(url, uri.Host, bytes) ? null : bytes;
    }

    // Anteprime generiche dei server DLNA (il foglio bianco della condivisione di Windows per i brani senza
    // copertina): la stessa immagine identica per 3 o piu' titoli diversi non e' una copertina. Al suo posto il
    // segnaposto dell'app (la nota per la musica). Le impronte restano in un file per i prossimi avvii.
    private static readonly ConcurrentDictionary<string, byte> RejectedArt = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ArtByHash = new(StringComparer.Ordinal);
    private static readonly string GenericArtPath = Path.Combine(LibraryStore.AppDataDir, "dlnaGenericArt.txt");
    private static readonly Lazy<ConcurrentDictionary<string, byte>> GenericHashes = new(() =>
    {
        var set = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        try { if (File.Exists(GenericArtPath)) foreach (var line in File.ReadAllLines(GenericArtPath)) if (line.Length > 0) set[line.Trim()] = 0; } catch { }
        return set;
    });
    /// <summary>Un'immagine gia' mostrata si e' rivelata generica: la vista la toglie dalla sua cache.</summary>
    public static event Action<string>? ArtRejected;

    private static bool IsGenericArt(string url, string host, byte[] bytes)
    {
        string hash = host + ":" + Convert.ToHexString(SHA1.HashData(bytes));
        if (GenericHashes.Value.ContainsKey(hash)) { RejectedArt[url] = 0; return true; }
        var urls = ArtByHash.GetOrAdd(hash, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        urls[url] = 0;
        if (urls.Count < 3) return false;
        GenericHashes.Value[hash] = 0;
        try { Directory.CreateDirectory(LibraryStore.AppDataDir); File.AppendAllLines(GenericArtPath, new[] { hash }); } catch { }
        foreach (string earlier in urls.Keys)
        {
            RejectedArt[earlier] = 0;
            try { string cached = ArtCacheFile(earlier); if (File.Exists(cached)) File.Delete(cached); } catch { }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ArtRejected?.Invoke(earlier));
        }
        return true;
    }

    /// <summary>Il file in cache di un'immagine del server, se c'e' gia' (per il telecomando).</summary>
    public static string? CachedArtFileIfPresent(string url) { string f = ArtCacheFile(url); return File.Exists(f) && !RejectedArt.ContainsKey(url) ? f : null; }

    private static string ArtCacheFile(string url) => Path.Combine(ArtCacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".img");

    private static async Task<string?> DownloadArtAsync(string url)
    {
        string file = ArtCacheFile(url);
        if (RejectedArt.ContainsKey(url)) return null;
        if (File.Exists(file))
        {
            // Anche le immagini gia' in cache dei server DLNA passano dal controllo delle anteprime generiche.
            if (Uri.TryCreate(url, UriKind.Absolute, out var cachedUri) && State.Servers.Any(s => !s.IsJellyfin && string.Equals(s.Host, cachedUri.Host, StringComparison.OrdinalIgnoreCase)))
            {
                byte[] cached;
                try { cached = await File.ReadAllBytesAsync(file); } catch { return file; }
                if (IsGenericArt(url, cachedUri.Host, cached)) return null;
            }
            return file;
        }
        await ArtworkDownloads.WaitAsync();
        string temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (File.Exists(file)) return file;
            // I ritratti dei metadati locali usano la CDN TMDb, senza credenziali Jellyfin.
            if (Uri.TryCreate(url, UriKind.Absolute, out var tmdb) && tmdb.Scheme == "https" &&
                tmdb.Host == "image.tmdb.org" && tmdb.IsDefaultPort && tmdb.Query.Length == 0 &&
                System.Text.RegularExpressions.Regex.Match(tmdb.AbsolutePath, @"^/t/p/(w[0-9]{2,4})(/[a-zA-Z0-9_-]+\.(?:jpg|png|webp))$") is { Success: true } image)
            {
                using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    if (!MovieMetadataService.DownloadTmdbImage(image.Groups[2].Value, image.Groups[1].Value, temp, timeout.Token))
                        return null;
                }
                catch (OperationCanceledException) { return null; }
                File.Move(temp, file, overwrite: true);
                return file;
            }
            var bytes = await JellyfinClient.TryDownloadArtworkAsync(url) ?? await TryDownloadDlnaArtAsync(url);
            if (bytes == null || bytes.Length < 128) return null;
            Directory.CreateDirectory(ArtCacheDir);
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, file, overwrite: true);
            return file;
        }
        finally { ArtworkDownloads.Release(); try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
