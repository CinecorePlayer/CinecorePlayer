using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CinecorePlayer2025.Audio;

namespace CinecoreMac;

// Impostazioni del player sul telefono (telecomando › Impostazioni): le stesse voci del pannello del Mac, con gli stessi
// effetti, divise nelle categorie della pagina. RemoteCatalog le descrive (valore e scelte), RemoteSet le applica.
internal sealed partial class SettingsView
{
    internal sealed record RemoteChoice(string value, string label);
    internal sealed record RemoteSetting(string key, string label, string description, string kind, string value,
        RemoteChoice[]? choices = null, string? action = null, bool reload = false);
    internal sealed record RemoteSection(string cat, string title, RemoteSetting[] settings);

    /// <summary>Bitstream dei film: MainWindow lo riapplica al film in corso.</summary>
    public Action<bool>? RemoteBitstream;

    private static RemoteChoice[] Ch(params (string Value, string Label)[] list) => list.Select(c => new RemoteChoice(c.Value, c.Label)).ToArray();
    private static RemoteSetting Tg(string key, string label, string description, bool on, bool reload = false) =>
        new(key, label, description, "toggle", on ? "1" : "0", reload: reload);
    private static RemoteSetting Sel(string key, string label, string description, string value, RemoteChoice[] choices, bool reload = false) =>
        new(key, label, description, "choice", value, choices, reload: reload);
    private static RemoteSetting Act(string key, string label, string description, string action) =>
        new(key, label, description, "action", "", action: action, reload: true);
    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    internal List<RemoteSection> RemoteCatalog()
    {
        var list = new List<RemoteSection>();
        void Add(string cat, string title, params RemoteSetting?[] settings)
        {
            var rows = settings.Where(s => s != null).Cast<RemoteSetting>().ToArray();
            if (rows.Length > 0) list.Add(new RemoteSection(cat, title, rows));
        }

        // ----- Riproduzione -----
        Add("play", "Altro",
            Tg("dvdMenus", "Menu dei DVD", "Come un lettore da tavolo: avvertenze, menu e contenuti speciali. Spento si va dritti al film.", MacDvdNav.Enabled),
            VideoHostClient.WineAvailable ? Sel("madvr-lowpower", "madVR in risparmio energetico",
                "Con il Mac in risparmio energetico la GPU rallenta: madVR può andare a scatti con le impostazioni più pesanti.",
                MacSettings.MadVrLowPower, Ch(("ask", "Chiedi"), ("light", "Profilo leggero"), ("asis", "Non chiedere"))) : null,
            Sel("jellyfin-quality", "Qualità da Jellyfin ed Emby", "Con un limite il server converte il video al volo.",
                JellyfinBitrate.ToString(CultureInfo.InvariantCulture), TranscodeChoices.Select(b => new RemoteChoice(b.ToString(CultureInfo.InvariantCulture), TranscodeLabel(b))).ToArray()));

        // ----- Immagine -----
        if (ImageSizing?.Invoke() is { } sizing)
        {
            Add("picture", "Dimensionamento",
                Sel("sizing-mode", "Dimensionamento", "Come il film riempie lo schermo (tasto Z durante il film).", ((int)sizing.Mode).ToString(CultureInfo.InvariantCulture),
                    Enumerable.Range(0, 4).Select(i => new RemoteChoice(i.ToString(CultureInfo.InvariantCulture), ImageSizingSettings.ModeName((ImageSizingMode)i))).ToArray()),
                Tg("sizing-detect", "Bande nere nel fotogramma", "Misura il formato reale anche quando cambia a metà film.", sizing.DetectBars),
                Tg("sizing-expand", "Scene IMAX a tutto schermo", "Quando l’immagine si allarga in altezza rispetto al resto del film.", sizing.ExpandTallScenes),
                Sel("sizing-transition", "Transizione al cambio di formato", "", sizing.TransitionMs.ToString(CultureInfo.InvariantCulture),
                    new[] { 0, 200, 400, 700, 1000, 1500 }.Select(v => new RemoteChoice(v.ToString(CultureInfo.InvariantCulture), v == 0 ? "Nessuna" : v + " ms")).ToArray()),
                Sel("sizing-bias", "Altezza costante ↔ area costante", "0% tutti i formati alla stessa altezza, 100% alla stessa area.",
                    Num(Math.Round(sizing.Bias, 1)), Enumerable.Range(0, 11).Select(i => new RemoteChoice(Num(i / 10.0), i * 10 + "%")).ToArray()));
        }

        // ----- Audio dei film -----
        Add("audio", "Film",
            Tg("bitstream", "Bitstream al ricevitore", "Dolby e DTS originali all’ampli o alla soundbar. Spento: sempre PCM.", MacSettings.Bitstream, reload: true),
            MacSettings.Bitstream ? Tg("bitstreamTrueHd", "TrueHD e Atmos (sperimentale)", "Serve una porta HDMI diretta o un adattatore che passa l’audio HBR.", MacSettings.Get("bitstreamTrueHd", false)) : null);

        // ----- Sottotitoli -----
        string sub = MacSettings.SubtitleLanguage is { Length: > 0 } saved ? saved : "it";
        Add("subs", "Sottotitoli",
            Sel("subtitle-language", "Lingua preferita", "Quella proposta per prima quando cerchi sottotitoli.", sub,
                Languages.Select(l => new RemoteChoice(l.Code, l.Italian)).ToArray()));

        // ----- Musica -----
        var a = CinecoreAudioSettings.Current;
        Add("music", "Motore",
            Tg("audio:engine", "Cinecore Audio Engine", "Spento, la musica passa dal lettore di sistema (senza equalizzatore e automix).", a.Enabled, reload: true),
            a.Enabled ? Tg("audio:exclusive", "Uscita esclusiva", "Alla frequenza del file, con l’uscita riservata alla musica.", a.Exclusive) : null,
            a.Enabled ? Tg("audio:bitperfect", "Bit perfect", "Niente EQ, ReplayGain, effetti o dissolvenze: i campioni arrivano identici.", a.BitPerfect) : null);
        Add("music", "Passaggi fra i brani",
            Tg("audio:gapless", "Senza pause (gapless)", "Album dal vivo e concept senza silenzi fra un brano e l’altro.", a.Gapless),
            Sel("audio:crossfade", "Dissolvenza incrociata", "Automix sceglie da solo dove e come passare al brano successivo.",
                a.CrossfadeSeconds.ToString(CultureInfo.InvariantCulture), Ch(("0", "Spenta"), ("-1", "Automix"), ("3", "3 s"), ("6", "6 s"), ("10", "10 s")), reload: true),
            a.CrossfadeSeconds < 0 ? Sel("audio:mixmode", "Stile dell’automix", "Brani interi, oppure DJ edit: uscite ed entrate sulle frasi.", a.AutomixMode,
                Ch(("preserve", "Brani interi"), ("dj", "DJ edit"))) : null,
            a.CrossfadeSeconds < 0 ? Sel("audio:mixintensity", "Intensità", "Delicata, bilanciata o da DJ (scambio dei bassi, filtri, eco).", a.AutomixIntensity,
                Ch(("gentle", "Delicata"), ("balanced", "Bilanciata"), ("dj", "DJ"))) : null,
            a.CrossfadeSeconds < 0 ? Tg("audio:temposync", "Sincronizzazione tempo", "Il brano che entra prende il tempo di quello che esce.", a.AutomixTempoSync) : null,
            a.CrossfadeSeconds < 0 ? Tg("audio:trim", "Salta i silenzi", "Il brano successivo entra dal primo suono.", a.AutomixTrimSilence) : null,
            a.CrossfadeSeconds < 0 ? Tg("audio:albummix", "Automix anche negli album", "Fra brani dello stesso album solo se il brano sfuma o finisce in silenzio.", a.AutomixWithinAlbum) : null);
        Add("music", "Suono",
            Tg("audio:eq", "Equalizzatore", "Sulla musica (i film restano com’erano).", a.EqEnabled, reload: true),
            a.EqMode != "parametric" ? Sel("audio:preset", "Curva dell’equalizzatore", "Cambiando una banda dal Mac diventa personalizzata.",
                EqPresets.Any(p => p.Key == a.Preset) ? a.Preset : "custom",
                EqPresets.Select(p => new RemoteChoice(p.Key, p.Name)).Concat(EqPresets.Any(p => p.Key == a.Preset) ? Array.Empty<RemoteChoice>() : new[] { new RemoteChoice("custom", "Personalizzata") }).ToArray()) : null,
            Sel("audio:replaygain", "ReplayGain", "Livella il volume dei brani con i loro tag.", a.ReplayGain, Ch(("off", "Spento"), ("track", "Brano"), ("album", "Album"))),
            Sel("audio:protection", "Protezione dalla saturazione", "Limiter: trattiene i picchi. Margine: abbassa quanto serve.", a.Protection,
                Ch(("Limiter", "Limiter"), ("Headroom", "Margine"), ("Off", "Nessuna"))),
            Tg("audio:loudness", "Loudness", "Più bassi e alti a volume basso.", a.Loudness),
            Tg("audio:dynamiceq", "AutoEQ dinamico", "Compensa l’orecchio secondo il volume, come il Dynamic EQ dei ricevitori.", a.DynamicEq));
        Add("music", "Immagine stereo",
            Sel("audio:crossfeed", "Crossfeed per le cuffie", "Mescola un poco i canali, come con le casse.", a.Crossfeed,
                Ch(("Off", "Spento"), ("Low", "Leggero"), ("Medium", "Medio"), ("High", "Forte"))),
            Sel("audio:width", "Ampiezza stereo", "100% = originale.", Num(a.Width),
                new double[] { 0, 50, 75, 100, 125, 150, 200 }.Select(v => new RemoteChoice(Num(v), Num(v) + "%")).ToArray()),
            Tg("audio:mono", "Mono", "", a.Mono));

        // ----- Schermi -----
        string filmScreen = MacSettings.Get("filmScreen", "");
        var screens = new List<RemoteChoice> { new("", "Dove c’è la finestra") };
        foreach (var s in MacScreens.All()) if (screens.All(c => c.value != s.Name)) screens.Add(new RemoteChoice(s.Name, s.Name));
        if (filmScreen.Length > 0 && screens.All(c => c.value != filmScreen)) screens.Add(new RemoteChoice(filmScreen, filmScreen + " (non collegato)"));
        Add("screen", "Avvio",
            Tg("start-fullscreen", "Avvia a schermo intero", "Il player si apre senza finestra, dal prossimo avvio.", MacSettings.StartFullscreen));
        Add("screen", "Film",
            Sel("film-screen", "Schermo dei film", "Il film si apre a schermo intero su questo schermo; alla chiusura la finestra torna dov’era.", filmScreen, screens.ToArray()),
            Tg("matchRefresh", "Frequenza adatta al film", "Se lo schermo non è a un multiplo dei fotogrammi (24 fps a 60 Hz) passa alla frequenza giusta e poi torna com’era.", MacDisplayRate.Enabled));

        // ----- Libreria e Spotlight -----
        var servers = NetworkStore.Servers.Where(s => NetworkStore.IsMine(s) || s.Available).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new RemoteChoice(s.Key, s.Name + (s.IsJellyfin ? "" : " (DLNA)"))).ToArray();
        bool libNet = MacSettings.Get("librarySource", "Library") == "DLNA";
        string spotSource = MacSettings.Get("spotlightSource", MacSettings.Get("librarySource", "Library"));
        Add("library", "Libreria",
            Sel("library-source", "Fonte predefinita", "La libreria si apre sul Mac o su un server di rete.", libNet ? "DLNA" : "Library",
                Ch(("Library", "Mac"), ("DLNA", "Server di rete")), reload: true),
            libNet && servers.Length > 0 ? Sel("library-server", "Server", "Jellyfin, Emby o DLNA: si collega da solo all’avvio.", MacSettings.Get("libraryServer", ""), servers) : null,
            Sel("music-source", "Foto degli artisti", "La fonte interrogata per prima; le altre fanno da riserva.",
                CinecorePlayer2025.Utilities.MusicArtistArtworkService.PreferredSource is "spotify" or "tidal" or "deezer" ? CinecorePlayer2025.Utilities.MusicArtistArtworkService.PreferredSource : "auto",
                Ch(("auto", "Automatica"), ("spotify", "Spotify"), ("tidal", "TIDAL"), ("deezer", "Deezer"))));
        Add("library", "Spotlight",
            Tg("spotlightHome", "Spotlight all’avvio", "All’apertura la sala a tutto schermo invece della libreria.", MacSettings.Get("spotlightHome", false)),
            Sel("spotlight-source", "Fonte di Spotlight", "", spotSource == "DLNA" ? "DLNA" : "Library", Ch(("Library", "Libreria del Mac"), ("DLNA", "Server di rete")), reload: true),
            spotSource == "DLNA" && servers.Length > 0 ? Sel("spotlight-server", "Server di Spotlight", "", MacSettings.Get("spotlightServer", ""), servers) : null);
        Add("library", "Avvio",
            Tg("openAtLogin", "Apri all’avvio del Mac", "Come in Impostazioni di Sistema › Elementi login.", LoginItem.Enabled));

