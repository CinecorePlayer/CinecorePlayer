#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Elenco dei server nella pagina Rete: quali mostrare, in che ordine, quali tenere.
    // - Si vedono solo i server che hanno un catalogo da sfogliare (Jellyfin, Plex, altri server
    //   DLNA): televisori, router e amplificatori rispondono alla ricerca ma non hanno nulla da
    //   aprire, e riempivano l'elenco.
    // - In alto i preferiti, poi Jellyfin e Plex, poi gli altri server DLNA.
    // - Un server e' riconosciuto dalla sua identita', non dall'indirizzo: se cambia IP la voce
    //   si aggiorna da sola. Le voci che non rispondono piu' da una settimana spariscono, tranne
    //   i preferiti, il server collegato e i server Jellyfin a cui si e' fatto l'accesso.
    internal sealed partial class CinematicMediaLibraryPage
    {
        private enum NetworkServerKind { Jellyfin, Plex, Dlna, Device }

        private static readonly TimeSpan NetworkServerKeepUnseen = TimeSpan.FromDays(7);
        private readonly HashSet<string> _networkFavoriteKeys = new(StringComparer.OrdinalIgnoreCase);

        private static NetworkServerKind KindOf(NetworkServerViewItem server)
        {
            if (IsJellyfinServer(server)) return NetworkServerKind.Jellyfin;
            if ((server.Name + " " + server.Model).Contains("Plex", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(server.ContentDirectoryControlUrl))
                return NetworkServerKind.Plex;
            return string.IsNullOrWhiteSpace(server.ContentDirectoryControlUrl) ? NetworkServerKind.Device : NetworkServerKind.Dlna;
        }

        private bool IsFavoriteNetworkServer(NetworkServerViewItem server) => _networkFavoriteKeys.Contains(NetworkServerKey(server));

        /// <summary>0 preferiti, 1 Jellyfin e Plex, 2 altri server DLNA.</summary>
        private int NetworkSectionOf(NetworkServerViewItem server) =>
            IsFavoriteNetworkServer(server) ? 0 : KindOf(server) is NetworkServerKind.Jellyfin or NetworkServerKind.Plex ? 1 : 2;

        private bool KeepNetworkServer(NetworkServerViewItem server)
        {
            string key = NetworkServerKey(server);
            if (string.IsNullOrWhiteSpace(key)) return false;
            bool favorite = _networkFavoriteKeys.Contains(key);
            NetworkServerKind kind = KindOf(server);
            // Senza catalogo non c'e' nulla da aprire.
            if (kind == NetworkServerKind.Device) return false;
            if (server.Available || favorite) return true;
            if (string.Equals(key, _networkConnectedServerKey, StringComparison.OrdinalIgnoreCase)) return true;
            if (kind == NetworkServerKind.Jellyfin && JellyfinClient.FindAccount(JellyfinServerId(server)) != null) return true;
            return DateTime.Now - server.SeenAt < NetworkServerKeepUnseen;
        }

        private List<NetworkServerViewItem> OrderNetworkServers(IEnumerable<NetworkServerViewItem> servers) => servers
            .Where(KeepNetworkServer)
            .OrderBy(NetworkSectionOf)
            .ThenBy(server => (int)KindOf(server))
            .ThenByDescending(server => server.Available)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        /// <summary>Lo stesso server ritrovato con un'altra identita' (reinstallato, o un'altra scheda di rete):
        /// stesso nome e stesso tipo, il vecchio non risponde e il nuovo si'.</summary>
        private static bool LooksLikeReplacedServer(NetworkServerViewItem stale, NetworkServerViewItem found)
        {
            if (stale.Available || KindOf(stale) != KindOf(found) || KindOf(found) == NetworkServerKind.Device) return false;
            string a = NormalizeNetworkIdentityPart(stale.Name), b = NormalizeNetworkIdentityPart(found.Name);
            return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private void ToggleNetworkServerFavorite(int index)
        {
            if (index < 0 || index >= _networkServers.Count) return;
            NetworkServerViewItem server = _networkServers[index];
            string key = NetworkServerKey(server);
            if (!_networkFavoriteKeys.Remove(key)) _networkFavoriteKeys.Add(key);
            string selectedKey = _networkSelectionIndex >= 0 && _networkSelectionIndex < _networkServers.Count ? NetworkServerKey(_networkServers[_networkSelectionIndex]) : key;
            var ordered = OrderNetworkServers(_networkServers);
            _networkServers.Clear();
            _networkServers.AddRange(ordered);
            int selected = _networkServers.FindIndex(s => string.Equals(NetworkServerKey(s), selectedKey, StringComparison.OrdinalIgnoreCase));
            _networkSelectionIndex = Math.Max(0, selected);
            SaveNetworkServers();
            Invalidate();
        }

        private static Color NetworkKindColor(NetworkServerKind kind) => kind switch
        {
            NetworkServerKind.Jellyfin => Color.FromArgb(170, 92, 195),
            NetworkServerKind.Plex => Color.FromArgb(229, 160, 13),
            _ => Color.FromArgb(120, 150, 180)
        };

        private void DrawNetworkServerList(Graphics g, Rectangle r, float scale)
        {
            _networkListViewport = r;
            int rowH = S(scale, 92), gap = S(scale, 10), headerH = S(scale, 30);
            _networkSelectionIndex = Math.Max(0, Math.Min(_networkSelectionIndex, Math.Max(0, _networkServers.Count - 1)));

            if (_networkServers.Count == 0)
            {
                Rectangle empty = new Rectangle(r.Left, r.Top, r.Width, Math.Min(r.Height, rowH * 2));
                using var font = LibraryFont("Segoe UI Semibold", Math.Max(10f, 11.5f * scale));
                string text = _networkDiscoveryInProgress ? L("Ricerca in corso...", "Searching...") : L("Nessun server trovato", "No server found");
                TextRenderer.DrawText(g, text, font, new Rectangle(empty.Left + S(scale, 4), empty.Top, empty.Width - S(scale, 24), empty.Height),
                    Color.FromArgb(206, 218, 229), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            // Voci dell'elenco: i server nell'ordine in cui sono tenuti, con un titolo quando cambia la sezione.
            string query = _searchBox.Text.Trim();
            var entries = new List<(string? Header, int Index)>();
            int lastSection = -1;
            for (int i = 0; i < _networkServers.Count; i++)
            {
                var candidate = _networkServers[i];
                if (query.Length > 0 && !(candidate.Name + " " + candidate.Model + " " + candidate.Host).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                int section = NetworkSectionOf(candidate);
                if (section != lastSection)
                {
                    entries.Add((section switch
                    {
                        0 => L("PREFERITI", "FAVOURITES"),
                        1 => L("JELLYFIN E PLEX", "JELLYFIN AND PLEX"),
                        _ => L("ALTRI SERVER DLNA", "OTHER DLNA SERVERS")
                    }, -1));
                    lastSection = section;
                }
                entries.Add((null, i));
            }

            int HeightOf((string? Header, int Index) entry) => entry.Header != null ? headerH : rowH + gap;
            // Scorrimento a voci: l'ultima posizione e' la prima da cui si vede la fine dell'elenco.
            int fromEnd = 0, room = r.Height + gap;
            for (int i = entries.Count - 1; i >= 0 && room >= HeightOf(entries[i]); i--) { room -= HeightOf(entries[i]); fromEnd++; }
            _networkListScrollMax = Math.Max(0, entries.Count - Math.Max(1, fromEnd));
            _networkListScroll = Math.Clamp(_networkListScroll, 0, _networkListScrollMax);

            using var headerFont = LibraryFont("Segoe UI Semibold", Math.Max(7.4f, 8.2f * scale));
            using var nameFont = LibraryFont("Segoe UI Semibold", Math.Max(10.2f, 12.2f * scale));
            using var metaFont = LibraryFont("Segoe UI", Math.Max(8.2f, 9.6f * scale));
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            int y = r.Top;
            for (int e = _networkListScroll; e < entries.Count; e++)
            {
                var entry = entries[e];
                if (y + HeightOf(entry) - (entry.Header != null ? 0 : gap) > r.Bottom) break;
                if (entry.Header != null)
                {
                    TextRenderer.DrawText(g, entry.Header, headerFont, new Rectangle(r.Left + S(scale, 4), y, r.Width - S(scale, 8), headerH - S(scale, 6)), Muted, Line);
                    y += headerH;
                    continue;
                }

                int i = entry.Index;
                var server = _networkServers[i];
                NetworkServerKind kind = KindOf(server);
                Rectangle row = new Rectangle(r.Left, y, r.Width, rowH);
                bool selected = i == _networkSelectionIndex;
                bool hover = row.Contains(_lastMouse);
                if (selected || hover)
                {
                    using var fill = new SolidBrush(Color.FromArgb(selected ? 32 : 18, selected ? Accent : HUD.Theme.Text));
                    using var shape = Round(row, S(scale, 7));
                    g.FillPath(fill, shape);
                }

                Rectangle icon = new Rectangle(row.Left + S(scale, 18), row.Top + S(scale, 20), S(scale, 52), S(scale, 52));
                DrawNetworkDeviceGlyph(g, icon, kind, selected, scale);
                int textX = icon.Right + S(scale, 20);
                int textW = row.Width - (textX - row.Left) - S(scale, 64);
                TextRenderer.DrawText(g, server.Name, nameFont, new Rectangle(textX, row.Top + S(scale, 18), textW, S(scale, 25)), TextMain, Line);
                // Jellyfin e Plex si riconoscono dal nome del servizio nel loro colore; gli altri dal modello.
                string service = kind switch
                {
                    NetworkServerKind.Jellyfin => FirstNonEmpty(server.Model, "Jellyfin"),
                    NetworkServerKind.Plex => "Plex Media Server",
                    _ => FirstNonEmpty(server.Model, server.Protocol)
                };
                TextRenderer.DrawText(g, service, metaFont, new Rectangle(textX, row.Top + S(scale, 43), textW, S(scale, 20)),
                    kind is NetworkServerKind.Jellyfin or NetworkServerKind.Plex ? NetworkKindColor(kind) : Color.FromArgb(176, 190, 204), Line);
                TextRenderer.DrawText(g, $"{(server.Available ? L("Disponibile", "Available") : L("Non disponibile", "Unavailable"))}   |   {server.Host}", metaFont,
                    new Rectangle(textX, row.Top + S(scale, 64), textW, S(scale, 19)), Color.FromArgb(156, 172, 188), Line);
                _hits.Add(new HitZone { Bounds = row, Kind = HitKind.NetworkSelect, Index = i });

                // Stella: il server resta in cima e non viene mai tolto dall'elenco.
                bool favorite = IsFavoriteNetworkServer(server);
                int starSize = S(scale, 20);
                Rectangle star = new Rectangle(row.Right - S(scale, 22) - starSize, row.Top + (row.Height - starSize) / 2, starSize, starSize);
                Rectangle starHit = Rectangle.Inflate(star, S(scale, 10), S(scale, 10));
                bool starHover = starHit.Contains(_lastMouse);
                if (favorite) DrawStar(g, star, Accent);
                else DrawIcon(g, star, "star", starHover ? TextMain : Color.FromArgb(hover || selected ? 150 : 70, HUD.Theme.SubtleText));
                _hits.Add(new HitZone { Bounds = starHit, Kind = HitKind.NetworkFavorite, Index = i });
                y += rowH + gap;
            }
        }

        private void DrawNetworkDeviceGlyph(Graphics g, Rectangle r, NetworkServerKind kind, bool selected, float scale)
        {
            Color accent = NetworkKindColor(kind);
            using (var path = Round(r, S(scale, 9)))
            using (var fill = new SolidBrush(selected ? Color.FromArgb(46, accent) : Color.FromArgb(26, accent)))
                g.FillPath(fill, path);
            DrawIcon(g, new Rectangle(r.Left + r.Width / 4, r.Top + r.Height / 4, r.Width / 2, r.Height / 2), "computer", accent);
        }
    }
}
