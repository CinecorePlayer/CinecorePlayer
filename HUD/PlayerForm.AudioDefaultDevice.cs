#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.Utilities;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Threading;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Con l'uscita su "Predefinito del PC" il player segue il dispositivo predefinito di
    // Windows anche quando cambia a riproduzione avviata (cuffie collegate, passaggio alla TV,
    // scelta dal mixer di Windows). Prima l'audio restava sul dispositivo di partenza finche'
    // non si riapriva il file.
    public sealed partial class PlayerForm
    {
        private MMDeviceEnumerator? _defaultDeviceEnumerator;
        private DefaultRenderDeviceListener? _defaultDeviceListener;
        private System.Windows.Forms.Timer? _defaultDeviceDebounce;
        private string? _defaultDeviceIdSeen;
        private string? _defaultDeviceNameSeen;

        private void InitializeDefaultAudioDeviceWatcher()
        {
            try
            {
                _defaultDeviceEnumerator = new MMDeviceEnumerator();
                try
                {
                    using var current = _defaultDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _defaultDeviceIdSeen = current.ID;
                    _defaultDeviceNameSeen = current.FriendlyName;
                }
                catch { } // nessuna uscita audio al momento: la prima notifica la registra

                _defaultDeviceListener = new DefaultRenderDeviceListener(id => TryBeginInvokeOnUi(() => OnDefaultRenderDeviceChanged(id)));
                _defaultDeviceEnumerator.RegisterEndpointNotificationCallback(_defaultDeviceListener);
                CinecorePlayer2025.Audio.CinecoreAudioEngine.OutputLost += OnCinecoreOutputLost;
                Disposed += (_, _) => ReleaseDefaultAudioDeviceWatcher();
            }
            catch (Exception ex)
            {
                Dbg.Warn("[AUDIO] default device watcher unavailable: " + ex.Message);
            }
        }

        // Disposed puo' arrivare due volte (chiusura della finestra, poi Dispose all'uscita da
        // Application.Run). Dopo il primo Dispose l'enumeratore di NAudio non ha piu' l'oggetto
        // COM e UnregisterEndpointNotificationCallback lancia NullReferenceException: i campi
        // si azzerano prima, cosi' il secondo passaggio non trova niente da rilasciare.
        private void ReleaseDefaultAudioDeviceWatcher()
        {
            var enumerator = Interlocked.Exchange(ref _defaultDeviceEnumerator, null);
            var listener = Interlocked.Exchange(ref _defaultDeviceListener, null);
            var debounce = Interlocked.Exchange(ref _defaultDeviceDebounce, null);
            CinecorePlayer2025.Audio.CinecoreAudioEngine.OutputLost -= OnCinecoreOutputLost;
            try { debounce?.Dispose(); } catch { }
            if (enumerator == null) return;
            try { if (listener != null) enumerator.UnregisterEndpointNotificationCallback(listener); }
            catch (Exception ex) { Dbg.Warn("[AUDIO] default device watcher release: " + ex.Message); }
            try { enumerator.Dispose(); } catch { }
        }

        private void OnDefaultRenderDeviceChanged(string? deviceId)
        {
            if (_defaultDeviceEnumerator == null) return; // notifica arrivata dopo la chiusura
            if (string.Equals(deviceId, _defaultDeviceIdSeen, StringComparison.OrdinalIgnoreCase))
                return;
            _defaultDeviceIdSeen = deviceId;

            // Windows notifica piu' ruoli (console, multimedia, comunicazioni) in rapida
            // successione, e collegando un dispositivo il predefinito puo' cambiare due volte.
            if (_defaultDeviceDebounce == null)
            {
                _defaultDeviceDebounce = new System.Windows.Forms.Timer { Interval = 700 };
                _defaultDeviceDebounce.Tick += (_, _) =>
                {
                    _defaultDeviceDebounce!.Stop();
                    try { ApplyDefaultRenderDeviceChange(); }
                    catch (Exception ex) { Dbg.Warn("[AUDIO] default device switch failed: " + ex.Message); }
                };
            }
            _defaultDeviceDebounce.Stop();
            _defaultDeviceDebounce.Start();
        }

        private long _outputLostAt;
        private int _outputLostStreak;
        private bool _outputLostPending;

        // L'uscita su cui suonava la musica non c'e' piu' (cuffie staccate, dispositivo disattivato, un altro programma
        // in esclusiva): senza questo il brano restava fermo in silenzio. Si riapre sullo stesso punto, sull'uscita
        // scelta se c'e' ancora o su quella predefinita; dopo tre tentativi ravvicinati ci si ferma.
        private void OnCinecoreOutputLost() => TryBeginInvokeOnUi(() =>
        {
            if (_closingForExit || IsDisposed || _engine is not CinecorePlayer2025.Audio.CinecoreAudioEngine || string.IsNullOrWhiteSpace(_currentPath))
                return;
            if (_outputLostPending) return; // una riapertura e' gia' in arrivo
            long now = Environment.TickCount64;
            _outputLostStreak = now - _outputLostAt < 15000 ? _outputLostStreak + 1 : 1;
            _outputLostAt = now;
            if (_outputLostStreak > 3)
            {
                Dbg.Warn("[AUDIO] output lost again and again: not reopening any more.");
                return;
            }
            _outputLostPending = true;
            var retry = new System.Windows.Forms.Timer { Interval = 900 };
            retry.Tick += (_, _) =>
            {
                retry.Stop();
                retry.Dispose();
                _outputLostPending = false;
                // Nel frattempo il cambio del dispositivo predefinito puo' avere gia' riaperto: il motore e' un altro.
                if (_closingForExit || IsDisposed || _engine is not CinecorePlayer2025.Audio.CinecoreAudioEngine || string.IsNullOrWhiteSpace(_currentPath))
                    return;
                if (_defaultDeviceDebounce is { Enabled: true }) return;
                Dbg.Log("[AUDIO] output lost, reopening.", Dbg.LogLevel.Info);
                try { ReopenSame(); }
                catch (Exception ex) { Dbg.Warn("[AUDIO] reopen after output loss failed: " + ex.Message); }
            };
            retry.Start();
        });

        private void ApplyDefaultRenderDeviceChange()
        {
            string? previousName = _defaultDeviceNameSeen;
            string? name = BitstreamReleasePulse.GetDefaultRenderDeviceName();
            _defaultDeviceNameSeen = name;
            if (_closingForExit || IsDisposed)
                return;
            // Uscita scelta a mano: resta quella, qualunque sia il predefinito di Windows.
            if (!string.IsNullOrWhiteSpace(_selectedAudioRendererName))
                return;
            if (_engine == null || IsPhotoMode || string.IsNullOrWhiteSpace(_currentPath) || string.IsNullOrWhiteSpace(name))
                return;
            // Durante la demo pre-film o il placeholder la riapertura perderebbe il film in attesa:
            // il film che parte subito dopo usera' comunque il nuovo dispositivo.
            if (_playingPreRoll || _preOpenPlaceholderGateActive)
                return;
            // mpv in PCM segue da solo il predefinito di Windows: lo si riapre solo se cambia
            // la possibilita' di passthrough (es. da cuffie a un ricevitore HDMI o viceversa).
            if (_engine is LibMpvPlaybackEngine &&
                LooksBitstreamCapableAudioOutput(previousName) == LooksBitstreamCapableAudioOutput(name) &&
                !IsBitstream())
                return;

            Dbg.Log($"[AUDIO] default render device changed: '{previousName ?? "?"}' -> '{name}', reopening on the new device.", Dbg.LogLevel.Info);
            _lblStatus.Text = Tx("Uscita audio: ", "Audio output: ") + name;
            ReopenSame();
        }

        private sealed class DefaultRenderDeviceListener : IMMNotificationClient
        {
            private readonly Action<string?> _changed;
            public DefaultRenderDeviceListener(Action<string?> changed) => _changed = changed;

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow != DataFlow.Render || role != Role.Multimedia) return;
                try { _changed(defaultDeviceId); } catch { }
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
            public void OnDeviceAdded(string pwstrDeviceId) { }
            public void OnDeviceRemoved(string deviceId) { }
            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }
    }
}
