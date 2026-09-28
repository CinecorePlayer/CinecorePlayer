#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025.HUD;

// A painted, keyboard-accessible choice surface. Selection is dispatched after
// the popup closes, so rebuilding the player cannot dispose an active menu stack.
internal sealed class ChoicePopup : Form
{
    internal sealed record Option(string Label, Action Select, bool Selected = false);
    private Option[] _options;
    private readonly Option[] _allOptions;
    private readonly int _top;
    private readonly Control _owner;
    private int _index, _offset;
    private int _pressed = -1;
    private const int Row = 40;
    private int Rows => Math.Max(1, (ClientSize.Height - _top - 6) / Row);
    private ChoicePopup(Control owner, IEnumerable<Option> options, int width, bool searchable)
    {
        _owner = owner; _options = options.ToArray();
        _allOptions = _options; _top = searchable ? 48 : 6;
        _index = Math.Max(0, Array.FindIndex(_options, x => x.Selected));
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; DoubleBuffered = true;
        BackColor = Color.FromArgb(8, 14, 20); ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f); KeyPreview = true;
        ClientSize = new Size(Math.Max(220, width), Math.Min(10, _options.Length) * Row + _top + 6);
        if (searchable)
        {
            var search = new TextBox { Bounds = new Rectangle(16, 12, ClientSize.Width - 32, 26),
                BorderStyle = BorderStyle.None, BackColor = BackColor, ForeColor = ForeColor, Font = Font,
                PlaceholderText = "Cerca…", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            search.TextChanged += (_, _) =>
            {
                _options = _allOptions.Where(x => x.Label.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
                _offset = 0; _index = Math.Max(0, Array.FindIndex(_options, x => x.Selected)); Invalidate();
            };
            Controls.Add(search); Shown += (_, _) => search.Focus();
        }
        Deactivate += (_, _) => Close();
    }
    internal static void Show(Control owner, Point anchor, IEnumerable<Option> options, int width = 280, bool searchable = false)
    {
        var popup = new ChoicePopup(owner, options, width, searchable);
        if (popup._options.Length == 0) { popup.Dispose(); return; }
        var point = owner.PointToScreen(anchor);
        var area = Screen.FromPoint(point).WorkingArea;
        popup.Size = new Size(Math.Min(popup.Width, area.Width), Math.Min(popup.Height, area.Height));
        popup.Location = new Point(Math.Clamp(point.X, area.Left, area.Right - popup.Width), Math.Clamp(point.Y, area.Top, area.Bottom - popup.Height));
        popup.EnsureVisible(); popup.TopMost = owner.FindForm()?.TopMost == true;
        using(var shape=ModalActionButton.Rounded(popup.ClientRectangle,10)) popup.Region=new Region(shape);
        popup.Show(owner.FindForm());
    }
    private void EnsureVisible() { _offset = Math.Clamp(_offset, Math.Max(0, _index - Rows + 1), _index); Invalidate(); }
    private void Commit()
    {
        if (_options.Length == 0) return;
        var action = _options[_index].Select;
        Close();
        try { if (!_owner.IsDisposed && _owner.IsHandleCreated) _owner.BeginInvoke(new Action(() => { if (!_owner.IsDisposed) action(); })); }
        catch (InvalidOperationException) { }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
        else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space && ActiveControl is not TextBox) Commit();
        else if (_options.Length > 0 && ((e.KeyCode is Keys.Up or Keys.Down) || (ActiveControl is not TextBox && (e.KeyCode is Keys.Home or Keys.End))))
        {
            _index = e.KeyCode == Keys.Home ? 0 : e.KeyCode == Keys.End ? _options.Length - 1 : Math.Clamp(_index + (e.KeyCode == Keys.Up ? -1 : 1), 0, _options.Length - 1);
            EnsureVisible();
        }
        else return;
        e.Handled = true; e.SuppressKeyPress = true;
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = IndexAt(e.Location); if (i >= 0 && i != _index) { _index = i; Invalidate(); } }
    private int IndexAt(Point point) => point.X >= 6 && point.X < Width-16 && point.Y >= _top && point.Y < _top+Math.Min(Rows,_options.Length-_offset)*Row ? _offset+(point.Y-_top)/Row : -1;
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed=e.Button==MouseButtons.Left?IndexAt(e.Location):-1; }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); int index=IndexAt(e.Location); if(e.Button==MouseButtons.Left && index>=0 && index==_pressed){_index=index;_pressed=-1;Commit();}else _pressed=-1; }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (_options.Length == 0) return; _offset = Math.Clamp(_offset - Math.Sign(e.Delta) * 3, 0, Math.Max(0, _options.Length - Rows)); _index = Math.Clamp(_index, _offset, Math.Min(_options.Length - 1, _offset + Rows - 1)); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics;
        g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var shape=ModalActionButton.Rounded(new Rectangle(0,0,Width-1,Height-1),10);
        using var border = new Pen(Color.FromArgb(36, 49, 61)); g.DrawPath(border,shape);
        for (int r = 0; r < Rows && r + _offset < _options.Length; r++)
        {
            int i = r + _offset; var box = new Rectangle(6, _top + r * Row, Width - 12, Row);
            if (i == _index) { using var fill = new SolidBrush(Color.FromArgb(23,35,46)); using var selection=ModalActionButton.Rounded(box,6); g.FillPath(fill,selection); }
            TextRenderer.DrawText(g, _options[i].Label, Font, new Rectangle(18, box.Y, Width - 54, Row), ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (_options[i].Selected) { using var dot = new SolidBrush(Theme.Accent); g.FillEllipse(dot, Width - 26, box.Y + 16, 8, 8); }
        }
        if (_options.Length > Rows)
        {
            var track = new Rectangle(Width - 11, _top + 4, 3, Math.Max(0, Height - _top - 12));
            int height = Math.Min(track.Height, Math.Max(20, track.Height * Rows / _options.Length));
            int top = track.Top + (track.Height - height) * _offset / Math.Max(1, _options.Length - Rows);
            ScrollbarChrome.Draw(g, track, top, height);
        }
    }
}
