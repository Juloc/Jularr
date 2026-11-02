import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';
import { Packages, target, tarEntries } from './emulator-assets.mjs';

const source = readFileSync(new URL('../Modules/Game/Game.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
const module = { exports: {} };
runInNewContext(script, { exports: module.exports, module, btoa, atob, Uint8Array, String, Math });
const { emulatorLanguage, toBase64, fromBase64, sameBytes } = module.exports;

test('the emulator speaks the page language where it has one', () => {
    assert.equal(emulatorLanguage('de'), 'de-GER');
    assert.equal(emulatorLanguage('pt-BR'), 'pt-BR');
    assert.equal(emulatorLanguage('nl'), 'en-US');
});

test('saves travel as base64 unchanged', () => {
    const bytes = Uint8Array.from({ length: 70_000 }, (_, index) => index % 251);
    const copy = fromBase64(toBase64(bytes));
    assert.equal(sameBytes(copy, bytes), true);
    assert.equal(sameBytes(undefined, bytes), false);
    assert.equal(sameBytes(copy.subarray(1), bytes), false);
});

test('only the pinned browser files of EmulatorJS are kept', () => {
    assert.equal(Packages.length, 14);
    assert.ok(Packages.every(([, integrity]) => integrity.startsWith('sha512-')));
    assert.equal(target('emulatorjs', 'package/data/loader.js'), 'data/loader.js');
    assert.equal(target('emulatorjs', 'package/LICENSE'), 'LICENSE');
    assert.equal(target('emulatorjs', 'package/build.js'), null);
    assert.equal(target('core-snes9x', 'package/snes9x-thread-legacy-wasm.data'), 'data/cores/snes9x-thread-legacy-wasm.data');
    assert.equal(target('core-snes9x', 'package/reports/snes9x.json'), 'data/cores/reports/snes9x.json');
    assert.equal(target('core-snes9x', 'package/../evil-wasm.data'), null);
    assert.equal(target('emulatorjs', 'package/data/../../evil.js'), null);
    assert.equal(target('core-snes9x', 'other/snes9x-wasm.data'), null);
});

test('tar entries are read with their ustar prefix', () => {
    const header = Buffer.alloc(512);
    header.write('file.txt', 0);
    header.write('package/data', 345);
    header.write('00000000005\0', 124);
    header.write('0', 156);
    const archive = Buffer.concat([header, Buffer.from('hello'), Buffer.alloc(507), Buffer.alloc(1024)]);
    const entries = [...tarEntries(archive)];
    assert.equal(entries.length, 1);
    assert.equal(entries[0].name, 'package/data/file.txt');
    assert.equal(entries[0].bytes.toString(), 'hello');
});
