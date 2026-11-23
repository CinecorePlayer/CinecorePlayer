#nullable enable
using System;

namespace CinecorePlayer2025.Audio
{
    /// <summary>
    /// Cambia il tempo di un flusso senza cambiarne l'intonazione (WSOLA: finestre di 40 ms sovrapposte a meta',
    /// ognuna presa dove continua meglio la precedente entro +-8 ms). Serve all'automix per portare il brano che
    /// entra al tempo di quello che esce. <see cref="Ratio"/> e' il tempo relativo: 1,05 = 5% piu' veloce (si
    /// consumano 1,05 fotogrammi di ingresso per ogni fotogramma in uscita). Con Ratio 1 l'uscita e' il segnale
    /// originale (le finestre di Hann a meta' sovrapposizione sommano a uno).
    /// </summary>
    internal sealed class TimeStretcher : ITimeStretch
    {
        private readonly int _channels, _frame, _hop, _delta;
        private readonly float[] _window;
        private float[] _in;              // ingresso interlacciato non ancora scartato
        private int _inFrames;            // fotogrammi validi in _in
        private double _inPos;            // dove comincia, nell'ingresso, la prossima finestra (nominale)
        private int _prevStart = -1;      // inizio della finestra scelta prima
        private readonly float[] _ola;    // somma delle finestre (N fotogrammi)
        private readonly float[] _ready;  // fotogrammi pronti da consegnare (un passo)
        private int _readyCount, _readyRead;
        private double _readyMedia, _readyRate; // posizione nell'ingresso e velocita' dei fotogrammi pronti
        private long _consumedBase;       // fotogrammi d'ingresso gia' scartati da _in (posizione assoluta di _in[0])

        public double Ratio { get; set; } = 1;
        public bool InputComplete { get; set; }
        private long? _lastInputFrame;

        /// <summary>Fotogramma d'ingresso (assoluto) e velocita' a cui corrisponde il prossimo fotogramma in uscita.</summary>
        public (double Media, double Rate) NextOutput => _readyRead < _readyCount ? (_readyMedia + (_readyRead) * _readyRate, _readyRate) : (_consumedBase + _inPos, Ratio);

        public TimeStretcher(int sampleRate, int channels)
        {
            _channels = channels;
            _frame = Math.Max(256, (int)(sampleRate * 0.04) & ~1);
            _hop = _frame / 2;
            _delta = (int)(sampleRate * 0.008); // ricerca stretta: i colpi restano a tempo (+-8 ms al massimo)
            _window = new float[_frame];
            for (int i = 0; i < _frame; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / _frame)); // periodica: somma esatta a 1
            _in = new float[(_frame * 6 + _delta * 4) * channels];
            _ola = new float[_frame * channels];
            _ready = new float[_hop * channels];
        }

        /// <summary>
        /// Riempie fino a <paramref name="frames"/> fotogrammi di <paramref name="target"/> da <paramref name="offset"/>.
        /// <paramref name="read"/> fornisce l'ingresso (destinazione, posizione, campioni) e dice quanti ne ha dati.
        /// Restituisce i fotogrammi prodotti; <paramref name="spans"/> riceve, per ogni tratto, posizione d'ingresso e velocita'.
        /// </summary>
        public int Produce(float[] target, int offset, int frames, Func<float[], int, int, int> read, Action<int, double, double>? spans = null)
        {
            int ch = _channels, produced = 0;
            while (produced < frames)
            {
                if (_readyRead >= _readyCount && !Step(read)) break;
                int n = Math.Min(frames - produced, _readyCount - _readyRead);
                spans?.Invoke(n, _readyMedia + _readyRead * _readyRate, _readyRate);
                Array.Copy(_ready, _readyRead * ch, target, offset + produced * ch, n * ch);
                _readyRead += n; produced += n;
            }
            return produced;
        }

