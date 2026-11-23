using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using CinecorePlayer2025.Audio;

namespace CinecoreMac;

/// <summary>
/// Cinecore Audio Engine per il Mac (CinecoreAudioEngine di Windows): la musica non passa piu' da AVPlayer ma da una
/// catena propria. FFmpeg decodifica ogni brano in float stereo a 48 kHz, la catena DSP di Windows (AudioDspChain:
/// EQ grafico 10/31 bande o parametrico, limiter true-peak, crossfeed, ampiezza e bilanciamento, loudness, AutoEQ
/// dinamico) lavora in doppia precisione, e CoreAudio (AudioQueue) suona sull'uscita scelta nelle Impostazioni.
/// Il brano successivo si prepara in anticipo: senza pause fra i brani (gapless) o con la dissolvenza incrociata.
/// Stessa interfaccia del vecchio lettore (MacAudioPlayer), piu' NextSource per il brano che segue.
/// </summary>
internal sealed unsafe class MacMusicEngine : IDisposable
{
    public const int BaseRate = 48000, Channels = 2;
    /// <summary>Frequenza dell'uscita: 48 kHz, oppure quella del file con Esclusivo / Bit perfect (vedi ChooseRate).</summary>
    private int Rate = BaseRate;
    private const int BufferFrames = 2048, BufferCount = 4;

    // ------------------------------------------------------------------ CoreAudio (AudioQueue)
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [StructLayout(LayoutKind.Sequential)]
    private struct StreamFormat { public double SampleRate; public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct QueueBuffer { public uint Capacity; public IntPtr Data; public uint ByteSize; public IntPtr UserData; public uint PacketDescCapacity; public IntPtr PacketDescs; public uint PacketDescCount; }
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct TimeStamp { public double SampleTime; public ulong HostTime; public double RateScalar; public ulong WordClockTime; }

