import { test } from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { installMessageBudget, withinMessageBudget } from '../src/message-budget.mjs';

test('Activity RPC budget accepts ordinary readiness/external-link responses', () => {
    assert(withinMessageBudget([1, { cmd: 'DISPATCH', evt: 'READY', data: { v: 1, config: { api_endpoint: '//discord.com/api' } } }]));
    assert(withinMessageBudget([1, { cmd: 'OPEN_EXTERNAL_LINK', nonce: 'fixture', data: { opened: true } }]));
});
test('Activity RPC budget rejects oversized collections, depth, strings and cycles before validation', () => {
    assert(!withinMessageBudget([1, { data: Array(257).fill({}) }]));
    assert(!withinMessageBudget({ data: 'x'.repeat(4097) }));
    assert(!withinMessageBudget(Array(10).fill('x'.repeat(4000))));
    let deep = {}; for (let i = 0; i < 18; i++) deep = { child: deep };
    assert(!withinMessageBudget(deep));
    const cycle = {}; cycle.self = cycle; assert(!withinMessageBudget(cycle));
    assert(!withinMessageBudget(Object.fromEntries(Array.from({ length: 129 }, (_, i) => ['key' + i, 0]))));
    assert(!withinMessageBudget(Array.from({ length: 100 }, () => Array(20).fill(0))));
    assert(!withinMessageBudget(new Uint8Array(100000)));
});
test('Capturing guard prevents oversized data from reaching SDK-style listeners and disposes cleanly', () => {
    const dom = new JSDOM(''), window = dom.window;
    try {
        let parsed = 0;
        const remove = installMessageBudget(window);
        window.addEventListener('message', () => parsed++);
        const send = data => window.dispatchEvent(new window.MessageEvent('message', { data, origin: 'https://discord.com' }));
        send([1, { data: Array(10000).fill(0) }]); assert.equal(parsed, 0);
        send([1, { data: { opened: true } }]); assert.equal(parsed, 1);
        remove(); send([1, { data: Array(10000).fill(0) }]); assert.equal(parsed, 2);
    } finally { window.close(); }
});
