// Bound structured-cloned RPC data BEFORE the SDK/Zod validator builds issue arrays.
// The SDK still owns protocol/origin validation. Never log rejected message contents.
export function withinMessageBudget(value) {
    const pending = [[value, 0]], seen = new Set();
    let nodes = 0, characters = 0;
    while (pending.length) {
        const [item, depth] = pending.pop();
        if (++nodes > 1024 || depth > 16) return false;
        if (typeof item === 'string') {
            if (item.length > 4096 || (characters += item.length) > 32768) return false;
        } else if (item !== null && typeof item === 'object') {
            if (seen.has(item)) return false;
            seen.add(item);
            if (Array.isArray(item)) {
                if (item.length > 256) return false;
                for (const child of item) pending.push([child, depth + 1]);
            } else {
                const prototype = Object.getPrototypeOf(item);
                if (prototype !== Object.prototype && prototype !== null) return false;
                let fields = 0;
                for (const key in item) {
                    if (!Object.hasOwn(item, key)) continue;
                    if (++fields > 128 || (characters += key.length) > 32768) return false;
                    pending.push([item[key], depth + 1]);
                }
            }
        } else if (!['undefined', 'boolean', 'number'].includes(typeof item) && item !== null) return false;
    }
    return true;
}
export function installMessageBudget(target) {
    const guard = event => {
        try { if (withinMessageBudget(event.data)) return; } catch { /* Reject malformed data, without payload diagnostics. */ }
        event.stopImmediatePropagation();
    };
    target.addEventListener('message', guard, true);
    return () => target.removeEventListener('message', guard, true);
}
