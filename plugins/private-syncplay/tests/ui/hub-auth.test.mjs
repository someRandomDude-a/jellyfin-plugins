import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {JSDOM} from 'jsdom';
test('only marked 403/401 are pending 2FA, without touching playback or treating ordinary permission denials as a challenge',async()=>{
    const dom=new JSDOM('<video></video>',{url:'https://media.example/web/',runScripts:'outside-only'});
    const w=dom.window;
    try{
        Object.defineProperty(w.document,'currentScript',{value:{src:'https://media.example/PrivateSyncPlay/assets/ui.js'}});
        w.eval(await readFile(new URL('../../src/Jellyfin.Plugin.PrivateSyncPlay/Web/ui.js',import.meta.url),'utf8'));
        const video=w.document.querySelector('video');video.muted=false;
        for(const status of [401,403]){
            const error=w.PrivateSyncPlayUI.authError({status,responseJSON:{twoFactorRequired:true}});
            assert.equal(error.code,'two_factor_required');assert.equal(error.status,status);assert(!video.muted);
            const plain={status,responseJSON:{error:'Denied'}};assert.equal(w.PrivateSyncPlayUI.authError(plain),plain);
        }
        const expired={status:401,responseText:'{"twoFactorRequired":false}'};assert.equal(w.PrivateSyncPlayUI.authError(expired),expired);
    }finally{w.close();}
});
