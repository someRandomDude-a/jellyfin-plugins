import test from 'node:test';
import assert from 'node:assert/strict';
import { verifyLatest } from '../scripts/jellyfin-target.mjs';

test('new stable Jellyfin releases automatically select the corresponding SDK, ABI and image', () => {
    for (const tag of ['v12.4', 'v25.7.2']) {
        const target = verifyLatest({ tag_name: tag });
        assert.equal(target.tag, tag.slice(1));
        assert.equal(target.abi, target.version + '.0');
        assert.equal(target.image, 'jellyfin/jellyfin:' + target.tag);
    }
});
test('draft and prerelease servers cannot silently become stable test targets', () => {
    assert.throws(() => verifyLatest({ tag_name: 'v99.0', prerelease: true }));
    assert.throws(() => verifyLatest({ tag_name: 'v99.0', draft: true }));
});
