// Embed in Account Hub's existing admin shell; not a second app or global bell.
const safeErrors=new WeakSet();
function failure(code,status,body,response){
    const candidate=body?.error?.errorId||body?.errorId||response?.headers?.get?.('X-Suite-Error-Id');
    const id=typeof candidate==='string'&&/^[a-f0-9-]{36}$/i.test(candidate)?candidate:null;
    const messages={400:'Some details are invalid. Check your entries.',401:'Your session needs attention. Sign in through Jellyfin to continue.',403:'You do not have permission to perform this action.',404:'This item is unavailable.',409:'This item changed. Refresh and review it.',429:'Too many attempts. Please wait.'};
    const message=code==='two_factor_required'?'Complete Jellyfin two-factor verification to continue.':messages[status]||'We could not confirm the result. Check the current state or contact an administrator before trying again.';
    const error=Object.assign(new Error(message+(id?' Error ID: '+id:'')),{code,status,errorId:id});safeErrors.add(error);return error;
}
export function securityErrorMessage(error){return error&&safeErrors.has(error)?error.message:'Unable to complete this request. Contact an administrator if the problem continues.';}
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
    if (!route.test(path)) throw failure('invalid_route',400);
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(query || {})) {
        if (!['skip', 'limit', 'refresh'].includes(key)) throw failure('invalid_query',400);
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
        if (!response.ok || data.ok !== true) throw failure(code||'request_failed',response.status,data,response);
        return data.data;
    } catch(error){
        if(signal?.aborted||error&&safeErrors.has(error))throw error;
        throw failure('request_failed',0);
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
