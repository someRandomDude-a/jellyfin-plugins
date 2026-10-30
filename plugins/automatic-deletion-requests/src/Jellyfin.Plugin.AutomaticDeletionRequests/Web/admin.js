(() => {
    'use strict'; const page = document.getElementById('automaticDeletionAdmin'); if (!page || page.dataset.bound) return; page.dataset.bound = 'true';
    const el = id => page.querySelector('#' + id); let revision = null, active = true, epoch = 0, rules = [], savedNames = new Map(), editors = [], filters=null, selected=null;
    const defaultPredicate=()=>({id:crypto.randomUUID(),name:'Library conditions',enabled:false,match:'all',conditions:[{field:'title',operator:'contains',value:''}]});
    let view={zoom:1,panX:0,panY:0};
    let nativeLibraries=[];
    let legacyLibrary='';
    const firstLibrary=()=>nativeLibraries[0]?.id||'';
    function migrateFilters(){
        if(filters===null){filters=[];for(const r of rules){const libraryId=r.libraryId||legacyLibrary||firstLibrary();let filter=filters.find(f=>f.libraryId===libraryId);if(!filter){filter={id:'library-'+libraryId.replaceAll('-','').toLowerCase(),name:nativeLibraries.find(l=>l.id===libraryId)?.name||'Library',description:'',libraryId,predicate:defaultPredicate(),outputs:[],x:40,y:40+filters.length*160};filters.push(filter);}filter.outputs.push(r.id);}}
        for(const library of nativeLibraries){const id='library-'+library.id.replaceAll('-','').toLowerCase();if(filters.some(f=>f.id===id)||filters.length>=50)continue;filters.push({id,name:library.name,description:'',libraryId:library.id,predicate:defaultPredicate(),outputs:[],x:40,y:40+filters.length*160});}
    }
    const fields = { title: 'Jellyfin · Title', year: 'Jellyfin · Release year', added: 'Jellyfin · Date added', rating: 'Jellyfin · Rating', kind: 'Jellyfin · Media kind', 'seerr.ownRequest': 'Seerr · Owned by automation authorizer (legacy)', 'seerr.is4k': 'Seerr · 4K edition', 'seerr.requestOnly': 'Seerr · Unfulfilled request', 'seerr.requesterId':'Seerr · Requester Jellyfin user ID' };
    const node = (tag, text) => { const e = document.createElement(tag); if (text !== undefined) e.textContent = text; return e; };
    const normalize = value => Array.isArray(value) ? value.map(normalize) : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([key, item]) => [key[0].toLowerCase() + key.slice(1), key.toLowerCase()==='trace' ? item : key.toLowerCase()==='sources'&&item ? Object.fromEntries(Object.entries(item).map(([id,position])=>[id,normalize(position)])) : normalize(item)])) : value;
    const button = (label, action) => { const holder = node('div'); holder.innerHTML = '<button is="emby-button" type="button" class="raised"></button>'; const b = holder.firstElementChild; b.textContent = label; b.addEventListener('click', action); return b; };
    const operators = field => window.RemovalMetadataCatalog?.find(f=>f.id===field)?.operators || (field.startsWith('seerr.') ? ['equals', 'not-equals'] : field === 'added' ? ['older-days', 'newer-days'] : ['title', 'kind'].includes(field) ? ['equals', 'not-equals', 'contains'] : ['equals', 'not-equals', 'greater', 'less']);
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
    // Maintainerr RuleDto/RuleDbDto exports. Never import execution or connection settings.
    function maintainerrDraft(input){
        const unpack=value=>typeof value==='string'?JSON.parse(value):value;
        const raw=unpack(input.JsonRules??input.jsonRules??input);
        const groups=Array.isArray(raw)?raw.every(r=>Array.isArray(r.rules))?raw:[{name:input.name||'Imported rule',description:input.description,libraryId:input.libraryId,dataType:input.dataType,arrAction:input.arrAction,rules:raw}]:Array.isArray(raw.ruleGroups)?raw.ruleGroups:[raw];
        if(!groups.length||groups.length>50)throw Error('Maintainerr: expected 1–50 rule groups.');
        const maps={1:{0:'radarr.added',5:'radarr.monitored',6:'radarr.inCinemas',8:'radarr.fileAudioChannels',9:'radarr.fileQuality',10:'radarr.fileAdded',11:'radarr.runtime',12:'radarr.filePath',13:'radarr.originalLanguage',16:'radarr.imdbRating',19:'radarr.imdbVotes',20:'radarr.fileQualityCutoffMet',21:'radarr.fileQualityName',22:'radarr.fileAudioLanguages',25:'radarr.title',26:'radarr.id'},2:{0:'sonarr.added',3:'sonarr.qualityProfileId',4:'sonarr.firstAired',5:'sonarr.seasonCount',6:'sonarr.status',7:'sonarr.ended',8:'sonarr.monitored',10:'sonarr.unairedEpisodes',11:'sonarr.monitoredSeasons',19:'sonarr.rating',20:'sonarr.ratingVotes',25:'sonarr.qualityProfile',27:'sonarr.seriesType',31:'sonarr.missingEpisodes',33:'sonarr.title',34:'sonarr.id'},3:{1:'seerr.requestDate',4:'seerr.mediaAddedAt',5:'seerr.requestCount',6:'seerr.isRequested'},6:{0:'added',2:'jellyfin.premiereDate',11:'jellyfin.genres',14:'jellyfin.episodeCount',16:'jellyfin.lastEpisodeAdded',19:'jellyfin.collections',21:'jellyfin.playlists',22:'jellyfin.criticRating',23:'rating',24:'jellyfin.tags',27:'jellyfin.lastEpisodeAired',30:'jellyfin.playCount',46:'jellyfin.studios',47:'jellyfin.lastPlayedAt'}};
        const actions=['greater','less','equals','not-equals','contains','before','after','in-last-days','in-next-days','not-contains','contains-partial','not-contains-partial','contains-all','not-contains-all','count-equals','count-not-equals','count-greater','count-less','exists','not-exists'];
        const imported=[],importedFilters=[];
        for(const [groupIndex,group]of groups.entries()){
            if(group.arrAction!=null&&Number(group.arrAction)!==0)throw Error('Maintainerr: non-deletion actions need manual conversion. Nothing imported.');
            if(group.dataType!=null&&!['movie','show'].includes(group.dataType))throw Error('Maintainerr: episode/season scopes need manual conversion. Nothing imported.');
            const rows=unpack(group.rules??group.JsonRules??group.jsonRules);if(!Array.isArray(rows)||!rows.length||rows.length>50)throw Error('Maintainerr: each group needs 1–50 conditions.');
            const nodes=[{id:'start',kind:'start',x:0,y:0,inputs:[]}],conditions=[],sections=[];let currentSection;
            const join=(left,right,operator)=>{if(!left)return right;if(operator!==0&&operator!==1&&operator!=='0'&&operator!=='1')throw Error('Maintainerr: unsupported Boolean operator.');const id='g'+nodes.length;nodes.push({id,kind:Number(operator)===0?'and':'or',x:500+nodes.length*12,y:100+nodes.length*35,inputs:[left,right]});return id;};
            for(const [index,saved]of rows.entries()){
                const row=saved.ruleJson?{...unpack(saved.ruleJson),section:saved.section}:saved;
                const fail=detail=>{throw Error('Maintainerr condition '+(index+1)+': '+detail+'. Nothing imported.');};
                const resolve=pair=>{if(!Array.isArray(pair)||pair.length!==2)return fail('invalid metadata reference');const field=maps[pair[0]]?.[pair[1]];if(!field||!window.RemovalMetadataCatalog?.some(f=>f.id===field))return fail('unsupported metadata '+pair.join(':'));return field;};
                if(row.username||row.arrDiskPath)fail('scoped user or disk rules need manual conversion');
                const field=resolve(row.firstVal),definition=window.RemovalMetadataCatalog.find(f=>f.id===field),action=Number(row.action);let operator=actions[action],value=null,referenceField;
                if(!Number.isInteger(action)||!operator)fail('unsupported comparison');
                if(action<18){if(row.lastVal){referenceField=resolve(row.lastVal);const other=window.RemovalMetadataCatalog.find(f=>f.id===referenceField);if(definition.type!==other.type||definition.unit!==other.unit)fail('incompatible metadata types');}
                    else{const custom=row.customVal;if(!custom||custom.value===undefined)fail('missing comparison value');value=custom.value;
                        if(definition.type==='date'&&Number(custom.ruleTypeId)===0){value=Number(value)/86400;if(!Number.isFinite(value)||value<0)fail('invalid duration');if(action===5)operator='older-days';else if(![7,8].includes(action))fail('unsupported relative-date comparison');}
                        else if(definition.type==='date'){if(Number(custom.ruleTypeId)!==1)fail('invalid date type');const date=new Date(String(value).includes('-')?value:Number(value)*1000);if(!Number.isFinite(date.getTime()))fail('invalid date');value=date.toISOString();}
                        else if(definition.type==='boolean'){if(!['0','1','true','false'].includes(String(value)))fail('invalid Boolean');value=['1','true'].includes(String(value));}
                        else if(definition.type==='number'||operator.startsWith('count-')){if(String(value).trim()===''||!Number.isFinite(Number(value)))fail('invalid number');value=Number(value);}
                        else if(definition.type==='list'&&typeof value==='string'&&value.trim().startsWith('[')){value=JSON.parse(value);if(!Array.isArray(value)||value.some(v=>typeof v!=='string'))fail('invalid text list');}
                    }
                }
                if(!definition.operators.includes(operator))fail('comparison unavailable for this metadata');
                const condition={field,operator,value,...(referenceField?{referenceField}:{} )},id='c'+index;conditions.push(condition);nodes.push({id,kind:'condition',x:200,y:150+index*180,inputs:[],condition});
                const section=row.section??0;if(currentSection!==section){if(sections.some(s=>s.id===section))fail('non-contiguous sections');sections.push({id:section,operator:row.operator,root:id});currentSection=section;}else{const previous=sections.at(-1);previous.root=join(previous.root,id,row.operator);}
            }
            let root;for(const section of sections)root=join(root,section.root,section.operator);
            if(group.dataType){if(conditions.length>=50)throw Error('Maintainerr: too many conditions including media type.');const condition={field:'kind',operator:'equals',value:group.dataType==='show'?'series':'movie'};conditions.push(condition);nodes.push({id:'scope',kind:'condition',x:200,y:100,inputs:[],condition});root=join(root,'scope',0);}
            nodes.push({id:'delete',kind:'delete',x:1000,y:100,inputs:[root,'start']});
            const id=crypto.randomUUID(),name=String(group.name||input.name||'Imported rule').slice(0,100);imported.push({id,name,description:String(group.description||input.description||'').slice(0,1000),enabled:false,match:'all',conditions,circuit:{version:1,nodes,zoom:1,panX:0,panY:0},canvasX:540,canvasY:40+groupIndex*160});
            const library=nativeLibraries.find(l=>l.id.replaceAll('-','').toLowerCase()===String(group.libraryId||'').replaceAll('-','').toLowerCase());
            importedFilters.push({id:crypto.randomUUID(),name:name+' library',libraryId:library?.id||'',predicate:defaultPredicate(),outputs:[id],x:40,y:40+groupIndex*160});
        }
        return {rules:imported,filters:importedFilters,view:{zoom:1,panX:0,panY:0}};
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
            const connected=filter?[]:filters.filter(f=>f.outputs.includes(rule.id));
            const incoming=filter?[]:connected.length?connected:filters.filter(f=>f.id.startsWith('library-'));
            const libraryChoices=filter?nativeLibraries.map(l=>[l.id,l.name]):[...new Map(incoming.map(f=>[f.libraryId,[f.libraryId,f.name]])).values()];
            const library = field(header,filter?'Filter items by library':connected.length?'Connected test libraries':'Default test libraries','',filter?.libraryId||incoming[0]?.libraryId||firstLibrary(),libraryChoices);
            library.onchange=()=>{if(filter){filter.libraryId=library.value;sync();}else{testSource=incoming.find(f=>f.libraryId===library.value)||null;findMedia();}};
            if(filter){const routes=node('fieldset');routes.append(node('legend','Output nodes · attached rules'));for(const target of rules){const checked=field(routes,target.name,'checkbox',filter.outputs.includes(target.id));checked.dataset.routeCheckbox=target.id;checked.onchange=()=>{filter.outputs=checked.checked?[...new Set([...filter.outputs,target.id])]:filter.outputs.filter(id=>id!==target.id);sync();};}card.append(routes);}
            else {const enabled = field(card, 'Enable this rule', 'checkbox', rule.enabled);enabled.disabled=!connected.length;enabled.title=connected.length?'':'Attach a library filter before enabling this rule.'; enabled.onchange = () => { rule.enabled = enabled.checked; sync(); };}
            cards.append(card);
            let testSource=filter?null:incoming.find(f=>f.libraryId===library.value)||null;
            const editor=window.RemovalCircuit.mount(card,rule,sync,{sources:incoming,allSources:filters,filterMode:!!filter,targets:rules,route:filter,onEditSource:source=>{selected={kind:'filter',id:source.id};render();},onRoute:(source,attached)=>{source.outputs=attached?[...new Set([...source.outputs,rule.id])]:source.outputs.filter(id=>id!==rule.id);render();},onSource:source=>{testSource=source;library.value=source.libraryId;media.replaceChildren();el('arStatus').textContent='Testing input path: '+source.name+'. Only items passing this filter can be selected.';findMedia();}});editors.push(editor);
            const tests=node('div');tests.className='ar-test';card.append(tests);
            const search=field(tests,'Find media in this library','search',''), media=field(tests,'Test media','','',[]);
            let searchEpoch=0;
            async function findMedia(){const current=++searchEpoch;testButton.disabled=true;try{const response=testSource?await call('POST','/media/testable',{filter:testSource,search:search.value}):await call('GET','/media?libraryId='+encodeURIComponent(library.value)+'&search='+encodeURIComponent(search.value));if(current!==searchEpoch||!media.isConnected)return;const found=testSource?response.items:response;media.replaceChildren();for(const item of found){const option=node('option',item.title+(item.year?' ('+item.year+')':''));option.value=item.id;media.append(option);}testButton.disabled=!found.length||(!filter&&!testSource);if(!found.length)el('arStatus').textContent='No media currently passes this input path. Refine the search or check unavailable evidence.';}catch(e){fail(e);}}
            const testButton=button('Test draft circuit',async()=>{try{if(!filter&&!testSource)return;const result=await call('POST','/test',{rule,itemId:media.value,libraryId:testSource?.libraryId||library.value,filters:filter?undefined:testSource?[testSource]:incoming});if(!media.isConnected)return;editor.trace(result.trace);el('arStatus').textContent=result.title+': '+(Object.values(result.trace).some(v=>v===null)?'Unknown evidence — blocked':result.matches?(filter?'True — passes filter':'True — would request removal'):'False — no request')+'. Test only; no changes made.';}catch(e){fail(e);}});testButton.disabled=true;tests.append(testButton);
            search.addEventListener('input',()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{if(search.isConnected)void findMedia();},250);});
            let searchTimer;void findMedia();
            card.append(button('Remove card', () => {if(!confirm('Remove this card and its connections?'))return;if(filter)filters=filters.filter(f=>f.id!==filter.id);else{rules.splice(index,1);for(const f of filters)f.outputs=f.outputs.filter(id=>id!==rule.id);}selected=null;render();}));
        }
        sync();
    }
    function sync() { const filter=selected?.kind==='filter'?filters?.find(f=>f.id===selected.id):null;if(filter)for(const box of page.querySelectorAll('[data-route-checkbox]'))box.checked=filter.outputs.includes(box.dataset.routeCheckbox);el('arRules').value = JSON.stringify({rules,filters,view}, null, 2); }
    async function call(method, suffix = '', body) { const generation = epoch;if(method==='PUT'&&body?.configuration)body.configuration.view=view; const response = await ApiClient.ajax({ url: ApiClient.getUrl('AutomaticDeletionRequests/admin' + suffix), type: method, dataType: 'json', contentType: 'application/json', data: body ? JSON.stringify(body) : undefined }).catch(error=>{if(active&&generation===epoch)window.SuiteAdminUI?.reportError?.(page,error);throw error;}); if (!active || generation !== epoch) throw new DOMException('View closed', 'AbortError');const result=normalize(response);if(method==='GET'&&!suffix){view=result.configuration.view||{zoom:1,panX:0,panY:0};nativeLibraries=result.libraries||[];if(result.metadata)window.RemovalMetadataCatalog=result.metadata;if(result.metadataUsers)window.RemovalMetadataUsers=result.metadataUsers;}return result; }
    const fail = error => { if (active && error?.name !== 'AbortError') el('arStatus').textContent = 'Operation unavailable. Review your administrator session, revision and rule values.'; };
    async function load() { const generation = epoch, form = el('arForm'); form.inert = true; form.setAttribute('aria-busy', 'true'); try { const result = await call('GET'), c = result.configuration; revision = c.revision; legacyLibrary=c.libraryId?.replaceAll('-','')==='0'.repeat(32)?'':c.libraryId; el('arHours').value = c.intervalHours; el('arEnabled').checked = c.enabled; rules = c.rules;filters=c.filters??null;selected=null; savedNames = new Map(rules.map(r => [r.id, r.name])); render(); el('arStatus').textContent = result.queueAvailable ? result.status : 'Deletion Queue is missing. Request creation is unavailable.'; form.inert = false; } finally { if (active && generation === epoch) form.setAttribute('aria-busy', 'false'); } }
    for(const section of page.querySelectorAll('[data-arr-source]')){
        const source=section.dataset.arrSource,prefix=source==='sonarr'?'arSonarr':'arRadarr',url=el(prefix+'Url'),key=el(prefix+'Key'),status=section.querySelector('[data-arr-status]');let savedRevision=null,busy=false;
        const paint=result=>{savedRevision=result.revision;url.value=result.url;key.value='';key.placeholder=result.keyConfigured?'Saved key (leave blank to keep)':'';status.textContent=result.keyConfigured?'Configured':'Not configured';};
        const run=async action=>{if(busy||!active)return;busy=true;for(const b of section.querySelectorAll('button'))b.disabled=true;try{await action();}catch{if(active)status.textContent='Unavailable. Check the connection or use one enabled Enhanced instance.';}finally{busy=false;if(active)for(const b of section.querySelectorAll('button'))b.disabled=false;}};
        el('arConnections').addEventListener('toggle',()=>{if(el('arConnections').open)void run(async()=>paint(await call('GET','/metadata/'+source)));});
        section.querySelector('[data-arr-save]').onclick=()=>run(async()=>{if(savedRevision===null)throw Error();paint(await call('PUT','/metadata/'+source,{revision:savedRevision,url:url.value.trim(),apiKey:key.value||undefined}));});
        section.querySelector('[data-arr-test]').onclick=()=>run(async()=>{await call('POST','/metadata/'+source+'/test',{});status.textContent='Saved connection verified';});
        section.querySelector('[data-arr-import]').onclick=()=>run(async()=>{if(savedRevision===null)throw Error();paint(await call('POST','/metadata/'+source+'/import-enhanced',{revision:savedRevision}));status.textContent='Imported and verified';});
        page.addEventListener('viewhide',()=>{key.value='';url.value='';savedRevision=null;status.textContent='';});
    }
    const dialog=el('arNewCard');
    el('arAddRule').onclick=()=>dialog.showModal();
    el('arNewCancel').onclick=()=>dialog.close();
    el('arNewCreate').onclick=()=>{
        const title=el('arNewTitle');if(!title.reportValidity())return;
        const type=el('arNewType').value;const card={id:crypto.randomUUID(),name:title.value.trim(),description:el('arNewDescription').value};if(!card.name)return;
        if(type==='filter'){if(filters.length>=50)return;filters.push({...card,libraryId:firstLibrary(),predicate:defaultPredicate(),outputs:[],x:40,y:40+filters.length*160});}
        else{if(rules.length>=50)return;rules.push({...card,enabled:false,match:'all',conditions:[{field:'added',operator:'older-days',value:30}],canvasX:540,canvasY:40+rules.length*160});}
        selected={kind:type,id:card.id};dialog.close();title.value='';el('arNewDescription').value='';render();
    };
    el('arImport').addEventListener('click', () => { try {
        if(el('arRules').value.length>2000000)throw Error('JSON exceeds 2 MB.');
        let draft=JSON.parse(el('arRules').value);const isMaintainerr=!!(draft?.JsonRules||draft?.jsonRules||draft?.ruleGroups||Array.isArray(draft?.rules)&&draft.rules.some(r=>r.firstVal||r.ruleJson)||Array.isArray(draft)&&draft.some(r=>r.firstVal||r.ruleJson||r.rules?.some(c=>c.firstVal||c.ruleJson)));if(isMaintainerr)draft=maintainerrDraft(draft);
        const imported=Array.isArray(draft)?draft:draft.rules;
        const validRule=r=>r&&typeof r.id==='string'&&/^[a-zA-Z0-9_-]{1,100}$/.test(r.id)&&typeof r.name==='string'&&r.name.length>0&&r.name.length<=100&&Array.isArray(r.conditions)&&r.conditions.length>=1&&r.conditions.length<=50&&r.conditions.every(c=>c&&(fields[c.field]||window.RemovalMetadataCatalog?.some(f=>f.id===c.field))&&operators(c.field).includes(c.operator));
        if(!Array.isArray(imported)||imported.length>50||!imported.every(validRule)||new Set(imported.map(r=>r.id)).size!==imported.length)throw Error();
        const importedFilters=Array.isArray(draft)?null:draft.filters??null;
        if(importedFilters!==null&&(!Array.isArray(importedFilters)||importedFilters.length>50||importedFilters.some(f=>!f||typeof f.id!=='string'||!Array.isArray(f.outputs)||f.outputs.some(id=>!imported.some(r=>r.id===id))||!validRule(f.predicate))))throw Error();
        for(const r of [...imported,...(importedFilters||[]).map(f=>f.predicate)])if(r.circuit)window.RemovalCircuit.check(r.circuit);
        const importedView=draft.view||{zoom:1,panX:0,panY:0};
        if(![importedView.zoom,importedView.panX,importedView.panY].every(Number.isFinite)||importedView.zoom<.25||importedView.zoom>2||Math.abs(importedView.panX)>10000||Math.abs(importedView.panY)>10000)throw Error();
        rules=imported;filters=importedFilters;view=importedView;selected=null;render();el('arStatus').textContent=isMaintainerr?'Maintainerr imported as disabled drafts. Select each library and review before enabling.':'Draft imported. Review before saving.';
    }catch(error){el('arStatus').textContent=error.message?.startsWith('Maintainerr')?error.message:'Invalid canvas JSON. Nothing imported.';} });
    el('arExport').onclick=()=>{sync();const url=URL.createObjectURL(new Blob([el('arRules').value],{type:'application/json'}));const a=node('a');a.href=url;a.download='removal-automation-canvas.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
    el('arForm').addEventListener('submit', async event => { event.preventDefault(); if (revision === null) return; try { const enabled = el('arEnabled').checked; if (enabled && filters.some(f=>!f.libraryId)) { el('arStatus').textContent = 'Choose a library for each filter.'; return; } if (enabled && !confirm('Enable creation of removal requests for matching items? Every request still requires Deletion Queue safety gates.')) return; await call('PUT', '', { confirmAutomation: enabled, configuration: { revision, enabled, intervalHours: Number(el('arHours').value), rules,filters } }); await load(); } catch (error) { fail(error); } });
    el('arPreview').addEventListener('click', async () => { try {
        const result = await call('POST', '/preview'), matches = result.matches || [], host = el('arResult'); host.replaceChildren();
        host.append(node('h2', matches.length ? matches.length + ' saved-rule matches' : 'No matches this time.'), node('p', 'Preview only. No requests, votes or deletions were created. Save draft changes before previewing them.'));
        const grid = node('div'); grid.className = 'ar-match-grid'; host.append(grid);
        for (const match of matches.slice(0, 200)) { const card = node('article'); card.className = 'ar-match-card'; card.append(node('h3', match.title), node('p', savedNames.get(match.ruleId) || 'Saved rule'), node('p', match.alreadySubmitted ? 'Already submitted · no duplicate request' : 'Would request removal · all safety gates still apply')); grid.append(card); }
        if (matches.length > 200) host.append(node('p', 'Showing the first 200 matches. Narrow your saved rules for a smaller preview.'));
    } catch (error) { fail(error); } });
    page.addEventListener('viewhide', () => { active = false; epoch++; revision = null;dialog.close();el('arNewTitle').value='';el('arNewDescription').value=''; editors.forEach(e=>e.dispose());editors=[];rules = [];filters=null;selected=null; savedNames.clear(); el('arRules').value = ''; el('arResult').textContent = ''; el('arRuleCards').replaceChildren(); });
    page.addEventListener('viewshow', () => { if (!active) { active = true; load().catch(fail); } }); load().catch(fail);
    page.addEventListener('viewhide',()=>{window.RemovalMetadataUsers=[];window.RemovalMetadataCatalog=undefined;el('arConnections').open=false;});
})();
