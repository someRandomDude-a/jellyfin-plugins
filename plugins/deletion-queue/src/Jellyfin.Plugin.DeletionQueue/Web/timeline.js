(() => {
    'use strict';
    if(window.DeletionQueueTimeline)return;
    const get=(o,k)=>o?.[k]??o?.[k[0].toUpperCase()+k.slice(1)];
    const node=(tag,text,cls)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(cls)e.className=cls;return e;};
    const date=value=>{const d=value?new Date(value):null;return d&&Number.isFinite(d.getTime())?d:null;};
    const state=p=>get(p,'state')||'pending';
    const frame=window.requestAnimationFrame?.bind(window)||(fn=>setTimeout(fn,0));
    const duration=(point)=>{
        const days=get(point,'durationDays'),hours=get(point,'durationHours'),key=get(point,'key');
        if(hours!=null)return Number((hours/24).toFixed(4))+' days ('+hours+' hours)';
        if(days==null)return '';
        return key==='activity'?days+' days lookback':key==='activity-timeout'?days+'-day waiting limit':key==='consent'?days+'-day request limit':days+'-day decision limit';
    };
    function render(p,content,{preview=false}={}){
        const timeline=get(p,'timeline')||[],find=key=>timeline.find(s=>get(s,'key')===key);
        const checks=find('checks'),paths=get(checks,'paths')||[];if(!paths.length)return false;
        const approval=find('approval'),approvalPaths=get(approval,'paths')||[],status=get(p,'status');
        const ended=['Completed','Cancelled','Expired'].includes(status),cancelled=ended&&status!=='Completed';
        const root=node('div',undefined,'dq-timeline dq-flow-timeline');root.tabIndex=0;root.setAttribute('aria-label',preview?'Removal policy timeline':'Removal request timeline');
        root.setAttribute('aria-description','All users inactive OR every relevant active viewer consents. Both paths require verified history, no active playback and all safety checks. Administrator denial or timeout closes the request when approval is required.');
        if(get(p,'closureExplanation'))root.setAttribute('aria-description',root.getAttribute('aria-description')+' '+get(p,'closureExplanation'));
        const grid=node('div',undefined,'dq-flow-grid'),list=node('ol',undefined,'dq-flow-nodes');root.append(grid);grid.append(list);
        const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.classList.add('dq-flow-wires');svg.setAttribute('aria-hidden','true');grid.prepend(svg);
        const labels=node('div',undefined,'dq-flow-labels');labels.setAttribute('aria-hidden','true');grid.append(labels);
        const points=new Map(),edges=[],elements=new Map();
        const add=(point,column,row)=>{if(!point)return;const key=get(point,'key');points.set(key,point);const li=node('li',undefined,'dq-flow-node paperList');li.dataset.checkpoint=key;li.dataset.state=state(point);li.style.gridColumn=String(column);li.style.gridRow=String(row);
            const icons={requested:'verified_user',activity:'manage_history',consent:'how_to_vote',checks:'fact_check',approval:'admin_panel_settings',approved:'check', 'approval-timeout':'timer_off','activity-timeout':'timer_off',delay:'hourglass_top',removing:'auto_delete',completed:'task_alt',cancelled:'cancel'};
            const glyph=node('span',icons[key]||'radio_button_unchecked','material-icons dq-flow-dot');glyph.setAttribute('aria-hidden','true');li.append(glyph,node('span',get(point,'label'),'dq-checkpoint-label'));
            const wait=duration(point);
            const at=date(get(point,'at'));li.tabIndex=0;
            if(at){const evaluated=date(get(p,'evaluatedAt'))||new Date(),days=Math.round((at-evaluated)/86400000),abandoned=ended&&!['requested','completed','cancelled','approval-timeout'].includes(key)&&['stopped','pending','cancelled'].includes(state(point));const text=key==='requested'?at.toLocaleDateString():abandoned?'Stopped':days===0?'Today':days>0?'In '+days+' days':Math.abs(days)+' days ago';const time=node('time',text);time.dateTime=at.toISOString();time.title=(get(point,'dateLabel')?get(point,'dateLabel')+' · ':'')+at.toLocaleString();li.title=time.title;
                const show=()=>{time.textContent=at.toLocaleString();},hide=()=>{time.textContent=text;};li.addEventListener('mouseenter',show);li.addEventListener('mouseleave',hide);li.addEventListener('focus',show);li.addEventListener('blur',hide);li.append(time);}
            li.setAttribute('aria-label',[get(point,'label'),preview?'Preview':state(point),wait,at?.toLocaleString()].filter(Boolean).join(' · '));list.append(li);elements.set(key,li);
        };
        const edge=(from,to,value='pending',label='')=>edges.push({from,to,state:value,label});
        const activity=paths.find(s=>get(s,'key')==='activity'),consent=paths.find(s=>get(s,'key')==='consent'),activityTimeout=paths.find(s=>get(s,'key')==='activity-timeout');
        const passed=paths.some(s=>state(s)==='complete');
        add(find('requested'),1,2);add(activity,2,1);add(consent,2,3);add({...checks,key:'checks',label:'Verify safeguards',state:passed?'complete':state(checks)},3,2);
        edge('requested','activity',state(activity),duration(activity));edge('requested','consent',state(consent),duration(consent)||'Current snapshot');edge('activity','checks',state(activity));edge('consent','checks',state(consent));
        if(activityTimeout){add(activityTimeout,3,5);edge('requested','activity-timeout',state(activityTimeout),duration(activityTimeout));edge('activity-timeout','cancelled',state(activityTimeout));}
        let next=4;
        if(approval&&approvalPaths.length){
            const approved=approvalPaths.find(s=>get(s,'key')==='approved'),timeout=approvalPaths.find(s=>get(s,'key')==='approval-timeout');
            add({...approval,label:'Administrator review',durationDays:null},4,2);add(approved,5,1);add(timeout,5,3);
            edge('checks','approval',passed?'complete':'pending');edge('approval','approved',state(approved));edge('approval','approval-timeout',state(timeout),duration(timeout));edge('approval-timeout','cancelled',state(timeout));next=6;
        }
        add(find('delay'),next,1);add(find('removing'),next+1,1);
        const closed=find('closed'),confirmed=find('confirmed');
        add({key:'completed',label:'Completed',state:status==='Completed'?'complete':cancelled?'stopped':'pending',at:status==='Completed'?get(closed,'at')||get(confirmed,'at'):null},next+2,1);
        add({key:'cancelled',label:'Cancelled',state:cancelled?'cancelled':status==='Completed'?'stopped':'pending',at:cancelled?get(closed,'at')||get(p,'closedAt'):null},next+2,activityTimeout?5:3);
        edge(approval&&approvalPaths.length?'approved':'checks','delay',['complete','current','cancelled'].includes(state(find('delay')))?'complete':'pending');
        edge('delay','removing',['complete','current'].includes(state(find('removing')))?'complete':'pending',duration(find('delay')||{}));edge('removing','completed',status==='Completed'?'complete':'pending');
        const closedAt=get(p,'closedCheckpoint');
        if(cancelled&&!['AdministratorDenied','AdministratorApprovalExpired'].includes(get(p,'reason'))&&['checks','delay','approval'].includes(closedAt))edge(closedAt,'cancelled','cancelled');
        else if(!ended&&(!approval||!approvalPaths.length))edge('checks','cancelled','pending','Undo');
        list.style.setProperty('--dq-flow-columns',String(next+2));
        let current=status==='Completed'?'completed':cancelled?'cancelled':['Claimed','Uncertain','FilesDeleted'].includes(status)?'removing':['Waiting','Ready'].includes(status)?'delay':get(p,'reason')==='AdministratorApprovalRequired'?'approval':get(p,'reason')==='ConsentRequired'?'consent':state(activity)==='current'?'activity':'checks';
        if(!preview&&elements.has(current)){const element=elements.get(current);element.setAttribute('aria-current','step');const marker=node('span',undefined,'dq-media-marker');marker.dataset.checkpoint=current;marker.dataset.stopped=String(cancelled);marker.dataset.completed=String(status==='Completed');marker.setAttribute('aria-label',ended?'Request ended':'Current stage');element.append(marker);}
        function draw(){svg.replaceChildren();labels.replaceChildren();if(!grid.isConnected)return;const bounds=grid.getBoundingClientRect();svg.setAttribute('viewBox','0 0 '+Math.max(1,grid.scrollWidth)+' '+Math.max(1,grid.scrollHeight));for(const e of edges){const from=elements.get(e.from),to=elements.get(e.to);if(!from||!to)continue;const a=from.getBoundingClientRect(),b=to.getBoundingClientRect(),x=a.right-bounds.left,y=a.top+a.height/2-bounds.top,tx=b.left-bounds.left,ty=b.top+b.height/2-bounds.top,mid=x+(tx-x)/2;const path=document.createElementNS(svg.namespaceURI,'path');let labelX=mid,labelY=(y+ty)/2;
            if(e.to==='activity-timeout'){const sx=a.left+a.width/2-bounds.left,sy=a.bottom-bounds.top;path.setAttribute('d',`M ${sx} ${sy} L ${sx} ${ty-12} Q ${sx} ${ty}, ${sx+12} ${ty} L ${tx} ${ty}`);labelX=(sx+tx)/2;labelY=ty;}
            else path.setAttribute('d',`M ${x} ${y} C ${mid} ${y}, ${mid} ${ty}, ${tx} ${ty}`);
            path.dataset.from=e.from;path.dataset.to=e.to;path.dataset.state=e.state;svg.append(path);if(e.label){const label=node('span',e.label,'dq-flow-edge-label paperList');label.dataset.from=e.from;label.dataset.to=e.to;label.style.left=labelX+'px';label.style.top=labelY+'px';labels.append(label);}}}
        const center=()=>{const marker=elements.get(current);if(!preview&&root.isConnected&&marker&&root.clientWidth){const r=marker.getBoundingClientRect(),box=root.getBoundingClientRect();root.scrollLeft+=r.left-box.left-root.clientWidth/2+r.width/2;}};
        content.append(root);draw();frame(()=>{draw();center();});
        if(typeof ResizeObserver==='function'){let width=root.clientWidth;const observer=new ResizeObserver(()=>{if(!grid.isConnected)observer.disconnect();else{draw();if(width!==root.clientWidth){width=root.clientWidth;center();}}});observer.observe(grid);observer.observe(root);}
        return true;
    }
    function policy(content,{activityDays,delayHours,approvalDays,requireApproval,paused,activityTimeoutEnabled,activityTimeoutDays}){
        const paths=[{key:'activity',label:'All users inactive',state:'pending',durationDays:activityDays},{key:'consent',label:'All active viewers consent',state:'pending'}];
        if(activityTimeoutEnabled)paths.push({key:'activity-timeout',label:'Activity/consent wait expired',state:'pending',durationDays:activityTimeoutDays});
        const timeline=[{key:'requested',label:'Request',state:'pending'},{key:'checks',label:'Verify safeguards',state:'pending',paths}];
        if(requireApproval)timeline.push({key:'approval',label:'Administrator review',state:'pending',paths:[{key:'approved',label:'Approved',state:'pending'},{key:'approval-timeout',label:'Denied or timed out',state:'pending',durationDays:approvalDays}]});
        timeline.push({key:'delay',label:'Safety delay',state:'pending',durationHours:delayHours},{key:'removing',label:'Verify & remove',state:'pending'},{key:'closed',label:'Completed',state:'pending'});
        content.replaceChildren();render({status:'Preview',timeline},content,{preview:true});content.dataset.paused=String(!!paused);
    }
    window.DeletionQueueTimeline={render,policy};
})();
