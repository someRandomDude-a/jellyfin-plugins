// Pure local acceptance predicates; no Discord access or credentials.
export function interactionsCompleted(counters) {
    return ['help:completed', 'status:completed', 'link.prepare:completed', 'watchparty.devices:completed',
        'watchparty.invite:delivered_dm', 'watchparty.invite:delivered_channel']
        .every(key => Number.isSafeInteger(counters[key]) && counters[key] > 0);
}

export function ownedInvitation(message, botId, started, publicBaseUrl) {
    if (message.author?.id !== botId || !(Date.parse(message.timestamp) >= started)
        || !/^(One-use Jellyfin watch-party invitation|Accept your Jellyfin invitation|Reset your Jellyfin password)/.test(message.content || '')) return false;
    const base = new URL(publicBaseUrl);
    return (message.content.match(/https:\/\/[^\s<>]+/g) || []).some(value => {
        try {
            const link = new URL(value);
            return link.origin === base.origin && link.pathname.startsWith(base.pathname.replace(/\/$/, '') + '/') && !link.username && !link.password;
        } catch { return false; }
    });
}
