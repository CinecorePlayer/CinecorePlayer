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
    private float? _ampSynced, _ampRemembered, _ampFilmLevel;
    private string? _ampText, _ampFilmKey;
    private long _ampReadAt;

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
            else if (_ampSession && !running) { _ampSession = false; RememberAmplifierLevelAtStop(); AmplifierFilmLeave(); }
        }).Start();
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

    private bool IsAmplifierTarget => _file != null && _video.IsRunning && !string.IsNullOrEmpty(_ampDevice?.AudioEndpointId)
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
        // Il cursore dell'HUD mostra il livello del ricevitore finche' comanda lui.
        if (!_ampGoal.HasValue) _hud.Volume = state.Level;
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

    private int _ampGesture;
    private float? _ampGoal;          // volume da raggiungere (dB interi), finche' il ricevitore non ci arriva
    private bool _ampMuteToggle;      // disattiva audio chiesto mentre un comando era in viaggio

    /// <summary>Livello 0..1 della barra in dB come il comando MV di Denon/Marantz (1/80 della barra = 1 dB).</summary>
    internal static string AmplifierDbText(float level) =>
        level <= 0 ? "MIN" : (level * 80 - 80).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " dB";

    /// <summary>
    /// Gesto sul volume (cursore, frecce, rotella, telefono, disattiva audio): va al ricevitore se comanda lui.
    /// La barra e' a scatti di 1 dB: dove la lasci diventa il volume da raggiungere e il cursore resta li' finche' il
    /// ricevitore non ci arriva. Tasti e telecomando: ±1 dB a pressione dal volume gia' chiesto (si sommano anche mentre
    /// un comando e' in viaggio). Mai oltre -20 dB; se il ricevitore e' gia' piu' alto (dal suo telecomando) non si sale.
    /// </summary>
    private bool TryAmplifierVolume(int step = 0, float? value = null, bool toggleMute = false)
    {
        if (!IsAmplifierActive || _ampDevice == null) return false;
        Interlocked.Increment(ref _ampGesture);
        if (toggleMute) _ampMuteToggle = true;
        else
        {
            float current = _ampSynced ?? 0;
            float asked = value ?? (_ampGoal ?? current) + Math.Sign(step) / 80f;
            float goal = MathF.Round(Math.Clamp(asked, 0f, 1f) * 80) / 80;
            float cap = Math.Max(current, AmplifierSafeCeiling);
            if (goal > current && goal > cap)
            {
                goal = MathF.Floor(cap * 80 + 0.001f) / 80;
                if (goal < current) goal = current;
                _osd.Show("Limite di sicurezza del player: oltre si alza dal ricevitore", 2600);
            }
            _ampGoal = goal;
            _hud.Volume = goal; _hud.InvalidateVisual();
        }
        if (!_ampBusy) _ = SendAmplifierAsync(_ampDevice);
        return true;
    }

    /// <summary>
    /// Porta il ricevitore al volume chiesto. Abbassare: un comando. Alzare: comandi uno dopo l'altro, ognuno al massimo
    /// +3 dB e sempre con la lettura prima nella stessa connessione (ReadThenSetAsync), 120 ms fra uno e l'altro. Si ferma
    /// quando arriva, al tetto di -20 dB, o se il ricevitore non segue per due volte di fila. Un gesto nuovo cambia la meta
    /// (anche a giro in corso).
    /// </summary>
    private async Task SendAmplifierAsync(AmplifierDevice device)
    {
        if (_ampBusy || _closing) return;
        _ampBusy = true;
        AmplifierStatus? state = null;
        bool limited = false, touched = false;
        try
        {
            using var client = new AmplifierControl(device);
            var token = _ampLifetime.Token;
            int stuck = 0, seen = -1;
            for (int round = 0; round < 200; round++)
            {
                if (_closing || device != _ampDevice || !IsAmplifierTarget) return;
                if (_ampMuteToggle)
                {
                    _ampMuteToggle = false;
                    var read = await client.ReadAsync(token);
                    state = await client.MuteAsync(!read.Muted, token);
                    touched = true;
                    continue;
                }
                if (_ampGoal is not float) break;
                int gesture = Volatile.Read(ref _ampGesture);
                if (gesture != seen) { seen = gesture; stuck = 0; }
                float before = 0;
                float? asked = null;
                bool capped = false;
                state = await client.ReadThenSetAsync(now =>
                {
                    before = now.Level;
                    if (_ampGoal is not float goal || Math.Abs(goal - now.Level) < 0.5f / 80) return null;
                    if (goal < now.Level) { asked = goal; return goal; }
                    float allowed = Math.Min(now.Level + AmplifierMaxRaise, Math.Max(now.Level, AmplifierSafeCeiling));
                    if (allowed <= now.Level + 0.0005f) { capped = true; return null; }
                    asked = Math.Min(goal, allowed);
                    return asked;
                }, token);
                touched = true;
                if (capped) { limited = true; _ampGoal = null; break; }
                if (asked is not float sent)
                {
                    // Arrivato (o meta tolta): finito, a meno che nel frattempo non sia arrivato un gesto nuovo.
                    if (Volatile.Read(ref _ampGesture) == gesture) { _ampGoal = null; break; }
                    continue;
                }
                bool followed = Math.Abs(state.Level - before) > 0.004f || Math.Abs(state.Level - sent) < 0.004f;
                stuck = followed ? 0 : stuck + 1;
                if (stuck >= 2)
                {
                    Console.Error.WriteLine("[volume] il ricevitore non segue: giro fermato");
                    if (Volatile.Read(ref _ampGesture) == gesture) { _ampGoal = null; break; }
                }
                if (state.Level >= AmplifierSafeCeiling - 0.001f && _ampGoal is float g && g > state.Level + 0.004f) { limited = true; _ampGoal = null; break; }
                await Task.Delay(120, token);
            }
            if (state == null && !_ampMuteToggle) state = await client.ReadAsync(token);
            if (state == null || _closing || device != _ampDevice || !IsAmplifierTarget) return;
            if (_ampFilmKey != null) { _ampFilmLevel = state.Level; if (touched) _ampFilmTouched = true; }
            ShowAmplifierState(state);
            RememberAmplifierLevel(state.Level);
            if (limited) _osd.Show("Limite di sicurezza del player: oltre si alza dal ricevitore", 2600);
            else if (touched) _osd.Show(device.Name + " · " + state.Volume + (state.Muted ? " · Muto" : ""), 1400);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _ampSynced = null; _ampText = null; _ampReadAt = 0; _ampGoal = null;
            if (!_closing) _osd.Show("Controllo IP non disponibile: " + ex.Message, 2600);
        }
        finally
        {
            _ampBusy = false;
            if (!_closing && device == _ampDevice && IsAmplifierActive && (_ampGoal.HasValue || _ampMuteToggle)) _ = SendAmplifierAsync(device);
        }
    }

    internal string AmplifierQa => $"dispositivo={_ampDevice?.Name ?? "-"} bersaglio={IsAmplifierTarget} attivo={IsAmplifierActive} livello={_ampSynced:0.000} testo={_ampText} ricordato={RememberedAmplifierLevel():0.000} hud={_hud.Volume:0.000} motore={_volume:0.00}";
}
