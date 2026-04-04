using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using System;

#nullable enable

namespace CinecorePlayer2025
{
    internal sealed partial class MediaLibraryPage
    {
        // ------------ DLNA DISCOVERY / BROWSE UI ------------

        private sealed class DlnaDevice
        {
            public string FriendlyName = "DLNA";
            // Base URI "pulita" (scheme+host+port + "/") o URLBase se presente
            public Uri BaseUri = null!;
            public Uri ControlUrl = null!;
            public string? IconUrl;
        }

        private sealed class DlnaObject
        {
            public string Id = "0";
            public string Title = "";
            public bool IsContainer;
            public string? AlbumArt;
            public string? Resource; // primo <res> utile (http-get)
            public string? Mime;
            public string? ClassName;
        }

        private sealed class DlnaIndexedItem
        {
            public string Title = string.Empty;
            public string Resource = string.Empty;
            public string Category = "Video";
            public string? Mime;
            public string? AlbumArt;
            public string? ClassName;
            public string ContainerTrail = string.Empty;
            public bool IsRecent;
        }

        // cache thumbnail (teniamo in vita per tutta l'app per evitare download/decodifica ripetuti)
        private static readonly Dictionary<string, Image> _thumbCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _thumbLock = new();

        // Dopo la scelta del server, mostriamo i contenuti smistati nelle categorie (Film/Video/Foto/Musica...).
        // Cache per ridurre le Browse ripetute: viene resettata quando si cambia server DLNA.
        private readonly Dictionary<string, string> _dlnaCatStartId = new(StringComparer.OrdinalIgnoreCase);

        // Categoria DLNA attiva (serve per filtrare gli item e mantenere il filtro quando si entra nelle sottocartelle).
        private string? _dlnaActiveCategory = null;
        private readonly Dictionary<string, List<DlnaIndexedItem>> _dlnaIndexedItems = new(StringComparer.OrdinalIgnoreCase);
        private string _dlnaIndexedServerKey = string.Empty;
        private bool _dlnaShowServerPicker = true;

        private void ResetDlnaSelectionState(bool showPicker)
        {
            try { _dlnaCts?.Cancel(); } catch { }
            try { _dlnaStack.Clear(); } catch { }
            try { _dlnaCatStartId.Clear(); } catch { }
            _dlnaActiveCategory = null;
            _dlnaIndexedItems.Clear();
            _dlnaIndexedServerKey = string.Empty;
            _dlnaShowServerPicker = showPicker;
            if (showPicker)
                _dlnaSel = null;
        }

        private string GetDlnaServerCacheKey(DlnaDevice? device)
        {
            try { return device?.ControlUrl?.ToString() ?? string.Empty; } catch { return string.Empty; }
        }

