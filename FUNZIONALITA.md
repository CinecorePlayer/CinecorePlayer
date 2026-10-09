# Cinecore Player: tutte le funzioni

Versione di riferimento: Beta 3.6 (0.3.6), 8 ottobre 2026.
Elenco ricavato dal codice del player, voce per voce. Le funzioni aggiunte nella 3.6 sono segnate con **(3.6)**.

## 1. Cosa riproduce

- **Video locali**: MKV, MP4, M2TS, TS, MOV, AVI, WMV, WebM e gli altri formati letti da LAV e da mpv.
- **Musica locale**: MP3, FLAC, WAV, OGG, Opus, M4A, AAC, WMA e altri.
- **Foto**: visualizzatore a tutta finestra con presentazione.
- **Dischi**: Blu-ray e DVD da unità ottica, da cartella (BDMV o VIDEO_TS) e da immagine ISO, che viene montata da sola senza permessi di amministratore. I Blu-ray protetti si aprono solo se l'utente ha installato MakeMKV o importato un proprio file di chiavi: il player non ne contiene.
- **DVD come su un lettore da tavolo (3.6)**: avvisi iniziali, menu del disco e contenuti speciali, con tutti i renderer (madVR, MPC Video Renderer, EVR) e nel formato corretto (16:9 o 4:3 anamorfico), anche passando da finestra a schermo intero. Nei menu ci si muove con frecce, Invio e Indietro della tastiera, con il mouse o con le frecce del telecomando sul telefono; il tasto M o la voce Menu del disco riportano al menu. Capitoli, tracce audio e sottotitoli si scelgono dal menu del player; menu e audio partono nella lingua dell'interfaccia quando il disco la offre. Richiede che la regione dell'unità coincida con quella del disco; se il disco non parte si passa da soli alla lettura diretta.
- **Lettura diretta dei DVD (3.6)**: in Impostazioni › Generale si può scegliere di andare dritti al film senza menu (con mpv), scegliendo capitoli, audio, sottotitoli e titoli del disco dal menu del player.
- **Ripresa dei dischi (3.6)**: alla riapertura di un disco visto in parte compare una schermata a tutto schermo con l'immagine del film e la scelta Riprendi o Ricomincia, come su un lettore (frecce e Invio, mouse o telecomando). Sulla timeline dei dischi compare l'orario sotto il puntatore, senza fotogramma di anteprima (un secondo lettore sullo stesso disco fermerebbe il film).
- **Ritaglio del video (3.6)**: da Video › Ritaglio si sceglie il formato (4:3, 16:9, 1,85, 2,00, 2,20, 2,35, 2,39, 2,76) e l'immagine viene tagliata al centro, con madVR, MPC Video Renderer, EVR e mpv; vale per il film in corso.
- **Schermate di caricamento (3.6)**: sfondo e titolo del film con una barra di avanzamento a tutta larghezza che arriva in fondo prima dell'inizio; per i CD la copertina dell'album e poi la foto larga dell'artista.
- **Barra musicale (3.6)**: isola di vetro centrata e arrotondata, con la pagina visibile attorno.
- **Telecomando (3.6)**: tempo scrivibile toccando il minuto corrente, tocco visibile sui tasti, nessuna selezione di sistema su iPhone; a schermo una pillola compatta per volume (con i dB dell'amplificatore) e messaggi, e la timeline del player che segue il trascinamento dal telefono.
- **Spagnolo (3.6)**: terza lingua dell'interfaccia, da Impostazioni › Generale › Lingua; circa 1.300 testi tradotti. Restano in inglese la pagina del telecomando, alcuni messaggi composti al momento e le trame di TMDb.
- **Cast durante il film (3.6)**: un tasto nell'overlay di riproduzione apre il cast con fotografie e personaggi.
- **Pannello Info (3.6)**: prima il film (locandina, anno, durata, voto, trama e cast), poi i dati tecnici.
- **Dischi in libreria (3.6)**: il disco inserito compare tra i film o tra gli album e come voce sotto Dispositivi, finché resta nel lettore.
- **Uscite audio (3.6)**: corretto il blocco collegando un dispositivo audio mentre suona un CD (un solo lettore sul disco, la copia per i testi cede il passo alla musica, il lettore si interroga solo quando Windows segnala un cambiamento); se l'uscita in uso sparisce il brano si riapre da solo nello stesso punto.
- **Schermo intero e analisi audio (3.6)**: passaggio a schermo intero e ritorno senza la finestra intermedia; l'immagine del caricamento non traspare più sotto il film; il selettore dei grafici ha un tratto uguale sotto ogni voce, grigio, colorato su quella scelta; libreria, grafici, testo, voci dell'analisi e riquadro PiP si danno il cambio in dissolvenza; tornando da Spotlight con la musica in corso non resta più lo schermo nero.
- **Caricamento (3.6)**: la barra arriva in fondo prima che il film parta e scorre in modo uniforme; nel logo d'avvio l'ultima lettera non compare più di scatto.
- **Interfaccia (3.6)**: trame e righe del cast con un carattere pensato per i corpi piccoli; cambio tema chiaro/scuro in dissolvenza; selettore dei grafici audio di solo testo con tratto colorato; schermo intero subito sotto Play nel menu del tasto destro.
- **Spotlight (3.6)**: i titoli iniziati e da riprendere vengono per primi, dal più recente; quando è vuoto una pagina spiega cosa aggiungere e porta alla libreria; l'interruttore Libreria / Rete segue sempre la sorgente salvata.
- **CD audio (3.6)**: il CD nel lettore si apre come un album (da Apri › Apri disco, dall'unità o da una sua traccia), con le tracce in coda. Il disco viene riconosciuto dall'indice delle tracce tramite MusicBrainz, che fornisce titoli, artista, anno e copertina; senza rete o se il disco non è in archivio restano Traccia 1, 2… L'audio è letto dal disco senza perdita e suonato dal Cinecore Audio Engine, quindi con passaggio senza pause tra i brani, uscita esclusiva o bit-perfect, equalizzatore, testi e registrazione degli ascolti. I dati di un disco già visto restano salvati: la volta dopo si apre senza rete.
- **Schermata prima del film anche per i dischi (3.6)**: se attiva, il disco viene presentato con il titolo e lo sfondo del film presi da TMDb a partire dall'etichetta del disco.
- **Server di rete**: DLNA, Jellyfin ed Emby **(3.6)**.
- **YouTube**: tramite yt-dlp, con limite di risoluzione da 144p a 8K.
- **File aperti da Esplora file**: "Apri con", doppio clic e trascinamento; se il player è già aperto il file passa alla finestra esistente.

## 2. Motori video

- **Quattro motori a scelta**: madVR, MPC Video Renderer, EVR (tutti su DirectShow con LAV) e mpv. "Auto" sceglie da solo; si possono impostare un motore per la sessione e uno predefinito.
- **HDR**: passthrough al display, RTX Video HDR con MPC Video Renderer, conversione HDR → SDR con pixel shader oppure con 3DLUT, profili HDR di madVR.
- **Upscaling**: madVR oppure NVIDIA RTX Super Resolution, con profili.
- **Frequenza dello schermo**: cambio automatico alla cadenza del film, compresi i valori frazionari come 23,976 Hz.
- **3D**: rilevamento automatico di Side-by-Side e Top/Bottom, conversione a 2D, uscita nativa, schermo intero esteso su più monitor (un occhio per schermo).
- **File MKV con tracce "disattivate"**: vengono serviti al lettore con il flag corretto, senza riscrivere il file.
- **Impostazioni dei componenti dentro il player**: pagine native per madVR, LAV Video, LAV Audio, MPC Video Renderer, MPC Audio Renderer, XySubFilter e mpv.

## 3. Immagine a schermo intero

- **Dimensionamento**: Riempi, altezza costante, area costante e personalizzata, con cursore tra altezza e area costante e moltiplicatore per formato. Tasto `Z` durante il film.
- **Bande nere**: misura del formato reale del fotogramma, anche quando cambia a metà film.
- **Scene IMAX**: possono allargarsi a tutto schermo.
- **Transizione animata** al cambio di formato, con durata a scelta.
- **Avvio a schermo intero** del player, sullo schermo dove si trova il mouse.
- **Adattamento allo schermo**: l'interfaccia si ridimensiona da sola cambiando monitor o scala di Windows.

## 4. Controlli durante la riproduzione

- **HUD**: timeline con anteprima del fotogramma, capitoli, volume, schermo intero, salti di 10 secondi.
- **Scan veloce (3.6)**: tenendo premuto uno skip parte lo scorrimento veloce (x0,5, x1, x2, x4); ogni clic aumenta la velocità o inverte il verso, Play lo chiude.
- **Passo singolo (3.6)**: `,` e `.` spostano di un fotogramma indietro o avanti, in pausa.
- **Riprendi da dove eri**, per ogni titolo, con "Continua a guardare" in libreria.
- **Volume per film**: il livello scelto durante un film viene ricordato per quel film.
- **Amplificazione oltre il 100%** fino a +12 dB.
- **Pannello Info**: sorgente, uscita, decodifica, audio, fotogrammi persi, origine del flusso; funziona anche su DLNA, Jellyfin ed Emby.
- **Intro e titoli di coda**: rilevamento automatico per le serie (confronto dell'audio tra gli episodi) e pulsante per saltarli.
- **Modalità PiP**: finestra piccola sempre in primo piano con i comandi essenziali.
- **Menu del tasto destro** in vetro, con tutte le scelte di video, audio, sottotitoli ed extra.
- **Tasti multimediali** della tastiera e scorciatoie (elenco al capitolo 16).

## 5. Audio dei film

- **Scelta dell'uscita audio** e del dispositivo, con ritorno al predefinito del PC.
- **Bitstream** (Dolby, DTS, TrueHD, DTS-HD) verso l'amplificatore oppure PCM forzato; "Auto" decide caso per caso.
- **Tracce audio**: scelta dal menu e dal telecomando.
- **Ritardo audio** regolabile al volo, a passi di 10 e 100 ms.
- **Audio esterno**: si può affiancare al film un file audio separato; la sincronizzazione viene trovata da sola confrontando le due tracce e resta salvata per quel film.
- **Analisi audio in tempo reale**: livelli, picco reale, correlazione, bilanciamento, ampiezza stereo; con il bitstream attivo l'analisi lavora su una decodifica parallela.

## 6. Sottotitoli

- **Tracce interne ed esterne**, scelta automatica (solo forzati) o manuale, XySubFilter con DirectShow.
- **Ricerca e scaricamento** dal player: YIFY Subtitles per i film e Addic7ed per le serie senza account; **Subdl per film e serie (3.6)** con la chiave gratuita del proprio profilo; OpenSubtitles con il proprio account.
- **34 lingue** nella ricerca, con lingua predefinita a scelta.
- **Riallineamento automatico**: il file scaricato viene messo a tempo ascoltando l'audio del film; si può rifare su un sottotitolo già presente.
- **Stile del testo e posizione** dalle impostazioni.

## 7. Libreria

- **Sezioni**: Home, Film, Serie TV, Musica, Foto, Video, Playlist, Rete.
- **Cartelle sorgenti** per i film e gli altri contenuti, aggiunte e tolte dall'interfaccia.
- **Metadati da TMDb**: locandine, sfondi, trama, generi, cast, registi, episodi delle serie. Serve la propria chiave TMDb, gratuita, da inserire in Impostazioni › Generale › Metadati **(3.6)**.
- **Correzione manuale**: titolo, anno, ricerca su TMDb e scelta della locandina tra quelle disponibili o da un file proprio.
- **Scheda del titolo**: dettagli tecnici del file, cast, recensioni e voti di TMDb, IMDb, Letterboxd e Metacritic.
- **Raccolte e filtri**: per genere, anno, decennio, regista, artista, cartella; ordinamenti per nome, data, durata, dimensione; preferiti, recenti, in corso, valutati.
- **Ricerca globale** dalla Home su tutta la libreria.
- **Continua a guardare e Diario** di ciò che è stato visto, con rimozione delle singole voci.
- **Playlist** di video, musica, foto o miste, e **coda di riproduzione** modificabile.
- **Rapporto della libreria**: quanti titoli, quanto spazio, quali formati, più i casi da controllare (doppioni, risoluzione o bitrate bassi, HDR con metadati incoerenti, file che non si aprono).
- **Tema chiaro, scuro o come Windows**, colore d'accento e tavolozza personalizzabile.

## 8. Spotlight e modalità cinema

- **Spotlight**: interfaccia a tutto schermo in stile sala, con sfondi grandi, scelta tra libreria del PC e server di rete, ricerca e cast; può aprirsi all'avvio.
- **Modalità cinema**: schermata segnaposto prima del film e filmato demo prima della proiezione, scelti dall'utente; lo sfondo può arrivare da TMDb.

## 9. Rete: DLNA, Jellyfin, Emby

- **DLNA**: ricerca automatica dei server, navigazione e riproduzione.
- **Jellyfin**: ricerca in rete, accesso con nome e password oppure con Quick Connect, catalogo con i metadati del server, riproduzione del file originale, punto di ripresa e stato "visto" allineati con gli altri dispositivi.
- **Emby (3.6)**: stesse funzioni di Jellyfin tranne Quick Connect, che Emby non ha.
- **Qualità ridotta**: con un limite di bitrate il server converte il video al volo; vale per Jellyfin e per Emby.
- **Token di accesso** salvati cifrati e mai scritti nei registri.

## 10. Musica

- **Cinecore Audio Engine**: motore proprio per la musica.
  - Uscita WASAPI condivisa, esclusiva o bit perfect.
  - Equalizzatore grafico a 10 o 31 bande oppure parametrico, con preset.
  - Protezione dal clipping (margine o limitatore) con soffitto regolabile.
  - Crossfeed per le cuffie, ampiezza stereo, bilanciamento, mono, loudness.
  - ReplayGain per brano o per album, con preamplificazione.
- **Dissolvenza tra i brani** (3, 6 o 10 secondi), esclusa tra brani consecutivi dello stesso album.
- **Gapless (3.6)**: i brani consecutivi si attaccano senza pausa, anche in esclusivo e bit perfect.
- **Area musica**: copertina, foto dell'artista, coda, preferiti, mini player.
- **Testi**: ricerca automatica (LRCLIB, Genius, lyrics.ovh), testi sincronizzati quando disponibili, sincronizzazione automatica con riconoscimento dell'audio se è installato il modulo facoltativo.
- **Foto degli artisti e copertine**: dal disco, poi da Spotify, TIDAL o Deezer (solo immagini e metadati, nessuno streaming).
- **Radio**: a fine coda aggiunge brani simili presi dalla propria libreria, senza servizi esterni.
- **Ripeti brano**, riproduzione casuale, avanzamento dell'album ricordato.
- **Ascolti verso Last.fm e ListenBrainz (3.6)**: invio dei brani ascoltati, con coda per quando manca la rete.

## 11. Foto

- **Visualizzatore**: zoom, rotazione, presentazione, primo e ultimo scatto.
- **Modifica**: penna, evidenziatore, linea, freccia, rettangolo, ellisse, testo, filtri, colore preso dalla foto, annulla; salva sempre una copia.
- **Azioni**: copia negli appunti, condividi con il pannello di Windows, stampa, apri con un'altra app, sposta nel Cestino.

## 12. Analisi HDR

- **Metadati dichiarati**: schermo di mastering, MaxCLL, MaxFALL, Dolby Vision, HDR10+.
- **Misura reale sul film**: picco e media in nit lungo tutta la durata, percentili, distribuzione della luminosità, quanta immagine esce da Rec.709 e da DCI-P3.
- **Tempo reale**: forma d'onda RGB, vettorscopio, cromaticità CIE 1931, falsi colori.
- **Esportazione** dell'analisi su file.

## 13. Telecomando dal telefono

- **Pagina web servita dal player**: si abbina inquadrando un codice QR, senza installare nulla; si può aggiungere alla schermata Home del telefono.
- **Comandi**: play, pausa, stop, salti, scan veloce, capitoli, volume, schermo intero, frecce e OK, tastiera per scrivere sul PC.
- **Tracce**: audio e sottotitoli.
- **Libreria e coda** dal telefono, con scelta tra PC e server di rete.
- **Impostazioni (riordinate nella 3.6)**: Riproduzione, Schermo, Player, Componenti avanzati, Telecomando. Comprendono renderer, HDR, 3D, upscaling, qualità Jellyfin/Emby, avvio a schermo intero, tema, lingua e le pagine complete dei componenti.
- **Personalizzazione (3.6)**: ordine e presenza dei blocchi, colore, pagina iniziale, vibrazione al tocco.
- **Proposta di abbinamento** al primo avvio del player.

## 14. Dispositivi di casa

- **Amplificatore via rete**: volume e mute del ricevitore dal player, con limiti di sicurezza (mai più di 3 dB per comando, tetto massimo, avvio al livello usato l'ultima volta).
- **Luci WLED**: si spengono o si abbassano durante la riproduzione.
- **Automazioni**: all'avvio, in pausa, alla ripresa, allo stop e ai titoli di coda il player può inviare una richiesta HTTP (GET o POST) o un messaggio MQTT, per esempio a Home Assistant.

## 15. Servizi collegati e aggiornamenti

- **Trakt**: collegamento con codice, consigli personali, invio di ciò che si è visto e dei voti.
- **Aggiornamento del player** dalle release di GitHub, con verifica dell'impronta dell'installer; controllo all'avvio facoltativo.
- **Componenti esterni**: versioni e aggiornamenti di yt-dlp, LAV Filters e MakeMKV dall'interno del player.
- **Avvio con Windows** facoltativo.
- **Italiano e inglese** in tutta l'interfaccia.
- **Chiavi e token**: mai in chiaro nei file o nei registri. Nel programma ci sono solo le chiavi dell'applicazione (Trakt e Last.fm); quelle legate a un account personale (TMDb, Subdl, OpenSubtitles, Spotify, TIDAL) le inserisce ogni utente e restano nel suo profilo di Windows, cifrate.

## 16. Scorciatoie da tastiera

| Tasto | Azione |
|---|---|
| Spazio | Play / pausa (in foto: presentazione) |
| ← → | Indietro / avanti di 10 secondi (in foto: precedente / successiva) |
| `,` `.` | Fotogramma precedente / successivo |
| Pag↑ Pag↓ | Capitolo successivo / precedente |
| ↑ ↓ | Volume, e oltre il 100% l'amplificazione |
| F | Schermo intero |
| Maiusc+F | Schermo intero esteso |
| Z | Dimensionamento dell'immagine |
| Ctrl + / Ctrl − | Ritardo audio di 10 ms (con Maiusc: 100 ms) |
| Ctrl 0 | Azzera il ritardo audio |
| O | Apri file |
| S | Chiudi e torna alla libreria |
| Esc | Indietro / esci dallo schermo intero |
| In foto: + − 0 R | Zoom, vista iniziale, rotazione |
| In foto: Inizio / Fine | Prima / ultima foto |
