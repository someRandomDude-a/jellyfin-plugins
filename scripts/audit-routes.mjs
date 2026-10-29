// Source trace inventory, not a substitute for executing authorization tests.
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { dependencyPaths } from './check-dependency-freshness.mjs';

const lineAt = (text, index) => text.slice(0, index).split('\n').length;
export function traceRoutes(path, text) {
    const routes = [], classes = [...text.matchAll(/((?:\[[^\]]+\]\s*)*)(?:public|internal)\s+(?:(?:sealed|abstract|partial)\s+)*class\s+(\w+)/g)];
    for (const attribute of text.matchAll(/((?:\[[^\]]+\]\s*)+)public\s+(?:async\s+)?[\w<>,?.\[\]]+\s+(\w+)\s*\(/g)) {
        if (!/\b(?:Http(?:Get|Post|Put|Patch|Delete|Head|Options)|Route|AcceptVerbs)\b/.test(attribute[1])) continue;
        const type = classes.filter(item => item.index < attribute.index).at(-1);
        if (!type) throw Error('Unrecognized HTTP action; manual trace required: ' + path + ':' + lineAt(text, attribute.index));
        const nextType = classes.find(item => item.index > type.index)?.index ?? text.length;
        const typeSource = text.slice(type.index, nextType);
        const guards = [...new Set([...typeSource.matchAll(/\b(?:SuiteDiagnostics\.Endpoint|NativeAuthorization\.RequireAsync|RequireAdministratorAsync|CurrentUserAsync|AuthorizeAsync|CheckAnonymous|CheckOrigin|RequireSessionAsync|RequireResetSessionAsync|Administrator|Require|User|Auth|Actor|Guest|ReceiveAsync|Forward)\s*\(/g)].map(match => match[0].replace(/\s*\($/, '')))];
        const typeRoute = type[1].match(/\bRoute\("([^"]*)"\)/)?.[1] || '';
        const verbs = [...attribute[1].matchAll(/\bHttp(Get|Post|Put|Patch|Delete|Head|Options)(?:\("([^"]*)"\))?/g)].map(match => ({ method: match[1].toUpperCase(), route: match[2] }));
        const actionRoutes = [...attribute[1].matchAll(/\bRoute\("([^"]*)"\)/g)].map(match => match[1]);
        const accepted = attribute[1].match(/AcceptVerbs\(([^)]+)\)/)?.[1];
        if (!verbs.length) for (const method of accepted ? [...accepted.matchAll(/"([A-Z]+)"/g)].map(match => match[1]) : ['ANY']) verbs.push({ method });
        for (const verb of verbs) for (const actionRoute of verb.route !== undefined ? [verb.route] : actionRoutes.length ? actionRoutes : ['']) {
            routes.push({ kind: 'mvc', path, line: lineAt(text, attribute.index), controller: type[2], action: attribute[2], method: verb.method,
                route: actionRoute.startsWith('/') || actionRoute.startsWith('~/') ? actionRoute.replace(/^~/, '') : '/' + [typeRoute, actionRoute].filter(Boolean).join('/'),
                classAttributes: type[1].trim(), actionAttributes: attribute[1].trim(), guardTraceCandidates: guards,
                verification: 'Trace the named guards and execute role/origin/ownership/replay tests; attributes alone do not certify access.' });
        }
    }
    for (const match of text.matchAll(/\b(\w+)\.Map(Get|Post|Put|Patch|Delete)\(([^,\n]+)/g)) {
        routes.push({ kind: 'minimal-handler', path, line: lineAt(text, match.index), group: match[1], method: match[2].toUpperCase(), routeExpression: match[3].trim(),
            verification: 'Module template; follow host mapping, group RequireAuthorization and handler-native User/Manage/producer-token checks.' });
    }
    return routes;
}
export async function routeInventory(paths) {
    const routes = [], excluded = [];
    for (const path of paths) {
        if (!/^(?:plugins\/[^/]+\/src\/|modules\/(?:security|notifications|user-management)\/src\/).*\.cs$/.test(path)&&path!=='shared/Jellyfin.NativeUi/SuiteDiagnostics.cs') continue;
        const text = await readFile(path, 'utf8');
        if (!/\b(?:Http(?:Get|Post|Put|Patch|Delete|Head|Options)|Route|AcceptVerbs)\b|\.Map(?:Get|Post|Put|Patch|Delete)\(/.test(text)) continue;
        if (path.startsWith('modules/user-management/') && /\/(?:Api|Discord|FileTransformation)\//.test(path)) {
            excluded.push({ path, reason: 'Legacy lifecycle controller excluded by Compile Remove in its active csproj.' }); continue;
        }
        routes.push(...traceRoutes(path, text));
    }
    return { scope: 'Production plugin/module source; example plugins and excluded legacy lifecycle routes are not shipped surfaces.',
        note: 'Static source trace only. Does not assert runtime coverage or authorization correctness.', routes, excluded };
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const file = resolve(process.argv.find(arg => arg.startsWith('--output='))?.slice(9) || 'artifacts/route-audit/routes.json');
    const inventory = await routeInventory(await dependencyPaths());
    if (!inventory.routes.length) throw Error('No routes found; refusing empty audit');
    await mkdir(dirname(file), { recursive: true }); await writeFile(file, JSON.stringify(inventory, null, 2) + '\n');
    console.log('TRACE ' + inventory.routes.length + ' MVC actions/minimal templates; ' + inventory.excluded.length + ' inactive legacy files excluded. ' + file);
}
