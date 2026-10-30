import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const require = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const source = path => readFile(new URL('../' + path, import.meta.url), 'utf8');
const turn = () => new Promise(resolve => setImmediate(resolve));
const response = data => ({ ok: true, status: 200, json: async () => ({ apiVersion: '1', ok: true, data }) });
// Explicit ownership, not incidental words in a test's human-readable title.
const owned = owner => (name, ...args) => test(owner + ': ' + name, ...args);
const automaticTest=owned('automatic-deletion-requests'),queueTest=owned('deletion-queue'),notificationTest=owned('notifications'),hubTest=owned('account-hub'),discordTest=owned('discord-bridge');
automaticTest('finished-viewer lists offer a requester metadata membership operand without requiring JSON',async()=>{
    const dom=new JSDOM('<main></main>',{runScripts:'outside-only'}),w=dom.window;w.CSS||={};w.CSS.escape||=x=>x;
    try{
        w.RemovalMetadataCatalog=[{id:'jellyfin.allEpisodesSeenBy',source:'jellyfin',label:'Every episode watched by',type:'list',elementType:'user',operators:['contains','not-contains'],conversions:['count']},{id:'seerr.requesterId',source:'seerr',label:'Requester',type:'user',operators:['equals','not-equals']},{id:'title',source:'jellyfin',label:'Title',type:'text',operators:['equals']}];
        w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/circuit.js'));
        const rule={match:'all',conditions:[{field:'jellyfin.allEpisodesSeenBy',operator:'contains',value:''}]};w.RemovalCircuit.mount(w.document.querySelector('main'),rule,()=>{});
        const operand=w.document.querySelector('[aria-label="Comparison operand"]');operand.value='metadata';operand.dispatchEvent(new w.Event('change'));
        const second=w.document.querySelector('[aria-label="Second metadata field"]');assert.deepEqual([...second.options].map(o=>o.value),['','seerr.requesterId']);second.value='seerr.requesterId';second.dispatchEvent(new w.Event('change'));
        assert.equal(rule.conditions[0].referenceField,'seerr.requesterId');assert.equal(w.document.querySelector('[aria-label="Condition value"]'),null);w.RemovalCircuit.check(rule.circuit);
    }finally{w.close();}
});
automaticTest('date conversion editor wires a second source and keeps numeric comparisons numeric',async()=>{
    const dom=new JSDOM('<main></main>',{runScripts:'outside-only'}),w=dom.window;w.CSS||={};w.CSS.escape||=x=>x;
    try{
        w.RemovalMetadataCatalog=[{id:'added',source:'jellyfin',label:'Added',type:'date',operators:['before','older-days'],conversions:['age-days','difference-days']},{id:'radarr.added',source:'radarr',label:'Radarr added',type:'date',operators:['before','older-days']}];
        w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/circuit.js'));
        const rule={match:'all',conditions:[{field:'added',operator:'older-days',value:30}]};w.RemovalCircuit.mount(w.document.querySelector('main'),rule,()=>{});
        const select=(label,value)=>{const input=w.document.querySelector('[aria-label="'+label+'"]');assert(input);input.value=value;input.dispatchEvent(new w.Event('change'));};
        select('Metadata conversion','difference-days');select('Second metadata field','radarr.added');
        const condition=rule.circuit.nodes.find(n=>n.kind==='condition');assert.equal(condition.condition.conversion,'difference-days');assert.equal(condition.condition.referenceField,'radarr.added');assert.equal(rule.circuit.nodes.find(n=>n.id===condition.inputs[1]).source,'radarr');
        select('Condition comparison','equals');assert.equal(w.document.querySelector('[aria-label="Condition value"]').type,'number');
        w.RemovalCircuit.check(rule.circuit);select('Metadata conversion','none');assert.equal(condition.inputs.length,1);assert.equal(condition.condition.referenceField,undefined);
        assert(w.document.querySelector('[aria-label="Metadata field"]'));assert([...w.document.querySelectorAll('button')].some(b=>b.textContent==='Arrange blocks'));
    }finally{w.close();}
});
automaticTest('Automation chrome defers surfaces and typography to native skins',async()=>{
    const html=await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/admin.html');
    assert(!/#151a24|#e3e9f3|#182131|#252e3e|font-family\s*:/i.test(html));
    assert.match(html,/--theme-primary-color/);assert.match(html,/background:var\(--ar-canvas-surface\)/);
    const canvas=await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/circuit.js');
    assert(canvas.includes('ar-circuit dialog paperList'));assert(canvas.includes("if(!n)return"));
});
automaticTest('Circuit library links are editable, cards open their filters and traces colour ports and wires', async () => {
    const dom=new JSDOM('<main></main>',{runScripts:'outside-only'}),w=dom.window;
    try {
        w.CSS ||= {};w.CSS.escape ||= x=>x;
        w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/circuit.js'));
        const rule={match:'all',conditions:[{field:'year',operator:'less',value:2000}]};let chosen;
        let routed;
        const editor=w.RemovalCircuit.mount(w.document.querySelector('main'),rule,()=>{},{sources:[{id:'scope',name:'Old movies'}],onSource:s=>{chosen=s.id;},onRoute:(s,attached)=>{routed={id:s.id,attached};}});
        const card=w.document.querySelector('.ar-source');assert.equal(card.querySelectorAll('.ar-socket').length,1);
        assert.equal(w.document.querySelectorAll('[data-kind=start]').length,0);assert.equal(rule.circuit.version,2);
        card.click();assert.equal(chosen,'scope');
        w.document.querySelector('.ar-wires path[data-scope=scope]').dispatchEvent(new w.MouseEvent('click',{bubbles:true}));assert.deepEqual(routed,{id:'scope',attached:false});
        const title=card.querySelector('header');const x=parseFloat(card.style.left);
        title.onpointerdown(new w.MouseEvent('pointerdown',{bubbles:true,clientX:20,clientY:20}));
        title.onpointermove(new w.MouseEvent('pointermove',{bubbles:true,clientX:70,clientY:40}));
        title.onpointerup();assert.equal(rule.circuit.sources.scope.x,x+50/rule.circuit.zoom);
        editor.trace({start:true,c0:false,group:false,delete:false,'filter:scope':true});
        assert(w.document.querySelector('.ar-wires path[data-signal=false]'));assert(w.document.querySelector('.ar-socket[data-signal=false]'));
        assert(w.document.querySelector('.ar-wires path[data-signal=true]'));
        const viewport=w.document.querySelector('.ar-viewport'),pan=rule.circuit.panX;
        viewport.dispatchEvent(new w.MouseEvent('pointerdown',{bubbles:true,button:1,clientX:10,clientY:10}));
        viewport.dispatchEvent(new w.MouseEvent('pointermove',{bubbles:true,clientX:50,clientY:30}));viewport.dispatchEvent(new w.MouseEvent('pointerup',{bubbles:true}));assert.equal(rule.circuit.panX,pan+40);
        const wire=w.document.querySelector('.ar-wires path[data-target=group]');wire.dispatchEvent(new w.MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:20,clientY:20}));
        const insert=[...w.document.querySelectorAll('.ar-canvas-menu button')].find(b=>b.textContent==='Insert NOT');assert(insert);insert.click();assert(rule.circuit.nodes.some(n=>n.kind==='not'));
        assert(w.document.querySelector('.ar-logic-gate svg path'));
        editor.dispose();assert.equal(w.document.querySelectorAll('.ar-circuit').length,0);
    }finally{w.close();}
});
automaticTest('metadata sockets constrain conditions and AND gates grow and shrink with connected inputs',async()=>{
    const dom=new JSDOM('<main></main>',{runScripts:'outside-only'}),w=dom.window;w.CSS||={};w.CSS.escape||=x=>x;
    try{
        w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/circuit.js'));
        const rule={match:'all',conditions:[{field:'title',operator:'contains',value:'movie'}]};w.RemovalCircuit.mount(w.document.querySelector('main'),rule,()=>{});
        assert.equal(w.document.querySelectorAll('[data-kind=start]').length,0);w.RemovalCircuit.check(rule.circuit);
        [...w.document.querySelectorAll('.ar-palette button')].find(b=>b.textContent==='AND').click();
        let gate=rule.circuit.nodes.filter(n=>n.kind==='and').at(-1);const condition=rule.circuit.nodes.find(n=>n.kind==='condition');
        const output=()=>w.document.querySelector(`[data-id="${condition.id}"] .ar-output`).click();
        for(let i=0;i<3;i++){output();w.document.querySelector(`[data-id="${gate.id}"] [data-port="${i}"]`).click();}
        assert.equal(gate.inputs.length,3);assert(gate.inputs.every(Boolean));
        assert.equal(w.document.querySelector(`[data-id="${gate.id}"]`).style.minHeight,'168px');
        w.document.querySelector(`path[data-target="${gate.id}"][data-input="1"]`).dispatchEvent(new w.MouseEvent('click',{bubbles:true}));
        [...w.document.querySelectorAll('.ar-canvas-menu button')].find(b=>b.textContent==='Disconnect').click();
        assert.equal(gate.inputs.length,2);assert(gate.inputs.every(Boolean));
        const metadata=w.document.querySelector('[data-kind=metadata]');metadata.querySelector('button').click();assert(w.document.querySelector('[data-field=title]'));
        w.document.querySelector('[data-field=title]').click();w.document.querySelector(`[data-id="${gate.id}"] [data-port="0"]`).click();
        assert.equal(gate.inputs[0],condition.id,'Metadata cannot feed a Boolean gate directly');
    }finally{w.close();}
});
automaticTest('metadata conditions support confirmed-absence operators and a second typed source operand',async()=>{
    const dom=new JSDOM('<main></main>',{runScripts:'outside-only'}),w=dom.window,d=w.document;w.CSS||={};w.CSS.escape||=x=>x;
    try{
        w.RemovalMetadataCatalog=[{id:'added',source:'jellyfin',label:'Added',type:'date',operators:['before','exists','not-exists']},{id:'radarr.added',source:'radarr',label:'Arr added',type:'date',operators:['before','exists','not-exists']}];
        w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/circuit.js'));
        const rule={match:'all',conditions:[{field:'added',operator:'before',value:'2020-01-01T00:00:00Z'}]};w.RemovalCircuit.mount(d.querySelector('main'),rule,()=>{});
        let select=d.querySelector('[aria-label="Condition comparison"]');select.value='not-exists';select.dispatchEvent(new w.Event('change'));
        assert.equal(rule.conditions[0].value,null);assert.equal(d.querySelector('[aria-label="Condition value"]'),null);w.RemovalCircuit.check(rule.circuit);
        select=d.querySelector('[aria-label="Condition comparison"]');select.value='before';select.dispatchEvent(new w.Event('change'));
        select=d.querySelector('[aria-label="Comparison operand"]');select.value='metadata';select.dispatchEvent(new w.Event('change'));
        assert.equal(d.querySelectorAll('[data-kind=condition] .ar-input').length,2);assert.throws(()=>w.RemovalCircuit.check(rule.circuit));
        d.querySelector('.ar-palette [aria-label="Radarr metadata"]').click();d.querySelector('[data-source-type=radarr] button').click();
        d.querySelector('[data-field="radarr.added"]').click();d.querySelector('[data-kind=condition] [data-port="1"]').click();
        assert.equal(rule.conditions[0].referenceField,'radarr.added');w.RemovalCircuit.check(rule.circuit);
    }finally{w.close();}
});
automaticTest('Sonarr and Radarr connection settings support manual editing and remain editable after import',async()=>{
    const html=await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/admin.html');
    const dom=new JSDOM(html,{runScripts:'outside-only',url:'https://example.test/web/'}),w=dom.window,d=w.document,calls=[];
    const saved={sonarr:{revision:0,url:'',keyConfigured:false},radarr:{revision:0,url:'',keyConfigured:false}};
    w.RemovalRouting={mount:()=>({dispose(){}})};
    w.ApiClient={getUrl:p=>p,ajax:async options=>{
        calls.push(options);const match=options.url.match(/\/metadata\/(sonarr|radarr)(\/import-enhanced)?$/);
        if(match){const source=match[1];if(options.type==='PUT'){const body=JSON.parse(options.data);assert.equal(body.revision,saved[source].revision);saved[source]={revision:body.revision+1,url:body.url,keyConfigured:true};}
            if(match[2])saved[source]={revision:saved[source].revision+1,url:'https://imported-'+source+'.test',keyConfigured:true};
            return {...saved[source]};}
        return {configuration:{revision:0,enabled:false,intervalHours:24,rules:[],filters:[]},libraries:[],queueAvailable:true};
    }};
    try{
        w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/admin.js'));await turn();await turn();
        d.querySelector('#arConnections').open=true;d.querySelector('#arConnections').dispatchEvent(new w.Event('toggle'));await turn();await turn();
        for(const source of ['sonarr','radarr']){
            const section=d.querySelector('[data-arr-source='+source+']'),url=section.querySelector('input[type=url]'),key=section.querySelector('input[type=password]');
            assert.equal(url.readOnly,false);assert.equal(key.readOnly,false);assert.equal(url.disabled,false);assert.equal(key.disabled,false);
            url.value='https://manual-'+source+'.test';key.value='manual-secret';section.querySelector('[data-arr-save]').click();await turn();await turn();
            assert.equal(saved[source].url,url.value);assert.equal(key.value,'');
            section.querySelector('[data-arr-import]').click();await turn();await turn();assert.equal(url.value,'https://imported-'+source+'.test');
            assert.equal(url.readOnly,false);assert.equal(key.readOnly,false);
            url.value='https://edited-'+source+'.test';key.value='replacement-secret';section.querySelector('[data-arr-save]').click();await turn();await turn();
            assert.equal(saved[source].url,'https://edited-'+source+'.test');assert.equal(saved[source].revision,3);assert.equal(key.value,'');
        }
        assert.equal(calls.filter(c=>c.type==='PUT').length,4);
    }finally{w.close();}
});
automaticTest('automatic rule editor uses typed Jellyfin/Seerr controls and does not save while editing', async () => {
    const html = await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/admin.html');
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/web/' }); const w = dom.window, d = w.document, writes = [];
    let config = { revision: 0, enabled: false, actorId: '00000000-0000-0000-0000-000000000000', libraryId: '00000000-0000-0000-0000-000000000000', intervalHours: 24, rules: [] };
    const actor = '11111111-1111-1111-1111-111111111111', library = '22222222-2222-2222-2222-222222222222';
    const pascal = value => Array.isArray(value) ? value.map(pascal) : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([key, item]) => [key[0].toUpperCase() + key.slice(1), pascal(item)])) : value;
    w.ApiClient = { getUrl: p => p, ajax: async options => { if(options.url.includes('/media/testable'))return {items:[]};if(options.url.includes('/media?'))return []; if (options.type === 'POST') return { matches: [{ title: '<img src=x onerror=alert(1)>', ruleId: config.rules[0].id, alreadySubmitted: false }] }; if (options.type === 'PUT') { const body = JSON.parse(options.data); writes.push(body); config = { ...body.configuration, revision: 1 }; } return { configuration: pascal(config), actors: [{ Id: actor, name: 'Library administrator' }], libraries: [{ id: library, Name: 'Movies' }], queueAvailable: true, status: 'Preview' }; } };
    try {
        w.CSS ||= {}; w.CSS.escape ||= x=>x;
        w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;};
        w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/routing.js'));
        w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/circuit.js'));
        w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/admin.js')); await turn(); await turn();
        assert.match(d.querySelector('#arRuleCards').textContent, /Movies/);
        assert.equal(d.querySelector('#arActor'), null); assert.equal(d.querySelector('#arLibrary'), null);
        assert(!d.body.textContent.includes('Execution settings'));
        d.querySelector('#arAddRule').click();d.querySelector('#arNewTitle').value='Old media';d.querySelector('#arNewCreate').click(); assert.equal(writes.length, 0);
        d.querySelector('.ar-palette [aria-label="Seerr metadata"]').click();
        const seerr=d.querySelector('.ar-block[data-source-type=seerr]');[...seerr.querySelectorAll('button')].find(b=>b.textContent==='Show outputs').click();
        d.querySelector('[data-field="seerr.ownRequest"]').click();d.querySelector('.ar-block[data-kind=condition] .ar-input').click();
        const draft = JSON.parse(d.querySelector('#arRules').value); assert.equal(draft.rules[0].conditions[0].field, 'seerr.ownRequest'); assert.equal(draft.rules[0].conditions[0].value, true);
        d.querySelector('[data-field="seerr.requesterId"]').click();d.querySelector('.ar-block[data-kind=condition] .ar-input').click();
        const requester=d.querySelector('.ar-block[data-kind=condition] input');requester.value=actor;requester.dispatchEvent(new w.Event('change'));
        assert.equal(JSON.parse(d.querySelector('#arRules').value).rules[0].conditions[0].value,actor);
        assert.equal(writes.length, 0); assert.equal(d.querySelector('#arEnabled').checked, false);
        d.querySelector('#arForm').dispatchEvent(new w.Event('submit', { cancelable: true })); await turn(); await turn();
        assert.equal(writes.length, 1); assert.equal(writes[0].configuration.enabled, false); assert.equal(writes[0].configuration.rules[0].enabled, false);
        assert(!('actorId' in writes[0].configuration)); assert(!('libraryId' in writes[0].configuration));
        assert.equal(writes[0].configuration.filters[0].libraryId,library);
        d.querySelector('#arPreview').click(); await turn(); assert.equal(d.querySelectorAll('.ar-match-card').length, 1); assert.equal(d.querySelectorAll('#arResult img').length, 0); assert.match(d.querySelector('#arResult').textContent, /Preview only/); assert.equal(writes.length, 1);
        d.querySelector('#automaticDeletionAdmin').dispatchEvent(new w.Event('viewhide')); assert.equal(d.querySelector('#arRules').value, ''); assert.equal(d.querySelector('#arRuleCards').children.length, 0);
    } finally { dom.window.close(); }
});
automaticTest('Maintainerr JSON imports disabled drafts, preserves Boolean sections and rejects unsupported semantics atomically',async()=>{
    const html=await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/admin.html');const dom=new JSDOM(html,{runScripts:'outside-only',url:'https://example.test/web/'}),w=dom.window,d=w.document,calls=[];
    const library='22222222-2222-2222-2222-222222222222';
    w.ApiClient={getUrl:p=>p,ajax:async options=>{calls.push(options);return{configuration:{revision:0,enabled:false,intervalHours:24,rules:[],filters:[]},libraries:[{id:library,name:'Movies'}],queueAvailable:true,metadata:[{id:'added',source:'jellyfin',type:'date',operators:['older-days']},{id:'kind',source:'jellyfin',type:'text',operators:['equals']},{id:'radarr.monitored',source:'radarr',type:'boolean',operators:['equals']} ]};}};
    try{
        w.CSS||={};w.CSS.escape||=x=>x;w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/routing.js'));w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/circuit.js'));w.eval(await source('plugins/automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/admin.js'));await turn();await turn();
        const input={name:'Old films',libraryId:library,dataType:'movie',arrAction:0,rules:[{section:1,operator:null,firstVal:[6,0],action:5,customVal:{ruleTypeId:0,value:String(30*86400)}},{section:1,operator:0,firstVal:[1,5],action:2,customVal:{ruleTypeId:3,value:'1'}},{section:2,operator:1,firstVal:[1,5],action:2,customVal:{ruleTypeId:3,value:'0'}}]};
        const importJson=value=>{d.querySelector('#arRules').value=JSON.stringify(value);d.querySelector('#arImport').click();};importJson(input);
        assert.match(d.querySelector('#arStatus').textContent,/Maintainerr imported as disabled drafts/);const draft=JSON.parse(d.querySelector('#arRules').value),rule=draft.rules[0];assert.equal(rule.enabled,false);assert.equal(rule.conditions[0].operator,'older-days');assert.equal(rule.conditions[0].value,30);assert.equal(rule.conditions.at(-1).value,'movie');assert.equal(draft.filters.find(f=>f.outputs.includes(rule.id)).libraryId,library);assert(rule.circuit.nodes.some(n=>n.kind==='or'));assert(rule.circuit.nodes.filter(n=>n.kind==='and').length>=2);assert(!calls.some(c=>c.type!=='GET'));
        importJson({...input,rules:[{...input.rules[0],firstVal:[4,999]}]});assert.match(d.querySelector('#arStatus').textContent,/unsupported metadata 4:999/);
        const choose=d.querySelector('#arRuleCards select');assert([...choose.options].some(o=>o.value==='rule:'+rule.id));
        importJson({...input,dataType:'episode'});assert.match(d.querySelector('#arStatus').textContent,/episode\/season scopes/);assert([...choose.options].some(o=>o.value==='rule:'+rule.id));
        importJson({...input,arrAction:1});assert.match(d.querySelector('#arStatus').textContent,/non-deletion actions/);
        importJson({...input,rules:undefined,JsonRules:JSON.stringify(input.rules)});assert.match(d.querySelector('#arStatus').textContent,/Maintainerr imported/);const community=JSON.parse(d.querySelector('#arRules').value);assert.equal(community.rules[0].conditions.at(-1).value,'movie');assert.equal(community.filters.find(f=>f.outputs.includes(community.rules[0].id)).libraryId,library);
        importJson([input]);assert.match(d.querySelector('#arStatus').textContent,/Maintainerr imported/);assert.equal(JSON.parse(d.querySelector('#arRules').value).rules.length,1);assert(!calls.some(c=>c.type!=='GET'));
    }finally{w.close();}
});
queueTest('overlay properties hide irrelevant fields and use compact accessible alignment controls',async()=>{
    const dom=new JSDOM('<div id="deletionQueueAdmin"><main></main></div>',{runScripts:'outside-only'}),w=dom.window,d=w.document;
    try{
        w.eval(await source('plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/overlay-editor.js'));
        const editor=w.DeletionQueueOverlayEditor.mount(d.querySelector('main'),{text:'Leaving in {days} days',position:'bottom',bandHeight:.18,fontSize:.055,foreground:'#FFFFFF',background:'#8B2635',opacity:220},true);
        const root=d.querySelector('.dq-canvas-editor');assert.equal(root.dataset.layout,'portrait');
        const field=caption=>{const label=[...root.querySelectorAll('label')].find(l=>l.textContent===caption);return d.getElementById(label.htmlFor);};
        assert.equal(field('Corner radius (%)').parentElement.hidden,true);assert.equal(field('Layer text').parentElement.hidden,false);
        const align=root.querySelectorAll('.dq-canvas-align button');assert.equal(align.length,6);assert([...align].every(b=>b.getAttribute('aria-label')&&b.querySelector('.material-icons')));
        [...root.querySelectorAll('.dq-canvas-layers button')].find(b=>b.textContent==='Rectangle').click();
        assert.equal(field('Corner radius (%)').parentElement.hidden,false);assert.equal(field('Layer text').parentElement.hidden,true);assert.equal(field('Font weight').parentElement.hidden,true);
        const zoom=field('Zoom');zoom.value='2';zoom.dispatchEvent(new w.Event('change'));assert.equal(root.dataset.zoom,'2');zoom.value='fit';zoom.dispatchEvent(new w.Event('change'));assert.equal(root.querySelector('.dq-canvas-artboard').style.maxWidth,'');
        assert(root.querySelector('.dq-canvas-help summary'));editor.dispose();
    }finally{w.close();}
});
queueTest('Deletion Queue hides routing mode and confirms permissions-only reset using the saved revision', async () => {
    const html = await source('plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/admin.html');
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const w = dom.window, d = w.document, calls = []; let confirmed = false;
    const status = { mode: 'test', storageAvailable: true,
        policy: { revision: 42, defaultAuthority: 1, activityDays: 30, delayHours: 48, accounts: {} },
        seerr: { baseUrl: '', keyConfigured: false }, accounts: [], enhanced: { connectionAvailable: false }, proposals: [], journal: [] };
    w.confirm = () => confirmed;
    w.ApiClient = { getUrl: path => path, ajax: async options => {
        calls.push(options);
        if (options.url.endsWith('/admin/status')) return status;
        if (options.url.endsWith('/settings')) return { ok: true, data: { revision: 'saved-settings', hubFallbackAfterMinutes: 10 } };
        if (options.url.endsWith('/dependencies')) return { data: { items: [] } };
        if (options.url.endsWith('/seerr/test')) return { working: true, version: '3.0.0', message: 'Authenticated saved connection verified.' };
        return {};
    } };
    try {
        w.eval(await source('plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/timeline.js'));
        w.eval(await source('plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/admin.js'));
        await turn(); await turn();
        assert.equal(d.querySelector('#deletionQueueAdmin').getAttribute('aria-busy'), 'false');
        assert.equal(d.querySelector('#dqNotificationMode'), null); assert(!d.body.textContent.includes('Notification mode'));
        assert.equal(d.querySelector('#dqEnhancedStatus').textContent, ''); assert.equal(d.querySelector('#dqEnhanced').hidden, true);
        // A synthetic invocation still fails closed even though users cannot click it.
        d.querySelector('#dqEnhanced').click(); await turn(); assert.match(d.querySelector('#dqEnhancedStatus').textContent, /exactly one/); assert.equal(calls.filter(c => c.type !== 'GET').length, 0);
        status.enhanced.connectionAvailable = true; d.querySelector('#dqRefresh').click(); await turn(); await turn();
        assert.equal(d.querySelector('#dqEnhanced').hidden, false); assert.equal(d.querySelector('#dqEnhanced').disabled, false);
        status.enhanced.connectionAvailable = false; d.querySelector('#dqRefresh').click(); await turn(); await turn();
        assert.equal(d.querySelector('#dqEnhanced').hidden, true);
        const button = d.querySelector('#dqResetAllPermissions'); button.click(); await turn();
        assert.equal(calls.filter(c => c.type !== 'GET').length, 0);
        d.querySelector('#dqAuthority').value = '2'; confirmed = true; button.click(); await turn(); await turn();
        const resets = calls.filter(c => c.url.endsWith('/admin/users/reset-permissions'));
        assert.equal(resets.length, 1); assert.equal(resets[0].type, 'POST');
        const body = JSON.parse(resets[0].data); assert.equal(body.revision, 42); assert.match(body.commandId, /^[a-f0-9-]{36}$/);
        assert.deepEqual(Object.keys(body).sort(), ['commandId', 'revision']);
        assert.equal(d.querySelectorAll('.dq-policy-timeline > li').length, 5);
        assert.equal(d.querySelector('#dqDelayDays').value, '2');
        const writesBeforePreview = calls.filter(c => c.type !== 'GET').length;
        d.querySelector('#dqDelayDays').value = '3'; d.querySelector('#dqDelayDays').dispatchEvent(new w.Event('input'));
        assert.equal(d.querySelector('#dqHours').value, '72');
        for(const id of ['dqDelayPreview','dqLookbackPreview','dqExecutionPreview'])assert.equal(d.querySelector('#'+id),null);
        d.querySelector('#dqRequireAdminApproval').checked = true; d.querySelector('#dqRequireAdminApproval').dispatchEvent(new w.Event('input'));
        assert.match(d.querySelector('#dqApprovalPreview').textContent, /either eligibility path/);
        assert(d.querySelector('#dqPolicyTimeline [data-checkpoint=activity]'));assert(d.querySelector('#dqPolicyTimeline [data-checkpoint=consent]'));
        assert.match(d.querySelector('#dqPolicyTimeline .dq-flow-edge-label[data-to="approval-timeout"]').textContent,/30-day decision limit/);
        d.querySelector('#dqApprovalDays').value='45';d.querySelector('#dqApprovalDays').dispatchEvent(new w.Event('input'));
        assert.match(d.querySelector('#dqPolicyTimeline .dq-flow-edge-label[data-to="approval-timeout"]').textContent,/45-day decision limit/);
        assert.match(d.querySelector('#dqPolicyTimeline .dq-flow-edge-label[data-from=delay][data-to=removing]').textContent,/3 days \(72 hours\)/);
        assert.equal(d.querySelector('#dqActivityTimeoutDays').disabled,true);
        d.querySelector('#dqActivityTimeoutEnabled').checked=true;d.querySelector('#dqActivityTimeoutEnabled').dispatchEvent(new w.Event('input'));
        assert.equal(d.querySelector('#dqActivityTimeoutDays').disabled,false);
        assert.match(d.querySelector('#dqPolicyTimeline .dq-flow-edge-label[data-to="activity-timeout"]').textContent,/45-day waiting limit/);
        assert.equal(calls.filter(c => c.type !== 'GET').length, writesBeforePreview, 'Preview must not save policy or opt in');
        d.querySelector('#dqPolicy').dispatchEvent(new w.Event('submit', { cancelable: true })); await turn(); await turn();
        const savedPolicy = JSON.parse(calls.find(c => c.type === 'PATCH' && c.url.endsWith('/admin/policy')).data);
        assert.equal(savedPolicy.policy.delayHours, 72); assert.equal(savedPolicy.policy.activityDays, 30); assert.equal(savedPolicy.policy.requireAdministratorApproval, true);
        assert.equal(savedPolicy.policy.activityWaitTimeoutEnabled,true);assert.equal(savedPolicy.policy.activityWaitTimeoutDays,45);
        d.querySelector('#dqHubSettings').dispatchEvent(new w.Event('submit', { cancelable: true })); await turn(); await turn();
        const settings = calls.find(c => c.type === 'PUT'); assert(settings); assert(!('notificationMode' in JSON.parse(settings.data)));
        d.querySelector('#dqKey').value = 'draft-secret-must-not-be-tested'; d.querySelector('#dqTestConnection').click(); await turn();
        const testRequest = calls.find(c => c.url.endsWith('/seerr/test')); assert.equal(testRequest.dataType, 'json'); assert.deepEqual(JSON.parse(testRequest.data), {}); assert.match(d.querySelector('#dqConnectionResult').textContent, /saved connection/); assert.equal(d.querySelector('#dqKey').value, 'draft-secret-must-not-be-tested');
    } finally { dom.window.close(); }
});
// The source may come from either Git checkout convention. JavaScript's dot
// does not match CR, so /^import .*\n/ leaves imports behind on Windows CRLF.
const workspaceScript = code => code.replace(/^import[^\r\n]*\r?\n/, 'const mountSecurityPanel = () => () => {};\n')
    .replace('export function mountWorkspace', 'window.mountWorkspace = function');
