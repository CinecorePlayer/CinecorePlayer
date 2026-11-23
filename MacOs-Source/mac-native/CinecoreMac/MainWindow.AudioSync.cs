using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Sincronizzazione audio: la scheda (AudioSyncView) e il ritardo "a curva" seguito durante la visione
/// (StartAudioDelayFollower di Windows). Tutto passa dal comando di ritardo audio del motore (audio-offset).
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherTimer? _delayFollower;
    private int _delayFollowApplied = int.MinValue;
    private CancellationTokenSource? _remoteAudioSync;
    private double _remoteAudioProgress;
    private string? _remoteAudioNote;
    private string? _remoteAudioFile;

    private ExternalAudioBinding? RemoteAudioBinding() =>
        _file is { } file && !IsStream(file) ? ExternalAudioStore.Get(file) : null;

    private bool CanRemoteAudioSync() =>
        _file is { } file && !IsStream(file) && _video.IsRunning && _hud.Duration > 0 &&
        _video.ActiveDvdNav == null && (_tracks.Count(t => t.Group == 1 && !IsExternalTrack(t)) > 1 ||
            _video.ExternalAudio != null && !IsStream(_video.ExternalAudio));

    private void StartRemoteAudioSync()
    {
        if (_remoteAudioSync != null || !CanRemoteAudioSync() || _file == null) return;
        string file = _file;
        double duration = _hud.Duration;
        string? external = _video.ExternalAudio;
        int count = _tracks.Count(t => t.Group == 1 && !IsExternalTrack(t));
        if (count == 0 || !File.Exists(file) || external != null && !File.Exists(external)) return;
        int selected = SelectedAudioOrdinal();
        int target = Math.Clamp(selected, 0, count - 1);
        int reference = Math.Clamp(RemoteAudioBinding()?.ReferenceOrdinal ?? 0, 0, count - 1);
        if (external == null && reference == target) reference = (target + 1) % count;
        int token = _loadToken;
        var job = _remoteAudioSync = new CancellationTokenSource();
        _remoteAudioFile = file;
        _remoteAudioProgress = 0;
        _remoteAudioNote = null;
        var progress = new Progress<ExternalAudioSynchronization.Step>(step =>
        {
            if (_remoteAudioSync == job) _remoteAudioProgress = Math.Clamp(step.Fraction, 0, 1);
        });
        _ = RunRemoteAudioSyncAsync();

        async Task RunRemoteAudioSyncAsync()
        {
            try
            {
                var result = await Task.Run(() => ExternalAudioSynchronization.AnalyzeAsync(file, external ?? file, reference,
                    duration, false, progress, job.Token, external != null ? 0 : target));
                if (_remoteAudioSync != job || _loadToken != token || SelectedAudioOrdinal() != selected || external != _video.ExternalAudio) return;
                if (result.SpeedReliable && Math.Abs(result.SpeedRatio - 1) > 1e-6)
                    _remoteAudioNote = "Velocità diversa. Apri Sincronizzazione audio sul Mac per convertire la traccia.";
                else if (!result.Reliable)
                    _remoteAudioNote = "Risultato incerto. Controlla il ritardo sul Mac.";
                else
                {
                    var binding = new ExternalAudioBinding(external, result.CorrectionMs, reference, target, SyncStatus.Verified,
                        result.Confidence, DateTime.UtcNow, result.Curve?.Select(p => p.Time).ToArray(),
                        result.Curve?.Select(p => p.Ms - result.CorrectionMs).ToArray());
                    _remoteAudioNote = binding.HasCurve ? "Audio allineato con ritardo variabile." : $"Audio allineato: {result.CorrectionMs:+0;-0;0} ms";
                    ApplyAudioBinding(binding, target, _remoteAudioNote);
                }
            }
            catch (OperationCanceledException) when (job.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (_remoteAudioSync == job) _remoteAudioNote = "Analisi audio non riuscita.";
                Console.Error.WriteLine("telecomando, sincronia audio: " + ex.GetType().Name);
            }
            finally
            {
                if (_remoteAudioSync == job) _remoteAudioSync = null;
                job.Dispose();
            }
        }
    }

    private void StopRemoteAudioSync()
    {
        if (_remoteAudioSync == null) return;
        var job = _remoteAudioSync;
        _remoteAudioSync = null;
        job.Cancel();
        _remoteAudioNote = "Analisi audio fermata.";
    }

    private int SelectedAudioOrdinal() => _tracks.Where(t => t.Group == 1).ToList().FindIndex(t => t.Selected);

    private void WireAudioSync()
    {
        _audioSync.PositionProvider = () => _hud.Position;
        _audioSync.PreviewDelay = ms => _video.Send("audio-offset " + ms.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _audioSync.Cancelled = () => _video.Send("audio-offset " + _audioOffsetMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _audioSync.Apply = (binding, target) => ApplyAudioBinding(binding, target, announce: null);
        _audioSync.FinishedInBackground = (video, binding, target, reliable, text) =>
        {
            // Applicato subito solo se si sta ancora guardando quel film con quella traccia.
            if (reliable && video == _file && (binding.AudioPath != null ? binding.AudioPath == _video.ExternalAudio : SelectedAudioOrdinal() == target)) ApplyAudioBinding(binding, target, text);
            else if (video == _file) _osd.Show(reliable ? "Analisi audio pronta, apri Sincronizzazione audio" : text, 4200);
        };
        _audioSync.Closed += () => { _hud.Focus(); _hud.Poke(); };
    }

    private void OpenAudioSync()
    {
        if (_file == null || !_video.IsRunning) return;
        var audio = _tracks.Where(t => t.Group == 1).ToList();
        var names = audio.Select((t, i) => SubtitleNameNormalizer.NormalizeAudioTrackName(t.Name, i + 1).Replace(" • ", ", ").Replace(" · ", ", ")).ToList();
        _hud.ResetPanels();
        _audioSync.Open(_file, names, SelectedAudioOrdinal(), _hud.Duration);
    }

    /// <summary>Traccia audio esterna salvata per il film (file sul Mac): il motore la apre come traccia separata.</summary>
    private static string? ExternalTrackFor(string file) =>
        !IsStream(file) && ExternalAudioStore.Get(file)?.AudioPath is { } path && File.Exists(path) ? path : null;

    private int? _audioOrdinalOnReady;

    private bool IsExternalTrack((int Index, int Group, bool Selected, string Name) t) =>
        _video.ExternalAudio != null && t.Group == 1 && (t.Index == 900 || t.Name.Contains("Audio esterno", StringComparison.Ordinal));

    /// <summary>
    /// Scelta di una traccia audio (menu, telefono). Con la traccia esterna attiva, scegliere una traccia del film la toglie:
    /// il film riparte dallo stesso punto con la traccia scelta (madVR e MPC-VR non hanno l'audio del file collegato).
    /// </summary>
    private void SelectAudioTrack(int index)
    {
        var audio = _tracks.Where(t => t.Group == 1).ToList();
        int ordinal = audio.FindIndex(t => t.Index == index);
        if (_file is { } file && ordinal >= 0 && _video.ExternalAudio != null && WebAudioFor(file) == null && !IsExternalTrack(audio[ordinal]))
        {
            if (_video.ExternalAudio is { } loaded && !loaded.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ExternalAudioStore.Set(file, null);
            double at = _hud.Position;
            _audioOffsetMs = 0;
            StartVideo(file, _titleOverride);
            _resumeOnReady = at > 1 ? at : null;
            _audioOrdinalOnReady = ordinal;
            return;
        }
        _video.Send("select " + index);
    }

    private void ApplyAudioBinding(ExternalAudioBinding binding, int target, string? announce)
    {
        if (_file is not { } file) return;
        // Traccia esterna nuova (o tolta): si salva e il film riparte dallo stesso punto con la traccia giusta.
        if (!string.Equals(binding.AudioPath, _video.ExternalAudio, StringComparison.Ordinal) && WebAudioFor(file) == null)
        {
            ExternalAudioStore.Set(file, binding);
            double at = _hud.Position;
            _audioOffsetMs = binding.DelayAt(at);
            StartVideo(file, _titleOverride);
            _resumeOnReady = at > 1 ? at : null;
            if (announce != null) _osd.Show(announce, 3600);
            else _osd.Show(binding.AudioPath != null ? "Traccia esterna: " + Path.GetFileName(binding.AudioPath) : "Traccia del film", 2600);
            return;
        }
        var audio = _tracks.Where(t => t.Group == 1).ToList();
        if (binding.AudioPath == null && target >= 0 && target < audio.Count && !audio[target].Selected) _video.Send("select " + audio[target].Index);
        _audioOffsetMs = binding.DelayAt(_hud.Position);
        _video.Send("audio-offset " + _audioOffsetMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Nessun allineamento residuo: si torna alla sincronizzazione originale del file.
        ExternalAudioStore.Set(file, binding.AudioPath == null && binding.DelayMs == 0 && !binding.HasCurve && target == SelectedAudioOrdinal() ? null : binding);
        _delayFollowApplied = int.MinValue;
        StartDelayFollower();
        if (announce != null) _osd.Show(announce, 3600);
    }

    /// <summary>Una traccia sincronizzata a tratti non ha un ritardo solo: una volta al secondo si passa a quello del punto.</summary>
    private void StartDelayFollower()
    {
        _delayFollower ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_file == null || !_video.IsRunning || _audioSync.IsOpen) return;
            if (ExternalAudioStore.Get(_file) is not { HasCurve: true } binding || binding.AudioPath != null && binding.AudioPath != _video.ExternalAudio) { _delayFollowApplied = int.MinValue; return; }
            int wanted = binding.DelayAt(_hud.Position);
            if (_delayFollowApplied != int.MinValue && Math.Abs(wanted - _delayFollowApplied) < 12) return;
            _audioOffsetMs = wanted;
            _video.Send("audio-offset " + wanted.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _delayFollowApplied = wanted;
        });
        if (!_delayFollower.IsEnabled) _delayFollower.Start();
    }
}
