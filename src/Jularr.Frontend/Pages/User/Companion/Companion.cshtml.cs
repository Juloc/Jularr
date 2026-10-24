using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Companion
{
    public sealed class CompanionModel(IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        protected override void Define(Page page)
        {
            page.Title("companion.title");
            page.Parent("settings.title", "/Settings");
            page.Navigation("nav.settings");
            page.Asset("Pages/User/Companion/Companion.entry.ts");
            page.Asset("Pages/User/Companion/Companion.entry.scss");

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "";
            page.Content.Add(new Section(
                new P(new Text("companion.description")).Class("companion__intro"),
                new Div(
                    new Label(new Text("companion.invite")).Attribute("for", "companion-token"),
                    new Div(
                        new Input().Attribute("id", "companion-token").Attribute("type", "text").Attribute("name", "token")
                            .Attribute("autocomplete", "off").Attribute("spellcheck", "false").Attribute("required", "required")
                            .TextAttribute("placeholder", "companion.invitePlaceholder"),
                        new Button(new Text("companion.connect")).Primary().Attribute("data-companion-connect", "")
                    ).Attribute("data-companion-claim", "").Class("companion__claim"),
                    new P(new Text("companion.inviteHint")).Class("companion__hint")
                ).Class("companion__pair").Attribute("data-companion-pair", ""),
                new Div(
                    new P(new Text("companion.disconnected")).Attribute("data-companion-status", "").Attribute("role", "status").Attribute("aria-live", "polite"),
                    new P(new ValueText("")).Attribute("data-companion-feedback", "").Attribute("role", "alert"),
                    new Div(
                        new Button(new Text("companion.play")).Attribute("data-companion-command", "play").Disabled(),
                        new Button(new Text("companion.pause")).Attribute("data-companion-command", "pause").Disabled(),
                        new Button(new Text("companion.disconnect")).Soft().Attribute("data-companion-disconnect", "")
                    ).Class("companion__actions"),
                    new Div(
                        new Label(new Text("companion.seek")).Attribute("for", "companion-seek"),
                        new Input().Attribute("id", "companion-seek").Attribute("type", "range").Attribute("min", "0")
                            .Attribute("max", "1").Attribute("step", "1000").Attribute("value", "0").Attribute("disabled", "disabled"),
                        new Span(new ValueText("0:00")).Attribute("data-companion-time", "")
                    ).Class("companion__seek")
                ).Class("companion__controls").Attribute("data-companion-controls", "").Attribute("hidden", "hidden")
            ).Class("companion").Attribute("data-companion", "")
                .Attribute("data-csrf", csrf)
                .Attribute("data-status-online", texts["companion.connected"])
                .Attribute("data-status-offline", texts["companion.connectionLost"])
                .Attribute("data-status-pending", texts["companion.pending"])
                .Attribute("data-status-failed", texts["companion.failed"])
                .Attribute("data-status-rejected", texts["companion.rejected"])
                .Attribute("data-status-ended", texts["companion.ended"])
                .Attribute("data-status-paired", texts["companion.paired"]));
        }

        public IActionResult OnGet()
        {
            PreparePage();
            return Page();
        }
    }
}
