(() => {
    'use strict'; const page = document.getElementById('automaticDeletionAdmin'); if (!page || page.dataset.bound) return; page.dataset.bound = 'true';
    const el = id => page.querySelector('#' + id); let revision = null, active = true, epoch = 0, rules = [], savedNames = new Map(), editors = [], filters=null, selected=null;
    const defaultPredicate=()=>({id:crypto.randomUUID(),name:'Library conditions',enabled:false,match:'all',conditions:[{field:'title',operator:'contains',value:''}]});
    let view={zoom:1,panX:0,panY:0};
    function migrateFilters(){if(filters!==null)return;filters=rules.map((r,i)=>({id:crypto.randomUUID(),name:'Library · '+r.name,description:'',libraryId:r.libraryId||el('arLibrary').value,predicate:defaultPredicate(),outputs:[r.id],x:40,y:40+i*160}));}
    const fields = { title: 'Jellyfin · Title', year: 'Jellyfin · Release year', added: 'Jellyfin · Date added', rating: 'Jellyfin · Rating', kind: 'Jellyfin · Media kind', 'seerr.ownRequest': 'Seerr · Owned by request actor', 'seerr.is4k': 'Seerr · 4K edition', 'seerr.requestOnly': 'Seerr · Unfulfilled request' };
    const node = (tag, text) => { const e = document.createElement(tag); if (text !== undefined) e.textContent = text; return e; };
    const normalize = value => Array.isArray(value) ? value.map(normalize) : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([key, item]) => [key[0].toLowerCase() + key.slice(1), key.toLowerCase()==='trace' ? item : key.toLowerCase()==='sources'&&item ? Object.fromEntries(Object.entries(item).map(([id,position])=>[id,normalize(position)])) : normalize(item)])) : value;
    const button = (label, action) => { const holder = node('div'); holder.innerHTML = '<button is="emby-button" type="button" class="raised"></button>'; const b = holder.firstElementChild; b.textContent = label; b.addEventListener('click', action); return b; };
    const operators = field => field.startsWith('seerr.') ? ['equals', 'not-equals'] : field === 'added' ? ['older-days', 'newer-days'] : ['title', 'kind'].includes(field) ? ['equals', 'not-equals', 'contains'] : ['equals', 'not-equals', 'greater', 'less'];
    function field(host, label, type, value, options) {
        const row = node('div'); row.className = options ? 'selectContainer' : type === 'checkbox' ? 'checkboxContainer' : 'inputContainer';
        row.innerHTML = type === 'checkbox' ? '<label><input is="emby-checkbox" type="checkbox"><span data-caption></span></label>' : options ? '<label></label><select is="emby-select"></select>' : '<label></label><input is="emby-input">';
        const input = row.querySelector('input,select'), caption = row.querySelector('label'); input.id = 'ar-' + crypto.randomUUID(); caption.htmlFor = input.id;
        if (type === 'checkbox') { input.checked = !!value; row.querySelector('[data-caption]').textContent = label; }
        else { caption.textContent = label; if (options) for (const [id, title] of options) { const option = node('option', title); option.value = id; input.append(option); } else input.type = type; input.value = value ?? ''; }
        // The shim uses these classes as its initialized marker. Do not add
        // emby-input/select/checkbox before its constructor has run.
        host.append(row); return input;
    }
    function render() {
        editors.forEach(e=>e.dispose());editors=[];
        const cards = el('arRuleCards'); cards.replaceChildren();
        migrateFilters();
        const choose=field(cards,'Select card','','',[['','Canvas overview'],...filters.map(f=>['filter:'+f.id,'Library filter · '+f.name]),...rules.map(r=>['rule:'+r.id,'Rule · '+r.name])]);choose.value=selected?selected.kind+':'+selected.id:'';
        choose.onchange=()=>{const split=choose.value.indexOf(':');selected=split<0?null:{kind:choose.value.slice(0,split),id:choose.value.slice(split+1)};render();};
        if(!selected){if(!rules.length&&!filters.length)cards.append(node('p','No cards. Create a library filter or rule.'));editors.push(window.RemovalRouting.mount(cards,filters,rules,(kind,id)=>{selected={kind,id};render();},sync,view));sync();return;}
        const filter=selected.kind==='filter'?filters.find(f=>f.id===selected.id):null;
        const list=filter?[filter.predicate]:rules.filter(r=>r.id===selected.id);
        for (const rule of list) {
            const index=rules.indexOf(rule), metadata=filter||rule;
            const card = node('fieldset'), legend = node('legend', 'Rule ' + (index + 1)); card.className = 'ar-rule'; card.append(legend);
            legend.textContent=filter?'Library filter':'Rule';
            const header = node('div'); header.className = 'ar-grid'; card.append(header);
            const type=field(header,'Type (fixed)','text',filter?'Library filter':'Rule');type.readOnly=true;
            const name = field(header, 'Title', 'text', metadata.name); name.maxLength = 100; name.required = true; name.oninput = () => { metadata.name = name.value; sync(); };
            const description=field(header,'Description','text',metadata.description||'');description.maxLength=1000;description.oninput=()=>{metadata.description=description.value;sync();};
            const incoming=filter?[]:filters.filter(f=>f.outputs.includes(rule.id));
            const library = field(header,filter?'Filter items by library':'Test library','',filter?.libraryId||incoming[0]?.libraryId||el('arLibrary').value,[...el('arLibrary').options].map(o=>[o.value,o.textContent])); library.onchange=()=>{if(filter){filter.libraryId=library.value;sync();}};
            if(filter){const routes=node('fieldset');routes.append(node('legend','Output nodes · attached rules'));for(const target of rules){const checked=field(routes,target.name,'checkbox',filter.outputs.includes(target.id));checked.dataset.routeCheckbox=target.id;checked.onchange=()=>{filter.outputs=checked.checked?[...new Set([...filter.outputs,target.id])]:filter.outputs.filter(id=>id!==target.id);sync();};}card.append(routes);}
            else {const enabled = field(card, 'Enable this rule', 'checkbox', rule.enabled); enabled.onchange = () => { rule.enabled = enabled.checked; sync(); };}
            cards.append(card);
            let testSource=null;
            const editor=window.RemovalCircuit.mount(card,rule,sync,{sources:incoming,filterMode:!!filter,targets:rules,route:filter,onSource:source=>{testSource=source;library.value=source.libraryId;media.replaceChildren();el('arStatus').textContent='Testing input path: '+source.name+'. Only items passing this filter can be selected.';findMedia();}});editors.push(editor);
            const tests=node('div');tests.className='ar-test';card.append(tests);
            const search=field(tests,'Find media in this library','search',''), media=field(tests,'Test media','','',[]);
            let searchEpoch=0;
            async function findMedia(){const current=++searchEpoch;testButton.disabled=true;try{const response=testSource?await call('POST','/media/testable',{filter:testSource,search:search.value}):await call('GET','/media?libraryId='+encodeURIComponent(library.value||el('arLibrary').value)+'&search='+encodeURIComponent(search.value));if(current!==searchEpoch||!media.isConnected)return;const found=testSource?response.items:response;media.replaceChildren();for(const item of found){const option=node('option',item.title+(item.year?' ('+item.year+')':''));option.value=item.id;media.append(option);}testButton.disabled=!found.length||(!filter&&!testSource);if(!found.length)el('arStatus').textContent='No media currently passes this input path. Refine the search or check unavailable evidence.';}catch(e){fail(e);}}
            tests.append(button('Find media',findMedia));
            const testButton=button('Test draft circuit',async()=>{try{if(!filter&&!testSource)return;const result=await call('POST','/test',{rule,itemId:media.value,libraryId:testSource?.libraryId||library.value||el('arLibrary').value,filters:filter?undefined:testSource?[testSource]:incoming});if(!media.isConnected)return;editor.trace(result.trace);el('arStatus').textContent=result.title+': '+(Object.values(result.trace).some(v=>v===null)?'Unknown evidence — blocked':result.matches?(filter?'True — passes filter':'True — would request removal'):'False — no request')+'. Test only; no changes made.';}catch(e){fail(e);}});testButton.disabled=true;tests.append(testButton);
            card.append(button('Remove card', () => {if(!confirm('Remove this card and its connections?'))return;if(filter)filters=filters.filter(f=>f.id!==filter.id);else{rules.splice(index,1);for(const f of filters)f.outputs=f.outputs.filter(id=>id!==rule.id);}selected=null;render();}));
        }
        sync();
    }
    function sync() { const filter=selected?.kind==='filter'?filters?.find(f=>f.id===selected.id):null;if(filter)for(const box of page.querySelectorAll('[data-route-checkbox]'))box.checked=filter.outputs.includes(box.dataset.routeCheckbox);el('arRules').value = JSON.stringify({rules,filters,view}, null, 2); }
    async function call(method, suffix = '', body) { const generation = epoch;if(method==='PUT'&&body?.configuration)body.configuration.view=view; const response = await ApiClient.ajax({ url: ApiClient.getUrl('AutomaticDeletionRequests/admin' + suffix), type: method, dataType: 'json', contentType: 'application/json', data: body ? JSON.stringify(body) : undefined }); if (!active || generation !== epoch) throw new DOMException('View closed', 'AbortError');const result=normalize(response);if(method==='GET'&&!suffix)view=result.configuration.view||{zoom:1,panX:0,panY:0};return result; }
    const fail = error => { if (active && error?.name !== 'AbortError') el('arStatus').textContent = 'Operation unavailable. Review your administrator session, revision and rule values.'; };
    function choices(id, values, saved, placeholder) { const input = el(id); if (saved?.replaceAll('-', '') === '0'.repeat(32)) saved = ''; input.replaceChildren(); for (const item of [{ id: '', name: placeholder }, ...values]) { const option = node('option', item.name); option.value = item.id; input.append(option); } if (saved && ![...input.options].some(o => o.value === saved)) { const option = node('option', 'Saved selection (currently unavailable)'); option.value = saved; input.append(option); } input.value = saved || ''; }
    async function load() { const generation = epoch, form = el('arForm'); form.inert = true; form.setAttribute('aria-busy', 'true'); try { const result = await call('GET'), c = result.configuration; revision = c.revision; choices('arActor', result.actors || [], c.actorId, 'Choose an administrator'); choices('arLibrary', result.libraries || [], c.libraryId, 'Choose a library'); el('arHours').value = c.intervalHours; el('arEnabled').checked = c.enabled; rules = c.rules;filters=c.filters??null;selected=null; savedNames = new Map(rules.map(r => [r.id, r.name])); render(); el('arStatus').textContent = result.queueAvailable ? result.status : 'Deletion Queue is missing. Request creation is unavailable.'; form.inert = false; } finally { if (active && generation === epoch) form.setAttribute('aria-busy', 'false'); } }
    const dialog=el('arNewCard');
    el('arAddRule').onclick=()=>dialog.showModal();
    el('arNewCancel').onclick=()=>dialog.close();
    el('arNewCreate').onclick=()=>{
        const title=el('arNewTitle');if(!title.reportValidity())return;
        const type=el('arNewType').value;const card={id:crypto.randomUUID(),name:title.value.trim(),description:el('arNewDescription').value};if(!card.name)return;
        if(type==='filter'){if(filters.length>=50)return;filters.push({...card,libraryId:el('arLibrary').value,predicate:defaultPredicate(),outputs:[],x:40,y:40+filters.length*160});}
        else{if(rules.length>=50)return;rules.push({...card,enabled:false,match:'all',conditions:[{field:'added',operator:'older-days',value:30}],canvasX:540,canvasY:40+rules.length*160});}
        selected={kind:type,id:card.id};dialog.close();title.value='';el('arNewDescription').value='';render();
    };
    el('arImport').addEventListener('click', () => { try {
        const draft=JSON.parse(el('arRules').value), imported=Array.isArray(draft)?draft:draft.rules;
        const validRule=r=>r&&typeof r.id==='string'&&/^[a-zA-Z0-9_-]{1,100}$/.test(r.id)&&typeof r.name==='string'&&r.name.length>0&&r.name.length<=100&&Array.isArray(r.conditions)&&r.conditions.length>=1&&r.conditions.length<=50&&r.conditions.every(c=>c&&fields[c.field]&&operators(c.field).includes(c.operator));
        if(!Array.isArray(imported)||imported.length>50||!imported.every(validRule)||new Set(imported.map(r=>r.id)).size!==imported.length)throw Error();
        const importedFilters=Array.isArray(draft)?null:draft.filters??null;
        if(importedFilters!==null&&(!Array.isArray(importedFilters)||importedFilters.length>50||importedFilters.some(f=>!f||typeof f.id!=='string'||!Array.isArray(f.outputs)||f.outputs.some(id=>!imported.some(r=>r.id===id))||!validRule(f.predicate))))throw Error();
        for(const r of [...imported,...(importedFilters||[]).map(f=>f.predicate)])if(r.circuit)window.RemovalCircuit.check(r.circuit);
        const importedView=draft.view||{zoom:1,panX:0,panY:0};
        if(![importedView.zoom,importedView.panX,importedView.panY].every(Number.isFinite)||importedView.zoom<.25||importedView.zoom>2||Math.abs(importedView.panX)>10000||Math.abs(importedView.panY)>10000)throw Error();
        rules=imported;filters=importedFilters;view=importedView;selected=null;render();
    }catch{fail();} });
    el('arExport').onclick=()=>{sync();const url=URL.createObjectURL(new Blob([el('arRules').value],{type:'application/json'}));const a=node('a');a.href=url;a.download='removal-automation-canvas.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
    el('arForm').addEventListener('submit', async event => { event.preventDefault(); if (revision === null) return; try { const enabled = el('arEnabled').checked, empty = '00000000-0000-0000-0000-000000000000'; if (enabled && (!el('arActor').value || filters.some(f=>!f.libraryId))) { el('arStatus').textContent = 'Choose a request owner and a library for each filter before enabling automation.'; return; } if (enabled && !confirm('Enable creation of removal requests for matching items? Every request still requires Deletion Queue safety gates.')) return; await call('PUT', '', { confirmAutomation: enabled, configuration: { revision, enabled, actorId: el('arActor').value || empty, libraryId: el('arLibrary').value || empty, intervalHours: Number(el('arHours').value), rules,filters } }); await load(); } catch (error) { fail(error); } });
    el('arPreview').addEventListener('click', async () => { try {
        const result = await call('POST', '/preview'), matches = result.matches || [], host = el('arResult'); host.replaceChildren();
        host.append(node('h2', matches.length ? matches.length + ' saved-rule matches' : 'No matches this time.'), node('p', 'Preview only. No requests, votes or deletions were created. Save draft changes before previewing them.'));
        const grid = node('div'); grid.className = 'ar-match-grid'; host.append(grid);
        for (const match of matches.slice(0, 200)) { const card = node('article'); card.className = 'ar-match-card'; card.append(node('h3', match.title), node('p', savedNames.get(match.ruleId) || 'Saved rule'), node('p', match.alreadySubmitted ? 'Already submitted · no duplicate request' : 'Would request removal · all safety gates still apply')); grid.append(card); }
        if (matches.length > 200) host.append(node('p', 'Showing the first 200 matches. Narrow your saved rules for a smaller preview.'));
    } catch (error) { fail(error); } });
    page.addEventListener('viewhide', () => { active = false; epoch++; revision = null;dialog.close();el('arNewTitle').value='';el('arNewDescription').value=''; editors.forEach(e=>e.dispose());editors=[];rules = [];filters=null;selected=null; savedNames.clear(); el('arRules').value = ''; el('arResult').textContent = ''; el('arRuleCards').replaceChildren(); });
    page.addEventListener('viewshow', () => { if (!active) { active = true; load().catch(fail); } }); load().catch(fail);
})();
