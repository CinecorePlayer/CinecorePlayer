#nullable enable
using CinecorePlayer2025.Audio;
using CinecorePlayer2025.Utilities;
using System;
using System.IO;
using System.Threading;

namespace CinecorePlayer2025
{
    // Dissolvenza incrociata fra i brani: quando al brano in corso resta il tempo della dissolvenza,
    // il player passa al successivo come farebbe a fine brano; il motore del brano che finisce non
    // viene chiuso ma lasciato sfumare (vedi CinecoreAudioEngine.BeginFadeOutAndRelease).
    public sealed partial class PlayerForm
    {
        private static readonly int[] CrossfadeChoices = { 0, 3, 6, 10 };
        private DateTime _crossfadeArmedUtc = DateTime.MinValue;   // il cambio brano in corso e' una dissolvenza
        private string? _crossfadeCheckedPath;                      // brano per cui la decisione e' gia' stata presa
        private bool _crossfadeAllowedForCurrent;

        private string CrossfadeMenuLabel()
        {
            int seconds = CinecoreAudioSettings.Current.CrossfadeSeconds;
            return seconds > 0
                ? Tx($"Dissolvenza tra i brani: {seconds} s", $"Crossfade between tracks: {seconds} s")
                : Tx("Dissolvenza tra i brani: spenta", "Crossfade between tracks: off");
        }

        private void CycleCrossfade()
        {
            var settings = CinecoreAudioSettings.Current;
            int index = Array.IndexOf(CrossfadeChoices, settings.CrossfadeSeconds);
            settings.CrossfadeSeconds = CrossfadeChoices[(index + 1 + CrossfadeChoices.Length) % CrossfadeChoices.Length];
            CinecoreAudioSettings.Save();
            _crossfadeCheckedPath = null;
            _lblStatus.Text = CrossfadeMenuLabel();
            if (settings.CrossfadeSeconds > 0 && _engine is CinecoreAudioEngine { ExclusiveActive: true })
                _lblStatus.Text += Tx("  ·  non con l'uscita esclusiva", "  ·  not with exclusive output");
            else if (settings.CrossfadeSeconds > 0 && HasMusicPlayback && _engine is not CinecoreAudioEngine)
                _lblStatus.Text += Tx("  ·  solo con Cinecore Audio Engine", "  ·  only with Cinecore Audio Engine");
            ShowRemoteOsd(null, null, 2200, _lblStatus.Text);
        }

        // Il brano successivo, se la dissolvenza ha senso: stesso tipo di contenuto, non il seguito
        // dello stesso album (li' i brani devono attaccarsi senza sovrapporsi, come sul disco).
        private bool CrossfadeAllowedForCurrent()
        {
            string? current = _currentPath?.Trim();
            if (string.IsNullOrWhiteSpace(current)) return false;
            if (string.Equals(_crossfadeCheckedPath, current, StringComparison.OrdinalIgnoreCase)) return _crossfadeAllowedForCurrent;
            _crossfadeCheckedPath = current;
            _crossfadeAllowedForCurrent = false;
            try
            {
                if (!_playbackQueueSessionActive || IsSingleTrackLoopEnabledForPath(current)) return false;
                int index = FindCurrentPlaybackQueueIndex();
                if (index < 0) return false;
                // A fine coda la radio puo' allungarla: lo fa qui, cosi' c'e' un brano con cui incrociare.
                if (index + 1 >= _playbackQueue.Count && !TryExtendQueueWithRadio(current)) return false;
                string? next = GetPlaybackQueuePathAtIndex(index + 1)?.Trim();
                if (string.IsNullOrWhiteSpace(next) || !LooksLikePureAudioByExt(next) || !File.Exists(next)) return false;
                if (string.Equals(next, current, StringComparison.OrdinalIgnoreCase)) return false;

                var now = MediaProbe.ReadAudioTags(current);
                var then = MediaProbe.ReadAudioTags(next);
                bool sameAlbumInOrder = !string.IsNullOrWhiteSpace(now.Album) &&
                    string.Equals(now.Album.Trim(), then.Album.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    now.TrackNumber is int a && then.TrackNumber is int b && b == a + 1;
                _crossfadeAllowedForCurrent = !sameAlbumInOrder;
            }
            catch (Exception ex) { Dbg.Warn("[CROSSFADE] " + ex.Message); }
            return _crossfadeAllowedForCurrent;
        }

        /// <summary>Chiamato a ogni aggiornamento della posizione: avvia il passaggio quando resta il tempo della dissolvenza.</summary>
        private void TryStartCrossfade(double position)
        {
            int seconds = CinecoreAudioSettings.Current.CrossfadeSeconds;
            if (seconds <= 0 || _engine is not CinecoreAudioEngine { ExclusiveActive: false }) return;
            if (!HasMusicPlayback || _paused || _stopping || _endTriggered || _playbackQueueTransitionInProgress || Volatile.Read(ref _scrubActive)) return;
            // Brani corti: niente dissolvenza, se ne mangerebbe una parte troppo grande.
            if (_duration < seconds * 2 + 8) return;
            double remaining = _duration - position;
            if (remaining > seconds || remaining < 0.6) return;
            if (!CrossfadeAllowedForCurrent()) return;

            _crossfadeArmedUtc = DateTime.UtcNow;
            Dbg.Log($"[CROSSFADE] handoff with {remaining:0.0}s left of '{Path.GetFileName(_currentPath)}'", Dbg.LogLevel.Info);
            HandlePlaybackCompleted();
            if (_engine != null && !_playbackQueueTransitionInProgress && !_endTriggered) _crossfadeArmedUtc = DateTime.MinValue;
        }

        /// <summary>Durante un cambio brano per dissolvenza: lascia sfumare il motore invece di chiuderlo. True se ha preso in carico il motore.</summary>
        private bool TryReleaseEngineForCrossfade(Engines.IPlaybackEngine engine, double lastPosition)
        {
            bool armed = (DateTime.UtcNow - _crossfadeArmedUtc).TotalSeconds < 3;
            _crossfadeArmedUtc = DateTime.MinValue;
            if (!armed || _closingForExit || engine is not CinecoreAudioEngine audio) return false;
            try
            {
                double remaining = audio.DurationSeconds - Math.Max(lastPosition, audio.PositionSeconds);
                return audio.BeginFadeOutAndRelease(Math.Min(CinecoreAudioSettings.Current.CrossfadeSeconds, remaining - 0.05));
            }
            catch (Exception ex) { Dbg.Warn("[CROSSFADE] release: " + ex.Message); return false; }
        }
    }
}
