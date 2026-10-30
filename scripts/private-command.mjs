// Privileged subprocess output is never diagnostic material: it may contain secrets.
import { execFileSync } from 'node:child_process';

export function privateCommand(command, args, { trim = true } = {}) {
    const env = Object.fromEntries(Object.entries(process.env).filter(([key]) =>
        !/^(?:GH_DEBUG|DEBUG|GIT_TRACE.*|GIT_CURL_VERBOSE)$/i.test(key)));
    try {
        const output = execFileSync(command, args, {
            encoding: 'utf8', maxBuffer: 1024 * 1024, stdio: 'pipe', env
        });
        return trim ? output.trim() : output;
    } catch {
        // Do not retain a cause, command arguments, stdout, stderr or the original error.
        throw Error('Privileged command failed; output withheld to protect credentials.');
    }
}
