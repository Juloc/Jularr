using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Policy;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// One Acquisition Profile owns the wait and the sources for every media type (#396): the same profile fields, the same selection engine and the same
/// search options drive Movie, TV, Anime, Book, Light Novel, Manga and Music, and the legacy Anime delay policy moves into the profile.
/// </summary>
[TestClass]
public sealed class AcquisitionProfilePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static MediaAcquisitionRegistry Registry() => new(
    [
        new AnimeAcquisitionRegistration(), new MovieAcquisitionRegistration(), new TvAcquisitionRegistration(), new BookAcquisitionRegistration(),
        new MusicAcquisitionRegistration(), new MangaAcquisitionRegistration(), new LightNovelAcquisitionRegistration()
    ]);

    /// <summary>One release name per media kind whose quality the kind's default profile ranks.</summary>
    public static IEnumerable<object[]> KindsWithAReleaseName() =>
    [
        [MediaAcquisitionKind.Movie, "Dune.2021.720p.WEB-DL.H264-GRP"],
        [MediaAcquisitionKind.Tv, "Show.S01E01.720p.WEB-DL.H264-GRP"],
        [MediaAcquisitionKind.Anime, "Show.S01E01.720p.WEB-DL.H264-GRP"],
        [MediaAcquisitionKind.Book, "Frank Herbert - Dune (1965) PDF"],
        [MediaAcquisitionKind.LightNovel, "Frieren Vol 01 ZIP"],
        [MediaAcquisitionKind.Manga, "Frieren Vol 01 ZIP"],
        [MediaAcquisitionKind.Music, "Artist - Album (2020) MP3-256"]
    ];

    [TestMethod]
    [DynamicData(nameof(KindsWithAReleaseName))]
    public void OneWaitPolicyAppliesToEveryMediaKindFromTheStoredStartOfTheWait(MediaAcquisitionKind kind, string releaseName)
    {
        var registry = Registry();
        var parser = registry.ParserFor(kind);
        var quality = ReleaseQuality.GetKey(parser.Parse(releaseName));
        var basic = registry.DefaultProfileFor(kind);
        Assert.IsTrue(basic.QualityOrder.Contains(quality, StringComparer.OrdinalIgnoreCase), $"{kind} ranks {quality}.");
        var profile = basic with { AllowedQualities = [.. basic.QualityOrder.Where(item => !item.Equals(quality, StringComparison.OrdinalIgnoreCase))], FallbackTiers = [new FallbackTier(90, [quality])], MinimumScore = 0 };
        var stored = Now.AddMinutes(-30);

        var waiting = ProfileTest.Run(profile, parser, releaseName, null, null, Now - stored, Now);
        var restartedNearTheEnd = ProfileTest.Run(profile, parser, releaseName, null, null, TimeSpan.FromMinutes(89), Now);
        var afterTheWait = ProfileTest.Run(profile, parser, releaseName, null, null, TimeSpan.FromMinutes(90), Now);

        Assert.AreEqual(SelectionDecision.Rejected, waiting.Decision);
        Assert.AreEqual(stored.AddMinutes(90), waiting.EligibleAt, "The wait ends a fixed time after the stored start, whenever the engine is asked.");
        Assert.IsFalse(waiting.WouldGrab);
        Assert.IsTrue(waiting.Reasons.Any(reason => reason.Contains("fallback tier 1") && reason.Contains($"{Now.AddMinutes(60):u}")), "Manual Search can say when and why.");
        Assert.IsNotNull(restartedNearTheEnd.EligibleAt);
        Assert.AreEqual(SelectionDecision.Temporary, afterTheWait.Decision, "After the wait the fallback quality is taken, and the title stays wanted for the better one.");
        Assert.IsNull(afterTheWait.EligibleAt);
        Assert.IsTrue(afterTheWait.WouldGrab);
    }

    [TestMethod]
    public void TheProfileTestNamesTheIndexerAProfileDoesNotSearch()
    {
        var registry = Registry();
        var allowed = Guid.NewGuid();
        var profile = registry.DefaultProfileFor(MediaAcquisitionKind.Movie) with { SourcePolicy = new AcquisitionSourcePolicy([allowed], []) };

        var fromAllowed = ProfileTest.Run(profile, registry.ParserFor(MediaAcquisitionKind.Movie), "Dune.2021.1080p.WEB-DL.H264-GRP", null, allowed, TimeSpan.Zero, Now);
        var fromOther = ProfileTest.Run(profile, registry.ParserFor(MediaAcquisitionKind.Movie), "Dune.2021.1080p.WEB-DL.H264-GRP", null, Guid.NewGuid(), TimeSpan.Zero, Now);

        Assert.IsTrue(fromAllowed.WouldGrab);
        Assert.IsNull(fromAllowed.SourceProblem);
        Assert.IsFalse(fromOther.WouldGrab, "A good release from an indexer the profile never asks is not found.");
        Assert.IsNotNull(fromOther.SourceProblem);
    }

    [TestMethod]
    public async Task ASourcePolicySurvivesEditingAndStorageAndAPreferenceOutsideTheListIsNotKept()
    {
        var directory = Directory.CreateTempSubdirectory("jularr-profile-sources-");
        try
        {
            var registry = Registry();
            var store = new QualityProfileStore(directory, registry);
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var stranger = Guid.NewGuid();
            var form = QualityProfileEditing.ToForm(registry.DefaultProfileFor(MediaAcquisitionKind.Movie));
            form.Id = "movie-restricted";
            form.Name = "Movie restricted";
            form.AllowedSources = [a, b];
            form.PreferredSources = [b, stranger];

            var parsed = QualityProfileEditing.Parse(form);
            await store.UpsertAsync(parsed.Profile!);
            var reloaded = (await new QualityProfileStore(directory, registry).LoadAsync()).Profiles.Single(profile => profile.Id == "movie-restricted");

            Assert.AreEqual(0, parsed.Errors.Count);
            CollectionAssert.AreEquivalent(new[] { a, b }, reloaded.SourcePolicy.AllowedEntryIds);
            CollectionAssert.AreEqual(new[] { b }, reloaded.SourcePolicy.PreferredEntryIds, "A preferred indexer the profile may not search could never be asked.");
            Assert.IsFalse(QualityProfileEditing.Parse(QualityProfileEditing.ToForm(registry.DefaultProfileFor(MediaAcquisitionKind.Movie))).Profile!.SourcePolicy.IsRestricted);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void TheBookAndReadingSelectorsHonourTheWaitFromTheRequestsStoredStart()
    {
        var book = BookQualityProfiles.CreateDefaultBook();
        var bookQuality = ReleaseQuality.GetKey(BookReleaseParser.Instance.Parse("Frank Herbert - Dune (1965) PDF"));
        var bookProfile = book with { AllowedQualities = [.. book.QualityOrder.Where(item => !item.Equals(bookQuality, StringComparison.OrdinalIgnoreCase))], FallbackTiers = [new FallbackTier(90, [bookQuality])] };
        var pdf = Release("Frank Herbert - Dune (1965) PDF");

        var bookWaiting = BookReleaseSelector.Rank([pdf], "Dune", "Frank Herbert", bookProfile, null, DateTimeOffset.UtcNow.AddMinutes(-30));
        var bookDue = BookReleaseSelector.Rank([pdf], "Dune", "Frank Herbert", bookProfile, null, DateTimeOffset.UtcNow.AddMinutes(-100));

        Assert.AreEqual(0, bookWaiting[0].Score);
        StringAssert.Contains(bookWaiting[0].RejectedBecause, "fallback tier 1", "Manual Search says what it waits for and until when.");
        Assert.IsTrue(bookDue[0].Score > 0);

        var target = new ReadingAcquisitionTarget(MediaAcquisitionKind.LightNovel, "Frieren", [], null, 1);
        var reading = ReadingQualityProfiles.For(MediaAcquisitionKind.LightNovel);
        var readingQuality = reading.QualityOrder[0];
        var readingProfile = reading with { AllowedQualities = [.. reading.QualityOrder.Skip(1)], FallbackTiers = [new FallbackTier(90, [readingQuality])] };
        var volume = Release($"Frieren Vol 01 {readingQuality}");

        var readingWaiting = ReadingReleaseSelector.Rank([volume], target, readingProfile, null, DateTimeOffset.UtcNow.AddMinutes(-30));
        var readingDue = ReadingReleaseSelector.Rank([volume], target, readingProfile, null, DateTimeOffset.UtcNow.AddMinutes(-100));

        Assert.AreEqual(0, readingWaiting[0].Score);
        Assert.IsTrue(readingDue[0].Score > 0);
    }

    [TestMethod]
    public void NoPipelineStartsTheWaitOverAtEverySearchAndEveryProfileSearchAppliesItsSourcePolicy()
    {
        var web = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Features");
        var offenders = Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("new SelectionContext(now, now)", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        var searches = new (string File, int Minimum)[]
        {
            ("Acquisition/Access/VideoAcquisitionRequestExecutor.cs", 1), ("Acquisition/Pipeline/AnimeAcquisitionPipeline.cs", 2), ("Books/BookAcquisitionExecutor.cs", 1),
            ("ReadingAcquisition/ReadingReleaseMatching.cs", 1), ("Music/MusicAcquisition.cs", 1)
        };

        Assert.AreEqual(0, offenders.Length, "A selection that is asked with 'wanted since now' can never reach a fallback tier: " + string.Join(", ", offenders));
        foreach (var (file, minimum) in searches)
        {
            var text = File.ReadAllText(Path.Combine(web, file));
            Assert.IsTrue(text.Split("WithSourcePolicy(").Length - 1 >= minimum, $"{file} applies the profile's source policy to its searches.");
        }
    }

    [TestMethod]
    public void ADelayProfileMovesIntoItsQualityProfileAndBehavesTheSameAsTheLegacyTranslation()
    {
        var registry = Registry();
        var anime = registry.DefaultProfileFor(MediaAcquisitionKind.Anime);
        var delay = new AnimeDelayProfile("d1", "Wait for Blu-ray", 120, anime.Id, [], false);
        var parser = registry.ParserFor(MediaAcquisitionKind.Anime);
        var legacy = AcquisitionDelayEngine.WithDelayAsFallbackTier(anime, delay);

        var plan = LegacyDelayMigration.Build([anime], [delay], new HashSet<string>());
        var migrated = plan.ChangedProfiles.Single();
        var again = LegacyDelayMigration.Build([migrated], [delay], new HashSet<string>());

        CollectionAssert.AreEqual(legacy.FallbackTiers.Select(tier => (tier.AfterMinutes, string.Join(',', tier.AddedQualities))).ToArray(), migrated.FallbackTiers.Select(tier => (tier.AfterMinutes, string.Join(',', tier.AddedQualities))).ToArray());
        CollectionAssert.AreEqual(legacy.AllowedQualities, migrated.AllowedQualities);
        Assert.IsTrue(plan.MigratedDelayIds.Contains("d1"));
        Assert.AreEqual(0, again.ChangedProfiles.Count, "Applying the migration twice changes nothing.");
        Assert.IsTrue(again.MigratedDelayIds.Contains("d1"), "A restart after the profile was written still removes the legacy entry.");
        foreach (var minutes in new[] { 0, 119, 120 })
        {
            Assert.AreEqual(
                ProfileTest.Run(legacy, parser, "Show.S01E01.720p.WEB-DL.H264-GRP", null, null, TimeSpan.FromMinutes(minutes), Now).Decision,
                ProfileTest.Run(migrated, parser, "Show.S01E01.720p.WEB-DL.H264-GRP", null, null, TimeSpan.FromMinutes(minutes), Now).Decision);
        }
    }

    [TestMethod]
    public void OnlyDelayProfilesWithoutATagMoveAndAGlobalOneReachesOnlyTheProfilesAnimeUses()
    {
        var registry = Registry();
        var anime = registry.DefaultProfileFor(MediaAcquisitionKind.Anime);
        var movie = registry.DefaultProfileFor(MediaAcquisitionKind.Movie);
        var specific = anime with { Id = "anime-specific", Name = "Anime specific" };
        var global = new AnimeDelayProfile("global", "Everything", 60, null, [], true);
        var scoped = new AnimeDelayProfile("scoped", "Specific", 240, "anime-specific", [], false);
        var tagged = new AnimeDelayProfile("tagged", "Fast track", 30, "other-profile", ["fast"], false);
        var none = new AnimeDelayProfile("none", "No wait", 0, null, [], false);

        var plan = LegacyDelayMigration.Build([anime, movie, specific], [global, scoped, tagged, none], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { anime.Id, "anime-specific" });

        CollectionAssert.AreEquivalent(new[] { "global", "scoped", "none" }, plan.MigratedDelayIds.ToArray());
        Assert.IsFalse(plan.MigratedDelayIds.Contains("tagged"), "A tag-scoped delay profile has no profile to move to and stays, applied by the Anime compatibility path.");
        Assert.AreEqual(60, plan.ChangedProfiles.Single(profile => profile.Id == anime.Id).FallbackTiers.Last().AfterMinutes);
        Assert.AreEqual(240, plan.ChangedProfiles.Single(profile => profile.Id == "anime-specific").FallbackTiers.Last().AfterMinutes, "The more specific delay profile wins, as it always did.");
        Assert.IsFalse(plan.ChangedProfiles.Any(profile => profile.Id == movie.Id), "A global Anime delay never leaks into another media kind's default profile.");
    }

    [TestMethod]
    public void AProfileATagScopedDelayCanAlsoApplyToKeepsItsLegacyDelayProfilesSoTheTagStillWins()
    {
        var registry = Registry();
        var anime = registry.DefaultProfileFor(MediaAcquisitionKind.Anime);
        var global = new AnimeDelayProfile("global", "Everything", 60, null, [], true);
        var tagged = new AnimeDelayProfile("tagged", "Fast track", 30, null, ["fast"], false);

        var plan = LegacyDelayMigration.Build([anime], [global, tagged], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { anime.Id });

        Assert.AreEqual(0, plan.ChangedProfiles.Count, "Merging the global delay would take the place of the tag's own delay for the Anime that carry the tag.");
        Assert.AreEqual(0, plan.MigratedDelayIds.Count);
    }

    [TestMethod]
    public async Task TheStartupMigrationMovesTheLegacyEntryAndLeavesTagPolicyAlone()
    {
        var root = Directory.CreateTempSubdirectory("jularr-policy-migration-");
        try
        {
            var registry = Registry();
            var profiles = new QualityProfileStore(new DirectoryInfo(Path.Combine(root.FullName, "acquisition")), registry);
            var policy = new AcquisitionPolicyStore(root.FullName);
            var anime = (await profiles.LoadAsync()).Profiles.First(profile => profile.Id == registry.DefaultProfileFor(MediaAcquisitionKind.Anime).Id);
            await policy.UpdateAsync(state => state with
            {
                DelayProfiles = [new AnimeDelayProfile("d1", "Wait", 90, null, [], true), new AnimeDelayProfile("d2", "Tagged", 30, "some-other-profile", ["fast"], false)],
                IndexerRestrictions = [new AnimeIndexerRestriction("r1", "Only A", ["fast"], [Guid.NewGuid()])]
            });
            var migration = new AcquisitionPolicyMigration(policy, profiles, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<AcquisitionPolicyMigration>.Instance);

            var moved = await migration.RunAsync(CancellationToken.None);
            var movedAgain = await migration.RunAsync(CancellationToken.None);

            var state = await policy.LoadAsync();
            var changed = (await profiles.LoadAsync()).Profiles.Single(profile => profile.Id == anime.Id);
            Assert.AreEqual(1, moved);
            Assert.AreEqual(0, movedAgain);
            CollectionAssert.AreEqual(new[] { "d2" }, state.DelayProfiles.Select(delay => delay.Id).ToArray());
            Assert.AreEqual(1, state.IndexerRestrictions.Count, "No user-created restriction disappears.");
            Assert.IsTrue(changed.FallbackTiers.Any(tier => tier.AfterMinutes == 90));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow(MediaAcquisitionKind.Movie)]
    [DataRow(MediaAcquisitionKind.Tv)]
    [DataRow(MediaAcquisitionKind.Anime)]
    [DataRow(MediaAcquisitionKind.Book)]
    [DataRow(MediaAcquisitionKind.LightNovel)]
    [DataRow(MediaAcquisitionKind.Manga)]
    [DataRow(MediaAcquisitionKind.Music)]
    public async Task AWorkOverrideAndTheKindDefaultAreResolvedAtEverySearchSoAReassignmentAppliesToAWantedItem(MediaAcquisitionKind kind)
    {
        var directory = Directory.CreateTempSubdirectory("jularr-profile-resolve-");
        try
        {
            var registry = Registry();
            var store = new QualityProfileStore(directory, registry);
            var baseline = registry.DefaultProfileFor(kind);
            var waiting = baseline with { Id = "waits", Name = "Waits", FallbackTiers = [new FallbackTier(60, [baseline.QualityOrder[^1]])] };
            var restricted = baseline with { Id = "restricted", Name = "Restricted", SourcePolicy = new AcquisitionSourcePolicy([Guid.NewGuid()], []) };
            await store.UpsertAsync(waiting);
            await store.UpsertAsync(restricted);
            var work = Guid.NewGuid();

            var before = await store.ResolveAsync(kind, work);
            await store.SetKindDefaultAsync(kind, "waits");
            var byDefault = await store.ResolveAsync(kind, work);
            await store.AssignWorkAsync(work, "restricted");
            var byOverride = await store.ResolveAsync(kind, work);
            await store.AssignWorkAsync(work, null);
            var afterClear = await store.ResolveAsync(kind, work);

            Assert.AreEqual(baseline.Id, before.Id, "Without a default or an override the kind's registered profile applies.");
            Assert.AreEqual("waits", byDefault.Id, "A changed kind default applies to a title that has no override.");
            Assert.IsTrue(byOverride.SourcePolicy.IsRestricted, "The Work's own profile wins, including its source policy.");
            Assert.AreEqual("waits", afterClear.Id, "Clearing the override returns to the kind default.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ProwlarrReleaseCandidate Release(string title) =>
        new(title, "Indexer", null, "usenet", 100_000_000, null, null, Now, 1, 24, title, null, AnimeReleaseParser.Parse(title), [], new Uri("http://indexer.example/nzb/" + Uri.EscapeDataString(title)), null);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Jularr.sln was not found above the test binaries.");
    }
}
