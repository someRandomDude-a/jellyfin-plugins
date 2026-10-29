import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';

// Test tooling only. No plugin dependency, account, tunnel token or host mounts.
export const tunnelImage = 'cloudflare/cloudflared:2026.10.6@sha256:004d843fa5fdc3103983fd580ee19fab4a8fa98add31f87249c1659e848c98e0';
export function probeTrustedProxyAuth(fixtureName,origin,token) {
    assert(/^jellyfin-suite-test-[a-f0-9]{8}$/.test(fixtureName));
    assert(quickTunnelOrigin(origin)===origin&&new URL(origin).origin===origin);
    assert(typeof token==='string'&&token.length>0);
    // Run in the SAME loopback namespace as cloudflared. Host-published ports
    // arrive from an untrusted Docker bridge address and are not equivalent.
    // Native credentials cross stdin, never command arguments, logs or files.
    const script=`const http=require('node:http');let input='';process.stdin.setEncoding('utf8');process.stdin.on('data',chunk=>input+=chunk);process.stdin.on('end',async()=>{try{const data=JSON.parse(input);const call=(origin,self=false)=>new Promise((resolve,reject)=>{const headers={Host:new URL(data.origin).host,'X-Forwarded-Proto':'https','Content-Type':'application/json',Authorization:'MediaBrowser Client="SuiteTest", Device="Disposable", DeviceId="suite-test", Version="1", Token="'+data.token+'"'};if(origin)headers.Origin=origin;const q=http.request('http://127.0.0.1:8096/suite/AccountHub/api/v1/'+(self?'self':'discord/link/lookup'),{method:self?'GET':'POST',headers},r=>{r.resume();r.on('end',()=>resolve(r.statusCode));});q.setTimeout(15000,()=>q.destroy(Error('deadline')));q.on('error',reject);q.end(self?undefined:JSON.stringify({token:'a'.repeat(43)}));});console.log(JSON.stringify({nativeSession:await call(null,true),sameOrigin:await call(data.origin),hostileOrigin:await call('https://attacker.invalid')}));}catch{process.exitCode=1;}});`;
    const result=spawnSync('docker',['run','--pull=always','--rm','-i','--read-only','--cap-drop=ALL','--security-opt=no-new-privileges','--network','container:'+fixtureName,'--entrypoint','node','mcr.microsoft.com/playwright:v1.64.1-noble@sha256:47b510dd9230bf0b9bfc9b20102241e577ea7a99334ab4c3ca19f19770e9b8ea','-e',script],{encoding:'utf8',input:JSON.stringify({origin,token}),timeout:60000,maxBuffer:65536});
    assert.equal(result.status,0,'Trusted-proxy native authorization probe failed (sensitive details withheld)');
    return JSON.parse(result.stdout.trim());
}
export function quickTunnelOrigin(log) {
    const match = log.match(/https:\/\/[a-z0-9]+(?:-[a-z0-9]+)*\.trycloudflare\.com(?=[\s|]|$)/);
    return match?.[0];
}
// Use the isolated Docker runtime's DNS rather than altering host DNS. The host
// resolver may reject newly issued names. Normal hostname TLS checks stay on.
export function probeDiscordTunnel(origin, docker, path = '/suite/System/Info/Public') {
    const url = new URL(origin);
    assert(quickTunnelOrigin(origin) === origin && url.origin === origin, 'Only generated Cloudflare HTTPS origins may be probed');
    assert(['/suite/System/Info/Public', '/suite/probe'].includes(path), 'Only public fixture probe routes are allowed');
    const script = `const dns=require('dns'),https=require('https');const fail=e=>{const code=e.code||e.name;console.log(JSON.stringify({status:0,text:'',errorCode:/^[A-Za-z0-9_]{1,60}$/.test(code)?code:'transport_error'}))};const q=https.get(${JSON.stringify(new URL(path, url).href)},{family:4,lookup:(host,options,cb)=>dns.resolve4(host,(e,a)=>e?cb(e):cb(null,a[0],4))},r=>{let text='';r.setEncoding('utf8');r.on('data',c=>{text+=c;if(text.length>65536)q.destroy(Error('size'))});r.on('end',()=>console.log(JSON.stringify({status:r.statusCode,text})));r.on('error',fail)});q.setTimeout(10000,()=>q.destroy(Error('deadline')));q.on('error',fail)`;
    return JSON.parse(docker('run', '--rm', '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges', '--entrypoint', 'node',
        'mcr.microsoft.com/playwright:v1.64.1-noble@sha256:47b510dd9230bf0b9bfc9b20102241e577ea7a99334ab4c3ca19f19770e9b8ea', '-e', script));
}
export async function startDiscordTunnel(fixtureName, docker, { wait = ms => new Promise(resolve => setTimeout(resolve, ms)), attempts = 60 } = {}) {
    assert(/^jellyfin-suite-test-[a-f0-9]{8}$/.test(fixtureName), 'Tunnel target must be an owned disposable suite container');
    const name = 'jellyfin-suite-tunnel-' + randomUUID().slice(0, 8);
    let created = false;
    const stop = () => { if (created) { docker('rm', '-f', name); created = false; } };
    try {
        // Share only the disposable fixture's network namespace; not the host
        // network. Port 8096 is fixed and callers cannot supply another target.
        docker('run', '-d', '--name', name, '--label', 'jellyfin.suite.tunnel-test=true', '--network', 'container:' + fixtureName,
            '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges',
            tunnelImage, 'tunnel', '--no-autoupdate', '--protocol', 'http2', '--url', 'http://127.0.0.1:8096');
        created = true;
        for (let i = 0; i < attempts; i++) {
            const origin = quickTunnelOrigin(docker('logs', name));
            if (origin) return { origin, stop, connectionReady: () => /Registered tunnel connection/.test(docker('logs', name)) };
            if (docker('inspect', '--format', '{{.State.Running}}', name) !== 'true') break;
            await wait(1000);
        }
        throw Error('Temporary HTTPS tunnel did not start; no raw tunnel logs are emitted');
    } catch (error) {
        try { stop(); } catch { throw Error('Tunnel startup/cleanup failed; inspect owned jellyfin-suite-tunnel containers manually'); }
        throw Error('Temporary HTTPS tunnel startup failed (details withheld)');
    }
}
