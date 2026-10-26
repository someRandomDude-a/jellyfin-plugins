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
        ['automatic-deletion-requests/src/Jellyfin.Plugin.AutomaticDeletionRequests/Web/admin.html', 'AutomaticDeletionRequests'],
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
test('Integration badges use native versions, distinguish missing providers and link real task cycles', async () => {
    const dom=new JSDOM('<main class="suiteAdmin" data-suite-plugin="automatic"><h1>Rules</h1><div data-suite-jobs></div></main>',{runScripts:'outside-only',url:'https://fixture.invalid/web/'}),w=dom.window,calls=[];
    w.ApiClient={getUrl:p=>p,accessToken:()=> 'fixture-token'};
    w.fetch=async(path,options)=>{calls.push({path,options});return {ok:true,json:async()=>path==='Plugins'?[{Id:'a6429fb2-c67c-459d-b6a7-579c4a05dace',Version:'0.2.0.0',Status:'Active'}]:path==='ScheduledTasks'?[{Key:'DeletionQueue.OverlayGeneration',Id:'artwork-job',Triggers:[{Type:'DailyTrigger',TimeOfDayTicks:144000000000}]}]:{configuration:{Enabled:false},queueAvailable:false}};};
    try {w.eval(source);await turn();await turn();
        assert.equal(w.document.querySelectorAll('.suiteIntegrationBadge').length,6);
        const own=w.document.querySelector('[data-plugin=automatic]');assert.match(own.textContent,/Rules paused.*v0.2.0.0/);assert.match(w.document.querySelector('[data-plugin=deletion-queue]').textContent,/Unavailable/);
        own.click();assert.equal(w.document.querySelector('.suiteBadgeDetail').hidden,false);
        const job=[...w.document.querySelectorAll('.suiteJobBadge')].find(n=>n.textContent.includes('Artwork'));assert.match(job.textContent,/Daily/);assert.equal(job.getAttribute('href'),'#/dashboard/tasks/artwork-job');
        assert(calls.every(c=>!c.options.body));w.document.querySelector('main').dispatchEvent(new w.Event('viewhide'));assert(w.document.querySelector('.suiteBadgeDetail').hidden);
    }finally{w.close();}
});
test('JSON diagnostics provide highlighted code, paged transaction blocks and safe navigation cleanup', async () => {
    const dom=fixture('<details open><summary>Journal</summary><pre></pre></details>'),w=dom.window,pre=w.document.querySelector('pre');
    try {pre.textContent=JSON.stringify(Array.from({length:125},(_,i)=>({Kind:'Transition '+i,At:'2026-10-26T02:00:00Z',Text:'<img src=x onerror=bad()>'})));await turn();await new Promise(r=>setTimeout(r,10));
        assert(pre.querySelector('.suiteJsonKey'));assert.equal(w.document.querySelectorAll('img').length,0);
        const more=[...w.document.querySelectorAll('.suiteJsonTree button')].find(b=>b.textContent==='Show next 100');assert(more);more.click();assert.match(w.document.querySelector('.suiteJsonTree').textContent,/Transition 124/);
        [...w.document.querySelectorAll('.suiteCodeTools button')].find(b=>b.textContent==='JSON code').click();assert.equal(pre.hidden,false);
        w.document.querySelector('main').dispatchEvent(new w.Event('viewhide'));assert.equal(w.document.querySelectorAll('.suiteJsonTree').length,0);
    }finally{w.close();}
});
