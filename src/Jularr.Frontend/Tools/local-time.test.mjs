import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { runInNewContext } from "node:vm";
import ts from './transpile.mjs';

const source = readFileSync(new URL("../Framework/Components/LocalTime.ts", import.meta.url), "utf8");
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports, Intl, Date, Number });
const { formatLocalTime, timeZoneCookieHeader, releaseCountdown, localTodayIso } = exports;

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

test("IANA timezone cookie persists the device zone and is stable across reloads", () => {
    const first = timeZoneCookieHeader("", "Europe/Berlin", true);
    assert.equal(first, "jularr_time_zone=Europe/Berlin; Path=/; Max-Age=31536000; SameSite=Lax; Secure");
    assert.equal(timeZoneCookieHeader("other=1; jularr_time_zone=Europe/Berlin", "Europe/Berlin", true), null);
    assert.ok(timeZoneCookieHeader("jularr_time_zone=UTC", "America/New_York", false)?.includes("America/New_York"));
    assert.equal(timeZoneCookieHeader("", "untrusted;cookie=injected", false), null);
    assert.equal(timeZoneCookieHeader("", undefined, false), null);
});

test("release countdown is based on civil days and changes only at the local midnight", () => {
    assert.equal(releaseCountdown("2026-10-25", "2026-10-24", "Airs in {0} days", "Unaired"), "Airs in 1 days");
    assert.equal(releaseCountdown("2026-10-25", "2026-10-23", "Airs in {0} days", "Unaired"), "Airs in 2 days");
    assert.equal(releaseCountdown("2026-11-07", "2026-10-24", "Airs in {0} days", "Unaired"), "Airs in 14 days");
    assert.equal(releaseCountdown("2026-11-08", "2026-10-24", "Airs in {0} days", "Unaired"), "Unaired");
    assert.equal(releaseCountdown("2026-10-24", "2026-10-24", "Airs in {0} days", "Unaired"), "Unaired");
    assert.equal(releaseCountdown("2026-10-23", "2026-10-24", "Airs in {0} days", "Unaired"), "Unaired");
});

test("device-local calendar day is not a formatted UTC instant", () => {
    const date = new Date(2026, 9, 24, 0, 30);
    assert.equal(localTodayIso(date), "2026-10-24");
});

test("DST autumn fold retains the correct date while two instants share the same wall-clock hour", () => {
    const early = formatLocalTime("2026-10-25T00:30:00.000Z", "datetime", "de-DE", "Europe/Berlin");
    const late = formatLocalTime("2026-10-25T01:30:00.000Z", "datetime", "de-DE", "Europe/Berlin");
    assert.equal(early, late);
    assert.ok(early?.includes("25.10.26"));
    assert.ok(early?.includes("02:30"));
});
