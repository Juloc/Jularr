import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/Truncated.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;

function element(scrollWidth, clientWidth, linked = false) {
    const classes = new Set();
    const attributes = new Map();
    const text = { scrollWidth, clientWidth };
    return {
        classes, attributes,
        querySelector: selector => selector === '.truncated__text' ? text : linked ? {} : null,
        classList: { toggle: (name, on) => (on ? classes.add(name) : classes.delete(name)) },
        setAttribute: (name, value) => attributes.set(name, value),
        removeAttribute: name => attributes.delete(name)
    };
}

test('only cut-off texts show their full text and become focusable', () => {
    const exports = {};
    runInNewContext(script, { exports });
    const cut = element(300, 120);
    const fits = element(100, 120);
    const link = element(300, 120, true);
    fits.attributes.set('tabindex', '0');
    exports.markTruncated({ querySelectorAll: () => [cut, fits, link] });

    assert.ok(cut.classes.has('truncated--clipped'));
    assert.equal(cut.attributes.get('tabindex'), '0');
    assert.ok(!fits.classes.has('truncated--clipped'));
    assert.equal(fits.attributes.has('tabindex'), false);
    assert.ok(link.classes.has('truncated--clipped'));
    assert.equal(link.attributes.has('tabindex'), false);
});
