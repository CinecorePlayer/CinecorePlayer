using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD;

internal sealed class ModalActionButton : Button
{
    public ModalActionButton()
    {
        FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; Cursor=Cursors.Hand;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
    }
    /// <summary>Azione di solo testo (un passo, un collegamento): al passaggio del mouse cambia il colore del testo, senza riquadro.
    /// I riquadri di pulsanti di testo vicini si toccavano.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool TextOnly { get; set; }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (TextOnly)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Color.Black);
            bool hot = Enabled && ClientRectangle.Contains(PointToClient(MousePosition));
            Color ink = !Enabled ? Color.FromArgb(90, 105, 115) : hot ? (ForeColor.ToArgb() == Theme.Accent.ToArgb() ? ControlPaint.Light(Theme.Accent, .45f) : Theme.Text) : ForeColor;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }
        var g=e.Graphics;g.Clear(Parent?.BackColor ?? Color.Black);g.SmoothingMode=SmoothingMode.AntiAlias;
        using var shape=Rounded(new Rectangle(0,0,Math.Max(1,Width-1),Math.Max(1,Height-1)),9);
        using var fill=new SolidBrush(Enabled?BackColor:Color.FromArgb(20,26,31));g.FillPath(fill,shape);
        if(ClientRectangle.Contains(PointToClient(MousePosition)) && Enabled) {using var hover=new SolidBrush(Color.FromArgb(15,255,255,255));g.FillPath(hover,shape);}
        if(Focused) {using var focus=new Pen(Color.FromArgb(100,Theme.Accent));g.DrawPath(focus,shape);}
        TextRenderer.DrawText(g,Text,Font,ClientRectangle,Enabled?ForeColor:Color.FromArgb(90,105,115),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
    }
    internal static GraphicsPath Rounded(Rectangle r,int radius)
    {
        int d=Math.Max(1,Math.Min(radius*2,Math.Min(r.Width,r.Height)));var path=new GraphicsPath();
        path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);path.CloseFigure();return path;
    }
}
