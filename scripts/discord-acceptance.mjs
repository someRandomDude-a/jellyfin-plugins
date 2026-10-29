// Pure local acceptance predicates; no Discord access or credentials.
export function interactionsCompleted(counters) {
    return ['help:completed', 'status:completed', 'link.prepare:completed',
        'watchparty:delivered_dm', 'watchparty:delivered_channel']
        .every(key => Number.isSafeInteger(counters[key]) && counters[key] > 0);
}

export function accountInteractionsCompleted(counters) {
    return ['status:completed','link.prepare:completed','reset:completed'].every(key=>Number.isSafeInteger(counters[key])&&counters[key]>0);
}
export function invitationInteractionsCompleted(counters) {
    return interactionsCompleted(counters)&&['invite.create:completed','invite.create:rejected_limits'].every(key=>Number.isSafeInteger(counters[key])&&counters[key]>0);
}
export function invitationRedemptionCompleted(items,userId,since){
    const id=value=>typeof value==='string'?value.replaceAll('-','').toLowerCase():'';
    return items.some(invite=>id(invite.createdBy)===id(userId)&&invite.createdAt>=since&&invite.maxUses===2&&invite.completedUses===2&&invite.reservedUses===2&&invite.expiresAt-invite.createdAt===60*60000);
}
export function invitationHumanConfirmed(value){
    return value?.source==='human-confirmed'&&['signupExtraUseDenied','partyChannelRedemption','partyDmRedemption','recipientPrivacy'].every(key=>value[key]===true);
}

export function accountResetCompleted(rows,userId,since) {
    const id=value=>typeof value==='string'?value.replaceAll('-','').toLowerCase():'';
    return rows.some(row=>{const event=row.event;return event?.source==='account-hub'&&event.eventType==='password.reset'&&id(event.subjectId)===id(userId)&&Date.parse(event.occurredAt)>=since;});
}

export function ownedInvitation(message, botId, started, publicBaseUrl) {
    if (message.author?.id !== botId || !(Date.parse(message.timestamp) >= started)
        || !/^(Jellyfin watch-party invitation|One-use Jellyfin watch-party invitation|Accept your Jellyfin invitation|Reset your Jellyfin password|Your one-time Jellyfin password reset \(five minutes\):)/.test(message.content || '')) return false;
    const base = new URL(publicBaseUrl);
    return (message.content.match(/https:\/\/[^\s<>]+/g) || []).some(value => {
        try {
            const link = new URL(value);
            return link.origin === base.origin && link.pathname.startsWith(base.pathname.replace(/\/$/, '') + '/') && !link.username && !link.password;
        } catch { return false; }
    });
}
