using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Pannello Info aggiornato in riproduzione: ogni 2 secondi bitrate attuale di video e audio (<see cref="LiveBitrate"/>),
/// fotogrammi persi e uscita. Solo a pannello aperto: chiuso, non si legge nulla (niente banda in piu' sui flussi di rete).
/// Video e audio si misurano ognuno per conto suo: su YouTube 4K il video impiega ~15 s, l'audio meno di un secondo.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherTimer? _liveInfoTimer;
    private int _liveVideoKbps, _liveAudioKbps;
    private bool _liveVideoBusy, _liveAudioBusy;
    private CancellationTokenSource _liveCts = new();

    /// <summary>Il pannello Info chiede i dati: parte il giro di aggiornamento (si ferma da solo a pannello chiuso).</summary>
    private void EnsureLiveInfo()
    {
        // Priorita' normale: in Background, con il video che ridisegna di continuo, il timer non scattava piu'.
        _liveInfoTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, (_, _) => LiveInfoTick());
        if (!_liveInfoTimer.IsEnabled) { _liveInfoTimer.Start(); LiveInfoTick(); }
    }

    /// <summary>File nuovo: le misure di prima non valgono piu'.</summary>
    private void ResetLiveInfo()
    {
        _liveCts.Cancel(); _liveCts = new CancellationTokenSource();
        _liveVideoKbps = 0; _liveAudioKbps = 0;
    }

    private void LiveInfoTick()
    {
        if (!_hud.InfoOpen) { _liveInfoTimer?.Stop(); _liveCts.Cancel(); _liveCts = new CancellationTokenSource(); return; }
        if (_closing || _file == null || !_video.IsRunning) return;
        string file = _file;
        long session = _video.SessionId;
        double position = _hud.Position;
        var (videoSource, audioSource, audioSelector) = LiveSources(file);
        if (!_liveVideoBusy && videoSource != null && (_probe?.HasVideo ?? true))
            Measure(videoSource, "v:0", position, file, session, video: true);
        if (!_liveAudioBusy && audioSource != null)
            Measure(audioSource, audioSelector, position, file, session, video: false);
    }

    private async void Measure(string source, string selector, double position, string file, long session, bool video)
    {
        var token = _liveCts.Token;
        if (video) _liveVideoBusy = true; else _liveAudioBusy = true;
        try
        {
            int kbps = await LiveBitrate.SampleAsync(source, selector, position, token);
            if (token.IsCancellationRequested || session != _video.SessionId || file != _file) return;
            if (Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null)
                Console.Error.WriteLine($"[info] bitrate attuale {(video ? "video" : "audio")} a {position:0.0} s: {kbps} kbps ({selector})");
            // Una misura fallita non cancella l'ultima buona: il numero non lampeggia.
            if (kbps <= 0) return;
            if (video) _liveVideoKbps = kbps; else _liveAudioKbps = kbps;
            RefreshInfoAsync();
        }
        finally { if (video) _liveVideoBusy = false; else _liveAudioBusy = false; }
    }

    /// <summary>
    /// Dove misurare: YouTube ha video e audio separati; un audio esterno scelto e' un altro file; altrimenti il file
    /// (o il flusso di rete con l'accesso) con la traccia audio attiva.
    /// </summary>
    private (string? Video, string? Audio, string AudioSelector) LiveSources(string file)
    {
        if (MacDisc.TryResolve(file, out _)) return (null, null, "a:0"); // dischi e immagini: ffprobe non ci arriva
        string source = IsStream(file) ? CinecorePlayer2025.Utilities.JellyfinClient.WithAccess(file) : file;
        if (WebAudioFor(file) is { } webAudio) return (source, webAudio, "a:0");
        if (_video.ExternalAudio is { Length: > 0 } external) return (source, external, "a:0");
        int ordinal = Math.Max(0, SelectedAudioOrdinal());
        bool noAudio = _probe is { } probe && probe.AudioCodec.Length == 0; // file senza audio: niente ffprobe a vuoto
        return (source, noAudio ? null : source, "a:" + ordinal);
    }
}
