#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using CinecorePlayer2025.HUD;

namespace CinecorePlayer2025;
internal sealed class CredentialsForm : HudModalFormBase
{
    private readonly List<TextBox> _fields = new();
    public string Value(int index) => _fields[index].Text.Trim();
    public CredentialsForm(string title, string description, bool english, params (string Label, string Value, bool Secret)[] fields)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 218 + fields.Length * 76);
        BackColor = Theme.Card;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30), ColumnCount = 1, BackColor = Theme.Card };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control control, int height) { int row = layout.RowCount++; layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); control.Dock = DockStyle.Fill; layout.Controls.Add(control, 0, row); }
        Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 18), ForeColor = Theme.Text }, 42);
        Add(new Label { Text = description, Font = new Font("Segoe UI", 10), ForeColor = Theme.Muted }, 58);
        foreach (var field in fields)
        {
            Add(new Label { Text = field.Label, Font = new Font("Segoe UI", 10), ForeColor = Theme.Text }, 28);
            var input = new TextBox { Text = field.Value, UseSystemPasswordChar = field.Secret, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Panel, ForeColor = Theme.Text, Font = new Font("Segoe UI", 11), Margin = new Padding(0, 0, 0, 14) };
            _fields.Add(input); Add(input, 48);
        }
        var footer = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var save = CreateModalButton(english ? "Save" : "Salva", DialogResult.OK, true); save.Size = new Size(108, 38);
        var cancel = CreateModalButton(english ? "Cancel" : "Annulla", DialogResult.Cancel, false); cancel.Size = new Size(108, 38);
        footer.Controls.Add(save); footer.Controls.Add(cancel); Add(footer, 58);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        Shown += (_, _) => _fields[0].Focus();
    }
}
