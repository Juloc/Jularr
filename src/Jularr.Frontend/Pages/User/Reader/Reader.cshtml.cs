using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Service.User.Reading.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Reader
{
    public sealed class ReaderModel(OpenReadingV1 open, ReadReadingTocV1 toc, ReadReadingSectionV1 section, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private ReadingDocumentV1? _document;
        private ReadingSectionV1? _section;
        private IReadOnlyList<ReadingTocEntryV1> _toc = [];

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty(SupportsGet = true, Name = "s")]
        public int? SectionIndex { get; set; }

        [BindProperty(SupportsGet = true, Name = "p")]
        public int? PageIndex { get; set; }

        protected override void Define(Page page)
        {
            page.Title("reader.title");
            page.Shell = false;
            if (_document is { ReadingFormatTypeId: ReadingFormatType.Comic } comic)
            {
                page.Asset("Modules/Reader/comic.entry.ts");
                page.Content.Add(ComicView(comic));
                return;
            }

            page.Asset("Modules/Reader/reader.entry.ts");
            if (_document is not { } document || _section is not { } current)
            {
                // Offline storage and an uncached book say so.
                page.Content.Add(new Div(
                    new P(new Text(FormError?.ResourceKey ?? "reader.notFound")).Attribute("role", "alert"),
                    new A(new Text("common.back")).Class("button").Attribute("href", "/Library")
                ).Class("reader__status"));
                return;
            }

            var asset = Id.ToString(CultureInfo.InvariantCulture);
            var index = current.SectionIndex;
            var top = new Div(
                new A(new Text("common.back")).Class("button").Attribute("href", "/Details/" + document.WorkId.ToString(CultureInfo.InvariantCulture)),
                new Div(new H1(new ValueText(document.Title)).Class("reader__title"), new P(new ValueText(current.Title ?? "")).Class("reader__chapter")).Class("reader__heading"),
                new Button(new Text("reader.contents")).Class("button").Attribute("data-action", "contents").Attribute("aria-expanded", "false"),
                new Button(new Text("reader.settings")).Class("button").Attribute("data-action", "settings").Attribute("aria-expanded", "false")
            ).Class("reader__top");

            // Contents and settings share one panel area.
            var contents = new Nav([.. _toc.Select(entry => new A(new ValueText(entry.Title)).Class("reader__toc-entry" + (entry.SectionIndex == index ? " is-current" : ""))
                .Attribute("href", "?s=" + entry.SectionIndex.ToString(CultureInfo.InvariantCulture) + (entry.Anchor is null ? "" : "#" + Uri.EscapeDataString(entry.Anchor)))
                .Attribute("data-level", entry.Level.ToString(CultureInfo.InvariantCulture)))]).Class("reader__panel").Attribute("data-panel", "contents").Attribute("hidden", "hidden")
                .Attribute("aria-label", texts["reader.contents"]);
            var settings = new Div(
                Setting("reader.fontSize", [("smaller", "reader.smaller"), ("larger", "reader.larger")], "size"),
                Setting("reader.theme", [("light", "reader.theme.light"), ("sepia", "reader.theme.sepia"), ("dark", "reader.theme.dark")], "theme"),
                Setting("reader.mode", [("scroll", "reader.mode.scroll"), ("paged", "reader.mode.paged")], "mode")
            ).Class("reader__panel").Attribute("data-panel", "settings").Attribute("hidden", "hidden");

            var navigation = new List<IUiComponent>();
            navigation.Add(index > 0
                ? new A(new Text("reader.previous")).Class("button").Attribute("href", "?s=" + (index - 1).ToString(CultureInfo.InvariantCulture)).Attribute("data-role", "previous")
                : new Span());
            navigation.Add(new Span(new ValueText(Percent(current.CharactersBefore, current.TotalCharacters))).Class("reader__position").Attribute("data-role", "position"));
            navigation.Add(index + 1 < document.SectionCount
                ? new A(new Text("reader.next")).Class("button button--primary").Attribute("href", "?s=" + (index + 1).ToString(CultureInfo.InvariantCulture)).Attribute("data-role", "next")
                : new Span(new Text("reader.end")));

            page.Content.Add(new Div(
                top,
                contents,
                settings,
                new ReaderSection(current),
                new P(new Text("progress.saveFailed")).Attribute("data-role", "save-error").Attribute("role", "status").Attribute("hidden", "hidden"),
                new Div([.. navigation]).Class("reader__bottom")
            ).Class("reader")
                .Attribute("data-progress-url", "/api/v1/reading/assets/" + asset + "/progress")
                .Attribute("data-antiforgery", antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "")
                .Attribute("data-section", index.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-sections", document.SectionCount.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-blocks", current.Blocks.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-before", current.CharactersBefore.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-characters", current.Characters.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-total", current.TotalCharacters.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-locator", document.Locator ?? ""));
            if (!document.IsCached)
            {
                page.Content.Add(new P(new Text("reader.notCached")).Class("reader__note").Attribute("role", "status"));
            }
        }

        // One page at a time with links that work without the script; the script adds spreads, direction and preloading.
        private IUiComponent ComicView(ReadingDocumentV1 document)
        {
            var count = Math.Max(1, document.SectionCount);
            var index = Math.Clamp(PageIndex ?? (document.Locator is { } locator && locator.StartsWith('p')
                && int.TryParse(locator[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var saved) ? saved : 0), 0, count - 1);
            var pages = "/api/v1/reading/documents/" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + "/resource?path=";
            string Page(int number) => "?p=" + number.ToString(CultureInfo.InvariantCulture);
            var top = new Div(
                new A(new Text("common.back")).Class("button").Attribute("href", "/Details/" + document.WorkId.ToString(CultureInfo.InvariantCulture)),
                new H1(new ValueText(document.Title)).Class("comic__title"),
                new Button(new Text("reader.comic.direction")).Class("button").Attribute("data-action", "direction"),
                new Button(new Text("reader.comic.spread")).Class("button").Attribute("data-action", "spread").Attribute("aria-pressed", "false")
            ).Class("comic__top");
            var previous = new A(new Text("reader.previous")).Class("button").Attribute("href", Page(Math.Max(0, index - 1))).Attribute("data-role", "previous");
            var next = new A(new Text("reader.next")).Class("button button--primary").Attribute("href", Page(Math.Min(count - 1, index + 1))).Attribute("data-role", "next");
            if (index == 0) previous.Attribute("hidden", "hidden");
            if (index + 1 >= count) next.Attribute("hidden", "hidden");
            var bottom = new Div(
                previous,
                new Span(new ValueText((index + 1).ToString(CultureInfo.InvariantCulture) + " / " + count.ToString(CultureInfo.InvariantCulture))).Class("comic__position")
                    .Attribute("data-role", "position").Attribute("aria-live", "polite"),
                next
            ).Class("comic__bottom");
            var stage = new Div(new Img().Attribute("src", pages + Uri.EscapeDataString(PagePath(index))).Attribute("alt", texts["reader.comic.page"] + " " + (index + 1).ToString(CultureInfo.InvariantCulture)))
                .Class("comic__stage").Attribute("data-role", "stage");

            var note = document.IsCached ? (IUiComponent)new Span() : new P(new Text("reader.notCached")).Class("reader__note").Attribute("role", "status");
            return new Div(top, stage, new P(new Text("progress.saveFailed")).Attribute("data-role", "save-error").Attribute("role", "status").Attribute("hidden", "hidden"), note, bottom)
                .Class("comic")
                .Attribute("data-progress-url", "/api/v1/reading/assets/" + Id.ToString(CultureInfo.InvariantCulture) + "/progress")
                .Attribute("data-antiforgery", antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "")
                .Attribute("data-pages", pages)
                .Attribute("data-page", index.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-count", count.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-direction", document.IsRightToLeft ? "rtl" : "ltr")
                .Attribute("data-page-label", texts["reader.comic.page"]);
        }

        private static string PagePath(int index) => ":page/" + index.ToString("D5", CultureInfo.InvariantCulture);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _document = await open.ExecuteAsync<ReadingDocumentV1>(new ReadingAssetParametersV1(Id), default, ct);
            if (_document.ReadingFormatTypeId == ReadingFormatType.Comic)
            {
                return;
            }

            // Without a section in the address, continue where this profile stopped.
            var index = SectionIndex ?? (_document.Locator is { } locator && locator.StartsWith('s') && int.TryParse(locator[1..].Split('/')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var saved)
                ? saved
                : 0);
            index = Math.Clamp(index, 0, Math.Max(0, _document.SectionCount - 1));
            _section = await section.ExecuteAsync<ReadingSectionV1>(new ReadingSectionParametersV1(_document.DocumentId, index), ct);
            _toc = await toc.ExecuteAsync<BaseDataList<ReadingTocEntryV1>>(new ReadingDocumentParametersV1(_document.DocumentId), ct);
        }

        private IUiComponent Setting(string labelKey, (string Value, string LabelKey)[] options, string name) =>
            new Div(new Span(new Text(labelKey)), new Div([.. options.Select(option => new Button(new Text(option.LabelKey)).Class("button")
                .Attribute("data-setting", name).Attribute("data-value", option.Value))]).Class("reader__choices")).Class("reader__setting");

        private static string Percent(long before, long total) =>
            total <= 0 ? "" : Math.Clamp((int)Math.Round(before * 100d / total), 0, 100).ToString(CultureInfo.InvariantCulture) + " %";
    }
}
