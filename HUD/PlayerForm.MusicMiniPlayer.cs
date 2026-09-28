#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CinecorePlayer2025.HUD;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private sealed partial class PipMiniPlayerForm
        {
            private Action? _showMusicLyrics;
            private MusicMiniSurface? _musicMini;
            private bool _musicMiniActive;
            private void UpdateMusicMini(PipMiniPlayerState state)
            {
                bool music = !state.ShowVideo;
                if (music && !_musicMiniActive)
                {
                    _musicMiniActive = true;
                    MinimumSize = new Size(220, 220); Size = new Size(244, 252);
                    _surface.Padding = Padding.Empty; _root.Visible = false;
                    _volumePopup.Visible = false; _resizeGrip.Visible = false;
                    _musicMini ??= new MusicMiniSurface(this);
                    if (_musicMini.Parent == null) _surface.Controls.Add(_musicMini);
                    _musicMini.Dock = DockStyle.Fill; _musicMini.Visible = true; _musicMini.BringToFront();
                }
                else if (!music && _musicMiniActive)
                {
                    _musicMiniActive = false;
                    if (_musicMini != null) _musicMini.Visible = false;
                    _surface.Padding = new Padding(7); _root.Visible = true;
                }
                if (music)
                {
                    RefreshArtwork(state);
                    _musicMini!.State = state;
                    _musicMini.Invalidate();
                }
            }

            private sealed class MusicMiniSurface : Control
            {
                private readonly PipMiniPlayerForm _owner;
                internal PipMiniPlayerState State = new();
                private Rectangle _close, _lyrics, _previous, _play, _next, _seek;
                private bool _drag, _scrub;
                private Point _start, _origin;
                private double _scrubValue;
                internal MusicMiniSurface(PipMiniPlayerForm owner)
                {
                    _owner = owner; DoubleBuffered = true; BackColor = Color.Black; TabStop = true;
                    AccessibleName = "Mini player musicale";
                    var tip = owner._toolTip;
                    tip.SetToolTip(this, "Mini player · trascina per spostare · Esc per tornare alla libreria");
                }
                protected override void OnPaint(PaintEventArgs e)
                {
                    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.Clear(Color.Black);
                    if (_owner._artImage is Image image)
                    {
                        float ratio = Math.Max(Width / (float)image.Width, Height / (float)image.Height);
                        float w = image.Width * ratio, h = image.Height * ratio;
                        g.DrawImage(image, (Width-w)/2, (Height-h)/2,w,h);
                        using var shade = new LinearGradientBrush(ClientRectangle, Color.Transparent, Color.Black, LinearGradientMode.Vertical);
                        shade.InterpolationColors = new ColorBlend { Positions = new[] {0f,.26f,.52f,.8f,1f}, Colors = new[] {Color.FromArgb(40,0,0,0),Color.FromArgb(95,0,0,0),Color.FromArgb(220,0,0,0),Color.FromArgb(190,0,0,0),Color.FromArgb(170,0,0,0)} };
                        g.FillRectangle(shade,ClientRectangle);
                    }
                    int size = 32;
                    _close = new Rectangle(12,12,size,size); _lyrics = new Rectangle(Width-44,12,size,size);
                    foreach (var item in new[] {(_close,"close"),(_lyrics,"list")})
                    {
                        using var fill = new SolidBrush(Color.FromArgb(200,22,22,22)); g.FillEllipse(fill,item.Item1);
                        MusicTransportBar.DrawSymbol(g,Rectangle.Inflate(item.Item1,-9,-9),item.Item2,Color.White);
                    }
                    using var title = new Font("Segoe UI Semibold",11.5f);
                    using var artist = new Font("Segoe UI",10f);
                    const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                    TextRenderer.DrawText(g,State.Title,title,new Rectangle(16,(int)(Height*.31),Width-32,28),Color.White,flags);
                    TextRenderer.DrawText(g,State.Subtitle,artist,new Rectangle(18,(int)(Height*.43),Width-36,25),Color.FromArgb(163,163,163),flags);
                    _seek = new Rectangle(24,(int)(Height*.59),Width-48,20);
                    double progress = _scrub ? _scrubValue : State.DurationSeconds > 0 ? Math.Clamp(State.PositionSeconds/State.DurationSeconds,0,1) : 0;
                    using var track = new Pen(Color.FromArgb(58,58,58),4) { StartCap=LineCap.Round, EndCap=LineCap.Round };
                    using var played = new Pen(Color.FromArgb(205,205,205),4) { StartCap=LineCap.Round, EndCap=LineCap.Round };
                    g.DrawLine(track,_seek.Left,_seek.Top+10,_seek.Right,_seek.Top+10);
                    if(progress>0)g.DrawLine(played,_seek.Left,_seek.Top+10,_seek.Left+(float)(_seek.Width*progress),_seek.Top+10);
                    using var timeFont = new Font("Segoe UI", 8f);
                    string Time(double value) => TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(value) ? value : 0)).ToString(@"m\:ss");
                    TextRenderer.DrawText(g, Time(State.PositionSeconds), timeFont, new Rectangle(24,_seek.Bottom-1,70,18), Color.Silver, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, Time(State.DurationSeconds), timeFont, new Rectangle(Width-94,_seek.Bottom-1,70,18), Color.Silver, TextFormatFlags.NoPadding | TextFormatFlags.Right);
                    int center=Width/2, y=(int)(Height*.77);
                    _previous=new Rectangle(center-76,y,36,40);_play=new Rectangle(center-21,y-4,42,48);_next=new Rectangle(center+40,y,36,40);
                    MusicTransportBar.DrawSymbol(g,Rectangle.Inflate(_previous,-9,-10),"previous",Color.White);
                    MusicTransportBar.DrawSymbol(g,Rectangle.Inflate(_play,-7,-8),State.IsPaused?"play":"pause",Color.White);
                    MusicTransportBar.DrawSymbol(g,Rectangle.Inflate(_next,-9,-10),"next",Color.White);
                    using var outline = new Pen(Color.FromArgb(55,65,62)); g.DrawRectangle(outline,0,0,Width-1,Height-1);
                }
                private bool Interactive(Point p) => _close.Contains(p)||_lyrics.Contains(p)||_previous.Contains(p)||_play.Contains(p)||_next.Contains(p)||_seek.Contains(p);
                protected override void OnMouseDown(MouseEventArgs e)
                {
                    base.OnMouseDown(e); if(e.Button!=MouseButtons.Left)return; Focus();
                    if(_seek.Contains(e.Location)&&State.DurationSeconds>0){_scrub=true;Capture=true;_scrubValue=Math.Clamp((e.X-_seek.Left)/(double)_seek.Width,0,1);Invalidate();}
                    else if(!Interactive(e.Location)){_drag=true;Capture=true;_start=MousePosition;_origin=_owner.Location;}
                }
                protected override void OnMouseMove(MouseEventArgs e)
                {
                    base.OnMouseMove(e);
                    Cursor=Interactive(e.Location)?Cursors.Hand:Cursors.Default;
                    if(_drag) _owner.Location=new Point(_origin.X+MousePosition.X-_start.X,_origin.Y+MousePosition.Y-_start.Y);
                    if(_scrub){_scrubValue=Math.Clamp((e.X-_seek.Left)/(double)Math.Max(1,_seek.Width),0,1);Invalidate();}
                }
                protected override void OnMouseUp(MouseEventArgs e)
                {
                    base.OnMouseUp(e);if(e.Button!=MouseButtons.Left)return;
                    bool dragged=_drag;_drag=false;
                    if(_scrub){_scrub=false;Capture=false;_owner._seekTo(_scrubValue*State.DurationSeconds);return;}
                    Capture=false;if(dragged)return;
                    if(_close.Contains(e.Location))_owner._restoreStandard();
                    else if(_lyrics.Contains(e.Location))_owner._showMusicLyrics?.Invoke();
                    else if(_play.Contains(e.Location))_owner._togglePlayPause();
                    else if(_previous.Contains(e.Location))_owner._previous();
                    else if(_next.Contains(e.Location))_owner._next();
                    _owner.RefreshState();
                }
                protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if(!Capture){_drag=false;_scrub=false;} }
                protected override void OnKeyDown(KeyEventArgs e)
                {
                    base.OnKeyDown(e);
                    if(e.KeyCode==Keys.Space){_owner._togglePlayPause();e.Handled=true;}
                    if(e.KeyCode==Keys.Escape){_owner._restoreStandard();e.Handled=true;}
                }
            }
        }
    }
}
