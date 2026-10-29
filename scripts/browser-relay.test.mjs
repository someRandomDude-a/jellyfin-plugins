import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import { createRequire } from 'node:module';
const { openBrowserRelay } = createRequire(import.meta.url)('./browser-relay.cjs');
async function upstream(t, handler) {
    const server=http.createServer(handler),sockets=new Set();
    server.on('connection',socket=>{sockets.add(socket);socket.once('close',()=>sockets.delete(socket));});
    await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
    t.after(async()=>{for(const socket of sockets)socket.destroy();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));});
    return {server,address:'http://127.0.0.1:'+server.address().port+'/suite'};
}
function readUntil(socket,predicate){return new Promise((resolve,reject)=>{let received='';socket.on('data',chunk=>{received+=chunk.toString();if(predicate(received))resolve(received);});socket.once('error',reject);});}
test('browser relay preserves request body, browser Host/Origin and native authorization without retries',async t=>{
    let writes=0;
    const source=await upstream(t,(request,response)=>{let body='';request.on('data',chunk=>{body+=chunk;});request.on('end',()=>{writes++;response.setHeader('Content-Type','application/json');response.end(JSON.stringify({url:request.url,host:request.headers.host,origin:request.headers.origin,authorization:request.headers.authorization,body}));});});
    const relay=await openBrowserRelay(source.address);t.after(()=>relay.close());
    const response=await fetch(relay.baseUrl+'/check?value=1',{method:'POST',headers:{Origin:relay.origin,Authorization:'Bearer fixture-only'},body:'owned fixture',signal:AbortSignal.timeout(5000)});
    assert.equal(response.status,200);assert.deepEqual(await response.json(),{url:'/suite/check?value=1',host:new URL(relay.origin).host,origin:relay.origin,authorization:'Bearer fixture-only',body:'owned fixture'});assert.equal(writes,1);
});
test('browser relay forwards upgrade protocols, both initial heads and bidirectional socket data',async t=>{
    const source=await upstream(t);let observed;
    source.server.on('upgrade',(request,socket,head)=>{
        observed=request.headers;socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Protocol: notifications.v1\r\n\r\nupstream-head');
        let data=head.toString();const echo=()=>{if(data.endsWith('client-tail'))socket.write('upstream-echo:'+data);};socket.on('data',chunk=>{data+=chunk;echo();});echo();
    });
    const relay=await openBrowserRelay(source.address);t.after(()=>relay.close());
    const socket=net.connect({host:'127.0.0.1',port:Number(new URL(relay.origin).port)});t.after(()=>socket.destroy());
    const received=readUntil(socket,value=>value.includes('upstream-echo:client-head-client-tail'));
    socket.write('GET /suite/socket HTTP/1.1\r\nHost: '+new URL(relay.origin).host+'\r\nOrigin: '+relay.origin+'\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Protocol: notifications.v1, ticket.fixture\r\n\r\nclient-head-');
    socket.write('client-tail');const result=await received;
    assert(result.includes('101 Switching Protocols'));assert(result.includes('Sec-WebSocket-Protocol: notifications.v1'));assert(result.includes('upstream-head'));
    assert.equal(observed.host,new URL(relay.origin).host);assert.equal(observed.origin,relay.origin);assert.equal(observed['sec-websocket-protocol'],'notifications.v1, ticket.fixture');
});
test('native WebSocket denials are forwarded without faking a session or upgrade',async t=>{
    const source=await upstream(t);source.server.on('upgrade',(_,socket)=>socket.end('HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n'));
    const relay=await openBrowserRelay(source.address);t.after(()=>relay.close());
    const socket=net.connect({host:'127.0.0.1',port:Number(new URL(relay.origin).port)});t.after(()=>socket.destroy());const received=readUntil(socket,value=>value.includes('\r\n\r\n'));
    socket.write('GET /suite/socket HTTP/1.1\r\nHost: '+new URL(relay.origin).host+'\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n');assert((await received).startsWith('HTTP/1.1 401'));
});
test('browser relay rejects off-target requests and unsafe upstream descriptors',async t=>{
    let requests=0;const source=await upstream(t,(_,response)=>{requests++;response.end();});
    const relay=await openBrowserRelay(source.address);t.after(()=>relay.close());
    const socket=net.connect({host:'127.0.0.1',port:Number(new URL(relay.origin).port)});t.after(()=>socket.destroy());const received=readUntil(socket,value=>value.includes('\r\n\r\n'));
    socket.write('GET http://off-target.invalid/ HTTP/1.1\r\nHost: '+new URL(relay.origin).host+'\r\nConnection: close\r\n\r\n');assert((await received).startsWith('HTTP/1.1 400'));assert.equal(requests,0);
    for(const address of ['http://off-target.invalid','https://127.0.0.1','http://user:password@127.0.0.1','http://127.0.0.1?secret=x'])await assert.rejects(()=>openBrowserRelay(address));
});
