#nullable enable
using CinecorePlayer2025.HUD;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Scheda WLED: indirizzo, stato del collegamento verificato dal vivo e interruttore.
    // Prima era una casella di testo generica: non si capiva se l'indirizzo fosse giusto
    // finche' non partiva un film.
    internal sealed class WledSettingsForm : HudModalFormBase
    {
        internal readonly record struct Probe(bool Reachable, bool On, int Brightness);

        private enum LinkState { Checking, Connected, Unreachable, Empty }

        private readonly bool _english;
        private readonly Func<string, CancellationToken, Task<Probe>> _probe;
        private readonly TextBox _address;
        private readonly System.Windows.Forms.Timer _typing = new() { Interval = 550 };
        private CancellationTokenSource? _probeCts;
        private LinkState _state = LinkState.Checking;
        private Probe _last;
        private bool _enabled;
        private bool _toggleHover;

        private static readonly Rectangle InputBounds = new(30, 122, 540, 38);
        private static readonly Rectangle ToggleRow = new(30, 206, 540, 40);
        // Area ridisegnata quando cambia lo stato dell'interruttore: un po' piu' larga della riga.
        private static readonly Rectangle ToggleDirty = Rectangle.Inflate(ToggleRow, 6, 4);

        public string Address => _address.Text.Trim();
        public bool LightsEnabled => _enabled;

        public WledSettingsForm(string address, bool enabled, bool english, Func<string, CancellationToken, Task<Probe>> probe)
        {
            _english = english;
            _probe = probe;
            _enabled = enabled;
            Text = T("Luci WLED", "WLED lights");
            ClientSize = new Size(600, 326);
            MinimumSize = Size;
            MaximumSize = Size;

            var shell = new Panel
            {
                BackColor = Theme.SheetRaised,
                Location = new Point(InputBounds.Left + 12, InputBounds.Top + 1),
                Size = new Size(InputBounds.Width - 24, InputBounds.Height - 2),
                Padding = new Padding(0, 8, 0, 6)
            };
            _address = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = shell.BackColor,
                ForeColor = Theme.Text,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
                Dock = DockStyle.Fill,
                Text = address ?? string.Empty,
                PlaceholderText = "wled.local     192.168.1.50"
            };
            _address.TextChanged += (_, _) => { _state = Address.Length == 0 ? LinkState.Empty : LinkState.Checking; Invalidate(); _typing.Stop(); _typing.Start(); };
            _address.GotFocus += (_, _) => Invalidate();
            _address.LostFocus += (_, _) => Invalidate();
            shell.Controls.Add(_address);

            var test = new Label
            {
                Text = T("Prova il collegamento", "Test the connection"), AutoSize = true, BackColor = Color.Transparent, ForeColor = Theme.Accent, Cursor = Cursors.Hand,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point)
            };
            Button cancel = CreateModalButton(T("Annulla", "Cancel"), DialogResult.Cancel, primary: false);
            Button save = CreateModalButton(T("Salva", "Save"), DialogResult.OK, primary: true);
            cancel.ForeColor = Theme.Text;
            test.Location = new Point(30, 278);
            cancel.SetBounds(366, 268, 96, 38);
            save.SetBounds(472, 268, 98, 38);
            test.Click += (_, _) => StartProbe();

            Controls.Add(shell);
            Controls.Add(test);
            Controls.Add(cancel);
            Controls.Add(save);
            AcceptButton = save;
            CancelButton = cancel;

            _typing.Tick += (_, _) => { _typing.Stop(); StartProbe(); };
            Shown += (_, _) =>
            {
                try { _address.Focus(); _address.SelectionStart = _address.TextLength; _address.SelectionLength = 0; } catch { }
                StartProbe();
            };
        }

        private string T(string italian, string english) => _english ? english : italian;

        private async void StartProbe()
        {
            string address = Address;
            _probeCts?.Cancel();
            _probeCts?.Dispose();
            _probeCts = null;
            if (address.Length == 0) { _state = LinkState.Empty; Invalidate(); return; }

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            _probeCts = cts;
            _state = LinkState.Checking;
            Invalidate();
            Probe result = default;
            try { result = await _probe(address, cts.Token); }
            catch { }
            // Una verifica piu' recente (o la chiusura) ha gia' preso il posto di questa.
            if (IsDisposed || !ReferenceEquals(_probeCts, cts)) return;
            _last = result;
            _state = result.Reachable ? LinkState.Connected : LinkState.Unreachable;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hover = ToggleRow.Contains(e.Location);
            if (hover == _toggleHover) return;
            _toggleHover = hover;
            Cursor = hover ? Cursors.Hand : Cursors.Default;
            Invalidate(ToggleDirty);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_toggleHover) return;
            _toggleHover = false;
            Cursor = Cursors.Default;
            Invalidate(ToggleDirty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && ToggleRow.Contains(e.Location))
            {
                _enabled = !_enabled;
                Invalidate(ToggleDirty);
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var labelFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 7.6f, FontStyle.Regular, GraphicsUnit.Point);
            using var rowFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);

            TextRenderer.DrawText(g, T("Luci WLED", "WLED lights"), titleFont, new Rectangle(30, 14, ClientSize.Width - 60, 42), Theme.Text, Line);
            TextRenderer.DrawText(g, T("Si spengono quando parte il film e tornano com'erano in pausa e alla fine.",
                    "They turn off when the movie starts and come back as they were on pause and at the end."),
                bodyFont, new Rectangle(30, 54, ClientSize.Width - 60, 22), Theme.Muted, Line);

            TextRenderer.DrawText(g, T("Indirizzo", "Address"), bodyFont, new Rectangle(30, 96, 200, 20), Theme.Muted, Line);
            // Campo: una superficie morbida arrotondata, senza sottolineature ne' bordi; un filo d'accento solo con il fuoco.
            using (var shape = Rounded(InputBounds, 8))
            using (var fill = new SolidBrush(Theme.SheetRaised))
            {
                g.FillPath(fill, shape);
                if (_address.Focused) { using var ring = new Pen(Color.FromArgb(150, Theme.Accent)); g.DrawPath(ring, shape); }
            }

            // Stato del collegamento: un punto colorato e una riga di testo.
            (Color dot, string text) = _state switch
            {
                LinkState.Connected => (Color.FromArgb(62, 190, 120), _last.On
                    ? string.Format(T("Collegato     luci accese al {0}%", "Connected     lights on at {0}%"), (int)Math.Round(_last.Brightness / 255.0 * 100))
                    : T("Collegato     luci spente", "Connected     lights off")),
                LinkState.Unreachable => (Theme.Danger, T("Nessuna risposta da questo indirizzo", "No answer from this address")),
                LinkState.Empty => (Color.FromArgb(120, Theme.Muted), T("Inserisci il nome o l'indirizzo IP del dispositivo", "Enter the device name or IP address")),
                _ => (Color.FromArgb(170, Theme.Muted), T("Verifica del collegamento…", "Checking the connection…"))
            };
            var dotBounds = new Rectangle(30, 180, 8, 8);
            using (var fill = new SolidBrush(dot)) g.FillEllipse(fill, dotBounds);
            TextRenderer.DrawText(g, text, bodyFont, new Rectangle(48, 172, ClientSize.Width - 78, 24),
                _state == LinkState.Connected ? Theme.SubtleText : Theme.Muted, Line);

            // Interruttore. Al passaggio del mouse reagisce solo l'interruttore (un tono piu'
            // chiaro): niente riquadro sulla riga. Il riquadro di prima usciva dall'area
            // ridisegnata e lasciava pezzi ai lati, oltre a stonare con il resto della scheda.
            TextRenderer.DrawText(g, T("Controlla le luci durante la riproduzione", "Control the lights during playback"), rowFont,
                new Rectangle(ToggleRow.Left, ToggleRow.Top, ToggleRow.Width - 60, ToggleRow.Height), Theme.Text, Line);
            var track = new RectangleF(ToggleRow.Right - 40, ToggleRow.Top + (ToggleRow.Height - 22) / 2f, 40, 22);
            Color trackColor = _enabled
                ? (_toggleHover ? ControlPaint.Light(Theme.Accent, .25f) : Theme.Accent)
                : Color.FromArgb(_toggleHover ? (Theme.IsLight ? 78 : 88) : (Theme.IsLight ? 50 : 56), Theme.Text);
            var smoothing = g.PixelOffsetMode;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var shape = new GraphicsPath())
            {
                shape.AddArc(track.Left, track.Top, track.Height, track.Height, 90, 180);
                shape.AddArc(track.Right - track.Height, track.Top, track.Height, track.Height, 270, 180);
                shape.CloseFigure();
                using var fill = new SolidBrush(trackColor);
                g.FillPath(fill, shape);
            }
            const float Knob = 16;
            var knobBounds = new RectangleF(_enabled ? track.Right - Knob - 3 : track.Left + 3, track.Top + 3, Knob, Knob);
            using (var fill = new SolidBrush(Color.White)) g.FillEllipse(fill, knobBounds);
            g.PixelOffsetMode = smoothing;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _typing.Dispose();
                try { _probeCts?.Cancel(); _probeCts?.Dispose(); } catch { }
                _probeCts = null;
            }
            base.Dispose(disposing);
        }
    }
}
