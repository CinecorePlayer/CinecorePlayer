using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CinecoreMac;

/// <summary>
/// Indirizzi web da riprodurre (WebMediaResolver di Windows, ridotto): un file diretto (.mp4, .m3u8…) si apre com'e';
/// YouTube e gli altri siti che yt-dlp conosce danno titolo e indirizzi diretti, spesso video e audio separati.
/// yt-dlp parte tramite <see cref="ProcessLauncher"/> (niente fork dell'app con mpv dentro).
/// </summary>
internal static class MacWebResolver
{
    public sealed record Resolved(string Video, string? Audio, string? Title, int Height = 0, int[]? Heights = null);

    /// <summary>Limite di altezza per YouTube (0 = nessun limite), come "Risoluzione web" di Windows.</summary>
    public static int MaxHeight
    {
        get => int.TryParse(MacSettings.Get("webMaxHeight", "0"), out int h) ? h : 0;
        set => MacSettings.Set("webMaxHeight", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static string? YtDlp => MacYtDlp.Find();

    public static bool IsWebAddress(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static bool IsYouTube(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host is "youtube.com" or "youtu.be" || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase));

    private static bool LooksDirect(string url)
    {
        string path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath.ToLowerInvariant() : url.ToLowerInvariant();
        return new[] { ".mp4", ".m4v", ".mkv", ".mov", ".webm", ".m3u8", ".mpd", ".ts", ".mp3", ".m4a", ".flac", ".aac", ".ogg", ".opus", ".wav" }.Any(path.EndsWith);
    }

    // Selettore dei formati di yt-dlp (BuildYtFormatSelector di Windows): senza limite punta al 4K/HDR.
    internal static string FormatSelector(int max, bool exact = false) => max <= 0
        ? "bv*[dynamic_range^=HDR][height>=2160]+ba/bv*[height>=2160]+ba/bv*[height>=1440]+ba/bv*[height>=1080]+ba/b[height>=1080]/bv*+ba/b"
        : exact ? $"bv[height={max}]+ba/b[height={max}]" : $"bv[height<={max}]+ba/b[height<={max}]/bv*+ba/b";

    /// <summary>Null se l'indirizzo non si riesce a riprodurre; <paramref name="error"/> dice perche'.</summary>
    public static Resolved? Resolve(string url, CancellationToken ct, out string? error, int? height = null, bool exact = false)
    {
        error = null;
        url = url.Trim();
        if (!IsWebAddress(url)) { error = "Non è un indirizzo web"; return null; }
        if (LooksDirect(url)) return new Resolved(url, null, null);
        if (MacYtDlp.Ensure(ct) is not { } ytdlp) { error = MacYtDlp.MissingMessage; return null; }
        bool youtube = IsYouTube(url);
        var outputArgs = youtube ? new[] { "--dump-single-json" } : new[] { "--print", "title", "--print", "urls" };
        var args = new[] { "--no-playlist", "--no-warnings", "--socket-timeout", "15", "-f", FormatSelector(height ?? MaxHeight, exact) }
            .Concat(outputArgs).Append(url);
        var (exit, stdout, stderr) = ProcessLauncher.Run(ytdlp, args, false, 60000, ct);
        // Primo tentativo dopo l'avvio fallito (rete o yt-dlp non ancora pronti): si riprova una volta.
        if (exit != 0 && youtube && !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("[web] yt-dlp fallito, riprovo: " + stderr.Trim().Replace('\n', ' '));
            ct.WaitHandle.WaitOne(1500);
            ct.ThrowIfCancellationRequested();
            (exit, stdout, stderr) = ProcessLauncher.Run(ytdlp, args, false, 60000, ct);
        }
        if (youtube && exit == 0 && ParseYouTubeInfo(stdout) is { } video) return video;
        var lines = Encoding.UTF8.GetString(stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var urls = youtube ? new() : lines.Skip(1).Where(IsWebAddress).ToList();
        if (exit != 0 || urls.Count == 0)
        {
            string last = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
            error = last.Length > 0 ? last.Replace("ERROR: ", "") : "Video non disponibile";
            // Un sito sconosciuto a yt-dlp: si prova l'indirizzo com'e' (mpv e LAV leggono molte pagine con un flusso).
            return youtube ? null : new Resolved(url, null, null);
        }
        return new Resolved(urls[0], urls.Count > 1 ? urls[1] : null, lines.Length > 0 ? lines[0] : null);
    }

    internal static Resolved? ParseYouTubeInfo(byte[] json)
    {
        static string? Field(JsonElement value, string key) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
                ? field.GetString() : null;
        static int Height(JsonElement value) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty("height", out var field) &&
            field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int h) ? h : 0;
        static bool Playable(JsonElement format) =>
            Field(format, "url") is { } url && IsWebAddress(url) &&
            (!format.TryGetProperty("has_drm", out var drm) || drm.ValueKind is JsonValueKind.False or JsonValueKind.Null);
        static bool Video(JsonElement format) => Field(format, "vcodec") is { Length: > 0 } codec && codec != "none";
        static bool Audio(JsonElement format) => Field(format, "acodec") is { Length: > 0 } codec && codec != "none";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var selected = root.TryGetProperty("requested_formats", out var requested) && requested.ValueKind == JsonValueKind.Array
                ? requested.EnumerateArray().ToArray() : new[] { root };
            var video = selected.FirstOrDefault(f => Video(f) && Playable(f));
            if (Field(video, "url") is not { } path) return null;
            var audio = selected.FirstOrDefault(f => !Video(f) && Audio(f) && Playable(f));
            var formats = root.TryGetProperty("formats", out var all) && all.ValueKind == JsonValueKind.Array
                ? all.EnumerateArray().Where(Playable).ToArray() : selected;
            bool separateAudio = formats.Any(f => !Video(f) && Audio(f));
            int h = Height(video);
            int[] heights = formats.Where(f => Video(f) && (Audio(f) || separateAudio)).Select(Height)
                .Append(h).Where(n => n > 0).Distinct().OrderByDescending(n => n).ToArray();
            return new Resolved(path, Field(audio, "url"), Field(root, "title"), h, heights);
        }
        catch (JsonException) { return null; }
    }
}
