using Jularr.Backend.Service.User.Translation.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Backend.Data.Public.Localization.V1;
using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Service.User.Reading.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Reader
{
    public sealed class ReaderModel(ReadReadingSourceV1 readSource, OpenReadingV1 open, ReadReadingTocV1 toc, ReadReadingSectionV1 section, ReadReadingSectionAnnotationsV1 readMarks, ReadReadingAnnotationsV1 readNotes,
        DeleteReadingAnnotationV1 deleteNote, SearchReadingV1 search, ReadUiLocalesV1 readLocales, ReadTranslationLanguagesV1 readLanguages, IAntiforgery antiforgery, UiText texts)
        : JularrPage
    {
        // The languages this book can be read in: its original first, then the profile's and the other allowed ones Mozilla's models reach.
        private string[] _languages = [];

        // A scan whose text is being recognized opens as a PDF meanwhile and says so.
        private string? _status;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private ReadingSourceV1? _source;
        private ReadingDocumentV1? _document;
        private ReadingSectionV1? _section;
        private IReadOnlyList<ReadingTocEntryV1> _toc = [];
        private IReadOnlyList<ReadingAnnotationV1> _marks = [];
        private PageResult<ReadingAnnotationV1>? _notes;
        private IReadOnlyList<ReadingSearchHitV1>? _hits;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty(SupportsGet = true, Name = "s")]
        public int? SectionIndex { get; set; }

        [BindProperty(SupportsGet = true, Name = "p")]
        public int? PageIndex { get; set; }

        [BindProperty(SupportsGet = true, Name = "n")]
        public int NotesPage { get; set; } = 1;

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Query { get; set; }

        protected override void Define(Page page)
        {
            page.Title("reader.title");
            page.Shell = false;
            // A PDF without text stays in the browser's own viewer.
            if (_source is { ReadingFormatTypeId: ReadingFormatType.Pdf } pdf && _document is null)
            {
                page.Asset("Modules/Reader/reader.entry.ts");
                page.Content.Add(PdfView(pdf));
                return;
            }

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
                new Button(new Text("reader.notes")).Class("button").Attribute("data-action", "notes").Attribute("aria-expanded", NotesPage > 1 ? "true" : "false"),
                new Button(new Text("reader.search")).Class("button").Attribute("data-action", "search").Attribute("aria-expanded", _hits is null ? "false" : "true"),
                new Button(new Text("reader.settings")).Class("button").Attribute("data-action", "settings").Attribute("aria-expanded", "false"),
                new Button(new Text("reader.focus")).Class("button").Attribute("data-action", "focus"),
                _languages.Length > 0
                    ? new Button(new ValueText(LanguageName(Chosen(document)))).Class("button").Attribute("data-action", "language").Attribute("aria-expanded", "false")
                        .Attribute("aria-label", texts["reader.language"])
                    : new Span(),
                _source?.ReadingFormatTypeId == ReadingFormatType.Pdf || document.TranslatedLanguages.Length > 0
                    ? new Button(new Text("reader.download")).Class("button").Attribute("data-action", "download").Attribute("aria-expanded", "false")
                    : new Span()
            ).Class("reader__top");

            // A converted PDF downloads as EPUB beside the original; a finished translation as its own EPUB.
            var ebook = "/api/v1/reading/documents/" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + "/ebook";
            IUiComponent[] converted = _source?.ReadingFormatTypeId == ReadingFormatType.Pdf
                ? [
                    new A(new Text("reader.download.epub")).Class("button").Attribute("href", ebook).Attribute("download", ""),
                    new A(new Text("reader.originalPdf")).Class("button").Attribute("href", "/api/v1/reading/assets/" + asset + "/file").Attribute("target", "_blank").Attribute("rel", "noopener")
                ]
                : [];
            var downloads = new Div([
                .. converted,
                .. document.TranslatedLanguages.Select(code => new A(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["reader.download.translated"], LanguageName(code)))).Class("button")
                    .Attribute("href", ebook + "?language=" + Uri.EscapeDataString(code)).Attribute("download", ""))
            ]).Class("reader__panel reader__choices").Attribute("data-panel", "download").Attribute("hidden", "hidden");

            // Contents and settings share one panel area.
            var contents = new Nav([.. _toc.Select(entry => new A(new ValueText(entry.Title)).Class("reader__toc-entry" + (entry.SectionIndex == index ? " is-current" : ""))
                .Attribute("href", "?s=" + entry.SectionIndex.ToString(CultureInfo.InvariantCulture) + (entry.Anchor is null ? "" : "#" + Uri.EscapeDataString(entry.Anchor)))
                .Attribute("data-level", entry.Level.ToString(CultureInfo.InvariantCulture)))]).Class("reader__panel").Attribute("data-panel", "contents").Attribute("hidden", "hidden")
                .Attribute("aria-label", texts["reader.contents"]);
            var notes = Notes(index);
            var searching = Search(index);
            var settings = new Div(
                Setting("reader.preset", [("book", "reader.preset.book"), ("light-novel", "reader.preset.lightNovel")], "preset"),
                Setting("reader.fontSize", [("smaller", "reader.smaller"), ("larger", "reader.larger")], "size"),
                Setting("reader.font", [("serif", "reader.font.serif"), ("sans", "reader.font.sans")], "font"),
                Setting("reader.lineHeight", [("compact", "reader.lineHeight.compact"), ("normal", "reader.lineHeight.normal"),
                    ("spacious", "reader.lineHeight.spacious")], "lineHeight"),
                Setting("reader.width", [("narrow", "reader.width.narrow"), ("normal", "reader.width.normal"), ("wide", "reader.width.wide")], "width"),
                Setting("reader.theme", [("light", "reader.theme.light"), ("sepia", "reader.theme.sepia"), ("dark", "reader.theme.dark")], "theme"),
                Setting("reader.mode", [("scroll", "reader.mode.scroll"), ("paged", "reader.mode.paged")], "mode"),
                Setting("reader.pages", [("auto", "reader.pages.auto"), ("single", "reader.pages.single"), ("spread", "reader.pages.spread")], "pages")
            ).Class("reader__panel").Attribute("data-panel", "settings").Attribute("hidden", "hidden");
            var languages = new Div([.. new[] { "original" }.Concat(_languages).Select(code => new Button(new ValueText(LanguageName(code))).Class("button")
                .Attribute("data-language-choice", code).Attribute("aria-pressed", code == Chosen(document) ? "true" : "false"))]).Class("reader__panel reader__choices")
                .Attribute("data-panel", "language").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.language"]);

            var navigation = new List<IUiComponent>();
            navigation.Add(index > 0
                ? new A(new Text("reader.previous")).Class("button").Attribute("href", "?s=" + (index - 1).ToString(CultureInfo.InvariantCulture)).Attribute("data-role", "previous")
                : new Span());
            navigation.Add(new Button(new Text("reader.previous")).Class("button reader__page-turn").Attribute("type", "button")
                .Attribute("data-page-step", "-1").Attribute("hidden", "hidden"));
            navigation.Add(new Span(new ValueText(Percent(current.CharactersBefore, current.TotalCharacters))).Class("reader__position").Attribute("data-role", "position"));
            navigation.Add(new Button(new Text("reader.next")).Class("button button--primary reader__page-turn").Attribute("type", "button")
                .Attribute("data-page-step", "1").Attribute("hidden", "hidden"));
            navigation.Add(index + 1 < document.SectionCount
                ? new A(new Text("reader.next")).Class("button button--primary").Attribute("href", "?s=" + (index + 1).ToString(CultureInfo.InvariantCulture)).Attribute("data-role", "next")
                : new Span(new Text("reader.end")));

            var view = new Div(
                top,
                new Button(new Text("reader.restoreControls")).Class("button reader__focus-restore").Attribute("data-action", "restore-focus")
                    .Attribute("hidden", "hidden"),
                contents,
                notes,
                searching,
                settings,
                languages,
                downloads,
                new ReaderSection(current),
                Toolbar(),
                Popover(),
                new P(new Text("progress.saveFailed")).Attribute("data-role", "save-error").Attribute("role", "status").Attribute("hidden", "hidden"),
                new P().Class("reader__note").Attribute("data-role", "translation-status").Attribute("role", "status").Attribute("hidden", "hidden"),
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
                .Attribute("data-locator", document.Locator ?? "")
                .Attribute("data-direction", document.IsRightToLeft ? "rtl" : "ltr")
                .Attribute("data-annotations-url", "/api/v1/reading/assets/" + asset + "/annotations")
                .Attribute("data-annotations", JsonSerializer.Serialize(_marks, Json))
                .Attribute("data-note-label", texts["reader.note"])
                .Attribute("data-query", Query ?? "");
            if (texts.CanTranslate())
            {
                // The section's translations are shared; the files to translate it come with its route.
                // A book in another language opens in the profile's, unless the profile chose another for it.
                view.Attribute("data-language", document.Language ?? document.OriginalLanguage ?? "")
                    .Attribute("data-reading-language", Chosen(document))
                    .Attribute("data-language-url", "/api/v1/reading/assets/" + asset + "/language")
                    .Attribute("data-translation-url", "/api/v1/reading/documents/" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + "/sections/"
                        + index.ToString(CultureInfo.InvariantCulture) + "/translations/")
                    .Attribute("data-route-url", "/api/v1/translation/routes/")
                    .Attribute("data-translating-label", texts["reader.translating"])
                    .Attribute("data-translation-failed-label", texts["reader.translationFailed"]);
            }

            page.Content.Add(view);
            if (!document.IsCached)
            {
                page.Content.Add(new P(new Text("reader.notCached")).Class("reader__note").Attribute("role", "status"));
            }
        }

        // The browser's own PDF viewer; the link serves browsers without one.
        private IUiComponent PdfView(ReadingSourceV1 source)
        {
            var file = "/api/v1/reading/assets/" + Id.ToString(CultureInfo.InvariantCulture) + "/file";
            return new Div(
                new Div(
                    new A(new Text("common.back")).Class("button").Attribute("href", "/Details/" + source.WorkId.ToString(CultureInfo.InvariantCulture)),
                    new Div(new H1(new ValueText(source.Title)).Class("reader__title")).Class("reader__heading"),
                    new A(new Text("reader.openPdf")).Class("button").Attribute("href", file).Attribute("target", "_blank").Attribute("rel", "noopener")
                ).Class("reader__top"),
                _status is { } status ? new P(new Text(status)).Class("reader__note").Attribute("role", "status") : new Span(),
                new Frame().Class("reader__pdf").Attribute("src", file).Attribute("title", source.Title)
            ).Class("reader reader--pdf");
        }

        private sealed class Frame() : HtmlElement("iframe");

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

        // The book's highlights in reading order; each leads to its passage and can go without the script.
        private IUiComponent Notes(int index)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var panel = new List<IUiComponent>();
            if (_notes is not { Items.Count: > 0 } notes)
            {
                // A later empty page leads back.
                panel.Add(NotesPage > 1 ? new A(new Text("common.previous")).Attribute("href", NotesAddress(index, NotesPage - 1)) : new P(new Text("reader.notesEmpty")));
            }
            else
            {
                panel.Add(new Ul([.. notes.Items.Select(note => new Li([
                    new A(new ValueText(note.Quote)).Class("reader__note-quote").Attribute("data-color", note.HighlightColorTypeId.ToString())
                        .Attribute("href", "?s=" + note.SectionIndex.ToString(CultureInfo.InvariantCulture) + "&b=" + note.StartBlock.ToString(CultureInfo.InvariantCulture)),
                    .. note.Note is { } text ? new IUiComponent[] { new P(new ValueText(text)).Class("reader__note-text") } : [],
                    new Form<ReadingAnnotationParametersV1>(form => form.Submit("reader.deleteNote"),
                        "/Read/" + Id.ToString(CultureInfo.InvariantCulture) + "?handler=DeleteNote&s=" + index.ToString(CultureInfo.InvariantCulture) + "&noteId=" + note.Id.ToString(CultureInfo.InvariantCulture), csrf,
                        new ReadingAnnotationParametersV1(Id, note.Id))]).Class("reader__note"))]).Class("reader__notes"));
                var paging = new List<IUiComponent>();
                if (notes.Page > 1) paging.Add(new A(new Text("common.previous")).Attribute("href", NotesAddress(index, notes.Page - 1)));
                if (notes.Items.Count == notes.PageSize) paging.Add(new A(new Text("common.next")).Attribute("href", NotesAddress(index, notes.Page + 1)));
                if (paging.Count > 0) panel.Add(new Nav([.. paging]).Attribute("aria-label", texts["reader.notesPages"]));
            }

            var div = new Div([.. panel]).Class("reader__panel").Attribute("data-panel", "notes").Attribute("aria-label", texts["reader.notes"]);
            return NotesPage > 1 ? div : div.Attribute("hidden", "hidden");
        }

        // Sections with the words; the script marks them in the opened section.
        private IUiComponent Search(int index)
        {
            var content = new List<IUiComponent> { new SearchBox("/Read/" + Id.ToString(CultureInfo.InvariantCulture), "reader.search", Query, new Dictionary<string, string> { ["s"] = index.ToString(CultureInfo.InvariantCulture) }) };
            if (_hits is { } hits)
            {
                content.Add(hits.Count == 0
                    ? new P(new Text("reader.searchEmpty"))
                    : new Ul([.. hits.Select(hit => new Li(
                        new A(new ValueText(hit.Title ?? (hit.SectionIndex + 1).ToString(CultureInfo.InvariantCulture))).Attribute("href", "?s=" + hit.SectionIndex.ToString(CultureInfo.InvariantCulture)
                            + "&q=" + Uri.EscapeDataString(Query!)),
                        new P(new ValueText(hit.Snippet)).Class("reader__note-text reader__snippet")).Class("reader__note"))]).Class("reader__notes"));
                if (hits.Count == 50) content.Add(new P(new Text("reader.searchMore")));
            }

            var div = new Div([.. content]).Class("reader__panel").Attribute("data-panel", "search").Attribute("aria-label", texts["reader.search"]);
            return _hits is null ? div.Attribute("hidden", "hidden") : div;
        }

        // Shown by the script: colours for a selection, and a note for a highlight.
        private static IUiComponent[] Colors() => [.. Enum.GetValues<HighlightColorType>().Select(color => new Button(new Text("reader.highlight." + color)).Class("button reader__color")
            .Attribute("data-color", ((byte)color).ToString(CultureInfo.InvariantCulture)))];

        private static IUiComponent Toolbar() => new Div([.. Colors(), new Button(new Text("reader.note")).Attribute("data-role", "add-note")])
            .Class("reader__toolbar").Attribute("data-role", "toolbar").Attribute("hidden", "hidden");

        private IUiComponent Popover() => new Div(
            new TextArea().Attribute("aria-label", texts["reader.note"]).Attribute("maxlength", "4000").Attribute("rows", "4"),
            new Div([.. Colors()]).Class("reader__choices"),
            new Div(new Button(new Text("reader.saveNote")).Primary().Attribute("data-role", "save-note"), new Button(new Text("reader.deleteNote")).Attribute("data-role", "delete-note"))
                .Class("reader__choices")
        ).Class("reader__popover").Attribute("data-role", "popover").Attribute("hidden", "hidden");

        private static string NotesAddress(int section, int page) => "?s=" + section.ToString(CultureInfo.InvariantCulture) + "&n=" + page.ToString(CultureInfo.InvariantCulture);

        public async Task<IActionResult> OnPostDeleteNoteAsync([BindRequired] long noteId)
        {
            await deleteNote.ExecuteAsync<UpdateResult>(new ReadingAnnotationParametersV1(Id, noteId), default, HttpContext.RequestAborted);
            return LocalRedirect("/Read/" + Id.ToString(CultureInfo.InvariantCulture) + "?s=" + (SectionIndex ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        private static string PagePath(int index) => ":page/" + index.ToString("D5", CultureInfo.InvariantCulture);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // Mozilla's list may be out of reach; then the book reads in its own language.
        private async Task<string[]> LanguagesAsync(CancellationToken ct)
        {
            try
            {
                var targets = (await readLanguages.ExecuteAsync<TranslationLanguagesV1>(new TranslationLanguagesParametersV1(), ct)).Targets;
                var allowed = (await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(new ReadUiLocalesParametersV1(), ct))
                    .Select(locale => CultureInfo.GetCultureInfo(locale.Locale).TwoLetterISOLanguageName);
                return [.. new[] { ProfileLanguage }.Concat(allowed).Distinct(StringComparer.Ordinal).Where(targets.Contains)];
            }
            catch (ServiceUnavailableException)
            {
                return [];
            }
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _source = await readSource.ExecuteAsync<ReadingSourceV1>(new ReadingAssetParametersV1(Id), ct);
            try
            {
                _document = await open.ExecuteAsync<ReadingDocumentV1>(new ReadingAssetParametersV1(Id), default, ct);
            }
            catch (ServiceUnavailableException exception) when (_source.ReadingFormatTypeId == ReadingFormatType.Pdf
                && exception.Code is "reading_pdf_without_text" or "reading_invalid_file" or "reading_pdf_recognizing")
            {
                // The browser may still show what Jularr cannot reflow, or not yet.
                _status = exception.Code == "reading_pdf_recognizing" ? "errors." + exception.Code : null;
                return;
            }

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
            if (texts.CanTranslate())
            {
                _languages = await LanguagesAsync(ct);
            }
            _toc = await toc.ExecuteAsync<BaseDataList<ReadingTocEntryV1>>(new ReadingDocumentParametersV1(_document.DocumentId), ct);
            _marks = await readMarks.ExecuteAsync<BaseDataList<ReadingAnnotationV1>>(new ReadingSectionAnnotationsParametersV1(Id, index), ct);
            _notes = await readNotes.ExecuteAsync<PageResult<ReadingAnnotationV1>>(new ReadingAnnotationsParametersV1(Id, Math.Max(1, NotesPage)), ct);
            if (Query is { Length: > 0 })
            {
                _hits = await search.ExecuteAsync<BaseDataList<ReadingSearchHitV1>>(new ReadingSearchParametersV1(_document.DocumentId, Query), ct);
            }
        }

        private string ProfileLanguage => CultureInfo.GetCultureInfo(texts.Locale).TwoLetterISOLanguageName;

        // The profile's choice for the book, else its language when Mozilla reaches it, else the original.
        private string Chosen(ReadingDocumentV1 document) => document.ReadingLanguage is { } chosen && (chosen == "original" || _languages.Contains(chosen)) ? chosen
            : _languages.Contains(ProfileLanguage) ? ProfileLanguage : "original";

        private string LanguageName(string code) => code == "original" ? texts["reader.translate.original"]
            : CultureInfo.GetCultureInfo(code) is { } culture ? culture.TextInfo.ToTitleCase(culture.NativeName) : code;

        private IUiComponent Setting(string labelKey, (string Value, string LabelKey)[] options, string name) =>
            new Div(new Span(new Text(labelKey)), new Div([.. options.Select(option => new Button(new Text(option.LabelKey)).Class("button")
                .Attribute("data-setting", name).Attribute("data-value", option.Value))]).Class("reader__choices")).Class("reader__setting");

        private static string Percent(long before, long total) =>
            total <= 0 ? "" : Math.Clamp((int)Math.Round(before * 100d / total), 0, 100).ToString(CultureInfo.InvariantCulture) + " %";
    }
}
