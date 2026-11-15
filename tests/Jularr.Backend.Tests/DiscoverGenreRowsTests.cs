using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Logic.Media;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DiscoverGenreRowsTests
    {
        [TestMethod]
        public void Every_mode_has_six_to_eight_genre_rows_and_each_scope_offers_its_genre()
        {
            foreach (var mode in new[] { "watch", "animanga", "read", "hear", "play" })
            {
                var rows = DiscoverGenreRows.For(mode);
                Assert.IsTrue(rows.Count is >= 6 and <= 8, mode);
                Assert.AreEqual(rows.Count, rows.Select(row => row.Genre).Distinct().Count(), mode);
                foreach (var (genre, slugs) in rows)
                {
                    Assert.IsTrue(slugs.Length > 0, mode);
                    foreach (var slug in slugs)
                    {
                        var scope = DiscoverScopes.All.Single(candidate => candidate.Slug == slug);
                        var options = DiscoverFilterScopes.Options(scope, new([]), new([]));
                        Assert.IsNotNull(options, slug);
                        Assert.IsTrue(options.Filters.Contains(DiscoverFilter.Genre), slug);
                        Assert.IsTrue(options.GenreIds.Contains(genre), mode + " " + slug + " " + genre);
                    }
                }
            }
        }

        [TestMethod]
        public void An_unknown_mode_has_no_genre_rows() => Assert.AreEqual(0, DiscoverGenreRows.For("nothing").Count);
    }
}
