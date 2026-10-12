import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { runInNewContext } from "node:vm";
import ts from "typescript";

const source = readFileSync(new URL("../Modules/Appearance/theme.entry.ts", import.meta.url), "utf8");
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;

function appearance(saved = {}, unavailable = false) {
    class Element {
        dataset;
        attributes = {};
        constructor(dataset) { this.dataset = dataset; }
        closest() { return this; }
        setAttribute(name, value) { this.attributes[name] = value; }
    }

    const root = { dataset: {} };
    const buttons = ["original", "clean"].map(setSkin => new Element({ setSkin }))
        .concat(["system", "light", "dark"].map(setMode => new Element({ setMode })));
    const events = {};
    const storage = new Map(Object.entries(saved));
    const document = {
        documentElement: root,
        querySelectorAll: () => buttons,
        addEventListener: (name, handler) => { events[name] = handler; }
    };
    const localStorage = {
        getItem: key => { if (unavailable) throw new Error("blocked"); return storage.get(key); },
        setItem: (key, value) => { if (unavailable) throw new Error("blocked"); storage.set(key, value); }
    };
    runInNewContext(script, { document, localStorage, Element, Set });
    return { root, buttons, storage, click: button => events.click({ target: button }) };
}

test("appearance restores valid preferences and exposes selected controls", () => {
    const ui = appearance({ "jularr-ui-skin": "original", "jularr-ui-mode": "dark" });
    assert.equal(ui.root.dataset.skin, "original");
    assert.equal(ui.root.dataset.mode, "dark");
    assert.equal(ui.buttons[0].attributes["aria-pressed"], "true");
    assert.equal(ui.buttons[4].attributes["aria-pressed"], "true");

    ui.click(ui.buttons[1]);
    assert.equal(ui.root.dataset.skin, "clean");
    assert.equal(ui.root.dataset.mode, "dark");
    assert.equal(ui.storage.get("jularr-ui-skin"), "clean");
    assert.equal(ui.buttons[0].attributes["aria-pressed"], "false");
    assert.equal(ui.buttons[1].attributes["aria-pressed"], "true");
});

test("invalid or blocked storage retains usable independent controls", () => {
    for (const blocked of [false, true]) {
        const ui = appearance({ "jularr-ui-skin": "unknown", "jularr-ui-mode": "unknown" }, blocked);
        assert.equal(ui.root.dataset.skin, "clean");
        assert.equal(ui.root.dataset.mode, "system");
        ui.click(ui.buttons[3]);
        assert.equal(ui.root.dataset.mode, "light");
        assert.equal(ui.root.dataset.skin, "clean");
        ui.click({ dataset: { setSkin: "unknown" }, closest() { return this; } });
        assert.equal(ui.root.dataset.skin, "clean");
    }
});

test("both skins preserve text and primary-button contrast in both modes", () => {
    const theme = readFileSync(new URL("../Modules/Appearance/Theme.scss", import.meta.url), "utf8");
    const palettes = theme.split("// Original ink and paper.").map(part => Object.fromEntries(
        [...part.matchAll(/--([a-z-]+): light-dark\((#[a-f0-9]+), (#[a-f0-9]+)\)/g)]
            .map(match => [match[1], [match[2], match[3]]])));
    const luminance = hex => {
        const channels = (hex.length === 4 ? [...hex.slice(1)].map(c => c + c) : hex.slice(1).match(/../g))
            .map(c => parseInt(c, 16) / 255).map(c => c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4);
        return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
    };
    for (const palette of palettes) {
        for (const mode of [0, 1]) {
            for (const [foreground, background] of [["text", "surface"], ["muted", "surface"], ["accent-contrast", "accent"]]) {
                const values = [luminance(palette[foreground][mode]), luminance(palette[background][mode])].sort((a, b) => b - a);
                assert.ok((values[0] + .05) / (values[1] + .05) >= 4.5, foreground + "/" + background);
            }
        }
    }
});
