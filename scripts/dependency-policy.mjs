const sha = value => /^[a-f0-9]{40}$/.test(value || '');
export function dependencyBot(pr, repo, login) {
    return pr.head?.repo?.full_name === repo && (
        pr.user?.login === 'dependabot[bot]' && pr.head.ref.startsWith('dependabot/') ||
        Boolean(login) && pr.user?.login === login && pr.head.ref.startsWith('dependency-maintenance/'));
}

// All inputs are API data, not instructions, PR text or downloadable artifacts.
export function promotionIdentity(run, pr, jobs, { repo, branch, login }) {
    const association = run.pull_requests?.find(item => item.number === pr.number);
    if (!Number.isSafeInteger(pr.number) || pr.number < 1 ||
        run.repository?.full_name !== repo || run.head_repository?.full_name !== repo ||
        run.path !== '.github/workflows/dependency-validation.yml' || run.event !== 'pull_request' ||
        run.status !== 'completed' || run.conclusion !== 'success' ||
        !dependencyBot(pr, repo, login) || pr.draft || pr.state !== 'open' ||
        pr.base?.ref !== branch || !sha(run.head_sha) || run.head_sha !== pr.head.sha ||
        association?.head?.sha !== pr.head.sha || !sha(association?.base?.sha) ||
        association.base.sha !== pr.base.sha ||
        !jobs.some(job => job.name === 'Dependency update acceptance' && job.conclusion === 'success')) {
        throw Error('Dependency run is not an accepted, current same-repository bot update');
    }
    return { number: String(pr.number), head: pr.head.sha, base: association.base.sha };
}
