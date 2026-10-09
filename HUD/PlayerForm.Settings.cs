#nullable enable
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    public sealed partial class PlayerForm
    {
        private void ShowSettingsHudPage(string? initialTab = null)
        {
            try
            {
                if (_settingsHudPage == null || _settingsHudPage.IsDisposed)
                    return;

                SyncSettingsHudState();
                if (string.Equals(initialTab, "MPV", StringComparison.OrdinalIgnoreCase))
                    _settingsHudPage.OpenMpvSettings();
                else if (string.Equals(initialTab, "Subtitles", StringComparison.OrdinalIgnoreCase))
                    _settingsHudPage.OpenSubtitlesSettings();
                _settingsHudPage.Visible = true;
                _settingsHudPage.BringToFront();
                RefreshSpotlightServerChoices();
                _hud.Visible = false;
                _infoOverlay.Visible = false;
                try { _remoteOsd.Visible = false; } catch { }
                try { HideExternalPlaybackOverlayHosts(); } catch { }
                try { _settingsHudPage.BringToFront(); } catch { }
                try { ActiveControl = _settingsHudPage; } catch { }
                try { _settingsHudPage.Focus(); } catch { }
                ResetLibraryRemoteActivation(clearFocusRing: true);
                RemoteAttachRoot(_settingsHudPage, forceReset: true);
                EnsureActive();
            }
            catch { }
        }

        private void HideSettingsHudPage()
        {
            try
            {
                if (_settingsHudPage == null)
                    return;
                bool wasVisible = _settingsHudPage.Visible;
                if (!wasVisible)
                    return;
                _settingsHudPage.Visible = false;
                _settingsHudPage.SendToBack();
                try { Focus(); } catch { }
                if (_engine != null && !_currentMediaHasVideo)
                {
                    try
                    {
                        EnsureAudioOnlyHudPinned();
                        BringOverlaysToFront();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void SyncSettingsHudState()
        {
            try
            {
                if (_settingsHudPage == null || _settingsHudPage.IsDisposed)
                    return;

                string renderer = _manualRendererChoice?.ToString() ?? "Auto";
                bool preferBitstream = _preferBitstreamUi && _audioOutPref != AudioOutPref.ForcePcm;
                _settingsHudPage.SetThemeColors(_uiAccentColor, _uiAccentSoftColor, _uiSelectionColor, _uiBorderAccentColor, _uiPanelColor, _uiCardColor, _uiNavColor);
                _settingsHudPage.ThemeMode = _uiThemeMode;
                _settingsHudPage.SetState(
                    preferBitstream,
                    _uiLanguage,
                    renderer,
                    _mpvRuntimeChoice == MpvRuntimeChoice.X64V3 && _mpvRuntimeV3Manual,
                    _spotlightAtStartup,
                    _spotlightAtWindowsStartup,
                    _spotlightSource,
                    _spotlightServerKey,
                    _cinematicLibraryPage?.GetSpotlightServerChoices() ?? Array.Empty<(string Key, string Name)>(),
                    BuildIntroOutroSettingsStatus(),
                    "TMDb: " + LocalizedTmdbStatus(),
                    BuildSettingsMpvValueSnapshot(),
                    BuildSettingsMpvToggleSnapshot());
            }
            catch { }
        }

        private string BuildIntroOutroSettingsStatus()
        {
            try
            {
                IntroOutroScanSnapshot status = IntroOutroScanService.GetStatusSnapshot();
                if (status.TotalSeasons <= 0)
                    return Tx("0% - nessuna stagione locale rilevata", "0% - no local seasons found");

                string failure = status.FailedSeasons > 0
                    ? Tx($" ({status.FailedSeasons} con errore)", $" ({status.FailedSeasons} failed)")
                    : string.Empty;
                if (status.RunningSeasons > 0 && !string.IsNullOrWhiteSpace(status.CurrentSeason))
                    return Tx($"{status.PercentComplete:0}% - scansione: {status.CurrentSeason}{failure}", $"{status.PercentComplete:0}% - scanning: {status.CurrentSeason}{failure}");
                if (status.QueuedSeasons > 0)
                    return Tx($"{status.PercentComplete:0}% - {status.CompletedSeasons}/{status.TotalSeasons} stagioni, {status.QueuedSeasons} in coda{failure}", $"{status.PercentComplete:0}% - {status.CompletedSeasons}/{status.TotalSeasons} seasons, {status.QueuedSeasons} queued{failure}");
                return Tx($"{status.PercentComplete:0}% - {status.CompletedSeasons}/{status.TotalSeasons} stagioni completate{failure}", $"{status.PercentComplete:0}% - {status.CompletedSeasons}/{status.TotalSeasons} seasons complete{failure}");
            }
            catch { return "-"; }
        }

        private string LocalizedTmdbStatus()
        {
            string value = MovieMetadataService.GetTmdbStatusText();
            if (!string.Equals(_uiLanguage, "en", StringComparison.OrdinalIgnoreCase))
                return value;
            if (value.IndexOf("personale", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Active (personal key)";
            if (value.IndexOf("integrata", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Active (built-in key)";
            if (value.IndexOf("Non configurato", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Not configured";
            return value;
        }

        private bool _uiSpanish;

        private void ApplyUiLanguage(string? language, bool save)
        {
            _uiSpanish = string.Equals(language, "es", StringComparison.OrdinalIgnoreCase);
            AppLanguage.Spanish = _uiSpanish;
            _uiLanguage = _uiSpanish || string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
            AppLanguage.Current = _uiLanguage;
            try { MovieMetadataService.SetPreferredMetadataLanguage(_uiLanguage); } catch { }
            try { _cinematicLibraryPage?.SetLanguage(_uiLanguage); } catch { }
            try { _netflixModePage?.SetLanguage(_uiLanguage); } catch { }
            try { _settingsHudPage?.Invalidate(); } catch { }
            try { _infoOverlay?.SetLanguage(_uiLanguage); } catch { }
            try { _hud?.SetLanguage(_uiLanguage); } catch { }
            try { _audioMeters?.SetLanguage(_uiLanguage); } catch { }
            try { _remoteOsd?.Invalidate(); } catch { }
            try { _videoLoading?.SetMessage(Tx("Caricamento…", "Loading…")); } catch { }
            try { if (_audioOnlyBanner != null) _audioOnlyBanner.Caption = Tx("Solo audio", "Audio only"); } catch { }
            try { if (_pausePlaceholder != null) _pausePlaceholder.Caption = Tx("PAUSA", "PAUSED"); } catch { }

            // Il menu contiene molte voci create dinamicamente: ricostruirlo e' il modo
            // affidabile per applicare la lingua anche ai sottomenu non ancora aperti.
            try { RebuildContextMenu(); } catch { }
            if (save)
            {
                try { SaveExtrasConfig(); } catch { }
                try { _lblStatus.Text = Tx("Lingua interfaccia: Italiano", "Interface language: English"); } catch { }
            }
            SyncSettingsHudState();
        }

        private void ShowThemeColorDialog(string key)
        {
            key = NormalizeThemeColorKey(key);
            Color current = GetUiThemeColor(key);
            try
            {
                using var sheetLayout = SheetPresenter.Layout(this);
                using var dialog = new AccentColorPickerForm(current, Tx($"Colore {ThemeColorLabel(key)}", $"{ThemeColorLabelEnglish(key)} color"), UiEnglish);
                if (SheetPresenter.ShowDialog(dialog, this) == DialogResult.OK)
                    ApplyUiThemeColor(key, dialog.SelectedColor, save: true);
            }
            catch (Exception ex) { Dbg.Warn("Theme color dialog failed: " + ex.Message); }
        }

        private void ApplyUiAccentColor(Color color, bool save)
        {
            try
            {
                Theme.SetAccent(color);
                _uiAccentColor = Theme.Accent;
                _uiAccentSoftColor = Theme.AccentSoft;
                _uiSelectionColor = Theme.Selection;
                _uiBorderAccentColor = Theme.BorderAccent;
                ApplyUiThemePalette(save, Tx("Colore interfaccia aggiornato", "Interface color updated"));
            }
            catch { }
        }

        private void ApplyUiThemeColor(string key, Color color, bool save)
        {
            try
            {
                color = NormalizeUiColor(color);
                switch (NormalizeThemeColorKey(key))
                {
                    case "soft":
                        _uiAccentSoftColor = color;
                        break;
                    case "selection":
                        _uiSelectionColor = color;
                        break;
                    case "border":
                        _uiBorderAccentColor = color;
                        break;
                    case "panel":
                        _uiPanelColor = color;
                        break;
                    case "card":
                        _uiCardColor = color;
                        break;
                    case "nav":
                        _uiNavColor = color;
                        break;
                    default:
                        _uiAccentColor = color;
                        break;
                }

                ApplyUiThemePalette(save, Tx($"Colore {ThemeColorLabel(key)} aggiornato", $"{ThemeColorLabelEnglish(key)} color updated"));
            }
            catch { }
        }

        private void ApplyUiThemePalette(bool save, string statusText)
        {
            try
            {
                _uiAccentColor = NormalizeUiColor(_uiAccentColor);
                _uiAccentSoftColor = NormalizeUiColor(_uiAccentSoftColor);
                _uiSelectionColor = NormalizeUiColor(_uiSelectionColor);
                _uiBorderAccentColor = NormalizeUiColor(_uiBorderAccentColor);
                _uiPanelColor = NormalizeUiColor(_uiPanelColor);
                _uiCardColor = NormalizeUiColor(_uiCardColor);
                _uiNavColor = NormalizeUiColor(_uiNavColor);
                Theme.SetFullPalette(_uiAccentColor, _uiAccentSoftColor, _uiSelectionColor, _uiBorderAccentColor, _uiPanelColor, _uiCardColor, _uiNavColor);

                try { BackColor = Theme.Panel; } catch { }
                try { _stack.BackColor = Theme.Panel; } catch { }
                try { _settingsHudPage?.SetThemeColors(_uiAccentColor, _uiAccentSoftColor, _uiSelectionColor, _uiBorderAccentColor, _uiPanelColor, _uiCardColor, _uiNavColor); } catch { }
                try { _cinematicLibraryPage?.ApplyTheme(); } catch { }
                // Dopo la pagina: il vetro della barra musica si rifa' subito sul nuovo tema.
                try { _musicTransport?.RefreshTheme(); } catch { }
                try { _netflixModePage?.ApplyTheme(); } catch { }
                try { _videoLoading?.ApplyTheme(); } catch { }
                try { _infoOverlay?.Invalidate(); } catch { }
                try { _hud?.Invalidate(); } catch { }
                try { _remoteOsd?.Invalidate(); } catch { }
                try { _photoHud?.Invalidate(); } catch { }
                try { _audioMeters?.ApplyThemePalette(); } catch { }
                try { _audioOnlyBanner?.Invalidate(true); } catch { }
                try { _videoLoading?.Invalidate(); } catch { }
                try { _contextOverlayMenu?.Invalidate(); } catch { }
                try
                {
                    if (_menu != null && !_menu.IsDisposed)
                    {
                        ApplyDarkMenuThemeRecursive(_menu.Items);
                        _menu.Invalidate();
                    }
                }
                catch { }

                if (save)
                {
                    try { SaveExtrasConfig(); } catch { }
                    _lblStatus.Text = statusText;
                }

                SyncSettingsHudState();
            }
            catch { }
        }

        // "system" (predefinito): chiaro o scuro come Windows, anche quando Windows cambia.
        // "light"/"dark": scelta esplicita dell'utente, Windows non la tocca piu'.
        private string _uiThemeMode = "system";
        private bool _systemThemeWatched;

        private static bool WindowsUsesLightTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
            }
            catch { return true; }
        }

        private void SetUiThemeMode(string? mode, bool save)
        {
            _uiThemeMode = mode is "light" or "dark" ? mode : "system";
            bool light = _uiThemeMode == "system" ? WindowsUsesLightTheme() : _uiThemeMode == "light";
            // Stesso tono: i colori personalizzati restano come sono.
            if (Theme.IsLight != light) ApplyUiTone(light, save);
            else if (save) { try { SaveExtrasConfig(); } catch { } SyncSettingsHudState(); }
        }

        private void FollowSystemUiTheme()
        {
            if (!_systemThemeWatched)
            {
                _systemThemeWatched = true;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
                FormClosed += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
            }
            if (_uiThemeMode == "system" && Theme.IsLight != WindowsUsesLightTheme())
                ApplyUiTone(!Theme.IsLight, save: false);
        }

        private void OnSystemThemeChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General || _uiThemeMode != "system") return;
            TryBeginInvokeOnUi(() =>
            {
                if (_uiThemeMode != "system" || IsDisposed || _closingForExit) return;
                bool light = WindowsUsesLightTheme();
                if (Theme.IsLight == light) return;
                Dbg.Log("[THEME] Windows switched to " + (light ? "light" : "dark"), Dbg.LogLevel.Info);
                ApplyUiTone(light, save: true);
                try { PublishRemoteState(); } catch { }
            });
        }

        /// <summary>Scelta esplicita (interruttore della libreria, telecomando): da qui Windows non decide piu'.</summary>
        private void ApplyUiToneMode(bool light)
        {
            _uiThemeMode = light ? "light" : "dark";
            ApplyUiTone(light, save: true);
        }

        private void ApplyUiTone(bool light, bool save)
        {
            if (light)
            {
                // Neutri distinti: chrome leggermente freddo, contenuti quasi bianchi
                // e navigazione più profonda. Evita il vecchio grigio uniforme.
                _uiAccentColor = Color.FromArgb(28, 95, 186);
                _uiAccentSoftColor = Color.FromArgb(72, 118, 178);
                _uiSelectionColor = Color.FromArgb(59, 119, 198);
                _uiBorderAccentColor = Color.FromArgb(156, 168, 181);
                _uiPanelColor = Color.FromArgb(226, 230, 235);
                _uiCardColor = Color.FromArgb(246, 247, 249);
                _uiNavColor = Color.FromArgb(211, 217, 224);
            }
            else
            {
                _uiAccentColor = Theme.DefaultAccent;
                _uiAccentSoftColor = Theme.DefaultAccentSoft;
                _uiSelectionColor = Theme.DefaultSelection;
                _uiBorderAccentColor = Theme.DefaultBorderAccent;
                _uiPanelColor = Theme.DefaultPanel;
                _uiCardColor = Theme.DefaultCard;
                _uiNavColor = Theme.DefaultNav;
            }

            ApplyUiThemePalette(save,
                light ? Tx("Modalità chiara attiva", "Light mode enabled") : Tx("Modalità scura attiva", "Dark mode enabled"));
        }

        private Color GetUiThemeColor(string key)
        {
            return NormalizeThemeColorKey(key) switch
            {
                "soft" => _uiAccentSoftColor,
                "selection" => _uiSelectionColor,
                "border" => _uiBorderAccentColor,
                "panel" => _uiPanelColor,
                "card" => _uiCardColor,
                "nav" => _uiNavColor,
                _ => _uiAccentColor
            };
        }

        private static string NormalizeThemeColorKey(string? key)
        {
            if (string.Equals(key, "soft", StringComparison.OrdinalIgnoreCase))
                return "soft";
            if (string.Equals(key, "selection", StringComparison.OrdinalIgnoreCase))
                return "selection";
            if (string.Equals(key, "border", StringComparison.OrdinalIgnoreCase))
                return "border";
            if (string.Equals(key, "panel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "background", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "sfondo", StringComparison.OrdinalIgnoreCase))
                return "panel";
            if (string.Equals(key, "card", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "panels", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "pannelli", StringComparison.OrdinalIgnoreCase))
                return "card";
            if (string.Equals(key, "nav", StringComparison.OrdinalIgnoreCase))
                return "nav";
            return "accent";
        }

        private static string ThemeColorLabel(string? key)
        {
            return NormalizeThemeColorKey(key) switch
            {
                "soft" => "secondario",
                "selection" => "selezione",
                "border" => "bordi",
                "panel" => "sfondo",
                "card" => "pannelli",
                "nav" => "navigazione",
                _ => "primario"
            };
        }

        private static string ThemeColorLabelEnglish(string? key)
        {
            return NormalizeThemeColorKey(key) switch
            {
                "soft" => "Secondary",
                "selection" => "Selection",
                "border" => "Border",
                "panel" => "Background",
                "card" => "Panel",
                "nav" => "Navigation",
                _ => "Primary"
            };
        }

        private void ResetUiThemePalette()
        {
            try
            {
                Theme.ResetPalette();
                _uiAccentColor = Theme.Accent;
                _uiAccentSoftColor = Theme.AccentSoft;
                _uiSelectionColor = Theme.Selection;
                _uiBorderAccentColor = Theme.BorderAccent;
                _uiPanelColor = Theme.Panel;
                _uiCardColor = Theme.Card;
                _uiNavColor = Theme.Nav;
                ApplyUiThemePalette(save: true, statusText: Tx("Colori interfaccia ripristinati", "Interface colors restored"));
            }
            catch { }
        }

        private void ConfigureTmdbApiKey() => _settingsHudPage.ExpandCredentialFields(spotify: false);
        private void ConfigureSpotifyCredentials() => _settingsHudPage.ExpandCredentialFields(spotify: true);

        private static Color NormalizeUiColor(Color color)
            => Color.FromArgb(255, color.R, color.G, color.B);

        private void ApplySettingsRendererChoice(string renderer)
        {
            VRChoice? choice = null;
            if (string.Equals(renderer, "MADVR", StringComparison.OrdinalIgnoreCase))
                choice = VRChoice.MADVR;
            else if (string.Equals(renderer, "MPCVR", StringComparison.OrdinalIgnoreCase))
                choice = VRChoice.MPCVR;
            else if (string.Equals(renderer, "EVR", StringComparison.OrdinalIgnoreCase))
                choice = VRChoice.EVR;
            else if (string.Equals(renderer, "MPV", StringComparison.OrdinalIgnoreCase))
                choice = VRChoice.MPV;

            if (_stereo != Stereo3DMode.None && choice is not (VRChoice.EVR or VRChoice.MPCVR))
            {
                _hasSavedRendererFor3D = true;
                _savedRendererFor3D = choice;
                _lblStatus.Text = Tx("3D->2D attivo: EVR obbligatorio. Preferenza memorizzata per dopo.", "3D->2D enabled: EVR is required. Preference saved for later.");
                _hud.ShowOnce(1400);
                return;
            }

            _hasRuntimeRendererOverride = false;
            _runtimeRendererChoiceOverride = null;
            _manualRendererChoice = choice;
            _lblStatus.Text = Tx("Motore video: ", "Video engine: ") + (choice?.ToString() ?? "Auto");
            if (choice.HasValue && choice.Value != VRChoice.MADVR)
            {
                _enableUpscaling = false;
                _videoUpscalingBackend = "off";
                try { _engine?.SetUpscaling(false); } catch { }
            }

            try { SaveExtrasConfig(); } catch { }
            if (_engine != null)
                ReopenSame();
        }

        private void ApplySettingsMpvRuntimeChoice(bool useV3)
        {
            _mpvRuntimeChoice = useV3 ? MpvRuntimeChoice.X64V3 : MpvRuntimeChoice.X64;
            _mpvRuntimeV3Manual = useV3;
            _lblStatus.Text = useV3 ? Tx("Runtime MPV: V3 (sperimentale)", "MPV runtime: V3 (experimental)") : "MPV runtime: Standard";
            try { SaveExtrasConfig(); } catch { }
            if (_engine != null && (_activeRendererChoice == VRChoice.MPV || _manualRendererChoice == VRChoice.MPV))
                ReopenSame();
        }

        private void EditSettingsMpvAdvancedOptions()
        {
            _mpvSettings ??= new MpvPlaybackSettings();
            using var sheetLayout = SheetPresenter.Layout(this);
            using var dialog = new Form
            {
                Text = Tx("Opzioni avanzate MPV", "Advanced MPV options"),
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(720, 520),
                MinimumSize = new Size(560, 420),
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                ShowInTaskbar = false,
                MaximizeBox = false,
                MinimizeBox = false,
                KeyPreview = true
            };

            var title = new Label
            {
                Dock = DockStyle.Top,
                Height = 54,
                Padding = new Padding(18, 12, 18, 0),
                Text = Tx("Configurazione libera libmpv", "Custom libmpv configuration"),
                ForeColor = Theme.Text,
                BackColor = Theme.Panel,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 15f)
            };
            var help = new Label
            {
                Dock = DockStyle.Top,
                Height = 48,
                Padding = new Padding(20, 2, 20, 8),
                Text = Tx("Una opzione per riga: nome=valore. Puoi usare anche --nome=valore e commenti con #. Queste righe hanno priorita sulle opzioni visuali.", "One option per line: name=value. --name=value and # comments are also supported. These lines override visual settings."),
                ForeColor = Theme.Muted,
                BackColor = Theme.Panel,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI", 9.5f)
            };
            var editor = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(6, 12, 18),
                ForeColor = Theme.Text,
                Font = new Font("Cascadia Mono", 10f),
                Text = _mpvSettings.ExtraOptions ?? string.Empty
            };
            var editorHost = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 6, 20, 12),
                BackColor = Theme.Panel
            };
            editorHost.Controls.Add(editor);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 62,
                Padding = new Padding(14, 11, 14, 10),
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = Theme.Card
            };
            Button MakeButton(string text, DialogResult result, bool primary)
            {
                var button = new Button
                {
                    Text = text,
                    DialogResult = result,
                    Width = 126,
                    Height = 36,
                    Margin = new Padding(8, 0, 0, 0),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = primary ? Theme.Accent : Theme.Card,
                    ForeColor = Color.White,
                    Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.4f)
                };
                button.FlatAppearance.BorderColor = primary ? Theme.BorderAccent : Theme.Border;
                button.FlatAppearance.BorderSize = 1;
                return button;
            }

            Button save = MakeButton(Tx("Salva", "Save"), DialogResult.OK, primary: true);
            Button cancel = MakeButton(Tx("Annulla", "Cancel"), DialogResult.Cancel, primary: false);
            footer.Controls.Add(save);
            footer.Controls.Add(cancel);
            dialog.Controls.Add(editorHost);
            dialog.Controls.Add(help);
            dialog.Controls.Add(title);
            dialog.Controls.Add(footer);
            dialog.AcceptButton = save;
            dialog.CancelButton = cancel;
            dialog.Shown += (_, __) => { try { editor.Focus(); editor.SelectionStart = editor.TextLength; } catch { } };

            if (SheetPresenter.ShowDialog(dialog, this) != DialogResult.OK)
                return;

            _mpvSettings.ExtraOptions = string.IsNullOrWhiteSpace(editor.Text) ? null : editor.Text.Trim();
            _mpvSettings = _mpvSettings.Clone();
            _lblStatus.Text = Tx("MPV: opzioni avanzate salvate", "MPV: advanced options saved");
            try { SaveExtrasConfig(); } catch { }
            SyncSettingsHudState();
        }

        private IReadOnlyDictionary<string, string> BuildSettingsMpvValueSnapshot()
        {
            var s = _mpvSettings ?? new MpvPlaybackSettings();
            static string Fmt(double value)
                => value.ToString(value == Math.Truncate(value) ? "0" : "0.###", CultureInfo.InvariantCulture);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Profile"] = s.Profile,
                ["Hwdec"] = s.Hwdec,
                ["VideoOutput"] = s.VideoOutput,
                ["GpuApi"] = s.GpuApi,
                ["GpuContext"] = s.GpuContext,
                ["VideoSync"] = s.VideoSync,
                ["ToneMapping"] = s.ToneMapping,
                ["TargetPrim"] = s.TargetPrim,
                ["TargetTrc"] = s.TargetTrc,
                ["TargetPeak"] = s.TargetPeak.ToString(CultureInfo.InvariantCulture),
                ["GamutMappingMode"] = s.GamutMappingMode,
                ["BlendSubtitles"] = s.BlendSubtitles,
                ["FboFormat"] = s.FboFormat,
                ["VideoOutputLevels"] = s.VideoOutputLevels,
                ["Scale"] = s.Scale,
                ["ScaleAntiring"] = Fmt(s.ScaleAntiring),
                ["CScale"] = s.CScale,
                ["CScaleAntiring"] = Fmt(s.CScaleAntiring),
                ["DScale"] = s.DScale,
                ["DScaleAntiring"] = Fmt(s.DScaleAntiring),
                ["TScale"] = s.TScale,
                ["ToneMappingParam"] = Fmt(s.ToneMappingParam),
                ["HdrContrastRecovery"] = Fmt(s.HdrContrastRecovery),
                ["HdrContrastSmoothness"] = Fmt(s.HdrContrastSmoothness),
                ["DebandIterations"] = s.DebandIterations.ToString(CultureInfo.InvariantCulture),
                ["DebandThreshold"] = s.DebandThreshold.ToString(CultureInfo.InvariantCulture),
                ["DebandRange"] = s.DebandRange.ToString(CultureInfo.InvariantCulture),
                ["DebandGrain"] = s.DebandGrain.ToString(CultureInfo.InvariantCulture),
                ["Dither"] = s.Dither,
                ["DitherDepth"] = s.DitherDepth,
                ["DitherSizeFruit"] = s.DitherSizeFruit.ToString(CultureInfo.InvariantCulture),
                ["ErrorDiffusion"] = s.ErrorDiffusion,
                ["InterpolationThreshold"] = Fmt(s.InterpolationThreshold),
                ["Cache"] = s.Cache,
                ["DemuxerReadaheadSeconds"] = s.DemuxerReadaheadSeconds.ToString(CultureInfo.InvariantCulture),
                ["DemuxerMaxBytesMb"] = s.DemuxerMaxBytesMb.ToString(CultureInfo.InvariantCulture),
                ["VideoThreads"] = s.VideoThreads.ToString(CultureInfo.InvariantCulture),
                ["SubAuto"] = s.SubAuto,
                ["SubAssOverride"] = s.SubAssOverride,
                ["SubScale"] = Fmt(s.SubScale),
                ["SubFontSize"] = s.SubFontSize.ToString(CultureInfo.InvariantCulture),
                ["SubBorderSize"] = Fmt(s.SubBorderSize),
                ["SubShadowOffset"] = Fmt(s.SubShadowOffset),
                ["AudioChannels"] = s.AudioChannels,
                ["VolumeMax"] = s.VolumeMax.ToString(CultureInfo.InvariantCulture),
                ["GaplessAudio"] = s.GaplessAudio
            };
        }

        private IReadOnlyDictionary<string, bool> BuildSettingsMpvToggleSnapshot()
        {
            var s = _mpvSettings ?? new MpvPlaybackSettings();
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["Interpolation"] = s.Interpolation,
                ["HdrComputePeak"] = s.HdrComputePeak,
                ["TargetColorspaceHint"] = s.TargetColorspaceHint,
                ["IccProfileAuto"] = s.IccProfileAuto,
                ["CorrectDownscaling"] = s.CorrectDownscaling,
                ["LinearDownscaling"] = s.LinearDownscaling,
                ["SigmoidUpscaling"] = s.SigmoidUpscaling,
                ["Deband"] = s.Deband,
                ["TemporalDither"] = s.TemporalDither,
                ["Deinterlace"] = s.Deinterlace,
                ["VideoLavcDr"] = s.VideoLavcDr,
                ["AudioExclusive"] = s.AudioExclusive,
                ["AudioNormalizeDownmix"] = s.AudioNormalizeDownmix
            };
        }

        private void SetSettingsMpvOptionValue(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                return;

            _mpvSettings ??= new MpvPlaybackSettings();
            var s = _mpvSettings;
            string v = value.Trim();
            bool TryInt(out int number) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
            bool TryDouble(out double number) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out number);

            switch (key.Trim())
            {
                case "Profile": s.Profile = v; break;
                case "Hwdec": s.Hwdec = v; break;
                case "VideoOutput": s.VideoOutput = v; break;
                case "GpuApi": s.GpuApi = v; break;
                case "GpuContext": s.GpuContext = v; break;
                case "VideoSync": s.VideoSync = v; break;
                case "ToneMapping": s.ToneMapping = v; break;
                case "TargetPrim": s.TargetPrim = v; break;
                case "TargetTrc": s.TargetTrc = v; break;
                case "TargetPeak": if (TryInt(out int targetPeak)) s.TargetPeak = targetPeak; else return; break;
                case "GamutMappingMode": s.GamutMappingMode = v; break;
                case "BlendSubtitles": s.BlendSubtitles = v; break;
                case "FboFormat": s.FboFormat = v; break;
                case "VideoOutputLevels": s.VideoOutputLevels = v; break;
                case "Scale": s.Scale = v; break;
                case "ScaleAntiring": if (TryDouble(out double scaleAntiring)) s.ScaleAntiring = scaleAntiring; else return; break;
                case "CScale": s.CScale = v; break;
                case "CScaleAntiring": if (TryDouble(out double cscaleAntiring)) s.CScaleAntiring = cscaleAntiring; else return; break;
                case "DScale": s.DScale = v; break;
                case "DScaleAntiring": if (TryDouble(out double dscaleAntiring)) s.DScaleAntiring = dscaleAntiring; else return; break;
                case "TScale": s.TScale = v; break;
                case "ToneMappingParam": if (TryDouble(out double toneParam)) s.ToneMappingParam = toneParam; else return; break;
                case "HdrContrastRecovery": if (TryDouble(out double recovery)) s.HdrContrastRecovery = recovery; else return; break;
                case "HdrContrastSmoothness": if (TryDouble(out double smoothness)) s.HdrContrastSmoothness = smoothness; else return; break;
                case "DebandIterations": if (TryInt(out int debandIterations)) s.DebandIterations = debandIterations; else return; break;
                case "DebandThreshold": if (TryInt(out int debandThreshold)) s.DebandThreshold = debandThreshold; else return; break;
                case "DebandRange": if (TryInt(out int debandRange)) s.DebandRange = debandRange; else return; break;
                case "DebandGrain": if (TryInt(out int debandGrain)) s.DebandGrain = debandGrain; else return; break;
                case "Dither": s.Dither = v; break;
                case "DitherDepth": s.DitherDepth = v; break;
                case "DitherSizeFruit": if (TryInt(out int fruit)) s.DitherSizeFruit = fruit; else return; break;
                case "ErrorDiffusion": s.ErrorDiffusion = v; break;
                case "InterpolationThreshold": if (TryDouble(out double threshold)) s.InterpolationThreshold = threshold; else return; break;
                case "Cache": s.Cache = v; break;
                case "DemuxerReadaheadSeconds": if (TryInt(out int readahead)) s.DemuxerReadaheadSeconds = readahead; else return; break;
                case "DemuxerMaxBytesMb": if (TryInt(out int cacheMb)) s.DemuxerMaxBytesMb = cacheMb; else return; break;
                case "VideoThreads": if (TryInt(out int threads)) s.VideoThreads = threads; else return; break;
                case "SubAuto": s.SubAuto = v; break;
                case "SubAssOverride": s.SubAssOverride = v; break;
                case "SubScale": if (TryDouble(out double subScale)) s.SubScale = subScale; else return; break;
                case "SubFontSize": if (TryInt(out int subFontSize)) s.SubFontSize = subFontSize; else return; break;
                case "SubBorderSize": if (TryDouble(out double subBorder)) s.SubBorderSize = subBorder; else return; break;
                case "SubShadowOffset": if (TryDouble(out double subShadow)) s.SubShadowOffset = subShadow; else return; break;
                case "AudioChannels": s.AudioChannels = v; break;
                case "VolumeMax": if (TryInt(out int volumeMax)) s.VolumeMax = volumeMax; else return; break;
                case "GaplessAudio": s.GaplessAudio = v; break;
                default: return;
            }

            _mpvSettings = s.Clone();
            _lblStatus.Text = Tx("MPV: impostazione salvata", "MPV: setting saved");
            try { SaveExtrasConfig(); } catch { }
        }

        private void CycleSettingsMpvOption(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            _mpvSettings ??= new MpvPlaybackSettings();
            string Next(string current, params string[] values)
            {
                int index = Array.FindIndex(values, v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
                return values[(index + 1 + values.Length) % values.Length];
            }
            double NextDouble(double current, double[] values)
            {
                if (values.Length == 0)
                    return current;
                int index = Array.FindIndex(values, v => Math.Abs(v - current) < 0.0001);
                return values[(index + 1 + values.Length) % values.Length];
            }

            var s = _mpvSettings;
            switch (key.Trim())
            {
                case "Profile":
                    s.Profile = Next(s.Profile, "default", "gpu-hq", "fast", "low-latency");
                    break;
                case "Hwdec":
                    s.Hwdec = Next(s.Hwdec, "auto-safe", "auto", "d3d11va", "d3d11va-copy", "no");
                    break;
                case "VideoOutput":
                    s.VideoOutput = Next(s.VideoOutput, "gpu-next", "gpu", "libmpv", "auto");
                    break;
                case "GpuApi":
                    s.GpuApi = Next(s.GpuApi, "auto", "d3d11", "vulkan", "opengl");
                    break;
                case "GpuContext":
                    s.GpuContext = Next(s.GpuContext, "auto", "d3d11", "winvk", "wgl");
                    break;
                case "VideoSync":
                    s.VideoSync = Next(s.VideoSync, "audio", "display-resample", "display-vdrop", "display-adrop", "desync");
                    break;
                case "ToneMapping":
                    s.ToneMapping = Next(s.ToneMapping, "auto", "bt.2446a", "mobius", "hable", "reinhard", "clip");
                    break;
                case "TargetPrim":
                    s.TargetPrim = Next(s.TargetPrim, "auto", "bt.709", "bt.2020", "display-p3");
                    break;
                case "TargetTrc":
                    s.TargetTrc = Next(s.TargetTrc, "auto", "srgb", "gamma2.2", "pq", "hlg");
                    break;
                case "TargetPeak":
                    s.TargetPeak = NextInt(s.TargetPeak, new[] { 0, 100, 203, 400, 600, 1000 });
                    break;
                case "GamutMappingMode":
                    s.GamutMappingMode = Next(s.GamutMappingMode, "auto", "clip", "perceptual", "relative", "saturation");
                    break;
                case "BlendSubtitles":
                    s.BlendSubtitles = Next(s.BlendSubtitles, "auto", "yes", "no", "video");
                    break;
                case "FboFormat":
                    s.FboFormat = Next(s.FboFormat, "auto", "rgba16f", "rgba32f", "rgb10_a2");
                    break;
                case "VideoOutputLevels":
                    s.VideoOutputLevels = Next(s.VideoOutputLevels, "auto", "limited", "full");
                    break;
                case "Scale":
                    s.Scale = Next(s.Scale, "ewa_lanczossharp", "ewa_lanczos", "spline36", "lanczos", "bicubic", "bilinear");
                    break;
                case "ScaleAntiring":
                    s.ScaleAntiring = NextDouble(s.ScaleAntiring, new[] { 0.0, 0.3, 0.5, 0.7, 1.0 });
                    break;
                case "CScale":
                    s.CScale = Next(s.CScale, "ewa_lanczossoft", "ewa_lanczos", "spline36", "bicubic", "bilinear");
                    break;
                case "CScaleAntiring":
                    s.CScaleAntiring = NextDouble(s.CScaleAntiring, new[] { 0.0, 0.3, 0.5, 0.7, 1.0 });
                    break;
                case "DScale":
                    s.DScale = Next(s.DScale, "mitchell", "catmull_rom", "lanczos", "bicubic", "bilinear");
                    break;
                case "DScaleAntiring":
                    s.DScaleAntiring = NextDouble(s.DScaleAntiring, new[] { 0.0, 0.3, 0.5, 0.7, 1.0 });
                    break;
                case "TScale":
                    s.TScale = Next(s.TScale, "oversample", "linear", "catmull_rom", "mitchell");
                    break;
                case "ToneMappingParam":
                    s.ToneMappingParam = NextDouble(s.ToneMappingParam, new[] { 0.0, 0.25, 0.5, 0.75, 1.0, 1.5, 2.0 });
                    break;
                case "HdrContrastRecovery":
                    s.HdrContrastRecovery = NextDouble(s.HdrContrastRecovery, new[] { 0.0, 0.3, 0.5, 1.0, 2.0, 4.0 });
                    break;
                case "HdrContrastSmoothness":
                    s.HdrContrastSmoothness = NextDouble(s.HdrContrastSmoothness, new[] { 0.0, 25.0, 50.0, 75.0, 100.0 });
                    break;
                case "DebandIterations":
                    s.DebandIterations = NextInt(s.DebandIterations, new[] { 0, 1, 2, 3, 4 });
                    break;
                case "DebandThreshold":
                    s.DebandThreshold = NextInt(s.DebandThreshold, new[] { 0, 32, 48, 64, 96, 128 });
                    break;
                case "DebandRange":
                    s.DebandRange = NextInt(s.DebandRange, new[] { 0, 16, 24, 32, 48 });
                    break;
                case "DebandGrain":
                    s.DebandGrain = NextInt(s.DebandGrain, new[] { 0, 4, 8, 12, 16 });
                    break;
                case "Dither":
                    s.Dither = Next(s.Dither, "auto", "ordered", "error-diffusion", "no");
                    break;
                case "DitherDepth":
                    s.DitherDepth = Next(s.DitherDepth, "auto", "8", "10", "no");
                    break;
                case "DitherSizeFruit":
                    s.DitherSizeFruit = NextInt(s.DitherSizeFruit, new[] { 2, 4, 6, 8 });
                    break;
                case "ErrorDiffusion":
                    s.ErrorDiffusion = Next(s.ErrorDiffusion, "sierra-lite", "floyd-steinberg", "simple", "no");
                    break;
                case "InterpolationThreshold":
                    s.InterpolationThreshold = NextDouble(s.InterpolationThreshold, new[] { 0.0, 0.001, 0.01, 0.05, 0.1 });
                    break;
                case "Cache":
                    s.Cache = Next(s.Cache, "auto", "yes", "no");
                    break;
                case "DemuxerReadaheadSeconds":
                    s.DemuxerReadaheadSeconds = NextInt(s.DemuxerReadaheadSeconds, new[] { 0, 10, 30, 60, 120, 300 });
                    break;
                case "DemuxerMaxBytesMb":
                    s.DemuxerMaxBytesMb = NextInt(s.DemuxerMaxBytesMb, new[] { 0, 64, 128, 256, 512, 1024 });
                    break;
                case "VideoThreads":
                    s.VideoThreads = NextInt(s.VideoThreads, new[] { 0, 2, 4, 8, 16 });
                    break;
                case "SubAuto":
                    s.SubAuto = Next(s.SubAuto, "no", "exact", "fuzzy", "all");
                    break;
                case "SubAssOverride":
                    s.SubAssOverride = Next(s.SubAssOverride, "no", "yes", "force", "strip");
                    break;
                case "SubScale":
                    s.SubScale = NextDouble(s.SubScale, new[] { 0.75, 0.9, 1.0, 1.15, 1.3, 1.5, 2.0 });
                    break;
                case "SubFontSize":
                    s.SubFontSize = NextInt(s.SubFontSize, new[] { 42, 48, 55, 62, 70, 84 });
                    break;
                case "SubBorderSize":
                    s.SubBorderSize = NextDouble(s.SubBorderSize, new[] { 0.0, 1.5, 2.0, 3.0, 4.0, 5.0 });
                    break;
                case "SubShadowOffset":
                    s.SubShadowOffset = NextDouble(s.SubShadowOffset, new[] { 0.0, 1.0, 2.0, 3.0, 4.0 });
                    break;
                case "AudioChannels":
                    s.AudioChannels = Next(s.AudioChannels, "auto", "stereo", "5.1", "7.1");
                    break;
                case "VolumeMax":
                    s.VolumeMax = NextInt(s.VolumeMax, new[] { 100, 120, 150, 200 });
                    break;
                case "GaplessAudio":
                    s.GaplessAudio = Next(s.GaplessAudio, "weak", "yes", "no");
                    break;
                default:
                    return;
            }

            _mpvSettings = s.Clone();
            _lblStatus.Text = Tx("MPV: impostazione salvata", "MPV: setting saved");
            try { SaveExtrasConfig(); } catch { }
        }

        private static int NextInt(int current, int[] values)
        {
            if (values.Length == 0)
                return current;
            int index = Array.IndexOf(values, current);
            return values[(index + 1 + values.Length) % values.Length];
        }

        private void SetSettingsMpvToggle(string key, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            _mpvSettings ??= new MpvPlaybackSettings();
            var s = _mpvSettings;
            switch (key.Trim())
            {
                case "Interpolation":
                    s.Interpolation = enabled;
                    break;
                case "HdrComputePeak":
                    s.HdrComputePeak = enabled;
                    break;
                case "TargetColorspaceHint":
                    s.TargetColorspaceHint = enabled;
                    break;
                case "IccProfileAuto":
                    s.IccProfileAuto = enabled;
                    break;
                case "CorrectDownscaling":
                    s.CorrectDownscaling = enabled;
                    break;
                case "LinearDownscaling":
                    s.LinearDownscaling = enabled;
                    break;
                case "SigmoidUpscaling":
                    s.SigmoidUpscaling = enabled;
                    break;
                case "Deband":
                    s.Deband = enabled;
                    break;
                case "TemporalDither":
                    s.TemporalDither = enabled;
                    break;
                case "Deinterlace":
                    s.Deinterlace = enabled;
                    break;
                case "VideoLavcDr":
                    s.VideoLavcDr = enabled;
                    break;
                case "AudioExclusive":
                    s.AudioExclusive = enabled;
                    break;
                case "AudioNormalizeDownmix":
                    s.AudioNormalizeDownmix = enabled;
                    break;
                default:
                    return;
            }

            _mpvSettings = s.Clone();
            _lblStatus.Text = Tx("MPV: impostazione salvata", "MPV: setting saved");
            try { SaveExtrasConfig(); } catch { }
        }

        private void OpenSettingsProviderPanel(string provider)
        {
            try
            {
                OpenDirectShowProviderDialog(provider);
                _lblStatus.Text = Tx("Pannello proprietario aperto", "Native settings opened");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = Tx("Pannello proprietario non disponibile", "Property page unavailable");
                try { Dbg.Warn("OpenSettingsProviderPanel EX: " + ex.Message); } catch { }
            }
        }

        private void SetSpotlightAtStartup(bool enabled)
        {
            _spotlightAtStartup = enabled;
            try { SaveExtrasConfig(); } catch { }
            SyncSettingsHudState();
        }

        private void SetSpotlightAtWindowsStartup(bool enabled)
        {
            const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "CinecorePlayer2025";
            const string legacyValueName = "CinecorePlayer2025.Spotlight";
            try
            {
                string executable = Environment.ProcessPath ?? Application.ExecutablePath;
                using var runKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true)
                    ?? throw new InvalidOperationException("Impossibile aprire la chiave Esecuzione automatica di Windows.");
                if (enabled)
                {
                    runKey.SetValue(valueName, "\"" + executable + "\"");
                    runKey.DeleteValue(legacyValueName, throwOnMissingValue: false);
                }
                else
                {
                    runKey.DeleteValue(valueName, throwOnMissingValue: false);
                    runKey.DeleteValue(legacyValueName, throwOnMissingValue: false);
                }

                _spotlightAtWindowsStartup = enabled;
                SaveExtrasConfig();
            }
            catch (Exception ex)
            {
                Dbg.Warn("Spotlight Windows startup setting failed: " + ex.Message);
            }
            SyncSettingsHudState();
        }

        private void SetSpotlightSource(string source)
        {
            try { if (_netflixModePage != null) _netflixModePage.NetworkSource = string.Equals(source, "DLNA", StringComparison.OrdinalIgnoreCase); } catch { }
            _spotlightSource = string.Equals(source, "DLNA", StringComparison.OrdinalIgnoreCase) ? "DLNA" : "Library";
            if (_spotlightSource == "DLNA" && string.IsNullOrWhiteSpace(_spotlightServerKey))
            {
                var first = _cinematicLibraryPage?.GetSpotlightServerChoices().FirstOrDefault();
                if (first.HasValue && !string.IsNullOrWhiteSpace(first.Value.Key))
                    _spotlightServerKey = first.Value.Key;
            }
            try { SaveExtrasConfig(); } catch { }
            SyncSettingsHudState();
        }

        private void SetSpotlightServer(string serverKey)
        {
            _spotlightServerKey = serverKey?.Trim() ?? string.Empty;
            try { SaveExtrasConfig(); } catch { }
            SyncSettingsHudState();
        }

        private void RefreshSpotlightServerChoices()
        {
            try
            {
                _cinematicLibraryPage?.RefreshSpotlightServerChoices(() =>
                {
                    if (_settingsHudPage?.Visible != true)
                        return;
                    if (_spotlightSource == "DLNA" && string.IsNullOrWhiteSpace(_spotlightServerKey))
                    {
                        var first = _cinematicLibraryPage?.GetSpotlightServerChoices().FirstOrDefault();
                        if (first.HasValue && !string.IsNullOrWhiteSpace(first.Value.Key))
                        {
                            _spotlightServerKey = first.Value.Key;
                            try { SaveExtrasConfig(); } catch { }
                        }
                    }
                    SyncSettingsHudState();
                });
            }
            catch { }
        }

        private void OpenDirectShowProviderDialog(string provider)
        {
            // Scheda disegnata al 100% e ingrandita da Windows alla scala dello schermo, come tutte le altre.
            using var sheetLayout = SheetPresenter.Layout(this);
            var dialog = new Form
            {
                Text = provider,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = true,
                FormBorderStyle = FormBorderStyle.Sizable,
                BackColor = SystemColors.Control,
                ForeColor = SystemColors.ControlText,
                ClientSize = new Size(1040, 720),
                MinimumSize = new Size(760, 540)
            };

            var host = new DsPropPageHost
            {
                Dock = DockStyle.Fill,
                BackColor = SystemColors.Control
            };
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 54,
                BackColor = SystemColors.Control,
                Padding = new Padding(12, 9, 12, 9)
            };
            var close = new Button
            {
                Text = Tx("Applica", "Apply"),
                Dock = DockStyle.Right,
                Width = 148,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Accent,
                ForeColor = Color.White,
                Font = global::CinecorePlayer2025.AppFonts.Create("Segoe UI Semibold", 9.5f),
                Cursor = Cursors.Hand
            };
            close.FlatAppearance.BorderSize = 0;
            close.Click += (_, __) =>
            {
                try { host.Apply(); } catch { }

            };

            var dismiss = new Button { Text = Tx("Chiudi", "Close"), Dock = DockStyle.Right, Width = 110, DialogResult = DialogResult.Cancel };
            footer.Controls.Add(dismiss);
            footer.Controls.Add(close);
            dialog.CancelButton = dismiss;
            dialog.Controls.Add(host);
            dialog.Controls.Add(footer);

            if (string.Equals(provider, "MPC Video Renderer", StringComparison.OrdinalIgnoreCase))
            {
                host.LoadFromClsid(DsHelpers.CLSID_MpcVideoRenderer);
            }
            else
            {
                string friendlyName = provider switch
                {
                    "LAV Video" => "LAV Video Decoder",
                    "LAV Audio" => "LAV Audio Decoder",
                    "MPC Audio Renderer" => "MPC Audio Renderer",
                    "Sottotitoli" => "XySubFilter",
                    _ => provider
                };
                host.LoadFromFriendlyName(friendlyName);
            }

            try { SheetPresenter.ShowDialog(dialog, this); }
            finally
            {
                try { host.Clear(); } catch { }
                dialog.Dispose();
            }
        }

        private static string? FindSettingsProviderTarget(string provider)
        {
            string baseDir = AppContext.BaseDirectory;
            string workspaceDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
            string[] roots = { baseDir, workspaceDir, Environment.CurrentDirectory };

            string[] relativeCandidates = provider switch
            {
                "madVR" => new[]
                {
                    Path.Combine("third-parties", "madVR09217", "madHcCtrl.exe"),
                    Path.Combine("third-parties", "madVR09217")
                },
                "MPC Video Renderer" => new[]
                {
                    Path.Combine("third-parties", "MpcVideoRenderer-0.9.7.2387", "MpcVideoRenderer64.ax"),
                    Path.Combine("third-parties", "MpcVideoRenderer-0.9.7.2387", "MpcVideoRenderer.ax")
                },
                "LAV Video" or "LAV Audio" => new[]
                {
                    Path.Combine("third-parties", "installers", "LAVFilters-0.80.exe"),
                    Path.Combine("third-parties", "installers")
                },
                "MPC Audio Renderer" => new[]
                {
                    Path.Combine("third-parties"),
                },
                "Sottotitoli" => new[]
                {
                    Path.Combine("third-parties", "XySubFilter_3.1.0.752_x64"),
                    Path.Combine("third-parties")
                },
                _ => new[] { Path.Combine("third-parties") }
            };

            foreach (string root in roots)
            {
                foreach (string relative in relativeCandidates)
                {
                    try
                    {
                        string candidate = Path.Combine(root, relative);
                        if (File.Exists(candidate) || Directory.Exists(candidate))
                            return candidate;
                    }
                    catch { }
                }
            }
            return null;
        }


    }
}
