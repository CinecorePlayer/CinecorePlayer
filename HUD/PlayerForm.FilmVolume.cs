#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.IO;
using System.Threading.Tasks;

namespace CinecorePlayer2025
{
    // Volume per film: se durante un film l'utente cambia il volume, quel livello viene
    // ricordato e ripreso alla prossima apertura dello stesso film.
    // Con l'amplificatore di rete il livello viene solo abbassato: il player non alza mai
    // da solo il volume di un amplificatore.
    public sealed partial class PlayerForm
    {
        private bool _filmVolumeMemoryEnabled = true;
        private string? _filmVolumeKey;          // film in corso (percorso locale), null se non e' un film
        private bool _filmVolumeOnAmplifier;     // il volume di questo film passa dall'amplificatore
        private float _filmVolumeAtOpen = 1f;    // livello del player applicato all'apertura
        private bool _filmAmplifierTouched;      // l'utente ha cambiato il volume dell'amplificatore durante il film
        private float? _filmAmplifierLevel;      // ultimo livello letto dall'amplificatore

        /// <summary>Chiamato a ogni apertura: restituisce il volume del player da applicare.</summary>
        private float FilmVolumeOnOpen(string? path, bool hasVideo)
        {
            FilmVolumeLeave();
            if (!_filmVolumeMemoryEnabled || !hasVideo || _playingPreRoll || _hud == null) return 1f;
            string local = NormalizeMediaPathForDisplay(path ?? string.Empty);
            if (string.IsNullOrWhiteSpace(local) || !File.Exists(local)) return 1f;

            var (player, amplifier) = FilmVolumeStore.Get(local);
            _filmVolumeKey = local;
            _filmAmplifierTouched = false;
            _filmAmplifierLevel = null;
            _filmVolumeOnAmplifier = IsNetworkVolumeActive;
            if (_filmVolumeOnAmplifier)
            {
                _pendingFilmAmplifierLevel = amplifier;
                return 1f;
            }
            if (IsBitstream() && _audioOutPref != AudioOutPref.ForcePcm)
            {
                _filmVolumeKey = null; // passthrough senza amplificatore collegato: il player non controlla il volume
                return 1f;
            }

            float level = player ?? 1f;
            _filmVolumeAtOpen = level;
            if (_hud.IsMuted) return level;
            try { _hud.SetExternalVolume(level); } catch { }
            if (player.HasValue)
            {
                _lblStatus.Text = Tx($"Volume di questo film: {level * 100:0}%", $"Volume for this film: {level * 100:0}%");
                Dbg.Log($"[VOLUME] restored {level:0.00} for '{Path.GetFileName(local)}'", Dbg.LogLevel.Info);
            }
            return level;
        }

        /// <summary>Il film in corso sta per chiudersi o cambiare: se il volume e' stato toccato, lo ricorda.</summary>
        private void FilmVolumeLeave()
        {
            string? key = _filmVolumeKey;
            _filmVolumeKey = null;
            if (key == null || !_filmVolumeMemoryEnabled || _hud == null) return;
            try
            {
                if (_filmVolumeOnAmplifier)
                {
                    if (_filmAmplifierTouched && _filmAmplifierLevel is float amplifier)
                        FilmVolumeStore.Set(key, amplifier: true, amplifier);
                    return;
                }
                if (_hud.IsMuted) return;
                float level = Math.Clamp(_hud.GetVolume(), 0f, 1f);
                if (Math.Abs(level - _filmVolumeAtOpen) < 0.01f) return;
                // Riportato al massimo: non c'e' piu' niente da ricordare.
                FilmVolumeStore.Set(key, amplifier: false, level > 0.99f ? null : level);
            }
            catch (Exception ex) { Dbg.Warn("[VOLUME] " + ex.Message); }
        }

        /// <summary>Stato letto dall'amplificatore dopo un comando; <paramref name="userChange"/> se il comando cambiava il volume.</summary>
        private void NoteAmplifierVolume(AmplifierStatus state, bool userChange)
        {
            if (_filmVolumeKey == null || !_filmVolumeOnAmplifier) return;
            _filmAmplifierLevel = state.Level;
            if (userChange) _filmAmplifierTouched = true;
        }

