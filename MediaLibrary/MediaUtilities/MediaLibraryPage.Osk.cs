#nullable enable
using CinecorePlayer2025.HUD;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class MediaLibraryPage
    {
        // -------------------------
        // App On-Screen Keyboard (OSK) - SOLO DPAD/Remote
        // -------------------------
        private DimmerPanel? _appOskOverlay;
        private RoundedCardPanel? _appOskCard;
        private AppOnScreenKeyboard? _appOsk;

        internal bool IsAppOskVisible => _appOskOverlay != null && _appOskOverlay.Visible;

        private void BuildAppOskOverlay()
        {
            // Overlay dimmer sopra tutta la colonna destra
            _appOskOverlay = new DimmerPanel
            {
                Dock = DockStyle.Fill,
                Visible = false,
                DimAlpha = 175,
            };

            _appOskCard = new RoundedCardPanel
            {
                BackColor = Theme.Panel,
                Padding = new Padding(16),
                Anchor = AnchorStyles.None,

                // Più sobria: meno "card" da mobile, più pannello moderno e pulito.
                CornerRadius = 16,
                ShadowOffset = 0,
                ShadowAlpha = 0f,
            };

            _appOsk = new AppOnScreenKeyboard
            {
                Dock = DockStyle.Fill,
            };

            _appOsk.DoneRequested += () =>
            {
                try
                {
                    HideAppOsk();
                    // Resta in libreria: torna al contenitore Search (cosi' DPAD riprende coerente)
                    try { _search?.Focus(); } catch { }
                    try { if (_search != null) RequestRemoteFocus(_search); } catch { }
                }
                catch { }
            };

            _appOskCard.Controls.Add(_appOsk);
            _appOskOverlay.Controls.Add(_appOskCard);

            void ResizeAndCenter()
            {
                try
                {
                    if (_appOskOverlay == null || _appOskCard == null) return;
                    if (_appOskOverlay.ClientSize.Width <= 0 || _appOskOverlay.ClientSize.Height <= 0) return;

                    // Card responsive: mai fuori schermo.
                    // NOTA: il layout dell'OSK ha altezze fisse (preview + 5 righe).
                    // Se la card è troppo bassa, i tasti "escono" (bug segnalato).
                    int minW = 680;
                    int maxW = 980;
                    int minH = 440;
                    int maxH = 560;

                    int w = Math.Max(minW, _appOskOverlay.ClientSize.Width - 180);
                    int h = Math.Max(minH, _appOskOverlay.ClientSize.Height - 260);

                    // clamp per sicurezza in finestre piccole
                    int maxPossibleW = Math.Max(320, _appOskOverlay.ClientSize.Width - 40);
                    int maxPossibleH = Math.Max(320, _appOskOverlay.ClientSize.Height - 40);

                    w = Math.Min(Math.Min(maxW, w), maxPossibleW);
                    h = Math.Min(Math.Min(maxH, h), maxPossibleH);

                    _appOskCard.Size = new Size(w, h);
                    _appOskCard.Left = Math.Max(0, (_appOskOverlay.ClientSize.Width - _appOskCard.Width) / 2);
                    _appOskCard.Top = Math.Max(0, (_appOskOverlay.ClientSize.Height - _appOskCard.Height) / 2);
                }
                catch { }
            }

            _appOskOverlay.SizeChanged += (_, __) => ResizeAndCenter();
            ResizeAndCenter();
        }

        internal void ShowAppOsk(TextBox target)
        {
            try
            {
                if (IsDisposed) return;
                if (target == null || target.IsDisposed) return;
                if (_appOskOverlay == null || _appOskCard == null || _appOsk == null) return;

                // Non mostrare sopra overlay che gia' catturano input
                if (_rootsOverlay != null && _rootsOverlay.Visible) return;

                _appOsk.SetTarget(target);
                _appOskOverlay.Visible = true;
                _appOskOverlay.BringToFront();

                // Porta SUBITO il focus sul primo tasto (cosi' le frecce non finiscono nel caret del TextBox)
                try
                {
                    var first = _appOsk.GetDefaultFocusTarget();
                    if (first != null && !first.IsDisposed)
                    {
                        try { first.Focus(); } catch { }
                        try { RequestRemoteFocus(first); } catch { }
                    }
                }
                catch { }

                // Safety: riprova nel prossimo tick
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var first = _appOsk.GetDefaultFocusTarget();
                        if (first != null && !first.IsDisposed)
                        {
                            try { first.Focus(); } catch { }
                            try { RequestRemoteFocus(first); } catch { }
                        }
                    }
                    catch { }
                }));
            }
            catch { }
        }

        internal void HideAppOsk()
        {
            try
            {
                if (_appOskOverlay == null || _appOsk == null) return;
                _appOskOverlay.Visible = false;
                _appOsk.SetTarget(null);
                // reset eventuale "arming" compat (OSK solo da remote)
                try { _remoteOskArmedForSearch = false; } catch { }
            }
            catch { }
        }

        // Pannello dimmer: disegna un overlay semi-trasparente senza usare BackColor con alpha
        private sealed class DimmerPanel : Panel
        {
            public int DimAlpha { get; set; } = 175;

            public DimmerPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Color.Transparent);
                using var b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, DimAlpha)), 0, 0, 0));
                e.Graphics.FillRectangle(b, 0, 0, Width, Height);
                base.OnPaint(e);
            }
        }

        // Card con angoli arrotondati e ombra leggera
        private sealed class RoundedCardPanel : Panel
        {
            public int CornerRadius { get; set; } = 20;
            public int ShadowOffset { get; set; } = 8;
            public float ShadowAlpha { get; set; } = 0.22f;

            public RoundedCardPanel()
            {
                DoubleBuffered = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                if (r.Width <= 0 || r.Height <= 0) return;

                // shadow
                if (ShadowAlpha > 0f && ShadowOffset > 0)
                {
                    var rs = new Rectangle(r.X + ShadowOffset, r.Y + ShadowOffset, r.Width, r.Height);
                    using var shPath = RoundedPath(rs, CornerRadius);
                    using var shBrush = new SolidBrush(Color.FromArgb((int)(255 * ShadowAlpha), 0, 0, 0));
                    e.Graphics.FillPath(shBrush, shPath);
                }

                using (var path = RoundedPath(r, CornerRadius))
                {
                    using var b = new SolidBrush(BackColor);
                    e.Graphics.FillPath(b, path);
                    using var p = new Pen(Theme.Border, 1f);
                    e.Graphics.DrawPath(p, path);
                }

                base.OnPaint(e);
            }

            private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle r, int radius)
            {
                var p = new System.Drawing.Drawing2D.GraphicsPath();
                int d = Math.Max(1, radius * 2);
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
        }

        private sealed class AppOnScreenKeyboard : UserControl
        {
            public event Action? DoneRequested;

            private TextBox? _target;
            private readonly Label _preview;
            private readonly OskRowPanel _rowQwerty;
            private readonly OskRowPanel _rowAsdf;
            private readonly OskRowPanel _rowZxcv;
            private readonly OskRowPanel _rowActions;

            public AppOnScreenKeyboard()
            {
                DoubleBuffered = true;
                BackColor = Theme.Panel;

                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 5,
                    Padding = new Padding(8),
                };

                // Layout sobrio e robusto: preview + 4 righe.
                // Percentuali (e non altezze fisse) per evitare tasti "fuori" su finestre più basse.
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));

                _preview = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Theme.SubtleText,
                    Font = new Font("Segoe UI", 10.0f, FontStyle.Bold),
                    Padding = new Padding(10, 0, 10, 0),
                    AutoEllipsis = true,
                };

                // Padding uniforme: la navigazione DPAD risulta molto più prevedibile.
                // KeyHeight è un "target" che verrà automaticamente ridotto se la riga è più bassa.
                _rowQwerty = new OskRowPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6), Gap = 8, KeyHeight = 52, MinKeyWidth = 40 };
                _rowAsdf = new OskRowPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6), Gap = 8, KeyHeight = 52, MinKeyWidth = 40 };
                _rowZxcv = new OskRowPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6), Gap = 8, KeyHeight = 52, MinKeyWidth = 40 };
                _rowActions = new OskRowPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 10), Gap = 10, KeyHeight = 52, MinKeyWidth = 44 };

                layout.Controls.Add(_preview, 0, 0);
                layout.Controls.Add(_rowQwerty, 0, 1);
                layout.Controls.Add(_rowAsdf, 0, 2);
                layout.Controls.Add(_rowZxcv, 0, 3);
                layout.Controls.Add(_rowActions, 0, 4);

                Controls.Add(layout);

                RebuildKeys();
            }

            public void SetTarget(TextBox? tb)
            {
                _target = tb;
                UpdatePreview();
            }

            public Control? GetDefaultFocusTarget()
            {
                // Start sul primo tasto "lettera" (Q) per UX piu' naturale.
                var q = _rowQwerty.Keys.FirstOrDefault();
                if (q != null) return q;
                var a = _rowAsdf.Keys.FirstOrDefault();
                if (a != null) return a;
                var z = _rowZxcv.Keys.FirstOrDefault();
                if (z != null) return z;
                var act = _rowActions.Keys.FirstOrDefault();
                return act;
            }

            // Per la navigazione DPAD deterministica (gestita da MediaLibraryPage).
            public List<List<Control>> GetNavigationRows()
            {
                // Layout volutamente essenziale: niente riga numeri.
                // (Meno righe = meno rischio di overflow verticale e navigazione più pulita.)
                var rows = new List<List<Control>>
                        {
                            _rowQwerty.Keys.Cast<Control>().ToList(),
                            _rowAsdf.Keys.Cast<Control>().ToList(),
                            _rowZxcv.Keys.Cast<Control>().ToList(),
                            _rowActions.Keys.Cast<Control>().ToList(),
                        };

                // rimuovi eventuali righe vuote (paranoia)
                rows = rows.Where(r => r.Count > 0).ToList();
                return rows;
            }

            /// <summary>
            /// Navigazione deterministica (freccia su/giu/sx/dx) tra i tasti.
            /// Evita il comportamento "casuale" del finder geometrico generico.
            /// </summary>
            public bool TryDpadMove(Control current, string dir, out Control? next)
            {
                next = null;

                var rows = GetNavigationRows();
                if (rows.Count == 0)
                    return false;

                // Trova posizione (r,c) del tasto corrente.
                int r = -1, c = -1;
                for (int i = 0; i < rows.Count; i++)
                {
                    for (int j = 0; j < rows[i].Count; j++)
                    {
                        if (ReferenceEquals(rows[i][j], current))
                        {
                            r = i;
                            c = j;
                            break;
                        }
                    }
                    if (r >= 0) break;
                }

                if (r < 0 || c < 0)
                {
                    next = GetDefaultFocusTarget();
                    return next != null;
                }

                dir = (dir ?? string.Empty).ToLowerInvariant();

                if (dir == "left")
                {
                    next = rows[r][Math.Max(0, c - 1)];
                    return true;
                }
                if (dir == "right")
                {
                    next = rows[r][Math.Min(rows[r].Count - 1, c + 1)];
                    return true;
                }
                if (dir == "up")
                {
                    if (r == 0)
                    {
                        next = rows[r][c];
                        return true;
                    }

                    int targetX = GetCenterX(current);
                    next = PickClosestByX(rows[r - 1], targetX) ?? rows[r - 1].Last();
                    return true;
                }
                if (dir == "down")
                {
                    if (r == rows.Count - 1)
                    {
                        next = rows[r][c];
                        return true;
                    }

                    int targetX = GetCenterX(current);
                    next = PickClosestByX(rows[r + 1], targetX) ?? rows[r + 1].Last();
                    return true;
                }

                return false;

                static int GetCenterX(Control c)
                {
                    try
                    {
                        var rc = c.RectangleToScreen(c.ClientRectangle);
                        return rc.Left + rc.Width / 2;
                    }
                    catch
                    {
                        return 0;
                    }
                }

                static Control? PickClosestByX(IReadOnlyList<Control> row, int targetX)
                {
                    Control? best = null;
                    int bestDx = int.MaxValue;

                    for (int i = 0; i < row.Count; i++)
                    {
                        var k = row[i];
                        int cx = GetCenterX(k);
                        int dx = Math.Abs(cx - targetX);
                        if (dx < bestDx)
                        {
                            bestDx = dx;
                            best = k;
                        }
                    }
                    return best;
                }
            }

            private void UpdatePreview()
            {
                try
                {
                    var t = _target?.Text ?? string.Empty;
                    _preview.Text = string.IsNullOrWhiteSpace(t) ? "Ricerca" : $"Ricerca: {t}";
                }
                catch { }
            }

            private void RebuildKeys()
            {
                _rowQwerty.ClearKeys();
                _rowAsdf.ClearKeys();
                _rowZxcv.ClearKeys();
                _rowActions.ClearKeys();

                // Layout essenziale (sobrio): lettere + backspace + spazio + OK.
                _rowQwerty.SetKeys(MakeCharRow("QWERTYUIOP"));
                _rowAsdf.SetKeys(MakeCharRow("ASDFGHJKL"));

                var z = MakeCharRow("ZXCVBNM");
                z.Add((MakeAction("⌫", OskKeyKind.Backspace), 1.60f));
                _rowZxcv.SetKeys(z);

                _rowActions.SetKeys(new List<(OskKeyButton key, float weight)>
                {
                    (MakeAction("Pulisci", OskKeyKind.Clear), 1.80f),
                    (MakeAction("Spazio", OskKeyKind.Space), 4.80f),
                    (MakePrimary("OK", OskKeyKind.Done), 1.55f),
                });

                UpdatePreview();

                List<(OskKeyButton key, float weight)> MakeCharRow(string chars)
                {
                    var list = new List<(OskKeyButton key, float weight)>();
                    foreach (var ch in chars)
                    {
                        var shown = ch.ToString();
                        var b = new OskKeyButton(shown)
                        {
                            KeyKind = OskKeyKind.Char,
                            KeyText = shown,
                        };
                        b.Click += (_, __) => InsertText(shown);
                        list.Add((b, 1f));
                    }
                    return list;
                }

                OskKeyButton MakeAction(string label, OskKeyKind kind)
                {
                    var b = new OskKeyButton(label) { KeyKind = kind };
                    b.Click += (_, __) =>
                    {
                        switch (kind)
                        {
                            case OskKeyKind.Backspace:
                                Backspace();
                                break;
                            case OskKeyKind.Clear:
                                Clear();
                                break;
                            case OskKeyKind.Space:
                                InsertText(" ");
                                break;
                            case OskKeyKind.Done:
                                DoneRequested?.Invoke();
                                break;
                        }
                    };
                    return b;
                }

                OskKeyButton MakePrimary(string label, OskKeyKind kind)
                {
                    var b = MakeAction(label, kind);
                    b.IsPrimary = true;
                    return b;
                }
            }

            private void InsertText(string s)
            {
                try
                {
                    if (_target == null || _target.IsDisposed) return;

                    var start = _target.SelectionStart;
                    var len = _target.SelectionLength;
                    var text = _target.Text ?? string.Empty;

                    if (start < 0) start = 0;
                    if (start > text.Length) start = text.Length;
                    if (len < 0) len = 0;
                    if (start + len > text.Length) len = text.Length - start;

                    var newText = text.Substring(0, start) + s + text.Substring(start + len);
                    _target.Text = newText;
                    _target.SelectionStart = start + s.Length;
                    _target.SelectionLength = 0;

                    UpdatePreview();
                }
                catch { }
            }

            private void Backspace()
            {
                try
                {
                    if (_target == null || _target.IsDisposed) return;

                    var start = _target.SelectionStart;
                    var len = _target.SelectionLength;
                    var text = _target.Text ?? string.Empty;

                    if (len > 0)
                    {
                        var newText = text.Substring(0, start) + text.Substring(start + len);
                        _target.Text = newText;
                        _target.SelectionStart = start;
                        _target.SelectionLength = 0;
                    }
                    else if (start > 0 && text.Length > 0)
                    {
                        var newText = text.Substring(0, start - 1) + text.Substring(start);
                        _target.Text = newText;
                        _target.SelectionStart = start - 1;
                        _target.SelectionLength = 0;
                    }

                    UpdatePreview();
                }
                catch { }
            }

            private void Clear()
            {
                try
                {
                    if (_target == null || _target.IsDisposed) return;
                    _target.Text = string.Empty;
                    _target.SelectionStart = 0;
                    _target.SelectionLength = 0;
                    UpdatePreview();
                }
                catch { }
            }

            private enum OskKeyKind { Char, Backspace, Space, Clear, Done }

            private sealed class OskRowPanel : Panel
            {
                private readonly List<(OskKeyButton key, float weight)> _keys = new();

                public IReadOnlyList<OskKeyButton> Keys => _keys.Select(x => x.key).ToList();

                public int KeyHeight { get; set; } = 56;
                public int Gap { get; set; } = 10;
                public int MinKeyWidth { get; set; } = 44;

                public OskRowPanel()
                {
                    DoubleBuffered = true;
                    SetStyle(ControlStyles.AllPaintingInWmPaint |
                             ControlStyles.OptimizedDoubleBuffer |
                             ControlStyles.UserPaint |
                             ControlStyles.ResizeRedraw, true);
                }

                public void ClearKeys()
                {
                    Controls.Clear();
                    _keys.Clear();
                }

                public void SetKeys(IEnumerable<(OskKeyButton key, float weight)> keys)
                {
                    SuspendLayout();
                    Controls.Clear();
                    _keys.Clear();

                    foreach (var k in keys)
                    {
                        k.key.Margin = Padding.Empty;
                        k.key.Size = new Size(60, KeyHeight);
                        Controls.Add(k.key);
                        _keys.Add(k);
                    }

                    ResumeLayout();
                    PerformLayout();
                }

                protected override void OnLayout(LayoutEventArgs levent)
                {
                    base.OnLayout(levent);

                    if (_keys.Count == 0) return;

                    int n = _keys.Count;
                    int gap = Gap;
                    int avail = ClientSize.Width - Padding.Left - Padding.Right;

                    if (avail <= 0) return;

                    // Se lo spazio è poco, riduci i gap prima di schiacciare i tasti.
                    while (gap > 4 && (avail - gap * (n - 1)) < (MinKeyWidth * n))
                        gap--;

                    int usable = Math.Max(0, avail - gap * (n - 1));
                    if (usable <= 0) return;

                    float sum = Math.Max(0.001f, _keys.Sum(k => Math.Max(0.01f, k.weight)));

                    // 1) larghezze proporzionali
                    var widths = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        var w = (int)Math.Floor(usable * (_keys[i].weight / sum));
                        widths[i] = Math.Max(1, w);
                    }

                    // 2) distribuisci i pixel rimanenti per arrivare esattamente a usable
                    int total = widths.Sum();
                    int leftover = usable - total;
                    int kx = 0;
                    while (leftover > 0)
                    {
                        widths[kx % n] += 1;
                        kx++;
                        leftover--;
                    }

                    // 3) applica MinKeyWidth SOLO se ci sta davvero
                    bool canEnforceMin = usable >= (MinKeyWidth * n);
                    if (canEnforceMin && MinKeyWidth > 1)
                    {
                        for (int i = 0; i < n; i++)
                            widths[i] = Math.Max(MinKeyWidth, widths[i]);

                        total = widths.Sum();
                        int excess = total - usable;

                        // Riduci dagli elementi più larghi, senza mai scendere sotto MinKeyWidth.
                        // (evita l'effetto "ultimo tasto negativo" che mandava i tasti fuori.)
                        while (excess > 0)
                        {
                            bool reduced = false;
                            for (int i = 0; i < n && excess > 0; i++)
                            {
                                if (widths[i] > MinKeyWidth)
                                {
                                    widths[i] -= 1;
                                    excess--;
                                    reduced = true;
                                }
                            }
                            if (!reduced) break; // non si può ridurre oltre
                        }

                        // Se ancora in eccesso, rilassa il min (meglio tasti più piccoli che fuori schermo).
                        if (widths.Sum() > usable)
                        {
                            int over = widths.Sum() - usable;
                            for (int i = 0; i < n && over > 0; i++)
                            {
                                int take = Math.Min(over, widths[i] - 1);
                                if (take > 0)
                                {
                                    widths[i] -= take;
                                    over -= take;
                                }
                            }
                        }
                    }

                    int x = Padding.Left;
                    // Adatta l'altezza reale del tasto alla riga (evita tasti "fuori"...)
                    int contentH = Math.Max(1, ClientSize.Height - Padding.Vertical);
                    int kh = Math.Min(KeyHeight, contentH);
                    kh = Math.Max(1, kh);
                    int y = Padding.Top + Math.Max(0, (contentH - kh) / 2);

                    for (int i = 0; i < n; i++)
                    {
                        var (key, _) = _keys[i];
                        int w = Math.Max(1, widths[i]);
                        key.Bounds = new Rectangle(x, y, w, kh);
                        x += w + gap;
                    }
                }
            }

            private sealed class OskKeyButton : Control
            {
                public OskKeyKind KeyKind { get; set; } = OskKeyKind.Char;
                public string? KeyText { get; set; }
                public bool IsPrimary { get; set; }

                public OskKeyButton(string text)
                {
                    Text = text;
                    TabStop = true;
                    Cursor = Cursors.Hand;
                    // Sobrio: font meno "aggressivo" e più compatto
                    Font = new Font("Segoe UI Semibold", 11.0f);
                    ForeColor = Theme.Text;

                    SetStyle(ControlStyles.AllPaintingInWmPaint |
                             ControlStyles.OptimizedDoubleBuffer |
                             ControlStyles.UserPaint |
                             ControlStyles.ResizeRedraw |
                             ControlStyles.Selectable, true);

                    BackColor = Theme.PanelAlt;
                }

                protected override void OnGotFocus(EventArgs e)
                {
                    base.OnGotFocus(e);
                    Invalidate();
                }

                protected override void OnLostFocus(EventArgs e)
                {
                    base.OnLostFocus(e);
                    Invalidate();
                }

                protected override void OnMouseEnter(EventArgs e)
                {
                    base.OnMouseEnter(e);
                    Invalidate();
                }

                protected override void OnMouseLeave(EventArgs e)
                {
                    base.OnMouseLeave(e);
                    Invalidate();
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                    if (rect.Width <= 0 || rect.Height <= 0) return;

                    int radius = 12;

                    // base bg
                    var bg = IsPrimary ? Theme.AccentSoft : Theme.Card;
                    using (var path = DrawHelpers.RoundRect(rect, radius))
                    using (var b = new SolidBrush(bg))
                        e.Graphics.FillPath(b, path);

                    // hover
                    if (ClientRectangle.Contains(PointToClient(Cursor.Position)))
                    {
                        using (var path = DrawHelpers.RoundRect(rect, radius))
                        using (var hov = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
                            e.Graphics.FillPath(hov, path);
                    }

                    // focus ring (moderno)
                    if (Focused)
                    {
                        using (var path = DrawHelpers.RoundRect(rect, radius))
                        using (var p = new Pen(Theme.Accent, 2f))
                            e.Graphics.DrawPath(p, path);
                    }
                    else
                    {
                        // bordo super sottile
                        using (var path = DrawHelpers.RoundRect(rect, radius))
                        using (var p = new Pen(Color.FromArgb(90, Theme.Border), 1f))
                            e.Graphics.DrawPath(p, path);
                    }

                    // text
                    TextRenderer.DrawText(
                        e.Graphics,
                        Text,
                        Font,
                        rect,
                        ForeColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }
        }
    }
}
