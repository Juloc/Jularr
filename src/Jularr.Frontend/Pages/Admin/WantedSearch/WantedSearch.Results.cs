using System.Globalization;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Wanted;
using Jularr.Frontend.Pages.Admin.Work;

namespace Jularr.Frontend.Pages.Admin.WantedSearch
{
    public sealed partial class WantedSearchModel
    {
        private string Address => WantedStatus.Home(Id) + "/Search";

        // The dialog over the page that opened it: the title and the form that searches again in its head, the panel as its body.
        // The X takes the first focus, as in every Modal; else it would fall on the (i) of the season, whose tip would open over the head by itself.
        private IUiComponent Dialog(WantedDetailsV1 item, string? csrf, string? returnUrl) =>
            new Framework.Dialog(
                new Div(new Div(new H2(new ValueText(item.CanonicalTitle)).Attribute("id", "wanted-search-title")).Class("search-dialog__title"),
                    _sources.Count == 0 ? new Span() : ScopeForm(item, csrf),
                    new Button(new Icon("close", assets, 18)).IconOnly(texts["common.close"]).Attribute("data-dialog-close", "").Attribute("autofocus", "autofocus")).Class("modal__head search-dialog__head"),
                new Div(Panel(item, csrf, returnUrl, false)).Class("modal__body"))
                .Class("modal search-dialog search-host").Attribute("id", "wanted-search").Attribute("aria-labelledby", "wanted-search-title");

        // The scope of a series or a manga (season and episode, or the volume) and the button that asks every source again.
        private IUiComponent ScopeForm(WantedDetailsV1 item, string? csrf) =>
            new Div(new Form<SearchWantedDataV1>(form =>
            {
                // An empty field searches all of it (the whole season, series or Manga), so it reads "All" rather than an example number that looks entered.
                var all = new FieldHelp(Placeholder: "common.all");
                if (item.MediaTypeId == MediaType.Series)
                {
                    form.Number(data => data.ParentUnitNumber, "wanted.search.season", all);
                    form.Number(data => data.UnitNumber, "wanted.search.episode", all);
                }
                else if (item.MediaSubtypeId == MediaSubtype.Manga)
                {
                    form.Number(data => data.UnitNumber, "wanted.search.volume", all);
                }

                // With script the button always reads "Search again"; without it the search is a start.
                var again = new Button(new Icon("sync", assets, 16), new Span(new Text("wanted.search.again")).Class("search-scope__again"), new Span(new Text("wanted.search.start")).Class("search-scope__start")).Primary();
                form.Submit(again);
            }, Address + "?handler=Search", csrf, _search ?? new SearchWantedDataV1())).Class("search-scope");

        // The page's panel: the form, then the sources and their releases. Without script a search of every source fills the one table.
        private IUiComponent Panel(WantedDetailsV1 item, string? csrf, string? returnUrl, bool page)
        {
            if (_sources.Count == 0) return new P(new Text("wanted.search.noSources")).Attribute("role", "status");
            var panel = new Div(
                new Div(new Progress().Attribute("max", Count(_sources.Count)).Attribute("value", _candidates is null ? "0" : Count(_sources.Count)).TextAttribute("aria-label", "wanted.search.progress"),
                    new P(new ValueText(_candidates is null ? Format("wanted.search.running", 0, Counted("wanted.search.sourceCount", _sources.Count), _sources.Count)
                        : Format("wanted.search.finished", Counted("wanted.search.releaseCount", _candidates.Count), Counted("wanted.search.sourceCount", _sources.Count))))
                        .Attribute("role", "status").Attribute("data-search-status", "")).Class("search-panel__status"),
                _candidates is null ? Chips() : new Span(),
                _candidates is null ? Filters() : new Span(),
                new P(new Text("wanted.search.noMatch")).Class("search-panel__empty").Attribute("role", "status").Attribute("data-search-nomatch", "").Attribute("hidden", "hidden"),
                new P(new Text("wanted.search.empty")).Class("search-panel__empty").Attribute("role", "status").Attribute("data-search-empty", "").Attribute("hidden", "hidden"),
                Results(item, csrf, returnUrl),
                _candidates is null ? Icons() : new Span()).Class("search-panel");
            if (_candidates is not null) return page ? new Div(ScopeForm(item, csrf), panel).Class("search-host") : panel;
            panel.Attribute("data-search-panel", "").Attribute("data-source-url", Address + "?handler=Source").Attribute("data-total", Count(_sources.Count))
                .Attribute("data-return-url", returnUrl ?? "").Attribute("aria-busy", "true")
                .TextAttribute("data-failed-text", "wanted.search.source.failed").TextAttribute("data-running", "wanted.search.running").TextAttribute("data-finished", "wanted.search.finished")
                .TextAttribute("data-finished-failed", "wanted.search.finishedFailed").TextAttribute("data-filtered", "wanted.search.filtered").TextAttribute("data-filtered-one", "wanted.search.filtered.one")
                .TextAttribute("data-releases", "wanted.search.releaseCount").TextAttribute("data-releases-one", "wanted.search.releaseCount.one")
                .TextAttribute("data-sources", "wanted.search.sourceCount").TextAttribute("data-sources-one", "wanted.search.sourceCount.one");
            return page ? new Div(ScopeForm(item, csrf), panel).Class("search-host") : panel;
        }

