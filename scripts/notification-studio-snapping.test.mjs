import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
const { JSDOM } = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url))('jsdom');
async function fixture() {
    const dom=new JSDOM('<main></main>',{runScripts:'outside-only',url:'https://fixture.test/web/'});
    for(const name of ['notification-card.js','notification-studio.js'])dom.window.eval(await readFile(new URL('../modules/notifications/src/AccountHub.Notifications/Web/'+name,import.meta.url),'utf8'));
    return {w:dom.window,root:dom.window.document.querySelector('main')};
}
const rectangle=(id,x,y,width,height)=>({id,kind:'text',x,y,width,height});
test('dragging snaps both axes to another layer edge without mutating either input',async()=>{
    const {w}=await fixture();
    try{
        const layer=rectangle('moving',9.6,19.5,20,20),other=rectangle('other',30,40,20,20);
        const result=w.NotificationStudio.geometry.snap(layer,[other],{width:1000,height:500});
        assert.equal(result.layer.x,10);assert.equal(result.layer.y,20);assert.equal(layer.x,9.6);assert.equal(other.x,30);
        assert(result.guides.some(guide=>guide.axis==='x'&&guide.position===30&&guide.targetId==='other'));
        assert(result.guides.some(guide=>guide.axis==='y'&&guide.position===40&&guide.targetId==='other'));
    }finally{w.close();}
});
test('card and peer centers snap like edges, including accurate fractional centers',async()=>{
    const {w}=await fixture();
    try{
        const snap=w.NotificationStudio.geometry.snap;
        let result=snap(rectangle('moving',39.7,39.8,20,20),[],{width:1000,height:500});
        assert.equal(result.layer.x,40);assert.equal(result.layer.y,40);assert(result.guides.every(guide=>guide.kind==='center'&&guide.position===50));
        result=snap(rectangle('moving',64.5,49.6,10,10),[rectangle('target',60,50,20,10)],{width:1000,height:500});
        assert.equal(result.layer.x,65);assert.equal(result.layer.y,50);assert(result.guides.every(guide=>guide.targetId==='target'));
        const aligned=w.NotificationStudio.geometry.align(rectangle('moving',0,0,33.3,20),null,'x','center');
        assert.equal(aligned.layer.x,33.35);assert.equal(aligned.layer.x+aligned.layer.width/2,50);
    }finally{w.close();}
});
test('snap tolerance is measured in screen pixels, not percentages, and Alt/free placement bypasses it',async()=>{
    const {w}=await fixture();
    try{
        const layer=rectangle('moving',9.2,10,20,10),other=rectangle('target',30,70,20,10),snap=w.NotificationStudio.geometry.snap;
        assert.equal(snap(layer,[other],{width:500,height:500}).layer.x,10);
        assert.equal(snap(layer,[other],{width:1000,height:500}).layer.x,9.2);
        const free=snap(layer,[other],{width:500,height:500},{enabled:false});
        assert.equal(free.layer.x,9.2);assert.equal(free.guides.length,0);
        const unmeasured=snap(layer,[other],{width:0,height:0});assert.equal(unmeasured.guides.length,0);
    }finally{w.close();}
});
test('resize handles snap their moving edges while preserving the fixed origin and card bounds',async()=>{
    const {w}=await fixture();
    try{
        const layer=rectangle('moving',10,10,19.6,29.6),result=w.NotificationStudio.geometry.snap(layer,[rectangle('target',30,40,20,20)],{width:1000,height:500},{mode:'resize'});
        assert.equal(result.layer.x,10);assert.equal(result.layer.y,10);assert.equal(result.layer.width,20);assert.equal(result.layer.height,30);
        assert(result.layer.x+result.layer.width<=100);assert(result.layer.y+result.layer.height<=100);
    }finally{w.close();}
});
test('alignment targets card or peer edges/centers and never claims an impossible alignment',async()=>{
    const {w}=await fixture();
    try{
        const align=w.NotificationStudio.geometry.align,layer=rectangle('moving',10,10,20,20),target=rectangle('target',50,25,20,40);
        assert.equal(align(layer,null,'x','start').layer.x,0);assert.equal(align(layer,null,'x','end').layer.x,80);
        assert.equal(align(layer,null,'y','center').layer.y,40);
        assert.equal(align(layer,target,'x','center').layer.x,50);assert.equal(align(layer,target,'y','end').layer.y,45);
        const blocked=align(rectangle('moving',0,0,80,20),rectangle('target',80,0,20,20),'x','center');
        assert.equal(blocked.layer.x,20);assert.equal(blocked.clamped,true);assert.equal(blocked.guides.length,0);
    }finally{w.close();}
});
test('snapping toggles and reference selection are not edits; alignment is undoable with Ctrl+Z/Y',async()=>{
    const {w,root}=await fixture();let current,changes=0;
    try{
        w.NotificationStudio.mount(root,{title:'Example',body:'Preview'},null,value=>{current=value;changes++;});
        const button=label=>[...root.querySelectorAll('button')].find(control=>control.getAttribute('aria-label')===label||control.textContent===label);
        const snap=button('Snap to layers');assert.equal(snap.getAttribute('aria-pressed'),'true');snap.click();assert.equal(snap.getAttribute('aria-pressed'),'false');assert.equal(changes,0);
        button('Center horizontally').click();assert.equal(current.layers.find(layer=>layer.id==='title').x,16);assert.equal(changes,1);
        root.dispatchEvent(new w.KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true}));assert.equal(current.layers.find(layer=>layer.id==='title').x,30);
        root.dispatchEvent(new w.KeyboardEvent('keydown',{key:'y',ctrlKey:true,bubbles:true}));assert.equal(current.layers.find(layer=>layer.id==='title').x,16);
        const reference=root.querySelector('[data-studio-field="Align relative to"]'),before=changes;reference.value='body';reference.dispatchEvent(new w.Event('change'));
        assert.equal(changes,before);button('Center horizontally').click();assert.equal(current.layers.find(layer=>layer.id==='title').x,30);
        assert.equal(root.querySelectorAll('.notificationStudioGuide').length,1);
    }finally{w.close();}
});
test('pointer handlers paint snap guides, clear them on cancel/release and honor Alt bypass',async()=>{
    const {w,root}=await fixture();let current,changes=0;
    try{
        const defaults=w.NotificationCard.defaults();
        const initial={height:200,layers:[{...defaults.layers[1],id:'moving',x:5,y:5,width:20,height:20},{...defaults.layers[2],id:'target',x:30,y:40,width:20,height:20}]};
        w.NotificationStudio.mount(root,{title:'Example',body:'Preview'},initial,value=>{current=value;changes++;});
        const pointer=(target,type,x,y,altKey=false)=>{const event=new w.MouseEvent(type,{button:0,clientX:x,clientY:y,altKey,bubbles:true,cancelable:true});Object.defineProperty(event,'pointerId',{value:1});target.dispatchEvent(event);};
        const drag=alt=>{
            root.querySelector('.notificationCanvas').getBoundingClientRect=()=>({width:1000,height:500});
            const box=root.querySelector('[data-layer=moving]');pointer(box,'pointerdown',100,100);pointer(box,'pointermove',146,172.5,alt);return box;
        };
        let box=drag(false);assert.equal(box.style.left,'10%');assert.equal(box.style.top,'20%');assert.equal(root.querySelectorAll('.notificationStudioGuide').length,2);
        pointer(box,'pointercancel',146,172.5);assert.equal(changes,0);assert.equal(root.querySelectorAll('.notificationStudioGuide').length,0);
        box=drag(false);pointer(box,'pointerup',146,172.5);assert.equal(current.layers[0].x,10);assert.equal(current.layers[0].y,20);assert.equal(root.querySelectorAll('.notificationStudioGuide').length,0);
        root.dispatchEvent(new w.KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true}));
        box=drag(true);assert.equal(box.style.left,'9.6%');assert.equal(box.style.top,'19.5%');assert.equal(root.querySelectorAll('.notificationStudioGuide').length,0);
        pointer(box,'pointerup',146,172.5,true);assert.equal(current.layers[0].x,9.6);assert.equal(current.layers[0].y,19.5);
    }finally{w.close();}
});
