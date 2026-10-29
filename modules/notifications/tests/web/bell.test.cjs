const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('src/AccountHub.Notifications/Web/bell.js', 'utf8');
test('floating inbox, options and toast surfaces use native theme palettes with skin overrides', () => {
    const css = fs.readFileSync('src/AccountHub.Notifications/Web/bell.css', 'utf8');
    assert.match(css, /\.accountHubInbox\.dialog, \.notificationToast\.dialog\s*\{[^}]*--theme-text-color[^}]*--jf-palette-text-primary[^}]*--theme-background-color[^}]*--jf-palette-background-paper/);
    assert.match(css, /\.accountHubInbox \.notificationOptionsMenu\s*\{[^}]*color: inherit;[^}]*background-color:[^}]*--jf-palette-background-paper/);
});
test('canvas card and timestamp occupy separate grid rows without overriding ordinary inbox layouts',()=>{
    const css=fs.readFileSync('src/AccountHub.Notifications/Web/bell.css','utf8');
    assert.match(css,/\.accountHubInbox \.notificationMainCanvas \.notificationCanvas\s*\{[^}]*grid-column:\s*1\s*\/\s*-1;[^}]*grid-row:\s*1/);
    assert.match(css,/\.accountHubInbox \.notificationMain\.notificationMainCanvas time\s*\{[^}]*grid-row:\s*2/);
    const studio=fs.readFileSync('src/AccountHub.Notifications/Web/notification-studio.css','utf8');
    assert.match(studio,/\.notificationCanvas \.notificationCanvasImage img\s*\{[^}]*float:\s*none;[^}]*width:\s*100%;[^}]*height:\s*100%;[^}]*margin:\s*0/);
});
test('swipe surfaces use low-specificity theme defaults so late injection cannot override skins', () => {
    const css = fs.readFileSync('src/AccountHub.Notifications/Web/bell.css', 'utf8');
    assert.match(css, /:where\(\.accountHubInbox\) \.notificationSwipePreview \{[^}]*--theme-primary-color[^}]*--jf-palette-primary-main/);
    assert.doesNotMatch(css, /^\.accountHubInbox \.notificationSwipe/m);
    assert.doesNotMatch(css.match(/:where\(\.accountHubInbox\) \.notificationSwipePreview \{[^}]*\}/)[0], /!important|#[0-9a-f]{3,8}\b/i);
});

