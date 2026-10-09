#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.IO;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    // Ascolti verso Last.fm e ListenBrainz: un brano conta quando e' stato ascoltato per meta' della sua
    // durata o per quattro minuti (la regola dei due servizi), e solo se dura almeno trenta secondi.
    public sealed partial class PlayerForm
    {
        private readonly object _scrobbleGate = new();
        private string? _scrobblePath;
        private MusicScrobbler.Track? _scrobbleTrack;
        private DateTime _scrobbleStartedUtc;
        private double _scrobbleListened, _scrobbleLastPosition;
        private bool _scrobbleSent;

        private void ShowScrobbleDialog()
        {
            try
            {
                using var sheetLayout = SheetPresenter.Layout(this);
                using var dialog = new ScrobbleForm(UiEnglish);
                SheetPresenter.ShowDialog(dialog, this);
            }
            catch (Exception ex) { Dbg.Warn("Scrobble dialog failed: " + ex.Message); }
        }

        /// <summary>Chiamato a ogni aggiornamento della posizione durante la musica.</summary>
        private void UpdateScrobble(double position)
        {
            if (!HasMusicPlayback || _paused || !MusicScrobbler.AnyConnected) return;
            string? path = _currentPath?.Trim();
            if (string.IsNullOrWhiteSpace(path)) return;

            lock (_scrobbleGate)
            {
                // Lo stesso brano ripartito da capo (ripetizione) e' un nuovo ascolto.
                bool restarted = _scrobbleSent && position < 3 && _scrobbleLastPosition > 10;
                if (!string.Equals(path, _scrobblePath, StringComparison.OrdinalIgnoreCase) || restarted)
                {
                    _scrobblePath = path;
                    _scrobbleTrack = null;
                    _scrobbleListened = 0;
                    _scrobbleLastPosition = position;
                    _scrobbleSent = false;
                    _scrobbleStartedUtc = DateTime.UtcNow;
                    double duration = _duration;
                    _ = Task.Run(() => BeginScrobbleSession(path!, duration));
                    return;
                }

                double delta = position - _scrobbleLastPosition;
                _scrobbleLastPosition = position;
                // Solo il tempo davvero ascoltato: un salto in avanti non conta.
                if (delta > 0 && delta < 2.5) _scrobbleListened += delta;
                if (_scrobbleSent || _scrobbleTrack == null || _scrobbleTrack.DurationSeconds < 30) return;
                if (_scrobbleListened < Math.Min(_scrobbleTrack.DurationSeconds / 2, 240)) return;
                _scrobbleSent = true;
                MusicScrobbler.Scrobble(_scrobbleTrack, _scrobbleStartedUtc);
            }
        }

        private void BeginScrobbleSession(string path, double duration)
        {
            try
            {
                // Solo i file locali hanno etichette leggibili da qui; senza artista e titolo non si invia nulla.
                if (!File.Exists(path)) return;
                var tags = MediaProbe.ReadAudioTags(path);
                string artist = !string.IsNullOrWhiteSpace(tags.Artist) ? tags.Artist : tags.AlbumArtist;
                if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(tags.Title)) return;
                if (duration <= 0 && tags.DurationMinutes is double minutes) duration = minutes * 60;
                var track = new MusicScrobbler.Track(artist, tags.Title, tags.Album ?? "", duration);
                lock (_scrobbleGate)
                {
                    if (!string.Equals(path, _scrobblePath, StringComparison.OrdinalIgnoreCase)) return;
                    _scrobbleTrack = track;
                }
                MusicScrobbler.NowPlaying(track);
                MusicScrobbler.Flush();
            }
            catch (Exception ex) { Dbg.Warn("[SCROBBLE] " + ex.Message); }
        }
    }
}
