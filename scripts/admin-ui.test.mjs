import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
const require = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const source = await readFile(new URL('../shared/Web/admin-ui.js', import.meta.url), 'utf8');
const turn = () => new Promise(resolve => setImmediate(resolve));
function fixture(html) {
    const dom = new JSDOM('<main class="suiteAdmin">' + html + '</main>', { runScripts: 'outside-only' });
    dom.window.eval(source); return dom;
}
test('Help supports hover, keyboard, touch and Escape without toggling settings', () => {
    const dom = fixture('<div data-help="Useful detail"><label><input type="checkbox">Allow guests</label></div>');
    const { document, MouseEvent, KeyboardEvent } = dom.window;
    const help = document.querySelector('.suiteHelp'), button = help.querySelector('button'), tip = help.querySelector('[role=tooltip]');
    assert(tip.hidden); assert.equal(button.type, 'button'); assert.equal(button.closest('label'), null);
    help.dispatchEvent(new MouseEvent('mouseenter')); assert(!tip.hidden);
    help.dispatchEvent(new MouseEvent('mouseleave')); assert(tip.hidden);
    button.focus(); assert(!tip.hidden); button.click(); assert(!tip.hidden);
    button.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); assert(tip.hidden);
    assert(!document.querySelector('input').checked); dom.window.close();
});
test('Late fields and diagnostic updates enhance once and retain text safely', async () => {
    const dom = fixture('<pre id="details">{"text":"&lt;img src=x onerror=bad()&gt;"}</pre>');
    const { document } = dom.window, root = document.querySelector('main'), pre = document.querySelector('pre');
    assert(pre.querySelector('code.language-json')); assert.equal(pre.querySelector('img'), null);
    const control = document.createElement('div'); control.dataset.help = 'New help'; control.textContent = 'Setting'; root.append(control); await turn();
    assert.equal(control.querySelectorAll('.suiteHelp').length, 1);
    dom.window.SuiteAdminUI.mount(); await turn(); assert.equal(control.querySelectorAll('.suiteHelp').length, 1);
    pre.textContent = '{"count":2}'; await turn(); assert.equal(pre.querySelector('code').textContent, '{\n  "count": 2\n}');
    root.dispatchEvent(new dom.window.Event('viewhide')); assert(root.querySelector('[role=tooltip]').hidden);
    dom.window.close();
});
test('JSON formatting preserves invalid drafts and reports valid formatting', () => {
    const dom = fixture('<textarea data-json>{"a":1}</textarea>');
    const { document } = dom.window, editor = document.querySelector('textarea'), button = document.querySelector('.suiteCodeTools button');
    let changes = 0; editor.addEventListener('input', () => changes++); button.click();
    assert.equal(editor.value, '{\n  "a": 1\n}'); assert.equal(changes, 1);
    editor.value = '{invalid'; button.click(); assert.equal(editor.value, '{invalid'); assert.match(document.querySelector('[role=status]').textContent, /Invalid JSON/);
    dom.window.close();
});
test('Each installable admin page loads its independently embedded presentation', async () => {
    const pages = [
        ['account-hub/src/AccountHub/Configuration/configPage.html', 'AccountHub'],
        ['notifications/src/Jellyfin.Plugin.Notifications/Web/admin.html', 'Notifications'],
        ['discord-bridge/src/Jellyfin.Plugin.DiscordBridge/Web/settings.html', 'DiscordBridge'],
        ['deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/admin.html', 'DeletionQueue'],
        ['private-syncplay/src/Jellyfin.Plugin.PrivateSyncPlay/Web/settings.html', 'PrivateSyncPlay']
    ];
    for (const [path, name] of pages) {
        const html = await readFile(new URL('../plugins/' + path, import.meta.url), 'utf8');
        assert.match(html, /suiteAdmin/); assert(html.includes('configurationpage?name=' + name + 'AdminUI.js'));
    }
});
