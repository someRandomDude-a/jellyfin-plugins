import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const quote = value => "'" + value.replaceAll("'", "''") + "'";
async function fixture(handler, { maxBytes = 1024, metadata = false } = {}) {
    const server = createServer(handler);
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = join(tmpdir(), 'opencode'); await mkdir(base, { recursive: true });
    const directory = await mkdtemp(join(base, 'bounded-tool-http-')), destination = join(directory, 'archive.zip');
    try {
        const helper = fileURLToPath(new URL('../../scripts/tool-http.ps1', import.meta.url));
        const mode = metadata ? ' -Quiet' : ' -Destination ' + quote(destination);
        const pipeline = metadata ? ' | ConvertFrom-Json | ConvertTo-Json -Compress' : '';
        const script = `$ErrorActionPreference='Stop'\n. ${quote(helper)}\nInvoke-ToolHttp -Name fixture -Uri 'http://127.0.0.1:${server.address().port}/'${mode} -TimeoutSeconds 1 -MaxBytes ${maxBytes}${pipeline}`;
        const started = Date.now();
        const result = await new Promise((resolve, reject) => {
            const child = spawn(process.env.PWSH || 'pwsh', ['-NoLogo','-NoProfile','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')], { timeout: 15000 });
            let stdout = '', stderr = '';
            child.stdout.on('data', chunk => { stdout += chunk; }); child.stderr.on('data', chunk => { stderr += chunk; });
            child.on('error', reject); child.on('close', (code, signal) => resolve({ code, signal, stdout, stderr, duration: Date.now() - started }));
        });
        assert.equal(result.signal, null, 'The HTTP deadline must fire; the test watchdog must not have to kill PowerShell');
        assert(result.duration < 12000, 'HTTP transfer exceeded its bounded deadline');
        let bytes; try { bytes = await readFile(destination); } catch (error) { if (error.code !== 'ENOENT') throw error; }
        return { ...result, bytes };
    } finally {
        server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
        await rm(directory, { recursive: true, force: true });
    }
}
test('bounded tool transfer succeeds and reports the stage and byte count', async () => {
    const result = await fixture((_request, response) => response.end('verified later'));
    assert.equal(result.code, 0, result.stdout + result.stderr);
    assert.equal(result.bytes.toString(), 'verified later');
    assert.match(result.stdout, /Fetching fixture/); assert.match(result.stdout, /Received fixture/);
});
test('metadata mode emits only JSON text, never internal Task return values', async () => {
    const result = await fixture((_request, response) => response.end('{"ok":true}'), { metadata: true });
    assert.equal(result.code, 0, result.stdout + result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), { ok: true }); assert.equal(result.bytes, undefined);
});
test('a server that never sends headers cannot hang bootstrap', async () => {
    const result = await fixture(() => {});
    assert.notEqual(result.code, 0); assert.match(result.stderr, /Timed out fetching fixture/);
    assert.equal(result.bytes, undefined);
});
test('a stalled response body is cancelled and the partial download is preserved', async () => {
    const result = await fixture((_request, response) => { response.writeHead(200, { 'Content-Length': 100 }); response.write('partial'); });
    assert.notEqual(result.code, 0); assert.match(result.stderr, /Timed out fetching fixture/);
    assert.equal(result.bytes.toString(), 'partial');
});
test('continuous trickle progress cannot reset the total download deadline', async () => {
    const result = await fixture((_request, response) => {
        response.writeHead(200, { 'Content-Length': 1000 }); response.write('.');
        const timer = setInterval(() => response.write('.'), 50);
        response.on('close', () => clearInterval(timer));
    });
    assert.notEqual(result.code, 0); assert.match(result.stderr, /Timed out fetching fixture/);
    assert(result.bytes.length > 1 && result.bytes.length < 1000);
});
test('an HTTP failure reports status without leaking upstream response prose', async () => {
    const result = await fixture((_request, response) => { response.writeHead(403); response.end('PRIVATE_UPSTREAM_CANARY'); });
    assert.notEqual(result.code, 0); assert.match(result.stderr, /HTTP 403 fetching fixture/);
    assert(!result.stderr.includes('PRIVATE_UPSTREAM_CANARY'));
});
test('oversized downloads fail before any bytes are accepted as an archive', async () => {
    const result = await fixture((_request, response) => { response.writeHead(200, { 'Content-Length': 1025 }); response.end('too large'); });
    assert.notEqual(result.code, 0); assert.match(result.stderr, /size limit/);
    assert.equal(result.bytes, undefined);
});
