using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025;

internal sealed partial class CinematicMediaLibraryPage
{
    private sealed class FolderSourceList : Control
    {
        public List<string> Items { get; } = new();
        public int ItemHeight { get; set; } = 74;
        public event DrawItemEventHandler? DrawItem;
        public event EventHandler? SelectedIndexChanged;
        /// <summary>Clic sul cestino della riga (a destra).</summary>
        public event Action<int>? RemoveRequested;
        public int HoverIndex { get; private set; } = -1;
        public bool HoverOnRemove { get; private set; }
        public Rectangle RemoveBounds(Rectangle row) => new(row.Right - 32, row.Top + (row.Height - 32) / 2, 32, 32);
        private int _selected = -1, _offset;
        private bool _scrolling;
        private int VisibleRows => Math.Max(1, Height / ItemHeight);
        public int SelectedIndex
        {
            get => _selected;
            set
            {
                _selected = Math.Clamp(value, -1, Items.Count - 1);
                if (_selected >= 0) _offset = Math.Clamp(_offset, Math.Max(0, _selected - VisibleRows + 1), _selected);
                Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public object? SelectedItem => _selected >= 0 && _selected < Items.Count ? Items[_selected] : null;
        public FolderSourceList() { DoubleBuffered = true; TabStop = true; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true); }
        public int IndexFromPoint(Point p) { int i = _offset + p.Y / ItemHeight; return ClientRectangle.Contains(p) && p.X < Width - 16 && i < Items.Count ? i : -1; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); _offset = Math.Clamp(_offset,0,Math.Max(0,Items.Count-VisibleRows));
            for (int i=_offset; i<Math.Min(Items.Count,_offset+VisibleRows);i++)
                DrawItem?.Invoke(this,new DrawItemEventArgs(e.Graphics,Font,new Rectangle(0,(i-_offset)*ItemHeight,Math.Max(1,Width-24),ItemHeight),i,i==_selected?DrawItemState.Selected:DrawItemState.None,ForeColor,BackColor));
            if (Items.Count > VisibleRows)
            {
                var track = new Rectangle(Width - 10, 4, 3, Math.Max(0, Height - 8));
                int thumb = Math.Min(track.Height, Math.Max(28, track.Height * VisibleRows / Items.Count));
                int y = track.Top + (track.Height - thumb) * _offset / Math.Max(1, Items.Count - VisibleRows);
                ScrollbarChrome.Draw(e.Graphics, track, y, thumb);
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if(e.Button != MouseButtons.Left)return; Focus();
            if(e.X>=Width-16 && Items.Count>VisibleRows) { _scrolling=true;Capture=true;ScrollAt(e.Y); }
            else
            {
                int index = IndexFromPoint(e.Location);
                SelectedIndex = index;
                if (index >= 0 && RemoveBounds(RowBounds(index)).Contains(e.Location)) RemoveRequested?.Invoke(index);
            }
        }
        private void ScrollAt(int y) { _offset=(int)Math.Round(Math.Clamp(y/(double)Math.Max(1,Height),0,1)*Math.Max(0,Items.Count-VisibleRows));Invalidate(); }
        // La colonna della barra di scorrimento si riserva solo quando serve: senza, la riga arriva al margine della scheda.
        private Rectangle RowBounds(int index) => new(0, (index - _offset) * ItemHeight, Math.Max(1, Width - (Items.Count > VisibleRows ? 24 : 0)), ItemHeight);
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if(_scrolling)ScrollAt(e.Y);
            int hover = IndexFromPoint(e.Location);
            bool onRemove = hover >= 0 && RemoveBounds(RowBounds(hover)).Contains(e.Location);
            if (hover != HoverIndex || onRemove != HoverOnRemove) { HoverIndex = hover; HoverOnRemove = onRemove; Cursor = onRemove ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (HoverIndex != -1) { HoverIndex = -1; HoverOnRemove = false; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { _scrolling=false;Capture=false;base.OnMouseUp(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); _offset=Math.Clamp(_offset-Math.Sign(e.Delta)*3,0,Math.Max(0,Items.Count-VisibleRows));Invalidate(); }
        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if(e.KeyCode is Keys.Up or Keys.Down or Keys.Home or Keys.End){SelectedIndex=e.KeyCode==Keys.Home?0:e.KeyCode==Keys.End?Items.Count-1:Math.Clamp(_selected+(e.KeyCode==Keys.Up?-1:1),0,Math.Max(0,Items.Count-1));e.Handled=true;} }
    }
}
