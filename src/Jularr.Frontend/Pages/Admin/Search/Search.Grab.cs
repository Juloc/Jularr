using System.Globalization;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Search
{
    // Holen + importieren: the work the release is for (recognised from its name, else picked by a person), then the normal grab of that wanted title, so the release
    // downloads and is imported like one its own search found. The dialog comes from the server; without script it is the page with the dialog open (pick=<releaseId>).
    public sealed partial class SearchModel
    {
        [BindProperty(SupportsGet = true, Name = "pick")]
        public string? Pick { get; set; }

        [BindProperty(SupportsGet = true, Name = "work")]
        public string? WorkQuery { get; set; }

        // The release's name, shown in the dialog; it comes with the link and is only shown.
        [BindProperty(SupportsGet = true, Name = "title")]
        public string? ReleaseTitle { get; set; }

        private IUiComponent? _pickDialog;

        // The dialog of one release: the recognised work chosen, the works a query finds below it.
        public async Task<IActionResult> OnGetPickAsync(string releaseId)
        {
            Pick = releaseId;
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            return Fragment(await PickDialogAsync(csrf, true));
        }

        // The recognised work of a release, for the bulk grab: a release without a clear one is asked for.
        public async Task<IActionResult> OnPostWorkAsync(string releaseId)
        {
            var work = await recognize.ExecuteAsync<IndexerReleaseWorkV1>(new IndexerReleaseParametersV1(releaseId ?? ""), new NoData(), HttpContext.RequestAborted);
            return new JsonResult(new { wantedItemId = work.WantedItemId, title = work.Title });
        }

        public async Task<IActionResult> OnPostGrabAsync(string releaseId, long wantedItemId, string? returnUrl)
        {
            var script = Request.Headers.Accept.ToString().Contains("application/json", StringComparison.Ordinal);
            try
            {
                await grab.ExecuteAsync<UpdateResult>(new IndexerReleaseParametersV1(releaseId ?? ""), new GrabIndexerReleaseDataV1(wantedItemId), HttpContext.RequestAborted);
            }
            catch (Exception exception) when (script && exception is not OperationCanceledException)
            {
                var error = ServiceError.Describe(exception, HttpContext, HttpContext.RequestServices.GetRequiredService<ILogger<JularrPage>>());
                return new JsonResult(new { ok = false, message = texts[error.FieldResourceKey ?? error.ResourceKey] }) { StatusCode = error.StatusCode };
            }

            if (script) return new JsonResult(new { ok = true, message = texts["search.grab.sent"] });
            Hint("search.grab.sent");
            return LocalRedirect(returnUrl is { Length: > 0 } local && local.StartsWith("/Admin/Search", StringComparison.Ordinal) ? local : "/Admin/Search");
        }

        private async Task<IUiComponent> PickDialogAsync(string? csrf, bool fragment)
        {
            var releaseId = Pick ?? "";
            var recognised = WorkQuery is { Length: > 0 } ? null
                : await recognize.ExecuteAsync<IndexerReleaseWorkV1>(new IndexerReleaseParametersV1(releaseId), new NoData(), HttpContext.RequestAborted);
            var found = WorkQuery is { Length: > 0 } query
                ? await findWorks.ExecuteAsync<BaseDataList<ManualImportWorkV1>>(new ManualImportWorksParametersV1(query, true), HttpContext.RequestAborted)
                : new BaseDataList<ManualImportWorkV1>([]);
            var options = new List<IUiComponent>();
            if (recognised?.WantedItemId is { } recognisedId)
            {
                options.Add(WorkOption(recognisedId, recognised.Title ?? "", recognised.ReleaseYear, recognised.MediaTypeId is { } type ? MediaTypeLabel.Key(type, null) : null, true));
            }

            options.AddRange(found.Where(work => work.WantedItemId != recognised?.WantedItemId)
                .Select((work, index) => WorkOption(work.WantedItemId, work.CanonicalTitle, work.ReleaseYear, MediaTypeLabel.Key(work.MediaTypeId, work.MediaSubtypeId), options.Count == 0 && index == 0)));
            IUiComponent note = recognised is { WantedItemId: null, ProblemKey: { } problem } ? new P(new Text("search.grab." + problem)).Class("search-pick__note")
                : options.Count == 0 ? new P(new Text("search.grab.noWork")).Class("search-pick__note") : new Span();
            var title = new P(new ValueText(ReleaseTitle is { Length: > 0 and <= 1000 } name ? name : "")).Class("search-pick__release");
            var search = new FormElement(
                new Input().Attribute("type", "hidden").Attribute("name", "pick").Attribute("value", releaseId),
                new Input().Attribute("type", "hidden").Attribute("name", "title").Attribute("value", ReleaseTitle ?? ""),
                new Input().Attribute("type", "search").Attribute("name", "work").Attribute("value", WorkQuery ?? "").Attribute("autocomplete", "off")
                    .Class("search-pick__query").TextAttribute("placeholder", "search.grab.find").TextAttribute("aria-label", "search.grab.find"),
                new Button(new Icon("search", assets, 16)).IconOnly(texts["search.grab.find"]).Submit())
                .Class("search-pick__search").Attribute("method", "get").Attribute("action", "/Admin/Search").Attribute("hx-boost", "false").Attribute("data-search-pick-find", "");
            var choose = new PostForm("/Admin/Search?handler=Grab", csrf, [("releaseId", releaseId), ("returnUrl", Address(null))],
                new Div([.. options]).Class("search-pick__works").Attribute("role", "radiogroup").TextAttribute("aria-label", "search.grab.work"),
                new Div(new Button(new Text("common.cancel")).Soft().Attribute("data-dialog-close", ""),
                    options.Count == 0 ? new Button(new Icon("download", assets, 16), new Span(new Text("search.grab"))).Primary().Disabled()
                        : new Button(new Icon("download", assets, 16), new Span(new Text("search.grab"))).Primary().Submit()).Class("search-pick__actions"))
                .Attribute("hx-boost", "false").Attribute("data-search-send", "").TextAttribute("data-failed", "search.grab.failed");
            return new Modal(assets, "search-pick", "search.grab.title", [title, note, search, choose], open: !fragment, returnTo: fragment ? null : Address(null));
        }

        // Holen + importieren of a row: the dialog with script, the page with it open without.
        private IUiComponent PickLink(IndexerSearchReleaseV1 release) =>
            new A(new Icon("plus", assets, 16), new Span(new Text("search.grab")).Class("search-download__text"))
                .Class("button button--compact button--primary search-action").Attribute("href", Address(null) + "&inline=true&pick=" + release.ReleaseId + "&title=" + Uri.EscapeDataString(release.Title))
                .Attribute("data-search-pick", release.ReleaseId).TextAttribute("title", "search.grab");

        private static IUiComponent WorkOption(long wantedItemId, string title, int? year, string? typeKey, bool chosen) => new Label(
            new Input().Attribute("type", "radio").Attribute("name", "wantedItemId").Attribute("value", wantedItemId.ToString(CultureInfo.InvariantCulture)).Attribute(chosen ? "checked" : "data-unchecked", chosen ? "checked" : ""),
            new Span(new Span(new ValueText(title)).Class("search-pick__title"),
                new Span(typeKey is null ? new Span() : new Text(typeKey), new ValueText(year is { } y ? " · " + y.ToString(CultureInfo.InvariantCulture) : "")).Class("search-pick__meta")))
            .Class("search-pick__work");
    }
}
