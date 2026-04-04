#nullable enable
using CinecorePlayer2025;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DirectShowLib;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025.HUD
{
    // ===================== SETTINGS MODAL (stile "libreria") =====================
    internal sealed class SettingsModal : ModalBase
    {
        public event Action<int, bool, bool>? ApplyClicked;
        public event Action<VideoSettings>? ApplyDetailed;

        // Layout base
        private TableLayoutPanel _root = null!;
        private Panel _leftNav = null!;
        private TableLayoutPanel _leftLayout = null!;
        private FlowLayoutPanel _navFlow = null!;
        private PictureBox _logoBox = null!;
        private Label _logoText = null!;
        private Button _btnApply = null!;
        private Button _btnClose = null!;

        private OutlinePanel _rightBorder = null!;
        private Panel _pageHost = null!;
        private Label _lblSection = null!;

        private readonly List<Button> _navButtons = new();
        private int _selNav = 0;

        // Pages
        private Control? _pgGeneral;
        private Control? _pgMadvr;
        private Control? _pgLavVideo;
        private Control? _pgLavAudio;
        private Control? _pgMpcVr;
        private Control? _pgMpcAr;
        private Control? _pgSubtitles;

        // Subtitles auto-open
        private bool _subsAutoOpenPending;
        private bool _subsAutoOpenShowNotFound;

        // Generali: controlli
        private RadioButton _fpsAuto = null!;
        private RadioButton _fps60 = null!;
        private RadioButton _fps24 = null!;
        private RadioButton _hdrAuto = null!;
        private RadioButton _hdrPass = null!;
        private RadioButton _hdrTone = null!;
        private RadioButton _hdrLut = null!;
        private CheckBox _upscale = null!;
        private CheckBox _preferBitstream = null!;
        private ComboBox _cbChroma = null!;
        private ComboBox _cbUp = null!;
        private ComboBox _cbDown = null!;
        private ComboBox _cbRefine = null!;

        private static readonly Guid CLSID_MPCVR = new("71F080AA-8661-4093-B15E-4F6903E77D0A");

        private readonly string[] _navItems =
        {
            "Generali",
            "madVR",
            "LAV VIDEO",
            "LAV AUDIO",
            "MPC VIDEO RENDERER",
            "MPC AUDIO RENDERER",
            "SOTTOTITOLI"
        };

        public SettingsModal() : base("")
        {
            // overlay modal
            OverlayColor = Theme.BackdropDim;
            CloseOnBackdropClick = true;
            CloseOnEscape = true;

            // IMPORTANT: nel tuo PlayerForm i modali sono creati una volta e riusati.
            RemoveFromParentOnClose = false;
            AutoDisposeOnClose = false;

            // stile “libreria”: niente header/footer di ModalBase
            HeaderVisible = false;
            FooterVisible = false;

            // card grande ma coerente
            CardMinSize = new Size(1100, 680);

            ContentHost.Padding = new Padding(0);
            ContentHost.BackColor = Theme.Panel;

            BuildLayout();
            HookEvents();

            EnsurePageGeneral();
            SetNavSelected(0);
        }

        public void EnsureHostsLoaded()
        {
            EnsurePageGeneral();
            EnsurePageMadvr();
            EnsurePageLavVideo();
            EnsurePageLavAudio();
            EnsurePageMpcVr();
            EnsurePageMpcAr();
            EnsurePageSubtitles();

            // avvia madVR se siamo già nella pagina
            try
            {
                var host = FindChild<MadVrSettingsEmbedder>(_pgMadvr);
                host?.EnsureStarted();
            }
            catch { }
        }

        public void SyncFromState(int targetFps, bool upscale, bool preferBitstream)
        {
            EnsurePageGeneral();

            _fpsAuto.Checked = targetFps == 0;
            _fps60.Checked = targetFps == 60;
            _fps24.Checked = targetFps == 24 || targetFps == 23;

            _upscale.Checked = upscale;
            _preferBitstream.Checked = preferBitstream;
        }

        public void FocusApply()
        {
            try { _btnApply.Focus(); } catch { }
        }

        // Compat: se già la chiami senza parametri da PlayerForm
        public void OpenSubtitlesSection() => OpenSubtitlesSection(autoOpen: true);

        /// <summary>
        /// Apre la sezione SOTTOTITOLI e, se autoOpen=true, prova ad aprire subito la property page.
        /// </summary>
        public void OpenSubtitlesSection(bool autoOpen)
        {
            EnsurePageSubtitles();
            _subsAutoOpenPending = autoOpen;
            _subsAutoOpenShowNotFound = autoOpen;
            SetNavSelected(6);
            TryAutoOpenSubtitlesIfPending();
        }

        // ----------------- BUILD LAYOUT -----------------
        private void BuildLayout()
        {
            ContentHost.Controls.Clear();

            _root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270)); // sidebar come libreria
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            ContentHost.Controls.Add(_root);

            BuildLeftNav();
            BuildRightArea();

            TryLoadLogo();
        }

        private void BuildLeftNav()
        {
            _leftNav = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Nav,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            _root.Controls.Add(_leftNav, 0, 0);

            // Separatore a destra (come libreria)
            var sep = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Theme.Border };
            _leftNav.Controls.Add(sep);

            _leftLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Nav,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12, 12, 12, 12),
                Margin = new Padding(0)
            };
            _leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));  // logo
            _leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // menu
            _leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));  // footer bottoni
            _leftNav.Controls.Add(_leftLayout);

            // ---------- LOGO ----------
            var logoRow = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Nav,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            _leftLayout.Controls.Add(logoRow, 0, 0);

            _logoBox = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                Width = 34,
                Height = 34,
                Left = 0,
                Top = 6,
                BackColor = Color.Transparent
            };
            logoRow.Controls.Add(_logoBox);

            var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border };
            logoRow.Controls.Add(line);

            // ---------- NAV ----------
            _navFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Theme.Nav,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            _leftLayout.Controls.Add(_navFlow, 0, 1);

            // sezione header (stile libreria: testo piccolo)
            _navFlow.Controls.Add(MakeSectionHeader("IMPOSTAZIONI"));

            _navButtons.Clear();
            _navFlow.Controls.Add(new Panel { Height = 4, Width = 10, BackColor = Color.Transparent, Margin = new Padding(0) });

            for (int i = 0; i < _navItems.Length; i++)
            {
                var b = UiKit.MakeOutlineButton(_navItems[i], leftAlign: true, useNavBg: true);
                b.Tag = i;
                b.Dock = DockStyle.None;
                b.Margin = new Padding(0, 0, 0, 6);
                b.Height = 34;
                _navButtons.Add(b);
                _navFlow.Controls.Add(b);
            }

            _navFlow.Resize += (_, __) => FitNavButtons();

            // ---------- FOOTER ----------
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Nav,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _leftLayout.Controls.Add(footer, 0, 2);

            _btnClose = UiKit.MakeOutlineButton("Chiudi", leftAlign: false, useNavBg: true);
            _btnClose.Dock = DockStyle.Fill;
            _btnClose.Margin = new Padding(0, 0, 8, 0);
            _btnClose.Height = 34;

            _btnApply = UiKit.MakeOutlineButton("Applica", leftAlign: false, useNavBg: true);
            _btnApply.Dock = DockStyle.Fill;
            _btnApply.Margin = new Padding(8, 0, 0, 0);
            _btnApply.Height = 34;

            footer.Controls.Add(_btnClose, 0, 0);
            footer.Controls.Add(_btnApply, 1, 0);

            FitNavButtons();
        }

        private void BuildRightArea()
        {
            var rightOuter = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(12),
                Margin = new Padding(0)
            };
            _root.Controls.Add(rightOuter, 1, 0);

            _rightBorder = new OutlinePanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            rightOuter.Controls.Add(_rightBorder);

            // Top bar (stile libreria)
            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 54,
                BackColor = Theme.Panel,
                Padding = new Padding(14, 0, 14, 0)
            };
            _rightBorder.Controls.Add(topBar);

            var topGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Panel,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topBar.Controls.Add(topGrid);

            _lblSection = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 11.5f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            topGrid.Controls.Add(_lblSection, 0, 0);

            var lblRight = new Label
            {
                Text = "IMPOSTAZIONI",
                AutoSize = true,
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 10f),
                Anchor = AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0, 0, 0, 0)
            };
            topGrid.Controls.Add(lblRight, 1, 0);

            var topLine = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border };
            topBar.Controls.Add(topLine);

            // Host pagine
            _pageHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(16, 14, 16, 14),
                AutoScroll = false
            };
            _rightBorder.Controls.Add(_pageHost);
            _pageHost.BringToFront();
        }

        private static Label MakeSectionHeader(string text)
        {
            return new Label
            {
                Text = text,
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 9f),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8)
            };
        }

        private void FitNavButtons()
        {
            if (_navFlow == null) return;
            int w = Math.Max(140, _navFlow.ClientSize.Width - 4);

            foreach (var b in _navButtons)
            {
                int ww = w - b.Margin.Horizontal;
                if (ww < 120) ww = 120;
                b.Width = ww;
            }
        }

        private void HookEvents()
        {
            _btnApply.Click += (_, __) =>
            {
                ApplyAllPropertyPagesInCurrentPage();
                var vs = CollectVideoSettings();

                ApplyDetailed?.Invoke(vs);
                ApplyClicked?.Invoke(vs.TargetFps, vs.AllowUpscaling, vs.PreferBitstream);

                CloseModal();
            };

            _btnClose.Click += (_, __) =>
            {
                try { FindChild<MadVrSettingsEmbedder>(_pgMadvr)?.CloseSettingsWindow(); } catch { }
                CloseModal();
            };

            foreach (var b in _navButtons)
            {
                b.Click += (_, __) =>
                {
                    if (b.Tag is int idx) SetNavSelected(idx);
                };
            }
            ;
        }

        // ----------------- NAV -----------------
        private void SetNavSelected(int index)
        {
            _selNav = Math.Clamp(index, 0, _navItems.Length - 1);

            _lblSection.Text = _navItems[_selNav];

            foreach (var btn in _navButtons)
            {
                bool sel = (btn.Tag is int i && i == _selNav);
                btn.BackColor = sel ? Theme.PanelAlt : Theme.Nav;
            }

            // Se l'utente entra in SOTTOTITOLI dalla sidebar, prova ad auto-aprire in modo SILENZIOSO.
            if (_selNav == 6 && !_subsAutoOpenPending)
            {
                _subsAutoOpenPending = true;
                _subsAutoOpenShowNotFound = false;
            }

            ShowPage(_selNav);

            // auto-start madVR quando entri nella pagina
            if (_selNav == 1)
            {
                try { FindChild<MadVrSettingsEmbedder>(_pgMadvr)?.EnsureStarted(); } catch { }
            }

            TryAutoOpenSubtitlesIfPending();
        }

        private void HideAllPages()
        {
            foreach (Control c in _pageHost.Controls)
                c.Visible = false;
        }

        private void AddPage(Control pg, ref Control? field)
        {
            pg.Visible = false;
            pg.Dock = DockStyle.Fill;
            _pageHost.Controls.Add(pg);
            field = pg;
        }

        private void ShowPage(int index)
        {
            HideAllPages();

            Control? toShow = null;
            switch (index)
            {
                case 0: EnsurePageGeneral(); toShow = _pgGeneral; break;
                case 1: EnsurePageMadvr(); toShow = _pgMadvr; break;
                case 2: EnsurePageLavVideo(); toShow = _pgLavVideo; break;
                case 3: EnsurePageLavAudio(); toShow = _pgLavAudio; break;
                case 4: EnsurePageMpcVr(); toShow = _pgMpcVr; break;
                case 5: EnsurePageMpcAr(); toShow = _pgMpcAr; break;
                case 6: EnsurePageSubtitles(); toShow = _pgSubtitles; break;
            }

            if (toShow != null)
            {
                toShow.Visible = true;
                toShow.BringToFront();
            }
        }

        // ----------------- PAGES -----------------
        private void EnsurePageGeneral()
        {
            if (_pgGeneral != null) return;

            var page = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                AutoScroll = true,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            // Contenitore interno con padding e layout a 2 colonne (sx larga, dx stretta)
            var inner = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Panel,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            page.Controls.Add(inner);

            var grid2 = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Panel,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            grid2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62f));
            grid2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
            inner.Controls.Add(grid2);

            var leftCol = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                BackColor = Theme.Panel,
                Margin = new Padding(0, 0, 10, 0),
                Padding = new Padding(0)
            };
            leftCol.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid2.Controls.Add(leftCol, 0, 0);

            var rightCol = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                BackColor = Theme.Panel,
                Margin = new Padding(10, 0, 0, 0),
                Padding = new Padding(0)
            };
            rightCol.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid2.Controls.Add(rightCol, 1, 0);

            OutlinePanel MakeBox(string title, string subtitle, out Panel body)
            {
                var box = new OutlinePanel
                {
                    BackColor = Theme.PanelAlt,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(12, 10, 12, 10),
                    Margin = new Padding(0, 0, 0, 12),
                    Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
                };

                var header = UiKit.MakeGroupHeader(title);
                header.Location = new Point(0, 0);
                box.Controls.Add(header);

                var sub = UiKit.MakeGroupSub(subtitle);
                sub.Location = new Point(0, header.Bottom);
                box.Controls.Add(sub);

                body = new Panel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = Color.Transparent,
                    Location = new Point(0, sub.Bottom + 6)
                };
                box.Controls.Add(body);

                return box;
            }

            void AddBox(TableLayoutPanel col, Control box)
            {
                int row = col.RowCount;
                col.RowCount++;
                col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                col.Controls.Add(box, 0, row);
            }

            // --------- sinistra (video/madVR) ----------
            Panel bodyAlgo;
            var boxAlgo = MakeBox(
                "Algoritmi madVR",
                "Profili madVR per upscaling / downscaling / refinement.",
                out bodyAlgo);

            var gridAlgo = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 4,
                AutoSize = true,
                BackColor = Color.Transparent,
                Dock = DockStyle.Top,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            gridAlgo.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            gridAlgo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Label L(string t) => new Label
            {
                Text = t,
                ForeColor = Theme.SubtleText,
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Margin = new Padding(0, 4, 12, 4)
            };

            _cbChroma = UiKit.MakePresetCombo();
            _cbUp = UiKit.MakePresetCombo();
            _cbDown = UiKit.MakePresetCombo();
            _cbRefine = UiKit.MakePresetCombo();

            gridAlgo.Controls.Add(L("Chroma upscaling:"), 0, 0);
            gridAlgo.Controls.Add(_cbChroma, 1, 0);
            gridAlgo.Controls.Add(L("Image upscaling:"), 0, 1);
            gridAlgo.Controls.Add(_cbUp, 1, 1);
            gridAlgo.Controls.Add(L("Image downscaling:"), 0, 2);
            gridAlgo.Controls.Add(_cbDown, 1, 2);
            gridAlgo.Controls.Add(L("Upscaling refinement:"), 0, 3);
            gridAlgo.Controls.Add(_cbRefine, 1, 3);

            bodyAlgo.Controls.Add(gridAlgo);
            AddBox(leftCol, boxAlgo);

            Panel bodyHdr;
            var boxHdr = MakeBox("madVR — HDR", "Come gestire i contenuti HDR.", out bodyHdr);

            _hdrAuto = UiKit.MakeRadio("Auto");
            _hdrAuto.Checked = true;
            _hdrPass = UiKit.MakeRadio("Passthrough HDR al display");
            _hdrTone = UiKit.MakeRadio("Converti HDR → SDR (tone mapping)");
            _hdrLut = UiKit.MakeRadio("HDR → SDR usando LUT 3D esterna");

            int y = 0;
            foreach (var rb in new[] { _hdrAuto, _hdrPass, _hdrTone, _hdrLut })
            {
                rb.Location = new Point(0, y);
                bodyHdr.Controls.Add(rb);
                y = rb.Bottom + 2;
            }
            AddBox(leftCol, boxHdr);

            Panel bodyFps;
            var boxFps = MakeBox("Frequenza monitor", "Cambia refresh del display durante la riproduzione.", out bodyFps);

            _fpsAuto = UiKit.MakeRadio("Non cambiare (usa frequenza attuale)");
            _fpsAuto.Checked = true;
            _fps60 = UiKit.MakeRadio("59/60p (desktop / sport)");
            _fps24 = UiKit.MakeRadio("23/24p (film)");

            y = 0;
            foreach (var rb in new[] { _fpsAuto, _fps60, _fps24 })
            {
                rb.Location = new Point(0, y);
                bodyFps.Controls.Add(rb);
                y = rb.Bottom + 2;
            }
            AddBox(leftCol, boxFps);

            // --------- destra (audio / player) ----------
            Panel bodyAud;
            var boxAud = MakeBox("Audio", "Uscita audio e passthrough bitstream.", out bodyAud);

            _preferBitstream = UiKit.MakeCheck("Preferisci inviare bitstream (se supportato)");
            _preferBitstream.Location = new Point(0, 0);
            bodyAud.Controls.Add(_preferBitstream);
            AddBox(rightCol, boxAud);

            Panel bodyVid;
            var boxVid = MakeBox("Video (player)", "Opzioni interne del player quando NON sto usando madVR.", out bodyVid);

            _upscale = UiKit.MakeCheck("Abilita upscaling lato player");
            _upscale.Location = new Point(0, 0);
            bodyVid.Controls.Add(_upscale);
            AddBox(rightCol, boxVid);

            AddPage(page, ref _pgGeneral);
        }

        private void EnsurePageMadvr()
        {
            if (_pgMadvr != null) return;

            var wrap = new Panel
            {
                BackColor = Theme.Panel,
                Dock = DockStyle.Fill,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            var host = new MadVrSettingsEmbedder
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel
            };
            wrap.Controls.Add(host);

            wrap.VisibleChanged += (_, __) => { if (wrap.Visible) host.EnsureStarted(); };
            host.HandleCreated += (_, __) => { if (wrap.Visible) host.EnsureStarted(); };

            AddPage(wrap, ref _pgMadvr);
        }

        private void EnsurePageLavVideo()
        {
            if (_pgLavVideo != null) return;

            var host = new DsPropPageHost { Dock = DockStyle.Fill, BackColor = Theme.Panel };
            try
            {
                host.LoadFromFriendlyName("LAV Video Decoder");
            }
            catch (Exception ex)
            {
                host.Dispose();
                var fb = MakeFallbackPage("LAV Video Decoder non trovato.\r\nDettagli: " + ex.Message);
                AddPage(fb, ref _pgLavVideo);
                return;
            }

            var wrap = new Panel { BackColor = Theme.Panel, Dock = DockStyle.Fill, Padding = new Padding(0) };
            wrap.Controls.Add(host);
            AddPage(wrap, ref _pgLavVideo);
        }

        private void EnsurePageLavAudio()
        {
            if (_pgLavAudio != null) return;

            var host = new DsPropPageHost { Dock = DockStyle.Fill, BackColor = Theme.Panel };
            try
            {
                host.LoadFromFriendlyName("LAV Audio Decoder");
            }
            catch (Exception ex)
            {
                host.Dispose();
                var fb = MakeFallbackPage("LAV Audio Decoder non trovato.\r\nDettagli: " + ex.Message);
                AddPage(fb, ref _pgLavAudio);
                return;
            }

            var wrap = new Panel { BackColor = Theme.Panel, Dock = DockStyle.Fill, Padding = new Padding(0) };
            wrap.Controls.Add(host);
            AddPage(wrap, ref _pgLavAudio);
        }

        private void EnsurePageMpcVr()
        {
            if (_pgMpcVr != null) return;

            var host = new DsPropPageHost { Dock = DockStyle.Fill, BackColor = Theme.Panel };
            bool ok = false;

            try { host.LoadFromClsid(CLSID_MPCVR); ok = true; } catch { }
            if (!ok)
            {
                try { host.LoadFromFriendlyName("MPC Video Renderer"); ok = true; } catch { }
            }

            if (!ok)
            {
                host.Dispose();
                var fb = MakeFallbackPage("MPC Video Renderer non trovato.");
                AddPage(fb, ref _pgMpcVr);
                return;
            }

            var wrap = new Panel { BackColor = Theme.Panel, Dock = DockStyle.Fill, Padding = new Padding(0) };
            wrap.Controls.Add(host);
            AddPage(wrap, ref _pgMpcVr);
        }

        private void EnsurePageMpcAr()
        {
            if (_pgMpcAr != null) return;

            var host = new DsPropPageHost { Dock = DockStyle.Fill, BackColor = Theme.Panel };
            try
            {
                host.LoadFromFriendlyName("MPC Audio Renderer");
            }
            catch (Exception ex)
            {
                host.Dispose();
                var fb = MakeFallbackPage("MPC Audio Renderer non trovato.\r\nDettagli: " + ex.Message);
                AddPage(fb, ref _pgMpcAr);
                return;
            }

            var wrap = new Panel { BackColor = Theme.Panel, Dock = DockStyle.Fill, Padding = new Padding(0) };
            wrap.Controls.Add(host);
            AddPage(wrap, ref _pgMpcAr);
        }

        private void EnsurePageSubtitles()
        {
            if (_pgSubtitles != null) return;

            var page = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                AutoScroll = true,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            var inner = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Panel,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            page.Controls.Add(inner);

            var box = new OutlinePanel
            {
                BackColor = Theme.PanelAlt,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 10, 12, 10),
                Margin = new Padding(0, 0, 0, 12),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            inner.Controls.Add(box);

            var hdr = UiKit.MakeGroupHeader("Sottotitoli");
            hdr.Location = new Point(0, 0);
            box.Controls.Add(hdr);

            var sub = UiKit.MakeGroupSub("Apri le impostazioni dei filtri sottotitoli installati (XySubFilter / xy-VSFilter).");
            sub.Location = new Point(0, hdr.Bottom);
            box.Controls.Add(sub);

            int y = sub.Bottom + 10;

            var btnXySub = UiKit.MakeOutlineButton("Impostazioni XySubFilter (madVR)…", leftAlign: false, useNavBg: false);
            btnXySub.Location = new Point(0, y);
            btnXySub.Width = 420;
            btnXySub.Height = 34;
            btnXySub.Click += (_, __) =>
                OpenFilterPropertyPages("XySubFilter", new[] { "XySubFilter" });
            box.Controls.Add(btnXySub);

            y = btnXySub.Bottom + 8;

            var btnVs = UiKit.MakeOutlineButton("Impostazioni xy-VSFilter / VSFilter (EVR)…", leftAlign: false, useNavBg: false);
            btnVs.Location = new Point(0, y);
            btnVs.Width = 420;
            btnVs.Height = 34;
            btnVs.Click += (_, __) =>
                OpenFilterPropertyPages("xy-VSFilter / VSFilter", new[] { "xy-VSFilter", "XyVSFilter", "VSFilter", "DirectVobSub" });
            box.Controls.Add(btnVs);

            y = btnVs.Bottom + 10;

            var note = new Label
            {
                Text =
                    "Nota:\r\n" +
                    "• madVR usa XySubFilter (subtitle consumer).\r\n" +
                    "• EVR standard di solito richiede xy-VSFilter/VSFilter (transform) per vedere i sottotitoli.\r\n" +
                    "• Se un filtro non si apre o non è trovato: controlla x86/x64 e runtime VC++.",
                ForeColor = Theme.SubtleText,
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                MaximumSize = new Size(900, 0),
                Location = new Point(0, y),
                Margin = new Padding(0)
            };
            box.Controls.Add(note);

            // Quando la pagina diventa visibile, prova ad auto-aprire (se pending)
            page.VisibleChanged += (_, __) =>
            {
                if (page.Visible) TryAutoOpenSubtitlesIfPending();
            };

            AddPage(page, ref _pgSubtitles);
        }

        private Control MakeFallbackPage(string msg)
        {
            var panel = new Panel
            {
                BackColor = Theme.Panel,
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                AutoScroll = true
            };
            var lbl = new Label
            {
                Text = msg,
                ForeColor = Theme.Danger,
                Font = new Font("Segoe UI", 9f),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            panel.Controls.Add(lbl);
            return panel;
        }

        // ----------------- APPLY -----------------
        private void ApplyAllPropertyPagesInCurrentPage()
        {
            var current = GetCurrentPage();
            if (current == null) return;

            void Walk(Control c)
            {
                if (c is DsPropPageHost host)
                {
                    try { host.Apply(); } catch { }
                }
                foreach (Control child in c.Controls) Walk(child);
            }
            Walk(current);
        }

        private Control? GetCurrentPage()
        {
            foreach (Control c in _pageHost.Controls)
                if (c.Visible) return c;
            return null;
        }

        private VideoSettings CollectVideoSettings()
        {
            var vs = new VideoSettings();

            if (_fpsAuto.Checked) { vs.TargetFps = 0; vs.FpsChoice = MadVrFpsChoice.Adapt; }
            else if (_fps60.Checked) { vs.TargetFps = 60; vs.FpsChoice = MadVrFpsChoice.Force60; }
            else { vs.TargetFps = 24; vs.FpsChoice = MadVrFpsChoice.Force24; }

            vs.AllowUpscaling = _upscale.Checked;
            vs.PreferBitstream = _preferBitstream.Checked;

            if (_hdrPass.Checked) vs.HdrMode = MadVrHdrMode.PassthroughHdr;
            else if (_hdrTone.Checked) vs.HdrMode = MadVrHdrMode.ToneMapHdrToSdr;
            else if (_hdrLut.Checked) vs.HdrMode = MadVrHdrMode.LutHdrToSdr;
            else vs.HdrMode = MadVrHdrMode.Auto;

            vs.ChromaPreset = UiKit.ComboToPreset(_cbChroma);
            vs.ImageUpscalePreset = UiKit.ComboToPreset(_cbUp);
            vs.ImageDownscalePreset = UiKit.ComboToPreset(_cbDown);
            vs.RefinementPreset = UiKit.ComboToPreset(_cbRefine);

            return vs;
        }

        // ----------------- LOGO -----------------
        private void TryLoadLogo()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                // stessi candidati che tipicamente usi nella libreria
                string[] candidates =
                {
                    Path.Combine(baseDir, "assets", "logo.png"),
                    Path.Combine(baseDir, "assets", "logo_orizzontale.png"),
                    Path.Combine(baseDir, "Assets", "logo.png"),
                    Path.Combine(baseDir, "logo.png"),
                };

                foreach (var p in candidates)
                {
                    if (!File.Exists(p)) continue;
                    using var tmp = Image.FromFile(p);
                    _logoBox.Image = new Bitmap(tmp);
                    break;
                }
            }
            catch { }
        }

        // FIX: ppUnk va marshaled come LPArray di IUnknown, altrimenti crasha con "Cannot marshal parameter #6"
        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int OleCreatePropertyFrame(
            IntPtr hwndOwner,
            int x, int y,
            string lpszCaption,
            int cObjects,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.IUnknown)]
            object[] ppUnk,
            int cPages,
            IntPtr pPageClsID,
            int lcid,
            int dwReserved,
            IntPtr pvReserved);

        private void OpenFilterPropertyPages(string caption, string[] candidates)
        {
            TryOpenFilterPropertyPages(caption, candidates, silentIfNotFound: false);
        }

        private bool TryOpenFilterPropertyPages(string caption, string[] candidates, bool silentIfNotFound)
        {
            IBaseFilter? f = null;
            string chosen = candidates.FirstOrDefault() ?? "Filtro";

            foreach (var name in candidates)
            {
                f = CreateFilterByName(name);
                if (f != null) { chosen = name; break; }
            }

            if (f == null)
            {
                if (!silentIfNotFound)
                {
                    MessageBox.Show(
                        $"Filtro non trovato: {string.Join(", ", candidates)}",
                        "Sottotitoli",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return false;
            }

            try
            {
                if (f is DirectShowLib.ISpecifyPropertyPages spp)
                {
                    spp.GetPages(out DirectShowLib.DsCAUUID ca);

                    try
                    {
                        int hr = OleCreatePropertyFrame(
                            this.Handle,
                            0, 0,
                            $"{caption} — {chosen}",
                            1,
                            new object[] { f },
                            ca.cElems,
                            ca.pElems,
                            0, 0, IntPtr.Zero);

                        if (hr != 0)
                            Marshal.ThrowExceptionForHR(hr);

                        return true;
                    }
                    finally
                    {
                        if (ca.pElems != IntPtr.Zero)
                            Marshal.FreeCoTaskMem(ca.pElems);
                    }
                }

                if (!silentIfNotFound)
                {
                    MessageBox.Show(
                        "Il filtro non espone property pages.",
                        "Sottotitoli",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return false;
            }
            catch (Exception ex)
            {
                if (!silentIfNotFound)
                {
                    MessageBox.Show(
                        "Impossibile aprire le impostazioni.\r\nDettagli: " + ex.Message,
                        "Sottotitoli",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return false;
            }
            finally
            {
                try { if (Marshal.IsComObject(f)) Marshal.ReleaseComObject(f); } catch { }
            }
        }

        private void TryAutoOpenSubtitlesIfPending()
        {
            if (!_subsAutoOpenPending) return;
            if (_selNav != 6) return;
            if (_pgSubtitles == null || !_pgSubtitles.Visible) return;
            if (!IsHandleCreated) return;

            bool showNotFound = _subsAutoOpenShowNotFound;

            _subsAutoOpenPending = false;
            _subsAutoOpenShowNotFound = false;

            // 1) prova XySubFilter
            if (TryOpenFilterPropertyPages("XySubFilter", new[] { "XySubFilter" }, silentIfNotFound: true))
                return;

            // 2) prova i transform per EVR
            if (TryOpenFilterPropertyPages("xy-VSFilter / VSFilter", new[] { "xy-VSFilter", "XyVSFilter", "VSFilter", "DirectVobSub" }, silentIfNotFound: true))
                return;

            if (showNotFound)
            {
                MessageBox.Show(
                    "Nessun filtro sottotitoli configurabile trovato.\r\n\r\n" +
                    "Cercati: XySubFilter, xy-VSFilter, VSFilter, DirectVobSub.\r\n" +
                    "Controlla anche x86/x64 e i runtime VC++.",
                    "Sottotitoli",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private static IBaseFilter? CreateFilterByName(string? friendlyName)
        {
            if (string.IsNullOrWhiteSpace(friendlyName)) return null;

            Guid[] cats =
            {
                FilterCategory.LegacyAmFilterCategory,
                FilterCategory.AudioRendererCategory,
                FilterCategory.VideoCompressorCategory,
                FilterCategory.AudioCompressorCategory,
                FilterCategory.VideoInputDevice,
                FilterCategory.AudioInputDevice
            };

            foreach (var cat in cats)
            {
                foreach (var d in DsDevice.GetDevicesOfCat(cat))
                {
                    if (d.Name.Equals(friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        d.Name.StartsWith(friendlyName, StringComparison.OrdinalIgnoreCase))
                    {
                        var iid = typeof(IBaseFilter).GUID;
                        d.Mon.BindToObject(null, null, ref iid, out object obj);
                        return (IBaseFilter)obj;
                    }
                }
            }
            return null;
        }

        private static T? FindChild<T>(Control? root) where T : Control
        {
            if (root == null) return null;

            if (root is T hit) return hit;

            foreach (Control c in root.Controls)
            {
                var deep = FindChild<T>(c);
                if (deep != null) return deep;
            }
            return null;
        }
    }

    // ===================== CREDITS MODAL (stesso stile, card più piccola) =====================
    internal sealed class CreditsModal : ModalBase
    {
        private FlowLayoutPanel _stack = null!;
        private Button _btnClose = null!;

        public CreditsModal() : base("")
        {
            OverlayColor = Theme.BackdropDim;
            CloseOnBackdropClick = true;
            CloseOnEscape = true;

            RemoveFromParentOnClose = false;
            AutoDisposeOnClose = false;

            HeaderVisible = false;
            FooterVisible = false;

            // più piccolo: non “quasi full-screen”
            CardMinSize = new Size(640, 420);
            CardMaxSize = new Size(900, 580);

            ContentHost.Padding = new Padding(0);
            ContentHost.BackColor = Theme.Panel;

            BuildLayout();
            BuildContent();
        }

        private void BuildLayout()
        {
            ContentHost.Controls.Clear();

            var outline = new OutlinePanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(0)
            };
            ContentHost.Controls.Add(outline);

            // Top bar (stile libreria)
            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 54,
                BackColor = Theme.Panel,
                Padding = new Padding(14, 0, 14, 0)
            };
            outline.Controls.Add(topBar);

            var topGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Panel,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topBar.Controls.Add(topGrid);

            var left = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 11.5f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            topGrid.Controls.Add(left, 0, 0);

            var titleRight = new Label
            {
                Text = "CREDITI",
                AutoSize = true,
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 10f),
                Anchor = AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleRight
            };
            topGrid.Controls.Add(titleRight, 1, 0);

            topBar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border });

            // Bottom bar con CHIUDI
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 58,
                BackColor = Theme.Panel,
                Padding = new Padding(14, 12, 14, 12)
            };
            outline.Controls.Add(bottom);
            bottom.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border });

            _btnClose = UiKit.MakeOutlineButton("Chiudi", leftAlign: false, useNavBg: false);
            _btnClose.Dock = DockStyle.Right;
            _btnClose.Width = 110;
            _btnClose.Height = 34;
            _btnClose.Margin = new Padding(0);
            bottom.Controls.Add(_btnClose);

            _btnClose.Click += (_, __) => CloseModal();

            // Scroll content
            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(16, 14, 16, 14),
                AutoScroll = true
            };
            outline.Controls.Add(scroll);
            scroll.BringToFront();

            _stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                BackColor = Theme.Panel,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            scroll.Controls.Add(_stack);
        }

        private void BuildContent()
        {
            _stack.SuspendLayout();
            _stack.Controls.Clear();

            void AddCard(string title, string subtitle, string[] bodyLines, (string text, string url)[]? links = null)
            {
                var box = new OutlinePanel
                {
                    BackColor = Theme.PanelAlt,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(12, 10, 12, 10),
                    Margin = new Padding(0, 0, 0, 12)
                };

                var hdr = UiKit.MakeGroupHeader(title);
                hdr.Location = new Point(0, 0);
                box.Controls.Add(hdr);

                if (!string.IsNullOrWhiteSpace(subtitle))
                {
                    var sub = UiKit.MakeGroupSub(subtitle);
                    sub.Location = new Point(0, hdr.Bottom);
                    box.Controls.Add(sub);
                }

                int curY = hdr.Bottom + 8;

                foreach (var line in bodyLines)
                {
                    var lbl = new Label
                    {
                        Text = line,
                        ForeColor = Theme.SubtleText,
                        Font = new Font("Segoe UI", 9f),
                        AutoSize = true,
                        Margin = new Padding(0),
                        Location = new Point(0, curY),
                        MaximumSize = new Size(1000, 0)
                    };
                    box.Controls.Add(lbl);
                    curY = lbl.Bottom + 4;
                }

                if (links != null)
                {
                    foreach (var (text, url) in links)
                    {
                        var lnk = new LinkLabel
                        {
                            Text = text,
                            Font = new Font("Segoe UI", 9f),
                            AutoSize = true,
                            LinkColor = Theme.Accent,
                            ActiveLinkColor = Theme.Accent,
                            Location = new Point(0, curY),
                            Margin = new Padding(0),
                        };
                        lnk.Links.Add(0, text.Length, url);
                        lnk.LinkClicked += (_, e) =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = e.Link.LinkData?.ToString(),
                                    UseShellExecute = true
                                });
                            }
                            catch { }
                        };
                        box.Controls.Add(lnk);
                        curY = lnk.Bottom + 4;
                    }
                }

                _stack.Controls.Add(box);
            }

            AddCard(
                "Cinecore Player 2025",
                "Progetto non commerciale",
                new[]
                {
                    "© 2025 — Niccolò Landolfi.",
                    "Video: DirectShow + LAV + madVR / MPCVR / EVR",
                    "Audio: LAV + MPC Audio Renderer / renderer di sistema"
                }
            );

            AddCard(
                "Componenti principali",
                "",
                new[]
                {
                    "• LAV Filters",
                    "• madVR (video renderer)",
                    "• MPC Video Renderer / MPC Audio Renderer",
                    "• DirectShowLib, FFmpeg.AutoGen"
                }
            );

            AddCard(
                "Ringraziamenti",
                "",
                new[]
                {
                    "• LAV Filters, MPC-HC team",
                    "• madshi (madVR)"
                }
            );

            AddCard(
                "Link utili",
                "",
                Array.Empty<string>(),
                new (string text, string url)[]
                {
                    ("Sito Cinecore", "https://cinecore.it"),
                    ("Repository (se pubblico)", "https://github.com/")
                }
            );

            _stack.ResumeLayout();
        }
    }
}
