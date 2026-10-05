using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Ui;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>#597: the availability state of a title and the small badge it becomes on a media card.</summary>
[TestClass]
public sealed class MediaAvailabilityTests
{
    private static readonly UiTextBundle Ui = UiTextBundle.English;

    [TestMethod]
    [DataRow(false, false, null, null)]
    [DataRow(true, false, null, MediaAvailabilityState.Local)]
    [DataRow(true, true, null, MediaAvailabilityState.Available)]
    [DataRow(false, false, AcquisitionRequestStatus.Pending, MediaAvailabilityState.Requested)]
    [DataRow(true, false, AcquisitionRequestStatus.Downloading, MediaAvailabilityState.Requested)]
    [DataRow(true, true, AcquisitionRequestStatus.Downloading, MediaAvailabilityState.Available)]
    public void StateFollowsPresenceAndOpenRequests(
        bool inLibrary,
        bool playable,
        AcquisitionRequestStatus? request,
        MediaAvailabilityState? expected)
    {
        Assert.AreEqual(expected, MediaAvailability.Resolve(new MediaAvailabilityFacts(inLibrary, playable, request)));
    }

    [TestMethod]
    [DataRow(AcquisitionRequestStatus.Completed)]
    [DataRow(AcquisitionRequestStatus.Rejected)]
    [DataRow(AcquisitionRequestStatus.Failed)]
    public void FinishedRequestsSayNothingAboutTheTitle(AcquisitionRequestStatus status)
    {
        Assert.IsNull(MediaAvailability.Resolve(new MediaAvailabilityFacts(false, false, status)));
        Assert.AreEqual(MediaAvailabilityState.Local, MediaAvailability.Resolve(new MediaAvailabilityFacts(true, false, status)));
    }

    [TestMethod]
    public void CardBadgeNamesTheStateAndTheStageOfARequest()
    {
        var local = Card(new MediaAvailabilityFacts(true, false)).Availability;
        Assert.AreEqual(MediaAvailabilityState.Local, local?.State);
        Assert.AreEqual("In library", local?.Label);
        Assert.AreEqual("local", local?.CssModifier);

        var available = Card(new MediaAvailabilityFacts(true, true)).Availability;
        Assert.AreEqual("Available", available?.Label);
        Assert.AreEqual("available", available?.CssModifier);

        // A requested title uses the words of the request lists for the stage it is in.
        Assert.AreEqual("Requested", Card(new MediaAvailabilityFacts(false, false, AcquisitionRequestStatus.Pending)).Availability?.Label);
        Assert.AreEqual("Approved", Card(new MediaAvailabilityFacts(false, false, AcquisitionRequestStatus.Approved)).Availability?.Label);
        Assert.AreEqual("Searching", Card(new MediaAvailabilityFacts(false, false, AcquisitionRequestStatus.Searching)).Availability?.Label);
        Assert.AreEqual("Downloading", Card(new MediaAvailabilityFacts(false, false, AcquisitionRequestStatus.Downloading)).Availability?.Label);
        Assert.AreEqual("Importing", Card(new MediaAvailabilityFacts(false, false, AcquisitionRequestStatus.Importing)).Availability?.Label);
        Assert.AreEqual("requested", Card(new MediaAvailabilityFacts(false, false, AcquisitionRequestStatus.Pending)).Availability?.CssModifier);
    }

    [TestMethod]
    public void CardWithoutFactsOrWithNothingToSayShowsNoBadge()
    {
        Assert.IsNull(Card(null).Availability, "A card never guesses at availability.");
        Assert.IsNull(Card(new MediaAvailabilityFacts(false, false)).Availability);
        Assert.IsNull(Card(new MediaAvailabilityFacts(false, false, AcquisitionRequestStatus.Completed)).Availability);
    }

    [TestMethod]
    public void AvailableIsNotRepeatedOnACardThatAlreadyOffersToPlay()
    {
        var withAction = MediaBannerCardModel.Create(
            Data(new MediaAvailabilityFacts(true, true)) with
            {
                Progress = new MediaBannerProgress(MediaBannerProgressState.NotStarted, MediaBannerUnit.Episode, 1, "/Library/Episode/1")
            },
            Ui);

        Assert.IsNotNull(withAction.Action);
        Assert.IsNull(withAction.Availability);

        var requestedWithAction = MediaBannerCardModel.Create(
            Data(new MediaAvailabilityFacts(true, false, AcquisitionRequestStatus.Searching)) with
            {
                Progress = new MediaBannerProgress(MediaBannerProgressState.NotStarted, MediaBannerUnit.Episode, 1, "/Library/Episode/1")
            },
            Ui);
        Assert.AreEqual("Searching", requestedWithAction.Availability?.Label, "Only the redundant Available badge is dropped.");
    }

    [TestMethod]
    public async Task LibraryCardsCarryWhatTheLibraryAndTheOpenRequestsKnowAsync()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        await seed.AddAnimeAsync("playable", [(1, 1, true)]);
        await seed.AddAnimeAsync("empty", [(1, 1, false)]);
        var requested = await seed.AddAnimeAsync("requested", [(1, 1, false)]);
        var finished = await seed.AddAnimeAsync("finished", [(1, 1, false)]);
        await AddMatchAsync(fixture.Db, requested.Anime, "42");
        await AddMatchAsync(fixture.Db, finished.Anime, "7");

        var store = new AcquisitionAccessStore(fixture.Db);
        await store.CreateAsync(AnimeDraft("42"), "alice", AcquisitionRequestStatus.Searching, "owner", CancellationToken.None);
        await store.CreateAsync(AnimeDraft("7"), "alice", AcquisitionRequestStatus.Completed, "owner", CancellationToken.None);

        var cards = (await new LibraryMediaCardQuery(fixture.Db).GetAnimeEntriesAsync("alice", CancellationToken.None))
            .Entries
            .ToDictionary(entry => entry.Card.Title, entry => entry.Card);
        var badges = cards.ToDictionary(pair => pair.Key, pair => MediaBannerCardModel.Create(pair.Value, Ui).Availability?.Label);

        Assert.IsNull(badges["playable"], "It has a play button already.");
        Assert.AreEqual("In library", badges["empty"]);
        Assert.AreEqual("Searching", badges["requested"]);
        Assert.AreEqual("In library", badges["finished"], "A finished request no longer says anything.");
        Assert.AreEqual(true, cards["playable"].Availability?.HasPlayableContent);
        Assert.AreEqual(false, cards["empty"].Availability?.HasPlayableContent);
    }

    private static MediaBannerCardModel Card(MediaAvailabilityFacts? facts) =>
        MediaBannerCardModel.Create(Data(facts), Ui);

    private static MediaBannerCardData Data(MediaAvailabilityFacts? facts) =>
        new(MediaBannerKind.Anime, "Akatsuki no Sora", "/Library/Anime/1", Availability: facts);

    private static AcquisitionRequestDraft AnimeDraft(string aniListId) =>
        new(MediaAcquisitionKind.Anime, AniListMetadataProvider.ProviderKey, aniListId, $"Anime {aniListId}", null, null);

    private static async Task AddMatchAsync(AppDbContext db, Anime anime, string aniListId)
    {
        db.AnimeMetadata.Add(new AnimeMetadata
        {
            AnimeId = anime.Id,
            Provider = AniListMetadataProvider.ProviderKey,
            ExternalId = aniListId,
            PreferredTitle = anime.Title
        });
        await db.SaveChangesAsync();
    }
}
