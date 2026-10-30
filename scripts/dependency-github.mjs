// Credentials travel only in headers to the fixed GitHub API origin. No response
// prose, request objects or underlying exceptions are ever emitted on failure.
export function dependencyGithub(token, fetchImpl = fetch) {
    if (!token) throw Error('Configure the dependency automation GitHub secret');
    return async (path, { method = 'GET', body } = {}) => {
        if (!/^repos\/[\w.-]+\/[\w.-]+\//.test(path) || /[\r\n]/.test(path)) throw Error('Invalid dependency API path');
        try {
            const response = await fetchImpl('https://api.github.com/' + path, {
                method, redirect: 'error', signal: AbortSignal.timeout(30000),
                headers: { Authorization: 'Bearer ' + token, Accept: 'application/vnd.github+json',
                    'X-GitHub-Api-Version': '2022-11-28', 'Content-Type': 'application/json' },
                body: body === undefined ? undefined : JSON.stringify(body)
            });
            if (!response.ok) throw Error('API request failed');
            return response.status === 204 ? null : await response.json();
        } catch {
            throw Error('Dependency GitHub API request failed; details withheld to protect credentials.');
        }
    };
}
