// Shared local/workflow bump engine. No commits, pushes, application execution or secret loading.
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { declarations, registryLookup, dependencyPaths, checkFreshness, checkLocks, refreshLock } from './check-dependency-freshness.mjs';
import { githubJson } from './latest-releases.mjs';
import { summarizeChanges } from './dependency-summary.mjs';

const hash = text => createHash('sha256').update(text).digest('hex');
const value = row => typeof row.latest === 'string' ? row.latest : row.latest.version;
export function rewriteDependencies(path, text, rows) {
    const find = (kind, name, current) => rows.find(row => row.kind === kind && row.name === name && row.current === current);
    if (/(?:^|\/)package\.json$/.test(path)) {
        const data = JSON.parse(text);
        for (const field of ['dependencies', 'devDependencies', 'optionalDependencies']) {
            for (const [name, current] of Object.entries(data[field] || {})) {
                const row = find('npm', name, current);
                if (row) data[field][name] = (/^[~^]/.exec(current)?.[0] || '') + value(row);
            }
        }
        return JSON.stringify(data) === JSON.stringify(JSON.parse(text)) ? text : JSON.stringify(data, null, 2) + '\n';
    }
    if (path === 'global.json') {
        const data = JSON.parse(text), row = rows.find(row => row.kind === 'sdk');
        if (row) data.sdk.version = value(row);
        return JSON.stringify(data) === JSON.stringify(JSON.parse(text)) ? text : JSON.stringify(data, null, 2) + '\n';
    }
    if (/\.(?:csproj|props|targets)$/.test(path)) {
        return text.replace(/<(?:PackageReference|PackageVersion)\b[^>]*>/g, tag => {
            const name = tag.match(/(?:Include|Update)=["']([^"']+)["']/)?.[1];
            return tag.replace(/Version=(["'])([^"']+)\1/, (match, quote, current) => {
                const row = find('nuget', name, current);
                return row ? 'Version=' + quote + value(row) + quote : match;
            });
        });
    }
    return text.replace(/(uses:\s+)([\w.-]+\/[\w.-]+)@([\w.-]+)(?:[ \t]+#[ \t]*(v?\d+[\w.-]*))?/g,
        (match, prefix, name, current, annotation) => {
            const row = find('action', name, current);
            return row ? prefix + name + '@' + value(row) + (row.latest.tag || annotation ? ' # ' + (row.latest.tag || value(row)) : '') : match;
        })
        .replace(/\b((?:[a-z0-9.-]+\/)?[a-z0-9._-]+\/[a-z0-9._-]+|python|nginx|node):(v?\d+[a-zA-Z0-9_.-]*|alpine)(?:@(sha256:[a-f0-9]{64}))?/g,
            (match, name, current) => {
                const row = find('image', name, current);
                return row ? name + ':' + value(row) + (row.latest.digest ? '@' + row.latest.digest : '') : match;
            })
        .replace(/\b(playwright)@(\d+\.\d+\.\d+)/g, (match, name, current) => {
            const row = find('npm', name, current); return row ? name + '@' + value(row) : match;
        })
        .replace(/(node-version:\s*['"]?)(\d+(?:\.\d+){0,2})/g, (match, prefix, current) => {
            const row = find('node', 'Node.js', current); return row ? prefix + value(row).replace(/^v/, '') : match;
        });
}

export async function planUpdates(paths, lookup) {
    const changes = [], rows = [];
    for (const path of paths) {
        if (!/(?:package\.json|global\.json|Dockerfile|\.(?:csproj|props|targets|ya?ml|mjs|cjs|ps1|sh))$/.test(path)) continue;
        const before = await readFile(path, 'utf8'), items = declarations(path, before);
        if (!items.length) continue;
        for (const row of items) { row.latest = await lookup(row); rows.push(row); }
        const after = rewriteDependencies(path, before, items);
        if (after !== before) changes.push({ path, before, after });
    }
    const markerPath = '.github/upstream-dependencies.json';
    if (paths.includes(markerPath)) {
        const before = await readFile(markerPath, 'utf8'), marker = JSON.parse(before);
        for (const dependency of marker.dependencies) {
            const release = await githubJson('repos/' + dependency.repository + '/releases/latest');
            if (release.draft || release.prerelease || !/^v?\d+(?:\.\d+){1,3}$/.test(release.tag_name)) throw Error('Invalid stable upstream marker');
            dependency.version = release.tag_name.replace(/^v/, '');
        }
        const after = JSON.stringify(marker) === JSON.stringify(JSON.parse(before)) ? before : JSON.stringify(marker, null, 2) + '\n';
        if (before !== after) changes.push({ path: markerPath, before, after });
    }
    // Reject identity, source/configuration and direct dependency-set edits before any writes.
    if (changes.length) summarizeChanges(changes);
    return { changes, rows };
}

export async function updateDependencies({ apply = false, output = 'artifacts/dependency-bump' } = {}) {
    const paths = await dependencyPaths(), lookup = registryLookup();
    const { changes, rows } = await planUpdates(paths, lookup);
    await mkdir(output, { recursive: true });
    const report = { policy: 'latest-stable-fixed-direct-dependency-set', requestedApply: apply, applied: false, verified: false, resolvedAt: new Date().toISOString(),
        declarations: rows.length, changes: changes.map(change => ({ path: change.path, beforeSha256: hash(change.before), afterSha256: hash(change.after) })) };
    await writeFile(resolve(output, 'report.json'), JSON.stringify(report, null, 2) + '\n');
    if (!apply) { console.log('PLAN ' + changes.length + ' files; rerun with --apply to update and resolve locks.'); return; }
    for (const change of changes) if (await readFile(change.path, 'utf8') !== change.before) throw Error('Concurrent edit; bump refused: ' + change.path);
    for (const change of changes) await writeFile(change.path, change.after);
    report.applied = true;
    await writeFile(resolve(output, 'report.json'), JSON.stringify(report, null, 2) + '\n');
    for (const path of paths.filter(path => /(?:^|\/)package-lock\.json$/.test(path) && !/(?:^|\/)(?:node_modules|artifacts|dist)\//.test(path))) {
        const before = await readFile(path, 'utf8');
        refreshLock(dirname(path));
        const after = await readFile(path, 'utf8');
        if (before !== after) changes.push({ path, before, after });
    }
    await writeFile(resolve(output, 'dependency-commit.txt'), changes.length ? summarizeChanges(changes) : 'No dependency version changes.\n');
    const actual = [];
    for (const path of paths) if (/(?:package\.json|global\.json|Dockerfile|\.(?:csproj|props|targets|ya?ml|mjs|cjs|ps1|sh))$/.test(path)) actual.push(...declarations(path, await readFile(path, 'utf8')));
    await checkFreshness(actual, lookup);
    await checkLocks(paths);
    report.verified = true;
    report.changes = changes.map(change => ({ path: change.path, beforeSha256: hash(change.before), afterSha256: hash(change.after) }));
    await writeFile(resolve(output, 'report.json'), JSON.stringify(report, null, 2) + '\n');
    console.log('PASS bump and freshness: ' + actual.length + ' declarations; locks resolved; no commit, push or publication.');
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const unknown = process.argv.slice(2).filter(arg => arg !== '--apply' && !arg.startsWith('--output='));
    if (unknown.length) throw Error('Unsupported bump option');
    await updateDependencies({ apply: process.argv.includes('--apply'), output: process.argv.find(arg => arg.startsWith('--output='))?.slice(9) });
}
