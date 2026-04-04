using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing;
using System.Diagnostics;
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
        // ------------ HEADER (search / chip estensione / chip sort / browse file) ------------
        private void BuildHeader()
        {
            _search = new SearchBox { Width = 360, Height = 32, Margin = new Padding(0) };
            _search.Placeholder = "Cerca nome o percorso…";
            _search.TextChanged += (_, __) =>
            {
                _searchDebounce.Stop();
                _searchDebounce.Start();
            };

            _header.Controls.Add(_search);

            _chipExt = new Chip("Estensione: Tutte") { Height = 32, TabStop = false };
            _chipExt.Click += (_, __) => ShowMenuExt();

            _chipSort = new Chip("Ordina: Recenti") { Height = 32, TabStop = false };
            _chipSort.Click += (_, __) => ShowMenuSort();

            _btnCollectionBack = new HeaderActionButton("Indietro")
            {
                Width = 104,
                Height = 32,
                TabStop = false
            };
            _btnCollectionBack.Click += (_, __) =>
            {
                if (HandleCollectionBackRequested())
                {
                    try { LayoutHeader(); } catch { }
                }
            };

            _btnCreatePlaylist = new HeaderActionButton("Nuova playlist")
            {
                Width = 136,
                Height = 32,
                TabStop = false
            };
            _btnCreatePlaylist.Click += (_, __) => HandlePlaylistCreateRequested();

            _btnPlayCollection = new HeaderActionButton("Riproduci")
            {
                Width = 112,
                Height = 32,
                TabStop = false
            };
            _btnPlayCollection.Click += (_, __) => RequestHeaderPlay();

            _btnShuffleCollection = new HeaderActionButton("Shuffle")
            {
                Width = 104,
                Height = 32,
                TabStop = false
            };
            _btnShuffleCollection.Click += (_, __) => RequestHeaderShuffle();

            _btnYtTrending = new HeaderActionButton("Esplora")
            {
                Width = 120,
                Height = 32,
                TabStop = false
            };
            _btnYtTrending.Click += (_, __) =>
            {
                try
                {
                    if (!string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase))
                        return;

                    try { _search.Inner.Text = string.Empty; } catch { }
                    _ytPane?.HostShowTrending(force: true);
                }
                catch { }
            };

            _btnYtPersonal = new HeaderActionButton("Per te")
            {
                Width = 92,
                Height = 32,
                TabStop = false
            };
            _btnYtPersonal.Click += (_, __) =>
            {
                try
                {
                    if (!string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase))
                        return;
                    _ytPane?.HostShowPersonal();
                }
                catch { }
            };

            _btnYtLogin = new HeaderActionButton("Accedi")
            {
                Width = 88,
                Height = 32,
                TabStop = false
            };
            _btnYtLogin.Click += (_, __) =>
            {
                try
                {
                    if (!string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase))
                        return;
                    _ytPane?.HostLogin();
                }
                catch { }
            };

            _btnYtLogout = new HeaderActionButton("Esci")
            {
                Width = 72,
                Height = 32,
                TabStop = false
            };
            _btnYtLogout.Click += (_, __) =>
            {
                try
                {
                    if (!string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase))
                        return;
                    _ytPane?.HostLogout();
                }
                catch { }
            };

            _btnBrowse = new HeaderActionButton("Scegli file")
            {
                Width = 148,
                Height = 32,
                TabStop = false
            };
            _btnBrowse.Click += (_, __) =>
            {
                using var ofd = new OpenFileDialog
                {
                    Filter =
                        "Media|*.mkv;*.m2ts;*.ts;*.iso;*.mp4;*.m4v;*.mov;*.avi;*.wmv;*.webm;*.flv;*.flac;*.mp3;*.mka;*.aac;*.ogg;*.wav;*.wma;*.m4a;*.opus|Tutti i file|*.*"
                };
                if (ofd.ShowDialog() == DialogResult.OK)
                    SafeOpen(ofd.FileName);
            };

            _btnAddFolder = new HeaderActionButton("API TMDb")
            {
                Width = 112,
                Height = 32,
                TabStop = false
            };
            _btnAddFolder.Click += (_, __) => ConfigureTmdbApiKey();

            _btnManageFolders = new HeaderActionButton("Librerie…")
            {
                Width = 122,
                Height = 32,
                TabStop = false
            };
            _btnManageFolders.Click += (_, __) => ManageFoldersForCurrentCategory();

            _btnRefresh = new HeaderActionButton("Aggiorna")
            {
                Width = 120,
                Height = 32,
                TabStop = false
            };
            _btnRefresh.Click += (_, __) => ForceRescanCurrentCategory();

            _header.Controls.AddRange(new Control[]
            {
                _btnBrowse,
                _btnYtLogout,
                _btnYtLogin,
                _btnYtPersonal,
                _btnYtTrending,
                _btnRefresh,
                _btnManageFolders,
                _btnAddFolder,
                _btnCollectionBack,
                _btnCreatePlaylist,
                _btnPlayCollection,
                _btnShuffleCollection,
                _chipSort,
                _chipExt
            });

            BuildHeaderFilters();
            LayoutHeader();
        }

        private void LayoutHeader()
        {
            bool isLocalCat = IsLocalLibraryCategory(_selCat);
            bool isLocalSrc = string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase);
            bool isYouTubeSrc = string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase);
            bool isFilmCat = string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase);
            bool showTmdbApiButton = isLocalSrc && isFilmCat;

            _btnYtTrending.Visible = isYouTubeSrc;
            _btnYtPersonal.Visible = isYouTubeSrc;
            _btnYtLogin.Visible = isYouTubeSrc;
            _btnYtLogout.Visible = isYouTubeSrc;

            _btnBrowse.Visible = !isYouTubeSrc;
            _btnAddFolder.Visible = showTmdbApiButton;
            _btnManageFolders.Visible = isLocalCat && isLocalSrc;
            _btnRefresh.Visible = isLocalCat && isLocalSrc;
            _chipExt.Visible = isLocalCat && isLocalSrc;
            _chipSort.Visible = isLocalCat && isLocalSrc;
            _btnCollectionBack.Visible = ShouldShowCollectionBackButton();
            _btnCreatePlaylist.Visible = ShouldShowCreatePlaylistButton();
            _btnPlayCollection.Visible = ShouldShowHeaderPlayButton();
            _btnShuffleCollection.Visible = ShouldShowHeaderShuffleButton();

            _search.Placeholder = isYouTubeSrc
                ? "Cerca su YouTube…"
                : "Cerca nome o percorso…";

            _chipExt.AutoSizeToText();
            _chipSort.AutoSizeToText();

            var ordered = new List<Control>();
            void Add(Control c) { if (c.Visible) ordered.Add(c); }

            Add(_chipExt);
            Add(_chipSort);
            Add(_btnPlayCollection);
            Add(_btnShuffleCollection);
            Add(_btnCreatePlaylist);
            Add(_btnCollectionBack);
            Add(_btnRefresh);
            Add(_btnAddFolder);
            Add(_btnManageFolders);
            Add(_btnYtTrending);
            Add(_btnYtPersonal);
            Add(_btnYtLogin);
            Add(_btnYtLogout);
            Add(_btnBrowse);

            const int padX = 16;
            const int padY = 10;
            const int gap = 8;
            const int lineH = 32;
            int innerWidth = Math.Max(260, _header.Width - padX * 2);

            int actionsWidth = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                actionsWidth += ordered[i].Width;
                if (i > 0) actionsWidth += gap;
            }

            bool singleRow = innerWidth >= Math.Max(340, 320 + actionsWidth + 16);

            if (singleRow)
            {
                if (_header.Height != 56)
                    _header.Height = 56;

                int right = _header.Width - padX;
                for (int i = ordered.Count - 1; i >= 0; i--)
                {
                    var ctrl = ordered[i];
                    ctrl.Location = new Point(right - ctrl.Width, (_header.Height - ctrl.Height) / 2);
                    right -= ctrl.Width + gap;
                }

                int searchWidth = Math.Max(320, right - padX);
                _search.Width = searchWidth;
                _search.Location = new Point(padX, (_header.Height - _search.Height) / 2);
                return;
            }

            _search.Width = innerWidth;
            _search.Location = new Point(padX, padY);

            int x = padX;
            int y = _search.Bottom + gap;
            foreach (var ctrl in ordered)
            {
                if (x > padX && x + ctrl.Width > _header.Width - padX)
                {
                    x = padX;
                    y += lineH + gap;
                }

                ctrl.Location = new Point(x, y);
                x += ctrl.Width + gap;
            }

            _header.Height = y + lineH + padY;
        }

        private void ForceRescanCurrentCategory()
        {
            if (string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase))
            {
                try { _dlnaCatStartId.Clear(); } catch { }
                try { _dlnaIndexedItems.Clear(); } catch { }
                _dlnaIndexedServerKey = string.Empty;
                RefreshDlnaSourceContent();
                return;
            }

            // azzera l’indice per la categoria corrente e forza una nuova indicizzazione completa
            _libraryIndex.ReplacePaths(_selCat, Array.Empty<string>());
            RefreshContent();
        }

        public List<string> transport_folder_name = new List<string>();

        private bool AddFolderForCurrentCategory(bool refreshAfterAdd)
        {
            if (IsDisposed) return false;

            var cat = _selCat;

            if (!IsLocalLibraryCategory(cat))
                return false;

            if (!string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                return false;

            string catLabel = string.Equals(cat, "Film", StringComparison.OrdinalIgnoreCase) ? "film e serie TV" : cat.ToLowerInvariant();

            using var fbd = new FolderBrowserDialog
            {
                Description = $"Seleziona una cartella o un intero disco per i tuoi {catLabel}.",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
                RootFolder = Environment.SpecialFolder.MyComputer
            };

            var owner = FindForm();

            List<Form>? hiddenOwned = null;
            try
            {
                if (owner is Form of)
                {
                    foreach (var child in of.OwnedForms)
                    {
                        try
                        {
                            if (child != null && child.Visible)
                            {
                                hiddenOwned ??= new List<Form>();
                                hiddenOwned.Add(child);
                                child.Hide();
                            }
                        }
                        catch { }
                    }
                }

                try { owner?.Activate(); } catch { }
            }
            catch { }

            DialogResult result;
            try
            {
                result = owner != null ? fbd.ShowDialog(owner) : fbd.ShowDialog();
            }
            finally
            {
                if (hiddenOwned != null)
                {
                    foreach (var child in hiddenOwned)
                    {
                        try { if (child != null && !child.IsDisposed) child.Show(); } catch { }
                    }
                }
            }
            if (result != DialogResult.OK)
                return false;

            var raw = fbd.SelectedPath ?? string.Empty;
            var path = NormalizeRootPath(raw);

            if (string.IsNullOrWhiteSpace(path))
                path = NormalizeRootPath(raw.Length >= 2 ? raw.Substring(0, 2) : raw);

            if (string.IsNullOrWhiteSpace(path))
            {
                SystemSounds.Beep.Play();
                return false;
            }
            transport_folder_name.Add(path);

            EnsureRootsOverlayEditSession(cat);
            AddRootToRootsOverlayDraft(cat, path);
            ShowRootsOverlay();
            RefreshRootsOverlayList();

            return true;
        }

        // wrapper per il comportamento classico: apri direttamente Librerie.
        private void AddFolderForCurrentCategory()
        {
            ManageFoldersForCurrentCategory();
        }
        private void ManageFoldersForCurrentCategory()
        {
            // solo per Film/Video/Foto/Musica sulla sorgente "Il mio computer"
            if (!IsLocalLibraryCategory(_selCat) ||
                !string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                return;

            ShowRootsOverlay();
        }


        private void ConfigureTmdbApiKey()
        {
            if (IsDisposed)
                return;

            var owner = FindForm();
            using var dlg = new Form
            {
                Text = "Configura API TMDb",
                StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(520, 176),
                BackColor = Theme.Panel,
                ForeColor = Theme.Text
            };

            var lbl = new Label
            {
                Left = 18,
                Top = 16,
                Width = 484,
                Height = 42,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5f),
                Text = "Inserisci una chiave API TMDb personale. Se lasci vuoto o usi il ripristino, Cinecore torna alla configurazione predefinita."
            };

            var txt = new TextBox
            {
                Left = 18,
                Top = 68,
                Width = 484,
                Height = 30,
                Font = new Font("Segoe UI", 10f),
                Text = MovieMetadataService.GetUserTmdbApiKey() ?? string.Empty
            };

            var btnCancel = new Button
            {
                Text = "Annulla",
                Width = 96,
                Height = 30,
                Left = 406,
                Top = 122,
                DialogResult = DialogResult.Cancel
            };

            var btnReset = new Button
            {
                Text = "Usa predefinita",
                Width = 116,
                Height = 30,
                Left = 282,
                Top = 122
            };

            var btnSave = new Button
            {
                Text = "Salva",
                Width = 96,
                Height = 30,
                Left = 178,
                Top = 122,
                DialogResult = DialogResult.OK
            };

            bool changed = false;

            btnSave.Click += (_, __) =>
            {
                try
                {
                    var value = (txt.Text ?? string.Empty).Trim();
                    MovieMetadataService.SetUserTmdbApiKey(string.IsNullOrWhiteSpace(value) ? null : value);
                    changed = true;
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                }
                catch
                {
                    SystemSounds.Beep.Play();
                }
            };

            btnReset.Click += (_, __) =>
            {
                try
                {
                    MovieMetadataService.SetUserTmdbApiKey(null);
                    changed = true;
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                }
                catch
                {
                    SystemSounds.Beep.Play();
                }
            };

            dlg.Controls.Add(lbl);
            dlg.Controls.Add(txt);
            dlg.Controls.Add(btnCancel);
            dlg.Controls.Add(btnReset);
            dlg.Controls.Add(btnSave);
            dlg.AcceptButton = btnSave;
            dlg.CancelButton = btnCancel;

            if (owner != null) dlg.ShowDialog(owner);
            else dlg.ShowDialog();

            if (!changed)
                return;

            try
            {
                if (string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                {
                    ForceRescanCurrentCategory();
                }
                else
                {
                    ForceCarouselRefresh();
                }
            }
            catch { }
        }


        private void TryShowOnScreenKeyboard()
        {
            try
            {
                // 1) Se la touch keyboard (TabTip) è già in esecuzione ma è stata chiusa a mano,
                //    il processo può rimanere vivo. In quel caso bisogna ri-mostrare la finestra.
                var hwndTabTip = FindWindow("IPTip_Main_Window", null);
                if (hwndTabTip != IntPtr.Zero)
                {
                    ShowWindow(hwndTabTip, SW_RESTORE);
                    ShowWindow(hwndTabTip, SW_SHOW);
                    SetForegroundWindow(hwndTabTip);
                    return;
                }

                // 2) Fallback: OSK classico (se già aperto, riportalo davanti)
                var hwndOsk = FindWindow("OSKMainClass", null);
                if (hwndOsk != IntPtr.Zero)
                {
                    ShowWindow(hwndOsk, SW_RESTORE);
                    ShowWindow(hwndOsk, SW_SHOW);
                    SetForegroundWindow(hwndOsk);
                    return;
                }

                // 3) Avvia TabTip se disponibile (prova anche i path x86/64), altrimenti OSK.
                string? tabTip = null;
                try
                {
                    var candidates = new List<string>();

                    // CommonProgramFiles può puntare a (x86) in base al target; proviamo entrambe.
                    var cpf = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
                    var cpf86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
                    if (!string.IsNullOrWhiteSpace(cpf))
                        candidates.Add(Path.Combine(cpf, "microsoft shared", "ink", "TabTip.exe"));
                    if (!string.IsNullOrWhiteSpace(cpf86))
                        candidates.Add(Path.Combine(cpf86, "microsoft shared", "ink", "TabTip.exe"));

                    var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                    if (!string.IsNullOrWhiteSpace(pf))
                        candidates.Add(Path.Combine(pf, "Common Files", "microsoft shared", "ink", "TabTip.exe"));
                    if (!string.IsNullOrWhiteSpace(pf86))
                        candidates.Add(Path.Combine(pf86, "Common Files", "microsoft shared", "ink", "TabTip.exe"));

                    tabTip = candidates.FirstOrDefault(File.Exists);
                }
                catch { tabTip = null; }

                if (!string.IsNullOrWhiteSpace(tabTip) && File.Exists(tabTip))
                {
                    Process.Start(new ProcessStartInfo { FileName = tabTip, UseShellExecute = true });
                    return;
                }

                Process.Start(new ProcessStartInfo { FileName = "osk.exe", UseShellExecute = true });
            }
            catch
            {
                // no-op
            }
        }

        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

    }
}
