using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Collection.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Backend.Service.User.Collection.V1;
using Jularr.Backend.Service.User.Discover.V1;
using Jularr.Backend.Service.User.Request.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Details
{
    public sealed class DetailsModel(ReadWorkV1 read, ReadWorkAvailabilityV1 readAvailability, UpdateWatchlistV1 update, CreateRequestV1 request, ReadUiLocalesV1 locales,
        ReadWorkPlatformsV1 readPlatforms, ReadRequestProfilesV1 readRequestProfiles, ReadWorkCollectionsV1 readCollections, AddCollectionWorkV1 addToCollection,
        RemoveCollectionWorkV1 removeFromCollection, ReadWorkSeasonsV1 readSeasons, ReadWorkEpisodesV1 readEpisodes, ReadWorkCreditsV1 readCredits,
        ReadWorkRecommendationsV1 readRecommendations, ReadFranchiseWorksV1 readFranchise, ReadDiscoverScopesV1 readScopes, IAntiforgery antiforgery, FrontendAssets assets, UiText texts) : JularrPage
    {
        private IReadOnlyList<LibraryItemV1> _related = [];
        private IReadOnlyList<DiscoverItemV1> _recommendations = [];
        private IReadOnlyList<DiscoverScopeV1> _scopes = [];
        private IReadOnlyList<WorkCreditV1> _credits = [];
        private IReadOnlyList<WorkSeasonV1> _seasons = [];
        private IReadOnlyList<WorkEpisodeV1> _episodes = [];
        private WorkDetailsV1? _work;
        private IReadOnlyList<WorkCollectionV1> _collections = [];
        private PageResult<WorkAvailabilityV1>? _availability;
        private BaseDataList<UiLocaleDetailsV1>? _locales;
        private BaseDataList<GamePlatformV1>? _platforms;
        private BaseDataList<RequestProfileOptionV1>? _requestProfiles;
        private CreateRequestDataV1? _requestInput;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty(SupportsGet = true, Name = "p")]
        public int PageNumber { get; set; } = 1;

        // The season whose episodes are shown; the first numbered one by default.
        [BindProperty(SupportsGet = true, Name = "season")]
        public int? SeasonNumber { get; set; }

        protected override void Define(Page page)
        {
            page.Title("details.title", _work?.CanonicalTitle);
            page.Navigation("nav.home");
            page.Asset("Pages/User/Details/Details.entry.scss");
            if (_recommendations.Count > 0 || _related.Count > 0)
            {
                page.Asset("Framework/Components/MediaShelf.entry.ts");
                page.Asset("Framework/Components/MediaCard.entry.ts");
            }

            // Persisted media identity.
            if (_work is { } work)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                // Plex plays what Jularr cannot play here.
                var plex = work.PlexUrl is not null && (!texts.CanPlay() || _availability?.Items.Any(item => item.IsAvailable) is not true) ? work.PlexUrl : null;
                var playable = _availability?.Items.Where(CanOpen).OrderByDescending(item => item.ResumePositionMs is > 0 && !item.IsCompleted).FirstOrDefault();
                var actionKey = plex is null ? null : "media.openInPlex";
                var actionHref = plex;
                if (playable is not null)
                {
                    actionKey = PlayKey(playable);
                    actionHref = PlayHref(playable);
                }

                page.Content.Add(new MediaHero(work.CanonicalTitle, work.BackdropUrl, string.Join(" · ", new[] { texts[MediaTypeLabel.Key(work.MediaTypeId, work.MediaSubtypeId)], work.ReleaseYear?.ToString(CultureInfo.InvariantCulture),
                    work.RuntimeMinutes is { } minutes ? string.Format(CultureInfo.CurrentCulture, texts["details.minutes"], minutes) : null, work.Genres, work.Studio,
                    work.Platforms.Length > 0 ? string.Join(", ", work.Platforms) : null }
                    .Where(part => part is not null)), work.Overview, actionKey, actionHref));

                // Each author, artist or studio leads to their other titles.
                var creators = new List<IUiComponent>();
                foreach (var creator in _credits.Where(credit => IsCreator(credit.PersonRoleId)))
                {
                    if (creators.Count > 0) creators.Add(new ValueText(", "));
                    creators.Add(new A(new ValueText(creator.Name)).Attribute("href", PersonHref(creator.PersonId)));
                }

                if (creators.Count > 0) page.Content.Add(new P([.. creators]).Class("details-creators"));
                if (work.FranchiseId is { } franchiseId && work.FranchiseName is { } franchiseName && _related.Count == 0)
                {
                    page.Content.Add(new P([new Text("details.franchise"), new ValueText(" "),
                        new A(new ValueText(franchiseName)).Attribute("href", "/Franchise/" + franchiseId.ToString(CultureInfo.InvariantCulture)),
                        new ValueText(work.FranchisePosition is { } place ? " · " + place.ToString("0.##", CultureInfo.CurrentCulture) : "")]).Class("details-franchise"));
                }

                // Shared personal actions.
                var actions = new List<IUiComponent>();
                actions.Add(new Form<UpdateWatchlistDataV1>(form =>
                {
                    form.Hidden(item => item.IsWatchlisted);
                    form.Submit(work.IsWatchlisted ? "media.favorites.remove" : "media.favorites.add");
                }, "/Details/" + work.Id.ToString(CultureInfo.InvariantCulture), csrf,
                    new UpdateWatchlistDataV1(!work.IsWatchlisted), FormError));

                // Explicit desired content language.
                if (_locales is { } languages && work.IsRequestable)
                {
                    var options = languages.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
                    var selected = languages.FirstOrDefault(locale => string.Equals(locale.Locale, texts.Locale, StringComparison.OrdinalIgnoreCase));
                    actions.Add(new Form<CreateRequestDataV1>(form =>
                    {
                        form.Select(item => item.UiLocaleId, "request.language", options);

                        // A game is requested for one of its platforms.
                        if (_platforms is { Count: > 0 } platforms)
                        {
                            form.Select(item => item.GamePlatformId, "request.platform",
                                platforms.Select(platform => new SelectOption(platform.Id.ToString(CultureInfo.InvariantCulture), platform.Name)));
                        }

                        // Qualities the request rule offers; none keeps the default.
                        if (_requestProfiles is { Count: > 0 } qualities)
                        {
                            form.Select(item => item.AcquisitionProfileId, "request.quality",
                                [new SelectOption("", texts["request.quality.default"]), .. qualities.Select(quality => new SelectOption(quality.Id.ToString(CultureInfo.InvariantCulture), quality.Name))]);
                        }

                        form.Submit("request.create");
                    }, "/Details/" + work.Id.ToString(CultureInfo.InvariantCulture) + "?handler=Request", csrf,
                        _requestInput ?? (selected is null ? null : new CreateRequestDataV1(selected.Id)), _requestInput is null ? null : FormError));
                }

                // The profile's request shows its state and leads to the requests.
                if (work.RequestStatusTypeId is { } requested && texts.CanRequest())
                {
                    actions.Add(new A(new Text("request.state." + requested)).Class("button").Attribute("href", "/Requests"));
                }

                page.Content.Add(new Div([.. actions]).Class("details-actions"));

                // The trailer beside a movie's languages; all its versions open on demand.
                var overview = new List<IUiComponent>();
                if (work.TrailerKey is { } trailer) overview.Add(new TrailerLink(trailer));
                var compact = work.MediaTypeId == MediaType.Movie && _availability is { Items.Count: > 0 };
                if (compact) overview.Add(Languages(_availability!.Items));
                if (overview.Count > 0) page.Content.Add(new Div([.. overview]).Class("details-overview"));

                // A series shows its seasons and their episodes instead of its files.
                if (_seasons.Count > 0)
                {
                    page.Content.Add(Series(work));
                }
                // Stored inventory, no storage access; named sections split it where staff defined them.
                else if (!compact && _availability is { Items.Count: > 0 } availability)
                {
                    var sections = new List<IUiComponent>();
                    var start = 0;
                    for (var index = 1; index <= availability.Items.Count; index++)
                    {
                        if (index < availability.Items.Count && availability.Items[index].GroupName == availability.Items[start].GroupName)
                        {
                            continue;
                        }

                        if (availability.Items[start].GroupName is { } name) sections.Add(new H3(new ValueText(name)));
                        sections.Add(Versions(availability.Items.Skip(start).Take(index - start).ToArray()));
                        start = index;
                    }

                    page.Content.Add(new UiSection("details.versions", [.. sections]));

                    // One bounded inventory page.
                    var paging = new List<IUiComponent>();
                    var address = "/Details/" + work.Id.ToString(CultureInfo.InvariantCulture) + "?p=";
                    if (PageNumber > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", address + (PageNumber - 1).ToString(CultureInfo.InvariantCulture)));
                    if (availability.Items.Count == availability.PageSize)
                    {
                        paging.Add(new A(new Text("common.next")).Class("button").Attribute("href", address + (PageNumber + 1).ToString(CultureInfo.InvariantCulture)));
                    }

                    if (paging.Count > 0) page.Content.Add(new Nav([.. paging]).Class("details-paging").Attribute("aria-label", texts["details.pages"]));
                }

                // Cast before crew, each with their character or job.
                if (_credits.Any(credit => !IsCreator(credit.PersonRoleId)))
                {
                    page.Content.Add(new UiSection("details.cast", [new Div([.. _credits.Where(credit => !IsCreator(credit.PersonRoleId)).Select(Person)]).Class("details-people")]));
                }

                // The other titles of its film series, then similar titles leading to their Work or their request.
                if (work.FranchiseId is { } series)
                {
                    page.Content.Add(new MediaShelf(assets, "details.related", "/Franchise/" + series.ToString(CultureInfo.InvariantCulture), [.. _related.Select(item => (IUiComponent)MediaCard.For(assets, item).Bookmark(new MediaBookmark(assets, item.Id, item.IsWatchlisted, Request.Path + Request.QueryString, csrf)))]));
                }

                page.Content.Add(new MediaShelf(assets, "details.recommendations", null, [.. _recommendations.Where(item => item.WorkId is not null || _scopes.Any(scope => scope.MediaTypeId == item.MediaTypeId && !scope.IsCatalog)).Select(Recommendation)]));

                // Hand-picked collections take or give back this Work.
                if (_collections.Count > 0)
                {
                    page.Content.Add(new UiSection("collections.title", [new Div([.. _collections.Select(collection => (IUiComponent)new Div(new Span(new ValueText(collection.Name)),
                        new Form<CollectionParametersV1>(form => form.Submit(collection.ContainsWork ? "collections.removeFrom" : "collections.addTo"),
                            "/Details/" + work.Id.ToString(CultureInfo.InvariantCulture) + "?handler=" + (collection.ContainsWork ? "RemoveFromCollection" : "AddToCollection")
                            + "&collectionId=" + collection.CollectionId.ToString(CultureInfo.InvariantCulture), csrf, new CollectionParametersV1(collection.CollectionId)))
                        .Class("details-collection"))]).Class("details-collections")]));
                }
            }
            else if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // Audio and subtitle languages and qualities of the present versions; the full table on demand.
        private IUiComponent Languages(IReadOnlyList<WorkAvailabilityV1> items)
        {
            var present = items.Where(item => item.IsAvailable).ToArray();
            var rows = new List<IUiComponent>();
            void Row(string labelKey, IEnumerable<string> values)
            {
                var chips = values.Distinct(StringComparer.OrdinalIgnoreCase).Select(value => (IUiComponent)new Span(new ValueText(value)).Class("details-chip")).ToArray();
                if (chips.Length > 0) rows.Add(new Div(new Dt(new Text(labelKey)), new Dd(chips)));
            }

            Row("details.audio", present.SelectMany(item => item.AudioLanguages).Select(code => LanguageLabel.Name(code) ?? code));
            Row("details.subtitles", present.SelectMany(item => item.SubtitleLanguages).Select(code => LanguageLabel.Name(code) ?? code));
            Row("details.quality", present.Select(item => item.Height).OfType<int>().OrderDescending()
                .Select(height => height >= 2000 ? "4K" : height.ToString(CultureInfo.InvariantCulture) + "p"));
            return new Section(
                new H2(new Text("details.versions")),
                rows.Count > 0 ? new Dl([.. rows]).Class("details-languages") : new Span(),
                new Framework.Details(new Summary(new Text("common.seeAll")), Versions(items)).Class("details-versions"))
                .Class("details-languages-card");
        }

        // A known title also goes into the favorites from its card.
        private IUiComponent Recommendation(DiscoverItemV1 item)
        {
            var card = MediaCard.For(assets, item, item.WorkId is { } known ? "/Details/" + known.ToString(CultureInfo.InvariantCulture)
                : "/Discover/" + _scopes.First(scope => scope.MediaTypeId == item.MediaTypeId && !scope.IsCatalog).Slug + "/" + item.TmdbId!.Value.ToString(CultureInfo.InvariantCulture));
            if (item.WorkId is { } workId) card.Bookmark(new MediaBookmark(assets, workId));
            return card;
        }

        private static bool IsCreator(PersonRole role) => role is PersonRole.Author or PersonRole.Illustrator or PersonRole.Artist or PersonRole.Developer;

        private static string PersonHref(long personId) => "/People/" + personId.ToString(CultureInfo.InvariantCulture);

        // Cast with their character, crew with their job; each leads to the person's other titles.
        private static IUiComponent Person(WorkCreditV1 credit) => new A(
            credit.ImageUrl is null ? new Div().Class("details-person__placeholder") : new MediaImage(credit.ImageUrl, credit.Name, 185, 278),
            new Strong(new ValueText(credit.Name)),
            credit.Credit is null ? new Span() : new Span(new ValueText(credit.Credit)).Class("details-person__role"))
            .Class("details-person").Attribute("href", PersonHref(credit.PersonId));

        private IUiComponent Series(WorkDetailsV1 work)
        {
            var home = "/Details/" + work.Id.ToString(CultureInfo.InvariantCulture);
            var seasons = _seasons.Select(season =>
            {
                var current = season.SeasonNumber == SelectedSeason;
                var facts = string.Join(" · ", new[] { string.Format(CultureInfo.CurrentCulture, texts["details.episodeCount"], season.EpisodeCount),
                    season.ReleaseYear?.ToString(CultureInfo.InvariantCulture) }.OfType<string>());
                var link = new A(season.ImageUrl is null ? new Span().Class("details-season__poster") : new MediaImage(season.ImageUrl, "", 92, 138),
                    new Span(new Strong(new ValueText(SeasonName(season))), new Span(new ValueText(facts)).Class("details-season__facts")).Class("details-season__text"))
                    .Class(current ? "details-season details-season--current" : "details-season").Attribute("href", home + "?season=" + season.SeasonNumber.ToString(CultureInfo.InvariantCulture));
                return current ? (IUiComponent)link.Attribute("aria-current", "page") : link;
            });
            var canPlay = texts.CanPlay();
            var episodes = new List<IUiComponent>();
            string? group = null;
            foreach (var episode in _episodes)
            {
                // Staff-named sections head their episodes.
                if (episode.GroupName is { } name && name != group) episodes.Add(new H3(new ValueText(name)));
                group = episode.GroupName;
                var resume = episode.ResumePositionMs is > 0 and { } position && !episode.IsCompleted ? Clock(position) : null;
                episodes.Add(new EpisodeCard(
                        "S" + episode.SeasonNumber.ToString("00", CultureInfo.InvariantCulture) + " E" + episode.Number.ToString("00.###", CultureInfo.InvariantCulture),
                        episode.Title, episode.Overview, episode.ImageUrl, episode.RuntimeMinutes,
                        episode.IsUpcoming && episode.MediaStateTypeId != MediaStateType.Available ? "details.upcoming" : "media.state." + episode.MediaStateTypeId,
                        canPlay && episode.MediaAssetId is { } asset ? "/Player/" + asset.ToString(CultureInfo.InvariantCulture) : null, resume)
                    .Progress(episode.IsCompleted ? null : episode.ResumePositionMs, episode.DurationMs));
            }

            return new Div(
                new Section(new H2(new Text("details.seasons")), new Nav([.. seasons]).Class("details-seasons").Attribute("aria-label", texts["details.seasons"])),
                new Section(new H2(new Text("details.episodes")), _episodes.Count == 0 ? new P(new Text("details.noEpisodes")) : new Div([.. episodes]).Class("details-episodes")))
                .Class("details-series");
        }

        private string SeasonName(WorkSeasonV1 season) => season.SeasonNumber == 0 ? texts["details.specials"]
            : MediaUnitLabel.Format(texts, WorkUnitType.Season, season.SeasonNumber, null)!;

        private int? SelectedSeason => SeasonNumber is { } chosen && _seasons.Any(season => season.SeasonNumber == chosen) ? chosen
            : _seasons.FirstOrDefault(season => season.SeasonNumber > 0)?.SeasonNumber ?? _seasons.FirstOrDefault()?.SeasonNumber;

        private Table<WorkAvailabilityV1> Versions(IReadOnlyList<WorkAvailabilityV1> items) => new Table<WorkAvailabilityV1>(items)
            .Content("details.unit", item => new ValueText(MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber, item.UnitTitle) ?? texts["details.wholeWork"]))
            .Resource(item => item.MediaAssetTypeId, "table.type", type => "media.asset." + type)
            .Content("details.quality", item => new ValueText(item.Height is { } height ? height.ToString(CultureInfo.InvariantCulture) + "p" : ""))
            .Content("details.audio", item => new ValueText(string.Join(", ", item.AudioLanguages)))
            .Content("details.subtitles", item => new ValueText(string.Join(", ", item.SubtitleLanguages)))
            .Content("details.duration", item => new ValueText(item.DurationMs is { } duration ? Clock(duration) : ""))
            .Content("table.status", item => new Text(item.IsAvailable ? "library.present" : "library.missing"))
            .Content("common.actions", Play);

        private IUiComponent Play(WorkAvailabilityV1 item)
        {
            // Continue where this profile stopped.
            if (!CanOpen(item))
            {
                return new Span();
            }

            var resume = !IsReadable(item) && item.ResumePositionMs is > 0 && !item.IsCompleted ? Clock(item.ResumePositionMs.Value) : null;
            var label = resume is null ? new Text(PlayKey(item)) : (IUiComponent)new Span(new Text("details.resume"), new ValueText(" " + resume));
            return new A(label).Class("button").Attribute("href", PlayHref(item));
        }

        private static string Clock(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

        private bool CanOpen(WorkAvailabilityV1 item) => item.IsAvailable && (item.MediaAssetTypeId == MediaAssetType.Game ? item.IsEmulated && texts.CanPlayGames()
            : IsReadable(item) ? texts.CanRead() : texts.CanPlay());

        private static string PlayKey(WorkAvailabilityV1 item) => IsReadable(item) ? "details.read"
            : item.IsCompleted ? "details.playAgain" : item.ResumePositionMs is > 0 ? "media.continue" : "details.play";

        private static string PlayHref(WorkAvailabilityV1 item) => (item.MediaAssetTypeId == MediaAssetType.Game ? "/Game/" : IsReadable(item) ? "/Read/" : "/Player/")
            + item.Id.ToString(CultureInfo.InvariantCulture);

        private static bool IsReadable(WorkAvailabilityV1 item) => ReadableMedia.IsReadable(item.MediaAssetTypeId);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAddToCollectionAsync([BindRequired] long collectionId)
        {
            await addToCollection.ExecuteAsync<UpdateResult>(new CollectionWorkParametersV1(collectionId, Id), default, HttpContext.RequestAborted);
            return RedirectToPage("/User/Details/Details", new { id = Id });
        }

        public async Task<IActionResult> OnPostRemoveFromCollectionAsync([BindRequired] long collectionId)
        {
            await removeFromCollection.ExecuteAsync<UpdateResult>(new CollectionWorkParametersV1(collectionId, Id), default, HttpContext.RequestAborted);
            return RedirectToPage("/User/Details/Details", new { id = Id });
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] bool isWatchlisted, string? returnUrl = null)
        {
            await update.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), new UpdateWatchlistDataV1(isWatchlisted), HttpContext.RequestAborted);
            if (Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
            return RedirectToPage("/User/Details/Details", new { id = Id });
        }

        protected override async Task LoadDataAsync()
        {
            _work = await read.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            if (_work.MediaTypeId == MediaType.Series)
            {
                _seasons = await readSeasons.ExecuteAsync<BaseDataList<WorkSeasonV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
                if (SelectedSeason is { } season)
                {
                    _episodes = await readEpisodes.ExecuteAsync<BaseDataList<WorkEpisodeV1>>(new ReadWorkEpisodesParametersV1(Id, season), HttpContext.RequestAborted);
                }
            }

            _availability = await readAvailability.ExecuteAsync<PageResult<WorkAvailabilityV1>>(new ReadWorkAvailabilityParametersV1(Id, page: PageNumber, pageSize: 48), HttpContext.RequestAborted);
            _credits = await readCredits.ExecuteAsync<BaseDataList<WorkCreditV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            if (_work.FranchiseId is { } franchiseId)
            {
                _related = [.. (await readFranchise.ExecuteAsync<PageResult<LibraryItemV1>>(new FranchiseWorksParametersV1(franchiseId, pageSize: 20), HttpContext.RequestAborted)).Items
                    .Where(item => item.Id != Id)];
            }

            try
            {
                _recommendations = await readRecommendations.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new WorkParametersV1(Id), default(NoData), HttpContext.RequestAborted);
                if (_recommendations.Count > 0) _scopes = await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), HttpContext.RequestAborted);
            }
            catch (ServiceUnavailableException)
            {
                // The Work stays complete without its provider's suggestions.
            }
            if (texts.CanCollect())
            {
                _collections = await readCollections.ExecuteAsync<BaseDataList<WorkCollectionV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            }
            if (texts.CanRequest())
            {
                _locales = await locales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: HttpContext.RequestAborted);
                _requestProfiles = await readRequestProfiles.ExecuteAsync<BaseDataList<RequestProfileOptionV1>>(new CreateRequestParametersV1(Id), HttpContext.RequestAborted);
                if (_work.MediaTypeId == MediaType.Game)
                {
                    _platforms = await readPlatforms.ExecuteAsync<BaseDataList<GamePlatformV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
                }
            }
        }

        public async Task<IActionResult> OnPostRequestAsync([BindRequired] long uiLocaleId, long? gamePlatformId, long? acquisitionProfileId)
        {
            _requestInput = new CreateRequestDataV1(uiLocaleId, gamePlatformId, acquisitionProfileId);
            await request.ExecuteAsync<CreateRequestResultV1>(new CreateRequestParametersV1(Id), _requestInput, HttpContext.RequestAborted);
            return RedirectToPage("/User/Requests/Requests");
        }
    }
}
