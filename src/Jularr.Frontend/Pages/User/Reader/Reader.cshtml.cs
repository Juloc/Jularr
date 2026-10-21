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
        ReadReadingBookmarksV1 readBookmarks, DeleteReadingAnnotationV1 deleteNote, SearchReadingV1 search, ReadUiLocalesV1 readLocales, ReadTranslationLanguagesV1 readLanguages, ReadWorkV1 readWork,
        ReadSpeechPreferencesV1 readSpeech, SaveSpeechPreferencesV1 saveSpeech, ReadAvailableSpeechModelsV1 readSpeechModels, ReadAuthenticationV1 authentication, IAntiforgery antiforgery, UiText texts, FrontendAssets assets)
        : JularrPage
    {
        // The languages this book can be read in: its original first, then the profile's and the other allowed ones Mozilla's models reach.
        private string[] _languages = [];

        // A scan whose text is being recognized opens as a PDF meanwhile and says so.
        private string? _status;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private ReadingSourceV1? _source;
        private WorkDetailsV1? _work;
        private ReadingDocumentV1? _document;
        private ReadingSectionV1? _section;
        private IReadOnlyList<ReadingTocEntryV1> _toc = [];
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

        [BindProperty(SupportsGet = true, Name = "n")]
        public int NotesPage { get; set; } = 1;

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Query { get; set; }

        protected override void Define(Page page)
        {
            page.Title("reader.title", _source?.Title);
            page.IsReader = true;
            // A PDF without text stays in the browser's own viewer.
            if (_source is { ReadingFormatTypeId: ReadingFormatType.Pdf } pdf && _document is null)
            {
                page.Asset("Modules/Reader/reader.entry.ts");
                page.Content.Add(PdfView(pdf));
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
                BookmarkTool(),
                Tool("pencil", "reader.notes", "notes").Class("button button--icon reader__desktop-tool"),
                new Button(new Icon("headphones", assets)).IconOnly(texts["speech.title"]).Attribute("data-speech-toggle", "").Attribute("aria-pressed", "false").Attribute("disabled", "disabled"),
                Tool("search", "reader.search", "search").Class("button button--icon reader__desktop-tool"),
                new Button(new Icon("text-select", assets)).IconOnly(texts["reader.selectText"]).Attribute("data-reader-select", "").Attribute("aria-pressed", "false"),
                Tool("player-more", "reader.more", "more")
            ], Chapters(index, document.SectionCount));

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
            var settings = Settings(false);
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
                .Attribute("data-bookmarks-url", "/api/v1/reading/assets/" + asset + "/bookmarks")
                .Attribute("data-bookmark-delete-label", texts["reader.bookmarkDelete"])
                .Attribute("data-annotations", JsonSerializer.Serialize(_marks, Json))
                .Attribute("data-note-label", texts["reader.note"])
                .Attribute("data-query", Query ?? "")
                .Attribute("data-page-label", texts["reader.pagePosition"])
                .Attribute("data-animation-failed", texts["reader.animationUnavailable"]);
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
        }

        // The browser's own PDF viewer; the link serves browsers without one.
        private IUiComponent PdfView(ReadingSourceV1 source)
        {
            var file = "/api/v1/reading/assets/" + Id.ToString(CultureInfo.InvariantCulture) + "/file";
            return new ReaderFrame(assets, texts,
                Header(source.Title, "", source.WorkId, [
                    new A(new Text("reader.openPdf")).Class("button").Attribute("href", file).Attribute("target", "_blank").Attribute("rel", "noopener")
                ]), new Span(), new Frame().Class("reader__pdf").Attribute("src", file).Attribute("title", source.Title),
                _status is { } status ? new P(new Text(status)).Class("reader__note").Attribute("role", "status") : new Span()
            ).Class("reader reader--pdf").Attribute("data-format", "pdf");
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
            var top = Header(document.Title, "", document.WorkId, [AppearanceTool(true), BookmarkTool(), Tool("player-more", "reader.more", "more")]);
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
            return new ReaderFrame(assets, texts, top, contents, stage, bottom, Settings(true), More(false, false),
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

        // Marks or clears the current place; the script keeps the pressed state with the page.
        private IUiComponent BookmarkTool() => new Button(new Icon("bookmark", assets)).IconOnly(texts["reader.bookmark"]).Class("button button--icon reader__desktop-tool")
            .Attribute("data-reader-bookmark", "").Attribute("aria-pressed", "false");

        private IUiComponent AppearanceTool(bool images) => new Button(new Span(new ValueText("Aa")).Class("reader__type-icon"))
            .IconOnly(texts["reader.appearance"]).Attribute("data-reader-open-settings", images ? "all" : "text").Attribute("aria-expanded", "false");

        private IUiComponent Chapters(int index, int count) => new Nav(
            index > 0 ? new A(new Icon("chevron-left", assets)).Class("button button--icon").Attribute("aria-label", texts["reader.previous"])
                .Attribute("href", "?s=" + (index - 1).ToString(CultureInfo.InvariantCulture)).Attribute("data-role", "previous") : new Span(),
            new Span(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["reader.chapterPosition"], index + 1, count))),
            index + 1 < count ? new A(new Icon("chevron-left", assets)).Class("button button--icon reader__turn-next").Attribute("aria-label", texts["reader.next"])
                .Attribute("href", "?s=" + (index + 1).ToString(CultureInfo.InvariantCulture)).Attribute("data-role", "next") : new Span())
            .Class("reader__chapter-navigation").Attribute("aria-label", texts["reader.chapterNavigation"]);

        private IUiComponent More(bool text, bool download) => new Div(
            new Div(new H2(new Text("reader.more")), new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("data-reader-close", "more")).Class("reader__panel-heading"),
            text ? new Button(new Icon("search", assets), new Text("reader.search")).Class("button reader__menu-item reader__mobile-item").Attribute("data-reader-open-panel", "search").Attribute("aria-expanded", "false") : new Span(),
            text ? new Button(new Icon("bookmark", assets), new Text("reader.notes")).Class("button reader__menu-item reader__mobile-item").Attribute("data-reader-open-panel", "notes").Attribute("aria-expanded", "false") : new Span(),
            new Button(new Icon("bookmark", assets), new Text("reader.bookmark")).Class("button reader__menu-item reader__mobile-item").Attribute("data-reader-bookmark", "").Attribute("aria-pressed", "false"),
            text ? new Button(new Icon("headphones", assets), new Text("speech.settings")).Class("button reader__menu-item").Attribute("data-reader-open-panel", "speech").Attribute("aria-expanded", "false") : new Span(),
            new Button(new Icon("settings", assets), new Text("reader.settings")).Class("button reader__menu-item").Attribute("data-reader-open-settings", "all").Attribute("aria-expanded", "false"),
            new Button(new Icon("player-theater", assets), new Text("reader.theater")).Class("button reader__menu-item reader__mobile-item").Attribute("data-reader-command", "theater").Attribute("aria-pressed", "false"),
            new Button(new Icon("player-fullscreen", assets), new Text("player.fullscreen")).Class("button reader__menu-item reader__mobile-item").Attribute("data-reader-command", "fullscreen").Attribute("aria-pressed", "false"),
            download ? new Button(new Icon("download", assets), new Text("reader.download")).Class("button reader__menu-item").Attribute("data-action", "download").Attribute("aria-expanded", "false") : new Span())
            .Class("reader__panel reader__more").Attribute("data-panel", "more").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.more"]);

        private IUiComponent Settings(bool images) => new Div(
            new Div(new H2(new Text("reader.settings")), new Button(new Icon("close", assets)).IconOnly(texts["common.close"])
                .Attribute("data-reader-close", "settings")).Class("reader__panel-heading"),
            SettingsGroup("navigation", "reader.settings.navigation", group => group
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
            SettingsGroup("paper", "reader.settings.paper", group => group
                .Select(data => data.Surface, "reader.surface", [new("clean", texts["reader.surface.clean"]), new("paper", texts["reader.surface.paper"])])
                .Color(data => data.Colour, "reader.colour")),
            SettingsGroup("motion", "reader.settings.motion", group => group
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
            var panel = new List<IUiComponent> { Bookmarks(false) };
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

            _bookmarks = await readBookmarks.ExecuteAsync<BaseDataList<ReadingBookmarkV1>>(new ReadingAssetParametersV1(Id), ct);
            if (_document.ReadingFormatTypeId == ReadingFormatType.Comic)
            {
                return;
            }

            var profile = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: ct)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            _speech = await readSpeech.ExecuteAsync<SpeechPreferencesV1>(new ReadProfileParametersV1(profile), ct);
            _speechModels = await readSpeechModels.ExecuteAsync<BaseDataList<SpeechModelV1>>(new SpeechModelsParametersV1(), ct);

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
