// Safety baselines, not software test targets. Unchanged source contracts can
// follow later releases automatically; changed native/security code fails closed.
import { createHash } from 'node:crypto';
import { githubJson } from './latest-releases.mjs';

export const reviewedContracts = {
    native: '16ff0f4174be4469fcd605f32e84462831b0d2322322e455140035f171bcbe24',
    security: '6cabd80b88d56ae4a911483b2b02192b7bb5e77fc21e35cf95457aaeb06db714'
};
export function contractFingerprint(tree, kind) {
    if (!['native', 'security'].includes(kind)) throw Error('Unknown source contract');
    if (tree.truncated || !Array.isArray(tree.tree)) throw Error('Incomplete upstream source contract');
    const rows = tree.tree.filter(item => item.type === 'blob' && item.path.endsWith('.cs') && (kind === 'security'
        ? item.path.startsWith('src/')
        : item.path.startsWith('src/Jellyfin.Database/') || /\/(UserManager|UserPolicy|UserUpdatedEventArgs|UserCreatedEventArgs)\.cs$/.test(item.path)))
        .map(item => [item.path, item.sha]).sort((a, b) => a[0].localeCompare(b[0], 'en'));
    if (!rows.length || rows.some(([, sha]) => !/^[a-f0-9]{40}$/.test(sha))) throw Error('Missing upstream source contract');
    return createHash('sha256').update(JSON.stringify(rows)).digest('hex');
}
export async function checkSourceContract(target, kind) {
    const commit = await githubJson('repos/' + target.repository + '/commits/' + encodeURIComponent(target.tag));
    if (!/^[a-f0-9]{40}$/.test(commit.sha)) throw Error('Upstream source commit unavailable');
    const tree = await githubJson('repos/' + target.repository + '/git/trees/' + commit.sha + '?recursive=1');
    const fingerprint = contractFingerprint(tree, kind);
    return { sourceCommit: commit.sha, contractFingerprint: fingerprint, contractReviewed: fingerprint === reviewedContracts[kind] };
}
