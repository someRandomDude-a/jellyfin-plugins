// Resolve current stable software under test. A run records concrete releases;
// those snapshots are evidence, never a permanent older-version fallback.
import { readFile, writeFile, mkdir, rename } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const projects = {
    jellyfin: { repository: 'jellyfin/jellyfin', image: 'jellyfin/jellyfin' },
    security: { repository: 'ZL154/JellyfinSecurity' },
    enhanced: { repository: 'n00bcodr/Jellyfin-Enhanced' },
    seerr: { repository: 'seerr-team/seerr', image: 'seerr/seerr' },
    sonarr: { repository: 'Sonarr/Sonarr', image: 'lscr.io/linuxserver/sonarr:latest' },
    radarr: { repository: 'Radarr/Radarr', image: 'lscr.io/linuxserver/radarr:latest' }
};
export const freshDockerArguments = args => args[0] === 'run' ? ['run', '--pull=always', ...args.slice(1)] : args;
export function releaseVersion(tag) {
    if (typeof tag !== 'string' || !/^v?\d+(?:\.\d+){1,3}$/.test(tag)) throw Error('Expected a numeric stable release tag');
    return tag.replace(/^v/, '');
}
export function describeRelease(name, release, { jellyfinMajor = 12 } = {}) {
    const project = projects[name];
    if (!project || release.draft || release.prerelease) throw Error('Expected an official stable release for ' + name);
    const version = releaseVersion(release.tag_name);
    const result = { repository: project.repository, version, tag: release.tag_name, releaseId: release.id, publishedAt: release.published_at };
    if (project.image) result.image = project.image.endsWith(':latest') ? project.image : project.image + ':' + (name === 'seerr' ? 'v' : '') + version;
    if (name === 'security') {
        const assets = (release.assets || []).filter(asset => new RegExp('^Jellyfin\\.Plugin\\.TwoFactorAuth.*-jf' + jellyfinMajor + '\\.zip$').test(asset.name));
        if (assets.length !== 1 || !/^sha256:[a-f0-9]{64}$/.test(assets[0].digest || '')) throw Error('Latest Security must publish one SHA-256-authenticated jf' + jellyfinMajor + ' asset');
        const asset = assets[0];
        if (!asset.browser_download_url?.startsWith('https://github.com/' + project.repository + '/releases/download/')) throw Error('Unexpected Security asset origin');
        Object.assign(result, { asset: asset.name, downloadUrl: asset.browser_download_url, sha256: asset.digest.slice(7) });
    }
    return result;
}
export async function githubJson(path, request = fetch) {
    const headers = { Accept: 'application/vnd.github+json' };
    if (process.env.GH_TOKEN) headers.Authorization = 'Bearer ' + process.env.GH_TOKEN;
    const response = await request('https://api.github.com/' + path, { headers, signal: AbortSignal.timeout(30000), redirect: 'error' });
    if (!response.ok) throw Error('Upstream release discovery failed (HTTP ' + response.status + ')');
    return response.json();
}
export async function discoverTargets(names, request = fetch, { jellyfin: existingJellyfin } = {}) {
    // Resolve Jellyfin first so Security selection follows the tested server major.
    const jellyfin = existingJellyfin || describeRelease('jellyfin', await githubJson('repos/jellyfin/jellyfin/releases/latest', request));
    const entries = await Promise.all([...new Set(names)].map(async name => {
        if (!projects[name]) throw Error('Unknown test target: ' + name);
        if (name === 'jellyfin') return [name, jellyfin];
        const release = await githubJson('repos/' + projects[name].repository + '/releases/latest', request);
        const target = describeRelease(name, release, { jellyfinMajor: Number(jellyfin.version.split('.')[0]) });
        if (name === 'enhanced') {
            const commit = await githubJson('repos/' + target.repository + '/commits/' + encodeURIComponent(target.tag), request);
            if (!/^[a-f0-9]{40}$/.test(commit.sha)) throw Error('Latest Enhanced release commit unavailable');
            target.sourceCommit = commit.sha;
        }
        return [name, target];
    }));
    return { schemaVersion: 1, policy: 'latest-stable', runId: randomUUID(), resolvedAt: new Date().toISOString(), targets: { jellyfin, ...Object.fromEntries(entries) } };
}
export async function loadTargets(names, { file = process.env.SUITE_TEST_TARGETS, request = fetch } = {}) {
    if (!file && !process.env.SUITE_TEST_TARGETS_JSON) return (await discoverTargets(names, request)).targets;
    const snapshot = JSON.parse(file ? await readFile(file, 'utf8') : process.env.SUITE_TEST_TARGETS_JSON);
    validateSnapshot(snapshot);
    for (const name of names) {
        if (!snapshot.targets?.[name] || snapshot.targets[name].repository !== projects[name]?.repository) throw Error('Missing current-run target: ' + name);
        const target = snapshot.targets[name];
        if (target.version !== releaseVersion(target.tag)) throw Error('Inconsistent current-run target: ' + name);
        if (projects[name].image && target.image !== (projects[name].image.endsWith(':latest') ? projects[name].image : projects[name].image + ':' + (name === 'seerr' ? 'v' : '') + target.version)) throw Error('Unexpected current-run image: ' + name);
    }
    return snapshot.targets;
}
export async function ensureTargets(names, { directory, platform = 'linux/amd64', pull = [] } = {}) {
    let file = process.env.SUITE_TEST_TARGETS;
    let changed = !file;
    const snapshot = file ? JSON.parse(await readFile(file, 'utf8')) : await discoverTargets(names);
    if (file) {
        validateSnapshot(snapshot);
        const missing = names.filter(name => !snapshot.targets[name]);
        if (missing.length) {
            Object.assign(snapshot.targets, (await discoverTargets(missing, fetch, { jellyfin: snapshot.targets.jellyfin })).targets);
            changed = true;
        }
    }
    for (const name of pull) {
        const target = snapshot.targets[name];
        if (!target.imageDigest || target.platform !== platform) {
            snapshot.targets[name] = pullImage(target, platform);
            changed = true;
        }
    }
    file ||= resolve(directory || fileURLToPath(new URL('../artifacts/test-runs/' + snapshot.runId + '/', import.meta.url)), 'targets.json');
    if (changed) await saveTargets(file, snapshot.targets, snapshot);
    process.env.SUITE_TEST_TARGETS = file;
    process.env.SUITE_TEST_RUN_ID = snapshot.runId;
    await loadTargets(names);
    return snapshot.targets;
}
export function validateSnapshot(snapshot, now = Date.now()) {
    const age = now - Date.parse(snapshot.resolvedAt);
    if (snapshot.schemaVersion !== 1 || snapshot.policy !== 'latest-stable' || !/^[a-f0-9-]{36}$/.test(snapshot.runId || '') || !Number.isFinite(age) || age < -60000 || age > 24 * 60 * 60 * 1000)
        throw Error('Invalid or expired per-run release snapshot; no cached-version fallback');
    if (process.env.SUITE_TEST_RUN_ID && snapshot.runId !== process.env.SUITE_TEST_RUN_ID) throw Error('Release snapshot belongs to a different run');
}
export async function saveTargets(file, targets, { runId = randomUUID(), resolvedAt = new Date().toISOString(), tools } = {}) {
    await mkdir(dirname(resolve(file)), { recursive: true });
    const temporary = resolve(file) + '.' + randomUUID() + '.tmp';
    await writeFile(temporary, JSON.stringify({ schemaVersion: 1, policy: 'latest-stable', runId, resolvedAt, targets, ...(tools ? { tools } : {}) }, null, 2) + '\n');
    await rename(temporary, resolve(file));
}
export async function downloadSecurity(target, destination, request = fetch) {
    const response = await request(target.downloadUrl, { signal: AbortSignal.timeout(120000), redirect: 'follow' });
    if (!response.ok) throw Error('Latest Security download failed (HTTP ' + response.status + ')');
    const bytes = Buffer.from(await response.arrayBuffer());
    if (bytes.length > 64 * 1024 * 1024 || createHash('sha256').update(bytes).digest('hex') !== target.sha256) throw Error('Latest Security asset digest mismatch');
    await mkdir(dirname(resolve(destination)), { recursive: true });
    await writeFile(destination, bytes);
    return resolve(destination);
}
export function pullImage(target, platform = 'linux/amd64') {
    execFileSync('docker', ['pull', '--platform', platform, target.image], { stdio: 'pipe', timeout: 300000 });
    const digest = execFileSync('docker', ['image', 'inspect', '--format', '{{index .RepoDigests 0}}', target.image], { encoding: 'utf8' }).trim();
    if (!/@sha256:[a-f0-9]{64}$/.test(digest)) throw Error('Pulled test image has no immutable digest');
    return { ...target, imageDigest: digest, platform };
}
export function jellyfinTarget(latest) {
    const sdk = releaseVersion(latest.tag).split('.'); while (sdk.length < 3) sdk.push('0');
    if (sdk.length !== 3) throw Error('Unsupported Jellyfin SDK version format');
    return { version: sdk.join('.'), tag: latest.version, abi: sdk.join('.') + '.0', image: latest.imageDigest || latest.image };
}
export function requireJellyfinSdk(latest, compiled) {
    if (jellyfinTarget(latest).version !== compiled.version) throw Error('Package SDK does not match this run\'s latest Jellyfin. No older-server fallback is allowed.');
}
export function assertRuntimeVersion(actual, expected, name) {
    const normalize = value => String(value).replace(/^v/, '').split('.').map(Number);
    const a = normalize(actual), b = normalize(expected);
    while (a.length < b.length) a.push(0);
    while (b.length < a.length) b.push(0);
    if (a.some((part, index) => !Number.isInteger(part) || part !== b[index])) throw Error(name + ' runtime does not match the latest stable release: ' + actual + ' != ' + expected);
}
