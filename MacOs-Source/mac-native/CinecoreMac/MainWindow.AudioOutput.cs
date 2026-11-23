using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;

namespace CinecoreMac;

public sealed partial class MainWindow
{
    private DispatcherTimer? _audioOutputWatch;
    private string? _activeAudioOutput;
    private string? _pendingAudioOutput;
    private bool _pendingDefaultFallback;

    private void StartAudioOutputWatch()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_QA_MUTE") == "1") return;
        RefreshAudioOutput(initial: true);
        _audioOutputWatch = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => RefreshAudioOutput(initial: false));
        _audioOutputWatch.Start();
    }

    internal static (MacAudioDevices.Device? Output, bool SwitchToDefault) ResolveAudioOutput(
        IReadOnlyList<MacAudioDevices.Device> devices, string uid)
    {
        var selected = uid.Length > 0 ? devices.FirstOrDefault(d => d.Uid == uid) : null;
        var output = selected ?? devices.FirstOrDefault(d => d.IsDefault);
        return (output, uid.Length > 0 && selected == null && output != null);
    }

    private void RefreshAudioOutput(bool initial)
    {
        var devices = MacAudioDevices.Outputs();
        var (output, fallback) = ResolveAudioOutput(devices, MacSettings.AudioDeviceUid);
        if (output == null) { _pendingAudioOutput = null; return; }
        // Uscita scelta sparita durante un cambio di frequenza (HDMI che si rinegozia): torna da sola, non si cambia
        // uscita e soprattutto non si cancella la scelta (prima l'audio saltava dal Marantz alle casse e indietro).
        if (fallback && MacDisplayRate.Settling) { _pendingAudioOutput = null; return; }
        if (initial || _activeAudioOutput == null)
        {
            _activeAudioOutput = output.Uid;
            if (fallback) SetAudioPreference("", "");
            return;
        }
        if (output.Uid == _activeAudioOutput && !fallback)
        {
            _pendingAudioOutput = null;
            return;
        }
        // Le cuffie Bluetooth possono cambiare profilo e uscita due volte durante la connessione.
        if (_pendingAudioOutput != output.Uid || _pendingDefaultFallback != fallback)
        {
            _pendingAudioOutput = output.Uid;
            _pendingDefaultFallback = fallback;
            return;
        }
        if (_file != null && !_videoReady) return;
        _activeAudioOutput = output.Uid;
        _pendingAudioOutput = null;
        if (fallback) SetAudioPreference("", "");
        ApplyAudioOutput(MacSettings.AudioDeviceUid, routeChanged: true);
    }

    private void ApplyAudioOutput(string uid, bool routeChanged)
    {
        _library.AudioOutputChanged(uid);
        if (_file != null && _video.IsRunning)
        {
            // Solo se la sorgente non permette di cambiare l'audio da solo (dischi, traccia esterna con madVR/MPC-VR).
            if (!_video.ChangeAudioDevice(uid, routeChanged)) ReopenSame(preservePause: true);
            else ApplyVolume(); // volume e amplificazione sull'audio rifatto
        }
    }

    private void SetAudioOutput(string uid, string name)
    {
        var (output, fallback) = ResolveAudioOutput(MacAudioDevices.Outputs(), uid);
        if (fallback) { uid = ""; name = ""; }
        SetAudioPreference(uid, name);
        _activeAudioOutput = output?.Uid;
        _pendingAudioOutput = null;
        ApplyAudioOutput(uid, routeChanged: false);
    }

    private static void SetAudioPreference(string uid, string name)
    {
        MacSettings.AudioDeviceUid = uid;
        MacSettings.AudioDeviceName = name;
    }
}
