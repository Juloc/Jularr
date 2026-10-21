import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';
import Bunzip from '../Modules/Speech/Runtime/seek-bzip/index.js';

function load(name, dependencies = {}, globals = {}) {
    const source = readFileSync(new URL('../Modules/Speech/' + name + '.ts', import.meta.url), 'utf8');
    const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const exports = {};
    runInNewContext(script, { exports, TextDecoder, Uint8Array, Float32Array, ArrayBuffer, crypto: globalThis.crypto,
        require: name => { if (!(name in dependencies)) throw new Error('Unexpected dependency: ' + name); return dependencies[name]; }, ...globals });
    return exports;
}
const archive = load('ModelArchive', { './Runtime/seek-bzip/index.js': { default: Bunzip } });
const device = load('DeviceSession');

test('Browser-port decoder checks the real bzip2 stream and CRC before accepting a synthetic model archive', () => {
    const bytes = readFileSync(new URL('../../../tests/Jularr.E2E.Tests/Fixtures/speech-model.tar.bz2', import.meta.url));
    const input = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
    const result = archive.unpackModel(input);
    assert.match(new TextDecoder().decode(result.model), /MIT License/);
    const corrupt = new Uint8Array(input.slice(0)); corrupt[corrupt.length - 4] ^= 1;
    assert.throws(() => archive.unpackModel(corrupt.buffer), /CRC|checksum|data|bzip|Truncated/i);
});

function header(name, size, type = '0') {
    const bytes = Buffer.alloc(512);
    bytes.write(name); bytes.write(size.toString(8).padStart(11, '0') + '\0', 124);
    bytes.fill(32, 148, 156); bytes.write(type, 156);
    bytes.write(bytes.reduce((sum, byte) => sum + byte, 0).toString(8).padStart(6, '0') + '\0 ', 148);
    return bytes;
}
function entry(name, text, type = '0') {
    const bytes = Buffer.from(text);
    return Buffer.concat([header(name, bytes.length, type), bytes, Buffer.alloc((512 - bytes.length % 512) % 512)]);
}
function parse(bytes) { const tar = new archive.ModelTar(); for (const byte of bytes) tar.writeByte(byte); return tar.finish(); }

test('Speech tar retains one model and tokens, skips support data and requires a complete terminator', () => {
    const bytes = Buffer.concat([entry('voice/model.onnx', 'fixture'), entry('voice/espeak/data', 'ignored'), entry('voice/tokens.txt', 'a 0\n'), Buffer.alloc(1024)]);
    const result = parse(bytes);
    assert.equal(new TextDecoder().decode(result.model), 'fixture');
    assert.equal(new TextDecoder().decode(result.tokens), 'a 0\n');
    assert.throws(() => parse(bytes.subarray(0, bytes.length - 512)), /Incomplete/);
});

test('Speech tar rejects traversal, links, duplicate models, corrupt headers and oversized entries before allocation', () => {
    for (const path of ['../model.onnx', '/model.onnx', 'safe/../model.onnx', 'C:/model.onnx', 'safe\\model.onnx']) assert.throws(() => parse(entry(path, 'bad')), /Unsafe/);
    assert.throws(() => parse(entry('link', 'target', '2')), /Unsupported/);
    assert.throws(() => parse(Buffer.concat([entry('one.onnx', 'one'), entry('two.onnx', 'two')])), /exactly one/);
    assert.throws(() => parse(header('model.onnx', 192 * 1024 * 1024 + 1)), /limit/);
    assert.throws(() => parse(header('tokens.txt', 1024 * 1024 + 1)), /tokens/);
    const corrupt = header('model.onnx', 1); corrupt[1] ^= 1;
    assert.throws(() => parse(corrupt), /checksum/);
    const tar = new archive.ModelTar(); tar.total = archive.ExpandedLimit;
    assert.throws(() => tar.writeByte(0), /limit/);
});

test('Speech verifies the exact compressed byte count and SHA-256, never just the declared metadata', async () => {
    const bytes = new TextEncoder().encode('fixture').buffer;
    const hash = Buffer.from(await crypto.subtle.digest('SHA-256', bytes)).toString('hex');
    await archive.verifyArchive(bytes, bytes.byteLength, hash);
    await assert.rejects(archive.verifyArchive(bytes, bytes.byteLength + 1, hash), /size/);
    await assert.rejects(archive.verifyArchive(bytes, bytes.byteLength, '0'.repeat(64)), /integrity/);
    await assert.rejects(archive.verifyArchive(bytes, archive.ArchiveLimit + 1, hash), /size/);
    assert.throws(() => archive.unpackModel(bytes), /bzip|magic/);
});

