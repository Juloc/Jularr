import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { runInNewContext } from "node:vm";
import ts from "typescript";

const source = readFileSync(new URL("../Framework/Components/LocalTime.ts", import.meta.url), "utf8");
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports, Intl, Date, Number });
const { formatLocalTime } = exports;

test("the same UTC instant is displayed in the viewer's zone", () => {
    const utc = "2026-10-23T18:26:00.000Z";
    assert.match(formatLocalTime(utc, "datetime", "de-DE", "Europe/Berlin"), /20:26/);
    assert.match(formatLocalTime(utc, "datetime", "de-DE", "UTC"), /18:26/);
    assert.match(formatLocalTime(utc, "datetime", "en-GB", "America\/New_York"), /14:26/);
});

test("summer and winter offsets come from the timestamp, not a fixed correction", () => {
    assert.match(formatLocalTime("2026-07-01T12:00:00.000Z", "time", "de-DE", "Europe/Berlin"), /14:00/);
    assert.match(formatLocalTime("2026-12-01T12:00:00.000Z", "time", "de-DE", "Europe/Berlin"), /13:00/);
});

test("local calendar date can change around midnight and invalid timestamps retain their fallback", () => {
    assert.match(formatLocalTime("2026-10-23T23:30:00.000Z", "date", "de-DE", "Europe/Berlin"), /24\.10/);
    assert.equal(formatLocalTime("not-a-date", "datetime", "de-DE", "Europe/Berlin"), null);
});
