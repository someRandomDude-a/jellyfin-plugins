import { createHash } from 'node:crypto';

export function dependencyFingerprint(rows, locks) {
    const stableRows = rows.map(row => JSON.stringify(row)).sort();
    const stableLocks = locks.map(([path, text]) => [path, text.replace(/\r\n/g, '\n')]).sort(([a], [b]) => a.localeCompare(b));
    return createHash('sha256').update(JSON.stringify([stableRows, stableLocks])).digest('hex');
}
export function assertDependencyEvidence(evidence, identity, now = Date.now()) {
    const age = now - Date.parse(evidence?.resolvedAt);
    if (evidence?.schemaVersion !== 1 || evidence.policy !== 'latest-stable' || evidence.verified !== true ||
        !Number.isFinite(age) || age < 0 || age > 86400000 ||
        !/^[a-f0-9]{40}$/.test(identity.source || '') || !/^[a-f0-9]{64}$/.test(identity.fingerprint || '') ||
        !/^(?:\d+|[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})$/.test(identity.run || '') || !/^\d+$/.test(identity.attempt || '') ||
        ['source', 'fingerprint', 'run', 'attempt'].some(key => evidence[key] !== identity[key])) {
        throw Error('Invalid, expired or different-source dependency audit; rerun the parent target gate. No cached/older fallback is permitted');
    }
}
