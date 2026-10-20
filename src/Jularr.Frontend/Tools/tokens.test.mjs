import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import test from "node:test";
import assert from "node:assert/strict";

const root = resolve(import.meta.dirname, "..");
const tokens = JSON.parse(readFileSync(join(root, "Assets", "Shared", "tokens.json"), "utf8"));

const paletteKeys = [
    "background", "surface", "field", "border", "text", "muted",
    "accent", "accentContrast", "accentText", "accentHover", "danger", "success",
];

test("every skin carries the whole palette", () => {
    for (const [name, skin] of Object.entries(tokens.skins)) {
        for (const mode of ["light", "dark"]) {
            for (const key of paletteKeys) {
                assert.match(skin.palette[mode][key], /^#[0-9a-f]{6}$/i, `${name}.${mode}.${key}`);
            }
        }
    }
});

test("the status palette covers every availability state", () => {
    const states = [
        "available", "profileAudio", "otherAudio", "partial", "partialOtherAudio",
        "missing", "downloading", "searching", "requested",
    ];
    for (const state of states) {
        assert.ok(tokens.status[state], state);
        assert.match(tokens.status[state].light, /^#[0-9a-f]{6}$/i);
        assert.match(tokens.status[state].dark, /^#[0-9a-f]{6}$/i);
    }
});

test("the type scale names the sizes the clients mirror", () => {
    for (const key of [
        "body", "bodyLineHeight", "cardTitle", "cardTitleLineHeight", "cardMeta",
        "shelfHeading", "heroTitle", "pageTitle", "topbar", "railItem", "availabilitySymbol",
    ]) {
        assert.equal(typeof tokens.type[key], "number", key);
    }
    assert.equal(typeof tokens.radius.control, "number");
    assert.equal(typeof tokens.radius.panel, "number");
});
