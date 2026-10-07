// LoopbackSampler.cs
#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Loopback + metriche audiofilo:
    /// - RMS/Peak/PeakHold (+ silence-gating)
    /// - True-Peak: parabolico + oversampling 4× (Catmull–Rom) low-latency (approssimato)
    /// - DC offset per canale (EMA ~0.5 s)
    /// - Correlazione Pearson DC-free [-1..+1]
    /// - Balance [-1..+1]
    /// - Mid/Side Width (dB) + compatibilità mono
    /// - Crest factor L/R (dB)
    /// - Spettro dBFS dall'energia dei canali L/R, con Hann e normalizzazione coerente
    /// - Dominant frequency (con sub-bin parabolico), spectral centroid, spectral roll-off 95%
    /// - Noise floor stimato (p10) + SNR e ENOB
    /// - Loudness EBU R128: M (400 ms), S (3 s), Integrated con gating, LRA (rolling)
    /// - Oscilloscopio L/R (ring buffer)
    /// </summary>
    internal sealed class LoopbackSampler : IDisposable
    {
        // === Config ===
        private const int FFT_N = 8192;                 // 5.4 Hz resolution at 44.1 kHz
        private const int FFT_M = 13;
        private const int SCOPE_N = 1024;
        private const double PEAKHOLD_DECAY_DBPS = 6.0; // dB/s
        private const double SILENCE_DBFS = -65.0;
        private const double EPS = 1e-12;

        // Oversampled True-Peak
        private const bool TP_OVERSAMPLE4X = true;      // abilita 4× Catmull–Rom (approssimazione low-latency)

        // Loudness / R128
        private const double M_WIN_S = 0.400;           // momentary
        private const double S_WIN_S = 3.000;           // short-term
        private const double BLOCK_S = 0.400;           // blocchi integrazione/gating
        private const double LRA_BUFFER_S = 60.0;       // storico ST per LRA (rolling)
        private const double K_LUFS_OFFSET = -0.691;    // offset ITU-R BS.1770

        private WasapiLoopbackCapture? _cap;
        private volatile bool _running;

        // Stereo RMS spectrum: phase-inverted channels must retain their energy.
        private readonly float[] _fftMid = new float[FFT_N], _fftRight = new float[FFT_N];
        private int _fftPos, _fftCount, _fftSinceTransform;
        private double[] _lastSpectrum = Array.Empty<double>();
        internal bool IsRunning => _running && _cap != null;
        private void AppendFft(float left, float right)
        {
            _fftMid[_fftPos] = left;
            _fftRight[_fftPos] = right;
            _fftPos = (_fftPos + 1) % FFT_N;
            _fftCount = Math.Min(FFT_N, _fftCount + 1);
            _fftSinceTransform++;
        }

        // Hann e normalizzazione per-bin
        private readonly float[] _win = BuildHann(FFT_N);
        private readonly double _denMain;  // 0.5 * Σ Hann / N (bin 1..N/2-1)
        private readonly double _denEdge;  // Σ Hann / N       (bin 0 e N/2)

        // Peak hold
        private float _peakHoldL, _peakHoldR;
        private DateTime _lastPeakUpdate = DateTime.MinValue;

        // Oscilloscopio
        private readonly float[] _scopeL = new float[SCOPE_N];
        private readonly float[] _scopeR = new float[SCOPE_N];
        private int _scopeW;
        // Four seconds of min/max PCM buckets, independent of callback size/FPS.
        private readonly float[] _waveL = new float[SCOPE_N], _waveR = new float[SCOPE_N];
        private int _waveW;
        private double _wavePhase;
        private float _waveMinL, _waveMaxL, _waveMinR, _waveMaxR;
        private void AppendWaveform(float left, float right)
        {
            _waveMinL=Math.Min(_waveMinL,left); _waveMaxL=Math.Max(_waveMaxL,left);
            _waveMinR=Math.Min(_waveMinR,right); _waveMaxR=Math.Max(_waveMaxR,right);
            _wavePhase += 128d / Math.Max(1,_sr);
            if (_wavePhase < 1) return;
            _wavePhase -= 1;
            _waveL[_waveW]=_waveMinL; _waveR[_waveW]=_waveMinR;
            _waveL[_waveW+1]=_waveMaxL; _waveR[_waveW+1]=_waveMaxR;
            _waveW=(_waveW+2)%SCOPE_N;
            _waveMinL=_waveMaxL=_waveMinR=_waveMaxR=0;
        }
        private float[] WaveformSnapshot(float[] buffer)
        {
            var result=new float[SCOPE_N];
            Array.Copy(buffer,_waveW,result,0,SCOPE_N-_waveW);
            Array.Copy(buffer,0,result,SCOPE_N-_waveW,_waveW);
            return result;
        }

        // ===== True-Peak & clipping =====
        private float _tpHoldL, _tpHoldR;          // picco vero tenuto per frame
        private long _clipEvents;                  // contatore clip (true-peak >= 0 dBFS)
        private long _clipSampleEvents;            // contatore clip sample-peak

        // Parabolico & 4× cubic: mantieni 3/4 campioni precedenti
        private float _prevL1, _prevL2, _prevL3;
        private float _prevR1, _prevR2, _prevR3;

        // ===== DC offset (EMA) =====
        private double _emaDcL, _emaDcR;
        private double _dcAlpha = 0.0;             // calcolato in base al SR

        // ===== Mid/Side energy (per Width) =====
        private double _sumM2, _sumS2;

        // ===== SR cache =====
        private int _sr;

        // ===== K-weighting (BS.1770) =====
        private WeightingBiquad? _kHpL, _kHpR;
        private WeightingBiquad? _kShelfL, _kShelfR;

        private sealed class WeightingBiquad(double b0, double b1, double b2, double a1, double a2)
        {
            private double _s1, _s2;
            public float Transform(float sample)
            {
                double output = b0 * sample + _s1;
                _s1 = b1 * sample - a1 * output + _s2;
                _s2 = b2 * sample - a2 * output;
                return (float)output;
            }
        }

        // ===== R128 windows (energies a densità) =====
        private readonly EnergyWindow _winM = new(M_WIN_S);
        private readonly EnergyWindow _winS = new(S_WIN_S);

        // blocchi per Integrated gating (lista di blocchi 400 ms dalla partenza)
        private double _blockAccumEnergy;          // somma (ms * dt) del blocco corrente
        private double _blockAccumDur;             // durata accumulata
        private readonly List<double> _intBlocksMs = new(2048); // mean-square per blocco

        // short-term history per LRA (rolling)
        private readonly List<StampedValue> _stHistory = new(1024);

        // ===== Payload =====
        public sealed class AudioMetrics
        {
            public float RmsL, RmsR;
            /// <summary>Somma dei quadrati e numero di frame dall'avvio: chi legge solo alcune
            /// misure ricava la media esatta dell'intervallo, senza perdere i blocchi saltati.</summary>
            public double EnergyL, EnergyR, EnergyFrames;
            public float PeakL, PeakR;
            public float PeakHoldL, PeakHoldR;
            public double[] SpectrumDb = Array.Empty<double>(); // N/2+1 (DC..Nyquist)
            public double Correlation;
            public double Balance;
            public double CrestL_dB, CrestR_dB;
            public float[] ScopeL = Array.Empty<float>();
            public float[] ScopeR = Array.Empty<float>();
            public float[] WaveformL = Array.Empty<float>();
            public float[] WaveformR = Array.Empty<float>();
            public int SampleRate;
            public bool IsSilent;
            public int FftLength;

            // Extra pro / audiofilo
            public double DbTpL, DbTpR;           // True-Peak dBTP
            public long ClipEvents;               // True-peak clip count
            public long ClipSampleEvents;         // Sample-peak clip count
            public double DcL, DcR;               // DC offset
            public double WidthDb;                // 10*log10(E_S/E_M)
            public double MonoCompat;             // proxy: correlazione
            public double DominantHz;             // picco spettrale
            public double SpectralCentroidHz;     // baricentro spettrale
            public double SpectralRollOffHz;      // 95% energia
            public double NoiseFloorDb;           // dBFS, stimato (p10)
            public double SnrDb;                  // SNR integrato
            public double EnobBits;               // (SNR-1.76)/6.02

            // Loudness ITU-R BS.1770 / EBU R128
            public double LufsM;                  // momentary 400 ms
            public double LufsS;                  // short-term 3 s
            public double LufsI;                  // integrated (gated)
            public double Lra;                    // loudness range (rolling)
            public double PlrDb;                  // True-peak - Integrated loudness
            public double PsrDb;                  // Sample peak - Short-term
        }

        public event Action<AudioMetrics>? OnMetrics;
        public event Action<float, float, float, float, double[]>? OnLevels;

        public LoopbackSampler()
        {
            double s = 0;
            for (int i = 0; i < FFT_N; i++) s += _win[i];
            _denMain = 0.5 * s / FFT_N; // NAudio normalizes the forward FFT by N.
            _denEdge = s / FFT_N;
        }

        // ===== Start con device selezionabile =====
        public bool Start(string? renderDeviceId = null)
        {
            if (_running && _cap != null)
                return true;

            // Una cattura può essere stata fermata dal device durante il PiP pur
            // lasciando l'istanza allocata. Ripuliscila prima di ricrearla.
            if (_cap != null)
                Stop();

            try
            {
                var mm = new MMDeviceEnumerator();

                // Scegli il device render
                MMDevice device = string.IsNullOrEmpty(renderDeviceId)
                    ? mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : mm.GetDevice(renderDeviceId);

                Dbg.Log("[LoopbackSampler] Start: uso device render '"
                        + device.FriendlyName + "' (" + device.ID + ")", Dbg.LogLevel.Info);

                // Cattura loopback su quel device (solo se il device lo permette)
                _cap = new WasapiLoopbackCapture(device);

                // Inizializza PRIMA di StartRecording per evitare race su _sr
                InitForSampleRate(_cap.WaveFormat.SampleRate);
                ResetState();

                _cap.DataAvailable += OnData;
                _cap.RecordingStopped += OnRecordingStopped;

                _cap.StartRecording();

                _running = true;
                Dbg.Log("[LoopbackSampler] Start: loopback avviato correttamente.", Dbg.LogLevel.Info);
                return true;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x8889000A)
            {
                // AUDCLNT_E_DEVICE_IN_USE
                Dbg.Warn(
                    "[LoopbackSampler] Start fallito: device in uso (probabile output in WASAPI exclusive, " +
                    "loopback non disponibile su questo endpoint). HResult=0x" + ((uint)ex.HResult).ToString("X8"));
                Stop();
                return false;
            }
            catch (Exception ex)
            {
                Dbg.Warn("[LoopbackSampler] Start EX: " + ex);
                Stop();
                return false;
            }
        }

        // Retro-compat
        public bool Start() => Start(null);

        public void Stop()
        {
            _running = false;

            try
            {
                if (_cap != null)
                {
                    _cap.DataAvailable -= OnData;
                    _cap.RecordingStopped -= OnRecordingStopped;
                }
            }
            catch { }

            try { _cap?.StopRecording(); } catch { }
            try { _cap?.Dispose(); } catch { }
            _cap = null;

            ResetState();
            Dbg.Log("[LoopbackSampler] Stop: loopback fermato e stato resettato.", Dbg.LogLevel.Info);
        }

        public void Dispose() => Stop();

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (ReferenceEquals(sender, _cap))
                _running = false;

            if (e.Exception != null)
                Dbg.Warn("[LoopbackSampler] RecordingStopped EX: " + e.Exception);
        }

        // ===== Init helpers =====
        private void InitForSampleRate(int sr)
        {
            _sr = sr;

            // DC offset EMA (tau ~0.5 s)
            double tau = 0.5;
            _dcAlpha = 1.0 - Math.Exp(-1.0 / Math.Max(1.0, tau * _sr));

            // BS.1770's shelf uses Q, whereas NAudio.HighShelf takes a shelf slope.
            // Generate the two stages at the actual sample rate, rather than
            // substituting an EQ filter with a different frequency response.
            double k = Math.Tan(Math.PI * 1681.974450955533 / _sr);
            double q = 0.7071752369554196;
            double vh = Math.Pow(10, 3.999843853973347 / 20);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double denominator = 1 + k / q + k * k;
            WeightingBiquad Shelf() => new((vh + vb * k / q + k * k) / denominator,
                2 * (k * k - vh) / denominator, (vh - vb * k / q + k * k) / denominator,
                2 * (k * k - 1) / denominator, (1 - k / q + k * k) / denominator);
            _kShelfL = Shelf(); _kShelfR = Shelf();
            k = Math.Tan(Math.PI * 38.13547087602444 / _sr);
            q = 0.5003270373238773;
            denominator = 1 + k / q + k * k;
            WeightingBiquad HighPass() => new(1, -2, 1,
                2 * (k * k - 1) / denominator, (1 - k / q + k * k) / denominator);
            _kHpL = HighPass(); _kHpR = HighPass();
        }

        private double _cumSqL, _cumSqR, _cumFrames;

        private void ResetState()
        {
            _cumSqL = _cumSqR = _cumFrames = 0;
            _tpHoldL = _tpHoldR = 0f;
            _prevL1 = _prevL2 = _prevL3 = 0f;
            _prevR1 = _prevR2 = _prevR3 = 0f;
            _clipEvents = 0;
            _clipSampleEvents = 0;
            _emaDcL = _emaDcR = 0.0;
            _sumM2 = _sumS2 = 0.0;

            _winM.Reset();
            _winS.Reset();
            _blockAccumEnergy = 0.0;
            _blockAccumDur = 0.0;
            _intBlocksMs.Clear();
            _stHistory.Clear();
            _subBlocks.Clear(); _stValues.Clear(); _stPeaks.Clear();
            _subEnergy = _subDur = 0; _programmeTp = _lastBlockTp = 0; _audioClock = 0;
            _tpFilterL.Reset(); _tpFilterR.Reset();

            _fftPos = _fftCount = _fftSinceTransform = 0;
            _lastSpectrum = Array.Empty<double>();
            Array.Clear(_fftMid, 0, _fftMid.Length);
            Array.Clear(_fftRight, 0, _fftRight.Length);

            _peakHoldL = _peakHoldR = 0f;
            _lastPeakUpdate = DateTime.MinValue;
            _scopeW = 0;
            _waveW=0; _wavePhase=0; _waveMinL=_waveMaxL=_waveMinR=_waveMaxR=0;
            Array.Clear(_waveL); Array.Clear(_waveR);
            Array.Clear(_scopeL, 0, _scopeL.Length);
            Array.Clear(_scopeR, 0, _scopeR.Length);
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (!_running || e.BytesRecorded <= 0 || _cap == null) return;
            var wf = _cap.WaveFormat;
            int ch = Math.Max(1, wf.Channels);
            int bps = Math.Max(1, wf.BitsPerSample / 8);
            bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;
            int frames = e.BytesRecorded / (bps * ch);
            if (frames <= 0) return;

            double sumSqL = 0, sumSqR = 0;
            double sumLL = 0, sumRR = 0, sumLR = 0;
            double sumL = 0, sumR = 0;
            float samplePeakL = 0f, samplePeakR = 0f;

            // Per loudness: energia K-weighted media*durata (densità * dt)
            double kwEnergy = 0.0;
            double dtBlock = frames / (double)_sr;

            int step = Math.Max(1, frames / Math.Min(SCOPE_N, frames));

            for (int i = 0; i < frames; i++)
            {
                int idx = i * bps * ch;
                float l = ReadSample(e.Buffer, idx + 0 * bps, bps, isFloat);
                float r = ch > 1 ? ReadSample(e.Buffer, idx + 1 * bps, bps, isFloat) : l;
                l = Clamp1(l);
                r = Clamp1(r);

                // sample-peak clipping events
                if (Math.Abs(l) >= 1.0f) _clipSampleEvents++;
                if (Math.Abs(r) >= 1.0f) _clipSampleEvents++;

                sumSqL += l * l; sumSqR += r * r;
                float al = Math.Abs(l), ar = Math.Abs(r);
                if (al > samplePeakL) samplePeakL = al;
                if (ar > samplePeakR) samplePeakR = ar;

                sumLL += l * l; sumRR += r * r; sumLR += l * r;
                sumL += l; sumR += r;

                AppendFft(l, r);

                AppendWaveform(l,r);
                if (i % step == 0)
                {
                    _scopeL[_scopeW] = l;
                    _scopeR[_scopeW] = r;
                    _scopeW = (_scopeW + 1) % SCOPE_N;
                }

                // === DC offset (EMA) ===
                _emaDcL += _dcAlpha * (l - _emaDcL);
                _emaDcR += _dcAlpha * (r - _emaDcR);

                // === Mid/Side energy per Width ===
                double m = 0.5 * (l + r);
                double s = 0.5 * (l - r);
                _sumM2 += m * m;
                _sumS2 += s * s;

                // True peak BS.1770: sovracampionamento 4x con FIR polifase (l'interpolazione
                // cubica di prima sbagliava fino a ~1 dB sui picchi fra i campioni).
                float tpL = _tpFilterL.Push(l), tpR = _tpFilterR.Push(r);
                if (tpL > _tpHoldL) _tpHoldL = tpL;
                if (tpR > _tpHoldR) _tpHoldR = tpR;

                // shift stati
                _prevL3 = _prevL2; _prevL2 = _prevL1; _prevL1 = l;
                _prevR3 = _prevR2; _prevR2 = _prevR1; _prevR1 = r;

                // === K-weighting per loudness (approssimazione) ===
                if (_kHpL != null && _kShelfL != null && _kHpR != null && _kShelfR != null)
                {
                    float lkw = _kShelfL.Transform(_kHpL.Transform(l));
                    float rkw = _kShelfR.Transform(_kHpR.Transform(r));
                    double ms = lkw * lkw + rkw * rkw;
                    kwEnergy += ms;
                }
            }

            _cumSqL += sumSqL; _cumSqR += sumSqR; _cumFrames += frames;
            float rmsL = (float)Math.Sqrt(sumSqL / Math.Max(1, frames));
            float rmsR = (float)Math.Sqrt(sumSqR / Math.Max(1, frames));
            bool isSilent = 20.0 * Math.Log10(Math.Max(rmsL, rmsR) + EPS) < SILENCE_DBFS;

            // Peak-hold time-based decay
            var now = DateTime.UtcNow;
            if (_lastPeakUpdate == DateTime.MinValue) _lastPeakUpdate = now;
            double dt = (now - _lastPeakUpdate).TotalSeconds;
            _lastPeakUpdate = now;
            float decay = (float)Math.Pow(10, -PEAKHOLD_DECAY_DBPS * dt / 20.0);
            _peakHoldL = Math.Max(samplePeakL, _peakHoldL * decay);
            _peakHoldR = Math.Max(samplePeakR, _peakHoldR * decay);

            // Silence gating time-based (dimezza ogni 1 s)
            if (isSilent)
            {
                float gate = (float)Math.Pow(0.5, Math.Max(0.0, dt));
                _peakHoldL *= gate;
                _peakHoldR *= gate;
            }

            // Correlazione Pearson DC-free
            double corr;
            {
                double n = Math.Max(1, frames);
                double meanL = sumL / n, meanR = sumR / n;
                double sLL = sumLL - n * meanL * meanL;
                double sRR = sumRR - n * meanR * meanR;
                double sLR = sumLR - n * meanL * meanR;
                double denom = Math.Sqrt(Math.Max(1e-18, sLL * sRR));
                corr = denom > 0 ? Clamp(sLR / denom, -1.0, +1.0) : 0.0;
            }

            // Balance [-1..+1]
            double balance = 0.0, sum = rmsL + rmsR;
            if (sum > 1e-9) balance = Clamp((rmsR - rmsL) / sum, -1.0, +1.0);

            // Crest (dB)
            double crestL = ToDb(samplePeakL / Math.Max(1e-9f, rmsL));
            double crestR = ToDb(samplePeakR / Math.Max(1e-9f, rmsR));
            crestL = Math.Max(0.0, crestL);
            crestR = Math.Max(0.0, crestR);

            // True-Peak dBTP & clip events
            _lastBlockTp = Math.Max(_tpHoldL, _tpHoldR); if (_lastBlockTp > _programmeTp) _programmeTp = _lastBlockTp;
            double dbtpL = 20.0 * Math.Log10(Math.Max(_tpHoldL, EPS));
            double dbtpR = 20.0 * Math.Log10(Math.Max(_tpHoldR, EPS));
            if (_tpHoldL >= 1.0f) _clipEvents++;
            if (_tpHoldR >= 1.0f) _clipEvents++;
            _tpHoldL = _tpHoldR = 0f;

            // DC offset corrente (EMA)
            double dcL = _emaDcL;
            double dcR = _emaDcR;

            // Width & compatibilità mono
            double widthDb = 10.0 * Math.Log10(Math.Max(_sumS2, 1e-18) / Math.Max(_sumM2, 1e-18));
            double monoCompat = corr;
            _sumM2 = _sumS2 = 0.0;

            // Loudness windows (usa durata AUDIO dtBlock, non wall-clock)
            if (dtBlock > 0 && kwEnergy > 0)
            {
                double kwMs = kwEnergy / frames;          // mean-square per campione
                double blockEnergy = kwMs * dtBlock;      // energia del blocco corrente

                _winM.Add(kwMs, dtBlock);
                _winS.Add(kwMs, dtBlock);

                AccumulateLoudness(blockEnergy, dtBlock, kwMs);
            }

            // Calcolo LUFS M/S/I + LRA
            double lufsM = double.NegativeInfinity;
            double lufsS = double.NegativeInfinity;
            double lufsI = double.NegativeInfinity;
            double lra = 0.0;

            if (_winM.Duration >= 0.100)
                lufsM = 10.0 * Math.Log10(_winM.MeanSquare + EPS) + K_LUFS_OFFSET;

            if (_winS.Duration >= 0.100)
            {
                double msS = _winS.MeanSquare;
                lufsS = 10.0 * Math.Log10(msS + EPS) + K_LUFS_OFFSET;

                
            }

            

            if (_intBlocksMs.Count > 0)
                lufsI = ComputeIntegratedLufs(_intBlocksMs);

            // LRA conforme: P95 - P10 sui valori S sopra max(-70, I-20)
            lra = ComputeLra(_stValues);

            // Spettro e feature
            double[] spectrumDb = _lastSpectrum;
            double dominantHz = 0.0;
            double centroidHz = 0.0;
            double rollOffHz = 0.0;
            double noiseFloorDb = double.NaN;
            double snrDb = double.NaN;
            double enob = double.NaN;

            if (_fftCount == FFT_N && _fftSinceTransform >= Math.Max(1, _sr / 30))
            {
                var buf = new Complex[FFT_N];
                var rightBuf = new Complex[FFT_N];
                for (int i = 0; i < FFT_N; i++) rightBuf[i].X = _fftRight[(_fftPos + i) % FFT_N] * _win[i];
                FastFourierTransform.FFT(true, FFT_M, rightBuf);
                for (int i = 0; i < FFT_N; i++) { buf[i].X = _fftMid[(_fftPos + i) % FFT_N] * _win[i]; buf[i].Y = 0f; }

                FastFourierTransform.FFT(true, FFT_M, buf);

                int nyquist = FFT_N / 2;
                int bins = nyquist + 1;           // includi Nyquist
                spectrumDb = new double[bins];

                const double SQRT2 = 1.4142135623730951;
                double maxDb = -1e9;
                int maxK = 1;
                double sumPow = 0.0, sumFreqPow = 0.0;
                double totalEnergy = 0.0;

                double[] mags = new double[bins];

                for (int k = 0; k <= nyquist; k++)
                {
                    double re = buf[k].X;
                    double im = buf[k].Y;
                    double mag = Math.Sqrt((re * re + im * im + rightBuf[k].X * (double)rightBuf[k].X + rightBuf[k].Y * (double)rightBuf[k].Y) * .5);

                    double den = k == 0 || k == nyquist ? _denEdge : _denMain;

                    // ampiezza corretta + conversione a RMS
                    double amp = mag / Math.Max(den, EPS);
                    double amp_rms = k == 0 || k == nyquist ? amp : amp / SQRT2;

                    double dbfs = 20.0 * Math.Log10(Math.Max(amp_rms, 1e-12));
                    if (dbfs < -120.0) dbfs = -120.0;
                    if (dbfs > 0.0) dbfs = 0.0;

                    spectrumDb[k] = dbfs;
                    mags[k] = amp_rms;

                    if (k >= 1 && dbfs > maxDb) { maxDb = dbfs; maxK = k; }

                    double freq = (double)k * _sr / FFT_N;
                    double energy = amp_rms * amp_rms;
                    sumPow += energy;
                    sumFreqPow += freq * energy;

                    totalEnergy += energy;
                }

                dominantHz = maxK * _sr / (double)FFT_N;

                // Interpolazione parabolica in lineare (mags), non in dB
                if (maxK >= 1 && maxK < nyquist)
                {
                    double PL = mags[maxK - 1];
                    double PC = mags[maxK];
                    double PR = mags[maxK + 1];
                    double dden = PL - 2 * PC + PR;
                    double delta = Math.Abs(dden) < 1e-18 ? 0.0 : 0.5 * (PL - PR) / dden;
                    delta = Math.Max(-0.5, Math.Min(0.5, delta));
                    double kInterp = maxK + delta;
                    dominantHz = kInterp * _sr / FFT_N;
                }

                centroidHz = sumPow > 0 ? sumFreqPow / sumPow : 0.0;

                // roll-off 95%
                double target = totalEnergy * 0.95;
                double cum = 0.0;
                for (int k = 0; k <= nyquist; k++)
                {
                    double ebin = mags[k] * mags[k];
                    cum += ebin;
                    if (cum >= target)
                    {
                        rollOffHz = (double)k * _sr / FFT_N;
                        break;
                    }
                }

                // noise floor = p10 (escludi DC/Nyq e ±1 bin attorno alla dominante)
                if (bins > 5)
                {
                    var tmp = new List<double>(bins);
                    for (int k = 1; k < nyquist; k++)
                    {
                        if (k >= maxK - 1 && k <= maxK + 1) continue;
                        tmp.Add(spectrumDb[k]);
                    }
                    tmp.Sort();
                    int idxP = (int)Math.Round(0.10 * (tmp.Count - 1));
                    idxP = Math.Max(0, Math.Min(idxP, Math.Max(0, tmp.Count - 1)));
                    noiseFloorDb = tmp.Count > 0 ? tmp[idxP] : double.NaN;

                    // SNR integrato Psig (±1 bin) / Pnoise (resto)
                    double[] p = new double[bins];
                    for (int k = 0; k <= nyquist; k++) p[k] = mags[k] * mags[k];

                    int k0 = Math.Max(0, maxK - 1);
                    int k1 = Math.Min(nyquist, maxK + 1);
                    double Psig = 0.0;
                    for (int k = k0; k <= k1; k++) Psig += p[k];

                    double Pnoise = 0.0;
                    for (int k = 0; k <= nyquist; k++)
                        if (k < k0 || k > k1) Pnoise += p[k];

                    snrDb = Pnoise > 0.0 && Psig > 0.0 ? 10.0 * Math.Log10(Psig / Pnoise) : double.NaN;
                    enob = double.IsNaN(snrDb) ? double.NaN : (snrDb - 1.76) / 6.02;
                }

                _fftSinceTransform = 0;
                _lastSpectrum = spectrumDb;
            }

            // Copia scope
            var scopeL = new float[SCOPE_N];
            var scopeR = new float[SCOPE_N];
            int w = _scopeW;
            for (int i = 0; i < SCOPE_N; i++)
            {
                int j = (w + i) % SCOPE_N;
                scopeL[i] = _scopeL[j];
                scopeR[i] = _scopeR[j];
            }

            // PLR/PSR
            double psr = double.NaN, plr = double.NaN;
            if (!double.IsNegativeInfinity(lufsS))
                psr = ToDb(ShortTermTruePeak(Math.Max(_lastBlockTp, 0f)) + EPS) - lufsS;
            if (!double.IsNegativeInfinity(lufsI))
                plr = ToDb(_programmeTp + EPS) - lufsI;

            var metrics = new AudioMetrics
            {
                RmsL = rmsL,
                RmsR = rmsR,
                EnergyL = _cumSqL,
                EnergyR = _cumSqR,
                EnergyFrames = _cumFrames,
                PeakL = samplePeakL,
                PeakR = samplePeakR,
                PeakHoldL = _peakHoldL,
                PeakHoldR = _peakHoldR,
                SpectrumDb = spectrumDb,           // N/2+1
                Correlation = corr,
                Balance = balance,
                CrestL_dB = crestL,
                CrestR_dB = crestR,
                ScopeL = scopeL,
                ScopeR = scopeR,
                WaveformL = WaveformSnapshot(_waveL),
                WaveformR = WaveformSnapshot(_waveR),
                SampleRate = _sr,
                IsSilent = isSilent,
                FftLength = FFT_N,

                DbTpL = dbtpL,
                DbTpR = dbtpR,
                ClipEvents = _clipEvents,
                ClipSampleEvents = _clipSampleEvents,
                DcL = dcL,
                DcR = dcR,
                WidthDb = widthDb,
                MonoCompat = monoCompat,
                DominantHz = dominantHz,
                SpectralCentroidHz = centroidHz,
                SpectralRollOffHz = rollOffHz,
                NoiseFloorDb = noiseFloorDb,
                SnrDb = snrDb,
                EnobBits = enob,
                LufsM = lufsM,
                LufsS = lufsS,
                LufsI = lufsI,
                Lra = lra,
                PlrDb = plr,
                PsrDb = psr
            };

            OnMetrics?.Invoke(metrics);
            OnLevels?.Invoke(rmsL, rmsR, _peakHoldL, _peakHoldR, spectrumDb);
        }

        // ===== OPTIONAL: feed diretto PCM (senza loopback) =====
        // Puoi chiamare PushPcm() dai tuoi decoder: interleaved float, samples = n° di float nel buffer.
        public void PushPcm(float[] interleaved, int samples, int sampleRate, int channels)
        {
            if (interleaved == null || samples <= 0) return;

            if (!_running || _sr != sampleRate)
            {
                InitForSampleRate(sampleRate);
                ResetState();
                _running = true;
            }

            int frames = samples / Math.Max(1, channels);

            double sumSqL = 0, sumSqR = 0;
            double sumLL = 0, sumRR = 0, sumLR = 0;
            double sumL = 0, sumR = 0;
            float samplePeakL = 0f, samplePeakR = 0f;

            double kwEnergy = 0.0;
            double dtBlock = frames / (double)_sr;
            int step = Math.Max(1, frames / Math.Min(SCOPE_N, frames));

            for (int i = 0; i < frames; i++)
            {
                float l = Clamp1(interleaved[i * channels + 0]);
                float r = channels > 1 ? Clamp1(interleaved[i * channels + 1]) : l;

                if (Math.Abs(l) >= 1.0f) _clipSampleEvents++;
                if (Math.Abs(r) >= 1.0f) _clipSampleEvents++;


                sumSqL += l * l; sumSqR += r * r;
                float al = Math.Abs(l), ar = Math.Abs(r);
                if (al > samplePeakL) samplePeakL = al;
                if (ar > samplePeakR) samplePeakR = ar;

                sumLL += l * l; sumRR += r * r; sumLR += l * r;
                sumL += l; sumR += r;

                AppendFft(l, r);

                AppendWaveform(l,r);
                if (i % step == 0)
                {
                    _scopeL[_scopeW] = l;
                    _scopeR[_scopeW] = r;
                    _scopeW = (_scopeW + 1) % SCOPE_N;
                }

                _emaDcL += _dcAlpha * (l - _emaDcL);
                _emaDcR += _dcAlpha * (r - _emaDcR);

                double m = 0.5 * (l + r);
                double s = 0.5 * (l - r);
                _sumM2 += m * m;
                _sumS2 += s * s;

                // True peak BS.1770: sovracampionamento 4x con FIR polifase (l'interpolazione
                // cubica di prima sbagliava fino a ~1 dB sui picchi fra i campioni).
                float tpL = _tpFilterL.Push(l), tpR = _tpFilterR.Push(r);
                if (tpL > _tpHoldL) _tpHoldL = tpL;
                if (tpR > _tpHoldR) _tpHoldR = tpR;

                _prevL3 = _prevL2; _prevL2 = _prevL1; _prevL1 = l;
                _prevR3 = _prevR2; _prevR2 = _prevR1; _prevR1 = r;

                if (_kHpL != null && _kShelfL != null && _kHpR != null && _kShelfR != null)
                {
                    float lkw = _kShelfL.Transform(_kHpL.Transform(l));
                    float rkw = _kShelfR.Transform(_kHpR.Transform(r));
                    double ms = lkw * lkw + rkw * rkw;
                    kwEnergy += ms;
                }
            }

            // --- copia pari a OnData dal punto RMS in poi ---
            _cumSqL += sumSqL; _cumSqR += sumSqR; _cumFrames += frames;
            float rmsL = (float)Math.Sqrt(sumSqL / Math.Max(1, frames));
            float rmsR = (float)Math.Sqrt(sumSqR / Math.Max(1, frames));
            bool isSilent = 20.0 * Math.Log10(Math.Max(rmsL, rmsR) + EPS) < SILENCE_DBFS;

            var now = DateTime.UtcNow;
            if (_lastPeakUpdate == DateTime.MinValue) _lastPeakUpdate = now;
            double dt = (now - _lastPeakUpdate).TotalSeconds;
            _lastPeakUpdate = now;
            float decay = (float)Math.Pow(10, -PEAKHOLD_DECAY_DBPS * dt / 20.0);
            _peakHoldL = Math.Max(samplePeakL, _peakHoldL * decay);
            _peakHoldR = Math.Max(samplePeakR, _peakHoldR * decay);
            if (isSilent)
            {
                float gate = (float)Math.Pow(0.5, Math.Max(0.0, dt));
                _peakHoldL *= gate;
                _peakHoldR *= gate;
            }

            double corr;
            {
                double n = Math.Max(1, frames);
                double meanL = sumL / n, meanR = sumR / n;
                double sLL = sumLL - n * meanL * meanL;
                double sRR = sumRR - n * meanR * meanR;
                double sLR = sumLR - n * meanL * meanR;
                double denom = Math.Sqrt(Math.Max(1e-18, sLL * sRR));
                corr = denom > 0 ? Clamp(sLR / denom, -1.0, +1.0) : 0.0;
            }

            double balance = 0.0, sum = rmsL + rmsR;
            if (sum > 1e-9) balance = Clamp((rmsR - rmsL) / sum, -1.0, +1.0);

            double crestL = ToDb(samplePeakL / Math.Max(1e-9f, rmsL));
            double crestR = ToDb(samplePeakR / Math.Max(1e-9f, rmsR));
            crestL = Math.Max(0.0, crestL);
            crestR = Math.Max(0.0, crestR);

            _lastBlockTp = Math.Max(_tpHoldL, _tpHoldR); if (_lastBlockTp > _programmeTp) _programmeTp = _lastBlockTp;
            double dbtpL = 20.0 * Math.Log10(Math.Max(_tpHoldL, EPS));
            double dbtpR = 20.0 * Math.Log10(Math.Max(_tpHoldR, EPS));
            if (_tpHoldL >= 1.0f) _clipEvents++;
            if (_tpHoldR >= 1.0f) _clipEvents++;
            _tpHoldL = _tpHoldR = 0f;

            double dcL = _emaDcL;
            double dcR = _emaDcR;

            double widthDb = 10.0 * Math.Log10(Math.Max(_sumS2, 1e-18) / Math.Max(_sumM2, 1e-18));
            double monoCompat = corr;
            _sumM2 = _sumS2 = 0.0;

            // QUI: usa dtBlock (durata AUDIO), non dt di wall-clock
            if (dtBlock > 0 && kwEnergy > 0)
            {
                double kwMs = kwEnergy / frames;
                double blockEnergy = kwMs * dtBlock;

                _winM.Add(kwMs, dtBlock);
                _winS.Add(kwMs, dtBlock);

                AccumulateLoudness(blockEnergy, dtBlock, kwMs);
            }

            double lufsM = double.NegativeInfinity;
            double lufsS = double.NegativeInfinity;
            double lufsI = double.NegativeInfinity;
            double lra = 0.0;

            if (_winM.Duration >= 0.100)
                lufsM = 10.0 * Math.Log10(_winM.MeanSquare + EPS) + K_LUFS_OFFSET;
            if (_winS.Duration >= 0.100)
            {
                double msS = _winS.MeanSquare;
                lufsS = 10.0 * Math.Log10(msS + EPS) + K_LUFS_OFFSET;
                
            }

            
            if (_intBlocksMs.Count > 0)
                lufsI = ComputeIntegratedLufs(_intBlocksMs);

            lra = ComputeLra(_stValues);

            double[] spectrumDb = _lastSpectrum;
            double dominantHz = 0.0;
            double centroidHz = 0.0;
            double rollOffHz = 0.0;
            double noiseFloorDb = double.NaN;
            double snrDb = double.NaN;
            double enob = double.NaN;

            if (_fftCount == FFT_N && _fftSinceTransform >= Math.Max(1, _sr / 30))
            {
                var buf = new Complex[FFT_N];
                var rightBuf = new Complex[FFT_N];
                for (int i = 0; i < FFT_N; i++) rightBuf[i].X = _fftRight[(_fftPos + i) % FFT_N] * _win[i];
                FastFourierTransform.FFT(true, FFT_M, rightBuf);
                for (int i = 0; i < FFT_N; i++) { buf[i].X = _fftMid[(_fftPos + i) % FFT_N] * _win[i]; buf[i].Y = 0f; }
                FastFourierTransform.FFT(true, FFT_M, buf);

                int nyquist = FFT_N / 2;
                int bins = nyquist + 1;
                spectrumDb = new double[bins];

                const double SQRT2 = 1.4142135623730951;
                double maxDb = -1e9;
                int maxK = 1;
                double sumPow = 0.0, sumFreqPow = 0.0;
                double totalEnergy = 0.0;

                double[] mags = new double[bins];
                for (int k = 0; k <= nyquist; k++)
                {
                    double re = buf[k].X, im = buf[k].Y;
                    double mag = Math.Sqrt((re * re + im * im + rightBuf[k].X * (double)rightBuf[k].X + rightBuf[k].Y * (double)rightBuf[k].Y) * .5);
                    double den = k == 0 || k == nyquist ? _denEdge : _denMain;
                    double amp = mag / Math.Max(den, EPS);
                    double amp_rms = k == 0 || k == nyquist ? amp : amp / SQRT2;
                    double dbfs = 20.0 * Math.Log10(Math.Max(amp_rms, 1e-12));
                    if (dbfs < -120.0) dbfs = -120.0; if (dbfs > 0.0) dbfs = 0.0;
                    spectrumDb[k] = dbfs;
                    mags[k] = amp_rms;

                    if (k >= 1 && dbfs > maxDb) { maxDb = dbfs; maxK = k; }

                    double freq = (double)k * _sr / FFT_N;
                    double energy = amp_rms * amp_rms;
                    sumPow += energy; sumFreqPow += freq * energy; totalEnergy += energy;
                }

                dominantHz = maxK * _sr / (double)FFT_N;
                if (maxK >= 1 && maxK < nyquist)
                {
                    double PL = mags[maxK - 1];
                    double PC = mags[maxK];
                    double PR = mags[maxK + 1];
                    double dden = PL - 2 * PC + PR;
                    double delta = Math.Abs(dden) < 1e-18 ? 0.0 : 0.5 * (PL - PR) / dden;
                    delta = Math.Max(-0.5, Math.Min(0.5, delta));
                    double kInterp = maxK + delta;
                    dominantHz = kInterp * _sr / FFT_N;
                }
                centroidHz = sumPow > 0 ? sumFreqPow / sumPow : 0.0;

                double target = totalEnergy * 0.95, cum = 0.0;
                for (int k = 0; k <= nyquist; k++)
                {
                    double ebin = mags[k] * mags[k];
                    cum += ebin;
                    if (cum >= target) { rollOffHz = (double)k * _sr / FFT_N; break; }
                }

                if (bins > 5)
                {
                    var tmp = new List<double>(bins);
                    for (int k = 1; k < nyquist; k++)
                    {
                        if (k >= maxK - 1 && k <= maxK + 1) continue;
                        tmp.Add(spectrumDb[k]);
                    }
                    tmp.Sort();
                    int idxP = (int)Math.Round(0.10 * (tmp.Count - 1));
                    idxP = Math.Max(0, Math.Min(idxP, Math.Max(0, tmp.Count - 1)));
                    noiseFloorDb = tmp.Count > 0 ? tmp[idxP] : double.NaN;

                    double[] p = new double[bins];
                    for (int k = 0; k <= nyquist; k++) p[k] = mags[k] * mags[k];
                    int k0 = Math.Max(0, maxK - 1), k1 = Math.Min(nyquist, maxK + 1);
                    double Psig = 0.0; for (int k = k0; k <= k1; k++) Psig += p[k];
                    double Pnoise = 0.0; for (int k = 0; k <= nyquist; k++) if (k < k0 || k > k1) Pnoise += p[k];
                    snrDb = Pnoise > 0 && Psig > 0 ? 10.0 * Math.Log10(Psig / Pnoise) : double.NaN;
                    enob = double.IsNaN(snrDb) ? double.NaN : (snrDb - 1.76) / 6.02;
                }

                _fftSinceTransform = 0;
                _lastSpectrum = spectrumDb;
            }

            var scopeL = new float[SCOPE_N];
            var scopeR = new float[SCOPE_N];
            int w = _scopeW;
            for (int i = 0; i < SCOPE_N; i++)
            {
                int j = (w + i) % SCOPE_N;
                scopeL[i] = _scopeL[j];
                scopeR[i] = _scopeR[j];
            }

            double psr = double.NaN, plr = double.NaN;
            if (!double.IsNegativeInfinity(lufsS))
                psr = ToDb(ShortTermTruePeak(Math.Max(_lastBlockTp, 0f)) + EPS) - lufsS;
            if (!double.IsNegativeInfinity(lufsI))
                plr = ToDb(_programmeTp + EPS) - lufsI;

            var metrics = new AudioMetrics
            {
                RmsL = rmsL,
                RmsR = rmsR,
                EnergyL = _cumSqL,
                EnergyR = _cumSqR,
                EnergyFrames = _cumFrames,
                PeakL = samplePeakL,
                PeakR = samplePeakR,
                PeakHoldL = _peakHoldL,
                PeakHoldR = _peakHoldR,
                SpectrumDb = spectrumDb,
                Correlation = corr,
                Balance = balance,
                CrestL_dB = crestL,
                CrestR_dB = crestR,
                ScopeL = scopeL,
                ScopeR = scopeR,
                WaveformL = WaveformSnapshot(_waveL),
                WaveformR = WaveformSnapshot(_waveR),
                SampleRate = _sr,
                IsSilent = isSilent,
                FftLength = FFT_N,
                DbTpL = dbtpL,
                DbTpR = dbtpR,
                ClipEvents = _clipEvents,
                ClipSampleEvents = _clipSampleEvents,
                DcL = _emaDcL,
                DcR = _emaDcR,
                WidthDb = widthDb,
                MonoCompat = monoCompat,
                DominantHz = dominantHz,
                SpectralCentroidHz = centroidHz,
                SpectralRollOffHz = rollOffHz,
                NoiseFloorDb = noiseFloorDb,
                SnrDb = snrDb,
                EnobBits = enob,
                LufsM = lufsM,
                LufsS = lufsS,
                LufsI = lufsI,
                Lra = lra,
                PlrDb = plr,
                PsrDb = psr
            };

            OnMetrics?.Invoke(metrics);
            OnLevels?.Invoke(rmsL, rmsR, _peakHoldL, _peakHoldR, spectrumDb);
        }

        // ===== Helpers =====
        private static float ReadSample(byte[] buf, int index, int bps, bool isFloat)
        {
            if (isFloat && bps == 4) return BitConverter.ToSingle(buf, index);
            return bps switch
            {
                2 => BitConverter.ToInt16(buf, index) / 32768f,
                3 => Read24(buf, index) / 8388608f,
                4 => BitConverter.ToInt32(buf, index) / 2147483648f,
                _ => 0f
            };
        }

        private static int Read24(byte[] b, int i)
        {
            int v = b[i] | b[i + 1] << 8 | b[i + 2] << 16;
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            return v;
        }

        private static float Clamp1(float x) => x < -1f ? -1f : x > 1f ? 1f : x;

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : v > hi ? hi : v;

        private static double ToDb(double x) => 20.0 * Math.Log10(Math.Max(x, EPS));

        private static float[] BuildHann(int N)
        {
            var w = new float[N];
            for (int n = 0; n < N; n++)
                w[n] = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * n / (N - 1)));
            return w;
        }

        private static float OversampledTpCubic4(float p0, float p1, float p2, float p3)
        {
            static float CR(float a, float b, float c, float d, float t)
            {
                float a0 = -0.5f * a + 1.5f * b - 1.5f * c + 0.5f * d;
                float a1 = a - 2.5f * b + 2.0f * c - 0.5f * d;
                float a2 = -0.5f * a + 0.5f * c;
                float a3 = b;
                return ((a0 * t + a1) * t + a2) * t + a3;
            }
            float v1 = Math.Abs(CR(p0, p1, p2, p3, 0.25f));
            float v2 = Math.Abs(CR(p0, p1, p2, p3, 0.50f));
            float v3 = Math.Abs(CR(p0, p1, p2, p3, 0.75f));
            float m = v1; if (v2 > m) m = v2; if (v3 > m) m = v3;
            return m;
        }

        private static float ParabolicTruePeak(float a, float b, float c)
        {
            float denom = a - 2f * b + c;
            if (Math.Abs(denom) < 1e-9f)
                return Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), Math.Abs(c));

            float xv = 0.5f * (a - c) / denom;
            if (xv < -0.5f) xv = -0.5f; else if (xv > 0.5f) xv = 0.5f;

            float peak = (float)Math.Abs(b - 0.25f * (a - c) * xv);
            return peak;
        }

        // ====== Loudness helpers ======
        private static void PruneHistory(List<StampedValue> hist, DateTime now, double seconds)
        {
            int cut = 0;
            for (int i = 0; i < hist.Count; i++)
            {
                if ((now - hist[i].T).TotalSeconds > seconds) cut = i + 1; else break;
            }
            if (cut > 0) hist.RemoveRange(0, Math.Min(cut, hist.Count));
        }

        private static double ComputeIntegratedLufs(List<double> msBlocks)
        {
            // Absolute gate -70 LUFS
            var gated = new List<double>(msBlocks.Count);
            foreach (var ms in msBlocks)
            {
                double l = 10.0 * Math.Log10(ms + EPS) + K_LUFS_OFFSET;
                if (l >= -70.0) gated.Add(ms);
            }
            if (gated.Count == 0) return double.NegativeInfinity;

            // Preliminary average
            double prelimMs = 0.0;
            foreach (var ms in gated) prelimMs += ms;
            prelimMs /= gated.Count;

            double prelimLufs = 10.0 * Math.Log10(prelimMs + EPS) + K_LUFS_OFFSET;

            // Relative gate: prelim - 10 LU
            double relGate = prelimLufs - 10.0;
            double relGateMs = Math.Pow(10.0, (relGate - K_LUFS_OFFSET) / 10.0);

            double sumMs = 0.0; int n = 0;
            foreach (var ms in gated)
            {
                if (ms >= relGateMs) { sumMs += ms; n++; }
            }
            if (n == 0) return double.NegativeInfinity;
            return 10.0 * Math.Log10(sumMs / n + EPS) + K_LUFS_OFFSET;
        }

        /// <summary>LRA secondo EBU Tech 3342: valori short-term (3 s) ogni 100 ms sull'intero brano,
        /// gate assoluto -70 LUFS e relativo -20 LU sotto la loro media energetica, poi P95 - P10.</summary>
        private static double ComputeLra(List<double> stLufs)
        {
            var abs = new List<double>(stLufs.Count);
            double sumE = 0;
            foreach (var v in stLufs) if (v >= -70.0) { abs.Add(v); sumE += Math.Pow(10, (v - K_LUFS_OFFSET) / 10); }
            if (abs.Count < 5) return 0.0;
            double rel = 10 * Math.Log10(sumE / abs.Count) + K_LUFS_OFFSET - 20.0;
            var vals = abs.Where(v => v >= rel).ToList();
            if (vals.Count < 5) return 0.0;
            vals.Sort();
            return Math.Max(0.0, Percentile(vals, 95.0) - Percentile(vals, 10.0));
        }

        // ---- loudness: blocchi di gating e LRA a tempo di AUDIO ----
        private const double SUBBLOCK_S = 0.100;
        private readonly List<double> _subBlocks = new();      // mean-square dei sotto-blocchi da 100 ms
        private readonly List<double> _stValues = new();       // short-term ogni 100 ms (LRA)
        private double _subEnergy, _subDur;
        private float _programmeTp, _lastBlockTp;              // true peak lineare: brano / ultimo blocco
        private readonly Queue<(double T, float Tp)> _stPeaks = new();
        private double _audioClock;

        /// <summary>BS.1770: blocchi da 400 ms con sovrapposizione del 75%, cioe' uno ogni 100 ms.</summary>
        private void AccumulateLoudness(double blockEnergy, double dtBlock, double kwMs)
        {
            _audioClock += dtBlock;
            _subEnergy += blockEnergy; _subDur += dtBlock;
            if (_subDur < SUBBLOCK_S - 1e-6) return;
            _subBlocks.Add(_subEnergy / _subDur);
            _subEnergy = 0; _subDur = 0;
            int n = _subBlocks.Count;
            if (n >= 4) _intBlocksMs.Add((_subBlocks[n - 1] + _subBlocks[n - 2] + _subBlocks[n - 3] + _subBlocks[n - 4]) / 4);
            if (_subBlocks.Count > 64) _subBlocks.RemoveRange(0, _subBlocks.Count - 4);
            // Short-term per l'LRA: solo quando la finestra da 3 s e' piena.
            if (_winS.Duration >= 2.95)
                _stValues.Add(10.0 * Math.Log10(_winS.MeanSquare + EPS) + K_LUFS_OFFSET);
        }

        /// <summary>Picco vero massimo degli ultimi 3 s (per il PSR).</summary>
        private float ShortTermTruePeak(float blockTp)
        {
            _stPeaks.Enqueue((_audioClock, blockTp));
            while (_stPeaks.Count > 0 && _audioClock - _stPeaks.Peek().T > 3.0) _stPeaks.Dequeue();
            float max = 0; foreach (var p in _stPeaks) if (p.Tp > max) max = p.Tp;
            return max;
        }

        /// <summary>
        /// Misuratore di true peak: interpolazione 4x con sinc a finestra di Kaiser (polifase,
        /// 16 prese per fase, guadagno unitario per ogni fase). Restituisce il massimo assoluto
        /// fra il campione e i tre punti interpolati.
        /// </summary>
        private sealed class TruePeakFilter
        {
            private const int Taps = 16, Phases = 4;
            private static readonly float[][] Coeffs = Build();
            private readonly float[] _hist = new float[Taps * 2];
            private int _pos;

            private static float[][] Build()
            {
                var c = new float[Phases][];
                double beta = 7.0, i0b = I0(beta);
                int half = Taps / 2;
                for (int p = 0; p < Phases; p++)
                {
                    c[p] = new float[Taps];
                    double frac = p / (double)Phases, sum = 0;
                    var tmp = new double[Taps];
                    for (int k = 0; k < Taps; k++)
                    {
                        double x = k - (half - 1) - frac;          // distanza dal punto da stimare
                        double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                        double r = x / (half + 0.5);
                        double w = Math.Abs(r) >= 1 ? 0 : I0(beta * Math.Sqrt(1 - r * r)) / i0b;
                        tmp[k] = sinc * w; sum += tmp[k];
                    }
                    for (int k = 0; k < Taps; k++) c[p][k] = (float)(tmp[k] / sum);
                }
                return c;
            }

            private static double I0(double x)
            {
                double s = 1, term = 1;
                for (int k = 1; k < 30; k++) { term *= (x / (2 * k)) * (x / (2 * k)); s += term; }
                return s;
            }

            public void Reset() { Array.Clear(_hist); _pos = 0; }

            public float Push(float x)
            {
                _hist[_pos] = x; _hist[_pos + Taps] = x;
                _pos = (_pos + 1) % Taps;
                // _hist[_pos .. _pos+Taps-1] = ultimi Taps campioni, dal piu' vecchio al piu' recente.
                float max = 0;
                for (int p = 0; p < Phases; p++)
                {
                    var c = Coeffs[p]; double acc = 0;
                    for (int k = 0; k < Taps; k++) acc += c[k] * _hist[_pos + k];
                    float a = (float)Math.Abs(acc);
                    if (a > max) max = a;
                }
                return max;
            }
        }

        private readonly TruePeakFilter _tpFilterL = new(), _tpFilterR = new();

        private static double Percentile(List<double> sortedVals, double p)
        {
            if (sortedVals.Count == 0) return double.NaN;
            double pos = p / 100.0 * (sortedVals.Count - 1);
            int lo = (int)Math.Floor(pos);
            int hi = (int)Math.Ceiling(pos);
            if (hi == lo) return sortedVals[lo];
            double frac = pos - lo;
            return sortedVals[lo] * (1.0 - frac) + sortedVals[hi] * frac;
        }

        // ===== support types =====
        private readonly struct StampedValue
        {
            public readonly DateTime T;
            public readonly double V;
            public StampedValue(DateTime t, double v) { T = t; V = v; }
        }

        /// <summary>
        /// Finestra scorrevole per energia (mean-square * durata).
        /// Mantiene Σ(ms * dt) e Σdt → MeanSquare = Σ(ms*dt)/Σdt.
        /// Supporta aggiunta con dt variabili e scarto automatico oltre windowSec.
        /// </summary>
        private sealed class EnergyWindow
        {
            private readonly double _winSec;
            private readonly Queue<(double ms, double dt)> _q = new();
            private double _sumE, _sumT;

            public EnergyWindow(double winSec) { _winSec = winSec; }

            public void Reset() { _q.Clear(); _sumE = 0.0; _sumT = 0.0; }

            public void Add(double ms, double dt)
            {
                if (dt <= 0) return;
                _q.Enqueue((ms, dt));
                _sumE += ms * dt;
                _sumT += dt;
                while (_sumT > _winSec + 1e-6 && _q.Count > 0)
                {
                    var (ms0, dt0) = _q.Peek();
                    double excess = _sumT - _winSec;
                    if (dt0 <= excess + 1e-9)
                    {
                        _q.Dequeue();
                        _sumE -= ms0 * dt0;
                        _sumT -= dt0;
                    }
                    else
                    {
                        double keep = dt0 - excess;
                        _q.Dequeue();
                        _q.Enqueue((ms0, keep));
                        _sumE -= ms0 * excess;
                        _sumT -= excess;
                        break;
                    }
                }
            }

            public double MeanSquare => _sumT > 0 ? _sumE / _sumT : 0.0;
            public double Duration => _sumT;
        }
    }
}
