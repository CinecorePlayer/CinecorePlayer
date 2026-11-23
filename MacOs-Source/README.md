# Cinecore Player per Mac

Lettore multimediale per macOS: film, serie, musica, foto, dischi e server di rete in un'unica app, con la qualità video di madVR e MPC Video Renderer e un motore audio proprio per la musica.
È il porting nativo di Cinecore Player per Windows (Beta 3.6). L'interfaccia è riscritta in Avalonia, mentre la logica di libreria, metadati, audio e Automix è in gran parte condivisa con Windows.

Stato: anteprima, 14/11/2026. Lo stato voce per voce è in `mac/PIANO_LAVORI_MAC.md`.

---

## Specifiche

| | |
|---|---|
| Sistema | macOS 13 o successivo; Liquid Glass da macOS 26 (provato su macOS 27.2) |
| Processore | Apple Silicon (arm64), provato su MacBook Air M4 |
| Interfaccia | .NET 9 + Avalonia 11.3 (Skia), disegnata su misura; vetro vero di macOS (NSGlassEffectView, o NSVisualEffectView prima di macOS 26) |
| Video nativo | libmpv (mpv 0.42) con Vulkan su MoltenVK; decodifica VideoToolbox |
| Video di qualità | madVR e MPC Video Renderer con LAV Filters, sotto Wine 11 (driver winemac modificato, D3D11 tramite DXMT su Metal) |
| Audio della musica | Cinecore Audio Engine: FFmpeg, poi la catena DSP di Windows in doppia precisione, poi CoreAudio (AudioQueue); float 48 kHz |
| Audio dei film | CoreAudio tramite libmpv. madVR e MPC-VR possono usare la stessa uscita audio nativa. |
| Time-stretch | Rubber Band 4 (motore R3), con un WSOLA interno come riserva |
| Componenti inclusi nel pacchetto | libmpv e dipendenze, ffmpeg/ffprobe, MoltenVK, libdvdread/libdvdnav/libdvdcss, Rubber Band (copiati da Homebrew in `Contents/Frameworks`) |
| Lingua | italiano (inglese e spagnolo da fare) |

---

## Funzionalità

### Video
- **Motori**: madVR, MPC-VR, mpv o Auto. Il motore scelto non cambia mai da solo: in risparmio energetico l'app avvisa e propone l'alternativa.
- **Statistiche**: madVR (Ctrl+J), MPC-VR e mpv, dal menu.
- **Profili HDR di madVR**: Immagine/HDR come scelta unica per tutti i renderer, upscaling di madVR dal menu, uscita HDR in EDR con mpv.
- **Immagine**: cambio della frequenza dello schermo prima dell'avvio, ritaglio, dimensionamento a schermo intero, 3D (SBS/TAB → 2D) con tutti i motori, schermo intero esteso su due schermi (Maiusc+F) per i Full-SBS.
- **Controlli**: HUD con timeline e anteprima, capitoli, ±10 s, passo singolo, scan veloce (×0,5 … ×4), PiP, "Vai a…", Salta intro / prossimo episodio, Cast.
- **Ripresa e volume**: ripresa dal punto lasciato, "Continua a guardare", volume ricordato per film.
- **YouTube e indirizzi web**: tramite yt-dlp, con limite di risoluzione.

### Dischi
- **DVD**: cartella, ISO o lettore, con i menu interattivi (libdvdnav) per tutti i motori. Menu, avvertenze e titoli brevi passano da mpv, il film dal motore scelto. Ripresa del punto per disco ("Riprendi il film / Dal menu"). Supporta CSS.
- **CD audio**: titoli, artista, anno e copertina da MusicBrainz, impronta del disco calcolata come su Windows. Riproduzione senza pause fra le tracce e testi.
- Blu-ray e UHD: da fare.

