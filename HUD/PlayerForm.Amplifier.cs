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
    private bool _pendingNetworkVolumeOsd;
    private string? _networkVolumeText;      // volume as the receiver reports it ("-35 dB")
    private long _networkVolumeReadAt;
    // Livello dell'amplificatore all'ultima lettura: e' quello che il cursore mostrava quando
    // l'utente ha cominciato a muoverlo. null finche' non e' mai stato letto.
    private float? _networkLevelSynced;
    private string _networkEndpoint = "", _networkRenderer = "";
    private long _networkEndpointChecked;

    // ---- Sicurezza del volume dell'amplificatore ----
    // Il cursore del player va da 0 a 1 e sull'amplificatore 1 vuol dire 0 dB, il livello di
    // riferimento: fortissimo. Prima che il volume reale venisse letto il cursore stava a 100%,
    // e bastava toccarlo (o toccare quello del telefono) per mandare l'amplificatore al massimo.
    // Regole: il player non manda mai un livello senza aver letto quello attuale; ogni comando
    // puo' alzare al massimo di 3 dB; oltre il tetto di sicurezza non alza affatto (si puo'
    // sempre abbassare); a ogni avvio riparte dall'ultimo volume usato, mai piu' forte.
    private const float AmplifierSafeCeiling = 0.75f;   // -20 dB
    private const float AmplifierFirstStart = 0.625f;   // -30 dB, quando non c'e' ancora un volume ricordato
    private const float AmplifierMaxRaise = 0.0375f;    // +3 dB per comando
    private bool _amplifierSessionStarted;              // l'avvio sicuro e' gia' stato fatto: le letture si possono ricordare
    private float? _amplifierRemembered;
    private bool _amplifierRememberedLoaded;
    private static string AmplifierLevelPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "amplifier-level.txt");

    /// <summary>Ultimo volume dell'amplificatore usato con il player (0..1), se ce n'e' uno.</summary>
    private float? RememberedAmplifierLevel()
    {
        if (_amplifierRememberedLoaded) return _amplifierRemembered;
        _amplifierRememberedLoaded = true;
        try
        {
            if (System.IO.File.Exists(AmplifierLevelPath) &&
                float.TryParse(System.IO.File.ReadAllText(AmplifierLevelPath).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float saved) &&
                saved >= 0f && saved <= 1f)
                _amplifierRemembered = saved;
        }
        catch { }
        return _amplifierRemembered;
    }

    private void RememberAmplifierLevel(float level)
    {
        // Solo dopo l'avvio sicuro: il volume trovato all'accensione puo' essere quello lasciato da un'altra sorgente.
        if (!_amplifierSessionStarted) return;
        level = Math.Clamp(level, 0f, AmplifierSafeCeiling);
        if (RememberedAmplifierLevel() is float known && Math.Abs(known - level) < 0.006f) return;
        _amplifierRemembered = level;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AmplifierLevelPath)!);
            System.IO.File.WriteAllText(AmplifierLevelPath, level.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { }
    }

    /// <summary>Fine della riproduzione: si ricorda il volume a cui si stava ascoltando (anche se cambiato dal telecomando dell'amplificatore).</summary>
    private void RememberAmplifierLevelAtStop()
    {
        var device = _networkVolumeDevice;
        if (device == null || string.IsNullOrEmpty(device.AudioEndpointId) || !_amplifierSessionStarted || _closingForExit) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new AmplifierControl(device);
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var state = await client.ReadAsync(limit.Token).ConfigureAwait(false);
                RememberAmplifierLevel(state.Level);
            }
            catch { }
        });
    }

    private string CurrentAudioEndpoint()
    {
        string renderer = _engine is LibMpvPlaybackEngine ? "" : _selectedAudioRendererName ?? "";
        if (_engine is DirectShowUnifiedEngine directShow && directShow.ActiveAudioRendererDeviceName != "?")
            renderer = directShow.ActiveAudioRendererDeviceName;
        bool sameRenderer = _networkRenderer == renderer;
        if (sameRenderer && Environment.TickCount64 - _networkEndpointChecked < 1500) return _networkEndpoint;
        _networkEndpointChecked = Environment.TickCount64;
        if (!sameRenderer)
        {
            // Uscita audio nuova: la risposta serve subito (da qui dipende l'avvio sicuro dell'amplificatore).
            _networkRenderer = renderer;
            return _networkEndpoint = ResolveAudioEndpoint(renderer);
        }
        // Stessa uscita di prima: e' solo il ricontrollo periodico. Chiedere a Windows l'elenco delle uscite
        // costa un quarto di secondo e avveniva nel thread dell'interfaccia a ogni comparsa dell'HUD e poi
        // ogni secondo e mezzo: HUD in ritardo, menu e schede a scatti. Ora si ricontrolla in background e
        // intanto vale la risposta precedente.
        if (Interlocked.Exchange(ref _networkEndpointResolving, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                string found = ResolveAudioEndpoint(renderer);
                bool queued = TryBeginInvokeOnUi(() =>
                {
                    _networkEndpointResolving = 0;
                    if (_networkRenderer != renderer || _networkEndpoint == found) return;
                    _networkEndpoint = found;
                    try { _hud?.Invalidate(); UpdateMusicTransport(); } catch { }
                });
                if (!queued) _networkEndpointResolving = 0;
            });
        }
        return _networkEndpoint;
    }

    private int _networkEndpointResolving;

    private static string ResolveAudioEndpoint(string renderer)
    {
        string found = "";
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (string.IsNullOrWhiteSpace(renderer) || renderer.Contains("Default", StringComparison.OrdinalIgnoreCase))
            {
                using var current = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return current.ID;
            }
            foreach (var endpoint in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (endpoint)
                    if (renderer.Contains(endpoint.FriendlyName, StringComparison.OrdinalIgnoreCase))
                        found = endpoint.ID;
        }
        catch { }
        return found;
    }

    private bool IsNetworkVolumeActive => _engine != null && !IsPhotoMode &&
        !string.IsNullOrEmpty(_networkVolumeDevice?.AudioEndpointId) &&
        string.Equals(_networkVolumeDevice.AudioEndpointId, CurrentAudioEndpoint(), StringComparison.OrdinalIgnoreCase);

    private void ShowAmplifierControl()
    {
        using var sheetLayout = SheetPresenter.Layout(this);
        using var dialog = new AmplifierControlForm(UiEnglish);
        SheetPresenter.ShowDialog(dialog, this);
        _networkVolumeDevice = AmplifierControl.Load();
        _networkLevelSynced = null;
        _networkEndpointChecked = 0;
        _hud?.Invalidate();
        UpdateMusicTransport();
        _networkVolumeText = null;
        RefreshNetworkVolume(force: true);
    }

    /// <summary>Reads the receiver's current volume into the HUD without showing anything on screen.</summary>
    private void RefreshNetworkVolume(bool force = false)
    {
        if (_networkVolumeBusy || !IsNetworkVolumeActive) return;
        // The receiver may have been changed with its own remote: read again, but not on every HUD wake.
        if (!force && Environment.TickCount64 - _networkVolumeReadAt < 3000) return;
        _ = SendNetworkVolumeAsync(_networkVolumeDevice!, 0, null, false, osd: false);
    }

    /// <param name="osd">True only for the remote control: the big on-screen overlay is its feedback.
    /// Mouse and keyboard already have the HUD slider.</param>
    private bool TryNetworkVolume(int step = 0, float? value = null, bool toggleMute = false, bool osd = false)
    {
        if (!IsNetworkVolumeActive) return false;
        ApplyVolume(1f);
        // The slider follows the hand at once; the receiver's answer settles it after the last value.
        // Il cursore si ferma dove il comando puo' davvero arrivare (+3 dB dal livello letto, mai oltre il
        // tetto): prima seguiva la mano, poi alla risposta dell'amplificatore tornava indietro e la sua
        // posizione non corrispondeva a quanto si era alzato. Continuando a trascinare, a ogni risposta il
        // limite si sposta e il cursore sale a passi, insieme al volume vero.
        if (value.HasValue && _networkLevelSynced is float level && value.Value > level)
            value = Math.Min(value.Value, Math.Min(level + AmplifierMaxRaise, Math.Max(level, AmplifierSafeCeiling)));
        if (value.HasValue) try { _hud.SetExternalVolume(value.Value); } catch { }
        // Key repeat must not queue delayed volume changes after release.
        if (_networkVolumeBusy && value.HasValue) { _pendingNetworkVolume = value; _pendingNetworkVolumeOsd = osd; }
        else if (!_networkVolumeBusy) _ = SendNetworkVolumeAsync(_networkVolumeDevice!, step, value, toggleMute, osd);
        return true;
    }

    private async Task SendNetworkVolumeAsync(AmplifierDevice device, int step, float? value, bool toggleMute, bool osd)
    {
        if (_networkVolumeBusy || _closingForExit) return;
        _networkVolumeBusy = true;
        try
        {
            using var client = new AmplifierControl(device);
            var token = _networkVolumeLifetime.Token;
            bool limited = false;
            if (value.HasValue || step > 0)
            {
                // Mai un comando che alza senza sapere da dove si parte.
                var now = await client.ReadAsync(token);
                if (value.HasValue)
                {
                    // Il cursore e' una posizione assoluta, ma chi lo muove pensa "un po' di piu'" o "un po'
                    // di meno" rispetto a quello che vede. Se nel frattempo l'amplificatore e' stato mosso
                    // (suo telecomando, altra app, avvio sicuro) la posizione vista era vecchia: alzando il
                    // cursore da 50 a 53 con l'amplificatore gia' a 60 il volume scendeva. Vale il gesto:
                    // stessa direzione e stessa ampiezza, a partire dal livello vero.
                    if (_networkLevelSynced is not float shown)
                        value = now.Level;      // mai letto: questa volta si allinea soltanto il cursore
                    else if (Math.Abs(shown - now.Level) > 0.004f)
                        value = Math.Clamp(now.Level + (value.Value - shown), 0f, 1f);
                }
                if (value.HasValue && value.Value > now.Level)
                {
                    float allowed = Math.Min(now.Level + AmplifierMaxRaise, Math.Max(now.Level, AmplifierSafeCeiling));
                    if (value.Value > allowed + 0.0005f) limited = true;
                    value = Math.Min(value.Value, allowed);
                }
                else if (step > 0 && now.Level >= AmplifierSafeCeiling - 0.001f)
                {
                    step = 0;
                    limited = true;
                }
            }
            var state = toggleMute
                ? await client.ReadAsync(token)
                : step != 0 ? await client.StepAsync(step, token)
                : value.HasValue ? await client.SetVolumeAsync(value.Value, token)
                : await client.ReadAsync(token);
            if (toggleMute && IsNetworkVolumeActive && device == _networkVolumeDevice)
                state = await client.MuteAsync(!state.Muted, token);
            if (IsDisposed || _closingForExit || device != _networkVolumeDevice || !IsNetworkVolumeActive) return;
            _networkLevelSynced = state.Level;
            NoteAmplifierVolume(state, userChange: step != 0 || value.HasValue);
            _networkVolumeReadAt = Environment.TickCount64;
            _networkVolumeText = state.Volume;
            _hud.SetMuted(state.Muted);
            // A newer slider position is still waiting to be sent: this answer is already old, and
            // showing it made the knob jump back and forth under the pointer.
            if (!_pendingNetworkVolume.HasValue) _hud.SetExternalVolume(state.Level);
            else _hud.Invalidate();
            RememberAmplifierLevel(state.Level);
            if (limited && state.Level >= AmplifierSafeCeiling - 0.001f)
                ShowRemoteOsd(null, null, 2600, Tx("Limite di sicurezza del player: oltre si alza dall’amplificatore", "Player safety limit: go higher from the receiver itself"));
            bool changed = step != 0 || value.HasValue || toggleMute || limited;
            if (changed)
            {
                _lblStatus.Text = device.Name + " · " + state.Volume + (state.Muted ? " · Mute" : "");
                if (osd) ShowRemoteOsd(GetVolumeOsdSvg(state.Level, state.Muted), state.Muted ? 0 : state.Level, 1200, state.Muted ? null : state.Volume);
                else _hud.ShowVolumeOsd(1400);
            }
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
                _ = SendNetworkVolumeAsync(device, 0, pending, false, _pendingNetworkVolumeOsd);
        }
    }
}
