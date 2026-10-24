import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
const require = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const turn = () => new Promise(resolve => setImmediate(resolve));
async function mount(request) {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only', url: 'https://example.test/suite/web/' });
    const code = await readFile(new URL('../plugins/account-hub/src/AccountHub/Web/group-policy.mjs', import.meta.url), 'utf8');
    dom.window.eval(code.replace('export function mountGroupPolicy', 'window.mountGroupPolicy = function'));
    const signal = new dom.window.AbortController();
    dom.window.mountGroupPolicy(dom.window.document.querySelector('main'), { request, signal: signal.signal }); await turn(); await turn();
    const click = async text => { const b = [...dom.window.document.querySelectorAll('button')].find(n => n.textContent === text); assert(b, text); b.click(); await turn(); await turn(); };
    return { dom, signal, click };
}
test('Group editor is native, labeled, inheritance-aware and saves intent only with proof transport', async () => {
    const calls = [];
    const h = await mount(async (path, method, payload, proof) => { calls.push({ path, method, payload, proof }); if (path === 'workspace') return { revision: 8 }; return { items: [], total: 0 }; });
    await h.click('Create group');
    const doc = h.dom.window.document;
    assert.equal(doc.querySelectorAll('textarea').length, 0);
    for (const e of doc.querySelectorAll('input,select')) assert(doc.querySelector(`label[for="${e.id}"]`));
    const name = [...doc.querySelectorAll('label')].find(n => n.textContent === 'Group name'); doc.getElementById(name.htmlFor).value = '<script>literal</script>';
    const field = [...doc.querySelectorAll('label')].find(n => n.textContent === 'Enable Remote Access'); doc.getElementById(field.htmlFor).value = 'false';
    await h.click('Save intent (no user changes)');
    const write = calls.find(c => c.path.includes('/commands/'));
    assert.equal(write.path, 'group-policy/commands/group.save'); assert.equal(write.proof, true);
    assert.deepEqual(Object.keys(write.payload.payload.managedFields), ['EnableRemoteAccess']);
    assert.equal(write.payload.payload.managedFields.EnableRemoteAccess, false);
    assert.equal(calls.filter(c => c.path.endsWith('/apply')).length, 0); h.dom.window.close();
});
test('Bulk reconciliation requires reviewed revision targets and excludes administrators in member selection', async () => {
    const calls = [], view = { userId: 'member', revision: 4, groupRevision: 7, current: { MaxActiveSessions: 1 }, desired: { MaxActiveSessions: 2 }, changedFields: ['MaxActiveSessions'] };
    const h = await mount(async (path, method, payload, proof) => {
        calls.push({ path, method, payload, proof });
        if (path.startsWith('users?')) return { total: 2, items: [{ id: 'member', username: 'Member' }, { id: 'admin', username: 'Admin', administrator: true }] };
        if (path === 'group-policy/preview') return [view]; if (path === 'workspace') return { revision: 10 };
        if (path.endsWith('/apply')) return { outcomes: [{ userId: 'member', status: 'applied' }] };
        return { total: 0, items: [] };
    });
    await h.click('Members and assignments');
    const inputs = h.dom.window.document.querySelectorAll('input[type=checkbox]'); assert.equal(inputs[1].disabled, true);
    inputs[0].checked = true; inputs[0].dispatchEvent(new h.dom.window.Event('change'));
    await h.click('Dry run selected policy'); assert(!calls.some(c => c.path.includes('/commands/')));
    assert.match(h.dom.window.document.body.textContent, /1 → 2/);
    await h.click('Confirm apply for reviewed users');
    const apply = calls.find(c => c.path.endsWith('/commands/apply')); assert(apply.proof);
    assert.equal(apply.payload.payload.targets[0].revision, 4); assert.equal(apply.payload.payload.targets[0].groupRevision, 7);
    h.signal.abort(); assert.equal(h.dom.window.document.querySelector('main>div').children.length, 0); h.dom.window.close();
});

test('Member selection checkboxes retain complete native checked, labeled and disabled structure', async t => {
    const calls = [];
    const h = await mount(async (path, method, payload) => {
        calls.push({ path, method, payload });
        return path.startsWith('users?') ? { total: 2, items: [{ id: 'member', username: 'Member' }, { id: 'admin', username: 'Admin', administrator: true }] } : { total: 0, items: [] };
    });
    t.after(() => h.dom.window.close()); await h.click('Members and assignments');
    const inputs = [...h.dom.window.document.querySelectorAll('input[type=checkbox]')]; assert.equal(inputs.length, 2);
    for (const input of inputs) {
        assert(input.matches('.checkboxContainer > .emby-checkbox-label > .emby-checkbox'));
        assert.equal(input.classList.contains('emby-input'), false);
        assert.equal(input.getAttribute('is'), 'emby-checkbox'); assert.equal(input.getAttribute('data-embycheckbox'), 'true');
        const label = input.labels[0]; assert.equal(label.htmlFor, input.id);
        assert.match(label.querySelector('.checkboxLabel').textContent, /^Select (Member|Admin)$/);
        assert(input.nextElementSibling.matches('.checkboxLabel'));
        const outline = input.nextElementSibling.nextElementSibling; assert(outline.matches('.checkboxOutline')); assert.equal(outline.getAttribute('aria-hidden'), 'true');
        assert(outline.querySelector('.material-icons.checkboxIcon.checkboxIcon-checked.check'));
        assert(outline.querySelector('.material-icons.checkboxIcon.checkboxIcon-unchecked'));
    }
    inputs[0].labels[0].click(); assert.equal(inputs[0].checked, true);
    assert.equal(inputs[1].disabled, true); inputs[1].labels[0].click(); assert.equal(inputs[1].checked, false);
    await h.click('Dry run selected policy');
    assert.deepEqual(Array.from(calls.find(c => c.path === 'group-policy/preview').payload.targets, x => x.userId), ['member']);
});
