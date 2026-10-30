import test from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {measureLivePoster}=createRequire(import.meta.url)('./live-poster-measurement.cjs');

function fixture(){
    const rect=(left,top,width,height)=>({left,top,width,height,right:left+width,bottom:top+height});
    const node=box=>({isConnected:true,getBoundingClientRect:()=>box});
    const image=Object.assign(node(rect(0,0,100,160)),{complete:true,naturalWidth:100});
    const poster=Object.assign(node(rect(0,0,100,160)),{querySelector:()=>image});
    const text=node(rect(120,0,200,30)),time=node(rect(0,180,500,20));
    const canvas=Object.assign(node(rect(0,0,500,180)),{querySelector:()=>poster,querySelectorAll:()=>[text]});
    const main=Object.assign(node(rect(0,0,500,200)),{querySelector:selector=>selector==='time'?time:canvas});
    const doc={querySelector:()=>main,defaultView:{getComputedStyle:()=>({objectFit:'contain',float:'none'})}};
    return{doc,main,canvas,poster,image,text,time,rect};
}
test('poster geometry comes from a current connected loaded card',()=>{
    const f=fixture();assert.deepEqual(measureLivePoster(f.doc),{posterLeft:true,noOverlap:true,fillsPoster:true,fit:'contain',float:'none',timestampBelow:true});
});
test('detached, unloaded and zero-size cards are not layout samples',()=>{
    for(const name of ['main','canvas','poster','image','text','time']){const f=fixture();f[name].isConnected=false;assert.equal(measureLivePoster(f.doc),null);}
    const f=fixture();f.image.naturalWidth=0;assert.equal(measureLivePoster(f.doc),null);f.image.naturalWidth=100;f.poster.getBoundingClientRect=()=>f.rect(0,0,0,0);assert.equal(measureLivePoster(f.doc),null);
});
test('genuine overlap or wrong CSS is returned immediately, not retried into a pass',()=>{
    const f=fixture();f.text.getBoundingClientRect=()=>f.rect(0,0,200,30);assert.equal(measureLivePoster(f.doc).noOverlap,false);
    f.doc.defaultView.getComputedStyle=()=>({objectFit:'cover',float:'left'});const result=measureLivePoster(f.doc);assert.equal(result.fit,'cover');assert.equal(result.float,'left');
});
test('a refreshed inbox is queried again rather than measuring the detached old row',()=>{
    const old=fixture(),fresh=fixture();let current=old.main;const doc={...fresh.doc,querySelector:()=>current};old.main.isConnected=false;assert.equal(measureLivePoster(doc),null);current=fresh.main;assert.equal(measureLivePoster(doc).noOverlap,true);
});
