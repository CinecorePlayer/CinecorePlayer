#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.IO;
using System.Text;

internal sealed partial class RemoteServer
{
    private static string RemoteIconData(string key, string fallbackSvg)
    {
        try
        {
            string? path = AssetIconService.Resolve(key);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch { }

        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(fallbackSvg));
    }

    /// <summary>Pagina del telecomando con il dizionario spagnolo (inglese -> spagnolo) per la traduzione nel browser.</summary>
    private static string RemoteHtmlV2WithSpanish()
    {
        string html = RemoteHtmlV2();
        string dictionary = System.Text.Json.JsonSerializer.Serialize(global::CinecorePlayer2025.Utilities.SpanishTexts.All);
        // "</" dentro uno script chiuderebbe il tag: si spezza.
        string script = "<script>window.ES=" + dictionary.Replace("</", "<" + (char)92 + "/") + ";</script>" + RemoteV3Addon() + RemoteV4Addon();
        int end = html.LastIndexOf("</body>", StringComparison.Ordinal);
        return end > 0 ? html.Insert(end, script) : html + script;
    }

    private static string RemoteHtmlV2()
    {
        const string fallback = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><circle cx='12' cy='12' r='7' fill='none' stroke='white' stroke-width='2'/></svg>";
        string play = RemoteIconData("play", fallback);
        string pause = RemoteIconData("pause", fallback);
        string stop = RemoteIconData("eject", fallback);
        string prev = RemoteIconData("previous", fallback);
        string next = RemoteIconData("next", fallback);
        string skipBack = RemoteIconData("skip-back", fallback);
        string skipForward = RemoteIconData("skip-forward", fallback);
        string back = RemoteIconData("back", fallback);
        string home = RemoteIconData("home", fallback);
        string library = RemoteIconData("library", fallback);
        string queue = RemoteIconData("queue", fallback);
        string photo = RemoteIconData("photo", fallback);
        string info = RemoteIconData("info", fallback);
        string fullscreen = RemoteIconData("maximize", fallback);
        string volume = RemoteIconData("volume-high", fallback);
        string settings = RemoteIconData("settings", fallback);
        string folder = RemoteIconData("folder", fallback);
        string cc = RemoteIconData("subtitles", fallback);
        string audio = RemoteIconData("wave", fallback);
        string power = RemoteIconData("power", "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M12 3v8m5.7-5.1a8 8 0 1 1-11.4 0' fill='none' stroke='white' stroke-width='1.9' stroke-linecap='round'/></svg>");
        string keyboard = RemoteIconData("keyboard", "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><rect x='3' y='6' width='18' height='12' rx='2' fill='none' stroke='white' stroke-width='1.8'/><path d='M6 10h1m3 0h1m3 0h1m3 0h1M6 14h9m2 0h2' stroke='white' stroke-width='1.8' stroke-linecap='round'/></svg>");
        string movie = RemoteIconData("movie", fallback);
        string tv = RemoteIconData("tv", fallback);
        string music = RemoteIconData("music", fallback);
        string chevron = RemoteIconData("chevron-right", fallback);

#if CINECORE_MAC
        const string platform = "mac";
#else
        const string platform = "windows";
#endif
        return $$$$$"""
<!doctype html>
<html lang="it">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover">
<meta name="theme-color" content="#0a0e13">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<link rel="icon" type="image/png" href="/icon.png">
<link rel="manifest" href="/manifest.webmanifest">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-title" content="Cinecore">
<title>Cinecore Remote</title>
<style>
.src-switch{display:flex;gap:18px}.src{border:0;background:transparent;padding:6px 2px 7px;font:inherit;font-size:14px;font-weight:600;color:var(--muted);border-bottom:2px solid transparent}.src.active{color:var(--text);border-bottom-color:var(--accent)}.src.busy{opacity:.55}
:root{color-scheme:dark;--accent:#1682f3;--accent-soft:#12365c;--bg:#05080b;--surface:#0a0f14;--surface2:#10171e;--text:#f3f6f8;--muted:#84919d;--line:rgba(205,220,232,.10);font-family:"Segoe UI Variable","SF Pro Display","Segoe UI",sans-serif}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}html,body{margin:0;min-height:100%;background:var(--bg);color:var(--text)}body{display:flex;justify-content:center;background:var(--bg)}button,input{font:inherit;color:inherit}button{touch-action:manipulation;outline:none;transition:transform .1s ease,background .14s ease,border-color .14s ease,color .14s ease}button:focus-visible{outline:1px solid color-mix(in srgb,var(--accent) 64%,transparent);outline-offset:2px}.app{width:min(440px,100%);min-height:100dvh;position:relative;padding:env(safe-area-inset-top) 17px calc(84px + env(safe-area-inset-bottom));overflow-x:hidden}.page{display:none;animation:pageIn .18s ease-out}.page.active{display:block}
.remote-top{height:54px;display:flex;align-items:center;justify-content:space-between;border-bottom:1px solid var(--line)}.remote-brand{font-size:11px;font-weight:720;letter-spacing:.17em;color:#cbd6df}.power-top{width:36px;height:36px;border:0;background:transparent;display:grid;place-items:center}.power-top img{width:18px;height:18px;filter:brightness(0) invert(1);opacity:.82}.power-top:active img{opacity:1}
.now-card{position:relative;height:188px;margin-top:10px;overflow:hidden;border:1px solid rgba(215,229,240,.13);border-radius:18px;background:#0b1117;box-shadow:0 18px 44px rgba(0,0,0,.28)}.now-card:after{content:"";position:absolute;inset:0;background:linear-gradient(90deg,rgba(4,8,12,.93) 0%,rgba(4,8,12,.55) 52%,rgba(4,8,12,.18) 100%),linear-gradient(0deg,rgba(4,8,12,.9),transparent 60%);pointer-events:none}.now-art{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;background:#111922}.now-copy{position:absolute;z-index:2;left:19px;right:62px;bottom:18px;min-width:0}.now-actions{position:absolute;z-index:3;right:10px;top:10px;display:grid;gap:7px}.now-actions .icon-btn{width:34px;height:34px;background:rgba(5,10,15,.72);backdrop-filter:blur(10px)}.title{font-size:20px;line-height:1.15;font-weight:680;letter-spacing:-.02em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;text-shadow:0 2px 12px #000}.title:empty:after{content:attr(data-empty);color:var(--muted)}.duration{font:10px ui-monospace,SFMono-Regular,Consolas,monospace;color:#c0cad3;margin-top:7px}.now-progress{height:7px;border-radius:5px;background:rgba(255,255,255,.19);margin-top:11px;overflow:hidden;cursor:pointer;touch-action:none}.now-progress i{display:block;width:0;height:100%;border-radius:5px;background:var(--accent);pointer-events:none}.icon-btn,.circle,.transport button{border:1px solid var(--line);background:var(--surface);border-radius:50%;display:grid;place-items:center}.icon-btn{width:38px;height:38px}.icon-btn img,.circle img,.transport img,.tab img,.row-icon img{width:17px;height:17px;filter:brightness(0) invert(1);pointer-events:none}.icon-btn:active,.circle:active,.transport button:active,.dkey:active,.primary:active,.pressed{transform:scale(.96);filter:brightness(1.1)}.icon-btn.pressed,.circle.pressed,.transport button.pressed,.small-action.pressed{background:color-mix(in srgb,var(--accent) 18%,var(--surface));border-color:color-mix(in srgb,var(--accent) 46%,transparent);box-shadow:none}
.quick{display:grid;grid-template-columns:repeat(5,1fr);gap:10px;margin:17px 0 24px}.circle{justify-self:center;width:48px;height:48px;background:linear-gradient(145deg,#121b24,#0a1016);position:relative}.circle span{position:absolute;top:54px;font-size:8px;color:var(--muted);white-space:nowrap}.dots{font-size:18px;letter-spacing:2px;margin-top:-4px}
.dpad-wrap{display:grid;place-items:center;margin:16px 0 13px}.dpad{width:min(246px,66vw);aspect-ratio:1;position:relative;border:1px solid rgba(211,225,236,.13);border-radius:50%;background:radial-gradient(circle at 50% 42%,#17212b 0,#0d141b 50%,#080d12 100%);box-shadow:inset 0 1px 0 rgba(255,255,255,.06),0 18px 38px rgba(0,0,0,.26)}.dpad:before,.dpad:after{content:"";position:absolute;background:rgba(224,235,243,.07)}.dpad:before{left:24px;right:24px;top:50%;height:1px}.dpad:after{left:50%;top:24px;bottom:24px;width:1px}.dkey{position:absolute;width:64px;height:55px;border:0;border-radius:28px;background:transparent;display:grid;place-items:center;z-index:2}.dkey:active,.dkey.pressed{background:rgba(255,255,255,.07)}.dkey svg{width:22px;fill:none;stroke:#edf3f7;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}.dkey.up{left:calc(50% - 32px);top:12px}.dkey.down{left:calc(50% - 32px);bottom:12px}.dkey.left{left:12px;top:calc(50% - 27px)}.dkey.right{right:12px;top:calc(50% - 27px)}.center{position:absolute;z-index:3;width:72px;height:72px;left:50%;top:50%;transform:translate(-50%,-50%);border-radius:50%;border:1px solid rgba(116,183,255,.65);background:linear-gradient(145deg,#2492ff,#0768d2);box-shadow:0 8px 24px rgba(0,106,224,.34),inset 0 1px rgba(255,255,255,.2)}.center:active{transform:translate(-50%,-50%) scale(.96)}.center img{width:28px;height:28px;filter:brightness(0) invert(1);margin-left:3px}
.transport{display:grid;grid-template-columns:repeat(3,44px);justify-content:center;gap:13px;margin:0 auto 12px}.transport button{width:44px;height:44px;background:#0c131a}.transport .stop img{width:12px;height:12px}.sliders{border-top:1px solid var(--line);padding-top:9px}.slider{display:grid;grid-template-columns:50px minmax(0,1fr) 50px;align-items:center;gap:8px;min-height:40px}.slider.vol{grid-template-columns:25px minmax(0,1fr) 50px}.slider time,.slider output{font:10px ui-monospace,SFMono-Regular,Consolas,monospace;color:#b6c0c9;text-align:right}.slider button{border:0;background:transparent;padding:0}.slider img{width:18px;height:18px;filter:brightness(0) invert(1)}input[type=range]{--progress:0%;appearance:none;width:100%;height:26px;background:transparent}input[type=range]::-webkit-slider-runnable-track{height:3px;border-radius:3px;background:linear-gradient(90deg,var(--accent) 0 var(--progress),rgba(255,255,255,.17) var(--progress) 100%)}input[type=range]::-webkit-slider-thumb{appearance:none;width:13px;height:13px;border-radius:50%;margin-top:-5px;background:#f3f7fa;border:2px solid var(--accent);box-shadow:0 2px 7px #0007}
.audio-out-row{grid-column:1/-1;display:grid;gap:6px;padding-top:4px}.audio-out-row span{font-size:11px;font-weight:650;text-align:center}.audio-out-row select{width:100%;height:38px;font-size:11px}.small-action[disabled]{opacity:.38}
.sync-panel{grid-column:1/-1;display:grid;gap:8px;padding:12px 2px 4px;justify-items:center}.sync-head{display:grid;justify-items:center;gap:2px}.sync-head span{font-size:12px;font-weight:650}.sync-head small,.sync-row small{font-size:9px;color:var(--muted)}.sync-ctl{display:flex;align-items:center;justify-content:center;gap:2px}.sync-ctl button{background:transparent;border:0;color:var(--muted);font-size:13px;font-weight:600;min-width:44px;height:40px;border-radius:6px}.sync-ctl button:active{background:var(--surface);color:#fff}.sync-ctl button[disabled],.sync-reset[disabled]{opacity:.35}.sync-value{min-width:104px;text-align:center;font-size:22px;font-weight:650;font-variant-numeric:tabular-nums;color:var(--muted)}.sync-value.changed{color:#fff}.sync-reset{height:26px;padding:0 14px;border:0;border-radius:999px;background:var(--surface);color:var(--muted);font-size:10px}#settingsPage .setting-row.sync-row{display:grid;grid-template-columns:1fr;justify-items:stretch;gap:8px}#settingsPage .sync-row .sync-ctl{justify-content:space-between}#settingsPage .sync-row .sync-reset{justify-self:center}
.more{display:none;margin-top:8px;border-top:1px solid var(--line);padding-top:13px;grid-template-columns:repeat(4,1fr);gap:8px}.more.open{display:grid}.small-action{height:43px;border:1px solid var(--line);border-radius:5px;background:var(--surface);font-size:10px}.small-action.active{border-color:var(--accent);color:#fff;background:color-mix(in srgb,var(--accent) 18%,var(--surface))}
.page-head{padding:24px 2px 14px;border-bottom:1px solid var(--line)}.page-head h1{font-size:25px;line-height:1.1;margin:0;letter-spacing:-.025em}.page-head p{display:none}.list{padding-top:8px}.media-row,.category-row,.setting-row{min-height:68px;border-bottom:1px solid var(--line);display:grid;align-items:center;gap:13px}.media-row,.category-row{width:100%;padding:0;border:0;border-bottom:1px solid var(--line);background:transparent;color:var(--text);text-align:left;font:inherit}.media-row{grid-template-columns:46px minmax(0,1fr) 20px}.category-row{grid-template-columns:minmax(0,1fr) auto;min-height:54px}.row-icon{width:42px;height:42px;border-radius:10px;background:var(--surface2);display:grid;place-items:center}.row-icon img{opacity:.76}.row-title{display:block;font-size:13px;font-weight:650;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.row-sub{display:block;font-size:10px;color:var(--muted);margin-top:4px}.chev{color:var(--muted);font-size:22px}.media-row.current .row-title,.category-row:active .row-title{color:var(--accent)}.empty{padding:72px 20px;text-align:center;color:var(--muted);font-size:12px}.library-section{padding:4px 0 22px}.library-section[hidden]{display:none}.mini-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:9px}.mini-card{padding:0;border:1px solid transparent;border-radius:9px;overflow:hidden;background:var(--surface);text-align:left;min-width:0}.mini-art{display:block;width:100%;aspect-ratio:2/3;object-fit:cover;background:#151b22}.mini-card[data-kind="music"] .mini-art{aspect-ratio:1}.mini-copy{display:block;padding:9px}.mini-title{display:block;font-size:10px;font-weight:660;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.mini-sub{display:block;font-size:8px;color:var(--muted);margin-top:3px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.library-search{position:sticky;top:0;z-index:4;padding:11px 0 8px;background:linear-gradient(var(--bg) 78%,transparent)}.library-search input{width:100%;height:42px;padding:0 14px;border:1px solid var(--line);border-radius:11px;background:var(--surface);outline:none}.library-search input:focus{border-color:color-mix(in srgb,var(--accent) 62%,transparent)}.media-sheet{position:fixed;z-index:35;inset:env(safe-area-inset-top) 0 calc(76px + env(safe-area-inset-bottom));margin:auto;width:min(440px,100%);padding:16px 18px;overflow:auto;background:var(--bg);transform:translateX(104%);transition:transform .2s ease}.media-sheet.open{transform:none}.media-sheet-head{height:48px;display:grid;grid-template-columns:36px minmax(0,1fr);align-items:center;gap:11px;border-bottom:1px solid var(--line);margin-bottom:8px}.media-sheet-head button{width:34px;height:34px;border:1px solid var(--line);border-radius:50%;background:var(--surface)}.media-sheet-head strong{font-size:15px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.setting-section{font-size:12px;letter-spacing:.02em;color:var(--accent);margin:23px 0 8px}.setting-row{grid-template-columns:minmax(0,1fr) auto;min-height:58px}.setting-row span{font-size:13px}.setting-row small{display:block;color:var(--muted);font-size:9px;margin-top:3px}.toggle{width:42px;height:24px;border-radius:20px;background:#29313a;position:relative;border:0}.toggle:after{content:"";position:absolute;width:18px;height:18px;left:3px;top:3px;border-radius:50%;background:#9da7b2;transition:.15s}.toggle.on{background:var(--accent)}.toggle.on:after{left:21px;background:white}.primary{height:48px;width:100%;border:1px solid color-mix(in srgb,var(--accent) 62%,white);border-radius:5px;background:var(--accent);font-weight:700;margin-top:18px}
body.light img[src^="data:image/svg"]{filter:invert(1) brightness(.35)}body.light .keyboard-sheet,body.light .track-sheet{background:var(--surface);box-shadow:0 -14px 36px #0003}body.light .tabs{background:color-mix(in srgb,var(--bg) 92%,transparent)}body.light .circle,body.light #remotePage .quick,body.light .stop-row button,body.light .library-tabs,body.light .mini-card,body.light .library-search input,body.light .keyboard-input,body.light .transport button:not(.play-main){background:var(--surface)!important;border-color:var(--line)!important}body.light .dpad,body.light .nav-pad{background:transparent!important;border-color:transparent!important;box-shadow:none!important}body.light .transport .play-main img{filter:none!important}body.light .nav-pad .dkey{background:var(--bg)!important;border-color:var(--line)!important}body.light .nav-pad .dkey.ok,body.light .sync-value.changed,body.light .track-option.selected,body.light .remote-brand,body.light .provider-tabs button.active,body.light .small-action.active{color:var(--text)!important}body.light .setting-value,body.light .renderer-select,body.light select{color:var(--text)!important;background:var(--surface)!important}body.light .slider time,body.light .slider output{color:var(--muted)!important}body.light .tabs{background:var(--bg)!important}body.light input[type=range]::-webkit-slider-thumb{background:var(--accent)}.color-row{min-height:64px;border-bottom:1px solid var(--line);display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:12px}.color-row>div:first-child span{display:block;font-size:13px;font-weight:650}.color-row>div:first-child small{font-size:10px;color:var(--muted)}.swatches{display:flex;gap:9px;align-items:center}.swatch{width:24px;height:24px;border-radius:50%;border:1px solid var(--line);padding:0;position:relative}.swatch.on{box-shadow:0 0 0 2px var(--bg),0 0 0 3.5px var(--text)}.swatch.free{background:conic-gradient(#ff4848,#ffc428,#46d25a,#28aaff,#965aff,#ff48a0,#ff4848)!important;overflow:hidden}.swatch.free input{position:absolute;inset:0;width:100%;height:100%;opacity:0;border:0;padding:0;cursor:pointer}.swatch.free::after{content:'';position:absolute;inset:5px;border-radius:50%;background:var(--fill,var(--surface))}
.opt-pad{padding:0 16px 8px}.opt-pad .opt-sync{padding-top:14px}.opt-pad .opt-sync output{font-size:38px}.opt-select{cursor:pointer}.opt-select select{max-width:200px;border:0;background:transparent;color:var(--muted);font:inherit;font-size:13px;text-align:right;text-align-last:right;appearance:none;-webkit-appearance:none}.track-extra{display:grid;grid-template-columns:1fr 1fr;gap:8px;padding-top:12px}.track-extra button{height:44px;border:0;border-radius:999px;background:var(--surface2);color:var(--text);font-size:12px;font-weight:600}.track-extra button[disabled]{opacity:.36}
:root{--btn-radius:12px}.setting-value,.renderer-select,.opt-actions button,.track-extra button,#trackClose,#keyboardClose,.keyboard-actions button,.sync-reset,.small-action,.primary,.opt-head button,.film-actions button{border-radius:var(--btn-radius)!important}.film-head{display:grid;grid-template-columns:86px minmax(0,1fr);gap:14px;align-items:end;padding:6px 2px 4px}.film-head img{width:86px;aspect-ratio:2/3;object-fit:cover;border-radius:10px;background:var(--surface)}.film-head strong{display:block;font-size:19px;font-weight:650;line-height:1.2}.film-head small{display:block;margin-top:6px;color:var(--muted);font-size:12px;line-height:1.5}.film-actions{display:grid;grid-template-columns:repeat(3,1fr);gap:8px;margin-top:16px}.film-actions button{height:44px;border:0;background:var(--surface);color:var(--text);font-size:12.5px;font-weight:600}.film-actions button[disabled]{opacity:.36}.film-actions button:active{background:var(--surface2)}.film-overview{margin:0 2px;color:var(--text);font-size:13.5px;line-height:1.6;opacity:.9}.cast-strip{display:flex;gap:12px;overflow-x:auto;padding:2px 2px 6px;scroll-snap-type:x proximity}.cast-strip::-webkit-scrollbar{display:none}.cast-card{flex:0 0 96px;scroll-snap-align:start}.cast-card img,.cast-card .ph{width:96px;height:128px;object-fit:cover;border-radius:10px;background:var(--surface);display:grid;place-items:center;color:var(--muted);font-weight:650}.cast-card b{display:block;margin-top:7px;font-size:12px;font-weight:650;line-height:1.3}.cast-card span{display:block;color:var(--muted);font-size:11px;line-height:1.3}.tech-list{display:grid;gap:0}.tech-list div{padding:12px 16px;border-bottom:1px solid var(--line);font-size:13px}.tech-list div:last-child{border-bottom:0}.hdr-grid{display:grid;grid-template-columns:1fr 1fr;gap:0}.hdr-grid div{padding:12px 16px;border-bottom:1px solid var(--line)}.hdr-grid div:nth-child(odd){border-right:1px solid var(--line)}.hdr-grid small{display:block;color:var(--muted);font-size:11px}.hdr-grid b{display:block;margin-top:3px;font-size:18px;font-weight:650;font-variant-numeric:tabular-nums}.hdr-formats{padding:12px 16px 0;color:var(--muted);font-size:12px}.sync2{display:grid;gap:12px;padding:16px}.sync-field{display:flex;align-items:center;gap:8px;height:52px;padding:0 16px;border-radius:var(--btn-radius);background:var(--bg)}.sync-field input{flex:1;min-width:0;border:0;background:transparent;color:var(--text);font:inherit;font-size:22px;font-weight:650;font-variant-numeric:tabular-nums;outline:none}.sync-field span{color:var(--muted);font-size:14px}.sync2 .opt-actions button{height:46px}.sync2 .opt-actions button:not(.lead){background:var(--surface2,rgba(255,255,255,.08));color:var(--text)}.hdr-grid div:nth-last-child(-n+2){border-bottom:0}
.opt-sheet{position:fixed;z-index:36;left:0;right:0;top:0;bottom:calc(64px + env(safe-area-inset-bottom));margin:0 auto;width:min(480px,100%);overflow:auto;background:var(--bg);transform:translateX(104%);transition:transform .22s ease;padding:calc(6px + env(safe-area-inset-top)) 16px 28px}.opt-sheet.open{transform:none}.opt-head{position:sticky;top:0;z-index:1;background:var(--bg);height:58px;display:grid;grid-template-columns:44px minmax(0,1fr) 44px;align-items:center}.opt-head strong{text-align:center;font-size:17px;font-weight:650;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.opt-head button{width:44px;height:44px;border:0;border-radius:50%;background:transparent;color:var(--text);font-size:26px;line-height:1;display:grid;place-items:center}.opt-head button:active{background:var(--surface)}.opt-head button.hidden{visibility:hidden}.opt-sec{margin:20px 6px 8px;font-size:11px;font-weight:650;letter-spacing:.07em;text-transform:uppercase;color:var(--muted)}.opt-group{border-radius:14px;background:var(--surface);overflow:hidden}.opt-row{width:100%;min-height:56px;padding:0 16px;border:0;border-bottom:1px solid var(--line);background:transparent;display:grid;grid-template-columns:minmax(0,1fr) auto 16px;align-items:center;gap:10px;text-align:left;color:var(--text);font-size:15px}.opt-row:last-child{border-bottom:0}.opt-row:active{background:var(--surface2)}.opt-row[disabled]{opacity:.36}.opt-row small{color:var(--muted);font-size:13px;max-width:190px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.opt-row .chev{color:var(--muted);font-size:22px;line-height:1;text-align:right}.opt-row .tick{color:var(--accent);font-size:17px;font-weight:700;text-align:right}.opt-sync{display:grid;justify-items:center;gap:18px;padding:22px 0 6px}.opt-sync output{font-size:46px;font-weight:650;font-variant-numeric:tabular-nums;letter-spacing:-.01em}.opt-steps{display:grid;grid-template-columns:repeat(4,1fr);gap:8px;width:100%}.opt-steps button{height:52px;border:0;border-radius:12px;background:var(--surface);font-size:15px;font-weight:600;color:var(--text)}.opt-steps button:active,.opt-actions button:active{background:var(--surface2)}.opt-actions{display:grid;grid-template-columns:1fr 1fr;gap:8px;width:100%}.opt-actions button{height:48px;border:0;border-radius:999px;background:var(--surface);font-size:14px;font-weight:600;color:var(--text)}.opt-actions button.lead{background:var(--accent);color:#fff}.opt-actions button[disabled],.opt-steps button[disabled]{opacity:.36}.opt-note{color:var(--muted);font-size:12.5px;text-align:center;line-height:1.45;max-width:340px}.opt-progress{width:100%;height:4px;border-radius:2px;background:var(--surface2);overflow:hidden}.opt-progress i{display:block;height:100%;background:var(--accent);transition:width .3s ease}.more{display:none!important}.sync-row{display:none!important}.opt-link{width:100%;min-height:58px;border:0;border-bottom:1px solid var(--line);background:transparent;display:grid;grid-template-columns:minmax(0,1fr) auto 16px;align-items:center;gap:10px;text-align:left;color:var(--text);padding:0}.opt-link span:first-child{font-size:13px;font-weight:650}.opt-link small{color:var(--muted);font-size:12px}.opt-link .chev{color:var(--muted);font-size:20px}
.keyboard-sheet,.track-sheet{position:fixed;z-index:30;left:50%;bottom:calc(72px + env(safe-area-inset-bottom));width:min(430px,calc(100% - 24px));transform:translate(-50%,120%);opacity:0;pointer-events:none;padding:14px;border:1px solid var(--line);border-radius:8px;background:rgba(8,13,18,.99);box-shadow:0 -14px 36px #0009;transition:transform .18s ease,opacity .18s ease}.keyboard-sheet.open,.track-sheet.open{transform:translate(-50%,0);opacity:1;pointer-events:auto}.keyboard-top{display:flex;align-items:center;justify-content:space-between;margin-bottom:10px}.keyboard-top strong{font-size:12px}.keyboard-top button{width:32px;height:32px;border:1px solid var(--line);border-radius:5px;background:var(--surface2)}.keyboard-input{width:100%;height:46px;padding:0 13px;border:1px solid color-mix(in srgb,var(--accent) 42%,var(--line));border-radius:6px;background:#070c11;outline:none;font-size:16px}.keyboard-actions{display:grid;grid-template-columns:1fr 1fr 1.4fr;gap:7px;margin-top:9px}.keyboard-actions button{height:40px;border:1px solid var(--line);border-radius:5px;background:var(--surface)}.keyboard-actions .send{background:var(--accent);border-color:transparent;font-weight:650}.track-list{max-height:min(56vh,430px);overflow:auto;border-top:1px solid var(--line)}.track-option{width:100%;min-height:50px;padding:0 4px;border:0;border-bottom:1px solid var(--line);background:transparent;display:grid;grid-template-columns:minmax(0,1fr) 18px;align-items:center;text-align:left;font-size:12px}.track-option span:first-child{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.track-mark{width:10px;height:10px;border:1px solid #607181;border-radius:50%}.track-option.selected{color:#fff}.track-option.selected .track-mark{border:3px solid var(--accent)}.danger-action{width:100%;height:46px;margin:18px 0 4px;border:1px solid rgba(224,104,104,.34);border-radius:5px;background:rgba(112,28,28,.12);color:#efb4b4;display:flex;align-items:center;justify-content:center;gap:9px}.danger-action img{width:17px;height:17px;filter:brightness(0) invert(84%) sepia(20%) saturate(640%) hue-rotate(307deg)}
.tabs{position:fixed;z-index:10;left:50%;transform:translateX(-50%);bottom:0;width:min(440px,100%);height:76px;padding:7px 12px max(8px,env(safe-area-inset-bottom));display:grid;grid-template-columns:repeat(4,1fr);background:rgba(6,10,14,.91);backdrop-filter:blur(20px);border-top:1px solid var(--line)}.tab{border:0;background:transparent;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:5px;color:#7f8a96;font-size:9px}.tab:focus{outline:none;box-shadow:none}.tab img{width:19px;height:19px;opacity:.58}.tab.active{color:var(--accent)}.tab.active img{opacity:1;filter:brightness(0) saturate(100%) invert(51%) sepia(91%) saturate(1874%) hue-rotate(199deg) brightness(95%) contrast(91%)}.setting-value{min-width:78px;height:34px;padding:0 11px;border:1px solid var(--line);border-radius:5px;background:var(--surface);color:#dce4eb;font-size:10px}@keyframes pageIn{from{opacity:0;transform:translateY(5px)}to{opacity:1;transform:none}}@media(max-width:360px){.app{padding-left:14px;padding-right:14px}.dpad{width:238px}.quick{gap:6px}.transport{gap:10px}}
.icon-btn.pressed,.circle.pressed,.transport button.pressed,.small-action.pressed,.dkey.pressed,.tab.pressed{background:color-mix(in srgb,var(--accent) 17%,var(--surface));border-color:color-mix(in srgb,var(--accent) 48%,transparent);box-shadow:none}.center.pressed{transform:translate(-50%,-50%) scale(.96);background:linear-gradient(145deg,#3aa0ff,#0872e2);box-shadow:0 4px 16px rgba(0,106,224,.28),inset 0 1px rgba(255,255,255,.22)}.tab.pressed{transform:translateY(1px)}.mini-card:active,.mini-card.pressed{border-color:color-mix(in srgb,var(--accent) 55%,transparent);background:color-mix(in srgb,var(--accent) 9%,var(--surface))}
.now-card{height:178px;margin-top:13px;border-radius:10px;border-color:rgba(214,226,236,.12);box-shadow:none}.now-actions{right:9px;top:9px}.now-actions .icon-btn{background:rgba(4,8,12,.64);backdrop-filter:none}.quick{margin:16px 0 22px}.circle{width:46px;height:46px;background:#0b1117;box-shadow:none}.dpad-wrap{margin:13px 0 12px}.dpad{width:min(226px,62vw);background:#0b1117;box-shadow:none;border-color:rgba(211,225,236,.12)}.dpad:before,.dpad:after{background:rgba(224,235,243,.055)}.center{width:64px;height:64px;border-color:rgba(105,177,250,.55);background:#126fcf;box-shadow:none}.center img{width:24px;height:24px;margin-left:0}.center.pressed{background:#197bdc;box-shadow:none}.transport{grid-template-columns:repeat(5,40px);gap:9px;margin-bottom:10px}.transport button{width:40px;height:40px;background:transparent;border-color:transparent}.transport button:hover,.transport button.pressed{background:#101820;border-color:var(--line)}.transport img{width:16px;height:16px}.transport .stop img{width:11px;height:11px}.more{grid-template-columns:repeat(2,1fr)}.library-tabs{display:grid;grid-template-columns:repeat(3,1fr);gap:5px;margin:11px 0 3px;padding:3px;background:#080d12;border:1px solid var(--line);border-radius:7px}.library-tab{height:34px;border:0;border-radius:4px;background:transparent;color:var(--muted);font-size:10px;font-weight:650}.library-tab.active{background:#131c24;color:#f2f6f9}.mini-grid{grid-template-columns:repeat(3,minmax(0,1fr));gap:8px}.mini-card{border-radius:5px;background:#0a1016}.mini-card[data-kind="album"] .mini-art,.mini-card[data-kind="track"] .mini-art,.mini-card[data-kind="resume-music"] .mini-art{aspect-ratio:1}.mini-copy{padding:8px}.library-search{top:0;padding-top:7px}.library-search input{border-radius:6px;background:#090f14}.page-head{padding-top:20px}.tabs{background:rgba(5,8,11,.96);backdrop-filter:none}
.remote-top{height:44px;justify-content:flex-end;border-bottom:0}.remote-brand{display:none}.now-card{margin-top:2px}.quick{margin:14px 0 14px}.circle{border-radius:6px}.dpad-wrap{position:relative;isolation:isolate;margin:8px 0 15px;min-height:158px}.nav-pad{width:174px;display:grid;grid-template-columns:repeat(3,52px);grid-template-rows:repeat(3,42px);gap:5px;padding:7px;border:1px solid var(--line);background:#080d12}.nav-pad .dkey{position:static;width:52px;height:42px;border:0;border-radius:4px;background:#0d141b}.nav-pad .dkey.up{grid-column:2;grid-row:1}.nav-pad .dkey.left{grid-column:1;grid-row:2}.nav-pad .dkey.ok{grid-column:2;grid-row:2;color:white;font-size:11px;font-weight:700;border:1px solid color-mix(in srgb,var(--accent) 48%,transparent);background:color-mix(in srgb,var(--accent) 17%,#0d141b)}.nav-pad .dkey.right{grid-column:3;grid-row:2}.nav-pad .dkey.down{grid-column:2;grid-row:3}.transport{grid-template-columns:repeat(7,36px);gap:6px;margin-top:0}.transport button{width:36px;height:36px}.transport .play-main{grid-column:4;background:var(--accent);border-color:transparent}.transport .play-main img{width:17px;height:17px}.transport .stop{grid-column:7}.setting-value[disabled]{opacity:.42}.renderer-select{width:112px;height:34px;padding:0 8px;border:1px solid var(--line);border-radius:5px;background:var(--surface);color:#dce4eb;font-size:10px}.renderer-note{color:var(--muted);font-size:9px}.mini-art.fallback-art{display:grid;place-items:center;background:#101820}.fallback-art img{width:30%;height:30%;filter:brightness(0) invert(1);opacity:.48}.mini-art.fallback-image{object-fit:contain;padding:34%;filter:brightness(0) invert(1);opacity:.48}.more .small-action{display:grid;gap:2px;place-content:center}.more .small-action small{font-size:8px;color:var(--muted);font-weight:400}
#remotePage.active{display:flex;flex-direction:column;min-height:calc(100dvh - 84px - env(safe-area-inset-top) - env(safe-area-inset-bottom))}.transport{grid-template-columns:38px 44px 54px 44px 38px;align-items:center;gap:10px;margin:0 auto 4px}.transport button{width:38px;height:38px;border:0;background:transparent}.transport button:nth-child(2),.transport button:nth-child(4){width:44px;height:44px}.transport .play-main{grid-column:auto;width:54px;height:54px;background:var(--accent);border:0}.transport .play-main img{width:21px;height:21px}.transport img{width:16px;height:16px}.stop-row{height:38px;display:grid;place-items:center;margin-bottom:5px}.stop-row button{width:44px;height:32px;border:1px solid var(--line);border-radius:4px;background:#0b1117;display:grid;place-items:center}.stop-row button img{width:12px;height:12px;filter:brightness(0) invert(1)}.stop-row button:active,.stop-row button.pressed{transform:scale(.96);background:#121b23;border-color:rgba(213,225,235,.22)}.sliders{margin-top:auto}.slider{min-height:38px}@media(max-height:760px){.now-card{height:154px}.quick{margin:10px 0}.dpad-wrap{min-height:148px;margin:4px 0 8px}.nav-pad{transform:scale(.94)}.transport{margin-top:0}.sliders{margin-top:5px}}
.quick{margin:15px 0 18px}.dpad-wrap{min-height:184px;margin:18px 0 22px}.nav-pad{width:212px;grid-template-columns:repeat(3,60px);grid-template-rows:repeat(3,48px);gap:5px;padding:12px;border:1px solid rgba(210,224,235,.10);border-radius:25px;background:#080d12}.nav-pad .dkey{width:60px;height:48px;border-radius:14px;background:#0d141b;border:1px solid rgba(210,224,235,.055)}.nav-pad .dkey.ok{border-radius:17px;background:#111d28;border-color:color-mix(in srgb,var(--accent) 42%,transparent);font-size:10px;letter-spacing:.08em}.nav-pad .dkey:active,.nav-pad .dkey.pressed{background:#16232e;border-color:rgba(210,224,235,.16)}.transport{width:100%;grid-template-columns:34px 38px 44px 56px 44px 38px 34px;justify-content:space-between;gap:5px;margin:0 auto 8px}.transport button,.transport button:nth-child(2),.transport button:nth-child(4){width:38px;height:38px}.transport button:nth-child(3),.transport button:nth-child(5){width:44px;height:44px}.transport .play-main{width:56px!important;height:56px!important}.transport .utility{width:34px;height:34px;opacity:.76}.transport .stop img{width:11px;height:11px}.sliders{margin-top:8px;padding-top:8px}.nested-settings{display:none;margin:0 0 8px 14px;padding:0 0 0 13px;border-left:1px solid color-mix(in srgb,var(--accent) 32%,var(--line))}.nested-settings.open{display:block}.nested-settings .setting-row{min-height:54px}.setting-row.compact{min-height:52px}@media(max-height:760px){.now-card{height:146px}.quick{margin:9px 0 13px}.dpad-wrap{min-height:163px;margin:10px 0 13px}.nav-pad{transform:scale(.91)}.transport{margin-bottom:3px}.sliders{margin-top:3px}}
.now-actions{right:10px;top:10px;gap:7px}.now-actions .icon-btn{width:36px;height:36px;border-radius:6px;border-color:rgba(220,232,241,.13);background:rgba(5,9,13,.78)}.now-actions .stop img{width:11px;height:11px}.quick{margin:16px 0 20px}.dpad-wrap{min-height:224px;margin:26px 0 24px}.nav-pad{position:relative;display:block;width:220px;height:220px;padding:0;border:1px solid rgba(213,226,236,.13);border-radius:50%;background:radial-gradient(circle at 50% 48%,#111a22 0,#0a1016 68%,#070b0f 100%);overflow:hidden}.nav-pad:before,.nav-pad:after{content:"";position:absolute;left:27px;right:27px;top:50%;height:1px;background:rgba(222,233,241,.055);transform-origin:center}.nav-pad:after{transform:rotate(90deg)}.nav-pad .dkey{position:absolute;margin:0;border:0;background:transparent;display:grid;place-items:center;border-radius:0}.nav-pad .dkey.up{left:47px;top:8px;width:126px;height:78px;clip-path:polygon(16% 0,84% 0,64% 100%,36% 100%)}.nav-pad .dkey.down{left:47px;bottom:8px;width:126px;height:78px;clip-path:polygon(36% 0,64% 0,84% 100%,16% 100%)}.nav-pad .dkey.left{left:8px;top:47px;width:78px;height:126px;clip-path:polygon(0 16%,100% 36%,100% 64%,0 84%)}.nav-pad .dkey.right{right:8px;top:47px;width:78px;height:126px;clip-path:polygon(0 36%,100% 16%,100% 84%,0 64%)}.nav-pad .dkey.ok{left:50%;top:50%;width:74px;height:74px;transform:translate(-50%,-50%);border-radius:50%;clip-path:none;border:1px solid color-mix(in srgb,var(--accent) 48%,transparent);background:#101b25;font-size:11px;letter-spacing:.08em}.nav-pad .dkey:active,.nav-pad .dkey.pressed{background:color-mix(in srgb,var(--accent) 15%,#111a22)}.nav-pad .dkey.ok:active,.nav-pad .dkey.ok.pressed{transform:translate(-50%,-50%) scale(.96);background:color-mix(in srgb,var(--accent) 24%,#101b25)}.transport{width:auto;grid-template-columns:40px 46px 58px 46px 40px;justify-content:center;align-items:center;gap:14px;margin:0 auto 12px}.transport button,.transport button:nth-child(2),.transport button:nth-child(4){width:40px;height:40px}.transport button:nth-child(2),.transport button:nth-child(4){width:46px;height:46px}.transport .play-main{width:58px!important;height:58px!important}.sliders{margin-top:12px;padding-top:10px}@media(max-height:760px){.now-card{height:142px}.quick{margin:9px 0 14px}.dpad-wrap{min-height:194px;margin:16px 0 17px}.nav-pad{transform:scale(.86)}.transport{margin-top:-7px;margin-bottom:4px}.sliders{margin-top:4px}}
/* Reference layout: equal navigation radii, image controls, quiet black chrome. */
:root{--bg:#070709;--surface:#0b0e11;--accent:#087cff;--line:rgba(255,255,255,.07)}
.app{max-width:440px;padding:env(safe-area-inset-top) 20px 88px}
.remote-top{order:0;height:70px;display:flex;justify-content:center;align-items:center;position:relative}.remote-top .power-top{position:absolute;right:0}
.remote-brand{display:block;font-size:17px;letter-spacing:.28em;font-weight:400;color:#fff}
#remotePage .now-card{order:1;flex-shrink:0;height:clamp(220px,calc(100dvh - 622px),350px);margin:0 -20px;border:0;border-radius:0;box-shadow:none;background:#08080a}
.now-art{opacity:.85}.now-copy{left:24px;right:24px;bottom:17px}.now-copy .title{font-size:21px}.now-copy .duration{font-size:11px;margin-top:5px}
.now-actions{right:22px;top:14px}.now-actions .icon-btn{border:0;background:#0004;border-radius:4px}
.now-progress{height:3px;margin-top:14px}.now-progress i:after{content:"";position:absolute;right:-5px;top:-4px;width:11px;height:11px;border-radius:50%;background:white}.now-progress i{position:relative}
#remotePage .dpad-wrap{order:2;min-height:0;margin:16px 0 14px;display:grid;place-items:center}
#remotePage .nav-pad{width:244px;height:244px;transform:none;border:0;border-radius:0;background:transparent;overflow:visible}
.nav-pad:before,.nav-pad:after{display:none}
#remotePage .nav-pad .dkey{width:56px;height:56px;inset:auto;border:0;border-radius:50%;clip-path:none;background:transparent;transform:none}
#remotePage .nav-pad .up{left:94px;top:0}#remotePage .nav-pad .left{left:0;top:94px}#remotePage .nav-pad .ok{left:94px;top:94px;font-size:17px;letter-spacing:0}#remotePage .nav-pad .right{left:188px;top:94px}#remotePage .nav-pad .down{left:94px;top:188px}
.nav-pad svg{width:32px;height:32px;stroke-width:1.6}
#remotePage .transport{order:3;width:100%;grid-template-columns:1fr 1fr 66px 1fr 1fr;gap:9px;justify-items:center;margin:0 0 20px}
#remotePage .transport .play-main{width:66px!important;height:66px!important;border-radius:50%;background:var(--accent)}.transport .play-main img{width:26px;height:26px}.transport img{width:24px;height:24px}
#remotePage .quick{order:4;display:grid;grid-template-columns:repeat(4,1fr);gap:0;border:1px solid var(--line);border-radius:30px;background:#0b0e11;margin:0 25px 15px;padding:6px}
.quick .circle{width:100%;height:42px;border:0;border-radius:0;background:none}.quick .circle span{display:none}.quick .circle img{width:22px;height:22px}.quick .circle:last-child{grid-column:1;grid-row:1}.quick .dots{font-size:20px}
#remotePage .sliders{order:5;margin-top:3px;border:0;padding:0}#remotePage .more{order:6}
.slider{grid-template-columns:24px minmax(0,1fr) 42px;gap:14px;min-height:38px}input[type=range]::-webkit-slider-thumb{width:16px;height:16px;border:0;margin-top:-6px;background:white}.tabs{background:#070709;border-color:var(--line);height:78px}.tab{font-size:10px}.tab img{width:23px;height:23px}
@media(max-height:860px){#remotePage .now-card{height:clamp(190px,calc(100dvh - 550px),310px)}#remotePage .dpad-wrap{margin:6px 0}#remotePage .nav-pad{width:210px;height:210px}#remotePage .nav-pad .up{left:77px}#remotePage .nav-pad .left{top:77px}#remotePage .nav-pad .ok{left:77px;top:77px}#remotePage .nav-pad .right{left:154px;top:77px}#remotePage .nav-pad .down{left:77px;top:154px}#remotePage .transport{margin-bottom:12px}.remote-top{height:54px}}
.now-time-row{display:grid;grid-template-columns:auto minmax(0,1fr) auto;align-items:center;gap:14px;margin-top:15px;font-size:11px;color:#eee}.now-time-row .now-progress{margin:0;height:3px;overflow:visible;touch-action:none;cursor:pointer}.now-time-row .now-progress:before{content:"";position:absolute;inset:-14px 0}.now-time-row .now-progress{position:relative}
.dkey img{width:22px;height:22px;filter:brightness(0) invert(1)}.dkey.up img{transform:rotate(-90deg)}.dkey.down img{transform:rotate(90deg)}.dkey.left img{transform:rotate(180deg)}.chev img{width:14px;height:14px;opacity:.7}
#remotePage .now-card.no-playback:after{z-index:1;background:#0a1016;pointer-events:none}
#remotePage .now-card.no-playback .now-art,#remotePage .now-card.no-playback .now-actions,#remotePage .now-card.no-playback .duration,#remotePage .now-card.no-playback .now-time-row{display:none!important}
#remotePage .now-card.no-playback .now-copy{left:20px;right:20px;top:50%;bottom:auto;transform:translateY(-50%);text-align:center}
.native-section{font-size:12px;letter-spacing:.02em;color:var(--accent);margin:23px 0 8px}
.provider-row{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:12px;min-height:58px;border-bottom:1px solid var(--line);cursor:pointer}
.provider-row span{font-size:13px}.provider-row small{display:block;color:var(--muted,#8b97a3);font-size:11px;margin-top:3px}
.provider-row img{width:16px;height:16px;opacity:.6}.provider-row:active{opacity:.7}
.provider-empty{color:var(--muted,#8b97a3);font-size:12px;padding:14px 0}
.provider-head{position:relative}.provider-head small{display:block;color:var(--muted,#8b97a3);font-size:11px;margin-top:6px}
.back-link{background:none;border:0;color:var(--accent);font-size:13px;padding:0 0 10px;cursor:pointer}
.provider-tabs{display:flex;gap:18px;overflow-x:auto;padding:14px 2px 2px;scrollbar-width:none}.provider-tabs::-webkit-scrollbar{display:none}
.provider-tabs button{background:none;border:0;border-bottom:2px solid transparent;color:var(--muted,#8b97a3);font-size:12px;padding:6px 0;white-space:nowrap;cursor:pointer}
.provider-tabs button.active{color:#fff;border-bottom-color:var(--accent)}
.native-row small.err{color:#ff7a86}
.native-row select{max-width:150px}
.swatches{display:flex;gap:9px;align-items:center}.swatch{width:24px;height:24px;border-radius:50%;border:2px solid transparent;padding:0;cursor:pointer}.swatch.on{border-color:var(--text,#f3f6f8)}
.block-tools{display:flex;gap:10px;align-items:center}.mini{width:34px;height:30px;border:0;border-radius:6px;background:none;color:var(--muted,#8b97a3);font-size:17px;cursor:pointer}.mini:disabled{opacity:.25}
.pref-row select{max-width:170px}
/* Telefono: niente selezione del testo ne' menu di sistema tenendo premuto (su iPhone uscivano evidenziazione e lente). */
html,body,button,.tab,.dkey,.small-action,.setting-row,.now-card,.provider-row{-webkit-user-select:none;user-select:none;-webkit-touch-callout:none}
input,textarea,select{-webkit-user-select:text;user-select:text}
button img,.tab img,.dkey img,.small-action img{-webkit-user-drag:none;pointer-events:none}
/* Tasti: il tocco si vede (anche dove il telefono non vibra) e sfuma al rilascio. */
button,.tab,.dkey,.small-action{transition:filter .28s ease,transform .16s ease,background-color .28s ease,border-color .28s ease;touch-action:manipulation}
button.pressed,.tab.pressed,.dkey.pressed,.small-action.pressed{filter:brightness(1.38);transition:none}
/* Tempi: cifre a larghezza fissa e mai compresse, cosi' "1:23:45" non finisce sopra la linea. */
.now-time-row span{font-variant-numeric:tabular-nums;white-space:nowrap;width:auto!important;min-width:max-content;flex:none}
#currentTime{cursor:pointer;padding:9px 0;text-decoration:underline;text-decoration-color:color-mix(in srgb,var(--accent) 70%,transparent);text-underline-offset:4px}
.time-sheet{position:fixed;left:0;right:0;bottom:0;z-index:80;transform:translateY(110%);transition:transform .24s ease;background:var(--surface);border-top:1px solid var(--line);padding:20px 20px calc(20px + env(safe-area-inset-bottom));max-width:640px;margin:0 auto}
.time-sheet.open{transform:none}
.time-sheet h3{margin:0 0 4px;font-size:17px;font-weight:600;text-align:left}
.time-sheet p{margin:0 0 14px;color:var(--muted);font-size:13px;text-align:left}
.time-sheet input{width:100%;font-size:22px;padding:12px 14px;border-radius:10px;border:1px solid var(--line);background:var(--bg);color:var(--text);font-variant-numeric:tabular-nums;outline:none}
.time-sheet input:focus{border-color:var(--accent)}
.time-sheet .row{display:flex;justify-content:flex-end;gap:10px;margin-top:14px}
.time-sheet .row button{padding:11px 20px;border-radius:10px;font-size:15px}

/* 2026-10-27 rifinitura: niente trattini e riquadri inutili, controlli tutti uguali, fogli in vetro. */
.page-head{border-bottom:0!important}
.src{border-bottom:0!important;padding:6px 2px!important;font-weight:500!important;font-size:14px!important;color:var(--muted)!important}
.src.active{color:var(--text)!important;font-weight:700!important}
.library-tabs{display:flex!important;gap:24px!important;justify-content:flex-start;background:transparent!important;border:0!important;padding:0!important;margin:4px 2px 12px!important}
.library-tab{height:32px!important;padding:0!important;border:0!important;background:transparent!important;font-size:15px!important;font-weight:500!important;color:var(--muted)!important}
.library-tab.active{background:transparent!important;color:var(--text)!important;font-weight:700!important}
.setting-row,.color-row,.opt-link{min-height:62px!important;align-items:center!important}
.setting-row span,.opt-link span:first-child,.color-row>div:first-child span{font-size:14px!important;font-weight:500!important}
.setting-row small,.color-row>div:first-child small{font-size:11px!important;line-height:1.35}
.setting-value,.renderer-select,.sync-reset{height:38px!important;min-width:118px;max-width:170px;padding:0 14px!important;border:0!important;background:var(--surface2)!important;color:var(--text)!important;font-size:13px!important;font-weight:500;display:inline-flex;align-items:center;justify-content:center;text-align:center;text-align-last:center;line-height:38px}
select.renderer-select{appearance:none;-webkit-appearance:none;background-image:none!important}
.opt-link{grid-template-columns:minmax(0,1fr) auto!important;padding:0!important}
.opt-link .chev{display:none!important}
.opt-link small{height:38px;min-width:118px;padding:0 14px;border-radius:var(--btn-radius);background:var(--surface2);color:var(--text)!important;font-size:13px!important;display:inline-flex!important;align-items:center;justify-content:center}
.swatch.free{background:conic-gradient(#ff4848,#ffc428,#46d25a,#28aaff,#965aff,#ff48a0,#ff4848)!important}
.swatch.free:before{content:"";position:absolute;inset:5px;border-radius:50%;background:var(--bg);pointer-events:none}
.swatch.free:after{content:"";position:absolute;inset:0;pointer-events:none;background:linear-gradient(var(--text),var(--text)) center/8px 1.7px no-repeat,linear-gradient(var(--text),var(--text)) center/1.7px 8px no-repeat}
.reset-row .setting-value{min-width:118px}
.keyboard-sheet,.track-sheet{left:0!important;right:0!important;margin:0 auto;width:min(440px,100%)!important;bottom:calc(76px + env(safe-area-inset-bottom))!important;transform:translateY(115%)!important;border:0!important;border-radius:22px 22px 0 0!important;padding:18px 18px 16px!important;background:color-mix(in srgb,var(--surface) 90%,transparent)!important;-webkit-backdrop-filter:blur(26px) saturate(1.4);backdrop-filter:blur(26px) saturate(1.4);box-shadow:0 -12px 40px rgba(0,0,0,.42)!important}
.keyboard-sheet.open,.track-sheet.open{transform:none!important}
.keyboard-top{margin-bottom:12px!important}.keyboard-top strong{font-size:17px!important;font-weight:650!important}
#trackClose,#keyboardClose{width:36px!important;height:36px!important;border:0!important;background:transparent!important;color:var(--muted)!important;font-size:24px!important;line-height:1}
.keyboard-input{width:100%;height:54px!important;border:0!important;border-radius:14px!important;background:var(--surface2)!important;color:var(--text)!important;font-size:17px!important;padding:0 16px!important;outline:none}
.keyboard-actions{grid-template-columns:1fr 1fr 1.3fr!important;gap:10px!important;margin-top:12px!important}
.keyboard-actions button{height:50px!important;border:0!important;background:var(--surface2)!important;color:var(--text)!important;font-size:15px!important;font-weight:600}
.keyboard-actions .send{background:var(--accent)!important;color:#fff!important}
.track-list{border-top:0!important;max-height:min(52vh,420px)!important}
.track-option{width:100%;min-height:56px!important;padding:0 4px!important;border:0!important;border-bottom:1px solid var(--line)!important;background:transparent!important;display:flex!important;align-items:center;justify-content:space-between;color:var(--text)!important;font-size:15px!important;text-align:left}
.track-option:last-child{border-bottom:0!important}
.track-option.selected{color:var(--accent)!important;font-weight:650}
.track-mark{width:20px!important;height:20px!important;border:0!important;border-radius:0!important;background:none!important;box-shadow:none!important}
.track-mark:before,.track-mark:after{display:none!important}
.track-option.selected .track-mark{background:var(--accent)!important;-webkit-mask:url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M5 12.5l4.5 4.5L19 7.5' fill='none' stroke='black' stroke-width='2.4' stroke-linecap='round' stroke-linejoin='round'/></svg>") center/20px no-repeat;mask:url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M5 12.5l4.5 4.5L19 7.5' fill='none' stroke='black' stroke-width='2.4' stroke-linecap='round' stroke-linejoin='round'/></svg>") center/20px no-repeat}
.track-extra{gap:10px!important;padding-top:14px!important}
.track-extra button{height:48px!important;border-radius:14px!important;background:var(--surface2)!important;font-size:14px!important}
.opt-group{background:transparent!important;border-radius:0!important}
.opt-row{padding:0 2px!important;min-height:58px!important;font-size:14px!important}
.opt-sec{margin:26px 2px 4px!important;font-size:12px!important;letter-spacing:.02em!important;text-transform:none!important;font-weight:600!important;color:var(--accent)!important}
.opt-head strong{font-size:17px!important}
.film-actions button{background:var(--surface2)!important}
.now-copy .title,.now-copy .duration,.now-time-row{color:#fff!important}
.row-icon:has(img[src^="/api/art"]){overflow:hidden;background:var(--surface2)}.row-icon img[src^="/api/art"]{width:100%;height:100%;object-fit:cover;filter:none!important;opacity:1;border-radius:inherit}.now-art:not([src]),.now-art[src=""]{visibility:hidden}body.light .now-card .now-actions img{filter:brightness(0) invert(1)!important;opacity:.92}body.light .now-card .now-actions .icon-btn{background:rgba(5,10,15,.42)!important;border-color:rgba(255,255,255,.16)!important}body.light .now-card:not(:has(.now-art[src*="api/art"])){background:var(--surface)!important;border-color:var(--line)!important;box-shadow:0 10px 28px rgba(20,30,45,.10)}body.light .now-card:not(:has(.now-art[src*="api/art"])):after{display:none}body.light .now-card:not(:has(.now-art[src*="api/art"])) .title{color:var(--text);text-shadow:none}body.light .now-card:not(:has(.now-art[src*="api/art"])) .duration{color:var(--muted)}body.light .now-card:not(:has(.now-art[src*="api/art"])) .now-actions img{filter:invert(1) brightness(.35)!important}body.light .now-card:not(:has(.now-art[src*="api/art"])) .now-actions .icon-btn{background:var(--surface2)!important;border-color:var(--line)!important}body.light .now-card:not(:has(.now-art[src*="api/art"])) input[type=range]::-webkit-slider-runnable-track{background:linear-gradient(90deg,var(--accent) 0 var(--progress),rgba(20,30,45,.16) var(--progress) 100%)}body.light input[type=range]::-webkit-slider-runnable-track{background:linear-gradient(90deg,var(--accent) 0 var(--progress),rgba(20,30,45,.16) var(--progress) 100%)}body.light .now-card:has(.now-art[src*="api/art"]) input[type=range]::-webkit-slider-runnable-track{background:linear-gradient(90deg,var(--accent) 0 var(--progress),rgba(255,255,255,.24) var(--progress) 100%)}body.light #remotePage .quick .circle{background:transparent!important;border:0!important}body.light #remotePage .quick{background:var(--surface)!important;box-shadow:0 6px 18px rgba(20,30,45,.07)}body.light .mini-art,body.light .mini-art.fallback-art,body.light .now-art{background:var(--surface2)!important}body.light .fallback-art img,body.light .fallback-image{filter:invert(1) brightness(.35)!important;opacity:.5}body.light .mini-art.fallback-image{object-fit:none;object-position:center;background:var(--surface2)!important}body.light .now-card:not(:has(.now-art[src*="api/art"])) .now-copy .title,body.light .now-card:not(:has(.now-art[src*="api/art"])) .now-time-row{color:var(--text)!important;text-shadow:none!important}body.light .now-card:not(:has(.now-art[src*="api/art"])) .now-copy .duration{color:var(--muted)!important}body.light .now-card:not(:has(.now-art[src*="api/art"])) .now-progress{background:rgba(20,30,45,.14)!important}body.light .now-card:not(:has(.now-art[src*="api/art"])) .now-progress i:after{background:var(--accent)!important;box-shadow:0 1px 4px rgba(20,30,45,.25)}body.light .toggle{background:#cdd5de!important}body.light .toggle:after{background:#fff!important}body.light .toggle.on{background:var(--accent)!important}
/* ===== Revisione 2026-10-31 (2): pulito, senza riquadri ne' blu. Barra in basso a capsula, capitoli in prima pagina. ===== */
:root{--accent:#f5f5f7!important;--accent-ink:#0b0b0c;--hair:rgba(255,255,255,.08);--soft:rgba(255,255,255,.62)}
body.light{--accent:#1d1d1f!important;--accent-ink:#ffffff;--hair:rgba(0,0,0,.09);--soft:rgba(0,0,0,.55)}
/* --- barra in basso: capsula di vetro sospesa, la voce aperta su una pillola chiara (come i controlli segmentati di iOS) --- */
nav.tabs{left:50%!important;right:auto!important;transform:translateX(-50%)!important;bottom:calc(14px + env(safe-area-inset-bottom))!important;top:auto!important;width:auto!important;max-width:calc(100% - 24px)!important;height:48px!important;padding:4px!important;display:flex!important;gap:2px!important;align-items:center!important;border-radius:999px!important;border:0!important;background:color-mix(in srgb,var(--bg) 62%,rgba(40,40,46,.55))!important;backdrop-filter:blur(24px) saturate(1.4)!important;-webkit-backdrop-filter:blur(24px) saturate(1.4)!important;box-shadow:0 10px 30px rgba(0,0,0,.45),inset 0 0 0 .5px rgba(255,255,255,.12)!important}
nav.tabs .tab{flex:none!important;display:flex!important;align-items:center!important;height:40px!important;padding:0 11px!important;margin:0!important;border:0!important;border-radius:999px!important;background:none!important;color:var(--soft)!important;font-size:13px!important;font-weight:600!important;letter-spacing:0!important;box-shadow:none!important;opacity:1!important;transition:background .2s,color .2s}
nav.tabs .tab img{display:none!important}
nav.tabs .tab span{display:inline!important;font:inherit!important;color:inherit!important;margin:0!important}
nav.tabs .tab.active{background:var(--accent)!important;color:var(--accent-ink)!important}
nav.tabs .tab::before,nav.tabs .tab::after{display:none!important}
.src-pick{position:relative;flex:none;height:40px;padding:0 10px 0 9px;display:flex;align-items:center;gap:5px;color:var(--soft);font-size:13.5px;font-weight:600;border-left:1px solid var(--hair);margin-left:2px}
.src-pick.busy{opacity:.5}
.src-pick::after{content:"";width:5px;height:5px;border-right:1.5px solid currentColor;border-bottom:1.5px solid currentColor;transform:translateY(-2px) rotate(45deg);margin-left:2px}
.src-pick select{position:absolute;inset:0;opacity:0;font-size:16px;width:100%}
.src-switch{display:none!important}
main.app{padding-bottom:calc(90px + env(safe-area-inset-bottom))!important}
.opt-sheet,.media-sheet{bottom:0!important;z-index:40!important}
.track-sheet,.keyboard-sheet{bottom:0!important;z-index:41!important}
/* --- niente blu --- */
.play-main{background:var(--accent)!important;box-shadow:none!important}
.play-main img{filter:brightness(0)!important}
body.light .play-main img{filter:brightness(0) invert(1)!important}
.pref-row:has(#prefAccent){display:none!important}
.toggle{background:color-mix(in srgb,var(--text) 18%,transparent)!important;border:0!important}
.toggle.on,.toggle[aria-pressed=true]{background:#34c759!important}
/* --- ora in riproduzione: barra sottile che si ingrossa al tocco, niente riga sotto il minutaggio --- */
#currentTime,#totalTime{text-decoration:none!important;border:0!important;box-shadow:none!important;background:none!important}
.now-progress{height:3px!important;border-radius:3px!important;background:rgba(255,255,255,.24)!important;overflow:visible!important;position:relative!important;transition:height .16s ease}
.now-progress::before{content:"";position:absolute;left:0;right:0;top:-14px;bottom:-14px}
.now-progress i{background:#fff!important;border-radius:3px!important;height:100%!important;box-shadow:none!important}
.now-progress i::after{opacity:0!important;transition:opacity .16s;background:#fff!important}
.now-progress.active{height:6px!important}
.now-progress.active i::after{opacity:1!important}
input[type=range]{height:22px!important;background:transparent!important;--fill:0%}
input[type=range]::-webkit-slider-runnable-track{height:3px!important;border-radius:3px!important;background:linear-gradient(90deg,var(--text) var(--fill),color-mix(in srgb,var(--text) 22%,transparent) var(--fill))!important}
input[type=range]::-webkit-slider-thumb{-webkit-appearance:none!important;width:0!important;height:0!important;margin-top:0!important;box-shadow:none!important;border:0!important;background:var(--text)!important}
input[type=range].active::-webkit-slider-runnable-track{height:6px!important}
input[type=range].active::-webkit-slider-thumb{width:16px!important;height:16px!important;margin-top:-5px!important}
/* --- capitoli in prima pagina: una riga che scorre, solo testo; quello in corso pieno con una lineetta sotto --- */
.chapters-strip{margin:14px -16px 4px;padding:0 16px;display:flex;gap:22px;overflow-x:auto;scrollbar-width:none;scroll-snap-type:x proximity}
.chapters-strip::-webkit-scrollbar{display:none}
.chapters-strip:empty{display:none}
.chap{flex:none;scroll-snap-align:start;border:0;background:none;padding:6px 0 8px;text-align:left;color:var(--soft);font:inherit;max-width:180px;position:relative}
.chap small{display:block;font-size:11.5px;font-variant-numeric:tabular-nums;color:color-mix(in srgb,var(--text) 45%,transparent);margin-bottom:2px}
.chap span{display:block;font-size:14px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.chap.on{color:var(--text)}
.chap.on::after{content:"";position:absolute;left:0;bottom:0;width:18px;height:2px;border-radius:2px;background:var(--text)}
.chapters-title{font-size:12.5px;font-weight:600;color:color-mix(in srgb,var(--text) 50%,transparent);margin:18px 0 0;letter-spacing:.02em}
/* --- pagine: titolo grande, elenchi piatti con righe sottili, niente riquadri --- */
.page-head h1{font-size:32px!important;font-weight:700!important;letter-spacing:-.02em!important;margin:8px 0 4px!important}
.page-head p,.page-head small{color:var(--soft)!important;font-size:14px!important}
.setting-section,.native-section{color:color-mix(in srgb,var(--text) 50%,transparent)!important;font-size:12.5px!important;font-weight:600!important;text-transform:none!important;letter-spacing:.02em!important;margin:30px 0 4px!important;padding:0!important;border:0!important;background:none!important}
.setting-row,.color-row,.opt-link{background:none!important;border:0!important;border-radius:0!important;margin:0!important;padding:14px 0!important;box-shadow:inset 0 -1px 0 var(--hair)!important}
.setting-row small,.color-row small{color:var(--soft)!important;font-size:12.5px!important}
.setting-value,.renderer-select,.sync-reset,.small-action{background:none!important;border:0!important;box-shadow:none!important;color:var(--soft)!important;font-size:15px!important;padding:4px 0!important;min-height:0!important;text-align:right!important}
.provider-head,.provider-tabs,#providerBody>*{background:none!important;border-color:var(--hair)!important;box-shadow:none!important}
.sync-row{display:none!important}
/* --- libreria: "Libreria: Film ⌄" e la ricerca come nel player --- */
.library-tabs{display:none!important}
.lib-head{display:flex;align-items:baseline;gap:8px;margin:8px 0 14px}
.lib-head b{font-size:32px;font-weight:700;letter-spacing:-.02em}
.lib-pick{position:relative;display:flex;align-items:center;gap:7px;font-size:32px;font-weight:700;letter-spacing:-.02em;color:color-mix(in srgb,var(--text) 55%,transparent)}
.lib-pick::after{content:"";width:9px;height:9px;border-right:2.4px solid currentColor;border-bottom:2.4px solid currentColor;transform:translateY(-5px) rotate(45deg);margin-left:4px}
.lib-pick select{position:absolute;inset:0;opacity:0;font-size:16px}
#libraryPage .page-head{display:none!important}
.library-search{margin:0 0 18px!important;position:relative}
.library-search input,#librarySearch{width:100%;height:42px!important;border:0!important;border-radius:999px!important;background:color-mix(in srgb,var(--text) 9%,transparent)!important;box-shadow:inset 0 0 0 .5px rgba(255,255,255,.1)!important;padding:0 16px 0 40px!important;font-size:15px!important;color:var(--text)!important}
.library-search::before{content:"";position:absolute;left:15px;top:13px;width:11px;height:11px;border:1.8px solid var(--soft);border-radius:50%;pointer-events:none}
.library-search::after{content:"";position:absolute;left:27px;top:26px;width:6px;height:1.8px;background:var(--soft);transform:rotate(45deg);transform-origin:left;pointer-events:none}
.category-row{border:0!important;box-shadow:none!important;padding:6px 0!important}
.mini-card,.media-row{border:0!important;box-shadow:none!important;background:none!important}
.list>*{border-top:0!important;border-bottom:0!important}
/* --- Extra: tutto in chiaro, niente gruppi a riquadro --- */
.film-head{display:flex!important;gap:14px!important;align-items:center!important;padding:6px 0 0!important;margin:0!important;background:none!important;border:0!important}
.film-head img,.film-head>span{width:56px!important;height:84px!important;border-radius:6px!important;object-fit:cover!important;flex:none!important;box-shadow:0 6px 18px rgba(0,0,0,.35)!important}
.film-head strong{display:block;font-size:19px!important;font-weight:700!important;line-height:1.2!important}
.film-head small{display:block;margin-top:4px;font-size:13px!important;color:var(--soft)!important}
#optBody .opt-sec{color:color-mix(in srgb,var(--text) 50%,transparent)!important;font-size:12.5px!important;font-weight:600!important;letter-spacing:.02em!important;text-transform:none!important;margin:30px 0 8px!important}
.film-overview{font-size:15px!important;line-height:1.55!important;color:color-mix(in srgb,var(--text) 86%,transparent)!important;margin:0!important}
.cast-strip{gap:16px!important}
.cast-card{width:68px!important;flex:none!important;background:none!important;border:0!important;padding:0!important;text-align:center}
.cast-card img,.cast-card .ph{width:68px!important;height:68px!important;border-radius:50%!important;object-fit:cover!important;aspect-ratio:1/1!important}
.cast-card b{display:block;margin-top:8px;font-size:12px!important;font-weight:600!important;line-height:1.25!important;white-space:normal!important}
.cast-card span{display:block;font-size:11px!important;color:var(--soft)!important;margin-top:2px}
.kv{background:none}
.kv>div{display:flex;justify-content:space-between;gap:16px;padding:11px 0;font-size:15px;box-shadow:inset 0 -1px 0 var(--hair)}
.kv>div:last-child{box-shadow:none}
.kv>div span{color:var(--soft)}
.kv>div b{font-weight:500;text-align:right}
.kv.plain>div{display:block;color:color-mix(in srgb,var(--text) 88%,transparent);padding:4px 0;box-shadow:none}
#optBody .opt-group{background:none!important;border:0!important;border-radius:0!important;box-shadow:none!important}
#optBody .opt-group>*+*{border-top:0!important;box-shadow:inset 0 1px 0 var(--hair)!important}
/* --- sincronia audio: il valore grande al centro, sotto i passi come parole --- */
.sync4{text-align:center;padding:6px 0 0}
.sync4 .val{display:flex;justify-content:center;align-items:baseline;gap:6px}
.sync4 input{width:96px;background:none;border:0;color:var(--text);font:inherit;font-size:44px;font-weight:700;letter-spacing:-.03em;text-align:center;padding:0;outline:none;-moz-appearance:textfield}
.sync4 input::-webkit-outer-spin-button,.sync4 input::-webkit-inner-spin-button{-webkit-appearance:none;margin:0}
.sync4 .val span{color:var(--soft);font-size:16px;font-weight:600}
.sync4 .steps{display:flex;justify-content:center;gap:26px;margin:10px 0 0}
.sync4 .steps button,.sync4 .links button{background:none;border:0;color:var(--soft);font:inherit;font-size:15px;font-weight:600;padding:8px 2px;font-variant-numeric:tabular-nums}
.sync4 .steps button:active,.sync4 .links button:active{color:var(--text)}
.sync4 .steps button:disabled,.sync4 .links button:disabled{opacity:.35}
.sync4 .links{display:flex;justify-content:center;gap:28px;margin-top:4px}
.sync4 .links button{color:var(--text)}
.sync-note{color:var(--soft);font-size:12.5px;margin:12px 0 0;text-align:center}
/* --- Scrivi su Cinecore: una riga e tre parole --- */
.keyboard-input{background:none!important;border:0!important;border-bottom:1.5px solid color-mix(in srgb,var(--text) 26%,transparent)!important;border-radius:0!important;padding:10px 0!important;height:auto!important;font-size:18px!important;box-shadow:none!important}
.keyboard-input:focus{border-bottom-color:var(--text)!important;outline:none!important}
.keyboard-actions{display:flex!important;gap:24px!important;align-items:center!important;margin-top:18px!important}
.keyboard-actions button{background:none!important;border:0!important;box-shadow:none!important;padding:8px 0!important;height:auto!important;min-height:0!important;width:auto!important;flex:none!important;color:var(--soft)!important;font-size:15px!important;font-weight:600!important}
.keyboard-actions button.send{margin-left:auto!important;color:var(--accent-ink)!important;background:var(--accent)!important;padding:9px 22px!important;border-radius:999px!important}
#keyboardClose,#trackClose{background:none!important;border:0!important;box-shadow:none!important}
.track-list>*{background:none!important;border:0!important;box-shadow:inset 0 -1px 0 var(--hair)!important;border-radius:0!important}
.opt-link small,#syncLinkValue{background:none!important;border:0!important;box-shadow:none!important;color:var(--soft)!important;padding:0!important}
/* --- 2026-10-31: barra in vetro liquido, voce aperta solo piu' chiara, niente separatore del PC --- */
nav.tabs{background:rgba(28,30,36,.34)!important;backdrop-filter:blur(30px) saturate(1.9) brightness(1.06)!important;-webkit-backdrop-filter:blur(30px) saturate(1.9) brightness(1.06)!important;box-shadow:0 12px 34px rgba(0,0,0,.40),inset 0 .5px 0 rgba(255,255,255,.22),inset 0 0 0 .5px rgba(255,255,255,.10)!important}
body.light nav.tabs{background:rgba(255,255,255,.42)!important;box-shadow:0 10px 30px rgba(0,0,0,.14),inset 0 .5px 0 rgba(255,255,255,.8),inset 0 0 0 .5px rgba(0,0,0,.06)!important}
nav.tabs .tab{color:color-mix(in srgb,var(--text) 55%,transparent)!important}
nav.tabs .tab.active{background:none!important;color:var(--text)!important}
.src-pick{border-left:0!important;margin-left:0!important;color:color-mix(in srgb,var(--text) 55%,transparent)!important}
/* ricerca della libreria: lente disegnata come immagine, centrata con il testo */
.library-search::before,.library-search::after{display:none!important}
.library-search input,#librarySearch{line-height:42px!important;padding-top:0!important;padding-bottom:0!important;box-sizing:border-box!important;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='16' height='16' viewBox='0 0 16 16' fill='none' stroke='%238e8e93' stroke-width='1.8' stroke-linecap='round'%3E%3Ccircle cx='6.8' cy='6.8' r='5'/%3E%3Cpath d='M10.6 10.6 14.5 14.5'/%3E%3C/svg%3E")!important;background-repeat:no-repeat!important;background-position:16px 50%!important;background-size:16px 16px!important}
/* coda e liste: niente frecce a destra, e l'ultima riga si ferma sopra la barra */
.media-row .chev{display:none!important}
.page{padding-bottom:calc(104px + env(safe-area-inset-bottom))!important}
#queueList,#libraryList{padding-bottom:calc(30px + env(safe-area-inset-bottom))}
/* --- 2026-10-31 (5): la pagina Telecomando sta tutta nello schermo, niente scorrimento --- */
body:has(#remotePage.active) main.app{padding-bottom:0!important;min-height:0!important;height:100dvh!important;overflow:hidden!important}
#remotePage.active{display:flex!important;flex-direction:column!important;height:calc(100dvh - 86px - env(safe-area-inset-bottom))!important;padding-bottom:0!important;overflow:hidden!important;gap:0!important}
#remotePage>*{flex:none!important;margin-top:0!important;margin-bottom:0!important}
#remotePage .remote-top{height:46px!important;min-height:0!important}
#remotePage .now-card{height:clamp(150px,25dvh,240px)!important;min-height:0!important}
#remotePage .chapters-title{display:none!important}
#remotePage .chapters-strip{margin:10px -16px 0!important}
#remotePage .dpad-wrap{flex:1 1 0!important;min-height:120px!important;max-height:240px;display:grid!important;place-items:center!important;margin:6px 0!important}
#remotePage .dpad-wrap .nav-pad{height:min(100%,210px)!important;width:min(100%,260px)!important}
#remotePage .transport{margin:0!important}
/* tasti rapidi: una sola riga di cinque, senza riquadro */
#remotePage .quick{grid-template-columns:repeat(5,1fr)!important;background:none!important;border:0!important;box-shadow:none!important;border-radius:0!important;padding:0!important;margin:8px 0 2px!important;gap:0!important;width:100%!important}
#remotePage .quick .circle{height:48px!important;min-height:0!important;width:auto!important;background:none!important;border:0!important;box-shadow:none!important;display:grid!important;place-items:center!important}
#remotePage .quick .circle span{display:none!important}
#remotePage .sliders{margin:2px 0 6px!important}
#remotePage .dpad-wrap{justify-items:center!important;width:100%!important}
#remotePage .dpad-wrap .nav-pad{margin:0 auto!important;justify-self:center!important}
#remotePage .chapters-strip{margin:10px -20px 0!important;padding:0 20px!important}
#remotePage .dpad-wrap .nav-pad{width:min(250px,64vw)!important;height:min(100%,200px)!important;grid-template-columns:repeat(3,1fr)!important;grid-template-rows:repeat(3,1fr)!important;transform:none!important;padding:0!important}
#remotePage .nav-pad{position:relative!important}
#remotePage .nav-pad .dkey{position:absolute!important;margin:0!important;right:auto!important;bottom:auto!important}
#remotePage .nav-pad .dkey.up{left:50%!important;top:0!important;transform:translateX(-50%)!important}
#remotePage .nav-pad .dkey.down{left:50%!important;top:auto!important;bottom:0!important;transform:translateX(-50%)!important}
#remotePage .nav-pad .dkey.left{left:0!important;top:50%!important;transform:translateY(-50%)!important}
#remotePage .nav-pad .dkey.right{left:auto!important;right:0!important;top:50%!important;transform:translateY(-50%)!important}
#remotePage .nav-pad .dkey.ok{left:50%!important;top:50%!important;transform:translate(-50%,-50%)!important}
/* --- Extra a schede --- */
#optBody .xtabs{display:flex;gap:26px;margin:0 0 20px;border-bottom:.5px solid var(--hair);position:sticky;top:0;background:var(--bg);z-index:2}
#optBody .xtab{background:none;border:0;padding:12px 0 11px;margin:0;color:color-mix(in srgb,var(--text) 50%,transparent);font-size:16px;font-weight:600;line-height:1;position:relative;border-radius:0;box-shadow:none;min-height:0;height:auto;width:auto}
#optBody .xtab.on{color:var(--text)}
#optBody .xtab.on::after{content:"";position:absolute;left:0;right:0;bottom:-1px;height:2px;border-radius:2px;background:var(--text)}
#optBody .xhead{display:flex;gap:16px;align-items:flex-end;margin-bottom:18px}
#optBody .xhead img{width:88px;height:132px;object-fit:cover;border-radius:10px;flex:none}
#optBody .xhead strong{display:block;font-size:23px;line-height:1.15;font-weight:700;color:var(--text)}
#optBody .xhead small{display:block;margin-top:6px;font-size:14px;color:color-mix(in srgb,var(--text) 58%,transparent)}
#optBody .xover{margin:0 0 22px;font-size:15.5px;line-height:1.5;color:color-mix(in srgb,var(--text) 86%,transparent);display:-webkit-box;-webkit-line-clamp:8;-webkit-box-orient:vertical;overflow:hidden}
#optBody .xsec{margin:0 0 10px;font-size:13px;font-weight:600;color:color-mix(in srgb,var(--text) 50%,transparent)}
#optBody .xcast{display:flex;gap:14px;overflow-x:auto;scrollbar-width:none;margin:0 -20px;padding:0 20px 6px}
#optBody .xcast>div{flex:none;width:74px;text-align:center}
#optBody .xcast img,#optBody .xcast i{display:grid;place-items:center;width:64px;height:64px;margin:0 auto 7px;border-radius:50%;object-fit:cover;background:color-mix(in srgb,var(--text) 10%,transparent);font-style:normal;font-weight:600;color:var(--text)}
#optBody .xcast b,#optBody .xcast span{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
#optBody .xcast b{font-size:12.5px;font-weight:600;color:var(--text)}
#optBody .xcast span{font-size:11.5px;margin-top:2px;color:color-mix(in srgb,var(--text) 55%,transparent)}
#optBody .xrow{display:grid;grid-template-columns:auto 1fr;gap:16px;align-items:baseline;padding:13px 0;border-bottom:.5px solid var(--hair);font-size:15px}
#optBody .xrow:last-child{border-bottom:0}
#optBody .xrow span{color:color-mix(in srgb,var(--text) 55%,transparent);white-space:nowrap}
#optBody .xrow b{font-weight:500;color:var(--text);text-align:right;overflow-wrap:anywhere}
#optBody .xsync{margin-top:4px}
#optBody .xdial{display:grid;grid-template-columns:46px 50px 1fr 50px 46px;align-items:center}
#optBody .xstep{background:none;border:0;box-shadow:none;height:52px;padding:0;margin:0;color:var(--text);font-size:17px;font-weight:600;border-radius:0;min-height:0;width:auto}
#optBody .xstep.far{color:color-mix(in srgb,var(--text) 50%,transparent);font-size:15px}
#optBody .xstep:disabled{opacity:.3}
#optBody .xval{display:flex;align-items:baseline;justify-content:center;gap:4px}
#optBody .xval input{width:3.4em;min-width:0;background:none!important;border:0!important;box-shadow:none!important;padding:0!important;text-align:right;font-size:42px;font-weight:700;color:var(--text);-moz-appearance:textfield;font-variant-numeric:tabular-nums;height:auto!important}
#optBody .xval input::-webkit-inner-spin-button,#optBody .xval input::-webkit-outer-spin-button{-webkit-appearance:none;margin:0}
#optBody .xval small{font-size:15px;color:color-mix(in srgb,var(--text) 50%,transparent)}
#optBody .xacts{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:22px}
#optBody .xacts button{height:46px;border:0;border-radius:12px;background:color-mix(in srgb,var(--text) 9%,transparent);color:var(--text);font-size:15px;font-weight:600;box-shadow:none;margin:0}
#optBody .xacts button:disabled{opacity:.35}
#optBody .xnote{margin:16px 0 0;font-size:13px;line-height:1.45;text-align:center;color:color-mix(in srgb,var(--text) 50%,transparent)}
#optBody .xnote.strong{color:var(--text)}
#optBody .xval input{text-align:center!important;width:2.4em!important}
/* --- Impostazioni a schede --- */
#settingsPage .page-head p{display:none!important}
#settingsPage .set-tabs{display:flex;gap:4px;padding:4px;margin:6px 0 16px;border-radius:999px;background:color-mix(in srgb,var(--text) 8%,transparent);position:sticky;top:6px;z-index:5;backdrop-filter:blur(18px);-webkit-backdrop-filter:blur(18px)}
#settingsPage .set-tab{flex:1;background:none;border:0;padding:10px 0;margin:0;color:color-mix(in srgb,var(--text) 55%,transparent);font-size:15px;font-weight:600;line-height:1;border-radius:999px;box-shadow:none;min-height:0;height:auto;width:auto;transition:background .25s ease,color .25s ease}
#settingsPage .set-tab.on{color:var(--text);background:color-mix(in srgb,var(--text) 16%,transparent)}
#settingsPage .set-tab.on::after{content:none}
#settingsPage[data-tab="play"]>[data-grp]:not([data-grp="play"]),
#settingsPage[data-tab="pref"]>[data-grp]:not([data-grp="pref"]),
#settingsPage[data-tab="remote"]>[data-grp]:not([data-grp="remote"]){display:none!important}
#settingsPage .tab-dup,#settingsPage #componentsTitle.empty{display:none!important}
/* valori a destra, con la freccina della scelta */
#settingsPage .setting-row select,#settingsPage .renderer-select{justify-self:end!important;text-align:right!important;text-align-last:right!important;padding:0 18px 0 0!important;background:transparent url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='10' height='6' viewBox='0 0 10 6' fill='none' stroke='%238e8e93' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'%3E%3Cpath d='M1 1l4 4 4-4'/%3E%3C/svg%3E") no-repeat right center/10px 6px!important;border:0!important;box-shadow:none!important;color:color-mix(in srgb,var(--text) 62%,transparent)!important;font-size:15px!important;-webkit-appearance:none!important;appearance:none!important;max-width:58vw}
#settingsPage .opt-link{text-align:left}
#optBody .xval input{field-sizing:content!important;width:auto!important;min-width:1ch!important;max-width:4.2em!important;text-align:center!important}
#settingsPage .setting-row>button:not(.toggle),#settingsPage .setting-row>a,#settingsPage .setting-row>.value,#settingsPage .opt-link>:last-child{justify-self:end!important;text-align:right!important;background:none!important;border:0!important;box-shadow:none!important;padding-right:0!important;color:color-mix(in srgb,var(--text) 62%,transparent)!important;font-size:15px!important;font-weight:500!important;min-width:0!important;width:auto!important}
#settingsPage #syncLink{display:grid!important;grid-template-columns:1fr auto!important;align-items:center}
#settingsPage #syncLink small{justify-self:end!important;text-align:right!important}
#settingsPage #syncLink{grid-template-columns:1fr auto auto!important;gap:8px}
#settingsPage #syncLink .chev{color:color-mix(in srgb,var(--text) 40%,transparent)}
#settingsPage .setting-row>.setting-value{font-weight:500!important;color:color-mix(in srgb,var(--text) 62%,transparent)!important}
#settingsPage #syncLink{display:flex!important;align-items:center!important;width:100%!important}
#settingsPage #syncLink small{margin-left:auto!important;justify-self:auto!important}
#settingsPage #syncLink .chev{display:inline!important;margin-left:8px}
#settingsPage .extra-row button{font-weight:500!important;color:color-mix(in srgb,var(--text) 62%,transparent)!important;background:none!important;border:0!important;padding-right:0!important}
</style>
<style>
body[data-platform="mac"] main.app,body[data-platform="mac"] #optSheet{max-width:none;width:100%}
body[data-platform="mac"] main.app{--page-gutter:20px;padding-inline:var(--page-gutter)}
body[data-platform="mac"] #remotePage .now-card{margin-inline:calc(-1 * var(--page-gutter))!important}
body[data-platform="mac"] #remotePage .now-art{display:block;inset:0;width:100%;height:100%;object-fit:cover;opacity:1}
body[data-platform="mac"] #remotePage .now-card:after{background:linear-gradient(0deg,rgba(4,8,12,.9),transparent 70%)}
body[data-platform="mac"] #remotePage .now-card.no-playback:after{background:#0a1016}
body[data-platform="mac"] #optSheet{--sheet-gutter:16px;padding-inline:var(--sheet-gutter)}
body[data-platform="mac"] #optBody .xhead{display:block}
body[data-platform="mac"] #optBody .xhead img{display:block;width:calc(100% + 2 * var(--sheet-gutter));height:auto;aspect-ratio:16/9;margin:0 calc(-1 * var(--sheet-gutter)) 14px;border-radius:0;object-fit:cover}
body[data-platform="mac"] #settingsPage #languageValue{display:none}
body[data-platform="mac"] #settingsPage .setting-row[hidden]{display:none!important}
body[data-platform="mac"] .now-actions .icon-btn[aria-pressed="true"]{border-color:var(--accent)}
body[data-platform="mac"] #optBody .xtabs{overflow-x:auto;white-space:nowrap;scrollbar-width:none}
</style>
</head>
<body data-platform="{{{{{platform}}}}}">
<main class="app">
  <section class="page active" id="remotePage">
    <div class="remote-top"><span class="remote-brand">cinecore</span><button class="power-top" onclick="cmd('poweroff')" aria-label="Esci da Cinecore"><img src="{{{{{power}}}}}" alt=""></button></div>
    <header class="now-card" data-block="header">
      <img class="now-art" id="nowArt" alt="">
      <div class="now-actions"><button class="icon-btn" id="nowFullscreen" onclick="cmd('full')" aria-label="Schermo intero" aria-pressed="false"><img src="{{{{{fullscreen}}}}}" alt=""></button><button class="icon-btn stop" onclick="cmd('stop')" aria-label="Ferma riproduzione"><img src="{{{{{stop}}}}}" alt=""></button></div>
      <div class="now-copy"><div class="title" id="title" data-empty="Nessuna riproduzione"></div><div class="duration" id="nowMeta"></div><div class="now-time-row"><span id="currentTime">00:00</span><div class="now-progress" id="nowTimeline" tabindex="0" aria-label="Posizione di riproduzione" role="slider" aria-valuemin="0" aria-valuemax="0" aria-valuenow="0"><i id="nowProgress"></i></div><span id="totalTime">00:00</span></div></div>
    </header>
    <div class="quick" data-block="quick">

      <button class="circle" onclick="cmd('info')"><img src="{{{{{info}}}}}" alt=""><span>Info</span></button>
      <button class="circle" onclick="openTrackSheet('subtitle')"><img src="{{{{{cc}}}}}" alt=""><span>Sottotitoli</span></button>
      <button class="circle" onclick="openTrackSheet('audio')"><img src="{{{{{audio}}}}}" alt=""><span>Lingua audio</span></button>
      <button class="circle" id="keyboardToggle"><img src="{{{{{keyboard}}}}}" alt=""><span>Tastiera</span></button>
      <button class="circle" id="moreToggle" data-label="extra"><b class="dots">•••</b><span>Extra</span></button>
    </div>
    <div class="dpad-wrap" data-block="navigation"><div class="nav-pad">
      <button class="dkey up" onclick="cmd('up')"><img src="{{{{{chevron}}}}}" alt=""></button>
      <button class="dkey left" onclick="cmd('left')"><img src="{{{{{chevron}}}}}" alt=""></button>
      <button class="dkey ok" onclick="cmd('ok')">OK</button>
      <button class="dkey right" onclick="cmd('right')"><img src="{{{{{chevron}}}}}" alt=""></button>
      <button class="dkey down" onclick="cmd('down')"><img src="{{{{{chevron}}}}}" alt=""></button>
    </div></div>
    <div class="transport" data-block="transport"><button onclick="cmd('prev')" aria-label="Capitolo precedente"><img src="{{{{{prev}}}}}" alt=""></button><button id="btnBack10" aria-label="Indietro di 10 secondi"><img src="{{{{{skipBack}}}}}" alt=""></button><button class="play-main" onclick="cmd('toggle')" aria-label="Play o pausa"><img id="playIcon" src="{{{{{play}}}}}" data-play="{{{{{play}}}}}" data-pause="{{{{{pause}}}}}" alt=""></button><button id="btnFwd10" aria-label="Avanti di 10 secondi"><img src="{{{{{skipForward}}}}}" alt=""></button><button onclick="cmd('next')" aria-label="Capitolo successivo"><img src="{{{{{next}}}}}" alt=""></button></div>
    <div class="sliders" data-block="sliders">
      <div class="slider vol"><button onclick="cmd('mute')"><img src="{{{{{volume}}}}}" alt=""></button><input id="volume" type="range" min="0" max="1" value="1" step="0.01"><output id="volPct">100%</output></div>
    </div>
    <div class="more" id="more"><button class="small-action" onclick="cmd('info')">Info</button><button class="small-action" id="hdrBtn" onclick="cycleHdr()">HDR<small id="hdrQuickValue">Auto</small></button><button class="small-action" id="stereoBtn" onclick="cycle3d()">3D<small id="stereoQuickValue">Off</small></button><button class="small-action" id="upscaleBtn" onclick="toggleUpscaling()">Upscaling<small id="upscaleQuickValue">Off</small></button><button class="small-action" id="castBtn" onclick="cmd('cast')">Cast<small id="castNote">Interpreti</small></button><button class="small-action" id="subDlBtn" onclick="cmd('subtitle_download')">Sottotitoli<small id="subDlNote">Scarica</small></button><button class="small-action" id="subSyncBtn" onclick="cmd('subtitle_realign')">Sottotitoli<small id="subSyncNote">Riallinea</small></button><button class="small-action" id="chaptersBtn" onclick="openTrackSheet('chapter')">Capitoli<small id="chaptersNote">Elenco</small></button><button class="small-action" id="hdrAnalysisBtn" onclick="cmd('hdr_analysis')">Analisi HDR<small id="hdrAnalysisNote">Sul player</small></button><div class="audio-out-row"><span id="audioOutLabel">Uscita audio</span><select class="renderer-select" id="audioOutSelect" onchange="cmd('audio_device','&value='+encodeURIComponent(this.value))"></select></div><div class="sync-panel tr-row" data-it="Sincronia audio|+ ritarda l'audio" data-en="Audio sync|+ delays the audio"><div class="sync-head"><span>Sincronia audio</span><small></small></div><div class="sync-ctl"><button onclick="avDelay(-50)" aria-label="-50 ms">−50</button><button onclick="avDelay(-10)" aria-label="-10 ms">−10</button><output class="sync-value">0 ms</output><button onclick="avDelay(10)" aria-label="+10 ms">+10</button><button onclick="avDelay(50)" aria-label="+50 ms">+50</button></div><button class="sync-reset" onclick="avReset()">Azzera</button></div></div>
  </section>

  <section class="page" id="queuePage"><div class="page-head"><h1>Coda</h1></div><div class="list" id="queueList"></div></section>
  <section class="page" id="libraryPage"><div class="page-head" style="display:flex;align-items:center;justify-content:space-between;gap:12px"><h1>Libreria</h1><div class="src-switch"><button type="button" id="srcLocal" class="src active" onclick="setLibrarySource(false)">Computer</button><button type="button" id="srcNet" class="src" onclick="setLibrarySource(true)">Rete</button></div></div><div class="library-tabs"><button class="library-tab active" data-library-key="Movies">Film</button><button class="library-tab" data-library-key="TV Series">Serie TV</button><button class="library-tab" data-library-key="Music">Musica</button></div><div class="library-search"><input id="librarySearch" type="search" autocomplete="off" placeholder="Cerca nella libreria"></div><div class="list" id="libraryList"></div></section>
  <section class="page" id="settingsPage">
    <div class="page-head"><h1>Impostazioni</h1><p>Il player e questo telecomando.</p></div>
    <div class="setting-section">In riproduzione</div>
    <div class="setting-row"><div><span>Renderer corrente</span><small>Cambia il motore durante la sessione</small></div><select class="renderer-select" id="rendererCurrent" onchange="setRenderer(false,this.value)"><option>Auto</option><option>EVR</option><option>MPCVR</option><option>MADVR</option><option>MPV</option></select></div>
    <div class="setting-row"><div><span>HDR</span><small>RTX Video HDR, passthrough e tone-map reali</small></div><button class="setting-value" id="settingsHdr" onclick="cycleHdr()">Auto</button></div>
    <div class="setting-row"><div><span>Modalità 3D</span><small>Rilevamento automatico oppure layout forzato</small></div><select class="renderer-select" id="settings3d" onchange="setStereoMode(this.value)"><option value="off">Disattivata</option><option value="auto">Auto</option><option value="sbs">Side-by-Side</option><option value="tab">Top/Bottom</option></select></div>
    <div class="setting-row compact"><div><span>Upscaling</span><small>Attiva il motore e mostra le opzioni disponibili</small></div><button class="toggle" id="settingsUpscaling" onclick="toggleUpscaling()"></button></div>
    <div class="nested-settings" id="upscaleDetails"><div class="setting-row"><div><span>Motore</span><small>madVR oppure NVIDIA Super Resolution</small></div><select class="renderer-select" id="upscaleBackend" onchange="setUpscalingProfile()"><option value="off">Off</option><option value="madvr">madVR</option><option value="mpcvr">MPCVR NVIDIA</option></select></div><div class="setting-row"><div><span>Profilo</span><small id="upscaleHelp">Soglia o profilo del renderer</small></div><select class="renderer-select" id="upscalePreset" onchange="setUpscalingProfile()"></select></div></div>
    <div class="setting-row sync-row tr-row" data-it="Sincronia audio|+ ritarda l'audio, − lo anticipa. Resta salvata per questo film" data-en="Audio sync|+ delays the audio, − advances it. Saved for this film"><div><span>Sincronia audio</span><small></small></div><div class="sync-ctl"><button onclick="avDelay(-50)" aria-label="-50 ms">−50</button><button onclick="avDelay(-10)" aria-label="-10 ms">−10</button><output class="sync-value">0 ms</output><button onclick="avDelay(10)" aria-label="+10 ms">+10</button><button onclick="avDelay(50)" aria-label="+50 ms">+50</button></div><button class="sync-reset" onclick="avReset()">Azzera</button></div>
    <button class="opt-link" id="syncLink" onclick="optOpen('sync')"><span id="syncLinkLabel">Sincronia audio</span><small id="syncLinkValue"></small><span class="chev">&#8250;</span></button>
    <div class="setting-section">Schermo</div>
    <div class="setting-row pref-row" data-it="Avvia a schermo intero|Il player si apre senza finestra, dal prossimo avvio" data-en="Start full screen|The player opens without a window, from the next start"><div><span></span><small></small></div><button class="toggle" data-native="Player|start-fullscreen" onclick="setQuickNative(this)"></button></div>
    <div class="setting-row extra-row" data-it="Schermo intero esteso|Il film si allarga su tutti gli schermi insieme. Con un film 3D ogni schermo mostra un occhio (due proiettori)|Attiva / esci" data-en="Extended fullscreen|The film spans all screens together. With a 3D film each screen shows one eye (two projectors)|Toggle"><div><span>Schermo intero esteso</span><small>Il film si allarga su tutti gli schermi insieme. Con un film 3D ogni schermo mostra un occhio (due proiettori)</small></div><button class="setting-value" onclick="cmd('fullscreen_extended')">Attiva / esci</button></div>
    <div class="setting-row"><div><span>Spotlight</span><small>Interfaccia cinema sullo schermo principale</small></div><button class="toggle" id="settingsSpotlight" onclick="cmd('spotlight')"></button></div>
    <div class="setting-section">Preferenze</div>
    <div class="setting-row"><div><span>Uscita audio</span><small>Dispositivo del PC per l’audio</small></div><select class="renderer-select" id="audioOutSettings" onchange="cmd('audio_device','&value='+encodeURIComponent(this.value))"></select></div>
    <div class="setting-row"><div><span>Renderer predefinito</span><small>Usato per le prossime aperture</small></div><select class="renderer-select" id="rendererDefault" onchange="setRenderer(true,this.value)"><option>Auto</option><option>EVR</option><option>MPCVR</option><option>MADVR</option><option>MPV</option></select></div>
    <div class="setting-row pref-row" data-it="Qualità Jellyfin ed Emby|Con un limite il server converte il video al volo" data-en="Jellyfin and Emby quality|With a limit the server converts the video on the fly"><div><span></span><small></small></div><select class="renderer-select" data-native="Player|jellyfin-quality" onchange="setQuickNative(this)"></select></div>
    <div class="setting-row"><div><span>Aspetto</span><small>Tema del player</small></div><button class="setting-value" id="themeValue" onclick="toggleTheme()">Scuro</button></div>
    <div class="color-row"><div><span id="accentRowLabel">Colore accento</span><small id="accentRowNote">Pulsanti e selezioni</small></div><div class="swatches" id="accentSwatches"></div></div>
    <div class="color-row"><div><span id="bgRowLabel">Colore sfondo</span><small id="bgRowNote">Il resto si adegua da solo</small></div><div class="swatches" id="bgSwatches"></div></div>
    <div class="setting-row"><div><span>Lingua</span><small>Lingua dell'interfaccia</small></div><button class="setting-value" id="languageValue" onclick="toggleLanguage()">Italiano</button></div>
    <div class="native-section" id="componentsTitle">Componenti avanzati</div>
    <div id="providerList"><div class="provider-empty">…</div></div>
    <div class="setting-section">Questo telecomando</div>
    <div class="setting-row pref-row" data-it="Pagina iniziale|Quella che si apre quando avvii il telecomando" data-en="Start page|The one shown when the remote opens"><div><span></span><small></small></div><select class="renderer-select" id="prefStart" onchange="setPref('start',this.value)"><option value="remotePage"></option><option value="libraryPage"></option><option value="queuePage"></option></select></div>
    <div class="setting-row pref-row" data-it="Colore|Tasti, cursori e interruttori di questo telecomando" data-en="Colour|Buttons, sliders and switches of this remote"><div><span></span><small></small></div><div class="swatches" id="prefAccent"></div></div>
    <div class="setting-row pref-row" id="prefVibrateRow" data-it="Vibrazione al tocco|Un colpetto a ogni tasto, dove il telefono lo consente" data-en="Vibrate on touch|A light tap on every button, where the phone allows it"><div><span></span><small></small></div><button class="toggle" id="prefVibrate" onclick="setPref('vibrate',!prefs.vibrate)"></button></div>
    <div class="native-section" id="blocksTitle">Blocchi</div>
    <div id="layoutSettings"></div>
    <div class="setting-row tr-row reset-row" data-it="Ripristina il telecomando|Blocchi, ordine e colore come all'inizio" data-en="Reset the remote|Blocks, order and colour as at first"><div><span>Ripristina il telecomando</span><small></small></div><button class="setting-value" onclick="resetLayout()">Ripristina</button></div>
  </section>
  <section class="page" id="providerPage">
    <div class="page-head provider-head"><button class="back-link" onclick="showPage('settingsPage')">‹ <span id="providerBack">Impostazioni</span></button><h1 id="providerTitle"></h1><small id="providerNote"></small></div>
    <div class="provider-tabs" id="providerTabs"></div>
    <div id="providerBody"></div>
  </section>
</main>
<aside class="media-sheet" id="mediaSheet"><div class="media-sheet-head"><button id="mediaSheetBack">‹</button><strong id="mediaSheetTitle"></strong></div><div class="list" id="mediaSheetList"></div></aside>
<aside class="opt-sheet" id="optSheet" aria-hidden="true"><div class="opt-head"><button id="optBack" aria-label="Indietro">&#8249;</button><strong id="optTitle">Opzioni</strong><button id="optClose" aria-label="Chiudi">&#215;</button></div><div id="optBody"></div></aside>
<aside class="track-sheet" id="trackSheet" aria-hidden="true">
  <div class="keyboard-top"><strong id="trackTitle">Tracce</strong><button id="trackClose" aria-label="Chiudi">×</button></div>
  <div class="track-list" id="trackList"></div>
  <div class="track-extra" id="trackExtra" hidden><button id="subDownload" onclick="cmd('subtitle_download')">Scarica sottotitoli</button><button id="subRealign" onclick="cmd('subtitle_realign')">Riallinea</button></div>
</aside>
<aside class="keyboard-sheet" id="keyboardSheet" aria-hidden="true">
  <div class="keyboard-top"><strong>Scrivi su Cinecore</strong><button id="keyboardClose" aria-label="Chiudi">×</button></div>
  <input class="keyboard-input" id="keyboardInput" type="text" autocomplete="off" autocapitalize="sentences" placeholder="Titolo, ricerca o testo">
  <div class="keyboard-actions"><button id="keyboardClear">Pulisci</button><button id="keyboardEnter">Invio</button><button class="send" id="keyboardSend">Invia</button></div>
</aside>
<nav class="tabs">
  <button class="tab active" data-page="remotePage"><img src="{{{{{home}}}}}" alt=""><span>Telecomando</span></button>
  <button class="tab" data-page="queuePage"><img src="{{{{{queue}}}}}" alt=""><span>Coda</span></button>
  <button class="tab" data-page="libraryPage"><img src="{{{{{library}}}}}" alt=""><span>Libreria</span></button>
  <button class="tab" data-page="settingsPage"><img src="{{{{{settings}}}}}" alt=""><span>Impostazioni</span></button>
</nav>
<script>
const I18N={
it:{noPlayback:'Nessuna riproduzione',queueEmpty:'La coda è vuota.',libraryEmpty:'La libreria non contiene ancora elementi.',categoryEmpty:'Nessun contenuto in questa categoria.',noSearch:'Nessun risultato.',items:'elementi',untitled:'Senza titolo',item:'Elemento',playing:'In riproduzione',position:'Posizione',select:'Seleziona',audioLanguage:'Lingua audio',subtitles:'Sottotitoli',tracks:'Tracce',off:'Disattivati',noTracks:'Nessuna traccia disponibile.',track:'Traccia',active:'Attivo',disabled:'Disattivata',light:'Chiaro',dark:'Scuro',movies:'Film',series:'Serie TV',music:'Musica',blockHeader:'In riproduzione',blockQuick:'Azioni rapide',blockNavigation:'Navigazione',blockTransport:'Controlli traccia',blockSliders:'Volume',remote:'Telecomando',queue:'Coda',library:'Libreria',settings:'Impostazioni',keyboard:'Tastiera',more:'Extra',search:'Cerca nella libreria',write:'Scrivi su Cinecore',clear:'Pulisci',enter:'Invio',send:'Invia',loadingTracks:'Caricamento tracce…'},
en:{noPlayback:'Nothing playing',queueEmpty:'The queue is empty.',libraryEmpty:'The library does not contain any items yet.',categoryEmpty:'No content in this category.',noSearch:'No results.',items:'items',untitled:'Untitled',item:'Item',playing:'Now playing',position:'Position',select:'Select',audioLanguage:'Audio language',subtitles:'Subtitles',tracks:'Tracks',off:'Off',noTracks:'No tracks available.',track:'Track',active:'On',disabled:'Off',light:'Light',dark:'Dark',movies:'Movies',series:'TV Series',music:'Music',blockHeader:'Now playing',blockQuick:'Quick actions',blockNavigation:'Navigation',blockTransport:'Playback controls',blockSliders:'Volume',remote:'Remote',queue:'Queue',library:'Library',settings:'Settings',keyboard:'Keyboard',more:'More',search:'Search library',write:'Type on Cinecore',clear:'Clear',enter:'Enter',send:'Send',loadingTracks:'Loading tracks…'}};
let uiLang='it';const tr=k=>(I18N[uiLang]&&I18N[uiLang][k])||I18N.it[k]||k;const blockKeys={header:'blockHeader',quick:'blockQuick',navigation:'blockNavigation',transport:'blockTransport',sliders:'blockSliders'},layoutKey='cinecore_remote_blocks_v4';let draggingSeek=false,draggingVolume=false,lastCompanion=0;
function setTexts(selector,values){document.querySelectorAll(selector).forEach((el,i)=>{if(values[i]!=null)el.textContent=values[i]})}
function applyLanguage(language){const es=language==='es',next=(language==='en'||es)?'en':'it',tag=es?'es':next;if(document.body.dataset.languageApplied===tag)return;uiLang=next;document.body.dataset.languageApplied=tag;document.documentElement.lang=uiLang;const en=uiLang==='en';document.getElementById('title').dataset.empty=tr('noPlayback');setTexts('.quick .circle span',['Info',tr('subtitles'),tr('audioLanguage'),tr('keyboard'),tr('more')]);setTexts('.tabs .tab span',[tr('remote'),tr('queue'),tr('library'),tr('settings')]);setTexts('#queuePage h1',[tr('queue')]);setTexts('#libraryPage h1',[tr('library')]);setTexts('#settingsPage h1',[tr('settings')]);setTexts('.library-tab',[tr('movies'),tr('series'),tr('music')]);document.getElementById('librarySearch').placeholder=tr('search');document.getElementById('trackTitle').textContent=trackSheetTitle();document.querySelector('#keyboardSheet strong').textContent=tr('write');document.getElementById('keyboardInput').placeholder=en?'Title, search, or text':'Titolo, ricerca o testo';setTexts('.keyboard-actions button',[tr('clear'),tr('enter'),tr('send')]);setTexts('#settingsPage .setting-section',en?['Now playing','Screen','Preferences','This remote']:['In riproduzione','Schermo','Preferenze','Questo telecomando']);document.querySelectorAll('.tr-row').forEach(row=>{const t=(en?row.dataset.en:row.dataset.it).split('|'),a=row.querySelector('span'),b=row.querySelector('small');if(a)a.textContent=t[0];if(b)b.textContent=t[1]||''});document.querySelectorAll('.sync-reset').forEach(b=>b.textContent=en?'Reset':'Azzera');const names=en?['Current renderer','HDR','3D mode','Upscaling','Engine','Profile','Spotlight','Audio output','Default renderer','Appearance','Language']:['Renderer corrente','HDR','Modalità 3D','Upscaling','Motore','Profilo','Spotlight','Uscita audio','Renderer predefinito','Aspetto','Lingua'],notes=en?['Changes the engine for this session','RTX Video HDR, passthrough, and real tone mapping','Automatic detection or forced layout','Enables the engine and its available options','madVR or NVIDIA Super Resolution','Renderer threshold or profile','Cinema interface on the main screen','PC device for the sound','Used for future playback','Player theme','Interface language']:['Cambia il motore durante la sessione','RTX Video HDR, passthrough e tone-map reali','Rilevamento automatico oppure layout forzato','Attiva il motore e mostra le opzioni disponibili','madVR oppure NVIDIA Super Resolution','Soglia o profilo del renderer','Interfaccia cinema sullo schermo principale','Dispositivo del PC per l’audio','Usato per le prossime aperture','Tema del player','Lingua dell\'interfaccia'];document.querySelectorAll('#settingsPage > .setting-row:not(.extra-row):not(.pref-row):not(.tr-row),#upscaleDetails .setting-row').forEach((row,i)=>{const span=row.querySelector('span'),small=row.querySelector('small');if(span&&names[i])span.textContent=names[i];if(small&&notes[i])small.textContent=notes[i]});document.querySelectorAll('#settingsPage .extra-row').forEach(row=>{const t=(en?row.dataset.en:row.dataset.it).split('|');row.querySelector('span').textContent=t[0];row.querySelector('small').textContent=t[1];row.querySelector('button').textContent=t[2]});document.querySelector('#settingsPage .page-head p').textContent=en?'The player and this remote.':'Il player e questo telecomando.';document.querySelector('#settingsPage .primary').textContent=en?'Reset the remote':'Ripristina il telecomando';const danger=document.querySelector('#settingsPage .danger-action');if(danger&&danger.lastChild)danger.lastChild.textContent=en?'Exit Cinecore':'Esci da Cinecore';const stereoOptions=document.querySelectorAll('#settings3d option');setTexts('#settings3d option',[tr('disabled'),'Auto','Side-by-Side','Top/Bottom']);document.getElementById('keyboardClose').ariaLabel=en?'Close':'Chiudi';document.getElementById('trackClose').ariaLabel=en?'Close':'Chiudi';const ariaEn={'Avanti di 10 secondi':'Forward 10 seconds','Capitolo precedente':'Previous chapter','Capitolo successivo':'Next chapter','Chiudi':'Close','Esci da Cinecore':'Quit Cinecore','Ferma riproduzione':'Stop playback','Indietro di 10 secondi':'Back 10 seconds','Play o pausa':'Play or pause','Posizione di riproduzione':'Playback position','Schermo intero':'Fullscreen'};document.querySelectorAll('[aria-label]').forEach(el=>{if(!el.dataset.ariaIt)el.dataset.ariaIt=el.getAttribute('aria-label');const it=el.dataset.ariaIt;el.setAttribute('aria-label',en&&ariaEn[it]?ariaEn[it]:it)});applyLayout();if(window.companionData){renderQueue(window.companionData.queue||[]);renderLibrary(window.companionData.library||[]);renderTracks()}}
function deviceId(){try{let id=localStorage.getItem('ccp_device_id');if(!id){id=crypto.randomUUID?crypto.randomUUID():'ccp-'+Date.now();localStorage.setItem('ccp_device_id',id)}document.cookie='ccp_device='+encodeURIComponent(id)+'; Path=/; Max-Age=31536000; SameSite=Lax';return id}catch(_){return ''}}
function headers(){const h={},id=deviceId(),t=localStorage.getItem('ccp_token')||'';if(id)h['X-Device-Id']=id;if(t)h.Authorization='Bearer '+t;return h}
let commandQueue=Promise.resolve(),stateRequest=null,stateEpoch=0;
async function api(url,controller=new AbortController()){
 const timeout=setTimeout(()=>controller.abort(),4000);
 try{return await fetch(url,{credentials:'same-origin',cache:'no-store',headers:headers(),signal:controller.signal})}
 finally{clearTimeout(timeout)}
}

const macRemote=document.body.dataset.platform==='mac';
function refreshRemoteChoices(s){
 if(!macRemote)return;
 const language=document.getElementById('languageValue');if(language)language.closest('.setting-row').hidden=true;
 const button=document.getElementById('nowFullscreen');
 button.setAttribute('aria-pressed',String(!!s.Fullscreen));
 button.setAttribute('aria-label',s.Fullscreen?'Esci da schermo intero':'Schermo intero');
 for(const id of ['rendererCurrent','rendererDefault']){
  const el=document.getElementById(id),choices=s.RendererChoices||[],key=choices.join('|');
  if(el.dataset.choices!==key){el.dataset.choices=key;el.replaceChildren(...choices.map(v=>new Option(v==='Auto'?'Automatico':v==='MADVR'?'madVR':v==='MPCVR'?'MPC-VR':v,v)))}
 }
 const hdr=document.querySelector('[onclick*="fullscreen_extended"]');
 if(hdr)hdr.disabled=!s.ExtendedFullscreenAvailable;
 const profile=document.getElementById('upscalePreset');
 if(profile)profile.closest('.setting-row').hidden=true;
}
function cmd(c,extra=''){
 commandQueue=commandQueue.catch(()=>{}).then(async()=>{
  const r=await api('/api/cmd?cmd='+encodeURIComponent(c)+extra);
  if(r.status===401){location.replace('/');return}
  if(!r.ok)return;
  poll(true);setTimeout(()=>poll(true),120);setTimeout(()=>poll(true),400);
 }).catch(()=>{});
 return commandQueue;
}
function fmt(sec){sec=Math.max(0,Math.floor(sec||0));const h=Math.floor(sec/3600),m=Math.floor((sec%3600)/60),s=sec%60;return(h?String(h).padStart(2,'0')+':':'')+String(m).padStart(2,'0')+':'+String(s).padStart(2,'0')}
function fillRange(el){const min=+el.min||0,max=+el.max||1,v=+el.value||0,p=Math.max(0,Math.min(100,(v-min)/(max-min||1)*100));el.style.setProperty('--progress',p+'%')}
function showPage(id){closeKeyboard();closeTrackSheet();document.querySelectorAll('.page').forEach(p=>p.classList.toggle('active',p.id===id));document.querySelectorAll('.tab').forEach(t=>t.classList.toggle('active',t.dataset.page===id));if(id==='queuePage'||id==='libraryPage'){pollCompanion(true);setTimeout(()=>pollCompanion(true),260)}if(id==='settingsPage')loadSettings();if(macRemote&&id!=='remotePage')optClose()}
document.querySelectorAll('.tab').forEach(t=>t.onclick=()=>showPage(t.dataset.page));document.getElementById('moreToggle').onclick=()=>{if(optSheet.classList.contains('open'))optClose();else optOpen()};
function loadHidden(){try{return new Set(JSON.parse(localStorage.getItem(layoutKey)||'[]'))}catch(_){return new Set()}}let hidden=loadHidden();
// Personalizzazione del telecomando: resta su questo telefono (ordine e presenza dei blocchi, colore, pagina iniziale, vibrazione).
const prefKey='cinecore_remote_prefs_v1',orderKey='cinecore_remote_order_v1',defaultOrder=['header','navigation','transport','quick','sliders'],accents=['#087cff','#19b38a','#e0a12b','#e5484d','#a66bff','#ff6fb5'];
function loadPrefs(){const base={start:'remotePage',accent:accents[0],vibrate:false};try{return Object.assign(base,JSON.parse(localStorage.getItem(prefKey)||'{}'))}catch(_){return base}}
let prefs=loadPrefs();
function loadOrder(){try{const saved=JSON.parse(localStorage.getItem(orderKey)||'[]').filter(k=>defaultOrder.includes(k));return[...saved,...defaultOrder.filter(k=>!saved.includes(k))]}catch(_){return[...defaultOrder]}}
let blockOrder=loadOrder();
function setPref(key,value){prefs[key]=value;try{localStorage.setItem(prefKey,JSON.stringify(prefs))}catch(_){}applyPrefs()}
function moveBlock(key,step){const i=blockOrder.indexOf(key),j=i+step;if(i<0||j<0||j>=blockOrder.length)return;[blockOrder[i],blockOrder[j]]=[blockOrder[j],blockOrder[i]];try{localStorage.setItem(orderKey,JSON.stringify(blockOrder))}catch(_){}applyLayout()}
function applyPrefs(){
 const en=uiLang==='en';
 if(accents.includes(prefs.accent)&&prefs.accent!==accents[0])document.documentElement.style.setProperty('--accent',prefs.accent);else document.documentElement.style.removeProperty('--accent');
 document.querySelectorAll('.pref-row').forEach(row=>{const text=(en?row.dataset.en:row.dataset.it).split('|');row.querySelector('span').textContent=text[0];row.querySelector('small').textContent=text[1]||''});
 const start=document.getElementById('prefStart');if(start){const names=[tr('remote'),tr('library'),tr('queue')];[...start.options].forEach((o,i)=>o.textContent=names[i]);start.value=prefs.start}
 const swatches=document.getElementById('prefAccent');if(swatches){swatches.innerHTML=accents.map(c=>'<button class="swatch '+(prefs.accent===c?'on':'')+'" style="background:'+c+'" data-accent="'+c+'"></button>').join('');swatches.querySelectorAll('.swatch').forEach(b=>b.onclick=()=>setPref('accent',b.dataset.accent))}
 const vibrate=document.getElementById('prefVibrate');if(vibrate)vibrate.classList.toggle('on',!!prefs.vibrate);
 const vibrateRow=document.getElementById('prefVibrateRow');if(vibrateRow)vibrateRow.style.display=navigator.vibrate?'':'none';
 const blocks=document.getElementById('blocksTitle');if(blocks)blocks.textContent=en?'Blocks: order and visibility':'Blocchi: ordine e presenza';
}
function applyLayout(){
 blockOrder.forEach((key,i)=>{const el=document.querySelector('[data-block="'+key+'"]');if(el){el.style.order=i+1;el.style.display=hidden.has(key)?'none':''}});
 document.getElementById('layoutSettings').innerHTML=blockOrder.map((key,i)=>'<div class="setting-row"><span>'+tr(blockKeys[key])+'</span><div class="block-tools"><button class="mini" data-move="'+key+'" data-step="-1" '+(i===0?'disabled':'')+'>↑</button><button class="mini" data-move="'+key+'" data-step="1" '+(i===blockOrder.length-1?'disabled':'')+'>↓</button><button class="toggle '+(hidden.has(key)?'':'on')+'" data-layout="'+key+'"></button></div></div>').join('');
 document.querySelectorAll('[data-layout]').forEach(b=>b.onclick=()=>{hidden.has(b.dataset.layout)?hidden.delete(b.dataset.layout):hidden.add(b.dataset.layout);localStorage.setItem(layoutKey,JSON.stringify([...hidden]));applyLayout()});
 document.querySelectorAll('[data-move]').forEach(b=>b.onclick=()=>moveBlock(b.dataset.move,+b.dataset.step));
 applyPrefs();
}
function resetLayout(){hidden=new Set();blockOrder=[...defaultOrder];prefs={start:'remotePage',accent:accents[0],vibrate:false};try{localStorage.removeItem(layoutKey);localStorage.removeItem(orderKey);localStorage.removeItem(prefKey)}catch(_){}applyLayout()}applyLayout();
function hdrLabel(v){return({auto:'Auto',rtx:'RTX Video HDR',passthrough:'Passthrough',tonemap:'Tone-map SDR',lut:'3DLUT SDR'})[v]||'Auto'}
function stereoLabel(v){return({off:tr('disabled'),auto:'Auto',sbs:'Side-by-Side',tab:'Top/Bottom'})[v]||tr('disabled')}
async function poll(force=false){
 if(document.hidden&&!force)return;
 if(stateRequest&&!force)return;
 if(stateRequest)stateRequest.abort();
 const controller=new AbortController(),epoch=++stateEpoch;stateRequest=controller;
 try{
  const r=await api('/api/state',controller);if(r.status===401){location.replace('/');return}if(!r.ok)return;
  const s=await r.json();if(epoch!==stateEpoch)return;const root=document.documentElement.style;applyLanguage(s.Language);refreshRemoteChoices(s);
  const lightUi=!!s.LightTheme;if(s.Accent){root.setProperty('--accent',s.Accent);root.setProperty('--accent-soft','color-mix(in srgb,'+s.Accent+' 24%,'+(lightUi?'#ffffff':'#07101a')+')')}if(s.Panel){const card=s.Card||s.Panel;root.setProperty('--bg',s.Panel);root.setProperty('--surface',card);root.setProperty('--surface2','color-mix(in srgb,'+card+' 84%,'+(lightUi?'#000':'#fff')+')');if(s.Text)root.setProperty('--text',s.Text);if(s.MutedText)root.setProperty('--muted',s.MutedText);root.setProperty('--line',lightUi?'rgba(16,26,36,.13)':'rgba(205,220,232,.10)');root.setProperty('color-scheme',lightUi?'light':'dark');document.body.classList.toggle('light',lightUi);const tc=document.querySelector('meta[name=theme-color]');if(tc)tc.setAttribute('content',s.Panel)}syncColorChoices(s);
  document.getElementById('title').textContent=(s.Title||'').trim();
  const nowCard=document.querySelector('.now-card'),hasPlayback=!!s.HasPlayback;nowCard.classList.toggle('no-playback',!hasPlayback);
  const art=document.getElementById('nowArt'),artKey=s.ArtPath||'';if(art.dataset.path!==artKey){art.dataset.path=artKey;art.src=artKey?'/api/art?path='+encodeURIComponent(artKey):''}
  document.getElementById('playIcon').src=s.Paused?document.getElementById('playIcon').dataset.play:document.getElementById('playIcon').dataset.pause;
  const cur=fmt(s.Position),dur=fmt(s.Duration),ratio=s.Duration>0?Math.max(0,Math.min(100,s.Position/s.Duration*100)):0;
  document.getElementById('currentTime').textContent=cur;document.getElementById('totalTime').textContent=dur;document.getElementById('nowMeta').textContent=s.Subtitle||'';if(!draggingSeek)document.getElementById('nowProgress').style.width=ratio+'%';const timeline=document.getElementById('nowTimeline'),seekDuration=hasPlayback?Math.max(0,s.Duration||0):0;timeline.dataset.duration=seekDuration;timeline.tabIndex=seekDuration>0?0:-1;timeline.setAttribute('aria-disabled',seekDuration>0?'false':'true');timeline.setAttribute('aria-valuemax',seekDuration);timeline.setAttribute('aria-valuenow',seekDuration>0?Math.max(0,s.Position||0):0);
  if(!draggingVolume){const vol=s.Muted?0:Math.max(0,Math.min(1,s.Volume||0)),vi=document.getElementById('volume');vi.value=vol;document.getElementById('volPct').textContent=Math.round(vol*100)+'%';fillRange(vi)}
  const hv=hdrLabel(s.HdrMode),sv=stereoLabel(s.StereoMode),uv=s.Upscaling?tr('active'):'Off';
  document.getElementById('hdrQuickValue').textContent=hv;document.getElementById('stereoQuickValue').textContent=sv;document.getElementById('upscaleQuickValue').textContent=uv;
  const en2=uiLang==='en';document.getElementById('castBtn').disabled=!s.CastAvailable;document.getElementById('chaptersBtn').disabled=!s.ChaptersAvailable;document.getElementById('hdrAnalysisBtn').disabled=!s.HdrAnalysisAvailable;document.getElementById('chaptersBtn').firstChild.textContent=en2?'Chapters':'Capitoli';document.getElementById('chaptersNote').textContent=en2?'List':'Elenco';document.getElementById('hdrAnalysisBtn').firstChild.textContent=en2?'HDR analysis':'Analisi HDR';document.getElementById('hdrAnalysisNote').textContent=en2?'On the player':'Sul player';document.getElementById('subDlBtn').disabled=!s.SubtitleDownloadAvailable;document.getElementById('subSyncBtn').disabled=!s.SubtitleRealignAvailable;document.getElementById('castNote').textContent=en2?'Actors':'Interpreti';document.getElementById('subDlNote').textContent=en2?'Download':'Scarica';document.getElementById('subSyncNote').textContent=en2?'Realign':'Riallinea';const sel=document.getElementById('audioOutSelect'),devs=s.AudioDevices||[],key=devs.join('|');if(sel.dataset.key!==key){sel.dataset.key=key;sel.innerHTML='';const o0=document.createElement('option');o0.value='';o0.textContent=en2?(macRemote?'Use system default':'PC default'):(macRemote?'Usa dispositivo predefinito':'Predefinito del PC');sel.appendChild(o0);devs.forEach(d=>{const o=document.createElement('option');o.value=d;o.textContent=d;sel.appendChild(o)})}if(document.activeElement!==sel)sel.value=s.AudioDevice||'';document.getElementById('audioOutLabel').textContent=en2?'Audio output':'Uscita audio';
  const syncText=!s.AudioDelayAvailable?'—':s.AudioDelayCurve?(uiLang==='en'?'variable':'variabile'):(s.AudioDelayMs>0?'+':s.AudioDelayMs<0?'−':'')+Math.abs(s.AudioDelayMs||0)+' ms';document.querySelectorAll('.sync-value').forEach(o=>{o.textContent=syncText;o.classList.toggle('changed',!!s.AudioDelayMs)});document.querySelectorAll('.sync-ctl button,.sync-reset').forEach(b=>b.disabled=!s.AudioDelayAvailable);
  document.getElementById('hdrBtn').classList.toggle('active',s.HdrMode!=='auto');document.getElementById('stereoBtn').classList.toggle('active',s.StereoMode!=='off');document.getElementById('upscaleBtn').classList.toggle('active',!!s.Upscaling);
  const hdrSetting=document.getElementById('settingsHdr'),stereoSetting=document.getElementById('settings3d');
  hdrSetting.textContent=hv;hdrSetting.disabled=!s.HdrAvailable;stereoSetting.value=s.StereoMode||'off';stereoSetting.disabled=!s.StereoAvailable;
  const currentRenderer=document.getElementById('rendererCurrent'),defaultRenderer=document.getElementById('rendererDefault');if(document.activeElement!==currentRenderer)currentRenderer.value=s.RendererSelection||s.Renderer||'Auto';if(document.activeElement!==defaultRenderer)defaultRenderer.value=s.DefaultRenderer||'Auto';syncAudioOut(s);syncUpscalingControls(s);document.getElementById('settingsSpotlight').classList.toggle('on',!!s.Spotlight);document.getElementById('themeValue').textContent=s.LightTheme?tr('light'):tr('dark');document.getElementById('languageValue').textContent=s.Language==='es'?'Español':s.Language==='en'?'English':'Italiano';window.remoteState=s;renderOpt()
}catch(_){}finally{if(stateRequest===controller)stateRequest=null}}
// ----- Opzioni ("•••"): una scheda con pagine, come le impostazioni del telefono -----
const optSheet=document.getElementById('optSheet');let optStack=['root'];
function oT(it,en){return uiLang==='en'?en:it}
function optOpen(page){optStack=page&&page!=='root'?['root',page]:['root'];optSheet.classList.add('open');optSheet.setAttribute('aria-hidden','false');document.getElementById('moreToggle').classList.add('active');pollCompanion(true);renderOpt()}
function optClose(){optSheet.classList.remove('open');optSheet.setAttribute('aria-hidden','true');document.getElementById('moreToggle').classList.remove('active')}
function optGo(page){optStack.push(page);renderOpt();optSheet.scrollTop=0}
function optBackStep(){if(optStack.length>1){optStack.pop();renderOpt()}else optClose()}
document.getElementById('optBack').onclick=optBackStep;document.getElementById('optClose').onclick=optClose;
let optActions=[];
function optRow(r){const i=optActions.push(r.act||(r.go?()=>optGo(r.go):null))-1;const tail=r.go?'<span class="chev">&#8250;</span>':(r.sel?'<span class="tick">&#10003;</span>':'<span></span>');return '<button class="opt-row" data-opt="'+i+'"'+(r.off?' disabled':'')+'><span>'+esc(r.label)+'</span><small>'+esc(r.value||'')+'</small>'+tail+'</button>'}
function optGroup(title,rows){rows=rows.filter(Boolean);if(!rows.length)return '';return (title?'<div class="opt-sec">'+esc(title)+'</div>':'')+'<div class="opt-group">'+rows.map(optRow).join('')+'</div>'}
function optSyncText(s){if(!s.AudioDelayAvailable)return '—';if(s.AudioDelayCurve)return oT('variabile','variable');const v=s.AudioDelayMs||0;return (v>0?'+':v<0?'−':'')+Math.abs(v)+' ms'}
const hdrNames={auto:['Automatico','Automatic'],rtx:['RTX Video HDR','RTX Video HDR'],passthrough:['Passthrough HDR','HDR passthrough'],tonemap:['Converti in SDR','Convert to SDR'],lut:['SDR con 3DLUT','SDR with 3DLUT']};
const stereoNames={off:['Disattivato','Off'],auto:['Automatico','Automatic'],sbs:['Affiancato (SBS)','Side by side (SBS)'],tab:['Sopra e sotto (TAB)','Top and bottom (TAB)']};
function nm(map,k){const v=map[k];return v?oT(v[0],v[1]):k}
function upscaleName(s){if(!s.Upscaling)return oT('Disattivato','Off');return s.UpscalingBackend==='madvr'?'madVR':s.UpscalingBackend==='mpcvr'?'NVIDIA RTX':oT('Attivo','On')}
function currentLabel(list,fallback){const x=(list||[]).find(t=>t.selected);return x?x.label:fallback}
const OPT={
 root:(s,d)=>({title:oT('Opzioni','Options'),html:
  optGroup('',[
   {label:'Audio',value:currentLabel(d.audioTracks,''),go:'audio',off:!s.HasPlayback},
   {label:oT('Sottotitoli','Subtitles'),value:d.subtitlesOff?oT('Disattivati','Off'):currentLabel(d.subtitleTracks,''),go:'subs',off:!s.HasPlayback},
   {label:oT('Immagine','Picture'),value:nm(hdrNames,s.HdrMode),go:'picture',off:!s.HasPlayback},
   {label:'Film',value:'',go:'film',off:!s.HasPlayback}])}),
 audio:(s,d)=>({title:'Audio',html:
  optGroup('',[
   {label:oT('Lingua audio','Audio language'),value:currentLabel(d.audioTracks,''),go:'audioTracks'},
   {label:oT('Uscita audio','Audio output'),value:s.AudioDevice||oT(macRemote?'Usa dispositivo predefinito':'Predefinito del PC',macRemote?'Use system default':'PC default'),go:'audioOut'},
   {label:oT('Sincronia audio','Audio sync'),value:optSyncText(s),go:'sync',off:!s.AudioDelayAvailable}])}),
 audioTracks:(s,d)=>({title:oT('Lingua audio','Audio language'),html:optGroup('',(d.audioTracks||[]).map(t=>({label:t.label||oT('Traccia','Track'),sel:t.selected,act:()=>{cmd('audio_track','&id='+encodeURIComponent(t.id));setTimeout(()=>pollCompanion(true),200)}})))||'<p class="opt-note">'+oT('Nessuna traccia.','No tracks.')+'</p>'}),
 audioOut:(s,d)=>({title:oT('Uscita audio','Audio output'),html:optGroup('',[''].concat(s.AudioDevices||[]).map(v=>({label:v||oT(macRemote?'Usa dispositivo predefinito':'Predefinito del PC',macRemote?'Use system default':'PC default'),sel:(s.AudioDevice||'')===v,act:()=>cmd('audio_device','&value='+encodeURIComponent(v))})))}),
 sync:(s,d)=>{const run=!!s.AudioAutoSyncRunning,dis=!s.AudioDelayAvailable;const i1=optActions.push(()=>avDelay(-50))-1,i2=optActions.push(()=>avDelay(-10))-1,i3=optActions.push(()=>avDelay(10))-1,i4=optActions.push(()=>avDelay(50))-1,ia=optActions.push(()=>cmd('avdelay','&auto=1'))-1,ir=optActions.push(()=>avReset())-1;
  return {title:oT('Sincronia audio','Audio sync'),html:'<div class="opt-sync"><output>'+esc(optSyncText(s))+'</output>'
   +'<div class="opt-steps"><button data-opt="'+i1+'"'+(dis?' disabled':'')+'>−50</button><button data-opt="'+i2+'"'+(dis?' disabled':'')+'>−10</button><button data-opt="'+i3+'"'+(dis?' disabled':'')+'>+10</button><button data-opt="'+i4+'"'+(dis?' disabled':'')+'>+50</button></div>'
   +'<div class="opt-actions"><button class="lead" data-opt="'+ia+'"'+(!s.AudioAutoSyncAvailable||run?' disabled':'')+'>'+(run?oT('Analisi…','Analysing…'):oT('Automatica','Automatic'))+'</button><button data-opt="'+ir+'"'+(dis||run?' disabled':'')+'>'+oT('Azzera','Reset')+'</button></div>'
   +(run?'<div class="opt-progress"><i style="width:'+Math.round((s.AudioAutoSyncProgress||0)*100)+'%"></i></div>':'')+(!run&&s.AudioAutoSyncNote?'<p class="opt-note" style="color:var(--text)">'+esc(s.AudioAutoSyncNote)+'</p>':'')
   +'<p class="opt-note">'+(s.AudioAutoSyncAvailable?oT('+ ritarda l’audio, − lo anticipa. Automatica confronta la traccia in ascolto con un’altra traccia del film e applica il ritardo da sola.','+ delays the audio, − advances it. Automatic compares the track being played with another track of the film and applies the delay by itself.'):oT('+ ritarda l’audio, − lo anticipa. La sincronia automatica serve una seconda traccia audio (o una traccia esterna) da confrontare.','+ delays the audio, − advances it. Automatic sync needs a second audio track (or an external one) to compare with.'))+'</p></div>'}},
 subs:(s,d)=>({title:oT('Sottotitoli','Subtitles'),html:optGroup('',[
   {label:oT('Traccia','Track'),value:d.subtitlesOff?oT('Disattivati','Off'):currentLabel(d.subtitleTracks,''),go:'subTracks'},
   {label:oT('Scarica sottotitoli','Download subtitles'),act:()=>cmd('subtitle_download'),off:!s.SubtitleDownloadAvailable},
   {label:oT('Riallinea automaticamente','Realign automatically'),act:()=>cmd('subtitle_realign'),off:!s.SubtitleRealignAvailable}])}),
 subTracks:(s,d)=>({title:oT('Traccia sottotitoli','Subtitle track'),html:optGroup('',[{label:oT('Disattivati','Off'),sel:!!d.subtitlesOff,act:()=>{cmd('subtitle_track','&id=-1');setTimeout(()=>pollCompanion(true),200)}}].concat((d.subtitleTracks||[]).map(t=>({label:t.label||oT('Traccia','Track'),sel:t.selected,act:()=>{cmd('subtitle_track','&id='+encodeURIComponent(t.id));setTimeout(()=>pollCompanion(true),200)}}))))}),
 picture:(s,d)=>({title:oT('Immagine','Picture'),html:optGroup('',[
   s.HdrAvailable&&{label:'HDR',value:nm(hdrNames,s.HdrMode),go:'hdr'},
   s.UpscalingAvailable&&{label:'Upscaling',value:upscaleName(s),go:'upscaling'},
   s.StereoAvailable&&{label:'3D',value:nm(stereoNames,s.StereoMode),go:'stereo'},
   {label:'Renderer',value:s.Renderer||'Auto',go:'renderer'}])}),
 hdr:(s,d)=>({title:'HDR',html:optGroup('',((s.RtxHdrAvailable?['auto','rtx','passthrough','tonemap','lut']:['auto','passthrough','tonemap','lut']).filter(m=>!(s.NoSystemHdr&&m==='passthrough'))).map(m=>({label:nm(hdrNames,m),sel:s.HdrMode===m,act:()=>cmd('hdr','&mode='+m)})))}),
 upscaling:(s,d)=>({title:'Upscaling',html:optGroup('',[
   {label:oT('Disattivato','Off'),sel:!s.Upscaling,act:()=>cmd('upscaling_profile','&backend=off&preset=0')},
   s.MadVrAvailable&&{label:'madVR',sel:!!s.Upscaling&&s.UpscalingBackend==='madvr',act:()=>cmd('upscaling_profile','&backend=madvr&preset=0')},
   s.MpcvrNvidiaUpscalingAvailable&&{label:'NVIDIA RTX',sel:!!s.Upscaling&&s.UpscalingBackend==='mpcvr',act:()=>cmd('upscaling_profile','&backend=mpcvr&preset=3')}])}),
 stereo:(s,d)=>({title:'3D',html:optGroup('',['off','auto','sbs','tab'].map(m=>({label:nm(stereoNames,m),sel:s.StereoMode===m,act:()=>setStereoMode(m)})))}),
 renderer:(s,d)=>({title:'Renderer',html:optGroup('',['Auto','EVR','MPCVR','MADVR','MPV'].map(v=>({label:v==='MADVR'?'madVR':v==='Auto'?oT('Automatico','Automatic'):v,sel:(s.RendererSelection||'Auto').toUpperCase()===v.toUpperCase(),act:()=>setRenderer(false,v)})))
   +'<p class="opt-note" style="margin:12px auto 0">'+oT('Cambia il renderer per questa riproduzione. Quello predefinito si sceglie nelle Impostazioni.','Changes the renderer for this playback. The default one is chosen in Settings.')+'</p>'}),
 film:(s,d)=>({title:'Film',html:optGroup('',[
   {label:oT('Capitoli','Chapters'),value:currentLabel(d.chapters,''),go:'chapters',off:!s.ChaptersAvailable},
   {label:'Cast',value:oT('Sul player','On the player'),act:()=>cmd('cast'),off:!s.CastAvailable},
   {label:'Info',value:oT('Sul player','On the player'),act:()=>cmd('info'),off:!s.HasPlayback},
   {label:oT('Analisi HDR','HDR analysis'),value:oT('Sul player','On the player'),act:()=>cmd('hdr_analysis'),off:!s.HdrAnalysisAvailable}])}),
 chapters:(s,d)=>({title:oT('Capitoli','Chapters'),html:optGroup('',(d.chapters||[]).map(c=>({label:c.label,sel:c.selected,act:()=>{cmd('chapter','&id='+c.id);setTimeout(()=>pollCompanion(true),300)}})))||'<p class="opt-note">'+oT('Nessun capitolo.','No chapters.')+'</p>'})
};
// Opzioni in una pagina: niente sotto-menu (solo i capitoli, che possono essere tanti).
OPT.root=(s,d)=>{
 const sync=OPT.sync(s,d).html;
 const devs=[''].concat(s.AudioDevices||[]);
 const outIdx=optActions.push(null)-1;
 const out='<label class="opt-row opt-select"><span>'+oT('Uscita audio','Audio output')+'</span><select data-out="'+outIdx+'">'
  +devs.map(v=>'<option value="'+esc(v)+'"'+((s.AudioDevice||'')===v?' selected':'')+'>'+esc(v||oT(macRemote?'Usa dispositivo predefinito':'Predefinito del PC',macRemote?'Use system default':'PC default'))+'</option>').join('')+'</select><span></span></label>';
 return {title:oT('Opzioni','Options'),html:
  '<div class="opt-sec">'+oT('Sincronia audio','Audio sync')+'</div><div class="opt-group opt-pad">'+sync+'</div>'
  +'<div class="opt-sec">Audio</div><div class="opt-group">'+out+'</div>'
  +optGroup('Film',[
   {label:oT('Capitoli','Chapters'),value:currentLabel(d.chapters,''),go:'chapters',off:!s.ChaptersAvailable},
   {label:'Cast',value:oT('Sul player','On the player'),act:()=>cmd('cast'),off:!s.CastAvailable},
   {label:'Info',value:oT('Sul player','On the player'),act:()=>cmd('info'),off:!s.HasPlayback}])};
};
// Extra: la scheda del film sul telefono (info, cast, dati tecnici, analisi HDR), poi capitoli e sincronia.
function syncBlock(s){const run=!!s.AudioAutoSyncRunning,dis=!s.AudioDelayAvailable,v=s.AudioDelayMs||0;
 const ia=optActions.push(()=>cmd('avdelay',run?'&stop=1':'&auto=1'))-1,ir=optActions.push(()=>{cmd('avdelay','&reset=1');const f=document.getElementById('syncInput');if(f)f.value='0'})-1;
 const st=[-50,-10,10,50].map(n=>[n,optActions.push(()=>cmd('avdelay','&step='+n))-1]);
 return '<div class="sync4"><div class="val"><input id="syncInput" type="number" inputmode="numeric" step="1" value="'+(s.AudioDelayCurve?'':v)+'" placeholder="'+(s.AudioDelayCurve?oT('variabile','variable'):'0')+'"'+(dis?' disabled':'')+'><span>ms</span></div>'
  +'<div class="steps">'+st.map(([n,id])=>'<button data-opt="'+id+'"'+(dis?' disabled':'')+'>'+(n>0?'+':'−')+Math.abs(n)+'</button>').join('')+'</div>'
  +'<div class="links"><button data-opt="'+ia+'"'+(!s.AudioAutoSyncAvailable&&!run?' disabled':'')+'>'+(run?oT('Ferma','Stop')+' '+Math.round((s.AudioAutoSyncProgress||0)*100)+'%':oT('Automatica','Automatic'))+'</button><button data-opt="'+ir+'"'+(dis?' disabled':'')+'>'+oT('Azzera','Reset')+'</button></div>'
  +(!run&&s.AudioAutoSyncNote?'<p class="sync-note" style="color:var(--text)">'+esc(s.AudioAutoSyncNote)+'</p>':'')+'</div>'}
OPT.sync=(s,d)=>({title:oT('Sincronia audio','Audio sync'),html:'<div class="opt-group">'+syncBlock(s)+'</div>'});
OPT.root=(s,d)=>{
 const f=d.film;let html='';
 if(f){
  html+='<div class="film-head">'+(f.poster?'<img src="/api/art?path='+encodeURIComponent(f.poster)+'" alt="">':'<span></span>')+'<div><strong>'+esc(f.title)+'</strong><small>'+esc(f.meta||'')+'</small></div></div>'
   ;
  if(f.overview)html+='<div class="opt-sec">'+oT('Trama','Overview')+'</div><p class="film-overview">'+esc(f.overview)+'</p>';
  if(f.cast&&f.cast.length)html+='<div class="opt-sec">Cast</div><div class="cast-strip">'+f.cast.map(c=>'<div class="cast-card">'+(c.photo?'<img loading="lazy" src="/api/art?path='+encodeURIComponent(c.photo)+'" alt="">':'<div class="ph">'+esc((c.name||'?').split(' ').map(w=>w[0]).slice(0,2).join(''))+'</div>')+'<b>'+esc(c.name)+'</b><span>'+esc(c.character||'')+'</span></div>').join('')+'</div>';
  if(f.tech&&f.tech.length)html+='<div class="opt-sec">'+oT('Dati tecnici','Technical')+'</div><div class="kv plain">'+f.tech.map(t=>'<div>'+esc(t)+'</div>').join('')+'</div>';
  if(f.hdr){const h=f.hdr,n=v=>Number(v).toLocaleString(uiLang==='en'?'en':'it');
   const kv=(a,b)=>'<div><span>'+a+'</span><b>'+b+'</b></div>';
   html+='<div class="opt-sec">'+oT('Analisi HDR','HDR analysis')+(h.formats?' · '+esc(h.formats):'')+'</div><div class="kv">'
    +kv(oT('Picco','Peak'),n(h.peak)+' nit')+kv(oT('Media più alta','Brightest average'),n(h.brightestAverage)+' nit')
    +kv(oT('Luminosità tipica','Typical'),n(h.typical)+' nit')+kv(oT('Media del film','Film average'),n(h.filmAverage)+' nit')
    +kv(oT('Gamma dinamica','Dynamic range'),n(h.stops)+' stop')+kv(oT('Sopra 100 nit','Above 100 nit'),n(h.above100)+'%')
    +kv(oT('Colori','Colours'),'Rec.709 '+n(h.rec709)+'% · P3 '+n(h.p3)+'% · BT.2020 '+n(h.bt2020)+'%')+'</div>'}
 } else if(!s.HasPlayback) html+='<p class="opt-note" style="margin:30px auto">'+oT('Avvia un film per vedere qui le sue informazioni.','Start a film to see its information here.')+'</p>';
 
 html+='<div class="opt-sec">'+oT('Sincronia audio','Audio sync')+'</div><div class="opt-group">'+syncBlock(s)+'</div><p class="sync-note">'+oT('+ ritarda l’audio, − lo anticipa. Resta salvata per questo film.','+ delays the audio, − advances it. Saved for this film.')+'</p>';
 return {title:'Extra',html};
};
function syncAudioOut(s){document.querySelectorAll('#audioOutSelect,#audioOutSettings').forEach(el=>{if(document.activeElement===el)return;const devs=[''].concat(s.AudioDevices||[]),key=devs.join('|');if(el.dataset.k!==key){el.innerHTML=devs.map(v=>'<option value="'+esc(v)+'">'+esc(v||(uiLang==='en'?(macRemote?'Use system default':'PC default'):(macRemote?'Usa dispositivo predefinito':'Predefinito del PC')))+'</option>').join('');el.dataset.k=key}el.value=s.AudioDevice||''})}
function renderOpt(){
 try{
  const s=window.remoteState||{};document.getElementById('syncLinkLabel').textContent=oT('Sincronia audio','Audio sync');document.getElementById('syncLinkValue').textContent=optSyncText(s);
  if(!optSheet.classList.contains('open'))return;
  if(document.activeElement&&document.activeElement.closest&&document.activeElement.closest('#optBody select,#optBody input'))return;
  const d=window.companionData||{},page=optStack[optStack.length-1],build=OPT[page]||OPT.root;optActions=[];
  const out=build(s,d);document.getElementById('optTitle').textContent=out.title;document.getElementById('optBack').classList.toggle('hidden',optStack.length<2);
  const body=document.getElementById('optBody');if(body.__html===out.html&&body.__page===page)return;body.__html=out.html;body.__page=page;body.innerHTML=out.html;
  body.querySelectorAll('[data-opt]').forEach(b=>b.onclick=()=>{const f=optActions[+b.dataset.opt];if(f&&!b.disabled)f()});
  body.querySelectorAll('select[data-out]').forEach(sel=>sel.onchange=()=>cmd('audio_device','&value='+encodeURIComponent(sel.value)));
  const sf=document.getElementById('syncInput');if(sf){const apply=()=>{const v=parseInt(sf.value,10);if(!isNaN(v))cmd('avdelay','&set='+v)};sf.onchange=apply;sf.onkeydown=e=>{if(e.key==='Enter'){sf.blur()}}}
 }catch(_){}
}
// ----- Colori del player dal telefono: preimpostati piu' un pallino libero (selettore del telefono) -----
const accentPresets=['#23a4ff','#5158c9','#715999','#b7356f','#c8673c','#189695'],bgPresets=['#070c12','#0b1120','#120e0c','#16181c','#e2e6eb','#f6f7f9'];
function hexOf(c){c=String(c||'').trim();if(/^#[0-9a-f]{6}$/i.test(c))return c.toLowerCase();const m=c.match(/rgba?\((\d+)\D+(\d+)\D+(\d+)/i);return m?'#'+[m[1],m[2],m[3]].map(v=>(+v).toString(16).padStart(2,'0')).join(''):''}
function buildSwatches(id,presets,current,key){const box=document.getElementById(id);if(!box)return;const sig=presets.join()+'|'+current;if(box.dataset.sig===sig)return;box.dataset.sig=sig;const preset=presets.includes(current);
 box.innerHTML=presets.map(c=>'<button class="swatch'+(c===current?' on':'')+'" style="background:'+c+'" data-color="'+c+'" aria-label="'+c+'"></button>').join('')
  +'<label class="swatch free'+(preset?'':' on')+'" style="--fill:'+(preset?'var(--surface)':current)+'"><input type="color" value="'+(current||'#1682f3')+'"></label>';
 box.querySelectorAll('[data-color]').forEach(b=>b.onclick=()=>cmd('theme_color','&key='+key+'&value='+encodeURIComponent(b.dataset.color)));
 const input=box.querySelector('input');input.onchange=()=>cmd('theme_color','&key='+key+'&value='+encodeURIComponent(input.value.toLowerCase()))}
function syncColorChoices(s){buildSwatches('accentSwatches',accentPresets,hexOf(s.Accent),'accent');buildSwatches('bgSwatches',bgPresets,hexOf(s.Panel),'background');
 const en=uiLang==='en';document.getElementById('accentRowLabel').textContent=en?'Accent color':'Colore accento';document.getElementById('accentRowNote').textContent=en?'Buttons and selections':'Pulsanti e selezioni';
 document.getElementById('bgRowLabel').textContent=en?'Background color':'Colore sfondo';document.getElementById('bgRowNote').textContent=en?'Everything else follows':'Il resto si adegua da solo'}
function cycleHdr(){const s=window.remoteState;if(!s||!s.HdrAvailable)return;const modes=s.HdrChoices||(s.RtxHdrAvailable?['auto','rtx','passthrough','tonemap','lut']:['auto','passthrough','tonemap','lut']).filter(m=>!(s.NoSystemHdr&&m==='passthrough')),i=Math.max(0,modes.indexOf(s.HdrMode));cmd('hdr','&mode='+modes[(i+1)%modes.length])}
function avDelay(step){cmd('avdelay','&step='+step)}
function avReset(){cmd('avdelay','&reset=1')}
function cycle3d(){const s=window.remoteState;if(!s||!s.StereoAvailable)return;const modes=['off','auto','sbs','tab'],i=Math.max(0,modes.indexOf(s.StereoMode));cmd('stereo','&mode='+modes[(i+1)%modes.length])}
function setStereoMode(value){cmd('stereo','&mode='+encodeURIComponent(value||'off'));setTimeout(poll,180)}
function toggleUpscaling(){const s=window.remoteState;if(!s||!s.UpscalingAvailable)return;if(s.Upscaling){cmd('upscaling_profile','&backend=off&preset=0')}else{const b=s.MadVrAvailable?'madvr':'mpcvr',p=b==='madvr'?'0':'3';cmd('upscaling_profile','&backend='+b+'&preset='+p)}setTimeout(poll,180)}
function setRenderer(asDefault,value){cmd(asDefault?'renderer_default':'renderer_current','&value='+encodeURIComponent((value||'auto').toLowerCase()))}
function syncUpscalingControls(s){const backend=document.getElementById('upscaleBackend'),preset=document.getElementById('upscalePreset'),kind=s.UpscalingBackend||'off',enabled=kind!=='off'&&!!s.Upscaling,en=uiLang==='en';document.getElementById('settingsUpscaling').classList.toggle('on',enabled);document.getElementById('upscaleDetails').classList.toggle('open',enabled);backend.innerHTML='<option value="off">Off</option>'+(s.MadVrAvailable?'<option value="madvr">madVR</option>':'')+(s.MpcvrNvidiaUpscalingAvailable?'<option value="mpcvr">MPCVR NVIDIA</option>':'');backend.value=kind;const mad=kind==='madvr';preset.innerHTML=mad?['Renderer',...(Array.from({length:6},(_,i)=>(en?'Profile ':'Profilo ')+(i+1)))].map((x,i)=>'<option value="'+i+'">'+x+'</option>').join(''):[['1',(en?'up to ':'fino a ')+'SD'],['2',(en?'up to ':'fino a ')+'720p'],['3',(en?'up to ':'fino a ')+'1080p'],['4',(en?'up to ':'fino a ')+'1440p']].map(x=>'<option value="'+x[0]+'">'+x[1]+'</option>').join('');preset.disabled=!enabled;preset.value=s.UpscalingPreset||(mad?'0':'3');document.getElementById('upscaleHelp').textContent=mad?(en?'Profile configured in madVR':'Profilo configurato in madVR'):(en?'NVIDIA Super Resolution threshold':'Soglia NVIDIA Super Resolution')}
function setUpscalingProfile(){const backend=document.getElementById('upscaleBackend').value,preset=document.getElementById('upscalePreset').value||'0';cmd('upscaling_profile','&backend='+encodeURIComponent(backend)+'&preset='+encodeURIComponent(preset));setTimeout(poll,180)}
function toggleTheme(){const light=!!(window.remoteState&&window.remoteState.LightTheme);cmd('theme','&mode='+(light?'dark':'light'))}function toggleLanguage(){const l=window.remoteState&&window.remoteState.Language;cmd('language','&value='+(l==='it'?'en':l==='en'?'es':'it'))}
function esc(v){return String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
async function pollCompanion(force=false){if(!force&&Date.now()-lastCompanion<3000)return;lastCompanion=Date.now();try{const r=await api('/api/companion');if(!r.ok)return;const d=await r.json();window.companionData=d;if(typeof d.librarySource==='string'){const net=d.librarySource==='network';if(sourceWanted===null||Date.now()>sourceWantedUntil||net===sourceWanted){sourceWanted=null;markLibrarySource(net,false)}}renderQueue(d.queue||[]);renderLibrary(d.library||[]);renderTracks();renderOpt()}catch(_){}}
const mediaIcons={movie:"{{{{{movie}}}}}",music:"{{{{{music}}}}}",photo:"{{{{{photo}}}}}"};
function renderQueue(items){const el=document.getElementById('queueList');if(!items.length){el.innerHTML='<div class="empty">'+tr('queueEmpty')+'</div>';return}el.innerHTML=items.map((x,i)=>'<button class="media-row '+(x.current?'current':'')+'" data-open="'+encodeURIComponent(x.path||'')+'" data-title="'+esc(x.title||'')+'"><span class="row-icon'+(x.artPath?' row-art':'')+'">'+(x.artPath?'<img src="/api/art?path='+encodeURIComponent(x.artPath)+'" alt="" onerror="this.onerror=null;this.parentNode.classList.remove(\'row-art\');this.src=\''+(mediaIcons[x.kind]||mediaIcons.movie)+'\'">':'<img src="'+(mediaIcons[x.kind]||mediaIcons.movie)+'">')+'</span><span><div class="row-title">'+esc(x.title||tr('item')+' '+(i+1))+'</div><div class="row-sub">'+(x.current?tr('playing'):tr('position')+' '+(i+1))+'</div></span><span class="chev"><img src="{{{{{chevron}}}}}" alt=""></span></button>').join('');el.querySelectorAll('[data-open]').forEach(b=>b.onclick=()=>cmd('open','&url='+encodeURIComponent(decodeURIComponent(b.dataset.open))+'&title='+encodeURIComponent(b.dataset.title||'')))}
let activeLibraryKey='Movies';
function libraryFallback(x){const k=((x&&x.kind)||'').toLowerCase();if(k.includes('music')||k.includes('album')||k.includes('track'))return'{{{{{music}}}}}';if(k.includes('series')||k.includes('season')||k.includes('episode'))return'{{{{{tv}}}}}';return'{{{{{movie}}}}}'}
function libraryLabel(g){return g.key==='Movies'?tr('movies'):g.key==='TV Series'?tr('series'):g.key==='Music'?tr('music'):(g.label||'')}
function markLibrarySource(network,busy){const a=document.getElementById('srcLocal'),b=document.getElementById('srcNet');if(!a||!b)return;b.textContent=(navigator.language||'it').toLowerCase().startsWith('it')?'Rete':'Network';a.classList.toggle('active',!network);b.classList.toggle('active',!!network);a.classList.toggle('busy',!!busy&&!network);b.classList.toggle('busy',!!busy&&!!network)}
let sourceWanted=null,sourceWantedUntil=0;
function setLibrarySource(network){sourceWanted=network;sourceWantedUntil=Date.now()+9000;markLibrarySource(network,true);cmd('library_source','&value='+(network?'network':'local'));[700,2000,4000,7000].forEach(ms=>setTimeout(()=>pollCompanion(true),ms))}
function renderLibrary(groups){const el=document.getElementById('libraryList');window.libraryGroups=groups;if(!groups.length){el.innerHTML='<div class="empty">'+tr('libraryEmpty')+'</div>';return}const main=groups.find(g=>g.key===activeLibraryKey),resume=groups.find(g=>g.key==='Resume'),resumeKind=activeLibraryKey==='Music'?'resume-music':activeLibraryKey==='TV Series'?'resume-series':'resume-movie',sections=[];if(resume){const items=(resume.items||[]).filter(x=>x.kind===resumeKind);if(items.length)sections.push({...resume,count:items.length,items})}if(main&&(main.items||[]).length)sections.push(main);window.visibleLibrarySections=sections;if(!sections.length){el.innerHTML='<div class="empty">'+tr('categoryEmpty')+'</div>';return}el.innerHTML=sections.map((g,gi)=>{const label=libraryLabel(g);return '<section class="library-section" data-library-section><div class="category-row"><span><span class="row-title">'+esc(label)+'</span><span class="row-sub">'+(g.items||[]).length+' '+tr('items')+'</span></span></div><div class="mini-grid">'+(g.items||[]).map((x,xi)=>{const fallback=libraryFallback(x),art=x.artPath?'<img class="mini-art" src="/api/art?path='+encodeURIComponent(x.artPath)+'" alt="" onerror="this.onerror=null;this.src=\''+fallback+'\';this.classList.add(\'fallback-image\')">':'<span class="mini-art fallback-art"><img src="'+fallback+'" alt=""></span>';return '<button class="mini-card" data-group="'+gi+'" data-item="'+xi+'" data-kind="'+esc(x.kind||'item')+'" data-title="'+esc((x.title||'')+' '+(x.subtitle||''))+'">'+art+'<span class="mini-copy"><span class="mini-title">'+esc(x.title||tr('untitled'))+'</span><span class="mini-sub">'+esc(x.subtitle||label)+'</span></span></button>'}).join('')+'</div></section>'}).join('');el.querySelectorAll('[data-item]').forEach(b=>b.onclick=()=>{const x=(window.visibleLibrarySections[+b.dataset.group].items||[])[+b.dataset.item];(x.children||[]).length?openMediaSheet(x):cmd('open','&url='+encodeURIComponent(x.path||'')+'&title='+encodeURIComponent(x.title||''))});filterLibrary()}
const mediaSheet=document.getElementById('mediaSheet'),mediaStack=[];function openMediaSheet(item){mediaStack.push(item);renderMediaSheet(item);mediaSheet.classList.add('open')}function renderMediaSheet(item){document.getElementById('mediaSheetTitle').textContent=item.title||tr('select');const list=document.getElementById('mediaSheetList'),children=item.children||[];list.innerHTML=children.map((x,i)=>'<button class="media-row" data-child="'+i+'"><span class="row-icon">'+(x.artPath?'<img src="/api/art?path='+encodeURIComponent(x.artPath)+'">':'')+'</span><span><div class="row-title">'+esc(x.title||tr('item'))+'</div><div class="row-sub">'+esc(x.subtitle||'')+'</div></span><span class="chev"><img src="{{{{{chevron}}}}}" alt=""></span></button>').join('');list.querySelectorAll('[data-child]').forEach(b=>b.onclick=()=>{const x=children[+b.dataset.child];(x.children||[]).length?openMediaSheet(x):cmd('open','&url='+encodeURIComponent(x.path||'')+'&title='+encodeURIComponent(x.title||''))})}document.getElementById('mediaSheetBack').onclick=()=>{mediaStack.pop();if(mediaStack.length)renderMediaSheet(mediaStack[mediaStack.length-1]);else mediaSheet.classList.remove('open')};
function filterLibrary(){const q=(document.getElementById('librarySearch').value||'').trim().toLocaleLowerCase(uiLang);document.querySelectorAll('[data-library-section]').forEach(section=>{let visible=0;section.querySelectorAll('[data-title]').forEach(card=>{const match=!q||(card.dataset.title||'').toLocaleLowerCase(uiLang).includes(q);card.hidden=!match;if(match)visible++});section.hidden=visible===0})}
document.getElementById('librarySearch').addEventListener('input',filterLibrary);
document.querySelectorAll('[data-library-key]').forEach(button=>button.onclick=()=>{activeLibraryKey=button.dataset.libraryKey;document.querySelectorAll('[data-library-key]').forEach(x=>x.classList.toggle('active',x===button));renderLibrary(window.libraryGroups||[])});
const trackSheet=document.getElementById('trackSheet'),trackList=document.getElementById('trackList');let trackType='subtitle';
function trackSheetTitle(){return trackType==='audio'?tr('audioLanguage'):trackType==='chapter'?(uiLang==='en'?'Chapters':'Capitoli'):tr('subtitles')}
function openTrackSheet(type){closeKeyboard();trackType=type==='audio'||type==='chapter'?type:'subtitle';document.getElementById('trackTitle').textContent=trackSheetTitle();trackSheet.classList.add('open');trackSheet.setAttribute('aria-hidden','false');trackList.innerHTML='<div class="empty">'+tr('loadingTracks')+'</div>';pollCompanion(true);[220,520,1050,1800].forEach(ms=>setTimeout(()=>{if(trackSheet.classList.contains('open'))pollCompanion(true)},ms))}
function closeTrackSheet(){trackSheet.classList.remove('open');trackSheet.setAttribute('aria-hidden','true')}
function renderTracks(){if(!trackSheet.classList.contains('open'))return;{const ex=document.getElementById('trackExtra'),st=window.remoteState||{},en=uiLang==='en';ex.hidden=trackType!=='subtitle';const dl=document.getElementById('subDownload'),ra=document.getElementById('subRealign');dl.textContent=en?'Download subtitles':'Scarica sottotitoli';ra.textContent=en?'Realign':'Riallinea';dl.disabled=!st.SubtitleDownloadAvailable;ra.disabled=!st.SubtitleRealignAvailable}const data=window.companionData||{},items=trackType==='audio'?(data.audioTracks||[]):trackType==='chapter'?(data.chapters||[]):(data.subtitleTracks||[]),rows=[];if(trackType==='subtitle')rows.push({id:-1,label:tr('off'),selected:!!data.subtitlesOff});items.forEach(x=>rows.push(x));if(!rows.length){trackList.innerHTML='<div class="empty">'+(trackType==='chapter'?(uiLang==='en'?'No chapters':'Nessun capitolo'):tr('noTracks'))+'</div>';return}trackList.innerHTML=rows.map(x=>'<button class="track-option '+(x.selected?'selected':'')+'" data-track="'+x.id+'"><span>'+esc(x.label||tr('track'))+'</span><span class="track-mark"></span></button>').join('');trackList.querySelectorAll('[data-track]').forEach(b=>b.onclick=()=>{cmd(trackType==='audio'?'audio_track':trackType==='chapter'?'chapter':'subtitle_track','&id='+encodeURIComponent(b.dataset.track));setTimeout(()=>pollCompanion(true),160)})}
document.getElementById('trackClose').onclick=closeTrackSheet;
const keyboardSheet=document.getElementById('keyboardSheet'),keyboardInput=document.getElementById('keyboardInput');let textTimer=0;
function openKeyboard(){closeTrackSheet();keyboardSheet.classList.add('open');keyboardSheet.setAttribute('aria-hidden','false');setTimeout(()=>keyboardInput.focus(),80)}
function closeKeyboard(){keyboardSheet.classList.remove('open');keyboardSheet.setAttribute('aria-hidden','true');keyboardInput.blur()}
function sendKeyboardText(){clearTimeout(textTimer);cmd('text','&value='+encodeURIComponent(keyboardInput.value))}
document.getElementById('keyboardToggle').onclick=openKeyboard;document.getElementById('keyboardClose').onclick=closeKeyboard;keyboardInput.oninput=()=>{clearTimeout(textTimer);textTimer=setTimeout(sendKeyboardText,120)};document.getElementById('keyboardClear').onclick=()=>{keyboardInput.value='';sendKeyboardText();keyboardInput.focus()};document.getElementById('keyboardSend').onclick=()=>{sendKeyboardText();closeKeyboard()};document.getElementById('keyboardEnter').onclick=()=>{sendKeyboardText();cmd('keyenter')};keyboardInput.onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();sendKeyboardText();cmd('keyenter')}};
const nowTimeline=document.getElementById('nowTimeline');let seekTimer=0;function timelinePosition(e){const r=nowTimeline.getBoundingClientRect(),duration=+(nowTimeline.dataset.duration||0);return duration>0?Math.max(0,Math.min(duration,(e.clientX-r.left)/Math.max(1,r.width)*duration)):0}function previewTimeline(e){const duration=+(nowTimeline.dataset.duration||0);if(duration<=0)return 0;const pos=timelinePosition(e);draggingSeek=true;document.getElementById('nowProgress').style.width=(pos/duration*100)+'%';document.getElementById('currentTime').textContent=fmt(pos);clearTimeout(seekTimer);seekTimer=setTimeout(()=>cmd('scrub','&pos='+encodeURIComponent(pos)),70);return pos}nowTimeline.addEventListener('pointerdown',e=>{if(+(nowTimeline.dataset.duration||0)<=0)return;e.preventDefault();nowTimeline.setPointerCapture(e.pointerId);previewTimeline(e)});nowTimeline.addEventListener('pointermove',e=>{if(draggingSeek)previewTimeline(e)});function commitTimeline(e){clearTimeout(seekTimer);if(!draggingSeek)return;const pos=timelinePosition(e);draggingSeek=false;cmd('seek','&pos='+encodeURIComponent(pos))}nowTimeline.addEventListener('pointerup',commitTimeline);nowTimeline.addEventListener('pointercancel',()=>{clearTimeout(seekTimer);draggingSeek=false});
const volumeInput=document.getElementById('volume');let volumeTimer=0;volumeInput.oninput=()=>{draggingVolume=true;fillRange(volumeInput);document.getElementById('volPct').textContent=Math.round(volumeInput.value*100)+'%';clearTimeout(volumeTimer);volumeTimer=setTimeout(()=>cmd('vol','&v='+encodeURIComponent(volumeInput.value)),60)};volumeInput.onchange=()=>{draggingVolume=false;cmd('vol','&v='+encodeURIComponent(volumeInput.value))};
function bindHold(id,tap,hold){const el=document.getElementById(id);let timer=0,interval=0,held=false,active=false;const end=()=>{if(!active)return;active=false;clearTimeout(timer);clearInterval(interval);if(!held)cmd(tap);held=false};el.addEventListener('pointerdown',e=>{e.preventDefault();active=true;held=false;timer=setTimeout(()=>{if(!active)return;held=true;cmd(hold);interval=setInterval(()=>cmd(hold),260)},420)});['pointerup','pointercancel','pointerleave'].forEach(n=>el.addEventListener(n,end))}bindHold('btnBack10','back10','scan_back');bindHold('btnFwd10','fwd10','scan_fwd');
document.addEventListener('pointerdown',e=>{const b=e.target.closest('button');if(b){b.classList.add('pressed');if(prefs.vibrate&&navigator.vibrate)try{navigator.vibrate(8)}catch(_){}}});['pointerup','pointercancel'].forEach(name=>document.addEventListener(name,()=>document.querySelectorAll('button.pressed').forEach(b=>b.classList.remove('pressed'))));
function resumeConnection(){if(document.hidden)return;poll(true);pollCompanion(true)}
window.addEventListener('pageshow',resumeConnection);window.addEventListener('online',resumeConnection);window.addEventListener('focus',resumeConnection);document.addEventListener('visibilitychange',()=>{if(document.hidden){stateRequest?.abort();stateRequest=null}else resumeConnection()});
setInterval(()=>{if(document.hidden)return;poll();const active=document.querySelector('.page.active')?.id;if(active==='queuePage'||active==='libraryPage'||(macRemote&&(optSheet.classList.contains('open')||trackSheet.classList.contains('open'))))pollCompanion(false)},450);poll();document.addEventListener('contextmenu',e=>e.preventDefault());

// ---- Componenti avanzati: stesse pagine e stessi valori delle impostazioni del player.
let nativeSettings=null,nativeProvider=null,nativeGroup=null;
async function loadSettings(){
 try{const r=await api('/api/settings');if(r.status===401){location.replace('/');return}if(!r.ok)return;nativeSettings=await r.json();renderProviderList();renderQuickNative();if(nativeProvider)renderProvider()}catch(_){}
}
function esc(t){return String(t??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
function renderProviderList(){
 const list=document.getElementById('providerList');if(!list||!nativeSettings)return;
 const en=uiLang==='en';document.getElementById('componentsTitle').textContent=en?'Advanced components':'Componenti avanzati';
 list.innerHTML=(nativeSettings.providers||[]).map(p=>`<div class="provider-row" data-id="${esc(p.id)}" onclick="openProvider(this.dataset.id)"><div><span>${esc(p.title)}</span><small>${esc(p.available?p.note:(en?'Not available on this PC':'Non disponibile su questo PC'))}</small></div><img src="{{{{{chevron}}}}}" alt=""></div>`).join('');
}
function findNative(spec){const[provider,key]=spec.split('|');const p=(nativeSettings?.providers||[]).find(x=>x.id===provider);let found=null;p?.sections?.forEach(s=>s.settings.forEach(st=>{if(st.key===key)found=st}));return found}
function renderQuickNative(){document.querySelectorAll('[data-native]').forEach(el=>{const st=findNative(el.dataset.native),row=el.closest('.setting-row');if(row)row.style.display=st?'':'none';if(!st)return;if(el.tagName==='SELECT'){el.innerHTML=(st.choices||[]).map(c=>`<option value="${esc(c.value)}">${esc(c.label)}</option>`).join('');el.value=st.value}else el.classList.toggle('on',st.value==='1')})}
async function setQuickNative(el){
 const[provider,key]=el.dataset.native.split('|'),value=el.tagName==='SELECT'?el.value:(el.classList.contains('on')?'0':'1');
 try{const r=await api('/api/settings/set?provider='+encodeURIComponent(provider)+'&key='+encodeURIComponent(key)+'&value='+encodeURIComponent(value));if(r.status===401){location.replace('/');return}const res=await r.json();const st=findNative(el.dataset.native);if(st)st.value=res.value;renderQuickNative()}catch(_){}
}
function openProvider(id){nativeProvider=id;nativeGroup=null;renderProvider();showPage('providerPage');document.querySelectorAll('.tab').forEach(t=>t.classList.toggle('active',t.dataset.page==='settingsPage'))}
function renderProvider(){
 const p=(nativeSettings?.providers||[]).find(x=>x.id===nativeProvider);if(!p)return;
 const en=uiLang==='en';
 document.getElementById('providerBack').textContent=en?'Settings':'Impostazioni';
 document.getElementById('providerTitle').textContent=p.title;document.getElementById('providerNote').textContent=p.note||'';
 const groups=[...new Set((p.sections||[]).map(s=>s.group).filter(g=>g))];
 if(groups.length>1&&!groups.includes(nativeGroup))nativeGroup=groups[0];
 document.getElementById('providerTabs').innerHTML=groups.length>1?groups.map(g=>`<button class="${g===nativeGroup?'active':''}" data-group="${esc(g)}" onclick="nativeGroup=this.dataset.group;renderProvider()">${esc(g)}</button>`).join(''):'';
 const sections=(p.sections||[]).filter(s=>groups.length<=1||s.group===nativeGroup);
 document.getElementById('providerBody').innerHTML=sections.map(sec=>`<div class="native-section">${esc(sec.title)}</div>`+sec.settings.map(st=>{
  const control=st.kind==='action'
   ?`<button class="setting-value" data-key="${esc(st.key)}" data-reload="1" onclick="setNative(this.dataset.key,'do',this)">${esc(st.action)}</button>`
   :st.kind==='toggle'
   ?`<button class="toggle ${st.value==='1'?'on':''}" data-key="${esc(st.key)}" ${st.reload?'data-reload="1"':''} onclick="setNative(this.dataset.key,this.classList.contains('on')?'0':'1',this)"></button>`
   :`<select class="renderer-select" data-key="${esc(st.key)}" ${st.reload?'data-reload="1"':''} onchange="setNative(this.dataset.key,this.value,this)">${(st.choices||[]).map(c=>`<option value="${esc(c.value)}" ${String(c.value).toLowerCase()===String(st.value).toLowerCase()?'selected':''}>${esc(c.label)}</option>`).join('')}</select>`;
  return `<div class="setting-row native-row"><div><span>${esc(st.label)}</span>${st.description?`<small>${esc(st.description)}</small>`:''}</div>${control}</div>`;
 }).join('')).join('')||`<div class="provider-empty">${en?'Not available on this PC.':'Non disponibile su questo PC.'}</div>`;
}
async function setNative(key,value,el){
 const p=nativeProvider;if(!p)return;
 try{
  const r=await api('/api/settings/set?provider='+encodeURIComponent(p)+'&key='+encodeURIComponent(key)+'&value='+encodeURIComponent(value));
  if(r.status===401){location.replace('/');return}
  const res=await r.json();
  if(res.ok&&(res.reload||el?.dataset?.reload)){await loadSettings();return}
  const prov=(nativeSettings?.providers||[]).find(x=>x.id===p);
  prov?.sections?.forEach(sec=>sec.settings.forEach(st=>{if(st.key===key)st.value=res.value}));
  if(el){if(el.tagName==='SELECT')el.value=res.value;else el.classList.toggle('on',res.value==='1')}
  const row=el?.closest('.setting-row');const small=row?.querySelector('small.err');if(small)small.remove();
  if(!res.ok&&row){const e=document.createElement('small');e.className='err';e.textContent=res.error||'Errore';row.firstElementChild.appendChild(e)}
 }catch(_){}
}
loadSettings();if(prefs.start&&prefs.start!=='remotePage'&&document.getElementById(prefs.start))showPage(prefs.start);
</script>
<aside class="time-sheet" id="timeSheet" aria-hidden="true"><h3 id="timeSheetTitle">Vai al minuto</h3><p id="timeSheetHint"></p><input id="timeInput" inputmode="decimal" autocomplete="off" enterkeyhint="go"><div class="row"><button id="timeCancel" type="button">Annulla</button><button id="timeGo" type="button">Vai</button></div></aside>
<script>
// Scrivere il punto del film: piu' preciso che trascinare la linea (soprattutto con DLNA, che risponde piano).
(function(){
 const sheet=document.getElementById('timeSheet'),input=document.getElementById('timeInput'),current=document.getElementById('currentTime'),line=document.getElementById('nowTimeline');
 const length=()=>+(line.dataset.duration||0);
 function parse(text){
  const parts=String(text).trim().replace(/[^0-9]+/g,':').split(':').filter(x=>x!=='').map(Number);
  if(!parts.length||parts.some(isNaN))return NaN;
  if(parts.length===1)return parts[0]*60;
  if(parts.length===2)return length()>=3600?parts[0]*3600+parts[1]*60:parts[0]*60+parts[1];
  return parts[0]*3600+parts[1]*60+parts[2];
 }
 function open(){
  if(length()<=0)return;
  const en=uiLang==='en',long=length()>=3600;
  document.getElementById('timeSheetTitle').textContent=en?'Go to time':'Vai al minuto';
  document.getElementById('timeSheetHint').textContent=long?(en?'Minutes (95), hours and minutes (1:35) or hours, minutes and seconds (1:35:20).':'Minuti (95), ore e minuti (1:35) oppure ore, minuti e secondi (1:35:20).'):(en?'Minutes (3) or minutes and seconds (3:20).':'Minuti (3) oppure minuti e secondi (3:20).');
  document.getElementById('timeCancel').textContent=en?'Cancel':'Annulla';document.getElementById('timeGo').textContent=en?'Go':'Vai';
  input.value='';input.placeholder=long?'1:35':'3:20';
  sheet.classList.add('open');sheet.setAttribute('aria-hidden','false');setTimeout(()=>input.focus(),80);
 }
 function close(){sheet.classList.remove('open');sheet.setAttribute('aria-hidden','true');input.blur()}
 function go(){const wanted=parse(input.value);if(!isFinite(wanted)){input.focus();return}const pos=Math.max(0,Math.min(Math.max(0,length()-2),wanted));cmd('seek','&pos='+encodeURIComponent(pos));close()}
 current.addEventListener('click',open);
 document.getElementById('timeCancel').onclick=close;document.getElementById('timeGo').onclick=go;
 input.addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();go()}else if(e.key==='Escape')close()});
})();
/* Spagnolo: la pagina lavora in inglese e ogni testo che compare a schermo passa dal dizionario (come nel player). */
(function(){
 let observer=null,busy=false;
 const map=()=>window.ES||{};
 function tx(t){const m=map(),k=t.trim();if(!k)return null;let v=m[k];if(v===undefined){const i=k.lastIndexOf(' ');if(i>0&&/^[0-9]/.test(k)&&m[k.slice(i+1)]!==undefined)v=k.slice(0,i+1)+m[k.slice(i+1)]}return v===undefined||v===k?null:t.replace(k,v)}
 function walk(root){if(!root)return;if(root.nodeType===3){const v=tx(root.nodeValue);if(v!==null)root.nodeValue=v;return}if(root.nodeType!==1)return;
  for(const a of ['placeholder','aria-label','title']){const val=root.getAttribute&&root.getAttribute(a);if(val){const v=tx(val);if(v!==null)root.setAttribute(a,v)}}
  if(root.tagName==='SCRIPT'||root.tagName==='STYLE')return;for(const c of root.childNodes)walk(c)}
 window.applySpanish=function(on){if(observer){observer.disconnect();observer=null}if(!on)return;busy=true;walk(document.body);busy=false;
  observer=new MutationObserver(list=>{if(busy)return;busy=true;try{for(const m of list){if(m.type==='characterData')walk(m.target);else m.addedNodes.forEach(walk);if(m.type==='attributes')walk(m.target)}}finally{busy=false}});
  observer.observe(document.body,{subtree:true,childList:true,characterData:true,attributes:true,attributeFilter:['placeholder','aria-label','title']})};
 const original=window.applyLanguage;
 if(typeof original==='function')window.applyLanguage=function(l){original(l);window.applySpanish(l==='es')};
})();
</script>
<script>
// Barra in basso a capsula; la fonte (PC o rete) e' l'ultima voce, con la scelta a discesa del telefono.
(function(){try{
 const nav=document.querySelector('nav.tabs');if(!nav)return;
 const en=!(navigator.language||'it').toLowerCase().startsWith('it');
 const short={remotePage:en?'Remote':'Telecomando',queuePage:en?'Queue':'Coda',libraryPage:en?'Library':'Libreria',settingsPage:en?'Settings':'Impostazioni'};
 nav.querySelectorAll('.tab').forEach(t=>{const s=t.querySelector('span');if(s&&short[t.dataset.page])s.textContent=short[t.dataset.page]});
 const pick=document.createElement('label');pick.className='src-pick';
 pick.innerHTML='<span id="srcPickText">PC</span><select id="srcPick" aria-label="'+(en?'Source':'Fonte')+'"><option value="local">'+(en?'This PC':'Questo PC')+'</option><option value="network">'+(en?'Network':'Rete')+'</option></select>';
 nav.appendChild(pick);
 const sel=pick.querySelector('select');sel.onchange=()=>setLibrarySource(sel.value==='network');
 const base=markLibrarySource;
 markLibrarySource=function(network,busy){try{base(network,busy)}catch(_){}sel.value=network?'network':'local';document.getElementById('srcPickText').textContent=network?(en?'Network':'Rete'):'PC';pick.classList.toggle('busy',!!busy)};
 const net=document.getElementById('srcNet');if(net)markLibrarySource(net.classList.contains('active'),false);
}catch(_){}})();
// Libreria: "Libreria: Film ⌄" al posto delle schede, come nel player.
(function(){try{
 const page=document.getElementById('libraryPage'),tabs=page&&page.querySelector('.library-tabs');if(!tabs)return;
 const en=!(navigator.language||'it').toLowerCase().startsWith('it');
 const head=document.createElement('div');head.className='lib-head';
 const opts=[...tabs.querySelectorAll('[data-library-key]')].map(b=>'<option value="'+b.dataset.libraryKey+'">'+b.textContent+'</option>').join('');
 head.innerHTML='<b>'+(en?'Library:':'Libreria:')+'</b><label class="lib-pick"><span id="libPickText"></span><select id="libPick">'+opts+'</select></label>';
 tabs.parentNode.insertBefore(head,tabs);
 const sel=head.querySelector('select'),text=head.querySelector('#libPickText');
 const sync=()=>{const on=tabs.querySelector('.library-tab.active')||tabs.querySelector('[data-library-key]');if(on){sel.value=on.dataset.libraryKey;text.textContent=on.textContent}};
 sel.onchange=()=>{const b=tabs.querySelector('[data-library-key="'+sel.value+'"]');if(b)b.click();sync()};
 sync();new MutationObserver(sync).observe(tabs,{subtree:true,attributes:true,attributeFilter:['class']});
}catch(_){}})();
// Capitoli in prima pagina: una riga che scorre, quello in corso segnato; un tocco salta li'.
(function(){try{
 if(macRemote)return;
 const anchor=document.querySelector('#remotePage [data-block="header"]');if(!anchor)return;
 const en=!(navigator.language||'it').toLowerCase().startsWith('it');
 const title=document.createElement('div');title.className='chapters-title';title.textContent=en?'Chapters':'Capitoli';title.hidden=true;
 const strip=document.createElement('div');strip.className='chapters-strip';strip.setAttribute('aria-label',title.textContent);
 anchor.after(title);title.after(strip);
 // I blocchi della pagina sono ordinati con "order": capitoli subito dopo la locandina.
 const keepOrder=()=>{const o=getComputedStyle(anchor).order;title.style.order=o;strip.style.order=o};keepOrder();setInterval(keepOrder,1500);
 let sig='';
 function render(){
  const list=(window.companionData&&window.companionData.chapters)||[];
  const s=list.map(c=>c.id+':'+c.selected).join('|');
  title.hidden=strip.hidden=list.length<2;
  if(s===sig)return;sig=s;
  strip.innerHTML=list.map(c=>{const m=String(c.label||'').match(/^\s*([\d:]+)\s+(.*)$/);return '<button class="chap'+(c.selected?' on':'')+'" data-id="'+c.id+'"><small>'+esc(m?m[1]:'')+'</small><span>'+esc(m?m[2]:c.label)+'</span></button>'}).join('');
  strip.querySelectorAll('.chap').forEach(b=>b.onclick=()=>{cmd('chapter','&id='+b.dataset.id);strip.querySelectorAll('.chap').forEach(x=>x.classList.toggle('on',x===b));setTimeout(()=>{try{pollCompanion(true)}catch(_){}},300)});
  const on=strip.querySelector('.chap.on');if(on)strip.scrollTo({left:Math.max(0,on.offsetLeft-strip.clientWidth/3),behavior:'smooth'});
 }
 setInterval(render,700);
 // I capitoli arrivano con i dati del film: si chiedono anche quando la pagina non lo farebbe.
 setInterval(()=>{try{if(document.getElementById('remotePage').classList.contains('active')&&!document.hidden)pollCompanion()}catch(_){}},4000);
}catch(_){}})();
// Cursori: sottili a riposo, piu' spessi e con il pallino mentre li si tocca (come nel player).
(function(){
 function fill(el){const min=+el.min||0,max=+el.max||1,v=+el.value;el.style.setProperty('--fill',((v-min)/Math.max(1e-9,max-min)*100)+'%')}
 document.querySelectorAll('input[type=range]').forEach(el=>{fill(el);el.addEventListener('input',()=>fill(el));
  const on=()=>el.classList.add('active'),off=()=>el.classList.remove('active');
  el.addEventListener('pointerdown',on);el.addEventListener('pointerup',off);el.addEventListener('pointercancel',off);el.addEventListener('touchend',off);el.addEventListener('blur',off)});
 setInterval(()=>document.querySelectorAll('input[type=range]').forEach(fill),400);
 const line=document.getElementById('nowTimeline');
 if(line){const on=()=>line.classList.add('active'),off=()=>setTimeout(()=>line.classList.remove('active'),120);
  line.addEventListener('pointerdown',on);window.addEventListener('pointerup',off);window.addEventListener('pointercancel',off)}
})();
// Extra a schede (2026-10-31): Film, Tecnica, HDR (se c'e') e Audio, una cosa per volta, niente pagina lunga.
(function(){try{
 if(typeof OPT==='undefined')return;
 let tab='film';
 const act=fn=>optActions.push(fn)-1;
 const row=(a,b)=>'<div class="xrow"><span>'+a+'</span><b>'+b+'</b></div>';
 function techRows(list){
  return list.map((t,i)=>{const m=String(t).match(/^([^:]{2,24}):\s*(.+)$/);return m?row(esc(m[1]),esc(m[2])):row(i===0?'Video':'',esc(t))}).join('')}
 function syncPane(s){
  const run=!!s.AudioAutoSyncRunning,dis=!s.AudioDelayAvailable,v=s.AudioDelayMs||0;
  const step=n=>'<button class="xstep'+(Math.abs(n)>=50?' far':'')+'" data-opt="'+act(()=>cmd('avdelay','&step='+n))+'"'+(dis?' disabled':'')+'>'+(n>0?'+':'−')+Math.abs(n)+'</button>';
  const ia=act(()=>cmd('avdelay',run?'&stop=1':'&auto=1')),ir=act(()=>{cmd('avdelay','&reset=1');const f=document.getElementById('syncInput');if(f)f.value='0'});
  return '<div class="xsec">'+oT('Ritardo dell’audio','Audio delay')+'</div>'
   +'<div class="xsync"><div class="xdial">'+step(-50)+step(-10)
   +'<label class="xval"><input id="syncInput" type="number" inputmode="numeric" step="1" value="'+(s.AudioDelayCurve?'':v)+'" placeholder="'+(s.AudioDelayCurve?oT('variabile','variable'):'0')+'"'+(dis?' disabled':'')+'><small>ms</small></label>'
   +step(10)+step(50)+'</div>'
   +'<div class="xacts"><button data-opt="'+ia+'"'+(!s.AudioAutoSyncAvailable&&!run?' disabled':'')+'>'+(run?oT('Ferma','Stop')+' · '+Math.round((s.AudioAutoSyncProgress||0)*100)+'%':oT('Automatica','Automatic'))+'</button><button data-opt="'+ir+'"'+(dis?' disabled':'')+'>'+oT('Azzera','Reset')+'</button></div>'
   +(!run&&s.AudioAutoSyncNote?'<p class="xnote strong">'+esc(s.AudioAutoSyncNote)+'</p>':'')
   +'<p class="xnote">'+(dis?oT('Disponibile durante la riproduzione.','Available during playback.'):oT('Valori positivi ritardano l’audio, negativi lo anticipano. Resta salvato per questo film.','Positive values delay the audio, negative ones advance it. Saved for this film.'))+'</p></div>';
 }
 // "Sincronia audio" dalle Impostazioni: la stessa schermata di Extra › Audio, da sola.
 OPT.sync=function(s){return {title:oT('Sincronia audio','Audio sync'),html:'<div class="xpane">'+syncPane(s)+'</div>'}};
 OPT.root=function(s,d){
  const f=s.HasPlayback?d.film:null,tabs=[];
  if(f){tabs.push(['film','Film']);if(f.tech&&f.tech.length)tabs.push(['tech',oT('Tecnica','Technical')]);if(f.hdr)tabs.push(['hdr','HDR'])}
  if(macRemote&&s.ChaptersAvailable)tabs.push(['chapters',oT('Capitoli','Chapters')]);
  tabs.push(['sync','Audio']);
  if(!tabs.some(t=>t[0]===tab))tab=tabs[0][0];
  let html='<div class="xtabs">'+tabs.map(([k,l])=>'<button class="xtab'+(k===tab?' on':'')+'" data-opt="'+act(()=>{tab=k;renderOpt()})+'">'+l+'</button>').join('')+'</div><div class="xpane">';
  if(tab==='film'&&f){
   const art=macRemote?(f.backdrop||f.poster||f.preview):f.poster;
   html+='<div class="xhead">'+(art?'<img class="'+(f.backdrop?'backdrop':'poster')+'" src="/api/art?path='+encodeURIComponent(art)+'" alt="">':'')+'<div><strong>'+esc(f.title||'')+'</strong><small>'+esc(f.meta||'')+'</small></div></div>';
   if(f.overview)html+='<p class="xover">'+esc(f.overview)+'</p>';
   if(f.cast&&f.cast.length)html+='<div class="xsec">Cast</div><div class="xcast">'+f.cast.map(c=>'<div>'+(c.photo?'<img loading="lazy" src="/api/art?path='+encodeURIComponent(c.photo)+'" alt="">':'<i>'+esc((c.name||'?').split(' ').map(w=>w[0]).slice(0,2).join(''))+'</i>')+'<b>'+esc(c.name||'')+'</b><span>'+esc(c.character||'')+'</span></div>').join('')+'</div>';
  }
  else if(tab==='chapters')html+=OPT.chapters(s,d).html;
  else if(tab==='tech'&&f){html+='<div class="xlist">'+techRows(f.tech)+'</div>';
   if(macRemote&&s.HdrAnalysisAvailable)html+=optGroup('',[{label:oT('Analisi HDR','HDR analysis'),act:()=>cmd('hdr_analysis')}])}
  else if(tab==='hdr'&&f&&f.hdr){const h=f.hdr,n=v=>Number(v).toLocaleString(uiLang==='en'?'en':'it');
   html+=(h.formats?'<div class="xsec">'+esc(h.formats)+'</div>':'')+'<div class="xlist">'
    +row(oT('Picco','Peak'),n(h.peak)+' nit')+row(oT('Media più alta','Brightest average'),n(h.brightestAverage)+' nit')
    +row(oT('Luminosità tipica','Typical'),n(h.typical)+' nit')+row(oT('Media del film','Film average'),n(h.filmAverage)+' nit')
    +row(oT('Gamma dinamica','Dynamic range'),n(h.stops)+' stop')+row(oT('Sopra 100 nit','Above 100 nit'),n(h.above100)+'%')
    +row('Rec.709',n(h.rec709)+'%')+row('DCI-P3',n(h.p3)+'%')+row('BT.2020',n(h.bt2020)+'%')+'</div>'}
  else html+=syncPane(s);
  return {title:'Extra',html:html+'</div>'};
 };
}catch(_){}})();
// Impostazioni a schede (2026-10-31): Riproduzione, Preferenze, Telecomando. Prima era un'unica pagina di 2000 px.
(function(){try{
 const page=document.getElementById('settingsPage');if(!page)return;
 const head=page.querySelector('.page-head');if(!head)return;
 const en=()=>typeof uiLang!=='undefined'&&uiLang==='en';
 const names=()=>en()?{play:'Playback',pref:'Preferences',remote:'This remote'}:{play:'Riproduzione',pref:'Preferenze',remote:'Telecomando'};
 // Ogni elemento prende il gruppo dell'ultima intestazione che lo precede.
 let sec=-1,grp='play';
 [...page.children].forEach(el=>{
  if(el===head)return;
  if(el.classList.contains('setting-section')){sec++;grp=sec<=1?'play':sec===2?'pref':'remote';
   // L'intestazione uguale al nome della scheda non serve: resta solo "Schermo" dentro Riproduzione.
   if(sec===0||sec===2||sec===3)el.classList.add('tab-dup')}
  else if(el.id==='componentsTitle')grp='pref';
  else if(el.id==='blocksTitle')grp='remote';
  el.dataset.grp=grp;
 });
 const bar=document.createElement('div');bar.className='set-tabs';
 head.after(bar);
 let tab='play';
 try{tab=localStorage.getItem('cc-set-tab')||'play'}catch(_){}
 function draw(){
  const n=names();
  bar.innerHTML=['play','pref','remote'].map(k=>'<button class="set-tab'+(k===tab?' on':'')+'" data-k="'+k+'">'+n[k]+'</button>').join('');
  bar.querySelectorAll('.set-tab').forEach(b=>b.onclick=()=>{tab=b.dataset.k;try{localStorage.setItem('cc-set-tab',tab)}catch(_){}page.dataset.tab=tab;draw();window.scrollTo(0,0)});
  page.dataset.tab=tab;
  // "Componenti avanzati" senza voci: niente intestazione vuota.
  const list=document.getElementById('providerList'),title=document.getElementById('componentsTitle');
  if(title)title.classList.toggle('empty',!list||!list.children.length);
 }
 draw();setInterval(draw,1500);
}catch(_){}})();
</script>
<style>
/* Telecomando 2026-10-31 (sera): una riga di quattro tasti senza riquadro, pagina che sta tutta nello schermo. */
#remotePage .quick{display:flex!important;flex-wrap:nowrap!important;justify-content:space-around!important;grid-template-columns:none!important;background:var(--surface)!important;border:1px solid var(--line)!important;box-shadow:none!important;border-radius:30px!important;padding:6px!important;gap:0!important;width:auto!important;margin:8px 25px 12px!important}
#remotePage .quick .circle{flex:1 1 0!important;min-width:0!important;grid-column:auto!important;grid-row:auto!important;height:46px!important;background:none!important;border:0!important;box-shadow:none!important;display:grid!important;place-items:center!important}
#remotePage .quick .circle img{width:24px!important;height:24px!important}
body[data-platform="windows"] #moreToggle,#more{display:none!important}
body:has(#remotePage.active){overflow:hidden!important;overscroll-behavior:none}
#remotePage.active{height:calc(100dvh - 84px - env(safe-area-inset-top) - env(safe-area-inset-bottom))!important;min-height:0!important;overflow:hidden!important}
#remotePage .dpad-wrap{flex:1 1 auto!important;min-height:0!important;margin:8px 0!important;container-type:size}
#remotePage .nav-pad{width:min(244px,100cqh,100cqw)!important;height:min(244px,100cqh,100cqw)!important;position:relative}
#remotePage .nav-pad .dkey{width:23%!important;height:23%!important}
#remotePage .nav-pad .up{left:38.5%!important;top:0!important}
#remotePage .nav-pad .left{left:0!important;top:38.5%!important}
#remotePage .nav-pad .ok{left:38.5%!important;top:38.5%!important}
#remotePage .nav-pad .right{left:77%!important;top:38.5%!important}
#remotePage .nav-pad .down{left:38.5%!important;top:77%!important}
@media (max-height:700px){#remotePage .transport button:not(.play-main){width:46px!important;height:46px!important}#remotePage .transport .play-main{width:62px!important;height:62px!important}#remotePage .quick .circle{height:40px!important}}
/* Barra in basso: tutte le voci dentro anche sui telefoni stretti. */
nav.tabs .tab{min-width:0!important;flex:0 1 auto!important;padding-left:clamp(6px,2.4vw,14px)!important;padding-right:clamp(6px,2.4vw,14px)!important}
/* Barra in basso: solo icone (richiesta del 31/10). */
nav.tabs .tab img{display:block!important;width:26px!important;height:26px!important;opacity:.55}
nav.tabs .tab img,nav.tabs .tab.active img{filter:brightness(0) invert(1)!important}
nav.tabs .tab.active img{opacity:1!important}
nav.tabs .tab span{display:none!important}
body.light nav.tabs .tab img,body.light nav.tabs .tab.active img{filter:brightness(0)!important}
/* Impostazioni: "Sincronia audio" solo in Riproduzione (prima compariva in tutte e tre le schede); valori leggibili. */
#settingsPage[data-tab="pref"] #syncLink,#settingsPage[data-tab="remote"] #syncLink{display:none!important}
#settingsPage .setting-value{color:var(--text)!important;opacity:1!important}
/* Info a tendina: due voci in colonna, solo testo, come i menu del player. */
.info-menu{position:fixed;z-index:60;display:none;flex-direction:column;min-width:150px;padding:6px 0;border-radius:16px;background:rgba(20,22,26,.92);backdrop-filter:blur(22px);-webkit-backdrop-filter:blur(22px);box-shadow:0 14px 40px rgba(0,0,0,.45)}
.info-menu.open{display:flex}
.info-menu button{height:46px;padding:0 20px;border:0;background:none;color:#fff;font-size:16px;text-align:left}
.info-menu button:active{opacity:.6}
body.light .info-menu{background:rgba(250,250,252,.94)}
body.light .info-menu button{color:#111}
/* Sincronia audio/video nel foglio della lingua audio. */
.track-extra.av{display:block!important;padding-top:16px!important}
#subDownload,#subRealign{display:none!important}
.av-head{display:flex;justify-content:space-between;align-items:baseline;margin:0 4px 12px;font-size:14px;color:var(--muted)}
.av-head b{font-size:18px;color:var(--text);font-variant-numeric:tabular-nums}
.av-acts{display:grid;grid-template-columns:1fr 1fr;gap:10px}
.av-acts button,.av-manual button{height:48px;border:0;border-radius:14px!important;background:var(--surface2);color:var(--text);font-size:14px;font-weight:600}
.av-acts button.on{background:var(--text);color:var(--bg,#000)}
.av-manual{display:grid;grid-template-columns:1fr auto;gap:10px;margin-top:12px}
.av-manual[hidden]{display:none}
.av-manual input{height:48px;padding:0 14px;border:0;border-bottom:1px solid var(--line);background:none;color:var(--text);font-size:17px;font-variant-numeric:tabular-nums;outline:none;border-radius:0}
.av-manual button{padding:0 22px;background:var(--text);color:var(--bg,#000)}
.track-extra #avBox .av-acts button.on,.track-extra #avBox .av-manual button{background:var(--text)!important;color:var(--bg)!important}
.track-extra #avBox button{height:48px!important;font-size:14px!important}
.av-note{margin:12px 4px 0;font-size:12px;color:var(--muted);line-height:1.4}
</style>
<script>
(function(){try{
 const en=()=>typeof uiLang!=='undefined'&&uiLang==='en';
 const t=(a,b)=>en()?b:a;
 // ••• ed Extra tolti del tutto.
 const more=document.getElementById('moreToggle');if(more&&!macRemote)more.remove();
 // Info: un menu con Info e Cast, entrambi aperti sul player.
 const quick=document.querySelector('#remotePage .quick');
 const info=quick&&[...quick.querySelectorAll('button')].find(b=>(b.getAttribute('onclick')||'').indexOf("'info'")>=0);
 const menu=document.createElement('div');menu.className='info-menu';menu.id='infoMenu';document.body.appendChild(menu);
 function closeMenu(){menu.classList.remove('open')}
 function openMenu(){
  menu.innerHTML='<button data-c="info">'+t('Info','Info')+'</button><button data-c="cast">Cast</button>';
  menu.querySelectorAll('button').forEach(b=>b.onclick=e=>{e.stopPropagation();closeMenu();cmd(b.dataset.c)});
  const r=info.getBoundingClientRect();menu.classList.add('open');
  const w=menu.offsetWidth,h=menu.offsetHeight;
  menu.style.left=Math.max(10,Math.min(window.innerWidth-w-10,r.left+r.width/2-w/2))+'px';
  menu.style.top=Math.max(10,r.top-h-8)+'px';
 }
 if(info){info.id='infoToggle';info.removeAttribute('onclick');info.onclick=e=>{e.stopPropagation();menu.classList.contains('open')?closeMenu():openMenu()}}
 document.addEventListener('click',e=>{if(!menu.contains(e.target))closeMenu()},true);
 // Lingua audio: niente "Scarica sottotitoli" / "Riallinea"; al loro posto la sincronia audio/video.
 const ex=document.getElementById('trackExtra');
 let built=false,manual=false;
 function fmtCs(ms){const cs=Math.round((ms||0)/10),s=(cs<0?'−':'+')+Math.floor(Math.abs(cs)/100)+(en()?'.':',')+String(Math.abs(cs)%100).padStart(2,'0');return s+' s'}
 function build(){
  let box=document.getElementById('avBox');if(!box){box=document.createElement('div');box.id='avBox';ex.appendChild(box)}
  box.innerHTML='<div class="av-head"><span>'+t('Sincronia audio/video','Audio/video sync')+'</span><b id="avNow"></b></div>'
   +'<div class="av-acts"><button id="avAuto"></button><button id="avManualBtn">'+t('Sincronizza a mano','Sync manually')+'</button></div>'
   +'<div class="av-manual" id="avManual" hidden><input id="avCs" type="number" inputmode="numeric" step="1" placeholder="'+t('Centesimi di secondo, es. 12 o −8','Hundredths of a second, e.g. 12 or −8')+'"><button id="avApply">'+t('Applica','Apply')+'</button></div>'
   +'<p class="av-note" id="avNote"></p>';
  document.getElementById('avManualBtn').onclick=()=>{manual=!manual;update();if(manual){const f=document.getElementById('avCs');f.value=String(Math.round(((window.remoteState||{}).AudioDelayMs||0)/10));f.focus()}};
  document.getElementById('avApply').onclick=()=>{const v=parseInt(document.getElementById('avCs').value,10);if(!isNaN(v)){cmd('avdelay','&set='+(v*10));manual=false;update()}};
  document.getElementById('avCs').onkeydown=e=>{if(e.key==='Enter')document.getElementById('avApply').click()};
  document.getElementById('avAuto').onclick=()=>{const s=window.remoteState||{};cmd('avdelay',s.AudioAutoSyncRunning?'&stop=1':'&auto=1')};
  built=true;
 }
 function update(){
  if(!built)return;
  const s=window.remoteState||{},run=!!s.AudioAutoSyncRunning,dis=!s.AudioDelayAvailable;
  document.getElementById('avNow').textContent=s.AudioDelayCurve?t('variabile','variable'):fmtCs(s.AudioDelayMs);
  const a=document.getElementById('avAuto');
  a.textContent=run?t('Ferma','Stop')+' · '+Math.round((s.AudioAutoSyncProgress||0)*100)+'%':t('Sincronizza in automatico','Sync automatically');
  a.classList.toggle('on',run);a.disabled=!run&&!s.AudioAutoSyncAvailable;
  document.getElementById('avManualBtn').disabled=dis;document.getElementById('avManualBtn').classList.toggle('on',manual);
  document.getElementById('avManual').hidden=!manual;
  document.getElementById('avNote').textContent=dis?t('Disponibile durante la riproduzione.','Available during playback.')
   :(!run&&s.AudioAutoSyncNote?s.AudioAutoSyncNote+' · ':'')+t('Valori positivi ritardano l’audio, negativi lo anticipano. 100 = un secondo. Resta salvato per questo film.','Positive values delay the audio, negative ones advance it. 100 = one second. Saved for this film.');
 }
 if(ex&&typeof renderTracks==='function'){
  const base=renderTracks;
  renderTracks=function(){
   base();
   if(typeof trackType==='undefined')return;
   if(trackType==='audio'){ex.classList.add('av');if(!built)build();ex.hidden=false;update()}
   else{ex.classList.remove('av');const box=document.getElementById('avBox');if(box)box.remove();built=false;manual=false;ex.hidden=true}
  };
  setInterval(()=>{if(trackSheet.classList.contains('open')&&trackType==='audio')update()},700);
 }
}catch(_){}})();
</script>
</body>
</html>
""";
    }
}
