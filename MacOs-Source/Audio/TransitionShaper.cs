#nullable enable
using System;
using System.Diagnostics;

namespace CinecorePlayer2025.Audio
{
    /// <summary>Come suona il passaggio fra due brani.</summary>
    internal enum TransitionStyle
    {
        /// <summary>Dissolvenza a potenza costante (brani lenti, orchestrali, senza battito chiaro).</summary>
        Plain,
        /// <summary>Come un DJ con l'equalizzatore: il brano nuovo entra senza bassi, su una battuta i bassi passano da uno all'altro.</summary>
        BassSwap,
        /// <summary>Il brano che finisce se ne va con un filtro passa-alto che sale (resta il brillante, poi sparisce).</summary>
        FilterOut,
        /// <summary>Brani che non si possono agganciare: su una battuta il brano che esce si ferma e ne restano solo
        /// gli echi (mezzo battito, sempre piu' sottili); il nuovo parte sul suo primo battito.</summary>
        EchoOut,
        /// <summary>Beat-aligned DJ blend: hold A, introduce B's highs, hand over bass and vocal mids on a beat.</summary>
        DjBlend,
        /// <summary>Let an incompatible phrase resolve into a diffuse tail, then start B at native tempo.</summary>
        ReverbOut,
        /// <summary>Handoff in a measured rest; only a short anti-click ramp.</summary>
        PhraseCut,
        /// <summary>Prepared multi-stem DJ edit; the mixer plays its PCM without a global crossfade.</summary>
        StemMix,
        /// <summary>Automix a cue: ogni deck segue le curve di un preset (volume, EQ a tre bande, filtri).</summary>
        Curves,
    }

    /// <summary>
    /// Il passaggio deciso dall'automix: stile e, per lo scambio dei bassi, quando avviene (secondi da ora,
    /// gia' messi su una battuta) e quanto dura la rampa (una battuta).
    /// </summary>
    internal sealed record TransitionShape(TransitionStyle Style, double SwapAfterSeconds = 0, double SwapRampSeconds = 0.5)
    {
        internal bool IsCut => Style is TransitionStyle.EchoOut or TransitionStyle.ReverbOut or TransitionStyle.PhraseCut;
        internal double TimelineStart { get; init; } = double.NaN;
        /// <summary>Tempo del brano che entra durante il passaggio (1 = il suo; 1,05 = 5% piu' veloce).</summary>
        public double TempoRatio { get; init; } = 1;
        public bool AllowTempoSync { get; init; } = true;
        public double OutgoingTempoRatio { get; init; } = 1;
        public double TempoRelation { get; init; }
        public double TempoReturnSeconds { get; init; } = 8;
        /// <summary>Uscita con eco: dopo quanti secondi il brano che esce si ferma (il resto sono echi).</summary>
        public double CutAfterSeconds { get; init; }
        /// <summary>Uscita con eco: distanza fra gli echi (mezzo battito).</summary>
        public double EchoSeconds { get; init; } = 0.25;
        /// <summary>Istante dello scambio (Stopwatch), fissato da chi esce e condiviso con chi entra.</summary>
        public long SwapAtTimestamp { get; init; }
        public double IncomingAtSwap { get; init; } = 0.7071067811865476;
        /// <summary>Stile Curves: le automazioni dei due deck.</summary>
        public TransitionCurves? Curves { get; init; }
    }

    /// <summary>
    /// Elaborazione di un motore durante il passaggio: oltre al volume della dissolvenza, divide il segnale in
    /// bassi e resto (Linkwitz-Riley a 4 poli, 160 Hz: le due parti sommate tornano piatte) per lo scambio dei
    /// bassi, oppure fa salire un passa-alto sul brano che esce. Lo scambio e' fissato su un istante comune ai due
    /// motori (orologio di sistema): i due flussi escono dallo stesso dispositivo con la stessa latenza.
    /// </summary>
    internal sealed class TransitionShaper
    {
        private const double CrossoverHz = 160;
        private readonly TransitionStyle _style;
        private readonly bool _outgoing;
        private readonly int _channels, _rate;
        private readonly long _swapAt, _swapRamp, _createdAt;
        private readonly Biquad[] _low1, _low2, _high1, _high2, _sweep, _vocal;
        private int _sweepCountdown;
        // Uscita con eco: linea di ritardo (mezzo battito) e istante in cui il brano si ferma.
        private readonly float[]? _echo;
        private int _echoPos;
        private long _cutFrames;
        private readonly long _echoFrames;
        private double _release; // brano che entra, a passaggio finito: dal segnale filtrato a quello diretto in 100 ms
        private readonly double _pivotAngle;
        private readonly DiffuseTail? _reverb;

