# Impostazioni di dmgbuild per il .dmg di Cinecore Player (mac/make-mac-release.sh le passa con -D).
# Finestra 660x420 con lo sfondo disegnato da make-background.swift: app a sinistra, Applicazioni a destra, freccia in mezzo.
app = defines["app"]
format = "UDZO"
compression_level = 9
filesystem = "HFS+"
files = [(app, "CinecorePlayer2026.app")]
symlinks = {"Applicazioni": "/Applications"}
icon = defines["icon"]
background = defines["background"]
# 420 punti di contenuto: la barra del titolo del Finder (32) si aggiunge sopra.
window_rect = ((240, 160), (660, 452))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
show_icon_preview = False
icon_size = 112
text_size = 13
icon_locations = {"CinecorePlayer2026.app": (175, 205), "Applicazioni": (485, 205)}
