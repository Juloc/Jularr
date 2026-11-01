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
        ReadReadingBookmarksV1 readBookmarks, DeleteReadingAnnotationV1 deleteNote, SearchReadingV1 search, ReadUiLocalesV1 readLocales, ReadLocalTranslationAvailabilityV1 readLocalAvailability, ReadWorkV1 readWork,
        ReadSpeechPreferencesV1 readSpeech, SaveSpeechPreferencesV1 saveSpeech, ReadAvailableSpeechModelsV1 readSpeechModels, ReadAuthenticationV1 authentication, IAntiforgery antiforgery, UiText texts, FrontendAssets assets,
        ReadReadingStateV1 readState, ReadReadingPositionV1 readPosition, ReadReadingPageNotesV1 readPageNotes, CreateReadingPageNoteV1 createPageNote, DeleteReadingPageNoteV1 deletePageNote,
        ReadReaderSettingsV1 readSettings, ReadReadingSectionOffsetsV1 readOffsets)
        : JularrPage
    {
        // The languages this book can be read in: its original first, then the profile's and the other allowed ones Mozilla's models reach.
        private string[] _languages = [];
        private string? _localTranslationModel;

        // A PDF without reading text, or whose reading text the profile did not ask for, opens as its original pages and says why, here, in a word.
        private string? _status;

        // The original pages show; the page they open at, the book's state and the profile's notes on its pages.
        private bool _original;

        // The prepared book whose scanned pages show as Smart Pages inside the original pages; null where they show as scanned.
        private ReadingDocumentV1? _smart;
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
        private IReadOnlyList<ReadingSectionOffsetV1> _offsets = [];
        private ReaderSettingsV1? _readerSettings;
        private IReadOnlyList<ReadingAnnotationV1> _marks = [];
        private PageResult<ReadingAnnotationV1>? _notes;
        private IReadOnlyList<ReadingBookmarkV1> _bookmarks = [];
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
            page.Asset("Framework/Components/Truncated.entry.ts");
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
            // Every tool but More leaves the bar when it has no room, in the order of its rank, and waits in the More menu.
            var canTranslate = texts.CanTranslate();
            var top = Header(document.Title, current.Title ?? "", document.WorkId, [
                .. canTranslate ? [Overflowing(Tool("globe", "reader.language", "language"), "language", 1)] : Array.Empty<IUiComponent>(),
                Overflowing(AppearanceTool(false), "text", 2),
                Overflowing(ModeTool(), "mode", 2),
                Overflowing(new Button(new Icon("palette", assets)).IconOnly(texts["reader.settings.paper"]).Attribute("data-reader-open-settings", "paper").Attribute("aria-expanded", "false"), "paper", 7),
                Overflowing(BookmarkTool(), "bookmark", 4),
                Overflowing(Tool("pencil", "reader.notes", "notes"), "notes", 6),
                Overflowing(new Button(new Icon("headphones", assets)).IconOnly(texts["speech.title"]).Attribute("data-speech-toggle", "").Attribute("aria-pressed", "false").Attribute("disabled", "disabled"), "speech", 3),
                Overflowing(Tool("search", "reader.search", "search"), "search", 5),
                Overflowing(new Button(new Icon("text-select", assets)).IconOnly(texts["reader.selectText"]).Attribute("data-reader-select", "").Attribute("aria-pressed", "false"), "select", 8),
                Tool("player-more", "reader.more", "more").Attribute("data-overflow-more", "")
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
            // Without a configured language the panel says so and leads to where the profile sets it.
            var languages = _languages.Length == 0
                ? new Div(new P(new Text("reader.language.none"))).Class("reader__panel")
                    .Attribute("data-panel", "language").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.language"])
                : new Div([.. new[] { "original" }.Concat(_languages).Select(code => new Button(new ValueText(LanguageName(code))).Class("button")
                    .Attribute("data-language-choice", code).Attribute("aria-pressed", code == Chosen(document) ? "true" : "false")),
                    new P(new Text("reader.translation.provider.original")).Attribute("data-translation-provider", "")
                        .Attribute("role", "status")]).Class("reader__panel reader__choices")
                    .Attribute("data-panel", "language").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.language"]);

            var bottom = new Div(Seek(false), new Div(
                    current.Title is { Length: > 0 } title ? new Span(new Truncated(title)).Class("reader__caption").Attribute("data-role", "caption") : new Span(),
                    new Span(new ValueText(Percent(current.CharactersBefore, current.TotalCharacters))).Class("reader__position").Attribute("data-role", "position"))
                .Class("reader__context")).Class("reader__bottom");
            var view = new ReaderFrame(assets, texts,
                top, contents, new ReaderSection(current,
                    index > 0 ? "?s=" + (index - 1).ToString(CultureInfo.InvariantCulture) + "&f=1" : null,
                    index + 1 < document.SectionCount ? "?s=" + (index + 1).ToString(CultureInfo.InvariantCulture) : null), bottom,
                notes,
                searching,
                settings,
                languages,
                downloads,
                SpeechPanel(document, index),
                TimerPanel(),
                SpeechTransport(),
                More([
                    .. canTranslate ? [MoreEntry(new Icon("globe", assets), "reader.language", "language")] : Array.Empty<IUiComponent>(),
                    MoreEntry(new Span(new ValueText("Aa")).Class("reader__type-icon"), "reader.appearance", "text"),
                    MoreEntry(new Icon("palette", assets), "reader.settings.paper", "paper"),
                    MoreEntry(new Icon("book", assets), "reader.mode.toggle", "mode"),
                    MoreEntry(new Icon("bookmark", assets), "reader.bookmark", "bookmark"),
                    MoreEntry(new Icon("pencil", assets), "reader.notes", "notes"),
                    MoreEntry(new Icon("headphones", assets), "speech.title", "speech"),
                    MoreEntry(new Icon("search", assets), "reader.search", "search"),
                    MoreEntry(new Icon("text-select", assets), "reader.selectText", "select"),
                    .. ChapterEntries(index, document.SectionCount),
                    .. ViewEntries(),
                    new Button(new Icon("headphones", assets), new Text("speech.settings")).Class("button reader__menu-item").Attribute("data-reader-open-panel", "speech").Attribute("aria-expanded", "false"),
                    .. converted.Length > 0 || document.TranslatedLanguages.Length > 0
                        ? [new Button(new Icon("download", assets), new Text("reader.download")).Class("button reader__menu-item").Attribute("data-action", "download").Attribute("aria-expanded", "false")]
                        : Array.Empty<IUiComponent>()
                ]),
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
                .Attribute("data-document-revision", document.Revision ?? "")
                .Attribute("data-sections-url", "/api/v1/reading/documents/" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + "/sections/")
                .Attribute("data-blocks", current.Blocks.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-before", current.CharactersBefore.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-characters", current.Characters.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-total", current.TotalCharacters.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-locator", document.Locator ?? "")
                .Attribute("data-annotations-url", "/api/v1/reading/assets/" + asset + "/annotations")
                .Attribute("data-bookmarks-url", "/api/v1/reading/assets/" + asset + "/bookmarks")
                .Attribute("data-bookmark-delete-label", texts["reader.bookmarkDelete"])
                .Attribute("data-annotations", JsonSerializer.Serialize(_marks, Json))
                .Attribute("data-note-label", texts["reader.note"])
                .Attribute("data-query", Query ?? "")
                .Attribute("data-page-label", texts["reader.pagePosition"])
                .TextAttribute("data-chapter-page-label", "reader.chapterPagePosition")
                .Attribute("data-animation-failed", texts["reader.animationUnavailable"])
                .Attribute("data-figure-label", texts["reader.figure.open"])
                .Attribute("data-offsets", string.Join(',', _offsets.Select(offset => offset.CharactersBefore.ToString(CultureInfo.InvariantCulture))))
                .Attribute("data-chapters", JsonSerializer.Serialize(ChapterTicks(), Json))
                .Attribute("data-settings-url", "/api/v1/reading/settings")
                .TextAttribute("data-timer-left-label", "speech.timer.left");
            if (_readerSettings is { IsSaved: true } saved)
            {
                // The profile's own choices lead over what this device remembered.
                view.Attribute("data-profile-settings", JsonSerializer.Serialize(new { font = saved.FontTypeId == ReaderFont.Sans ? "sans" : "serif", size = saved.Size, spacing = saved.LineSpacing,
                    paragraph = saved.ParagraphSpacing, width = saved.TextWidth, hyphenation = saved.Hyphenation }, Json));
            }

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
                // The server's TranslateGemma translates; what its job prepared shows at once, the rest of the page in view it translates first.
                // A book in another language opens in the profile's, unless the profile chose another for it.
                // TODO(#1726): Mozilla's translation in the browser returns only as an emergency module, off by default.
                view.Attribute("data-translation-engine", "gemma")
                    .Attribute("data-local-model", _localTranslationModel ?? "")
                    .Attribute("data-language", document.Language ?? document.OriginalLanguage ?? "")
                    .Attribute("data-reading-language", Chosen(document))
                    .Attribute("data-language-url", "/api/v1/reading/assets/" + asset + "/language")
                    .Attribute("data-local-translation-url", "/api/v1/reading/documents/" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + "/sections/" + index.ToString(CultureInfo.InvariantCulture) + "/local-translations/")
                    .Attribute("data-translation-url", "/api/v1/reading/documents/" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + "/sections/"
                        + index.ToString(CultureInfo.InvariantCulture) + "/translations/")
                    .Attribute("data-translating-label", texts["reader.translating"])
                    .Attribute("data-language-saving-label", texts["reader.language.saving"])
                    .Attribute("data-translation-failed-label", texts["reader.translationFailed"])
                    .Attribute("data-provider-original-label", texts["reader.translation.provider.original"])
                    .Attribute("data-provider-local-label", texts["reader.translation.provider.local"]);
                // The Mozilla module is an emergency only: with it on, a page the server cannot translate falls back to the browser.
                if (texts.CanTranslateInBrowser())
                {
                    view.Attribute("data-browser-translation", "true")
                        .Attribute("data-route-url", "/api/v1/translation/routes/")
                        .Attribute("data-browser-unsupported-label", texts["reader.translation.browserUnsupported"]);
                }
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
            var translates = _smart is not null && texts.CanTranslate();
            var top = Header(source.Title, "", source.WorkId, [
                .. translates ? [Overflowing(Tool("globe", "reader.language", "language"), "language", 1)] : Array.Empty<IUiComponent>(),
                Overflowing(Tool("bookmark", "reader.notes", "notes"), "notes", 2),
                Overflowing(new Button(new Icon("settings", assets)).IconOnly(texts["reader.settings"]).Attribute("data-reader-open-settings", "all").Attribute("aria-expanded", "false"), "settings", 1),
                Tool("player-more", "reader.more", "more").Attribute("data-overflow-more", "")
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

            var frame = new ReaderFrame(assets, texts, top, new Span(), stage, bottom, Settings(true, true, true), PageNotes(), notices,
                translates && _smart is { } smart ? Languages(smart) : new Span(),
                More([.. translates ? [MoreEntry(new Icon("globe", assets), "reader.language", "language")] : Array.Empty<IUiComponent>(),
                    MoreEntry(new Icon("bookmark", assets), "reader.notes", "notes"), MoreEntry(new Icon("settings", assets), "reader.settings", "settings"), .. ViewEntries(),
                    new A(new Icon("download", assets), new Text("reader.originalPdf")).Class("button reader__menu-item").Attribute("href", file).Attribute("target", "_blank").Attribute("rel", "noopener")]),
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
            if (_smart is { } document)
            {
                // Scanned pages set anew: their reading units (from 100000 on) and, where the server's model translates, the language to show them in.
                var units = "/api/v1/reading/documents/" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + "/sections/";
                frame.Attribute("data-smart-pages", string.Join(',', document.SmartPages.Select(number => number.ToString(CultureInfo.InvariantCulture))))
                    .Attribute("data-smart-url", units);
                if (translates)
                {
                    frame.Attribute("data-local-model", _localTranslationModel ?? "")
                        .Attribute("data-language", document.Language ?? document.OriginalLanguage ?? "")
                        .Attribute("data-reading-language", Chosen(document))
                        .Attribute("data-language-url", "/api/v1/reading/assets/" + asset + "/language")
                        .Attribute("data-translating-label", texts["reader.translating"])
                        .Attribute("data-translation-failed-label", texts["reader.translationFailed"]);
                }
            }

            return frame;
        }

        // The languages the book can be read in; the choice is kept for the profile.
        private IUiComponent Languages(ReadingDocumentV1 document) => _languages.Length == 0
            ? new Div(new P(new Text("reader.language.none"))).Class("reader__panel").Attribute("data-panel", "language").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.language"])
            : new Div([.. new[] { "original" }.Concat(_languages).Select(code => new Button(new ValueText(LanguageName(code))).Class("button")
                .Attribute("data-language-choice", code).Attribute("aria-pressed", code == Chosen(document) ? "true" : "false")),
                new P().Attribute("data-role", "translation-status").Attribute("role", "status").Attribute("hidden", "hidden")])
                .Class("reader__panel reader__choices").Attribute("data-panel", "language").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.language"]);

        private string RecognitionText(ReadingStateV1 state) => string.Format(CultureInfo.InvariantCulture, texts["reader.recognition.progress"], state.RecognizedPages, state.RecognitionPages);

        // The profile's choice as the page's script names it.
        private static string ViewChoiceName(ReadingView? view) => view switch
        {
            ReadingView.Smart => "smart", ReadingView.Original => "original", ReadingView.SmartPages => "smartpages", _ => "auto"
        };

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
                new Input().Attribute("type", "range").Attribute("min", "100").Attribute("max", "800").Attribute("step", "1").Attribute("value", "100")
                    .Attribute("aria-label", texts["reader.figure.zoomLevel"]).Attribute("data-figure-scale", "").Class("reader-figure__scale"),
                new Span(new ValueText("100%")).Class("reader-figure__level").Attribute("data-figure-level", ""),
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
            var top = Header(document.Title, "", document.WorkId, [Overflowing(AppearanceTool(true), "text", 1), Overflowing(ModeTool(), "mode", 2), Overflowing(BookmarkTool(), "bookmark", 2), Tool("player-more", "reader.more", "more").Attribute("data-overflow-more", "")]);
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
                .Class("reader__toc-entry" + (number == index ? " is-current" : "")).Attribute("href", Page(number))), Bookmarks(true)])
                .Class("reader__panel").Attribute("data-panel", "contents").Attribute("aria-label", texts["reader.contents"]);

            var note = document.IsCached ? (IUiComponent)new Span() : new P(new Text("reader.notCached")).Class("reader__note").Attribute("role", "status");
            var more = More([MoreEntry(new Span(new ValueText("Aa")).Class("reader__type-icon"), "reader.appearance", "text"), MoreEntry(new Icon("book", assets), "reader.mode.toggle", "mode"),
                MoreEntry(new Icon("bookmark", assets), "reader.bookmark", "bookmark"), .. ViewEntries()]);
            return new ReaderFrame(assets, texts, top, contents, stage, bottom, Settings(true), more,
                new P(new Text("progress.saveFailed")).Attribute("data-role", "save-error").Attribute("role", "status").Attribute("hidden", "hidden"), note)
                .Class("reader").Attribute("data-format", "comic")
                .Attribute("data-progress-url", "/api/v1/reading/assets/" + Id.ToString(CultureInfo.InvariantCulture) + "/progress")
                .Attribute("data-antiforgery", antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "")
                .Attribute("data-pages", pages)
                .Attribute("data-bookmarks-url", "/api/v1/reading/assets/" + Id.ToString(CultureInfo.InvariantCulture) + "/bookmarks")
                .Attribute("data-bookmark-delete-label", texts["reader.bookmarkDelete"])
                .Attribute("data-page", index.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-count", count.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-direction", document.IsRightToLeft ? "rtl" : "ltr")
                .Attribute("data-page-label", texts["reader.comic.page"])
                .Attribute("data-animation-failed", texts["reader.animationUnavailable"]);
        }

        // The progress slider on the bottom bar's upper edge; script gives it its range, and its ticks where chapters begin.
        private static IUiComponent Seek(bool images) => new Div(
            new Form<ReaderPositionData>(form => form.Range(data => data.Position, images ? "reader.seek.pages" : "reader.seek.book"), data: new ReaderPositionData(), local: true),
            images ? new Span() : new Div().Class("reader__ticks").Attribute("data-role", "ticks").Attribute("aria-hidden", "true"))
                .Class("reader__seek").Attribute("data-role", "seek").Attribute("hidden", "hidden");

        private IUiComponent Header(string title, string chapter, long workId, IUiComponent[] tools, IUiComponent? chapters = null) => new Header(
            new Div(
                new A(new Icon("chevron-left", assets)).Class("button button--icon").Attribute("aria-label", texts["common.back"]).Attribute("data-reader-back", "")
                    .Attribute("href", "/Details/" + workId.ToString(CultureInfo.InvariantCulture)),
                new A(MediaImage.Or(assets, _work?.PosterUrl, "", 28, 40, priority: true)).Class("reader__cover").Attribute("href", "/Details/" + workId.ToString(CultureInfo.InvariantCulture))
                        .Attribute("aria-label", title),
                new Div(new H1(new Truncated(title)).Class("reader__title").Attribute("id", "reader-document-title"),
                    chapter.Length > 0 ? new P(new Truncated(chapter)).Class("reader__chapter") : new Span()).Class("reader__heading")).Class("reader__identity"),
            chapters ?? new Span(), new Nav(tools).Class("reader__tools"))
                .Class("reader__top");

        private HtmlElement Tool(string icon, string key, string action) => new Button(new Icon(icon, assets)).IconOnly(texts[key])
            .Attribute("data-action", action).Attribute("aria-expanded", "false");

        // A tool that waits in the More menu when its bar has no room; the lower its rank, the longer it stays.
        private static HtmlElement Overflowing(HtmlElement tool, string key, int rank) => tool.Attribute("data-overflow", key).Attribute("data-overflow-rank", rank.ToString(CultureInfo.InvariantCulture));

        // Marks or clears the current place; the script keeps the pressed state with the page.
        // Flat pages, the book or scrolling: one button cycles through them and shows the current one's icon.
        private HtmlElement ModeTool() => new Button(new Span(new Icon("document", assets)).Attribute("data-mode-icon", "flat"),
                new Span(new Icon("book", assets)).Attribute("data-mode-icon", "book"), new Span(new Icon("list", assets)).Attribute("data-mode-icon", "scroll"))
            .IconOnly(texts["reader.mode.toggle"]).Attribute("data-reader-mode", "").TextAttribute("data-mode-label", "reader.mode.toggle")
            .TextAttribute("data-label-flat", "reader.mode.flat").TextAttribute("data-label-book", "reader.mode.book").TextAttribute("data-label-scroll", "reader.mode.scroll");

        private HtmlElement BookmarkTool() => new Button(new Icon("bookmark", assets)).IconOnly(texts["reader.bookmark"])
            .Attribute("data-reader-bookmark", "").Attribute("aria-pressed", "false");

        private HtmlElement AppearanceTool(bool images) => new Button(new Span(new ValueText("Aa")).Class("reader__type-icon"))
            .IconOnly(texts["reader.appearance"]).Attribute("data-reader-open-settings", images ? "all" : "text").Attribute("aria-expanded", "false");

        // The More menu's stand-in for a tool of the bar: shown only while that tool has left it, and doing what the tool does.
        private static IUiComponent MoreEntry(IUiComponent glyph, string key, string tool) => new Button(glyph, new Text(key)).Class("button reader__menu-item")
            .Attribute("data-overflow-for", tool).Attribute("hidden", "hidden");

        private IEnumerable<IUiComponent> ViewEntries() =>
        [
            new Button(new Icon("player-theater", assets), new Text("reader.theater")).Class("button reader__menu-item")
                .Attribute("data-reader-command", "theater").Attribute("data-overflow-for", "theater").Attribute("aria-pressed", "false").Attribute("hidden", "hidden"),
            new Button(new Icon("player-fullscreen", assets), new Text("player.fullscreen")).Class("button reader__menu-item")
                .Attribute("data-reader-command", "fullscreen").Attribute("data-overflow-for", "fullscreen").Attribute("aria-pressed", "false").Attribute("hidden", "hidden")
        ];

        private IEnumerable<IUiComponent> ChapterEntries(int index, int count) =>
        [
            index > 0 ? new A(new Icon("chevron-left", assets), new Text("reader.chapter.previous")).Class("button reader__menu-item").Attribute("href", "?s=" + (index - 1).ToString(CultureInfo.InvariantCulture))
                .Attribute("data-overflow-for", "chapters").Attribute("hidden", "hidden") : new Span(),
            index + 1 < count ? new A(new Icon("chevron-left", assets), new Text("reader.chapter.next")).Class("button reader__menu-item reader__turn-next").Attribute("href", "?s=" + (index + 1).ToString(CultureInfo.InvariantCulture))
                .Attribute("data-overflow-for", "chapters").Attribute("hidden", "hidden") : new Span()
        ];

        // Where each chapter of the book begins, as a share of its text, for the progress slider's ticks and its caption while it is dragged.
        private object[] ChapterTicks()
        {
            var before = _offsets.ToDictionary(offset => offset.SectionIndex, offset => offset.CharactersBefore);
            var total = Math.Max(1, _section?.TotalCharacters ?? 1);
            return [.. _toc.Where(entry => entry.Level == 0 && before.ContainsKey(entry.SectionIndex)).GroupBy(entry => entry.SectionIndex).Select(group => group.First()).Take(500)
                .Select(entry => (object)new { section = entry.SectionIndex, title = entry.Title, before = Math.Round((decimal)before[entry.SectionIndex] / total, 4) })];
        }

        private IUiComponent Chapters(int index, int count) => new Nav(
            index > 0 ? new A(new Icon("chevron-left", assets)).Class("button button--icon").Attribute("aria-label", texts["reader.previous"])
                .Attribute("href", "?s=" + (index - 1).ToString(CultureInfo.InvariantCulture)).Attribute("data-role", "previous") : new Span(),
            new Span(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["reader.chapterPosition"], index + 1, count))),
            index + 1 < count ? new A(new Icon("chevron-left", assets)).Class("button button--icon reader__turn-next").Attribute("aria-label", texts["reader.next"])
                .Attribute("href", "?s=" + (index + 1).ToString(CultureInfo.InvariantCulture)).Attribute("data-role", "next") : new Span())
            .Class("reader__chapter-navigation").Attribute("aria-label", texts["reader.chapterNavigation"]).Attribute("data-overflow", "chapters").Attribute("data-overflow-rank", "9");

        // Tools that left their bar wait here, then what only the menu holds.
        private IUiComponent More(IEnumerable<IUiComponent> entries) => new Div(
            new Div(new H2(new Text("reader.more")), new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("data-reader-close", "more")).Class("reader__panel-heading"),
            new Div([.. entries]).Class("reader__menu"))
            .Class("reader__panel reader__more").Attribute("data-panel", "more").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.more"]);

        // The view switch of a book with a PDF: the profile's choice, kept on all its devices.
        private IUiComponent ViewGroup() => SettingsGroup("view", "reader.settings.view", group => group.Content(new Div(
            new Button(new Text("reader.view.auto")).Class("button").Attribute("data-view-choice", "auto"),
            new Button(new Text("reader.view.smart")).Class("button").Attribute("data-view-choice", "smart"),
            new Button(new Text("reader.view.smartPages")).Class("button").Attribute("data-view-choice", "smartpages"),
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
                .Select(data => data.Layout, "reader.layout", [new("auto", texts["reader.layout.auto"]), new("single", texts["reader.layout.single"]), new("spread", texts["reader.layout.spread"])])
                .Select(data => data.Direction, "reader.comic.direction", [new("ltr", texts["reader.direction.ltr"]), new("rtl", texts["reader.direction.rtl"])]), open: true),
            images ? new Span() : SettingsGroup("text", "reader.settings.text", group => group
                .Select(data => data.FontFamily, "reader.font", [new("serif", texts["reader.font.serif"]), new("sans", texts["reader.font.sans"])])
                .Range(data => data.Size, "reader.sizePixels").Range(data => data.Spacing, "reader.spacing").Range(data => data.Paragraph, "reader.paragraph").Range(data => data.TextWidth, "reader.textWidth")
                .Range(data => data.MarginTop, "reader.margin.top").Range(data => data.MarginBottom, "reader.margin.bottom")
                .Range(data => data.MarginInner, "reader.margin.inner").Range(data => data.MarginOuter, "reader.margin.outer")
                .Toggle(data => data.Indent, "reader.indent").Toggle(data => data.Hyphenation, "reader.hyphenation").Toggle(data => data.Illustrations, "reader.illustrations")
                .Content(new P().Attribute("data-role", "book-pages").Attribute("role", "status").Attribute("hidden", "hidden")
                    .TextAttribute("data-calculating-label", "reader.bookPages.calculating").TextAttribute("data-paused-label", "reader.bookPages.paused")
                    .TextAttribute("data-ready-label", "reader.bookPages.ready").TextAttribute("data-unavailable-label", "reader.bookPages.unavailable"))),
            pages ? new Span() : SettingsGroup("paper", "reader.settings.paper", group => group
                .Select(data => data.Surface, "reader.surface", [new("clean", texts["reader.surface.clean"]), new("paper", texts["reader.surface.paper"])])
                .Select(data => data.Binding, "reader.binding", [new("soft", texts["reader.binding.soft"]), new("hard", texts["reader.binding.hard"])])
                .Color(data => data.Colour, "reader.colour")),
            new P(new Text("reader.settings.saveFailed")).Attribute("data-role", "reader-settings-save-error")
                .Attribute("role", "alert").Attribute("hidden", "hidden")
            )
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
            var div = new Div(Bookmarks(false), NoteEntries(index)).Class("reader__panel").Attribute("data-panel", "notes").Attribute("aria-label", texts["reader.notes"]);
            return NotesPage > 1 ? div : div.Attribute("hidden", "hidden");
        }

        private IUiComponent NoteEntries(int index)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var notes = _notes?.Items ?? [];
            var entries = new Ul([.. notes.Select(note => new Li([
                    new A(new ValueText(note.Quote)).Class("reader__note-quote").Attribute("data-color", note.HighlightColorTypeId.ToString())
                        .Attribute("href", "?s=" + note.SectionIndex.ToString(CultureInfo.InvariantCulture) + "&b=" + note.StartBlock.ToString(CultureInfo.InvariantCulture)),
                    .. note.Note is { } text ? new IUiComponent[] { new P(new ValueText(text)).Class("reader__note-text") } : [],
                    new Form<ReadingAnnotationParametersV1>(form => form.Submit("reader.deleteNote"),
                        "/Read/" + Id.ToString(CultureInfo.InvariantCulture) + "?handler=DeleteNote&s=" + index.ToString(CultureInfo.InvariantCulture) + "&noteId=" + note.Id.ToString(CultureInfo.InvariantCulture), csrf,
                        new ReadingAnnotationParametersV1(Id, note.Id))]).Class("reader__note").Attribute("data-list-key", note.Id.ToString(CultureInfo.InvariantCulture)))]).Class("reader__notes");
            return new Div(entries,
                notes.Count == 0 ? new P(new Text("reader.notesEmpty")) : new Span(),
                new Span().Infinite(notes.Count, 48, "n", ".reader__notes"));
        }

        // The profile's places in the book in reading order; the script adds and removes them.
        private IUiComponent Bookmarks(bool images)
        {
            string Share(decimal progression) => Math.Round(progression * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
            var list = new Ul([.. _bookmarks.Select(mark => new Li([
                new A(new ValueText(images ? texts["reader.comic.page"] + " " + (mark.SectionIndex + 1).ToString(CultureInfo.InvariantCulture) : mark.Title ?? Share(mark.Progression))).Class("reader__note-quote")
                    .Attribute("href", images ? "?p=" + mark.SectionIndex.ToString(CultureInfo.InvariantCulture)
                        : "?s=" + mark.SectionIndex.ToString(CultureInfo.InvariantCulture) + "&b=" + mark.Block.ToString(CultureInfo.InvariantCulture)),
                new P(new ValueText(Share(mark.Progression))).Class("reader__note-text"),
                new Button(new ValueText(texts["reader.bookmarkDelete"])).Class("button").Attribute("type", "button").Attribute("data-bookmark-delete", "")])
                .Class("reader__note").Attribute("data-bookmark-id", mark.Id.ToString(CultureInfo.InvariantCulture)).Attribute("data-section", mark.SectionIndex.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-block", mark.Block.ToString(CultureInfo.InvariantCulture)).Attribute("data-progression", mark.Progression.ToString(CultureInfo.InvariantCulture)))])
                .Class("reader__notes").Attribute("data-role", "bookmark-list");
            var group = new Div(new H3(new Text("reader.bookmarks")), list).Class("reader__bookmarks").Attribute("data-role", "bookmarks");
            return _bookmarks.Count == 0 ? group.Attribute("hidden", "hidden") : group;
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


        public async Task<IActionResult> OnPostDeleteNoteAsync([BindRequired] long noteId)
        {
            await deleteNote.ExecuteAsync<UpdateResult>(new ReadingAnnotationParametersV1(Id, noteId), default, HttpContext.RequestAborted);
            return LocalRedirect("/Read/" + Id.ToString(CultureInfo.InvariantCulture) + "?s=" + (SectionIndex ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        private static string PagePath(int index) => ":page/" + index.ToString("D5", CultureInfo.InvariantCulture);

        // A list read checks the same source visibility without reopening or preparing the book.
        public async Task<IActionResult> OnGetNotesAsync()
        {
            var ct = HttpContext.RequestAborted;
            await readSource.ExecuteAsync<ReadingSourceV1>(new ReadingAssetParametersV1(Id), ct);
            _notes = await readNotes.ExecuteAsync<PageResult<ReadingAnnotationV1>>(new ReadingAnnotationsParametersV1(Id, Math.Max(1, NotesPage), 48), ct);
            PagingScope.Begin(Request, assets);
            try { return Partial("_UiComponent", NoteEntries(SectionIndex ?? 0)); }
            finally { PagingScope.End(); }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // The profile's languages, offered where the server's TranslateGemma is installed. The Reader asks Mozilla for nothing (#1726).
        private async Task<string[]> LanguagesAsync(CancellationToken ct)
        {
            var allowed = (await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(new ReadUiLocalesParametersV1(), ct))
                .Select(locale => CultureInfo.GetCultureInfo(locale.Locale).TwoLetterISOLanguageName);
            var candidates = new[] { ProfileLanguage }.Concat(allowed).Distinct(StringComparer.Ordinal).ToArray();

            try
            {
                _localTranslationModel = (await readLocalAvailability.ExecuteAsync<LocalTranslationAvailabilityV1>(
                    new LocalTranslationAvailabilityParametersV1(), ct)).Model;
            }
            catch (ServiceUnavailableException)
            {
                _localTranslationModel = null;
            }

            return _localTranslationModel is not null ? candidates : [];
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _source = await readSource.ExecuteAsync<ReadingSourceV1>(new ReadingAssetParametersV1(Id), ct);
            _work = await readWork.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(_source.WorkId), ct);

            // The view: this visit's, else the profile's choice for the book, else automatic. The original pages need no analysis, so they open without it.
            var requested = ViewName switch { "smart" => ReadingView.Smart, "original" => ReadingView.Original, "smartpages" => ReadingView.SmartPages, _ => _source.ReadingViewTypeId };
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
                _status = exception.Code == "reading_pdf_recognizing" ? "errors." + exception.Code : requested == ReadingView.Smart ? "reader.view.smartUnavailable"
                    : requested == ReadingView.SmartPages ? "reader.view.smartPagesUnavailable" : null;
                await LoadOriginalAsync(ct);
                return;
            }

            // Smart Pages: by choice, or by themselves where the reading text did not pass its quality gate and the scan's pages are set anew; the pages
            // without a Smart Page show as scanned.
            if (_source.HasPdf && _document.ReadingFormatTypeId == ReadingFormatType.Pdf && (requested == ReadingView.SmartPages || requested is null && !_document.IsRecommended))
            {
                if (_document.SmartPages.Length > 0)
                {
                    _smart = _document;
                    if (texts.CanTranslate())
                    {
                        _languages = await LanguagesAsync(ct);
                    }

                    await LoadOriginalAsync(ct);
                    return;
                }

                if (requested == ReadingView.SmartPages)
                {
                    _status = "reader.view.smartPagesUnavailable";
                    _document = null;
                    await LoadOriginalAsync(ct);
                    return;
                }
            }

            // A converted PDF whose text did not pass the quality gate opens as its original pages, unless the profile asked for the Smart Book.
            if (_source.HasPdf && _document.ReadingFormatTypeId == ReadingFormatType.Pdf && !_document.IsRecommended && requested != ReadingView.Smart)
            {
                _document = null;
                await LoadOriginalAsync(ct);
                return;
            }

            _bookmarks = await readBookmarks.ExecuteAsync<BaseDataList<ReadingBookmarkV1>>(new ReadingAssetParametersV1(Id), ct);
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
            _offsets = await readOffsets.ExecuteAsync<BaseDataList<ReadingSectionOffsetV1>>(new ReadingDocumentParametersV1(_document.DocumentId), ct);
            _readerSettings = await readSettings.ExecuteAsync<ReaderSettingsV1>(new ReaderSettingsParametersV1(), ct);
            _marks = await readMarks.ExecuteAsync<BaseDataList<ReadingAnnotationV1>>(new ReadingSectionAnnotationsParametersV1(Id, index), ct);
            _notes = await readNotes.ExecuteAsync<PageResult<ReadingAnnotationV1>>(new ReadingAnnotationsParametersV1(Id, Math.Max(1, NotesPage), 48), ct);
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

        // The floating bar while the book is read aloud: what is happening, pause, stop, the sleep timer with its remaining time and the voice settings.
        private IUiComponent SpeechTransport() => new Div(
            new Span().Attribute("data-speech-status", "").Attribute("role", "status"),
            new Button(new Icon("player-pause", assets), new Icon("player-play", assets)).IconOnly(texts["speech.pause"])
                .Attribute("data-speech-pause", "").Attribute("aria-pressed", "false"),
            new Button(new Icon("close", assets)).IconOnly(texts["speech.stop"]).Attribute("data-speech-stop", ""),
            new Button(new Icon("clock", assets), new Span().Attribute("data-speech-timer-left", "").Class("reader__timer-left")).IconOnly(texts["speech.timer"])
                .Class("button button--icon reader__timer").Attribute("data-reader-open-panel", "timer").Attribute("aria-expanded", "false"),
            new Button(new Icon("settings", assets)).IconOnly(texts["speech.settings"]).Attribute("data-reader-open-panel", "speech").Attribute("aria-expanded", "false")
        ).Class("reader__speech").Attribute("data-speech-transport", "").Attribute("hidden", "hidden").Attribute("role", "toolbar").Attribute("aria-label", texts["speech.title"]);

        // The sleep timer: read-aloud stops by itself after the chosen minutes; it is not remembered.
        private IUiComponent TimerPanel() => new Div(
            new Div(new H2(new Text("speech.timer")), new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("data-reader-close", "timer")).Class("reader__panel-heading"),
            new Form<ReaderTimerData>(form => form.Select(data => data.Minutes, "speech.timer.after", [new SelectOption("0", texts["speech.timer.off"]),
                .. new[] { 10, 15, 30, 45, 60 }.Select(minutes => new SelectOption(minutes.ToString(CultureInfo.InvariantCulture), string.Format(CultureInfo.InvariantCulture, texts["speech.timer.minutes"], minutes)))]),
                data: new ReaderTimerData(), local: true)
        ).Class("reader__panel").Attribute("data-panel", "timer").Attribute("hidden", "hidden");

        public async Task<IActionResult> OnPostSpeechAsync([BindRequired] decimal rate, [BindRequired] decimal pitch, [BindRequired] decimal volume, long version,
            string language, SpeechProviderType providerTypeId, string? modelId, string? voiceId)
        {
            var profile = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            _speechInput = new SaveSpeechPreferencesDataV1(rate, pitch, volume, version, language, providerTypeId, modelId, voiceId);
            await saveSpeech.ExecuteAsync<UpdateResult>(new UpdateProfileParametersV1(profile), _speechInput, HttpContext.RequestAborted);
            return RedirectToPage("/User/Reader/Reader", new { id = Id, s = SectionIndex });
        }

        // The profile's choice for the book, else its language when the server translates it, else the original.
        private string Chosen(ReadingDocumentV1 document) => document.ReadingLanguage is { } chosen && (chosen == "original" || _languages.Contains(chosen)) ? chosen
            : _languages.Contains(ProfileLanguage) ? ProfileLanguage : "original";

        private string LanguageName(string code) => code == "original" ? texts["reader.translate.original"]
            : CultureInfo.GetCultureInfo(code) is { } culture ? culture.TextInfo.ToTitleCase(culture.NativeName) : code;

        private static string Percent(long before, long total) =>
            total <= 0 ? "" : Math.Clamp((int)Math.Round(before * 100d / total), 0, 100).ToString(CultureInfo.InvariantCulture) + " %";
    }
}