        // The sources as chips: each shows its state while it searches and then its number of releases or why it gave none; a click shows only its releases.
        private IUiComponent Chips() => new Div([.. _sources.Select(source => (IUiComponent)new Button(
            new Span().Class("search-chip__state"),
            new Span(new ValueText(source.Name)).Class("search-chip__name"),
            new Span().Class("search-chip__result"))
            .Class("search-chip").Attribute("aria-pressed", "false").Attribute("data-source", Count(source.Id)).Attribute("data-state", "loading"))])
            .Class("search-panel__chips").Attribute("role", "group").TextAttribute("aria-label", "wanted.search.sources");

        // What a person narrows and orders the loaded releases by; the script applies it to the rows, nothing is asked again. The language list grows with what the sources found.
        private IUiComponent Filters()
        {
            static IUiComponent Choice(string value, string labelKey) => new Option(new Text(labelKey)).Attribute("value", value);
            IUiComponent Menu(string name, string labelKey, IUiComponent[] options) => new Select(options).Class("search-filter").Attribute("data-filter", name).TextAttribute("aria-label", labelKey);
            var clear = new Button(new Icon("close", assets, 14), new Span(new Text("wanted.search.filters.clear"))).Compact().Soft();
            clear.Attribute("data-filters-clear", "").Attribute("disabled", "disabled");
            var title = new Input().Attribute("type", "search").Attribute("autocomplete", "off").Attribute("data-filter", "title").Class("search-box__input search-filter-title")
                .TextAttribute("placeholder", "wanted.search.filter.title").TextAttribute("aria-label", "wanted.search.filter.title");

            // The qualities are not known before the releases are; the script lists what was found as checkboxes in the popup.
            var qualityButton = new Button(new Span().Class("ui-dropdown__value"));
            qualityButton.Attribute("type", "button").Attribute("aria-haspopup", "true").Attribute("aria-expanded", "false").Attribute("data-multi-button", "")
                .Class("search-filter ui-dropdown__button").TextAttribute("aria-label", "wanted.search.filter.quality");
            var quality = new Div(qualityButton, new Div().Class("ui-dropdown__popup search-multi__popup").Attribute("popover", "auto").Attribute("role", "group").Attribute("data-multi-popup", "")
                .TextAttribute("aria-label", "wanted.search.filter.quality")).Class("search-multi").Attribute("data-multi", "quality")
                .TextAttribute("data-all", "wanted.search.filter.quality").TextAttribute("data-some", "wanted.search.quality.some").TextAttribute("data-unknown", "wanted.search.quality.unknown");
            return new Div(
                title,
                Menu("language", "wanted.search.filter.language", [Choice("", "wanted.search.filter.language")]),
                Menu("protocol", "wanted.search.filter.protocol", [Choice("", "wanted.search.filter.protocol"), .. Enum.GetNames<ReleaseProtocol>().Select(name => Choice(name, "wanted.search.protocol." + name))]),
                Menu("age", "wanted.search.filter.age", [Choice("", "wanted.search.filter.age"), .. new[] { 1, 7, 30, 90, 365 }.Select(days => Choice(Count(days), "wanted.search.age." + Count(days)))]),
                Menu("size", "wanted.search.filter.size", [Choice("", "wanted.search.filter.size"), .. Enumerable.Range(0, 5).Select(range => Choice(Count(range), "wanted.search.size." + Count(range)))]),
                quality,
                Menu("decision", "wanted.search.filter.decision", [Choice("", "wanted.search.filter.decision"), .. Enum.GetNames<ReleaseDecisionType>().Select(name => Choice(name, "acquisition.decision." + name))]),
                Menu("sort", "wanted.search.sort", [Choice("source", "wanted.search.sort.source"), Choice("score", "wanted.search.sort.score"), Choice("newest", "wanted.search.sort.newest"),
                    Choice("largest", "wanted.search.sort.largest"), Choice("smallest", "wanted.search.sort.smallest")]),
                clear).Class("search-filters").Attribute("role", "group").TextAttribute("aria-label", "wanted.search.filters");
        }

