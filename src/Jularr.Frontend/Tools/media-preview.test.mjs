import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/MediaPreview.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;

function preview() {
    const events = {};
    const windowEvents = {};
    const dialogEvents = {};
    const frames = [];
    const content = { children: [], replaceChildren(...children) { this.children = children; } };
    class Element {
        constructor(kind, dataset = {}) { this.kind = kind; this.dataset = dataset; this.isConnected = true; }
        closest(selector) {
            return ({ '[data-preview-open]': 'trigger', '[data-preview-close]': 'close', '[data-trailer-id]': 'play', 'a[href]': 'link' })[selector] === this.kind ? this : null;
        }
        getAttribute() { return 'Play trailer'; }
        focus() { document.activeElement = this; }
    }
    class HTMLTemplateElement {
        id = 'preview-title';
        content = { cloneNode: () => ({ template: this.id }) };
    }
    const template = new HTMLTemplateElement();
    const trigger = new Element('trigger', { previewOpen: template.id });
    const close = new Element('close');
    const play = new Element('play', { trailerId: 'ayi6VfCKBcA' });
    play.parentElement = content;
    const dialog = Object.assign(new Element('dialog'), {
        open: false, attributes: {},
        querySelector: selector => selector === '[data-preview-content]' ? content : close,
        addEventListener: (name, action) => { dialogEvents[name] = action; },
        getBoundingClientRect: () => ({ left: 20, right: 820, top: 20, bottom: 900 }),
        contains: element => ['play', 'close', 'link'].includes(element.kind),
        setAttribute(name, value) { this.attributes[name] = value; },
        removeAttribute(name) { delete this.attributes[name]; },
        showModal() { this.open = true; close.focus(); },
        close() { this.open = false; dialogEvents.close(); }
    });
    const document = {
        activeElement: null, querySelector: () => dialog, getElementById: id => id === template.id ? template : null,
        addEventListener: (name, action) => { events[name] = action; },
        createElement: name => { assert.equal(name, 'iframe'); const frame = {}; frames.push(frame); return frame; }
    };
    runInNewContext(script, { exports: {}, document, Element, HTMLTemplateElement,
        window: { addEventListener: (name, action) => { windowEvents[name] = action; } } });
    function click(target, options = {}) {
        const event = { target, button: 0, preventDefault() { this.prevented = true; }, ...options };
        events.click(event);
        return event;
    }
    return { trigger, play, close, dialog, content, frames, document, template, click,
        navigate: () => windowEvents.pagehide(), outside: () => dialogEvents.click({ target: dialog, clientX: 0, clientY: 0 }) };
}

test('preview opens inert SSR content and restores native close focus', () => {
    const view = preview();
    assert.equal(view.click(view.trigger).prevented, true);
    assert.equal(view.dialog.open, true);
    assert.equal(view.dialog.attributes['aria-labelledby'], 'preview-title-title');
    assert.equal(view.frames.length, 0);
    view.dialog.close();
    assert.equal(view.content.children.length, 0);
    assert.equal(view.document.activeElement, view.trigger);
});

test('trailer creates one allowlisted embed on Play and unloads on close', () => {
    const view = preview();
    view.click(view.trigger);
    view.click(view.play);
    view.click(view.play);
    assert.equal(view.frames.length, 1);
    assert.equal(view.frames[0].src, 'https://www.youtube.com/embed/ayi6VfCKBcA?autoplay=1&playsinline=1');
    assert.equal(view.frames[0].referrerPolicy, 'strict-origin-when-cross-origin');
    assert.equal(view.dialog.dataset.trailerOpen, 'true');
    assert.equal(view.document.activeElement, view.close);
    view.click(view.close);
    assert.equal(view.content.children.length, 0);
    assert.equal(view.dialog.dataset.trailerOpen, undefined);
});

test('preview switching and page navigation remove the current trailer', () => {
    const view = preview();
    view.click(view.trigger);
    view.click(view.play);
    view.click(view.trigger);
    assert.equal(view.content.children[0].template, view.template.id);
    assert.equal(view.dialog.dataset.trailerOpen, undefined);
    view.click(view.play);
    view.navigate();
    assert.equal(view.content.children.length, 0);
    assert.equal(view.dialog.open, false);
});

test('modified or missing preview links stay native and invalid trailer IDs never load', () => {
    const view = preview();
    assert.equal(view.click(view.trigger, { ctrlKey: true }).prevented, undefined);
    view.trigger.dataset.previewOpen = 'missing';
    assert.equal(view.click(view.trigger).prevented, undefined);
    assert.equal(view.dialog.open, false);
    view.trigger.dataset.previewOpen = view.template.id;
    view.click(view.trigger);
    view.play.dataset.trailerId = '../unexpected';
    view.click(view.play);
    assert.equal(view.frames.length, 0);
    view.outside();
    assert.equal(view.dialog.open, false);
});
