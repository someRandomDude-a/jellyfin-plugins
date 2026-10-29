// Publication is read-only: a new upstream release requires new tests, not fallback.
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { discoverTargets, validateSnapshot, pullImage, githubJson } from './latest-releases.mjs';
import { checkFreshness, registryLookup } from './check-dependency-freshness.mjs';
export async function assertStillLatest(snapshot, discover = discoverTargets, resolveImage = pullImage,
    resolveCommit = async target => (await githubJson('repos/' + target.repository + '/commits/' + encodeURIComponent(target.tag))).sha) {
    validateSnapshot(snapshot);
    const names = Object.keys(snapshot.targets);
    if (!names.length) throw Error('No tested targets recorded');
    const current = await discover(names);
    const changes = [];
    for (const name of names) {
        const tested = snapshot.targets[name], fresh = current.targets[name];
        if (!fresh) throw Error('Missing current release metadata for ' + name);
        if (tested.sourceCommit && !fresh.sourceCommit) fresh.sourceCommit = await resolveCommit(fresh);
        for (const key of ['version', 'tag', 'releaseId', 'sha256', 'sourceCommit']) {
            if (fresh[key] !== undefined && fresh[key] !== tested?.[key]) changes.push(name + '.' + key);
        }
        if (fresh.image) {
            if (!tested.imageDigest || !tested.platform) throw Error('Missing tested immutable image evidence for ' + name);
            if ((await resolveImage(fresh, tested.platform)).imageDigest !== tested.imageDigest) changes.push(name + '.imageDigest');
        }
    }
    if (changes.length) throw Error('LATEST-ONLY RELEASE BLOCKED: upstream changed after testing (' + changes.join(', ') + '). Rerun all checks; do not publish older targets.');
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    if (!process.env.SUITE_TEST_TARGETS) throw Error('Missing tested target snapshot; publication refused');
    const snapshot = JSON.parse(await readFile(process.env.SUITE_TEST_TARGETS, 'utf8'));
    if (!snapshot.tools?.node || !snapshot.tools?.dotnet) throw Error('Missing tested toolchain evidence; publication refused');
    await checkFreshness([{ path: '(tested toolchain)', kind: 'node', name: 'Node.js', current: snapshot.tools.node }], registryLookup());
    await assertStillLatest(snapshot);
    console.log('PASS tested upstream targets are still latest stable at publication.');
}
