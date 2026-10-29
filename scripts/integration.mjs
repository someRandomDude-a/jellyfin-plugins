// Installs locally generated packages into a NEW disposable Jellyfin; never mounts user data.
import { spawnSync } from 'node:child_process';
import { mkdir, readFile, writeFile, rm, stat } from 'node:fs/promises';
import { resolve, join, dirname } from 'node:path';
import { randomUUID, randomBytes, createHash, createHmac } from 'node:crypto';
import { request as httpRequest } from 'node:http';
import assert from 'node:assert/strict';
import { ensureTargets, jellyfinTarget, downloadSecurity, assertRuntimeVersion, freshDockerArguments } from './latest-releases.mjs';
import { interactionsCompleted, accountInteractionsCompleted, accountResetCompleted, ownedInvitation, invitationInteractionsCompleted, invitationRedemptionCompleted, invitationHumanConfirmed } from './discord-acceptance.mjs';
import { startDiscordTunnel, probeDiscordTunnel, probeTrustedProxyAuth } from './discord-tunnel.mjs';
import { requireFixturePowerShell } from './fixture-tools.mjs';
import { moveFixtureState } from './fixture-state-files.mjs';

const option = name => process.argv.find(value => value.startsWith('--' + name + '='))?.split('=').slice(1).join('=');
const liveDiscord = process.argv.includes('--live-discord');
const interactiveDiscord = process.argv.includes('--interactive-discord');
const humanAccounts = process.argv.includes('--human-accounts');
const humanInvitations = process.argv.includes('--human-invitations');
const humanWindow = humanAccounts || humanInvitations;
const readyFile = option('ready-file');
const humanWaitMinutes = Number(option('human-wait-minutes')||15);
assert(Number.isInteger(humanWaitMinutes)&&humanWaitMinutes>=15&&humanWaitMinutes<=120,'Human acceptance window must be 15–120 minutes');
assert(!option('human-wait-minutes')||humanWindow,'Custom human acceptance window requires explicit human acceptance');
assert(!humanAccounts||interactiveDiscord,'--human-accounts requires explicit interactive Discord acceptance');
assert(!humanInvitations||interactiveDiscord&&!humanAccounts,'--human-invitations requires interactive Discord mode and cannot combine with reset acceptance');
assert(!readyFile||humanWindow,'--ready-file is only for human acceptance');
assert(!humanWindow||readyFile,'Human acceptance requires a unique readiness file for operator handoff');
if(readyFile)for(const file of [resolve(readyFile),resolve(readyFile)+'.complete']){
    try{await stat(file);}catch(error){if(error.code==='ENOENT')continue;throw error;}
    assert.fail('Human acceptance requires absent readiness and confirmation files; existing files are preserved');
}
const discordTunnel = process.argv.includes('--discord-tunnel');
assert(!discordTunnel || interactiveDiscord, '--discord-tunnel requires explicit interactive Discord acceptance');
let interrupted = false;
const interrupt = () => { interrupted = true; process.exitCode = 130; };
if (interactiveDiscord) { process.once('SIGINT', interrupt); process.once('SIGTERM', interrupt); }
if (interactiveDiscord) assert(liveDiscord, '--interactive-discord requires explicit live Discord mode');
if (process.argv.includes('--load-test-env') || process.argv.includes('--env-file')) { assert(liveDiscord, 'Test environment loading is only for explicitly enabled live tests'); process.loadEnvFile('.env'); }
const botToken = liveDiscord ? process.env.DISCORD_TEST_BOT_TOKEN : undefined;
const guildId = process.env.DISCORD_TEST_GUILD_ID, channelId = process.env.DISCORD_TEST_CHANNEL_ID;
if (liveDiscord) {
    assert(process.env.DISCORD_LIVE_TESTS === 'true', 'Live tests require DISCORD_LIVE_TESTS=true');
    assert(botToken && !/\s/.test(botToken), 'A valid disposable bot token is required');
    assert([guildId, channelId].every(id => /^\d{1,20}$/.test(id || '')), 'Disposable guild/channel IDs are required');
    assert(!process.argv.includes('--keep'), 'Live tests must not retain credential-bearing containers');
}
if (interactiveDiscord) {
    assert(/^\d{1,20}$/.test(process.env.DISCORD_TEST_RECIPIENT_ID || ''), 'Interactive DM acceptance requires a consenting disposable recipient');
    assert(!discordTunnel || !process.env.DISCORD_TEST_PUBLIC_BASE_URL, 'Choose a generated tunnel OR an explicit public base URL, not both');
    if (!discordTunnel) {
        const url = new URL(process.env.DISCORD_TEST_PUBLIC_BASE_URL || 'about:blank');
        assert(url.protocol === 'https:' && !url.username && !url.password && !url.search && !url.hash, 'Interactive checks require DISCORD_TEST_PUBLIC_BASE_URL or --discord-tunnel');
    }
}
const liveMessages = [];
let liveStage = 'startup';
async function discordApi(path, method = 'GET', body) {
    const response = await fetch('https://discord.com/api/v10/' + path, { method, headers: { Authorization: 'Bot ' + botToken, ...(body ? { 'Content-Type': 'application/json' } : {}) }, ...(body ? { body: JSON.stringify(body) } : {}), signal: AbortSignal.timeout(20000), redirect: 'error' });
    assert(response.ok, 'Discord API verification failed (HTTP ' + response.status + '; body withheld)');
    return response.status === 204 ? null : response.json();
}
const root = resolve(option('root') || join(process.env.TEMP || '/tmp', 'opencode', 'suite-integration-' + randomUUID()));
const packages = resolve(option('packages') || 'artifacts/runtime-review/packages');
const platform = option('platform') || 'linux/amd64';
assert(['linux/amd64', 'linux/arm64'].includes(platform), 'Only Linux amd64/arm64 disposable Docker fixtures are supported');
requireFixturePowerShell();
const targets = await ensureTargets(['jellyfin', ...(option('security') ? ['security'] : []), ...(process.argv.includes('--theme-check') ? ['enhanced'] : [])], { platform, pull: ['jellyfin'] });
const target = jellyfinTarget(targets.jellyfin);
const pluginNames = { hub: 'Account Hub', discord: 'Discord Bridge', deletion: 'Deletion Queue', syncplay: 'Private SyncPlay', notifications: 'Notifications', automatic: 'Automatic Deletion Requests' };
const selected = option('plugins') === 'none' ? [] : (option('plugins') || 'hub,discord,deletion,syncplay,notifications,automatic').split(',');
assert(selected.every(key => key in pluginNames), 'Unknown plugin selection');
const has = key => selected.includes(key);
const name = 'jellyfin-suite-test-' + randomUUID().slice(0, 8), keep = process.argv.includes('--keep');
const run = (exe, args) => { const result = spawnSync(exe, args, { encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 }); if (result.status !== 0) throw Error(`${exe} ${args[0]} failed: ${result.error?.code || result.stderr || result.stdout || 'no process result'}`); return result.stdout.trim(); };
const docker = (...args) => run('docker', freshDockerArguments(args));
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const normal = x => Array.isArray(x) ? x.map(normal) : x && typeof x === 'object' ? Object.fromEntries(Object.entries(x).map(([k, v]) => [k[0].toLowerCase() + k.slice(1), normal(v)])) : x;
const guid = value => value.replaceAll('-', '').replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5');
const libraryFixture = process.argv.includes('--library');
const extendedSecurity = process.argv.includes('--extended-security');
const v1Flows = process.argv.includes('--v1-flows');
const databaseSafety = process.argv.includes('--database-safety');
const storageRecovery = process.argv.includes('--storage-recovery');
const permissionsCheck = process.argv.includes('--permissions-check');
assert(!permissionsCheck || !liveDiscord, 'Permission checks require a disposable server without live Discord credentials');
const wiringCheck = process.argv.includes('--wiring-check');
assert(!wiringCheck || !liveDiscord, 'Wiring checks require a disposable server without live Discord credentials');
const resetSession = process.argv.includes('--reset-session');
assert(!process.argv.includes('--reset-proof'), 'The fresh-code reset scenario is superseded; use --reset-session');
assert(!resetSession || !liveDiscord && has('hub') && option('security') && process.argv.includes('--proof'), 'Reset session checks require disposable Hub and pinned native Security');
const themeCheck = process.argv.includes('--theme-check');
const notificationStudio = process.argv.includes('--notification-studio');
const diagnosticsUi = process.argv.includes('--diagnostics-ui');
assert(!diagnosticsUi||selected.length&&!liveDiscord&&!option('security'),'Diagnostics UI checks require a disposable installed plugin without live credentials or native factor enrollment');
const publicOnboardingUi=process.argv.includes('--public-onboarding-ui');
assert(!publicOnboardingUi||has('hub')&&!liveDiscord&&!option('security'),'Public onboarding UI checks require disposable Hub without live credentials or native factor enrollment');
const publicLinkUi=process.argv.includes('--public-link-ui');
assert(!publicLinkUi||has('hub')&&!liveDiscord,'Public link UI checks require disposable Hub without live credentials');
const publicLinkFactor=process.argv.includes('--public-link-factor');
assert(!publicLinkFactor||publicLinkUi&&option('security'),'Factor-enrolled link acceptance requires native Security and --public-link-ui');
assert(!notificationStudio || has('notifications') && !liveDiscord, 'Studio checks require disposable Notifications without live credentials');
const accountResponsive=process.argv.includes('--account-responsive');
assert(!accountResponsive||has('hub')&&has('discord')&&!liveDiscord,'Account responsive checks require disposable Hub and Bridge without live credentials');
assert(!themeCheck || !liveDiscord && ['hub', 'discord', 'deletion', 'syncplay'].every(has), 'Installed browser checks require all four disposable plugins without live credentials');
assert(!storageRecovery || has('hub') && !liveDiscord, 'Storage-loss checks require disposable Hub without live credentials');
assert(!databaseSafety || !liveDiscord, 'Abrupt-restart checks must not run with live credentials or external sends');
if (v1Flows) assert(has('hub'), 'V1 onboarding/group checks require Hub');
if (extendedSecurity) assert(has('hub') && option('security') && process.argv.includes('--proof'), 'Extended Security checks require Hub, pinned Security and --proof');
if (liveDiscord) assert(libraryFixture && has('discord'), 'Live checks require --library and Discord Bridge');
function totp(secret) {
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = '';
    for (const letter of secret.toUpperCase().replace(/[=\s]/g, '')) { const value = alphabet.indexOf(letter); assert(value >= 0); bits += value.toString(2).padStart(5, '0'); }
    const key = Buffer.from((bits.match(/.{8}/g) || []).map(byte => parseInt(byte, 2)));
    const counter = Buffer.alloc(8); counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
    const digest = createHmac('sha1', key).update(counter).digest(), offset = digest[19] & 15;
    return ((digest.readUInt32BE(offset) & 0x7fffffff) % 1000000).toString().padStart(6, '0');
}
let base, admin, started = false;
let interactiveAccount, interactiveReadiness, readyCreated=false;
let temporaryTunnel;
async function request(path, { method = 'GET', body, auth = admin, expected = 200, headers = {}, raw } = {}) {
    if (auth) headers.Authorization = `MediaBrowser Client="SuiteTest", Device="Disposable", DeviceId="${auth.device || 'suite-test'}", Version="1", Token="${auth.token || ''}"`;
    if (body !== undefined || raw !== undefined) headers['Content-Type'] = 'application/json';
    const response = await new Promise((resolve, reject) => {
        const deadline = setTimeout(() => { outgoing.destroy(); reject(Error('HTTP deadline exceeded')); }, 30000);
        const outgoing = httpRequest(base + '/' + path, { method, headers, agent: false }, incoming => {
            const chunks = []; let size = 0;
            incoming.on('data', chunk => { size += chunk.length; if (size > 4 * 1024 * 1024) outgoing.destroy(Error('Response exceeds test limit')); else chunks.push(chunk); });
            incoming.on('end', () => { clearTimeout(deadline); resolve({ status: incoming.statusCode, text: Buffer.concat(chunks).toString(), headers: new Headers(Object.entries(incoming.headers).flatMap(([key, value]) => [value].flat().map(item => [key, item]))) }); });
        });
        outgoing.on('error', error => { clearTimeout(deadline); reject(error); });
        const payload = raw ?? (body !== undefined ? JSON.stringify(body) : undefined); if (payload !== undefined) outgoing.write(payload); outgoing.end();
    });
    const text = response.text; let data; try { data = normal(JSON.parse(text)); } catch { data = text; }
    // Native authentication/setup errors can include one-time challenge material. Never print bodies.
    if(expected!==null&&![expected].flat().includes(response.status)&&method==='GET'&&path==='AccountHub/api/v1/security/devices'){
        // Read-only distinction between a missing Hub route and a forwarded
        // native denial. Retain no response prose, device rows or session tokens.
        const native=await request('TwoFactorAuth/AllTrustedDevices',{auth,expected:null});
        const id=response.headers.get('X-Suite-Error-Id');
        const nativeUserDirectory=await stat(join(root,'config','plugins','configurations','TwoFactorAuth','users')).then(value=>value.isDirectory()).catch(()=>false);
        await writeFile(join(root,'security-read-failure.json'),JSON.stringify({hubStatus:response.status,hubEnvelope:data?.apiVersion==='1',nativeStatus:native.status,nativeArray:Array.isArray(native.data),nativeUserDirectory,errorId:/^[a-f0-9-]{36}$/i.test(id||'')?id:null},null,2));
    }
    if (expected !== null) assert([expected].flat().includes(response.status), `${method} ${path.split('?')[0]} expected ${expected}, got ${response.status} (response body withheld)`);
    return { status: response.status, data, text, headers: response.headers };
}
async function until(check, label, seconds = 120) {
    const deadline = Date.now() + seconds * 1000;
    do {
        if (interrupted) throw Error('Interactive fixture interrupted; cleaning owned resources');
        if (await check()) return;
        await wait(1000);
    } while (Date.now() < deadline);
    assert.fail('Timed out: ' + label);
}
async function nativeFixtureUser(body) {
    if (!has('hub')) return await request('Users/New', { method: 'POST', body });
    const override = async enabled => await request('AccountHub/api/v1/user-creation/native-override', { method: 'POST', body: {
        commandId: randomUUID(), revision: (await request('AccountHub/api/v1/workspace')).data.data.revision,
        confirmation: 'CONFIRM ACCOUNT HUB CHANGE', enabled
    } });
    await request('Users/New', { method: 'POST', body, expected: 409 });
    await override(true);
    try {
        const paused = await request('AccountHub/public/v1/invites/redeem', { method: 'POST', auth: null,
            body: { token: 'a'.repeat(43), claimId: randomUUID(), username: 'must-not-create-during-override', password: 'Disposable-Only-9714!' }, expected: 503 });
        assert.equal(paused.data.error.code, 'hub_onboarding_paused');
        return await request('Users/New', { method: 'POST', body });
    }
    finally { await override(false); }
}
async function testPermissionSurfaces() {
    const endpoints = [
        ['hub', 'AccountHub/api/v1/workspace'], ['discord', 'DiscordBridge/admin/v1/settings'],
        ['deletion', 'DeletionQueue/admin/status'], ['syncplay', 'PrivateSyncPlay/admin/account-hub/v1/settings'],
        ['notifications', 'Notifications/api/v1/settings/notifications'], ['automatic', 'AutomaticDeletionRequests/admin']
    ].filter(([plugin]) => has(plugin));
    if(selected.length)endpoints.push(['diagnostics','SuiteDiagnostics/api/v1/errors']);
    const ordinary = (await nativeFixtureUser({ Name: 'permission-member' })).data;
    const temporaryAdmin = (await nativeFixtureUser({ Name: 'permission-administrator' })).data;
    const policy = async (user, patch) => request(`Users/${user.id}/Policy`, { method: 'POST', body: { ...(await request(`Users/${user.id}`)).data.policy, ...patch }, expected: 204 });
    const signIn = async (user, device) => (await request('Users/AuthenticateByName', { method: 'POST', auth: { device }, body: { Username: user.name, Pw: '' } })).data;
    const member = await signIn(ordinary, 'permission-member');
    await policy(temporaryAdmin, { isAdministrator: true });
    const elevated = await signIn(temporaryAdmin, 'permission-administrator');
    if (has('discord')) {
        const path = 'DiscordBridge/admin/v1/settings', snapshot = (await request(path)).data.data;
        assert(snapshot.permissionActions.some(action => action.id === 'party.guests'));
        assert.equal(snapshot.permissionActions.length, 9);
        assert(snapshot.permissionUsers.some(user => user.id.replaceAll('-', '') === ordinary.id.replaceAll('-', '')));
        const permissions = { defaults: { ...snapshot.settings.permissions.defaults, 'party.share.channel': false }, users: { ...snapshot.settings.permissions.users, [guid(ordinary.id)]: { reset: false, link: false } } };
        const body = { revision: snapshot.revision, settings: { ...snapshot.settings, permissions, inviteDefaultUses: 2, inviteMaxUses: 4, inviteDefaultExpiryMinutes: 90, inviteMaxExpiryMinutes: 120, watchPartyUses: 3 }, confirmation: 'CONFIRM ACCOUNT HUB CHANGE' };
        await request(path, { method: 'PUT', body });
        const saved = (await request(path)).data.data;
        assert.equal(saved.settings.permissions.users[guid(ordinary.id)].reset, false);
        const { permissions: omitted, inviteDefaultUses, inviteMaxUses, inviteDefaultExpiryMinutes, inviteMaxExpiryMinutes, watchPartyUses, watchPartyExpiryMinutes, watchPartyGuests, ...legacySettings } = saved.settings;
        await request(path, { method: 'PUT', body: { revision: saved.revision, settings: legacySettings } });
        const retained = (await request(path)).data.data;
        assert.equal(retained.settings.permissions.users[guid(ordinary.id)].link, false, 'Legacy settings omission must preserve permission denials');
        assert.equal(retained.settings.inviteMaxUses, 4); assert.equal(retained.settings.inviteDefaultUses, 2);
        assert.equal(retained.settings.watchPartyUses, 3, 'Legacy settings writes retain invitation limits');
        await request(path, { method: 'PUT', body, expected: 409 });
        for (const auth of [null, { token: member.accessToken }]) await request(path, { method: 'PUT', auth, body: { ...body, revision: retained.revision }, expected: [401, 403] });
        if (has('hub')) await request('AccountHub/api/v1/discord/link/confirm', { method: 'POST', auth: { token: member.accessToken }, body: { confirmation: 'LINK MY DISCORD ACCOUNT', userId: guid(ordinary.id), token: 'a'.repeat(43) }, expected: 403 });
        await request(path, { method: 'PUT', body: { revision: retained.revision, settings: snapshot.settings } });
        console.log('PASS installed Discord action policy/user editor data, durable defaults/overrides, legacy-client preservation, stale/anonymous/ordinary-write denial and native link-confirmation ceiling.');
    }
    await request('Auth/Keys?app=SuitePermissionChecks', { method: 'POST', expected: 204 });
    const key = (await request('Auth/Keys')).data.items.find(item => item.appName === 'SuitePermissionChecks'); assert(key?.accessToken);
    for (const [, path] of endpoints) {
        await request(path, { auth: { token: elevated.accessToken } });
        for (const auth of [null, { token: member.accessToken }, { token: key.accessToken }]) await request(path, { auth, expected: [401,403] });
    }
    await policy(temporaryAdmin, { isAdministrator: false });
    for (const [, path] of endpoints) await request(path, { auth: { token: elevated.accessToken }, expected: [401,403] });
    await policy(ordinary, { isDisabled: true });
    for (const [, path] of endpoints) await request(path, { auth: { token: member.accessToken }, expected: [401,403] });
    const personal = [['hub', 'AccountHub/api/v1/self'], ['deletion', 'DeletionQueue/proposals'], ['syncplay', 'PrivateSyncPlay/Me'], ['notifications', 'Notifications/api/v1/notifications']].filter(([plugin]) => has(plugin));
    for (const [, path] of personal) {
        await request(path, { auth: { token: member.accessToken }, expected: [401,403] });
        await request(path, { auth: { token: key.accessToken }, expected: [401,403] });
    }
    await request(`Users/${ordinary.id}`, { method: 'DELETE', expected: 204 });
    await request(`Users/${temporaryAdmin.id}`, { method: 'DELETE', expected: 204 });
    console.log('PASS installed authorization surfaces for ' + selected.length + ' plugins and shared diagnostics: anonymous/non-admin/API-key denial, stale-session demotion/disable and personal endpoint restrictions.');
}
async function testSharedDiagnostics(){
    if(!selected.length)return;
    const endpoint='SuiteDiagnostics/api/v1/',sources={hub:'account-hub',discord:'discord-bridge',deletion:'deletion-queue',syncplay:'private-syncplay',notifications:'notifications',automatic:'automatic-deletion-requests'};
    const before=(await request(endpoint+'errors')).data.data;
    assert.deepEqual(before.plugins.map(plugin=>plugin.source).sort(),selected.map(plugin=>sources[plugin]).sort(),'One combination-independent diagnostics owner must discover every installed plugin');
    const protectedPath={hub:'AccountHub/api/v1/workspace',discord:'DiscordBridge/admin/v1/settings',deletion:'DeletionQueue/admin/status',syncplay:'PrivateSyncPlay/admin/account-hub/v1/settings',notifications:'Notifications/api/v1/settings/notifications',automatic:'AutomaticDeletionRequests/admin'}[selected[0]];
    const failure=await request(protectedPath,{auth:null,expected:[401,403]});const id=failure.headers.get('X-Suite-Error-Id');assert.match(id||'',/^[a-f0-9-]{36}$/i);
    const found=(await request(endpoint+'errors?id='+id)).data.data.items;assert.equal(found.length,1);assert.equal(found[0].source,sources[selected[0]]);assert.equal(found[0].id,id);assert.equal(found[0].isFailure,true);
    assert(!JSON.stringify(found).includes(admin.token),'Diagnostics must never capture native authentication material');
    await request(endpoint+'errors?id='+id,{auth:null,expected:[401,403]});
    await request(endpoint+'settings',{method:'POST',headers:{Origin:'https://attacker.invalid'},body:{revision:before.settings.revision,minimumLevel:'debug'},expected:[401,403]});
    await request(endpoint+'clear',{method:'POST',body:{confirmation:'incorrect',ids:[id]},expected:400});assert.equal((await request(endpoint+'errors?id='+id)).data.data.items.length,1);
    await request(endpoint+'settings',{method:'POST',body:{revision:before.settings.revision,minimumLevel:'critical'}});
    await request(endpoint+'settings',{method:'POST',body:{revision:before.settings.revision,minimumLevel:'debug'},expected:409});
    const second=await request(protectedPath,{auth:null,expected:[401,403]});const secondId=second.headers.get('X-Suite-Error-Id');assert.match(secondId||'',/^[a-f0-9-]{36}$/i);assert.notEqual(id,secondId);assert.equal((await request(endpoint+'errors?id='+secondId)).data.data.items.length,1,'User-facing failure IDs remain searchable regardless of background level');
    await request(endpoint+'clear',{method:'POST',body:{confirmation:'CLEAR DIAGNOSTICS',ids:[id]}});assert.equal((await request(endpoint+'errors?id='+id)).data.data.items.length,0);assert.equal((await request(endpoint+'errors?id='+secondId)).data.data.items.length,1);
    const settings=(await request(endpoint+'errors')).data.data.settings;await request(endpoint+'settings',{method:'POST',body:{revision:settings.revision,minimumLevel:before.settings.minimumLevel}});
    console.log('PASS shared diagnostics for this exact plugin combination: one searchable store, opaque failure IDs, owner/source discovery, anonymous/origin denial, revisioned levels, failure retention and explicit selected-only clear.');
}
async function testSuiteWiring() {
    await testSharedDiagnostics();
    if (has('discord')) {
        const bridge = (await request('DiscordBridge/admin/v1/settings')).data.data;
        assert.equal(bridge.features.find(feature => feature.id === 'notifications').available, has('notifications'), 'Bridge must discover the standalone Notifications owner, not the legacy Hub adapter');
    }
    if (!has('notifications')) return;
    const endpoint = 'Notifications/api/v1/';
    const credential = (await request(endpoint + 'settings/notifications/producer-tokens', { method: 'POST', expected: 201, body: { source: 'private-syncplay', scopes: ['audit:write'] } })).data.data;
    const eventId = randomUUID(), eventType = 'syncplay.wiring.checked';
    const publication = { apiVersion: '1', operation: 'audit.publish', requestId: randomUUID(), payload: {
        schemaVersion: 1, eventId, source: 'private-syncplay', eventType, outcome: 'success', occurredAt: new Date().toISOString(), metadata: { reason: 'disposable_wiring_check' }
    } };
    const send = body => request(endpoint + 'producer/audit', { method: 'POST', auth: null, headers: { Authorization: 'Bearer ' + credential.token }, body });
    const accepted = await send(publication), repeated = await send(publication);
    assert.equal(accepted.data.data.status, 'accepted'); assert.equal(repeated.data.data.status, 'duplicate');
    const denied = await request(endpoint + 'producer/audit', { method: 'POST', auth: null, expected: [400,401,403], body: publication });
    assert.notEqual(denied.data.ok, true);
    if (has('hub')) {
        assert(accepted.data.data.auditId, 'Notifications must return the owning Hub audit acknowledgment');
        assert.equal(repeated.data.data.auditId, accepted.data.data.auditId);
        const history = (await request('AccountHub/api/v1/audit/query', { method: 'POST', body: { eventType, limit: 50 } })).data.data.items;
        assert.equal(history.filter(row => row.event.eventId === eventId && row.event.source === 'private-syncplay').length, 1, 'The forwarded audit must exist exactly once in the Hub ledger');
    }
    await request(endpoint + 'producer/notifications', { method: 'POST', auth: null, headers: { Authorization: 'Bearer ' + credential.token }, body: { ...publication, operation: 'notifications.publish' }, expected: [400,401,403] });
    console.log('PASS installed Notifications audit routing to ' + (has('hub') ? 'the single Hub ledger' : 'its independent owner') + ', stable duplicate acknowledgment, anonymous/scope denial and accurate Bridge dependency discovery.');
}
async function testResetSession() {
    const user = (await nativeFixtureUser({ Name: 'reset-session-user' })).data;
    const beforePassword = 'Disposable-Before-9427!', afterPassword = 'Disposable-After-9427!';
    await request(`Users/${user.id}/Password`, { method: 'POST', body: { CurrentPw: '', NewPw: beforePassword, ResetPassword: false }, expected: 204 });
    const login = (await request('Users/AuthenticateByName', { method: 'POST', auth: { device: 'reset-session-before' }, body: { Username: user.name, Pw: beforePassword } })).data;
    const auth = { device: 'reset-session-before', token: login.accessToken };
    const seed = () => {
        const token = randomBytes(32).toString('base64url'), hash = createHash('sha256').update(token).digest('hex');
        // Synthetic issuance into this NEW fixture's own lifecycle database only.
        // Native factors are enrolled/verified exclusively through public Security APIs.
        const script = "import sqlite3,sys,time; c=sqlite3.connect('/fixture/account-hub.db',timeout=10); n=int(time.time()*1000); c.execute('INSERT INTO reset_tokens(TokenHash,JellyfinUserId,CreatedByUserId,CreatedAt,ExpiresAt) VALUES(?,?,?,?,?)',(sys.argv[1],sys.argv[2],'reset-session-fixture',n,n+300000)); c.commit(); c.close()";
        docker('run','--rm','--network=none','--read-only','--mount',`type=bind,source=${join(root,'config','plugins','configurations','AccountHub')},target=/fixture`,'python:alpine','python','-c',script,hash,user.id.replaceAll('-',''));
        return token;
    };
    const complete = (caller, token, expected = 403, flags = {}) => request('AccountHub/public/v1/resets/complete', { method: 'POST', auth: caller, body: { token, password: afterPassword, ...flags }, expected });
    const ready = (caller, token, expected = 403, flags = {}) => request('AccountHub/public/v1/resets/session', { method: 'POST', auth: caller, body: { token, ...flags }, expected });
    const valid = async token => assert.equal((await request('AccountHub/public/v1/resets/lookup', { method: 'POST', auth: null, body: { token } })).data.data.valid, true, 'Denied session checks must not consume recovery');
    const anonymous = seed(); await ready(null, anonymous, [401,403]); await complete(null, anonymous, [401,403]);
    const wrongAccount = seed();
    assert.equal((await ready(admin, wrongAccount)).data.error.code, 'reset_account_mismatch');
    assert.equal((await complete(admin, wrongAccount)).data.error.code, 'reset_account_mismatch');
    await request('Auth/Keys?app=ResetSessionChecks', { method: 'POST', expected: 204 });
    const key = (await request('Auth/Keys')).data.items.find(item => item.appName === 'ResetSessionChecks'); assert(key?.accessToken);
    const keyed = seed(); await ready({ token: key.accessToken }, keyed, [401,403]); await complete({ token: key.accessToken }, keyed, [401,403]);
    const unmarked = seed(), flags = { verified: true, twoFactorVerified: true };
    assert.equal((await ready(auth, unmarked, 403, flags)).data.error.code, 'reset_session_required');
    assert.equal((await complete(auth, unmarked, 403, flags)).data.error.code, 'reset_session_required');
    await valid(unmarked);
    const enrollment = (await request('TwoFactorAuth/Setup/Totp', { method: 'POST', auth, body: {} })).data;
    try {
        await request('TwoFactorAuth/Setup/Totp/Confirm', { method: 'POST', auth, body: { Code: totp(enrollment.secretKey) } });
        const signIn = async (device, password) => {
            const pending = (await request('Users/AuthenticateByName', { method: 'POST', auth: { device }, body: { Username: user.name, Pw: password }, expected: 401 })).data;
            assert.equal(pending.twoFactorRequired, true); assert.equal(pending.accessToken, undefined);
            try {
                const denied = seed(); await ready({ device, token: pending.challengeToken }, denied, [401,403]); await complete({ device, token: pending.challengeToken }, denied, [401,403]);
                await wait(30000 - Date.now() % 30000 + 1000);
                const signedIn = (await request('TwoFactorAuth/Verify', { method: 'POST', auth: { device }, body: { ChallengeToken: pending.challengeToken, Method: 'totp', Code: totp(enrollment.secretKey), TrustDevice: false } })).data;
                assert(signedIn.accessToken, 'Completed native 2FA must provide a native session');
                return { device, token: signedIn.accessToken };
            } finally { pending.challengeToken = pending.challengePageUrl = pending.enrollmentPageUrl = null; }
        };
        const token = seed(), verified = await signIn('reset-session-verified', beforePassword);
        assert.equal((await ready(verified, token, 200)).data.data.ready, true, 'Readiness must read the running native plugin verdict');
        await request('Sessions/Logout', { method: 'POST', auth: verified, expected: 204 });
        await ready(verified, token, [401,403]); await complete(verified, token, [401,403]); await valid(token);
        const fresh = await signIn('reset-session-final', beforePassword);
        assert.equal((await ready(fresh, token, 200)).data.data.ready, true);
        await complete(fresh, token, 200); // Deliberately no code or stepUp object.
        await request('Users/Me', { auth: fresh, expected: [401,403] });
        await request('Users/Me', { auth, expected: [401,403] });
        const old = await request('Users/AuthenticateByName', { method: 'POST', auth: { device: 'reset-session-after' }, body: { Username: user.name, Pw: beforePassword }, expected: 401 });
        assert.notEqual(old.data.twoFactorRequired, true, 'The previous password must not get a native factor challenge');
        const after = await signIn('reset-session-after', afterPassword);
        assert.equal((await request('TwoFactorAuth/MyStatus', { auth: after })).data.totpEnabled, true, 'Reset preserves native factors');
        await ready(after, token, 400); await complete(after, token, 400);
        console.log('PASS installed reset reads the exact same-account native verified-session verdict with no fresh code; anonymous/API-key/wrong-account/unmarked/pending/revoked sessions denied, client flags ignored, readiness revocation rechecked, denied checks preserve tokens, success is one-use and revokes sessions while preserving native factors.');
    } finally { enrollment.secretKey = enrollment.manualEntryKey = enrollment.qrCodeBase64 = null; }
}
async function testLibraryAdditions() {
    assert(has('notifications'), '--library requires the Notifications owner');
    const notificationApi='Notifications/api/v1/';
    const media = join(root, 'media');
    for (const path of ['movies/Baseline', 'movies/Future', 'tv/Disposable Series/Season 01']) await mkdir(join(media, path), { recursive: true });
    const video = path => docker('exec', name, '/usr/lib/jellyfin-ffmpeg/ffmpeg', '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'color=c=black:s=64x64:r=1', '-t', '1', '-c:v', 'mpeg4', '-y', '/media/' + path);
    video('movies/Baseline/Baseline (2026).mp4');
    video('tv/Disposable Series/Season 01/Disposable Series S01E00.mp4');
    const options = { EnableRealtimeMonitor: false, AutomaticRefreshIntervalDays: 0, TypeOptions: ['Movie', 'Series', 'Season', 'Episode'].map(Type => ({ Type, MetadataFetchers: [], ImageFetchers: [], MetadataFetcherOrder: [], ImageFetcherOrder: [] })) };
    await request('Library/VirtualFolders?name=DisposableMovies&collectionType=movies&paths=%2Fmedia%2Fmovies', { method: 'POST', body: { LibraryOptions: options }, expected: 204 });
    await request('Library/VirtualFolders?name=DisposableTV&collectionType=tvshows&paths=%2Fmedia%2Ftv', { method: 'POST', body: { LibraryOptions: options }, expected: 204 });
    await request('Library/Refresh', { method: 'POST', expected: 204 });
    const mediaItems = async () => (await request('Items?Recursive=true&IncludeItemTypes=Movie,Episode')).data.items;
    await until(async () => (await mediaItems()).length === 2, 'initial synthetic-media scan');
    assert.equal((await request(notificationApi+'settings/notifications/library')).data.data.health.retained, 0, 'Disabled notifications must not retain media/audience details');
    const nativeFolders = (await request('Library/VirtualFolders')).data;
    // Other enabled acceptance flows may already own a separate library fixture.
    const libraries = nativeFolders.filter(folder => ['DisposableMovies', 'DisposableTV'].includes(folder.name)).map(folder => guid(folder.itemId)); assert.equal(libraries.length, 2);
    const password = 'Disposable-Library-Only-9714!';
    const createUser = async (username, blocked = false) => {
        const user = (await nativeFixtureUser({ Name: username })).data;
        await request(`Users/${user.id}/Password`, { method: 'POST', body: { CurrentPw: '', NewPw: password, ResetPassword: false }, expected: 204 });
        if (blocked) await request(`Users/${user.id}/Policy`, { method: 'POST', body: { ...user.policy, EnableAllFolders: false, EnabledFolders: [] }, expected: 204 });
        const login = (await request('Users/AuthenticateByName', { method: 'POST', auth: {}, body: { Username: username, Pw: password } })).data;
        return { id: user.id, token: login.accessToken, policy: user.policy };
    };
    const ordinary = await createUser('library-ordinary'), blocked = await createUser('library-blocked', true);
    let watchlisted, favourited;
    {
        // Seed native interest flags while capture is disabled, not in a race
        // with the 30-second addition publication window. No history is rewritten.
        const series = (await request('Items?Recursive=true&IncludeItemTypes=Series')).data.items.find(item => item.name === 'Disposable Series');
        assert(series, 'Synthetic baseline series must be available');
        watchlisted = await createUser('library-watchlisted'); favourited = await createUser('library-favorited');
        const liked = await request(`UserItems/${series.id}/Rating?likes=true`, { method: 'POST', auth: watchlisted });
        assert.equal(liked.data.likes, true); assert.equal(liked.data.isFavorite, false, 'Watchlist must not modify Favorites');
        const favorite = await request(`UserFavoriteItems/${series.id}`, { method: 'POST', auth: favourited });
        assert.equal(favorite.data.isFavorite, true); assert.notEqual(favorite.data.likes, true, 'Favorites must not modify the watchlist');
        for (const [auth, category, importance] of [[watchlisted, 'watchlisted', 'high'], [favourited, 'favourite', 'silent']])
            await request(notificationApi+'notifications/preferences', { method:'PUT', auth, body:[
                { source:'notifications', type:'category.library.all', channel:'in-app', enabled:false },
                { source:'notifications', type:'category.library.'+category, channel:'in-app', enabled:true, importance }
            ] });
        const ownerSettings = (await request(notificationApi+'settings/notifications/library')).data.data;
        assert.deepEqual(ownerSettings.libraries.map(folder=>folder.id).sort(),nativeFolders.map(folder=>guid(folder.itemId)).sort(),'Library selector IDs must use the same canonical format as saved scope');
        const current=ownerSettings.settings;
        await request(notificationApi+'settings/notifications/library', { method: 'PUT', body: { ...current, enabled: true, libraryIds:libraries } });
        assert.deepEqual((await request(notificationApi+'settings/notifications/library')).data.data.settings.libraryIds.sort(),libraries.slice().sort());
        await request(notificationApi+'notifications/preferences', { method: 'PUT', auth: ordinary, body: [{ source: 'notifications', type: 'library.episode.added', channel: 'in-app', enabled: false }] });
    }
    if (has('discord')) {
        const config = (await request('DiscordBridge/admin/v1/settings')).data.data;
        assert.equal(config.libraryFolders.length, nativeFolders.length, 'Bridge must discover every native library ID');
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: { revision: config.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', ...(liveDiscord ? { token: botToken } : {}), settings: { ...config.settings, ...(liveDiscord ? { enabled: true, guildId } : {}), libraryAnnouncementsEnabled: true, libraryDisclosureAcknowledged: true, allowedChannelIds: [liveDiscord ? channelId : '1234567890'], libraryChannelIds: [liveDiscord ? channelId : '1234567890'], libraryIds: libraries } } });
        if (liveDiscord) {
            const channel = await discordApi('channels/' + channelId); assert.equal(channel.guild_id, guildId, 'Test channel must belong to the selected guild');
            await until(async () => { const state = (await request('DiscordBridge/admin/v1/settings')).data.data; return state.connected && state.state === 'connected'; }, 'live gateway and guild command registration');
            const bot = await discordApi('users/@me');
            const commands = await discordApi(`applications/${bot.id}/guilds/${guildId}/commands`);
            for (const name of ['help', 'link', 'unlink', 'reset', 'status', 'invite', 'watchparty']) assert(commands.some(command => command.name === name), 'Expected guild command missing');
            console.log('PASS real bot gateway connection, guild/channel scope and slash-command registration.');
        }
    }
    const inbox = async auth => (await request(notificationApi+'notifications?limit=100', { auth })).data.data.notifications.filter(row => row.event.type.startsWith('library.'));
    assert.equal((await inbox(admin)).length, 0, 'Enabling must not backfill existing media');
    await wait(1100);
    liveStage = 'new-media-scan';
    video('movies/Future/Future (2026).mp4'); video('tv/Disposable Series/Season 01/Disposable Series S01E01.mp4');
    if(notificationStudio)docker('exec',name,'/usr/lib/jellyfin-ffmpeg/ffmpeg','-hide_banner','-loglevel','error','-f','lavfi','-i','color=c=0x25496b:s=200x300:d=1','-vf','drawbox=x=20:y=30:w=160:h=180:color=0x80cbc4:t=fill,drawbox=x=20:y=230:w=110:h=12:color=white:t=fill','-frames:v','1','-threads','1','-y','/media/movies/Future/poster.png');
    await request('Library/Refresh', { method: 'POST', expected: 204 });
    await until(async () => (await mediaItems()).length === 4, 'new episode and movie scan');
    if(notificationStudio)await until(async()=>(await mediaItems()).some(item=>item.name.includes('Future')&&item.imageTags?.primary),'native synthetic poster discovery');
    {
        liveStage = 'inbox-permissions';
        await until(async () => (await inbox(admin)).length === 2, 'permission-aware episode/movie inbox notifications');
        assert.deepEqual((await inbox(admin)).map(row => row.event.type).sort(), ['library.episode.added', 'library.movie.added']);
        assert.equal((await inbox(ordinary)).length, 1, 'Episode-specific personal mute must not mute movies');
        assert.equal((await inbox(blocked)).length, 0, 'Inaccessible library metadata must not enter another user inbox');
        for (const [auth, category, importance] of [[watchlisted, 'watchlisted', 'high'], [favourited, 'favourite', 'silent']]) {
            const rows = await inbox(auth); assert.equal(rows.length, 1, 'Interest-only preferences must exclude unrelated new movies');
            assert.equal(rows[0].event.type, 'library.episode.added'); assert.equal(rows[0].event.importance, importance);
            assert.equal(rows[0].event.metadata.libraryCategories, 'all,' + category, 'Only the recipient\'s own native interest belongs in the notice');
        }
        if(!liveDiscord){
            const catalog=async()=>(await request(notificationApi+'settings/notifications/types')).data.data.catalogs.find(value=>value.source==='notifications');
            const before=await catalog();assert.equal(before.grouping.metadataKey,'groupId');
            const rows=await inbox(admin);assert.equal(rows.length,2);
            for(const row of rows){assert.match(row.event.group?.key||'',/^[a-f0-9]{64}$/i);assert.equal(row.event.group.source,'notifications');assert(row.event.group.label);}
            assert.notEqual(rows[0].event.group.key,rows[1].event.group.key,'A show and unrelated movie must not share a group');
            const setDefault=async enabled=>request(notificationApi+'settings/notifications/grouping',{method:'PUT',body:{source:'notifications',revision:(await catalog()).revision,enabled}});
            const personal=enabled=>request(notificationApi+'notifications/grouping',{method:'PUT',body:{source:'notifications',enabled}});
            await setDefault(false);assert((await inbox(admin)).every(row=>!row.event.group));assert((await inbox(watchlisted)).every(row=>!row.event.group));
            await request(notificationApi+'settings/notifications/grouping',{method:'PUT',body:{source:'notifications',revision:before.revision,enabled:true},expected:409});
            await personal(true);assert((await inbox(admin)).every(row=>row.event.group));assert((await inbox(watchlisted)).every(row=>!row.event.group),'One user override must not change another recipient');
            await personal(null);assert((await inbox(admin)).every(row=>!row.event.group));
            await setDefault(true);await personal(false);assert((await inbox(admin)).every(row=>!row.event.group));assert((await inbox(watchlisted)).every(row=>row.event.group));
            await personal(null);assert((await inbox(admin)).every(row=>row.event.group));
            console.log('PASS real native-library grouping identities, revisioned plugin defaults and per-user inherit/on/off isolation without delivery-preference changes or notification replay.');
        }
        await request(`Users/${ordinary.id}/Policy`, { method: 'POST', body: { ...ordinary.policy, EnableAllFolders: false, EnabledFolders: [] }, expected: 204 });
        assert.equal((await inbox(ordinary)).length, 0, 'Permission revocation must hide queued/stored media details');
        assert.equal((await request(notificationApi+'notifications/unread-count', { auth: ordinary })).data.data.count, 0);
    }
    liveStage = 'discord-queue-completion';
    if (has('discord')) await until(async () => { const health = (await request('DiscordBridge/admin/v1/settings')).data.data.libraryHealth; return health.retained === 2 && health.pending === (liveDiscord ? 0 : 2) && health.uncertainDiscord === 0; }, 'exactly one durable Discord announcement per new item');
    let liveAfter;
    if (liveDiscord) {
        liveStage = 'discord-message-verification';
        // Only inspect messages created during this test by this bot. Never print channel history.
        liveAfter = ((BigInt(liveStarted) - 1420070400000n) << 22n).toString();
        const bot = await discordApi('users/@me');
        let messages = [];
        // Queue completion and Discord's history read may become visible at different times.
        // Retry only read-side verification, never a message send or uncertain delivery.
        liveStage = 'card-history-visibility';
        await until(async () => {
            messages = await discordApi(`channels/${channelId}/messages?after=${liveAfter}&limit=100`);
            return ['New episode available', 'New movie available'].every(title => messages.some(message => message.author.id === bot.id && message.content === title && message.embeds?.[0]?.title === title));
        }, 'Discord card history visibility', 20);
        for (const title of ['New episode available', 'New movie available']) {
            const matches = messages.filter(message => message.author.id === bot.id && message.content === title && message.embeds?.[0]?.title === title);
            liveStage = 'card-match-count';
            console.log('Sanitized card match count:', matches.length);
            assert.equal(matches.length, 1, 'Expected one real synthetic-media announcement per kind');
            liveMessages.push(matches[0].id); assert.equal(matches[0].mention_everyone, false);
            const card = matches[0].embeds[0];
            liveStage = 'card-media-description';
            assert(/Disposable Series|Future/.test(card.description), 'Card must contain synthetic media metadata');
            for (const key of ['author', 'footer', 'url', 'image', 'thumbnail']) { liveStage = 'card-prohibited-' + key; assert(!card[key], 'Shared card must not disclose identity, links or images'); }
            liveStage = 'card-field-allowlist';
            assert.deepEqual(card.fields.map(field => field.name), ['Content', 'Status']);
            liveStage = 'card-user-mentions';
            assert.equal(matches[0].mentions.length, 0, 'No user mentions are allowed');
            liveStage = 'card-identity-exclusion';
            for (const id of [ordinary.id, blocked.id, watchlisted?.id, favourited?.id, ...libraries].filter(Boolean)) assert(!JSON.stringify(card).includes(id), 'No audience or library identities are allowed in shared cards');
        }
        console.log('PASS real installed-plugin Discord episode/movie cards, one per kind, media-only fields, no identities/URLs/images or user/everyone mentions.');
        if(has('notifications')){
            liveStage='notifications-through-bridge';
            const settingsPath=notificationApi+'settings/notifications',before=(await request(settingsPath)).data.data;
            const route={id:randomUUID(),source:'account-hub',type:'notifications.test',channel:'discord',destination:channelId,adminOnly:true,directMessage:false,allowPrivate:false,allowHttp:false};
            await request(settingsPath,{method:'PUT',body:{...before,routes:[...before.routes,route]}});
            try{
                const prior=new Set(messages.map(message=>message.id));
                const catalogs=(await request(settingsPath+'/types')).data.data.catalogs;
                const definition=catalogs.find(catalog=>catalog.source==='account-hub')?.types.find(type=>type.type==='*');
                const sharedText=definition?definition.name+' — '+definition.trigger+'. Open Jellyfin to review.':'A plugin reported an update. Open Jellyfin to review.';
                await request(settingsPath+'/test',{method:'POST',body:{}});
                let delivered;
                await until(async()=>{const current=await discordApi(`channels/${channelId}/messages?after=${liveAfter}&limit=100`);delivered=current.filter(message=>!prior.has(message.id)&&message.author.id===bot.id&&message.content===sharedText);return delivered.length>0;},'Notifications delivery through shared Bridge',30);
                for(const message of delivered)liveMessages.push(message.id);
                assert.equal(delivered.length,1);assert.equal(delivered[0].mention_everyone,false);assert.equal(delivered[0].mentions.length,0);
                console.log('PASS real Notifications → Discord Bridge approved-channel delivery with generic privacy-safe summary and no mentions.');
            }finally{const current=(await request(settingsPath)).data.data;await request(settingsPath,{method:'PUT',body:{...current,routes:current.routes.filter(value=>value.id!==route.id)}});}
        }
    }
    await request('Library/Refresh', { method: 'POST', expected: 204 }); await wait(10000);
    if (has('hub')) assert.equal((await inbox(admin)).length, 2, 'Rescanning must not duplicate inbox notices');
    if (has('discord')) assert.equal((await request('DiscordBridge/admin/v1/settings')).data.data.libraryHealth.retained, 2, 'Hub plus Bridge must not duplicate announcement intake');
    if (liveDiscord) {
        const bot = await discordApi('users/@me');
        const messages = await discordApi(`channels/${channelId}/messages?after=${liveAfter}&limit=100`);
        assert.equal(messages.filter(message => message.author.id === bot.id && /^(New episode available|New movie available)$/.test(message.content) && /Disposable Series|Future/.test(message.embeds?.[0]?.description || '')).length, 2, 'Rescan must not duplicate real Discord messages');
    }
    console.log('PASS synthetic physical episode/movie additions, opt-in without backfill, native-library discovery, durable single Discord intake' + (has('hub') ? ', personal kind preferences, current library access, revocation and rescan deduplication.' : ' and standalone observer without Hub.'));
    if (!liveDiscord) console.log('NOTE Discord external delivery not tested: no disposable bot credentials configured.');
}
async function testV1Flows(person) {
    const send = async (route, payload, stable) => {
        const revision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
        const body = stable || { commandId: randomUUID(), revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', payload };
        return { reply: await request('AccountHub/api/v1/group-policy/commands/' + route, { method: 'POST', body }), body };
    };
    const groupId = randomUUID();
    const people=(await request('AccountHub/api/v1/users?skip=0&limit=100')).data.data.items;
    assert.equal(people.find(user=>guid(user.id)===guid(person.id)).discordLinked,false);
    await request('AccountHub/api/v1/commands/user.discord.unlink',{method:'POST',body:{commandId:randomUUID(),revision:(await request('AccountHub/api/v1/workspace')).data.data.revision,confirmation:'CONFIRM ACCOUNT HUB CHANGE',payload:{userId:person.id}}});
    const copiedGroupId=randomUUID();
    await send('group.copy-user',{id:copiedGroupId,sourceUserId:person.id,name:'Copied native access'});
    await request('AccountHub/api/v1/invite-defaults',{auth:null,expected:[401,403]});
    let defaultsRevision=(await request('AccountHub/api/v1/workspace')).data.data.revision;
    await request('AccountHub/api/v1/invite-defaults',{method:'POST',body:{commandId:randomUUID(),revision:defaultsRevision,confirmation:'CONFIRM ACCOUNT HUB CHANGE',settings:{defaultGroupId:copiedGroupId,discordCreationEnabled:false,allowedCreators:[person.id],discordExpiryMinutes:60,dailyLimit:5}}});
    const inviteDefaults=(await request('AccountHub/api/v1/invite-defaults')).data.data;
    assert.equal(guid(inviteDefaults.defaultGroupId),guid(copiedGroupId));assert.equal(inviteDefaults.discordCreationEnabled,false);
    assert.deepEqual(inviteDefaults.allowedCreators.map(guid),[guid(person.id)]);
    const copiedGroups=(await request('AccountHub/api/v1/group-policy/groups?skip=0&limit=100')).data.data.items;
    const copied=copiedGroups.find(group=>guid(group.id)===guid(copiedGroupId));assert(copied);assert(!Object.hasOwn(JSON.parse(copied.policyJson),'IsAdministrator'),'Templates must not copy administrator privileges');
    await request('AccountHub/api/v1/invite-defaults',{method:'POST',body:{commandId:randomUUID(),revision:(await request('AccountHub/api/v1/workspace')).data.data.revision,confirmation:'CONFIRM ACCOUNT HUB CHANGE',settings:{allowedCreators:['not-a-native-id']}},expected:400});
    const defaultInvite=(await request('AccountHub/api/v1/invites',{method:'POST',body:{commandId:randomUUID(),revision:(await request('AccountHub/api/v1/workspace')).data.data.revision,confirmation:'CONFIRM ACCOUNT HUB CHANGE',note:'Default template fixture'}})).data.data;
    const defaultDetail=(await request('AccountHub/api/v1/invites/'+defaultInvite.id)).data.data;
    assert.equal(guid(defaultDetail.groupId),guid(copiedGroupId));assert.equal(defaultDetail.status,'pending_approval','Selecting a default template must not bypass approval');
    console.log('PASS native permission-template copying, persisted default invite template and administrator-only Discord invitation delegation settings.');
    const saved = await send('group.save', { id: groupId, groupRevision: 0, name: 'V1 disposable group', description: 'Generated fixture only', managedFields: { EnableMediaPlayback: true, EnableContentDownloading: false, MaxActiveSessions: 3 } });
    const groupRevision = saved.reply.data.data.revision;
    await send('assign', { userId: guid(person.id), assignmentRevision: 0, groupId, groupRevision, overrides: {} });
    let preview = (await request('AccountHub/api/v1/group-policy/preview', { method: 'POST', body: { targets: [{ userId: guid(person.id), revision: 0 }], mode: 'apply' } })).data.data;
    const targets = preview.map(view => ({ userId: view.userId, revision: view.revision, groupRevision: view.groupRevision }));
    const applied = await send('apply', { targets });
    await send('apply', null, applied.body);
    assert.equal((await request(`Users/${person.id}`)).data.policy.maxActiveSessions, 3);
    let native = (await request(`Users/${person.id}`)).data;
    await request(`Users/${person.id}/Policy`, { method: 'POST', body: { ...native.policy, maxActiveSessions: 7 }, expected: 204 });
    const drift = (await request(`AccountHub/api/v1/group-policy/users/${guid(person.id)}/drift`)).data.data;
    assert(drift.changedFields.includes('MaxActiveSessions'), 'Native changes must appear as drift');
    assert.equal((await request(`Users/${person.id}`)).data.policy.maxActiveSessions, 7, 'Observing drift must not silently reconcile');
    preview = (await request('AccountHub/api/v1/group-policy/preview', { method: 'POST', body: { targets, mode: 'reset' } })).data.data;
    await send('reset', { targets: preview.map(view => ({ userId: view.userId, revision: view.revision, groupRevision: view.groupRevision })) });
    assert.equal((await request(`AccountHub/api/v1/group-policy/users/${guid(person.id)}/drift`)).data.data.groupId, null);
    const revision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
    const created = (await request('AccountHub/api/v1/invites', { method: 'POST', body: { commandId: randomUUID(), revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', expiresInMinutes: 30, maxUses: 1, groupId, note: 'Disposable v1 onboarding' } })).data.data;
    const approval = { commandId: randomUUID(), revision: (await request('AccountHub/api/v1/workspace')).data.data.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE' };
    const issued = (await request(`AccountHub/api/v1/invites/${created.id}/approve`, { method: 'POST', body: approval })).data.data;
    const token = new URL(issued.oneTimeLink, base).hash.slice('#token='.length);
    assert(/^[A-Za-z0-9_-]{43}$/.test(token), 'Approval must disclose one valid credential once');
    const repeated = (await request(`AccountHub/api/v1/invites/${created.id}/approve`, { method: 'POST', body: approval })).data.data;
    assert(!repeated.oneTimeLink, 'Completed approval retries must never disclose credentials again');
    const claimId = randomUUID(), username = 'v1-invited-' + randomUUID().slice(0, 8), password = 'Disposable-V1-Only-9714!';
    const redeemed = await request('AccountHub/public/v1/invites/redeem', { method: 'POST', auth: null, body: { token, claimId, username, password } });
    assert.equal(redeemed.data.data.status, 'completed', 'Native invite saga must finish, not return fake success');
    await request('AccountHub/public/v1/invites/redeem', { method: 'POST', auth: null, body: { token, claimId, username, password: 'Must-not-replace-the-password!' } });
    const login = (await request('Users/AuthenticateByName', { method: 'POST', auth: { device: 'v1-invite-test' }, body: { Username: username, Pw: password } })).data;
    await request('AccountHub/api/v1/invite-defaults',{auth:{token:login.accessToken},expected:403});
    assert(login.accessToken, 'Redeeming user must be able to sign in with only their chosen password');
    assert(!login.user.policy.isAdministrator && !login.user.policy.isDisabled && login.user.policy.isHidden, 'New accounts must be enabled non-admins hidden from public login tiles');
    assert.equal(login.user.policy.maxActiveSessions, 3);
    assert(!(await request('Users/Public', { auth: null })).data.some(user => user.name === username), 'Invite onboarding must not publish the new username');
    assert.equal((await request(`AccountHub/api/v1/group-policy/users/${guid(login.user.id)}/drift`)).data.data.groupId, groupId);
    const detail = await request(`AccountHub/api/v1/invites/${created.id}`);
    assert.equal(detail.data.data.completedUses, 1); assert.equal(detail.data.data.reservedUses, 1);
    assert(!detail.text.includes(token), 'Administrator history must not return invite credentials');
    const reconcilePath = `AccountHub/api/v1/invite-reservations/${detail.data.data.reservations[0].id}/reconcile`;
    const reconcile = { commandId: randomUUID(), revision: (await request('AccountHub/api/v1/workspace')).data.data.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE' };
    assert.equal((await request(reconcilePath, { method: 'POST', body: reconcile })).data.data.status, 'completed');
    assert.equal((await request(reconcilePath, { method: 'POST', body: reconcile })).data.data.status, 'completed', 'Reconciliation retries must return their durable result');
    await request('AccountHub/public/v1/invites/redeem', { method: 'POST', auth: null, body: { token, claimId: randomUUID(), username: 'must-not-create', password }, expected: 400 });
    if (databaseSafety) {
        const createBody = { commandId: randomUUID(), revision: (await request('AccountHub/api/v1/workspace')).data.data.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', expiresInMinutes: 30, maxUses: 3, groupId };
        const concurrentInvite = (await request('AccountHub/api/v1/invites', { method: 'POST', body: createBody })).data.data;
        const approved = (await request(`AccountHub/api/v1/invites/${concurrentInvite.id}/approve`, { method: 'POST', body: { commandId: randomUUID(), revision: (await request('AccountHub/api/v1/workspace')).data.data.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE' } })).data.data;
        const concurrentToken = new URL(approved.oneTimeLink, base).hash.slice('#token='.length);
        for (let i = 0; i < 3; i++) {
            const suffix = randomUUID().slice(0, 8);
            await Promise.all([
                request('Users/New', { method: 'POST', body: { Name: 'native-race-' + suffix }, expected: 409 }),
                request('AccountHub/public/v1/invites/redeem', { method: 'POST', auth: null, body: { token: concurrentToken, claimId: randomUUID(), username: 'invite-race-' + suffix, password } })
            ]);
        }
        console.log('PASS native HTTP creation is blocked during concurrent Hub onboarding; native ID uniqueness is checked offline after restart.');
    }
    console.log('PASS installed v1 groups/assignment, explicit apply/retry, native drift/reset, approval-only invite issuance, one-time readback, native password saga, hidden non-admin creation, primary group and slot deduplication.');
}
const liveStarted = Date.now();
let rootCreated = false,linkFactor;
try {
    // Refuse to reuse any existing directory, even one from an earlier test.
    await mkdir(dirname(root), { recursive: true });
    await mkdir(root, { recursive: false });
    rootCreated = true;
    await writeFile(join(root, 'targets.json'), await readFile(process.env.SUITE_TEST_TARGETS));
    await mkdir(join(root, 'config', 'config'), { recursive: true }); await mkdir(join(root, 'cache')); if (libraryFixture) await mkdir(join(root, 'media'));
    const manifest = JSON.parse(await readFile(join(packages, 'manifest.json'), 'utf8'));
    assert(Array.isArray(manifest) && manifest.length >= 1 && manifest.length <= 6, 'Expected selected-plugin or aggregate package catalog');
    assert.equal(new Set(manifest.map(plugin=>plugin.name)).size,manifest.length,'Duplicate plugin package');
    assert(manifest.every(plugin=>Object.values(pluginNames).includes(plugin.name)),'Unknown plugin package');
    for(const key of selected)assert(manifest.some(plugin=>plugin.name===pluginNames[key]),'Selected plugin package missing: '+key);
    const packageRuntime = platform === 'linux/arm64' ? 'linux-arm64' : 'linux-x64';
    assert(manifest.every(plugin => new URL(plugin.versions[0].sourceUrl).pathname.endsWith('-' + packageRuntime + '.zip')), 'Packages must match the actual server architecture');
    for (const plugin of manifest.filter(item => selected.some(key => pluginNames[key] === item.name))) {
        assert.equal(plugin.versions[0].targetAbi, target.abi, 'Package must be built against this run\'s latest Jellyfin SDK');
        const zip = join(packages, new URL(plugin.versions[0].sourceUrl).pathname.split('/').at(-1));
        const destination = join(root, 'config', 'plugins', plugin.name + '_' + plugin.versions[0].version);
        run(process.env.PWSH || 'pwsh', ['-NoProfile', '-CommandWithArgs', '[IO.Compression.ZipFile]::ExtractToDirectory($args[0],$args[1])', zip, destination]);
    }
    if (option('security')) {
        const zip = option('security') === 'latest' ? await downloadSecurity(targets.security, join(root, 'security-latest.zip')) : resolve(option('security'));
        assert.equal(createHash('sha256').update(await readFile(zip)).digest('hex'), targets.security.sha256, 'Security fixture must match the latest release discovered for this run');
        targets.security.assetAssemblyVersion = run(process.env.PWSH || 'pwsh', ['-NoProfile', '-File', resolve('scripts/read-security-version.ps1'), '-ZipPath', zip, '-Sha256', targets.security.sha256]);
        const evidence = JSON.parse(await readFile(join(root, 'targets.json'), 'utf8'));
        evidence.targets.security = targets.security;
        await writeFile(join(root, 'targets.json'), JSON.stringify(evidence, null, 2));
        run(process.env.PWSH || 'pwsh', ['-NoProfile', '-CommandWithArgs', '[IO.Compression.ZipFile]::ExtractToDirectory($args[0],$args[1])', zip, join(root, 'config', 'plugins', 'JellyfinSecurity_' + targets.security.version)]);
    }
    // cloudflared reaches this fixture only over its shared loopback namespace.
    // Let native Jellyfin honor that exact proxy's HTTPS forwarding information;
    // otherwise strict same-origin POST authorization correctly rejects HTTPS
    // browser requests seen as backend HTTP. Never trust arbitrary proxy ranges.
    const trustedTunnelProxy=discordTunnel?'<KnownProxies><string>127.0.0.1</string></KnownProxies>':'';
    await writeFile(join(root, 'config', 'config', 'network.xml'), '<?xml version="1.0"?><NetworkConfiguration><BaseUrl>/suite</BaseUrl><EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>true</EnableRemoteAccess><InternalHttpPort>8096</InternalHttpPort>'+trustedTunnelProxy+'</NetworkConfiguration>');
    docker('run', '-d', '--platform', platform, '--name', name, '-p', '127.0.0.1::8096', '-v', `${join(root, 'config')}:/config`, '-v', `${join(root, 'cache')}:/cache`, ...(libraryFixture ? ['-v', `${join(root, 'media')}:/media`] : []), target.image); started = true;
    if (platform === 'linux/arm64') assert.equal(docker('exec', name, 'uname', '-m'), 'aarch64', 'ARM fixture must execute the ARM server image, not just ARM-named plugin ZIPs');
    base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
    let ready = false;
    for (let i = 0; i < (platform === 'linux/arm64' ? 300 : 90); i++) { try { if ((await request('Startup/Configuration', { auth: null, expected: null })).status === 200) { ready = true; break; } } catch {} await wait(1000); }
    assert(ready, 'Jellyfin startup timed out');
    assertRuntimeVersion((await request('System/Info/Public', { auth: null })).data.version, target.version, 'Jellyfin');
    const password = interactiveDiscord?'Disposable-Admin-'+randomBytes(24).toString('base64url')+'!':'Disposable-Only-Suite-9714!';
    await request('Startup/Configuration', { method: 'POST', auth: null, body: { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' }, expected: 204 });
    await request('Startup/User', { auth: null });
    await request('Startup/User', { method: 'POST', auth: null, body: { Name: 'suite-admin', Password: password }, expected: 204 });
    await request('Startup/RemoteAccess', { method: 'POST', auth: null, body: { EnableRemoteAccess: true, EnableAutomaticPortMapping: false }, expected: 204 });
    await request('Startup/Complete', { method: 'POST', auth: null, expected: 204 });
    const login = await request('Users/AuthenticateByName', { method: 'POST', auth: {}, body: { Username: 'suite-admin', Pw: password } }); admin = { token: login.data.accessToken };
    const installed = (await request('Plugins')).data;
    if (option('security')) {
        const security = installed.find(plugin => plugin.id.replaceAll('-', '') === '94879a0cda244eb1aa06f28b4b9333b1');
        assert.equal(security?.status, 'Active', 'Latest Security must actually load');
        assertRuntimeVersion(security.version, targets.security.assetAssemblyVersion, 'Security');
    }
    for (const plugin of manifest) {
        const wanted = selected.some(key => pluginNames[key] === plugin.name);
        const active = installed.some(p => p.id.replaceAll('-', '') === plugin.guid.replaceAll('-', '') && p.status === 'Active');
        assert.equal(active, wanted, `${plugin.name} installed state must match selection`);
    }
    const scheduled = (await request('ScheduledTasks')).data;
    for (const [plugin, key] of [['automatic', 'AutomaticDeletionRequests.RuleMaintenance'], ['deletion', 'DeletionQueue.RemovalMaintenance'], ['deletion', 'DeletionQueue.OverlayGeneration']]) {
        const task = scheduled.find(t => t.key === key);
        assert.equal(!!task, has(plugin), 'Native scheduled task discovery: ' + key);
        if (!task) continue;
        assert(task.triggers.some(t => t.type === 'DailyTrigger'), key + ' must default to a native daily trigger');
        const before = task.lastExecutionResult?.endTimeUtc;
        await request('ScheduledTasks/Running/' + task.id, { method: 'POST', expected: 204 });
        await until(async () => {
            const current = (await request('ScheduledTasks')).data.find(t => t.id === task.id);
            return current.state === 'Idle' && current.lastExecutionResult?.endTimeUtc !== before && current.lastExecutionResult?.status === 'Completed';
        }, 'daily scheduled task can run manually: ' + key);
    }
    for (const [key, prefix] of [['deletion', 'DeletionQueue'], ['syncplay', 'PrivateSyncPlay']]) if (has(key)) {
        const dependencies = (await request(prefix + '/admin/dependencies')).data.data.items;
        assert.equal(dependencies.find(item => item.id === 'account-hub').available, has('hub'));
        assert.equal(dependencies.find(item => item.id === 'discord').available, has('discord'));
    }
    if (permissionsCheck) await testPermissionSurfaces();
    if (wiringCheck) await testSuiteWiring();
    if(has('deletion')){
        await request('DeletionQueue/admin/approvals',{auth:null,expected:[401,403]});
        assert(Array.isArray((await request('DeletionQueue/admin/approvals')).data),'Approval view must be an administrator-authorized, focused projection');
    }
    if(has('notifications')) {
        const endpoint='Notifications/api/v1/';
        await request(endpoint+'notifications',{auth:null,expected:[401,403]});
        const worker=(await request(endpoint+'notifications/push/sw.js',{auth:null})).text;assert(worker.includes('showNotification'),'Narrow push worker must be public without exposing any session');
        await request(endpoint+'notifications/push',{auth:null,expected:[401,403]});
        let push=(await request(endpoint+'settings/notifications/push')).data.data;assert.equal(push.enabled,false,'Web Push must default off');assert(!('privateKey'in push));
        await request(endpoint+'settings/notifications/push',{method:'PUT',body:{revision:push.revision,enabled:true}});
        const enabledPush=(await request(endpoint+'notifications/push')).data.data;assert(enabledPush.enabled&&enabledPush.publicKey.length===87);assert(!('privateKey'in enabledPush));
        push=(await request(endpoint+'settings/notifications/push')).data.data;await request(endpoint+'settings/notifications/push',{method:'PUT',body:{revision:push.revision,enabled:false}});
        const ticket=(await request(endpoint+'notifications/live-ticket',{method:'POST'})).data.data.ticket;assert(/^[A-F0-9]{64}$/.test(ticket));
        const handshake=()=>new Promise((resolve,reject)=>{
            const address=new URL(endpoint+'notifications/live',base+'/');
            const req=httpRequest(address,{headers:{Connection:'Upgrade',Upgrade:'websocket','Sec-WebSocket-Version':'13','Sec-WebSocket-Key':randomBytes(16).toString('base64'),'Sec-WebSocket-Protocol':'notifications.v1, ticket.'+ticket,Origin:address.origin}});
            req.setTimeout(10000,()=>req.destroy(Error('Live socket timeout')));req.on('error',reject);
            req.on('upgrade',(response,socket)=>{assert.equal(response.headers['sec-websocket-protocol'],'notifications.v1');socket.destroy();resolve(response.statusCode);});
            req.on('response',response=>{response.resume();resolve(response.statusCode);});req.end();
        });
        assert.equal(await handshake(),101,'Native installed socket must accept a valid grant without a URL token');assert.equal(await handshake(),401,'Native socket grant must not be replayable');
        console.log('PASS installed Web Push defaults/contact/enable/disable/authentication, anonymous scoped worker and native single-use live handshake');
        const before=(await request(endpoint+'notifications/unread-count')).data.data.count;
        await request(endpoint+'settings/notifications/test',{method:'POST'});
        const inbox=(await request(endpoint+'notifications')).data.data.notifications;
        assert(inbox.length>=1,'Standalone provider must persist and return notifications');
        assert.equal((await request(endpoint+'notifications/unread-count')).data.data.count,before+1);
        await request(endpoint+'notifications/'+inbox[0].id+'/read',{method:'POST'});
        assert.equal((await request(endpoint+'notifications/unread-count')).data.data.count,before);
        const credential=(await request(endpoint+'settings/notifications/producer-tokens',{method:'POST',expected:[201],body:{source:'account-hub',scopes:['notifications:write']}})).data.data;
        const eventId=randomUUID();
        const publication={apiVersion:'1',operation:'notifications.publish',requestId:randomUUID(),payload:{schemaVersion:1,eventId,source:'account-hub',type:'test.notification',severity:'info',title:'Disposable integration notification',body:'No media was changed.',audience:[guid(login.data.user.id)],adminOnly:false,occurredAt:new Date().toISOString()}};
        const published=await request(endpoint+'producer/notifications',{method:'POST',auth:null,headers:{Authorization:'Bearer '+credential.token},body:publication});
        assert.equal(published.data.data.status,'accepted');
        const repeated=await request(endpoint+'producer/notifications',{method:'POST',auth:null,headers:{Authorization:'Bearer '+credential.token},body:publication});
        assert.equal(repeated.data.data.status,'duplicate');
        assert.equal((await request(endpoint+'notifications/unread-count')).data.data.count,before+1,'Producer replay must not duplicate the inbox');
        await request(endpoint+'producer/notifications',{method:'POST',auth:null,body:publication,expected:[400,401,403]});
        await request(endpoint+'notifications/'+inbox[0].id+'/dismiss',{method:'POST'});
        const archived=(await request(endpoint+'notifications?history=true')).data.data.notifications;
        assert(archived.some(row=>row.id===inbox[0].id&&row.dismissed&&row.read),'Dismissed notification must remain in recipient history');
        assert(!(await request(endpoint+'notifications')).data.data.notifications.some(row=>row.id===inbox[0].id),'Dismissed notification must leave the active inbox');
        if(process.argv.includes('--notifications-restart')) {
            docker('restart',name);
            base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
            let ready=false;
            for(let i=0;i<90;i++){try{if((await request('System/Info/Public',{auth:null,expected:null})).status===200){ready=true;break;}}catch{}await wait(1000);}
            assert(ready,'Notification restart fixture did not recover');
            assert.equal((await request(endpoint+'notifications/unread-count')).data.data.count,before+1,'Read state/inbox must survive restart');
        }
        const html=(await request('web/index.html',{auth:null})).text;
        const mutedType='test.blocked';
        await request(endpoint+'notifications/preferences/rule',{method:'PUT',body:{source:'account-hub',type:mutedType,channel:'in-app',enabled:false}});
        const mutedEvent={...publication.payload,eventId:randomUUID(),type:mutedType,title:'Disposable blocked notification'};
        const mutedReply=await request(endpoint+'producer/notifications',{method:'POST',auth:null,headers:{Authorization:'Bearer '+credential.token},body:{...publication,requestId:randomUUID(),payload:mutedEvent}});assert.deepEqual(mutedReply.data.data.notificationIds,[]);
        const blockedRows=(await request(endpoint+'notifications?blocked=true')).data.data.notifications;assert(blockedRows.some(row=>row.event.eventId===mutedEvent.eventId));assert(!(await request(endpoint+'notifications')).data.data.notifications.some(row=>row.event.eventId===mutedEvent.eventId));
        await request(endpoint+'notifications/preferences/rule',{method:'PUT',body:{source:'account-hub',type:mutedType,channel:'in-app',enabled:true,importance:'silent'}});
        const restored=(await request(endpoint+'notifications')).data.data.notifications.find(row=>row.event.eventId===mutedEvent.eventId);assert(restored?.event.retroactive);assert.equal(restored.event.importance,'silent');assert(!(await request(endpoint+'notifications?blocked=true')).data.data.notifications.some(row=>row.event.eventId===mutedEvent.eventId));
        assert((await request(endpoint+'notifications/categories')).data.data.isAdministrator);
        const personalUser = (await nativeFixtureUser({ Name: 'notification-preferences-user' })).data;
        const personalLogin = (await request('Users/AuthenticateByName', { method:'POST', auth:{}, body:{ Username:personalUser.name, Pw:'' } })).data;
        const personal = { token: personalLogin.accessToken };
        const adminPreferences = (await request(endpoint+'notifications/preferences')).data.data.preferences;
        const personalCatalog = (await request(endpoint+'notifications/categories', { auth: personal })).data.data;
        assert.equal(personalCatalog.isAdministrator, false);
        if (has('deletion')) assert(!personalCatalog.catalogs.find(c => c.source === 'deletion-queue')?.types.some(t => t.type === 'proposal.approval_required' || t.type === 'alert.removal_uncertain'), 'Personal category discovery must omit administrative types');
        for (const type of ['category.library.watchlisted', 'category.library.favourite', 'test.personal'])
            await request(endpoint+'notifications/preferences/rule', { method:'PUT', auth:personal, body:{ source:'account-hub', type, channel:'in-app', enabled:true, importance:'high' } });
        const personalRules=(await request(endpoint+'notifications/preferences', { auth:personal })).data.data.preferences;
        assert.equal(personalRules.length, 5); // Three saved rules plus two legacy library-owner aliases.
        assert(personalRules.some(rule=>rule.source==='notifications'&&rule.type==='category.library.watchlisted'&&rule.importance==='high'));
        assert.deepEqual((await request(endpoint+'notifications/preferences')).data.data.preferences, adminPreferences, 'Personal changes must not edit administrator preferences');
        await request(endpoint+'settings/notifications/types', { auth:personal, expected:[401,403] });
        const personalEvent = { ...publication.payload, eventId:randomUUID(), type:'test.personal', audience:[guid(personalUser.id)] };
        await request(endpoint+'producer/notifications', { method:'POST', auth:null, headers:{ Authorization:'Bearer '+credential.token }, body:{ ...publication, requestId:randomUUID(), payload:personalEvent } });
        assert.equal((await request(endpoint+'notifications', { auth:personal })).data.data.notifications.find(row => row.event.eventId === personalEvent.eventId)?.event.importance, 'high');
        assert(!(await request(endpoint+'notifications')).data.data.notifications.some(row => row.event.eventId === personalEvent.eventId), 'A personal notification must not enter another inbox');
        await request('Users/'+personalUser.id, { method:'DELETE', expected:204 });
        console.log('PASS installed per-plugin/category importance, retained Blocked inbox and retroactive visibility without external replay');
        console.log('PASS ordinary-user category discovery/importance, administrator-type filtering, personal preference/inbox isolation and server-management denial');
        assert(html.includes('data-notifications-bootstrap'),'Standalone Notifications owns Web injection');
        assert(!html.includes('data-account-hub-bootstrap'),'Hub must not inject a competing bell');
        assert.equal((html.match(/data-notifications-bootstrap/g)||[]).length,1);
        console.log('PASS standalone notification inbox, native authorization, read state and exclusive Web ownership');
    }
    if (has('automatic')) {
        await request('AutomaticDeletionRequests/admin', { auth: null, expected: [401,403] });
        const rules = (await request('AutomaticDeletionRequests/admin')).data;
        assert.equal(rules.configuration.enabled, false, 'Automation must be disabled by default');
        assert.equal(rules.queueAvailable, has('deletion'));
        const user = (await nativeFixtureUser({ Name: 'automation-nonadmin' })).data;
        const ordinary = (await request('Users/AuthenticateByName', { method: 'POST', auth: {}, body: { Username: user.name, Pw: '' } })).data;
        await request('AutomaticDeletionRequests/admin', { auth: { token: ordinary.accessToken }, expected: [401,403] });
        if (has('deletion')) await request('DeletionQueue/admin/presentation', { auth: { token: ordinary.accessToken }, expected: [401,403] });
        if (has('deletion')) await request('DeletionQueue/admin/seerr/test', { method: 'POST', body: {}, auth: { token: ordinary.accessToken }, expected: [401,403] });
        await request('AutomaticDeletionRequests/admin', { method: 'PUT', body: { configuration: rules.configuration, confirmAutomation: false }, headers: { Origin: 'https://attacker.invalid' }, expected: 403 });
        await request('Users/' + user.id, { method: 'DELETE', expected: 204 });
        console.log('PASS installed automatic rules: disabled by default, dependency discovery and native administrator authorization');
    }
    if (has('deletion')) {
        await request('DeletionQueue/admin/presentation', { auth: null, expected: [401,403] });
        await request('DeletionQueue/admin/seerr/test', { method: 'POST', body: {}, auth: null, expected: [401,403] });
        assert.equal((await request('DeletionQueue/admin/seerr/test', { method: 'POST', body: {} })).data.working, false, 'An unconfigured saved connection cannot pass a test');
        const initial = (await request('DeletionQueue/admin/presentation')).data;
        assert.equal(initial.settings.overlaysEnabled, false); assert.equal(initial.settings.collectionEnabled, false);
        await request('DeletionQueue/admin/presentation', { method: 'PUT', body: { settings: { ...initial.settings, collectionEnabled: true }, confirmChanges: false }, expected: [400,409] });
        const enabled = (await request('DeletionQueue/admin/presentation', { method: 'PUT', body: { settings: { ...initial.settings, collectionEnabled: true }, confirmChanges: true } })).data;
        await request('DeletionQueue/admin/presentation/sync', { method: 'POST', body: {}, expected: 204 });
        const managed = (await request('DeletionQueue/admin/presentation')).data;
        assert(managed.managedCollectionId, 'Native managed collection creation must persist its receipt');
        assert(managed.collections.some(c => c.id === managed.managedCollectionId));
        await request('DeletionQueue/admin/presentation', { method: 'PUT', body: { settings: initial.settings, confirmChanges: false }, expected: 409 });
        await request('DeletionQueue/admin/presentation', { method: 'PUT', body: { settings: { ...enabled, collectionEnabled: false }, confirmChanges: false } });
        await request('DeletionQueue/admin/presentation/sync', { method: 'POST', body: {}, expected: 204 });
        const restored = (await request('DeletionQueue/admin/presentation')).data;
        assert(restored.collections.some(c => c.id === managed.managedCollectionId), 'Disabling must retain the native collection');
        console.log('PASS installed presentation defaults, confirmation/revision checks, native collection creation and non-destructive disabling');
        const preview = await request('DeletionQueue/targets/items/11111111-1111-1111-1111-111111111111/preview', { method: 'POST', body: {}, expected: [404,409,503] });
        assert.notEqual(preview.data.code, 'InvalidJson', 'Removal preview must reach dependency/target validation');
        if (!has('notifications')) assert.equal(preview.data.code, 'NotificationsRequired');
    }
    if (has('discord')) {
        const bridge = (await request('DiscordBridge/admin/v1/settings')).data.data;
        assert.equal(bridge.state, 'disabled');
        assert.equal(bridge.features.find(item => item.id === 'identity').available, has('hub'));
        assert.equal(bridge.features.find(item => item.id === 'watchparty').available, has('hub') && has('syncplay'));
        assert.equal(bridge.features.find(item => item.id === 'transport').available, true);
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: { revision: bridge.revision, settings: bridge.settings, confirmation: 'CONFIRM ACCOUNT HUB CHANGE' } });
        // Transport configuration is operational, not a fresh-factor or typed-confirmation action.
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: { revision: bridge.revision + 1, settings: bridge.settings } });
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: { revision: bridge.revision + 2, settings: { ...bridge.settings, publicServerUrl: 'http://insecure.invalid' } }, expected: 400 });
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: { revision: bridge.revision, settings: bridge.settings }, expected: 409 });
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', raw: '{"revision":1,"revision":1}', expected: 400 });
        await request('DiscordBridge/admin/v1/settings', { headers: { Origin: 'https://attacker.invalid' }, expected: 403 });
    }
    if (!has('hub')) await request('AccountHub/api/v1/workspace', { expected: 404 });
    if (has('hub')) {
    const workspace = await request('AccountHub/api/v1/workspace'); assert.equal(workspace.data.ok, true);
    const dependencies = (await request('AccountHub/api/v1/dependencies')).data.data.items;
    assert.equal(dependencies.find(item => item.id === 'discord').available, has('discord'));
    assert.equal(dependencies.find(item => item.id === 'private-syncplay').available, has('syncplay'));
    assert.equal(dependencies.find(item => item.id === 'deletion-queue').available, has('deletion'));
    assert.equal(dependencies.find(item => item.id === 'automatic').available, has('automatic'));
    await request('AccountHub/api/v1/workspace', { auth: null, expected: [401, 403] });
    await request('AccountHub/api/v1/workspace', { headers: { Origin: 'https://attacker.invalid' }, expected: 403 });
    const people = await request('AccountHub/api/v1/users'); assert.equal(people.data.data.items.length, 1);
    for (const [key, domain] of [['deletion','deletion-queue'], ['syncplay','private-syncplay']]) if (has(key)) assert.equal((await request('AccountHub/api/v1/domains/' + domain + '/capabilities')).data.ok, true);
    assert.equal((await request('AccountHub/api/v1/notifications/unread-count')).data.ok, true);
    await request('AccountHub/api/v1/policy/preview', { method: 'POST', raw: '{"userIds":[],"userIds":[]}', expected: 400 });
    await request('AccountHub/api/v1/security/jellyfin-security', { method: 'POST', auth: null, body: {}, expected: [400, 401] });
    const html = await request('web/index.html', { auth: null }); assert(html.text.includes(has('notifications')?'data-notifications-bootstrap':'data-account-hub-bootstrap'), 'Hosted web must include bounded bell bootstrap');
    const reset = await request('AccountHub/reset', { auth: null }); assert(reset.text.includes('/suite/AccountHub/'), 'Public shell must preserve BaseUrl'); assert.equal(reset.headers.get('referrer-policy'), 'no-referrer');
    const key = randomUUID(), command = { commandId: key, revision: workspace.data.data.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', payload: { name: 'Disposable', managedFields: { EnableMediaPlayback: true, EnableContentDownloading: false } } };
    const created = await request('AccountHub/api/v1/commands/group.save', { method: 'POST', body: command }); assert.equal(created.data.data.status, 'saved');
    const retry = await request('AccountHub/api/v1/commands/group.save', { method: 'POST', body: command }); assert.deepEqual(retry.data, created.data, 'Completed retry must not replay the effect');
    await request('AccountHub/api/v1/commands/group.save', { method: 'POST', body: { ...command, payload: { ...command.payload, name: 'Changed' } }, expected: 409 });
    const person = (await nativeFixtureUser({ Name: 'suite-user' })).data;
    const ownAccount=(await request('AccountHub/api/v1/self')).data.data;
    assert.equal(guid(ownAccount.userId),guid(login.data.user.id));assert.equal(ownAccount.discordLinked,false);
    await request('AccountHub/api/v1/discord/link/confirm',{method:'POST',body:{token:'a'.repeat(43),confirmation:'LINK MY DISCORD ACCOUNT',userId:person.id},expected:409});
    const accountPassword=humanWindow?'Discord-Test-'+randomBytes(12).toString('hex')+'!':password;
    await request(`Users/${person.id}/Password`, { method: 'POST', body: { CurrentPw: '', NewPw: accountPassword, ResetPassword: false }, expected: 204 });
    const userLogin = (await request('Users/AuthenticateByName', { method: 'POST', auth: {}, body: { Username: 'suite-user', Pw: accountPassword } })).data;
    if(humanWindow)interactiveAccount={id:person.id,username:'suite-user',password:accountPassword,token:userLogin.accessToken};
    await request('AccountHub/api/v1/workspace', { auth: { token: userLogin.accessToken }, expected: 403 });
    await request('Auth/Keys?app=DisposableSuiteTest', { method: 'POST', expected: 204 });
    const apiKey = (await request('Auth/Keys')).data.items.find(item => item.appName === 'DisposableSuiteTest'); assert(apiKey?.accessToken);
    await request('AccountHub/api/v1/workspace', { auth: { token: apiKey.accessToken }, expected: 403 });
    const revision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
    const changed = await request('AccountHub/api/v1/commands/policy.apply', { method: 'POST', body: { commandId: randomUUID(), revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', payload: { userIds: [person.id, login.data.user.id], managedFields: { EnableContentDownloading: false, MaxActiveSessions: 3 } } } });
    assert(changed.data.data.outcomes.some(item => item.status === 'administrator_skipped'));
    const applied = changed.data.data.outcomes.find(item => item.status === 'applied');
    assert(applied, 'Policy apply failed: ' + JSON.stringify(changed.data.data.outcomes.map(({ status, code }) => ({ status, code }))));
    const snapshot = applied.snapshotId; assert(snapshot);
    const history = (await request('AccountHub/api/v1/policy/snapshots', { method: 'POST', body: { userId: person.id } })).data.data;
    const reviewedSnapshot = history.items.find(item => item.snapshotId === snapshot);
    assert(reviewedSnapshot, 'Rollback history must expose the stored snapshot ID used by the command');
    assert.notEqual(reviewedSnapshot.eventId, reviewedSnapshot.snapshotId);
    assert.equal((await request(`Users/${person.id}`)).data.policy.maxActiveSessions, 3);
    const nextRevision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
    await request('AccountHub/api/v1/commands/policy.rollback', { method: 'POST', body: { commandId: randomUUID(), revision: nextRevision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', payload: { userIds: [guid(person.id)], snapshotId: reviewedSnapshot.snapshotId } } });
    assert.notEqual((await request(`Users/${person.id}`)).data.policy.maxActiveSessions, 3);
    console.log('PASS non-admin/API-key rejection, managed-policy apply, administrator exclusion, durable snapshot and managed-only rollback.');
    if (!liveDiscord) {
        const currentWorkspace = async () => (await request('AccountHub/api/v1/workspace')).data.data;
        const accountCommand = async (operation, userId, expected = 200) => request('AccountHub/api/v1/commands/' + operation, { method: 'POST', body: { commandId: randomUUID(), revision: (await currentWorkspace()).revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', payload: { userId } }, expected });
        await accountCommand('user.disable', login.data.user.id, 403);
        await accountCommand('user.disable', person.id);
        assert.equal((await request(`Users/${person.id}`)).data.policy.isDisabled, true);
        await request('Users/Me', { auth: { token: userLogin.accessToken }, expected: [401, 403] });
        await accountCommand('user.enable', person.id);
        assert.equal((await request(`Users/${person.id}`)).data.policy.isDisabled, false);
        const session = (await request('Users/AuthenticateByName', { method: 'POST', auth: { device: 'workspace-session-test' }, body: { Username: 'suite-user', Pw: accountPassword } })).data;
        await accountCommand('user.sessions.revoke', person.id);
        await request('Users/Me', { auth: { token: session.accessToken }, expected: [401, 403] });
        const initial = await currentWorkspace(), retention = async days => request('AccountHub/api/v1/settings/retention', { method: 'POST', body: { commandId: randomUUID(), revision: (await currentWorkspace()).revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', auditRetentionDays: days, notificationRetentionDays: initial.notificationRetentionDays } });
        await retention(initial.auditRetentionDays === 30 ? 31 : 30);
        assert.notEqual((await currentWorkspace()).auditRetentionDays, initial.auditRetentionDays);
        await retention(initial.auditRetentionDays);
        const config = await currentWorkspace();
        await request('AccountHub/api/v1/security/settings', { method: 'PUT', body: { commandId: randomUUID(), revision: config.revision, confirmation: 'CHANGE SECURITY SETTINGS', settings: { stepUpLevelMirror: config.stepUpLevelMirror, alertMode: config.securityAlertMode, passwordLoginPolicy: config.passwordLoginPolicy } } });
        const savedConfig = await currentWorkspace();
        for (const key of ['stepUpLevelMirror', 'securityAlertMode', 'passwordLoginPolicy']) assert.equal(savedConfig[key], config[key]);
        assert.equal((await request('AccountHub/api/v1/audit/verify', { method: 'POST', body: {} })).data.data.valid, true);
        const audit = (await request('AccountHub/api/v1/audit/query', { method: 'POST', body: { eventType: 'user.disable.completed', limit: 50 } })).data.data;
        assert(audit.items.length > 0); assert(audit.items.every(row => row.event.eventType === 'user.disable.completed'));
        for (const format of ['csv', 'json']) {
            const exported = (await request('AccountHub/api/v1/audit/export', { method: 'POST', body: { format } })).data.data;
            if (format === 'json') assert(Array.isArray(JSON.parse(exported.content))); else assert(exported.content.startsWith('sequence,event,hash\r\n'));
        }
        for (const route of ['policy/snapshots', 'audit/query', 'audit/export', 'settings/retention']) await request('AccountHub/api/v1/' + route, { method: 'POST', auth: { token: apiKey.accessToken }, body: {}, expected: 403 });
        // Later native Security checks need a new session, not a token deliberately revoked above.
        userLogin.accessToken = (await request('Users/AuthenticateByName', { method: 'POST', auth: { device: 'suite-user-followups' }, body: { Username: 'suite-user', Pw: accountPassword } })).data.accessToken;
        console.log('PASS installed account enable/disable, protected administrator, native session revocation, retention/Security settings persistence and filtered/verified CSV/JSON audit exports.');
        if (has('deletion')) {
            const route = 'AccountHub/api/v1/domains/deletion-queue/', readPolicy = async () => (await request(route + 'policy')).data.data;
            const send = async (path, method, payload, expected = 200) => request(route + 'command', { method: 'POST', body: { commandId: randomUUID(), hubRevision: (await currentWorkspace()).revision, route: path, method, payload }, expected });
            const before = await readPolicy(), users = [{ userId: guid(person.id), policy: { authority: 'Denied' } }, { userId: guid(login.data.user.id), policy: { authority: 'Denied' } }];
            const preview = (await send('policy/preview', 'POST', { revision: before.revision, userIds: users.map(user => user.userId), users })).data.data;
            assert(preview.skippedAdmins.map(guid).includes(guid(login.data.user.id)));
            assert.equal((await readPolicy()).revision, before.revision, 'Permission preview must not mutate Queue state');
            const applied = (await send('policy', 'PUT', { revision: before.revision, users })).data.data;
            assert(applied.skippedAdmins.map(guid).includes(guid(login.data.user.id)));
            const effective = (await request(route + `users/${guid(person.id)}/effective-policy`)).data.data;
            assert.equal(effective.policy.authority, 'Denied'); assert.equal(effective.policy.source, 'override');
            await send(`users/${guid(login.data.user.id)}/policy`, 'PUT', { revision: (await readPolicy()).revision, policy: { authority: 'AnyAccessibleItem' } }, 403);
            await send(`users/${guid(person.id)}/policy`, 'PUT', { revision: (await readPolicy()).revision, policy: { authority: null } });
            const restored = (await request(route + `users/${guid(person.id)}/effective-policy`)).data.data;
            assert.equal(restored.policy.source, 'default'); assert.equal(restored.policy.authority, before.defaults.defaultAuthority);
            const ordinary = (await request('Users/AuthenticateByName', { method: 'POST', auth: { device: 'permission-test' }, body: { Username: 'suite-user', Pw: accountPassword } })).data;
            for (const auth of [{ token: ordinary.accessToken }, { token: apiKey.accessToken }]) {
                await request('DeletionQueue/admin/status', { auth, expected: [401,403] });
                await request('DeletionQueue/admin/approvals', { auth, expected: [401,403] });
                await request(route + 'policy', { auth, expected: [401,403] });
                if (has('automatic')) {
                    await request('AutomaticDeletionRequests/admin', { auth, expected: [401,403] });
                    await request('AutomaticDeletionRequests/admin/preview', { method: 'POST', body: {}, auth, expected: [401,403] });
                }
            }
            console.log('PASS installed Queue permission preview/apply/inheritance through Hub, administrator exclusion and non-admin/API-key rejection across Queue approvals/policy and automatic rules.');
        }
    }
    if (v1Flows) await testV1Flows(person);
    if (libraryFixture) await testLibraryAdditions();
    if(publicLinkUi){
        // Provision before optional administrator factor enrollment; later native
        // ownership overrides correctly require fresh Security proof.
        const member=(await nativeFixtureUser({Name:'link-ui-member'})).data;
        await request(`Users/${member.id}/Password`,{method:'POST',body:{CurrentPw:'',NewPw:'Disposable-Link-Only-4298!',ResetPassword:false},expected:204});
        await request(`Users/${member.id}/Policy`,{method:'POST',body:{...member.policy,isHidden:true},expected:204});
        if(publicLinkFactor){
            const login=(await request('Users/AuthenticateByName',{method:'POST',auth:{device:'link-factor-enrollment'},body:{Username:'link-ui-member',Pw:'Disposable-Link-Only-4298!'}})).data;
            const auth={token:login.accessToken,device:'link-factor-enrollment'};
            const enrollment=(await request('TwoFactorAuth/Setup/Totp',{method:'POST',auth,body:{}})).data;
            await request('TwoFactorAuth/Setup/Totp/Confirm',{method:'POST',auth,body:{Code:totp(enrollment.secretKey)}});
            assert.equal((await request('TwoFactorAuth/MyStatus',{auth})).data.totpEnabled,true,'Disposable link factor must be enabled before browser acceptance');
            linkFactor={secret:enrollment.secretKey,enrolledStep:Math.floor(Date.now()/30000)};
            enrollment.secretKey=enrollment.manualEntryKey=enrollment.qrCodeBase64=login.accessToken=null;
        }
    }
    if (option('security')) {
        const security = await request('AccountHub/api/v1/security/status'); assert.equal(security.data.data.compatibility, 'ok');
        await request('AccountHub/api/v1/security/users'); await request('AccountHub/api/v1/security/devices');
        const secret = 'disposable-signing-secret-' + randomUUID();
        const current = (await request('AccountHub/api/v1/workspace')).data.data;
        await request('AccountHub/api/v1/settings/secrets/security.webhookSecret', { method: 'POST', body: { commandId: randomUUID(), revision: current.revision, secret, confirmation: 'CONFIRM ACCOUNT HUB CHANGE' } });
        const raw = JSON.stringify({ event: 'lockout', timestamp: new Date().toISOString(), username: 'disposable-user', ip: '192.0.2.5' });
        const timestamp = Math.floor(Date.now() / 1000).toString(), signature = 'sha256=' + createHmac('sha256', secret).update(timestamp + '.' + raw).digest('hex');
        const headers = { 'X-2FA-Timestamp': timestamp, 'X-2FA-Signature': signature };
        await request('AccountHub/api/v1/security/jellyfin-security', { method: 'POST', auth: null, raw, headers, expected: 202 });
        const replay = await request('AccountHub/api/v1/security/jellyfin-security', { method: 'POST', auth: null, raw, headers, expected: 202 }); assert.equal(replay.data.data.status, 'duplicate');
        await request('AccountHub/api/v1/security/jellyfin-security', { method: 'POST', auth: null, raw: raw + ' ', headers, expected: 401 });
        console.log('PASS latest real Security ' + targets.security.version + ' status/users/devices, authorized signing-secret write, raw-body HMAC acceptance, replay dedupe and tamper rejection.');
        if (resetSession) await testResetSession();
        if (process.argv.includes('--proof')) {
            // Self-enrollment only, using public native APIs on this disposable account. Never
            // inspect Security's private records or persist factor material in Hub/evidence.
            const enrollment = (await request('TwoFactorAuth/Setup/Totp', { method: 'POST', body: {} })).data;
            assert(enrollment.secretKey, 'Native self-enrollment must return a one-time secret');
            await request('TwoFactorAuth/Setup/Totp/Confirm', { method: 'POST', body: { Code: totp(enrollment.secretKey) } });
            const revision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
            const payload = { userIds: [guid(person.id)], managedFields: { MaxActiveSessions: 4 } };
            await request('AccountHub/api/v1/commands/policy.apply', { method: 'POST', body: { commandId: randomUUID(), revision, payload, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', stepUp: { code: 'not-a-code' } }, expected: 403 });
            await wait(30000 - Date.now() % 30000 + 1000); // A fresh step: enrollment consumed the previous code.
            const code = totp(enrollment.secretKey), proofCommand = { commandId: randomUUID(), revision, payload, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', stepUp: { code } };
            await request('AccountHub/api/v1/commands/policy.apply', { method: 'POST', body: proofCommand });
            await request('AccountHub/api/v1/commands/policy.apply', { method: 'POST', body: proofCommand }); // completed retry must not consume proof again
            const nextRevision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
            await request('AccountHub/api/v1/commands/policy.apply', { method: 'POST', body: { ...proofCommand, commandId: randomUUID(), revision: nextRevision, payload: { ...payload, managedFields: { MaxActiveSessions: 5 } } }, expected: 403 });
            assert.equal((await request(`Users/${person.id}`)).data.policy.maxActiveSessions, 4);
            const pendingLogin = (await request('Users/AuthenticateByName', { method: 'POST', auth: { device: 'suite-pending-factor' }, body: { Username: 'suite-admin', Pw: password }, expected: 401 })).data;
            assert.equal(pendingLogin.twoFactorRequired, true); assert.equal(pendingLogin.accessToken, undefined, 'Native Security must not mint a new Jellyfin token before completing 2FA');
            if (extendedSecurity) {
                // Native self-service only: never inspect factor records or store the generated list.
                await request('AccountHub/api/v1/workspace', { auth: { device: 'suite-pending-factor', token: pendingLogin.challengeToken }, expected: [401, 403] });
                await wait(30000 - Date.now() % 30000 + 1000);
                const recovery = (await request('TwoFactorAuth/RecoveryCodes/Generate', { method: 'POST', body: { Code: totp(enrollment.secretKey) } })).data;
                let verifiedDevice;
                try {
                    assert(Array.isArray(recovery.codes) && recovery.codes.length >= 4, 'Native self-service recovery generation must return a one-time list');
                    const before = (await request('TwoFactorAuth/RecoveryCodes/Status')).data.remaining;
                    const current = (await request('AccountHub/api/v1/workspace')).data.data.revision;
                    const recoveryCommand = { commandId: randomUUID(), revision: current, payload: { userIds: [guid(person.id)], managedFields: { MaxActiveSessions: 6 } }, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', stepUp: { code: recovery.codes[0] } };
                    await request('AccountHub/api/v1/commands/policy.apply', { method: 'POST', body: recoveryCommand });
                    await request('AccountHub/api/v1/commands/policy.apply', { method: 'POST', body: recoveryCommand });
                    const consumedRevision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
                    await request('AccountHub/api/v1/commands/policy.apply', { method: 'POST', body: { ...recoveryCommand, commandId: randomUUID(), revision: consumedRevision }, expected: 403 });
                    recoveryCommand.stepUp.code = null;
                    assert.equal((await request('TwoFactorAuth/RecoveryCodes/Status')).data.remaining, before - 1, 'Recovery proof is consumed once; completed retry must not consume again');
                    // Challenges freeze their allowed factors at issuance. Recovery codes were
                    // generated after the earlier challenge, so obtain a fresh native challenge.
                    const recoveryPending = (await request('Users/AuthenticateByName', { method: 'POST', auth: { device: 'suite-pending-factor' }, body: { Username: 'suite-admin', Pw: password }, expected: 401 })).data;
                    try {
                        verifiedDevice = (await request('TwoFactorAuth/Verify', { method: 'POST', auth: { device: 'suite-pending-factor' }, body: { ChallengeToken: recoveryPending.challengeToken, Method: 'recovery', Code: recovery.codes[1], TrustDevice: true } })).data;
                    } finally { recoveryPending.challengeToken = recoveryPending.challengePageUrl = recoveryPending.enrollmentPageUrl = null; }
                    assert(verifiedDevice.accessToken, 'Successful native challenge completion must return a native session');
                    const deviceAuth = { device: 'suite-pending-factor', token: verifiedDevice.accessToken };
                    await request('Users/Me', { auth: deviceAuth });
                    const devices = (await request('TwoFactorAuth/Devices')).data;
                    const trusted = devices.find(device => device.deviceId === 'suite-pending-factor');
                    assert(trusted, 'Native challenge trust must create a public trusted-device record');
                    const userId = guid(login.data.user.id), deviceRevision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
                    await request(`AccountHub/api/v1/security/devices/${userId}/${encodeURIComponent(trusted.id)}/revoke`, { method: 'POST', body: { commandId: randomUUID(), revision: deviceRevision, confirmation: 'REVOKE TRUST ' + userId, stepUp: { code: recovery.codes[2] } } });
                    assert(!(await request('TwoFactorAuth/Devices')).data.some(device => device.id === trusted.id), 'Revoked trust must disappear from native public state');
                    // Revoking trust is not a promise to revoke every live Jellyfin session.
                    // Session termination is the separately confirmed native Force Logout action.
                    // Target the disposable non-admin: Hub deliberately forbids self-force-logout.
                    await request('Users/Me', { auth: { token: userLogin.accessToken } });
                    const logoutTarget = guid(person.id);
                    const logoutRevision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
                    await request(`AccountHub/api/v1/security/users/${logoutTarget}/force-logout`, { method: 'POST', body: { commandId: randomUUID(), revision: logoutRevision, confirmation: 'FORCE LOGOUT ' + logoutTarget, stepUp: { code: recovery.codes[3] } } });
                    await request('Users/Me', { auth: { token: userLogin.accessToken }, expected: [401, 403] });
                    console.log('PASS real native recovery-code proof/consumption/replay rejection, pending challenge rejection, native challenge completion, trusted-device revocation and native session termination.');
                } finally {
                    recovery.codes?.fill(null);
                    if (verifiedDevice) verifiedDevice.accessToken = verifiedDevice.deviceToken = null;
                }
            }
            await request('AccountHub/api/v1/workspace', { auth: null, expected: [401, 403] });
            await request('AccountHub/api/v1/security/jellyfin-security', { method: 'POST', auth: null, raw, headers, expected: 202 });
            pendingLogin.challengeToken = pendingLogin.challengePageUrl = pendingLogin.enrollmentPageUrl = null;
            enrollment.secretKey = enrollment.manualEntryKey = enrollment.qrCodeBase64 = null; proofCommand.stepUp.code = null;
            console.log('PASS native disposable self-enrollment, invalid-proof denial, fresh real TOTP authorization, completed-command retry and proof replay rejection with no second effect.');
            console.log('PASS real native login requires 2FA without minting a Jellyfin token; anonymous Hub reads remain denied while token-free signed ingress works.');
        }
    }
    }
    if (!has('hub') && libraryFixture) await testLibraryAdditions();
    if (!has('hub') && has('discord') && option('security') && process.argv.includes('--proof')) {
        const enrollment = (await request('TwoFactorAuth/Setup/Totp', { method: 'POST', body: {} })).data;
        await request('TwoFactorAuth/Setup/Totp/Confirm', { method: 'POST', body: { Code: totp(enrollment.secretKey) } });
        const bridge = (await request('DiscordBridge/admin/v1/settings')).data.data;
        const command = { revision: bridge.revision, settings: bridge.settings, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', stepUp: { code: 'invalid' } };
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: command, expected: 403 });
        await wait(30000 - Date.now() % 30000 + 1000);
        command.stepUp.code = totp(enrollment.secretKey);
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: command });
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: { ...command, revision: command.revision + 1 }, expected: 403 });
        assert.equal((await request('DiscordBridge/admin/v1/settings')).data.data.revision, command.revision + 1);
        enrollment.secretKey = enrollment.manualEntryKey = enrollment.qrCodeBase64 = command.stepUp.code = null;
        console.log('PASS standalone Discord configuration uses real fresh native Security proof and rejects invalid/replayed proof without any Hub.');
    }
    if (databaseSafety) {
        const identities = async () => (await request('Users')).data.map(user => ({ id: user.id, name: user.name, policy: user.policy })).sort((a, b) => a.id.localeCompare(b.id));
        const before = await identities();
        const revision = has('hub') ? (await request('AccountHub/api/v1/workspace')).data.data.revision : null;
        docker('kill', '--signal=KILL', name);
        docker('start', name);
        // Docker may allocate a different ephemeral host port when a container starts again.
        base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
        await until(async () => { try { return (await request('System/Info/Public', { auth: null, expected: null })).status === 200; } catch { return false; } }, 'native restart after abrupt termination');
        if (has('hub')) await until(async () => { try { return (await request('AccountHub/api/v1/workspace', { expected: null })).status === 200; } catch { return false; } }, 'Hub database recovery after restart');
        assert.deepEqual(await identities(), before, 'Native identities and policies must survive abrupt restart unchanged');
        if (has('hub')) assert.equal((await request('AccountHub/api/v1/workspace')).data.data.revision, revision, 'Hub revision must survive restart');
        docker('stop', '--time=30', name);
        const checker = await readFile(new URL('./check-fixture-databases.py', import.meta.url), 'utf8');
        const result = JSON.parse(docker('run', '--rm', '--network=none', '--read-only', '--tmpfs', '/tmp:rw,noexec,nosuid,size=512m', '--mount', `type=bind,source=${join(root, 'config')},target=/fixture,readonly`, 'python:alpine', 'python', '-c', checker));
        await writeFile(join(root, 'database-safety.json'), JSON.stringify(result, null, 2));
        console.log('PASS abrupt restart preserves native identities/policies and Hub revision; read-only SQLite integrity, foreign keys and native ID uniqueness:', result.length, 'databases.');
    }
    if (storageRecovery) {
        docker('stop', '--time=30', name);
        const move=(source,destination)=>moveFixtureState(join(root,'config'),source,destination,{container:name,docker});
        const database = join(root, 'config', 'plugins', 'configurations', 'AccountHub', 'account-hub.db');
        const moved = [];
        const exists = async path => { try { await stat(path); return true; } catch (error) { if (error.code === 'ENOENT') return false; throw error; } };
        try {
            for (const suffix of ['', '-wal', '-shm', '-journal']) if (await exists(database + suffix)) { await move(database + suffix, database + suffix + '.preserved'); moved.push(suffix); }
            assert(moved.includes(''), 'Expected an initialized owned Hub database');
            docker('start', name); base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
            await until(async () => { try { return (await request('System/Info/Public', { auth: null, expected: null })).status === 200; } catch { return false; } }, 'native startup with missing Hub database');
            const health = await request('AccountHub/api/v1/health', { expected: 503 });
            assert.equal(health.data.data.ready, false); assert.equal(health.data.data.errorCode, 'storage_recovery_required');
            assert(!await exists(database), 'No module may silently create a replacement Hub database');
        } finally {
            docker('stop', '--time=30', name);
            for (const suffix of ['', '-wal', '-shm', '-journal']) if (await exists(database + suffix)) await move(database + suffix, database + suffix + '.unexpected');
            for (const suffix of moved) await move(database + suffix + '.preserved', database + suffix);
        }
        docker('start', name); base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
        await until(async () => { try { return (await request('AccountHub/api/v1/health', { expected: null })).status === 200; } catch { return false; } }, 'Hub recovery after original database restore');
        console.log('PASS missing Hub database leaves native Jellyfin available, refuses replacement state, and recovers after original fixture restoration.');
        if (has('automatic')) {
            const endpoint = 'AutomaticDeletionRequests/admin';
            const original = (await request(endpoint)).data.configuration;
            const saved = (await request(endpoint, { method: 'PUT', body: { configuration: original, confirmAutomation: false } })).data;
            const file = join(root, 'config', 'data', 'automatic-deletion-requests', 'rules.json');
            assert(await exists(file + '.installed'), 'Rule state must retain its installation marker');
            docker('stop', '--time=30', name);
            await move(file, file + '.preserved');
            try {
                docker('start', name); base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
                await until(async () => { try { return (await request('System/Info/Public', { auth: null, expected: null })).status === 200; } catch { return false; } }, 'native startup with missing rule state');
                await request(endpoint, { expected: 409 });
                await request(endpoint, { method: 'PUT', body: { configuration: saved, confirmAutomation: false }, expected: 409 });
                assert(!await exists(file), 'Missing rule state must not silently reset configuration or receipts');
            } finally {
                docker('stop', '--time=30', name);
                if (await exists(file)) await move(file, file + '.unexpected');
                await move(file + '.preserved', file);
            }
            docker('start', name); base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
            await until(async () => { try { return (await request(endpoint, { expected: null })).status === 200; } catch { return false; } }, 'rule state recovery after original restoration');
            assert.deepEqual((await request(endpoint)).data.configuration, saved);
            console.log('PASS missing automatic-rule state fails closed across restart, preserves the installation marker and recovers only after original fixture restoration.');
        }
    }
    if (interactiveDiscord) {
        liveStage = 'human interactive commands';
        assert(has('hub') && (humanAccounts||has('syncplay')), 'Interactive acceptance requires Hub; watch-party checks additionally need Private SyncPlay');
        if (discordTunnel) {
            liveStage='human tunnel startup';
            temporaryTunnel = await startDiscordTunnel(name, (...args) => {
                const result = spawnSync('docker', args, { encoding: 'utf8', timeout: 180000, maxBuffer: 1024 * 1024 });
                assert.equal(result.status, 0, 'Disposable tunnel Docker operation failed (details withheld)');
                return (result.stdout + (args[0] === 'logs' ? result.stderr : '')).trim();
            });
            process.env.DISCORD_TEST_PUBLIC_BASE_URL = temporaryTunnel.origin + '/suite';
        }
        const publicBaseUrl = process.env.DISCORD_TEST_PUBLIC_BASE_URL;
        liveStage='human public HTTPS reachability';
        const nativeInfo = (await request('System/Info/Public', { auth: null })).data;
        await until(async () => {
            try {
                if (temporaryTunnel) {
                    const response = probeDiscordTunnel(temporaryTunnel.origin, (...args) => {
                        const result = spawnSync('docker', args, { encoding: 'utf8', timeout: 30000, maxBuffer: 128 * 1024 });
                        assert.equal(result.status, 0, 'Public tunnel probe failed (details withheld)');
                        return result.stdout.trim();
                    });
                    return response.status === 200 && normal(JSON.parse(response.text)).id === nativeInfo.id;
                }
                const response = await fetch(publicBaseUrl + '/System/Info/Public', { redirect: 'error', signal: AbortSignal.timeout(10000) });
                return response.ok && normal(await response.json()).id === nativeInfo.id;
            } catch { return false; }
        }, 'trusted HTTPS tunnel reaches this disposable server', 300);
        await request('AccountHub/api/v1/commands/settings.public-url', { method: 'POST', body: { commandId: randomUUID(), revision: (await request('AccountHub/api/v1/workspace')).data.data.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', payload: { publicBaseUrl } } });
        if(humanInvitations){
            assert(has('syncplay')&&has('notifications')&&interactiveAccount,'Human invitation acceptance requires Notifications, the native party provider and owned account');
            const template=randomUUID();
            await request('AccountHub/api/v1/group-policy/commands/group.copy-user',{method:'POST',body:{commandId:randomUUID(),revision:(await request('AccountHub/api/v1/workspace')).data.data.revision,confirmation:'CONFIRM ACCOUNT HUB CHANGE',payload:{id:template,sourceUserId:interactiveAccount.id,name:'Disposable Discord signup access'}}});
            await request('AccountHub/api/v1/invite-defaults',{method:'POST',body:{commandId:randomUUID(),revision:(await request('AccountHub/api/v1/workspace')).data.data.revision,confirmation:'CONFIRM ACCOUNT HUB CHANGE',settings:{defaultGroupId:template,discordCreationEnabled:true,allowedCreators:[guid(interactiveAccount.id)],discordExpiryMinutes:60,discordDefaultUses:1,discordMaxUses:4,discordMaxExpiryMinutes:120,dailyLimit:5}}});
            const library=(await request('Notifications/api/v1/settings/notifications/library')).data.data.settings;
            await request('Notifications/api/v1/settings/notifications/library',{method:'PUT',body:{...library,enabled:false}});
        }
        liveStage='human bot public URL configuration';
        const bridgeConfig = (await request('DiscordBridge/admin/v1/settings')).data.data;
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: { revision: bridgeConfig.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE',
            settings: { ...bridgeConfig.settings, publicServerUrl: publicBaseUrl, enableSelfServiceCommands: true, enableWatchPartyCommands: true,
                ...(humanInvitations?{inviteDefaultUses:1,inviteMaxUses:4,inviteDefaultExpiryMinutes:60,inviteMaxExpiryMinutes:120,watchPartyGuests:false,watchPartyUses:2,libraryAnnouncementsEnabled:false}:{}),
                permissions: { ...bridgeConfig.settings.permissions, defaults: { ...bridgeConfig.settings.permissions.defaults, 'party.share.channel': true },users:{...bridgeConfig.settings.permissions.users,...(humanInvitations?{[guid(interactiveAccount.id)]:{'invite.create':true}}:{})} } } } });
        await until(async () => (await request('DiscordBridge/admin/v1/settings')).data.data.connected, 'interactive bot configuration reconnect');
        if(humanWindow&&discordTunnel){
            liveStage='human trusted-proxy authorization';
            // Native middleware, not a plugin bypass, must reconstruct HTTPS
            // from this explicitly trusted loopback proxy before we say ready.
            const proxyCheck=probeTrustedProxyAuth(name,new URL(publicBaseUrl).origin,interactiveAccount.token);
            console.log('Native trusted-proxy authorization status codes:',JSON.stringify(proxyCheck));
            assert.equal(proxyCheck.sameOrigin,400,'Signed-in HTTPS proxy request must reach challenge validation');
            assert([401,403].includes(proxyCheck.hostileOrigin),'Hostile browser origins must remain denied');
            console.log('PASS trusted-proxy HTTPS link-review authentication; mismatched browser origins remain denied.');
        }
        console.log('Disposable HTTPS test endpoint:', publicBaseUrl);
        liveStage='human interactive commands';
        if (!discordTunnel) console.log('Disposable reverse proxy must forward to this fixture endpoint:', base);
        if(humanInvitations){
            docker('exec',name,'/usr/lib/jellyfin-ffmpeg/ffmpeg','-hide_banner','-loglevel','error','-f','lavfi','-i','testsrc2=size=640x360:rate=24','-f','lavfi','-i','sine=frequency=440','-t','300','-c:v','libx264','-preset','ultrafast','-g','48','-pix_fmt','yuv420p','-c:a','aac','/media/movies/Human watch party (2026).mp4');
            await request('Library/Refresh',{method:'POST',expected:204});
            await until(async()=>(await request('Items?Recursive=true&IncludeItemTypes=Movie')).data.items.some(item=>item.name.includes('Human watch party')),'synthetic native watch-party media scan');
            interactiveReadiness={status:'ready',scope:'native-signup-and-watchparty-human-acceptance',publicBaseUrl,loginUrl:publicBaseUrl+'/web/index.html#/login',username:interactiveAccount.username,password:interactiveAccount.password,botUsername:bridgeConfig.botUsername,botId:bridgeConfig.botId,expiresAt:new Date(Date.now()+humanWaitMinutes*60000).toISOString()};
            await writeFile(resolve(readyFile),JSON.stringify(interactiveReadiness,null,2),{flag:'wx'});readyCreated=true;
            console.log('READY for real human Discord invitation commands. In the dedicated guild use /help, /link and confirm the disposable account, /status; /invite uses:5 must be rejected; /invite uses:2 expires-in-minutes:60 must create a signup link. Redeem it twice using distinct test usernames, verify a third use is denied. Start Human watch party on a real native Jellyfin device, run /watchparty in the approved test channel and /watchparty user:@user for the consenting configured recipient. Inspect privacy and redeem both native links. Do not post passwords, bearer links or proof codes in logs/chat. No interactions are forged or replayed.');
        }else if(humanAccounts){
            assert(interactiveAccount,'Disposable human account was not initialized');
            if(readyFile){interactiveReadiness={status:'ready',publicBaseUrl,loginUrl:publicBaseUrl+'/web/index.html#/login',username:interactiveAccount.username,password:interactiveAccount.password,botUsername:bridgeConfig.botUsername,botId:bridgeConfig.botId,serverReference:bridgeConfig.instanceCode,expiresAt:new Date(Date.now()+humanWaitMinutes*60000).toISOString()};await writeFile(resolve(readyFile),JSON.stringify(interactiveReadiness,null,2),{flag:'wx'});readyCreated=true;}
            console.log('READY for human account acceptance. Use the configured consenting Discord account in the test guild/channel: /link, open the private link, sign in to the disposable suite-user account and Review/Confirm; /status; /reset; open the bot DM, choose a NEW test-only password, then sign in with it. Never paste bearer links or chosen passwords into logs/chat. This fixture expires in '+humanWaitMinutes+' minutes.');
        }else console.log('ACTION REQUIRED in the disposable Discord guild: execute /help, /status, /link, complete verified linking to this disposable Jellyfin server, then /watchparty twice: once with @user for the configured consenting test recipient and once with no options in the approved test channel. Use no other destinations. Inspect both messages privately. Do not paste bearer links, passwords or proof codes into logs/chat. No commands are forged or automatically replayed.');
        let linkedVerified=false,nextAudit=0;const humanStarted=Date.now();
        await until(async () => {
            const counters = (await request('DiscordBridge/admin/v1/settings')).data.data.interactions || {};
            if(humanInvitations){
                if(!invitationInteractionsCompleted(counters)||Date.now()<nextAudit)return false;nextAudit=Date.now()+3000;
                const self=(await request('AccountHub/api/v1/self',{auth:{token:interactiveAccount.token}})).data.data;
                if(!self.discordLinked||self.discordId!==process.env.DISCORD_TEST_RECIPIENT_ID)return false;
                const invites=(await request('AccountHub/api/v1/invites?limit=100')).data.data.items;
                return invitationRedemptionCompleted(invites,interactiveAccount.id,humanStarted);
            }
            if(!humanAccounts)return interactionsCompleted(counters);
            if(!accountInteractionsCompleted(counters)||Date.now()<nextAudit)return false;nextAudit=Date.now()+3000;
            if(!linkedVerified){
                // Inspect only owned plugin link state, read-only, and return one
                // Boolean. Never read native factors, message bodies or secrets.
                // SQLite may need a new shared-memory file to read WAL. Keep
                // original storage read-only and let SQLite open a private tmpfs
                // snapshot instead; never checkpoint or modify live fixture data.
                const script="import sqlite3,sys,tempfile,shutil,pathlib; t=tempfile.TemporaryDirectory(); src=pathlib.Path('/fixture/account-hub.db'); dst=pathlib.Path(t.name)/'snapshot.db'; shutil.copyfile(src,dst); wal=pathlib.Path(str(src)+'-wal'); shutil.copyfile(wal,str(dst)+'-wal') if wal.exists() else None; c=sqlite3.connect(dst); print(c.execute('SELECT COUNT(*) FROM linked_accounts WHERE JellyfinUserId=? AND DiscordUserId=?',(sys.argv[1],sys.argv[2])).fetchone()[0]); c.close(); t.cleanup()";
                linkedVerified=docker('run','--rm','--network=none','--read-only','--tmpfs','/tmp:rw,noexec,nosuid,size=32m','--mount',`type=bind,source=${join(root,'config','plugins','configurations','AccountHub')},target=/fixture,readonly`,'python:alpine','python','-c',script,interactiveAccount.id.replaceAll('-',''),process.env.DISCORD_TEST_RECIPIENT_ID)==='1';
                if(!linkedVerified)return false;
            }
            const rows=(await request('AccountHub/api/v1/audit/query',{method:'POST',body:{eventType:'password.reset',limit:50}})).data.data.items;
            if(!accountResetCompleted(rows,interactiveAccount.id,humanStarted))return false;
            await request('Users/Me',{auth:{token:interactiveAccount.token},expected:[401,403]});
            await request('Users/AuthenticateByName',{method:'POST',auth:{device:'human-old-password-check'},body:{Username:interactiveAccount.username,Pw:interactiveAccount.password},expected:401});
            return true;
        }, humanInvitations?'actual invitation handlers, native verified link, bounded signup options and two completed native signups':humanAccounts?'human /link confirmation, private /reset delivery and completed native password reset':'human-invoked Discord self-service and selected-destination watch-party delivery',humanWindow?humanWaitMinutes*60:600);
        if(humanInvitations){
            interactiveReadiness.status='signup-verified-awaiting-party-and-privacy-confirmation';await writeFile(resolve(readyFile),JSON.stringify(interactiveReadiness,null,2));
            await until(async()=>{try{return invitationHumanConfirmed(JSON.parse(await readFile(resolve(readyFile)+'.complete','utf8')));}catch(error){if(error.code==='ENOENT'||error instanceof SyntaxError)return false;throw error;}},'explicit human confirmation of third-use denial, native channel/DM redemption and recipient privacy',Math.max(1,Math.ceil((Date.parse(interactiveReadiness.expiresAt)-Date.now())/1000)));
            console.log('PASS real human invitation command handlers, exact native Discord link, owner-bounded two-use/60-minute signup issuance and two native signups; third-use denial, both native party redemptions and recipient privacy are human-confirmed, not automated client certification.');
        }
        if(humanAccounts){
            interactiveReadiness.status='reset-verified-awaiting-signin-confirmation';await writeFile(resolve(readyFile),JSON.stringify(interactiveReadiness,null,2));
            console.log('Human reset and old-session revocation verified. Sign in with your NEW password, then tell the operator you have finished. The fixture remains available until the original expiry.');
            await until(async()=>{try{return(await readFile(resolve(readyFile)+'.complete','utf8')).trim()==='confirmed';}catch(error){if(error.code==='ENOENT')return false;throw error;}},'human confirmation of new-password native sign-in',Math.max(1,Math.ceil((Date.parse(interactiveReadiness.expiresAt)-Date.now())/1000)));
        }
        const bot = await discordApi('users/@me');
        const recent = await discordApi(`channels/${channelId}/messages?limit=100`);
        for (const message of recent) if (ownedInvitation(message, bot.id, liveStarted, publicBaseUrl)) liveMessages.push(message.id);
        if(!humanInvitations)console.log(humanAccounts?'PASS human /link confirmation for the configured consenting Discord identity, /status, private /reset delivery, completed native password reset audit, old-password denial and prior native-session revocation. New-password sign-in remains human-confirmed; no chosen password was collected.':'PASS real gateway command handlers acknowledged self-service responses and selected-destination invitation delivery; metrics retain no actor, channel, link or message payload.');
    }
    if(databaseSafety&&!storageRecovery&&(accountResponsive||themeCheck||notificationStudio||diagnosticsUi||publicOnboardingUi||publicLinkUi)){
        // The offline SQLite checker intentionally stops the fixture.
        // Browser acceptance needs a fresh native startup afterward.
        docker('start',name);base=`http://127.0.0.1:${docker('port',name,'8096/tcp').split(':').at(-1)}/suite`;
        await until(async()=>{try{return(await request('System/Info/Public',{auth:null,expected:null})).status===200;}catch{return false;}},'native startup before browser acceptance');
        if(has('hub'))await until(async()=>{try{return(await request('AccountHub/api/v1/workspace',{expected:null})).status===200;}catch{return false;}},'account startup before browser acceptance');
    }
    if(publicLinkUi){
        // Only this newly created fixture's challenge table is seeded. This is
        // synthetic issuance, NOT a Discord command or human acceptance pass.
        const challenges=Array.from({length:3},(_,index)=>({token:randomBytes(32).toString('base64url'),discordId:String(123456789012345670n+BigInt(index)),expires:Date.now()+(index===2?-1000:300000)}));
        const input=join(root,'synthetic-link-fixture.json');await writeFile(input,JSON.stringify({challenges,...(linkFactor?{factor:linkFactor}:{})}),{flag:'wx'});
        const seed='import json,sqlite3,hashlib\nf=json.load(open("/input/synthetic-link-fixture.json"))\nc=sqlite3.connect("/data/account-hub.db",timeout=30)\nfor row in f["challenges"]: c.execute("INSERT INTO discord_challenges (TokenHash,DiscordId,ExpiresAtUnixMs,ConsumedBy) VALUES (?,?,?,NULL)",(hashlib.sha256(row["token"].encode()).hexdigest(),row["discordId"],row["expires"]))\nc.commit()\nc.close()';
        try{
            docker('run','--rm','--network=none','--read-only','--mount',`type=bind,source=${root},target=/input,readonly`,'--mount',`type=bind,source=${join(root,'config','plugins','configurations','AccountHub')},target=/data`,'python:alpine','python','-c',seed);
            await mkdir(join(root,'link-screenshots'),{recursive:true});
            console.log(docker('run','--rm','--network','container:'+name,'--mount',`type=bind,source=${resolve('scripts')},target=/checks,readonly`,'--mount',`type=bind,source=${input},target=/synthetic-link-fixture.json,readonly`,'--mount',`type=bind,source=${join(root,'link-screenshots')},target=/evidence`,'--env','ADMIN_SCREENSHOTS=/evidence','--env','DISPOSABLE_THEME_TEST=true','--env','THEME_TEST_BASE_URL=http://127.0.0.1:8096/suite','mcr.microsoft.com/playwright:v1.64.1-noble@sha256:47b510dd9230bf0b9bfc9b20102241e577ea7a99334ab4c3ca19f19770e9b8ea','/bin/sh','/checks/run-public-link-acceptance.sh'));
        }finally{await rm(input,{force:true});if(linkFactor)linkFactor.secret=null;}
    }
    if(publicOnboardingUi){
        await mkdir(join(root,'signup-screenshots'),{recursive:true});
        console.log(docker('run','--rm','--network','container:'+name,'--mount',`type=bind,source=${resolve('scripts')},target=/checks,readonly`,'--mount',`type=bind,source=${join(root,'signup-screenshots')},target=/evidence`,'--env','ADMIN_SCREENSHOTS=/evidence','--env','DISPOSABLE_THEME_TEST=true','--env','THEME_TEST_BASE_URL=http://127.0.0.1:8096/suite','mcr.microsoft.com/playwright:v1.64.1-noble@sha256:47b510dd9230bf0b9bfc9b20102241e577ea7a99334ab4c3ca19f19770e9b8ea','/bin/sh','/checks/run-public-onboarding-acceptance.sh'));
    }
    if(diagnosticsUi){
        const member=(await nativeFixtureUser({Name:'diagnostics-ui-member'})).data;
        await request(`Users/${member.id}/Password`,{method:'POST',body:{CurrentPw:'',NewPw:'Disposable-Diagnostics-Only-6328!',ResetPassword:false},expected:204});
        await request(`Users/${member.id}/Policy`,{method:'POST',body:{...member.policy,isHidden:true},expected:204});
        await mkdir(join(root,'diagnostics-screenshots'),{recursive:true});
        const startPage=has('notifications')?'Notifications':{hub:'AccountHub',discord:'DiscordBridge',deletion:'DeletionQueueAdmin',syncplay:'privateSyncPlay',automatic:'AutomaticDeletionRequests'}[selected[0]];
        const output=docker('run','--rm','--network','container:'+name,'--mount',`type=bind,source=${resolve('scripts')},target=/checks,readonly`,'--mount',`type=bind,source=${join(root,'diagnostics-screenshots')},target=/evidence`,'--env','ADMIN_SCREENSHOTS=/evidence','--env','DISPOSABLE_THEME_TEST=true','--env','THEME_TEST_BASE_URL=http://127.0.0.1:8096/suite','--env','DIAGNOSTICS_START_PAGE='+startPage,'mcr.microsoft.com/playwright:v1.64.1-noble@sha256:47b510dd9230bf0b9bfc9b20102241e577ea7a99334ab4c3ca19f19770e9b8ea','/bin/sh','/checks/run-diagnostics-acceptance.sh');
        console.log(output);
    }
    if(accountResponsive){
        await mkdir(join(root,'account-screenshots'),{recursive:true});
        const output=docker('run','--rm','--network','container:'+name,'--mount',`type=bind,source=${resolve('scripts')},target=/checks,readonly`,'--mount',`type=bind,source=${join(root,'account-screenshots')},target=/evidence`,'--env','ADMIN_SCREENSHOTS=/evidence','--env','DISPOSABLE_THEME_TEST=true','--env','THEME_TEST_BASE_URL=http://127.0.0.1:8096/suite','mcr.microsoft.com/playwright:v1.64.1-noble@sha256:47b510dd9230bf0b9bfc9b20102241e577ea7a99334ab4c3ca19f19770e9b8ea','/bin/sh','/checks/run-account-responsive.sh');
        const result=JSON.parse(output.split(/\r?\n/).find(line=>line.startsWith('{"checks":'))||'{}');
        assert.equal(result.checks?.length,18);assert(result.checks.every(check=>check.passed));
        await writeFile(join(root,'account-responsive-results.json'),JSON.stringify(result,null,2));
        console.log('PASS 18 native dark/light Discord, linking and reset responsive checks at 320/390/1280px, native controls and custom theme overrides.');
    }
    if (themeCheck) {
        await mkdir(join(root, 'admin-screenshots'), { recursive: true });
        // No published port or host alias is needed: the browser shares only
        // this newly owned disposable server's network namespace.
        const output = docker('run', '--rm', '--network', 'container:' + name,
            '--mount', `type=bind,source=${resolve('scripts')},target=/checks,readonly`,
            '--mount', `type=bind,source=${join(root, 'admin-screenshots')},target=/evidence`,
            '--env', 'ADMIN_SCREENSHOTS=/evidence',
            '--env', 'SUITE_TEST_TARGETS_JSON=' + await readFile(process.env.SUITE_TEST_TARGETS, 'utf8'),
            '--env', 'SUITE_TEST_RUN_ID=' + process.env.SUITE_TEST_RUN_ID,
            '--env', 'THEME_TEST_AUTOMATIC=' + has('automatic'),
            '--env', 'DISPOSABLE_THEME_TEST=true', '--env', 'THEME_TEST_BASE_URL=http://127.0.0.1:8096/suite',
            'mcr.microsoft.com/playwright:v1.64.1-noble@sha256:47b510dd9230bf0b9bfc9b20102241e577ea7a99334ab4c3ca19f19770e9b8ea',
            '/bin/sh', '/checks/run-theme-acceptance.sh');
        const line = output.split(/\r?\n/).find(line => line.startsWith('{"checks":'));
        assert(line, 'Browser check result missing');
        const result = JSON.parse(line);
        assert(result.checks.length === 30 && result.checks.every(item => item.passed) && result.syncplaySettings === true, 'Browser coverage incomplete');
        if (has('notifications')) assert(result.notificationPalettes?.length === 4 && result.notificationPalettes.every(item => item.nativePalette && item.skinOverride), 'Native notification palette coverage incomplete');
        if (has('notifications')) assert(result.notificationCategories?.length === 4 && result.notificationCategories.every(item => item.watchlisted && item.favorited && item.personalImportance && item.nativeKeyboard), 'Personal notification category coverage incomplete');
        if (has('notifications')) assert(result.notificationSwipes?.length === 4 && result.notificationSwipes.every(item => item.rightRead && item.leftHide && item.confirmRequired && item.consentThenDismiss && item.skinOverride), 'Notification swipe coverage incomplete');
        await writeFile(join(root, 'theme-results.json'), JSON.stringify(result, null, 2));
        console.log('PASS 30 account-focused native-theme/workspace checks and keyboard/custom-theme override at three widths.');
    }
    if(notificationStudio){
        await mkdir(join(root,'studio-screenshots'),{recursive:true});
        const output=docker('run','--rm','--network','container:'+name,'--mount',`type=bind,source=${resolve('scripts')},target=/checks,readonly`,'--mount',`type=bind,source=${join(root,'studio-screenshots')},target=/evidence`,'--env','ADMIN_SCREENSHOTS=/evidence','--env','DISPOSABLE_THEME_TEST=true','--env','THEME_TEST_BASE_URL=http://127.0.0.1:8096/suite','mcr.microsoft.com/playwright:v1.64.1-noble@sha256:47b510dd9230bf0b9bfc9b20102241e577ea7a99334ab4c3ca19f19770e9b8ea','/bin/sh','/checks/run-notification-studio.sh');
        const result=JSON.parse(output.split(/\r?\n/).find(line=>line.startsWith('{"checks":'))||'{}');
        assert.equal(result.checks?.length,6);assert(result.persisted&&result.liveBell&&result.noSends);
        await writeFile(join(root,'studio-results.json'),JSON.stringify(result,null,2));
        console.log('PASS standalone-capable notification studio: six native theme/width cases, drag/keyboard/fonts, saved defaults/layout, live bell and no preview publication.');
    }
    const nativeLog=docker('logs',name);
    assert(!(nativeLog.includes('StatusCode cannot be set because the response has already started.')&&nativeLog.includes('SuiteDiagnostics.DiagnosticsResultFilter')),'Diagnostics must not rewrite an already-started native response');
    console.log('PASS combination:', selected.join('+') || 'none', option('security') ? '+ native Security' : '(no native Security)', '— independent startup/configuration, optional-dependency health and installed-feature checks.');
    console.log('Server platform:', platform, '(ARM execution on an x64 Docker host uses emulation; this is not hardware/performance certification).');
    if (!liveDiscord) console.log('Disposable evidence:', root);
    else console.log('Live fixture passed; credential-bearing disposable state is removed during cleanup.');
} catch (error) {
    if (started && !liveDiscord) { await writeFile(join(root, 'server.log'), docker('logs', name)); console.error('Server log:', join(root, 'server.log')); }
    if (liveDiscord) {
        if(readyFile&&!readyCreated){try{await writeFile(resolve(readyFile),JSON.stringify({status:'failed',stage:liveStage}),{flag:'wx'});}catch{}}
        try {
            const health = (await request('DiscordBridge/admin/v1/settings')).data.data.libraryHealth;
            console.error('Sanitized library queue:', JSON.stringify(Object.fromEntries(['retained', 'pending', 'uncertainDiscord', 'failed'].map(key => [key, Number(health[key])]))));
        } catch { /* No raw diagnostics or credentials. */ }
        throw Error('Live Discord fixture failed at ' + liveStage + '; sensitive details withheld.');
    }
    throw error;
} finally {
    if(readyCreated){await rm(resolve(readyFile),{force:true});await rm(resolve(readyFile)+'.complete',{force:true});}
    try { temporaryTunnel?.stop(); }
    catch { console.error('Temporary tunnel cleanup could not be verified; remove the owned jellyfin-suite-tunnel container manually.'); process.exitCode = 1; }
    try { if (started && !keep) docker('rm', '-f', name); }
    finally {
        if (liveDiscord) {
            try {
                if (interactiveDiscord) {
                    try {
                        const bot = await discordApi('users/@me');
                        const recent = await discordApi(`channels/${channelId}/messages?limit=100`);
                        for (const message of recent) if (ownedInvitation(message, bot.id, liveStarted, process.env.DISCORD_TEST_PUBLIC_BASE_URL)) liveMessages.push(message.id);
                        const recipient = process.env.DISCORD_TEST_RECIPIENT_ID;
                        if (/^\d{1,20}$/.test(recipient || '')) {
                            const dm = await discordApi('users/@me/channels', 'POST', { recipient_id: recipient });
                            const messages = await discordApi(`channels/${dm.id}/messages?limit=100`);
                            for (const message of messages) if (ownedInvitation(message, bot.id, liveStarted, process.env.DISCORD_TEST_PUBLIC_BASE_URL)) await discordApi(`channels/${dm.id}/messages/${message.id}`, 'DELETE');
                        }
                    } catch { console.error('Interactive message cleanup could not be fully verified; inspect the dedicated test channel/recipient DMs manually. No message content is logged.'); }
                }
                for (const id of new Set(liveMessages)) await discordApi(`channels/${channelId}/messages/${id}`, 'DELETE');
            }
            finally { if (rootCreated) await rm(root, { recursive: true, force: true }); }
        }
    }
    if (started && keep) console.log('Retained isolated test container:', name, 'URL:', base);
    process.removeListener('SIGINT', interrupt); process.removeListener('SIGTERM', interrupt);
}
