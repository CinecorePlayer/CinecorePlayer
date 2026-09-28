using System.Diagnostics;
using System.Text.Json;

namespace Cinecore.AudioVideoSync;

internal static class MkvRemuxer
{
    public static async Task ApplyDelayAsync(
        string mkvMergePath,
        string input,
        string output,
        int targetAudioOrdinal,
        int correctionMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(input), ".mkv", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(output), ".mkv", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Il remux lossless v1 supporta soltanto input e output Matroska (.mkv).");
        if (File.Exists(output))
            throw new IOException($"L'output esiste già e non verrà sovrascritto: {output}");

        string identification = await RunAndCaptureAsync(
            mkvMergePath, new[] { "-J", input }, cancellationToken);
        int trackId = ResolveAudioTrackId(identification, targetAudioOrdinal);

        string? directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await RunAndCaptureAsync(
            mkvMergePath,
            new[]
            {
                "--output", output,
                "--sync", $"{trackId}:{correctionMilliseconds}",
                input
            },
            cancellationToken);

        if (!File.Exists(output))
            throw new IOException("mkvmerge è terminato senza creare il file di output.");
    }

    private static int ResolveAudioTrackId(string json, int audioOrdinal)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var audioTracks = document.RootElement
            .GetProperty("tracks")
            .EnumerateArray()
            .Where(track => string.Equals(
                track.GetProperty("type").GetString(), "audio", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (audioOrdinal < 0 || audioOrdinal >= audioTracks.Length)
            throw new InvalidOperationException(
                $"La traccia audio {audioOrdinal} non esiste: il file contiene {audioTracks.Length} tracce audio.");
        return audioTracks[audioOrdinal].GetProperty("id").GetInt32();
    }

    private static async Task<string> RunAndCaptureAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Impossibile avviare {executable}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FileNotFoundException(
                "mkvmerge non è disponibile. Installare MKVToolNix o specificare --mkvmerge <path>.",
                executable,
                ex);
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"mkvmerge è terminato con codice {process.ExitCode}: {stderr.Trim()}");
        return stdout;
    }
}
