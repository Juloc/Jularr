using System.Globalization;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Collection.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.User.Details
{
    public sealed partial class DetailsModel
    {
        private enum WorkKind { Film, Series, Anime, Book, Manga, LightNovel, Music, Game }

        // One part of the page: the Overview shows each with a few entries and a link, its own tab shows all of it. A part without content is no block at all.
        private sealed record Block(string TabKey, string LabelKey, int? Count, Func<bool, IUiComponent> Render);

        private static WorkKind Kind(WorkDetailsV1 work) => work.MediaTypeId switch
        {
            MediaType.Series => work.MediaSubtypeId == MediaSubtype.Anime ? WorkKind.Anime : WorkKind.Series,
            MediaType.Book => work.MediaSubtypeId switch { MediaSubtype.Manga => WorkKind.Manga, MediaSubtype.LightNovel => WorkKind.LightNovel, _ => WorkKind.Book },
            MediaType.Music => WorkKind.Music,
            MediaType.Game => WorkKind.Game,
            _ => WorkKind.Film
        };

        // The blocks that have something to show, in the order of the Overview.
        private IReadOnlyList<Block> Blocks(WorkDetailsV1 work)
        {
            var kind = Kind(work);
            var blocks = new List<Block>();
            var items = _availability?.Items ?? [];

            // The units of the type.
            if (kind is WorkKind.Series or WorkKind.Anime && _seasons.Count > 0)
            {
                blocks.Add(new("episodes", "details.episodes", _seasons.Sum(season => season.EpisodeCount), preview => Episodes(work, preview)));
            }
            else if (kind is WorkKind.Manga or WorkKind.LightNovel or WorkKind.Book && Volumes(items) is { Count: > 0 } volumes)
            {
                blocks.Add(new("volumes", kind switch { WorkKind.Manga => "details.tab.volumes.manga", WorkKind.LightNovel => "details.tab.volumes.lightNovel", _ => "details.tab.volumes.book" },
                    volumes.Count, preview => VolumeList(volumes, preview)));
            }
            else if (kind == WorkKind.Music && items.Where(item => item.WorkUnitTypeId == WorkUnitType.Track).ToArray() is { Length: > 0 } tracks)
            {
                blocks.Add(new("tracks", "details.tab.tracks", tracks.Length, preview => Tracks(tracks, preview)));
            }

            if (_credits.Count > 0) blocks.Add(new("people", "details.tab.people", _credits.Count, People));
            if (_relations.Count > 0) blocks.Add(new("related", "details.related", _relations.Count, preview => Related(work, preview)));
            var similar = _recommendations.Where(item => item.WorkId is not null || _scopes.Any(scope => scope.MediaTypeId == item.MediaTypeId && !scope.IsCatalog)).ToArray();
            if (similar.Length > 0) blocks.Add(new("similar", "details.tab.similar", similar.Length, preview => Similar(similar, preview)));
            blocks.Add(new("details", "details.tab.facts", null, preview => Facts(work, kind, preview)));

            // Editions of a book, a comic or an album; a film's cuts and a game's platforms are listed with its versions.
            if (kind is WorkKind.Book or WorkKind.Manga or WorkKind.LightNovel or WorkKind.Music && Editions(kind) is { Count: > 0 } editions)
            {
                blocks.Add(new("editions", "details.tab.editions", editions.Count, preview => EditionCards(editions, preview)));
            }

            // The files of a book, a comic or an album are already its volumes, tracks and editions.
            var listed = kind is WorkKind.Book or WorkKind.Manga or WorkKind.LightNovel or WorkKind.Music && blocks.Any(block => block.TabKey is "volumes" or "tracks" or "editions");
            var hasFiles = !listed && (items.Count > 0 || kind == WorkKind.Game && _platforms is { Count: > 0 });
            if (hasFiles)
            {
                var versions = kind is WorkKind.Film or WorkKind.Game;
                blocks.Add(new("files", versions ? "details.tab.versions" : "details.tab.files", null, preview => Files(work, kind, preview)));
            }

            return blocks;
        }

        // The Overview's part: its heading once, a link to its tab, and a short run of it.
        private IUiComponent Preview(Block block) => new Section(
            new Div(new H2(new Text(block.LabelKey)), new A(new Text("common.seeAll"), new Icon("chevron-right", assets, 14)).Class("details-block__all")
                .Attribute("href", Address(query => query["tab"] = block.TabKey))).Class("details-block__heading"),
            block.Render(true)).Class("details-block");

        // Cast and crew with their portraits; the Overview shows the first eight.
        private IUiComponent People(bool preview)
        {
            var credits = preview ? _credits.OrderBy(PersonGroup).Take(8).ToArray() : [.. _credits.OrderBy(PersonGroup)];
            var parts = new List<IUiComponent>();
            foreach (var group in credits.GroupBy(PersonGroup).OrderBy(group => group.Key))
            {
                var key = group.Key switch { 0 => "details.people.creators", 1 => "details.people.cast", _ => "details.people.crew" };
                parts.Add(new Div(new H3(new Text(key)).Class("details-sub"), new Div([.. group.Select(Person)]).Class("details-people")));
            }

            return new Div([.. parts]).Class("details-groups");
        }

        private static int PersonGroup(WorkCreditV1 credit) => IsCreator(credit.PersonRoleId) ? 0 : credit.PersonRoleId is PersonRole.Actor or PersonRole.GuestStar ? 1 : 2;

        private static bool IsCreator(PersonRole role) => role is PersonRole.Author or PersonRole.Illustrator or PersonRole.Artist or PersonRole.Developer;

        private static string PersonHref(long personId) => "/People/" + personId.ToString(CultureInfo.InvariantCulture);

        // The portrait, the name and the character or job in one line each; leads to the person's other titles.
        private IUiComponent Person(WorkCreditV1 credit) => new A(
            new Div(MediaImage.Or(assets, credit.ImageUrl, "", 185, 231, NoCoverKind.Person)).Class("details-person__photo"),
            new Strong(new Truncated(credit.Name)),
            credit.Credit is null ? new Span() : new Span(new Truncated(credit.Credit)).Class("details-person__role"))
            .Class("details-person").Attribute("href", PersonHref(credit.PersonId));

        // Similar titles lead to their Work or to their request; a known one can also go into the favorites from its card.
        private IUiComponent Similar(IReadOnlyList<DiscoverItemV1> similar, bool preview) =>
            new Div([.. (preview ? similar.Take(8) : similar).Select(Recommendation)]).Class("media-grid");

        private IUiComponent Recommendation(DiscoverItemV1 item)
        {
            var card = MediaCard.For(assets, item, item.WorkId is { } known ? "/Details/" + known.ToString(CultureInfo.InvariantCulture)
                : "/Discover/" + _scopes.First(scope => scope.MediaTypeId == item.MediaTypeId && !scope.IsCatalog).Slug + "/" + item.TmdbId!.Value.ToString(CultureInfo.InvariantCulture));
            if (item.WorkId is { } workId) card.Bookmark(new MediaBookmark(assets, workId));
            return card;
        }

        // The full summary with the genres beside the facts; the Overview shows the facts and the collections alone.
        private IUiComponent Facts(WorkDetailsV1 work, WorkKind kind, bool preview)
        {
            var facts = FactList(work, kind);
            var parts = new List<IUiComponent>();
            var grid = new FactGrid(preview ? [.. facts.Take(8)] : facts);
            if (preview) parts.Add(grid);
            else
            {
                var summary = new List<IUiComponent>();
                if (work.Overview is not null) summary.Add(new P(new ValueText(work.Overview)).Class("details-summary__text"));
                var genres = (work.Genres ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (genres.Length > 0) summary.Add(new Div([.. genres.Select(genre => (IUiComponent)new Tag(new ValueText(genre)))]).Class("details-summary__genres"));
                parts.Add(new Div(summary.Count > 0 ? new Div([.. summary]).Class("details-summary") : new Span(), grid).Class("details-facts"));
            }

            // Hand-picked collections take or give back this Work.
            if (_collections.Count > 0)
            {
                parts.Add(new UiSection("collections.title", [new Div([.. _collections.Select(collection => (IUiComponent)new Div(new Span(new ValueText(collection.Name)),
                    new Form<CollectionParametersV1>(form => form.Submit(collection.ContainsWork ? "collections.removeFrom" : "collections.addTo"),
                        "/Details/" + work.Id.ToString(CultureInfo.InvariantCulture) + "?handler=" + (collection.ContainsWork ? "RemoveFromCollection" : "AddToCollection")
                        + "&collectionId=" + collection.CollectionId.ToString(CultureInfo.InvariantCulture), _csrf, new CollectionParametersV1(collection.CollectionId)))
                    .Class("details-collection"))]).Class("details-collections")]));
            }

            return new Div([.. parts]).Class("details-facts-block");
        }

        // What the catalog knows of the title and what is here; whatever is unknown is left out.
        private List<(string LabelKey, IUiComponent Value)> FactList(WorkDetailsV1 work, WorkKind kind)
        {
            var facts = new List<(string, IUiComponent)>();
            void Add(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) facts.Add((key, new Truncated(value)));
            }

            // Each author, artist or studio leads to their other titles.
            var creators = new List<IUiComponent>();
            foreach (var creator in _credits.Where(credit => IsCreator(credit.PersonRoleId)))
            {
                if (creators.Count > 0) creators.Add(new ValueText(", "));
                creators.Add(new A(new ValueText(creator.Name)).Attribute("href", PersonHref(creator.PersonId)));
            }

            if (creators.Count > 0) facts.Add(("details.fact.creators", new Span([.. creators])));
            if (work.FranchiseId is { } franchiseId && work.FranchiseName is { } franchiseName)
            {
                facts.Add(("details.fact.series", new A(new ValueText(franchiseName + (work.FranchisePosition is { } place ? " · " + place.ToString("0.##", CultureInfo.CurrentCulture) : "")))
                    .Attribute("href", "/Franchise/" + franchiseId.ToString(CultureInfo.InvariantCulture))));
            }

            if (work.Rating is { } rating) Add("details.fact.rating", rating.ToString("0.0", CultureInfo.CurrentCulture) + (RatingSource(work.RatingSource) is { } source ? " · " + source : ""));
            if (work.OriginalTitle is { } original && !string.Equals(original, work.CanonicalTitle, StringComparison.OrdinalIgnoreCase)) Add("details.fact.originalTitle", original);
            Add("details.fact.released", work.ReleasedOn?.ToString("d", CultureInfo.CurrentCulture) ?? work.ReleaseYear?.ToString(CultureInfo.InvariantCulture));
            if (work.RuntimeMinutes is { } minutes) Add("details.fact.runtime", string.Format(CultureInfo.CurrentCulture, texts["details.minutes"], minutes));
            Add("details.fact.studio", work.Studio);
            if (_seasons.Count > 0)
            {
                var numbered = _seasons.Count(season => season.SeasonNumber > 0);
                Add("details.fact.structure", string.Join(" · ", new[] { numbered > 0 ? string.Format(CultureInfo.CurrentCulture, texts["details.seasonCount"], numbered) : null,
                    string.Format(CultureInfo.CurrentCulture, texts["details.episodeCount"], _seasons.Sum(season => season.EpisodeCount)) }.OfType<string>()));
            }

            if (work.Platforms.Length > 0) Add("details.fact.platforms", string.Join(", ", work.Platforms));
            if (work.OriginalLanguage is { } language) Add("details.fact.language", LanguageLabel.Name(language) ?? language);

            // What is here, as chips.
            var present = (_availability?.Items ?? []).Where(item => item.IsAvailable).ToArray();
            void Chips(string key, IEnumerable<string> values)
            {
                var chips = values.Distinct(StringComparer.OrdinalIgnoreCase).Select(value => (IUiComponent)new Span(new ValueText(value)).Class("details-chip")).ToArray();
                if (chips.Length > 0) facts.Add((key, new Div(chips).Class("details-chips")));
            }

            Chips("details.audio", present.SelectMany(item => item.AudioLanguages).Select(code => LanguageLabel.Name(code) ?? code));
            Chips("details.subtitles", present.SelectMany(item => item.SubtitleLanguages).Select(code => LanguageLabel.Name(code) ?? code));
            if (kind is WorkKind.Film or WorkKind.Series or WorkKind.Anime) Chips("details.quality", present.Select(item => item.Height).OfType<int>().OrderDescending().Select(Quality));
            return facts;
        }

        private static string Quality(int height) => height >= 2000 ? "4K" : height.ToString(CultureInfo.InvariantCulture) + "p";

        // The catalogs that give a rating are brand names, not words of the interface.
        private static string? RatingSource(string? catalog) => catalog switch
        {
            "tmdb_movie" or "tmdb_tv" => "TMDB",
            "tvdb" => "TVDB",
            "anilist" => "AniList",
            "openlibrary" => "Open Library",
            "musicbrainz" => "MusicBrainz",
            "igdb" => "IGDB",
            _ => null
        };
    }
}
