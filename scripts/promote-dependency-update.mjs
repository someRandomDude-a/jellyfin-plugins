// Privileged phase: this script is checked out from the trusted PR base, never head.
import { privateCommand } from './private-command.mjs';
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { nextDependencyRelease } from './dependency-summary.mjs';
import { promotionIdentity } from './dependency-policy.mjs';

const gh = args => privateCommand('gh', args);
const repo = process.env.GH_REPO, runId = process.env.VALIDATION_RUN_ID;
if (!/^[\w.-]+\/[\w.-]+$/.test(repo || '') || !/^\d+$/.test(runId || '')) throw Error('Invalid promotion identity');
const branch = process.env.DEFAULT_BRANCH;
const run = JSON.parse(gh(['api', `repos/${repo}/actions/runs/${runId}`]));
if (run.pull_requests?.length !== 1) throw Error('Validation must identify exactly one pull request');
const pr = JSON.parse(gh(['api', `repos/${repo}/pulls/${run.pull_requests[0].number}`]));
const jobs = gh(['api', `repos/${repo}/actions/runs/${runId}/attempts/${run.run_attempt}/jobs?per_page=100`, '--paginate', '--jq', '.jobs[] | {name, conclusion} | @json'])
    .split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
const { number, head, base } = promotionIdentity(run, pr, jobs, { repo, branch, login: process.env.DEPENDENCY_BOT_LOGIN });
if (pr.head.sha !== head || pr.head.repo.full_name !== repo || pr.base.ref !== branch || !pr.merged && pr.base.sha !== base) throw Error('PR base/head changed after testing; rebase and retest before promotion');
const current = JSON.parse(gh(['api', `repos/${repo}/branches/${encodeURIComponent(branch)}`]));
if (!pr.merged && current.commit.sha !== base) throw Error('Default branch advanced after testing; rebase and retest');
// Recompute from trusted code rather than trusting an artifact written by a
// candidate test job or any PR-controlled commit/PR description.
privateCommand('git', ['fetch', 'origin', head]);
privateCommand(process.execPath, [fileURLToPath(new URL('./dependency-summary.mjs', import.meta.url)), '--base=' + base, '--head=' + head, '--output=dependency-commit.txt']);
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
