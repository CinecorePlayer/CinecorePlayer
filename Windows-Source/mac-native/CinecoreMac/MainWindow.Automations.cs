using System;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CinecorePlayer2025.Utilities;

namespace CinecoreMac;

/// <summary>
/// Traduzione di HUD/PlayerForm.Automations.cs: gli eventi del player verso i dispositivi di casa (DeviceAutomations):
/// avvio, pausa, ripresa, fine e titoli di coda. Seguono la "seduta", non il singolo file: un brano dopo l'altro non
/// genera una fine e un nuovo avvio. Si guarda lo stato ogni 400 ms (come le luci WLED), senza toccare il motore.
/// </summary>
public sealed partial class MainWindow
{
    private bool _automationSession, _automationPlaying, _automationCreditsFired;
    private string? _automationCreditsPath;
    private double _automationCreditsStart = double.NaN;
    private long _automationCreditsCheckedTick;
    private static readonly Regex CreditsChapter = new(@"\b(end\s*)?credits?\b|titoli\s+di\s+coda|end\s*titles?|abspann|g[ée]n[ée]rique", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void WireAutomations()
    {
        new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => UpdateAutomations()).Start();
        _automations.Closed += () => { if (_settings.IsOpen) _settings.Focus(); else if (_library.IsVisible) _library.Focus(); else _hud.Focus(); };
        _automations.MenuRequested += (entries, at) => ShowTextMenu(_automations, entries, at);
    }

    /// <summary>Titolo, video o musica e posizione di quello che suona adesso.</summary>
    private AutomationContext CurrentAutomationContext()
    {
        if (_file != null && _video.IsRunning)
            return new AutomationContext(_hud.Title, true, _lastKnownPos, _hud.Duration);
        if (_library.RemoteMusicState is { } music)
            return new AutomationContext(string.IsNullOrWhiteSpace(music.Item.ArtistName) ? music.Item.Title : music.Item.ArtistName + " – " + music.Item.Title, false, music.Position, music.Duration);
        return new AutomationContext("Cinecore Player", true, 0, 0);
    }

    private void FireAutomation(string eventName)
    {
        try { DeviceAutomations.Fire(eventName, CurrentAutomationContext()); }
        catch (Exception ex) { Console.Error.WriteLine("[automazioni] " + eventName + ": " + ex.Message); }
    }

    private void UpdateAutomations()
    {
        if (_closing) return;
        bool video = _file != null && _video.IsRunning && _gatePending == null;
        var music = video ? null : _library.RemoteMusicState;
        bool active = video || music != null;
        bool playing = video ? _hud.IsPlaying : music is { Paused: false };
        if (!active)
        {
            if (_automationSession) { _automationSession = false; _automationCreditsFired = false; FireAutomation("stop"); }
            return;
        }
        if (!_automationSession)
        {
            // Aperto in pausa: la prima partenza e' l'avvio.
            if (!playing) return;
            _automationSession = true; _automationPlaying = true; _automationCreditsFired = false;
            FireAutomation("start");
            return;
        }
        if (playing != _automationPlaying)
        {
            _automationPlaying = playing;
            FireAutomation(playing ? "resume" : "pause");
        }
        if (video && playing) UpdateAutomationCredits(_lastKnownPos);
    }

    /// <summary>
    /// Inizio dei titoli di coda: segmento rilevato (serie) oppure un capitolo che si chiama "credits" / "titoli di coda"
    /// nell'ultima parte del film. Senza nessuno dei due l'evento non parte: meglio niente che luci accese a meta' del finale.
    /// </summary>
    private void UpdateAutomationCredits(double position)
    {
        bool newFile = !string.Equals(_automationCreditsPath, _file, StringComparison.OrdinalIgnoreCase);
        if (newFile || (double.IsNaN(_automationCreditsStart) && Environment.TickCount64 - _automationCreditsCheckedTick >= 5000))
        {
            _automationCreditsPath = _file;
            _automationCreditsCheckedTick = Environment.TickCount64;
            _automationCreditsStart = ResolveCreditsStart(_hud.Duration);
            if (newFile) _automationCreditsFired = false;
        }
        if (double.IsNaN(_automationCreditsStart)) return;
        if (position < _automationCreditsStart - 30) _automationCreditsFired = false; // tornati indietro: puo' ripartire
        else if (position >= _automationCreditsStart && !_automationCreditsFired)
        {
            _automationCreditsFired = true;
            FireAutomation("credits");
        }
    }

    private double ResolveCreditsStart(double duration)
    {
        try
        {
            var outro = _introOutro?.Outro;
            if (outro != null && outro.EndSeconds > outro.StartSeconds + 1) return outro.StartSeconds;
            if (_probe != null && duration > 0)
                foreach (var (start, title) in _probe.Chapters)
                    if (start > duration * 0.75 && CreditsChapter.IsMatch(title ?? "")) return start;
        }
        catch { }
        return double.NaN;
    }
}
