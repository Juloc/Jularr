// Maintainer-only mechanical conversion of the reviewed, pinned build. Normal builds never download or compile it.
import { createHash } from 'node:crypto';
import { readFile, writeFile, mkdir, copyFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';

const [built, decoder] = process.argv.slice(2).map(path => resolve(path));
if (!built || !decoder) throw new Error('Usage: node Tools/prepare-speech-runtime.mjs <lean-runtime> <seek-bzip package>');
const output = resolve(import.meta.dirname, '../Modules/Speech/Runtime');
await mkdir(output, { recursive: true });
const hashes = {
    'sherpa-onnx-tts.js': 'b9cb4782010b22d64be31298a3e407170d2b8f5def471ea6011c42a921577e8f',
    'sherpa-onnx-wasm-main-tts.js': '2c3640151122d0de265b591bc9e596889e5b3b1e0b88da7cbcea76c70303f12f',
    'sherpa-onnx-wasm-main-tts.wasm': '667780ecc39e93c38b310fac8748e239fd280a1d067e39f6f8149c299f722347',
    'sherpa-onnx-wasm-main-tts.data': '45a9bbe5295d30f7fccb448be3144c4a7892208e2b0cfa36afa96b62101aab8c'
};
for (const [name, hash] of Object.entries(hashes)) {
    const bytes = await readFile(join(built, name));
    if (createHash('sha256').update(bytes).digest('hex') !== hash) throw new Error(`Unreviewed runtime: ${name}`);
    if (name.endsWith('.wasm') || name.endsWith('.data')) await copyFile(join(built, name), join(output, name));
    else if (name === 'sherpa-onnx-tts.js') {
        await writeFile(join(output, name), '// ESM export added by Jularr; engine algorithms unchanged.\n' + bytes.toString().replace('function createOfflineTts(', 'export function createOfflineTts('));
    } else {
        await writeFile(join(output, name), '// Jularr: scope the generated Module to one explicit loader; algorithms unchanged.\nexport default function load(Module) {\n' + bytes.toString() + '\nreturn Module;\n}\n');
    }
}
const metadata = JSON.parse(await readFile(join(decoder, 'package.json'), 'utf8'));
if (metadata.name !== 'seek-bzip' || metadata.version !== '2.0.0' || metadata.license !== 'MIT') throw new Error('Unreviewed decoder');
const target = join(output, 'seek-bzip');
const decoderHashes = {
    index: '71e9584b89c6468f5b65b78498e3d37793eff429051820a724bf3a09f87c605e',
    stream: '4ec2d27a74d7c16e797a1f70ea11bc73584b407baf8428d54fe6bba19040332d',
    bitreader: 'c2012c468b89ecf73e83a4454af52758594f59a4a256b21ae7ccba09347350db',
    crc32: 'c007037841eed8d292137dc69c740be6d8560234f118a4863bbaba359f104b1b'
};
await mkdir(target, { recursive: true });
for (const name of ['bitreader', 'stream', 'crc32', 'index']) {
    let source = await readFile(join(decoder, 'lib', name + '.js'), 'utf8');
    if (createHash('sha256').update(source).digest('hex') !== decoderHashes[name]) throw new Error(`Unreviewed decoder source: ${name}`);
    source = source.replaceAll('new Buffer(', 'new Uint8Array(')
        .replace("var BitReader = require('./bitreader');", "import BitReader from './bitreader.js';")
        .replace("var Stream = require('./stream');", "import Stream from './stream.js';")
        .replace("var CRC32 = require('./crc32');", "import CRC32 from './crc32.js';")
        .replace("var pjson = require('../package.json');", "var pjson = { version: '2.0.0', license: 'MIT' };")
        .replace("return buf.toString('hex');", "return Array.from(buf, value => value.toString(16).padStart(2, '0')).join('');")
        .replace('this.buffer.copy(newBuffer);', 'newBuffer.set(this.buffer);')
        .replace('this.buffer.copy(newBuffer, 0, 0, this.pos);', 'newBuffer.set(this.buffer.subarray(0, this.pos));')
        .replace('module.exports = ', 'export default ');
    await writeFile(join(target, name + '.js'), '// Jularr browser port: ESM and Uint8Array instead of Node Buffer; decoding and CRC checks unchanged.\n' + source);
}
await copyFile(join(decoder, 'LICENSE'), join(target, 'LICENSE'));
console.log('Pinned Speech assets prepared; no model weights copied.');
