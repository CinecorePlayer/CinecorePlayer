# Cinecore Player per macOS: tutte le funzioni

Versione di riferimento: Beta 1.1 per macOS (0.1.1), 24 novembre 2026. Mac con Apple Silicon, macOS 13 o successivo.
Elenco ricavato dal codice dell'app Mac (`mac-native/CinecoreMac`), voce per voce.

## 1. Cosa riproduce

- **Video locali**: MKV, MP4, M2TS, TS, MOV, AVI, WMV, WebM e gli altri formati letti da mpv e da LAV Filters.
- **Musica locale**: MP3, FLAC, WAV, AIFF, OGG, Opus, M4A, AAC, WMA e altri, con il Cinecore Audio Engine.
- **Foto**: visualizzatore a tutta finestra con presentazione, modifica e azioni del Mac (capitolo 11).
- **Playlist `.wpl`**: si aprono dal Finder o da Apri › Apri playlist.
- **DVD**: da lettore, cartella `VIDEO_TS` o immagine ISO (montata da sola). Menu interattivi con libdvdnav per tutti i motori: menu, avvertenze e titoli brevi passano da mpv, il film dal motore scelto. Si naviga con frecce, Invio e Indietro, con il mouse o dal telefono; `M` torna al menu del disco. Supporta i DVD protetti con CSS (libdvdcss).
- **Lettura diretta dei DVD**: in alternativa ai menu, si va dritti al titolo principale scegliendo capitoli, audio e sottotitoli dal menu del player (Impostazioni › Riproduzione › Lettura dei DVD).
- **Ripresa dei dischi**: alla riapertura di un disco visto in parte si sceglie *Riprendi il film* o *Dal menu*.
- **Blu-ray**: titolo principale da cartella `BDMV`, disco o ISO (libbluray con mpv; LAV Splitter con madVR e MPC-VR). I Blu-ray protetti si aprono solo con libaacs e un `KEYDB.cfg` dell'utente, importabile da Impostazioni › Riproduzione › Blu-ray protetti: il player non contiene chiavi.
- **CD audio**: il CD si apre come un album, riconosciuto tramite MusicBrainz (titoli, artista, anno, copertina) con l'impronta del disco calcolata come su Windows. Lettura senza perdita, passaggi senza pause, testi e ascolti registrati. I dati di un disco già visto restano salvati.
- **Dischi in libreria**: il disco inserito compare tra i film o gli album e sotto Dispositivi finché resta nel lettore.
- **Server di rete**: DLNA, Jellyfin, Emby e Plex (capitolo 9).
- **YouTube e indirizzi web**: tramite yt-dlp, con limite di risoluzione; i file diretti (`.mp4`, `.m3u8`…) si aprono così come sono. La miniatura compare mentre l'indirizzo viene risolto.
- **Dal Finder**: *Apri con*, doppio clic e trascinamento sulla finestra, anche ad app già aperta; foto, video, musica, playlist e dischi finiscono nel posto giusto.

## 2. Motori video

