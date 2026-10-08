using System.Xml.Linq;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The categories of a direct Newznab indexer, per media type (#396): the owner can choose them for every type, the caps document tells which ones the indexer
/// really offers, and an indexer that no longer offers the categories of a type is not asked for it, so one type never lands in another one's section.
/// </summary>
[TestClass]
public sealed class IndexerCategoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static IndexerEntry Entry(string name, IndexerCapabilities? capabilities = null, Dictionary<string, int[]>? byKind = null) =>
        new(Guid.NewGuid(), name, IndexerType.Newznab, Enabled: true, 1, new IndexerSettings("https://indexer.example", [5070], [], 100) { Capabilities = capabilities, CategoriesByKind = byKind }, "key");

    [TestMethod]
    public void TheCapsDocumentNamesTheCategoriesAnIndexerOffersAndAParentCoversItsSubCategories()
    {
        var caps = NewznabCapsParser.Parse(XDocument.Parse("""
            <caps><searching><search available="yes" supportedParams="q"/></searching>
            <categories>
              <category id="2000" name="Movies"><subcat id="2040" name="Movies/HD"/><subcat id="2045" name="Movies/UHD"/></category>
              <category id="5000" name="TV"><subcat id="5070" name="TV/Anime"/></category>
              <category id="100123" name="Custom audiobooks"/>
            </categories></caps>
            """), Now);

        CollectionAssert.AreEqual(new[] { 2000, 2040, 2045, 5000, 5070, 100123 }, caps.Categories);
        Assert.IsTrue(caps.Offers(2040), "A listed sub category.");
        Assert.IsTrue(caps.Offers(2010), "A sub category of a listed parent is covered by it.");
        Assert.IsTrue(caps.Offers(100123), "A custom category of the indexer.");
        Assert.IsFalse(caps.Offers(3000), "Music is not offered.");
        Assert.IsFalse(caps.Offers(7020), "Books are not offered.");
        Assert.IsTrue(NewznabCapsParser.Parse(XDocument.Parse("<caps><searching/></caps>"), Now).Offers(3000), "An indexer whose categories were never read keeps searching.");
    }

    [TestMethod]
    public void EveryMediaTypeUsesItsOwnCategoriesAndTheOwnersChoiceWins()
    {
        var plain = Entry("Plain");
        var chosen = Entry("Chosen", byKind: new() { ["movie"] = [2040, 2045], ["music"] = [3040], ["tv"] = [5030, 5040], ["audiobook"] = [3030] });

        CollectionAssert.AreEqual(new[] { 2000 }, SearchPlanner.Categories(MediaAcquisitionKind.Movie, plain).ToArray());
        CollectionAssert.AreEqual(new[] { 5000 }, SearchPlanner.Categories(MediaAcquisitionKind.Tv, plain).ToArray());
        CollectionAssert.AreEqual(new[] { 2040, 2045 }, SearchPlanner.Categories(MediaAcquisitionKind.Movie, chosen).ToArray());
        CollectionAssert.AreEqual(new[] { 5030, 5040 }, SearchPlanner.Categories(MediaAcquisitionKind.Tv, chosen).ToArray());
        CollectionAssert.AreEqual(new[] { 3040 }, SearchPlanner.Categories(MediaAcquisitionKind.Music, chosen).ToArray());
        CollectionAssert.AreEqual(new[] { 5070 }, SearchPlanner.Categories(MediaAcquisitionKind.Anime, chosen).ToArray(), "A type without a choice keeps its default.");
    }

    [TestMethod]
    public async Task TheChoiceIsStoredPerMediaTypeAndOnlyMediaTypesThatExistSurvive()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            var store = new IndexerStore(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), directory);
            var entry = Entry("Chosen", byKind: new() { ["Movie"] = [2045, 2040, 2040, -1], ["spaceship"] = [9999], ["tv"] = [] });
            await store.SaveAsync(entry);

            var reloaded = (await new IndexerStore(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), directory).GetAsync(entry.Id))!;

            Assert.AreEqual(1, reloaded.Settings.CategoriesByKind!.Count);
            CollectionAssert.AreEqual(new[] { 2040, 2045 }, reloaded.Settings.CategoriesByKind["movie"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task AnIndexerThatNoLongerOffersTheCategoriesOfATypeIsSkippedAndOneThatDoesIsAskedOnlyInTheOfferedOnes()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            var store = new IndexerStore(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), directory);
            var tvOnly = Entry("TV only", new IndexerCapabilities(Now, new Dictionary<IndexerSearchMode, string[]> { [IndexerSearchMode.Search] = ["q"] }, null, [5000, 5040]));
            var both = Entry("Both", new IndexerCapabilities(Now, new Dictionary<IndexerSearchMode, string[]> { [IndexerSearchMode.Search] = ["q"] }, null, [2000, 2040, 5000]), new() { ["movie"] = [2040, 3040] });
            await store.SaveAsync(tvOnly);
            await store.SaveAsync(both);
            var asked = new List<(string Name, int[] Categories)>();
            var indexer = new FakeIndexer((entry, _) =>
            {
                asked.Add((entry.Name, entry.Settings.Categories));
                return [];
            });
            var coordinator = new IndexerSearchCoordinator(new Dictionary<IndexerType, IIndexer> { [IndexerType.Newznab] = indexer }, store, new AcquisitionHealthStore(directory), NullLogger<IndexerSearchCoordinator>.Instance);

            var result = await coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Movie, "Dune"), new SearchOptions(), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "Both" }, asked.Select(call => call.Name).Distinct().ToArray(), "The TV-only indexer is not asked about movies.");
            CollectionAssert.AreEqual(new[] { 2040 }, asked[0].Categories, "Only the chosen categories the indexer offers are sent.");
            Assert.IsTrue(result.Outcomes.Any(outcome => outcome.IndexerName == "TV only" && outcome.State == IndexerSearchState.Skipped && outcome.Message!.Contains("offers none", StringComparison.Ordinal)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FakeIndexer(Func<IndexerEntry, IndexerSearchQuery, IReadOnlyList<ProwlarrReleaseCandidate>> search) : IIndexer
    {
        public IndexerType Type => IndexerType.Newznab;

        public Task<IndexerConnectionTestResult> TestAsync(IndexerEntry entry, CancellationToken cancellationToken) => Task.FromResult(new IndexerConnectionTestResult(true));

        public Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(IndexerEntry entry, IndexerSearchQuery query, CancellationToken cancellationToken) => Task.FromResult(search(entry, query));
    }
}
