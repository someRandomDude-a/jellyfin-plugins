// Read candidate data only. Never execute code from the dependency-update branch.
import { execFileSync } from 'node:child_process';
import { writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

const dockerPattern = /\b((?:[a-z0-9.-]+\/)?[a-z0-9._-]+\/[a-z0-9._-]+|python|nginx|node):(v?\d+[a-zA-Z0-9_.-]*|alpine)(@sha256:[a-f0-9]{64})?/g;
const jsonDependencyFields = ['dependencies', 'devDependencies', 'peerDependencies', 'optionalDependencies', 'engines', 'resolutions', 'overrides'];
export function dependencyInventory(path, text) {
    const entries = new Map();
    const add = (kind, name, value, location = name) => {
        if (typeof value !== 'string' || value.length > 500 || /[\r\n]|:\/\/[^/]*@|[?&](?:token|secret|password|auth)=/i.test(value)) throw Error('Unsafe dependency descriptor in ' + path);
        let key = kind + ':' + location, occurrence = 1;
        while (entries.has(key)) key = kind + ':' + location + ':' + occurrence++;
        entries.set(key, { kind, name, value, path });
        return '<dependency-version>';
    };
    let normalized = text;
    if (path === '.github/upstream-dependencies.json') {
        const json = JSON.parse(text);
        for (const item of json.dependencies) item.version = add('upstream', item.repository, item.version);
        normalized = JSON.stringify(json);
    } else if (/(?:^|\/)package(?:-lock)?\.json$/.test(path) || /(?:^|\/)npm-shrinkwrap\.json$/.test(path)) {
        const json = JSON.parse(text);
        if (path.endsWith('package.json')) {
            const replace = (value, field, trail = '') => {
                for (const [name, spec] of Object.entries(value || {})) {
                    if (typeof spec === 'string') value[name] = add('npm', name, spec, field + ':' + trail + name);
                    else if (spec && typeof spec === 'object') replace(spec, field, trail + name + '/');
                }
            };
            for (const field of jsonDependencyFields) if (json[field]) replace(json[field], field);
            normalized = JSON.stringify(json);
        } else {
            if (!json.packages || !Number.isInteger(json.lockfileVersion)) throw Error('Unsupported npm lockfile; cannot provide a complete dependency summary');
            for (const [location, item] of Object.entries(json.packages)) {
                if (!location || !item.version) continue;
                const name = item.name || location.split('node_modules/').at(-1);
                add('npm-lock', name, item.version, location);
                if (item.integrity) add('npm-integrity', name, item.integrity, location);
            }
            // npm owns resolved URLs, integrity and dependency edges. Never run
            // lifecycle scripts while updating/installing these manifests.
            normalized = '<npm-lockfile>';
        }
    } else if (/(?:^|\/)global\.json$/.test(path)) {
        const json = JSON.parse(text);
        json.sdk.version = add('dotnet-sdk', '.NET SDK', json.sdk.version);
        normalized = JSON.stringify(json);
    } else if (/\.(?:csproj|props|targets)$/.test(path)) {
        normalized = text.replace(/<(?:PackageReference|PackageVersion)\b[^>]*>/g, tag => {
            const name = tag.match(/(?:Include|Update)="([^"]+)"/)?.[1];
            if (!name) return tag;
            return tag.replace(/Version="([^"$]+)"/, (match, version) => 'Version="' + add('nuget', name, version) + '"');
        }).replace(/<([A-Za-z0-9_]*Version)>(\d+(?:\.\d+){1,3})<\/\1>/g, (match, name, version) => {
            if (['Version', 'AssemblyVersion', 'FileVersion', 'PluginVersion'].includes(name)) return match;
            return '<' + name + '>' + add('nuget-property', name, version) + '</' + name + '>';
        });
    } else if (/\.(?:ya?ml|mjs|cjs|ps1|sh)$/.test(path) || /(?:^|\/)Dockerfile$/.test(path)) {
        normalized = text.replace(/(uses:\s+)([\w.-]+\/[\w./-]+)@([\w./-]+)(?:[ \t]+#[ \t]*(v?\d+[\w.-]*))?/g, (match, prefix, name, version, annotation) => prefix + name + '@' + add('github-action', name, version + (annotation ? ' (' + annotation + ')' : '')))
            .replace(dockerPattern, (match, name, version, digest) => name + ':' + add('docker', name, version + (digest || '')))
            .replace(/\b(playwright)@(\d+\.\d+\.\d+)/g, (match, name, version) => name + '@' + add('npm-tool', name, version))
            .replace(/(node-version:\s*['"]?)(\d+(?:\.\d+){0,2})(['"]?)/g, (match, prefix, version, suffix) => prefix + add('node', 'Node.js', version) + suffix);
    } else throw Error('Automatic dependency update changes an unsupported file: ' + path);
    return { entries, normalized };
}
export function nextDependencyRelease(tags, commit) {
    const versions = tags.map(tag => ({ ...tag, parts: /(?:^|\/)v(\d+)\.(\d+)\.(\d+)$/.exec(tag.name)?.slice(1).map(Number) })).filter(tag => tag.parts);
    const compare = (a, b) => a.parts[0] - b.parts[0] || a.parts[1] - b.parts[1] || a.parts[2] - b.parts[2];
    const existing = versions.filter(tag => /^v/.test(tag.name) && tag.commit === commit).sort(compare).at(-1);
    if (existing) return existing.name;
    const latest = versions.sort(compare).at(-1)?.parts || [0, 0, 0];
    if (latest.some(part => part >= 65534)) throw Error('Automatic patch version would exceed assembly limits');
    return 'v' + [latest[0], latest[1], latest[2] + 1].join('.');
}
export function summarizeChanges(files) {
    const lines = [];
    for (const { path, before, after } of files) {
        if (!before || !after) throw Error('Automatic dependency updates may not add or delete project files: ' + path);
        const old = dependencyInventory(path, before), next = dependencyInventory(path, after);
        if (old.normalized !== next.normalized) throw Error('Non-dependency content changed; automatic promotion refused: ' + path);
        let changes = 0;
        for (const key of new Set([...old.entries.keys(), ...next.entries.keys()])) {
            const a = old.entries.get(key), b = next.entries.get(key);
            if (a?.value === b?.value) continue;
            const entry = b || a;
            // A version change already identifies this transitive dependency;
            // only report an additional integrity line for same-version changes.
            if (entry.kind === 'npm-integrity') {
                const versionKey = key.replace(/^npm-integrity:/, 'npm-lock:');
                if (old.entries.get(versionKey)?.value !== next.entries.get(versionKey)?.value) continue;
            }
            lines.push(`- ${entry.kind} ${entry.name}: ${a?.value || '(added)'} -> ${b?.value || '(removed)'} [${path}]`);
            changes++;
        }
        if (!changes && /(?:package-lock|npm-shrinkwrap)\.json$/.test(path)) lines.push('- npm lockfile metadata refreshed (versions unchanged) [' + path + ']');
        else if (!changes) throw Error('No dependency changes found in changed file: ' + path);
    }
    if (!lines.length) throw Error('No dependency updates to promote');
    return 'chore(deps): update project dependencies\n\n' + [...new Set(lines)].sort().join('\n') + '\n\nDependency-Maintenance: true\n';
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const option = name => process.argv.find(arg => arg.startsWith('--' + name + '='))?.slice(name.length + 3);
    const base = option('base'), head = option('head');
    if (![base, head].every(ref => /^[a-f0-9]{40}$/.test(ref || ''))) throw Error('Expected exact base/head commit SHAs');
    const git = args => execFileSync('git', args, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
    const paths = git(['diff', '--name-only', '-z', base, head]).split('\0').filter(Boolean);
    const files = paths.map(path => ({ path, before: git(['show', base + ':' + path]), after: git(['show', head + ':' + path]) }));
    await writeFile(option('output') || 'dependency-commit.txt', summarizeChanges(files));
}
