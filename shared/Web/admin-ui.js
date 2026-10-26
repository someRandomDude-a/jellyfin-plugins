/* Shared administrator presentation. Read-only, same-origin status checks; no authorization changes. */
(() => {
    'use strict';
    if (window.SuiteAdminUI) { window.SuiteAdminUI.mount(); return; }
    const style = document.createElement('style');
    style.textContent = `
    .suiteAdmin { --suite-line: color-mix(in srgb, currentColor 18%, transparent); --suite-surface: color-mix(in srgb, currentColor 4%, transparent); }
    .suiteAdmin .content-primary { max-width: 76rem; margin-inline: auto; padding-bottom: 3rem; }
    .suiteAdmin h1, .suiteAdmin .sectionTitle { letter-spacing: -.025em; }
    .suiteAdmin h2 { margin-block: 1.7rem 1rem; font-size: 1.3em; }
    .suiteAdmin h3 { font-size: 1.08em; }
    .suiteAdmin fieldset { min-width: 0; }
    .suiteAdmin legend { max-width: 100%; overflow-wrap: anywhere; }
    .suiteAdmin form, .suiteAdmin [data-notification-settings] > div > fieldset, .suiteAdmin .suiteCard { border: 1px solid var(--suite-line); border-radius: 1rem; padding: clamp(1rem, 3vw, 1.75rem); margin-block: 1rem; background: var(--suite-surface); min-width: 0; }
    .suiteAdmin form form { border: 0; padding: 0; background: none; }
    .suiteAdmin .inputContainer, .suiteAdmin .selectContainer { position: relative; margin-block: 1rem 1.5rem; }
    .suiteAdmin .inputLabel, .suiteAdmin .selectLabel { display: inline-block; width: auto; }
    .suiteAdmin input:not([type=checkbox]):not([type=radio]), .suiteAdmin select, .suiteAdmin textarea { box-sizing: border-box; max-width: 100%; border-radius: .45rem; }
    .suiteAdmin input:focus-visible, .suiteAdmin select:focus-visible, .suiteAdmin textarea:focus-visible, .suiteAdmin button:focus-visible, .suiteAdmin summary:focus-visible { outline: 2px solid var(--theme-primary-color, currentColor); outline-offset: 3px; }
    .suiteAdmin .checkboxContainer { display: block; height: auto; min-height: 1.8em; margin-block: 1rem; }
    .suiteAdmin .emby-checkbox-label { height: auto; min-height: 1.8em; white-space: normal; }
    .suiteAdmin .checkboxContainer .fieldDescription { margin-block: .5rem; }
    .suiteAdmin .hubGrid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 18rem), 1fr)); gap: .5rem 1.5rem; }
    .suiteAdmin button.raised { border-radius: .45rem; margin: .25rem; min-width: 0; padding: .55em .9em; font-size: .9em; }
    .suiteAdmin [role=status]:empty, .suiteAdmin [role=alert]:empty { display: none; }
    .suiteAdmin [role=status], .suiteAdmin [role=alert] { overflow-wrap: anywhere; }
    .suiteAdmin .hubTabs { display: flex; flex-wrap: wrap; gap: .4rem; border-bottom: 1px solid var(--suite-line); padding-bottom: .8rem; }
    .suiteAdmin .hubTabs button { border-radius: 2rem; }
    .suiteAdmin .hubTabs [aria-selected=true] { box-shadow: inset 0 -3px var(--theme-primary-color, currentColor); }
    .suiteAdmin table { width: 100%; border-collapse: collapse; }
    .suiteAdmin th { text-align: start; font-size: .9em; }
    .suiteAdmin td, .suiteAdmin th { padding: .8rem; border-bottom: 1px solid var(--suite-line); vertical-align: top; }
    .suiteAdmin tbody tr:hover { background: var(--suite-surface); }
    .suiteAdmin details.suiteCard > summary { cursor: pointer; font-weight: 600; padding-block: .3rem; }
    .suiteAdmin pre, .suiteAdmin textarea[data-json] { border: 1px solid var(--suite-line); border-radius: .65rem; padding: 1rem; background: var(--suite-surface); font: .85rem/1.65 ui-monospace, Consolas, monospace; white-space: pre; overflow: auto; max-height: 28rem; tab-size: 2; }
    .suiteAdmin pre code { font: inherit; color: inherit; }
    .suiteAdmin .suiteHelp { display: inline-block; vertical-align: middle; margin-inline-start: .45rem; position: relative; font-size: 1rem; font-weight: normal; letter-spacing: normal; }
    .suiteAdmin .suiteHelpButton { display: inline-grid; place-items: center; padding: 0; margin: 0; min-width: 0; width: 1.4rem; height: 1.4rem; border: 1px solid var(--suite-line); border-radius: 50%; background: transparent; color: inherit; font: inherit; cursor: help; }
    .suiteAdmin .suiteHelpText { position: absolute; z-index: 100; inset-inline-start: 0; inset-block-start: 100%; width: min(20rem, 65vw); box-sizing: border-box; padding: .85rem 1rem; border: 1px solid var(--suite-line); border-radius: .65rem; background: var(--theme-background-color, Canvas); color: var(--theme-text-color, CanvasText); box-shadow: 0 .4rem 1.5rem #0004; font-size: .9rem; line-height: 1.5; white-space: normal; overflow-wrap: anywhere; }
    .suiteAdmin .suiteCodeTools { display: flex; align-items: center; gap: .6rem; flex-wrap: wrap; font-size: .9em; }
    .suiteAdmin .suiteIntegrationBadges, .suiteAdmin .suiteJobBadges { display:flex; flex-wrap:wrap; gap:.55rem; margin-block:1rem; }
    .suiteAdmin .suiteIntegrationBadge, .suiteAdmin .suiteJobBadge { display:flex; align-items:center; gap:.65rem; border:1px solid var(--suite-line); border-radius:.8rem; background:var(--suite-surface); color:inherit; padding:.65rem .85rem; text-align:start; text-decoration:none; font:inherit; min-width:0; max-width:100%; cursor:pointer; }
    .suiteAdmin .suiteBadgeDot { width:.55rem; height:.55rem; border-radius:50%; background:currentColor; opacity:.45; flex:none; }
    .suiteAdmin [data-health=ok] .suiteBadgeDot { background:#35b879; opacity:1; }
    .suiteAdmin [data-health=bad] .suiteBadgeDot { background:#de6565; opacity:1; }
    .suiteAdmin .suiteBadgeTitle { display:block; font-size:.85rem; font-weight:600; overflow-wrap:anywhere; }
    .suiteAdmin .suiteBadgeMeta { display:block; font-size:.73rem; opacity:.7; margin-top:.2rem; overflow-wrap:anywhere; }
    .suiteAdmin .suiteBadgeDetail { border-inline-start:3px solid var(--theme-primary-color,currentColor); padding:.7rem 1rem; font-size:.9rem; line-height:1.6; background:var(--suite-surface); overflow-wrap:anywhere; }
    .suiteAdmin .suiteJsonTree { border:1px solid var(--suite-line); border-radius:.7rem; padding:.8rem 1rem; font:.85rem/1.7 ui-monospace,Consolas,monospace; overflow:auto; max-height:32rem; }
    .suiteAdmin .suiteJsonTree details { padding-inline-start:1rem; border-inline-start:1px solid var(--suite-line); margin-block:.2rem; }
    .suiteAdmin .suiteJsonTree summary { cursor:pointer; overflow-wrap:anywhere; }
    .suiteAdmin .suiteJsonLeaf { padding-inline-start:1rem; overflow-wrap:anywhere; white-space:pre-wrap; }
    .suiteAdmin .suiteJsonKey { color:color-mix(in srgb,var(--theme-primary-color,#40a9db) 65%,currentColor); }
    .suiteAdmin .suiteJsonString { color:color-mix(in srgb,#36b67e 40%,currentColor); }
    .suiteAdmin .suiteJsonNumber { color:color-mix(in srgb,#d2a052 45%,currentColor); }
    .suiteAdmin .suiteJsonLiteral { color:color-mix(in srgb,#aa80d6 45%,currentColor); }
    .suiteAdmin .suiteJsonTree button, .suiteAdmin .suiteCodeTools button { color:inherit; background:var(--suite-surface); border:1px solid var(--suite-line); border-radius:.4rem; padding:.35rem .6rem; cursor:pointer; }
    @media (max-width: 600px) { .suiteAdmin .content-primary { padding-inline: 1rem; } .suiteAdmin form { padding: 1rem; } .suiteAdmin .hubGrid { grid-template-columns: minmax(0, 1fr); } }
    @media (prefers-reduced-motion: reduce) { .suiteAdmin * { scroll-behavior: auto; } }
    `;
    document.head.append(style);
    let sequence = 0;
    function help(text, label = 'More information') {
        const wrap = document.createElement('span'), button = document.createElement('button'), tip = document.createElement('span');
        wrap.className = 'suiteHelp'; button.setAttribute('is', 'emby-button'); button.className = 'emby-button button-flat suiteHelpButton'; button.type = 'button'; button.textContent = '?'; button.setAttribute('aria-label', label);
        tip.className = 'suiteHelpText'; tip.id = 'suite-help-' + (++sequence); tip.role = 'tooltip'; tip.textContent = text; tip.hidden = true;
        button.setAttribute('aria-describedby', tip.id); button.setAttribute('aria-expanded', 'false');
        const show = () => {
            tip.hidden = false; button.setAttribute('aria-expanded', 'true'); tip.style.transform = '';
            const box = tip.getBoundingClientRect(), left = Math.max(8, Math.min(box.left, window.innerWidth - box.width - 8));
            tip.style.transform = 'translateX(' + (left - box.left) + 'px)';
        };
        const hide = () => { tip.hidden = true; button.setAttribute('aria-expanded', 'false'); };
        button.onclick = show; wrap.onmouseenter = show; wrap.onmouseleave = () => { if (!wrap.contains(document.activeElement)) hide(); };
        button.onfocus = show; wrap.onfocusout = event => { if (!wrap.contains(event.relatedTarget)) hide(); };
        wrap.onkeydown = event => { if (event.key === 'Escape') { hide(); event.stopPropagation(); } };
        wrap.append(button, tip); return wrap;
    }
    const normal = value => Array.isArray(value) ? value.map(normal) : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([key,item]) => [key[0].toLowerCase()+key.slice(1),normal(item)])) : value;
    const catalog = [
        ['account-hub','Account Hub','bc9c3d3e-6da3-4ee5-91cd-c429d58ad901'], ['notifications','Notifications','c18d78ec-4b70-47cf-a0d2-936d987c37ba'],
        ['discord','Discord Bridge','4b047046-a550-4415-b204-4206f1f18201'], ['private-syncplay','Private SyncPlay','09514a9e-f7a0-4c5a-91bc-3a46338298ca'],
        ['deletion-queue','Deletion Queue','3de88ba1-6d5c-49f5-854b-65a72f473f21'], ['automatic','Automatic Deletion Requests','a6429fb2-c67c-459d-b6a7-579c4a05dace']
    ];
    const integrations = new WeakMap();
    function renderBadges(root) {
        const state = integrations.get(root); if (!state?.host || !root.isConnected) return;
        state.host.replaceChildren();
        for (const [id,name,guid] of [...catalog,...state.extra]) {
            const candidates = (state.plugins || []).filter(p => guid && p.id?.replaceAll('-','').toLowerCase() === guid.replaceAll('-','').toLowerCase()).sort((a,b)=>String(b.version).localeCompare(String(a.version),undefined,{numeric:true}));
            const plugin = candidates.find(p=>p.status==='Active') || candidates[0];
            const update = state.overrides.get(id), loaded = plugin?.status === 'Active';
            const status = update?.state || (state.plugins ? plugin ? loaded ? 'Loaded' : String(plugin.status || 'Unavailable') : 'Not installed' : 'Not verified');
            const version = update?.version || plugin?.version;
            const button = document.createElement('button'); button.type='button'; button.className='suiteIntegrationBadge'; button.dataset.plugin=id;
            button.dataset.health = update?.working === false ? 'bad' : update?.working === true || loaded ? 'ok' : 'unknown';
            const dot=document.createElement('span');dot.className='suiteBadgeDot';dot.setAttribute('aria-hidden','true');
            const labels=document.createElement('span'), title=document.createElement('span'), meta=document.createElement('span');title.className='suiteBadgeTitle';meta.className='suiteBadgeMeta';
            title.textContent=name;meta.textContent=status+' · '+(version ? 'v'+version : plugin ? 'Version unknown' : '—'); labels.append(title,meta);button.append(dot,labels);
            button.setAttribute('aria-expanded','false');button.onclick=()=>{
                const opened=state.detail.dataset.plugin===id && !state.detail.hidden;
                for(const b of state.host.querySelectorAll('button')) b.setAttribute('aria-expanded','false');
                state.detail.hidden=opened;state.detail.dataset.plugin=id;button.setAttribute('aria-expanded',String(!opened));
                state.detail.textContent=name+': '+(update?.detail || (plugin ? loaded ? 'Plugin is loaded. This does not by itself verify credentials, external connectivity or every dependent capability.' : 'Enable a compatible version in Dashboard → Plugins and restart Jellyfin.' : 'Not installed. Only features requiring this plugin are unavailable.'));
            };state.host.append(button);
        }
    }
    function integration(root,id,update) {
        let state=integrations.get(root);if(!state){state={overrides:new Map(),extra:[]};integrations.set(root,state);}
        if(!catalog.some(row=>row[0]===id)&&!state.extra.some(row=>row[0]===id))state.extra.push([id,update.name||id,({enhanced:'f69e946a-4b3c-4e9a-8f0a-8d7c1b2c4d9b',security:'94879a0c-da24-4eb1-aa06-f28b4b9333b1'})[id]||null]);
        state.overrides.set(id,{...state.overrides.get(id),...update});renderBadges(root);
    }
    async function jsonGet(path,signal) {
        const response=await fetch(ApiClient.getUrl(path),{cache:'no-store',credentials:'omit',signal,headers:{Authorization:'MediaBrowser Token="'+encodeURIComponent(ApiClient.accessToken())+'"'}});
        if(!response.ok)throw Error('Status unavailable');return normal(await response.json());
    }
    function statusWidgets(root) {
        if(!root.dataset.suitePlugin)return ()=>{};
        let state=integrations.get(root);if(!state){state={overrides:new Map(),extra:[]};integrations.set(root,state);}
        let host=root.querySelector('[data-suite-integrations]');
        if(!host){host=document.createElement('div');host.dataset.suiteIntegrations='';const heading=root.querySelector('h1,h2');heading?.insertAdjacentElement('afterend',host);}
        host.classList.add('suiteIntegrationBadges');host.setAttribute('aria-label','Plugin integration status');state.host=host;
        const detail=document.createElement('div');detail.className='suiteBadgeDetail';detail.hidden=true;host.insertAdjacentElement('afterend',detail);state.detail=detail;
        let controller,epoch=0;
        const jobs=[['AutomaticDeletionRequests.RuleMaintenance','Rules & collections'],['DeletionQueue.RemovalMaintenance','Safety & deletions'],['DeletionQueue.OverlayGeneration','Artwork overlays']];
        async function refresh(){
            controller?.abort();controller=new AbortController();const current=++epoch,signal=controller.signal;renderBadges(root);
            if(typeof ApiClient==='undefined'||typeof fetch!=='function')return;
            try {const plugins=await jsonGet('Plugins',signal);if(current!==epoch)return;state.plugins=plugins;renderBadges(root);}catch{if(current===epoch){state.plugins=undefined;renderBadges(root);}}
            if(current!==epoch)return;
            const route={'account-hub':'AccountHub/api/v1/dependencies','private-syncplay':'PrivateSyncPlay/admin/dependencies','deletion-queue':'DeletionQueue/admin/dependencies'}[root.dataset.suitePlugin];
            if(route)try{const result=await jsonGet(route,signal);if(current!==epoch)return;for(const item of (result.data||result).items||[])if(!(root.dataset.suitePlugin==='deletion-queue'&&item.id==='notifications'))integration(root,item.id,{name:item.name,working:item.available,state:item.available?'Available':'Unavailable',detail:item.reason+' '+(item.available?'':item.fix)});}catch{/* Native loaded status remains distinct from an unverified integration. */}
            if(current!==epoch)return;
            const ownRoute={notifications:'Notifications/api/v1/settings/notifications',automatic:'AutomaticDeletionRequests/admin',discord:'DiscordBridge/admin/v1/settings'}[root.dataset.suitePlugin];
            if(ownRoute)try{const response=await jsonGet(ownRoute,signal);if(current!==epoch)return;const data=response.data||response;
                if(root.dataset.suitePlugin==='discord'){integration(root,'discord',{working:!!data.connected,state:data.connected?'Connected':String(data.state||'Not connected'),detail:'Bot gateway status. Saved-key authentication and external delivery are tested only when you request them.'});integration(root,'security',{name:'Jellyfin Security',working:data.security==='ok',state:data.security==='ok'?'Available':String(data.security||'Not verified'),detail:'Native Security integration status; 2FA authorization is never bypassed to repair dependencies.'});}
                else integration(root,root.dataset.suitePlugin,{working:true,state:root.dataset.suitePlugin==='automatic'&&!data.configuration?.enabled?'Rules paused':'Working',detail:'Administrator configuration and plugin-owned state are responding. External connection tests remain explicit.'});
                if(root.dataset.suitePlugin==='automatic')integration(root,'deletion-queue',{working:!!data.queueAvailable,state:data.queueAvailable?'Available':'Unavailable',detail:data.queueAvailable?'Automatic submission provider is available. Each request still requires current authorization and Notifications.':'Deletion Queue automation provider is unavailable. New requests and live rule progression cannot be authorized.'});
            }catch{if(current===epoch)integration(root,root.dataset.suitePlugin,{working:false,state:'Not verified',detail:'Current administrator/storage health could not be verified. Refresh your session and check plugin diagnostics.'});}
            if(current!==epoch||!root.querySelector('[data-suite-jobs]'))return;
            try {const tasks=await jsonGet('ScheduledTasks',signal);if(current!==epoch)return;
                for(const place of root.querySelectorAll('[data-suite-jobs]')){place.classList.add('suiteJobBadges');place.replaceChildren();
                    for(const [key,name]of jobs){const task=tasks.find(t=>t.key===key),link=document.createElement('a');link.className='suiteJobBadge';link.href='#/dashboard/tasks'+(task?'/'+encodeURIComponent(task.id):'');
                        const labels=document.createElement('span'),title=document.createElement('span'),cycle=document.createElement('span');title.className='suiteBadgeTitle';cycle.className='suiteBadgeMeta';title.textContent=name;
                        cycle.textContent=task?(task.triggers?.map(t=>{const time=new Intl.DateTimeFormat(undefined,{hour:'numeric',minute:'2-digit',timeZone:'UTC'}).format(new Date(Number(t.timeOfDayTicks||0)/10000));return t.type==='DailyTrigger'?'Daily · '+time:t.type==='WeeklyTrigger'?'Weekly · '+t.dayOfWeek+' · '+time:t.type==='IntervalTrigger'?'Every '+Number((Number(t.intervalTicks)/36000000000).toFixed(2))+' hours':'On startup';}).join(' / ')||'Manual only'):'Task not installed';
                        labels.append(title,cycle);link.append(labels);place.append(link);
                    }
                }
            }catch{if(current===epoch)for(const place of root.querySelectorAll('[data-suite-jobs]'))place.textContent='Scheduled-task status unavailable. Open Dashboard → Scheduled Tasks.';}
        }
        const stop=()=>{epoch++;controller?.abort();detail.hidden=true;};root.addEventListener('viewshow',refresh);root.addEventListener('viewhide',stop);root.addEventListener('viewdestroy',stop);root.addEventListener('suite-status-refresh',refresh);refresh();return stop;
    }
    function highlight(code,text){
        code.replaceChildren();const tokens=/"(?:\\.|[^"\\])*"(?=\s*:)|"(?:\\.|[^"\\])*"|\b(?:true|false|null)\b|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g;let at=0;
        for(const match of text.matchAll(tokens)){code.append(document.createTextNode(text.slice(at,match.index)));const span=document.createElement('span');span.className=match[0][0]==='"'?(text.slice(match.index+match[0].length).trimStart().startsWith(':')?'suiteJsonKey':'suiteJsonString'):/^[\d-]/.test(match[0])?'suiteJsonNumber':'suiteJsonLiteral';span.textContent=match[0];code.append(span);at=match.index+match[0].length;}
        code.append(document.createTextNode(text.slice(at)));
    }
    function jsonTree(host,value){
        host.replaceChildren();
        function entry(parent,key,item,depth){
            if(item===null||typeof item!=='object'){const line=document.createElement('div');line.className='suiteJsonLeaf';const k=document.createElement('span');k.className='suiteJsonKey';k.textContent=key+': ';const v=document.createElement('span');v.className=typeof item==='string'?'suiteJsonString':typeof item==='number'?'suiteJsonNumber':'suiteJsonLiteral';v.textContent=JSON.stringify(item);line.append(k,v);parent.append(line);return;}
            const details=document.createElement('details'),summary=document.createElement('summary');const entries=Object.entries(item);const kind=item.Kind||item.kind,at=item.At||item.at;
            summary.textContent=key+(kind?' · '+kind+(at?' · '+String(at).slice(0,19).replace('T',' '):''):'')+' · '+entries.length+(Array.isArray(item)?' items':' fields');details.append(summary);parent.append(details);let rendered=false;
            details.addEventListener('toggle',()=>{if(!details.isConnected||!details.open||rendered)return;rendered=true;if(depth>=12){const raw=document.createElement('pre');raw.dataset.suiteJsonSkip='true';raw.textContent=JSON.stringify(item,null,2);details.append(raw);return;}
                let offset=0;const more=document.createElement('button');more.type='button';more.textContent='Show next 100';const next=()=>{more.remove();for(const [child,val]of entries.slice(offset,offset+100))entry(details,child,val,depth+1);offset+=100;if(offset<entries.length)details.append(more);};more.onclick=next;next();});
            if(depth===0)details.open=true;
        }
        entry(host,Array.isArray(value)?'Entries':'Object',value,0);
    }
    const mounted = new WeakSet();
    function mount() {
        for (const root of document.querySelectorAll('.suiteAdmin')) {
            if (mounted.has(root)) continue; mounted.add(root);
            statusWidgets(root);
            const viewers=new WeakMap(),parts=new Set(),editPreviews=new Set();
            function showJson(pre){
                if(pre.dataset.suiteJsonSkip)return;
                const text=pre.textContent;if(viewers.get(pre)?.text===text)return;
                let data,pretty;try{if(!text.trim()||text.length>2*1024*1024)throw Error();data=JSON.parse(text);pretty=JSON.stringify(data,null,2);}catch{
                    const previous=viewers.get(pre);if(previous){previous.tools.remove();previous.tree.remove();parts.delete(previous);viewers.delete(pre);}pre.hidden=false;
                    if(pre.firstElementChild?.tagName!=='CODE'&&text.trim()){const code=document.createElement('code');code.textContent=text;pre.replaceChildren(code);}return;
                }
                let state=viewers.get(pre);
                if(!state){const tools=document.createElement('div'),tree=document.createElement('div');tools.className='suiteCodeTools';tree.className='suiteJsonTree';pre.before(tools,tree);state={pre,tools,tree,mode:'tree'};viewers.set(pre,state);parts.add(state);}
                state.text=pretty;const code=document.createElement('code');code.className='language-json';if(pretty.length<=200000)highlight(code,pretty);else code.textContent=pretty;pre.replaceChildren(code);pre.tabIndex=0;pre.setAttribute('aria-label',pre.getAttribute('aria-label')||'JSON code');
                state.tools.replaceChildren();const treeButton=document.createElement('button'),codeButton=document.createElement('button'),copy=document.createElement('button');
                for(const [b,label]of [[treeButton,'Browse blocks'],[codeButton,'JSON code'],[copy,'Copy JSON']]){b.type='button';b.textContent=label;state.tools.append(b);}
                const change=mode=>{state.mode=mode;pre.hidden=mode!=='code';state.tree.hidden=mode!=='tree';treeButton.setAttribute('aria-pressed',String(mode==='tree'));codeButton.setAttribute('aria-pressed',String(mode==='code'));};
                treeButton.onclick=()=>change('tree');codeButton.onclick=()=>change('code');copy.onclick=async()=>{try{await navigator.clipboard.writeText(state.text);copy.textContent='Copied';}catch{change('code');const range=document.createRange();range.selectNodeContents(code);const selection=window.getSelection();selection.removeAllRanges();selection.addRange(range);copy.textContent='Select and copy JSON';}};
                jsonTree(state.tree,data);change(state.mode);
            }
            function enhance() {
                for(const state of parts)if(!root.contains(state.pre)){state.tools.remove();state.tree.remove();parts.delete(state);}
                for (const node of root.querySelectorAll('[data-help]')) {
                    const text = node.getAttribute('data-help'); node.removeAttribute('data-help');
                    const label = node.querySelector('label, legend') || node;
                    // Keep help buttons outside labels so they never toggle a checkbox.
                    if (node.matches('input, select, textarea')) (node.closest('label') || node).insertAdjacentElement('afterend', help(text));
                    else if (label !== node) label.insertAdjacentElement('afterend', help(text, 'About ' + label.textContent.trim()));
                    else node.append(help(text));
                }
                for (const node of root.querySelectorAll('[data-help-for]')) {
                    const target = root.querySelector(node.getAttribute('data-help-for'));
                    if (!target) continue;
                    const anchor = target.closest('.inputContainer, .selectContainer, .checkboxContainer') || target;
                    const info = help(node.textContent, 'About ' + (target.labels?.[0]?.textContent.trim() || target.textContent.trim() || 'this setting'));
                    if (anchor.matches('button, label, input, select, textarea')) anchor.insertAdjacentElement('afterend', info); else anchor.append(info);
                    node.remove();
                }
                for (const pre of root.querySelectorAll('pre')) showJson(pre);
                for (const editor of root.querySelectorAll('textarea[data-json]:not([data-code-ready])')) {
                    editor.dataset.codeReady = 'true'; editor.spellcheck = false;
                    const tools = document.createElement('div'), format = document.createElement('button'), result = document.createElement('span');
                    tools.className = 'suiteCodeTools'; format.type = 'button'; format.className = 'emby-button raised'; format.textContent = 'Format JSON'; result.setAttribute('role', 'status');
                    format.onclick = () => { try { editor.value = JSON.stringify(JSON.parse(editor.value), null, 2); editor.dispatchEvent(new Event('input', { bubbles: true })); result.textContent = 'Valid JSON'; } catch { result.textContent = 'Invalid JSON. Check quotes, commas and brackets.'; editor.focus(); } };
                    tools.append(format, result); editor.insertAdjacentElement('afterend', tools);
                    const preview=document.createElement('details'),summary=document.createElement('summary'),pre=document.createElement('pre');summary.textContent='Browse / highlight JSON draft';preview.append(summary,pre);tools.after(preview);editPreviews.add(preview);
                    const update=()=>{if(preview.open){try{pre.textContent=JSON.stringify(JSON.parse(editor.value),null,2);}catch{pre.textContent='Invalid JSON draft. Formatting and browsing are available once its syntax is valid.';}}};
                    editor.addEventListener('input',update);preview.addEventListener('toggle',update);
                }
            }
            const observer = new MutationObserver(enhance);
            const start = () => { enhance(); observer.observe(root, { childList: true, subtree: true }); };
            const stop = () => { observer.disconnect(); for(const state of parts){state.tools.remove();state.tree.remove();state.pre.hidden=false;viewers.delete(state.pre);}parts.clear();for(const preview of editPreviews){preview.open=false;preview.querySelector('pre').replaceChildren();}for (const tip of root.querySelectorAll('.suiteHelpText')) tip.hidden = true; for (const button of root.querySelectorAll('.suiteHelpButton')) button.setAttribute('aria-expanded', 'false'); };
            root.addEventListener('viewshow', start); root.addEventListener('viewhide', stop); root.addEventListener('viewdestroy', stop); start();
        }
    }
    window.SuiteAdminUI = { mount, help, integration }; mount();
})();