        private readonly CurveDeckProcessor? _curveDeck;

        /// <summary>Il brano che entra ha finito il passaggio (volume pieno, bassi pieni, filtri tolti senza scatti).</summary>
        public bool Done => _curveDeck?.Done ?? _release >= 1;

        // Con i due deck nello stesso motore lo scambio si conta in fotogrammi del passaggio (esatto, sulla battuta);
        // -1: si usa l'orologio di sistema (deck su uscite separate).
        private long _swapFrameExact = -1, _rampFramesExact;

        internal void SetFrameTiming(long swapFrame, long rampFrames, long cutFrames)
        {
            _swapFrameExact = Math.Max(1, swapFrame);
            _rampFramesExact = Math.Max(1, rampFrames);
            _cutFrames = Math.Max(0, cutFrames);
        }

        public TransitionShaper(TransitionShape shape, bool outgoing, int rate, int channels, long swapFrame, long rampFrames)
            : this(shape, outgoing, rate, channels)
        {
            _swapFrameExact = Math.Max(1, swapFrame); _rampFramesExact = Math.Max(1, rampFrames);
        }

        public TransitionShaper(TransitionShape shape, bool outgoing, int rate, int channels)
        {
            _style = shape.Style; _outgoing = outgoing; _rate = rate; _channels = channels;
            _pivotAngle = Math.Asin(Math.Clamp(shape.IncomingAtSwap, 0.1, 0.98));
            _swapAt = shape.SwapAtTimestamp != 0 ? shape.SwapAtTimestamp : Stopwatch.GetTimestamp() + (long)(Math.Max(0, shape.SwapAfterSeconds) * Stopwatch.Frequency);
            _swapRamp = Math.Max(1, (long)(Math.Max(0.05, shape.SwapRampSeconds) * Stopwatch.Frequency));
            _createdAt = Stopwatch.GetTimestamp();
            _low1 = Make(channels); _low2 = Make(channels); _high1 = Make(channels); _high2 = Make(channels); _sweep = Make(channels); _vocal = Make(channels);
            if (shape.Style == TransitionStyle.EchoOut && outgoing)
            {
                _echoFrames = Math.Max(64, (long)(Math.Clamp(shape.EchoSeconds, 0.08, 1.0) * rate));
                _echo = new float[_echoFrames * channels];
                _cutFrames = (long)(Math.Max(0, shape.CutAfterSeconds) * rate);
            }
            if (shape.IsCut) _cutFrames = (long)(Math.Max(0, shape.CutAfterSeconds) * rate);
            if (shape.Style == TransitionStyle.ReverbOut && outgoing) _reverb = new(rate, channels);
            if (shape.Style == TransitionStyle.Curves)
            {
                var curves = shape.Curves ?? TransitionCurves.Preset(1);
                _curveDeck = new CurveDeckProcessor(outgoing ? curves.Out : curves.In, outgoing, rate, channels);
            }
            for (int c = 0; c < channels; c++)
            {
                _low1[c].SetLowPass(CrossoverHz, rate); _low2[c].SetLowPass(CrossoverHz, rate);
                _high1[c].SetHighPass(CrossoverHz, rate); _high2[c].SetHighPass(CrossoverHz, rate);
                _sweep[c].SetHighPass(20, rate);
            }
        }

        public TransitionStyle Style => _style;

        private static Biquad[] Make(int n) { var a = new Biquad[n]; for (int i = 0; i < n; i++) a[i] = new Biquad(); return a; }

