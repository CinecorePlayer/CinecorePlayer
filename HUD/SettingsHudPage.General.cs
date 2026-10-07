using System.Collections.Generic;
using CinecorePlayer2025.Utilities;
#nullable enable
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CinecorePlayer2025.HUD
{
    internal sealed partial class SettingsHudPage
    {
        private const TextFormatFlags SettingsText = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis |
            TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;

        private void DrawGeneralRows(Graphics g, Rectangle body)
        {
            int width = Math.Min(1120, body.Width);
            int controlW = Math.Min(330, Math.Max(210, width * 2 / 5));
            int controlX = body.Left + width - controlW;
            int y = body.Top;
            using var heading = UiFont("Segoe UI Semibold", 9f);
            using var label = UiFont("Segoe UI", 10.5f);
            using var caption = UiFont("Segoe UI", 9f);
            void Section(string title)
            {
                using var line = new Pen(Color.FromArgb(50, Border));
                g.DrawLine(line, body.Left, y, body.Left + width, y);
                TextRenderer.DrawText(g, title.ToUpperInvariant(), heading, new Rectangle(body.Left, y + 20, width, 24), Muted, SettingsText);
                y += 56;
            }
            Rectangle Row(string title, string description = "")
            {
                int h = description.Length > 0 ? 66 : 56;
                TextRenderer.DrawText(g, title, label, new Rectangle(body.Left, y, Math.Max(90, controlX - body.Left - 28), 30), TextColor, SettingsText);
                if (description.Length > 0) TextRenderer.DrawText(g, description, caption, new Rectangle(body.Left, y + 29, Math.Max(90, controlX - body.Left - 28), 23), Muted, SettingsText);
                var r = new Rectangle(controlX, y + 1, controlW, 36); y += h; return r;
            }
            void Choice(Rectangle r, string text, string key)
            {
                DrawValueChip(g, r, text, r.Contains(_lastMouse));
                if (key == "spotlight-server")
                {
                    _hits.Add(new Hit { Bounds = r, Kind = HitKind.SpotlightServer, Key = "center" });
                }
                else
                {
                    _hits.Add(new Hit { Bounds = r, Kind = HitKind.GeneralChoice, Key = key });
                }
            }
            void Toggle(Rectangle r, string text, bool value, HitKind kind) { DrawSettingsSwitch(g, r, text, value); _hits.Add(new Hit { Bounds = r, Kind = kind }); }
            // Valore con − e + ai lati, come i numeri della pagina MPV.
            void Stepper(Rectangle r, string value, string key)
            {
                using var shape = Round(r, 6); using var fill = new SolidBrush(Theme.Card); using var border = new Pen(Color.FromArgb(65, Border));
                g.FillPath(fill, shape); g.DrawPath(border, shape);
                var minus = new Rectangle(r.Left + 4, r.Top, 40, r.Height);
                var plus = new Rectangle(r.Right - 44, r.Top, 40, r.Height);
                TextRenderer.DrawText(g, value, label, new Rectangle(minus.Right, r.Top, Math.Max(1, plus.Left - minus.Right), r.Height), TextColor, SettingsText | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, "−", label, minus, minus.Contains(_lastMouse) ? TextColor : Muted, SettingsText | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, "+", label, plus, plus.Contains(_lastMouse) ? TextColor : Muted, SettingsText | TextFormatFlags.HorizontalCenter);
                _hits.Add(new Hit { Bounds = minus, Kind = HitKind.GeneralChoice, Key = key + "|-" });
                _hits.Add(new Hit { Bounds = plus, Kind = HitKind.GeneralChoice, Key = key + "|+" });
            }
            Section(L("Avvio", "Startup"));
            Toggle(Row(L("Apri Cinecore all’avvio di Windows", "Open Cinecore when Windows starts")),
                _spotlightAtWindowsStartup ? L("Attivo", "Enabled") : L("Disattivato", "Disabled"),
                _spotlightAtWindowsStartup, HitKind.SpotlightAtWindowsStartup);
            var startFullscreen = Row(L("Avvia a schermo intero", "Start full screen"), L("Il player si apre senza finestra, sullo schermo dove si trova il mouse.", "The player opens without a window, on the screen where the mouse is."));
            DrawSettingsSwitch(g, startFullscreen, StartupPreferences.StartFullscreen ? L("Attivo", "Enabled") : L("Disattivato", "Disabled"), StartupPreferences.StartFullscreen);
            _hits.Add(new Hit { Bounds = startFullscreen, Kind = HitKind.GeneralChoice, Key = "start-fullscreen" });
            Section("Spotlight");
            Toggle(Row(L("Spotlight all’avvio", "Spotlight at startup")), _netflixHome ? L("Attivo", "Enabled") : L("Disattivato", "Disabled"), _netflixHome, HitKind.NetflixHome);
            var spotlightSource = Row(L("Fonte Spotlight", "Spotlight source"), L("Scegli libreria locale o server di rete.", "Choose the local library or a network server."));
            DrawValueChip(g, spotlightSource, _spotlightSource == "DLNA" ? L("Rete", "Network") : L("Libreria", "Library"), spotlightSource.Contains(_lastMouse));
            _hits.Add(new Hit { Bounds = spotlightSource, Kind = HitKind.SpotlightSource, Key = "center" });
            if (_spotlightSource == "DLNA")
            {
                string serverName = _spotlightServers.FirstOrDefault(server => string.Equals(server.Key, _spotlightServerKey, StringComparison.OrdinalIgnoreCase)).Name;
                if (string.IsNullOrWhiteSpace(serverName))
                    serverName = string.IsNullOrWhiteSpace(_spotlightServerKey) ? L("Scegli un server", "Choose a server") : L("Server non trovato", "Server not found");
                Choice(Row(L("Server di rete", "Network server"), L("Seleziona Jellyfin o un server DLNA.", "Select Jellyfin or a DLNA server.")), serverName, "spotlight-server");
            }
            y += 12;
            Section(L("Riproduzione", "Playback"));
            Toggle(Row(L("Uscita audio", "Audio output"), L("Audio originale al dispositivo collegato.", "Original audio to the connected device.")), "Bitstream / passthrough", _preferBitstream, HitKind.Bitstream);
            Choice(Row(L("Motore video", "Video engine")), RendererMatches("Auto") ? L("Automatico", "Automatic") : _renderer, "renderer");
            Choice(Row(L("Versione MPV", "MPV version")), _mpvRuntimeV3 ? "V3 experimental" : "Standard", "runtime");
            Choice(Row(L("Qualità da Jellyfin", "Jellyfin quality"), L("Con un limite il server converte il video al volo.", "With a limit the server converts the video on the fly.")),
                JellyfinClient.TranscodeLabel(JellyfinClient.TranscodeBitrate, UiEnglish), "jellyfin-quality");
            var amplifier = Row(L("Amplificatore via rete", "Network amplifier"), L("Volume e mute del dispositivo collegato.", "Control the connected device’s volume and mute."));
            DrawTextAction(g, amplifier, L("Configura e controlla", "Configure and control"), "amplifier");
            _hits.Add(new Hit { Bounds = amplifier, Kind = HitKind.Amplifier });
            var discs = Row(L("Blu-ray protetti", "Protected Blu-rays"), DiscProtection.Status(UiEnglish));
            DrawTextAction(g, discs, L("MakeMKV o KEYDB.cfg", "MakeMKV or KEYDB.cfg"), "");
            _hits.Add(new Hit { Bounds = discs, Kind = HitKind.GeneralChoice, Key = "disc-protection" });
            y += 12;
            if (ImageSizing is { } sizing)
            {
                Section(L("Immagine a schermo intero", "Full-screen image"));
                Choice(Row(L("Dimensionamento", "Sizing"), L("Tasto Z durante il film. Riempi è il comportamento normale.", "Z key during the film. Fill is the normal behaviour.")),
                    sizing.Mode switch
                    {
                        ImageSizingMode.ConstantHeight => L("Altezza costante", "Constant height"),
                        ImageSizingMode.ConstantArea => L("Area costante", "Constant area"),
                        ImageSizingMode.Custom => L("Personalizzata", "Custom"),
                        _ => L("Riempi", "Fill")
                    }, "sizing-mode");
                if (sizing.Mode != ImageSizingMode.Fill)
                {
                    var detect = Row(L("Bande nere nel fotogramma", "Black bars inside the frame"), L("Misura il formato reale anche quando cambia a metà film.", "Measures the real format, also when it changes mid-film."));
                    DrawSettingsSwitch(g, detect, sizing.DetectBars ? L("Attivo", "Enabled") : L("Disattivato", "Disabled"), sizing.DetectBars);
                    _hits.Add(new Hit { Bounds = detect, Kind = HitKind.GeneralChoice, Key = "sizing-detect" });
                    var expand = Row(L("Scene IMAX a tutto schermo", "IMAX scenes on the whole screen"), L("Quando l’immagine si allarga in altezza rispetto al resto del film.", "When the picture grows taller than the rest of the film."));
                    DrawSettingsSwitch(g, expand, sizing.ExpandTallScenes ? L("Attivo", "Enabled") : L("Disattivato", "Disabled"), sizing.ExpandTallScenes);
                    _hits.Add(new Hit { Bounds = expand, Kind = HitKind.GeneralChoice, Key = "sizing-expand" });
                    Stepper(Row(L("Transizione al cambio di formato", "Transition when the format changes")),
                        sizing.TransitionMs <= 0 ? L("Nessuna", "None") : sizing.TransitionMs + " ms", "sizing-transition");
                }
                if (sizing.Mode == ImageSizingMode.Custom)
                {
                    Stepper(Row(L("Altezza costante ↔ area costante", "Constant height ↔ constant area"), L("0% tutti i formati alla stessa altezza, 100% alla stessa area.", "0% every format at the same height, 100% at the same area.")),
                        (int)Math.Round(sizing.Bias * 100) + "%", "sizing-bias");
                    foreach (var preset in ImageSizingSettings.Presets)
                    {
                        double value = sizing.Multiplier(preset.Key);
                        string name = preset.Key switch { "1.43" => "1.43 IMAX", "1.78" => "1.78 (16:9)", "1.33" => "1.33 (4:3)", "2.39" => "2.35 / 2.40", _ => preset.Key };
                        Stepper(Row(L("Altezza del ", "Height of ") + name),
                            value <= 0 ? L("Tutta l’area", "Whole area") : value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "×", "sizing-preset-" + preset.Key);
                    }
                }
                y += 12;
            }
            Section(L("Aspetto", "Appearance"));
            Choice(Row(L("Tema", "Theme")), ThemeMode switch { "light" => L("Chiaro", "Light"), "dark" => L("Scuro", "Dark"), _ => L("Come Windows", "Same as Windows") }, "theme");
            var palette = Row(L("Colore accento", "Accent color"));
            var colors = new[] { Color.FromArgb(35,164,255), Color.FromArgb(81,88,201), Color.FromArgb(113,89,153), Color.FromArgb(183,53,111), Color.FromArgb(200,103,60), Color.FromArgb(24,150,149) };
            for (int i = 0; i < colors.Length; i++)
            {
                var swatch = new Rectangle(palette.Left + (palette.Width - (colors.Length * 22 + (colors.Length - 1) * 20)) / 2 + i * 42, palette.Top + 6, 22, 22);
                using var fill = new SolidBrush(colors[i]); g.FillEllipse(fill, swatch);
                if (_accentColor.ToArgb() == colors[i].ToArgb()) { using var pen = new Pen(TextColor, 1.5f); g.DrawEllipse(pen, Rectangle.Inflate(swatch, 3, 3)); }
                _hits.Add(new Hit { Bounds = Rectangle.Inflate(swatch, 4, 5), Kind = HitKind.GeneralAccent, Key = colors[i].ToArgb().ToString() });
            }
            var customize = Row(L("Palette interfaccia", "Interface palette"));
            DrawTextAction(g, customize, L("Personalizza colori", "Customize colors"), "");
            _hits.Add(new Hit { Bounds = customize, Kind = HitKind.GeneralChoice, Key = "palette" });
            y += 12;
            Section(L("Interfaccia", "Interface"));
            Choice(Row(L("Lingua", "Language")), UiEnglish ? "English" : "Italiano", "language");
            string subtitleLanguage = SubtitleSources.DefaultLanguage is { Length: > 0 } saved ? saved : UiEnglish ? "en" : "it";
            Choice(Row(L("Lingua dei sottotitoli", "Subtitle language"), L("Quella proposta per prima quando cerchi sottotitoli.", "Offered first when you search for subtitles.")),
                SubtitleSources.LanguageLabel(subtitleLanguage, UiEnglish), "subtitle-language");
            var spotlight = Row("Spotlight");
            int spotlightButtonW = Math.Min(220, spotlight.Width);
            var spotlightButton = new Rectangle(spotlight.Left + (spotlight.Width - spotlightButtonW) / 2,
                spotlight.Top + 1, spotlightButtonW, 34);
            DrawActionButton(g, spotlightButton, L("Apri Spotlight", "Open Spotlight"), "spotlight", primary: true);
            _hits.Add(new Hit { Bounds = spotlightButton, Kind = HitKind.OpenNetflix });
            y += 12;
            Section(L("Aggiornamenti", "Updates"));
            // Solo la copia installata si aggiorna da sola; il controllo a mano funziona sempre.
            bool autoUpdate = AppUpdate.State.AutoCheck;
            Toggle(Row(L("Aggiornamenti automatici", "Automatic updates"),
                    AppUpdate.IsInstalledCopy ? L("All’avvio cerca una nuova versione e la propone.", "Looks for a new version at startup and offers it.")
                                              : L("Attivi solo nella copia installata con il setup.", "Only active in the copy installed by the setup.")),
                autoUpdate ? L("Attivi", "Enabled") : L("Disattivati", "Disabled"), autoUpdate, HitKind.UpdateAuto);
            var update = Row(L("Versione installata", "Installed version"), AppUpdate.CurrentLabel);
            DrawTextAction(g, update, L("Controlla aggiornamenti", "Check for updates"), "");
            _hits.Add(new Hit { Bounds = update, Kind = HitKind.UpdateCheck });
            var pendingComponents = ComponentUpdates.State.Pending;
            var components = Row(L("Componenti esterni", "External components"),
                pendingComponents.Count > 0 ? L("Disponibile: ", "Available: ") + string.Join(", ", pendingComponents) : "yt-dlp, LAV Filters, MakeMKV");
            DrawTextAction(g, components, L("Versioni e aggiornamenti", "Versions and updates"), "");
            _hits.Add(new Hit { Bounds = components, Kind = HitKind.GeneralChoice, Key = "components" });
            y += 12;
            Section(L("Metadati", "Metadata"));
            var cardArea = new Rectangle(body.Left, y, width, 0);
            bool customTmdb = MovieMetadataService.HasCustomTmdbApiKey;
            y += DrawCredentialCard(g, cardArea, "tmdb", "TMDb",
                L("Informazioni, locandine e cast di film e serie.", "Film and series information, posters and cast."),
                customTmdb ? L("Chiave personale", "Personal key") : L("Chiave integrata", "Built-in key"), true, _tmdbFieldsExpanded,
                new[] { (L("Chiave API (v3)", "API key (v3)"), _tmdbKeyInput, L("Da themoviedb.org › Impostazioni › API. Vuoto: chiave integrata.", "From themoviedb.org › Settings › API. Empty: built-in key.")) },
                customTmdb, L("Usa chiave integrata", "Use built-in key"));
            y += 14;
            cardArea.Y = y;
            bool spotifyReady = SpotifyArtistArtworkService.IsConfigured;
            y += DrawCredentialCard(g, cardArea, "spotify", "Spotify",
                L("Foto degli artisti e sfondi degli album.", "Artist photos and album backgrounds."),
                spotifyReady ? L("Configurato", "Configured") : L("Non configurato", "Not configured"), spotifyReady, _spotifyFieldsExpanded,
                new[]
                {
                    ("Client ID", _spotifyIdInput, L("Da developer.spotify.com › Dashboard.", "From developer.spotify.com › Dashboard.")),
                    ("Client Secret", _spotifySecretInput, spotifyReady ? L("Vuoto: conserva il segreto salvato.", "Empty: keep the saved secret.") : L("Richiesto al primo salvataggio.", "Required the first time."))
                },
                spotifyReady, L("Rimuovi", "Remove"));
            y += 14;
            cardArea.Y = y;
            bool tidalReady = TidalArtistArtworkService.IsConfigured;
            y += DrawCredentialCard(g, cardArea, "tidal", "TIDAL",
                L("Foto degli artisti dal catalogo TIDAL (solo metadati, niente streaming).", "Artist photos from the TIDAL catalogue (metadata only, no streaming)."),
                tidalReady ? L("Configurato", "Configured") : L("Non configurato", "Not configured"), tidalReady, _tidalFieldsExpanded,
                new[]
                {
                    ("Client ID", _tidalIdInput, L("Da developer.tidal.com › Dashboard.", "From developer.tidal.com › Dashboard.")),
                    ("Client Secret", _tidalSecretInput, tidalReady ? L("Vuoto: conserva il segreto salvato.", "Empty: keep the saved secret.") : L("Richiesto al primo salvataggio.", "Required the first time."))
                },
                tidalReady, L("Rimuovi", "Remove"));
            y += 14;
            string preferred = MusicArtistArtworkService.PreferredSource;
            var musicSource = new Rectangle(controlX, y + 1, controlW, 36);
            TextRenderer.DrawText(g, L("Fonte per la musica", "Music source"), label, new Rectangle(body.Left, y, Math.Max(90, controlX - body.Left - 28), 30), TextColor, SettingsText);
            TextRenderer.DrawText(g, L("La prima a essere interrogata; le altre fanno da riserva.", "Asked first; the others are the fallback."), caption, new Rectangle(body.Left, y + 29, Math.Max(90, controlX - body.Left - 28), 23), Muted, SettingsText);
            DrawValueChip(g, musicSource, preferred switch { "spotify" => "Spotify", "tidal" => "TIDAL", "deezer" => "Deezer", _ => L("Automatica", "Automatic") }, musicSource.Contains(_lastMouse));
            _hits.Add(new Hit { Bounds = musicSource, Kind = HitKind.GeneralChoice, Key = "music-source" });
            y += 66;

            int measured = y - body.Top + 40;
            if (measured != _generalMeasuredHeight)
            {
                _generalMeasuredHeight = measured;
                if (IsHandleCreated) BeginInvoke(new Action(Invalidate));
            }
        }

        private void DrawSettingsSwitch(Graphics g, Rectangle r, string text, bool selected)
        {
            // Stessa forma in entrambi gli stati: pista sempre visibile, cambia solo il colore.
            // Interruttore allineato a destra nella colonna dei controlli (come scelte e
            // pulsanti), con lo stato scritto alla sua sinistra.
            var track = new Rectangle(r.Right - 44, r.Top + (r.Height - 22) / 2, 42, 22);
            using var shape = Round(track, 11);
            using var fill = new SolidBrush(selected ? Accent : Color.FromArgb(Theme.IsLight ? 34 : 46, TextColor)); g.FillPath(fill, shape);
            using var edge = new Pen(selected ? Accent : Color.FromArgb(Theme.IsLight ? 90 : 80, TextColor)); g.DrawPath(edge, shape);
            using var knob = new SolidBrush(selected ? Color.White : Color.FromArgb(Theme.IsLight ? 255 : 230, Theme.IsLight ? Color.White : TextColor));
            g.FillEllipse(knob, track.Left + (selected ? 23 : 3), track.Top + 3, 16, 16);
            using var font = UiFont("Segoe UI", 10f);
            TextRenderer.DrawText(g, text, font, new Rectangle(r.Left, r.Top, Math.Max(1, track.Left - r.Left - 14), r.Height), Muted, SettingsText | TextFormatFlags.Right);
        }

        private void ShowGeneralChoice(string key, Rectangle source, Point click)
        {
            if (key == "spotlight-server")
            {
                var options = new List<ChoicePopup.Option>();
                foreach (var server in _spotlightServers)
                {
                    string serverKey = server.Key;
                    options.Add(new(server.Name,
                        () => SpotlightServerChanged?.Invoke(serverKey),
                        Selected: string.Equals(serverKey, _spotlightServerKey, StringComparison.OrdinalIgnoreCase)));
                }
                options.Add(new(L("Aggiorna elenco server", "Refresh server list"), () => SpotlightServerRefreshRequested?.Invoke()));
                ChoicePopup.Show(this, new Point(source.Left, source.Bottom + 4), options, source.Width);
                return;
            }
            if (key == "start-fullscreen")
            {
                StartupPreferences.StartFullscreen = !StartupPreferences.StartFullscreen;
                Invalidate();
                return;
            }
            if (key.StartsWith("sizing-", StringComparison.Ordinal) && ImageSizing is { } sizing)
            {
                int step = key.EndsWith("|-", StringComparison.Ordinal) ? -1 : 1;
                string name = key.Split('|')[0];
                if (name == "sizing-mode")
                {
                    int direction = click.X < source.Left + source.Width / 2 ? -1 : 1;
                    sizing.Mode = (ImageSizingMode)(((int)sizing.Mode + direction + 4) % 4);
                }
                else if (name == "sizing-detect") sizing.DetectBars = !sizing.DetectBars;
                else if (name == "sizing-expand") sizing.ExpandTallScenes = !sizing.ExpandTallScenes;
                else if (name == "sizing-transition")
                {
                    int[] values = { 0, 200, 400, 800, 1200 };
                    int index = Array.FindIndex(values, value => value >= sizing.TransitionMs);
                    sizing.TransitionMs = values[Math.Clamp((index < 0 ? values.Length - 1 : index) + step, 0, values.Length - 1)];
                }
                else if (name == "sizing-bias") sizing.Bias = Math.Clamp(Math.Round(sizing.Bias * 20 + step) / 20, 0, 1);
                else if (name.StartsWith("sizing-preset-", StringComparison.Ordinal))
                {
                    string preset = name["sizing-preset-".Length..];
                    double value = sizing.Multiplier(preset);
                    // Sotto 0,50 c'e' "tutta l'area" (0): l'immagine riempie lo schermo come in Riempi.
                    value = value <= 0 ? (step > 0 ? 0.50 : 0) : Math.Round(value * 100 + step) / 100;
                    sizing.Multipliers[preset] = value < 0.495 ? 0 : Math.Min(value, 1.60);
                }
                Invalidate();
                ImageSizingChanged?.Invoke();
                return;
            }
            if (key == "components")
            {
                ComponentsRequested?.Invoke();
                return;
            }
            if (key == "disc-protection")
            {
                var options = new List<ChoicePopup.Option>
                {
                    new(L("Importa il tuo KEYDB.cfg…", "Import your KEYDB.cfg…"), ImportKeyDb),
                    new(DiscProtection.MakeMkvFolder() != null ? L("MakeMKV: versione e aggiornamenti…", "MakeMKV: version and updates…") : L("Installa MakeMKV…", "Install MakeMKV…"),
                        () => ComponentsRequested?.Invoke())
                };
                ChoicePopup.Show(this, new Point(source.Left, source.Bottom + 4), options, source.Width);
                return;
            }
            if (key == "tidal-credentials")
            {
                ExpandCredentialFields(spotify: false, tidal: true);
                return;
            }
            if (key == "music-source")
            {
                var options = new[] { ("auto", L("Automatica (Spotify, TIDAL, Deezer)", "Automatic (Spotify, TIDAL, Deezer)")), ("spotify", "Spotify"), ("tidal", "TIDAL"), ("deezer", "Deezer") }
                    .Select(entry => new ChoicePopup.Option(entry.Item2, () => { MusicArtistArtworkService.PreferredSource = entry.Item1; Invalidate(); }, Selected: entry.Item1 == MusicArtistArtworkService.PreferredSource)).ToList();
                ChoicePopup.Show(this, new Point(source.Left, source.Bottom + 4), options, source.Width);
                return;
            }
            if (key == "jellyfin-quality")
            {
                var options = JellyfinClient.TranscodeChoices.Select(bitrate => new ChoicePopup.Option(
                    JellyfinClient.TranscodeLabel(bitrate, UiEnglish),
                    () => { JellyfinClient.TranscodeBitrate = bitrate; Invalidate(); },
                    Selected: bitrate == JellyfinClient.TranscodeBitrate)).ToList();
                ChoicePopup.Show(this, new Point(source.Left, source.Bottom + 4), options, source.Width);
                return;
            }
            if (key == "subtitle-language")
            {
                string current = SubtitleSources.DefaultLanguage is { Length: > 0 } saved ? saved : UiEnglish ? "en" : "it";
                var options = SubtitleSources.AllLanguages.Select(language => new ChoicePopup.Option(
                    UiEnglish ? language.English : language.Italian,
                    () => { SubtitleSources.DefaultLanguage = language.Code; Invalidate(); },
                    Selected: language.Code == current)).ToList();
                ChoicePopup.Show(this, new Point(source.Left, source.Bottom + 4), options, source.Width, searchable: true);
                return;
            }
            if (key == "palette")
            {
                var options = new List<ChoicePopup.Option>();
                foreach (var entry in new[] { ("accent", L("Accento", "Accent")), ("soft", L("Accento secondario", "Secondary accent")), ("selection", L("Selezione", "Selection")), ("border", L("Bordi", "Borders")), ("panel", L("Sfondo", "Background")), ("card", L("Superfici", "Surfaces")), ("nav", L("Navigazione", "Navigation")) })
                    options.Add(new(entry.Item2, () => ThemeColorRequested?.Invoke(entry.Item1)));
                options.Add(new(L("Ripristina colori", "Reset colors"), () => ThemeResetRequested?.Invoke()));
                ChoicePopup.Show(this, new Point(source.Left, source.Bottom + 4), options, source.Width);
            }
            else
            {
                string[] values = key == "language" ? new[] { "it", "en" } : key == "renderer" ? new[] { "Auto", "MADVR", "MPCVR", "EVR", "MPV" } : key == "theme" ? new[] { "system", "light", "dark" } : new[] { "Standard", "V3" };
                string current = key == "language" ? _language : key == "renderer" ? _renderer : key == "theme" ? ThemeMode : _mpvRuntimeV3 ? "V3" : "Standard";
                int index = Array.FindIndex(values, value => string.Equals(value, current, StringComparison.OrdinalIgnoreCase));
                int direction = click.X < source.Left + 30 ? -1 : 1;
                if (index < 0) index = direction > 0 ? -1 : 0;
                string selected = values[(index + direction + values.Length) % values.Length];
                if (key == "language") LanguageChanged?.Invoke(selected);
                else if (key == "renderer") RendererChanged?.Invoke(selected);
                else if (key == "theme") { ThemeMode = selected; Invalidate(); ThemeModeChanged?.Invoke(selected); }
                else MpvRuntimeV3Changed?.Invoke(selected == "V3");
            }
        }

        // Il file di chiavi e' dell'utente: il player lo copia soltanto dove libaacs lo cerca.
        private void ImportKeyDb()
        {
            using var dialog = new OpenFileDialog
            {
                Title = L("Scegli il tuo KEYDB.cfg", "Choose your KEYDB.cfg"),
                Filter = "KEYDB.cfg|*.cfg|" + L("Tutti i file", "All files") + "|*.*",
                RestoreDirectory = true
            };
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            if (!DiscProtection.ImportKeyDb(dialog.FileName, out string error))
                MessageBox.Show(FindForm(), L("Il file non sembra un KEYDB.cfg valido.", "The file does not look like a valid KEYDB.cfg.") + (error is "size" or "format" ? "" : "\n" + error),
                    "Cinecore Player", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Invalidate();
        }

        private void ChangeSpotlightSource(string direction, Rectangle source, Point click)
        {
            int step = string.Equals(direction, "left", StringComparison.Ordinal) ? -1
                : string.Equals(direction, "right", StringComparison.Ordinal) ? 1
                : click.X < source.Left + source.Width / 2 ? -1 : 1;
            string[] sources = { "Library", "DLNA" };
            int current = string.Equals(_spotlightSource, "DLNA", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            string next = sources[(current + step + sources.Length) % sources.Length];
            _spotlightSource = next;
            Invalidate();
            SpotlightSourceChanged?.Invoke(next);
        }

        private void ChangeSpotlightServer(string direction)
        {
            if (_spotlightServers.Count == 0)
            {
                SpotlightServerRefreshRequested?.Invoke();
                return;
            }

            int step = string.Equals(direction, "left", StringComparison.Ordinal) ? -1 : 1;
            int current = _spotlightServers.FindIndex(server =>
                string.Equals(server.Key, _spotlightServerKey, StringComparison.OrdinalIgnoreCase));
            if (current < 0)
                current = step > 0 ? -1 : 0;

            string next = _spotlightServers[(current + step + _spotlightServers.Count) % _spotlightServers.Count].Key;
            _spotlightServerKey = next;
            Invalidate();
            SpotlightServerChanged?.Invoke(next);
        }

        private void DrawAppearanceRows(Graphics g, Rectangle body)
        {
            int w = Math.Min(880, body.Width);
            var colors = new[] { ("accent", L("Accento", "Accent"), _accentColor), ("soft", L("Accento secondario", "Secondary accent"), _accentSoftColor),
                ("selection", L("Selezione", "Selection"), _selectionColor), ("border", L("Bordi", "Borders"), _borderAccentColor),
                ("panel", L("Sfondo", "Background"), _panelColor), ("card", L("Superfici", "Surfaces"), _cardColor), ("nav", L("Navigazione", "Navigation"), _navColor) };
            int y = body.Top;
            foreach (var (key, label, color) in colors)
            {
                var r = new Rectangle(body.Left, y, w, 54);
                using var font = UiFont("Segoe UI", 10.3f);
                TextRenderer.DrawText(g, label, font, new Rectangle(r.Left, r.Top, r.Width - 80, r.Height), TextColor, SettingsText);
                using var swatch = new SolidBrush(color);
                using var shape = Round(new Rectangle(r.Right - 54, r.Top + 13, 42, 28), 6);
                g.FillPath(swatch, shape);
                using var border = new Pen(Color.FromArgb(65, Color.White)); g.DrawPath(border, shape);
                using var divider = new Pen(Color.FromArgb(30, Border)); g.DrawLine(divider, r.Left, r.Bottom, r.Right, r.Bottom);
                _hits.Add(new Hit { Bounds = r, Kind = HitKind.AccentColor, Key = key });
                y += 58;
            }
            var reset = new Rectangle(body.Left, y + 18, 190, 36);
            DrawTextAction(g, reset, L("Ripristina colori", "Reset colors"), "reset");
            _hits.Add(new Hit { Bounds = reset, Kind = HitKind.ResetTheme });
        }
    }
}
