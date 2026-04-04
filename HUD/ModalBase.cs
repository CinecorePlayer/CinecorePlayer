#nullable enable
using CinecorePlayer2025.HUD;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    /// <summary>
    /// Overlay modale full-screen con card centrale (bordo 1px Theme.Border) e backdrop scuro.
    /// Progettato per essere RIUSATO (non rimosso/disposto) se necessario.
    /// </summary>
    internal abstract class ModalBase : UserControl
    {
        private readonly ModalCard _card;
        private readonly Panel _headerOuter;
        private readonly Panel _bottomBar;

        private readonly Label _lblTitle;
        private readonly Label _lblSubtitle;

        protected readonly Panel ContentHost;
        protected readonly FlowLayoutPanel ButtonsHost;

        private ModalButton? _primaryButton;

        public event Action? Closed;

        public Color OverlayColor { get; set; } = Theme.BackdropDim;
        public bool CloseOnBackdropClick { get; set; } = true;
        public bool CloseOnEscape { get; set; } = true;

        /// <summary>
        /// Se TRUE: quando chiudi il modal viene rimosso dal Parent.
        /// Nel tuo PlayerForm i modali sono creati una volta e riusati: per Settings/Credits mettilo a FALSE.
        /// </summary>
        public bool RemoveFromParentOnClose { get; set; } = true;

        /// <summary>
        /// Se TRUE: dopo la chiusura viene chiamato Dispose().
        /// Per modali riusabili (come i tuoi) deve stare FALSE.
        /// </summary>
        public bool AutoDisposeOnClose { get; set; } = false;

        private Size _cardMinSize = new(900, 580);
        public Size CardMinSize
        {
            get => _cardMinSize;
            set { _cardMinSize = value; _card.MinimumSize = value; CenterCard(); }
        }

        private Size? _cardMaxSize = null;
        public Size? CardMaxSize
        {
            get => _cardMaxSize;
            set { _cardMaxSize = value; CenterCard(); }
        }

        private int _cardMargin = 32;
        public int CardMargin
        {
            get => _cardMargin;
            set { _cardMargin = Math.Max(0, value); CenterCard(); }
        }

        public bool HeaderVisible
        {
            get => _headerOuter.Visible;
            set { _headerOuter.Visible = value; PerformLayout(); CenterCard(); }
        }

        public bool FooterVisible
        {
            get => _bottomBar.Visible;
            set { _bottomBar.Visible = value; PerformLayout(); CenterCard(); }
        }

        public string TitleText
        {
            get => _lblTitle.Text;
            set => _lblTitle.Text = value;
        }

        public string SubtitleText
        {
            get => _lblSubtitle.Text;
            set
            {
                _lblSubtitle.Text = value ?? string.Empty;
                _lblSubtitle.Visible = !string.IsNullOrWhiteSpace(value);
            }
        }

        protected ModalBase(string title = "", string? subtitle = null)
        {
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            BackColor = Color.Transparent;

            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);

            TabStop = true;

            // Click sul backdrop
            MouseDown += (_, e) =>
            {
                if (!CloseOnBackdropClick) return;
                if (!_card.Bounds.Contains(e.Location))
                    CloseModal();
            };

            // CARD
            _card = new ModalCard
            {
                BackColor = Theme.Panel,
                MinimumSize = _cardMinSize,
                Padding = new Padding(0)
            };
            Controls.Add(_card);

            // HEADER
            _headerOuter = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = Theme.Panel,
                Padding = new Padding(16, 10, 16, 0),
                Visible = true
            };

            _lblTitle = new Label
            {
                Text = title,
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 13f),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _lblSubtitle = new Label
            {
                Text = subtitle ?? string.Empty,
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Theme.SubtleText,
                Font = new Font("Segoe UI", 9.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Visible = !string.IsNullOrWhiteSpace(subtitle)
            };

            _headerOuter.Controls.Add(_lblSubtitle);
            _headerOuter.Controls.Add(_lblTitle);

            // CONTENT
            ContentHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            // FOOTER (host bottoni base)
            _bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                BackColor = Theme.Panel,
                Padding = new Padding(0, 10, 16, 10),
                Visible = true
            };

            ButtonsHost = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Right,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            _bottomBar.Controls.Add(ButtonsHost);

            _card.Controls.Add(ContentHost);
            _card.Controls.Add(_bottomBar);
            _card.Controls.Add(_headerOuter);

            Resize += (_, __) => CenterCard();
            VisibleChanged += (_, __) =>
            {
                if (Visible)
                {
                    CenterCard();
                    try { Focus(); } catch { }
                }
            };
        }

        public void ShowOver(Control host)
        {
            Dock = DockStyle.Fill;

            if (Parent != host)
            {
                host.Controls.Add(this);
                host.Controls.SetChildIndex(this, 0);
            }

            Visible = true;
            BringToFront();
            Focus();
            CenterCard();
        }

        protected void CloseModal()
        {
            // modalità “riuso”: nascondi e basta
            Visible = false;

            Closed?.Invoke();

            if (RemoveFromParentOnClose)
            {
                try { Parent?.Controls.Remove(this); } catch { }
            }

            if (AutoDisposeOnClose)
                Dispose();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (CloseOnEscape && keyData == Keys.Escape)
            {
                CloseModal();
                return true;
            }

            if (keyData == Keys.Enter && _primaryButton != null)
            {
                _primaryButton.TriggerClick();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var br = new SolidBrush(OverlayColor);
            e.Graphics.FillRectangle(br, ClientRectangle);
        }

        private void CenterCard()
        {
            if (!IsHandleCreated) return;

            int margin = Math.Max(0, CardMargin);

            int availW = Math.Max(1, ClientSize.Width - margin * 2);
            int availH = Math.Max(1, ClientSize.Height - margin * 2);

            int w = Math.Max(CardMinSize.Width, availW);
            int h = Math.Max(CardMinSize.Height, availH);

            if (CardMaxSize.HasValue)
            {
                w = Math.Min(w, CardMaxSize.Value.Width);
                h = Math.Min(h, CardMaxSize.Value.Height);
            }

            w = Math.Min(w, Math.Max(1, ClientSize.Width - 8));
            h = Math.Min(h, Math.Max(1, ClientSize.Height - 8));

            _card.Size = new Size(w, h);

            int x = (ClientSize.Width - _card.Width) / 2;
            int y = (ClientSize.Height - _card.Height) / 2;

            x = Math.Max(margin, x);
            y = Math.Max(margin, y);

            _card.Location = new Point(x, y);
            _card.BringToFront();
        }

        protected ModalButton AddPrimaryButton(string text, Action onClick)
        {
            var btn = new ModalButton(text, ModalButton.Variant.Primary)
            {
                Margin = new Padding(8, 0, 0, 0)
            };
            btn.Click += (_, __) => onClick();
            ButtonsHost.Controls.Add(btn);

            _primaryButton ??= btn;
            return btn;
        }

        protected ModalButton AddSecondaryButton(string text, Action onClick)
        {
            var btn = new ModalButton(text, ModalButton.Variant.Secondary)
            {
                Margin = new Padding(8, 0, 0, 0)
            };
            btn.Click += (_, __) => onClick();
            ButtonsHost.Controls.Add(btn);
            return btn;
        }

        // ===================== CARD =====================
        private sealed class ModalCard : Panel
        {
            public ModalCard()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.None;

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);

                using (var br = new SolidBrush(Theme.Panel))
                    g.FillRectangle(br, rect);

                using var pen = new Pen(Theme.Border, 1f);
                g.DrawRectangle(pen, rect);
            }
        }

        // ===================== BUTTON =====================
        protected sealed class ModalButton : Control
        {
            public enum Variant { Primary, Secondary }

            private readonly Variant _variant;
            private bool _hover;
            private bool _down;
            private readonly string _text;

            public ModalButton(string text, Variant variant)
            {
                _text = text;
                _variant = variant;

                Cursor = Cursors.Hand;
                Size = new Size(140, 32);
                TabStop = true;

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.Selectable, true);

                BackColor = Color.Transparent;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, e) =>
                {
                    if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); }
                };
                MouseUp += (_, e) =>
                {
                    if (_down && e.Button == MouseButtons.Left)
                    {
                        _down = false;
                        Invalidate();
                        OnClick(EventArgs.Empty);
                    }
                };
            }

            internal void TriggerClick() => OnClick(EventArgs.Empty);

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Color.Transparent);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);

                using var path = new GraphicsPath();
                int r = 4;
                int d = r * 2;
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();

                if (_variant == Variant.Primary)
                {
                    var cTop = Theme.Accent;
                    var cBot = Theme.AccentSoft;

                    if (_down) { cTop = ControlPaint.Dark(cTop); cBot = ControlPaint.Dark(cBot); }
                    else if (_hover) { cTop = ControlPaint.Light(cTop); }

                    using var lg = new LinearGradientBrush(rect, cTop, cBot, LinearGradientMode.Vertical);
                    g.FillPath(lg, path);
                }
                else
                {
                    var baseCol = Theme.Panel;
                    if (_hover) baseCol = ControlPaint.Light(baseCol);
                    if (_down) baseCol = ControlPaint.Dark(baseCol);

                    using (var br = new SolidBrush(baseCol))
                        g.FillPath(br, path);

                    using var pen = new Pen(Theme.Border);
                    g.DrawPath(pen, path);
                }

                using var f = new Font("Segoe UI Semibold", 10.5f);
                TextRenderer.DrawText(
                    g,
                    _text,
                    f,
                    rect,
                    Color.White,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
            }
        }
    }
}