### Audio dei film
- **Tracce e uscita**: scelta della traccia e del dispositivo d'uscita, che cambia al volo.
- **Ritardo audio**: dal menu e da Ctrl ±, salvato per film.
- **Sincronizzazione**: automatica fra tracce interne, anche con ritardo variabile; traccia audio esterna con conversione di velocità.
- **Livello**: amplificazione fino a +12 dB (mpv).
- **Audio spaziale**: in cuffia (AirPods con i movimenti della testa) e sugli altoparlanti del Mac il 5.1 e il 7.1 passano dallo spazializzatore di Apple invece di essere ridotti in stereo (patch `mac/mpv-patches/coreaudio-spatial-v0.42.0.patch`, opzione `--coreaudio-spatial`). Vale con tutti i motori (con madVR e MPC-VR l'audio passa a mpv nativo). Il DTS:X arriva in 7.1.4 con i quattro canali alti (libavcodec con le patch in `mac/ffmpeg-patches`, `mac/build-ffmpeg-dtsx.sh`); il TrueHD Atmos è decodificato da LibTrueHD e i suoi oggetti sono resi in 7.1.4 secondo la loro posizione (patch `truehd-atmos` di libmpv, serve `brew install rustup` per compilarla). Con il ricevitore non cambia nulla. Impostazioni › Riproduzione › Audio spaziale.

### Sottotitoli
- **Tracce**: interne ed esterne, solo forzati.
- **Scaricamento**: ricerca e download da YIFY, Addic7ed, Subdl e OpenSubtitles.
- **Sincronizzazione**: riallineamento automatico sull'audio.

### Musica: Cinecore Audio Engine
- **Passaggi fra i brani**: senza pause (gapless), dissolvenza incrociata a potenza costante, oppure **Automix**. L'Automix analizza battute, frasi, tonalità e voce, poi sceglie fra passaggi da DJ (EQ a tre bande, filtri, eco, brake, loop). Con "Sincronizzazione tempo" il brano che entra prende il tempo di quello che esce, agganciato battuta per battuta, e poi torna al suo.
- **EQ**: 10 bande, 31 bande o parametrico, con i preset di Windows; preamplificazione.
- **Elaborazione**: limiter true-peak, ReplayGain (brano o album), loudness, AutoEQ dinamico, crossfeed, bilanciamento, ampiezza stereo, mono.
- **Grafici**: Spettro, VU ad ago, Spettrogramma, Onda, Loudness, Dinamica, Stereo (goniometro), Livelli con correlazione di fase, Info. Sotto ogni grafico LUFS integrato, true peak, gamma e PLR in tempo reale.
- **Ascolto**: brano a schermo intero, testi sincronizzati (LRCLIB, Genius, lyrics.ovh) con sincronizzazione vocale locale (faster-whisper, GPU), mini player.
- **Barra e coda**: barra e coda sul vetro vero di macOS. La coda mostra cronologia, brano in corso e brani in arrivo; si scorre, si riordina trascinando e indica la durata di ogni brano. Volume a scomparsa.
- **Libreria e servizi**: album, artisti con foto, playlist, preferiti, radio (brani simili a fine coda), avanzamento degli album, ascolti inviati a Last.fm e ListenBrainz.

### Libreria
- **Sezioni**: Home, Film, Serie TV, Musica, Foto, Video, Playlist, Preferiti, Diario e Rete.
- **Metadati**: TMDb con correzione manuale. La scheda del titolo ha panoramica, dettagli, recensioni (TMDb, IMDb, Letterboxd, Metacritic), troupe e cast.
- **Navigazione**: raccolte, filtri, ordinamenti e ricerca globale su vetro vero.
- **Strumenti**: rapporto della libreria e Analisi HDR (metadati, misura reale, grafici, tempo reale, esportazione).
- **Spotlight**: la "sala" a tutto schermo, con sfondi preparati in anticipo e passaggi fluidi.
- **Modalità cinema**: schermata prima del film e demo.

### Rete e servizi
- **Server**: DLNA, Jellyfin ed Emby (anche Quick Connect), con qualità dello streaming regolabile.
- **Servizi**: Trakt (consigli, visti e voti), Spotify e TIDAL per le foto degli artisti.
- **Telecomando dal telefono**: abbinamento con QR, comandi, tracce, libreria e coda.

### Foto
- Visualizzatore con zoom, rotazione e presentazione.
- Modifica (penna, forme, testo, filtri) con salvataggio in copia.
- Condivisione, copia, stampa, apri con, Cestino.

### Sistema
- **Integrazione**: Apri con dal Finder, doppio clic e trascinamento (anche ad app aperta), apertura all'avvio del Mac, schermo intero di sistema.
- **Interfaccia**: tema chiaro/scuro con colore d'accento e di sfondo; dimensione della barra in alto regolabile (automatica sugli schermi piccoli). Il menu tasto destro blocca le voci finché non si possono usare.
- **Sicurezza**: chiavi e token cifrati in un archivio locale, mai in chiaro.

---

## Da fare
- Blu-ray e UHD.
- Inglese e spagnolo.
- Editor del mix manuale.
- Uscita esclusiva/bit-perfect della musica.
- Analisi audio dal vivo dei film.
- Stile dei sottotitoli.
- Dispositivi di casa (amplificatore, WLED, automazioni).
- Indirizzo del canale di aggiornamento (la struttura c'è).

## Bitstream HDMI

Seleziona il ricevitore in **Audio › Uscita audio** e attiva **Auto (bitstream se conviene)**. Per Jellyfin scegli la qualità originale, così il server consegna la traccia compressa del film.

Se macOS espone i formati compressi dell'uscita (Dolby Digital, Dolby Digital Plus, TrueHD) il player li usa direttamente.

Molti adattatori USB-C/HDMI riscrivono l'EDID del ricevitore e dichiarano **solo PCM**: è il caso del Marantz SR7010 collegato al MacBook (adattatore con convertitore MegaChips, 8 voci audio tutte LPCM). Lì macOS non offre nessun formato compresso e il bitstream può passare solo **dentro il PCM** (pacchetti IEC 61937 in un flusso LPCM intero a 16 bit, bit per bit, in uscita esclusiva): il ricevitore li riconosce da solo. In **Impostazioni › Riproduzione**, sotto *Uscita audio*, compare una riga per formato (Dolby Digital, DTS, Dolby Digital Plus/Atmos, Dolby TrueHD/Atmos, DTS-HD MA/DTS:X) con **Prova**: suona qualche secondo di silenzio codificato in quel formato. Se il ricevitore mostra il formato, attiva *Bitstream* per quella riga; se invece si sente un fruscio, il ricevitore lo sta trattando come PCM: lascia *PCM* (l'audio viene decodificato in PCM multicanale). Fai la prova a volume basso. I formati restano spenti finché non li attivi.

Senza un adattatore che passi l'EDID originale i formati HD (TrueHD, DTS-HD MA) potrebbero non essere riconosciuti: in quel caso restano in PCM 7.1 senza perdita (si perdono solo gli oggetti Atmos/DTS:X).

L'override sperimentale dell'EDID (`CINECORE_HDMI_EDID_TEST=1`) resta disattivato nell'avvio normale.

## Compilare
```
mac/build-native-app.sh      # crea "dist/CinecorePlayer2026.app"
mac/make-mac-release.sh      # crea il .dmg (e lo .zip per gli aggiornamenti) in dist/releases
```
Servono .NET 9 (`~/.dotnet`), Xcode Command Line Tools e le librerie Homebrew usate da mpv. La prima compilazione della libmpv con la patch CoreAudio richiede Meson, Ninja e pkg-config. Il pacchetto riusa la libreria compilata in `dist/mac-native/mpv-coreaudio`.
Il runtime Wine per madVR/MPC-VR si prepara con gli script in `mac/` (vedi `mac/PIANO_LAVORI_MAC.md`).
L'icona si rigenera con `swift mac/make-icon.swift <logo.png> <cartella.iconset>` e `iconutil`.
