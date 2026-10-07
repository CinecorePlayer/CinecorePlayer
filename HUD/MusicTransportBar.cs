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
        // Durante la riproduzione la pagina si invalida di continuo: ridisegnare la barra e rifare
        // il vetro ogni volta (~11 volte al secondo) rallentava tutto il programma. Il vetro si
        // rifa' quando la pagina si ferma, o al piu' ogni 1,5 s se non si ferma mai.
        private void BackdropInvalidated(object? sender, InvalidateEventArgs e) { _backdropDirty=true; ScheduleSettledCapture(); }
        // La pagina viene invalidata prima di ridisegnarsi: la cattura immediata prende il
        // fotogramma vecchio. Una seconda cattura a pagina ferma rimette il vetro in pari.
        private Timer? _settleTimer;
        private void ScheduleSettledCapture()
        {
            if (_settleTimer == null)
            {
                _settleTimer = new Timer { Interval = 220 };
                _settleTimer.Tick += (_, _) => { _settleTimer!.Stop(); _backdropDirty = true; _lastBackdropCaptureTick = 0; Invalidate(); };
            }
            _settleTimer.Stop(); _settleTimer.Start();
        }
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
        // Comparsa: la barra entra in dissolvenza con un leggero scorrimento; la timeline
        // aspetta la durata reale invece di mostrare 0:00 / 0:00 e poi saltare.
        private const int RevealMs = 220, SeekRevealMs = 240;
        private long _revealStart = -1, _seekRevealStart = -1;
        private readonly Timer _revealTimer = new() { Interval = CinecorePlayer2025.Utilities.AnimationClock.FrameIntervalMs };
        private static double Ease(long start, int ms)
        {
            if (start < 0) return 1;
            double t = Math.Clamp((CinecorePlayer2025.Utilities.AnimationClock.NowMs - start) / (double)ms, 0, 1);
            return 1 - Math.Pow(1 - t, 3);
        }
        // Uscita: la comparsa al contrario (dissolvenza e leggera discesa), poi si nasconde.
        private const int HideMs = 180;
        private long _hideStart = -1;
        private Action? _onHidden;
        // Ultimo fotogramma della barra, congelato: mentre esce, brano e copertina vengono gia'
        // azzerati dal player e la barra non deve svuotarsi a vista.
        private Bitmap? _hideLayer;
        public bool IsHiding => _hideStart >= 0;
        private double Reveal => _hideStart >= 0 ? 1 - Ease(_hideStart, HideMs) : Ease(_revealStart, RevealMs);
        public void PlayHide(Action onHidden)
        {
            if (!Visible || Width <= 0 || Height <= 0) { onHidden(); return; }
            if (IsHiding) { _onHidden = onHidden; return; }
            try
            {
                _hideLayer?.Dispose();
                _hideLayer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                using var frozen = Graphics.FromImage(_hideLayer);
                PaintSurface(frozen, withGlass: true); PaintContent(frozen);
            }
            catch { _hideLayer?.Dispose(); _hideLayer = null; }
            // Sfondo aggiornato: durante l'uscita sotto la barra si vede la pagina attuale.
            try { if (Parent != null) CaptureBackdrop(Parent.ClientSize, forcePage: true); } catch { }
            _onHidden = onHidden;
            _hideStart = CinecorePlayer2025.Utilities.AnimationClock.NowMs;
            StartRevealTimer();
            Invalidate();
        }
        public void CancelHide()
        {
            if (!IsHiding) return;
            _hideStart = -1; _onHidden = null;
            _hideLayer?.Dispose(); _hideLayer = null;
            _backdropDirty = true; _lastBackdropCaptureTick = 0;
            Invalidate();
        }

        /// <summary>Cambio di tema: il vetro va rifatto subito. Senza, durante la riproduzione
        /// restava quello del tema precedente fino al rifacimento periodico (1-2 secondi).</summary>
        public void RefreshTheme()
        {
            _glass?.Dispose(); _glass = null;
            _backdrop?.Dispose(); _backdrop = null;
            _backdropDirty = true; _lastBackdropCaptureTick = 0;
            if (!Visible || Parent == null || IsHiding) return;
            // Prima la pagina dietro (il vetro la legge dallo schermo), poi la barra.
            try { Parent.Update(); } catch { }
            Invalidate();
            try { Update(); } catch { }
        }
        private double SeekReveal => _duration > 0 ? Ease(_seekRevealStart, SeekRevealMs) : 0;
        private void StartRevealTimer() { if (!_revealTimer.Enabled) _revealTimer.Start(); }
        // La comparsa animata parte solo all'avvio della riproduzione: legarla a
        // VisibleChanged la faceva ripartire (sfarfallio) quando una scheda nascondeva e
        // rimostrava la barra.
        public void PlayReveal()
        {
            _hideStart = -1; _onHidden = null;
            // Il vetro si prepara prima di far partire il tempo: catturare la pagina nel primo
            // fotogramma costava decine di ms e l'animazione (a tempo) ripartiva gia' avanti.
            try { if (Parent != null && Width > 0 && Height > 0) CaptureBackdrop(Parent.ClientSize); } catch { }
            // Anche icone e livello di composizione si preparano ora: al primo disegno le icone
            // SVG vengono rasterizzate e il primo fotogramma costava ~45 ms.
            try
            {
                if (Width > 0 && Height > 0)
                {
                    if (_revealLayer == null || _revealLayer.Size != Size) { _revealLayer?.Dispose(); _revealLayer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb); }
                    using var warm = Graphics.FromImage(_revealLayer);
                    PaintContent(warm);
                }
            }
            catch { }
            _revealStart = CinecorePlayer2025.Utilities.AnimationClock.NowMs;
            _seekRevealStart = _duration > 0 ? _revealStart : -1;
            StartRevealTimer();
            Invalidate();
        }
        private bool _playing, _muted, _bitstream, _shuffle, _repeat, _favorite, _english;
        private int _view, _focusIndex;
        public bool Expanded { get; set; }
        // Barra a tutta larghezza, attaccata al fondo (niente margini ne' raggi).
        private int CardMargin => 0;
        private int CardRadius => 0;
        private Rectangle _card;
        public int PreferredBarHeight => CardMargin + (Width - CardMargin * 2 < D(760) ? D(174) : Width - CardMargin * 2 < D(1180) ? D(116) : D(84));
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
            _revealTimer.Tick += (_, _) =>
            {
                Invalidate();
                if (IsHiding)
                {
                    if (Ease(_hideStart, HideMs) < 1) return;
                    _revealTimer.Stop();
                    _hideStart = -1;
                    _hideLayer?.Dispose(); _hideLayer = null;
                    var done = _onHidden; _onHidden = null;
                    done?.Invoke();
                    return;
                }
                if (Reveal >= 1 && (_duration <= 0 || SeekReveal >= 1)) _revealTimer.Stop();
            };
        }
        public void SetLanguage(bool english) { _english = english; }
        public void SetFavorite(bool favorite) { if (_favorite != favorite) { _favorite = favorite; Invalidate(); } }
        public void SetTrack(string title,string artist) { if (_title == title && _artist == artist) return; _title = title; _artist = artist; Invalidate(); }
        public void SetArtwork(string? path)
        {
            if (_artworkPath == (path ?? "")) return; _artworkPath = path ?? "";
            Image? next = null;
            // Copertina ridotta una volta sola: a piena risoluzione (spesso migliaia di pixel)
            // veniva ricampionata a ogni ridisegno della barra e a ogni rifacimento del vetro.
            try
            {
                if (File.Exists(path))
                {
                    using var image = Image.FromFile(path!);
                    int side = Math.Max(D(46) * 2, 128);
                    var small = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        int crop = Math.Min(image.Width, image.Height);
                        using var wrap = new ImageAttributes(); wrap.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(image, new Rectangle(0, 0, side, side), (image.Width - crop) / 2, (image.Height - crop) / 2, crop, crop, GraphicsUnit.Pixel, wrap);
                    }
                    next = small;
                }
            }
            catch { }
            var old = _cover; _cover = next; old?.Dispose(); _backdropDirty=true; _lastBackdropCaptureTick=0; Invalidate();
        }
        public void UpdatePlayback(double position,double duration,bool playing,float volume,bool muted,bool bitstream,bool shuffle,bool repeat,int view)
        {
            bool bitstreamChanged = _bitstream != bitstream;
            _position = double.IsFinite(position) ? Math.Max(0,position) : 0;
            double previousDuration = _duration;
            _duration = double.IsFinite(duration) ? Math.Max(0,duration) : 0;
            if (previousDuration <= 0 && _duration > 0 && Visible) { _seekRevealStart = CinecorePlayer2025.Utilities.AnimationClock.NowMs; StartRevealTimer(); }
            _volume = Math.Clamp(volume,0,1); _playing=playing; _muted=muted; _bitstream=bitstream; _shuffle=shuffle; _repeat=repeat; _view=view;
            if (bitstreamChanged) { _hover = ""; _drag = ""; Capture = false; Cursor = Cursors.Default; }
            Invalidate();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (_targets == null) return;
            _targets.Clear();
            _card = new Rectangle(CardMargin, 0, Math.Max(1, Width - CardMargin * 2), Math.Max(1, Height - CardMargin));
            int w=(int)(_card.Width*96f/DeviceDpi),h=(int)(_card.Height*96f/DeviceDpi);
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
            int seekLeft=narrow?70:Math.Max(songW+72,w/2-300);
            int seekRight=narrow?w-70:Math.Min(w-340,w/2+300);
            int seekY=h-17;
            _targets["seek"]=R(seekLeft,seekY-10,Math.Max(20,seekRight-seekLeft),20);
            // Tutto il contenuto e' calcolato nella lastra: si sposta nella sua posizione.
            foreach (var key in _targets.Keys.ToList()) { var t = _targets[key]; t.Offset(_card.Location); _targets[key] = t; }
            _art.Offset(_card.Location); _titleRect.Offset(_card.Location); _artistRect.Offset(_card.Location);
            // Tempi ricavati dalla timeline gia' in pixel: stessa distanza dai due capi (il
            // trascorso e' allineato a destra, verso la linea) e stesso centro verticale della
            // linea. Prima erano rettangoli arrotondati a parte: storti e a distanze diverse.
            var seek=_targets["seek"];
            int timeH=D(20), timeW=D(54), timeGap=D(10), seekCenter=seek.Top+seek.Height/2;
            _elapsed=new Rectangle(seek.Left-timeGap-timeW,seekCenter-timeH/2,timeW,timeH);
            _end=new Rectangle(seek.Right+timeGap,seekCenter-timeH/2,timeW,timeH);
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
                    bool revealing = Reveal < 1 && _glass != null; // durante la comparsa lo sfondo resta fermo
                    if (_backdrop == null || _backdropParentSize != size || (_glassBounds != Bounds && !revealing) ||
                        (_backdropDirty && !revealing && (_glass==null || Environment.TickCount64-_lastBackdropCaptureTick>=1500)))
                        CaptureBackdrop(size);
                    if (_backdrop == null || _glass == null) return;
                    PaintSurface(e.Graphics, withGlass: Reveal >= 1);
                }
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            double reveal = Reveal;
            if (reveal >= 1 || _glass == null || Width <= 0 || Height <= 0) { PaintContent(e.Graphics); return; }
            // Vetro e contenuto dipinti insieme su un layer opaco (ClearType corretto),
            // poi composti con alpha e offset crescenti sopra lo sfondo della pagina.
            Bitmap layer;
            if (IsHiding && _hideLayer != null && _hideLayer.Size == Size) layer = _hideLayer;
            else
            {
                if (_revealLayer == null || _revealLayer.Size != Size) { _revealLayer?.Dispose(); _revealLayer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb); }
                layer = _revealLayer;
                using var lg = Graphics.FromImage(layer);
                PaintSurface(lg, withGlass: true); PaintContent(lg);
            }
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)reveal });
            // Dissolvenza quasi sul posto (come le schede): solo un accenno di movimento.
            int offset = (int)Math.Round((1 - reveal) * D(12));
            e.Graphics.DrawImage(layer, new Rectangle(0, offset, Width, Height), 0, 0, Width, Height, GraphicsUnit.Pixel, attributes);
        }
        private Bitmap? _revealLayer;

        /// <summary>La pagina dietro (attorno alla lastra) e, sopra, il vetro sul tracciato
        /// arrotondato con antialias: nessun contorno tracciato.</summary>
        private void PaintSurface(Graphics g, bool withGlass)
        {
            if (_backdrop == null) return;
            var behindBar = new Rectangle(Bounds.X - _backdropRegion.X, Bounds.Y - _backdropRegion.Y, Width, Height);
            g.DrawImage(_backdrop, ClientRectangle, behindBar, GraphicsUnit.Pixel);
            if (!withGlass || _glass == null || _card.Width <= 0 || _card.Height <= 0) return;
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (var shape = RoundedCard(_card, CardRadius))
            using (var glass = new TextureBrush(_glass, WrapMode.Clamp))
            {
                glass.TranslateTransform(_card.Left, _card.Top);
                g.FillPath(glass, shape);
            }
            g.Restore(state);
        }

        private static GraphicsPath RoundedCard(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(r); return path; }
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private Rectangle _backdropRegion;
        private Size _backdropParentSize;

        private void CaptureBackdrop(Size size, bool forcePage = false)
        {
            if (Parent == null || size.Width <= 0 || size.Height <= 0 || Width <= 0 || Height <= 0) return;
            // Solo la striscia dietro la barra, piu' il margine della sfocatura, in una bitmap
            // della stessa misura: ridisegnare la pagina intera in una bitmap a 1440p costava
            // ~200 ms (ogni testo GDI copia l'intera bitmap). Il vetro e' sfocato: bastano
            // immagini e colori, i testi (fuori dalla striscia per TextRenderer) non servono.
            var region = Rectangle.Intersect(Rectangle.Inflate(Bounds, 56, 56), new Rectangle(Point.Empty, size));
            if (region.Width <= 0 || region.Height <= 0) return;
            // Con il riflesso basta leggere la finestra: la pagina si ridipinge solo se serve
            // lo sfondo per la comparsa animata o se la lettura non riesce.
            if (!forcePage && _backdrop != null && _backdrop.Size == region.Size && Reveal >= 1 && TryCreateReflectedGlass())
            {
                _backdropRegion = region; _backdropParentSize = size;
                _backdropDirty = false; _lastBackdropCaptureTick = Environment.TickCount64;
                return;
            }
            if (_backdrop == null || _backdrop.Size != region.Size)
            { _backdrop?.Dispose(); _backdrop = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb); }
            _backdropRegion = region;
            _backdropParentSize = size;
            _backdropDirty = false;
            _lastBackdropCaptureTick = Environment.TickCount64;
            using (var graphics = Graphics.FromImage(_backdrop))
            {
                graphics.Clear(Parent.BackColor);
                graphics.TranslateTransform(-region.X, -region.Y);
                graphics.SetClip(region);
                using var behind = new PaintEventArgs(graphics, region);
                InvokePaintBackground(Parent, behind); InvokePaint(Parent, behind);
                graphics.Flush();
            }
            if (_card.Width <= 0 || _card.Height <= 0) PerformLayout();
            var local = new Rectangle(Bounds.X - region.X + _card.X, Bounds.Y - region.Y + _card.Y, _card.Width, _card.Height);
            // Stesso vetro della coda. Dietro la barra la pagina e' vuota (il contenuto si ferma
            // sopra), quindi il vetro sfocava una tinta unita: si usa il contenuto appena sopra la
            // barra, figli compresi (copertine, grafici), riflesso sotto di essa come se
            // continuasse a scorrere dietro il vetro.
            if (TryCreateReflectedGlass()) return;
            // Mai un'eccezione dal disegno: senza vetro la barra mostra comunque la pagina dietro.
            try { var glass = CreateBarGlass(_backdrop, local); _glass?.Dispose(); _glass = glass; _glassBounds = Bounds; }
            catch (Exception ex) { Dbg.Warn("Music bar glass failed: " + ex.Message); }
        }

        // Materiale della barra: vetro vero, non un pannello tinto. La velatura e' molto piu'
        // leggera di quella della coda, cosi' copertine e colori della pagina si vedono
        // passare sfocati dietro; i colori sono piu' saturi e le luci (nel tema scuro) o le
        // ombre (nel chiaro) vengono compresse perche' testo e icone restino sempre leggibili.
        private Bitmap CreateBarGlass(Bitmap source, Rectangle bar)
            => GlassSurface.CreateLiquid(source, bar, hairline: true);

        private bool TryCreateReflectedGlass()
        {
            Bitmap? reflected = null;
            try
            {
                reflected = BuildReflectedBackdrop(out var bar);
                if (reflected == null) return false;
                _glass?.Dispose(); _glass = CreateBarGlass(reflected, bar); _glassBounds = Bounds;
                return true;
            }
            catch { return false; }
            finally { reflected?.Dispose(); }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, int rop);

        /// <summary>Striscia della pagina sopra la barra letta dalla superficie della finestra
        /// (include i controlli figli) e, sotto, la stessa striscia specchiata alta quanto la
        /// barra. <paramref name="bar"/> e' il rettangolo della barra in questa bitmap.</summary>
        private Bitmap? BuildReflectedBackdrop(out Rectangle bar)
        {
            bar = Rectangle.Empty;
            var parent = Parent;
            if (parent == null || !parent.IsHandleCreated || Width <= 0 || Height <= 0) return null;
            if (FindForm() is { WindowState: FormWindowState.Minimized }) return null;
            int above = Math.Min(Top, Math.Max(Height * 2, D(160)));
            if (above < Math.Max(D(24), Height)) return null;
            using var strip = new Bitmap(Width, above, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(strip))
            {
                IntPtr dest = g.GetHdc(), source = GetDC(parent.Handle);
                try { if (source == IntPtr.Zero || !BitBlt(dest, 0, 0, Width, above, source, Left, Top - above, 0x00CC0020)) return null; }
                finally { if (source != IntPtr.Zero) ReleaseDC(parent.Handle, source); g.ReleaseHdc(dest); }
            }
            // Una superficie non disponibile si legge tutta nera: meglio il metodo classico.
            bool anyContent = false;
            for (int i = 0; i < 24 && !anyContent; i++)
            {
                var c = strip.GetPixel((int)((i * 7919L) % Width), (int)((i * 104729L) % above));
                anyContent = c.R + c.G + c.B > 9;
            }
            if (!anyContent) return null;
            var result = new Bitmap(Width, above + Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.DrawImageUnscaled(strip, 0, 0);
                // Specchio: la riga subito sopra la barra diventa la prima riga dietro di essa.
                g.DrawImage(strip, new[] { new Point(0, above + Height), new Point(Width, above + Height), new Point(0, above) },
                    new Rectangle(0, above - Height, Width, Height), GraphicsUnit.Pixel);
            }
            bar = new Rectangle(0, above, Width, Height);
            return result;
        }

        private void PaintContent(Graphics g)
        {
            g.SmoothingMode=SmoothingMode.AntiAlias;
            if(_cover!=null)
            {
                // Copertina con angoli appena arrotondati, come nella coda.
                var state=g.Save(); g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                using(var brush=new TextureBrush(_cover,WrapMode.Clamp)){ brush.TranslateTransform(_art.X,_art.Y); brush.ScaleTransform(_art.Width/(float)_cover.Width,_art.Height/(float)_cover.Height); using var shape=RoundedCard(_art,D(5)); g.FillPath(brush,shape); }
                g.Restore(state);
            }
            using var title=global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold",10f);using var normal=global::CinecorePlayer2025.AppFonts.Create("Segoe UI",9f);
            const TextFormatFlags flags=TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g,_title,title,_titleRect,Theme.Text,flags);TextRenderer.DrawText(g,_artist,normal,_artistRect,Theme.Muted,flags);
            double seekReveal = SeekReveal;
            if (seekReveal > 0)
            {
                Color timeColor = Color.FromArgb((int)(255 * seekReveal), Theme.Muted);
                TextRenderer.DrawText(g,Time(_position),normal,_elapsed,timeColor,flags|TextFormatFlags.Right);TextRenderer.DrawText(g,Time(_duration),normal,_end,timeColor,flags);
            }
            foreach(var entry in _targets)
            {
                string key=entry.Key;var bounds=entry.Value;
                if(key is "seek" or "volume")
                {
                    if (key=="seek" && seekReveal < 1)
                    {
                        // Prima della durata solo il binario; poi il riempimento cresce fino alla posizione.
                        int railY=bounds.Top+bounds.Height/2;
                        using var railPen=new Pen(Color.FromArgb(60,153,181,205),D(2));
                        g.DrawLine(railPen,bounds.Left,railY,bounds.Right,railY);
                        if (seekReveal <= 0) continue;
                        double target=_duration>0?Math.Clamp(_position/_duration,0,1):0;
                        int fx=bounds.Left+(int)(bounds.Width*target*seekReveal);
                        using var fillPen=new Pen(Color.FromArgb((int)(235*seekReveal),Theme.Accent),D(2));
                        g.DrawLine(fillPen,bounds.Left,railY,fx,railY);
                        continue;
                    }
                    double value=_drag==key?_dragValue:key=="seek"?(_duration>0?_position/_duration:0):_muted?0:_volume;
                    int y=bounds.Top+bounds.Height/2,x=bounds.Left+(int)(bounds.Width*Math.Clamp(value,0,1));
                    bool disabled=_bitstream;
                    if (disabled && key=="volume")
                    {
                        using var rail=new Pen(Color.FromArgb(48,153,181,205),D(2));
                        g.DrawLine(rail,bounds.Left,y,bounds.Right,y);
                        continue;
                    }
                    using var bg=new Pen(Color.FromArgb(disabled?40:70,Ink),Math.Max(2,D(3))){StartCap=LineCap.Round,EndCap=LineCap.Round};using var fg=new Pen(Color.FromArgb(disabled?85:240,Theme.Accent),Math.Max(2,D(3))){StartCap=LineCap.Round,EndCap=LineCap.Round};
                    g.DrawLine(bg,bounds.Left,y,bounds.Right,y);g.DrawLine(fg,bounds.Left,y,x,y);
                    int radius = key=="volume" || _hover==key || _drag==key ? D(5) : D(4);
                    using (var knob=new SolidBrush(disabled?Color.FromArgb(105,Theme.IsLight?Theme.Accent:Color.White):(Theme.IsLight?Theme.Accent:Color.White))) g.FillEllipse(knob,x-radius,y-radius,radius*2,radius*2);
                    continue;
                }
                string icon=key switch {"play"=>_playing?"pause":"play","mute"=>_muted?"mute":"volume","analysis"=>"wave","lyrics"=>"lyrics","repeat"=>"repeat","favorite"=>"star","stop"=>"stop",_=>key};
                // La libreria e' lo stato di riposo: evidenziarla (colore e puntino) sembrava un tasto attivo.
                bool selected=key switch {"shuffle"=>_shuffle,"repeat"=>_repeat,"favorite"=>_favorite,"analysis"=>_view==1,"lyrics"=>_view==2,_=>false};
                bool hot=_hover==key && !(key=="mute"&&_bitstream);
                Color color=key=="mute"&&_bitstream?Color.FromArgb(70,70,70):selected?Theme.Accent:Color.FromArgb(hot?255:215,Ink);
                int glyphSize=D(20);
                if(key=="play")
                {
                    // Pulsante principale: solo il simbolo, piu' grande, senza cerchio attorno.
                    color=Color.FromArgb(hot?255:235,Ink); glyphSize=D(26);
                }
                else if(hot)
                {
                    int d=Math.Min(bounds.Height,D(34));
                    using var halo=new SolidBrush(Color.FromArgb(Theme.IsLight?22:34,Ink));
                    g.FillEllipse(halo,bounds.X+(bounds.Width-d)/2,bounds.Y+(bounds.Height-d)/2,d,d);
                }
                // Nessun puntino sotto la modalita' attiva: basta il colore d'accento dell'icona.
                var glyph=new Rectangle(bounds.X+(bounds.Width-glyphSize)/2,bounds.Y+(bounds.Height-glyphSize)/2,glyphSize,glyphSize);
                string cacheKey=icon+"|"+glyph.Width+"|"+color.ToArgb();
                if(!_icons.TryGetValue(cacheKey,out var bitmap))
                {
                    bitmap=new Bitmap(glyph.Width,glyph.Height);using(var canvas=Graphics.FromImage(bitmap))DrawSymbol(canvas,new Rectangle(Point.Empty,bitmap.Size),icon,color);
                    _icons[cacheKey]=bitmap;
                }
                g.DrawImageUnscaled(bitmap,glyph.Location);
            }
            // Il riquadro di focus serve solo a chi naviga da tastiera, non dopo un clic.
            if(Focused&&ShowFocusCues&&_targets.Count>0)
            {
                var focused=_targets.ElementAt(Math.Clamp(_focusIndex,0,_targets.Count-1));
                if(!(_bitstream && focused.Key is "volume" or "mute"))ControlPaint.DrawFocusRectangle(g,focused.Value);
            }
        }
        private string Hit(Point point)=>_targets.FirstOrDefault(x=>x.Value.Contains(point)).Key??"";
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if(_drag!="")
            {
                _dragValue=ValueAt(e.X,_targets[_drag]);
                // Il volume segue il trascinamento (non solo il rilascio); la posizione invece
                // si applica al rilascio per non inondare il motore di seek.
                if(_drag=="volume") Volume?.Invoke((float)_dragValue);
                Invalidate();return;
            }
            string key=Hit(e.Location);if(key==_hover)return;_hover=key;Cursor=key==""||(_bitstream&&(key is "seek" or "volume" or "mute"))?Cursors.Default:Cursors.Hand;
            string T(string it,string en)=>_english?en:it;_tips.SetToolTip(this,key switch {"analysis"=>T("Grafici audio","Audio charts"),"lyrics"=>T("Testo","Lyrics"),"queue"=>T("Coda","Queue"),"more"=>T("Altre opzioni","More options"),"favorite"=>T("Preferito","Favourite"),"pip"=>"Mini player","stop"=>T("Interrompi riproduzione","Stop playback"),"shuffle"=>T("Riproduzione casuale","Shuffle"),"repeat"=>T("Ripeti","Repeat"),"previous"=>T("Precedente","Previous"),"next"=>T("Successivo","Next"),"play"=>_playing?T("Pausa","Pause"):T("Riproduci","Play"),"library"=>T("Libreria","Library"),"mute"=>_muted?T("Riattiva audio","Unmute"):T("Disattiva audio","Mute"),"seek" when _bitstream=>T("Ricerca non disponibile in bitstream","Seeking unavailable in bitstream"),"seek"=>T("Posizione","Position"),"volume" when _bitstream=>T("Volume non disponibile in bitstream","Volume unavailable in bitstream"),"volume"=>"Volume",_=>key});Invalidate();
        }
        private static double ValueAt(int x,Rectangle bounds)=>Math.Clamp((x-bounds.Left)/(double)Math.Max(1,bounds.Width),0,1);
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;Focus();string key=Hit(e.Location);_pressed=key;
            if(key is "seek" or "volume") {if(key=="volume"&&_bitstream||key=="seek"&&_duration<=0)return;_drag=key;_dragValue=ValueAt(e.X,_targets[key]);Capture=true;if(key=="volume")Volume?.Invoke((float)_dragValue);Invalidate();}
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
        protected override void Dispose(bool disposing){if(disposing){if(_backdropParent!=null){_backdropParent.Invalidated-=BackdropInvalidated;_backdropParent.SizeChanged-=BackdropResized;}_revealTimer.Dispose();_settleTimer?.Dispose();_glass?.Dispose();_backdrop?.Dispose();_hideLayer?.Dispose();_revealLayer?.Dispose();_cover?.Dispose();_tips.Dispose();foreach(var image in _icons.Values)image.Dispose();}base.Dispose(disposing);}
        // Colore di icone, disco del play e traccia di avanzamento: bianco sul vetro scuro,
        // testo scuro sul vetro chiaro (prima restavano bianche e sparivano nel tema chiaro).
        private static Color Ink => Theme.IsLight ? Theme.Text : Color.White;

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
                case "info":g.DrawEllipse(pen,3,3,18,18);Line(12,11,12,17);g.FillEllipse(fill,11,6.6f,2,2);break;
                case "person":g.DrawEllipse(pen,8,3,8,8);g.DrawArc(pen,4,13,16,14,200,140);break;
                case "review":using(var bubble=new GraphicsPath()){bubble.AddArc(3,4,6,6,180,90);bubble.AddArc(15,4,6,6,270,90);bubble.AddArc(15,12,6,6,0,90);bubble.AddLine(18,18,10,18);bubble.AddLine(10,18,6,22);bubble.AddLine(6,22,6,18);bubble.AddArc(3,12,6,6,90,90);bubble.CloseFigure();g.DrawPath(pen,bubble);}Line(8,9,16,9);Line(8,13,14,13);break;
                case "volume":case "mute":g.DrawPolygon(pen,new[]{new PointF(3,9),new PointF(7,9),new PointF(12,5),new PointF(12,19),new PointF(7,15),new PointF(3,15)});if(icon=="volume"){g.DrawArc(pen,11,5,10,14,-65,130);}else{Line(16,9,22,15);Line(22,9,16,15);}break;
            }
            g.Restore(state);
        }

    }
}

