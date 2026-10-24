import { test } from 'node:test';
import assert from 'node:assert/strict';
import { interactionsCompleted, ownedInvitation } from './discord-acceptance.mjs';
test('Interactive acceptance requires both explicit delivery paths and actual command outcomes', () => {
    const keys = ['help:completed', 'status:completed', 'link.prepare:completed', 'watchparty.devices:completed', 'watchparty.invite:delivered_dm', 'watchparty.invite:delivered_channel'];
    const counters = Object.fromEntries(keys.map(key => [key, 1]));
    assert(interactionsCompleted(counters));
    for (const key of keys) assert(!interactionsCompleted({ ...counters, [key]: 0 }));
    assert(!interactionsCompleted({ connected: true }));
    assert(!interactionsCompleted({ ...counters, 'help:completed': '1' }));
});
test('Cleanup matches only this bot, time window, invitation prefix and fixture URL boundary', () => {
    const started = Date.parse('2026-10-24T00:00:00Z');
    const message = { author: { id: '123' }, timestamp: '2026-10-24T00:01:00Z', content: 'One-use Jellyfin watch-party invitation:\nhttps://fixture.example/suite/watch#secret' };
    const match = value => ownedInvitation(value, '123', started, 'https://fixture.example/suite');
    assert(match(message));
    assert(!match({ ...message, author: { id: '456' } }));
    assert(!match({ ...message, timestamp: '2026-10-23T00:00:00Z' }));
    assert(!match({ ...message, timestamp: 'invalid' }));
    assert(!match({ ...message, content: message.content.replace('/suite/', '/suite-other/') }));
    assert(!match({ ...message, content: message.content.replace('fixture.example', 'other.example') }));
    assert(!match({ ...message, content: 'unrelated https://fixture.example/suite/watch#secret' }));
});
