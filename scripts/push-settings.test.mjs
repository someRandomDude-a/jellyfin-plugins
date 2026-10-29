import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
const require=createRequire(new URL('../plugins/private-syncplay/package.json',import.meta.url));
const { JSDOM }=require('jsdom');
const script=await readFile(new URL('../modules/notifications/src/AccountHub.Notifications/Web/settings.js',import.meta.url),'utf8');
const tick=()=>new Promise(resolve=>setTimeout(resolve,15));
async function fixture(fail=false){
    const dom=new JSDOM('<main></main>',{runScripts:'outside-only'}),root=dom.window.document.querySelector('main'),calls=[];
    let enabled=false,revision=0,defaultOn=false;
    dom.window.eval(script);
    await dom.window.AccountHubNotificationSettings.mount(root,async(path,method,body)=>{
        calls.push({path,method,body});
        if(path.endsWith('/push')){
            if(method==='PUT'){if(fail)throw Error('offline');assert.equal(body.revision,revision);enabled=body.enabled;defaultOn=body.defaultOn;revision++;}
            return{enabled,revision,defaultOn};
        }
        if(path.includes('/deliveries'))return{deliveries:[]};
        if(path.endsWith('/types'))return{catalogs:[]};
        return{revision:0,routes:[],notificationDays:90};
    });
    return{dom,root,calls};
}
test('browser notifications require only an autosaved toggle, never contact or URL fields',async()=>{
    const f=await fixture();try{
        const panel=[...f.root.querySelectorAll('section')].find(e=>e.textContent.includes('Browser notifications'));
        const toggle=panel.querySelector('input');assert.equal(toggle.type,'checkbox');assert.equal(toggle.disabled,false);
        assert.equal(panel.querySelectorAll('input').length,2);assert.equal(panel.querySelectorAll('input:not([type=checkbox])').length,0);assert.equal(panel.querySelectorAll('button').length,0);
        toggle.checked=true;toggle.dispatchEvent(new f.dom.window.Event('change'));await tick();
        const write=f.calls.find(c=>c.method==='PUT');assert.deepEqual(Object.keys(write.body).sort(),['defaultOn','enabled','revision']);assert.equal(write.body.enabled,true);
        const defaults=panel.querySelectorAll('input')[1];defaults.checked=true;defaults.dispatchEvent(new f.dom.window.Event('change'));await tick();assert.equal(f.calls.filter(c=>c.method==='PUT').at(-1).body.defaultOn,true);assert.equal(defaults.checked,true);
        assert.match(panel.textContent,/Ready/);assert.equal(toggle.checked,true);
    }finally{f.dom.window.close();}
});
test('a failed automatic save restores the real saved toggle state',async()=>{
    const f=await fixture(true);try{const toggle=[...f.root.querySelectorAll('section')].find(e=>e.textContent.includes('Browser notifications')).querySelector('input');toggle.checked=true;toggle.dispatchEvent(new f.dom.window.Event('change'));await tick();assert.equal(toggle.checked,false);assert.equal(toggle.disabled,false);assert.match(toggle.closest('section').textContent,/Could not save/);}finally{f.dom.window.close();}
});
