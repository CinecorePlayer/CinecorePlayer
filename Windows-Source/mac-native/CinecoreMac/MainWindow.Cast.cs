using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

// Tasti Cast e Qualita' della fascia in alto dell'HUD (PlayerForm.LoadingArt.cs su Windows: SyncCastButton,
// ShowCastSheet, ShowQualityMenu).
public partial class MainWindow
{
    private string? _castFor;
    private LibraryItem? _dlnaItem;
    private List<(string Name, string Role, string? Photo)> _castList = new();

    /// <summary>Gli interpreti del film che parte: dalla libreria, altrimenti dal server o dai metadati salvati.</summary>
    private void PrepareCast(string file)
    {
        _castFor = file;
        _castList = new();
        _hud.CastAvailable = false;
        var item = _library.ItemFor(file);
        if (item?.Cast.Count > 0) { _castList = item.Cast.ToList(); _hud.CastAvailable = true; return; }
        _ = Task.Run(async () =>
        {
            List<(string, string, string?)> found = new();
            try
            {
                if (item != null && item.IsNetwork) { await NetworkStore.LoadPeopleAsync(item); found = item.Cast.ToList(); }
                else if (!IsStream(file))
                {
                    var rich = MovieMetadataService.TryGetSavedRichMetadata(MetadataKey(file), AppLanguage.TmdbLocale(L10n.Language));
                    if (rich != null) found = rich.CastMembers.Select(c => (c.Name, c.Character, c.ProfilePath)).ToList();
                }
            }
            catch { }
            Dispatcher.UIThread.Post(() =>
            {
                if (_castFor != file || found.Count == 0) return;
                _castList = found;
                _hud.CastAvailable = true;
                _hud.InvalidateVisual();
            });
        });
    }

    private void ToggleCast()
    {
        if (_hud.CastOpen) { _hud.CloseCast(); return; }
        if (_castList.Count == 0 || _file == null) return;
        var item = _library.ItemFor(StreamIdentity(_file));
        // Riga dei dati come Windows: anno, durata, voto TMDb, due generi.
        var meta = new List<string>();
        if (item?.Year is int year) meta.Add(year.ToString());
        double seconds = _hud.Duration > 0 ? _hud.Duration : (item?.DurationMinutes ?? 0) * 60;
        if (seconds > 60) meta.Add((int)(seconds / 3600) > 0 ? $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60):00}m" : $"{(int)(seconds / 60)}m");
        if (item?.Rating is > 0) meta.Add("TMDb " + item.Rating.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        if (item?.Genres.Count > 0) meta.Add(string.Join(", ", item.Genres.Take(2)));
        string title = item?.Title is { Length: > 0 } t ? t : _hud.Title;
        _hud.OpenCast(title, string.Join("      ", meta), _castList);
    }

    private void SetStreamQualitySource(string file)
    {
        if (!IsStream(file) || IsWebVideo(file) || JellyfinClient.TryParseStream(file, out _, out _, out _))
            _dlnaItem = null;
        else if (_dlnaItem?.Path != file && _dlnaItem?.DlnaVideoResources?.Any(r => r.Path == file) != true)
            _dlnaItem = _library.ItemFor(file) is { IsNetwork: true } item ? item : null;
        _hud.QualityAvailable = IsYouTubeVideo || _dlnaItem != null || JellyfinClient.TryParseStream(file, out _, out _, out _);
        _hud.QualityLabel = "Qualità";
    }

    private string StreamIdentity(string file) => _dlnaItem is { } item &&
        (item.Path == file || item.DlnaVideoResources?.Any(r => r.Path == file) == true) ? item.Path : file;

    private void RefreshQualityLabel()
    {
        if (!_hud.QualityAvailable) return;
        // YouTube: la qualita' nominale (1080p), non l'altezza in pixel di un film 2,39:1 (1038p).
        int height = IsYouTubeVideo && _webResolvedHeight > 0 ? _webResolvedHeight : _runtime.OutHeight > 0 ? _runtime.OutHeight
            : _dlnaItem?.DlnaVideoResources?.FirstOrDefault(r => r.Path == _file)?.Height ?? _probe?.Height ?? 0;
        _hud.QualityLabel = height > 0 ? $"{height}p" : "Qualità";
        _hud.InvalidateVisual();
    }

    /// <summary>La scelta dipende dalle varianti effettivamente disponibili per il flusso.</summary>
    private void ShowQualityMenu(Point at)
    {
        if (_menu.IsOpen || CurrentStreamQualityEntries() is not { Count: > 0 } entries) return;
        _hud.ResetPanels();
        _hud.FadeOutNow();
        _menu.Open(entries, new Point(Math.Max(8, at.X - 220), at.Y), null);
        if (!_menu.IsOpen) _hud.MenuClosed(at);
    }

