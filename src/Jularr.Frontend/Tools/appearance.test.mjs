import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { runInNewContext } from "node:vm";
import ts from "typescript";

const source = readFileSync(new URL("../Modules/Appearance/theme.entry.ts", import.meta.url), "utf8");
const compile = source => ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const script = compile(source);
const palette = {};
runInNewContext(compile(readFileSync(new URL("../Modules/Appearance/Palette.ts", import.meta.url), "utf8")), { exports: palette });

function appearance(saved = {}, unavailable = false) {
    class Element {
        dataset;
        attributes = {};
        value = "";
        constructor(dataset) { this.dataset = dataset; }
        closest() { return this; }
        setAttribute(name, value) { this.attributes[name] = value; }
    }

    const styles = new Map();
    const root = { dataset: {}, style: { setProperty: (name, value) => styles.set(name, value) } };
    const buttons = ["original", "clean"].map(setSkin => new Element({ setSkin }))
        .concat(["system", "light", "dark"].map(setMode => new Element({ setMode })));
    const events = {};
    const accentButtons = ["#c8102e", "#813eff"].map(setAccent => new Element({ setAccent }));
    const reset = new Element({ resetAccent: "true" });
    const picker = new Element({ accentInput: "true" });
    const value = {};
    const meta = new Element({});
    const storage = new Map(Object.entries(saved));
    const document = {
        documentElement: root,
        querySelectorAll: selector => selector.includes("data-accent-input") ? [picker] : selector.includes("data-accent-value") ? [value]
            : selector.includes("data-set-accent") ? accentButtons : buttons,
        querySelector: () => meta,
        addEventListener: (name, handler) => { events[name] = handler; }
    };
    const localStorage = {
        getItem: key => { if (unavailable) throw new Error("blocked"); return storage.get(key); },
        setItem: (key, value) => { if (unavailable) throw new Error("blocked"); storage.set(key, value); },
        removeItem: key => { if (unavailable) throw new Error("blocked"); storage.delete(key); }
    };
    const systemMode = { matches: false, addEventListener() {} };
    runInNewContext(script, { document, localStorage, Element, HTMLInputElement: Element, Set, exports: {}, require: () => palette, window: { matchMedia: () => systemMode } });
    return { root, buttons, storage, styles, accentButtons, reset, picker, value, meta,
        click: button => events.click({ target: button }), input: color => { picker.value = color; events.input({ target: picker }); } };
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

test("free accents update tokens and artwork without changing semantic colors", () => {
    const ui = appearance({ "jularr-ui-skin": "original" });
    assert.equal(ui.root.dataset.accent, "#c8102e");
    assert.equal(ui.styles.get("--art-hue-rotate"), "0.0deg");
    ui.input("#1b8aa0");
    assert.equal(ui.root.dataset.accent, "#1b8aa0");
    assert.equal(ui.storage.get("jularr-ui-accent"), "#1b8aa0");
    assert.equal(ui.picker.value, ui.value.textContent);
    assert.notEqual(ui.styles.get("--art-hue-rotate"), "0.0deg");
    assert.equal(ui.styles.has("--danger"), false);
    ui.click(ui.buttons[1]);
    assert.equal(ui.root.dataset.accent, "#1b8aa0");
    ui.click(ui.reset);
    assert.equal(ui.root.dataset.accent, "#813eff");
    assert.equal(ui.storage.has("jularr-ui-accent"), false);
    ui.input("#808080");
    assert.equal(ui.styles.get("--art-saturate"), "0");
    ui.input("url(bad)");
    assert.equal(ui.root.dataset.accent, "#808080");
});

test("arbitrary saturated and monochrome accents remain readable in both modes", () => {
    const luminance = hex => {
        const channels = (hex.length === 4 ? [...hex.slice(1)].map(c => c + c) : hex.slice(1).match(/../g))
            .map(c => parseInt(c, 16) / 255).map(c => c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4);
        return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
    };
    for (const seed of ["#c8102e", "#813eff", "#ff0000", "#00ff00", "#0000ff", "#ffff00", "#00ffff", "#d9577a", "#000000", "#ffffff", "#808080"]) {
        const result = palette.buildPalette(seed);
        for (const mode of [result.light, result.dark]) {
            for (const [foreground, background] of [["text", "surface"], ["muted", "surface-hover"], ["accent-contrast", "accent"], ["accent-contrast", "accent-hover"], ["accent-text", "accent-soft"]]) {
                const values = [luminance(mode[foreground]), luminance(mode[background])].sort((a, b) => b - a);
                assert.ok((values[0] + .05) / (values[1] + .05) >= 4.5, seed + " " + foreground + "/" + background);
            }
        }
    }
    assert.equal(palette.buildPalette("#c8102e").light.accent, "#c8102e");
    assert.throws(() => palette.buildPalette("not a color"));
});

test("the default accent is the mockups' reference palette", () => {
    const result = palette.buildPalette("#813EFF");
    assert.deepEqual([result.light.accent, result.dark.accent], ["#813eff", "#a980ff"]);
    assert.deepEqual([result.light.background, result.light.surface, result.light.border], ["#faf9ff", "#ffffff", "#ebe8f5"]);
    assert.deepEqual([result.dark.background, result.dark.surface, result.dark.border], ["#11111a", "#1c1b29", "#363247"]);
    for (const mode of [result.light, result.dark]) {
        for (const ground of ["background", "surface", "field", "surface-hover", "accent-soft"]) {
            assert.ok(palette.contrast(mode.muted, mode[ground]) >= 4.5, "muted on " + ground);
            assert.ok(palette.contrast(mode["accent-text"], mode[ground]) >= 4.5, "accent text on " + ground);
        }
    }
});