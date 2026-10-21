import assert from "node:assert/strict";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { test } from "node:test";

const root = resolve(import.meta.dirname, "..");
const load = file => readFileSync(resolve(root, file), "utf8").replaceAll("\r\n", "\n");

test("Pages own folders, explicit routes and colocated definitions", () => {
    for (const file of readdirSync(resolve(root, "Pages"), { recursive: true })) {
        if (!file.endsWith(".cshtml") || file.startsWith("Shared") || file.startsWith("_")) continue;

        const path = "Pages/" + file.replaceAll("\\", "/");
        const directory = dirname(path);
        const view = load(path);
        const model = load(path + ".cs");
        const expectedNamespace = "Jularr.Frontend." + directory.replaceAll("/", ".");

        assert.match(directory, /^Pages\/(?:Account|Admin|User|Development|Setup)\/[^/]+$/);
        assert.match(view, /^@page "\/[^"\n]*"/);
        assert.ok(model.includes("namespace " + expectedNamespace + "\n"), path);
        assert.ok(view.includes("@model " + expectedNamespace + "."), path);
    }
});

test("Login is a short page composition, Form owns field HTML", () => {
    const view = load("Pages/Account/Login/Login.cshtml");
    const page = load("Pages/Account/Login/Login.cshtml.cs");
    const form = load("Framework/Components/Form.cs");
    assert.match(view, /<partial name="_PageBody"/);
    assert.doesNotMatch(view, /<(?:form|h1|section|button|input)(?:\s|>)/i);
    assert.match(page, /page\.Title\("account\.login\.title"\)/);
    assert.match(page, /page\.Asset\("Pages\/Account\/Login\/Login\.entry\.scss"\)/);
    assert.match(page, /new Form<LoginDataV1>/);
    assert.match(page, /form\.Password\(x => x\.Password/);
    assert.doesNotMatch(page, /private sealed class (?:Label|FormElement)/);
    assert.match(form, /BaseField\.From\(/);
    assert.match(form, /Attribute\("disabled", "disabled"\)/);
});

test("Shell has one owning C# file, not feature-specific Framework wrappers", () => {
    const view = load("Pages/Shared/_AppShell.cshtml");
    const shell = load("Pages/Shared/Shell.cs");
    assert.doesNotMatch(view, /<(?:header|nav|aside|button)(?:\s|>)/i);
    assert.match(view, /new Shell\(/);
    assert.match(shell, /"nav\.breadcrumb"/);
    assert.doesNotMatch(shell, /new H1\(/);
    for (const old of ["ShellHeader", "ShellSidebar", "ShellMobileNavigation"]) {
        assert.ok(!existsSync(resolve(root, "Framework/Components/" + old + ".cs")));
    }
    assert.match(load("Assets/Shared/shell.entry.scss"), /Pages\/Shared\/Shell/);
});

test("Shell has an admin mode, a linked breadcrumb and a one-row flat header", () => {
    const shell = load("Pages/Shared/Shell.cs");
    const styles = load("Pages/Shared/Shell.scss");
    assert.match(shell, /adminMode/);
    assert.match(shell, /expand: true/);
    assert.match(shell, /new Ol\(/);
    assert.match(shell, /new LinkMenu\(/);
    assert.match(shell, /AdministrationLinks\(texts, adminPath\)/);
    assert.match(styles, /breadcrumb__item--menu \.link-menu \{ display: block/);
    assert.match(shell, /Add\(new Text\("nav\.admin"\)/);
    assert.match(styles, /\.app-header\s*\{[^}]*height: 64px/);
    assert.match(styles, /container: crumbs/);
    assert.match(styles, /nth-last-child\(n \+ 3\)/);
    assert.doesNotMatch(styles, /flex-wrap: wrap|box-shadow/);
});

test("Clean shows the single-path parrot mark, Original keeps its own mark, and the J in a ring is gone", () => {
    const clean = load("Pages/Shared/brand-clean.svg");
    assert.equal(clean.match(/<path\b/g)?.length, 1);
    assert.match(clean, /fill="currentColor"/);
    assert.doesNotMatch(clean, /\bstyle=|<g\b|transform=/);
    const [, side] = /viewBox="0 0 ([\d.]+) \1"/.exec(clean) ?? [];
    assert.ok(side, "the mark has a square viewBox starting at the origin");
    assert.match(load("Pages/Shared/brand-original.svg"), /--jularr-ring/);
    assert.ok(!existsSync(resolve(root, "Pages/Shared/brand-mark.svg")), "the J in a ring must not exist");

    const brand = load("Pages/Shared/Brand.cs");
    assert.match(brand, /Mark\("brand-clean", "brand-symbol__clean"\)/);
    assert.match(brand, /Mark\("brand-original", "brand-symbol__original"\)/);
    assert.match(load("Pages/Shared/Brand.scss"), /html\[data-skin="original"\] \.brand-symbol \{\s*&__clean \{ display: none; \}\s*&__original \{ display: block; \}/);

    const favicon = load("Assets/Shared/favicon.svg");
    assert.equal(favicon.match(/<path\b[^>]*d=/g)?.length, 1);
    assert.match(favicon, /prefers-color-scheme:dark/);
    for (const icon of ["icon-32", "apple-touch-icon", "icon-192", "icon-512", "icon-maskable-512"]) {
        const png = readFileSync(resolve(root, "Assets/Shared/" + icon + ".png"));
        assert.equal(png.subarray(1, 4).toString(), "PNG");
    }
    const layout = load("Pages/Shared/_Layout.cshtml");
    assert.match(layout, /rel="icon" type="image\/svg\+xml"/);
    assert.match(layout, /rel="apple-touch-icon"/);
});

test("Shell does not invent optional media modules", () => {
    const shell = load("Pages/Shared/Shell.cs");
    assert.doesNotMatch(shell, /new\("nav\.watchlist"/);
    assert.match(load("Localization/UiResources.cs"), /new\("nav\.secondary", "Secondary navigation"\)/);
});

test("Split offers MasterDetail as optional layout mode", () => {
    const split = load("Framework/Components/Split.cs");
    assert.match(split, /public Split MasterDetail\(/);
    assert.match(split, /split--master-detail/);
    assert.match(split, /"nav\.back"/);
    assert.ok(!existsSync(resolve(root, "Framework/Components/MasterDetail.cs")));
});

test("Tabs show only selected content and provide real server navigation", () => {
    const tabs = load("Framework/Components/Tabs.cs");
    assert.match(tabs, /Items\.Single\(item => item\.Selected\)/);
    assert.match(tabs, /new NavigationTabs\(/);
    const navigation = load("Framework/Components/NavigationTabs.cs");
    assert.match(navigation, /Attribute\("href", tab\.Href\)/);
    assert.match(navigation, /Attribute\("aria-current", "page"\)/);
    assert.match(tabs, /Exactly one tab must be selected/);
});

test("Settings use backend services and generic forms without unsupported controls", () => {
    const settings = load("Pages/User/Settings/Settings.cshtml.cs");
    assert.match(settings, /new UiSection\(/);
    assert.match(settings, /new Form<UpdateProfileDataV1>/);
    assert.match(settings, /updateProfile\.ExecuteAsync<UpdateResult>/);
    assert.doesNotMatch(settings, /Npgsql|SqlStatement|settings\.playback|settings\.connections/);
    assert.ok(!existsSync(resolve(root, "Framework/Components/SettingsGroup.cs")));
});