        private static bool IsDlnaSupportedCategory(string category)
        {
            return string.Equals(category, "Film", StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, "Video", StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, "Foto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, "Musica", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<Image?> GetThumbAsync(string url, CancellationToken ct)
        {
            lock (_thumbLock)
                if (_thumbCache.TryGetValue(url, out var cached))
                    return cached;

            try
            {
                using var resp = await _http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();

                await using var s = await resp.Content.ReadAsStreamAsync(ct);

                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, ct);
                ms.Position = 0;

                using var raw = Image.FromStream(ms);

                // downscale fisso per tile (cache leggera)
                const int W = 56, H = 56;
                var bmp = new Bitmap(W, H);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(raw, new Rectangle(0, 0, W, H));
                }

                lock (_thumbLock)
                    _thumbCache[url] = bmp;

                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<List<DlnaDevice>> DiscoverDlnaAsync(CancellationToken ct)
        {
            var list = new List<DlnaDevice>();

            // forza IPv4 (su .NET recenti altrimenti a volte si incasina)
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

            string req =
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                "ST: urn:schemas-upnp-org:service:ContentDirectory:1\r\n\r\n";

            byte[] data = Encoding.ASCII.GetBytes(req);

            var start = Environment.TickCount;
            int lastSend = start - 3000;
            int sendCount = 0;

            while (!ct.IsCancellationRequested && Environment.TickCount - start < 9000)
            {
                int now = Environment.TickCount;

                // manda M-SEARCH subito e poi ogni ~3s
                if (sendCount == 0 || now - lastSend >= 3000)
                {
                    try
                    {
                        await udp.SendAsync(data, data.Length, ep);
                    }
                    catch
                    {
                        // best-effort
                    }
                    lastSend = now;
                    sendCount++;
                }

                try
                {
                    var res = await udp.ReceiveAsync().WaitAsync(
                        TimeSpan.FromMilliseconds(1000), ct);

                    string resp = Encoding.ASCII.GetString(res.Buffer);
                    var headers = ParseHttpHeaders(resp);

                    if (!headers.TryGetValue("LOCATION", out var loc))
                        continue;

                    try
                    {
                        var locUri = new Uri(loc);
                        var desc = await _http.GetStringAsync(locUri, ct);

                        // FIX: usa URLBase se presente, altrimenti authority + "/"
                        var (friendly, ctrl, baseUri, iconUrl) = ParseDeviceDescription(desc, locUri);
                        if (ctrl != null && friendly != null && baseUri != null)
                        {
                            list.Add(new DlnaDevice
                            {
                                FriendlyName = friendly,
                                BaseUri = baseUri,
                                ControlUrl = ctrl,
                                IconUrl = iconUrl
                            });
                        }
                    }
                    catch
                    {
                        // best-effort
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // timeout singolo ok, continuiamo finché non scade il while
                }
            }

            // dedup per ControlUrl
            var unique = new Dictionary<string, DlnaDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in list)
            {
                var key = d.ControlUrl.ToString();
                if (!unique.ContainsKey(key))
                    unique[key] = d;
            }

            return unique.Values.ToList();

            static Dictionary<string, string> ParseHttpHeaders(string raw)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var l in lines)
                {
                    int c = l.IndexOf(':');
                    if (c > 0)
                        dict[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim();
                }
                return dict;
            }

            static (string? friendly, Uri? ctrl, Uri? baseUri, string? iconUrl) ParseDeviceDescription(string xml, Uri loc)
            {
                try
                {
                    var x = XDocument.Parse(xml);

                    // URLBase (se presente) è quello “giusto” per risolvere albumArtURI e controlURL
                    var urlBaseStr = x.Root?
                        .Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "URLBase")?
                        .Value?.Trim();

                    Uri baseUri;
                    if (!string.IsNullOrWhiteSpace(urlBaseStr) &&
                        Uri.TryCreate(urlBaseStr, UriKind.Absolute, out var ub))
                    {
                        // assicuriamoci di avere trailing slash
                        baseUri = ub.AbsolutePath.EndsWith("/")
                            ? ub
                            : new Uri(ub.ToString().TrimEnd('/') + "/");
                    }
                    else
                    {
                        baseUri = new Uri(loc.GetLeftPart(UriPartial.Authority) + "/");
                    }

                    // prova con namespace ufficiale, ma con fallback sui LocalName
                    XNamespace ns = "urn:schemas-upnp-org:device-1-0";
                    var dev = x.Root?.Element(ns + "device")
                              ?? x.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
                    if (dev == null) return (null, null, null, null);

                    string? name = dev.Elements().FirstOrDefault(e => e.Name.LocalName == "friendlyName")?.Value;

                    string? iconUrl = null;
                    try
                    {
                        var iconCandidates = (dev.Elements().FirstOrDefault(e => e.Name.LocalName == "iconList")?.Elements()
                                .Where(e => e.Name.LocalName == "icon")
                            ?? Enumerable.Empty<XElement>())
                            .Select(icon => new
                            {
                                Url = icon.Elements().FirstOrDefault(e => e.Name.LocalName == "url")?.Value?.Trim(),
                                Mime = icon.Elements().FirstOrDefault(e => e.Name.LocalName == "mimetype")?.Value?.Trim() ?? string.Empty,
                                Width = int.TryParse(icon.Elements().FirstOrDefault(e => e.Name.LocalName == "width")?.Value, out var w) ? w : 0,
                                Height = int.TryParse(icon.Elements().FirstOrDefault(e => e.Name.LocalName == "height")?.Value, out var h) ? h : 0,
                            })
                            .Where(icon => !string.IsNullOrWhiteSpace(icon.Url))
                            .OrderByDescending(icon =>
                                (icon.Mime.Contains("png", StringComparison.OrdinalIgnoreCase) ? 2000000 : 0) +
                                (icon.Mime.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || icon.Mime.Contains("jpg", StringComparison.OrdinalIgnoreCase) ? 1000000 : 0) +
                                (icon.Width * icon.Height))
                            .ToList();

                        var bestIcon = iconCandidates.FirstOrDefault();
                        if (bestIcon != null)
                        {
                            iconUrl = Uri.TryCreate(bestIcon.Url, UriKind.Absolute, out var absIcon)
                                ? absIcon.ToString()
                                : new Uri(baseUri, bestIcon.Url).ToString();
                        }
                    }
                    catch { }

                    var servicesParent = dev.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceList");
                    var services = servicesParent?.Elements().Where(e => e.Name.LocalName == "service")
                                  ?? Enumerable.Empty<XElement>();

                    foreach (var s in services)
                    {
                        var st = s.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value ?? "";
                        if (!st.Contains("ContentDirectory", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var rel = s.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(rel))
                            continue;

                        Uri ctrl = Uri.TryCreate(rel, UriKind.Absolute, out var abs)
                            ? abs
                            : new Uri(baseUri, rel);

                        return (name, ctrl, baseUri, iconUrl);
                    }
                }
                catch { }

                return (null, null, null, null);
            }
        }

        private static async Task<List<DlnaDevice>> DiscoverDlnaWithRetry(CancellationToken ct)
        {
            List<DlnaDevice> devs;
            try { devs = await DiscoverDlnaAsync(ct); }
            catch { devs = new List<DlnaDevice>(); }

            if (ct.IsCancellationRequested || devs.Count > 0)
                return devs;

            // se non ha trovato nulla, aspetta un attimo e riprova
            try { await Task.Delay(1000, ct); } catch { }

            try { devs = await DiscoverDlnaAsync(ct); }
            catch { devs = new List<DlnaDevice>(); }

            return devs;
        }

        private static async Task<(List<DlnaObject> containers, List<DlnaObject> items)> BrowseAsync(DlnaDevice dev, string objectId, CancellationToken ct)
        {
            // SOAP Browse: BrowseDirectChildren
            string soap =
            $@"<?xml version=""1.0"" encoding=""utf-8""?>
            <s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
              <s:Body>
                <u:Browse xmlns:u=""urn:schemas-upnp-org:service:ContentDirectory:1"">
                  <ObjectID>{SecurityElement.Escape(objectId)}</ObjectID>
                  <BrowseFlag>BrowseDirectChildren</BrowseFlag>
                  <Filter>*</Filter>
                  <StartingIndex>0</StartingIndex>
                  <RequestedCount>200</RequestedCount>
                  <SortCriteria></SortCriteria>
                </u:Browse>
              </s:Body>
            </s:Envelope>";

            using var msg = new HttpRequestMessage(HttpMethod.Post, dev.ControlUrl);
            msg.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");
            msg.Content = new StringContent(soap, Encoding.UTF8, "text/xml");

            using var httpResp = await _http.SendAsync(msg, ct);
            httpResp.EnsureSuccessStatusCode();
            string resp = await httpResp.Content.ReadAsStringAsync(ct);

            var (cont, items) = ParseDidl(resp, dev.BaseUri);
            return (cont, items);

            static (List<DlnaObject> containers, List<DlnaObject> items) ParseDidl(string soapResp, Uri baseUri)
            {
                var containers = new List<DlnaObject>();
                var items = new List<DlnaObject>();

                try
                {
                    var x = XDocument.Parse(soapResp);
                    var resultStr = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Result")?.Value;
                    if (string.IsNullOrWhiteSpace(resultStr)) return (containers, items);

                    var didl = XDocument.Parse(resultStr);

                    XNamespace didlns = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
                    XNamespace upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
                    XNamespace dc = "http://purl.org/dc/elements/1.1/";

                    static string? PickAlbumArt(XElement el)
                    {
                        // Plex/alcuni server possono avere più albumArtURI con profileID
                        var arts = el.Descendants()
                            .Where(e => e.Name.LocalName == "albumArtURI")
                            .Select(e => new
                            {
                                Url = (e.Value ?? "").Trim(),
                                Profile = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "profileID")?.Value ?? ""
                            })
                            .Where(a => !string.IsNullOrWhiteSpace(a.Url))
                            .ToList();

                        if (arts.Count == 0) return null;

                        // preferisci thumbnail se disponibile
                        var best = arts.FirstOrDefault(a =>
                            a.Profile.Contains("JPEG_TN", StringComparison.OrdinalIgnoreCase) ||
                            a.Profile.Contains("PNG_TN", StringComparison.OrdinalIgnoreCase) ||
                            a.Profile.Contains("TN", StringComparison.OrdinalIgnoreCase));

                        return (best ?? arts[0]).Url;
                    }

                    foreach (var c in didl.Descendants(didlns + "container"))
                    {
                        var id = c.Attribute("id")?.Value ?? "0";
                        var title = c.Element(dc + "title")?.Value ?? "(cartella)";

                        var art = PickAlbumArt(c);
                        if (!string.IsNullOrWhiteSpace(art))
                        {
                            try { art = new Uri(baseUri, art).ToString(); } catch { }
                        }

                        containers.Add(new DlnaObject
                        {
                            Id = id,
                            Title = title,
                            IsContainer = true,
                            AlbumArt = art
                        });
                    }

                    foreach (var it in didl.Descendants(didlns + "item"))
                    {
                        var id = it.Attribute("id")?.Value ?? "";
                        var title = it.Element(dc + "title")?.Value ?? "(sorgente)";

                        var art = PickAlbumArt(it);
                        if (!string.IsNullOrWhiteSpace(art))
                        {
                            try { art = new Uri(baseUri, art).ToString(); } catch { }
                        }

                        string? className = it.Element(upnp + "class")?.Value;
                        if (string.IsNullOrWhiteSpace(className))
                            className = it.Elements().FirstOrDefault(e => e.Name.LocalName == "class")?.Value;

                        string? res = null, mime = null;
                        foreach (var r in it.Elements(didlns + "res"))
                        {
                            var proto = (r.Attribute("protocolInfo")?.Value ?? "").ToLowerInvariant();
                            var url = r.Value?.Trim();
                            if (string.IsNullOrWhiteSpace(url)) continue;

                            try { url = new Uri(baseUri, url).ToString(); } catch { }

                            // preferiamo risorse http-get
                            if (proto.Contains("http-get"))
                            {
                                res = url;
                                mime = proto.Split(':').ElementAtOrDefault(2);
                                break;
                            }
                        }

                        items.Add(new DlnaObject
                        {
                            Id = id,
                            Title = title,
                            IsContainer = false,
                            AlbumArt = art,
                            Resource = res,
                            Mime = mime,
                            ClassName = className
                        });
                    }
                }
                catch { }

                return (containers, items);
            }
        }

        private sealed class RemoteTile : Control
        {
            private readonly string _title;
            private readonly string? _sub;
            private readonly Action _onClick;
            private bool _hover;
            private Image? _thumb;