notificationTest('Notification settings use native fields and erase unsaved secrets on cleanup', async () => {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom; window.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    const cleanup = await window.AccountHubNotificationSettings.mount(window.document.querySelector('main'), async path => path.includes('deliveries') ? { deliveries: [] } : { revision: 0, notificationDays: 90, routes: [] });
    [...window.document.querySelectorAll('button')].find(n => n.textContent === 'Add route').click(); await turn();
    assert.equal(window.document.querySelectorAll('textarea').length, 0);
    assert(window.document.querySelector('select.emby-select'));
    const secret = window.document.querySelector('input[type=password]'); secret.value = 'unsaved-test-secret'; cleanup(); assert.equal(secret.value, ''); assert.equal(window.document.querySelector('main').children.length, 0); dom.window.close();
});
notificationTest('Registered notification layout editor saves only layout with the current source revision', async () => {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom, writes = []; window.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    const cleanup = await window.AccountHubNotificationSettings.mount(window.document.querySelector('main'), async (path, method, body) => {
        if (method === 'PUT') { writes.push(body); return { status: 'updated' }; }
        if (path.endsWith('/types')) return { catalogs: [{ source: 'example-plugin', revision: 4, version: '1', types: [{ type: 'example.changed', name: 'Example changes', trigger: 'An example changes', recipients: 'explicit-users', layout: { density: 'compact', summaryLength: 140, expandable: true }, actions: [] }] }] };
        return path.includes('deliveries') ? { deliveries: [] } : { revision: 0, notificationDays: 90, routes: [] };
    });
    const panel = window.document.querySelector('details'); panel.open = true; panel.dispatchEvent(new window.Event('toggle')); await turn();
    const save = [...panel.querySelectorAll('button')].find(button => button.textContent === 'Save layout'); save.click(); await turn(); save.click(); await turn();
    assert.equal(writes[0].revision, 4); assert.equal(writes[1].revision, 5);
    assert.deepEqual(Object.keys(writes[0]).sort(), ['layout', 'revision', 'source', 'type']);
    assert.equal(writes[0].layout.summaryLength, 140); cleanup(); dom.window.close();
});
notificationTest('Notifications page does not remount during initial viewshow or discard an active editor', async () => {
    const html = await source('plugins/notifications/src/Jellyfin.Plugin.Notifications/Web/admin.html');
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const w = dom.window, page = w.document.querySelector('#notificationsAdmin'); let release, mounts = 0, cleanups = 0;
    w.ApiClient={getUrl:path=>path};w.NotificationCard={};w.NotificationStudio={};
    const gate = new Promise(resolve => { release = resolve; });
    w.AccountHubNotificationSettings = { mount: async () => { mounts++; await gate; return () => { cleanups++; }; } };
    w.eval(w.document.querySelector('script').textContent);
    page.dispatchEvent(new w.Event('viewshow')); page.dispatchEvent(new w.Event('viewshow')); assert.equal(mounts, 1);
    release(); await turn(); page.dispatchEvent(new w.Event('viewshow')); assert.equal(mounts, 1);
    page.dispatchEvent(new w.Event('viewhide')); assert.equal(cleanups, 1);
    page.dispatchEvent(new w.Event('viewshow')); await turn(); assert.equal(mounts, 2);
    page.dispatchEvent(new w.Event('viewhide')); dom.window.close();
});
notificationTest('Notification catalog opening during initial fetch shares the request and preserves drafts', async () => {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only' }); const w = dom.window; w.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    let release, reads = 0; const gate = new Promise(resolve => { release = resolve; });
    const mounted = w.AccountHubNotificationSettings.mount(w.document.querySelector('main'), async path => {
        if (path.endsWith('/types')) { reads++; await gate; return { catalogs: [{ source: 'example', version: '1', revision: 1, types: [{ type: 'example.changed', name: 'Example', trigger: 'Change', recipients: 'explicit-users', layout: { density: 'compact', summaryLength: 140, expandable: true }, actions: [] }] }] }; }
        return path.includes('deliveries') ? { deliveries: [] } : { revision: 0, routes: [], notificationDays: 90 };
    });
    await turn(); const panel = w.document.querySelector('details'); panel.open = true; panel.dispatchEvent(new w.Event('toggle')); await turn(); assert.equal(reads, 1);
    release(); const cleanup = await mounted; const length = panel.querySelector('input[type=number]'); length.value = '220';
    panel.open = false; panel.open = true; panel.dispatchEvent(new w.Event('toggle')); await turn(); assert.equal(reads, 1); assert.equal(length.value, '220');
    cleanup(); dom.window.close();
});
notificationTest('Opening the catalog before initial settings finish does not trigger a second render after startup', async () => {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only' }); const w = dom.window;
    w.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    let releaseSettings, reads = 0; const gate = new Promise(resolve => { releaseSettings = resolve; });
    const root = w.document.querySelector('main');
    const mounted = w.AccountHubNotificationSettings.mount(root, async path => {
        if (path === 'settings/notifications') { await gate; return { revision: 0, routes: [], notificationDays: 90 }; }
        if (path.endsWith('/types')) { reads++; return { catalogs: [{ source: 'example', version: '1', revision: 1, types: [{ type: 'example.changed', name: 'Example', trigger: 'Change', recipients: 'explicit-users', layout: { density: 'compact', summaryLength: 140, expandable: true }, actions: [] }] }] }; }
        return { deliveries: [] };
    });
    const panel = root.querySelector('details'); panel.open = true; panel.dispatchEvent(new w.Event('toggle')); await turn();
    const layoutSelect=()=>w.document.getElementById([...panel.querySelectorAll('label')].find(label=>label.textContent==='Layout').htmlFor);
    const density = layoutSelect(); assert(density); assert.equal(reads, 1);
    assert.equal(root.getAttribute('aria-busy'), 'true'); assert(density.matches(':disabled'));
    density.value = 'standard'; // Also prove DOM identity/draft preservation independently of disabled controls.
    releaseSettings(); const cleanup = await mounted;
    assert.equal(reads, 1); assert.equal(layoutSelect(), density); assert.equal(density.value, 'standard');
    assert.equal(root.getAttribute('aria-busy'), 'false'); assert(!density.matches(':disabled'));
    const reload = [...root.querySelectorAll('button')].find(button => button.textContent === 'Reload'); reload.click(); await turn(); await turn();
    assert.equal(reads, 2); assert.notEqual(layoutSelect(), density); // Explicit reload still discards drafts.
    cleanup(); dom.window.close();
});
notificationTest('Aborting a pending notification settings mount cannot clear the next editor', async () => {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only' }); const w = dom.window; w.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    const controller = new w.AbortController(), root = w.document.querySelector('main'); let release, reads = 0;
    const mounted = w.AccountHubNotificationSettings.mount(root, async () => { reads++; return await new Promise(resolve => { release = resolve; }); }, { signal: controller.signal });
    controller.abort(); assert.equal(root.children.length, 0); root.textContent = 'Replacement editor';
    release({ revision: 0, routes: [], notificationDays: 90 }); const cleanup = await mounted; cleanup();
    assert.equal(reads, 1); assert.equal(root.textContent, 'Replacement editor'); dom.window.close();
});
hubTest('Workspace navigation clears an open fresh-proof dialog without issuing a write', async () => {
    const dom = new JSDOM('<div class="accountHubPage"><p class="hubMessage"></p><nav class="hubTabs"></nav><section class="hubContent"></section></div>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom; const calls = [];
    window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    window.fetch = async (url, options) => { calls.push({ url, method: options.method }); return response(url.endsWith('/users?skip=0&limit=50&search=') ? { items: [], total: 0 } : { revision: 0, auditRetentionDays: 90, notificationRetentionDays: 90, stepUpLevelMirror: 'Destructive', securityAlertMode: 'native', passwordLoginPolicy: 'auto' }); };
    let code = await source('plugins/account-hub/src/AccountHub/Web/workspace.js');
    code = workspaceScript(code);
    window.eval(code);
    const root = window.document.querySelector('.accountHubPage'); window.mountWorkspace(root, { getUrl: path => 'https://example.test/' + path, accessToken: () => 'disposable-token' }); await turn(); await turn();
    [...root.querySelectorAll('[role=tab]')].find(n => n.textContent === 'Settings').click(); await turn(); await turn();
    [...root.querySelectorAll('button')].find(n => n.textContent === 'Save audit retention').click(); await turn();
    const proof = window.document.querySelector('dialog input[type=password]'); assert(proof); proof.value = '123456';
    root.dispatchEvent(new window.Event('viewhide')); await turn();
    assert.equal(proof.value, ''); assert.equal(window.document.querySelectorAll('dialog').length, 0); assert.equal(calls.filter(c => c.method === 'POST').length, 0); dom.window.close();
});
notificationTest('Installed studio saves priority through its visible native radio group', async () => {
    const acceptance = await source('scripts/notification-studio-acceptance.cjs');
    assert(!acceptance.includes("getByLabel('Default priority'"));
    assert(acceptance.includes("getByRole('group',{name:'Default priority',exact:true}).getByRole('radio',{name:'Low',exact:true}).check()"));
    assert(acceptance.includes("assert.equal(saved.defaults.importance,'low')"));
});
notificationTest('Installed category acceptance targets visible headings and retains saved priority and keyboard checks', async () => {
    const acceptance = await source('scripts/notification-category-acceptance.cjs');
    assert(!acceptance.includes("closest('details').open"));
    assert(acceptance.includes("getByRole('heading', { name, exact: true })"));
    for (const check of ['await important.check()', "important.press('ArrowRight')", "press('ArrowLeft')", 'document.activeElement', 'box.width >= 44', 'Could not restore disposable personal preferences']) assert(acceptance.includes(check));
});
queueTest('Installed queued context fixture invalidates shared lookup and checks concise status without timelines', async () => {
    const acceptance = await source('scripts/theme-acceptance.cjs');
    const queued = acceptance.slice(acceptance.indexOf('queuedMenu = true;'), acceptance.indexOf("phase = 'enhanced-deletion-views'"));
    const invalidate = queued.indexOf("window.dispatchEvent(new Event('suite-removal-changed'))");
    assert(invalidate >= 0 && invalidate < queued.indexOf("new MouseEvent('contextmenu'"));
    assert(queued.includes(".dq-timeline, .dq-flow-timeline, [aria-current=step]"));
    assert(queued.includes("getByRole('button', { name: 'Undo removal request', exact: true }).waitFor()"));
    assert(queued.includes("'Keep item (reject removal)'"));
});
queueTest('Installed journal acceptance checks current table records rather than the retired JSON viewer', async () => {
    const acceptance = await source('scripts/theme-acceptance.cjs');
    assert(acceptance.includes("journal.locator('#dqJournal table')"));
    assert(acceptance.includes("['Sequence', 'Time', 'Event', 'Actor']"));
    assert(acceptance.includes("journal.locator('#dqJournal tbody tr').count() > 0"));
    assert(!acceptance.includes("journal.locator('.suiteJsonTree')"));
});
hubTest('Installed diagnostic acceptance selects the table checkbox independently of inspect details', async () => {
    const acceptance = await source('scripts/diagnostics-acceptance.cjs');
    assert(acceptance.includes("panel().locator('tbody tr').filter"));
    assert(acceptance.includes("const record=row.locator('details')"));
    assert(acceptance.includes("row.getByRole('checkbox',{name:'Select error '+id,exact:true}).check()"));
    assert(acceptance.includes("assert.equal((await api('errors?id='+id)).items.length,0)"));
    assert(acceptance.includes('assert.equal(denied.disclosed,false)'));
});
hubTest('Permission dialog foreground inheritance outranks late native dialog rules without fixed colors', async () => {
    const css = await source('plugins/account-hub/src/AccountHub/Web/workspace.css');
    assert.match(css, /dialog\.dialog\.accountHubDialog\s*\{\s*color:\s*inherit;\s*font:\s*inherit;/);
    assert(!/\.accountHubDialog[^{}]*\{[^}]*color:\s*(?:#|rgb|white|black)/i.test(css));
});
hubTest('People select-all uses native checkbox markup and excludes administrators', async () => {
    const dom = new JSDOM('<div class="accountHubPage"><p class="hubMessage"></p><nav class="hubTabs"></nav><section class="hubContent"></section></div>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom; const writes = [];
    try {
        window.fetch = async (url, options) => {
            if (options.method === 'POST') writes.push(url);
            return response(url.includes('/users?') ? { items: [
                { id: 'admin', username: 'Administrator', administrator: true },
                { id: 'one', username: 'One', administrator: false },
                { id: 'two', username: 'Two', administrator: false }
            ], total: 3 } : url.endsWith('/dependencies') ? { items: [] } : { revision: 0 });
        };
        window.eval(workspaceScript(await source('plugins/account-hub/src/AccountHub/Web/workspace.js')));
        const root = window.document.querySelector('.accountHubPage');
        window.mountWorkspace(root, { getUrl: path => 'https://example.test/' + path, accessToken: () => 'test' });
        await turn(); await turn();
        const all = root.querySelector('[aria-label="Select all users on this page"]'); assert(all);
        for (const input of root.querySelectorAll('input[type=checkbox]')) {
            assert(input.classList.contains('emby-checkbox'));
            assert(input.closest('label')?.querySelector('.checkboxOutline'));
        }
        const admin = root.querySelector('[aria-label="Administrator"]'), one = root.querySelector('[aria-label="One"]'), two = root.querySelector('[aria-label="Two"]');
        assert(admin.disabled); all.click();
        assert(one.checked && two.checked && all.checked); assert(!admin.checked);
        one.click(); assert(all.indeterminate); assert(!all.checked);
        all.click(); assert(one.checked && two.checked && !all.indeterminate);
        all.click(); assert(!one.checked && !two.checked && !admin.checked);
        assert.equal(writes.length, 0);
    } finally { window.close(); }
});
hubTest('Public recovery erases bearer fragment and sends no Jellyfin credentials', async () => {
    const token = 'a'.repeat(43), calls = [];
    const dom = new JSDOM('<base href="https://example.test/suite/AccountHub/"><main class="accountHubPublic" data-page="reset"><p class="hubPublicStatus"></p><div class="hubPublicContent"></div></main>', { runScripts: 'outside-only', url: 'https://example.test/suite/AccountHub/reset#token=' + token });
    dom.window.localStorage.setItem('jellyfin_credentials', JSON.stringify({ Servers: [{ ManualAddress: 'https://example.test/suite', AccessToken: 'must-not-send' }] }));
    dom.window.fetch = async (url, options) => { calls.push({ url: String(url), options }); return response({ valid: false }); };
    dom.window.eval(await source('plugins/account-hub/src/AccountHub/Web/public.js')); await turn();
    assert.equal(dom.window.location.hash, ''); assert.equal(calls.length, 1);
    assert.equal(calls[0].options.credentials, 'omit'); assert.equal(calls[0].options.headers.Authorization, undefined);
    assert(!calls[0].url.includes(token)); assert.equal(JSON.parse(calls[0].options.body).token, token); dom.window.close();
});
hubTest('Missing optional Hub settings stay hidden without fetching unavailable providers', async () => {
    const dom = new JSDOM('<div class="accountHubPage"><p class="hubMessage"></p><nav class="hubTabs"></nav><section class="hubContent"></section></div>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom; const calls = [];
    const dependencies = ['security', 'discord', 'deletion-queue', 'private-syncplay'].map(id => ({ id, name: id, available: false, state: 'missing', reason: 'Optional provider is missing.', fix: 'Install and enable the compatible plugin, then restart Jellyfin.' }));
    window.fetch = async (url, options) => { calls.push(url); return response(url.endsWith('/dependencies') ? { items: dependencies } : url.includes('/users?') ? { items: [], total: 0 } : { revision: 0 }); };
    let code = await source('plugins/account-hub/src/AccountHub/Web/workspace.js'); code = workspaceScript(code); window.eval(code);
    const root = window.document.querySelector('.accountHubPage'); window.mountWorkspace(root, { getUrl: path => 'https://example.test/' + path, accessToken: () => 'test' }); await turn(); await turn();
    const security = [...root.querySelectorAll('[role=tab]')].find(n => n.textContent === 'Security'); assert(security.disabled); assert(security.hidden);
    for (const name of ['Discord', 'DeletionQueue', 'Private SyncPlay', 'Seerr']) assert(![...root.querySelectorAll('[role=tab]')].some(n => n.textContent === name));
    assert(!root.querySelector('#hub-dependency-deletion-queue')); assert(!root.querySelector('#hub-dependency-private-syncplay'));
    assert.equal([...root.querySelectorAll('[role=tab]')].find(n => n.textContent === 'People').disabled, false);
    assert.match(root.querySelector('.hubDependencyHealth').textContent, /Install and enable/); assert(!calls.some(path => path.includes('/domains/'))); dom.window.close();
});
discordTest('Discord transport settings stay enabled without Hub while unavailable command settings stay hidden', async () => {
    const html = await source('plugins/discord-bridge/src/Jellyfin.Plugin.DiscordBridge/Web/settings.html');
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/web/' }); const { window } = dom;
    window.ApiClient = { getUrl: path => 'https://example.test/' + path, accessToken: () => 'test' };
    window.fetch = async () => response({ revision: 0, settings: { enabled: false, publicServerUrl: '', guildId: '', allowedChannelIds: [], enableSelfServiceCommands: true, enableWatchPartyCommands: true }, tokenConfigured: false, state: 'disabled', authorization: 'Standalone native elevation', features: [
        { id: 'transport', name: 'Bot transport', available: true, reason: 'Independent', fix: '' },
        { id: 'identity', name: 'Account commands', available: false, reason: 'Account Hub is missing.', fix: 'Install Account Hub and restart Jellyfin.' },
        { id: 'watchparty', name: 'Party commands', available: false, reason: 'Account Hub and Private SyncPlay are required.', fix: 'Install the missing plugins and restart Jellyfin.' }
    ] });
    window.eval(window.document.querySelector('script').textContent); await turn(); await turn();
    assert.equal(window.document.querySelector('.bridgeEnabled').disabled, false); assert.equal(window.document.querySelector('.bridgeToken').disabled, false);
    assert.equal(window.document.querySelector('.bridgeSelfService').disabled, true); assert.equal(window.document.querySelector('.bridgeWatchParty').disabled, true);
    assert(window.document.querySelector('.bridgeSelfService').closest('.checkboxContainer').hidden);assert(window.document.querySelector('.bridgeWatchParty').closest('.checkboxContainer').hidden);
    assert(window.document.querySelector('[data-bridge-feature=identity]').hidden);assert(window.document.querySelector('[data-bridge-feature=watchparty]').hidden);assert(window.document.querySelector('.bridgeDependencyHealth table'));dom.window.close();
});
notificationTest('Library inbox controls remain native and independent when Discord is missing', async () => {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom,root=window.document.querySelector('main'),writes=[];window.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    const cleanup=await window.AccountHubNotificationSettings.mount(root,async(path,method,body)=>{
        if(path.endsWith('/library')){if(method==='PUT')writes.push(body);return{settings:{revision:2,enabled:false,episodes:true,movies:true,libraryIds:[]},health:{retained:0,pending:0,uncertainDiscord:0},libraries:[{id:'library-id',name:'<b>Not HTML</b>'}]};}
        if(path.endsWith('/types'))return{catalogs:[]};if(path.includes('deliveries'))return{deliveries:[]};return{revision:0,routes:[]};
    });
    try{const panel=[...root.querySelectorAll('section')].find(n=>n.textContent.includes('Library events'));assert.equal(panel.hidden,false);assert.equal(panel.querySelectorAll('input[type=checkbox]').length,3);assert.equal(panel.querySelector('select').options[0].textContent,'<b>Not HTML</b>');
        const save=[...panel.querySelectorAll('button')].find(n=>n.textContent==='Save library events');assert.equal(save.disabled,false);save.click();await turn();assert.equal(writes[0].revision,2);
    }finally{cleanup();dom.window.close();}
});
discordTest('Discord library controls load explicit scopes safely, without requiring public invitation URL', async () => {
    const html = await source('plugins/discord-bridge/src/Jellyfin.Plugin.DiscordBridge/Web/settings.html');
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/web/' }); const { window } = dom;
    const id = '9dbb15b7-7eea-445a-8e67-5bc8a85f0387';
    window.ApiClient = { getUrl: path => 'https://example.test/' + path, accessToken: () => 'test' };
    window.fetch = async () => response({ revision: 0, settings: { enabled: false, publicServerUrl: '', guildId: '', allowedChannelIds: ['1234567890'], libraryIds: [id], libraryChannelIds: ['1234567890'] }, features: [], state: 'disabled', libraryStatus: 'available', libraryFolders: [{ id, name: '<img onerror=alert(1)>' }], libraryCaptureOwner: 'Discord Bridge', libraryHealth: { pending: 2, uncertainDiscord: 1 } });
    window.eval(window.document.querySelector('script').textContent); await turn(); await turn();
    const folders = window.document.querySelector('.bridgeLibraryFolders'); assert.equal(folders.selectedOptions[0].value, id); assert.equal(folders.querySelector('img'), null);
    assert.equal(window.document.querySelector('.bridgeLibraryEnabled').disabled, false); assert.equal(window.document.querySelector('.bridgeUrl').required, false);
    assert.match(window.document.querySelector('.bridgeLibraryHealth').textContent, /uncertainDiscord/); dom.window.close();
});

async function workspaceFixture(t, lineEnding) {
    const dom = new JSDOM('<div class="accountHubPage"><p class="hubMessage"></p><nav class="hubTabs"></nav><section class="hubContent"></section></div>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    t.after(() => dom.window.close());
    const { window } = dom, calls = [];
    window.fetch = async (url, options) => {
        calls.push({ url, options });
        return response(url.endsWith('/dependencies') ? { items: [] } : url.includes('/users?') ? { items: [], total: 0 }
            : url.endsWith('/settings/notifications') ? { revision: 0, notificationDays: 90, routes: [] }
                : url.includes('/deliveries?') ? { deliveries: [] }
                    : { revision: 0, auditRetentionDays: 90, notificationRetentionDays: 90, stepUpLevelMirror: 'Off', securityAlertMode: 'native', passwordLoginPolicy: 'auto' });
    };
    const code = await source('plugins/account-hub/src/AccountHub/Web/workspace.js');
    window.eval(workspaceScript(lineEnding ? code.replace(/\r?\n/g, lineEnding) : code));
    const root = window.document.querySelector('.accountHubPage'), content = root.querySelector('.hubContent');
    window.mountWorkspace(root, { getUrl: path => 'https://example.test/' + path, accessToken: () => 'test' }); await turn(); await turn();
    const tab = async name => { [...root.querySelectorAll('[role=tab]')].find(n => n.textContent === name).click(); await turn(); await turn(); };
    return { window, root, content, calls, tab };
}

for (const navigation of ['tab switch', 'viewhide', 'viewdestroy']) {
    hubTest('Delayed Notifications script cannot mount after ' + navigation, async t => {
        const f = await workspaceFixture(t); await f.tab('Notifications');
        const script = f.window.document.querySelector('script[src$="/notifications/settings.js"]'); assert(script);
        if (navigation === 'tab switch') await f.tab('Settings'); else f.root.dispatchEvent(new f.window.Event(navigation));
        const expected = f.content.innerHTML;
        f.window.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
        script.dispatchEvent(new f.window.Event('load')); await turn(); await turn();
        assert.equal(f.content.innerHTML, expected);
        assert.equal(f.calls.filter(c => c.url.includes('/settings/notifications')).length, 0);
        if (navigation === 'tab switch') assert.equal(f.root.querySelector('[aria-selected=true]').textContent, 'Settings');
    });
}

hubTest('Late Notifications mount cleanup and stale requests cannot affect a newer tab', async t => {
    const f = await workspaceFixture(t);
    let release, panel, panelRequest, disposed = 0;
    const pending = new Promise(resolve => { release = resolve; });
    f.window.AccountHubNotificationSettings = { async mount(root, request) {
        panel = root; panelRequest = request; root.textContent = 'Pending Notifications mount';
        await pending; root.textContent = 'Late Notifications mount';
        return () => { disposed++; root.replaceChildren(); };
    } };
    await f.tab('Notifications'); await f.tab('Settings');
    assert.equal(panel.isConnected, false);
    const count = f.calls.length;
    await assert.rejects(panelRequest('settings/notifications', 'GET'), { name: 'AbortError' });
    assert.equal(f.calls.length, count);
    const secret = f.content.querySelector('input[type=password]'); secret.value = 'new-view-secret';
    release(); await turn(); await turn();
    assert.equal(disposed, 1);
    assert.equal(f.content.querySelector('h2').textContent, 'Account settings');
    assert.equal(secret.value, 'new-view-secret');
    await f.tab('People'); assert.equal(disposed, 1);
    assert.equal(secret.value, '');
});

hubTest('Navigation aborts a real pending Notifications mount and immediately erases its unsaved secret', async t => {
    const f = await workspaceFixture(t);
    f.window.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    const fetch = f.window.fetch; let release, signal;
    const pending = new Promise(resolve => { release = resolve; });
    f.window.fetch = async (url, options) => {
        if (url.includes('/deliveries?')) { signal = options.signal; await pending; }
        const result = await fetch(url, options); // Deliberately finishes even after abort, like a late transport completion.
        return url.endsWith('/settings/notifications') ? response({ revision: 0, notificationDays: 90, routes: [{ id: 'saved-route', source: '*', type: '*', channel: 'webhook', destination: '[configured]', adminOnly: true }] }) : result;
    };
    await f.tab('Notifications'); assert(signal);
    assert([...f.content.querySelectorAll('button')].find(n => n.textContent === 'Add route').matches(':disabled'));
    const secret = f.content.querySelector('input[type=password]'); assert(secret); secret.value = 'pending-notifications-secret';
    await f.tab('Settings'); assert.equal(signal.aborted, true); assert.equal(secret.value, ''); assert.equal(secret.isConnected, false);
    const expected = f.content.innerHTML;
    release(); await turn(); await turn();
    assert.equal(f.content.innerHTML, expected); assert.equal(secret.value, '');
    assert.equal(f.calls.filter(c => c.options.method !== 'GET').length, 0);
});

for (const lineEnding of ['\n', '\r\n']) for (const navigation of ['tab switch', 'viewhide', 'viewdestroy']) {
    hubTest('Unsaved Settings webhook secret is erased through retained reference on ' + navigation + (lineEnding === '\n' ? ' (LF)' : ' (CRLF)'), async t => {
        const f = await workspaceFixture(t, lineEnding); await f.tab('Settings');
        const secret = f.content.querySelector('input[type=password]'); assert(secret); secret.value = 'unsaved-webhook-secret';
        if (navigation === 'tab switch') await f.tab('People'); else f.root.dispatchEvent(new f.window.Event(navigation));
        assert.equal(secret.value, ''); assert.equal(secret.isConnected, false);
        assert.equal(f.calls.filter(c => c.options.method !== 'GET').length, 0);
    });
}

hubTest('Native creation override uses explicit fresh confirmation and exposes five-minute status', async t => {
    const f = await workspaceFixture(t), original = f.window.fetch; let enabled = false, sent;
    f.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    f.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    f.window.fetch = async (url, options) => {
        if (url.endsWith('/user-creation/native-override')) { sent = JSON.parse(options.body); enabled = sent.enabled; return response({ owner: 'native' }); }
        if (url.endsWith('/user-creation')) return response({ owner: enabled ? 'native' : 'hub', nativeOverrideUntil: enabled ? Date.now() + 300000 : null });
        return original(url, options);
    };
    await f.tab('Settings');
    [...f.content.querySelectorAll('button')].find(b => b.textContent === 'Enable native creation for five minutes').click(); await turn();
    assert.equal(sent, undefined);
    const dialog = f.window.document.querySelector('dialog'); assert(dialog);
    assert.equal(dialog.querySelector('input[type=text]'),null);assert(!dialog.textContent.includes('CONFIRM ACCOUNT HUB CHANGE'));
    dialog.querySelector('form').dispatchEvent(new f.window.Event('submit', { bubbles: true, cancelable: true })); await turn(); await turn();
    assert.equal(sent.enabled, true); assert.equal(sent.confirmation, 'CONFIRM ACCOUNT HUB CHANGE'); assert(sent.commandId);
    assert.match(f.content.textContent, /Hub onboarding is paused/);
    assert([...f.content.querySelectorAll('button')].some(b => b.textContent === 'End native override and resume Hub'));
});

queueTest('Deletion Queue retains native surfaces and scoped semantic selection tints', async t => {
    const css = await source('plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/integrated.css');
    assert.doesNotMatch(css.replace(/color:\s*inherit\s*;/g, ''), /(?:^|[;{])\s*(?:color|font-family|backdrop-filter)\s*:|::backdrop|prefers-color-scheme/mi);
    assert.match(css, /:where\(dialog\.dq-panel\)\s*\{\s*color:\s*inherit/);
    assert.match(css, /\[data-answer=allow\]\[aria-pressed=true\]/);
    assert.match(css, /\[data-answer=keep\]\[aria-pressed=true\]/);
    assert.doesNotMatch(css, /background(?:-color)?\s*:\s*#[\da-f]+/i);
    // jsdom does not resolve :where specificity/inherited computed color like a
    // browser. Verify inline skin priority here; Chromium covers class overrides.
    const dom = new JSDOM('<style>.dialog { background: rgb(10, 20, 30); color: rgb(230, 220, 210); border-radius: 9px; } .emby-button { background: rgb(40, 50, 60); color: rgb(210, 200, 190); border-radius: 7px; }</style><dialog class="dialog dq-panel" style="color:rgb(230, 220, 210)" open></dialog><button class="emby-button dq-card-action">Remove</button>', { url: 'https://example.test/web/' });
    t.after(() => dom.window.close());
    const style = dom.window.document.createElement('style'); style.textContent = css; dom.window.document.head.append(style);
    const panel = dom.window.getComputedStyle(dom.window.document.querySelector('dialog')), card = dom.window.getComputedStyle(dom.window.document.querySelector('button'));
    assert.equal(panel.backgroundColor, 'rgb(10, 20, 30)'); assert.equal(panel.color, 'rgb(230, 220, 210)'); assert.equal(panel.borderRadius, '1rem');
    assert.equal(card.backgroundColor, 'rgb(40, 50, 60)'); assert.equal(card.color, 'rgb(210, 200, 190)'); assert.equal(card.borderRadius, '7px');
    assert.equal(panel.position, 'fixed'); assert.equal(card.position, 'static');
});
queueTest('administrator backlog explains exact identity mismatches without rendering identifier text as HTML',async()=>{
    const html=await source('plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/admin.html');
    const dom=new JSDOM(html,{runScripts:'outside-only',url:'https://example.test/web/'}),w=dom.window;
    const status={mode:'test',storageAvailable:true,policy:{revision:1,defaultAuthority:1,activityDays:30,delayHours:48,accounts:{},requireAdministratorApproval:false},seerr:{baseUrl:'',keyConfigured:false},accounts:[],enhanced:{connectionAvailable:false},journal:[],administratorApprovalSnapshots:{},proposals:[{id:'fixture',target:{title:'Example'},status:'Blocked',reason:'TargetVerificationRequired',terminal:false,requiresAutomaticRuleValidation:false,lastCheckedAt:new Date().toISOString(),lastCheckEvidence:{targetVerified:false,targetDifferences:[{field:'Jellyfin item ID',expected:'<img src=x>',current:'current-id'}]}}]};
    w.ApiClient={getUrl:path=>path,ajax:async options=>options.url.endsWith('/admin/status')?status:options.url.endsWith('/settings')?{ok:true,data:{revision:0,hubFallbackAfterMinutes:10}}:options.url.endsWith('/dependencies')?{data:{items:[]}}:{}};
    try{
        w.eval(await source('plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/admin.js'));for(let i=0;i<8;i++)await turn();
        assert.match(w.document.querySelector('.dq-blocker').textContent,/Media identity changed/);
        const list=w.document.querySelector('[aria-label="Media identity mismatches"]');assert(list);assert.match(list.textContent,/Jellyfin item ID: saved <img src=x> → current current-id/);assert.equal(list.querySelector('img'),null);
        assert.match(w.document.querySelector('#dqBacklog').textContent,/Administrator approval \(if required\): not required/);
        assert.match(w.document.querySelector('#dqBacklog').textContent,/Automatic rule \(if required\): not required/);
    }finally{dom.window.close();}
});