        // The small marks a chip takes when its source has answered.
        private IUiComponent Icons() => new Template(new Span(new Icon("check", assets, 14)).Attribute("data-icon", "ok"), new Span(new Icon("alert", assets, 14)).Attribute("data-icon", "failed"));

        private IUiComponent Results(WantedDetailsV1 item, string? csrf, string? returnUrl)
        {
            var head = new Thead(new Tr(
                Head("wanted.search.release"), Head("wanted.search.source"), Head("wanted.search.quality"), Head("wanted.search.size"),
                Head("wanted.search.age"), Head("wanted.search.score"), Head("wanted.search.decision"), Head("common.actions")));
            IUiComponent[] bodies = _candidates is { } candidates
                ? [new Tbody([.. candidates.Select((candidate, index) => Row(candidate, csrf, returnUrl, 0, index))])]
                : [.. _sources.Select(Skeleton)];
            return new Div(new TableElement([head, .. bodies]).Class("page-table search-table")).Class("page-table-wrap page-table-wrap--stack search-panel__results");
        }

        private static IUiComponent Head(string key) => new Th(new Text(key));

        // Two lines where the releases of a running source will appear.
        private static IUiComponent Skeleton(WantedSearchSourceV1 source) => new Tbody([.. Enumerable.Range(0, 2).Select(_ => (IUiComponent)new Tr(new Td(new Div(
            new Span().Class("search-skeleton__bar search-skeleton__bar--long"), new Span().Class("search-skeleton__bar")).Class("search-skeleton"))
            .Attribute("colspan", "8")).Class("search-row search-row--skeleton"))])
            .Attribute("data-source", Count(source.Id)).Attribute("data-state", "loading").Attribute("aria-hidden", "true");

        // The answer of one source: its rows and their number.
        private IUiComponent SourceBody(long connectionId, BaseDataList<WantedReleaseCandidateV1> candidates, string? csrf, string? returnUrl) =>
            new Tbody([.. candidates.Select((candidate, index) => Row(candidate, csrf, returnUrl, connectionId, index))]).Attribute("data-source", Count(connectionId)).Attribute("data-state", "done").Attribute("data-count", Count(candidates.Count));

        // A source that failed has no rows; the chip says why in short, and in full when hovered.
        private IUiComponent SourceFailure(long connectionId, ServiceError error)
        {
            var key = error.Code switch
            {
                "acquisition_query_limit_reached" or "acquisition_limit_reached" or "acquisition_rate_limited" => "wanted.search.source.limit",
                "source_timeout" => "wanted.search.source.timeout",
                "acquisition_unavailable" => "wanted.search.source.unavailable",
                _ => "wanted.search.source.failed"
            };
            var detail = texts[error.FieldResourceKey ?? error.ResourceKey] + (error.Detail is { } more ? " " + more : "");
            return new Tbody().Attribute("data-source", Count(connectionId)).Attribute("data-state", "failed").Attribute("data-label", texts[key]).Attribute("data-detail", detail);
        }

