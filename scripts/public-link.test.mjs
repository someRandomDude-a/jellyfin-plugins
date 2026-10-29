import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
const {JSDOM}=createRequire(new URL('../plugins/private-syncplay/package.json',import.meta.url))('jsdom');
const code=await readFile(new URL('../plugins/account-hub/src/AccountHub/Web/public.js',import.meta.url),'utf8');
const settle=async()=>{for(let i=0;i<10;i++)await new Promise(setImmediate);};
async function fixture({signed=true,rejected=false,nativeFailure=false,errorCode='unauthorized',continuationFailure=false}={}){
    const dom=new JSDOM('<base href="https://fixture.test/suite/AccountHub/"><main class="accountHubPublic" data-page="link"><p class="hubPublicStatus"></p><div class="hubPublicContent"></div></main>',{runScripts:'outside-only',url:'https://fixture.test/suite/AccountHub/link#token='+'a'.repeat(43)}),w=dom.window,calls=[];
    const signIn=(token='native-session')=>w.localStorage.setItem('jellyfin_credentials',JSON.stringify({Servers:[{ManualAddress:'https://fixture.test/suite',AccessToken:token}]}));if(signed)signIn();
    w.fetch=async(url,options)=>{calls.push({url:String(url),options});if(nativeFailure||continuationFailure&&String(url).endsWith('/continue'))throw Object.assign(new Error('private-native-exception'),{code:'private-native-code'});const lookup=String(url).endsWith('/lookup');return{ok:!rejected,status:rejected?401:200,json:async()=>rejected?{ok:false,error:{code:errorCode,message:'private-provider-message',details:'private-provider-details'}}:{ok:true,data:lookup?{userId:'11111111-1111-4111-8111-111111111111',jellyfinUsername:'<img src=x onerror=alert(1)>',discordUsername:'test-user',discordId:'123456789012345678'}:{status:'linked'}}};};
    w.eval(code);await settle();return{w,calls,signIn,close:()=>w.close()};
}
test('signed-in link review automatically validates the native session and shows details without another sign-in prompt',async()=>{
    const f=await fixture();try{assert.equal(f.w.location.hash,'');assert.equal(f.calls.length,1);assert(f.calls[0].url.endsWith('/discord/link/lookup'));assert(f.calls[0].options.headers.Authorization.includes('native-session'));assert(!f.calls[0].url.includes('a'.repeat(43)));assert.equal(f.w.document.querySelector('.hubPublicSignIn'),null);assert(f.w.document.querySelector('.hubPublicAccounts').textContent.includes('<img'));assert.equal(f.w.document.querySelectorAll('img').length,0);f.w.document.querySelector('.hubPublicConfirm').click();await settle();assert(f.calls[1].url.endsWith('/confirm'));assert.match(f.w.document.querySelector('.hubPublicStatus').textContent,/Discord linked/);}finally{f.close();}
});
test('signed-out review discloses no Jellyfin identity and resumes validation after native sign-in',async()=>{
    const f=await fixture({signed:false});try{assert.equal(f.calls.length,0);assert.equal(f.w.document.querySelector('.hubPublicAccounts'),null);assert.equal(f.w.document.querySelector('.hubPublicConfirm'),null);const link=f.w.document.querySelector('.hubPublicSignIn');assert.equal(link.tagName,'BUTTON');assert.equal(link.getAttribute('target'),null);f.signIn();f.w.dispatchEvent(new f.w.Event('focus'));await settle();assert.equal(f.calls.length,1);assert(f.w.document.querySelector('.hubPublicAccounts'));}finally{f.close();}
});
test('signed-out same-tab handoff is anonymous, cookie-enabled, single-submit and never stores the challenge',async()=>{
    const f=await fixture({signed:false});try{
        const button=f.w.document.querySelector('.hubPublicSignIn');button.click();button.click();await settle();
        assert.equal(f.calls.length,1);const call=f.calls[0];assert(call.url.endsWith('/public/v1/discord/link/continue'));assert.equal(call.options.credentials,'same-origin');assert.equal(call.options.headers.Authorization,undefined);
        assert.deepEqual(JSON.parse(call.options.body),{token:'a'.repeat(43),confirmation:'REVIEW AFTER SIGN-IN'});
        assert.equal(f.w.localStorage.length,0);assert.equal(f.w.sessionStorage.length,0);assert.equal(f.w.document.querySelector('.hubPublicConfirm'),null);assert.equal(button.disabled,true);
        assert.match(code,/location\.assign\(new URL\('web\/index\.html#\/login\?url=home%3FaccountHubLinkReturn%3D1'/);
    }finally{f.close();}
});
test('an unknown handoff outcome is not retried on clicks, focus or storage events',async()=>{
    const f=await fixture({signed:false,continuationFailure:true});try{
        const button=f.w.document.querySelector('.hubPublicSignIn');button.click();await settle();button.click();f.w.dispatchEvent(new f.w.Event('focus'));f.w.dispatchEvent(new f.w.Event('storage'));await settle();
        assert.equal(f.calls.length,1);assert.match(f.w.document.querySelector('.hubPublicStatus').textContent,/could not confirm/);assert.doesNotMatch(f.w.document.body.textContent,/private-/);
    }finally{f.close();}
});
test('a stored but rejected native token never reveals identity or enables linking',async()=>{
    const f=await fixture({rejected:true});try{assert.equal(f.calls.length,1);assert.equal(f.w.document.querySelector('.hubPublicAccounts'),null);assert.equal(f.w.document.querySelector('.hubPublicConfirm'),null);assert(f.w.document.querySelector('.hubPublicSignIn'));}finally{f.close();}
});
test('changing accounts after review forces another authenticated review instead of confirming the previous identity',async()=>{
    const f=await fixture();try{const button=f.w.document.querySelector('.hubPublicConfirm');f.signIn('different-native-session');button.click();await settle();assert.equal(f.calls.filter(call=>call.url.endsWith('/confirm')).length,0);assert.equal(f.calls.filter(call=>call.url.endsWith('/lookup')).length,2);assert(f.calls[1].options.headers.Authorization.includes('different-native-session'));}finally{f.close();}
});
test('link review never renders native exception prose or arbitrary provider codes, including prototype property names',async()=>{
    for(const options of [{nativeFailure:true},{rejected:true,errorCode:'private-provider-code'},{rejected:true,errorCode:'__proto__'}]){
        const f=await fixture(options);try{
            assert.doesNotMatch(f.w.document.querySelector('.hubPublicStatus').textContent,/private-|__proto__|\[object/);
            assert.equal(f.w.document.querySelector('.hubPublicAccounts'),null);assert.equal(f.w.document.querySelector('.hubPublicConfirm'),null);assert.equal(f.calls.length,1);
        }finally{f.close();}
    }
});
