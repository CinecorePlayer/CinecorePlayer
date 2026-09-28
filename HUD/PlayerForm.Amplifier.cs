#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using NAudio.CoreAudioApi;
namespace CinecorePlayer2025;
public sealed partial class PlayerForm
{
    private AmplifierDevice? _networkVolumeDevice = AmplifierControl.Load();
    private readonly CancellationTokenSource _networkVolumeLifetime = new();
    private bool _networkVolumeBusy;
    private float? _pendingNetworkVolume;
    private string _networkEndpoint = "", _networkRenderer = "";
    private long _networkEndpointChecked;

    private string CurrentAudioEndpoint()
    {
        string renderer = _engine is LibMpvPlaybackEngine ? "" : _selectedAudioRendererName ?? "";
        if (_engine is DirectShowUnifiedEngine directShow && directShow.ActiveAudioRendererDeviceName != "?")
            renderer = directShow.ActiveAudioRendererDeviceName;
        if (_networkRenderer == renderer && Environment.TickCount64 - _networkEndpointChecked < 1500) return _networkEndpoint;
        _networkRenderer = renderer; _networkEndpointChecked = Environment.TickCount64; _networkEndpoint = "";
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (string.IsNullOrWhiteSpace(renderer) || renderer.Contains("Default", StringComparison.OrdinalIgnoreCase))
            {
                using var current = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return _networkEndpoint = current.ID;
            }
            foreach (var endpoint in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (endpoint)
                    if (renderer.Contains(endpoint.FriendlyName, StringComparison.OrdinalIgnoreCase))
                        _networkEndpoint = endpoint.ID;
        }
        catch { }
        return _networkEndpoint;
    }

    private bool IsNetworkVolumeActive => _engine != null && !IsPhotoMode &&
        !string.IsNullOrEmpty(_networkVolumeDevice?.AudioEndpointId) &&
        string.Equals(_networkVolumeDevice.AudioEndpointId, CurrentAudioEndpoint(), StringComparison.OrdinalIgnoreCase);

    private void ShowAmplifierControl()
    {
        using var dialog = new AmplifierControlForm(UiEnglish);
        dialog.ShowDialog(this);
        _networkVolumeDevice = AmplifierControl.Load();
        _networkEndpointChecked = 0;
        _hud?.Invalidate();
        UpdateMusicTransport();
        if (IsNetworkVolumeActive) _ = SendNetworkVolumeAsync(_networkVolumeDevice!, 0, null, false);
    }

    private bool TryNetworkVolume(int step = 0, float? value = null, bool toggleMute = false)
    {
        if (!IsNetworkVolumeActive) return false;
        ApplyVolume(1f);
        // Key repeat must not queue delayed volume changes after release.
        if (_networkVolumeBusy && value.HasValue) _pendingNetworkVolume = value;
        else if (!_networkVolumeBusy) _ = SendNetworkVolumeAsync(_networkVolumeDevice!, step, value, toggleMute);
        return true;
    }

    private async Task SendNetworkVolumeAsync(AmplifierDevice device, int step, float? value, bool toggleMute)
    {
        if (_networkVolumeBusy || _closingForExit) return;
        _networkVolumeBusy = true;
        try
        {
            using var client = new AmplifierControl(device);
            var token = _networkVolumeLifetime.Token;
            var state = toggleMute
                ? await client.ReadAsync(token)
                : step != 0 ? await client.StepAsync(step, token)
                : value.HasValue ? await client.SetVolumeAsync(value.Value, token)
                : await client.ReadAsync(token);
            if (toggleMute && IsNetworkVolumeActive && device == _networkVolumeDevice)
                state = await client.MuteAsync(!state.Muted, token);
            if (IsDisposed || _closingForExit || device != _networkVolumeDevice || !IsNetworkVolumeActive) return;
            _hud.SetMuted(state.Muted);
            _hud.SetExternalVolume(state.Level);
            _hud.ShowVolumeOsd(1400);
            _lblStatus.Text = device.Name + " · " + state.Volume + (state.Muted ? " · Mute" : "");
            ShowRemoteOsd(GetVolumeOsdSvg(state.Level, state.Muted), state.Muted ? 0 : state.Level, 1200);
            UpdateMusicTransport();
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !_closingForExit)
                _lblStatus.Text = Tx("Controllo IP non disponibile: ", "IP control unavailable: ") + ex.Message;
        }
        finally
        {
            _networkVolumeBusy = false;
            var pending = _pendingNetworkVolume;
            _pendingNetworkVolume = null;
            if (pending.HasValue && !_closingForExit && !IsDisposed && device == _networkVolumeDevice && IsNetworkVolumeActive)
                _ = SendNetworkVolumeAsync(device, 0, pending, false);
        }
    }
}
