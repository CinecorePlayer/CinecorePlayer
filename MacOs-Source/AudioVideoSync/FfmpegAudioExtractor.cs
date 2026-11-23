using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;

namespace Cinecore.AudioVideoSync;

internal static class FfmpegAudioExtractor
{
    public const int SampleRate = 16_000;

    public static async Task<short[]> ExtractMonoPcmAsync(
        string ffmpegPath,
        string input,
        int audioOrdinal,
        double startSeconds,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Add(startInfo, "-hide_banner", "-loglevel", "error", "-nostdin");
        if (startSeconds > 0)
            Add(startInfo, "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        Add(startInfo,
            "-i", input,
            "-map", $"0:a:{audioOrdinal}",
            "-vn", "-sn", "-dn",
            "-t", durationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            // Raw PCM has no timestamps: without aresample a track that starts later
            // than the container (MKV priming, external files with a lead-in) would be
            // analysed as if it began at 0, while the player honours its real start.
            // first_pts=0 pads that lead-in with silence; async fills gaps mid-stream.
            "-af", "aresample=async=1:min_hard_comp=0.02:first_pts=0,highpass=f=90,lowpass=f=6500",
            "-ac", "1",
            "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le",
            "-f", "s16le",
            "pipe:1");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Impossibile avviare FFmpeg.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new FileNotFoundException(
                "FFmpeg non è disponibile. Specifica --ffmpeg <path> oppure aggiungilo al PATH.",
                ffmpegPath,
                ex);
        }

        using var cancellation = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        await using var pcmBytes = new MemoryStream();
        Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(pcmBytes, cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(copyTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        string stderr = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg non ha estratto la traccia audio {audioOrdinal}: {stderr.Trim()}");

        byte[] bytes = pcmBytes.ToArray();
        int sampleCount = bytes.Length / 2;
        if (sampleCount < SampleRate * 8)
            throw new InvalidOperationException(
                $"La traccia audio {audioOrdinal} contiene meno di 8 secondi analizzabili.");

        var samples = new short[sampleCount];
        Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * 2);
        return samples;
    }

    private static void Add(ProcessStartInfo startInfo, params string[] args)
    {
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);
    }
}
