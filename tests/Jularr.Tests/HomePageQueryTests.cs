using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

[TestClass]
public sealed class HomePageQueryTests
{
    [TestMethod]
    public async Task HomeLoadsTheAddedAnimeWithoutVocabularyRows()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await new LibraryCanonicalSeed(fixture.Db).AddAnimeAsync("Test", [(1, 1, true)]);

        var model = EpisodeFlowFixture.Home(fixture.Db, EpisodeFlowFixture.Account("owner"));
        await model.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        var recent = Assert.ContainsSingle(model.RecentTitles);
        Assert.AreEqual(anime.Work.Id, recent.Title.WorkId);
        Assert.AreEqual(WorkMediaType.Anime, recent.Title.MediaType);
        Assert.AreEqual(0, recent.TotalOccurrences);
        Assert.AreEqual(0, recent.PreparedOccurrences);
        Assert.AreEqual(0, recent.PreparationPercent);
    }
}
