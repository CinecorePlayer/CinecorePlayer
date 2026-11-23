#nullable enable
using CinecorePlayer2025.Utilities;
using CinecoreMac;
using System;
using System.IO;
using System.Threading;

namespace CinecorePlayer2025.Audio;

/// <summary>Anteprima delle corsie e del passaggio sull'uscita audio della musica.</summary>
internal sealed class MixPreviewPlayer : IDisposable
{
    public enum Lane { None, From, To }
    private const int Rate = MacPcmOutput.Rate, Channels = MacPcmOutput.Channels, Chunk = 2048;

    private sealed class Playback(MacPcmOutput output, Func<long, (double?, double?)> clock, Lane mode)
    {
        public readonly MacPcmOutput Output = output;
        public readonly Func<long, (double?, double?)> Clock = clock;
        public readonly Lane Mode = mode;
        public volatile bool Running = true;
        public Thread? Worker;
    }

    private Playback? _playback;
    public bool Playing => _playback is { Running: true };
    public Lane Mode => _playback?.Mode ?? Lane.None;
    public string? LastError { get; private set; }

    public (double? From, double? To) Position => _playback is { } playback
        ? playback.Clock(playback.Output.PlayedFrames) : (null, null);

    public bool PlaySolo(string path, double start, Lane lane, float volume, double speed)
    {
        Stop();
        CinecoreAudioDecoder decoder;
        try { decoder = new CinecoreAudioDecoder(path, Rate, Channels, start: start, speed: speed); }
        catch (Exception ex) { LastError = ex.Message; return false; }

        long frame = 0;
        int Render(float[] target, int frames)
        {
            int got = ReadFrames(decoder, target.AsSpan(0, frames * Channels));
            for (int f = 0; f < got; f++)
            {
                float gain = volume * Math.Min(1f, (frame + f) / (Rate * .008f));
                for (int c = 0; c < Channels; c++) target[f * Channels + c] *= gain;
            }
            frame += got;
            return got;
        }

        (double?, double?) Clock(long played)
        {
            double seconds = start + played * speed / Rate;
            return lane == Lane.From ? (seconds, null) : (null, seconds);
        }

        return Start(Render, Clock, lane, decoder);
    }

    public bool PlayTransition(string fromPath, string toPath, double start, double outAt, double inAt,
        double fade, TransitionCurves? curves, float volume, double speed, double outSpeed = 1)
    {
        Stop();
        CinecoreAudioDecoder? a = null, a2 = null;
        CinecoreAudioDecoder b;
        // Brano che finisce: alla sua velocita' fino al punto del passaggio, poi a quella scelta (come il motore).
        bool stretchedOut = Math.Abs(outSpeed - 1) >= .002;
        try
        {
            a = new CinecoreAudioDecoder(fromPath, Rate, Channels, start: start);
            if (stretchedOut)
            {
                // Parte mezzo secondo prima e quel pezzo si scarta: l'inizio dello stiramento (che sale dal silenzio)
                // non si sente. Al punto del passaggio si raccorda al brano normale con 20 ms di dissolvenza.
                double pre = Math.Min(0.5, outAt);
                a2 = new CinecoreAudioDecoder(fromPath, Rate, Channels, start: outAt - pre, speed: outSpeed);
                var skip = new float[Chunk * Channels];
                for (long left = (long)Math.Round(pre / outSpeed * Rate); left > 0;)
                {
                    int got = ReadFrames(a2, skip.AsSpan(0, (int)Math.Min(Chunk, left) * Channels));
                    if (got <= 0) break;
                    left -= got;
                }
            }
            b = new CinecoreAudioDecoder(toPath, Rate, Channels, start: inAt, speed: speed);
        }
        catch (Exception ex) { a?.Dispose(); a2?.Dispose(); LastError = ex.Message; return false; }

        long outFrame = (long)Math.Round(Math.Max(0, outAt - start) * Rate);
        long fadeFrames = Math.Max(1, (long)Math.Round(fade * Rate));
        long fadeEnd = outFrame + fadeFrames;
        long endFrame = fadeEnd + 10L * Rate;
        var outDeck = curves == null ? null : new CurveDeckProcessor(curves.Out, true, Rate, Channels);
        var inDeck = curves == null ? null : new CurveDeckProcessor(curves.In, false, Rate, Channels);
        var limiter = new TruePeakLimiter(Channels, Rate, -1);
        var outgoing = new float[Chunk * Channels];
        var joint = new float[Chunk * Channels];
        long Joint = Rate / 50;
        var incoming = new float[Chunk * Channels];
        long frame = 0, flush = limiter.LatencyFrames;

        int Render(float[] target, int frames)
        {
            var buffer = target.AsSpan(0, frames * Channels);
            buffer.Clear();
            int dry = (int)Math.Clamp(outFrame - frame, 0, frames);
            int aMix = (int)Math.Clamp(fadeEnd - frame - dry, 0, frames - dry);
            int gotDry = ReadFrames(a, buffer[..(dry * Channels)]);
            int gotA = ReadFrames(a2 ?? a, outgoing.AsSpan(0, aMix * Channels));
            long mixAt = Math.Max(0, frame + dry - outFrame);
            if (a2 != null && mixAt < Joint && gotA > 0)
            {
                // Raccordo: per i primi Joint fotogrammi il brano non stirato sfuma in quello stirato.
                int n = (int)Math.Min(gotA, Joint - mixAt);
                int plain = ReadFrames(a, joint.AsSpan(0, n * Channels));
                for (int f = 0; f < plain; f++)
                {
                    float g = (float)((mixAt + f + 1) / (double)Joint);
                    for (int c = 0; c < Channels; c++) outgoing[f * Channels + c] = outgoing[f * Channels + c] * g + joint[f * Channels + c] * (1 - g);
                }
            }
            int bCount = (int)Math.Clamp(endFrame - frame - dry, 0, frames - dry);
            int gotB = ReadFrames(b, incoming.AsSpan(0, bCount * Channels));
            long mixStart = Math.Max(0, frame + dry - outFrame);

            outDeck?.Process(outgoing, gotA * Channels, mixStart, fadeFrames);
            inDeck?.Process(incoming, gotB * Channels, mixStart, fadeFrames);
            int mixed = Math.Max(gotA, gotB);
            for (int f = 0; f < mixed; f++)
            {
                double progress = Math.Clamp((mixStart + f) / (double)fadeFrames, 0, 1);
                double ga = curves?.OutVolumeAt(progress) ?? Math.Cos(progress * Math.PI / 2);
                double gb = curves?.InVolumeAt(progress) ?? Math.Sin(progress * Math.PI / 2);
                float norm = (float)(1 / Math.Sqrt(Math.Max(1, ga * ga + gb * gb)));
                for (int c = 0; c < Channels; c++)
                {
                    int i = f * Channels + c;
                    double sampleA = f >= gotA ? 0 : outgoing[i] * (outDeck == null ? ga : 1);
                    double sampleB = f >= gotB ? 0 : incoming[i] * (inDeck == null ? gb : 1);
                    buffer[(dry + f) * Channels + c] = (float)(sampleA + sampleB) * norm;
                }
            }

            int rendered = mixed == 0 ? gotDry : dry + mixed;
            if (rendered < frames && (frame + rendered >= endFrame || ((a2 ?? a).EndOfStream || frame + rendered >= fadeEnd) && b.EndOfStream))
            {
                int added = (int)Math.Min(frames - rendered, flush);
                rendered += added;
                flush -= added;
            }
            Span<double> stereo = stackalloc double[Channels];
            for (int f = 0; f < rendered; f++)
            {
                double edge = Math.Clamp(Math.Min(frame + f, endFrame - frame - f) / (Rate * .012), 0, 1);
                int i = f * Channels;
                for (int c = 0; c < Channels; c++) stereo[c] = buffer[i + c] * volume * edge;
                limiter.Process(stereo, out _);
                for (int c = 0; c < Channels; c++) buffer[i + c] = (float)stereo[c];
            }
            frame += rendered;
            return rendered;
        }

        (double?, double?) Clock(long played)
        {
            long audible = Math.Max(0, played - limiter.LatencyFrames);
            double? from = audible <= fadeEnd ? (audible <= outFrame ? start + audible / (double)Rate : outAt + (audible - outFrame) * outSpeed / Rate) : null;
            double? to = audible >= outFrame ? inAt + (audible - outFrame) * speed / Rate : null;
            return (from, to);
        }

        return a2 != null ? Start(Render, Clock, Lane.None, a, b, a2) : Start(Render, Clock, Lane.None, a, b);
    }

