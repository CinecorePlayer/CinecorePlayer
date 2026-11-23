#nullable enable
using System;

namespace CinecorePlayer2025.Audio
{
    /// <summary>
    /// Livello d'ascolto fuori dal player: il volume del ricevitore collegato (0 dB = riferimento) e quello di Windows.
    /// Lo aggiorna il player; l'AutoEQ dinamico lo somma all'attenuazione del volume digitale.
    /// </summary>
    internal static class ListeningLevel
    {
        private static double _receiverDb = double.NaN, _systemDb;
        /// <summary>Volume del ricevitore in dB rispetto al riferimento (es. -33), NaN se non collegato.</summary>
        public static double ReceiverDb { get => _receiverDb; set => _receiverDb = double.IsFinite(value) ? Math.Clamp(value, -90, 18) : double.NaN; }
        /// <summary>Volume di Windows sull'uscita in dB (0 = massimo, negativo = attenuato).</summary>
        public static double SystemDb { get => _systemDb; set => _systemDb = double.IsFinite(value) ? Math.Clamp(value, -96, 0) : 0; }
        /// <summary>Attenuazione esterna totale (dB, positiva = piu' basso del riferimento).</summary>
        public static double ExternalAttenuationDb
        {
            get
            {
                EnsureWatch();
                // In esclusiva il volume di Windows non agisce sul segnale.
                double system = _systemDb;
                return Math.Max(0, -(double.IsFinite(_receiverDb) ? _receiverDb : 0) - system);
            }
        }

        // Sul Mac il volume di sistema non si legge: conta il volume del player (e quello del ricevitore, se noto).
        private static void EnsureWatch() { }
    }

    /// <summary>
    /// AutoEQ dinamico, sul modello del Dynamic EQ di Audyssey dei ricevitori Denon/Marantz: abbassando il volume
    /// l'orecchio perde bassi (e un poco gli altissimi) secondo le curve isofoniche ISO 226:2003. Qui le curve si
    /// calcolano esatte per il livello di riferimento del brano e per quello d'ascolto vero (volume del player, del
    /// ricevitore e di Windows, piu' il livello del brano istante per istante) e la differenza diventa due shelf sui
    /// bassi e uno sugli alti. Con l'offset di riferimento (0/5/10/15 dB, come sui ricevitori) si dice quanto la
    /// musica e' gia' masterizzata forte.
    /// </summary>
    internal sealed class DynamicEq
    {
        // f, af, Lu, Tf della norma ISO 226:2003 (tabella 1).
        private static readonly double[] F = { 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500 };
        private static readonly double[] Af = { .532, .506, .480, .455, .432, .409, .387, .367, .349, .330, .315, .301, .288, .276, .267, .259, .253, .250, .246, .244, .243, .243, .243, .242, .242, .245, .254, .271, .301 };
        private static readonly double[] Lu = { -31.6, -27.2, -23.0, -19.1, -15.9, -13.0, -10.3, -8.1, -6.2, -4.5, -3.1, -2.0, -1.1, -0.4, 0, .3, .5, 0, -2.7, -4.1, -1.0, 1.7, 2.5, 1.2, -2.1, -7.1, -11.2, -10.7, -3.1 };
        private static readonly double[] Tf = { 78.5, 68.7, 59.5, 51.1, 44.0, 37.5, 31.5, 26.5, 22.1, 17.9, 14.4, 11.4, 8.6, 6.2, 4.4, 3.0, 2.2, 2.4, 3.5, 1.7, -1.3, -4.2, -6.0, -5.4, -1.5, 6.0, 12.6, 13.9, 12.3 };

        /// <summary>Livello di pressione (dB SPL) che a <paramref name="phon"/> phon suona come 1 kHz allo stesso livello.</summary>
        public static double Spl(int index, double phon)
        {
            phon = Math.Clamp(phon, 0, 100);
            double af = Af[index];
            double a = 4.47e-3 * (Math.Pow(10, 0.025 * phon) - 1.15) + Math.Pow(0.4 * Math.Pow(10, (Tf[index] + Lu[index]) / 10 - 9), af);
            return 10 / af * Math.Log10(Math.Max(1e-12, a)) - Lu[index] + 94;
        }

        /// <summary>Rinforzo che serve alla frequenza <paramref name="index"/> passando da <paramref name="reference"/> a <paramref name="actual"/> phon.</summary>
        public static double Boost(int index, double reference, double actual)
            => (Spl(index, actual) - actual) - (Spl(index, reference) - reference);

        private static int Index(double hz) { int best = 0; for (int i = 1; i < F.Length; i++) if (Math.Abs(Math.Log(F[i] / hz)) < Math.Abs(Math.Log(F[best] / hz))) best = i; return best; }
        private static readonly int I40 = Index(40), I125 = Index(125), I10k = Index(10000);

