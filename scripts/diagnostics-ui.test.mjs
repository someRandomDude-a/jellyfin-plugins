import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
const {JSDOM}=createRequire(new URL('../plugins/private-syncplay/package.json',import.meta.url))('jsdom');
const code=await readFile(new URL('../shared/Web/admin-ui.js',import.meta.url),'utf8');
const id='11111111-1111-4111-8111-111111111111';
const settle=async()=>{for(let index=0;index<12;index++)await new Promise(setImmediate);};
async function fixture({owner='notifications',rowSource='notifications',denied=false}={}){
    const dom=new JSDOM('<main class="suiteAdmin" data-suite-plugin="'+owner+'"><div class="content-primary"></div></main>',{runScripts:'outside-only',url:'https://fixture.test/suite/web/index.html#/configurationpage?name=Notifications'}),w=dom.window,calls=[];
    w.ApiClient={accessToken:()=> 'native-session',getUrl:path=>'https://fixture.test/suite/'+path};
    w.fetch=async(url,options={})=>{if(!String(url).includes('/SuiteDiagnostics/'))return{ok:true,json:async()=>({})};calls.push({url,options});return{ok:!denied,json:async()=>({ok:!denied,data:{settings:{revision:3,minimumLevel:'info'},plugins:[{source:'notifications',page:'Notifications'},{source:'deletion-queue',page:'DeletionQueueAdmin'}],items:[{id,source:rowSource,level:'error',code:'native_denied',operation:'<img src=x onerror=alert(1)>',createdAt:new Date().toISOString(),frames:'Namespace.Method'}]}})};};
    w.eval(code);await settle();return{w,calls,root:w.document.querySelector('main'),close:()=>w.close()};
}
test('diagnostics are lazy, native styled and render searchable details as plain text without any secret fields',async()=>{
    const f=await fixture();try{
        assert.equal(f.calls.length,0);const panel=f.root.querySelector('.suiteDiagnostics');assert(panel);panel.open=true;await settle();
        assert.equal(f.calls.length,1);assert.equal(f.calls[0].options.credentials,'omit');assert(f.calls[0].options.headers.Authorization.includes('native-session'));
        assert(panel.textContent.includes('<img src=x onerror=alert(1)>'));assert.equal(panel.querySelectorAll('img,script').length,0);
        assert.equal(panel.querySelector('[aria-label="Error ID"]').maxLength,36);assert.match(panel.textContent,/10,000/);
        assert.equal(f.calls.filter(call=>call.options.method==='POST').length,0);
    }finally{f.close();}
});
test('exact error lookup redirects to the installed owning plugin native diagnostics view',async()=>{
    const f=await fixture({rowSource:'deletion-queue'});try{const panel=f.root.querySelector('.suiteDiagnostics');panel.querySelector('[aria-label="Error ID"]').value=id;panel.open=true;await settle();assert.match(f.w.location.hash,/name=DeletionQueueAdmin&suiteError=/);assert(f.w.location.hash.endsWith(id));}finally{f.close();}
});
test('clearing requires explicit confirmation and sends only selected IDs; recording level uses the shared revision',async()=>{
    const f=await fixture();try{
        const panel=f.root.querySelector('.suiteDiagnostics');panel.open=true;await settle();const find=text=>[...panel.querySelectorAll('button')].find(node=>node.textContent===text);
        find('Clear selected errors').click();await settle();assert.equal(f.calls.filter(call=>call.options.method==='POST').length,0);
        panel.querySelector('[data-diagnostic-id]').checked=true;panel.querySelector('[aria-label="Type CLEAR DIAGNOSTICS to clear selected errors"]').value='CLEAR DIAGNOSTICS';find('Clear selected errors').click();await settle();
        const clear=f.calls.find(call=>call.url.endsWith('/clear'));assert.deepEqual(JSON.parse(clear.options.body),{confirmation:'CLEAR DIAGNOSTICS',ids:[id]});
        panel.querySelector('[aria-label="Minimum recorded diagnostic level"]').value='debug';find('Save recording level').click();await settle();const settings=f.calls.find(call=>call.url.endsWith('/settings'));assert.deepEqual(JSON.parse(settings.options.body),{revision:3,minimumLevel:'debug'});
    }finally{f.close();}
});
test('denied diagnostics do not disclose rows or retry any application actions',async()=>{
    const f=await fixture({denied:true});try{const panel=f.root.querySelector('.suiteDiagnostics');panel.open=true;await settle();assert.match(panel.textContent,/administrator authorization is required/);assert.equal(panel.querySelectorAll('[data-diagnostic-id]').length,0);assert.equal(f.calls.length,1);assert.equal(f.calls[0].options.method,'GET');}finally{f.close();}
});
test('search submitted during the initial load is retained as a read-only lookup rather than silently dropped',async()=>{
    const f=await fixture({rowSource:'deletion-queue'});try{
        const original=f.w.fetch;let release,started=0;
        f.w.fetch=(...args)=>++started===1?new Promise(resolve=>{release=()=>original(...args).then(resolve);}):original(...args);
        const panel=f.root.querySelector('.suiteDiagnostics');panel.open=true;await settle();assert.equal(started,1);
        panel.querySelector('[aria-label="Error ID"]').value=id;
        panel.querySelector('form').dispatchEvent(new f.w.Event('submit',{bubbles:true,cancelable:true}));
        assert.equal(started,1);release();await settle();
        assert.equal(f.calls.length,2);assert.equal(new URL(f.calls[0].url).searchParams.get('id'),'');assert.equal(new URL(f.calls[1].url).searchParams.get('id'),id);
        assert.match(f.w.location.hash,/name=DeletionQueueAdmin&suiteError=/);assert.equal(f.calls.filter(call=>call.options.method==='POST').length,0);
    }finally{f.close();}
});
test('typing an ID while a list request is pending never turns that older list into an exact-ID owner redirect',async()=>{
    const f=await fixture({rowSource:'deletion-queue'});try{
        const original=f.w.fetch;let release;
        f.w.fetch=(...args)=>new Promise(resolve=>{release=()=>original(...args).then(resolve);});
        const panel=f.root.querySelector('.suiteDiagnostics');panel.open=true;await settle();panel.querySelector('[aria-label="Error ID"]').value=id;release();await settle();
        assert.equal(f.calls.length,1);assert.doesNotMatch(f.w.location.hash,/suiteError=/);
    }finally{f.close();}
});
test('diagnostic inputs and selects retain native Jellyfin label and focus contracts',async()=>{
    const f=await fixture();try{
        for(const control of f.root.querySelectorAll('.suiteDiagnostics input:not([type=checkbox]),.suiteDiagnostics select')){
            assert(control.id,'Native controls need stable IDs');assert.equal(control.labels.length,1,'Native upgrades need their associated label');
            assert.equal(control.labelElement,control.labels[0]);assert(control.closest('.inputContainer,.selectContainer'));
        }
    }finally{f.close();}
});
test('returning to a cached owning plugin view applies the newly searched error ID',async()=>{
    const f=await fixture();try{
        const panel=f.root.querySelector('.suiteDiagnostics');panel.open=true;await settle();
        f.root.dispatchEvent(new f.w.Event('viewhide'));f.w.location.hash='#/configurationpage?name=Notifications&suiteError='+id;
        f.root.dispatchEvent(new f.w.Event('viewshow'));await settle();
        assert.equal(panel.querySelector('[aria-label="Error ID"]').value,id);
        assert.equal(new URL(f.calls.at(-1).url).searchParams.get('id'),id);
    }finally{f.close();}
});
