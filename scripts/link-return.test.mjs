import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
const {JSDOM}=createRequire(new URL('../plugins/private-syncplay/package.json',import.meta.url))('jsdom');
const code=await readFile(new URL('../plugins/account-hub/src/AccountHub/Web/link-return.js',import.meta.url),'utf8');
const settle=async()=>{for(let i=0;i<10;i++)await new Promise(setImmediate);};
async function fixture({marker=true,address='https://fixture.test/suite',token='native-session',status=200,result={ok:true,data:{token:'a'.repeat(43)}},network=false,route,storedIntent}={}){
    const dom=new JSDOM('<body><script src="https://fixture.test/suite/AccountHub/assets/link-return.js"></script></body>',{runScripts:'outside-only',url:'https://fixture.test/suite/web/index.html#/home'+(marker?'?accountHubLinkReturn=1':'')}),w=dom.window,calls=[],timers=[];
    Object.defineProperty(w.document,'currentScript',{value:w.document.querySelector('script')});w.ApiClient={accessToken:()=>token,serverAddress:()=>address};if(route)w.history.replaceState(null,'',route);if(storedIntent)for(const [key,value]of Object.entries(storedIntent))w.sessionStorage.setItem(key,value);
    w.setTimeout=fn=>{timers.push(fn);return timers.length;};w.clearTimeout=()=>{};let interval;w.setInterval=fn=>{interval=fn;return 1;};w.clearInterval=()=>{};
    w.fetch=async(url,options)=>{calls.push({url:String(url),options});if(network)throw Error('private-network-error');return{ok:status===200,status,headers:{get:()=>null},json:async()=>result};};
    w.eval(code);await settle();return{w,calls,timers,check:()=>interval(),close:()=>w.close()};
}
test('native return marker resumes only the cookie-bound review on this server without browser challenge storage',async()=>{
    const f=await fixture();try{assert.equal(f.calls.length,1);const call=f.calls[0];assert.equal(call.url,'https://fixture.test/suite/AccountHub/api/v1/discord/link/continuation');assert.equal(call.options.credentials,'same-origin');assert.equal(call.options.headers.Authorization,'MediaBrowser Token="native-session"');assert.equal(call.options.body,'{}');assert.equal(f.w.localStorage.length,0);assert.equal(f.w.sessionStorage.length,0);assert(!f.calls.some(call=>call.url.endsWith('/confirm')));assert.match(code,/location\.replace\(new URL\('AccountHub\/discord\/link#token='/);}finally{f.close();}
});
test('native history navigation without hashchange discovers the credential-free return marker',async()=>{
    const f=await fixture({marker:false});try{assert.equal(f.calls.length,0);f.w.history.pushState(null,'','#/home?accountHubLinkReturn=1');f.check();await settle();assert.equal(f.calls.length,1);f.check();await settle();assert.equal(f.calls.length,1);}finally{f.close();}
});
test('bubbling Jellyfin pagehide events do not terminate native continuation',async()=>{
    const f=await fixture({marker:false});try{const view=f.w.document.createElement('div');f.w.document.body.append(view);view.dispatchEvent(new f.w.CustomEvent('pagehide',{bubbles:true}));f.w.history.pushState(null,'','#/home?accountHubLinkReturn=1');f.check();await settle();assert.equal(f.calls.length,1);}finally{f.close();}
});
test('a real page transition stops subsequent marker handling',async()=>{
    const f=await fixture({marker:false});try{f.w.dispatchEvent(new f.w.PageTransitionEvent('pagehide'));f.w.history.pushState(null,'','#/home?accountHubLinkReturn=1');f.check();await settle();assert.equal(f.calls.length,0);}finally{f.close();}
});
test('native login records only expiring tab-local intent, then resumes after Security drops the URL marker',async()=>{
    const f=await fixture({marker:false,route:'#/login?url=home%3FaccountHubLinkReturn%3D1'});try{
        assert.equal(f.calls.length,0);assert.equal(f.w.localStorage.length,0);assert.equal(f.w.sessionStorage.length,1);const expiry=Number(f.w.sessionStorage.getItem('accountHub.linkReturnIntent:/suite/'));assert(expiry>Date.now()&&expiry<=Date.now()+300000);
        assert.doesNotMatch(JSON.stringify(Object.values(f.w.sessionStorage)),/native-session|aaaaa/);
        f.w.history.pushState(null,'','#/home');f.check();await settle();assert.equal(f.calls.length,1);assert.equal(f.w.sessionStorage.length,0);
    }finally{f.close();}
});
test('post-factor native web reload can resume valid intent without a URL token or marker',async()=>{
    const f=await fixture({marker:false,storedIntent:{'accountHub.linkReturnIntent:/suite/':String(Date.now()+60000)}});try{assert.equal(f.calls.length,1);assert.equal(f.w.sessionStorage.length,0);}finally{f.close();}
});
test('expired, malformed, unbounded and different-server intent never consumes a handoff',async()=>{
    for(const storedIntent of [{'accountHub.linkReturnIntent:/suite/':String(Date.now()-1)},{'accountHub.linkReturnIntent:/suite/':'not-an-expiry'},{'accountHub.linkReturnIntent:/suite/':String(Date.now()+600000)},{'accountHub.linkReturnIntent:/other/':String(Date.now()+60000)}]){
        const f=await fixture({marker:false,storedIntent});try{assert.equal(f.calls.length,0);}finally{f.close();}
    }
});
test('no marker or mismatched native server can consume or forward a continuation',async()=>{
    for(const options of [{marker:false},{address:'https://other.test/suite'},{address:'https://fixture.test/elsewhere'},{token:'bad"header'}]){const f=await fixture(options);try{assert.equal(f.calls.length,0);}finally{f.close();}}
});
test('failed, expired or unknown return outcomes never replay the one-use handoff or render provider details',async()=>{
    for(const options of [{network:true},{status:404,result:{ok:false,error:{message:'private-prose'}}},{result:{ok:true,data:{token:'x'.repeat(44)}}}]){
        const f=await fixture(options);try{f.w.dispatchEvent(new f.w.Event('focus'));f.w.dispatchEvent(new f.w.Event('hashchange'));await settle();assert.equal(f.calls.length,1);assert.match(f.w.document.body.textContent,/Reopen the original link/);assert.doesNotMatch(f.w.document.body.textContent,/private-/);}finally{f.close();}
    }
});
test('only an explicit native pending-factor denial schedules a bounded recheck',async()=>{
    const f=await fixture({status:403,result:{twoFactorRequired:true}});try{assert.equal(f.calls.length,1);assert.equal(f.timers.length,2);await f.timers[1]();await settle();assert.equal(f.calls.length,2);assert(!f.w.document.querySelector('[role=alert]'));}finally{f.close();}
});
