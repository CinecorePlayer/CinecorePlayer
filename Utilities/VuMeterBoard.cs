#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025
{
    internal sealed partial class AudioMetersLiveCharts
    {
        private readonly VuMeterBoard _vuBoard = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };

        /// <summary>Sorgente dell'analisi quando non c'e' il Cinecore Audio Engine (es. bitstream):
        /// voci separate da '|', mostrate sotto i VU.</summary>
        internal static string? AnalysisSource { get; set; }

        /// <summary>
        /// Due VU meter analogici "veri", con sotto le letture (picco vero, clipping, loudness,
        /// dinamica, limiter) e i comandi di taratura, azzeramento ed esportazione.
        /// Balistica VU (IEC 60268-17): 99% del valore in 300 ms con ~1,5% di sovraelongazione,
        /// simulata come sistema del secondo ordine; la deflessione e' proporzionale alla
        /// tensione come negli strumenti reali (per questo la parte bassa della scala e' compressa).
        /// L'ingresso e' la media quadratica esatta di tutti i campioni fra due letture, non
        /// l'ultimo blocco: i blocchi saltati dall'interfaccia non fanno piu' scattare l'ago.
        /// Taratura: AES17 (0 VU = -18 dBFS) e' pensata per registrare, non per la musica finita:
        /// un master moderno sta 8-12 dB sopra e l'ago restava sempre nel rosso. In "auto" lo zero
        /// segue la loudness del brano (come il tecnico che ritara il banco a ogni pezzo).
        /// </summary>
        private sealed class VuMeterBoard : Control
        {
            private static readonly string[] Calibrations = { "auto", "-18", "-14", "-10" };
            private double _reference = -18, _referenceTarget = -18; // dBFS a 0 VU
            private readonly double[] _rms = new double[2];
            private bool _silent = true;
            private int _faceReference = int.MinValue;
            private static string Calibration => Audio.CinecoreAudioSettings.Current.VuCalibration is { } c && Array.IndexOf(Calibrations, c) >= 0 ? c : "auto";
            private static bool AutoCalibration => Calibration == "auto";
            private const double Zeta = 0.81, Omega = 21.0; // smorzamento e pulsazione dell'equipaggio mobile
            private const double Sweep = 48; // gradi per lato

            private readonly double[] _target = new double[2];
            private readonly double[] _position = new double[2];
            private readonly double[] _velocity = new double[2];
            private readonly long[] _peakLampUntil = new long[2];
            private readonly long[] _clipLampUntil = new long[2];
            private readonly Timer _timer = new() { Interval = 15 };
            private long _lastTick;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private double _lastEnergyL, _lastEnergyR, _lastEnergyFrames = -1;

            // Livelli in cache: quadrante (comune ai due canali), piastra e riflesso (per canale),
            // cornice con la finestra ritagliata in antialias, disegnata per ultima.
            private Bitmap? _faceCache, _bezelCache, _overlayL, _overlayR;
            private Size _cacheSize;

            // letture
            private double _maxTpL = double.NegativeInfinity, _maxTpR = double.NegativeInfinity;
            private long _tpClips, _spClips, _lastSpClips = -1;
            private double _lufsI = double.NaN, _lufsS = double.NaN, _plr = double.NaN;
            private readonly List<string> _log = new();
            private long _lastLogMs = -1000, _lastStripMs;
            private Rectangle _metersArea, _stripArea, _calButton, _resetButton, _exportButton;
            private Point _mouse = new(-1, -1);
            public bool English { get; set; }

            public VuMeterBoard()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                _timer.Tick += (_, _) => Step();
            }

            private string L(string it, string en) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(English ? en : it);

            protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); EnsureTimer(); }
            protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); EnsureTimer(); }
            protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); EnsureTimer(); }

            // Aggiunto a una pagina gia' visibile non riceve VisibleChanged: gli aghi restavano
            // fermi. Il timer parte anche all'arrivo delle misure e si ferma quando non si vede.
            private void EnsureTimer()
            {
                bool shown = IsHandleCreated && Visible && Parent != null;
                if (shown && !_timer.Enabled) { _lastTick = _clock.ElapsedMilliseconds; _timer.Start(); }
                else if (!shown && _timer.Enabled) _timer.Stop();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { _timer.Dispose(); DropCaches(); }
                base.Dispose(disposing);
            }

            private void DropCaches()
            {
                _faceCache?.Dispose(); _bezelCache?.Dispose(); _overlayL?.Dispose(); _overlayR?.Dispose();
                _faceCache = _bezelCache = _overlayL = _overlayR = null;
            }

            public void ResetTrack()
            {
                _maxTpL = _maxTpR = double.NegativeInfinity;
                _tpClips = _spClips = 0; _lastSpClips = -1;
                _lufsI = _lufsS = _plr = double.NaN;
                _lastEnergyFrames = -1;
                lock (_log) _log.Clear();
                Invalidate();
            }

            /// <summary>Ultime misure dell'analizzatore (energia, true peak, loudness).</summary>
            public void UpdateMetrics(LoopbackSampler.AudioMetrics m)
            {
                long now = _clock.ElapsedMilliseconds;
                if (!_timer.Enabled) EnsureTimer();
                // Media quadratica di tutto l'audio arrivato dall'ultima lettura.
                double frames = m.EnergyFrames - _lastEnergyFrames;
                if (_lastEnergyFrames >= 0 && frames > 0 && m.EnergyL >= _lastEnergyL && m.EnergyR >= _lastEnergyR)
                {
                    _rms[0] = Math.Sqrt((m.EnergyL - _lastEnergyL) / frames);
                    _rms[1] = Math.Sqrt((m.EnergyR - _lastEnergyR) / frames);
                }
                else if (frames != 0 || _lastEnergyFrames < 0)
                {
                    _rms[0] = m.RmsL; _rms[1] = m.RmsR; // primo blocco o analizzatore ripartito
                }
                _lastEnergyL = m.EnergyL; _lastEnergyR = m.EnergyR; _lastEnergyFrames = m.EnergyFrames;
                _silent = m.IsSilent;

                double tpL = Finite(m.DbTpL), tpR = Finite(m.DbTpR);
                if (tpL > _maxTpL) _maxTpL = tpL;
                if (tpR > _maxTpR) _maxTpR = tpR;
                // Spia PEAK: picco vero oltre -1 dBTP (margine per i convertitori e i codec lossy).
                if (tpL > -1) _peakLampUntil[0] = now + 1500;
                if (tpR > -1) _peakLampUntil[1] = now + 1500;
                if (_lastSpClips >= 0 && m.ClipSampleEvents > _lastSpClips)
                {
                    bool l = m.PeakL >= 0.9999f, r = m.PeakR >= 0.9999f;
                    if (l || !r) _clipLampUntil[0] = now + 2500;
                    if (r || !l) _clipLampUntil[1] = now + 2500;
                }
                _lastSpClips = m.ClipSampleEvents;
                _tpClips = m.ClipEvents; _spClips = m.ClipSampleEvents;
                _lufsI = m.LufsI; _lufsS = m.LufsS; _plr = m.PlrDb;
                // Media del brano circa a -3 VU: i passaggi forti arrivano a 0 / +1 come su un banco.
                double loud = double.IsFinite(m.LufsI) && m.LufsI > -70 ? m.LufsI : double.IsFinite(m.LufsS) && m.LufsS > -70 ? m.LufsS : double.NaN;
                if (!AutoCalibration) _referenceTarget = double.Parse(Calibration, CultureInfo.InvariantCulture);
                else if (double.IsFinite(loud)) _referenceTarget = Math.Clamp(Math.Round(loud + 6), -24, -4);

                // Le letture sotto gli strumenti si aggiornano 4 volte al secondo: leggibili e leggere.
                if (now - _lastStripMs >= 250 && !_stripArea.IsEmpty) { _lastStripMs = now; Invalidate(_stripArea); }

                // Registro per l'esportazione: 10 righe al secondo.
                if (now - _lastLogMs >= 100)
                {
                    _lastLogMs = now;
                    var engine = Audio.CinecoreAudioEngine.Active?.Meters;
                    string row = string.Join(",", new[]
                    {
                        (now / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),
                        F(Db(m.RmsL)), F(Db(m.RmsR)), F(Db(m.PeakL)), F(Db(m.PeakR)), F(tpL), F(tpR),
                        F(m.LufsM), F(m.LufsS), F(m.LufsI), F(m.Lra), F(m.PlrDb), F(m.PsrDb),
                        F(m.Correlation), F(m.CrestL_dB), F(m.CrestR_dB), F(m.WidthDb),
                        m.ClipEvents.ToString(CultureInfo.InvariantCulture), m.ClipSampleEvents.ToString(CultureInfo.InvariantCulture),
                        F(engine?.GainReductionDb ?? double.NaN), (engine?.SourceClipSamples ?? 0).ToString(CultureInfo.InvariantCulture)
                    });
                    lock (_log) { if (_log.Count < 200_000) _log.Add(row); }
                }
            }

            private static string F(double v) => double.IsFinite(v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "";
            private static double Finite(double v) => double.IsFinite(v) ? v : double.NegativeInfinity;
            private static double Db(double linear) => linear > 0 ? 20 * Math.Log10(linear) : double.NegativeInfinity;

            /// <summary>Deflessione 0..1 per un RMS lineare (1.0 = +3 VU, fondo scala).</summary>
            private double Deflection(double rmsLinear)
            {
                if (rmsLinear <= 0) return 0;
                double vu = 20 * Math.Log10(rmsLinear) + 3.01 - _reference; // AES17: sinusoide a fondo scala = 0 dBFS
                return Math.Clamp(Math.Pow(10, (vu - 3) / 20), 0, 1.08);         // tensione relativa; oltre il fondo scala sbatte
            }

            private static double DeflectionForVu(double vu) => Math.Pow(10, (vu - 3) / 20);

            private void Step()
            {
                long now = _clock.ElapsedMilliseconds;
                double dt = Math.Clamp((now - _lastTick) / 1000.0, 0, 0.1);
                _lastTick = now;
                // Integrazione a passi di 1 ms: stabile anche se un fotogramma tarda.
                int steps = Math.Max(1, (int)Math.Round(dt * 1000));
                double h = dt / steps;
                // La taratura automatica si sposta piano (6 dB/s): nessun salto dell'ago fra i brani.
                double maxMove = 6 * dt;
                _reference += Math.Clamp(_referenceTarget - _reference, -maxMove, maxMove);
                _target[0] = _silent ? 0 : Deflection(_rms[0]);
                _target[1] = _silent ? 0 : Deflection(_rms[1]);
                for (int c = 0; c < 2; c++)
                    for (int i = 0; i < steps; i++)
                    {
                        double a = Omega * Omega * (_target[c] - _position[c]) - 2 * Zeta * Omega * _velocity[c];
                        _velocity[c] += a * h;
                        _position[c] += _velocity[c] * h;
                        if (_position[c] < -0.02) { _position[c] = -0.02; _velocity[c] = 0; }     // fermo meccanico
                        if (_position[c] > 1.10) { _position[c] = 1.10; _velocity[c] = -_velocity[c] * 0.2; }
                    }
                // Si ridisegnano solo gli strumenti, non l'intera pagina.
                if (!_metersArea.IsEmpty) Invalidate(_metersArea); else Invalidate();
            }

            // ---------- layout ----------
            private float Ui => DeviceDpi / 96f * Math.Clamp(Math.Min(Width / (1250f * DeviceDpi / 96f), Height / (640f * DeviceDpi / 96f)), 1f, 1.3f);
            private int P(double v) => (int)Math.Round(v * Ui);

            private (Rectangle Left, Rectangle Right, Rectangle Strip, Rectangle Controls) MeterLayout()
            {
                int pad = Math.Max(P(24), Width / 50);
                int controlsH = P(54), stripH = P(58);
                var controls = new Rectangle(pad, Height - pad - controlsH, Math.Max(1, Width - pad * 2), controlsH);
                int metersBottom = controls.Top - P(22) - stripH - P(26);
                var area = new Rectangle(pad, pad, Math.Max(1, Width - pad * 2), Math.Max(40, metersBottom - pad));
                int gap = Math.Max(P(24), area.Width / 36);
                int w = Math.Max(1, Math.Min((area.Width - gap) / 2, (int)(area.Height * 1.62)));
                int h = (int)Math.Round(w / 1.62);
                int x = area.Left + (area.Width - (w * 2 + gap)) / 2;
                int y = area.Top + (area.Height - h) / 2;
                var strip = new Rectangle(x, y + h + P(26), w * 2 + gap, stripH);
                // Largo come la barra di scelta del grafico sopra la pagina (tutta la larghezza).
                controls = new Rectangle(0, strip.Bottom + P(22), Math.Max(1, Width - 1), controlsH);
                return (new Rectangle(x, y, w, h), new Rectangle(x + w + gap, y, w, h), strip, controls);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(HUD.Theme.Panel);
                if (Width < 200 || Height < 160) { _metersArea = _stripArea = Rectangle.Empty; return; }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                var (left, right, strip, controls) = MeterLayout();
                _metersArea = Rectangle.Union(left, right);
                _stripArea = strip;
                long now = _clock.ElapsedMilliseconds;
                EnsureCaches(left.Size);
                if (e.ClipRectangle.IntersectsWith(left)) DrawMeter(g, left, 0, now);
                if (e.ClipRectangle.IntersectsWith(right)) DrawMeter(g, right, 1, now);
                if (e.ClipRectangle.IntersectsWith(strip)) DrawReadings(g, strip);
                if (e.ClipRectangle.IntersectsWith(controls)) DrawControls(g, controls);
            }

            // ---------- disegno dello strumento ----------
            // Struttura di un VU da pannello (Sifam/Weston): cornice nera opaca, quadrante di
            // carta crema retroilluminato dal basso, doppia scala (VU sopra, % di modulazione
            // sotto), zona rossa stampata come settore pieno, ago sottile che esce da sotto una
            // piastra ad arco con vite di azzeramento, due LED sotto il vetro.
            // Nessun contorno tracciato: bordi e smussi sono riempimenti fra due tracciati, cosi'
            // restano continui e uniformi con l'antialias; niente ritagli GDI+ (senza antialias)
            // sulle curve: la cornice copre il quadrante con la finestra gia' ritagliata.

            private static Font MeterFont(string family, float px)
            {
                try { using var probe = new FontFamily(family); return new Font(probe, px, FontStyle.Regular, GraphicsUnit.Pixel); }
                catch { return new Font("Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel); }
            }

            private static readonly Color Ink = Color.FromArgb(38, 31, 24), RedInk = Color.FromArgb(190, 36, 28), RedDeep = Color.FromArgb(140, 22, 18);

            private static (RectangleF Face, float S) FaceOf(Size size)
            {
                float s = size.Width / 400f;
                float inset = 9 * s;
                return (new RectangleF(inset, inset, size.Width - inset * 2, size.Height - inset * 2), s);
            }

            private static float FaceRadius(float s) => 5 * s;
            private static float BezelRadius(float s) => 12 * s;

            private static (float Cx, float Cy, float Radius) Pivot(RectangleF face)
                => (face.Left + face.Width / 2f, face.Bottom + face.Height * .16f, face.Height * .86f);

            /// <summary>Raggio della piastra che copre il perno (l'ago esce da qui).</summary>
            private static float ShieldRadius(RectangleF face) => face.Height * .40f;

            private static double Angle(double deflection) => (-Sweep + deflection * 2 * Sweep) * Math.PI / 180;

            private void EnsureCaches(Size size)
            {
                int faceRef = (int)Math.Round(_reference);
                if (_cacheSize != size) { DropCaches(); _cacheSize = size; }
                if (_faceCache == null || _faceReference != faceRef)
                {
                    _faceReference = faceRef;
                    _faceCache?.Dispose();
                    _faceCache = NewLayer(size);
                    using (var fg = Graphics.FromImage(_faceCache)) DrawFace(fg, size);
                    VisualDither.AddNoise(_faceCache, 2); // grana della carta e niente gradini
                }
                if (_bezelCache == null)
                {
                    _bezelCache = NewLayer(size);
                    using (var bg = Graphics.FromImage(_bezelCache)) DrawBezel(bg, size);
                    VisualDither.AddNoise(_bezelCache, 1);
                }
                if (_overlayL == null) { _overlayL = NewLayer(size); using var og = Graphics.FromImage(_overlayL); DrawOverlay(og, size, "L"); }
                if (_overlayR == null) { _overlayR = NewLayer(size); using var og = Graphics.FromImage(_overlayR); DrawOverlay(og, size, "R"); }
            }

            private static Bitmap NewLayer(Size size) => new(Math.Max(1, size.Width), Math.Max(1, size.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

            private static void Prepare(Graphics g)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.CompositingQuality = CompositingQuality.HighQuality;
            }

            /// <summary>Anello pieno fra due tracciati (bordo esterno e interno con antialias).</summary>
            private static void FillRing(Graphics g, Brush brush, GraphicsPath outer, GraphicsPath inner)
            {
                using var ring = (GraphicsPath)outer.Clone();
                ring.AddPath(inner, false);
                ring.FillMode = FillMode.Alternate;
                g.FillPath(brush, ring);
            }

            /// <summary>Cornice con la finestra del quadrante: un anello pieno, piu' lo smusso
            /// chiaro sul bordo esterno e l'ombra sul bordo della finestra, tutti riempimenti.</summary>
            private static void DrawBezel(Graphics g, Size size)
            {
                Prepare(g);
                var (face, s) = FaceOf(size);
                var outer = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
                float r = BezelRadius(s), fr = FaceRadius(s);
                using var outerPath = Rounded(outer, r);
                using var window = Rounded(face, fr);
                // Cornice minimale, nel linguaggio delle schede del programma: tinta piatta appena
                // piu' chiara della pagina, nessun filo di luce (si leggeva come secondo contorno).
                Color baseColor = HUD.Theme.IsLight ? Color.FromArgb(38, 40, 44) : Color.FromArgb(26, 29, 34);
                using (var fill = new SolidBrush(baseColor))
                    FillRing(g, fill, outerPath, window);
                // Battuta della finestra: il quadrante e' incassato.
                using (var lipOuter = Rounded(RectangleF.Inflate(face, 2f * s, 2f * s), fr + 2f * s))
                using (var lip = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    FillRing(g, lip, lipOuter, window);
            }

            private void DrawFace(Graphics g, Size size)
            {
                Prepare(g);
                var (face, s) = FaceOf(size);
                // Il quadrante sborda sotto la cornice: nessuna fessura lungo la finestra.
                var paperRect = RectangleF.Inflate(face, 4 * s, 4 * s);
                using (var paper = Vertical(paperRect, 0f, 1f, Color.FromArgb(246, 236, 206), Color.FromArgb(238, 220, 172)))
                    g.FillRectangle(paper, paperRect);
                // Retroilluminazione: due lampadine dietro il quadrante, in basso. Limitata alla
                // carta: fuori sborderebbe sotto gli angoli arrotondati della cornice.
                g.SetClip(paperRect);
                foreach (float fx in new[] { .28f, .72f })
                    using (var glowPath = new GraphicsPath())
                    {
                        glowPath.AddEllipse(face.Left + face.Width * (fx - .42f), face.Top + face.Height * .18f, face.Width * .84f, face.Height * 1.25f);
                        using var glow = new PathGradientBrush(glowPath) { CenterColor = Color.FromArgb(70, 255, 232, 176), SurroundColors = new[] { Color.FromArgb(0, 255, 232, 176) } };
                        g.FillPath(glow, glowPath);
                    }
                g.ResetClip();
                // Ombra interna della cornice su tutto il bordo della finestra (il quadrante e'
                // incassato di qualche millimetro): anelli pieni sempre piu' tenui.
                using (var window = Rounded(face, FaceRadius(s)))
                {
                    const int layers = 14;
                    GraphicsPath previous = (GraphicsPath)window.Clone();
                    for (int i = 1; i <= layers; i++)
                    {
                        float d = i * 0.75f * s;
                        var next = Rounded(RectangleF.Inflate(face, -d, -d), Math.Max(1, FaceRadius(s) - d * .5f));
                        double k = 1 - (i - 1) / (double)layers;
                        using (var shade = new SolidBrush(Color.FromArgb((int)(64 * k * k), 60, 40, 12)))
                            FillRing(g, shade, previous, next);
                        previous.Dispose(); previous = next;
                    }
                    previous.Dispose();
                }
                // In alto la cornice fa piu' ombra (luce ambiente dall'alto).
                using (var shade = Vertical(paperRect, (face.Top - paperRect.Top) / paperRect.Height, (face.Top + 22 * s - paperRect.Top) / paperRect.Height, Color.FromArgb(60, 50, 32, 8), Color.FromArgb(0, 50, 32, 8)))
                    g.FillRectangle(shade, paperRect);

                var (cx, cy, radius) = Pivot(face);
                PointF At(double deflection, double r) { double a = Angle(deflection); return new PointF((float)(cx + Math.Sin(a) * r), (float)(cy - Math.Cos(a) * r)); }
                float Deg(double deflection) => (float)(Angle(deflection) * 180 / Math.PI - 90);
                double zeroD = DeflectionForVu(0);

                float rArc = radius;
                // Zona rossa: settore pieno da 0 a +3 VU appoggiato all'arco della scala, con i
                // lati radiali allineati alle tacche (prima era un tratto spesso con le estremita' storte).
                float band = 5f * s;
                using (var zone = new GraphicsPath())
                {
                    float a0 = Deg(zeroD), a1 = Deg(1.0);
                    // Il bordo interno coincide con il centro della linea della scala: niente
                    // scalino fra il tratto nero e la fascia (sembrava una linea interrotta).
                    float inner = rArc - 0.55f * s;
                    zone.AddArc(cx - (rArc + band), cy - (rArc + band), (rArc + band) * 2, (rArc + band) * 2, a0, a1 - a0);
                    zone.AddArc(cx - inner, cy - inner, inner * 2, inner * 2, a1, a0 - a1);
                    zone.CloseFigure();
                    using var red = new SolidBrush(RedInk);
                    g.FillPath(red, zone);
                }
                // Arco della scala: nero fino a 0 VU, sottile.
                using (var arc = new Pen(Ink, 1.1f * s))
                    g.DrawArc(arc, new RectangleF(cx - rArc, cy - rArc, rArc * 2, rArc * 2), Deg(0), Deg(zeroD) - Deg(0));
                // La stessa linea prosegue sotto la fascia rossa fino a +3, in rosso scuro.
                using (var redArc = new Pen(RedDeep, 1.1f * s))
                    g.DrawArc(redArc, new RectangleF(cx - rArc, cy - rArc, rArc * 2, rArc * 2), Deg(zeroD), Deg(1.0) - Deg(zeroD));
                float rPct = rArc - 32 * s;
                using (var pctArc = new Pen(Color.FromArgb(150, Ink), 0.8f * s))
                    g.DrawArc(pctArc, new RectangleF(cx - rPct, cy - rPct, rPct * 2, rPct * 2), Deg(0), Deg(zeroD) - Deg(0));

                using var major = MeterFont("Bahnschrift SemiBold SemiConden", 11.5f * s);
                using var minor = MeterFont("Bahnschrift SemiCondensed", 7.6f * s);
                using var ink = new SolidBrush(Ink);
                using var redInk = new SolidBrush(RedInk);
                using var tick = new Pen(Ink, 1.1f * s);
                using var fine = new Pen(Ink, 0.75f * s);
                using var redTick = new Pen(RedDeep, 1.1f * s);
                using var redFine = new Pen(RedDeep, 0.8f * s);
                using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                // Scala VU: tacche maggiori con numero, intermedie senza. Nella zona rossa le tacche
                // sono piu' scure e sporgono oltre la fascia, come sui quadranti stampati.
                foreach (double vu in new double[] { -20, -10, -7, -5, -3, -2, -1, 0, 1, 2, 3 })
                {
                    double d = DeflectionForVu(vu); bool hot = vu > 0;
                    g.DrawLine(hot ? redTick : tick, At(d, rArc), At(d, rArc + 9.5f * s));
                    string label = vu > 0 ? "+" + vu : vu == 0 ? "0" : Math.Abs(vu).ToString(CultureInfo.InvariantCulture);
                    var p = At(d, rArc + 19 * s);
                    g.DrawString(label, major, hot ? redInk : ink, new RectangleF(p.X - 22 * s, p.Y - 9 * s, 44 * s, 18 * s), centered);
                }
                foreach (double vu in new double[] { -15, -9, -8, -6, -4, -2.5, -1.5, -0.5, 0.5, 1.5, 2.5 })
                {
                    double d = DeflectionForVu(vu);
                    g.DrawLine(vu > 0 ? redFine : fine, At(d, rArc), At(d, rArc + (vu > 0 ? 7f : 5f) * s));
                }
                // Segni meno e piu' agli estremi della scala, come sugli strumenti veri.
                using (var sign = MeterFont("Bahnschrift SemiBold", 14f * s))
                {
                    var minus = At(0.03, rArc - 13 * s); var plus = At(0.985, rArc - 14 * s);
                    g.DrawString("−", sign, ink, new RectangleF(minus.X - 14 * s, minus.Y - 12 * s, 28 * s, 24 * s), centered);
                    g.DrawString("+", sign, redInk, new RectangleF(plus.X - 14 * s, plus.Y - 12 * s, 28 * s, 24 * s), centered);
                }
                // Scala percentuale (100% = 0 VU) sull'arco interno.
                for (int pct = 0; pct <= 100; pct += 10)
                {
                    double d = zeroD * pct / 100.0; bool labelled = pct % 20 == 0;
                    g.DrawLine(fine, At(d, rPct), At(d, rPct + (labelled ? 5.5f : 3.2f) * s));
                    if (!labelled) continue;
                    var p = At(d, rPct - 8 * s);
                    g.DrawString(pct.ToString(CultureInfo.InvariantCulture), minor, ink, new RectangleF(p.X - 16 * s, p.Y - 6 * s, 32 * s, 12 * s), centered);
                }

                // "VU" e taratura stampata piccola sotto, come la dicitura del costruttore.
                using (var vuFont = MeterFont("Bahnschrift SemiBold", 22f * s))
                    g.DrawString("VU", vuFont, ink, new RectangleF(cx - 60 * s, cy - radius + 56 * s, 120 * s, 28 * s), centered);
                using (var brand = MeterFont("Bahnschrift SemiCondensed", 6.8f * s))
                using (var brandInk = new SolidBrush(Color.FromArgb(130, 70, 50, 25)))
                {
                    string refText = "0 VU = " + FormatDb(_faceReference) + " dBFS" + (AutoCalibration ? "  ·  AUTO" : "");
                    g.DrawString(refText, brand, brandInk, new RectangleF(face.Left, cy - radius + 82 * s, face.Width, 10 * s), centered);
                }
            }

            /// <summary>Piastra del perno, vite di azzeramento, nome del canale e riflesso del vetro:
            /// stanno sopra l'ago, quindi vanno in un livello separato.</summary>
            private static void DrawOverlay(Graphics g, Size size, string name)
            {
                Prepare(g);
                var (face, s) = FaceOf(size);
                var (cx, cy, _) = Pivot(face);
                float shieldR = ShieldRadius(face);
                // Ritaglio rettangolare: gli angoli li copre la cornice.
                g.SetClip(RectangleF.Inflate(face, 2 * s, 2 * s));

                // Ombra morbida della piastra sul quadrante: anelli pieni concentrici, niente tratti.
                var shieldBox = new RectangleF(cx - shieldR, cy - shieldR, shieldR * 2, shieldR * 2);
                for (int i = 6; i >= 1; i--)
                {
                    using var halo = new SolidBrush(Color.FromArgb(9, 40, 24, 0));
                    g.FillEllipse(halo, RectangleF.Inflate(shieldBox, i * 1.1f * s, i * 1.1f * s));
                }
                using (var fill = Vertical(shieldBox, 0f, .4f, Color.FromArgb(52, 50, 48), Color.FromArgb(20, 19, 18)))
                    g.FillEllipse(fill, shieldBox);
                // Filo di luce sul bordo della piastra: anello sottile che sfuma verso i lati.
                using (var rimOuter = new GraphicsPath())
                using (var rimInner = new GraphicsPath())
                using (var light = Vertical(shieldBox, 0f, .22f, Color.FromArgb(80, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
                {
                    rimOuter.AddEllipse(shieldBox);
                    rimInner.AddEllipse(RectangleF.Inflate(shieldBox, -1.1f * s, -1.1f * s));
                    FillRing(g, light, rimOuter, rimInner);
                }
                // Vite di azzeramento con taglio.
                float screwR = 5.5f * s;
                var screw = new RectangleF(cx - screwR, face.Bottom - 12 * s - screwR, screwR * 2, screwR * 2);
                using (var screwFill = new LinearGradientBrush(RectangleF.Inflate(screw, 1, 1), Color.FromArgb(150, 146, 140), Color.FromArgb(62, 60, 57), LinearGradientMode.ForwardDiagonal))
                    g.FillEllipse(screwFill, screw);
                using (var slot = new Pen(Color.FromArgb(200, 25, 22, 20), 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(slot, screw.Left + screwR * .4f, screw.Top + screwR * 1.3f, screw.Right - screwR * .4f, screw.Top + screwR * .7f);
                // Nome del canale serigrafato sulla piastra.
                using (var nameFont = MeterFont("Bahnschrift SemiBold", 10.5f * s))
                using (var nameInk = new SolidBrush(Color.FromArgb(200, 232, 222, 196)))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(name, nameFont, nameInk, new RectangleF(cx - 40 * s, cy - shieldR + 8 * s, 80 * s, 16 * s), fmt);

                // Vetro: riflesso diagonale ampio e morbido, piu' un secondo riflesso stretto.
                using (var glassPath = new GraphicsPath())
                {
                    glassPath.AddBezier(face.Left - 2 * s, face.Top + face.Height * .46f, face.Left + face.Width * .35f, face.Top + face.Height * .30f, face.Left + face.Width * .65f, face.Top + face.Height * .18f, face.Right + 2 * s, face.Top + face.Height * .12f);
                    glassPath.AddLine(face.Right + 2 * s, face.Top + face.Height * .12f, face.Right + 2 * s, face.Top - 2 * s);
                    glassPath.AddLine(face.Right + 2 * s, face.Top - 2 * s, face.Left - 2 * s, face.Top - 2 * s);
                    glassPath.CloseFigure();
                    using var glass = Vertical(RectangleF.Inflate(face, 2 * s, 2 * s), 0f, .46f, Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
                    g.FillPath(glass, glassPath);
                }
                using (var streak = new GraphicsPath())
                {
                    float x0 = face.Left + face.Width * .70f;
                    streak.AddPolygon(new[] { new PointF(x0, face.Top - 2 * s), new PointF(x0 + 16 * s, face.Top - 2 * s), new PointF(x0 - 30 * s, face.Bottom + 2 * s), new PointF(x0 - 38 * s, face.Bottom + 2 * s) });
                    using var soft = Vertical(RectangleF.Inflate(face, 2 * s, 2 * s), 0f, 1f, Color.FromArgb(22, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
                    g.FillPath(soft, streak);
                }
                g.ResetClip();
            }

            private void DrawMeter(Graphics g, Rectangle r, int channel, long now)
            {
                g.DrawImageUnscaled(_faceCache!, r.Location);

                var (faceLocal, s) = FaceOf(r.Size);
                var face = new RectangleF(faceLocal.X + r.X, faceLocal.Y + r.Y, faceLocal.Width, faceLocal.Height);
                var (cx, cy, radius) = Pivot(face);

                // LED sotto il vetro: disegnati prima dell'ago e del riflesso.
                DrawLed(g, new PointF(face.Left + 24 * s, face.Top + 22 * s), 4.2f * s, now < _clipLampUntil[channel], Color.FromArgb(255, 40, 30), "CLIP", s);
                DrawLed(g, new PointF(face.Right - 24 * s, face.Top + 22 * s), 4.2f * s, now < _peakLampUntil[channel], Color.FromArgb(255, 168, 30), "PEAK", s);

                double angle = Angle(Math.Clamp(_position[channel], -0.03, 1.1));
                double sin = Math.Sin(angle), cos = Math.Cos(angle);
                PointF Along(double dist, double side) => new((float)(cx + sin * dist + cos * side), (float)(cy - cos * dist + sin * side));
                float tipLen = radius + 8 * s, baseLen = ShieldRadius(face) - 4 * s;
                // Ombra dell'ago sul quadrante: luce dall'alto a sinistra, morbida (due passate).
                for (int i = 0; i < 2; i++)
                {
                    using var shadow = new Pen(Color.FromArgb(i == 0 ? 18 : 26, 70, 45, 10), (i == 0 ? 4.2f : 2.2f) * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    var a = Along(baseLen, 0); var b = Along(tipLen - 2 * s, 0);
                    g.DrawLine(shadow, a.X + 3.5f * s, a.Y + 2.5f * s, b.X + 3.5f * s, b.Y + 2.5f * s);
                }
                // Ago rastremato: piu' largo alla base, sottilissimo in punta.
                using (var needle = new GraphicsPath())
                {
                    needle.AddPolygon(new[] { Along(baseLen, -1.2 * s), Along(tipLen - 1, -0.28 * s), Along(tipLen, 0), Along(tipLen - 1, 0.28 * s), Along(baseLen, 1.2 * s) });
                    using var ink = new SolidBrush(Color.FromArgb(20, 17, 14));
                    g.FillPath(ink, needle);
                }
                g.DrawImageUnscaled(channel == 0 ? _overlayL! : _overlayR!, r.Location);
                g.DrawImageUnscaled(_bezelCache!, r.Location);
            }

            /// <summary>LED da 3 mm incassato nel quadrante: foro in ombra, lente colorata
            /// traslucida con riflesso; acceso diffonde luce sulla carta attorno.</summary>
            private static void DrawLed(Graphics g, PointF center, float radius, bool lit, Color color, string label, float s)
            {
                var lens = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);
                if (lit)
                {
                    using var bloomPath = new GraphicsPath();
                    bloomPath.AddEllipse(RectangleF.Inflate(lens, radius * 3.2f, radius * 3.2f));
                    using var bloom = new PathGradientBrush(bloomPath) { CenterColor = Color.FromArgb(120, color), SurroundColors = new[] { Color.FromArgb(0, color) } };
                    g.FillPath(bloom, bloomPath);
                }
                // Foro nel quadrante: ombra morbida attorno alla lente.
                using (var holePath = new GraphicsPath())
                {
                    holePath.AddEllipse(RectangleF.Inflate(lens, 1.8f * s, 1.8f * s));
                    using var hole = new PathGradientBrush(holePath) { CenterColor = Color.FromArgb(150, 40, 26, 12), SurroundColors = new[] { Color.FromArgb(0, 40, 26, 12) }, FocusScales = new PointF(.7f, .7f) };
                    g.FillPath(hole, holePath);
                }
                using (var lensPath = new GraphicsPath())
                {
                    lensPath.AddEllipse(lens);
                    Color Scale(Color c, float k) => Color.FromArgb(255, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
                    using var body = new PathGradientBrush(lensPath)
                    {
                        CenterPoint = new PointF(center.X, center.Y + radius * .15f),
                        CenterColor = lit ? Color.FromArgb(255, 255, Math.Min(255, color.G + 150), Math.Min(255, color.B + 120)) : Scale(color, .42f),
                        SurroundColors = new[] { lit ? color : Scale(color, .22f) }
                    };
                    g.FillPath(body, lensPath);
                }
                // Riflesso della lente a cupola.
                using (var shinePath = new GraphicsPath())
                {
                    var spot = new RectangleF(center.X - radius * .62f, center.Y - radius * .72f, radius * .8f, radius * .6f);
                    shinePath.AddEllipse(spot);
                    using var shine = new PathGradientBrush(shinePath) { CenterColor = Color.FromArgb(lit ? 200 : 120, 255, 255, 255), SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) } };
                    g.FillPath(shine, shinePath);
                }
                using var font = MeterFont("Bahnschrift SemiBold SemiConden", 6.8f * s);
                using var ink = new SolidBrush(Color.FromArgb(150, 60, 42, 22));
                using var fmt = new StringFormat { Alignment = StringAlignment.Center };
                var hint = g.TextRenderingHint; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(label, font, ink, new RectangleF(center.X - 30 * s, center.Y + radius + 3.5f * s, 60 * s, 12 * s), fmt);
                g.TextRenderingHint = hint;
            }

            // ---------- letture e comandi ----------
            private static string FormatDb(double v) => (v < 0 ? "−" : v > 0 ? "+" : "") + Math.Abs(v).ToString("0", CultureInfo.InvariantCulture);
            private static string Signed(double v, string format) => (v < 0 ? "−" : "") + Math.Abs(v).ToString(format, CultureInfo.CurrentCulture);
            private static string Tp(double v) => double.IsFinite(v) && v > -99 ? Signed(v, "0.0") : "—";

            /// <summary>Riga delle letture: per ogni voce solo etichetta e valore con unita', centrati
            /// sotto gli strumenti. Nessun riquadro, nessuna riga di dettaglio.</summary>
            private void DrawReadings(Graphics g, Rectangle strip)
            {
                Color text = HUD.Theme.Text, muted = HUD.Theme.Muted;
                Color warn = Color.FromArgb(230, 150, 40), bad = Color.FromArgb(236, 84, 74);
                var em = Audio.CinecoreAudioEngine.Active?.Meters;
                double tpMax = Math.Max(_maxTpL, _maxTpR);
                var cells = new List<(string Label, string Value, string Unit, Color? Accent)>
                {
                    (L("Picco vero", "True peak"), Tp(tpMax), "dBTP", tpMax > -1 ? warn : null),
                    (L("Clipping", "Clipping"), _spClips.ToString("N0", CultureInfo.CurrentCulture), _spClips == 1 ? L("campione", "sample") : L("campioni", "samples"), _spClips > 0 ? bad : null),
                    (L("Loudness", "Loudness"), double.IsFinite(_lufsI) && _lufsI > -70 ? Signed(_lufsI, "0.0") : "—", "LUFS", null),
                    (L("Dinamica", "Dynamics"), double.IsFinite(_plr) && _plr > 0 && _plr < 60 ? _plr.ToString("0.0", CultureInfo.CurrentCulture) : "—", "dB PLR", null),
                };
                if (em != null)
                {
                    bool limiting = em.GainReductionDb < -0.05;
                    cells.Add((L("Limiter", "Limiter"), limiting ? Signed(em.GainReductionDb, "0.0") : "0", "dB", limiting ? HUD.Theme.Accent : null));
                    cells.Add((L("File sorgente", "Source file"), em.SourceClipSamples.ToString("N0", CultureInfo.CurrentCulture), L("saturati", "clipped"), em.SourceClipSamples > 0 ? warn : null));
                }

                using var labelFont = AppFonts.Create("Segoe UI", 8.8f * Ui / (DeviceDpi / 96f));
                using var valueFont = AppFonts.Create("Segoe UI Semibold", 17f * Ui / (DeviceDpi / 96f));
                using var unitFont = AppFonts.Create("Segoe UI", 9.5f * Ui / (DeviceDpi / 96f));
                const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Top | TextFormatFlags.Left;
                int valueBaseline = strip.Top + P(20) + Ascent(g, valueFont);
                int n = cells.Count;
                using var divider = new Pen(Color.FromArgb(HUD.Theme.IsLight ? 40 : 34, text));
                for (int i = 0; i < n; i++)
                {
                    var (label, value, unit, accent) = cells[i];
                    int left = strip.Left + i * strip.Width / n, right = strip.Left + (i + 1) * strip.Width / n, w = right - left;
                    if (i > 0) g.DrawLine(divider, left, strip.Top + P(4), left, strip.Bottom - P(4));
                    var labelSize = TextRenderer.MeasureText(g, label, labelFont, Size.Empty, Flags);
                    TextRenderer.DrawText(g, label, labelFont, new Point(left + (w - labelSize.Width) / 2, strip.Top), muted, Flags);
                    // Valore e unita' sulla stessa linea di base, centrati come gruppo.
                    int vw = TextRenderer.MeasureText(g, value, valueFont, Size.Empty, Flags).Width;
                    int uw = TextRenderer.MeasureText(g, unit, unitFont, Size.Empty, Flags).Width;
                    int gap = P(5), total = vw + gap + uw;
                    int x = left + Math.Max(0, (w - total) / 2);
                    TextRenderer.DrawText(g, value, valueFont, new Point(x, valueBaseline - Ascent(g, valueFont)), accent ?? text, Flags);
                    TextRenderer.DrawText(g, unit, unitFont, new Point(x + vw + gap, valueBaseline - Ascent(g, unitFont)), muted, Flags);
                }
            }

            /// <summary>Distanza fra la cima della cella del testo e la linea di base, in pixel.</summary>
            private static int Ascent(Graphics g, Font font)
            {
                var family = font.FontFamily;
                float px = font.SizeInPoints * g.DpiY / 72f;
                return (int)Math.Round(px * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style));
            }

            /// <summary>Riquadro orizzontale largo quanto i due strumenti: catena del segnale a
            /// sinistra, i tre comandi a destra, centrati in verticale.</summary>
            private void DrawControls(Graphics g, Rectangle row)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // Stesso riquadro della barra di scelta del grafico: stesso fondo, bordo e raggio.
                using (var shape = Rounded(new RectangleF(row.X, row.Y, row.Width, row.Height), 10))
                using (var fill = new SolidBrush(HUD.Theme.IsLight ? HUD.Theme.Panel : Color.FromArgb(10, 22, 31)))
                using (var border = new Pen(Color.FromArgb(80, HUD.Theme.Border)))
                {
                    g.FillPath(fill, shape);
                    g.DrawPath(border, shape);
                }
                var engine = Audio.CinecoreAudioEngine.Active;
                string chain = engine != null
                    ? "Cinecore Audio  \u00b7  " + engine.SourceDescription + "  \u2192  " + engine.OutputDescription
                    : AnalysisSource is { Length: > 0 } src ? string.Join("  \u00b7  ", src.Split('|'))
                    : L("Uscita di sistema  \u00b7  cattura loopback", "System output  \u00b7  loopback capture");
                string cal = (AutoCalibration ? L("Taratura auto", "Auto calibration") : L("Taratura", "Calibration")) + $"  {FormatDb(Math.Round(_reference))} dBFS";
                string reset = L("Azzera picchi", "Reset peaks"), export = L("Esporta dati\u2026", "Export data\u2026");
                using var font = AppFonts.Create("Segoe UI Semibold", 9f * Ui / (DeviceDpi / 96f));
                // Area cliccabile = testo + 12 px per lato; il testo dell'ultimo comando sta a 20 px
                // dal bordo destro, come la catena del segnale da quello sinistro.
                int Pill(string t) => TextRenderer.MeasureText(g, t, font, Size.Empty, TextFormatFlags.NoPadding).Width + P(24);
                int gap = P(12), inner = P(8), bh = P(34), by = row.Top + (row.Height - bh) / 2;
                _exportButton = new Rectangle(row.Right - inner - Pill(export), by, Pill(export), bh);
                _resetButton = new Rectangle(_exportButton.Left - gap - Pill(reset), by, Pill(reset), bh);
                _calButton = new Rectangle(_resetButton.Left - gap - Pill(cal), by, Pill(cal), bh);
                using (var chainFont = AppFonts.Create("Segoe UI", 9.5f * Ui / (DeviceDpi / 96f)))
                    TextRenderer.DrawText(g, chain, chainFont, new Rectangle(row.Left + P(20), row.Top, Math.Max(1, _calButton.Left - row.Left - P(36)), row.Height), HUD.Theme.Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                DrawAction(g, _calButton, cal, font);
                DrawAction(g, _resetButton, reset, font);
                DrawAction(g, _exportButton, export, font);
            }

            /// <summary>Comando come testo semplice (niente pillola): accento al passaggio.</summary>
            private void DrawAction(Graphics g, Rectangle r, string text, Font font)
            {
                bool hover = r.Contains(_mouse);
                TextRenderer.DrawText(g, text, font, r, hover ? HUD.Theme.Accent : HUD.Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                bool wasHot = Hot(_mouse);
                _mouse = e.Location;
                bool hot = Hot(e.Location);
                Cursor = hot ? Cursors.Hand : Cursors.Default;
                if (hot || wasHot) Invalidate(Rectangle.Union(_calButton, _exportButton));
            }

            private bool Hot(Point p) => _resetButton.Contains(p) || _exportButton.Contains(p) || _calButton.Contains(p);

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _mouse = new Point(-1, -1);
                Invalidate(Rectangle.Union(_calButton, _exportButton));
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left) return;
                if (_resetButton.Contains(e.Location)) ResetPeaks();
                else if (_exportButton.Contains(e.Location)) Export();
                else if (_calButton.Contains(e.Location))
                    SetCalibration(Calibrations[(Array.IndexOf(Calibrations, Calibration) + 1) % Calibrations.Length]);
            }

            private void SetCalibration(string calibration)
            {
                var settings = Audio.CinecoreAudioSettings.Current;
                settings.VuCalibration = calibration;
                if (calibration != "auto") _referenceTarget = _reference = double.Parse(calibration, CultureInfo.InvariantCulture);
                Audio.CinecoreAudioSettings.Save();
                Invalidate();
            }

            private void ResetPeaks()
            {
                _maxTpL = _maxTpR = double.NegativeInfinity;
                _peakLampUntil[0] = _peakLampUntil[1] = _clipLampUntil[0] = _clipLampUntil[1] = 0;
                Invalidate();
            }

            private void Export()
            {
                string[] rows; lock (_log) rows = _log.ToArray();
                using var dialog = new SaveFileDialog
                {
                    Title = L("Esporta i dati dell'analisi", "Export analysis data"),
                    Filter = "CSV|*.csv|JSON|*.json",
                    FileName = L("cinecore-analisi-", "cinecore-analysis-") + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                };
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                const string header = "time_s,rms_l_dbfs,rms_r_dbfs,peak_l_dbfs,peak_r_dbfs,truepeak_l_dbtp,truepeak_r_dbtp,lufs_m,lufs_s,lufs_i,lra_lu,plr_db,psr_db,correlation,crest_l_db,crest_r_db,width_db,clip_events_true,clip_events_sample,limiter_gr_db,source_clip_samples";
                try
                {
                    if (dialog.FilterIndex == 2 || dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        var names = header.Split(',');
                        var engine = Audio.CinecoreAudioEngine.Active;
                        static string? Num(double v) => double.IsFinite(v) && v > -99 ? v.ToString("0.0", CultureInfo.InvariantCulture) : null;
                        var payload = new
                        {
                            exported = DateTime.Now,
                            reference = $"0 VU = {Math.Round(_reference):0} dBFS" + (AutoCalibration ? " (auto)" : _reference == -18 ? " (AES17)" : ""),
                            source = engine?.SourceDescription ?? AnalysisSource?.Replace('|', ' '),
                            output = engine?.OutputDescription,
                            summary = new { truePeakMaxL = Num(_maxTpL), truePeakMaxR = Num(_maxTpR), clipsSample = _spClips, clipsTrue = _tpClips, lufsIntegrated = double.IsFinite(_lufsI) ? Math.Round(_lufsI, 2) : (double?)null },
                            samples = rows.Select(r => names.Zip(r.Split(','), (n, v) => (n, v)).ToDictionary(x => x.n, x => double.TryParse(x.v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : (double?)null))
                        };
                        File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    }
                    else
                    {
                        var sb = new StringBuilder(header.Length + rows.Length * 120);
                        sb.AppendLine(header);
                        foreach (var r in rows) sb.AppendLine(r);
                        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
                    }
                }
                catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, L("Esportazione non riuscita", "Export failed")); }
            }

            /// <summary>Sfumatura verticale su tutta <paramref name="area"/>, piatta prima di
            /// <paramref name="from"/> e dopo <paramref name="to"/> (frazioni dell'altezza).
            /// Il LinearGradientBrush di GDI+ fuori dal suo rettangolo si ripete a tessere: le
            /// fasce corte facevano comparire pieghe e trattini sui bordi.</summary>
            private static LinearGradientBrush Vertical(RectangleF area, float from, float to, Color a, Color b)
            {
                var rect = new RectangleF(area.X, area.Y - 1, Math.Max(1, area.Width), Math.Max(2, area.Height + 2));
                from = Math.Clamp(from, 0f, 1f); to = Math.Clamp(Math.Max(to, from + .001f), 0f, 1f);
                var brush = new LinearGradientBrush(rect, a, b, LinearGradientMode.Vertical);
                var colors = new List<Color>(); var positions = new List<float>();
                void Stop(Color c, float p) { if (positions.Count > 0 && p <= positions[^1]) return; colors.Add(c); positions.Add(p); }
                Stop(a, 0f); Stop(a, from); Stop(b, to); Stop(b, 1f);
                if (positions[^1] < 1f) { colors.Add(b); positions.Add(1f); }
                brush.InterpolationColors = new ColorBlend { Colors = colors.ToArray(), Positions = positions.ToArray() };
                return brush;
            }

            private static GraphicsPath Rounded(RectangleF r, float radius)
            {
                var p = new GraphicsPath();
                float d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
                p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
        }
    }
}
