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
    assert.equal(exports.languageOf('de-DE', ''), 'de');
    assert.equal(exports.languageOf('fr', ''), 'fr');
    assert.equal(exports.languageOf('in', ''), 'id');
    assert.equal(exports.languageOf('', 'ルイ・クリードの記憶では、ある瞬間がいつも魔法のような性質を持っていた。'), 'ja');
    assert.equal(exports.languageOf(undefined, 'Er sagte, dass die Tür nicht zu war und das Haus sich mit der Nacht füllte, und der Hund schlief.'), 'de');
    assert.equal(exports.languageOf(undefined, 'Dia bilang itu tidak dari rumah yang di sana dan dengan anjing yang tidur untuk malam ini.'), 'id');
    assert.equal(exports.languageOf(undefined, 'He said that the door was open and the house was full of the night, and it was quiet.'), 'en');
    assert.equal(exports.languageOf(undefined, "Il dit que la porte n'est pas fermée et que la maison est dans le noir, une nuit pour les chiens."), 'fr');
    assert.equal(exports.languageOf(undefined, '12 34'), undefined);
});

test('English is a step of its own; other pairs go through it', () => {
    const steps = (source, target) => JSON.stringify(exports.steps(source, target));
    assert.equal(steps('en', 'ja'), '[["en","ja"]]');
    assert.equal(steps('id', 'en'), '[["id","en"]]');
    assert.equal(steps('de', 'ja'), '[["de","en"],["en","ja"]]');
});


test('translation restore signals a reader reflow without changing the canonical blocks', async () => {
    const block = { innerHTML: '<p>Original</p>' };
    const reader = { dataset: { language: 'en' }, querySelector: () => null };
    const content = { innerText: 'Original', querySelectorAll: () => [block] };
    let reflows = 0;
    const choose = exports.translator(reader, content, () => { throw new Error('No worker expected'); }, () => reflows++);
    await choose('');
    assert.equal(block.innerHTML, '<p>Original</p>');
    assert.equal(reflows, 1);
});
