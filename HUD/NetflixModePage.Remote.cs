#nullable enable
using System;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    /// <summary>
    /// Spotlight is the home-theatre mode, used from the sofa with the phone remote. The
    /// remote only has a D-pad, OK and Back, so every control is reachable through three
    /// rows: the title carousel (default), the actions (Play · Restart · Queue · More) and
    /// the top bar (Search · Filter). The focus ring is shown even when the window does
    /// not own the keyboard focus, which is the normal case while the phone drives it.
    /// </summary>
    internal sealed partial class NetflixModePage
    {
        private enum RemoteRow { Browse, Actions, TopBar }
        private RemoteRow _remoteRow = RemoteRow.Browse;
        private bool _remoteFocus;

        private string[] RemoteZones(RemoteRow row) => row switch
        {
            RemoteRow.Actions => _restartRect.IsEmpty ? new[] { "play", "queue", "more" } : new[] { "play", "restart", "queue", "more" },
            // Anche la scelta Libreria / Rete, che sta sulla stessa riga della ricerca.
            RemoteRow.TopBar => new[] { "search", "filter", "source-library", "source-network" },
            _ => Array.Empty<string>()
        };

        /// <summary>True when a zone must show its focus ring for remote navigation.</summary>
        private bool RemoteHot(string zone) => _remoteFocus && _remoteRow != RemoteRow.Browse && _keyboardZone == zone;

        internal void ResetRemoteFocus()
        {
            _remoteRow = RemoteRow.Browse;
            _remoteFocus = false;
            _keyboardFocus = false;
            _keyboardZone = string.Empty;
            Invalidate();
        }

        private void EnterRemoteRow(RemoteRow row, string? preferred = null)
        {
            _remoteRow = row;
            _remoteFocus = true;
            ClearPointerHover();
            string[] zones = RemoteZones(row);
            _keyboardZone = zones.Length == 0 ? string.Empty
                : preferred != null && Array.IndexOf(zones, preferred) >= 0 ? preferred : zones[0];
            _keyboardFocus = zones.Length > 0;
            if (row != RemoteRow.TopBar && _searchBox.Focused)
                try { Focus(); } catch { }
            Invalidate();
        }

        public void RemoteMove(string direction)
        {
            string dir = direction.ToLowerInvariant();
            ClearPointerHover();
            if (_castSheetOpen)
            {
                if (dir == "left") MoveCastSheet(-1);
                else if (dir == "right") MoveCastSheet(1);
                // Il foglio e' salito dal basso: la freccia giu' lo rimanda giu'.
                else if (dir == "down") CloseCastSheet();
                return;
            }

            switch (dir)
            {
                case "up":
                    if (_remoteRow == RemoteRow.Browse) EnterRemoteRow(RemoteRow.Actions, "play");
                    else if (_remoteRow == RemoteRow.Actions) EnterRemoteRow(RemoteRow.TopBar, "search");
                    return;
                case "down":
                    if (_remoteRow == RemoteRow.TopBar) EnterRemoteRow(RemoteRow.Actions, "play");
                    else if (_remoteRow == RemoteRow.Actions) EnterRemoteRow(RemoteRow.Browse);
                    return;
                case "left":
                case "right":
                    int step = dir == "left" ? -1 : 1;
                    if (_remoteRow == RemoteRow.Browse)
                    {
                        _remoteFocus = true;
                        if (step < 0) MovePrevious(); else MoveNext();
                        return;
                    }
                    string[] zones = RemoteZones(_remoteRow);
                    int index = Math.Max(0, Array.IndexOf(zones, _keyboardZone));
                    _keyboardZone = zones[Math.Clamp(index + step, 0, zones.Length - 1)];
                    _keyboardFocus = true;
                    _remoteFocus = true;
                    Invalidate();
                    return;
            }
        }

        public void RemoteOk()
        {
            if (_castSheetOpen) { CloseCastSheet(); return; }
            if (_remoteRow == RemoteRow.Browse) { OpenSelected(); return; }

            switch (_keyboardZone)
            {
                case "search":
                    // Text arrives from the phone ("text" command); focusing the field
                    // shows where it goes and lets a real keyboard type too.
                    _searchBox.Visible = true;
                    try { _searchBox.Focus(); } catch { }
                    Invalidate();
                    return;
                case "filter":
                    ShowFilterMenu();
                    Invalidate();
                    return;
                case "source-library":
                case "source-network":
                    {
                        bool network = _keyboardZone == "source-network";
                        if (network != _networkSource) { NetworkSource = network; SourceChangeRequested?.Invoke(network ? "DLNA" : "Library"); }
                        Invalidate();
                        return;
                    }
                case "restart":
                    if (CurrentItem != null) RestartRequested?.Invoke(CurrentItem.Path);
                    return;
                case "queue":
                    QueueCurrent();
                    return;
                case "more":
                    OpenCastSheet();
                    return;
                default:
                    OpenSelected();
                    return;
            }
        }

        /// <summary>Back: cast sheet, then search text, then the focused row; false = leave Spotlight.</summary>
        public bool RemoteBack()
        {
            if (TryHandleBackKey()) return true;
            if (_remoteRow != RemoteRow.Browse)
            {
                EnterRemoteRow(RemoteRow.Browse);
                return true;
            }
            return false;
        }
    }
}