        private readonly int _channels;
        private readonly double _rate;
        private readonly Biquad[] _sub, _low, _high;
        private double _subDb, _lowDb, _highDb, _makeupDb;
        private double _energy; private int _frames;
        private double _contentDb = -18;
        public double OffsetDb { get; set; }

        public DynamicEq(int channels, double rate)
        {
            _channels = Math.Max(1, channels); _rate = rate;
            _sub = new Biquad[_channels]; _low = new Biquad[_channels]; _high = new Biquad[_channels];
            for (int c = 0; c < _channels; c++)
            {
                _sub[c] = new Biquad(new EqBand(EqBandType.LowShelf, 50, 0, 0.7), rate);
                _low[c] = new Biquad(new EqBand(EqBandType.LowShelf, 160, 0, 0.6), rate);
                _high[c] = new Biquad(new EqBand(EqBandType.HighShelf, 7000, 0, 0.7), rate);
            }
        }

        /// <summary>Rinforzo attuale dei bassi (per le informazioni).</summary>
        public double BassDb => _lowDb + _subDb;
        public double TrebleDb => _highDb;

        /// <summary>
        /// Elabora un fotogramma (prima del limiter). <paramref name="digitalAttenuationDb"/> = quanto il volume digitale
        /// abbassa dopo; serve a non far saturare quando l'attenuazione e' tutta fuori (ricevitore).
        /// </summary>
        public void Process(double[] frame, double digitalAttenuationDb)
        {
            double sum = 0;
            for (int c = 0; c < _channels; c++) sum += frame[c] * frame[c];
            _energy += sum / _channels;
            if (++_frames >= 1024) Update(digitalAttenuationDb);
            double makeup = _makeupLinear;
            for (int c = 0; c < _channels; c++)
                frame[c] = _high[c].Process(_low[c].Process(_sub[c].Process(frame[c]))) * makeup;
        }

        private double _makeupLinear = 1;

        private void Update(double digitalAttenuationDb)
        {
            double rms = Math.Sqrt(_energy / Math.Max(1, _frames));
            double seconds = _frames / _rate;
            _energy = 0; _frames = 0;
            // Livello del brano (RMS in dBFS), lento in discesa: le pause non fanno pompare i bassi.
            if (rms > 1e-4)
            {
                double db = 20 * Math.Log10(rms);
                double tau = db > _contentDb ? 0.4 : 2.0;
                _contentDb += (db - _contentDb) * (1 - Math.Exp(-seconds / tau));
            }
            // -20 dBFS RMS al volume di riferimento = 85 phon (la taratura dei ricevitori). L'offset dice quanto il
            // materiale e' piu' forte del riferimento: con 10 dB la compensazione parte 10 dB piu' in basso.
            double attenuation = Math.Max(0, digitalAttenuationDb + ListeningLevel.ExternalAttenuationDb - Math.Max(0, OffsetDb));
            double reference = Math.Clamp(85 + (_contentDb + 20), 40, 100);
            double actual = Math.Clamp(reference - attenuation, 20, 100);
            double low = Math.Clamp(Boost(I125, reference, actual), 0, 12);
            double sub = Math.Clamp(Boost(I40, reference, actual) - low, 0, 6);
            double high = Math.Clamp(Boost(I10k, reference, actual), 0, 5);
            // Sale e scende piano (circa 15 dB al secondo): niente scatti.
            double step = 15 * seconds;
            double Move(double from, double to) => from + Math.Clamp(to - from, -step, step);
            double nSub = Move(_subDb, sub), nLow = Move(_lowDb, low), nHigh = Move(_highDb, high);
            // Il rinforzo che il volume digitale non ricopre si toglie a tutto il segnale (in parte): il limiter non lavora.
            double makeup = -Math.Max(0, (nLow + nSub) * 0.6 - digitalAttenuationDb);
            _makeupDb = Move(_makeupDb, makeup); _makeupLinear = Math.Pow(10, _makeupDb / 20);
            if (Math.Abs(nSub - _subDb) > 0.02 || Math.Abs(nLow - _lowDb) > 0.02 || Math.Abs(nHigh - _highDb) > 0.02)
            {
                _subDb = nSub; _lowDb = nLow; _highDb = nHigh;
                for (int c = 0; c < _channels; c++)
                {
                    _sub[c].Retune(new EqBand(EqBandType.LowShelf, 50, _subDb, 0.7), _rate);
                    _low[c].Retune(new EqBand(EqBandType.LowShelf, 160, _lowDb, 0.6), _rate);
                    _high[c].Retune(new EqBand(EqBandType.HighShelf, 7000, _highDb, 0.7), _rate);
                }
            }
        }
    }
}
