using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Il ricevitore collegato via rete segue il volume durante il film (HUD/PlayerForm.Amplifier.cs e la parte amplificatore
/// di PlayerForm.FilmVolume.cs di Windows). Vale quando il film suona sull'uscita collegata al ricevitore in
/// Impostazioni › Dispositivi. Regole di sicurezza (identiche a Windows, da non allentare senza chiedere):
/// - mai un livello inviato senza aver letto prima quello attuale;
/// - ogni comando alza al massimo di 3 dB; oltre -20 dB il player non alza affatto (abbassare si puo' sempre);
/// - a ogni avvio il ricevitore si porta all'ultimo volume usato con il player (o a -30 dB se non ce n'e' uno), solo
///   abbassando: non si alza mai da solo.
/// A differenza di Windows il volume del motore non viene portato al 100% quando si tocca il cursore: resta dov'era
/// (nessun salto di livello digitale).
/// </summary>
public sealed partial class MainWindow
{
    private const float AmplifierSafeCeiling = 0.75f;   // -20 dB
    private const float AmplifierFirstStart = 0.625f;   // -30 dB
    private const float AmplifierMaxRaise = 0.0375f;    // +3 dB per comando

    private AmplifierDevice? _ampDevice;
    private readonly CancellationTokenSource _ampLifetime = new();
    private bool _ampBusy, _ampSessionStarted, _ampRememberedLoaded, _ampSession, _ampFilmTouched;
    private float? _ampPending, _ampSynced, _ampRemembered, _ampFilmLevel;
    private string? _ampText, _ampFilmKey;
    private long _ampReadAt;
    // Gesto in corso: il cursore resta sul valore chiesto finche' i comandi arrivano (prima ogni risposta e la lettura
    // ogni 3 s lo riportavano all'ultimo livello letto: "torna indietro di continuo").
    private float? _ampWanted;
    private long _ampHoldUntil;
    // Volume scelto con la barra (a scatti di 1 dB): il cursore resta li' e il player ci arriva, al massimo +3 dB per
    // comando (in salita piu' comandi di fila), mai oltre il tetto. Prima il cursore tornava a +3 dB dal volume letto.
    private float? _ampGoal;
    private int _ampGoalStalls;
    private const float AmplifierDb = 1f / 80f;
    private const float AmplifierKeyStep = 0.0125f;     // 1 dB per pressione (tasti, rotella, telecomando)

    private static string AmplifierLevelPath => Path.Combine(LibraryStore.AppDataDir, "amplifier-level.txt");

    private void WireAmplifier()
    {
        _ampDevice = AmplifierControl.Load();
        // Ogni 3 s, a film in corso sull'uscita del ricevitore: lettura del volume (cambiato anche dal suo telecomando)
        // e fine della seduta (si ricorda il volume a cui si ascoltava).
        new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            bool running = _file != null && _video.IsRunning;
            if (running && IsAmplifierTarget) { _ampSession = true; RefreshAmplifier(); }
            else if (_ampSession && !running) { _ampSession = false; RememberAmplifierLevelAtStop(); AmplifierFilmLeave(); UnmuteAmplifierAfterFilm(); }
            // Musica sul ricevitore: solo lettura (il cursore della barra mostra il suo volume). Nessun cambio automatico.
            if (!running && IsAmplifierTarget) RefreshAmplifier();
        }).Start();
        _library.AmplifierState = () => IsMusicOnAmplifier && IsAmplifierActive && _ampSynced is float level
            ? ((double)(_ampWanted ?? level), _hud.IsMuted) : null;
        _library.AmplifierSetVolume = value => IsMusicOnAmplifier && TryAmplifierVolume(value: (float)value);
        _library.AmplifierStepVolume = step => IsMusicOnAmplifier && TryAmplifierVolume(step: step);
        _library.AmplifierToggleMute = () => IsMusicOnAmplifier && TryAmplifierVolume(toggleMute: true);
    }

    /// <summary>Il muto messo dal player sul ricevitore durante il film (non quello del suo telecomando).</summary>
    private bool _ampMutedByPlayer;

    /// <summary>
    /// Film chiuso con il ricevitore messo in muto dal player: si toglie. Prima restava muto e la musica, che non sa del
    /// ricevitore, mostrava l'audio attivo senza che si sentisse nulla.
    /// </summary>
    private void UnmuteAmplifierAfterFilm()
    {
        if (!_ampMutedByPlayer || _ampDevice is not { } device || _closing) return;
        _ampMutedByPlayer = false;
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new AmplifierControl(device);
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var state = await client.ReadAsync(limit.Token).ConfigureAwait(false);
                if (state.Muted) await client.MuteAsync(false, limit.Token).ConfigureAwait(false);
                Console.Error.WriteLine("[volume] ricevitore: muto del film tolto alla chiusura");
            }
            catch (Exception ex) { Console.Error.WriteLine("[volume] ricevitore: muto non tolto: " + ex.Message); }
        });
    }

    /// <summary>Impostazioni › Dispositivi cambiate: il volume segue subito il ricevitore nuovo (o torna al Mac).</summary>
    internal void ReloadAmplifier()
    {
        _ampDevice = AmplifierControl.Load();
        _ampSynced = null; _ampText = null; _ampReadAt = 0;
        if (!IsAmplifierTarget) { _hud.Volume = _volume; _hud.InvalidateVisual(); }
        else RefreshAmplifier(force: true);
    }

    /// <summary>L'uscita su cui suona il film adesso (quella scelta, o il predefinito del Mac).</summary>
    private string CurrentOutputUid()
    {
        if (!string.IsNullOrEmpty(_activeAudioOutput)) return _activeAudioOutput;
        try { return MacAudioDevices.Outputs().FirstOrDefault(d => d.IsDefault)?.Uid ?? ""; } catch { return ""; }
    }

    /// <summary>Musica in corso (barra visibile) sull'uscita del ricevitore, senza film aperto.</summary>
    private bool IsMusicOnAmplifier => !(_file != null && _video.IsRunning) && _library.MusicActive;

    private bool IsAmplifierTarget => (_file != null && _video.IsRunning || IsMusicOnAmplifier) && !string.IsNullOrEmpty(_ampDevice?.AudioEndpointId)
        && string.Equals(_ampDevice.AudioEndpointId, CurrentOutputUid(), StringComparison.Ordinal);

    private bool IsAmplifierActive => IsAmplifierTarget && _ampSynced.HasValue && _ampText != null && _ampReadAt != 0 && Environment.TickCount64 - _ampReadAt < 10000;

    private float? RememberedAmplifierLevel()
    {
        if (_ampRememberedLoaded) return _ampRemembered;
        _ampRememberedLoaded = true;
        try
        {
            if (File.Exists(AmplifierLevelPath) &&
                float.TryParse(File.ReadAllText(AmplifierLevelPath).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float saved) &&
                saved >= 0f && saved <= 1f)
                _ampRemembered = saved;
        }
        catch { }
        return _ampRemembered;
    }

    private void RememberAmplifierLevel(float level)
    {
        // Solo dopo l'avvio sicuro: il volume trovato all'accensione puo' essere quello lasciato da un'altra sorgente.
        if (!_ampSessionStarted) return;
        level = Math.Clamp(level, 0f, AmplifierSafeCeiling);
        if (RememberedAmplifierLevel() is float known && Math.Abs(known - level) < 0.006f) return;
        _ampRemembered = level;
        try { Directory.CreateDirectory(Path.GetDirectoryName(AmplifierLevelPath)!); File.WriteAllText(AmplifierLevelPath, level.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)); }
        catch { }
    }

    private void RememberAmplifierLevelAtStop()
    {
        var device = _ampDevice;
        if (device == null || string.IsNullOrEmpty(device.AudioEndpointId) || !_ampSessionStarted || _closing) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new AmplifierControl(device);
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var state = await client.ReadAsync(limit.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => RememberAmplifierLevel(state.Level));
            }
            catch { }
        });
    }

    // ------------------------------------------------------------------ volume di questo film sul ricevitore
    private void AmplifierFilmLeave()
    {
        string? key = _ampFilmKey;
        _ampFilmKey = null;
        if (key != null && MacSettings.RememberFilmVolume && _ampFilmTouched && _ampFilmLevel is float level)
            FilmVolumeStore.Set(key, amplifier: true, level);
        _ampFilmTouched = false; _ampFilmLevel = null;
    }

    /// <summary>
    /// Film pronto con il ricevitore collegato: legge il volume e, se e' piu' alto dell'ultimo usato con il player (o di
    /// quello ricordato per questo film), lo abbassa. Non alza mai. Senza un volume ricordato parte al massimo da -30 dB.
    /// </summary>
    private void BeginAmplifierSafeStart()
    {
        AmplifierFilmLeave();
        _ampDevice = AmplifierControl.Load();
        if (!IsAmplifierTarget || _ampDevice == null) return;
        _ampSession = true;
        float? film = null;
        if (MacSettings.RememberFilmVolume && _file != null && !IsStream(_file))
        {
            _ampFilmKey = _file;
            var (_, amplifier) = FilmVolumeStore.Get(_file);
            film = amplifier;
        }
        float start = RememberedAmplifierLevel() ?? AmplifierFirstStart;
        float target = film.HasValue ? Math.Min(film.Value, start) : start;
        _ = LowerAmplifierAsync(_ampDevice, _file, target, film.HasValue && film.Value <= start);
    }

    private async Task LowerAmplifierAsync(AmplifierDevice device, string? file, float remembered, bool fromFilm)
    {
        for (int wait = 0; _ampBusy && wait < 40; wait++) await Task.Delay(100);
        if (_ampBusy || _closing) return;
        _ampBusy = true;
        try
        {
            using var client = new AmplifierControl(device);
            var token = _ampLifetime.Token;
            var state = await client.ReadAsync(token);
            bool current() => !_closing && device == _ampDevice && IsAmplifierTarget && _file == file;
            if (!current()) return;
            ShowAmplifierState(state);
            _ampFilmLevel = state.Level;
            if (_ampFilmTouched) { _ampSessionStarted = true; return; } // l'utente ha gia' messo mano al volume: decide lui
            if (remembered < state.Level - 0.004f)
            {
                string before = state.Volume;
                state = await client.SetVolumeAsync(remembered, token);
                _ampSessionStarted = true;
                if (!current()) return;
                ShowAmplifierState(state);
                _ampFilmLevel = state.Level;
                _osd.Show($"{device.Name}: {before} → {state.Volume}" + (fromFilm ? " (volume di questo film)" : ""), 2600);
                Console.Error.WriteLine($"[volume] ricevitore abbassato da {before} a {state.Volume} ({(fromFilm ? "film" : "ultimo usato")})");
            }
            _ampSessionStarted = true;
            if (fromFilm && remembered > state.Level + 0.004f)
                _osd.Show("Per questo film avevi un volume più alto: non lo alzo da solo", 2600);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[volume] ricevitore non raggiungibile: " + ex.Message);
            _ampSynced = null; _ampText = null; _ampReadAt = 0;
        }
        finally { _ampBusy = false; }
    }

    private void ShowAmplifierState(AmplifierStatus state)
    {
        _ampSynced = state.Level;
        _ampText = state.Volume;
        _ampReadAt = Environment.TickCount64;
        // Il cursore dell'HUD mostra il livello del ricevitore finche' comanda lui (non durante un gesto).
        bool holding = _ampGoal.HasValue || _ampPending.HasValue || Environment.TickCount64 < _ampHoldUntil;
        if (!holding) { _ampWanted = null; _hud.Volume = state.Level; }
        _hud.IsMuted = state.Muted;
        _hud.InvalidateVisual();
    }

    /// <summary>Legge il volume del ricevitore senza mostrare nulla (non piu' di una volta ogni 3 s).</summary>
    private void RefreshAmplifier(bool force = false)
    {
        if (_ampBusy || !IsAmplifierTarget || _ampDevice == null) return;
        if (!force && Environment.TickCount64 - _ampReadAt < 2500) return;
        _ = SendAmplifierAsync(_ampDevice);
    }

    /// <summary>Gesto sul volume (cursore, frecce, telefono, disattiva audio): va al ricevitore se comanda lui.</summary>
    private bool TryAmplifierVolume(int step = 0, float? value = null, bool toggleMute = false)
    {
        if (!IsAmplifierActive || _ampDevice == null) return false;
        if (toggleMute)
        {
            if (!_ampBusy) _ = SendAmplifierAsync(_ampDevice, toggleMute: true);
            return true;
        }
        // Le pressioni (tasti, telecomando) partono dal volume gia' chiesto: si sommano anche mentre un comando e' in viaggio.
        if (step != 0) value = (_ampGoal ?? _ampWanted ?? _ampSynced ?? _hud.Volume) + Math.Sign(step) * AmplifierDb;
        if (!value.HasValue) return true;
        // Barra divisa in dB interi; tetto di sicurezza (se il ricevitore e' gia' piu' alto dal suo telecomando, non si sale).
        float top = Math.Max(AmplifierSafeCeiling, _ampSynced ?? 0f);
        float goal = (float)(Math.Round(Math.Clamp(value.Value, 0f, 1f) / AmplifierDb) * AmplifierDb);
        if (goal > top + 0.0005f)
        {
            goal = top;
            _osd.Show("Limite di sicurezza del player: " + AmplifierDbText(top) + ". Oltre si alza dal ricevitore", 2200);
        }
        _ampGoal = goal; _ampGoalStalls = 0;
        _ampWanted = goal; _hud.Volume = goal; _hud.InvalidateVisual();
        _ampHoldUntil = Environment.TickCount64 + 1500;
        if (!_ampBusy) _ = SendAmplifierAsync(_ampDevice);
        return true;
    }

    private static string AmplifierDbText(float level) =>
        (Math.Round(level * 80) - 80).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " dB";

    /// <summary>
    /// Un giro con il ricevitore, in una connessione: lettura e, se c'e' un volume scelto, il passo verso di lui
    /// (giu' subito tutto, su al massimo +3 dB). Se manca ancora strada, il giro dopo parte da solo.
    /// </summary>
    private async Task SendAmplifierAsync(AmplifierDevice device, bool toggleMute = false)
    {
        if (_ampBusy || _closing) return;
        _ampBusy = true;
        bool again = false;
        try
        {
            using var client = new AmplifierControl(device);
            var token = _ampLifetime.Token;
            float? goal = toggleMute ? null : _ampGoal;
            float before = float.NaN;
            var state = goal is float g
                ? await client.ReadThenSetAsync(now =>
                {
                    before = now.Level;
                    float v = g;
                    if (v > now.Level) v = Math.Min(v, Math.Min(now.Level + AmplifierMaxRaise, Math.Max(now.Level, AmplifierSafeCeiling)));
                    return Math.Abs(v - now.Level) < 0.003f ? null : v;
                }, token)
                : await client.ReadAsync(token);
            if (toggleMute && IsAmplifierTarget && device == _ampDevice)
            {
                state = await client.MuteAsync(!state.Muted, token);
                _ampMutedByPlayer = state.Muted;
            }
            if (_closing || device != _ampDevice || !IsAmplifierTarget) { _ampGoal = null; return; }
            bool userChange = goal.HasValue;
            if (goal is float target)
            {
                if (_ampGoal is float latest && Math.Abs(latest - target) > 0.0005f) again = true; // gesto nuovo nel frattempo
                else if (Math.Abs(state.Level - target) < 0.004f) _ampGoal = null;                  // arrivato
                else if (state.Level > target) again = true;                                         // giu': ancora un colpo
                else if (state.Level >= Math.Max(AmplifierSafeCeiling, before) - 0.001f) _ampGoal = null; // tetto
                else if (state.Level <= before + 0.002f && ++_ampGoalStalls >= 2) _ampGoal = null;  // il ricevitore non segue
                else again = true;
            }
            if (_ampGoal == null) _ampHoldUntil = 0;
            if (_ampFilmKey != null) { _ampFilmLevel = state.Level; if (userChange) _ampFilmTouched = true; }
            ShowAmplifierState(state);
            if (_ampGoal is float pending) { _ampWanted = pending; _hud.Volume = pending; _hud.InvalidateVisual(); }
            RememberAmplifierLevel(state.Level);
            if (userChange || toggleMute)
                _osd.Show(device.Name + "   " + state.Volume + (state.Muted ? "   Muto" : ""), 1400);
        }
        catch (OperationCanceledException) { _ampGoal = null; }
        catch (Exception ex)
        {
            _ampSynced = null; _ampText = null; _ampReadAt = 0; _ampPending = null; _ampGoal = null;
            if (!_closing) _osd.Show("Controllo IP non disponibile: " + ex.Message, 2600);
        }
        finally
        {
            _ampBusy = false;
            // In salita i comandi vanno uno dopo l'altro, con un respiro: il ricevitore applica e il volume cresce a scatti di 3 dB.
            if (again && !_closing && device == _ampDevice && IsAmplifierActive)
                _ = Task.Delay(120).ContinueWith(_ => Dispatcher.UIThread.Post(() => { if (_ampGoal != null && !_ampBusy) _ = SendAmplifierAsync(device); }));
        }
    }

    internal string AmplifierQa => $"dispositivo={_ampDevice?.Name ?? "-"} bersaglio={IsAmplifierTarget} attivo={IsAmplifierActive} livello={_ampSynced:0.000} testo={_ampText} ricordato={RememberedAmplifierLevel():0.000} hud={_hud.Volume:0.000} motore={_volume:0.00}";
}
