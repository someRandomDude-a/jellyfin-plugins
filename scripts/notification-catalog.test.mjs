import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
const { JSDOM } = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url))('jsdom');
const settle = async () => { for (let index=0;index<6;index++) await new Promise(setImmediate); };
const definition = (type, name) => ({ type, name, trigger: 'Reported trigger', recipients: 'explicit-users', actions: [], layout: { density: 'compact', summaryLength: 140, expandable: true } });
async function fixture(catalogs) {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only', url: 'https://fixture.test/web/' }), w=dom.window, root=w.document.querySelector('main'), writes=[];
    for (const name of ['notification-card.js','notification-studio.js','settings.js']) w.eval(await readFile(new URL('../modules/notifications/src/AccountHub.Notifications/Web/'+name,import.meta.url),'utf8'));
    const cleanup = await w.AccountHubNotificationSettings.mount(root,async(path,method='GET',body)=>{
        if(method==='PUT'&&path.includes('/types/')){const catalog=catalogs.find(c=>c.source===body.source);assert.equal(body.revision,catalog.revision);catalog.revision++;writes.push(JSON.parse(JSON.stringify(body)));return{};}
        if(path.endsWith('/types'))return{catalogs:JSON.parse(JSON.stringify(catalogs))};
        if(path.includes('/deliveries'))return{deliveries:[]};
        if(path.endsWith('/push'))return{enabled:false,revision:0,defaultOn:false};
        if(path.endsWith('/library'))return{settings:{revision:0,enabled:false,episodes:true,movies:true,libraryIds:[]},libraries:[],health:{retained:0,pending:0,uncertainDiscord:0}};
        return{revision:0,notificationDays:90,routes:[]};
    });
    return {w,root,writes,close(){cleanup();w.close();}};
}
test('registered notifications navigate by registering plugin and exact reported type, including duplicate names',async()=>{
    const f=await fixture([
        {source:'notifications',revision:4,types:[definition('library.movie.added','New movies'),definition('library.episode.added','New episodes')]},
        {source:'deletion-queue',revision:7,types:[definition('proposal.updated','New movies')]},
        {source:'external-plugin',revision:1,types:[]},
        {source:'__proto__',revision:1,types:[definition('custom.event','<img src=x onerror=alert(1)>')]}
    ]);
    try{
        const plugins=[...f.root.querySelectorAll('.notificationPluginCatalog')];
        assert.deepEqual(plugins.map(node=>node.dataset.notificationSource),['__proto__','deletion-queue','external-plugin','notifications']);
        const own=plugins.find(node=>node.dataset.notificationSource==='notifications');
        assert.match(own.querySelector('summary').textContent,/Notifications.*notifications.*2 types/);
        assert.deepEqual([...own.querySelectorAll('.notificationRegisteredType')].map(node=>node.dataset.notificationType),['library.episode.added','library.movie.added']);
        for(const plugin of plugins){assert.equal(plugin.open,false);for(const entry of plugin.querySelectorAll('.notificationRegisteredType')){assert.equal(entry.open,false);assert.equal(entry.querySelector('summary code').textContent,entry.dataset.notificationType);assert(entry.querySelector('.notificationTypeSection'));}}
        assert.match(plugins.find(node=>node.dataset.notificationSource==='external-plugin').textContent,/No notification types reported/);
        assert(f.root.textContent.includes('<img src=x onerror=alert(1)>'));assert.equal(f.root.querySelectorAll('img,script').length,0);
        assert.equal(f.writes.length,0,'Building or navigating the catalog must not change settings');
    }finally{f.close();}
});
test('collapsing plugin/type navigation retains studio drafts and saves to the exact registration and shared revision',async()=>{
    const f=await fixture([{source:'notifications',revision:4,types:[definition('library.movie.added','Same name'),definition('library.episode.added','Same name')]},{source:'deletion-queue',revision:9,types:[definition('proposal.updated','Same name')]}]);
    try{
        const plugin=f.root.querySelector('[data-notification-source="notifications"]'),entry=plugin.querySelector('[data-notification-type="library.movie.added"]'),studio=entry.querySelector('details:not(.notificationRegisteredType)');
        plugin.open=true;entry.open=true;studio.open=true;studio.dispatchEvent(new f.w.Event('toggle'));
        const field=(root,label)=>root.querySelector('#'+[...root.querySelectorAll('label')].find(node=>node.textContent===label).htmlFor);
        const height=field(entry,'Card height');height.value='240';height.dispatchEvent(new f.w.Event('change'));
        const board=entry.querySelector('.notificationStudio');assert.equal(board.dataset.dirty,'true');
        entry.open=false;plugin.open=false;await settle();plugin.open=true;entry.open=true;await settle();
        assert.equal(field(entry,'Card height'),height);assert.equal(height.value,'240');assert.equal(board.dataset.dirty,'true');assert.equal(f.writes.length,0);
        const click=async(root,label)=>{[...root.querySelectorAll('button')].find(node=>node.textContent===label).click();await settle();};
        await click(entry,'Save layout');assert.equal(f.writes[0].source,'notifications');assert.equal(f.writes[0].type,'library.movie.added');assert.equal(f.writes[0].revision,4);assert.equal(f.writes[0].layout.canvas.height,240);
        const sibling=plugin.querySelector('[data-notification-type="library.episode.added"]');await click(sibling,'Save defaults');assert.equal(f.writes[1].revision,5);assert.equal(f.writes[1].type,'library.episode.added');
        const other=f.root.querySelector('[data-notification-source="deletion-queue"]');await click(other,'Save defaults');assert.equal(f.writes[2].source,'deletion-queue');assert.equal(f.writes[2].revision,9);
    }finally{f.close();}
});
test('empty registration catalog has a clear empty state',async()=>{
    const f=await fixture([]);try{assert.equal(f.root.querySelectorAll('.notificationPluginCatalog').length,0);assert.match(f.root.textContent,/No plugin notification types registered yet/);}finally{f.close();}
});
