# Cinecore Player 2026 per Windows — documento tecnico

Versione di riferimento: Beta 4 (0.4.0), novembre 2026.

Questo documento descrive cosa fa il player, come è costruito e perché è costruito così. Per ogni area ci sono le
funzioni, la soluzione tecnica e la scelta che c'è dietro, con le alternative scartate.

---

## 1. In breve

Cinecore Player è un player per l'home cinema: film e serie con renderer di qualità (madVR, MPC Video Renderer, mpv),
una libreria con metadati, la musica con un motore audio proprio, il telecomando dal telefono e il controllo
dell'ampli via rete.

| | |
|---|---|
| Linguaggio e runtime | C# su .NET 9 |
| Interfaccia | Avalonia 11.3, controlli disegnati a mano, condivisa con la versione macOS |
| Video | madVR o MPC Video Renderer (DirectShow, in un processo separato) oppure libmpv |
| Audio musica | Motore proprio: decodifica FFmpeg, catena DSP a doppia precisione, uscita WASAPI |
| Piattaforma | Windows 10 2004 o successivo, x64 |
| Licenza | PolyForm Noncommercial 1.0.0 (codice disponibile, uso non commerciale) |

---

## 2. Architettura

```
CinecorePlayer2026.exe ─ interfaccia Avalonia, libreria, motore musicale, libmpv, server del telecomando
 │
 ├─ VideoHost.exe ─────── madVR / MPC Video Renderer / DVD Navigator (DirectShow + Direct3D 11)
 │                        comandi su stdin, stato su stdout
 ├─ IntroOutroDetector ── impronte audio degli episodi per trovare sigle e titoli di coda
 ├─ ffmpeg / ffprobe ──── analisi, bitrate dal vivo, estrazione audio
 ├─ yt-dlp ────────────── indirizzi di YouTube e degli altri siti
 └─ python (facoltativo)  faster-whisper per sincronizzare i testi
```

### 2.1 Un codice, due sistemi

**Cosa:** l'applicazione vive in `mac-native/CinecoreMac` ed è la stessa del Mac. Il progetto Windows
(`win-native/CinecoreWin`) compila quei sorgenti con il simbolo `CINECORE_WINDOWS`, esclude una lista di file solo-Mac
(`MacOnlyFiles` nel `.csproj`) e mette al loro posto `Platform/*.Win.cs`, con le stesse classi e gli stessi metodi:

| Servizio | Mac | Windows |
|---|---|---|
| Finestre, vetro, angoli | AppKit, NSVisualEffectView | Win32, DWM (attributi di angolo, bordo, cloak, acrilico) |
| Uscita audio | CoreAudio AudioQueue | WASAPI (NAudio), anche in modo esclusivo |
| Segreti | Portachiavi | DPAPI dell'utente, in `credentials.json` |
| Frequenza dello schermo | CoreGraphics | CCD (`SetDisplayConfig`) con frequenze razionali |
| Fotogrammi per anteprime e analisi | ffmpeg con VideoToolbox | libmpv nascosto con decodifica della GPU |
| Tasti multimediali, condivisione | MediaPlayer, NSSharingService | SMTC, pannello Condividi di Windows |

**Perché:** una sola interfaccia da mantenere. Ogni funzione nuova arriva su tutti e due i sistemi, e le differenze
restano confinate in pochi file con la stessa forma.

**Scartato:** due interfacce separate (WinForms su Windows, la strada della 0.3.6), che divergevano a ogni versione.

### 2.2 Il video in una finestra a parte, sotto l'interfaccia

**Cosa:** il video non è disegnato dentro Avalonia. Sta in una finestra Win32 separata, del VideoHost o del nostro
processo per libmpv, tenuta sempre subito sotto la finestra del player (`WinVideoStack.KeepBelow`). La finestra del
player è trasparente dove si vede il film (composizione di Windows), e HUD, menu e vetro stanno sopra.

**Perché:**
- madVR e MPC Video Renderer vogliono la loro finestra e il loro Direct3D: copiarne i fotogrammi in Avalonia
  costerebbe latenza, banda e la loro gestione di HDR e frequenza.
