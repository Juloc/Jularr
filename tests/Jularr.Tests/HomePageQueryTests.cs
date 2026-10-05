using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class HomePageQueryTests
{
    [TestMethod]
    public async Task HomeLoadsEpisodeWithoutVocabularyRows()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"jularr-home-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Foreign Keys=True")
                .Options;

            await using var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var anime = new Anime
            {
                Key = "test",
                Title = "Test"
            };
            var episode = new Episode
            {
                AnimeId = anime.Id,
                SeasonNumber = 1,
                Number = 1,
                Title = "Episode 1",
                DiscoveredAt = DateTime.UtcNow
            };

            db.AddRange(anime, episode);
            await db.SaveChangesAsync();

            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "owner")],
                        "test"))
            };
            var currentAccount = new CurrentAccountContext(
                new HttpContextAccessor { HttpContext = httpContext });
            var model = EpisodeFlowFixture.Home(db, currentAccount);

            await model.OnGetAsync(CancellationToken.None);

            Assert.AreEqual(1, model.RecentEpisodes.Count);
            Assert.AreEqual(episode.Id, model.RecentEpisodes[0].Id);
            Assert.AreEqual(0, model.RecentEpisodes[0].TotalOccurrences);
            Assert.AreEqual(0, model.RecentEpisodes[0].PreparedOccurrences);
            Assert.AreEqual(0, model.RecentEpisodes[0].PreparationPercent);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}
