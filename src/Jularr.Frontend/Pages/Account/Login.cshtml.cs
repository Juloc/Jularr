using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel : JularrPage
{
    protected override void Define(Page page)
    {
        page.Shell = false;
        page.Title("account.login.title");
        page.Asset("Pages/Account/Login.entry.scss");

        // This is the approved, minimal login page. No provider, passkey or
        // recovery action is displayed until the Backend confirms it exists.
        // Fields stay disabled until the canonical authentication flow is wired.
        page.Content.Add(new Section(
            new Brand(login: true),
            new H1(new Text("account.login.title"))
                .Attribute("id", "login-title").Class("visually-hidden"),
            new FormElement(
                Field("account.field.userName", "login-username", "username", "text", "username"),
                Field("account.field.password", "login-password", "password", "password", "current-password"),
                new Label(
                    new Input().Attribute("type", "checkbox").Attribute("disabled", "disabled"),
                    new Span(new Text("account.login.remember"))
                ).Class("ui-checkbox"),
                new Button(new Text("account.login.submit"))
                    .Class("button button-primary")
                    .Attribute("type", "submit").Attribute("disabled", "disabled")
            ).Class("ui-form")
                .Attribute("aria-labelledby", "login-title")
                .Attribute("aria-describedby", "login-unavailable"),
            new P(new Text("account.login.unavailable"))
                .Class("auth-unavailable").Attribute("id", "login-unavailable")
                .Attribute("role", "status")
        ).Class("auth-card").Attribute("aria-labelledby", "login-title"));
    }

    private static IUiComponent Field(string key, string id, string name, string type, string autocomplete) =>
        new Div(
            new Label(new Text(key)).Attribute("for", id),
            new Input().Attribute("id", id).Attribute("name", name)
                .Attribute("type", type).Attribute("autocomplete", autocomplete)
                .Attribute("disabled", "disabled")
        ).Class("ui-field");

    private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);
    private sealed class FormElement(params IUiComponent[] children) : HtmlElement("form", children);

    public IActionResult OnGet()
    {
        PreparePage();
        return Page();
    }
}
