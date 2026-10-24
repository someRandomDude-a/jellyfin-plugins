// Embed in Account Hub's existing admin shell; not a second app or global bell.
export function securityLinks(apiClient) {
    return {
        setup: apiClient.getUrl('TwoFactorAuth/Setup'),
        login: apiClient.getUrl('TwoFactorAuth/Login'),
        admin: apiClient.getUrl('web/index.html') + '#/configurationpage?name=TwoFactorAuth'
    };
}

export function classifySecurityResponse(status, body) {
    if ((status === 401 || status === 403) && (body?.twoFactorRequired === true || body?.error?.code === 'two_factor_required')) return 'two_factor_required';
    if (status === 401) return 'unauthorized';
    if (status === 403) return 'forbidden';
    return null;
}

// Caller pauses existing bell polling on pending2fa; resumes after verification/on focus.
// No retry, proof cache, email/passkey acquisition UI or deep-link-as-proof behavior.
export async function securityRequest(apiClient, path, { method = 'GET', body, signal, pending2fa, query } = {}) {
    const guid = '[a-fA-F0-9-]{36}';
    const route = new RegExp(`^security/(?:(?:status|users|devices|settings|webhook-test)|users/${guid}/(?:summary|force-logout)|devices/${guid}/[A-Za-z0-9_-]{1,256}/revoke)$`);
    if (!route.test(path)) throw new Error('invalid_route');
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(query || {})) {
        if (!['skip', 'limit', 'refresh'].includes(key)) throw new Error('invalid_query');
        params.set(key, String(value));
    }
    const proof = body?.stepUp;
    try {
        const response = await fetch(apiClient.getUrl('AccountHub/api/v1/' + path) + (params.size ? '?' + params : ''), {
            method, signal, cache: 'no-store', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'X-Emby-Token': apiClient.accessToken() },
            body: body === undefined ? undefined : JSON.stringify(body)
        });
        const data = await response.json();
        const code = classifySecurityResponse(response.status, data);
        if (code === 'two_factor_required') pending2fa?.(securityLinks(apiClient));
        if (!response.ok || data.ok !== true) throw new Error(code || data.error?.code || 'unavailable');
        return data.data;
    } finally {
        if (proof) { proof.code = undefined; proof.stepUpToken = undefined; }
    }
}

// This hint MUST originate from recipient browser, not a server loopback probe.
export async function passwordLoginHint(apiClient, signal) {
    const response = await fetch(apiClient.getUrl('TwoFactorAuth/public-config'), {
        credentials: 'omit', cache: 'no-store', signal
    });
    if (!response.ok) return null;
    const data = await response.json();
    return typeof data.passwordLoginDisabled === 'boolean' ? data.passwordLoginDisabled : null;
}
