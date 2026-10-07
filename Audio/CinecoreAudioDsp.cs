#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CinecorePlayer2025.Audio
{
    // Cinecore Audio Engine · catena DSP.
    // Tutto in doppia precisione: i float a 32 bit bastano per il trasporto, non per
    // filtri IIR a bassa frequenza (i biquad a 20-40 Hz accumulano errore e rumore).
    // Ordine: ReplayGain + preamp/headroom -> EQ -> crossfeed -> immagine stereo
    //         (ampiezza, bilanciamento, mono) -> limiter true-peak -> volume -> loudness.
    // La loudness sta dopo il volume: alza bassi e alti di meno di quanto il volume abbassa,
    // quindi non puo' mai far saturare.

    internal enum EqBandType { Peak, LowShelf, HighShelf, LowPass, HighPass }

    internal sealed record EqBand(EqBandType Type, double FrequencyHz, double GainDb, double Q, bool Enabled = true);

    internal enum ClipProtection { Off, Headroom, Limiter }

    internal enum EqMode { Graphic10, Graphic31, Parametric }

    internal enum CrossfeedLevel { Off, Low, Medium, High }

    internal sealed record AudioDspSettings(
        bool EqEnabled,
        IReadOnlyList<EqBand> Bands,
        double PreampDb,
        ClipProtection Protection,
        double CeilingDbTp,
        EqMode Mode = EqMode.Graphic10,
        double Balance = 0,          // -1 (sinistra) .. +1 (destra)
        double Width = 1,            // 0 = mono, 1 = originale, 2 = doppia
        bool Mono = false,
        CrossfeedLevel Crossfeed = CrossfeedLevel.Off,
        bool Loudness = false)
    {
        // Prima di Default: i campi statici si inizializzano nell'ordine in cui sono scritti.
        public static readonly double[] GraphicFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        /// <summary>Frequenze ISO a terzi d'ottava (EQ grafico a 31 bande).</summary>
        public static readonly double[] ThirdOctaveFrequencies =
        {
            20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
            1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000
        };

        public static AudioDspSettings Default { get; } = new(false, GraphicBands(new double[10]), 0, ClipProtection.Limiter, -1.0);

        public bool IsGraphic => Mode != EqMode.Parametric;

        /// <summary>EQ grafico a 10 bande (ottave).</summary>
        public static IReadOnlyList<EqBand> GraphicBands(IReadOnlyList<double> gainsDb)
            => GraphicFrequencies.Select((f, i) => new EqBand(EqBandType.Peak,
                // Bande estreme piu' larghe: l'effetto prosegue dolcemente oltre 31 Hz e 16 kHz
                // (uno shelf, compensato, si impennava fino a +9 dB sopra i 16 kHz).
                f, i < gainsDb.Count ? gainsDb[i] : 0, i == 0 || i == GraphicFrequencies.Length - 1 ? 0.7 : 1.41)).ToList();

        /// <summary>EQ grafico a 31 bande (terzi d'ottava, Q 2,5: campane piu larghe, curva liscia fra i centri).</summary>
        public static IReadOnlyList<EqBand> ThirdOctaveBands(IReadOnlyList<double> gainsDb)
            => ThirdOctaveFrequencies.Select((f, i) => new EqBand(EqBandType.Peak,
                f, i < gainsDb.Count ? gainsDb[i] : 0, i == 0 || i == ThirdOctaveFrequencies.Length - 1 ? 2.0 : 2.5)).ToList();

        private static readonly ConcurrentDictionary<string, IReadOnlyList<EqBand>> CompensationCache = new();

        /// <summary>
        /// Le bande di un EQ grafico si sommano con le vicine: con i guadagni impostati tali e
        /// quali la curva non passa per i valori scelti (a +5 dB ovunque risultava +8 al centro
        /// e +3 ai bordi). Si risolve il sistema lineare delle interazioni (risposta di ogni
        /// banda a +1 dB al centro delle altre) e si rifinisce con le risposte reali: la curva
        /// passa per i valori impostati anche con 31 bande, in pochi millisecondi.
        /// </summary>
        public static IReadOnlyList<EqBand> Compensate(IReadOnlyList<EqBand> targets, double fs)
        {
            static bool Gain(EqBand b) => b.Enabled && b.Type is EqBandType.Peak or EqBandType.LowShelf or EqBandType.HighShelf;
            var peaks = targets.Where(Gain).ToList();
            if (peaks.Count < 2 || peaks.All(p => Math.Abs(p.GainDb) < 1e-6)) return targets;
            string key = fs.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(";", peaks.Select(p =>
                $"{(int)p.Type}:{p.FrequencyHz.ToString("0.###", CultureInfo.InvariantCulture)}:{p.Q.ToString("0.###", CultureInfo.InvariantCulture)}:{p.GainDb.ToString("0.###", CultureInfo.InvariantCulture)}"));
            if (CompensationCache.TryGetValue(key, out var cached)) return cached;

            int n = peaks.Count;
            var matrix = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    matrix[i, j] = Biquad.ResponseDb(peaks[j] with { GainDb = 1 }, fs, peaks[i].FrequencyHz);
            var target = peaks.Select(p => p.GainDb).ToArray();
            var gains = Solve(matrix, target);
            for (int pass = 0; pass < 4; pass++)
            {
                var current = peaks.Select((b, i) => b with { GainDb = Math.Clamp(gains[i], -30, 30) }).ToArray();
                var error = new double[n]; double worst = 0;
                for (int i = 0; i < n; i++)
                {
                    double response = 0;
                    for (int j = 0; j < n; j++) response += Biquad.ResponseDb(current[j], fs, peaks[i].FrequencyHz);
                    error[i] = target[i] - response; worst = Math.Max(worst, Math.Abs(error[i]));
                }
                if (worst < 0.01) break;
                var delta = Solve(matrix, error);
                for (int i = 0; i < n; i++) gains[i] += delta[i];
            }
            int k = 0;
            var result = targets.Select(b => Gain(b) ? b with { GainDb = Math.Clamp(gains[k++], -30, 30) } : b).ToList();
            if (CompensationCache.Count > 256) CompensationCache.Clear();
            CompensationCache[key] = result;
            return result;
        }

        // Eliminazione di Gauss con pivot parziale (matrici piccole: 10 o 31 bande).
        private static double[] Solve(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = (double[,])a.Clone(); var x = (double[])b.Clone();
            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
                if (Math.Abs(m[pivot, col]) < 1e-12) continue;
                if (pivot != col)
                {
                    for (int c = 0; c < n; c++) (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                    (x[col], x[pivot]) = (x[pivot], x[col]);
                }
                for (int r = col + 1; r < n; r++)
                {
                    double factor = m[r, col] / m[col, col];
                    if (factor == 0) continue;
                    for (int c = col; c < n; c++) m[r, c] -= factor * m[col, c];
                    x[r] -= factor * x[col];
                }
            }
            for (int r = n - 1; r >= 0; r--)
            {
                double sum = x[r];
                for (int c = r + 1; c < n; c++) sum -= m[r, c] * x[c];
                x[r] = Math.Abs(m[r, r]) < 1e-12 ? 0 : sum / m[r, r];
            }
            return x;
        }

        /// <summary>Bande effettive del motore: compensate per il grafico, tali e quali per il parametrico.</summary>
        public IReadOnlyList<EqBand> EffectiveBands(double fs) => IsGraphic ? Compensate(Bands, fs) : Bands;
    }

    /// <summary>Biquad RBJ ("Audio EQ Cookbook") in forma diretta I, un'istanza per canale.</summary>
    internal sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        public Biquad(EqBand band, double sampleRate) => Design(band, sampleRate, out _b0, out _b1, out _b2, out _a1, out _a2);

        /// <summary>Nuovi coefficienti mantenendo lo stato: nessun click quando si regola dal vivo.</summary>
        public void Retune(EqBand band, double sampleRate) => Design(band, sampleRate, out _b0, out _b1, out _b2, out _a1, out _a2);

        public static void Design(EqBand band, double fs, out double b0, out double b1, out double b2, out double a1, out double a2)
        {
            double f = Math.Clamp(band.FrequencyHz, 10, fs * 0.49);
            double q = Math.Clamp(band.Q, 0.1, 24);
            double w0 = 2 * Math.PI * f / fs, cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / (2 * q);
            if (band.Type == EqBandType.Peak)
            {
                // Larghezza in ottave pre-compensata per la bilineare (RBJ, forma BW): vicino a
                // Nyquist le campane restano larghe quanto dichiarato invece di stringersi, e l'EQ
                // a 31 bande non ondeggia sopra i 10 kHz. In basso il risultato e' identico.
                double bw = 2 / Math.Log(2) * Math.Asinh(1 / (2 * q));
                alpha = sin * Math.Sinh(Math.Log(2) / 2 * bw * w0 / sin);
            }
            double a = Math.Pow(10, band.GainDb / 40);
            double nb0, nb1, nb2, na0, na1, na2;
            switch (band.Type)
            {
                case EqBandType.LowShelf:
                {
                    double s = 2 * Math.Sqrt(a) * alpha;
                    nb0 = a * ((a + 1) - (a - 1) * cos + s); nb1 = 2 * a * ((a - 1) - (a + 1) * cos); nb2 = a * ((a + 1) - (a - 1) * cos - s);
                    na0 = (a + 1) + (a - 1) * cos + s; na1 = -2 * ((a - 1) + (a + 1) * cos); na2 = (a + 1) + (a - 1) * cos - s;
                    break;
                }
                case EqBandType.HighShelf:
                {
                    double s = 2 * Math.Sqrt(a) * alpha;
                    nb0 = a * ((a + 1) + (a - 1) * cos + s); nb1 = -2 * a * ((a - 1) + (a + 1) * cos); nb2 = a * ((a + 1) + (a - 1) * cos - s);
                    na0 = (a + 1) - (a - 1) * cos + s; na1 = 2 * ((a - 1) - (a + 1) * cos); na2 = (a + 1) - (a - 1) * cos - s;
                    break;
                }
                case EqBandType.LowPass:
                    nb0 = (1 - cos) / 2; nb1 = 1 - cos; nb2 = (1 - cos) / 2; na0 = 1 + alpha; na1 = -2 * cos; na2 = 1 - alpha;
                    break;
                case EqBandType.HighPass:
                    nb0 = (1 + cos) / 2; nb1 = -(1 + cos); nb2 = (1 + cos) / 2; na0 = 1 + alpha; na1 = -2 * cos; na2 = 1 - alpha;
                    break;
                default:
                    nb0 = 1 + alpha * a; nb1 = -2 * cos; nb2 = 1 - alpha * a; na0 = 1 + alpha / a; na1 = -2 * cos; na2 = 1 - alpha / a;
                    break;
            }
            b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0; a1 = na1 / na0; a2 = na2 / na0;
        }

        /// <summary>Guadagno in dB della banda alla frequenza <paramref name="hz"/>.</summary>
        public static double ResponseDb(EqBand band, double fs, double hz)
        {
            Design(band, fs, out double b0, out double b1, out double b2, out double a1, out double a2);
            double w = 2 * Math.PI * hz / fs;
            var z1 = System.Numerics.Complex.FromPolarCoordinates(1, -w);
            var z2 = z1 * z1;
            var h = (b0 + b1 * z1 + b2 * z2) / (1 + a1 * z1 + a2 * z2);
            return 20 * Math.Log10(Math.Max(1e-12, h.Magnitude));
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1;
            // Denormali: sotto 1e-25 lo stato vale zero (evita picchi di CPU nei silenzi).
            _y1 = Math.Abs(y) < 1e-25 ? 0 : y;
            return y;
        }
    }

    /// <summary>
    /// Limiter true-peak con lookahead (anti-clipping). Il picco fra un campione e l'altro
    /// e' stimato con un sovracampionamento 4x (FIR sinc finestrato); la riduzione di guadagno
    /// e' un mantenimento del minimo seguito da una media mobile della stessa durata, quindi al
    /// campione di picco il guadagno e' sempre sufficiente e non ci sono gradini udibili.
    /// </summary>
    internal sealed class TruePeakLimiter
    {
        private const int Taps = 8;           // campioni per lato del FIR di interpolazione
        private const int Phases = 4;         // sovracampionamento 4x
        private static readonly double[][] Kernel = BuildKernel();

        private readonly int _channels, _lookahead, _delay;
        private readonly double[] _history;   // ultimi 2*Taps campioni per canale (per l'interpolazione)
        private readonly double[] _delayLine; // audio ritardato: lookahead + ritardo del rivelatore
        private int _historyPos, _delayPos;
        private readonly double[] _holdWindow; private int _holdPos;
        private readonly double[] _boxWindow; private int _boxPos; private double _boxSum;
        private readonly double _releaseCoef;
        private double _gain = 1;

        public double CeilingLinear { get; set; }
        public double LastGainReductionDb { get; private set; }
        public long Activations { get; private set; }

        public TruePeakLimiter(int channels, double sampleRate, double ceilingDbTp, double lookaheadMs = 1.5, double releaseMs = 120)
        {
            _channels = channels;
            _lookahead = Math.Max(4, (int)Math.Round(sampleRate * lookaheadMs / 1000));
            _delay = _lookahead + Taps;
            _history = new double[channels * Taps * 2];
            _delayLine = new double[channels * (_delay + 1)];
            _holdWindow = new double[_lookahead + 1]; Array.Fill(_holdWindow, 1.0);
            _boxWindow = new double[_lookahead + 1]; Array.Fill(_boxWindow, 1.0); _boxSum = _boxWindow.Length;
            _releaseCoef = Math.Exp(-1.0 / (sampleRate * releaseMs / 1000));
            CeilingLinear = Math.Pow(10, ceilingDbTp / 20);
        }

        public int LatencyFrames => _delay;

        private static double[][] BuildKernel()
        {
            var k = new double[Phases][];
            for (int p = 0; p < Phases; p++)
            {
                k[p] = new double[Taps * 2];
                double frac = p / (double)Phases;
                double sum = 0;
                for (int i = 0; i < Taps * 2; i++)
                {
                    double t = i - (Taps - 1) - frac;             // distanza dal punto interpolato
                    double sinc = Math.Abs(t) < 1e-9 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                    double n = (i + 1 - frac) / (Taps * 2 + 1);
                    double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n) + 0.08 * Math.Cos(4 * Math.PI * n); // Blackman
                    k[p][i] = sinc * window; sum += k[p][i];
                }
                for (int i = 0; i < Taps * 2; i++) k[p][i] /= sum;
            }
            return k;
        }

        /// <summary>Elabora un fotogramma (un campione per canale) sul posto.</summary>
        public void Process(Span<double> frame, out double inputTruePeak)
        {
            double peak = 0;
            int hl = Taps * 2;
            for (int c = 0; c < _channels; c++)
            {
                _history[c * hl + _historyPos] = frame[c];
                for (int p = 0; p < Phases; p++)
                {
                    double acc = 0; var kern = Kernel[p];
                    for (int i = 0; i < hl; i++)
                        acc += kern[i] * _history[c * hl + (_historyPos + 1 + i) % hl];
                    peak = Math.Max(peak, Math.Abs(acc));
                }
            }
            _historyPos = (_historyPos + 1) % hl;
            inputTruePeak = peak;

            double need = peak > CeilingLinear ? CeilingLinear / peak : 1.0;
            _holdWindow[_holdPos] = need; _holdPos = (_holdPos + 1) % _holdWindow.Length;
            double held = 1.0; foreach (double v in _holdWindow) if (v < held) held = v;
            _boxSum += held - _boxWindow[_boxPos]; _boxWindow[_boxPos] = held; _boxPos = (_boxPos + 1) % _boxWindow.Length;
            double target = Math.Min(1.0, _boxSum / _boxWindow.Length);
            _gain = target < _gain ? target : target + (_gain - target) * _releaseCoef;
            if (_gain < 0.9999) { if (LastGainReductionDb > -0.01) Activations++; }
            LastGainReductionDb = 20 * Math.Log10(_gain);

            int stride = _delay + 1;
            for (int c = 0; c < _channels; c++)
            {
                _delayLine[c * stride + _delayPos] = frame[c];
                double delayed = _delayLine[c * stride + (_delayPos + 1) % stride];
                double y = delayed * _gain;
                frame[c] = Math.Clamp(y, -CeilingLinear, CeilingLinear);
            }
            _delayPos = (_delayPos + 1) % stride;
        }
    }

    /// <summary>Misure dal vivo lette dall'interfaccia (VU, picchi, clipping).</summary>
    internal sealed class AudioEngineMeters
    {
        public readonly double[] Rms = new double[2];          // lineare, finestra ~50 ms
        public readonly double[] Peak = new double[2];         // lineare, uscita
        public double InputTruePeakDb = double.NegativeInfinity; // prima del limiter (dopo EQ)
        public double GainReductionDb;                           // limiter (<= 0)
        public long SourceClipSamples;                           // campioni gia' saturati nel file
        public long WouldClipEvents;                             // eventi oltre 0 dBTP prima della protezione
        public long LimiterActivations;
        public double HeadroomDb;                                // riduzione automatica applicata
        public double ReplayGainDb;                              // guadagno ReplayGain applicato
        public double LoudnessBassDb;                            // compensazione loudness attuale (bassi)
        public int SampleRate, Channels;
        public string OutputDescription = "";
        public long Revision;
    }

    /// <summary>Catena completa, per un certo numero di canali e frequenza di campionamento.</summary>
    internal sealed class AudioDspChain
    {
        private readonly int _channels;
        private readonly double _rate;
        private Biquad[][] _filters = Array.Empty<Biquad[]>();
        private TruePeakLimiter? _limiter;
        private double _preGain = 1, _volume = 1, _replayGainDb;
        private AudioDspSettings _settings = AudioDspSettings.Default;
        private readonly double[] _frame;
        private readonly double[] _rmsAcc = new double[2];
        private readonly double[] _peakAcc = new double[2];
        private int _meterFrames;
        private bool _wasOver;

        // crossfeed (Bauer, semplificato): passa-basso del canale opposto miscelato al diretto
        private double _cfGain, _cfAlpha, _cfLpL, _cfLpR;
        // loudness: shelf su bassi e alti in funzione del volume (dopo il volume)
        private readonly Biquad[] _loudLow = new Biquad[2], _loudHigh = new Biquad[2];
        private bool _loudnessActive;
        private double _loudnessForVolumeDb = double.NaN;

        public AudioEngineMeters Meters { get; } = new();

        public AudioDspChain(int channels, double sampleRate)
        {
            _channels = Math.Max(1, channels);
            _rate = sampleRate;
            _frame = new double[_channels];
            for (int c = 0; c < 2; c++)
            {
                _loudLow[c] = new Biquad(new EqBand(EqBandType.LowShelf, 100, 0, 0.7), _rate);
                _loudHigh[c] = new Biquad(new EqBand(EqBandType.HighShelf, 8000, 0, 0.7), _rate);
            }
            Meters.SampleRate = (int)sampleRate; Meters.Channels = _channels;
            Apply(AudioDspSettings.Default);
        }

        public double Volume
        {
            get => _volume;
            set { _volume = Math.Clamp(value, 0, 1); UpdateLoudness(); }
        }

        private double _boostDb;

        /// <summary>
        /// Amplificazione oltre il 100% chiesta dall'ascoltatore (0..12 dB). Entra prima del limiter:
        /// i passaggi deboli salgono, i picchi vengono trattenuti invece di distorcere.
        /// </summary>
        public double BoostDb
        {
            get => _boostDb;
            set { _boostDb = Math.Clamp(value, 0, 12); Apply(_settings); }
        }

        /// <summary>Guadagno ReplayGain del brano (dai tag), applicato prima di EQ e protezione.</summary>
        public double ReplayGainDb
        {
            get => _replayGainDb;
            set { _replayGainDb = Math.Clamp(value, -24, 12); Apply(_settings); }
        }

        /// <summary>Massimo guadagno (dB) della curva EQ sull'intera banda udibile.</summary>
        public static double MaxBoostDb(AudioDspSettings s, double fs)
        {
            if (!s.EqEnabled) return 0;
            var bands = s.EffectiveBands(fs).Where(b => b.Enabled).ToList();
            if (bands.Count == 0) return 0;
            double max = double.NegativeInfinity;
            for (int i = 0; i <= 160; i++)
            {
                double hz = 20 * Math.Pow(1000, i / 160.0); // 20 Hz .. 20 kHz
                if (hz >= fs / 2) break;
                double sum = 0;
                foreach (var b in bands) sum += Biquad.ResponseDb(b, fs, hz);
                max = Math.Max(max, sum);
            }
            return Math.Max(0, max);
        }

        public void Apply(AudioDspSettings settings)
        {
            _settings = settings;
            var active = settings.EqEnabled
                ? settings.EffectiveBands(_rate).Where(b => b.Enabled && (Math.Abs(b.GainDb) > 0.01 || b.Type is EqBandType.LowPass or EqBandType.HighPass)).ToList()
                : new List<EqBand>();
            var filters = new Biquad[_channels][];
            for (int c = 0; c < _channels; c++) filters[c] = active.Select(b => new Biquad(b, _rate)).ToArray();
            _filters = filters;
            // Headroom automatico: EQ e ReplayGain positivo non possono spingere oltre 0 dBFS.
            double boost = MaxBoostDb(settings, _rate) + Math.Max(0, _replayGainDb) + Math.Max(0, settings.PreampDb) + (settings.Width > 1 ? 20 * Math.Log10(settings.Width) : 0);
            double headroom = settings.Protection != ClipProtection.Off ? -boost + Math.Max(0, settings.PreampDb) : 0;
            Meters.HeadroomDb = headroom;
            Meters.ReplayGainDb = _replayGainDb;
            _preGain = Math.Pow(10, (settings.PreampDb + _replayGainDb + headroom + _boostDb) / 20);
            // Con l'amplificazione il limiter c'e' sempre, anche se la protezione e' spenta.
            if (settings.Protection == ClipProtection.Limiter || _boostDb > 0)
            {
                if (_limiter == null) _limiter = new TruePeakLimiter(_channels, _rate, settings.CeilingDbTp);
                else _limiter.CeilingLinear = Math.Pow(10, settings.CeilingDbTp / 20);
            }
            else _limiter = null;

            // Crossfeed: livelli come bs2b (frequenza di taglio, attenuazione del canale opposto).
            (double cut, double feedDb) = settings.Crossfeed switch
            {
                CrossfeedLevel.Low => (650.0, 9.5),
                CrossfeedLevel.Medium => (700.0, 6.0),
                CrossfeedLevel.High => (700.0, 4.5),
                _ => (0.0, 0.0)
            };
            _cfGain = cut > 0 ? Math.Pow(10, -feedDb / 20) : 0;
            _cfAlpha = cut > 0 ? Math.Exp(-2 * Math.PI * cut / _rate) : 0;
            _loudnessActive = settings.Loudness && _channels >= 1;
            _loudnessForVolumeDb = double.NaN;
            UpdateLoudness();
        }

        // Compensazione ISO 226 semplificata: a volume basso l'orecchio perde bassi e alti.
        // Bassi +0,35 dB e alti +0,15 dB per ogni dB di attenuazione (max 12 / 6 dB).
        private void UpdateLoudness()
        {
            if (!_loudnessActive) { Meters.LoudnessBassDb = 0; return; }
            double attenuation = _volume <= 0.0001 ? 60 : -20 * Math.Log10(_volume);
            if (!double.IsNaN(_loudnessForVolumeDb) && Math.Abs(attenuation - _loudnessForVolumeDb) < 0.25) return;
            _loudnessForVolumeDb = attenuation;
            double bass = Math.Min(12, attenuation * 0.35), treble = Math.Min(6, attenuation * 0.15);
            for (int c = 0; c < 2; c++)
            {
                _loudLow[c].Retune(new EqBand(EqBandType.LowShelf, 100, bass, 0.7), _rate);
                _loudHigh[c].Retune(new EqBand(EqBandType.HighShelf, 8000, treble, 0.7), _rate);
            }
            Meters.LoudnessBassDb = bass;
        }

        public int LatencyFrames => _limiter?.LatencyFrames ?? 0;

        /// <summary>Elabora <paramref name="frames"/> fotogrammi interlacciati sul posto.</summary>
        /// <summary>
        /// Se impostato, riceve il segnale dopo EQ, DSP e limiter ma prima del volume: le analisi
        /// (LUFS, true peak, VU) descrivono il brano e non cambiano girando la manopola.
        /// </summary>
        public float[]? AnalysisOut { get; set; }

        public void Process(Span<float> interleaved, int frames)
        {
            var analysis = AnalysisOut is { } a0 && a0.Length >= frames * _channels ? a0 : null;
            var filters = _filters; var limiter = _limiter; var s = _settings;
            double pre = _preGain, vol = _volume;
            int meterChannels = Math.Min(2, _channels);
            bool stereo = _channels >= 2;
            double cfGain = _cfGain, cfA = _cfAlpha, cfNorm = 1 / (1 + cfGain);
            double width = s.Width, balance = s.Balance;
            double balL = balance > 0 ? 1 - balance : 1, balR = balance < 0 ? 1 + balance : 1;
            bool loud = _loudnessActive;
            for (int f = 0; f < frames; f++)
            {
                int o = f * _channels;
                for (int c = 0; c < _channels; c++)
                {
                    double x = interleaved[o + c];
                    if (Math.Abs(x) >= 0.99997) Meters.SourceClipSamples++;
                    x *= pre;
                    var chain = filters[c];
                    for (int i = 0; i < chain.Length; i++) x = chain[i].Process(x);
                    _frame[c] = x;
                }
                if (stereo)
                {
                    double l = _frame[0], r = _frame[1];
                    if (cfGain > 0)
                    {
                        _cfLpL = (1 - cfA) * l + cfA * _cfLpL;
                        _cfLpR = (1 - cfA) * r + cfA * _cfLpR;
                        double nl = (l + cfGain * _cfLpR) * cfNorm, nr = (r + cfGain * _cfLpL) * cfNorm;
                        l = nl; r = nr;
                    }
                    if (s.Mono) { double m = (l + r) * 0.5; l = m; r = m; }
                    else if (Math.Abs(width - 1) > 1e-6)
                    {
                        double mid = (l + r) * 0.5, side = (l - r) * 0.5 * width;
                        l = mid + side; r = mid - side;
                    }
                    _frame[0] = l * balL; _frame[1] = r * balR;
                }
                double tp;
                if (limiter != null) { limiter.Process(_frame, out tp); }
                else
                {
                    tp = 0;
                    for (int c = 0; c < _channels; c++) { tp = Math.Max(tp, Math.Abs(_frame[c])); _frame[c] = Math.Clamp(_frame[c], -1, 1); }
                }
                bool over = tp > 1.0;
                if (over && !_wasOver) Meters.WouldClipEvents++;
                _wasOver = over;
                if (tp > 0) Meters.InputTruePeakDb = Math.Max(Meters.InputTruePeakDb, 20 * Math.Log10(tp));
                if (analysis != null)
                    for (int c = 0; c < _channels; c++) analysis[o + c] = (float)_frame[c];
                for (int c = 0; c < _channels; c++)
                {
                    double y = _frame[c] * vol;
                    if (loud && c < 2) y = _loudHigh[c].Process(_loudLow[c].Process(y));
                    interleaved[o + c] = (float)y;
                    if (c < meterChannels)
                    {
                        _rmsAcc[c] += y * y;
                        double a = Math.Abs(y); if (a > _peakAcc[c]) _peakAcc[c] = a;
                    }
                }
                if (meterChannels == 1) { _rmsAcc[1] = _rmsAcc[0]; _peakAcc[1] = _peakAcc[0]; }
                if (++_meterFrames >= (int)(_rate / 50)) PublishMeters(limiter);
            }
        }

        private void PublishMeters(TruePeakLimiter? limiter)
        {
            for (int c = 0; c < 2; c++)
            {
                Meters.Rms[c] = Math.Sqrt(_rmsAcc[c] / Math.Max(1, _meterFrames));
                Meters.Peak[c] = _peakAcc[c];
                _rmsAcc[c] = 0; _peakAcc[c] = 0;
            }
            Meters.GainReductionDb = limiter?.LastGainReductionDb ?? 0;
            Meters.LimiterActivations = limiter?.Activations ?? 0;
            Meters.Revision++;
            _meterFrames = 0;
            Meters.InputTruePeakDb = Math.Max(-120, Meters.InputTruePeakDb - 0.4);
        }
    }
}