        /// <summary>
        /// Applica dissolvenza ed effetti a <paramref name="samples"/> campioni interlacciati. <paramref name="position"/> e
        /// <paramref name="total"/> sono i fotogrammi della dissolvenza (gia' trascorsi e in tutto).
        /// </summary>
        public void Process(float[] target, int samples, long position, long total)
        {
            if (_curveDeck != null) { _curveDeck.Process(target, samples, position, total); return; }
            int ch = _channels;
            int frames = samples / ch;
            bool eqBlend = _style is TransitionStyle.BassSwap or TransitionStyle.DjBlend;
            // Bass ownership moves between decks on a sample-counted ramp.
            double swap = _swapFrameExact >= 0
                ? Math.Clamp((position - _swapFrameExact) / (double)_rampFramesExact, 0, 1)
                : Math.Clamp((Stopwatch.GetTimestamp() - _swapAt) / (double)_swapRamp, 0, 1);
            swap = 0.5 - 0.5 * Math.Cos(swap * Math.PI);
            long nowTicks = Stopwatch.GetTimestamp();
            // B enters with restrained mids and no bass; the bass handover has its own curve.
            double toSwap = _swapFrameExact >= 0 ? Math.Clamp(position / (double)_swapFrameExact, 0, 1)
                : Math.Clamp((nowTicks - _createdAt) / (double)Math.Max(1, _swapAt - _createdAt), 0, 1);
            double swapFrame = _swapFrameExact >= 0 ? _swapFrameExact : Math.Max(1, (_swapAt - _createdAt) / (double)Stopwatch.Frequency * _rate);
            for (int f = 0; f < frames; f++)
            {
                if (_swapFrameExact >= 0)
                {
                    double phase = Math.Clamp((position + f - _swapFrameExact) / (double)_rampFramesExact, 0, 1);
                    swap = 0.5 - 0.5 * Math.Cos(phase * Math.PI);
                }
                if (eqBlend && (!_outgoing || _style == TransitionStyle.DjBlend) && --_sweepCountdown <= 0)
                {
                    _sweepCountdown = 128; // frames, independent of WASAPI callback size
                    double progress = _swapFrameExact >= 0 ? Math.Clamp((position + f) / swapFrame, 0, 1) : toSwap;
                    double hz = _style == TransitionStyle.DjBlend || swap > 0 ? 20 : 240 * Math.Pow(160 / 240.0, progress);
                    for (int c = 0; c < ch; c++)
                    {
                        _sweep[c].SetHighPass(hz, _rate, 0.75);
                        double midDb = _style == TransitionStyle.DjBlend ? -12 * (_outgoing ? swap : 1 - swap) : -3 * (1 - swap);
                        _vocal[c].SetPeak(1500, midDb, 0.5, _rate);
                    }
                }
                double t = Math.Min(1.0, (position + f) / (double)Math.Max(1, total));
                float gain;
                switch (_style)
                {
                    case TransitionStyle.ReverbOut:
                    case TransitionStyle.PhraseCut:
                    case TransitionStyle.EchoOut:
                        // B's fade starts at the actual handoff, after its scheduled silence.
                        gain = (float)Math.Sin(Math.Clamp((position + f - _cutFrames) / (double)Math.Max(1,total),0,1) * Math.PI / 2);
                        break;
                    case TransitionStyle.DjBlend:
                        double intro = Math.Clamp((position + f) / swapFrame, 0, 1);
                        double outro = Math.Clamp((position + f - swapFrame) / Math.Max(1, total - swapFrame), 0, 1);
                        intro = intro * intro * (3 - 2 * intro);
                        outro = outro * outro * (3 - 2 * outro);
                        double oldHigh = Math.Cos(outro * Math.PI / 2);
                        double newHigh = position + f < swapFrame ? Math.Sin(_pivotAngle * intro) : Math.Sin(_pivotAngle + (Math.PI / 2 - _pivotAngle) * outro);
                        // Keep the shared highs at constant power; the independent bass curve
                        // retains its punch without driving the limiter on two sets of hi-hats.
                        gain = (float)((_outgoing ? oldHigh : newHigh) / Math.Sqrt(oldHigh * oldHigh + newHigh * newHigh));
                        break;
                    case TransitionStyle.BassSwap:
                        if (_outgoing)
                        {
                            double pre = Math.Clamp((position + f) / swapFrame, 0, 1);
                            double q = Math.Clamp((position + f - swapFrame) / Math.Max(1, total - swapFrame), 0, 1);
                            double angle = _pivotAngle * pre * pre * (3 - 2 * pre) + (Math.PI / 2 - _pivotAngle) * q;
                            gain = (float)Math.Cos(angle);
                        }
                        else
                        {
                            // Complementary mid/high faders meet at the planner's chosen pivot.
                            double pre = Math.Clamp((position + f) / swapFrame, 0, 1);
                            double post = Math.Clamp((position + f - swapFrame) / Math.Max(1, total - swapFrame), 0, 1);
                            double angle = _pivotAngle * pre * pre * (3 - 2 * pre) + (Math.PI / 2 - _pivotAngle) * post;
                            gain = (float)Math.Sin(angle);
                        }
                        break;
                    case TransitionStyle.FilterOut:
                        gain = (float)(_outgoing ? Math.Cos(Math.Pow(t, 1.3) * Math.PI / 2) : Math.Sin(t * Math.PI / 2));
                        break;
                    default:
                        gain = (float)(_outgoing ? Math.Cos(t * Math.PI / 2) : Math.Sin(t * Math.PI / 2));
                        break;
                }
                if (_echo != null)
                {
                    EchoFrame(target, f * ch, position + f, total);
                    continue;
                }
                if (_outgoing && _style is TransitionStyle.ReverbOut or TransitionStyle.PhraseCut)
                {
                    double after = (position + f - _cutFrames) / (double)_rate;
                    float dry = (float)Math.Clamp(1 - Math.Max(0, after) / .035, 0, 1);
                    double tailProgress = Math.Clamp((position + f - _cutFrames) / (double)Math.Max(1, total - _cutFrames), 0, 1);
                    for (int c = 0; c < ch; c++)
                    {
                        float x = target[f * ch + c];
                        float tail = _reverb?.Run(x, c, after >= -.4 && after < 0) ?? 0;
                        double wet = Math.Clamp(after / .035, 0, 1) * Math.Cos(tailProgress * Math.PI / 2);
                        target[f * ch + c] = x * dry + tail * (float)(.48 * wet);
                    }
                    continue;
                }
                if (_style == TransitionStyle.FilterOut && _outgoing && --_sweepCountdown <= 0)
                {
                    // Passa-alto da 20 Hz a ~1,6 kHz in modo esponenziale (a orecchio la salita e' regolare).
                    _sweepCountdown = 32;
                    double hz = 20 * Math.Pow(80, Math.Min(1, t * 1.15));
                    for (int c = 0; c < ch; c++) _sweep[c].SetHighPass(hz, _rate, 0.8);
                }
                if (!_outgoing && t >= 1 && swap >= 1 && _release < 1) _release += 1.0 / (0.1 * _rate);
                else if (!_outgoing && t >= 1 && !eqBlend) _release = 1;
                int i = f * ch;
                for (int c = 0; c < ch; c++)
                {
                    float x = target[i + c];
                    if (eqBlend)
                    {
                        // The bass handover has its own power curve. Applying the global fader twice
                        // used to hollow out the low end right at the swap.
                        double ownership = _outgoing ? 1 - swap : swap;
                        float low = _low2[c].Run(_low1[c].Run(x));
                        float high = _high2[c].Run(_high1[c].Run(x));
                        if (_style == TransitionStyle.DjBlend) high = _vocal[c].Run(_sweep[c].Run(high));
                        else if (!_outgoing && swap < 1) high = (float)(_vocal[c].Run(_sweep[c].Run(high)) * (1 - swap) + high * swap);
                        float shaped = high * gain + low * (float)Math.Sqrt(Math.Max(0, ownership));
                        // Warm up the crossover without a phase jump on the outgoing deck's
                        // first sample. Bass ownership is independent of the mid/high fader.
                        if (_outgoing)
                        {
                            double attack = Math.Clamp((position + f) / (0.05 * _rate), 0, 1);
                            shaped = (float)(x * gain + (shaped - x * gain) * attack);
                        }
                        // Finito il passaggio, i filtri si tolgono in dissolvenza: il loro sfasamento attorno a 160 Hz
                        // tolto di colpo farebbe un piccolo scatto.
                        target[i + c] = _release > 0 ? (float)(shaped + (x * gain - shaped) * Math.Min(1, _release)) : shaped;
                        continue;
                    }
                    else if (_style == TransitionStyle.FilterOut && _outgoing)
                        x = _sweep[c].Run(x);
                    target[i + c] = x * gain;
                }
            }
        }

