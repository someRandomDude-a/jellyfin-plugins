import test from 'node:test';
import assert from 'node:assert/strict';
import { rewriteDependencies } from './update-dependencies.mjs';
import { declarations } from './check-dependency-freshness.mjs';
import { summarizeChanges } from './dependency-summary.mjs';

test('bump changes versions but preserves the direct dependency set and scripts', () => {
    const before = JSON.stringify({ dependencies: { existing: '^1.0.0' }, scripts: { test: 'node test.mjs' } });
    const after = rewriteDependencies('package.json', before, [{ kind: 'npm', name: 'existing', current: '^1.0.0', latest: '2.0.0' }]);
    assert.deepEqual(JSON.parse(after), { dependencies: { existing: '^2.0.0' }, scripts: { test: 'node test.mjs' } });
    assert.match(summarizeChanges([{ path: 'package.json', before, after }]), /existing/);
});
test('bump updates action refs and annotated hashes without changing workflow behavior', () => {
    const path = '.github/workflows/test.yml', before = 'uses: actions/checkout@' + 'a'.repeat(40) + ' # v4\nrun: safe';
    const row = { kind: 'action', name: 'actions/checkout', current: 'a'.repeat(40), latest: { version: 'b'.repeat(40), tag: 'v6.0.0' } };
    const after = rewriteDependencies(path, before, [row]);
    assert(after.endsWith('# v6.0.0\nrun: safe'));
    assert.match(summarizeChanges([{ path, before, after }]), /actions\/checkout/);
});
test('bump preserves descriptive action comments after an existing version annotation', () => {
    const path = '.github/workflows/test.yml', before = 'uses: actions/checkout@v4 # v4 preserve reviewed permissions';
    const after = rewriteDependencies(path, before, [{ kind: 'action', name: 'actions/checkout', current: 'v4', latest: 'v7.0.1' }]);
    assert.equal(after, 'uses: actions/checkout@v7.0.1 # v7.0.1 preserve reviewed permissions');
    assert.match(summarizeChanges([{ path, before, after }]), /actions\/checkout/);
});
test('bump keeps native ABI packages and historical fixture versions out of scope', () => {
    const path = 'project.csproj', before = '<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.11"/><PackageReference Include="Moq" Version="4.20.70"/>';
    const rows = declarations(path, before).map(row => ({ ...row, latest: '4.20.72' }));
    const after = rewriteDependencies(path, before, rows);
    assert(after.includes('Sqlite" Version="10.0.11"'));
    assert(after.includes('Moq" Version="4.20.72"'));
    assert.deepEqual(declarations('scripts/example.test.mjs', "const image='python:3.13-alpine';"), []);
    assert.deepEqual(declarations(path, '<PackageReference Include="SkiaSharp" Version="$(JellyfinSkiaPackageVersion)"/>'), []);
});
test('bump changes image version and digest together and retains npm tool invocation', () => {
    const path = 'scripts/example.sh', before = 'docker run python:3.13-alpine@sha256:' + 'a'.repeat(64) + '\nnpx playwright@1.50.0 test';
    const rows = declarations(path, before).map(row => ({ ...row, latest: row.kind === 'image' ? { version: 'alpine', digest: 'sha256:' + 'b'.repeat(64) } : '1.60.0' }));
    const after = rewriteDependencies(path, before, rows);
    assert(after.includes('python:alpine@sha256:' + 'b'.repeat(64)));
    assert(after.includes('npx playwright@1.60.0 test'));
    assert.match(summarizeChanges([{ path, before, after }]), /python/);
});
