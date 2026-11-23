# Icone personalizzate di Cinecore

Metti gli SVG in questa cartella, con i nomi indicati sotto, e riavvia il player. Nella versione compilata la cartella è `Assets/Icons.Custom` accanto a `CinecorePlayer2025.exe`. Nel progetto i file vengono copiati nelle build successive automaticamente.

Usa un solo set coerente: SVG quadrati, sfondo trasparente, segno bianco o nero monocromatico e senza cerchi o riquadri di sfondo. Il set integrato attuale è Material Symbols Rounded, peso leggero 200: contorni sottili e simboli minimal. Il programma applica colore, hover e selezione. Rispetta la licenza del set scaricato.

Ogni funzione ha un file canonico: `movie.svg` vale anche per film/video/cinema, `star.svg` per tutti i preferiti, `playlist.svg` per tutte le playlist, `wave.svg` per l'audio. Usa questi nomi per sostituire il simbolo in tutte le sezioni contemporaneamente; gli alias non selezionano varianti separate.

I file sono facoltativi: quelli mancanti continuano a usare le icone integrate. Non occorre modificare codice o impostazioni. I pulsanti mantengono la propria funzione: cambiare SVG cambia soltanto il simbolo. Le icone vengono memorizzate durante l'esecuzione, quindi riavvia dopo una sostituzione.

## Riproduzione, barra musicale, mini player e coda

Priorità per questa revisione: `play`, `pause`, `previous`, `next`, `shuffle`, `repeat-one`, `volume`, `mute`, `star`, `playlist`, `more`, `queue`, `queue-add`, `wave`, `list`, `library`, `pip`. Per il remote aggiungi `skip-back`, `skip-forward`, `maximize`, `subtitles`, `keyboard`, `power`, `eject`.

| Nome file | Cosa deve rappresentare | Posizione / funzione |
|---|---|---|
| play.svg | Triangolo verso destra | Riproduci |
| pause.svg | Due barre verticali | Pausa |
| stop.svg | Quadrato pieno | Interrompi |
| previous.svg | Triangolo a sinistra con barra | Brano precedente |
| next.svg | Triangolo a destra con barra | Brano successivo |
| skip-back.svg | Freccia di riavvolgimento | Salto indietro video |
| skip-forward.svg | Freccia di avanzamento | Salto avanti video |
| shuffle.svg | Due frecce incrociate | Riproduzione casuale |
| repeat-one.svg | Frecce circolari con 1 | Ripeti brano |
| volume.svg | Altoparlante con onde | Volume |
| volume-low.svg | Altoparlante con un'onda | Volume basso |
| volume-high.svg | Altoparlante con tre onde | Volume alto |
| mute.svg | Altoparlante barrato | Audio disattivato |
| star.svg | Stella | Preferito, anche in musica e Home |
| more.svg | Tre punti orizzontali | Altre azioni |
| queue.svg | Tre righe orizzontali | Apri coda |
| queue-add.svg | Righe con segno più | Aggiungi alla coda |
| playlist.svg | Lista con nota musicale | Playlist, coerente con Home |
| playlist-add.svg | Stessa lista con nota musicale | Alias playlist |
| wave.svg | Forma d'onda / barre audio | Apri analisi audio |
| list.svg | Fumetto con righe | Testo della canzone nella barra e nel mini player |
| library.svg | Dorsi di dischi o libri | Torna alla libreria |
| pip.svg | Schermo con piccolo rettangolo interno | Mini player |
| maximize.svg | Quattro angoli aperti | Schermo intero |
| close.svg | X semplice | Chiudi pannello / rimuovi dalla coda |
| eject.svg | Triangolo sopra una barra | Espelli / chiudi media |

## Libreria e navigazione

| Nome file | Cosa deve rappresentare | Posizione / funzione |
|---|---|---|
| home.svg | Casa | Home della libreria |
| home-2.svg | Casa | Pulsanti Home secondari |
| grid.svg | Griglia 2×2 | Vista a griglia |
| movie.svg | Fotogramma / pellicola | Film e video |
| tv.svg | Televisore | Serie TV / schermo |
| music.svg | Nota musicale | Categoria Musica |
| audio.svg | Barre audio | Informazioni e impostazioni audio |
| photo.svg | Paesaggio in un rettangolo | Fotografie |
| star.svg | Stella vuota | Preferiti della libreria |
| playlist.svg | Lista con nota musicale | Playlist |
| search.svg | Lente | Cerca |
| filter.svg | Tre cursori | Filtri, artista e regista |
| sort.svg | Righe con freccia verticale | Ordinamento |
| scan.svg | Freccia circolare / ricerca | Scansiona libreria |
| storage.svg | Disco rigido | Spazio e dispositivi |
| computer.svg | Computer | Sorgenti locali |
| network.svg | Nodi collegati | Rete |
| youtube.svg | Rettangolo con play | YouTube |
| cloud.svg | Nuvola | Sorgente online |
| link.svg | Due anelli collegati | Apri URL |
| clipboard.svg | Appunti | Incolla / lista |
| info.svg | i in un cerchio | Informazioni |
| add.svg | Segno più | Aggiungi |
| folder.svg | Cartella aperta o chiusa | Cartella sorgente |
| folder-plus.svg | Cartella con più | Aggiungi cartella |
| image-add.svg | Immagine con più | Aggiungi immagine |
| trash.svg | Cestino | Elimina / svuota |
| back.svg | Freccia a sinistra | Torna indietro |
| reset.svg | Freccia di ritorno | Ripristina |

## Impostazioni e informazioni

| Nome file | Cosa deve rappresentare | Posizione / funzione |
|---|---|---|
| settings.svg | Ingranaggio | Impostazioni generali |
| palette.svg | Tavolozza | Aspetto e colori |
| key.svg | Chiave | Configurazione API |
| check.svg | Spunta | Applica / conferma |
| remote.svg | Telecomando | Controlli remoti |
| spotlight.svg | Piccola stella luminosa | Spotlight |
| language.svg | Globo / lettere di lingue diverse | Lingua |
| calendar.svg | Calendario | Data / anno |
| time.svg | Orologio | Durata e cronologia |
| quality.svg | Fotogramma con dettaglio | Qualità video |
| cinema.svg | Pellicola | Opzioni cinema |
| exit.svg | Porta con freccia | Esci |
| subtitles.svg | Rettangolo con CC | Sottotitoli |
| chapters.svg | Lista numerata | Capitoli |
| keyboard.svg | Tastiera | Tastiera del remote |
| power.svg | Simbolo di accensione | Esci da Cinecore dal remote |

Le frecce del carousel, la croce delle cartelle e il pad direzionale usano tratti geometrici con dimensioni fisse: non serve scaricare immagini per questi elementi.

I nomi alternativi già usati nel programma (ad esempio `film`, `movies`, `photos`, `prev`, `gear`) ereditano l'icona principale corrispondente. Puoi anche inserire un SVG con il nome alternativo per sostituire solo quella variante. Il logo Cinecore e le copertine non fanno parte di questo set e non vanno sostituiti con queste icone.
