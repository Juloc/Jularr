import assert from "node:assert/strict";
import { readFileSync, existsSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { test } from "node:test";

const root = resolve(import.meta.dirname, "..");
const build = join(root, "wwwroot", "build");
const manifest = JSON.parse(readFileSync(join(build, ".vite", "manifest.json"), "utf8"));
const icons = JSON.parse(readFileSync(join(build, ".vite", "icons.json"), "utf8"));
const files = Object.values(manifest).flatMap(entry => [entry.file, ...(entry.css ?? []), ...(entry.assets ?? [])].filter(Boolean));

test("all published assets exist with content fingerprints", () => {
    for (const file of [...new Set([...files, icons.file])]) {
        assert.match(file, /^assets\/[A-Za-z0-9_./-]+-[A-Za-z0-9_-]{8,}\.(?:css|js|svg|png|jpe?g|webp|avif)$/i, file);
        assert.ok(existsSync(join(build, file)), "missing compiled " + file);
    }
});

test("shared asset and page/module entries have separate manifests", () => {
    for (const key of [
        "Assets/Shared/shell.entry.scss",
        "Modules/Appearance/theme.entry.ts",
        "Pages/Index.entry.scss",
        "Pages/Settings.entry.scss",
        "Modules/Player/player.entry.scss"
    ]) assert.ok(manifest[key], "missing entry " + key);
    assert.notEqual(manifest["Pages/Index.entry.scss"].file, manifest["Assets/Shared/shell.entry.scss"].file);
});

test("SVG sprite contains every compiled icon and no unhashed sprite", () => {
    for (const key of ["home", "library", "bookmark", "calendar", "settings", "user", "search", "bell"]) {
        assert.equal(icons.symbols[key], "icon-" + key);
    }
    assert.ok(!readdirSync(join(build, "assets")).includes("icons.svg"));
});

test("manifest URLs are relative and cannot escape the asset root", () => {
    for (const file of [...files, icons.file]) {
        assert.ok(!file.startsWith("/") && !file.includes("..") && !file.includes("?"), file);
    }
});