        // Every candidate stays visible with its decision and reason; a rejected one is grey and can only be taken through its warning menu. The score's rules and points show on hover.
        private IUiComponent Row(WantedReleaseCandidateV1 item, string? csrf, string? returnUrl, long sourceId, int index)
        {
            var rejected = item.ReleaseDecisionTypeId == ReleaseDecisionType.Rejected;
            var facts = new List<IUiComponent>();
            if (item.ReleaseGroup is { } group) facts.Add(new ValueText(group));
            if (item.BookReleaseKindId is { } kind) facts.Add(new Text("acquisition.kind." + kind));
            if (Volumes(item) is { } volumes) facts.Add(volumes);
            if (item.Languages.Select(WorkLanguages.Code).Distinct().ToArray() is { Length: > 0 } languages) facts.Add(new Span(new ValueText(string.Join(", ", languages.Select(language => WorkLanguages.Name(language)))), item.LanguagesFromTitle ? new Text("wanted.search.languageFromTitle") : new Span()));
            var reason = item.ReasonValue is { } value ? new Span(new Text("acquisition.reason." + item.ReasonCode), new ValueText(" · " + value)) : new Span(new Text("acquisition.reason." + item.ReasonCode));
            return new Tr(
                new Td(new Span(new ValueText(item.Title)).Class("search-row__title"), facts.Count == 0 ? new Span() : new Span([.. Joined(facts)]).Class("search-row__facts")).Class("search-row__release"),
                Cell("wanted.search.source", new ValueText(item.ConnectionName)),
                Cell("wanted.search.quality", new ValueText(item.QualityKey ?? "")),
                Cell("wanted.search.size", Size(item)),
                Cell("wanted.search.age", new ValueText(ReleaseFacts.AgeDays(item.PublishedAt))),
                Cell("wanted.search.score", Score(item)),
                Cell("wanted.search.decision", new Div(new StateChip(new Text("acquisition.decision." + item.ReleaseDecisionTypeId), Tone(item.ReleaseDecisionTypeId)), reason.Class("search-row__reason"))),
                new Td(rejected ? Override(item, csrf, returnUrl) : Grabs(item, csrf, returnUrl)).Class("search-row__actions"))
                .Class("search-row" + (rejected ? " search-row--rejected" : "")).Attribute("data-decision", item.ReleaseDecisionTypeId.ToString())
                .Attribute("data-source", Count(sourceId)).Attribute("data-index", Count(index)).Attribute("data-title", item.Title).Attribute("data-quality", item.QualityKey ?? "").Attribute("data-score", Count(item.Score))
                .Attribute("data-size", item.SizeBytes is { } size ? Count(size) : "").Attribute("data-published", item.PublishedAt is { } published ? new DateTimeOffset(published, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : "")
                .Attribute("data-protocol", item.ReleaseProtocolId.ToString()).Attribute("data-languages", string.Join(';', item.Languages.Select(WorkLanguages.Code).Distinct().Select(code => code + "=" + WorkLanguages.Name(code))));
        }

        private static IEnumerable<IUiComponent> Joined(List<IUiComponent> facts)
        {
            for (var index = 0; index < facts.Count; index++)
            {
                if (index > 0) yield return new ValueText(" \u00b7 ");
                yield return facts[index];
            }
        }

        private IUiComponent Cell(string labelKey, IUiComponent content) => new Td(content).Attribute("data-label", texts[labelKey]);

        private static UiTone Tone(ReleaseDecisionType decision) => decision switch { ReleaseDecisionType.Eligible => UiTone.Success, ReleaseDecisionType.ManualReview => UiTone.Warning, _ => UiTone.Neutral };

        // In queue goes to the end of the client's queue; start now goes before the others.
        private IUiComponent Grabs(WantedReleaseCandidateV1 item, string? csrf, string? returnUrl) => new Div(
            Grab(item, csrf, returnUrl, "wanted.search.queue", "download", false, false),
            Grab(item, csrf, returnUrl, "wanted.search.startNow", "play", true, false)).Class("search-row__buttons");

        private IUiComponent Grab(WantedReleaseCandidateV1 item, string? csrf, string? returnUrl, string labelKey, string icon, bool startsNow, bool overrides)
        {
            var button = new Button(new Icon(icon, assets, 16), new Span(new Text(labelKey))).Compact().Submit();
            if (overrides) button.Warn(); else if (startsNow) button.Primary(); else button.Soft();
            return new PostForm(Address + "?handler=Grab", csrf,
                [("candidateId", item.CandidateId), ("startsNow", startsNow ? "true" : "false"), ("overridesRejection", overrides ? "true" : "false"), ("returnUrl", returnUrl ?? "")], button);
        }

        // A release the profile rejected is taken only on purpose: the menu says why it was rejected before it offers the grab.
        private IUiComponent Override(WantedReleaseCandidateV1 item, string? csrf, string? returnUrl) => new ActionMenu(new Icon("dots", assets, 18),
            [new P(new Text("wanted.search.overrideWarning")).Class("search-row__warning"), Grab(item, csrf, returnUrl, "wanted.search.override", "alert", false, true)],
            "button button--compact button--icon", "wanted.search.overrideMenu");

        // The sum, with every rule and its points on hover.
        private IUiComponent Score(WantedReleaseCandidateV1 item)
        {
            var score = new Span(new ValueText(item.Score.ToString(CultureInfo.CurrentCulture)));
            if (item.ScorePoints.Count == 0) return score;

            // A rule's text names where what it saw goes ({0}, "Group LuCaZ (good)"), else it follows the text.
            var lines = item.ScorePoints.Select(point => Rule(texts["acquisition.points." + point.Code], point.Value) + ": " + point.Points.ToString("+0;-0;0", CultureInfo.CurrentCulture));
            return score.Attribute("title", string.Join("\n", lines));
        }

        private static string Rule(string text, string? value) => value is null ? text
            : text.Contains("{0}", StringComparison.Ordinal) ? string.Format(CultureInfo.CurrentCulture, text, value) : text + " " + value;

        private static IUiComponent? Volumes(WantedReleaseCandidateV1 item)
        {
            var range = item.VolumeStart is not { } start ? null
                : item.VolumeEnd is { } end ? start.ToString(CultureInfo.CurrentCulture) + "–" + end.ToString(CultureInfo.CurrentCulture) : start.ToString(CultureInfo.CurrentCulture);
            return (range, item.IsComplete) switch
            {
                (null, false) => null,
                (null, true) => new Text("wanted.search.complete"),
                (_, false) => new ValueText(range),
                _ => new Span(new ValueText(range + " · "), new Text("wanted.search.complete"))
            };
        }

        // An outlier of its kind's size per volume is marked, not silent.
        private static IUiComponent Size(WantedReleaseCandidateV1 item)
        {
            var size = new ValueText(ReleaseFacts.Size(item.SizeBytes));
            return item.ReleaseSizeMarkId switch
            {
                ReleaseSizeMark.AboveMaximum or ReleaseSizeMark.BelowMinimum => new Span(size, new ValueText(" "), new StateChip(new Text("acquisition.size." + item.ReleaseSizeMarkId), UiTone.Warning)),
                ReleaseSizeMark.Unknown => new Span(size, new ValueText(" "), new StateChip(new Text("acquisition.size.Unknown"), UiTone.Neutral)),
                _ => size
            };
        }

        private static string Count(long number) => number.ToString(CultureInfo.InvariantCulture);

        private string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, texts[key], values);

        // "1 release" or "31 releases": the singular has its own text, since languages differ in more than an "s".
        private string Counted(string key, int count) => count == 1 ? texts[key + ".one"] : Format(key, count);
    }
}
