#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
using System.Windows.Forms;
using System.Xml.Linq;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        private Rectangle _networkListViewport;
        private int _networkListScroll, _networkListScrollMax;
        private void ShowNetworkPage()
        {
            ReleaseNativeSearchFocus();
            CancelYouTubeWork();
            CloseGroupPicker(invalidate: false);
            _view = PageView.Network;
            _source = "Network";
            ResetSmoothGridScroll();
            if (!string.IsNullOrEmpty(_searchBox.Text))
                _searchBox.Text = string.Empty;
            UpdateSearchPlaceholder();
            RefreshNetworkServers(force: _networkServers.Count == 0);
            Invalidate();
        }

        private void DrawNetworkPage(Graphics g, Rectangle content, float scale)
        {
            int titleY = content.Top + S(scale, 58);
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(21f, 25.5f * scale));
            using var subFont = LibraryFont("Segoe UI", Math.Max(9.5f, 11f * scale));
            TextRenderer.DrawText(g, L("Seleziona server DLNA", "Select DLNA server"), titleFont,
                new Rectangle(content.Left, titleY, content.Width, S(scale, 40)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string subtitle = _networkContentLoading
                ? L("Caricamento contenuti dal server...", "Loading content from server...")
                : !string.IsNullOrWhiteSpace(_networkContentStatus)
                    ? _networkContentStatus
                    : _networkDiscoveryInProgress
                ? L("Ricerca server nella rete domestica...", "Searching for servers on the home network...")
                : L("Scegli il server da cui riprodurre i contenuti.", "Choose the server to play content from.");
            TextRenderer.DrawText(g, subtitle, subFont,
                new Rectangle(content.Left, titleY + S(scale, 40), content.Width, S(scale, 28)), Color.FromArgb(184, 196, 207),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int bodyTop = titleY + S(scale, 96);
            int footerH = S(scale, 100);
            int gap = S(scale, 34);
            int bodyH = Math.Max(1, content.Bottom - bodyTop - footerH);
            Rectangle left = new Rectangle(content.Left, bodyTop, Math.Max(1, (content.Width - gap) / 2), bodyH);
            Rectangle right = new Rectangle(left.Right + gap, bodyTop, Math.Max(1, content.Right - left.Right - gap), bodyH);

            DrawNetworkServerList(g, left, scale);
            DrawNetworkServerDetail(g, right, scale);
            using (var divider = new Pen(Color.FromArgb(65, HUD.Theme.Border)))
                g.DrawLine(divider, left.Right + gap / 2, bodyTop, left.Right + gap / 2, bodyTop + bodyH);

            var footer = new Rectangle(content.Left, content.Bottom - S(scale, 70), content.Width, S(scale, 66));
            using (var shape = Round(footer, S(scale, 8)))
            using (var fill = new SolidBrush(Color.FromArgb(170, HUD.Theme.Card)))
            using (var edge = new Pen(Color.FromArgb(55, HUD.Theme.Border)))
            { g.FillPath(fill, shape); g.DrawPath(edge, shape); }
            int footerY = footer.Top + S(scale, 11);
            Rectangle refresh = new Rectangle(footer.Left + S(scale, 12), footerY, S(scale, 196), S(scale, 44));
            DrawNetworkRefreshButton(g, refresh, scale);
            Rectangle connect = new Rectangle(footer.Right - S(scale, 180), footerY, S(scale, 164), S(scale, 44));
            Rectangle remove = new Rectangle(connect.Left - S(scale, 152), footerY, S(scale, 140), S(scale, 44));
            if (remove.Left - refresh.Right > S(scale, 220))
            {
                using var updatedFont = LibraryFont("Segoe UI", 9.5f * scale);
                string seen = _networkLastRefresh == DateTime.MinValue ? "—" : _networkLastRefresh.ToString("HH:mm");
                TextRenderer.DrawText(g, L("Aggiornato alle ", "Updated at ") + seen, updatedFont,
                    new Rectangle(refresh.Right + S(scale, 20), footerY, remove.Left - refresh.Right - S(scale, 40), S(scale, 44)), Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            DrawNetworkRemoveButton(g, remove, scale);
            DrawNetworkConnectButton(g, connect, scale);
            if (!_networkContentLoading && _networkServers.Count > 0)
            {
                _hits.Add(new HitZone { Bounds = remove, Kind = HitKind.NetworkRemove });
                _hits.Add(new HitZone { Bounds = connect, Kind = HitKind.NetworkConnect });
            }
        }

        private void DrawNetworkAction(Graphics g, Rectangle bounds, string text, string icon, bool primary, bool enabled, float scale)
        {
            bool hot = enabled && bounds.Contains(_lastMouse);
            if (primary || hot)
            {
                using var shape = Round(bounds, S(scale, 6));
                using var fill = new SolidBrush(primary ? (enabled ? (hot ? HUD.Theme.AccentSoft : Accent) : HUD.Theme.Card) : Color.FromArgb(28, Accent));
                g.FillPath(fill, shape);
            }
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(8, 10.5f * scale));
            int size = S(scale, 18), gap = S(scale, 10);
            int group = Math.Min(bounds.Width - S(scale, 16), size + gap + TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding).Width);
            int x = bounds.Left + (bounds.Width - group) / 2;
            Color tint = enabled ? (primary ? Color.White : HUD.Theme.Text) : Muted;
            DrawIcon(g, new Rectangle(x, bounds.Top + (bounds.Height - size) / 2, size, size), icon, tint);
            TextRenderer.DrawText(g, text, font, new Rectangle(x + size + gap, bounds.Top, Math.Max(1, group - size - gap), bounds.Height), tint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private void DrawNetworkRemoveButton(Graphics g, Rectangle r, float scale)
            => DrawNetworkAction(g, r, L("Rimuovi", "Remove"), "trash", false, _networkServers.Count > 0 && !_networkContentLoading, scale);

        private void RemoveSelectedNetworkServer()
        {
            if (_networkServers.Count == 0)
                return;

            int index = Math.Clamp(_networkSelectionIndex, 0, _networkServers.Count - 1);
            NetworkServerViewItem server = _networkServers[index];
            string key = NetworkServerKey(server);
            _networkServers.RemoveAt(index);
            _networkSelectionIndex = Math.Clamp(index, 0, Math.Max(0, _networkServers.Count - 1));
            if (string.Equals(_networkConnectedServerKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _networkConnectedServerKey = string.Empty;
                _networkItems = new();
                _networkCatalogRevision++;
                _networkContentStatus = L("Server rimosso.", "Server removed.");
            }
            SaveNetworkServers();
            Invalidate();
        }

        private void DrawNetworkConnectButton(Graphics g, Rectangle r, float scale)
            => DrawNetworkAction(g, r, _networkContentLoading ? L("Caricamento…", "Loading…") : L("Connetti", "Connect"), "link", true,
                _networkServers.Count > 0 && !_networkContentLoading, scale);

        private void DrawNetworkServerList(Graphics g, Rectangle r, float scale)
        {
            _networkListViewport = r;
            int rowH = S(scale, 92);
            int gap = S(scale, 10);
            int y = r.Top;
            int count = Math.Max(1, _networkServers.Count);
            _networkSelectionIndex = Math.Max(0, Math.Min(_networkSelectionIndex, Math.Max(0, _networkServers.Count - 1)));

            if (_networkServers.Count == 0)
            {
                Rectangle empty = new Rectangle(r.Left, r.Top, r.Width, Math.Min(r.Height, rowH * 2));
                using var path = Round(empty, S(scale, 8));
                using var fill = new SolidBrush(Color.FromArgb(44, 5, 15, 24));
                using var border = new Pen(Color.FromArgb(36, 255, 255, 255));
                g.FillPath(fill, path);
                g.DrawPath(border, path);
                using var font = LibraryFont("Segoe UI Semibold", Math.Max(10f, 11.5f * scale));
                string text = _networkDiscoveryInProgress ? L("Ricerca in corso...", "Searching...") : L("Nessun server DLNA trovato", "No DLNA server found");
                TextRenderer.DrawText(g, text, font, new Rectangle(empty.Left + S(scale, 20), empty.Top, empty.Width - S(scale, 40), empty.Height),
                    Color.FromArgb(206, 218, 229), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            string query = _searchBox.Text.Trim();
            var visible = _networkServers.Select((server, index) => (server, index))
                .Where(item => string.IsNullOrEmpty(query) ||
                    (item.server.Name + " " + item.server.Model + " " + item.server.Host).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            int rows = Math.Max(1, (r.Height + gap) / (rowH + gap));
            _networkListScrollMax = Math.Max(0, visible.Count - rows);
            _networkListScroll = Math.Clamp(_networkListScroll, 0, _networkListScrollMax);
            for (int itemIndex = _networkListScroll; itemIndex < Math.Min(visible.Count, _networkListScroll + rows); itemIndex++, y += rowH + gap)
            {
                int i = visible[itemIndex].index;
                var server = _networkServers[i];
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
                DrawNetworkDeviceGlyph(g, icon, i, selected, scale);
                using var nameFont = LibraryFont("Segoe UI Semibold", Math.Max(10.2f, 12.2f * scale));
                using var metaFont = LibraryFont("Segoe UI", Math.Max(8.2f, 9.6f * scale));
                int textX = icon.Right + S(scale, 20);
                TextRenderer.DrawText(g, server.Name, nameFont, new Rectangle(textX, row.Top + S(scale, 18), row.Width - S(scale, 132), S(scale, 25)),
                    TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, FirstNonEmpty(server.Model, server.Protocol), metaFont, new Rectangle(textX, row.Top + S(scale, 43), row.Width - S(scale, 132), S(scale, 20)),
                    Color.FromArgb(176, 190, 204), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, $"{(server.Available ? L("Disponibile", "Available") : L("Non disponibile", "Unavailable"))}   |   {server.Host}", metaFont,
                    new Rectangle(textX, row.Top + S(scale, 64), row.Width - S(scale, 132), S(scale, 19)),
                    Color.FromArgb(156, 172, 188), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                _hits.Add(new HitZone { Bounds = row, Kind = HitKind.NetworkSelect, Index = i });
                DrawIcon(g, new Rectangle(row.Right - S(scale, 32), row.Top + (row.Height - S(scale, 20)) / 2, S(scale, 20), S(scale, 20)), "chevron-right", Muted);
            }
        }

        private void DrawNetworkServerDetail(Graphics g, Rectangle r, float scale)
        {
            int pad = S(scale, 34);
            var server = _networkServers.Count > 0 ? _networkServers[Math.Max(0, Math.Min(_networkSelectionIndex, _networkServers.Count - 1))] : null;
            Rectangle glyph = new Rectangle(r.Left + pad, r.Top + pad, S(scale, 72), S(scale, 72));
            DrawNetworkDeviceGlyph(g, glyph, _networkSelectionIndex, true, scale);

            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(19f, 24f * scale));
            using var metaFont = LibraryFont("Segoe UI", Math.Max(9.5f, 10.8f * scale));
            string name = server?.Name ?? L("Server DLNA", "DLNA server");
            string model = server?.Model ?? L("In attesa di dispositivi", "Waiting for devices");
            TextRenderer.DrawText(g, name, titleFont, new Rectangle(r.Left + pad, glyph.Bottom + S(scale, 22), r.Width - pad * 2, S(scale, 42)),
                TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, model, metaFont, new Rectangle(r.Left + pad, glyph.Bottom + S(scale, 62), r.Width - pad * 2, S(scale, 26)),
                Color.FromArgb(188, 200, 212), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int y = glyph.Bottom + S(scale, 122);
            DrawNetworkDetailRow(g, r.Left + pad, ref y, r.Width - pad * 2, "folder", L("Contenuti", "Content"), L("Film, Serie TV, Musica, Foto", "Movies, TV, Music, Photos"), scale);
            DrawNetworkDetailRow(g, r.Left + pad, ref y, r.Width - pad * 2, "network", L("Indirizzo IP", "IP address"), server?.Host ?? "-", scale);
            DrawNetworkDetailRow(g, r.Left + pad, ref y, r.Width - pad * 2, "link", L("Protocollo", "Protocol"), server?.Protocol ?? "DLNA / UPnP", scale);
            string seen = server == null ? "-" : server.SeenAt.ToString("HH:mm", CultureInfo.CurrentCulture);
            DrawNetworkDetailRow(g, r.Left + pad, ref y, r.Width - pad * 2, "time", L("Ultimo rilevamento", "Last seen"), seen, scale);
            if (_networkContentLoading || !string.IsNullOrWhiteSpace(_networkContentStatus))
            {
                string status = _networkContentLoading
                    ? L("Sto leggendo il catalogo DLNA. Le categorie si popolano appena arrivano i file.", "Reading the DLNA catalog. Categories will populate when files arrive.")
                    : _networkContentStatus;
                DrawNetworkDetailRow(g, r.Left + pad, ref y, r.Width - pad * 2, "info", L("Stato", "Status"), status, scale);
            }
        }

        private void DrawNetworkDetailRow(Graphics g, int x, ref int y, int width, string iconKey, string label, string value, float scale)
        {
            int h = S(scale, 62);
            Rectangle icon = new Rectangle(x, y + S(scale, 16), S(scale, 32), S(scale, 32));
            DrawIcon(g, icon, iconKey, Color.FromArgb(154, 190, 226));
            using var labelFont = LibraryFont("Segoe UI", Math.Max(8.2f, 9.4f * scale));
            using var valueFont = LibraryFont("Segoe UI Semibold", Math.Max(8.9f, 10f * scale));
            TextRenderer.DrawText(g, label, labelFont, new Rectangle(icon.Right + S(scale, 16), y + S(scale, 13), width - icon.Width - S(scale, 16), S(scale, 22)),
                Color.FromArgb(166, 180, 196), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, value, valueFont, new Rectangle(icon.Right + S(scale, 16), y + S(scale, 35), width - icon.Width - S(scale, 16), S(scale, 24)),
                TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += h;
        }

        private void DrawNetworkRefreshButton(Graphics g, Rectangle r, float scale)
        {
            DrawNetworkAction(g, r, _networkContentLoading ? L("Annulla", "Cancel") : L("Aggiorna elenco", "Refresh list"), "scan", false, !_networkDiscoveryInProgress, scale);
            if (!_networkDiscoveryInProgress) _hits.Add(new HitZone { Bounds = r, Kind = HitKind.NetworkRefresh });
        }

        private void DrawNetworkDeviceGlyph(Graphics g, Rectangle r, int index, bool selected, float scale)
        {
            Color accent = (index % 4) switch
            {
                1 => Color.FromArgb(164, 86, 255),
                2 => Color.FromArgb(255, 170, 38),
                3 => Color.FromArgb(0, 184, 255),
                _ => Color.FromArgb(0, 222, 116)
            };
            using (var path = Round(r, S(scale, 9)))
            using (var fill = new SolidBrush(selected ? Color.FromArgb(42, accent) : Color.FromArgb(22, accent)))
                g.FillPath(fill, path);
            DrawIcon(g, new Rectangle(r.Left + r.Width / 4, r.Top + r.Height / 4, r.Width / 2, r.Height / 2), "computer", accent);
        }

        private void RefreshNetworkServers(bool force)
        {
            if (_networkDiscoveryInProgress)
                return;
            if (!force && _networkServers.Count > 0 && DateTime.Now - _networkLastRefresh < TimeSpan.FromMinutes(2))
                return;

            _networkDiscoveryInProgress = true;
            Invalidate();

            Task.Run(async () =>
            {
                List<NetworkServerViewItem> found = new();
                try { found = await DiscoverDlnaServersAsync(TimeSpan.FromSeconds(4)); } catch { }
                try
                {
                    if (IsDisposed || !IsHandleCreated)
                        return;
                    BeginInvoke(new Action(() =>
                    {
                        MergeNetworkServers(found);
                        _networkDiscoveryInProgress = false;
                        _networkLastRefresh = DateTime.Now;
                        SaveNetworkServers();
                        Invalidate();
                    }));
                }
                catch { }
            });
        }

        private void LoadSavedNetworkServers()
        {
            try
            {
                var model = LoadJson<NetworkServersModel>(NetworkServersPath);
                if (model?.Servers == null || model.Servers.Count == 0)
                    return;

                List<NetworkServerViewItem> savedServers = model.Servers
                    .Where(s => s != null && !string.IsNullOrWhiteSpace(NetworkServerKey(s)))
                    .Select(s => WithNetworkAvailability(s, available: false))
                    .GroupBy(NetworkServerKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(s => s.SeenAt).First())
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _networkServers.Clear();
                _networkServers.AddRange(savedServers);

                // Migra le chiavi storiche basate su Location/IP verso l'identita'
                // stabile del dispositivo. Cosi' un DHCP lease diverso aggiorna lo
                // stesso server anziche' creare una nuova tessera salvata.
                string persistedConnected = model.ConnectedServerKey ?? string.Empty;
                NetworkServerViewItem? connected = savedServers.FirstOrDefault(s =>
                    string.Equals(NetworkServerLegacyKey(s), persistedConnected, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NetworkServerNameOnlyKey(s), persistedConnected, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NetworkServerKey(s), persistedConnected, StringComparison.OrdinalIgnoreCase));
                _networkConnectedServerKey = connected != null
                    ? NetworkServerKey(connected)
                    : string.Empty;

                // Pulisce subito il file storico: non serve attendere una nuova
                // scansione per eliminare i duplicati accumulati dopo un cambio IP.
                SaveNetworkServers();
            }
            catch { }
        }

        private void SaveNetworkServers()
        {
            try
            {
                var model = new NetworkServersModel
                {
                    ConnectedServerKey = _networkConnectedServerKey,
                    Servers = _networkServers
                        .Where(s => s != null && !string.IsNullOrWhiteSpace(NetworkServerKey(s)))
                        .GroupBy(NetworkServerKey, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList()
                };
                SaveJson(NetworkServersPath, model);
            }
            catch { }
        }

        private void MergeNetworkServers(IReadOnlyList<NetworkServerViewItem> found)
        {
            var merged = new Dictionary<string, NetworkServerViewItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var saved in _networkServers)
            {
                string key = NetworkServerKey(saved);
                if (!string.IsNullOrWhiteSpace(key))
                    merged[key] = WithNetworkAvailability(saved, available: false);
            }

            foreach (var server in found)
            {
                string key = NetworkServerKey(server);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    // Migrazione trasparente dei server salvati prima dell'uso UDN:
                    // sostituisci la tessera name/model con quella scoperta, completa
                    // di identità e nuovo endpoint, senza creare duplicati.
                    var aliases = merged
                        .Where(pair => NetworkServersDescribeSameDevice(pair.Value, server))
                        .Select(pair => pair.Key)
                        .Where(alias => !string.Equals(alias, key, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (string alias in aliases)
                    {
                        merged.Remove(alias);
                        if (string.Equals(_networkConnectedServerKey, alias, StringComparison.OrdinalIgnoreCase))
                            _networkConnectedServerKey = key;
                    }
                    merged[key] = WithNetworkAvailability(server, available: true);
                }
            }

            string selectedKey = _networkServers.Count > 0 && _networkSelectionIndex >= 0 && _networkSelectionIndex < _networkServers.Count
                ? NetworkServerKey(_networkServers[_networkSelectionIndex])
                : _networkConnectedServerKey;

            _networkServers.Clear();
            _networkServers.AddRange(merged.Values
                .OrderByDescending(s => s.Available)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase));

            int selected = _networkServers.FindIndex(s => string.Equals(NetworkServerKey(s), selectedKey, StringComparison.OrdinalIgnoreCase));
            if (selected < 0 && !string.IsNullOrWhiteSpace(_networkConnectedServerKey))
                selected = _networkServers.FindIndex(s => string.Equals(NetworkServerKey(s), _networkConnectedServerKey, StringComparison.OrdinalIgnoreCase));
            _networkSelectionIndex = selected >= 0
                ? selected
                : Math.Max(0, Math.Min(_networkSelectionIndex, Math.Max(0, _networkServers.Count - 1)));
        }

        private static NetworkServerViewItem WithNetworkAvailability(NetworkServerViewItem server, bool available)
        {
            return new NetworkServerViewItem
            {
                DeviceId = server.DeviceId,
                Name = server.Name,
                Model = server.Model,
                Host = server.Host,
                Location = server.Location,
                ResourceBaseUrl = server.ResourceBaseUrl,
                ContentDirectoryControlUrl = server.ContentDirectoryControlUrl,
                ContentDirectoryServiceType = FirstRawNonEmpty(server.ContentDirectoryServiceType, "urn:schemas-upnp-org:service:ContentDirectory:1"),
                Protocol = server.Protocol,
                Available = available,
                SeenAt = available ? DateTime.Now : server.SeenAt
            };
        }

        private static string NetworkServerKey(NetworkServerViewItem? server)
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

        private static string NetworkServerLegacyKey(NetworkServerViewItem? server)
        {
            if (server == null)
                return string.Empty;
            return FirstRawNonEmpty(
                server.Location,
                server.ContentDirectoryControlUrl,
                string.Join("|", new[] { server.Host, server.Name }.Where(v => !string.IsNullOrWhiteSpace(v))),
                server.Name);
        }

        private static string NetworkServerNameOnlyKey(NetworkServerViewItem? server)
        {
            string name = NormalizeNetworkIdentityPart(server?.Name);
            return name.Length == 0 ? string.Empty : "device:" + name;
        }

        private static bool NetworkServersDescribeSameDevice(NetworkServerViewItem? left, NetworkServerViewItem? right)
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

        private static string NormalizeNetworkIdentityPart(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return string.Join(" ", value.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private void ConnectSelectedNetworkServer(bool force = false)
        {
            if (_networkContentLoading)
                return;
            if (_networkServers.Count == 0)
            {
                _networkContentStatus = L("Nessun server selezionabile. Aggiorna elenco e riprova.", "No selectable server. Refresh the list and try again.");
                Invalidate();
                return;
            }

            _networkSelectionIndex = Math.Max(0, Math.Min(_networkSelectionIndex, _networkServers.Count - 1));
            var server = _networkServers[_networkSelectionIndex];
            string serverKey = NetworkServerKey(server);
            if (!force && _networkItems.Count > 0 &&
                !string.IsNullOrWhiteSpace(_networkConnectedServerKey) &&
                string.Equals(_networkConnectedServerKey, serverKey, StringComparison.OrdinalIgnoreCase))
            {
                _networkContentStatus = L($"Gia connesso a {server.Name}.", $"Already connected to {server.Name}.");
                _source = "Network";
                _view = PageView.Collection;
                _category = IsCinematicCategoryKey(_category) && !string.Equals(_category, "Favourites", StringComparison.OrdinalIgnoreCase) && !string.Equals(_category, "Playlists", StringComparison.OrdinalIgnoreCase)
                    ? _category
                    : BestNetworkStartCategory(_networkItems);
                RefreshContent();
                SaveNetworkServers();
                return;
            }
            if (string.IsNullOrWhiteSpace(server.ContentDirectoryControlUrl))
            {
                _networkContentStatus = L("Questo server non espone un catalogo DLNA leggibile.", "This server does not expose a readable DLNA catalog.");
                Invalidate();
                return;
            }

            int browseVersion = Interlocked.Increment(ref _networkBrowseVersion);
            var browseCts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            var previousBrowse = Interlocked.Exchange(ref _networkBrowseCts, browseCts);
            try { previousBrowse?.Cancel(); } catch { }
            try { previousBrowse?.Dispose(); } catch { }

            _networkContentLoading = true;
            _networkContentStatus = L($"Connessione a {server.Name}...", $"Connecting to {server.Name}...");
            Invalidate();

            Task.Run(async () =>
            {
                List<LibraryItem> items = new();
                string? error = null;
                try
                {
                    items = await BrowseDlnaServerItemsAsync(server, browseCts.Token, partial =>
                    {
                        try
                        {
                            if (IsDisposed || !IsHandleCreated || browseVersion != Volatile.Read(ref _networkBrowseVersion))
                                return;
                            BeginInvoke(new Action(() =>
                            {
                                if (browseVersion != Volatile.Read(ref _networkBrowseVersion) || partial.Count == 0)
                                    return;

                                _networkItems = partial;
                                _networkCatalogRevision++;
                                _networkConnectedServerKey = serverKey;
                                _networkContentStatus = L($"{partial.Count:N0} elementi trovati, continuo a leggere…", $"{partial.Count:N0} items found, still scanning…");
                                if (!IsNetworkSourceActive()) return;
                                if (_view == PageView.Network)
                                {
                                    _view = PageView.Collection;
                                    _category = BestNetworkStartCategory(_networkItems);
                                    _filter = "All";
                                    _resumeOffset = 0;
                                    ResetSmoothGridScroll();
                                }
                                // Category milestones and throttled progress batches make
                                // new media appear without rebuilding for every DLNA page.
                                RefreshContent();
                            }));
                        }
                        catch { }
                    });
                }
                catch (OperationCanceledException)
                {
                    error = L("Timeout durante la lettura del catalogo DLNA.", "Timed out while reading the DLNA catalog.");
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                try
                {
                    if (IsDisposed || !IsHandleCreated)
                        return;

                    BeginInvoke(new Action(() =>
                    {
                        if (browseVersion != Volatile.Read(ref _networkBrowseVersion))
                            return;
                        if (ReferenceEquals(Interlocked.CompareExchange(ref _networkBrowseCts, null, browseCts), browseCts))
                            browseCts.Dispose();
                        _networkContentLoading = false;
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            _networkContentStatus = L("Connessione non riuscita: ", "Connection failed: ") + ShortNetworkError(error);
                            Invalidate();
                            return;
                        }

                        _networkItems = items;
                        _networkCatalogRevision++;
                        _networkConnectedServerKey = serverKey;
                        SaveNetworkServers();
                        if (!IsNetworkSourceActive()) { Invalidate(); return; }

                        if (_networkItems.Count == 0)
                        {
                            _networkContentStatus = L("Server collegato, ma non ho trovato file multimediali.", "Server connected, but no media files were found.");
                            _view = PageView.Network;
                            Invalidate();
                            return;
                        }

                        _networkContentStatus = L($"{_networkItems.Count:N0} elementi caricati da {server.Name}.", $"{_networkItems.Count:N0} items loaded from {server.Name}.");
                        if (_view == PageView.Network)
                        {
                            _view = PageView.Collection;
                            _category = BestNetworkStartCategory(_networkItems);
                        }
                        _filter = "All";
                        _resumeOffset = 0;
                        ResetSmoothGridScroll();
                        _photoVisibleLimit = PhotoPageSize;
                        if (!string.IsNullOrEmpty(_searchBox.Text))
                            _searchBox.Text = string.Empty;
                        UpdateSearchPlaceholder();
                        RefreshContent();
                    }));
                }
                catch { }
            });
        }

        private void CancelNetworkBrowse()
        {
            Interlocked.Increment(ref _networkBrowseVersion);
            var cts = Interlocked.Exchange(ref _networkBrowseCts, null);
            try { cts?.Cancel(); } catch { }
            try { cts?.Dispose(); } catch { }
            _networkContentLoading = false;
            _networkContentStatus = L("Caricamento DLNA annullato.", "DLNA loading cancelled.");
            Invalidate();
        }

        private static async Task<List<NetworkServerViewItem>> DiscoverDlnaServersAsync(TimeSpan timeout)
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

            return result
                .GroupBy(NetworkServerKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private int _networkCatalogRevision;

        internal IReadOnlyList<string>? GetPhotoPathsForViewer(string currentPath)
        {
            if (_category != "Photos") return null;
            var visible = VisibleItems().Where(item => item.Category == "Photos").ToList();
            var paths = BuildTemporalSections(visible).SelectMany(section => section.Items).Select(item => item.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return paths.Contains(currentPath, StringComparer.OrdinalIgnoreCase) ? paths : null;
        }

        internal IReadOnlyList<string>? GetNetworkPhotoPaths(string currentPath)
        {
            if (!IsNetworkSourceActive()) return null;
            var paths = _networkItems.Where(x => x.Category == "Photos").Select(x => x.Path).ToList();
            return paths.Contains(currentPath, StringComparer.OrdinalIgnoreCase) ? paths : null;
        }

        private static async Task<List<LibraryItem>> BrowseDlnaServerItemsAsync(
            NetworkServerViewItem server,
            CancellationToken ct,
            Action<List<LibraryItem>>? progress = null)
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
                // Alcuni server DLNA rispondono lentamente su cartelle profonde: i risultati
                // gia letti sono comunque usabili e non vanno convertiti in errore.
            }

            if (result.Count == 0 && failedRoots > 0 && !string.IsNullOrWhiteSpace(lastError))
                throw new InvalidOperationException(lastError);

            return NormalizeNetworkItems(result);
        }

        private static int NetworkContainerPriority(string? title, string? upnpClass, string? trail, int depth)
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

        private static IEnumerable<XElement> SelectNetworkBrowseContainers(XDocument document, string serverName)
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

        private static int NetworkCategoryMask(IEnumerable<LibraryItem> items)
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

        private static List<LibraryItem> NormalizeNetworkItems(IEnumerable<LibraryItem> source)
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

        private static int NetworkDuplicateCategoryPriority(string? category)
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

        private static int NetworkDuplicateMetadataScore(LibraryItem item)
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

        private static void TryApplyDlnaHttpHeaders(HttpClient http)
        {
            try { http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CinecorePlayer/1.0 DLNADOC/1.50 UPnP/1.0"); } catch { }
            try { http.DefaultRequestHeaders.TryAddWithoutValidation("X-AV-Client-Info", "CinecorePlayer/1.0"); } catch { }
        }

        private static async Task<(XDocument Document, string ServiceType, bool QuoteSoapAction)> DlnaBrowseWithFallbacksAsync(HttpClient http, Uri controlUri, string serviceType, string objectId, int start, int count, CancellationToken ct)
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

        private static async Task<XDocument> DlnaBrowseAsync(HttpClient http, Uri controlUri, string serviceType, string objectId, int start, int count, bool quoteSoapAction, CancellationToken ct)
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

        private static IEnumerable<string> NetworkServiceTypeCandidates(string? serviceType)
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

        private static XElement? SelectBestDlnaResource(IReadOnlyList<XElement> resources, string itemClass)
        {
            return resources
                .Where(IsPlayableDlnaResource)
                .OrderByDescending(resource => DlnaResourceScore(resource) + DlnaExpectedKindBonus(resource, itemClass))
                .FirstOrDefault();
        }

        private static XElement? SelectBestFallbackDlnaResource(IReadOnlyList<XElement> resources, string itemClass)
        {
            return resources
                .Where(r => !string.IsNullOrWhiteSpace((r.Value ?? string.Empty).Trim()))
                .OrderByDescending(resource => DlnaResourceScore(resource) + DlnaExpectedKindBonus(resource, itemClass))
                .FirstOrDefault();
        }

        private static int DlnaExpectedKindBonus(XElement resource, string itemClass)
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

        private static bool LooksLikePlayableDlnaItem(XElement item)
        {
            string cls = XmlLocalValue(item, "class").ToLowerInvariant();
            return cls.Contains("videoitem") ||
                   cls.Contains("audioitem") ||
                   cls.Contains("musictrack") ||
                   cls.Contains("imageitem") ||
                   cls.Contains("photo");
        }

        private static int DlnaResourceScore(XElement res)
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
                return score;
            }
            catch
            {
                return 0;
            }
        }

        private static (int Width, int Height) ParseDlnaResolution(string? value)
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

        private static string? DlnaResolutionLabel((int Width, int Height) size)
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

        private static bool IsDlna4K((int Width, int Height) size)
        {
            int longSide = Math.Max(size.Width, size.Height);
            int shortSide = Math.Min(size.Width, size.Height);
            return longSide >= 3500 || shortSide >= 2000;
        }

        private static string NetworkClassificationHints(XElement item)
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

        private static LibraryItem? BuildNetworkLibraryItem(XElement item, Uri resourceBase, string serverName, int order, string contextTrail)
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

        private static NetworkItemMetadata BuildNetworkItemMetadata(XElement item, string url, string category, string contextTrail, string rawTitle)
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

        private static string CleanNetworkDisplayTitle(string? value)
        {
            string clean = WebUtility.HtmlDecode(value ?? string.Empty);
            clean = clean.Replace('\u00a0', ' ');
            clean = Regex.Replace(clean, @"[_\.]+", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '_', '.');
            return string.IsNullOrWhiteSpace(clean) ? "Network item" : clean;
        }

        private static bool LooksLikeGenericNetworkTitle(string? value)
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

        private static int? TryParseNetworkYear(params string?[] values)
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

        private static int? TryParseNetworkInt(params string?[] values)
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

        private static string NetworkSeriesTitleFromTrail(string? trail)
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

        private static bool IsPlayableDlnaResource(XElement res)
        {
            string url = (res.Value ?? string.Empty).Trim();
            string protocol = res.Attribute("protocolInfo")?.Value ?? string.Empty;
            string probe = NetworkProbePath(url);
            return IsVideoPath(probe) || IsMusicPath(probe) || IsPhotoPath(probe) ||
                   protocol.Contains("video/", StringComparison.OrdinalIgnoreCase) ||
                   protocol.Contains("audio/", StringComparison.OrdinalIgnoreCase) ||
                   protocol.Contains("image/", StringComparison.OrdinalIgnoreCase);
        }

        private static string? NetworkArtworkUrl(XElement item, IReadOnlyList<XElement> resources, Uri resourceBase, string category)
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

        private static string CategoryForNetworkResource(string url, string title, string upnpClass, string protocolInfo, string contextTrail, string metadataHints)
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

        private static bool ParsedLooksLikeNetworkEpisode(MovieMetadataService.MediaTitleInfo? parsed)
        {
            return parsed?.EpisodeNumber.HasValue == true ||
                   (parsed?.SeasonNumber.HasValue == true && !string.IsNullOrWhiteSpace(parsed.SeriesTitle));
        }

        private static bool LooksLikeNetworkMovieItem(string sample, string trail)
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

        private static bool LooksLikeNetworkVideosContainer(string trail)
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

        private static bool LooksLikeNetworkPersonalVideoItem(string sample)
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

        private static string NormalizeNetworkTrail(string value)
        {
            return " " + Regex.Replace(value ?? string.Empty, @"[^a-z0-9]+", " ").Trim().ToLowerInvariant() + " ";
        }

        private static bool HasNetworkMovieTrail(string normalizedTrail)
        {
            return normalizedTrail.Contains(" movie ") ||
                   normalizedTrail.Contains(" movies ") ||
                   normalizedTrail.Contains(" film ") ||
                   normalizedTrail.Contains(" films ") ||
                   normalizedTrail.Contains(" films by ") ||
                   normalizedTrail.Contains(" cinema ");
        }

        private static bool HasNetworkTvTrail(string normalizedTrail)
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

        private static bool HasGenericNetworkVideoTrail(string normalizedTrail)
        {
            return normalizedTrail.Contains(" video ") ||
                   normalizedTrail.Contains(" videos ") ||
                   normalizedTrail.Contains(" clip ") ||
                   normalizedTrail.Contains(" clips ") ||
                   normalizedTrail.Contains(" recording ") ||
                   normalizedTrail.Contains(" recordings ");
        }

        private static bool LooksLikeNetworkDateNamedVideo(string sample)
        {
            if (string.IsNullOrWhiteSpace(sample))
                return false;

            return Regex.IsMatch(sample, @"\b(?:vid|pxl|img|dsc|dscn|mvi|gopr|gopro|gh\d{2}|wa)[-_ ]?\d{4,}", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(sample, @"\b(?:20\d{2}|19\d{2})[-_. ](?:0[1-9]|1[0-2])[-_. ](?:0[1-9]|[12]\d|3[01])\b", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(sample, @"\b(?:0[1-9]|[12]\d|3[01])[-_. ](?:0[1-9]|1[0-2])[-_. ](?:20\d{2}|19\d{2})\b", RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeNetworkTvItem(string sample)
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

        private static string BestNetworkStartCategory(IReadOnlyList<LibraryItem> items)
        {
            var best = items
                .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key == "Movies" ? 0 : group.Key == "TV Series" ? 1 : group.Key == "Music" ? 2 : 3)
                .FirstOrDefault()?.Key;
            return NormalizeCategoryKey(best) is string normalized && !string.IsNullOrWhiteSpace(normalized) ? normalized : "Movies";
        }

        private static bool IsNetworkVideoItem(LibraryItem item)
        {
            return string.Equals(item.Category, "Videos", StringComparison.OrdinalIgnoreCase) ||
                   (IsVideoPath(NetworkProbePath(item.Path)) &&
                    !string.Equals(item.Category, "Movies", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.Category, "TV Series", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsNetworkVideoCategory(string category)
        {
            return string.Equals(category, "Movies", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(category, "TV Series", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(category, "Videos", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNetworkPath(string path)
        {
            try
            {
                return Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                       (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private static string NetworkProbePath(string pathOrUrl)
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

        private static string NetworkTitleFromUrl(string url)
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

        private static double? ParseDlnaDurationMinutes(string? value)
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

        private static string ExtractSoapResultXml(XDocument doc)
        {
            var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Result");
            if (result == null)
                return string.Empty;
            if (result.HasElements)
                return string.Concat(result.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));
            return result.Value ?? string.Empty;
        }

        private static bool TryParseDidlDocument(string raw, out XDocument didl)
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

        private static IEnumerable<string> DidlXmlCandidates(string raw)
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

        private static string EscapeBareXmlAmpersands(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml) || xml.IndexOf('&') < 0)
                return xml;

            return Regex.Replace(xml, "&(?!amp;|lt;|gt;|quot;|apos;|#\\d+;|#x[0-9a-fA-F]+;)", "&amp;");
        }

        private static int SoapIntValue(XDocument doc, string localName)
        {
            string value = XmlLocalValue(doc.Root, localName);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        }

        private static string XmlLocalValue(XContainer? root, string localName)
        {
            if (root == null || string.IsNullOrWhiteSpace(localName))
                return string.Empty;
            return root.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim() ?? string.Empty;
        }

        private static XElement? FindUpnpService(XDocument doc, string serviceNameFragment)
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

        private static Uri ResolveDeviceBaseUri(Uri location, string urlBase)
        {
            if (!string.IsNullOrWhiteSpace(urlBase) && Uri.TryCreate(urlBase.Trim(), UriKind.Absolute, out var baseUri))
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

        private static string ResolveUpnpUrl(Uri baseUri, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            value = WebUtility.HtmlDecode(value.Trim());
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
                return absolute.ToString();
            if (Uri.TryCreate(baseUri, value, out var relative))
                return relative.ToString();
            return value;
        }

        private static string SecurityElementEscape(string value)
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

        private static string ShortNetworkError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return "errore sconosciuto";

            string clean = error.Trim()
                .Replace("\r", " ")
                .Replace("\n", " ");
            while (clean.Contains("  ", StringComparison.Ordinal))
                clean = clean.Replace("  ", " ");
            return clean.Length > 96 ? clean[..96] + "..." : clean;
        }


    }
}
