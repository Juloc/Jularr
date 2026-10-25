#!/usr/bin/env node
// Disposable staging probe. This detects browser callbacks; it does not enforce isolation.
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import { setTimeout as sleep } from 'node:timers/promises';

const args = process.argv.slice(2);
const arg = name => { const index = args.indexOf(name); return index < 0 ? undefined : args[index + 1]; };
let port;
let seconds;
try {
    port = Number(arg('--port') ?? fs.readFileSync(arg('--port-file') ?? '/data/browser/managed/profile/browser.port', 'utf8').trim());
    seconds = Number(arg('--seconds') ?? 8);
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw Error('Invalid CDP port.');
    if (!Number.isInteger(seconds) || seconds < 3 || seconds > 30) throw Error('Seconds must be 3–30.');
} catch (error) {
    console.error('INCONCLUSIVE: test setup failed: ' + error.message);
    process.exit(2);
}
const base = 'http://127.0.0.1:' + port;
const hits = [];
const servers = [];
const sockets = [];
let controller;
let target;

async function sentinel(label, host) {
    const server = http.createServer((request, response) => {
        hits.push({ label, transport: 'http', method: request.method, path: request.url });
        response.writeHead(204, { 'Cache-Control': 'no-store' });
        response.end();
    });
    // A TLS handshake to an HTTP sentinel still proves the browser opened a socket.
    server.on('connection', socket => hits.push({ label, transport: 'tcp', from: socket.remoteAddress }));
    await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, host, resolve);
    });
    servers.push(server);
    return { host, port: server.address().port };
}

function containerIpv4() {
    for (const entries of Object.values(os.networkInterfaces())) {
        for (const entry of entries ?? []) if (entry.family === 'IPv4' && !entry.internal) return entry.address;
    }
    return null;
}

async function cdpJson(path) {
    const response = await fetch(base + path, { signal: AbortSignal.timeout(6000) });
    if (!response.ok) throw Error('CDP HTTP ' + response.status);
    return response.json();
}

async function connect(url) {
    const uri = new URL(url);
    if (uri.protocol !== 'ws:' || !['127.0.0.1', 'localhost', '[::1]'].includes(uri.hostname) || uri.port !== String(port)) {
        throw Error('Refusing non-loopback CDP websocket.');
    }
    const ws = new WebSocket(url);
    await new Promise((resolve, reject) => {
        const deadline = setTimeout(() => reject(Error('CDP timeout')), 6000);
        ws.addEventListener('open', () => { clearTimeout(deadline); resolve(); }, { once: true });
        ws.addEventListener('error', () => { clearTimeout(deadline); reject(Error('CDP websocket error')); }, { once: true });
    });
    sockets.push(ws);
    let next = 0;
    const pending = new Map();
    ws.addEventListener('message', event => {
        let data;
        try { data = JSON.parse(String(event.data)); } catch { return; }
        const entry = pending.get(data.id);
        if (!entry) return;
        pending.delete(data.id);
        clearTimeout(entry.timeout);
        if (data.error) entry.reject(Error(JSON.stringify(data.error)));
        else entry.resolve(data.result);
    });
    ws.addEventListener('close', () => {
        for (const entry of pending.values()) {
            clearTimeout(entry.timeout);
            entry.reject(Error('CDP socket closed'));
        }
        pending.clear();
    });
    return {
        send(method, params = {}) {
            const id = ++next;
            return new Promise((resolve, reject) => {
                const timeout = setTimeout(() => { pending.delete(id); reject(Error(method + ' timeout')); }, 10000);
                pending.set(id, { resolve, reject, timeout });
                ws.send(JSON.stringify({ id, method, params }));
            });
        }
    };
}

try {
    const loop4 = await sentinel('loopback-v4', '127.0.0.1');
    let loop6;
    try { loop6 = await sentinel('loopback-v6', '::1'); }
    catch (error) {
        if (!['EADDRNOTAVAIL', 'EAFNOSUPPORT'].includes(error.code)) throw error;
        console.log('IPv6 not available: NOT TESTED.');
    }
    const localAddress = containerIpv4();
    const local = localAddress ? await sentinel('container-ipv4', localAddress) : null;
    const endpoints = [loop4, ...(loop6 ? [loop6] : []), ...(local ? [local] : [])]
        .flatMap(endpoint => {
            const hostname = endpoint.host.includes(':') ? '[' + endpoint.host + ']' : endpoint.host;
            return ['http', 'https'].map(scheme => scheme + '://' + hostname + ':' + endpoint.port);
        });
    console.log('Testing own sentinel endpoints: ' + endpoints.join(' '));
    const initial = (await cdpJson('/json/list')).find(page => page.type === 'page' && page.webSocketDebuggerUrl);
    if (!initial) throw Error('No running Chrome page');
    controller = await connect(initial.webSocketDebuggerUrl);
    const created = await controller.send('Target.createTarget', { url: 'about:blank' });
    target = created.targetId;
    let testPage;
    for (let i = 0; i < 12; i++) {
        testPage = (await cdpJson('/json/list')).find(page => page.id === target && page.webSocketDebuggerUrl);
        if (testPage) break;
        await sleep(250);
    }
    if (!testPage) throw Error('Could not open test tab');
    const browser = await connect(testPage.webSocketDebuggerUrl);
    await browser.send('Page.enable');
    await browser.send('Network.enable');
    const payload = '<!doctype html><body><script>' +
        'const urls=' + JSON.stringify(endpoints) + ';' +
        'for(const [i,u] of urls.entries()) {' +
        'let img=document.createElement("img");img.src=u+"/img/"+i;document.body.append(img);' +
        'let frame=document.createElement("iframe");frame.src=u+"/iframe/"+i;document.body.append(frame);' +
        'let script=document.createElement("script");script.src=u+"/script/"+i;document.body.append(script);' +
        'fetch(u+"/fetch/"+i,{mode:"no-cors",cache:"no-store"}).catch(()=>{});' +
        'try {let socket=new WebSocket(u.replace(/^http/,"ws")+"/ws/"+i);socket.onerror=()=>{};}catch{}' +
        '}</script>';
    const navigation = await browser.send('Page.navigate', { url: 'data:text/html;charset=utf-8,' + encodeURIComponent(payload) });
    if (navigation.errorText) throw Error('CDP navigation failed: ' + navigation.errorText);
    await sleep(seconds * 1000);
    // Also navigate directly to each owned sentinel. Browser preflight protection and
    // data-URL restrictions can otherwise hide a socket bypass in subresource tests.
    // A blocked navigation is expected, while any sentinel callback is a real failure.
    const direct = [];
    for (const url of endpoints) {
        const navigation = await browser.send('Page.navigate', { url: url + '/direct' });
        direct.push({ target: url, error: navigation.errorText ?? null });
        await sleep(250);
    }
    console.log(JSON.stringify({ browserCdpPort: port, seconds, sentinels: endpoints, callbacks: hits, direct }, null, 2));
    if (hits.length) {
        console.error('FAIL: Chrome reached an internal sentinel; leave Browser OFF.');
        process.exitCode = 1;
    } else {
        console.log('LIMITED PASS: no sentinel callback. Does NOT prove process isolation, redirects, rebinding, UDP, or public-to-private subresources.');
    }
} catch (error) {
    console.error('INCONCLUSIVE: ' + (error.stack ?? String(error)));
    process.exitCode = 2;
} finally {
    if (controller && target) {
        try { await controller.send('Target.closeTarget', { targetId: target }); } catch { /* Browser closed. */ }
    }
    for (const ws of sockets) ws.close();
    await Promise.all(servers.map(server => new Promise(resolve => server.close(resolve))));
}
