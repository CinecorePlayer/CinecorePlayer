#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Audio
{
    internal sealed record TrackEntryGrid(double Cue, double Bpm, double Beat, double Confidence, double Downbeat, double DownbeatConfidence);
    /// <summary>
    /// Dove un brano comincia davvero, dove inizia a spegnersi e dove finisce: il necessario per una
    /// dissolvenza automatica (primo passo dell'automix). Si misura una volta per brano, in sottofondo e a
    /// bassa priorita', leggendo solo l'inizio e la fine; il risultato resta salvato.
    /// </summary>
    internal sealed record TrackMixProfile(double LeadIn, double OutroStart, double End, double Duration, double BodyDb,
        int Version = 1, double HeadBpm = 0, double HeadBeat = 0, double HeadConfidence = 0, double TailBpm = 0, double TailBeat = 0, double TailConfidence = 0,
        int Key = -1, double KeyConfidence = 0,
        double HeadDownbeat = 0, double HeadDownbeatConfidence = 0, double TailDownbeat = 0, double TailDownbeatConfidence = 0,
        double[]? HeadPhrases = null, double[]? HeadPhraseStrength = null, double[]? TailPhrases = null, double[]? TailPhraseStrength = null,
        int[]? HeadDb = null, int[]? TailDb = null, byte[]? HeadVoice = null, byte[]? TailVoice = null,
        double[]? HeadEntries = null, TrackEntryGrid[]? EntryGrids = null,
        MusicalMap? HeadMusic = null, MusicalMap? TailMusic = null)
    {
        public const int CurrentVersion = 11; // 11: fase sul battere (basse) invece che sui charleston in levare

        /// <summary>Inizio dei secondi misurati alla fine del brano (TailDb, TailVoice).</summary>
        [System.Text.Json.Serialization.JsonIgnore] public double TailStart => Math.Max(0, Duration - 60);

        /// <summary>Livello (dB RMS) del secondo che contiene <paramref name="time"/>, NaN se fuori dai tratti misurati.</summary>
        public double LevelAt(double time)
        {
            if (HeadDb is { Length: > 0 } h && time >= 0 && time < h.Length) return h[(int)time];
            if (TailDb is { Length: > 0 } t && time >= TailStart && time - TailStart < t.Length) return t[(int)(time - TailStart)];
            return double.NaN;
        }

        /// <summary>Probabilita' di voce (0-1) nel secondo che contiene <paramref name="time"/>, NaN se non misurata.</summary>
        public double VoiceAt(double time)
        {
            if (HeadVoice is { Length: > 0 } h && time >= 0 && time < h.Length) return h[(int)time] / 100.0;
            if (TailVoice is { Length: > 0 } t && time >= TailStart && time - TailStart < t.Length) return t[(int)(time - TailStart)] / 100.0;
            return double.NaN;
        }

        /// <summary>Forza (0-1) dell'inizio di frase vicino a <paramref name="time"/> (entro 0.15 s), 0 se non e' un inizio di frase.</summary>
        public double PhraseAt(double time)
        {
            double best = 0;
            foreach (var (times, strength) in new[] { (HeadPhrases, HeadPhraseStrength), (TailPhrases, TailPhraseStrength) })
            {
                if (times == null || strength == null) continue;
                for (int i = 0; i < times.Length && i < strength.Length; i++)
                    if (Math.Abs(times[i] - time) < 0.15) best = Math.Max(best, strength[i]);
            }
            return best;
        }

        /// <summary>Tempo del brano da mostrare (la griglia piu' affidabile fra inizio e fine), 0 se non ha un battito chiaro.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public double Bpm => Tail.Reliable && (!Head.Reliable || TailConfidence >= HeadConfidence) ? TailBpm : Head.Reliable ? HeadBpm : 0;
        /// <summary>Tonalita' in notazione Camelot (es. "8A"), vuota se incerta.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public string Camelot => KeyConfidence >= 0.15 ? KeyDetector.CamelotText(Key) : "";
        [System.Text.Json.Serialization.JsonIgnore] public string KeyName => KeyConfidence >= 0.15 ? KeyDetector.Name(Key) : "";

        /// <summary>Durata della dissolvenza adatta a questo finale: lunga su una coda che sfuma, corta su una chiusa netta.</summary>
        public double FadeSeconds => Math.Clamp(End - OutroStart, 3, 12);

        /// <summary>Battute dell'inizio del brano (dopo il silenzio iniziale).</summary>
        [System.Text.Json.Serialization.JsonIgnore] public BeatGrid Head => new(HeadBpm, HeadBeat, HeadConfidence);
        /// <summary>Battute della parte finale, prima dell'ultima uscita.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public BeatGrid Tail => new(TailBpm, TailBeat, TailConfidence);
    }

    internal static class AutomixAnalyzer
    {
        private const int Rate = 22050;
        private const double FrameSeconds = 0.05;
        private const double SilenceDb = -48;
        private static readonly ConcurrentDictionary<string, TrackMixProfile> Memory = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<Task<TrackMixProfile?>>> Running = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (string Identity, long CheckedAt)> MemoryStamps = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static string Folder => Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "automix");

        /// <summary>Profilo gia' pronto (in memoria o salvato), senza misurare.</summary>
        public static TrackMixProfile? TryGet(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (Memory.TryGetValue(path, out var known))
                {
                    long now = Environment.TickCount64;
                    bool stamped = MemoryStamps.TryGetValue(path, out var stamp);
                    if (known.Version >= TrackMixProfile.CurrentVersion && stamped && now - stamp.CheckedAt < 5000) return known;
                    string identity = SourceIdentity(path);
                    if (known.Version >= TrackMixProfile.CurrentVersion && (!stamped || identity == stamp.Identity))
                    { MemoryStamps[path] = (identity, now); return known; }
                    Memory.TryRemove(path, out _); MemoryStamps.TryRemove(path, out _);
                }
                string file = CachePath(path);
                if (!File.Exists(file)) return null;
                var loaded = JsonSerializer.Deserialize<TrackMixProfile>(File.ReadAllText(file));
                if (loaded == null || loaded.Version < TrackMixProfile.CurrentVersion) return null; // misurato prima delle battute: si rifa'
                Memory[path] = loaded;
                MemoryStamps[path] = (SourceIdentity(path), Environment.TickCount64);
                return loaded;
            }
            catch { return null; }
        }

        /// <summary>Prepara il profilo in sottofondo (una misura alla volta, a bassa priorita').</summary>
        public static Task<TrackMixProfile?> PrepareAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<TrackMixProfile?>(null);
            if (TryGet(path) is { } ready) return Task.FromResult<TrackMixProfile?>(ready);
            // GetOrAdd may invoke its factory more than once. Lazy ensures only the dictionary's
            // winning entry starts native analysis, even when playback and queue sorting ask together.
            return Running.GetOrAdd(path, p => new Lazy<Task<TrackMixProfile?>>(() => Task.Run(async () =>
            {
                await Gate.WaitAsync().ConfigureAwait(false);
                var thread = Thread.CurrentThread;
                var priority = thread.Priority;
                try
                {
                    thread.Priority = ThreadPriority.BelowNormal;
                    string identity = SourceIdentity(p);
                    var profile = Measure(p);
                    if (identity != SourceIdentity(p)) return null;
                    if (profile != null)
                    {
                        Memory[p] = profile;
                        MemoryStamps[p] = (identity, Environment.TickCount64);
                        try
                        {
                            Directory.CreateDirectory(Folder);
                            File.WriteAllText(CachePath(p), JsonSerializer.Serialize(profile));
                        }
                        catch { }
                        Dbg.Log($"[AUTOMIX] {Path.GetFileName(p)}: lead-in {profile.LeadIn:0.0}s, outro {profile.OutroStart:0.0}s, end {profile.End:0.0}s of {profile.Duration:0.0}s, fade {profile.FadeSeconds:0.0}s, " +
                            $"head {profile.HeadBpm:0.0} BPM (x{profile.HeadConfidence:0.0}), tail {profile.TailBpm:0.0} BPM (x{profile.TailConfidence:0.0}), key {KeyDetector.Name(profile.Key)} {KeyDetector.CamelotText(profile.Key)} ({profile.KeyConfidence:0.00}), " +
                            $"downbeats {profile.HeadDownbeat:0.00}s (x{profile.HeadDownbeatConfidence:0.00}) / {profile.TailDownbeat:0.00}s (x{profile.TailDownbeatConfidence:0.00}), " +
                            $"phrases {profile.HeadPhrases?.Length ?? 0}/{profile.TailPhrases?.Length ?? 0}, voice head {Percent(profile.HeadVoice)} tail {Percent(profile.TailVoice)}", Dbg.LogLevel.Info);
                    }
                    return profile;
                }
                catch (Exception ex) { Dbg.Warn("[AUTOMIX] " + Path.GetFileName(p) + ": " + ex.Message); return null; }
                finally
                {
                    thread.Priority = priority;
                    Gate.Release();
                    Running.TryRemove(p, out _);
                }
            }), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private static string Percent(byte[]? voice) => voice is { Length: > 0 } v ? $"{v.Count(x => x >= 50) * 100 / v.Length}%" : "-";

        private static string SourceIdentity(string path)
        {
            var info = new FileInfo(path);
            return path.ToLowerInvariant() + "|" + (info.Exists ? info.Length + "|" + info.LastWriteTimeUtc.Ticks : "missing");
        }

        private static string CachePath(string path)
        {
            string identity = SourceIdentity(path);
            string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
            return Path.Combine(Folder, hash + ".json");
        }

        private static TrackMixProfile? Measure(string path)
        {
            if (!File.Exists(path)) return null;
            // In stereo: la voce si riconosce anche da quanto sta al centro. Il resto dell'analisi usa la media dei canali.
            using var decoder = new CinecoreAudioDecoder(path, Rate, 2);
            double duration = decoder.DurationSeconds;
            if (duration < 20) return null;

            // Keep enough intro context for long DJ blends and quiet orchestral introductions.
            var headSamples = new List<float>();
            var headLeft = new List<float>(); var headRight = new List<float>();
            var head = Levels(decoder, 0, Math.Min(60, duration), headSamples, headLeft, headRight);
            int firstSound = head.FindIndex(db => db > SilenceDb);
            double leadIn = firstSound <= 0 ? 0 : Math.Min(12, Math.Max(0, firstSound * FrameSeconds - 0.15));

            // Corpo: 25 s a meta' brano. Intro e coda spesso non hanno batteria (pad, voce sola, applausi) e da sole
            // davano tempi a caso; il corpo dice il tempo vero del brano e fa da riferimento alle altre due letture.
            BeatGrid bodyGrid = default;
            if (duration > 110)
            {
                try
                {
                    double bodyFrom = duration * 0.45;
                    decoder.Seek(bodyFrom);
                    var bodySamples = new List<float>();
                    Levels(decoder, bodyFrom, 25, bodySamples);
                    if (bodySamples.Count > Rate * 10) bodyGrid = BeatTracker.Analyse(bodySamples.ToArray(), Rate, bodyFrom);
                }
                catch { }
            }

            // Fine: gli ultimi 60 s. Dove l'ultimo suono si spegne e da dove il livello scende stabilmente.
            double tailStart = Math.Max(0, duration - 60);
            decoder.Seek(tailStart);
            var tailSamples = new List<float>();
            var tailLeft = new List<float>(); var tailRight = new List<float>();
            var tail = Levels(decoder, tailStart, duration - tailStart, tailSamples, tailLeft, tailRight);
            if (tail.Count < 40) return null;
            int lastSound = tail.FindLastIndex(db => db > SilenceDb);
            if (lastSound < 0) return null;
            double end = tailStart + (lastSound + 1) * FrameSeconds;

            var smooth = Smooth(tail, 20); // un secondo
            var voiced = smooth.Take(lastSound + 1).Where(db => db > SilenceDb).OrderBy(db => db).ToList();
            double body = voiced.Count > 0 ? voiced[(int)(voiced.Count * 0.6)] : -20;
            // L'uscita comincia dove il livello resta sotto il corpo del brano di 6 dB fino alla fine.
            int outroIndex = lastSound;
            for (int i = lastSound; i >= 0; i--)
            {
                if (smooth[i] >= body - 6) break;
                outroIndex = i;
            }
            double outroStart = tailStart + outroIndex * FrameSeconds;
            if (end - outroStart < 0.5) outroStart = end - 3; // chiusa netta: dissolvenza corta

            // Battute: dopo il silenzio iniziale (25 s) e nei 30 s prima che il brano si spenga.
            BeatGrid headGrid = default, tailGrid = default;
            float[]? headPart = null, tailPart = null; double tailFrom = 0;
            try
            {
                int from = (int)(leadIn * Rate), length = Math.Min(headSamples.Count - from, 25 * Rate);
                if (length > Rate * 6) { headPart = headSamples.GetRange(from, length).ToArray(); headGrid = BeatTracker.Analyse(headPart, Rate, leadIn); }
                tailFrom = Math.Max(tailStart, Math.Min(outroStart, end) - 30);
                int tailOffset = (int)((tailFrom - tailStart) * Rate), tailLength = Math.Min(tailSamples.Count - tailOffset, (int)((Math.Max(outroStart, tailFrom + 8) - tailFrom) * Rate));
                if (tailOffset >= 0 && tailLength > Rate * 6) { tailPart = tailSamples.GetRange(tailOffset, tailLength).ToArray(); tailGrid = BeatTracker.Analyse(tailPart, Rate, tailFrom); }
            }
            catch { }
            // Tempo di riferimento: quello su cui concordano piu' letture (uguale, doppio o meta'), a parita' la piu' netta.
            // Una lettura d'intro o di coda che non concorda si rifa' cercando solo attorno al riferimento; se il battito li'
            // resta debole quel tratto non va a tempo. Prima bastava un disaccordo fra intro e coda per buttare tutto.
            static bool Related(double a, double b) => a > 0 && b > 0 && new[] { 1.0, 2.0, 0.5 }.Any(m => Math.Abs(a / b / m - 1) < 0.03);
            var readings = new[] { headGrid, bodyGrid, tailGrid }.Where(g => g.Reliable).ToList();
            BeatGrid reference = readings
                .OrderByDescending(g => readings.Count(o => Related(o.Bpm, g.Bpm)))
                .ThenByDescending(g => g.Confidence)
                .FirstOrDefault();
            if (reference.Reliable)
            {
                BeatGrid Reread(BeatGrid grid, float[]? part, double start)
                {
                    if (part == null || (grid.Reliable && Related(grid.Bpm, reference.Bpm))) return grid;
                    var again = BeatTracker.Analyse(part, Rate, start, reference.Bpm);
                    return again.Reliable ? again : grid with { Confidence = Math.Min(grid.Confidence, 1.3) };
                }
                var (oldHead, oldTail) = (headGrid.Bpm, tailGrid.Bpm);
                headGrid = Reread(headGrid, headPart, leadIn);
                tailGrid = Reread(tailGrid, tailPart, tailFrom);
                Dbg.Log($"[AUTOMIX] {Path.GetFileName(path)}: tempo {reference.Bpm:0.0} BPM (body {bodyGrid.Bpm:0.0} x{bodyGrid.Confidence:0.0}); " +
                    $"head {oldHead:0.0} -> {headGrid.Bpm:0.0} x{headGrid.Confidence:0.0}, tail {oldTail:0.0} -> {tailGrid.Bpm:0.0} x{tailGrid.Confidence:0.0}", Dbg.LogLevel.Info);
            }
            // Tonalita' sull'inizio e sulla fine insieme (le parti che si sovrappongono nel passaggio).
            var (key, keyConfidence) = (-1, 0.0);
            try
            {
                var both = new List<float>(headSamples.Count + tailSamples.Count);
                both.AddRange(headSamples); both.AddRange(tailSamples);
                (key, keyConfidence) = KeyDetector.Detect(both, Rate);
            }
            catch { }
            // Struttura: il "uno" delle battute, le frasi, livello e voce secondo per secondo (servono al planner per
            // scegliere dove uscire e dove entrare, senza due voci sovrapposte e senza salti di volume).
            double headDown = headGrid.FirstBeat, headDownConf = 0, tailDown = tailGrid.FirstBeat, tailDownConf = 0;
            List<(double Time, double Strength)> headPhrases = new(), tailPhrases = new();
            int[]? headDb = null, tailDb = null; byte[]? headVoice = null, tailVoice = null;
            try
            {
                var headArray = headSamples.ToArray();
                var tailArray = tailSamples.ToArray();
                if (headGrid.Reliable)
                {
                    (headDown, headDownConf) = MusicStructure.Downbeat(headArray, Rate, 0, headGrid);
                    headPhrases = MusicStructure.Phrases(headArray, Rate, 0, headDown, headGrid);
                }
                if (tailGrid.Reliable)
                {
                    (tailDown, tailDownConf) = MusicStructure.Downbeat(tailArray, Rate, tailStart, tailGrid);
                    tailPhrases = MusicStructure.Phrases(tailArray, Rate, tailStart, tailDown, tailGrid);
                }
                headDb = MusicStructure.LevelsPerSecond(headArray, Rate);
                tailDb = MusicStructure.LevelsPerSecond(tailArray, Rate);
                headVoice = MusicStructure.VoicePerSecondStereo(headLeft.ToArray(), headRight.ToArray(), Rate);
                tailVoice = MusicStructure.VoicePerSecondStereo(tailLeft.ToArray(), tailRight.ToArray(), Rate);
            }
            catch (Exception ex) { Dbg.Warn("[AUTOMIX] structure: " + ex.Message); }
            double[] entryCues = MusicStructure.EntryCues(head, FrameSeconds, body);
            var entryGrids = new List<TrackEntryGrid>();
            foreach (double cue in entryCues)
            {
                if (cue <= leadIn + 2) continue;
                int offset = (int)(cue * Rate), count = Math.Min(25 * Rate, headSamples.Count - offset);
                if (count < 12 * Rate) continue;
                var part = headSamples.GetRange(offset, count).ToArray();
                var grid = BeatTracker.Analyse(part, Rate, cue, reference.Reliable ? reference.Bpm : 0);
                if (!grid.Reliable || grid.Confidence < 1.8) continue;
                var down = MusicStructure.Downbeat(part, Rate, cue, grid);
                entryGrids.Add(new(cue, grid.Bpm, grid.FirstBeat, grid.Confidence, down.Downbeat, down.Confidence));
            }
            return new TrackMixProfile(leadIn, Math.Max(0, outroStart), end, duration, body, TrackMixProfile.CurrentVersion,
                headGrid.Bpm, headGrid.FirstBeat, headGrid.Confidence, tailGrid.Bpm, tailGrid.FirstBeat, tailGrid.Confidence, key, keyConfidence,
                headDown, headDownConf, tailDown, tailDownConf,
                headPhrases.Select(p => p.Time).ToArray(), headPhrases.Select(p => p.Strength).ToArray(),
                tailPhrases.Select(p => p.Time).ToArray(), tailPhrases.Select(p => p.Strength).ToArray(),
                headDb, tailDb, headVoice, tailVoice, entryCues, entryGrids.ToArray(),
                MusicalSections.Analyse(headSamples.ToArray(), Rate, 0), MusicalSections.Analyse(tailSamples.ToArray(), Rate, tailStart));
        }

        /// <summary>Livelli ogni 50 ms da un flusso stereo interlacciato; <paramref name="samples"/> riceve la media dei canali.</summary>
        private static List<double> Levels(CinecoreAudioDecoder decoder, double from, double seconds, List<float>? samples = null,
            List<float>? left = null, List<float>? right = null)
        {
            var levels = new List<double>();
            int frame = (int)(Rate * FrameSeconds);
            var buffer = new float[8192];
            double sum = 0; int count = 0;
            while (true)
            {
                int read = decoder.Read(buffer, out _);
                if (read <= 0) break;
                if (levels.Count * FrameSeconds >= seconds + 0.5) break; // i tempi del flusso possono mancare: conta i campioni
                // Read restituisce fotogrammi (un campione per canale), interlacciati nel buffer.
                for (int f = 0; f < read; f++)
                {
                    float l = buffer[2 * f], r = buffer[2 * f + 1], m = (l + r) * 0.5f;
                    samples?.Add(m); left?.Add(l); right?.Add(r);
                    sum += m * m;
                    if (++count == frame)
                    {
                        double rms = Math.Sqrt(sum / count);
                        levels.Add(rms > 1e-7 ? 20 * Math.Log10(rms) : -140);
                        sum = 0; count = 0;
                    }
                }
            }
            return levels;
        }

        private static double[] Smooth(List<double> levels, int window)
        {
            var result = new double[levels.Count];
            for (int i = 0; i < levels.Count; i++)
            {
                int a = Math.Max(0, i - window / 2), b = Math.Min(levels.Count, i + window / 2 + 1);
                double energy = 0;
                for (int j = a; j < b; j++) energy += Math.Pow(10, levels[j] / 10);
                result[i] = 10 * Math.Log10(Math.Max(1e-14, energy / (b - a)));
            }
            return result;
        }
    }
}
