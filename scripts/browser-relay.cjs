// Disposable browser fixtures only. Fixed upstream; preserve native auth/origin
// and WebSocket handshakes instead of manufacturing sessions or replaying writes.
const assert = require('node:assert/strict');
const http = require('node:http');

async function openBrowserRelay(address) {
    const upstream = new URL(address);
    assert.equal(upstream.protocol, 'http:');
    assert(['127.0.0.1', 'localhost', 'host.docker.internal'].includes(upstream.hostname));
    assert(!upstream.username && !upstream.password && !upstream.search && !upstream.hash);
    const sockets = new Set();
    const track = socket => { sockets.add(socket); socket.once('close', () => sockets.delete(socket)); };
    const target = request => { const value = new URL(request.url, upstream.origin); if (value.origin !== upstream.origin) throw Error('Off-target relay request'); return value; };
    const headers = response => `HTTP/1.1 ${response.statusCode} ${response.statusMessage}\r\n` + response.rawHeaders.reduce((text, value, index, all) => index % 2 ? text : text + value + ': ' + all[index + 1] + '\r\n', '') + '\r\n';
    const server = http.createServer((request, response) => {
        let address;
        try { address = target(request); } catch { response.writeHead(400); response.end(); return; }
        // Host and Origin must remain the browser's exact pair for native checks.
        const outgoing = http.request(address, { method: request.method, headers: request.headers, agent: false }, incoming => {
            response.writeHead(incoming.statusCode, incoming.headers); incoming.pipe(response);
            incoming.on('error', () => response.destroy());
        });
        outgoing.on('error', () => { if (response.headersSent) response.destroy(); else { response.writeHead(502); response.end(); } });
        outgoing.setTimeout(30000, () => outgoing.destroy());
        request.on('aborted', () => outgoing.destroy());
        response.on('close', () => { if (!response.writableEnded) outgoing.destroy(); });
        request.pipe(outgoing);
    });
    server.on('connection', track);
    server.on('upgrade', (request, client, clientHead) => {
        let address;
        try { address = target(request); } catch { client.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n'); return; }
        const outgoing = http.request(address, { method: request.method, headers: request.headers, agent: false });
        outgoing.on('error', () => client.destroy());
        outgoing.setTimeout(15000, () => outgoing.destroy());
        client.on('error', () => outgoing.destroy());
        outgoing.on('response', response => {
            // Native 401/403/404 responses remain denials, never fake upgrades.
            client.write(headers(response)); response.pipe(client);
            response.on('error', () => client.destroy());
        });
        outgoing.on('upgrade', (response, socket, upstreamHead) => {
            track(socket); socket.setTimeout(0);
            client.write(headers(response));
            if (clientHead.length) socket.write(clientHead);
            if (upstreamHead.length) client.write(upstreamHead);
            socket.on('error', () => client.destroy());
            socket.once('close', () => client.destroy());
            client.once('close', () => socket.destroy());
            socket.pipe(client); client.pipe(socket);
        });
        outgoing.end();
    });
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
    const origin = `http://127.0.0.1:${server.address().port}`;
    return {
        baseUrl: origin + upstream.pathname.replace(/\/$/, ''),
        origin,
        close: async () => {
            for (const socket of sockets) socket.destroy();
            server.closeAllConnections();
            await new Promise(resolve => server.close(resolve));
        }
    };
}
module.exports = { openBrowserRelay };