        private float? _pendingFilmAmplifierLevel;

        /// <summary>
        /// A ogni apertura con l'amplificatore collegato: legge il volume e, se e' piu' alto
        /// dell'ultimo usato con il player (o di quello ricordato per questo film), lo abbassa.
        /// Non alza mai. Senza un volume ricordato parte al massimo da -30 dB.
        /// </summary>
        private void BeginAmplifierSafeStart()
        {
            float? film = _pendingFilmAmplifierLevel;
            _pendingFilmAmplifierLevel = null;
            if (!IsNetworkVolumeActive || _networkVolumeDevice == null) return;
            float start = RememberedAmplifierLevel() ?? AmplifierFirstStart;
            _ = LowerAmplifierToFilmVolumeAsync(_networkVolumeDevice, _filmVolumeKey, film.HasValue ? Math.Min(film.Value, start) : start, film.HasValue && film.Value <= start);
        }

        private async Task LowerAmplifierToFilmVolumeAsync(AmplifierDevice device, string? key, float remembered, bool fromFilm)
        {
            if (_closingForExit) return;
            // Un comando di volume in corso non deve far saltare l'avvio sicuro: si aspetta il suo turno.
            for (int wait = 0; _networkVolumeBusy && wait < 40; wait++) await Task.Delay(100);
            if (_networkVolumeBusy || _closingForExit) return;
            _networkVolumeBusy = true;
            try
            {
                using var client = new AmplifierControl(device);
                var token = _networkVolumeLifetime.Token;
                var state = await client.ReadAsync(token);
                bool current() => !IsDisposed && !_closingForExit && device == _networkVolumeDevice && IsNetworkVolumeActive &&
                    string.Equals(_filmVolumeKey ?? string.Empty, key ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                if (!current()) return;
                _filmAmplifierLevel = state.Level;
                void show()
                {
                    _networkVolumeReadAt = Environment.TickCount64;
                    _networkVolumeText = state.Volume;
                    _hud.SetMuted(state.Muted);
                    _hud.SetExternalVolume(state.Level);
                    _networkLevelSynced = state.Level; // da qui partono i gesti sul cursore
                }
                show();
                if (_filmAmplifierTouched) { _amplifierSessionStarted = true; return; } // l'utente ha gia' messo mano al volume: decide lui
                if (remembered < state.Level - 0.004f)
                {
                    string before = state.Volume;
                    state = await client.SetVolumeAsync(remembered, token);
                    _amplifierSessionStarted = true;
                    if (!current()) return;
                    _filmAmplifierLevel = state.Level;
                    show();
                    _lblStatus.Text = (fromFilm ? Tx("Volume di questo film: ", "Volume for this film: ") : Tx("Amplificatore riportato all'ultimo volume usato: ", "Receiver back to the last volume used: ")) + state.Volume;
                    ShowRemoteOsd(null, null, 2600, Tx($"Amplificatore: {before} → {state.Volume}", $"Receiver: {before} → {state.Volume}"));
                    Dbg.Log($"[VOLUME] amplifier lowered from {before} to {state.Volume} ({(fromFilm ? "film" : "last used")})", Dbg.LogLevel.Info);
                }
                _amplifierSessionStarted = true;
                if (fromFilm && remembered > state.Level + 0.004f)
                {
                    _lblStatus.Text = Tx("Per questo film avevi un volume più alto: non lo alzo da solo", "You had a higher volume for this film: it is not raised automatically");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Dbg.Warn("[VOLUME] amplifier: " + ex.Message); }
            finally { _networkVolumeBusy = false; }
        }

        private void ToggleFilmVolumeMemory()
        {
            if (_filmVolumeMemoryEnabled) FilmVolumeLeave();
            _filmVolumeMemoryEnabled = !_filmVolumeMemoryEnabled;
            try { SaveExtrasConfig(); } catch { }
            _lblStatus.Text = _filmVolumeMemoryEnabled
                ? Tx("Volume per film: attivo dal prossimo film", "Per-film volume: on from the next film")
                : Tx("Volume per film: disattivato", "Per-film volume: off");
        }
    }
}
