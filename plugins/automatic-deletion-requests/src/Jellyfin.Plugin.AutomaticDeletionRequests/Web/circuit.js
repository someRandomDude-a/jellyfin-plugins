(() => {
    'use strict';
    const copy = x => JSON.parse(JSON.stringify(x)), id = () => crypto.randomUUID();
    const fallback=['title','year','added','rating','kind','seerr.ownRequest','seerr.is4k','seerr.requestOnly','seerr.requesterId'].map(id=>({id,source:id.startsWith('seerr.')?'seerr':'jellyfin',label:id,type:id==='seerr.requesterId'?'user':id.startsWith('seerr.')?'boolean':id==='added'?'date':['title','kind'].includes(id)?'text':'number'}));
    const catalog=()=>window.RemovalMetadataCatalog||fallback;
    const definition=f=>catalog().find(x=>x.id===f);
    const booleanField = f => definition(f)?.type==='boolean';
    const textField = f => ['text','user'].includes(definition(f)?.type);
    const ops = f => definition(f)?.operators|| (booleanField(f)||definition(f)?.type==='user'?['equals','not-equals']:definition(f)?.type==='date'?['older-days','newer-days']:textField(f)?['equals','not-equals','contains']:['equals','not-equals','greater','less']);
    const singleList=op=>['contains','not-contains','contains-partial','not-contains-partial'].includes(op);
    const absoluteDate=(field,op)=>definition(field)?.type==='date'&&['before','after','equals','not-equals'].includes(op);
    const defaultValue=(field,operator)=>['exists','not-exists'].includes(operator)?null:booleanField(field)?true:textField(field)||absoluteDate(field,operator)?'':definition(field)?.type==='list'&&!operator.startsWith('count-')?(singleList(operator)?'':[]):30;
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
    function upgrade(g) {
        if(g.version===2)return g;
        const start=g.nodes.find(n=>n.kind==='start');g.nodes=g.nodes.filter(n=>n.kind!=='start');
        const cards=new Map();
        for(const n of g.nodes){if(n.kind==='delete')n.inputs=n.inputs.slice(0,1);if(n.kind==='custom')upgrade(n.circuit);if(n.kind==='condition'){n.inputs=[n.condition.field,n.condition.referenceField].filter(Boolean).map(field=>{const source=definition(field)?.source;if(!source)throw Error('Unknown metadata source');if(!cards.has(source))cards.set(source,{id:id(),kind:'metadata',source,expanded:false,x:start?.x||40,y:(start?.y||40)+cards.size*220,inputs:[]});return cards.get(source).id;});}}
        g.nodes.push(...cards.values());g.version=2;return g;
    }
    function mount(host, rule, changed, {sources=[],allSources=sources,filterMode=false,targets=[],route=null,onSource=()=>{},onEditSource=onSource,onRoute=()=>{}}={}) {
        rule.circuit = upgrade(rule.circuit||legacy(rule)); const g=rule.circuit;
        g.sources = Object.assign(Object.create(null),g.sources||{});
        if(allSources.length&&g.panX===0){g.panX=380;g.zoom=Math.min(g.zoom,.6);}
        const el=(tag,text)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;return e;};
        const root=el('div');root.className='ar-circuit dialog paperList';host.append(root);
        const toolbar=el('div');toolbar.className='ar-circuit-tools';root.append(toolbar);
        const btn=(parent,text,fn)=>{const b=el('button',text);b.type='button';b.onclick=fn;parent.append(b);return b;};
        const message=el('span');message.setAttribute('role','status');
        const viewport=el('div');viewport.className='ar-viewport';viewport.tabIndex=0;
        const stage=el('div');stage.className='ar-stage';viewport.append(stage);
        const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.classList.add('ar-wires');stage.append(svg);
        const palette=el('aside');palette.className='ar-palette';palette.append(el('h3','Blocks'));
        const layout=el('div');layout.className='ar-circuit-layout';layout.append(viewport,palette);root.append(layout);
        const selection=el('select');selection.setAttribute('aria-label','Block to edit');toolbar.append(selection);
        let selected, linking, linkingField, drag, states={}, disposed=false, libraryLink=null;
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
            if(kind==='condition'){n.condition={field:'added',operator:'older-days',value:30};n.inputs=[''];}
            if(kind==='metadata'){n.source=custom.source;n.expanded=false;}
            else if(custom){n.circuit=upgrade(copy(custom.circuit));n.name=custom.name;}
            g.nodes.push(n);selected=n.id;save();render();
        }
        function paletteButton(label,kind,item){const b=btn(palette,label,()=>add(kind,160-g.panX/g.zoom,120-g.panY/g.zoom,item));b.draggable=true;b.ondragstart=e=>e.dataTransfer.setData('text/plain',JSON.stringify({kind,source:item?.source,index:item?custom.indexOf(item):-1}));}
        for(const source of ['jellyfin','seerr','sonarr','radarr'])paletteButton(source[0].toUpperCase()+source.slice(1)+' metadata','metadata',{source});
        for(const kind of ['condition','and','or','not'])paletteButton(kind==='condition'?'Condition':kind.toUpperCase(),kind);
        palette.title='Drag a block onto the canvas. Connect output to input. Use + to add an AND/OR input.';
        viewport.ondragover=e=>e.preventDefault();viewport.ondrop=e=>{e.preventDefault();try{const data=JSON.parse(e.dataTransfer.getData('text/plain'));if(!['condition','and','or','not','custom','metadata'].includes(data.kind))return;if(data.kind==='metadata'&&!['jellyfin','seerr','sonarr','radarr'].includes(data.source))return;const r=viewport.getBoundingClientRect();add(data.kind,(e.clientX-r.left-g.panX)/g.zoom,(e.clientY-r.top-g.panY)/g.zoom,data.kind==='metadata'?{source:data.source}:custom[data.index]);}catch{}};
        function connect(target,index,source){if(!source)return;const n=g.nodes.find(n=>n.id===target),from=g.nodes.find(n=>n.id===source);if(!n||!from||target===source||from.kind==='delete')return;
            if((n.kind==='condition')!==(from.kind==='metadata')){message.textContent='Metadata connects to conditions; Boolean outputs connect to gates or the result.';return;}
            if(from.kind==='metadata'){const f=definition(linkingField);if(!f||f.source!==from.source)return;
                if(index===1){const left=definition(n.condition.field);if(f.type!==left?.type||f.unit!==left?.unit){message.textContent='Metadata operands must have the same type and units.';return;}n.condition.referenceField=f.id;}
                else{n.condition={field:f.id,operator:ops(f.id)[0],value:defaultValue(f.id,ops(f.id)[0])};n.inputs=n.inputs.slice(0,1);}
            }
            const reaches=(at,seen=new Set())=>{if(at===target)return true;if(seen.has(at))return false;seen.add(at);return (g.nodes.find(n=>n.id===at)?.inputs||[]).some(x=>reaches(x,seen));};
            if(reaches(source)){message.textContent='Cycles are not allowed.';return;}
            n.inputs[index]=source;linking=null;linkingField=null;save();render();
        }
        function socket(n,index,out=false,extra=false){const b=el('button',out?'●':extra?'＋':'●');b.type='button';b.className='ar-socket '+(out?'ar-output':'ar-input');b.dataset.port=out?'out':index;b.dataset.owner=n.id;b.title=out?'Output':extra?'Connect another input':'Input '+(index+1);b.setAttribute('aria-label',n.kind+' '+b.title);
            const source=out?n.id:n.inputs[index];if(states[source]!==undefined)b.dataset.signal=String(states[source]);
            b.onclick=e=>{e.stopPropagation();if(out){linking=n.id;linkingField=null;message.textContent='Select an input socket.';}else if(linking)connect(n.id,index,linking);};
            b.onpointerdown=e=>{e.stopPropagation();if(out){linking=n.id;linkingField=null;b.setPointerCapture?.(e.pointerId);}};
            b.onpointerup=e=>{if(!out||!linking)return;const target=document.elementFromPoint?.(e.clientX,e.clientY)?.closest('.ar-input');if(target)connect(target.dataset.owner,Number(target.dataset.port),linking);};
            return b;
        }
        function control(parent,value,values,change,label=values?'Condition comparison':'Condition value'){const input=el(values?'select':'input');if(values)for(const v of values){const o=el('option',Array.isArray(v)?v[1]:v);o.value=Array.isArray(v)?v[0]:v;input.append(o);}else input.type='text';input.value=value;input.setAttribute('aria-label',label);input.onchange=()=>change(input.value);parent.append(input);return input;}
        function render(){stage.querySelectorAll('.ar-block').forEach(e=>e.remove());selection.replaceChildren();const placeholder=el('option','Select block');placeholder.value='';selection.append(placeholder);
            for(const n of g.nodes){const option=el('option',n.name||n.kind.toUpperCase());option.value=n.id;selection.append(option);const block=el('section');block.className='ar-block paperList';block.dataset.id=n.id;block.dataset.kind=n.kind;block.style.left=n.x+'px';block.style.top=n.y+'px';
                if(['and','or'].includes(n.kind)){n.inputs=n.inputs.filter(Boolean);while(n.inputs.length<2)n.inputs.push('');block.style.minHeight=(60+n.inputs.length*36)+'px';}
                const title=el('header',n.name||n.kind.toUpperCase());block.append(title);title.onpointerdown=e=>{if(e.button!==0)return;selected=n.id;selection.value=n.id;drag={id:n.id,x:e.clientX,y:e.clientY,ox:n.x,oy:n.y};title.setPointerCapture?.(e.pointerId);e.preventDefault();};
                if(['and','or','not'].includes(n.kind)) {
                    block.classList.add('ar-logic-gate');block.setAttribute('aria-label',n.kind.toUpperCase()+' gate');block.title=n.kind==='and'?'True only when every input is true.':n.kind==='or'?'True when any input is true. Unknown required evidence blocks execution.':'Inverts a known Boolean value. Unknown evidence stays blocked.';
                    title.textContent='';title.setAttribute('aria-label','Move '+n.kind.toUpperCase()+' gate');
                    const shape=document.createElementNS(svg.namespaceURI,'svg');shape.classList.add('ar-gate-shape');shape.setAttribute('viewBox','0 0 230 120');shape.setAttribute('preserveAspectRatio','none');shape.setAttribute('aria-hidden','true');const path=document.createElementNS(svg.namespaceURI,'path');path.setAttribute('d',n.kind==='and'?'M10 5 H110 C250 5 250 115 110 115 H10 Z':n.kind==='or'?'M10 5 Q145 5 215 60 Q145 115 10 115 Q65 60 10 5 Z':'M10 5 L200 60 L10 115 Z M225 60 A10 10 0 1 1 205 60 A10 10 0 1 1 225 60');shape.append(path);block.prepend(shape);
                }
                title.onpointermove=e=>{if(drag?.id!==n.id)return;n.x=Math.max(-10000,Math.min(10000,drag.ox+(e.clientX-drag.x)/g.zoom));n.y=Math.max(-10000,Math.min(10000,drag.oy+(e.clientY-drag.y)/g.zoom));block.style.left=n.x+'px';block.style.top=n.y+'px';wires();};
                title.onpointerup=()=>{drag=null;sync();};title.onpointercancel=()=>{drag=null;sync();};
                if(n.kind==='metadata'){
                    title.textContent=n.source[0].toUpperCase()+n.source.slice(1)+' metadata';
                    const expand=btn(block,n.expanded?'Collapse':'Show outputs',()=>{n.expanded=!n.expanded;render();sync();});expand.setAttribute('aria-expanded',String(!!n.expanded));
                    block.dataset.sourceType=n.source;block.onclick=e=>{if(e.target===title){n.expanded=!n.expanded;render();sync();}};
                    if(n.expanded)for(const f of catalog().filter(f=>f.source===n.source)){
                        const row=el('div');row.className='ar-metadata-row';row.append(el('span',f.label+' · '+f.type+(f.unit?' ('+f.unit+')':'')));
                        const port=el('button','●');port.type='button';port.className='ar-socket ar-output';port.dataset.field=f.id;port.dataset.owner=n.id;port.title=f.label;port.setAttribute('aria-label',n.source+' '+f.label+' output');
                        const select=()=>{linking=n.id;linkingField=f.id;message.textContent='Connect '+f.label+' to a condition.';};port.onclick=e=>{e.stopPropagation();select();};port.onpointerdown=e=>{e.stopPropagation();select();port.setPointerCapture?.(e.pointerId);};port.onpointerup=e=>{const target=document.elementFromPoint?.(e.clientX,e.clientY)?.closest('.ar-input');if(target)connect(target.dataset.owner,Number(target.dataset.port),n.id);};row.append(port);block.append(row);
                    }
                }
                if(n.kind==='condition'){
                    const c=n.condition,f=definition(c.field),connected=g.nodes.some(m=>m.id===n.inputs[0]&&m.kind==='metadata'&&m.source===f?.source);
                    block.append(el('p',connected?f.label:'Connect a metadata output'));
                    if(connected){const comparable=['equals','not-equals','greater','less','before','after'];control(block,c.operator,c.referenceField?ops(c.field).filter(op=>comparable.includes(op)):ops(c.field),v=>{c.operator=v;c.value=defaultValue(c.field,v);save();render();});
                        if(f.type!=='list'&&comparable.includes(c.operator))control(block,c.referenceField?'metadata':'value',[['value','Compare with a value'],['metadata','Compare with metadata']],v=>{if(v==='metadata'){c.referenceField=c.field;n.inputs=[n.inputs[0],''];}else{delete c.referenceField;n.inputs=n.inputs.slice(0,1);}save();render();},'Comparison operand');
                        if(c.referenceField)block.append(el('p','Connect operand 2: '+(definition(c.referenceField)?.label||c.referenceField)));
                        if(!c.referenceField&&!['exists','not-exists'].includes(c.operator)){
                        const list=f.type==='list'&&!c.operator.startsWith('count-')&&!singleList(c.operator);
                        let values=booleanField(c.field)?['true','false']:null;
                        if(f.type==='user'&&window.RemovalMetadataUsers){values=[['','Choose a user'],...window.RemovalMetadataUsers.map(u=>[u.id,u.name])];if(c.value&&!values.some(v=>v[0]===c.value))values.push([c.value,'Saved user (unavailable)']);}
                        const input=control(block,list?JSON.stringify(c.value):c.value,values,v=>{try{c.value=booleanField(c.field)?v==='true':list?JSON.parse(v):textField(c.field)||absoluteDate(c.field,c.operator)||f.type==='list'&&!c.operator.startsWith('count-')?v:Number(v);save();}catch{message.textContent='Enter a JSON list of text values.';}},'Condition value');
                        if(!booleanField(c.field)&&!textField(c.field)&&!list&&!(f.type==='list'&&!c.operator.startsWith('count-'))&&!absoluteDate(c.field,c.operator)){input.type='number';input.step=f.type==='date'||c.operator.startsWith('count-')?'1':'any';if(f.type==='date'||c.operator.startsWith('count-'))input.min='0';}
                        if(absoluteDate(c.field,c.operator)){
                            input.type='datetime-local';const value=new Date(c.value);if(!Number.isNaN(value.valueOf()))input.value=new Date(value.getTime()-value.getTimezoneOffset()*60000).toISOString().slice(0,16);
                            input.onchange=()=>{const date=new Date(input.value);if(!Number.isNaN(date.valueOf())){c.value=date.toISOString();save();}};
                        }
                        input.placeholder=f.type==='user'?'Jellyfin user ID':list?'["value"]':f.unit||'';
                        if(f.unit)block.append(el('p',f.unit));
                        }
                    }
                }
                if(n.kind==='delete'){title.textContent=filterMode?'OUTPUT':'DELETE';block.title=filterMode?'Matching items flow to attached rules.':'True requests removal; false stops. Consent, approval and safety delay still apply.';}
                if(n.kind==='custom')block.title='Reusable circuit · '+conditions(n.circuit).length+' conditions';
                for(let i=0;i<n.inputs.length;i++){const row=el('div');row.className='ar-port-row';row.append(socket(n,i),el('span',n.kind==='condition'?'Metadata':'Boolean '+(i+1)));block.append(row);}
                if(['and','or'].includes(n.kind)&&n.inputs.length<128){const row=el('div');row.className='ar-port-row';row.append(socket(n,n.inputs.length,false,true),el('span','Add input'));block.append(row);}
                if(!['delete','metadata'].includes(n.kind))block.append(socket(n,0,true));
                else if(filterMode&&route){const port=el('button','●');port.type='button';port.className='ar-socket ar-output';port.setAttribute('aria-label','Filter output to rule');port.onclick=()=>{routing=true;message.textContent='Select a rule input to attach it.';};port.onpointerdown=e=>{e.stopPropagation();routing=true;port.setPointerCapture?.(e.pointerId);};port.onpointerup=e=>{const target=document.elementFromPoint?.(e.clientX,e.clientY)?.closest('[data-route-target]');if(target){if(!route.outputs.includes(target.dataset.routeTarget))route.outputs.push(target.dataset.routeTarget);routing=false;sync();render();}};block.append(port);}
                stage.append(block);
            }
            if(!filterMode&&allSources.length){
                const scope=el('section');scope.className='ar-block ar-scope-anchor paperList';scope.style.left='-260px';scope.style.top='40px';scope.append(el('header','Library inputs'));
                const port=btn(scope,'●',()=>{if(libraryLink)onRoute(libraryLink,true);});port.className='ar-socket ar-input';port.setAttribute('aria-label','Connect library input');stage.append(scope);
            }
            if(!filterMode)for(const [i,source]of allSources.entries()){
                const card=el('section');card.className='ar-block ar-source';const position=g.sources[source.id]||{x:-600,y:i*170};card.style.left=position.x+'px';card.style.top=position.y+'px';card.dataset.source=source.id;
                const attached=source.outputs?.includes(rule.id)??sources.some(s=>s.id===source.id);card.dataset.attached=String(attached);
                const title=el('header','LIBRARY FILTER · '+source.name);card.append(title);let moving,moved=false;
                title.onpointerdown=e=>{moved=false;moving={x:e.clientX,y:e.clientY,ox:position.x,oy:position.y};title.setPointerCapture?.(e.pointerId);};title.onpointermove=e=>{if(!moving)return;const dx=(e.clientX-moving.x)/g.zoom,dy=(e.clientY-moving.y)/g.zoom;moved ||= Math.abs(dx)+Math.abs(dy)>4;position.x=Math.max(-10000,Math.min(10000,moving.ox+dx));position.y=Math.max(-10000,Math.min(10000,moving.oy+dy));card.style.left=position.x+'px';card.style.top=position.y+'px';wires();};title.onpointerup=title.onpointercancel=()=>{moving=null;g.sources[source.id]=position;sync();};
                card.onclick=e=>{if(!moved&&!e.target.closest('button'))onEditSource(source);};card.tabIndex=0;card.setAttribute('role','group');card.setAttribute('aria-label','Library filter '+source.name);card.onkeydown=e=>{if(e.target===card&&(e.key==='Enter'||e.key===' ')){e.preventDefault();onEditSource(source);}};
                const port=btn(card,'●',()=>{libraryLink=source;message.textContent='Select Library inputs to connect.';});port.className='ar-socket ar-output';port.setAttribute('aria-label','Library output '+source.name);
                btn(card,attached?'Disconnect':'Connect',()=>onRoute(source,!attached));btn(card,'Test path',()=>onSource(source));stage.append(card);
            }
            const output=g.nodes.find(n=>n.kind==='delete');
            if(filterMode&&output)for(const [i,target]of targets.entries()){const card=el('section');card.className='ar-block ar-route-target';card.style.left=(output.x+330)+'px';card.style.top=(output.y+i*130)+'px';card.dataset.target=target.id;card.append(el('header','RULE · '+target.name));const port=el('button','●');port.type='button';port.className='ar-socket ar-input';port.dataset.routeTarget=target.id;port.setAttribute('aria-label','Attach '+target.name);port.onclick=()=>{if(!routing)return;if(!route.outputs.includes(target.id))route.outputs.push(target.id);routing=false;sync();render();};card.append(port,el('p','Library input'));stage.append(card);}
            stage.querySelectorAll('.ar-block').forEach(block=>block.classList.add('paperList'));
            selection.value=selected||'';transform();wires();
        }
        selection.onchange=()=>{selected=selection.value;const n=g.nodes.find(n=>n.id===selected);if(!n)return;g.panX=40-n.x*g.zoom;g.panY=40-n.y*g.zoom;transform();sync();};
        function wires(){svg.replaceChildren();for(const n of g.nodes)n.inputs.forEach((source,i)=>{
            if(!source)return;const from=g.nodes.find(n=>n.id===source),metadata=from?.kind==='metadata';
            const card=stage.querySelector(`[data-id="${CSS.escape(source)}"]`);
            const a=metadata?card?.querySelector(`[data-field="${CSS.escape(n.condition?.field||'')}"]`)||card?.querySelector('header'):card?.querySelector('.ar-output');
            const b=stage.querySelector(`[data-id="${CSS.escape(n.id)}"] [data-port="${i}"]`);if(!a||!b)return;
            const ar=a.getBoundingClientRect(),br=b.getBoundingClientRect(),r=stage.getBoundingClientRect();const x=(ar.x+ar.width/2-r.x)/g.zoom,y=(ar.y+ar.height/2-r.y)/g.zoom,tx=(br.x+br.width/2-r.x)/g.zoom,ty=(br.y+br.height/2-r.y)/g.zoom,d=Math.max(70,Math.abs(tx-x)/2);const p=document.createElementNS(svg.namespaceURI,'path');p.setAttribute('d',`M ${x} ${y} C ${x+d} ${y}, ${tx-d} ${ty}, ${tx} ${ty}`);p.dataset.signal=states[source]===undefined?'idle':String(states[source]);if(metadata)p.dataset.metadata=from.source;p.dataset.target=n.id;p.dataset.input=i;p.onclick=e=>{e.stopPropagation();contextMenu(e,n,i);};svg.append(p);
        });}
        viewport.onwheel=e=>{e.preventDefault();if(e.ctrlKey||e.metaKey){g.zoom=Math.max(.25,Math.min(2,g.zoom-e.deltaY*.002));}else{g.panX=Math.max(-10000,Math.min(10000,g.panX-e.deltaX));g.panY=Math.max(-10000,Math.min(10000,g.panY-e.deltaY));}transform();sync();};
        const drawWires=wires;
        stage.addEventListener('pointerdown',event=>{const card=event.target.closest('.ar-source');if(card&&!event.target.closest('header,button,input,select,textarea'))card.querySelector('header').onpointerdown(event);});
        wires=function(){drawWires();const scope=stage.querySelector('.ar-scope-anchor');
            if(scope)for(const source of stage.querySelectorAll('[data-source][data-attached=true]')){const x=parseFloat(source.style.left)+230,y=parseFloat(source.style.top)+45,tx=parseFloat(scope.style.left),ty=parseFloat(scope.style.top)+35;const p=document.createElementNS(svg.namespaceURI,'path');p.setAttribute('d',`M${x} ${y} C${x+100} ${y},${tx-100} ${ty},${tx} ${ty}`);p.dataset.scope=source.dataset.source;p.dataset.signal=states['filter:'+source.dataset.source]===undefined?'idle':String(states['filter:'+source.dataset.source]);p.onclick=e=>{e.stopPropagation();onRoute(allSources.find(s=>s.id===source.dataset.source),false);};svg.append(p);}
            const output=g.nodes.find(n=>n.kind==='delete');if(filterMode&&output)for(const card of stage.querySelectorAll('[data-target]')){if(!route.outputs.includes(card.dataset.target))continue;const x=output.x+230,y=output.y+55,tx=parseFloat(card.style.left),ty=parseFloat(card.style.top)+35;const p=document.createElementNS(svg.namespaceURI,'path');p.setAttribute('d',`M${x} ${y} C${x+100} ${y},${tx-100} ${ty},${tx} ${ty}`);p.dataset.signal=states[output.id]===undefined?'idle':String(states[output.id]);p.onclick=()=>{route.outputs=route.outputs.filter(id=>id!==card.dataset.target);sync();render();};svg.append(p);}};
        let menu=null,panning=null;
        const closeMenu=()=>{menu?.remove();menu=null;};
        function contextMenu(event,target=null,input=0) {
            closeMenu();menu=el('div');menu.className='ar-canvas-menu dialog paperList';menu.setAttribute('role','menu');
            const r=viewport.getBoundingClientRect(),x=(event.clientX-r.left-g.panX)/g.zoom,y=(event.clientY-r.top-g.panY)/g.zoom;
            const choice=(label,run)=>{const b=btn(menu,label,()=>{closeMenu();run();});b.setAttribute('role','menuitem');};
            if(target){choice('Disconnect',()=>{target.inputs.splice(input,1);if(!['and','or'].includes(target.kind))target.inputs=[''];save();render();});if(target.inputs[input]&&target.kind!=='condition')for(const kind of ['not','and','or'])choice('Insert '+kind.toUpperCase(),()=>{if(g.nodes.length>=128)return;const source=target.inputs[input],n={id:id(),kind,x,y,inputs:kind==='not'?[source]:[source,'']};g.nodes.push(n);target.inputs[input]=n.id;save();render();});}
            else for(const kind of ['condition','and','or','not'])choice(kind==='condition'?'Condition':kind.toUpperCase(),()=>add(kind,x,y));
            choice('Cancel',()=>{});root.append(menu);menu.style.left=Math.max(0,Math.min(event.clientX-root.getBoundingClientRect().left,root.clientWidth-180))+'px';menu.style.top=Math.max(0,event.clientY-root.getBoundingClientRect().top)+'px';menu.querySelector('button')?.focus();
        }
        viewport.addEventListener('contextmenu',event=>{if(event.target.closest('.ar-source'))return;event.preventDefault();const wire=event.target.closest('path[data-target]');contextMenu(event,wire?g.nodes.find(n=>n.id===wire.dataset.target):null,Number(wire?.dataset.input||0));});
        viewport.addEventListener('pointerdown',event=>{if(event.button!==1||event.target.closest('.ar-block,button,input,select,textarea'))return;event.preventDefault();closeMenu();panning={x:event.clientX,y:event.clientY,px:g.panX,py:g.panY};viewport.setPointerCapture?.(event.pointerId);});
        viewport.addEventListener('pointermove',event=>{if(!panning)return;g.panX=Math.max(-10000,Math.min(10000,panning.px+event.clientX-panning.x));g.panY=Math.max(-10000,Math.min(10000,panning.py+event.clientY-panning.y));transform();});
        const endPan=()=>{if(panning){panning=null;sync();}};viewport.addEventListener('pointerup',endPan);viewport.addEventListener('pointercancel',endPan);
        root.addEventListener('keydown',event=>{if(event.key==='Escape')closeMenu();});
        root.addEventListener('pointerdown',event=>{if(!event.target.closest('.ar-canvas-menu')&&!event.target.closest('path'))closeMenu();});
        const observer=typeof ResizeObserver==='function'?new ResizeObserver(wires):null;observer?.observe(viewport);render();sync();
        return {dispose(){disposed=true;closeMenu();observer?.disconnect();root.remove();},trace(values){if(!disposed){states=values;render();}}};
    }
    function check(g,depth=0,budget={n:0}){
        if(!g||![1,2].includes(g.version)||depth>4||!Array.isArray(g.nodes)||g.nodes.length<3||g.nodes.length>128||(budget.n+=g.nodes.length)>256)throw Error();
        const ids=new Map();for(const n of g.nodes){if(!n||typeof n.id!=='string'||!/^[a-zA-Z0-9_-]{1,100}$/.test(n.id)||ids.has(n.id)||!['start','delete','condition','and','or','not','custom','metadata'].includes(n.kind)||!Number.isFinite(n.x)||!Number.isFinite(n.y)||!Array.isArray(n.inputs)||n.inputs.length>128)throw Error();ids.set(n.id,n);if(n.kind==='custom')check(n.circuit,depth+1,budget);}
        if(g.nodes.filter(n=>n.kind==='start').length!==(g.version===1?1:0)||g.nodes.filter(n=>n.kind==='delete').length!==1||g.nodes.some(n=>n.inputs.some(i=>!ids.has(i))))throw Error();
         for(const n of g.nodes){if(['and','or'].includes(n.kind)&&n.inputs.length<2)throw Error();if(g.version===2){if(n.kind==='metadata'&&(!['jellyfin','seerr','sonarr','radarr'].includes(n.source)||n.inputs.length))throw Error();if(n.kind==='condition'&&(n.inputs.length!==(n.condition?.referenceField?2:1)||ids.get(n.inputs[0]).kind!=='metadata'||ids.get(n.inputs[0]).source!==definition(n.condition?.field)?.source))throw Error();if(n.kind==='condition'&&n.condition?.referenceField&&(ids.get(n.inputs[1]).kind!=='metadata'||ids.get(n.inputs[1]).source!==definition(n.condition.referenceField)?.source))throw Error();if(n.kind==='delete'&&n.inputs.length!==1)throw Error();}}
        const active=new Set(),done=new Set();function visit(id,depth=0){if(active.has(id)||depth>64)throw Error();if(done.has(id))return;active.add(id);for(const input of ids.get(id).inputs)visit(input,depth+1);active.delete(id);done.add(id);}for(const n of g.nodes)visit(n.id);
        JSON.stringify(g);
    }
    window.RemovalCircuit={mount,check,conditions,upgrade};
})();