    private static int ReadFrames(CinecoreAudioDecoder decoder, Span<float> target)
    {
        int filled = 0;
        while (filled * Channels < target.Length)
        {
            int got = decoder.Read(target[(filled * Channels)..], out _);
            if (got <= 0) break;
            filled += got;
        }
        return filled;
    }

    private bool Start(Func<float[], int, int> render, Func<long, (double?, double?)> clock, Lane mode, params IDisposable[] owned)
    {
        LastError = null;
        // Prove: CINECORE_PREVIEW_DUMP=file scrive l'anteprima (float32, interleaved) invece di suonarla.
        if (Environment.GetEnvironmentVariable("CINECORE_PREVIEW_DUMP") is { Length: > 0 } dump)
        {
            var buffer = new float[Chunk * Channels];
            using (var file = File.Create(dump))
                for (int frames; (frames = render(buffer, Chunk)) > 0;)
                    file.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan(0, frames * Channels)));
            foreach (var item in owned) item.Dispose();
            Console.Error.WriteLine("[qa] anteprima scritta in " + dump);
            LastError = "scritta su file";
            return false;
        }
        var output = new MacPcmOutput();
        var block = new float[Chunk * Channels];
        bool ended = false;
        try
        {
            while (!ended && output.Buffered < .5)
            {
                int frames = render(block, Chunk);
                ended = frames == 0;
                if (!ended) output.Write(block, frames * Channels);
            }
            if (output.Buffered <= 0) LastError = "nessun campione audio";
            else if (!output.Start(out string? error)) LastError = error;
        }
        catch (Exception ex) { LastError = ex.Message; }
        if (LastError != null)
        {
            output.Dispose();
            foreach (var item in owned) item.Dispose();
            return false;
        }

        var playback = new Playback(output, clock, mode);
        _playback = playback;
        playback.Worker = new Thread(() =>
        {
            try
            {
                while (playback.Running)
                {
                    if (!ended && output.Buffered < .5)
                    {
                        int frames = render(block, Chunk);
                        ended = frames == 0;
                        if (!ended) output.Write(block, frames * Channels);
                        continue;
                    }
                    if (ended && output.Drained) break;
                    Thread.Sleep(8);
                }
            }
            catch (Exception ex) { Dbg.Warn("[MIX PREVIEW] render: " + ex.Message); }
            finally
            {
                playback.Running = false;
                output.Dispose();
                foreach (var item in owned) item.Dispose();
            }
        }) { IsBackground = true, Name = "Mix preview", Priority = ThreadPriority.AboveNormal };
        playback.Worker.Start();
        return true;
    }

    public void Stop()
    {
        var playback = _playback;
        _playback = null;
        if (playback == null) return;
        playback.Running = false;
        playback.Worker?.Join(200);
    }

    public void Dispose() => Stop();
}