function harness({ href = 'https://example.test/jellyfin/web/index.html#/home', status = 200, saveStatus = 200, saveGate, actionStatus = 200, dismissStatus = 200, blocked = false, hidden = false, hasMore = false, preferences = [], noticeLink, inbox, read = false, push,categories,defaultOn=false,permission='default',permissionResult='granted',storage=new Map() } = {}) {
    const listeners = {}, calls = [], timers = [];
    let focused;
    class Element {
        constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; this.style = {}; this.attrs = {}; this.hidden = false; }
        set textContent(text) { this.text = text; this.children = []; }
        get textContent() { return (this.text || '') + this.children.map(c => c.textContent).join(''); }
        set innerHTML(_) { throw Error('HTML insertion is forbidden'); }
        append(...nodes) { for (const node of nodes) { node.parentElement = this; this.children.push(node); } }
        replaceChildren(...nodes) { for (const c of this.children) c.parentElement = null; this.children = []; this.text = ''; this.append(...nodes); }
        insertBefore(node, reference) { node.parentElement = this; this.children.splice(this.children.indexOf(reference), 0, node); }
        remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(c => c !== this); this.parentElement = null; }
        get isConnected() { return this === body || this === header || !!this.parentElement?.isConnected; }
        setAttribute(k, v) { this.attrs[k] = v; }
        getAttribute(k) { return this.attrs[k] ?? null; }
        addEventListener(k, v) { (this.events ??= {})[k] = v; }
        focus() { focused = this; }
        getClientRects() { return [{}]; }
        getBoundingClientRect() { return { bottom: 50, right: 900 }; }
        closest(selector) { if(selector==='[data-notification-id]')return this.dataset.notificationId?this:this.parentElement?.closest(selector)||null;return null; }
        querySelectorAll(selector) { return all(this).slice(1).filter(e => selector === 'button' ? e.tag === 'button' : selector==='.notificationOptions[open]'?e.className==='notificationOptions'&&e.open:selector.startsWith('.') && e.className?.split(' ').includes(selector.slice(1))); }
        querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
        contains(target) { return this === target || this.children.some(c => c.contains(target)); }
    }
    const body = new Element('body'), header = new Element('header'), anchor = new Element('button');
    body.append(header); header.append(anchor);
    function all(node = body) { return [node, ...node.children.flatMap(c => all(c))]; }
    const document = {
        body, documentElement: body, hidden,
        get activeElement(){return focused;},
        createElement: tag => new Element(tag),
        querySelector: selector => selector.includes('headerUserButton') ? anchor : all().find(e => e.dataset.accountHubBell),
        querySelectorAll: selector => selector.includes('headerUserButton') ? [anchor] : [],
        addEventListener: (name, handler) => { listeners[name] = handler; }
    };
    const window = {
        localStorage:{getItem:key=>storage.get(key),setItem:(key,value)=>storage.set(key,value)},
        ApiClient: { accessToken: () => 'session-secret', getCurrentUserId: () => 'alice', getUrl: p => { if (!p) throw Error('Url name cannot be empty'); return 'https://example.test/jellyfin/' + p; } },
        addEventListener: (name, handler) => { listeners[name] = handler; },
        dispatchEvent: event => { listeners[event.type]?.(event); }
    };
    const pushEvents=[];
    if(push){let subscription=null;const registration={scope:'https://example.test/jellyfin/AccountHub/api/v1/notifications/push/',active:true,pushManager:{getSubscription:async()=>subscription,subscribe:async options=>{pushEvents.push({kind:'subscribe',options});subscription={endpoint:'https://fcm.googleapis.com/disposable',toJSON:()=>({endpoint:'https://fcm.googleapis.com/disposable',keys:{p256dh:'public-key',auth:'auth-key'}}),unsubscribe:async()=>{pushEvents.push({kind:'unsubscribe'});subscription=null;}};return subscription;}},unregister:async()=>pushEvents.push({kind:'unregister'})};
        window.isSecureContext=true;window.PushManager={};window.Notification={permission,requestPermission:async()=>{pushEvents.push({kind:'permission'});window.Notification.permission=permissionResult;return permissionResult;}};window.navigator={serviceWorker:{getRegistration:async()=>registration,register:async(url,options)=>{pushEvents.push({kind:'register',url,options});return registration;}}};
    }
    const context = vm.createContext({ window, document, location: { href }, innerWidth: 1024, innerHeight: 768,
        URL,Uint8Array,TextEncoder,atob,
        MutationObserver: class { observe() {} }, requestAnimationFrame: fn => fn(), crypto: require('node:crypto').webcrypto, Event: class { constructor(type) { this.type = type; } },
        setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }, clearTimeout: () => {},
        fetch: async (url, options) => {
            calls.push({ url, options });
            if (url.endsWith('/preferences/rule') && saveGate) await saveGate;
            const code = url.endsWith('/preferences/rule') ? saveStatus : url.endsWith('/actions') ? actionStatus : url.endsWith('/dismiss') ? dismissStatus : status;
            return { status: code, ok: code === 200, headers: { get: () => '"tag"' }, json: async () => code !== 200
                ? (blocked ? { twoFactorRequired: true } : {})
                : { ok: true, data: url.endsWith('/categories')?categories||{isAdministrator:false,catalogs:[]}:url.endsWith('/push')?{enabled:push==='enabled',defaultOn,publicKey:Buffer.alloc(65,1).toString('base64url'),devices:[]}:url.endsWith('/preferences') ? { preferences } : url.endsWith('unread-count') ? { count: 1 } : inbox ? inbox(Number(new URL(url).searchParams.get('skip') || 0)) : { hasMore, notifications: [
                    { id: 'one', event: { title: '<img src=x onerror=alert(1)>', body: 'plain text', link: noticeLink }, read }
                ] } }
            };
        }
    });
    vm.runInContext(source, context);
    return { calls, timers, all, window, listeners,pushEvents, rerun: () => vm.runInContext(source, context), get focused() { return focused; } };
}
const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(setImmediate); };
test('producer-projected grouping remains source and importance scoped and preserves individual rows and expanded state',async()=>{
    const key='a'.repeat(64),entry=(id,source='notifications',importance='normal',group={key,label:'<b>Example show</b>'})=>({id,read:false,event:{source,title:id,body:'Episode available',importance,group,occurredAt:new Date().toISOString()}});
    const h=harness({inbox:()=>({hasMore:false,notifications:[entry('episode1'),entry('episode2'),entry('different-plugin','discord-bridge'),entry('important','notifications','high'),entry('missing-key','notifications','normal',null)]})});
    await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();await settle();
    const groups=h.all().filter(e=>e.className==='notificationRelatedGroup');assert.equal(groups.length,1);assert.equal(groups[0].open,false);assert.match(groups[0].textContent,/<b>Example show<\/b> · 2 notifications/);
    assert.equal(groups[0].children.filter(e=>e.dataset.notificationId).length,2);assert.equal(h.all().filter(e=>e.dataset.notificationId).length,5);
    groups[0].open=true;groups[0].events.toggle();await h.timers.filter(t=>t.ms===30000).at(-1).fn();await settle();
    const next=h.all().find(e=>e.className==='notificationRelatedGroup');assert.equal(next.open,true);groups[0].open=false;groups[0].events.toggle();assert.equal(next.open,true);
    assert.equal(h.calls.filter(c=>c.options.method==='POST').length,0,'Grouping never marks, dismisses or replays notifications');
});
test('personal per-plugin grouping has inherit/group/separate choices without rewriting delivery preferences',async()=>{
    const h=harness({categories:{catalogs:[{source:'notifications',grouping:{name:'Show',defaultEnabled:true,enabled:null},types:[]}]}});
    await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();await settle();h.all().find(e=>e.className?.includes('notificationSettings')).onclick();await settle();
    const select=h.all().find(e=>e.attrs?.['aria-label']==='Notifications grouping');assert(select);assert.equal(select.value,'');
    for(const [value,enabled]of [['false',false],['true',true],['',null]]){select.value=value;await select.onchange();const call=h.calls.filter(c=>c.url.endsWith('/notifications/grouping')).at(-1);assert.deepEqual(JSON.parse(call.options.body),{source:'notifications',enabled});}
    assert.equal(h.calls.filter(c=>c.options.method==='PUT'&&c.url.endsWith('/preferences/rule')).length,0);
});
test('live refresh preserves an open options menu and focus only while its authorized row still exists',async()=>{
    let visible=true;const h=harness({inbox:()=>({hasMore:false,notifications:visible?[{id:'one',read:false,event:{title:'Current authorized row',body:'Example'}}]:[]})});
    await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();await settle();
    const options=h.all().find(e=>e.className==='notificationOptions');options.open=true;
    options.children[0].focus();await h.timers.filter(t=>t.ms===30000).at(-1).fn();await settle();
    const refreshed=h.all().find(e=>e.className==='notificationOptions');assert.notEqual(refreshed,options);assert.equal(refreshed.open,true);assert.equal(h.focused,refreshed.children[0]);
    visible=false;await h.timers.filter(t=>t.ms===30000).at(-1).fn();await settle();
    assert(!h.all().some(e=>e.className==='notificationOptions'));assert(!h.calls.some(c=>c.options.method==='POST'));
});
test('a delayed toggle from a detached menu cannot close its refreshed authorized replacement',async()=>{
    let reversed=false;const h=harness({inbox:()=>({hasMore:false,notifications:(reversed?['two','one']:['one','two']).map(id=>({id,read:false,event:{title:id,body:'Current authorized row'}}))})});
    await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();await settle();
    const row=id=>h.all().find(e=>e.dataset.notificationId===id);
    const original=row('one').querySelector('.notificationOptions');original.open=true;
    reversed=true;await h.timers.filter(t=>t.ms===30000).at(-1).fn();await settle();
    const replacement=row('one').querySelector('.notificationOptions');assert.equal(original.isConnected,false);assert.equal(replacement.open,true);
    original.events.toggle();assert.equal(replacement.open,true,'Queued detached toggle must not dismiss the replacement menu');
    const other=row('two').querySelector('.notificationOptions');other.open=true;other.events.toggle();
    assert.equal(replacement.open,false,'A current connected menu still closes other open menus');assert.equal(other.open,true);
});
test('importance selection remains checked while asynchronous autosave is pending', async () => {
    let release; const saveGate = new Promise(resolve => { release = resolve; });
    const h = harness({ saveGate }); await settle(); h.all().find(e => e.dataset.accountHubBell).onclick();
    h.all().find(e => e.attrs['aria-label'] === 'Notification settings').onclick(); await settle();
    const choice = h.all().find(e => e.attrs['aria-label'] === 'non-critical in-app notifications Important');
    choice.checked = true; const pending = choice.onchange();
    assert.equal(choice.checked, true); assert.equal(choice.disabled, true);
    release(); await pending; assert.equal(choice.checked, true); assert.equal(choice.disabled, false);
});
async function swipeFixture(options = {}) {
    const h = harness(options); await settle(); h.all().find(e => e.dataset.accountHubBell).onclick(); await settle();
    const row = h.all().find(e => e.dataset.notificationId === 'one');
    const send = (name, x, y, extra = {}) => row.events[name]?.({ pointerType: 'touch', pointerId: 1, isPrimary: true, clientX: x, clientY: y, target: row, ...extra });
    return { h, row, send, actions: h.all().find(e => e.className === 'notificationSwipeActions') };
}
test('touch swipes reveal right/read and left/hide actions only after confirmation', async () => {
    for (const [dx, action] of [[90, 'read'], [-90, 'dismiss']]) {
        const { h, send, actions, row } = await swipeFixture();
        send('pointerdown', 150, 100); send('pointermove', 150 + dx, 104); send('pointerup', 150 + dx, 104);
        assert.equal(actions.hidden, false);
        assert.equal(h.calls.filter(c => /\/one\/(read|dismiss)$/.test(c.url)).length, 0);
        const control = actions.children.find(e => e.dataset.swipeAction === action); assert.equal(control.hidden, false);
        assert.equal(actions.children.find(e => e.dataset.swipeAction && e !== control).hidden, true);
        let prevented = false, stopped = false;
        row.events.click({ target: row, preventDefault() { prevented = true; }, stopPropagation() { stopped = true; } });
        assert(prevented && stopped, 'Swipe must not follow the notification link');
        const pending = control.onclick(); await control.onclick(); await pending; await settle();
        assert.equal(h.calls.filter(c => c.url.endsWith('/one/' + action)).length, 1);
        assert(!h.calls.some(c => c.url.endsWith('/actions')), 'Swipe must never execute producer commands');
    }
});
test('swipes ignore vertical scrolling, short/diagonal drags, secondary buttons, interactive controls and cancellation', async () => {
    for (const mode of ['vertical', 'short', 'diagonal', 'right-button', 'interactive', 'cancel', 'secondary', 'other-pointer']) {
        const { send, actions } = await swipeFixture();
        const extra = mode === 'right-button' ? { pointerType: 'mouse', button: 2 } : mode === 'interactive' ? { target: { closest: () => ({}) } } : mode === 'secondary' ? { isPrimary: false } : {};
        send('pointerdown', 100, 100, extra);
        if (mode === 'vertical') send('pointermove', 105, 140);
        if (mode === 'cancel') send('pointercancel', 100, 100);
        send('pointerup', mode === 'short' ? 120 : 200, mode === 'diagonal' ? 180 : 102, mode === 'other-pointer' ? { pointerId: 2 } : extra);
        assert.equal(actions.hidden, true, mode);
    }
});
test('swipe cancellation and already-read rows never issue read requests', async () => {
    const { h, send, actions } = await swipeFixture({ read: true });
    send('pointerdown', 100, 100); send('pointerup', 200, 100);
    assert.equal(actions.hidden, false); assert(actions.textContent.includes('Already read'));
    assert(!actions.children.some(e => e.dataset.swipeAction === 'read'));
    actions.children.find(e => e.textContent === 'Cancel').onclick(); assert.equal(actions.hidden, true);
    assert(!h.calls.some(c => /\/one\/(read|dismiss)$/.test(c.url)));
});
const consentInbox = (changes = {}) => () => ({ notifications: [{ id: 'one', read: false, event: {
    source: 'deletion-queue', type: 'proposal.updated', title: 'Removal consent', body: 'Existing delay applies',
    commands: [{ id: 'allow', label: 'Allow', enabled: true, revision: 7, snapshot: 'native-consent-snapshot' }], ...changes
} }] });
test('touch, mouse and pen right-swipes submit projected native consent before dismissing', async () => {
    for (const pointerType of ['touch', 'mouse', 'pen']) {
        const { h, send } = await swipeFixture({ inbox: consentInbox() });
        send('pointerdown', 100, 100, { pointerType }); send('pointerup', 200, 102, { pointerType }); await settle();
        const writes = h.calls.filter(c => /\/one\/(actions|dismiss)$/.test(c.url)); assert.equal(writes.length, 2);
        assert(writes[0].url.endsWith('/actions')); assert(writes[1].url.endsWith('/dismiss'));
        const payload = JSON.parse(writes[0].options.body); assert.equal(payload.id, 'allow'); assert.equal(payload.revision, 7);
        assert.equal(payload.snapshot, 'native-consent-snapshot'); assert(payload.commandId);
    }
});
test('failed native consent never dismisses; failed dismissal never resubmits saved consent', async () => {
    for (const actionStatus of [409, 403, 500]) {
        const { h, send } = await swipeFixture({ inbox: consentInbox(), actionStatus });
        send('pointerdown', 100, 100); send('pointerup', 200, 100); await settle();
        assert.equal(h.calls.filter(c => c.url.endsWith('/actions')).length, 1);
        assert(!h.calls.some(c => c.url.endsWith('/dismiss')));
    }
    const { h, send } = await swipeFixture({ inbox: consentInbox(), dismissStatus: 500 });
    send('pointerdown', 100, 100); send('pointerup', 200, 100); await settle();
    assert(h.all().some(e => e.textContent.includes('Consent saved; notification could not be hidden.')));
    send('pointerdown', 100, 100); send('pointerup', 200, 100); await settle();
    assert.equal(h.calls.filter(c => c.url.endsWith('/actions')).length, 1);
});
test('only enabled unselected Deletion Queue proposal consent can run from a swipe', async () => {
    for (const changes of [ { source: 'other' }, { type: 'removal.playback_reset' }, { commands: [] },
        { commands: [{ id: 'allow', enabled: false }] }, { commands: [{ id: 'allow', enabled: true, selected: true }] } ]) {
        const { h, send } = await swipeFixture({ inbox: consentInbox(changes) });
        send('pointerdown', 100, 100); send('pointerup', 200, 100); await settle();
        assert(!h.calls.some(c => /\/one\/(actions|dismiss)$/.test(c.url)));
    }
    const { h, send } = await swipeFixture({ inbox: consentInbox() });
    send('pointerdown', 100, 100); send('pointerup', 165, 100); await settle();
    assert(!h.calls.some(c => c.url.endsWith('/actions')), 'Consent requires a deliberate 80px gesture');
    send('pointerdown', 200, 100); send('pointerup', 100, 100); await settle();
    assert(!h.calls.some(c => c.url.endsWith('/actions')), 'Left-swipe never consents');
});
test('desktop dragging also reveals ordinary notification actions', async () => {
    const { send, actions } = await swipeFixture();
    send('pointerdown', 100, 100, { pointerType: 'mouse', button: 0 });
    send('pointerup', 200, 100, { pointerType: 'mouse', button: 0 });
    assert.equal(actions.hidden, false); assert.equal(actions.children.find(e => e.dataset.swipeAction === 'read').hidden, false);
});
test('consent swipes do not duplicate in-flight commands or dismiss across an account change', async () => {
    const { h, send } = await swipeFixture({ inbox: consentInbox() });
    send('pointerdown', 100, 100); send('pointerup', 200, 100);
    send('pointerdown', 100, 100); send('pointerup', 200, 100);
    h.window.ApiClient.accessToken = () => 'different-session'; await settle();
    assert.equal(h.calls.filter(c => c.url.endsWith('/actions')).length, 1);
    assert(!h.calls.some(c => c.url.endsWith('/dismiss')));
});
test('swipe actions stay unavailable in History and Blocked views', async () => {
    for (const view of ['History', 'Blocked']) {
        const { h } = await swipeFixture();
        h.all().find(e => e.attrs.role === 'tab' && e.textContent === view).onclick(); await settle();
        assert(!h.all().some(e => e.className === 'notificationSwipeActions'));
    }
});
test('failed confirmed swipe actions retain the row and permit retry', async () => {
    let failing = false;
    const { h, send, actions } = await swipeFixture({ inbox: () => {
        if (failing) throw Error('Unavailable');
        return { notifications: [{ id: 'one', read: false, event: { title: 'Test', body: 'Body' } }] };
    } });
    send('pointerdown', 200, 100); send('pointerup', 100, 100);
    failing = true;
    const control = actions.children.find(e => e.dataset.swipeAction === 'dismiss'); await control.onclick();
    assert.equal(control.disabled, false); assert(h.all().some(e => e.textContent === 'Could not save. Try again.'));
    assert(h.all().some(e => e.dataset.notificationId === 'one'));
});
test('global device default prompts on bell interaction, never page load',async()=>{
    const h=harness({push:'enabled',defaultOn:true});await settle();assert.equal(h.pushEvents.length,0);
    h.all().find(e=>e.dataset.accountHubBell).onclick();await settle();assert.equal(h.pushEvents.filter(e=>e.kind==='permission').length,1);assert(h.pushEvents.some(e=>e.kind==='subscribe'));
});
test('default enrollment reuses already granted permission without prompting',async()=>{
    const h=harness({push:'enabled',defaultOn:true,permission:'granted'});await settle();assert(!h.pushEvents.some(e=>e.kind==='permission'));assert(h.pushEvents.some(e=>e.kind==='subscribe'));
});
test('device opt-out persists across reload even if browser permission remains granted',async()=>{
    const storage=new Map(),h=harness({push:'enabled',defaultOn:true,permission:'granted',storage});await settle();
    h.all().find(e=>e.dataset.accountHubBell).onclick();h.all().find(e=>e.attrs['aria-label']==='Notification settings').onclick();await settle();
    const toggle=h.all().find(e=>e.attrs['aria-label']==='Notifications on this device');toggle.checked=false;await toggle.onchange();await settle();
    const next=harness({push:'enabled',defaultOn:true,permission:'granted',storage});await settle();next.all().find(e=>e.dataset.accountHubBell).onclick();await settle();assert(!next.pushEvents.some(e=>e.kind==='subscribe'||e.kind==='permission'));
});
test('declining the default permission prompt does not repeatedly prompt or subscribe',async()=>{
    const h=harness({push:'enabled',defaultOn:true,permissionResult:'denied'});await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();await settle();
    assert.equal(h.pushEvents.filter(e=>e.kind==='permission').length,1);assert(!h.pushEvents.some(e=>e.kind==='subscribe'));
});
test('Blocked tab requests retained suppressed rows and does not offer executable commands',async()=>{
    const h=harness({inbox:()=>({notifications:[{id:'blocked',read:false,event:{title:'Retained',body:'Muted',commands:[{id:'allow',label:'Allow',enabled:true}]}}],hasMore:false})});await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();await settle();h.all().find(e=>e.attrs.role==='tab'&&e.textContent==='Blocked').onclick();await settle();
    assert(h.calls.some(c=>c.url.includes('blocked=true')));assert(!h.all().some(e=>e.dataset.action==='allow'));assert(h.all().some(e=>e.textContent.includes('old external deliveries are not replayed')));
});
test('ordinary users get Android-style per-app category and importance controls',async()=>{
    const h=harness({preferences:[{source:'deletion-queue',type:'proposal.updated',channel:'in-app',enabled:true,importance:'silent'}],categories:{isAdministrator:false,catalogs:[{source:'deletion-queue',types:[{type:'proposal.updated',name:'Removal requests'}]}]}});await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();h.all().find(e=>e.attrs['aria-label']==='Notification settings').onclick();await settle();
    const silent=h.all().find(e=>e.attrs['aria-label']==='deletion-queue Removal requests Silent');assert.equal(silent.checked,true);
    const important=h.all().find(e=>e.attrs['aria-label']==='deletion-queue Removal requests Important');assert(important);assert.equal(important.disabled,false);important.checked=true;await important.onchange();
    const write=h.calls.filter(c=>c.url.endsWith('/preferences/rule')).at(-1);const body=JSON.parse(write.options.body);assert.equal(body.source,'deletion-queue');assert.equal(body.type,'proposal.updated');assert.equal(body.importance,'high');assert.equal(important.checked,true);assert.equal(silent.checked,false);
    assert(h.all().some(e=>e.attrs.role==='radiogroup'&&e.attrs['aria-label']==='deletion-queue Removal requests importance'));
});
test('watchlisted and favorited categories are separate personal opt-ins and preserve the legacy favourite key', async()=>{
    const h=harness();await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();h.all().find(e=>e.attrs['aria-label']==='Notification settings').onclick();await settle();
    for(const [label,type]of [['Watchlisted media','watchlisted'],['Favorited media','favourite']]){
        const toggle=h.all().find(e=>e.attrs['aria-label']===label+' on in-app');assert.equal(toggle.getAttribute('aria-checked'),'false');
        const important=h.all().find(e=>e.attrs['aria-label']===label+' on in-app Important');assert.equal(important.disabled,true);
        await toggle.onclick();assert.equal(important.disabled,false);important.checked=true;await important.onchange();
        const body=JSON.parse(h.calls.filter(c=>c.url.endsWith('/preferences/rule')).at(-1).options.body);assert.equal(body.type,'category.library.'+type);assert.equal(body.enabled,true);assert.equal(body.importance,'high');
    }
});
test('failed importance saves restore the confirmed choice without enabling a muted category',async()=>{
    const h=harness({saveStatus:500,preferences:[{source:'notifications',type:'category.library.favourite',channel:'in-app',enabled:true,importance:'silent'}]});await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();h.all().find(e=>e.attrs['aria-label']==='Notification settings').onclick();await settle();
    const important=h.all().find(e=>e.attrs['aria-label']==='Favorited media on in-app Important');important.checked=true;await important.onchange();
    assert.equal(important.checked,false);assert.equal(h.all().find(e=>e.attrs['aria-label']==='Favorited media on in-app Silent').checked,true);
    assert(h.all().some(e=>e.className==='notificationSettingsStatus'&&e.textContent==='Could not save. Try again.'));
    assert.equal(h.all().find(e=>e.attrs['aria-label']==='Watchlisted media on in-app Important').disabled,true);
});
test('parent opt-out disables child switches and importance choices until the parent is enabled',async()=>{
    const h=harness({preferences:[{source:'*',type:'*',channel:'in-app',enabled:false},{source:'notifications',type:'category.library.favourite',channel:'in-app',enabled:true,importance:'high'}]});await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();h.all().find(e=>e.attrs['aria-label']==='Notification settings').onclick();await settle();
    const child=h.all().find(e=>e.attrs['aria-label']==='Favorited media on in-app'),important=h.all().find(e=>e.attrs['aria-label']==='Favorited media on in-app Important');assert.equal(child.disabled,true);assert.equal(important.disabled,true);
    await h.all().find(e=>e.attrs['aria-label']==='non-critical in-app notifications').onclick();assert.equal(child.disabled,false);assert.equal(important.disabled,false);assert.equal(important.checked,true);
    const inherit=h.all().find(e=>e.attrs['aria-label']==='Favorited media on in-app Use server default');inherit.checked=true;await inherit.onchange();
    const body=JSON.parse(h.calls.filter(c=>c.url.endsWith('/preferences/rule')).at(-1).options.body);assert.equal(body.importance,null);assert.equal(body.enabled,true);
});
test('retroactive and silent notices never create a popup',async()=>{
    for(const fields of [{retroactive:true},{importance:'silent'}]){const h=harness({inbox:()=>({notifications:[{id:'quiet',read:false,event:{title:'Quiet',body:'Retained',occurredAt:new Date().toISOString(),layout:{toastSeconds:5},...fields}}],hasMore:false})});await settle();assert(!h.all().some(e=>e.className==='dialog paperList notificationToast'));}
});
test('Web Push prompts only on explicit opt-in, uses a narrow worker scope and can unsubscribe',async()=>{
    const h=harness({push:'enabled'});await settle();assert.equal(h.pushEvents.length,0);
    h.all().find(e=>e.dataset.accountHubBell).onclick();h.all().find(e=>e.attrs['aria-label']==='Notification settings').onclick();await settle();
    assert.equal(h.pushEvents.length,0);const enable=h.all().find(e=>e.attrs['aria-label']==='Notifications on this device');assert.equal(enable.disabled,false);enable.checked=true;await enable.onchange();await settle();assert.equal(enable.checked,true);
    assert.equal(h.pushEvents.filter(e=>e.kind==='permission').length,1);const registration=h.pushEvents.find(e=>e.kind==='register');assert(registration.options.scope.endsWith('/notifications/push/'));assert(!registration.url.includes('session-secret'));
    const write=h.calls.find(c=>c.url.endsWith('/push')&&c.options.method==='POST');assert.equal(JSON.parse(write.options.body).endpoint,'https://fcm.googleapis.com/disposable');assert.match(write.options.headers.Authorization,/session-secret/);
    enable.checked=false;await enable.onchange();assert.equal(enable.disabled,false);assert(h.pushEvents.some(e=>e.kind==='unsubscribe'));assert(h.pushEvents.some(e=>e.kind==='unregister'));assert(h.calls.some(c=>c.options.method==='DELETE'&&c.url.includes('/push/')));
});
test('administrator-disabled Web Push does not prompt or subscribe',async()=>{
    const h=harness({push:'disabled'});await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();h.all().find(e=>e.attrs['aria-label']==='Notification settings').onclick();await settle();
    assert.equal(h.all().find(e=>e.attrs['aria-label']==='Notifications on this device').disabled,true);assert.equal(h.pushEvents.length,0);
});
test('denied notification permission explains browser settings without another prompt',async()=>{
    const h=harness({push:'enabled'});h.window.Notification.permission='denied';await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();h.all().find(e=>e.attrs['aria-label']==='Notification settings').onclick();await settle();
    assert.equal(h.all().find(e=>e.attrs['aria-label']==='Notifications on this device').checked,false);assert(h.all().some(e=>e.textContent.includes('browser’s site settings')));assert.equal(h.pushEvents.length,0);
});
test('insecure connections explain HTTPS rather than silently hiding device setup',async()=>{
    const h=harness({push:'enabled'});h.window.isSecureContext=false;await settle();h.all().find(e=>e.dataset.accountHubBell).onclick();h.all().find(e=>e.attrs['aria-label']==='Notification settings').onclick();await settle();
    assert(h.all().some(e=>e.textContent.includes('Open Jellyfin using HTTPS')));assert(!h.all().some(e=>e.textContent==='Enable on this device'));assert.equal(h.pushEvents.length,0);
});
test('push worker displays only fixed text and opens the native BaseUrl without processing payload links',async()=>{
    const listeners={},notices=[],opened=[];const context=vm.createContext({URL,self:{location:{pathname:'/jellyfin/Notifications/api/v1/notifications/push/sw.js',origin:'https://example.test'},addEventListener:(name,fn)=>listeners[name]=fn,registration:{showNotification:async(...args)=>notices.push(args)},clients:{openWindow:async url=>opened.push(url)}}});
    vm.runInContext(fs.readFileSync('src/AccountHub.Notifications/Web/push-worker.js','utf8'),context);let task;
    listeners.push({data:{json:()=>({title:'Private media',url:'https://evil.invalid'})},waitUntil:p=>task=p});await task;assert(!JSON.stringify(notices).includes('Private media'));
    listeners.notificationclick({notification:{close(){}},waitUntil:p=>task=p});await task;assert.deepEqual(opened,['https://example.test/jellyfin/web/index.html']);
});
test('only open notification options consume Escape so the next Escape can close the inbox', async () => {
    const h = harness(); await settle();
    const options = h.all().find(e => e.className === 'notificationOptions');
    let consumed = 0;
    const event = { key: 'Escape', preventDefault() { consumed++; }, stopPropagation() { consumed++; } };
    options.open = true; options.events.keydown(event);
    assert.equal(options.open, false); assert.equal(consumed, 2);
    options.events.keydown(event); assert.equal(consumed, 2);
});
test('Escape closes settings after a refresh loses control focus without consuming nested Escape', async () => {
    const h = harness(); await settle();
    const bell = h.all().find(e => e.dataset.notificationsBell);
    const panel = h.all().find(e => e.id === 'account-hub-inbox');
    bell.onclick();
    h.all().find(e => e.attrs['aria-label'] === 'Notification settings').onclick(); await settle();
    let prevented = false;
    h.listeners.keydown({ key: 'Escape', defaultPrevented: true, preventDefault() { prevented = true; } });
    assert.equal(panel.hidden, false); assert.equal(prevented, false);
    h.listeners.keydown({ key: 'Escape', defaultPrevented: false, preventDefault() { prevented = true; } });
    assert.equal(panel.hidden, true); assert.equal(prevented, true); assert.equal(h.focused, bell);
    bell.onclick(); assert.equal(panel.hidden, false);
});
test('removal commands run inline with projected revision and snapshot without closing the menu', async () => {
    const h = harness({ inbox: () => ({ hasMore: false, notifications: [{ id: 'request', event: { title: 'Movie', body: 'Awaiting consent', commands: [
        { id: 'allow', label: 'Allow', revision: 4, snapshot: 'fresh', enabled: true, selected: true },
        { id: 'keep', label: 'Keep', revision: 4, snapshot: 'fresh', enabled: true },
        { id: 'undo', label: 'Undo', revision: 4, snapshot: 'fresh', enabled: true }
    ] } }] }) }); await settle();
    h.all().find(e => e.dataset.accountHubBell).onclick(); await settle();
    const panel = h.all().find(e => e.id === 'account-hub-inbox');
    assert.equal(panel.style.maxHeight, '384px'); assert.equal(panel.style.right, '.75rem');
    const allow = h.all().find(e => e.dataset.action === 'allow'); assert.equal(allow.attrs['aria-pressed'], 'true');
    await allow.onclick(); await settle();
    const write = h.calls.find(call => call.url.endsWith('/request/actions'));
    const body = JSON.parse(write.options.body); assert.equal(body.id, 'allow'); assert.equal(body.revision, 4); assert.equal(body.snapshot, 'fresh'); assert.match(body.commandId, /^[\da-f-]{36}$/);
    assert.equal(panel.hidden, false);
});
test('only fresh opted-in notices create a bounded toast and old notices remain inbox-only', async () => {
    const h = harness({ inbox: () => ({ hasMore: false, notifications: [{ id: 'reset', event: { title: 'Movie', body: 'Removal delayed by 2 days', occurredAt: new Date().toISOString(), layout: { toastSeconds: 5 }, link: '#/home?deletionQueueProposal=1' } }] }) }); await settle();
    assert(h.all().some(e => e.className === 'dialog paperList notificationToast')); assert(h.timers.some(timer => timer.ms === 5000));
    const old = harness({ inbox: () => ({ hasMore: false, notifications: [{ id: 'old', event: { title: 'Old', body: 'Old reset', occurredAt: new Date(Date.now() - 3600000).toISOString(), layout: { toastSeconds: 5 } } }] }) }); await settle();
    assert(!old.all().some(e => e.className === 'dialog paperList notificationToast'));
});
test('routine UI feedback stays in the closed inbox and rejects another user', async () => {
    const h = harness(); await settle();
    const panel = h.all().find(e => e.id === 'account-hub-inbox');
    h.listeners['suite-notification-message']({ detail: { userId: 'bob', title: 'Other user', body: 'private' } });
    assert(!h.all().some(e => e.textContent === 'Other user'));
    h.listeners['suite-notification-message']({ detail: { userId: 'alice', title: 'Connection issue', body: 'Try again later.' } });
    assert.equal(panel.hidden, true); assert(h.all().some(e => e.tag === 'h3' && e.textContent === 'Connection issue'));
    assert(h.all().some(e => e.tag === 'details')); assert(h.all().some(e => e.dataset.accountHubBell && e.attrs['aria-label'] === '2 unread notifications'));
});
test('server read state removes redundant read actions and unread highlighting', async () => {
    const h = harness({ read: true }); await settle();
    assert(!h.all().some(e => e.tag === 'button' && e.textContent === 'Mark read'));
    assert(h.all().some(e => e.tag === 'section' && e.dataset.unread === 'false'));
});
test('groups by importance and age, shows exact and relative time, and keeps source links on the notification', async () => {
    const now = Date.now();
    const entry = (id, age, severity = 'info', adminOnly = false) => ({ id, read: false, event: { title: id, body: 'Summary', source: 'deletion-queue', type: 'proposal.updated', link: '#/details?id=media', occurredAt: new Date(now - age).toISOString(), severity, adminOnly } });
    const h = harness({ inbox: () => ({ hasMore: false, notifications: [entry('Routine', 2 * 3600000), entry('Priority', 3600000, 'high'), entry('Old', 10 * 86400000), entry('Administrator', 1000, 'info', true)] }) }); await settle();
    for (const name of ['Important', 'Other', 'Older']) assert(h.all().some(e => e.tag === 'h2' && e.textContent === name));
    assert(h.all().some(e => e.tag === 'time' && /hours? ago/.test(e.textContent) && e.title && e.attrs.datetime));
    assert(h.all().some(e => e.tag === 'a' && e.className === 'notificationMain' && e.href === '#/details?id=media'));
    assert(h.all().some(e => e.tag === 'summary' && e.attrs['aria-label'] === 'Notification options'));
    assert(h.all().some(e => e.tag === 'button' && e.textContent === 'Hide notification'));
});
test('source mute only changes the current source in-app preference', async () => {
    const global = { source: '*', type: '*', channel: 'discord', enabled: true };
    const h = harness({ preferences: [global], inbox: () => ({ hasMore: false, notifications: [{ id: 'request', event: { title: 'Request', body: 'Review', source: 'deletion-queue', type: 'proposal.updated', severity: 'info' } }] }) }); await settle();
    await h.all().find(e => e.tag === 'button' && e.textContent === 'Mute removal notifications').onclick();
    const saved = h.calls.find(c => c.options.method === 'PUT');
    assert(saved.url.endsWith('/preferences/rule'));
    assert.deepEqual(JSON.parse(saved.options.body), { source: 'deletion-queue', type: '*', channel: 'in-app', enabled: false });
});
test('settings show current choices and can restore a previously muted source', async () => {
    const h = harness({ preferences: [{ source: 'deletion-queue', type: '*', channel: 'in-app', enabled: false }] }); await settle();
    h.all().find(e => e.attrs['aria-label'] === 'Notification settings').onclick(); await settle();
    assert(h.all().some(e => e.tag === 'button' && e.attrs['aria-label'] === 'non-critical in-app notifications' && e.attrs['role']==='switch' && e.attrs['aria-checked'] === 'true'));
    const enable = h.all().find(e => e.attrs['aria-label'] === 'Unmute deletion-queue · * · in-app'); assert(enable); await enable.onclick(); await settle();
    const saved = h.calls.find(call => call.options.method === 'PUT');
    assert.deepEqual(JSON.parse(saved.options.body), { source: 'deletion-queue', type: '*', channel: 'in-app', enabled: true });
});
test('silent inbox refresh does not rebuild an active preference editor', async () => {
    const h = harness(); await settle(); h.all().find(e => e.attrs['aria-label'] === 'Notification settings').onclick(); await settle();
    const before = h.all().find(e => e.attrs['aria-label'] === 'non-critical in-app notifications');
    const reads = h.calls.filter(call => call.url.endsWith('/preferences')).length;
    h.listeners.focus(); await settle();
    assert(h.all().includes(before)); assert.equal(h.calls.filter(call => call.url.endsWith('/preferences')).length, reads);
});

