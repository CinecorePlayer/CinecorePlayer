using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CinecoreMac;

/// <summary>
/// Tasti multimediali e pannello "In riproduzione" di Windows (SystemMediaTransportControls della finestra), al posto
/// di MPRemoteCommandCenter / MPNowPlayingInfoCenter del Mac. Stessi comandi: "toggle", "play", "pause", "stop",
/// "next", "previous", "seek:&lt;secondi&gt;".
/// </summary>
internal static class MacMediaKeys
{
    [ComImport, Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a"), InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface ISystemMediaTransportControlsInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, in Guid riid);
    }

    private static readonly Guid SmtcIid = new("99FA3FF4-1742-42A6-902E-087D41F965EC");
    private static SystemMediaTransportControls? _smtc;
    private static string? _artPath;

    public static event Action<string>? Command;

    public static void Start()
    {
        if (_smtc != null) return;
        if (Environment.GetEnvironmentVariable("CINECORE_QA_BACKGROUND") == "1" && Environment.GetEnvironmentVariable("CINECORE_QA_MEDIAKEYS") != "1") return;
        try
        {
            IntPtr hwnd = WinNative.Handle((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow);
            if (hwnd == IntPtr.Zero) return;
            var interop = WinRT.CastExtensions.As<ISystemMediaTransportControlsInterop>(
                WinRT.ActivationFactory.Get("Windows.Media.SystemMediaTransportControls"));
            IntPtr abi = interop.GetForWindow(hwnd, SmtcIid);
            _smtc = SystemMediaTransportControls.FromAbi(abi);
            Marshal.Release(abi);
            _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsStopEnabled = true;
            _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
            _smtc.ButtonPressed += (_, e) =>
            {
                string? cmd = e.Button switch
                {
                    SystemMediaTransportControlsButton.Play => "play",
                    SystemMediaTransportControlsButton.Pause => "pause",
                    SystemMediaTransportControlsButton.Stop => "stop",
                    SystemMediaTransportControlsButton.Next => "next",
                    SystemMediaTransportControlsButton.Previous => "previous",
                    _ => null,
                };
                if (cmd != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => Command?.Invoke(cmd));
            };
            _smtc.PlaybackPositionChangeRequested += (_, e) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Command?.Invoke("seek:" + e.RequestedPlaybackPosition.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
            _smtc.IsEnabled = false;
        }
        catch (Exception ex) { Console.Error.WriteLine("[tasti] controlli multimediali di Windows non disponibili: " + ex.Message); _smtc = null; }
    }

    public static void Update(string? title, string? artist, double position, double duration, bool playing, string? artPath = null)
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try
        {
            if (title == null)
            {
                smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                smtc.IsEnabled = false;
                return;
            }
            smtc.IsEnabled = true;
            var updater = smtc.DisplayUpdater;
            bool changed = updater.Type != MediaPlaybackType.Music || updater.MusicProperties.Title != title || updater.MusicProperties.Artist != (artist ?? "") || artPath != _artPath;
            if (changed)
            {
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = title;
                updater.MusicProperties.Artist = artist ?? "";
                if (artPath != _artPath)
                {
                    _artPath = artPath;
                    updater.Thumbnail = !string.IsNullOrEmpty(artPath) && File.Exists(artPath)
                        ? RandomAccessStreamReference.CreateFromFile(StorageFile.GetFileFromPathAsync(Path.GetFullPath(artPath)).AsTask().GetAwaiter().GetResult())
                        : null;
                }
                updater.Update();
            }
            smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            if (duration > 0)
            {
                var timeline = new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero, MinSeekTime = TimeSpan.Zero,
                    Position = TimeSpan.FromSeconds(Math.Clamp(position, 0, duration)),
                    MaxSeekTime = TimeSpan.FromSeconds(duration), EndTime = TimeSpan.FromSeconds(duration),
                };
                smtc.UpdateTimelineProperties(timeline);
            }
        }
        catch { }
    }

    internal static void TestCommand(string command) => Command?.Invoke(command);
}
