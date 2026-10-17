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
        "Pages/User/Discover/Index.entry.ts",
        "Pages/Account/Login/Login.entry.scss",
        "Pages/Development/Design/Design.entry.scss",
        "Framework/Components/MediaPreview.entry.ts",
        "Modules/Player/player.entry.ts"
    ]) assert.ok(manifest[key], "missing entry " + key);
    assert.notEqual(manifest["Pages/User/Discover/Index.entry.scss"].file, manifest["Assets/Shared/shell.entry.scss"].file);
});

test("a page's CSS and TypeScript entries both survive discovery and publish", () => {
    const styles = manifest["Pages/User/Discover/Index.entry.scss"];
    const script = manifest["Pages/User/Discover/Index.entry.ts"];
    assert.match(styles.file, /\.css$/);
    assert.match(script.file, /\.js$/);
    assert.notEqual(styles.file, script.file);
    const css = readFileSync(join(build, styles.file), "utf8");
    assert.ok(css.includes(".media-grid") && css.includes(".discover-paging"));
});

test("design media and Original artwork have fingerprinted image entries", () => {
    for (const image of ["Modules/Appearance/Original/hero-torii.webp", "Modules/Appearance/Original/heroine.webp",
        "Modules/Appearance/Original/washi.webp", "Modules/Appearance/Original/blossom-branch.webp", "Modules/Appearance/Original/hero-fuji.webp",
        "Pages/Development/Design/Images/your-name-poster.jpg", "Pages/Development/Design/Images/suzume-poster.jpg",
        "Pages/Development/Design/Images/your-name-backdrop.jpg"]) {
        assert.ok(manifest[image]?.file, image);
    }
});

test("obsolete design references are not published as current assets", () => {
    for (const key of Object.keys(manifest)) assert.ok(!key.includes('/Design/References/'), key);
});

test("CSS image URLs use the host build root and resolve to published assets", () => {
    let images = 0;
    for (const file of new Set(files.filter(file => file.endsWith(".css")))) {
        const css = readFileSync(join(build, file), "utf8");
        for (const match of css.matchAll(/url\(["']?([^\s)"']+)["']?\)/g)) {
            const url = match[1];
            if (url.startsWith("data:")) continue;
            assert.ok(url.startsWith("/build/assets/"), file + ": " + url);
            assert.ok(existsSync(join(build, url.slice("/build/".length))), url);
            images++;
        }
    }
    assert.ok(images >= 5, "Original decorations must be emitted and referenced");
});

test("SVG sprite contains every compiled icon and no unhashed sprite", () => {
    for (const key of ["home", "library", "bookmark", "calendar", "settings", "user", "search", "bell", "brand-clean", "brand-original",
        "books", "reading", "folder", "subtitles", "providers", "theme-system", "player-previous", "player-next", "close"]) {
        assert.equal(icons.symbols[key], "icon-" + key);
    }
    assert.ok(!readdirSync(join(build, "assets")).includes("icons.svg"));
    assert.ok(!existsSync(join(build, ".vite")), "build metadata must be private");
    const sprite = readFileSync(join(build, icons.file), "utf8");
    for (const id of Object.values(icons.symbols)) {
        assert.ok(sprite.includes('id="' + id + '"'), "missing sprite symbol " + id);
    }
});

test("media preview bounds its hero width and uses full-width mobile context", () => {
    const css = readFileSync(join(build, manifest["Pages/Development/Design/Design.entry.scss"].file), "utf8");
    assert.match(css, /\.media-preview__hero\{[^}]*width:100%/);
    assert.match(css, /\.media-preview__body\{display:contents\}/);
    assert.ok(css.includes('max-width:none'));
});