        /// <summary>
        /// Un fotogramma dell'uscita con eco: fino al taglio il brano suona e, nell'ultimo battito, alimenta la linea di
        /// ritardo; al taglio si spegne in 30 ms e restano gli echi, ogni volta piu' deboli e piu' sottili (passa-alto
        /// che sale), fino al silenzio alla fine del passaggio.
        /// </summary>
        private void EchoFrame(float[] target, int i, long p, long total)
        {
            int ch = _channels;
            long afterCut = p - _cutFrames;
            float dry = afterCut < 0 ? 1f : (float)Math.Max(0, 1 - afterCut / (0.03 * _rate));
            bool feeding = afterCut < 0 && afterCut >= -2 * _echoFrames;
            float wet = afterCut < 0 ? 0f : 0.6f;
            double rest = Math.Max(1, total - _cutFrames);
            float tailGain = afterCut <= 0 ? 1f : (float)Math.Cos(Math.Min(1, afterCut / rest) * Math.PI / 2);
            if (afterCut >= 0 && --_sweepCountdown <= 0)
            {
                _sweepCountdown = 32;
                double hz = 150 * Math.Pow(12, Math.Min(1, afterCut / rest));
                for (int c = 0; c < ch; c++) _sweep[c].SetHighPass(hz, _rate, 0.7);
            }
            int at = _echoPos * ch;
            for (int c = 0; c < ch; c++)
            {
                float x = target[i + c];
                float delayed = _echo![at + c];
                float echoed = afterCut >= 0 ? _sweep[c].Run(delayed) : delayed;
                // Riscrittura della linea: il brano (solo nell'ultimo battito prima del taglio) piu' gli echi che si ripetono.
                _echo[at + c] = (feeding ? x : 0f) + echoed * 0.58f;
                target[i + c] = (x * dry + echoed * wet) * tailGain;
            }
            _echoPos = (_echoPos + 1) % (int)_echoFrames;
        }

