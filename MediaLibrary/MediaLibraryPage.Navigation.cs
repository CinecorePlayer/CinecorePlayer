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
        public event Action? RemoteNavigationResetRequested;
        public bool IsRemoteNavigationReady => _mask != null && !_mask.Visible;

        private void NotifyRemoteNavigationResetRequested()
        {
            try { RemoteNavigationResetRequested?.Invoke(); } catch { }
        }
        // ------------ mask overlay ------------
        private void ShowMask(string msg)
            => ShowMask(msg, showSpinner: true);

        private void ShowMask(string msg, bool showSpinner)
        {
            try { _mask.SetVisualState(msg, showSpinner); } catch { }
            _mask.Visible = true;
            _mask.BringToFront();
            try { HideStickySectionDivider(); } catch { }
            try { _mask.Invalidate(); _mask.Update(); } catch { }

            // IMPORTANT: la mask non deve coprire overlay interattivi (Roots/OSK).
            // Altrimenti l'utente vede la tastiera "sparire" mentre è ancora aperta.
            try { if (_rootsOverlay != null && _rootsOverlay.Visible) _rootsOverlay.BringToFront(); } catch { }
            try { if (_appOskOverlay != null && _appOskOverlay.Visible) _appOskOverlay.BringToFront(); } catch { }
        }

        private void HideMask()
        {
            _mask.Visible = false;

            // IMPORTANT:
            // Non portare la griglia in primo piano qui.
            // Durante ricerca/filtri il mask viene mostrato/nascosto spesso e questo
            // "BringToFront" finiva per coprire overlay interni (es. la nostra OSK).
            // Gli overlay (Roots/OSK) si gestiscono esplicitamente e devono restare SEMPRE sopra.
            try { if (_rootsOverlay != null) _rootsOverlay.BringToFront(); } catch { }
            try { if (_appOskOverlay != null && _appOskOverlay.Visible) _appOskOverlay.BringToFront(); } catch { }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try { UpdateStickySectionDivider(); } catch { }
                    try { TryFulfillPendingContentFocusAfterRender(); } catch { }
                }));
            }
            catch { }
        }


        // ------------ LEFT NAV (logo + categorie/sorgenti + footer Chiudi) ------------
        private void BuildLeftBody()
        {
            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = Theme.Nav,
                Padding = new Padding(10, 10, 10, 6)
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _leftBody.Controls.Add(stack);

            stack.Controls.Add(MakeLogoHeader());

            stack.Controls.Add(MkSection("Catalogo").WithMargin(0, 10, 0, 0));
            foreach (var c in _catOrder)
            {
                var b = new NavButton(c) { Dock = DockStyle.Top };
                b.Click += (_, __) =>
                {
                    SetCategory(c);
                    RefreshNavPaint();
                };
                _catButtons.Add(b);
                stack.Controls.Add(b);
            }

            stack.Controls.Add(MkSection("SORGENTE").WithMargin(0, 12, 0, 0));
            foreach (var s in _srcOrder)
            {
                var b = new NavButton(s) { Dock = DockStyle.Top };
                b.Click += (_, __) =>
                {
                    SetSource(s);
                    RefreshNavPaint();
                };
                _srcButtons.Add(b);
                stack.Controls.Add(b);
            }

            // filler
            stack.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.Nav });
        }

        private Control MakeLogoHeader()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var logoPath = Path.Combine(baseDir, "assets", "logo.png"); // logo bianco orizzontale
                if (File.Exists(logoPath))
                {
                    var pic = new PictureBox
                    {
                        Height = 80,
                        Dock = DockStyle.Top,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        BackColor = Theme.Nav,
                        Padding = new Padding(8, 0, 0, 0),
                        Margin = new Padding(0)
                    };
                    using (var bmp = new Bitmap(logoPath))
                    {
                        pic.Image = new Bitmap(bmp); // clone → niente file lock
                    }
                    return pic;
                }
            }
            catch { }

            // fallback vuoto senza scritta cinecore
            return new Panel
            {
                Height = 44,
                Dock = DockStyle.Top,
                BackColor = Theme.Nav,
                Margin = new Padding(0)
            };
        }
        private static string NormalizeRootPath(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            string path = input.Trim();

            // prima uniformiamo gli slash (ma NON tocchiamo l’eventuale prefisso UNC \\server\share)
            path = path.Replace('/', Path.DirectorySeparatorChar);

            // --- pattern “drive letter” puri: D / d / D: / d: / D:\ / d:\ ---
            if (path.Length == 1 && char.IsLetter(path[0]))
            {
                // "D" → "D:\"
                return $"{char.ToUpperInvariant(path[0])}:{Path.DirectorySeparatorChar}";
            }

            if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':')
            {
                // "D:" → "D:\"
                return $"{char.ToUpperInvariant(path[0])}:{Path.DirectorySeparatorChar}";
            }

            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == Path.DirectorySeparatorChar))
            {
                // "D:\" o "D:\qualcosa"
                char drive = char.ToUpperInvariant(path[0]);

                if (path.Length == 3)
                {
                    // esattamente "D:\" → normalizza e basta
                    return $"{drive}:{Path.DirectorySeparatorChar}";
                }

                // "D:\qualcosa" → drive + resto ripulito dagli slash doppi
                string rest = path.Substring(2)
                                  .Replace('\\', Path.DirectorySeparatorChar)
                                  .Replace('/', Path.DirectorySeparatorChar);

                // evitiamo roba tipo "D::\"
                if (rest.Length == 0 || rest == Path.DirectorySeparatorChar.ToString())
                    return $"{drive}:{Path.DirectorySeparatorChar}";

                return $"{drive}:{rest}";
            }

            // Se è un path radicato (es. UNC \\server\share o C:\foo\bar) proviamo a canonizzarlo
            try
            {
                if (Path.IsPathRooted(path))
                    path = Path.GetFullPath(path);
            }
            catch
            {
                // se fallisce, lasciamo il path così com'è
            }

            return path;
        }

        private void BuildLeftFooter()
        {
            var btnClose = new FlatButton("Chiudi", FlatButton.Variant.Secondary)
            {
                Dock = DockStyle.Fill,
                Height = 32,
                TabStop = false
            };
            btnClose.Click += (_, __) => CloseRequested?.Invoke();

            // keep a reference for DPAD navigation (menu left)
            _btnClose = btnClose;
            _leftFooter.Controls.Add(btnClose);
        }

        private static Label MkSection(string text) => new()
        {
            Text = text.ToUpperInvariant(),
            AutoSize = false,
            Height = 20,
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(6, 0, 0, 0),
            ForeColor = Theme.Muted,
            BackColor = Theme.Nav,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };

        private void RefreshNavPaint()
        {
            bool dlnaSource = string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase);

            foreach (var b in _catButtons)
            {
                bool disableForDlna = dlnaSource &&
                    (string.Equals(b.Text, "Playlist", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(b.Text, "Preferiti", StringComparison.OrdinalIgnoreCase));

                try { b.Enabled = !disableForDlna; } catch { }
                b.Selected = !disableForDlna && string.Equals(b.Text, _selCat, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var b in _srcButtons)
                b.Selected = string.Equals(b.Text, _selSrc, StringComparison.OrdinalIgnoreCase);

            _leftBody.Invalidate(true);
        }
        private void SetCategory(string c)
        {
            if (string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(c, "Playlist", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c, "Preferiti", StringComparison.OrdinalIgnoreCase)))
            {
                c = "Film";
            }

            _selCat = c;
            NotifyRemoteNavigationResetRequested();

            try { StopAnimatedLibraryVisuals(); } catch { }

            // Evita che un testo di ricerca/filtri resti attivo passando a un'altra categoria
            ResetSearchOnNavigationChange();


            ResetPhotoPagingState();

            ResetCollectionSelectionStateForTargetCategory(c);

            // NEW: titolo sezione diversi per Musica vs resto
            if (string.Equals(_selCat, "Musica", StringComparison.OrdinalIgnoreCase))
                _secRecenti.Title = "Recenti";
            else
                _secRecenti.Title = "Riprendi";

            if (_rootsOverlay != null)
                CloseRootsOverlay(commit: false, refreshAfterCommit: false);

            // Con DLNA il catalogo continua a funzionare: dopo la scelta del server,
            // il cambio categoria deve ricaricare i contenuti remoti filtrati.
            if (string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase))
            {
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;
                _secAll.Visible = false;

                BuildHeaderFilters();
                try { LayoutHeader(); } catch { }
                try { ResetRecentCarouselVisualState(); } catch { }
                try { RefreshContent(); } catch { }
                RefreshNavPaint();
                AlignCarouselViewport();
                return;
            }

            _recents.PruneToCategory(_selCat, ExtsForCategory(_selCat));

            BuildHeaderFilters();
            try { LayoutHeader(); } catch { }

            bool isPlaylist = string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase);
            bool isPreferiti = string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase);
            bool isFoto = string.Equals(_selCat, "Foto", StringComparison.OrdinalIgnoreCase);
            bool isUrlSrc = string.Equals(_selSrc, "URL", StringComparison.OrdinalIgnoreCase);
            bool isYtSrc = string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase);
            bool isDlnaSrc = string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase);

            bool showCarousel = ShouldShowRecentCarouselForCurrentState();

            _secRecenti.Visible = showCarousel;
            _carouselHost.Visible = showCarousel;
            try { ResetRecentCarouselVisualState(); } catch { }

            if (showCarousel)
            {
                string expectedCat = _selCat;
                string expectedSrc = _selSrc;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (IsDisposed) return;
                            if (!string.Equals(_selCat, expectedCat, StringComparison.OrdinalIgnoreCase)) return;
                            if (!string.Equals(_selSrc, expectedSrc, StringComparison.OrdinalIgnoreCase)) return;
                            LoadRecentsCarouselImmediate();
                        }
                        catch { }
                    }));
                }
                catch { }
            }

            if (!isUrlSrc && !isYtSrc && !isDlnaSrc)
            {
                try { ShowMask("Aggiornamento libreria…"); } catch { }
            }

            try { ReconfigureLibraryWatchers(); } catch { }
            RefreshContent();

            RefreshNavPaint();
            AlignCarouselViewport();
        }

        // ⬇️ RIMETTI / LASCIA COSÌ QUESTO
        private void SetSource(string s)
        {
            // se stiamo uscendo da YouTube, interrompi eventuali fetch in corso
            try
            {
                if (!string.Equals(s, "YouTube", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    _ytPane?.CancelPending();
                }
            }
            catch { }

            bool enteringDlna = string.Equals(s, "Rete domestica", StringComparison.OrdinalIgnoreCase);
            bool leavingDlna = !string.Equals(s, "Rete domestica", StringComparison.OrdinalIgnoreCase)
                && string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase);

            _selSrc = s;

            if (enteringDlna &&
                (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase)))
            {
                _selCat = "Film";
            }

            try
            {
                if (enteringDlna)
                    ResetDlnaSelectionState(showPicker: true);
                else if (leavingDlna)
                    _dlnaCts?.Cancel();
            }
            catch { }

            NotifyRemoteNavigationResetRequested();

            try { StopAnimatedLibraryVisuals(); } catch { }

            // Evita che la ricerca/filtri rimangano attivi su una sorgente diversa
            ResetSearchOnNavigationChange();


            ResetPhotoPagingState();

            ResetCollectionSelectionStateForTargetCategory(_selCat);

            if (_rootsOverlay != null)
                CloseRootsOverlay(commit: false, refreshAfterCommit: false);

            // come in SetCategory: aggiorna visibilita' carosello anche al cambio sorgente
            bool isPlaylist = string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase);
            bool isPreferiti = string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase);
            bool isFoto = string.Equals(_selCat, "Foto", StringComparison.OrdinalIgnoreCase);
            bool isUrlSrc = string.Equals(_selSrc, "URL", StringComparison.OrdinalIgnoreCase);
            bool isYtSrc = string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase);
            bool isDlnaSrc = string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase);

            bool showCarousel = ShouldShowRecentCarouselForCurrentState();

            _secRecenti.Visible = showCarousel;
            _carouselHost.Visible = showCarousel;
            try { ResetRecentCarouselVisualState(); } catch { }

            // "Tutti i file" non ha senso su sorgenti non-locali (URL / YouTube / DLNA)
            try
            {
                _secAll.Visible = !(isUrlSrc || isYtSrc || isDlnaSrc);
            }
            catch { }

            // header chips / filtri
            try { BuildHeaderFilters(); } catch { }
            try { LayoutHeader(); } catch { }

            if (showCarousel)
            {
                string expectedCat = _selCat;
                string expectedSrc = _selSrc;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (IsDisposed) return;
                            if (!string.Equals(_selCat, expectedCat, StringComparison.OrdinalIgnoreCase)) return;
                            if (!string.Equals(_selSrc, expectedSrc, StringComparison.OrdinalIgnoreCase)) return;
                            LoadRecentsCarouselImmediate();
                        }
                        catch { }
                    }));
                }
                catch { }
            }

            if (!isUrlSrc && !isYtSrc && !isDlnaSrc)
            {
                try { ShowMask("Aggiornamento libreria…"); } catch { }
            }

            try { ReconfigureLibraryWatchers(); } catch { }
            RefreshContent();
            RefreshNavPaint();

            try { AlignCarouselViewport(); } catch { }

            // Se entri su YouTube con search vuota (e quindi non scatta il debounce),
            // forziamo un primo caricamento (tendenze).
            if (isYtSrc)
            {
                try { BeginInvoke(new Action(() => _ytPane?.HostSetQuery(_search?.Inner?.Text))); } catch { }
            }
        }


        public void ForceCarouselRefresh()
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            // Se chiamato da un thread che non è l’UI, rimbalza sull’UI
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ForceCarouselRefresh));
                return;
            }

            LoadRecentsCarouselImmediate();
        }


    }
}