test("country flags keep their colors and rectangular size in the shared sprite", () => {
    assert.equal(icons.symbols["flag-de"], "icon-flag-de");
    const sprite = readFileSync(join(build, icons.file), "utf8");
    const flag = sprite.match(/<symbol\b[^>]*id="icon-flag-de"[\s\S]*?<\/symbol>/)?.[0];
    assert.ok(flag, "missing German flag");
    assert.match(flag, /viewBox="0 0 30 18"/);
    // Black is the SVG default.
    for (const color of ["#d00", "#ffce00"]) assert.ok(flag.includes(color), color);
    assert.doesNotMatch(flag, /currentColor/);

    const css = readFileSync(join(build, manifest["Pages/Development/Design/Design.entry.scss"].file), "utf8");
    assert.match(css, /\.ui-flag\{[^}]*width:20px;height:12px;[^}]*border-radius:2px/);
});

test("shared base styles cover forms, tables and capsule navigation on every page", () => {
    const css = readFileSync(join(build, manifest["Assets/Shared/shell.entry.scss"].file), "utf8");
    for (const selector of [".ui-form", ".ui-field__control>legend", ".page-table", ".navigation-tabs__link--active", ".toggle", ".tag"]) {
        assert.ok(css.includes(selector), selector);
    }
    assert.doesNotMatch(css, /backdrop-filter|--glass|box-shadow:inset/);
    assert.match(css, /\.page-screen\{[^}]*grid-template-columns:minmax\(0,1fr\)/);
    assert.match(css, /\.app-main \[id\]\{scroll-margin-top:var\(--shell-header-offset,\s*80px\)/);
    assert.match(css, /\.split\{grid-template-columns:minmax\(0,1fr\)/);
    assert.ok(!manifest["Pages/User/Settings/Settings.entry.scss"], "base-only pages need no duplicate bundle");
});

test("shared navigation stays single-row and the mobile bar floats above its safe area", () => {
    const css = readFileSync(join(build, manifest["Assets/Shared/shell.entry.scss"].file), "utf8");
    assert.match(css, /\.navigation-tabs\{[^}]*overflow-x:auto/);
    assert.match(css, /\.navigation-tabs\{[^}]*scrollbar-width:none/);
    assert.match(css, /\.navigation-tabs::-webkit-scrollbar\{display:none/);
    assert.doesNotMatch(css, /\.navigation-tabs\{[^}]*flex-wrap:wrap/);
    assert.match(css, /\.navigation-tabs__link--active[^{}]*\{background:var\(--accent-soft\);color:var\(--accent-text\)/);
    assert.match(css, /\.mobile-navigation\{[^}]*bottom:calc\(12px \+ env\(safe-area-inset-bottom\)\)[^}]*border-radius:var\(--radius-control\)/);
    assert.match(css, /\.mobile-link\{[^}]*min-height:54px/);
    assert.match(css, /body\{min-width:0/);
});

test("appearance choices wrap at their natural width and search retains an accessible target", () => {
    const appearance = readFileSync(join(build, manifest["Modules/Appearance/appearance.entry.scss"].file), "utf8");
    assert.match(appearance, /\.appearance__choices>\.button\{[^}]*flex:1 0 auto;[^}]*max-width:100%;overflow-wrap:anywhere/);
    assert.match(appearance, /\.appearance__color input\{[^}]*height:44px/);

    const shell = readFileSync(join(build, manifest["Assets/Shared/shell.entry.scss"].file), "utf8");
    assert.match(shell, /\.search-box__input\{[^}]*min-width:0;min-height:44px/);
    assert.match(shell, /\.search-box__input:focus-visible\{outline:none;border-color:var\(--accent\)/);
});

test("media shelf snap alignment includes its focus padding without visible scrollbars", () => {
    const css = readFileSync(join(build, manifest["Pages/Development/Design/Design.entry.scss"].file), "utf8");
    assert.match(css, /\.media-shelf__items\{[^}]*padding:4px 2px 8px;[^}]*scroll-padding-inline:2px;scrollbar-width:none/);
    assert.match(css, /\.media-shelf__items::-webkit-scrollbar\{display:none\}/);
});

test("icon hints remain painted on closed summaries and language hints open above the shelf edge", () => {
    const css = readFileSync(join(build, manifest["Pages/Development/Design/Design.entry.scss"].file), "utf8");
    assert.match(css, /content:attr\(aria-label\)/);
    assert.match(css, /\.icon-info\[open\]>.icon-info__trigger:{1,2}after\{display:none\}/);
    assert.match(css, /\.media-card__languages \.icon-info \.icon-info__trigger:{1,2}after\{top:auto;bottom:100%\}/);
});

test("media badges share quiet surfaces without painting their larger touch targets", () => {
    const css = readFileSync(join(build, manifest["Pages/Development/Design/Design.entry.scss"].file), "utf8");
    assert.match(css, /\.media-availability-badge\{[^}]*min-width:44px;height:44px;padding:10px/);
    assert.match(css, /\.media-availability\{position:relative;display:block;width:32px;height:32px;padding:2px;border-radius:50%;background:var\(--surface-hover\);color:var\(--status-color\)\}/);
    assert.match(css, /\.media-availability>svg\{display:block;width:28px;height:28px\}/);
    assert.match(css, /\.media-availability--compact\{width:16px;height:16px;padding:0;background:none\}/);
    assert.match(css, /\.media-availability--compact>\.ui-icon\{width:9px;height:9px\}/);
    assert.match(css, /\.media-availability>\.ui-icon\{position:absolute;top:50%;left:50%;width:14px;height:14px;transform:translate\(-50%,-50%\)\}/);
    assert.match(css, /\.icon-info__trigger>\.ui-icon\{width:18px;height:18px;flex:0 0 auto\}/);
    assert.ok(!css.includes(".icon-info__trigger .ui-icon{"), "Hints must not resize another control's nested icon.");
    assert.match(css, /\.media-card__language>\.ui-icon\{width:18px;height:18px;flex:0 0 auto\}/);
    assert.match(css, /\.icon-info__trigger\{[^}]*min-width:44px;min-height:44px/);

    const shell = readFileSync(join(build, manifest["Assets/Shared/shell.entry.scss"].file), "utf8");
    assert.match(shell, /\.tag--neutral\{border-color:transparent;background:var\(--surface-hover\);color:var\(--text\)\}/);
});

test("shared cards replace artwork with a floating landscape trailer and retain panel actions", () => {
    const css = readFileSync(join(build, manifest["Pages/Development/Design/Design.entry.scss"].file), "utf8");
    assert.match(css, /\.media-availability-badge__body\{[^}]*box-shadow:0 0 0 2px var\(--surface\),0 0 0 3px color-mix/);
    assert.match(css, /\.media-card__surface\[data-expanded\]\{position:fixed;z-index:20;inset:auto;top:var\(--preview-top\);left:var\(--preview-left\);width:var\(--preview-width\);height:auto\}/);
    assert.match(css, /\.media-card__surface\[data-expanded\] \.media-card__art\{display:none\}/);
    assert.match(css, /\.media-card__surface\[data-expanded\] \.media-card__trailer\{position:relative;inset:auto\}/);
    assert.match(css, /\.media-card__surface\[data-expanded\] \.media-card__content\{position:relative;inset:auto;border-radius:0 0 24px 24px\}/);
    assert.match(css, /\.media-card__actions\{display:flex;[^}]*gap:6px/);
    assert.doesNotMatch(css, /\.design-cover__/);
});

test("manifest URLs are relative and cannot escape the asset root", () => {
    for (const file of [...files, icons.file]) {
        assert.ok(!file.startsWith("/") && !file.includes("..") && !file.includes("?"), file);
    }
});

test("review variants inherit the shared progress and trailer layout", () => {
    const studies = readFileSync(join(root, "Pages/Development/Design/CardExperiments.scss"), "utf8");
    assert.doesNotMatch(studies, /media-card__progress|data-expanded|media-card__languages|media-card__genres|media-card__series/);
    for (const variant of ["inset", "editorial", "cinema", "rail", "frame", "centered"]) assert.ok(studies.includes(`&--${variant}`));
});

test("minimal cards clip full-width progress and retain the trailer height", () => {
    const css = readFileSync(join(build, manifest["Pages/Development/Design/Design.entry.scss"].file), "utf8");
    assert.ok(css.includes(".media-card--minimal .media-card__surface{overflow:hidden}"));
    assert.match(css, /\.media-card--minimal \.media-card__progress\{inset:auto 0 0;width:100%;height:12px;border-radius:0;background:#ffffffa6\}/);
    assert.ok(css.includes(".media-card--minimal .media-card__actions{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));align-items:stretch;margin:0}"));
    assert.ok(css.includes(".media-card--minimal .media-card__progress::-webkit-progress-value{border-radius:999px}"));
    assert.ok(css.includes(".media-card--minimal .media-card__progress::-moz-progress-bar{border-radius:999px}"));
    assert.ok(css.includes(".media-card--minimal .media-card__surface[data-expanded]{height:var(--preview-height);display:grid;grid-template-rows:minmax(0,1fr) auto}"));
    assert.ok(css.includes(".media-card--minimal .media-card__surface[data-expanded] .media-card__trailer{height:100%;aspect-ratio:auto}"));
});

test("availability dots and symbols share vivid status colors", () => {
    const css = readFileSync(join(build, manifest["Pages/Development/Design/Design.entry.scss"].file), "utf8");
    assert.match(css, /--status-color:\s*light-dark\(#16a34a,\s*#22c55e\)/);
    assert.match(css, /--status-color:\s*light-dark\(#e66a0a,\s*#fb923c\)/);
    assert.match(css, /--status-color:\s*light-dark\(#ac8000,\s*#facc15\)/);
    assert.match(css, /--status-dot:\s*linear-gradient\(135deg, var\(--status-alternate\) 50%, var\(--status-color\) 50%\)/);
    assert.ok(css.includes(".media-availability--partialotheraudio .media-availability__track{color:var(--status-alternate)}"));
    assert.match(css, /--status-color:\s*light-dark\(#1672e8,\s*#3b82f6\)/);
    assert.match(css, /--status-color:\s*light-dark\(#7c3aed,\s*#a855f7\)/);
});

test("normal card hover stays minimal and icon actions retain touch sizing", () => {
    const css = readFileSync(join(build, manifest["Pages/Development/Design/Design.entry.scss"].file), "utf8");
    assert.match(css, /\.media-card__overview\{display:none/);
    assert.match(css, /\.media-card__original-title\{display:none/);
    assert.match(css, /\.media-card__genres\{display:none\}/);
    assert.match(css, /\.media-card__surface\[data-expanded\] \.media-card__overview\{display:block\}/);
    const shell = readFileSync(join(build, manifest["Assets/Shared/shell.entry.scss"].file), "utf8");
    assert.match(shell, /\.button--icon\{width:36px;min-height:36px;padding:0;border-radius:50%/);
    assert.match(shell, /\.button--compact\{min-height:32px;padding:5px 10px;font-size:12px\}/);
    assert.match(shell, /@media\(pointer:coarse\)\{\.button--compact\{min-height:44px\}\.button--icon\{width:44px;min-height:44px\}/);
    assert.match(shell, /\.button:disabled\{opacity:1;background:var\(--surface-hover\);border-color:var\(--border\);color:var\(--muted\);cursor:not-allowed\}/);
    assert.doesNotMatch(shell, /\.button:disabled\{[^}]*opacity:\.65/);
});

test("reduced motion never introduces a positioning transition", () => {
    const shell = readFileSync(join(build, manifest["Assets/Shared/shell.entry.scss"].file), "utf8");
    assert.match(shell, /@media\(prefers-reduced-motion:reduce\)\{[^}]*transition-duration:0s/);
});