        // ----- Aspetto e lingua -----
        Add("look", "Interfaccia",
            Sel("language", "Lingua", "Menu, testi e dati dei film (TMDb).", L10n.Language, L10n.Languages.Select(l => new RemoteChoice(l.Code, l.Name)).ToArray(), reload: true),
            Sel("theme-mode", "Tema", "", MacSettings.ThemeMode is "light" or "dark" ? MacSettings.ThemeMode : "system", Ch(("system", "Come macOS"), ("light", "Chiaro"), ("dark", "Scuro"))),
            Sel("header-size", "Barra in alto", "Dimensione di Home, Film, Serie TV… Automatica la ingrandisce sugli schermi piccoli.", MacSettings.Get("headerSize", "auto"),
                Ch(("auto", "Automatica"), ("normal", "Normale"), ("large", "Grande"), ("xlarge", "Molto grande"))));

        // ----- Aggiornamenti -----
        Add("updates", "Cinecore Player",
            Act("update-app", "Versione " + MacUpdate.DisplayName, "Controlla se c’è una versione nuova (la finestra si apre sul Mac).", "Controlla"),
            Act("update-components", "Componenti di terze parti", "FFmpeg, mpv, yt-dlp, madVR e MPC Video Renderer: installata e ultima pubblicata.", "Controlla"));
        Add("updates", "Motore madVR e MPC Video Renderer",
            MacEngine.Busy ? Act("engine-cancel", "Motore", MacEngine.Status, "Annulla")
            : MacEngine.CanInstall ? Act("engine-install", "Motore", MacEngine.Status, MacEngine.Installed ? "Aggiorna" : "Scarica")
            : new RemoteSetting("engine", "Motore", MacEngine.Status, "info", ""));
        return list;
    }

    internal (bool Ok, string Value, string? Error) RemoteSet(string key, string value)
    {
        var inv = CultureInfo.InvariantCulture;
        bool On() => value == "1";
        string Current() => RemoteCatalog().SelectMany(s => s.settings).FirstOrDefault(s => s.key == key)?.value ?? value;
        if (key.StartsWith("audio:", StringComparison.Ordinal))
        {
            var a = CinecoreAudioSettings.Current;
            switch (key[6..])
            {
                case "engine": a.Enabled = On(); break;
                case "exclusive": a.Exclusive = On(); break;
                case "bitperfect": a.BitPerfect = On(); break;
                case "gapless": a.Gapless = On(); break;
                case "crossfade" when int.TryParse(value, NumberStyles.Integer, inv, out int cf) && cf is -1 or 0 or 3 or 6 or 10: a.SetCrossfade(cf); break;
                case "mixmode" when value is "preserve" or "dj": a.AutomixMode = value; break;
                case "mixintensity" when value is "gentle" or "balanced" or "dj": a.AutomixIntensity = value; break;
                case "temposync": a.AutomixTempoSync = On(); break;
                case "trim": a.AutomixTrimSilence = On(); break;
                case "albummix": a.AutomixWithinAlbum = On(); break;
                case "eq": a.EqEnabled = On(); break;
                case "preset" when EqPresets.FirstOrDefault(p => p.Key == value) is { Gains: not null } p:
                    a.Preset = p.Key; a.EqEnabled = true;
                    a.Bands = AudioDspSettings.GraphicBands(p.Gains).Select(CinecoreAudioSettings.BandDto.From).ToList();
                    if (a.EqMode == "graphic31") a.Graphic31 = Interpolate31(p.Gains);
                    break;
                case "replaygain" when value is "off" or "track" or "album": a.ReplayGain = value; break;
                case "protection" when value is "Limiter" or "Headroom" or "Off": a.Protection = value; break;
                case "loudness": a.Loudness = On(); break;
                case "dynamiceq": a.DynamicEq = On(); break;
                case "crossfeed" when value is "Off" or "Low" or "Medium" or "High": a.Crossfeed = value; break;
                case "width" when double.TryParse(value, NumberStyles.Float, inv, out double w) && w is >= 0 and <= 200: a.Width = w; break;
                case "mono": a.Mono = On(); break;
                default: return (false, Current(), "Valore non accettato.");
            }
            CinecoreAudioSettings.Save();
            InvalidateVisual();
            return (true, Current(), null);
        }

        var sizing = ImageSizing?.Invoke();
        switch (key)
        {
            case "dvdMenus": MacDvdNav.Enabled = On(); break;
            case "madvr-lowpower" when value is "ask" or "light" or "asis":
                MacSettings.MadVrLowPower = value; RendererChanged?.Invoke(MacSettings.EffectiveRenderer); break;
            case "jellyfin-quality" when int.TryParse(value, NumberStyles.Integer, inv, out int bitrate) && TranscodeChoices.Contains(bitrate):
                CinecorePlayer2025.Utilities.JellyfinClient.TranscodeBitrate = bitrate; break;
            case "sizing-mode" when sizing != null && int.TryParse(value, out int mode) && mode is >= 0 and < 4:
                sizing.Mode = (ImageSizingMode)mode; ImageSizingChanged?.Invoke(); break;
            case "sizing-detect" when sizing != null: sizing.DetectBars = On(); ImageSizingChanged?.Invoke(); break;
            case "sizing-expand" when sizing != null: sizing.ExpandTallScenes = On(); ImageSizingChanged?.Invoke(); break;
            case "sizing-transition" when sizing != null && int.TryParse(value, out int ms) && ms is >= 0 and <= 1500:
                sizing.TransitionMs = ms; ImageSizingChanged?.Invoke(); break;
            case "sizing-bias" when sizing != null && double.TryParse(value, NumberStyles.Float, inv, out double bias) && bias is >= 0 and <= 1:
                sizing.Bias = Math.Round(bias, 1); ImageSizingChanged?.Invoke(); break;
            case "bitstream": if (RemoteBitstream != null) RemoteBitstream(On()); else MacSettings.Bitstream = On(); break;
            case "start-fullscreen": MacSettings.StartFullscreen = On(); break;
            case "bitstreamTrueHd": MacSettings.Set("bitstreamTrueHd", On()); break;
            case "subtitle-language" when Languages.Any(l => l.Code == value): MacSettings.SubtitleLanguage = value; break;
            case "film-screen": MacSettings.Set("filmScreen", value); break;
            case "matchRefresh":
                MacDisplayRate.Enabled = On();
                if (!MacDisplayRate.Enabled) System.Threading.Tasks.Task.Run(MacDisplayRate.Restore);
                break;
            case "library-source" when value is "Library" or "DLNA":
                MacSettings.Set("librarySource", value);
                if (value == "DLNA" && NetworkStore.Servers.All(s => s.Key != MacSettings.Get("libraryServer", "")) && NetworkStore.Servers.FirstOrDefault(NetworkStore.IsMine) is { } first)
                    MacSettings.Set("libraryServer", first.Key);
                LibrarySourceChanged?.Invoke();
                break;
            case "library-server" when NetworkStore.Servers.Any(s => s.Key == value):
                MacSettings.Set("libraryServer", value); NetworkStore.ConnectedKey = value; LibrarySourceChanged?.Invoke(); break;
            case "music-source" when value is "auto" or "spotify" or "tidal" or "deezer":
                CinecorePlayer2025.Utilities.MusicArtistArtworkService.PreferredSource = value; break;
            case "spotlightHome": MacSettings.Set("spotlightHome", On()); break;
            case "spotlight-source" when value is "Library" or "DLNA":
                MacSettings.Set("spotlightSource", value);
                if (value == "DLNA" && NetworkStore.Servers.All(s => s.Key != MacSettings.Get("spotlightServer", "")) && NetworkStore.Servers.FirstOrDefault(NetworkStore.IsMine) is { } firstSpot)
                    MacSettings.Set("spotlightServer", firstSpot.Key);
                break;
            case "spotlight-server" when NetworkStore.Servers.Any(s => s.Key == value): MacSettings.Set("spotlightServer", value); break;
            case "openAtLogin": if (LoginItem.Enabled != On()) LoginItem.Set(On()); MacSettings.Set("openAtLogin", LoginItem.Enabled); break;
            case "language" when L10n.Languages.Any(l => l.Code == value): L10n.Set(value); break;
            case "theme-mode" when value is "system" or "light" or "dark": SetThemeMode(value); break;
            case "header-size" when value is "auto" or "normal" or "large" or "xlarge": MacSettings.Set("headerSize", value); InterfaceChanged?.Invoke(); break;
            case "update-app" or "update-components" or "engine-install" or "engine-cancel": DeviceServiceRequested?.Invoke(key); break;
            default: return (false, Current(), "Valore non accettato.");
        }
        InvalidateVisual();
        return (true, Current(), null);
    }
}

