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
        // ------------ OVERLAY GESTIONE CARTELLE (sopra al pannello destro) ------------
        private Panel? _inlineRootsCallToActionHost;
        private PictureBox? _inlineRootsCallToActionPreview;
        private Label? _inlineRootsCallToActionTitle;
        private Label? _inlineRootsCallToActionSubtitle;
        private FlatButton? _inlineRootsCallToActionButton;

        private Panel? _seasonEpisodeOverlay;
        private Label? _seasonEpisodeOverlayTitle;
        private Label? _seasonEpisodeOverlaySubtitle;
        private EpisodePickerListBox? _seasonEpisodeOverlayList;
        private FlatButton? _seasonEpisodeOverlayOpenButton;
        private FlatButton? _seasonEpisodeOverlayCloseButton;
        private TvSeasonGroup? _seasonEpisodeOverlayGroup;
        private CancellationTokenSource? _seasonEpisodeOverlayTitleCts;
        private Panel? _playlistEditorOverlay;
        private Label? _playlistEditorOverlayTitle;
        private Label? _playlistEditorOverlaySubtitle;
        private TextBox? _playlistEditorNameBox;
        private FlatButton? _playlistEditorCreateButton;
        private Action<string>? _playlistEditorCommitAction;
        private string _playlistEditorBucketKey = string.Empty;
        private bool _seriesSectionFirst = false;

        private sealed class EpisodePickerListBox : ListBox
        {
            private int _lastKnownTopIndex;

            public EpisodePickerListBox()
            {
                DrawMode = DrawMode.OwnerDrawFixed;
                BorderStyle = BorderStyle.FixedSingle;
                IntegralHeight = false;
                ItemHeight = 30;
                BackColor = Theme.Panel;
                ForeColor = Theme.Text;
                Font = new Font("Segoe UI", 9.5f);
                SelectionMode = SelectionMode.One;
                TabStop = true;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                HideBars();
                BeginHideBars();
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                HideBars();
                BeginHideBars();
            }

            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);
                HideBars();
                BeginHideBars();
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (Visible)
                {
                    HideBars();
                    BeginHideBars();
                }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                int before = SafeGetTopIndex();
                base.OnMouseWheel(e);
                int after = SafeGetTopIndex();

                if (after == before && Items.Count > 0)
                {
                    int lines = SystemInformation.MouseWheelScrollLines;
                    if (lines <= 0)
                        lines = 3;

                    int direction = e.Delta < 0 ? 1 : -1;
                    int target = Math.Max(0, Math.Min(Math.Max(0, Items.Count - 1), before + (direction * Math.Max(1, lines))));
                    try { TopIndex = target; } catch { }
                }

                _lastKnownTopIndex = SafeGetTopIndex();
                HideBars();
                BeginHideBars();
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);

                const int WM_VSCROLL = 0x0115;
                const int WM_HSCROLL = 0x0114;
                const int WM_MOUSEWHEEL = 0x020A;
                const int WM_MOUSEHWHEEL = 0x020E;
                const int WM_PAINT = 0x000F;
                const int WM_NCPAINT = 0x0085;
                const int WM_WINDOWPOSCHANGED = 0x0047;

                if (m.Msg == WM_VSCROLL || m.Msg == WM_HSCROLL ||
                    m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL ||
                    m.Msg == WM_PAINT || m.Msg == WM_NCPAINT ||
                    m.Msg == WM_WINDOWPOSCHANGED)
                {
                    HideBars();
                    BeginHideBars();
                }
            }

            private int SafeGetTopIndex()
            {
                try { return TopIndex; }
                catch { return _lastKnownTopIndex; }
            }

            private void BeginHideBars()
            {
                if (!IsHandleCreated)
                    return;

                try { BeginInvoke(new Action(HideBars)); } catch { }
            }

            private void HideBars()
            {
                if (!IsHandleCreated)
                    return;

                try { Win32.ShowScrollBar(Handle, Win32.SB_VERT, false); } catch { }
                try { Win32.ShowScrollBar(Handle, Win32.SB_HORZ, false); } catch { }
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                e.DrawBackground();
                if (e.Index < 0 || e.Index >= Items.Count)
                    return;

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

                Color fill = selected
                    ? Color.FromArgb(62, 92, 156)
                    : (e.Index % 2 == 0 ? Color.FromArgb(24, 24, 30) : Color.FromArgb(28, 28, 34));

                using (var br = new SolidBrush(fill))
                    g.FillRectangle(br, e.Bounds);

                var textRect = new Rectangle(e.Bounds.Left + 12, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 24), e.Bounds.Height);
                TextRenderer.DrawText(
                    g,
                    Items[e.Index]?.ToString() ?? string.Empty,
                    Font,
                    textRect,
                    Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                if (selected)
                {
                    using var pen = new Pen(Color.FromArgb(190, Theme.Accent));
                    g.DrawRectangle(pen, e.Bounds.Left, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 1), Math.Max(1, e.Bounds.Height - 1));
                }
            }
        }

        private void BuildRootsOverlay()
        {
            _rootsOverlay = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(184, 8, 8, 12),
                Visible = false
            };

            var content = new Panel
            {
                BackColor = Theme.PanelAlt,
                Size = new Size(720, 420),
                Padding = new Padding(20),
                BorderStyle = BorderStyle.FixedSingle
            };

            void CenterContent()
            {
                if (_rootsOverlay == null) return;
                if (_rootsOverlay.ClientSize.Width <= 0 || _rootsOverlay.ClientSize.Height <= 0)
                    return;

                content.Left = (_rootsOverlay.ClientSize.Width - content.Width) / 2;
                content.Top = (_rootsOverlay.ClientSize.Height - content.Height) / 2;
            }

            _rootsOverlay.Resize += (_, __) => CenterContent();
            _rootsOverlay.Controls.Add(content);

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 72,
                BackColor = Theme.PanelAlt,
                Padding = new Padding(0, 0, 0, 6)
            };

            var lblTitle = new Label
            {
                Text = "Librerie della categoria corrente",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 12f),
                Padding = new Padding(4, 0, 4, 0)
            };
            header.Controls.Add(lblTitle);

            var lblSub = new Label
            {
                Text = "Aggiungi o rimuovi cartelle da questa libreria. Rimuovere una cartella non cancella i file.",
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Theme.SubtleText,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.25f),
                Padding = new Padding(4, 4, 4, 0)
            };
            header.Controls.Add(lblSub);

            _rootsOverlayList = new BetterFlow
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.Panel,
                HideHScroll = true,
                HideVScroll = true,
                Padding = new Padding(0, 2, 18, 2)
            };
            _rootsOverlayList.Resize += (_, __) =>
            {
                try
                {
                    if (_rootsOverlay != null && _rootsOverlay.Visible)
                        RefreshRootsOverlayList();
                }
                catch { }
            };

            var bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = Theme.PanelAlt,
                Padding = new Padding(16, 12, 16, 12)
            };

            var lblHint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Le modifiche vengono applicate alla chiusura.",
                ForeColor = Theme.SubtleText,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9f)
            };

            var btnClose = new FlatButton("Chiudi", FlatButton.Variant.Secondary)
            {
                Width = 110,
                Height = 32,
                Dock = DockStyle.Right,
                Margin = new Padding(0)
            };
            btnClose.Click += (_, __) => CloseRootsOverlay(commit: true, refreshAfterCommit: true);

            bottomBar.Controls.Add(btnClose);
            bottomBar.Controls.Add(lblHint);

            var listHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            var listMask = new Panel
            {
                Width = 22,
                BackColor = Theme.Panel,
                Enabled = false,
                TabStop = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right
            };

            void LayoutRootsListMask()
            {
                try
                {
                    listMask.Bounds = new Rectangle(Math.Max(0, listHost.ClientSize.Width - listMask.Width), 0, listMask.Width, Math.Max(0, listHost.ClientSize.Height));
                    listMask.BringToFront();
                }
                catch { }
            }

            listHost.Resize += (_, __) => LayoutRootsListMask();
            listHost.Controls.Add(_rootsOverlayList);
            listHost.Controls.Add(listMask);
            LayoutRootsListMask();

            content.Controls.Add(listHost);
            content.Controls.Add(bottomBar);
            content.Controls.Add(header);

            CenterContent();
        }

        private void EnsureRootsOverlayEditSession(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                category = _selCat;

            if (string.Equals(_rootsOverlayDraftCategory, category, StringComparison.OrdinalIgnoreCase) &&
                _rootsOverlayWorkingRoots != null)
                return;

            _rootsOverlayDraftCategory = category;
            _rootsOverlayWorkingRoots = _roots.Get(category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _rootsOverlayHasPendingChanges = false;
        }

        private void ResetRootsOverlayEditSession()
        {
            _rootsOverlayDraftCategory = string.Empty;
            _rootsOverlayWorkingRoots = new List<string>();
            _rootsOverlayHasPendingChanges = false;
        }

        private void AddRootToRootsOverlayDraft(string category, string folder)
        {
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(folder))
                return;

            EnsureRootsOverlayEditSession(category);

            if (_rootsOverlayWorkingRoots.Any(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase)))
                return;

            _rootsOverlayWorkingRoots.Add(folder);
            _rootsOverlayWorkingRoots = _rootsOverlayWorkingRoots
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _rootsOverlayHasPendingChanges = true;
        }

        private List<string> GetRootsOverlayEffectiveList()
        {
            if (!string.IsNullOrWhiteSpace(_rootsOverlayDraftCategory) &&
                string.Equals(_rootsOverlayDraftCategory, _selCat, StringComparison.OrdinalIgnoreCase))
            {
                return _rootsOverlayWorkingRoots
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return _roots.Get(_selCat)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void CommitRootsOverlayChanges(bool refreshAfterCommit)
        {
            string category = !string.IsNullOrWhiteSpace(_rootsOverlayDraftCategory) ? _rootsOverlayDraftCategory : _selCat;
            var desired = _rootsOverlayWorkingRoots
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var current = _roots.Get(category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool changed = _rootsOverlayHasPendingChanges ||
                           desired.Count != current.Count ||
                           desired.Except(current, StringComparer.OrdinalIgnoreCase).Any() ||
                           current.Except(desired, StringComparer.OrdinalIgnoreCase).Any();

            if (changed)
            {
                foreach (var existing in current)
                {
                    if (!desired.Contains(existing, StringComparer.OrdinalIgnoreCase))
                        _roots.Remove(category, existing);
                }

                foreach (var folder in desired)
                {
                    if (!current.Contains(folder, StringComparer.OrdinalIgnoreCase))
                        _roots.Add(category, folder);
                }

                _libraryIndex.ReplacePaths(category, Array.Empty<string>());
            }

            ResetRootsOverlayEditSession();

            if (changed)
            {
                try { ReconfigureLibraryWatchers(); } catch { }
            }

            if (changed && refreshAfterCommit && string.Equals(category, _selCat, StringComparison.OrdinalIgnoreCase))
                ForceRescanCurrentCategory();
        }

        private void CloseRootsOverlay(bool commit, bool refreshAfterCommit)
        {
            try
            {
                if (_rootsOverlay != null)
                    _rootsOverlay.Visible = false;
            }
            catch { }

            if (commit)
                CommitRootsOverlayChanges(refreshAfterCommit);
            else
                ResetRootsOverlayEditSession();
        }

        private void ShowRootsOverlay()
        {
            EnsureRootsOverlayEditSession(_selCat);
            RefreshRootsOverlayList();
            _rootsOverlay.Visible = true;
            _rootsOverlay.BringToFront();
        }

        private void RefreshRootsOverlayList()
        {
            if (_rootsOverlayList == null) return;

            _rootsOverlayList.SuspendLayout();
            _rootsOverlayList.Controls.Clear();

            var rootsForCat = GetRootsOverlayEffectiveList();
            int width = _rootsOverlayList.ClientSize.Width;
            if (width <= 0) width = 680;

            var addRow = new Panel
            {
                Height = 56,
                Width = width - 24,
                BackColor = Theme.Card,
                Margin = new Padding(8, 6, 8, 4),
                Padding = new Padding(12, 12, 12, 12)
            };

            var addLabel = new Label
            {
                Text = "Aggiungi cartelle o interi dischi a questa libreria.",
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9.25f)
            };

            var btnAdd = new FlatButton("Aggiungi cartella", FlatButton.Variant.Primary)
            {
                Width = 150,
                Height = 28,
                Dock = DockStyle.Right
            };
            btnAdd.Click += (_, __) =>
            {
                try { _ = AddFolderForCurrentCategory(refreshAfterAdd: false); } catch { }
            };

            addRow.Controls.Add(btnAdd);
            addRow.Controls.Add(addLabel);
            _rootsOverlayList.Controls.Add(addRow);

            if (rootsForCat.Count == 0)
            {
                var emptyRow = new Panel
                {
                    Height = 64,
                    Width = width - 24,
                    BackColor = Theme.Card,
                    Margin = new Padding(8, 8, 8, 0),
                    Padding = new Padding(14, 10, 14, 10)
                };

                var empty = new Label
                {
                    Text = "Nessuna cartella configurata per questa categoria.",
                    Dock = DockStyle.Fill,
                    ForeColor = Theme.SubtleText,
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 9.5f)
                };
                emptyRow.Controls.Add(empty);
                _rootsOverlayList.Controls.Add(emptyRow);
                _rootsOverlayList.ResumeLayout();
                try { (_rootsOverlayList as BetterFlow)?.ForceHideScrollbars(); } catch { }
                return;
            }

            foreach (var folder in rootsForCat)
            {
                var row = new Panel
                {
                    Height = 46,
                    Width = width - 24,
                    BackColor = Theme.Card,
                    Margin = new Padding(8, 6, 8, 0),
                    Padding = new Padding(10, 10, 10, 10),
                    TabStop = false
                };

                var lbl = new Label
                {
                    Text = folder,
                    Dock = DockStyle.Fill,
                    AutoEllipsis = true,
                    ForeColor = Theme.Text,
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Segoe UI", 9.25f),
                    TabStop = false
                };

                var actionHost = new Panel
                {
                    Dock = DockStyle.Right,
                    Width = 108,
                    BackColor = Color.Transparent,
                    TabStop = false
                };

                var btnDelete = new FlatButton("Rimuovi", FlatButton.Variant.Secondary)
                {
                    Width = 100,
                    Height = 24,
                    Visible = true,
                    Left = 8,
                    Top = 0,
                    TabStop = true,
                    TabIndex = 0
                };

                btnDelete.Click += (_, __) =>
                {
                    EnsureRootsOverlayEditSession(_selCat);
                    int removed = _rootsOverlayWorkingRoots.RemoveAll(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase));
                    if (removed > 0)
                        _rootsOverlayHasPendingChanges = true;
                    RefreshRootsOverlayList();
                };

                void SetRowHot(bool hot)
                {
                    row.BackColor = hot ? Theme.PanelAlt : Theme.Card;
                }

                foreach (var ctrl in new Control[] { row, lbl, actionHost, btnDelete })
                {
                    ctrl.MouseEnter += (_, __) => SetRowHot(true);
                    ctrl.MouseLeave += (_, __) =>
                    {
                        try
                        {
                            var client = row.PointToClient(Control.MousePosition);
                            SetRowHot(row.ClientRectangle.Contains(client));
                        }
                        catch
                        {
                            SetRowHot(false);
                        }
                    };
                }

                actionHost.Controls.Add(btnDelete);
                row.Controls.Add(actionHost);
                row.Controls.Add(lbl);
                _rootsOverlayList.Controls.Add(row);
            }

            _rootsOverlayList.ResumeLayout();
            try { (_rootsOverlayList as BetterFlow)?.ForceHideScrollbars(); } catch { }
        }

        private void EnsureInlineRootsCallToAction()
        {
            if (_inlineRootsCallToActionHost != null)
                return;

            _inlineRootsCallToActionHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Visible = false
            };

            var content = new Panel
            {
                Size = new Size(940, 620),
                BackColor = Color.Transparent
            };

            void CenterContent()
            {
                if (_inlineRootsCallToActionHost == null) return;
                if (_inlineRootsCallToActionHost.ClientSize.Width <= 0 || _inlineRootsCallToActionHost.ClientSize.Height <= 0)
                    return;

                content.Left = (_inlineRootsCallToActionHost.ClientSize.Width - content.Width) / 2;
                content.Top = (_inlineRootsCallToActionHost.ClientSize.Height - content.Height) / 2;
            }

            _inlineRootsCallToActionHost.Resize += (_, __) => CenterContent();
            _inlineRootsCallToActionHost.Controls.Add(content);

            _inlineRootsCallToActionPreview = new PictureBox
            {
                Size = new Size(800, 450),
                Location = new Point(70, 0),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            _inlineRootsCallToActionTitle = new Label
            {
                Left = 70,
                Top = 468,
                Width = 800,
                Height = 40,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 20f)
            };

            _inlineRootsCallToActionSubtitle = new Label
            {
                Left = 70,
                Top = 514,
                Width = 800,
                Height = 56,
                ForeColor = Theme.SubtleText,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 10.5f)
            };

            _inlineRootsCallToActionButton = new FlatButton("Aggiungi cartella…", FlatButton.Variant.Primary)
            {
                Width = 196,
                Height = 36,
                Left = 70,
                Top = 580,
                TabStop = true
            };
            _inlineRootsCallToActionButton.Click += (_, __) => ManageFoldersForCurrentCategory();

            content.Controls.Add(_inlineRootsCallToActionPreview);
            content.Controls.Add(_inlineRootsCallToActionTitle);
            content.Controls.Add(_inlineRootsCallToActionSubtitle);
            content.Controls.Add(_inlineRootsCallToActionButton);

            _right.Controls.Add(_inlineRootsCallToActionHost);
            CenterContent();
        }

        private void ShowInlineRootsCallToAction(string category)
        {
            EnsureInlineRootsCallToAction();
            if (_inlineRootsCallToActionHost == null) return;

            category = string.IsNullOrWhiteSpace(category) ? _selCat : category;
            string friendly = string.Equals(category, "Film", StringComparison.OrdinalIgnoreCase)
                ? "Film e Serie TV"
                : category;

            if (_inlineRootsCallToActionTitle != null)
                _inlineRootsCallToActionTitle.Text = $"Configura la libreria {friendly}.";

            if (_inlineRootsCallToActionSubtitle != null)
                _inlineRootsCallToActionSubtitle.Text = "Scegli le cartelle che vuoi usare per questa sezione. Le immagini placeholder possono essere personalizzate dagli asset della libreria.";

            if (_inlineRootsCallToActionPreview != null)
            {
                try
                {
                    var old = _inlineRootsCallToActionPreview.Image;
                    _inlineRootsCallToActionPreview.Image = BuildInlineRootsPreview(category, _inlineRootsCallToActionPreview.Width, _inlineRootsCallToActionPreview.Height);
                    try { old?.Dispose(); } catch { }
                }
                catch { }
            }

            _inlineRootsCallToActionHost.Visible = true;
            _inlineRootsCallToActionHost.BringToFront();
        }

        private void HideInlineRootsCallToAction()
        {
            try
            {
                if (_inlineRootsCallToActionHost != null)
                    _inlineRootsCallToActionHost.Visible = false;
            }
            catch { }
        }

        private static string NormalizeCategoryPreviewKey(string category)
        {
            category = (category ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(category))
                return "media";

            return category.ToLowerInvariant() switch
            {
                "film" => "film",
                "film e serie tv" => "film",
                "video" => "video",
                "foto" => "foto",
                "musica" => "musica",
                "playlist" => "playlist",
                "preferiti" => "preferiti",
                _ => Regex.Replace(category.ToLowerInvariant(), @"[^a-z0-9]+", string.Empty)
            };
        }


        private static string NormalizeCollectionArtworkKey(string? key)
        {
            string value = (key ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Replace('_', '-');
            value = Regex.Replace(value, @"\s+", "-");
            value = Regex.Replace(value, @"[^a-z0-9\-]+", string.Empty);
            value = Regex.Replace(value, @"\-+", "-").Trim('-');
            return value;
        }

        private static Bitmap? TryLoadCollectionTileArtwork(string? artworkKey, string? fallbackBucketKey = null)
        {
            try
            {
                var candidateKeys = new List<string>();

                void AddKey(string? key)
                {
                    string normalized = NormalizeCollectionArtworkKey(key);
                    if (string.IsNullOrWhiteSpace(normalized))
                        return;
                    if (candidateKeys.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                        return;
                    candidateKeys.Add(normalized);
                }

                AddKey(artworkKey);
                if (string.IsNullOrWhiteSpace(artworkKey))
                    AddKey(fallbackBucketKey);

                if (candidateKeys.Count == 0)
                    return null;

                string baseDir = AppContext.BaseDirectory;
                string[] dirs =
                {
                    Path.Combine(baseDir, "Assets", "CollectionTiles"),
                    Path.Combine(baseDir, "assets", "CollectionTiles")
                };

                string[] exts = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir))
                        continue;

                    foreach (var key in candidateKeys)
                    {
                        foreach (var ext in exts)
                        {
                            string path = Path.Combine(dir, key + ext);
                            if (!File.Exists(path))
                                continue;

                            using var bmp = new Bitmap(path);
                            return new Bitmap(bmp);
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static Bitmap? TryLoadInlineRootsPreviewFromAssets(string category)
        {
            try
            {
                string key = NormalizeCategoryPreviewKey(category);
                string baseDir = AppContext.BaseDirectory;
                string[] dirs =
                {
                    Path.Combine(baseDir, "Assets", "LibraryEmpty"),
                    Path.Combine(baseDir, "Assets", "CategoryPlaceholders"),
                    Path.Combine(baseDir, "assets", "LibraryEmpty"),
                    Path.Combine(baseDir, "assets", "CategoryPlaceholders")
                };

                string[] exts = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir))
                        continue;

                    foreach (var ext in exts)
                    {
                        string path = Path.Combine(dir, key + ext);
                        if (!File.Exists(path))
                            continue;

                        using var bmp = new Bitmap(path);
                        return new Bitmap(bmp);
                    }
                }
            }
            catch { }

            return null;
        }

        private static Bitmap BuildInlineRootsPreviewFallback(string category, int width, int height)
        {
            width = Math.Max(760, width);
            height = Math.Max(430, height);

            string key = NormalizeCategoryPreviewKey(category);
            string title = string.Equals(category, "Film", StringComparison.OrdinalIgnoreCase)
                ? "Film e Serie TV"
                : (string.IsNullOrWhiteSpace(category) ? "Libreria" : category);

            var accentA = key switch
            {
                "film" => Color.FromArgb(86, 132, 255),
                "video" => Color.FromArgb(93, 98, 255),
                "foto" => Color.FromArgb(44, 190, 156),
                "musica" => Color.FromArgb(233, 119, 74),
                "playlist" => Color.FromArgb(216, 173, 74),
                "preferiti" => Color.FromArgb(224, 92, 132),
                _ => Theme.Accent
            };

            var accentB = key switch
            {
                "film" => Color.FromArgb(35, 74, 196),
                "video" => Color.FromArgb(55, 57, 178),
                "foto" => Color.FromArgb(22, 126, 104),
                "musica" => Color.FromArgb(160, 72, 38),
                "playlist" => Color.FromArgb(154, 112, 22),
                "preferiti" => Color.FromArgb(146, 52, 82),
                _ => Color.FromArgb(33, 92, 184)
            };

            string subtitle = key switch
            {
                "film" => "Aggiungi dischi o cartelle e la libreria mostrerà film, saghe e serie TV.",
                "video" => "Configura le sorgenti video per avere subito clip, demo e contenuti brevi.",
                "foto" => "Collega le cartelle foto per sfogliare la libreria visiva da qui.",
                "musica" => "Imposta le cartelle musicali per album, tracce e contenuti recenti.",
                "playlist" => "Aggiungi sorgenti locali per creare e gestire playlist personalizzate.",
                "preferiti" => "I contenuti salvati compariranno qui appena colleghi almeno una libreria.",
                _ => "Aggiungi una cartella per iniziare a popolare questa libreria."
            };

            void DrawPosterCard(Graphics g, Rectangle rect, Color border, Color accent, bool wide)
            {
                using var path = GraphicsUtil.RoundRect(rect, 14);
                using var fill = new LinearGradientBrush(rect, Color.FromArgb(30, 33, 42), Color.FromArgb(18, 19, 26), 90f);
                using var pen = new Pen(Color.FromArgb(90, border), 1.4f);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);

                int artH = wide ? (int)(rect.Height * 0.64f) : (int)(rect.Height * 0.72f);
                var artRect = new Rectangle(rect.Left + 10, rect.Top + 10, rect.Width - 20, Math.Max(32, artH - 14));
                using (var artPath = GraphicsUtil.RoundRect(artRect, 10))
                using (var artFill = new LinearGradientBrush(artRect, Color.FromArgb(42, accent), Color.FromArgb(18, accent), 35f))
                {
                    g.FillPath(artFill, artPath);
                }

                using var barBrush = new SolidBrush(Color.FromArgb(220, accent));
                g.FillRectangle(barBrush, rect.Left + 12, rect.Top + artH + 6, Math.Max(40, rect.Width - 40), 4);
                using var txtBrush = new SolidBrush(Color.FromArgb(215, 225, 232));
                using var subBrush = new SolidBrush(Color.FromArgb(128, 170, 178, 188));
                using var body = new Font("Segoe UI Semibold", 10f, GraphicsUnit.Point);
                using var meta = new Font("Segoe UI", 8.5f, GraphicsUnit.Point);
                g.DrawString("Copertina", body, txtBrush, rect.Left + 12, rect.Bottom - 50);
                g.DrawString("Artworks e metadati", meta, subBrush, rect.Left + 12, rect.Bottom - 30);
            }

            void DrawFilmScene(Graphics g, Rectangle stage)
            {
                var left = new Rectangle(stage.Left + 28, stage.Top + 24, 150, stage.Height - 48);
                var middle = new Rectangle(left.Right + 24, stage.Top + 6, 172, stage.Height - 12);
                var right = new Rectangle(middle.Right + 24, stage.Top + 24, 150, stage.Height - 48);
                DrawPosterCard(g, left, accentA, accentB, wide: false);
                DrawPosterCard(g, middle, accentA, accentA, wide: false);
                DrawPosterCard(g, right, accentA, accentB, wide: false);
            }

            void DrawVideoScene(Graphics g, Rectangle stage)
            {
                using var path = GraphicsUtil.RoundRect(stage, 24);
                using var fill = new LinearGradientBrush(stage, Color.FromArgb(24, 28, 40), Color.FromArgb(14, 16, 24), 90f);
                using var pen = new Pen(Color.FromArgb(86, accentA), 1.8f);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);

                var playRect = new Rectangle(stage.Left + stage.Width / 2 - 64, stage.Top + stage.Height / 2 - 64, 128, 128);
                using (var ellipseBrush = new SolidBrush(Color.FromArgb(34, accentA)))
                    g.FillEllipse(ellipseBrush, playRect);
                using (var ellipsePen = new Pen(Color.FromArgb(120, accentA), 2f))
                    g.DrawEllipse(ellipsePen, playRect);

                var tri = new PointF[]
                {
                    new PointF(playRect.Left + 48, playRect.Top + 34),
                    new PointF(playRect.Left + 48, playRect.Bottom - 34),
                    new PointF(playRect.Right - 34, playRect.Top + playRect.Height / 2f)
                };
                using var triBrush = new SolidBrush(Color.White);
                g.FillPolygon(triBrush, tri);
            }

            void DrawPhotoScene(Graphics g, Rectangle stage)
            {
                using var path = GraphicsUtil.RoundRect(stage, 22);
                using var fill = new LinearGradientBrush(stage, Color.FromArgb(18, 28, 28), Color.FromArgb(9, 14, 16), 90f);
                using var pen = new Pen(Color.FromArgb(82, accentA), 1.8f);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);

                using var sky = new SolidBrush(Color.FromArgb(48, accentA));
                g.FillRectangle(sky, stage.Left + 18, stage.Top + 18, stage.Width - 36, (int)(stage.Height * 0.38f));
                using var sun = new SolidBrush(Color.FromArgb(220, 245, 225, 162));
                g.FillEllipse(sun, stage.Right - 124, stage.Top + 38, 46, 46);
                using var mountainBrush = new SolidBrush(Color.FromArgb(170, accentB));
                g.FillPolygon(mountainBrush, new[]
                {
                    new Point(stage.Left + 42, stage.Bottom - 44),
                    new Point(stage.Left + 140, stage.Top + 124),
                    new Point(stage.Left + 248, stage.Bottom - 44)
                });
                g.FillPolygon(mountainBrush, new[]
                {
                    new Point(stage.Left + 182, stage.Bottom - 44),
                    new Point(stage.Left + 312, stage.Top + 88),
                    new Point(stage.Right - 42, stage.Bottom - 44)
                });
            }

            void DrawMusicScene(Graphics g, Rectangle stage)
            {
                using var path = GraphicsUtil.RoundRect(stage, 22);
                using var fill = new LinearGradientBrush(stage, Color.FromArgb(30, 20, 18), Color.FromArgb(12, 10, 10), 90f);
                using var pen = new Pen(Color.FromArgb(86, accentA), 1.8f);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);

                var disc = new Rectangle(stage.Left + 50, stage.Top + 34, 180, 180);
                using var discBrush = new SolidBrush(Color.FromArgb(34, 34, 38));
                using var discPen = new Pen(Color.FromArgb(104, accentA), 2f);
                g.FillEllipse(discBrush, disc);
                g.DrawEllipse(discPen, disc);
                g.DrawEllipse(discPen, disc.Left + 36, disc.Top + 36, disc.Width - 72, disc.Height - 72);
                g.FillEllipse(new SolidBrush(accentA), disc.Left + 78, disc.Top + 78, 24, 24);

                using var eqBrush = new SolidBrush(Color.FromArgb(212, accentA));
                int baseX = disc.Right + 54;
                int baseY = stage.Bottom - 40;
                int[] heights = { 42, 88, 54, 118, 72, 96, 58 };
                for (int i = 0; i < heights.Length; i++)
                    g.FillRectangle(eqBrush, baseX + i * 26, baseY - heights[i], 14, heights[i]);
            }

            void DrawListScene(Graphics g, Rectangle stage, bool star)
            {
                using var path = GraphicsUtil.RoundRect(stage, 22);
                using var fill = new LinearGradientBrush(stage, Color.FromArgb(26, 24, 22), Color.FromArgb(14, 12, 10), 90f);
                using var pen = new Pen(Color.FromArgb(86, accentA), 1.8f);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);

                using var barBrush = new SolidBrush(Color.FromArgb(220, accentA));
                using var faintBrush = new SolidBrush(Color.FromArgb(120, 255, 255, 255));
                for (int i = 0; i < 5; i++)
                {
                    int y = stage.Top + 42 + i * 42;
                    g.FillRectangle(faintBrush, stage.Left + 52, y + 4, 18, 18);
                    g.FillRectangle(i < 2 ? barBrush : faintBrush, stage.Left + 88, y + 3, stage.Width - 156, 6);
                    g.FillRectangle(faintBrush, stage.Left + 88, y + 16, stage.Width - 220, 5);
                }

                if (star)
                {
                    var cx = stage.Right - 100;
                    var cy = stage.Top + 86;
                    var r1 = 42f;
                    var r2 = 18f;
                    var pts = new List<PointF>();
                    for (int i = 0; i < 10; i++)
                    {
                        double a = -Math.PI / 2 + i * Math.PI / 5;
                        float r = (i % 2 == 0) ? r1 : r2;
                        pts.Add(new PointF(cx + (float)(Math.Cos(a) * r), cy + (float)(Math.Sin(a) * r)));
                    }
                    using var starBrush = new SolidBrush(Color.FromArgb(220, accentA));
                    g.FillPolygon(starBrush, pts.ToArray());
                }
            }

            var bmp = new Bitmap(width, height);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Black);

            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(14, 17, 24), Color.FromArgb(7, 8, 12), 15f))
                g.FillRectangle(bg, 0, 0, width, height);

            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(width - 320, -70, 360, 360);
                using var br = new PathGradientBrush(glow)
                {
                    CenterColor = Color.FromArgb(112, accentA),
                    SurroundColors = new[] { Color.FromArgb(0, accentA) }
                };
                g.FillPath(br, glow);
            }

            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(-120, height - 220, 280, 280);
                using var br = new PathGradientBrush(glow)
                {
                    CenterColor = Color.FromArgb(70, accentB),
                    SurroundColors = new[] { Color.FromArgb(0, accentB) }
                };
                g.FillPath(br, glow);
            }

            var hero = new Rectangle(34, 28, width - 68, height - 56);
            using (var path = GraphicsUtil.RoundRect(hero, 24))
            {
                using var fill = new SolidBrush(Color.FromArgb(32, 255, 255, 255));
                using var pen = new Pen(Color.FromArgb(62, 255, 255, 255));
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            var stage = new Rectangle(hero.Left + 34, hero.Top + 30, hero.Width - 68, hero.Height - 164);
            switch (key)
            {
                case "film":
                    DrawFilmScene(g, stage);
                    break;
                case "video":
                    DrawVideoScene(g, stage);
                    break;
                case "foto":
                    DrawPhotoScene(g, stage);
                    break;
                case "musica":
                    DrawMusicScene(g, stage);
                    break;
                case "preferiti":
                    DrawListScene(g, stage, star: true);
                    break;
                default:
                    DrawListScene(g, stage, star: false);
                    break;
            }

            using var titleFont = new Font("Segoe UI Semibold", 21f, GraphicsUnit.Point);
            using var subFont = new Font("Segoe UI", 10.5f, GraphicsUnit.Point);
            using var chipFont = new Font("Segoe UI Semibold", 9.5f, GraphicsUnit.Point);
            TextRenderer.DrawText(
                g,
                title,
                titleFont,
                new Rectangle(hero.Left + 34, hero.Bottom - 104, hero.Width - 68, 34),
                Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            TextRenderer.DrawText(
                g,
                subtitle,
                subFont,
                new Rectangle(hero.Left + 34, hero.Bottom - 66, hero.Width - 68, 40),
                Theme.SubtleText,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            return bmp;
        }

        private static Bitmap BuildInlineRootsPreview(string category, int width, int height)
        {
            var asset = TryLoadInlineRootsPreviewFromAssets(category);
            if (asset != null)
                return asset;

            return BuildInlineRootsPreviewFallback(category, width, height);
        }


        private void EnsureSeasonEpisodeOverlay()
        {
            if (_seasonEpisodeOverlay != null)
                return;

            _seasonEpisodeOverlay = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(190, 8, 8, 12),
                Visible = false
            };

            var content = new Panel
            {
                BackColor = Theme.PanelAlt,
                Size = new Size(720, 470),
                Padding = new Padding(24),
                BorderStyle = BorderStyle.FixedSingle
            };

            void CenterContent()
            {
                if (_seasonEpisodeOverlay == null) return;
                if (_seasonEpisodeOverlay.ClientSize.Width <= 0 || _seasonEpisodeOverlay.ClientSize.Height <= 0)
                    return;

                content.Left = (_seasonEpisodeOverlay.ClientSize.Width - content.Width) / 2;
                content.Top = (_seasonEpisodeOverlay.ClientSize.Height - content.Height) / 2;
            }

            _seasonEpisodeOverlay.Resize += (_, __) => CenterContent();
            _seasonEpisodeOverlay.Click += (_, __) => CloseSeasonEpisodeOverlay();
            content.Click += (_, __) => { };

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 82,
                BackColor = Color.Transparent
            };

            _seasonEpisodeOverlayTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 16f),
                AutoEllipsis = true
            };

            _seasonEpisodeOverlaySubtitle = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Theme.SubtleText,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 10f),
                TextAlign = ContentAlignment.MiddleLeft
            };

            header.Controls.Add(_seasonEpisodeOverlaySubtitle);
            header.Controls.Add(_seasonEpisodeOverlayTitle);

            _seasonEpisodeOverlayList = new EpisodePickerListBox
            {
                Dock = DockStyle.Fill
            };
            _seasonEpisodeOverlayList.DoubleClick += (_, __) => OpenSelectedSeasonEpisode();
            _seasonEpisodeOverlayList.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    OpenSelectedSeasonEpisode();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    CloseSeasonEpisodeOverlay();
                    e.Handled = true;
                }
            };

            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 14, 0, 0)
            };

            var footerHint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Scegli l'episodio da aprire. Premi Invio oppure fai doppio clic.",
                ForeColor = Theme.SubtleText,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9f)
            };

            var footerSpacer = new Panel
            {
                Dock = DockStyle.Right,
                Width = 8,
                BackColor = Color.Transparent
            };

            _seasonEpisodeOverlayCloseButton = new FlatButton("Chiudi", FlatButton.Variant.Secondary)
            {
                Width = 110,
                Height = 32,
                Dock = DockStyle.Right
            };
            _seasonEpisodeOverlayCloseButton.Click += (_, __) => CloseSeasonEpisodeOverlay();

            _seasonEpisodeOverlayOpenButton = new FlatButton("Apri episodio", FlatButton.Variant.Primary)
            {
                Width = 150,
                Height = 32,
                Dock = DockStyle.Right
            };
            _seasonEpisodeOverlayOpenButton.Click += (_, __) => OpenSelectedSeasonEpisode();

            footer.Controls.Add(_seasonEpisodeOverlayCloseButton);
            footer.Controls.Add(footerSpacer);
            footer.Controls.Add(_seasonEpisodeOverlayOpenButton);
            footer.Controls.Add(footerHint);

            content.Controls.Add(_seasonEpisodeOverlayList);
            content.Controls.Add(footer);
            content.Controls.Add(header);

            _seasonEpisodeOverlay.Controls.Add(content);
            _right.Controls.Add(_seasonEpisodeOverlay);
            CenterContent();
        }

        private string BuildSeasonEpisodeOverlaySubtitle(TvSeasonGroup group)
        {
            string seasonLabel = group.SeasonNumber.HasValue ? $"Stagione {group.SeasonNumber.Value:00}" : "Speciali";
            string countLabel = group.Episodes.Count == 1 ? "1 episodio" : $"{group.Episodes.Count} episodi";
            return seasonLabel + " • " + countLabel;
        }

        private Control? GetSeasonEpisodeOverlayFocusTarget()
        {
            try
            {
                if (_seasonEpisodeOverlay == null || !_seasonEpisodeOverlay.Visible)
                    return null;

                if (_seasonEpisodeOverlayList != null && !_seasonEpisodeOverlayList.IsDisposed && _seasonEpisodeOverlayList.Visible && _seasonEpisodeOverlayList.Enabled)
                    return _seasonEpisodeOverlayList;

                if (_seasonEpisodeOverlayOpenButton != null && !_seasonEpisodeOverlayOpenButton.IsDisposed && _seasonEpisodeOverlayOpenButton.Visible && _seasonEpisodeOverlayOpenButton.Enabled)
                    return _seasonEpisodeOverlayOpenButton;

                if (_seasonEpisodeOverlayCloseButton != null && !_seasonEpisodeOverlayCloseButton.IsDisposed && _seasonEpisodeOverlayCloseButton.Visible && _seasonEpisodeOverlayCloseButton.Enabled)
                    return _seasonEpisodeOverlayCloseButton;

                if (!_seasonEpisodeOverlay.IsDisposed && _seasonEpisodeOverlay.Enabled)
                    return _seasonEpisodeOverlay;
            }
            catch { }

            return null;
        }

        private void EnsureSeasonEpisodeOverlayFocus()
        {
            try
            {
                if (_seasonEpisodeOverlayList == null)
                    return;

                if (_seasonEpisodeOverlayList.Items.Count > 0 && _seasonEpisodeOverlayList.SelectedIndex < 0)
                    _seasonEpisodeOverlayList.SelectedIndex = 0;

                _seasonEpisodeOverlayList.Focus();
            }
            catch { }
        }

        private void MoveSeasonEpisodeOverlaySelectionTo(int index)
        {
            if (_seasonEpisodeOverlayList == null || _seasonEpisodeOverlayList.Items.Count == 0)
                return;

            int clamped = Math.Max(0, Math.Min(index, _seasonEpisodeOverlayList.Items.Count - 1));
            _seasonEpisodeOverlayList.SelectedIndex = clamped;
            EnsureSeasonEpisodeOverlayFocus();
        }

        private void MoveSeasonEpisodeOverlaySelection(int delta)
        {
            if (_seasonEpisodeOverlayList == null || _seasonEpisodeOverlayList.Items.Count == 0)
                return;

            int current = _seasonEpisodeOverlayList.SelectedIndex;
            if (current < 0)
                current = 0;

            MoveSeasonEpisodeOverlaySelectionTo(current + delta);
        }

        private void RefreshSeasonEpisodeOverlayTitlesFromCache()
        {
            if (_seasonEpisodeOverlayGroup == null || _seasonEpisodeOverlayList == null)
                return;

            bool changed = false;
            foreach (var episode in _seasonEpisodeOverlayGroup.Episodes)
            {
                try
                {
                    var parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(episode.FilePath);
                    string bestTitle = MovieMetadataService.GetBestKnownDisplayTitle(episode.FilePath);
                    string resolvedText = BuildEpisodeChoiceDisplay(parsed, episode.FileName, bestTitle);
                    if (!string.Equals(episode.DisplayText, resolvedText, StringComparison.Ordinal))
                    {
                        episode.DisplayText = resolvedText;
                        changed = true;
                    }
                }
                catch { }
            }

            if (changed)
            {
                try { _seasonEpisodeOverlayList.Refresh(); } catch { }
            }
        }

        private void BeginResolveSeasonEpisodeOverlayTitles(TvSeasonGroup group)
        {
            try
            {
                var previous = Interlocked.Exchange(ref _seasonEpisodeOverlayTitleCts, null);
                try { previous?.Cancel(); } catch { }
                try { previous?.Dispose(); } catch { }
            }
            catch { }

            var cts = new CancellationTokenSource();
            _seasonEpisodeOverlayTitleCts = cts;
            var token = cts.Token;

            _ = Task.Run(() =>
            {
                foreach (var episode in group.Episodes.ToList())
                {
                    if (token.IsCancellationRequested)
                        return;

                    try
                    {
                        var parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(episode.FilePath);
                        string currentBest = MovieMetadataService.GetBestKnownDisplayTitle(episode.FilePath);
                        string currentText = BuildEpisodeChoiceDisplay(parsed, episode.FileName, currentBest);
                        if (!string.Equals(episode.DisplayText, currentText, StringComparison.Ordinal))
                        {
                            episode.DisplayText = currentText;
                            TryPostToControl(this, () =>
                            {
                                if (token.IsCancellationRequested) return;
                                if (ReferenceEquals(_seasonEpisodeOverlayGroup, group) && _seasonEpisodeOverlay?.Visible == true)
                                    _seasonEpisodeOverlayList?.Refresh();
                            });
                        }

                        bool needsResolve = string.Equals(currentText, BuildEpisodeChoiceDisplay(parsed, episode.FileName), StringComparison.Ordinal);
                        if (!needsResolve)
                            continue;

                        double? durationSeconds = null;
                        try
                        {
                            var mins = GetDurationMinutesCached(episode.FilePath);
                            if (mins.HasValue)
                                durationSeconds = mins.Value * 60.0;
                        }
                        catch { }

                        var resolved = MovieMetadataService.ResolveTitleAndPoster(episode.FilePath, durationSeconds, token);
                        string refreshedText = BuildEpisodeChoiceDisplay(parsed, episode.FileName, resolved.normalizedTitle);
                        if (string.Equals(episode.DisplayText, refreshedText, StringComparison.Ordinal))
                            continue;

                        episode.DisplayText = refreshedText;
                        TryPostToControl(this, () =>
                        {
                            if (token.IsCancellationRequested) return;
                            if (ReferenceEquals(_seasonEpisodeOverlayGroup, group) && _seasonEpisodeOverlay?.Visible == true)
                                _seasonEpisodeOverlayList?.Refresh();
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                    }
                }
            }, token);
        }

        private void ShowSeasonEpisodeOverlay(TvSeasonGroup group, string? displayTitle)
        {
            if (group == null)
                return;

            EnsureSeasonEpisodeOverlay();
            if (_seasonEpisodeOverlay == null || _seasonEpisodeOverlayList == null)
                return;

            _seasonEpisodeOverlayGroup = group;
            if (_seasonEpisodeOverlayTitle != null)
            {
                string title = !string.IsNullOrWhiteSpace(displayTitle)
                    ? displayTitle.Trim()
                    : (!string.IsNullOrWhiteSpace(group.SeriesTitle) ? group.SeriesTitle.Trim() : group.DisplayName);
                _seasonEpisodeOverlayTitle.Text = title;
            }

            if (_seasonEpisodeOverlaySubtitle != null)
                _seasonEpisodeOverlaySubtitle.Text = BuildSeasonEpisodeOverlaySubtitle(group);

            RefreshSeasonEpisodeOverlayTitlesFromCache();

            _seasonEpisodeOverlayList.BeginUpdate();
            _seasonEpisodeOverlayList.Items.Clear();
            foreach (var episode in group.Episodes)
                _seasonEpisodeOverlayList.Items.Add(episode);
            _seasonEpisodeOverlayList.EndUpdate();

            if (_seasonEpisodeOverlayList.Items.Count > 0)
                _seasonEpisodeOverlayList.SelectedIndex = 0;

            _seasonEpisodeOverlay.Visible = true;
            _seasonEpisodeOverlay.BringToFront();
            BeginResolveSeasonEpisodeOverlayTitles(group);

            try
            {
                BeginInvoke(new Action(EnsureSeasonEpisodeOverlayFocus));
            }
            catch
            {
                EnsureSeasonEpisodeOverlayFocus();
            }
        }

        private void CloseSeasonEpisodeOverlay()
        {
            try
            {
                var previous = Interlocked.Exchange(ref _seasonEpisodeOverlayTitleCts, null);
                try { previous?.Cancel(); } catch { }
                try { previous?.Dispose(); } catch { }
            }
            catch { }

            try
            {
                if (_seasonEpisodeOverlay != null)
                    _seasonEpisodeOverlay.Visible = false;
            }
            catch { }
        }

        private void OpenSelectedSeasonEpisode()
        {
            if (_seasonEpisodeOverlayList?.SelectedItem is TvEpisodeOption episode && !string.IsNullOrWhiteSpace(episode.FilePath))
            {
                CloseSeasonEpisodeOverlay();
                SafeOpen(episode.FilePath);
                return;
            }

            if (_seasonEpisodeOverlayGroup != null && !string.IsNullOrWhiteSpace(_seasonEpisodeOverlayGroup.RepresentativePath))
            {
                CloseSeasonEpisodeOverlay();
                SafeOpen(_seasonEpisodeOverlayGroup.RepresentativePath);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            try
            {
                if (_seasonEpisodeOverlay?.Visible == true)
                {
                    var keyCode = keyData & Keys.KeyCode;
                    Control? focused = null;
                    try { focused = FindForm()?.ActiveControl; } catch { }
                    if (focused != null && focused.Parent != null && !IsDescendant(_seasonEpisodeOverlay, focused))
                        focused = null;

                    bool onList = ReferenceEquals(focused, _seasonEpisodeOverlayList) || (_seasonEpisodeOverlayList != null && focused != null && IsDescendant(_seasonEpisodeOverlayList, focused));
                    bool onOpen = ReferenceEquals(focused, _seasonEpisodeOverlayOpenButton);
                    bool onClose = ReferenceEquals(focused, _seasonEpisodeOverlayCloseButton);

                    switch (keyCode)
                    {
                        case Keys.Up:
                            if (onOpen || onClose)
                            {
                                EnsureSeasonEpisodeOverlayFocus();
                                return true;
                            }
                            MoveSeasonEpisodeOverlaySelection(-1);
                            return true;
                        case Keys.Down:
                            if (onOpen || onClose)
                            {
                                EnsureSeasonEpisodeOverlayFocus();
                                return true;
                            }
                            MoveSeasonEpisodeOverlaySelection(+1);
                            return true;
                        case Keys.PageUp:
                            MoveSeasonEpisodeOverlaySelection(-8);
                            return true;
                        case Keys.PageDown:
                            MoveSeasonEpisodeOverlaySelection(+8);
                            return true;
                        case Keys.Home:
                            MoveSeasonEpisodeOverlaySelectionTo(0);
                            return true;
                        case Keys.End:
                            if (_seasonEpisodeOverlayList != null)
                                MoveSeasonEpisodeOverlaySelectionTo(_seasonEpisodeOverlayList.Items.Count - 1);
                            return true;
                        case Keys.Right:
                        case Keys.Tab:
                            if (onList && _seasonEpisodeOverlayOpenButton != null)
                            {
                                _seasonEpisodeOverlayOpenButton.Focus();
                                return true;
                            }
                            if (onOpen && _seasonEpisodeOverlayCloseButton != null)
                            {
                                _seasonEpisodeOverlayCloseButton.Focus();
                                return true;
                            }
                            EnsureSeasonEpisodeOverlayFocus();
                            return true;
                        case Keys.Left:
                            if (onClose && _seasonEpisodeOverlayOpenButton != null)
                            {
                                _seasonEpisodeOverlayOpenButton.Focus();
                                return true;
                            }
                            if (onOpen)
                            {
                                EnsureSeasonEpisodeOverlayFocus();
                                return true;
                            }
                            if (onList && _seasonEpisodeOverlayCloseButton != null)
                            {
                                _seasonEpisodeOverlayCloseButton.Focus();
                                return true;
                            }
                            return true;
                        case Keys.Enter:
                        case Keys.Space:
                            if (onClose && _seasonEpisodeOverlayCloseButton != null)
                            {
                                CloseSeasonEpisodeOverlay();
                                return true;
                            }
                            if (onOpen && _seasonEpisodeOverlayOpenButton != null)
                            {
                                OpenSelectedSeasonEpisode();
                                return true;
                            }
                            OpenSelectedSeasonEpisode();
                            return true;
                        case Keys.Escape:
                            CloseSeasonEpisodeOverlay();
                            return true;
                    }
                }

                if (_rootsOverlay?.Visible == true)
                {
                    var keyCode = keyData & Keys.KeyCode;
                    if (keyCode == Keys.Escape)
                    {
                        CloseRootsOverlay(commit: true, refreshAfterCommit: true);
                        return true;
                    }
                }
            }
            catch { }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private string GetCurrentSortLabel()
        {
            return _sortIndex switch
            {
                1 => "Nome A–Z",
                2 => "Dimensione",
                _ => "Recenti"
            };
        }

        private void UpdateSortChipText()
        {
            string label = GetCurrentSortLabel();
            if (string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase))
            {
                string sectionLabel = _seriesSectionFirst ? "Serie TV prima" : "Film prima";
                _chipSort.Text = $"Ordina: {label} • {sectionLabel}";
            }
            else
            {
                _chipSort.Text = $"Ordina: {label}";
            }

            _chipSort.AutoSizeToText();
        }

        private static readonly ToolStripRenderer _libraryMenuRenderer = new LibraryMenuRenderer();

        private static void ApplyDarkMenuTheme(ContextMenuStrip? menu)
        {
            if (menu == null) return;
            try
            {
                menu.RenderMode = ToolStripRenderMode.Professional;
                menu.Renderer = _libraryMenuRenderer;
                menu.BackColor = Color.FromArgb(26, 26, 26);
                menu.ForeColor = Color.Gainsboro;
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = false;
                menu.Padding = new Padding(4);
            }
            catch { }
        }

        private static void ApplyDarkMenuTheme(ToolStripDropDownMenu? menu)
        {
            if (menu == null) return;
            try
            {
                menu.RenderMode = ToolStripRenderMode.Professional;
                menu.Renderer = _libraryMenuRenderer;
                menu.BackColor = Color.FromArgb(26, 26, 26);
                menu.ForeColor = Color.Gainsboro;
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = false;
                menu.Padding = new Padding(4);
            }
            catch { }
        }

        private static void ApplyDarkMenuThemeRecursive(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                if (it is ToolStripMenuItem mi)
                {
                    try
                    {
                        if (mi.DropDown is ToolStripDropDownMenu dd)
                            ApplyDarkMenuTheme(dd);
                    }
                    catch { }

                    try
                    {
                        if (mi.HasDropDownItems)
                            ApplyDarkMenuThemeRecursive(mi.DropDownItems);
                    }
                    catch { }
                }
            }
        }

        private sealed class LibraryMenuColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Color.FromArgb(26, 26, 26);
            public override Color MenuItemSelected => Color.FromArgb(52, 52, 52);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(52, 52, 52);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(52, 52, 52);
            public override Color MenuItemBorder => Color.FromArgb(80, 80, 80);
            public override Color SeparatorDark => Color.FromArgb(55, 55, 55);
            public override Color SeparatorLight => Color.FromArgb(55, 55, 55);
            public override Color ImageMarginGradientBegin => Color.FromArgb(26, 26, 26);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(26, 26, 26);
            public override Color ImageMarginGradientEnd => Color.FromArgb(26, 26, 26);
        }

        private sealed class LibraryMenuRenderer : ToolStripProfessionalRenderer
        {
            public LibraryMenuRenderer() : base(new LibraryMenuColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                try { e.Graphics.Clear(Color.FromArgb(26, 26, 26)); }
                catch { base.OnRenderToolStripBackground(e); }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                try
                {
                    var r = new Rectangle(Point.Empty, e.Item.Size);
                    var mi = e.Item as ToolStripMenuItem;
                    bool selected = e.Item.Selected;
                    bool checkedItem = mi?.Checked == true;

                    Color bg = Color.FromArgb(26, 26, 26);
                    if (checkedItem) bg = Color.FromArgb(40, 40, 40);
                    if (selected) bg = Color.FromArgb(52, 52, 52);

                    using var b = new SolidBrush(bg);
                    e.Graphics.FillRectangle(b, r);

                    if (selected)
                    {
                        using var p = new Pen(Color.FromArgb(80, 80, 80));
                        e.Graphics.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
                    }
                }
                catch
                {
                    base.OnRenderMenuItemBackground(e);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Color.Gainsboro : Color.FromArgb(140, 140, 140);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item.Enabled ? Color.Gainsboro : Color.FromArgb(140, 140, 140);
                base.OnRenderArrow(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                try
                {
                    int y = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2;
                    using var p = new Pen(Color.FromArgb(55, 55, 55));
                    e.Graphics.DrawLine(p, e.Item.ContentRectangle.Left + 8, y, e.Item.ContentRectangle.Right - 8, y);
                }
                catch
                {
                    base.OnRenderSeparator(e);
                }
            }
        }

        private void BuildHeaderFilters()
        {
            _selExt = "Tutte";
            _chipExt.Text = "Estensione: Tutte";
            _chipExt.AutoSizeToText();

            _menuExt = new ContextMenuStrip
            {
                ShowImageMargin = false,
                RenderMode = ToolStripRenderMode.Professional,
                BackColor = Color.FromArgb(36, 36, 42),
                ForeColor = Theme.Text
            };

            _menuExt.Items.Add(MakeExtItem("Tutte"));
            foreach (var e in ExtsForCategory(_selCat)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s))
            {
                _menuExt.Items.Add(MakeExtItem(e));
            }
            ApplyDarkMenuTheme(_menuExt);
            ApplyDarkMenuThemeRecursive(_menuExt.Items);

            _menuSort ??= new ContextMenuStrip
            {
                ShowImageMargin = false,
                RenderMode = ToolStripRenderMode.Professional,
                BackColor = Color.FromArgb(36, 36, 42),
                ForeColor = Theme.Text
            };

            _menuSort.Items.Clear();
            _menuSort.Items.Add(MakeSortItem("Recenti", 0));
            _menuSort.Items.Add(MakeSortItem("Nome A–Z", 1));
            _menuSort.Items.Add(MakeSortItem("Dimensione", 2));

            if (string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase))
            {
                _menuSort.Items.Add(new ToolStripSeparator());
                _menuSort.Items.Add(MakeSectionOrderItem("Film prima", seriesFirst: false));
                _menuSort.Items.Add(MakeSectionOrderItem("Serie TV prima", seriesFirst: true));
            }
            ApplyDarkMenuTheme(_menuSort);
            ApplyDarkMenuThemeRecursive(_menuSort.Items);

            UpdateSortChipText();
        }
        private ToolStripMenuItem MakeExtItem(string label)
        {
            var it = new ToolStripMenuItem(label) { ForeColor = Theme.Text };
            it.Click += (_, __) =>
            {
                _selExt = label;
                _chipExt.Text = $"Estensione: {label}";
                _chipExt.AutoSizeToText();
                LayoutHeader();
                ApplyFilterAndRender();
            };
            return it;
        }

        private ToolStripMenuItem MakeSortItem(string label, int idx)
        {
            var it = new ToolStripMenuItem(label) { ForeColor = Theme.Text };
            it.Click += (_, __) =>
            {
                _sortIndex = idx;
                UpdateSortChipText();
                LayoutHeader();
                ApplyFilterAndRender();
            };
            return it;
        }

        private ToolStripMenuItem MakeSectionOrderItem(string label, bool seriesFirst)
        {
            bool selected = _seriesSectionFirst == seriesFirst;
            var it = new ToolStripMenuItem(selected ? label + "  ✓" : label)
            {
                ForeColor = Theme.Text
            };
            it.Click += (_, __) =>
            {
                _seriesSectionFirst = seriesFirst;
                UpdateSortChipText();
                LayoutHeader();
                ApplyFilterAndRender();
            };
            return it;
        }

        private static ToolStripItem? FirstSelectableItem(ContextMenuStrip menu)
        {
            foreach (ToolStripItem it in menu.Items)
            {
                if (it != null && it.Available && it.Enabled)
                    return it;
            }
            return null;
        }

        private static ToolStripItem? FindMenuItemByText(ContextMenuStrip menu, string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            foreach (ToolStripItem it in menu.Items)
            {
                if (it == null) continue;
                if (string.Equals(it.Text, text, StringComparison.OrdinalIgnoreCase))
                    return it;
            }
            return null;
        }

        private void ShowMenuExt()
        {
            if (_menuExt == null) return;

            _menuExt.Show(_chipExt, new Point(0, _chipExt.Height));

            // Su WinForms, all'apertura il primo item non viene "selezionato" di default.
            // Per DPAD / tastiera è più naturale evidenziare subito qualcosa.
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (_menuExt == null) return;
                    var pref = FindMenuItemByText(_menuExt, _selExt);
                    (pref ?? FirstSelectableItem(_menuExt))?.Select();
                }));
            }
            catch { }
        }

        private void ShowMenuSort()
        {
            if (_menuSort == null) return;

            _menuSort.Show(_chipSort, new Point(0, _chipSort.Height));

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (_menuSort == null) return;

                    ToolStripItem? pref = null;
                    try
                    {
                        if (_sortIndex >= 0 && _sortIndex < _menuSort.Items.Count)
                            pref = _menuSort.Items[_sortIndex];
                    }
                    catch { }

                    (pref ?? FirstSelectableItem(_menuSort))?.Select();
                }));
            }
            catch { }
        }


        private string? PromptForPlaylistName()
        {
            return ShowTextPromptDialog(
                title: "Nuova playlist",
                message: "Nome della playlist",
                confirmText: "Crea");
        }

        private string? ShowTextPromptDialog(string title, string message, string confirmText)
        {
            using var dlg = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ShowIcon = false,
                ControlBox = false,
                ClientSize = new Size(420, 178),
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9.25f)
            };

            var lblTitle = new Label
            {
                Left = 18,
                Top = 16,
                Width = 384,
                Height = 24,
                Text = title,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 11f)
            };

            var lblMessage = new Label
            {
                Left = 18,
                Top = 50,
                Width = 384,
                Height = 22,
                Text = message,
                ForeColor = Theme.SubtleText,
                BackColor = Color.Transparent
            };

            var txt = new TextBox
            {
                Left = 18,
                Top = 78,
                Width = 384,
                Height = 30,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(20, 20, 24),
                ForeColor = Theme.Text
            };

            var btnOk = new Button
            {
                Text = confirmText,
                Width = 110,
                Height = 32,
                Left = 292,
                Top = 126,
                DialogResult = DialogResult.OK,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Accent,
                ForeColor = Color.White
            };
            btnOk.FlatAppearance.BorderSize = 0;

            var btnCancel = new Button
            {
                Text = "Annulla",
                Width = 96,
                Height = 32,
                Left = 188,
                Top = 126,
                DialogResult = DialogResult.Cancel,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.Text
            };
            btnCancel.FlatAppearance.BorderColor = Theme.Border;

            dlg.Controls.Add(lblTitle);
            dlg.Controls.Add(lblMessage);
            dlg.Controls.Add(txt);
            dlg.Controls.Add(btnCancel);
            dlg.Controls.Add(btnOk);
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            Form? owner = null;
            try { owner = FindForm(); } catch { }

            try
            {
                dlg.TopMost = owner?.TopMost == true;
                dlg.Shown += (_, __) =>
                {
                    try { dlg.Activate(); dlg.BringToFront(); } catch { }
                    try { txt.Focus(); txt.SelectAll(); } catch { }
                };
            }
            catch { }

            DialogResult result;
            try
            {
                result = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            }
            catch
            {
                result = dlg.ShowDialog();
            }

            if (result != DialogResult.OK)
                return null;

            string value = Regex.Replace((txt.Text ?? string.Empty).Trim(), @"\s+", " ");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private (string Name, string Bucket)? PromptForPlaylistDefinition(string? initialBucket = null, bool allowBucketSelection = true)
        {
            using var dlg = new Form
            {
                Text = "Nuova playlist",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ShowIcon = false,
                ControlBox = false,
                ClientSize = new Size(420, allowBucketSelection ? 230 : 178),
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9.25f)
            };

            var lblTitle = new Label
            {
                Left = 18,
                Top = 16,
                Width = 384,
                Height = 24,
                Text = "Nuova playlist",
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 11f)
            };

            var lblMessage = new Label
            {
                Left = 18,
                Top = 50,
                Width = 384,
                Height = 22,
                Text = "Nome della playlist",
                ForeColor = Theme.SubtleText,
                BackColor = Color.Transparent
            };

            var txt = new TextBox
            {
                Left = 18,
                Top = 78,
                Width = 384,
                Height = 30,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(20, 20, 24),
                ForeColor = Theme.Text
            };

            Label? lblBucket = null;
            ComboBox? cmbBucket = null;
            if (allowBucketSelection)
            {
                lblBucket = new Label
                {
                    Left = 18,
                    Top = 116,
                    Width = 384,
                    Height = 22,
                    Text = "Tipo di contenuto",
                    ForeColor = Theme.SubtleText,
                    BackColor = Color.Transparent
                };

                cmbBucket = new ComboBox
                {
                    Left = 18,
                    Top = 142,
                    Width = 384,
                    Height = 30,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(20, 20, 24),
                    ForeColor = Theme.Text
                };
                cmbBucket.Items.AddRange(new object[] { "Film e Serie TV", "Video", "Foto", "Musica" });
                string normalizedBucket = NormalizeCollectionBucketKey(initialBucket);
                cmbBucket.SelectedIndex = normalizedBucket switch
                {
                    "Film" => 0,
                    "Foto" => 2,
                    "Musica" => 3,
                    _ => 1
                };
            }

            int buttonsTop = allowBucketSelection ? 184 : 126;
            var btnOk = new Button
            {
                Text = "Crea",
                Width = 110,
                Height = 32,
                Left = 292,
                Top = buttonsTop,
                DialogResult = DialogResult.OK,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Accent,
                ForeColor = Color.White
            };
            btnOk.FlatAppearance.BorderSize = 0;

            var btnCancel = new Button
            {
                Text = "Annulla",
                Width = 96,
                Height = 32,
                Left = 188,
                Top = buttonsTop,
                DialogResult = DialogResult.Cancel,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.Text
            };
            btnCancel.FlatAppearance.BorderColor = Theme.Border;

            dlg.Controls.Add(lblTitle);
            dlg.Controls.Add(lblMessage);
            dlg.Controls.Add(txt);
            if (lblBucket != null) dlg.Controls.Add(lblBucket);
            if (cmbBucket != null) dlg.Controls.Add(cmbBucket);
            dlg.Controls.Add(btnCancel);
            dlg.Controls.Add(btnOk);
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            Form? owner = null;
            try { owner = FindForm(); } catch { }
            try
            {
                dlg.TopMost = owner?.TopMost == true;
                dlg.Shown += (_, __) =>
                {
                    try { dlg.Activate(); dlg.BringToFront(); } catch { }
                    try { txt.Focus(); txt.SelectAll(); } catch { }
                };
            }
            catch { }

            DialogResult result;
            try
            {
                result = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            }
            catch
            {
                result = dlg.ShowDialog();
            }

            if (result != DialogResult.OK)
                return null;

            string name = Regex.Replace((txt.Text ?? string.Empty).Trim(), @"\s+", " ");
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string bucket = NormalizeCollectionBucketKey(initialBucket);
            if (allowBucketSelection && cmbBucket != null)
            {
                bucket = cmbBucket.SelectedIndex switch
                {
                    0 => "Film",
                    2 => "Foto",
                    3 => "Musica",
                    _ => "Video"
                };
            }

            return (name, bucket);
        }


        private void EnsurePlaylistEditorOverlay()
        {
            if (_playlistEditorOverlay != null)
                return;

            _playlistEditorOverlay = new Panel
            {
                Size = new Size(460, 228),
                Padding = new Padding(18),
                BackColor = Theme.PanelAlt,
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false,
                Anchor = AnchorStyles.Top,
                TabStop = false
            };

            void PositionOverlay()
            {
                if (_playlistEditorOverlay == null)
                    return;

                int rightWidth = _right?.ClientSize.Width ?? Width;
                int overlayWidth = _playlistEditorOverlay.Width;
                int headerBottom = 0;
                try { headerBottom = _header?.Bottom ?? 0; } catch { }

                int left = Math.Max(16, (rightWidth - overlayWidth) / 2);
                int topMin = Math.Max(18, headerBottom + 12);
                int availableHeight = Math.Max(_playlistEditorOverlay.Height, (_right?.ClientSize.Height ?? Height) - topMin);
                int top = topMin + Math.Max(0, (availableHeight - _playlistEditorOverlay.Height) / 2);

                _playlistEditorOverlay.Left = left;
                _playlistEditorOverlay.Top = top;
            }

            try { _right.Resize += (_, __) => PositionOverlay(); } catch { }
            try { _header.Resize += (_, __) => PositionOverlay(); } catch { }
            _playlistEditorOverlay.VisibleChanged += (_, __) =>
            {
                if (_playlistEditorOverlay != null && _playlistEditorOverlay.Visible)
                {
                    PositionOverlay();
                    _playlistEditorOverlay.BringToFront();
                }
            };

            _playlistEditorOverlayTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 32,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 14f),
                Text = "Nuova playlist"
            };

            _playlistEditorOverlaySubtitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 38,
                ForeColor = Theme.SubtleText,
                Font = new Font("Segoe UI", 9.25f),
                Text = string.Empty
            };

            var nameLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = Theme.SubtleText,
                Font = new Font("Segoe UI", 9f),
                Text = "Nome playlist"
            };

            _playlistEditorNameBox = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 34,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(18, 18, 24),
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 10f)
            };
            _playlistEditorNameBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    CommitPlaylistEditorOverlay();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    ClosePlaylistEditorOverlay();
                }
                else if (e.KeyCode == Keys.Down || (e.KeyCode == Keys.Tab && !e.Shift))
                {
                    e.SuppressKeyPress = true;
                    try { _playlistEditorCreateButton?.Focus(); } catch { }
                }
            };

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            _playlistEditorCreateButton = new FlatButton("Crea playlist", FlatButton.Variant.Primary)
            {
                Width = 148,
                Height = 34,
                Margin = new Padding(8, 0, 0, 0),
                TabStop = true
            };
            _playlistEditorCreateButton.Click += (_, __) => CommitPlaylistEditorOverlay();

            var cancelButton = new FlatButton("Chiudi", FlatButton.Variant.Secondary)
            {
                Width = 104,
                Height = 34,
                Margin = new Padding(0),
                TabStop = true
            };
            cancelButton.Click += (_, __) => ClosePlaylistEditorOverlay();

            _playlistEditorCreateButton.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Left)
                {
                    e.SuppressKeyPress = true;
                    try { cancelButton.Focus(); } catch { }
                }
                else if (e.KeyCode == Keys.Up)
                {
                    e.SuppressKeyPress = true;
                    try { _playlistEditorNameBox?.Focus(); } catch { }
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    ClosePlaylistEditorOverlay();
                }
            };

            cancelButton.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Right)
                {
                    e.SuppressKeyPress = true;
                    try { _playlistEditorCreateButton?.Focus(); } catch { }
                }
                else if (e.KeyCode == Keys.Up)
                {
                    e.SuppressKeyPress = true;
                    try { _playlistEditorNameBox?.Focus(); } catch { }
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    ClosePlaylistEditorOverlay();
                }
            };

            footer.Controls.Add(_playlistEditorCreateButton);
            footer.Controls.Add(cancelButton);

            var body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 12, 0, 0)
            };
            body.Controls.Add(footer);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 14, BackColor = Color.Transparent });
            body.Controls.Add(_playlistEditorNameBox);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Color.Transparent });
            body.Controls.Add(nameLabel);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Color.Transparent });
            body.Controls.Add(_playlistEditorOverlaySubtitle);
            body.Controls.Add(_playlistEditorOverlayTitle);

            _playlistEditorOverlay.Controls.Add(body);
            _right.Controls.Add(_playlistEditorOverlay);
            PositionOverlay();
        }

        private void ShowCreatePlaylistOverlay(string bucketKey, Action<string> onCommit)
        {
            EnsurePlaylistEditorOverlay();
            if (_playlistEditorOverlay == null || _playlistEditorNameBox == null)
                return;

            _playlistEditorBucketKey = NormalizeCollectionBucketKey(bucketKey);
            _playlistEditorCommitAction = onCommit;

            if (_playlistEditorOverlayTitle != null)
                _playlistEditorOverlayTitle.Text = "Nuova playlist";
            if (_playlistEditorOverlaySubtitle != null)
                _playlistEditorOverlaySubtitle.Text = "Categoria: " + GetCollectionBucketDisplayName(_playlistEditorBucketKey) + Environment.NewLine + "Crea la playlist senza bloccare la libreria.";
            if (_playlistEditorCreateButton != null)
                _playlistEditorCreateButton.Text = "Crea playlist";

            _playlistEditorNameBox.Text = string.Empty;
            _playlistEditorOverlay.Visible = true;
            _playlistEditorOverlay.BringToFront();
            try { NotifyRemoteNavigationResetRequested(); } catch { }
            try { BeginInvoke(new Action(() => { _playlistEditorNameBox.Focus(); _playlistEditorNameBox.SelectAll(); try { RequestRemoteFocus(_playlistEditorNameBox); } catch { } })); } catch { }
        }

        private void CommitPlaylistEditorOverlay()
        {
            if (_playlistEditorNameBox == null)
                return;

            string name = Regex.Replace((_playlistEditorNameBox.Text ?? string.Empty).Trim(), @"\s+", " ");
            if (string.IsNullOrWhiteSpace(name))
            {
                try { _playlistEditorNameBox.Focus(); } catch { }
                return;
            }

            var callback = _playlistEditorCommitAction;
            ClosePlaylistEditorOverlay();
            try { callback?.Invoke(name); } catch { }
        }

        private void ClosePlaylistEditorOverlay()
        {
            try
            {
                if (_playlistEditorOverlay != null)
                    _playlistEditorOverlay.Visible = false;
                _playlistEditorCommitAction = null;
                _playlistEditorBucketKey = string.Empty;
                try
                {
                    if (_btnCreatePlaylist != null && _btnCreatePlaylist.Visible)
                    {
                        _btnCreatePlaylist.Focus();
                        try { RequestRemoteFocus(_btnCreatePlaylist); } catch { }
                    }
                }
                catch { }
            }
            catch { }
        }


        // ------------ ROW informativa / vuoto / messaggi ------------
        private sealed class InfoRow : Panel
        {
            public InfoRow(string text)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint, true);

                Height = 40;
                Dock = DockStyle.Top;
                BackColor = Color.Black;

                Controls.Add(new Label
                {
                    Text = text,
                    Dock = DockStyle.Fill,
                    ForeColor = Theme.SubtleText,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 11f),
                    BackColor = Color.Black
                });
            }
        }


        // ------------ HEADER BAR + CHIP + SEARCH + BUTTON ------------

        private sealed class HeaderBar : Panel
        {
            public HeaderBar()
            {
                BackColor = Theme.PanelAlt;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.PanelAlt);
            }
        }

        private sealed class HeaderActionButton : Control
        {
            private bool _hover;
            private bool _down;
            public string BtnText { get; set; }

            public HeaderActionButton(string text)
            {
                BtnText = text;
                Cursor = Cursors.Hand;
                Size = new Size(148, 32);
                TabStop = false;

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, __) => { _down = true; Invalidate(); };
                MouseUp += (_, __) =>
                {
                    _down = false;
                    Invalidate();
                };
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.PanelAlt);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);

                using var gp = GraphicsUtil.RoundRect(rect, 6);

                Color cTop = Theme.Accent;
                Color cBot = Color.FromArgb(180, Theme.Accent);
                if (_down)
                {
                    cTop = ControlPaint.Dark(cTop);
                    cBot = ControlPaint.Dark(cBot);
                }
                else if (_hover)
                {
                    cTop = ControlPaint.Light(cTop);
                }

                using (var lg = new LinearGradientBrush(rect, cTop, cBot, LinearGradientMode.Vertical))
                    g.FillPath(lg, gp);

                using var f = new Font("Segoe UI Semibold", 10.5f);
                TextRenderer.DrawText(
                    g,
                    BtnText,
                    f,
                    rect,
                    Color.White,
                    TextFormatFlags.HorizontalCenter
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);
            }
        }

        private sealed class Chip : Control
        {
            private bool _hover, _down;
            public Chip(string text)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.SupportsTransparentBackColor
                       | ControlStyles.ResizeRedraw, true);

                Font = new Font("Segoe UI", 10.5f);
                ForeColor = Color.White;
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
                Text = text;
                Height = 32;
                Width = 160;
                Margin = new Padding(0);

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, __) => { _down = true; Invalidate(); };
                MouseUp += (_, __) =>
                {
                    _down = false;
                    Invalidate();
                };
            }

            public void AutoSizeToText()
            {
                var w = TextRenderer.MeasureText(Text, Font).Width + 28;
                Width = Math.Max(120, w);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.PanelAlt);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rc = new Rectangle(0, 0, Width - 1, Height - 1);

                var fill = _down ? Color.FromArgb(60, Theme.Accent)
                                 : _hover ? Color.FromArgb(40, Theme.Accent)
                                          : Color.FromArgb(28, Theme.Accent);

                using var br = new SolidBrush(fill);
                using var pen = new Pen(Color.FromArgb(120, Theme.Accent));

                using var gp = GraphicsUtil.RoundRect(rc, 6);

                g.FillPath(br, gp);
                g.DrawPath(pen, gp);

                TextRenderer.DrawText(
                    g,
                    Text,
                    Font,
                    rc,
                    Color.White,
                    TextFormatFlags.VerticalCenter
                  | TextFormatFlags.HorizontalCenter
                  | TextFormatFlags.EndEllipsis);
            }
        }

        private sealed class SearchBox : Panel
        {
            public TextBox Inner { get; }

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public new string Text
            {
                get => Inner.Text;
                set => Inner.Text = value;
            }

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public new event EventHandler? TextChanged
            {
                add { Inner.TextChanged += value; }
                remove { Inner.TextChanged -= value; }
            }

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string Placeholder
            {
                get => Inner.PlaceholderText;
                set => Inner.PlaceholderText = value;
            }

            public SearchBox()
            {
                // Selezionabile via DPAD (evidenziamo il riquadro esterno).
                // L'editing nel TextBox interno avviene solo su OK/ENTER.
                SetStyle(ControlStyles.Selectable, true);
                TabStop = true;

                DoubleBuffered = true;
                Height = 32;
                BackColor = Theme.Panel;
                Padding = new Padding(12, 6, 12, 6);

                Inner = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    Font = new Font("Segoe UI", 10.5f),
                    ForeColor = Theme.Text,
                    BackColor = Theme.Panel,
                    Dock = DockStyle.Fill,
                    TabStop = false
                };

                Controls.Add(Inner);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Panel);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                using var bg = new SolidBrush(Theme.Panel);
                using var pen = new Pen(Theme.Border);
                g.FillRectangle(bg, ClientRectangle);
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }


        // ------------ NAV BUTTON SINISTRA ------------
        private sealed class NavButton : Control
        {
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool Selected { get; set; }

            public NavButton(string text)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint, true);

                Height = 40;
                Width = 220;
                Cursor = Cursors.Hand;
                Text = text;
                ForeColor = Theme.Text;
                BackColor = Theme.Nav;
                Margin = new Padding(0, 6, 0, 0);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);

                Color bgColor = !Enabled
                    ? Color.FromArgb(16, 18, 24)
                    : (Selected ? Theme.PanelAlt : Theme.Nav);
                Color borderColor = !Enabled ? Color.FromArgb(40, 44, 52) : Theme.Border;
                Color textColor = !Enabled ? Theme.Muted : Theme.Text;

                using var bg = new SolidBrush(bgColor);
                using var bd = new Pen(borderColor);
                g.FillRectangle(bg, r);
                g.DrawRectangle(bd, r);

                using var font = Selected && Enabled
                    ? new Font("Segoe UI Semibold", 10.5f)
                    : new Font("Segoe UI", 10.5f);

                string displayText = string.Equals(Text, "Film", StringComparison.OrdinalIgnoreCase)
                    ? "Film e Serie TV"
                    : Text;

                TextRenderer.DrawText(
                    g,
                    displayText,
                    font,
                    new Rectangle(12, 0, Width - 24, Height),
                    textColor,
                    TextFormatFlags.Left
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);
            }
        }


        // ------------ BOTTONE FOOTER SINISTRO ------------
        private sealed class FlatButton : Control
        {
            public enum Variant { Primary, Secondary }

            private readonly Variant _variant;
            private bool _hover;
            private bool _down;
            private readonly string _text;

            public FlatButton(string text, Variant variant)
            {
                _text = text;
                _variant = variant;

                Cursor = Cursors.Hand;
                Size = new Size(148, 32);
                TabStop = false;

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, __) => { _down = true; Invalidate(); };
                MouseUp += (_, __) =>
                {
                    _down = false;
                    Invalidate();
                };
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Nav);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);

                using var gp = GraphicsUtil.RoundRect(rect, 6);

                if (_variant == Variant.Primary)
                {
                    var cTop = Theme.Accent;
                    var cBot = Color.FromArgb(180, Theme.Accent);

                    if (_down)
                    {
                        cTop = ControlPaint.Dark(cTop);
                        cBot = ControlPaint.Dark(cBot);
                    }
                    else if (_hover)
                    {
                        cTop = ControlPaint.Light(cTop);
                    }

                    using (var lg = new LinearGradientBrush(rect, cTop, cBot, LinearGradientMode.Vertical))
                        g.FillPath(lg, gp);
                }
                else
                {
                    var baseCol = Theme.PanelAlt;
                    if (_hover) baseCol = ControlPaint.Light(baseCol);
                    if (_down) baseCol = ControlPaint.Dark(baseCol);

                    using (var br = new SolidBrush(baseCol))
                        g.FillPath(br, gp);

                    using var pen = new Pen(Color.FromArgb(90, Theme.Accent));
                    g.DrawPath(pen, gp);
                }

                using var f = new Font("Segoe UI Semibold", 10.5f);
                TextRenderer.DrawText(
                    g,
                    _text,
                    f,
                    rect,
                    Color.White,
                    TextFormatFlags.HorizontalCenter
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);
            }
        }


        // ------------ LOADING MASK (overlay caricamento) ------------
        private sealed class LoadingMask : Control
        {
            private readonly System.Windows.Forms.Timer _t = new() { Interval = 90 };
            private int _angle;
            private string _message = "Caricamento…";
            private bool _showSpinner = true;

            public LoadingMask()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;

                _t.Tick += (_, __) =>
                {
                    _angle = (_angle + 30) % 360;
                    Invalidate();
                };
                _t.Start();
            }

            public void SetVisualState(string m, bool showSpinner)
            {
                _message = m ?? string.Empty;
                _showSpinner = showSpinner;
                Invalidate();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // Schermo nero/opaque sopra tutta la pagina: così durante il cambio
                // categoria/sorgente non si vedono gli elementi “costruirsi/distruggersi”.
                using var br = new SolidBrush(Color.FromArgb(255, 10, 10, 14));
                e.Graphics.FillRectangle(br, ClientRectangle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                int w = Width;
                int h = Height;
                if (w <= 0 || h <= 0) return;

                if (!_showSpinner && string.IsNullOrWhiteSpace(_message))
                    return;

                int cx = w / 2;
                int cy = h / 2 - 10;
                int textTop = cy;

                if (_showSpinner)
                {
                    int r = 32;
                    var rect = new Rectangle(cx - r / 2, cy - r / 2, r, r);

                    using (var p = new Pen(Theme.Accent, 3)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    })
                    {
                        g.DrawArc(p, rect, _angle, 300);
                    }

                    textTop = cy + r / 2 + 8;
                }

                if (!string.IsNullOrWhiteSpace(_message))
                {
                    using var f = new Font("Segoe UI", 11f);
                    var sz = TextRenderer.MeasureText(_message, f);
                    var txtRect = new Rectangle(
                        cx - sz.Width / 2,
                        textTop,
                        sz.Width,
                        sz.Height);

                    TextRenderer.DrawText(
                        g,
                        _message,
                        f,
                        txtRect,
                        Theme.SubtleText,
                        TextFormatFlags.HorizontalCenter
                      | TextFormatFlags.VerticalCenter
                      | TextFormatFlags.EndEllipsis);
                }
            }
        }


        // ------------ FileCard (griglia e carosello) ------------
        private sealed class FileCard : Control
        {
            private static readonly SemaphoreSlim _thumbGate = new SemaphoreSlim(4, 4);

            private readonly string _path;
            private string _displayName;
            private string? _subtitle;
            private readonly DBPictureBox _img;
            private readonly IconButton? _starBtn;
            private bool _fav;
            private readonly Action<string, bool>? _favSetter;
            private readonly Action _openAction;
            private readonly int _imgHeight;
            private bool _hover;
            private double _progress;
            private bool _suppressNextClickAction;
            public string FilePath => _path;

            public FileCard(
                string path,
                bool showFavorite,
                bool favInit,
                Action<string, bool>? onFavToggle,
                Action clickOpen,
                int cardWidth,
                int cardHeight,
                int imgHeight)
            {
                _path = path;
                _displayName = string.Empty;
                _subtitle = null;
                ApplyDisplayName(Path.GetFileNameWithoutExtension(path) ?? Path.GetFileName(path), path);
                _openAction = clickOpen;
                _imgHeight = imgHeight;
                _fav = favInit;
                _favSetter = onFavToggle;

                Size = new Size(cardWidth, cardHeight);
                Margin = new Padding(10, 6, 10, 6);

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.Selectable, true);

                BackColor = Theme.Card;
                Cursor = Cursors.Hand;
                TabStop = true;

                _img = new DBPictureBox
                {
                    Location = new Point(0, 0),
                    Size = new Size(cardWidth, imgHeight),
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                _img.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _openAction(); };
                _img.MouseDown += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                        _suppressNextClickAction = true;
                };
                _img.MouseUp += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                    {
                        _suppressNextClickAction = true;
                        ShowContextMenuAt(_img, e.Location);
                    }
                };
                _img.MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                _img.MouseLeave += (_, __) => { _hover = false; Invalidate(); };
                _img.MouseMove += (_, __) => { try { FindHostingFlow()?.NotifyPointerMoveFromChild(); } catch { } };
                Controls.Add(_img);

                if (showFavorite)
                {
                    _starBtn = new IconButton(favInit ? IconButton.Kind.StarFilled : IconButton.Kind.Star)
                    {
                        Size = new Size(22, 22),
                        BackColor = Color.Transparent,
                        TabStop = true
                    };
                    _starBtn.Click += (_, __) =>
                    {
                        _fav = !_fav;
                        _favSetter?.Invoke(_path, _fav);
                        _starBtn.SetKind(_fav ? IconButton.Kind.StarFilled : IconButton.Kind.Star);
                        Invalidate();
                    };
                    _starBtn.MouseDown += (_, e) =>
                    {
                        if (e.Button == MouseButtons.Right)
                            _suppressNextClickAction = true;
                    };
                    _starBtn.MouseUp += (_, e) =>
                    {
                        if (e.Button == MouseButtons.Right)
                        {
                            _suppressNextClickAction = true;
                            ShowContextMenuAt(_starBtn, e.Location);
                        }
                    };
                    Controls.Add(_starBtn);
                }

                Resize += (_, __) => LayoutInternal();

                MouseDown += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                        _suppressNextClickAction = true;
                };
                MouseUp += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                    {
                        _suppressNextClickAction = true;
                        ShowContextMenuAt(this, e.Location);
                    }
                };
                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; Invalidate(); };
                MouseMove += (_, __) => { try { FindHostingFlow()?.NotifyPointerMoveFromChild(); } catch { } };
            }

            private SkinnedFlow? FindHostingFlow()
            {
                Control? p = Parent;
                while (p != null && p is not SkinnedFlow)
                    p = p.Parent;
                return p as SkinnedFlow;
            }

            private bool IsHoverVisualActive()
            {
                var flow = FindHostingFlow();
                if (flow == null)
                    return _hover;
                return _hover && !flow.IsHoverTrackingSuspended;
            }

            public void SetFavoriteState(bool fav)
            {
                _fav = fav;
                try { _starBtn?.SetKind(_fav ? IconButton.Kind.StarFilled : IconButton.Kind.Star); }
                catch { }
                Invalidate();
            }

            public void SetItemContextMenu(ContextMenuStrip? menu, object? tag)
            {
                ContextMenuStrip = menu;
                Tag = tag;

                try
                {
                    _img.Tag = tag;
                }
                catch { }

                try
                {
                    if (_starBtn != null)
                    {
                        _starBtn.Tag = tag;
                    }
                }
                catch { }
            }

            private void ShowContextMenuAt(Control source, Point location)
            {
                try
                {
                    var menu = ContextMenuStrip;
                    if (menu == null || menu.IsDisposed)
                        return;

                    try { Focus(); } catch { }
                    Point clientPoint = source == this
                        ? location
                        : PointToClient(source.PointToScreen(location));
                    clientPoint.X = Math.Max(12, Math.Min(Math.Max(12, Width - 12), clientPoint.X));
                    clientPoint.Y = Math.Max(12, Math.Min(Math.Max(12, Height - 12), clientPoint.Y));
                    menu.Show(this, clientPoint);
                }
                catch { }
            }

            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                if (_suppressNextClickAction)
                {
                    _suppressNextClickAction = false;
                    return;
                }
                _openAction();
            }

            protected override bool IsInputKey(Keys keyData)
            {
                var keyCode = keyData & Keys.KeyCode;
                if (keyCode == Keys.Enter || keyCode == Keys.Space || keyCode == Keys.Apps)
                    return true;
                return base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);

                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    _openAction();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Apps)
                {
                    try { ContextMenuStrip?.Show(this, new Point(Math.Max(12, Width / 2), Math.Max(12, Height / 2))); } catch { }
                    e.Handled = true;
                }
            }

            private static string NormalizeCodeToken(int? season, int? episode)
            {
                if (season.HasValue && episode.HasValue)
                    return $"S{season.Value:00}E{episode.Value:00}";
                if (episode.HasValue)
                    return $"E{episode.Value:00}";
                if (season.HasValue)
                    return $"S{season.Value:00}";
                return string.Empty;
            }

            private static string CleanLine(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return string.Empty;

                return Regex.Replace(value, @"\s+", " ").Trim(' ', '-', '–', '—', '•');
            }

            private static string ExtractEpisodeTitleFromDisplay(string candidate)
            {
                string normalized = CleanLine(candidate);
                if (string.IsNullOrWhiteSpace(normalized))
                    return string.Empty;

                var patterns = new[]
                {
                    @"^(?<series>.+?)\s*•\s*(?<code>S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3})\s*[-–—]\s*(?<title>.+)$",
                    @"^(?<series>.+?)\s+(?<code>S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3})\s*[-–—]\s*(?<title>.+)$",
                    @"^(?<code>S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3})\s*[-–—]\s*(?<title>.+)$",
                    @"^(?<series>.+?)\s*•\s*Stagione\s*\d{1,2}\s*•\s*(?<title>.+)$"
                };

                foreach (var pattern in patterns)
                {
                    var match = Regex.Match(normalized, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string title = CleanLine(match.Groups["title"].Value);
                        if (!string.IsNullOrWhiteSpace(title))
                            return title;
                    }
                }

                return normalized;
            }

            private static void BuildCardText(string? name, string path, out string primary, out string? subtitle)
            {
                string candidate = string.IsNullOrWhiteSpace(name)
                    ? (Path.GetFileNameWithoutExtension(path) ?? string.Empty)
                    : name.Trim();

                primary = CleanLine(candidate);
                subtitle = null;

                try
                {
                    var parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(path);
                    if (parsed != null && parsed.IsTvEpisode)
                    {
                        string seriesTitle = CleanLine(parsed.SeriesTitle);
                        if (string.IsNullOrWhiteSpace(seriesTitle))
                            seriesTitle = "Serie TV";

                        string code = NormalizeCodeToken(parsed.SeasonNumber, parsed.EpisodeNumber);
                        subtitle = string.IsNullOrWhiteSpace(code)
                            ? seriesTitle
                            : $"{seriesTitle} • {code}";

                        string episodeTitle = ExtractEpisodeTitleFromDisplay(candidate);
                        if (!string.IsNullOrWhiteSpace(parsed.EpisodeTitle))
                        {
                            string parsedEpisodeTitle = CleanLine(parsed.EpisodeTitle);
                            if (string.IsNullOrWhiteSpace(episodeTitle) ||
                                string.Equals(episodeTitle, seriesTitle, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(episodeTitle, CleanLine(candidate), StringComparison.OrdinalIgnoreCase))
                            {
                                episodeTitle = parsedEpisodeTitle;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(episodeTitle))
                        {
                            if (string.Equals(episodeTitle, seriesTitle, StringComparison.OrdinalIgnoreCase))
                                episodeTitle = string.Empty;
                            else if (!string.IsNullOrWhiteSpace(code) && string.Equals(episodeTitle, code, StringComparison.OrdinalIgnoreCase))
                                episodeTitle = string.Empty;
                        }

                        if (string.IsNullOrWhiteSpace(episodeTitle))
                        {
                            episodeTitle = !string.IsNullOrWhiteSpace(parsed.EpisodeTitle)
                                ? CleanLine(parsed.EpisodeTitle)
                                : (!string.IsNullOrWhiteSpace(code) ? code : primary);
                        }

                        primary = string.IsNullOrWhiteSpace(episodeTitle) ? seriesTitle : episodeTitle;
                        return;
                    }
                }
                catch { }

                primary = string.IsNullOrWhiteSpace(primary)
                    ? (Path.GetFileNameWithoutExtension(path) ?? path)
                    : primary;
            }

            private void ApplyDisplayName(string? name, string path)
            {
                BuildCardText(name, path, out _displayName, out _subtitle);
            }

            public void SetDisplayName(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;

                string previousDisplayName = _displayName;
                string? previousSubtitle = _subtitle;
                ApplyDisplayName(name, _path);

                if (string.Equals(previousDisplayName, _displayName, StringComparison.Ordinal) &&
                    string.Equals(previousSubtitle ?? string.Empty, _subtitle ?? string.Empty, StringComparison.Ordinal))
                    return;

                Invalidate();
            }

            public void SetProgress(double progress01)
            {
                _progress = Math.Max(0.0, Math.Min(1.0, progress01));
                Invalidate();
            }

            private void LayoutInternal()
            {
                _img.Size = new Size(Width, _imgHeight);
                _img.Location = new Point(0, 0);

                if (_starBtn != null)
                {
                    int footerY = _imgHeight;
                    int footerH = Height - _imgHeight;
                    _starBtn.Left = Width - _starBtn.Width - 8;
                    _starBtn.Top = footerY + (footerH - _starBtn.Height) / 2;
                }
            }

            public void SetInitialPlaceholder(Bitmap bmp)
            {
                if (_img.IsDisposed || bmp == null) return;
                Bitmap? clone = null;
                try { clone = new Bitmap(bmp); } catch { }
                if (clone == null) return;
                try { _img.Image?.Dispose(); } catch { }
                _img.Image = clone;
            }

            public void SetImage(Bitmap bmp)
            {
                if (bmp == null) return;
                if (_img.IsDisposed)
                {
                    try { bmp.Dispose(); } catch { }
                    return;
                }

                try { _img.Image?.Dispose(); } catch { }
                _img.Image = bmp;
            }

            public void BeginThumbLoad(CancellationToken ct)
            {
                Task.Run(async () =>
                {
                    bool acquired = false;
                    try
                    {
                        if (ct.IsCancellationRequested) return;

                        await _thumbGate.WaitAsync(ct).ConfigureAwait(false);
                        acquired = true;

                        if (ct.IsCancellationRequested) return;

                        Bitmap? bmp = TryLoadThumb(_path, Math.Max(520, Width));

                        if (bmp == null)
                        {
                            var cat = CategoryFromExt((Path.GetExtension(_path) ?? "").ToLowerInvariant());
                            var sharedPlaceholder = GetCategoryPlaceholder(cat, Math.Max(520, Width));
                            try { bmp = new Bitmap(sharedPlaceholder); } catch { bmp = null; }
                        }

                        if (ct.IsCancellationRequested)
                        {
                            bmp?.Dispose();
                            return;
                        }

                        if (bmp != null && _img.IsHandleCreated && !_img.IsDisposed)
                        {
                            _img.BeginInvoke(new Action(() =>
                            {
                                if (_img.IsDisposed)
                                {
                                    try { bmp.Dispose(); } catch { }
                                    return;
                                }

                                try { _img.Image?.Dispose(); } catch { }
                                _img.Image = bmp;
                            }));
                        }
                        else
                        {
                            bmp?.Dispose();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                    finally
                    {
                        if (acquired)
                        {
                            try { _thumbGate.Release(); } catch { }
                        }
                    }
                }, ct);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                bool hoverActive = IsHoverVisualActive();

                using (var bg = new SolidBrush(hoverActive ? Theme.PanelAlt : Theme.Card))
                    g.FillRectangle(bg, new Rectangle(0, 0, Width - 1, Height - 1));

                using (var penBorder = new Pen(Theme.Border))
                    g.DrawRectangle(penBorder, 0, 0, Width - 1, Height - 1);

                int footerY = _imgHeight;
                int footerH = Height - _imgHeight;
                var footerRect = new Rectangle(0, footerY, Width, footerH);

                using (var footerBg = new SolidBrush(hoverActive ? Theme.PanelAlt : Color.FromArgb(36, 36, 40)))
                    g.FillRectangle(footerBg, footerRect);

                int barHeight = 4;
                if (_progress > 0.001)
                {
                    double p = Math.Max(0.0, Math.Min(1.0, _progress));
                    var trackRect = new Rectangle(footerRect.Left, footerRect.Top, footerRect.Width, barHeight);

                    using (var trackBg = new SolidBrush(Color.FromArgb(80, Theme.Border)))
                        g.FillRectangle(trackBg, trackRect);

                    int filledW = (int)Math.Round(trackRect.Width * p);
                    if (filledW > 0)
                    {
                        var fillRect = new Rectangle(trackRect.Left, trackRect.Top, filledW, trackRect.Height);
                        using var fillBr = new SolidBrush(Theme.Accent);
                        g.FillRectangle(fillBr, fillRect);
                    }
                }

                int rightPad = _starBtn != null ? (_starBtn.Width + 18) : 12;
                int leftPad = 10;
                int textTop = footerY + 8;
                int textWidth = Math.Max(24, Width - leftPad - rightPad);
                bool hasSubtitle = !string.IsNullOrWhiteSpace(_subtitle);

                using var titleFont = new Font("Segoe UI Semibold", hasSubtitle ? 9.75f : 10f);
                using var subtitleFont = new Font("Segoe UI", 8.5f);

                if (hasSubtitle)
                {
                    var titleRect = new Rectangle(leftPad, textTop, textWidth, 22);
                    var subtitleRect = new Rectangle(leftPad, textTop + 21, textWidth, Math.Max(18, footerH - 30));

                    TextRenderer.DrawText(
                        g,
                        _displayName,
                        titleFont,
                        titleRect,
                        Color.White,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                    TextRenderer.DrawText(
                        g,
                        _subtitle,
                        subtitleFont,
                        subtitleRect,
                        Theme.SubtleText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
                else
                {
                    var textRect = new Rectangle(leftPad, footerY + 6, textWidth, footerH - 12);
                    TextRenderer.DrawText(
                        g,
                        _displayName,
                        titleFont,
                        textRect,
                        Color.White,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }

                var ext = (Path.GetExtension(_path) ?? "")
                    .Trim('.')
                    .ToUpperInvariant();

                if (!string.IsNullOrEmpty(ext))
                {
                    var badge = $" {ext} ";
                    using var badgeFont = new Font("Segoe UI Semibold", 8.5f);
                    var sz = g.MeasureString(badge, badgeFont);
                    int bx = Width - (int)sz.Width - 12;
                    int by = 10;

                    using var brBadgeBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                    using var brBadgeFg = new SolidBrush(Color.White);

                    g.FillRectangle(brBadgeBg, new Rectangle(bx - 4, by - 2, (int)sz.Width + 8, (int)sz.Height + 4));
                    g.DrawString(badge, badgeFont, brBadgeFg, bx, by);
                }
            }
        }

        private sealed class SeasonSelectorCard : Control
        {
            private readonly DBPictureBox _img;
            private readonly Action<TvSeasonGroup, string?> _showEpisodePicker;
            private readonly TvSeasonGroup _group;
            private readonly int _imgHeight;
            private readonly int? _seasonNumber;
            private string _displayName;
            private string _subtitle;
            private bool _hover;
            private bool _focused;
            private bool _suppressNextClickAction;

            public string RepresentativePath { get; }

            public SeasonSelectorCard(
                TvSeasonGroup group,
                Action<TvSeasonGroup, string?> showEpisodePicker,
                int cardWidth,
                int cardHeight,
                int imgHeight)
            {
                _group = group;
                RepresentativePath = group.RepresentativePath;
                _showEpisodePicker = showEpisodePicker;
                _imgHeight = imgHeight;
                _seasonNumber = group.SeasonNumber;
                _displayName = !string.IsNullOrWhiteSpace(group.SeriesTitle)
                    ? group.SeriesTitle.Trim()
                    : (!string.IsNullOrWhiteSpace(group.DisplayName) ? group.DisplayName : "Serie TV");
                _subtitle = BuildSubtitle(group);

                Size = new Size(cardWidth, cardHeight);
                Margin = new Padding(10, 6, 10, 6);
                TabStop = true;

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.Selectable, true);

                BackColor = Theme.Card;
                Cursor = Cursors.Hand;

                _img = new DBPictureBox
                {
                    Location = new Point(0, 0),
                    Size = new Size(cardWidth, imgHeight),
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                _img.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowEpisodePicker(); };
                _img.MouseDown += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                        _suppressNextClickAction = true;
                };
                _img.MouseUp += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                    {
                        _suppressNextClickAction = true;
                        ShowContextMenuAt(_img, e.Location);
                    }
                };
                _img.MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                _img.MouseLeave += (_, __) => { _hover = false; Invalidate(); };
                _img.MouseMove += (_, __) => { try { FindHostingFlow()?.NotifyPointerMoveFromChild(); } catch { } };
                Controls.Add(_img);

                Resize += (_, __) => LayoutInternal();
                MouseDown += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                        _suppressNextClickAction = true;
                };
                MouseUp += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                    {
                        _suppressNextClickAction = true;
                        ShowContextMenuAt(this, e.Location);
                    }
                };
                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; Invalidate(); };
                MouseMove += (_, __) => { try { FindHostingFlow()?.NotifyPointerMoveFromChild(); } catch { } };

                LayoutInternal();
            }

            private SkinnedFlow? FindHostingFlow()
            {
                Control? p = Parent;
                while (p != null && p is not SkinnedFlow)
                    p = p.Parent;
                return p as SkinnedFlow;
            }

            private bool IsHoverVisualActive()
            {
                var flow = FindHostingFlow();
                if (flow == null)
                    return _hover;
                return _hover && !flow.IsHoverTrackingSuspended;
            }

            public void SetItemContextMenu(ContextMenuStrip? menu, object? tag)
            {
                ContextMenuStrip = menu;
                Tag = tag;

                try
                {
                    _img.Tag = tag;
                }
                catch { }
            }

            private void ShowContextMenuAt(Control source, Point location)
            {
                try
                {
                    var menu = ContextMenuStrip;
                    if (menu == null || menu.IsDisposed)
                        return;

                    try { Focus(); } catch { }
                    Point clientPoint = source == this
                        ? location
                        : PointToClient(source.PointToScreen(location));
                    clientPoint.X = Math.Max(12, Math.Min(Math.Max(12, Width - 12), clientPoint.X));
                    clientPoint.Y = Math.Max(12, Math.Min(Math.Max(12, Height - 12), clientPoint.Y));
                    menu.Show(this, clientPoint);
                }
                catch { }
            }

            private static string BuildSubtitle(TvSeasonGroup group)
            {
                string seasonLabel = group.SeasonNumber.HasValue
                    ? $"Stagione {group.SeasonNumber.Value:00}"
                    : "Speciali";
                string countLabel = group.Episodes.Count == 1 ? "1 episodio" : $"{group.Episodes.Count} episodi";
                return seasonLabel + " • " + countLabel;
            }

            private static string SanitizeSeriesTitle(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return string.Empty;

                string cleaned = name.Trim();
                int bulletIndex = cleaned.IndexOf('•');
                if (bulletIndex > 0)
                {
                    string head = cleaned.Substring(0, bulletIndex).Trim();
                    if (!string.IsNullOrWhiteSpace(head))
                        return head;
                }

                var match = Regex.Match(cleaned, @"^(?<title>.+?)\s+(?:S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3})\b", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string head = match.Groups["title"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(head))
                        return head;
                }

                return cleaned;
            }

            private void ShowEpisodePicker()
            {
                _showEpisodePicker(_group, _displayName);
            }

            public void SetDisplayName(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;

                string sanitized = SanitizeSeriesTitle(name);
                if (string.Equals(_displayName, sanitized, StringComparison.Ordinal))
                    return;

                _displayName = sanitized;
                Invalidate();
            }

            private void LayoutInternal()
            {
                _img.Size = new Size(Width, _imgHeight);
                _img.Location = new Point(0, 0);
            }

            public void SetInitialPlaceholder(Bitmap bmp)
            {
                if (_img.IsDisposed || bmp == null) return;
                Bitmap? clone = null;
                try { clone = new Bitmap(bmp); } catch { }
                if (clone == null) return;
                try { _img.Image?.Dispose(); } catch { }
                _img.Image = clone;
            }

            public void SetImage(Bitmap bmp)
            {
                if (bmp == null) return;
                if (_img.IsDisposed)
                {
                    try { bmp.Dispose(); } catch { }
                    return;
                }

                try { _img.Image?.Dispose(); } catch { }
                _img.Image = bmp;
            }

            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                if (_suppressNextClickAction)
                {
                    _suppressNextClickAction = false;
                    return;
                }
                ShowEpisodePicker();
            }

            protected override bool IsInputKey(Keys keyData)
            {
                var keyCode = keyData & Keys.KeyCode;
                if (keyCode == Keys.Enter || keyCode == Keys.Space || keyCode == Keys.F4 || keyCode == Keys.Apps)
                    return true;
                return base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);

                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || e.KeyCode == Keys.F4)
                {
                    ShowEpisodePicker();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Apps)
                {
                    try { ContextMenuStrip?.Show(this, new Point(Math.Max(12, Width / 2), Math.Max(12, Height / 2))); } catch { }
                    e.Handled = true;
                }
            }

            protected override void OnGotFocus(EventArgs e)
            {
                base.OnGotFocus(e);
                _focused = true;
                Invalidate();
            }

            protected override void OnLostFocus(EventArgs e)
            {
                base.OnLostFocus(e);
                _focused = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                bool hoverActive = IsHoverVisualActive();

                using (var bg = new SolidBrush(hoverActive ? Theme.PanelAlt : Theme.Card))
                    g.FillRectangle(bg, new Rectangle(0, 0, Width - 1, Height - 1));

                using (var penBorder = new Pen(_focused ? Theme.Accent : Theme.Border))
                    g.DrawRectangle(penBorder, 0, 0, Width - 1, Height - 1);

                int footerY = _imgHeight;
                int footerH = Height - _imgHeight;
                var footerRect = new Rectangle(0, footerY, Width, footerH);

                using (var footerBg = new SolidBrush(hoverActive
                    ? Theme.PanelAlt
                    : Color.FromArgb(36, 36, 40)))
                {
                    g.FillRectangle(footerBg, footerRect);
                }

                using var titleFont = new Font("Segoe UI Semibold", 10f);
                using var subFont = new Font("Segoe UI", 8.5f);

                var titleRect = new Rectangle(10, footerY + 8, Width - 20, 18);
                var subRect = new Rectangle(10, footerY + 28, Width - 20, 15);

                TextRenderer.DrawText(
                    g,
                    _displayName,
                    titleFont,
                    titleRect,
                    Color.White,
                    TextFormatFlags.Left
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis
                  | TextFormatFlags.NoPadding);

                TextRenderer.DrawText(
                    g,
                    _subtitle,
                    subFont,
                    subRect,
                    Theme.SubtleText,
                    TextFormatFlags.Left
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis
                  | TextFormatFlags.NoPadding);

                string badge = _seasonNumber.HasValue
                    ? $" ST. {_seasonNumber.Value:00} "
                    : " TV ";
                using var badgeFont = new Font("Segoe UI Semibold", 8.5f);
                var badgeSize = g.MeasureString(badge, badgeFont);
                int bx = Width - (int)badgeSize.Width - 12;
                int by = 10;
                using var brBadgeBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                using var brBadgeFg = new SolidBrush(Color.White);
                g.FillRectangle(
                    brBadgeBg,
                    new Rectangle(
                        bx - 4,
                        by - 2,
                        (int)badgeSize.Width + 8,
                        (int)badgeSize.Height + 4));
                g.DrawString(badge, badgeFont, brBadgeFg, bx, by);
            }
        }


        // ------------ VIEWPORT CAROSELLO (Recenti) ------------
        private sealed class CarouselViewport : Panel
        {
            private readonly FlowLayoutPanel _flow;
            private int _offsetX; // scroll orizzontale manuale

            public CarouselViewport()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw, true);

                BackColor = Color.Black;
                AutoScroll = false; // no scrollbar Windows

                _flow = new FlowLayoutPanel
                {
                    WrapContents = false,
                    FlowDirection = FlowDirection.LeftToRight,
                    Margin = new Padding(0),
                    Padding = new Padding(0),
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = Color.Black,
                    Location = new Point(0, 0)
                };

                Controls.Add(_flow);
                Anchor = AnchorStyles.Top;
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                ClampOffset();
                UpdateFlowPosition();
            }

            public void ResetItems(
                List<string> paths,
                CancellationToken token,
                Action<string> openCb,
                Action<string, FileCard> initThumb)
            {
                SuspendLayout();
                _flow.SuspendLayout();

                _flow.Controls.Clear();

                foreach (var p in paths)
                {
                    var card = new FileCard(
                        path: p,
                        showFavorite: false,
                        favInit: false,
                        onFavToggle: null,
                        clickOpen: () => openCb(p),
                        cardWidth: 300,
                        cardHeight: 236,
                        imgHeight: 170
                    );

                    initThumb(p, card);
                    _flow.Controls.Add(card);
                }

                _flow.ResumeLayout(true);

                _offsetX = 0;
                ClampOffset();
                UpdateFlowPosition();

                ResumeLayout(true);
                Invalidate();
            }

            public void StepItems(int dir)
            {
                int iw = GetItemOuterWidthEstimate();
                if (iw < 1) return;
                _offsetX += dir * iw;
                ClampOffset();
                UpdateFlowPosition();
            }

            public void StopAnimation(bool snapToTarget)
            {
                ClampOffset();
                UpdateFlowPosition();
            }

            public int GetItemOuterWidthEstimate()
            {
                var c = _flow.Controls.Cast<Control>().FirstOrDefault();
                if (c == null) return 320;
                return c.Width + c.Margin.Left + c.Margin.Right;
            }

            public int GetPreferredHeightEstimate()
            {
                var c = _flow.Controls.Cast<Control>().FirstOrDefault();
                if (c == null) return 236;
                return c.Height + c.Margin.Top + c.Margin.Bottom;
            }

            private void UpdateFlowPosition()
            {
                _flow.Location = new Point(-_offsetX, 0);
            }

            private void ClampOffset()
            {
                int contentW = TotalContentWidth();
                int viewW = ClientSize.Width;
                if (contentW <= viewW)
                {
                    _offsetX = 0;
                }
                else
                {
                    if (_offsetX < 0) _offsetX = 0;
                    int maxOff = contentW - viewW;
                    if (_offsetX > maxOff) _offsetX = maxOff;
                }
            }

            private int TotalContentWidth()
            {
                int tot = 0;
                foreach (Control c in _flow.Controls)
                    tot += c.Width + c.Margin.Left + c.Margin.Right;
                return tot;
            }

            public void EnsureChildVisible(Control anyDescendant)
            {
                if (anyDescendant == null || anyDescendant.IsDisposed) return;

                Control? item = anyDescendant;
                while (item != null && item.Parent != _flow)
                    item = item.Parent;

                if (item == null) return;

                int viewW = ClientSize.Width;
                if (viewW <= 0) viewW = Width;
                if (viewW <= 0) return;

                int pad = 12;
                int left = item.Left - item.Margin.Left;
                int right = item.Right + item.Margin.Right;
                int leftInView = left - _offsetX;
                int rightInView = right - _offsetX;

                bool changed = false;
                if (leftInView < pad)
                {
                    _offsetX = Math.Max(0, left - pad);
                    changed = true;
                }
                else if (rightInView > viewW - pad)
                {
                    _offsetX = Math.Max(0, right - (viewW - pad));
                    changed = true;
                }

                if (changed)
                {
                    ClampOffset();
                    UpdateFlowPosition();
                }
            }

            public IEnumerable<FileCard> GetCards()
                => _flow.Controls.OfType<FileCard>();

            public int ItemsCount => _flow.Controls.Count;

            public bool HasHorizontalOverflow()
            {
                int contentW = TotalContentWidth();
                int viewW = ClientSize.Width;
                if (viewW <= 0) viewW = Width;
                return contentW > viewW && contentW > 0;
            }
        }


        // ------------ ICON BUTTON (frecce carosello / stellina preferiti) ------------
        private sealed class IconButton : Control
        {
            public enum Kind { ChevronLeft, ChevronRight, Star, StarFilled }
            private Kind _kind;
            private bool _hover, _down;

            public IconButton(Kind k)
            {
                _kind = k;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.SupportsTransparentBackColor
                       | ControlStyles.ResizeRedraw, true);

                Cursor = Cursors.Hand;
                Size = new Size(42, 42);
                BackColor = Color.Transparent;
                TabStop = false;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, __) => { _down = true; Invalidate(); };
                MouseUp += (_, __) => { _down = false; Invalidate(); };
            }

            public void SetKind(Kind k) { _kind = k; Invalidate(); }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Color.Transparent);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                if (_kind is Kind.ChevronLeft or Kind.ChevronRight)
                {
                    var bg = _down ? Color.FromArgb(190, Theme.Accent)
                                   : _hover ? Color.FromArgb(160, Theme.Accent)
                                            : Color.FromArgb(120, Theme.Accent);

                    using var b = new SolidBrush(bg);
                    g.FillEllipse(b, 0, 0, Width - 1, Height - 1);

                    using var p = new Pen(Color.White, 3f)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    var cx = Width / 2f;
                    var cy = Height / 2f;
                    if (_kind == Kind.ChevronLeft)
                    {
                        g.DrawLines(p, new[]
                        {
                            new PointF(cx + 5, cy - 9),
                            new PointF(cx - 5, cy),
                            new PointF(cx + 5, cy + 9)
                        });
                    }
                    else
                    {
                        g.DrawLines(p, new[]
                        {
                            new PointF(cx - 5, cy - 9),
                            new PointF(cx + 5, cy),
                            new PointF(cx - 5, cy + 9)
                        });
                    }
                }
                else
                {
                    // stellina preferiti
                    var color = _kind == Kind.StarFilled ? Color.Gold : Color.White;
                    var r = new RectangleF(
                        (Width - 18) / 2f,
                        (Height - 18) / 2f,
                        18,
                        18);
                    GraphicsUtil.DrawStar(g, r, color, fill: _kind == Kind.StarFilled);
                }
            }
        }
        private void RefreshVisibleCardTitlesFromPosterIndex()
        {
            try
            {
                foreach (Control ctrl in _grid.Controls)
                {
                    if (ctrl is FileCard fileCard)
                    {
                        string best = MovieMetadataService.GetBestKnownDisplayTitle(fileCard.FilePath);
                        if (!string.IsNullOrWhiteSpace(best))
                            fileCard.SetDisplayName(best);
                    }
                    else if (ctrl is SeasonSelectorCard seasonCard)
                    {
                        string best = MovieMetadataService.GetBestKnownDisplayTitle(seasonCard.RepresentativePath);
                        if (!string.IsNullOrWhiteSpace(best))
                            seasonCard.SetDisplayName(best);
                    }
                }
            }
            catch { }

            try
            {
                foreach (var card in _carouselViewport.GetCards())
                {
                    string best = MovieMetadataService.GetBestKnownDisplayTitle(card.FilePath);
                    if (!string.IsNullOrWhiteSpace(best))
                        card.SetDisplayName(best);
                }
            }
            catch { }

            try { RefreshSeasonEpisodeOverlayTitlesFromCache(); } catch { }
        }

        private void RefreshVisibleCardImagesFromPosterIndex()
        {
            try
            {
                foreach (Control ctrl in _grid.Controls)
                {
                    if (ctrl is FileCard fileCard)
                    {
                        string? poster = MovieMetadataService.GetCachedPosterPath(fileCard.FilePath);
                        if (!string.IsNullOrWhiteSpace(poster) && File.Exists(poster))
                        {
                            var bmp = LoadBitmapClone(poster);
                            if (bmp != null)
                                ApplyBitmapToCard(fileCard, bmp);
                        }
                    }
                    else if (ctrl is SeasonSelectorCard seasonCard)
                    {
                        string? poster = MovieMetadataService.GetCachedPosterPath(seasonCard.RepresentativePath);
                        if (!string.IsNullOrWhiteSpace(poster) && File.Exists(poster))
                        {
                            var bmp = LoadBitmapClone(poster);
                            if (bmp != null)
                                ApplyBitmapToCard(seasonCard, bmp);
                        }
                    }
                }
            }
            catch { }

            try
            {
                foreach (var card in _carouselViewport.GetCards())
                {
                    string? poster = MovieMetadataService.GetCachedPosterPath(card.FilePath);
                    if (!string.IsNullOrWhiteSpace(poster) && File.Exists(poster))
                    {
                        var bmp = LoadBitmapClone(poster);
                        if (bmp != null)
                            ApplyBitmapToCard(card, bmp);
                    }
                }
            }
            catch { }
        }

        private readonly System.Windows.Forms.Timer _posterRefreshDebounce = new() { Interval = 160 };
        private bool _posterRefreshDebounceHooked;

        // chiamato quando MovieMetadataService segnala che l'indice poster è stato aggiornato
        private void OnPostersChanged()
        {
            if (IsDisposed)
                return;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed)
                        return;

                    if (!_posterRefreshDebounceHooked)
                    {
                        _posterRefreshDebounceHooked = true;
                        _posterRefreshDebounce.Tick += (_, __) =>
                        {
                            try
                            {
                                _posterRefreshDebounce.Stop();
                                if (IsDisposed)
                                    return;

                                if (_mask.Visible || _progressiveTimer.Enabled || !_grid.Visible)
                                    return;

                                RefreshVisibleCardImagesFromPosterIndex();
                                RefreshVisibleCardTitlesFromPosterIndex();
                            }
                            catch { }
                        };
                    }

                    _posterRefreshDebounce.Stop();
                    _posterRefreshDebounce.Start();
                }));
            }
            catch { }
        }


        // ------------ Flow / scrollbar custom ------------
        private class BetterFlow : FlowLayoutPanel
        {
            public bool HideHScroll { get; set; }
            public bool HideVScroll { get; set; }

            public BetterFlow()
            {
                typeof(Panel)
                    .GetProperty("DoubleBuffered",
                        System.Reflection.BindingFlags.Instance
                      | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(this, true, null);

                SetStyle(ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.ResizeRedraw, true);
                UpdateStyles();
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_VSCROLL = 0x00200000;
                    const int WS_HSCROLL = 0x00100000;
                    var cp = base.CreateParams;
                    cp.Style &= ~WS_VSCROLL;
                    cp.Style &= ~WS_HSCROLL;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                HideBars();
            }
            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);
                HideBars();
            }
            protected override void OnResize(EventArgs eventargs)
            {
                base.OnResize(eventargs);
                HideBars();
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);

                const int WM_VSCROLL = 0x0115;
                const int WM_HSCROLL = 0x0114;
                const int WM_MOUSEWHEEL = 0x020A;
                const int WM_MOUSEHWHEEL = 0x020E;
                const int WM_PAINT = 0x000F;
                const int WM_NCPAINT = 0x0085;
                const int WM_WINDOWPOSCHANGED = 0x0047;

                if (m.Msg == WM_VSCROLL || m.Msg == WM_HSCROLL ||
                    m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL ||
                    m.Msg == WM_PAINT || m.Msg == WM_NCPAINT ||
                    m.Msg == WM_WINDOWPOSCHANGED)
                {
                    HideBars();
                }
            }

            public void ForceHideScrollbars() => HideBars();

            protected void HideBars()
            {
                if (!IsHandleCreated) return;
                if (HideHScroll) Win32.ShowScrollBar(Handle, Win32.SB_HORZ, false);
                if (HideVScroll) Win32.ShowScrollBar(Handle, Win32.SB_VERT, false);
            }
        }

        private sealed class SkinnedFlow : BetterFlow
        {
            private ThemedVScroll? _skin;
            private bool _hoverTrackingSuspended;
            private Point _lastMousePoint = new Point(int.MinValue, int.MinValue);

            public bool UseThemedVScroll { get; set; }
            public bool IsHoverTrackingSuspended => _hoverTrackingSuspended;

            public event EventHandler? ScrollStateChanged;

            public SkinnedFlow()
            {
                HideHScroll = true;
                HideVScroll = true;
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                try
                {
                    if (e != null && DisplayRectangle.Height > ClientSize.Height)
                    {
                        int cur = GetScrollY();
                        int notches = Math.Max(1, Math.Abs(e.Delta) / Math.Max(1, SystemInformation.MouseWheelScrollDelta));
                        int step = Math.Max(84, ClientSize.Height / 7) * notches;
                        int direction = e.Delta > 0 ? -1 : 1;
                        SetScrollValueImmediate(cur + (direction * step));
                    }
                }
                catch { }
            }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                if (UseThemedVScroll)
                {
                    _skin = new ThemedVScroll
                    {
                        Dock = DockStyle.Right,
                        Width = 12
                    };

                    Controls.Add(_skin);
                    _skin.BringToFront();

                    _skin.ScrollTo += v =>
                    {
                        SuspendHoverTrackingUntilMouseMove();
                        SetScrollValueImmediate(v);
                    };
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);

                if (_hoverTrackingSuspended &&
                    (_lastMousePoint.X != e.X || _lastMousePoint.Y != e.Y))
                {
                    ResumeHoverTracking();
                }

                _lastMousePoint = e.Location;
            }

            protected override void OnScroll(ScrollEventArgs se)
            {
                base.OnScroll(se);
                ScrollStateChanged?.Invoke(this, EventArgs.Empty);
                UpdateThemedScrollbar();
            }

            public void EnsureChildVisible(Control anyDescendant)
            {
                if (anyDescendant == null || anyDescendant.IsDisposed)
                    return;

                Control? anchor = anyDescendant;
                while (anchor != null && anchor.Parent != this)
                    anchor = anchor.Parent;

                if (anchor == null || anchor.IsDisposed)
                    return;

                Rectangle rc;
                try
                {
                    if (anchor.Parent == null)
                        return;

                    rc = RectangleToClient(anchor.Parent.RectangleToScreen(anchor.Bounds));
                }
                catch
                {
                    return;
                }

                int viewportH = ClientSize.Height;
                if (viewportH <= 0)
                    viewportH = Height;
                if (viewportH <= 0)
                    return;

                int pad = 18;
                int cur = GetScrollY();
                int desired = cur;

                if (rc.Top < pad)
                    desired = cur + rc.Top - pad;
                else if (rc.Bottom > viewportH - pad)
                    desired = cur + (rc.Bottom - (viewportH - pad));

                if (desired != cur)
                {
                    SuspendHoverTrackingUntilMouseMove();
                    SetScrollValueImmediate(desired);
                }
            }

            public void UpdateThemedScrollbar()
            {
                if (UseThemedVScroll && _skin != null)
                {
                    var total = DisplayRectangle.Height;
                    var viewport = ClientSize.Height;
                    var value = GetScrollY();
                    _skin.SetRange(total, viewport, value);
                }
                ForceHideScrollbars();
            }

            public void StopAnimatedScroll(bool snapToTarget)
            {
                UpdateThemedScrollbar();
            }

            public void SuspendHoverTrackingUntilMouseMove()
            {
                if (_hoverTrackingSuspended)
                    return;

                _hoverTrackingSuspended = true;
                InvalidateInteractiveChildren();
            }

            public void NotifyPointerMoveFromChild()
            {
                ResumeHoverTracking();
            }

            private void ResumeHoverTracking()
            {
                if (!_hoverTrackingSuspended)
                    return;

                _hoverTrackingSuspended = false;
                InvalidateInteractiveChildren();
            }

            private void InvalidateInteractiveChildren()
            {
                try { InvalidateInteractiveChildrenRecursive(this); }
                catch { }
            }

            private static void InvalidateInteractiveChildrenRecursive(Control root)
            {
                foreach (Control c in root.Controls)
                {
                    if (c == null || c.IsDisposed)
                        continue;

                    if (c is FileCard || c is SeasonSelectorCard || c is CollectionBucketCard || c is CollectionHubTileCard)
                        c.Invalidate();

                    if (c.HasChildren)
                        InvalidateInteractiveChildrenRecursive(c);
                }
            }

            private int GetScrollY()
            {
                try { return Math.Max(0, -AutoScrollPosition.Y); }
                catch { return 0; }
            }

            private void SetScrollValueImmediate(int value)
            {
                value = Math.Max(0, Math.Min(value, Math.Max(0, DisplayRectangle.Height - ClientSize.Height)));
                try
                {
                    AutoScrollPosition = new Point(0, value);
                    ScrollStateChanged?.Invoke(this, EventArgs.Empty);
                    UpdateThemedScrollbar();
                }
                catch { }
            }
        }


        // ------------ Scrollbar verticale custom ------------
        private sealed class ThemedVScroll : Control
        {
            private int _total = 1;
            private int _view = 1;
            private int _value = 0;
            private bool _drag;
            private int _dragOffset;

            public event Action<int>? ScrollTo;

            public ThemedVScroll()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Black; // nessuna gutter chiara
                Cursor = Cursors.Hand;
            }

            protected override void OnParentChanged(EventArgs e)
            {
                base.OnParentChanged(e);
                if (Parent != null) BackColor = Parent.BackColor;
            }

            public void SetRange(int total, int viewport, int value)
            {
                _total = Math.Max(1, total);
                _view = Math.Max(1, viewport);
                _value = Math.Max(0,
                    Math.Min(value, Math.Max(0, _total - _view)));

                Visible = _total > _view;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (!Visible) return;
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.None;

                using (var bg = new SolidBrush(Color.Black))
                    g.FillRectangle(bg, ClientRectangle);

                var thRect = GetThumbRect();

                // thumb = barretta Accent da 4px sul bordo destro
                var drawRect = new Rectangle(Width - 4, thRect.Y, 4, thRect.Height);
                using var thumbBr = new SolidBrush(Color.FromArgb(200, Theme.Accent));
                g.FillRectangle(thumbBr, drawRect);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (!Visible) return;
                var th = GetThumbRect();
                if (th.Contains(e.Location))
                {
                    _drag = true;
                    _dragOffset = e.Y - th.Y;
                }
                else
                {
                    JumpTo(e.Y);
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (Visible && _drag)
                    DragTo(e.Y - _dragOffset);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _drag = false;
            }

            private Rectangle GetThumbRect()
            {
                float ratio = _total <= _view ? 1f : (float)_view / _total;
                int th = Math.Max(20, (int)(Height * ratio));
                int maxY = Height - th;
                int y = (_total <= _view)
                    ? 0
                    : (int)(maxY * (_value / (float)(_total - _view)));

                // hitbox larga 8px, disegniamo 4px
                return new Rectangle(Width - 8, y, 8, th);
            }

            private void JumpTo(int y)
            {
                var th = GetThumbRect();
                DragTo(y - th.Height / 2);
            }

            private void DragTo(int y)
            {
                float ratio = _total <= _view ? 1f : (float)_view / _total;
                int th = Math.Max(20, (int)(Height * ratio));
                int maxY = Height - th;
                y = Math.Max(0, Math.Min(y, maxY));

                int newVal = (int)((_total - _view) * (y / (float)maxY));
                ScrollTo?.Invoke(newVal);
                _value = newVal;
                Invalidate();
            }
        }



        // ------------ LoadMoreBanner (Foto) ------------
        private sealed class LoadMoreBanner : Control
        {
            public string Title { get; set; } = "Mostra altre foto";
            public string Subtitle { get; set; } = "Carica altri risultati nella galleria";

            private bool _hover;
            private bool _down;

            public LoadMoreBanner()
            {
                Cursor = Cursors.Hand;
                TabStop = true;

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, __) => { _down = true; Invalidate(); };
                MouseUp += (_, __) => { _down = false; Invalidate(); };
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.PanelAlt);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using var gp = GraphicsUtil.RoundRect(rect, 12);

                var fill = _down ? Color.FromArgb(60, Theme.Accent)
                                 : _hover ? Color.FromArgb(40, Theme.Accent)
                                          : Theme.Card;

                using (var br = new SolidBrush(fill))
                    g.FillPath(br, gp);

                using (var pen = new Pen(_hover ? Color.FromArgb(180, Theme.Border)
                                                : Color.FromArgb(140, Theme.Border)))
                    g.DrawPath(pen, gp);

                // Chevron (stile carousel)
                int chevronSize = 16;
                var chevRect = new Rectangle(Width - chevronSize - 20, (Height - chevronSize) / 2, chevronSize, chevronSize);
                using (var p = new Pen(Theme.Text, 2))
                {
                    p.EndCap = LineCap.Round;
                    p.StartCap = LineCap.Round;
                    g.DrawLines(p, new[]
                    {
                        new Point(chevRect.Left + 4, chevRect.Top + 3),
                        new Point(chevRect.Right - 4, chevRect.Top + chevronSize / 2),
                        new Point(chevRect.Left + 4, chevRect.Bottom - 3),
                    });
                }

                var textRect = new Rectangle(18, 10, Width - 18 - 50, Height - 20);

                using var titleFont = new Font("Segoe UI Semibold", 11.0f);
                using var subFont = new Font("Segoe UI", 9.5f);

                TextRenderer.DrawText(
                    g,
                    Title ?? string.Empty,
                    titleFont,
                    new Rectangle(textRect.Left, textRect.Top, textRect.Width, 24),
                    Theme.Text,
                    TextFormatFlags.Left
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);

                TextRenderer.DrawText(
                    g,
                    Subtitle ?? string.Empty,
                    subFont,
                    new Rectangle(textRect.Left, textRect.Top + 26, textRect.Width, textRect.Height - 26),
                    Theme.SubtleText,
                    TextFormatFlags.Left
                  | TextFormatFlags.Top
                  | TextFormatFlags.EndEllipsis);
            }
        }

        private static void DrawBucketGlyph(Graphics g, Rectangle rect, string? bucketKey, Color color)
        {
            string normalized = NormalizeCollectionBucketKey(bucketKey);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var pen = new Pen(color, 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var brush = new SolidBrush(color);

            if (string.Equals(normalized, "Foto", StringComparison.OrdinalIgnoreCase))
            {
                using var frame = GraphicsUtil.RoundRect(rect, 4);
                g.DrawPath(pen, frame);
                int sun = Math.Max(4, rect.Width / 6);
                g.FillEllipse(brush, rect.Right - sun - 4, rect.Top + 4, sun, sun);
                g.DrawLines(pen, new[]
                {
                    new Point(rect.Left + 5, rect.Bottom - 6),
                    new Point(rect.Left + rect.Width / 2 - 3, rect.Top + rect.Height / 2 + 1),
                    new Point(rect.Left + rect.Width / 2 + 3, rect.Top + rect.Height / 2 + 5),
                    new Point(rect.Right - 5, rect.Top + 8)
                });
                return;
            }

            if (string.Equals(normalized, "Musica", StringComparison.OrdinalIgnoreCase))
            {
                int stemX = rect.Left + rect.Width / 2 + 3;
                int stemTop = rect.Top + 4;
                int stemBottom = rect.Bottom - 8;
                g.DrawLine(pen, stemX, stemTop, stemX, stemBottom);
                g.DrawLine(pen, stemX, stemTop, rect.Right - 3, rect.Top + 2);
                g.FillEllipse(brush, rect.Left + 5, rect.Bottom - 11, 8, 8);
                g.FillEllipse(brush, stemX - 4, rect.Bottom - 7, 8, 8);
                return;
            }

            if (string.Equals(normalized, "Film", StringComparison.OrdinalIgnoreCase))
            {
                using var frame = GraphicsUtil.RoundRect(rect, 4);
                g.DrawPath(pen, frame);
                int holeSize = Math.Max(2, rect.Height / 8);
                for (int i = 0; i < 3; i++)
                {
                    int y = rect.Top + 4 + i * (holeSize + 3);
                    g.FillRectangle(brush, rect.Left + 3, y, holeSize, holeSize);
                    g.FillRectangle(brush, rect.Right - holeSize - 3, y, holeSize, holeSize);
                }
                Point[] triangle =
                {
                    new Point(rect.Left + rect.Width / 2 - 3, rect.Top + 8),
                    new Point(rect.Left + rect.Width / 2 - 3, rect.Bottom - 8),
                    new Point(rect.Right - 8, rect.Top + rect.Height / 2)
                };
                g.FillPolygon(brush, triangle);
                return;
            }

            using (var frame = GraphicsUtil.RoundRect(rect, 4))
                g.DrawPath(pen, frame);
            Point[] play =
            {
                new Point(rect.Left + 9, rect.Top + 7),
                new Point(rect.Left + 9, rect.Bottom - 7),
                new Point(rect.Right - 6, rect.Top + rect.Height / 2)
            };
            g.FillPolygon(brush, play);
        }

        private static Size MeasureQuickActionChip(Graphics g, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Size.Empty;

            using var font = new Font("Segoe UI Semibold", 8.5f);
            Size textSize = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            return new Size(Math.Max(54, textSize.Width + 20), 26);
        }

        private static void DrawQuickActionChip(Graphics g, Rectangle rect, string text, bool emphasized)
        {
            Color fill = emphasized ? Color.FromArgb(42, 76, 146) : Color.FromArgb(30, 30, 36);
            Color border = emphasized ? Color.FromArgb(86, 146, 255) : Color.FromArgb(74, 74, 82);
            Color textColor = emphasized ? Color.White : Theme.Text;

            using var path = GraphicsUtil.RoundRect(rect, 11);
            using var fillBrush = new SolidBrush(fill);
            using var borderPen = new Pen(border);
            using var font = new Font("Segoe UI Semibold", 8.5f);
            g.FillPath(fillBrush, path);
            g.DrawPath(borderPen, path);
            TextRenderer.DrawText(g, text, font, rect, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private sealed class CollectionHubTileCard : Control
        {
            private readonly Action _openAction;
            private readonly string _bucketKey;
            private readonly string _title;
            private readonly string _subtitle;
            private readonly string _artworkKey;
            private Bitmap? _customArtwork;
            private bool _hover;
            private bool _down;
            private bool _focused;
            private bool _suppressNextClickAction;

            public CollectionHubTileCard(string bucketKey, string title, string subtitle, Action openAction, string? artworkKey = null)
            {
                _bucketKey = NormalizeCollectionBucketKey(bucketKey);
                _title = title ?? string.Empty;
                _subtitle = subtitle ?? string.Empty;
                _artworkKey = artworkKey ?? string.Empty;
                _openAction = openAction ?? (() => { });
                _customArtwork = TryLoadCollectionTileArtwork(_artworkKey, _bucketKey);

                Height = 234;
                Margin = new Padding(12, 8, 12, 18);
                Cursor = Cursors.Hand;
                TabStop = true;
                BackColor = Color.Black;

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.Selectable, true);

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseMove += (_, __) => { try { FindHostingFlow()?.NotifyPointerMoveFromChild(); } catch { } };
                MouseDown += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                    {
                        _suppressNextClickAction = true;
                        return;
                    }

                    if (e.Button == MouseButtons.Left)
                    {
                        _down = true;
                        Invalidate();
                    }
                };
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _customArtwork?.Dispose(); } catch { }
                    _customArtwork = null;
                }
                base.Dispose(disposing);
            }

            public void SetQuickActions(string? primaryLabel, Action? primaryAction, string? secondaryLabel = null, Action? secondaryAction = null)
            {
                // Tile volutamente pulita: azioni rapide non mostrate qui.
            }

            public void SetItemContextMenu(ContextMenuStrip? menu, object? tag)
            {
                ContextMenuStrip = menu;
                Tag = tag;
            }

            private SkinnedFlow? FindHostingFlow()
            {
                Control? p = Parent;
                while (p != null && p is not SkinnedFlow)
                    p = p.Parent;
                return p as SkinnedFlow;
            }

            private bool IsHoverVisualActive()
            {
                var flow = FindHostingFlow();
                if (flow == null)
                    return _hover;
                return _hover && !flow.IsHoverTrackingSuspended;
            }

            private static Color GetBucketAccent(string bucketKey)
            {
                return NormalizeCollectionBucketKey(bucketKey) switch
                {
                    "Film" => Color.FromArgb(72, 124, 232),
                    "Foto" => Color.FromArgb(74, 148, 110),
                    "Musica" => Color.FromArgb(164, 116, 66),
                    _ => Color.FromArgb(86, 92, 108)
                };
            }

            private void ShowContextMenuAt(Point location)
            {
                try
                {
                    var menu = ContextMenuStrip;
                    if (menu == null || menu.IsDisposed)
                        return;

                    try { Focus(); } catch { }
                    location.X = Math.Max(12, Math.Min(Math.Max(12, Width - 12), location.X));
                    location.Y = Math.Max(12, Math.Min(Math.Max(12, Height - 12), location.Y));
                    menu.Show(this, location);
                }
                catch { }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    _suppressNextClickAction = true;
                    ShowContextMenuAt(e.Location);
                }
                base.OnMouseUp(e);
                if (_down)
                {
                    _down = false;
                    Invalidate();
                }
            }

            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                if (_suppressNextClickAction)
                {
                    _suppressNextClickAction = false;
                    return;
                }
                _openAction();
            }

            protected override bool IsInputKey(Keys keyData)
            {
                var keyCode = keyData & Keys.KeyCode;
                if (keyCode == Keys.Enter || keyCode == Keys.Space || keyCode == Keys.Apps)
                    return true;
                return base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);

                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    _openAction();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Apps)
                {
                    try { ContextMenuStrip?.Show(this, new Point(Math.Max(12, Width / 2), Math.Max(12, Height / 2))); } catch { }
                    e.Handled = true;
                }
            }

            protected override void OnGotFocus(EventArgs e)
            {
                base.OnGotFocus(e);
                _focused = true;
                Invalidate();
            }

            protected override void OnLostFocus(EventArgs e)
            {
                base.OnLostFocus(e);
                _focused = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                bool hoverActive = IsHoverVisualActive();
                Color accent = GetBucketAccent(_bucketKey);
                var outer = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                var cover = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(124, Height - 62));
                var textBand = new Rectangle(0, cover.Bottom, Math.Max(1, Width - 1), Math.Max(48, Height - cover.Height));

                Color bodyFill = _down
                    ? Color.FromArgb(34, 38, 46)
                    : Theme.Card;
                using (var br = new SolidBrush(bodyFill))
                    g.FillRectangle(br, outer);

                if (_customArtwork != null)
                {
                    g.DrawImage(_customArtwork, cover);
                    using var shade = new SolidBrush(Color.FromArgb(22, 0, 0, 0));
                    g.FillRectangle(shade, cover);
                }
                else
                {
                    using var coverBrush = new SolidBrush(Color.FromArgb(188, accent));
                    g.FillRectangle(coverBrush, cover);
                }

                using (var accentBrush = new SolidBrush(accent))
                    g.FillRectangle(accentBrush, new Rectangle(0, 0, Math.Max(1, Math.Min(cover.Width, 72)), 5));

                using (var bandBrush = new SolidBrush(bodyFill))
                    g.FillRectangle(bandBrush, textBand);

                using var titleFont = new Font("Segoe UI Semibold", 12f, GraphicsUnit.Point);
                using var subFont = new Font("Segoe UI", 9.25f, GraphicsUnit.Point);

                var titleRect = new Rectangle(0, cover.Bottom + 10, Math.Max(1, Width - 1), 24);
                var subtitleRect = new Rectangle(0, cover.Bottom + 34, Math.Max(1, Width - 1), Math.Max(20, Height - (cover.Bottom + 40)));

                TextRenderer.DrawText(
                    g,
                    _title,
                    titleFont,
                    titleRect,
                    Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                TextRenderer.DrawText(
                    g,
                    _subtitle,
                    subFont,
                    subtitleRect,
                    Theme.SubtleText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                using var borderPen = new Pen(_focused ? Theme.Accent : (hoverActive ? Color.FromArgb(200, accent) : Color.FromArgb(72, 88, 106)), _focused ? 2f : 1f);
                g.DrawRectangle(borderPen, outer);
            }
        }

        private sealed class CollectionBucketCard : Control
        {
            private readonly Action _openAction;
            private bool _hover;
            private bool _down;
            private bool _focused;
            private string _title;
            private string _subtitle;
            private string _bucketKey = string.Empty;
            private string? _primaryActionLabel;
            private Action? _primaryAction;
            private string? _secondaryActionLabel;
            private Action? _secondaryAction;
            private Rectangle _primaryButtonRect = Rectangle.Empty;
            private Rectangle _secondaryButtonRect = Rectangle.Empty;
            private bool _suppressNextClickAction;

            public string BucketKey
            {
                get => _bucketKey;
                set
                {
                    _bucketKey = NormalizeCollectionBucketKey(value);
                    Invalidate();
                }
            }

            public string Title
            {
                get => _title;
                set
                {
                    _title = value ?? string.Empty;
                    Invalidate();
                }
            }

            public string Subtitle
            {
                get => _subtitle;
                set
                {
                    _subtitle = value ?? string.Empty;
                    Invalidate();
                }
            }

            public CollectionBucketCard(string title, string subtitle, Action openAction)
            {
                _title = title ?? string.Empty;
                _subtitle = subtitle ?? string.Empty;
                _openAction = openAction ?? (() => { });

                Height = 118;
                Margin = new Padding(12, 8, 12, 14);
                Cursor = Cursors.Hand;
                TabStop = true;
                BackColor = Color.Black;

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.Selectable, true);

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseMove += (_, __) => { try { FindHostingFlow()?.NotifyPointerMoveFromChild(); } catch { } };
                MouseDown += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                    {
                        _suppressNextClickAction = true;
                        return;
                    }

                    if (e.Button == MouseButtons.Left)
                    {
                        _down = true;
                        Invalidate();
                    }
                };
            }

            public void SetQuickActions(string? primaryLabel, Action? primaryAction, string? secondaryLabel = null, Action? secondaryAction = null)
            {
                _primaryActionLabel = string.IsNullOrWhiteSpace(primaryLabel) ? null : primaryLabel.Trim();
                _primaryAction = primaryAction;
                _secondaryActionLabel = string.IsNullOrWhiteSpace(secondaryLabel) ? null : secondaryLabel.Trim();
                _secondaryAction = secondaryAction;
                Invalidate();
            }

            private SkinnedFlow? FindHostingFlow()
            {
                Control? p = Parent;
                while (p != null && p is not SkinnedFlow)
                    p = p.Parent;
                return p as SkinnedFlow;
            }

            private bool IsHoverVisualActive()
            {
                var flow = FindHostingFlow();
                if (flow == null)
                    return _hover;
                return _hover && !flow.IsHoverTrackingSuspended;
            }

            private static Color GetBucketAccent(string bucketKey)
            {
                return NormalizeCollectionBucketKey(bucketKey) switch
                {
                    "Film" => Color.FromArgb(86, 146, 255),
                    "Foto" => Color.FromArgb(82, 186, 135),
                    "Musica" => Color.FromArgb(230, 148, 64),
                    _ => Color.FromArgb(124, 136, 156)
                };
            }

            public void SetItemContextMenu(ContextMenuStrip? menu, object? tag)
            {
                ContextMenuStrip = menu;
                Tag = tag;
            }

            private bool TryInvokeQuickAction(Point location)
            {
                if (_secondaryAction != null && !_secondaryButtonRect.IsEmpty && _secondaryButtonRect.Contains(location))
                {
                    _suppressNextClickAction = true;
                    _secondaryAction();
                    return true;
                }

                if (_primaryAction != null && !_primaryButtonRect.IsEmpty && _primaryButtonRect.Contains(location))
                {
                    _suppressNextClickAction = true;
                    _primaryAction();
                    return true;
                }

                return false;
            }

            private void ShowContextMenuAt(Point location)
            {
                try
                {
                    var menu = ContextMenuStrip;
                    if (menu == null || menu.IsDisposed)
                        return;

                    try { Focus(); } catch { }
                    location.X = Math.Max(12, Math.Min(Math.Max(12, Width - 12), location.X));
                    location.Y = Math.Max(12, Math.Min(Math.Max(12, Height - 12), location.Y));
                    menu.Show(this, location);
                }
                catch { }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    _suppressNextClickAction = true;
                    ShowContextMenuAt(e.Location);
                }
                else if (e.Button == MouseButtons.Left)
                {
                    TryInvokeQuickAction(e.Location);
                }
                base.OnMouseUp(e);
                if (_down)
                {
                    _down = false;
                    Invalidate();
                }
            }

            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                if (_suppressNextClickAction)
                {
                    _suppressNextClickAction = false;
                    return;
                }
                _openAction();
            }

            protected override bool IsInputKey(Keys keyData)
            {
                var keyCode = keyData & Keys.KeyCode;
                if (keyCode == Keys.Enter || keyCode == Keys.Space || keyCode == Keys.Apps)
                    return true;
                return base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);

                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    _openAction();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Apps)
                {
                    try { ContextMenuStrip?.Show(this, new Point(Math.Max(12, Width / 2), Math.Max(12, Height / 2))); } catch { }
                    e.Handled = true;
                }
            }

            protected override void OnGotFocus(EventArgs e)
            {
                base.OnGotFocus(e);
                _focused = true;
                Invalidate();
            }

            protected override void OnLostFocus(EventArgs e)
            {
                base.OnLostFocus(e);
                _focused = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                bool hoverActive = IsHoverVisualActive();
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using var gp = GraphicsUtil.RoundRect(rect, 16);

                Color fill = _down
                    ? Color.FromArgb(34, 38, 48)
                    : (hoverActive || _focused)
                        ? Color.FromArgb(22, 27, 38)
                        : Color.FromArgb(17, 21, 30);

                using (var br = new SolidBrush(fill))
                    g.FillPath(br, gp);

                Color accent = GetBucketAccent(_bucketKey);
                using (var pen = new Pen(_focused ? Theme.Accent : (hoverActive ? Color.FromArgb(88, accent) : Theme.Border), _focused ? 1.7f : 1.1f))
                    g.DrawPath(pen, gp);

                var accentRect = new Rectangle(22, 18, Math.Max(82, Math.Min(170, Width / 3)), 5);
                using (var accentPath = GraphicsUtil.RoundRect(accentRect, 3))
                using (var accentBrush = new SolidBrush(Color.FromArgb(_focused || hoverActive ? 255 : 196, accent)))
                    g.FillPath(accentBrush, accentPath);

                _primaryButtonRect = Rectangle.Empty;
                _secondaryButtonRect = Rectangle.Empty;

                int textLeft = 24;
                int chevronReserve = 40;
                var textRect = new Rectangle(textLeft, 30, Math.Max(80, Width - textLeft - chevronReserve - 18), Height - 54);
                using var titleFont = new Font("Segoe UI Semibold", 12f);
                using var subFont = new Font("Segoe UI", 9.25f);

                TextRenderer.DrawText(
                    g,
                    _title ?? string.Empty,
                    titleFont,
                    new Rectangle(textRect.Left, textRect.Top, textRect.Width, 26),
                    Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                TextRenderer.DrawText(
                    g,
                    _subtitle ?? string.Empty,
                    subFont,
                    new Rectangle(textRect.Left, textRect.Top + 28, textRect.Width, Math.Max(22, textRect.Height - 28)),
                    Theme.SubtleText,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordEllipsis | TextFormatFlags.NoPadding);

                int chevronSize = 10;
                int cx = Width - 28;
                int cy = Height / 2;
                using var chevronPen = new Pen(Color.FromArgb(hoverActive || _focused ? 220 : 132, 214, 220, 226), 2f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLines(chevronPen, new[]
                {
                    new Point(cx - chevronSize, cy - chevronSize),
                    new Point(cx, cy),
                    new Point(cx - chevronSize, cy + chevronSize)
                });
            }
        }

        private sealed class LibrarySectionDivider : Control
        {
            public string Title { get; }
            public string? Bucket { get; }
            public int LeftMargin { get; set; } = 12;

            public LibrarySectionDivider(string title, string? bucket = null)
            {
                Title = title ?? string.Empty;
                Bucket = bucket;
                Height = 34;
                Margin = new Padding(0, 22, 0, 12);
                BackColor = Color.Black;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);
                TabStop = false;
            }

            private static Color GetAccent(string? bucket)
            {
                return NormalizeCollectionBucketKey(bucket) switch
                {
                    "Film" => Color.FromArgb(86, 146, 255),
                    "Foto" => Color.FromArgb(82, 186, 135),
                    "Musica" => Color.FromArgb(230, 148, 64),
                    _ => Color.FromArgb(124, 136, 156)
                };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                using var font = new Font("Segoe UI Semibold", 9.75f);
                int leftPad = Math.Max(12, LeftMargin);
                Color accent = GetAccent(Bucket);

                var accentRect = new Rectangle(leftPad, Math.Max(4, (Height - 14) / 2), 4, 14);
                using (var accentPath = GraphicsUtil.RoundRect(accentRect, 2))
                using (var accentBrush = new SolidBrush(accent))
                    g.FillPath(accentBrush, accentPath);

                var textSize = TextRenderer.MeasureText(
                    Title,
                    font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                var textRect = new Rectangle(accentRect.Right + 10, 0, textSize.Width + 10, Height);

                using (var bg = new SolidBrush(Color.Black))
                    g.FillRectangle(bg, new Rectangle(textRect.Left - 4, 0, textRect.Width + 12, Height));

                TextRenderer.DrawText(
                    g,
                    Title,
                    font,
                    textRect,
                    Color.FromArgb(234, 238, 244),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

                using var linePen = new Pen(Color.FromArgb(54, Theme.Border));
                int lineY = Height / 2;
                int lineStart = Math.Min(Width - 8, textRect.Right + 12);
                if (lineStart < Width - 8)
                    g.DrawLine(linePen, lineStart, lineY, Width - 8, lineY);
            }
        }

        // ------------ SectionHeader ("Recenti", "Tutti i file") ------------
        private sealed class SectionHeader : Panel
        {
            private string _text;
            public int LeftMargin { get; set; } = 104;

            public string Title
            {
                get => _text;
                set
                {
                    _text = value;
                    Invalidate();
                }
            }

            public SectionHeader(string text)
            {
                _text = text;
                Height = 30;
                Dock = DockStyle.Top;
                BackColor = Color.Black;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using var font = new Font("Segoe UI Semibold", 9.75f);
                int leftPad = Math.Max(12, LeftMargin);
                var textSize = TextRenderer.MeasureText(_text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                var textRect = new Rectangle(leftPad, 0, textSize.Width + 6, Height);

                using (var bg = new SolidBrush(Color.Black))
                    g.FillRectangle(bg, new Rectangle(textRect.Left - 4, 0, textRect.Width + 12, Height));

                TextRenderer.DrawText(
                    g,
                    _text,
                    font,
                    textRect,
                    Color.FromArgb(238, 240, 244),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

                using var linePen = new Pen(Color.FromArgb(54, Theme.Border));
                int lineY = (Height / 2) + 2;
                int lineStart = Math.Min(Width - 8, textRect.Right + 14);
                if (lineStart < Width - 8)
                    g.DrawLine(linePen, lineStart, lineY, Width - 8, lineY);
            }
        }


        // ------------ Pannello destro host custom (toglie scrollbar di sistema) ------------
        private sealed class RightHostPanel : Panel
        {
            public RightHostPanel()
            {
                Dock = DockStyle.Fill;
                BackColor = Color.Black;
                AutoScroll = false;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);
            }

            // togli gli style WS_VSCROLL / WS_HSCROLL per evitare gutter bianca
            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_VSCROLL = 0x00200000;
                    const int WS_HSCROLL = 0x00100000;
                    var cp = base.CreateParams;
                    cp.Style &= ~WS_VSCROLL;
                    cp.Style &= ~WS_HSCROLL;
                    return cp;
                }
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                Win32.ShowScrollBar(Handle, Win32.SB_VERT, false);
                Win32.ShowScrollBar(Handle, Win32.SB_HORZ, false);
            }
        }


    }
}
