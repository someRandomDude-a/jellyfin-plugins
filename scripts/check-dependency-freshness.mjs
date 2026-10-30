// Read-only release gate. Never rewrite an immutable release's manifests/locks.
import { execFileSync } from 'node:child_process';
import { readFile, stat, mkdir, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { dirname, join } from 'node:path';
import { githubJson } from './latest-releases.mjs';
import { dependencyFingerprint, assertDependencyEvidence } from './dependency-evidence.mjs';

const version = value => String(value).replace(/^[v^~]/, '').split('.').map(Number);
export const compare = (a, b) => {
    const x = version(a), y = version(b);
    if (![...x, ...y].every(Number.isInteger)) throw Error('Unsupported stable version comparison: ' + a + ', ' + b);
    for (let i = 0; i < Math.max(x.length, y.length); i++) if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) - (y[i] || 0);
    return 0;
};
const latest = values => values.filter(v => /^v?\d+(?:\.\d+){1,3}$/.test(v)).sort(compare).at(-1);
const databasePackages = new Set(['Jellyfin.Controller', 'Jellyfin.Model', 'Jellyfin.Common', 'Microsoft.Data.Sqlite', 'Microsoft.Data.Sqlite.Core', 'Microsoft.EntityFrameworkCore.Sqlite', 'Microsoft.EntityFrameworkCore.Design', 'SkiaSharp', 'SkiaSharp.NativeAssets.Linux.NoDependencies']);
export function declarations(path, text) {
    // Old-version examples and reviewed safety fingerprints are not dependencies.
    if (path === 'Jellyfin.Target.props' || /(?:^|\/)(?:artifacts|node_modules|obj|bin|dist)\/|\.test\.|(?:^|\/)tests\//.test(path) && !path.endsWith('.csproj')) return [];
    const rows = [], add = (kind, name, current) => {
        if (typeof current !== 'string' || current.length > 500 || /[\r\n]|:\/\/[^/]*@|[?&](?:token|secret|password|auth)=/i.test(current)) throw Error('Unsafe dependency descriptor in ' + path + ' (value withheld)');
        rows.push({ path, kind, name, current });
    };
    if (path.endsWith('/package.json') || path === 'package.json') {
        const json = JSON.parse(text);
        for (const field of ['dependencies', 'devDependencies', 'optionalDependencies']) for (const [name, spec] of Object.entries(json[field] || {})) add('npm', name, spec);
    } else if (/\.(?:csproj|props|targets)$/.test(path)) {
        for (const [tag] of text.matchAll(/<(?:PackageReference|PackageVersion)\b[^>]*>/g)) {
            const name = tag.match(/(?:Include|Update)=["']([^"']+)["']/)?.[1];
            if (!name) throw Error('Unsupported NuGet declaration in ' + path);
            if (name.split(';').every(packageName => databasePackages.has(packageName))) continue;
            const current = tag.match(/Version=["']([^"']+)["']/)?.[1];
            if (!current || current.includes('$')) throw Error('Unresolved NuGet version policy for ' + name + ' in ' + path);
            add('nuget', name, current);
        }
    } else if (path === 'global.json') add('sdk', '.NET SDK', JSON.parse(text).sdk.version);
    else if ((/\.(?:ya?ml|mjs|cjs|ps1|sh)$/.test(path) || /(?:^|\/)Dockerfile$/.test(path)) && path !== 'scripts/latest-releases.mjs') {
        for (const m of text.matchAll(/uses:\s+([\w.-]+\/[\w.-]+)@([\w.-]+)(?:[ \t]+#[ \t]*(v?\d+[\w.-]*))?/g)) add('action', m[1], m[2]);
        for (const m of text.matchAll(/\b((?:[a-z0-9.-]+\/)?[a-z0-9._-]+\/[a-z0-9._-]+|python|nginx|node):(v?\d+[a-zA-Z0-9_.-]*|alpine)(?:@(sha256:[a-f0-9]{64}))?/g)) { add('image', m[1], m[2]); if (m[3]) rows.at(-1).digest = m[3]; }
        for (const m of text.matchAll(/\b(playwright)@(\d+\.\d+\.\d+)/g)) add('npm', m[1], m[2]);
        for (const m of text.matchAll(/node-version:\s*['"]?(\d+(?:\.\d+){0,2})/g)) add('node', 'Node.js', m[1]);
    }
    return rows;
}
async function json(url) {
    const response = await fetch(url, { signal: AbortSignal.timeout(30000), redirect: 'error' });
    if (!response.ok) throw Error('Discovery failed: HTTP ' + response.status + ' from ' + new URL(url).host);
    return response.json();
}
export function registryLookup() {
    const cache = new Map();
    const memo = (key, fn) => { if (!cache.has(key)) cache.set(key, fn()); return cache.get(key); };
    const release = name => memo('release:' + name, async () => {
        const data = await githubJson('repos/' + name + '/releases/latest');
        if (data.draft || data.prerelease || !/^v?\d+(?:\.\d+){1,3}$/.test(data.tag_name || '')) throw Error('Invalid stable release metadata for ' + name);
        return data;
    });
    const npm = name => memo('npm:' + name, async () => (await json('https://registry.npmjs.org/' + encodeURIComponent(name) + '/latest')).version);
    const actionCommit = (name, tag) => memo('action-ref:' + name + ':' + tag, async () => {
        const ref = await githubJson('repos/' + name + '/git/ref/tags/' + encodeURIComponent(tag));
        return ref.object.type === 'tag' ? (await githubJson('repos/' + name + '/git/tags/' + ref.object.sha)).object.sha : ref.object.sha;
    });
    const imageDigest = (name, tag) => memo('digest:' + name + ':' + tag, async () => {
        if (name.startsWith('mcr.microsoft.com/')) {
            const response = await fetch('https://mcr.microsoft.com/v2/' + name.slice('mcr.microsoft.com/'.length) + '/manifests/' + tag, { method: 'HEAD', headers: { Accept: 'application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.index.v1+json' }, signal: AbortSignal.timeout(30000), redirect: 'error' });
            if (!response.ok) throw Error('Image digest discovery failed: HTTP ' + response.status);
            const digest = response.headers.get('docker-content-digest');
            if (!/^sha256:[a-f0-9]{64}$/.test(digest || '')) throw Error('Missing current image digest: ' + name);
            return digest;
        }
        const info = await json('https://hub.docker.com/v2/repositories/' + (name.includes('/') ? name : 'library/' + name) + '/tags/' + tag);
        if (!/^sha256:[a-f0-9]{64}$/.test(info.digest || '')) throw Error('Missing current image digest: ' + name);
        return info.digest;
    });
    return async row => {
        if (row.kind === 'npm') return npm(row.name);
        if (row.kind === 'nuget') return memo('nuget:' + row.name, async () => latest((await json('https://api.nuget.org/v3-flatcontainer/' + row.name.toLowerCase() + '/index.json')).versions));
        if (row.kind === 'sdk') return memo('sdk', async () => latest((await json('https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json'))['releases-index'].filter(r => ['active', 'maintenance'].includes(r['support-phase'])).map(r => r['latest-sdk'])));
        if (row.kind === 'node') return memo('node', async () => latest((await json('https://nodejs.org/dist/index.json')).map(r => r.version)));
        if (row.kind === 'action') {
            const current = await release(row.name);
            if (/^[a-f0-9]{40}$/.test(row.current)) return { version: await actionCommit(row.name, current.tag_name), tag: current.tag_name };
            if (version(row.current).every((part, i) => part === version(current.tag_name)[i]) &&
                await actionCommit(row.name, row.current) !== await actionCommit(row.name, current.tag_name)) throw Error('Action tag does not point at the current stable release commit');
            return current.tag_name;
        }
        if (row.kind === 'image') {
            let expected;
            if (row.name === 'mcr.microsoft.com/playwright') expected = 'v' + await npm('playwright') + '-' + row.current.split('-').slice(1).join('-');
            const repos = { 'rhysd/actionlint': 'rhysd/actionlint', 'cloudflare/cloudflared': 'cloudflare/cloudflared' };
            if (repos[row.name]) expected = (await release(repos[row.name])).tag_name.replace(/^v/, row.current.startsWith('v') ? 'v' : '');
            if (['python', 'nginx', 'node'].includes(row.name)) {
                const tag = row.current.includes('alpine') ? 'alpine' : 'latest';
                expected = await imageDigest(row.name, row.current) === await imageDigest(row.name, tag) ? row.current : tag;
            }
            if (!expected) throw Error('No latest-stable policy for image ' + row.name + '; add an explicit resolver instead of silently skipping it');
            return { version: expected, digest: row.digest ? await imageDigest(row.name, expected) : undefined };
        }
        throw Error('Unsupported dependency kind: ' + row.kind);
    };
}
export async function checkFreshness(rows, lookup) {
    const findings = [];
    for (const row of rows) {
        try {
            const result = await lookup(row), expected = typeof result === 'string' ? result : result?.version;
            if (!expected) throw Error('No stable version returned');
            const equal = row.kind === 'image' || /^[a-f0-9]{40}$/.test(row.current)
                ? row.current === expected
                : row.kind === 'action' || row.kind === 'node'
                    ? version(row.current).every((part, i) => part === version(expected)[i])
                    : compare(row.current, expected) === 0;
            if (!equal) findings.push(`${row.path}: ${row.name} ${row.current} is not latest stable ${expected}`);
            if (row.digest && row.digest !== result?.digest) findings.push(`${row.path}: ${row.name} image digest is not the current stable tag's digest`);
        } catch (error) { findings.push(`${row.path}: ${row.name}: ${error.message}`); }
    }
    if (findings.length) throw Error('LATEST-ONLY RELEASE BLOCKED\n' + findings.map(line => '- ' + line).join('\n') + '\nRun dependency maintenance and pass all tests on its updated immutable source; no older-version fallback is permitted.');
}
export function lockChanges(before, after) {
    if (!before.packages || !after.packages) throw Error('Unsupported npm lock format; latest-only approval refused');
    return [...new Set([...Object.keys(before.packages), ...Object.keys(after.packages)])].filter(key => key &&
        ['version', 'integrity'].some(field => before.packages[key]?.[field] !== after.packages[key]?.[field]));
}
export function refreshLock(directory) {
    const args = ['install', '--package-lock-only', '--ignore-scripts', '--no-audit', '--no-fund', '--loglevel=error'];
    // Fixed Windows command, with no interpolated manifest or user arguments.
    execFileSync(process.platform === 'win32' ? (process.env.ComSpec || 'cmd.exe') : 'npm',
        process.platform === 'win32' ? ['/d', '/s', '/c', 'npm ' + args.join(' ')] : args,
        { cwd: directory, stdio: 'pipe', timeout: 240000 });
}
export function validateDirectLock(manifest, lock) {
    if (!lock.packages?.[''] || !Number.isInteger(lock.lockfileVersion)) throw Error('Unsupported npm lock format; direct dependency consistency refused');
    const stable = value => JSON.stringify(Object.entries(value || {}).sort(([a], [b]) => a.localeCompare(b)));
    for (const field of ['dependencies', 'devDependencies', 'optionalDependencies']) {
        if (stable(manifest[field]) !== stable(lock.packages[''][field])) throw Error('Lock root does not match direct ' + field);
        for (const [name, spec] of Object.entries(manifest[field] || {})) {
            const installed = lock.packages['node_modules/' + name];
            if (!installed?.version || compare(spec, installed.version) !== 0) throw Error('Direct lock version does not match the current declaration: ' + name);
        }
    }
    // Transitives remain reproducibly locked and npm ci verifies their integrity.
    // Their independent release/advisory state is informational, not our freshness gate.
}
export async function checkLocks(paths) {
    for (const path of paths.filter(p => /(?:^|\/)package-lock\.json$/.test(p) && !/(?:^|\/)(?:node_modules|artifacts)\//.test(p))) {
        try { validateDirectLock(JSON.parse(await readFile(join(dirname(path), 'package.json'), 'utf8')), JSON.parse(await readFile(path, 'utf8'))); }
        catch (error) { throw Error('LATEST-ONLY RELEASE BLOCKED: ' + path + ': ' + error.message); }
    }
}
export async function dependencyPaths() {
    const paths = execFileSync('git', ['ls-files', '-c', '-o', '--exclude-standard', '-z'], { encoding: 'utf8' }).split('\0').filter(Boolean);
    const existing = [];
    for (const path of new Set(paths)) {
        try { if ((await stat(path)).isFile()) existing.push(path); }
        catch (error) { if (error.code !== 'ENOENT') throw error; }
    }
    return existing;
}
export async function dependencyState() {
    const paths = await dependencyPaths();
    const rows = [{ path: '(running toolchain)', kind: 'node', name: 'Node.js', current: process.versions.node }];
    for (const path of new Set(paths)) if (/(?:package\.json|global\.json|Dockerfile|\.(?:csproj|props|targets|ya?ml|mjs|cjs|ps1|sh))$/.test(path)) rows.push(...declarations(path, await readFile(path, 'utf8')));
    const unique = [...new Map(rows.map(row => [JSON.stringify(row), row])).values()];
    if (!unique.some(row => row.path !== '(running toolchain)')) throw Error('No dependency declarations found; refusing empty release freshness approval');
    const locks = await Promise.all(paths.filter(path => /(?:^|\/)package-lock\.json$/.test(path) && !/(?:^|\/)(?:node_modules|artifacts)\//.test(path)).map(async path => [path, await readFile(path, 'utf8')]));
    const identity = {
        source: execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
        fingerprint: dependencyFingerprint(unique, locks),
        run: process.env.GITHUB_RUN_ID || process.env.SUITE_TEST_RUN_ID,
        attempt: process.env.GITHUB_RUN_ID ? process.env.GITHUB_RUN_ATTEMPT : '1'
    };
    return { paths, rows: unique, identity };
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const args = process.argv.slice(2);
    if (args.some(arg => arg !== '--refresh' && !arg.startsWith('--output='))) throw Error('Unsupported freshness option');
    const output = args.find(arg => arg.startsWith('--output='))?.slice(9);
    const { paths, rows, identity } = await dependencyState();
    const inherited = process.env.SUITE_DEPENDENCY_AUDIT;
    if (inherited && output) throw Error('A child may not replace the parent dependency audit');
    if (inherited && !args.includes('--refresh')) {
        assertDependencyEvidence(JSON.parse(await readFile(inherited, 'utf8')), identity);
        await checkLocks(paths);
        console.log('PASS current-run dependency audit: exact source, declarations, locks, Node and run attempt; no rediscovery.');
    } else {
        await checkFreshness(rows, registryLookup());
        await checkLocks(paths);
        if (output) {
            const evidence = { schemaVersion: 1, policy: 'latest-stable', verified: true, resolvedAt: new Date().toISOString(), ...identity };
            assertDependencyEvidence(evidence, identity);
            await mkdir(dirname(output), { recursive: true });
            await writeFile(output, JSON.stringify(evidence, null, 2) + '\n');
        }
        console.log('PASS latest-only dependency freshness: ' + rows.length + ' declarations; no manifests or locks modified.');
    }
}