    private List<MenuEntry>? CurrentStreamQualityEntries()
    {
        if (_file == null) return null;
        if (IsYouTubeVideo) return BuildYouTubeQualityEntries();
        if (_dlnaItem is { } item) return BuildDlnaQualityEntries(item);
        return JellyfinClient.TryParseStream(_file, out _, out _, out _) ? BuildJellyfinQualityEntries(_file) : null;
    }

    private List<MenuEntry> BuildYouTubeQualityEntries()
    {
        return new[] { 0 }.Concat(_webHeights).Select(height =>
            new MenuEntry(height == 0 ? "Automatica" : $"{height}p", null, () =>
                {
                    if (IsYouTubeVideo && _webPlayingMaxHeight != height)
                        OpenWebAddress(_webPage!, _titleOverride, resume: true, requestedHeight: height);
                }) { Checked = _webPlayingMaxHeight == height }).ToList();
    }

    private List<MenuEntry> BuildDlnaQualityEntries(LibraryItem item)
    {
        var variants = item.DlnaVideoResources;
        if (variants is not { Count: > 1 })
        {
            string? label = variants?.FirstOrDefault() is { Width: > 0, Height: > 0 } original
                ? $"{original.Width} × {original.Height}" : item.ResolutionLabel;
            return new() { new MenuEntry("Originale", null, () => { }) { Value = label, Checked = true },
                new MenuEntry("Il server offre una sola versione") { Enabled = false } };
        }
        var current = _file;
        return variants.Select(resource =>
        {
            bool primary = resource.Path == item.Path;
            string label = resource.Width > 0 && resource.Height > 0
                ? $"{resource.Width} × {resource.Height}" : primary ? "Originale" : "Versione alternativa";
            return new MenuEntry(label, null, () =>
            {
                if (_file != current || resource.Path == _file) return;
                RestartStream(resource.Path, _hud.Title, _hud.Position, !_hud.IsPlaying);
            })
            {
                Checked = resource.Path == current,
                Value = resource.BytesPerSecond > 0
                    ? $"{resource.BytesPerSecond * 8.0 / 1_000_000:0.##} Mbps"
                    : primary && resource.Height > 0 ? "Originale" : null,
            };
        }).ToList();
    }

    private void RestartStream(string path, string? title, double position, bool paused)
    {
        StartVideo(path, title, rendererOverride: _renderer, replacingStream: true);
        _resumeOnReady = position > 0 ? position : null;
        _pauseOnAudioReady = paused;
    }

    private List<MenuEntry> BuildJellyfinQualityEntries(string file)
    {
        int sourceHeight = _probe?.Height > 0 ? _probe.Height : _library.ItemFor(file)?.VideoHeight ?? 0;
        int selected = JellyfinClient.TranscodeHeight;
        var heights = new[] { 2160, 1440, 1080, 720, 480, 360 }
            .Where(h => h <= sourceHeight).Append(sourceHeight).Append(selected)
            .Where(h => h > 0).Distinct().OrderByDescending(h => h);
        var resolutions = new[] { 0 }.Concat(heights).Select(height =>
            new MenuEntry(height == 0 ? "Automatica" : $"{height}p", null, () =>
            {
                if (_file != file || JellyfinClient.TranscodeHeight == height) return;
                JellyfinClient.TranscodeHeight = height;
                RestartStream(file, _titleOverride, _hud.Position, !_hud.IsPlaying);
            }) { Checked = selected == height }).ToList();
        return new List<MenuEntry>
        {
            new("Risoluzione", null) { Children = resolutions, Value = selected > 0 ? $"{selected}p" : "Automatica" },
            new("Bitrate", null) { Children = BuildStreamQualityEntries(),
                Value = JellyfinClient.TranscodeBitrate > 0 ? JellyfinClient.TranscodeLabel(JellyfinClient.TranscodeBitrate, false) : "Automatico" },
        };
    }

    private List<MenuEntry> BuildStreamQualityEntries() =>
        JellyfinClient.TranscodeChoices.Select(bitrate => new MenuEntry(
            bitrate == 0 && JellyfinClient.TranscodeHeight > 0 ? "Automatico" : JellyfinClient.TranscodeLabel(bitrate, false), null, () =>
        {
            if (bitrate == JellyfinClient.TranscodeBitrate || _file == null) return;
            JellyfinClient.TranscodeBitrate = bitrate;
            RestartStream(_file, _titleOverride, _hud.Position, !_hud.IsPlaying);
        }) { Checked = bitrate == JellyfinClient.TranscodeBitrate }).ToList();
}
