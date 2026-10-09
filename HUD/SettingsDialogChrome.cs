#nullable enable
using CinecorePlayer2025.HUD;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal class HudModalFormBase : Form
    {
        private const int WmNclbuttondown = 0xA1;
        private const int HtCaption = 0x2;

        protected HudModalFormBase()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Sheet;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            KeyPreview = true;
            Padding = new Padding(1);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CinecorePlayer2025.Utilities.WindowCorners.Apply(this, 14, border: Theme.BorderAccent);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            CinecorePlayer2025.Utilities.WindowCorners.Refresh(this, 14);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 1 || bounds.Height <= 1)
                return;

            using var gradient = new LinearGradientBrush(
                bounds,
                Color.FromArgb(255, BackColor),
                Color.FromArgb(255, BackColor),
                LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(gradient, bounds);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // With system corners the compositor draws the outline (coloured in OnHandleCreated).
            if (CinecorePlayer2025.Utilities.WindowCorners.SystemRounded(this))
                return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle borderRect = Rectangle.Inflate(ClientRectangle, -1, -1);
            using GraphicsPath borderPath = Rounded(borderRect, 13);
            using var border = new Pen(Color.FromArgb(110, Theme.BorderAccent), 1.2f);
            e.Graphics.DrawPath(border, borderPath);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || e.Y > 62)
                return;

            try
            {
                ReleaseCapture();
                SendMessage(Handle, WmNclbuttondown, HtCaption, 0);
            }
            catch { }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected static Button CreateModalButton(string text, DialogResult result, bool primary)
        {
            var button = new SettingsActionButton
            {
                Text = text,
                DialogResult = result,
                FlatStyle = FlatStyle.Flat,
                // Il secondario ha una sua forma tenue: solo testo sembrava un'etichetta, non un pulsante.
                BackColor = primary ? Theme.Accent : Theme.SheetRaised,
                ForeColor = primary ? Color.White : Theme.Text,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
                Cursor = Cursors.Hand,
                TabStop = true
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.BorderColor = Color.FromArgb(110, Theme.BorderAccent);
            button.FlatAppearance.MouseOverBackColor = primary ? Theme.AccentSoft : Theme.SheetHover;
            button.FlatAppearance.MouseDownBackColor = Theme.Selection;
            return button;
        }

        protected static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            int r = Math.Max(1, Math.Min(radius, Math.Min(Math.Max(1, rect.Width), Math.Max(1, rect.Height)) / 2));
            int d = r * 2;
            var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
    }

    /// <summary>
    /// Scheda costruita sul posto da chi la apre (controlli aggiunti dall'esterno) con la stessa
    /// cornice delle altre: fondo, angoli, Esc, doppio buffer e la X di chiusura standard.
    /// </summary>
    internal sealed class PlainSheetForm : HudModalFormBase
    {
        private bool _closeHover;
        public bool ShowCloseButton { get; set; } = true;
        private Rectangle CloseBounds => new(ClientSize.Width - 30 - 32, 18, 32, 32);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool close = ShowCloseButton && CloseBounds.Contains(e.Location);
            Cursor = close ? Cursors.Hand : Cursors.Default;
            if (close != _closeHover) { _closeHover = close; Invalidate(Rectangle.Inflate(CloseBounds, 2, 2)); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_closeHover) { _closeHover = false; Invalidate(Rectangle.Inflate(CloseBounds, 2, 2)); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (ShowCloseButton && e.Button == MouseButtons.Left && CloseBounds.Contains(e.Location)) { DialogResult = DialogResult.Cancel; Close(); return; }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ShowCloseButton) Theme.DrawCloseButton(e.Graphics, CloseBounds, _closeHover);
        }
    }

    internal sealed class SettingsActionButton : Button
    {
        private bool _hot, _pressed;
        public SettingsActionButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Sheet);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 2), Math.Max(1, Height - 2));
            using var shape = ModalActionButton.Rounded(bounds, 9);
            Color fill = !Enabled ? Theme.Sheet : _pressed ? FlatAppearance.MouseDownBackColor : _hot ? FlatAppearance.MouseOverBackColor : BackColor;
            using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, shape);
            if (Focused) { using var border = new Pen(Theme.AccentSoft); e.Graphics.DrawPath(border, shape); }
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class SettingsTextPromptForm : HudModalFormBase
    {
        private readonly TextBox _valueBox;
        private readonly string _title;
        private readonly string _description;

        public string Value => _valueBox.Text.Trim();

        public SettingsTextPromptForm(string title, string description, string initialValue, bool english, bool password = false)
        {
            _title = string.IsNullOrWhiteSpace(title) ? (english ? "Setting" : "Impostazione") : title.Trim();
            _description = description ?? string.Empty;
            Text = _title;
            ClientSize = new Size(610, 226);
            MinimumSize = Size;
            MaximumSize = Size;

            var inputShell = new Panel
            {
                // Standard WinForms panels do not support an alpha background.
                // Keep the shell opaque; the modal itself already supplies depth.
                BackColor = Theme.Sheet,
                Location = new Point(30, 104),
                Size = new Size(550, 42),
                Padding = new Padding(14, 10, 14, 8)
            };
            _valueBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = inputShell.BackColor,
                ForeColor = Theme.Text,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
                Dock = DockStyle.Fill,
                Text = initialValue ?? string.Empty,
                UseSystemPasswordChar = password
            };
            inputShell.Controls.Add(_valueBox);

            Button cancel = CreateModalButton(english ? "Cancel" : "Annulla", DialogResult.Cancel, primary: false);
            Button ok = CreateModalButton(english ? "Save" : "Salva", DialogResult.OK, primary: true);
            cancel.SetBounds(374, 169, 96, 38);
            ok.SetBounds(480, 169, 100, 38);

            Controls.Add(inputShell);
            Controls.Add(cancel);
            Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;

            Shown += (_, __) =>
            {
                try
                {
                    _valueBox.Focus();
                    _valueBox.SelectAll();
                }
                catch { }
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            TextRenderer.DrawText(e.Graphics, _title, titleFont,
                new Rectangle(30, 14, ClientSize.Width - 60, 42),
                Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, _description, bodyFont,
                new Rectangle(30, 56, ClientSize.Width - 60, 35),
                Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            Rectangle inputBorder = new(29, 103, 552, 44);
            using GraphicsPath path = Rounded(inputBorder, 8);
            using var border = new Pen(_valueBox.Focused ? Theme.Accent : Theme.BorderAccent, 1f);
            e.Graphics.DrawLine(border, inputBorder.Left, inputBorder.Bottom, inputBorder.Right, inputBorder.Bottom);
        }
    }
}
