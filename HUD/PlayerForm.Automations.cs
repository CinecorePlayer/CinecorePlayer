#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    // Eventi del player verso i dispositivi di casa (vedi DeviceAutomations): avvio, pausa,
    // ripresa, fine e titoli di coda. Gli eventi seguono la "seduta", non il singolo file:
    // demo -> film o un brano dopo l'altro non generano una fine e un nuovo avvio.
    public sealed partial class PlayerForm
    {
        private bool _automationSessionActive;
        private bool _automationCreditsFired;
        private string? _automationCreditsPath;
        private double _automationCreditsStart = double.NaN;
        private long _automationCreditsCheckedTick;

        private AutomationContext CurrentAutomationContext()
        {
            string title = "";
            double position = 0, duration = 0;
            try { title = BuildBestDisplayTitleForPath(_playingPreRoll ? _pendingMainPathAfterPreRoll ?? _currentPath : _currentPath); } catch { }
            try { position = _engine != null ? GetTimelinePositionForHud() : 0; duration = GetTimelineDurationSeconds(); } catch { }
            return new AutomationContext(title, _currentMediaHasVideo || _playingPreRoll, position, duration);
        }

        private void FireAutomation(string eventName)
        {
            try { DeviceAutomations.Fire(eventName, CurrentAutomationContext()); }
            catch (Exception ex) { Dbg.Warn("[AUTOMATION] " + eventName + ": " + ex.Message); }
        }

        /// <summary>Un file e' pronto e sta suonando (o e' stato aperto in pausa).</summary>
        private void NotifyAutomationPlaybackOpened()
        {
            if (_engine == null || IsPhotoMode) return;
            _automationCreditsFired = false;
            if (_paused || _automationSessionActive) return;
            _automationSessionActive = true;
            FireAutomation("start");
        }

        private void NotifyAutomationPauseChanged(bool paused)
        {
            if (_engine == null || IsPhotoMode) return;
            if (paused)
            {
                if (_automationSessionActive) FireAutomation("pause");
            }
            else if (!_automationSessionActive)
            {
                // Aperto in pausa: la prima partenza e' l'avvio.
                _automationSessionActive = true;
                FireAutomation("start");
            }
            else
                FireAutomation("resume");
        }

        /// <summary>Fine vera della riproduzione (non il passaggio da un file al successivo).</summary>
        private void NotifyAutomationPlaybackStopped()
        {
            if (!_automationSessionActive) return;
            _automationSessionActive = false;
            _automationCreditsFired = false;
            FireAutomation("stop");
        }

        // Inizio dei titoli di coda: segmento rilevato (serie) oppure un capitolo che si chiama
        // "credits" / "titoli di coda" nell'ultima parte del film. Senza nessuno dei due
        // l'evento non parte: meglio niente che luci accese a meta' del finale.
        private static readonly Regex CreditsChapter = new(@"\b(end\s*)?credits?\b|titoli\s+di\s+coda|end\s*titles?|abspann|g[ée]n[ée]rique", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private double ResolveCreditsStart(double duration)
        {
            try
            {
                var outro = _currentIntroOutroMarkers?.Outro;
                if (outro != null && outro.EndSeconds > outro.StartSeconds + 1) return outro.StartSeconds;
                if (_info != null && duration > 0)
                    foreach (var (title, start) in _info.Chapters)
                        if (start > duration * 0.75 && CreditsChapter.IsMatch(title ?? "")) return start;
            }
            catch { }
            return double.NaN;
        }

        private void UpdateAutomationCredits(double position)
        {
            if (!_automationSessionActive || _engine == null || _paused || _playingPreRoll || !_currentMediaHasVideo) return;
            bool newFile = !string.Equals(_automationCreditsPath, _currentPath, StringComparison.OrdinalIgnoreCase);
            // Senza titoli di coda noti si riprova ogni pochi secondi (i segmenti rilevati possono
            // arrivare a film avviato), non a ogni aggiornamento della posizione.
            if (newFile || (double.IsNaN(_automationCreditsStart) && Environment.TickCount64 - _automationCreditsCheckedTick >= 5000))
            {
                _automationCreditsPath = _currentPath;
                _automationCreditsCheckedTick = Environment.TickCount64;
                _automationCreditsStart = ResolveCreditsStart(GetTimelineDurationSeconds());
            }
            if (double.IsNaN(_automationCreditsStart)) return;
            if (position < _automationCreditsStart - 30) _automationCreditsFired = false; // tornati indietro: puo' ripartire
            else if (position >= _automationCreditsStart && !_automationCreditsFired)
            {
                _automationCreditsFired = true;
                FireAutomation("credits");
            }
        }

        private void ShowAutomationsDialog()
        {
            try
            {
                using var sheetLayout = SheetPresenter.Layout(this);
                using var dialog = new AutomationsForm(UiEnglish, () =>
                {
                    var context = CurrentAutomationContext();
                    // "Prova" senza nulla in riproduzione: un contesto d'esempio.
                    return string.IsNullOrWhiteSpace(context.Title) ? new AutomationContext("Cinecore Player", true, 0, 0) : context;
                });
                SheetPresenter.ShowDialog(dialog, this);
            }
            catch (Exception ex) { Dbg.Warn("Automations dialog failed: " + ex.Message); }
        }
    }
}
