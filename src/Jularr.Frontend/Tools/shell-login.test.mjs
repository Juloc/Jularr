import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";

const root = resolve(import.meta.dirname, "..");
const load = file => readFileSync(resolve(root, file), "utf8");

test("Login owns its C# page and Razor remains a thin route", () => {
    const view = load("Pages/Account/Login.cshtml");
    const page = load("Pages/Account/Login.cshtml.cs");
    assert.match(view, /<partial name="_PageBody"/);
    assert.doesNotMatch(view, /<(?:form|h1|section|button|input)(?:\s|>)/i);
    assert.match(page, /page\.Title\("account\.login\.title"\)/);
    assert.match(page, /page\.Asset\("Pages\/Account\/Login\.entry\.scss"\)/);
    assert.match(page, /new Brand\(login: true\)/);
    assert.match(page, /Attribute\("disabled", "disabled"\)/);
});

test("Shell markup belongs to independent C# components", () => {
    const view = load("Pages/Shared/_AppShell.cshtml");
    assert.doesNotMatch(view, /<(?:header|nav|aside|button)(?:\s|>)/i);
    for (const name of ["ShellHeader", "ShellSidebar", "ShellMobileNavigation"]) {
        assert.match(view, new RegExp("new " + name + "\\("));
        assert.match(load("Framework/Components/" + name + ".cs"), /void WriteTo\(/);
    }
});

test("Shell never invents optional media navigation or duplicate page headings", () => {
    const routes = load("Framework/ShellNavigation.cs");
    assert.doesNotMatch(routes, /new\("nav\.watchlist"/);
    assert.match(load("Framework/Components/ShellHeader.cs"), /"nav\.breadcrumb"/);
    assert.doesNotMatch(load("Framework/Components/ShellHeader.cs"), /new H1\(/);
    assert.match(load("Localization/UiResources.cs"), /new\("nav\.secondary", "Secondary navigation"\)/);
});
