#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CinecorePlayer2025.Audio
{
    /// <summary>Impostazioni persistenti del Cinecore Audio Engine.</summary>
    internal sealed class CinecoreAudioSettings
    {
        public bool Enabled { get; set; } = true;
        public bool Exclusive { get; set; }
        public bool EqEnabled { get; set; }
        public double PreampDb { get; set; }
        public string Protection { get; set; } = nameof(ClipProtection.Limiter);
        public double CeilingDbTp { get; set; } = -1.0;
        public string Preset { get; set; } = "flat";
        public List<BandDto> Bands { get; set; } = AudioDspSettings.GraphicBands(new double[10]).Select(BandDto.From).ToList();

        // Modalita' EQ: "graphic10", "graphic31", "parametric".
        public string EqMode { get; set; } = "graphic10";
        public List<double> Graphic31 { get; set; } = new double[31].ToList();
        public List<BandDto> Parametric { get; set; } = DefaultParametric();
        public int SelectedBand { get; set; }

        // DSP
        public double Balance { get; set; }            // -100 .. +100
        public double Width { get; set; } = 100;       // 0 .. 200 (%)
        public bool Mono { get; set; }
        public string Crossfeed { get; set; } = nameof(CrossfeedLevel.Off);
        public bool Loudness { get; set; }
        public string ReplayGain { get; set; } = "off"; // off | track | album
        public double ReplayGainPreampDb { get; set; }  // per i brani senza tag: 0 = nessuna modifica
        // Taratura dei VU meter: "auto" (segue la loudness del brano) oppure dBFS per 0 VU ("-18", "-14", "-10").
        public string VuCalibration { get; set; } = "auto";
        // Dissolvenza incrociata fra un brano e il successivo, in secondi (0 = spenta).
        public int CrossfadeSeconds { get; set; }
        // Bit perfect: uscita esclusiva alla frequenza del file e nessuna elaborazione
        // (EQ, ReplayGain, limiter, DSP e dissolvenza restano fuori dal percorso del segnale).
        public bool BitPerfect { get; set; }

        public static List<BandDto> DefaultParametric() => new()
        {
            new() { Type = nameof(EqBandType.LowShelf), Frequency = 80, Gain = 0, Q = 0.71 },
            new() { Type = nameof(EqBandType.Peak), Frequency = 250, Gain = 0, Q = 1.0 },
            new() { Type = nameof(EqBandType.Peak), Frequency = 1000, Gain = 0, Q = 1.0 },
            new() { Type = nameof(EqBandType.Peak), Frequency = 4000, Gain = 0, Q = 1.0 },
            new() { Type = nameof(EqBandType.HighShelf), Frequency = 10000, Gain = 0, Q = 0.71 },
        };

        public EqMode Mode => EqMode switch { "graphic31" => Audio.EqMode.Graphic31, "parametric" => Audio.EqMode.Parametric, _ => Audio.EqMode.Graphic10 };

        internal sealed class BandDto
        {
            public string Type { get; set; } = nameof(EqBandType.Peak);
            public double Frequency { get; set; }
            public double Gain { get; set; }
            public double Q { get; set; } = 1.41;
            public bool Enabled { get; set; } = true;
            public static BandDto From(EqBand b) => new() { Type = b.Type.ToString(), Frequency = b.FrequencyHz, Gain = b.GainDb, Q = b.Q, Enabled = b.Enabled };
            public EqBand ToBand() => new(Enum.TryParse<EqBandType>(Type, out var t) ? t : EqBandType.Peak, Frequency, Gain, Q, Enabled);
        }

        public AudioDspSettings ToDsp()
        {
            var mode = Mode;
            IReadOnlyList<EqBand> bands = mode switch
            {
                Audio.EqMode.Graphic31 => AudioDspSettings.ThirdOctaveBands(Graphic31),
                Audio.EqMode.Parametric => Parametric.Select(b => b.ToBand()).ToList(),
                _ => Bands.Select(b => b.ToBand()).ToList()
            };
            return new AudioDspSettings(EqEnabled, bands, PreampDb,
                Enum.TryParse<ClipProtection>(Protection, out var p) ? p : ClipProtection.Limiter, Math.Clamp(CeilingDbTp, -6, 0),
                mode, Math.Clamp(Balance, -100, 100) / 100.0, Math.Clamp(Width, 0, 200) / 100.0, Mono,
                Enum.TryParse<CrossfeedLevel>(Crossfeed, out var cf) ? cf : CrossfeedLevel.Off, Loudness);
        }

        private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "audioEngine.json");
        private static CinecoreAudioSettings? _current;
        public static event Action? Changed;

        public static CinecoreAudioSettings Current
        {
            get
            {
                if (_current != null) return _current;
                try { if (File.Exists(FilePath)) _current = JsonSerializer.Deserialize<CinecoreAudioSettings>(File.ReadAllText(FilePath)); } catch { }
                return _current ??= new CinecoreAudioSettings();
            }
        }

        /// <summary>Notifica il motore senza scrivere il file (trascinamenti in corso).</summary>
        public static void Touch() => Changed?.Invoke();

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Cinecore Audio Engine: riproduzione musicale con catena DSP propria.
    /// FFmpeg decodifica (e ricampiona con soxr se il dispositivo lo richiede), il DSP gira
    /// nel callback WASAPI in doppia precisione, cosi' EQ e protezione rispondono subito.
    /// Uscita condivisa (formato del mixer) o esclusiva alla frequenza del file.
    /// </summary>
    internal sealed class CinecoreAudioEngine : IPlaybackEngine
    {
        private readonly string? _deviceName;
        private CinecoreAudioDecoder? _decoder;
        private WasapiOut? _output;
        private MMDevice? _device;
        private SampleSource? _source;
        private AudioDspChain? _dsp;
        private Thread? _worker;
        private volatile bool _disposed, _ended, _decodeEnded;
        private readonly object _gate = new();
        private readonly AutoResetEvent _wake = new(false);
        private double _pendingSeek = -1;
        private System.Threading.Timer? _progressTimer;
        private float _volume = 1f;
        private double _duration;
        private Action? _update;

        public static event Action<float[], int, int, int>? PcmTap; // (campioni, fotogrammi, canali, frequenza)
        public static CinecoreAudioEngine? Active { get; private set; }

        public AudioEngineMeters? Meters => _dsp?.Meters;
        public string OutputDescription { get; private set; } = "";
        public string SourceDescription => _decoder?.SourceFormat ?? "";
        public bool ExclusiveActive { get; private set; }

        public CinecoreAudioEngine(string? deviceName) => _deviceName = deviceName;

        public event Action<double>? OnProgressSeconds;
        public event Action<string>? OnStatus;
        public event Action<bool>? OnBitstreamChanged { add { } remove { } }

        // ===== apertura =====
        public void Open(string mediaPath, bool hasVideo)
        {
            _device = ResolveDevice(_deviceName);
            var settings = CinecoreAudioSettings.Current;

            // Formato di uscita: esclusivo alla frequenza del file se richiesto e supportato,
            // altrimenti quello del mixer di Windows (condiviso).
            using (var probe = new CinecoreAudioDecoder(mediaPath, 48000, 2))
            {
                _duration = probe.DurationSeconds;
                if (settings.BitPerfect)
                {
                    // Campioni interi a 2 canali fino a 24 bit: il percorso in virgola mobile li porta
                    // esatti fino all'uscita, se il dispositivo accetta la frequenza del file e almeno quei bit.
                    bool exactSource = probe.SourceIsInteger && probe.SourceChannels == 2 && probe.SourceBits <= 24;
                    if (exactSource && TryBitPerfectFormat(_device, probe.SourceRate, probe.SourceBits, out var exact))
                    {
                        _format = exact; ExclusiveActive = true; _bypass = true; _exact = true;
                    }
                    else if (TryExclusiveFormat(_device, probe.SourceRate, out var direct) && direct.SampleRate == probe.SourceRate)
                    {
                        // Sorgente non intera (MP3, AAC...) o non stereo: resta il percorso diretto, senza elaborazione.
                        _format = direct; ExclusiveActive = true; _bypass = true;
                    }
                }
                if (_format == null && settings.Exclusive && TryExclusiveFormat(_device, probe.SourceRate, out var exclusive))
                {
                    _format = exclusive; ExclusiveActive = true;
                }
            }
            if (_format == null)
            {
                var mix = _device.AudioClient.MixFormat;
                _format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
                ExclusiveActive = false;
            }

            _outputValidBits = ValidBits(_format);
            _decoder = new CinecoreAudioDecoder(mediaPath, _format.SampleRate, _format.Channels);
            _dsp = new AudioDspChain(_format.Channels, _format.SampleRate) { Volume = _bypass ? 1f : _volume };
            if (!_bypass && _boostDb > 0) _dsp.BoostDb = _boostDb;
            // Percorso diretto: la catena DSP resta neutra e serve solo ai misuratori, su una copia del segnale.
            _dsp.Apply(_bypass ? AudioDspSettings.Default with { Protection = ClipProtection.Off } : settings.ToDsp());
            _replayGain = _decoder.ReplayGain;
            ApplyReplayGain();
            OutputDescription = (_exact ? "Bit perfect · " : _bypass ? global::CinecorePlayer2025.Utilities.AppLanguage.T("Diretto · ", "Direct · ") : "") + (ExclusiveActive ? global::CinecorePlayer2025.Utilities.AppLanguage.T("WASAPI esclusivo · ", "Exclusive WASAPI · ") : global::CinecorePlayer2025.Utilities.AppLanguage.T("WASAPI condiviso · ", "Shared WASAPI · "))
                + $"{_format.SampleRate / 1000.0:0.#} kHz · " + (_format.Encoding == WaveFormatEncoding.IeeeFloat ? "32 bit float" : _outputValidBits + " bit");
            _dsp.Meters.OutputDescription = OutputDescription;
            CinecoreAudioSettings.Changed += OnSettingsChanged;

            BeginFadeInIfHandedOff();
            _source = new SampleSource(this, _format);
            _output = new WasapiOut(_device, ExclusiveActive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, true, ExclusiveActive ? 40 : 60);
            _output.Init(_source);

            _worker = new Thread(DecodeLoop) { IsBackground = true, Name = "Cinecore Audio decode", Priority = ThreadPriority.AboveNormal };
            _worker.Start();
            _progressTimer = new System.Threading.Timer(_ => Tick(), null, 100, 100);
            Active = this;
            SafeStatus("Cinecore Audio: " + (_exact ? "bit perfect" : _bypass ? global::CinecorePlayer2025.Utilities.AppLanguage.T("diretto", "direct") : ExclusiveActive ? global::CinecorePlayer2025.Utilities.AppLanguage.T("esclusivo", "exclusive") : global::CinecorePlayer2025.Utilities.AppLanguage.T("condiviso", "shared")));
            _update?.Invoke();
        }

        private WaveFormat? _format;

        private static MMDevice ResolveDevice(string? name)
        {
            var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrWhiteSpace(name))
            {
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    string friendly = d.FriendlyName ?? "";
                    if (friendly.Equals(name, StringComparison.OrdinalIgnoreCase) || name.Contains(friendly, StringComparison.OrdinalIgnoreCase) || friendly.Contains(name, StringComparison.OrdinalIgnoreCase))
                        return d;
                }
            }
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        // PCM stereo a 24 bit dentro un contenitore a 32: e' la forma in cui le uscite HDMI (e molti DAC)
        // accettano il 24 bit in esclusiva; NAudio non ha un costruttore per questo caso.
        private static WaveFormat Pcm24In32(int rate)
        {
            byte[] raw = new byte[40];
            BitConverter.GetBytes((ushort)0xFFFE).CopyTo(raw, 0);            // WAVE_FORMAT_EXTENSIBLE
            BitConverter.GetBytes((ushort)2).CopyTo(raw, 2);                 // canali
            BitConverter.GetBytes((uint)rate).CopyTo(raw, 4);
            BitConverter.GetBytes((uint)(rate * 8)).CopyTo(raw, 8);          // byte al secondo
            BitConverter.GetBytes((ushort)8).CopyTo(raw, 12);                // byte per fotogramma
            BitConverter.GetBytes((ushort)32).CopyTo(raw, 14);               // contenitore
            BitConverter.GetBytes((ushort)22).CopyTo(raw, 16);               // dimensione dell'estensione
            BitConverter.GetBytes((ushort)24).CopyTo(raw, 18);               // bit validi
            BitConverter.GetBytes((uint)3).CopyTo(raw, 20);                  // sinistro + destro
            new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray().CopyTo(raw, 24); // PCM
            IntPtr memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(raw.Length);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(raw, 0, memory, raw.Length);
                return WaveFormat.MarshalFromPtr(memory);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
        }

        private static readonly System.Reflection.FieldInfo? ValidBitsField =
            typeof(NAudio.Wave.WaveFormatExtensible).GetField("wValidBitsPerSample", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>Bit realmente usati dal formato: in un contenitore a 32 bit possono essere 24.</summary>
        private static int ValidBits(WaveFormat format)
        {
            try
            {
                if (format is NAudio.Wave.WaveFormatExtensible && ValidBitsField?.GetValue(format) is short valid && valid > 0 && valid <= format.BitsPerSample)
                    return valid;
            }
            catch { }
            return format.BitsPerSample;
        }

        private int _outputValidBits = 32;

        private static bool TryExclusiveFormat(MMDevice device, int rate, out WaveFormat format)
        {
            var candidates = new List<WaveFormat>();
            foreach (int r in new[] { rate, 48000, 44100 }.Distinct())
            {
                candidates.Add(WaveFormat.CreateIeeeFloatWaveFormat(r, 2));
                candidates.Add(new NAudio.Wave.WaveFormatExtensible(r, 32, 2));
                candidates.Add(Pcm24In32(r));
                candidates.Add(new NAudio.Wave.WaveFormatExtensible(r, 24, 2));
                candidates.Add(new WaveFormat(r, 16, 2));
            }
            foreach (var candidate in candidates)
            {
                try { if (device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, candidate)) { format = candidate; return true; } }
                catch { }
            }
            format = null!;
            return false;
        }

        // Uscita intera con almeno i bit della sorgente, alla sua frequenza. Niente virgola mobile:
        // la conversione finale la farebbe il driver, fuori dal nostro controllo.
        private static bool TryBitPerfectFormat(MMDevice device, int rate, int sourceBits, out WaveFormat format)
        {
            var candidates = new List<WaveFormat>();
            if (sourceBits <= 16) candidates.Add(new WaveFormat(rate, 16, 2));
            candidates.Add(new NAudio.Wave.WaveFormatExtensible(rate, 24, 2));
            candidates.Add(Pcm24In32(rate));
            candidates.Add(new NAudio.Wave.WaveFormatExtensible(rate, 32, 2));
            foreach (var candidate in candidates)
            {
                try { if (device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, candidate)) { format = candidate; return true; } }
                catch { }
            }
            format = null!;
            return false;
        }

        private bool _bypass;   // percorso diretto: nessuna elaborazione sul segnale in uscita
        private bool _exact;    // e in piu' sorgente intera e uscita con abbastanza bit: bit perfect

        /// <summary>Percorso diretto attivo (modalita' bit perfect richiesta e uscita esclusiva ottenuta).</summary>
        public bool DirectActive => _bypass;

        /// <summary>I campioni del file arrivano identici al dispositivo: percorso esatto e volume al massimo.</summary>
        public bool BitPerfectNow => _exact && _volume >= 0.9999f;

        private (double? Track, double? Album) _replayGain;

        private void OnSettingsChanged()
        {
            if (_bypass) return;
            try { _dsp?.Apply(CinecoreAudioSettings.Current.ToDsp()); ApplyReplayGain(); } catch { }
        }

        private void ApplyReplayGain()
        {
            if (_dsp == null || _bypass) return;
            var s = CinecoreAudioSettings.Current;
            double? gain = s.ReplayGain switch
            {
                "track" => _replayGain.Track ?? _replayGain.Album,
                "album" => _replayGain.Album ?? _replayGain.Track,
                _ => null
            };
            double value = s.ReplayGain == "off" ? 0 : (gain ?? 0) + s.ReplayGainPreampDb;
            if (Math.Abs(_dsp.ReplayGainDb - value) > 0.01) _dsp.ReplayGainDb = value;
        }

        public string ReplayGainDescription => _replayGain.Track is double t
            ? $"ReplayGain {t:+0.0;-0.0} dB" + (_replayGain.Album is double a ? $" (album {a:+0.0;-0.0} dB)" : "")
            : _replayGain.Album is double al ? $"ReplayGain album {al:+0.0;-0.0} dB" : "";

        // ===== decodifica =====
        private FloatRing _ring = new(1 << 18);
        private volatile bool _seeking;
        private double _anchorMedia;          // secondi del primo campione dopo l'ultimo seek
        private long _anchorOutputFrame = -1; // fotogramma di uscita corrispondente
        private long _framesDelivered;        // fotogrammi consegnati a WASAPI

        private void DecodeLoop()
        {
            var buffer = new float[65536 * Math.Max(1, _format!.Channels)];
            _ring = new FloatRing(Math.Max(1 << 18, buffer.Length * 3));
            try
            {
                while (!_disposed)
                {
                    double seek;
                    lock (_gate) { seek = _pendingSeek; _pendingSeek = -1; }
                    if (seek >= 0)
                    {
                        _decoder!.Seek(seek);
                        _ring.Clear();
                        lock (_gate) { _anchorMedia = seek; _anchorOutputFrame = -1; }
                        _decodeEnded = false; _ended = false;
                        lock (_gate) { if (_pendingSeek < 0) _seeking = false; }
                    }
                    if (_decodeEnded) { _wake.WaitOne(50); continue; }
                    if (_ring.Free < buffer.Length) { _wake.WaitOne(10); continue; }
                    int frames = _decoder!.Read(buffer, out _);
                    if (frames <= 0)
                    {
                        if (_decoder.EndOfStream) _decodeEnded = true;
                        continue;
                    }
                    _ring.Write(buffer, frames * _format.Channels);
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("[CinecoreAudio] decodifica interrotta: " + ex.Message);
                _decodeEnded = true;
                SafeStatus(global::CinecorePlayer2025.Utilities.AppLanguage.T("Cinecore Audio: errore di decodifica", "Cinecore Audio: decoding error"));
            }
        }

        private float[] _analysis = Array.Empty<float>();
        private float[] _meterCopy = Array.Empty<float>();

        /// <summary>Chiamato dal thread WASAPI: preleva, elabora, misura.</summary>
        private int Render(float[] target, int samples)
        {
            int channels = _format!.Channels;
            // Durante un seek nessun campione del punto precedente deve arrivare all'uscita.
            int got = _seeking ? 0 : _ring.Read(target, samples);
            if (got > 0)
            {
                lock (_gate) { if (_anchorOutputFrame < 0) _anchorOutputFrame = _framesDelivered; }
                var dsp = _dsp;
                if (_bypass)
                {
                    // I campioni in uscita non si toccano: misuratori e spettro lavorano su una copia.
                    if (_analysis.Length < got) _analysis = new float[got];
                    if (dsp != null)
                    {
                        if (_meterCopy.Length < got) _meterCopy = new float[got];
                        Array.Copy(target, _meterCopy, got);
                        dsp.AnalysisOut = _analysis;
                        dsp.Process(_meterCopy.AsSpan(0, got), got / channels);
                    }
                    else Array.Copy(target, _analysis, got);
                    try { PcmTap?.Invoke(_analysis, got / channels, channels, _format.SampleRate); } catch { }
                    // Volume sotto il massimo: l'utente lo ha chiesto, e l'uscita non e' piu' bit perfect.
                    float volume = _volume;
                    if (volume < 0.9999f)
                        for (int i = 0; i < got; i++) target[i] *= volume;
                }
                else if (dsp != null)
                {
                    if (_analysis.Length < got) _analysis = new float[got];
                    dsp.AnalysisOut = _analysis;
                    dsp.Process(target.AsSpan(0, got), got / channels);
                }
                // Alle analisi va il segnale prima del volume (vedi AudioDspChain.AnalysisOut).
                // Il brano che sta sfumando via non alimenta piu' misuratori e spettro.
                if (!_bypass && _fade != FadeOut)
                    try { PcmTap?.Invoke(dsp != null ? _analysis : target, got / channels, channels, _format.SampleRate); } catch { }
                if (_fade != FadeNone) ApplyFade(target, got, channels);
            }
            if (got < samples) Array.Clear(target, got, samples - got); // sottoflusso o fine brano: silenzio
            _framesDelivered += samples / channels;
            _wake.Set();
            return samples;
        }

        // ===== dissolvenza incrociata =====
        // Il passaggio avviene fra due motori: quello del brano che finisce continua a suonare da
        // solo mentre sfuma (e poi si chiude), quello del brano nuovo entra in dissolvenza. I due
        // flussi li somma il mixer di Windows, quindi vale solo per l'uscita condivisa.
        private const int FadeNone = 0, FadeIn = 1, FadeOut = 2;
        private volatile int _fade;
        private long _fadeFrames, _fadePosition;
        private static readonly object HandoffGate = new();
        private static CinecoreAudioEngine? _tail;      // il brano che sta sfumando via
        private static double _handoffSeconds;
        private static DateTime _handoffAtUtc;

        /// <summary>Il brano in uscita che sta ancora sfumando, se c'e'.</summary>
        public static bool TailPlaying { get { lock (HandoffGate) return _tail != null; } }

        /// <summary>Lascia suonare questo motore da solo mentre sfuma in <paramref name="seconds"/>, poi lo chiude.
        /// Il prossimo motore aperto entra in dissolvenza. False se non e' possibile (uscita esclusiva, motore fermo).</summary>
        public bool BeginFadeOutAndRelease(double seconds)
        {
            if (_disposed || ExclusiveActive || _format == null || _output?.PlaybackState != PlaybackState.Playing || seconds < 0.3) return false;
            CinecoreAudioEngine? previous;
            lock (HandoffGate)
            {
                previous = _tail;
                _tail = this;
                _handoffSeconds = seconds;
                _handoffAtUtc = DateTime.UtcNow;
            }
            try { previous?.Dispose(); } catch { }
            CinecoreAudioSettings.Changed -= OnSettingsChanged;
            if (ReferenceEquals(Active, this)) Active = null;
            _fadePosition = 0;
            _fadeFrames = Math.Max(1, (long)(seconds * _format.SampleRate));
            _fade = FadeOut;
            return true;
        }

        private void BeginFadeInIfHandedOff()
        {
            double seconds;
            lock (HandoffGate)
            {
                seconds = _handoffSeconds;
                bool fresh = (DateTime.UtcNow - _handoffAtUtc).TotalSeconds < 4;
                _handoffSeconds = 0;
                if (!fresh || seconds <= 0) return;
            }
            if (ExclusiveActive) { StopTail(); return; }
            _fadePosition = 0;
            _fadeFrames = Math.Max(1, (long)(seconds * _format!.SampleRate));
            _fade = FadeIn;
        }

        /// <summary>Tronca il brano in uscita (pausa, salto, stop: la coda non deve restare a suonare da sola).</summary>
        public static void StopTail()
        {
            CinecoreAudioEngine? tail;
            lock (HandoffGate) { tail = _tail; _tail = null; _handoffSeconds = 0; }
            try { tail?.Dispose(); } catch { }
        }

        // Curve a potenza costante: il volume percepito resta uguale durante il passaggio.
        private void ApplyFade(float[] target, int samples, int channels)
        {
            int mode = _fade;
            long total = _fadeFrames, position = _fadePosition;
            for (int i = 0; i < samples; i += channels)
            {
                double t = Math.Min(1.0, position / (double)total);
                float gain = (float)(mode == FadeIn ? Math.Sin(t * Math.PI / 2) : Math.Cos(t * Math.PI / 2));
                for (int c = 0; c < channels && i + c < samples; c++) target[i + c] *= gain;
                position++;
            }
            _fadePosition = position;
            if (position >= total && mode == FadeIn) _fade = FadeNone;
        }

        private void Tick()
        {
            if (_disposed) return;
            if (_fade == FadeOut)
            {
                // Sfumato del tutto, o brano finito prima: il motore si chiude da solo.
                if (_fadePosition >= _fadeFrames || (_decodeEnded && _ring.Count == 0))
                {
                    lock (HandoffGate) { if (ReferenceEquals(_tail, this)) _tail = null; }
                    System.Threading.Tasks.Task.Run(Dispose);
                }
                return;
            }
            double position = PositionSeconds;
            if (!_ended && _decodeEnded && _ring.Count == 0)
            {
                long played = PlayedFrames();
                if (played >= _framesDelivered - (_format?.SampleRate ?? 48000) / 10)
                {
                    _ended = true;
                    try { OnProgressSeconds?.Invoke(_duration > 0 ? _duration : position); } catch { }
                    return;
                }
            }
            if (_output?.PlaybackState == PlaybackState.Playing)
                try { OnProgressSeconds?.Invoke(position); } catch { }
        }

        private long PlayedFrames()
        {
            try { return _output == null || _format == null ? 0 : _output.GetPosition() / _format.BlockAlign; } catch { return 0; }
        }

        // ===== controllo =====
        public void Play() { _output?.Play(); _update?.Invoke(); }
        public void Pause() { StopTail(); _output?.Pause(); _update?.Invoke(); }
        public void Stop() { try { _output?.Stop(); } catch { } }

        public double DurationSeconds => _duration;

        public double PositionSeconds
        {
            get
            {
                if (_ended) return _duration;
                long anchor; double media;
                lock (_gate) { anchor = _anchorOutputFrame; media = _anchorMedia; }
                if (anchor < 0 || _format == null) return media;
                long latency = _dsp?.LatencyFrames ?? 0;
                double seconds = media + (PlayedFrames() - anchor - latency) / (double)_format.SampleRate;
                return Math.Clamp(seconds, media, _duration > 0 ? _duration : double.MaxValue);
            }
            set
            {
                StopTail();
                _fade = FadeNone;
                double target = Math.Clamp(value, 0, _duration > 0 ? Math.Max(0, _duration - 0.05) : value);
                lock (_gate) { _pendingSeek = target; _anchorMedia = target; _anchorOutputFrame = -1; _seeking = true; }
                _ring.Clear();
                _ended = false;
                _wake.Set();
            }
        }

        public void SetVolume(float volume)
        {
            _volume = Math.Clamp(volume, 0f, 1f);
            if (_dsp != null && !_bypass) _dsp.Volume = _volume;
        }

        private double _boostDb;

        /// <summary>Amplificazione oltre il 100% (dB). Falso sul percorso diretto, dove il segnale non si tocca.</summary>
        public bool SetVolumeBoostDb(double db)
        {
            db = Math.Clamp(db, 0, 12);
            bool same = Math.Abs(db - _boostDb) < 0.01;
            _boostDb = db;
            if (_bypass || _dsp == null) return _boostDb <= 0;
            if (!same || Math.Abs(_dsp.BoostDb - _boostDb) > 0.01) _dsp.BoostDb = _boostDb;
            return true;
        }

        public void BindUpdateCallback(Action? cb) => _update = cb;

        public List<DsStreamItem> EnumerateStreams() => new()
        {
            new DsStreamItem { GlobalIndex = 0, NativeIndex = 0, IsAudio = true, Name = SourceDescription.Length > 0 ? SourceDescription : "Audio", Selected = true }
        };

        private void SafeStatus(string text) { try { OnStatus?.Invoke(text); } catch { } }

        // ===== parti video: non applicabili =====
        public void UpdateVideoWindow(nint ownerHwnd, Rectangle ownerClient) { }
        public Rectangle GetLastDestRectAsClient(Rectangle ownerClient) => Rectangle.Empty;
        public void SetStereo3D(Stereo3DMode mode) { }
        public void SetUpscaling(bool enable) { }
        public bool IsBitstreamActive() => false;
        public bool HasDisplayControl() => false;
        public (string text, DateTime when) GetLastVideoMTDump() => ("", DateTime.MinValue);
        public (int width, int height, string subtype) GetNegotiatedVideoFormat() => (0, 0, "");
        public (int bytes, DateTime when) GetLastSnapshotInfo() => (0, DateTime.MinValue);
        public bool EnableByGlobalIndex(int globalIndex) => globalIndex == 0;
        public bool DisableSubtitlesIfPossible() => false;
        public bool TrySnapshot(out int byteCount) { byteCount = 0; return false; }
        public Bitmap? GetPreviewFrame(double seconds, int maxW = 360) => null;
        public void SetMadVrChroma(MadVrCategoryPreset preset) { }
        public void SetMadVrImageUpscale(MadVrCategoryPreset preset) { }
        public void SetMadVrImageDownscale(MadVrCategoryPreset preset) { }
        public void SetMadVrRefinement(MadVrCategoryPreset preset) { }
        public void SetMadVrFps(MadVrFpsChoice choice) { }
        public void SetMadVrHdrMode(MadVrHdrMode mode) { }
        public bool TrySendRendererKey(Keys keyData) => false;
        public bool TrySetMadVrExclusiveModeDisabled(bool disabled) => false;
        public string GetRendererDiagnosticSnapshot() => $"Cinecore Audio Engine · {SourceDescription} -> {OutputDescription}";

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Chiuso il brano in corso (stop, salto, altro contenuto): la coda di quello precedente va con lui.
            // Se invece e' questo motore a sfumare, la chiusura avviene per un nuovo passaggio che lo rimpiazza.
            bool isTail;
            lock (HandoffGate) { isTail = ReferenceEquals(_tail, this); if (isTail) _tail = null; }
            if (!isTail && _fade != FadeOut) StopTail();
            CinecoreAudioSettings.Changed -= OnSettingsChanged;
            if (ReferenceEquals(Active, this)) Active = null;
            try { _progressTimer?.Dispose(); } catch { }
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _wake.Set();
            try { _worker?.Join(1000); } catch { }
            try { _decoder?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            _wake.Dispose();
        }

        // ===== ponte verso NAudio =====
        private sealed class SampleSource : IWaveProvider
        {
            private readonly CinecoreAudioEngine _owner;
            private float[] _scratch = Array.Empty<float>();
            private readonly Random _dither = new();
            public WaveFormat WaveFormat { get; }
            public SampleSource(CinecoreAudioEngine owner, WaveFormat format) { _owner = owner; WaveFormat = format; }

            public int Read(byte[] buffer, int offset, int count)
            {
                int bytesPerSample = WaveFormat.BitsPerSample / 8;
                int samples = count / bytesPerSample;
                samples -= samples % WaveFormat.Channels;
                if (_scratch.Length < samples) _scratch = new float[samples];
                _owner.Render(_scratch, samples);
                if (WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    Buffer.BlockCopy(_scratch, 0, buffer, offset, samples * 4);
                    return samples * 4;
                }
                // Uscita intera (esclusivo): arrotondamento con dither TPDF a 16/24 bit.
                int bits = WaveFormat.BitsPerSample;
                // Il dither va fatto sui bit che il dispositivo usa davvero (24 in un contenitore a 32).
                int valid = Math.Clamp(_owner._outputValidBits, 16, bits), shift = bits - valid;
                double scale = valid switch { 16 => 32767.0, 24 => 8388607.0, _ => 2147483647.0 };
                bool dither = valid <= 24;
                if (_owner.BitPerfectNow)
                {
                    // Bit perfect: i campioni sono interi della sorgente divisi per una potenza di due;
                    // la stessa potenza di due li riporta identici, senza dither ne' arrotondamenti.
                    double exact = bits switch { 16 => 32768.0, 24 => 8388608.0, _ => 2147483648.0 };
                    for (int i = 0; i < samples; i++)
                    {
                        long q = (long)Math.Clamp(_scratch[i] * exact, -exact, exact - 1);
                        int o = offset + i * bytesPerSample;
                        for (int b = 0; b < bytesPerSample; b++) buffer[o + b] = (byte)(q >> (8 * b));
                    }
                    return samples * bytesPerSample;
                }
                for (int i = 0; i < samples; i++)
                {
                    double v = _scratch[i] * scale;
                    if (dither) v += _dither.NextDouble() - _dither.NextDouble();
                    long q = (long)Math.Round(Math.Clamp(v, -scale - 1, scale)) << shift;
                    int o = offset + i * bytesPerSample;
                    for (int b = 0; b < bytesPerSample; b++) buffer[o + b] = (byte)(q >> (8 * b));
                }
                return samples * bytesPerSample;
            }
        }

        /// <summary>Buffer circolare a singolo produttore/consumatore per campioni float.</summary>
        private sealed class FloatRing
        {
            private readonly float[] _data;
            private int _read, _write, _count;
            private readonly object _lock = new();
            public FloatRing(int capacity) => _data = new float[capacity];
            public int Count { get { lock (_lock) return _count; } }
            public int Free { get { lock (_lock) return _data.Length - _count; } }
            public void Clear() { lock (_lock) { _read = _write = _count = 0; } }
            public void Write(float[] source, int length)
            {
                lock (_lock)
                {
                    length = Math.Min(length, _data.Length - _count);
                    int first = Math.Min(length, _data.Length - _write);
                    Array.Copy(source, 0, _data, _write, first);
                    Array.Copy(source, first, _data, 0, length - first);
                    _write = (_write + length) % _data.Length; _count += length;
                }
            }
            public int Read(float[] target, int length)
            {
                lock (_lock)
                {
                    length = Math.Min(length, _count);
                    int first = Math.Min(length, _data.Length - _read);
                    Array.Copy(_data, _read, target, 0, first);
                    Array.Copy(_data, 0, target, first, length - first);
                    _read = (_read + length) % _data.Length; _count -= length;
                    return length;
                }
            }
        }
    }
}