        private bool Step(Func<float[], int, int, int> read)
        {
            if (_lastInputFrame is long last && _consumedBase + _inPos >= last) return false;
            int ch = _channels;
            double ratio = Math.Clamp(Ratio, 0.5, 2.0);
            int center = (int)Math.Round(_inPos);
            int natural = _prevStart < 0 ? center : _prevStart + _hop;
            int need = Math.Max(center + _delta, natural) + _frame + 1;
            if (!Fill(need, read)) return false;

            int chosen = center;
            // A tempo invariato la continuazione naturale e' esatta: niente ricerca (resta attivo dopo i passaggi).
            if (_prevStart >= 0 && Math.Abs(ratio - 1) < 1e-6 && Math.Abs(natural - center) <= _delta) chosen = natural;
            else if (_prevStart >= 0)
            {
                // La finestra che continua meglio la precedente: correlazione sul mono, prima a passi di 4, poi fine.
                int lo = Math.Max(0, center - _delta), hi = center + _delta;
                int len = _hop;
                double Score(int start)
                {
                    double sum = 0;
                    for (int i = 0; i < len; i += 2)
                    {
                        int a = (start + i) * ch, b = (natural + i) * ch;
                        float x = _in[a], y = _in[b];
                        if (ch > 1) { x += _in[a + 1]; y += _in[b + 1]; }
                        sum += x * y;
                    }
                    return sum;
                }
                double best = double.NegativeInfinity;
                for (int s = lo; s <= hi; s += 4) { double v = Score(s); if (v > best) { best = v; chosen = s; } }
                int coarse = chosen;
                for (int s = Math.Max(lo, coarse - 3); s <= Math.Min(hi, coarse + 3); s++) { double v = Score(s); if (v > best) { best = v; chosen = s; } }
                // A tempo invariato la continuazione naturale e' esatta: niente spostamenti inutili.
                if (Math.Abs(ratio - 1) < 1e-6 && natural >= lo && natural <= hi) chosen = natural;
            }

            // Somma della finestra scelta, consegna del primo passo, scorrimento.
            for (int i = 0; i < _frame; i++)
            {
                float w = _window[i];
                int src = (chosen + i) * ch, dst = i * ch;
                for (int c = 0; c < ch; c++) _ola[dst + c] += _in[src + c] * w;
            }
            Array.Copy(_ola, 0, _ready, 0, _hop * ch);
            Array.Copy(_ola, _hop * ch, _ola, 0, (_frame - _hop) * ch);
            Array.Clear(_ola, (_frame - _hop) * ch, _hop * ch);
            _readyCount = _hop; _readyRead = 0;
            _readyMedia = _consumedBase + chosen; _readyRate = ratio;

            _prevStart = chosen;
            _inPos += _hop * ratio;

            // Scarta l'ingresso che non serve piu'.
            int keepFrom = Math.Max(0, Math.Min(_prevStart, (int)_inPos - _delta) - 4);
            if (keepFrom > _frame)
            {
                Array.Copy(_in, keepFrom * ch, _in, 0, (_inFrames - keepFrom) * ch);
                _inFrames -= keepFrom; _inPos -= keepFrom; _prevStart -= keepFrom; _consumedBase += keepFrom;
            }
            return true;
        }

        private bool Fill(int frames, Func<float[], int, int, int> read)
        {
            int ch = _channels;
            if (frames * ch > _in.Length) Array.Resize(ref _in, frames * ch * 2);
            while (_inFrames < frames)
            {
                int got = read(_in, _inFrames * ch, (frames - _inFrames) * ch);
                if (got <= 0)
                {
                    if (!InputComplete) return false;
                    _lastInputFrame ??= _consumedBase + _inFrames;
                    if (_consumedBase + _inPos >= _lastInputFrame.Value) return false;
                    Array.Clear(_in, _inFrames * ch, (frames - _inFrames) * ch);
                    _inFrames = frames;
                    break;
                }
                _inFrames += got / ch;
            }
            return true;
        }
    }
}
