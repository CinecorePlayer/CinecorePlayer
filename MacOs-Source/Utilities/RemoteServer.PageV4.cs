#nullable enable

/// <summary>
/// Impostazioni del telecomando (2026-11-21), come la finestra Impostazioni del player: titolo, in alto i menu
/// "Player: …" e "Componenti …" (piu' "Telefono e PC" per quello che c'e' solo qui), sotto la scheda scelta con le
/// sezioni (titolo maiuscolo e linea sopra), i gruppi dei componenti a pillole e le scelte in un menu a tendina.
/// Le voci arrivano dal PC: sono le stesse della finestra. "In riproduzione" e "Questo telecomando" comandano i
/// controlli di prima, nascosti.
/// </summary>
internal sealed partial class RemoteServer
{
    private static string RemoteV4Addon() => """
<style id="v4style">
html body #settingsPage#settingsPage.v4set>*:not(.v4){display:none!important}
#settingsPage .v4{--v4line:color-mix(in srgb,var(--text) 10%,transparent);--v4muted:color-mix(in srgb,var(--text) 52%,transparent);padding:0 0 calc(130px + env(safe-area-inset-bottom))}
#settingsPage .v4 .v4head{position:sticky;top:0;z-index:4;padding:calc(10px + env(safe-area-inset-top)) 0 4px;background:var(--bg)}
#settingsPage .v4 h1{margin:0;font-size:30px;font-weight:700;letter-spacing:-.02em;line-height:1.15}
#settingsPage .v4 .v4menus{display:flex;gap:26px;overflow-x:auto;scrollbar-width:none;margin:10px 0 0;padding:0 0 10px}
#settingsPage .v4 .v4menus::-webkit-scrollbar{display:none}
#settingsPage .v4 .v4menus button{flex:none;display:inline-flex;align-items:center;gap:6px;border:0;background:none;padding:6px 0;color:var(--v4muted);font-size:15px;font-weight:500;white-space:nowrap}
#settingsPage .v4 .v4menus button.on{color:var(--text);font-weight:650}
#settingsPage .v4 .v4menus svg{width:10px;height:6px;flex:none;transition:transform .18s ease}
#settingsPage .v4 .v4menus button.open svg{transform:rotate(180deg)}
#settingsPage .v4 .v4note{margin:4px 0 0;font-size:13px;line-height:1.35;color:var(--v4muted)}
#settingsPage .v4 .v4pills{display:flex;gap:6px;overflow-x:auto;scrollbar-width:none;margin:16px 0 0;padding:4px;border-radius:999px;background:color-mix(in srgb,var(--text) 8%,transparent)}
#settingsPage .v4 .v4pills::-webkit-scrollbar{display:none}
#settingsPage .v4 .v4pills button{flex:none;border:0;border-radius:999px;background:none;padding:8px 14px;color:var(--v4muted);font-size:14px;font-weight:600;white-space:nowrap}
#settingsPage .v4 .v4pills button.on{background:color-mix(in srgb,var(--text) 16%,transparent);color:var(--text)}
#settingsPage .v4 .v4sec{margin:22px 0 2px;padding-top:18px;border-top:1px solid var(--v4line);font-size:12px;font-weight:600;letter-spacing:.07em;text-transform:uppercase;color:var(--v4muted)}
#settingsPage .v4 .v4head+.v4sec{border-top:0;padding-top:6px;margin-top:12px}
#settingsPage .v4 .v4row{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;column-gap:16px;width:100%;min-height:58px;padding:10px 0;border:0;background:none;color:var(--text);text-align:left;font:inherit;border-radius:0}
#settingsPage .v4 .v4row .t{min-width:0}
#settingsPage .v4 .v4row b{display:block;font-size:16px;font-weight:500;line-height:1.25}
#settingsPage .v4 .v4row small{display:block;margin-top:3px;font-size:13px;line-height:1.32;color:var(--v4muted)}
#settingsPage .v4 .v4val{display:inline-flex;align-items:center;gap:7px;max-width:46vw;border:0;background:none;padding:6px 0;color:var(--text);font-size:15px;font-weight:500;text-align:right}
#settingsPage .v4 .v4val span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
#settingsPage .v4 .v4val svg{width:10px;height:6px;flex:none;opacity:.55}
#settingsPage .v4 .v4val:active span,#settingsPage .v4 .v4act:active{color:var(--accent)}
#settingsPage .v4 .v4act{border:0;background:none;padding:6px 0 6px 8px;color:var(--accent);font-size:15px;font-weight:600;white-space:nowrap}
#settingsPage .v4 .v4info{max-width:46vw;color:var(--v4muted);font-size:15px;text-align:right;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
#settingsPage .v4 .v4sw{position:relative;flex:none;width:51px;height:31px;border:0;border-radius:16px;background:color-mix(in srgb,var(--text) 18%,transparent);transition:background .2s ease;padding:0}
#settingsPage .v4 .v4sw:after{content:"";position:absolute;top:2px;left:2px;width:27px;height:27px;border-radius:50%;background:#fff;box-shadow:0 1px 3px rgba(0,0,0,.3);transition:transform .2s ease}
#settingsPage .v4 .v4sw.on{background:var(--accent)}
#settingsPage .v4 .v4sw.on:after{transform:translateX(20px);background:var(--on-accent,#fff)}
#settingsPage .v4 .v4step{display:flex;align-items:center}
#settingsPage .v4 .v4step button{width:34px;height:36px;border:0;background:none;color:var(--v4muted);font-size:22px;font-weight:300;line-height:1;padding:0}
#settingsPage .v4 .v4step button:active{color:var(--accent)}
#settingsPage .v4 .v4step span{min-width:60px;max-width:32vw;text-align:center;font-size:15px;font-weight:500;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
#settingsPage .v4 .v4dots{grid-column:1/-1;display:flex;flex-wrap:wrap;gap:14px;margin-top:12px}
#settingsPage .v4 .v4dots button{width:30px;height:30px;border-radius:50%;border:1px solid color-mix(in srgb,var(--text) 25%,transparent);padding:0;transition:box-shadow .15s ease}
#settingsPage .v4 .v4dots button.on{box-shadow:0 0 0 2px var(--bg),0 0 0 4px var(--text)}
html body #settingsPage#settingsPage .v4 .v4moved{grid-column:1/-1;margin-top:12px!important;display:flex!important}
html body #settingsPage#settingsPage .v4 #layoutSettings .setting-row{display:grid!important;grid-template-columns:minmax(0,1fr) auto!important;align-items:center!important;min-height:56px!important;padding:4px 0!important;margin:0!important;border:0!important;background:none!important}
html body #settingsPage#settingsPage .v4 #layoutSettings .setting-row span{font-size:16px!important;font-weight:500!important}
html body #settingsPage#settingsPage .v4 #layoutSettings .block-tools{display:flex!important;align-items:center!important;gap:4px!important}
html body #settingsPage#settingsPage .v4 #layoutSettings .mini{width:34px!important;height:34px!important;border:0!important;background:none!important;box-shadow:none!important;color:var(--v4muted)!important;font-size:17px!important}
html body #settingsPage#settingsPage .v4 #layoutSettings .mini[disabled]{opacity:.25!important}
#settingsPage .v4 .v3pc{padding:0 0 14px}
#settingsPage .v4 .v4empty{padding:40px 0;color:var(--v4muted);font-size:14px;text-align:center}
/* Menu a tendina come quelli del player: lista di testo su vetro scuro, la voce scelta con la spunta. */
.v4pop{position:fixed;z-index:80;min-width:200px;max-width:calc(100vw - 32px);max-height:min(62vh,520px);overflow-y:auto;padding:6px 0;border-radius:12px;background:rgba(22,24,28,.94);backdrop-filter:blur(24px);-webkit-backdrop-filter:blur(24px);box-shadow:0 18px 44px rgba(0,0,0,.5);opacity:0;transform:translateY(-6px);transition:opacity .16s ease,transform .16s ease}
.v4pop.show{opacity:1;transform:none}
.v4pop button{display:flex;align-items:center;justify-content:space-between;gap:18px;width:100%;min-height:46px;padding:0 18px;border:0;background:none;color:#fff;font-size:16px;text-align:left}
.v4pop button i{font-style:normal;color:var(--accent);font-weight:600}
.v4pop button.sel{font-weight:650}
.v4pop button:active{background:rgba(255,255,255,.08)}
.v4pop hr{border:0;height:1px;margin:6px 18px;background:rgba(255,255,255,.12)}
.v4shade{position:fixed;inset:0;z-index:79}
body.light .v4pop{background:rgba(250,250,252,.96)}body.light .v4pop button{color:#111}
</style>
<script>
(function(){try{
 const page=document.getElementById('settingsPage');if(!page)return;
 const T0=(it,en)=>(typeof uiLang!=='undefined'&&uiLang==='en')?en:it;
 const MAC=document.body.dataset.platform==='mac';
 const T=(it,en)=>{const t=T0(it,en);return MAC?t.replace(/\bPC\b/g,'Mac'):t};
 const E=t=>String(t??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const $=id=>document.getElementById(id);
 const CHEV='<svg viewBox="0 0 10 6" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M1 1l4 4 4-4"/></svg>';
 const root=document.createElement('div');root.className='v4';page.appendChild(root);page.classList.add('v4set');
 const pcBox=page.querySelector('.v3pc'),blocks=$('layoutSettings'),swatches=$('prefAccent');
 // Come nel player: le schede del menu "Player" e quelle di "Componenti".
 const PLAYER=['Generali','Sottotitoli','Dispositivi','Cinecore Audio'];
 const PLAYER_EN={'Generali':'General','Sottotitoli':'Subtitles','Dispositivi':'Devices','Cinecore Audio':'Cinecore Audio'};
 const EXTRA=[['now',()=>T('In riproduzione','Now playing')],['pc',()=>T('Questo PC','This PC')],['remote',()=>T('Questo telecomando','This remote')]];
 let data=null,busy=false,tab='Generali',group={};
 try{tab=localStorage.getItem('cc-v4-tab')||'Generali'}catch(_){}
 const name=id=>PLAYER.includes(id)?T(id,PLAYER_EN[id]||id):(EXTRA.find(x=>x[0]===id)||[0,()=>id])[1]();
 const provider=id=>(data&&data.providers||[]).find(p=>p.id===id);
 const comps=()=>(data&&data.providers||[]).filter(p=>!PLAYER.includes(p.id));
 async function load(){try{const r=await api('/api/settings');if(r.status===401){location.replace('/');return}if(r.ok)data=await r.json()}catch(_){}}
 function choose(t){tab=t;try{localStorage.setItem('cc-v4-tab',t)}catch(_){}page.dataset.cat=t==='pc'?'pc':'index';draw();window.scrollTo(0,0)}
 // ---------- righe ----------
 const sec=t=>'<div class="v4sec">'+E(t||'')+'</div>';
 const text=(label,note)=>'<div class="t"><b>'+E(label)+'</b>'+(note?'<small>'+E(note)+'</small>':'')+'</div>';
 const valueBtn=(attrs,label)=>'<button class="v4val" '+attrs+'><span>'+E(label)+'</span>'+CHEV+'</button>';
 function settingRow(p,st){
  const k=E(p.id+'|'+st.key),label=st.label||'',note=st.description||'';
  switch(st.kind){
   case 'toggle':return '<div class="v4row">'+text(label,note)+'<button class="v4sw '+(st.value==='1'?'on':'')+'" data-set="'+k+'" data-toggle="1" aria-label="'+E(label)+'"></button></div>';
   case 'choice':{const c=(st.choices||[]).find(c=>String(c.value).toLowerCase()===String(st.value).toLowerCase());
    return '<div class="v4row">'+text(label,note)+valueBtn('data-pick="'+k+'"',c?c.label:(st.action||T('Scegli','Choose')))+'</div>'}
   case 'step':return '<div class="v4row">'+text(label,note)+'<span class="v4step"><button data-set="'+k+'" data-v="-1" aria-label="-">‹</button><span>'+E(st.action||'')+'</span><button data-set="'+k+'" data-v="1" aria-label="+">›</button></span></div>';
   case 'action':return '<div class="v4row">'+text(label,note)+'<button class="v4act" data-set="'+k+'" data-v="do">'+E(st.action||T('Apri','Open'))+'</button></div>';
   case 'colors':return '<div class="v4row">'+text(label,note)+'<span></span><div class="v4dots">'+(st.choices||[]).map(c=>'<button class="'+(c.value===st.value?'on':'')+'" style="background:'+E(c.label)+'" data-set="'+k+'" data-v="'+E(c.value)+'" aria-label="'+E(c.label)+'"></button>').join('')+'</div></div>';
   default:return '<div class="v4row">'+text(label,note)+'<span class="v4info">'+E(st.action||st.value||'')+'</span></div>';
  }
 }
 function drawProvider(id){
  const p=provider(id);if(!p)return '<div class="v4empty">'+T('Carico le impostazioni del player…','Loading the player settings…')+'</div>';
  if(!p.available)return '<p class="v4note">'+T('Non disponibile su questo PC.','Not available on this PC.')+'</p>';
  let sections=p.sections||[],h=p.note?'<p class="v4note">'+E(p.note)+'</p>':'';
  const groups=[...new Set(sections.map(s=>s.group).filter(g=>g))];
  if(groups.length>1){const g=groups.includes(group[id])?group[id]:groups[0];
   h+='<div class="v4pills">'+groups.map(x=>'<button class="'+(x===g?'on':'')+'" data-group="'+E(x)+'">'+E(x)+'</button>').join('')+'</div>';
   sections=sections.filter(s=>s.group===g)}
  return h+sections.map(s=>sec(s.title)+s.settings.map(st=>settingRow(p,st)).join('')).join('');
 }
 // "In riproduzione" e "Questo telecomando": i controlli di prima, nascosti.
 function proxySelect(id,label,note){const el=$(id);if(!el||!el.options||!el.options.length)return '';const o=el.options[el.selectedIndex];
  return '<div class="v4row">'+text(label,note)+valueBtn('data-proxy-pick="'+id+'"',o?o.textContent:T('Scegli','Choose'))+'</div>'}
 function proxyToggle(id,label,note){const el=$(id);if(!el)return '';return '<div class="v4row">'+text(label,note)+'<button class="v4sw '+(el.classList.contains('on')?'on':'')+'" data-proxy-click="'+id+'"></button></div>'}
 function proxyCycle(id,label,note){const el=$(id);if(!el)return '';return '<div class="v4row">'+text(label,note)+'<button class="v4val" data-proxy-click="'+id+'"><span>'+E(el.textContent)+'</span></button></div>'}
 function drawNow(){
  const ups=$('settingsUpscaling'),upOn=ups&&ups.classList.contains('on'),sync=$('syncLinkValue');
  return '<p class="v4note">'+T('Valgono per il film in corso.','For the film that is playing.')+'</p>'
   +sec('Video')+proxySelect('rendererCurrent','Renderer',T('Il motore video di questa sessione','The video engine for this session'))
   +proxyCycle('settingsHdr','HDR',T('RTX Video HDR, passthrough o tone mapping','RTX Video HDR, passthrough or tone mapping'))
   +proxySelect('settings3d','3D',T('Rilevamento automatico o formato forzato','Automatic detection or forced layout'))
   +proxyToggle('settingsUpscaling','Upscaling',T('Ingrandimento del video con madVR o NVIDIA','Video upscaling with madVR or NVIDIA'))
   +(upOn?proxySelect('upscaleBackend',T('Motore','Engine'),'')+proxySelect('upscalePreset',T('Profilo','Profile'),''):'')
   +sec('Audio')+proxySelect('audioOutSettings',T('Uscita audio','Audio output'),T('Il dispositivo del PC','The PC device'))
   +'<div class="v4row">'+text(T('Sincronia audio','Audio sync'),T('Anticipa o ritarda l’audio di questo film','Advance or delay the audio of this film'))+valueBtn('data-run="sync"',sync&&sync.textContent.trim()&&sync.textContent.trim()!=='—'?sync.textContent:'0 ms')+'</div>'
   +sec(T('Schermo','Screen'))+'<div class="v4row">'+text(T('Schermo intero esteso','Extended full screen'),T('Il film su tutti gli schermi insieme','The film across all screens'))+'<button class="v4act" data-run="extended">'+T('Attiva / esci','Toggle')+'</button></div>'
   +proxyToggle('settingsSpotlight','Spotlight',T('La sala a tutto schermo sul PC','The full-screen cinema on the PC'));
 }
 function drawRemote(){
  const vib=$('prefVibrateRow');
  return '<p class="v4note">'+T('Valgono solo per questo telefono.','For this phone only.')+'</p>'
   +sec(T('Telecomando','Remote'))+proxySelect('prefStart',T('Pagina iniziale','Start page'),T('Quella che si apre all’avvio','The one shown when it opens'))
   +'<div class="v4row" id="v4accent">'+text(T('Colore','Colour'),T('Tasti, cursori e interruttori','Buttons, sliders and switches'))+'<span></span></div>'
   +(vib&&!vib.hidden&&vib.style.display!=='none'?proxyToggle('prefVibrate',T('Vibrazione al tocco','Vibrate on touch'),T('Un colpetto a ogni tasto','A light tap on every button')):'')
   +sec(T('Blocchi','Blocks'))+'<div id="v4blocks"></div>'
   +sec('')+'<div class="v4row">'+text(T('Ripristina il telecomando','Reset the remote'),T('Blocchi, ordine e colore come all’inizio','Blocks, order and colour as at first'))+'<button class="v4act" data-run="reset">'+T('Ripristina','Reset')+'</button></div>';
 }
 function menus(){
  const inPlayer=PLAYER.includes(tab),inComp=!!comps().find(p=>p.id===tab),inExtra=!inPlayer&&!inComp;
  const b=(key,label,on)=>'<button class="'+(on?'on':'')+'" data-menu="'+key+'"><span>'+E(label)+'</span>'+CHEV+'</button>';
  // Due menu come nel player; quello che c'e' solo sul telefono sta in fondo al menu Player.
  return '<div class="v4menus">'+b('player',inPlayer?'Player: '+name(tab):inExtra?name(tab):'Player',inPlayer||inExtra)
   +b('comp',inComp?T('Componenti: ','Components: ')+tab:T('Componenti avanzati','Advanced components'),inComp)+'</div>';
 }
 function draw(){
  // I pezzi spostati tornano nella pagina (nascosti) prima di ridisegnare: il codice di prima li cerca per id.
  [pcBox,blocks,swatches].forEach(x=>{if(x&&x.parentNode!==page)page.appendChild(x)});
  if(tab==='pc'&&!pcBox)tab='Generali';
  let body=tab==='now'?drawNow():tab==='remote'?drawRemote():tab==='pc'?'<div id="v4pc"></div>':drawProvider(tab);
  root.innerHTML='<div class="v4head"><h1>'+T('Impostazioni','Settings')+'</h1>'+menus()+'</div>'+body;
  if(tab==='pc'&&pcBox){$('v4pc').appendChild(pcBox);page.dataset.cat='pc'}
  if(tab==='remote'){if(blocks)$('v4blocks').appendChild(blocks);if(swatches){swatches.classList.add('v4moved');$('v4accent').appendChild(swatches)}}
 }
 // ---------- menu a tendina ----------
 let pop=null;
 function closePop(){if(!pop)return;const p=pop;pop=null;p.box.classList.remove('show');p.shade.remove();setTimeout(()=>p.box.remove(),170);root.querySelectorAll('.v4menus button.open').forEach(b=>b.classList.remove('open'))}
 function openPop(anchor,items,onPick){
  closePop();
  const shade=document.createElement('div');shade.className='v4shade';shade.onclick=closePop;
  const box=document.createElement('div');box.className='v4pop';
  box.innerHTML=items.map((it,i)=>it==='-'?'<hr>':'<button data-i="'+i+'" class="'+(it.on?'sel':'')+'"><span>'+E(it.label)+'</span>'+(it.on?'<i>✓</i>':'')+'</button>').join('');
  document.body.append(shade,box);
  const r=anchor.getBoundingClientRect();
  box.style.minWidth=Math.min(Math.max(220,r.width),window.innerWidth-32)+'px';
  const bw=box.offsetWidth,bh=box.offsetHeight,menuAnchor=!!anchor.dataset.menu;
  let x=menuAnchor?Math.max(16,Math.min(r.left,window.innerWidth-16-bw)):Math.min(Math.max(16,r.right-bw),window.innerWidth-16-bw),y=r.bottom+6;
  if(y+bh>window.innerHeight-90)y=Math.max(16,r.top-6-bh);
  box.style.left=x+'px';box.style.top=y+'px';
  const sel=box.querySelector('.sel');if(sel)sel.scrollIntoView({block:'center'});
  box.onclick=e=>{const b=e.target.closest('button');if(!b)return;const it=items[+b.dataset.i];closePop();onPick(it)};
  pop={box,shade};requestAnimationFrame(()=>box.classList.add('show'));
 }
 // ---------- tocchi ----------
 const findSetting=(pid,key)=>{const p=provider(pid);let f=null;p&&(p.sections||[]).forEach(s=>s.settings.forEach(st=>{if(st.key===key)f=st}));return f};
 async function set(pid,key,value){
  if(busy)return;busy=true;
  try{const r=await api('/api/settings/set?provider='+encodeURIComponent(pid)+'&key='+encodeURIComponent(key)+'&value='+encodeURIComponent(value));
   if(r.status===401){location.replace('/');return}const res=await r.json().catch(()=>({}));
   if(res.reload){await load()}else{const st=findSetting(pid,key);if(st&&res.value!=null)st.value=res.value}
  }catch(_){}finally{busy=false;draw()}
 }
 const split=s=>{const i=s.indexOf('|');return [s.slice(0,i),s.slice(i+1)]};
 root.addEventListener('click',e=>{
  const b=e.target.closest('button');if(!b||!root.contains(b))return;
  if(b.dataset.menu){const m=b.dataset.menu;b.classList.add('open');
   const items=m==='player'?PLAYER.filter(id=>provider(id)||!data).map(id=>({id,label:name(id),on:tab===id}))
     .concat(['-'],EXTRA.filter(x=>x[0]!=='pc'||pcBox).map(x=>({id:x[0],label:x[1](),on:tab===x[0]})))
    :comps().map(p=>({id:p.id,label:p.title,on:tab===p.id}));
   openPop(b,items,it=>{choose(it.id);if(provider(it.id))load().then(draw)});return}
  if(b.dataset.group){group[tab]=b.dataset.group;draw();return}
  if(b.dataset.pick){const[pid,key]=split(b.dataset.pick),st=findSetting(pid,key);if(!st)return;
   openPop(b,(st.choices||[]).map(c=>({value:c.value,label:c.label,on:String(c.value).toLowerCase()===String(st.value).toLowerCase()})),it=>{st.value=it.value;draw();set(pid,key,it.value)});return}
  if(b.dataset.set){const[pid,key]=split(b.dataset.set);
   if(b.dataset.toggle){const on=!b.classList.contains('on');b.classList.toggle('on',on);set(pid,key,on?'1':'0')}else set(pid,key,b.dataset.v);return}
  if(b.dataset.proxyPick){const el=$(b.dataset.proxyPick);if(!el)return;
   openPop(b,[...el.options].map((o,i)=>({i,label:o.textContent,on:i===el.selectedIndex})),it=>{el.selectedIndex=it.i;el.dispatchEvent(new Event('change',{bubbles:true}));draw();setTimeout(draw,700)});return}
  if(b.dataset.proxyClick){const el=$(b.dataset.proxyClick);if(el)el.click();setTimeout(draw,120);setTimeout(draw,900);return}
  if(b.dataset.run==='sync'){if(typeof optOpen==='function')optOpen('sync');return}
  if(b.dataset.run==='extended'){cmd('fullscreen_extended');return}
  if(b.dataset.run==='reset'){if(typeof resetLayout==='function')resetLayout();setTimeout(draw,200);return}
 });
 window.addEventListener('scroll',()=>{if(pop)closePop()},{passive:true});
 const old=window.showPage;
 if(typeof old==='function')window.showPage=function(id){old.apply(this,arguments);if(id!=='settingsPage'){closePop();return}page.dataset.cat=tab==='pc'?'pc':'index';draw();load().then(draw)};
 // Valori che cambiano dal PC (renderer, HDR, film nuovo): si rinfrescano le schede senza menu aperti.
 setInterval(()=>{if(!pop&&page.classList.contains('active')&&['now','remote'].includes(tab))draw()},2500);
 draw();load().then(draw);
}catch(e){console.error(e)}})();
</script>
""";
}
