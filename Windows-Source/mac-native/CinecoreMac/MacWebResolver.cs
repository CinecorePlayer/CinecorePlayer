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
    /// <summary>
    /// Video risolto. <paramref name="Height"/> e <paramref name="Heights"/> sono qualita' nominali (1080, 2160…), non
    /// le altezze in pixel: un film 2,39:1 in 4K e' 3840×1608, ma per tutti e' "2160p".
    /// </summary>
    public sealed record Resolved(string Video, string? Audio, string? Title, int Height = 0, int[]? Heights = null, WebFormat[]? Formats = null);

    /// <summary>Un formato di YouTube; <paramref name="Index"/> e' la posizione nell'elenco di yt-dlp (dal peggiore al migliore).</summary>
    public sealed record WebFormat(string Id, string Url, int Quality, bool HasVideo, bool HasAudio, bool Hdr, bool Https, int Index, double Kbps = 0);

    /// <summary>Non piu' usato per scegliere: ogni video parte in Automatica (resta per lo script QA "web-max").</summary>
    public static int MaxHeight
    {
        get => int.TryParse(MacSettings.Get("webMaxHeight", "0"), out int h) ? h : 0;
        set => MacSettings.Set("webMaxHeight", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static readonly int[] StandardQualities = { 144, 240, 360, 480, 720, 1080, 1440, 2160, 4320 };

    /// <summary>Qualita' nominale come la chiama YouTube: 1920×1038 → 1080, 256×138 → 144, 1080×1920 (verticale) → 1080.</summary>
    public static int QualityOf(int width, int height)
    {
        if (height <= 0) return 0;
        if (width <= 0) width = height * 16 / 9;
        int shortSide = Math.Min(width, height), longSide = Math.Max(width, height);
        double nominal = Math.Max(shortSide, longSide * 9.0 / 16);
        return StandardQualities.OrderBy(q => Math.Abs(q - nominal)).First();
    }

    /// <summary>Elenco dei formati gia' letti per pagina: il cambio di qualita' non rilancia yt-dlp (10-15 s).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, Resolved Info)> Cache = new();
    private static readonly TimeSpan CacheLife = TimeSpan.FromHours(2); // gli indirizzi diretti di YouTube durano ~6 ore

    private static readonly System.Net.Http.HttpClient Probe = new(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = true })
    { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// YouTube risponde 403 a una parte dei flussi diretti (cambia da sessione a sessione e da formato a formato):
    /// si chiedono i primi byte, come fara' il lettore, e si scarta chi rifiuta.
    /// </summary>
    private static bool Reachable(string url, CancellationToken ct)
    {
        // Due tentativi: un 403 isolato capita anche sui flussi buoni (si sceglieva l'audio a 48 kbps).
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
                // Come il lettore vero (LAV Splitter e mpv leggono con FFmpeg).
                request.Headers.TryAddWithoutValidation("User-Agent", "Lavf/61.7.100");
                using var response = Probe.Send(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return false;
    }

    /// <summary>Il primo raggiungibile, in ordine di preferenza; si provano tre candidati alla volta.</summary>
    private static WebFormat? FirstReachable(System.Collections.Generic.IEnumerable<WebFormat> ordered, CancellationToken ct)
    {
        foreach (var batch in ordered.Chunk(3))
        {
            var ok = batch.AsParallel().WithCancellation(ct).Select(f => Reachable(f.Url, ct)).ToArray();
            for (int i = 0; i < batch.Length; i++) if (ok[i]) return batch[i];
            Console.Error.WriteLine("[web] formati rifiutati da YouTube: " + string.Join(",", batch.Where((_, i) => !ok[i]).Select(f => f.Id)));
        }
        return null;
    }

    /// <summary>
    /// Video e audio da usare: <paramref name="quality"/> 0 = Automatica (la migliore che risponde); altrimenti quella
    /// qualita' esatta, poi la piu' vicina sotto. A parita' di qualita': HDR, poi https (m3u8 in fondo), poi l'ordine di yt-dlp.
    /// </summary>
    private static Resolved? Pick(Resolved info, int quality, CancellationToken ct)
    {
        var formats = info.Formats ?? Array.Empty<WebFormat>();
        bool separateAudio = formats.Any(f => !f.HasVideo && f.HasAudio);
        var videos = formats.Where(f => f.HasVideo && (f.HasAudio || separateAudio) && (quality <= 0 || f.Quality <= quality))
            .OrderByDescending(f => quality > 0 && f.Quality == quality)
            .ThenByDescending(f => f.Quality).ThenByDescending(f => f.Hdr).ThenByDescending(f => f.Https).ThenByDescending(f => f.Index);
        var video = FirstReachable(videos, ct);
        if (video == null) return null;
        WebFormat? audio = null;
        if (!video.HasAudio)
        {
            // Per bitrate: con 251 e 140 rifiutati si finiva sul 139 (AAC 48 kbps) anche con l'HLS a 128 kbps disponibile.
            audio = FirstReachable(formats.Where(f => !f.HasVideo && f.HasAudio)
                .OrderByDescending(f => Math.Round(f.Kbps / 32)).ThenByDescending(f => f.Https).ThenByDescending(f => f.Index), ct);
            if (audio == null) return null;
        }
        Console.Error.WriteLine($"[web] scelto {video.Id} ({video.Quality}p{(video.Hdr ? " HDR" : "")})" + (audio != null ? " + audio " + audio.Id : "") + (quality > 0 ? $" per {quality}p" : " in automatico"));
        return info with { Video = video.Url, Audio = audio?.Url, Height = video.Quality };
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
    /// <param name="height">Qualita' nominale per YouTube (0 = Automatica).</param>
    public static Resolved? Resolve(string url, CancellationToken ct, out string? error, int? height = null, bool exact = false)
    {
        error = null;
        url = url.Trim();
        if (!IsWebAddress(url)) { error = "Non è un indirizzo web"; return null; }
        if (LooksDirect(url)) return new Resolved(url, null, null);
        bool youtube = IsYouTube(url);
        int quality = height ?? 0;
        // Cambio di qualita' (o riapertura) dello stesso video: i formati sono gia' noti, niente yt-dlp.
        if (youtube && Cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.At < CacheLife)
        {
            if (Pick(cached.Info, quality, ct) is { } fast) return fast;
            Cache.TryRemove(url, out _); // indirizzi scaduti o tutti rifiutati: si rilegge da YouTube
        }
        if (MacYtDlp.Ensure(ct) is not { } ytdlp) { error = MacYtDlp.MissingMessage; return null; }
        var outputArgs = youtube ? new[] { "--dump-single-json" } : new[] { "--print", "title", "--print", "urls" };
        var args = new[] { "--no-playlist", "--no-warnings", "--socket-timeout", "15", "-f", FormatSelector(youtube ? 0 : quality, exact) }
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
        if (youtube && exit == 0 && ParseYouTubeInfo(stdout) is { } info)
        {
            if (info.Formats is not { Length: > 0 }) return info;
            Cache[url] = (DateTime.UtcNow, info);
            if (Pick(info, quality, ct) is { } picked) return picked;
            error = "YouTube rifiuta tutti i flussi di questo video";
            return null;
        }
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
        static int Number(JsonElement value, string key) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) &&
            field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int n) ? n : 0;
        static int Height(JsonElement value) => QualityOf(Number(value, "width"), Number(value, "height"));
        static double Rate(JsonElement value)
        {
            foreach (string key in new[] { "abr", "tbr" })
                if (value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.GetDouble() > 0) return field.GetDouble();
            return 0;
        }
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
            var choices = formats.Select((f, i) => new WebFormat(Field(f, "format_id") ?? i.ToString(), Field(f, "url")!, Video(f) ? Height(f) : 0,
                Video(f), Audio(f), Field(f, "dynamic_range") is { } dr && dr.StartsWith("HDR", StringComparison.OrdinalIgnoreCase),
                Field(f, "protocol") is "https" or "http", i, Rate(f))).ToArray();
            return new Resolved(path, Field(audio, "url"), Field(root, "title"), h, heights, choices);
        }
        catch (JsonException) { return null; }
    }
}
