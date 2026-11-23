#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CinecorePlayer2025.Audio
{
    // Copia per il Mac delle impostazioni del Cinecore Audio Engine di Windows (Audio/CinecoreAudioEngine.cs, che
    // dipende da NAudio): stessi campi e stessa conversione verso la catena DSP; il file sta in Application Support.
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
        // AutoEQ dinamico (come il Dynamic EQ dei ricevitori): curve isofoniche secondo il livello d'ascolto vero.
        public bool DynamicEq { get; set; }
        public int DynamicEqOffset { get; set; } = 10;
        public string ReplayGain { get; set; } = "off"; // off | track | album
        public double ReplayGainPreampDb { get; set; }  // per i brani senza tag: 0 = nessuna modifica
        // Taratura dei VU meter: "auto" (segue la loudness del brano) oppure dBFS per 0 VU ("-18", "-14", "-10").
        public string VuCalibration { get; set; } = "auto";
        // Scala degli strumenti: "peak" (picco digitale, -inf..+3 dBFS, come lo strumento di riferimento) oppure "vu".
        public string MeterScale { get; set; } = "peak";
        // Dissolvenza incrociata fra un brano e il successivo, in secondi (0 = spenta).
        public int CrossfadeSeconds { get; set; }
        public int CrossfadeBeforeAutomix { get; set; }
        public void SetCrossfade(int seconds)
        {
            if (seconds < 0 && CrossfadeSeconds >= 0) CrossfadeBeforeAutomix = CrossfadeSeconds;
            CrossfadeSeconds = seconds;
        }
        // Brani di seguito senza pausa (stessa uscita), quando la dissolvenza non interviene.
        public bool Gapless { get; set; } = true;
        // Automix: il brano successivo parte dal primo suono e quello che finisce non aspetta la sua coda muta.
        public bool AutomixTrimSilence { get; set; } = true;
        // Automix anche fra brani consecutivi di un album, se il brano sfuma o finisce in silenzio.
        public bool AutomixWithinAlbum { get; set; } = true;
        // Automix: "preserve" (brani interi, si taglia al massimo un po' di coda) o "dj" (DJ edit: uscite e entrate sulle
        // frasi, anche saltando parti).
        public string AutomixMode { get; set; } = "dj";
        // Gentle fino a +-3%, balanced fino a +-4%, DJ fino a +-6% solo sul brano entrante.
        // Quando le sezioni non si prestano al beatmix, il planner cambia tecnica.
        public string AutomixIntensity { get; set; } = "dj";
        // Automix: il brano nuovo prende il tempo di quello che esce (time-stretch). Spento: a tempo solo i brani gia' uguali.
        public bool AutomixTempoSync { get; set; } = true;
        // Effetti da DJ (scambio dei bassi, filtro, eco): tutte le intensita' tranne la delicata.
        [System.Text.Json.Serialization.JsonIgnore] public bool AutomixEffects => AutomixIntensity is not ("gentle" or "crossfade");
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
                Enum.TryParse<CrossfeedLevel>(Crossfeed, out var cf) ? cf : CrossfeedLevel.Off, Loudness, DynamicEq, Math.Clamp(DynamicEqOffset, 0, 15));
        }

        private static string FilePath => (Environment.GetEnvironmentVariable("CINECORE_SETTINGS") is { Length: > 0 } qa
            ? Path.Combine(Path.GetDirectoryName(qa) ?? ".", Path.GetFileNameWithoutExtension(qa) + "-audioEngine.json") // prove: mai il file vero
            : System.IO.Path.Combine(global::CinecoreMac.PlatformPaths.CinecoreData, "audioEngine.json"));
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
}
