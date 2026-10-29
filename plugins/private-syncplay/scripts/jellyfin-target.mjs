import { ensureTargets, jellyfinTarget, describeRelease } from '../../../scripts/latest-releases.mjs';

export function verifyLatest(release) {
    return jellyfinTarget(describeRelease('jellyfin', release));
}
export async function resolveTarget({ pull = false, platform = 'linux/amd64' } = {}) {
    const targets = await ensureTargets(['jellyfin'], { platform, pull: pull ? ['jellyfin'] : [] });
    return jellyfinTarget(targets.jellyfin);
}
