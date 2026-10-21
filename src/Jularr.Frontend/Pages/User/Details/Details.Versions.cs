using System.Globalization;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.User.Details
{
    // The files of a title: its versions, editions and platforms, the table of what is here, and the choice the primary action opens.
    public sealed partial class DetailsModel
    {
        // One choice of the primary action: a film's file, a book's edition, a game's platform.
        private sealed record Variant(string Key, string Label, IReadOnlyList<WorkAvailabilityV1> Items);

        // The openable files grouped by what the profile may choose between; fewer than two groups are no choice.
        private IReadOnlyList<Variant> Variants(WorkKind kind)
        {
            var open = (_availability?.Items ?? []).Where(CanOpen).ToArray();
            switch (kind)
            {
                case WorkKind.Film:
                    return [.. open.Where(item => item.WorkUnitId is null).Select(item => new Variant(item.Id.ToString(CultureInfo.InvariantCulture), FileName(item), [item]))];
                case WorkKind.Game:
                    return [.. open.Where(item => item.PlatformName is not null).GroupBy(item => item.PlatformName!).Select(group => new Variant(group.Key, group.Key, [.. group]))];
                default:
                    return [.. open.GroupBy(item => item.WorkEditionId).Select(group => new Variant(group.Key.ToString(CultureInfo.InvariantCulture), EditionName(_editions.FirstOrDefault(edition => edition.Id == group.Key), group.Key), [.. group]))];
            }
        }

        // A film's file by its quality and its cut.
        private string FileName(WorkAvailabilityV1 item)
        {
            var cut = _editions.FirstOrDefault(edition => edition.Id == item.WorkEditionId)?.Name;
            return string.Join(" · ", new[] { item.Height is { } height ? Quality(height) : texts["details.wholeWork"], cut }.OfType<string>());
        }

        // An edition by its name, else by its language and format.
        private string EditionName(WorkEditionV1? edition, long id)
        {
            var named = edition?.Name ?? string.Join(" · ", new[] { edition?.LanguageCode is { } code ? LanguageLabel.Name(code) ?? code : null,
                edition?.EditionFormatId is { } format ? texts["details.editionFormat." + format] : null }.OfType<string>());
            return named.Length > 0 ? named : texts["details.edition"] + " " + id.ToString(CultureInfo.InvariantCulture);
        }

        // Editions worth a tab of their own: more than the plain one every title has.
        private IReadOnlyList<WorkEditionV1> Editions(WorkKind kind) =>
            _editions.Count >= 2 || _editions.Any(edition => edition.Name is not null || edition.EditionFormatId is not null || edition.Publisher is not null) ? _editions : [];

        // Edition cards: its name and where it is here, the publisher and year, its language and format; one with files can be chosen for the primary action.
        private IUiComponent EditionCards(IReadOnlyList<WorkEditionV1> editions, bool preview)
        {
            var selected = Selected(Variants(Kind(_work!)));
            return new Div([.. (preview ? editions.Take(3) : editions).Select(edition =>
            {
                var chips = new List<IUiComponent>();
                if (edition.LanguageCode is { } code) chips.Add(new Span(new ValueText(LanguageLabel.Name(code) ?? code)).Class("details-chip"));
                if (edition.EditionFormatId is { } format) chips.Add(new Span(new Text("details.editionFormat." + format)).Class("details-chip details-chip--accent"));
                var facts = string.Join(" · ", new[] { edition.Publisher, edition.ReleaseYear?.ToString(CultureInfo.InvariantCulture) }.OfType<string>());
                var state = edition.IsAvailable ? Chip(new Text("details.state.local"), UiTone.Success, "check") : Chip(new Text("details.state.notLocal"), UiTone.Warning, "cloud");
                var body = new IUiComponent[] { new Div(new Strong(new Truncated(EditionName(edition, edition.Id))), state).Class("details-edition__head"),
                    facts.Length > 0 ? new Span(new Truncated(facts)).Class("details-edition__facts") : new Span(), new Div([.. chips]).Class("details-chips") };
                var picked = selected?.Key == edition.Id.ToString(CultureInfo.InvariantCulture);
                HtmlElement card = edition.IsAvailable ? new A(body).Attribute("href", Address(query => { query["tab"] = "editions"; query["version"] = edition.Id.ToString(CultureInfo.InvariantCulture); }))
                    : new Div(body);
                return (IUiComponent)card.Class(picked ? "details-edition details-edition--picked" : "details-edition");
            })]).Class("details-editions");
        }

        // The versions of a film or game, the files of the rest: what is here, with the languages a film's own chips show; one page of them at a time.
        private IUiComponent Files(WorkDetailsV1 work, WorkKind kind, bool preview)
        {
            var items = _availability?.Items ?? [];
            var parts = new List<IUiComponent>();
            if (kind == WorkKind.Game && _platforms is { Count: > 0 } platforms) parts.Add(PlatformCards(work, platforms));
            if (items.Count == 0) return new Div([.. parts]);

            if (preview) return new Div([.. parts, VersionTable(kind, [.. items.Take(5)])]);

            // Stored inventory, no storage access; named sections split it where staff defined them.
            var start = 0;
            for (var index = 1; index <= items.Count; index++)
            {
                if (index < items.Count && items[index].GroupName == items[start].GroupName) continue;
                if (items[start].GroupName is { } name) parts.Add(new H3(new ValueText(name)));
                parts.Add(VersionTable(kind, [.. items.Skip(start).Take(index - start)]));
                start = index;
            }

            // One bounded inventory page.
            var paging = new List<IUiComponent>();
            if (PageNumber > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(query => { query["tab"] = "files"; query["p"] = (PageNumber - 1).ToString(CultureInfo.InvariantCulture); })));
            if (items.Count == _availability!.PageSize) paging.Add(new A(new Text("common.next")).Class("button").Attribute("href", Address(query => { query["tab"] = "files"; query["p"] = (PageNumber + 1).ToString(CultureInfo.InvariantCulture); })));
            if (paging.Count > 0) parts.Add(new Nav([.. paging]).Class("details-paging").Attribute("aria-label", texts["details.pages"]));
            return new Div([.. parts]).Class("details-files");
        }

        // The platforms a game is on, the ones that are here marked, and the one the primary action opens.
        private IUiComponent PlatformCards(WorkDetailsV1 work, IReadOnlyList<GamePlatformV1> platforms)
        {
            var selected = Selected(Variants(WorkKind.Game));
            return new Div([.. platforms.Select(platform =>
            {
                var local = work.Platforms.Contains(platform.Name, StringComparer.OrdinalIgnoreCase);
                var body = new IUiComponent[] { new Div(new Strong(new Truncated(platform.Name)), local ? Chip(new Text("details.state.local"), UiTone.Success, "check") : Chip(new Text("details.state.notLocal"), UiTone.Warning, "cloud")).Class("details-edition__head"),
                    platform.Abbreviation is { } abbreviation ? new Span(new Truncated(abbreviation)).Class("details-edition__facts") : new Span() };
                HtmlElement card = local ? new A(body).Attribute("href", Address(query => { query["tab"] = "files"; query["version"] = platform.Name; })) : new Div(body);
                return (IUiComponent)card.Class(local && selected?.Key == platform.Name ? "details-edition details-edition--picked" : "details-edition");
            })]).Class("details-editions");
        }

        private Variant? Selected(IReadOnlyList<Variant> variants) => variants.FirstOrDefault(variant => variant.Key == VersionKey) ?? variants.FirstOrDefault();

        // The files of one table: what each is, its quality, languages, size and state, and the way to open it.
        private IUiComponent VersionTable(WorkKind kind, IReadOnlyList<WorkAvailabilityV1> items)
        {
            var table = new Table<WorkAvailabilityV1>(items).Compact();
            if (kind == WorkKind.Film) table.Content("details.version", item => new Truncated(FileName(item)));
            else
            {
                table.Content("details.unit", item => new Truncated(MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber, item.UnitTitle)
                    ?? (item.PlatformName is { } platform ? platform : texts["details.wholeWork"])));
                if (items.Select(item => item.MediaAssetTypeId).Distinct().Count() > 1) table.Resource(item => item.MediaAssetTypeId, "table.type", type => "media.asset." + type);
            }

            // A column that no file has anything for is left out.
            if (items.Any(item => item.Height is not null)) table.Content("details.quality", item => item.Height is { } height ? new Span(new ValueText(Quality(height))).Class("details-chip details-chip--accent") : new Span());
            if (items.Any(item => item.AudioLanguages.Length > 0)) table.Content("details.audio", item => LanguageChips(item.AudioLanguages));
            if (items.Any(item => item.SubtitleLanguages.Length > 0)) table.Content("details.subtitles", item => LanguageChips(item.SubtitleLanguages));
            return table
                .Content("table.size", item => new ValueText(ByteSize.Format(item.SizeBytes)))
                .Content("table.status", item => item.IsAvailable ? Chip(new Text("details.state.local"), UiTone.Success, "check") : Chip(new Text("library.missing"), UiTone.Warning, "cloud"))
                .Content("details.progress", item => IsReadable(item) && item.HasProgress
                    ? Chip(new Text(item.IsCompleted ? "progress.read" : "progress.started"), item.IsCompleted ? UiTone.Success : UiTone.Info, item.IsCompleted ? "check" : "play")
                    : new Span())
                .Content("common.actions", item => new Div(Play(item), Mark(item)).Class("details-row__actions"));
        }

        private static IUiComponent LanguageChips(string[] codes) => new Div([.. codes.Select(code => (IUiComponent)new Span(new ValueText(LanguageLabel.Name(code) ?? code)).Class("details-chip"))]).Class("details-chips");

        // A book's volume where the profile has progress: read or unread, or begun again.
        private IUiComponent Mark(WorkAvailabilityV1 item)
        {
            if (!IsReadable(item) || !item.HasProgress || item.WorkUnitId is not { } unit || !texts.CanReadMedia()) return new Span();
            return new Div(
                texts.CanRead() ? ProgressForm("progress.startOver", ProgressAction.Unwatched, unit, PlayHref(item), "history", ProgressStyle.Icon) : new Span(),
                ProgressForm(item.IsCompleted ? "progress.markUnread" : "progress.markRead", item.IsCompleted ? ProgressAction.Unwatched : ProgressAction.Watched, unit, null, "check",
                    ProgressStyle.Icon, pressed: item.IsCompleted)).Class("details-row__actions");
        }

        // Continue where this profile stopped.
        private IUiComponent Play(WorkAvailabilityV1 item)
        {
            if (!CanOpen(item)) return new Span();

            var resume = !IsReadable(item) && item.ResumePositionMs is > 0 && !item.IsCompleted ? ShortClock(item.ResumePositionMs.Value) : null;
            var label = resume is null ? new Text(PlayKey(item)) : (IUiComponent)new Span(new Text("media.continue"), new ValueText(" · " + resume));
            return new A(label).Class("button button--compact").Attribute("href", PlayHref(item));
        }

        private bool CanOpen(WorkAvailabilityV1 item) => item.IsAvailable && (item.MediaAssetTypeId == MediaAssetType.Game ? item.IsEmulated && texts.CanPlayGames()
            : IsReadable(item) ? texts.CanRead() : texts.CanPlay());

        private static string PlayKey(WorkAvailabilityV1 item) => IsReadable(item) ? "details.read"
            : item.IsCompleted ? "details.playAgain" : item.ResumePositionMs is > 0 ? "media.continue" : "details.play";

        private static string PlayHref(WorkAvailabilityV1 item) => (item.MediaAssetTypeId == MediaAssetType.Game ? "/Game/" : IsReadable(item) ? "/Read/" : "/Player/")
            + item.Id.ToString(CultureInfo.InvariantCulture);

        private static bool IsReadable(WorkAvailabilityV1 item) => ReadableMedia.IsReadable(item.MediaAssetTypeId);
    }
}
