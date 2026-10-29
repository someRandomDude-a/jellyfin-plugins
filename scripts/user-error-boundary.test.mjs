import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
const {JSDOM}=createRequire(new URL('../plugins/private-syncplay/package.json',import.meta.url))('jsdom');
const code=await readFile(new URL('../plugins/private-syncplay/src/Jellyfin.Plugin.PrivateSyncPlay/Web/ui.js',import.meta.url),'utf8');
function fixture(){
    const dom=new JSDOM('<script src="https://fixture.test/suite/PrivateSyncPlay/assets/ui.js"></script><main></main>',{url:'https://fixture.test/suite/web/index.html',runScripts:'outside-only'});
    Object.defineProperty(dom.window.document,'currentScript',{value:dom.window.document.querySelector('script')});dom.window.eval(code);return dom;
}
test('watch-party failures never echo native exception prose, model state, provider bodies or technical help',async()=>{
    const dom=fixture(),w=dom.window,ui=w.PrivateSyncPlayUI;
    try{
        for(const error of [new Error('private-password'),{status:403,message:'private-exception',detail:'private-help',responseJSON:{error:{message:'private-provider',details:'private-token'}}},{status:500,responseText:'<html>private-link</html>'}]){
            const safe=ui.authError(error);assert.doesNotMatch(safe.message,/private-/);assert.equal(safe.detail,undefined);assert.equal(safe.responseJSON,undefined);assert.equal(ui.authError(safe),safe);
            const parent=w.document.querySelector('main');ui.status(parent);const button=ui.button('Fail',parent,async()=>{throw error;});await button.onclick();assert.doesNotMatch(parent.textContent,/private-/);assert.equal(parent.querySelector('.pspMessageHelp'),null);parent.replaceChildren();
        }
    }finally{w.close();}
});
test('watch-party category and opaque ID survive mapping without trusting arbitrary nature strings',()=>{
    const dom=fixture(),ui=dom.window.PrivateSyncPlayUI,id='11111111-1111-4111-8111-111111111111';
    try{
        const error={status:409,responseJSON:{error:{nature:'uncertain',errorId:id,message:'private-provider'}}};
        assert.match(ui.authError(error).message,/could not confirm the result/);assert.match(ui.authError(error).message,new RegExp(id));
        assert.match(ui.authError({status:403,responseJSON:{error:{nature:'__proto__',errorId:'private-token'}}}).message,/do not have permission/);
        assert.equal(ui.authError({status:403,responseJSON:{twoFactorRequired:true}}).code,'two_factor_required');
    }finally{dom.window.close();}
});
