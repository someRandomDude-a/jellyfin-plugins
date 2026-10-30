import test from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile, access } from 'node:fs/promises';
import { publishedPlugins, publishedRuntimes, publishedSummary } from './published-releases.mjs';

test('root, current guides, module/plugin READMEs and retained contract links resolve locally', async () => {
    const root = new URL('../', import.meta.url);
    const files = ['README.md'];
    async function collect(directory) {
        let entries;
        try { entries = await readdir(new URL(directory + '/', root), { withFileTypes: true }); }
        catch (error) { if (error.code === 'ENOENT') return; throw error; }
        for (const entry of entries) {
            const path = directory + '/' + entry.name;
            if (entry.isDirectory()&&!['artifacts','node_modules','bin','obj','.git'].includes(entry.name)) await collect(path);
            else if (entry.isFile() && entry.name.endsWith('.md')) files.push(path);
        }
    }
    await collect('docs');
    await collect('.github');await collect('plugins');await collect('modules');
    const broken = [];
    for (const file of files) {
        const url = new URL(file, root), text = await readFile(url, 'utf8');
        const links=[...text.matchAll(/\[[^\]]*\]\(([^\s)]+)(?:\s+"[^"]*")?\)/g),...text.matchAll(/^\[[^\]]+\]:\s*(\S+)/gm)];
        for (const match of links) {
            const href = match[1];
            if (/^(?:[a-z]+:|\/\/)/i.test(href)) continue;
            try { await access(new URL(href.split('#')[0], url)); }
            catch { broken.push(file + ': ' + href); }
        }
        const definitions=new Set([...text.matchAll(/^\[([^\]]+)\]:\s*\S+/gm)].map(m=>m[1].toLowerCase()));
        for(const match of text.matchAll(/!?\[[^\]\n]*\]\[([^\]\n]+)\]/g))if(!definitions.has(match[1].toLowerCase()))broken.push(file+': undefined link '+match[1]);
    }
    assert.deepEqual(broken, []);
});

const fixtureVersion=(version='0.9.0.0',tag='v'+version.split('.').slice(0,3).join('.'))=>({version,targetAbi:'12.4.0.0',sourceUrl:'https://github.com/owner/repository/releases/download/'+encodeURIComponent(tag)+'/plugin.zip'});
const fixtureCatalogs=()=>Object.fromEntries(publishedRuntimes.map(runtime=>[runtime,Object.entries(publishedPlugins).map(([name,guid])=>({name,guid,versions:[fixtureVersion()]}))]));
test('published metadata selects each plugin independently by numeric version without rewriting catalogs',()=>{
    const catalogs=fixtureCatalogs();for(const runtime of publishedRuntimes)catalogs[runtime][0].versions.push(fixtureVersion('0.11.0.0','account-hub/v0.11.0'));
    const summary=publishedSummary(catalogs,'owner/repository');assert.equal(Object.keys(summary).length,6);assert.equal(summary.AccountHub.version,'0.11.0.0');assert.equal(summary.Notifications.version,'0.9.0.0');assert.equal(summary.AccountHub.releaseUrl,'https://github.com/owner/repository/releases/tag/account-hub%2Fv0.11.0');assert.equal(catalogs['linux-x64'][0].versions[0].version,'0.9.0.0');
});
test('published metadata rejects inconsistent platforms, duplicate identities and unrelated asset origins',()=>{
    for(const mutate of [c=>c['win-x64'][0].versions[0].version='0.8.0.0',c=>c['win-x64'][0].versions[0].targetAbi='12.3.0.0',c=>c['linux-x64'].push(c['linux-x64'][0]),c=>c['linux-x64'][0].versions[0].sourceUrl='https://example.test/plugin.zip']){const catalogs=fixtureCatalogs();mutate(catalogs);assert.throws(()=>publishedSummary(catalogs,'owner/repository'));}
    assert.deepEqual(publishedSummary(Object.fromEntries(publishedRuntimes.map(r=>[r,[]])),'owner/repository'),{});
});
test('README release badges query version and ABI for every published plugin without positional catalog indexes',async()=>{
    const text=await readFile(new URL('../README.md',import.meta.url),'utf8');const badges=[...text.matchAll(/^\[[^\]]+\]: (https:\/\/img\.shields\.io\/badge\/dynamic\/json\?\S+)/gm)].map(m=>new URL(m[1]));
    assert.equal(badges.length,12);for(const name of Object.keys(publishedPlugins))for(const field of ['version','targetAbi'])assert(badges.some(url=>url.searchParams.get('query')==='$.'+name+'.'+field));
    for(const url of badges)assert.equal(url.searchParams.get('url'),'https://raw.githubusercontent.com/someRandomDude-a/jellyfin-plugins/catalog/releases.json');
});
