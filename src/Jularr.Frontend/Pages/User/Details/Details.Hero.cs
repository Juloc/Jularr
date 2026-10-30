using System.Globalization;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.User.Details
{
    public sealed partial class DetailsModel
    {
        // The hero: a plain poster, identity and longer summary at the top, with actions and progress aligned at the bottom.
        private IUiComponent Hero(WorkDetailsV1 work, Page page)
        {
            var kind = Kind(work);
            var items = _availability?.Items ?? [];
            var variants = Variants(kind);
            var selected = Selected(variants);
            var playable = Pick(selected is not null ? selected.Items : items);

            // Plex plays what Jularr cannot play here.
            var plex = work.PlexUrl is not null && (!texts.CanPlay() || items.Any(item => item.IsAvailable) is not true) ? work.PlexUrl : null;
            var facts = string.Join(" · ", new[] { work.ReleaseYear?.ToString(CultureInfo.InvariantCulture), work.RuntimeMinutes is { } minutes ? string.Format(CultureInfo.CurrentCulture, texts["details.minutes"], minutes) : null,
                texts.Genres(work.Genres), work.Studio, work.Platforms.Length > 0 ? string.Join(", ", work.Platforms) : null }.OfType<string>());

            var chips = new List<IUiComponent>
            {
                Chip(new Text(MediaTypeLabel.Key(work.MediaTypeId, work.MediaSubtypeId)), UiTone.Accent, TypeIcon(kind, work)),
                new MediaAvailability(assets, work.AvailabilityStateId, work.AvailableUnits, work.MonitoredUnits).Badge(style: MediaAvailabilityBadgeStyle.Inline)
            };
            if (work.ProgressStateId == ProgressStateType.Finished && FinishedKey(kind) is { } finished) chips.Add(Chip(new Text(finished), UiTone.Success, "check"));

            var hero = new MediaHero(work.CanonicalTitle, work.BackdropUrl, facts.Length == 0 ? null : facts, work.Overview)
                .Poster(work.PosterUrl, new Icon(TypeIcon(kind, work), assets, 48))
                .Chips([.. chips])
                .Actions([.. Actions(work, kind, variants, selected, playable, plex, page)]);
            if (work.Rating is { } rating)
            {
                hero.Rating(new Span(new Icon("star", assets, 13), new ValueText(rating.ToString("0.0", CultureInfo.CurrentCulture)),
                    RatingSource(work.RatingSource) is { } source ? new Small(new ValueText(source)) : new Span()).Class("media-hero__rating"));
            }

            if (work.ProgressStateId != ProgressStateType.NotStarted && kind != WorkKind.Game) hero.Progress(work.ProgressPercent, texts[ProgressKey(kind)], ProgressValue(work, kind));
            return hero;
        }

        // The file that plays next: the one the profile began, else the first not finished, else the first.
        private WorkAvailabilityV1? Pick(IEnumerable<WorkAvailabilityV1> items)
        {
            var open = items.Where(CanOpen).ToArray();
            return open.FirstOrDefault(item => item.ResumePositionMs is > 0 && !item.IsCompleted) ?? open.FirstOrDefault(item => !item.IsCompleted) ?? open.FirstOrDefault();
        }

        private List<IUiComponent> Actions(WorkDetailsV1 work, WorkKind kind, IReadOnlyList<Variant> variants, Variant? selected, WorkAvailabilityV1? playable, string? plex, Page page)
        {
            var actions = new List<IUiComponent>();
            if (playable is not null)
            {
                actions.Add(new A(new Icon(PlayIconName(playable), assets, 16), new Span(PlayLabel(playable))).Class("button button--primary").Attribute("href", PlayHref(playable)));
            }
            else if (plex is not null)
            {
                actions.Add(new A(new Icon("link", assets, 16), new Span(new Text("media.openInPlex"))).Class("button button--primary").Attribute("href", plex));
            }

            // The profile's request names its phase live and leads to the requests.
            if (work.RequestStatusTypeId is { } requested && texts.CanRequest())
            {
                actions.Add(new LivePhaseButton(assets, LiveKeys.RequestOfWork(work.Id), requested, "/Requests"));
                page.Asset("Framework/Components/LiveProgress.entry.ts");
            }

            if (RequestMenu(work) is { } request) actions.Add(request);

            // Which version, edition or platform the primary action opens.
            if (variants.Count > 1)
            {
                // The button shows the choice itself (4K, an edition's language and format, a platform); the kind of choice is its label.
                var caption = kind switch { WorkKind.Film => "details.version", WorkKind.Game => "details.platform", _ => "details.edition" };
                actions.Add(new SelectLinkMenu(assets, [.. variants.Select(variant => new NavigationTab(variant.Label, Address(query => query["version"] = variant.Key), variant == selected, true))], caption, null, "glass"));
            }

            if (work.TrailerKey is { } trailer)
            {
                actions.Add(new A(new Icon("trailer", assets, 16), new Span(new Text("details.trailer"))).Class("button").Attribute("href", "https://www.youtube.com/watch?v=" + trailer)
                    .Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"));
            }

            actions.Add(new MediaBookmark(assets, work.Id, work.IsWatchlisted, Request.Path + Request.QueryString, _csrf));
            if (MoreMenu(work, kind) is { } more) actions.Add(more);
            return actions;
        }

        // The language, the platform of a game and the quality a request is made for, in a panel beside the primary action.
        private IUiComponent? RequestMenu(WorkDetailsV1 work)
        {
            if (_locales is not { } languages || !work.IsRequestable) return null;

            var options = languages.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
            var selected = languages.FirstOrDefault(locale => string.Equals(locale.Locale, texts.Locale, StringComparison.OrdinalIgnoreCase));
            var form = new Form<CreateRequestDataV1>(form =>
            {
                form.Select(item => item.UiLocaleId, "request.language", options);

                // A game is requested for one of its platforms.
                if (_platforms is { Count: > 0 } platforms)
                {
                    form.Select(item => item.GamePlatformId, "request.platform", platforms.Select(platform => new SelectOption(platform.Id.ToString(CultureInfo.InvariantCulture), platform.Name)));
                }

                // Qualities the request rule offers; none keeps the default.
                if (_requestProfiles is { Count: > 0 } qualities)
                {
                    form.Select(item => item.AcquisitionProfileId, "request.quality",
                        [new SelectOption("", texts["request.quality.default"]), .. qualities.Select(quality => new SelectOption(quality.Id.ToString(CultureInfo.InvariantCulture), quality.Name))]);
                }

                form.Submit("request.create");
            }, "/Details/" + work.Id.ToString(CultureInfo.InvariantCulture) + "?handler=Request", _csrf,
                _requestInput ?? (selected is null ? null : new CreateRequestDataV1(selected.Id)), _requestInput is null ? null : FormError);
            return new ActionMenu(new Span(new Icon("plus", assets, 16), new Span(new Text("details.request"))), [form], "button button--primary", null, FormError is not null && _requestInput is not null);
        }

        // Mark the title watched or unwatched or begin it again, and for an administrator the way to its management page.
        private IUiComponent? MoreMenu(WorkDetailsV1 work, WorkKind kind)
        {
            var menu = new List<IUiComponent>();
            if (texts.CanReadMedia() && kind != WorkKind.Game)
            {
                var (watched, unwatched, startOver) = kind switch
                {
                    WorkKind.Series or WorkKind.Anime => ("progress.series.markWatched", "progress.series.markUnwatched", "progress.series.startOver"),
                    WorkKind.Book or WorkKind.Manga or WorkKind.LightNovel => ("progress.markRead", "progress.markUnread", "progress.startOver"),
                    WorkKind.Music => ("progress.markHeard", "progress.markUnheard", "progress.startOver"),
                    _ => ("progress.markWatched", "progress.markUnwatched", "progress.startOver")
                };
                var started = work.ProgressStateId != ProgressStateType.NotStarted;
                var start = (_availability?.Items ?? []).FirstOrDefault(CanOpen);
                if (work.ProgressStateId != ProgressStateType.Finished) menu.Add(ProgressForm(watched, ProgressAction.Watched, null, null, "check", ProgressStyle.Menu));
                if (started) menu.Add(ProgressForm(unwatched, ProgressAction.Unwatched, null, null, "eye", ProgressStyle.Menu));
                if (started && start is not null) menu.Add(ProgressForm(startOver, ProgressAction.Unwatched, null, PlayHref(start), "history", ProgressStyle.Menu));
            }

            if (texts.CanManageRelations())
            {
                menu.Add(Modal.Opener(new Button(new Icon("link", assets, 16), new Span(new Text("details.relation.add"))).Class("action-menu__item"), RelationDialogId));
            }

            if (texts.CanManageMedia())
            {
                if (menu.Count > 0) menu.Add(new Hr().Class("action-menu__divider"));
                menu.Add(new A(new Icon("pencil", assets, 16), new Span(new Text("details.manage"))).Class("action-menu__item").Attribute("href", "/Admin/Works/" + work.Id.ToString(CultureInfo.InvariantCulture)));
            }

            return menu.Count == 0 ? null : new ActionMenu(new Icon("dots", assets, 18), menu, "button button--icon", "details.more");
        }

        // What the primary action says: play, continue (where) or read again, and for a unit which one.
        private IUiComponent PlayLabel(WorkAvailabilityV1 item)
        {
            if (!IsReadable(item) && item.ResumePositionMs is > 0 && !item.IsCompleted && item.WorkUnitTypeId is null)
            {
                return new Span(new Text("media.continue"), new ValueText(" · " + ShortClock(item.ResumePositionMs.Value)));
            }

            var unit = MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber);
            return new Span(new Text(PlayKey(item)), unit is null ? new Span() : new ValueText(" · " + unit));
        }

        private static string PlayIconName(WorkAvailabilityV1 item) => item.MediaAssetTypeId switch
        {
            MediaAssetType.Game => "gamepad",
            MediaAssetType.Ebook or MediaAssetType.Comic => "reading",
            _ => "play"
        };

        private static string TypeIcon(WorkKind kind, WorkDetailsV1 work) => kind switch
        {
            WorkKind.Series => "tv",
            WorkKind.Anime => "anime",
            WorkKind.Book or WorkKind.Manga or WorkKind.LightNovel => "book",
            WorkKind.Music => "music",
            WorkKind.Game => "gamepad",
            _ => work.MediaSubtypeId == MediaSubtype.Anime ? "anime" : "film"
        };

        private static string? FinishedKey(WorkKind kind) => kind switch
        {
            WorkKind.Film or WorkKind.Series or WorkKind.Anime => "progress.watched",
            WorkKind.Book or WorkKind.Manga or WorkKind.LightNovel => "progress.read",
            WorkKind.Music => "progress.heard",
            _ => null
        };

        private static string ProgressKey(WorkKind kind) => FinishedKey(kind) ?? "details.progress";

        // The episodes watched of those there are, else the share.
        private string ProgressValue(WorkDetailsV1 work, WorkKind kind)
        {
            if (kind is WorkKind.Series or WorkKind.Anime && _seasons.Sum(season => season.EpisodeCount) is > 0 and var total)
            {
                return string.Format(CultureInfo.CurrentCulture, texts["details.progressEpisodes"], _seasons.Sum(season => season.WatchedCount), total);
            }

            return string.Format(CultureInfo.CurrentCulture, texts["details.percent"], work.ProgressPercent);
        }

        private sealed class Small(params IUiComponent[] children) : HtmlElement("small", children);

        private sealed class Hr() : HtmlElement("hr");
    }
}
