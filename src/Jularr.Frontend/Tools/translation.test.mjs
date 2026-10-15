import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Modules/Reader/Translation/translation.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports });

test('a book names its language, else its text tells it', () => {
    assert.equal(exports.languageOf('de-DE', ''), 'German');
    assert.equal(exports.languageOf('in', ''), 'Indonesian');
    assert.equal(exports.languageOf('', 'ルイ・クリードの記憶では、ある瞬間がいつも魔法のような性質を持っていた。'), 'Japanese');
    assert.equal(exports.languageOf(undefined, 'Er sagte, dass die Tür nicht zu war und das Haus sich mit der Nacht füllte, und der Hund schlief.'), 'German');
    assert.equal(exports.languageOf(undefined, 'Dia bilang itu tidak dari rumah yang di sana dan dengan anjing yang tidur untuk malam ini.'), 'Indonesian');
    assert.equal(exports.languageOf(undefined, 'He said that the door was open and the house was full of the night, and it was quiet.'), 'English');
    assert.equal(exports.languageOf(undefined, '12 34'), undefined);
});

test('English is a step of its own; other pairs go through it', () => {
    const steps = (source, target) => JSON.stringify(exports.steps(source, target));
    assert.equal(steps('English', 'Japanese'), '[["en","ja"]]');
    assert.equal(steps('Indonesian', 'English'), '[["id","en"]]');
    assert.equal(steps('German', 'Japanese'), '[["de","en"],["en","ja"]]');
});
