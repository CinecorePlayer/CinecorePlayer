#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025.HUD
{
    // Paint the transport in one surface. Transparent child HWNDs repainting the
    // parent during progress updates were corrupting the button images at DPI scale.
    internal sealed class MusicTransportBar : UserControl
    {
        public event Action<string>? Command;
        public event Action<double>? Seek;
        public event Action<float>? Volume;
        private readonly Dictionary<string, Rectangle> _targets = new();
        private readonly ToolTip _tips = new();
        private readonly Dictionary<string, Bitmap> _icons = new();
        private string _title = "", _artist = "", _artworkPath = "", _hover = "", _drag = "", _pressed = "";
        private Image? _cover;
        private Bitmap? _backdrop;
        private Bitmap? _glass;
        private Rectangle _glassBounds;
        private Control? _backdropParent;
        private bool _backdropDirty = true;
        private long _lastBackdropCaptureTick;
        private void BackdropInvalidated(object? sender, InvalidateEventArgs e) { _backdropDirty=true; Invalidate(); }
        private void BackdropResized(object? sender, EventArgs e) { _backdropDirty=true; _lastBackdropCaptureTick=0; Invalidate(); }
        protected override void OnParentChanged(EventArgs e)
        {
            if (_backdropParent != null) { _backdropParent.Invalidated-=BackdropInvalidated; _backdropParent.SizeChanged-=BackdropResized; }
            base.OnParentChanged(e);
            _backdropParent=Parent;
            if (_backdropParent != null) { _backdropParent.Invalidated+=BackdropInvalidated; _backdropParent.SizeChanged+=BackdropResized; }
            _backdropDirty=true;
            _lastBackdropCaptureTick=0;
        }
        private double _duration, _position, _volume = 1, _dragValue;
        private bool _playing, _muted, _bitstream, _shuffle, _repeat, _favorite, _english;
        private int _view, _focusIndex;
        public bool Expanded { get; set; }
        public int PreferredBarHeight => Width < D(760) ? D(174) : Width < D(1180) ? D(116) : D(84);
        internal string TrackTitle => _title;
        internal string TrackArtist => _artist;
        internal string ArtworkPath => _artworkPath;
        private int D(float value) => (int)Math.Round(value * DeviceDpi / 96f);
        private Rectangle R(int x,int y,int w,int h) => new(D(x),D(y),D(w),D(h));
        private Rectangle _art, _titleRect, _artistRect, _elapsed, _end;
        public MusicTransportBar()
        {
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent; TabStop = true; AccessibleName = "Controlli riproduzione musicale";
        }
        public void SetLanguage(bool english) { _english = english; }
        public void SetFavorite(bool favorite) { if (_favorite != favorite) { _favorite = favorite; Invalidate(); } }
        public void SetTrack(string title,string artist) { if (_title == title && _artist == artist) return; _title = title; _artist = artist; Invalidate(); }
        public void SetArtwork(string? path)
        {
            if (_artworkPath == (path ?? "")) return; _artworkPath = path ?? "";
            Image? next = null;
            try { if (File.Exists(path)) { using var image = Image.FromFile(path!); next = new Bitmap(image); } } catch { }
            var old = _cover; _cover = next; old?.Dispose(); _backdropDirty=true; _lastBackdropCaptureTick=0; Invalidate();
        }
        public void UpdatePlayback(double position,double duration,bool playing,float volume,bool muted,bool bitstream,bool shuffle,bool repeat,int view)
        {
            bool bitstreamChanged = _bitstream != bitstream;
            _position = double.IsFinite(position) ? Math.Max(0,position) : 0;
            _duration = double.IsFinite(duration) ? Math.Max(0,duration) : 0;
            _volume = Math.Clamp(volume,0,1); _playing=playing; _muted=muted; _bitstream=bitstream; _shuffle=shuffle; _repeat=repeat; _view=view;
            if (bitstreamChanged) { _hover = ""; _drag = ""; Capture = false; Cursor = Cursors.Default; }
            Invalidate();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (_targets == null) return;
            _targets.Clear(); int w=(int)(Width*96f/DeviceDpi),h=(int)(Height*96f/DeviceDpi);
            bool compact=w<760, narrow=w<1180;
            _art=R(16,compact?9:(h-46)/2,46,46);
            int songW=compact?w-16:Math.Clamp(w/4,200,350);
            _titleRect=R(76,compact?8:h/2-22,Math.Max(40,songW-150),22);
            _artistRect=R(76,compact?31:h/2+1,Math.Max(40,songW-150),22);
            _targets["favorite"]=R(songW-60,compact?16:h/2-15,28,30);
            _targets["more"]=R(songW-28,compact?16:h/2-15,28,30);
            int transportX=compact?(w-186)/2:narrow?songW+14:w/2-93;
            int transportY=compact?59:15;
            int i=0;foreach(string key in new[]{"shuffle","previous","play","next","repeat"}) _targets[key]=R(transportX+i++*38,transportY,32,32);
            int actionsX=narrow?Math.Max(compact?12:songW+14,(w-314)/2):w-328;
            int actionsY=compact?97:narrow?52:27;
            i=0;foreach(string key in new[]{"queue","analysis","lyrics","library","pip","stop","mute"})_targets[key]=R(actionsX+i++*30,actionsY,26,28);
            _targets["volume"]=R(actionsX+216,actionsY+2,82,24);
            int seekLeft=narrow?58:Math.Max(songW+52,w/2-300);
            int seekRight=narrow?w-58:Math.Min(w-340,w/2+300);
            int seekY=h-17;
            if(compact) { seekY=h-17; seekLeft=58; seekRight=w-58; }
            _targets["seek"]=R(seekLeft,seekY-10,Math.Max(20,seekRight-seekLeft),20);
            _elapsed=R(seekLeft-49,seekY-11,43,22);_end=R(seekRight+6,seekY-11,44,22);
        }
        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            PerformLayout();
            _backdropDirty = true;
            _lastBackdropCaptureTick = 0;
            Parent?.PerformLayout();
            Invalidate();
        }
        private static string Time(double value) => TimeSpan.FromSeconds(Math.Max(0,value)).ToString(value>=3600?@"h\:mm\:ss":@"m\:ss");
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var background = new SolidBrush(Parent?.BackColor ?? Color.Black)) e.Graphics.FillRectangle(background,ClientRectangle);
            if (Parent != null)
            {
                var size=Parent.ClientSize;
                if(size.Width>0 && size.Height>0)
                {
                    if(_backdrop == null || _backdrop.Size != size)
                    { _backdrop?.Dispose(); _backdrop=new Bitmap(size.Width,size.Height); _glass?.Dispose();_glass=null; _backdropDirty=true; _lastBackdropCaptureTick=0; }
                    if(_backdropDirty && (_glass==null || Environment.TickCount64-_lastBackdropCaptureTick>=350))
                    {
                        _backdropDirty=false;
                        _lastBackdropCaptureTick=Environment.TickCount64;
                        // GDI TextRenderer ignores GDI+ translation. Paint once at the
                        // original coordinates, then crop the cached surface to this bar.
                        using var graphics=Graphics.FromImage(_backdrop);
                        using var behind=new PaintEventArgs(graphics,Parent.ClientRectangle);
                        InvokePaintBackground(Parent,behind); InvokePaint(Parent,behind);
                        graphics.Flush();
                        _glass?.Dispose(); _glass=GlassSurface.Create(_backdrop,Bounds,_cover); _glassBounds=Bounds;
                    }
                    if (_glass == null || _glassBounds != Bounds)
                    { _glass?.Dispose(); _glass=GlassSurface.Create(_backdrop,Bounds,_cover); _glassBounds=Bounds; }
                    e.Graphics.DrawImageUnscaled(_glass,Point.Empty);
                }
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            if(_cover!=null)g.DrawImage(_cover,_art);
            using var title=new Font("Segoe UI Semibold",10f);using var normal=new Font("Segoe UI",9f);
            const TextFormatFlags flags=TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g,_title,title,_titleRect,Theme.Text,flags);TextRenderer.DrawText(g,_artist,normal,_artistRect,Theme.Muted,flags);
            TextRenderer.DrawText(g,Time(_position),normal,_elapsed,Theme.Muted,flags);TextRenderer.DrawText(g,Time(_duration),normal,_end,Theme.Muted,flags);
            foreach(var entry in _targets)
            {
                string key=entry.Key;var bounds=entry.Value;
                if(key is "seek" or "volume")
                {
                    double value=_drag==key?_dragValue:key=="seek"?(_duration>0?_position/_duration:0):_muted?0:_volume;
                    int y=bounds.Top+bounds.Height/2,x=bounds.Left+(int)(bounds.Width*Math.Clamp(value,0,1));
                    bool disabled=_bitstream;
                    if (disabled && key=="volume")
                    {
                        using var rail=new Pen(Color.FromArgb(48,153,181,205),D(2));
                        g.DrawLine(rail,bounds.Left,y,bounds.Right,y);
                        continue;
                    }
                    using var bg=new Pen(Color.FromArgb(disabled?48:90,153,181,205),D(2));using var fg=new Pen(Color.FromArgb(disabled?85:235,Theme.Accent),D(2));
                    g.DrawLine(bg,bounds.Left,y,bounds.Right,y);g.DrawLine(fg,bounds.Left,y,x,y);
                    int radius = key=="volume" || _hover==key || _drag==key ? D(5) : D(4);
                    using (var knob=new SolidBrush(disabled?Color.FromArgb(105,Color.White):Color.White)) g.FillEllipse(knob,x-radius,y-radius,radius*2,radius*2);
                    continue;
                }
                string icon=key switch {"play"=>_playing?"pause":"play","mute"=>_muted?"mute":"volume","analysis"=>"wave","lyrics"=>"lyrics","repeat"=>"repeat","favorite"=>"star","stop"=>"stop",_=>key};
                bool selected=key switch {"shuffle"=>_shuffle,"repeat"=>_repeat,"favorite"=>_favorite,"analysis"=>_view==1,"lyrics"=>_view==2,"library"=>_view==0,_=>false};
                Color color=key=="mute"&&_bitstream?Color.FromArgb(70,70,70):selected?Theme.Accent:Color.FromArgb(222,226,230);
                if(_hover==key && !(key=="mute"&&_bitstream)) color=Theme.Accent;
                var glyph=new Rectangle(bounds.X+(bounds.Width-D(20))/2,bounds.Y+(bounds.Height-D(20))/2,D(20),D(20));
                string cacheKey=icon+"|"+glyph.Width+"|"+color.ToArgb();
                if(!_icons.TryGetValue(cacheKey,out var bitmap))
                {
                    bitmap=new Bitmap(glyph.Width,glyph.Height);using(var canvas=Graphics.FromImage(bitmap))DrawSymbol(canvas,new Rectangle(Point.Empty,bitmap.Size),icon,color);
                    _icons[cacheKey]=bitmap;
                }
                g.DrawImageUnscaled(bitmap,glyph.Location);
            }
            if(Focused&&_targets.Count>0)
            {
                var focused=_targets.ElementAt(Math.Clamp(_focusIndex,0,_targets.Count-1));
                if(!(_bitstream && focused.Key is "volume" or "mute"))ControlPaint.DrawFocusRectangle(g,focused.Value);
            }
        }
        private string Hit(Point point)=>_targets.FirstOrDefault(x=>x.Value.Contains(point)).Key??"";
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if(_drag!="") { _dragValue=ValueAt(e.X,_targets[_drag]);Invalidate();return; }
            string key=Hit(e.Location);if(key==_hover)return;_hover=key;Cursor=key==""||(_bitstream&&(key is "seek" or "volume" or "mute"))?Cursors.Default:Cursors.Hand;
            _tips.SetToolTip(this,key switch {"analysis"=>"Grafici audio","lyrics"=>"Testo","queue"=>"Coda","more"=>"Altre opzioni","favorite"=>"Preferito","pip"=>"Mini player","stop"=>"Interrompi riproduzione","seek" when _bitstream=>"Ricerca non disponibile in bitstream","seek"=>"Posizione","volume" when _bitstream=>"Volume non disponibile in bitstream","volume"=>"Volume",_=>key});Invalidate();
        }
        private static double ValueAt(int x,Rectangle bounds)=>Math.Clamp((x-bounds.Left)/(double)Math.Max(1,bounds.Width),0,1);
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;Focus();string key=Hit(e.Location);_pressed=key;
            if(key is "seek" or "volume") {if(key=="volume"&&_bitstream||key=="seek"&&_duration<=0)return;_drag=key;_dragValue=ValueAt(e.X,_targets[key]);Capture=true;Invalidate();}
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);if(e.Button==MouseButtons.Right){Command?.Invoke("more");return;}if(e.Button!=MouseButtons.Left)return;
            if(_drag!="") {string key=_drag;double value=ValueAt(e.X,_targets[key]);_drag="";Capture=false;if(key=="seek")Seek?.Invoke(value*_duration);else Volume?.Invoke((float)value);Invalidate();}
            else {string key=Hit(e.Location);if(key==_pressed&&key!=""&&key is not "volume" and not "seek"&&!(key=="mute"&&_bitstream))Command?.Invoke(key);}
        }
        protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){_drag="";Invalidate();}}
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);_hover="";Invalidate();}
        protected override bool IsInputKey(Keys keyData)=>keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down||base.IsInputKey(keyData);
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if((keyData&Keys.KeyCode)==Keys.Tab){int next=_focusIndex+((keyData&Keys.Shift)!=0?-1:1);if(next>=0&&next<_targets.Count){_focusIndex=next;Invalidate();return true;}}
            return base.ProcessDialogKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);if(_targets.Count==0)return;string key=_targets.ElementAt(Math.Clamp(_focusIndex,0,_targets.Count-1)).Key;
            if(e.KeyCode is Keys.Enter or Keys.Space){if(key is not "volume" and not "seek" && !(key=="mute"&&_bitstream))Command?.Invoke(key);e.Handled=true;}
            else if(e.KeyCode is Keys.Left or Keys.Right){int direction=e.KeyCode==Keys.Left?-1:1;if(key=="seek"&&!_bitstream)Seek?.Invoke(Math.Clamp(_position+direction*5,0,_duration));else if(key=="volume"&&!_bitstream)Volume?.Invoke((float)Math.Clamp(_volume+direction*.05,0,1));e.Handled=true;}
        }
        protected override void Dispose(bool disposing){if(disposing){if(_backdropParent!=null){_backdropParent.Invalidated-=BackdropInvalidated;_backdropParent.SizeChanged-=BackdropResized;}_glass?.Dispose();_backdrop?.Dispose();_cover?.Dispose();_tips.Dispose();foreach(var image in _icons.Values)image.Dispose();}base.Dispose(disposing);}
        internal static void DrawSymbol(Graphics g, Rectangle bounds, string icon, Color color)
        {
            if (AssetIconService.DrawCustom(g, bounds, icon, color)) return;
            var state = g.Save(); g.TranslateTransform(bounds.X, bounds.Y); g.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var fill = new SolidBrush(color);
            void Line(float x, float y, float x2, float y2) => g.DrawLine(pen, x, y, x2, y2);
            switch (icon)
            {
                case "play": g.FillPolygon(fill, new[] { new PointF(7,3),new PointF(20,12),new PointF(7,21) }); break;
                case "pause": g.FillRectangle(fill,7,4,3.5f,16);g.FillRectangle(fill,14,4,3.5f,16);break;
                case "previous": g.FillPolygon(fill,new[]{new PointF(18,5),new PointF(7,12),new PointF(18,19)});g.FillRectangle(fill,5,5,2,14);break;
                case "next":g.FillPolygon(fill,new[]{new PointF(6,5),new PointF(17,12),new PointF(6,19)});g.FillRectangle(fill,17,5,2,14);break;
                case "heart":using(var path=new GraphicsPath()){path.AddBezier(12,20,10,18,3,13,3,8);path.AddBezier(3,8,3,2,9,2,12,7);path.AddBezier(12,7,15,2,21,2,21,8);path.AddBezier(21,8,21,13,14,18,12,20);g.DrawPath(pen,path);}break;
                case "more":for(int x=5;x<=19;x+=7)g.FillEllipse(fill,x-1,11,2,2);break;
                case "shuffle":g.DrawLines(pen,new[]{new PointF(3,6),new PointF(7,6),new PointF(17,18),new PointF(21,18)});g.DrawLines(pen,new[]{new PointF(3,18),new PointF(7,18),new PointF(17,6),new PointF(21,6)});Line(18,3,21,6);Line(18,9,21,6);Line(18,15,21,18);Line(18,21,21,18);break;
                case "repeat-one":g.DrawArc(pen,3,5,18,14,180,160);g.DrawArc(pen,3,5,18,14,0,160);Line(3,5,3,11);Line(3,11,8,11);Line(21,13,21,19);Line(16,13,21,13);break;
                case "wave":Line(3,10,3,14);Line(7,6,7,18);Line(12,2,12,22);Line(17,7,17,17);Line(21,10,21,14);break;
                case "list":g.DrawRectangle(pen,3,4,18,14);Line(6,21,10,18);Line(7,8,17,8);Line(7,12,15,12);break;
                case "playlist-add":g.DrawRectangle(pen,3,3,14,17);Line(7,7,13,7);Line(7,11,13,11);Line(18,14,18,22);Line(14,18,22,18);break;
                case "queue":Line(4,5,20,5);Line(4,10,20,10);Line(4,15,20,15);Line(4,20,14,20);break;
                case "queue-add":Line(3,5,18,5);Line(3,10,18,10);Line(3,15,11,15);Line(16,17,22,17);Line(19,14,19,20);break;
                case "library":Line(4,4,4,20);Line(9,4,9,20);Line(14,4,14,20);Line(18,4,21,20);break;
                case "pip":g.DrawRectangle(pen,3,5,18,14);g.FillRectangle(fill,12,11,7,6);break;
                case "close":Line(6,6,18,18);Line(18,6,6,18);break;
                case "volume":case "mute":g.DrawPolygon(pen,new[]{new PointF(3,9),new PointF(7,9),new PointF(12,5),new PointF(12,19),new PointF(7,15),new PointF(3,15)});if(icon=="volume"){g.DrawArc(pen,11,5,10,14,-65,130);}else{Line(16,9,22,15);Line(22,9,16,15);}break;
            }
            g.Restore(state);
        }

    }
}

