using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Video dal web (YouTube e simili), come PlayerForm.PlaybackOpen di Windows: l'indirizzo si risolve con yt-dlp, il video
/// va al motore come un flusso e l'audio, quando arriva a parte, come traccia separata (audio-add in mpv, secondo
/// LAV Splitter nel VideoHost per madVR e MPC-VR).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Audio separato del video web in corso (vale finche' si riapre lo stesso indirizzo, es. cambio renderer).</summary>
    private (string Video, string Audio)? _webAudio;
    private CancellationTokenSource? _webResolve;
    /// <summary>La pagina (es. il link di YouTube) del video web in corso e l'indirizzo diretto che ne e' uscito.</summary>
    private string? _webPage, _webPageVideo;
    private int _webPlayingMaxHeight;
    private int _webResolvedHeight;
    private int[] _webHeights = Array.Empty<int>();

    private void WireWeb()
    {
        _web.Closed += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _web.AddressChosen += url => OpenWebAddress(url);
        _goTo.Closed += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _goTo.TimeChosen += seconds => { if (_video.IsRunning && _file != null) { SeekTo(seconds); _osd.Show("Vai a " + Fmt(seconds), 1800); } };
        // YouTube e' una pagina della libreria (come Windows): il video scelto si apre nel player.
        _library.YouTubeVideoChosen += (url, title) => OpenWebAddress(url, title.Length > 0 ? title : null);
        _library.OpenLinkRequested += _web.Open;
        _youtube.Closed += () => { if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _youtube.VideoChosen += (url, title) => { _youtube.Close(); OpenWebAddress(url, title.Length > 0 ? title : null); };
    }

    private void CancelWebResolve()
    {
        var pending = _webResolve;
        _webResolve = null;
        pending?.Cancel();
    }

    internal void OpenWebAddress(string url, string? title = null, bool resume = false, int? requestedHeight = null)
    {
        // Jellyfin ed Emby hanno gia' i loro flussi: si aprono come sempre.
        if (CinecorePlayer2025.Utilities.JellyfinClient.TryParseStream(url, out _, out _, out _)) { StartVideo(url, title); return; }
        CancelWebResolve();
        var cts = _webResolve = new CancellationTokenSource();
        string? resumeFile = resume ? _file : null;
        int height = requestedHeight ?? MacWebResolver.MaxHeight;
        if (!resume)
        {
            ShowLibrary(false);
            // Subito la schermata di caricamento, che poi prosegue con il film (prima: player vuoto, poi di nuovo caricamento).
            _loading.ShowResolving(title ?? (MacWebResolver.IsYouTube(url) ? "YouTube" : "Video dal web"),
                MacWebResolver.IsYouTube(url) ? "Risolvo il video…" : "Apro l'indirizzo…");
            LoadYouTubeArt(url, CancellationToken.None);
        }
        else _osd.Show(MacWebResolver.IsYouTube(url) ? "Risolvo YouTube…" : "Apro l'indirizzo…", 4000);
        _ = Task.Run(() =>
        {
            MacWebResolver.Resolved? resolved;
            string? error;
            try { resolved = MacWebResolver.Resolve(url, cts.Token, out error, height, exact: resume && height > 0); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Dispatcher.UIThread.Post(cts.Dispose);
                return;
            }
            catch (Exception ex) { resolved = null; error = ex.GetBaseException().Message; }
            Dispatcher.UIThread.Post(() =>
            {
                bool current = _webResolve == cts;
                if (current) _webResolve = null;
                cts.Dispose();
                if (!current || _closing || resumeFile != null && resumeFile != _file) return;
                if (resolved == null)
                {
                    if (!resume) _loading.Hide();
                    _osd.Show("Video non disponibile" + (error != null ? ": " + error : ""), 5000);
                    if (!_video.IsRunning) ShowLibrary(true);
                    return;
                }
                double position = resume ? _hud.Position : 0;
                bool paused = resume && !_hud.IsPlaying;
                _webAudio = resolved.Audio != null ? (resolved.Video, resolved.Audio) : null;
                _webPage = url; _webPageVideo = resolved.Video;
                _webHeights = resolved.Heights ?? Array.Empty<int>();
                _webResolvedHeight = resolved.Height;
                _webPlayingMaxHeight = height > 0 ? resolved.Height : 0;
                if (requestedHeight.HasValue) MacWebResolver.MaxHeight = requestedHeight.Value;
                Console.Error.WriteLine($"[web] video risolto audio={(resolved.Audio != null ? "separato" : "-")} titolo={resolved.Title}");
                if (resume) RestartStream(resolved.Video, title ?? resolved.Title ?? _hud.Title, position, paused);
                else StartVideo(resolved.Video, title ?? resolved.Title ?? "Video dal web");
            });
        });
    }

    private static readonly System.Net.Http.HttpClient ThumbHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>L'ID del video dall'indirizzo di YouTube (watch?v=, youtu.be/, shorts/, embed/, live/).</summary>
    internal static string? YouTubeId(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// La miniatura di YouTube nella schermata di caricamento (e poi come foto del video), subito dall'ID: non serve
    /// aspettare yt-dlp. Prima la grande (maxres), se manca la hq.
    /// </summary>
    private void LoadYouTubeArt(string url, CancellationToken ct)
    {
        if (YouTubeId(url) is not { } id) return;
        _ = Task.Run(async () =>
        {
            string folder = System.IO.Path.Combine(LibraryStore.AppDataDir, "youtube-thumbs");
            string file = System.IO.Path.Combine(folder, "video-" + id + ".jpg");
            try
            {
                if (!System.IO.File.Exists(file))
                {
                    System.IO.Directory.CreateDirectory(folder);
                    byte[]? bytes = null;
                    foreach (string name in new[] { "maxresdefault", "hqdefault" })
                    {
                        using var response = await ThumbHttp.GetAsync($"https://i.ytimg.com/vi/{id}/{name}.jpg", ct);
                        if (response.IsSuccessStatusCode) { bytes = await response.Content.ReadAsByteArrayAsync(ct); break; }
                    }
                    if (bytes is not { Length: > 1000 }) return;
                    await System.IO.File.WriteAllBytesAsync(file, bytes, ct);
                }
                Dispatcher.UIThread.Post(() => { _webArt = (id, file); if (_loading.IsVisible) _loading.SetArt(file); });
            }
            catch { }
        });
    }

    /// <summary>Miniatura dell'ultimo video di YouTube aperto (per HUD e scheda Info).</summary>
    private (string Id, string File)? _webArt;

    /// <summary>Video aperto da un indirizzo web: l'indirizzo diretto scade, niente Diario ne' punto di ripresa (come Windows).</summary>
    private bool IsWebVideo(string file) => file == _webPageVideo;
    private bool IsYouTubeVideo => _file != null && _file == _webPageVideo && _webPage is { } page && MacWebResolver.IsYouTube(page);

    private string? WebAudioFor(string file) => _webAudio is { } web && web.Video == file ? web.Audio : null;
}
