import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
const require = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const source = path => readFile(new URL('../' + path, import.meta.url), 'utf8');
const hub = 'plugins/account-hub/src/AccountHub/Web/';
const sync = 'plugins/private-syncplay/src/Jellyfin.Plugin.PrivateSyncPlay/Web/';
const dq = 'plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/';
const turn = async () => { await new Promise(r => setImmediate(r)); await new Promise(r => setImmediate(r)); };
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
function environment(t, html = '<main></main>') {
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/web/' });
    t.after(() => dom.window.close());
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new dom.window.Event('close')); };
    return dom.window;
}
const find = (w, label) => [...w.document.querySelectorAll('button')].find(b => b.textContent === label);
async function click(w, label) { const b = find(w, label); assert(b, label); b.click(); await turn(); }
async function seerr(t, request) {
    const w = environment(t), make = (tag, text) => { const n = w.document.createElement(tag); if (text) n.textContent = text; return n; };
    const button = (label, fn) => { const b = make('button', label); b.onclick = fn; return b; };
    w.eval((await source(hub + 'seerr-panel.mjs')).replace('export async function mountSeerrPanel', 'window.mountSeerrPanel = async function'));
    const signal = new w.AbortController();
    await w.mountSeerrPanel(w.document.querySelector('main'), { request, signal: signal.signal, make, button, table: (_, rows) => { const div = make('div'); for (const row of rows) for (const item of row) div.append(item instanceof w.Node ? item : make('span', String(item))); return div; }, field: (_, value) => { const input = make('input'); input.value = value; return { input, root: input, value: () => input.value }; } });
    return { w, signal };
}
const connection = { baseUrl: 'https://seerr.test', keyConfigured: true, warnings: [] };
const people = { items: [{ id: 'a', username: 'Alice' }, { id: 'b', username: 'Bob' }], total: 2 };
async function workspace(t, respond) {
    const w = environment(t, '<main class="accountHubPage"><p class="hubMessage"></p><nav class="hubTabs"></nav><section class="hubContent"></section></main>'), calls = [];
    w.fetch = async (url, options) => {
        const path = url.split('/api/v1/')[1], body = options.body && JSON.parse(options.body); calls.push({ path, body });
        return { ok: true, status: 200, json: async () => ({ ok: true, data: await respond(path, body) }) };
    };
    w.eval((await source(hub + 'workspace.js')).replace(/^import[^\r\n]*\r?\n/, 'const mountSecurityPanel = () => () => {};\n').replace('export function mountWorkspace', 'window.mountWorkspace = function'));
    w.mountWorkspace(w.document.querySelector('main'), { getUrl: path => 'https://example.test/' + path, accessToken: () => 'disposable-token' });
    await turn(); await turn(); return { w, calls };
}
test('a late account preview cannot authorize an edited permission draft', async t => {
    const pending = deferred();
    const { w } = await workspace(t, path => path === 'dependencies' ? { items: [] } : path.startsWith('users?') ? { items: [{ id: 'a', username: 'Alice', policy: { MaxActiveSessions: 3 } }], total: 1 } : path === 'policy/preview' ? pending.promise : { revision: 0 });
    await click(w, 'Edit access'); find(w, 'Preview').click(); await turn();
    const input = w.document.querySelector('dialog input[type=number]'); input.value = '5'; input.dispatchEvent(new w.Event('input', { bubbles: true }));
    pending.resolve({ users: [{ userId: 'a', before: { MaxActiveSessions: 3 }, after: { MaxActiveSessions: 3 } }], skippedAdmins: [] }); await turn();
    assert(find(w, 'Apply reviewed changes').disabled, 'An edited draft must need another preview');
});
test('account rollback uses the stored snapshot ID rather than its distinct event ID', async t => {
    const { w, calls } = await workspace(t, path => path === 'dependencies' ? { items: [] } : path.startsWith('users?') ? { items: [{ id: 'a', username: 'Alice', policy: { MaxActiveSessions: 3 } }], total: 1 } : path === 'policy/snapshots' ? { items: [{ snapshotId: 'stored-snapshot', eventId: 'source-event', metadata: { before: { MaxActiveSessions: 1 }, after: { MaxActiveSessions: 3 } } }] } : { revision: 0 });
    await click(w, 'Edit access'); await click(w, 'Review rollback history'); await click(w, 'Rollback managed fields');
    const confirm = [...w.document.querySelectorAll('dialog')].at(-1); confirm.querySelector('input').value = 'CONFIRM ACCOUNT HUB CHANGE'; confirm.querySelector('form').dispatchEvent(new w.Event('submit', { cancelable: true })); await turn();
    assert.equal(calls.find(call => call.path === 'commands/policy.rollback').body.payload.snapshotId, 'stored-snapshot');
});
test('individual permissions retain empty libraries and nullable ratings and show readable diffs', async t => {
    const policy = { EnabledFolders: null, MaxParentalRating: null, EnableMediaPlayback: true };
    const { w, calls } = await workspace(t, path => path === 'dependencies' ? { items: [] } : path.startsWith('users?') ? { items: [{ id: 'a', username: 'Alice', policy }], total: 1 } : path === 'policy/preview' ? { users: [{ userId: 'a', before: policy, after: { ...policy, EnabledFolders: [], EnableMediaPlayback: false } }] } : { revision: 0 });
    await click(w, 'Edit access'); const checkbox = w.document.querySelector('dialog input[type=checkbox]'); checkbox.checked = false; checkbox.dispatchEvent(new w.Event('change', { bubbles: true }));
    await click(w, 'Preview');
    assert.deepEqual(calls.find(call => call.path === 'policy/preview').body.managedFields, { EnabledFolders: [], MaxParentalRating: null, EnableMediaPlayback: false });
    assert.match(w.document.querySelector('dialog').textContent, /Enable Media PlaybackAllowDeny/);
    assert(!w.document.querySelector('dialog').textContent.includes('"before"'));
});
test('simplified settings preserve native enum values and the existing authorization boundary', async t => {
    const state = { revision: 4, publicBaseUrl: 'https://example.test/suite', auditRetentionDays: 30, notificationRetentionDays: 30, stepUpLevelMirror: 'AllConfigChanges', securityAlertMode: 'both', passwordLoginPolicy: 'auto' };
    const { w, calls } = await workspace(t, path => path === 'dependencies' ? { items: [] } : path.startsWith('users?') ? { items: [], total: 0 } : path === 'user-creation' ? { owner: 'hub' } : state);
    await click(w, 'Settings');
    const advanced = [...w.document.querySelectorAll('details')].find(node => node.querySelector('summary')?.textContent === 'Advanced settings'); assert(advanced); assert.equal(advanced.open, false);
    const labels = [...advanced.querySelectorAll('label')], select = name => w.document.getElementById(labels.find(label => label.textContent === name).htmlFor);
    assert.equal(select('Extra verification level').value, 'AllConfigChanges'); assert.equal(select('Local password login').selectedOptions[0].textContent, 'Follow native sign-in policy');
    select('Security alerts').value = 'hub'; await click(w, 'Save Security integration settings'); assert(!calls.some(call => call.path === 'security/settings'));
    const dialog = w.document.querySelector('dialog'); dialog.querySelector('input').value = 'CONFIRM ACCOUNT HUB CHANGE'; dialog.querySelector('input[type=password]').value = 'fresh-proof'; dialog.querySelector('form').dispatchEvent(new w.Event('submit', { cancelable: true })); await turn();
    const saved = calls.find(call => call.path === 'security/settings'); assert.equal(saved.body.confirmation, 'CHANGE SECURITY SETTINGS'); assert.equal(saved.body.stepUp.code, 'fresh-proof');
    assert.deepEqual(saved.body.settings, { stepUpLevelMirror: 'AllConfigChanges', alertMode: 'hub', passwordLoginPolicy: 'auto' });
    assert(find(w, 'Removal permissions').disabled); assert(find(w, 'Automatic request rules').disabled);
});
test('removal and automation permissions link to their owning plugin without granting native access', async t => {
    const { w, calls } = await workspace(t, path => path === 'dependencies' ? { items: [{ id: 'deletion-queue', available: true }, { id: 'automatic', available: true }] } : path.startsWith('users?') ? { items: [], total: 0 } : path === 'user-creation' ? { owner: 'hub' } : { revision: 0 });
    await click(w, 'Settings'); await click(w, 'Removal permissions'); assert.equal(w.location.hash, '#/configurationpage?name=DeletionQueueAdmin');
    await click(w, 'Automatic request rules'); assert.equal(w.location.hash, '#/configurationpage?name=AutomaticDeletionRequests');
    assert(!calls.some(call => call.path.startsWith('commands/')));
});
test('party errors keep technical detail behind hover and keyboard-accessible help',async t=>{
    const w=environment(t,'<script src="https://example.test/PrivateSyncPlay/assets/ui.js"></script><p role="status"></p>');
    Object.defineProperty(w.document,'currentScript',{value:w.document.querySelector('script'),configurable:true});w.eval(await source(sync+'ui.js'));
    const status=w.document.querySelector('p');w.PrivateSyncPlayUI.message(status,'Check your party.',true,'The original request is not automatically repeated.');
    assert.equal(status.firstChild.textContent,'Check your party.');const help=status.querySelector('details');assert.equal(help.open,false);assert.equal(help.querySelector('summary').getAttribute('aria-label'),'More information');
    help.dispatchEvent(new w.Event('mouseenter'));assert(help.open);help.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));assert.equal(help.open,false);
    w.PrivateSyncPlayUI.message(status,'Ready.');assert.equal(status.querySelector('details'),null);
});
test('human Discord linking pins confirmation to the reviewed native account and re-reviews account changes',async t=>{
    const w=environment(t,'<base href="https://example.test/suite/AccountHub/"><main class="accountHubPublic" data-page="link"><p class="hubPublicStatus"></p><div class="hubPublicContent"></div></main>'),calls=[];
    w.history.replaceState(null,'','/suite/AccountHub/discord/link#token='+'a'.repeat(43));
    w.localStorage.setItem('jellyfin_credentials',JSON.stringify({Servers:[{ManualAddress:'https://example.test/suite',AccessToken:'native-session'}]}));
    let account='11111111-1111-1111-1111-111111111111',changed=true;
    w.fetch=async(url,options)=>{const body=JSON.parse(options.body);calls.push({url:String(url),options,body});const confirming=String(url).endsWith('/confirm');return{ok:!confirming||!changed,status:confirming&&changed?409:200,json:async()=>confirming&&changed?{ok:false,error:{code:'account_changed'}}:{ok:true,data:confirming?{status:'linked'}:{userId:account,jellyfinUsername:'Reviewed user',discordId:'123456789012345678',discordUsername:'friendly.user'}}};};
    w.eval(await source(hub+'public.js'));assert.equal(w.location.hash,'');
    await click(w,'Review link');assert.match(w.document.querySelector('.hubPublicAccounts').textContent,/@friendly.userDiscord ID123456789012345678/);await click(w,'Link my Discord account');
    assert.equal(calls[1].body.userId,account);assert.equal(calls[1].options.headers.Authorization,'MediaBrowser Token="native-session"');
    assert.match(w.document.querySelector('[role=status],.hubPublicStatus').textContent,/Review the link again/);
    assert(find(w,'Review link'));assert.equal(calls.length,2,'An account change cannot auto-confirm another account');
    account='22222222-2222-2222-2222-222222222222';changed=false;
    await click(w,'Review link');await click(w,'Link my Discord account');
    assert.equal(calls[3].body.userId,account);assert.equal(w.document.querySelector('.hubPublicStatus').textContent,'Discord linked. You can close this page.');
});
test('human password recovery clears secrets and never resubmits an uncertain native password effect',async t=>{
    const w=environment(t,'<base href="https://example.test/suite/AccountHub/"><main class="accountHubPublic" data-page="reset"><p class="hubPublicStatus"></p><div class="hubPublicContent"></div></main>');
    w.history.replaceState(null,'','/suite/AccountHub/reset#token='+'a'.repeat(43));let writes=0;
    w.localStorage.setItem('jellyfin_credentials',JSON.stringify({Servers:[{ManualAddress:'https://example.test/suite',AccessToken:'native-session'}]}));
    w.fetch=async(url,options)=>{if(String(url).endsWith('/complete')){writes++;assert.equal(options.headers.Authorization,'MediaBrowser Token="native-session"');assert.equal(JSON.parse(options.body).stepUp,undefined);throw new TypeError('Network unavailable');}return{ok:true,json:async()=>String(url).includes('public-config')?{passwordLoginDisabled:false}:{ok:true,data:String(url).endsWith('/session')?{ready:true}:{valid:true,verificationAvailable:true}}};};
    w.eval(await source(hub+'public.js'));await turn();await turn();const form=w.document.querySelector('form');
    const passwords=form.querySelectorAll('input');for(const input of passwords)input.value='Disposable-chosen-password-9714!';
    form.dispatchEvent(new w.Event('submit',{cancelable:true}));await turn();
    assert.equal(writes,1);assert([...passwords].every(input=>input.value===''&&input.disabled));assert(find(w,'Reset my password').disabled);
    assert.match(w.document.querySelector('.hubPublicStatus').textContent,/Unable to confirm the change/);
    form.dispatchEvent(new w.Event('submit',{cancelable:true}));await turn();assert.equal(writes,1);
});
test('a reset link alone cannot submit a password change and missing verification blocks the form',async t=>{
    const w=environment(t,'<base href="https://example.test/suite/AccountHub/"><main class="accountHubPublic" data-page="reset"><p class="hubPublicStatus"></p><div class="hubPublicContent"></div></main>');
    w.history.replaceState(null,'','/suite/AccountHub/reset#token='+'a'.repeat(43));let writes=0;
    w.fetch=async(url)=>{if(String(url).endsWith('/complete'))writes++;return{ok:true,json:async()=>String(url).includes('public-config')?{passwordLoginDisabled:false}:{ok:true,data:{valid:true,verificationAvailable:true}}};};
    const code=await source(hub+'public.js');w.eval(code);await turn();await turn();
    assert.equal(w.document.querySelector('form'),null);assert.equal(writes,0);assert.match(w.document.querySelector('.hubPublicStatus').textContent,/Sign in/);assert(!find(w,'Check signed-in session').disabled);
    w.fetch=async()=>({ok:true,json:async()=>({ok:true,data:{valid:true,verificationAvailable:false}})});
    w.history.replaceState(null,'','/suite/AccountHub/reset#token='+'a'.repeat(43));w.document.querySelector('.hubPublicContent').replaceChildren();w.eval(code);await turn();
    assert.equal(w.document.querySelector('form'),null);assert.match(w.document.querySelector('.hubPublicStatus').textContent,/verification is unavailable/);
});
test('reset UI trusts the server session verdict, not a browser flag, and has no fresh factor prompt',async t=>{
    const w=environment(t,'<base href="https://example.test/suite/AccountHub/"><main class="accountHubPublic" data-page="reset"><p class="hubPublicStatus"></p><div class="hubPublicContent"></div></main>');
    w.history.replaceState(null,'','/suite/AccountHub/reset#token='+'a'.repeat(43));
    w.localStorage.setItem('jellyfin_credentials',JSON.stringify({Servers:[{ManualAddress:'https://example.test/suite',AccessToken:'native-session'}]}));w.localStorage.setItem('twoFactorVerified','true');
    let verified=false;const calls=[];
    w.fetch=async(url,options)=>{calls.push({url:String(url),options});const session=String(url).endsWith('/session');return{ok:!session||verified,status:session&&!verified?403:200,json:async()=>String(url).includes('public-config')?{passwordLoginDisabled:false}:session&&!verified?{ok:false,error:{code:'reset_session_required'}}:{ok:true,data:session?{ready:true}:{valid:true,verificationAvailable:true}}};};
    w.eval(await source(hub+'public.js'));await turn();await turn();assert.equal(w.document.querySelector('form'),null);
    verified=true;await click(w,'Check signed-in session');assert.equal(w.document.querySelectorAll('form input').length,2);assert.equal(w.document.querySelector('#verification-code'),null);
    for(const input of w.document.querySelectorAll('form input'))input.value='Disposable-chosen-password-9714!';w.document.querySelector('form').dispatchEvent(new w.Event('submit',{cancelable:true}));await turn();
    const sent=calls.find(call=>call.url.endsWith('/complete'));assert(sent);assert.equal(sent.options.headers.Authorization,'MediaBrowser Token="native-session"');assert.deepEqual(Object.keys(JSON.parse(sent.options.body)).sort(),['password','token']);
});
test('invite defaults preselect the template and save only explicit delegated creator permissions',async t=>{
    const w=environment(t),calls=[],group='11111111-1111-1111-1111-111111111111',user='22222222-2222-2222-2222-222222222222';
    w.eval(await source(hub+'invite-admin.js'));
    await w.AccountHubInvites.mount(w.document.querySelector('main'),{discordAvailable:true,read:async path=>path==='invite-defaults'?{defaultGroupId:group,discordCreationEnabled:false,allowedCreators:[user],discordExpiryMinutes:60,dailyLimit:5}:path.startsWith('users?')?{items:[{id:user,username:'Linked user'}],total:1}:path.startsWith('group-policy/')?{items:[{id:group,name:'Standard access'}],total:1}:{items:[]},write:async(path,body)=>{calls.push({path,body});}});
    const selects=w.document.querySelectorAll('select');assert.equal(selects[0].value,group);
    await click(w,'Save invitation defaults');assert.equal(calls[0].path,'invite-defaults');assert.equal(calls[0].body.settings.defaultGroupId,group);assert.equal(calls[0].body.settings.discordCreationEnabled,false);assert.deepEqual([...calls[0].body.settings.allowedCreators],[user]);
});
test('bulk account access previews only selected fields and invalidates the preview when edited', async t => {
    const w = environment(t, '<main class="accountHubPage"><p class="hubMessage"></p><nav class="hubTabs"></nav><section class="hubContent"></section></main>'), calls = [];
    const policy = { EnableMediaPlayback: true, EnableRemoteAccess: true, MaxActiveSessions: 3 };
    w.fetch = async (url, options) => {
        calls.push({ url, options });
        const data = url.endsWith('/dependencies') ? { items: [] } : url.includes('/users?') ? {
            items: [{ id: 'a', username: 'Alice', discordLinked: true, policy }, { id: 'b', username: 'Bob', policy }, { id: 'admin', username: 'Administrator', administrator: true, policy }], total: 3
        } : url.endsWith('/policy/preview') ? { users: ['a', 'b'].map(userId => ({ userId, before: policy, after: { ...policy, MaxActiveSessions: 5 } })), skippedAdmins: [] } : { revision: 0 };
        return { ok: true, status: 200, json: async () => ({ ok: true, data }) };
    };
    w.eval((await source(hub + 'workspace.js')).replace(/^import[^\r\n]*\r?\n/, 'const mountSecurityPanel = () => () => {};\n').replace('export function mountWorkspace', 'window.mountWorkspace = function'));
    w.mountWorkspace(w.document.querySelector('main'), { getUrl: path => 'https://example.test/' + path, accessToken: () => 'disposable-token' });
    await turn(); await turn();
    assert.equal([...w.document.querySelectorAll('button')].filter(button=>button.textContent==='Unlink Discord').length,1);
    await click(w,'Unlink Discord');
    assert(w.document.querySelector('dialog input[type=password]'),'Unlink must use the existing native confirmation/proof transport');
    assert(!calls.some(call=>call.url.endsWith('/commands/user.discord.unlink')));
    await click(w,'Cancel');
    const choices = w.document.querySelectorAll('.hubTable input[type=checkbox]');
    assert.equal(choices.length, 3); assert(choices[2].disabled);
    for (const choice of [...choices].slice(0, 2)) { choice.checked = true; choice.dispatchEvent(new w.Event('change')); }
    await click(w, 'Edit selected access');
    const dialog = w.document.querySelector('dialog');
    const include = [...dialog.querySelectorAll('label')].find(label => label.textContent === 'Change Max Active Sessions').querySelector('input');
    include.checked = true; include.dispatchEvent(new w.Event('change'));
    const sessions = dialog.querySelector('input[type=number]'); sessions.value = '5'; sessions.dispatchEvent(new w.Event('input', { bubbles: true }));
    await click(w, 'Preview');
    const body = JSON.parse(calls.find(call => call.url.endsWith('/policy/preview')).options.body);
    assert.deepEqual(body, { userIds: ['a', 'b'], managedFields: { MaxActiveSessions: 5 } });
    assert.equal(find(w, 'Apply reviewed changes').disabled, false);
    sessions.value = '6'; sessions.dispatchEvent(new w.Event('input', { bubbles: true }));
    assert(find(w, 'Apply reviewed changes').disabled);
    w.document.querySelector('main').dispatchEvent(new w.Event('viewhide'));
    assert.equal(w.document.querySelectorAll('dialog').length, 0);
    assert(!calls.some(call => call.url.endsWith('/commands/policy.apply')));
});
test('template paging preserves off-page defaults and explicit native-default choices', async t => {
    const w = environment(t), saved = '11111111-1111-1111-1111-111111111111', other = '22222222-2222-2222-2222-222222222222';
    w.eval(await source(hub + 'invite-admin.js'));
    await w.AccountHubInvites.mount(w.document.querySelector('main'), { read: async path => path === 'invite-defaults' ? { defaultGroupId: saved } : path.startsWith('users?') ? { items: [], total: 0 } : path.startsWith('group-policy/') ? { items: [{ id: other, name: 'Other template' }], total: 101 } : { items: [] } });
    const [inviteTemplate, defaultTemplate] = w.document.querySelectorAll('select');
    assert.equal(inviteTemplate.value, saved); assert.equal(defaultTemplate.value, saved);
    await click(w, 'Next templates');
    assert.equal(inviteTemplate.value, saved); assert.equal(defaultTemplate.value, saved);
    inviteTemplate.value = ''; defaultTemplate.value = '';
    await click(w, 'Previous templates');
    assert.equal(inviteTemplate.value, ''); assert.equal(defaultTemplate.value, '');
});
test('Unconfigured Seerr greys dependent actions but retains local jobs and explains remediation', async t => {
    const calls = [];
    const { w } = await seerr(t, async path => { calls.push(path); return path === 'seerr/connection' ? { warnings: [], keyConfigured: false } : path.startsWith('users?') ? people : { items: [{ id: 'job', state: 'failed' }], total: 1, attention: 1 }; });
    for (const label of ['Test connection', 'Manage Seerr for Alice', 'Retry after review']) { assert(find(w, label).disabled); assert.match(find(w, label).title, /API key/); }
    assert(!find(w, 'Cancel follow-up').disabled);
    assert(!calls.some(p => p.startsWith('seerr/users')));
});
test('Seerr detail response epochs reject an older selection and directory links capture the reviewed native ID', async t => {
    const first = deferred(), calls = [];
    const { w } = await seerr(t, async (path, method, payload) => {
        calls.push({ path, payload });
        if (path === 'seerr/connection') return connection;
        if (path.startsWith('users?')) return people;
        if (path === 'users/a/seerr') return first.promise;
        if (path === 'users/b/seerr') return { linked: false };
        if (path.startsWith('seerr/users?')) return { items: [{ id: 12, jellyfinUserId: 'b', username: 'Upstream Bob' }] };
        if (path === 'workspace') return { revision: 4 };
        return { items: [], total: 0, attention: 0 };
    });
    find(w, 'Manage Seerr for Alice').click(); await click(w, 'Manage Seerr for Bob');
    first.resolve({ linked: false }); await turn();
    assert.match(w.document.querySelector('main').textContent, /Seerr for Bob/);
    assert(![...w.document.querySelectorAll('h3')].some(n => n.textContent === 'Seerr for Alice'));
    const link = find(w, 'Verify and link #12');
    find(w, 'Manage Seerr for Alice').click(); await turn();
    // Even a retained reference must never retarget Bob's reviewed upstream evidence to Alice.
    link.click(); await turn();
    const command = calls.find(c => c.path === 'seerr/commands/link');
    if (command) assert.equal(command.payload.payload.userId, 'b');
});
test('Aborting a pending Seerr mount prevents late content from returning', async t => {
    const w = environment(t), pending = deferred();
    w.eval((await source(hub + 'seerr-panel.mjs')).replace('export async function mountSeerrPanel', 'window.mountSeerrPanel = async function'));
    const signal = new w.AbortController(), root = w.document.querySelector('main');
    const mounted = w.mountSeerrPanel(root, { signal: signal.signal, request: () => pending.promise, make: tag => w.document.createElement(tag) });
    signal.abort(); pending.resolve(connection); await mounted;
    assert.equal(root.children.length, 0);
});
test('Group subviews reject late lists and native selects expose arrow and focus contracts', async t => {
    const w = environment(t), pending = deferred(); let lists = 0;
    w.eval((await source(hub + 'group-policy.mjs')).replace('export function mountGroupPolicy', 'window.mountGroupPolicy = function'));
    w.mountGroupPolicy(w.document.querySelector('main'), { request: async path => path.startsWith('group-policy/groups?') ? (++lists === 1 ? { items: [], total: 0 } : pending.promise) : { items: [], total: 0 } }); await turn();
    await click(w, 'Members and assignments'); find(w, 'Groups').click(); await turn();
    await click(w, 'Search'); pending.resolve({ items: [{ name: 'Stale group', id: 'old' }], total: 1 }); await turn();
    assert(!w.document.body.textContent.includes('Stale group'));
    await click(w, 'Groups'); await click(w, 'Create group');
    for (const select of w.document.querySelectorAll('select')) { assert(select.parentNode.querySelector('.selectArrowContainer[aria-hidden=true]')); select.focus(); assert(select.labels[0].classList.contains('selectLabelFocused')); }
});
test('Invite pagination follows the no-total API through the end, browses templates, and exposes reservation reconciliation', async t => {
    const w = environment(t), calls = [];
    w.eval(await source(hub + 'invite-admin.js'));
    const panel = await w.AccountHubInvites.mount(w.document.querySelector('main'), { read: async path => {
        calls.push(path);
        if (path.startsWith('group-policy/groups?')) return { items: [], total: 101 };
        if (path === 'invites?skip=0&limit=100') return { items: Array.from({ length: 100 }, (_, id) => ({ id: String(id), status: 'active', expiresAt: '2030-01-01' })) };
        if (path === 'invites?skip=100&limit=100') return { items: [{ id: 'last', status: 'active', expiresAt: '2030-01-01' }] };
        return { reservations: [{ id: 'reservation', userId: 'native', username: 'User', state: 'ready_to_enable' }] };
    }, write: async (path, payload, risk) => { calls.push({ path, payload, risk }); return {}; } });
    assert(!find(w, 'Next invitations').disabled); await click(w, 'Next invitations');
    assert(find(w, 'Next invitations').disabled); assert(!find(w, 'Previous invitations').disabled);
    await click(w, 'Next templates'); assert(calls.includes('group-policy/groups?skip=100&limit=100'));
    await click(w, 'Provisioning history'); await click(w, 'Reconcile reservation (record verified completion; never create or enable)');
    assert(calls.some(c => c.path === 'invite-reservations/reservation/reconcile' && c.risk === 'Configuration'));
    const discord = [...w.document.querySelectorAll('input')].find(n => n.maxLength === 20); assert(discord.disabled); assert.match(discord.title, /connect its bot/);
    panel.dispose(); assert.equal(w.document.querySelector('main').children.length, 0);
});
test('Invite disposal erases retained one-time link and ignores late issuance', async t => {
    const w = environment(t), pending = deferred(), signal = new w.AbortController();
    w.eval(await source(hub + 'invite-admin.js'));
    let count = 0;
    await w.AccountHubInvites.mount(w.document.querySelector('main'), { signal: signal.signal, read: async path => path.startsWith('group-policy') ? { items: [], total: 0 } : { items: [{ id: 'one', status: 'active' }] }, write: async () => ++count === 1 ? { oneTimeLink: '/invite#first' } : pending.promise });
    await click(w, 'Rotate link (invalidates old link)');
    const secret = w.document.querySelector('input[readonly]'); assert.match(secret.value, /#first/);
    find(w, 'Rotate link (invalidates old link)').click(); await turn(); assert.equal(secret.value, '');
    signal.abort(); pending.resolve({ oneTimeLink: '/invite#late' }); await turn();
    assert.equal(w.document.querySelector('main').children.length, 0);
});
test('SyncPlay nested dialogs have unique names and navigation disposes every modal and retained secret', async t => {
    const w = environment(t, '<script src="https://example.test/PrivateSyncPlay/assets/ui.js"></script>');
    Object.defineProperty(w.document, 'currentScript', { value: w.document.querySelector('script'), configurable: true });
    w.eval(await source(sync + 'ui.js'));
    const ui = w.PrivateSyncPlayUI, first = ui.dialog('Parent'), second = ui.dialog('Confirm');
    first.dialog.showModal(); second.dialog.showModal(); const input = ui.field('Link', 'text', 'private-link', first.content);
    const ids = [...w.document.querySelectorAll('[id]')].map(n => n.id); assert.equal(new Set(ids).size, ids.length);
    for (const d of [first.dialog, second.dialog]) assert(w.document.getElementById(d.getAttribute('aria-labelledby')));
    const epoch = ui.navigationEpoch; w.dispatchEvent(new w.Event('hashchange'));
    assert.equal(w.document.querySelectorAll('dialog').length, 0); assert.equal(input.value, ''); assert(ui.navigationEpoch > epoch);
});
test('Deletion Queue admin rejects late navigation responses and clears a retained API key', async t => {
    const w = environment(t, await source(dq + 'admin.html')), pending = deferred();
    w.ApiClient = { getUrl: path => path, ajax: () => pending.promise };
    w.eval(await source(dq + 'admin.js'));
    const key = w.document.getElementById('dqKey'), page = w.document.getElementById('deletionQueueAdmin'); key.value = 'unsaved-key';
    page.dispatchEvent(new w.Event('viewhide')); assert.equal(key.value, '');
    pending.resolve({ mode: 'stale' }); await turn();
    assert.notEqual(w.document.getElementById('dqMode').textContent, 'stale'); assert.equal(page.getAttribute('aria-busy'), 'false');
});