        // Eight decorrelated, damped feedback delays with a lossless Householder mix.
        // No dry signal after the cut and no single rhythmic repeat as in an echo.
        private sealed class DiffuseTail
        {
            private readonly float[][][] _lines;
            private readonly int[][] _positions;
            private readonly float[][] _damped;
            private readonly Biquad[] _hp;
            private readonly float[] _values = new float[8];
            internal DiffuseTail(int rate, int channels)
            {
                _lines = new float[channels][][]; _positions = new int[channels][]; _damped = new float[channels][]; _hp = Make(channels);
                int[] lengths = {1493,1601,1747,1867,1999,2131,2281,2423};
                for (int c = 0; c < channels; c++)
                {
                    _lines[c] = new float[8][]; _positions[c] = new int[8]; _damped[c] = new float[8]; _hp[c].SetHighPass(350,rate);
                    for (int j = 0; j < 8; j++) _lines[c][j] = new float[Math.Max(1,(int)((lengths[j] + c * 73) * rate / 44100.0))];
                }
            }
            internal float Run(float input, int channel, bool feed)
            {
                float x = _hp[channel].Run(input), sum = 0;
                for (int j = 0; j < 8; j++)
                {
                    float v = _lines[channel][j][_positions[channel][j]];
                    _values[j] = _damped[channel][j] = .55f * v + .45f * _damped[channel][j]; sum += _values[j];
                }
                float output = 0;
                for (int j = 0; j < 8; j++)
                {
                    output += _values[j] * ((j & 1) == 0 ? 1 : -1);
                    _lines[channel][j][_positions[channel][j]] = (feed ? x * .353553f : 0) + .76f * (_values[j] - sum * .25f);
                    _positions[channel][j] = (_positions[channel][j] + 1) % _lines[channel][j].Length;
                }
                return output * .353553f;
            }
        }

        /// <summary>Filtro del secondo ordine (forma diretta trasposta II).</summary>
        private sealed class Biquad
        {
            private double _b0 = 1, _b1, _b2, _a1, _a2, _z1, _z2;

            public void SetLowPass(double hz, int rate, double q = 0.70710678)
            {
                double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
                _b0 = (1 - cos) / 2 / a0; _b1 = (1 - cos) / a0; _b2 = _b0; _a1 = -2 * cos / a0; _a2 = (1 - alpha) / a0;
            }

            /// <summary>Campana (EQ parametrico, formule RBJ): <paramref name="db"/> attorno a <paramref name="hz"/>.</summary>
            public void SetPeak(double hz, double db, double q, int rate)
            {
                double a = Math.Pow(10, db / 40), w = 2 * Math.PI * Math.Min(hz, rate * 0.45) / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
                double a0 = 1 + alpha / a;
                _b0 = (1 + alpha * a) / a0; _b1 = -2 * cos / a0; _b2 = (1 - alpha * a) / a0; _a1 = -2 * cos / a0; _a2 = (1 - alpha / a) / a0;
            }

            public void SetHighPass(double hz, int rate, double q = 0.70710678)
            {
                hz = Math.Min(hz, rate * 0.45);
                double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
                _b0 = (1 + cos) / 2 / a0; _b1 = -(1 + cos) / a0; _b2 = _b0; _a1 = -2 * cos / a0; _a2 = (1 - alpha) / a0;
            }

            public float Run(float x)
            {
                double y = _b0 * x + _z1;
                _z1 = _b1 * x - _a1 * y + _z2;
                _z2 = _b2 * x - _a2 * y;
                return (float)y;
            }
        }
    }
}
