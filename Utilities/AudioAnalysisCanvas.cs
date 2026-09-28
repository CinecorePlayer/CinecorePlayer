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
            private readonly Font _heading = new("Segoe UI", 10.8f, FontStyle.Bold), _axis = new("Segoe UI", 9.2f);
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
                if (Section == 0) { DrawOverview(g); return; }
                var graphs = Graphs; var boxes = GraphBounds();
                for(int i=0;i<graphs.Length;i++) DrawGraph(g,boxes[i],graphs[i]);
            }
            private static Color CardColor => HUD.Theme.IsLight ? Color.FromArgb(245, 248, 251) : Color.FromArgb(10, 22, 30);
            private void Card(Graphics g, Rectangle box)
            {
                using var path = RoundedRect(box, D(10));
                using var fill = new LinearGradientBrush(box, CardColor, HUD.Theme.IsLight ? CardColor : Color.FromArgb(8, 18, 26), 90);
                using var edge = new Pen(Color.FromArgb(65, HUD.Theme.Border));
                g.FillPath(fill, path); g.DrawPath(edge, path);
            }
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
                DrawKpis(g,kpi);
            }
            private void DrawWaveCard(Graphics g, Rectangle box)
            {
                if (box.Width < 1 || box.Height < 1) return;
                Card(g,box); Heading(g,box,English ? "Waveform" : "Forma d'onda",1);
                var plot = new Rectangle(box.Left+D(20),box.Top+D(62),box.Width-D(40),Math.Max(1,box.Height-D(84)));
                if (plot.Width < 1) return;
                using var grid = new Pen(Color.FromArgb(12,HUD.Theme.Text));
                for (int i=0;i<=4;i++) g.DrawLine(grid,plot.Left,plot.Top+plot.Height*i/4,plot.Right,plot.Top+plot.Height*i/4);
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
                var plot=new Rectangle(box.Left+D(48),box.Top+D(62),Math.Max(1,box.Width-D(68)),Math.Max(1,box.Height-D(94)));
                using var grid=new Pen(Color.FromArgb(14,HUD.Theme.Text));
                for(int db=0;db>=-120;db-=20)
                {
                    int y=plot.Top+plot.Height*(-db)/120;
                    g.DrawLine(grid,plot.Left,y,plot.Right,y);
                    TextRenderer.DrawText(g,db.ToString(),_axis,new Rectangle(box.Left+D(3),y-D(9),D(36),D(18)),HUD.Theme.Muted,TextFormatFlags.Right|TextFormatFlags.NoPadding);
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
                int bars=Math.Clamp(plot.Width/D(6),20,120);
                var points=new PointF[bars];
                using var brush=new LinearGradientBrush(plot,Color.FromArgb(190,Signal),Color.FromArgb(16,Signal),90);
                using var line=new Pen(Signal,1.3f);
                using var glow=new Pen(Color.FromArgb(22,Signal),4);
                for(int i=0;i<bars;i++)
                {
                    double lo=20*Math.Pow(maxHz/20,i/(double)bars), hi=20*Math.Pow(maxHz/20,(i+1d)/bars);
                    double step=_spectrumHz/(_spectrum.Length-1);
                    // Integrate power over fractional FFT bins. Narrow bass bands
                    // interpolate adjacent bins instead of repeating one maximum.
                    double left = Math.Clamp(lo / step, 0, _spectrum.Length - 1d);
                    double right = Math.Clamp(hi / step, left, _spectrum.Length - 1d);
                    double power = 0, weight = 0;
                    for (double at = left; at < right;)
                    {
                        int bin = Math.Min(_spectrum.Length - 2, (int)at);
                        double end = Math.Min(right, bin + 1d);
                        double p0 = Math.Pow(10, _spectrum[bin] / 10);
                        double p1 = Math.Pow(10, _spectrum[bin + 1] / 10);
                        double width = end - at;
                        power += width * (p0 + (p1 - p0) * ((at + end) / 2 - bin));
                        weight += width;
                        at = end;
                    }
                    double db = weight > 0 ? 10 * Math.Log10(Math.Max(1e-12, power / weight)) : -120;
                    float y=plot.Bottom-(float)((Math.Clamp(db,-120,0)+120)/120)*plot.Height;
                    float x=plot.Left+i*plot.Width/(float)bars;
                    float w=plot.Width/(float)bars;
                    g.FillRectangle(brush,x,y,Math.Max(1,w-1.5f),plot.Bottom-y);
                    points[i]=new PointF(x+w/2,y);
                }
                g.DrawLines(glow,points);g.DrawLines(line,points);
            }
            private void DrawKpis(Graphics g, Rectangle box)
            {
                if(box.Width<1 || box.Height<1)return;
                Card(g,box);
                string[] names=English ? new[]{"Loudness (LUFS)","Levels (RMS)","Crest factor","Balance"} : new[]{"Loudness (LUFS)","Livelli (RMS)","Fattore di cresta","Bilanciamento"};
                int[] histories={2,0,5,7}, icons={3,6,7,4};
                using var valueFont=new Font("Segoe UI",19,FontStyle.Bold);
                using var separator=new Pen(Color.FromArgb(70,HUD.Theme.Border));
                for(int i=0;i<4;i++)
                {
                    var cell=Rectangle.FromLTRB(box.Left+box.Width*i/4,box.Top,box.Left+box.Width*(i+1)/4,box.Bottom);
                    if(i>0)g.DrawLine(separator,cell.Left,cell.Top+D(19),cell.Left,cell.Bottom-D(19));
                    Heading(g,cell,names[i],icons[i]);
                    var data=_history[histories[i]];
                    double value=data.LastOrDefault(double.NaN);
                    string label=double.IsFinite(value) ? (i==3?value.ToString("+0.0;-0.0;0.0")+"%":value.ToString("0.0")) : "—";
                    TextRenderer.DrawText(g,label,valueFont,new Rectangle(cell.Left+D(22),cell.Top+D(53),D(95),D(34)),HUD.Theme.Text,TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g,i==0?"LUFS":i==1?"dB":"",_axis,new Rectangle(cell.Left+D(22),cell.Top+D(88),D(70),D(20)),HUD.Theme.Muted,TextFormatFlags.NoPadding);
                    var spark=new Rectangle(cell.Left+D(110),cell.Top+D(62),cell.Width-D(132),Math.Max(D(16),cell.Height-D(85)));
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
            private void DrawGraph(Graphics g,Rectangle box,int graph)
            {
                string[] names = English ? new[]{"Waveform","Spectrum","Levels (RMS)","Loudness (LUFS)","Crest factor","Balance","Phase correlation"} : new[]{"Forma d’onda","Spettro","Livelli (RMS)","Loudness (LUFS)","Fattore di cresta","Bilanciamento","Correlazione di fase"};
                int icon = graph switch { 0 => 1, 1 => 2, 2 => 6, 3 => 3, 4 => 7, 5 => 4, _ => 5 };
                int iconSize = D(18);
                AnalysisIconPainter.Draw(g, new Rectangle(box.Left, box.Top + D(5), iconSize, iconSize), icon, HUD.Theme.SubtleText);
                TextRenderer.DrawText(g,names[graph],_heading,new Rectangle(box.Left+iconSize+D(11),box.Top,Math.Max(1,box.Width-iconSize-D(11)),D(30)),HUD.Theme.Text,TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter);
                if(graph != 1) TextRenderer.DrawText(g,graph==0?"4 s · Auto":"60 s · Auto",_axis,new Rectangle(box.Right-D(98),box.Top,D(98),D(30)),HUD.Theme.Muted,TextFormatFlags.Right|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
                bool compact = box.Height < D(195);
                int plotTop = compact ? D(38) : D(52);
                int plotBottom = compact ? D(20) : D(29);
                var plot=new Rectangle(box.Left+D(72),box.Top+plotTop,Math.Max(1,box.Width-D(80)),Math.Max(1,box.Height-plotTop-plotBottom));
                var range = GraphRange(graph);
                double low = range.Low, high = range.High;
                int tickCount = plot.Height < D(112) ? 2 : 4;
                double step = (high - low) / tickCount;
                using var grid=new Pen(Color.FromArgb(HUD.Theme.IsLight ? 20 : 17,HUD.Theme.Text),1f);
                for(double value=low;value<=high+.001;value+=step)
                {
                    float y=plot.Bottom-(float)((value-low)/(high-low)*plot.Height);
                    g.DrawLine(grid,plot.Left,y,plot.Right,y);
                    string label=graph==5 ? value.ToString("+0;-0;0")+"%" : value.ToString(graph == 0 ? "0.##" : "0.#")+(graph==2?" dB":graph==3?" LUFS":"");
                    TextRenderer.DrawText(g,label,_axis,new Rectangle(box.Left,(int)y-D(10),D(64),D(20)),HUD.Theme.Muted,TextFormatFlags.Right|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
                }
                if(graph==1)
                {
                    int divisions=plot.Width<340?3:5;
                    for(int i=0;i<=divisions;i++)
                    {
                        double hz=_spectrumHz*i/divisions;
                        string label=i==0?"0 Hz":(hz/1000).ToString("0.#")+" kHz";
                        int x=plot.Left+plot.Width*i/divisions;
                        TextRenderer.DrawText(g,label,_axis,new Rectangle(Math.Clamp(x-27,plot.Left-4,plot.Right-56),plot.Bottom+10,60,18),HUD.Theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPadding);
                    }
                }
                void Series(IReadOnlyList<double> values,Color color,bool history=false,bool fill=false)
                {
                    if(values.Count<2)return;
                    using var pen=new Pen(color,1.4f){LineJoin=LineJoin.Round};
                    var state=g.Save();g.SetClip(Rectangle.Inflate(plot,1,1));
                    using var shade = new SolidBrush(Color.FromArgb(17,color));
                    PointF? previous=null;
                    int stride = !history ? Math.Max(1, values.Count / Math.Max(1, plot.Width)) : 1;
                    for(int i=0;i<values.Count;i+=stride)
                    {
                        double value = values[i];
                        // Keep narrow FFT peaks when several bins occupy one pixel.
                        if (fill) for (int j=i+1;j<Math.Min(values.Count,i+stride);j++) value=Math.Max(value,values[j]);
                        else if (!history && stride > 1)
                        {
                            double sum = 0; int samples = 0;
                            for (int j=i;j<Math.Min(values.Count,i+stride);j++)
                                if (double.IsFinite(values[j])) { sum += values[j]; samples++; }
                            value = samples > 0 ? sum / samples : double.NaN;
                        }
                        if(!double.IsFinite(value)){previous=null;continue;}
                        float x=plot.Left+(history?(History-values.Count+i)/(float)(History-1):i/(float)(values.Count-1))*plot.Width;
                        float y=plot.Bottom-(float)((value-low)/(high-low)*plot.Height);
                        var point=new PointF(x,y);
                        if(previous.HasValue)
                        {
                            if(fill){g.FillPolygon(shade,new[]{previous.Value,point,new PointF(x,plot.Bottom),new PointF(previous.Value.X,plot.Bottom)});}
                            g.DrawLine(pen,previous.Value,point);
                        }
                        previous=point;
                    }
                    g.Restore(state);
                }
                switch(graph)
                {
                    case 0:Series(_left,Signal);Series(_right,Secondary);break;
                    case 1:Series(_spectrum,Signal,fill:true);break;
                    case 2:Series(_history[0],Signal,true);Series(_history[1],Secondary,true);break;
                    case 3:Series(_history[2],Signal,true);Series(_history[3],Secondary,true);Series(_history[4],Tertiary,true);break;
                    case 4:Series(_history[5],Signal,true);Series(_history[6],Secondary,true);break;
                    case 5:Series(_history[7],Signal,true);break;
                    case 6:Series(_history[8],Signal,true);break;
                }
            }
            protected override void Dispose(bool disposing) { if(disposing){_heading.Dispose();_axis.Dispose();}base.Dispose(disposing); }
        }
    }
}
