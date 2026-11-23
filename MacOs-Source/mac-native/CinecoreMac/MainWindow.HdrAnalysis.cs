using System;
using System.IO;

namespace CinecoreMac;

/// <summary>
/// Strumenti › Analisi HDR… (ShowHdrAnalysis di Windows): la scheda legge il file con un ffmpeg a parte, quindi il film
/// continua; la timeline della scheda e il clic sul grafico spostano la riproduzione con i comandi normali (Seek).
/// </summary>
public sealed partial class MainWindow
{
    private string? _hdrFile;

    private void WireHdrAnalysis()
    {
        _hdr.MenuRequested += (entries, at) => ShowTextMenu(_hdr, entries, at);
        _hdr.Closed += () => { _hdrFile = null; ApplyVolumeLevel(); if (_library.IsVisible) _library.Focus(); else { _hud.Focus(); _hud.Poke(); } };
        _hdr.DurationProvider = () => _hud.Duration;
        _hdr.IsPlayingProvider = () => _hud.IsPlaying;
        _hdr.TogglePlay = () => { if (HdrFileIsPlaying()) TogglePlay(); };
        _hdr.Seek = seconds => { if (HdrFileIsPlaying()) SeekTo(seconds); };
    }

    private bool HdrFileIsPlaying() => _hdrFile != null && _video.IsRunning && string.Equals(_file, _hdrFile, StringComparison.Ordinal);

    /// <summary>Il film in riproduzione (durante la demo, il film che segue), se e' un file sul Mac.</summary>
    private string? HdrCandidate()
    {
        string? path = _afterDemo?.File ?? _file;
        // Ogni video salvato sul Mac: HDR (PQ, HLG, Dolby Vision) e anche SDR (scala BT.1886 a 100 nit).
        return path != null && !IsStream(path) && (_afterDemo != null ? File.Exists(path) : _probe is { HasVideo: true }) ? path : null;
    }

    private static bool IsHdrProbe(ProbeData? p) => p is { HasVideo: true } &&
        (p.Transfer is "smpte2084" or "arib-std-b67" || p.DolbyVisionProfile > 0);

    private void OpenHdrAnalysis() => OpenHdrAnalysis(_afterDemo?.File ?? _file, _afterDemo?.Title ?? _hud.Title);

    internal void OpenHdrAnalysis(string? path, string? title)
    {
        if (string.IsNullOrWhiteSpace(path) || IsStream(path) || !File.Exists(path))
        {
            _osd.Show(path != null && IsStream(path) ? "Analisi HDR: serve un video salvato sul Mac (non in streaming)" : "Analisi HDR: apri prima un video salvato sul Mac", 2600);
            return;
        }
        _hdrFile = path;
        ApplyVolumeLevel(); // l'analisi non si ascolta
        _hud.ResetPanels();
        _hdr.PositionProvider = string.Equals(_file, path, StringComparison.Ordinal) && _video.IsRunning ? () => HdrFileIsPlaying() ? _hud.Position : 0 : null;
        string? backdrop = null;
        try { backdrop = CinecorePlayer2025.Utilities.MovieMetadataService.GetCachedBackdropPath(path); } catch { }
        _hdr.Open(path, string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title!, backdrop);
    }
}
