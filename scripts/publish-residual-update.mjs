// Runs only on a trusted default-branch checkout after the residual updater.
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { privateCommand } from './private-command.mjs';
import { dependencyGithub } from './dependency-github.mjs';
import { summarizeChanges, dependencyInventory } from './dependency-summary.mjs';
import { rewriteDependencies } from './update-dependencies.mjs';

export function residualChanges(files) {
    const message = summarizeChanges(files);
    for (const file of files) {
        const before = dependencyInventory(file.path, file.before), after = dependencyInventory(file.path, file.after);
        for (const [key, entry] of after.entries) {
            if (before.entries.get(key)?.value === entry.value) continue;
            if (!['upstream', 'docker', 'npm-tool', 'node'].includes(entry.kind) ||
                entry.kind === 'docker' && /(?:^|\/)(?:docker-compose|compose)\.ya?ml$/.test(file.path)) {
                throw Error('Residual publisher cannot change Dependabot-owned declarations');
            }
        }
    }
    return message;
}

export function supplementResidual(files, candidate, rows) {
    return files.map(file => {
        const before = candidate(file.path);
        const after = file.path === '.github/upstream-dependencies.json' ? file.after : rewriteDependencies(file.path, before, rows);
        return { ...file, before, after };
    }).filter(file => file.before !== file.after);
}

export async function publishResidual({ repo, branch, login, api }) {
    if (!/^[\w.-]+\/[\w.-]+$/.test(repo || '') || !branch || !login) throw Error('Invalid residual publication configuration');
    const base = privateCommand('git', ['rev-parse', 'HEAD']);
    const paths = privateCommand('git', ['diff', '--name-only', '-z']).split('\0').filter(Boolean);
    if (!paths.length) { console.log('No residual dependency changes.'); return; }
    const files = await Promise.all(paths.map(async path => ({ path,
        before: privateCommand('git', ['show', base + ':' + path], { trim: false }), after: await readFile(path, 'utf8'),
        mode: privateCommand('git', ['ls-tree', base, '--', path]).split(' ')[0] })));
    let message = residualChanges(files);
    const prefix = `repos/${repo}/`;
    const current = await api(prefix + 'branches/' + encodeURIComponent(branch));
    if (current.commit.sha !== base) throw Error('Default branch advanced; rerun residual discovery');
    // Never rewrite an existing branch or create unbounded replacement PRs.
    const open = await api(prefix + 'pulls?state=open&per_page=100');
    const candidates = open.filter(pr => pr.user.login === 'dependabot[bot]' &&
        pr.head.repo?.full_name === repo && pr.head.ref.startsWith('dependabot/') && pr.base.ref === branch && !pr.draft);
    const grouped = candidates.filter(pr => pr.head.ref.startsWith('dependabot/multi-ecosystem/'));
    const candidate = grouped.length === 1 ? grouped[0] : candidates.length === 1 ? candidates[0] : null;
    if (candidate) {
        if (candidate.base.sha !== base) throw Error('Dependabot branch needs rebase before residual supplementation');
        const head = candidate.head.sha;
        if (!/^[a-f0-9]{40}$/.test(head)) throw Error('Invalid Dependabot head');
        privateCommand('git', ['fetch', 'origin', head]);
        const text = (ref, path) => privateCommand('git', ['show', ref + ':' + path], { trim: false });
        const changed = privateCommand('git', ['diff', '--name-only', '-z', base, head]).split('\0').filter(Boolean);
        // Read Git objects only. Never check out or execute the candidate branch.
        summarizeChanges(changed.map(path => ({ path, before: text(base, path), after: text(head, path) })));
        const report = JSON.parse(await readFile('artifacts/dependency-bump/report.json', 'utf8'));
        if (!report.residual || !report.verified || !Array.isArray(report.updates)) throw Error('Missing residual discovery evidence');
        const supplemental = supplementResidual(files, path => text(head, path), report.updates);
        if (!supplemental.length) { console.log('Dependabot already contains current residual updates.'); return; }
        message = residualChanges(supplemental);
        const headCommit = await api(prefix + 'git/commits/' + head);
        const tree = await api(prefix + 'git/trees', { method: 'POST', body: { base_tree: headCommit.tree.sha,
            tree: supplemental.map(file => ({ path: file.path, mode: file.mode, type: 'blob', content: file.after })) } });
        const commit = await api(prefix + 'git/commits', { method: 'POST', body: { message, tree: tree.sha, parents: [head] } });
        await api(prefix + 'git/refs/heads/' + candidate.head.ref, { method: 'PATCH', body: { sha: commit.sha, force: false } });
        console.log('Supplemented Dependabot update; the new exact head must pass all PR gates.');
        return;
    }
    if (candidates.length) throw Error('Multiple Dependabot candidates; consolidate before residual supplementation');
    if (open.some(pr => pr.user.login === login && pr.head.ref.startsWith('dependency-maintenance/'))) {
        console.log('An existing residual update needs review or rebase; no branch overwritten.'); return;
    }
    const tree = await api(prefix + 'git/trees', { method: 'POST', body: {
        base_tree: current.commit.commit.tree.sha,
        tree: files.map(file => ({ path: file.path, mode: file.mode, type: 'blob', content: file.after }))
    } });
    const commit = await api(prefix + 'git/commits', { method: 'POST', body: { message, tree: tree.sha, parents: [base] } });
    const ref = 'dependency-maintenance/' + commit.sha;
    await api(prefix + 'git/refs', { method: 'POST', body: { ref: 'refs/heads/' + ref, sha: commit.sha } });
    await api(prefix + 'pulls', { method: 'POST', body: { head: ref, base: branch,
        title: 'chore(deps): update embedded tools and upstream releases', body: message } });
    console.log('Opened residual dependency update; full PR acceptance is required.');
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    await publishResidual({ repo: process.env.GH_REPO, branch: process.env.DEFAULT_BRANCH,
        login: process.env.DEPENDENCY_BOT_LOGIN, api: dependencyGithub(process.env.GH_TOKEN) });
}
