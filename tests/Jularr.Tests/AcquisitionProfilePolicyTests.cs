using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
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
    public void AnAllowedQualityIsTakenAtOnceAndAnotherOneIsNotForEveryMediaKind(MediaAcquisitionKind kind, string releaseName)
    {
        var registry = Registry();
        var parser = registry.ParserFor(kind);
        var quality = ReleaseQuality.GetKey(parser.Parse(releaseName));
        var basic = registry.DefaultProfileFor(kind);
        Assert.IsTrue(basic.QualityOrder.Contains(quality, StringComparer.OrdinalIgnoreCase), $"{kind} ranks {quality}.");
        var allowed = basic with { AllowedQualities = [quality] };
        var other = basic with { AllowedQualities = [.. basic.QualityOrder.Where(item => !item.Equals(quality, StringComparison.OrdinalIgnoreCase))] };

        var taken = ProfileTest.Run(allowed, parser, releaseName, null, null);
        var refused = ProfileTest.Run(other, parser, releaseName, null, null);

        Assert.AreEqual(SelectionDecision.Eligible, taken.Decision, "An allowed quality is taken now; a better one upgrades it later.");
        Assert.IsTrue(taken.WouldGrab);
        Assert.AreEqual(SelectionDecision.Rejected, refused.Decision);
        Assert.IsFalse(refused.WouldGrab);
    }

    [TestMethod]
    public void TheProfileTestNamesTheIndexerAProfileDoesNotSearch()
    {
        var registry = Registry();
        var allowed = Guid.NewGuid();
        var profile = registry.DefaultProfileFor(MediaAcquisitionKind.Movie) with { SourcePolicy = new AcquisitionSourcePolicy([allowed], []) };

        var fromAllowed = ProfileTest.Run(profile, registry.ParserFor(MediaAcquisitionKind.Movie), "Dune.2021.1080p.WEB-DL.H264-GRP", null, allowed);
        var fromOther = ProfileTest.Run(profile, registry.ParserFor(MediaAcquisitionKind.Movie), "Dune.2021.1080p.WEB-DL.H264-GRP", null, Guid.NewGuid());

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
    public void EveryProfileSearchAppliesItsSourcePolicy()
    {
        var web = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Features");
        var searches = new (string File, int Minimum)[]
        {
            ("Acquisition/Core/AcquisitionCore.cs", 1), ("Acquisition/Pipeline/AnimeAcquisitionPipeline.cs", 2), ("Books/BookAcquisitionExecutor.cs", 1)
        };

        foreach (var (file, minimum) in searches)
        {
            var text = File.ReadAllText(Path.Combine(web, file));
            Assert.IsTrue(text.Split("WithSourcePolicy(").Length - 1 >= minimum, $"{file} applies the profile's source policy to its searches.");
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
            var waiting = baseline with { Id = "waits", Name = "Waits", UpgradeAllowed = !baseline.UpgradeAllowed };
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

    [TestMethod]
    public async Task AStoredProfileThatWaitedBeforeTakingLowerQualitiesAllowsThemAtOnce()
    {
        var directory = Directory.CreateTempSubdirectory("jularr-profile-fold-");
        try
        {
            var registry = Registry();
            var profile = registry.DefaultProfileFor(MediaAcquisitionKind.Movie);
            var lower = profile.QualityOrder[^1];
            var strict = profile with { AllowedQualities = [.. profile.QualityOrder.Take(2)] };
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new QualityProfileState(2, [strict], new Dictionary<string, string> { ["movie"] = strict.Id }, new Dictionary<string, string>()), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!;
            legacy["profiles"]![0]!["fallbackTiers"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["afterMinutes"] = 90, ["addedQualities"] = new System.Text.Json.Nodes.JsonArray(lower) });
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "quality-profiles.json"), legacy.ToJsonString());

            var store = new QualityProfileStore(directory, registry);
            var loaded = await store.LoadAsync();
            var again = await new QualityProfileStore(directory, registry).LoadAsync();

            Assert.AreEqual(QualityProfileState.CurrentVersion, loaded.Version);
            CollectionAssert.Contains(loaded.Profiles.Single().AllowedQualities, lower, "A quality that was allowed after a wait is allowed now.");
            Assert.AreEqual(QualityProfileState.CurrentVersion, again.Version, "The file was rewritten in the current shape.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static AcquisitionCandidate Release(string title) =>
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
