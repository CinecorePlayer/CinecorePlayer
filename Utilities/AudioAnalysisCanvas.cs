#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025
{
    internal sealed partial class AudioMetersLiveCharts
    {
        private readonly AudioAnalysisCanvas _cleanOverview = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };

        private sealed class AudioAnalysisCanvas : Control
        {
            private const int History = 240;
            private const int HistoryIntervalMs = 250;
            private long _lastHistoryTick;
            private long _lastSpectrumTick;
            private readonly List<double>[] _history = Enumerable.Range(0, 10).Select(_ => new List<double>()).ToArray();
            private double[] _left = Array.Empty<double>(), _right = Array.Empty<double>(), _spectrum = Array.Empty<double>();
            private double _spectrumHz = 20000, _waveHalfRange = .5;
            private long _lastWaveTick;
            private int D(double value) => (int)Math.Round(value * DeviceDpi / 96d);
            private readonly Font _heading = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 10.8f, FontStyle.Bold), _axis = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.2f);
            private static Color Signal => HUD.Theme.Accent;
            private static Color Secondary => HUD.Theme.IsLight ? Color.FromArgb(189, 103, 42) : Color.FromArgb(240, 178, 105);
            private static Color Tertiary => HUD.Theme.IsLight ? Color.FromArgb(121, 81, 187) : Color.FromArgb(183, 157, 238);
            public int Section { get; set; }
            public bool English { get; set; }
            public AudioAnalysisCanvas() { DoubleBuffered = true; BackColor = MeterBack; SetStyle(ControlStyles.ResizeRedraw, true); }
            internal void ResetTrack()
            {
                foreach (var history in _history) history.Clear();
                _left = _right = _spectrum = Array.Empty<double>();
                _spectrumHz = 20000; _waveHalfRange = .5; _lastWaveTick = 0;
                _lastHistoryTick = 0;
                _lastSpectrumTick = 0;
                Invalidate();
            }
            internal void UpdateMetrics(LoopbackSampler.AudioMetrics m)
            {
                _left = m.WaveformL.Select(v => double.IsFinite(v) ? (double)v : double.NaN).ToArray();
                _right = m.WaveformR.Select(v => double.IsFinite(v) ? (double)v : double.NaN).ToArray();
                int fft = m.FftLength > 0 ? m.FftLength : Math.Max(2,(m.SpectrumDb.Length-1)*2);
                double hzPerBin = m.SampleRate > 0 ? m.SampleRate/(double)fft : 1;
                int bins = Math.Min(m.SpectrumDb.Length, (int)(20000 / hzPerBin)+1);
                long now = Environment.TickCount64;
                double peak = _left.Concat(_right).Where(double.IsFinite).Select(Math.Abs).DefaultIfEmpty(0).Max();
                double target = new[] { .05, .1, .25, .5, .75, 1, 1.25, 1.5, 2, 3, 5 }
                    .FirstOrDefault(range => range >= peak * 1.1, NiceStep(Math.Max(.05, peak * 1.1)));
                double elapsed = _lastWaveTick == 0 ? 0 : Math.Max(0, now - _lastWaveTick);
                _waveHalfRange = target >= _waveHalfRange ? target : Math.Max(target, _waveHalfRange * Math.Exp(-elapsed / 3500));
                _lastWaveTick = now;
                if (bins > 0)
                {
                    bool firstSpectrum = _spectrum.Length != bins;
                    if (firstSpectrum) _spectrum = new double[bins];
                    double blend = _lastSpectrumTick == 0 ? 1 : 1-Math.Exp(-Math.Max(0,now-_lastSpectrumTick)/250d);
                    for (int i=0;i<bins;i++)
                    {
                        double value=double.IsFinite(m.SpectrumDb[i]) ? m.SpectrumDb[i] : -120;
                        _spectrum[i] = firstSpectrum || value > _spectrum[i] ? value : _spectrum[i] + blend * (value - _spectrum[i]);
                    }
                    _lastSpectrumTick=now;
                    if (bins > 1) _spectrumHz = (bins-1)*hzPerBin;
                }
                static double Db(double value) => value > 0 ? 20*Math.Log10(value) : double.NaN;
                double[] values = { Db(m.RmsL), Db(m.RmsR), m.LufsM, m.LufsS, m.LufsI, m.CrestL_dB, m.CrestR_dB, m.Balance*100, m.Correlation, m.PeakHoldL>0?20*Math.Log10(m.PeakHoldL):-70 };
                if (_lastHistoryTick == 0 || now - _lastHistoryTick >= HistoryIntervalMs)
                {
                    int steps = _lastHistoryTick == 0 ? 1 : (int)Math.Min(History, (now - _lastHistoryTick) / HistoryIntervalMs);
                    for (int step=0; step<steps; step++)
                        for(int i=0;i<values.Length;i++)
                        {
                            var history=_history[i];
                            double value = step == steps-1 ? values[i] : history.LastOrDefault(double.NaN);
                            history.Add(double.IsFinite(value) ? value : double.NaN);
                            if(history.Count>History)history.RemoveAt(0);
                        }
                    _lastHistoryTick = now - (_lastHistoryTick == 0 ? 0 : (now-_lastHistoryTick)%HistoryIntervalMs);
                }
                Invalidate();
            }
            private static double NiceStep(double value)
            {
                double power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1e-9, value))));
                double scaled = value / power;
                return (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 2.5 ? 2.5 : scaled <= 3 ? 3 : scaled <= 5 ? 5 : 10) * power;
            }
            private static (double Low, double High) FitRange(IEnumerable<double> values, double minimum, double maximum)
            {
                foreach (double value in values.Where(double.IsFinite)) { minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value); }
                double step = NiceStep(Math.Max(1e-6, (maximum - minimum) / 4));
                double high = Math.Ceiling(maximum / step) * step;
                while (high - 4 * step > minimum + 1e-9) { step = NiceStep(step * 1.01); high = Math.Ceiling(maximum / step) * step; }
                return (high - 4 * step, high);
            }
            private (double Low, double High) GraphRange(int graph)
            {
                if (graph == 0) return (-_waveHalfRange, _waveHalfRange);
                if (graph == 1) return FitRange(_spectrum, -100, 0);
                if (graph == 2) return FitRange(_history[0].Concat(_history[1]), -60, 0);
                if (graph == 3) return FitRange(_history[2].Concat(_history[3]).Concat(_history[4]), -60, 0);
                if (graph == 4)
                {
                    double maximum = Math.Max(20, _history[5].Concat(_history[6]).Where(double.IsFinite).DefaultIfEmpty(0).Max() * 1.05);
                    return (0, NiceStep(maximum / 4) * 4);
                }
                if (graph == 5)
                {
                    double extent = NiceStep(Math.Max(10, _history[7].Where(double.IsFinite).Select(Math.Abs).DefaultIfEmpty(0).Max() * 1.1));
                    return (-extent, extent);
                }
                return (-1, 1);
            }
            private int[] Graphs => Section switch { 1 => new[]{0}, 2 => new[]{1}, 3 => new[]{3}, 4 => new[]{5}, 5 => new[]{6}, 6 => new[]{2,4}, _ => new[]{0,1} };
            private Rectangle[] GraphBounds()
            {
                int count = Graphs.Length;
                int columns = count == 2 && Width >= D(640) ? 2 : 1;
                int rows = (count + columns - 1) / columns;
                int pad = D(24), gap = D(16);
                int width = Math.Max(1, (Width - pad * 2 - gap * (columns-1)) / columns);
                int height = Math.Max(1, (Height - pad * 2 - gap * (rows-1)) / rows);
                return Enumerable.Range(0,count).Select(i => new Rectangle(pad+(i%columns)*(width+gap),pad+(i/columns)*(height+gap),width,height)).ToArray();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g=e.Graphics;g.Clear(BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;
                _infoTargets.Clear();
                if (Section == 0) { DrawOverview(g); return; }
                var graphs = Graphs; var boxes = GraphBounds();
                for(int i=0;i<graphs.Length;i++) DrawGraph(g,boxes[i],graphs[i]);
            }
            private static Color CardColor => HUD.Theme.IsLight ? Color.FromArgb(245, 248, 251) : Color.FromArgb(10, 22, 30);
            // Nessuna scheda: i gruppi sono separati solo da linee sottili (vedi DrawOverview).
            private void Card(Graphics g, Rectangle box) { }
            private Pen Hairline() => new(Color.FromArgb(HUD.Theme.IsLight ? 40 : 52, HUD.Theme.Border));
            private void Heading(Graphics g, Rectangle box, string name, int icon)
            {
                AnalysisIconPainter.Draw(g, new Rectangle(box.Left+D(20),box.Top+D(21),D(20),D(20)),icon,HUD.Theme.SubtleText);
                TextRenderer.DrawText(g,name,_heading,new Rectangle(box.Left+D(54),box.Top+D(14),box.Width-D(65),D(34)),HUD.Theme.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis);
            }
            private void DrawOverview(Graphics g)
            {
                int gap = D(16), top = D(24), kpiHeight = Math.Min(D(134), Math.Max(D(84), Height/4));
                bool narrow = Width < D(640);
                int available = Math.Max(60, Height-top-kpiHeight-gap-D(2));
                int width = (Width-gap-2)/2;
                var wave = new Rectangle(1,top,narrow ? Width-2 : width,narrow ? (available-gap)/2 : available);
                var spec = new Rectangle(narrow ? 1 : wave.Right+gap,narrow ? wave.Bottom+gap : top,narrow ? Width-2 : Width-wave.Right-gap-1,wave.Height);
                DrawWaveCard(g,wave); DrawSpectrumCard(g,spec);
                var kpi = new Rectangle(1,Math.Max(wave.Bottom,spec.Bottom)+gap,Width-2,kpiHeight-1);
                // Niente linee fra i gruppi: i due grafici stanno sulla pagina, i dati in un riquadro.
                DrawKpis(g,kpi);
            }
            private void DrawWaveCard(Graphics g, Rectangle box)
            {
                if (box.Width < 1 || box.Height < 1) return;
                Card(g,box); Heading(g,box,English ? "Waveform" : "Forma d'onda",1);
                var plot = new Rectangle(box.Left+D(20),box.Top+D(62),box.Width-D(40),Math.Max(1,box.Height-D(84)));
                if (plot.Width < 1) return;
                // Solo la linea dello zero: la griglia a cinque righe non diceva nulla.
                using var grid = new Pen(Color.FromArgb(14,HUD.Theme.Text));
                g.DrawLine(grid,plot.Left,plot.Top+plot.Height/2,plot.Right,plot.Top+plot.Height/2);
                int count = Math.Max(_left.Length,_right.Length);
                float center = plot.Top+plot.Height/2f;
                using var signal = new Pen(Secondary,1f);
                using var glow = new Pen(Color.FromArgb(25,Secondary),3f);
                var state = g.Save(); g.SetClip(plot);
                // Peak envelope preserves short transients when many PCM samples share a pixel.
                for (int x=0;x<plot.Width && count>0;x++)
                {
                    int first=x*count/plot.Width, last=Math.Max(first+1,(x+1)*count/plot.Width);
                    double peak=0;
                    for(int i=first;i<Math.Min(count,last);i++)
                    {
                        if(i<_left.Length && double.IsFinite(_left[i])) peak=Math.Max(peak,Math.Abs(_left[i]));
                        if(i<_right.Length && double.IsFinite(_right[i])) peak=Math.Max(peak,Math.Abs(_right[i]));
                    }
                    float amp=(float)Math.Clamp(peak/_waveHalfRange,0,1)*plot.Height*.46f;
                    g.DrawLine(glow,plot.Left+x,center-amp,plot.Left+x,center+amp);
                    g.DrawLine(signal,plot.Left+x,center-amp,plot.Left+x,center+amp);
                }
                g.Restore(state);
            }
            private void DrawSpectrumCard(Graphics g, Rectangle box)
            {
                if (box.Width < 1 || box.Height < 1) return;
                Card(g,box); Heading(g,box,English ? "Frequency spectrum" : "Spettro di frequenza",2);
                var plot=new Rectangle(box.Left+D(56),box.Top+D(62),Math.Max(1,box.Width-D(76)),Math.Max(1,box.Height-D(94)));
                DrawSpectrumPlot(g, plot, box.Left + D(3));
            }
            // Asse delle frequenze logaritmico (20 Hz - 20 kHz) sia in panoramica sia a
            // pagina intera: con l'asse lineare bassi e medi occupavano pochi pixel.
            private void DrawSpectrumPlot(Graphics g, Rectangle plot, int labelLeft)
            {
                using var grid=new Pen(Color.FromArgb(14,HUD.Theme.Text));
                for(int db=0;db>=-120;db-=20)
                {
                    int y=plot.Top+plot.Height*(-db)/120;
                    g.DrawLine(grid,plot.Left,y,plot.Right,y);
                    TextRenderer.DrawText(g,db+" dB",_axis,new Rectangle(labelLeft,y-D(9),Math.Max(1,plot.Left-labelLeft-D(8)),D(18)),HUD.Theme.Muted,TextFormatFlags.Right|TextFormatFlags.NoPadding);
                }
                double maxHz=Math.Min(20000,Math.Max(21,Math.Ceiling(_spectrumHz/100)*100));
                float X(double hz)=>(float)(plot.Left+Math.Log(hz/20)/Math.Log(maxHz/20)*plot.Width);
                foreach(int hz in new[]{20,50,100,200,500,1000,2000,5000,10000,20000})
                {
                    if(hz>maxHz)continue;
                    float x=X(hz);g.DrawLine(grid,x,plot.Top,x,plot.Bottom);
                    string label=hz>=1000 ? (hz/1000)+"k" : hz.ToString();
                    TextRenderer.DrawText(g,label,_axis,new Rectangle((int)x-D(16),plot.Bottom+D(9),D(32),D(20)),HUD.Theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPadding);
                }
                if(_spectrum.Length<2)return;
                int bars=Math.Clamp(plot.Width/D(6),20,160);
                var points=new PointF[bars];
                // Tinta unita: il gradiente verticale produceva banding sui pannelli scuri.
                using var brush=new SolidBrush(Color.FromArgb(70,Signal));
                using var line=new Pen(Signal,1.3f);
                double step=_spectrumHz/(_spectrum.Length-1);
                for(int i=0;i<bars;i++)
                {
                    double lo=20*Math.Pow(maxHz/20,i/(double)bars), hi=20*Math.Pow(maxHz/20,(i+1d)/bars);
                    // Ogni barra mostra il bin più alto della sua banda, come un analizzatore:
                    // la media di potenza abbassava di 6-10 dB i toni puri (un tono a -6 dB
                    // appariva a -12). Le bande più strette di un bin interpolano al centro.
                    double left = Math.Clamp(lo / step, 0, _spectrum.Length - 1d);
                    double right = Math.Clamp(hi / step, left, _spectrum.Length - 1d);
                    int firstBin = (int)Math.Ceiling(left), lastBin = (int)Math.Floor(right);
                    double db;
                    if (lastBin >= firstBin)
                    {
                        db = double.NegativeInfinity;
                        for (int bin = firstBin; bin <= lastBin; bin++) db = Math.Max(db, _spectrum[bin]);
                    }
                    else
                    {
                        double center = (left + right) / 2;
                        int bin = Math.Min(_spectrum.Length - 2, (int)center);
                        double f = center - bin;
                        db = 10 * Math.Log10(Math.Max(1e-12, Math.Pow(10, _spectrum[bin] / 10) * (1 - f) + Math.Pow(10, _spectrum[bin + 1] / 10) * f));
                    }
                    float y=plot.Bottom-(float)((Math.Clamp(db,-120,0)+120)/120)*plot.Height;
                    float x=plot.Left+i*plot.Width/(float)bars;
                    float w=plot.Width/(float)bars;
                    g.FillRectangle(brush,x,y,Math.Max(1,w-1.5f),plot.Bottom-y);
                    points[i]=new PointF(x+w/2,y);
                }
                g.DrawLines(line,points);
            }
            private void DrawKpis(Graphics g, Rectangle box)
            {
                if(box.Width<1 || box.Height<1)return;
                // Nessun riquadro: i quattro dati stanno sulla pagina, separati da barre verticali sottili.
                string[] names=English ? new[]{"Loudness (LUFS)","Levels (RMS)","Crest factor","Balance"} : new[]{"Loudness (LUFS)","Livelli (RMS)","Fattore di cresta","Bilanciamento"};
                int[] histories={2,0,5,7}, icons={3,6,7,4};
                using var valueFont=global::CinecorePlayer2025.AppFonts.Create("Segoe UI",19,FontStyle.Bold);
                for(int i=0;i<4;i++)
                {
                    var cell=Rectangle.FromLTRB(box.Left+box.Width*i/4,box.Top,box.Left+box.Width*(i+1)/4,box.Bottom);
                    if(i>0){ using var separator=new Pen(Color.FromArgb(90,HUD.Theme.Border)); g.DrawLine(separator,cell.Left,cell.Top+D(16),cell.Left,cell.Bottom-D(16)); }
                    Heading(g,cell,names[i],icons[i]);
                    var data=_history[histories[i]];
                    double value=data.LastOrDefault(double.NaN);
                    string label=double.IsFinite(value) ? (i==3?value.ToString("+0.0;-0.0;0.0")+"%":value.ToString("0.0")) : "—";
                    int labelWidth=TextRenderer.MeasureText(g,label,valueFont,Size.Empty,TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g,label,valueFont,new Rectangle(cell.Left+D(22),cell.Top+D(53),labelWidth+D(4),D(34)),HUD.Theme.Text,TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g,i==0?"LUFS":i is 1 or 2?"dB":"L / R",_axis,new Rectangle(cell.Left+D(22),cell.Top+D(88),D(70),D(20)),HUD.Theme.Muted,TextFormatFlags.NoPadding);
                    // La mini-linea parte dopo il valore: con "+12,0%" si sovrapponeva al testo.
                    int sparkLeft=Math.Max(D(110),D(22)+labelWidth+D(18));
                    var spark=new Rectangle(cell.Left+sparkLeft,cell.Top+D(62),cell.Width-sparkLeft-D(22),Math.Max(D(16),cell.Height-D(85)));
                    if(spark.Width<8 || data.Count<2)continue;
                    var range=FitRange(data,i==3?-5:i==2?0:-40,i==3?5:i==2?20:0);
                    using var pen=new Pen(i is 1 or 2 ? Secondary : Signal,1.2f);
                    PointF? previous=null;
                    for(int n=0;n<data.Count;n++)
                    {
                        if(!double.IsFinite(data[n])){previous=null;continue;}
                        var point=new PointF(spark.Left+n*spark.Width/(float)(data.Count-1),spark.Bottom-(float)((data[n]-range.Low)/(range.High-range.Low))*spark.Height);
                        if(previous.HasValue)g.DrawLine(pen,previous.Value,point); previous=point;
                    }
                }
            }
            private string T(string it, string en) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(English ? en : it);

            /// <summary>Serie di un grafico: nome in legenda, colore, valori e unita'.</summary>
            private readonly record struct GraphSeries(string Name, Color Color, IReadOnlyList<double> Values, string Unit, bool Primary);

            private GraphSeries[] SeriesFor(int graph) => graph switch
            {
                0 => new[] { new GraphSeries("L", Signal, _left, "", true), new GraphSeries("R", Secondary, _right, "", false) },
                2 => new[] { new GraphSeries(T("Sinistro", "Left"), Signal, _history[0], "dB", true), new GraphSeries(T("Destro", "Right"), Secondary, _history[1], "dB", false) },
                3 => new[] { new GraphSeries(T("Momentanea", "Momentary"), Signal, _history[2], "LUFS", true), new GraphSeries(T("Breve (3 s)", "Short (3 s)"), Secondary, _history[3], "LUFS", false), new GraphSeries(T("Integrata", "Integrated"), Tertiary, _history[4], "LUFS", false) },
                4 => new[] { new GraphSeries(T("Sinistro", "Left"), Signal, _history[5], "dB", true), new GraphSeries(T("Destro", "Right"), Secondary, _history[6], "dB", false) },
                5 => new[] { new GraphSeries(T("Bilanciamento", "Balance"), Signal, _history[7], "%", true) },
                6 => new[] { new GraphSeries(T("Correlazione", "Correlation"), Signal, _history[8], "", true) },
                _ => Array.Empty<GraphSeries>()
            };

            private string Description(int graph) => graph switch
            {
                0 => T("Il segnale in uscita negli ultimi istanti: più alta l'onda, più forte il suono.", "The output signal over the last moments: the taller the wave, the louder."),
                1 => T("Quanta energia c'è a ogni frequenza: a sinistra i bassi, a destra gli acuti.", "How much energy there is at each frequency: lows on the left, highs on the right."),
                2 => T("Livello medio (RMS) dei due canali negli ultimi 60 secondi. 0 dB è il massimo digitale.", "Average (RMS) level of both channels over the last 60 seconds. 0 dB is digital full scale."),
                3 => T("Volume percepito (EBU R128). Le piattaforme di streaming normalizzano intorno a -14 LUFS.", "Perceived loudness (EBU R128). Streaming services normalize around -14 LUFS."),
                4 => T("Distanza fra picchi e livello medio: valori alti = suono dinamico, bassi = molto compresso.", "Gap between peaks and average level: high = dynamic, low = heavily compressed."),
                5 => T("Quale canale suona più forte: 0 è centrato, valori positivi spostano a destra.", "Which channel is louder: 0 is centred, positive values lean right."),
                _ => T("Somiglianza fra i canali: +1 mono, intorno a 0 stereo largo, sotto 0 rischio di cancellazioni.", "Similarity between channels: +1 mono, around 0 wide stereo, below 0 risk of cancellation.")
            };

            private static string Num(double value, string format) => (value < 0 ? "−" : "") + Math.Abs(value).ToString(format, System.Globalization.CultureInfo.CurrentCulture);

            /// <summary>Scala fissa per tipo di grafico (si allarga solo se i dati escono), con passo delle tacche.</summary>
            private (double Low, double High, double Step) ScaleFor(int graph)
            {
                IEnumerable<double> Data(params int[] h) => h.SelectMany(i => _history[i]).Where(double.IsFinite);
                switch (graph)
                {
                    case 0: return (-_waveHalfRange, _waveHalfRange, _waveHalfRange / 2);
                    case 2: { double min = Data(0, 1).DefaultIfEmpty(0).Min(); double low = Math.Max(-90, Math.Min(-60, Math.Floor(min / 10) * 10)); return (low, 0, low <= -80 ? 20 : 10); }
                    case 3: { double min = Data(2, 3, 4).DefaultIfEmpty(0).Min(); double low = Math.Max(-70, Math.Min(-40, Math.Floor(min / 10) * 10)); return (low, 0, 10); }
                    case 4: { double max = Data(5, 6).DefaultIfEmpty(0).Max(); double high = Math.Max(20, Math.Ceiling(max / 5) * 5); return (0, high, high > 30 ? 10 : 5); }
                    case 5: { double extent = NiceStep(Math.Max(10, Data(7).Select(Math.Abs).DefaultIfEmpty(0).Max() * 1.1)); return (-extent, extent, extent / 2); }
                    default: return (-1, 1, .5);
                }
            }

            private void DrawGraph(Graphics g,Rectangle box,int graph)
            {
                string[] names = English ? new[]{"Waveform","Spectrum","Levels (RMS)","Loudness (LUFS)","Crest factor","Balance","Phase correlation"} : new[]{"Forma d’onda","Spettro","Livelli (RMS)","Loudness (LUFS)","Fattore di cresta","Bilanciamento","Correlazione di fase"};
                int icon = graph switch { 0 => 1, 1 => 2, 2 => 6, 3 => 3, 4 => 7, 5 => 4, _ => 5 };
                int iconSize = D(18);
                AnalysisIconPainter.Draw(g, new Rectangle(box.Left, box.Top + D(6), iconSize, iconSize), icon, HUD.Theme.SubtleText);
                int titleW = TextRenderer.MeasureText(g, names[graph], _heading, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g,names[graph],_heading,new Rectangle(box.Left+iconSize+D(11),box.Top,titleW+D(4),D(30)),HUD.Theme.Text,TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter);
                // Intestazione minimale: titolo a sinistra, letture attuali piccole a destra sulla
                // stessa riga; sotto il titolo una riga discreta che spiega il grafico.
                var series = SeriesFor(graph);
                var readings = new List<(string Name, Color Color, string Value, string Unit)>();
                if (graph == 1)
                {
                    var (hz, db) = SpectrumPeak();
                    string value = double.IsFinite(hz) ? (hz >= 1000 ? (hz / 1000).ToString("0.0#", System.Globalization.CultureInfo.CurrentCulture) + " kHz" : Math.Round(hz) + " Hz") : "—";
                    readings.Add((T("Picco", "Peak"), Signal, value, double.IsFinite(db) ? Num(db, "0") + " dB" : ""));
                }
                foreach (var serie in series)
                {
                    double last = serie.Values.Count > 0 ? (graph == 0 ? PeakDb(serie.Values) : serie.Values[serie.Values.Count - 1]) : double.NaN;
                    string value = !double.IsFinite(last) ? "—"
                        : graph is 5 or 6 ? (last > 0 ? "+" : "") + Num(last, graph == 6 ? "0.00" : "0.0")
                        : Num(last, "0.0");
                    string unit = graph == 0 ? "dBFS" : serie.Unit;
                    if (graph == 6 && double.IsFinite(last)) unit = last > .9 ? T("quasi mono", "near mono") : last > .25 ? "stereo" : last >= 0 ? T("stereo largo", "wide stereo") : T("fuori fase", "out of phase");
                    if (graph == 5 && double.IsFinite(last)) unit = Math.Abs(last) < 2 ? T("% centrato", "% centred") : last > 0 ? T("% a destra", "% right") : T("% a sinistra", "% left");
                    readings.Add((serie.Name, serie.Color, value, unit));
                }
                using var valueFont = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.6f);
                // Spiegazione nel classico pulsante info accanto al titolo: compare al passaggio.
                int info = D(16);
                var infoRect = new Rectangle(box.Left + iconSize + D(11) + titleW + D(8), box.Top + (D(30) - info) / 2, info, info);
                bool infoHot = infoRect.Contains(_mouse);
                HUD.MusicTransportBar.DrawSymbol(g, infoRect, "info", Color.FromArgb(infoHot ? 255 : 150, infoHot ? HUD.Theme.Text : HUD.Theme.Muted));
                _infoTargets.Add((Rectangle.Inflate(infoRect, D(6), D(6)), Description(graph)));
                DrawReadings(g, readings, box.Right, box.Top, D(30), valueFont, infoRect.Right + D(24));

                bool history = graph >= 2;
                int plotTop = box.Top + D(58);
                int plotBottom = history || graph == 1 ? D(30) : D(12);
                var plot=new Rectangle(box.Left+D(72),plotTop,Math.Max(1,box.Width-D(80)),Math.Max(1,box.Bottom-plotTop-plotBottom));
                if (graph == 1)
                {
                    DrawSpectrumPlot(g, new Rectangle(plot.Left, plot.Top, plot.Width, Math.Max(1, plot.Height - D(4))), box.Left);
                    return;
                }
                var (low, high, step) = ScaleFor(graph);
                if (plot.Height < D(140)) step *= 2;
                using var grid=new Pen(Color.FromArgb(HUD.Theme.IsLight ? 22 : 18,HUD.Theme.Text),1f);
                using var zero=new Pen(Color.FromArgb(HUD.Theme.IsLight ? 60 : 50,HUD.Theme.Text),1f);
                float Y(double v) => plot.Bottom-(float)((v-low)/(high-low)*plot.Height);
                for(double value=low;value<=high+step*.001;value+=step)
                {
                    float y=Y(value);
                    bool axis = Math.Abs(value) < step * .001 && low < 0 && high > 0;
                    g.DrawLine(axis ? zero : grid,plot.Left,y,plot.Right,y);
                    string label = graph switch
                    {
                        0 => Num(value, "0.##"),
                        5 => (value > 0 ? "+" : "") + Num(value, "0") + "%",
                        6 => (value > 0 ? "+" : "") + Num(value, "0.#"),
                        3 => Num(value, "0"),
                        _ => Num(value, "0")
                    };
                    TextRenderer.DrawText(g,label,_axis,new Rectangle(box.Left,(int)y-D(10),D(60),D(20)),HUD.Theme.Muted,TextFormatFlags.Right|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
                }
                // Unita' dell'asse verticale, sopra le tacche.
                string axisUnit = graph switch { 2 => "dBFS", 3 => "LUFS", 4 => "dB", 5 => "%", 6 => "", _ => "" };
                if (axisUnit.Length > 0)
                    TextRenderer.DrawText(g, axisUnit, _axis, new Rectangle(box.Left, plot.Top - D(26), D(60), D(16)), HUD.Theme.SubtleText, TextFormatFlags.Right | TextFormatFlags.NoPadding);

                // Riferimenti con nome: target di loudness, zone della correlazione, centro.
                void Reference(double value, string text, Color color, bool dashed = true)
                {
                    if (value < low || value > high) return;
                    float y = Y(value);
                    using var pen = new Pen(Color.FromArgb(150, color), 1f) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
                    g.DrawLine(pen, plot.Left, y, plot.Right, y);
                    int tw = TextRenderer.MeasureText(g, text, _axis, Size.Empty, TextFormatFlags.NoPadding).Width;
                    // Etichetta a sinistra: a destra c'e' il punto "ora" delle serie.
                    TextRenderer.DrawText(g, text, _axis, new Rectangle(plot.Left + D(8), (int)y - D(19), tw + D(4), D(16)), Color.FromArgb(220, color), TextFormatFlags.NoPadding);
                }
                Color refColor = HUD.Theme.SubtleText;
                if (graph == 3) { Reference(-14, T("-14 streaming", "-14 streaming"), refColor); Reference(-23, T("-23 EBU R128 (TV)", "-23 EBU R128 (broadcast)"), refColor); }
                if (graph == 6) { Reference(1, T("mono", "mono"), refColor); Reference(-.5, T("fuori fase", "out of phase"), Color.FromArgb(226, 96, 86)); }
                if (graph == 2) Reference(-18, T("-18 riferimento AES17", "-18 AES17 reference"), refColor);

                // Asse del tempo per le serie storiche: 60 secondi, "ora" a destra.
                if (history)
                    for (int sec = 60; sec >= 0; sec -= plot.Width < D(420) ? 30 : 15)
                    {
                        float x = plot.Right - sec / 60f * plot.Width;
                        string label = sec == 0 ? T("ora", "now") : "−" + sec + " s";
                        g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                        TextRenderer.DrawText(g, label, _axis, new Rectangle((int)x - D(30), plot.Bottom + D(8), D(60), D(18)), HUD.Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                    }

                void Series(IReadOnlyList<double> values,Color color,bool isHistory,bool primary)
                {
                    if(values.Count<2)return;
                    using var pen=new Pen(color,primary ? 2f : 1.6f){LineJoin=LineJoin.Round, StartCap=LineCap.Round, EndCap=LineCap.Round};
                    var state=g.Save();g.SetClip(Rectangle.Inflate(plot,2,2));
                    PointF? previous=null, last=null;
                    int stride = !isHistory ? Math.Max(1, values.Count / Math.Max(1, plot.Width)) : 1;
                    var segment = new List<PointF>();
                    void Flush()
                    {
                        if (segment.Count >= 2)
                        {
                            if (primary && isHistory)
                            {
                                // Area tenue sotto la serie principale: si legge l'andamento a colpo d'occhio.
                                var area = new List<PointF>(segment) { new PointF(segment[^1].X, plot.Bottom), new PointF(segment[0].X, plot.Bottom) };
                                using var shade = new SolidBrush(Color.FromArgb(22, color));
                                g.FillPolygon(shade, area.ToArray());
                            }
                            g.DrawLines(pen, segment.ToArray());
                        }
                        segment.Clear();
                    }
                    for(int i=0;i<values.Count;i+=stride)
                    {
                        double value = values[i];
                        if (!isHistory && stride > 1)
                        {
                            double sum = 0; int samples = 0;
                            for (int j=i;j<Math.Min(values.Count,i+stride);j++)
                                if (double.IsFinite(values[j])) { sum += values[j]; samples++; }
                            value = samples > 0 ? sum / samples : double.NaN;
                        }
                        if(!double.IsFinite(value)){Flush();previous=null;continue;}
                        float x=plot.Left+(isHistory?(History-values.Count+i)/(float)(History-1):i/(float)(values.Count-1))*plot.Width;
                        var point=new PointF(x,Y(Math.Clamp(value, low, high)));
                        segment.Add(point); previous=point; last=point;
                    }
                    Flush();
                    g.Restore(state);
                    // Punto sull'ultimo valore: "ora".
                    if (isHistory && last.HasValue)
                    {
                        float r = D(4);
                        using var halo = new SolidBrush(Color.FromArgb(60, color));
                        using var dot = new SolidBrush(color);
                        g.FillEllipse(halo, last.Value.X - r * 1.8f, last.Value.Y - r * 1.8f, r * 3.6f, r * 3.6f);
                        g.FillEllipse(dot, last.Value.X - r, last.Value.Y - r, r * 2, r * 2);
                    }
                }
                // La principale per ultima, sopra le altre.
                foreach (var serie in series.Where(x => !x.Primary)) Series(serie.Values, serie.Color, history, false);
                foreach (var serie in series.Where(x => x.Primary)) Series(serie.Values, serie.Color, history, true);
            }

            /// <summary>Letture allineate a destra su una riga: pallino, nome in grigio, valore e unita'.
            /// Se non c'e' spazio accanto al titolo, si tolgono i nomi (restano pallino e valore).</summary>
            private int DrawReadings(Graphics g, List<(string Name, Color Color, string Value, string Unit)> items, int right, int top, int height, Font valueFont, int minLeft)
            {
                const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                int dot = D(7), gapIn = D(6), gapOut = D(22);
                int Width(bool names) => items.Sum(it =>
                    dot + gapIn + (names ? TextRenderer.MeasureText(g, it.Name, _axis, Size.Empty, F).Width + gapIn : 0)
                    + TextRenderer.MeasureText(g, it.Value, valueFont, Size.Empty, F).Width
                    + (it.Unit.Length > 0 ? D(4) + TextRenderer.MeasureText(g, it.Unit, _axis, Size.Empty, F).Width : 0)) + gapOut * Math.Max(0, items.Count - 1);
                bool withNames = right - Width(true) >= minLeft;
                int x = right - Width(withNames);
                if (x < minLeft) return right;
                int baseline = top + (height + Ascent(g, valueFont)) / 2 - D(1);
                foreach (var it in items)
                {
                    using (var brush = new SolidBrush(it.Color)) g.FillEllipse(brush, x, baseline - Ascent(g, valueFont) / 2 - dot / 2 + D(1), dot, dot);
                    x += dot + gapIn;
                    if (withNames)
                    {
                        TextRenderer.DrawText(g, it.Name, _axis, new Point(x, baseline - Ascent(g, _axis)), HUD.Theme.Muted, F);
                        x += TextRenderer.MeasureText(g, it.Name, _axis, Size.Empty, F).Width + gapIn;
                    }
                    TextRenderer.DrawText(g, it.Value, valueFont, new Point(x, baseline - Ascent(g, valueFont)), HUD.Theme.Text, F);
                    x += TextRenderer.MeasureText(g, it.Value, valueFont, Size.Empty, F).Width;
                    if (it.Unit.Length > 0)
                    {
                        x += D(4);
                        TextRenderer.DrawText(g, it.Unit, _axis, new Point(x, baseline - Ascent(g, _axis)), HUD.Theme.Muted, F);
                        x += TextRenderer.MeasureText(g, it.Unit, _axis, Size.Empty, F).Width;
                    }
                    x += gapOut;
                }
                return right;
            }

            private static int Ascent(Graphics g, Font font)
            {
                var family = font.FontFamily;
                return (int)Math.Round(font.SizeInPoints * g.DpiY / 72f * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style));
            }

            private static double PeakDb(IReadOnlyList<double> values)
            {
                double peak = 0;
                foreach (double v in values) if (double.IsFinite(v)) peak = Math.Max(peak, Math.Abs(v));
                return peak > 0 ? 20 * Math.Log10(peak) : double.NaN;
            }

            private (double Hz, double Db) SpectrumPeak()
            {
                if (_spectrum.Length < 2) return (double.NaN, double.NaN);
                double step = _spectrumHz / (_spectrum.Length - 1);
                int best = -1; double bestDb = double.NegativeInfinity;
                for (int i = 1; i < _spectrum.Length; i++)
                {
                    double hz = i * step;
                    if (hz < 20 || hz > 20000) continue;
                    if (_spectrum[i] > bestDb) { bestDb = _spectrum[i]; best = i; }
                }
                return best < 0 || bestDb <= -119 ? (double.NaN, double.NaN) : (best * step, bestDb);
            }
            // Pulsanti info: area e testo (gia' nella lingua dell'interfaccia) per il tooltip.
            private readonly List<(Rectangle Bounds, string Text)> _infoTargets = new();
            private readonly ToolTip _infoTip = new() { InitialDelay = 150, ReshowDelay = 100, AutoPopDelay = 20000 };
            private Point _mouse = new(-1, -1);
            private string? _shownInfo;

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                bool wasHot = _infoTargets.Any(t => t.Bounds.Contains(_mouse));
                _mouse = e.Location;
                var hit = _infoTargets.FirstOrDefault(t => t.Bounds.Contains(e.Location));
                bool hot = hit.Text != null;
                Cursor = hot ? Cursors.Help : Cursors.Default;
                if (hot && _shownInfo != hit.Text)
                {
                    _shownInfo = hit.Text;
                    _infoTip.Show(hit.Text, this, hit.Bounds.Left, hit.Bounds.Bottom + D(4));
                }
                else if (!hot && _shownInfo != null) { _shownInfo = null; _infoTip.Hide(this); }
                if (hot != wasHot) Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _mouse = new Point(-1, -1);
                if (_shownInfo != null) { _shownInfo = null; _infoTip.Hide(this); Invalidate(); }
            }

            protected override void Dispose(bool disposing) { if(disposing){_heading.Dispose();_axis.Dispose();_infoTip.Dispose();}base.Dispose(disposing); }
        }
    }
}
