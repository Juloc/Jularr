using System.Globalization;
using Jularr.Backend.Data.Admin.Ai.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Ai.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Ai
{
    // The instance's one Codex connection as a card: its state, and Connect with the address and code while a login waits, Cancel, Disconnect.
    public sealed class AiModel(ReadCodexConnectionV1 read, StartCodexLoginV1 start, CancelCodexLoginV1 cancel, LogoutCodexV1 logout, FrontendAssets assets, IAntiforgery antiforgery,
        UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Ai";
        private const string DisconnectDialog = "codex-disconnect";
        private CodexConnectionV1? _connection;

        protected override void Define(Page page)
        {
            page.Title("admin.ai.title");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Pages/Admin/Ai/Ai.entry.ts");
            if (_connection is null) return;
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            page.Content.Add(new CardGrid(Card(_connection, csrf)));
            page.Content.Add(Modal.Confirm(assets, DisconnectDialog, "admin.ai.disconnectTitle", "admin.ai.disconnect", Address + "?handler=Logout", csrf, new CodexConnectionParametersV1()));
        }

        // The state in the head, the facts under it, the actions at the foot.
        private IUiComponent Card(CodexConnectionV1 connection, string? csrf)
        {
            var (key, tone) = connection.Phase switch
            {
                CodexPhase.Connected => ("admin.ai.connected", UiTone.Success),
                CodexPhase.LoginPending => ("admin.ai.pending", UiTone.Warning),
                CodexPhase.NotInstalled => ("admin.ai.notInstalled", UiTone.Danger),
                _ => ("admin.ai.notConnected", UiTone.Neutral)
            };

            var body = new List<IUiComponent>();
            var facts = new List<(string, IUiComponent)>();
            if (connection.Version is { } version) facts.Add(("admin.ai.version", new ValueText(version)));
            if (connection.SignIn is { } signIn) facts.Add(("admin.ai.signIn", new Text("admin.ai.signIn." + signIn)));
            if (connection.VerificationUrl is { } url && connection.UserCode is { } code)
            {
                facts.Add(("admin.ai.enterCode", new ValueText(code)));
            }

            if (facts.Count > 0) body.Add(new FactList(facts));
            if (connection.Phase == CodexPhase.NotInstalled) body.Add(new P(new Text("admin.ai.notInstalledNote")).Class("feature-card__note"));
            if (connection.ErrorKey is { } error) body.Add(new P(new Text("errors." + error)).Class("feature-card__note"));

            var actions = new List<IUiComponent>();
            switch (connection.Phase)
            {
                case CodexPhase.NotConnected:
                    actions.Add(Post("Login", csrf, new Button(new Icon("link", assets, 14), new Text("admin.ai.connect")).Compact().Soft().Submit()));
                    break;
                case CodexPhase.LoginPending:
                    if (connection.VerificationUrl is { } link)
                    {
                        actions.Add(new A(new Icon("link", assets, 14), new Text("admin.ai.openLink")).Class("button button--soft button--compact").Attribute("href", link)
                            .Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"));
                    }

                    actions.Add(Post("Cancel", csrf, new Button(new Icon("close", assets, 14), new Text("admin.ai.cancel")).Compact().Soft().Submit()));
                    break;
                case CodexPhase.Connected:
                    actions.Add(Modal.Opener(new Button(new Icon("power", assets, 14), new Text("admin.ai.disconnect")).Compact().Warn(), DisconnectDialog));
                    break;
            }

            var card = new FeatureCard(new IconTile(new Icon("spark", assets, 18), tone == UiTone.Neutral ? UiTone.Accent : tone), "OpenAI Codex", texts["admin.ai.line"],
                new StateChip(new Text(key), tone), body, actions, null, "codex-card").Wide();
            card.Attribute("data-codex-phase", connection.Phase.ToString());
            if (connection.Phase == CodexPhase.LoginPending) card.Attribute("data-codex-pending", "");
            return card;
        }

        private static IUiComponent Post(string handler, string? csrf, IUiComponent button) => new PostForm(Address + "?handler=" + handler, csrf, [], button);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // Only the card, for the page to ask again while a login waits.
        public async Task<IActionResult> OnGetCardAsync()
        {
            await LoadDataAsync();
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            Card(_connection!, csrf).WriteTo(writer, global::System.Text.Encodings.Web.HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        public async Task<IActionResult> OnPostLoginAsync()
        {
            await start.ExecuteAsync<CodexConnectionV1>(new CodexConnectionParametersV1(), new NoData(), HttpContext.RequestAborted);
            Hint("admin.ai.loginStarted");
            return LocalRedirect(Address);
        }

        public async Task<IActionResult> OnPostCancelAsync()
        {
            await cancel.ExecuteAsync<UpdateResult>(new CodexConnectionParametersV1(), new NoData(), HttpContext.RequestAborted);
            Hint("admin.ai.cancelDone");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostLogoutAsync()
        {
            await logout.ExecuteAsync<UpdateResult>(new CodexConnectionParametersV1(), new NoData(), HttpContext.RequestAborted);
            Hint("admin.ai.disconnectDone");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _connection = await read.ExecuteAsync<CodexConnectionV1>(new CodexConnectionParametersV1(), HttpContext.RequestAborted);
        }
    }
}
