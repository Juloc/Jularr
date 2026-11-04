using System.Globalization;
using Jularr.Backend.Data.Admin.Ai.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Ai.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Ai
{
    // The instance's one Codex connection as a card: its state, and Connect with the address and code while a login waits, Cancel, Disconnect. Once connected, the plan's usage
    // and the Owner's choices (model, effort, fallback, token limits) follow as cards.
    public sealed class AiModel(ReadCodexConnectionV1 read, StartCodexLoginV1 start, CancelCodexLoginV1 cancel, LogoutCodexV1 logout, ReadCodexCatalogV1 readCatalog, ReadCodexSettingsV1 readSettings, ReadCodexActivityV1 readActivity,
        SaveCodexSettingsV1 saveSettings, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Ai";
        private const string DisconnectDialog = "codex-disconnect";
        private CodexConnectionV1? _connection;
        private CodexCatalogV1? _catalog;
        private CodexSettingsV1? _settings;
        private CodexActivityV1? _activity;
        private CodexSettingsDataV1? _entered;

        protected override void Define(Page page)
        {
            page.Title("admin.ai.title");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Pages/Admin/Ai/Ai.entry.scss");
            page.Asset("Pages/Admin/Ai/Ai.entry.ts");
            if (_connection is null) return;
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            var cards = new List<IUiComponent> { Card(_connection, csrf) };
            if (_connection.Phase == CodexPhase.Connected && _catalog is not null && _settings is not null && _activity is not null)
            {
                if (_catalog.Windows.Length > 0 || _catalog.UsedToday > 0 || _catalog.UsedSinceStart > 0 || _activity.PlanLimitedUntil is not null) cards.Add(UsageCard(_catalog, _settings, _activity));
                if (_activity.Requests.Length > 0 || _activity.Chapters.Length > 0) cards.Add(ActivityCard(_activity));
                cards.Add(SettingsCard(_catalog, _settings, csrf));
            }

            page.Content.Add(new CardGrid([.. cards]));
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

        // The ChatGPT plan's windows as bars with the time they start over.
        private IUiComponent UsageCard(CodexCatalogV1 catalog, CodexSettingsV1 settings, CodexActivityV1 activity)
        {
            var windows = catalog.Windows.Select(window =>
            {
                var percent = Math.Round(window.UsedPercent).ToString(CultureInfo.InvariantCulture);
                var parts = new List<IUiComponent>
                {
                    new Div(new Span(new Text(window.Minutes is null or < 7 * 24 * 60 ? "admin.ai.windowShort" : "admin.ai.windowWeek")).Class("codex-window__label"),
                        new Span(new ValueText(percent + " %")).Class("codex-window__percent")).Class("codex-window__head"),
                    new Progress().Attribute("max", "100").Attribute("value", percent).TextAttribute("aria-label", "admin.ai.usage")
                };
                if (window.ResetsAt is { } reset) parts.Add(new Span(new Text("admin.ai.resets"), new ValueText(" "), new LocalTime(reset.UtcDateTime)).Class("codex-window__reset"));
                return (IUiComponent)new Div([.. parts]).Class("codex-window");
            });
            var plan = catalog.Plan is { Length: > 0 } name ? char.ToUpperInvariant(name[0]) + name[1..] : texts["admin.ai.usageLine"];
            // Jularr's own count against the Owner's limits; a limit of 0 shows the count alone.
            var counts = new List<(string, IUiComponent)>
            {
                ("admin.ai.usedToday", new ValueText(Count(catalog.UsedToday, settings.DailyTokens))),
                ("admin.ai.usedSinceStart", new ValueText(Count(catalog.UsedSinceStart, settings.RunTokens)))
            };
            var warn = settings.DailyTokens > 0 && catalog.UsedToday * 100 >= settings.DailyTokens * settings.WarnPercent;
            var body = new List<IUiComponent> { new Div([.. windows]).Class("codex-windows"), new FactList(counts) };
            StateChip? state = warn ? new StateChip(new Text(catalog.UsedToday >= settings.DailyTokens ? "admin.ai.limitReached" : "admin.ai.limitNear"), UiTone.Warning) : null;
            if (activity.PlanLimitedUntil is { } until)
            {
                state = new StateChip(new Text("admin.ai.planLimited"), UiTone.Danger);
                body.Insert(0, new P(new Text("admin.ai.planLimitedNote"), new ValueText(" "), new LocalTime(until.UtcDateTime)).Class("feature-card__note").Attribute("role", "status"));
            }

            // The tokens per day of the last two weeks, each bar against the busiest day.
            var days = activity.Days.TakeLast(14).ToArray();
            if (days.Length > 0)
            {
                var peak = Math.Max(1, days.Max(day => day.Tokens));
                body.Add(new Div(new P(new Text("admin.ai.history")).Class("codex-history__title"), new Div([.. days.Select(day => (IUiComponent)new Div(
                    new Span(new ValueText(day.Day[5..])).Class("codex-history__day"),
                    new Progress().Attribute("max", "100").Attribute("value", Math.Max(2, day.Tokens * 100 / peak).ToString(CultureInfo.InvariantCulture)).TextAttribute("aria-label", "admin.ai.history"),
                    new Span(new ValueText(day.Tokens.ToString("N0", CultureInfo.CurrentCulture))).Class("codex-history__tokens")).Class("codex-history__row"))]).Class("codex-history__rows")).Class("codex-history"));
            }

            return new FeatureCard(new IconTile(new Icon("pulse", assets, 18), UiTone.Accent), texts["admin.ai.usage"], plan, state, body, [], null, "codex-usage").Wide();
        }

        // The running chapters and requests and the last ones, newest first; the page asks again while one runs. A chapter adds up its requests, so the
        // Owner compares what one chapter costs with each model and effort.
        private IUiComponent ActivityCard(CodexActivityV1 activity)
        {
            var running = activity.Requests.Any(request => request.State == 0) || activity.Chapters.Any(chapter => chapter.State == 0);
            var chapters = activity.Chapters.Select(chapter => (IUiComponent)new Li(
                new Span(new LocalTime(chapter.Started.UtcDateTime, "time")).Class("codex-request__time"),
                new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["admin.ai.chapter"], chapter.Chapter) + (chapter.Title is { Length: > 0 } title ? " · " + title : "")))
                    .Class("codex-request__what"),
                new Span(new ValueText(ModelAndEffort(chapter.Model, chapter.Effort))).Class("codex-request__model"),
                new Span(new ValueText(Cost(chapter.Tokens, chapter.Seconds) + " · " + string.Format(CultureInfo.CurrentCulture, texts["admin.ai.chapterRequests"], chapter.Requests)))
                    .Class("codex-request__cost"),
                State(chapter.State)).Class("codex-request").Attribute("data-codex-chapter", ""));
            var requests = activity.Requests.Select(request => (IUiComponent)new Li(
                new Span(new LocalTime(request.Started.UtcDateTime, "time")).Class("codex-request__time"),
                new Span(new ValueText(request.What)).Class("codex-request__what"),
                new Span(new ValueText(ModelAndEffort(request.Model, request.Effort))).Class("codex-request__model"),
                new Span(new ValueText(Cost(request.Tokens, request.Seconds))).Class("codex-request__cost"),
                State(request.State)).Class("codex-request"));
            IUiComponent[] body = activity.Chapters.Length == 0 ? [new Ul([.. requests]).Class("codex-requests")]
                : [new P(new Text("admin.ai.chapters")).Class("codex-requests__title"), new Ul([.. chapters]).Class("codex-requests").Attribute("data-role", "codex-chapters"),
                    .. activity.Requests.Length == 0 ? Array.Empty<IUiComponent>() : [new P(new Text("admin.ai.requests")).Class("codex-requests__title"), new Ul([.. requests]).Class("codex-requests")]];
            var card = new FeatureCard(new IconTile(new Icon("activity", assets, 18), UiTone.Accent), texts["admin.ai.activity"], texts["admin.ai.activityLine"], null,
                body, [], null, "codex-activity").Wide();
            if (running) card.Attribute("data-codex-busy", "");
            return card;
        }

        private static StateChip State(byte state)
        {
            var (key, tone) = state switch
            {
                0 => ("admin.ai.requestRunning", UiTone.Accent),
                1 => ("admin.ai.requestDone", UiTone.Success),
                3 => ("admin.ai.requestPlanLimit", UiTone.Danger),
                _ => ("admin.ai.requestFailed", UiTone.Warning)
            };
            return new StateChip(new Text(key), tone, null, state == 0);
        }

        // The model and, when the Owner chose one, the effort by its name on this page.
        private string ModelAndEffort(string model, string? effort) =>
            effort is null ? model : model + " · " + (texts.Has("admin.ai.effort." + effort) ? texts["admin.ai.effort." + effort] : effort);

        private static string Cost(long tokens, double seconds) =>
            (tokens > 0 ? tokens.ToString("N0", CultureInfo.CurrentCulture) + " · " : "") + seconds.ToString("0.#", CultureInfo.CurrentCulture) + " s";

        private static string Count(long used, long limit) =>
            used.ToString("N0", CultureInfo.CurrentCulture) + (limit > 0 ? " / " + limit.ToString("N0", CultureInfo.CurrentCulture) : "");

        // The Owner's choices: they apply to everything Codex translates.
        private IUiComponent SettingsCard(CodexCatalogV1 catalog, CodexSettingsV1 saved, string? csrf)
        {
            var entered = _entered ?? new CodexSettingsDataV1(saved.Model, saved.Effort, saved.FallbackModel, saved.DailyTokens, saved.RunTokens, saved.WarnPercent);
            var efforts = catalog.Models.SelectMany(model => model.Efforts).Distinct().Select(effort => new SelectOption(effort, texts.Has("admin.ai.effort." + effort) ? texts["admin.ai.effort." + effort] : effort));
            var body = new List<IUiComponent>();
            if (!catalog.Available) body.Add(new P(new Text("admin.ai.catalogUnavailable")).Class("feature-card__note").Attribute("role", "status"));
            body.Add(new Form<CodexSettingsDataV1>(form =>
            {
                form.Select(item => item.Model, "admin.ai.model", ModelOptions(catalog, entered.Model));
                form.Select(item => item.Effort, "admin.ai.effort", [new SelectOption("", texts["admin.ai.codexDefault"]), .. efforts]);
                form.Select(item => item.FallbackModel, "admin.ai.fallback", ModelOptions(catalog, entered.FallbackModel, "admin.ai.noFallback"), new FieldHelp(Info: "admin.ai.fallbackHint"));
                form.Number(item => item.DailyTokens, "admin.ai.dailyTokens", new FieldHelp(Info: "admin.ai.limitHint"));
                form.Number(item => item.RunTokens, "admin.ai.runTokens");
                form.Number(item => item.WarnPercent, "admin.ai.warnPercent");
                form.Save();
            }, Address + "?handler=Settings", csrf, entered, _entered is null ? null : FormError));
            return new FeatureCard(new IconTile(new Icon("settings", assets, 18), UiTone.Accent), texts["admin.ai.settings"], texts["admin.ai.settingsLine"], null, body, [], null, "codex-settings").Wide();
        }

        // The account's models, with the saved one kept visible when the account no longer offers it.
        private IEnumerable<SelectOption> ModelOptions(CodexCatalogV1 catalog, string? chosen, string noneKey = "admin.ai.codexDefault")
        {
            yield return new SelectOption("", texts[noneKey]);
            foreach (var model in catalog.Models) yield return new SelectOption(model.Id, model.Name);
            if (!string.IsNullOrEmpty(chosen) && catalog.Models.All(model => model.Id != chosen)) yield return new SelectOption(chosen, chosen + " (" + texts["admin.ai.notOffered"] + ")");
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

        // Only the activity card, for the page to ask again while a request runs.
        public async Task<IActionResult> OnGetActivityAsync()
        {
            await LoadDataAsync();
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            if (_activity is { Requests.Length: > 0 } or { Chapters.Length: > 0 }) ActivityCard(_activity).WriteTo(writer, global::System.Text.Encodings.Web.HtmlEncoder.Default, texts);
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

        public async Task<IActionResult> OnPostSettingsAsync(string? model, string? effort, string? fallbackModel, long? dailyTokens, long? runTokens, int? warnPercent)
        {
            _entered = new CodexSettingsDataV1(model, effort, fallbackModel, dailyTokens ?? 0, runTokens ?? 0, warnPercent ?? 80);
            await saveSettings.ExecuteAsync<UpdateResult>(new CodexConnectionParametersV1(), _entered, HttpContext.RequestAborted);
            Hint("admin.ai.settingsSaved");
            return LocalRedirect(Address);
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
            if (_connection.Phase != CodexPhase.Connected) return;
            _catalog = await readCatalog.ExecuteAsync<CodexCatalogV1>(new CodexConnectionParametersV1(), HttpContext.RequestAborted);
            _settings = await readSettings.ExecuteAsync<CodexSettingsV1>(new CodexConnectionParametersV1(), HttpContext.RequestAborted);
            _activity = await readActivity.ExecuteAsync<CodexActivityV1>(new CodexConnectionParametersV1(), HttpContext.RequestAborted);
        }
    }
}
