import test from 'node:test';
import assert from 'node:assert/strict';
import { inspect } from 'node:util';
import { privateCommand } from './private-command.mjs';

test('privileged command failures discard arguments, output and nested errors', () => {
    const sentinel = 'synthetic-credential-do-not-log';
    assert.throws(() => privateCommand(process.execPath, ['-e',
        `console.log('${sentinel}');console.error('${sentinel}');process.exit(1)`]), error => {
        assert(!inspect(error, { depth: 10 }).includes(sentinel));
        assert(!JSON.stringify(error).includes(sentinel));
        assert.equal(error.cause, undefined);
        assert.equal(error.stdout, undefined);
        assert.equal(error.stderr, undefined);
        return /output withheld/.test(error.message);
    });
    assert.throws(() => privateCommand('missing-privileged-command', [sentinel]), /output withheld/);
});

test('privileged commands suppress HTTP and Git tracing without mutating the parent environment', () => {
    const keys = ['GH_DEBUG', 'DEBUG', 'GIT_TRACE', 'GIT_TRACE_CURL', 'GIT_CURL_VERBOSE'];
    const before = keys.map(key => process.env[key]);
    try {
        for (const key of keys) process.env[key] = '1';
        assert.equal(privateCommand(process.execPath, ['-e',
            `console.log(JSON.stringify(${JSON.stringify(keys)}.filter(key=>process.env[key])))`]), '[]');
        for (const key of keys) assert.equal(process.env[key], '1');
    } finally {
        keys.forEach((key, index) => { if (before[index] === undefined) delete process.env[key]; else process.env[key] = before[index]; });
    }
});