    [DllImport(AudioToolbox)] private static extern int AudioQueueNewOutput(StreamFormat* format, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);
    [DllImport(AudioToolbox)] private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint size, out IntPtr buffer);
    [DllImport(AudioToolbox)] private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescs, IntPtr descs);
    [DllImport(AudioToolbox)] private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(AudioToolbox)] private static extern int AudioQueuePause(IntPtr queue);
    [DllImport(AudioToolbox)] private static extern int AudioQueueStop(IntPtr queue, byte immediate);
    [DllImport(AudioToolbox)] private static extern int AudioQueueDispose(IntPtr queue, byte immediate);
    [DllImport(AudioToolbox)] private static extern int AudioQueueGetCurrentTime(IntPtr queue, IntPtr timeline, TimeStamp* time, byte* discontinuity);
    [DllImport(AudioToolbox)] private static extern int AudioQueueSetProperty(IntPtr queue, uint property, IntPtr* data, uint size);
    [DllImport(AudioToolbox)] private static extern int AudioQueueGetProperty(IntPtr queue, uint property, IntPtr* data, uint* size);
    [DllImport(CoreFoundation)] private static extern bool CFStringGetCString(IntPtr s, byte* buffer, long size, uint encoding);

    /// <summary>Registro dell'uscita (~/Library/Logs/Cinecore/musica.log): cambi di dispositivo e dispositivo reale.</summary>
    private static void OutputLog(string line)
    {
        Console.Error.WriteLine("[musica] " + line);
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Cinecore");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "musica.log");
            if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.Delete(file);
            File.AppendAllText(file, DateTime.Now.ToString("dd/MM HH:mm:ss ") + line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>Dispositivo su cui l'AudioQueue suona davvero (per il registro: verifica del cambio d'uscita).</summary>
    private string QueueDevice()
    {
        if (_queue == IntPtr.Zero || NullOutput) return "-";
        IntPtr cf = IntPtr.Zero; uint size = (uint)IntPtr.Size;
        if (AudioQueueGetProperty(_queue, CurrentDeviceProperty, &cf, &size) != 0 || cf == IntPtr.Zero) return "(predefinito)";
        var buffer = stackalloc byte[512];
        string uid = CFStringGetCString(cf, buffer, 512, 0x08000100) ? new string((sbyte*)buffer) : "?";
        CFRelease(cf);
        return uid;
    }
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr obj);
    private const uint CurrentDeviceProperty = 0x61716364; // 'aqcd'

    private IntPtr _queue;
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private GCHandle _self;
    private readonly object _gate = new();
    private bool _playing, _queueRunning;

    // ------------------------------------------------------------------ brani
    private sealed class Track
    {
        public string Path = "";
        public Process? Ffmpeg;
        public Thread? Reader;
        public float[] Ring = new float[BaseRate * Channels * 6];   // 6 s di anticipo
        public int Rate = BaseRate;                              // frequenza a cui ffmpeg lo decodifica
        public int SourceRate;                                   // frequenza del file (ffprobe, 0 finche' non nota)
        public int SourceBits;                                   // bit dei campioni del file (ffprobe, 0 finche' non noti)
        public long Written, Read;                               // campioni (non fotogrammi)
        public volatile bool Eof, Failed;
        public long StartFrame;                                  // punto del brano da cui si e' partiti (ricerca)
        public long DurationFrames;                              // da ffprobe (0 se non noto)
        public double Gain = 1;                                  // ReplayGain lineare
        public bool OnCd;                                        // letto da un CD audio (anticipo lungo, un lettore alla volta)
        public string Album = ""; public int TrackNumber;         // per non mescolare i brani consecutivi di un album
        public long FramesOut;                                   // fotogrammi del brano gia' mandati all'uscita
        public long Available => Interlocked.Read(ref Written) - Interlocked.Read(ref Read);
        public readonly object RingGate = new();

        // Automix a tempo (CinecoreAudioEngine di Windows, "tempo adattato"): il brano che entra prende il tempo di quello
        // che esce per tutto il passaggio, poi torna al suo in TempoReturnSeconds. Una volta creato, il time-stretch resta
        // fino alla fine del brano (a 1,000x: ha gia' letto avanti dall'anello, toglierlo farebbe un salto).
        public ITimeStretch? Stretch;
        public double StretchBase;          // FramesOut quando il time-stretch e' partito
        public double StretchNominal = 1;   // tempo durante il passaggio
        public long StretchOut;             // fotogrammi prodotti dal time-stretch
        public long RampStart = -1;         // inizio del ritorno al tempo del brano
        public double RampTo = 1;           // tempo d'arrivo della curva (1 = il suo; il brano che finisce va a quello scelto)
        public double RampFrom = 1, ReturnSeconds = 8;
        public float[] StretchBuffer = Array.Empty<float>();
        public Func<float[], int, int, int>? StretchRead;
        /// <summary>Fotogramma del brano (dal punto di partenza) che si sente col prossimo campione in uscita.</summary>
        public double MediaFrame => Stretch is { } st && !double.IsNaN(StretchBase) ? StretchBase + st.NextOutput.Media : FramesOut;
        /// <summary>Fotogrammi del brano per fotogramma in uscita (1 senza time-stretch).</summary>
        public double Speed => Stretch?.NextOutput.Rate ?? 1;
    }

    private Track? _current, _next;

    // Fotogramma d'uscita (dall'avvio della coda) -> brano e punto del brano: la posizione segue quello che si sente.
    private readonly List<(long OutFrame, Track Track, double TrackFrame, double Speed)> _marks = new();
    private long _outFrames;
    private long _endOutFrame = -1;   // fotogramma d'uscita dove finisce l'ultimo brano (niente dopo)

    private AudioDspChain _dsp = new(Channels, BaseRate);
    private float _volume = 1;
    private bool _muted;

    public AudioEngineMeters Meters => _dsp.Meters;

    // ----- analisi (grafici del brano a schermo intero): spettro, loudness EBU R128, true peak, livelli -----
    // I campioni (dopo il DSP, prima del volume) vanno a un thread a parte: mai FFT nel callback dell'audio.
    private readonly CinecorePlayer2025.Utilities.LoopbackSampler _sampler = new();
    // Pezzi per l'analisi con il fotogramma d'uscita in cui finiscono (per sapere quando si sentono davvero).
    private readonly System.Collections.Concurrent.BlockingCollection<(float[] Pcm, long EndFrame)> _analysisQueue = new(64);
    private float[] _partBuffer = Array.Empty<float>();
    // Doppio canale (come Windows): con l'uscita in bitstream i grafici si alimentano da una seconda decodifica PCM.
    private ParallelPcmAnalysis? _parallel;
    /// <summary>L'uscita non e' PCM prodotto dal motore (bitstream): i grafici passano al secondo canale.</summary>
    public bool OutputIsBitstream { get; set; }
    private bool UseParallelAnalysis => OutputIsBitstream || Environment.GetEnvironmentVariable("CINECORE_QA_PARALLEL_ANALYSIS") == "1";
    private Thread? _analysisThread;
    /// <summary>1 = alla prossima misura l'analisi riparte (brano nuovo, salto, impostazioni).</summary>
    private int _analysisReset;
    private void ResetAnalysis() => Interlocked.Exchange(ref _analysisReset, 1);
    private volatile bool _analysis;
    public CinecorePlayer2025.Utilities.LoopbackSampler.AudioMetrics? Analysis { get; private set; }

    /// <summary>Acceso solo con i grafici aperti (risparmia CPU il resto del tempo).</summary>
    public bool AnalysisEnabled
    {
        get => _analysis;
        set
        {
            if (value == _analysis) return;
            _analysis = value;
            if (value && _analysisThread == null)
            {
                _sampler.OnMetrics += m => Analysis = m;
                _dsp.AnalysisOut = new float[BufferFrames * Channels];
                _analysisThread = new Thread(() =>
                {
                    long qaLast = 0; double qaLeadSum = 0, qaLeadMax = 0, qaGapMax = 0; int qaCount = 0; long qaReport = Environment.TickCount64;
                    foreach (var (chunk, endFrame) in _analysisQueue.GetConsumingEnumerable())
                        try
                        {
                            if (Interlocked.Exchange(ref _analysisReset, 0) == 1) _sampler.ResetProgramme();
                            // Come il loopback di Windows: l'analisi riceve il suono quando esce dalle casse, a pezzetti.
                            // Prima riceveva interi buffer da 43 ms al momento del riempimento (~340 ms prima di sentirli):
                            // spettro e VU anticipavano la musica e la curva si muoveva a scatti (23 misure al secondo).
                            int parts = 4, frames = chunk.Length / Channels, partFrames = frames / parts;
                            for (int k = 0; k < parts; k++)
                            {
                                long partEnd = endFrame - (long)(parts - 1 - k) * partFrames;
                                long waitStart = Environment.TickCount64;
                                while (_analysis && _playing && Environment.TickCount64 - waitStart < 600)
                                {
                                    double ahead = (partEnd - Played()) * 1000.0 / Rate;
                                    if (ahead <= 2 || ahead > 1500) break; // salti e ripartenze: niente attese
                                    Thread.Sleep((int)Math.Clamp(ahead, 1, 15));
                                }
                                int n = (k == parts - 1 ? frames - k * partFrames : partFrames) * Channels;
                                if (_partBuffer.Length != n) _partBuffer = new float[n];
                                Array.Copy(chunk, k * partFrames * Channels, _partBuffer, 0, n);
                                _sampler.PushPcm(_partBuffer, n, Rate, Channels);
                            }
                            if (QaTiming)
                            {
                                long now = Environment.TickCount64;
                                double lead = (endFrame - Played()) * 1000.0 / Rate;
                                qaLeadSum += lead; qaLeadMax = Math.Max(qaLeadMax, lead); qaCount++;
                                if (qaLast > 0) qaGapMax = Math.Max(qaGapMax, now - qaLast);
                                qaLast = now;
                                if (now - qaReport >= 2000)
                                {
                                    Console.Error.WriteLine($"[qa-analisi] {qaCount * 1000.0 / (now - qaReport):0.0} misure/s, anticipo sul suono medio {qaLeadSum / Math.Max(1, qaCount):0} ms max {qaLeadMax:0} ms, pausa max fra misure {qaGapMax} ms");
                                    qaReport = now; qaCount = 0; qaLeadSum = 0; qaLeadMax = 0; qaGapMax = 0;
                                }
                            }
                        }
                        catch { }
                }) { IsBackground = true, Name = "musica analisi", Priority = ThreadPriority.BelowNormal };
                _analysisThread.Start();
            }
            if (value && UseParallelAnalysis)
            {
                _parallel ??= new ParallelPcmAnalysis(() => (Source, Position, IsPlaying),
                    (pcm, n, rate, ch) => { try { _sampler.PushPcm(pcm, n, rate, ch); } catch { } },
                    () => { try { _sampler.ResetProgramme(); } catch { } });
                _parallel.Start();
            }
            if (!value) { Analysis = null; _parallel?.Stop(); }
        }
    }

    static MacMusicEngine()
    {
        // Rubber Band (Automix a tempo): percorso dal pacchetto o da Homebrew, DllImport risolti dal resolver dell'app.
        RubberBandStretcher.PlatformLibraryPath = MpvEngine.RubberBandPath;
        MpvEngine.EnsureResolver();
    }

    public MacMusicEngine()
    {
        ApplySettings();
        CinecoreAudioSettings.Changed += ApplySettings;
        _control = new Timer(_ => Control(), null, 500, 100);
    }

    // ------------------------------------------------------------------ passaggi (dissolvenza, Automix)
    private readonly Timer _control;
    private AutomixPlanner.MixPlan? _plan;
    private string? _planFor;
    private int _controlling;
    private int _previousCrossfade;

    /// <summary>Ogni 100 ms, fuori dal thread dell'audio: decide se e come passare al brano successivo.</summary>
    private void Control()
    {
        if (Interlocked.Exchange(ref _controlling, 1) != 0) return;
        try
        {
            var s = CinecoreAudioSettings.Current;
            Track? cur, next; string? nextPath;
            lock (_gate)
            {
                cur = _current; next = _next; nextPath = _nextSource;
                if (cur == null || !_playing || _mix != null || _fallback != null) return;
            }
            if (DirectMode) CheckRateSwitch(cur);
            if (s.BitPerfect) return; // bit perfect: niente dissolvenze, Automix o mix manuale (solo gapless)
            // Passaggio scelto a mano per questa coppia (Mix con il prossimo brano…): vale anche con la dissolvenza spenta.
            if (nextPath != null && ManualMixStore.Get(cur.Path, nextPath) is { } manual && ManualMix(cur, next, nextPath, manual, s)) return;
            if (s.CrossfadeSeconds > 0)
            {
                if (next is not { Failed: false } || cur.DurationFrames <= 0 || SameAlbumInOrder(cur, next)) return;
                long fade = Math.Min(s.CrossfadeSeconds, 12) * (long)Rate, length = cur.DurationFrames - cur.StartFrame;
                if (length < fade * 2 + 8L * Rate) return; // brani corti: niente dissolvenza
                lock (_gate)
                    if (_mix == null && s.CrossfadeSeconds > 0 && ReferenceEquals(_current, cur) && ReferenceEquals(_next, next) && next.FramesOut == 0)
                        _mix = NewMix(cur, next, length - fade, fade / (double)Rate, 0, new TransitionShape(TransitionStyle.Plain), 0, "dissolvenza");
                return;
            }
            if (s.CrossfadeSeconds < 0 && nextPath != null) Automix(cur, next, nextPath, s);
        }
        catch (Exception ex) { Console.Error.WriteLine("[musica] passaggio: " + ex.Message); }
        finally { Volatile.Write(ref _controlling, 0); }
    }

    private static bool SameAlbumInOrder(Track a, Track b) =>
        a.Album.Length > 0 && string.Equals(a.Album, b.Album, StringComparison.OrdinalIgnoreCase) && a.TrackNumber > 0 && b.TrackNumber == a.TrackNumber + 1;

    /// <summary>
    /// Automix (PlayerForm.Crossfade di Windows): il brano dice dove comincia a spegnersi e dove finisce; con i profili di
    /// entrambi il planner sceglie punti musicali, durata in battute, stile (volumi, EQ a tre bande, filtri, eco) e il
    /// punto d'ingresso del successivo. Con "Sincronizzazione tempo" (Impostazioni › Cinecore Audio): il brano nuovo prende il tempo
    /// del vecchio con Rubber Band per tutto il passaggio, agganciato battuta per battuta, poi torna al suo.
    /// </summary>
    private void Automix(Track cur, Track? next, string nextPath, CinecoreAudioSettings s)
    {
        _ = AutomixAnalyzer.PrepareAsync(cur.Path);
        _ = AutomixAnalyzer.PrepareAsync(nextPath);
        var pa = AutomixAnalyzer.TryGet(cur.Path);
        var pb = AutomixAnalyzer.TryGet(nextPath);
        double duration = cur.DurationFrames / (double)Rate, pos = (cur.StartFrame + cur.MediaFrame) / (double)Rate;
        if (duration <= 0) return;
        double endAt = pa != null && s.AutomixTrimSilence ? Math.Min(duration, pa.End) : duration;
        // Stesso album, brani di seguito: gapless, a meno che il brano non finisca sfumando o in silenzio.
        if (next != null && SameAlbumInOrder(cur, next) && !(s.AutomixWithinAlbum && pa is { } e && (e.Duration - e.End > 1.0 || e.End - e.OutroStart >= 4))) return;

        string key = cur.Path + "\n" + nextPath;
        if (pa != null && pb != null && _planFor != key && endAt - pos < 75)
        {
            _planFor = key;
            _plan = AutomixPlanner.Plan(pa, pb, endAt, pos, AutomixPreferences.From(s), cur.Path, nextPath);
            if (_plan != null)
            {
                Console.Error.WriteLine($"[musica] Automix: {_plan.Kind} ({_plan.Style}) da {_plan.Start:0.0} s per {_plan.Fade:0.0} s, entra da {_plan.S0:0.0} s, tempo {_plan.Ratio:0.000}x");
                VerifyPlanPhase(_plan, cur.Path, nextPath);
            }
        }
        var plan = _planFor == key ? _plan : null;
        if (plan != null && pos > plan.Start + 3) { _plan = null; plan = null; }
        double entry = plan?.S0 ?? (s.AutomixTrimSilence && pb is { LeadIn: > 0.2 } ? pb.LeadIn : 0);
        double fade = plan?.Fade ?? (pa != null ? Math.Clamp(pa.FadeSeconds + 1, 1.5, 12) : 2);
        double start = plan?.Start ?? endAt - fade;
        if (pos < start - 20) return; // il successivo si prepara 20 s prima
        if (CdBusy(cur, nextPath)) return;
        // Il brano successivo deve partire dal punto d'ingresso scelto.
        if (next == null || next.Path != nextPath || next.FramesOut > 0 || Math.Abs(next.StartFrame - (long)(entry * Rate)) > Rate / 20)
        {
            var prepared = StartTrack(nextPath, entry);
            lock (_gate)
            {
                if (!ReferenceEquals(_current, cur) || _mix != null || s.CrossfadeSeconds >= 0 || _nextSource != nextPath) { DisposeTrack(prepared); return; }
                if (_next != null && !ReferenceEquals(_next, prepared)) DisposeTrack(_next);
                _next = prepared;
            }
            return;
        }
        if (duration < fade * 2 + 8 && plan == null) return;
        TransitionShape shape;
        double fadeIn, gainDb = 0;
        string kind;
        if (plan != null)
        {
            double swapAfter = double.IsNaN(plan.Swap) ? fade / 2 : plan.Swap - plan.Start;
            double beat = Math.Clamp(plan.Bpm > 0 ? 60 / plan.Bpm : 0.5, 0.25, 1.0);
            shape = new TransitionShape(plan.Style, Math.Clamp(swapAfter, 0.5, Math.Max(0.5, fade - 0.5)), Math.Clamp(beat, 0.25, 0.8))
            {
                TempoRatio = double.IsNaN(plan.SyncT0) || plan.Style is TransitionStyle.EchoOut or TransitionStyle.ReverbOut or TransitionStyle.PhraseCut ? 0 : plan.Ratio,
                AllowTempoSync = s.AutomixTempoSync, TempoRelation = plan.TempoRelation, TempoReturnSeconds = plan.TempoReturnSeconds,
                EchoSeconds = beat / 2, IncomingAtSwap = plan.IncomingAtSwap, Curves = plan.Curves,
                CutAfterSeconds = double.IsNaN(plan.Cut) ? 0 : Math.Max(0, plan.Cut - plan.Start),
            };
            fadeIn = plan.FadeIn; gainDb = plan.GainDb; kind = "Automix " + plan.Kind;
        }
        else
        {
            // Senza battute in comune: un brano ritmato se ne va con il filtro che sale, uno lento con la dissolvenza.
            var style = s.AutomixEffects && fade >= 4 && pa is { } e2 && e2.Tail.Reliable && e2.TailConfidence >= 2 && e2.End - e2.OutroStart < 4 ? TransitionStyle.FilterOut : TransitionStyle.Plain;
            shape = new TransitionShape(style);
            fadeIn = Math.Clamp(fade * .4, 1.5, 4);
            kind = "Automix semplice";
        }
        long startFrame = (long)(start * Rate) - cur.StartFrame;
        // Time-stretch creato qui, fuori dal callback dell'audio (Rubber Band alloca e prepara le sue finestre).
        ITimeStretch? stretch = null;
        if (plan != null && !double.IsNaN(plan.SyncT0) && !shape.IsCut && shape.AllowTempoSync && shape.TempoRatio > 0 && Math.Abs(shape.TempoRatio - 1) >= 0.002)
        {
            try { stretch = TimeStretchFactory.Create(Rate, Channels, shape.TempoRatio); }
            catch (Exception ex) { Console.Error.WriteLine("[musica] Automix: time-stretch non disponibile: " + ex.Message); }
        }
        bool used = false;
        lock (_gate)
            if (_mix == null && s.CrossfadeSeconds < 0 && ReferenceEquals(_current, cur) && ReferenceEquals(_next, next) &&
                _nextSource == nextPath && next.Available > 0)
            {
                _mix = NewMix(cur, next, Math.Max((long)cur.MediaFrame, startFrame), Math.Min(fade, Math.Max(0.3, duration - start)), fadeIn, shape, gainDb, kind,
                    plan?.SyncT0 ?? double.NaN, plan?.S0 ?? 0, stretch);
                used = true;
            }
        if (!used) (stretch as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Mix manuale (TryStartManualMix di Windows): il brano nuovo entra dal suo secondo InAt quando quello in corso arriva
    /// a OutAt, per Fade secondi, con lo stile scelto nell'editor (volume, EQ, filtro, effetto). False se il punto e'
    /// gia' passato (es. dopo un salto): si torna al comportamento normale.
    /// </summary>
    private bool ManualMix(Track cur, Track? next, string nextPath, ManualMix mix, CinecoreAudioSettings s)
    {
        double duration = cur.DurationFrames / (double)Rate, pos = (cur.StartFrame + cur.MediaFrame) / (double)Rate;
        if (duration <= 0 || mix.OutAt >= duration) return false;
        if (pos > mix.OutAt + 2) return false;
        if (pos < mix.OutAt - 20) return true; // in attesa del punto scelto: niente automix nel frattempo
        if (CdBusy(cur, nextPath)) return true;
        if (next == null || next.Path != nextPath || next.FramesOut > 0 || Math.Abs(next.StartFrame - (long)(mix.InAt * Rate)) > Rate / 20)
        {
            var prepared = StartTrack(nextPath, mix.InAt);
            lock (_gate)
            {
                if (!ReferenceEquals(_current, cur) || _mix != null || _nextSource != nextPath) { DisposeTrack(prepared); return true; }
                if (_next != null && !ReferenceEquals(_next, prepared)) DisposeTrack(_next);
                _next = prepared;
            }
            return true;
        }
        if (pos < mix.OutAt - 0.35) return true;
        double fade = Math.Max(0.5, mix.Fade), fadeIn = Math.Clamp(fade * .5, 0.5, 6);
        var shape = new TransitionShape(TransitionStyle.Plain);
        double ratio = 1;
        var pa = AutomixAnalyzer.TryGet(cur.Path);
        var pb = AutomixAnalyzer.TryGet(nextPath);
        if (s.AutomixTempoSync && pa is { Tail.Reliable: true } && pb is { Head.Reliable: true } && Math.Abs(pa.Tail.Bpm / pb.Head.Bpm - 1) <= 0.06)
            ratio = pa.Tail.Bpm / pb.Head.Bpm;
        // Velocita' del brano che finisce (scelta nel mix manuale): dall'inizio del passaggio. Il brano che entra in Auto si
        // aggancia al suo tempo effettivo; scelto a mano resta quello.
        double outSpeed = mix.OutgoingSpeed is double o && double.IsFinite(o) ? Math.Clamp(o, .5, 1.1) : 1;
        if (Math.Abs(outSpeed - 1) < .002) outSpeed = 1;
        double? speed = mix.IncomingSpeed;
        if (speed is double requested && double.IsFinite(requested)) ratio = Math.Clamp(requested, .5, 1.1);
        if (mix.NoTransition) { fade = 0.08; fadeIn = 0.05; ratio = 1; outSpeed = 1; }
        else if (mix.HasStyle)
        {
            int bars = 8; double beat = 0.5;
            if (pa is { Tail.Reliable: true } styled && styled.Tail.Period > 0)
            { bars = Math.Clamp((int)Math.Round(fade / (styled.Tail.Period * 4)), 1, 64); beat = styled.Tail.Period; }
            double beatIn = pb is { Head.Reliable: true } incoming && incoming.Head.Period > 0 ? incoming.Head.Period : beat;
            var curves = TransitionCurves.Compose(mix.Volume, Math.Max(0, mix.Eq), Math.Max(0, mix.Filter), bars)
                .ApplyFx(Math.Clamp(mix.Fx, 0, TransitionCurves.FxCount - 1), bars, beat, beatIn)
                .WithLanes(mix.Lanes, beat, beatIn);
            shape = new TransitionShape(TransitionStyle.Curves) { Curves = curves };
            fadeIn = fade;
        }
        // TempoRelation: quanto avanza il brano nuovo per ogni secondo del vecchio (entrambi possono essere stirati).
        // Con il tempo del brano che esce cambiato, anche quello che entra va adattato (lo segue, vedi SteerMix).
        bool outgoingChanges = mix.OutgoingSpeed is double os && double.IsFinite(os) && Math.Abs(os - 1) >= 0.002 && !mix.NoTransition;
        shape = shape with { TempoRatio = ratio, TempoRelation = ratio / outSpeed, AllowTempoSync = Math.Abs(ratio - 1) >= 0.002 || outgoingChanges };
        ITimeStretch? stretch = null;
        if (shape.AllowTempoSync)
        {
            try { stretch = TimeStretchFactory.Create(Rate, Channels, ratio); }
            catch (Exception ex) { Console.Error.WriteLine("[musica] mix manuale: time-stretch non disponibile: " + ex.Message); }
        }
        long startFrame = (long)(mix.OutAt * Rate) - cur.StartFrame;
        bool used = false;
        lock (_gate)
            if (_mix == null && ReferenceEquals(_current, cur) && ReferenceEquals(_next, next) && _nextSource == nextPath &&
                (next.Available >= Rate * Channels / 2 || next.Eof && next.Available > 0))
            {
                _mix = NewMix(cur, next, Math.Max((long)cur.MediaFrame, startFrame), Math.Min(fade, Math.Max(0.08, duration - mix.OutAt)), fadeIn, shape, 0, "mix manuale",
                    // Anche con il tempo scelto a mano il brano nuovo parte agganciato al punto scelto (sulla battuta) e lo
                    // tiene: prima con una velocita' manuale non c'era aggancio e le casse si allontanavano subito.
                    stretch != null ? mix.OutAt : double.NaN, mix.InAt, stretch);
                _mix.OutSpeed = outSpeed;
                used = true;
            }
        if (!used) (stretch as IDisposable)?.Dispose();
        else Console.Error.WriteLine($"[musica] mix manuale a {mix.OutAt:0.00} s, il successivo entra da {mix.InAt:0.0} s per {fade:0.0} s");
        return true;
    }

    /// <summary>
    /// Passaggio a tempo: si controlla sul segnale che le casse dei due brani cadano insieme (la griglia stimata puo'
    /// sbagliare di mezzo battito) e, se serve, si sposta l'ingresso del brano nuovo (VerifyPlanPhase di Windows).
    /// </summary>
    private void VerifyPlanPhase(AutomixPlanner.MixPlan plan, string from, string to)
    {
        if (plan.Bpm <= 0 || double.IsNaN(plan.SyncT0) || Math.Abs(plan.Ratio - 1) > 0.08) return;
        if (plan.Style is TransitionStyle.Plain or TransitionStyle.EchoOut or TransitionStyle.ReverbOut or TransitionStyle.PhraseCut) return;
        double period = 60 / plan.Bpm;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                double? lag = AutomixPhaseAligner.MeasureLag(from, to, plan.SyncT0, plan.S0, period, plan.Ratio, Math.Clamp(plan.Fade + 4, 8, 14));
                if (lag is not double shift || Math.Abs(shift) < 0.025) return;
                if (!ReferenceEquals(_plan, plan)) return;
                _plan = plan with { S0 = Math.Max(0, plan.S0 + shift) };
                Console.Error.WriteLine($"[musica] Automix: fase corretta, il brano nuovo entra {shift * 1000:+0;-0} ms ({plan.S0:0.00} s -> {_plan.S0:0.00} s)");
            }
            catch (Exception ex) { Console.Error.WriteLine("[musica] Automix: verifica della fase: " + ex.Message); }
        });
    }

    private void ApplySettings()
    {
        try { _dsp.Apply(CinecoreAudioSettings.Current.ToDsp()); } catch { }
        ResetAnalysis();
        var s = CinecoreAudioSettings.Current;
        lock (_gate)
        {
            if (_previousCrossfade != s.CrossfadeSeconds)
            {
                _previousCrossfade = s.CrossfadeSeconds;
                if (_mix is { Started: false } mix && mix.Kind != "mix manuale") DiscardPendingMix();
            }
        }
        string mode = (s.Exclusive ? "E" : "") + (s.BitPerfect ? "B" : "");
        if (mode == _directModeKey) return;
        _directModeKey = mode;
        // Esclusivo o bit perfect cambiati: il brano riparte dallo stesso punto alla frequenza giusta.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                string? path; double pos; bool playing;
                lock (_gate) { path = (Heard() ?? _current)?.Path; playing = _playing; }
                if (!DirectMode) lock (_gate) ReleaseDevice();
                if (path == null || _fallback != null) return;
                pos = Position;
                Open(path, pos);
                if (!playing) Pause();
            }
            catch (Exception ex) { Console.Error.WriteLine("[musica] cambio esclusivo/bit perfect: " + ex.Message); }
        });
    }

    public void ManualMixChanged(string from, string to)
    {
        lock (_gate)
            if (_mix is { Started: false } mix && mix.A.Path == from && mix.B.Path == to) DiscardPendingMix();
    }

    private void DiscardPendingMix()
    {
        _mix = null;
        DisposeTrack(_next);
        _next = null;
    }

    // ------------------------------------------------------------------ uscita esclusiva / bit perfect
    private string _directModeKey = "";
    private uint _exDevice;
    private double _exOriginalRate;
    private bool _exHog, _rateSwitching;
    // Bit perfect: bit dei campioni del brano, bit del formato fisico impostato, formato di prima da rimettere.
    private int _wantBits, _deviceBits;
    private MacAudioDevices.PhysicalFormat? _exOriginalFormat;
    private static bool DirectMode => CinecoreAudioSettings.Current is { Enabled: true } s && (s.Exclusive || s.BitPerfect);

    /// <summary>Frequenza del primo flusso audio del file (ffprobe), 0 se non si legge; i bit dei campioni in _wantBits.</summary>
    private int ProbeRate(string path)
    {
        try
        {
            if (MacTools.Ffprobe is not { } probe || path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return 0;
            var (exit, stdout, _) = ProcessLauncher.Run(probe, new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate,bits_per_raw_sample,sample_fmt", "-of", "json", path }, false, 5000, CancellationToken.None);
            if (exit != 0 || stdout.Length == 0) return 0;
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) return 0;
            var st = streams[0];
            _wantBits = SampleBits(st);
            return st.TryGetProperty("sample_rate", out var sr) && int.TryParse(sr.GetString(), out int rate) ? rate : 0;
        }
        catch { return 0; }
    }

    /// <summary>Bit dei campioni del file: quelli veri se dichiarati (FLAC/ALAC a 24 in contenitori a 32), altrimenti dal
    /// formato dei campioni; i formati compressi con perdita (decodificati in float) contano 24.</summary>
    private static int SampleBits(JsonElement stream)
    {
        if (stream.TryGetProperty("bits_per_raw_sample", out var raw) && int.TryParse(raw.GetString(), out int bits) && bits is > 0 and <= 32) return bits;
        string fmt = stream.TryGetProperty("sample_fmt", out var f) ? f.GetString() ?? "" : "";
        return fmt.StartsWith("s16") ? 16 : fmt.StartsWith("u8") ? 16 : fmt.StartsWith("s32") ? 32 : 24;
    }

    /// <summary>
    /// Esclusivo / bit perfect: la frequenza del file se l'uscita la accetta; altrimenti un suo multiplo intero (la
    /// conversione e' la piu' pulita possibile, ma non e' piu' bit perfect), infine 48 kHz. Senza: sempre 48 kHz.
    /// </summary>
    private int ChooseRate(string path, int knownSource = 0)
    {
        if (!DirectMode) return BaseRate;
        int source = knownSource > 0 ? knownSource : ProbeRate(path);
        if (source <= 0) return Rate;
        if (NullOutput) return source;
        var ranges = MacAudioDevices.AvailableRates(MacAudioDevices.DeviceId(MacAudioDevices.ConfiguredOutputUid));
        bool Accepts(double r) => ranges.Count == 0 || ranges.Exists(x => r >= x.Min - 0.5 && r <= x.Max + 0.5);
        if (Accepts(source)) return source;
        foreach (int m in new[] { 2, 4 }) if (Accepts(source * m)) return source * m;
        return Accepts(BaseRate) ? BaseRate : (int)System.Linq.Enumerable.Max(ranges, x => x.Max);
    }

    /// <summary>Sotto il lucchetto, a coda ferma: AudioQueue e DSP alla frequenza nuova, uscita configurata.</summary>
    private void ApplyRate(int rate)
    {
        if (rate != Rate)
        {
            if (_queue != IntPtr.Zero && !NullOutput) { AudioQueueStop(_queue, 1); AudioQueueDispose(_queue, 1); _queue = IntPtr.Zero; }
            _queueRunning = false;
            Rate = rate;
            var dsp = new AudioDspChain(Channels, Rate);
            try { dsp.Apply(CinecoreAudioSettings.Current.ToDsp()); } catch { }
            dsp.Volume = _muted ? 0 : _volume;
            if (_dsp.AnalysisOut != null) dsp.AnalysisOut = new float[BufferFrames * Channels];
            _dsp = dsp;
            OutputLog($"frequenza dell'uscita: {rate} Hz");
        }
        if (DirectMode) ConfigureDevice(); else ReleaseDevice();
    }

    /// <summary>Dispositivo alla frequenza del brano e, con Esclusivo, riservato all'app (hog mode).</summary>
    private void ConfigureDevice()
    {
        if (NullOutput) return;
        uint device = MacAudioDevices.DeviceId(MacAudioDevices.ConfiguredOutputUid);
        if (device == 0) return;
        if (device != _exDevice) { ReleaseDevice(); _exDevice = device; _exOriginalRate = MacAudioDevices.NominalRate(device); }
        if (!MacAudioDevices.SetNominalRate(device, Rate)) OutputLog($"il dispositivo non accetta {Rate} Hz");
        if (CinecoreAudioSettings.Current.BitPerfect && _wantBits > 0)
        {
            // Formato fisico intero con almeno i bit del brano: altrimenti CoreAudio tronca (es. DAC lasciato a 16 bit).
            var current = MacAudioDevices.CurrentPhysicalFormat(device);
            if (MacAudioDevices.ChoosePhysicalFormat(device, Rate, _wantBits) is { } wanted)
            {
                if (current is not { } now || now.Flags != wanted.Flags || now.Bits != wanted.Bits || now.Channels != wanted.Channels || Math.Abs(now.Rate - wanted.Rate) > .5)
                {
                    _exOriginalFormat ??= current;
                    bool ok = MacAudioDevices.SetPhysicalFormat(device, wanted);
                    OutputLog(ok ? $"formato dell'uscita: {wanted} (brano a {_wantBits} bit)" : $"il dispositivo non accetta {wanted}");
                }
                _deviceBits = wanted.IsIntegerPcm ? (int)wanted.Bits : 24;
            }
            else
            {
                _deviceBits = current is { IsIntegerPcm: true } c ? (int)c.Bits : current is { Bits: 32 } ? 24 : 0;
                OutputLog($"nessun formato intero a {_wantBits} bit e {Rate} Hz: resta {current?.ToString() ?? "quello di prima"}");
            }
        }
        bool wantHog = CinecoreAudioSettings.Current.Exclusive || CinecoreAudioSettings.Current.BitPerfect;
        if (wantHog && !_exHog)
        {
            _exHog = MacAudioDevices.SetHog(device, true);
            OutputLog(_exHog ? "uscita esclusiva presa" : "uscita esclusiva non disponibile (la usa un altro programma)");
        }
        else if (!wantHog && _exHog) { MacAudioDevices.SetHog(device, false); _exHog = false; }
    }

    /// <summary>Uscita libera e alla frequenza di prima (fine della musica, esclusivo spento).</summary>
    private void ReleaseDevice()
    {
        if (_exDevice == 0 || NullOutput) return;
        if (_exHog) { MacAudioDevices.SetHog(_exDevice, false); _exHog = false; OutputLog("uscita esclusiva lasciata"); }
        if (_exOriginalRate > 0) MacAudioDevices.SetNominalRate(_exDevice, _exOriginalRate);
        if (_exOriginalFormat is { } original) { MacAudioDevices.SetPhysicalFormat(_exDevice, original); OutputLog($"formato dell'uscita rimesso: {original}"); }
        _exDevice = 0; _exOriginalRate = 0; _exOriginalFormat = null; _deviceBits = 0;
    }

    /// <summary>Il brano che si sente ha un'altra frequenza: si riparte da li' alla sua frequenza (una breve pausa).</summary>
    private void CheckRateSwitch(Track cur)
    {
        if (_rateSwitching || cur.SourceRate <= 0 || cur.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        var heard = Heard() ?? cur;
        if (!ReferenceEquals(heard, cur) || heard.SourceRate <= 0) return;
        int wanted = ChooseRate(heard.Path, heard.SourceRate);
        // Bit perfect: anche un brano con piu' bit del formato in uso (16 → 24 alla stessa frequenza) riparte.
        bool moreBits = CinecoreAudioSettings.Current.BitPerfect && heard.SourceBits > _deviceBits && _deviceBits > 0;
        if (wanted == Rate && !moreBits) return;
        if (moreBits) _wantBits = heard.SourceBits;
        _rateSwitching = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                double pos = Position;
                Console.Error.WriteLine($"[musica] {Path.GetFileName(heard.Path)} a {heard.SourceRate} Hz: uscita da {Rate} a {wanted} Hz");
                Open(heard.Path, pos);
            }
            catch (Exception ex) { Console.Error.WriteLine("[musica] cambio di frequenza: " + ex.Message); }
            finally { _rateSwitching = false; }
        });
    }

    // ------------------------------------------------------------------ interfaccia (come MacAudioPlayer)
    /// <summary>Brano che si sente adesso (cambia da solo quando parte il successivo senza pausa).</summary>
    public string? Source { get { if (_fallback != null) return _fallback.Source; lock (_gate) return Heard()?.Path ?? _current?.Path; } }

    // Riserva: il lettore di sistema (AVPlayer) se CoreAudio non parte o se il motore e' spento nelle Impostazioni.
    private IMusicFallback? _fallback;
    private bool _queueFailed;
    private bool UseFallback => _queueFailed || !CinecoreAudioSettings.Current.Enabled;

    /// <summary>Il brano che segue nella coda: si prepara in anticipo per il gapless e la dissolvenza.</summary>
    // Il brano dopo si prepara in anticipo nella catena PCM (senza pause): uno da mandare in bitstream no, si apre
    // a parte quando tocca a lui.
    private string? _nextSource, _nextRequested;
    public string? NextSource
    {
        get => _nextRequested;
        set { if (value == _nextRequested) return; _nextRequested = value; _nextSource = value != null && MpvMusicPlayer.BitstreamCodecs(value) != null ? null : value; }
    }

    public bool Failed { get { if (_fallback != null) return _fallback.Failed; lock (_gate) return _current is { Failed: true }; } }

    public void Open(string pathOrUrl, double startSeconds = 0)
    {
        startSeconds = double.IsFinite(startSeconds) ? Math.Max(0, startSeconds) : 0;
        // Brano Dolby / DTS / Atmos con l'uscita in bitstream: al ricevitore originale, come su Windows.
        if (MpvMusicPlayer.BitstreamCodecs(pathOrUrl) is { } codecs) { OpenBitstream(pathOrUrl, startSeconds, codecs); return; }
        if (UseFallback) { OpenFallback(pathOrUrl, startSeconds); return; }
        _fallback?.Dispose(); _fallback = null;
        int rate = ChooseRate(pathOrUrl);
        lock (_gate)
        {
            StopQueue();
            DisposeTrack(_current); DisposeTrack(_next);
            _next = null; _mix = null; _tailShaper = null;
            ApplyRate(rate);
            ResetAnalysis();
            _current = StartTrack(pathOrUrl, startSeconds);
            _playing = true;
            StartQueue();
        }
        if (_queueFailed) OpenFallback(pathOrUrl, startSeconds);
    }

    private void OpenFallback(string pathOrUrl, double startSeconds)
    {
        lock (_gate) { StopQueue(); DisposeTrack(_current); DisposeTrack(_next); _current = _next = null; }
        if (_fallback is not MacAudioPlayer) { _fallback?.Dispose(); _fallback = new MacAudioPlayer(); }
        _fallback.Volume = _volume; _fallback.Muted = _muted;
        _fallback.Open(pathOrUrl);
        if (startSeconds > 0) _fallback.Seek(startSeconds);
    }

    private void OpenBitstream(string pathOrUrl, double startSeconds, string codecs)
    {
        lock (_gate) { StopQueue(); DisposeTrack(_current); DisposeTrack(_next); _current = _next = null; }
        if (_fallback is not MpvMusicPlayer player) { _fallback?.Dispose(); _fallback = player = new MpvMusicPlayer(); }
        player.Volume = _volume; player.Muted = _muted;
        player.Configure(codecs);
        player.Open(pathOrUrl);
        if (startSeconds > 0) player.Seek(startSeconds);
    }

    /// <summary>Il brano in uscita e' in bitstream (al ricevitore originale).</summary>
    public bool PlayingBitstream => _fallback is MpvMusicPlayer;

    public void Play()
    {
        if (_fallback != null) { _fallback.Play(); return; }
        lock (_gate)
        {
            if (_current == null) return;
            _playing = true;
            if (DirectMode) ConfigureDevice();
            if (_queue != IntPtr.Zero) QStart();
            else StartQueue();
        }
    }

    public void Pause()
    {
        if (_fallback != null) { _fallback.Pause(); return; }
        lock (_gate)
        {
            _playing = false;
            if (_queue != IntPtr.Zero && _queueRunning) QPause();
            // In pausa l'uscita torna libera (un film o un'altra app possono suonare).
            if (_exHog) { MacAudioDevices.SetHog(_exDevice, false); _exHog = false; }
        }
    }

    public bool IsPlaying { get { if (_fallback != null) return _fallback.IsPlaying; lock (_gate) return _playing && _current != null && !(_endOutFrame >= 0 && Played() >= _endOutFrame); } }

    public double Position
    {
        get
        {
            if (_fallback != null) return _fallback.Position;
            lock (_gate)
            {
                long played = Played();
                for (int i = _marks.Count - 1; i >= 0; i--)
                    if (_marks[i].OutFrame <= played)
                    {
                        long end = _endOutFrame >= 0 ? Math.Min(played, _endOutFrame) : played;
                        double pos = (_marks[i].Track.StartFrame + _marks[i].TrackFrame + Math.Max(0, end - _marks[i].OutFrame) * _marks[i].Speed) / (double)Rate;
                        return _marks[i].Track.DurationFrames > 0 ? Math.Min(pos, _marks[i].Track.DurationFrames / (double)Rate) : pos;
                    }
                return (_current?.StartFrame ?? 0) / (double)Rate;
            }
        }
    }

    public double Duration { get { if (_fallback != null) return _fallback.Duration; lock (_gate) { var t = Heard() ?? _current; return t is { DurationFrames: > 0 } ? t.DurationFrames / (double)Rate : 0; } } }

    public float Volume { get => _volume; set { _volume = Math.Clamp(value, 0f, 1f); _dsp.Volume = _muted ? 0 : _volume; if (_fallback != null) _fallback.Volume = _volume; } }
    public bool Muted { get => _muted; set { _muted = value; _dsp.Volume = _muted ? 0 : _volume; if (_fallback != null) _fallback.Muted = _muted; } }

    public void Seek(double seconds)
    {
        if (_fallback != null) { _fallback.Seek(seconds); return; }
        lock (_gate)
        {
            if (_current == null) return;
            var heard = Heard() ?? _current;
            string path = heard.Path;
            bool wasPlaying = _playing;
            StopQueue();
            DisposeTrack(_current); if (!ReferenceEquals(_next, heard)) DisposeTrack(_next);
            _next = null; _mix = null; _tailShaper = null;
            _current = StartTrack(path, Math.Max(0, seconds));
            ResetAnalysis();
            _playing = wasPlaying;
            StartQueue();
            if (!wasPlaying && _queue != IntPtr.Zero) QPause();
        }
    }

    public void Stop()
    {
        _fallback?.Stop();
        lock (_gate)
        {
            StopQueue();
            DisposeTrack(_current); DisposeTrack(_next);
            _current = _next = null;
            _playing = false;
            ReleaseDevice();
        }
    }

    /// <summary>
    /// Uscita cambiata (menu Audio › Uscita audio, o un altro predefinito del Mac): l'AudioQueue sceglie il dispositivo
    /// solo quando nasce, quindi la si ricrea; il brano prosegue dallo stesso punto (si perdono ~0,2 s gia' in coda).
    /// </summary>
    public void OutputChanged(string? selectedUid = null)
    {
        if (NullOutput) return;
        string uid = selectedUid ?? MacAudioDevices.ConfiguredOutputUid;
        if (_fallback != null) { _fallback.SetOutput(uid); return; }
        lock (_gate)
        {
            if (_queue == IntPtr.Zero || _current == null) return;
            string path = (Heard() ?? _current).Path;
            double seconds = Position;
            AudioQueueStop(_queue, 1);
            _queueRunning = false;
            AudioQueueDispose(_queue, 1);
            _queue = IntPtr.Zero;
            DisposeTrack(_current); DisposeTrack(_next);
            _next = null; _mix = null; _tailShaper = null;
            _current = StartTrack(path, seconds);
            StartQueue(uid);
        }
        OutputLog("uscita cambiata: " + (uid.Length > 0 ? uid : "predefinito del Mac") + " -> coda su " + QueueDevice());
    }

    public void Dispose()
    {
        CinecoreAudioSettings.Changed -= ApplySettings;
        _control.Dispose();
        Stop();
        lock (_gate) if (_queue != IntPtr.Zero) { if (!NullOutput) AudioQueueDispose(_queue, 1); _queue = IntPtr.Zero; }
        if (_self.IsAllocated) _self.Free();
    }

    // ------------------------------------------------------------------ decodifica (FFmpeg)
    private Track StartTrack(string path, double startSeconds)
    {
        var track = new Track { Path = path, StartFrame = (long)(startSeconds * Rate), Rate = Rate };
        if (Rate != BaseRate) track.Ring = new float[Rate * Channels * 6];
        // CD audio: un solo lettore alla volta sul disco. Con due ffmpeg insieme (fine del brano e inizio del successivo)
        // il lettore salta fra due punti del disco e il brano nuovo arrivava dopo ~20 s. Il brano si legge con 90 s di
        // anticipo, cosi' il disco e' libero ben prima che serva il successivo (che parte solo dopo, vedi CdBusy).
        if (MacAudioCd.IsOnAudioCd(path)) { track.Ring = new float[Rate * Channels * 90]; track.OnCd = true; }
        // Durata e ReplayGain arrivano da ffprobe in sottofondo: l'audio parte subito.
        ThreadPool.QueueUserWorkItem(_ => ReadInfo(track));
        string? ffmpeg = MacTools.Ffmpeg;
        if (ffmpeg == null) { track.Failed = true; track.Eof = true; return track; }
        string input = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? CinecorePlayer2025.Utilities.JellyfinClient.PlaybackUrl(path) : path;
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error" }) psi.ArgumentList.Add(a);
        if (startSeconds > 0) { psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(startSeconds.ToString("0.###", CultureInfo.InvariantCulture)); }
        foreach (var a in new[] { "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "2",
                     "-af", "aresample=" + Rate + ":filter_size=64:phase_shift=10:cutoff=0.97", "-f", "f32le", "pipe:1" })
            psi.ArgumentList.Add(a);
        try
        {
            var process = Process.Start(psi)!;
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Console.Error.WriteLine("[musica] ffmpeg: " + e.Data); };
            process.BeginErrorReadLine();
            track.Ffmpeg = process;
            track.Reader = new Thread(() => ReadLoop(track)) { IsBackground = true, Name = "musica ffmpeg", Priority = ThreadPriority.AboveNormal };
            track.Reader.Start();
        }
        catch (Exception ex) { Console.Error.WriteLine("[musica] ffmpeg non partito: " + ex.Message); track.Failed = true; track.Eof = true; }
        return track;
    }

    private static void ReadLoop(Track track)
    {
        var stream = track.Ffmpeg!.StandardOutput.BaseStream;
        var bytes = new byte[16384];
        int carry = 0;
        long total = 0;
        try
        {
            while (true)
            {
                int n = stream.Read(bytes, carry, bytes.Length - carry);
                if (n <= 0) break;
                n += carry;
                int samples = n / 4;
                carry = n - samples * 4;
                // Aspetta che ci sia posto nell'anello (l'uscita consuma).
                // Gli ultimi HistoryFrames gia' suonati restano nell'anello: servono ad avviare il time-stretch a meta' brano.
                while (track.Ring.Length - track.Available - HistoryFrames * Channels < samples)
                {
                    if (track.Failed) return;
                    Thread.Sleep(10);
                }
                var src = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, samples * 4));
                long w = Interlocked.Read(ref track.Written);
                for (int i = 0; i < samples; i++) track.Ring[(w + i) % track.Ring.Length] = src[i];
                Interlocked.Add(ref track.Written, samples);
                total += samples;
                if (carry > 0) Buffer.BlockCopy(bytes, samples * 4, bytes, 0, carry);
            }
        }
        catch { }
        if (total == 0 && !track.Failed) { track.Failed = true; Console.Error.WriteLine("[musica] nessun campione da " + Path.GetFileName(track.Path)); }
        track.Eof = true;
    }

    /// <summary>Durata e ReplayGain dai tag (ffprobe).</summary>
    private static void ReadInfo(Track track)
    {
        try
        {
            if (MacTools.Ffprobe is not { } probe) return;
            string input = track.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? CinecorePlayer2025.Utilities.JellyfinClient.PlaybackUrl(track.Path) : track.Path;
            var (exit, stdout, _) = ProcessLauncher.Run(probe, new[] { "-v", "error", "-show_entries", "format=duration:format_tags:stream_tags:stream=sample_rate,bits_per_raw_sample,sample_fmt", "-select_streams", "a:0", "-of", "json", input }, false, 8000, CancellationToken.None);
            if (exit != 0 || stdout.Length == 0) return;
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    track.DurationFrames = (long)(seconds * track.Rate);
                var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                void Collect(JsonElement e) { if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) tags[p.Name] = p.Value.GetString() ?? ""; }
                if (format.TryGetProperty("tags", out var ft)) Collect(ft);
                if (doc.RootElement.TryGetProperty("streams", out var streams))
                    foreach (var s in streams.EnumerateArray())
                    {
                        if (s.TryGetProperty("tags", out var st)) Collect(st);
                        if (track.SourceRate == 0 && s.TryGetProperty("sample_rate", out var sr) && int.TryParse(sr.GetString(), out int rate)) track.SourceRate = rate;
                        if (track.SourceBits == 0) track.SourceBits = SampleBits(s);
                    }
                track.Gain = ReplayGain(tags);
                if (tags.TryGetValue("album", out var album)) track.Album = album.Trim();
                if (tags.TryGetValue("track", out var number) && int.TryParse(number.Split('/')[0], out int n)) track.TrackNumber = n;
            }
        }
        catch { }
    }

    /// <summary>ReplayGain del brano o dell'album (Impostazioni › Cinecore Audio), con il preamp per i brani senza tag.</summary>
    private static double ReplayGain(Dictionary<string, string> tags)
    {
        var s = CinecoreAudioSettings.Current;
        if (s.BitPerfect || s.ReplayGain is not ("track" or "album")) return 1;
        static double? Db(string? text) => text != null && double.TryParse(text.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        tags.TryGetValue(s.ReplayGain == "album" ? "REPLAYGAIN_ALBUM_GAIN" : "REPLAYGAIN_TRACK_GAIN", out var wanted);
        tags.TryGetValue("REPLAYGAIN_TRACK_GAIN", out var trackGain);
        double? db = Db(wanted) ?? Db(trackGain);
        double gain = db ?? s.ReplayGainPreampDb;
        return Math.Pow(10, Math.Clamp(gain, -24, 12) / 20);
    }

    private static void DisposeTrack(Track? track)
    {
        if (track == null) return;
        track.Failed = track.Failed || !track.Eof;
        try { if (track.Ffmpeg is { HasExited: false } p) p.Kill(); } catch { }
        try { track.Ffmpeg?.Dispose(); } catch { }
        try { (track.Stretch as IDisposable)?.Dispose(); } catch { }
        track.Stretch = null;
    }

    // ------------------------------------------------------------------ uscita
    // Prove automatiche (CINECORE_QA_MUTE): nessun suono vero, un'uscita finta che consuma i buffer in tempo reale.
    private static readonly bool NullOutput = Environment.GetEnvironmentVariable("CINECORE_QA_MUTE") == "1";
    // Solo prove: l'uscita del motore (float stereo 48 kHz) scritta su file, per cercare vuoti e salti nei passaggi.
    private static readonly System.IO.FileStream? QaDump = Environment.GetEnvironmentVariable("CINECORE_QA_MUSIC_DUMP") is { Length: > 0 } dumpPath
        ? new System.IO.FileStream(dumpPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read, 1 << 16) : null;
    private static readonly bool QaTiming = Environment.GetEnvironmentVariable("CINECORE_QA_AUDIO_TIMING") == "1";
    private readonly Stopwatch _nullClock = new();
    private long _nullConsumed;
    private Thread? _nullThread;

    private void QStart()
    {
        _queueRunning = true;
        if (_returnedFrames > 0) _returnedAt = Stopwatch.GetTimestamp();
        if (!NullOutput) { AudioQueueStart(_queue, IntPtr.Zero); return; }
        _nullClock.Start();
    }

    private void QPause()
    {
        _returnedFrames = Played(); _returnedAt = 0;
        _queueRunning = false;
        if (!NullOutput) { AudioQueuePause(_queue); return; }
        _nullClock.Stop();
    }

    private void NullLoop()
    {
        int next = 0;
        while (_queue != IntPtr.Zero)
        {
            Thread.Sleep(5);
            long asked = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                double waited = (Stopwatch.GetTimestamp() - asked) * 1000.0 / Stopwatch.Frequency;
                if (QaTiming && waited > 5) Console.Error.WriteLine($"[qa-audio] attesa del lucchetto {waited:0.0} ms" + (_mix != null ? " (passaggio)" : ""));
                if (!_queueRunning) continue;
                long due = (long)(_nullClock.Elapsed.TotalSeconds * Rate);
                while (_nullConsumed + BufferFrames <= due)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    Fill(_buffers[next]); next = (next + 1) % BufferCount; _nullConsumed += BufferFrames;
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    if (QaTiming && ms > 4) Console.Error.WriteLine($"[qa-audio] riempimento {ms:0.0} ms" + (_mix != null ? " (passaggio)" : ""));
                }
            }
        }
    }

    private void StartQueue(string? selectedUid = null)
    {
        if (NullOutput && _queue == IntPtr.Zero)
        {
            _queue = (IntPtr)1;
            for (int i = 0; i < BufferCount; i++)
            {
                IntPtr b = Marshal.AllocHGlobal(sizeof(QueueBuffer));
                ((QueueBuffer*)b)->Data = Marshal.AllocHGlobal(BufferFrames * Channels * 4);
                ((QueueBuffer*)b)->Capacity = BufferFrames * Channels * 4;
                _buffers[i] = b;
            }
            _nullThread = new Thread(NullLoop) { IsBackground = true, Name = "musica uscita finta" };
            _nullThread.Start();
        }
        if (NullOutput) { _nullClock.Reset(); _nullConsumed = 0; }
        if (_queue == IntPtr.Zero)
        {
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            var format = new StreamFormat
            {
                SampleRate = Rate, FormatId = 0x6C70636D /* lpcm */, FormatFlags = 1 | 8 /* float, packed */,
                BytesPerPacket = 8, FramesPerPacket = 1, BytesPerFrame = 8, ChannelsPerFrame = Channels, BitsPerChannel = 32,
            };
            int r = AudioQueueNewOutput(&format, &OnBuffer, GCHandle.ToIntPtr(_self), IntPtr.Zero, IntPtr.Zero, 0, out _queue);
            if (r != 0) { Console.Error.WriteLine($"[musica] AudioQueue non creata ({r}): lettore di sistema"); _queue = IntPtr.Zero; _queueFailed = true; return; }
            string uid = selectedUid ?? MacAudioDevices.ConfiguredOutputUid;
            if (uid.Length > 0)
            {
                IntPtr cf = CFStringCreateWithCString(IntPtr.Zero, uid, 0x08000100);
                int d = AudioQueueSetProperty(_queue, CurrentDeviceProperty, &cf, (uint)IntPtr.Size);
                if (d != 0) OutputLog($"uscita {uid} non impostata (errore {d})");
                else OutputLog($"uscita impostata: {uid}");
                CFRelease(cf);
            }
            for (int i = 0; i < BufferCount; i++) AudioQueueAllocateBuffer(_queue, BufferFrames * Channels * 4, out _buffers[i]);
        }
        _marks.Clear(); _outFrames = 0; _endOutFrame = -1; _returnedFrames = 0; _returnedAt = 0;
        // Il primo giro di buffer si riempie subito (aspettando un attimo i primi campioni del brano).
        var until = DateTime.UtcNow.AddMilliseconds(600);
        while (_current is { Eof: false } c && c.Available < BufferFrames * Channels && DateTime.UtcNow < until) Thread.Sleep(5);
        for (int i = 0; i < BufferCount; i++) Fill(_buffers[i]);
        if (_playing) QStart();
    }

    private void StopQueue()
    {
        if (_queue == IntPtr.Zero) return;
        if (!NullOutput) AudioQueueStop(_queue, 1); else _nullClock.Stop();
        _queueRunning = false;
    }

    [UnmanagedCallersOnly]
    private static void OnBuffer(IntPtr user, IntPtr queue, IntPtr buffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not MacMusicEngine engine) return;
            // Attesa breve: l'interfaccia tiene il lucchetto per pochi microsecondi (posizione, stato). Durante uno stop
            // (apertura, ricerca) AudioQueueStop aspetta questo callback tenendo il lucchetto: dopo 20 ms si lascia andare
            // il buffer, che lo stop rimette comunque a disposizione.
            if (!Monitor.TryEnter(engine._gate, 20)) return;
            try
            {
                if (engine._queue == IntPtr.Zero || !engine._queueRunning) return;
                engine._returnedFrames += BufferFrames;           // il buffer tornato e' stato suonato
                engine._returnedAt = Stopwatch.GetTimestamp();
                engine.Fill(buffer);
            }
            finally { Monitor.Exit(engine._gate); }
        }
        catch { }
    }

    /// <summary>Fotogrammi gia' suonati dall'avvio della coda.</summary>
    private long Played()
    {
        if (_queue == IntPtr.Zero) return 0;
        if (NullOutput) return _lastPlayed = Math.Max(0, (long)(_nullClock.Elapsed.TotalSeconds * Rate) - BufferCount * BufferFrames);
        // Buffer tornati (= suonati) piu' il tempo dall'ultimo ritorno, al massimo un buffer: avanza liscio e si ferma in pausa.
        long extra = _queueRunning && _returnedAt > 0 ? (long)((Stopwatch.GetTimestamp() - _returnedAt) / (double)Stopwatch.Frequency * Rate) : 0;
        return _lastPlayed = _returnedFrames + Math.Clamp(extra, 0, BufferFrames);
    }
    private long _lastPlayed, _returnedFrames, _returnedAt;

    private Track? Heard()
    {
        long played = Played();
        for (int i = _marks.Count - 1; i >= 0; i--) if (_marks[i].OutFrame <= played) return _marks[i].Track;
        return null;
    }

    /// <summary>
    /// Passaggio fra due brani (dissolvenza o Automix): due deck nello stesso flusso, mescolati al campione come in
    /// MixInto di Windows. Le TransitionShaper fanno volumi, EQ a tre bande, filtri ed eco di ciascun deck.
    /// </summary>
    private sealed class Mix
    {
        public Track A = null!, B = null!;
        public long StartFrame;            // fotogramma di A (dal punto da cui A e' partito) dove comincia il passaggio
        public long Total, InTotal, Pos, HeardSwitch;
        public TransitionShaper Out = null!, In = null!;
        public TransitionCurves? Curves;
        public bool Plain, Started;
        public float GainB = 1;
        public double CurveNorm = 1, CurveNormStep;
        public string Kind = "";
        // A tempo: il brano nuovo deve trovarsi in SyncS0 + (posizione del vecchio - SyncT0) * MediaRatio (secondi).
        public double SyncT0 = double.NaN, SyncS0, MediaRatio = 1;
        /// <summary>Velocita' del brano che finisce durante il passaggio (mix manuale): si applica quando il passaggio parte.</summary>
        public double OutSpeed = 1;
        public long DropFrames, SilenceFrames, NextCheck;
        public bool Aligned;
    }
    private Mix? _mix;
    private TransitionShaper? _tailShaper; private long _tailPos, _tailTotal;
    private float[] _mixA = new float[BufferFrames * Channels], _mixB = new float[BufferFrames * Channels];

    private Mix NewMix(Track a, Track b, long startFrame, double seconds, double fadeInSeconds, TransitionShape shape, double gainDb, string kind,
        double syncT0 = double.NaN, double syncS0 = 0, ITimeStretch? stretch = null)
    {
        long total = Math.Max(1, (long)(seconds * Rate));
        long inTotal = shape.Style is TransitionStyle.BassSwap or TransitionStyle.DjBlend or TransitionStyle.Curves || fadeInSeconds <= 0
            ? total : Math.Max(1, (long)(Math.Min(fadeInSeconds, seconds) * Rate));
        long swap = (long)(Math.Clamp(shape.SwapAfterSeconds, 0.05, seconds) * Rate), ramp = (long)(Math.Max(0.05, shape.SwapRampSeconds) * Rate);
        var mix = new Mix
        {
            A = a, B = b, StartFrame = startFrame, Total = total, InTotal = inTotal, Kind = kind,
            Out = new TransitionShaper(shape, true, Rate, Channels, swap, ramp), In = new TransitionShaper(shape, false, Rate, Channels, swap, ramp),
            Curves = shape.Style == TransitionStyle.Curves ? shape.Curves ?? TransitionCurves.Preset(1) : null,
            Plain = shape.Style == TransitionStyle.Plain,
            GainB = (float)Math.Pow(10, Math.Clamp(gainDb, -8, 6) / 20),
            HeardSwitch = shape.Style == TransitionStyle.Plain ? total / 2 : Math.Clamp(swap, 1, total),
        };
        long cut = (long)(Math.Max(0, shape.CutAfterSeconds) * Rate);
        mix.Out.SetFrameTiming(swap, ramp, cut); mix.In.SetFrameTiming(swap, ramp, cut);
        mix.SyncT0 = syncT0; mix.SyncS0 = syncS0;
        double ratio = shape.TempoRatio > 0 ? shape.TempoRatio : 1;
        mix.MediaRatio = shape.TempoRelation > 0 ? shape.TempoRelation : ratio;
        if (stretch != null && b.Stretch == null)
        {
            b.Stretch = stretch; b.StretchBase = double.NaN; b.StretchNominal = ratio; b.StretchOut = 0; b.RampStart = -1;
            b.ReturnSeconds = Math.Clamp(shape.TempoReturnSeconds, 8, 32);
        }
        else if (stretch != null) (stretch as IDisposable)?.Dispose();
        return mix;
    }

    /// <summary>Riempie un buffer d'uscita: brano in corso, passaggio (dissolvenza, Automix) o gapless al successivo, DSP.</summary>
    private void Fill(IntPtr bufferRef)
    {
        var buffer = (QueueBuffer*)bufferRef;
        int frames = BufferFrames;
        var outSpan = new Span<float>((void*)buffer->Data, frames * Channels);
        outSpan.Clear();
        PrepareNext();
        int done = 0;
        while (done < frames && _current != null)
        {
            var cur = _current;
            var mix = _mix is { } m && ReferenceEquals(m.A, cur) && ReferenceEquals(m.B, _next) ? m : null;
            int want = frames - done;
            if (mix is { Started: false })
            {
                long until = (long)Math.Ceiling((mix.StartFrame - cur.MediaFrame) / Math.Max(0.5, cur.Speed));
                if (until <= 0)
                {
                    mix.Started = true;
                    if (Math.Abs(mix.OutSpeed - 1) >= .002 && cur.Stretch == null)
                    {
                        try
                        {
                            // Si parte dalla sua velocita' e si arriva a quella scelta con una curva dolce (al massimo 2 s, o
                            // meta' passaggio se e' piu' corto): niente salto di tempo all'inizio del mix.
                            // Avviato con gli ultimi campioni suonati al posto del silenzio iniziale: prima l'uscita
                            // ripartiva da zero (buco e click a pieno volume).
                            cur.Stretch = TimeStretchFactory.Create(Rate, Channels, 1, History(cur));
                            cur.StretchBase = double.NaN; cur.StretchNominal = mix.OutSpeed; cur.StretchOut = 0;
                            cur.RampStart = 0; cur.RampFrom = 1; cur.RampTo = mix.OutSpeed;
                            cur.ReturnSeconds = Math.Clamp(mix.Total / (double)Rate / 2, 0.3, 2);
                            Console.Error.WriteLine($"[musica] mix manuale: il brano che finisce a {mix.OutSpeed:0.000}x");
                        }
                        catch (Exception ex) { Console.Error.WriteLine("[musica] mix manuale: velocita' in uscita non disponibile: " + ex.Message); }
                    }
                    Console.Error.WriteLine($"[musica] passaggio {mix.Kind}: {mix.Total / (double)Rate:0.0} s" + (mix.B.Stretch != null ? $", tempo {mix.B.StretchNominal:0.000}x" : ""));
                    AlignMix(mix);
                }
                else want = (int)Math.Min(want, until);
            }
            if (mix is { Started: true })
            {
                want = (int)Math.Min(want, mix.Total - mix.Pos);
                int n = want * Channels;
                if (_mixA.Length < n) { _mixA = new float[n]; _mixB = new float[n]; }
                Array.Clear(_mixA, 0, n); Array.Clear(_mixB, 0, n);
                double aFrame = cur.MediaFrame, aSpeed = cur.Speed;
                Take(cur, _mixA.AsSpan(0, n), cur.Gain, 1);
                // Brano nuovo: prima gli scarti o l'attesa dell'allineamento, poi i campioni (eventualmente a tempo).
                if (mix.DropFrames > 0) mix.DropFrames -= Skip(mix.B, mix.DropFrames);
                double bFrame = mix.B.MediaFrame, bSpeed = mix.B.Speed;
                if (mix.DropFrames <= 0)
                {
                    int silent = (int)Math.Min(mix.SilenceFrames, want);
                    mix.SilenceFrames -= silent;
                    if (silent < want) Take(mix.B, _mixB.AsSpan(silent * Channels, (want - silent) * Channels), mix.B.Gain, 1);
                }
                mix.Out.Process(_mixA, n, mix.Pos, mix.Total);
                mix.In.Process(_mixB, n, mix.Pos, mix.InTotal);
                for (int f = 0; f < want; f++)
                {
                    double frame = mix.Pos + f;
                    float norm = 1;
                    if (mix.Plain)
                    {
                        double ga = Math.Cos(Math.Min(1, frame / mix.Total) * Math.PI / 2), gb = Math.Sin(Math.Min(1, frame / mix.InTotal) * Math.PI / 2);
                        norm = (float)(1 / Math.Max(1, Math.Sqrt(ga * ga + gb * gb)));
                    }
                    else if (mix.Curves is { } curves)
                    {
                        // Ogni 256 campioni: margine quando i due fader insieme superano la potenza di uno (max -3 dB).
                        const int interval = 256;
                        if ((mix.Pos + f) % interval == 0)
                        {
                            double span = Math.Min(interval, mix.Total - frame);
                            double pp = (frame + span) / mix.Total;
                            double va = curves.OutVolumeAt(pp), vb = curves.InVolumeAt(pp) * mix.GainB;
                            double target = Math.Clamp(1 / Math.Sqrt(Math.Max(1, va * va + vb * vb)), 0.70710678, 1);
                            mix.CurveNormStep = (target - mix.CurveNorm) / span;
                        }
                        norm = (float)mix.CurveNorm;
                        mix.CurveNorm += mix.CurveNormStep;
                    }
                    for (int c = 0; c < Channels; c++)
                    {
                        int i = f * Channels + c;
                        outSpan[(done + f) * Channels + c] = (_mixB[i] * mix.GainB + _mixA[i]) * norm;
                    }
                }
                // Si sente il brano nuovo dallo scambio (o da meta' dissolvenza): titolo, posizione e coda lo seguono.
                if (mix.Pos < mix.HeardSwitch && mix.Pos + want >= mix.HeardSwitch)
                {
                    long offset = mix.HeardSwitch - mix.Pos;
                    _marks.Add((_outFrames + done + offset, mix.B, bFrame + offset * bSpeed, bSpeed));
                }
                else if (_marks.Count == 0 || (aSpeed != 1 && mix.Pos < mix.HeardSwitch)) _marks.Add((_outFrames + done, cur, aFrame, aSpeed));
                else if (mix.Pos >= mix.HeardSwitch && mix.B.Stretch != null) _marks.Add((_outFrames + done, mix.B, bFrame, bSpeed));
                mix.Pos += want;
                SteerMix(mix);
                done += want;
                if (mix.Pos >= mix.Total)
                {
                    Console.Error.WriteLine($"[musica] passaggio finito ({mix.Kind})");
                    DisposeTrack(cur);
                    _current = mix.B; _next = null; _mix = null; ResetAnalysis();
                    // Finito il passaggio il brano nuovo torna al suo tempo (curva dolce, TempoReturnSeconds).
                    if (mix.B.Stretch is { } st) { mix.B.RampStart = mix.B.StretchOut; mix.B.RampFrom = st.Ratio; Console.Error.WriteLine($"[musica] Automix: ritorno al tempo del brano da {st.Ratio:0.000}x"); }
                    if (!mix.In.Done) { _tailShaper = mix.In; _tailPos = mix.Pos; _tailTotal = mix.InTotal; }
                }
                continue;
            }
            if (_marks.Count == 0 || !ReferenceEquals(_marks[^1].Track, cur) || cur.Stretch != null) _marks.Add((_outFrames + done, cur, cur.MediaFrame, cur.Speed));
            var slice = outSpan.Slice(done * Channels, want * Channels);
            int got = Take(cur, slice, cur.Gain, 1);
            // Filtri del brano nuovo non ancora chiusi a passaggio finito (salita piu' lunga): finiscono qui.
            if (_tailShaper is { } tail && got > 0)
            {
                int n = got * Channels;
                if (_mixA.Length < n) _mixA = new float[n];
                slice.Slice(0, n).CopyTo(_mixA);
                tail.Process(_mixA, n, _tailPos, _tailTotal);
                _mixA.AsSpan(0, n).CopyTo(slice);
                _tailPos += got;
                if (tail.Done) _tailShaper = null;
            }
            done += got;
            if (got < want)
            {
                if (!cur.Eof) break;             // il decodificatore e' in ritardo: silenzio, riprende al giro dopo
                // Fine del brano: si passa al successivo senza pausa.
                if (_next is { Failed: false } next)
                {
                    DisposeTrack(cur);
                    _current = next; _next = null; _mix = null;
                    _marks.Add((_outFrames + done, next, next.MediaFrame, next.Speed));
                    ResetAnalysis();
                    continue;
                }
                if (_endOutFrame < 0) _endOutFrame = _outFrames + done;
                break;
            }
        }
        if (CinecoreAudioSettings.Current.BitPerfect)
        {
            // Bit perfect: nessuna elaborazione; solo il volume, se non e' al massimo (allora non e' piu' bit perfect).
            if (_analysis && _dsp.AnalysisOut is { } feed && feed.Length >= frames * Channels) outSpan[..(frames * Channels)].CopyTo(feed);
            float v = _muted ? 0f : _volume;
            if (v < 0.99995f) for (int i = 0; i < frames * Channels; i++) outSpan[i] *= v;
        }
        else _dsp.Process(outSpan, frames);
        if (_analysis && !UseParallelAnalysis && _dsp.AnalysisOut is { } analysisOut && _playing) _analysisQueue.TryAdd(((float[])analysisOut.Clone(), _outFrames + frames));
        if (QaDump != null && _playing) { QaDump.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(outSpan[..(frames * Channels)])); QaDump.Flush(); }
        _outFrames += frames;
        if (_marks.Count > 64) _marks.RemoveRange(0, _marks.Count - 64);
        buffer->ByteSize = (uint)(frames * Channels * 4);
        if (!NullOutput) AudioQueueEnqueueBuffer(_queue, bufferRef, 0, IntPtr.Zero);
    }

    private const int HistoryFrames = 16384;

    /// <summary>Gli ultimi fotogrammi suonati del brano (interleaved), per avviare il time-stretch senza buco.</summary>
    private static float[] History(Track track)
    {
        long r = Interlocked.Read(ref track.Read);
        int frames = (int)Math.Min(HistoryFrames, r / Channels);
        var history = new float[frames * Channels];
        int len = track.Ring.Length;
        long from = r - history.Length;
        for (int i = 0; i < history.Length; i++) history[i] = track.Ring[(from + i) % len];
        return history;
    }

    /// <summary>Copia dall'anello del brano (con il guadagno), restituisce i fotogrammi presi.</summary>
    private static int Take(Track track, Span<float> dest, double gain, double extra)
    {
        if (track.Stretch != null) return TakeStretched(track, dest, gain * extra);
        long available = track.Available / Channels;
        int frames = (int)Math.Min(dest.Length / Channels, available);
        long r = Interlocked.Read(ref track.Read);
        float g = (float)(gain * extra);
        int samples = frames * Channels, len = track.Ring.Length;
        for (int i = 0; i < samples; i++) dest[i] = track.Ring[(r + i) % len] * g;
        Interlocked.Add(ref track.Read, samples);
        track.FramesOut += frames;
        return frames;
    }

    /// <summary>Brano a tempo adattato: Rubber Band (o il WSOLA interno) legge dall'anello, poi ritorno al tempo del brano.</summary>
    private static int TakeStretched(Track track, Span<float> dest, double gain)
    {
        var st = track.Stretch!;
        int frames = dest.Length / Channels;
        if (track.StretchBuffer.Length < dest.Length) track.StretchBuffer = new float[dest.Length];
        if (double.IsNaN(track.StretchBase)) track.StretchBase = track.FramesOut;
        st.InputComplete = track.Eof;
        track.StretchRead ??= (dst, offset, count) => ReadRaw(track, dst, offset, count);
        int got = st.Produce(track.StretchBuffer, 0, frames, track.StretchRead);
        float g = (float)gain;
        for (int i = 0, n = got * Channels; i < n; i++) dest[i] = track.StretchBuffer[i] * g;
        track.StretchOut += got;
        if (track.RampStart >= 0)
        {
            double t = Math.Clamp((track.StretchOut - track.RampStart) / (track.ReturnSeconds * track.Rate), 0, 1);
            t = t * t * (3 - 2 * t);
            st.Ratio = t >= 1 ? track.RampTo : track.RampFrom + (track.RampTo - track.RampFrom) * t;
        }
        return got;
    }

    /// <summary>Ingresso del time-stretch: campioni dall'anello senza guadagno (count in campioni).</summary>
    private static int ReadRaw(Track track, float[] dest, int offset, int count)
    {
        long available = track.Available / Channels;
        int frames = (int)Math.Min(count / Channels, available);
        long r = Interlocked.Read(ref track.Read);
        int samples = frames * Channels, len = track.Ring.Length;
        for (int i = 0; i < samples; i++) dest[offset + i] = track.Ring[(r + i) % len];
        Interlocked.Add(ref track.Read, samples);
        track.FramesOut += frames;
        return samples;
    }

    /// <summary>Scarta fino a <paramref name="frames"/> fotogrammi dall'anello (allineamento del passaggio).</summary>
    private static long Skip(Track track, long frames)
    {
        long n = Math.Min(frames, track.Available / Channels);
        if (n <= 0) return 0;
        Interlocked.Add(ref track.Read, n * Channels);
        track.FramesOut += n;
        return n;
    }

    /// <summary>All'inizio del passaggio a tempo: il "uno" del brano nuovo sul "uno" del vecchio, al campione (AlignMix di Windows).</summary>
    private static void AlignMix(Mix mix)
    {
        if (double.IsNaN(mix.SyncT0)) return;
        double aNow = (mix.A.StartFrame + mix.A.MediaFrame) / mix.A.Rate, bNow = (mix.B.StartFrame + mix.B.MediaFrame) / mix.A.Rate;
        double delta = mix.SyncS0 + (aNow - mix.SyncT0) * mix.MediaRatio - bNow;
        if (Math.Abs(delta) > 8) { Console.Error.WriteLine($"[musica] Automix: allineamento fuori di {delta:0.00} s, saltato"); return; }
        if (delta > 0) mix.DropFrames = (long)Math.Round(delta * mix.A.Rate);
        else mix.SilenceFrames = (long)Math.Round(-delta / Math.Max(0.5, mix.B.StretchNominal) * mix.A.Rate);
        Console.Error.WriteLine($"[musica] Automix: brani allineati ({(delta > 0 ? "scarto" : "attesa")} {Math.Abs(delta) * 1000:0} ms)");
    }

    /// <summary>Durante il passaggio il tempo del brano nuovo segue la griglia del vecchio (anello d'aggancio, al massimo +-1,2%).</summary>
    private static readonly bool QaSteer = Environment.GetEnvironmentVariable("CINECORE_QA_MUSIC_DUMP") != null;
    private static void SteerMix(Mix mix)
    {
        var b = mix.B;
        if (b.Stretch is not { } st || double.IsNaN(mix.SyncT0) || double.IsNaN(b.StretchBase) || mix.DropFrames > 0 || mix.SilenceFrames > 0) return;
        if (mix.Pos < mix.NextCheck) return;
        mix.NextCheck = mix.Pos + mix.A.Rate / 5;
        double own = (b.StartFrame + b.MediaFrame) / mix.A.Rate;
        double error = own - (mix.SyncS0 + ((mix.A.StartFrame + mix.A.MediaFrame) / mix.A.Rate - mix.SyncT0) * mix.MediaRatio);
        if (Math.Abs(error) > 1.5) return;
        // Sul Mac il brano che esce piu' lento e' gia' nell'obiettivo (MediaRatio = TempoRelation = ratio / outSpeed): il
        // brano che entra resta al suo tempo nominale. La versione Windows lo moltiplica anche per il tempo del brano che
        // esce: qui lo conterebbe due volte (provato 23/11: errore fino a -1,07 s).
        double outgoing = mix.A.Stretch is { } sa ? sa.Ratio : 1;
        st.Ratio = b.StretchNominal * (1 - Math.Clamp(error * 0.8, -0.012, 0.012));
        if (QaSteer && mix.Pos % mix.A.Rate < mix.A.Rate / 5)
            Console.Error.WriteLine($"[qa-aggancio] t={mix.Pos / (double)mix.A.Rate:0.0} s errore={error * 1000:+0;-0} ms tempo B={st.Ratio:0.000}x A={outgoing:0.000}x");
    }

    /// <summary>CD: il brano in corso sta ancora leggendo il disco, il successivo (dallo stesso disco) aspetta.</summary>
    private static bool CdBusy(Track current, string nextPath) => current.OnCd && !current.Eof && MacAudioCd.IsOnAudioCd(nextPath);

    /// <summary>Il successivo parte 15 s prima della fine (o della dissolvenza): pronto senza pause.</summary>
    private void PrepareNext()
    {
        if (_current == null || _next != null || _nextSource is not { Length: > 0 } nextPath) return;
        if (CdBusy(_current, nextPath)) return;
        if (CinecoreAudioSettings.Current is { Gapless: false, CrossfadeSeconds: 0 }) return;
        // Automix: il successivo lo prepara Control, dal punto d'ingresso scelto dal planner.
        if (CinecoreAudioSettings.Current.CrossfadeSeconds < 0) return;
        long remaining = _current.DurationFrames > 0 ? _current.DurationFrames - _current.StartFrame - _current.FramesOut : (_current.Eof ? 0 : long.MaxValue);
        long lead = (15 + Math.Clamp(CinecoreAudioSettings.Current.CrossfadeSeconds, 0, 12)) * (long)Rate;
        if (remaining > lead) return;
        // Avvio di FFmpeg fuori dal callback dell'audio.
        if (_preparing) return;
        _preparing = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var track = StartTrack(nextPath, 0);
            lock (_gate)
            {
                _preparing = false;
                if (_current != null && _next == null && _nextSource == nextPath) _next = track;
                else DisposeTrack(track);
            }
        });
    }
    private bool _preparing;
}
