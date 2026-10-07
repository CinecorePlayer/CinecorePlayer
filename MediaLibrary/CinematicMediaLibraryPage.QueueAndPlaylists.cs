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
            // Una chiusura ancora in animazione non deve finire nello sfondo catturato.
            _queueSlideTimer?.Stop();
            if (_queueOverlay != null) _queueOverlay.Visible = false;
            _queueBackdrop?.Dispose();
            _queueBackdrop = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            DrawToBitmap(_queueBackdrop, ClientRectangle);
            _queueGlassCache?.Dispose(); _queueGlassCache = null;
            _queueEditorVisible = true;
            _queueEditorSelectionIndex = Math.Max(0, _queueEditorSelectionIndex);
            _queueEditorScroll = Math.Max(0, _queueEditorScroll);
            _queueOverlay ??= new QueueDrawer(this);
            if (_queueOverlay.Parent != this) Controls.Add(_queueOverlay);
            // Il pannello entra da destra come un cassetto. Vetro e contenuto si preparano
            // prima di far partire il tempo: l'animazione compone soltanto un'immagine.
            _queueSlideOffset = QueueDrawerBaseBounds().Width + 24;
            PrepareQueueSheet();
            LayoutQueueDrawer();
            _queueOverlay.Visible = true;
            _queueOverlay.BringToFront();
            StartQueueSlide(opening: true);
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

        private int _queueSlideOffset;
        private System.Windows.Forms.Timer? _queueSlideTimer;
        private long _queueSlideStart;
        private bool _queueSlideOpening;

        private Bitmap? _queueSheet;
        private float _queueSheetAlpha = 1;
        private bool QueueAnimating => _queueSheet != null;

        /// <summary>Il pannello finito (vetro, testi, copertine) in un'immagine con gli angoli
        /// ritagliati in antialias: durante l'animazione si sposta e sfuma senza ridisegnare.</summary>
        private void PrepareQueueSheet()
        {
            _queueSheet?.Dispose(); _queueSheet = null;
            if (Width < 1 || Height < 1) return;
            var panel = QueueDrawerBaseBounds();
            int savedOffset = _queueSlideOffset;
            var normalHits = _hits;
            _queueSlideOffset = 0;
            _hits = _queueHits;
            try
            {
                _queueHits.Clear();
                using var layer = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var canvas = Graphics.FromImage(layer))
                {
                    canvas.SetClip(panel);
                    if (_queueBackdrop != null) canvas.DrawImage(_queueBackdrop, panel, panel, GraphicsUnit.Pixel);
                    DrawQueueEditor(canvas, ClientRectangle);
                }
                var sheet = new Bitmap(panel.Width, panel.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var sg = Graphics.FromImage(sheet))
                    sg.DrawImage(layer, new Rectangle(Point.Empty, panel.Size), panel, GraphicsUnit.Pixel);
                using (var mask = Round(new Rectangle(Point.Empty, panel.Size), 13))
                    ApplyRoundedAlphaMask(sheet, mask);
                _queueSheet = sheet;
            }
            catch { _queueSheet = null; }
            finally { _hits = normalHits; _queueSlideOffset = savedOffset; }
        }

        private void StartQueueSlide(bool opening)
        {
            _queueSlideOpening = opening;
            _queueSlideStart = CinecorePlayer2025.Utilities.AnimationClock.NowMs;
            if (_queueSlideTimer == null)
            {
                _queueSlideTimer = new System.Windows.Forms.Timer { Interval = CinecorePlayer2025.Utilities.AnimationClock.FrameIntervalMs };
                _queueSlideTimer.Tick += (_, _) => StepQueueSlide();
            }
            _queueSlideTimer.Start();
        }

        private void StepQueueSlide()
        {
            int distance = QueueDrawerBaseBounds().Width + 24;
            double t = Math.Clamp((CinecorePlayer2025.Utilities.AnimationClock.NowMs - _queueSlideStart) / (_queueSlideOpening ? 240.0 : 180.0), 0, 1);
            double eased = _queueSlideOpening ? 1 - Math.Pow(1 - t, 3) : t * t * t;
            // Entra per una frazione della larghezza mentre compare: piu' morbido di un
            // cassetto che attraversa tutto lo schermo.
            distance = Math.Min(distance, (int)Math.Round(96 * DeviceDpi / 96f));
            _queueSlideOffset = (int)Math.Round(distance * (_queueSlideOpening ? 1 - eased : eased));
            _queueSheetAlpha = (float)(_queueSlideOpening ? Math.Min(1, eased * 1.25) : 1 - eased);
            // Solo il cassetto si ridisegna: niente layout ne' ridisegno della pagina.
            if (_queueOverlay != null && _queueOverlay.Visible) _queueOverlay.Invalidate();
            if (t < 1) return;
            _queueSlideTimer?.Stop();
            _queueSlideOffset = 0;
            _queueSheetAlpha = 1;
            _queueSheet?.Dispose(); _queueSheet = null;
            LayoutQueueDrawer();
            _queueOverlay?.Invalidate();
            if (!_queueSlideOpening) FinishCloseQueueEditorOverlay();
        }

        private void CloseQueueEditorOverlay()
        {
            if (!_queueEditorVisible) return;
            _queueEditorVisible = false;
            _queueHits.Clear();
            _queueDragIndex = -1;
            _queueDropIndex = -1;
            _queueDragPath = null;
            _queueDragging = false;
            if (_queueOverlay != null && _queueOverlay.Visible && IsHandleCreated)
            {
                _queueSlideOffset = 0;
                PrepareQueueSheet();
                _queueHits.Clear();
                LayoutQueueDrawer();
                StartQueueSlide(opening: false);
                return;
            }
            FinishCloseQueueEditorOverlay();
        }

        private void FinishCloseQueueEditorOverlay()
        {
            if (_queueEditorVisible) return;
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

        private Rectangle QueueDrawerBaseBounds()
        {
            int height = Math.Max(1, Height - MusicTransportInset);
            float scale = Math.Clamp(Math.Min(Width / 1440f, height / 950f), .86f, 1.15f);
            int width = Math.Max(1, Math.Min(Width - 32, S(scale, 420)));
            return new Rectangle(Width - width - 28, 28, width, Math.Max(1, height - 56));
        }

        private Rectangle QueueDrawerBounds()
        {
            var bounds = QueueDrawerBaseBounds();
            if (!QueueAnimating) bounds.Offset(_queueSlideOffset, 0);
            return bounds;
        }

        private void LayoutQueueDrawer()
        {
            if (_queueOverlay == null) return;
            var panel = QueueDrawerBaseBounds();
            // Durante l'animazione il cassetto copre anche il margine destro, dove scorre il pannello.
            _queueOverlay.Bounds = QueueAnimating ? Rectangle.FromLTRB(panel.Left, panel.Top, Math.Max(panel.Right, Width), panel.Bottom) : QueueDrawerBounds();
            // Niente Region: gli angoli arrotondati sono disegnati con antialias sopra lo
            // sfondo catturato della pagina.
            var old = _queueOverlay.Region;
            _queueOverlay.Region = null;
            old?.Dispose();
        }

        /// <summary>
        /// Tiene la coda sopra la pagina quando cambia il contenuto (grafici, testo): prima i
        /// grafici venivano portati in primo piano e la coda restava sotto. Con
        /// <paramref name="refreshBackdrop"/> ricattura anche la pagina dietro al vetro.
        /// </summary>
        public void KeepQueueEditorOnTop(bool refreshBackdrop)
        {
            if (!_queueEditorVisible || _queueOverlay == null || _queueOverlay.Parent != this) return;
            _queueOverlay.BringToFront();
            if (!refreshBackdrop || !IsHandleCreated || _queueBackdropRefreshPending) return;
            _queueBackdropRefreshPending = true;
            BeginInvoke(new Action(RecaptureQueueBackdrop));
        }

        private bool _queueBackdropRefreshPending;

        private void RecaptureQueueBackdrop()
        {
            _queueBackdropRefreshPending = false;
            if (!_queueEditorVisible || _queueOverlay == null || IsDisposed || Width < 1 || Height < 1) return;
            var next = new Bitmap(Width, Height);
            _queueOverlay.Visible = false;
            try { DrawToBitmap(next, ClientRectangle); }
            finally { _queueOverlay.Visible = true; }
            _queueBackdrop?.Dispose(); _queueBackdrop = next;
            _queueGlassCache?.Dispose(); _queueGlassCache = null;
            _queueOverlay.BringToFront();
            _queueOverlay.Invalidate();
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
                if (_owner._queueSheet is { } sheet)
                {
                    // Animazione: sfondo catturato e pannello pronto, spostato e sfumato.
                    var g = e.Graphics;
                    if (_owner._queueBackdrop != null) g.DrawImage(_owner._queueBackdrop, ClientRectangle, Bounds, GraphicsUnit.Pixel);
                    else { using var background = new SolidBrush(BackColor); g.FillRectangle(background, ClientRectangle); }
                    var panel = _owner.QueueDrawerBaseBounds();
                    using var fade = new System.Drawing.Imaging.ImageAttributes();
                    fade.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = Math.Clamp(_owner._queueSheetAlpha, 0f, 1f) });
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(sheet, new Rectangle(panel.Left + _owner._queueSlideOffset - Left, panel.Top - Top, sheet.Width, sheet.Height),
                        0, 0, sheet.Width, sheet.Height, GraphicsUnit.Pixel, fade);
                    return;
                }
                var normalHits = _owner._hits;
                _owner._hits = _owner._queueHits;
                try
                {
                    _owner._queueHits.Clear();
                    if (_layer == null || _layer.Size != _owner.ClientSize)
                    { _layer?.Dispose(); _layer = new Bitmap(_owner.Width, _owner.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb); } // opaca: ClearType di GDI su ARGB impastava i testi
                    // GDI TextRenderer and GDI+ icons must share untransformed
                    // coordinates; crop the finished drawer instead of translating.
                    using (var canvas = Graphics.FromImage(_layer))
                    {
                        canvas.SetClip(Bounds);
                        if (_owner._queueBackdrop != null) canvas.DrawImage(_owner._queueBackdrop, Bounds, Bounds, GraphicsUnit.Pixel);
                        else { using var background = new SolidBrush(BackColor); canvas.FillRectangle(background, Bounds); }
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
                var state = g.Save();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (_queueBackdrop != null)
                {
                    var glassBounds = QueueDrawerBaseBounds();
                    if (_queueGlassCache == null || _queueGlassBounds != glassBounds ||
                        !string.Equals(_queueGlassArtworkPath, _glassArtworkPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _queueGlassCache?.Dispose();
                        _queueGlassCache = GlassSurface.CreateLiquid(_queueBackdrop, glassBounds, hairline: false);
                        _queueGlassBounds = glassBounds;
                        _queueGlassArtworkPath = _glassArtworkPath;
                    }
                    // Vetro come texture su tracciato con antialias (il clip dava scalini).
                    using (var glass = new TextureBrush(_queueGlassCache, WrapMode.Clamp))
                    {
                        glass.TranslateTransform(modal.Left, modal.Top);
                        g.FillPath(glass, shape);
                    }
                    // Nessun contorno: il tratto da 1 px sugli angoli arrotondati si vedeva a
                    // gradini. La velatura e' gia' nel materiale del vetro.
                }
                g.Restore(state);
            }
            _hits.Add(new HitZone { Bounds = modal, Kind = HitKind.None, Key = "queue-panel" });
            // Una sola colonna di allineamento (pad) per titolo, riepilogo, copertine e pulsante;
            // il titolo e' centrato in verticale sulla X.
            int pad = S(scale, 24);
            var close = new Rectangle(modal.Right - S(scale, 16) - S(scale, 36), modal.Top + S(scale, 18), S(scale, 36), S(scale, 36));
            DrawModalClose(g, close, HitKind.QueueClose, scale);
            using var title = LibraryFont("Segoe UI Semibold", 18f * scale);
            using var countFont = LibraryFont("Segoe UI", 9.5f * scale);
            QueueText(g, L("Coda", "Queue"), title, new Rectangle(modal.Left + pad, close.Top, close.Left - modal.Left - pad - S(scale, 8), close.Height), TextMain);
            var summary = new Rectangle(modal.Left + pad, close.Bottom, modal.Width - pad * 2, S(scale, 22));
            QueueText(g, QueueSummary(snapshot), countFont, summary, Muted);
            // "Svuota coda" e' un pulsante vero in fondo al pannello, lontano dai brani.
            int bottom = modal.Bottom - S(scale, 20);
            if (snapshot.Count > 0)
            {
                var clear = new Rectangle(modal.Left + pad, bottom - S(scale, 44), modal.Width - pad * 2, S(scale, 44));
                DrawQueueClearButton(g, clear, scale);
                bottom = clear.Top - S(scale, 8);
            }
            int listTop = summary.Bottom + S(scale, 14);
            _queueListRect = new Rectangle(modal.Left + pad - S(scale, 8), listTop, modal.Width - pad - S(scale, 10), Math.Max(1, bottom - listTop));
            if(snapshot.Count==0) DrawQueueEmptyState(g,_queueListRect,scale);
            else DrawQueueRows(g,_queueListRect,snapshot,scale);
        }

        // Il cassetto e' composto su un livello opaco. Testo in GDI+ (TextRenderer su una bitmap
        // copia l'intero livello a ogni chiamata e rallentava lo scorrimento): ClearType nel tema
        // scuro per la nitidezza, antialias in scala di grigi nel chiaro (il ClearType impastava).
        private static void QueueText(Graphics g, string text, Font font, Rectangle bounds, Color color)
        {
            if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
            var hint = g.TextRenderingHint;
            g.TextRenderingHint = Theme.IsLight ? System.Drawing.Text.TextRenderingHint.AntiAliasGridFit : System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            // Tipografico: senza il margine sinistro di DrawString (proporzionale al corpo), titolo
            // grande e testi piccoli partono dalla stessa colonna.
            using var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            format.FormatFlags |= StringFormatFlags.NoWrap;
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, bounds, format);
            g.TextRenderingHint = hint;
        }

        private string QueueSummary(IReadOnlyList<PlaybackQueueViewItem> snapshot)
        {
            if (snapshot.Count == 0) return L("Nessun brano in coda", "Nothing in the queue");
            string count = snapshot.Count == 1 ? L("1 brano", "1 track") : string.Format(L("{0} brani", "{0} tracks"), snapshot.Count);
            int total = (int)Math.Round(snapshot.Sum(entry => QueueTrack(entry.Path).Minutes ?? 0));
            if (total < 1) return count;
            int hours = total / 60, minutes = total % 60;
            string Minutes(int m) => m == 1 ? L("1 minuto", "1 minute") : string.Format(L("{0} minuti", "{0} minutes"), m);
            string Hours(int h) => h == 1 ? L("1 ora", "1 hour") : string.Format(L("{0} ore", "{0} hours"), h);
            string length = hours == 0 ? Minutes(minutes) : minutes == 0 ? Hours(hours) : Hours(hours) + L(" e ", " ") + Minutes(minutes);
            return count + "  \u00b7  " + length;
        }

        // Azione in fondo al pannello: un filo separa l'elenco, poi il testo centrato,
        // senza riquadro. Al passaggio cambia solo il colore (rosso tenue): niente blocchi pieni.
        private void DrawQueueClearButton(Graphics g, Rectangle r, float scale)
        {
            using (var rule = new Pen(Color.FromArgb(Theme.IsLight ? 26 : 22, Theme.Text)))
                g.DrawLine(rule, r.Left, r.Top, r.Right, r.Top);
            using var font = LibraryFont("Segoe UI", 9.5f * scale);
            string text = L("Svuota coda", "Clear queue");
            int textW = (int)Math.Ceiling(g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width);
            int icon = 0, gap = 0;
            var line = new Rectangle(r.Left, r.Top + S(scale, 1), r.Width, r.Height - S(scale, 1));
            int hitWidth = icon + gap + textW + S(scale, 32);
            bool hover = new Rectangle(line.Left + (line.Width - hitWidth) / 2, line.Top, hitWidth, line.Height).Contains(_lastMouse);
            Color ink = hover ? Color.FromArgb(255, 122, 132) : Muted;
            int x = line.Left + (line.Width - icon - gap - textW) / 2;
            QueueText(g, text, font, new Rectangle(x + icon + gap, line.Top, textW + S(scale, 4), line.Height), ink);
            // Area cliccabile solo attorno al testo, non tutta la riga.
            int hitW = icon + gap + textW + S(scale, 32);
            _hits.Add(new HitZone { Bounds = new Rectangle(line.Left + (line.Width - hitW) / 2, line.Top, hitW, line.Height), Kind = HitKind.QueueClear });
        }

        // Titolo, artista, durata e copertina anche per i brani fuori dalla categoria aperta:
        // prima comparivano senza artista e con l'icona generica.
        private readonly Dictionary<string, (string Title, string Artist, double? Minutes, string? Art)> _queueTrackCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _queueArtworkRequests = new(StringComparer.OrdinalIgnoreCase);

        private (string Title, string Artist, double? Minutes, string? Art) QueueTrack(string path)
        {
            if (_queueTrackCache.TryGetValue(path, out var known) && (known.Art != null || _queueArtworkRequests.Contains(path))) return known;
            var item = _items.SelectMany(t => t.IsGroup ? t.Children : new List<LibraryItem> { t })
                .FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
            var tags = item == null ? TryGetIndexedAudioTags(path) : null;
            string title = FirstNonEmpty(item?.Title, tags == null ? null : MusicTextIdentity.Title(tags.Title), Path.GetFileNameWithoutExtension(path));
            string artist = FirstNonEmpty(item?.ArtistName, tags?.Artist, tags?.AlbumArtist, InferAlbumInfo(path).Artist);
            double? minutes = item?.DurationMinutes ?? tags?.DurationMinutes;
            string? art = item?.ArtPath ?? MusicArtworkService.GetCachedArtworkPath(path) ?? FolderCover(path);
            if (art == null && _queueArtworkRequests.Add(path))
            {
                _ = MusicArtworkService.ResolveArtworkAsync(path, System.Threading.CancellationToken.None).ContinueWith(task =>
                {
                    if (task.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || task.Result == null) return;
                    PostToUi(() => { _queueTrackCache.Remove(path); _queueOverlay?.Invalidate(); });
                }, System.Threading.Tasks.TaskScheduler.Default);
            }
            var info = (title, MusicTextIdentity.Artist(artist), minutes, art);
            _queueTrackCache[path] = info;
            return info;
        }

        private static string? FolderCover(string path) => MusicArtworkService.FindLocalAlbumCover(path);


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
            using var heading = LibraryFont("Segoe UI Semibold",8.4f*scale);
            var state=g.Save(); g.SetClip(viewport,CombineMode.Intersect);
            for(int index=_queueEditorScroll;index<Math.Min(snapshot.Count,_queueEditorScroll+maxVisible);index++)
            {
                string section=index<current?L("Cronologia","History"):index==current?L("In riproduzione","Now playing"):L("In arrivo","Up next");
                if(section!=lastSection)
                {
                    QueueText(g,section.ToUpperInvariant(),heading,new Rectangle(viewport.Left+S(scale,8),y,viewport.Width,headingH),Muted);
                    y+=headingH;lastSection=section;
                }
                var row=new Rectangle(viewport.Left,y,viewport.Width-S(scale,10),rowH);
                DrawQueueRow(g,row,snapshot[index],index,index==_queueEditorSelectionIndex,scale);
                if(_queueDragging&&_queueDropIndex==index) { using var line=new Pen(Accent,2);g.DrawLine(line,row.Left,row.Top,row.Right,row.Top); }
                y+=rowH;
            }
            g.Restore(state);
            DrawQueueScrollbar(g,new Rectangle(viewport.Right+S(scale,2),viewport.Top,Math.Max(3,S(scale,3)),viewport.Height),snapshot.Count,maxVisible,scale);
        }

        private void DrawQueueRow(Graphics g, Rectangle r, PlaybackQueueViewItem item, int visualIndex, bool selected, float scale)
        {
            bool hover = r.Contains(_lastMouse);
            if(selected || hover)
                Theme.DrawHighlight(g, r, selected, S(scale, 3));
            _hits.Add(new HitZone { Bounds=r,Kind=HitKind.QueuePlay,Key=item.Path,Index=visualIndex });
            var track = QueueTrack(item.Path);
            int cover=S(scale,44);
            var art=new Rectangle(r.Left+S(scale,8),r.Top+(r.Height-cover)/2,cover,cover);
            DrawQueueArtwork(g,art,track.Art,item.IsCurrent,scale);
            string artist = string.IsNullOrWhiteSpace(track.Artist) ? L("Artista sconosciuto", "Unknown artist") : track.Artist;
            int x=art.Right+S(scale,12), width=Math.Max(30,r.Right-x-S(scale,40));
            using var title=LibraryFont("Segoe UI Semibold",10f*scale);
            using var sub=LibraryFont("Segoe UI",9f*scale);
            QueueText(g,track.Title,title,new Rectangle(x,r.Top+S(scale,9),width,S(scale,23)),item.IsCurrent?Accent:TextMain);
            QueueText(g,artist,sub,new Rectangle(x,r.Top+S(scale,32),width,S(scale,21)),Muted);
            var remove=new Rectangle(r.Right-S(scale,34),r.Top+(r.Height-S(scale,28))/2,S(scale,28),S(scale,28));
            if (remove.Contains(_lastMouse))
                using (var halo = new SolidBrush(Color.FromArgb(28, Theme.Text))) g.FillEllipse(halo, remove);
            HUD.MusicTransportBar.DrawSymbol(g,Rectangle.Inflate(remove,-S(scale,8),-S(scale,8)),"close",remove.Contains(_lastMouse)?TextMain:Muted);
            _hits.Add(new HitZone { Bounds=remove,Kind=HitKind.QueueRemove,Key=item.Path,Index=visualIndex });
        }

        private void DrawQueueScrollbar(Graphics g, Rectangle track, int total, int visible, float scale)
        {
            if (total <= visible || track.Height <= S(scale, 24))
            {
                _queueScrollbarTrack = Rectangle.Empty;
                _queueScrollbarThumb = Rectangle.Empty;
                return;
            }

            double ratio = visible / (double)Math.Max(1, total);
            int thumbH = ScrollbarChrome.ThumbLength(track.Height, ratio);
            int maxScroll = Math.Max(1, total - visible);
            int travel = Math.Max(1, track.Height - thumbH);
            int thumbY = track.Top + (int)Math.Round(travel * (_queueEditorScroll / (double)maxScroll));
            _queueScrollbarTrack = Rectangle.Inflate(track, 8, 0);
            _queueScrollbarThumb = new Rectangle(_queueScrollbarTrack.Left, thumbY, _queueScrollbarTrack.Width, thumbH);
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
                { } // senza contorno
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

        private void DrawQueueArtwork(Graphics g, Rectangle r, string? artPath, bool selected, float scale)
        {
            // Miniatura ritagliata con maschera antialias e messa in cache: con SetClip gli angoli
            // erano a scalini, e ricomporla a ogni fotogramma rallentava lo scorrimento.
            string key = $"q|{artPath}|{r.Width}x{r.Height}|{selected}|{Theme.Text.ToArgb()}";
            Bitmap? thumb = GetPosterCardBitmap(key, r.Size, local =>
            {
                var lr = new Rectangle(Point.Empty, r.Size);
                using var lg = Graphics.FromImage(local);
                lg.SmoothingMode = SmoothingMode.AntiAlias;
                lg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                lg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var fill = new SolidBrush(Color.FromArgb(selected ? 60 : 40, Theme.Text)))
                    lg.FillRectangle(fill, lr);
                bool drawn = artPath != null && DrawImagePath(lg, lr, artPath, requireLandscape: false, tint: false, preserveAspectWhenWide: false, verticalFocus: 0.5f);
                if (!drawn)
                    DrawIcon(lg, new Rectangle(lr.Width / 4, lr.Height / 4, lr.Width / 2, lr.Height / 2), "music", Muted);
                using (var clip = Round(lr, S(scale, 5)))
                    ApplyRoundedAlphaMask(local, clip);
                return drawn ? IsDisplayImageExact(ResolveDisplayImagePath(artPath), Math.Max(r.Width, r.Height)) : artPath == null;
            });
            if (thumb != null)
            {
                // Copia 1:1: il ricampionamento bilineare a mezzo pixel sfumava il bordo della
                // copertina in un contorno chiaro e sfocato.
                var mode = g.InterpolationMode; var offset = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(thumb, new Rectangle(r.X, r.Y, thumb.Width, thumb.Height));
                g.InterpolationMode = mode; g.PixelOffsetMode = offset;
                return;
            }
            using (var clip = Round(r, S(scale, 5)))
            using (var smooth = CinecorePlayer2025.Utilities.SmoothClip.Begin(g, clip))
            {
                var sg = smooth.Graphics;
                using (var fill = new SolidBrush(Color.FromArgb(selected ? 60 : 40, Theme.Text)))
                    sg.FillRectangle(fill, r);
                bool drawn = artPath != null && DrawImagePath(sg, r, artPath, requireLandscape: false, tint: false, preserveAspectWhenWide: false, verticalFocus: 0.5f);
                if (!drawn)
                    DrawIcon(sg, new Rectangle(r.Left + r.Width / 4, r.Top + r.Height / 4, r.Width / 2, r.Height / 2), "music", Muted);
            }
        }

        private void DrawQueueMiniButton(Graphics g, Rectangle r, string icon, HitKind kind, string key, float scale)
        {
            using (var path = Round(r, S(scale, 7)))
            using (var fill = new SolidBrush(r.Contains(_lastMouse) ? Color.FromArgb(82, 16, 38, 58) : Color.FromArgb(58, 12, 28, 44)))
            using (var border = new Pen(Color.FromArgb(36, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                { } // senza contorno
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
                ShowCreatePlaylistOverlay(path, albumPaths);
                return;
            }

            string? selectedKey = ShowPlaylistPicker(choices.Select(pair => (pair.Key, FirstRawNonEmpty(pair.Value.Name, pair.Key))).ToList());
            if (selectedKey == "__new__")
            {
                ShowCreatePlaylistOverlay(path, albumPaths);
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
            // Scheda disegnata al 100% e ingrandita da Windows alla scala dello schermo, come tutte le altre.
            using var sheetLayout = SheetPresenter.Layout(this);
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

            if (SheetPresenter.ShowDialog(dialog, FindForm()) != DialogResult.OK || list.SelectedIndex < 0)
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
