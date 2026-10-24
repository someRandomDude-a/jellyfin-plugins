import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';

// Test tooling only. No plugin dependency, account, tunnel token or host mounts.
export const tunnelImage = 'cloudflare/cloudflared:2026.10.3@sha256:faa8e0a1475f137c910be6025f43c8e4092fdd35461b31e19fd1390657a1ff39';
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
        'mcr.microsoft.com/playwright:v1.56.1-noble@sha256:f1e7e01021efd65dd1a2c56064be399f3e4de00fd021ac561325f2bfbb2b837a', '-e', script));
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
