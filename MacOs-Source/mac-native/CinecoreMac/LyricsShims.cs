using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace CinecorePlayer2025.Utilities;

/// <summary>
/// Quello che MusicLyricsService chiede a Media.cs di Windows (FFmpeg.AutoGen): sul Mac tag e durata arrivano da ffprobe.
/// </summary>
internal static class MediaProbe
{
    public sealed record AudioTags
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public double? DurationMinutes { get; set; }
        public int? TrackNumber { get; set; }
        public int? ArtworkStream { get; set; }
        public bool ReadFromFile { get; set; }
        public string? FileTitle { get; set; }
        public string? FileArtist { get; set; }
        /// <summary>Anno e genere dai tag del file (date/year/originaldate, genre).</summary>
        public int? Year { get; set; }
        public string? Genre { get; set; }
        /// <summary>2 = letti anche anno e genere: le letture salvate prima si rifanno una volta.</summary>
        public int TagVersion { get; set; }
    }

    public sealed class Result { public double Duration { get; set; } }

    private static (AudioTags Tags, double Duration) Read(string path, CancellationToken ct)
    {
        var tags = new AudioTags();
        double duration = 0;
        // Traccia di un CD audio: i dati del disco (MusicBrainz), le AIFF di cddafs non hanno tag (testi non trovati).
        if (CinecoreMac.MacAudioCd.TrackInfo(path) is { } cd)
        {
            tags.Title = cd.Title; tags.Artist = cd.Artist; tags.AlbumArtist = cd.Artist; tags.Album = cd.Album;
            tags.TagVersion = CurrentTagVersion;
            if (cd.Seconds > 0) tags.DurationMinutes = cd.Seconds / 60;
            return (tags, cd.Seconds);
        }
        if (CinecoreMac.MacTools.Ffprobe is not { } exe || !File.Exists(path)) return (tags, duration);
        var result = CinecoreMac.ProcessLauncher.Run(exe,
            ["-v", "error", "-of", "json", "-show_entries",
             "format=duration:format_tags:stream=index,codec_type:stream_disposition=attached_pic:stream_tags", path],
            background: false, timeoutMs: 10000, ct, adjustPriority: false);
        if (result.Exit != 0) return (tags, duration);
        using var doc = JsonDocument.Parse(result.Stdout);
        if (doc.RootElement.TryGetProperty("streams", out var streams))
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio")
                    ReadTags(stream, tags);
                if (tags.ArtworkStream == null && stream.TryGetProperty("disposition", out var disposition) &&
                    disposition.TryGetProperty("attached_pic", out var attached) && attached.GetInt32() == 1 &&
                    stream.TryGetProperty("index", out var index))
                    tags.ArtworkStream = index.GetInt32();
            }
        if (doc.RootElement.TryGetProperty("format", out var format))
        {
            ReadTags(format, tags);
            if (format.TryGetProperty("duration", out var d))
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        }
        if (duration > 0) tags.DurationMinutes = duration / 60;
        tags.ReadFromFile = true;
        tags.TagVersion = CurrentTagVersion;
        tags.FileTitle = tags.Title;
        tags.FileArtist = tags.Artist;
        return (tags, duration);
    }

    public const int CurrentTagVersion = 2;

    /// <summary>L'anno da "1997", "1997-05-21", "21/05/1997"...: il primo numero di quattro cifre plausibile.</summary>
    public static int? YearOf(string? value)
    {
        var m = System.Text.RegularExpressions.Regex.Match(value ?? "", @"(?<!\d)(1[89]\d\d|20\d\d|2100)(?!\d)");
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    private static void ReadTags(JsonElement metadata, AudioTags tags)
    {
        if (!metadata.TryGetProperty("tags", out var entries)) return;
        foreach (var entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String || entry.Value.GetString()?.Trim() is not { Length: > 0 } value) continue;
            switch (entry.Name.ToLowerInvariant())
            {
                case "title": tags.Title = value; break;
                case "artist": tags.Artist = value; break;
                case "album_artist": case "albumartist": case "album artist": tags.AlbumArtist = value; break;
                case "album": tags.Album = value; break;
                case "date": case "year": case "originaldate": case "original_date": case "tdrc": case "tyer": case "tdor": case "release_date":
                    tags.Year ??= YearOf(value);
                    break;
                case "genre":
                    if (tags.Genre == null && !value.StartsWith('[')) tags.Genre = value;
                    break;
                case "track": case "tracknumber":
                    if (int.TryParse(value.Split('/')[0], out int track) && track > 0) tags.TrackNumber = track;
                    break;
            }
        }
    }

    public static byte[]? ReadEmbeddedAudioArtwork(string path, AudioTags tags)
    {
        if (tags.ArtworkStream is not int stream || CinecoreMac.MacTools.Ffmpeg is not { } exe) return null;
        var result = CinecoreMac.ProcessLauncher.Run(exe,
            ["-v", "error", "-i", path, "-map", "0:" + stream.ToString(CultureInfo.InvariantCulture),
             "-frames:v", "1", "-c:v", "copy", "-f", "image2pipe", "-"],
            background: false, timeoutMs: 10000, CancellationToken.None, adjustPriority: false);
        return result.Exit == 0 && result.Stdout.Length is > 0 and <= 16 * 1024 * 1024 ? result.Stdout : null;
    }

    public static AudioTags ReadAudioTags(string path, CancellationToken ct = default) => Read(path, ct).Tags;
    public static Result Probe(string path) => new() { Duration = Read(path, CancellationToken.None).Duration };
}

