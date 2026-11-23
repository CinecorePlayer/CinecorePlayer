#nullable enable

/// <summary>
/// Telecomando 2026-11-20 (rifacimento): foto a tutta larghezza, copertine dei film in coda a locandina, Extra come una
/// scheda del film con vignettatura (sincronia audio compresa), Impostazioni a indice di categorie (con Schermi e PC),
/// monitoraggio del PC e trackpad. Si aggiunge in coda alla pagina V2 e ne sostituisce le parti.
/// </summary>
internal sealed partial class RemoteServer
{
    private static string RemoteV3Addon() => """
<style id="v3style">
/* ---------- In riproduzione: foto a tutta larghezza ---------- */
#remotePage{position:relative}
#remotePage .remote-top{position:absolute!important;top:0;left:0;right:0;z-index:6;background:none!important;border:0!important;padding-top:env(safe-area-inset-top)}
/* Foto: a tutta larghezza (bordo a bordo), altezza di prima */
html body #remotePage.page .now-card.now-card{width:auto!important;max-width:none!important;margin-left:calc(50% - 50vw)!important;margin-right:calc(50% - 50vw)!important;height:clamp(150px,25dvh,240px)!important;border:0!important;border-radius:0!important;box-shadow:none!important;background:#000!important;flex-shrink:0!important}
html body #remotePage.page .now-art.now-art{position:absolute!important;inset:0!important;width:100%!important;height:100%!important;object-fit:cover!important;opacity:1!important;border-radius:0!important;display:block!important}
#remotePage .now-card:after{background:linear-gradient(0deg,var(--bg) 0%,color-mix(in srgb,var(--bg) 70%,transparent) 22%,transparent 58%)!important}
#remotePage .now-card.no-playback:after{background:var(--bg)!important}
#remotePage .now-copy{left:22px!important;right:22px!important;bottom:12px!important}
#remotePage .now-copy .title{font-size:24px!important;letter-spacing:-.02em}
#remotePage .now-actions{top:calc(48px + env(safe-area-inset-top))!important}
/* ---------- Coda: locandine per i film, quadrati per la musica ---------- */
#queueList .media-row[data-kind="movie"] .row-icon,#queueList .media-row[data-kind="movie"] .row-icon.row-art{width:48px!important;height:72px!important;border-radius:6px!important;flex:none}
#queueList .media-row[data-kind="music"] .row-icon.row-art{width:58px!important;height:58px!important;border-radius:6px!important}
#queueList .media-row[data-kind="movie"]{min-height:92px!important}
#queueList .media-row .row-icon img[src^="/api/art"]{object-fit:cover!important;width:100%!important;height:100%!important;display:block}
/* La colonna dell'immagine segue la locandina (era fissa a 46 px: copertine fuori dal riquadro, sopra il testo). */
#queueList .media-row{grid-template-columns:auto minmax(0,1fr)!important;column-gap:14px!important}
#queueList .media-row .row-icon{overflow:hidden!important}
/* ---------- Extra: scheda del film con vignettatura ---------- */
#optSheet{bottom:0!important;width:100%!important;max-width:none!important;padding:0 0 calc(110px + env(safe-area-inset-bottom))!important;background:var(--bg)!important;z-index:60!important}
#optSheet .opt-head{position:sticky!important;top:0;z-index:4;background:linear-gradient(color-mix(in srgb,var(--bg) 85%,transparent),transparent)!important;border:0!important;padding:calc(8px + env(safe-area-inset-top)) 12px 18px!important;margin:0!important}
#optSheet .opt-head strong{opacity:0;transition:opacity .2s}
#optSheet.scrolled .opt-head strong{opacity:1}
#optBody .v3hero{position:relative;height:clamp(260px,52dvh,480px);margin-top:-64px;overflow:hidden;background:#000}
#optBody .v3hero img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover}
#optBody .v3hero:after{content:"";position:absolute;inset:0;background:radial-gradient(ellipse 120% 90% at 50% 30%,transparent 35%,color-mix(in srgb,var(--bg) 70%,transparent) 80%,var(--bg) 100%),linear-gradient(0deg,var(--bg) 0%,transparent 50%)}
#optBody .v3hero .t{position:absolute;left:20px;right:20px;bottom:16px;z-index:2}
#optBody .v3hero h2{font-size:30px;line-height:1.05;margin:0 0 8px;letter-spacing:-.03em}
#optBody .v3hero small{color:color-mix(in srgb,var(--text) 68%,transparent);font-size:13px}
#optBody .v3sec{padding:0 20px}
#optBody .v3h{font-size:13px;font-weight:600;color:color-mix(in srgb,var(--text) 50%,transparent);margin:28px 0 10px}
#optBody .v3p{font-size:15px;line-height:1.5;margin:0;color:color-mix(in srgb,var(--text) 86%,transparent)}
#optBody .v3cast{display:flex;gap:14px;overflow-x:auto;padding:0 20px 4px;scrollbar-width:none}
#optBody .v3cast::-webkit-scrollbar{display:none}
#optBody .v3cast div{flex:0 0 84px;text-align:left}
#optBody .v3cast img,#optBody .v3cast i{display:block;width:84px;height:110px;border-radius:8px;object-fit:cover;background:color-mix(in srgb,var(--text) 10%,transparent);font-style:normal;line-height:110px;text-align:center;font-weight:600;color:color-mix(in srgb,var(--text) 50%,transparent)}
#optBody .v3cast b{display:block;font-size:12px;margin-top:6px;line-height:1.2}
#optBody .v3cast span{display:block;font-size:11px;color:color-mix(in srgb,var(--text) 55%,transparent);line-height:1.2}
#optBody .v3kv>div{display:flex;justify-content:space-between;gap:12px;padding:9px 0;font-size:14px;border:0}
#optBody .v3kv>div span{color:color-mix(in srgb,var(--text) 58%,transparent)}
#optBody .v3line{font-size:14px;padding:6px 0;color:color-mix(in srgb,var(--text) 82%,transparent)}
#optBody .v3chap{display:flex;gap:10px;overflow-x:auto;padding:0 20px;scrollbar-width:none}
#optBody .v3chap button{flex:none;background:color-mix(in srgb,var(--text) 8%,transparent);border:0;border-radius:999px;color:var(--text);padding:9px 14px;font-size:13px}
#optBody .v3chap button.on{background:var(--text);color:var(--bg)}
#optBody .v3link{display:block;width:100%;text-align:left;background:none;border:0;color:var(--text);font-size:15px;padding:12px 0;border:0}
/* Sincronia audio: come la vignettatura del player, numero grande e comandi senza riquadri */
.v3sync{text-align:center;padding:6px 0 4px}
.v3sync .big{display:flex;align-items:baseline;justify-content:center;gap:6px}
.v3sync input{width:150px;background:none;border:0;color:var(--text);font-size:48px;font-weight:600;text-align:center;letter-spacing:-.03em;padding:0;-moz-appearance:textfield}
.v3sync input::-webkit-inner-spin-button{display:none}
.v3sync .big span{font-size:16px;color:color-mix(in srgb,var(--text) 55%,transparent)}
.v3sync .steps{display:flex;justify-content:center;gap:12px;margin:14px 0 6px}
.v3sync .steps button{width:58px;height:58px;border-radius:50%;border:1px solid color-mix(in srgb,var(--text) 18%,transparent);background:none;color:var(--text);font-size:15px;font-weight:600}
.v3sync .steps button:active{background:color-mix(in srgb,var(--text) 12%,transparent)}
.v3sync .links{display:flex;justify-content:center;gap:28px;margin-top:10px}
.v3sync .links button{background:none;border:0;color:color-mix(in srgb,var(--text) 70%,transparent);font-size:14px;font-weight:600;padding:8px}
.v3sync .note{font-size:12px;color:color-mix(in srgb,var(--text) 50%,transparent);margin:10px 24px 0}
.v3sync .bar{height:3px;border-radius:2px;background:color-mix(in srgb,var(--text) 12%,transparent);margin:10px 60px 0;overflow:hidden}
.v3sync .bar i{display:block;height:100%;background:var(--accent)}
/* ---------- Impostazioni: indice di categorie ---------- */
#settingsPage .set-tabs{display:none!important}
html body #settingsPage.v3set>[data-cat],html body #settingsPage.v3set>[data-grp],html body #settingsPage.v3set>.setting-section,html body #settingsPage.v3set>.native-section{display:none!important}
html body #settingsPage.v3set[data-cat="play"]>[data-cat="play"],html body #settingsPage.v3set[data-cat="audio"]>[data-cat="audio"],html body #settingsPage.v3set[data-cat="screen"]>[data-cat="screen"],html body #settingsPage.v3set[data-cat="look"]>[data-cat="look"],html body #settingsPage.v3set[data-cat="adv"]>[data-cat="adv"],html body #settingsPage.v3set[data-cat="remote"]>[data-cat="remote"],html body #settingsPage.v3set[data-cat="pc"]>[data-cat="pc"]{display:grid!important}
html body #settingsPage.v3set[data-cat="adv"]>#providerList,html body #settingsPage.v3set[data-cat="remote"]>#layoutSettings,html body #settingsPage.v3set[data-cat="play"]>#upscaleDetails.open,html body #settingsPage.v3set[data-cat="pc"]>.v3pc,html body #settingsPage.v3set[data-cat="screen"]>.v3screens,html body #settingsPage.v3set[data-cat="audio"]>#syncLink{display:block!important}
html body #settingsPage.v3set[data-cat="look"]>.color-row{display:grid!important}
html body #settingsPage.v3set[data-cat="picture"]>[data-cat="picture"],html body #settingsPage.v3set[data-cat="subs"]>[data-cat="subs"],html body #settingsPage.v3set[data-cat="music"]>[data-cat="music"],html body #settingsPage.v3set[data-cat="library"]>[data-cat="library"],html body #settingsPage.v3set[data-cat="updates"]>[data-cat="updates"]{display:block!important}
html body #settingsPage.v3set[data-cat="library"]>.setting-row[data-cat="library"]{display:grid!important}
html body #settingsPage.v3set>.v3native:not(:empty){margin-top:4px}
html body #settingsPage.v3set>.v3native{display:none!important}
html body #settingsPage.v3set[data-cat="play"]>.v3native[data-cat="play"],html body #settingsPage.v3set[data-cat="picture"]>.v3native[data-cat="picture"],html body #settingsPage.v3set[data-cat="subs"]>.v3native[data-cat="subs"],html body #settingsPage.v3set[data-cat="audio"]>.v3native[data-cat="audio"],html body #settingsPage.v3set[data-cat="music"]>.v3native[data-cat="music"],html body #settingsPage.v3set[data-cat="screen"]>.v3native[data-cat="screen"],html body #settingsPage.v3set[data-cat="library"]>.v3native[data-cat="library"],html body #settingsPage.v3set[data-cat="look"]>.v3native[data-cat="look"],html body #settingsPage.v3set[data-cat="updates"]>.v3native[data-cat="updates"]{display:block!important}
#settingsPage .v3native h3,#settingsPage .v3index h3{font-size:13px;font-weight:600;text-transform:uppercase;letter-spacing:.04em;color:color-mix(in srgb,var(--text) 50%,transparent);margin:26px 0 4px}
#settingsPage .v3index h3:first-of-type{margin-top:14px}
#settingsPage .v3search{width:100%;box-sizing:border-box;margin:6px 0 2px;padding:11px 14px;border-radius:12px;border:0;background:color-mix(in srgb,var(--text) 9%,transparent);color:var(--text);font-size:16px;-webkit-appearance:none;appearance:none}
#settingsPage .v3search::placeholder{color:color-mix(in srgb,var(--text) 45%,transparent)}
#settingsPage .v3results .note{font-size:14px;color:color-mix(in srgb,var(--text) 50%,transparent);margin:18px 0}
html body #settingsPage#settingsPage .setting-row.v3hide{display:none!important}
#settingsPage .v3flash{animation:v3flash 1.6s ease}
@keyframes v3flash{0%,40%{background:color-mix(in srgb,var(--accent) 22%,transparent)}100%{background:transparent}}
#settingsPage small.err{color:#ff6b57!important}
html body #settingsPage.v3set:not([data-cat="index"])>.v3index{display:none!important}
#settingsPage .v3index{margin-top:8px}
#settingsPage .v3index button{display:block;width:100%;background:none;border:0;color:var(--text);padding:13px 0;text-align:left}
#settingsPage .v3index button i,#settingsPage .v3index em{display:none}
#settingsPage .v3index button span{flex:1;min-width:0}
#settingsPage .v3index b{display:block;font-size:19px;font-weight:600;letter-spacing:-.01em}
#settingsPage .v3index small{display:block;font-size:13px;color:color-mix(in srgb,var(--text) 52%,transparent);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
#settingsPage .v3back{display:none;background:none;border:0;color:var(--accent);font-size:15px;padding:0;margin:0 0 6px}
#settingsPage.v3set:not([data-cat="index"]) .v3back{display:block}
#settingsPage .v3screens .scr{display:flex;align-items:center;justify-content:space-between;width:100%;background:none;border:0;border:0;color:var(--text);padding:14px 2px;font-size:15px;text-align:left}
#settingsPage .v3screens .scr small{display:block;font-size:12px;color:color-mix(in srgb,var(--text) 52%,transparent);margin-top:2px}
#settingsPage .v3screens .scr .mark{color:var(--accent);font-weight:700}
#settingsPage .v3screens h3{font-size:13px;font-weight:600;color:color-mix(in srgb,var(--text) 50%,transparent);margin:26px 0 4px}
/* PC */
#settingsPage .v3pc h3{font-size:13px;font-weight:600;color:color-mix(in srgb,var(--text) 50%,transparent);margin:24px 0 8px}
#settingsPage .v3pc .m{padding:10px 0;border:0}
#settingsPage .v3pc .m .r{display:flex;justify-content:space-between;align-items:baseline;gap:10px;font-size:14px}
#settingsPage .v3pc .m .r b{font-size:20px;font-weight:600;letter-spacing:-.02em}
#settingsPage .v3pc .m .r span{color:color-mix(in srgb,var(--text) 58%,transparent)}
#settingsPage .v3pc .m .bar{height:4px;border-radius:2px;background:color-mix(in srgb,var(--text) 10%,transparent);margin-top:8px;overflow:hidden}
#settingsPage .v3pc .m .bar i{display:block;height:100%;background:var(--accent);transition:width .6s ease}
#settingsPage .v3pc .m .bar i.hot{background:#ff6b57}
#settingsPage .v3pc .note{font-size:12px;color:color-mix(in srgb,var(--text) 45%,transparent);margin-top:14px}
/* ---------- Frecce / Trackpad ---------- */
#remotePage .dpad-wrap{position:relative!important}
/* Il selettore non toglie spazio alle frecce: sta nell'angolo, fuori dal flusso */
#remotePage .v3modes{position:absolute;top:0;right:14px;z-index:3;display:flex;gap:2px}
#remotePage .v3modes button{background:none;border:0;color:color-mix(in srgb,var(--text) 40%,transparent);font-size:12px;font-weight:600;padding:6px}
#remotePage .v3modes button.on{color:var(--text)}
#settingsPage#settingsPage.v3set:not([data-cat="audio"])>#syncLink{display:none!important}
#remotePage .v3pad{display:none;position:absolute;inset:28px 8px -4px;border-radius:20px;background:color-mix(in srgb,var(--text) 7%,transparent);touch-action:none;overflow:hidden}
#remotePage.trackpad .v3pad{display:block}
#remotePage.trackpad .nav-pad{visibility:hidden!important}
/* Componenti avanzati: niente titolo doppio, niente frecce a destra */
html body #settingsPage.v3set #componentsTitle{display:none!important}
html body .provider-row img{display:none!important}
#remotePage .v3pad .btns{position:absolute;left:0;right:0;bottom:0;display:flex;height:50px}
#remotePage .v3pad .btns button{flex:1;background:none;border:0;color:color-mix(in srgb,var(--text) 62%,transparent);font-size:13px;font-weight:600}
#remotePage .v3pad .btns button:active{color:var(--text)}
#remotePage .v3pad .hint{position:absolute;inset:0 0 50px;display:grid;place-items:center;color:color-mix(in srgb,var(--text) 28%,transparent);font-size:13px;pointer-events:none;text-align:center;padding:0 24px}
/* ---------- Fogli (lingua audio, sottotitoli, tastiera): fondo pieno e uniforme, niente luce dal contenuto sotto ---------- */
html body .track-sheet.track-sheet,html body .keyboard-sheet.keyboard-sheet{background:var(--surface)!important;-webkit-backdrop-filter:none!important;backdrop-filter:none!important;background-image:none!important}
html body .track-sheet:before,html body .track-sheet:after,html body .keyboard-sheet:before,html body .keyboard-sheet:after{display:none!important}
/* ---------- Niente righe separatrici ---------- */
html body .media-row,html body .setting-row,html body .provider-row,html body .media-sheet-head,html body .track-row,html body .category-row,html body .opt-head,html body .list>*,html body #trackList>*,html body .native-section,html body .setting-section{border-top:0!important;border-bottom:0!important}
html body hr{display:none!important}
/* ---------- Libreria: freccia per uscire senza cerchio, azioni ---------- */
html body .media-sheet-head{grid-template-columns:28px minmax(0,1fr)!important}
html body .media-sheet-head button#mediaSheetBack{width:28px!important;height:34px!important;border:0!important;border-radius:0!important;background:none!important;color:var(--text)!important;font-size:32px!important;font-weight:300!important;line-height:1!important;padding:0!important;box-shadow:none!important}
.v3acts{display:flex;gap:8px;margin:2px 0 14px}
.v3acts button{flex:1;min-width:0;height:40px;border:0;border-radius:12px;background:color-mix(in srgb,var(--text) 9%,transparent);color:var(--text);font-size:13px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;padding:0 8px}
.v3acts button.main{background:var(--text);color:var(--bg)}
.media-row .v3more{flex:none;background:none;border:0;color:color-mix(in srgb,var(--text) 55%,transparent);font-size:20px;letter-spacing:1px;padding:0 4px 0 10px;line-height:1}
.media-row .chev{display:none!important}
#v3act{position:fixed;inset:0;z-index:80;display:none;background:rgba(0,0,0,.45)}
#v3act.open{display:block}
#v3act .box{position:absolute;left:0;right:0;bottom:0;margin:0 auto;width:min(440px,100%);max-height:80dvh;overflow:auto;background:var(--surface);border-radius:22px 22px 0 0;padding:18px 18px calc(18px + env(safe-area-inset-bottom))}
#v3act h4{margin:0 0 6px;font-size:14px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:color-mix(in srgb,var(--text) 55%,transparent)}
#v3act .box button{display:block;width:100%;text-align:left;background:none;border:0;color:var(--text);font-size:17px;padding:13px 2px}
#v3act .box button.sub{padding-left:16px;font-size:16px;color:color-mix(in srgb,var(--text) 85%,transparent)}
#v3act .box button.cancel{text-align:center;color:color-mix(in srgb,var(--text) 55%,transparent);margin-top:6px}
/* ---------- 2026-11-20 sera: impaginazione del telecomando ---------- */
/* Marchio e spegni sopra la foto, non sopra l'immagine */
html body #remotePage.page .remote-top.remote-top{position:relative!important;top:auto!important;left:auto!important;right:auto!important;order:0!important;flex:none!important;height:auto!important;min-height:44px!important;display:flex!important;align-items:center!important;justify-content:center!important;padding:env(safe-area-inset-top) 0 0!important;background:var(--bg)!important;z-index:6}
html body #remotePage.page .remote-top .power-top{position:absolute!important;right:0!important;top:calc(50% + env(safe-area-inset-top)/2)!important;transform:translateY(-50%)!important}
html body #remotePage.page .now-card.now-card{order:1!important}
html body #remotePage .now-actions{top:10px!important}
/* Frecce / Trackpad: sotto la foto, al centro */
html body #remotePage .v3modes{position:static!important;order:2!important;flex:none!important;display:flex;justify-content:center;gap:18px;margin:10px 0 0!important}
html body #remotePage .v3modes button{font-size:14px;padding:6px 4px}
/* Frecce e OK in un quadrato: stesse distanze in orizzontale e in verticale */
html body #remotePage.page .dpad-wrap.dpad-wrap{order:3!important;flex:1 1 0!important;min-height:0!important;max-height:none!important;margin:4px 0!important;display:grid!important;place-items:center!important;container-type:size}
html body #remotePage.page .dpad-wrap .nav-pad.nav-pad{width:min(250px,100cqh,100cqw)!important;height:min(250px,100cqh,100cqw)!important;aspect-ratio:1/1!important;position:relative!important;transform:none!important;padding:0!important;margin:0 auto!important}
html body #remotePage.page .nav-pad .dkey.dkey{position:absolute!important;width:30%!important;height:30%!important;transform:none!important;right:auto!important;bottom:auto!important;margin:0!important;display:grid!important;place-items:center!important}
html body #remotePage.page .nav-pad .dkey.up{left:35%!important;top:0!important}
html body #remotePage.page .nav-pad .dkey.left{left:0!important;top:35%!important}
html body #remotePage.page .nav-pad .dkey.ok{left:35%!important;top:35%!important}
html body #remotePage.page .nav-pad .dkey.right{left:70%!important;top:35%!important}
html body #remotePage.page .nav-pad .dkey.down{left:35%!important;top:70%!important}
/* Comandi e volume in fondo, sopra la barra */
html body #remotePage.page .transport{order:4!important}
html body #remotePage.page .quick{order:5!important;margin-top:12px!important}
html body #remotePage.page .sliders{order:6!important;margin:12px 0 10px!important}
/* Trackpad: da sotto la foto fino in fondo, il resto sparisce */
html body #remotePage.trackpad .transport,html body #remotePage.trackpad .quick,html body #remotePage.trackpad .sliders,html body #remotePage.trackpad .more{display:none!important}
html body #remotePage.trackpad .dpad-wrap{margin:8px 0 12px!important}
html body #remotePage .v3pad{inset:0!important}
html body #remotePage .v3pad .btns{height:64px}
/* Voce scelta: solo piu luminosa, nessuna riga sotto */
html body .src{border-bottom-color:transparent!important}
html body .src.active{color:var(--text)!important}
html body .provider-tabs button,html body .provider-tabs button.active{border-bottom-color:transparent!important}
html body .provider-tabs button.active{color:var(--text)!important}
html body .chap.on::after,html body #optBody .xtab.on::after{display:none!important}
html body #currentTime{text-decoration:none!important}
html body .setting-row,html body .color-row,html body .opt-link,html body .kv>div,html body .track-list>*,html body .track-option{box-shadow:none!important;border-bottom:0!important}
/* Frecce per tornare indietro: grandi e facili da toccare */
html body .media-sheet-head{grid-template-columns:48px minmax(0,1fr)!important}
html body .media-sheet-head button#mediaSheetBack,html body #optBack,html body #providerBack{width:48px!important;height:48px!important;min-width:48px!important;font-size:46px!important;line-height:40px!important;font-weight:300!important;background:none!important;border:0!important;color:var(--text)!important;padding:0 0 6px!important;display:grid!important;place-items:center!important}
#settingsPage .v3back{color:var(--text)!important;font-size:17px!important;display:none;align-items:center;gap:4px;padding:6px 10px 6px 0!important;min-height:48px}
#settingsPage.v3set:not([data-cat="index"]) .v3back{display:inline-flex!important}
#settingsPage .v3back i{font-style:normal;font-size:44px;line-height:30px;font-weight:300;margin-top:-6px}
/* Icone nelle azioni (album, film, brani) */
.v3ico{width:20px;height:20px;flex:none;stroke:currentColor;fill:none;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
.v3acts button{display:flex!important;align-items:center;justify-content:center;gap:6px}
.v3acts button .v3ico{width:17px;height:17px}
#v3act .box button:not(.cancel){display:flex!important;align-items:center;gap:14px}
/* ---------- 2026-11-20 notte: Impostazioni rifatte, un solo stile per tutte le righe ---------- */
html body #settingsPage#settingsPage{padding-bottom:calc(140px + env(safe-area-inset-bottom))!important}
html body #settingsPage#settingsPage .page-head{margin:0 0 6px!important;padding-top:calc(6px + env(safe-area-inset-top))!important}
html body #settingsPage#settingsPage .page-head h1{font-size:32px!important;font-weight:700!important;letter-spacing:-.025em!important;margin:2px 0 10px!important;line-height:1.1!important}
html body #settingsPage#settingsPage .page-head p{display:none!important}
html body #settingsPage#settingsPage .v3back{font-size:16px!important;min-height:40px!important;color:color-mix(in srgb,var(--text) 70%,transparent)!important;padding:0!important;margin:0!important}
html body #settingsPage#settingsPage .v3back i{font-size:30px!important;line-height:1!important;margin:-3px 2px 0 -2px!important}
/* Indice: voce grande, descrizione sotto, freccia a destra */
html body #settingsPage#settingsPage .v3index{margin-top:4px!important}
html body #settingsPage#settingsPage .v3index button{display:grid!important;grid-template-columns:minmax(0,1fr) auto;align-items:center;column-gap:12px;padding:12px 0!important;min-height:62px}
html body #settingsPage#settingsPage .v3index button:after{content:"\203A";grid-row:1/3;grid-column:2;font-size:26px;font-weight:300;line-height:1;color:color-mix(in srgb,var(--text) 32%,transparent)}
html body #settingsPage#settingsPage .v3index b{font-size:17px!important;font-weight:600!important}
html body #settingsPage#settingsPage .v3index small{font-size:13px!important;margin-top:3px!important}
html body #settingsPage#settingsPage .v3index button:active b{color:var(--accent)}
/* Righe: testo a sinistra, comando a destra, stesse misure ovunque */
html body #settingsPage#settingsPage .provider-row,html body #settingsPage#settingsPage .v3screens .scr{display:grid!important}
html body #settingsPage#settingsPage .setting-row,html body #settingsPage#settingsPage .provider-row,html body #settingsPage#settingsPage #syncLink,html body #settingsPage#settingsPage .v3screens .scr{grid-template-columns:minmax(0,1fr) auto!important;align-items:center!important;column-gap:16px!important;row-gap:0!important;width:100%!important;min-height:58px!important;padding:12px 0!important;margin:0!important;background:none!important;border:0!important;box-shadow:none!important;text-align:left!important;font:inherit!important;color:var(--text)!important}
html body #settingsPage#settingsPage .setting-row>div:first-child,html body #settingsPage#settingsPage .provider-row>div:first-child,html body #settingsPage#settingsPage .color-row>div:first-child{min-width:0!important;display:block!important}
html body #settingsPage#settingsPage .setting-row span,html body #settingsPage#settingsPage .provider-row span,html body #settingsPage#settingsPage .color-row span,html body #settingsPage#settingsPage #syncLinkLabel,html body #settingsPage#settingsPage .v3screens .scr>span:first-child{display:block!important;font-size:16px!important;font-weight:600!important;line-height:1.3!important;color:var(--text)!important;letter-spacing:-.005em}
html body #settingsPage#settingsPage .setting-row small,html body #settingsPage#settingsPage .provider-row small,html body #settingsPage#settingsPage .color-row small,html body #settingsPage#settingsPage .v3screens .scr small{display:block!important;font-size:13px!important;font-weight:400!important;line-height:1.38!important;margin-top:3px!important;color:color-mix(in srgb,var(--text) 52%,transparent)!important}
html body #settingsPage#settingsPage .setting-row small:empty{display:none!important}
/* Comandi a destra: stessa capsula morbida per tendine e pulsanti (raggio 12 come il resto del telecomando) */
html body #settingsPage#settingsPage .renderer-select,html body #settingsPage#settingsPage .setting-value{-webkit-appearance:none!important;appearance:none!important;font-family:inherit!important;font-size:14px!important;font-weight:600!important;line-height:1.2!important;color:var(--text)!important;background-color:color-mix(in srgb,var(--text) 9%,transparent)!important;border:0!important;border-radius:12px!important;height:36px!important;min-height:36px!important;padding:0 14px!important;max-width:min(52vw,230px)!important;width:auto!important;white-space:nowrap!important;overflow:hidden!important;text-overflow:ellipsis!important;justify-self:end!important;display:inline-block!important;box-shadow:none!important;text-align:center!important}
html body #settingsPage#settingsPage .renderer-select{padding-right:32px!important;text-align:left!important;text-align-last:left!important;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='%23999' stroke-width='2.2' stroke-linecap='round' stroke-linejoin='round'%3E%3Cpath d='m7 10 5 5 5-5'/%3E%3C/svg%3E")!important;background-repeat:no-repeat!important;background-position:right 10px center!important;background-size:16px 16px!important}
html body #settingsPage#settingsPage .renderer-select option{color:initial;background:initial}
html body #settingsPage#settingsPage .renderer-select:empty{display:none!important}
html body #settingsPage#settingsPage .setting-value:active,html body #settingsPage#settingsPage .renderer-select:active{background-color:color-mix(in srgb,var(--text) 16%,transparent)!important}
/* Interruttori: seguono il colore del telecomando */
html body #settingsPage#settingsPage .toggle,html body #remotePage .toggle{flex:none!important;width:51px!important;height:31px!important;border-radius:16px!important;border:0!important;background:color-mix(in srgb,var(--text) 18%,transparent)!important;justify-self:end!important;position:relative!important;transition:background .2s ease!important}
html body #settingsPage#settingsPage .toggle:after{width:27px!important;height:27px!important;top:2px!important;left:2px!important;background:#fff!important;box-shadow:0 1px 3px rgba(0,0,0,.3)!important;transition:transform .2s ease!important}
html body #settingsPage#settingsPage .toggle.on{background:var(--accent)!important}
html body #settingsPage#settingsPage .toggle.on:after{background:var(--on-accent,#fff)!important;transform:translateX(20px)!important;left:2px!important}
/* Colori: pallini sotto il testo, su una riga intera */
html body #settingsPage#settingsPage .color-row,html body #settingsPage#settingsPage .setting-row:has(.swatches){grid-template-columns:1fr!important;row-gap:12px!important;padding:14px 0!important;min-height:0!important}
html body #settingsPage#settingsPage .swatches{display:flex!important;flex-wrap:wrap!important;gap:12px!important;justify-content:flex-start!important;justify-self:start!important;margin:0!important;padding:3px!important}
html body #settingsPage#settingsPage .swatches .swatch,html body #settingsPage#settingsPage .swatches button{width:32px!important;height:32px!important;min-width:32px!important;border-radius:50%!important;padding:0!important;margin:0!important;border:0!important;box-shadow:inset 0 0 0 1px color-mix(in srgb,var(--text) 14%,transparent)!important}
html body #settingsPage#settingsPage .swatches .on{box-shadow:0 0 0 2px var(--bg),0 0 0 4px var(--text)!important}
/* Sincronia audio: riga come le altre, valore a destra */
html body #settingsPage#settingsPage[data-cat="audio"]>#syncLink{display:grid!important;grid-template-columns:minmax(0,1fr) auto auto!important}
html body #settingsPage#settingsPage #syncLink>*{grid-row:1!important;align-self:center!important;margin:0!important}
html body #settingsPage#settingsPage #syncLinkValue{font-size:14px!important;font-weight:600!important;color:color-mix(in srgb,var(--text) 60%,transparent)!important;margin:0!important}
html body #settingsPage#settingsPage #syncLink .chev,html body #settingsPage#settingsPage .provider-row:after{display:block!important;content:"\203A";font-size:26px!important;font-weight:300!important;line-height:1!important;color:color-mix(in srgb,var(--text) 32%,transparent)!important}
html body #settingsPage#settingsPage #syncLink .chev{font-size:0!important}
html body #settingsPage#settingsPage #syncLink .chev:before{content:"\203A";font-size:26px;font-weight:300}
/* Upscaling: le opzioni sotto rientrano, senza linea */
html body #settingsPage#settingsPage .nested-settings{border:0!important;margin:0!important;padding:0 0 0 18px!important}
/* Titoletti dei gruppi */
html body #settingsPage#settingsPage #blocksTitle,html body #settingsPage#settingsPage .v3screens h3,html body #settingsPage#settingsPage .v3pc h3{font-size:12px!important;font-weight:600!important;text-transform:uppercase!important;letter-spacing:.06em!important;color:color-mix(in srgb,var(--text) 45%,transparent)!important;margin:26px 0 2px!important;padding:0!important;border:0!important;background:none!important}
html body #settingsPage#settingsPage .v3screens h3:first-child{margin-top:6px!important}
html body #settingsPage#settingsPage .v3screens .scr .mark{color:var(--accent)!important;font-size:17px!important;font-weight:700!important}
/* Blocchi del telecomando: frecce in quadratini morbidi */
html body #settingsPage#settingsPage #layoutSettings .setting-row{min-height:54px!important;padding:8px 0!important}
html body #settingsPage#settingsPage .block-tools{display:flex!important;align-items:center!important;gap:8px!important;justify-self:end!important}
html body #settingsPage#settingsPage .block-tools .mini{width:36px!important;height:36px!important;border-radius:12px!important;border:0!important;background:color-mix(in srgb,var(--text) 9%,transparent)!important;color:var(--text)!important;font-size:16px!important;padding:0!important;display:grid!important;place-items:center!important}
html body #settingsPage#settingsPage .block-tools .mini:disabled{opacity:.28!important}
html body #settingsPage#settingsPage .block-tools .toggle{margin-left:6px!important}
/* Componenti: freccia a destra al posto dell'icona */
html body #settingsPage#settingsPage .provider-row{cursor:pointer}
html body #settingsPage#settingsPage .provider-row:active span{color:var(--accent)!important}
/* Lingua: nell'app Avalonia non si cambia dal telecomando, quindi niente riga vuota */
html body[data-platform="mac"] #settingsPage#settingsPage .setting-row:has(#languageValue){display:none!important}
/* PC: stessi margini delle altre pagine */
html body #settingsPage#settingsPage .v3pc .m{padding:10px 0!important}
</style>
<script id="v3script">
(function(){try{
 const en=()=>typeof uiLang!=='undefined'&&uiLang==='en';
 const T=(a,b)=>en()?b:a;
 const IC={play:'<path d="M7 4.5v15l12-7.5z"/>',queue:'<path d="M4 6h12M4 12h12M4 18h8M18 15v6M15 18h6"/>',fav:'<path d="M12 20s-7-4.4-7-10a4 4 0 0 1 7-2.6A4 4 0 0 1 19 10c0 5.6-7 10-7 10z"/>',pl:'<path d="M4 6h11M4 11h11M4 16h6"/><circle cx="17" cy="17" r="2.5"/><path d="M19.5 17V7l2-1"/>'};
 const ico=k=>'<svg class="v3ico" viewBox="0 0 24 24">'+IC[k]+'</svg>';
 const E=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const ART=p=>'/api/art?path='+encodeURIComponent(p);

 // ----- Coda: tipo della voce sulla riga (locandina o quadrato) -----
 if(typeof renderQueue==='function'){const base=renderQueue;renderQueue=function(items){base(items);const rows=document.querySelectorAll('#queueList .media-row');rows.forEach((r,i)=>{const x=(items||[])[i];if(x)r.dataset.kind=x.kind||'movie'})}}

 // ----- Extra: scheda del film con vignettatura -----
 const sheet=document.getElementById('optSheet');
 if(sheet)sheet.addEventListener('scroll',()=>sheet.classList.toggle('scrolled',sheet.scrollTop>140),{passive:true});
 window.v3SyncBlock=function(s){
  const run=!!s.AudioAutoSyncRunning,dis=!s.AudioDelayAvailable,v=s.AudioDelayMs||0;
  const ia=optActions.push(()=>cmd('avdelay',run?'&stop=1':'&auto=1'))-1,ir=optActions.push(()=>{cmd('avdelay','&reset=1');const f=document.getElementById('syncInput');if(f)f.value='0'})-1;
  const st=[-50,-10,10,50].map(n=>[n,optActions.push(()=>cmd('avdelay','&step='+n))-1]);
  return '<div class="v3sync"><div class="big"><input id="syncInput" type="number" inputmode="numeric" step="1" value="'+(s.AudioDelayCurve?'':v)+'" placeholder="'+(s.AudioDelayCurve?T('variabile','variable'):'0')+'"'+(dis?' disabled':'')+'><span>ms</span></div>'
   +'<div class="steps">'+st.map(([n,id])=>'<button data-opt="'+id+'"'+(dis?' disabled':'')+'>'+(n>0?'+':'−')+Math.abs(n)+'</button>').join('')+'</div>'
   +'<div class="links"><button data-opt="'+ia+'"'+(!s.AudioAutoSyncAvailable&&!run?' disabled':'')+'>'+(run?T('Ferma','Stop'):T('Automatica','Automatic'))+'</button><button data-opt="'+ir+'"'+(dis?' disabled':'')+'>'+T('Azzera','Reset')+'</button></div>'
   +(run?'<div class="bar"><i style="width:'+Math.round((s.AudioAutoSyncProgress||0)*100)+'%"></i></div>':'')
   +(!run&&s.AudioAutoSyncNote?'<p class="note" style="color:var(--text)">'+E(s.AudioAutoSyncNote)+'</p>':'')
   +'<p class="note">'+T('+ ritarda l’audio, − lo anticipa. Resta salvata per questo film.','+ delays the audio, − advances it. Saved for this film.')+'</p></div>'};
 if(typeof OPT==='object'){
  OPT.sync=(s,d)=>({title:T('Sincronia audio','Audio sync'),html:'<div class="v3sec" style="padding-top:70px">'+v3SyncBlock(s)+'</div>'});
  OPT.root=(s,d)=>{
   const f=d.film;let h='';
   if(f){
    const art=f.backdrop||f.preview||f.poster;
    h+='<div class="v3hero">'+(art?'<img src="'+ART(art)+'" alt="">':'')+'<div class="t"><h2>'+E(f.title)+'</h2><small>'+E(f.meta||'')+'</small></div></div>';
    if(f.overview)h+='<div class="v3sec"><p class="v3p">'+E(f.overview)+'</p></div>';
   } else h+='<div class="v3sec" style="padding-top:90px"><p class="v3p" style="text-align:center;opacity:.6">'+(s.HasPlayback?'':T('Avvia un film per vedere qui le sue informazioni.','Start a film to see its information here.'))+'</p></div>';
   const ch=d.chapters||[];
   if(ch.length){const ids=ch.map(c=>optActions.push(()=>{cmd('chapter','&id='+c.id);setTimeout(()=>pollCompanion(true),300)})-1);
    h+='<div class="v3sec"><div class="v3h">'+T('Capitoli','Chapters')+'</div></div><div class="v3chap">'+ch.map((c,i)=>'<button data-opt="'+ids[i]+'" class="'+(c.selected?'on':'')+'">'+E(c.label)+'</button>').join('')+'</div>'}
   if(f&&f.cast&&f.cast.length)h+='<div class="v3sec"><div class="v3h">Cast</div></div><div class="v3cast">'+f.cast.map(c=>'<div>'+(c.photo?'<img loading="lazy" src="'+ART(c.photo)+'" alt="">':'<i>'+E((c.name||'?').split(' ').map(w=>w[0]).slice(0,2).join(''))+'</i>')+'<b>'+E(c.name)+'</b><span>'+E(c.character||'')+'</span></div>').join('')+'</div>';
   if(f&&f.tech&&f.tech.length)h+='<div class="v3sec"><div class="v3h">'+T('Dati tecnici','Technical')+'</div>'+f.tech.map(t=>'<div class="v3line">'+E(t)+'</div>').join('')+'</div>';
   if(f&&f.hdr){const x=f.hdr,n=v=>Number(v).toLocaleString(en()?'en':'it'),kv=(a,b)=>'<div><span>'+a+'</span><b>'+b+'</b></div>';
    h+='<div class="v3sec"><div class="v3h">'+T('Analisi HDR','HDR analysis')+(x.formats?' · '+E(x.formats):'')+'</div><div class="v3kv">'+kv(T('Picco','Peak'),n(x.peak)+' nit')+kv(T('Media più alta','Brightest average'),n(x.brightestAverage)+' nit')+kv(T('Luminosità tipica','Typical'),n(x.typical)+' nit')+kv(T('Media del film','Film average'),n(x.filmAverage)+' nit')+kv(T('Gamma dinamica','Dynamic range'),n(x.stops)+' stop')+kv('Rec.709 · P3 · BT.2020',n(x.rec709)+'% · '+n(x.p3)+'% · '+n(x.bt2020)+'%')+'</div></div>'}
   if(s.HasPlayback){const a1=optActions.push(()=>cmd('info'))-1,a2=optActions.push(()=>cmd('cast'))-1,a3=optActions.push(()=>cmd('hdr_analysis'))-1;
    h+='<div class="v3sec"><div class="v3h">'+T('Sul player','On the player')+'</div><button class="v3link" data-opt="'+a1+'">Info</button>'+(s.CastAvailable?'<button class="v3link" data-opt="'+a2+'">Cast</button>':'')+(s.HdrAnalysisAvailable?'<button class="v3link" data-opt="'+a3+'">'+T('Analisi HDR','HDR analysis')+'</button>':'')+'</div>'}
   return {title:(f&&f.title)||'Extra',html:h};
  };
 }

 // ----- Colore del pallino sugli interruttori accesi: scuro se il colore del telecomando e' chiaro -----
 function onAccent(){try{const v=getComputedStyle(document.documentElement).getPropertyValue('--accent').trim();const c=document.createElement('canvas').getContext('2d');c.fillStyle=v||'#fff';const h=c.fillStyle;
  let r=255,g=255,b=255;if(/^#[0-9a-f]{6}$/i.test(h)){r=parseInt(h.slice(1,3),16);g=parseInt(h.slice(3,5),16);b=parseInt(h.slice(5,7),16)}
  const lum=(.2126*r+.7152*g+.0722*b)/255;document.documentElement.style.setProperty('--on-accent',lum>.72?'#1c1c1e':'#fff')}catch(_){}}
 onAccent();new MutationObserver(onAccent).observe(document.documentElement,{attributes:true,attributeFilter:['style','class']});
 new MutationObserver(onAccent).observe(document.body,{attributes:true,attributeFilter:['class']});

 // ----- Impostazioni: indice di categorie -----
 const page=document.getElementById('settingsPage');
 if(page){
  page.classList.add('v3set');page.dataset.cat='index';
  const head=page.querySelector('.page-head'),h1=head&&head.querySelector('h1');
  const back=document.createElement('button');back.className='v3back';head.prepend(back);
  const mac=document.body.dataset.platform==='mac';
  // Categorie in gruppi, dall'uso piu' comune al piu' tecnico.
  const cats=[
   ['play','',()=>T('Riproduzione','Playback'),()=>T('Motore video, HDR, 3D, upscaling, DVD','Video engine, HDR, 3D, upscaling, DVD')],
   ['picture','',()=>T('Immagine','Picture'),()=>T('Dimensionamento, bande nere, scene IMAX','Sizing, black bars, IMAX scenes')],
   ['subs','',()=>T('Sottotitoli','Subtitles'),()=>T('Lingua preferita e aspetto','Preferred language and style')],
   ['audio','',()=>T('Audio dei film','Film audio'),()=>T('Uscita, bitstream, sincronia','Output, bitstream, sync')],
   ['music','',()=>T('Musica','Music'),()=>T('Automix, dissolvenze, equalizzatore','Automix, crossfades, equalizer')],
   ['screen','',()=>T('Schermi','Screens'),()=>T('Dove si apre il player, schermo dei film, frequenza','Where the player opens, film screen, refresh rate')],
   ['library','',()=>T('Libreria e Spotlight','Library and Spotlight'),()=>T('Fonti, server, avvio','Sources, servers, startup')],
   ['look','',()=>T('Aspetto e lingua','Look and language'),()=>T('Tema, colori, lingua','Theme, colours, language')],
   ['remote','',()=>T('Questo telecomando','This remote'),()=>T('Pagina iniziale, colore, blocchi','Start page, colour, blocks')],
   ['pc','',()=>mac?T('Questo Mac','This Mac'):T('Questo PC','This PC'),()=>T('CPU, GPU, memoria, batteria, rete, dischi','CPU, GPU, memory, battery, network, disks')],
   ['updates','',()=>T('Aggiornamenti','Updates'),()=>mac?T('Versione, componenti, motore madVR','Version, components, madVR engine'):T('Versione e componenti','Version and components')],
   ['adv','',()=>T('Componenti avanzati','Advanced components'),()=>T('madVR, LAV, MPC Video Renderer, mpv','madVR, LAV, MPC Video Renderer, mpv')]];
  const groups=[[()=>T('Video','Video'),['play','picture','subs']],[()=>T('Audio','Audio'),['audio','music']],
   [()=>T('Player','Player'),['screen','library','look']],[()=>T('Telefono e sistema','Phone and system'),['remote','pc','updates','adv']]];
  const index=document.createElement('div');index.className='v3index';head.after(index);
  // Righe esistenti nella loro categoria.
  const has=(el,sel)=>el.matches&&(el.matches(sel)||el.querySelector(sel));
  const txt=el=>(el.textContent||'')+' '+(el.dataset.it||'');
  [...page.children].forEach(el=>{
   if(el===head||el===index)return;
   let c='';
   if(has(el,'#rendererCurrent,#settingsHdr,#settings3d,#settingsUpscaling,#rendererDefault')||el.id==='upscaleDetails'||/Jellyfin/.test(txt(el)))c='play';
   else if(has(el,'#audioOutSettings')||el.classList.contains('sync-row')||el.id==='syncLink')c='audio';
   else if(/Spotlight/i.test(txt(el)))c='library';
   else if(/schermo intero|full screen/i.test(txt(el)))c='screen';
   else if(has(el,'#themeValue,#languageValue')||el.classList.contains('color-row'))c='look';
   else if(el.id==='componentsTitle'||el.id==='providerList')c='adv';
   else if(has(el,'#prefStart,#prefAccent,#prefVibrate')||el.id==='blocksTitle'||el.id==='layoutSettings'||el.classList.contains('reset-row'))c='remote';
   if(c)el.dataset.cat=c;
  });
  // Sincronia: la riga vecchia a pulsanti no, resta il collegamento alla scheda.
  page.querySelectorAll('.sync-row').forEach(r=>r.dataset.cat='none');
  // Schermi
  const screens=document.createElement('div');screens.className='v3screens';screens.dataset.cat='screen';
  const firstScreen=page.querySelector('[data-cat="screen"]');page.insertBefore(screens,firstScreen||null);
  // PC
  const pc=document.createElement('div');pc.className='v3pc';pc.dataset.cat='pc';page.appendChild(pc);
  const catOf=k=>cats.find(c=>c[0]===k);
  function drawIndex(){
   const q=index.querySelector('.v3search')?.value||'';
   index.innerHTML='<input class="v3search" type="search" autocomplete="off" placeholder="'+E(T('Cerca un’impostazione','Search settings'))+'"><div class="v3results"></div><div class="v3groups">'+
    groups.map(([g,keys])=>'<h3>'+E(g())+'</h3>'+keys.map(k=>{const c=catOf(k);return '<button data-k="'+k+'"><b>'+E(c[2]())+'</b><small>'+E(c[3]())+'</small></button>'}).join('')).join('')+'</div>';
   index.querySelectorAll('.v3groups button').forEach(b=>b.onclick=()=>open(b.dataset.k));
   const input=index.querySelector('.v3search');input.value=q;input.oninput=search;if(q)search();
  }
  // Ricerca: righe di tutte le categorie (testo e descrizione); un tocco apre la categoria e mostra la riga.
  function search(){
   const input=index.querySelector('.v3search'),out=index.querySelector('.v3results'),grp=index.querySelector('.v3groups');
   const q=input.value.trim().toLowerCase();grp.style.display=q?'none':'';
   if(!q){out.innerHTML='';return}
   const rows=[...page.querySelectorAll('[data-cat] .setting-row, .setting-row[data-cat], .color-row[data-cat], .v3native [data-key]')]
    .filter(r=>{const c=(r.closest('[data-cat]')||r).dataset.cat;return c&&c!=='none'&&c!=='index'&&!r.classList.contains('v3hide')});
   const hits=[];rows.forEach(r=>{const label=(r.querySelector('span')?.textContent||'').trim(),desc=(r.querySelector('small')?.textContent||'').trim();
    if(label&&(label+' '+desc).toLowerCase().includes(q)&&!hits.some(h=>h.label===label))hits.push({r,label,cat:(r.closest('[data-cat]')||r).dataset.cat})});
   catsForSearch().forEach(c=>{if((c[2]()+' '+c[3]()).toLowerCase().includes(q))hits.unshift({cat:c[0],label:c[2](),whole:true})});
   out.innerHTML=hits.length?hits.slice(0,30).map((h,i)=>'<button data-i="'+i+'"><b>'+E(h.label)+'</b><small>'+E(catOf(h.cat)?.[2]()||'')+'</small></button>').join(''):'<p class="note">'+E(T('Nessuna impostazione trovata.','No settings found.'))+'</p>';
   out.querySelectorAll('button').forEach(b=>b.onclick=()=>{const h=hits[+b.dataset.i];open(h.cat);if(h.r&&!h.whole)setTimeout(()=>{h.r.scrollIntoView({block:'center'});h.r.classList.add('v3flash');setTimeout(()=>h.r.classList.remove('v3flash'),1600)},60)});
  }
  const catsForSearch=()=>cats;
  function open(k){page.dataset.cat=k;const c=cats.find(x=>x[0]===k);h1.textContent=c?c[2]():T('Impostazioni','Settings');back.innerHTML='<i>‹</i>'+T('Impostazioni','Settings');window.scrollTo(0,0);if(k==='screen'){drawScreens();if(typeof pollCompanion==='function')pollCompanion(true);setTimeout(drawScreens,500)}if(k==='pc')pollPc(true);if(typeof loadSettings==='function')loadSettings()}
  back.onclick=()=>{page.dataset.cat='index';h1.textContent=T('Impostazioni','Settings');window.scrollTo(0,0);const i=index.querySelector('.v3search');if(i&&i.value){i.value='';search()}};
  drawIndex();
  function drawScreens(){
   const d=window.companionData||{},list=d.screens||[],start=d.startScreen||'';
   let h='<h3>'+T('All’avvio','At startup')+'</h3>';
   const opt=(key,label,sub)=>'<button class="scr" data-start="'+E(key)+'"><span>'+E(label)+(sub?'<small>'+E(sub)+'</small>':'')+'</span><span class="mark">'+(start===key?'✓':'')+'</span></button>';
   h+=opt('',T('Ultima posizione','Last position'),T('Dove era quando l’hai chiuso','Where it was when you closed it'));
   h+=opt('active',T('Schermo attivo','Active screen'),T('Quello dove c’è il mouse','The one with the mouse'));
   list.forEach(sc=>h+=opt(sc.key,sc.label,''));
   if(list.length>1){h+='<h3>'+T('Sposta adesso','Move now')+'</h3>';list.forEach(sc=>h+='<button class="scr" data-move="'+E(sc.key)+'"><span>'+E(sc.label)+'</span><span class="mark">'+(sc.current?T('qui','here'):'')+'</span></button>')}
   screens.innerHTML=h;
   screens.querySelectorAll('[data-start]').forEach(b=>b.onclick=()=>{cmd('start_screen','&key='+encodeURIComponent(b.dataset.start));if(window.companionData)window.companionData.startScreen=b.dataset.start;drawScreens();setTimeout(()=>pollCompanion(true),300)});
   screens.querySelectorAll('[data-move]').forEach(b=>b.onclick=()=>{cmd('screen','&key='+encodeURIComponent(b.dataset.move));setTimeout(()=>{pollCompanion(true);setTimeout(drawScreens,400)},700)});
  }
  setInterval(()=>{if(page.dataset.cat==='screen'&&page.classList.contains('active'))drawScreens()},3000);
  // ----- PC -----
  const gb=b=>(b/1073741824).toLocaleString(en()?'en':'it',{maximumFractionDigits:b>=1e11?0:1})+' GB';
  const meter=(label,value,sub,pct,hot)=>'<div class="m"><div class="r"><span>'+E(label)+'</span><b>'+E(value)+'</b></div>'+(sub?'<div class="r"><span>'+E(sub)+'</span></div>':'')+(pct!=null?'<div class="bar"><i class="'+(hot?'hot':'')+'" style="width:'+Math.max(0,Math.min(100,pct))+'%"></i></div>':'')+'</div>';
  let pcBusy=false;
  async function pollPc(force){
   if(pcBusy||(!force&&(page.dataset.cat!=='pc'||!page.classList.contains('active'))))return;pcBusy=true;
   try{const r=await api('/api/pc');if(!r.ok)return;const p=await r.json();
    if(!p.available){pc.innerHTML='<p class="note">'+T('Monitoraggio non disponibile su questo sistema.','Monitoring not available on this system.')+'</p>';return}
    let h='<h3>'+E(p.machine||'PC')+'</h3>';
    h+=meter('CPU',Math.round(p.cpu.load)+'%',p.cpu.cores+' '+T('thread','threads')+(p.cpu.temp!=null?' · '+p.cpu.temp+' °C':''),p.cpu.load,p.cpu.load>90);
    h+=meter(T('Memoria','Memory'),gb(p.memory.used)+' / '+gb(p.memory.total),'',p.memory.used/p.memory.total*100,p.memory.load>90);
    if(p.gpu){const g=p.gpu;h+='<h3>GPU · '+E(g.name)+'</h3>';
     h+=meter(T('Utilizzo','Load'),Math.round(g.load)+'%',g.clock?Math.round(g.clock)+' MHz':'',g.load,g.load>95);
     h+=meter(T('Memoria video','Video memory'),Math.round(g.memUsed)+' / '+Math.round(g.memTotal)+' MB','',g.memUsed/g.memTotal*100,false);
     if(!isNaN(g.temp))h+=meter(T('Temperatura','Temperature'),Math.round(g.temp)+' °C',g.fan>0?T('Ventola ','Fan ')+Math.round(g.fan)+'%':T('Ventole ferme','Fans stopped'),g.temp,g.temp>83);
     if(!isNaN(g.power))h+=meter(T('Consumo GPU','GPU power'),g.power.toFixed(0)+' W',!isNaN(g.powerLimit)?T('limite ','limit ')+g.powerLimit.toFixed(0)+' W':'',!isNaN(g.powerLimit)&&g.powerLimit>0?g.power/g.powerLimit*100:null,false)}
    if(p.battery){const b=p.battery;h+='<h3>'+T('Batteria','Battery')+'</h3>'+meter(b.charging?T('In carica','Charging'):T('A batteria','On battery'),b.percent+'%',b.minutesLeft>0?Math.floor(b.minutesLeft/60)+' h '+(b.minutesLeft%60)+' min':'',b.percent,b.percent<15)}
    h+='<h3>'+T('Rete','Network')+'</h3>'+meter(T('Ricezione','Download'),p.network.rxMbps.toFixed(1)+' Mbit/s','',null)+meter(T('Invio','Upload'),p.network.txMbps.toFixed(1)+' Mbit/s','',null);
    if((p.disks||[]).length){h+='<h3>'+T('Dischi','Disks')+'</h3>';p.disks.forEach(d=>{const used=d.total-d.free;h+=meter(d.name+(d.label?' · '+d.label:''),gb(d.free)+' '+T('liberi','free'),gb(used)+' / '+gb(d.total),used/d.total*100,d.free/d.total<.08)})}
    h+='<p class="note">'+(document.body.dataset.platform==='mac'?T('macOS non espone temperature e consumi senza privilegi di amministratore. Aggiornato ogni 2 secondi.','macOS does not expose temperatures and power without administrator rights. Updated every 2 seconds.'):T('Il consumo della CPU e la sua temperatura si leggono solo se il sistema li espone (sensori ACPI). Aggiornato ogni 2 secondi.','CPU power and temperature are shown only when the system exposes them (ACPI sensors). Updated every 2 seconds.'))+'</p>';
    pc.innerHTML=h}catch(_){}finally{pcBusy=false}}
  setInterval(()=>pollPc(false),2000);

  // ----- Impostazioni del player: il catalogo "player" di /api/settings, nelle categorie -----
  const playerSections=()=>(window.nativeSettings||nativeSettings||{}).player||[];
  const findPlayer=key=>{let f=null;playerSections().forEach(s=>s.settings.forEach(x=>{if(x.key===key)f=x}));return f};
  // Le righe rapide della V2 ("Player|chiave") leggono e scrivono il catalogo.
  const _findNative=findNative;
  findNative=function(spec){const[provider,key]=spec.split('|');if(provider==='Player'){const f=findPlayer(key);if(f)return f}return _findNative(spec)};
  const quick=new Set([...page.querySelectorAll('[data-native^="Player|"]')].map(el=>el.dataset.native.split('|')[1]));
  function drawPlayer(){
   const secs=playerSections();if(!secs.length)return;
   page.querySelectorAll('.v3native').forEach(n=>n.innerHTML='');
   secs.forEach(sec=>{
    const rows=sec.settings.filter(st=>!quick.has(st.key));if(!rows.length)return;
    let box=page.querySelector('.v3native[data-cat="'+sec.cat+'"]');
    if(!box){box=document.createElement('div');box.className='v3native';box.dataset.cat=sec.cat;page.appendChild(box)}
    box.insertAdjacentHTML('beforeend','<h3>'+E(sec.title)+'</h3>'+rows.map(st=>{
     const ctl=st.kind==='toggle'?'<button class="toggle '+(st.value==='1'?'on':'')+'" data-k="'+E(st.key)+'" data-v="'+(st.value==='1'?'0':'1')+'"></button>'
      :st.kind==='choice'?'<select class="renderer-select" data-k="'+E(st.key)+'">'+(st.choices||[]).map(c=>'<option value="'+E(c.value)+'"'+(String(c.value)===String(st.value)?' selected':'')+'>'+E(c.label)+'</option>').join('')+'</select>'
      :st.kind==='action'?'<button class="setting-value" data-k="'+E(st.key)+'" data-v="do">'+E(st.action)+'</button>':'';
     return '<div class="setting-row native-row" data-key="'+E(st.key)+'"><div><span>'+E(st.label)+'</span>'+(st.description?'<small>'+E(st.description)+'</small>':'')+'</div>'+ctl+'</div>'}).join(''));
   });
   page.querySelectorAll('.v3native [data-k]').forEach(el=>{
    if(el.tagName==='SELECT')el.onchange=()=>setPlayer(el.dataset.k,el.value,el);else el.onclick=()=>setPlayer(el.dataset.k,el.dataset.v,el)});
  }
  async function setPlayer(key,value,el){
   const st=findPlayer(key);
   try{const r=await api('/api/settings/set?provider=Player&key='+encodeURIComponent(key)+'&value='+encodeURIComponent(value));if(r.status===401){location.replace('/');return}
    const res=await r.json();const row=el.closest('.setting-row');row.querySelector('small.err')?.remove();
    if(!res.ok){const e=document.createElement('small');e.className='err';e.textContent=res.error||T('Errore','Error');row.firstElementChild.appendChild(e)}
    if(st)st.value=res.value;
    if(st?.reload||value==='do'||!res.ok){await loadSettings();return}
    if(el.tagName==='SELECT')el.value=res.value;else{el.classList.toggle('on',res.value==='1');el.dataset.v=res.value==='1'?'0':'1'}
   }catch(_){}
  }
  const _rpl=renderProviderList;
  renderProviderList=function(){_rpl();drawPlayer();renderQuickNative()};
  if(typeof nativeSettings!=='undefined'&&nativeSettings)drawPlayer();
  // Sul Mac MPC Video Renderer non ha le funzioni NVIDIA RTX e non c'e' EVR: niente voci per loro.
  if(mac){
   const hdr=document.getElementById('settingsHdr')?.closest('.setting-row')?.querySelector('small');if(hdr)hdr.textContent=T('Passthrough, tone mapping o LUT (madVR)','Passthrough, tone mapping or LUT (madVR)');
   const be=document.getElementById('upscaleBackend');if(be){be.querySelector('option[value="mpcvr"]')?.remove();const sm=be.closest('.setting-row')?.querySelector('small');if(sm)sm.textContent='madVR'}
   document.getElementById('upscalePreset')?.closest('.setting-row')?.classList.add('v3hide');
   // Il tema ha la sua voce completa (Come macOS, Chiaro, Scuro) nel catalogo: niente pulsante doppio.
   document.getElementById('themeValue')?.closest('.setting-row')?.classList.add('v3hide');
   const ao=document.getElementById('audioOutSettings')?.closest('.setting-row')?.querySelector('small');if(ao)ao.textContent=T('Dispositivo del Mac per l’audio dei film','Mac device for film audio');
   ['rendererCurrent','rendererDefault'].forEach(id=>document.getElementById(id)?.querySelectorAll('option').forEach(o=>{if(o.value==='EVR'||o.textContent==='EVR')o.remove()}));
  }
 }

 // ----- Frecce / Trackpad -----
 const rp=document.getElementById('remotePage'),wrap=rp&&rp.querySelector('.dpad-wrap');
 if(wrap){
  const modes=document.createElement('div');modes.className='v3modes';
  const pad=document.createElement('div');pad.className='v3pad';
  pad.innerHTML='<div class="hint"></div><div class="btns"><button data-b="left"></button><button data-b="right"></button></div>';
  rp.insertBefore(modes,wrap);wrap.appendChild(pad);
  let mode='keys';try{mode=localStorage.getItem('cc-pad-mode')||'keys'}catch(_){}
  function drawModes(){const ok=(window.companionData||{}).pointer!==false;modes.style.display=ok?'':'none';if(!ok&&mode!=='keys'){mode='keys';rp.classList.remove('trackpad')}
   modes.innerHTML='<button data-m="keys" class="'+(mode==='keys'?'on':'')+'">'+T('Frecce','Arrows')+'</button><button data-m="pad" class="'+(mode==='pad'?'on':'')+'">Trackpad</button>';
   modes.querySelectorAll('button').forEach(b=>b.onclick=()=>{mode=b.dataset.m;try{localStorage.setItem('cc-pad-mode',mode)}catch(_){}apply()});
   pad.querySelector('.hint').textContent=T('Trascina per muovere il puntatore · tocca per cliccare · due dita per scorrere','Drag to move the pointer · tap to click · two fingers to scroll');
   pad.querySelector('[data-b=left]').textContent=T('Sinistro','Left');pad.querySelector('[data-b=right]').textContent=T('Destro','Right')}
  function apply(){rp.classList.toggle('trackpad',mode==='pad');drawModes()}
  apply();setInterval(drawModes,4000);
  pad.addEventListener('pointerdown',()=>sock(),{capture:true});
  // Comandi del puntatore: una richiesta alla volta, senza aggiornare lo stato del telecomando (prima ogni movimento
  // passava dalla coda dei comandi con tre letture dello stato: il puntatore arrivava con secondi di ritardo).
  let inFlight=false,accX=0,accY=0,accS=0,clicks=[];
  // Canale sempre aperto: i movimenti partono a ogni fotogramma senza aspettare risposte (WebSocket).
  let ws=null,wsTried=0,frame=0;
  function sock(){
   if(ws&&ws.readyState<=1)return ws;
   if(Date.now()-wsTried<3000)return null;wsTried=Date.now();
   try{ws=new WebSocket((location.protocol==='https:'?'wss://':'ws://')+location.host+'/ws/pointer');ws.onclose=()=>{ws=null}}catch(_){ws=null}
   return ws;
  }
  function flushWs(){
   frame=0;const w=sock();if(!w||w.readyState!==1)return false;
   const out=[];
   while(clicks.length)out.push('c '+clicks.shift());
   if(Math.abs(accS)>=1){out.push('s '+Math.round(accS));accS=0}
   const x=Math.round(accX),y=Math.round(accY);if(x||y){accX-=x;accY-=y;out.push('m '+x+' '+y)}
   if(out.length)try{w.send(out.join('\n'))}catch(_){return false}
   return true;
  }
  async function pump(){
   const w=sock();
   if(w&&w.readyState===1){if(!frame)frame=requestAnimationFrame(()=>{if(!flushWs())pumpHttp()});return}
   pumpHttp();
  }
  async function pumpHttp(){
   if(inFlight)return;
   let url=null;
   if(clicks.length)url='/api/cmd?cmd=pointer_click&button='+clicks.shift();
   else if(Math.abs(accS)>=1){url='/api/cmd?cmd=pointer_scroll&dy='+Math.round(accS);accS=0}
   else if(accX||accY){const x=Math.round(accX),y=Math.round(accY);if(!x&&!y)return;accX-=x;accY-=y;url='/api/cmd?cmd=pointer_move&dx='+x+'&dy='+y}
   if(!url)return;
   inFlight=true;
   try{await api(url)}catch(_){}finally{inFlight=false;pumpHttp()}
  }
  function click(b){clicks.push(b);pump()}
  pad.querySelectorAll('[data-b]').forEach(b=>b.onclick=e=>{e.stopPropagation();click(b.dataset.b)});
  const touches=new Map();let start=0,moved=0,fingers=0;
  // Due dita come sul trackpad del Mac: la pagina segue le dita al pixel (media delle dita, prima ogni dito contava
  // e a scatti di rotella) e, lasciate, continua per inerzia rallentando; un nuovo tocco la ferma.
  let vel=0,lastS=0,glide=0;
  function stopGlide(){if(glide)cancelAnimationFrame(glide);glide=0}
  function startGlide(){stopGlide();if(Math.abs(vel)<.4)return;let prev=performance.now();
   const step=now=>{const k=Math.min(3,(now-prev)/16.7);prev=now;vel*=Math.pow(.95,k);if(Math.abs(vel)<.25){glide=0;return}accS+=vel*k;pump();glide=requestAnimationFrame(step)};
   glide=requestAnimationFrame(step)}
  pad.addEventListener('pointerdown',e=>{if(e.target.closest('.btns'))return;stopGlide();pad.setPointerCapture(e.pointerId);touches.set(e.pointerId,{x:e.clientX,y:e.clientY});if(touches.size===1){start=Date.now();moved=0;fingers=1}else{fingers=Math.max(fingers,touches.size);vel=0;lastS=performance.now()}});
  pad.addEventListener('pointermove',e=>{const t=touches.get(e.pointerId);if(!t)return;const dx=e.clientX-t.x,dy=e.clientY-t.y;t.x=e.clientX;t.y=e.clientY;moved+=Math.abs(dx)+Math.abs(dy);
   if(touches.size>=2){const d=dy/touches.size,now=performance.now(),dt=Math.max(1,now-lastS);lastS=now;accS+=d;
    // velocita' in pixel per fotogramma, smorzata: e' quella che prosegue dopo il rilascio
    vel=vel*.6+Math.max(-60,Math.min(60,d*16.7/Math.max(dt,4)))*.4}
   else{const sp=Math.hypot(dx,dy),acc=sp>14?3:sp>6?2.2:1.5;accX+=dx*acc;accY+=dy*acc}pump()});
  const up=e=>{if(!touches.has(e.pointerId))return;const was=touches.size;touches.delete(e.pointerId);
   if(touches.size===0&&Date.now()-start<260&&moved<10){click(fingers>=2?'right':'left');return}
   if(was>=2&&touches.size<2&&performance.now()-lastS<80)startGlide();else if(was>=2&&touches.size<2)vel=0};
  pad.addEventListener('pointerup',up);pad.addEventListener('pointercancel',up);
 }

 // ----- Libreria: coda, preferiti, playlist -----
 const act=document.createElement('div');act.id='v3act';act.innerHTML='<div class="box"></div>';document.body.appendChild(act);
 act.addEventListener('click',e=>{if(e.target===act)act.classList.remove('open')});
 const pathsOf=x=>(x.children||[]).length?(x.children||[]).flatMap(pathsOf):(x.path?[x.path]:[]);
 const favSet=()=>new Set(((window.companionData||{}).favorites||[]).map(p=>p.toLowerCase()));
 function send(c,paths,extra){cmd(c,'&paths='+encodeURIComponent(JSON.stringify(paths))+(extra||''));setTimeout(()=>pollCompanion(true),350)}
 function isFav(paths){const f=favSet();return paths.length>0&&paths.every(p=>f.has(p.toLowerCase()))}
 function toggleFav(paths){const d=window.companionData||{};const f=new Set(d.favorites||[]),on=isFav(paths);paths.forEach(p=>{if(on)f.delete(p);else f.add(p)});d.favorites=[...f];send('favorite',paths)}
 function choosePlaylist(item,paths){
  const box=act.querySelector('.box'),list=(window.companionData||{}).playlists||[],music=item.kind==='music'||item.kind==='album';
  const ok=list.filter(p=>p.bucket==='Mixed'||p.bucket==='Misto'||(music?p.bucket==='Music':p.bucket!=='Music'));
  box.innerHTML='<h4>'+T('Aggiungi a playlist','Add to playlist')+'</h4>'+(ok.length?ok.map((p,i)=>'<button class="sub" data-i="'+i+'">'+E(p.name)+'</button>').join(''):'<p style="opacity:.6;font-size:14px;margin:8px 0 4px">'+T('Nessuna playlist adatta: creala dal player.','No suitable playlist: create one on the player.')+'</p>')+'<button class="cancel">'+T('Annulla','Cancel')+'</button>';
  box.querySelectorAll('[data-i]').forEach(b=>b.onclick=()=>{send('playlist_add',paths,'&key='+encodeURIComponent(ok[+b.dataset.i].key));act.classList.remove('open')});
  box.querySelector('.cancel').onclick=()=>act.classList.remove('open');
  act.classList.add('open');
 }
 function actions(item){
  const paths=pathsOf(item);if(!paths.length)return;
  const box=act.querySelector('.box'),fav=isFav(paths);
  box.innerHTML='<h4>'+E(item.title||'')+'</h4>'
   +'<button data-a="play">'+ico('play')+T('Riproduci','Play')+'</button>'
   +'<button data-a="queue">'+ico('queue')+T('Metti in coda','Add to queue')+'</button>'
   +'<button data-a="fav">'+ico('fav')+(fav?T('Togli dai preferiti','Remove from favourites'):T('Aggiungi ai preferiti','Add to favourites'))+'</button>'
   +'<button data-a="pl">'+ico('pl')+T('Aggiungi a playlist…','Add to playlist…')+'</button>'
   +'<button class="cancel">'+T('Annulla','Cancel')+'</button>';
  box.querySelector('[data-a=play]').onclick=()=>{act.classList.remove('open');if(paths.length===1)cmd('open','&url='+encodeURIComponent(paths[0])+'&title='+encodeURIComponent(item.title||''));else{cmd('open','&url='+encodeURIComponent(paths[0]));send('queue_add',paths.slice(1))}};
  box.querySelector('[data-a=queue]').onclick=()=>{act.classList.remove('open');send('queue_add',paths)};
  box.querySelector('[data-a=fav]').onclick=()=>{act.classList.remove('open');toggleFav(paths)};
  box.querySelector('[data-a=pl]').onclick=()=>choosePlaylist(item,paths);
  box.querySelector('.cancel').onclick=()=>act.classList.remove('open');
  act.classList.add('open');
 }
 window.v3LibraryActions=actions;
 if(typeof renderMediaSheet==='function'){
  const baseSheet=renderMediaSheet;
  renderMediaSheet=function(item){
   baseSheet(item);
   const list=document.getElementById('mediaSheetList');if(!list)return;
   const paths=pathsOf(item),children=item.children||[];
   // In cima: Riproduci / In coda / Preferiti / Playlist per tutto l'album o la serie.
   const bar=document.createElement('div');bar.className='v3acts';
   bar.innerHTML='<button class="main" data-a="play">'+ico('play')+T('Riproduci','Play')+'</button><button data-a="queue">'+ico('queue')+T('In coda','Queue')+'</button><button data-a="fav">'+ico('fav')+'<span>'+(isFav(paths)?T('Preferito','Favourite'):T('Preferiti','Favourites'))+'</span></button><button data-a="pl">'+ico('pl')+'Playlist</button>';
   list.prepend(bar);
   bar.querySelector('[data-a=play]').onclick=()=>{if(!paths.length)return;cmd('open','&url='+encodeURIComponent(paths[0]));if(paths.length>1)send('queue_add',paths.slice(1))};
   bar.querySelector('[data-a=queue]').onclick=()=>send('queue_add',paths);
   bar.querySelector('[data-a=fav]').onclick=()=>{toggleFav(paths);bar.querySelector('[data-a=fav] span').textContent=isFav(paths)?T('Preferito','Favourite'):T('Preferiti','Favourites')};
   bar.querySelector('[data-a=pl]').onclick=()=>choosePlaylist(item,paths);
   // Su ogni riga i tre puntini con le stesse azioni per il singolo brano o episodio.
   list.querySelectorAll('[data-child]').forEach(row=>{const x=children[+row.dataset.child];if(!x)return;
    const more=document.createElement('span');more.className='v3more';more.textContent='•••';
    more.onclick=e=>{e.stopPropagation();actions(x)};row.appendChild(more)});
  };
 }
 // Film nella griglia: tenendo premuto (o col tasto destro) le stesse azioni.
 const libList=document.getElementById('libraryList');
 if(libList){
  let holdTimer=null,held=false;
  const itemOf=b=>{const g=(window.visibleLibrarySections||[])[+b.dataset.group];return g&&(g.items||[])[+b.dataset.item]};
  libList.addEventListener('pointerdown',e=>{const b=e.target.closest('[data-item]');if(!b)return;held=false;clearTimeout(holdTimer);holdTimer=setTimeout(()=>{const x=itemOf(b);if(x){held=true;if(navigator.vibrate)try{navigator.vibrate(10)}catch(_){}actions(x)}},480)});
  ['pointerup','pointercancel','pointerleave'].forEach(n=>libList.addEventListener(n,()=>clearTimeout(holdTimer)));
  libList.addEventListener('scroll',()=>clearTimeout(holdTimer),{passive:true});
  libList.addEventListener('click',e=>{if(held){e.stopPropagation();e.preventDefault();held=false}},true);
  libList.addEventListener('contextmenu',e=>{const b=e.target.closest('[data-item]');if(!b)return;e.preventDefault();const x=itemOf(b);if(x)actions(x)});
 }
}catch(e){console.error('v3',e)}})();
</script>
""";
}