// Impostazioni del telefono nella forma della pagina V4 di Windows (23/11/2026): menu "Player: Generali / Dispositivi /
// Cinecore Audio", sezioni con titolo maiuscolo, scelte a tendina. Le voci restano quelle del catalogo Mac qui sopra
// (niente RTX/EVR, testi del Mac); Windows invece le registra disegnando la scheda della finestra.
internal sealed partial class SettingsView
{
    internal static readonly string[] RemoteTabs = { "Generali", "Dispositivi", "Cinecore Audio" };

    private static string RemoteTabOf(string cat) => cat switch
    {
        "audio" => "Dispositivi",
        "music" => "Cinecore Audio",
        "remote" or "pc" => "",       // il telefono ha le sue ("Telefono e Mac")
        _ => "Generali",
    };

    private static string RemoteCatLabel(string cat) => cat switch
    {
        "play" => "Riproduzione", "picture" => "Immagine", "subs" => "Sottotitoli", "screen" => "Schermi",
        "library" => "Libreria e Spotlight", "look" => "Aspetto e lingua", "updates" => "Aggiornamenti",
        "audio" => "Audio dei film", "music" => "Musica", _ => cat,
    };

    /// <summary>Una scheda per il telefono, nello stesso formato dei componenti avanzati (pagina V4).</summary>
    internal object RemoteProvider(string tab)
    {
        var sections = RemoteCatalog().Where(s => RemoteTabOf(s.cat) == tab).ToList();
        return new
        {
            id = tab,
            title = L10n.T(tab),
            note = "",
            available = true,
            kind = "player",
            sections = sections.Select(s => (object)new
            {
                group = "",
                // Titoli generici ("Altro", "Avvio", "Film") con la categoria davanti: "Schermi · Avvio".
                title = s.title is "Altro" or "Avvio" or "Film" ? L10n.T(RemoteCatLabel(s.cat)) + " · " + L10n.T(s.title) : L10n.T(s.title),
                settings = s.settings.Select(r => new
                {
                    key = r.key,
                    label = L10n.T(r.label),
                    description = L10n.T(r.description),
                    kind = r.kind,
                    action = r.action == null ? null : L10n.T(r.action),
                    reload = true,
                    value = r.value,
                    choices = r.choices?.Select(c => new { value = c.value, label = L10n.T(c.label) }).ToArray(),
                }).ToArray(),
            }).ToArray(),
        };
    }

    /// <summary>Tocco dal telefono su una voce del player: lo stesso effetto del pannello del Mac.</summary>
    internal object RemoteApply(string tab, string key, string value)
    {
        var (ok, now, error) = RemoteSet(key, value);
        InvalidateVisual();
        return new { ok, value = now, reload = true, error = error == null ? null : L10n.T(error) };
    }
}
