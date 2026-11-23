using System;
using System.IO;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// Traduzione di HUD/PlayerForm.IntroOutro.cs: i marcatori di intro e finale degli episodi (IntroOutroScanService,
// risultati del rilevatore) fanno comparire "Salta intro" e "Prossimo episodio".
public partial class MainWindow
{
    private IntroOutroEpisodeMarkers? _introOutro;
    private LibraryItem? _nextDlnaEpisode;
    private bool _skipIntroConsumed, _nextEpisodeConsumed;

    private void ResetIntroOutro(string file)
    {
        string identity = StreamIdentity(file);
        _introOutro = IntroOutroScanService.TryGetEpisodeMarkers(identity);
        _nextDlnaEpisode = IsStream(identity) ? NetworkStore.QueueDlnaIntroOutro(identity) : null;
        _skipIntroConsumed = _nextEpisodeConsumed = false;
        _hud.SetIntroOutroPrompts(false, false);
    }

    private void OnIntroOutroScanStatusChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_closing || _file is not { } file) return;
        _introOutro = IntroOutroScanService.TryGetEpisodeMarkers(StreamIdentity(file));
        UpdateIntroOutroPrompts(_hud.Position);
    }, DispatcherPriority.Background);

    /// <summary>UpdateIntroOutroPromptForPlayback: a ogni posizione nuova.</summary>
    private void UpdateIntroOutroPrompts(double pos)
    {
        if (_introOutro == null || !_hud.VideoVisible || _loading.Active) { _hud.SetIntroOutroPrompts(false, false); return; }
        var intro = _introOutro.Intro; var outro = _introOutro.Outro;
        double duration = _hud.Duration > 0 ? _hud.Duration : Math.Max(intro?.EndSeconds ?? 0, outro?.EndSeconds ?? 0);
        if (intro != null && pos < Math.Max(0, intro.StartSeconds - 5)) _skipIntroConsumed = false;
        if (outro != null && pos < Math.Max(0, outro.StartSeconds - 10)) _nextEpisodeConsumed = false;
        bool showSkip = intro != null && !_skipIntroConsumed && intro.EndSeconds > intro.StartSeconds + 1
            && pos >= Math.Max(0, intro.StartSeconds - 2) && pos < intro.EndSeconds - .25;
        double nextEnd = duration > 0 ? Math.Max(0, duration - .25) : outro?.EndSeconds ?? double.MaxValue;
        bool showNext = outro != null && HasNextEpisode() && !_nextEpisodeConsumed && outro.EndSeconds > outro.StartSeconds + 1
            && pos >= Math.Max(0, outro.StartSeconds - 2) && pos < nextEnd;
        _hud.SetIntroOutroPrompts(showSkip, showNext);
    }

    private bool HasNextEpisode()
    {
        if (_videoQueue.Count > 0 || _nextDlnaEpisode != null) return true;
        try { return IntroOutroScanService.TryGetNextEpisodePath(_file) is { } next && File.Exists(next); } catch { return false; }
    }

    private void SkipDetectedIntro()
    {
        if (_introOutro?.Intro is not { } intro) return;
        _skipIntroConsumed = true;
        _hud.SetIntroOutroPrompts(false, false);
        SeekTo(intro.EndSeconds + .25);
    }

    private void OpenNextEpisodeFromPrompt()
    {
        _nextEpisodeConsumed = true;
        _hud.SetIntroOutroPrompts(false, false);
        SaveResumePoint();
        if (_videoQueue.Count > 0) { var next = _videoQueue[0]; _videoQueue.RemoveAt(0); StartVideo(next.Path, next.Title); return; }
        if (_nextDlnaEpisode is { } episode) { StartVideo(episode.Path, episode.Title); return; }
        try
        {
            if (IntroOutroScanService.TryGetNextEpisodePath(_file) is { } path && File.Exists(path))
                StartVideo(path, _library.ItemFor(path)?.Title);
        }
        catch { }
    }
}