- **Tre motori a scelta**: **mpv** nativo (Vulkan su MoltenVK, decodifica VideoToolbox), **madVR** e **MPC Video Renderer** (con LAV Filters e XySubFilter, sotto Wine per ARM64), più **Auto**. Si sceglie un motore per la sessione e uno predefinito.
- **Il motore non cambia mai da solo**: in risparmio energetico, o se madVR continua a perdere fotogrammi, l'app avvisa e propone un motore più leggero, riprendendo dallo stesso punto. L'avviso ha *Non mostrare più*, non compare se un preset è già attivo e aspetta che il menu del tasto destro sia chiuso.
- **Motore madVR e MPC Video Renderer scaricabile**: al primo avvio l'app scarica MetalSharp Wine, madVR, MPC Video Renderer, LAV Filters e XySubFilter dai siti ufficiali, controlla l'impronta SHA-256 di ogni archivio e registra i filtri. Avanzamento e *Annulla* in Impostazioni › Aggiornamenti. Senza motore tutto si riproduce con mpv.
- **Presentazione esatta** con madVR e MPC-VR: ogni fotogramma esce alla sincronia verticale giusta, sullo schermo dove si trova il film, anche su schermi esterni e proiettori a 23,976 / 24 Hz.
- **madVR parte con la coda piena**: nessun fotogramma perso all'avvio e dopo un salto (Componenti › madVR › Prestazioni). mpv parte quando il primo fotogramma è pronto.
- **Preset dei renderer** (Video › Preset):
  - **Automatico**: decide quanto far lavorare il renderer guardando il motore, il Mac (chip, core della GPU, presenza della ventola), risparmio energetico, batteria e calore, e il film (risoluzione rispetto alla misura a schermo, fotogrammi al secondo, qualità della sorgente dal bitrate per pixel corretto per il codec). Se il film va ingrandito la potenza va prima all'ingrandimento (NGU Anti-Alias e pulizia per le sorgenti povere, NGU Sharp e poi crominanza NGU e SuperChromaRes per quelle buone). Accanto alla voce compare il livello scelto.
  - **Personalizzato**: automazioni dell'utente, come i Comandi rapidi dell'iPhone ("Quando il film è 4K e il risparmio energetico è attivo" → "Usa madVR: Alto"), su risoluzione, HDR, fotogrammi al secondo, alimentazione e schermo.
  - **Disattiva preset**: valgono solo le Impostazioni.
  - Preset pronti per ogni renderer (Basso, Medio, Alto, Ultra, "DVD e bassa qualità"), modificabili; nuovi preset in cui ogni voce parte da "Come nelle Impostazioni". Un preset non tocca mai le Impostazioni salvate. Pagina Impostazioni › Componenti › Preset.
- **Immagine / HDR**, una scelta sola per tutti i motori: Auto, passthrough HDR al display, tone mapping HDR → SDR, oppure HDR → SDR via 3DLUT di madVR. Con mpv l'HDR si vede sullo schermo del Mac in EDR.
- **Upscaling** di madVR oltre la risoluzione nativa, dal menu.
- **Frequenza dello schermo**: cambio alla cadenza del film prima dell'avvio, anche quando lo schermo offre solo 24,000 Hz per un film a 23,976.
- **3D**: rilevamento automatico, Side-by-Side e Top/Bottom → 2D con tutti i motori, uscita nativa, schermo intero esteso su due schermi (un occhio per schermo, `Maiusc+F`).
- **Statistiche**: madVR (`Ctrl+J`, con azzeramento), MPC-VR e mpv, dal menu.
- **Impostazioni dei componenti dentro l'app**: pagine native per madVR (compresa la scheda Prestazioni), MPC Video Renderer, MPV, LAV Video e LAV Audio.
- **Schermo dei film**: si sceglie su quale schermo aprire il film.

## 3. Immagine a schermo intero

- **Dimensionamento**: riempi, altezza costante, area costante e personalizzato, con cursore fra altezza e area costante. Tasto `Z` durante il film.
- **Bande nere**: misura del formato reale del fotogramma, anche quando cambia a metà film.
- **Scene IMAX** a tutto schermo.
- **Transizione animata** al cambio di formato.
- **Ritaglio** del video (Video › Ritaglio).
- **Avvio a schermo intero** facoltativo; schermo intero di sistema di macOS, con passaggio pulito su nero.

## 4. Controlli durante la riproduzione

