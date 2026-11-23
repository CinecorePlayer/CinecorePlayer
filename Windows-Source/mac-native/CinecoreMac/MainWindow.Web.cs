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
    /// <summary>Copertina (file locale) del video web in caricamento: StartVideo la ripassa alla schermata di caricamento.</summary>
    private string? _webCover;
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
        // Ogni video parte in Automatica; la qualita' scelta dal menu vale solo per il video in corso.
        int height = requestedHeight ?? 0;
        if (resume) _osd.Show(height > 0 ? $"Passo a {height}p…" : "Passo alla qualità automatica…", 4000);
        // Tutto dentro la schermata di caricamento: prima si entrava nel player vuoto mentre si risolveva
        // l'indirizzo, poi si usciva per caricare. Al cambio di risoluzione il video continua finche' non e' pronto.
        if (!resume)
        {
            // Copertina di YouTube subito (miniatura gia' in cache) o appena scaricata, come per i film della libreria.
            _webCover = MacWebResolver.IsYouTube(url)
                ? _library.YouTubeCover(url, file => { if (_webResolve == cts || _webPage == url) { _webCover = file; _loading.UpdateArt(null, file); } })
                : null;
            _loading.Show(title ?? (MacWebResolver.IsYouTube(url) ? "YouTube" : "Video dal web"), _webCover, LoadingRendererName());
            // Prima tappa dello stesso caricamento: la barra prosegue poi con le fasi del motore (StartVideo).
            _loading.Stage(.2, "");
            _loading.SetMessage(MacWebResolver.IsYouTube(url) ? "Preparo il video di YouTube…" : "Apro l'indirizzo…");
            ShowLibrary(false);
        }
        _ = Task.Run(() =>
        {
            MacWebResolver.Resolved? resolved;
            string? error;
            try { resolved = MacWebResolver.Resolve(url, cts.Token, out error, height); }
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
                if (resume && height > 0 && resolved.Height != height) _osd.Show($"{height}p non disponibile: {resolved.Height}p", 4000);
                Console.Error.WriteLine($"[web] video risolto audio={(resolved.Audio != null ? "separato" : "-")} titolo={resolved.Title}");
                if (resume) RestartStream(resolved.Video, title ?? resolved.Title ?? _hud.Title, position, paused);
                else StartVideo(resolved.Video, title ?? resolved.Title ?? "Video dal web");
            });
        });
    }

    private string LoadingRendererName()
    {
        string r = Environment.GetEnvironmentVariable("CINECORE_RENDERER") ?? _remotePlaybackRenderer ?? MacSettings.EffectiveRenderer;
        return r == "madvr" ? "madVR" : r == "mpcvr" ? "MPC Video Renderer" : "MPV";
    }

    /// <summary>Video aperto da un indirizzo web: l'indirizzo diretto scade, niente Diario ne' punto di ripresa (come Windows).</summary>
    private bool IsWebVideo(string file) => file == _webPageVideo;
    private bool IsYouTubeVideo => _file != null && _file == _webPageVideo && _webPage is { } page && MacWebResolver.IsYouTube(page);

    private string? WebCoverFor(string file) => IsWebVideo(file) ? _webCover : null;

    private string? WebAudioFor(string file) => _webAudio is { } web && web.Video == file ? web.Audio : null;
}
