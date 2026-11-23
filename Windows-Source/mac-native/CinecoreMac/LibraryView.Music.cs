using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// Musica, prima parte: album in griglia (copertine quadrate, titolo e artista sotto), scheda dell'album con i brani
// e la barra di riproduzione in basso (MusicTransportBar di Windows). Suona con il Cinecore Audio Engine (MacMusicEngine).
// Mancano ancora rispetto a Windows: artisti, playlist, testi, VU/grafici, Automix, Cinecore Audio.
internal sealed partial class LibraryView
{
    internal sealed record Album(string Key, string Title, string Artist, List<LibraryItem> Tracks)
    {
        public string? Art
        {
            get
            {
                string? art = Tracks.Select(t => t.ArtPath).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
                if (art == null)
                {
                    foreach (var track in Tracks)
                        if (LibraryStore.QueueMusicArtwork(track)) break;
                }
                return art;
            }
        }
    }

    // Cinecore Audio Engine (MacMusicEngine): FFmpeg, catena DSP di Windows, CoreAudio; gapless e dissolvenza.
    private readonly MacMusicEngine _audio = new();
    // Casuale (l'ordine di prima resta per quando si spegne), ripeti brano, coda aperta sopra la barra.
    private bool _shuffle, _repeat, _queueOpen;
    private List<LibraryItem> _queueOriginal = new();
    private List<LibraryItem> _queue = new();
    private int _queueIndex = -1;
    private DispatcherTimer? _audioTimer, _seekTimer;
    private int _seekPixel = -1;
    private Album? _album;
    private DateTime _albumOpenedAt;
    private double _albumScroll;

    public bool MusicPlaying => _audio.IsPlaying;
    private bool MusicBarVisible => _queueIndex >= 0 && _queueIndex < _queue.Count;
    // Nel mini player la "barra" e' tutta la finestra: una sola lastra di vetro con copertina, brano e comandi.
    private double MusicBarH => !MusicBarVisible ? 0 : _miniPlayer ? Bounds.Height : BarHeightFor(Bounds.Width);

    /// <summary>Un video sta per partire: la musica si ferma (come il player Windows).</summary>
    public void PauseMusic() { _audio.Pause(); InvalidateVisual(); }
    // Per il menu tasto destro della finestra: con la musica in corso Pausa, Chiudi e Audio agiscono sulla musica.
    public bool MusicActive => MusicBarVisible;

    // Ricevitore di rete (controllo IP): se la musica suona sulla sua uscita, volume e muto della barra vanno a lui,
    // con le stesse regole di sicurezza del film (MainWindow.Amplifier.cs). Null o false = volume del player.
    internal Func<double, bool>? AmplifierSetVolume;
    internal Func<int, bool>? AmplifierStepVolume;
    internal Func<bool>? AmplifierToggleMute;
    internal Func<(double Level, bool Muted)?>? AmplifierState;
    private (double Level, bool Muted)? AmpNow => MusicBarVisible ? AmplifierState?.Invoke() : null;
    private double ShownMusicVolume => AmpNow is { } a ? a.Level : _audio.Volume;
    private bool ShownMusicMuted => AmpNow is { } a ? a.Muted : _audio.Muted;
    private void ToggleMusicMute() { if (AmplifierToggleMute?.Invoke() == true) return; _audio.Muted = !_audio.Muted; }
    public bool MusicMuted { get => _audio.Muted; set { _audio.Muted = value; InvalidateVisual(); } }
    public void MenuToggleMusic() { if (MusicBarVisible) { TogglePlayMusic(); InvalidateVisual(); } }
    public void MenuStopMusic() { if (MusicBarVisible) StopMusicAnimated(); }
    /// <summary>Uscita audio cambiata dal menu: anche la musica passa subito al nuovo dispositivo.</summary>
    public void AudioOutputChanged(string? uid = null) => _audio.OutputChanged(uid);

    internal (LibraryItem Item, double Position, double Duration, bool Paused, float Volume, bool Muted)? RemoteMusicState =>
        MusicBarVisible ? (_queue[_queueIndex], _audio.Position, _audio.Duration, !_audio.IsPlaying, (float)ShownMusicVolume, ShownMusicMuted) : null;
    internal IEnumerable<(LibraryItem Item, bool Current)> RemoteMusicQueue =>
        MusicBarVisible ? _queue.Select((track, index) => (track, index == _queueIndex)) : Array.Empty<(LibraryItem, bool)>();

    internal void ControlMusicFromRemote(string command, double value)
    {
        if (!MusicBarVisible) return;
        switch (command)
        {
            case "toggle": MenuToggleMusic(); break;
            case "play": if (!MusicPlaying) _audio.Play(); break;
            case "pause": PauseMusic(); break;
            case "seek": _audio.Seek(Math.Clamp(value, 0, Math.Max(0, _audio.Duration))); break;
            case "back10": _audio.Seek(Math.Max(0, _audio.Position - 10)); break;
            case "fwd10": _audio.Seek(Math.Min(_audio.Duration, _audio.Position + 10)); break;
            case "prev": PreviousTrack(); break;
            case "next": NextTrack(); break;
            case "vol": if (AmplifierSetVolume?.Invoke(Math.Clamp(value, 0, 1)) != true) _audio.Volume = (float)Math.Clamp(value, 0, 1); break;
            case "volup": if (AmplifierStepVolume?.Invoke(+1) != true) _audio.Volume += .05f; break;
            case "voldown": if (AmplifierStepVolume?.Invoke(-1) != true) _audio.Volume -= .05f; break;
            case "mute": ToggleMusicMute(); break;
        }
        InvalidateVisual();
    }

