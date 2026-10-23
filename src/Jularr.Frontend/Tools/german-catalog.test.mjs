// Every UI source text needs a German entry; German is the owner's working language.
// Texts that are identical in both languages (names, units, technical terms) are listed in german-identical.json.
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const frontend = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const source = fs.readFileSync(path.join(frontend, "Localization", "UiResources.cs"), "utf8");
const english = [...source.matchAll(/new\("([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)",\s*"((?:\\.|[^"\\])*)"\)/g)]
  .map(match => [match[1], JSON.parse(`"${match[2]}"`)]);
const german = JSON.parse(fs.readFileSync(path.join(frontend, "Localization", "Catalogs", "de.json"), "utf8").replace(/^\uFEFF/, "")).texts;
const identical = JSON.parse(fs.readFileSync(path.join(import.meta.dirname, "german-identical.json"), "utf8"));

// Example values in input placeholders and texts without words are the same in every language.
const exempt = (key, text) => /^hint\..*\.placeholder$/.test(key) || !/[A-Za-z]{2}/.test(text.replace(/\{\d+\}/g, ""));

test("every UI text has a German translation", () => {
  const missing = english.filter(([key]) => !german[key]?.[1]?.trim()).map(([key]) => key);
  assert.deepEqual(missing, [], `German text missing for ${missing.length} keys, add them to Localization/Catalogs/de.json`);
});

test("German texts are not left in English", () => {
  const allowed = new Set(identical);
  const untranslated = english
    .filter(([key, text]) => german[key]?.[1] === text && !exempt(key, text) && !allowed.has(text))
    .map(([key]) => key);
  assert.deepEqual(untranslated, [], "Translate these keys or, for names and technical terms, list the text in Tools/german-identical.json");
});

test("German placeholders match the English source", () => {
  const placeholders = text => (text.match(/\{\d+\}/g) ?? []).sort().join();
  const broken = english.filter(([key, text]) => german[key]?.[1] && placeholders(text) !== placeholders(german[key][1])).map(([key]) => key);
  assert.deepEqual(broken, []);
});

test("the identical-text allowlist has no stale entries", () => {
  const used = new Set(english.filter(([key, text]) => german[key]?.[1] === text).map(([, text]) => text));
  assert.deepEqual(identical.filter(text => !used.has(text)), []);
  assert.equal(new Set(identical).size, identical.length);
});