test('singleton mounts one bell, uses header auth, and renders hostile titles as text', async () => {
    const h = harness(); await settle(); h.rerun(); await settle();
    assert.equal(h.all().filter(e => e.dataset.accountHubBell).length, 1);
    assert.equal(h.calls.length, 2);
    assert.equal(h.calls[0].options.credentials, 'omit');
    assert.match(h.calls[0].options.headers.Authorization, /session-secret/);
    assert(h.all().some(e => e.tag === 'h3' && e.textContent === '<img src=x onerror=alert(1)>'));
    const button = h.all().find(e => e.dataset.accountHubBell), panel = h.all().find(e => e.id === 'account-hub-inbox');
    button.onclick(); assert.equal(panel.hidden, false); assert.equal(h.focused, panel);
    panel.events.keydown({ key: 'Escape', preventDefault() {} }); assert.equal(panel.hidden, true); assert.equal(h.focused, button);
});
test('marked 403 pauses polling, clears inbox and links to 2FA without rewriting status', async () => {
    const h = harness({ status: 403, blocked: true }); await settle();
    assert.equal(h.calls.length, 1); assert.equal(h.timers.length, 0);
    assert(h.all().some(e => e.tag === 'a' && e.href.endsWith('/TwoFactorAuth/Login')));
    assert(!h.all().some(e => e.tag === 'h3'));
});
test('unmarked 401 does not become a 2FA challenge', async () => {
    const h = harness({ status: 401 }); await settle();
    assert(!h.all().some(e => e.textContent.includes('Complete 2FA'))); assert.equal(h.timers.length, 0);
});
test('server errors back off instead of tight polling', async () => {
    const h = harness({ status: 503 }); await settle(); assert.equal(h.timers[0].ms, 60000);
});
for (const page of ['#/login', '#/challenge', '/TwoFactorAuth/Setup', '/AccountHub/invite#secret', '/AccountHub/reset#secret', '/AccountHub/linktoken#secret']) {
    test('does not run on secret/auth page ' + page, async () => {
        const h = harness({ href: 'https://example.test/' + page }); await settle();
        assert.equal(h.calls.length, 0); assert.equal(h.all().filter(e => e.dataset.accountHubBell).length, 0);
    });
}
test('hidden pages do not poll', async () => { const h = harness({ hidden: true }); await settle(); assert.equal(h.calls.length, 0); });
test('an existing bell keeps polling when playback temporarily hides the native header', async () => {
    const h = harness(); await settle(); const before = h.calls.length;
    const header = h.all().find(element => element.tag === 'header'); header.children.at(-1).getClientRects = () => [];
    h.listeners.focus(); await settle(); assert.equal(h.calls.length, before + 2);
});
test('episode-specific preference saves do not replace unrelated or global choices', async () => {
    const global = { source: '*', type: '*', channel: 'in-app', enabled: true };
    const h = harness({ preferences: [global] }); await settle();
    h.all().find(e => e.attrs['aria-label'] === 'Notification settings').onclick();
    await settle();
    const control = h.all().find(e => e.tag === 'button' && e.attrs['aria-label'] === 'new episodes on in-app');
    assert(control); await control.onclick(); await settle();
    const write = h.calls.find(c => c.options.method === 'PUT'); assert(write);
    assert(write.url.endsWith('/preferences/rule'));
    assert.deepEqual(JSON.parse(write.options.body), { source: 'notifications', type: 'library.episode.added', channel: 'in-app', enabled: false });
});
test('opening refreshes silently, outside clicks dismiss and settings stay in the same panel', async () => {
    const h = harness(); await settle();
    const bell = h.all().find(e => e.dataset.accountHubBell);
    const panel = h.all().find(e => e.id === 'account-hub-inbox');
    bell.onclick(); await settle();
    assert(h.calls.length >= 4);
    assert(!h.all().some(e => e.tag === 'button' && e.textContent === 'Refresh'));
    h.all().find(e => e.attrs['aria-label'] === 'Notification settings').onclick();
    assert(h.all().some(e => e.tag === 'h2' && e.textContent === 'Notification settings'));
    h.listeners.pointerdown({ target: {} });
    assert.equal(panel.hidden, true);
});
test('permission-filtered short pages can still navigate to older notifications', async () => {
    const h = harness({ hasMore: true }); await settle();
    const older = h.all().find(e => e.tag === 'button' && e.textContent === 'Older notifications'); assert.equal(older.disabled, false);
    older.onclick(); await settle(); assert(h.calls.some(c => c.url.includes('skip=100')));
});
test('incremental pages append without duplicates and refresh removes revoked older rows', async () => {
    let revoked = false;
    const first = { id: 'first', event: { title: 'First', body: 'Summary', details: 'More information' } };
    const older = { id: 'older', event: { title: 'Admin-only older row', body: 'Private' } };
    const h = harness({ inbox: skip => ({ notifications: skip === 0 ? [first] : revoked ? [] : [first, older], hasMore: skip === 0 }) }); await settle();
    h.all().find(e => e.tag === 'button' && e.textContent === 'Older notifications').onclick(); await settle();
    assert.equal(h.all().filter(e => e.tag === 'h3' && e.textContent === 'First').length, 1);
    assert(h.all().some(e => e.tag === 'h3' && e.textContent === 'Admin-only older row'));
    revoked = true; h.listeners.focus(); await settle();
    assert(!h.all().some(e => e.tag === 'h3' && e.textContent === 'Admin-only older row'));
});
test('native item links resolve from the server BaseUrl, not a nested web/web path', async () => {
    const h = harness({ noticeLink: 'web/index.html#/details?id=disposable-item' }); await settle();
    assert.equal(h.all().find(e => e.tag === 'a' && e.className === 'notificationMain').href, 'https://example.test/jellyfin/web/index.html#/details?id=disposable-item');
});
