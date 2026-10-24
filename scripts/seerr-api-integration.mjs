// Disposable public-API acceptance: no Seerr private-state access, .env, or CLI secrets.
import { spawnSync } from 'node:child_process';
import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { randomUUID, randomBytes } from 'node:crypto';
import assert from 'node:assert/strict';
const suffix = randomUUID().slice(0, 8);
// Keep Node alive while undici waits on its unreferenced connection/timeout handles.
const keepAlive = setInterval(() => {}, 1000);
const network = `seerr-api-${suffix}`;
const jf = `${network}-jf`, seerr = `${network}-seerr`;
function docker(...args) {
  const r = spawnSync('docker', args, { encoding: 'utf8', timeout: 180000 });
  if (r.status !== 0) throw new Error(`Docker ${args[0]} failed (output withheld)`);
  return r.stdout.trim();
}
const sleep = ms => new Promise(r => setTimeout(r, ms));
let cookie, key;
const packages = process.argv.find(arg => arg.startsWith('--packages='))?.slice('--packages='.length);
let pluginDirectory;
async function api(base, path, body, method = body === undefined ? 'GET' : 'POST', auth = {}) {
  let response;
  try { response = await fetch(base + path, { method, headers: { 'Content-Type': 'application/json', ...auth }, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30000) }); }
  catch (error) {
    const value = error.cause?.code || error.name;
    const code = /^[A-Za-z0-9_]{1,80}$/.test(value || '') ? value : 'transport_failure';
    throw new Error(`${method} ${path}: ${code} (transport details withheld)`);
  }
  if (!response.ok) {
    let code = '';
    if (path.startsWith('/AccountHub/api/v1/')) {
      try { const value = (await response.json()).error?.code; if (/^[a-z0-9_.-]{1,80}$/.test(value || '')) code = '; code=' + value; } catch { /* Never emit raw responses. */ }
    }
    throw new Error(`${method} ${path}: HTTP ${response.status}${code} (body withheld)`);
  }
  if (response.headers.get('set-cookie')) cookie = response.headers.get('set-cookie').split(';')[0];
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}
async function wait(base, path) {
  for (let i = 0; i < 120; i++) {
    try { await api(base, path); return; } catch { await sleep(1000); }
  }
  throw new Error(`Readiness timeout: ${path}`);
}
try {
  if (packages) {
    const directory = resolve(packages), archives = (await readdir(directory)).filter(name => /^AccountHub-.*\.zip$/.test(name));
    assert.equal(archives.length, 1, 'Choose one immutable Account Hub package');
    pluginDirectory = await mkdtemp(join(process.env.TEMP, 'opencode', 'seerr-plugin-'));
    const extracted = spawnSync(process.env.PWSH || 'pwsh', ['-NoProfile', '-CommandWithArgs', '[IO.Compression.ZipFile]::ExtractToDirectory($args[0],$args[1])', join(directory, archives[0]), join(pluginDirectory, 'config', 'plugins', 'AccountHub_1.0.0.0')], { encoding: 'utf8' });
    assert.equal(extracted.status, 0, 'Hub fixture extraction failed (output withheld)');
  }
  docker('network', 'create', network);
  // Jellyfin owns mutable plugin metadata beside the installed assemblies. Use a
  // disposable writable config directory, not a read-only nested mount under tmpfs.
  docker('run', '-d', '--name', jf, '--network', network, '-p', '127.0.0.1::8096', ...(pluginDirectory ? ['--mount', `type=bind,source=${join(pluginDirectory, 'config')},target=/config`] : ['--tmpfs', '/config']), '--tmpfs', '/cache', 'jellyfin/jellyfin:12.2');
  docker('run', '-d', '--name', seerr, '--network', network, '-p', '127.0.0.1::5055', '--tmpfs', '/app/config:uid=1000,gid=1000', 'seerr/seerr:v3.5.1');
  const port = name => docker('port', name, name === jf ? '8096/tcp' : '5055/tcp').split(':').at(-1);
  const j = `http://127.0.0.1:${port(jf)}`, s = `http://127.0.0.1:${port(seerr)}/api/v1`;
  await Promise.all([wait(j, '/System/Info/Public'), wait(s, '/status?checkUpdateAvailable=false')]);
  assert.match((await api(s, '/status?checkUpdateAvailable=false')).version, /^v?3\.5\.1$/);
  const password = randomBytes(24).toString('hex');
  await api(j, '/Startup/Configuration', { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' });
  await api(j, '/Startup/User');
  await api(j, '/Startup/User', { Name: 'acceptance-owner', Password: password });
  await api(j, '/Startup/RemoteAccess', { EnableRemoteAccess: true, EnableAutomaticPortMapping: false });
  await api(j, '/Startup/Complete', {});
  const nativeAuth = { Authorization: 'MediaBrowser Client="SeerrAcceptance", Device="Disposable", DeviceId="acceptance", Version="1"' };
  const login = await api(j, '/Users/AuthenticateByName', { Username: 'acceptance-owner', Pw: password }, 'POST', nativeAuth);
  const nativeToken = login.AccessToken ?? login.accessToken;
  assert.ok(nativeToken, 'Native authentication must return a token');
  const nativeAdminHeaders = { Authorization: nativeAuth.Authorization + `, Token="${nativeToken}"` };
  async function createNativeUser(body) {
    const override = async enabled => {
      const current = await api(j, '/AccountHub/api/v1/workspace', undefined, 'GET', nativeAdminHeaders);
      await api(j, '/AccountHub/api/v1/user-creation/native-override', { enabled, commandId: randomUUID(), revision: current.data.revision, confirmation: 'CONFIRM ACCOUNT HUB CHANGE' }, 'POST', nativeAdminHeaders);
    };
    if (packages) await override(true);
    try { return await api(j, '/Users/New', body, 'POST', nativeAdminHeaders); }
    finally { if (packages) await override(false); }
  }
  const user = await createNativeUser({ Name: 'acceptance-user', Password: randomBytes(24).toString('hex') });
  const nativeId = user.Id ?? user.id;
  await api(s, '/auth/jellyfin', { username: 'acceptance-owner', password, hostname: jf, port: 8096, useSsl: false, urlBase: '', serverType: 2, email: 'owner@example.test' });
  const main = await api(s, '/settings/main', undefined, 'GET', { Cookie: cookie });
  key = main.apiKey;
  assert.ok(key, 'API key must be obtainable via public authenticated settings');
  const auth = { 'X-Api-Key': key };
  await api(s, '/settings/initialize', {}, 'POST', auth);
  const imported = await api(s, '/user/import-from-jellyfin', { jellyfinUserIds: [nativeId] }, 'POST', auth);
  assert.equal(imported.length, 1);
  const account = await api(s, `/user/jellyfin/${nativeId}`, undefined, 'GET', auth);
  assert.equal(account.jellyfinUserId.replaceAll('-', ''), nativeId.replaceAll('-', ''));
  assert.notEqual(account.id, 1);
  const profile = await api(s, `/user/${account.id}/settings/main`, undefined, 'GET', auth);
  const preserved = Object.fromEntries(['username', 'email', 'locale', 'discoverRegion', 'streamingRegion', 'originalLanguage', 'watchlistSyncMovies', 'watchlistSyncTv'].filter(k => k in profile).map(k => [k, profile[k]]));
  await api(s, `/user/${account.id}/settings/main`, { ...preserved, movieQuotaLimit: 10, movieQuotaDays: 7, tvQuotaLimit: null, tvQuotaDays: null }, 'POST', auth);
  const quota = await api(s, `/user/${account.id}/settings/main`, undefined, 'GET', auth);
  assert.equal(quota.movieQuotaLimit, 10); assert.equal(quota.movieQuotaDays, 7);
  assert.equal(quota.tvQuotaLimit, null); assert.equal(quota.tvQuotaDays, null);
  await api(s, `/user/${account.id}/settings/permissions`, { permissions: 32 }, 'POST', auth);
  assert.equal((await api(s, `/user/${account.id}`, undefined, 'GET', auth)).permissions, 32);
  assert.deepEqual((await api(s, `/user/${account.id}/requests?take=10&skip=0`, undefined, 'GET', auth)).results, []);
  await api(s, `/user/${account.id}`, undefined, 'DELETE', auth);
  const absent = await fetch(`${s}/user/jellyfin/${nativeId}`, { headers: auth });
  assert.equal(absent.status, 404);
  if (packages) {
    const nativeHeaders = { Authorization: nativeAuth.Authorization + `, Token="${nativeToken}"` };
    const hub = async (path, body) => {
      const reply = await api(j, '/AccountHub/api/v1/' + path, body, body === undefined ? 'GET' : 'POST', nativeHeaders);
      assert.equal(reply.ok, true, 'Hub API rejected operation'); return reply.data;
    };
    let hubReady = false;
    for (let i = 0; i < 120; i++) { try { await hub('workspace'); hubReady = true; break; } catch { await sleep(1000); } }
    assert(hubReady, 'Installed Hub did not become available');
    const revision = async () => (await hub('workspace')).revision;
    const command = async (path, payload) => hub(path, { commandId: randomUUID(), revision: await revision(), confirmation: 'CONFIRM ACCOUNT HUB CHANGE', payload });
    await command('commands/settings.seerr', { baseUrl: `http://${seerr}:5055`, allowHttp: true, allowPrivateNetwork: true });
    await hub('settings/secrets/seerr.apiKey', { commandId: randomUUID(), revision: await revision(), confirmation: 'CONFIRM ACCOUNT HUB CHANGE', secret: key });
    const discovery = await hub('seerr/test', {});
    assert.match(discovery.capabilities.version, /^v?3\.5\.1$/);
    const fresh = await createNativeUser({ Name: 'plugin-managed-user' });
    const id = fresh.Id ?? fresh.id;
    const dashed = id.replaceAll('-', '').replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5');
    const desired = { permissions: 32, movieQuota: { limit: 8, days: 7 }, tvQuota: { limit: null, days: null } };
    const provision = await command('seerr/commands/provision', { userId: dashed, desired });
    async function completed(jobId) {
      for (let i = 0; i < 120; i++) {
        const jobs = await hub('seerr/jobs?skip=0&limit=100'); const job = jobs.items.find(entry => entry.id === jobId);
        if (job?.state === 'completed') return job;
        assert(!['failed', 'blocked'].includes(job?.state), 'Installed Seerr job failed: ' + (job?.error || 'unknown'));
        await sleep(1000);
      }
      assert.fail('Installed Seerr job did not complete within fixture deadline');
    }
    await completed(provision.id);
    const drift = await hub(`users/${dashed}/seerr`); assert.equal(drift.linked, true); assert.equal(drift.drift, false);
    assert.deepEqual(drift.actual, desired);
    assert.deepEqual((await hub(`users/${dashed}/seerr/requests?skip=0&limit=10`)).items, []);
    await api(j, '/Users/' + id, undefined, 'DELETE', nativeHeaders);
    for (let i = 0; i < 120; i++) {
      const jobs = await hub('seerr/jobs?skip=0&limit=100'); const deletion = jobs.items.find(entry => entry.userId === dashed && entry.kind === 'delete');
      if (deletion) { await completed(deletion.id); break; }
      if (i === 119) assert.fail('Native deletion did not enqueue a Seerr follow-up');
      await sleep(1000);
    }
    assert.equal((await fetch(`${s}/user/jellyfin/${id}`, { headers: auth, signal: AbortSignal.timeout(30000) })).status, 404);
    console.log('PASS: installed Hub discovery, durable provisioning/quotas, drift/requests and native-deletion follow-up through real Seerr HTTP API only.');
  }
  console.log('PASS: real Seerr 3.5.1 HTTP API; native onboarding, API-key discovery, targeted import, immutable lookup, quota/null inheritance, permissions, request paging, deletion.');
} catch (error) {
  console.error(error.message); process.exitCode = 1;
  for (const name of [jf, seerr]) {
    const state = spawnSync('docker', ['inspect', '--format', '{{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}}', name], { encoding: 'utf8' });
    if (state.status === 0 && /^[a-z]+ exit=-?\d+ oom=(true|false)\s*$/.test(state.stdout)) console.error(name === jf ? 'Jellyfin fixture:' : 'Seerr fixture:', state.stdout.trim());
  }
} finally {
  for (const name of [seerr, jf]) spawnSync('docker', ['rm', '-f', '-v', name], { stdio: 'ignore' });
  spawnSync('docker', ['network', 'rm', network], { stdio: 'ignore' });
  if (pluginDirectory) await rm(pluginDirectory, { recursive: true, force: true });
  clearInterval(keepAlive);
}
