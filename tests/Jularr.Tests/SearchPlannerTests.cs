using System.Xml.Linq;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class SearchPlannerTests
{
    private static readonly DateTimeOffset Posted = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The query texts a Light Novel or Manga target is searched with, in the deepest search.</summary>
    public static IReadOnlyList<string> ReadingQueries(ReadingAcquisitionTarget target) =>
        [.. SearchPlanner.Plan(
            new SearchIntent(target.Kind, target.Title) { Aliases = target.Aliases, Creator = target.Author, Volume = target.RequestedVolume },
            null,
            SearchDepth.Deep).Where(query => !query.AnyCategory).Select(query => query.Text!)];

    private static IndexerCapabilities Caps(params (IndexerSearchMode Mode, string[] Parameters)[] modes) =>
        new(Posted, modes.ToDictionary(item => item.Mode, item => item.Parameters));

    [TestMethod]
    public void MovieUsesTheImdbIdFirstWhenTheIndexerAdvertisesItAndTheTitleWithYearAfterwards()
    {
        var intent = new SearchIntent(MediaAcquisitionKind.Movie, "Dune") { Year = 2021, ExternalIds = new Dictionary<string, string> { ["imdb"] = "tt1160419", ["tmdb"] = "438631" } };
        var caps = Caps((IndexerSearchMode.Search, ["q"]), (IndexerSearchMode.Movie, ["q", "imdbid", "tmdbid"]));

        var plan = SearchPlanner.Plan(intent, caps, SearchDepth.Normal);

        Assert.AreEqual(IndexerSearchMode.Movie, plan[0].Mode);
        CollectionAssert.AreEqual(new[] { new KeyValuePair<string, string>("imdbid", "1160419") }, plan[0].Parameters.ToArray());
        Assert.IsNull(plan[0].Text);
        Assert.AreEqual(0, plan[0].Tier);
        Assert.IsTrue(plan.Any(query => query.Text == "Dune 2021" && query.Tier == 0));
        Assert.IsTrue(plan.Any(query => query.Parameters.Any(pair => pair.Key == "tmdbid") && query.Tier == 1), "A second id only joins the expansion tier.");
    }

    [TestMethod]
    public void AnIdTheIndexerDidNotAdvertiseIsNeverSentAndTheRungFallsBackToText()
    {
        var intent = new SearchIntent(MediaAcquisitionKind.Movie, "Dune") { Year = 2021, ExternalIds = new Dictionary<string, string> { ["imdb"] = "tt1160419" } };

        var withoutCaps = SearchPlanner.Plan(intent, null, SearchDepth.Normal);
        var movieWithoutIds = SearchPlanner.Plan(intent, Caps((IndexerSearchMode.Search, ["q"]), (IndexerSearchMode.Movie, ["q"])), SearchDepth.Normal);

        Assert.IsTrue(withoutCaps.All(query => query.Parameters.Count == 0 && query.Mode == IndexerSearchMode.Search));
        Assert.IsTrue(movieWithoutIds.All(query => query.Parameters.Count == 0));
        Assert.AreEqual("Dune 2021", withoutCaps[0].Text);
    }

    [TestMethod]
    public void TvEpisodeUsesTheTvIdWithSeasonAndEpisodeThenTheTitleWithTheSameNumbering()
    {
        var intent = new SearchIntent(MediaAcquisitionKind.Tv, "Severance") { Season = 2, Episode = 3, ExternalIds = new Dictionary<string, string> { ["tvdb"] = "371980" } };
        var caps = Caps((IndexerSearchMode.Search, ["q"]), (IndexerSearchMode.TvSearch, ["q", "tvdbid", "season", "ep"]));

        var plan = SearchPlanner.Plan(intent, caps, SearchDepth.Normal);

        Assert.AreEqual(IndexerSearchMode.TvSearch, plan[0].Mode);
        CollectionAssert.AreEqual(
            new[] { new KeyValuePair<string, string>("tvdbid", "371980"), new("season", "2"), new("ep", "3") },
            plan[0].Parameters.ToArray());
        Assert.AreEqual("TVDB + S02E03", plan[0].Provenance);
        Assert.IsTrue(plan.Any(query => query.Mode == IndexerSearchMode.TvSearch && query.Text == "Severance" && query.Provenance == "Title + S02E03"));
        Assert.IsTrue(plan.Any(query => query.Mode == IndexerSearchMode.Search && query.Text == "Severance S02E03"));
    }

    [TestMethod]
    public void AnimeKeepsTextNumberingAndTheAbsoluteFormAndNeverSendsStructuredSeasonFields()
    {
        var intent = new SearchIntent(MediaAcquisitionKind.Anime, "Sousou no Frieren")
        {
            Aliases = ["Frieren: Beyond Journey's End"],
            Season = 2,
            Episode = 3,
            AbsoluteEpisode = 31
        };

        var texts = SearchPlanner.Plan(intent, Caps((IndexerSearchMode.Search, ["q"]), (IndexerSearchMode.TvSearch, ["q", "season", "ep"])), SearchDepth.Deep)
            .Where(query => !query.AnyCategory).Select(query => query.Text).ToArray();

        CollectionAssert.IsSubsetOf(new[] { "Sousou no Frieren S02E03", "Sousou no Frieren - 31", "Frieren: Beyond Journey's End S02E03" }, texts);
        Assert.AreEqual(texts.Length, texts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(SearchPlanner.Plan(intent, Caps((IndexerSearchMode.Search, ["q"]), (IndexerSearchMode.TvSearch, ["q", "season", "ep"])), SearchDepth.Deep).All(query => query.Mode == IndexerSearchMode.Search));
    }

    [TestMethod]
    public void SeasonPackQueriesAskForTheSeasonOnly()
    {
        var plan = SearchPlanner.Plan(new SearchIntent(MediaAcquisitionKind.Anime, "Anime") { Season = 2 }, null, SearchDepth.Normal);

        CollectionAssert.AreEqual(new[] { "Anime S02", "Anime Season 2" }, plan.Where(query => !query.AnyCategory).Select(query => query.Text).ToArray());
    }

    [TestMethod]
    public void BookSearchUsesTheBookFunctionOnlyWhenTheIndexerAdvertisesTitleAndAuthor()
    {
        var intent = new SearchIntent(MediaAcquisitionKind.Book, "Frankenstein: The 1818 Text") { Creator = "Mary Shelley" };

        var structured = SearchPlanner.Plan(intent, Caps((IndexerSearchMode.Search, ["q"]), (IndexerSearchMode.Book, ["q", "title", "author"])), SearchDepth.Normal);
        var textOnly = SearchPlanner.Plan(intent, Caps((IndexerSearchMode.Search, ["q"])), SearchDepth.Normal);

        Assert.AreEqual(IndexerSearchMode.Book, structured[0].Mode);
        CollectionAssert.AreEqual(new[] { new KeyValuePair<string, string>("title", "Frankenstein"), new("author", "Mary Shelley") }, structured[0].Parameters.ToArray());
        Assert.IsTrue(textOnly.All(query => query.Mode == IndexerSearchMode.Search));
    }

    [TestMethod]
    public void ReadingTargetsUseCreatorAliasesAndTheRequestedVolume()
    {
        var queries = ReadingQueries(new ReadingAcquisitionTarget(MediaAcquisitionKind.LightNovel, "Mushoku Tensei", ["Jobless Reincarnation"], "Rifujin na Magonote", RequestedVolume: 12));

        CollectionAssert.IsSubsetOf(new[] { "Rifujin na Magonote Mushoku Tensei", "Mushoku Tensei vol 12", "Jobless Reincarnation volume 12" }, queries.ToArray());
    }

    [TestMethod]
    public void PlansAreDeterministicAndTheDepthBoundsTheVariants()
    {
        var intent = new SearchIntent(MediaAcquisitionKind.Anime, "A")
        {
            Aliases = ["B", "C", "D", "E", "F", "G"],
            Season = 1,
            Episode = 1,
            AbsoluteEpisode = 1
        };

        var fast = SearchPlanner.Plan(intent, null, SearchDepth.Fast);
        var normal = SearchPlanner.Plan(intent, null, SearchDepth.Normal);
        var deep = SearchPlanner.Plan(intent, null, SearchDepth.Deep);

        CollectionAssert.AreEqual(normal.Select(query => query.Key).ToArray(), SearchPlanner.Plan(intent, null, SearchDepth.Normal).Select(query => query.Key).ToArray());
        Assert.IsTrue(fast.Count <= SearchBudget.For(SearchDepth.Fast).MaxQueryVariants);
        Assert.IsTrue(fast.All(query => query.Tier == 0));
        Assert.IsTrue(normal.All(query => query.Tier <= 1) && normal.Count <= SearchBudget.For(SearchDepth.Normal).MaxQueryVariants);
        Assert.IsTrue(deep.Count > normal.Count);
    }

    [TestMethod]
    public void CapsParserReadsFunctionsParametersAndLimits()
    {
        var caps = NewznabCapsParser.Parse(XDocument.Parse("""
            <caps><limits max="250" default="100"/>
            <searching>
              <search available="yes" supportedParams="q"/>
              <tv-search available="yes" supportedParams="q,rid,tvdbid,season,ep"/>
              <movie-search available="no" supportedParams="q,imdbid"/>
              <book-search available="yes" supportedParams="q,author,title"/>
            </searching></caps>
            """), Posted);

        Assert.IsTrue(caps.Supports(IndexerSearchMode.TvSearch, "tvdbid"));
        Assert.IsFalse(caps.Supports(IndexerSearchMode.Movie));
        Assert.IsTrue(caps.Supports(IndexerSearchMode.Book, "author"));
        Assert.AreEqual(250, caps.MaximumLimit);
        Assert.AreEqual("TV ID ✓ · Season/Episode ✓ · Book ✓", caps.Describe());
    }

    [TestMethod]
    public void EquivalentReleasesOfTwoIndexersBecomeOneCandidateWithBothSources()
    {
        var first = Hit("Indexer A", 1, "Show.S01E01.1080p.WEB.H264-GRP", 1_000_000_000, "a-1", Posted);
        var second = Hit("Indexer B", 2, "Show.S01E01.1080p.WEB.H264-GRP", 1_004_000_000, "b-9", Posted.AddHours(3));

        var merged = ReleaseDeduplicator.Merge([second, first]);

        Assert.AreEqual(1, merged.Count);
        CollectionAssert.AreEqual(new[] { "Indexer A", "Indexer B" }, merged[0].Sources.Select(source => source.Indexer).ToArray(), "The primary source follows the indexer priority, not the arrival order.");
        Assert.AreEqual(2, merged[0].Provenance.Count);
    }

    [TestMethod]
    public void ReleasesWithTheSameTitleButAnotherSizeOrGroupAreNotMerged()
    {
        var baseline = Hit("Indexer A", 1, "Show.S01E01.1080p.WEB.H264-GRP", 1_000_000_000, "a-1", Posted);
        var otherSize = Hit("Indexer B", 2, "Show.S01E01.1080p.WEB.H264-GRP", 2_000_000_000, "b-1", Posted);
        var unknownSize = Hit("Indexer C", 3, "Show.S01E01.1080p.WEB.H264-GRP", null, "c-1", Posted);
        var otherGroup = Hit("Indexer D", 4, "Show.S01E01.1080p.WEB.H264-OTHER", 1_000_000_000, "d-1", Posted);

        Assert.AreEqual(4, ReleaseDeduplicator.Merge([baseline, otherSize, unknownSize, otherGroup]).Count);
    }

    [TestMethod]
    public async Task OneFailingIndexerNeverHidesTheResultsOfTheOthers()
    {
        using var host = new SearchHost();
        var good = await host.AddAsync("Good", 1);
        var broken = await host.AddAsync("Broken", 2);
        host.Script = (entry, _) => entry.Id == broken.Id
            ? throw new IndexerException("down")
            : [Release("Good", "Show.S01E01.1080p.WEB.H264-GRP", 1_000_000_000, "g-1")];

        var result = await host.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Tv, "Show") { Season = 1, Episode = 1 }, new SearchOptions(), CancellationToken.None);

        Assert.AreEqual(1, result.Releases.Count);
        Assert.AreEqual(IndexerSearchState.Searched, result.Outcomes.Single(outcome => outcome.EntryId == good.Id).State);
        Assert.AreEqual(IndexerSearchState.Unavailable, result.Outcomes.Single(outcome => outcome.EntryId == broken.Id).State);
        Assert.IsFalse(result.EveryIndexerFailed);
        Assert.AreEqual(1, result.Warnings.Count);
    }

    [TestMethod]
    public async Task ARateLimitedIndexerIsLeftAloneUntilItsRetryAfter()
    {
        using var host = new SearchHost();
        var entry = await host.AddAsync("Limited", 1);
        var calls = 0;
        host.Script = (_, _) =>
        {
            calls++;
            throw new IndexerRateLimitedException("limit", TimeSpan.FromMinutes(10));
        };

        var first = await host.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Movie, "Dune") { Year = 2021 }, new SearchOptions(), CancellationToken.None);
        var callsAfterFirst = calls;
        var second = await host.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Movie, "Dune") { Year = 2021 }, new SearchOptions(), CancellationToken.None);

        Assert.AreEqual(IndexerSearchState.RateLimited, first.Outcomes.Single().State);
        Assert.IsNotNull(first.Outcomes.Single().RetryAfter);
        Assert.AreEqual(IndexerSearchState.RateLimited, second.Outcomes.Single().State);
        Assert.AreEqual(callsAfterFirst, calls, "A backed-off indexer is not asked again.");
        Assert.IsTrue(first.EveryIndexerFailed);
        Assert.IsNotNull(entry);
    }

    [TestMethod]
    public async Task AutomaticSearchSkipsAManualOnlyIndexerAndManualSearchSkipsAnAutomaticOnlyOne()
    {
        using var host = new SearchHost();
        await host.AddAsync("Manual only", 1, automatic: false);
        await host.AddAsync("Automatic only", 2, interactive: false);
        var searched = new List<string>();
        host.Script = (entry, _) =>
        {
            lock (searched)
            {
                searched.Add(entry.Name);
            }

            return [];
        };

        await host.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Movie, "Dune"), new SearchOptions { Purpose = SearchPurpose.Automatic }, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Automatic only" }, searched.Distinct().ToArray());
        searched.Clear();
        await host.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Movie, "Dune"), new SearchOptions { Purpose = SearchPurpose.Interactive, Refresh = true }, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Manual only" }, searched.Distinct().ToArray());
    }

    [TestMethod]
    public async Task PagingContinuesWhileFullPagesBringNewReleasesAndStopsOnDuplicates()
    {
        using var host = new SearchHost();
        await host.AddAsync("Paged", 1, limit: 2);
        var offsets = new List<int>();
        host.Script = (_, query) =>
        {
            offsets.Add(query.Offset);
            return query.Offset switch
            {
                0 => [Release("Paged", "Show.S01E01.1080p.WEB.H264-AAA", 1_000_000_000, "p-1"), Release("Paged", "Show.S01E01.1080p.WEB.H264-BBB", 1_100_000_000, "p-2")],
                _ => [Release("Paged", "Show.S01E01.1080p.WEB.H264-AAA", 1_000_000_000, "p-1"), Release("Paged", "Show.S01E01.1080p.WEB.H264-BBB", 1_100_000_000, "p-2")]
            };
        };

        var result = await host.Coordinator.SearchAsync(
            new SearchIntent(MediaAcquisitionKind.Tv, "Show") { Season = 1, Episode = 1 },
            new SearchOptions { Depth = SearchDepth.Deep, UsableCount = _ => 0 },
            CancellationToken.None);

        Assert.AreEqual(2, result.Releases.Count);
        Assert.IsTrue(offsets.Contains(2), "A full page that brought something new is followed by the next page.");
        Assert.IsFalse(offsets.Contains(4), "A page of duplicates only ends the paging.");
    }

    [TestMethod]
    public async Task ManualSearchReadsCachedEvidenceAndAnAutomaticSearchNeverDoes()
    {
        using var host = new SearchHost();
        await host.AddAsync("Cached", 1);
        var calls = 0;
        host.Script = (_, _) =>
        {
            calls++;
            return [Release("Cached", "Dune.2021.1080p.WEB.H264-GRP", 4_000_000_000, "c-1")];
        };
        var intent = new SearchIntent(MediaAcquisitionKind.Movie, "Dune") { Year = 2021 };
        var options = new SearchOptions { Depth = SearchDepth.Fast, Purpose = SearchPurpose.Interactive };

        await host.Coordinator.SearchAsync(intent, options, CancellationToken.None);
        var afterFirst = calls;
        var again = await host.Coordinator.SearchAsync(intent, options, CancellationToken.None);
        var cached = calls;
        await host.Coordinator.SearchAsync(intent, options with { Refresh = true }, CancellationToken.None);

        Assert.AreEqual(afterFirst, cached, "The same search reads the short-lived evidence.");
        Assert.IsTrue(again.Trace.All(line => line.FromCache));
        Assert.IsTrue(calls > cached, "An explicit refresh asks the indexer again.");

        var beforeAutomatic = calls;
        await host.Coordinator.SearchAsync(intent, new SearchOptions { Depth = SearchDepth.Fast }, CancellationToken.None);
        Assert.IsTrue(calls > beforeAutomatic, "A Wanted search never reads remembered answers.");
    }

    [TestMethod]
    public async Task ATimedOutIndexerReportsItselfAndKeepsTheOthers()
    {
        using var host = new SearchHost();
        var slow = await host.AddAsync("Slow", 1);
        await host.AddAsync("Fast", 2);
        host.AsyncScript = async (entry, query, token) =>
        {
            if (entry.Id == slow.Id)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return [Release("Fast", "Dune.2021.1080p.WEB.H264-GRP", 4_000_000_000, "f-1")];
        };

        var result = await host.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Movie, "Dune") { Year = 2021 }, new SearchOptions { Depth = SearchDepth.Fast, IndexerTimeout = TimeSpan.FromMilliseconds(200) }, CancellationToken.None);

        Assert.AreEqual(IndexerSearchState.TimedOut, result.Outcomes.Single(outcome => outcome.IndexerName == "Slow").State);
        Assert.AreEqual(1, result.Releases.Count);
    }

    private static SearchHit Hit(string indexer, int priority, string title, long? size, string guid, DateTimeOffset posted) =>
        new(Release(indexer, title, size, guid, posted), Guid.NewGuid(), priority, indexer, new PlannedQuery("title", 0, IndexerSearchMode.Search, "Show S01E01", [], "Title + S01E01"));

    private static ProwlarrReleaseCandidate Release(string indexer, string title, long? size, string guid, DateTimeOffset? posted = null) =>
        new(title, indexer, null, "usenet", size, null, null, posted ?? Posted, 1, 24, guid, null, AnimeReleaseParser.Parse(title), [], new Uri($"http://{indexer.Replace(' ', '-')}.example/nzb/{guid}"), null);

    private sealed class SearchHost : IDisposable
    {
        private readonly DirectoryInfo directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        private readonly IndexerStore store;
        private readonly ScriptedIndexer indexer;
        private IndexerSearchCoordinator? coordinator;

        public SearchHost()
        {
            store = new IndexerStore(new EphemeralDataProtectionProvider(), directory);
            indexer = new ScriptedIndexer(this);
        }

        public Func<IndexerEntry, IndexerSearchQuery, IReadOnlyList<ProwlarrReleaseCandidate>>? Script { get; set; }

        public Func<IndexerEntry, IndexerSearchQuery, CancellationToken, Task<IReadOnlyList<ProwlarrReleaseCandidate>>>? AsyncScript { get; set; }

        public IndexerSearchCoordinator Coordinator => coordinator ??= new IndexerSearchCoordinator(
            new Dictionary<IndexerType, IIndexer> { [IndexerType.Newznab] = indexer },
            store,
            new AcquisitionHealthStore(directory),
            NullLogger<IndexerSearchCoordinator>.Instance,
            new SearchEvidenceCache());

        public async Task<IndexerEntry> AddAsync(string name, int priority, bool automatic = true, bool interactive = true, int limit = 100)
        {
            var entry = new IndexerEntry(
                Guid.NewGuid(),
                name,
                IndexerType.Newznab,
                true,
                priority,
                new IndexerSettings("http://indexer.example", [5070], [], limit) { AutomaticSearch = automatic, InteractiveSearch = interactive },
                "key");
            await store.SaveAsync(entry);
            return entry;
        }

        public void Dispose() => directory.Delete(recursive: true);

        private sealed class ScriptedIndexer(SearchHost host) : IIndexer
        {
            public IndexerType Type => IndexerType.Newznab;

            public Task<IndexerConnectionTestResult> TestAsync(IndexerEntry entry, CancellationToken cancellationToken) =>
                Task.FromResult(new IndexerConnectionTestResult(true));

            public Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(IndexerEntry entry, IndexerSearchQuery query, CancellationToken cancellationToken)
            {
                if (host.AsyncScript is { } asyncScript)
                {
                    return asyncScript(entry, query, cancellationToken);
                }

                return Task.FromResult(host.Script!(entry, query));
            }
        }
    }
}
