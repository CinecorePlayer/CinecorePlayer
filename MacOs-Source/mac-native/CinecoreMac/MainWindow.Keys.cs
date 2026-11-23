using System;
using System.IO;
using System.Linq;
using Avalonia.Input;

namespace CinecoreMac;

// Scorciatoie del player che mancavano (FUNZIONALITA.md, capitolo 16): capitoli, passo singolo, ritardo audio,
// chiudi e torna alla libreria, apri.
public sealed partial class MainWindow
{
    /// <summary>True se il tasto e' stato usato.</summary>
    private bool HandlePlayerShortcut(KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.PageUp: JumpChapter(+1); return true;
            case Key.PageDown: JumpChapter(-1); return true;
            case Key.OemPeriod when !ctrl: FrameStep(+1); return true;
            case Key.OemComma when !ctrl: FrameStep(-1); return true;
            case Key.OemPlus when ctrl: case Key.Add when ctrl: ShiftAudioDelay(shift ? 100 : 10); return true;
            case Key.OemMinus when ctrl: case Key.Subtract when ctrl: ShiftAudioDelay(shift ? -100 : -10); return true;
            case Key.D0 when ctrl: case Key.NumPad0 when ctrl: ShiftAudioDelay(-_audioOffsetMs); return true;
            case Key.S when !ctrl: CloseFile(); return true;
            case Key.O when !ctrl: _ = OpenAsync(); return true;
        }
        return false;
    }

    /// <summary>Capitolo successivo/precedente; indietro, oltre i 3 secondi si torna all'inizio di quello in corso.</summary>
    private void JumpChapter(int direction)
    {
        // DVD: capitoli del titolo in corso (dvdnav), come DvdStepChapter su Windows.
        if (_video.ActiveDvdNav is { InMenu: false, Chapter: > 0 } dvd) { dvd.PlayChapter(Math.Max(1, dvd.Chapter + direction)); _hud.Poke(); return; }
        var starts = (_probe?.Chapters ?? new()).Select(c => c.Start).OrderBy(s => s).ToList();
        if (starts.Count == 0) { if (direction < 0) { SeekTo(0); _hud.Poke(); } return; }
        double pos = _hud.Position;
        double? target = direction > 0
            ? starts.Cast<double?>().FirstOrDefault(s => s > pos + 1)
            : starts.Cast<double?>().LastOrDefault(s => s < pos - 3) ?? 0;
        if (target is double t) { SeekTo(t); _hud.Poke(); }
    }

    /// <summary>"." e ",": un fotogramma avanti o indietro, in pausa (StepFrame di Windows: con DirectShow e' un salto
    /// della durata di un fotogramma).</summary>
    private void FrameStep(int direction)
    {
        if (_file == null || _hud.Duration <= 0) return;
        double fps = _probe?.Fps is > 1 and var f ? f : 24000.0 / 1001.0;
        if (_hud.IsPlaying) TogglePlay();
        SeekTo(Math.Clamp(_hud.Position + direction / fps, 0, _hud.Duration));
        _hud.Poke();
    }

    /// <summary>Ritardo audio a passi di 10 ms (100 con Maiusc), lo stesso del menu Audio › Ritardo audio.</summary>
    private void ShiftAudioDelay(int deltaMs)
    {
        _audioOffsetMs = Math.Clamp(_audioOffsetMs + deltaMs, -600000, 600000);
        _video.Send("audio-offset " + _audioOffsetMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SaveAudioDelay();
        _hud.InfoLine = _audioOffsetMs == 0 ? "Ritardo audio: 0 ms (originale)" : $"Ritardo audio: {_audioOffsetMs:+0;-0} ms";
        _hud.Poke();
    }

    /// <summary>
    /// Il ritardo audio resta legato al film (ExternalAudioStore, lo stesso archivio di Windows): riaprendolo riparte
    /// allineato. Solo i ritardi semplici sulla traccia del file: una traccia audio esterna o un ritardo "a curva"
    /// (sincronizzazione a tratti di Windows) qui non si applicano.
    /// </summary>
    private static int SavedAudioDelay(string? file)
    {
        if (file == null) return 0;
        try
        {
            return CinecorePlayer2025.Utilities.ExternalAudioStore.Get(file) is { } binding && (binding.AudioPath == null || File.Exists(binding.AudioPath))
                ? Math.Clamp(binding.DelayAt(0), -600000, 600000) : 0;
        }
        catch { return 0; }
    }

    private void SaveAudioDelay()
    {
        if (_file is not { } file) return;
        try
        {
            var current = CinecorePlayer2025.Utilities.ExternalAudioStore.Get(file);
            if (current is { AudioPath: not null }) return; // non si tocca una traccia esterna di Windows
            // Un ritocco a mano toglie la curva: il ritardo diventa quello scelto.
            var updated = (current ?? new CinecorePlayer2025.Utilities.ExternalAudioBinding(null)) with
                { DelayMs = _audioOffsetMs, Status = CinecorePlayer2025.Utilities.SyncStatus.Manual, CurveTimes = null, CurveOffsets = null };
            CinecorePlayer2025.Utilities.ExternalAudioStore.Set(file, _audioOffsetMs == 0 && updated.TargetOrdinal == null ? null : updated);
        }
        catch { }
    }
}
