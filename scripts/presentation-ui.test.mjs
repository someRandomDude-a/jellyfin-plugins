import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
const require = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const source = await readFile(new URL('../plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/presentation.js', import.meta.url), 'utf8');
const overlaySource = await readFile(new URL('../plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/overlay-editor.js', import.meta.url), 'utf8');
const template = { text: 'Removal {date} · {quality}', position: 'bottom', bandHeight: .16, fontSize: .055, foreground: '#FFFFFF', background: '#8B2635', opacity: 220 };
const turn = () => new Promise(resolve => setImmediate(resolve));
async function fixture() {
    const dom = new JSDOM('<div id="deletionQueueAdmin"><section id="dqPresentation"><div data-presentation-editor></div><p data-presentation-status></p></section></div>', { url: 'https://fixture.invalid/', runScripts: 'outside-only' });
    const w = dom.window, calls = [], revoked = []; let revision = 7, confirmed = true;
    const create = w.document.createElement.bind(w.document);
    w.document.createElement = (tag, type) => { if (type !== undefined) type.toLowerCase(); return create(tag); };
    w.ApiClient = { getUrl: x => x, accessToken: () => 'fixture' }; w.confirm = () => confirmed;
    w.URL.createObjectURL = () => 'blob:fixture-' + calls.length; w.URL.revokeObjectURL = value => revoked.push(value);
    w.fetch = async (url, options) => {
        calls.push({ url, ...options });
        if (url.endsWith('/preview')) return { ok: true, status: 200, blob: async () => new w.Blob(['png']) };
        if (options.method === 'PUT') revision++;
        return { ok: true, status: url.endsWith('/sync') ? 204 : 200, json: async () => ({ settings: { revision, collectionName: 'Pending removal', collectionEnabled: false, overlaysEnabled: false, portrait: template, landscape: template }, collections: [], artwork: {}, status: 'ready' }) };
    };
    w.eval(overlaySource); w.eval(source); await turn();
    const click = async text => { [...w.document.querySelectorAll('button')].find(b => b.textContent === text).click(); await turn(); };
    return { w, calls, revoked, click, confirm: value => { confirmed = value; }, close: () => dom.window.close() };
}
test('presentation editor previews both layouts and revokes blobs on refresh and navigation', async t => {
    const f = await fixture(); t.after(f.close);
    assert([...f.w.document.querySelectorAll('input[is],select[is]')].every(input => !input.classList.contains(input.getAttribute('is'))), 'Native initialization marker classes must not be set before Jellyfin upgrades the controls');
    await f.click('Preview portrait'); await f.click('Preview landscape');
    assert.equal(f.w.document.querySelectorAll('img:not([hidden])').length, 2);
    assert.deepEqual(f.calls.filter(c => c.url.endsWith('/preview')).map(c => JSON.parse(c.body).portrait), [true, false]);
    await f.click('Sync / restore now'); assert.equal(f.calls.find(c => c.url.endsWith('/sync')).body, '{}'); assert.equal(f.revoked.length, 2);
    await f.click('Preview portrait'); f.w.document.querySelector('#deletionQueueAdmin').dispatchEvent(new f.w.Event('viewhide'));
    assert.equal(f.revoked.length, 3); assert.equal(f.w.document.querySelector('[data-presentation-editor]').childElementCount, 0);
});
test('presentation changes require confirmation and preserve the loaded revision', async t => {
    const f = await fixture(); t.after(f.close);
    f.w.document.querySelectorAll('input[type=checkbox]')[1].checked = true;
    f.confirm(false); await f.click('Save collection and overlay settings'); assert.equal(f.calls.filter(c => c.method === 'PUT').length, 0);
    f.confirm(true); await f.click('Save collection and overlay settings');
    const saved = JSON.parse(f.calls.find(c => c.method === 'PUT').body); assert.equal(saved.settings.revision, 7); assert.equal(saved.confirmChanges, true); assert.equal(saved.settings.overlaysEnabled, true);
});
test('presentation editor aborts late loading when the view closes', async t => {
    const f = await fixture(); t.after(f.close); const page = f.w.document.querySelector('#deletionQueueAdmin');
    page.dispatchEvent(new f.w.Event('viewhide')); let release, signal;
    f.w.fetch = async (_, options) => { signal = options.signal; await new Promise(resolve => { release = resolve; }); return { ok: true, json: async () => ({}) }; };
    page.dispatchEvent(new f.w.Event('viewshow')); page.dispatchEvent(new f.w.Event('viewhide')); assert.equal(signal.aborted, true); release(); await turn();
    assert.equal(f.w.document.querySelector('[data-presentation-editor]').childElementCount, 0);
});
test('presentation editor ignores an older view response after reopening', async t => {
    const f = await fixture(); t.after(f.close); const page = f.w.document.querySelector('#deletionQueueAdmin'), normal = f.w.fetch;
    page.dispatchEvent(new f.w.Event('viewhide')); let release, first = true;
    f.w.fetch = async (...args) => { if (!first) return normal(...args); first = false; await new Promise(resolve => { release = resolve; }); return { ok: true, json: async () => ({}) }; };
    page.dispatchEvent(new f.w.Event('viewshow')); page.dispatchEvent(new f.w.Event('viewhide')); page.dispatchEvent(new f.w.Event('viewshow')); await turn();
    release(); await turn();
    assert(f.w.document.querySelector('[data-presentation-editor] form'));
    assert.match(f.w.document.querySelector('[data-presentation-status]').textContent, /^ready/);
});
test('Canvas editor supports layer history, pointer movement, keyboard nudging and serialization', async t => {
    const dom=new JSDOM('<main></main>',{runScripts:'outside-only',url:'https://fixture.invalid/'}),w=dom.window;t.after(()=>w.close());w.eval(overlaySource);
    const studio=w.DeletionQueueOverlayEditor.mount(w.document.querySelector('main'),template,true),root=w.document.querySelector('.dq-canvas-editor');
    const click=text=>[...root.querySelectorAll('button')].find(b=>b.textContent===text).click();
    assert.equal(studio.read().layers.length,2);click('Add text');assert.equal(studio.read().layers.length,3);click('Duplicate');assert.equal(studio.read().layers.length,4);click('Delete layer');assert.equal(studio.read().layers.length,3);click('Undo');assert.equal(studio.read().layers.length,4);click('Redo');
    const canvas=root.querySelector('.dq-canvas-artboard');canvas.getBoundingClientRect=()=>({left:0,top:0,width:400,height:600});
    root.querySelector('.dq-canvas-selected').dispatchEvent(new w.MouseEvent('pointerdown',{bubbles:true,button:0,clientX:50,clientY:50}));canvas.dispatchEvent(new w.MouseEvent('pointermove',{bubbles:true,clientX:70,clientY:110}));canvas.dispatchEvent(new w.MouseEvent('pointerup',{bubbles:true}));
    assert.equal(studio.read().layers.at(-1).x,.15);assert.equal(studio.read().layers.at(-1).y,.2);
    canvas.dispatchEvent(new w.KeyboardEvent('keydown',{bubbles:true,key:'ArrowRight',shiftKey:true}));assert.equal(studio.read().layers.at(-1).x,.16);click('Undo');assert.equal(studio.read().layers.at(-1).x,.15);
    studio.setSample('2026-11-01');assert.match(canvas.textContent,/2026-11-01/);studio.dispose();assert.equal(w.document.querySelector('.dq-canvas-editor'),null);
});
