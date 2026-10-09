using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Watchlist;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class WatchlistTests
{
    [TestMethod]
    public async Task LocalFollowStateOverridesFranchiseInheritance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var watchlist = new WatchlistStore(fixture.Db);
        var franchises = new FranchiseStore(fixture.Db);
        const string profile = "profile-a";

        var seed = Draft("100", "Series A");
        var sequel = Draft("101", "Series A 2");
        var franchiseId = await franchises.GetOrCreateBySeedAsync(seed.Identity, CancellationToken.None);
        await franchises.UpsertMemberAsync(franchiseId, seed, null, true, CancellationToken.None);
        await franchises.UpsertMemberAsync(
            franchiseId,
            sequel,
            "SEQUEL",
            false,
            CancellationToken.None);
        await franchises.FollowAsync(profile, franchiseId, CancellationToken.None);

        var inherited = await watchlist.GetEffectiveAsync(profile, CancellationToken.None);
        Assert.AreEqual(2, inherited.Count);
        Assert.IsTrue(inherited.All(item => item.IsFromFranchise));

        await watchlist.UnfollowAsync(profile, sequel.Identity, CancellationToken.None);
        var excluded = await watchlist.GetEffectiveAsync(profile, CancellationToken.None);
        Assert.AreEqual(1, excluded.Count);
        Assert.AreEqual(seed.Identity.Key, excluded.Single().Identity.Key);
        CollectionAssert.AreEqual(
            new[] { sequel.Identity.Key },
            (await watchlist.GetHiddenKeysAsync(profile, CancellationToken.None)).ToArray());

        await watchlist.RestoreAsync(profile, sequel.Identity, CancellationToken.None);
        var shownAgain = await watchlist.GetEffectiveAsync(profile, CancellationToken.None);
        Assert.AreEqual(2, shownAgain.Count, "Hiding a franchise work can be undone.");
        Assert.AreEqual(0, (await watchlist.GetHiddenKeysAsync(profile, CancellationToken.None)).Count);

        await watchlist.FollowAsync(profile, sequel, CancellationToken.None);
        var restored = await watchlist.GetEffectiveAsync(profile, CancellationToken.None);
        Assert.AreEqual(2, restored.Count);
        var explicitSequel = restored.Single(item => item.Identity.Key == sequel.Identity.Key);
        Assert.IsTrue(explicitSequel.IsExplicit);
        Assert.AreEqual(franchiseId, explicitSequel.FranchiseId);
    }

    [TestMethod]
    public async Task RemoteFollowedAnimeUsesCachedReleaseDataInProfilesCalendar()
    {
        await using var fixture = await Fixture.CreateAsync();
        var watchlist = new WatchlistStore(fixture.Db);
        var cache = new ReleaseCalendarCacheStore(fixture.Db);
        const string profile = "profile-a";
        var followed = Draft("154587", "Frieren");

        await watchlist.FollowAsync(profile, followed, CancellationToken.None);
        var airing = ReleaseDate.FromInstant(
            new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero));
        await cache.SaveAsync(
            "anilist",
            [
                new ReleaseSourceSnapshot(
                    "154587",
                    "RELEASING",
                    [new CachedRelease("anilist", "154587", ReleaseKind.Episode, 3, airing)])
            ],
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        var source = new WatchlistReleaseEventSource(cache, watchlist, new WatchlistLibraryResolver(fixture.Db));
        var query = new ReleaseEventQuery(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31),
            TimeZoneInfo.Utc,
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            ProfileId: profile);

        var events = await source.GetEventsAsync(query, CancellationToken.None);
        var release = events.Single();
        Assert.AreEqual(ReleaseMediaType.Anime, release.MediaType);
        Assert.AreEqual(3, release.Unit?.Number);
        Assert.AreEqual(ReleaseLocalState.Following, release.Local.State);
        Assert.IsNull(release.Local.Monitored);
        Assert.IsFalse(release.Local.InLibrary);
        Assert.AreEqual(followed.Identity.WatchlistUrl, release.DetailsUrl);
        CollectionAssert.AreEquivalent(
            new[] { ReleaseMediaType.Anime, ReleaseMediaType.Manga, ReleaseMediaType.LightNovel },
            source.MediaTypes.ToArray(),
            "No TV or movie filter without a source for them.");

        var otherProfile = await source.GetEventsAsync(
            query with { ProfileId = "profile-b" },
            CancellationToken.None);
        Assert.AreEqual(0, otherProfile.Count);
    }

    [TestMethod]
    public async Task CalendarReadsReleaseFromSecondBoundedWatchlistPage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var watchlist = new WatchlistStore(fixture.Db);
        var cache = new ReleaseCalendarCacheStore(fixture.Db);
        const string profile = "profile-a";

        for (var i = 0; i <= PageRequest.MaximumPageSize; i++)
        {
            await watchlist.FollowAsync(
                profile,
                Draft((200000 + i).ToString(), $"Item {i:D3}"),
                CancellationToken.None);
        }

        const string lastExternalId = "200100";
        var airing = ReleaseDate.FromInstant(
            new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero));
        await cache.SaveAsync(
            "anilist",
            [
                new ReleaseSourceSnapshot(
                    lastExternalId,
                    "RELEASING",
                    [new CachedRelease("anilist", lastExternalId, ReleaseKind.Episode, 5, airing)])
            ],
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        var source = new WatchlistReleaseEventSource(
            cache,
            watchlist,
            new WatchlistLibraryResolver(fixture.Db));
        var events = await source.GetEventsAsync(
            new ReleaseEventQuery(
                new DateOnly(2026, 10, 1),
                new DateOnly(2026, 10, 31),
                TimeZoneInfo.Utc,
                new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
                ProfileId: profile),
            CancellationToken.None);

        var release = Assert.ContainsSingle(events);
        Assert.AreEqual(lastExternalId, release.ProviderExternalId);
        Assert.AreEqual(5, release.Unit?.Number);
    }

    [TestMethod]
    public void WatchlistInputRejectsUnsafeImageUrls()
    {
        Assert.IsTrue(WatchlistDraftInput.TryCreate(
            "anime",
            "anilist",
            "1",
            "Title",
            null,
            "javascript:alert(1)",
            "TV",
            "RELEASING",
            2026,
            out var unsafeCover));
        Assert.IsNull(unsafeCover.CoverImageUrl);

        Assert.IsTrue(WatchlistDraftInput.TryCreate(
            "anime", "anilist", "1", "Title", null, "https://cdn.example/cover.jpg", "TV", "RELEASING", 2026, out var draft));
        Assert.AreEqual("https://cdn.example/cover.jpg", draft.CoverImageUrl);
        Assert.AreEqual("https://anilist.co/anime/1", draft.Identity.ProviderUrl, "Links are built from the identity.");
    }

    [TestMethod]
    public async Task LibraryMembershipIsResolvedWhenShown()
    {
        await using var fixture = await Fixture.CreateAsync();
        var watchlist = new WatchlistStore(fixture.Db);
        var resolver = new WatchlistLibraryResolver(fixture.Db);
        await watchlist.FollowAsync("profile-a", Draft("154587", "Frieren"), CancellationToken.None);

        var before = (await resolver.ApplyAsync(await watchlist.GetEffectiveAsync("profile-a", CancellationToken.None), CancellationToken.None)).Single();
        Assert.IsNull(before.LocalMediaId);
        Assert.AreEqual("https://anilist.co/anime/154587", before.DetailsUrl);

        var anime = new Jularr.Web.Features.Library.Anime { Key = "frieren", Title = "Frieren" };
        fixture.Db.Anime.Add(anime);
        fixture.Db.AnimeMetadata.Add(new Jularr.Web.Features.Metadata.AnimeMetadata
        {
            AnimeId = anime.Id,
            Provider = "anilist",
            ExternalId = "154587",
            PreferredTitle = "Frieren"
        });
        await fixture.Db.SaveChangesAsync();

        var after = (await resolver.ApplyAsync(await watchlist.GetEffectiveAsync("profile-a", CancellationToken.None), CancellationToken.None)).Single();
        Assert.AreEqual(anime.Id, after.LocalMediaId);
        Assert.AreEqual($"/Library/Anime/{anime.Id}", after.DetailsUrl);
    }

    [TestMethod]
    public async Task ClientWatchlistItemsAreScopedToTheCallingProfileAndCarryAnAddedDate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var watchlist = new WatchlistStore(fixture.Db);
        var resolver = new WatchlistLibraryResolver(fixture.Db);

        await watchlist.FollowAsync("profile-a", Draft("154587", "Frieren"), CancellationToken.None);
        await watchlist.FollowAsync("profile-b", Draft("999", "Someone Else's Show"), CancellationToken.None);

        var items = await resolver.ApplyAsync(
            await watchlist.GetEffectiveAsync("profile-a", CancellationToken.None),
            CancellationToken.None);
        var mapped = items.Select(ClientApiMappings.ToClientWatchlistItem).ToArray();

        var item = Assert.ContainsSingle(mapped, "Only profile-a's own entry is present, never profile-b's.");
        Assert.AreEqual("anime", item.MediaType);
        Assert.AreEqual("Frieren", item.Title);
        Assert.AreEqual("https://cdn.example/cover.jpg", item.ArtworkUrl);
        Assert.AreEqual("external", item.Availability, "Not matched to a library entry yet.");
        Assert.AreEqual("https://anilist.co/anime/154587", item.DetailsUrl);
        Assert.IsNotNull(item.AddedAtUtc);
        Assert.AreEqual(DateTimeKind.Utc, item.AddedAtUtc!.Value.Kind);

        var anime = new Jularr.Web.Features.Library.Anime { Key = "frieren", Title = "Frieren" };
        fixture.Db.Anime.Add(anime);
        fixture.Db.AnimeMetadata.Add(new Jularr.Web.Features.Metadata.AnimeMetadata
        {
            AnimeId = anime.Id,
            Provider = "anilist",
            ExternalId = "154587",
            PreferredTitle = "Frieren"
        });
        await fixture.Db.SaveChangesAsync();

        var afterLibraryMatch = (await resolver.ApplyAsync(
            await watchlist.GetEffectiveAsync("profile-a", CancellationToken.None),
            CancellationToken.None))
            .Select(ClientApiMappings.ToClientWatchlistItem)
            .Single();
        Assert.AreEqual("in_library", afterLibraryMatch.Availability);
        Assert.AreEqual($"/Library/Anime/{anime.Id}", afterLibraryMatch.DetailsUrl);
    }

    [TestMethod]
    public async Task FranchiseInheritedWatchlistItemsHaveNoIndividualAddedDate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var watchlist = new WatchlistStore(fixture.Db);
        var franchises = new FranchiseStore(fixture.Db);
        const string profile = "profile-a";
        var seed = Draft("100", "Series A");
        var franchiseId = await franchises.GetOrCreateBySeedAsync(seed.Identity, CancellationToken.None);
        await franchises.UpsertMemberAsync(franchiseId, seed, null, true, CancellationToken.None);
        await franchises.FollowAsync(profile, franchiseId, CancellationToken.None);

        var item = Assert.ContainsSingle(await watchlist.GetEffectiveAsync(profile, CancellationToken.None));
        Assert.IsTrue(item.IsFromFranchise);
        Assert.IsNull(item.AddedAtUtc, "A work only followed through its franchise was never individually added.");
        Assert.IsNull(ClientApiMappings.ToClientWatchlistItem(item).AddedAtUtc);

        await watchlist.FollowAsync(profile, seed, CancellationToken.None);
        var explicitItem = Assert.ContainsSingle(await watchlist.GetEffectiveAsync(profile, CancellationToken.None));
        Assert.IsNotNull(explicitItem.AddedAtUtc, "Explicitly following a franchise work now records when it was added.");
    }


    [TestMethod]
    public async Task PagedEffectiveRead_MergesFranchisesOverridesAndIgnoresBeforePaging()
    {
        await using var fixture = await Fixture.CreateAsync();
        var watchlist = new WatchlistStore(fixture.Db);
        var franchises = new FranchiseStore(fixture.Db);
        const string profile = "profile-a";

        var inherited = Draft("100", "Original");
        var ignored = Draft("101", "Hidden");
        var franchiseId = await franchises.GetOrCreateBySeedAsync(
            inherited.Identity,
            CancellationToken.None);
        await franchises.UpsertMemberAsync(franchiseId, inherited, null, true, CancellationToken.None);
        await franchises.UpsertMemberAsync(franchiseId, ignored, "SEQUEL", false, CancellationToken.None);
        await franchises.FollowAsync(profile, franchiseId, CancellationToken.None);

        await watchlist.FollowAsync(profile, Draft("100", "Override"), CancellationToken.None);
        await watchlist.UnfollowAsync(profile, ignored.Identity, CancellationToken.None);
        await watchlist.FollowAsync(profile, Draft("200", "Alpha"), CancellationToken.None);
        await watchlist.FollowAsync(profile, Draft("201", "Beta"), CancellationToken.None);
        await watchlist.FollowAsync(profile, Draft("202", "Gamma"), CancellationToken.None);
        await watchlist.FollowAsync(
            profile,
            new WatchlistDraft(new WatchlistIdentity(WatchlistMediaType.Manga, "anilist", "300"), "Manga"),
            CancellationToken.None);
        await watchlist.FollowAsync("profile-b", Draft("400", "Secret"), CancellationToken.None);

        var account = CurrentAccountContext.ForProfile(profile);
        var visible = new[] { WatchlistMediaType.Anime };
        var first = await watchlist.GetEffectivePageAsync(account, new PageRequest(1, 2), visible, CancellationToken.None);
        var second = await watchlist.GetEffectivePageAsync(account, new PageRequest(2, 2), visible, CancellationToken.None);
        var beyond = await watchlist.GetEffectivePageAsync(account, new PageRequest(3, 2), visible, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "Alpha", "Beta" }, first.Items.Select(item => item.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "Gamma", "Override" }, second.Items.Select(item => item.Title).ToArray());
        Assert.AreEqual(4L, first.TotalCount);
        Assert.AreEqual(true, first.HasMore);
        Assert.AreEqual(false, second.HasMore);
        Assert.IsEmpty(beyond.Items);
        Assert.AreEqual(4L, beyond.TotalCount);
        var overridden = second.Items.Single(item => item.Title == "Override");
        Assert.AreEqual(franchiseId, overridden.FranchiseId);
        Assert.IsTrue(overridden.IsExplicit);

        var addressed = await watchlist.GetEffectivePageAsync(
            account,
            new PageRequest(),
            visible,
            CancellationToken.None,
            inherited.Identity);
        var targetItem = Assert.ContainsSingle(addressed.Items);
        Assert.AreEqual("Override", targetItem.Title);
        Assert.AreEqual(1L, addressed.TotalCount);

        var hiddenTarget = await watchlist.GetEffectivePageAsync(
            account,
            new PageRequest(),
            visible,
            CancellationToken.None,
            ignored.Identity);
        Assert.IsEmpty(hiddenTarget.Items);

        var other = await watchlist.GetEffectivePageAsync(
            CurrentAccountContext.ForProfile("profile-b"),
            new PageRequest(),
            visible,
            CancellationToken.None);
        Assert.AreEqual("Secret", Assert.ContainsSingle(other.Items).Title);
        Assert.AreEqual(1L, other.TotalCount);
    }

    [TestMethod]
    public async Task FollowedFranchises_ArePagedAndProfileScoped()
    {
        await using var fixture = await Fixture.CreateAsync();
        var franchises = new FranchiseStore(fixture.Db);
        for (var i = 1; i <= 3; i++)
        {
            var item = Draft(i.ToString(), "Franchise " + i);
            var id = await franchises.GetOrCreateBySeedAsync(item.Identity, CancellationToken.None);
            await franchises.FollowAsync("profile-a", id, CancellationToken.None);
        }

        var first = await franchises.ListFollowedAsync(
            "profile-a", new PageRequest(1, 2), CancellationToken.None);
        var second = await franchises.ListFollowedAsync(
            "profile-a", new PageRequest(2, 2), CancellationToken.None);
        var otherProfile = await franchises.ListFollowedAsync(
            "profile-b", new PageRequest(), CancellationToken.None);

        Assert.AreEqual(2, first.Items.Count);
        Assert.AreEqual(1, second.Items.Count);
        Assert.AreEqual(3L, first.TotalCount);
        Assert.AreEqual(true, first.HasMore);
        Assert.AreEqual(false, second.HasMore);
        Assert.IsEmpty(otherProfile.Items);
        Assert.AreEqual(0L, otherProfile.TotalCount);
    }

    [TestMethod]
    public async Task DiscoverFollowLookup_FiltersRequestedKeysAndCurrentProfile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new WatchlistStore(fixture.Db);
        var franchises = new FranchiseStore(fixture.Db);
        var inherited = Draft("800", "Inherited");
        var hidden = Draft("801", "Hidden");
        var explicitOnly = Draft("802", "Explicit");
        var foreign = Draft("803", "Other account");
        var franchiseId = await franchises.GetOrCreateBySeedAsync(
            inherited.Identity, CancellationToken.None);
        await franchises.UpsertMemberAsync(
            franchiseId, inherited, null, true, CancellationToken.None);
        await franchises.UpsertMemberAsync(
            franchiseId, hidden, "SEQUEL", false, CancellationToken.None);
        await franchises.FollowAsync("profile-a", franchiseId, CancellationToken.None);
        await store.FollowAsync("profile-a", inherited, CancellationToken.None);
        await store.FollowAsync("profile-a", explicitOnly, CancellationToken.None);
        await store.UnfollowAsync("profile-a", hidden.Identity, CancellationToken.None);
        await store.FollowAsync("profile-b", foreign, CancellationToken.None);

        // Force a second SQL batch: the matching keys come after more than 100 misses.
        var selected = Enumerable.Range(0, PageRequest.MaximumPageSize + 5)
            .Select(i => Draft("missing-" + i, "Not followed").Identity)
            .Concat([
                inherited.Identity,
                hidden.Identity,
                explicitOnly.Identity,
                foreign.Identity,
                inherited.Identity
            ])
            .ToArray();
        var result = await store.GetFollowedFranchiseIdsForKeysAsync(
            CurrentAccountContext.ForProfile("profile-a"),
            selected,
            CancellationToken.None);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(franchiseId, result[inherited.Identity.Key]);
        Assert.IsTrue(result.ContainsKey(explicitOnly.Identity.Key));
        Assert.IsNull(result[explicitOnly.Identity.Key]);
        Assert.IsFalse(result.ContainsKey(hidden.Identity.Key));
        Assert.IsFalse(result.ContainsKey(foreign.Identity.Key));

        var other = await store.GetFollowedFranchiseIdsForKeysAsync(
            CurrentAccountContext.ForProfile("profile-b"),
            selected,
            CancellationToken.None);
        Assert.AreEqual(1, other.Count);
        Assert.IsTrue(other.ContainsKey(foreign.Identity.Key));
        Assert.IsNull(other[foreign.Identity.Key]);

        var empty = await store.GetFollowedFranchiseIdsForKeysAsync(
            CurrentAccountContext.ForProfile("profile-a"),
            [],
            CancellationToken.None);
        Assert.IsEmpty(empty);
    }

    private static WatchlistDraft Draft(string externalId, string title) =>
        new(
            new WatchlistIdentity(WatchlistMediaType.Anime, "anilist", externalId),
            title,
            CoverImageUrl: "https://cdn.example/cover.jpg",
            Format: "TV",
            Status: "RELEASING",
            Year: 2026);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;

        private Fixture(string directory, AppDbContext db)
        {
            this.directory = directory;
            Db = db;
        }

        public AppDbContext Db { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"jularr-watchlist-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(directory, "app.db")};Foreign Keys=True")
                    .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new Fixture(directory, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
