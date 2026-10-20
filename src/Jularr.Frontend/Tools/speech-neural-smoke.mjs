// Manual, real-model integration check. No model is fetched by this tool: supply the already verified archives.
import assert from 'node:assert/strict';
import { readFile, writeFile, mkdir, stat } from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import { createServer } from 'node:http';
import { resolve, extname, basename, sep } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = resolve(import.meta.dirname, '../../..');
const { chromium } = await import(pathToFileURL(resolve(root, 'tests/Jularr.E2E.Tests/bin/Debug/net10.0/.playwright/package/index.mjs')).href);
const directory = resolve(process.argv[2] ?? resolve(root, '.scratch/speech-neural-spike'));
const id = process.argv[3] ?? 'piper-de-thorsten';
const catalog = JSON.parse(await readFile(resolve(root, 'src/Jularr.Backend/Logic/Speech/models.json'), 'utf8')).models;
const model = catalog.find(model => model.id === id);
assert(model, 'Unknown catalog model');
const packagePath = resolve(directory, basename(new URL(model.source).pathname));
assert.equal((await stat(packagePath)).size, model.sizeBytes);
const build = resolve(root, 'src/Jularr.Frontend/wwwroot/build');
const manifest = JSON.parse(await readFile(resolve(root, 'src/Jularr.Frontend/Assets/Generated/manifest.json'), 'utf8'));
const install = '/build/' + manifest['Modules/Speech/ModelInstall.ts'].file;
const neural = '/build/' + manifest['Modules/Speech/NeuralSession.ts'].file;
const errors = [], requests = [], workers = new Set();
const browser = await chromium.launch();
// Stream large fixtures over loopback instead of base64-copying >100 MiB through the browser's debugging connection.
const server = createServer((request, response) => {
    if (request.method !== 'GET' || request.url !== '/api/v1/speech/models/' + model.sha256 + '/file') { response.writeHead(404); response.end(); return; }
    response.writeHead(200, { 'content-type': 'application/octet-stream', 'content-length': model.sizeBytes });
    createReadStream(packagePath).pipe(response);
});
try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const origin = 'http://127.0.0.1:' + server.address().port;
    const page = await browser.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('worker', worker => { workers.add(worker); worker.on('close', () => workers.delete(worker)); });
    await page.route('**/*', async route => {
        const url = new URL(route.request().url());
        assert.equal(url.origin, origin); requests.push(url.pathname);
        if (url.pathname === '/') return route.fulfill({ contentType: 'text/html', body: `<!doctype html><meta charset="utf-8"><button id="install">Install</button><button id="read">Read</button><script type="module">
            import { installModel } from ${JSON.stringify(install)};
            import { NeuralSession } from ${JSON.stringify(neural)};
            window.states=[]; window.marks=[]; window.messages=[]; window.workerEvents=[]; window.beats=[];
            const NativeWorker=Worker;
            window.Worker=class extends NativeWorker {
                constructor(...args){super(...args);this.addEventListener('message',event=>window.workerEvents.push({type:event.data.type,id:event.data.id,message:event.data.message}));}
                postMessage(message, transfer) { window.messages.push({type:message.type, id:message.id, text:message.text}); super.postMessage(message,transfer); }
            };
            let previous=performance.now(); setInterval(()=>{const now=performance.now();window.beats.push(now-previous);previous=now;},20);
            window.session=new NeuralSession(state=>window.states.push(state),(item,start,length)=>window.marks.push({start,length}));
            window.database=()=>new Promise((resolve,reject)=>{const r=indexedDB.open('jularr-speech-models',1); r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(r.error);});
            document.querySelector('#install').onclick=async()=>{try {await installModel(${JSON.stringify(model)},new AbortController().signal);window.installed=true;}catch(e){window.problem=String(e);}};
            document.querySelector('#read').onclick=async()=>{try {window.context=new AudioContext();void context.resume();const db=await database();const record=await new Promise(resolve=>{const r=db.transaction('metadata').objectStore('metadata').get(${JSON.stringify(id)});r.onsuccess=()=>resolve(r.result);});db.close();session.start([{text:window.text??'Guten Tag. Dies ist ein lokaler Test der Stimme.',range:{}}],{rate:1,pitch:1,volume:.5,language:${JSON.stringify(model.language)}},record,context);}catch(e){window.problem=String(e);}};
            window.prepared=true;
        </script>` });
        if (url.pathname === '/api/v1/speech/models/' + model.sha256 + '/file') return route.continue();
        const path = resolve(build, '.' + url.pathname.slice('/build'.length));
        assert(path.startsWith(build + sep));
        await route.fulfill({ body: await readFile(path), contentType: extname(path) === '.js' ? 'text/javascript' : extname(path) === '.wasm' ? 'application/wasm' : 'application/octet-stream' });
    });
    await page.goto(origin);
    await page.waitForFunction(() => window.prepared);
    assert.equal(requests.some(path => /\.wasm$|\.data$|neural\.worker|\/api\//.test(path)), false, 'No assets/model before an explicit action');
    const started = performance.now();
    await page.click('#install');
    await page.waitForFunction(() => window.installed || window.problem, null, { timeout: 180000 });
    assert.equal(await page.evaluate(() => window.problem), undefined);
    const installMs = Math.round(performance.now() - started);
    assert.equal(requests.some(path => /\.wasm$|\.data$/.test(path)), false, 'Installation checks the archive without starting inference');
    await page.click('#read');
    await page.waitForFunction(() => window.session.state === 'speaking' || window.session.state === 'failed' || window.problem, null, { timeout: 90000 });
    assert.equal(await page.evaluate(() => window.session.state), 'speaking', JSON.stringify({ errors, problem: await page.evaluate(() => window.problem), worker: await page.evaluate(() => window.workerEvents) }));
    await page.evaluate(() => window.session.pause());
    const time = await page.evaluate(() => window.context.currentTime);
    await page.waitForTimeout(200);
    assert.ok(Math.abs(await page.evaluate(() => window.context.currentTime) - time) < .03);
    await page.evaluate(() => window.session.resume());
    await page.waitForFunction(() => window.session.state === 'speaking');
    await page.evaluate(() => window.session.stop());
    await page.waitForTimeout(2300);
    assert.equal(workers.size, 0);
    assert.equal(await page.evaluate(() => window.context.state), 'closed');
    await page.reload(); await page.waitForFunction(() => window.prepared);
    await page.click('#read');
    await page.waitForFunction(() => ['speaking','failed'].includes(window.session.state), null, { timeout: 90000 });
    assert.equal(await page.evaluate(() => window.session.state), 'speaking', 'Reload uses the verified device cache');
    await page.evaluate(() => window.session.stop()); await page.waitForTimeout(2300);
    assert.equal(requests.filter(path => path.startsWith('/api/v1/speech/models/')).length, 1);
    await page.evaluate(async id => {
        const db=await window.database();await new Promise((resolve,reject)=>{const tx=db.transaction('files','readwrite');const store=tx.objectStore('files');const r=store.get(id);r.onsuccess=()=>{new Uint8Array(r.result.tokens)[0]^=1;store.put(r.result,id);};tx.oncomplete=resolve;tx.onerror=()=>reject(tx.error);});db.close();
    }, id);
    await page.click('#read');
    await page.waitForFunction(() => window.session.state === 'failed');
    assert.equal(workers.size, 0, 'Corrupt cached bytes do not start inference or a silent re-download');
    assert.equal(errors.length, 0, errors.join('\n'));
    const report = { id, installMs, modelDownloads: requests.filter(path => path.startsWith('/api/v1/speech/models/')).length,
        realAudioPlayed: true, pauseResume: true, reloadFromCache: true, corruptCacheRejected: true, liveWorkers: workers.size,
        marks: await page.evaluate(() => window.marks), maxMainThreadIntervalMs: await page.evaluate(() => Math.max(...window.beats)), errors };
    const evidence = resolve(root, '.scratch/speech-neural-spike'); await mkdir(evidence, { recursive: true });
    await writeFile(resolve(evidence, 'browser-integration-' + id + '.json'), JSON.stringify(report, null, 2));
    console.log(report);
} finally {
    await browser.close();
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
}
