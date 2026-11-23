# Piano di lavoro — app nativa Cinecore per Mac

Riferimento: le funzioni di Windows (Beta 3.6, `FUNZIONALITA.md`) confrontate con l'app nativa (`mac-native/CinecoreMac`).
Aggiornato: 15/11/2026. Si segue l'ordine della sezione **Ordine di lavoro**; ogni voce chiusa passa in **Fatto** con la data.

Legenda: ✅ fatto · 🟡 parziale · ❌ manca · ⏸ aspetta l'ok dell'utente (tocca il motore video) · 🚫 non serve sul Mac

Regola fissa: **la riproduzione video non si tocca** senza ok esplicito (VideoHost, MpvEngine, renderer, Wine).
Le prove si fanno sullo schermo virtuale "Cinecore Test", con impostazioni di prova, e si pulisce cio' che finisce negli archivi veri.

---

## 1. Cosa riproduce

| Funzione | Stato | Note |
|---|---|---|
| Video locali | ✅ | madVR / MPC-VR sotto Wine, mpv nativo |
| Musica locale | ✅ | Cinecore Audio Engine Mac (FFmpeg + DSP di Windows + AudioQueue), AVPlayer come riserva |
| Foto | 🟡 | visualizzatore, zoom, rotazione, presentazione; manca modifica e azioni (§11) |
| Server di rete DLNA, Jellyfin, Emby | ✅ | |
| Dischi: DVD, Blu-ray, UHD (cartella, ISO, unita') | 🟡 | DVD ✅ con menu (libdvdnav) su tutti i motori, provato col lettore vero; Blu-ray/UHD: codice fatto (mpv con libbluray, madVR/MPC-VR con LAV), da provare con un disco vero |
| CD audio | ✅ | provato con un CD vero il 14/11 (Depeche Mode, riconosciuto, gapless fra le tracce, uscita vera in cuffia) |
| Ripresa dei dischi (Riprendi / Ricomincia) | ✅ | DVD; Blu-ray con i Blu-ray |
| YouTube (yt-dlp, limite di risoluzione) | ✅ | mpv apre gli URL con yt-dlp; voce "Risoluzione web" nel menu |
| Apri con / doppio clic / trascinamento | ✅ | trascinamento sulla finestra c'e'; da verificare "Apri con" del Finder e file passato all'app gia' aperta |
| Schermate di caricamento | ✅ | LoadingView |
| Ritaglio del video | ✅ | serve un comando nel motore |

## 2. Motori video

| Funzione | Stato | Note |
|---|---|---|
| madVR, MPC-VR, mpv, Auto | ✅ | |
| Statistiche madVR / mpv nel menu | ✅ | |
| Statistiche MPC-VR nel menu | ✅ | comando `mpcvr-stats` nel VideoHost (IExFilterConfig statsEnable) |
| Profili HDR madVR nel menu | ✅ | |
| HDR: passthrough, RTX Video HDR, HDR→SDR shader/3DLUT | ✅ | Video › Immagine / HDR per i tre renderer; 3DLUT con madVR (toneMap3dlut, 15/11); RTX 🚫 sul Mac |
| Upscaling (madVR) dal menu | ✅ | impostazione di madVR dal vivo (comando gia' esistente `madvr k=v`) |
| Cambio frequenza dello schermo | ✅ | prima dell'avvio del motore (MacDisplayRate) |
| 3D (SBS/TAB → 2D, nativo, esteso) | ✅ | tutti i renderer; madVR: niente GetPreferredAspectRatio (crash nativo sotto Wine, 21/11) |
| Pagine componenti (madVR, LAV, MPC-VR, XySubFilter, mpv; MPC Audio Renderer tolto il 22/11: sul Mac non serve) | ✅ | |

## 3. Immagine a schermo intero

| Funzione | Stato | Note |
|---|---|---|
| Dimensionamento (Z), bande nere, scene IMAX, transizione | ⏸ | tocca la resa del video |
| Avvio a schermo intero | ✅ | |
| Schermo intero di sistema | ✅ | |

## 4. Controlli durante la riproduzione

| Funzione | Stato | Note |
|---|---|---|
| HUD (timeline con anteprima, capitoli, volume, ±10 s) | ✅ | |
| Cast, Qualita', Salta intro / prossimo episodio | ✅ | |
| Scan veloce (x0,5 … x4) | ✅ | serve velocita' di riproduzione nel motore |
| Passo singolo `,` `.` | ✅ | |
| Riprendi da dove eri, Continua a guardare | ✅ | |
| Volume per film | ✅ | |
| Amplificazione oltre il 100% (+12 dB) | ✅ | 15/11: con tutti i motori (madVR/MPC-VR passano all'audio nativo affiancato); non con dischi e traccia esterna sotto madVR/MPC-VR |
| Pannello Info | ✅ | |
| Modalita' PiP del video | ✅ | finestra video piccola sempre in primo piano |
| Menu tasto destro | 🟡 | mancano: Apri disco, Video › Immagine/HDR completo, 3D, Ritaglio, Risoluzione web, PiP; Dispositivi › Amplificatore, Automazioni, WLED; Strumenti › Aggiornamenti |
| Tasti multimediali e scorciatoie | ✅ | 15/11: scorciatoie di §16 verificate; tasti multimediali del Mac e In riproduzione (MacMediaKeys.cs) |

## 5. Audio dei film

| Funzione | Stato | Note |
|---|---|---|
| Uscita audio e dispositivo | ✅ | |
| Bitstream / PCM forzato | 🟡 | 18/11: con tutti i motori (madVR/MPC-VR con l'audio nativo affiancato); uscita HDMI che dichiara solo PCM (adattatore USB-C) = bitstream dentro il PCM, formato per formato dopo la Prova in Impostazioni › Riproduzione; da provare col Marantz |
| Tracce audio | ✅ | |
| Ritardo audio (menu, Ctrl ±, salvato per film) | ✅ | 13/11 |
| Sincronizzazione automatica fra tracce interne, ritardo variabile | ✅ | 13/11 |
| Traccia audio esterna + conversione di velocita' | ✅ | carica un file nel motore |
| Analisi audio in tempo reale dei film | ❌ | |

## 6. Sottotitoli

| Funzione | Stato | Note |
|---|---|---|
| Tracce interne ed esterne, solo forzati | ✅ | |
| Ricerca e scaricamento (YIFY, Addic7ed, Subdl, OpenSubtitles) | ✅ | |
| Riallineamento sull'audio | ✅ | |
| Stile del testo e posizione | ❌ | pagina Impostazioni › Sottotitoli: verificare cosa manca rispetto a Windows |

## 7. Libreria

| Funzione | Stato | Note |
|---|---|---|
| Home, Film, Serie, Musica, Foto, Video, Playlist, Rete, Preferiti, Diario | ✅ | |
| Cartelle sorgenti | ✅ | |
| Metadati TMDb, correzione manuale | ✅ | |
| Scheda del titolo | ✅ | 13/11: Panoramica, Dettagli, Recensioni (voti TMDb, IMDb, Letterboxd, Metacritic; pubblico/critica; fonti), Cast |
| Raccolte, filtri e ordinamenti (genere, anno, decennio, regista, artista, cartella) | ✅ | 13/11: scelte rapide per sezione (Lossless, Atmos, Valutati, Classifica…) e sottomenu come Windows |
| Ricerca globale | ✅ | |
| Rapporto della libreria | ✅ | 13/11 |
| Tema, accento, sfondo | ✅ | 13/11: anche la dissolvenza al cambio |

## 8. Spotlight e modalita' cinema

| Funzione | Stato | Note |
|---|---|---|
| Spotlight (sala, cast, ricerca, all'avvio) | 🟡 | da confrontare: schermata d'ingresso, ordine dei titoli iniziati, pagina vuota |
| Modalita' cinema: schermata prima del film (e in pausa) | ✅ | 13/11: CinemaGateView; il film si apre solo con "Avvia", provati schermo intero, Impostazioni, annulla e riapertura |
| Demo prima del film | ✅ | 13/11: cartella Demos in Application Support; chiudendo la demo parte il film |

## 9. Rete

| Funzione | Stato | Note |
|---|---|---|
| DLNA, Jellyfin, Emby, Quick Connect, qualita' ridotta, token cifrati | ✅ | |
| Plex (via DLNA, come Windows) e dischi rclone / di rete | ✅ | 15/11: Plex riconosciuto e ordinato con Jellyfin; dischi lenti (MacStorage): anteprima a un fotogramma, controllo dei file spariti in sottofondo |

## 10. Musica

| Funzione | Stato | Note |
|---|---|---|
| Album, scheda album, coda, casuale, ripeti, preferiti | ✅ | |
| Barra in vetro vero (Liquid Glass / NSVisualEffectView) | ✅ | 13/11 |
| Brano a schermo intero, informazioni, mini player | ✅ | 13/11 |
| Testi: LRCLIB/Genius/lyrics.ovh, sincronizzati | ✅ | 13/11 |
| Sincronizzazione vocale dei testi (faster-whisper, GPU) | ✅ | 13/11 |
| Pagina Musica come Windows: banner artisti a rotazione, Continua ad ascoltare, file Album/Artisti, griglie Album/Artisti/Brani, scheda artista, foto artisti (Spotify, TIDAL, Deezer, cartella) | ✅ | 13/11 |
| Radio (brani simili a fine coda) | ✅ | 13/11: stesso punteggio di Windows, voce nel menu "altro" della barra |
| Avanzamento dell'album ricordato | ✅ | 13/11: MusicListeningStore condiviso, "Continua ad ascoltare" |
| Ascolti verso Last.fm e ListenBrainz | ✅ | 13/11: scheda Ascolti, conteggio come Windows (meta' brano o 4 min), coda se manca la rete |
| Grafici audio, VU meter | ✅ | Spettro, Spettrogramma, Onda, Loudness, Stereo, Livelli, VU |
| Cinecore Audio Engine: EQ, limitatore, crossfeed, ReplayGain, gapless, dissolvenza, Automix, esclusivo/bit-perfect | 🟡 | fatto tutto, Automix compreso (anche a tempo con Rubber Band); esclusivo/bit-perfect e editor del mix manuale fatti il 15/11 (provati in uscita muta); uscita vera da provare con l'utente |

## 11. Foto

| Funzione | Stato | Note |
|---|---|---|
| Visualizzatore, zoom, rotazione, presentazione, barra degli strumenti e miniature | ✅ | 13/11: barra come Windows |
| Modifica (penna, forme, testo, filtri, annulla; salva una copia) | ✅ | 13/11: PhotoEditView (Avalonia a schermo, SkiaSharp nel file) |
| Azioni: copia, condividi (pannello di macOS), stampa, apri con, Cestino | ✅ | 13/11: MacFileActions (NSSharingServicePicker, NSPasteboard, Launch Services, Cestino) |

## 12. Analisi HDR

| Funzione | Stato | Note |
|---|---|---|
| Metadati dichiarati | ✅ | ffprobe (anche HDR10+ e MaxCLL del primo fotogramma) |
| Misura reale sul film, grafici, esportazione | ✅ | HdrAnalyzer.Mac.cs: stessi conti di Windows, fotogrammi da ffmpeg; cache JSON compatibile; HTML/PNG/CSV/JSON |
| Tempo reale (forma d'onda, vettorscopio, CIE, falsi colori, ultimo minuto) | ✅ | in pausa il fotogramma esatto, in riproduzione il fotogramma chiave |

## 13. Telecomando dal telefono

| Funzione | Stato | Note |
|---|---|---|
| Abbinamento QR, comandi, tracce, libreria e coda | ✅ | |
| Cambia indirizzo (nome del Mac / IP) | ✅ | 13/11 |
| Impostazioni dal telefono e personalizzazione | 🟡 | da verificare voce per voce |

## 14. Dispositivi di casa

| Funzione | Stato | Note |
|---|---|---|
| Amplificatore via rete (regole di sicurezza del volume!) | 🟡 | fatto (15/11) con le regole di Windows, provato con un finto Denon; da provare con il Marantz insieme all'utente |
| Luci WLED | ✅ | 14/11 |
| Automazioni (HTTP, MQTT) | ✅ | 15/11, password nel Portachiavi |

## 15. Servizi collegati e aggiornamenti

| Funzione | Stato | Note |
|---|---|---|
| Trakt (codice, consigli, invio visti e voti) | ✅ | 13/11: pagina "Consigliati per te", diario e segna-visto a fine film; la parte con account va provata dall'utente |
| Aggiornamenti del player | ✅ | 15/11: release GitHub di CinecorePlayer/CinecorePlayer, zip "Mac" (mac/make-mac-release.sh). 18/11: tag come Windows, "beta-v0.1.0-Avalonia-MacOs" (release Beta 1 creata dall'utente, ancora senza allegato); l'app considera solo i tag con "mac" |
| Componenti esterni (versioni) | 🟡 | pagine componenti si'; versioni/aggiornamenti: sul Mac i componenti sono altri (ffmpeg, libmpv, yt-dlp, Wine): si fa insieme a YouTube |
| Apri all'avvio del Mac | ✅ | |
| Inglese e spagnolo | ✅ | 15/11: Impostazioni › Generali › Lingua; traduzione al disegno (L10n.cs, Lang/strings.tsv), metadati TMDb nella lingua scelta |
| Chiavi e token cifrati | ✅ | archivio locale cifrato (scelta dell'utente: niente Portachiavi) |

## 16. Impostazioni (pagina)

| Scheda | Stato | Note |
|---|---|---|
| Generali | ✅ | 13/11: banner foto, Fonte Spotlight e server, schede Spotify e TIDAL (verifica TIDAL), Fonte per la musica. Lingua con la voce 11, Componenti esterni con la voce 8 |
| MPV, Sottotitoli, Dispositivi | ✅ | Dispositivi come Windows (ricevitore, telefono, automazioni, WLED, servizi) |
| Cinecore Audio | ✅ | 15/11: editor grafico della curva EQ (10/31 bande, parametrico) |
| Componenti avanzati | ✅ | |

---

## Ordine di lavoro

1. ~~**Strumenti senza motore**: Trakt, Ascolti (Last.fm, ListenBrainz)~~ fatto 13/11; Componenti esterni spostato con YouTube (voce 8).
2. ~~**Impostazioni › Generali completa**~~ fatto 13/11.
3. ~~**Musica**: pagina Artisti con foto, Radio, avanzamento album~~ fatto 13/11.
4. ~~**Libreria**: Recensioni, Cast, filtri, dissolvenza del tema~~ fatto 13/11.
5. ~~**Foto**: azioni e modifica~~ fatto 13/11.
6. ~~**Modalita' cinema**~~ fatto 13/11.
7. ~~**Analisi HDR**~~ fatto 13/11.
8. ~~**Menu tasto destro**: Loop brano, Upscaling, Componenti esterni~~ fatto 13/11. YouTube e Risoluzione web spostati
   in attesa: YouTube manda video e audio separati e il motore deve poter aggiungere una traccia audio esterna.
9. **Dischi** (CD, DVD, Blu-ray, UHD) a doppia via.
10. **Cinecore Audio Engine** sul Mac (con grafici, VU, gapless, EQ…) e la sua scheda nelle Impostazioni.
11. **Lingue** inglese e spagnolo.
12. **Dispositivi di casa** (dopo aver chiesto all'utente).

Ok dell'utente sul motore arrivato il 13/11. Fatti: statistiche MPC-VR, ritaglio, YouTube / Risoluzione web / indirizzo web,
traccia audio esterna con conversione di velocita', dimensionamento a schermo intero (non con madVR).
Restano: scan veloce, PiP del video, 3D, cambio frequenza, opzioni HDR dei renderer, amplificazione con LAV Audio,
pagina YouTube con ricerca nella libreria, poi i dischi (voce 9).

## Problemi noti della versione Windows (da NON portare; verificare sul Mac)

Segnalati dall'utente il 13/11. Ogni voce tradotta va controllata contro questo elenco.

| Problema su Windows | Sul Mac |
|---|---|
| Schermata TMDb prima del film: uscendo dallo schermo intero resta lo schermo nero | ✅ evitato e provato (13/11) |
| Senza internet la schermata di caricamento lascia pezzi neri invece di un errore | ✅ la schermata prima del film ha sempre il fondo neutro; la schermata di caricamento: da verificare |
| Chiudendo un film dalla schermata TMDb senza avviarlo, la volta dopo parte senza schermata | ✅ evitato e provato |
| Rientrando in libreria dopo aver annullato dalla schermata TMDb si vede la libreria ricomporsi | ✅ evitato e provato |
| Dalla schermata TMDb, Impostazioni e ritorno: schermo nero fisso | ✅ evitato e provato |
| A 24 fps l'HUD scorrendo la libreria ha tearing e animazioni a scatti | da verificare |
| Animazioni che partono quando vogliono | da verificare |
| Vari rallentamenti di HUD e libreria (troppi ridisegni) | da verificare |
| Telecomando › Sincronizza audio: un riflesso da togliere e la foto non a tutta larghezza | da verificare quando si tocca il telecomando |
| Dopo molto tempo in pausa si ricarica tutto (perde sottotitoli e preferenze) | da verificare (riguarda il motore: solo segnalare) |
| Metadati in ritardo o mancanti nella scheda del film (cast, regia, consigli) | da verificare |
| Impostazioni: aprendo i menu in alto c'e' un artefatto con la vignettatura | da verificare |

## Fatto (registro)

- 13/11 — Impostazioni: banner con la foto dell'ultimo film.
- 13/11 — Musica: barra in vetro vero con tutti i tasti, brano a schermo intero, informazioni, mini player, testi sincronizzati, sincronizzazione vocale su GPU.
- 13/11 — Menu: Cambia indirizzo del telefono, Qualita' Jellyfin ed Emby in Video, Strumenti › Rapporto della libreria.
- 13/11 — Audio: ritardo salvato per film, scheda Sincronizzazione audio con analisi automatica e ritardo variabile.
- 13/11 — Impostazioni › Generali: Fonte Spotlight, Spotify, TIDAL, Fonte per la musica (servizi foto artisti portati con SkiaSharp al posto di GDI).
- 13/11 — Musica: panoramica come Windows (artisti a rotazione, continua ad ascoltare, album, artisti), griglie Album/Artisti/Brani, scheda artista, Radio.
- 13/11 — Libreria: scheda del titolo con Recensioni e Cast, filtri come Windows, dissolvenza del tema.
- 13/11 — Foto: barra degli strumenti e miniature, condividi/copia/apri con/stampa/Finder/Cestino, editor.
- 13/11 — Prima del film: schermata (anche in pausa) e demo, senza i difetti della versione Windows.
- 13/11 — Strumenti: Consigliati per te (Trakt) e Ascolti (Last.fm, ListenBrainz); chiavi dell'app portate dal cifrato di Windows.
- 13/11 — Strumenti › Analisi HDR: scheda come Windows (tempo reale con forma d'onda, cromaticita', vettorscopio, falsi colori, ultimo minuto; film intero con luminosita', distribuzione, colori; esportazione). Il film non rallenta: ffmpeg parte da shell di servizio (ProcessLauncher: il fork dell'app con mpv dentro rallentava la riproduzione) e, con il film che scorre, decodifica in software a priorita' di sottofondo (VideoToolbox condiviso la rallentava del 40%). Verificato: 0 fotogrammi persi, posizione identica alla prova senza analisi.
- 13/11 — Menu: Loop brano (a fine file riparte lo stesso), Video › Upscaling (oltre nativo) come Windows, Strumenti › Componenti esterni (versioni e provenienza di FFmpeg, libmpv, libplacebo, MoltenVK, yt-dlp, runtime Wine; ultima versione da Homebrew; la build scrive Contents/Resources/components.txt). Analisi HDR attiva solo per un video salvato sul Mac.
- 13/11 — Col tuo ok sul motore: Video › Statistiche MPC-VR (statsEnable nel VideoHost), Video › Ritaglio per mpv (video-crop), madVR (sorgente IBasicVideo) e MPC-VR (finestra video ristretta e fotogramma ingrandito). Corretta la posizione della finestra video di Wine sugli schermi a 1x (proiettore, schermo virtuale): la scala ora e' quella di Wine (NSScreen), non quella dello schermo della finestra. VideoHost installato (copia precedente: dist/mac-native/videohost-prima-stats-mpcvr-20261113). Notato: madVR in pausa non ridisegna dopo un cambio di misura (anche solo ridimensionando), torna alla ripresa: da guardare.
- 13/11 — YouTube e video dal web: Apri › Indirizzo web (YouTube)… (incolla dagli appunti), Video › Risoluzione web (YouTube). yt-dlp di Homebrew via ProcessLauncher; l'audio separato va a mpv (audio-add) e al VideoHost (--audio-url, secondo LAV Splitter): verificato con mpv e MPC-VR. Niente Diario ne' ripresa per i video web (indirizzi che scadono). Manca: pagina YouTube con ricerca nella libreria.
- 13/11 — Traccia audio esterna come Windows: nella scheda Sincronizzazione audio "File audio esterno…", analisi contro il film, "Converti la traccia" se la velocita' e' diversa (25/24, 24/23,976: copia convertita e nuova analisi), Applica riapre il film nello stesso punto con la traccia. mpv: audio-add; VideoHost: secondo LAV Splitter (tracce del file mostrate come non attive, voce "Audio esterno"). Scegliendo una traccia del film la si toglie. Ritardo e curva valgono anche per la traccia esterna. Da guardare: con MPC-VR un cambio di traccia normale ferma l'immagine per alcuni secondi (gia' cosi' prima).
- 13/11 — Immagine a schermo intero come Windows (MacImageSizing.cs, MainWindow.ImageSizing.cs): Riempi / Altezza costante / Area costante / Personalizzata (Impostazioni › Generali e tasto Z), bande nere misurate ogni 2 s su un fotogramma piccolo (ffmpeg in sottofondo), scene IMAX, transizione. Motore: "scale" (mpv video-zoom, VideoHost destinazione ridotta). Verificato con mpv e MPC-VR. madVR: sotto Wine presenta sempre a tutta finestra (ne' destinazione ne' setZoomFactorX/Y hanno effetto): per ora spento con avviso.
- 13/11 — Confronto con Windows (menu, Impostazioni, HUD, libreria) e allineamenti: Libreria › Altro › YouTube (ricerca con yt-dlp, miniature) e Apri link; Audio › Preferenza uscita audio (bitstream solo se il dispositivo accetta formati compressi: la prova vera stasera, con il permesso dell'utente); "Audio esterno e sincronizzazione…"; Genius come Windows (WKWebView nascosta quando genius.com risponde 403, Strumenti › Genius: verifica connessione…; mac/MacWebSession.m in libCinecoreGlass.dylib); Video › Modalita' PiP (riquadro 560×180 sempre in primo piano, video a sinistra e comandi a destra; mpv video-margin-ratio-right, VideoHost rettangolo); Video › Immagine / HDR unico per i tre renderer; Video › 3D (Auto da nome/metadati/formato, Nativo, SBS → 2D, Top/Bottom → 2D; con madVR il film passa a MPC-VR, come faceva Windows). Restano: dischi, Dispositivi (luci, automazioni: da chiedere), controllo aggiornamenti dell'app, scan veloce, cambio frequenza, LAV amplificazione, schermo intero esteso 3D.
- 13/11 — Scan veloce come Windows (pressione lunga su -10/+10, telefono scan_back/scan_fwd; salti continui x0,5…x4, vale con tutti i renderer). Amplificazione oltre il 100% fino a +12 dB con limitatore (solo MPV, come Windows). Dischi, architettura come Windows (MacDisc.cs, MacDiscStream.cs, MainWindow.Disc.cs): cartella, ISO (hdiutil, sola lettura, smontata dopo), dischi inseriti in /Volumes; Apri › Apri disco, Titoli del disco (DVD: gruppi VTS con durata; Blu-ray: playlist .mpls), Libreria › Altro con disco ed Espelli, Impostazioni › Lettura dei DVD e Blu-ray protetti (KEYDB.cfg copiato per libaacs del Mac e di Wine). Vie: MPV (bd:// con bluray-device, DVD con lavf://concat dei VOB), VideoHost (index.bdmv o .mpls con LAV; DVD con i VOB uniti da un server locale 127.0.0.1 con Range, perche' LAV apre un VOB alla volta). Verificato con un DVD sintetico (cartella e ISO, uno e due VOB, salti) con mpv, MPC-VR e madVR. Da fare stasera con il lettore vero; menu del DVD non ancora (navigatore); Blu-ray non ancora provati (serve materiale).
- 13/11 — Scheda del titolo: "Metti in coda" e "Aggiungi a una playlist" accanto alla stella (DetailHeader di Windows). Diario: voto personale a 5 stelle (mezze stelle) sotto ogni titolo, WatchHistoryStore.SetRating come Windows.

### Ancora aperto dopo il confronto del 13/11 (testi e moduli di Windows)
- Creazione playlist guidata come Windows (nome, copertina, contenuto, descrizione) e coda unica con pannello (cronologia / in riproduzione / in arrivo, "Riproduci ora", "Svuota coda").
- Album: "Potrebbe piacerti" (consigli); scheda: "In evidenza", troupe (regia, sceneggiatura) nel Cast, "Leggi tutto" nelle recensioni.
- Ordinamenti "Più recenti / Meno recenti / Nome A–Z" da verificare; ripristino dei titoli nascosti.
- Rete: disco di rete rclone, server Plex.
- "Schermo intero in finestra" (impostazione), "Apri con" dal Finder e file aperti con l'app gia' avviata.
- Sottotitoli: stile del testo e posizione; pagina Impostazioni › Sottotitoli da confrontare.
- Telecomando: impostazioni dal telefono da verificare voce per voce; Spotlight da confrontare.
- Cinecore Audio Engine (EQ, gapless, crossfade, Automix/mix manuale, grafici, VU), analisi audio dei film in tempo reale.
- Dispositivi (ricevitore, WLED, automazioni): chiedere. Aggiornamenti dell'app: canale di rilascio. Lingue EN/ES.
- Con lo schermo vero: cambio frequenza, schermo intero esteso 3D. Con madVR: 3D e dimensionamento.
- Stasera: DVD con il lettore, bitstream.
- 13/11 sera — Creazione playlist con copertina facoltativa; scheda album con "Potrebbe piacerti" (LastFmSimilar compilato sul Mac): compila, NON ancora visto a schermo (la libreria vera non ha musica: provarlo con la musica di prova).
- 13/11 sera — Cambio di motore: il motore non cambia MAI da solo. Con madVR in risparmio energetico compare un avviso nell'HUD (in basso a destra) con "Passa a MPV", "Passa a MPC-VR", "Resta su madVR"; la scelta vale finche' dura il risparmio energetico nella sessione (MacSettings.LowPowerSessionRenderer), la preferenza salvata resta. Impostazioni › madVR in risparmio energetico: Chiedi / Profilo leggero / Non chiedere (i vecchi valori "Passa a…" diventano Chiedi). La spunta nel menu segna il motore che gira davvero. Verificato MPV → madVR → MPC-VR → MPV con ripresa dallo stesso punto, e l'avviso con madVR.
- 13/11 sera — Menu tasto destro › Vai a… (GoToTimeView): 1:20:00, 80:00, 80 (minuti), 1h20, 2m, 50%, +5m/-30s; oltre la durata avvisa. Comandi di prova: goto, renderer-choose.
- 13/11 notte — 3D e dimensionamento con TUTTI i renderer: madVR sotto Wine applica la destinazione ma ignora la sorgente, e dichiara la misura gia' corretta per il formato; il VideoHost ora usa la misura vera del fotogramma e, con madVR, il fotogramma ingrandito oltre la finestra (occhio sinistro a tutto schermo, ritaglio, scala). Niente piu' passaggio a MPC-VR per i film 3D, dimensionamento acceso anche con madVR. Verificato SBS → 2D con MPV, MPC-VR e madVR. Prestazioni invariate (madVR ~1-2 persi/s in risparmio energetico prima e dopo; MPC-VR 24/24, 0 persi).
- 13/11 notte — Pausa nera con madVR: succedeva con le statistiche Ctrl+J accese (madVR in pausa ridisegna di continuo e il ponte consegna nero). Il VideoHost legge lo stato salvato (HKCU\Software\madshi\madVR ShowOsd_), le nasconde durante la pausa e le rimette alla ripresa. Statistiche del renderer anche al primo livello del menu.
- 13/11 notte — 3D › Schermo intero esteso (Maiusc+F), come Windows: la finestra copre tutti gli schermi e il fotogramma 3D si stira (comando "stretch" in VideoHost e mpv). Su macOS funziona davvero solo con "Gli schermi usano spazi separati" spento (avviso a schermo).
- 13/11 notte — Bitstream con madVR e MPC-VR: con "Auto (bitstream se conviene)" e un'uscita che accetta Dolby/DTS il VideoHost tiene solo il video (--no-audio) e l'audio lo suona una libmpv senza video (MpvAudioSidecar.cs) con lo stesso bitstream del motore MPV; segue l'orologio del VideoHost (velocita' +-0,5% in PCM, salto oltre 80 ms in bitstream), piu' il ritardo del ponte madVR ("audiodelay"). Verificato in PCM (CINECORE_QA_SIDECAR=1): scarto entro 15 ms con MPC-VR e madVR dopo salti, pausa e ritardo. Il bitstream vero si verifica solo con un ricevitore collegato (ora il Mac ha solo gli altoparlanti).
- 13/11 notte — Cambio frequenza dello schermo (MacDisplayRate.cs, Impostazioni › Riproduzione › Frequenza dello schermo, acceso di serie): regola di Windows (multiplo esatto piu' alto senza superare la frequenza attuale; 24 fps -> 48 Hz se c'e', altrimenti 24; 23,976 solo se lo schermo la offre), ripristino a chiusura del film e dell'app. Si cambia PRIMA di avviare il motore (con madVR un cambio a film avviato fermava l'immagine); a film avviato solo con MPV. Il VideoHost riceve la frequenza vera dello schermo del film (CINECORE_REFRESH_HZ): prima leggeva quella dello schermo principale. Provato su schermo virtuale 60/50/48 e 60/24 Hz. Lo schermo del MacBook Air ha solo 60 Hz.
- 13/11 notte — DVD vero (lettore HP collegato dall'utente, disco "FILM_BLU" in chiaro, 16:9 anamorfico): parte con MPV, MPC-VR e madVR, salti ok. Corretto MPC-VR che mostrava i video anamorfici schiacciati (proporzioni lette da VIDEOINFOHEADER2; chiedere IBasicVideo2 al grafo durante la costruzione va in ricorsione infinita). DVD protetti (CSS): rilevati dal bit di cifratura dei pacchetti, letti con libdvdread + libdvdcss (MacDvdRead.cs) e serviti dal server locale a tutti i motori (provato forzando la via su questo disco: ok con i tre motori, ~4 s in piu' all'avvio, per questo solo con i dischi cifrati). libdvdread e libdvdcss incluse nel pacchetto. MANCANO: menu dei DVD (serve un navigatore con libdvdnav), Blu-ray con MakeMKV (libmmbd).
- 14/11 — Menu dei DVD con libdvdnav (MacDvdNav.cs, DvdMenuView.cs): il disco si suona come un lettore (avvertenze, menu animati, film); il navigatore produce un MPEG-PS con i tempi riscritti (sempre crescenti) servito da MacDiscStream (/dvdnav/<gettone>/<generazione>.mpg, diretta senza lunghezza); a ogni salto (pulsante, menu, ricerca, capitolo) nuova generazione e il motore ricarica. Pulsanti e evidenziazione (sottoimmagine del disco + tavolozza) disegnati dall'app sopra il video, sincronizzati sulla posizione del motore; i comandi usano i dati dei pulsanti del VOBU mostrato (il navigatore legge avanti). Frecce, Invio, clic e passaggio del mouse; menu tasto destro › Riproduzione › Menu del DVD. Audio: passa solo la traccia scelta dal disco. Dischi in chiaro aperti da un collegamento a VIDEO_TS (niente chiavi CSS: apertura da 8 s a 0,02 s). Verificato con MPV su FILM_BLU: intro -> menu -> Film (titolo 2, 94 min, tempo giusto). Con madVR / MPC-VR: menu, avvertenze e titoli brevi con MPV, il film (titolo >= 5 min) passa al motore scelto che legge il gruppo VTS (VideoHostPathForTitleSet) dal punto d'inizio del tratto; Menu del DVD e fine del film tornano ai menu con MPV (il navigatore si porta prima al punto del film, cosi' 'Riprendi' del disco torna li'). Verificato con madVR e MPC-VR (menu -> film in 3-4 s, salto a 600/1200 s, ritorno al menu Scene). Tasti: M menu del disco, Backspace / Indietro del telefono = menu precedente; clic e mouse sui pulsanti verificati. Impostazioni › Riproduzione › DVD: Con i menu / Dritto al film. Sottotitoli del film con MPV: tavolozza del disco (sub-lavc-o palette). MANCA: ripresa del punto per i DVD.
- 14/11 — Menu tasto destro: primo livello corto (Riproduzione raccoglie Vai a, Capitoli, Menu/Titoli del disco, Coda, Loop, Prima del film); "mira" verso il sottomenu aperto (le voci attraversate in diagonale non lo cambiano).
- 14/11 — Crash del VideoHost all'avvio (NSException "insertObject:atIndex: object cannot be nil" nel driver Mac di Wine, ripetuto a ogni tentativo finche' l'app non veniva riaperta): causa NON trovata (nessun rapporto di crash, stack vuoto). Rimedio: se madVR/MPC-VR cadono durante l'avvio l'app riavvia il server di Wine e riprova una volta. Prossimo passo: gestore delle eccezioni in winemac che scriva lo stack.
- 14/11 — Avviso madVR in risparmio energetico come scheda nello stile delle finestre dell'app; statistiche madVR chieste in pausa: compaiono alla ripresa (prima: nero); nomi dei dischi come su Windows ("FILM_BLU" -> "Film Blu").
- 14/11 notte — MPV su un altro schermo: senza l'opzione screen mpv tiene la sua finestra sullo schermo principale e AppKit la riporta li' a ogni spostamento (il video finiva su un altro schermo / scrivania). Ora screen e fs-screen = schermo del player. Scadenza d'apertura dei dischi 45 s (lettore fermo che riprende i giri). Motore che gira davvero (Engine) usato nelle scelte tecniche al posto della preferenza.
- 14/11 notte — winemac ARM64: registro delle eccezioni Objective-C (patch 0018), per trovare il crash "insertObject:atIndex: object cannot be nil".
- 14/11 notte — Ripresa dei DVD: alla chiusura titolo e punto del film (solo film >= 20 min, non nei primi 60 s ne' negli ultimi 3 min) per disco (etichetta + misura degli IFO, MacSettings "dvdResume:..."); alla riapertura scheda "Riprendi il film / Dal menu" (HudView.ShowChoicePrompt, stesso stile dell'avviso madVR). Riprendi = dvdnav_title_play + ricerca per tempo; con madVR / MPC-VR il film passa poi al motore scelto. Verificato con MPV: 25:30 ripreso.
- 14/11 notte — Apri con dal Finder / doppio clic / trascinamento (anche ad app aperta, piu' file insieme): video nel player con gli altri in coda, musica in Musica con i tag (e in coda), foto nel visualizzatore. Info.plist: video (anche mkv, webm, m2ts, ts, avi, iso, vob), musica e foto (queste due come "Alternate"). "Schermo intero in finestra" di Windows: 🚫 sul Mac (imita lo schermo intero del Mac). Sottotitoli stile/posizione: gia' presenti (MPV e XySubFilter con "Sottotitoli nelle bande").
- 14/11 notte — Libreria come Windows: ordinamento con menu e spunta (Foto: Più recenti / Nome A–Z / Meno recenti / Dimensione, con le sezioni per mese al contrario per "Meno recenti"; anche in Video e Musica); recensioni a 8 righe con "Leggi tutto ›" e lettura intera con "‹ Tutte le recensioni"; Troupe (regia, sceneggiatura, fotografia, musiche, montaggio con i volti, da TMDb con cache) sopra il Cast; coda della musica con cronologia (ultimi 2, attenuati), brano in corso evidenziato, × per togliere, "Svuota coda". Ripristino dei titoli nascosti c'era gia' (Opzioni › Elementi nascosti).
- 14/11 notte — Cinecore Audio Engine sul Mac (MusicEngine/MacMusicEngine.cs): la musica non passa piu' da AVPlayer. FFmpeg decodifica in float stereo 48 kHz (ricampionatore di FFmpeg a filtro lungo: soxr non c'e' nel FFmpeg di Homebrew), la catena DSP di Windows (Audio/CinecoreAudioDsp.cs collegato cosi' com'e', DynamicEq e impostazioni copiati per il Mac senza NAudio) in doppia precisione, uscita CoreAudio AudioQueue sul dispositivo scelto. Brano successivo preparato 15 s prima: gapless e dissolvenza incrociata a potenza costante; la coda segue il motore quando passa da solo al brano dopo. ReplayGain (brano/album) dai tag con ffprobe. Impostazioni › Cinecore Audio: EQ 10 bande con preset di Windows, preamp, protezione (limiter/margine), ReplayGain, loudness, AutoEQ dinamico, gapless, dissolvenza, crossfeed, bilanciamento, ampiezza, mono. Prove con uscita finta (CINECORE_QA_MUTE, mai audio vero) e file di impostazioni di prova: posizione, pausa, salto, gapless, dissolvenza 6 s, preamp -12 dB misurato sul VU. VU meter analogici L/R (balistica IEC, taratura automatica, spia di picco) nella vista Informazioni del brano a schermo intero. Riserva: se AudioQueue non parte, o con Impostazioni › Cinecore Audio › Motore = Lettore di sistema, si torna ad AVPlayer. ATTENZIONE: l'uscita CoreAudio vera NON e' stata provata (regola: niente audio vero nelle prove, l'utente registra); provata solo l'uscita finta. Grafici nella vista Informazioni del brano (schede Informazioni / Spettro / Loudness / Livelli / VU, numeri LUFS integrato, true peak, gamma, PLR): analisi di Windows (LoopbackSampler, copia Mac senza WASAPI con FFT propria) alimentata dal motore su un thread a parte, solo con i grafici aperti; verificata: -25,4 LUFS come volumedetect di FFmpeg. EQ: 10 bande, 31 bande (si parte dalla curva a 10 portata sui terzi d'ottava) e parametrico (5 filtri: tipo, frequenza, guadagno, Q) nelle Impostazioni. Schede come Windows: Informazioni, Spettro, Spettrogramma (14 s), Onda (15 s), Loudness, Stereo (goniometro, correlazione, bilanciamento), Livelli, VU. MANCANO: l'editor grafico della curva, esclusivo/bit-perfect, Automix.
- 14/11 notte — CD audio (MacAudioCd.cs): macOS li monta (cddafs) con le tracce AIFF e .TOC.plist; impronta MusicBrainz calcolata come Windows (verificata sull'esempio ufficiale: 49HHV7Eb8UKF3aQiNmu1GR8vKTY-), album/artista/anno/titoli e copertina (Cover Art Archive) in cache ~/Library/Caches/Cinecore/audio-cd; le tracce suonano in Musica con il Cinecore Audio Engine. Menu Apri disco e menu dei dischi della libreria: "CD audio · …" ed "Espelli CD". Provato con un volume finto con la stessa tabella (riconosciuto, copertina, riproduzione); con un CD vero: da verificare se cddafs da' i blocchi da 0 o da 150 (gestiti entrambi).
- 14/11 — Automix a tempo sul Mac ("Sincronizzazione tempo" in Impostazioni › Cinecore Audio, come Windows): il brano che entra passa da Rubber Band 4 (RubberBandStretcher condiviso; libreria nel pacchetto in Contents/Frameworks, riserva WSOLA interno) per tutto il passaggio, allineato al campione sul "uno" del brano che esce (scarto/attesa), agganciato battuta per battuta (correzione max ±1,2% ogni 0,2 s) e poi riportato al suo tempo con una curva dolce (8-32 s). Posizione e barra del tempo seguono la velocita' vera (segni con velocita'). Verifica della fase sulle casse (AutomixPhaseAligner) in sottofondo. Provato con uscita finta su due brani sintetici 120 → 123 BPM: "cued 8 bars build", tempo 0,976x, allineamento 0 ms, brano nuovo a 0,975 s/s durante il passaggio e poi 1,0. MANCA: editor del mix manuale (ManualMixForm), esclusivo/bit-perfect.
- 14/11 — CD vero (Sounds of the Universe): cddafs da' i blocchi da 150 (gestito). Due difetti trovati e corretti: plutil non converte in JSON il .TOC.plist intero (campo binario "Format 0x02 TOC Data": disco senza nome) -> si estrae solo Sessions; con due ffmpeg sul disco (fine del brano + successivo) il lettore saltava fra due punti e la traccia dopo arrivava con ~20 s di silenzio -> brani del CD con 90 s di anticipo e un solo lettore alla volta (CdBusy). Provato anche sull'uscita vera (cuffie dell'utente, volume app 15%): passaggio fra le tracce senza pausa.
- 14/11 — Grafici del brano a schermo intero rifatti come Windows (MusicFullscreenView.Charts.cs): titolo con freccia e menu a tendina al posto delle schede, ordine Spettro/VU/Spettrogramma/Onda/Loudness/Dinamica (nuovo)/Stereo/Livelli/Info, riga della legenda con lettura dal vivo, quattro numeri sotto tutti i grafici (anche VU), Livelli e Stereo centrati, dati raccolti a ogni misura con le stesse salite/discese. VU come Windows (stampa, scala %, VOLUME UNITS, cappello del perno, PEAK, ombra dell'ago, impilati se la colonna e' stretta, livello dall'analisi).
- 14/11 — Coda come il cassetto di Windows (LibraryView.Queue.cs): tutta altezza a destra, X e riepilogo, elenco completo con CRONOLOGIA / IN RIPRODUZIONE / IN ARRIVO, barra di scorrimento, rotella, trascinamento per riordinare, doppio clic suona, X su ogni riga, Svuota coda (testo, rosso al passaggio), entra da destra, clic fuori chiude. Sul vetro vero di macOS.
- 14/11 — Barra della musica adattiva: isola quasi a tutta larghezza fino a 1700 punti, passo delle icone 32-40 e volume 90-120 secondo lo spazio, timeline fra brano e comandi; due righe (brano+comandi+icone sopra, timeline sotto) solo se la prima riga ci sta, altrimenti tre. Prima sotto ~1000 punti comandi e icone si sovrapponevano.
- 14/11 — VU ridisegnati puliti e moderni su richiesta dell'utente (da riportare poi anche su Windows): per canale un arco sottile da 240 gradi aperto in basso, livello pieno bianco (rosso sopra 0 VU), tacca del picco che resta 1 s e scende, scala in dB lineare -20…+3 con poche cifre, valore grande al centro, canale e spia di picco come punto. Balistica e taratura invariate. Barra della musica: comandi e timeline al centro esatto dell'isola (timeline simmetrica); passo icone e volume crescono solo oltre 1300 punti.
- 14/11 — Barra della musica: spariva per sempre dopo un menu che la copriva (il vetro nativo, nascosto con orderOut, veniva staccato da macOS dalla finestra e non si riagganciava: ora si nasconde con la trasparenza e si riaggancia da solo, MacLiquidGlass.m). Spotlight, Impostazioni e gli altri livelli a pagina intera si fermano sopra la barra mentre suona la musica (KeepMusicBarClear): barra sempre visibile e usabile. Stella e "…" subito dopo il titolo; sotto 1500 unita' il volume e' solo l'icona e il cursore si apre al passaggio sopra le icone vicine; luce sui bordi come il Liquid Glass di Apple (barra e coda).
- 14/11 — Menu tasto destro: le voci restano al loro posto e sono bloccate finche' non si possono usare (Audio, Loop brano, Coda, Statistiche, Vai a… e Loop anche dentro i menu dei DVD); il grigio delle voci bloccate era quasi uguale a quello normale: ora molto piu' spento. "Vai a…" c'e' (Riproduzione › Vai a…).
- 14/11 — Permesso "Volumi rimovibili" chiesto di continuo: l'app entrava in ogni volume a ogni apertura dei menu per cercare VIDEO_TS/BDMV. Ora i dischi si riconoscono dal filesystem (udf/cd9660/cddafs, senza leggere dentro); si entra solo aprendo il disco. Con la firma ad hoc ogni build e' un'app nuova per macOS: lo script usa "Cinecore Local Signing" se c'e' nel Portachiavi (da creare con Accesso Portachiavi › Assistente certificato, tipo "Firma del codice").
- 14/11 sera — Firma ad hoc con requisito sull'identificativo (com.cinecore.player.native): macOS ricorda i permessi fra le build. Menu del vetro in una finestra figlia sopra barra e coda (non si nascondono piu' aprendo il menu). Menu tasto destro con la musica: Pausa/Interrompi/Audio (uscita, disattiva, Impostazioni Cinecore Audio) attivi, voci da film bloccate. Testi sui CD: titolo/artista/durata dal disco (MusicBrainz) al posto dei tag delle AIFF. VU ad ago moderni; Livelli come Windows piu' la correlazione di fase. Volume a scomparsa sempre nella barra della musica. Coda: durata a destra, X al passaggio. Spotlight: icona Impostazioni, rotella/trackpad accumulati (un titolo per gesto), fila che scorre, dissolvenza solo di sfondo e titolo, sfondi composti in sottofondo con Skia e preparati per i titoli vicini (prima 150-190 ms fermi a ogni titolo, ora nessuno). Ricerca della libreria e di Spotlight su vetro vero (GlassSearchField). Impostazioni › Generali › Interfaccia › Barra in alto (Automatica: piu' grande sugli schermi sotto ~15"). Lettura dei dischi montati senza attese (getfsstat MNT_NOWAIT).
- DA FARE (ultimo, richiesta dell'utente 14/11): bitstream Dolby TrueHD Atmos nativo su CoreAudio (formati non documentati 'mtc+'/'mtct' a 768 kHz, 'cc+3' a 192 kHz per DD+ Atmos, da una prova di un contributore di mpv dell'8/11/2026; patch del backend CoreAudio di mpv non integrata). Prima uno strumento che elenchi AudioStream e AvailablePhysicalFormats dell'uscita HDMI (adattatore USB-C/HDMI dell'utente in arrivo lunedi' 16/11, Marantz SR7010), poi la patch a mpv con IEC 61937; con madVR/MPC-VR l'audio passa gia' dal sidecar mpv nativo (MpvAudioSidecar). Possibile vincolo EDID: il Marantz potrebbe voler un display a valle.
- Rimasti aperti: blocco di ~0,7 s all'avvio (gia' presente prima) e ~0,2 s alla prima creazione del vetro della barra.
- 14/11 tardo — Uscita audio dal menu e cambio del predefinito del Mac anche per la musica (l'AudioQueue si ricrea sul nuovo dispositivo). Menu tasto destro: si apre come su macOS (tutto a sinistra/in alto del puntatore se non ci sta), vetro riordinato solo se serve (spariva per un attimo). (Menu della barra in alto su vetro: ANNULLATO su richiesta dell'utente, restano la lista di testo e la vignettatura come su Windows.) Titolo delle Impostazioni sulla riga dei menu, allineato al contenuto. (Icona con sfondo: ANNULLATA, l'utente vuole il logo originale; mac/make-icon.swift resta come strumento.) README_MAC.md con funzioni e specifiche.
- 14/11 — Verificato con prova muta che Audio › Uscita audio sposta anche la musica (registro: "uscita cambiata: … -> coda su <UID>"; QA audio-out:<UID> e audio-devices). Sottotitoli: stile e posizione c'erano gia' (scheda XySubFilter), voce chiusa. Analisi audio dei film: non esiste nemmeno su Windows (i misuratori si spengono col video), voce chiusa.
- 14/11 sera tardi — Crash del Diario (CultureInfo it-IT con InvariantGlobalization: MacTools.Italian al suo posto, 3 punti) e di "Apri link" (WebAddressView misurata con Bounds vuoto al primo Arrange). Barra della musica: i menu della barra in alto non tolgono piu' il vetro (lampeggiava alla chiusura della vignettatura). Menu tasto destro sui titoli come Windows (TextMenuOverlay: lista di testo nel punto del clic con la pagina velata; sottomenu = seconda lista). Filtri accanto alla ricerca: la casella si restringe invece di toglierli (sparivano con la barra in alto ingrandita). Registro dell'uscita della musica in ~/Library/Logs/Cinecore/musica.log (l'utente dice che resta sulle cuffie; nella prova muta il dispositivo cambia).
- DA CONFRONTARE CON WINDOWS (segnalati dall'utente 14/11): Diario (aspetto), creazione playlist, Foto (pulsanti senza riscontro, tasti che non vanno nella foto singola, selettore colore brutto e senza vetro, altri difetti estetici), pagina YouTube, Cast nella scheda film/serie, Musica (soprattutto i menu di brani e album). Icona nel Finder: riquadro grigio imposto da macOS 26 al logo senza sfondo (spiegato all'utente).
- 14/11 notte — Audio dei film sull'uscita scelta anche con madVR/MPC-VR (sidecar mpv quando c'e' un dispositivo scelto: il DirectSound di Wine suona sempre sul predefinito). Crash madVR "insertObject:atIndex nil": patch 0019 (preprocessore delle eccezioni) per avere lo stack al prossimo caso; 5 avvii di fila senza errori. Menu: voci piu' leggibili sul vetro (testo e velatura). Avviso del renderer in risparmio energetico libero e velato (niente scheda). Icona su quadrato di vetro (scelta dell'utente; originale in mac/Cinecore-logo.icns). Icone Lucide per disco/HDR/nascondi/immagine/upscaling/apri con e per gli strumenti dell'editor foto. Metadati: bordo di fuoco con l'accento (niente anello di Avalonia). Diario: Valuta e Classifica in alto a destra, voto da lista, numero della classifica su vetro, titoli fuori libreria riusati (non perdono i metadati).
- DA FARE (lista dell'utente 14/11 sera): schermata di caricamento del motore con artefatti; menu tasto destro durante il video che non si apre al primo colpo; ruota colori di sistema (NSColorPanel) nelle Impostazioni; lentezza entrando nelle serie; normalizzatore che smette di funzionare dopo un cambio di impostazioni; mpv con schermo nero (2 volte); cambio di renderer che cambia Scrivania; statistiche madVR + pausa che sporcano lo schermo; scorciatoie da verificare tutte; aggiornamenti automatici (non esistono sul Mac); Cast, Musica, creazione playlist, YouTube, Foto da confrontare con Windows.

### 14/11/2026 (pomeriggio)
- Nome del pacchetto: `dist/CinecorePlayere2026.app`; icona tornata al logo originale; versione 0.3.6 (Beta 3.6) presa dal csproj.
- Avviso del cambio di renderer centrato; diario senza "Valuta" accanto alle stelle; velo più scuro sulla scelta del voto.
- Uscita audio dal menu cambiata dal vivo (VideoHostClient.ChangeAudioDevice): mpv e audio nativo affiancato cambiano solo `audio-device`; con l'audio di Wine si accende l'audio nativo al punto in corso e il VideoHost resta muto. Il VideoHost manda sempre `audiodelay` (VideoHost.dll aggiornato, copia precedente in dist/backups). Si riapre il film solo se cambia il bitstream o è un disco.
- Menu tasto destro: ridisegna e rilegge dalla GPU solo il pannello che cambia (cache per pannello in ContextMenuView); Analisi HDR attiva solo per file HDR (PQ, HLG, Dolby Vision dal probe).
- Aggiornamenti: MacUpdate.cs + UpdateView.cs (stile tutorial), Strumenti › Controlla aggiornamenti… e Cerca aggiornamenti all'avvio. **Manca l'indirizzo del canale**: `MacUpdate.DefaultFeed` (release GitHub o JSON {version,url,sha256,notes}); SHA-256 obbligatorio.
- Pulsanti principali bianchi (UiTheme.PrimaryFill/OnPrimary) al posto dell'accento blu: sincronizzazione audio, apri link, vai a, metadati, Trakt.
- Trakt: diario inviato da solo (all'avvio, ogni 10 minuti, all'apertura della pagina) se "Segna su Trakt quello che finisco" è attivo; tolta la scritta "Inviati a Trakt" e la voce del menu.
- Schede con dati in stile tutorial (WizardSheet.cs: velo, vignettatura, colonna, pillola chiara, campi arrotondati senza righe): Nuova playlist a 4 passi come Windows, Apri link, Vai a, Accedi a Jellyfin/Emby, Correggi titolo e copertina.
- DA FARE ancora: menu a tendina rimasti da portare al sistema a testo; ScrobbleView/AudioSync/ricerca sottotitoli nello stile nuovo; voci vecchie (artefatti caricamento, tasto destro durante il video, NSColorPanel, lentezza serie, normalizzatore, nero mpv, cambio Space, statistiche madVR in pausa, scorciatoie, cast/musica/YouTube/foto rispetto a Windows, esclusivo/bit-perfect, editor del mix, dispositivi di casa, lingue).
- 14/11 sera (2) — Scheda album/artista rifatta come Windows (LibraryView.AlbumSheet.cs, da CinematicMediaLibraryPage.AlbumSheet.cs): scheda grande dei film, foto sotto (panoramica dell'artista se c'e', altrimenti il fondo dai colori della copertina) sfumata da sinistra e in basso; ALBUM/RACCOLTA/ARTISTA, titolo, artista · brani, anno/genere (se noti) e formato (FLAC · 96 kHz · kb/s); Riproduci + cerchi Preferiti, Aggiungi alla coda, Aggiungi a playlist, Altro (icone Lucide nuove: star, list-end, list-plus, ellipsis — quelle di Windows erano troppo simili), nome del tasto al passaggio; Tracce con numeri grandi, riga scelta, "…" per brano (Riproduci, Aggiungi alla coda, playlist, tutto l'album in coda, preferiti), brano in corso con le barrette; "Potrebbe piacerti" 3×2 con "Vedi altri". Frecce/Invio (anche dal telefono) scelgono e suonano. Anno e genere: i tag letti sul Mac non li hanno ancora (su Windows arrivano dal catalogo online).
- 14/11 sera (2) — Impostazioni › Dispositivi come Windows (SettingsView.Devices.cs): Amplificatore via rete (AmplifierControl condiviso: stato, Verifica / Cerca in rete, Dispositivo, Protocollo, Indirizzo + Porta, Uscita audio collegata, Scollega; amplifier.json in Application Support), Telefono, Luci WLED, Trakt, Last.fm/ListenBrainz. La pagina LEGGE soltanto: il comando del volume del ricevitore durante il film (PlayerForm.Amplifier.cs con le regole di sicurezza) NON è ancora collegato. Prova col ricevitore vero: da fare con l'utente.
- 14/11 sera (2) — Luci WLED (MacWled.cs + WledView.cs nello stile delle schede nuove): verifica dal vivo mentre si scrive, interruttore; al via del film dissolvenza a spento (4,7 s), in pausa ritorno dopo 2 s, alla fine e all'uscita dall'app tornano com'erano. Verificato con un finto WLED locale.
- 14/11 notte (3) — Richieste dell'utente: barra musica, il quadrato (e le icone vicine) non rispondeva: la zona invisibile del cursore del volume a scomparsa le copriva (ora conta solo da aperto; tutti i tasti provati con la QA bar-click). Vetro nel tema chiaro: il menu tasto destro segue il tema (vetro chiaro, voci scure; CinecoreGlassSetTheme), barra con meno bianco e ombra morbida. Versione Mac = Beta 1 (0.1.0); Impostazioni › Aggiornamenti: "Controlla aggiornamenti" e "Componenti di terze parti › Controlla" (al posto della scritta "Anteprima"). Dispositivi: stato del ricevitore senza puntino, come riga normale; MPV spostato in Componenti avanzati; menu tasto destro › Dispositivi: Amplificatore via rete…, WLED (Abilita controllo LED, Configura dispositivo…). Ricerca della libreria: ordinamento e filtri sotto un solo tasto (icona dei cursori) con la lista di testo, niente icone uguali, casella piu' larga; anche elementi nascosti, playlist della scheda e "tipo" della Musica ora sono liste di testo. Ascolti (Last.fm/ListenBrainz) e Rapporto della libreria nello stile delle schede nuove; Sottotitoli e Sincronizzazione audio senza riquadro e ingrandite con la finestra. YouTube rifatto come Windows: pagina della libreria (LibraryView.YouTube.cs) con la ricerca della barra, In evidenza / Risultati per, Aggiorna, schede con durata, disco di riproduzione al passaggio, canale · visualizzazioni, sagome durante la ricerca.
- Ancora con il menu a tendina in vetro: menu delle foto (Altro, conferma Cestino), Analisi HDR, Trakt, Correggi titolo. Selettore colore: da sostituire con la ruota colori di macOS.

### 15/11/2026
- Menu a tendina rimasti portati alla lista di testo: foto (Altro, conferma Cestino, sopra la barra degli strumenti), Trakt, Analisi HDR, Correggi titolo (TextMenuLayer.cs, lo stesso aspetto dei menu della libreria).
- Selettore colore: la ruota colori di macOS (NSColorPanel, MacColorPanel.cs) per sfondo, accento e colore dell'editor foto; il selettore disegnato resta solo di riserva.
- YouTube: la ricerca riprova da sola (fino a 3 volte, timeout di rete 15 s) e scrive l'errore in app-*.log; anche l'apertura del video riprova una volta. In prova il primo caricamento va (24 risultati in ~2 s, anche dal pacchetto): se ricapita, il registro dice perché.
- Editor del mix manuale come Windows (ManualMixView.cs da ManualMixForm.cs): menu "…" della barra musica › Mix con il prossimo brano…; panoramica, corsie con forme d'onda a due bande, battute, curve, preset e tecniche DJ, 4 selettori, guida "Come si fa un mix", anteprima (MixPreviewPlayer.Mac.cs + MacPcmOutput.cs, sull'uscita della musica). ManualMixStore condiviso; il motore Mac usa il passaggio salvato (MacMusicEngine.ManualMix). Provato muto: passaggio a 86,00 s esatti.
- Automazioni (DeviceAutomation.Mac.cs, AutomationsView.cs, MainWindow.Automations.cs): elenco con interruttori, scheda della regola (Quando, HTTP POST/GET o MQTT, indirizzo, topic, contenuto con {title} {event} {position} {duration}, utente, password nel Portachiavi, solo video, Prova, Elimina). Eventi avvio/pausa/ripresa/fine/titoli di coda per film e musica. Da Impostazioni › Dispositivi e dal menu Dispositivi. Provate con finti server HTTP e MQTT.
- Ricevitore durante il film (MainWindow.Amplifier.cs): stesse regole di Windows (lettura prima di ogni comando, +3 dB al massimo, mai sopra -20 dB, all'avvio solo abbassare all'ultimo volume o a -30 dB, volume per film). Cursore, frecce, telefono, muto. Il volume del motore non viene alzato al 100% come su Windows (nessun salto digitale). Provato con un finto Denon: -20 → -30 dB all'avvio, gesti 50→53→56→59→60, fermo a -20. Da provare sul Marantz con l'utente.
- Uscita esclusiva e Bit perfect per la musica (Impostazioni › Cinecore Audio › Motore): frequenza dell'uscita = quella del file (o un multiplo intero se il dispositivo non la accetta), dispositivo riservato (hog mode, lasciato in pausa e alla fine), frequenza di prima ripristinata; bit perfect senza DSP/ReplayGain/dissolvenze. Cambio di frequenza fra brani con una breve ripartenza. Provato muto (44,1 → 96 → 48 kHz); sull'uscita vera solo con l'utente.
- Prove: ora con HOME separata nello scratchpad (alcuni archivi condivisi con Windows ignorano CINECORE_DATA_DIR: automations, manual-mix, music-listening, posterIndex, automix). Puliti i file di prova finiti in Application Support.
- Tasti multimediali (F7–F9, cuffie, Centro di Controllo) con MPRemoteCommandCenter e "In riproduzione" (MacMediaKeys.cs): film (play/pausa, stop, avanti = coda o +10 s, indietro −10 s, posizione) e musica. Scorciatoie della tabella §16 controllate: ci sono tutte.
- Normalizzazione dei VU (taratura sul LUFS integrato): l'analisi non si azzerava mai e dopo un cambio di brano, salto o impostazione la taratura restava quella vecchia; ora riparte a ogni cambio. Limite della taratura allargato a -34 (brani molto piani). In bit perfect l'analisi riceve il segnale vero.
- Lentezza nelle serie: con dati di prova nessun blocco; corretto un collo di bottiglia (le immagini dal server si scaricavano dentro il semaforo della decodifica: sfondo e locandina aspettavano le miniature degli episodi). Ora l'app registra in app-*.log le attese dell'interfaccia sopra 250 ms con il contesto ("serie: Nome"): se ricapita si vede dove.
- Menu del tasto destro durante il video in finestra: il clic finiva alla finestra del video (mpv o Wine) e il menu si apriva dopo vari tentativi; ora il clic "perso" si recupera anche in finestra, solo se sotto il puntatore c'e' il player.
- Lingue inglese e spagnolo: Impostazioni › Generali › Interfaccia › Lingua (Italiano, English, Español; predefinita quella di macOS). Circa 1.450 testi in mac-native/CinecoreMac/Lang/strings.tsv (italiano, inglese, spagnolo; {0} numeri, {s} parti libere); per lo spagnolo vale anche il dizionario di Windows. I testi delle canzoni non si traducono.
- APERTO: cambio di Scrivania col cambio di renderer (la finestra di mpv nasce prima di essere agganciata: da riprodurre a schermo intero sullo schermo virtuale), statistiche madVR in pausa, artefatti nella schermata di caricamento, schermo nero con mpv (non riprodotti), Sottotitoli e Sincronizzazione audio con l'impaginazione interna vecchia, Cast/Foto da confrontare con Windows; Blu-ray, ricevitore e uscita esclusiva da provare con l'utente; Atmos dopo l'adattatore HDMI.

### 15/11/2026 (seconda parte)
- Sincronizzazione audio e ricerca Sottotitoli con l'impaginazione delle schede nuove: titolo grande a sinistra con una riga di spiegazione, "Annulla"/"Chiudi" solo testo in alto a destra, scelte con il testo a sinistra e le frecce a destra, "Applica" a pillola in basso a sinistra; niente puntini animati (stato in una riga di testo).
- Cast nella scheda film/serie come Windows (Rendering.Cast.cs): "Cast e troupe" con regia e voto TMDb a destra, carosello di ritratti con ombra e sfumatura, nome e personaggio, frecce e puntini della pagina.
- Foto: aprendo una foto la pagina prende il fuoco (i tasti finivano alla casella di ricerca); pulsanti della barra con disco al passaggio e alla pressione.
- Cambio di Scrivania al cambio di renderer: con l'app in primo piano mpv crea e porta davanti la sua finestra prima che il player la agganci; MacSpaceGuard.cs le da' "Spazio attivo + ausiliaria dello schermo intero" prima che compaia (le finestre di Avalonia e dei pannelli di sistema non si toccano). Provato sullo schermo virtuale: lo schermo intero resta nel suo Spazio e il video riparte.
- Statistiche madVR in pausa e artefatti della schermata di caricamento: non riprodotti sullo schermo virtuale (le statistiche di madVR non compaiono nelle catture; la schermata di caricamento e' pulita senza immagini TMDb). Restano da vedere con l'utente, come lo schermo nero con mpv.
- Sospesi su richiesta: Blu-ray e Atmos.

### 15/11/2026 (terza parte)
- Amplificazione oltre il 100% con tutti i motori: con madVR / MPC-VR alla prima pressione oltre il 100% l'audio passa all'mpv nativo affiancato (come il cambio d'uscita a film avviato) con lo stesso filtro di MPV (volume in dB + limitatore). Provato muto con MPC-VR e madVR: +1…+5 dB, scarto 15-38 ms dopo un riallineamento iniziale (~0,5 s, solo all'attivazione). Con dischi o traccia esterna sotto madVR/MPC-VR: avviso "non disponibile con questa sorgente".
- HDR: Video › Immagine / HDR › 3DLUT imposta davvero madVR (hdrMode=toneMap3dlut, come Windows; avviso la prima volta); scelta anche in Componenti › madVR; MPV e MPC-VR la trattano come SDR. Verificato che madVR la accetta (e ripristino del valore dell'utente).
- Editor della curva EQ (SettingsView.EqCurve.cs, da SettingsHudPage.CinecoreAudio di Windows): risposta vera della catena, nodi da trascinare, parametrico anche in frequenza, rotella = Q, doppio clic = nuova banda, margine automatico. Provato in 10 bande e parametrico.
- Anno e genere degli album: letti dai tag (date/year/originaldate, genre; la libreria rilegge i tag una volta) e, se mancano, dal catalogo online (MacAlbumInfo.cs: Spotify/TIDAL se configurati, iTunes; edizioni deluxe/bonus accettate con l'anno dell'uscita originale; cache album-info.json). Anche il filtro per genere ora vale per la musica.
- Rete: Plex (scheda "PLEX" arancione, ordinato con Jellyfin/Emby). Dischi di rete / rclone (MacStorage.cs, getfsstat senza attese): anteprima della timeline a un fotogramma ogni 10 s, file spariti cercati una volta in sottofondo invece che a ogni apertura. Non provato con un montaggio vero (nessuno sul Mac).
- Aggiornamenti: canale = release GitHub di CinecorePlayer/CinecorePlayer (solo allegati .zip/.dmg "Mac" dallo stesso repository, SHA-256 di GitHub obbligatorio); mac/make-mac-release.sh prepara lo zip e stampa il comando gh (non pubblica). Oggi risponde "Sei aggiornato" (nessuna release Mac).
- Prove: .NET su macOS ignora HOME per ApplicationData/LocalApplicationData, quindi le prove scrivevano nei dati veri; ora tutte le cartelle passano da Utilities/AppDataPaths.cs e CINECORE_APPDATA_ROOT le sposta (solo prove). Pulite le cache nate dalle prove (lastfm-similar, foto artista, Genius).
- Da installare: il pacchetto dist/CinecorePlayere2026.app non e' stato rifatto (l'app era aperta): bash mac/build-native-app.sh ad app chiusa.


### 18/11/2026 (notte)
- Bitstream: il MacBook esce in HDMI da un adattatore USB-C generico ("USB Type-C Digital AV Adapter", convertitore MegaChips) che riscrive l'EDID del Marantz: 8 voci audio tutte LPCM, quindi macOS non offre nessun formato compresso (per questo "non funzionava"). Nuova via: IEC 61937 dentro LPCM intero (libmpv con `coreaudio-spdif-hack` riscritto, uscita esclusiva a 16 bit bit-esatta, per TUTTI i formati: AC3/DTS 2ch 48 kHz, E-AC3 2ch 192 kHz, TrueHD/DTS-HD 8ch 192 kHz). Impostazioni › Riproduzione: una riga per formato con Prova (silenzio codificato, l'uscita si apre in pausa e si controlla il formato negoziato prima di mandare dati). Spenti finché l'utente non li attiva. Verificato su file (pacchetti IEC corretti, periodi standard) con MPV, con madVR + audio affiancato (DTS:X → DTS-HD) e con la Prova; NON provato sul Marantz (le prove sull'uscita audio vera sono bloccate di notte): da fare con l'utente, a volume basso.
- Streaming con l'audio affiancato: riapriva lo stesso indirizzo e scaricava due volte tutto il film (Interstellar 4K: riallineamenti continui). Ora in streaming l'audio affiancato si usa solo se serve (bitstream o uscita diversa dalla predefinita); i riallineamenti imparano l'anticipo del salto. Bitstream in streaming: resta il doppio scaricamento (Jellyfin può dare il solo audio originale, /Audio/<id>/stream.mka?audioCodec=copy, ma senza salti per byte: da studiare).
- madVR più veloce all'apertura: VideoHost pronto in anticipo (parte a libreria aperta e dopo ogni film, carica Wine, .NET, Direct3D, ganci e DLL dei filtri e aspetta il film; canale VideoToolbox passato col film), compilato ReadyToRun, grafo avviato in pausa finché madVR ha il primo fotogramma. Banco (1080p HEVC, schermo virtuale, piena potenza): primo fotogramma 3,7-4,1 s → 1,4 s, persi all'avvio 16 → 0; anche MPC-VR 1,5 s. In streaming 4K: 0 persi all'avvio.
- Salti in riproduzione con madVR/MPC-VR: in pausa fino al primo fotogramma, poi Run. Streaming 4K: 60 persi dopo un salto → 0 (pronto in 0,7 s).
- Riduzioni del 12/11 (gruppo di thread di madVR, attesa delle query D3D11, copie evitate) ora attive anche a piena potenza: 4K HDR pesante CPU del VideoHost 95% → 74%, presentazioni a Metal 46/s → 24/s, immagine identica.
- Processo pronto a riposo: 0,6% di CPU, ~390 MB. CINECORE_STANDBY=0 lo spegne, CINECORE_PREROLL=0 / CINECORE_SEEK_CUE=0 tornano all'avvio e ai salti diretti.

### 18/11/2026 — lista dell'utente (mattina)
- [x] Metadati persi quando cade la connessione e non riacquisiti (MacNetwork + richieste ripetute al ritorno).
- [x] Copertina dell'album presa dalla miniatura del video incorporata (Woodkid): immagini non quadrate scartate, come Apple Music/Spotify.
- [x] Musica → altra voce → ritorno: restava su Album senza poter tornare (filtro rapido non azzerato).
- [x] Menu tasto destro › Apri › Libreria tolta.
- [x] Intro/outro detector: impronta Mac rifatta (Haitsma-Kalker, passo 0,128 s, confronto a blocchi), pezzi vicini uniti, capitoli finali usati solo se si chiamano come i titoli di coda, sigla cercata sempre dall'inizio. Prove: 4 stagioni sintetiche (anche volumi/codifiche diversi, senza sigla, con capitoli) e via HTTP come il DLNA: sigla e coda entro ~0,5 s, nessun falso positivo. Profilo mac-pcm-haitsma-v2 (le stagioni gia' analizzate si rifanno).
- [x] Ordine automatico dei brani per l'Automix: menu "…" della barra › Ordina la coda per il mix (come Windows, in piu' misura anche il brano in corso). Provato: 120 → 121 → 127 → 128 → 92 → 90.
- [~] Artefatti al momento del mix: mescolamento verificato su file (nessun vuoto ne' salto, riempimenti 4-24 ms su 43). Trovato e corretto il rilevatore di battiti (condiviso con Windows) che metteva la griglia sui charleston in levare (mezzo battito di errore → doppio colpo fra brani presi uno sul battere e uno sul levare): ora la fase si decide sulle basse, errore < 9 ms. Profili Automix v11 (si rimisurano).
- [x] Mix manuale: righe dei brani a colonne fisse (tonalita', BPM effettivi, posizione, tempo), cursore continuo del tempo del brano che entra (−20%…+8%, Auto, doppio clic = Auto, rotella 0,1%) al posto delle velocita' a scatti; preset su una riga in due gruppi, selettori sotto, pulsanti in basso a destra. Il motore aggancia la battuta anche con il tempo manuale.
- [x] Rallentamenti schermo intero/mini player: la libreria sotto la vista opaca non si ridisegna piu' (foto della pagina per l'uscita). Mini player rifatto: una lastra di Liquid Glass compatta (copertina centrata, brano, timeline, comandi, icone), niente titolo/copertina doppi, pulsanti di sistema nascosti. Finestra minima 1024x660. In riproduzione di macOS con la copertina (mac/MacNowPlaying.m, NON provato: le prove non si registrano nel Centro di Controllo).
- [~] Spettro: l'analisi riceveva il suono ~340 ms prima di sentirlo, a pacchetti da 43 ms (23 misure/s): ora a tempo con le casse (anticipo misurato ~0 ms) e a pezzi da ~11 ms (~94 misure/s). Da guardare a occhio con l'utente.
- [x] Doppio canale audio (MusicEngine/ParallelPcmAnalysis.cs: seconda decodifica PCM a tempo per i grafici quando l'uscita e' bitstream; provato forzandolo, misure uguali al canale normale). Bit perfect: c'era (Impostazioni › Cinecore Audio › Motore) ma il formato fisico del dispositivo restava quello di prima (es. 16 bit): ora intero con i bit del brano o float 32 fino a 24 bit, ripristinato all'uscita; decodifica verificata bit-esatta.
- [x] Analisi HDR: stile lasciato (l'utente: "qualita' assurda"); demo 4K TS da ~3 min a 53 s (thread dedicati, 3 decodifiche VideoToolbox), solo fotogrammi chiave interi (prima nei TS fotogrammi rovinati), aggiornamenti max 5/s, ProcessLauncher senza attese sincrone sull'asincrono. Aggiunta l'analisi SDR (BT.1886, bianco 100 nit): menu "Analisi HDR e SDR…". Tutorial: passo "Il menu del tasto destro".
- [x] Foto rifatte con i controlli di Avalonia (PhotoViewerView, PhotoEditView + PhotoEditCanvas, PhotoUi): visualizzatore con capsula di comandi, miniature, frecce, cornice che si nasconde, EXIF; editor con barra, strumenti, colori, spessori e filtri con anteprima. Corretto un difetto vecchio dei filtri (traslazione della matrice x255: Caldo/Freddo/Contrasto/Luminoso saturavano anche nelle copie salvate).
- [~] Timeline: HUD e barra musica continue (prima a scatti di 250 ms), ridisegno solo quando si sposta di un pixel. Anteprima sulla timeline video ("ogni tanto si ricarica dopo essere arrivata"): spostando il puntatore di 2 s i riquadri nuovi restavano vuoti ~0,2 s e spesso tornava lo stesso fotogramma chiave, e la scheda collassava se nessun riquadro era pronto: ora resta il fotogramma piu' vicino gia' pronto e la scheda mantiene la misura.

- 18/11 — Pacchetto dist/CinecorePlayere2026.app ricostruito alle 10:21 con tutto quanto sopra.

### 2026-11-19 notte — madVR in risparmio energetico (Claude)
Diagnosi chiusa sul file 4K 16:9 di Codex: GPU (M4, 10 core) satura a P1/P2; 81% del tempo GPU sono gli shader di madVR
(Jinc3 in riduzione 17 ms, NGU crominanza ~16 ms per fotogramma). Mancano ~20% di GPU. Copie, attese, traffico di memoria,
HDR passthrough e carico CPU misurati: nessuno sposta i persi (4,7/s). Percorso "copia IOSurface con disegno" pronto ma spento
(CINECORE_SURFACE_DRAW=1). Film scope: dentro il tempo. Consiglio: lasciare madvrLowPower=mpv.

## Da portare anche su Windows (segnalazioni GitHub del 19/11)
Due issue sulla Beta 3.6 Windows (WinForms/WPF): (1) richiesta di aprire le playlist `.wpl` di Windows Media Player;
(2) avvio sempre a schermo intero / finestra sempre ingrandita, misura della finestra non ricordata, testo illeggibile
quando si rimpicciolisce la finestra. Fatto nell'app Avalonia (mac-native/CinecoreMac) il 19/11:
- [ ] **Playlist .wpl** — `WplPlaylist.cs` (lettura SMIL, percorsi relativi/assoluti C:\ e \\server, ricerca in libreria per
  nome + cartelle), `LibraryView.ImportPlaylistFile` (diventa playlist della libreria, campo `SourceFile` in playlists.json:
  riaprire lo stesso file la aggiorna), `MainWindow.OpenFiles` (Finder/trascinamento/argomento d'avvio), Apri › Apri playlist (.wpl)….
  Su Windows manca: associazione `.wpl` nell'installer (Inno Setup, `installer/*.iss`, ProgID + "Apri con") e prova con
  percorsi assoluti veri (lì il passo 1 "così com'è" li trova subito).
- [ ] **Misura e posizione della finestra ricordate** — `MainWindow.WindowPlacement.cs` (impostazioni `windowBounds`,
  `windowMaximized`). Il ripristino di "ingrandita" vale solo fuori dal Mac: su Windows da provare con 100/125/150% e due
  schermi (Position in pixel fisici, Width/Height in DIP: la riduzione al WorkingArea divide per `Screen.Scaling`).
- [ ] **Testo mai sotto 10,5 DIP in libreria** — `LibraryView.MinTextPx` in `Format()`. Su Windows `ScreenDensity()` chiama
  CoreGraphics e ricade su 1,25: verificare che la scala della libreria a 1024×660 sia leggibile a 100% di Windows.
- [ ] **Avvio**: nell'app Avalonia lo splash è dentro la finestra e "Avvia a schermo intero" è spento di serie (bug della 3.6
  non presente); verificarlo sulla beta Windows.
- [ ] Rispondere alle due issue su GitHub quando esce la Beta 4 Windows.

### 20/11/2026 — bitstream con il cavo HDMI nuovo
- Con il cavo nuovo il Marantz dichiara a CoreAudio i formati compressi veri: `cac3` (Dolby Digital 48 kHz), `cec3`/`cc+3`/`cd+3` (Dolby Digital Plus / Atmos, 192 kHz), `mtct`/`mtc+` (TrueHD / Atmos in MAT, 768 kHz). Il libmpv del pacchetto (patch coreaudio-atmos) li usa già: bitstream automatico, niente più IEC dentro il PCM.
- Tolti: righe per formato e "Prova" in Impostazioni › Riproduzione (MacBitstreamTest.cs), impostazioni `bitstreamPcm:<uid>`, `coreaudio-spdif-hack` dall'app, EDID sintetico del Marantz (MacHdmiOverride.cs, mac/MacHdmiOverride.c, cinecore-hdmi-override), comandi QA bitstream-test/bitstream-carrier.
- DTS: macOS non lo espone mai via HDMI (solo formati Dolby). Il trucco "DTS dentro il PCM" passerebbe solo il core: DTS e DTS-HD MA restano decodificati da mpv (DTS-HD MA = PCM 7.1 senza perdite). Si perdono solo gli oggetti DTS:X.
- Da provare con l'utente a volume basso: un film AC3, uno E-AC3 Atmos e uno TrueHD Atmos (il Marantz deve mostrare il formato).
- Prova vera sul Marantz (20/11, stato letto via telnet 192.168.8.100 solo con interrogazioni "?", volume MV28 mai toccato):
  AC3 → `MSDOLBY D+DS` · E-AC3 → `MSDOLBY D+ +DS` · E-AC3 Atmos (Babylon) → `MSDOLBY ATMOS` · DTS (anche Moonrise ITA) → `MSDTS+NEURAL:X`
  (DTS core dentro 'cac3' 48 kHz, aggiunto a runtime solo per tracce DTS semplici: MacAudioDevices.IsPlainDts) ·
  DTS-HD MA / DTS:X → PCM 7.1 `MULTI CH IN` (voluto) · TrueHD / TrueHD Atmos: macOS apre 'mtct'/'mtc+' ma il Marantz vede
  "nessun segnale" (SSINFAISSIG 01, FSV NON). Il PCM 8ch 192 kHz passa: il cavo (convertitore MegaChips OUI 00-60-AD,
  "cHDMIb") non porta l'HBR, come il Lenovo C106A nella PR mpv 18801 (l'AUKEY CB-C89 invece funziona). TrueHD dentro il PCM = rumore.
  Quindi TrueHD di serie decodificato in PCM 7.1; impostazione `bitstreamTrueHd` per riaccenderlo con un collegamento che porta l'HBR.

### 20/11/2026 — lista dell'utente (Spotlight, schermi)
- [x] Spotlight: chiudendo un film partito da Spotlight si torna a Spotlight sullo stesso titolo (prima: Home). `MainWindow.Spotlight.cs` (_spotlightReturnPath).
- [x] Spotlight con tasti/telecomando: Invio (o clic sul titolo scelto, o freccia su) porta l'evidenziazione sulla fila Riprendi/Riproduci · Ricomincia · In coda · Cast; sinistra/destra solo su quella fila; giù/Esc tornano al carosello. Provato con QA spotlight-key.
- [x] Schermo spento durante il film = nero / colori che lampeggiano: mpv vincolava la sua finestra allo schermo di partenza (indice di NSScreen.screens, che cambia quando gli schermi si accendono/spengono) e la riportava li' a ogni spostamento del player. Ora `MpvEngine.FollowParentScreen` riallinea screen/fs-screen e `CinecoreUnconstrainWindow` (mac/MacLiquidGlass.m) toglie il vincolo. Provato con due schermi virtuali (scratchpad vd): riordino e spegnimento dello schermo del film, il video segue il player e mpv passa alla frequenza giusta. madVR/MPC-VR (Wine) non provati in questo caso.
- [x] Impostazioni › Riproduzione › Schermo dei film: il film si apre a schermo intero sullo schermo scelto (nome di macOS, MacScreens.cs), alla chiusura la finestra torna com'era (MainWindow.FilmScreen.cs). Provato su schermi virtuali.
- [ ] Telecomando nuovo (scelta schermo, trackpad, statistiche PC): in attesa della versione Windows dell'utente, per coerenza visiva.
- Prove: le istanze di prova con CINECORE_QA_BACKGROUND=1 sono processi accessori (niente Dock ne' "CinecoreMac" nei menu); CINECORE_QA_SCREEN accetta il nome dello schermo. Il settings.json di prova vuole SOLO stringhe ("true"/"false"): un booleano JSON fa scartare tutto il file.

## 20/11 sera — correzioni e DMG
- Fatti: trascinamento finestra dalla fascia alta, descrizioni lunghe nelle impostazioni (clic), traduzioni, riga motore, finestra all'avvio, bitstream musica, schede a schermo intero, Nascondi per DLNA, foto da Finder.
- mpv: la finestra video nasce invisibile e ausiliaria (gancio setTitle/setCollectionBehavior in MacLiquidGlass.m), si mostra solo al suo posto: niente "quadrato" al caricamento; tolta la linea della barra del titolo (riga nel video all'uscita dallo schermo intero, non riprodotta sullo schermo virtuale).
- Playlist: nome con maiuscole, scheda in stile album con mosaico 2x2 delle copertine (un album una volta sola), Rimuovi/Elimina dai tre puntini.
- Coda nel menu del tasto destro anche con la musica; rotella di Spotlight apre il menu Opzioni; tasto destro su Spotlight; Spotlight si entra con Invio/OK (non freccia su).
- Foto del regista dalla troupe TMDb; hero Musica non cambia artista al primo ingresso (timer); niente fascia scura Musica -> Serie TV; vetro dell'HUD foto in dissolvenza.
- App: dist/CinecorePlayer2026.app (eseguibile CinecorePlayer2026), versione 0.1.0 Beta 1 (non ancora pubblicata). DMG: mac/make-mac-release.sh.
- Motore: pacchetto 2026.11.20 senza i filtri; madVR/MPC-VR/LAV/XySubFilter dai siti ufficiali (mac/engine-components.json), provato in copia isolata: 44 file identici al prefisso di sviluppo. Al primo avvio il download parte da solo.
- Da pubblicare (utente): release mac-engine-v2026.11.20 con dist/engine-pack/2026.11.20/*.part* e beta-v0.1.0-Avalonia-MacOs con il dmg.
- 20/11 tardi: Spotlight, freccia su fuori dal film = barra in alto (ricerca/preferiti/impostazioni, OK per usarle); mpv all'uscita dallo schermo intero: livello Metal in "resize" (prima 2-3 fotogrammi tagliati ai bordi, registrati sul MacBook); analisi HDR/SDR muta e con "fotogrammi/s" (1-24; da 5 in su un flusso ffmpeg continuo, 24 arriva a ~13-15 misure/s per il ridisegno). Versione rimessa 0.1.0 Beta 1 (non pubblicata).
- 20/11 notte: cambio del dispositivo audio e di "Auto/Forza PCM" senza riaprire il film (MPV: audio-output + ao-reload; madVR/MPC-VR: solo l'audio affiancato rifatto, grafo acceso; provato con uscita nulla: madVR "ready" una volta). "Continua ad ascoltare": brano da cui si riprende (n/N · titolo) e barra dell'album.
- 20/11 notte (2): foto aperte all'avvio senza passare dal player (niente HUD in basso); cambio di qualita' YouTube/DLNA/Jellyfin nello stesso player (mpv "replace" + rotellina nell'HUD), si riapre solo con madVR/MPC-VR; YouTube: schermata di caricamento subito durante la risoluzione e senza ripartire; tasto libreria dell'HUD ferma il film come su Windows (stopCurrent).
- 20/11 sera tardi: icona bloccata su Cinecore (gancio setApplicationIconImage:), miniatura YouTube nel caricamento, etichetta della qualita' aggiornata dopo il cambio; DMG con sfondo (mac/dmg/make-background.swift + dmg-settings.py, dmgbuild in dist/tools), note in mac/RELEASE_NOTES_macOS_Beta1.md.
- 20/11 notte (3): motore senza pacchetto nostro su GitHub. MetalSharp Wine 6.0 e i filtri dai siti ufficiali, pezzi nostri in Contents/Resources/Engine (mac/build-engine-overlay.sh), prefisso con wineboot; verificato con mac/verify-engine.py (identico al motore di sviluppo) e film madVR A/B. Inoltre: bitrate in tempo reale nella scheda Info (mpv e LAV IBitRateInfo), niente splash con i file dal Finder, barra "Continua ad ascoltare" leggibile, automix che chiude i brani, ridisegni a cascata delle barrette animate (scatti con la musica), libreria ferma sotto il mix manuale, colonna tempo del primo brano nel mix.
- 21/11: "Continua ad ascoltare" solo titolo (avanzamento = barra); mix manuale senza Salva/Annulla: X/Esc chiude, con modifiche domanda Salva/Elimina sulla vignettatura come le foto; spiegazioni al passaggio del mouse (transizioni, tecniche, selettori, forme d'onda, tempo); colonna tempo del primo brano allineata a quella del secondo (fissa a 0, il motore stira solo il brano che entra); mini player: testi/analisi/coda/libreria riportano al player grande, schermo intero (anche dal pulsante verde) va al player normale.
- 21/11: mix manuale, velocita' anche del brano che finisce (ManualMix.OutgoingSpeed): solo nel passaggio, ci arriva con una curva di max 2 s; il brano che entra in Auto si aggancia al tempo effettivo; a fine passaggio il nuovo torna al suo tempo in 8 s (gia' prima). Tolta la riga in basso.
- 21/11: telecomando e playlist presi dalla versione Windows (~/Downloads/Cinecore_per_Mac_telecomando_e_playlist, copia del 20/11) e adattati: schermi (MainWindow.RemoteScreens.cs), preferiti, Questo Mac (MacPcMonitor.cs). Impostazioni del telefono rifatte: indice a gruppi con ricerca, catalogo "player" da SettingsView.Remote.cs (stesse voci e stessi effetti del pannello del Mac), niente RTX/EVR sul Mac. Backup dei file Mac sostituiti nello scratchpad della sessione.
- 21/11 (2): mix manuale: l'onda di sopra segue la velocita' del primo brano, quella di sotto no (B in Auto non dipende piu' da A); apertura dell'editor in parallelo e onde in memoria + preparazione anticipata; time-stretch del brano che finisce avviato con la storia dell'anello (niente buco/click: misurato con CINECORE_QA_MUSIC_DUMP e CINECORE_PREVIEW_DUMP). Ampli IP a scatti di dB (RampAmplifierAsync/ReadThenSetAsync, provato con un finto Denon). Testi: DropCollapsedRuns, copertura sull'insieme piu' piccolo, TightenOpening, motivi di scarto, revisione 4, "Sincronizza il testo" nei tre puntini. Release in ~/Downloads/CinecorePlayer2026_Mac_Beta1_Release.
- 21/11 — Foto: crash scorrendo con le frecce (bitmap liberata ancora mostrata) risolto. Audio spaziale fase 1 e 2: patch `coreaudio-spatial` di libmpv (AUSpatialMixer, head tracking AirPods) per cuffie e altoparlanti del Mac con tutti i motori; DTS:X 7.1.4 con libavcodec patchata (`mac/build-ffmpeg-dtsx.sh`, letto senza i canali alti). Fase 3 (oggetti TrueHD Atmos con LibTrueHD) da fare. 3D con madVR: il VideoHost andava in crash (0xFD) su GetPreferredAspectRatio, ora funziona. Cambio di frequenza su HDMI con ricevitore: il film aspetta che schermo e uscita audio si stabilizzino e l'uscita scelta non viene piu' cancellata durante la rinegoziazione (audio che saltava Marantz ↔ casse, aperture MPC-VR scadute).
- 21/11 sera — Audio spaziale fase 3: TrueHD Atmos con LibTrueHD (Rust, toolchain ufficiale via rustup: il Rust di Homebrew ha la std in bitcode LLVM illeggibile dal linker Apple) dentro libmpv, decoder `truehd_atmos` (--ad-truehd-atmos) con panner cartesiano oggetti → 7.1.4 e rampe OAMD; provato su Dolby "Unfold" (17x tempo reale, LFE identico al 7.1 Dolby, canali alti attivi, seek ok) fino allo spazializzatore. mpv: in uso normale i fotogrammi persi finiscono nel registro di sessione ([mpv] fotogrammi persi ...); sullo schermo del Mac 0 persi anche a batteria, da misurare sul Marantz.
- 22/11 — Tolto MPC Audio Renderer dalla versione Mac (schede Componenti del Mac e del telefono, stringhe, prefisso Wine e script del vecchio pacchetto: sul Mac l'audio lo fa mpv nativo). Audio spaziale con tutti i renderer: DTS:X 7.1.4 e oggetti TrueHD Atmos si accendono/spengono al volo cambiando uscita (AirPods collegati a film avviato) o interruttore, con mpv e con l'audio affiancato di madVR/MPC-VR (ad-truehd-atmos ha UPDATE_AD: mpv ricrea il decoder; provato sul DTS:X di Atomic Blonde con ao=null: 8 → 12 → 8 canali, riproduzione regolare). Con madVR/MPC-VR l'audio affiancato ora vale anche per i dischi (Blu-ray con libbluray sulla stessa playlist, DVD dallo stesso indirizzo/VOB): prima restavano allo stereo di Wine. Dischi non provati (nessun disco sul Mac).
- 22/11 — Audio che saltava scorrendo l'anteprima della barra durante il film: i fotogrammi (MenuGlass.GrabFramesAsync) partivano con Process.Start (fork dell'app con libmpv a ogni punto, vedi nota del 13/11). Ora passano da ProcessLauncher con taskpolicy -b. Da confermare all'ascolto.
- 23/11: dieta del motore madVR/MPC-VR (EngineSlim.cs in installazione, mac/slim-engine.py per motori esistenti): via i simboli di debug delle DLL di Wine (1,5 GB), Mono, Gecko, archivi/asset di MetalSharp per i giochi, client Steam; DLL del prefisso uguali a Wine come collegamenti fissi. Installato da 6,2 a ~1,5 GB (manifesto installedSize 1,5 GB, prima diceva 6,8). verify-engine.py tiene conto della dieta. A/B madVR 3 coppie (risparmio energetico): identico. Alleggeriti anche dist/mac/runtime-arm64 (motore di sviluppo) e ~/Library/.../Cinecore/engine; avanzi vecchi (runtime/, wineprefix-*) nel Cestino. Aperto: impostazioni madVR del motore di sviluppo cambiate il 21/11, l'overlay distribuito ha quelle del 20/11.
- 23/11 pomeriggio: consumi madVR. Analisi (schermo virtuale 4K, CPU per thread, mappa blocchi FEX): la traduzione GL non rallenta gli shader; il costo CPU e' codice x86 di madVR sotto FEX. Fatti: (1) MadVrCodePatches: ciclo crominanza madVR 0x114f90 in SSE (identico, 906 casi); (2) MadVrThreadPool senza wineserver (eventi riusati, semaforo nativo); (3) cache su disco di FEX (FEX_DISKCACHE + lazy, pronto 8,0 -> 6,7 s); (4) decoder VideoToolbox nello stesso processo: mac-native/VideoToolboxBridge/vt_unix.c -> Contents/MacOS/cinecorevt.so caricata dal ponte con NtQueryVirtualMemory(1002) + __wine_unix_call_dispatcher (0,04 us a chiamata, niente socket ne' programma separato, fotogramma senza copia): CPU totale ~180% -> ~160%, immagine uguale. Motori installati con la DLL vecchia: l'app torna al programma separato (BridgeSupportsUnixLib). Overlay: aggiornata solo CinecoreVideoToolbox.dll (manifesto resta 2026.11.20).
- 23/11 sera: Impostazioni › Componenti › Preset: automazioni stile iPhone (Quando … → Usa renderer: preset) e preset per renderer (Basso/Medio/Alto/Ultra/DVD nostri + utente), menu del tasto destro Video › Preset (Automatico, Impostazioni normali, preset), gruppo madVR "Prestazioni" (compromessi qualità/prestazioni), avviso risparmio energetico con "Non mostrare più" (e niente avviso con preset attivo). File: RenderPresets.cs, MainWindow.Presets.cs, SettingsView.Presets.cs (+ ComponentSettings, SettingsView, MainWindow). Pacchetto per Windows: ~/Downloads/CinecorePlayer_modifiche_preset_20261123 (LEGGIMI.md).
