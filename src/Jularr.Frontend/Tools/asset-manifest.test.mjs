import assert from "node:assert/strict";
import { readFileSync, existsSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { test } from "node:test";

const root = resolve(import.meta.dirname, "..");
const build = join(root, "wwwroot", "build");
const manifest = JSON.parse(readFileSync(join(root, "Assets", "Generated", "manifest.json"), "utf8"));
const icons = JSON.parse(readFileSync(join(root, "Assets", "Generated", "icons.json"), "utf8"));
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
        "Modules/Appearance/appearance.entry.scss",
        "Pages/User/Discover/Index.entry.scss",
        "Pages/User/Settings/Settings.entry.scss",
        "Pages/Account/Login/Login.entry.scss",
        "Pages/Development/Design/Design.entry.scss",
        "Modules/Player/player.entry.ts"
    ]) assert.ok(manifest[key], "missing entry " + key);
    assert.notEqual(manifest["Pages/User/Discover/Index.entry.scss"].file, manifest["Assets/Shared/shell.entry.scss"].file);
});

test("design references and Original artwork have fingerprinted image entries", () => {
    for (const image of ["Modules/Appearance/Original/hero-torii.webp", "Modules/Appearance/Original/heroine.webp",
        "Pages/Development/Design/References/appearance-approved.png", "Pages/Development/Design/References/library-approved.png",
        "Pages/Development/Design/Images/your-name-poster.jpg", "Pages/Development/Design/Images/suzume-poster.jpg",
        "Pages/Development/Design/Images/your-name-backdrop.jpg"]) {
        assert.ok(manifest[image]?.file, image);
    }
});

test("SVG sprite contains every compiled icon and no unhashed sprite", () => {
    for (const key of ["home", "library", "bookmark", "calendar", "settings", "user", "search", "bell", "brand-clean", "brand-original",
        "books", "reading", "folder", "subtitles", "providers", "theme-system", "player-previous", "player-next"]) {
        assert.equal(icons.symbols[key], "icon-" + key);
    }
    assert.ok(!readdirSync(join(build, "assets")).includes("icons.svg"));
    assert.ok(!existsSync(join(build, ".vite")), "build metadata must be private");
    const sprite = readFileSync(join(build, icons.file), "utf8");
    for (const id of Object.values(icons.symbols)) {
        assert.ok(sprite.includes('id="' + id + '"'), "missing sprite symbol " + id);
    }
});

test("manifest URLs are relative and cannot escape the asset root", () => {
    for (const file of [...files, icons.file]) {
        assert.ok(!file.startsWith("/") && !file.includes("..") && !file.includes("?"), file);
    }
});
