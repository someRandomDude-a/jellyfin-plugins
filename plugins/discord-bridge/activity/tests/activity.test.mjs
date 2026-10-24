import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';
import { createHash } from 'node:crypto';
import { invitation, mountActivity, serverBase } from '../src/activity.mjs';
const html = await readFile(new URL('../../src/Jellyfin.Plugin.DiscordBridge/Web/activity.html', import.meta.url), 'utf8');
const config = { available: true, applicationId: '12345', publicServerUrl: 'https://fixture.example/suite' };
const token = 'a'.repeat(43), link = config.publicServerUrl + '/PrivateSyncPlay/Watch#' + token;
const tick = () => new Promise(resolve => setImmediate(resolve));
function fixture(overrides = {}) {
    const dom = new JSDOM(html, { url: 'https://12345.discordsays.com/suite/DiscordBridge/activity?frame_id=test' });
    const root = dom.window.document.querySelector('main'), calls = [], sdk = { ready: async () => {}, commands: { openExternalLink: async value => { calls.push(value); return { opened: true }; } }, close: () => {} };
    let sdkOptions;
    const ready = mountActivity(root, { createSdk: (id, options) => { assert.equal(id, config.applicationId); sdkOptions = options; return sdk; }, fetchConfig: async () => config, location: dom.window.location, ...overrides });
    return { dom, root, calls, sdk, ready, options: () => sdkOptions, input: root.querySelector('input'), status: root.querySelector('[data-status]'), open: root.querySelector('[data-open]'), join: root.querySelector('[data-join]') };
}
test('Accepts only exact configured HTTPS server/BaseUrl and native invitation route', () => {
    assert.equal(invitation(link, config.publicServerUrl), link);
    for (const value of [link.replace('fixture.example', 'evil.example'), link.replace('/suite/', '/suite-other/'), link.replace('/Watch#', '/Player#'), link.replace('/Watch#', '/Watch?token=x#'), 'javascript:alert(1)', 'https://fixture.example/suite/PrivateSyncPlay/Watch#short']) assert.throws(() => invitation(value, config.publicServerUrl));
    for (const value of ['http://fixture.example', 'https://user:secret@fixture.example', 'https://fixture.example?secret=x']) assert.throws(() => serverBase(value));
});
test('Official SDK handshake disables console forwarding and no actions run before explicit click', async () => {
    const view = fixture(); await view.ready;
    assert.deepEqual(view.options(), { disableConsoleLogOverride: true }); assert.equal(view.calls.length, 0);
    view.open.click(); await tick();
    assert.deepEqual(view.calls, [{ url: 'https://fixture.example/suite/web/index.html' }]);
    (await view.ready).dispose(); view.dom.window.close();
});
test('Invitation handoff erases the retained input immediately, does not redeem or replay', async () => {
    const view = fixture(); await view.ready; view.input.value = link; view.join.click();
    assert.equal(view.input.value, ''); await tick(); assert.deepEqual(view.calls, [{ url: link }]);
    assert.equal(view.dom.window.localStorage.length, 0); assert.equal(view.dom.window.sessionStorage.length, 0);
    (await view.ready).dispose(); view.dom.window.close();
});
test('Invalid invitation is cleared without SDK/network action or payload in errors', async () => {
    const view = fixture(); await view.ready; view.input.value = 'https://evil.example/#private'; view.join.click();
    assert.equal(view.input.value, ''); assert.equal(view.calls.length, 0); assert(!view.status.textContent.includes('private'));
    (await view.ready).dispose(); view.dom.window.close();
});
test('Unavailable dependency and SDK timeout fail closed with repair instructions', async () => {
    for (const options of [{ fetchConfig: async () => ({ available: false }) }, { createSdk: () => ({ ready: () => new Promise(() => {}), close() {} }), timeoutMs: 5 }]) {
        const view = fixture(options); await view.ready;
        assert(view.open.disabled); assert(view.join.disabled); assert.match(view.status.textContent, /administrator/); assert.equal(view.calls.length, 0);
        (await view.ready).dispose(); view.dom.window.close();
    }
});
test('Page disposal aborts a pending mount and rejects late SDK/config results', async () => {
    let resolve; const signal = new AbortController();
    const view = fixture({ signal: signal.signal, fetchConfig: () => new Promise(done => { resolve = done; }) });
    view.input.value = link; signal.abort(); await view.ready; resolve(config); await tick();
    assert.equal(view.input.value, ''); assert(view.open.disabled); assert.equal(view.options(), undefined); view.dom.window.close();
});
test('Uncertain external open is reported once without automatic retry', async () => {
    const view = fixture(); await view.ready;
    view.sdk.commands.openExternalLink = async value => { view.calls.push(value); throw Error('private-secret'); };
    view.input.value = link; view.join.click(); await tick();
    assert.equal(view.calls.length, 1); assert.match(view.status.textContent, /No invitation was redeemed/); assert(!view.status.textContent.includes('private-secret'));
    (await view.ready).dispose(); view.dom.window.close();
});
test('Native labeled controls and scoped layout do not introduce a fixed palette', async () => {
    const view = fixture(); await view.ready;
    assert.equal(view.input.labels.length, 1); assert(view.input.classList.contains('emby-input')); assert(view.open.classList.contains('emby-button'));
    const css = await readFile(new URL('../../src/Jellyfin.Plugin.DiscordBridge/Web/activity.css', import.meta.url), 'utf8');
    assert(!/#[0-9a-f]{3,8}\b|\brgba?\(|font-family\s*:/i.test(css));
    (await view.ready).dispose(); view.dom.window.close();
});
test('Concurrent clicks cannot duplicate an uncertain external handoff', async () => {
    const view = fixture(); await view.ready;
    let complete;
    view.sdk.commands.openExternalLink = value => { view.calls.push(value); return new Promise(resolve => { complete = resolve; }); };
    view.input.value = link; view.join.click(); view.open.click(); view.join.click();
    assert.equal(view.calls.length, 1); assert(view.join.disabled); assert.equal(view.input.value, '');
    complete({ opened: true }); await tick(); assert(!view.open.disabled);
    (await view.ready).dispose(); view.dom.window.close();
});
test('Launch bearer fragments are erased without being accepted as invitations', async () => {
    const replacements = [];
    const view = fixture({ location: { pathname: '/activity/', search: '?frame_id=test', hash: '#' + token, replaceState: (...args) => replacements.push(args) } });
    await view.ready; assert.deepEqual(replacements, [[null, '', '/activity/?frame_id=test']]);
    assert.equal(view.input.value, ''); assert.equal(view.calls.length, 0);
    (await view.ready).dispose(); view.dom.window.close();
});
test('Shipped JavaScript hash and SDK prebundled runtime dependency licenses are inventoried', async () => {
    const inventory = JSON.parse(await readFile(new URL('../dist/activity-inventory.json', import.meta.url), 'utf8'));
    const bytes = await readFile(new URL('../dist/activity.js', import.meta.url));
    assert.equal(inventory.bundleSha256, createHash('sha256').update(bytes).digest('hex'));
    const notices = await readFile(new URL('../dist/embedded-app-sdk.LICENSE', import.meta.url), 'utf8');
    for (const name of ['@discord/embedded-app-sdk', 'big-integer', 'decimal.js-light', 'eventemitter3', 'lodash.transform', 'uuid', 'zod']) {
        assert(inventory.packages.some(item => item.name === name && item.version && item.license));
        assert(notices.includes(name));
    }
    assert(!inventory.packages.some(item => ['esbuild', 'jsdom'].includes(item.name) || item.name.startsWith('@types/')));
});
