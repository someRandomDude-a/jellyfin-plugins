import assert from 'node:assert/strict';
import { test } from 'node:test';
import { waitForGuestState } from '../scripts/guest-state.mjs';

test('subtitle transitions skip a stale ready stream and wait for subtitle index zero to be ready', async () => {
    const states = [
        { ready: true, playbackMethod: 'Remux' },
        { ready: false, burnedSubtitleIndex: 0, playbackMethod: 'Transcode' },
        { ready: true, burnedSubtitleIndex: 0, playbackMethod: 'Transcode' },
    ];
    let calls = 0;
    const state = await waitForGuestState(() => states[calls++], s => s.burnedSubtitleIndex === 0, { wait: async () => {} });
    assert.equal(calls, 3);
    assert.equal(state.burnedSubtitleIndex, 0);
});

test('seek transitions skip a ready stream with the previous offset', async () => {
    let calls = 0;
    const state = await waitForGuestState(() => ({ ready: true, offset: calls++ ? 60 : 0 }), s => s.offset === 60, { wait: async () => {} });
    assert.equal(calls, 2);
    assert.equal(state.offset, 60);
});

test('a permanently wrong ready state fails with bounded polling and useful diagnostics', async () => {
    let calls = 0, waits = 0;
    await assert.rejects(waitForGuestState(() => {
        calls++;
        return { ready: true, playbackMethod: 'Remux', selectedSubtitleIndex: -1, offset: 0, token: 'secret' };
    }, s => s.burnedSubtitleIndex === 0, { attempts: 3, wait: async () => { waits++; } }), error => {
        assert.match(error.message, /expected ready state/);
        assert.match(error.message, /"playbackMethod":"Remux"/);
        assert(!error.message.includes('secret'));
        return true;
    });
    assert.equal(calls, 3);
    assert.equal(waits, 2);
});

test('callers without a transition predicate still wait for readiness', async () => {
    let calls = 0;
    assert.equal((await waitForGuestState(() => ({ ready: calls++ > 0 }), undefined, { wait: async () => {} })).ready, true);
    assert.equal(calls, 2);
});
