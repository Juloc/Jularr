using System.Globalization;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Ui;

namespace Jularr.Tests;

[TestClass]
public sealed class MediaBannerCardTests
{
    private const string Profile = "owner";
    private static readonly UiTextBundle Ui = UiTextBundle.English;
    private static readonly DateTime BaseTime = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow("RELEASING", MediaReleaseStatus.Ongoing, "Ongoing")]
    [DataRow("FINISHED", MediaReleaseStatus.Finished, "Finished")]
    [DataRow("NOT_YET_RELEASED", MediaReleaseStatus.Upcoming, "Upcoming")]
    [DataRow("HIATUS", MediaReleaseStatus.Hiatus, "Hiatus")]
    [DataRow("cancelled", MediaReleaseStatus.Cancelled, "Cancelled")]
    public void ProviderStatusMapsToBadge(string providerStatus, MediaReleaseStatus expected, string label)
    {
        var card = MediaBannerCardModel.Create(Anime() with { ProviderStatus = providerStatus }, Ui);

        Assert.IsNotNull(card.Status);
        Assert.AreEqual(expected, card.Status.Status);
        Assert.AreEqual(label, card.Status.Label);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("SOMETHING_NEW")]
    public void UnknownOrMissingStatusShowsNoBadge(string? providerStatus)
    {
        var card = MediaBannerCardModel.Create(Anime() with { ProviderStatus = providerStatus }, Ui);

        Assert.IsNull(card.Status);
    }

    [TestMethod]
    public void MetaShowsTypeAndYearOnlyWhenTheYearIsKnown()
    {
        Assert.AreEqual("Anime · 2024", MediaBannerCardModel.Create(Anime() with { Year = 2024 }, Ui).Meta);
        Assert.AreEqual("Anime", MediaBannerCardModel.Create(Anime(), Ui).Meta);
        Assert.AreEqual(
            "Light novel",
            MediaBannerCardModel.Create(Anime() with { Kind = MediaBannerKind.LightNovel }, Ui).Meta);
    }

    [TestMethod]
    public void InProgressAnimeContinuesWithTheNextEpisodeAndShowsProgress()
    {
        var card = MediaBannerCardModel.Create(
            Anime() with
            {
                Progress = new MediaBannerProgress(
                    MediaBannerProgressState.InProgress,
                    MediaBannerUnit.Episode,
                    6,
                    "/Library/Episode/next",
                    TotalUnits: 12,
                    Percent: 42)
            },
            Ui);

        Assert.IsNotNull(card.Action);
        Assert.AreEqual("Continue watching", card.Action.Label);
        Assert.AreEqual("Episode 6 of 12", card.Action.UnitLabel);
        Assert.AreEqual("/Library/Episode/next", card.Action.Url);
        Assert.IsNotNull(card.Progress);
        Assert.AreEqual(42, card.Progress.Percent);
        Assert.AreEqual("42%", card.Progress.Text);
        Assert.AreEqual("42% watched", card.Progress.AriaLabel);
    }

    [TestMethod]
    public void UnstartedTitlesOfferToStartWithoutAProgressBar()
    {
        var anime = MediaBannerCardModel.Create(
            Anime() with { Progress = Unit(MediaBannerProgressState.NotStarted, MediaBannerUnit.Episode, 1, total: 12, percent: 0) },
            Ui);
        var manga = MediaBannerCardModel.Create(
            Anime() with
            {
                Kind = MediaBannerKind.Manga,
                Progress = Unit(MediaBannerProgressState.NotStarted, MediaBannerUnit.Chapter, 1)
            },
            Ui);

        Assert.AreEqual("Start watching", anime.Action?.Label);
        Assert.AreEqual("Episode 1 of 12", anime.Action?.UnitLabel);
        Assert.IsNull(anime.Progress);
        Assert.AreEqual("Start reading", manga.Action?.Label);
        Assert.AreEqual("Chapter 1", manga.Action?.UnitLabel);
        Assert.IsNull(manga.Progress);
    }

    [TestMethod]
    public void NextUnitLabelsFollowTheMediumAndWhatIsKnown()
    {
        string Label(MediaBannerKind kind, MediaBannerProgress progress) =>
            MediaBannerCardModel.Create(Anime() with { Kind = kind, Progress = progress }, Ui).Action!.UnitLabel;

        Assert.AreEqual(
            "Season 2, episode 3",
            Label(MediaBannerKind.Anime, Unit(MediaBannerProgressState.InProgress, MediaBannerUnit.Episode, 3, total: 24, season: 2)));
        Assert.AreEqual(
            "Episode 4",
            Label(MediaBannerKind.Anime, Unit(MediaBannerProgressState.InProgress, MediaBannerUnit.Episode, 4)));
        Assert.AreEqual(
            "Chapter 10.5 of 40",
            Label(MediaBannerKind.Manga, Unit(MediaBannerProgressState.InProgress, MediaBannerUnit.Chapter, 10.5, total: 40)));
        Assert.AreEqual(
            "Volume 3 of 8",
            Label(MediaBannerKind.LightNovel, Unit(MediaBannerProgressState.InProgress, MediaBannerUnit.Volume, 3, total: 8)));

        var reading = MediaBannerCardModel.Create(
            Anime() with
            {
                Kind = MediaBannerKind.Book,
                Progress = Unit(MediaBannerProgressState.InProgress, MediaBannerUnit.Chapter, 2, percent: 10)
            },
            Ui);
        Assert.AreEqual("Continue reading", reading.Action?.Label);
        Assert.AreEqual("10% read", reading.Progress?.AriaLabel);

        var finished = MediaBannerCardModel.Create(
            Anime() with { Progress = Unit(MediaBannerProgressState.Completed, MediaBannerUnit.Episode, 1, total: 12, percent: 100) },
            Ui);
        Assert.AreEqual("Watch again", finished.Action?.Label);
        Assert.AreEqual(100, finished.Progress?.Percent);
    }

    [TestMethod]
    public void LanguageChipsAppearOnlyForKnownLanguages()
    {
        var empty = MediaBannerCardModel.Create(
            Anime() with { AudioLanguages = [], SubtitleLanguages = ["und", " ", "off"] },
            Ui);
        Assert.IsNull(empty.Audio);
        Assert.IsNull(empty.Subtitles);
        Assert.IsFalse(empty.HasFacts);

        var card = MediaBannerCardModel.Create(
            Anime() with
            {
                AudioLanguages = ["jpn", "ja", "ger", "eng"],
                SubtitleLanguages = ["de", "en", "fr"]
            },
            Ui);

        Assert.AreEqual("Audio languages", card.Audio?.Label);
        CollectionAssert.AreEqual(new[] { "JA", "DE", "EN" }, card.Audio!.Chips.ToArray());
        Assert.AreEqual("Subtitle languages", card.Subtitles?.Label);
        CollectionAssert.AreEqual(new[] { "DE", "EN", "FR" }, card.Subtitles!.Chips.ToArray());
        Assert.IsTrue(card.HasFacts);
    }

    [TestMethod]
    public void ManyLanguagesCollapseIntoAnOverflowChip()
    {
        var chips = MediaBannerCardModel.LanguageChips(["ja", "en", "de", "fr", "es", "it"]);

        CollectionAssert.AreEqual(new[] { "JA", "EN", "DE", "+3" }, chips.ToArray());
        Assert.AreEqual(MediaBannerCardModel.MaxLanguageChips, chips.Count);
    }

    [TestMethod]
    public void RatingUsesOneDecimalOnATenScale()
    {
        Assert.AreEqual("8.6", MediaBannerCardModel.FormatRating(86, CultureInfo.InvariantCulture));
        Assert.AreEqual("8.0", MediaBannerCardModel.FormatRating(80, CultureInfo.InvariantCulture));
        Assert.AreEqual("10.0", MediaBannerCardModel.FormatRating(100, CultureInfo.InvariantCulture));
        Assert.IsNull(MediaBannerCardModel.FormatRating(null, CultureInfo.InvariantCulture));
        Assert.IsNull(MediaBannerCardModel.FormatRating(0, CultureInfo.InvariantCulture));
        Assert.IsNull(MediaBannerCardModel.FormatRating(101, CultureInfo.InvariantCulture));

        var german = CultureInfo.GetCultureInfo("de");
        Assert.AreEqual($"8{german.NumberFormat.NumberDecimalSeparator}6", MediaBannerCardModel.FormatRating(86, german));

        var card = MediaBannerCardModel.Create(Anime() with { AverageScore = 86, GroupCount = 2 }, Ui);
        Assert.AreEqual("8.6", card.Rating?.Value);
        Assert.AreEqual("Rating", card.Rating?.Label);
        Assert.AreEqual("2", card.Groups?.Value);
        Assert.AreEqual("Seasons", card.Groups?.Label);

        var bare = MediaBannerCardModel.Create(Anime() with { AverageScore = null, GroupCount = 0 }, Ui);
        Assert.IsNull(bare.Rating);
        Assert.IsNull(bare.Groups);
    }

    [TestMethod]
    public void NextEpisodeResolutionFollowsCanonicalProgress()
    {
        var episodes = Enumerable.Range(1, 4)
            .Select(number => new EpisodeOrderKey(Guid.NewGuid(), 1, number))
            .Append(new EpisodeOrderKey(Guid.NewGuid(), 0, 1))
            .ToArray();
        var progress = new Dictionary<Guid, EpisodeProgressState>();

        var start = LibraryMediaCardQuery.ResolveNext(episodes, progress);
        Assert.AreEqual((MediaBannerProgressState.NotStarted, episodes[0]), start);

        progress[episodes[0].Id] = Watched(episodes[0], BaseTime);
        progress[episodes[1].Id] = new EpisodeProgressState(episodes[1].Id, 600_000, false, BaseTime.AddMinutes(1));
        Assert.AreEqual(
            (MediaBannerProgressState.InProgress, episodes[1]),
            LibraryMediaCardQuery.ResolveNext(episodes, progress),
            "An unfinished latest episode is resumed.");

        progress[episodes[1].Id] = Watched(episodes[1], BaseTime.AddMinutes(2));
        progress[episodes[2].Id] = Watched(episodes[2], BaseTime.AddMinutes(-5));
        Assert.AreEqual(
            (MediaBannerProgressState.InProgress, episodes[3]),
            LibraryMediaCardQuery.ResolveNext(episodes, progress),
            "An already watched neighbour is skipped.");

        progress[episodes[3].Id] = Watched(episodes[3], BaseTime.AddMinutes(3));
        Assert.AreEqual(
            (MediaBannerProgressState.Completed, episodes[0]),
            LibraryMediaCardQuery.ResolveNext(episodes, progress),
            "Specials never block completion; a finished series starts over at episode 1.");

        Assert.IsNull(LibraryMediaCardQuery.ResolveNext([], progress));
    }

    [TestMethod]
    public async Task LibraryCardsCombineMetadataInventoryAndProgressAsync()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var ongoing = await seed.AddAnimeAsync("akatsuki", [.. Enumerable.Range(1, 8).Select(number => (1, number, false))]);
        await seed.AddVideoAsync(ongoing.Work, ongoing.Episodes[0].Canonical, audio: ["jpn", "ger"], subtitles: ["eng"]);
        await seed.AddVideoAsync(ongoing.Work, ongoing.Episodes[1].Canonical, audio: ["jpn"], subtitles: ["eng", "und"]);
        foreach (var episode in ongoing.Episodes.Skip(2))
        {
            await seed.AddVideoAsync(ongoing.Work, episode.Canonical);
        }

        fixture.Db.AnimeMetadata.Add(new AnimeMetadata
        {
            AnimeId = ongoing.Anime.Id,
            Provider = AniListMetadataProvider.ProviderKey,
            ExternalId = "1",
            PreferredTitle = "Akatsuki no Sora",
            Status = "RELEASING",
            SeasonYear = 2024,
            EpisodeCount = 12,
            AverageScore = 86
        });
        fixture.Db.SubtitleTracks.Add(new SubtitleTrack { EpisodeId = ongoing.Episodes[0].Legacy.Id, Path = "a.de.srt", Language = "de", Format = "srt" });
        await fixture.Db.SaveChangesAsync();

        for (var index = 0; index < 5; index++)
        {
            await seed.SetProgressAsync(Profile, ongoing.Work, ongoing.Episodes[index].Canonical, 0, null, completed: true, BaseTime.AddMinutes(index));
        }

        await seed.SetProgressAsync("other", ongoing.Work, ongoing.Episodes[5].Canonical, 0, null, completed: true, BaseTime);

        var multiSeason = await seed.AddAnimeAsync("bravo", [(1, 1, true), (2, 1, true)]);
        await seed.AddAnimeAsync("charlie", [(1, 1, false)]);

        var cards = (await new LibraryMediaCardQuery(fixture.Db).GetAnimeEntriesAsync(Profile, CancellationToken.None))
            .Entries
            .Select(entry => entry.Card)
            .OrderBy(card => card.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "Akatsuki no Sora", "bravo", "charlie" },
            cards.Select(x => x.Title).ToArray());

        var card = cards[0];
        Assert.AreEqual($"/Library/Anime/{ongoing.Anime.Id}", card.Href);
        Assert.AreEqual("RELEASING", card.ProviderStatus);
        Assert.AreEqual(2024, card.Year);
        Assert.AreEqual(86, card.AverageScore);
        Assert.AreEqual(1, card.GroupCount);
        CollectionAssert.AreEqual(new[] { "ja", "de" }, card.AudioLanguages!.ToArray());
        CollectionAssert.AreEqual(new[] { "en", "de" }, card.SubtitleLanguages!.ToArray());
        Assert.AreEqual(
            new MediaBannerProgress(
                MediaBannerProgressState.InProgress,
                MediaBannerUnit.Episode,
                6,
                $"/Library/Episode/{ongoing.Episodes[5].Legacy.Id}",
                TotalUnits: 12,
                NextSeason: null,
                Percent: 42),
            card.Progress,
            "Five of twelve provider episodes watched; another profile's progress does not count.");

        var bravo = cards[1];
        Assert.IsNull(bravo.ProviderStatus);
        Assert.IsNull(bravo.AverageScore);
        Assert.AreEqual(2, bravo.GroupCount);
        Assert.AreEqual(0, bravo.AudioLanguages!.Count);
        Assert.AreEqual(MediaBannerProgressState.NotStarted, bravo.Progress?.State);
        Assert.AreEqual(1, bravo.Progress?.NextSeason);
        Assert.IsNull(bravo.Progress?.TotalUnits);
        Assert.AreEqual($"/Library/Episode/{multiSeason.Episodes[0].Legacy.Id}", bravo.Progress?.NextUrl);

        Assert.IsNull(cards[2].Progress, "Nothing playable means no play button.");
        Assert.IsNull(cards[2].GroupCount);
    }

    private static MediaBannerCardData Anime() =>
        new(MediaBannerKind.Anime, "Akatsuki no Sora", "/Library/Anime/1");

    private static MediaBannerProgress Unit(
        MediaBannerProgressState state,
        MediaBannerUnit unit,
        double number,
        int? total = null,
        int? season = null,
        int? percent = null) =>
        new(state, unit, number, "/next", total, season, percent);

    private static EpisodeProgressState Watched(EpisodeOrderKey episode, DateTime updatedAt) =>
        new(episode.Id, 0, true, updatedAt);
}
