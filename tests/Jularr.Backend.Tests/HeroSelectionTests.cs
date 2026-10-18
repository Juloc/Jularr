using Jularr.Backend.Data.Enums.Media;
using Jularr.Frontend.Framework;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class HeroSelectionTests
    {
        private static HeroCandidate<string> Item(
            string identity, string group = "movies",
            DiscoverFeedType? feed = DiscoverFeedType.Trending, bool isContinue = false) =>
            new(identity, group, isContinue ? null : feed, isContinue, identity);

        [TestMethod]
        public void EmptyAndPartialPools_NeverUseFakeItemsOrRepeats()
        {
            Assert.AreEqual(0, HeroSelection.Choose<HeroCandidate<string>>([]).Count);

            var one = HeroSelection.Choose([Item("work:1")], new Random(1));
            Assert.AreEqual(1, one.Count);
            Assert.AreEqual("work:1", one[0].Identity);

            var duplicates = HeroSelection.Choose([
                Item("work:1"), Item("work:1", "anime", DiscoverFeedType.New),
                Item("work:2"), Item("work:2", "movies", DiscoverFeedType.Upcoming)
            ], new Random(2));
            CollectionAssert.AreEquivalent(new[] { "work:1", "work:2" }, duplicates.Select(x => x.Identity).ToArray());
        }

        [TestMethod]
        public void ContinueIsLimitedToTwoAndCannotReappearInProviderFeed()
        {
            var items = Enumerable.Range(1, 5).Select(n => Item("work:" + n, isContinue: true))
                .Concat(Enumerable.Range(1, 20).Select(n => Item("work:" + n)));
            var result = HeroSelection.Choose(items, new Random(3));

            Assert.AreEqual(8, result.Count);
            CollectionAssert.AreEqual(new[] { "work:1", "work:2" },
                result.Where(x => x.IsContinue).Select(x => x.Identity).ToArray());
            Assert.AreEqual(result.Count, result.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count());
        }

        [TestMethod]
        public void MediaGroupsAndFeedsAreBalancedWhereCandidatesExist()
        {
            var items = new List<HeroCandidate<string>>();
            foreach (var group in new[] { "movies", "series", "anime" })
            {
                foreach (var feed in new[] { DiscoverFeedType.Trending, DiscoverFeedType.New, DiscoverFeedType.Upcoming })
                {
                    items.AddRange(Enumerable.Range(1, 5).Select(n =>
                        Item("provider:" + group + ":" + feed + ":" + n, group, feed)));
                }
            }

            var chosen = HeroSelection.Choose(items, new Random(4));
            Assert.AreEqual(8, chosen.Count);
            var perGroup = chosen.GroupBy(x => x.MediaGroup).Select(x => x.Count()).ToArray();
            Assert.IsTrue(perGroup.Max() - perGroup.Min() <= 1);
            Assert.IsTrue(chosen.Select(x => x.Feed).Distinct().Count() >= 2);
        }

        [TestMethod]
        public void ContinueSlidesCountTowardTheMediaGroupBalance()
        {
            var items = new[]
            {
                Item("continue:1", "movies", isContinue: true),
                Item("continue:2", "movies", isContinue: true)
            }.Concat(Enumerable.Range(1, 10).Select(n => Item("movie:" + n, "movies")))
                .Concat(Enumerable.Range(1, 10).Select(n => Item("book:" + n, "books")));

            var chosen = HeroSelection.Choose(items, new Random(8));
            Assert.AreEqual(8, chosen.Count);
            Assert.AreEqual(2, chosen.Count(item => item.IsContinue));
            Assert.IsTrue(chosen.Count(item => item.MediaGroup == "books") >= 3);
        }

        [TestMethod]
        public void IgnoresUnavailableSourcesAndUnidentifiedEntries()
        {
            var result = HeroSelection.Choose([
                Item("", "movies"),
                Item("provider:1", ""),
                Item("provider:2", "books", DiscoverFeedType.Top),
                Item("provider:3", "books", DiscoverFeedType.Trending),
                Item("provider:4", "games", DiscoverFeedType.New),
                Item("provider:5", "movies", null)
            ], new Random(1));

            CollectionAssert.AreEquivalent(new[] { "provider:2", "provider:3", "provider:4" },
                result.Select(x => x.Identity).ToArray());
        }

        [TestMethod]
        public void LaterVisitsCanSelectDifferentItemsButEachVisitIsUnique()
        {
            var items = Enumerable.Range(1, 100).Select(n => Item("work:" + n)).ToArray();
            var alternatives = Enumerable.Range(0, 15)
                .Select(seed => HeroSelection.Choose(items, new Random(seed)).Select(x => x.Identity).ToArray()).ToArray();

            Assert.IsTrue(alternatives.All(visit => visit.Length == 8 && visit.Distinct().Count() == 8));
            Assert.IsTrue(alternatives.Select(visit => string.Join(",", visit)).Distinct().Count() > 1);
        }
    }
}