- **HUD**: timeline con anteprima del fotogramma (sui dischi l'orario), capitoli, volume, schermo intero, salti di 10 secondi.
- **Scan veloce**: ×0,5 … ×4 tenendo premuto uno skip.
- **Passo singolo**: `,` e `.` di un fotogramma, in pausa.
- **Vai a…**: salto a un tempo preciso.
- **Capitoli** dal menu e con Pag↑ / Pag↓.
- **Riprendi da dove eri**, con "Continua a guardare" in libreria.
- **Volume ricordato per ogni film** e volume oltre il 100% con tutti i motori.
- **Intro e titoli di coda**: rilevamento automatico per le serie, con *Salta intro* e *Prossimo episodio*.
- **Cast** durante il film, con fotografie e personaggi.
- **Pannello Info**: prima il film (locandina, anno, durata, voto, trama, cast), poi i dati tecnici con il bitrate video e audio dal vivo, anche per YouTube e DLNA.
- **Cambio di qualità** di uno streaming YouTube, DLNA, Jellyfin o Emby sul posto, dallo stesso punto.
- **Menu del tasto destro** su vetro, con Apri, Capitoli, Audio, Sottotitoli, Video, Preset, Dispositivi, Strumenti e Impostazioni; le voci restano bloccate finché non si possono usare.
- **Tasti multimediali** del Mac e *In riproduzione* di macOS.

## 5. Audio dei film

- **Uscita audio**: scelta del dispositivo, che cambia al volo ricostruendo solo l'audio; ritorno al predefinito.
- **Bitstream HDMI**: Dolby Digital, Dolby Digital Plus (anche Atmos) e DTS verso il ricevitore, con *Auto (bitstream se conviene)* o *Forza PCM*. DTS-HD e DTS:X vengono decodificati; TrueHD è decodificato salvo adattatori che portano l'HBR (opzione sperimentale *TrueHD e Atmos*).
- **Tracce audio** dal menu e dal telefono.
- **Ritardo audio** al volo (`Ctrl +/−` a 10 ms, con Maiusc 100 ms), salvato per film.
- **Sincronizzazione automatica** fra tracce interne, anche con ritardo variabile; **audio esterno** affiancato al film, con conversione di velocità e sincronizzazione trovata da sola.
- **Amplificazione** fino a +12 dB.
- **Audio spaziale**: in cuffia (AirPods con i movimenti della testa) e sugli altoparlanti del Mac il 5.1 e il 7.1 passano dallo spazializzatore di Apple. Il DTS:X arriva in 7.1.4 con i canali alti; il TrueHD Atmos è decodificato da LibTrueHD e reso in 7.1.4. Vale con tutti i motori.
- **Volume del ricevitore di rete** (capitolo 14) con i tasti ↑ ↓.

## 6. Sottotitoli

- **Tracce interne ed esterne**: disattivati, automatici (solo forzati) o scelti a mano; file `.srt`, `.ass`, `.ssa`, `.sub`/`.idx`, `.sup`, `.vtt`.
- **Ricerca e scaricamento** dal player: YIFY, Addic7ed, Subdl (chiave del proprio account) e OpenSubtitles (proprio account).
- **Lingua predefinita** dei sottotitoli.
- **Riallineamento sull'audio**: il file viene rimesso a tempo ascoltando il film, anche su un sottotitolo già caricato.
- **Impostazioni dei sottotitoli** dal menu.

## 7. Libreria

- **Sezioni**: Home, Film, Serie TV, Musica, Foto, Video, YouTube, Playlist, Preferiti, Diario e Rete.
- **Home** con banner a rotazione e "Continua a guardare".
- **Cartelle sorgenti** e volumi esterni, aggiunti e tolti dall'interfaccia; fonte predefinita della libreria (Mac o server di rete).
- **Metadati da TMDb** nella lingua dell'app: locandine, sfondi, trama, generi, cast, registi, episodi. Serve la propria chiave TMDb gratuita.
- **Correzione manuale** di titolo e copertina.
- **Scheda del titolo**: panoramica, dettagli tecnici, recensioni e voti (TMDb, IMDb, Letterboxd, Metacritic), troupe e cast.
- **Raccolte, filtri, ordinamenti** e **ricerca globale** su vetro.
- **Diario** di ciò che è stato visto, con rimozione delle voci; *Nascondi dalla libreria*, *Mostra nel Finder*, *Apri con*.
- **Playlist** e **coda** modificabili, *Aggiungi a playlist*, *Riproduci da qui*.
- **Rapporto della libreria**: titoli, spazio, formati e casi da controllare.
- **Guida del primo avvio**: benvenuto, cartelle, chiave TMDb, funzioni in più.
- **Aspetto**: tema chiaro o scuro, colore d'accento e di sfondo con selettore colore, altezza della barra in alto (automatica sugli schermi piccoli); **Liquid Glass** da macOS 26 e sfocatura vera nelle versioni precedenti.
- **Lingue**: italiano, inglese e spagnolo in tutta l'app.

## 8. Spotlight e modalità cinema

- **Spotlight**: la sala a tutto schermo con sfondi grandi preparati in anticipo, ricerca, preferiti e cast; fonte libreria del Mac o server di rete; può aprirsi all'avvio. Usabile tutto dal telecomando.
- **Modalità cinema**: schermata prima del film (anche con lo sfondo automatico di TMDb) e demo prima della proiezione, scelte o casuali, dalle cartelle dell'utente.

## 9. Rete: DLNA, Jellyfin, Emby, Plex

- **Ricerca automatica** dei server DLNA, Jellyfin, Emby e Plex nella rete, oppure indirizzo aggiunto a mano.
- **Jellyfin ed Emby**: accesso con nome e password (Jellyfin anche Quick Connect), catalogo con i metadati del server, file originale o qualità ridotta convertita dal server, punto di ripresa e stato "visto" allineati.
- **DLNA e Plex**: navigazione e riproduzione.
- **Token di accesso** nel Portachiavi, mai nei registri.

## 10. Musica

- **Cinecore Audio Engine per Mac** su Core Audio: FFmpeg, catena DSP in doppia precisione, uscita condivisa, **esclusiva** o **bit perfect**.
- **Equalizzatore** a 10 bande, 31 bande o parametrico (frequenza, guadagno, Q), con preset e preamplificazione.
- **Volume e protezione**: protezione dalla saturazione (limiter true-peak), ReplayGain per brano o album, loudness, AutoEQ dinamico.
- **Immagine stereo**: crossfeed per le cuffie, bilanciamento, ampiezza stereo, mono.
- **Passaggi fra i brani**: senza pause (gapless), dissolvenza incrociata, salto dei silenzi, oppure **Automix** (anche negli album) con stile e intensità: analizza battute, frasi, tonalità e voce e sceglie passaggi da DJ (EQ a tre bande, filtri, eco, brake, loop), con "Solo dissolvenza" e niente effetti da club sulla musica che non è da ballo. **Sincronizzazione tempo**: il brano che entra prende il tempo di quello che esce e poi torna al suo.
- **Mix manuale**: editor del passaggio con velocità del brano che esce e curve di volume, EQ e filtro disegnate a mano; *Ordina la coda per il mix*.
- **Grafici**: Spettro, VU ad ago, Spettrogramma, Onda, Loudness, Dinamica, Stereo (goniometro), Livelli con correlazione di fase, Info; LUFS integrato, true peak, gamma e PLR dal vivo.
- **Brano a schermo intero** con copertina e grafici, **mini player**, **barra e coda su vetro** (cronologia, brano in corso, in arrivo; riordino trascinando, durata di ogni brano), volume a scomparsa.
- **Testi**: LRCLIB, Genius e lyrics.ovh, sincronizzati quando disponibili e allineati alla voce dal player (*Sincronizza il testo* nel menu ••• della barra).
- **Album, artisti con foto** (dal disco, poi Spotify o TIDAL con le proprie chiavi), playlist (copertina dai primi quattro album), preferiti, **radio** con brani simili a fine coda, **Continua ad ascoltare** con avanzamento dell'album, loop del brano.
- **Ascolti verso Last.fm e ListenBrainz**, con coda per quando manca la rete.

## 11. Foto

- **Visualizzatore**: zoom, rotazione, specchio, presentazione, preferiti, info, prima e ultima foto.
- **Editor a capsula** in basso su vetro: penna, forme, testo, filtri; salva sempre una copia.
- **Azioni del Mac**: condividi, copia, stampa, *Apri con*, *Mostra nel Finder*, imposta come sfondo della scrivania, luoghi in Mappe, sposta nel Cestino.

## 12. Analisi HDR e SDR

- **Metadati dichiarati**: schermo di mastering, MaxCLL, MaxFALL, Dolby Vision, HDR10+.
- **Misura reale sul film**: picco e media in nit, percentili, distribuzione della luminosità, gamut oltre Rec.709 e DCI-P3.
- **Tempo reale**, muto, da 1 a 24 misure al secondo: forma d'onda RGB, vettorscopio, cromaticità CIE 1931, falsi colori.
- **Esportazione** su file.

## 13. Telecomando dal telefono

- **Pagina web servita dal Mac**: abbinamento con codice QR, senza installare nulla; se il telefono non si collega si può cambiare indirizzo (consigliato il nome del Mac).
- **Comandi**: play, pausa, stop, salti, scan, capitoli, volume, schermo intero, frecce e OK, tastiera.
- **Trackpad**: muove il puntatore del Mac, clicca e scorre (serve il permesso Accessibilità).
- **Tracce** audio e sottotitoli, **libreria e coda**, Spotlight completo.
- **Impostazioni** con i menu Player e Componenti, categorie raggruppate e ricerca: le stesse voci della finestra Impostazioni del Mac, più il monitor *Questo Mac* (CPU, memoria, GPU, batteria, rete, dischi).

## 14. Dispositivi di casa

- **Amplificatore via rete** (Denon, Marantz e altri via IP): volume a passi di 1 dB e mute, con limiti di sicurezza (al massimo 3 dB per comando, mai sopra −20 dB); collegato a un'uscita audio del Mac.
- **Luci WLED**: si spengono o si abbassano durante la riproduzione.
- **Automazioni**: all'avvio, in pausa, alla ripresa, allo stop e ai titoli di coda, richiesta HTTP o messaggio MQTT (per esempio a Home Assistant).

## 15. Servizi, aggiornamenti e sistema

- **Trakt**: collegamento con codice, *Consigliati per te* con *Vedi tutti*, *Segna come visto* e *Non mi interessa*, invio di ciò che si finisce di guardare e dei voti.
- **Aggiornamenti** dell'app dalle release di GitHub, con controllo all'avvio facoltativo; aggiornamento dei componenti di terze parti (yt-dlp e motore madVR/MPC-VR).
- **Apri Cinecore all'avvio del Mac**.
- **Chiavi e token** cifrati nel Portachiavi di macOS, mai in chiaro nei file o nei registri; le chiavi personali (TMDb, Subdl, OpenSubtitles, Spotify, TIDAL) le inserisce ogni utente.
- **Prestazioni**: interfaccia che ridisegna solo ciò che cambia, avvio dei processi esterni senza disturbare il film, finestre del motore tenute nello Spazio dello schermo intero.

## 16. Scorciatoie da tastiera

| Tasto | Azione |
|---|---|
| Spazio | Play / pausa (in foto: presentazione) |
| ← → | Indietro / avanti di 10 secondi (in foto: precedente / successiva) |
| `,` `.` | Fotogramma precedente / successivo |
| Pag↑ Pag↓ | Capitolo successivo / precedente |
| ↑ ↓ | Volume (oltre il 100% l'amplificazione; con il ricevitore di rete il suo volume) |
| M | Muto (sui DVD: menu del disco) |
| F | Schermo intero |
| Maiusc+F | Schermo intero esteso su due schermi |
| Z | Dimensionamento dell'immagine |
| Ctrl+J | Statistiche del renderer |
| Ctrl + / Ctrl − | Ritardo audio di 10 ms (con Maiusc: 100 ms) |
| Ctrl 0 | Azzera il ritardo audio |
| O, Cmd+O | Apri file |
| S | Chiudi e torna alla libreria |
| Cmd+W | Chiudi |
| Esc | Indietro / esci dallo schermo intero |
| Frecce, Invio, Indietro | Menu dei DVD |
| In foto: + − 0 | Zoom, vista iniziale |
| In foto: R, Maiusc+R, H | Rotazione, rotazione inversa, specchio |
| In foto: F, I, E, Canc | Preferito, info, modifica, Cestino |
| In foto: Inizio / Fine | Prima / ultima foto |
