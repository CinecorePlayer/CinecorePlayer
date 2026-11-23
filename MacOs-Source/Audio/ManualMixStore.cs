#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CinecorePlayer2025.Audio
{
    /// <summary>
    /// Passaggio scelto a mano fra due brani: il secondo entra quando il primo e' a <see cref="OutAt"/>
    /// secondi, partendo dal suo secondo <see cref="InAt"/>, con una dissolvenza di <see cref="Fade"/>.
    /// </summary>
    /// <remarks><see cref="Volume"/>, <see cref="Eq"/> e <see cref="Filter"/> sono lo stile del passaggio (come i tre
    /// selettori di Spotify, vedi <see cref="TransitionCurves.Compose"/>); -1 = stile automatico di prima.</remarks>
    internal sealed record ManualMix(double OutAt, double InAt, double Fade, int Volume = -1, int Eq = -1, int Filter = -1, int Fx = 0)
    {
#if CINECORE_MAC
        public double? IncomingSpeed { get; init; }
        /// <summary>Velocita' del brano che finisce dall'inizio del passaggio (1 o null = la sua).</summary>
        public double? OutgoingSpeed { get; init; }
        /// <summary>[NOI, 2026-11-22] Automazioni disegnate a mano ("out.vol", "in.filter"...: x,y,x,y...), vedi
        /// <see cref="TransitionCurves.WithLanes"/>. null = le curve dei selettori.</summary>
        public Dictionary<string, double[]>? Lanes { get; init; }
#endif
        public bool HasStyle => Volume >= 0;
        /// <summary>"Nessuna transizione": il brano nuovo parte quando finisce il vecchio, senza sovrapposizione.</summary>
        public bool NoTransition => Volume == TransitionCurves.VolumeNone;
    }

    /// <summary>I passaggi scelti a mano, per coppia di brani (salvati in %AppData%).</summary>
    internal static class ManualMixStore
    {
        private static readonly object Gate = new();
        private static Dictionary<string, ManualMix>? _mixes;
        private static string FilePath => Path.Combine(global::CinecorePlayer2025.Utilities.AppDataPaths.Get(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "manual-mix.json");

        private static string Key(string from, string to) => from.Trim().ToLowerInvariant() + "\n" + to.Trim().ToLowerInvariant();

        private static Dictionary<string, ManualMix> Mixes()
        {
            if (_mixes != null) return _mixes;
            try
            {
                if (File.Exists(FilePath))
                    _mixes = JsonSerializer.Deserialize<Dictionary<string, ManualMix>>(File.ReadAllText(FilePath));
            }
            catch (Exception ex) { Dbg.Warn("[MIX] manual mixes unreadable: " + ex.Message); }
            return _mixes ??= new Dictionary<string, ManualMix>();
        }

        public static ManualMix? Get(string? from, string? to)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return null;
            lock (Gate) return Mixes().TryGetValue(Key(from, to), out var mix) ? mix : null;
        }

        public static void Set(string from, string to, ManualMix mix)
        {
            lock (Gate) { Mixes()[Key(from, to)] = mix; Save(); }
        }

        public static void Remove(string from, string to)
        {
            lock (Gate) { if (Mixes().Remove(Key(from, to))) Save(); }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_mixes));
            }
            catch (Exception ex) { Dbg.Warn("[MIX] save failed: " + ex.Message); }
        }
    }

    internal static partial class AutomixAnalyzerEnvelope
    {
        /// <summary>
        /// Forma d'onda a due bande (picco per punto, 0..1): tutto il segnale e solo i bassi (sotto ~160 Hz), per disegnarle
        /// con due colori come nell'editor di Spotify.
        /// </summary>
        public static (float[] Full, float[] Low) ReadBands(string path, double from, double seconds, int points)
        {
            var full = new float[Math.Max(1, points)];
            var low = new float[Math.Max(1, points)];
            try
            {
                const int rate = 11025;
                using var decoder = new CinecoreAudioDecoder(path, rate, 1);
                if (seconds <= 0) seconds = Math.Max(1, decoder.DurationSeconds - from);
                if (from > 0.05) decoder.Seek(from);
                long total = (long)(seconds * rate), read = 0;
                var buffer = new float[4096];
                double k = 1 - Math.Exp(-2 * Math.PI * 160.0 / rate), l1 = 0, l2 = 0;
                while (read < total)
                {
                    int got = decoder.Read(buffer, out _);
                    if (got <= 0) break;
                    for (int i = 0; i < got && read < total; i++, read++)
                    {
                        int bucket = (int)(read * full.Length / total);
                        float x = buffer[i];
                        l1 += (x - l1) * k; l2 += (l1 - l2) * k;
                        float a = Math.Abs(x), b = (float)Math.Abs(l2) * 1.4f;
                        if (a > full[bucket]) full[bucket] = a;
                        if (b > low[bucket]) low[bucket] = Math.Min(1, b);
                    }
                }
            }
            catch { }
            return (full, low);
        }

        /// <summary>Forma d'onda di un tratto (picco per punto, 0..1): per disegnare il passaggio.</summary>
        public static float[] Read(string path, double from, double seconds, int points)
        {
            var result = new float[Math.Max(1, points)];
            try
            {
                const int rate = 11025;
                using var decoder = new CinecoreAudioDecoder(path, rate, 1);
                if (from > 0.05) decoder.Seek(from);
                long total = (long)(seconds * rate), read = 0;
                var buffer = new float[4096];
                while (read < total)
                {
                    int got = decoder.Read(buffer, out _);
                    if (got <= 0) break;
                    for (int i = 0; i < got && read < total; i++, read++)
                    {
                        int bucket = (int)(read * result.Length / total);
                        float value = Math.Abs(buffer[i]);
                        if (value > result[bucket]) result[bucket] = value;
                    }
                }
            }
            catch { }
            return result;
        }
    }
}
