import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";

const root = resolve(import.meta.dirname, "..");
const load = file => readFileSync(resolve(root, file), "utf8");

test("Login is a short page composition, Form owns field HTML", () => {
    const view = load("Pages/Account/Login.cshtml");
    const page = load("Pages/Account/Login.cshtml.cs");
    const form = load("Framework/Components/Form.cs");
    assert.match(view, /<partial name="_PageBody"/);
    assert.doesNotMatch(view, /<(?:form|h1|section|button|input)(?:\s|>)/i);
    assert.match(page, /page\.Title\("account\.login\.title"\)/);
    assert.match(page, /page\.Asset\("Pages\/Account\/Login\.entry\.scss"\)/);
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
    assert.match(tabs, /Attribute\("href", item\.Href\)/);
    assert.match(tabs, /Exactly one tab must be selected/);
});

test("Settings use generic sections and content lists", () => {
    const settings = load("Pages/Settings.cshtml.cs");
    assert.match(settings, /new UiSection\(/);
    assert.match(settings, /new ItemList\(/);
    assert.ok(!existsSync(resolve(root, "Framework/Components/SettingsGroup.cs")));
});
