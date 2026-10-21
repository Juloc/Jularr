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
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Reader;
using Jularr.Frontend.Modules.Speech;
using Jularr.Backend.Data.Enums.Speech;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Service.User.Speech.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Reader
{
    public sealed class ReaderModel(ReadReadingSourceV1 readSource, OpenReadingV1 open, ReadReadingTocV1 toc, ReadReadingSectionV1 section, ReadReadingSectionAnnotationsV1 readMarks, ReadReadingAnnotationsV1 readNotes,
        DeleteReadingAnnotationV1 deleteNote, SearchReadingV1 search, ReadUiLocalesV1 readLocales, ReadTranslationLanguagesV1 readLanguages, ReadWorkV1 readWork,
        ReadSpeechPreferencesV1 readSpeech, SaveSpeechPreferencesV1 saveSpeech, ReadAvailableSpeechModelsV1 readSpeechModels, ReadAuthenticationV1 authentication, IAntiforgery antiforgery, UiText texts, FrontendAssets assets,
        ReadReadingStateV1 readState, ReadReadingPositionV1 readPosition, ReadReadingPageNotesV1 readPageNotes, CreateReadingPageNoteV1 createPageNote, DeleteReadingPageNoteV1 deletePageNote)
        : JularrPage
    {
        // The languages this book can be read in: its original first, then the profile's and the other allowed ones Mozilla's models reach.
        private string[] _languages = [];

        // A PDF without reading text, or whose reading text the profile did not ask for, opens as its original pages and says why, here, in a word.
        private string? _status;

        // The original pages show; the page they open at, the book's state and the profile's notes on its pages.
        private bool _original;
        private int _page = 1;
        private ReadingStateV1? _state;
        private IReadOnlyList<ReadingPageNoteV1> _pageNotes = [];

        // Where the Smart Book opens when it was reached from a page of the original.
        private int? _startBlock;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private ReadingSourceV1? _source;
        private WorkDetailsV1? _work;
        private ReadingDocumentV1? _document;
        private ReadingSectionV1? _section;
        private IReadOnlyList<ReadingTocEntryV1> _toc = [];
        private IReadOnlyList<ReadingAnnotationV1> _marks = [];
        private PageResult<ReadingAnnotationV1>? _notes;
        private IReadOnlyList<ReadingSearchHitV1>? _hits;
        private SpeechPreferencesV1? _speech;
        private SaveSpeechPreferencesDataV1? _speechInput;
        private IReadOnlyList<SpeechModelV1> _speechModels = [];

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty(SupportsGet = true, Name = "s")]
        public int? SectionIndex { get; set; }

        [BindProperty(SupportsGet = true, Name = "p")]
        public int? PageIndex { get; set; }

        // A page of the PDF to open at (1 is the first), and the view to open in for this visit: smart or original.
        [BindProperty(SupportsGet = true, Name = "pg")]
        public int? PdfPage { get; set; }

        [BindProperty(SupportsGet = true, Name = "view")]
        public string? ViewName { get; set; }

        [BindProperty(SupportsGet = true, Name = "n")]
        public int NotesPage { get; set; } = 1;

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Query { get; set; }

        protected override void Define(Page page)
        {
            page.Title("reader.title", _source?.Title);
            page.IsReader = true;
            // The original pages: by choice, where there is no reading text, or where it is not good enough to open by itself.
            if (_original && _source is { } original)
            {
                page.Asset("Modules/Reader/reader.entry.ts");
                page.Content.Add(OriginalView(original));
                return;
            }

            if (_document is { ReadingFormatTypeId: ReadingFormatType.Comic } comic)
            {
                page.Asset("Modules/Reader/reader.entry.ts");
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
            var top = Header(document.Title, current.Title ?? "", document.WorkId, [
                _languages.Length > 0 ? Tool("globe", "reader.language", "language") : new Span(),
                AppearanceTool(false),
                new Button(new Icon("palette", assets)).IconOnly(texts["reader.settings.paper"]).Attribute("data-reader-open-settings", "paper").Attribute("aria-expanded", "false").Class("button button--icon reader__desktop-tool"),
                Tool("bookmark", "reader.notes", "notes").Class("button button--icon reader__desktop-tool"),
                new Button(new Icon("headphones", assets)).IconOnly(texts["speech.title"]).Attribute("data-speech-toggle", "").Attribute("aria-pressed", "false").Attribute("disabled", "disabled"),
                Tool("search", "reader.search", "search").Class("button button--icon reader__desktop-tool"),
                new Button(new Icon("text-select", assets)).IconOnly(texts["reader.selectText"]).Attribute("data-reader-select", "").Attribute("aria-pressed", "false"),
                Tool("player-more", "reader.more", "more")
            ], Chapters(index, document.SectionCount));

            // A converted PDF downloads as EPUB beside the original; a finished translation as its own EPUB.
            var ebook = "/api/v1/reading/documents/" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + "/ebook";
            var originalPdf = new A(new Text("reader.originalPdf")).Class("button").Attribute("href", "/api/v1/reading/assets/" + asset + "/file").Attribute("target", "_blank").Attribute("rel", "noopener");
            IUiComponent[] converted = _source?.ReadingFormatTypeId == ReadingFormatType.Pdf
                ? [new A(new Text("reader.download.epub")).Class("button").Attribute("href", ebook).Attribute("download", ""), originalPdf]
                : _source?.HasPdf == true ? [originalPdf] : [];
            var downloads = new Div([
                .. converted,
                .. document.TranslatedLanguages.Select(code => new A(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["reader.download.translated"], LanguageName(code)))).Class("button")
                    .Attribute("href", ebook + "?language=" + Uri.EscapeDataString(code)).Attribute("download", "")),
                // Pages only the original shows are not in the EPUB, and it says so before it is downloaded.
                .. document.OriginalPages > 0 && _source?.ReadingFormatTypeId == ReadingFormatType.Pdf
                    ? [new P(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["reader.download.omitted"], document.OriginalPages)))]
                    : Array.Empty<IUiComponent>()
            ]).Class("reader__panel reader__choices").Attribute("data-panel", "download").Attribute("hidden", "hidden");

            // Contents and settings share one panel area.
            var contents = new Nav([.. _toc.Select(entry => new A(new ValueText(entry.Title)).Class("reader__toc-entry" + (entry.SectionIndex == index ? " is-current" : ""))
                .Attribute("href", "?s=" + entry.SectionIndex.ToString(CultureInfo.InvariantCulture) + (entry.Anchor is null ? "" : "#" + Uri.EscapeDataString(entry.Anchor)))
                .Attribute("data-level", entry.Level.ToString(CultureInfo.InvariantCulture)))]).Class("reader__panel").Attribute("data-panel", "contents").Attribute("hidden", "hidden")
                .Attribute("aria-label", texts["reader.contents"]);
            var notes = Notes(index);
            var searching = Search(index);
            var settings = Settings(false, _source?.HasPdf == true);
            var languages = new Div([.. new[] { "original" }.Concat(_languages).Select(code => new Button(new ValueText(LanguageName(code))).Class("button")
                .Attribute("data-language-choice", code).Attribute("aria-pressed", code == Chosen(document) ? "true" : "false"))]).Class("reader__panel reader__choices")
                .Attribute("data-panel", "language").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.language"]);

            var navigation = new List<IUiComponent>();
            navigation.Add(new Span(new ValueText(Percent(current.CharactersBefore, current.TotalCharacters))).Class("reader__position").Attribute("data-role", "position"));
            navigation.Insert(0, Seek(false, current.Title));
            var view = new ReaderFrame(assets, texts,
                top, contents, new ReaderSection(current), new Div([.. navigation]).Class("reader__bottom"),
                notes,
                searching,
                settings,
                languages,
                downloads,
                SpeechPanel(document, index),
                SpeechTransport(),
                More(true, converted.Length > 0 || document.TranslatedLanguages.Length > 0),
                Toolbar(),
                Popover(),
                FigureViewer(),
                new P(new Text("progress.saveFailed")).Attribute("data-role", "save-error").Attribute("role", "status").Attribute("hidden", "hidden"),
                new P().Class("reader__note").Attribute("data-role", "translation-status").Attribute("role", "status").Attribute("hidden", "hidden")
            ).Class("reader")
                .Attribute("data-progress-url", "/api/v1/reading/assets/" + asset + "/progress")
                .Attribute("data-antiforgery", antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "")
                .Attribute("data-section", index.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-sections", document.SectionCount.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-sections-url", "/api/v1/reading/documents/" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + "/sections/")
                .Attribute("data-blocks", current.Blocks.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-before", current.CharactersBefore.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-characters", current.Characters.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-total", current.TotalCharacters.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-locator", document.Locator ?? "")
                .Attribute("data-annotations-url", "/api/v1/reading/assets/" + asset + "/annotations")
                .Attribute("data-annotations", JsonSerializer.Serialize(_marks, Json))
                .Attribute("data-note-label", texts["reader.note"])
                .Attribute("data-query", Query ?? "")
                .Attribute("data-page-label", texts["reader.pagePosition"])
                .Attribute("data-animation-failed", texts["reader.animationUnavailable"])
                .Attribute("data-figure-label", texts["reader.figure.open"]);
            if (_source?.HasPdf == true)
            {
                // A page that only the original shows is drawn from the PDF in its place and links to its page in the original pages; the view can be switched from here.
                view.Attribute("data-file-url", "/api/v1/reading/assets/" + asset + "/file")
                    .Attribute("data-original-url", "/Read/" + asset + "?view=original")
                    .Attribute("data-original-label", texts["reader.figure.original"])
                    .Attribute("data-original-link", texts["reader.figure.originalPages"])
                    .Attribute("data-original-alt", texts["reader.figure.originalAlt"])
                    .Attribute("data-pdf-from", current.PageFrom.ToString(CultureInfo.InvariantCulture))
                    .Attribute("data-view-url", "/api/v1/reading/assets/" + asset + "/view")
                    .Attribute("data-view-chosen", ViewChoiceName(_source.ReadingViewTypeId));
                if (_startBlock is { } startBlock)
                {
                    view.Attribute("data-start-block", startBlock.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (_speech is not null)
            {
                view.Attribute("data-speech-preferences", JsonSerializer.Serialize(_speech, Json))
                    .Attribute("data-speech-language", SpeechLanguage(document))
                    .Attribute("data-speech-original-language", document.Language ?? document.OriginalLanguage ?? texts.Locale)
                    .TextAttribute("data-speech-translation-label", "speech.translationPending")
                    .TextAttribute("data-speech-unavailable-label", "speech.unavailable")
                    .TextAttribute("data-speech-failed-label", "speech.failed")
                    .TextAttribute("data-speech-loading-label", "speech.loading")
                    .TextAttribute("data-speech-speaking-label", "speech.speaking")
                    .TextAttribute("data-speech-paused-label", "speech.paused")
                    .TextAttribute("data-speech-pause-label", "speech.pause")
                    .TextAttribute("data-speech-resume-label", "speech.resume");
            }
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
            else if (SmartNote() is { } note)
            {
                page.Content.Add(new P(new Text(note)).Class("reader__note").Attribute("role", "status"));
            }
        }

        // The original pages of the PDF, drawn by the browser inside the same frame, settings and progress as every book. They need no analysis: the file is all they use.
        private IUiComponent OriginalView(ReadingSourceV1 source)
        {
            var asset = Id.ToString(CultureInfo.InvariantCulture);
            var file = "/api/v1/reading/assets/" + asset + "/file";
            var top = Header(source.Title, "", source.WorkId, [
                Tool("bookmark", "reader.notes", "notes").Class("button button--icon reader__desktop-tool"),
                new Button(new Icon("settings", assets)).IconOnly(texts["reader.settings"]).Attribute("data-reader-open-settings", "all").Attribute("aria-expanded", "false"),
                Tool("player-more", "reader.more", "more")
            ]);
            var stage = new Div(
                new Div().Class("reader__sheet").Attribute("data-role", "sheet"),
                new P(new Text("reader.original.loading")).Class("reader__original-status").Attribute("data-role", "status").Attribute("role", "status"),
                new Div(
                    new Button(new Icon("zoom-out", assets)).IconOnly(texts["reader.figure.zoomOut"]).Attribute("data-page-zoom", "out"),
                    new Button(new Icon("zoom-in", assets)).IconOnly(texts["reader.figure.zoomIn"]).Attribute("data-page-zoom", "in"),
                    new Button(new Icon("fit", assets)).IconOnly(texts["reader.figure.fit"]).Attribute("data-page-zoom", "fit")
                ).Class("reader__zoom").Attribute("role", "group").Attribute("aria-label", texts["reader.original.zoom"])
            ).Class("reader__content reader__original").Attribute("data-role", "stage");
            var bottom = new Div(Seek(true), new Span().Class("reader__position").Attribute("data-role", "position").Attribute("aria-live", "polite")).Class("reader__bottom");

            // What the book says about itself, quietly: why the original pages show, how far a scan is read, and the offer of a better view.
            var recognition = _state is { IsRecognizing: true, RecognitionPages: > 0 } state ? RecognitionText(state) : null;
            var progress = new P(new ValueText(recognition ?? "")).Class("reader__notice").Attribute("data-role", "recognition").Attribute("role", "status");
            if (recognition is null)
            {
                progress.Attribute("hidden", "hidden");
            }

            var notices = new Div(
                progress,
                (_status ?? SmartNote()) is { } note ? new P(new Text(note)).Class("reader__notice").Attribute("role", "status") : new Span(),
                new Div(
                    new P(new Text("reader.offer.smart")),
                    new Button(new Text("reader.offer.open")).Primary().Attribute("data-view-offer-open", ""),
                    new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("data-view-offer-dismiss", "")
                ).Class("reader__offer").Attribute("data-role", "view-offer").Attribute("role", "status").Attribute("hidden", "hidden")
            ).Class("reader__notices");

            return new ReaderFrame(assets, texts, top, new Span(), stage, bottom, Settings(true, true, true), PageNotes(), notices,
                More(false, false, new A(new Icon("download", assets), new Text("reader.originalPdf")).Class("button reader__menu-item").Attribute("href", file).Attribute("target", "_blank").Attribute("rel", "noopener"), notes: true),
                new P(new Text("progress.saveFailed")).Attribute("data-role", "save-error").Attribute("role", "status").Attribute("hidden", "hidden"))
                .Class("reader reader--pages").Attribute("data-format", "pages")
                .Attribute("data-progress-url", "/api/v1/reading/assets/" + asset + "/progress")
                .Attribute("data-antiforgery", antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "")
                .Attribute("data-file-url", file)
                .Attribute("data-page", _page.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-page-label", texts["reader.original.page"])
                .Attribute("data-failed-label", texts["reader.original.failed"])
                .Attribute("data-state-url", "/api/v1/reading/assets/" + asset + "/state")
                .Attribute("data-view-url", "/api/v1/reading/assets/" + asset + "/view")
                .Attribute("data-view-chosen", ViewChoiceName(source.ReadingViewTypeId))
                .Attribute("data-recognition-label", texts["reader.recognition.progress"]);
        }

        private string RecognitionText(ReadingStateV1 state) => string.Format(CultureInfo.InvariantCulture, texts["reader.recognition.progress"], state.RecognizedPages, state.RecognitionPages);

        // The profile's choice as the page's script names it.
        private static string ViewChoiceName(ReadingView? view) => view switch { ReadingView.Smart => "smart", ReadingView.Original => "original", _ => "auto" };

        // This book's notes on pages: the one for the page in view, and the others, each leading to its page.
        private IUiComponent PageNotes()
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var asset = Id.ToString(CultureInfo.InvariantCulture);
            var content = new List<IUiComponent>
            {
                new Form<CreateReadingPageNoteDataV1>(form => form.Hidden(data => data.PageNumber).Hidden(data => data.Anchor).Multiline(data => data.Note, "reader.pageNote.label").Submit("reader.pageNote.save"),
                    "/Read/" + asset + "?handler=PageNote", csrf, new CreateReadingPageNoteDataV1(_page, null, ""))
            };
            if (_pageNotes.Count > 0)
            {
                content.Add(new Ul([.. _pageNotes.Select(note => new Li([
                    new A(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["reader.pageNote.page"], note.PageNumber))).Class("reader__note-quote")
                        .Attribute("href", "/Read/" + asset + "?view=original&pg=" + note.PageNumber.ToString(CultureInfo.InvariantCulture)),
                    new P(new ValueText(note.Note)).Class("reader__note-text"),
                    new Form<ReadingPageNoteParametersV1>(form => form.Submit("reader.deleteNote"),
                        "/Read/" + asset + "?handler=DeletePageNote&pg=" + _page.ToString(CultureInfo.InvariantCulture) + "&noteId=" + note.Id.ToString(CultureInfo.InvariantCulture), csrf,
                        new ReadingPageNoteParametersV1(Id, note.Id))]).Class("reader__note"))]).Class("reader__notes"));
            }

            return new Div([.. content]).Class("reader__panel").Attribute("data-panel", "notes").Attribute("aria-label", texts["reader.notes"]).Attribute("hidden", "hidden");
        }

        public async Task<IActionResult> OnPostPageNoteAsync([BindRequired] int pageNumber, string? anchor, [BindRequired] string note)
        {
            await createPageNote.ExecuteAsync<CreateReadingPageNoteResultV1>(new ReadingAssetParametersV1(Id), new CreateReadingPageNoteDataV1(pageNumber, anchor, note), HttpContext.RequestAborted);
            return LocalRedirect("/Read/" + Id.ToString(CultureInfo.InvariantCulture) + "?view=original&pg=" + pageNumber.ToString(CultureInfo.InvariantCulture));
        }

        public async Task<IActionResult> OnPostDeletePageNoteAsync([BindRequired] long noteId)
        {
            await deletePageNote.ExecuteAsync<UpdateResult>(new ReadingPageNoteParametersV1(Id, noteId), default, HttpContext.RequestAborted);
            return LocalRedirect("/Read/" + Id.ToString(CultureInfo.InvariantCulture) + "?view=original&pg=" + (PdfPage ?? 1).ToString(CultureInfo.InvariantCulture));
        }

        // Where the PDF of this book stands on its way to a reading copy, when the module is on and it matters to the reader.
        private string? SmartNote() => _source?.ReadingDerivationStatusTypeId switch
        {
            ReadingDerivationStatus.Preparing => "reader.smart.preparing",
            ReadingDerivationStatus.Failed => _source.Reason == "offline" ? "reader.smart.preparing" : "reader.smart.failed",
            _ => null
        };

        private sealed class Dialog(params IUiComponent[] children) : HtmlElement("dialog", children);

        // The full-screen view of a figure; the script fills it with the picture that was tapped.
        private IUiComponent FigureViewer() => new Dialog(
            new Div(
                new Button(new Icon("zoom-out", assets)).IconOnly(texts["reader.figure.zoomOut"]).Attribute("data-figure-zoom", "out"),
                new Button(new Icon("zoom-in", assets)).IconOnly(texts["reader.figure.zoomIn"]).Attribute("data-figure-zoom", "in"),
                new Button(new Icon("fit", assets)).IconOnly(texts["reader.figure.fit"]).Attribute("data-figure-zoom", "fit"),
                new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("data-figure-close", "")
            ).Class("reader-figure__tools"),
            new Div(new Img().Attribute("alt", "").Class("reader-figure__image")).Class("reader-figure__stage")
        ).Class("reader-figure").Attribute("aria-label", texts["reader.figure.view"]);

        // One page at a time with links that work without the script; the script adds spreads, direction and preloading.
        private IUiComponent ComicView(ReadingDocumentV1 document)
        {
            var count = Math.Max(1, document.SectionCount);
            var index = Math.Clamp(PageIndex ?? (document.Locator is { } locator && locator.StartsWith('p')
                && int.TryParse(locator[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var saved) ? saved : 0), 0, count - 1);
            var pages = "/api/v1/reading/documents/" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + "/resource?path=";
            string Page(int number) => "?p=" + number.ToString(CultureInfo.InvariantCulture);
            var top = Header(document.Title, "", document.WorkId, [AppearanceTool(true), Tool("player-more", "reader.more", "more")]);
            var previous = new A(new Icon("player-previous", assets)).Class("button button--icon").Attribute("aria-label", texts["reader.previous"]).Attribute("href", Page(Math.Max(0, index - 1))).Attribute("data-role", "previous");
            var next = new A(new Icon("player-next", assets)).Class("button button--icon").Attribute("aria-label", texts["reader.next"]).Attribute("href", Page(Math.Min(count - 1, index + 1))).Attribute("data-role", "next");
            if (index == 0) previous.Attribute("hidden", "hidden");
            if (index + 1 >= count) next.Attribute("hidden", "hidden");
            var bottom = new Div(
                previous,
                Seek(true),
                new Span(new ValueText((index + 1).ToString(CultureInfo.InvariantCulture) + " / " + count.ToString(CultureInfo.InvariantCulture))).Class("reader__position")
                    .Attribute("data-role", "position").Attribute("aria-live", "polite"),
                next
            ).Class("reader__bottom");
            var stage = new Div(new Img().Attribute("src", pages + Uri.EscapeDataString(PagePath(index))).Attribute("alt", texts["reader.comic.page"] + " " + (index + 1).ToString(CultureInfo.InvariantCulture)))
                .Class("reader__content reader__images").Attribute("data-role", "stage");

            // Bounded page links; the complete image list is never embedded in the response.
            var start = Math.Max(0, index - 10);
            var contents = new Nav([.. Enumerable.Range(start, Math.Min(20, count - start)).Select(number => new A(new ValueText((number + 1).ToString(CultureInfo.InvariantCulture)))
                .Class("reader__toc-entry" + (number == index ? " is-current" : "")).Attribute("href", Page(number)))])
                .Class("reader__panel").Attribute("data-panel", "contents").Attribute("aria-label", texts["reader.contents"]);

            var note = document.IsCached ? (IUiComponent)new Span() : new P(new Text("reader.notCached")).Class("reader__note").Attribute("role", "status");
            return new ReaderFrame(assets, texts, top, contents, stage, bottom, Settings(true), More(false, false),
                new P(new Text("progress.saveFailed")).Attribute("data-role", "save-error").Attribute("role", "status").Attribute("hidden", "hidden"), note)
                .Class("reader").Attribute("data-format", "comic")
                .Attribute("data-progress-url", "/api/v1/reading/assets/" + Id.ToString(CultureInfo.InvariantCulture) + "/progress")
                .Attribute("data-antiforgery", antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "")
                .Attribute("data-pages", pages)
                .Attribute("data-page", index.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-count", count.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-direction", document.IsRightToLeft ? "rtl" : "ltr")
                .Attribute("data-page-label", texts["reader.comic.page"])
                .Attribute("data-animation-failed", texts["reader.animationUnavailable"]);
        }

        private static IUiComponent Seek(bool images, string? chapter = null) => new Div(
            chapter is { Length: > 0 } ? new Span(new ValueText(chapter)).Class("reader__seek-caption") : new Span(),
            new Form<ReaderPositionData>(form => form.Range(data => data.Position, images ? "reader.seek.pages" : "reader.seek.chapter"), data: new ReaderPositionData(), local: true))
                .Class("reader__seek").Attribute("data-role", "seek").Attribute("hidden", "hidden");

        private IUiComponent Header(string title, string chapter, long workId, IUiComponent[] tools, IUiComponent? chapters = null) => new Header(
            new Div(
                new A(new Icon("chevron-left", assets)).Class("button button--icon").Attribute("aria-label", texts["common.back"])
                    .Attribute("href", "/Details/" + workId.ToString(CultureInfo.InvariantCulture)),
                new A(_work?.PosterUrl is { } cover
                    ? new MediaImage(cover, "", 28, 40, priority: true)
                    : new Icon("reading", assets)).Class("reader__cover").Attribute("href", "/Details/" + workId.ToString(CultureInfo.InvariantCulture))
                        .Attribute("aria-label", title),
                new Div(new H1(new ValueText(title)).Class("reader__title").Attribute("id", "reader-document-title"), new P(new ValueText(chapter)).Class("reader__chapter")).Class("reader__heading")).Class("reader__identity"),
            chapters ?? new Span(), new Nav(tools).Class("reader__tools"))
                .Class("reader__top");

        private HtmlElement Tool(string icon, string key, string action) => new Button(new Icon(icon, assets)).IconOnly(texts[key])
            .Attribute("data-action", action).Attribute("aria-expanded", "false");

        private IUiComponent AppearanceTool(bool images) => new Button(new Span(new ValueText("Aa")).Class("reader__type-icon"))
            .IconOnly(texts["reader.appearance"]).Attribute("data-reader-open-settings", images ? "all" : "text").Attribute("aria-expanded", "false");

        private IUiComponent Chapters(int index, int count) => new Nav(
            index > 0 ? new A(new Icon("chevron-left", assets)).Class("button button--icon").Attribute("aria-label", texts["reader.previous"])
                .Attribute("href", "?s=" + (index - 1).ToString(CultureInfo.InvariantCulture)).Attribute("data-role", "previous") : new Span(),
            new Span(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["reader.chapterPosition"], index + 1, count))),
            index + 1 < count ? new A(new Icon("chevron-left", assets)).Class("button button--icon reader__turn-next").Attribute("aria-label", texts["reader.next"])
                .Attribute("href", "?s=" + (index + 1).ToString(CultureInfo.InvariantCulture)).Attribute("data-role", "next") : new Span())
            .Class("reader__chapter-navigation").Attribute("aria-label", texts["reader.chapterNavigation"]);

        private IUiComponent More(bool text, bool download, IUiComponent? extra = null, bool notes = false) => new Div(
            new Div(new H2(new Text("reader.more")), new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("data-reader-close", "more")).Class("reader__panel-heading"),
            text ? new Button(new Icon("search", assets), new Text("reader.search")).Class("button reader__menu-item reader__mobile-item").Attribute("data-reader-open-panel", "search").Attribute("aria-expanded", "false") : new Span(),
            text || notes ? new Button(new Icon("bookmark", assets), new Text("reader.notes")).Class("button reader__menu-item reader__mobile-item").Attribute("data-reader-open-panel", "notes").Attribute("aria-expanded", "false") : new Span(),
            text ? new Button(new Icon("headphones", assets), new Text("speech.settings")).Class("button reader__menu-item").Attribute("data-reader-open-panel", "speech").Attribute("aria-expanded", "false") : new Span(),
            new Button(new Icon("settings", assets), new Text("reader.settings")).Class("button reader__menu-item").Attribute("data-reader-open-settings", "all").Attribute("aria-expanded", "false"),
            new Button(new Icon("player-theater", assets), new Text("reader.theater")).Class("button reader__menu-item reader__mobile-item").Attribute("data-reader-command", "theater").Attribute("aria-pressed", "false"),
            new Button(new Icon("player-fullscreen", assets), new Text("player.fullscreen")).Class("button reader__menu-item reader__mobile-item").Attribute("data-reader-command", "fullscreen").Attribute("aria-pressed", "false"),
            download ? new Button(new Icon("download", assets), new Text("reader.download")).Class("button reader__menu-item").Attribute("data-action", "download").Attribute("aria-expanded", "false") : new Span(),
            extra ?? new Span())
            .Class("reader__panel reader__more").Attribute("data-panel", "more").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.more"]);

        // The view switch of a book with a PDF: the profile's choice, kept on all its devices.
        private IUiComponent ViewGroup() => SettingsGroup("view", "reader.settings.view", group => group.Content(new Div(
            new Button(new Text("reader.view.auto")).Class("button").Attribute("data-view-choice", "auto"),
            new Button(new Text("reader.view.smart")).Class("button").Attribute("data-view-choice", "smart"),
            new Button(new Text("reader.view.original")).Class("button").Attribute("data-view-choice", "original"),
            new P(new Text("reader.view.failed")).Attribute("data-role", "view-error").Attribute("role", "status").Attribute("hidden", "hidden")
        ).Class("reader__choices")), open: true);

        private IUiComponent Settings(bool images, bool hasPdf = false, bool pages = false) => new Div(
            new Div(new H2(new Text("reader.settings")), new Button(new Icon("close", assets)).IconOnly(texts["common.close"])
                .Attribute("data-reader-close", "settings")).Class("reader__panel-heading"),
            hasPdf ? ViewGroup() : new Span(),
            pages ? SettingsGroup("pages", "reader.settings.pages", group => group
                .Select(data => data.Fit, "reader.original.fit", [new("page", texts["reader.original.fit.page"]), new("width", texts["reader.original.fit.width"])]), open: true)
            : SettingsGroup("navigation", "reader.settings.navigation", group => group
                .Select(data => data.Preset, "reader.preset", [new("book", texts["reader.preset.book"]), new("novel", texts["reader.preset.novel"]), new("manga", texts["reader.preset.manga"])])
                .Select(data => data.Mode, "reader.mode", [new("paged", texts["reader.mode.paged"]), new("scroll", texts["reader.mode.scroll"])])
                .Select(data => data.Layout, "reader.layout", [new("auto", texts["reader.layout.auto"]), new("single", texts["reader.layout.single"]), new("spread", texts["reader.layout.spread"])])
                .Select(data => data.Direction, "reader.comic.direction", [new("ltr", texts["reader.direction.ltr"]), new("rtl", texts["reader.direction.rtl"])]), open: true),
            images ? new Span() : SettingsGroup("text", "reader.settings.text", group => group
                .Select(data => data.FontFamily, "reader.font", [new("serif", texts["reader.font.serif"]), new("sans", texts["reader.font.sans"])])
                .Range(data => data.Size, "reader.sizePixels").Range(data => data.Spacing, "reader.spacing").Range(data => data.TextWidth, "reader.textWidth")
                .Toggle(data => data.Indent, "reader.indent").Toggle(data => data.Hyphenation, "reader.hyphenation").Toggle(data => data.Illustrations, "reader.illustrations")
                .Content(new P().Attribute("data-role", "book-pages").Attribute("role", "status").Attribute("hidden", "hidden")
                    .TextAttribute("data-calculating-label", "reader.bookPages.calculating").TextAttribute("data-paused-label", "reader.bookPages.paused")
                    .TextAttribute("data-ready-label", "reader.bookPages.ready").TextAttribute("data-unavailable-label", "reader.bookPages.unavailable"))),
            pages ? new Span() : SettingsGroup("paper", "reader.settings.paper", group => group
                .Select(data => data.Surface, "reader.surface", [new("clean", texts["reader.surface.clean"]), new("paper", texts["reader.surface.paper"])])
                .Color(data => data.Colour, "reader.colour")),
            pages ? new Span() : SettingsGroup("motion", "reader.settings.motion", group => group
                .Select(data => data.Animation, "reader.animation", [new("system", texts["reader.animation.system"]), new("on", texts["reader.animation.on"]), new("off", texts["reader.animation.off"])])
                .Content(new P(new Text("reader.animation.hint")))))
            .Class("reader__panel").Attribute("data-panel", "settings").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.settings"]);

        private static IUiComponent SettingsGroup(string name, string title, Action<Form<ReaderSettingsData>> define, bool open = false)
        {
            var group = new Jularr.Frontend.Framework.Details(new Summary(new Text(title)), new Form<ReaderSettingsData>(define, data: new ReaderSettingsData(), local: true))
                .Class("reader__settings-group").Attribute("data-reader-group", name);
            return open ? group.Attribute("open", "open") : group;
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
            _work = await readWork.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(_source.WorkId), ct);

            // The view: this visit's, else the profile's choice for the book, else automatic. The original pages need no analysis, so they open without it.
            var requested = ViewName switch { "smart" => ReadingView.Smart, "original" => ReadingView.Original, _ => _source.ReadingViewTypeId };
            if (_source.HasPdf && requested == ReadingView.Original)
            {
                await LoadOriginalAsync(ct);
                return;
            }

            try
            {
                _document = await open.ExecuteAsync<ReadingDocumentV1>(new ReadingAssetParametersV1(Id), default, ct);
            }
            catch (ServiceUnavailableException exception) when (_source.ReadingFormatTypeId == ReadingFormatType.Pdf
                && exception.Code is "reading_pdf_without_text" or "reading_invalid_file" or "reading_pdf_recognizing" or "reading_pdf_unfit")
            {
                // No reading text, or not yet: the original pages show what Jularr cannot reflow, and a profile that asked for the Smart Book is told it is not there.
                _status = exception.Code == "reading_pdf_recognizing" ? "errors." + exception.Code : requested == ReadingView.Smart ? "reader.view.smartUnavailable" : null;
                await LoadOriginalAsync(ct);
                return;
            }

            // A converted PDF whose text did not pass the quality gate opens as its original pages, unless the profile asked for the Smart Book.
            if (_source.HasPdf && _document.ReadingFormatTypeId == ReadingFormatType.Pdf && !_document.IsRecommended && requested != ReadingView.Smart)
            {
                _document = null;
                await LoadOriginalAsync(ct);
                return;
            }

            if (_document.ReadingFormatTypeId == ReadingFormatType.Comic)
            {
                return;
            }

            var profile = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: ct)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            _speech = await readSpeech.ExecuteAsync<SpeechPreferencesV1>(new ReadProfileParametersV1(profile), ct);
            _speechModels = await readSpeechModels.ExecuteAsync<BaseDataList<SpeechModelV1>>(new SpeechModelsParametersV1(), ct);

            // Reached from a page of the original, or after stopping in it: the text opens where that page starts.
            if (SectionIndex is null && _source.HasPdf && (PdfPage ?? PagePosition.Page(_document.Locator)) is { } wanted)
            {
                try
                {
                    var position = await readPosition.ExecuteAsync<ReadingPositionV1>(new ReadingPositionParametersV1(Id, wanted), ct);
                    SectionIndex = position.SectionIndex;
                    _startBlock = position.Block;
                }
                catch (KeyNotFoundException)
                {
                    // Without prepared text there is no anchor; the text opens where it stopped.
                }
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

        // The original pages open at the page asked for, else where the profile stopped (in them, or in the text, mapped to its page), else the first.
        private async Task LoadOriginalAsync(CancellationToken ct)
        {
            _original = true;
            _state = await readState.ExecuteAsync<ReadingStateV1>(new ReadingAssetParametersV1(Id), ct);
            _pageNotes = await readPageNotes.ExecuteAsync<BaseDataList<ReadingPageNoteV1>>(new ReadingAssetParametersV1(Id), ct);
            _page = PdfPage is >= 1 ? PdfPage.Value : PagePosition.Page(_source!.Locator) ?? await TextPageAsync(ct) ?? 1;
        }

        // The page the text position of the profile (s3/b17) stands on, where the text is prepared.
        private async Task<int?> TextPageAsync(CancellationToken ct)
        {
            var match = System.Text.RegularExpressions.Regex.Match(_source!.Locator ?? "", @"^s(\d{1,5})/b(\d{1,6})$");
            if (!match.Success)
            {
                return null;
            }

            try
            {
                return (await readPosition.ExecuteAsync<ReadingPositionV1>(new ReadingPositionParametersV1(Id, null, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)), ct)).Page;
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }

        private static class PagePosition
        {
            // A position in the original pages is p12: page 12.
            public static int? Page(string? locator) => locator is { Length: > 1 } && locator[0] == 'p' && int.TryParse(locator.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= 1 ? page : null;
        }

        private string ProfileLanguage => CultureInfo.GetCultureInfo(texts.Locale).TwoLetterISOLanguageName;

        private string SpeechLanguage(ReadingDocumentV1 document) => Chosen(document) is { } chosen && chosen != "original" ? chosen
            : document.Language ?? document.OriginalLanguage ?? texts.Locale;

        private IUiComponent SpeechPanel(ReadingDocumentV1 document, int index) => new Div(
            new Div(new H2(new Text("speech.settings")), new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("data-reader-close", "speech")).Class("reader__panel-heading"),
            _speech is null ? new Span() : new SpeechSettings(_speech, SpeechLanguage(document), "/Read/" + Id.ToString(CultureInfo.InvariantCulture)
                + "?handler=Speech&s=" + index.ToString(CultureInfo.InvariantCulture), antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "", FormError, _speechInput, _speechModels)
        ).Class("reader__panel").Attribute("data-panel", "speech").Attribute("hidden", "hidden");

        private IUiComponent SpeechTransport() => new Div(
            new Span().Attribute("data-speech-status", "").Attribute("role", "status"),
            new Button(new Icon("player-pause", assets), new Icon("player-play", assets)).IconOnly(texts["speech.pause"])
                .Attribute("data-speech-pause", "").Attribute("aria-pressed", "false"),
            new Button(new Icon("close", assets)).IconOnly(texts["speech.stop"]).Attribute("data-speech-stop", ""),
            new Button(new Icon("settings", assets)).IconOnly(texts["speech.settings"]).Attribute("data-reader-open-panel", "speech").Attribute("aria-expanded", "false")
        ).Class("reader__speech").Attribute("data-speech-transport", "").Attribute("hidden", "hidden").Attribute("role", "toolbar").Attribute("aria-label", texts["speech.title"]);

        public async Task<IActionResult> OnPostSpeechAsync([BindRequired] decimal rate, [BindRequired] decimal pitch, [BindRequired] decimal volume, long version,
            string language, SpeechProviderType providerTypeId, string? modelId, string? voiceId)
        {
            var profile = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            _speechInput = new SaveSpeechPreferencesDataV1(rate, pitch, volume, version, language, providerTypeId, modelId, voiceId);
            await saveSpeech.ExecuteAsync<UpdateResult>(new UpdateProfileParametersV1(profile), _speechInput, HttpContext.RequestAborted);
            return RedirectToPage("/User/Reader/Reader", new { id = Id, s = SectionIndex });
        }

        // The profile's choice for the book, else its language when Mozilla reaches it, else the original.
        private string Chosen(ReadingDocumentV1 document) => document.ReadingLanguage is { } chosen && (chosen == "original" || _languages.Contains(chosen)) ? chosen
            : _languages.Contains(ProfileLanguage) ? ProfileLanguage : "original";

        private string LanguageName(string code) => code == "original" ? texts["reader.translate.original"]
            : CultureInfo.GetCultureInfo(code) is { } culture ? culture.TextInfo.ToTitleCase(culture.NativeName) : code;

        private static string Percent(long before, long total) =>
            total <= 0 ? "" : Math.Clamp((int)Math.Round(before * 100d / total), 0, 100).ToString(CultureInfo.InvariantCulture) + " %";
    }
}
