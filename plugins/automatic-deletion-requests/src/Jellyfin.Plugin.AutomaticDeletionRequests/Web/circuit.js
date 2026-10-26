(() => {
    'use strict';
    const copy = x => JSON.parse(JSON.stringify(x)), id = () => crypto.randomUUID();
    const fields = ['title','year','added','rating','kind','seerr.ownRequest','seerr.is4k','seerr.requestOnly'];
    const ops = f => f.startsWith('seerr.') ? ['equals','not-equals'] : f === 'added' ? ['older-days','newer-days'] : ['title','kind'].includes(f) ? ['equals','not-equals','contains'] : ['equals','not-equals','greater','less'];
    const conditions = g => g.nodes.flatMap(n => n.kind === 'condition' ? [n.condition] : n.kind === 'custom' ? conditions(n.circuit) : []);
    function legacy(rule) {
        const nodes = [{id:'start',kind:'start',x:40,y:40,inputs:[]}];
        rule.conditions.forEach((condition,i) => nodes.push({id:'c'+i,kind:'condition',x:80,y:160+i*190,inputs:[],condition:copy(condition)}));
        nodes.push({id:'group',kind:rule.match === 'any'?'or':'and',x:400,y:180,inputs:rule.conditions.map((_,i)=>'c'+i)});
        // A one-condition legacy group needs two sockets, without changing its meaning.
        if(rule.conditions.length===1)nodes.at(-1).inputs.push('c0');
        nodes.push({id:'delete',kind:'delete',x:720,y:180,inputs:['group','start']});
        return {version:1,nodes,zoom:1,panX:0,panY:0};
    }
    function mount(host, rule, changed, {sources=[],filterMode=false,targets=[],route=null,onSource=()=>{}}={}) {
        rule.circuit ||= legacy(rule); const g=rule.circuit;
        g.sources = Object.assign(Object.create(null),g.sources||{});
        if(sources.length&&g.panX===0)g.panX=300;
        const el=(tag,text)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;return e;};
        const root=el('div');root.className='ar-circuit';host.append(root);
        const toolbar=el('div');toolbar.className='ar-circuit-tools';root.append(toolbar);
        const btn=(parent,text,fn)=>{const b=el('button',text);b.type='button';b.onclick=fn;parent.append(b);return b;};
        const message=el('span','Drag blocks from the right. Connect output → input; click a wire to disconnect.');
        const viewport=el('div');viewport.className='ar-viewport';viewport.tabIndex=0;
        const stage=el('div');stage.className='ar-stage';viewport.append(stage);
        const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.classList.add('ar-wires');stage.append(svg);
        const palette=el('aside');palette.className='ar-palette';palette.append(el('h3','Blocks'));
        const layout=el('div');layout.className='ar-circuit-layout';layout.append(viewport,palette);root.append(layout);
        const selection=el('select');selection.setAttribute('aria-label','Block to edit');toolbar.append(selection);
        let selected, linking, drag, states={}, disposed=false;
        let routing=false;
        const sync=()=>{rule.conditions=conditions(g);changed();};
        const transform=()=>{stage.style.transform=`translate(${g.panX}px,${g.panY}px) scale(${g.zoom})`;};
        const save=()=>{states={};sync();};
        btn(toolbar,'−',()=>{g.zoom=Math.max(.25,g.zoom-.1);transform();sync();});
        btn(toolbar,'+',()=>{g.zoom=Math.min(2,g.zoom+.1);transform();sync();});
        btn(toolbar,'Reset view',()=>{g.zoom=1;g.panX=g.panY=0;transform();sync();});
        btn(toolbar,'Fit canvas',()=>{const blocks=[...stage.querySelectorAll('.ar-block')];if(!blocks.length)return;const xs=blocks.map(b=>parseFloat(b.style.left)),ys=blocks.map(b=>parseFloat(b.style.top));const left=Math.min(...xs),top=Math.min(...ys),right=Math.max(...xs)+260,bottom=Math.max(...blocks.map(b=>parseFloat(b.style.top)+b.offsetHeight))+40;g.zoom=Math.max(.25,Math.min(2,(viewport.clientWidth-60)/(right-left),(viewport.clientHeight-60)/(bottom-top)));g.panX=30-left*g.zoom;g.panY=30-top*g.zoom;transform();sync();});
        btn(toolbar,'Remove block',()=>{const n=g.nodes.find(n=>n.id===selected);if(!n||['start','delete'].includes(n.kind))return;g.nodes=g.nodes.filter(n=>n.id!==selected);for(const n of g.nodes)n.inputs=n.inputs.map(x=>x===selected?'':x);save();render();});
        const file=(name,value)=>{const url=URL.createObjectURL(new Blob([JSON.stringify(value,null,2)],{type:'application/json'}));const a=el('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
        btn(toolbar,'Export circuit',()=>file('removal-circuit.json',g));
        const upload=el('input');upload.type='file';upload.accept='.json,application/json';upload.hidden=true;toolbar.append(upload);
        btn(toolbar,'Import custom block',()=>upload.click());
        const custom=[];
        upload.onchange=async()=>{try{if(upload.files[0].size>200000)throw Error();const circuit=JSON.parse(await upload.files[0].text());check(circuit);const name=prompt('Custom block name','Custom circuit');if(!name||name.length>100)return;custom.push({name,circuit});paletteButton(name,'custom',custom.at(-1));}catch{message.textContent='Invalid or oversized circuit. Nothing imported.';}upload.value='';};
        btn(toolbar,'Save as custom block',()=>{try{check(g);const name=prompt('Custom block name','Custom circuit');if(!name||name.length>100)return;const item={name,circuit:copy(g)};custom.push(item);paletteButton(name,'custom',item);file('custom-circuit.json',g);}catch{message.textContent='Complete the circuit before saving a custom block.';}});
        toolbar.append(message);
        function add(kind,x=120,y=160,custom) {
            if(g.nodes.length>=128){message.textContent='Circuit limit: 128 blocks.';return;}
            const n={id:id(),kind,x,y,inputs:kind==='and'||kind==='or'?['','']:kind==='not'?['']:[]};
            if(kind==='condition')n.condition={field:'added',operator:'older-days',value:30};
            if(custom){n.circuit=copy(custom.circuit);n.name=custom.name;}
            g.nodes.push(n);selected=n.id;save();render();
        }
        function paletteButton(label,kind,item){const b=btn(palette,label,()=>add(kind,160-g.panX/g.zoom,120-g.panY/g.zoom,item));b.draggable=true;b.ondragstart=e=>e.dataTransfer.setData('text/plain',JSON.stringify({kind,index:item?custom.indexOf(item):-1}));}
        for(const kind of ['condition','and','or','not'])paletteButton(kind==='condition'?'Condition':kind.toUpperCase(),kind);
        palette.append(el('p','AND / OR: use the + input socket to append another input. Start → Delete carries execution; coloured sockets carry Boolean values.'));
        viewport.ondragover=e=>e.preventDefault();viewport.ondrop=e=>{e.preventDefault();try{const data=JSON.parse(e.dataTransfer.getData('text/plain'));if(!['condition','and','or','not','custom'].includes(data.kind))return;const r=viewport.getBoundingClientRect();add(data.kind,(e.clientX-r.left-g.panX)/g.zoom,(e.clientY-r.top-g.panY)/g.zoom,custom[data.index]);}catch{}};
        function connect(target,index,source){if(!source)return;const n=g.nodes.find(n=>n.id===target),from=g.nodes.find(n=>n.id===source);if(!n||!from||target===source||from.kind==='delete')return;
            if((n.kind==='delete'&&index===1)!==(from.kind==='start')){message.textContent='Connect execution to execution, Boolean to Boolean.';return;}
            const reaches=(at,seen=new Set())=>{if(at===target)return true;if(seen.has(at))return false;seen.add(at);return (g.nodes.find(n=>n.id===at)?.inputs||[]).some(x=>reaches(x,seen));};
            if(reaches(source)){message.textContent='Cycles are not allowed.';return;}
            n.inputs[index]=source;linking=null;save();render();
        }
        function socket(n,index,out=false,extra=false){const b=el('button',out?'●':extra?'＋':'●');b.type='button';b.className='ar-socket '+(out?'ar-output':'ar-input');b.dataset.port=out?'out':index;b.dataset.owner=n.id;b.title=out?'Output':extra?'Connect another input':'Input '+(index+1);b.setAttribute('aria-label',n.kind+' '+b.title);
            const source=out?n.id:n.inputs[index];if(states[source]!==undefined)b.dataset.signal=String(states[source]);
            b.onclick=e=>{e.stopPropagation();if(out){linking=n.id;message.textContent='Select an input socket.';}else if(linking)connect(n.id,index,linking);};
            b.onpointerdown=e=>{e.stopPropagation();if(out){linking=n.id;b.setPointerCapture?.(e.pointerId);}};
            b.onpointerup=e=>{if(!out||!linking)return;const target=document.elementFromPoint?.(e.clientX,e.clientY)?.closest('.ar-input');if(target)connect(target.dataset.owner,Number(target.dataset.port),linking);};
            return b;
        }
        function control(parent,value,values,change){const input=el(values?'select':'input');if(values)for(const v of values){const o=el('option',v);o.value=v;input.append(o);}else input.type='text';input.value=value;input.setAttribute('aria-label','Condition '+(values===fields?'field':values?'comparison':'value'));input.onchange=()=>change(input.value);parent.append(input);return input;}
        function render(){stage.querySelectorAll('.ar-block').forEach(e=>e.remove());selection.replaceChildren();
            for(const n of g.nodes){const option=el('option',n.name||n.kind.toUpperCase());option.value=n.id;selection.append(option);const block=el('section');block.className='ar-block';block.dataset.id=n.id;block.dataset.kind=n.kind;block.style.left=n.x+'px';block.style.top=n.y+'px';
                const title=el('header',n.name||n.kind.toUpperCase());block.append(title);title.onpointerdown=e=>{if(e.button!==0)return;selected=n.id;selection.value=n.id;drag={id:n.id,x:e.clientX,y:e.clientY,ox:n.x,oy:n.y};title.setPointerCapture?.(e.pointerId);e.preventDefault();};
                title.onpointermove=e=>{if(drag?.id!==n.id)return;n.x=Math.max(-10000,Math.min(10000,drag.ox+(e.clientX-drag.x)/g.zoom));n.y=Math.max(-10000,Math.min(10000,drag.oy+(e.clientY-drag.y)/g.zoom));block.style.left=n.x+'px';block.style.top=n.y+'px';wires();};
                title.onpointerup=()=>{drag=null;sync();};title.onpointercancel=()=>{drag=null;sync();};
                if(n.kind==='condition'){const c=n.condition;control(block,c.field,fields,v=>{c.field=v;c.operator=ops(v)[0];c.value=v.startsWith('seerr.')?true:['title','kind'].includes(v)?'':30;save();render();});control(block,c.operator,ops(c.field),v=>{c.operator=v;save();});control(block,c.value,c.field.startsWith('seerr.')?['true','false']:null,v=>{c.value=c.field.startsWith('seerr.')?v==='true':['title','kind'].includes(c.field)?v:Number(v);save();});}
                if(n.kind==='delete'){title.textContent=filterMode?'FILTER OUTPUT':'DELETE';block.append(el('p',filterMode?'True → send this item to attached rule cards. False → skip item.':'True → removal request. False → stop. Consent, approval and safety delay still apply.'));}
                if(n.kind==='custom')block.append(el('p','Reusable circuit · '+conditions(n.circuit).length+' conditions'));
                for(let i=0;i<n.inputs.length;i++){const row=el('div');row.className='ar-port-row';row.append(socket(n,i),el('span',n.kind==='delete'&&i===1?'Execution':'Boolean '+(i+1)));block.append(row);}
                if(['and','or'].includes(n.kind)&&n.inputs.length<128){const row=el('div');row.className='ar-port-row';row.append(socket(n,n.inputs.length,false,true),el('span','Add input'));block.append(row);}
                if(n.kind!=='delete')block.append(socket(n,0,true));
                else if(filterMode&&route){const port=el('button','●');port.type='button';port.className='ar-socket ar-output';port.setAttribute('aria-label','Filter output to rule');port.onclick=()=>{routing=true;message.textContent='Select a rule input to attach it.';};port.onpointerdown=e=>{e.stopPropagation();routing=true;port.setPointerCapture?.(e.pointerId);};port.onpointerup=e=>{const target=document.elementFromPoint?.(e.clientX,e.clientY)?.closest('[data-route-target]');if(target){if(!route.outputs.includes(target.dataset.routeTarget))route.outputs.push(target.dataset.routeTarget);routing=false;sync();render();}};block.append(port);}
                stage.append(block);
            }
            const start=g.nodes.find(n=>n.kind==='start');
            if(start)for(const [i,source]of sources.entries()){const card=el('section');card.className='ar-block ar-source';const position=g.sources[source.id]||{x:start.x-280,y:start.y+i*125};card.style.left=position.x+'px';card.style.top=position.y+'px';card.dataset.source=source.id;const title=el('header','LIBRARY FILTER · '+source.name);card.append(title,el('p','Locked connection · click to test this path. Drag to reposition.'));let moving,moved=false;title.onpointerdown=e=>{moved=false;moving={x:e.clientX,y:e.clientY,ox:position.x,oy:position.y};title.setPointerCapture?.(e.pointerId);};title.onpointermove=e=>{if(!moving)return;const dx=(e.clientX-moving.x)/g.zoom,dy=(e.clientY-moving.y)/g.zoom;moved ||= Math.abs(dx)+Math.abs(dy)>4;position.x=Math.max(-10000,Math.min(10000,moving.ox+dx));position.y=Math.max(-10000,Math.min(10000,moving.oy+dy));card.style.left=position.x+'px';card.style.top=position.y+'px';wires();};title.onpointerup=title.onpointercancel=()=>{moving=null;g.sources[source.id]=position;sync();};card.onclick=()=>{if(!moved)onSource(source);};card.tabIndex=0;card.setAttribute('role','button');card.setAttribute('aria-label','Test input path '+source.name);card.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();onSource(source);}};stage.append(card);}
            const output=g.nodes.find(n=>n.kind==='delete');
            if(filterMode&&output)for(const [i,target]of targets.entries()){const card=el('section');card.className='ar-block ar-route-target';card.style.left=(output.x+330)+'px';card.style.top=(output.y+i*130)+'px';card.dataset.target=target.id;card.append(el('header','RULE · '+target.name));const port=el('button','●');port.type='button';port.className='ar-socket ar-input';port.dataset.routeTarget=target.id;port.setAttribute('aria-label','Attach '+target.name);port.onclick=()=>{if(!routing)return;if(!route.outputs.includes(target.id))route.outputs.push(target.id);routing=false;sync();render();};card.append(port,el('p','Input → Start'));stage.append(card);}
            selection.value=selected||'';transform();wires();
        }
        selection.onchange=()=>{selected=selection.value;const n=g.nodes.find(n=>n.id===selected);g.panX=40-n.x*g.zoom;g.panY=40-n.y*g.zoom;transform();sync();};
        function wires(){svg.replaceChildren();for(const n of g.nodes)n.inputs.forEach((source,i)=>{if(!source)return;const a=stage.querySelector(`[data-id="${CSS.escape(source)}"] .ar-output`),b=stage.querySelector(`[data-id="${CSS.escape(n.id)}"] [data-port="${i}"]`);if(!a||!b)return;const ar=a.getBoundingClientRect(),br=b.getBoundingClientRect(),r=stage.getBoundingClientRect();const x=(ar.x+ar.width/2-r.x)/g.zoom,y=(ar.y+ar.height/2-r.y)/g.zoom,tx=(br.x+br.width/2-r.x)/g.zoom,ty=(br.y+br.height/2-r.y)/g.zoom,d=Math.max(70,Math.abs(tx-x)/2);const p=document.createElementNS(svg.namespaceURI,'path');p.setAttribute('d',`M ${x} ${y} C ${x+d} ${y}, ${tx-d} ${ty}, ${tx} ${ty}`);p.dataset.signal=states[source]===undefined?'idle':String(states[source]);p.dataset.execution=String(g.nodes.find(n=>n.id===source)?.kind==='start');p.onclick=()=>{n.inputs[i]='';save();render();};svg.append(p);});}
        viewport.onwheel=e=>{e.preventDefault();if(e.ctrlKey||e.metaKey){g.zoom=Math.max(.25,Math.min(2,g.zoom-e.deltaY*.002));}else{g.panX=Math.max(-10000,Math.min(10000,g.panX-e.deltaX));g.panY=Math.max(-10000,Math.min(10000,g.panY-e.deltaY));}transform();sync();};
        const drawWires=wires;
        stage.addEventListener('pointerdown',event=>{const card=event.target.closest('.ar-source');if(card&&!event.target.closest('header'))card.querySelector('header').onpointerdown(event);});
        wires=function(){drawWires();const start=stage.querySelector('[data-kind=start]');if(!start)return;for(const source of stage.querySelectorAll('[data-source]')){const x=parseFloat(source.style.left)+230,y=parseFloat(source.style.top)+45,tx=parseFloat(start.style.left),ty=parseFloat(start.style.top)+35;const p=document.createElementNS(svg.namespaceURI,'path');p.setAttribute('d',`M${x} ${y} C${x+100} ${y},${tx-100} ${ty},${tx} ${ty}`);p.dataset.execution='true';p.dataset.signal=states['filter:'+source.dataset.source]===undefined?'idle':String(states['filter:'+source.dataset.source]);p.style.pointerEvents='none';svg.append(p);}const output=g.nodes.find(n=>n.kind==='delete');if(filterMode&&output)for(const card of stage.querySelectorAll('[data-target]')){if(!route.outputs.includes(card.dataset.target))continue;const x=output.x+230,y=output.y+55,tx=parseFloat(card.style.left),ty=parseFloat(card.style.top)+35;const p=document.createElementNS(svg.namespaceURI,'path');p.setAttribute('d',`M${x} ${y} C${x+100} ${y},${tx-100} ${ty},${tx} ${ty}`);p.dataset.signal=states[output.id]===undefined?'idle':String(states[output.id]);p.onclick=()=>{route.outputs=route.outputs.filter(id=>id!==card.dataset.target);sync();render();};svg.append(p);}};
        const observer=typeof ResizeObserver==='function'?new ResizeObserver(wires):null;observer?.observe(viewport);render();sync();
        return {dispose(){disposed=true;observer?.disconnect();root.remove();},trace(values){if(!disposed){states=values;render();}}};
    }
    function check(g,depth=0,budget={n:0}){if(!g||g.version!==1||depth>4||!Array.isArray(g.nodes)||g.nodes.length>128||(budget.n+=g.nodes.length)>256)throw Error();const ids=new Set();for(const n of g.nodes){if(!n||typeof n.id!=='string'||!/^[a-zA-Z0-9_-]{1,100}$/.test(n.id)||ids.has(n.id)||!['start','delete','condition','and','or','not','custom'].includes(n.kind)||!Number.isFinite(n.x)||!Number.isFinite(n.y)||!Array.isArray(n.inputs)||n.inputs.length>128)throw Error();ids.add(n.id);if(n.kind==='custom')check(n.circuit,depth+1,budget);}if(g.nodes.filter(n=>n.kind==='start').length!==1||g.nodes.filter(n=>n.kind==='delete').length!==1||g.nodes.some(n=>n.inputs.some(i=>!ids.has(i))))throw Error();JSON.stringify(g);}
    window.RemovalCircuit={mount,check,conditions};
})();
