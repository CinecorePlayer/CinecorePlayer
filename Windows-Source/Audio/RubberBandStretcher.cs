#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025.Audio
{
    /// <summary>Time-stretch usato dall'automix: Rubber Band se c'e' la libreria, altrimenti il WSOLA interno.</summary>
    internal interface ITimeStretch
    {
        /// <summary>Tempo relativo: 1,05 = 5% piu' veloce (fotogrammi d'ingresso consumati per fotogramma in uscita).</summary>
        double Ratio { get; set; }
        bool InputComplete { get; set; }
        /// <summary>Fotogramma d'ingresso (assoluto) e velocita' a cui corrisponde il prossimo fotogramma in uscita.</summary>
        (double Media, double Rate) NextOutput { get; }
        int Produce(float[] target, int offset, int frames, Func<float[], int, int, int> read, Action<int, double, double>? spans = null);
    }

    internal static class TimeStretchFactory
    {
        public static ITimeStretch Create(int sampleRate, int channels, double ratio)
        {
            if (RubberBandStretcher.Available)
            {
                try { return new RubberBandStretcher(sampleRate, channels, ratio); }
                catch (Exception ex) { Dbg.Warn("[AUTOMIX] Rubber Band: " + ex.Message + " (internal stretcher instead)"); }
            }
            return new TimeStretcher(sampleRate, channels) { Ratio = ratio };
        }
    }

    /// <summary>
    /// Rubber Band 4 (motore R3 "finer", tempo reale): cambia il tempo mantenendo pitchScale = 1. La libreria e' third-parties\rubberband\rubberband.dll,
    /// compilata dai sorgenti ufficiali (GPL). La posizione nel brano si tiene sommando la velocita' a cui e' stato prodotto
    /// ogni fotogramma in uscita, dopo aver compensato il ritardo iniziale come prescrive la libreria (start pad / start delay).
    /// </summary>
    internal sealed class RubberBandStretcher : ITimeStretch, IDisposable
    {
        private const string Library = "rubberband.dll";
        private const int OptionProcessRealTime = 0x00000001, OptionEngineFiner = 0x20000000, OptionChannelsTogether = 0x10000000,
            OptionPitchHighConsistency = 0x04000000, OptionThreadingNever = 0x00010000;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr rubberband_new(uint sampleRate, uint channels, int options, double timeRatio, double pitchScale);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void rubberband_delete(IntPtr state);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void rubberband_set_time_ratio(IntPtr state, double ratio);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint rubberband_get_preferred_start_pad(IntPtr state);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint rubberband_get_start_delay(IntPtr state);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint rubberband_get_samples_required(IntPtr state);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void rubberband_set_max_process_size(IntPtr state, uint samples);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void rubberband_process(IntPtr state, IntPtr[] input, uint samples, int final);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rubberband_available(IntPtr state);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint rubberband_retrieve(IntPtr state, IntPtr[] output, uint samples);

        private static bool? _available;
        /// <summary>Percorso della libreria dato dalla piattaforma (macOS); null = ricerca di Windows.</summary>
        internal static Func<string?>? PlatformLibraryPath;
        private readonly object _processGate = new();

        /// <summary>La libreria si carica (cercata accanto al programma, in third-parties\rubberband).</summary>
        public static bool Available
        {
            get
            {
                if (_available.HasValue) return _available.Value;
                try
                {
                    // macOS: il percorso lo da' l'app (Contents/Frameworks o Homebrew), che risolve anche i DllImport.
                    if (PlatformLibraryPath?.Invoke() is { } platform)
                    {
                        _available = NativeLibrary.TryLoad(platform, out _);
                        Dbg.Log($"[AUTOMIX] Rubber Band {(_available == true ? platform : "not loadable: internal stretcher")}", Dbg.LogLevel.Info);
                        return _available.Value;
                    }
                    string local = Path.Combine(AppContext.BaseDirectory, "third-parties", "rubberband", Library);
                    string beside = Path.Combine(Path.GetDirectoryName(typeof(RubberBandStretcher).Assembly.Location) ?? "", "third-parties", "rubberband", Library);
                    if (!File.Exists(local) && File.Exists(beside)) local = beside;
                    if (File.Exists(local)) NativeLibrary.Load(local);
                    // Caricata per percorso: le chiamate per nome trovano poi il modulo gia' in memoria.
                    _available = NativeLibrary.TryLoad(File.Exists(local) ? local : Library, out _);
                }
                catch { _available = false; }
                Dbg.Log($"[AUTOMIX] Rubber Band {(_available == true ? "available" : "not found: internal stretcher")}", Dbg.LogLevel.Info);
                return _available.Value;
            }
        }

        private const int Block = 1024;
        private readonly int _channels;
        private IntPtr _state;
        private readonly IntPtr[] _inPtr, _outPtr;
        private readonly float[] _interleaved;
        private readonly float[] _planar;
        private int _discard;            // fotogrammi d'uscita iniziali da scartare (ritardo della libreria)
        private long _inputFrames;       // fotogrammi d'ingresso consegnati (senza il riempimento iniziale)
        private double _outMedia;        // fotogramma d'ingresso a cui corrisponde il prossimo fotogramma in uscita
        private double _ratio = 1, _appliedRatio = 1;

        public RubberBandStretcher(int sampleRate, int channels, double ratio)
        {
            _channels = channels;
            _ratio = _appliedRatio = Math.Clamp(ratio, 0.5, 2.0);
            _state = rubberband_new((uint)sampleRate, (uint)channels,
                OptionProcessRealTime | OptionEngineFiner | OptionChannelsTogether | OptionPitchHighConsistency | OptionThreadingNever,
                1.0 / _ratio, 1.0);
            if (_state == IntPtr.Zero) throw new InvalidOperationException("rubberband_new failed");
            rubberband_set_max_process_size(_state, Block);
            _inPtr = new IntPtr[channels]; _outPtr = new IntPtr[channels];
            for (int c = 0; c < channels; c++)
            {
                _inPtr[c] = Marshal.AllocHGlobal(Block * sizeof(float));
                _outPtr[c] = Marshal.AllocHGlobal(Block * sizeof(float));
            }
            _interleaved = new float[Block * channels];
            _planar = new float[Block];
            // Riempimento iniziale di silenzio e scarto dell'uscita corrispondente: il primo campione in uscita e'
            // allora il primo del brano, allineato (come prescrive la documentazione del modo in tempo reale).
            int pad = (int)rubberband_get_preferred_start_pad(_state);
            for (int c = 0; c < channels; c++) Marshal.Copy(new float[Block], 0, _inPtr[c], Block);
            while (pad > 0)
            {
                int n = Math.Min(pad, Block);
                rubberband_process(_state, _inPtr, (uint)n, 0);
                pad -= n;
            }
            _discard = (int)rubberband_get_start_delay(_state);
        }

        public double Ratio { get => _ratio; set => _ratio = Math.Clamp(value, 0.5, 2.0); }
        public bool InputComplete { get; set; }
        private bool _finalSent;

        public (double Media, double Rate) NextOutput => (_outMedia, _appliedRatio);

        public int Produce(float[] target, int offset, int frames, Func<float[], int, int, int> read, Action<int, double, double>? spans = null)
        {
            lock (_processGate) return ProduceCore(target, offset, frames, read, spans);
        }

        private int ProduceCore(float[] target, int offset, int frames, Func<float[], int, int, int> read, Action<int, double, double>? spans)
        {
            if (_state == IntPtr.Zero) return 0;
            int ch = _channels, produced = 0;
            if (Math.Abs(_ratio - _appliedRatio) > 1e-7)
            {
                rubberband_set_time_ratio(_state, 1.0 / _ratio);
                _appliedRatio = _ratio;
            }
            int guard = 0;
            while (produced < frames && guard++ < 256)
            {
                int available = rubberband_available(_state);
                if (available > 0)
                {
                    int n = Math.Min(Block, available);
                    // A small callback can straddle the end of the initial latency discard.
                    // Bound retrieval by discard + writable output, not only after discard
                    // reaches zero, otherwise the first retained block can overrun target.
                    n = Math.Min(n, frames - produced + Math.Max(0,_discard));
                    uint got = rubberband_retrieve(_state, _outPtr, (uint)n);
                    if (got == 0) break;
                    int g = (int)got, skip = Math.Min(_discard, g);
                    _discard -= skip;
                    int keep = g - skip;
                    // Real-time Rubber Band rounds its final drain up to an internal block.
                    // Trim that padding using the media-frame count, including variable tempo.
                    if (_finalSent) keep = Math.Min(keep, (int)Math.Ceiling(Math.Max(0, _inputFrames - _outMedia) / _appliedRatio));
                    if (keep <= 0) continue;
                    for (int c = 0; c < ch; c++)
                    {
                        Marshal.Copy(_outPtr[c] + skip * sizeof(float), _planar, 0, keep);
                        for (int i = 0; i < keep; i++) target[offset + (produced + i) * ch + c] = _planar[i];
                    }
                    spans?.Invoke(keep, _outMedia, _appliedRatio);
                    _outMedia += keep * _appliedRatio;
                    produced += keep;
                    continue;
                }
                int required = (int)Math.Min(Block, Math.Max(64, rubberband_get_samples_required(_state)));
                int samples = read(_interleaved, 0, required * ch);
                int inFrames = samples / ch;
                if (inFrames <= 0)
                {
                    if (InputComplete && !_finalSent)
                    {
                        rubberband_process(_state, _inPtr, 0, 1);
                        _finalSent = true;
                        continue;
                    }
                    break;
                }
                for (int c = 0; c < ch; c++)
                {
                    for (int i = 0; i < inFrames; i++) _planar[i] = _interleaved[i * ch + c];
                    Marshal.Copy(_planar, 0, _inPtr[c], inFrames);
                }
                rubberband_process(_state, _inPtr, (uint)inFrames, 0);
                _inputFrames += inFrames;
            }
            return produced;
        }

        public void Dispose()
        {
            lock (_processGate) DisposeCore();
        }

        private void DisposeCore()
        {
            if (_state != IntPtr.Zero) { rubberband_delete(_state); _state = IntPtr.Zero; }
            for (int c = 0; c < _inPtr.Length; c++) { if (_inPtr[c] != IntPtr.Zero) Marshal.FreeHGlobal(_inPtr[c]); _inPtr[c] = IntPtr.Zero; }
            for (int c = 0; c < _outPtr.Length; c++) { if (_outPtr[c] != IntPtr.Zero) Marshal.FreeHGlobal(_outPtr[c]); _outPtr[c] = IntPtr.Zero; }
            GC.SuppressFinalize(this);
        }

        ~RubberBandStretcher() { try { Dispose(); } catch { } }
    }
}