/// <summary>Niente CD audio sul Mac: nessun file e' un segnaposto di traccia del CD.</summary>
internal static class AudioCd
{
    public static bool IsStub(string? path) => false;
    public static string? ExportForAnalysis(string stubPath, CancellationToken ct) => null;
}

/// <summary>
/// GeniusBrowserSession di Windows (WebView2) con WebKit: quando genius.com risponde 403 alla richiesta diretta, una
/// WKWebView nascosta (cookie persistenti) legge la pagina; se il sito vuole una verifica, Strumenti › "Genius: verifica
/// connessione…" la mostra in una finestra. Il codice nativo e' in mac/MacWebSession.m (libCinecoreGlass.dylib).
/// </summary>
internal static class GeniusBrowserSession
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TextCallback(IntPtr text, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ClosedCallback();
    [DllImport("libCinecoreGlass.dylib")] private static extern void CinecoreWebFetch([MarshalAs(UnmanagedType.LPUTF8Str)] string url, double timeoutSeconds, TextCallback callback, IntPtr context);
    [DllImport("libCinecoreGlass.dylib")] private static extern void CinecoreWebShowVerification(ClosedCallback closed);

    private static readonly TextCallback OnText = (text, context) =>
    {
        var handle = GCHandle.FromIntPtr(context);
        var done = (TaskCompletionSource<string?>)handle.Target!;
        handle.Free();
        done.TrySetResult(text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text));
    };
    private static readonly ClosedCallback OnClosed = () => GeniusAvailability.Reset();

    /// <summary>Su Windows la sessione e' una WebView2 (GeniusWebSessionWin), se il runtime c'e'.</summary>
    public static bool Available =>
#if CINECORE_WINDOWS
        _winAvailable ??= GeniusWebSessionWin.RuntimeInstalled;
    private static bool? _winAvailable;
#else
        true;
#endif

    public static async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        // [2026-11-22] Su Windows chiamava libCinecoreGlass.dylib a ogni 403 di Genius: eccezione nel log e poi un
        // invito a una verifica che non esiste. Ora si lascia perdere subito e il testo arriva dagli altri fornitori.
        if (!Available) return null;
#if CINECORE_WINDOWS
        string? fetched;
        try { fetched = await GeniusWebSessionWin.GetTextAsync(url, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Console.Error.WriteLine("[GENIUS] sessione web: " + ex.Message); fetched = null; }
        if (fetched == null) { Console.Error.WriteLine("[GENIUS] serve la verifica: Strumenti › Genius: verifica connessione…"); GeniusAvailability.PauseForVerification(); }
        return fetched;
#else
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { CinecoreWebFetch(url, 8, OnText, GCHandle.ToIntPtr(GCHandle.Alloc(done))); }
        catch (Exception ex) { Console.Error.WriteLine("[GENIUS] sessione web non disponibile: " + ex.Message); return null; }
        string? text;
        try { text = await done.Task.WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false); }
        catch (TimeoutException) { text = null; }
        // Nessuna pagina utile in tempo: il sito vuole una verifica a mano (come Windows: pausa e voce di menu).
        if (text == null) { Console.Error.WriteLine("[GENIUS] serve la verifica: Strumenti › Genius: verifica connessione…"); GeniusAvailability.PauseForVerification(); }
        return text;
#endif
    }

    public static void ShowVerification()
    {
        if (!Available) return;
        GeniusAvailability.Reset();
#if CINECORE_WINDOWS
        GeniusWebSessionWin.ShowVerification(() => GeniusAvailability.Reset());
#else
        try { CinecoreWebShowVerification(OnClosed); } catch (Exception ex) { Console.Error.WriteLine("[GENIUS] " + ex.Message); }
#endif
    }
}
