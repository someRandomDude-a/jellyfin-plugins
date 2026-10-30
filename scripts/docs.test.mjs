import test from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile, access } from 'node:fs/promises';

test('root, current guides, module/plugin READMEs and retained contract links resolve locally', async () => {
    const root = new URL('../', import.meta.url);
    const files = ['README.md', '.github/README.md'];
    async function collect(directory) {
        let entries;
        try { entries = await readdir(new URL(directory + '/', root), { withFileTypes: true }); }
        catch (error) { if (error.code === 'ENOENT') return; throw error; }
        for (const entry of entries) {
            const path = directory + '/' + entry.name;
            if (entry.isDirectory()) await collect(path);
            else if (entry.isFile() && entry.name.endsWith('.md')) files.push(path);
        }
    }
    await collect('docs');
    for (const owner of ['plugins', 'modules']) {
        for (const entry of await readdir(new URL(owner + '/', root), { withFileTypes: true })) {
            if (!entry.isDirectory()) continue;
            const directory = owner + '/' + entry.name;
            try { await access(new URL(directory + '/README.md', root)); files.push(directory + '/README.md'); }
            catch (error) { if (error.code !== 'ENOENT') throw error; }
            await collect(directory + '/docs');
            await collect(directory + '/contracts');
        }
    }
    files.push('plugins/deletion-queue/test-infrastructure/README.md', 'plugins/deletion-queue/manifests/README.md',
        'plugins/private-syncplay/examples/WatchPartyConsumer/README.md');
    const broken = [];
    for (const file of files) {
        const url = new URL(file, root), text = await readFile(url, 'utf8');
        for (const match of text.matchAll(/\[[^\]]*\]\(([^\s)]+)(?:\s+"[^"]*")?\)/g)) {
            const href = match[1];
            if (/^(?:[a-z]+:|#|\/\/)/i.test(href)) continue;
            try { await access(new URL(href.split('#')[0], url)); }
            catch { broken.push(file + ': ' + href); }
        }
    }
    assert.deepEqual(broken, []);
});
