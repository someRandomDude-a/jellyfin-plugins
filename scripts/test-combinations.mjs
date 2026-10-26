// Exhaustive subsets of six installable plugins; each child owns a NEW disposable server.
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import assert from 'node:assert/strict';

const option = name => process.argv.find(value => value.startsWith('--' + name + '='))?.split('=').slice(1).join('=');
const packages = resolve(option('packages') || 'artifacts/runtime-review/combinations/linux-x64');
const output = resolve(option('output') || 'artifacts/combination-tests/' + Date.now());
const security = option('security');
const jobs = Number(option('jobs') || 2); assert(Number.isInteger(jobs) && jobs >= 1 && jobs <= 4);
await mkdir(output, { recursive: true });
const names = ['hub', 'discord', 'deletion', 'syncplay', 'notifications', 'automatic'], cases = [];
for (const withSecurity of security ? [false, true] : [false]) for (let mask = 0; mask < (1 << names.length); mask++) cases.push({ plugins: names.filter((_, bit) => mask & (1 << bit)).join(',') || 'none', withSecurity });
let next = 0; const results = [];
async function worker() {
    while (next < cases.length) {
        const fixture = cases[next++], label = fixture.plugins.replaceAll(',', '+') + (fixture.withSecurity ? '+security' : '');
        const args = ['scripts/integration.mjs', '--packages=' + packages, '--plugins=' + fixture.plugins];
        if (fixture.withSecurity) args.push('--security=' + resolve(security));
        const result = await new Promise(resolve => {
            const child = spawn(process.execPath, args, { env: process.env, stdio: ['ignore', 'pipe', 'pipe'] }); let text = '';
            child.stdout.on('data', chunk => { text += chunk; }); child.stderr.on('data', chunk => { text += chunk; });
            child.on('error', error => resolve({ passed: false, text: String(error) })); child.on('close', code => resolve({ passed: code === 0, text }));
        });
        await writeFile(join(output, label + '.log'), result.text); results.push({ ...fixture, label, passed: result.passed });
        console.log(result.passed ? 'PASS' : 'FAIL', label);
    }
}
await Promise.all(Array.from({ length: jobs }, worker));
await writeFile(join(output, 'results.json'), JSON.stringify(results, null, 2));
assert(results.every(result => result.passed), 'Some independent/plugin-combination fixtures failed; see ' + output);
console.log('PASS all ' + results.length + ' plugin combinations; sanitized logs:', output);