            public RemoteTile(string title, string? subtitle, Action onClick, int w = 360, int h = 76)
            {
                _title = title;
                _sub = subtitle;
                _onClick = onClick;

                Size = new Size(w, h);
                Margin = new Padding(10, 6, 10, 6);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw, true);

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; Invalidate(); };
                Click += (_, __) => _onClick();
            }

            public void SetThumb(Image? img)
            {
                _thumb = img;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                var rc = new Rectangle(0, 0, Width - 1, Height - 1);

                // tile background
                using var bg = new SolidBrush(_hover ? ControlPaint.Light(Theme.Card) : Theme.Card);
                using var bd = new Pen(_hover ? ControlPaint.Light(Theme.Border) : Theme.Border);

                g.FillRectangle(bg, rc);
                g.DrawRectangle(bd, rc);

                // thumbnail box
                var thumbRc = new Rectangle(10, 10, 56, 56);
                using (var ph = new SolidBrush(Color.FromArgb(25, 255, 255, 255)))
                    g.FillRectangle(ph, thumbRc);

                if (_thumb != null)
                {
                    g.DrawImage(_thumb, thumbRc);
                }
                else
                {
                    using var phBorder = new Pen(Color.FromArgb(68, 170, 182, 206));
                    using var rackBrush = new SolidBrush(Color.FromArgb(26, 255, 255, 255));
                    using var rackPen = new Pen(Color.FromArgb(110, 180, 192, 214), 1.6f);
                    using var ledBrush = new SolidBrush(Color.FromArgb(190, 88, 210, 255));

                    g.DrawRectangle(phBorder, thumbRc);

                    var rack1 = new Rectangle(thumbRc.Left + 8, thumbRc.Top + 11, thumbRc.Width - 16, 14);
                    var rack2 = new Rectangle(thumbRc.Left + 8, thumbRc.Top + 31, thumbRc.Width - 16, 14);
                    g.FillRectangle(rackBrush, rack1);
                    g.FillRectangle(rackBrush, rack2);
                    g.DrawRectangle(rackPen, rack1);
                    g.DrawRectangle(rackPen, rack2);

                    g.FillEllipse(ledBrush, rack1.Right - 13, rack1.Top + 4, 4, 4);
                    g.FillEllipse(ledBrush, rack1.Right - 7, rack1.Top + 4, 4, 4);
                    g.FillEllipse(ledBrush, rack2.Right - 13, rack2.Top + 4, 4, 4);
                    g.FillEllipse(ledBrush, rack2.Right - 7, rack2.Top + 4, 4, 4);

                    using var wavePen = new Pen(Color.FromArgb(132, 96, 214, 255), 1.6f);
                    g.DrawArc(wavePen, thumbRc.Left + 14, thumbRc.Top + 17, 16, 16, 220, 100);
                    g.DrawArc(wavePen, thumbRc.Left + 11, thumbRc.Top + 14, 22, 22, 220, 100);
                }

                using var t1 = new Font("Segoe UI Semibold", 10.5f);
                using var t2 = new Font("Segoe UI", 9f);

                var textLeft = thumbRc.Right + 10;
                var rcTitle = new Rectangle(textLeft, 12, Width - textLeft - 12, 22);
                var rcSub = new Rectangle(textLeft, rcTitle.Bottom, Width - textLeft - 12, Height - rcTitle.Bottom - 10);

                TextRenderer.DrawText(g, _title, t1, rcTitle, Color.White, TextFormatFlags.EndEllipsis);
                if (!string.IsNullOrWhiteSpace(_sub))
                    TextRenderer.DrawText(g, _sub, t2, rcSub, Theme.SubtleText, TextFormatFlags.EndEllipsis);
            }
        }


        private enum DlnaCategoryKind { Other, Video, Audio, Image }

        private static DlnaCategoryKind DlnaKindFromCategory(string category)
        {
            if (string.Equals(category, "Foto", StringComparison.OrdinalIgnoreCase)) return DlnaCategoryKind.Image;
            if (string.Equals(category, "Musica", StringComparison.OrdinalIgnoreCase)) return DlnaCategoryKind.Audio;
            if (string.Equals(category, "Film", StringComparison.OrdinalIgnoreCase)) return DlnaCategoryKind.Video;
            if (string.Equals(category, "Video", StringComparison.OrdinalIgnoreCase)) return DlnaCategoryKind.Video;
            return DlnaCategoryKind.Other;
        }

        private static bool ContainsAny(string hay, params string[] needles)
        {
            foreach (var n in needles)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (hay.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static int ScoreFolderTitle(string title, string category)
        {
            if (string.IsNullOrWhiteSpace(title)) return 0;
            var t = title.Trim();

            // score base per tipo
            var kind = DlnaKindFromCategory(category);
            int score = 0;

            if (kind == DlnaCategoryKind.Video)
            {
                if (ContainsAny(t, "video", "videos", "movies", "movie", "film", "films", "cinema", "tv", "series", "shows", "show")) score += 4;
                if (string.Equals(category, "Film", StringComparison.OrdinalIgnoreCase) && ContainsAny(t, "movies", "movie", "film", "films")) score += 3;
                if (ContainsAny(t, "music", "audio", "foto", "photo", "photos", "image", "images")) score -= 2;
            }
            else if (kind == DlnaCategoryKind.Audio)
            {
                if (ContainsAny(t, "music", "audio", "musica", "songs", "brani", "album", "artists", "artist")) score += 4;
                if (ContainsAny(t, "video", "movie", "movies", "film", "photo", "photos", "image", "images")) score -= 2;
            }
            else if (kind == DlnaCategoryKind.Image)
            {
                if (ContainsAny(t, "photo", "photos", "foto", "immagini", "image", "images", "pictures", "pic")) score += 4;
                if (ContainsAny(t, "video", "movie", "movies", "film", "music", "audio")) score -= 2;
            }

            // bonus per container tipici plex
            if (ContainsAny(t, "plex")) score += 1;

            return score;
        }

        private async Task<string> ResolveDlnaCategoryStartContainerIdAsync(string category, CancellationToken ct)
        {
            if (_dlnaSel == null) return "0";

            // categorie non multimediali: non forziamo path specifici
            var kind = DlnaKindFromCategory(category);
            if (kind == DlnaCategoryKind.Other)
                return "0";

            if (_dlnaCatStartId.TryGetValue(category, out var cached) && !string.IsNullOrWhiteSpace(cached))
                return cached;

            // 1) browse root
            List<DlnaObject> rootFolders;
            try
            {
                (rootFolders, _) = await BrowseAsync(_dlnaSel, "0", ct);
            }
            catch
            {
                rootFolders = new();
            }

            string bestId = "0";
            int bestScore = 0;

            foreach (var f in rootFolders.Where(x => x.IsContainer))
            {
                int s = ScoreFolderTitle(f.Title, category);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestId = f.Id;
                }
            }

            // 2) se abbiamo trovato un container generico (es. "Video"), proviamo a scendere di un livello
            // cercando un titolo più specifico (es. "Movies").
            if (bestId != "0" && bestScore > 0)
            {
                try
                {
                    (var subFolders, _) = await BrowseAsync(_dlnaSel, bestId, ct);
                    string subBestId = bestId;
                    int subBestScore = bestScore;
                    foreach (var sf in subFolders.Where(x => x.IsContainer))
                    {
                        int s2 = ScoreFolderTitle(sf.Title, category);
                        if (s2 > subBestScore)
                        {
                            subBestScore = s2;
                            subBestId = sf.Id;
                        }
                    }
                    bestId = subBestId;
                }
                catch { }
            }

            // fallback: root
            if (string.IsNullOrWhiteSpace(bestId)) bestId = "0";

            _dlnaCatStartId[category] = bestId;
            return bestId;
        }

        private static string? TryGetUrlExtension(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                    return Path.GetExtension(u.AbsolutePath);
                return Path.GetExtension(url);
            }
            catch
            {
                return null;
            }
        }

        private bool DlnaItemMatchesActiveCategory(DlnaObject it)
        {
            if (it == null) return false;
            if (_dlnaActiveCategory == null) return true;

            var kind = DlnaKindFromCategory(_dlnaActiveCategory);
            if (kind == DlnaCategoryKind.Other) return true;

            var mime = it.Mime ?? "";
            if (kind == DlnaCategoryKind.Video && mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return true;
            if (kind == DlnaCategoryKind.Audio && mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return true;
            if (kind == DlnaCategoryKind.Image && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;

            // fallback per mime mancante: inferiamo da estensione
            var ext = (TryGetUrlExtension(it.Resource) ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(ext)) return false;

            var cat = CategoryFromExt(ext);
            if (kind == DlnaCategoryKind.Video)
                return string.Equals(cat, "Film", StringComparison.OrdinalIgnoreCase) || string.Equals(cat, "Video", StringComparison.OrdinalIgnoreCase);
            if (kind == DlnaCategoryKind.Audio)
                return string.Equals(cat, "Musica", StringComparison.OrdinalIgnoreCase);
            if (kind == DlnaCategoryKind.Image)
                return string.Equals(cat, "Foto", StringComparison.OrdinalIgnoreCase);

            return false;
        }

        private static bool LooksLikeRecentFolderName(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ContainsAny(text,
                "recent",
                "recently",
                "recentemente",
                "aggiunti di recente",
                "new",
                "latest",
                "on deck",
                "continue watching",
                "continuare",
                "added");
        }

        private string? ClassifyDlnaItemCategory(DlnaObject? item, string containerTrail)
        {
            if (item == null)
                return null;

            string mime = item.Mime ?? string.Empty;
            string className = item.ClassName ?? string.Empty;
            string resource = item.Resource ?? string.Empty;
            string ext = (TryGetUrlExtension(resource) ?? string.Empty).ToLowerInvariant();
            string titleTrail = $"{containerTrail} {item.Title} {resource} {className}";

            if (!string.IsNullOrWhiteSpace(mime))
            {
                if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                    return "Musica";
                if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return "Foto";
                if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    return ClassifyDlnaVideoCategory(item, containerTrail, ext);
            }

            if (!string.IsNullOrWhiteSpace(className))
            {
                if (className.Contains("audio", StringComparison.OrdinalIgnoreCase) || className.Contains("musicTrack", StringComparison.OrdinalIgnoreCase))
                    return "Musica";
                if (className.Contains("image", StringComparison.OrdinalIgnoreCase) || className.Contains("photo", StringComparison.OrdinalIgnoreCase))
                    return "Foto";
                if (className.Contains("video", StringComparison.OrdinalIgnoreCase) || className.Contains("movie", StringComparison.OrdinalIgnoreCase) || className.Contains("episode", StringComparison.OrdinalIgnoreCase))
                    return ClassifyDlnaVideoCategory(item, containerTrail, ext);
            }

            if (!string.IsNullOrWhiteSpace(ext))
            {
                string extCategory;
                try { extCategory = CategoryFromExt(ext); }
                catch { extCategory = string.Empty; }

                if (string.Equals(extCategory, "Musica", StringComparison.OrdinalIgnoreCase))
                    return "Musica";
                if (string.Equals(extCategory, "Foto", StringComparison.OrdinalIgnoreCase))
                    return "Foto";
                if (string.Equals(extCategory, "Film", StringComparison.OrdinalIgnoreCase) || string.Equals(extCategory, "Video", StringComparison.OrdinalIgnoreCase))
                    return ClassifyDlnaVideoCategory(item, containerTrail, ext);
            }

            if (LooksLikeTvEpisodePath(titleTrail))
                return "Film";

            return null;
        }

        private string ClassifyDlnaVideoCategory(DlnaObject item, string containerTrail, string ext)
        {
            string sample = $"{containerTrail} {item.Title} {item.Resource} {item.ClassName}";

            if (LooksLikeTvEpisodePath(sample))
                return "Film";

            if (ContainsAny(sample,
                "movie",
                "movies",
                "film",
                "films",
                "cinema",
                "show",
                "shows",
                "series",
                "serie",
                "season",
                "stagione",
                "episode",
                "episodio",
                "tv"))
            {
                return "Film";
            }

            if (ContainsAny(sample,
                "music video",
                "musicvideo",
                "musicvideoclip",
                "clip",
                "clips",
                "trailer",
                "trailers",
                "camera uploads",
                "home video",
                "short",
                "shorts"))
            {
                return "Video";
            }

            if (!string.IsNullOrWhiteSpace(item.ClassName) && item.ClassName.Contains("movie", StringComparison.OrdinalIgnoreCase))
                return "Film";

            if (!string.IsNullOrWhiteSpace(ext))
            {
                try
                {
                    string extCategory = CategoryFromExt(ext);
                    if (string.Equals(extCategory, "Film", StringComparison.OrdinalIgnoreCase))
                        return "Film";
                }
                catch { }
            }

            return "Video";
        }

        private static string AppendDlnaTrail(string parentTrail, string title)
        {
            title = (title ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title))
                return parentTrail ?? string.Empty;
            if (string.IsNullOrWhiteSpace(parentTrail))
                return title;
            return parentTrail + " / " + title;
        }

        private static string NormalizeDlnaTitleComparison(string? value)
        {
            string normalized = WebUtility.HtmlDecode(value ?? string.Empty);
            normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized.ToLowerInvariant();
        }

        private static IEnumerable<string> EnumerateDlnaTrailSegments(string containerTrail)
        {
            if (string.IsNullOrWhiteSpace(containerTrail))
                yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = containerTrail
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => WebUtility.HtmlDecode(part ?? string.Empty).Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Reverse();

            foreach (var part in parts)
            {
                if (seen.Add(part))
                    yield return part;
            }
        }

        private static string GetDlnaResourceDisplayName(string resource)
        {
            if (string.IsNullOrWhiteSpace(resource))
                return string.Empty;

            try
            {
                if (Uri.TryCreate(resource, UriKind.Absolute, out var uri))
                {
                    string localName = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath) ?? string.Empty);
                    string withoutExt = Path.GetFileNameWithoutExtension(localName) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(withoutExt))
                        return withoutExt.Trim();
                    if (!string.IsNullOrWhiteSpace(localName))
                        return localName.Trim();
                }
            }
            catch { }

            try
            {
                string fileName = Path.GetFileNameWithoutExtension(resource) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName.Trim();
            }
            catch { }

            return resource.Trim();
        }

        private static bool LooksLikeGenericDlnaDisplayTitle(string? title)
        {
            string normalized = NormalizeDlnaTitleComparison(title);
            if (string.IsNullOrWhiteSpace(normalized))
                return true;

            return normalized == "video"
                || normalized == "videos"
                || normalized == "film"
                || normalized == "films"
                || normalized == "movie"
                || normalized == "movies"
                || normalized == "serie"
                || normalized == "series"
                || normalized == "tv"
                || normalized == "musica"
                || normalized == "music"
                || normalized == "foto"
                || normalized == "photo"
                || normalized == "photos"
                || normalized == "immagini"
                || normalized == "images"
                || normalized == "cartella"
                || normalized == "folder"
                || normalized == "file"
                || normalized == "files"
                || normalized == "item"
                || normalized == "items"
                || normalized == "audio"
                || normalized == "track"
                || normalized == "tracks"
                || normalized == "episode"
                || normalized == "episodes"
                || normalized == "episodio"
                || normalized == "episodi";
        }

        private static string SanitizeDlnaIndexedTitle(DlnaObject item, string containerTrail)
        {
            string fallback = GetDlnaResourceDisplayName(item?.Resource ?? string.Empty);
            string title = WebUtility.HtmlDecode(item?.Title ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title))
                return fallback;

            if (title.Contains('/') || title.Contains('\\'))
            {
                var pathBits = title
                    .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(bit => bit.Trim())
                    .Where(bit => !string.IsNullOrWhiteSpace(bit))
                    .ToList();
                if (pathBits.Count > 0)
                    title = pathBits[pathBits.Count - 1];
            }

            foreach (var segment in EnumerateDlnaTrailSegments(containerTrail))
            {
                foreach (var separator in new[] { " - ", " – ", " — ", " | ", " • ", " > ", ": " })
                {
                    string prefix = segment + separator;
                    if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        title = title.Substring(prefix.Length).Trim();
                        break;
                    }
                }
            }

            string normalizedTitle = NormalizeDlnaTitleComparison(title);
            string normalizedFallback = NormalizeDlnaTitleComparison(fallback);

            if (!string.IsNullOrWhiteSpace(normalizedFallback))
            {
                foreach (var separator in new[] { " - ", " – ", " — ", " | ", " • ", " > ", ": " })
                {
                    if (title.EndsWith(separator + fallback, StringComparison.OrdinalIgnoreCase))
                    {
                        title = fallback;
                        normalizedTitle = normalizedFallback;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title) || LooksLikeGenericDlnaDisplayTitle(title))
                return fallback;

            foreach (var segment in EnumerateDlnaTrailSegments(containerTrail))
            {
                if (string.Equals(normalizedTitle, NormalizeDlnaTitleComparison(segment), StringComparison.Ordinal))
                    return fallback;
            }

            return title;
        }

        private static string BuildDlnaIndexedDisplayTitle(DlnaIndexedItem item)
        {
            if (item == null)
                return string.Empty;

            string title = WebUtility.HtmlDecode(item.Title ?? string.Empty).Trim();
            string resourceTitle = GetDlnaResourceDisplayName(item.Resource ?? string.Empty);

            if (string.IsNullOrWhiteSpace(title) || LooksLikeGenericDlnaDisplayTitle(title))
            {
                if (!string.IsNullOrWhiteSpace(resourceTitle) && !LooksLikeGenericDlnaDisplayTitle(resourceTitle))
                    return resourceTitle;

                foreach (var segment in EnumerateDlnaTrailSegments(item.ContainerTrail))
                {
                    if (!LooksLikeGenericDlnaDisplayTitle(segment))
                        return segment.Trim();
                }

                return resourceTitle;
            }

            return title;
        }

        private async Task EnsureDlnaIndexForSelectedServerAsync(CancellationToken ct)
        {
            if (_dlnaSel == null)
                return;

            string key = GetDlnaServerCacheKey(_dlnaSel);
            if (!string.IsNullOrWhiteSpace(key) &&
                string.Equals(_dlnaIndexedServerKey, key, StringComparison.OrdinalIgnoreCase) &&
                _dlnaIndexedItems.Count > 0)
            {
                return;
            }

            var indexed = new Dictionary<string, List<DlnaIndexedItem>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Film"] = new List<DlnaIndexedItem>(),
                ["Video"] = new List<DlnaIndexedItem>(),
                ["Foto"] = new List<DlnaIndexedItem>(),
                ["Musica"] = new List<DlnaIndexedItem>()
            };

            var seenContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Id, string Trail, bool RecentHint, int Depth)>();
            queue.Enqueue(("0", string.Empty, false, 0));

            const int maxContainers = 1800;
            const int maxDepth = 8;

            while (queue.Count > 0 && seenContainers.Count < maxContainers)
            {
                ct.ThrowIfCancellationRequested();

                var node = queue.Dequeue();
                if (string.IsNullOrWhiteSpace(node.Id))
                    continue;
                if (!seenContainers.Add(node.Id))
                    continue;

                List<DlnaObject> folders;
                List<DlnaObject> items;
                try
                {
                    (folders, items) = await BrowseAsync(_dlnaSel, node.Id, ct);
                }
                catch
                {
                    continue;
                }

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();

                    string? category = ClassifyDlnaItemCategory(item, node.Trail);
                    if (string.IsNullOrWhiteSpace(category))
                        continue;

                    string resource = item.Resource?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(resource))
                        continue;
                    if (!seenResources.Add(resource))
                        continue;

                    if (!indexed.TryGetValue(category, out var bucket))
                    {
                        bucket = new List<DlnaIndexedItem>();
                        indexed[category] = bucket;
                    }

                    bucket.Add(new DlnaIndexedItem
                    {
                        Title = SanitizeDlnaIndexedTitle(item, node.Trail),
                        Resource = resource,
                        Category = category,
                        Mime = item.Mime,
                        AlbumArt = item.AlbumArt,
                        ClassName = item.ClassName,
                        ContainerTrail = node.Trail,
                        IsRecent = node.RecentHint || LooksLikeRecentFolderName(node.Trail)
                    });
                }

                if (node.Depth >= maxDepth)
                    continue;

                foreach (var folder in folders)
                {
                    ct.ThrowIfCancellationRequested();
                    string nextTrail = AppendDlnaTrail(node.Trail, folder.Title);
                    bool nextRecent = node.RecentHint || LooksLikeRecentFolderName(folder.Title) || LooksLikeRecentFolderName(node.Trail);
                    queue.Enqueue((folder.Id, nextTrail, nextRecent, node.Depth + 1));
                }
            }

            foreach (var pair in indexed)
            {
                pair.Value.Sort((a, b) =>
                {
                    int recentCmp = b.IsRecent.CompareTo(a.IsRecent);
                    if (recentCmp != 0)
                        return recentCmp;
                    return StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title);
                });
            }

            _dlnaIndexedItems.Clear();
            foreach (var pair in indexed)
                _dlnaIndexedItems[pair.Key] = pair.Value;

            _dlnaIndexedServerKey = key;
        }

        private List<DlnaIndexedItem> GetDlnaIndexedItemsForCategory(string category)
        {
            if (!_dlnaIndexedItems.TryGetValue(category, out var items) || items == null)
                return new List<DlnaIndexedItem>();

            var filtered = items.ToList();
            string query = (_search?.Text ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(query))
            {
                var tokens = query
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (tokens.Count > 0)
                {
                    filtered = filtered
                        .Where(item =>
                        {
                            string hay = ($"{BuildDlnaIndexedDisplayTitle(item)} {item.ContainerTrail} {item.Resource}").ToLowerInvariant();
                            return tokens.All(token => hay.Contains(token, StringComparison.OrdinalIgnoreCase));
                        })
                        .ToList();
                }
            }

            return filtered;
        }

        private static Bitmap ScaleDlnaArtworkToCover(Image source, int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            var bmp = new Bitmap(width, height);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (var bg = new SolidBrush(Color.FromArgb(18, 18, 18)))
                g.FillRectangle(bg, 0, 0, width, height);

            double scale = Math.Min((double)width / Math.Max(1, source.Width), (double)height / Math.Max(1, source.Height));
            int drawW = Math.Max(1, (int)Math.Round(source.Width * scale));
            int drawH = Math.Max(1, (int)Math.Round(source.Height * scale));
            int x = (width - drawW) / 2;
            int y = (height - drawH) / 2;

            g.DrawImage(source, new Rectangle(x, y, drawW, drawH));
            return bmp;
        }

        private async Task<Bitmap?> DownloadDlnaArtworkAsync(string? url, int width, int height, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            try
            {
                using var resp = await _http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                ms.Position = 0;
                using var raw = Image.FromStream(ms);
                return ScaleDlnaArtworkToCover(raw, width, height);
            }
            catch
            {
                return null;
            }
        }

        private void QueueDlnaThumbLoadForFileCard(FileCard card, DlnaIndexedItem item, int width, int height, CancellationToken ct)
        {
            if (card == null || item == null)
                return;

            _ = Task.Run(async () =>
            {
                Bitmap? bmp = null;
                try
                {
                    bmp = await DownloadDlnaArtworkAsync(item.AlbumArt, width, height, ct);
                }
                catch { }

                if (bmp == null)
                    return;

                if (ct.IsCancellationRequested || card.IsDisposed)
                {
                    try { bmp.Dispose(); } catch { }
                    return;
                }

                try
                {
                    card.BeginInvoke(new Action(() =>
                    {
                        if (card.IsDisposed || ct.IsCancellationRequested)
                        {
                            try { bmp.Dispose(); } catch { }
                            return;
                        }

                        card.SetImage(bmp);
                    }));
                }
                catch
                {
                    try { bmp.Dispose(); } catch { }
                }
            }, ct);
        }

        private void ShowDlnaServerPicker()
        {
            _grid.Controls.Clear();
            _grid.UpdateThemedScrollbar();

            _dlnaCts?.Cancel();
            _dlnaCts = new CancellationTokenSource();
            var ctDlna = _dlnaCts.Token;

            ShowMask("Ricerca dispositivi DLNA…");
            Task.Run(async () =>
            {
                List<DlnaDevice> devs;
                try { devs = await DiscoverDlnaWithRetry(ctDlna); }
                catch { devs = new List<DlnaDevice>(); }

                if (IsDisposed || ctDlna.IsCancellationRequested) return;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || ctDlna.IsCancellationRequested) return;
                    if (!string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase)) return;
                    HideMask();
                    RenderDlnaDeviceList(devs);
                }));
            }, ctDlna);
        }

        private void AddDlnaCategoryHeader(string category, int cardWidth)
        {
            int dividerWidth = Math.Max(cardWidth, _grid.ClientSize.Width - _grid.Padding.Left - _grid.Padding.Right - 20);
            var divider = new LibrarySectionDivider($"{category} • Server: {_dlnaSel?.FriendlyName}", category)
            {
                Width = dividerWidth,
                LeftMargin = 12
            };
            _grid.Controls.Add(divider);
            _grid.SetFlowBreak(divider, true);
        }

        private (List<DlnaIndexedItem> MovieItems, List<TvSeasonGroup> SeriesItems) BuildDlnaFilmSections(IEnumerable<DlnaIndexedItem> items)
        {
            var movieItems = new List<DlnaIndexedItem>();
            var groupMap = new Dictionary<string, TvSeasonGroup>(StringComparer.OrdinalIgnoreCase);
            var seriesGroups = new List<TvSeasonGroup>();

            foreach (var item in items ?? Enumerable.Empty<DlnaIndexedItem>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Resource))
                    continue;

                string displayTitle = BuildDlnaIndexedDisplayTitle(item);

                MovieMetadataService.MediaTitleInfo info;
                try
                {
                    info = MovieMetadataService.ExtractMediaTitleInfoFromPath(displayTitle);
                    if (!info.IsTvEpisode)
                    {
                        var byResource = MovieMetadataService.ExtractMediaTitleInfoFromPath(item.Resource);
                        if (byResource.IsTvEpisode)
                            info = byResource;
                    }
                    if (!info.IsTvEpisode && !string.IsNullOrWhiteSpace(item.ContainerTrail))
                    {
                        var byTrail = MovieMetadataService.ExtractMediaTitleInfoFromPath((item.ContainerTrail + " " + displayTitle).Trim());
                        if (byTrail.IsTvEpisode)
                            info = byTrail;
                    }
                }
                catch
                {
                    info = new MovieMetadataService.MediaTitleInfo { NormalizedTitle = displayTitle };
                }

                if (!info.IsTvEpisode)
                {
                    movieItems.Add(item);
                    continue;
                }

                string seriesTitle = ExtractSeriesTitleFromBestKnownDisplay(displayTitle, info);
                if (string.IsNullOrWhiteSpace(seriesTitle))
                    seriesTitle = !string.IsNullOrWhiteSpace(info.SeriesTitle) ? info.SeriesTitle!.Trim() : displayTitle;

                string key = (seriesTitle + "|" + (info.SeasonNumber?.ToString() ?? "speciali")).Trim();
                if (!groupMap.TryGetValue(key, out var group))
                {
                    group = new TvSeasonGroup
                    {
                        SeriesTitle = seriesTitle,
                        SeasonNumber = info.SeasonNumber,
                        RepresentativeEpisodeNumber = info.EpisodeNumber,
                        RepresentativePath = item.Resource,
                        DisplayName = BuildSeasonGroupDisplayName(seriesTitle, info.SeasonNumber)
                    };
                    groupMap[key] = group;
                    seriesGroups.Add(group);
                }
                else if ((info.EpisodeNumber ?? int.MaxValue) < (group.RepresentativeEpisodeNumber ?? int.MaxValue))
                {
                    group.RepresentativeEpisodeNumber = info.EpisodeNumber;
                    group.RepresentativePath = item.Resource;
                }

                group.Episodes.Add(new TvEpisodeOption
                {
                    SourcePath = item.Resource,
                    SourceName = BuildDlnaIndexedDisplayTitle(item),
                    EpisodeNumber = info.EpisodeNumber,
                    DisplayText = BuildEpisodeChoiceDisplay(info, BuildDlnaIndexedDisplayTitle(item), displayTitle)
                });
            }

            foreach (var group in seriesGroups)
            {
                group.Episodes = group.Episodes
                    .OrderBy(ep => ep.EpisodeNumber ?? int.MaxValue)
                    .ThenBy(ep => ep.DisplayText, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            seriesGroups = seriesGroups
                .OrderBy(group => group.SeriesTitle, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(group => group.SeasonNumber ?? int.MaxValue)
                .ToList();

            return (movieItems, seriesGroups);
        }

        private void QueueDlnaThumbLoadForSeasonCard(SeasonSelectorCard card, string? albumArtUrl, int width, int height, CancellationToken ct)
        {
            if (card == null)
                return;

            _ = Task.Run(async () =>
            {
                Bitmap? bmp = null;
                try { bmp = await DownloadDlnaArtworkAsync(albumArtUrl, width, height, ct); } catch { }

                if (bmp == null)
                    return;

                if (ct.IsCancellationRequested || card.IsDisposed)
                {
                    try { bmp.Dispose(); } catch { }
                    return;
                }

                ApplyBitmapToCard(card, bmp);
            }, ct);
        }

        private void RenderDlnaUnsupportedCategoryUi(string category)
        {
            _grid.Controls.Clear();

            var layout = GetGridCardLayout();
            AddDlnaCategoryHeader(category, layout.CardWidth);
            _grid.Controls.Add(new InfoRow("Nessun contenuto disponibile in questa vista del server selezionato."));
            _grid.Visible = true;
            _grid.UpdateThemedScrollbar();
        }

        private void RenderDlnaIndexedCategoryUi(string category)
        {
            _grid.Controls.Clear();
            var items = GetDlnaIndexedItemsForCategory(category);
            var layout = GetGridCardLayout();
            var token = _dlnaCts?.Token ?? CancellationToken.None;

            AddDlnaCategoryHeader(category, layout.CardWidth);

            if (items.Count == 0)
            {
                _grid.Controls.Add(new InfoRow("Nessun contenuto trovato per questa categoria sul server selezionato."));
                _grid.Visible = true;
                _grid.UpdateThemedScrollbar();
                return;
            }

            if (string.Equals(category, "Film", StringComparison.OrdinalIgnoreCase))
            {
                var filmSections = BuildDlnaFilmSections(items);

                void AddSectionDivider(string title, string bucket)
                {
                    int dividerWidth = Math.Max(layout.CardWidth, _grid.ClientSize.Width - _grid.Padding.Left - _grid.Padding.Right - 20);
                    var divider = new LibrarySectionDivider(title, bucket)
                    {
                        Width = dividerWidth,
                        LeftMargin = 12
                    };
                    _grid.Controls.Add(divider);
                    _grid.SetFlowBreak(divider, true);
                }

                void AddMovieCards(IEnumerable<DlnaIndexedItem> movieItems)
                {
                    foreach (var item in movieItems)
                    {
                        string resource = item.Resource;
                        if (string.IsNullOrWhiteSpace(resource))
                            continue;

                        var card = new FileCard(
                            resource,
                            showFavorite: false,
                            favInit: false,
                            onFavToggle: null,
                            clickOpen: () => SafeOpen(resource),
                            cardWidth: layout.CardWidth,
                            cardHeight: layout.CardHeight,
                            imgHeight: layout.ImgHeight);

                        string displayTitle = BuildDlnaIndexedDisplayTitle(item);
                        if (!string.IsNullOrWhiteSpace(displayTitle))
                            card.SetDisplayName(displayTitle);

                        card.SetInitialPlaceholder(GetCategoryPlaceholder("Film", 520));
                        card.SetItemContextMenu(EnsureLibraryItemMenu(), LibraryItemContext.FromFile(resource));
                        _grid.Controls.Add(card);
                        QueueDlnaThumbLoadForFileCard(card, item, layout.CardWidth, layout.ImgHeight, token);
                    }
                }

                void AddSeriesCards(IEnumerable<TvSeasonGroup> seriesItems)
                {
                    foreach (var group in seriesItems)
                    {
                        var seasonCard = new SeasonSelectorCard(
                            group,
                            showEpisodePicker: (seasonGroup, displayTitle) => ShowSeasonEpisodeOverlay(seasonGroup, displayTitle),
                            cardWidth: layout.CardWidth,
                            cardHeight: layout.CardHeight,
                            imgHeight: layout.ImgHeight);

                        seasonCard.SetInitialPlaceholder(GetCategoryPlaceholder("Film", 520));
                        seasonCard.SetItemContextMenu(EnsureLibraryItemMenu(), LibraryItemContext.FromSeasonGroup(group));
                        _grid.Controls.Add(seasonCard);

                        string? albumArt = items
                            .Where(item => string.Equals(item.Resource, group.RepresentativePath, StringComparison.OrdinalIgnoreCase)
                                || group.Episodes.Any(ep => string.Equals(ep.FilePath, item.Resource, StringComparison.OrdinalIgnoreCase)))
                            .Select(item => item.AlbumArt)
                            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
                        QueueDlnaThumbLoadForSeasonCard(seasonCard, albumArt, layout.CardWidth, layout.ImgHeight, token);
                    }
                }

                if (_seriesSectionFirst)
                {
                    if (filmSections.SeriesItems.Count > 0)
                    {
                        AddSectionDivider("Serie TV", "Serie");
                        AddSeriesCards(filmSections.SeriesItems);
                    }

                    if (filmSections.MovieItems.Count > 0)
                    {
                        AddSectionDivider("Film", "Film");
                        AddMovieCards(filmSections.MovieItems);
                    }
                }
                else
                {
                    if (filmSections.MovieItems.Count > 0)
                    {
                        AddSectionDivider("Film", "Film");
                        AddMovieCards(filmSections.MovieItems);
                    }

                    if (filmSections.SeriesItems.Count > 0)
                    {
                        AddSectionDivider("Serie TV", "Serie");
                        AddSeriesCards(filmSections.SeriesItems);
                    }
                }

                if (filmSections.MovieItems.Count == 0 && filmSections.SeriesItems.Count == 0)
                    _grid.Controls.Add(new InfoRow("Nessun contenuto trovato per questa categoria sul server selezionato."));

                _grid.Visible = true;
                _grid.UpdateThemedScrollbar();
                return;
            }

            foreach (var item in items)
            {
                string resource = item.Resource;
                if (string.IsNullOrWhiteSpace(resource))
                    continue;

                var card = new FileCard(
                    resource,
                    showFavorite: false,
                    favInit: false,
                    onFavToggle: null,
                    clickOpen: () => SafeOpen(resource),
                    cardWidth: layout.CardWidth,
                    cardHeight: layout.CardHeight,
                    imgHeight: layout.ImgHeight);

                string displayTitle = BuildDlnaIndexedDisplayTitle(item);
                if (!string.IsNullOrWhiteSpace(displayTitle))
                    card.SetDisplayName(displayTitle);

                string placeholderCategory = string.Equals(category, "Musica", StringComparison.OrdinalIgnoreCase)
                    ? "Musica"
                    : (string.Equals(category, "Foto", StringComparison.OrdinalIgnoreCase)
                        ? "Foto"
                        : (string.Equals(category, "Film", StringComparison.OrdinalIgnoreCase) ? "Film" : "Video"));

                card.SetInitialPlaceholder(GetCategoryPlaceholder(placeholderCategory, 520));
                card.SetItemContextMenu(EnsureLibraryItemMenu(), LibraryItemContext.FromFile(resource));
                _grid.Controls.Add(card);
                QueueDlnaThumbLoadForFileCard(card, item, layout.CardWidth, layout.ImgHeight, token);
            }

            _grid.Visible = true;
            _grid.UpdateThemedScrollbar();
        }

        private void LoadDlnaSelectedServerCategory()
        {
            if (_dlnaSel == null)
            {
                ShowDlnaServerPicker();
                return;
            }

            string category = _selCat;
            _dlnaActiveCategory = category;

            if (!IsDlnaSupportedCategory(category))
            {
                try { HideMask(); } catch { }
                RenderDlnaUnsupportedCategoryUi(category);
                return;
            }

            _dlnaCts?.Cancel();
            _dlnaCts = new CancellationTokenSource();
            var ct = _dlnaCts.Token;

            bool cacheReady = string.Equals(_dlnaIndexedServerKey, GetDlnaServerCacheKey(_dlnaSel), StringComparison.OrdinalIgnoreCase)
                && _dlnaIndexedItems.Count > 0;

            if (!cacheReady)
                ShowMask($"Indicizzazione {_dlnaSel.FriendlyName}…");
            else
                ShowMask($"Aggiornamento {category}…", showSpinner: false);

            Task.Run(async () =>
            {
                try { await EnsureDlnaIndexForSelectedServerAsync(ct); }
                catch { }

                if (IsDisposed || ct.IsCancellationRequested)
                    return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || ct.IsCancellationRequested)
                        return;
                    if (!string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase))
                        return;
                    if (_dlnaSel == null)
                        return;

                    HideMask();
                    RenderDlnaIndexedCategoryUi(category);
                }));
            }, ct);
        }

        private void RefreshDlnaSourceContent()
        {
            try { HideInlineRootsCallToAction(); } catch { }
            _secRecenti.Visible = false;
            _carouselHost.Visible = false;
            _secAll.Visible = false;

            if (_dlnaSel == null || _dlnaShowServerPicker)
            {
                _dlnaShowServerPicker = true;
                ShowDlnaServerPicker();
                return;
            }

            _dlnaShowServerPicker = false;
            LoadDlnaSelectedServerCategory();
        }

        private void DlnaShowCategory(string category)
        {
            if (_dlnaSel == null)
                return;

            _dlnaActiveCategory = category;
            _dlnaShowServerPicker = false;
            LoadDlnaSelectedServerCategory();
        }

        private void RenderDlnaDeviceList(List<DlnaDevice> devs)
        {
            _grid.Controls.Clear();

            if (devs.Count == 0)
            {
                _grid.Controls.Add(new InfoRow("Nessun server DLNA trovato nella rete domestica."));
                _grid.Visible = true;
                _grid.UpdateThemedScrollbar();
                return;
            }

            int dividerWidth = Math.Max(320, _grid.ClientSize.Width - _grid.Padding.Left - _grid.Padding.Right - 20);
            var divider = new LibrarySectionDivider("Dispositivi DLNA", "Video")
            {
                Width = dividerWidth,
                LeftMargin = 12
            };
            _grid.Controls.Add(divider);
            _grid.SetFlowBreak(divider, true);

            var token = _dlnaCts?.Token ?? CancellationToken.None;

            foreach (var d in devs.OrderBy(v => v.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
            {
                var tile = new RemoteTile(d.FriendlyName, d.BaseUri.Host, () =>
                {
                    _dlnaSel = d;
                    _dlnaShowServerPicker = false;
                    _dlnaStack.Clear();
                    try { _dlnaCatStartId.Clear(); } catch { }
                    _dlnaActiveCategory = _selCat;
                    _dlnaIndexedItems.Clear();
                    _dlnaIndexedServerKey = string.Empty;

                    // Dopo la scelta del server DLNA, la sorgente rete domestica mostra i contenuti
                    // della categoria corrente smistati come una libreria separata.
                    try { BeginInvoke(new Action(() => RefreshDlnaSourceContent())); } catch { RefreshDlnaSourceContent(); }
                }, w: 420, h: 76);

                _grid.Controls.Add(tile);

                if (!string.IsNullOrWhiteSpace(d.IconUrl))
                {
                    _ = Task.Run(async () =>
                    {
                        Image? img = null;
                        try { img = await GetThumbAsync(d.IconUrl!, token); } catch { }
                        if (img == null || token.IsCancellationRequested || IsDisposed)
                            return;

                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (!token.IsCancellationRequested && !tile.IsDisposed)
                                    tile.SetThumb(img);
                            }));
                        }
                        catch { }
                    }, token);
                }
            }

            _grid.Visible = true;
            _grid.UpdateThemedScrollbar();
        }

        private void DlnaEnterContainer(string id)
        {
            if (_dlnaSel == null) return;

            _dlnaStack.Push(id); // current on stack per back
            _dlnaCts?.Cancel();
            _dlnaCts = new CancellationTokenSource();
            var ct = _dlnaCts.Token;

            ShowMask("Caricamento contenuti…");
            Task.Run(async () =>
            {
                List<DlnaObject> folders;
                List<DlnaObject> items;

                try
                {
                    (folders, items) = await BrowseAsync(_dlnaSel, id, ct);
                }
                catch
                {
                    folders = new();
                    items = new();
                }

                if (IsDisposed || ct.IsCancellationRequested) return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || ct.IsCancellationRequested) return;
                    if (!string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase)) return;
                    HideMask();
                    RenderDlnaContainerUi(folders, items);
                }));
            }, ct);
        }

        private void DlnaBack()
        {
            if (_dlnaSel == null) return;

            if (_dlnaStack.Count <= 1)
            {
                // torna alla lista dispositivi
                _dlnaSel = null;
                _dlnaStack.Clear();
                try { _dlnaCatStartId.Clear(); } catch { }
                _dlnaActiveCategory = null;

                _grid.Controls.Clear();
                _grid.UpdateThemedScrollbar();

                _dlnaCts?.Cancel();
                _dlnaCts = new CancellationTokenSource();
                var ctDlna = _dlnaCts.Token;

                ShowMask("Ricerca dispositivi DLNA…");
                Task.Run(async () =>
                {
                    List<DlnaDevice> devs;
                    try { devs = await DiscoverDlnaWithRetry(ctDlna); }
                    catch { devs = new List<DlnaDevice>(); }

                    if (IsDisposed || ctDlna.IsCancellationRequested) return;

                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || ctDlna.IsCancellationRequested) return;
                        if (!string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase)) return;
                        HideMask();
                        RenderDlnaDeviceList(devs);
                    }));
                }, ctDlna);

                return;
            }

            // pop current e vai al parent
            _dlnaStack.Pop();
            var parent = _dlnaStack.Peek();
            _dlnaStack.Pop(); // re-push inside Enter
            DlnaEnterContainer(parent);
        }

        private void RenderDlnaContainerUi(List<DlnaObject> folders, List<DlnaObject> items)
        {
            _grid.Controls.Clear();

            // Filtra gli item in base alla categoria scelta (Film/Video/Foto/Musica).
            // Le cartelle restano visibili: potrebbero contenere elementi della categoria.
            try
            {
                if (_dlnaActiveCategory != null)
                    items = items.Where(DlnaItemMatchesActiveCategory).ToList();
            }
            catch { }


            var backLabel = (_dlnaStack.Count <= 1) ? "← Server DLNA" : "← Indietro";

            var back = new RemoteTile(backLabel, _dlnaSel?.FriendlyName, () => DlnaBack(), w: 240, h: 56);
            _grid.Controls.Add(back);

            if (!string.IsNullOrWhiteSpace(_dlnaActiveCategory))
                _grid.Controls.Add(new InfoRow($"Categoria: {_dlnaActiveCategory} • Server: {_dlnaSel?.FriendlyName}"));

            if (folders.Count == 0 && items.Count == 0)
            {
                _grid.Controls.Add(new InfoRow(string.IsNullOrWhiteSpace(_dlnaActiveCategory) ? "Cartella vuota." : "Nessun contenuto disponibile in questa categoria."));
                _grid.Visible = true;
                _grid.UpdateThemedScrollbar();
                return;
            }

            // helper: carica thumb async dentro una tile
            void KickThumb(RemoteTile tile, string? url)
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                var token = _dlnaCts?.Token ?? CancellationToken.None;

                _ = Task.Run(async () =>
                {
                    var img = await GetThumbAsync(url!, token);
                    if (img == null) return;

                    if (token.IsCancellationRequested || tile.IsDisposed) return;

                    try
                    {
                        tile.BeginInvoke(new Action(() =>
                        {
                            if (!tile.IsDisposed && !token.IsCancellationRequested)
                                tile.SetThumb(img);
                        }));
                    }
                    catch { }
                }, token);
            }

            if (folders.Count > 0)
                _grid.Controls.Add(new InfoRow("Cartelle:"));

            foreach (var f in folders)
            {
                var t = new RemoteTile(f.Title, "Cartella", () => DlnaEnterContainer(f.Id), w: 360, h: 76);
                _grid.Controls.Add(t);
                KickThumb(t, f.AlbumArt);
            }

            if (items.Count > 0)
                _grid.Controls.Add(new InfoRow("Elementi:"));

            foreach (var it in items)
            {
                string? res = it.Resource;
                var sub = string.IsNullOrWhiteSpace(it.Mime) ? "Sorgente" : it.Mime;

                var t = new RemoteTile(it.Title, sub, () =>
                {
                    if (!string.IsNullOrWhiteSpace(res))
                        SafeOpen(res!);
                }, w: 360, h: 76);

                _grid.Controls.Add(t);
                KickThumb(t, it.AlbumArt);
            }

            _grid.Visible = true;
            _grid.UpdateThemedScrollbar();
        }
    }
}