function fixture(files = async () => ({ model: new ArrayBuffer(8), tokens: new ArrayBuffer(4) })) {
    const workers = [], states = [], marks = [], timers = new Map();
    let nextTimer = 0;
    const exports = load('NeuralSession', { './DeviceSession': device, './ModelStore': { modelFiles: files }, './NeuralWorker': {
        neuralWorker: () => {
            const worker = { messages: [], closed: false, postMessage(message) { this.messages.push(message); }, terminate() { this.closed = true; },
                emit(message) { this.onmessage?.({ data: message }); } };
            workers.push(worker); return worker;
        }
    } }, { window: { setTimeout: fn => { timers.set(++nextTimer, fn); return nextTimer; } }, clearTimeout: id => timers.delete(id) });
    const context = { state: 'running', sources: [],
        async resume() { this.state = 'running'; }, async suspend() { this.state = 'suspended'; }, async close() { this.state = 'closed'; },
        createGain: () => ({ gain: { value: 1 }, connect() {}, disconnect() {} }),
        createBuffer: () => ({ copyToChannel() {} }),
        createBufferSource() {
            const source = { started: false, stopped: false, connect() {}, disconnect() {}, start() { this.started = true; }, stop() { this.stopped = true; } };
            this.sources.push(source); return source;
        }
    };
    const session = new exports.NeuralSession(state => states.push(state), (...args) => marks.push(args));
    const start = (text = 'word '.repeat(80)) => session.start([{ text, range: {} }], { rate: 1.5, pitch: 2, volume: .7, language: 'de' }, { id: 'voice', voice: '0' }, context);
    return { session, workers, states, marks, timers, context, start };
}
const flush = () => new Promise(resolve => setImmediate(resolve));
const audio = id => ({ type: 'audio', id, samples: new Float32Array(32), sampleRate: 22050 });

test('Neural speech starts only explicitly, pulls bounded chunks, applies speed/volume and highlights a chunk only when played', async () => {
    const f = fixture(); assert.equal(f.workers.length, 0);
    f.start(); await flush();
    const worker = f.workers[0]; assert.equal(f.marks.length, 0);
    worker.emit({ type: 'ready' });
    const request = worker.messages.at(-1);
    assert.ok(request.text.length <= 240); assert.equal(request.speed, 1.5); assert.equal('pitch' in request, false);
    worker.emit(audio(request.id));
    assert.equal(f.session.state, 'speaking'); assert.equal(f.marks[0][1], 0); assert.equal(f.marks[0][2], request.text.length);
    f.context.sources[0].onended();
    assert.equal(worker.messages.at(-1).id, request.id + 1);
    f.session.stop(); assert.equal(worker.closed, true); assert.equal(f.context.state, 'closed'); assert.equal(f.timers.size, 0);
});

test('Neural pause resumes the same playing source and Stop fences every old audio/end callback', async () => {
    const f = fixture(); f.start(); await flush();
    const worker = f.workers[0]; worker.emit({ type: 'ready' }); worker.emit(audio(worker.messages.at(-1).id));
    const source = f.context.sources[0], ended = source.onended, message = worker.onmessage;
    f.session.pause(); await flush(); assert.equal(f.context.state, 'suspended');
    f.session.resume(); await flush(); assert.equal(f.context.sources.length, 1); assert.equal(f.session.state, 'speaking');
    f.session.stop(); ended(); message({ data: audio(1) });
    assert.equal(f.context.sources.length, 1); assert.equal(source.stopped, true); assert.equal(f.session.state, 'idle');
});

test('Pausing generation discards its worker and resumes the unheard chunk once; cache failures and timeouts fail closed', async () => {
    const f = fixture(); f.start('first chunk'); await flush();
    const first = f.workers[0]; first.emit({ type: 'ready' }); const old = first.onmessage;
    f.session.pause(); await flush(); assert.equal(first.closed, true);
    f.session.resume(); await flush();
    const second = f.workers[1]; old({ data: audio(1) }); assert.equal(f.context.sources.length, 0);
    second.emit({ type: 'ready' }); assert.equal(second.messages.at(-1).text, 'first chunk');
    [...f.timers.values()][0](); assert.equal(f.session.state, 'failed'); assert.equal(second.closed, true);
    const broken = fixture(async () => { throw new Error('integrity'); }); broken.start(); await flush();
    assert.equal(broken.session.state, 'failed'); assert.equal(broken.workers.length, 0); assert.equal(broken.context.state, 'closed');
});

test('Stopping while a verified model is still being read cannot create a late worker', async () => {
    let complete;
    const f = fixture(() => new Promise(resolve => { complete = resolve; }));
    f.start(); f.session.stop(); complete({ model: new ArrayBuffer(8), tokens: new ArrayBuffer(4) }); await flush();
    assert.equal(f.workers.length, 0); assert.equal(f.session.state, 'idle');
});
