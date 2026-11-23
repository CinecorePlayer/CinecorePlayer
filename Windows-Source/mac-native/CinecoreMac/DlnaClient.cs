// Generato da MediaLibrary/CinematicMediaLibraryPage.Network.cs (Windows): scoperta SSDP e navigazione
// ContentDirectory dei server DLNA/UPnP, senza interfaccia. Adattamenti per il Mac segnati con "Mac:".
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

internal sealed record DlnaVideoResource(string Path, int Width, int Height, long BytesPerSecond);

internal static partial class DlnaClient
{
    internal static async Task<List<NetworkServerViewItem>> DiscoverDlnaServersAsync(TimeSpan timeout)
        {
            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cts = new CancellationTokenSource(timeout);
            var udpClients = new List<UdpClient>();
            try
            {
                // One problematic virtual/VPN adapter must not abort discovery on all
                // healthy interfaces. Query every adapter independently and retain the
                // usable IPv4 multicast addresses.
                var localAddresses = new HashSet<IPAddress>();
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (adapter.OperationalStatus != OperationalStatus.Up ||
                            adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                            adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                            !adapter.SupportsMulticast)
                            continue;

                        foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
                        {
                            if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback(address.Address))
                                localAddresses.Add(address.Address);
                        }
                    }
                    catch { }
                }

                foreach (IPAddress localAddress in localAddresses)
                {
                    try
                    {
                        var client = new UdpClient(new IPEndPoint(localAddress, 0));
                        client.EnableBroadcast = true;
                        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
                        udpClients.Add(client);
                    }
                    catch { }
                }

                if (udpClients.Count == 0)
                {
                    var fallbackClient = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                    fallbackClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udpClients.Add(fallbackClient);
                }

                var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                string[] searchTargets =
                {
                    "urn:schemas-upnp-org:device:MediaServer:1",
                    "urn:schemas-upnp-org:service:ContentDirectory:1",
                    "upnp:rootdevice",
                    "ssdp:all"
                };
                foreach (UdpClient udp in udpClients)
                {
                    foreach (string st in searchTargets)
                    {
                        byte[] packet = Encoding.ASCII.GetBytes(
                            "M-SEARCH * HTTP/1.1\r\n" +
                            "HOST: 239.255.255.250:1900\r\n" +
                            "MAN: \"ssdp:discover\"\r\n" +
                            "MX: 2\r\n" +
                            "ST: " + st + "\r\n\r\n");
                        try { await udp.SendAsync(packet, packet.Length, endpoint); } catch { }
                    }
                }

                async Task ReceiveLocationsAsync(UdpClient udp)
                {
                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            var res = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(650), cts.Token);
                            string text = Encoding.ASCII.GetString(res.Buffer);
                            foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                int colon = line.IndexOf(':');
                                if (colon <= 0)
                                    continue;
                                if (string.Equals(line[..colon].Trim(), "LOCATION", StringComparison.OrdinalIgnoreCase))
                                {
                                    lock (locations)
                                        locations.Add(line[(colon + 1)..].Trim());
                                }
                            }
                        }
                        catch (TimeoutException) { }
                        catch (OperationCanceledException) { break; }
                        catch { }
                    }
                }

                await Task.WhenAll(udpClients.Select(ReceiveLocationsAsync)).ConfigureAwait(false);
            }
            catch { }
            finally
            {
                foreach (UdpClient udp in udpClients)
                {
                    try { udp.Dispose(); } catch { }
                }
            }

            var result = new List<NetworkServerViewItem>();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            TryApplyDlnaHttpHeaders(http);
            using var descriptionGate = new SemaphoreSlim(6, 6);
            var descriptionTasks = locations.Take(24).Select(async location =>
            {
                await descriptionGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var uri = new Uri(location);
                    string xml = await http.GetStringAsync(uri).ConfigureAwait(false);
                    XDocument doc = XDocument.Parse(xml);
                    string name = XmlLocalValue(doc.Root, "friendlyName");
                    string model = XmlLocalValue(doc.Root, "modelName");
                    string deviceId = XmlLocalValue(doc.Root, "UDN");
                    string urlBase = XmlLocalValue(doc.Root, "URLBase");
                    Uri baseUri = ResolveDeviceBaseUri(uri, urlBase);
                    var contentDirectory = FindUpnpService(doc, "ContentDirectory");
                    string control = contentDirectory != null ? XmlLocalValue(contentDirectory, "controlURL") : string.Empty;
                    string serviceType = contentDirectory != null ? XmlLocalValue(contentDirectory, "serviceType") : "urn:schemas-upnp-org:service:ContentDirectory:1";
                    string controlUrl = ResolveUpnpUrl(baseUri, control);
                    if (string.IsNullOrWhiteSpace(name))
                        name = uri.Host;
                    return new NetworkServerViewItem
                    {
                        DeviceId = WebUtility.HtmlDecode(deviceId),
                        Name = WebUtility.HtmlDecode(name),
                        Model = WebUtility.HtmlDecode(model),
                        Host = uri.Host,
                        Location = location,
                        ResourceBaseUrl = baseUri.AbsoluteUri,
                        ContentDirectoryControlUrl = controlUrl,
                        ContentDirectoryServiceType = FirstRawNonEmpty(serviceType, "urn:schemas-upnp-org:service:ContentDirectory:1"),
                        SeenAt = DateTime.Now
                    };
                }
                catch { return null; }
                finally { descriptionGate.Release(); }
            }).ToArray();

            var descriptions = await Task.WhenAll(descriptionTasks).ConfigureAwait(false);
            result.AddRange(descriptions.Where(server => server != null).Cast<NetworkServerViewItem>());

            // Lo stesso server risponde da ogni scheda di rete: fra i suoi indirizzi si preferisce quello
            // della rete vera a uno automatico (169.254.x.x), che fuori da quella scheda non porta a nulla.
            return result
                .GroupBy(NetworkServerKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(s => s.Host.StartsWith("169.254.", StringComparison.Ordinal) ? 1 : 0).First())
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

    internal static async Task<List<LibraryItem>> BrowseDlnaServerItemsAsync(
            NetworkServerViewItem server,
            CancellationToken ct,
            Action<List<LibraryItem>>? progress = null,
            DlnaBrowseReport? report = null)
        {
            var result = new List<LibraryItem>();
            if (string.IsNullOrWhiteSpace(server.ContentDirectoryControlUrl))
                return result;

            Uri controlUri = new(server.ContentDirectoryControlUrl);
            Uri resourceBase = !string.IsNullOrWhiteSpace(server.ResourceBaseUrl) && Uri.TryCreate(server.ResourceBaseUrl, UriKind.Absolute, out var storedBaseUri)
                ? storedBaseUri
                : !string.IsNullOrWhiteSpace(server.Location) && Uri.TryCreate(server.Location, UriKind.Absolute, out var locationUri)
                    ? ResolveDeviceBaseUri(locationUri, string.Empty)
                    : new Uri(controlUri.GetLeftPart(UriPartial.Authority));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            TryApplyDlnaHttpHeaders(http);
            var queue = new PriorityQueue<(string Id, int Depth, string Trail), int>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            // UPnP ContentDirectory definisce "0" come root. Il vecchio codice provava
            // anche nomi e ID inventati dopo aver gia' attraversato l'intero albero,
            // moltiplicando richieste, timeout e duplicati.
            queue.Enqueue(("0", 0, "0"), 0);
            string negotiatedServiceType = FirstRawNonEmpty(server.ContentDirectoryServiceType, "urn:schemas-upnp-org:service:ContentDirectory:1");
            bool? negotiatedQuoteSoapAction = null;
            int order = 0;
            const int maxItems = 8000;
            const int maxDepth = 24;
            string? lastError = null;
            int failedRoots = 0;
            int lastPublishedCount = 0;
            int lastPublishedCategoryMask = 0;
            long lastPublishedAt = Environment.TickCount64;
            var byUrl = new Dictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase);

            try
            {
                while (queue.Count > 0 && result.Count < maxItems)
                {
                    ct.ThrowIfCancellationRequested();
                    queue.TryDequeue(out var current, out _);
                    var (objectId, depth, trail) = current;
                    // Plex can expose the same object ID through several virtual views.
                    // IDs are server-global: visiting them once removes the largest source
                    // of duplicate requests and cuts multi-minute scans dramatically.
                    if (!visited.Add(objectId) || depth > maxDepth)
                        continue;

                    int start = 0;
                    int total = int.MaxValue;
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        XDocument response;
                        try
                        {
                            if (negotiatedQuoteSoapAction.HasValue)
                            {
                                try
                                {
                                    response = await DlnaBrowseAsync(http, controlUri, negotiatedServiceType, objectId, start, 600, negotiatedQuoteSoapAction.Value, ct);
                                }
                                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                                {
                                    throw;
                                }
                                catch
                                {
                                    negotiatedQuoteSoapAction = null;
                                    var negotiated = await DlnaBrowseWithFallbacksAsync(http, controlUri, negotiatedServiceType, objectId, start, 600, ct);
                                    response = negotiated.Document;
                                    negotiatedServiceType = negotiated.ServiceType;
                                    negotiatedQuoteSoapAction = negotiated.QuoteSoapAction;
                                }
                            }
                            else
                            {
                                var negotiated = await DlnaBrowseWithFallbacksAsync(http, controlUri, negotiatedServiceType, objectId, start, 600, ct);
                                response = negotiated.Document;
                                negotiatedServiceType = negotiated.ServiceType;
                                negotiatedQuoteSoapAction = negotiated.QuoteSoapAction;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex.Message;
                            if (depth == 0)
                                failedRoots++;
                            // Connessione caduta a meta': il catalogo letto e' incompleto (NetworkStore tiene le voci di prima).
                            if (report != null) report.Failures++;
                            break;
                        }

                        string didlXml = ExtractSoapResultXml(response);
                        if (!TryParseDidlDocument(didlXml, out XDocument didl))
                            break;

                        foreach (var container in SelectNetworkBrowseContainers(didl, server.Name))
                        {
                            string id = container.Attribute("id")?.Value ?? string.Empty;
                            string containerTitle = XmlLocalValue(container, "title");
                            string containerClass = XmlLocalValue(container, "class");
                            string childTrail = FirstRawNonEmpty(
                                string.Join(" / ", new[] { trail, containerTitle, containerClass }.Where(v => !string.IsNullOrWhiteSpace(v))),
                                id);
                            if (!string.IsNullOrWhiteSpace(id) && depth < maxDepth)
                            {
                                int priority = NetworkContainerPriority(containerTitle, containerClass, childTrail, depth + 1);
                                queue.Enqueue((id, depth + 1, childTrail), priority);
                            }
                        }

                        foreach (var item in didl.Descendants().Where(e => e.Name.LocalName == "item"))
                        {
                            var libraryItem = BuildNetworkLibraryItem(item, resourceBase, server.Name, order++, trail);
                            if (libraryItem != null)
                            {
                                if (byUrl.TryGetValue(libraryItem.Path, out var existing))
                                {
                                    // Virtual folders can expose the same resource with
                                    // richer metadata. Upgrade it without consuming the limit.
                                    var preferred = NormalizeNetworkItems(new[] { existing, libraryItem })[0];
                                    if (!ReferenceEquals(preferred, existing))
                                    {
                                        result[result.IndexOf(existing)] = preferred;
                                        byUrl[libraryItem.Path] = preferred;
                                    }
                                }
                                else { byUrl[libraryItem.Path] = libraryItem; result.Add(libraryItem); }
                            }
                            if (result.Count >= maxItems)
                                break;
                        }

                        int categoryMask = NetworkCategoryMask(result);
                        bool gainedCategory = (categoryMask & ~lastPublishedCategoryMask) != 0;
                        bool firstUsefulBatch = lastPublishedCount == 0 && result.Count >= 80;
                        if (progress != null && result.Count > lastPublishedCount && (gainedCategory || firstUsefulBatch || Environment.TickCount64 - lastPublishedAt >= 1500))
                        {
                            lastPublishedCount = result.Count;
                            lastPublishedAt = Environment.TickCount64;
                            lastPublishedCategoryMask = categoryMask;
                            try { progress(NormalizeNetworkItems(result)); } catch { }
                        }

                        int returned = Math.Max(0, SoapIntValue(response, "NumberReturned"));
                        int parsedTotal = SoapIntValue(response, "TotalMatches");
                        if (parsedTotal > 0)
                            total = parsedTotal;
                        if (returned <= 0)
                            break;
                        start += returned;
                    }
                    while (start < total && result.Count < maxItems);
                }
            }
            catch (OperationCanceledException) when (result.Count > 0)
            {
                if (report != null) report.Partial = true;
                // Alcuni server DLNA rispondono lentamente su cartelle profonde: i risultati
                // gia letti sono comunque usabili e non vanno convertiti in errore.
            }

            if (result.Count == 0 && failedRoots > 0 && !string.IsNullOrWhiteSpace(lastError))
                throw new InvalidOperationException(lastError);

            return NormalizeNetworkItems(result);
        }

    internal static string NetworkServerKey(NetworkServerViewItem? server)
        {
            if (server == null)
                return string.Empty;

            string deviceId = NormalizeNetworkIdentityPart(server.DeviceId);
            if (!string.IsNullOrWhiteSpace(deviceId))
                return "udn:" + deviceId;

            string name = NormalizeNetworkIdentityPart(server.Name);
            string model = NormalizeNetworkIdentityPart(server.Model);
            if (!string.IsNullOrWhiteSpace(name))
                return "device:" + name + "|" + model;

            return "endpoint:" + NetworkServerLegacyKey(server);
        }

    internal static string NetworkServerLegacyKey(NetworkServerViewItem? server)
        {
            if (server == null)
                return string.Empty;
            return FirstRawNonEmpty(
                server.Location,
                server.ContentDirectoryControlUrl,
                string.Join("|", new[] { server.Host, server.Name }.Where(v => !string.IsNullOrWhiteSpace(v))),
                server.Name);
        }

    internal static string NetworkServerNameOnlyKey(NetworkServerViewItem? server)
        {
            string name = NormalizeNetworkIdentityPart(server?.Name);
            return name.Length == 0 ? string.Empty : "device:" + name;
        }

    internal static bool NetworkServersDescribeSameDevice(NetworkServerViewItem? left, NetworkServerViewItem? right)
        {
            if (left == null || right == null)
                return false;
            if (!string.IsNullOrWhiteSpace(left.DeviceId) && !string.IsNullOrWhiteSpace(right.DeviceId))
                return string.Equals(NormalizeNetworkIdentityPart(left.DeviceId), NormalizeNetworkIdentityPart(right.DeviceId), StringComparison.OrdinalIgnoreCase);

            string leftName = NormalizeNetworkIdentityPart(left.Name);
            string rightName = NormalizeNetworkIdentityPart(right.Name);
            if (leftName.Length == 0 || !string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase))
                return false;

            string leftModel = NormalizeNetworkIdentityPart(left.Model);
            string rightModel = NormalizeNetworkIdentityPart(right.Model);
            return (leftModel.Length > 0 && rightModel.Length > 0 && string.Equals(leftModel, rightModel, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrWhiteSpace(left.Host) && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase));
        }

    internal static string NormalizeNetworkIdentityPart(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return string.Join(" ", value.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

    internal static string XmlLocalValue(XContainer? root, string localName)
        {
            if (root == null || string.IsNullOrWhiteSpace(localName))
                return string.Empty;
            return root.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim() ?? string.Empty;
        }

    internal static XElement? FindUpnpService(XDocument doc, string serviceNameFragment)
        {
            foreach (var service in doc.Descendants().Where(e => e.Name.LocalName == "service"))
            {
                string type = XmlLocalValue(service, "serviceType");
                string id = XmlLocalValue(service, "serviceId");
                if (type.Contains(serviceNameFragment, StringComparison.OrdinalIgnoreCase) ||
                    id.Contains(serviceNameFragment, StringComparison.OrdinalIgnoreCase))
                {
                    return service;
                }
            }
            return null;
        }

    internal static Uri ResolveDeviceBaseUri(Uri location, string urlBase)
        {
            if (!string.IsNullOrWhiteSpace(urlBase) && Uri.TryCreate(urlBase.Trim(), UriKind.Absolute, out var baseUri) && IsWeb(baseUri))
                return baseUri;
            try
            {
                return new Uri(location, ".");
            }
            catch
            {
                return new Uri(location.GetLeftPart(UriPartial.Authority));
            }
        }

    private static bool IsWeb(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    internal static string ResolveUpnpUrl(Uri baseUri, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            value = WebUtility.HtmlDecode(value.Trim());
            // Mac: su Unix "/percorso" e' un Uri assoluto file:// — qui e' sempre relativo al server.
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && IsWeb(absolute))
                return absolute.ToString();
            if (Uri.TryCreate(baseUri, value, out var relative))
                return relative.ToString();
            return value;
        }

    internal static int NetworkContainerPriority(string? title, string? upnpClass, string? trail, int depth)
        {
            string sample = NormalizeNetworkTrail(string.Join(" ", new[] { title, upnpClass, trail }
                .Where(value => !string.IsNullOrWhiteSpace(value))));

            int semantic = HasNetworkTvTrail(sample) ? 0
                : sample.Contains(" music ") || sample.Contains(" audio ") || sample.Contains(" album ") || sample.Contains(" artist ") ? 1
                : HasNetworkMovieTrail(sample) ? 2
                : sample.Contains(" photo ") || sample.Contains(" image ") ? 3
                : 7;

            // Aggregated Plex views such as "All videos" can contain thousands of
            // duplicates. Keep them available, but traverse real media sections first.
            bool aggregate = sample.Contains(" all video ") || sample.Contains(" all videos ") ||
                             sample.Contains(" recently added ") || sample.Contains(" recently viewed ") ||
                             sample.Contains(" by actor ") || sample.Contains(" by director ") ||
                             sample.Contains(" by genre ") || sample.Contains(" continue watching ");
            return semantic * 100 + Math.Max(0, depth) + (aggregate ? 500 : 0);
        }

    internal static IEnumerable<XElement> SelectNetworkBrowseContainers(XDocument document, string serverName)
        {
            var containers = document.Descendants().Where(x => x.Name.LocalName == "container").ToList();
            if (!serverName.Contains("Plex", StringComparison.OrdinalIgnoreCase)) return containers;
            // Plex exposes each section again by year, genre, actor, resolution,
            // recently viewed, etc. Its All view already includes every item.
            bool hasAlternateIndexes = containers.Any(x => XmlLocalValue(x, "title").StartsWith("By ", StringComparison.OrdinalIgnoreCase));
            var all = containers.Where(x =>
                {
                    string title = XmlLocalValue(x, "title").ToLowerInvariant();
                    return title.StartsWith("all ", StringComparison.Ordinal) &&
                        (hasAlternateIndexes || title is "all tracks" or "all albums" or "all artists" or "all shows" or "all videos");
                })
                .OrderBy(x =>
                {
                    string title = XmlLocalValue(x, "title").ToLowerInvariant();
                    return title.Contains("artist") ? 2 : title.Contains("album") ? 1 : 0;
                }).FirstOrDefault();
            if (all != null) return new[] { all };
            return containers.Where(x =>
            {
                string title = XmlLocalValue(x, "title").ToLowerInvariant();
                return title is not ("preferences" or "watch later" or "recommended" or "music queue" or "music recommendations" or "photo queue" or "photo recommendations");
            });
        }

    internal static int NetworkCategoryMask(IEnumerable<LibraryItem> items)
        {
            int mask = 0;
            foreach (LibraryItem item in items)
            {
                if (string.Equals(item.Category, "Movies", StringComparison.OrdinalIgnoreCase)) mask |= 1;
                else if (string.Equals(item.Category, "TV Series", StringComparison.OrdinalIgnoreCase)) mask |= 2;
                else if (string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase)) mask |= 4;
                else if (string.Equals(item.Category, "Photos", StringComparison.OrdinalIgnoreCase)) mask |= 8;
                else if (string.Equals(item.Category, "Videos", StringComparison.OrdinalIgnoreCase)) mask |= 16;
                if (mask == 31) break;
            }
            return mask;
        }

    internal static List<LibraryItem> NormalizeNetworkItems(IEnumerable<LibraryItem> source)
        {
            return source
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderBy(item => NetworkDuplicateCategoryPriority(item.Category))
                    .ThenByDescending(item => NetworkDuplicateMetadataScore(item))
                    .ThenByDescending(item => !string.IsNullOrWhiteSpace(item.ArtPath))
                    .First())
                .ToList();
        }

    internal static int NetworkDuplicateCategoryPriority(string? category)
        {
            if (string.Equals(category, "TV Series", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (string.Equals(category, "Movies", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(category, "Videos", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "Photos", StringComparison.OrdinalIgnoreCase))
                return 3;
            return 4;
        }

    internal static int NetworkDuplicateMetadataScore(LibraryItem item)
        {
            int score = 0;
            if (!string.IsNullOrWhiteSpace(item.SeriesTitle)) score += 4;
            if (item.SeasonNumber.HasValue) score += 2;
            if (item.EpisodeNumber.HasValue) score += 2;
            if (!string.IsNullOrWhiteSpace(item.AlbumTitle)) score += 3;
            if (!string.IsNullOrWhiteSpace(item.ArtistName)) score += 2;
            if (item.TrackNumber.HasValue) score += 1;
            if (item.Year.HasValue) score += 1;
            return score;
        }

    internal static void TryApplyDlnaHttpHeaders(HttpClient http)
        {
            try { http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CinecorePlayer/1.0 DLNADOC/1.50 UPnP/1.0"); } catch { }
            try { http.DefaultRequestHeaders.TryAddWithoutValidation("X-AV-Client-Info", "CinecorePlayer/1.0"); } catch { }
        }

    internal static async Task<(XDocument Document, string ServiceType, bool QuoteSoapAction)> DlnaBrowseWithFallbacksAsync(HttpClient http, Uri controlUri, string serviceType, string objectId, int start, int count, CancellationToken ct)
        {
            Exception? last = null;
            foreach (string candidateServiceType in NetworkServiceTypeCandidates(serviceType))
            {
                foreach (bool quoteSoapAction in new[] { true, false })
                {
                    try
                    {
                        XDocument document = await DlnaBrowseAsync(http, controlUri, candidateServiceType, objectId, start, count, quoteSoapAction, ct);
                        return (document, candidateServiceType, quoteSoapAction);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }
            }

            throw last ?? new InvalidOperationException("DLNA Browse non riuscito.");
        }

    internal static async Task<XDocument> DlnaBrowseAsync(HttpClient http, Uri controlUri, string serviceType, string objectId, int start, int count, bool quoteSoapAction, CancellationToken ct)
        {
            serviceType = FirstRawNonEmpty(serviceType, "urn:schemas-upnp-org:service:ContentDirectory:1");
            string body =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body>" +
                "<u:Browse xmlns:u=\"" + SecurityElementEscape(serviceType) + "\">" +
                "<ObjectID>" + SecurityElementEscape(objectId) + "</ObjectID>" +
                "<BrowseFlag>BrowseDirectChildren</BrowseFlag>" +
                "<Filter>*</Filter>" +
                "<StartingIndex>" + start.ToString(CultureInfo.InvariantCulture) + "</StartingIndex>" +
                "<RequestedCount>" + count.ToString(CultureInfo.InvariantCulture) + "</RequestedCount>" +
                "<SortCriteria></SortCriteria>" +
                "</u:Browse>" +
                "</s:Body>" +
                "</s:Envelope>";

            using var req = new HttpRequestMessage(HttpMethod.Post, controlUri);
            string action = serviceType + "#Browse";
            req.Headers.TryAddWithoutValidation("SOAPAction", quoteSoapAction ? "\"" + action + "\"" : action);
            req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            res.EnsureSuccessStatusCode();
            string xml = await res.Content.ReadAsStringAsync(ct);
            return XDocument.Parse(xml);
        }

    internal static IEnumerable<string> NetworkServiceTypeCandidates(string? serviceType)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in new[]
            {
                serviceType ?? string.Empty,
                "urn:schemas-upnp-org:service:ContentDirectory:1",
                "urn:schemas-upnp-org:service:ContentDirectory:2",
                "urn:schemas-upnp-org:service:ContentDirectory:3",
                "urn:schemas-upnp-org:service:ContentDirectory:4"
            })
            {
                string clean = FirstRawNonEmpty(value, string.Empty).Trim();
                if (clean.Length > 0 && seen.Add(clean))
                    yield return clean;
            }
        }

    internal static XElement? SelectBestDlnaResource(IReadOnlyList<XElement> resources, string itemClass)
        {
            return resources
                .Where(IsPlayableDlnaResource)
                .OrderByDescending(resource => DlnaResourceScore(resource) + DlnaExpectedKindBonus(resource, itemClass))
                .FirstOrDefault();
        }

    internal static XElement? SelectBestFallbackDlnaResource(IReadOnlyList<XElement> resources, string itemClass)
        {
            return resources
                .Where(r => !string.IsNullOrWhiteSpace((r.Value ?? string.Empty).Trim()))
                .OrderByDescending(resource => DlnaResourceScore(resource) + DlnaExpectedKindBonus(resource, itemClass))
                .FirstOrDefault();
        }

    internal static int DlnaExpectedKindBonus(XElement resource, string itemClass)
        {
            string cls = itemClass?.ToLowerInvariant() ?? string.Empty;
            string protocol = resource.Attribute("protocolInfo")?.Value ?? string.Empty;
            string probe = NetworkProbePath((resource.Value ?? string.Empty).Trim());
            bool video = IsVideoPath(probe) || protocol.Contains("video/", StringComparison.OrdinalIgnoreCase);
            bool audio = IsMusicPath(probe) || protocol.Contains("audio/", StringComparison.OrdinalIgnoreCase);
            bool image = IsPhotoPath(probe) || protocol.Contains("image/", StringComparison.OrdinalIgnoreCase);

            if (cls.Contains("videoitem")) return video ? 500_000 : -250_000;
            if (cls.Contains("audioitem") || cls.Contains("musictrack")) return audio ? 500_000 : -250_000;
            if (cls.Contains("imageitem") || cls.Contains("photo")) return image ? 500_000 : -250_000;
            return 0;
        }

    internal static bool LooksLikePlayableDlnaItem(XElement item)
        {
            string cls = XmlLocalValue(item, "class").ToLowerInvariant();
            return cls.Contains("videoitem") ||
                   cls.Contains("audioitem") ||
                   cls.Contains("musictrack") ||
                   cls.Contains("imageitem") ||
                   cls.Contains("photo");
        }

    internal static int DlnaResourceScore(XElement res)
        {
            try
            {
                string protocol = res.Attribute("protocolInfo")?.Value ?? string.Empty;
                string value = (res.Value ?? string.Empty).Trim();
                string probe = NetworkProbePath(value);
                var size = ParseDlnaResolution(res.Attribute("resolution")?.Value);
                long.TryParse(res.Attribute("size")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes);
                long.TryParse(res.Attribute("bitrate")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bitrate);

                int score = 0;
                if (IsVideoPath(probe) || protocol.Contains("video/", StringComparison.OrdinalIgnoreCase)) score += 100_000;
                if (IsMusicPath(probe) || protocol.Contains("audio/", StringComparison.OrdinalIgnoreCase)) score += 50_000;
                if (IsPhotoPath(probe) || protocol.Contains("image/", StringComparison.OrdinalIgnoreCase)) score += 30_000;
                score += Math.Min(80_000, Math.Max(0, size.Width * size.Height / 40));
                score += (int)Math.Min(25_000, Math.Max(0, bitrate / 1024));
                score += (int)Math.Min(20_000, Math.Max(0, bytes / (1024L * 1024L * 64L)));
                if (protocol.Contains("transcod", StringComparison.OrdinalIgnoreCase)) score -= 40_000;
                if (protocol.Contains("DLNA.ORG_PN", StringComparison.OrdinalIgnoreCase)) score += 2_000;
                // Mac: AVPlayer non legge il PCM grezzo (audio/L16, che il server Windows offre convertito e con il
                // bitrate piu' alto, quindi vinceva) ne' WMA. Meglio il file originale (DLNA.ORG_CI=0) in un formato noto.
                if (protocol.Contains("audio/L16", StringComparison.OrdinalIgnoreCase) || protocol.Contains("audio/x-ms-wma", StringComparison.OrdinalIgnoreCase)) score -= 60_000;
                if (protocol.Contains("DLNA.ORG_CI=1", StringComparison.OrdinalIgnoreCase)) score -= 20_000;
                if (protocol.Contains("audio/mpeg", StringComparison.OrdinalIgnoreCase) || protocol.Contains("audio/mp4", StringComparison.OrdinalIgnoreCase) ||
                    protocol.Contains("flac", StringComparison.OrdinalIgnoreCase) || protocol.Contains("audio/x-m4a", StringComparison.OrdinalIgnoreCase)) score += 30_000;
                return score;
            }
            catch
            {
                return 0;
            }
        }

    internal static (int Width, int Height) ParseDlnaResolution(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return (0, 0);

            var match = Regex.Match(value, @"(?<w>\d{2,5})\s*x\s*(?<h>\d{2,5})", RegexOptions.IgnoreCase);
            if (!match.Success)
                return (0, 0);

            return int.TryParse(match.Groups["w"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) &&
                   int.TryParse(match.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                ? (Math.Max(0, w), Math.Max(0, h))
                : (0, 0);
        }

    internal static string? DlnaResolutionLabel((int Width, int Height) size)
        {
            int longSide = Math.Max(size.Width, size.Height);
            int shortSide = Math.Min(size.Width, size.Height);
            if (longSide <= 0 || shortSide <= 0)
                return null;
            if (longSide >= 3500 || shortSide >= 2000)
                return "4K";
            if (longSide >= 1900 || shortSide >= 1000)
                return "Full HD";
            if (longSide >= 1200 || shortSide >= 700)
                return "720p";
            if (longSide >= 900 || shortSide >= 560)
                return "576p";
            if (longSide >= 700 || shortSide >= 460)
                return "480p";
            return $"{size.Width}x{size.Height}";
        }

    internal static bool IsDlna4K((int Width, int Height) size)
        {
            int longSide = Math.Max(size.Width, size.Height);
            int shortSide = Math.Min(size.Width, size.Height);
            return longSide >= 3500 || shortSide >= 2000;
        }

    internal static string NetworkClassificationHints(XElement item)
        {
            string[] names =
            {
                "seriesTitle", "programTitle", "episodeTitle", "seasonNumber", "episodeNumber",
                "show", "grandparentTitle", "parentTitle", "parentIndex", "index",
                "grandparentIndex", "ratingKey", "parentRatingKey", "grandparentRatingKey",
                "type", "mediaType", "contentType", "librarySectionType", "originallyAvailableAt",
                "originalTrackNumber", "genre", "album", "creator", "artist", "date", "year"
            };

            return string.Join(" ", names
                .Select(name =>
                {
                    string value = XmlLocalValue(item, name);
                    return string.IsNullOrWhiteSpace(value) ? string.Empty : $"{name}:{value}";
                })
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

    internal static LibraryItem? BuildNetworkLibraryItem(XElement item, Uri resourceBase, string serverName, int order, string contextTrail)
        {
            string upnpClass = XmlLocalValue(item, "class");
            var resources = item.Elements().Where(e => e.Name.LocalName == "res").ToList();
            XElement? res = SelectBestDlnaResource(resources, upnpClass) ??
                            (LooksLikePlayableDlnaItem(item) ? SelectBestFallbackDlnaResource(resources, upnpClass) : null);
            if (res == null)
                return null;

            string rawUrl = (res.Value ?? string.Empty).Trim();
            string url = ResolveUpnpUrl(resourceBase, rawUrl);
            if (string.IsNullOrWhiteSpace(url))
                return null;

            string title = FirstRawNonEmpty(XmlLocalValue(item, "title"), NetworkTitleFromUrl(url));
            string protocolInfo = res.Attribute("protocolInfo")?.Value ?? string.Empty;
            string metadataHints = NetworkClassificationHints(item);
            string category = CategoryForNetworkResource(url, title, upnpClass, protocolInfo, contextTrail, metadataHints);
            if (string.IsNullOrWhiteSpace(category))
                return null;

            var metadata = BuildNetworkItemMetadata(item, url, category, contextTrail, title);
            string? art = NetworkArtworkUrl(item, resources, resourceBase, category);

            long bytes = 0;
            long.TryParse(res.Attribute("size")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes);
            string qualitySample = string.Join(" ", new[]
            {
                url,
                title,
                upnpClass,
                protocolInfo,
                contextTrail,
                metadataHints,
                res.Attribute("resolution")?.Value,
                res.Attribute("bitrate")?.Value,
                res.Attribute("sampleFrequency")?.Value,
                res.Attribute("nrAudioChannels")?.Value
            }.Where(v => !string.IsNullOrWhiteSpace(v)));
            var resSize = ParseDlnaResolution(res.Attribute("resolution")?.Value);
            string? resolutionLabel = FirstRawNonEmpty(DlnaResolutionLabel(resSize), InferResolutionLabel(qualitySample));
            bool textSdr = HasSdrToken(qualitySample);
            bool is4K = IsDlna4K(resSize) || Has4KToken(qualitySample);
            bool isHdr = !textSdr && HasHdrToken(qualitySample);
            string? audioLabel = InferAudioLabel(qualitySample);

            return new LibraryItem
            {
                Path = url,
                Title = metadata.Title,
                Category = category,
                PlaybackCategory = PlaybackCategoryForDisplayCategory(category),
                Year = metadata.Year,
                DurationMinutes = ParseDlnaDurationMinutes(res.Attribute("duration")?.Value),
                SortDateUtc = DateTime.TryParse(XmlLocalValue(item, "date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var mediaDate)
                    ? mediaDate : DateTime.UtcNow.AddSeconds(-order),
                ArtPath = string.Equals(category, "Photos", StringComparison.OrdinalIgnoreCase) ? art ?? url : art,
                WideArtPath = art,
                Overview = string.Empty,
                AudioLabel = string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase)
                    ? FirstRawNonEmpty(audioLabel, "DLNA")
                    : audioLabel,
                ResolutionLabel = IsNetworkVideoCategory(category) ? resolutionLabel : null,
                DlnaVideoResources = IsNetworkVideoCategory(category) ? VideoResources(resources, res, url, resourceBase) : null,
                SeriesTitle = metadata.SeriesTitle,
                SeasonNumber = metadata.SeasonNumber,
                EpisodeNumber = metadata.EpisodeNumber,
                AlbumTitle = metadata.AlbumTitle,
                ArtistName = metadata.ArtistName,
                AlbumArtist = metadata.AlbumArtist,
                TrackNumber = metadata.TrackNumber,
                Is4K = is4K,
                IsHdr = isHdr,
                HasAtmos = qualitySample.Contains("atmos", StringComparison.OrdinalIgnoreCase) ||
                           qualitySample.Contains("dts:x", StringComparison.OrdinalIgnoreCase) ||
                           qualitySample.Contains("dts-x", StringComparison.OrdinalIgnoreCase) ||
                           qualitySample.Contains("dtsx", StringComparison.OrdinalIgnoreCase),
                Bytes = Math.Max(0, bytes)
            };
        }

    internal static List<DlnaVideoResource> VideoResources(IEnumerable<XElement> resources, XElement primary, string primaryUrl, Uri resourceBase)
    {
        var variants = new List<DlnaVideoResource>();
        foreach (var res in resources.OrderByDescending(r => r == primary).ThenByDescending(DlnaResourceScore))
        {
            string raw = res.Value.Trim();
            var (width, height) = ParseDlnaResolution(res.Attribute("resolution")?.Value);
            string protocol = res.Attribute("protocolInfo")?.Value ?? "";
            if (res != primary && !IsVideoPath(NetworkProbePath(raw)) && !protocol.Contains("video/", StringComparison.OrdinalIgnoreCase)
                && !(width > 0 && protocol.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase))) continue;
            string url = res == primary ? primaryUrl : ResolveUpnpUrl(resourceBase, raw);
            if (!MacWebResolver.IsWebAddress(url) || variants.Any(v => v.Path == url)) continue;
            long.TryParse(res.Attribute("bitrate")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long rate);
            variants.Add(new DlnaVideoResource(url, width, height, Math.Max(0, rate)));
        }
        return variants;
    }

    internal static NetworkItemMetadata BuildNetworkItemMetadata(XElement item, string url, string category, string contextTrail, string rawTitle)
        {
            string probe = NetworkProbePath(url);
            string title = CleanNetworkDisplayTitle(FirstRawNonEmpty(rawTitle, XmlLocalValue(item, "title"), NetworkTitleFromUrl(url)));
            var pathInfo = TryExtractMediaTitleInfo(FirstRawNonEmpty(probe, title));
            var titleInfo = TryExtractMediaTitleInfo(title);
            int? year = TryParseNetworkYear(
                XmlLocalValue(item, "date"),
                XmlLocalValue(item, "year"),
                XmlLocalValue(item, "originallyAvailableAt"),
                title,
                probe) ?? pathInfo?.Year ?? titleInfo?.Year;

            if (string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase))
            {
                string artist = CleanMusicTitle(FirstRawNonEmpty(
                    XmlLocalValue(item, "artist"),
                    XmlLocalValue(item, "albumArtist"),
                    XmlLocalValue(item, "creator"),
                    XmlLocalValue(item, "author")));
                string albumArtist = CleanMusicTitle(XmlLocalValue(item, "albumArtist"));
                string album = CleanMusicTitle(FirstRawNonEmpty(
                    XmlLocalValue(item, "album"),
                    XmlLocalValue(item, "albumTitle"),
                    XmlLocalValue(item, "collectionTitle")));
                int? track = TryParseNetworkInt(
                    XmlLocalValue(item, "originalTrackNumber"),
                    XmlLocalValue(item, "trackNumber"),
                    XmlLocalValue(item, "track")) ?? TryParseTrackNumber(title) ?? TryParseTrackNumber(probe);
                string trackTitle = CleanMusicTrackTitle(FirstRawNonEmpty(title, NetworkTitleFromUrl(url)));
                return new NetworkItemMetadata
                {
                    Title = trackTitle,
                    Year = year,
                    AlbumTitle = string.IsNullOrWhiteSpace(album) ? null : album,
                    ArtistName = string.IsNullOrWhiteSpace(artist) ? null : artist,
                    AlbumArtist = string.IsNullOrWhiteSpace(albumArtist) ? null : albumArtist,
                    TrackNumber = track
                };
            }

            if (string.Equals(category, "TV Series", StringComparison.OrdinalIgnoreCase))
            {
                string series = FirstNonEmpty(
                    XmlLocalValue(item, "seriesTitle"),
                    XmlLocalValue(item, "programTitle"),
                    XmlLocalValue(item, "show"),
                    XmlLocalValue(item, "grandparentTitle"),
                    XmlLocalValue(item, "grandparentName"),
                    pathInfo?.SeriesTitle,
                    titleInfo?.SeriesTitle,
                    ExtractSeriesTitleFromDisplay(title),
                    NetworkSeriesTitleFromTrail(contextTrail));
                int? season = TryParseNetworkInt(
                    XmlLocalValue(item, "seasonNumber"),
                    XmlLocalValue(item, "season"),
                    XmlLocalValue(item, "parentIndex"),
                    XmlLocalValue(item, "grandparentIndex")) ?? pathInfo?.SeasonNumber ?? titleInfo?.SeasonNumber ?? TryParseSeasonNumber(title) ?? TryParseSeasonNumber(probe);
                int? episode = TryParseNetworkInt(
                    XmlLocalValue(item, "episodeNumber"),
                    XmlLocalValue(item, "episode"),
                    XmlLocalValue(item, "index"),
                    XmlLocalValue(item, "originalTrackNumber")) ?? pathInfo?.EpisodeNumber ?? titleInfo?.EpisodeNumber ?? TryParseEpisodeNumber(title) ?? TryParseEpisodeNumber(probe);
                string episodeTitle = FirstNonEmpty(
                    XmlLocalValue(item, "episodeTitle"),
                    pathInfo?.EpisodeTitle,
                    titleInfo?.EpisodeTitle,
                    ExtractEpisodeTitleFromDisplay(title),
                    title);
                string display = title;
                if (!string.IsNullOrWhiteSpace(series) && (season.HasValue || episode.HasValue))
                {
                    string seasonEpisode = season.HasValue && episode.HasValue
                        ? $"S{season.Value:00}E{episode.Value:00}"
                        : season.HasValue
                            ? $"Season {season.Value:00}"
                            : $"E{episode!.Value:00}";
                    string suffix = string.IsNullOrWhiteSpace(episodeTitle) ? string.Empty : " - " + episodeTitle;
                    display = $"{series} {seasonEpisode}{suffix}";
                }

                return new NetworkItemMetadata
                {
                    Title = CleanTitle(display),
                    Year = year,
                    SeriesTitle = string.IsNullOrWhiteSpace(series) ? null : series,
                    SeasonNumber = season,
                    EpisodeNumber = episode
                };
            }

            if (string.Equals(category, "Movies", StringComparison.OrdinalIgnoreCase))
            {
                // The DIDL title is usually the film title; the resource URL is
                // often an opaque/transcoded endpoint. Normalize the title itself
                // before using it for artwork and TMDb queries.
                string parsedTitle = LooksLikeGenericNetworkTitle(title)
                    ? FirstNonEmpty(pathInfo?.NormalizedTitle, titleInfo?.NormalizedTitle)
                    : FirstNonEmpty(titleInfo?.NormalizedTitle, title);
                if (!string.IsNullOrWhiteSpace(parsedTitle))
                    title = parsedTitle;

                return new NetworkItemMetadata
                {
                    Title = CleanTitle(title),
                    Year = year
                };
            }

            return new NetworkItemMetadata
            {
                Title = string.Equals(category, "Photos", StringComparison.OrdinalIgnoreCase)
                    ? CleanTitle(title)
                    : CleanTitle(FirstRawNonEmpty(title, NetworkTitleFromUrl(url))),
                Year = year
            };
        }

    internal static string CleanNetworkDisplayTitle(string? value)
        {
            string clean = WebUtility.HtmlDecode(value ?? string.Empty);
            clean = clean.Replace('\u00a0', ' ');
            clean = Regex.Replace(clean, @"[_\.]+", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '_', '.');
            return string.IsNullOrWhiteSpace(clean) ? "Network item" : clean;
        }

    internal static bool LooksLikeGenericNetworkTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;
            string clean = value.Trim();
            if (clean.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("video", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("movie", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("network item", StringComparison.OrdinalIgnoreCase))
                return true;
            return Regex.IsMatch(clean, @"^(?:\d+|part\s*\d+|stream|media)$", RegexOptions.IgnoreCase);
        }

    internal static int? TryParseNetworkYear(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                var match = Regex.Match(value, @"(?<!\d)(19\d{2}|20\d{2}|2100)(?!\d)");
                if (match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int year))
                    return year;
            }
            return null;
        }

    internal static int? TryParseNetworkInt(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                var match = Regex.Match(value.Trim(), @"(?<!\d)\d{1,4}(?!\d)");
                if (match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                    return number;
            }
            return null;
        }

    internal static string NetworkSeriesTitleFromTrail(string? trail)
        {
            if (string.IsNullOrWhiteSpace(trail))
                return string.Empty;

            var parts = Regex.Split(WebUtility.HtmlDecode(trail), @"\s*/\s+|\s+/\s*|/")
                .Select(part => Regex.Replace(part, @"\s+", " ").Trim(' ', '-', ':'))
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();
            if (parts.Count == 0)
                parts = Regex.Split(trail, @"\s{2,}")
                    .Select(part => Regex.Replace(part, @"\s+", " ").Trim(' ', '-', ':'))
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .ToList();

            for (int i = parts.Count - 1; i >= 0; i--)
            {
                string part = parts[i];
                string normalized = NormalizeNetworkTrail(part);
                if (normalized.Contains(" object ") ||
                    normalized.Contains(" container ") ||
                    normalized.Contains(" storagefolder ") ||
                    normalized.Contains(" browse ") ||
                    normalized.Contains(" folders ") ||
                    normalized.Contains(" videos ") ||
                    normalized.Contains(" video ") ||
                    normalized.Contains(" tv shows ") ||
                    normalized.Contains(" tv series ") ||
                    normalized.Contains(" season ") ||
                    normalized.Contains(" stagione "))
                    continue;
                return CleanTitle(part);
            }

            return string.Empty;
        }

    internal static bool IsPlayableDlnaResource(XElement res)
        {
            string url = (res.Value ?? string.Empty).Trim();
            string protocol = res.Attribute("protocolInfo")?.Value ?? string.Empty;
            string probe = NetworkProbePath(url);
            return IsVideoPath(probe) || IsMusicPath(probe) || IsPhotoPath(probe) ||
                   protocol.Contains("video/", StringComparison.OrdinalIgnoreCase) ||
                   protocol.Contains("audio/", StringComparison.OrdinalIgnoreCase) ||
                   protocol.Contains("image/", StringComparison.OrdinalIgnoreCase);
        }

    internal static string? NetworkArtworkUrl(XElement item, IReadOnlyList<XElement> resources, Uri resourceBase, string category)
        {
            bool tv = string.Equals(category, "TV Series", StringComparison.OrdinalIgnoreCase);
            string? hierarchyArt = tv
                ? FirstRawNonEmpty(
                    XmlLocalValue(item, "grandparentThumb"),
                    XmlLocalValue(item, "grandparentArt"),
                    XmlLocalValue(item, "seriesPoster"),
                    XmlLocalValue(item, "showPoster"),
                    XmlLocalValue(item, "parentThumb"),
                    XmlLocalValue(item, "parentArt"))
                : null;
            string? art = FirstRawNonEmpty(
                hierarchyArt,
                XmlLocalValue(item, "albumArtURI"),
                XmlLocalValue(item, "albumArtUri"),
                XmlLocalValue(item, "poster"),
                XmlLocalValue(item, "cover"),
                XmlLocalValue(item, "coverArt"),
                XmlLocalValue(item, "artwork"),
                XmlLocalValue(item, "thumb"),
                XmlLocalValue(item, "thumbnail"),
                XmlLocalValue(item, "icon"),
                XmlLocalValue(item, "smallIcon"),
                XmlLocalValue(item, "mediumIcon"),
                XmlLocalValue(item, "largeIcon"),
                XmlLocalValue(item, "picture"),
                XmlLocalValue(item, "image"));

            if (string.IsNullOrWhiteSpace(art))
            {
                art = resources
                    .Where(r =>
                    {
                        string protocol = r.Attribute("protocolInfo")?.Value ?? string.Empty;
                        string value = (r.Value ?? string.Empty).Trim();
                        return protocol.Contains("image/", StringComparison.OrdinalIgnoreCase) || IsPhotoPath(NetworkProbePath(value));
                    })
                    .Select(r => (r.Value ?? string.Empty).Trim())
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            }

            if (string.IsNullOrWhiteSpace(art))
                return null;

            string resolved = ResolveUpnpUrl(resourceBase, art);
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }

    internal static string CategoryForNetworkResource(string url, string title, string upnpClass, string protocolInfo, string contextTrail, string metadataHints)
        {
            string probe = NetworkProbePath(url);
            string sample = string.Join(" ", new[] { probe, title, upnpClass, protocolInfo, contextTrail, metadataHints }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
                .ToLowerInvariant();
            string trail = contextTrail ?? string.Empty;
            // Opaque/transcoded URLs often have misleading extensions. Trust
            // the advertised item class and MIME before looking at the path.
            if (upnpClass.Contains("audioItem", StringComparison.OrdinalIgnoreCase) || protocolInfo.Contains("audio/", StringComparison.OrdinalIgnoreCase))
                return "Music";
            if (upnpClass.Contains("imageItem", StringComparison.OrdinalIgnoreCase) || protocolInfo.Contains("image/", StringComparison.OrdinalIgnoreCase))
                return "Photos";

            // Il tipo della risorsa scelta e la classe UPnP hanno precedenza sul
            // nome dei contenitori. Prima bastava attraversare una cartella "Foto"
            // perché un vero video venisse etichettato come immagine.
            bool videoResource = IsVideoPath(probe) || protocolInfo.Contains("video/", StringComparison.OrdinalIgnoreCase) ||
                                 upnpClass.Contains("videoitem", StringComparison.OrdinalIgnoreCase);
            bool audioResource = IsMusicPath(probe) || protocolInfo.Contains("audio/", StringComparison.OrdinalIgnoreCase) ||
                                 upnpClass.Contains("audioitem", StringComparison.OrdinalIgnoreCase) ||
                                 upnpClass.Contains("musictrack", StringComparison.OrdinalIgnoreCase);
            bool imageResource = IsPhotoPath(probe) || protocolInfo.Contains("image/", StringComparison.OrdinalIgnoreCase) ||
                                 upnpClass.Contains("imageitem", StringComparison.OrdinalIgnoreCase) ||
                                 upnpClass.Contains("photo", StringComparison.OrdinalIgnoreCase);

            if (videoResource)
            {
                var parsedTitle = TryExtractMediaTitleInfo(title);
                var parsedProbe = TryExtractMediaTitleInfo(probe);
                string normalizedTrail = NormalizeNetworkTrail(trail);
                if (ParsedLooksLikeNetworkEpisode(parsedTitle) ||
                    ParsedLooksLikeNetworkEpisode(parsedProbe) ||
                    LooksLikeNetworkTvItem(string.Join(" ", title, upnpClass, metadataHints)))
                    return "TV Series";

                // Le classi esplicite di Plex/UPnP sono più affidabili del root
                // generico "Video" che compare nel trail di quasi ogni libreria.
                if (upnpClass.Contains("videoitem.movie", StringComparison.OrdinalIgnoreCase) || HasNetworkMovieTrail(normalizedTrail))
                    return "Movies";
                if (HasNetworkTvTrail(normalizedTrail))
                    return "TV Series";

                if (LooksLikeNetworkPersonalVideoItem(sample))
                    return "Videos";

                if (LooksLikeNetworkMovieItem(sample, trail))
                    return "Movies";

                if ((parsedTitle?.Year.HasValue == true || parsedProbe?.Year.HasValue == true) &&
                    !LooksLikeNetworkDateNamedVideo(sample))
                    return "Movies";

                if (LooksLikeNetworkVideosContainer(trail))
                    return "Videos";

                return "Videos";
            }
            if (audioResource)
                return "Music";
            if (imageResource)
                return "Photos";

            // Fallback per server non conformi che omettono MIME e classi.
            if (sample.Contains("video/")) return "Videos";
            if (sample.Contains("audio/") || sample.Contains("musictrack")) return "Music";
            if (sample.Contains("image/") || sample.Contains("photo")) return "Photos";
            return string.Empty;
        }

    internal static bool ParsedLooksLikeNetworkEpisode(MovieMetadataService.MediaTitleInfo? parsed)
        {
            return parsed?.EpisodeNumber.HasValue == true ||
                   (parsed?.SeasonNumber.HasValue == true && !string.IsNullOrWhiteSpace(parsed.SeriesTitle));
        }

    internal static bool LooksLikeNetworkMovieItem(string sample, string trail)
        {
            string normalizedTrail = NormalizeNetworkTrail(trail);
            bool movieTrail = HasNetworkMovieTrail(normalizedTrail);
            bool movieClass = sample.Contains("videoitem.movie") ||
                              sample.Contains("object.item.videoitem.movie");
            bool genericVideoTrail = HasGenericNetworkVideoTrail(normalizedTrail);

            if (movieTrail)
                return true;

            return movieClass && !LooksLikeNetworkDateNamedVideo(sample);
        }

    internal static bool LooksLikeNetworkVideosContainer(string trail)
        {
            string normalizedTrail = NormalizeNetworkTrail(trail);
            return normalizedTrail.Contains(" home video ") ||
                   normalizedTrail.Contains(" home videos ") ||
                   normalizedTrail.Contains(" personal video ") ||
                   normalizedTrail.Contains(" personal videos ") ||
                   normalizedTrail.Contains(" clips ") ||
                   normalizedTrail.Contains(" recordings ") ||
                   normalizedTrail.Contains(" camcorder ") ||
                   (HasGenericNetworkVideoTrail(normalizedTrail) &&
                    !HasNetworkMovieTrail(normalizedTrail) &&
                    !HasNetworkTvTrail(normalizedTrail));
        }

    internal static bool LooksLikeNetworkPersonalVideoItem(string sample)
        {
            return sample.Contains("object.item.videoitem.videoBroadcast", StringComparison.OrdinalIgnoreCase) ||
                   sample.Contains("homevideo") ||
                   sample.Contains("home video") ||
                   sample.Contains("personal") ||
                   sample.Contains("camcorder") ||
                   sample.Contains("recording") ||
                   sample.Contains("clip") ||
                   LooksLikeNetworkDateNamedVideo(sample);
        }

    internal static string NormalizeNetworkTrail(string value)
        {
            return " " + Regex.Replace(value ?? string.Empty, @"[^a-z0-9]+", " ").Trim().ToLowerInvariant() + " ";
        }

    internal static bool HasNetworkMovieTrail(string normalizedTrail)
        {
            return normalizedTrail.Contains(" movie ") ||
                   normalizedTrail.Contains(" movies ") ||
                   normalizedTrail.Contains(" film ") ||
                   normalizedTrail.Contains(" films ") ||
                   normalizedTrail.Contains(" films by ") ||
                   normalizedTrail.Contains(" cinema ");
        }

    internal static bool HasNetworkTvTrail(string normalizedTrail)
        {
            return normalizedTrail.Contains(" tv show ") ||
                   normalizedTrail.Contains(" tv shows ") ||
                   normalizedTrail.Contains(" tv series ") ||
                   normalizedTrail.Contains(" television ") ||
                   normalizedTrail.Contains(" serie tv ") ||
                   normalizedTrail.Contains(" programmi tv ") ||
                   normalizedTrail.Contains(" all shows ") ||
                   normalizedTrail.Contains(" season ") ||
                   normalizedTrail.Contains(" stagione ");
        }

    internal static bool HasGenericNetworkVideoTrail(string normalizedTrail)
        {
            return normalizedTrail.Contains(" video ") ||
                   normalizedTrail.Contains(" videos ") ||
                   normalizedTrail.Contains(" clip ") ||
                   normalizedTrail.Contains(" clips ") ||
                   normalizedTrail.Contains(" recording ") ||
                   normalizedTrail.Contains(" recordings ");
        }

    internal static bool LooksLikeNetworkDateNamedVideo(string sample)
        {
            if (string.IsNullOrWhiteSpace(sample))
                return false;

            return Regex.IsMatch(sample, @"\b(?:vid|pxl|img|dsc|dscn|mvi|gopr|gopro|gh\d{2}|wa)[-_ ]?\d{4,}", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(sample, @"\b(?:20\d{2}|19\d{2})[-_. ](?:0[1-9]|1[0-2])[-_. ](?:0[1-9]|[12]\d|3[01])\b", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(sample, @"\b(?:0[1-9]|[12]\d|3[01])[-_. ](?:0[1-9]|1[0-2])[-_. ](?:20\d{2}|19\d{2})\b", RegexOptions.IgnoreCase);
        }

    internal static bool LooksLikeNetworkTvItem(string sample)
        {
            if (string.IsNullOrWhiteSpace(sample))
                return false;
            sample = sample.ToLowerInvariant();
            if (TvEpisodeRegex.IsMatch(sample))
                return true;
            if (sample.Contains("seriestitle:") ||
                sample.Contains("episodetitle:") ||
                sample.Contains("seasonnumber:") ||
                sample.Contains("episodenumber:") ||
                sample.Contains("type:episode") ||
                sample.Contains("mediatype:episode") ||
                sample.Contains("contenttype:episode") ||
                sample.Contains("librarysectiontype:show") ||
                sample.Contains("videoitem.episode") ||
                sample.Contains("object.item.videoitem.episode"))
                return true;

            // Plex espone spesso gli episodi come videoItem generici: la coppia
            // grandparentTitle (serie) + parentIndex/index (stagione/episodio) è
            // più affidabile della classe UPnP in quel caso.
            if ((sample.Contains("grandparenttitle:") || sample.Contains("seriestitle:") || sample.Contains("show:")) &&
                (sample.Contains("parentindex:") || sample.Contains("episodenumber:") || sample.Contains(" index:")))
                return true;

            if (Regex.IsMatch(sample, @"\b(?:season|stagione)\s*\d{1,2}\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(sample, @"\b(?:episode|episodio|ep)\s*\d{1,3}\b", RegexOptions.IgnoreCase))
                return true;

            return false;
        }

    internal static string BestNetworkStartCategory(IReadOnlyList<LibraryItem> items)
        {
            var best = items
                .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key == "Movies" ? 0 : group.Key == "TV Series" ? 1 : group.Key == "Music" ? 2 : 3)
                .FirstOrDefault()?.Key;
            return NormalizeCategoryKey(best) is string normalized && !string.IsNullOrWhiteSpace(normalized) ? normalized : "Movies";
        }

    internal static bool IsNetworkVideoItem(LibraryItem item)
        {
            return string.Equals(item.Category, "Videos", StringComparison.OrdinalIgnoreCase) ||
                   (IsVideoPath(NetworkProbePath(item.Path)) &&
                    !string.Equals(item.Category, "Movies", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.Category, "TV Series", StringComparison.OrdinalIgnoreCase));
        }

    internal static bool IsNetworkVideoCategory(string category)
        {
            return string.Equals(category, "Movies", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(category, "TV Series", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(category, "Videos", StringComparison.OrdinalIgnoreCase);
        }

    internal static bool IsNetworkPath(string path)
        {
            try
            {
                return Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                       (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

    internal static string NetworkProbePath(string pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
                return string.Empty;
            try
            {
                if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri))
                    return Uri.UnescapeDataString(uri.AbsolutePath);
            }
            catch { }

            int cut = pathOrUrl.IndexOfAny(new[] { '?', '#' });
            return cut >= 0 ? pathOrUrl[..cut] : pathOrUrl;
        }

    internal static string NetworkTitleFromUrl(string url)
        {
            try
            {
                string probe = NetworkProbePath(url);
                string name = Path.GetFileNameWithoutExtension(probe);
                if (!string.IsNullOrWhiteSpace(name))
                    return Uri.UnescapeDataString(name).Replace('.', ' ').Replace('_', ' ');
            }
            catch { }
            return "Network item";
        }

    internal static double? ParseDlnaDurationMinutes(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string clean = value.Trim();
            int dot = clean.IndexOf('.');
            if (dot > 0)
                clean = clean[..dot];
            if (TimeSpan.TryParse(clean, CultureInfo.InvariantCulture, out var ts) && ts.TotalSeconds > 0)
                return ts.TotalMinutes;
            return null;
        }

    internal static string ExtractSoapResultXml(XDocument doc)
        {
            var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Result");
            if (result == null)
                return string.Empty;
            if (result.HasElements)
                return string.Concat(result.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));
            return result.Value ?? string.Empty;
        }

    internal static bool TryParseDidlDocument(string raw, out XDocument didl)
        {
            didl = new XDocument();
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            foreach (string candidate in DidlXmlCandidates(raw))
            {
                try
                {
                    didl = XDocument.Parse(candidate);
                    return true;
                }
                catch { }
            }

            return false;
        }

    internal static IEnumerable<string> DidlXmlCandidates(string raw)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string candidate in new[]
            {
                raw.Trim(),
                WebUtility.HtmlDecode(raw).Trim()
            })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;
                if (seen.Add(candidate))
                    yield return candidate;

                string sanitized = EscapeBareXmlAmpersands(candidate);
                if (seen.Add(sanitized))
                    yield return sanitized;
            }
        }

    internal static string EscapeBareXmlAmpersands(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml) || xml.IndexOf('&') < 0)
                return xml;

            return Regex.Replace(xml, "&(?!amp;|lt;|gt;|quot;|apos;|#\\d+;|#x[0-9a-fA-F]+;)", "&amp;");
        }

    internal static int SoapIntValue(XDocument doc, string localName)
        {
            string value = XmlLocalValue(doc.Root, localName);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        }

    internal static string SecurityElementEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

    internal static string ShortNetworkError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return global::CinecorePlayer2025.Utilities.AppLanguage.T("errore sconosciuto", "unknown error");

            string clean = error.Trim()
                .Replace("\r", " ")
                .Replace("\n", " ");
            while (clean.Contains("  ", StringComparison.Ordinal))
                clean = clean.Replace("  ", " ");
            return clean.Length > 96 ? clean[..96] + "..." : clean;
        }

    internal static string CleanTitle(string title)
        {
            string value = Regex.Replace((title ?? string.Empty).Trim(), "\\s+", " ");
            return string.IsNullOrWhiteSpace(value) ? "Untitled" : value;
        }

    internal static bool IsVideoPath(string path)
        {
            string ext = MediaPathExtension(path);
            return ext is ".mkv" or ".mp4" or ".m4v" or ".mov" or ".avi" or ".wmv" or ".webm" or ".flv" or ".m2ts" or ".ts" or ".iso" or ".cinedisc";
        }

    internal static bool IsMusicPath(string path)
        {
            string ext = MediaPathExtension(path);
            return ext == ".cdda" || global::CinecorePlayer2025.Utilities.MediaFileKinds.IsAudioExtension(ext);
        }

    internal static bool IsPhotoPath(string path)
        {
            string ext = MediaPathExtension(path);
            return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tiff" or ".webp";
        }

    internal static string MediaPathExtension(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            try
            {
                string probe = path;
                if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    probe = Uri.UnescapeDataString(uri.AbsolutePath);
                return (Path.GetExtension(probe) ?? string.Empty).ToLowerInvariant();
            }
            catch { return string.Empty; }
        }

    internal static string NormalizeRootPath(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;
            string path = input.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (Regex.IsMatch(path, @"^[A-Za-z]:$"))
                path += Path.DirectorySeparatorChar;
            try
            {
                if (Path.IsPathRooted(path))
                    path = Path.GetFullPath(path);
            }
            catch { }
            return path;
        }

    internal static string FirstRawNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return Regex.Replace(value!.Trim(), @"\s+", " ");
            }
            return string.Empty;
        }

    internal static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return CleanTitle(value!);
            }
            return string.Empty;
        }

    internal static MovieMetadataService.MediaTitleInfo? TryExtractMediaTitleInfo(string path)
        {
            try { return MovieMetadataService.ExtractMediaTitleInfoFromPath(path); }
            catch { return null; }
        }

    internal static string NormalizeCategoryKey(string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return string.Empty;

            string value = category.Trim();
            return value switch
            {
                "Film" or "Movie" or "Movies" => "Movies",
                "Video" or "Videos" => "Videos",
                "Musica" or "Music" => "Music",
                "Foto" or "Photo" or "Photos" => "Photos",
                "Serie" or "Serie TV" or "TV" or "TV Series" => "TV Series",
                "Preferiti" or "Favourites" or "Favorites" => "Favourites",
                "Diario" or "Visti" or "Diary" or "WatchHistory" => "WatchHistory",
                "Playlist" or "Playlists" => "Playlists",
                _ => value
            };
        }

    internal static string? InferResolutionLabel(string hay)
        {
            string sample = hay ?? string.Empty;
            if (Has4KToken(sample))
                return "4K";
            if (Regex.IsMatch(sample, @"(?<!\d)1080p?(?![a-z0-9])", RegexOptions.IgnoreCase))
                return "Full HD";
            if (Regex.IsMatch(sample, @"(?<!\d)720p?(?![a-z0-9])", RegexOptions.IgnoreCase))
                return "720p";
            if (Regex.IsMatch(sample, @"(?<!\d)576p?(?![a-z0-9])", RegexOptions.IgnoreCase))
                return "576p";
            if (Regex.IsMatch(sample, @"(?<!\d)480p?(?![a-z0-9])", RegexOptions.IgnoreCase))
                return "480p";
            return null;
        }

    internal static bool HasSdrToken(string sample)
        {
            return !string.IsNullOrWhiteSpace(sample) &&
                   Regex.IsMatch(sample, @"(?<![a-z0-9])sdr(?![a-z0-9])", RegexOptions.IgnoreCase);
        }

    internal static bool Has4KToken(string sample)
        {
            return !string.IsNullOrWhiteSpace(sample) &&
                   Regex.IsMatch(sample, @"(?<!\d)(?:2160p?|4k|uhd)(?![a-z0-9])", RegexOptions.IgnoreCase);
        }

    internal static bool HasHdrToken(string sample)
        {
            return !string.IsNullOrWhiteSpace(sample) &&
                   Regex.IsMatch(sample, @"(?<![a-z0-9])(?:hdr10\+?|hdr|dolby[ ._-]?vision|dovi|dv)(?![a-z0-9])", RegexOptions.IgnoreCase);
        }

    internal static string? InferAudioLabel(string hay)
        {
            string sample = hay ?? string.Empty;
            if (sample.Contains("atmos")) return "Dolby Atmos";
            if (sample.Contains("dts:x") || sample.Contains("dts-x") || sample.Contains("dtsx")) return "DTS:X";
            if (sample.Contains("truehd")) return "Dolby TrueHD";
            if (sample.Contains("dts-hd") || sample.Contains("dtshd") || sample.Contains("dts hd")) return "DTS-HD";
            if (sample.Contains("eac3") || sample.Contains("e-ac-3") || sample.Contains("ddp")) return "Dolby Digital+";
            if (sample.Contains("ac3") || sample.Contains("ac-3")) return "Dolby Digital";
            if (sample.Contains("dts")) return "DTS";
            if (Regex.IsMatch(sample, @"(?<!\d)7[._ ]1(?!\d)")) return "7.1";
            if (Regex.IsMatch(sample, @"(?<!\d)5[._ ]1(?!\d)")) return "5.1";
            if (Regex.IsMatch(sample, @"(?<!\d)2[._ ]0(?!\d)")) return "2.0";
            return null;
        }

    internal static string PlaybackCategoryForDisplayCategory(string? category)
        {
            return category switch
            {
                "Movies" => "Film",
                "TV Series" => "Serie",
                "Videos" => "Video",
                "Music" => "Musica",
                "Photos" => "Foto",
                _ => "Video"
            };
        }

    internal static string CleanMusicTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string clean = Regex.Replace(value.Trim(), @"[_\.]+", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '_', '.');
            return clean;
        }

    internal static int? TryParseTrackNumber(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var match = Regex.Match(name, @"^\s*(?:disc\s*\d+\s*)?(?<n>\d{1,3})(?:\s*[-_.\)]|\s+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(name, @"^\s*\d{1,2}\s*[-_.]\s*(?<n>\d{1,3})(?:\s*[-_.\)]|\s+)", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups["n"].Value, out int value) ? value : null;
        }

    internal static string CleanMusicTrackTitle(string? value)
        {
            string clean = MusicTextIdentity.Title(value);
            return string.IsNullOrWhiteSpace(clean) ? "Untitled" : clean;
        }

    internal static string? ExtractSeriesTitleFromDisplay(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            string value = Regex.Replace(title!, @"\s+", " ").Trim();
            var match = Regex.Match(value, @"^(?<title>.+?)\s*(?:[-:]\s*)?(?:S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3}|E\d{1,3})\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string head = match.Groups["title"].Value.Trim(' ', '-', ':');
                if (!string.IsNullOrWhiteSpace(head))
                    return CleanTitle(head);
            }

            match = Regex.Match(value, @"^(?<title>.+?)\s*[-:]\s*(?:Stagione|Season)\s*\d{1,2}\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string head = match.Groups["title"].Value.Trim(' ', '-', ':');
                if (!string.IsNullOrWhiteSpace(head))
                    return CleanTitle(head);
            }

            return null;
        }

    internal static int? TryParseSeasonNumber(string path)
        {
            foreach (string sample in MediaPathSegmentsFromLeaf(path))
            {
                var match = Regex.Match(sample, @"\bS(?<n>\d{1,2})\s*[-_. ]?\s*E\d{1,3}\b", RegexOptions.IgnoreCase);
                if (!match.Success)
                    match = Regex.Match(sample, @"\b(?<n>\d{1,2})x\d{1,3}\b", RegexOptions.IgnoreCase);
                if (!match.Success)
                    match = Regex.Match(sample, @"\b(?:season|stagione)\s*(?<n>\d{1,2})\b", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups["n"].Value, out int value))
                    return value;
            }
            return null;
        }

    internal static int? TryParseEpisodeNumber(string path)
        {
            foreach (string sample in MediaPathSegmentsFromLeaf(path))
            {
                var match = Regex.Match(sample, @"\bS\d{1,2}\s*[-_. ]?\s*E(?<n>\d{1,3})\b", RegexOptions.IgnoreCase);
                if (!match.Success)
                    match = Regex.Match(sample, @"\b\d{1,2}x(?<n>\d{1,3})\b", RegexOptions.IgnoreCase);
                if (!match.Success)
                    match = Regex.Match(sample, @"\b(?:episode|episodio|ep)\s*(?<n>\d{1,3})\b", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups["n"].Value, out int value))
                    return value;
            }
            return null;
        }

    internal static string? ExtractEpisodeTitleFromDisplay(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            string value = Regex.Replace(title!, @"\s+", " ").Trim();
            var match = Regex.Match(value, @"(?:S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3}|E\d{1,3})\s*[-:]\s*(?<title>.+)$", RegexOptions.IgnoreCase);
            if (match.Success)
                return CleanTitle(match.Groups["title"].Value);

            int dash = value.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash + 3 < value.Length)
                return CleanTitle(value.Substring(dash + 3));

            return null;
        }

    internal static IEnumerable<string> MediaPathSegmentsFromLeaf(string path)
        {
            string file = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(file))
                yield return file;

            string? directory = Path.GetDirectoryName(path);
            int depth = 0;
            while (!string.IsNullOrWhiteSpace(directory) && depth++ < 5)
            {
                string segment = Path.GetFileName(directory) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(segment))
                    yield return segment;
                directory = Path.GetDirectoryName(directory);
            }
        }

    internal static readonly Regex TvEpisodeRegex = new(
            @"(?:\bS\d{1,2}E\d{1,3}\b)|(?:\b\d{1,2}x\d{1,3}\b)|(?:\b(?:season|stagione)\s*\d{1,2}\b)|(?:\b(?:episode|episodio|ep)\s*\d{1,3}\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal sealed class NetworkServerViewItem
        {
            public string DeviceId { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string Model { get; init; } = string.Empty;
            public string Host { get; init; } = string.Empty;
            public string Location { get; init; } = string.Empty;
            public string ResourceBaseUrl { get; init; } = string.Empty;
            public string ContentDirectoryControlUrl { get; init; } = string.Empty;
            public string ContentDirectoryServiceType { get; init; } = "urn:schemas-upnp-org:service:ContentDirectory:1";
            public string Protocol { get; init; } = "DLNA / UPnP";
            public bool Available { get; init; } = true;
            public DateTime SeenAt { get; init; } = DateTime.Now;
        }

        internal sealed class NetworkItemMetadata
        {
            public string Title { get; init; } = string.Empty;
            public int? Year { get; init; }
            public string? SeriesTitle { get; init; }
            public int? SeasonNumber { get; init; }
            public int? EpisodeNumber { get; init; }
            public string? AlbumTitle { get; init; }
            public string? ArtistName { get; init; }
            public string? AlbumArtist { get; init; }
            public int? TrackNumber { get; init; }
        }
}

/// <summary>Esito di una lettura DLNA: errori o interruzioni vogliono dire catalogo incompleto.</summary>
internal sealed class DlnaBrowseReport
{
    public int Failures;
    public bool Partial;
    public bool Incomplete => Failures > 0 || Partial;
}
