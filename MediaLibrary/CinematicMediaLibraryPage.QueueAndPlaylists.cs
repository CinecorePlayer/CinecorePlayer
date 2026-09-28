#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    internal sealed partial class CinematicMediaLibraryPage
    {
        public void OpenQueueEditorOverlay()
        {
            if (_queueEditorVisible) { _queueOverlay?.BringToFront(); return; }
            // Capture children too: PaintLibrary alone contains only the shell
            // when lyrics or analysis are hosted by a child control.
            _queueBackdrop?.Dispose();
            _queueBackdrop = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            DrawToBitmap(_queueBackdrop, ClientRectangle);
            _queueGlassCache?.Dispose(); _queueGlassCache = null;
            _queueEditorVisible = true;
            _queueOverlay ??= new QueueDrawer(this);
            if (_queueOverlay.Parent != this) Controls.Add(_queueOverlay);
            LayoutQueueDrawer();
            _queueOverlay.Visible = true;
            _queueOverlay.BringToFront();
            _queueEditorSelectionIndex = Math.Max(0, _queueEditorSelectionIndex);
            _queueEditorScroll = Math.Max(0, _queueEditorScroll);
            BringToFront();
            try { Focus(); } catch { }
            Invalidate();
        }

        public void RefreshQueueEditorOverlay()
        {
            if (!_queueEditorVisible)
                return;
            var snapshot = QueueSnapshotResolver?.Invoke() ?? Array.Empty<PlaybackQueueViewItem>();
            _queueEditorSelectionIndex = Math.Max(0, Math.Min(_queueEditorSelectionIndex, Math.Max(0, snapshot.Count - 1)));
            _queueEditorScrollMax = Math.Max(0, snapshot.Count - Math.Max(1, _queueEditorVisibleRows));
            _queueEditorScroll = Math.Max(0, Math.Min(_queueEditorScroll, _queueEditorScrollMax));
            EnsureQueueSelectionVisible();
            Invalidate();
        }

        private void CloseQueueEditorOverlay()
        {
            _queueEditorVisible = false;
            if (_queueOverlay != null) _queueOverlay.Visible = false;
            _queueHits.Clear();
            _queueBackdrop?.Dispose(); _queueBackdrop = null;
            _queueGlassCache?.Dispose(); _queueGlassCache = null;
            _queueDragIndex = -1;
            _queueDropIndex = -1;
            _queueDragPath = null;
            _queueDragging = false;
            try { Focus(); } catch { }
            Invalidate();
        }

        private void EnsureQueueSelectionVisible()
        {
            int visible = Math.Max(1, _queueEditorVisibleRows);
            if (_queueEditorSelectionIndex < _queueEditorScroll)
                _queueEditorScroll = _queueEditorSelectionIndex;
            else if (_queueEditorSelectionIndex >= _queueEditorScroll + visible)
                _queueEditorScroll = Math.Max(0, _queueEditorSelectionIndex - visible + 1);
            _queueEditorScroll = Math.Max(0, Math.Min(_queueEditorScroll, _queueEditorScrollMax));
        }

        private int QueueIndexAtPoint(Point point)
        {
            var rows = _queueHits.Where(h => h.Kind == HitKind.QueuePlay).ToList();
            return rows.Count == 0 ? -1 : rows.OrderBy(h => Math.Abs(h.Bounds.Top + h.Bounds.Height / 2 - point.Y)).First().Index;
        }

        private Bitmap? _queueBackdrop;
        private QueueDrawer? _queueOverlay;
        private readonly List<HitZone> _queueHits = new();

        private Rectangle QueueDrawerBounds()
        {
            int height = Math.Max(1, Height - MusicTransportInset);
            float scale = Math.Clamp(Math.Min(Width / 1440f, height / 950f), .86f, 1.15f);
            int width = Math.Max(1, Math.Min(Width - 32, S(scale, 420)));
            return new Rectangle(Width - width - 18, 20, width, Math.Max(1, height - 40));
        }

        private void LayoutQueueDrawer()
        {
            if (_queueOverlay == null) return;
            _queueOverlay.Bounds = QueueDrawerBounds();
            using var shape = Round(_queueOverlay.ClientRectangle, 13);
            var old = _queueOverlay.Region;
            _queueOverlay.Region = new Region(shape);
            old?.Dispose();
        }

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            base.OnInvalidated(e);
            if (_queueEditorVisible) _queueOverlay?.Invalidate();
        }

        // Only the drawer covers the page. The workspace stays visible and live.
        private sealed class QueueDrawer : Control, IMessageFilter
        {
            private readonly CinematicMediaLibraryPage _owner;
            private Bitmap? _layer;
            internal QueueDrawer(CinematicMediaLibraryPage owner)
            {
                _owner = owner; Visible = false; DoubleBuffered = true;
                BackColor = Color.FromArgb(12, 22, 30);
                SetStyle(ControlStyles.ResizeRedraw, true);
                VisibleChanged += (_, _) => { Application.RemoveMessageFilter(this); if (Visible) Application.AddMessageFilter(this); };
            }
            public bool PreFilterMessage(ref Message message)
            {
                if (!Visible || message.Msg != 0x201) return false;
                var control = Control.FromHandle(message.HWnd);
                if (control == null || (control != _owner && !_owner.Contains(control))) return false;
                var point = _owner.PointToClient(Control.MousePosition);
                if (point.Y >= _owner.Height - _owner.MusicTransportInset || Bounds.Contains(point)) return false;
                _owner.CloseQueueEditorOverlay();
                return true;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var normalHits = _owner._hits;
                _owner._hits = _owner._queueHits;
                try
                {
                    _owner._queueHits.Clear();
                    if (_layer == null || _layer.Size != _owner.ClientSize)
                    { _layer?.Dispose(); _layer = new Bitmap(_owner.Width, _owner.Height); }
                    // GDI TextRenderer and GDI+ icons must share untransformed
                    // coordinates; crop the finished drawer instead of translating.
                    using (var canvas = Graphics.FromImage(_layer))
                    {
                        canvas.SetClip(Bounds);
                        using var background = new SolidBrush(BackColor);
                        canvas.FillRectangle(background, Bounds);
                        _owner.DrawQueueEditor(canvas, _owner.ClientRectangle);
                    }
                    e.Graphics.DrawImage(_layer, ClientRectangle, Bounds, GraphicsUnit.Pixel);
                }
                finally { _owner._hits = normalHits; }
            }
            private MouseEventArgs Translate(MouseEventArgs e) => new(e.Button, e.Clicks, e.X + Left, e.Y + Top, e.Delta);
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _owner.OnMouseDown(Translate(e)); if (Visible) Capture = true; }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _owner.OnMouseUp(Translate(e)); Capture = false; }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); _owner.OnMouseMove(Translate(e)); Cursor = _owner.Cursor; }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _owner.OnMouseLeave(e); }
            protected override void OnMouseWheel(MouseEventArgs e) { _owner.ApplyMouseWheelDelta(e.Delta); }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Application.RemoveMessageFilter(this); _layer?.Dispose(); }
                base.Dispose(disposing);
            }
        }
        private Bitmap? _queueGlassCache;
        private Rectangle _queueGlassBounds;
        private string? _queueGlassArtworkPath;
        private string? _glassArtworkPath;
        internal string? GlassArtworkPath
        {
            get => _glassArtworkPath;
            set { if (_glassArtworkPath == value) return; _glassArtworkPath=value; _queueGlassCache?.Dispose(); _queueGlassCache=null; Invalidate(); }
        }
        private void DrawQueueEditor(Graphics g, Rectangle bounds)
        {
            bounds.Height = Math.Max(1, bounds.Height - MusicTransportInset);
            float scale = Math.Clamp(Math.Min(bounds.Width / 1440f, bounds.Height / 950f), .86f, 1.15f);
            _hits.Add(new HitZone { Bounds = bounds, Kind = HitKind.QueueClose, Key = "queue-outside" });
            var snapshot = (QueueSnapshotResolver?.Invoke() ?? Array.Empty<PlaybackQueueViewItem>()).ToList();
            var modal = QueueDrawerBounds();
            using (var shape = Round(modal, 13))
            {
                var state = g.Save(); g.SetClip(shape, CombineMode.Intersect);
                if (_queueBackdrop != null)
                {
                    if (_queueGlassCache == null || _queueGlassBounds != modal ||
                        !string.Equals(_queueGlassArtworkPath, _glassArtworkPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _queueGlassCache?.Dispose();
                        var artwork = string.IsNullOrEmpty(_glassArtworkPath) ? null : LoadImageForDisplay(_glassArtworkPath,128);
                        _queueGlassCache = GlassSurface.Create(_queueBackdrop, modal, artwork);
                        _queueGlassBounds = modal;
                        _queueGlassArtworkPath = _glassArtworkPath;
                    }
                    g.DrawImageUnscaled(_queueGlassCache, modal.Location);
                }
                g.Restore(state);
            }
            _hits.Add(new HitZone { Bounds = modal, Kind = HitKind.None, Key = "queue-panel" });
            int pad = S(scale, 24);
            var close = new Rectangle(modal.Right-pad-S(scale,40),modal.Top+S(scale,16),S(scale,40),S(scale,40));
            DrawModalClose(g, close, HitKind.QueueClose, scale);
            using var title = LibraryFont("Segoe UI Semibold", 13f*scale);
            TextRenderer.DrawText(g,L("Coda di riproduzione","Playback queue"),title,new Rectangle(modal.Left+pad,modal.Top+20,modal.Width-pad*2-32,34),TextMain,MusicText);
            _queueListRect = new Rectangle(modal.Left+pad,modal.Top+80,modal.Width-pad*2,modal.Height-160);
            if (snapshot.Count > 0)
            {
                var clear = new Rectangle(modal.Left + pad, modal.Bottom - S(scale, 64), modal.Width - pad * 2, S(scale, 40));
                DrawQueueFooterButton(g, clear, L("Svuota tutto", "Clear all"), "", TextMain, false, scale);
                _hits.Add(new HitZone { Bounds = clear, Kind = HitKind.QueueClear });
            }
            if(snapshot.Count==0) DrawQueueEmptyState(g,_queueListRect,scale);
            else DrawQueueRows(g,_queueListRect,snapshot,scale);
        }

        private void DrawQueueToast(Graphics g, Rectangle bounds)
        {
            if (string.IsNullOrWhiteSpace(_queueToastText) || DateTime.UtcNow >= _queueToastUntilUtc)
                return;

            float scale = Math.Max(0.9f, Math.Min(1.18f, Math.Min(bounds.Width / 1600f, bounds.Height / 900f)));
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(9.6f, 10.8f * scale));
            Size text = TextRenderer.MeasureText(_queueToastText, font, new Size(Math.Max(240, bounds.Width / 2), 80), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            int padX = S(scale, 18);
            int iconSide = S(scale, 22);
            int w = Math.Min(bounds.Width - S(scale, 44), Math.Max(S(scale, 260), text.Width + padX * 2 + iconSide + S(scale, 12)));
            int h = S(scale, 50);
            Rectangle r = new(bounds.Right - w - S(scale, 28), bounds.Bottom - h - S(scale, 30), w, h);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(r, S(scale, 9)))
            using (var fill = new LinearGradientBrush(r, Color.FromArgb(238, 7, 23, 38), Color.FromArgb(238, 2, 10, 18), LinearGradientMode.Vertical))
            using (var border = new Pen(Color.FromArgb(130, Accent), 1.2f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            Rectangle icon = new(r.Left + padX, r.Top + (r.Height - iconSide) / 2, iconSide, iconSide);
            using (var pen = new Pen(Color.FromArgb(210, Accent), Math.Max(2f, 2.2f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[]
                {
                    new Point(icon.Left + S(scale, 3), icon.Top + icon.Height / 2),
                    new Point(icon.Left + icon.Width / 2 - S(scale, 1), icon.Bottom - S(scale, 4)),
                    new Point(icon.Right - S(scale, 3), icon.Top + S(scale, 4))
                });
            }

            Rectangle textRect = new(icon.Right + S(scale, 12), r.Top, r.Right - icon.Right - S(scale, 22), r.Height);
            TextRenderer.DrawText(g, _queueToastText, font, textRect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawQueueRows(Graphics g, Rectangle viewport, IReadOnlyList<PlaybackQueueViewItem> snapshot, float scale)
        {
            int rowH=S(scale,64), headingH=S(scale,32);
            int maxVisible=Math.Max(1,(viewport.Height-headingH*3)/rowH);
            _queueEditorVisibleRows=maxVisible;
            _queueEditorScrollMax=Math.Max(0,snapshot.Count-maxVisible);
            _queueEditorScroll=Math.Clamp(_queueEditorScroll,0,_queueEditorScrollMax);
            int current = snapshot.ToList().FindIndex(t=>t.IsCurrent);
            int y=viewport.Top;
            string lastSection="";
            using var heading = LibraryFont("Segoe UI Semibold",10f*scale);
            var state=g.Save(); g.SetClip(viewport,CombineMode.Intersect);
            for(int index=_queueEditorScroll;index<Math.Min(snapshot.Count,_queueEditorScroll+maxVisible);index++)
            {
                string section=index<current?L("Cronologia","History"):index==current?L("In riproduzione","Now playing"):L("In arrivo","Up next");
                if(section!=lastSection)
                {
                    TextRenderer.DrawText(g,section,heading,new Rectangle(viewport.Left,y,viewport.Width,headingH),Color.FromArgb(215,215,215),MusicText);
                    y+=headingH;lastSection=section;
                }
                var row=new Rectangle(viewport.Left,y,viewport.Width-S(scale,16),rowH);
                DrawQueueRow(g,row,snapshot[index],index,index==_queueEditorSelectionIndex,scale);
                if(_queueDragging&&_queueDropIndex==index) { using var line=new Pen(Accent,2);g.DrawLine(line,row.Left,row.Top,row.Right,row.Top); }
                y+=rowH;
            }
            g.Restore(state);
            DrawQueueScrollbar(g,new Rectangle(viewport.Right-S(scale,3),viewport.Top,Math.Max(3,S(scale,3)),viewport.Height),snapshot.Count,maxVisible,scale);
        }

        private void DrawQueueRow(Graphics g, Rectangle r, PlaybackQueueViewItem item, int visualIndex, bool selected, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            if(selected || hover)
            {
                using var path=Round(r,S(scale,7));
                using var fill = new SolidBrush(Color.FromArgb(selected ? 32 : 18, selected ? Accent : Theme.Text));
                g.FillPath(fill, path);
            }
            _hits.Add(new HitZone { Bounds=r,Kind=HitKind.QueuePlay,Key=item.Path,Index=visualIndex });
            int cover=S(scale,44);
            var art=new Rectangle(r.Left+S(scale,8),r.Top+(r.Height-cover)/2,cover,cover);
            DrawQueueArtwork(g,art,item.Path,item.IsCurrent,scale);
            var libraryItem=_items.SelectMany(t=>t.IsGroup?t.Children:new List<LibraryItem>{t}).FirstOrDefault(t=>string.Equals(t.Path,item.Path,StringComparison.OrdinalIgnoreCase));
            string name=FirstNonEmpty(libraryItem?.Title,item.Label,Path.GetFileNameWithoutExtension(item.Path));
            string artist=libraryItem?.ArtistName ?? (item.IsCurrent?L("In riproduzione","Now playing"):"");
            int x=art.Right+S(scale,12), width=Math.Max(30,r.Right-x-S(scale,40));
            using var title=LibraryFont("Segoe UI Semibold",10f*scale);
            using var sub=LibraryFont("Segoe UI",9f*scale);
            TextRenderer.DrawText(g,name,title,new Rectangle(x,r.Top+S(scale,9),width,S(scale,23)),TextMain,MusicText);
            TextRenderer.DrawText(g,artist,sub,new Rectangle(x,r.Top+S(scale,32),width,S(scale,21)),item.IsCurrent?Accent:Muted,MusicText);
            var remove=new Rectangle(r.Right-26,r.Top+(r.Height-26)/2,26,26);
            HUD.MusicTransportBar.DrawSymbol(g,Rectangle.Inflate(remove,-6,-6),"close",Muted);
            _hits.Add(new HitZone { Bounds=remove,Kind=HitKind.QueueRemove,Key=item.Path,Index=visualIndex });
        }

        private void DrawQueueScrollbar(Graphics g, Rectangle track, int total, int visible, float scale)
        {
            if (total <= visible || track.Height <= S(scale, 24))
                return;

            double ratio = visible / (double)Math.Max(1, total);
            int thumbH = Math.Max(S(scale, 34), (int)Math.Round(track.Height * ratio));
            int maxScroll = Math.Max(1, total - visible);
            int travel = Math.Max(1, track.Height - thumbH);
            int thumbY = track.Top + (int)Math.Round(travel * (_queueEditorScroll / (double)maxScroll));
            ScrollbarChrome.Draw(g, track, thumbY, thumbH);
        }

        private void DrawQueueDetail(Graphics g, Rectangle r, PlaybackQueueViewItem item, float scale)
        {
            int pad = S(scale, 12);
            var libraryItem = ToItem(item.Path);
            int artW = Math.Min(r.Width - pad * 2, S(scale, 270));
            bool posterShape = libraryItem != null && !string.Equals(libraryItem.Category, "Music", StringComparison.OrdinalIgnoreCase);
            int artH = posterShape ? (int)Math.Round(artW * 1.42) : artW;
            artH = Math.Min(artH, S(scale, 360));
            Rectangle art = new Rectangle(r.Left + pad, r.Top, artW, artH);
            bool drawn = libraryItem != null && DrawPosterArt(g, art, libraryItem);
            if (!drawn)
                DrawQueueArtwork(g, art, item.Path, true, scale);
            using var titleFont = LibraryFont("Segoe UI Semibold", Math.Max(14f, 18f * scale));
            using var metaFont = LibraryFont("Segoe UI", Math.Max(9f, 10f * scale));
            int y = art.Bottom + S(scale, 24);
            TextRenderer.DrawText(g, FirstNonEmpty(item.Label, Path.GetFileNameWithoutExtension(item.Path), item.Path), titleFont,
                new Rectangle(r.Left + pad, y, r.Width - pad * 2, S(scale, 84)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += S(scale, 102);
            string category = ResolvePlaybackCategoryForPath(item.Path);
            DrawQueueInfoLine(g, r.Left + pad, ref y, r.Width - pad * 2, "time", L("Durata", "Duration"), libraryItem != null ? DurationText(libraryItem) : "-", scale);
            DrawQueueInfoLine(g, r.Left + pad, ref y, r.Width - pad * 2, "audio", "Audio", FirstRawNonEmpty(libraryItem?.AudioLabel, "-"), scale);
            DrawQueueInfoLine(g, r.Left + pad, ref y, r.Width - pad * 2, "folder", L("Categoria", "Category"), category, scale);

            Rectangle play = new Rectangle(r.Left + pad, r.Bottom - pad - S(scale, 48), r.Width - pad * 2, S(scale, 48));
            DrawQueueFooterButton(g, play, L("Riproduci ora", "Play now"), "player-play", Color.White, danger: false, scale);
            _hits.Add(new HitZone { Bounds = play, Kind = HitKind.QueuePlay, Key = item.Path });
        }

        private void DrawQueueEmptyState(Graphics g, Rectangle r, float scale)
        {
            DrawMinimalEmptyState(g, r, "queue", L("La coda è vuota", "Queue is empty"),
                L("Aggiungi un brano, un film o un episodio alla coda.", "Add a track, movie or episode to the queue."));
        }

        private void DrawQueueDetailEmptyState(Graphics g, Rectangle r, float scale)
        {
            using (var path = Round(r, S(scale, 8)))
            using (var fill = new LinearGradientBrush(r, Color.FromArgb(42, 8, 18, 29), Color.FromArgb(22, 3, 9, 16), LinearGradientMode.Vertical))
            using (var border = new Pen(Color.FromArgb(24, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            DrawIcon(g, new Rectangle(r.Left + (r.Width - S(scale, 54)) / 2, r.Top + S(scale, 84), S(scale, 54), S(scale, 54)), "music", Color.FromArgb(112, 140, 178));
            using var font = LibraryFont("Segoe UI", Math.Max(8.8f, 9.8f * scale));
            TextRenderer.DrawText(g, L("Seleziona un elemento per vedere i dettagli.", "Select an item to see details."), font,
                new Rectangle(r.Left + S(scale, 28), r.Top + S(scale, 154), r.Width - S(scale, 56), S(scale, 56)),
                Color.FromArgb(156, 172, 190), TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawQueueDragHandle(Graphics g, Rectangle r, bool selected, float scale)
        {
            Color c = selected ? Color.FromArgb(100, 150, 210, 255) : Color.FromArgb(92, 120, 148, 180);
            using var brush = new SolidBrush(c);
            int dot = Math.Max(2, S(scale, 3));
            int gap = Math.Max(5, S(scale, 7));
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 2; col++)
                {
                    int x = r.Left + col * gap;
                    int y = r.Top + row * gap;
                    g.FillEllipse(brush, x, y, dot, dot);
                }
            }
        }

        private void DrawQueueMoreButton(Graphics g, Rectangle r, float scale)
        {
            using (var path = Round(r, S(scale, 8)))
            using (var fill = new SolidBrush(r.Contains(_lastMouse) ? Color.FromArgb(76, 18, 40, 62) : Color.FromArgb(52, 12, 28, 44)))
                g.FillPath(fill, path);

            using var brush = new SolidBrush(Color.White);
            int dot = Math.Max(3, S(scale, 4));
            int y = r.Top + (r.Height - dot) / 2;
            int start = r.Left + (r.Width - dot * 3 - S(scale, 10) * 2) / 2;
            for (int i = 0; i < 3; i++)
                g.FillEllipse(brush, start + i * (dot + S(scale, 10)), y, dot, dot);
        }

        private void DrawQueueArtwork(Graphics g, Rectangle r, string path, bool selected, float scale)
        {
            using var clip = Round(r, S(scale, 7));
            using (var fill = new SolidBrush(Color.FromArgb(selected ? 72 : 52, 10, 23, 38)))
                g.FillPath(fill, clip);
            using Region old = g.Clip.Clone();
            g.SetClip(clip, CombineMode.Intersect);
            var item = _items.SelectMany(t => t.IsGroup ? t.Children : new List<LibraryItem> { t })
                .FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))
                ?? ToItem(path, allowMediaFileIo: false);
            bool drawn = item != null && DrawImagePath(g, r, item.ArtPath ?? item.WideArtPath, requireLandscape: false, tint: false, preserveAspectWhenWide: false, verticalFocus: 0.5f);
            if (!drawn)
                DrawIcon(g, new Rectangle(r.Left + r.Width / 4, r.Top + r.Height / 4, r.Width / 2, r.Height / 2), "music", Color.FromArgb(144, 168, 205));
            g.Clip = old;
        }

        private void DrawQueueMiniButton(Graphics g, Rectangle r, string icon, HitKind kind, string key, float scale)
        {
            using (var path = Round(r, S(scale, 7)))
            using (var fill = new SolidBrush(r.Contains(_lastMouse) ? Color.FromArgb(82, 16, 38, 58) : Color.FromArgb(58, 12, 28, 44)))
            using (var border = new Pen(Color.FromArgb(36, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            Rectangle glyph = new Rectangle(r.Left + S(scale, 8), r.Top + S(scale, 8), r.Width - S(scale, 16), r.Height - S(scale, 16));
            if (!DrawIcon(g, glyph, icon, Color.White))
                DrawQueueActionGlyph(g, glyph, icon, Color.White, scale);
            _hits.Add(new HitZone { Bounds = r, Kind = kind, Key = key });
        }

        private void DrawQueueFooterButton(Graphics g, Rectangle r, string text, string icon, Color color, bool danger, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            if (hover)
            {
                using var path = Round(r, S(scale, 7));
                using var fill = new SolidBrush(Color.FromArgb(24, danger ? Color.IndianRed : Accent));
                g.FillPath(fill, path);
            }
            bool hasIcon = !string.IsNullOrEmpty(icon);
            Rectangle iconRect = new Rectangle(r.Left + S(scale, 16), r.Top + (r.Height - S(scale, 20)) / 2, S(scale, 20), S(scale, 20));
            if (hasIcon && !DrawIcon(g, iconRect, icon, color)) DrawQueueActionGlyph(g, iconRect, icon, color, scale);
            using var font = LibraryFont("Segoe UI Semibold", Math.Max(9.2f, 10.5f * scale));
            int inset = S(scale, hasIcon ? 48 : 16);
            TextRenderer.DrawText(g, text, font, new Rectangle(r.Left + inset, r.Top, r.Width - inset - S(scale, 16), r.Height),
                color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private static void DrawQueueActionGlyph(Graphics g, Rectangle r, string icon, Color color, float scale)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, Math.Max(1.7f, 2.0f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var brush = new SolidBrush(color);
            string key = (icon ?? string.Empty).Trim().ToLowerInvariant();
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            if (key.Contains("up"))
            {
                g.DrawLine(pen, cx, r.Bottom - 4, cx, r.Top + 5);
                g.DrawLines(pen, new[] { new Point(r.Left + 5, r.Top + 10), new Point(cx, r.Top + 4), new Point(r.Right - 5, r.Top + 10) });
                return;
            }
            if (key.Contains("down"))
            {
                g.DrawLine(pen, cx, r.Top + 4, cx, r.Bottom - 5);
                g.DrawLines(pen, new[] { new Point(r.Left + 5, r.Bottom - 10), new Point(cx, r.Bottom - 4), new Point(r.Right - 5, r.Bottom - 10) });
                return;
            }
            if (key.Contains("trash") || key.Contains("remove"))
            {
                g.DrawLine(pen, r.Left + 5, r.Top + 7, r.Right - 5, r.Top + 7);
                g.DrawRectangle(pen, new Rectangle(r.Left + 7, r.Top + 10, r.Width - 14, r.Height - 14));
                g.DrawLine(pen, cx - 4, r.Top + 13, cx - 4, r.Bottom - 5);
                g.DrawLine(pen, cx + 4, r.Top + 13, cx + 4, r.Bottom - 5);
                return;
            }
            if (key.Contains("close") || key.Contains("x"))
            {
                int m = Math.Max(4, r.Width / 5);
                g.DrawLine(pen, r.Left + m, r.Top + m, r.Right - m, r.Bottom - m);
                g.DrawLine(pen, r.Right - m, r.Top + m, r.Left + m, r.Bottom - m);
                return;
            }
            if (key.Contains("play"))
            {
                Point[] pts = { new Point(r.Left + 7, r.Top + 4), new Point(r.Left + 7, r.Bottom - 4), new Point(r.Right - 4, cy) };
                g.FillPolygon(brush, pts);
                return;
            }
            g.FillEllipse(brush, Rectangle.Inflate(r, -r.Width / 3, -r.Height / 3));
        }

        private static void DrawLibraryFallbackGlyph(Graphics g, Rectangle r, string icon, Color color, float scale)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, Math.Max(1.6f, 1.8f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var brush = new SolidBrush(color);
            string key = (icon ?? string.Empty).Trim().ToLowerInvariant();
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;

            if (key is "time" or "duration" or "clock")
            {
                int radius = Math.Max(3, Math.Min(r.Width, r.Height) / 2 - 2);
                g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
                g.DrawLine(pen, cx, cy, cx, cy - radius + 3);
                g.DrawLine(pen, cx, cy, cx + radius - 3, cy);
                return;
            }

            if (key is "movie" or "info")
            {
                g.DrawRectangle(pen, new Rectangle(r.Left + 2, r.Top + 3, r.Width - 4, r.Height - 6));
                g.DrawLine(pen, r.Left + 5, r.Top + 6, r.Right - 5, r.Top + 6);
                g.FillEllipse(brush, cx - 1, cy - 1, 2, 2);
                return;
            }

            if (key is "folder")
            {
                g.DrawLines(pen, new[]
                {
                    new Point(r.Left + 2, r.Top + 7),
                    new Point(r.Left + 8, r.Top + 7),
                    new Point(r.Left + 11, r.Top + 10),
                    new Point(r.Right - 2, r.Top + 10),
                    new Point(r.Right - 2, r.Bottom - 3),
                    new Point(r.Left + 2, r.Bottom - 3),
                    new Point(r.Left + 2, r.Top + 7)
                });
                return;
            }

            if (key is "person" or "cast" or "profile")
            {
                int head = Math.Max(4, Math.Min(r.Width, r.Height) / 3);
                g.DrawEllipse(pen, cx - head / 2, r.Top + 2, head, head);
                int shoulderTop = r.Top + head + Math.Max(3, r.Height / 7);
                g.DrawArc(pen, new Rectangle(r.Left + 2, shoulderTop, r.Width - 4, Math.Max(5, r.Bottom - shoulderTop + 4)), 190, 160);
                return;
            }

            if (key is "add" or "plus")
            {
                g.DrawLine(pen, r.Left + 3, cy, r.Right - 3, cy);
                g.DrawLine(pen, cx, r.Top + 3, cx, r.Bottom - 3);
                return;
            }

            if (key is "playlist" or "queue")
            {
                for (int row = -1; row <= 1; row++)
                {
                    int y = cy + row * Math.Max(4, r.Height / 4);
                    g.DrawLine(pen, r.Left + 2, y, r.Right - 7, y);
                }
                Point[] arrow =
                {
                    new(r.Right - 6, cy - 4),
                    new(r.Right - 6, cy + 4),
                    new(r.Right - 1, cy)
                };
                g.FillPolygon(brush, arrow);
                return;
            }

            if (key is "star" or "favorite" or "favourite")
            {
                var points = new PointF[10];
                double step = Math.PI / 5.0;
                double start = -Math.PI / 2.0;
                float outer = Math.Max(3, Math.Min(r.Width, r.Height) / 2f - 1f);
                float inner = outer * 0.46f;
                for (int i = 0; i < points.Length; i++)
                {
                    float radius = (i & 1) == 0 ? outer : inner;
                    points[i] = new PointF(cx + (float)Math.Cos(start + i * step) * radius, cy + (float)Math.Sin(start + i * step) * radius);
                }
                g.DrawPolygon(pen, points);
                return;
            }

            if (key is "calendar")
            {
                Rectangle box = new(r.Left + 2, r.Top + 4, r.Width - 4, r.Height - 6);
                g.DrawRectangle(pen, box);
                g.DrawLine(pen, box.Left, box.Top + Math.Max(4, box.Height / 3), box.Right, box.Top + Math.Max(4, box.Height / 3));
                g.DrawLine(pen, box.Left + 4, box.Top - 2, box.Left + 4, box.Top + 3);
                g.DrawLine(pen, box.Right - 4, box.Top - 2, box.Right - 4, box.Top + 3);
                return;
            }

            g.FillEllipse(brush, Rectangle.Inflate(r, -r.Width / 3, -r.Height / 3));
        }

        private void DrawQueueInfoLine(Graphics g, int x, ref int y, int width, string icon, string label, string value, float scale)
        {
            Rectangle iconRect = new Rectangle(x, y, S(scale, 22), S(scale, 22));
            if (!DrawIcon(g, iconRect, icon, Color.FromArgb(158, 184, 218)))
                DrawLibraryFallbackGlyph(g, iconRect, icon, Color.FromArgb(158, 184, 218), scale);
            using var font = LibraryFont("Segoe UI", Math.Max(8.8f, 9.8f * scale));
            TextRenderer.DrawText(g, label, font, new Rectangle(x + S(scale, 34), y - S(scale, 2), S(scale, 110), S(scale, 26)),
                Color.FromArgb(156, 172, 190), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, value, font, new Rectangle(x + S(scale, 150), y - S(scale, 2), width - S(scale, 150), S(scale, 26)),
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += S(scale, 42);
        }

        private void ShowCreatePlaylistOverlay(string? initialItemPath = null)
        {
            _playlistCreatePendingAlbum = null;
            EnsurePlaylistCreateOverlay();
            if (_playlistCreateOverlay == null)
                return;

            _playlistCreateCoverPath = null;
            _playlistCreatePendingItemPath = string.IsNullOrWhiteSpace(initialItemPath) ? null : initialItemPath;
            if (_playlistCreateNameBox != null)
                _playlistCreateNameBox.Text = string.Empty;
            if (_playlistCreateDescriptionBox != null)
                _playlistCreateDescriptionBox.Text = string.Empty;
            string initialBucket = PlaylistBucketForItem(
                string.IsNullOrWhiteSpace(initialItemPath) ? null : ToItem(initialItemPath));
            if (_category == "Music" && string.IsNullOrWhiteSpace(initialItemPath)) initialBucket = "Music";
            _playlistCreateBucketValue = initialBucket;
            UpdatePlaylistCreateCoverPreview(null);

            CapturePlaylistLibraryBackdrop();
            _playlistCreateOverlay.Visible = true;
            _playlistCreateOverlay.BringToFront();
            LayoutPlaylistCreateOverlay();
            AnimatePanelEntrance(_playlistCreateCard);
            try { _playlistCreateNameBox?.Focus(); } catch { }
        }

        private void HideCreatePlaylistOverlay()
        {
            if (_playlistCreateOverlay == null)
                return;

            _playlistCreateOverlay.Visible = false;
            var backdrop = _playlistCreateOverlay.BackgroundImage;
            _playlistCreateOverlay.BackgroundImage = null;
            backdrop?.Dispose();
            _playlistCreateCoverPath = null;
            _playlistCreatePendingItemPath = null;
            _playlistCreatePendingAlbum = null;
            UpdatePlaylistCreateCoverPreview(null);
            try { Focus(); } catch { }
        }

        private void CapturePlaylistLibraryBackdrop()
        {
            if (_playlistCreateOverlay == null || Width < 1 || Height < 1) return;
            var snapshot = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(snapshot)) PaintLibrary(g);
            var old = _playlistCreateOverlay.BackgroundImage;
            _playlistCreateOverlay.BackgroundImage = snapshot;
            old?.Dispose();
        }

        private void EnsurePlaylistCreateOverlay()
        {
            if (_playlistCreateOverlay != null)
                return;

            var overlay = new BufferedPanel
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BackColor = Color.Transparent,
                TabStop = true
            };
            overlay.BackgroundImageLayout = ImageLayout.Stretch;
            overlay.Paint += (_, e) => { using var dim = new SolidBrush(Color.FromArgb(80, 0, 0, 0)); e.Graphics.FillRectangle(dim, overlay.ClientRectangle); };
            overlay.Resize += (_, _) =>
            {
                if (overlay.Visible) CapturePlaylistLibraryBackdrop();
                LayoutPlaylistCreateOverlay();
            };
            // The modal owns input; scrolling must not reach the library underneath.

            var card = new BufferedPanel
            {
                BackColor = Color.FromArgb(10, 18, 26),
                TabStop = true
            };
            card.Paint += (_, e) => PaintPlaylistCreateCard(e.Graphics, card.ClientRectangle);
            card.MouseDown += (_, e) =>
            {
                    if (_playlistCreateCoverShell.Contains(e.Location))
                    ChoosePlaylistCreateCover();
            };
            overlay.Controls.Add(card);

            var title = OverlayLabel(L("Crea playlist", "Create playlist"), 27f, FontStyle.Bold, Color.White);
            title.Name = "title";
            card.Controls.Add(title);

            var close = OverlayButton(string.Empty, primary: false, icon: "close");
            close.Name = "close";
            close.TabStop = false;
            close.Click += (_, __) => HideCreatePlaylistOverlay();
            card.Controls.Add(close);

            var nameLabel = OverlayLabel(L("Nome", "Name"), 12f, FontStyle.Regular, Color.FromArgb(168, 182, 198));
            nameLabel.Name = "nameLabel";
            card.Controls.Add(nameLabel);

            _playlistCreateNameBox = OverlayTextBox(L("Inserisci il nome della playlist...", "Enter playlist name..."));
            _playlistCreateNameBox.GotFocus += (_, __) => card.Invalidate();
            _playlistCreateNameBox.LostFocus += (_, __) => card.Invalidate();
            card.Controls.Add(_playlistCreateNameBox);

            var descriptionLabel = OverlayLabel(L("Descrizione · facoltativa", "Description · optional"), 12f, FontStyle.Regular, Color.FromArgb(168, 182, 198));
            descriptionLabel.Name = "descriptionLabel";
            card.Controls.Add(descriptionLabel);

            _playlistCreateDescriptionBox = OverlayTextBox(L("Aggiungi una descrizione (facoltativa)...", "Add a description (optional)..."));
            _playlistCreateDescriptionBox.Multiline = true;
            _playlistCreateDescriptionBox.ScrollBars = ScrollBars.None;
            _playlistCreateDescriptionBox.GotFocus += (_, __) => card.Invalidate();
            _playlistCreateDescriptionBox.LostFocus += (_, __) => card.Invalidate();
            card.Controls.Add(_playlistCreateDescriptionBox);

            var categoryLabel = OverlayLabel(L("Tipo", "Type"), 12f, FontStyle.Regular, Color.FromArgb(168, 182, 198));
            categoryLabel.Name = "categoryLabel";
            card.Controls.Add(categoryLabel);

            var typeSelector = new PlaylistTypeSelector { Name = "typeSelector", BackColor = card.BackColor, AccessibleName = L("Tipo di playlist", "Playlist type") };
            typeSelector.Changed += direction =>
            {
                string[] types = { "Movies", "TV", "Videos", "Music", "Photos", "Mixed" };
                int index = Array.FindIndex(types, value => string.Equals(value, _playlistCreateBucketValue, StringComparison.OrdinalIgnoreCase));
                _playlistCreateBucketValue = types[(Math.Max(0, index) + direction + types.Length) % types.Length];
                typeSelector.Text = PlaylistBucketLabel(_playlistCreateBucketValue);
            };
            card.Controls.Add(typeSelector);

            _playlistCreateCoverPreview = new PictureBox
            {
                BackColor = Color.FromArgb(5, 13, 22),
                SizeMode = PictureBoxSizeMode.Zoom,
                TabStop = false,
                Visible = true,
                Cursor = Cursors.Hand
            };
            _playlistCreateCoverPreview.Click += (_, __) => ChoosePlaylistCreateCover();
            card.Controls.Add(_playlistCreateCoverPreview);

            _playlistCreateCoverHint = OverlayLabel(string.Empty, 10.0f, FontStyle.Regular, Color.FromArgb(154, 170, 186));
            _playlistCreateCoverHint.TextAlign = ContentAlignment.MiddleCenter;
            _playlistCreateCoverHint.Visible = false;
            _playlistCreateCoverHint.Cursor = Cursors.Hand;
            _playlistCreateCoverHint.Click += (_, __) => ChoosePlaylistCreateCover();
            card.Controls.Add(_playlistCreateCoverHint);

            var cover = OverlayButton(L("Scegli cover", "Choose cover"), primary: false, icon: "image-add");
            cover.Name = "cover";
            cover.Click += (_, __) => ChoosePlaylistCreateCover();
            cover.Visible = false;
            card.Controls.Add(cover);

            var create = OverlayButton(L("Crea playlist", "Create playlist"), primary: true, icon: "plus");
            create.Name = "create";
            create.Click += (_, __) => CommitCreatePlaylistOverlay();
            card.Controls.Add(create);
            var cancel = OverlayButton(L("Annulla", "Cancel"), primary: false);
            cancel.Name = "cancel"; cancel.Click += (_, __) => HideCreatePlaylistOverlay();
            card.Controls.Add(cancel);
            _playlistCreateNameBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape) { HideCreatePlaylistOverlay(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter) { CommitCreatePlaylistOverlay(); e.SuppressKeyPress = true; }
            };
            _playlistCreateDescriptionBox.KeyDown += (_, e) =>
            { if (e.KeyCode == Keys.Escape) { HideCreatePlaylistOverlay(); e.SuppressKeyPress = true; } };

            _playlistCreateOverlay = overlay;
            _playlistCreateCard = card;
            Controls.Add(overlay);
            overlay.BringToFront();
        }

        private sealed class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            }
        }

        private static Label OverlayLabel(string text, float size, FontStyle style, Color color) => new()
        {
            Text = text,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = color,
            Font = LibraryFont("Segoe UI", size, style),
            TextAlign = ContentAlignment.MiddleLeft
        };

        private TextBox OverlayTextBox(string placeholder) => new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(10, 18, 26),
            ForeColor = Color.White,
            Font = LibraryFont("Segoe UI", 12.2f),
            PlaceholderText = placeholder,
            TabStop = true
        };

        private PlaylistOverlayButton OverlayButton(string text, bool primary, string? icon = null)
        {
            var button = new PlaylistOverlayButton(this, primary, icon)
            {
                Text = text,
                BackColor = Color.Transparent,
                ForeColor = Color.White,
                Font = LibraryFont("Segoe UI Semibold", 10.2f),
                TabStop = true
            };
            return button;
        }

        private sealed class PlaylistOverlayButton : Control
        {
            private readonly CinematicMediaLibraryPage _owner;
            private readonly bool _primary;
            private readonly string _icon;
            protected override bool ShowFocusCues => false;

            public PlaylistOverlayButton(CinematicMediaLibraryPage owner, bool primary, string? icon)
            {
                _owner = owner;
                _primary = primary;
                _icon = icon ?? string.Empty;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
                if (string.Equals(_icon, "close", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Text))
                {
                    SetStyle(ControlStyles.Selectable, false);
                    TabStop = false;
                }
            }

            protected override void OnPaintBackground(PaintEventArgs pevent)
            {
                if (Parent == null)
                    return;

                var state = pevent.Graphics.Save();
                try
                {
                    pevent.Graphics.TranslateTransform(-Left, -Top);
                    using var parentArgs = new PaintEventArgs(pevent.Graphics, new Rectangle(Left, Top, Width, Height));
                    InvokePaintBackground(Parent, parentArgs);
                    InvokePaint(Parent, parentArgs);
                }
                catch
                {
                }
                finally
                {
                    pevent.Graphics.Restore(state);
                }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Invalidate();
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                Invalidate();
                base.OnMouseUp(e);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { _owner.HideCreatePlaylistOverlay(); e.Handled = true; }
                base.OnKeyDown(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
                bool iconOnly = !string.IsNullOrWhiteSpace(_icon) && string.IsNullOrWhiteSpace(Text);

                if (iconOnly && string.Equals(_icon, "close", StringComparison.OrdinalIgnoreCase))
                {
                    Rectangle box = Rectangle.Inflate(r, -1, -1);
                    int iconSide = Math.Max(18, Math.Min(24, Math.Min(Width, Height) - 18));
                    var iconOnlyRect = new Rectangle((Width - iconSide) / 2, (Height - iconSide) / 2, iconSide, iconSide);
                    DrawGlyph(g, iconOnlyRect, _icon, Color.White);
                    return;
                }

                if (_primary || hot)
                {
                    using var shape = RoundLocal(r, _primary ? r.Height / 2 : 8);
                    using var fill = new SolidBrush(_primary ? Accent : Color.FromArgb(20, 255, 255, 255));
                    g.FillPath(fill, shape);
                }

                if (iconOnly)
                {
                    int iconSide = Math.Max(18, Math.Min(24, Math.Min(Width, Height) - 24));
                    var iconOnlyRect = new Rectangle((Width - iconSide) / 2, (Height - iconSide) / 2, iconSide, iconSide);
                    if (!_owner.DrawIcon(g, iconOnlyRect, _icon, Color.White))
                        DrawGlyph(g, iconOnlyRect, _icon, Color.White);
                    return;
                }

                bool hasIcon = !string.IsNullOrWhiteSpace(_icon);
                bool hasText = !string.IsNullOrWhiteSpace(Text);
                int iconSize = hasIcon
                    ? (string.Equals(_icon, "close", StringComparison.OrdinalIgnoreCase) ? Math.Min(30, Height / 2) : Math.Min(24, Math.Max(18, Height / 3)))
                    : 0;
                int textW = hasText ? TextRenderer.MeasureText(Text, Font).Width : 0;
                int gap = hasIcon && hasText ? 12 : 0;
                int totalW = Math.Min(Width - 18, iconSize + gap + textW);
                int startX = Math.Max(9, (Width - totalW) / 2);
                Rectangle iconRect = hasIcon
                    ? new Rectangle(startX, (Height - iconSize) / 2, iconSize, iconSize)
                    : Rectangle.Empty;
                if (hasIcon && !_owner.DrawIcon(g, iconRect, _icon, _primary ? Color.White : Color.FromArgb(24, 150, 255)))
                    DrawGlyph(g, iconRect, _icon, _primary ? Color.White : Color.FromArgb(24, 150, 255));

                if (hasText)
                {
                    Rectangle textRect = hasIcon
                        ? new Rectangle(iconRect.Right + gap, 0, Math.Max(1, Width - iconRect.Right - gap - 12), Height)
                        : new Rectangle(12, 0, Math.Max(1, Width - 24), Height);
                    TextRenderer.DrawText(g, Text, Font, textRect, Color.White,
                        (hasIcon ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }

            private static GraphicsPath RoundLocal(Rectangle r, int radius)
            {
                int rad = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
                int d = rad * 2;
                var path = new GraphicsPath();
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            private static void DrawGlyph(Graphics g, Rectangle r, string icon, Color color)
            {
                using var pen = new Pen(color, Math.Max(2f, r.Width / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                using var brush = new SolidBrush(color);
                string key = icon.Trim().ToLowerInvariant();
                if (key == "close")
                {
                    g.DrawLine(pen, r.Left + 3, r.Top + 3, r.Right - 3, r.Bottom - 3);
                    g.DrawLine(pen, r.Right - 3, r.Top + 3, r.Left + 3, r.Bottom - 3);
                    return;
                }
                if (key == "back" || key == "arrow-back")
                {
                    int cy = r.Top + r.Height / 2;
                    g.DrawLine(pen, r.Right - 4, cy, r.Left + 5, cy);
                    g.DrawLines(pen, new[]
                    {
                        new Point(r.Left + 11, r.Top + 5),
                        new Point(r.Left + 4, cy),
                        new Point(r.Left + 11, r.Bottom - 5)
                    });
                    return;
                }
                if (key == "plus")
                {
                    g.DrawLine(pen, r.Left + r.Width / 2, r.Top + 3, r.Left + r.Width / 2, r.Bottom - 3);
                    g.DrawLine(pen, r.Left + 3, r.Top + r.Height / 2, r.Right - 3, r.Top + r.Height / 2);
                    return;
                }
                if (key == "image-add")
                {
                    Rectangle box = new Rectangle(r.Left + 1, r.Top + 4, r.Width - 6, r.Height - 7);
                    g.DrawRectangle(pen, box);
                    g.DrawLines(pen, new[]
                    {
                        new Point(box.Left + 4, box.Bottom - 4),
                        new Point(box.Left + box.Width / 2, box.Top + box.Height / 2),
                        new Point(box.Right - 4, box.Bottom - 5)
                    });
                    int cx = box.Right - 1;
                    int cy = box.Top + 2;
                    g.DrawLine(pen, cx, cy - 5, cx, cy + 5);
                    g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                }
            }
        }

        private sealed class PlaylistTypeSelector : Control
        {
            public event Action<int>? Changed;
            public PlaylistTypeSelector()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true; Cursor = Cursors.Hand; ForeColor = Color.White;
                AccessibleRole = AccessibleRole.SpinButton;
            }
            protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode is Keys.Left or Keys.Right or Keys.Space)
                { Changed?.Invoke(e.KeyCode == Keys.Left ? -1 : 1); e.Handled = true; Invalidate(); }
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                Focus(); Changed?.Invoke(e.X < Width / 2 ? -1 : 1); Invalidate();
            }
            protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int size = Math.Max(12, Height / 3), pad = Math.Max(8, Height / 4), cy = Height / 2;
                using var pen = new Pen(HUD.Theme.Accent, Math.Max(1.5f, Height / 25f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                e.Graphics.DrawLines(pen, new[] { new Point(pad + size / 2, cy - size / 2), new Point(pad, cy), new Point(pad + size / 2, cy + size / 2) });
                e.Graphics.DrawLines(pen, new[] { new Point(Width - pad - size / 2, cy - size / 2), new Point(Width - pad, cy), new Point(Width - pad - size / 2, cy + size / 2) });
                TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(pad + size, 0, Math.Max(1, Width - (pad + size) * 2), Height), ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                using var line = new Pen(Focused ? HUD.Theme.Accent : Color.FromArgb(62, 78, 91));
                e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
            }
        }

        private void LayoutPlaylistCreateOverlay()
        {
            var overlay = _playlistCreateOverlay; var card = _playlistCreateCard;
            if (overlay == null || card == null) return;
            card.SuspendLayout();
            float scale = Math.Clamp(Math.Min(overlay.Width / 1920f, overlay.Height / 1080f), .7f, 3f);
            int U(int value) => Math.Max(1, (int)Math.Round(value * scale));
            card.Size = new Size(U(1000), U(550));
            card.Location = new Point((overlay.Width - card.Width) / 2, (overlay.Height - card.Height) / 2);
            using (var shape = Round(card.ClientRectangle, U(22)))
            { var previous = card.Region; card.Region = new Region(shape); previous?.Dispose(); }
            int pad = U(40), coverW = U(260), gap = U(40);
            int leftW = card.Width - pad * 2 - coverW - gap;
            foreach (Control child in card.Controls)
            {
                float size = child.Name == "title" ? 23 : child is PlaylistTypeSelector ? 12f : 12;
                var old = child.Font;
                if (Math.Abs(old.SizeInPoints - size * scale) > .05f)
                {
                    child.Font = new Font("Segoe UI", size * scale * (96f / 72f), child.Name == "title" ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                    if (!ReferenceEquals(old, Control.DefaultFont)) old.Dispose();
                }
            }
            card.Controls["title"]!.Bounds = new Rectangle(pad, U(30), U(850), U(54));
            card.Controls["close"]!.Bounds = new Rectangle(card.Width - pad - U(38), U(36), U(38), U(38));
            card.Controls["nameLabel"]!.Bounds = new Rectangle(pad, U(108), leftW, U(26));
            _playlistCreateNameShell = new Rectangle(pad, U(140), leftW, U(48));
            if (_playlistCreateNameBox != null)
            { _playlistCreateNameBox.AutoSize = false; _playlistCreateNameBox.Bounds = new Rectangle(pad + U(8), U(152), leftW - 2 * U(8), _playlistCreateNameBox.Font.Height + 1); }
            card.Controls["descriptionLabel"]!.Bounds = new Rectangle(pad, U(224), leftW, U(26));
            _playlistCreateDescriptionShell = new Rectangle(pad, U(260), leftW, U(64));
            if (_playlistCreateDescriptionBox != null) _playlistCreateDescriptionBox.Bounds = Rectangle.Inflate(_playlistCreateDescriptionShell, -U(8), -U(8));
            card.Controls["categoryLabel"]!.Bounds = new Rectangle(pad, U(360), leftW, U(26));
            _playlistCreateBucketShell = Rectangle.Empty;
            var selector = card.Controls["typeSelector"]!;
            selector.Bounds = new Rectangle(pad, U(392), leftW, U(44));
            selector.Text = PlaylistBucketLabel(_playlistCreateBucketValue);
            _playlistCreateCoverShell = new Rectangle(card.Width - pad - coverW, U(108), coverW, coverW);
            if (_playlistCreateCoverPreview != null) _playlistCreateCoverPreview.Visible = false;
            if (_playlistCreateCoverHint != null) _playlistCreateCoverHint.Visible = false;
            card.Controls["cover"]!.Visible = false;
            card.Controls["create"]!.Bounds = new Rectangle(card.Width - pad - U(230), U(462), U(230), U(56));
            card.Controls["cancel"]!.Bounds = new Rectangle(card.Width - pad - U(350), U(462), U(108), U(56));
            card.ResumeLayout();
            card.Invalidate();
        }

        private void PaintPlaylistCreateCard(Graphics g, Rectangle bounds)
        {
            if (bounds.Width <= 2 || bounds.Height <= 2)
                return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(Color.FromArgb(10, 18, 26));
            g.FillRectangle(fill, bounds);

            DrawPlaylistCreateInputShell(g, _playlistCreateNameShell, focused: _playlistCreateNameBox?.Focused == true);


            DrawPlaylistCreateInputShell(g, _playlistCreateDescriptionShell, focused: _playlistCreateDescriptionBox?.Focused == true);
            DrawPlaylistCreateCoverShell(g, _playlistCreateCoverShell);
        }

        private void DrawPlaylistCreateInputShell(Graphics g, Rectangle r, bool focused)
        {
            if (r.Width <= 0 || r.Height <= 0)
                return;

            using var underline = new Pen(focused ? Accent : Color.FromArgb(62, 78, 91), focused ? 1.5f : 1f);
            g.DrawLine(underline, r.Left, r.Bottom, r.Right, r.Bottom);
            
        }

        private string PlaylistBucketLabel(string bucket) => bucket switch
        {
            "Movies" => L("Film", "Movies"),
            "TV" => L("Serie TV", "TV series"),
            "Videos" => L("Video", "Videos"),
            "Music" => L("Musica", "Music"),
            "Photos" => L("Foto", "Photos"),
            "Mixed" => L("Mista", "Mixed"),
            _ => bucket
        };

        private void DrawPlaylistCreateCoverShell(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0)
                return;

            if (!string.IsNullOrWhiteSpace(_playlistCreateCoverPath) && File.Exists(_playlistCreateCoverPath))
            {
                using Region old = g.Clip.Clone();
                using var imageClip = Round(r, 8);
                g.SetClip(imageClip, CombineMode.Intersect);
                DrawImagePath(g, r, _playlistCreateCoverPath, requireLandscape: false, tint: false, preserveAspectWhenWide: false, verticalFocus: 0.5f);
                g.Clip = old;
                using var border = new Pen(Color.FromArgb(120, 92, 132, 172));
                g.DrawPath(border, imageClip);
                return;
            }


            float scale = r.Width / 260f;
            int U(int value) => S(scale, value);
            var iconRect = new Rectangle(r.Left + (r.Width - U(78)) / 2, r.Top + U(60), U(78), U(78));
            DrawPlaylistCreateGlyph(g, iconRect, "image-add", Color.FromArgb(151, 173, 193));
            var plus = new Rectangle(iconRect.Right - U(22), iconRect.Bottom - U(24), U(30), U(30));
            using (var bg = new SolidBrush(Color.FromArgb(10, 18, 26))) g.FillEllipse(bg, plus);
            using (var pen = new Pen(Color.FromArgb(151, 173, 193), Math.Max(1, 2 * scale))) { g.DrawEllipse(pen, plus); g.DrawLine(pen, plus.Left + U(7), plus.Top + U(15), plus.Right - U(7), plus.Top + U(15)); g.DrawLine(pen, plus.Left + U(15), plus.Top + U(7), plus.Left + U(15), plus.Bottom - U(7)); }

            using var title = LibraryFont("Segoe UI", 12f * scale);
            using var meta = LibraryFont("Segoe UI", 10f * scale);
            Rectangle titleRect = new Rectangle(r.Left + U(18), r.Top, Math.Max(1, r.Width - U(36)), U(26));
            Rectangle metaRect = new Rectangle(r.Left + U(18), iconRect.Bottom + U(22), Math.Max(1, r.Width - U(36)), U(22));
            TextRenderer.DrawText(g, L("Aggiungi cover", "Add cover"), title, titleRect, Color.FromArgb(218, 230, 240),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, L("JPG, PNG o WEBP", "JPG, PNG or WEBP"), meta, metaRect, Color.FromArgb(136, 154, 174),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private static void DrawPlaylistCreateGlyph(Graphics g, Rectangle r, string icon, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, Math.Max(1.8f, r.Width / 18f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var brush = new SolidBrush(color);
            string key = (icon ?? string.Empty).Trim().ToLowerInvariant();
            if (key == "tag")
            {
                Point[] pts =
                {
                    new Point(r.Left + r.Width / 2, r.Top + 3),
                    new Point(r.Right - 3, r.Top + r.Height / 2),
                    new Point(r.Left + r.Width / 2, r.Bottom - 3),
                    new Point(r.Left + 3, r.Top + r.Height / 2)
                };
                g.DrawPolygon(pen, pts);
                g.FillEllipse(brush, r.Left + r.Width / 2 - 2, r.Top + r.Height / 2 - 2, 4, 4);
                return;
            }
            if (key == "edit")
            {
                g.DrawLine(pen, r.Left + 5, r.Bottom - 5, r.Right - 5, r.Top + 5);
                g.DrawLine(pen, r.Left + 5, r.Bottom - 5, r.Left + 3, r.Bottom - 2);
                g.DrawLine(pen, r.Left + 5, r.Bottom - 5, r.Left + 9, r.Bottom - 4);
                return;
            }

            Rectangle frame = Rectangle.Inflate(r, -Math.Max(4, r.Width / 8), -Math.Max(4, r.Height / 8));
            g.DrawRectangle(pen, frame);
            g.DrawEllipse(pen, frame.Left + frame.Width / 6, frame.Top + frame.Height / 6, Math.Max(4, frame.Width / 5), Math.Max(4, frame.Height / 5));
            g.DrawLines(pen, new[]
            {
                new Point(frame.Left + 4, frame.Bottom - 5),
                new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2),
                new Point(frame.Right - 4, frame.Bottom - 6)
            });
        }

        private void ChoosePlaylistCreateCover()
        {
            using var dlg = new OpenFileDialog
            {
                Title = L("Scegli copertina playlist", "Choose playlist cover"),
                Filter = L("Immagini|*.jpg;*.jpeg;*.png;*.webp;*.bmp|Tutti i file|*.*", "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All files|*.*"),
                CheckFileExists = true,
                Multiselect = false
            };

            var owner = FindForm();
            if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) != DialogResult.OK)
                return;

            _playlistCreateCoverPath = dlg.FileName;
            UpdatePlaylistCreateCoverPreview(_playlistCreateCoverPath);
        }

        private void UpdatePlaylistCreateCoverPreview(string? path)
        {
            if (_playlistCreateCoverPreview == null)
                return;

            try
            {
                var old = _playlistCreateCoverPreview.Image;
                _playlistCreateCoverPreview.Image = null;
                old?.Dispose();
            }
            catch { }

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    using var img = Image.FromFile(path);
                    _playlistCreateCoverPreview.Image = new Bitmap(img);
                }
            }
            catch { }

            bool hasCover = !string.IsNullOrWhiteSpace(path) && _playlistCreateCoverPreview.Image != null;
            _playlistCreateCoverPreview.Visible = false;
            if (_playlistCreateCoverHint != null)
            {
                _playlistCreateCoverHint.Visible = false;
                _playlistCreateCoverHint.Text = string.Empty;
            }
            _playlistCreateCard?.Invalidate();
        }

        private void CommitCreatePlaylistOverlay()
        {
            string name = (_playlistCreateNameBox?.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                try { _playlistCreateNameBox?.Focus(); } catch { }
                return;
            }

            string description = (_playlistCreateDescriptionBox?.Text ?? string.Empty).Trim();
            string bucket = FirstRawNonEmpty(_playlistCreateBucketValue, "Music").Trim();

            var model = LoadJson<PlaylistModel>(PlaylistsPath) ?? new PlaylistModel();
            string key = MakePlaylistKey(name);
            int suffix = 2;
            string baseKey = key;
            while (model.Playlists.ContainsKey(key))
                key = baseKey + "-" + suffix++.ToString(CultureInfo.InvariantCulture);

            string cover = CachePlaylistCoverForKey(key, _playlistCreateCoverPath);
            string? pendingItemPath = _playlistCreatePendingItemPath;
            model.Playlists[key] = new PlaylistDefinition
            {
                Key = key,
                Name = name,
                Description = description,
                Bucket = bucket,
                CoverPath = cover,
                ArtworkPath = cover,
                Items = _playlistCreatePendingAlbum?.ToList() ?? (string.IsNullOrWhiteSpace(pendingItemPath) ? new List<string>() : new List<string> { pendingItemPath })
            };

            SaveJson(PlaylistsPath, model);
            _playlistViewCache = null;
            _playlistViewCacheWriteTicks = long.MinValue;
            _playlistSelectionIndex = Math.Max(0, model.Playlists.Count - 1);
            HideCreatePlaylistOverlay();
            RefreshContent();
        }

        private List<string>? _playlistCreatePendingAlbum;
        private void AddItemToPlaylist(string path) => AddItemsToPlaylist(new[] { path });
        private void AddItemsToPlaylist(IEnumerable<string> paths)
        {
            var albumPaths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string path = albumPaths.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                return;

            var model = LoadJson<PlaylistModel>(PlaylistsPath) ?? new PlaylistModel();
            string itemBucket = PlaylistBucketForItem(ToItem(path));
            var choices = model.Playlists
                .Where(pair => pair.Value != null && PlaylistAcceptsBucket(pair.Value.Bucket, itemBucket))
                .OrderBy(pair => FirstRawNonEmpty(pair.Value.Name, pair.Key), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (choices.Count == 0)
            {
                ShowCreatePlaylistOverlay(path);
                _playlistCreatePendingAlbum = albumPaths;
                return;
            }

            string? selectedKey = ShowPlaylistPicker(choices.Select(pair => (pair.Key, FirstRawNonEmpty(pair.Value.Name, pair.Key))).ToList());
            if (selectedKey == "__new__")
            {
                ShowCreatePlaylistOverlay(path);
                _playlistCreatePendingAlbum = albumPaths;
                return;
            }
            if (string.IsNullOrWhiteSpace(selectedKey) || !model.Playlists.TryGetValue(selectedKey, out PlaylistDefinition? selected))
                return;

            selected.Items ??= new List<string>();
            foreach (string track in albumPaths)
                if (!selected.Items.Contains(track, StringComparer.OrdinalIgnoreCase)) selected.Items.Add(track);
            SaveJson(PlaylistsPath, model);
            _playlistViewCache = null;
            _playlistViewCacheWriteTicks = long.MinValue;
            Invalidate();
        }

        private static string PlaylistBucketForItem(LibraryItem? item)
        {
            return item?.Category switch
            {
                "Movies" => "Movies",
                "TV Series" => "TV",
                "Videos" => "Videos",
                "Music" => "Music",
                "Photos" => "Photos",
                _ => "Mixed"
            };
        }

        private static bool PlaylistAcceptsBucket(string? playlistBucket, string itemBucket)
        {
            string bucket = NormalizePlaylistBucket(playlistBucket);
            return string.Equals(bucket, "Mixed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(bucket, itemBucket, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePlaylistBucket(string? value)
        {
            string bucket = (value ?? string.Empty).Trim();
            if (bucket.Equals("Video", StringComparison.OrdinalIgnoreCase))
                return "Videos";
            if (bucket.Equals("Film", StringComparison.OrdinalIgnoreCase))
                return "Movies";
            if (bucket.Equals("Series", StringComparison.OrdinalIgnoreCase) || bucket.Equals("Serie", StringComparison.OrdinalIgnoreCase))
                return "TV";
            return string.IsNullOrWhiteSpace(bucket) ? "Mixed" : bucket;
        }

        private string? ShowPlaylistPicker(IReadOnlyList<(string Key, string Name)> choices)
        {
            using var dialog = new Form
            {
                Text = L("Aggiungi alla playlist", "Add to playlist"),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(430, 390),
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                BackColor = Color.FromArgb(7, 18, 30),
                ForeColor = Color.White,
                Font = LibraryFont("Segoe UI", 10f),
                KeyPreview = true
            };
            using var title = new Label
            {
                Text = L("Scegli una playlist", "Choose a playlist"),
                AutoSize = false,
                Bounds = new Rectangle(22, 18, 386, 34),
                ForeColor = Color.White,
                Font = LibraryFont("Segoe UI Semibold", 15f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            using var list = new ListBox
            {
                Bounds = new Rectangle(22, 62, 386, 244),
                BackColor = Color.FromArgb(13, 31, 48),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
                ItemHeight = 30
            };
            foreach (var choice in choices) list.Items.Add(choice.Name);
            list.Items.Add(L("+ Nuova playlist...", "+ New playlist..."));
            if (list.Items.Count > 0) list.SelectedIndex = 0;

            using var cancel = new Button { Text = L("Annulla", "Cancel"), Bounds = new Rectangle(206, 326, 96, 40), DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
            using var confirm = new Button { Text = L("Aggiungi", "Add"), Bounds = new Rectangle(312, 326, 96, 40), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 116, 150);
            confirm.FlatAppearance.BorderColor = Accent;
            confirm.BackColor = Color.FromArgb(0, 92, 184);
            cancel.ForeColor = confirm.ForeColor = Color.White;
            dialog.Controls.AddRange(new Control[] { title, list, cancel, confirm });
            dialog.AcceptButton = confirm;
            dialog.CancelButton = cancel;
            list.DoubleClick += (_, __) => { if (list.SelectedIndex >= 0) dialog.DialogResult = DialogResult.OK; };

            if (dialog.ShowDialog(FindForm()) != DialogResult.OK || list.SelectedIndex < 0)
                return null;
            return list.SelectedIndex >= choices.Count ? "__new__" : choices[list.SelectedIndex].Key;
        }

        private static string CachePlaylistCoverForKey(string key, string? sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return string.Empty;

            try
            {
                if (!File.Exists(sourcePath))
                    return string.Empty;

                string dir = Path.Combine(AppDataDir, "playlist-covers");
                Directory.CreateDirectory(dir);
                string safeKey = MakePlaylistKey(key);
                string dest = Path.Combine(dir, safeKey + ".png");
                using (var image = Image.FromFile(sourcePath))
                using (var copy = new Bitmap(image))
                {
                    copy.Save(dest, System.Drawing.Imaging.ImageFormat.Png);
                }
                return dest;
            }
            catch
            {
                try { return File.Exists(sourcePath) ? sourcePath : string.Empty; }
                catch { return string.Empty; }
            }
        }

        private static string MakePlaylistKey(string name)
        {
            string key = Regex.Replace((name ?? string.Empty).Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(key) ? "playlist" : key;
        }


    }
}
