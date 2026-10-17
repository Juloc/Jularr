import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { test } from "node:test";
import ts from "typescript";

const code = readFileSync(new URL("../Modules/Player/PlayerSettings.ts", import.meta.url), "utf8");
const script = ts.transpileModule(code, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports });
const { normalizedSpeed, applyFit, selectSettingsPane } = exports;

test("speed is bounded, finite and rounded to the actual 0.05 step", () => {
    assert.equal(normalizedSpeed(0.1), 0.25);
    assert.equal(normalizedSpeed(5), 4);
    assert.equal(normalizedSpeed(1.247), 1.25);
    assert.equal(normalizedSpeed(1.27), 1.25);
    assert.equal(normalizedSpeed(Number.NaN), null);
    assert.equal(normalizedSpeed(Number.POSITIVE_INFINITY), null);
});

test("fit only changes presentation on the existing media element", () => {
    const video = { style: { objectFit: "contain" } };
    assert.equal(applyFit(video, "cover"), true);
    assert.equal(video.style.objectFit, "cover");
    assert.equal(applyFit(video, "javascript:alert(1)"), false);
    assert.equal(video.style.objectFit, "cover");
});

test("settings tabs share one panel, preserving selected options and hiding inactive views", () => {
    const panes = ["speed", "audio", "subtitles", "info"].map(name => ({ dataset: { settingsPane: name }, hidden: name !== "speed" }));
    const tabs = ["speed", "audio", "subtitles", "info"].map(name => ({ dataset: { settingsTab: name },
        setAttribute(key, value) { this[key] = value; } }));
    const dialog = { hidden: true,
        querySelectorAll(selector) { return selector === "[data-settings-pane]" ? panes : selector === "[data-settings-tab]" ? tabs : []; } };
    assert.equal(selectSettingsPane(dialog, "audio"), true);
    assert.equal(dialog.hidden, false);
    assert.deepEqual(panes.map(pane => pane.hidden), [true, false, true, true]);
    assert.deepEqual(tabs.map(tab => tab["aria-pressed"]), ["false", "true", "false", "false"]);
    assert.equal(selectSettingsPane(dialog, "invalid"), false);
    assert.deepEqual(panes.map(pane => pane.hidden), [true, false, true, true]);
});
