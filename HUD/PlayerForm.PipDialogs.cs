#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm : Form
    {
        private sealed class PipMiniPlayerState
        {
            public string Title { get; set; } = string.Empty;
            public string Subtitle { get; set; } = string.Empty;
            public string? MediaPath { get; set; }
            public string? ArtworkPath { get; set; }
            public bool IsPaused { get; set; }
            public bool HasPlayback { get; set; }
            public bool CanPrevious { get; set; }
            public bool CanNext { get; set; }
            public bool ShowVideo { get; set; }
            public double PositionSeconds { get; set; }
            public double DurationSeconds { get; set; }
            public int VideoWidth { get; set; }
            public int VideoHeight { get; set; }
            public float Volume { get; set; } = 1f;
            public bool VolumeEditable { get; set; } = true;
            public bool Muted { get; set; }
            public bool ShuffleActive { get; set; }
            public bool LoopActive { get; set; }
        }

        private enum PipButtonKind
        {
            Mute,
            Previous,
            Back10,
            PlayPause,
            Stop,
            Forward10,
            Next,
            Shuffle,
            Repeat,
            Restore,
            Close
        }

        private sealed class PipVideoSurfacePanel : Panel
        {
            public Action<Message>? ForwardOwnerMessage { get; set; }

            public PipVideoSurfacePanel()
            {
                BackColor = Color.Black;
                Dock = DockStyle.None;
                Margin = new Padding(0);
                Padding = new Padding(0);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, false);
                SetStyle(ControlStyles.ResizeRedraw, true);
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                try { ForwardOwnerMessage?.Invoke(m); } catch { }
            }
        }

        private sealed class PipCoverPictureBox : PictureBox
        {
            public PipCoverPictureBox()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            protected override void OnPaint(PaintEventArgs pe)
            {
                pe.Graphics.Clear(BackColor);
                Image? image = Image;
                if (image == null || ClientSize.Width < 2 || ClientSize.Height < 2)
                    return;

                pe.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                pe.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float scale = Math.Max(ClientSize.Width / (float)Math.Max(1, image.Width),
                                       ClientSize.Height / (float)Math.Max(1, image.Height));
                int width = Math.Max(1, (int)Math.Ceiling(image.Width * scale));
                int height = Math.Max(1, (int)Math.Ceiling(image.Height * scale));
                int x = (ClientSize.Width - width) / 2;
                int y = (ClientSize.Height - height) / 2;
                pe.Graphics.DrawImage(image, new Rectangle(x, y, width, height));
            }
        }

        private sealed class PipIconButton : Button
        {
            public PipButtonKind Kind { get; }
            public bool Primary { get; }
            public bool IsPaused { get; set; }
            public bool IsMuted { get; set; }
            public bool IsActive { get; set; }
            private Bitmap? _iconBitmap;
            private string _iconCacheKey = string.Empty;

            public PipIconButton(PipButtonKind kind, bool primary)
            {
                Kind = kind;
                Primary = primary;
                Text = string.Empty;
                FlatStyle = FlatStyle.Flat;
                UseVisualStyleBackColor = false;
                Cursor = Cursors.Hand;
                TabStop = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                FlatAppearance.BorderSize = 0;
                MouseEnter += (_, __) => Invalidate();
                MouseLeave += (_, __) => Invalidate();
                GotFocus += (_, __) => Invalidate();
                LostFocus += (_, __) => Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = ClientRectangle;
                r.Width -= 1;
                r.Height -= 1;

                bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
                bool disabled = !Enabled;

                e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(8, 12, 17));
                bool pressed = Capture && (Control.MouseButtons & MouseButtons.Left) != 0;
                if (Primary)
                {
                    Color plate = disabled
                        ? Color.FromArgb(23, 30, 38)
                        : pressed ? Color.FromArgb(17, 93, 184)
                        : hot ? Color.FromArgb(29, 121, 218)
                        : Color.FromArgb(19, 105, 198);
                    using var plateBrush = new SolidBrush(plate);
                    var plateRect = r;
                    plateRect.Inflate(-3, -5);
                    using var platePath = RoundedRect(plateRect, 5);
                    e.Graphics.FillPath(plateBrush, platePath);
                }
                else if (IsActive && !disabled)
                {
                    using var active = new SolidBrush(Color.FromArgb(34, Theme.Accent));
                    var activeRect = r;
                    activeRect.Inflate(-4, -6);
                    using var activePath = RoundedRect(activeRect, 4);
                    e.Graphics.FillPath(active, activePath);
                    using var underline = new SolidBrush(Theme.Accent);
                    e.Graphics.FillRectangle(underline, activeRect.Left + 5, activeRect.Bottom - 2,
                        Math.Max(3, activeRect.Width - 10), 2);
                }
                else if (hot && !disabled)
                {
                    using var hover = new SolidBrush(pressed
                        ? Color.FromArgb(42, 255, 255, 255)
                        : Color.FromArgb(22, 255, 255, 255));
                    var hoverRect = r;
                    hoverRect.Inflate(-5, -6);
                    using var hoverPath = RoundedRect(hoverRect, 4);
                    e.Graphics.FillPath(hover, hoverPath);
                }

                Color icon = Primary || IsActive ? Color.White : Color.FromArgb(214, 223, 232);
                if (hot && !disabled && !Primary)
                    icon = Color.White;
                if (disabled)
                    icon = Color.FromArgb(88, 94, 108);

                DrawAssetIcon(e.Graphics, r, icon);
            }

            private void DrawAssetIcon(Graphics g, Rectangle r, Color tint)
            {
                string key = Kind switch
                {
                    PipButtonKind.Mute => IsMuted ? "mute" : "volume",
                    PipButtonKind.Previous => "previous",
                    PipButtonKind.Back10 => "skip-back",
                    PipButtonKind.PlayPause => IsPaused ? "play" : "pause",
                    PipButtonKind.Forward10 => "skip-forward",
                    PipButtonKind.Next => "next",
                    PipButtonKind.Stop => "stop",
                    PipButtonKind.Shuffle => "shuffle",
                    PipButtonKind.Repeat => "repeat-one",
                    PipButtonKind.Restore => "maximize",
                    PipButtonKind.Close => "close",
                    _ => "play"
                };
                int iconSize = Primary ? 18 : Kind == PipButtonKind.Stop ? 14 : Kind == PipButtonKind.Close ? 13 : 15;
                string cacheKey = $"{key}|{iconSize}|{tint.ToArgb()}";
                if (!string.Equals(_iconCacheKey, cacheKey, StringComparison.Ordinal))
                {
                    try { _iconBitmap?.Dispose(); } catch { }
                    _iconBitmap = null;
                    _iconCacheKey = cacheKey;
                    try
                    {
                        string? path = AssetIconService.Resolve(key);
                        if (!string.IsNullOrWhiteSpace(path))
                            _iconBitmap = AssetIconService.RenderSvg(path, iconSize, tint);
                    }
                    catch { }
                }
                if (_iconBitmap == null)
                    return;
                int x = r.Left + (r.Width - _iconBitmap.Width) / 2;
                int y = r.Top + (r.Height - _iconBitmap.Height) / 2;
                g.DrawImageUnscaled(_iconBitmap, x, y);
            }

            private static GraphicsPath RoundedRect(Rectangle r, int radius)
            {
                int d = Math.Max(2, radius * 2);
                var path = new GraphicsPath();
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _iconBitmap?.Dispose(); } catch { }
                    _iconBitmap = null;
                }
                base.Dispose(disposing);
            }

        }

        private sealed class PipTimelineBar : Control
        {
            private double _position;
            private double _duration;
            private bool _dragging;
            public Action<double>? SeekRequested { get; set; }

            public PipTimelineBar()
            {
                Height = 58;
                Dock = DockStyle.Fill;
                Margin = new Padding(0);
                Cursor = Cursors.Hand;
                BackColor = Color.FromArgb(8, 12, 17);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            public void SetProgress(double positionSeconds, double durationSeconds)
            {
                double pos = Math.Max(0, positionSeconds);
                double dur = Math.Max(0, durationSeconds);
                if (dur > 0)
                    pos = Math.Min(pos, dur);

                if (Math.Abs(_position - pos) < 0.05 && Math.Abs(_duration - dur) < 0.05)
                    return;

                _position = pos;
                _duration = dur;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                var r = ClientRectangle;
                if (r.Width <= 8 || r.Height <= 8)
                    return;

                string current = FormatPipTime(_position);
                string total = FormatPipTime(_duration);
                using var timeFont = SafeRegular(9.2f);
                TextRenderer.DrawText(g, current, timeFont, new Rectangle(r.Left, r.Top + 2, r.Width / 2, 20),
                    Color.FromArgb(215, 222, 230), TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, total, timeFont, new Rectangle(r.Left + r.Width / 2, r.Top + 2, r.Width / 2, 20),
                    Color.FromArgb(215, 222, 230), TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);

                const int knobRadius = 5;
                int trackH = 3;
                int y = r.Bottom - 12;
                var track = new Rectangle(r.Left + knobRadius, y,
                    Math.Max(1, r.Width - knobRadius * 2 - 1), trackH);
                double ratio = _duration > 0 ? Math.Max(0, Math.Min(1, _position / _duration)) : 0;
                int fillW = Math.Max(0, (int)Math.Round(track.Width * ratio));

                using var bg = new SolidBrush(Color.FromArgb(54, 139, 153, 168));
                using var fg = new SolidBrush(Theme.Accent);
                using var knob = new SolidBrush(Color.White);
                FillRound(g, bg, track, 2);
                if (fillW > 0)
                    FillRound(g, fg, new Rectangle(track.Left, track.Top, fillW, track.Height), 2);

                int kx = track.Left + Math.Min(track.Width, fillW);
                using var knobOutline = new Pen(Color.FromArgb(168, Theme.Accent), 1.5f);
                g.FillEllipse(knob, kx - knobRadius, y - knobRadius + 1, knobRadius * 2, knobRadius * 2);
                g.DrawEllipse(knobOutline, kx - knobRadius, y - knobRadius + 1, knobRadius * 2, knobRadius * 2);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || _duration <= 0)
                    return;
                _dragging = true;
                Capture = true;
                SeekFromX(e.X);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_dragging)
                    SeekFromX(e.X);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left)
                    return;
                if (_dragging)
                    SeekFromX(e.X);
                _dragging = false;
                Capture = false;
            }

            private void SeekFromX(int x)
            {
                if (_duration <= 0)
                    return;

                const int left = 5;
                int width = Math.Max(1, ClientSize.Width - left * 2 - 1);
                double ratio = Math.Max(0, Math.Min(1, (x - left) / (double)width));
                double target = ratio * _duration;
                _position = target;
                Invalidate();
                try { SeekRequested?.Invoke(target); } catch { }
            }

            private static string FormatPipTime(double seconds)
            {
                if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                    seconds = 0;
                var span = TimeSpan.FromSeconds(seconds);
                return span.TotalHours >= 1
                    ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                    : $"{span.Minutes:00}:{span.Seconds:00}";
            }

            private static void FillRound(Graphics g, Brush b, Rectangle r, int radius)
            {
                using var gp = RoundedRect(r, radius);
                g.FillPath(b, gp);
            }

            private static GraphicsPath RoundedRect(Rectangle r, int radius)
            {
                int d = Math.Max(1, radius * 2);
                var path = new GraphicsPath();
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        private sealed class PipVolumeBar : Control
        {
            private float _value = 1f;
            private bool _dragging;
            public Action<float>? ValueChanged { get; set; }
            public Action? InteractionFinished { get; set; }

            public PipVolumeBar()
            {
                Height = 30;
                Width = 126;
                Cursor = Cursors.Hand;
                BackColor = Color.FromArgb(8, 12, 17);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            public void SetValue(float value)
            {
                value = Math.Clamp(value, 0f, 1f);
                if (Math.Abs(_value - value) < .002f)
                    return;
                _value = value;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int left = 7;
                int right = Math.Max(left + 1, ClientSize.Width - 7);
                int y = ClientSize.Height / 2;
                int progress = (int)Math.Round((right - left) * _value);
                using var rail = new Pen(Color.FromArgb(61, 146, 160, 174), 3f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var fill = new Pen(Theme.Accent, 3f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var knob = new SolidBrush(Color.FromArgb(244, 248, 252));
                e.Graphics.DrawLine(rail, left, y, right, y);
                if (progress > 0)
                    e.Graphics.DrawLine(fill, left, y, left + progress, y);
                e.Graphics.FillEllipse(knob, left + progress - 5, y - 5, 10, 10);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                _dragging = true;
                Capture = true;
                UpdateFromX(e.X);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_dragging) UpdateFromX(e.X);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left) return;
                if (_dragging) UpdateFromX(e.X);
                _dragging = false;
                Capture = false;
                try { InteractionFinished?.Invoke(); } catch { }
            }

            private void UpdateFromX(int x)
            {
                const int left = 7;
                int width = Math.Max(1, ClientSize.Width - 14);
                _value = Math.Clamp((x - left) / (float)width, 0f, 1f);
                Invalidate();
                try { ValueChanged?.Invoke(_value); } catch { }
            }
        }

        private sealed partial class PipMiniPlayerForm : Form
        {
            private readonly Func<PipMiniPlayerState> _stateProvider;
            private readonly Action _togglePlayPause;
            private readonly Action _previous;
            private readonly Action _back10;
            private readonly Action _next;
            private readonly Action _forward10;
            private readonly Action _stopPlayback;
            private readonly Action _toggleShuffle;
            private readonly Action _toggleLoop;
            private readonly Action _restoreStandard;
            private readonly Action<double> _seekTo;
            private readonly Action _toggleMute;
            private readonly Action<float> _setVolume;
            private readonly Action<nint, Rectangle> _syncVideoSurface;
            private readonly Action<Message> _forwardVideoSurfaceMessage;
            private readonly Action _restoreVideoSurface;
            private readonly Panel _surface;
            private readonly Panel _resizeGrip;
            private readonly TableLayoutPanel _root;
            private readonly TableLayoutPanel _left;
            private readonly Panel _mediaHost;
            private readonly PipVideoSurfacePanel _videoSurface;
            private readonly PictureBox _art;
            private readonly TableLayoutPanel _right;
            private readonly Panel _textPanel;
            private readonly FlowLayoutPanel _controls;
            private readonly PipTimelineBar _timeline;
            private readonly Label _title;
            private readonly Label _subtitle;
            private readonly PipIconButton _btnPrev;
            private readonly PipIconButton _btnBack10;
            private readonly PipIconButton _btnMute;
            private readonly PipIconButton _btnPlay;
            private readonly PipIconButton _btnForward10;
            private readonly PipIconButton _btnNext;
            private readonly PipIconButton _btnStop;
            private readonly PipIconButton _btnShuffle;
            private readonly PipIconButton _btnRepeat;
            private readonly PipIconButton _btnClose;
            private readonly PipVolumeBar _volumeSlider;
            private readonly Panel _volumePopup;
            private readonly ToolTip _toolTip = new();
            private readonly System.Windows.Forms.Timer _refreshTimer;
            private string? _artKey;
            private Image? _artImage;
            private bool _allowClose;
            private bool _dragging;
            private bool _resizing;
            private Point _dragStart;
            private Point _formStart;
            private Size _resizeStartSize;
            private DateTime _lastButtonInvokeUtc = DateTime.MinValue;
            private ColumnStyle? _mediaColumnStyle;
            private bool _videoMode;
            private double _videoAspect = 16.0 / 9.0;
            private bool _restoreQueued;
            private bool _updatingVolume;
            private DateTime _volumePopupShownUtc = DateTime.MinValue;

            public PipMiniPlayerForm(
                Func<PipMiniPlayerState> stateProvider,
                Action togglePlayPause,
                Action previous,
                Action back10,
                Action next,
                Action forward10,
                Action stopPlayback,
                Action toggleShuffle,
                Action toggleLoop,
                Action restoreStandard,
                Action<double> seekTo,
                Action toggleMute,
                Action<float> setVolume,
                Action<nint, Rectangle> syncVideoSurface,
                Action<Message> forwardVideoSurfaceMessage,
                Action restoreVideoSurface,
                bool english, Action? showLyrics = null)
            {
                _stateProvider = stateProvider;
                _showMusicLyrics = showLyrics;
                _togglePlayPause = togglePlayPause;
                _previous = previous;
                _back10 = back10;
                _next = next;
                _forward10 = forward10;
                _stopPlayback = stopPlayback;
                _toggleShuffle = toggleShuffle;
                _toggleLoop = toggleLoop;
                _restoreStandard = restoreStandard;
                _seekTo = seekTo;
                _toggleMute = toggleMute;
                _setVolume = setVolume;
                _syncVideoSurface = syncVideoSurface;
                _forwardVideoSurfaceMessage = forwardVideoSurfaceMessage;
                _restoreVideoSurface = restoreVideoSurface;

                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = true;
                TopMost = true;
                KeyPreview = true;
                // Bordo neutro e sottile: il vecchio blu pieno trasformava il PiP in
                // un rettangolone estraneo al resto del player.
                BackColor = Color.FromArgb(45, 53, 60);
                ForeColor = Color.Gainsboro;
                Size = new Size(480, 164);
                MinimumSize = new Size(452, 158);
                Padding = new Padding(1);
                DoubleBuffered = true;

                _surface = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(7, 11, 15),
                    Padding = new Padding(7),
                    Margin = new Padding(0)
                };
                Controls.Add(_surface);

                _resizeGrip = new Panel
                {
                    Width = 10,
                    Height = 10,
                    BackColor = Color.FromArgb(8, 12, 17),
                    Cursor = Cursors.Default,
                    Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                    Visible = false
                };
                _resizeGrip.MouseDown += (_, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    _resizing = true;
                    _dragStart = Cursor.Position;
                    _resizeStartSize = Size;
                    _resizeGrip.Capture = true;
                };
                _resizeGrip.MouseMove += (_, __) =>
                {
                    if (!_resizing) return;
                    var p = Cursor.Position;
                    int w = Math.Max(MinimumSize.Width, _resizeStartSize.Width + p.X - _dragStart.X);
                    int h = Math.Max(MinimumSize.Height, _resizeStartSize.Height + p.Y - _dragStart.Y);
                    Size = new Size(w, h);
                };
                _resizeGrip.MouseUp += (_, __) =>
                {
                    _resizing = false;
                    _resizeGrip.Capture = false;
                    if (_videoMode)
                        SyncVideoSurfaceIfNeeded();
                };
                Controls.Add(_resizeGrip);
                PositionResizeGrip();
                _resizeGrip.BringToFront();

                _root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(8, 12, 17),
                    ColumnCount = 2,
                    RowCount = 1,
                    Padding = new Padding(0)
                };
                _mediaColumnStyle = new ColumnStyle(SizeType.Absolute, 120);
                _root.ColumnStyles.Add(_mediaColumnStyle);
                _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                _surface.Controls.Add(_root);

                _left = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(8, 12, 17),
                    ColumnCount = 1,
                    RowCount = 1,
                    Margin = new Padding(0, 0, 12, 0),
                    Padding = new Padding(0)
                };
                _left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                _left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                _root.Controls.Add(_left, 0, 0);

                _mediaHost = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(8, 12, 17),
                    Margin = new Padding(0)
                };
                _mediaHost.Resize += (_, __) =>
                {
                    if (_videoMode)
                    {
                        LayoutPipVideoSurface();
                        if (!_resizing)
                            SyncVideoSurfaceIfNeeded();
                    }
                };
                _left.Controls.Add(_mediaHost, 0, 0);

                _art = new PipCoverPictureBox
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(8, 12, 17),
                    SizeMode = PictureBoxSizeMode.Normal,
                    Margin = new Padding(0)
                };
                _mediaHost.Controls.Add(_art);

                _videoSurface = new PipVideoSurfacePanel
                {
                    Visible = false,
                    Margin = new Padding(0)
                };
                _videoSurface.ForwardOwnerMessage = _forwardVideoSurfaceMessage;
                _videoSurface.Resize += (_, __) =>
                {
                    if (!_resizing)
                        SyncVideoSurfaceIfNeeded();
                };
                _mediaHost.Controls.Add(_videoSurface);
                _videoSurface.BringToFront();

                _right = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(8, 12, 17),
                    ColumnCount = 1,
                    RowCount = 3,
                    Padding = new Padding(0)
                };
                _right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                _right.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
                _right.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
                _root.Controls.Add(_right, 1, 0);

                _textPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(8, 12, 17) };
                _title = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 29,
                    AutoEllipsis = true,
                    Font = SafeSemibold(11.2f),
                    ForeColor = Color.White,
                    BackColor = Color.FromArgb(8, 12, 17),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                _subtitle = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 20,
                    AutoEllipsis = true,
                    Font = SafeRegular(8.3f),
                    ForeColor = Color.FromArgb(154, 169, 187),
                    BackColor = Color.FromArgb(8, 12, 17),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                _textPanel.Controls.Add(_subtitle);
                _textPanel.Controls.Add(_title);
                _right.Controls.Add(_textPanel, 0, 0);

                _timeline = new PipTimelineBar();
                _timeline.SeekRequested = seconds => _seekTo(seconds);
                _right.Controls.Add(_timeline, 0, 1);

                _controls = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    BackColor = Color.FromArgb(8, 12, 17),
                    Padding = new Padding(0)
                };
                _btnMute = MakePipButton(PipButtonKind.Mute, 28);
                _btnPrev = MakePipButton(PipButtonKind.Previous, 28);
                _btnBack10 = MakePipButton(PipButtonKind.Back10, 30);
                _btnPlay = MakePipButton(PipButtonKind.PlayPause, 36, primary: true);
                _btnStop = MakePipButton(PipButtonKind.Stop, 30);
                _btnForward10 = MakePipButton(PipButtonKind.Forward10, 30);
                _btnNext = MakePipButton(PipButtonKind.Next, 28);
                _btnShuffle = MakePipButton(PipButtonKind.Shuffle, 28);
                _btnRepeat = MakePipButton(PipButtonKind.Repeat, 28);
                _btnClose = MakePipButton(PipButtonKind.Close, 28);
                _volumeSlider = new PipVolumeBar
                {
                    Width = 122,
                    Height = 30,
                    BackColor = Color.FromArgb(8, 12, 17)
                };
                _volumeSlider.ValueChanged = value =>
                {
                    if (_updatingVolume) return;
                    try { _setVolume(value); } catch { }
                };
                _volumePopup = new Panel
                {
                    Width = 142,
                    Height = 42,
                    Padding = new Padding(10, 6, 10, 6),
                    BackColor = Theme.IsLight ? Color.FromArgb(230, 234, 239) : Theme.Card,
                    Visible = false
                };
                _volumeSlider.Dock = DockStyle.Fill;
                _volumeSlider.BackColor = _volumePopup.BackColor;
                _volumePopup.Controls.Add(_volumeSlider);
                _volumeSlider.InteractionFinished = () =>
                {
                    try { _volumePopup.Visible = false; } catch { }
                };
                _surface.Controls.Add(_volumePopup);

                _controls.Controls.Add(_btnMute);
                _controls.Controls.Add(_btnPrev);
                _controls.Controls.Add(_btnBack10);
                _controls.Controls.Add(_btnPlay);
                _controls.Controls.Add(_btnStop);
                _controls.Controls.Add(_btnForward10);
                _controls.Controls.Add(_btnNext);
                _controls.Controls.Add(_btnShuffle);
                _controls.Controls.Add(_btnRepeat);
                _controls.Controls.Add(_btnClose);
                _right.Controls.Add(_controls, 0, 2);

                _toolTip.SetToolTip(_btnPrev, english ? "Previous chapter or track" : "Capitolo o traccia precedente");
                _toolTip.SetToolTip(_btnBack10, english ? "Back 10 seconds" : "Indietro di 10 secondi");
                _toolTip.SetToolTip(_btnPlay, english ? "Play / pause" : "Play / pausa");
                _toolTip.SetToolTip(_btnForward10, english ? "Forward 10 seconds" : "Avanti di 10 secondi");
                _toolTip.SetToolTip(_btnNext, english ? "Next chapter or track" : "Capitolo o traccia successiva");
                _toolTip.SetToolTip(_btnStop, "Stop");
                _toolTip.SetToolTip(_btnShuffle, english ? "Shuffle" : "Riproduzione casuale");
                _toolTip.SetToolTip(_btnRepeat, english ? "Repeat item" : "Ripeti elemento");
                _toolTip.SetToolTip(_btnClose, english ? "Close PiP" : "Chiudi PiP");
                _toolTip.SetToolTip(_btnMute, english ? "Volume; double-click to mute" : "Volume; doppio click per silenziare");
                BindPipButton(_btnPlay, _togglePlayPause);
                BindPipButton(_btnPrev, _previous);
                BindPipButton(_btnBack10, _back10);
                BindPipButton(_btnForward10, _forward10);
                BindPipButton(_btnNext, _next);
                BindPipButton(_btnStop, _stopPlayback);
                BindPipButton(_btnShuffle, _toggleShuffle);
                BindPipButton(_btnRepeat, _toggleLoop);
                BindPipButton(_btnClose, _restoreStandard);
                BindPipButton(_btnMute, () =>
                {
                    ToggleVolumePopup();
                });
                _btnMute.DoubleClick += (_, __) => { try { _toggleMute(); } catch { } };

                WireDrag(this);
                WireDrag(_surface);
                WireDrag(_root);
                WireDrag(_left);
                WireDrag(_art);
                WireDrag(_videoSurface);
                WireDrag(_right);
                WireDrag(_textPanel);
                WireDrag(_title);
                WireDrag(_subtitle);

                // L'espansione è volutamente separata dai tasti di trasporto:
                // doppio click sulla superficie, mai un click accidentale sui controlli.
                foreach (Control target in new Control[] { _surface, _mediaHost, _art, _title })
                    target.DoubleClick += (_, __) => { try { _restoreStandard(); } catch { } };

                ConfigurePipLayout(showVideo: false);

                _refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
                _refreshTimer.Tick += (_, __) =>
                {
                    RefreshState();
                    if (_volumePopup.Visible && DateTime.UtcNow - _volumePopupShownUtc > TimeSpan.FromSeconds(3))
                        _volumePopup.Visible = false;
                };
                Deactivate += (_, __) => { try { _volumePopup.Visible = false; } catch { } };
            }

            private void PositionResizeGrip()
            {
                if (_resizeGrip == null || _resizeGrip.IsDisposed)
                    return;

                try
                {
                    _resizeGrip.Location = new Point(
                        Math.Max(0, ClientSize.Width - _resizeGrip.Width),
                        Math.Max(0, ClientSize.Height - _resizeGrip.Height));
                    _resizeGrip.BringToFront();
                }
                catch { }
            }

            public void ShowIndependent(Form owner)
            {
                if (!Visible)
                {
                    Rectangle area;
                    try { area = Screen.FromControl(owner).WorkingArea; }
                    catch { area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720); }

                    Location = new Point(
                        Math.Max(area.Left + 12, area.Right - Width - 18),
                        Math.Max(area.Top + 12, area.Bottom - Height - 18));
                }

                if (!Visible)
                    Show();
                else
                    BringToFront();

                if (!_refreshTimer.Enabled)
                    _refreshTimer.Start();
            }

            public void RefreshState()
            {
                try
                {
                    var state = _stateProvider();
                    _title.Text = state.Title;
                    _subtitle.Text = state.Subtitle;
                    _btnPlay.IsPaused = state.IsPaused;
                    _btnMute.IsMuted = state.Muted;
                    _btnMute.Enabled = _volumeSlider.Enabled = state.VolumeEditable;
                    if (!state.VolumeEditable) _volumePopup.Visible = false;
                    _btnShuffle.IsActive = state.ShuffleActive;
                    _btnRepeat.IsActive = state.LoopActive;
                    _btnPrev.Enabled = state.CanPrevious;
                    _btnNext.Enabled = state.CanNext;
                    _btnBack10.Enabled = state.HasPlayback;
                    _btnForward10.Enabled = state.HasPlayback;
                    _btnPlay.Enabled = state.HasPlayback;
                    _btnStop.Enabled = state.HasPlayback;
                    _btnShuffle.Enabled = state.HasPlayback;
                    _btnRepeat.Enabled = state.HasPlayback;
                    _timeline.Enabled = state.DurationSeconds > 0;
                    _timeline.SetProgress(state.PositionSeconds, state.DurationSeconds);
                    _updatingVolume = true;
                    _volumeSlider.SetValue(state.Muted ? 0f : state.Volume);
                    _updatingVolume = false;
                    _btnPlay.Invalidate();
                    _btnStop.Invalidate();
                    _btnMute.Invalidate();
                    _btnShuffle.Invalidate();
                    _btnRepeat.Invalidate();
                    bool videoAspectChanged = UpdateVideoAspect(state);
                    ApplyMediaMode(state.ShowVideo);
                    UpdateMusicMini(state);
                    if (state.ShowVideo && videoAspectChanged)
                        ApplyVideoPipSizeForAspect(force: false);
                    if (state.ShowVideo)
                    {
                        LayoutPipVideoSurface();
                        SyncVideoSurfaceIfNeeded();
                    }
                    else
                        RefreshArtwork(state);
                }
                catch { }
            }

            private void ToggleVolumePopup()
            {
                try
                {
                    if (_volumePopup.Visible)
                    {
                        _volumePopup.Visible = false;
                        return;
                    }

                    Point button = _surface.PointToClient(_btnMute.PointToScreen(Point.Empty));
                    int x = Math.Max(0, Math.Min(_surface.ClientSize.Width - _volumePopup.Width,
                        button.X - (_volumePopup.Width - _btnMute.Width) / 2));
                    int y = Math.Max(0, button.Y - _volumePopup.Height - 4);
                    _volumePopup.Location = new Point(x, y);
                    _volumePopup.Visible = true;
                    _volumePopupShownUtc = DateTime.UtcNow;
                    _volumePopup.BringToFront();
                }
                catch { }
            }

            private bool UpdateVideoAspect(PipMiniPlayerState state)
            {
                double previous = _videoAspect;
                try
                {
                    int w = state.VideoWidth;
                    int h = state.VideoHeight;
                    if (w > 0 && h > 0)
                        _videoAspect = Math.Max(0.25, Math.Min(4.0, w / (double)h));
                }
                catch { _videoAspect = 16.0 / 9.0; }

                return Math.Abs(previous - _videoAspect) > 0.03;
            }

            public void DestroyForAppExit()
            {
                _allowClose = true;
                try { _refreshTimer.Stop(); } catch { }
                try { Close(); } catch { Dispose(); }
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (Visible)
                {
                    RefreshState();
                    if (!_refreshTimer.Enabled)
                        _refreshTimer.Start();
                }
                else
                {
                    _refreshTimer.Stop();
                    try { _restoreVideoSurface(); } catch { }
                }
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                try
                {
                    Region?.Dispose();
                    using var rounded = BuildPipRoundedRect(ClientRectangle, 4);
                    Region = new Region(rounded);
                }
                catch { }
                PositionResizeGrip();
                if (_videoMode && !_resizing)
                    SyncVideoSurfaceIfNeeded();
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (!_allowClose)
                {
                    e.Cancel = true;
                    if (_restoreQueued) return;
                    _restoreQueued = true;
                    try { _refreshTimer.Stop(); } catch { }
                    try { Hide(); } catch { }
                    try { _restoreVideoSurface(); } catch { }
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try { _restoreStandard(); } catch { }
                            finally { _restoreQueued = false; }
                        }));
                    }
                    catch { _restoreQueued = false; }
                    return;
                }

                base.OnFormClosing(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _restoreVideoSurface(); } catch { }
                    try { _refreshTimer.Dispose(); } catch { }
                    try { _toolTip.Dispose(); } catch { }
                    try { _artImage?.Dispose(); } catch { }
                    _artImage = null;
                }
                base.Dispose(disposing);
            }

            private void RefreshArtwork(PipMiniPlayerState state)
            {
                string key = !string.IsNullOrWhiteSpace(state.ArtworkPath)
                    ? "art:" + state.ArtworkPath
                    : "icon:" + (state.MediaPath ?? string.Empty);

                if (string.Equals(_artKey, key, StringComparison.OrdinalIgnoreCase))
                    return;

                _artKey = key;
                try { _artImage?.Dispose(); } catch { }
                _artImage = LoadArtwork(state.ArtworkPath, state.MediaPath);
                _art.Image = _artImage;
            }

            private void ApplyMediaMode(bool showVideo)
            {
                if (_videoMode == showVideo)
                    return;

                _videoMode = showVideo;
                ConfigurePipLayout(showVideo);

                _videoSurface.Visible = showVideo;
                _art.Visible = !showVideo;
                if (showVideo)
                {
                    _videoSurface.BringToFront();
                    try { _art.Image = null; } catch { }
                }
                else
                {
                    _art.BringToFront();
                    try { _restoreVideoSurface(); } catch { }
                }

                PerformLayout();
                LayoutPipVideoSurface();
            }

            private void ConfigurePipLayout(bool showVideo)
            {
                try
                {
                    SuspendLayout();
                    _surface.SuspendLayout();
                    _root.SuspendLayout();
                    _right.SuspendLayout();
                    _root.Controls.Clear();
                    _root.ColumnStyles.Clear();
                    _root.RowStyles.Clear();
                    _right.Controls.Clear();
                    _right.ColumnStyles.Clear();
                    _right.RowStyles.Clear();

                    Size = new Size(480, 164);
                    MinimumSize = new Size(452, 158);
                    _root.ColumnCount = 2;
                    _root.RowCount = 1;
                    _mediaColumnStyle = new ColumnStyle(SizeType.Absolute, 120);
                    _root.ColumnStyles.Add(_mediaColumnStyle);
                    _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    _left.Margin = new Padding(0, 0, 12, 0);
                    _left.ColumnCount = 1;
                    _left.RowCount = 1;
                    _left.ColumnStyles.Clear();
                    _left.RowStyles.Clear();
                    _left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    _left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    _mediaHost.Margin = new Padding(0);
                    _right.Margin = new Padding(0);
                    _right.ColumnCount = 1;
                    _right.RowCount = 3;
                    _right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    _right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    _right.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
                    _right.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
                    _textPanel.Margin = new Padding(0, 1, 0, 0);
                    _timeline.Margin = new Padding(0);
                    _timeline.Dock = DockStyle.Fill;
                    _controls.Dock = DockStyle.None;
                    _controls.Anchor = AnchorStyles.None;
                    _controls.AutoSize = true;
                    _controls.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                    _controls.Margin = new Padding(0);
                    _controls.Padding = new Padding(0);
                    _left.Controls.Clear();
                    _left.Controls.Add(_mediaHost, 0, 0);
                    _root.Controls.Add(_left, 0, 0);
                    _right.Controls.Add(_textPanel, 0, 0);
                    _right.Controls.Add(_timeline, 0, 1);
                    _right.Controls.Add(_controls, 0, 2);
                    _root.Controls.Add(_right, 1, 0);
                }
                finally
                {
                    try { _right.ResumeLayout(true); } catch { }
                    try { _root.ResumeLayout(true); } catch { }
                    try { _surface.ResumeLayout(true); } catch { }
                    try { ResumeLayout(true); } catch { }
                }
            }

            private void ApplyVideoPipSizeForAspect(bool force)
            {
                // Il layout resta stabile tra audio e video: niente salti o controlli compressi.
            }

            private static Size CalculateVideoPipSize(double aspect)
            {
                aspect = Math.Max(0.35, Math.Min(3.2, aspect));

                int mediaW;
                if (aspect < 0.9)
                    mediaW = 360;
                else if (aspect > 2.05)
                    mediaW = 560;
                else
                    mediaW = 520;

                int mediaH = (int)Math.Round(mediaW / aspect);
                mediaH = Math.Max(180, Math.Min(440, mediaH));

                const int surfacePad = 20;
                const int mediaBottomGap = 7;
                const int controlsBlock = 112;
                return new Size(mediaW + surfacePad, mediaH + mediaBottomGap + controlsBlock + surfacePad);
            }

            private void SyncVideoSurfaceIfNeeded()
            {
                try
                {
                    if (!_videoMode || !_videoSurface.Visible || !_videoSurface.IsHandleCreated)
                        return;
                    if (_resizing)
                        return;

                    LayoutPipVideoSurface();
                    var bounds = _videoSurface.ClientRectangle;
                    if (bounds.Width < 2 || bounds.Height < 2)
                        return;

                    _syncVideoSurface(_videoSurface.Handle, bounds);
                }
                catch { }
            }

            private void LayoutPipVideoSurface()
            {
                try
                {
                    if (!_videoMode || _mediaHost == null || _videoSurface == null || _videoSurface.IsDisposed)
                        return;

                    var host = _mediaHost.ClientRectangle;
                    host.Inflate(-1, -1);
                    if (host.Width < 2 || host.Height < 2)
                        return;

                    double aspect = Math.Max(0.25, Math.Min(4.0, _videoAspect));
                    int w = host.Width;
                    int h = (int)Math.Round(w / aspect);
                    if (h > host.Height)
                    {
                        h = host.Height;
                        w = (int)Math.Round(h * aspect);
                    }

                    w = Math.Max(2, Math.Min(host.Width, w));
                    h = Math.Max(2, Math.Min(host.Height, h));
                    int x = host.Left + (host.Width - w) / 2;
                    int y = host.Top + (host.Height - h) / 2;
                    _videoSurface.Bounds = new Rectangle(x, y, w, h);
                    _videoSurface.BringToFront();
                }
                catch { }
            }

            private static Image LoadArtwork(string? artworkPath, string? mediaPath)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(artworkPath) && File.Exists(artworkPath))
                    {
                        using var src = Image.FromFile(artworkPath);
                        return CreateArtworkBitmap(src, 512);
                    }
                }
                catch { }

                return BuildFallbackArtwork();
            }

            private static Bitmap CreateArtworkBitmap(Image src, int size)
            {
                var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;

                float scale = Math.Max(size / (float)Math.Max(1, src.Width), size / (float)Math.Max(1, src.Height));
                int w = Math.Max(1, (int)Math.Round(src.Width * scale));
                int h = Math.Max(1, (int)Math.Round(src.Height * scale));
                int x = (size - w) / 2;
                int y = (size - h) / 2;
                g.DrawImage(src, new Rectangle(x, y, w, h));
                return bmp;
            }

            private static Bitmap BuildFallbackArtwork()
            {
                var bmp = new Bitmap(512, 512, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.FromArgb(9, 16, 24));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var panel = new SolidBrush(Color.FromArgb(15, 28, 40)))
                    g.FillRectangle(panel, 42, 42, 428, 428);
                using (var accent = new SolidBrush(Color.FromArgb(18, Theme.Accent)))
                    g.FillRectangle(accent, 42, 42, 9, 428);
                try
                {
                    string? iconPath = AssetIconService.Resolve("movie");
                    using Bitmap? icon = string.IsNullOrWhiteSpace(iconPath)
                        ? null
                        : AssetIconService.RenderSvg(iconPath, 112, Color.FromArgb(112, 144, 169));
                    if (icon != null)
                        g.DrawImageUnscaled(icon, (bmp.Width - icon.Width) / 2, (bmp.Height - icon.Height) / 2 - 18);
                }
                catch { }
                return bmp;
            }

            private static PipIconButton MakePipButton(PipButtonKind kind, int width, bool primary = false)
            {
                var b = new PipIconButton(kind, primary)
                {
                    Width = width,
                    Height = 34,
                    Margin = new Padding(primary ? 3 : 1, 0, primary ? 3 : 1, 0),
                    TabStop = true,
                    BackColor = Color.FromArgb(8, 12, 17),
                    ForeColor = Color.White
                };
                return b;
            }

            private void BindPipButton(Button button, Action action)
            {
                void Invoke()
                {
                    try
                    {
                        if (!button.Enabled)
                            return;

                        var now = DateTime.UtcNow;
                        if ((now - _lastButtonInvokeUtc).TotalMilliseconds < 90)
                            return;

                        _lastButtonInvokeUtc = now;
                        action();
                        if (!IsDisposed && Visible)
                        {
                            try { BeginInvoke(new Action(RefreshState)); } catch { }
                        }
                    }
                    catch { }
                }

                button.MouseUp += (_, e) =>
                {
                    if (e.Button != MouseButtons.Left)
                        return;
                    if (!button.ClientRectangle.Contains(e.Location))
                        return;
                    Invoke();
                };
                button.KeyDown += (_, e) =>
                {
                    if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Space)
                        return;
                    e.Handled = true;
                    Invoke();
                };
            }

            private void WireDrag(Control c)
            {
                c.MouseDown += (_, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    _dragging = true;
                    _dragStart = Cursor.Position;
                    _formStart = Location;
                };
                c.MouseMove += (_, __) =>
                {
                    if (!_dragging) return;
                    var p = Cursor.Position;
                    Location = new Point(_formStart.X + p.X - _dragStart.X, _formStart.Y + p.Y - _dragStart.Y);
                };
                c.MouseUp += (_, __) => _dragging = false;
            }

            private static GraphicsPath BuildPipRoundedRect(Rectangle r, int radius)
            {
                int d = Math.Max(2, radius * 2);
                var path = new GraphicsPath();
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        private sealed class PlaybackQueueEditorForm : Form
        {
            private sealed class QueueRow
            {
                public string Path { get; set; } = string.Empty;
                public string Label { get; set; } = string.Empty;
                public int Index { get; set; }
                public bool IsCurrent { get; set; }
            }

            private readonly Func<IReadOnlyList<PlaybackQueueViewItem>> _snapshotProvider;
            private readonly Action<string> _playNow;
            private readonly Action<string> _removeOne;
            private readonly Action _clearAll;
            private readonly Action<string, int> _reorder;
            private readonly ListView _list;
            private readonly Label _subtitle;
            private readonly Label _countLabel;
            private readonly Button _btnPlay;
            private readonly Button _btnUp;
            private readonly Button _btnDown;
            private readonly Button _btnRemove;

            public PlaybackQueueEditorForm(
                Func<IReadOnlyList<PlaybackQueueViewItem>> snapshotProvider,
                Action<string> playNow,
                Action<string> removeOne,
                Action clearAll,
                Action<string, int> reorder)
            {
                _snapshotProvider = snapshotProvider;
                _playNow = playNow;
                _removeOne = removeOne;
                _clearAll = clearAll;
                _reorder = reorder;

                Text = "Coda di riproduzione";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.SizableToolWindow;
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = false;
                MinimumSize = new Size(780, 520);
                ClientSize = new Size(980, 660);
                BackColor = Color.FromArgb(10, 12, 18);
                ForeColor = Color.Gainsboro;
                KeyPreview = true;

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 4,
                    BackColor = Color.FromArgb(10, 12, 18),
                    Padding = new Padding(18),
                    Margin = new Padding(0)
                };
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
                Controls.Add(root);

                var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0) };
                header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                root.Controls.Add(header, 0, 0);

                header.Controls.Add(new Label
                {
                    Text = "Coda di riproduzione",
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI Semibold", 16f),
                    ForeColor = Color.White,
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleLeft
                }, 0, 0);

                _countLabel = new Label
                {
                    AutoSize = true,
                    Anchor = AnchorStyles.Right,
                    Font = new Font("Segoe UI Semibold", 9.5f),
                    ForeColor = Color.FromArgb(146, 160, 184),
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleRight,
                    Margin = new Padding(12, 0, 0, 0)
                };
                header.Controls.Add(_countLabel, 1, 0);

                _subtitle = new Label
                {
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 9.4f),
                    ForeColor = Color.FromArgb(184, 192, 204),
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.TopLeft,
                    Margin = new Padding(0)
                };
                root.Controls.Add(_subtitle, 0, 1);

                var listHost = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(16, 20, 30),
                    Padding = new Padding(1),
                    Margin = new Padding(0, 0, 0, 14)
                };
                listHost.Paint += (_, e) =>
                {
                    var r = listHost.ClientRectangle;
                    r.Width -= 1; r.Height -= 1;
                    using var pen = new Pen(Color.FromArgb(62, 74, 96));
                    e.Graphics.DrawRectangle(pen, r);
                };
                root.Controls.Add(listHost, 0, 2);

                _list = new ListView
                {
                    Dock = DockStyle.Fill,
                    View = View.Details,
                    FullRowSelect = true,
                    HideSelection = false,
                    MultiSelect = false,
                    BorderStyle = BorderStyle.None,
                    BackColor = Color.FromArgb(16, 20, 30),
                    ForeColor = Color.Gainsboro,
                    Font = new Font("Segoe UI", 9.5f),
                    AllowDrop = true,
                    HeaderStyle = ColumnHeaderStyle.Nonclickable
                };
                _list.Columns.Add("#", 52, HorizontalAlignment.Right);
                _list.Columns.Add("Titolo", 310, HorizontalAlignment.Left);
                _list.Columns.Add("Stato", 120, HorizontalAlignment.Left);
                _list.Columns.Add("Percorso", 440, HorizontalAlignment.Left);
                listHost.Controls.Add(_list);

                _list.DoubleClick += (_, __) => PlaySelectedAndClose();
                _list.KeyDown += OnQueueKeyDown;
                _list.SelectedIndexChanged += (_, __) => UpdateButtonState();
                _list.ItemDrag += (_, e) =>
                {
                    if (e.Item is ListViewItem lvi && lvi.Tag is QueueRow row)
                        _list.DoDragDrop(row, DragDropEffects.Move);
                };
                _list.DragEnter += (_, e) => { e.Effect = e.Data?.GetDataPresent(typeof(QueueRow)) == true ? DragDropEffects.Move : DragDropEffects.None; };
                _list.DragOver += (_, e) => { e.Effect = e.Data?.GetDataPresent(typeof(QueueRow)) == true ? DragDropEffects.Move : DragDropEffects.None; };
                _list.DragDrop += OnQueueDragDrop;

                var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0) };
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                root.Controls.Add(bottom, 0, 3);

                bottom.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "Enter riproduce. Canc rimuove. Ctrl/Alt+Su/Giù riordina. Trascina le righe per cambiare ordine.",
                    ForeColor = Color.FromArgb(130, 142, 160),
                    Font = new Font("Segoe UI", 8.8f),
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleLeft
                }, 0, 0);

                var buttons = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Fill,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0),
                    Padding = new Padding(0)
                };
                bottom.Controls.Add(buttons, 1, 0);

                Button MakeButton(string text, int width, Action onClick, bool primary = false)
                {
                    var b = new Button
                    {
                        Text = text,
                        Width = width,
                        Height = 36,
                        Margin = new Padding(8, 10, 0, 0),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = primary ? Color.FromArgb(42, 76, 146) : Color.FromArgb(27, 32, 44),
                        ForeColor = Color.White,
                        Font = new Font("Segoe UI Semibold", 9f),
                        UseVisualStyleBackColor = false,
                        TabStop = true
                    };
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.BorderColor = primary ? Color.FromArgb(94, 154, 255) : Color.FromArgb(74, 88, 110);
                    b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(54, 92, 174) : Color.FromArgb(36, 43, 58);
                    b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(34, 64, 126) : Color.FromArgb(22, 26, 36);
                    b.Click += (_, __) => onClick();
                    return b;
                }

                _btnPlay = MakeButton("Riproduci", 104, PlaySelectedAndClose, true);
                _btnUp = MakeButton("Su", 64, () => MoveSelected(-1));
                _btnDown = MakeButton("Giù", 64, () => MoveSelected(+1));
                _btnRemove = MakeButton("Rimuovi", 92, RemoveSelected);
                var btnClose = MakeButton("Chiudi", 86, () => Close());
                buttons.Controls.Add(_btnPlay);
                buttons.Controls.Add(_btnUp);
                buttons.Controls.Add(_btnDown);
                buttons.Controls.Add(_btnRemove);
                buttons.Controls.Add(btnClose);

                Resize += (_, __) => ResizeColumns();
                KeyDown += OnQueueKeyDown;
                Shown += (_, __) => FocusList();
                ReloadSnapshot();
            }

            public void FocusList()
            {
                try { _list.Focus(); } catch { }
            }

            public void RefreshSnapshotExternal(string? selectPath = null)
            {
                if (IsDisposed) return;
                if (InvokeRequired)
                {
                    try { BeginInvoke(new Action(() => RefreshSnapshotExternal(selectPath))); } catch { }
                    return;
                }
                ReloadSnapshot(selectPath);
            }

            private QueueRow? GetSelectedRow()
            {
                return _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as QueueRow : null;
            }

            private void ResizeColumns()
            {
                try
                {
                    int w = Math.Max(200, _list.ClientSize.Width - 8);
                    _list.Columns[0].Width = 52;
                    _list.Columns[2].Width = 128;
                    _list.Columns[1].Width = Math.Max(220, (int)(w * 0.38));
                    _list.Columns[3].Width = Math.Max(260, w - _list.Columns[0].Width - _list.Columns[1].Width - _list.Columns[2].Width);
                }
                catch { }
            }

            private void ReloadSnapshot(string? selectPath = null)
            {
                var snapshot = (_snapshotProvider?.Invoke() ?? Array.Empty<PlaybackQueueViewItem>())
                    .OrderBy(item => item.Index)
                    .ToList();

                string? keepPath = string.IsNullOrWhiteSpace(selectPath) ? GetSelectedRow()?.Path : selectPath;

                _list.BeginUpdate();
                try
                {
                    _list.Items.Clear();
                    foreach (var item in snapshot)
                    {
                        var row = new QueueRow
                        {
                            Path = item.Path,
                            Label = string.IsNullOrWhiteSpace(item.Label) ? Path.GetFileName(item.Path) : item.Label,
                            Index = item.Index,
                            IsCurrent = item.IsCurrent
                        };
                        var lvi = new ListViewItem((row.Index + 1).ToString("00")) { Tag = row };
                        lvi.SubItems.Add(row.Label);
                        lvi.SubItems.Add(row.IsCurrent ? "In riproduzione" : "In coda");
                        lvi.SubItems.Add(row.Path);
                        if (row.IsCurrent)
                        {
                            lvi.BackColor = Color.FromArgb(26, 42, 72);
                            lvi.ForeColor = Color.White;
                        }
                        else
                        {
                            lvi.BackColor = Color.FromArgb(16, 20, 30);
                            lvi.ForeColor = Color.Gainsboro;
                        }
                        _list.Items.Add(lvi);
                    }
                }
                finally { _list.EndUpdate(); }

                _countLabel.Text = snapshot.Count == 1 ? "1 elemento" : $"{snapshot.Count} elementi";
                _subtitle.Text = snapshot.Count == 0
                    ? "La coda è vuota. Aggiungi elementi dalla libreria o dal menu contestuale."
                    : "Enter riproduce la selezione, Canc rimuove, Ctrl/Alt+Su/Giù riordina.";

                int selectedIndex = -1;
                if (!string.IsNullOrWhiteSpace(keepPath))
                {
                    for (int i = 0; i < _list.Items.Count; i++)
                    {
                        if (_list.Items[i].Tag is QueueRow row && string.Equals(row.Path, keepPath, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                }
                if (selectedIndex < 0)
                {
                    for (int i = 0; i < _list.Items.Count; i++)
                    {
                        if (_list.Items[i].Tag is QueueRow row && row.IsCurrent)
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                }
                if (selectedIndex < 0 && _list.Items.Count > 0) selectedIndex = 0;
                if (selectedIndex >= 0) _list.Items[selectedIndex].Selected = true;

                ResizeColumns();
                UpdateButtonState();
                FocusList();
            }

            private void UpdateButtonState()
            {
                bool hasSelection = GetSelectedRow() != null;
                _btnPlay.Enabled = hasSelection;
                _btnRemove.Enabled = hasSelection;
                int idx = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
                _btnUp.Enabled = hasSelection && idx > 0;
                _btnDown.Enabled = hasSelection && idx >= 0 && idx < _list.Items.Count - 1;
            }

            private void PlaySelectedAndClose()
            {
                var row = GetSelectedRow();
                if (row == null) return;
                _playNow(row.Path);
                Close();
            }

            private void RemoveSelected()
            {
                var row = GetSelectedRow();
                if (row == null) return;
                int oldIndex = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : 0;
                _removeOne(row.Path);
                ReloadSnapshot();
                if (_list.Items.Count > 0)
                    _list.Items[Math.Max(0, Math.Min(oldIndex, _list.Items.Count - 1))].Selected = true;
                UpdateButtonState();
            }

            private void MoveSelected(int delta)
            {
                var row = GetSelectedRow();
                if (row == null || delta == 0) return;
                int oldIndex = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
                int target = oldIndex + delta;
                if (target < 0 || target >= _list.Items.Count) return;
                _reorder(row.Path, target);
                ReloadSnapshot(row.Path);
            }

            private void OnQueueKeyDown(object? sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Enter) { PlaySelectedAndClose(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; return; }
                if ((e.Alt || e.Control) && e.KeyCode == Keys.Up) { MoveSelected(-1); e.Handled = true; return; }
                if ((e.Alt || e.Control) && e.KeyCode == Keys.Down) { MoveSelected(+1); e.Handled = true; return; }
                if (e.Control && e.KeyCode == Keys.L) { _clearAll(); ReloadSnapshot(); e.Handled = true; }
            }

            private void OnQueueDragDrop(object? sender, DragEventArgs e)
            {
                if (e.Data?.GetData(typeof(QueueRow)) is not QueueRow row) return;
                Point pt = _list.PointToClient(new Point(e.X, e.Y));
                int target = _list.Items.Count - 1;
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    var bounds = _list.Items[i].Bounds;
                    if (pt.Y < bounds.Top + bounds.Height / 2) { target = i; break; }
                    if (pt.Y <= bounds.Bottom) { target = i + 1; break; }
                }
                target = Math.Max(0, Math.Min(_list.Items.Count - 1, target));
                _reorder(row.Path, target);
                ReloadSnapshot(row.Path);
            }
        }
    }
}
