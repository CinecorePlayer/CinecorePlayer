#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal static class AutomationText
    {
        public static string Event(string key, bool english) => key switch
        {
            "start" => english ? "When playback starts" : "Quando parte la riproduzione",
            "pause" => english ? "On pause" : "In pausa",
            "resume" => english ? "When playback resumes" : "Quando riprende",
            "stop" => english ? "At the end or on stop" : "Alla fine o allo stop",
            "credits" => english ? "At the end credits" : "Ai titoli di coda",
            _ => key
        };

        public static string Kind(string key, bool english) => key switch
        {
            "http-post" => english ? "HTTP request (POST)" : "Richiesta HTTP (POST)",
            "http-get" => english ? "HTTP request (GET)" : "Richiesta HTTP (GET)",
            "mqtt" => english ? "MQTT message" : "Messaggio MQTT",
            _ => key
        };

        public static string Summary(AutomationRule rule, bool english)
        {
            string target = rule.Kind == "mqtt" ? rule.Topic : rule.Target;
            target = target.Replace("https://", "").Replace("http://", "");
            return Event(rule.Event, english) + "   →   " + (rule.Kind == "mqtt" ? "MQTT" : rule.Kind == "http-get" ? "GET" : "POST") + "  " + target;
        }
    }

    // Campo di testo senza riquadro: solo una linea sotto, d'accento quando ha il fuoco.
    internal sealed class UnderlineTextBox : Panel
    {
        public TextBox Box { get; }
        public UnderlineTextBox(string text, string placeholder, bool password = false)
        {
            Height = 36;
            BackColor = Theme.Sheet;
            Padding = new Padding(12, 9, 12, 7);
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.SheetRaised,
                ForeColor = Theme.Text,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
                Dock = DockStyle.Fill,
                Text = text,
                PlaceholderText = placeholder,
                UseSystemPasswordChar = password
            };
            Box.GotFocus += (_, _) => Invalidate();
            Box.LostFocus += (_, _) => Invalidate();
            Controls.Add(Box);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Campo: una superficie morbida arrotondata, senza sottolineature ne' bordi; un filo d'accento solo con il fuoco.
            e.Graphics.Clear(Theme.Sheet);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var shape = ModalActionButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 8);
            using var fill = new SolidBrush(Theme.SheetRaised); e.Graphics.FillPath(fill, shape);
            if (Box.Focused) { using var ring = new Pen(Color.FromArgb(150, Theme.Accent)); e.Graphics.DrawPath(ring, shape); }
        }
    }

    // Elenco delle automazioni: una riga per regola, interruttore a destra, clic per modificare.
    internal sealed class AutomationsForm : HudModalFormBase
    {
        private const int RowHeight = 56, ListTop = 112, ListLeft = 30;
        private readonly bool _english;
        private readonly Func<AutomationContext> _context;
        private List<AutomationRule> _rules;
        private Point _mouse = new(-1, -1);
        private int _scroll;

        private int ListBottom => ClientSize.Height - 78;
        private int VisibleRows => Math.Max(1, (ListBottom - ListTop) / RowHeight);

        public AutomationsForm(bool english, Func<AutomationContext> context)
        {
            _english = english;
            _context = context;
            _rules = DeviceAutomations.Rules.ToList();
            Text = T("Dispositivi e automazioni", "Devices and automations");
            ClientSize = new Size(720, 540);

            _add = CreateModalButton(T("Aggiungi", "Add"), DialogResult.None, primary: true);
            _close = CreateModalButton(T("Chiudi", "Close"), DialogResult.Cancel, primary: false);
            _close.SetBounds(ClientSize.Width - 30 - 100 - 10 - 96, ClientSize.Height - 58, 96, 38);
            _add.SetBounds(ClientSize.Width - 30 - 100, ClientSize.Height - 58, 100, 38);
            _add.Click += (_, _) => Edit(null);
            Controls.Add(_close);
            Controls.Add(_add);
            CancelButton = _close;
            FitHeight();
        }

        private readonly Button _add, _close;

        // La scheda e' alta quanto le sue righe (fino a sette): con poche automazioni restava mezza vuota.
        private void FitHeight()
        {
            int rows = Math.Clamp(_rules.Count, 1, 7);
            int wanted = ListTop + (_rules.Count == 0 ? 64 : rows * RowHeight) + 20 + 78;
            if (ClientSize.Height != wanted)
            {
                int grow = wanted - ClientSize.Height;
                MinimumSize = MaximumSize = Size.Empty;
                ClientSize = new Size(ClientSize.Width, wanted);
                if (IsHandleCreated) Top = Math.Max(0, Top - grow / 2);
            }
            MinimumSize = MaximumSize = Size;
            _add.Top = _close.Top = ClientSize.Height - 58;
        }

        private string T(string italian, string english) => _english ? english : italian;

        private Rectangle RowBounds(int visibleIndex) => new(ListLeft, ListTop + visibleIndex * RowHeight, ClientSize.Width - ListLeft * 2, RowHeight);
        private static Rectangle SwitchBounds(Rectangle row) => new(row.Right - 40, row.Top + (row.Height - 22) / 2, 40, 22);

        private void Edit(AutomationRule? rule)
        {
            using var editor = new AutomationRuleForm(rule?.Clone(), _english, _context);
            var outcome = SheetPresenter.ShowDialog(editor, this);
            if (outcome == DialogResult.OK && editor.Result != null)
            {
                int index = rule == null ? -1 : _rules.FindIndex(existing => existing.Id == rule.Id);
                if (index >= 0) _rules[index] = editor.Result; else _rules.Add(editor.Result);
            }
            else if (outcome == DialogResult.Abort && rule != null)
                _rules.RemoveAll(existing => existing.Id == rule.Id);
            else return;
            DeviceAutomations.Save(_rules);
            FitHeight();
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _rules.Count - VisibleRows));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;
            Cursor = RowAt(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate(new Rectangle(0, ListTop - 4, ClientSize.Width, ListBottom - ListTop + 8));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouse = new Point(-1, -1);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta), 0, Math.Max(0, _rules.Count - VisibleRows));
            Invalidate();
        }

        private int RowAt(Point point)
        {
            if (point.Y < ListTop || point.Y >= ListTop + VisibleRows * RowHeight || point.X < ListLeft || point.X > ClientSize.Width - ListLeft) return -1;
            int index = _scroll + (point.Y - ListTop) / RowHeight;
            return index < _rules.Count ? index : -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int index = e.Button == MouseButtons.Left ? RowAt(e.Location) : -1;
            if (index < 0) { base.OnMouseDown(e); return; }
            Rectangle row = RowBounds(index - _scroll);
            if (Rectangle.Inflate(SwitchBounds(row), 10, 12).Contains(e.Location))
            {
                _rules[index].Enabled = !_rules[index].Enabled;
                DeviceAutomations.Save(_rules);
                Invalidate();
                return;
            }
            Edit(_rules[index]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            using var titleFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var nameFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 10f, FontStyle.Regular, GraphicsUnit.Point);
            using var smallFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 8.6f, FontStyle.Regular, GraphicsUnit.Point);

            TextRenderer.DrawText(g, T("Dispositivi e automazioni", "Devices and automations"), titleFont, new Rectangle(30, 14, ClientSize.Width - 60, 42), Theme.Text, Line);
            TextRenderer.DrawText(g, T("Quando nel player succede qualcosa, invia un comando a un dispositivo di casa: una richiesta HTTP (Home Assistant, Shelly, Hue…) o un messaggio MQTT.",
                    "When something happens in the player, send a command to a home device: an HTTP request (Home Assistant, Shelly, Hue…) or an MQTT message."),
                bodyFont, new Rectangle(30, 54, ClientSize.Width - 60, 40), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            if (_rules.Count == 0)
            {
                TextRenderer.DrawText(g, T("Nessuna automazione.\nUn esempio: all'avvio del film, una richiesta a Home Assistant che abbassa le luci e chiude le tapparelle.",
                        "No automations yet.\nAn example: when the movie starts, a request to Home Assistant that dims the lights and closes the blinds."),
                    bodyFont, new Rectangle(30, ListTop + 4, ClientSize.Width - 60, 60), Theme.SubtleText,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                return;
            }

            int hover = RowAt(_mouse);
            for (int visible = 0; visible < VisibleRows && visible + _scroll < _rules.Count; visible++)
            {
                int index = visible + _scroll;
                var item = _rules[index];
                Rectangle row = RowBounds(visible);
                bool hot = index == hover;
                string name = string.IsNullOrWhiteSpace(item.Name) ? AutomationText.Kind(item.Kind, _english) : item.Name;
                Color nameInk = !item.Enabled ? Theme.Muted : hot ? Theme.Accent : Theme.Text;
                TextRenderer.DrawText(g, name, nameFont, new Rectangle(row.Left, row.Top + 8, row.Width - 70, 22), nameInk, Line);
                TextRenderer.DrawText(g, AutomationText.Summary(item, _english), smallFont, new Rectangle(row.Left, row.Top + 30, row.Width - 70, 18), Theme.Muted, Line);

                RectangleF track = SwitchBounds(row);
                bool switchHot = hot && Rectangle.Inflate(SwitchBounds(row), 10, 12).Contains(_mouse);
                Color trackColor = item.Enabled ? (switchHot ? ControlPaint.Light(Theme.Accent, .25f) : Theme.Accent)
                    : Color.FromArgb(switchHot ? (Theme.IsLight ? 78 : 88) : (Theme.IsLight ? 50 : 56), Theme.Text);
                var offset = g.PixelOffsetMode;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var shape = new GraphicsPath())
                {
                    shape.AddArc(track.Left, track.Top, track.Height, track.Height, 90, 180);
                    shape.AddArc(track.Right - track.Height, track.Top, track.Height, track.Height, 270, 180);
                    shape.CloseFigure();
                    using var fill = new SolidBrush(trackColor);
                    g.FillPath(fill, shape);
                }
                using (var knob = new SolidBrush(Color.White))
                    g.FillEllipse(knob, item.Enabled ? track.Right - 19 : track.Left + 3, track.Top + 3, 16, 16);
                g.PixelOffsetMode = offset;
            }
            if (_rules.Count > VisibleRows)
            {
                var trackBounds = new Rectangle(ClientSize.Width - 16, ListTop, 3, VisibleRows * RowHeight);
                int thumb = Math.Max(24, trackBounds.Height * VisibleRows / _rules.Count);
                int top = trackBounds.Top + (trackBounds.Height - thumb) * _scroll / Math.Max(1, _rules.Count - VisibleRows);
                ScrollbarChrome.Draw(g, trackBounds, top, thumb);
            }
        }
    }

    // Scheda di una regola: quando, cosa inviare, a chi. "Prova" esegue subito il comando.
    internal sealed class AutomationRuleForm : HudModalFormBase
    {
        private readonly bool _english, _editing;
        private readonly Func<AutomationContext> _context;
        private readonly AutomationRule _rule;
        private readonly UnderlineTextBox _name, _target, _topic, _payload, _user, _password;
        private readonly ChoiceField _event, _kind;
        private readonly Button _delete, _test, _cancel, _save;
        private readonly List<(string Text, Point Location)> _labels = new();
        private Rectangle _toggleRow, _statusRow;
        private bool _videoOnly, _toggleHover, _passwordTouched;
        private string _status = "";
        private bool? _statusOk;

        public AutomationRule? Result { get; private set; }

        public AutomationRuleForm(AutomationRule? rule, bool english, Func<AutomationContext> context)
        {
            _english = english;
            _context = context;
            _editing = rule != null;
            _rule = rule ?? new AutomationRule();
            _videoOnly = _rule.VideoOnly;
            Text = _editing ? T("Modifica automazione", "Edit automation") : T("Nuova automazione", "New automation");
            ClientSize = new Size(620, 560);

            _name = new UnderlineTextBox(_rule.Name, T("es. Luci del salotto", "e.g. Living room lights"));
            _target = new UnderlineTextBox(_rule.Target, "");
            _topic = new UnderlineTextBox(_rule.Topic, "casa/salotto/scena");
            _payload = new UnderlineTextBox(_rule.Payload, "{\"scene\":\"cinema\"}");
            _user = new UnderlineTextBox(_rule.User, T("facoltativo", "optional"));
            _password = new UnderlineTextBox("", string.IsNullOrEmpty(_rule.ProtectedPassword) ? T("facoltativa", "optional") : T("salvata", "saved"), password: true);
            _password.Box.TextChanged += (_, _) => _passwordTouched = true;

            _event = NewChoice(DeviceAutomations.Events.Select(key => AutomationText.Event(key, english)), Array.IndexOf(DeviceAutomations.Events, _rule.Event));
            _kind = NewChoice(DeviceAutomations.Kinds.Select(key => AutomationText.Kind(key, english)), Array.IndexOf(DeviceAutomations.Kinds, _rule.Kind));
            _kind.SelectedIndexChanged += (_, _) => Relayout();

            _delete = CreateModalButton(T("Elimina", "Delete"), DialogResult.Abort, primary: false);
            _delete.ForeColor = Theme.Danger;
            _delete.Visible = _editing;
            _test = CreateModalButton(T("Prova", "Test"), DialogResult.None, primary: false);
            _cancel = CreateModalButton(T("Annulla", "Cancel"), DialogResult.Cancel, primary: false);
            _save = CreateModalButton(T("Salva", "Save"), DialogResult.None, primary: true);
            _test.Click += async (_, _) =>
            {
                var candidate = Collect();
                _status = T("Invio in corso…", "Sending…"); _statusOk = null; Invalidate(_statusRow);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var (ok, detail) = await DeviceAutomations.RunAsync(candidate, _context(), timeout.Token);
                if (IsDisposed) return;
                _statusOk = ok;
                _status = ok ? T("Inviato", "Sent") + ": " + detail : T("Non riuscito", "Failed") + ": " + detail;
                Invalidate(_statusRow);
            };
            _save.Click += (_, _) =>
            {
                var candidate = Collect();
                bool missing = string.IsNullOrWhiteSpace(candidate.Target) || (candidate.Kind == "mqtt" && string.IsNullOrWhiteSpace(candidate.Topic));
                if (missing)
                {
                    _statusOk = false;
                    _status = candidate.Kind == "mqtt" ? T("Servono il broker e il topic", "Broker and topic are required") : T("Serve l'indirizzo", "The address is required");
                    Invalidate(_statusRow);
                    return;
                }
                Result = candidate;
                DialogResult = DialogResult.OK;
                Close();
            };

            Controls.AddRange(new Control[] { _name, _event, _kind, _target, _topic, _payload, _user, _password, _delete, _test, _cancel, _save });
            AcceptButton = _save;
            CancelButton = _cancel;
            Relayout();
            Shown += (_, _) => { try { _name.Box.Focus(); } catch { } };
        }

        private string T(string italian, string english) => _english ? english : italian;

        private ChoiceField NewChoice(IEnumerable<string> items, int selected)
        {
            var field = new ChoiceField
            {
                Underline = true,
                BackColor = Theme.Sheet,
                ForeColor = Theme.Text,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point)
            };
            foreach (string item in items) field.Items.Add(item);
            field.SelectedIndex = Math.Max(0, selected);
            return field;
        }

        private string SelectedKind => DeviceAutomations.Kinds[Math.Clamp(_kind.SelectedIndex, 0, DeviceAutomations.Kinds.Length - 1)];

        private AutomationRule Collect()
        {
            var rule = _rule.Clone();
            rule.Name = _name.Box.Text.Trim();
            rule.Event = DeviceAutomations.Events[Math.Clamp(_event.SelectedIndex, 0, DeviceAutomations.Events.Length - 1)];
            rule.Kind = SelectedKind;
            rule.Target = _target.Box.Text.Trim();
            rule.Topic = _topic.Box.Text.Trim();
            rule.Payload = _payload.Box.Text;
            rule.User = _user.Box.Text.Trim();
            if (_passwordTouched) rule.ProtectedPassword = DeviceAutomations.Protect(_password.Box.Text);
            rule.VideoOnly = _videoOnly;
            return rule;
        }

        // I campi cambiano con il tipo: topic e credenziali solo per MQTT, contenuto non per GET.
        private void Relayout()
        {
            SuspendLayout();
            _labels.Clear();
            const int Left = 30, Gap = 20;
            int width = ClientSize.Width - Left * 2, half = (width - Gap) / 2;
            int y = 74;
            void Row(string label, Control control, int x, int w) { _labels.Add((label, new Point(x, y))); control.SetBounds(x, y + 18, w, 36); }

            string kind = SelectedKind;
            bool mqtt = kind == "mqtt";
            Row(T("Nome", "Name"), _name, Left, width); y += 70;
            Row(T("Quando", "When"), _event, Left, half);
            Row(T("Invia", "Send"), _kind, Left + half + Gap, half); y += 70;
            _target.Box.PlaceholderText = mqtt ? "192.168.1.10:1883" : "http://homeassistant.local:8123/api/webhook/cinema";
            Row(mqtt ? "Broker" : T("Indirizzo", "Address"), _target, Left, width); y += 70;
            _topic.Visible = mqtt;
            if (mqtt) { Row("Topic", _topic, Left, width); y += 70; }
            // Variabile a parte: Control.Visible risponde "falso" finche' la scheda non e' a schermo.
            bool hasPayload = kind != "http-get";
            _payload.Visible = hasPayload;
            if (hasPayload) { Row(mqtt ? T("Messaggio", "Message") : T("Contenuto", "Body"), _payload, Left, width); y += 70; }
            _user.Visible = _password.Visible = mqtt;
            if (mqtt)
            {
                Row(T("Utente", "User"), _user, Left, half);
                Row("Password", _password, Left + half + Gap, half); y += 70;
            }
            _toggleRow = new Rectangle(Left, y, width, 36); y += 44;
            _statusRow = new Rectangle(Left, y, width, 24); y += 40;

            _delete.SetBounds(Left, y, 96, 38);
            _test.SetBounds(_editing ? Left + 106 : Left, y, 92, 38);
            _cancel.SetBounds(ClientSize.Width - Left - 100 - 10 - 96, y, 96, 38);
            _save.SetBounds(ClientSize.Width - Left - 100, y, 100, 38);
            MinimumSize = MaximumSize = Size.Empty;
            ClientSize = new Size(ClientSize.Width, y + 38 + 22);
            MinimumSize = MaximumSize = Size;
            ResumeLayout();
            if (Owner != null && Visible)
                Location = new Point(Location.X, Owner.Top + (Owner.Height - Height) / 2);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hover = _toggleRow.Contains(e.Location);
            if (hover == _toggleHover) return;
            _toggleHover = hover;
            Cursor = hover ? Cursors.Hand : Cursors.Default;
            Invalidate(Rectangle.Inflate(_toggleRow, 6, 4));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_toggleHover) return;
            _toggleHover = false;
            Cursor = Cursors.Default;
            Invalidate(Rectangle.Inflate(_toggleRow, 6, 4));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _toggleRow.Contains(e.Location))
            {
                _videoOnly = !_videoOnly;
                Invalidate(Rectangle.Inflate(_toggleRow, 6, 4));
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
            using var labelFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 7.6f, FontStyle.Regular, GraphicsUnit.Point);
            using var rowFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            using var bodyFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.2f, FontStyle.Regular, GraphicsUnit.Point);

            TextRenderer.DrawText(g, Text, titleFont, new Rectangle(30, 14, ClientSize.Width - 60, 42), Theme.Text, Line);
            foreach (var (text, location) in _labels)
                TextRenderer.DrawText(g, text, labelFont, new Rectangle(location.X, location.Y, 260, 16), Theme.Muted, Line);

            TextRenderer.DrawText(g, T("Solo durante i video (non con la musica)", "Only during video (not with music)"), rowFont,
                new Rectangle(_toggleRow.Left, _toggleRow.Top, _toggleRow.Width - 60, _toggleRow.Height), Theme.Text, Line);
            var track = new RectangleF(_toggleRow.Right - 40, _toggleRow.Top + (_toggleRow.Height - 22) / 2f, 40, 22);
            Color trackColor = _videoOnly ? (_toggleHover ? ControlPaint.Light(Theme.Accent, .25f) : Theme.Accent)
                : Color.FromArgb(_toggleHover ? (Theme.IsLight ? 78 : 88) : (Theme.IsLight ? 50 : 56), Theme.Text);
            var offset = g.PixelOffsetMode;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var shape = new GraphicsPath())
            {
                shape.AddArc(track.Left, track.Top, track.Height, track.Height, 90, 180);
                shape.AddArc(track.Right - track.Height, track.Top, track.Height, track.Height, 270, 180);
                shape.CloseFigure();
                using var fill = new SolidBrush(trackColor);
                g.FillPath(fill, shape);
            }
            using (var knob = new SolidBrush(Color.White))
                g.FillEllipse(knob, _videoOnly ? track.Right - 19 : track.Left + 3, track.Top + 3, 16, 16);
            g.PixelOffsetMode = offset;

            if (_status.Length > 0)
            {
                Color dot = _statusOk == true ? Color.FromArgb(62, 190, 120) : _statusOk == false ? Theme.Danger : Color.FromArgb(170, Theme.Muted);
                using (var fill = new SolidBrush(dot)) g.FillEllipse(fill, _statusRow.Left, _statusRow.Top + 8, 8, 8);
                TextRenderer.DrawText(g, _status, bodyFont, new Rectangle(_statusRow.Left + 18, _statusRow.Top, _statusRow.Width - 18, _statusRow.Height), Theme.SubtleText, Line);
            }
            else
                TextRenderer.DrawText(g, T("Nel testo puoi usare {title}, {event}, {position} e {duration}.", "You can use {title}, {event}, {position} and {duration} in the text."),
                    bodyFont, _statusRow, Theme.Muted, Line);
        }
    }
}
