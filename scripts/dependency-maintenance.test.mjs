import test from 'node:test';
import assert from 'node:assert/strict';
import { dependencyInventory, summarizeChanges, nextDependencyRelease } from './dependency-summary.mjs';

test('dependency commits list direct, transitive, SDK, action and image changes', () => {
    const lock = version => JSON.stringify({ lockfileVersion: 3, packages: { 'node_modules/transitive': { version, integrity: 'sha512-' + version } } });
    const changes = [
        { path: 'package.json', before: '{"dependencies":{"direct":"1.0.0"}}', after: '{"dependencies":{"direct":"2.0.0"}}' },
        { path: 'package-lock.json', before: lock('1.0.0'), after: lock('2.0.0') },
        { path: 'global.json', before: '{"sdk":{"version":"10.0.1"}}', after: '{"sdk":{"version":"10.0.2"}}' },
        { path: 'project.csproj', before: '<PackageReference Include="Example" Version="1.0" />', after: '<PackageReference Include="Example" Version="2.0" />' },
        { path: '.github/workflows/check.yml', before: 'uses: actions/checkout@v4', after: 'uses: actions/checkout@v5' },
        { path: 'scripts/test.mjs', before: "const image='example/image:1.0';", after: "const image='example/image:2.0';" }
    ];
    const message = summarizeChanges(changes);
    for (const name of ['direct', 'transitive', '.NET SDK', 'Example', 'actions/checkout', 'example/image']) assert(message.includes(name));
    assert(message.endsWith('Dependency-Maintenance: true\n'));
});
test('automatic promotion refuses code/configuration changes, new files and credential-bearing specs', () => {
    assert.throws(() => summarizeChanges([{ path: 'scripts/test.mjs', before: "const image='example/image:1.0';", after: "const image='example/image:2.0'; stealSecrets();" }]), /Non-dependency/);
    assert.throws(() => summarizeChanges([{ path: 'package.json', before: '{"scripts":{"test":"safe"}}', after: '{"scripts":{"test":"skip"}}' }]), /Non-dependency/);
    assert.throws(() => summarizeChanges([{ path: 'new.cs', before: '', after: 'unsafe' }]));
    assert.throws(() => dependencyInventory('package.json', '{"dependencies":{"unsafe":"https://user:secret@example.invalid/a"}}'));
});
test('patch releases advance beyond every plugin version and retry the same immutable tag', () => {
    const tags = [{ name: 'v0.2.0', commit: 'old' }, { name: 'notifications/v0.3.9', commit: 'other' }];
    assert.equal(nextDependencyRelease(tags, 'new'), 'v0.3.10');
    assert.equal(nextDependencyRelease([...tags, { name: 'v0.3.10', commit: 'new' }], 'new'), 'v0.3.10');
    assert.equal(nextDependencyRelease([], 'new'), 'v0.0.1');
});
test('dependency summaries include action digest annotations without accepting workflow logic edits', () => {
    const before = 'uses: actions/checkout@v4';
    const after = 'uses: actions/checkout@' + 'a'.repeat(40) + ' # v5';
    assert(summarizeChanges([{ path: '.github/workflows/check.yml', before, after }]).includes('actions/checkout'));
    assert.throws(() => summarizeChanges([{ path: '.github/workflows/check.yml', before, after: after + '\ncontinue-on-error: true' }]), /Non-dependency/);
});
test('upstream releases trigger maintenance without becoming permanent test pins', async () => {
    const marker = version => JSON.stringify({ purpose: 'Detection only', dependencies: [{ repository: 'jellyfin/jellyfin', version }] });
    const message = summarizeChanges([{ path: '.github/upstream-dependencies.json', before: marker('12.4'), after: marker('12.5') }]);
    assert(message.includes('upstream jellyfin/jellyfin: 12.4 -> 12.5'));
});
