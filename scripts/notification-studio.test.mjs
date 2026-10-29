import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
const { JSDOM } = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url))('jsdom');
async function fixture() {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only', url: 'https://fixture.test/web/' });
    for (const name of ['notification-card.js', 'notification-studio.js']) dom.window.eval(await readFile(new URL('../modules/notifications/src/AccountHub.Notifications/Web/' + name, import.meta.url), 'utf8'));
    return { dom, w: dom.window, root: dom.window.document.querySelector('main') };
}
test('notification preview renders plain placeholders and allowlisted styles without scripts or requests', async () => {
    const { dom, w, root } = await fixture();
    try {
        w.fetch = () => { throw Error('Preview must not send'); };
        const canvas = w.NotificationCard.defaults(); canvas.layers[1].text = '{title} <script>not executable</script>'; canvas.layers[1].fontFamily = 'url(https://external.test)';
        w.NotificationCard.render(root, { title: '<img src=x onerror=alert(1)>', body: 'Example', source: 'fixture', type: 'example' }, { canvas, simulated: true });
        assert(root.textContent.includes('<script>not executable</script>')); assert.equal(root.querySelectorAll('script,img').length, 0);
        assert.equal(root.querySelector('[data-layer=title]').style.fontFamily, 'inherit'); assert(root.textContent.includes('Media image'));
    } finally { w.close(); }
});
test('default poster stays left of both text layers and new text starts in the text column',async()=>{
    const {w,root}=await fixture();let current;
    try{
        const defaults=w.NotificationCard.defaults(),poster=defaults.layers.find(layer=>layer.kind==='image'),text=defaults.layers.filter(layer=>layer.kind!=='image');
        assert.equal(poster.x,0);assert.equal(poster.fit,'contain');
        for(const layer of text){assert(layer.x>poster.x+poster.width);assert(layer.x+layer.width<=100);}
        assert(text[0].y+text[0].height<text[1].y);
        const dispose=w.NotificationStudio.mount(root,{title:'Example',body:'Preview'},null,value=>{current=value;});
        const untouched=dispose.getCanvas();assert.equal(untouched.layers[0].x,0);untouched.layers[0].x=50;assert.equal(dispose.getCanvas().layers[0].x,0,'Save snapshots must not share mutable model objects');
        for(const kind of ['title','body','text']){
            [...root.querySelectorAll('button')].find(button=>button.textContent==='Add '+kind).click();
            const added=current.layers.at(-1);assert(added.x>poster.x+poster.width,'New '+kind+' must not cover the default poster');
        }
        [...root.querySelectorAll('button')].find(button=>button.textContent==='Reset studio').click();assert.equal(current.layers[0].x,0);assert.equal(current.layers[0].fit,'contain');
        dispose();assert.equal(dispose.getCanvas(),null);
    }finally{w.close();}
});
test('live canvas posters load for Notifications and legacy Hub only, retain native access checks and keep time outside the card',async()=>{
    const card=await readFile(new URL('../modules/notifications/src/AccountHub.Notifications/Web/notification-card.js',import.meta.url),'utf8');
    const bell=await readFile(new URL('../modules/notifications/src/AccountHub.Notifications/Web/bell.js',import.meta.url),'utf8');
    const settle=async()=>{for(let index=0;index<8;index++)await new Promise(setImmediate);};
    for(const [source,denied,mismatch,expected] of [['notifications',false,false,1],['account-hub',false,false,1],['discord-bridge',false,false,0],['notifications',true,false,0],['notifications',false,true,0]]){
        const dom=new JSDOM('<div data-notifications-bootstrap data-notifications-api="Notifications/api/v1/"></div><header><button class="headerUserButton">User</button></header>',{runScripts:'outside-only',pretendToBeVisual:true,url:'https://fixture.test/jellyfin/web/index.html#/home'}),w=dom.window,images=[];
        try{
            w.document.querySelector('.headerUserButton').getClientRects=()=>[{}];w.MutationObserver=class{observe(){}};w.WebSocket=undefined;
            w.URL.createObjectURL=()=> 'blob:https://fixture.test/poster';w.URL.revokeObjectURL=()=>{};
            w.ApiClient={accessToken:()=> 'fixture-session',getCurrentUserId:()=> 'owner',getUrl:path=>new URL(path,'https://fixture.test/jellyfin/').href};
            w.eval(card);
            const item='11111111-1111-1111-1111-111111111111',row={id:'notice',read:false,event:{eventId:'22222222-2222-2222-2222-222222222222',source,type:'library.movie.added',title:'Movie',body:'Available',occurredAt:new Date().toISOString(),metadata:{itemId:item,imageItemId:mismatch?'33333333-3333-3333-3333-333333333333':item},layout:{canvas:w.NotificationCard.defaults()}}};
            w.fetch=async(url,options)=>{
                if(url.includes('/Images/Primary')){images.push({url,options});return{ok:!denied,headers:{get:()=> 'image/png'},blob:async()=>new w.Blob(['poster'],{type:'image/png'})};}
                return{status:200,ok:true,headers:{get:()=> 'tag'},json:async()=>({ok:true,data:url.endsWith('unread-count')?{count:1}:{hasMore:false,notifications:[row]}})};
            };
            w.eval(bell);await settle();w.document.querySelector('[data-account-hub-bell]').click();await settle();
            const main=w.document.querySelector('.notificationMainCanvas');assert(main);assert.equal(main.querySelectorAll('.notificationCanvasImage img').length,expected,source+'/'+denied+'/'+mismatch);
            assert.equal(main.lastElementChild.tagName,'TIME');assert.equal(main.querySelector('.notificationCanvas time'),null);
            if(expected){const image=main.querySelector('img');assert.equal(image.style.objectFit,'contain');assert.equal(image.parentElement.style.left,'0%');}
            for(const image of images){assert(image.options.headers.Authorization.includes('fixture-session'));assert(!new URL(image.url).search.includes('token'));}
            if(mismatch||source==='discord-bridge')assert.equal(images.length,0);
        }finally{w.close();}
    }
});
test('notification studio edits geometry/fonts, supports keyboard and undo/redo, and cleans up on navigation', async () => {
    const { w, root } = await fixture(); let current, changes = 0;
    try {
        const dispose = w.NotificationStudio.mount(root, { title: 'Example', body: 'Only simulated' }, null, value => { current = value; changes++; });
        const button = text => [...root.querySelectorAll('button')].find(b => b.textContent === text);
        const field = label => root.querySelector('#' + [...root.querySelectorAll('label')].find(l => l.textContent === label).htmlFor);
        const change = (label, value) => { const input = field(label); input.value = value; input.dispatchEvent(new w.Event('change')); };
        root.querySelector('[data-layer=title]').dispatchEvent(new w.KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
        assert.equal(current.layers.find(l => l.id === 'title').x, 29); assert.equal(w.document.activeElement.dataset.layer, 'title');
        const font=field('Font family');font.focus();change('Font family', 'serif'); assert.equal(current.layers.find(l => l.id === 'title').fontFamily, 'serif');
        assert.equal(field('Font family'),font,'Editing keeps the native input node and Tab order');assert.equal(w.document.activeElement,font);
        change('Font family','inherit');assert.equal(current.layers.find(l=>l.id==='title').fontFamily,'inherit');
        change('Left (%)', 90); assert(current.layers.find(l => l.id === 'title').x <= 32);
        button('Add text').click(); assert.equal(current.layers.length, 4);
        const shortcut = key => root.dispatchEvent(new w.KeyboardEvent('keydown', { key, ctrlKey: true, bubbles: true }));
        shortcut('z'); assert.equal(current.layers.length, 3); shortcut('y'); assert.equal(current.layers.length, 4);
        assert.equal(button('Undo'),undefined);assert.equal(button('Redo'),undefined);
        const stale = button('Add image'); const before = changes; dispose(); stale.click(); assert.equal(changes, before); assert.equal(root.children.length, 0);
    } finally { w.close(); }
});
test('layer selection is not an edit and studio history shortcuts leave native text editing alone',async()=>{
    const {w,root}=await fixture();let current,changes=0;
    try{
        const dispose=w.NotificationStudio.mount(root,{title:'Example',body:'Preview'},null,value=>{current=value;changes++;});
        const field=label=>[...root.querySelectorAll('[data-studio-field]')].find(input=>input.dataset.studioField===label);
        const choose=(label,value)=>{const input=field(label);input.value=value;input.dispatchEvent(new w.Event('change'));};
        choose('Selected layer','image');assert.equal(changes,0);assert.equal(root.dataset.dirty,'false');
        choose('Selected layer','title');assert.equal(changes,0);
        root.querySelector('[data-layer=title]').dispatchEvent(new w.KeyboardEvent('keydown',{key:'ArrowLeft',bubbles:true}));
        assert.equal(current.layers[1].x,29);assert.equal(root.dataset.dirty,'true');
        const text=field('Text template'),nativeUndo=new w.KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true,cancelable:true});text.dispatchEvent(nativeUndo);
        assert.equal(nativeUndo.defaultPrevented,false);assert.equal(changes,1);
        root.dispatchEvent(new w.KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true}));assert.equal(current.layers[1].x,30);assert.equal(root.dataset.dirty,'false');
        root.dispatchEvent(new w.KeyboardEvent('keydown',{key:'y',ctrlKey:true,bubbles:true}));assert.equal(current.layers[1].x,29);
        const saved=structuredClone(current);dispose.markSaved(saved);assert.equal(root.dataset.dirty,'false');
        root.querySelector('[data-layer=title]').dispatchEvent(new w.KeyboardEvent('keydown',{key:'ArrowRight',shiftKey:true,bubbles:true}));
        assert.equal(current.layers[1].x,32);dispose.markSaved(saved);assert.equal(root.dataset.dirty,'true','Completing an older save must not mark newer edits saved');
        dispose();
    }finally{w.close();}
});
test('studio resizing is bounded, cancellation restores the design, and clicks do not manufacture edits',async()=>{
    const {w,root}=await fixture();let current,changes=0;
    try{
        const dispose=w.NotificationStudio.mount(root,{title:'Example',body:'Preview'},null,value=>{current=value;changes++;});
        const pointer=(target,type,x,y)=>{const event=new w.MouseEvent(type,{button:0,clientX:x,clientY:y,bubbles:true,cancelable:true});Object.defineProperty(event,'pointerId',{value:1});target.dispatchEvent(event);};
        const canvas=()=>root.querySelector('.notificationCanvas');
        canvas().getBoundingClientRect=()=>({width:600,height:180});
        let title=root.querySelector('[data-layer=title]');pointer(title,'pointerdown',200,30);pointer(title,'pointerup',200,30);assert.equal(changes,0);
        let handle=root.querySelector('.notificationStudioResize');pointer(handle,'pointerdown',300,70);pointer(handle,'pointermove',360,88);pointer(handle,'pointerup',360,88);
        assert.equal(changes,1);assert.equal(current.layers[1].width,70);assert.equal(current.layers[1].height,38);
        root.dispatchEvent(new w.KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true}));assert.equal(current.layers[1].width,68);
        canvas().getBoundingClientRect=()=>({width:600,height:180});title=root.querySelector('[data-layer=title]');
        const before=changes;pointer(title,'pointerdown',200,30);pointer(title,'pointermove',260,50);pointer(title,'pointercancel',260,50);
        assert.equal(changes,before);assert.equal(root.querySelector('[data-layer=title]').style.left,'30%');
        canvas().getBoundingClientRect=()=>({width:600,height:180});handle=root.querySelector('.notificationStudioResize');
        pointer(handle,'pointerdown',300,70);pointer(handle,'pointermove',9999,9999);pointer(handle,'pointerup',9999,9999);
        assert.equal(current.layers[1].width,70);assert.equal(current.layers[1].height,94);
        const stale=handle;dispose();pointer(stale,'pointermove',100,100);assert.equal(root.children.length,0);
    }finally{w.close();}
});
test('layer tools retain a bounded editable model and disable unavailable operations',async()=>{
    const {w,root}=await fixture();let current;
    try{
        w.NotificationStudio.mount(root,{title:'Example',body:'Preview'},null,value=>{current=value;});
        const button=name=>[...root.querySelectorAll('button')].find(control=>control.textContent===name);
        button('Duplicate layer').click();assert.equal(current.layers.length,4);assert.notEqual(current.layers[1].id,current.layers[3].id);
        for(let index=0;index<20;index++)button('Add text').click();assert.equal(current.layers.length,12);assert.equal(button('Add image').disabled,true);assert.equal(button('Duplicate layer').disabled,true);
        for(let index=0;index<20;index++)button('Remove layer').click();assert.equal(current.layers.length,1);assert.equal(button('Remove layer').disabled,true);
        assert.equal(button('Move layer forward').disabled,true);assert.equal(button('Move layer backward').disabled,true);
    }finally{w.close();}
});
test('blank or out-of-range inspector numbers do not save invalid geometry',async()=>{
    const {w,root}=await fixture();let changes=0;
    try{
        const dispose=w.NotificationStudio.mount(root,{title:'Example',body:'Preview'},null,()=>changes++);
        for(const [label,value] of [['Width (%)',''],['Card height','0'],['Font size','99'],['Left (%)','-1']]){
            const field=[...root.querySelectorAll('[data-studio-field]')].find(input=>input.dataset.studioField===label);
            field.value=value;field.dispatchEvent(new w.Event('change'));assert.equal(changes,0,label);
        }
        assert.equal(root.dataset.dirty,'false');
        assert.equal(dispose.validate(),false,'Invalid inspector text must prevent a layout save');
    }finally{w.close();}
});
test('notification studio binds native control labels before Jellyfin value setters run',async()=>{
    const {w,root}=await fixture();
    try{
        for(const prototype of [w.HTMLInputElement.prototype,w.HTMLSelectElement.prototype]){
            const value=Object.getOwnPropertyDescriptor(prototype,'value');
            Object.defineProperty(prototype,'value',{...value,set(next){if(this.getAttribute('is')?.startsWith('emby-')){assert(this.labelElement,'Native control needs its label before value assignment');this.labelElement.htmlFor=this.id;}value.set.call(this,next);}});
        }
        const dispose=w.NotificationStudio.mount(root,{title:'Native label fixture',body:'Example'},null,()=>{});
        for(const input of root.querySelectorAll('input,select'))assert.equal(input.labelElement.htmlFor,input.id);
        dispose();
    }finally{w.close();}
});
