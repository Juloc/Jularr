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
    const accentButtons = ["#c8102e", "#8040f7"].map(setAccent => new Element({ setAccent }));
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
    return { Element, root, buttons, storage, styles, accentButtons, reset, picker, value, meta,
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

test("the header switch toggles the brightness in effect and keeps the style", () => {
    const ui = appearance({ "jularr-ui-skin": "original" });
    const toggle = new ui.Element({ toggleMode: "true" });
    assert.equal(ui.root.dataset.mode, "system");
    ui.click(toggle);
    assert.equal(ui.root.dataset.mode, "dark");
    assert.equal(ui.storage.get("jularr-ui-mode"), "dark");
    assert.equal(ui.buttons[4].attributes["aria-pressed"], "true");
    ui.click(toggle);
    assert.equal(ui.root.dataset.mode, "light");
    assert.equal(ui.root.dataset.skin, "original");
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
    assert.equal(ui.root.dataset.accent, "#8040f7");
    assert.equal(ui.storage.has("jularr-ui-accent"), false);
    ui.input("#808080");
    assert.equal(ui.styles.get("--art-saturate"), "0");
    ui.input("url(bad)");
    assert.equal(ui.root.dataset.accent, "#808080");
});

const hueOf = hex => {
    const [r, g, b] = hex.slice(1).match(/../g).map(c => parseInt(c, 16) / 255);
    const max = Math.max(r, g, b), delta = max - Math.min(r, g, b);
    if (delta === 0) return 0;
    const value = max === r ? (g - b) / delta : max === g ? (b - r) / delta + 2 : (r - g) / delta + 4;
    return (value * 60 + 360) % 360;
};
const hueDistance = (a, b) => Math.abs(((a - b) % 360 + 540) % 360 - 180);

test("the default accent is the owner's mockup palette, verbatim", () => {
    const result = palette.buildPalette("#8040F7");
    assert.deepEqual({ ...result.light }, {
        background: "#faf9ff", surface: "#ffffff", field: "#f8f7ff", "surface-hover": "#f8f7ff", "accent-soft": "#f2edff",
        accent: "#8040f7", "accent-outline": "#b59aff", focus: "#b59aff", "accent-text": "#7136d9", "accent-edge": "#dcd0ff",
        text: "#26223d", muted: "#938fa9", border: "#eae6f4", "border-strong": "#e1dcee", "accent-contrast": "#ffffff"
    });
    assert.deepEqual({ ...result.dark }, {
        background: "#12121d", surface: "#1d1b2b", field: "#262339", "surface-hover": "#262339", "accent-soft": "#35294e",
        accent: "#9a70ff", "accent-outline": "#8060bf", focus: "#8060bf", "accent-text": "#c3a8ff", "accent-edge": "#dcd0ff",
        text: "#f4f1ff", muted: "#aba5bf", border: "#38334e", "border-strong": "#4b4260", "accent-contrast": "#ffffff"
    });
});

test("another accent gets the same palette turned to its hue: tinted neutrals, the seed as accent, no tint for monochrome", () => {
    const teal = palette.buildPalette("#1b8aa0");
    const seedHue = hueOf("#1b8aa0");
    assert.equal(teal.light.accent, "#1b8aa0");
    assert.equal(teal.light["accent-contrast"], "#ffffff");
    assert.notEqual(teal.light.background, palette.buildPalette("#8040f7").light.background);
    for (const mode of [teal.light, teal.dark]) {
        for (const name of ["background", "field", "accent-soft", "border", "border-strong", "text", "muted", "accent-text", "accent-outline"]) {
            assert.ok(hueDistance(hueOf(mode[name]), seedHue) < 30, name + " follows the seed hue: " + mode[name]);
        }
        assert.equal(mode["surface-hover"], mode.field);
    }
    assert.equal(teal.light.surface, "#ffffff");
    // The neutrals keep the mockup's order of lightness: page and field lighter than the line, the line lighter than its stronger sibling.
    const lightness = hex => hex.slice(1).match(/../g).reduce((sum, c) => sum + parseInt(c, 16), 0);
    assert.ok(lightness(teal.light.border) > lightness(teal.light["border-strong"]));
    assert.ok(lightness(teal.dark.border) < lightness(teal.dark["border-strong"]));
    assert.ok(lightness(teal.dark.accent) > lightness(teal.light.accent));

    for (const grey of ["#808080", "#000000", "#ffffff"]) {
        const result = palette.buildPalette(grey);
        for (const mode of [result.light, result.dark]) {
            for (const name of ["background", "field", "accent-soft", "border", "text", "muted"]) {
                const [r, g, b] = mode[name].slice(1).match(/../g);
                assert.ok(r === g && g === b, grey + " " + name + " has no tint: " + mode[name]);
            }
        }
    }
    assert.equal(palette.buildPalette("#c8102e").light.accent, "#c8102e");
    assert.equal(palette.buildPalette("#ffff00").light["accent-contrast"] !== "#ffffff", true);
    assert.throws(() => palette.buildPalette("not a color"));
});
