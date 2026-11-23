#nullable enable
using Cinecore.AudioVideoSync;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities;

internal static class SubtitleSyncCheck
{
    internal sealed record Finding(string Name, SubtitleAligner.Result? Alignment, string Status);

    internal static async Task<ExternalAudioBinding?> AudioForLanguageAsync(string video, string? language, CancellationToken ct)
    {
        var binding = ExternalAudioStore.Get(video);
        string? wanted = SubtitleNameNormalizer.TryDetectLanguageKey(language);
        if (wanted == null) return binding;
        string ffprobe = Path.Combine(Path.GetDirectoryName(ExternalToolLocator.FindFfmpeg(null)) ?? "", OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        using var info = JsonDocument.Parse(await RunAsync(ffprobe, new[] { "-v", "error", "-select_streams", "a", "-show_entries", "stream_tags=language", "-of", "json", video }, ct).ConfigureAwait(false));
        var audio = info.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        string? currentLanguage = binding?.Language;
        if (binding?.AudioPath is string external && currentLanguage == null)
        {
            using var externalInfo = JsonDocument.Parse(await RunAsync(ffprobe, new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream_tags=language", "-of", "json", external }, ct).ConfigureAwait(false));
            currentLanguage = externalInfo.RootElement.GetProperty("streams").EnumerateArray().Select(Language).FirstOrDefault();
        }
        else if (binding?.AudioPath == null) currentLanguage ??= Language(audio.ElementAtOrDefault(binding?.TargetOrdinal ?? 0));
        if (wanted == SubtitleNameNormalizer.TryDetectLanguageKey(currentLanguage)) return binding;
        int ordinal = Array.FindIndex(audio, stream => Language(stream) == wanted);
        // A correction measured for another language must not shift this subtitle.
        return ordinal >= 0 ? new ExternalAudioBinding(null, TargetOrdinal: ordinal, Language: wanted) : null;
    }

    internal static async Task<List<Finding>> CheckAsync(string video, ExternalAudioBinding binding, string? language, CancellationToken ct)
    {
        string ffmpeg = ExternalToolLocator.FindFfmpeg(null);
        string ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        using var info = JsonDocument.Parse(await RunAsync(ffprobe, new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name:stream_tags=language,title", "-of", "json", video }, ct).ConfigureAwait(false));
        var streams = info.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        double duration = info.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var seconds)
            && double.TryParse(seconds.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
        if (binding.AudioPath != null)
        {
            using var audioInfo = JsonDocument.Parse(await RunAsync(ffprobe, new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream_tags=language", "-of", "json", binding.AudioPath }, ct).ConfigureAwait(false));
            language = audioInfo.RootElement.GetProperty("streams").EnumerateArray().Select(Language).FirstOrDefault(x => x != null) ?? binding.Language ?? language;
        }
        else language ??= Language(streams.Where(IsAudio).ElementAtOrDefault(binding.TargetOrdinal ?? 0));
        if (language == null) return new() { new("", null, "unknown-language") };
        var candidates = new List<(string Name, string? Path, int Ordinal, string Codec)>();
        int ordinal = 0;
        foreach (var stream in streams.Where(s => Type(s) == "subtitle"))
        {
            if (Language(stream) == language)
                candidates.Add(($"{language} #{ordinal + 1}", null, ordinal, stream.GetProperty("codec_name").GetString() ?? ""));
            ordinal++;
        }
        foreach (string file in SubtitleFile.FindNextTo(video))
            if (SubtitleNameNormalizer.TryDetectLanguageKey(Path.GetFileName(file)) == language)
                candidates.Add((Path.GetFileName(file), file, -1, "subrip"));
        var findings = new List<Finding>();
        var prepared = new SubtitleAligner.PreparedAudio();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (candidate.Codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub")
            { findings.Add(new(candidate.Name, null, "bitmap")); continue; }
            try
            {
                var cues = candidate.Path != null
                    ? SubtitleFile.Parse(SubtitleFile.Decode(await File.ReadAllBytesAsync(candidate.Path, ct).ConfigureAwait(false)))
                    : await ReadEmbeddedCuesAsync(ffprobe, video, candidate.Ordinal, duration, ct).ConfigureAwait(false);
                var alignment = await SubtitleAligner.AlignAsync(video, cues, null, ct, binding, prepared).ConfigureAwait(false);
                findings.Add(new(candidate.Name, alignment, alignment.Reliable ? alignment.Changed ? "desync" : "aligned" : "uncertain"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn("[SUBS-CHECK] " + candidate.Name + ": " + ex.Message); findings.Add(new(candidate.Name, null, "failed")); }
        }
        return findings;
    }

    internal static async Task<List<SubtitleFile.Cue>> ReadEmbeddedCuesAsync(string ffprobe, string video, int ordinal, double duration, CancellationToken ct)
    {
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("Durata sottotitoli non disponibile.");
        // The check only needs cue timings around its audio samples. Seeking to those
        // ranges avoids extracting every subtitle by scanning a whole large film.
        string intervals = string.Join(",", SubtitleAligner.SubtitleReadIntervals(duration).Select(range =>
            range.Start.ToString("0.###", CultureInfo.InvariantCulture) + "%" + range.End.ToString("0.###", CultureInfo.InvariantCulture)));
        using var packets = JsonDocument.Parse(await RunAsync(ffprobe, new[] { "-v", "error", "-select_streams", "s:" + ordinal,
            "-read_intervals", intervals, "-show_packets", "-show_entries", "packet=pts_time,duration_time", "-of", "json", video }, ct).ConfigureAwait(false));
        var cues = new List<SubtitleFile.Cue>();
        foreach (var packet in packets.RootElement.GetProperty("packets").EnumerateArray())
        {
            if (!packet.TryGetProperty("pts_time", out var pts) || !packet.TryGetProperty("duration_time", out var length) ||
                !double.TryParse(pts.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double start) ||
                !double.TryParse(length.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double span) ||
                !double.IsFinite(start) || !double.IsFinite(span) || span <= 0) continue;
            cues.Add(new(start, start + span, ""));
        }
        return cues.DistinctBy(cue => (cue.Start, cue.End)).OrderBy(cue => cue.Start).ToList();
    }

    private static string? Type(JsonElement stream) => stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("codec_type", out var value) ? value.GetString() : null;
    private static bool IsAudio(JsonElement stream) => Type(stream) == "audio";
    private static string? Language(JsonElement stream)
        => stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var lang)
            ? SubtitleNameNormalizer.TryDetectLanguageKey(lang.GetString()) : null;

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Avvio analisi sottotitoli non riuscito.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var cancellation = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        Task<string> output = process.StandardOutput.ReadToEndAsync(ct), errors = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(output, errors, process.WaitForExitAsync(ct)).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidDataException("Formato sottotitoli non verificabile.");
        return await output.ConfigureAwait(false);
    }
}
