// Installs locally generated packages into a NEW disposable Jellyfin; never mounts user data.
import { spawnSync } from 'node:child_process';
import { mkdir, readFile, writeFile, rm, rename, stat } from 'node:fs/promises';
import { resolve, join, dirname } from 'node:path';
import { randomUUID, createHash, createHmac } from 'node:crypto';
import { request as httpRequest } from 'node:http';
import assert from 'node:assert/strict';
import { target } from '../plugins/private-syncplay/scripts/jellyfin-target.mjs';
import { interactionsCompleted, ownedInvitation } from './discord-acceptance.mjs';
import { startDiscordTunnel, probeDiscordTunnel } from './discord-tunnel.mjs';

const option = name => process.argv.find(value => value.startsWith('--' + name + '='))?.split('=').slice(1).join('=');
const liveDiscord = process.argv.includes('--live-discord');
const interactiveDiscord = process.argv.includes('--interactive-discord');
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
const pluginNames = { hub: 'Account Hub', discord: 'Discord Bridge', deletion: 'Deletion Queue', syncplay: 'Private SyncPlay', notifications: 'Notifications', automatic: 'Automatic Deletion Requests' };
const selected = option('plugins') === 'none' ? [] : (option('plugins') || 'hub,discord,deletion,syncplay,notifications,automatic').split(',');
assert(selected.every(key => key in pluginNames), 'Unknown plugin selection');
const has = key => selected.includes(key);
const name = 'jellyfin-suite-test-' + randomUUID().slice(0, 8), keep = process.argv.includes('--keep');
const run = (exe, args) => { const result = spawnSync(exe, args, { encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 }); if (result.status !== 0) throw Error(`${exe} ${args[0]} failed: ${result.stderr || result.stdout}`); return result.stdout.trim(); };
const docker = (...args) => run('docker', args);
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const normal = x => Array.isArray(x) ? x.map(normal) : x && typeof x === 'object' ? Object.fromEntries(Object.entries(x).map(([k, v]) => [k[0].toLowerCase() + k.slice(1), normal(v)])) : x;
const guid = value => value.replaceAll('-', '').replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5');
const libraryFixture = process.argv.includes('--library');
const extendedSecurity = process.argv.includes('--extended-security');
const v1Flows = process.argv.includes('--v1-flows');
const databaseSafety = process.argv.includes('--database-safety');
const storageRecovery = process.argv.includes('--storage-recovery');
const themeCheck = process.argv.includes('--theme-check');
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
async function testLibraryAdditions() {
    assert(has('hub') || has('discord'), '--library needs Hub or Bridge');
    const media = join(root, 'media');
    for (const path of ['movies/Baseline', 'movies/Future', 'tv/Disposable Series/Season 01']) await mkdir(join(media, path), { recursive: true });
    const video = path => docker('exec', name, '/usr/lib/jellyfin-ffmpeg/ffmpeg', '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'color=c=black:s=64x64:r=1', '-t', '1', '-c:v', 'mpeg4', '-y', '/media/' + path);
    video('movies/Baseline/Baseline (2026).mp4');
    const options = { EnableRealtimeMonitor: false, AutomaticRefreshIntervalDays: 0, TypeOptions: ['Movie', 'Series', 'Season', 'Episode'].map(Type => ({ Type, MetadataFetchers: [], ImageFetchers: [], MetadataFetcherOrder: [], ImageFetcherOrder: [] })) };
    await request('Library/VirtualFolders?name=DisposableMovies&collectionType=movies&paths=%2Fmedia%2Fmovies', { method: 'POST', body: { LibraryOptions: options }, expected: 204 });
    await request('Library/VirtualFolders?name=DisposableTV&collectionType=tvshows&paths=%2Fmedia%2Ftv', { method: 'POST', body: { LibraryOptions: options }, expected: 204 });
    await request('Library/Refresh', { method: 'POST', expected: 204 });
    const mediaItems = async () => (await request('Items?Recursive=true&IncludeItemTypes=Movie,Episode')).data.items;
    await until(async () => (await mediaItems()).length === 1, 'initial synthetic-media scan');
    if (has('hub')) assert.equal((await request('AccountHub/api/v1/library/settings')).data.data.health.retained, 0, 'Disabled notifications must not retain media/audience details');
    const libraries = (await request('Library/VirtualFolders')).data.map(folder => guid(folder.itemId)); assert.equal(libraries.length, 2);
    const password = 'Disposable-Library-Only-9714!';
    const createUser = async (username, blocked = false) => {
        const user = (await nativeFixtureUser({ Name: username })).data;
        await request(`Users/${user.id}/Password`, { method: 'POST', body: { CurrentPw: '', NewPw: password, ResetPassword: false }, expected: 204 });
        if (blocked) await request(`Users/${user.id}/Policy`, { method: 'POST', body: { ...user.policy, EnableAllFolders: false, EnabledFolders: [] }, expected: 204 });
        const login = (await request('Users/AuthenticateByName', { method: 'POST', auth: {}, body: { Username: username, Pw: password } })).data;
        return { id: user.id, token: login.accessToken, policy: user.policy };
    };
    const ordinary = await createUser('library-ordinary'), blocked = await createUser('library-blocked', true);
    if (has('hub')) {
        const current = (await request('AccountHub/api/v1/library/settings')).data.data.settings;
        const revision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
        await request('AccountHub/api/v1/commands/settings.library', { method: 'POST', body: { commandId: randomUUID(), revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', payload: { ...current, enabled: true } } });
        await request('AccountHub/api/v1/notifications/preferences', { method: 'PUT', auth: ordinary, body: [{ source: 'account-hub', type: 'library.episode.added', channel: 'in-app', enabled: false }] });
    }
    if (has('discord')) {
        const config = (await request('DiscordBridge/admin/v1/settings')).data.data;
        assert.equal(config.libraryFolders.length, 2, 'Bridge must discover native library IDs');
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: { revision: config.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', ...(liveDiscord ? { token: botToken } : {}), settings: { ...config.settings, ...(liveDiscord ? { enabled: true, guildId } : {}), libraryAnnouncementsEnabled: true, libraryDisclosureAcknowledged: true, allowedChannelIds: [liveDiscord ? channelId : '1234567890'], libraryChannelIds: [liveDiscord ? channelId : '1234567890'], libraryIds: libraries } } });
        if (liveDiscord) {
            const channel = await discordApi('channels/' + channelId); assert.equal(channel.guild_id, guildId, 'Test channel must belong to the selected guild');
            await until(async () => { const state = (await request('DiscordBridge/admin/v1/settings')).data.data; return state.connected && state.state === 'connected'; }, 'live gateway and guild command registration');
            const bot = await discordApi('users/@me');
            const commands = await discordApi(`applications/${bot.id}/guilds/${guildId}/commands`);
            for (const name of ['help', 'link', 'unlink', 'reset', 'status', 'watchparty']) assert(commands.some(command => command.name === name), 'Expected guild command missing');
            console.log('PASS real bot gateway connection, guild/channel scope and slash-command registration.');
        }
    }
    const inbox = async auth => (await request('AccountHub/api/v1/notifications?limit=100', { auth })).data.data.notifications.filter(row => row.event.type.startsWith('library.'));
    if (has('hub')) assert.equal((await inbox(admin)).length, 0, 'Enabling must not backfill existing media');
    await wait(1100);
    liveStage = 'new-media-scan';
    video('movies/Future/Future (2026).mp4'); video('tv/Disposable Series/Season 01/Disposable Series S01E01.mp4');
    await request('Library/Refresh', { method: 'POST', expected: 204 });
    await until(async () => (await mediaItems()).length === 3, 'new episode and movie scan');
    if (has('hub')) {
        liveStage = 'inbox-permissions';
        await until(async () => (await inbox(admin)).length === 2, 'permission-aware episode/movie inbox notifications');
        assert.deepEqual((await inbox(admin)).map(row => row.event.type).sort(), ['library.episode.added', 'library.movie.added']);
        assert.equal((await inbox(ordinary)).length, 1, 'Episode-specific personal mute must not mute movies');
        assert.equal((await inbox(blocked)).length, 0, 'Inaccessible library metadata must not enter another user inbox');
        await request(`Users/${ordinary.id}/Policy`, { method: 'POST', body: { ...ordinary.policy, EnableAllFolders: false, EnabledFolders: [] }, expected: 204 });
        assert.equal((await inbox(ordinary)).length, 0, 'Permission revocation must hide queued/stored media details');
        assert.equal((await request('AccountHub/api/v1/notifications/unread-count', { auth: ordinary })).data.data.count, 0);
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
            for (const id of [ordinary.id, blocked.id, ...libraries]) assert(!JSON.stringify(card).includes(id), 'No audience or library identities are allowed in shared cards');
        }
        console.log('PASS real installed-plugin Discord episode/movie cards, one per kind, media-only fields, no identities/URLs/images or user/everyone mentions.');
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
let rootCreated = false;
try {
    // Refuse to reuse any existing directory, even one from an earlier test.
    await mkdir(dirname(root), { recursive: true });
    await mkdir(root, { recursive: false });
    rootCreated = true;
    await mkdir(join(root, 'config', 'config'), { recursive: true }); await mkdir(join(root, 'cache')); if (libraryFixture) await mkdir(join(root, 'media'));
    const manifest = JSON.parse(await readFile(join(packages, 'manifest.json'), 'utf8'));
    assert(Array.isArray(manifest) && manifest.length >= 1 && manifest.length <= 6, 'Expected selected-plugin or aggregate package catalog');
    assert.equal(new Set(manifest.map(plugin=>plugin.name)).size,manifest.length,'Duplicate plugin package');
    assert(manifest.every(plugin=>Object.values(pluginNames).includes(plugin.name)),'Unknown plugin package');
    for(const key of selected)assert(manifest.some(plugin=>plugin.name===pluginNames[key]),'Selected plugin package missing: '+key);
    const packageRuntime = platform === 'linux/arm64' ? 'linux-arm64' : 'linux-x64';
    assert(manifest.every(plugin => new URL(plugin.versions[0].sourceUrl).pathname.endsWith('-' + packageRuntime + '.zip')), 'Packages must match the actual server architecture');
    for (const plugin of manifest.filter(item => selected.some(key => pluginNames[key] === item.name))) {
        const zip = join(packages, new URL(plugin.versions[0].sourceUrl).pathname.split('/').at(-1));
        const destination = join(root, 'config', 'plugins', plugin.name + '_' + plugin.versions[0].version);
        run(process.env.PWSH || 'pwsh', ['-NoProfile', '-CommandWithArgs', '[IO.Compression.ZipFile]::ExtractToDirectory($args[0],$args[1])', zip, destination]);
    }
    if (option('security')) {
        const zip = resolve(option('security'));
        assert.equal(createHash('sha256').update(await readFile(zip)).digest('hex'), '6c1ae5e41cc0bc7f168025d9b941578f13115cd73cac7a72496888c46f64a998', 'Security fixture must match the pinned jf12 release');
        run(process.env.PWSH || 'pwsh', ['-NoProfile', '-CommandWithArgs', '[IO.Compression.ZipFile]::ExtractToDirectory($args[0],$args[1])', zip, join(root, 'config', 'plugins', 'JellyfinSecurity_2.6.4.0')]);
    }
    await writeFile(join(root, 'config', 'config', 'network.xml'), '<?xml version="1.0"?><NetworkConfiguration><BaseUrl>/suite</BaseUrl><EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>true</EnableRemoteAccess><InternalHttpPort>8096</InternalHttpPort></NetworkConfiguration>');
    docker('run', '-d', '--platform', platform, '--name', name, '-p', '127.0.0.1::8096', '-v', `${join(root, 'config')}:/config`, '-v', `${join(root, 'cache')}:/cache`, ...(libraryFixture ? ['-v', `${join(root, 'media')}:/media`] : []), target.image); started = true;
    if (platform === 'linux/arm64') assert.equal(docker('exec', name, 'uname', '-m'), 'aarch64', 'ARM fixture must execute the ARM server image, not just ARM-named plugin ZIPs');
    base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
    let ready = false;
    for (let i = 0; i < (platform === 'linux/arm64' ? 300 : 90); i++) { try { if ((await request('Startup/Configuration', { auth: null, expected: null })).status === 200) { ready = true; break; } } catch {} await wait(1000); }
    assert(ready, 'Jellyfin startup timed out');
    const password = 'Disposable-Only-Suite-9714!';
    await request('Startup/Configuration', { method: 'POST', auth: null, body: { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' }, expected: 204 });
    await request('Startup/User', { auth: null });
    await request('Startup/User', { method: 'POST', auth: null, body: { Name: 'suite-admin', Password: password }, expected: 204 });
    await request('Startup/RemoteAccess', { method: 'POST', auth: null, body: { EnableRemoteAccess: true, EnableAutomaticPortMapping: false }, expected: 204 });
    await request('Startup/Complete', { method: 'POST', auth: null, expected: 204 });
    const login = await request('Users/AuthenticateByName', { method: 'POST', auth: {}, body: { Username: 'suite-admin', Pw: password } }); admin = { token: login.data.accessToken };
    const installed = (await request('Plugins')).data;
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
    if(has('notifications')) {
        const endpoint='Notifications/api/v1/';
        await request(endpoint+'notifications',{auth:null,expected:[401,403]});
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
        if(process.argv.includes('--notifications-restart')) {
            docker('restart',name);
            base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
            let ready=false;
            for(let i=0;i<90;i++){try{if((await request('System/Info/Public',{auth:null,expected:null})).status===200){ready=true;break;}}catch{}await wait(1000);}
            assert(ready,'Notification restart fixture did not recover');
            assert.equal((await request(endpoint+'notifications/unread-count')).data.data.count,before+1,'Read state/inbox must survive restart');
        }
        const html=(await request('web/index.html',{auth:null})).text;
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
        const activity = await request('DiscordBridge/activity/', { auth: null });
        assert(activity.text.includes('Jellyfin watch-party companion'));
        assert(activity.headers.get('Content-Security-Policy').includes('frame-ancestors https://discord.com'));
        assert(!activity.headers.get('Content-Security-Policy').includes("script-src 'unsafe-inline'"));
        const activityConfig = (await request('DiscordBridge/activity/config', { auth: null })).data;
        assert(Object.keys(activityConfig).every(key => ['applicationId', 'available', 'publicServerUrl'].includes(key)), 'Activity config must not disclose transport/user fields');
        assert.equal(activityConfig.available, false, 'Disabled Bridge must fail closed without exposing bot configuration');
        assert.equal(activityConfig.applicationId ?? null, null); assert.equal(activityConfig.publicServerUrl ?? null, null);
        assert((await request('DiscordBridge/activity/assets/activity.js', { auth: null })).text.includes('disableConsoleLogOverride'));
        await request('DiscordBridge/activity/assets/not-allowed', { auth: null, expected: 404 });
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
    await request(`Users/${person.id}/Password`, { method: 'POST', body: { CurrentPw: '', NewPw: password, ResetPassword: false }, expected: 204 });
    const userLogin = (await request('Users/AuthenticateByName', { method: 'POST', auth: {}, body: { Username: 'suite-user', Pw: password } })).data;
    await request('AccountHub/api/v1/workspace', { auth: { token: userLogin.accessToken }, expected: 403 });
    await request('Auth/Keys?app=DisposableSuiteTest', { method: 'POST', expected: 204 });
    const apiKey = (await request('Auth/Keys')).data.items.find(item => item.appName === 'DisposableSuiteTest'); assert(apiKey?.accessToken);
    await request('AccountHub/api/v1/workspace', { auth: { token: apiKey.accessToken }, expected: 403 });
    const revision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
    const changed = await request('AccountHub/api/v1/commands/policy.apply', { method: 'POST', body: { commandId: randomUUID(), revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', payload: { userIds: [guid(person.id), guid(login.data.user.id)], managedFields: { EnableContentDownloading: false, MaxActiveSessions: 3 } } } });
    assert(changed.data.data.outcomes.some(item => item.status === 'administrator_skipped'));
    const applied = changed.data.data.outcomes.find(item => item.status === 'applied');
    assert(applied, 'Policy apply failed: ' + JSON.stringify(changed.data.data.outcomes.map(({ status, code }) => ({ status, code }))));
    const snapshot = applied.snapshotId; assert(snapshot);
    assert.equal((await request(`Users/${person.id}`)).data.policy.maxActiveSessions, 3);
    const nextRevision = (await request('AccountHub/api/v1/workspace')).data.data.revision;
    await request('AccountHub/api/v1/commands/policy.rollback', { method: 'POST', body: { commandId: randomUUID(), revision: nextRevision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', payload: { userIds: [guid(person.id)], snapshotId: snapshot } } });
    assert.notEqual((await request(`Users/${person.id}`)).data.policy.maxActiveSessions, 3);
    console.log('PASS non-admin/API-key rejection, managed-policy apply, administrator exclusion, durable snapshot and managed-only rollback.');
    if (v1Flows) await testV1Flows(person);
    if (libraryFixture) await testLibraryAdditions();
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
        console.log('PASS pinned real Security 2.6.4 status/users/devices, authorized signing-secret write, raw-body HMAC acceptance, replay dedupe and tamper rejection.');
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
        const result = JSON.parse(docker('run', '--rm', '--network=none', '--read-only', '--tmpfs', '/tmp:rw,noexec,nosuid,size=512m', '--mount', `type=bind,source=${join(root, 'config')},target=/fixture,readonly`, 'python:3.13-alpine', 'python', '-c', checker));
        await writeFile(join(root, 'database-safety.json'), JSON.stringify(result, null, 2));
        console.log('PASS abrupt restart preserves native identities/policies and Hub revision; read-only SQLite integrity, foreign keys and native ID uniqueness:', result.length, 'databases.');
    }
    if (storageRecovery) {
        docker('stop', '--time=30', name);
        const database = join(root, 'config', 'plugins', 'configurations', 'AccountHub', 'account-hub.db');
        const moved = [];
        const exists = async path => { try { await stat(path); return true; } catch (error) { if (error.code === 'ENOENT') return false; throw error; } };
        try {
            for (const suffix of ['', '-wal', '-shm', '-journal']) if (await exists(database + suffix)) { await rename(database + suffix, database + suffix + '.preserved'); moved.push(suffix); }
            assert(moved.includes(''), 'Expected an initialized owned Hub database');
            docker('start', name); base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
            await until(async () => { try { return (await request('System/Info/Public', { auth: null, expected: null })).status === 200; } catch { return false; } }, 'native startup with missing Hub database');
            const health = await request('AccountHub/api/v1/health', { expected: 503 });
            assert.equal(health.data.data.ready, false); assert.equal(health.data.data.errorCode, 'storage_recovery_required');
            assert(!await exists(database), 'No module may silently create a replacement Hub database');
        } finally {
            docker('stop', '--time=30', name);
            for (const suffix of ['', '-wal', '-shm', '-journal']) if (await exists(database + suffix)) await rename(database + suffix, database + suffix + '.unexpected');
            for (const suffix of moved) await rename(database + suffix + '.preserved', database + suffix);
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
            await rename(file, file + '.preserved');
            try {
                docker('start', name); base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
                await until(async () => { try { return (await request('System/Info/Public', { auth: null, expected: null })).status === 200; } catch { return false; } }, 'native startup with missing rule state');
                await request(endpoint, { expected: 409 });
                await request(endpoint, { method: 'PUT', body: { configuration: saved, confirmAutomation: false }, expected: 409 });
                assert(!await exists(file), 'Missing rule state must not silently reset configuration or receipts');
            } finally {
                docker('stop', '--time=30', name);
                if (await exists(file)) await rename(file, file + '.unexpected');
                await rename(file + '.preserved', file);
            }
            docker('start', name); base = `http://127.0.0.1:${docker('port', name, '8096/tcp').split(':').at(-1)}/suite`;
            await until(async () => { try { return (await request(endpoint, { expected: null })).status === 200; } catch { return false; } }, 'rule state recovery after original restoration');
            assert.deepEqual((await request(endpoint)).data.configuration, saved);
            console.log('PASS missing automatic-rule state fails closed across restart, preserves the installation marker and recovers only after original fixture restoration.');
        }
    }
    if (interactiveDiscord) {
        liveStage = 'human interactive commands';
        assert(has('hub') && has('syncplay'), 'Interactive watch-party acceptance requires Hub and Private SyncPlay');
        if (discordTunnel) {
            temporaryTunnel = await startDiscordTunnel(name, (...args) => {
                const result = spawnSync('docker', args, { encoding: 'utf8', timeout: 180000, maxBuffer: 1024 * 1024 });
                assert.equal(result.status, 0, 'Disposable tunnel Docker operation failed (details withheld)');
                return (result.stdout + (args[0] === 'logs' ? result.stderr : '')).trim();
            });
            process.env.DISCORD_TEST_PUBLIC_BASE_URL = temporaryTunnel.origin + '/suite';
        }
        const publicBaseUrl = process.env.DISCORD_TEST_PUBLIC_BASE_URL;
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
        const bridgeConfig = (await request('DiscordBridge/admin/v1/settings')).data.data;
        await request('DiscordBridge/admin/v1/settings', { method: 'PUT', body: { revision: bridgeConfig.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE', settings: { ...bridgeConfig.settings, publicServerUrl: publicBaseUrl, enableSelfServiceCommands: true, enableWatchPartyCommands: true } } });
        await until(async () => (await request('DiscordBridge/admin/v1/settings')).data.data.connected, 'interactive bot configuration reconnect');
        console.log('Disposable HTTPS test endpoint:', publicBaseUrl);
        if (!discordTunnel) console.log('Disposable reverse proxy must forward to this fixture endpoint:', base);
        console.log('ACTION REQUIRED in the disposable Discord guild: execute /help, /status, /link, complete verified linking to this disposable Jellyfin server, /watchparty devices, then /watchparty invite twice: once to the configured consenting test recipient and once to the approved test channel with confirm-share:true. Use no other destinations. Inspect both messages privately. Do not paste bearer links, passwords or proof codes into logs/chat. No commands are forged or automatically replayed.');
        await until(async () => {
            const counters = (await request('DiscordBridge/admin/v1/settings')).data.data.interactions || {};
            return interactionsCompleted(counters);
        }, 'human-invoked Discord self-service and selected-destination watch-party delivery', 600);
        const bot = await discordApi('users/@me');
        const recent = await discordApi(`channels/${channelId}/messages?limit=100`);
        for (const message of recent) if (ownedInvitation(message, bot.id, liveStarted, publicBaseUrl)) liveMessages.push(message.id);
        console.log('PASS real gateway command handlers acknowledged self-service responses and selected-destination invitation delivery; metrics retain no actor, channel, link or message payload.');
    }
    if (themeCheck) {
        await mkdir(join(root, 'admin-screenshots'), { recursive: true });
        // No published port or host alias is needed: the browser shares only
        // this newly owned disposable server's network namespace.
        const output = docker('run', '--rm', '--network', 'container:' + name,
            '--mount', `type=bind,source=${resolve('scripts')},target=/checks,readonly`,
            '--mount', `type=bind,source=${join(root, 'admin-screenshots')},target=/evidence`,
            '--env', 'ADMIN_SCREENSHOTS=/evidence',
            '--env', 'THEME_TEST_AUTOMATIC=' + has('automatic'),
            '--env', 'DISPOSABLE_THEME_TEST=true', '--env', 'THEME_TEST_BASE_URL=http://127.0.0.1:8096/suite',
            'mcr.microsoft.com/playwright:v1.56.1-noble@sha256:f1e7e01021efd65dd1a2c56064be399f3e4de00fd021ac561325f2bfbb2b837a',
            '/bin/sh', '/checks/run-theme-acceptance.sh');
        const line = output.split(/\r?\n/).find(line => line.startsWith('{"checks":'));
        assert(line, 'Browser check result missing');
        const result = JSON.parse(line);
        assert(result.checks.length === 36 && result.checks.every(item => item.passed) && result.activity?.length === 3 && result.syncplaySettings === true, 'Browser coverage incomplete');
        await writeFile(join(root, 'theme-results.json'), JSON.stringify(result, null, 2));
        console.log('PASS 36 account-focused native-theme/workspace checks, keyboard/custom-theme override and Activity fail-closed layouts at three widths.');
    }
    console.log('PASS combination:', selected.join('+') || 'none', option('security') ? '+ native Security' : '(no native Security)', '— independent startup/configuration, optional-dependency health and installed-feature checks.');
    console.log('Server platform:', platform, '(ARM execution on an x64 Docker host uses emulation; this is not hardware/performance certification).');
    if (!liveDiscord) console.log('Disposable evidence:', root);
    else console.log('Live fixture passed; credential-bearing disposable state is removed during cleanup.');
} catch (error) {
    if (started && !liveDiscord) { await writeFile(join(root, 'server.log'), docker('logs', name)); console.error('Server log:', join(root, 'server.log')); }
    if (liveDiscord) {
        try {
            const health = (await request('DiscordBridge/admin/v1/settings')).data.data.libraryHealth;
            console.error('Sanitized library queue:', JSON.stringify(Object.fromEntries(['retained', 'pending', 'uncertainDiscord', 'failed'].map(key => [key, Number(health[key])]))));
        } catch { /* No raw diagnostics or credentials. */ }
        throw Error('Live Discord fixture failed at ' + liveStage + '; sensitive details withheld.');
    }
    throw error;
} finally {
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
