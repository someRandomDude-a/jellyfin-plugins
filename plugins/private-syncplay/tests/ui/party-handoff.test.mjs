import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {JSDOM} from 'jsdom';

const root=new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/',import.meta.url);
const [ui,web]=await Promise.all(['ui.js','web.js'].map(n=>readFile(new URL(n,root),'utf8')));
const tick=()=>new Promise(resolve=>setImmediate(resolve));
async function setup({owner=false,clipboardDenied=false,invite=false,navigate=false,fail=false}={}) {
    const dom=new JSDOM('<header class="skinHeader"><div class="headerRight"><button class="btnSyncPlay"></button></div></header>',
        {url:'https://media.example/jellyfin/web/index.html#/home',runScripts:'outside-only'});
    const w=dom.window,d=w.document,calls=[],copies=[];
    const state={ready:true,navigate,fail,sequence:0};
    Object.defineProperty(d,'currentScript',{value:{src:'https://media.example/jellyfin/PrivateSyncPlay/assets/web.js'}});
    w.HTMLElement.prototype.getClientRects=()=>[{width:10,height:10}];
    w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};
    w.HTMLDialogElement.prototype.close=function(){this.open=false;this.dispatchEvent(new w.Event('close'));};
    Object.defineProperty(w.navigator,'clipboard',{value:{writeText:async text=>{if(clipboardDenied)throw new Error('denied');copies.push(text);}}});
    let refresh;
    w.setInterval=fn=>{refresh=fn;};
    w.ApiClient={accessToken:()=> 'account-token',getUrl:p=>'https://media.example/jellyfin/'+p,ajax:async options=>{
        calls.push(options);const path=new URL(options.url).pathname;
        if(options.type==='GET') {
            if(path.endsWith('/Me'))return {realtimeReady:state.ready,canCreatePublic:true,canCreatePrivate:true,maxViewers:8,maxInviteUses:4,canInviteGuests:true,
                party:owner?{id:'party',isOwner:true,mode:'Private',viewers:1,capacity:8,invites:[]}:null};
            if(path.endsWith('/List'))return [{groupId:'public-group',groupName:'Movie night'}];
            if(path.endsWith('/Management'))return {policy:{allowJoins:true,allowGuests:true},participants:[],blocked:[],capacity:8};
        }
        if(path.endsWith('/Invites')&&options.type==='POST')return {id:'invite-'+(++state.sequence),token:String(state.sequence).repeat(64)};
        if(options.type==='POST') {
            assert.equal(d.querySelectorAll('dialog').length,0,'Release dialogs before the native mutation');
            const event=new w.Event('command',{cancelable:true});w.dispatchEvent(event);
            assert.equal(event.defaultPrevented,false,'Release native command guard');
            if(state.navigate)w.dispatchEvent(new w.Event('hashchange'));
            if(state.fail)throw new Error('response lost');
        }
    }};
    const key='PrivateSyncPlay.Invite:/jellyfin/PrivateSyncPlay/';
    if(invite)w.sessionStorage.setItem(key,JSON.stringify({token:'A'.repeat(64),expires:Date.now()+60000}));
    w.eval(ui);w.eval(web);await tick();
    const button=label=>[...d.querySelectorAll('button')].find(b=>b.textContent.trim()===label);
    if(!invite){d.querySelector('.privateSyncPlayLauncher').click();await tick();await tick();}
    return {w,d,calls,copies,state,key,button,refresh:()=>refresh(),close:()=>w.close()};
}

test('new invitations auto-copy BaseUrl-aware links, replace retained output, and erase revoked secrets',async()=>{
    const p=await setup({owner:true});try {
        p.button('Create invite link').click();await tick();
        const old=p.d.querySelector('.privateSyncPlayInviteResult input');
        assert.equal(p.copies[0],'https://media.example/jellyfin/PrivateSyncPlay/Watch#'+'1'.repeat(64));
        p.button('Create invite link').click();await tick();
        assert.equal(old.value,'');assert.equal(old.isConnected,false);
        assert.equal(p.d.querySelectorAll('.privateSyncPlayInviteResult input').length,1);
        assert.equal(p.copies.length,2);
        p.button('Revoke this invite').click();await tick();
        assert.equal(p.d.querySelector('.privateSyncPlayInviteResult input').value,'');
        assert.equal(p.button('Copy link').disabled,true);
        assert(p.calls.some(c=>c.type==='DELETE'&&c.url.endsWith('/Invites/invite-2')));
    }finally{p.close();}
});
test('clipboard denial keeps the one created invitation selected for manual copying',async()=>{
    const p=await setup({owner:true,clipboardDenied:true});try {
        p.button('Create invite link').click();await tick();
        const input=p.d.querySelector('.privateSyncPlayInviteResult input');
        assert.equal(p.d.activeElement,input);assert.equal(input.selectionEnd,input.value.length);
        assert(p.d.querySelector('[data-psp-status]').textContent.includes('manually'));
        p.button('Copy link').click();await tick();
        assert.equal(p.calls.filter(c=>c.type==='POST'&&c.url.endsWith('/Invites')).length,1);
        p.button('Close').click();await tick();assert.equal(input.value,'');
    }finally{p.close();}
});
for(const label of ['Create','Join','Join by code']) {
    test(`${label} waits for realtime readiness and hands off without reopening over native navigation`,async()=>{
        const p=await setup();try {
            p.state.ready=false;p.button(label).click();await tick();
            assert.equal(p.calls.filter(c=>c.type==='POST').length,0);
            assert(p.d.querySelector('[data-psp-status]').textContent.includes('No group action was sent'));
            const input=[...p.d.querySelectorAll('input')].find(i=>i.maxLength===8);input.value='ABCD1234';
            p.state.ready=true;p.state.navigate=true;p.button(label).click();await tick();await tick();
            const posts=p.calls.filter(c=>c.type==='POST');assert.equal(posts.length,1);
            if(label==='Join by code')assert.equal(JSON.parse(posts[0].data).Code,'ABCD1234');
            assert.equal(p.d.querySelectorAll('dialog').length,0);
        }finally{p.close();}
    });
}
for(const navigate of [false,true]) {
    test(`invitation mutation is never replayed after ${navigate?'native navigation':'a lost response'}`,async()=>{
        const p=await setup({invite:true,navigate,fail:true});try {
            p.w.Date.now=()=>Date.now()+60000;p.refresh();await tick();
            assert.equal(p.calls.filter(c=>c.type==='POST'&&c.url.endsWith('/JoinInvite')).length,1);
            assert.equal(p.w.sessionStorage.getItem(p.key),null);
            assert.equal(p.d.querySelectorAll('dialog').length,navigate?0:1);
        }finally{p.close();}
    });
}
