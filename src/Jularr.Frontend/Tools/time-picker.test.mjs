import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/TimePicker.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports });
const { timePlatform, parseTime, formatTime, cleanPart } = exports;

test('each device gets its picker: fields on a desktop, a wheel on Android, the system picker on iOS', () => {
    assert.equal(timePlatform('Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/141.0', 0), 'desktop');
    assert.equal(timePlatform('Mozilla/5.0 (Linux; Android 15; Pixel 9) Chrome/141.0 Mobile', 5), 'android');
    assert.equal(timePlatform('Mozilla/5.0 (iPhone; CPU iPhone OS 26_0 like Mac OS X) Safari/604.1', 5), 'ios');
    assert.equal(timePlatform('Mozilla/5.0 (Macintosh; Intel Mac OS X 15_0) Safari/605.1', 5), 'ios', 'an iPad asks for a desktop page');
    assert.equal(timePlatform('Mozilla/5.0 (Macintosh; Intel Mac OS X 15_0) Safari/605.1', 0), 'desktop');
});

test('times are read and written as the native field keeps them', () => {
    assert.deepEqual([...parseTime('02:05')], [2, 5]);
    assert.deepEqual([...parseTime('23:59:00')], [23, 59]);
    assert.equal(parseTime('24:00'), null);
    assert.equal(parseTime(''), null);
    assert.equal(formatTime(7, 3), '07:03');
});

test('typed parts keep two digits within their range', () => {
    assert.equal(cleanPart('1a', 23), '1');
    assert.equal(cleanPart('23', 23), '23');
    assert.equal(cleanPart('29', 23), '9');
    assert.equal(cleanPart('123', 59), '23');
    assert.equal(cleanPart('', 59), '');
});