    private static string TrackTitle(LibraryItem t)
    {
        if (t.IsNetwork || t.MusicTags != null || string.IsNullOrWhiteSpace(t.Path)) return t.Title;
        // File locali senza tag: "01 - Titolo" -> "Titolo".
        return System.Text.RegularExpressions.Regex.Replace(t.Title, @"^\s*\d{1,3}\s*[-._ ]\s*", "").Trim() is { Length: > 0 } clean ? clean : t.Title;
    }

    private static int? TrackNumber(LibraryItem t)
    {
        if (t.TrackNumber > 0) return t.TrackNumber;
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(t.Path) ?? "", @"^\s*(\d{1,3})");
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > 0 ? n : null;
    }

    /// <summary>
    /// MusicArtworkPlaceholder di Windows: fondo neutro e una nota piccola e tenue al centro (piu' grande nelle
    /// copertine piccole, come la barra di riproduzione).
    /// </summary>
    private static void DrawMusicPlaceholder(DrawingContext ctx, Rect r)
    {
        if (r.Width <= 2 || r.Height <= 2) return;
        bool light = UiTheme.IsLight;
        ctx.FillRectangle(new SolidColorBrush(light ? Color.FromRgb(229, 231, 235) : Color.FromRgb(27, 30, 35)), r);
        double smallest = Math.Min(r.Width, r.Height);
        bool wide = r.Width > r.Height * 1.15;
        double side = Math.Max(10, Math.Round(smallest * (smallest < 140 ? .46 : wide ? .16 : .2)));
        Color ink = smallest < 140 ? (light ? Color.FromArgb(150, 40, 44, 52) : Color.FromArgb(150, 255, 255, 255))
                                   : (light ? Color.FromArgb(100, 40, 44, 52) : Color.FromArgb(80, 255, 255, 255));
        SvgIcon.Get("music")?.Draw(ctx, new Rect(r.Center.X - side / 2, r.Center.Y - side / 2, side, side), ink);
    }

    private static string AlbumArtist(LibraryItem t) =>
        (t.AlbumArtist ?? t.ArtistName ?? "").Trim();

    private static string TrackArtist(LibraryItem track) => track.ArtistName ?? AlbumArtist(track);

    private static bool UnknownTag(string? value) => string.IsNullOrWhiteSpace(value) || value.Trim().StartsWith('[');

    private static bool GenericMusicFolder(string folder) => folder.Trim().ToUpperInvariant() is
        "" or "DOWNLOAD" or "DOWNLOADS" or "SCARICATI" or "DESKTOP" or "SCRIVANIA" or "MUSIC" or "MUSICA" or "DOCUMENTS" or "DOCUMENTI";

    // Elenchi della musica memorizzati per il disegno (come CollectionItems): album, artisti, brani ordinati e "continua
    // ad ascoltare" si rifacevano a ogni fotogramma (raggruppamenti e ordinamenti per lingua su tutta la libreria
    // musicale, piu' una ricerca nel catalogo degli album per ogni brano senza album). Si rifanno quando cambia
    // l'elenco di partenza o dopo CollectionMemoMs, cosi' le modifiche dei metadati arrivano entro mezzo secondo.
    private readonly Dictionary<string, (object A, object? B, int Count, long At, object Value)> _musicMemo = new(StringComparer.Ordinal);

    private T MusicMemo<T>(string name, object a, object? b, int count, Func<T> build) where T : class
    {
        long now = Environment.TickCount64;
        if (_musicMemo.TryGetValue(name, out var m) && ReferenceEquals(m.A, a) && ReferenceEquals(m.B, b) && m.Count == count
            && now - m.At < CollectionMemoMs) return (T)m.Value;
        var value = build();
        _musicMemo[name] = (a, b, count, now, value);
        return value;
    }

    /// <summary>Album di tutta la musica della libreria (memorizzati per il disegno).</summary>
    private List<Album> LibraryAlbums() =>
        MusicMemo("albums-all", _items, null, _items.Count, () => Albums(_items.Where(t => t.Category == "Music")));

    internal static List<Album> Albums(IEnumerable<LibraryItem> tracks)
    {
        var items = tracks.ToList();
        string Folder(LibraryItem t) => t.IsNetwork ? "" : Path.GetDirectoryName(t.Path) ?? "";
        string FolderName(LibraryItem t) => Path.GetFileName(Folder(t));
        foreach (var track in items.Where(t => !t.IsNetwork && UnknownTag(t.AlbumTitle) && GenericMusicFolder(FolderName(t))))
        {
            if (MacAlbumInfo.GetTrackAlbum(TrackArtist(track), TrackTitle(track), track.DurationMinutes * 60) is not { Album: { } title } found) continue;
            track.AlbumTitle = title;
            track.AlbumArtist ??= found.Artist;
            // La copertina segue l'album mostrato: senza questo resta quella del brano cercato come singolo
            // (Woodkid "Run Boy Run" finiva in "The Golden Age" con la copertina dell'EP).
            if (track.MusicTags is { } tags && !string.Equals(tags.Album, title, StringComparison.Ordinal))
            {
                track.MusicTags = tags with { Album = title, AlbumArtist = string.IsNullOrWhiteSpace(tags.AlbumArtist) ? found.Artist ?? "" : tags.AlbumArtist };
                track.ArtPath = null;
                if (track.MusicArtworkRetryAfter != DateTime.MaxValue) track.MusicArtworkRetryAfter = DateTime.MinValue;
            }
        }
        var collections = items.Where(t => !t.IsNetwork)
            .GroupBy(t => Path.GetDirectoryName(t.Path) ?? "", StringComparer.Ordinal)
            .Where(g => MusicArtworkService.IsLooseCollection(Path.GetFileName(g.Key)) ||
                (g.Any(t => MusicArtworkService.IsLooseCollection(t.AlbumTitle)) &&
                 g.All(t => UnknownTag(t.AlbumTitle) || MusicArtworkService.IsLooseCollection(t.AlbumTitle))))
            .ToDictionary(g => g.Key, g =>
            {
                var titles = g.Select(t => t.AlbumTitle).OfType<string>().Where(MusicArtworkService.IsLooseCollection)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                return titles.Count == 1 ? titles[0] : Path.GetFileName(g.Key);
            }, StringComparer.Ordinal);

        string Title(LibraryItem t) => !UnknownTag(t.AlbumTitle) ? t.AlbumTitle! : t.IsNetwork ? "Senza album"
            : GenericMusicFolder(FolderName(t)) ? TrackTitle(t) : FolderName(t);
        return items.GroupBy(t =>
            {
                string folder = Folder(t);
                if (!t.IsNetwork && collections.ContainsKey(folder)) return "collection\u0001" + folder;
                bool untagged = UnknownTag(t.AlbumTitle) || MusicArtworkService.IsLooseCollection(t.AlbumTitle);
                string artist = untagged ? MusicTextIdentity.ArtistKey(AlbumArtist(t)) :
                    t.IsNetwork ? "" : MusicTextIdentity.ArtistKey(t.AlbumArtist);
                return Title(t).Trim().ToUpperInvariant() + "\u0001" + (untagged ? folder + "\u0001" : "") + artist;
            }, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                bool collection = !first.IsNetwork && collections.TryGetValue(Folder(first), out _);
                var artists = g.Select(AlbumArtist).Where(a => !UnknownTag(a)).DistinctBy(MusicTextIdentity.ArtistKey).ToList();
                string artist = artists.Count > (collection ? 1 : 2) ? "Artisti vari" : string.Join(", ", artists);
                // Il server Windows elenca lo stesso file in piu' cartelle (Album, Artisti, Generi...): una volta sola.
                var network = g.Where(t => t.IsNetwork)
                    .DistinctBy(t => (TrackTitle(t).ToUpperInvariant(), TrackNumber(t), Math.Round(t.DurationMinutes ?? 0, 2)));
                var ordered = g.Where(t => !t.IsNetwork).Concat(network)
                    .OrderBy(t => TrackNumber(t) ?? 999).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                string title = collection ? collections[Folder(first)] : Title(first);
                if (collection) ordered = OrderCollectionTracks(ordered);
                return new Album(g.Key, UnknownTag(title) ? "Album sconosciuto" : title.Trim(), artist, ordered);
            })
            .OrderByDescending(a => a.Tracks.Max(t => t.SortDateUtc))
            .ToList();
    }

    private void RefreshTrackAlbums()
    {
        if (_album != null && !IsArtistSheet(_album))
        {
            string path = _album.Tracks[Math.Clamp(_albumSelected, 0, _album.Tracks.Count - 1)].Path;
            if (Albums(_items.Where(t => t.Category == "Music")).FirstOrDefault(a => a.Tracks.Any(t => t.Path == path)) is { } updated)
            {
                _album = updated;
                _albumSelected = updated.Tracks.FindIndex(t => t.Path == path);
            }
        }
        InvalidateVisual();
    }

    private static List<LibraryItem> OrderCollectionTracks(List<LibraryItem> tracks)
    {
        // In una raccolta con tag parziali, i numeri espliciti mantengono la loro posizione
        // e i brani senza numero riempiono gli spazi nell'ordine corrente.
        var numbered = tracks.Where(t => TrackNumber(t) != null).ToList();
        if (numbered.Count == 0 || numbered.Count == tracks.Count) return tracks;
        var slots = new LibraryItem?[tracks.Count];
        foreach (var track in numbered)
        {
            int index = TrackNumber(track)!.Value - 1;
            if (index >= slots.Length || slots[index] != null) return tracks;
            slots[index] = track;
        }
        using var unnumbered = tracks.Where(t => TrackNumber(t) == null).GetEnumerator();
        var ordered = new List<LibraryItem>(tracks.Count);
        foreach (var track in slots)
        {
            if (track != null) ordered.Add(track);
            else if (unnumbered.MoveNext()) ordered.Add(unnumbered.Current);
        }
        return ordered;
    }

    private void DrawMusicGrid(DrawingContext ctx, Rect viewport, IReadOnlyList<LibraryItem> tracks)
    {
        var albums = MusicMemo("albums-visible", tracks, _items, tracks.Count + _items.Count, () =>
        {
            var visiblePaths = tracks.Select(t => t.Path).ToHashSet(StringComparer.Ordinal);
            return LibraryAlbums().Where(album => album.Tracks.Any(t => visiblePaths.Contains(t.Path))).ToList();
        });
        double gap = S(24);
        int columns = (int)Math.Clamp(Math.Floor((viewport.Width + gap) / Math.Max(1, S(190) + gap)), 3, 10);
        double cardW = Math.Max(1, Math.Floor((viewport.Width - gap * (columns - 1)) / columns));
        double cardH = cardW + S(52), rowGap = S(22);
        int rows = (albums.Count + columns - 1) / columns;
        double bottom = viewport.Bottom - MusicBarH;
        _scrollMax = Math.Max(0, rows * (cardH + rowGap) - rowGap + S(24) - (bottom - viewport.Y));
        _scroll = Math.Clamp(_scroll, 0, _scrollMax); _scrollTarget = Math.Clamp(_scrollTarget, 0, _scrollMax);
        using (ctx.PushClip(new Rect(0, viewport.Y - S(6), Bounds.Width, Math.Max(1, bottom - viewport.Y + S(6)))))
        {
            int first = Math.Max(0, (int)Math.Floor(_scroll / (cardH + rowGap)) * columns);
            for (int i = first; i < albums.Count; i++)
            {
                int row = i / columns, col = i % columns;
                double y = viewport.Y + row * (cardH + rowGap) - _scroll;
                if (y > bottom) break;
                var r = new Rect(viewport.X + col * (cardW + gap), y, cardW, cardH);
                var album = albums[i];
                var cover = new Rect(r.X, r.Y, cardW, cardW);
                double radius = S(6);
                using (ctx.PushGeometryClip(new RectangleGeometry(cover, radius, radius)))
                {
                    if (Image(album.Art, cardW) is { } bmp) DrawCover(ctx, bmp, cover);
                    else DrawMusicPlaceholder(ctx, cover);
                }
                bool hover = Hover(r);
                bool light = UiTheme.IsLight;
                ctx.DrawRectangle(null, hover ? new Pen(B(light ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(215, 255, 255, 255)), 2)
                                              : new Pen(B(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255)), 1), cover.Deflate(hover ? 1 : .5), radius, radius);
                TextAt(ctx, album.Title, Math.Max(9.4, 11.4 * _s), DisplaySemibold, Text, new Rect(r.X + 1, cover.Bottom + S(8), r.Width - 2, S(20)));
                TextAt(ctx, album.Artist, Math.Max(9, 10.6 * _s), DisplayRegular, A(Text, 160), new Rect(r.X + 1, cover.Bottom + S(28), r.Width - 2, S(18)));
                var hit = r.Intersect(new Rect(0, viewport.Y, Bounds.Width, Math.Max(1, bottom - viewport.Y)));
                if (hit.Width > 0 && hit.Height > 0) _hits.Add(new Hit(hit, "album", album.Key, album.Tracks[0]));
            }
        }
    }

    // ----- scheda dell'album -----
    private void OpenAlbum(string key)
    {
        _album = Albums(_items.Where(i => i.Category == "Music")).FirstOrDefault(a => a.Key == key);
        _albumOpenedAt = DateTime.UtcNow; _albumScroll = 0; _albumSelected = 0; _albumRecPage = 0; _albumRowArmed = false;
        _frames.Start();
        InvalidateVisual();
    }

    private void CloseAlbum() { _album = null; InvalidateVisual(); }

    // ----- riproduzione -----
    private void PlayQueue(List<LibraryItem> tracks, int index, double resumeSeconds = 0)
    {
        if (tracks.Count == 0) return;
        _queue = tracks; _queueIndex = Math.Clamp(index, 0, tracks.Count - 1);
        _queueOriginal = tracks.ToList();
        if (_shuffle) ShuffleRest();
        StartCurrentTrack(resumeSeconds);
    }

    private void StartCurrentTrack(double resumeSeconds = 0)
    {
        var track = _queue[_queueIndex];
        // La barra entra solo quando parte la musica (non a ogni cambio di brano).
        bool reveal = !_audio.IsPlaying && _audio.Source == null || BarHiding;
        if (_audio.Source == null && double.TryParse(MacSettings.Get("musicVolume", "1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double volume))
            _audio.Volume = (float)volume;
        string source = track.IsNetwork ? CinecorePlayer2025.Utilities.JellyfinClient.PlaybackUrl(track.Path) : track.Path;
        _audio.Open(source, resumeSeconds);
        UpdateNextSource();
        _audioTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => AudioTick());
        // Timeline continua: il ridisegno a 4 al secondo la faceva avanzare a scatti (visibili sui brani corti). Qui si
        // ridisegna solo quando l'avanzamento si sposta davvero di un pixel.
        _seekTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            if (!MusicBarVisible || !_audio.IsPlaying || !IsVisible || !_barTargets.TryGetValue("seek", out var seek)) return;
            double dur = _audio.Duration;
            // In punti (non in pixel Retina): ogni ridisegno della libreria costa ~13 ms.
            int pixel = dur > 0 ? (int)(seek.Width * Math.Clamp(_audio.Position / dur, 0, 1)) : -1;
            if (pixel == _seekPixel) return;
            _seekPixel = pixel;
            InvalidateBar();
        });
        _seekTimer.Start();
        _audioTimer.Start();
        if (reveal) PlayBarReveal();
        // Brano nuovo con la vista a schermo intero aperta: testo, informazioni e sfondo del brano nuovo.
        if (_musicView == 4) LoadMusicLyricsIfNeeded();
        if (_musicView == 5) LoadMusicInfoIfNeeded();
        if (MusicFullscreenOpen) RequestMusicBackground();
        InvalidateVisual();
    }

    private double _lastPosition;
    private int _stuckTicks;

    /// <summary>Il brano che segue (per il gapless e la dissolvenza): nessuno con la ripetizione del brano.</summary>
    private void UpdateNextSource()
    {
        string? next = !_repeat && _queueIndex + 1 < _queue.Count ? _queue[_queueIndex + 1] is var t && t.IsNetwork ? CinecorePlayer2025.Utilities.JellyfinClient.PlaybackUrl(t.Path) : _queue[_queueIndex + 1].Path : null;
        if (_audio.NextSource != next) _audio.NextSource = next;
    }

    private void AudioTick()
    {
        if (!MusicBarVisible) { _audioTimer?.Stop(); return; }
        UpdateNextSource();
        // Il motore e' passato da solo al brano successivo (gapless o dissolvenza): la coda lo segue.
        if (_queueIndex + 1 < _queue.Count && _audio.Source is { } heard && _audio.NextSource == heard && heard != CurrentSourcePath())
        {
            _queueIndex++;
            if (_musicView == 4) LoadMusicLyricsIfNeeded();
            if (_musicView == 5) LoadMusicInfoIfNeeded();
            if (MusicFullscreenOpen) RequestMusicBackground();
            UpdateNextSource();
            InvalidateVisual();
        }
        double pos = _audio.Position, dur = _audio.Duration;
        // Fine del brano: il successivo (se il motore non e' gia' passato da solo, senza pausa).
        bool ended = dur > 0 && pos >= dur - 0.3 && !_audio.IsPlaying;
        if (_audio.Failed) { _stuckTicks++; if (_stuckTicks > 4) { _stuckTicks = 0; NextTrack(); return; } }
        if (ended)
        {
            RecordListening(ended: true);
            if (_repeat) { _audio.Seek(0); _audio.Play(); }
            else if (_queueIndex < _queue.Count - 1) NextTrack();
            // Radio: finita la coda, la si allunga con brani simili e si prosegue.
            else if (TryExtendQueueWithRadio()) NextTrack();
            else { _audio.Pause(); _audio.Seek(0); }
        }
        UpdateScrobble();
        if (!ended) RecordListening(ended: false);
        if (ended) InvalidateVisual();
        else if (Math.Abs(pos - _lastPosition) > 0.01) InvalidateBar();
        _lastPosition = pos;
    }

    private string CurrentSourcePath()
    {
        var t = _queue[_queueIndex];
        return t.IsNetwork ? CinecorePlayer2025.Utilities.JellyfinClient.PlaybackUrl(t.Path) : t.Path;
    }

    private void NextTrack() { if (_queueIndex < _queue.Count - 1) { _queueIndex++; StartCurrentTrack(); } }
    private void PreviousTrack()
    {
        // Come i lettori: oltre i 3 secondi si torna all'inizio del brano.
        if (_audio.Position > 3 || _queueIndex == 0) { _audio.Seek(0); InvalidateVisual(); return; }
        _queueIndex--; StartCurrentTrack();
    }

    /// <summary>Il brano in corso resta, i successivi si mescolano.</summary>
    private void ShuffleRest()
    {
        var rest = _queue.Skip(_queueIndex + 1).OrderBy(_ => Random.Shared.Next()).ToList();
        _queue = _queue.Take(_queueIndex + 1).Concat(rest).ToList();
    }

    private void ToggleShuffle()
    {
        _shuffle = !_shuffle;
        if (MusicBarVisible)
        {
            var current = _queue[_queueIndex];
            if (_shuffle) ShuffleRest();
            else { _queue = _queueOriginal.ToList(); _queueIndex = Math.Max(0, _queue.FindIndex(t => t.Path == current.Path)); }
        }
        InvalidateVisual();
    }

    private void TogglePlayMusic()
    {
        if (_audio.IsPlaying) _audio.Pause(); else _audio.Play();
        InvalidateVisual();
    }

    private void StopMusic()
    {
        _audio.Stop(); _queue = new(); _queueIndex = -1; _audioTimer?.Stop();
        _musicView = 0; _fsExitStart = -1; _barLastTrack = null;
        InvalidateVisual();
    }

    private bool MusicActivate(Hit hit, Point p)
    {
        switch (hit.Kind)
        {
            case "album": OpenAlbum(hit.Key); return true;
            case "album-rec-web": OpenRecPage(hit.Key); return true;
            case "album-close": CloseAlbum(); return true;
            case "album-panel": case "music-bar": return true;
            case "album-play":
                if (_album != null && int.TryParse(hit.Key, out int index)) PlayQueue(_album.Tracks.ToList(), index);
                return true;
            case var music when music.StartsWith("music-", StringComparison.Ordinal): return MusicBarCommand(music["music-".Length..]);
            case var q when q.StartsWith("queue-", StringComparison.Ordinal): return QueueActivate(hit);
        }
        return false;
    }

    private bool HandleMusicKey(KeyEventArgs e)
    {
        if (_musicView != 0 && e.Key == Key.Escape) { CloseMusicFullscreen(); e.Handled = true; return true; }
        if (_album != null && e.Key == Key.Escape) { CloseAlbum(); e.Handled = true; return true; }
        if (_album != null && _menu == null && HandleAlbumKey(e)) return true;
        if (MusicBarVisible && e.Key == Key.Space && _searchBox?.IsFocused != true) { TogglePlayMusic(); e.Handled = true; return true; }
        return false;
    }

    internal void TestPlayMusicFile(string path)
    {
        var item = new LibraryItem { Path = path, Title = Path.GetFileNameWithoutExtension(path), Category = "Music" };
        PlayQueue(new List<LibraryItem> { item }, 0);
    }

    internal void TestPlayMusicFiles(string paths)
    {
        var items = paths.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => LibraryStore.ReadMusicItem(p)).ToList();
        if (items.Count > 0) PlayQueue(items, 0);
    }

    internal string TestMusicState => $"brano={_queueIndex}/{_queue.Count} src={System.IO.Path.GetFileName(_audio.Source ?? "-")} playing={_audio.IsPlaying} pos={_audio.Position:0.0} dur={_audio.Duration:0.0} failed={_audio.Failed} vu={_audio.Meters.Rms[0]:0.000} ago={_vuPos[0]:0.00} rif={_vuReference:0.0} vista={_musicView} lufsI={(_audio.Analysis?.LufsI ?? double.NaN):0.0} lufsM={(_audio.Analysis?.LufsM ?? double.NaN):0.0} picco={(_audio.Analysis is {} am ? Math.Max(am.PeakL, am.PeakR) : double.NaN):0.000} fotogrammi={_fsRunning} testo={_lyricActive}/{_lyricLines.Count} scorr={_lyricScroll:0.0} attesa={_lyricsLoading} sinc={_lyricSyncFor != null} controllo={_lyricWatch?.IsEnabled == true} sfondo={_fsBackgroundFade >= 0}";

    /// <summary>Prove: apre l'album con questo titolo e scrive anno e genere (dai tag o dal catalogo online).</summary>
    internal void TestOpenAlbum(string title)
    {
        var album = Albums(_items.Where(i => i.Category == "Music")).FirstOrDefault(a => a.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (album == null) { Console.Error.WriteLine("[qa] album non trovato: " + title); return; }
        OpenAlbum(album.Key);
        int? year = album.Tracks.Select(c => c.Year).FirstOrDefault(v => v is > 0);
        string? genre = album.Tracks.SelectMany(c => c.Genres).FirstOrDefault(g => !UnknownTag(g));
        var online = MacAlbumInfo.Get(album.Artist, album.Title, album.Tracks[0].Title, album.Tracks[0].DurationMinutes * 60);
        Console.Error.WriteLine($"[qa] album {album.Title} · {album.Artist}: tag anno={year?.ToString() ?? "-"} genere={genre ?? "-"}; online anno={online?.Year?.ToString() ?? "-"} genere={online?.Genre ?? "-"}");
    }

    /// <summary>
    /// Prove: analizza i brani della libreria (profili in cache) e pianifica il passaggio fra coppie a caso (seme fisso)
    /// con ogni combinazione di stile e intensita'; scrive ogni decisione e il conteggio delle tecniche.
    /// </summary>
    private async System.Threading.Tasks.Task AutomixSurveyAsync(int pairs)
    {
        var paths = _items.Where(i => i.Category == "Music" && !i.IsNetwork && File.Exists(i.Path)).Select(i => i.Path).Distinct().ToList();
        Console.Error.WriteLine($"[survey] {paths.Count} brani, analisi...");
        var profiles = new Dictionary<string, CinecorePlayer2025.Audio.TrackMixProfile>();
        foreach (var p in paths)
            if (await CinecorePlayer2025.Audio.AutomixAnalyzer.PrepareAsync(p).ConfigureAwait(false) is { } prof) profiles[p] = prof;
        var list = profiles.Keys.ToList();
        Console.Error.WriteLine($"[survey] {list.Count} profili");
        foreach (var path in list)
        {
            var pr = profiles[path];
            static (double P, double T, double B) Mean(CinecorePlayer2025.Audio.MusicalMap? m)
            {
                if (m == null || m.Frames.Length == 0) return (double.NaN, double.NaN, double.NaN);
                return (m.Frames.Average(f => f.Percussion), m.Frames.Average(f => f.Tonal), m.Frames.Average(f => f.Bass));
            }
            var t = Mean(pr.TailMusic); var h = Mean(pr.HeadMusic);
            Console.Error.WriteLine(FormattableString.Invariant($"[survey-prof] {Path.GetFileNameWithoutExtension(path)} | tail {pr.TailBpm:0} x{pr.TailConfidence:0.0} db{pr.TailDownbeatConfidence:0.00} perc {t.P:0.00} ton {t.T:0.00} bass {t.B:0.00} | head {pr.HeadBpm:0} x{pr.HeadConfidence:0.0} perc {h.P:0.00} ton {h.T:0.00} | voce {(pr.TailVoice is { Length: > 0 } v ? v.Count(x => x >= 50) * 100 / v.Length : -1)}%"));
        }
        if (list.Count < 2) return;
        var rng = new Random(7);
        var chosen = Enumerable.Range(0, pairs).Select(_ => { int x = rng.Next(list.Count), y = rng.Next(list.Count - 1); if (y >= x) y++; return (list[x], list[y]); }).ToList();
        foreach (var mode in new[] { "dj", "preserve" })
            foreach (var intensity in new[] { "crossfade", "gentle", "balanced", "dj" })
            {
                var prefs = new CinecorePlayer2025.Audio.AutomixPreferences(mode, intensity, true, true, "off");
                var counts = new Dictionary<string, int>();
                var watch = System.Diagnostics.Stopwatch.StartNew(); double slowest = 0; string slowestPair = "";
                foreach (var (from, to) in chosen)
                {
                    var a = profiles[from]; var b = profiles[to];
                    double endAt = Math.Min(a.Duration, a.End);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var plan = CinecorePlayer2025.Audio.AutomixPlanner.Plan(a, b, endAt, Math.Max(0, endAt - 70), prefs, from, to);
                    double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    if (ms > slowest) { slowest = ms; slowestPair = Path.GetFileNameWithoutExtension(from) + " -> " + Path.GetFileNameWithoutExtension(to); }
                    string kind = plan == null ? "(nessun piano)" : plan.Curves?.Name is { Length: > 0 } n && plan.Kind.StartsWith("cued") ? "a tempo: " + n : plan.Kind + (plan.Curves?.Name is { Length: > 0 } m ? " / " + m : "");
                    kind = System.Text.RegularExpressions.Regex.Replace(kind, @"\d+(\.\d+)?", "#");
                    counts[kind] = counts.GetValueOrDefault(kind) + 1;
                    if (mode == "dj" && intensity == "balanced")
                        Console.Error.WriteLine($"[survey-pair] {Path.GetFileNameWithoutExtension(from)} -> {Path.GetFileNameWithoutExtension(to)}: {kind} | {plan?.Fade:0.0}s | {CinecorePlayer2025.Audio.AutomixPlanner.LastCuedDecision}");
                }
                Console.Error.WriteLine($"[survey] tempi stile={mode} intensita={intensity}: totale {watch.ElapsedMilliseconds} ms, il piu' lento {slowest:0} ms ({slowestPair})");
                Console.Error.WriteLine($"[survey] stile={mode} intensita={intensity}: " + string.Join("; ", counts.OrderByDescending(c => c.Value).Select(c => $"{c.Value}x {c.Key}")));
            }
        Console.Error.WriteLine("[survey] fine");
    }

    internal void TestOpenFirstAlbum(bool play)
    {
        var album = Albums(_items.Where(i => i.Category == "Music")).FirstOrDefault();
        if (album == null) return;
        OpenAlbum(album.Key);
        if (play && _album != null) PlayQueue(_album.Tracks.ToList(), 0);
    }

    /// <summary>Simboli di MusicTransportBar.DrawSymbol (24 unita'): casuale, ripeti brano, coda.</summary>
    private void DrawTransportSymbol(DrawingContext ctx, Rect box, string icon, Color color)
    {
        double k = box.Width / 24;
        Point P(double x, double y) => new(box.X + x * k, box.Y + y * k);
        var pen = new Pen(B(color), Math.Max(1.2, 1.6 * k), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        switch (icon)
        {
            case "shuffle":
                ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(3, 6), P(7, 6), P(17, 18), P(21, 18) }, false));
                ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(3, 18), P(7, 18), P(17, 6), P(21, 6) }, false));
                ctx.DrawLine(pen, P(18, 3), P(21, 6)); ctx.DrawLine(pen, P(18, 9), P(21, 6));
                ctx.DrawLine(pen, P(18, 15), P(21, 18)); ctx.DrawLine(pen, P(18, 21), P(21, 18));
                break;
            case "repeat":
                ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(4, 11), P(4, 7), P(20, 7) }, false));
                ctx.DrawLine(pen, P(17, 4), P(20, 7)); ctx.DrawLine(pen, P(17, 10), P(20, 7));
                ctx.DrawGeometry(null, pen, new PolylineGeometry(new[] { P(20, 13), P(20, 17), P(4, 17) }, false));
                ctx.DrawLine(pen, P(7, 14), P(4, 17)); ctx.DrawLine(pen, P(7, 20), P(4, 17));
                ctx.DrawText(UiText.Get("1", DisplaySemibold, 7 * k, color), P(10.3, 7.4));
                break;
            case "list":
                ctx.DrawRectangle(null, pen, new Rect(P(3, 4), P(21, 18)));
                ctx.DrawLine(pen, P(6, 21), P(10, 18)); ctx.DrawLine(pen, P(7, 8), P(17, 8)); ctx.DrawLine(pen, P(7, 12), P(15, 12));
                break;
        }
    }

    /// <summary>Tasti multimediali con la musica in corso (play/pausa, avanti, indietro, stop, posizione).</summary>
    public bool MediaKeyCommand(string command)
    {
        if (!MusicBarVisible) return false;
        switch (command)
        {
            case "toggle": return MusicBarCommand("play");
            case "play": if (!_audio.IsPlaying) MusicBarCommand("play"); return true;
            case "pause": if (_audio.IsPlaying) MusicBarCommand("play"); return true;
            case "next": return MusicBarCommand("next");
            case "previous": return MusicBarCommand("previous");
            case "stop": StopMusicAnimated(); return true;
        }
        if (command.StartsWith("seek:", StringComparison.Ordinal) && double.TryParse(command[5..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double at))
        { _audio.Seek(Math.Max(0, at)); InvalidateVisual(); return true; }
        return false;
    }

    internal void TestMusicCommand(string key) { if (!MusicBarCommand(key)) Console.Error.WriteLine("[qa] music command ignored: " + key); }

    internal void TestMusicUi(string what)
    {
        if (what.StartsWith("seek:")) { _audio.Seek(double.Parse(what[5..], System.Globalization.CultureInfo.InvariantCulture)); return; }
        if (what.StartsWith("preamp:")) { CinecorePlayer2025.Audio.CinecoreAudioSettings.Current.PreampDb = double.Parse(what[7..], System.Globalization.CultureInfo.InvariantCulture); CinecorePlayer2025.Audio.CinecoreAudioSettings.Current.EqEnabled = true; CinecorePlayer2025.Audio.CinecoreAudioSettings.Save(); return; }
        if (what.StartsWith("bitperfect:")) { CinecorePlayer2025.Audio.CinecoreAudioSettings.Current.BitPerfect = what.EndsWith("1"); CinecorePlayer2025.Audio.CinecoreAudioSettings.Save(); return; }
        if (what.StartsWith("intensity:")) { CinecorePlayer2025.Audio.CinecoreAudioSettings.Current.AutomixIntensity = what[10..]; return; } // solo prove: non salvato
        if (what.StartsWith("crossfade:")) { CinecorePlayer2025.Audio.CinecoreAudioSettings.Current.CrossfadeSeconds = int.Parse(what[10..]); return; }
        if (what.StartsWith("fs:")) { ShowMusicFullscreen(int.Parse(what[3..])); return; }
        if (what == "fs-close") { CloseMusicFullscreen(); return; }
        if (what.StartsWith("fs-tab:")) { _fsTab = int.Parse(what[7..]); InvalidateVisual(); return; }
        if (what == "lyrics-sync") { RequestLyricSync(); Console.Error.WriteLine($"[qa] lyrics-sync per={_lyricSyncFor}"); return; }
        if (what == "order-mix") { OrderQueueForMixing(); return; }
        if (what.StartsWith("automix-survey:")) { _ = AutomixSurveyAsync(int.Parse(what[15..])); return; }
        if (what.StartsWith("mix-set:"))
        {
            // mix-set:uscita,ingresso,durata,volume,eq,filtro,effetto[,velocita'[,uscita]][@out.filter=x,y,x,y|in.vol=...]
            // fra il brano in corso e il successivo.
            var parts = what[8..].Split('@');
            var lanes = parts.Length > 1 ? parts[1].Split('|').Select(l => l.Split('=')).ToDictionary(l => l[0],
                l => l[1].Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray()) : null;
            var v = parts[0].Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (UpcomingLocalTrack() is not { } upcomingMix) { Console.Error.WriteLine("[qa] mix-set: niente brano successivo"); return; }
            var mix = new CinecorePlayer2025.Audio.ManualMix(v[0], v[1], v[2], (int)v[3], (int)v[4], (int)v[5], (int)v[6]) { IncomingSpeed = v.Length > 7 && v[7] > 0 ? v[7] : null, OutgoingSpeed = v.Length > 8 ? v[8] : null, Lanes = lanes };
            CinecorePlayer2025.Audio.ManualMixStore.Set(_queue[_queueIndex].Path, upcomingMix.Path, mix);
            _audio.ManualMixChanged(_queue[_queueIndex].Path, upcomingMix.Path);
            Console.Error.WriteLine($"[qa] mix salvato {mix}");
            return;
        }
        if (what == "queue-dump")
        {
            Console.Error.WriteLine("[qa] coda " + string.Join(" | ", _queue.Select((t, i) => (i == _queueIndex ? "*" : "") + Path.GetFileNameWithoutExtension(t.Path) +
                (CinecorePlayer2025.Audio.AutomixAnalyzer.TryGet(t.Path) is { } p ? $" {p.Bpm:0.0}bpm {p.Camelot}" : ""))));
            return;
        }
        if (what == "fs-menu") { SetFsMenu(!_fsMenuOpen); return; }
        if (what == "vol-open") { _volumeOpen = !_volumeOpen; _volumeOpenAt = NowMs; StartBarAnimation(); return; }
        if (what.StartsWith("fstab:")) { _fsTab = int.Parse(what[6..]); _specSmooth = null; StartFsFrames(); InvalidateVisual(); return; }
        if (what.StartsWith("tab:")) { _musicOverview = false; _musicTab = int.Parse(what[4..]); _scroll = _scrollTarget = 0; }
        else if (what.StartsWith("artist:")) OpenArtist(what[7..]);
        else if (what == "shuffle") ToggleShuffle(); else if (what == "repeat") _repeat = !_repeat; else if (what == "queue") _queueOpen = !_queueOpen; else if (what == "close-album") CloseAlbum();
        InvalidateVisual();
    }
}