- L'interfaccia si ridisegna senza toccare la presentazione del video, quindi niente scatti nel film quando si apre
  un menu.

**Costo:** la sincronizzazione di posizione, misura, ordine e angoli fra due finestre. Se ne occupa un controllo che
guarda l'ordine delle finestre e lo ripara.

### 2.3 VideoHost: il renderer in un processo separato

**Cosa:** `VideoHost.exe` costruisce il grafo DirectShow (LAV Splitter, LAV Video, madVR o MPC-VR, XySubFilter) e
riceve comandi di testo su stdin (`play`, `pause`, `seek`, `rect`, `volume`, `key`, `quit`). Risponde su stdout
(`ready`, `pos`, `error`, stato del DVD).

**Perché:**
- Un blocco o un crash del renderer non porta giù il player: si chiude il processo e si riapre.
- DirectShow e madVR hanno esigenze di thread e COM che non convivono bene con l'interfaccia.
- È lo stesso programma che sul Mac gira sotto Wine: un solo VideoHost per i due sistemi.

### 2.4 Componenti inclusi nel pacchetto

**Cosa:** madVR, MPC Video Renderer, XySubFilter, libmpv, FFmpeg, yt-dlp, libaacs e Rubber Band stanno in
`third-parties\` accanto all'eseguibile. madVR, MPC-VR e XySubFilter si registrano da lì.

**Perché:** prima l'app usava la copia registrata in Windows. Se era vecchia o mancava, o se si spostava la cartella,
i video non si aprivano. Con i componenti nel pacchetto la versione è nota e l'app funziona ovunque venga messa.

### 2.5 Interfaccia disegnata a mano

**Cosa:** le schermate sono controlli che disegnano direttamente su `DrawingContext` (libreria, HUD, schede, mini
player, PiP), con una lista di aree cliccabili ricostruita a ogni disegno. Le animazioni seguono il ritmo dello
schermo; il vetro è un'operazione di disegno personalizzata che sfoca ciò che sta già sotto, nello stesso fotogramma
(`BackdropGlass`).

**Perché:**
- Controllo totale sull'aspetto, identico sui due sistemi, e prestazioni prevedibili con librerie di migliaia di
  locandine.
- Il vetro precedente era una copia della pagina rifatta 220 ms dopo ogni cambio: arrivava in ritardo e cambiava di
  colpo.

### 2.6 Compilazione ReadyToRun

**Cosa:** la release è compilata in codice macchina in anticipo (`PublishReadyToRun`), con PGO a livelli.

**Perché:** senza, la compilazione JIT al primo uso di ogni schermata faceva scattare l'interfaccia all'avvio e alla
prima apertura dell'HUD.

---

## 3. Video

### 3.1 Motori

| Motore | Quando usarlo |
|---|---|
| madVR | Massima qualità: scaling, tone mapping HDR, gestione della frequenza. |
| MPC Video Renderer | Leggero e moderno. Supporta **RTX Video Super Resolution** (SD, 720p, 1080p o 1440p) e **RTX Video HDR** sulle GPU NVIDIA RTX. |
| mpv | Universale, il più rapido ad aprire. Usato anche per YouTube e per i flussi di rete. |

Il motore si sceglie per film. Ogni motore ha le sue impostazioni (Impostazioni › Componenti), che arrivano al
VideoHost come voci di registro, come se le avesse scritte il pannello del componente.

### 3.2 Frequenza dello schermo

**Cosa:** se la frequenza dello schermo non è un multiplo esatto della frequenza del film (24 fps su 60 Hz), lo
schermo passa al multiplo più alto disponibile alla stessa risoluzione, senza superare quella attuale. Alla chiusura
torna com'era.

**Scelta tecnica:** le API CCD di Windows con frequenze razionali (23,976 = 24000/1001), invece di
`ChangeDisplaySettings`, che arrotonda agli interi e trasforma 23,976 in 23 o 24 Hz, con uno scatto ogni 40 secondi.

### 3.3 Dimensionamento dell'immagine

A schermo intero si può tenere l'altezza costante, l'area costante o una misura personalizzata. Il formato reale del
film (le bande nere incise nel video) si misura ogni due secondi su un fotogramma piccolo, e le scene IMAX si
allargano quando il formato cambia. Al motore arriva solo un fattore di scala.

### 3.4 Anteprima sulla barra del tempo e analisi HDR

**Cosa:** una striscia di cinque fotogrammi (sotto il puntatore e ±10 e ±20 secondi) e la misura della luminosità
del film (analisi HDR/SDR).

**Scelta tecnica:** un libmpv nascosto (`MpvFrameGrabber`), senza finestra né audio, tiene il film aperto in pausa con
il decoder della scheda video pronto, e restituisce fotogrammi già ridotti.

**Perché:** prima ogni passaggio avviava un FFmpeg nuovo che riapriva il film e decodificava con la CPU.

| Operazione | Prima (FFmpeg) | Ora (GPU) |
|---|---|---|
| Fotogramma di anteprima, 4K HEVC 10 bit | 300–450 ms | circa 50 ms |
| Analisi HDR, 480 campioni | circa 46 s | circa 14 s |
| Misura dal vivo durante la visione | decodifica 4K sulla CPU | 60–100 ms |

I valori dell'analisi coincidono al pixel con quelli di FFmpeg.

### 3.5 Bitrate dal vivo

Il pannello Info mostra il bitrate reale di video e audio: ffprobe legge i pacchetti nei due secondi dopo la
posizione di riproduzione e ne somma le dimensioni. Funziona per file, YouTube (video e audio sono due indirizzi
separati), DLNA e Jellyfin. **Scelta:** si misura solo mentre il pannello è aperto, quindi a pannello chiuso non costa
niente.

### 3.6 Dischi

- **DVD:** il DVD Navigator di DirectShow nel VideoHost. I pulsanti dei menu sono disegnati sopra il video e
  allineati al fotogramma; si comandano anche dal telecomando.
- **Blu-ray e ISO:** libbluray con libaacs e libbdplus, oppure la libreria di MakeMKV se è installata.
- **Scelta:** nessuna chiave di decifrazione è inclusa. Il player usa solo ciò che l'utente ha già sul suo sistema.

### 3.7 3D

Il formato si riconosce da solo (Side-by-Side o Top/Bottom) dal nome, dai metadati e dalle proporzioni. Si può
lasciare l'immagine doppia per un proiettore 3D o mostrare un occhio solo.

### 3.8 Sigle e titoli di coda

**Cosa:** "Salta intro" e "Prossimo episodio".

**Come:** `IntroOutroDetector` è un processo separato che confronta gli episodi della stessa stagione.
1. FFmpeg estrae l'audio mono a 8 kHz dei primi e degli ultimi minuti di ogni episodio.
2. L'audio viene diviso in blocchi da 512 ms, ognuno trasformato con una FFT in 32 bande.
3. Da ogni blocco si ricava un'impronta a 32 bit, ispirata a Chromaprint ma molto più leggera.
4. Le sequenze di impronte che si ripetono fra gli episodi sono la sigla e i titoli di coda.

**Perché così:** non servono database esterni né servizi online, e una stagione di 10–20 episodi si analizza in
pochi minuti.

### 3.9 Sincronia audio

Ritardo audio fisso o "a curva", cioè che cambia durante il film (utile per i rip con deriva). Per un audio esterno,
per esempio un doppiaggio da un'altra fonte, l'allineamento è automatico per correlazione delle due tracce.

### 3.10 Schermo intero, PiP e mini player animati

**Problema:** le finestre di Windows cambiano misura a scatti, e la finestra del video (di un altro processo) non si
può animare insieme all'interfaccia. Il risultato era un lampo nero o un salto.

**Soluzione:**
1. Si fotografa il monitor e si mostra la foto in una finestra a parte, ferma.
2. Le finestre vere vengono nascoste a DWM (cloak) e cambiano misura sotto la foto.
3. La foto si anima fino alla posizione nuova (450 ms, easing cubico). Dentro c'è il fotogramma vero del video,
   catturato con `PrintWindow`.
4. Arrivata la foto, le finestre vere ricompaiono sotto di lei e la foto sfuma.

La finestra del video di madVR aspetta fuori schermo, già alla misura giusta, finché la foto non arriva.

### 3.11 Picture in Picture e mini player

- **PiP:** una finestra propria senza cornice di sistema, sempre in primo piano, con il video a sinistra e i comandi
  completi. Il player si riduce a icona invece di cambiare misura, così torna esattamente com'era (in finestra,
  ingrandito o a schermo intero).
- **Mini player della musica:** la finestra principale stessa, rimpicciolita, senza cornice, con il vetro acrilico di
  Windows. Ricorda misura e posizione. Un controllo la riporta a finestra intera se la musica si ferma, parte un video
  o la finestra viene ingrandita.

---

## 4. YouTube e web

- **yt-dlp** ricava titolo e indirizzi diretti. Spesso video e audio sono separati, e mpv li suona insieme.
- **Problema:** YouTube rifiuta una parte dei flussi, e quali cambia da un video all'altro.
- **Soluzione:** prima di aprire, il player prova i flussi e sceglie il migliore che risponde. L'audio è sempre quello
  di qualità più alta.
- **Qualità:** i nomi sono normalizzati (2160p, 1440p, 1080p…) anche per i formati cinema. Il cambio di qualità non
  rilegge il video da YouTube e riprende dallo stesso punto.
- yt-dlp si aggiorna da GitHub, perché YouTube cambia spesso e una versione vecchia smette di funzionare.

---

## 5. Libreria e metadati

- **Contenuti:** film, serie (stagioni ed episodi), musica, foto e cartelle. Titolo, anno, stagione ed episodio si
  riconoscono dal nome del file.
- **Metadati:** TMDb per trame, locandine, sfondi, cast e consigliati. Recensioni a testo intero da TMDb, Letterboxd e
  Metacritic, più voti esterni.
- **Sorgenti:** dischi locali e cartelle di rete, DLNA, Jellyfin ed Emby (un unico client per i due), link diretti e
  rclone.
- **Continua a guardare:** il punto di ripresa viene salvato per ogni file, con la cronologia delle visioni e un
  diario con voti e date.
- **Trakt:** sincronizzazione di cronologia e libreria.
- **Playlist** di film, serie e musica; si importano anche i file `.wpl`.
- **Spotlight:** ricerca veloce in tutta la libreria.
- **Scelta:** le chiavi personali dei servizi (TMDb, OpenSubtitles, Subdl) le inserisce l'utente e restano cifrate nel
  suo profilo. Le uniche chiavi integrate sono quelle dell'applicazione per Trakt e Last.fm.

---

## 6. Sottotitoli

- Tracce interne ed esterne. Con madVR e MPC-VR il rendering è di XySubFilter (qualità ASS/SSA completa).
- Ricerca e download da OpenSubtitles e Subdl.
- **Allineamento automatico:** i sottotitoli vengono allineati al parlato e si può verificarne la sincronia.
- I nomi delle tracce sono ripuliti e normalizzati (lingua, forzati, SDH).

---

## 7. Musica

### 7.1 Motore audio proprio

**Cosa:** la musica non passa da un lettore di sistema ma da una catena propria:
1. FFmpeg decodifica ogni brano in float stereo a 48 kHz.
2. La catena DSP lavora in doppia precisione: EQ grafico a 10 o 31 bande oppure parametrico, limiter true-peak,
   crossfeed, ampiezza stereo e bilanciamento, loudness e AutoEQ dinamico.
3. L'uscita è WASAPI condivisa, oppure **esclusiva** (bit perfect) se il dispositivo la accetta.
4. Il brano successivo si prepara in anticipo, per passare senza pause (gapless) o con la dissolvenza incrociata.

**Perché:** gapless, automix e DSP richiedono il controllo dei campioni, che un lettore di sistema non dà. Su Mac e
Windows cambia solo l'ultimo pezzo (AudioQueue o WASAPI): sopra c'è una piccola libreria con le stesse chiamate, così
il motore è identico.

**AutoEQ dinamico:** compensa la curva di sensibilità dell'orecchio secondo il livello d'ascolto reale, cioè il
volume dell'ampli (0 dB di riferimento) sommato a quello di Windows.

### 7.2 Automix e mix manuale

- **Analisi:** beat e battute, struttura e sezioni del brano, tonalità.
- **Transizioni:** si sceglie il punto di uscita e di ingresso, si allinea la fase dei beat e si applica una curva
  "da DJ" (diverse tecniche). Se i BPM sono diversi si adatta il tempo con **Rubber Band** senza cambiare
  l'intonazione.
- **Mix manuale:** un editor con le onde dei due brani, dove si regolano tonalità, BPM, punti di uscita e ingresso e
  tempo. Il mix si salva per quella coppia di brani.

### 7.3 Radio

Quando la coda finisce, si allunga con brani della libreria che stanno bene dopo l'ultimo: lo stesso artista, chi ha
suonato negli stessi dischi, anni vicini, la stessa raccolta, i preferiti, i brani ascoltati fino in fondo. Pesano
contro i brani saltati. **Scelta:** niente servizi esterni, funziona offline e solo con la musica dell'utente.

### 7.4 Testi

- Testi sincronizzati da più fonti, tra cui Genius.
- **"Sincronizza il testo":** se un testo non è sincronizzato (o è sbagliato), il player riconosce la voce con
  **faster-whisper** (script Python in `LyricsSynchronization`) e allinea le righe al canto. Regge anche le
  ripetizioni del riconoscimento e i ritornelli scritti una volta sola.

### 7.5 Il resto

- Copertine e foto degli artisti da più servizi, con sfondi generati dai colori della copertina.
- Scrobbling su Last.fm e statistiche di ascolto.
- VU meter e grafici del segnale dal vivo.

---

## 8. Home cinema e casa

### 8.1 Ampli via rete

**Protocolli:** Denon/Marantz, Yamaha MusicCast, Onkyo/Integra/Pioneer, NAD, Anthem, UPnP.

**Regole di sicurezza** (fisse nel codice, non configurabili):
- si legge il volume attuale prima di mandarne uno;
- in salita al massimo +3 dB per comando: un salto grande diventa una scala di passi;
- tetto a −20 dB, che nessun comando supera;
- all'avvio di un film il volume si può solo abbassare, mai alzare;
- il muto messo durante un film viene tolto quando il film si chiude.

**Perché:** un errore di volume su un impianto home cinema danneggia altoparlanti e udito. Le regole valgono per ogni
fonte: player, telecomando, musica.

**Volume per film:** ogni film ricorda il volume a cui è stato visto.

### 8.2 Automazioni

Gli eventi del player (avvio, pausa, ripresa, fine, titoli di coda) vengono inviati a **MQTT** o a un **webhook di
Home Assistant**. **Scelta:** seguono la sessione e non il singolo file, quindi un brano dopo l'altro non genera una
fine e un nuovo avvio (le luci non lampeggiano fra due canzoni).

### 8.3 Luci WLED

Collegamento diretto alle strisce LED WLED, con lo stato verificato dal vivo mentre si scrive l'indirizzo.

---

## 9. Telecomando dal telefono

- **Server:** un server HTTP minimo su `TcpListener` dentro il player, che serve una pagina web completa. Non serve
  installare app sul telefono.
- **Scelta:** `TcpListener` invece di `HttpListener`, perché quest'ultimo richiede di registrare l'indirizzo in
  Windows (URLACL) con i diritti di amministratore.
- **Indirizzo facile:** un responder **mDNS** pubblica un nome univoco per il PC (`….local`). Un proxy sulla porta 80
  permette di scrivere l'indirizzo senza `:porta`, quando la porta è libera.
- **Abbinamento:** un codice mostrato sul player o un QR. Il dispositivo resta abbinato con un token (cookie
  HttpOnly, localStorage e header `Authorization`), così sopravvive alla pulizia di una delle tre cose.
- **Funzioni:** riproduzione, trackpad con scorrimento naturale e inerzia, coda della musica, impostazioni del player
  (gli stessi menu della finestra), menu dei DVD.
- **Pagina PC:** CPU, memoria, GPU (nvidia-smi), dischi, rete, batteria, consumi e temperature. **Scelta:** il
  campionamento gira solo mentre qualcuno guarda la pagina.

---

## 10. Foto

Visore con miniature, zoom, rotazione, presentazione ed editor di modifica. Da Windows si condivide (pannello
Condividi), si copia, si apre con un'altra app, si stampa e si elimina (nel Cestino).

---

## 11. Integrazione con Windows

- **Istanza unica:** un file aperto da Esplora file mentre il player è già aperto arriva alla finestra esistente.
- **"Apri con":** Cinecore si registra come candidato per video, musica e foto, con le sue icone
  (`CinecoreShellIcons.dll`). **Scelta:** non cambia mai l'app predefinita scelta dall'utente.
- **Finestra:** angoli arrotondati e ombra di DWM anche senza cornice di sistema; squadrati a schermo intero e da
  ingrandita.
- **Risparmio energetico:** con il risparmio batteria di Windows acceso, il player propone (o usa, se impostato) un
  renderer più leggero di madVR per la sessione.
- **Aggiornamenti:** il player legge le release su GitHub (tag `vX.Y.Z`) e scarica l'installer. Prima di avviarlo ne
  confronta l'impronta SHA-256 con quella pubblicata da GitHub, poi lo avvia in modalità aggiornamento silenziosa.
- **Lingue:** italiano, inglese e spagnolo. L'app è scritta in italiano e i testi si traducono al momento del disegno
  da `Lang/strings.tsv`, così la logica non dipende dalla lingua.

---

## 12. Dati e privacy

| Cosa | Dove |
|---|---|
| Impostazioni (solo scelte, mai segreti) | `%APPDATA%\Cinecore\settings.json` |
| Chiavi e password dell'utente | `%APPDATA%\Cinecore\credentials.json`, cifrato con DPAPI (leggibile solo dal suo account Windows) |
| Libreria, punti di ripresa, playlist | `%APPDATA%\Cinecore` |
| Cache delle immagini e delle analisi | `%LOCALAPPDATA%\Cinecore\Cache` |
| Log delle sessioni | `%LOCALAPPDATA%\Cinecore\Logs` |

- Le chiavi vengono tolte dagli indirizzi prima di finire nei log.
- Le chiavi dell'applicazione (Trakt, Last.fm) sono cifrate AES-256-GCM nel programma. Nel codice pubblico mancano: si
  aggiungono in `SecretVault.Keys.cs`, escluso dal repository.
- Nessuna telemetria. Le connessioni in uscita sono solo verso i servizi che l'utente usa (metadati, sottotitoli,
  testi, YouTube, Trakt, Last.fm, aggiornamenti).

---

## 13. Build e distribuzione

- **Sviluppo:** `dotnet build` in `win-native/CinecoreWin`. VideoHost e IntroOutroDetector si compilano da soli
  accanto all'app.
- **Release:** `dotnet publish -c Release` (ReadyToRun). I componenti di `third-parties` vengono copiati nel pacchetto.
- **Installer:** Inno Setup (`installer/Build-Installer-Avalonia.ps1`).
  - Runtime .NET incluso, licenza da accettare, italiano o inglese secondo il sistema.
  - Componenti di terze parti facoltativi (LAV Filters, decoder e renderer audio MPC con i loro installatori
    originali).
  - Disinstallazione che deregistra anche i filtri.
- **Prove automatiche:** il player accetta uno script di prova (`CINECORE_QA_SCRIPT`) che apre file, cambia schermate
  e scrive lo stato nel log. Si usa per verificare animazioni e transizioni con catture dello schermo.

---

## 14. Limiti noti

- Un video 4K di YouTube richiede ancora 20–25 secondi per aprirsi: circa metà del tempo è di yt-dlp.
- Con madVR lo schermo può diventare nero per un momento durante il caricamento (inizializzazione del renderer).
- Il PiP mostra il video senza vetro dietro, perché l'acrilico di Windows sfocherebbe anche il video.
- La sincronizzazione dei testi richiede Python con faster-whisper installato a parte.
