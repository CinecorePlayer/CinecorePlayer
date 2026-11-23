using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Ascolti verso Last.fm e ListenBrainz (PlayerForm.Scrobble di Windows): un brano conta quando e' stato ascoltato per
/// meta' della sua durata o per quattro minuti, e solo se dura almeno trenta secondi. Salti e pause non contano.
/// Artista e titolo: dalle etichette del file; per i brani dei server dai dati del catalogo.
/// </summary>
internal sealed partial class LibraryView
{
    private readonly object _scrobbleGate = new();
    private string? _scrobblePath;
    private MusicScrobbler.Track? _scrobbleTrack;
    // [2026-11-22] Le prove automatiche a volume zero (CINECORE_QA_MUTE) non sono ascolti veri: non partono verso
    // Last.fm e ListenBrainz (prima ogni giro di prova segnava "in ascolto" sull'account).
    private static readonly bool QaNoScrobble = Environment.GetEnvironmentVariable("CINECORE_QA_MUTE") == "1";
    private DateTime _scrobbleStartedUtc;
    private double _scrobbleListened, _scrobbleLastPosition;
    private bool _scrobbleSent;
    private long _scrobbleSampleTimestamp;
    private int _scrobbleSessionSerial;

    /// <summary>A ogni passo del timer della musica.</summary>
    private void UpdateScrobble()
    {
        if (!MusicBarVisible || !MusicScrobbler.AnyEnabled) return;
        var item = _queue[_queueIndex];
        double position = _audio.Position, duration = _audio.Duration;
        if (!double.IsFinite(position)) return;
        lock (_scrobbleGate)
        {
            long now = Stopwatch.GetTimestamp();
            if (!_audio.IsPlaying) { _scrobbleLastPosition = position; _scrobbleSampleTimestamp = now; return; }
            // Lo stesso brano ripartito da capo (ripetizione) e' un nuovo ascolto.
            bool restarted = position < 3 && _scrobbleLastPosition > 10;
            if (!string.Equals(item.Path, _scrobblePath, StringComparison.OrdinalIgnoreCase) || restarted)
            {
                _scrobblePath = item.Path;
                _scrobbleTrack = null;
                _scrobbleListened = 0;
                _scrobbleLastPosition = position;
                _scrobbleSent = false;
                _scrobbleStartedUtc = DateTime.UtcNow;
                _scrobbleSampleTimestamp = now;
                int serial = ++_scrobbleSessionSerial;
                _ = Task.Run(() => BeginScrobbleSession(item, duration, serial));
                return;
            }
            double delta = position - _scrobbleLastPosition;
            double elapsed = Stopwatch.GetElapsedTime(_scrobbleSampleTimestamp, now).TotalSeconds;
            _scrobbleSampleTimestamp = now;
            _scrobbleLastPosition = position;
            if (delta > 0 && elapsed > 0 && elapsed < 30 && delta <= Math.Max(2.5, elapsed * 1.15 + .5))
                _scrobbleListened += Math.Min(delta, elapsed);
            if (_scrobbleTrack is { DurationSeconds: <= 0 } && duration > 0) _scrobbleTrack = _scrobbleTrack with { DurationSeconds = duration };
            if (_scrobbleSent || _scrobbleTrack == null || _scrobbleTrack.DurationSeconds <= 30) return;
            if (_scrobbleListened < Math.Min(_scrobbleTrack.DurationSeconds / 2, 240)) return;
            _scrobbleSent = true;
            Console.Error.WriteLine($"[SCROBBLE] listen eligible: listened={_scrobbleListened:0.0}s duration={_scrobbleTrack.DurationSeconds:0.0}s");
            if (!QaNoScrobble) MusicScrobbler.Scrobble(_scrobbleTrack, _scrobbleStartedUtc);
        }
    }

    private void BeginScrobbleSession(LibraryItem item, double duration, int serial)
    {
        try
        {
            string artist = UnknownTag(item.ArtistName) ? "" : item.ArtistName!;
            string title = TrackTitle(item), album = UnknownTag(item.AlbumTitle) ? "" : item.AlbumTitle!;
            if (!item.IsNetwork && File.Exists(item.Path))
            {
                var tags = CinecorePlayer2025.Utilities.MediaProbe.ReadAudioTags(item.Path);
                if (!string.IsNullOrWhiteSpace(tags.Artist)) artist = tags.Artist;
                else if (artist.Length == 0) artist = tags.AlbumArtist;
                if (!string.IsNullOrWhiteSpace(tags.Title)) title = tags.Title;
                if (!string.IsNullOrWhiteSpace(tags.Album)) album = tags.Album;
                if (artist.Length == 0) artist = MusicLyricsService.InferArtistFromPath(item.Path, ref title) ?? "";
                if (duration <= 0 && tags.DurationMinutes is double minutes) duration = minutes * 60;
            }
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) { Console.Error.WriteLine("[SCROBBLE] track has no usable artist/title"); return; }
            var track = new MusicScrobbler.Track(artist, title, album, duration);
            lock (_scrobbleGate)
            {
                if (serial != _scrobbleSessionSerial || !string.Equals(item.Path, _scrobblePath, StringComparison.OrdinalIgnoreCase)) return;
                _scrobbleTrack = track;
            }
            if (QaNoScrobble) return;
            MusicScrobbler.NowPlaying(track);
            MusicScrobbler.Flush();
        }
        catch (Exception ex) { Console.Error.WriteLine("[SCROBBLE] " + ex.Message); }
    }
}
