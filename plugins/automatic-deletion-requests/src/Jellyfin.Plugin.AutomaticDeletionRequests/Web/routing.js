(() => {
    'use strict';
    function mount(host,filters,rules,edit,changed,view={zoom:1,panX:0,panY:0}) {
        const node=(tag,text)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;return e;};
        const root=node('div');root.className='ar-circuit';host.append(root);
        const help=node('p','Library filters → rule inputs. Select a card to edit. Connect or disconnect rule outputs in library-filter mode.');help.style.padding='1rem';root.append(help);
        const viewport=node('div');viewport.className='ar-viewport';root.append(viewport);const stage=node('div');stage.className='ar-stage';viewport.append(stage);
        const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.classList.add('ar-wires');stage.append(svg);
        let {zoom,panX,panY}=view;const positions=new Map();stage.style.transform=`translate(${panX}px,${panY}px) scale(${zoom})`;
        for(const [kind,items] of [['filter',filters],['rule',rules]])items.forEach((item,index)=>{
            const x=kind==='filter'?(item.x??40):(item.canvasX??540),y=kind==='filter'?(item.y??index*150):(item.canvasY??index*150);
            const card=node('section');card.className='ar-block';card.dataset.kind=kind;card.style.left=x+'px';card.style.top=y+'px';stage.append(card);positions.set(kind+':'+item.id,card);
            const title=node('header',(kind==='filter'?'LIBRARY FILTER · ':'RULE · ')+item.name);card.append(title);
            card.tabIndex=0;card.setAttribute('role','button');card.setAttribute('aria-label','Select '+item.name);card.onclick=()=>{if(!dragged)edit(kind,item.id);};card.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();edit(kind,item.id);}};
            let drag,dragged=false;title.onpointerdown=e=>{dragged=false;drag={x:e.clientX,y:e.clientY,ox:parseFloat(card.style.left),oy:parseFloat(card.style.top)};title.setPointerCapture?.(e.pointerId);};title.onpointermove=e=>{if(!drag)return;dragged ||= Math.abs(e.clientX-drag.x)+Math.abs(e.clientY-drag.y)>4;card.style.left=Math.max(-10000,Math.min(10000,drag.ox+(e.clientX-drag.x)/zoom))+'px';card.style.top=Math.max(-10000,Math.min(10000,drag.oy+(e.clientY-drag.y)/zoom))+'px';draw();};title.onpointerup=title.onpointercancel=()=>{drag=null;if(kind==='filter'){item.x=parseFloat(card.style.left);item.y=parseFloat(card.style.top);}else{item.canvasX=parseFloat(card.style.left);item.canvasY=parseFloat(card.style.top);}changed();};
            card.onpointerdown=e=>{if(!title.contains(e.target))dragged=false;};
            card.append(node('p',kind==='filter'?item.outputs.length+' rule outputs':filters.filter(f=>f.outputs.includes(item.id)).length+' filter inputs'));
        });
        function draw(){svg.replaceChildren();for(const filter of filters)for(const output of filter.outputs){const a=positions.get('filter:'+filter.id),b=positions.get('rule:'+output);if(!a||!b)continue;const x=parseFloat(a.style.left)+230,y=parseFloat(a.style.top)+60,tx=parseFloat(b.style.left),ty=parseFloat(b.style.top)+60;const path=document.createElementNS(svg.namespaceURI,'path');path.setAttribute('d',`M${x} ${y} C${x+140} ${y},${tx-140} ${ty},${tx} ${ty}`);svg.append(path);}}
        viewport.onwheel=e=>{e.preventDefault();if(e.ctrlKey||e.metaKey)zoom=Math.max(.25,Math.min(2,zoom-e.deltaY*.002));else{panX=Math.max(-10000,Math.min(10000,panX-e.deltaX));panY=Math.max(-10000,Math.min(10000,panY-e.deltaY));}Object.assign(view,{zoom,panX,panY});changed();stage.style.transform=`translate(${panX}px,${panY}px) scale(${zoom})`;};draw();
        return {dispose(){root.remove();}};
    }
    window.RemovalRouting={mount};
})();
