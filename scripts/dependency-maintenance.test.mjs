import test from 'node:test';
import assert from 'node:assert/strict';
import { dependencyInventory, summarizeChanges, nextDependencyRelease } from './dependency-summary.mjs';
import { dependencyBot, promotionIdentity } from './dependency-policy.mjs';
import { dependencyGithub } from './dependency-github.mjs';
import { residualChanges, supplementResidual, publishResidual } from './publish-residual-update.mjs';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';

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

const promotionFixture = () => {
    const repo = 'owner/repository', head = 'a'.repeat(40), base = 'b'.repeat(40);
    return {
        options: { repo, branch: 'main', login: 'maintenance-bot' },
        pr: { number: 42, state: 'open', draft: false, user: { login: 'dependabot[bot]' },
            head: { sha: head, ref: 'dependabot/multi-ecosystem/project-dependencies', repo: { full_name: repo } },
            base: { sha: base, ref: 'main' } },
        run: { repository: { full_name: repo }, head_repository: { full_name: repo },
            path: '.github/workflows/dependency-validation.yml', event: 'pull_request', status: 'completed',
            conclusion: 'success', head_sha: head, pull_requests: [{ number: 42, head: { sha: head }, base: { sha: base } }] },
        jobs: [{ name: 'Dependency update acceptance', conclusion: 'success' }]
    };
};
test('trusted promotion binds the successful acceptance run to exact bot, repository, base and head', () => {
    const fixture = promotionFixture();
    const identity = value => promotionIdentity(value.run, value.pr, value.jobs, value.options);
    assert.deepEqual(identity(fixture), { number: '42', head: 'a'.repeat(40), base: 'b'.repeat(40) });
    for (const mutate of [
        f => f.pr.head.sha = 'c'.repeat(40), f => f.pr.base.sha = 'c'.repeat(40),
        f => f.run.head_sha = 'c'.repeat(40), f => f.run.pull_requests = [],
        f => f.pr.user.login = 'someone-else', f => f.pr.head.ref = 'feature/dependabot',
        f => f.pr.head.repo.full_name = 'fork/repository', f => f.run.head_repository.full_name = 'fork/repository',
        f => f.run.repository.full_name = 'fork/repository', f => f.run.path = '.github/workflows/tests.yml',
        f => f.run.event = 'push', f => f.run.conclusion = 'failure', f => f.run.status = 'in_progress',
        f => f.pr.draft = true, f => f.pr.state = 'closed', f => f.pr.base.ref = 'other',
        f => f.jobs[0].conclusion = 'skipped', f => f.jobs = []
    ]) { const value = promotionFixture(); mutate(value); assert.throws(() => identity(value)); }
    fixture.pr.user.login = 'maintenance-bot'; fixture.pr.head.ref = 'dependency-maintenance/update';
    assert(identity(fixture));
    assert(!dependencyBot(fixture.pr, fixture.options.repo, ''));
});
test('residual publisher refuses native manifest updates and candidate code changes', () => {
    const file = { path: 'scripts/tool.sh', before: 'npx playwright@1.0.0 test\n', after: 'npx playwright@2.0.0 test\n' };
    assert(residualChanges([file]).includes('playwright'));
    assert.throws(() => residualChanges([{ path: 'package.json', before: '{"dependencies":{"x":"1.0"}}', after: '{"dependencies":{"x":"2.0"}}' }]));
    assert.throws(() => residualChanges([{ path: 'compose.yaml', before: 'image: python:3.13', after: 'image: python:3.14' }]));
    assert.throws(() => residualChanges([{ ...file, after: file.after + 'steal_secrets\n' }]));
});
test('residual supplementation preserves native Dependabot edits and only replaces embedded versions', () => {
    const file = { path: '.github/workflows/check.yml', before: 'uses: actions/checkout@v6\nrun: docker run python:3.13\n',
        after: 'uses: actions/checkout@v6\nrun: docker run python:3.14\n' };
    const candidate = file.before.replace('@v6', '@v7');
    const rows = [{ path: file.path, kind: 'image', name: 'python', current: '3.13', latest: { version: '3.14' } }];
    const result = supplementResidual([file], () => candidate, rows);
    assert.equal(result[0].after, 'uses: actions/checkout@v7\nrun: docker run python:3.14\n');
    assert(residualChanges(result));
    assert.deepEqual(supplementResidual([file], () => result[0].after, rows), []);
});
test('dependency mutation API uses header-only credentials, refuses redirects and withholds failures', async () => {
    const token = 'synthetic-secret-never-print';
    let request;
    const api = dependencyGithub(token, async (url, options) => {
        request = { url, options }; return { ok: true, status: 200, json: async () => ({ sha: 'ok' }) };
    });
    assert.deepEqual(await api('repos/owner/repository/git/refs', { method: 'POST', body: { sha: 'value' } }), { sha: 'ok' });
    assert.equal(request.options.headers.Authorization, 'Bearer ' + token);
    assert.equal(request.options.redirect, 'error');
    assert(!request.url.includes(token)); assert(!request.options.body.includes(token));
    for (const response of [async () => { throw Error(token); }, async () => ({ ok: false, status: 403, body: token }),
        async () => ({ ok: true, status: 200, json: async () => { throw Error(token); } })]) {
        await assert.rejects(dependencyGithub(token, response)('repos/owner/repository/pulls'), error =>
            !String(error.stack).includes(token) && !error.cause && /withheld/.test(error.message));
    }
    await assert.rejects(api('https://elsewhere.invalid/'), /Invalid/);
});
test('residual publication opens a non-forced PR or supplements a validated Dependabot head using mocked GitHub', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'dependency-publication-'));
    const previous = process.cwd();
    const git = (...args) => execFileSync('git', args, { cwd: directory, encoding: 'utf8', stdio: 'pipe' }).trim();
    try {
        git('init', '--quiet'); git('config', 'user.name', 'Synthetic Test'); git('config', 'user.email', 'test@example.invalid');
        await mkdir(join(directory, 'scripts')); await writeFile(join(directory, 'scripts/tool.sh'), 'docker run python:3.13\n');
        await mkdir(join(directory, '.github/workflows'), { recursive: true });
        await writeFile(join(directory, '.github/workflows/check.yml'), 'uses: actions/checkout@v6\n');
        git('add', '.'); git('commit', '--quiet', '-m', 'base');
        const base = git('rev-parse', 'HEAD'), baseTree = git('rev-parse', 'HEAD^{tree}');
        await writeFile(join(directory, '.github/workflows/check.yml'), 'uses: actions/checkout@v7\n');
        git('add', '.'); git('commit', '--quiet', '-m', 'native dependency update');
        const candidateHead = git('rev-parse', 'HEAD');
        await writeFile(join(directory, '.github/workflows/check.yml'), 'uses: actions/checkout@v7\nrun: steal_secrets\n');
        git('add', '.'); git('commit', '--quiet', '-m', 'unsafe candidate');
        const unsafeHead = git('rev-parse', 'HEAD');
        git('switch', '--detach', base);
        git('remote', 'add', 'origin', directory);
        await writeFile(join(directory, 'scripts/tool.sh'), 'docker run python:3.14\n');
        await mkdir(join(directory, 'artifacts/dependency-bump'), { recursive: true });
        await writeFile(join(directory, 'artifacts/dependency-bump/report.json'), JSON.stringify({ residual: true, verified: true,
            updates: [{ path: 'scripts/tool.sh', kind: 'image', name: 'python', current: '3.13', latest: { version: '3.14' } }] }));
        process.chdir(directory);
        for (const supplement of [false, true, 'unsafe', 'existing', 'moving-base']) {
            const writes = [];
            const candidate = { user: { login: 'dependabot[bot]' }, head: { repo: { full_name: 'owner/repository' },
                ref: 'dependabot/multi-ecosystem/project-dependencies', sha: supplement === 'unsafe' ? unsafeHead : candidateHead }, base: { sha: base, ref: 'main' }, draft: false };
            const api = async (path, options) => {
                if (!options) {
                    if (path.includes('/branches/')) return { commit: { sha: supplement === 'moving-base' ? 'd'.repeat(40) : base, commit: { tree: { sha: baseTree } } } };
                    if (path.includes('/pulls?')) return supplement === 'existing'
                        ? [{ user: { login: 'maintenance-bot' }, head: { ref: 'dependency-maintenance/existing' } }]
                        : supplement ? [candidate] : [];
                    if (path.includes('/git/commits/')) return { tree: { sha: baseTree } };
                    throw Error('Unexpected read');
                }
                writes.push({ path, ...options }); return { sha: 'c'.repeat(40) };
            };
            const publish = () => publishResidual({ repo: 'owner/repository', branch: 'main', login: 'maintenance-bot', api });
            if (['unsafe', 'moving-base'].includes(supplement)) {
                await assert.rejects(publish(), /Non-dependency|advanced/); assert.equal(writes.length, 0); continue;
            }
            await publish();
            if (supplement === 'existing') { assert.equal(writes.length, 0); continue; }
            assert.equal(writes[0].body.tree[0].content, 'docker run python:3.14\n');
            assert.deepEqual(writes[1].body.parents, [supplement ? candidateHead : base]);
            if (supplement) {
                assert.equal(writes.length, 3); assert.equal(writes[2].method, 'PATCH');
                assert.equal(writes[2].body.force, false);
            } else {
                assert.equal(writes.length, 4); assert(writes[2].body.ref.startsWith('refs/heads/dependency-maintenance/'));
                assert.equal(writes[3].body.base, 'main');
            }
        }
    } finally { process.chdir(previous); await rm(directory, { recursive: true, force: true }); }
});
