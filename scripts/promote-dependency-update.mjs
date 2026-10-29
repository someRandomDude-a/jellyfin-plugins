// Privileged phase: this script is checked out from the trusted PR base, never head.
import { execFileSync } from 'node:child_process';
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { nextDependencyRelease } from './dependency-summary.mjs';

const gh = args => execFileSync('gh', args, { encoding: 'utf8', maxBuffer: 1024 * 1024 }).trim();
const repo = process.env.GH_REPO, number = process.env.PR_NUMBER, head = process.env.HEAD_SHA, base = process.env.BASE_SHA;
if (!/^[\w.-]+\/[\w.-]+$/.test(repo || '') || !/^\d+$/.test(number || '') || ![head, base].every(value => /^[a-f0-9]{40}$/.test(value || ''))) throw Error('Invalid promotion identity');
const branch = process.env.DEFAULT_BRANCH;
const pr = JSON.parse(gh(['api', `repos/${repo}/pulls/${number}`]));
if (pr.head.sha !== head || pr.head.repo.full_name !== repo || pr.base.ref !== branch || !pr.merged && pr.base.sha !== base) throw Error('PR base/head changed after testing; rebase and retest before promotion');
const current = JSON.parse(gh(['api', `repos/${repo}/branches/${encodeURIComponent(branch)}`]));
if (!pr.merged && current.commit.sha !== base) throw Error('Default branch advanced after testing; rebase and retest');
// Recompute from trusted code rather than trusting an artifact written by a
// candidate test job or any PR-controlled commit/PR description.
execFileSync('git', ['fetch', 'origin', head], { stdio: 'pipe' });
execFileSync(process.execPath, [fileURLToPath(new URL('./dependency-summary.mjs', import.meta.url)), '--base=' + base, '--head=' + head, '--output=dependency-commit.txt'], { stdio: 'pipe' });
const message = await readFile('dependency-commit.txt', 'utf8');
if (!message.endsWith('Dependency-Maintenance: true\n')) throw Error('Missing validated dependency commit summary');
if (!pr.merged) {
    const protection = JSON.parse(gh(['api', `repos/${repo}/branches/${encodeURIComponent(branch)}/protection/required_status_checks`]));
    const contexts = [...(protection.contexts || []), ...(protection.checks || []).map(check => check.context)];
    if (!protection.strict || !contexts.includes('Dependency update acceptance')) throw Error('Configure strict up-to-date branch protection requiring Dependency update acceptance; protections will not be bypassed');
    // No --admin, bypass flag, force push or blind update of a moving PR head.
    await writeFile('dependency-body.txt', message.split('\n').slice(2).join('\n'));
    gh(['pr', 'merge', number, '--repo', repo, '--squash', '--match-head-commit', head, '--subject', message.split('\n')[0], '--body-file', 'dependency-body.txt']);
}
const merged = JSON.parse(gh(['api', `repos/${repo}/pulls/${number}`]));
if (!merged.merged || !/^[a-f0-9]{40}$/.test(merged.merge_commit_sha || '')) throw Error('Merge awaits repository approvals/checks; release not dispatched');
const commit = JSON.parse(gh(['api', `repos/${repo}/commits/${merged.merge_commit_sha}`]));
if (commit.parents[0]?.sha !== base) throw Error('Merge parent differs from tested base; release refused');
const tags = gh(['api', `repos/${repo}/tags?per_page=100`, '--paginate', '--jq', '.[] | {name: .name, commit: .commit.sha} | @json']).split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
const version = nextDependencyRelease(tags, merged.merge_commit_sha);
gh(['workflow', 'run', 'release-all.yml', '--repo', repo, '--ref', branch, '-f', 'version=' + version, '-f', 'ref=' + merged.merge_commit_sha, '-f', 'expected_commit=' + merged.merge_commit_sha]);
console.log('Validated dependency update merged; immutable suite patch release dispatched for ' + merged.merge_commit_sha + '. Existing release-environment rules still apply.');
