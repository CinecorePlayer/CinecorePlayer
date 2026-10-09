using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD;

// The field and its popup share the painted menu implementation; no native
// ComboBox dropdown is created inside a renderer property-page host.
internal sealed class ChoiceField : Control
{
    public List<object> Items { get; } = new();
    public event EventHandler? SelectedIndexChanged;
    public event EventHandler? DropDown;
    public event DrawItemEventHandler? DrawItem;
    public ComboBoxStyle DropDownStyle { get; set; }
    public FlatStyle FlatStyle { get; set; }
    public DrawMode DrawMode { get; set; }
    public int ItemHeight { get; set; } = 32;
    private int _selected=-1;
    public int SelectedIndex
    {
        get=>_selected;
        set {int next=Math.Clamp(value,-1,Items.Count-1);if(next==_selected)return;_selected=next;Text=SelectedItem?.ToString()??"";Invalidate();SelectedIndexChanged?.Invoke(this,EventArgs.Empty);}
    }
    public object? SelectedItem {get=>_selected>=0&&_selected<Items.Count?Items[_selected]:null;set=>SelectedIndex=Items.FindIndex(x=>Equals(x,value));}
    public ChoiceField() {DoubleBuffered=true;Height=36;TabStop=true;Cursor=Cursors.Hand;BackColor=Color.FromArgb(13,24,34);ForeColor=Color.White;SetStyle(ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);}
    private void Open() { if(!Enabled||Items.Count==0)return;DropDown?.Invoke(this,EventArgs.Empty);ChoicePopup.Show(this,new Point(0,Height+4),Items.Select((x,i)=>new ChoicePopup.Option(x.ToString()??"",()=>SelectedIndex=i,i==SelectedIndex)),Width); }
    protected override void OnClick(EventArgs e){base.OnClick(e);Focus();Open();}
    protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode is Keys.Space or Keys.Enter or Keys.Down){Open();e.Handled=true;}}
    protected override bool IsInputKey(Keys keyData)=>keyData==Keys.Down||base.IsInputKey(keyData);
    /// <summary>Box-less style: only a baseline (accent while focused), like the text inputs.</summary>
    public bool Underline { get; set; }
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if(Underline)
        {
            // Campo: una superficie morbida arrotondata, senza sottolineature ne' bordi; un filo d'accento solo con il fuoco.
            g.Clear(Parent?.BackColor??BackColor);
            using var soft=ModalActionButton.Rounded(new Rectangle(0,0,Width-1,Height-1),8);
            using var softFill=new SolidBrush(Theme.SheetRaised);g.FillPath(softFill,soft);
            if(Focused){using var ring=new Pen(Color.FromArgb(150,Theme.Accent));g.DrawPath(ring,soft);}
        }
        else
        {
            using var shape=ModalActionButton.Rounded(new Rectangle(0,0,Width-1,Height-1),7);using var fill=new SolidBrush(BackColor);g.FillPath(fill,shape);
        }
        var label=new Rectangle(12,1,Math.Max(1,Width-44),Height-2);
        if(DrawItem!=null)DrawItem(this,new DrawItemEventArgs(g,Font,label,SelectedIndex,DrawItemState.None,ForeColor,BackColor));
        else TextRenderer.DrawText(g,Text,Font,label,ForeColor,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
        using var pen=new Pen(Color.FromArgb(155,178,196),1.4f);int x=Width-19,y=Height/2;g.DrawLines(pen,new[]{new Point(x-4,y-2),new Point(x,y+2),new Point(x+4,y-2)});
    }
}
