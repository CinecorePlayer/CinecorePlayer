using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;
using SkiaSharp;

namespace CinecoreMac;

public sealed partial class MainWindow
{
    private sealed record RemoteFilmData(string Title, string Meta, string? Overview, string? Poster, string? Backdrop,
        string? Preview, object[] Cast, string[] Tech, object? Hdr)
    {
        public string? Artwork => Backdrop ?? Poster ?? Preview;
    }
    private RemoteFilmData? _remoteFilm;
    private string? _remoteFilmPath;
    private long _remoteFilmAt;
    private bool _remoteFilmLoading;
    private bool _remoteFilmPreviewTried;

    private RemoteFilmData? RemoteFilm()
    {
        string? path = _gatePending?.File ?? _file;
        if (path != _remoteFilmPath)
        {
            _remoteFilmPath = path;
            _remoteFilm = null;
            _remoteFilmAt = 0;
            _remoteFilmPreviewTried = false;
        }
        if (path == null || (!_remoteStateRequested && !_remoteCompanionRequested)) return _remoteFilm;
        long now = Environment.TickCount64;
        if (_remoteFilmLoading || _remoteFilmAt != 0 && now - _remoteFilmAt < 5000) return _remoteFilm;
        _remoteFilmAt = now;
        _remoteFilmLoading = true;
        var item = _gateItem ?? _library.ItemFor(path);
        var probe = _probe;
        string title = item?.Title ?? _gatePending?.Title ?? _hud.Title;
        string? poster = item?.ArtPath, backdrop = item?.WideArtPath, overview = item?.Overview;
        string? preview = _remoteFilm?.Preview;
        bool makePreview = !_remoteFilmPreviewTried && probe is { HasVideo: true } && !IsStream(path) && _video.ActiveDvdNav == null;
        _remoteFilmPreviewTried |= makePreview;
        double position = _hud.Position;
        var cast = (_castFor == path && _castList.Count > 0 ? _castList : item?.Cast)?.Take(24).ToArray() ?? [];
        double seconds = _hud.Duration > 0 ? _hud.Duration : (item?.DurationMinutes ?? 0) * 60;
        string meta = string.Join(" · ", new[] { item?.Year?.ToString(), seconds >= 60 ? $"{(int)(seconds / 60)} min" : null }
            .Where(s => s != null));
        _ = Task.Run(async () =>
        {
            try
            {
                if (item?.IsNetwork == true)
                {
                    await CacheNetworkArt(backdrop);
                    await CacheNetworkArt(poster);
                }
                else if (!IsStream(path))
                {
                    backdrop ??= MovieMetadataService.GetCachedBackdropPath(MetadataKey(path));
                    poster ??= MovieMetadataService.GetCachedPosterPath(MetadataKey(path));
                    if ((backdrop == null || poster == null || overview == null) && MovieMetadataService.HasCustomTmdbApiKey)
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_remoteFilmPath == path) LibraryStore.QueueArtwork(item ?? new LibraryItem { Path = path, Title = title }, includeBackdrop: true);
                        });
                    if (overview == null || cast.Length == 0)
                    {
                        var rich = MovieMetadataService.TryGetSavedRichMetadata(MetadataKey(path), AppLanguage.TmdbLocale(CinecoreMac.L10n.Language));
                        overview ??= rich?.Overview;
                        if (cast.Length == 0 && rich != null)
                            cast = rich.CastMembers.Take(24).Select(c => (c.Name, c.Character, c.ProfilePath)).ToArray();
                    }
                }
                string? localBackdrop = RemoteArt(backdrop), localPoster = RemoteArt(poster);
                var data = new RemoteFilmData(title, meta, overview, localPoster, localBackdrop, preview,
                    cast.Select(c => (object)new { name = c.Name, character = c.Role, photo = RemoteArt(c.Photo) }).ToArray(),
                    RemoteTechnicalData(probe), RemoteHdrData(path, probe));
                Dispatcher.UIThread.Post(() => { if (_remoteFilmPath == path) _remoteFilm = data; });
                if (makePreview && data.Artwork == null && File.Exists(path))
                {
                    string target = RemotePreviewPath(path);
                    if (!File.Exists(target))
                    {
                        double sample = Math.Clamp(Math.Max(position, Math.Min(30, seconds * .15)), 0, Math.Max(0, seconds - 1));
                        var frames = await MenuAmbient.GrabFramesAsync(path, [sample], 768, probe!,
                            TimeSpan.FromSeconds(10), CancellationToken.None);
                        if (frames[0] is not { } frame || !SaveRemoteFrame(target, frame)) return;
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_remoteFilmPath == path && _remoteFilm == data) _remoteFilm = data with { Preview = target };
                    });
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("telecomando, metadati: " + ex.GetType().Name); }
            finally { Dispatcher.UIThread.Post(() => _remoteFilmLoading = false); }
        });
        return _remoteFilm;
    }

    private static string RemotePreviewPath(string path)
    {
        var file = new FileInfo(path);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")));
        return Path.Combine(LibraryStore.AppDataDir, "remoteArt", "frames", key + ".jpg");
    }

    private static bool SaveRemoteFrame(string target, MenuAmbient.Frame frame)
    {
        using var bitmap = new SKBitmap(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        Marshal.Copy(frame.Bgra, 0, bitmap.GetPixels(), checked(frame.Width * frame.Height * 4));
        using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 82);
        if (jpeg == null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + ".tmp";
        try
        {
            File.WriteAllBytes(temp, jpeg.ToArray());
            File.Move(temp, target, overwrite: true);
        }
        finally { File.Delete(temp); }
        return true;
    }

    private static async Task CacheNetworkArt(string? art)
    {
        if (art != null && art.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            await NetworkStore.CachedArtAsync(art);
    }

    private static string[] RemoteTechnicalData(ProbeData? probe)
    {
        if (probe == null) return [];
        var rows = new List<string>();
        if (probe.Width > 0) rows.Add($"Risoluzione: {probe.Width}×{probe.Height}");
        if (probe.VideoCodec.Length > 0) rows.Add("Video: " + probe.VideoCodec.ToUpperInvariant() +
            (probe.VideoBits > 0 ? $" · {probe.VideoBits} bit" : ""));
        if (probe.AudioCodec.Length > 0) rows.Add("Audio: " + probe.AudioCodec.ToUpperInvariant() +
            (probe.AudioChannels > 0 ? $" · {probe.AudioChannels} canali" : ""));
        return rows.ToArray();
    }

    private static object? RemoteHdrData(string path, ProbeData? probe)
    {
        if (IsStream(path) || !IsHdrProbe(probe) ||
            HdrAnalyzer.TryLoadCached(path, 1) is not { IsHdr: true, Error: null, Samples.Count: > 0 } a) return null;
        return new
        {
            formats = (a.Transfer == "HLG" ? "HLG" : "HDR10") + (a.DolbyVisionProfile is int dv ? $" · Dolby Vision {dv}" : "") +
                (a.Hdr10Plus ? " · HDR10+" : ""),
            peak = Math.Round(a.MeasuredPeakNits), brightestAverage = Math.Round(a.MeasuredMaxAverageNits),
            filmAverage = Math.Round(a.OverallAverageNits), typical = Math.Round(a.MedianNits),
            stops = a.MeasuredPeakNits > 0 ? Math.Round(Math.Log2(a.MeasuredPeakNits / Math.Max(0.005, a.BlackNits)), 1) : 0,
            above100 = Math.Round(a.ShareAbove100 * 100, 1), rec709 = Math.Round(a.GamutRec709 * 100, 1),
            p3 = Math.Round(a.GamutP3 * 100, 1), bt2020 = Math.Round(a.GamutRec2020 * 100, 1),
        };
    }
}
