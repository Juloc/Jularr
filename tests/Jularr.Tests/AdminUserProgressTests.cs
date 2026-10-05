using Jularr.Web.Data;
using Jularr.Web.Features.Admin;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Vocabulary;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class AdminUserProgressTests
{
    [TestMethod]
    public async Task OverviewCombinesAnimeNovelAndLearningProgressPerAccount()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var now = DateTime.UtcNow;

            db.OwnerAccounts.AddRange(
                new OwnerAccount
                {
                    Id = OwnerAccount.SingletonId,
                    UserName = "owner",
                    NormalizedUserName = "OWNER",
                    PasswordHash = "hash",
                    Role = AccountRole.Owner,
                    IsEnabled = true,
                    CreatedAt = now.AddDays(-10)
                },
                new OwnerAccount
                {
                    Id = "reader",
                    UserName = "reader",
                    NormalizedUserName = "READER",
                    PasswordHash = "hash",
                    Role = AccountRole.User,
                    IsEnabled = true,
                    CreatedAt = now.AddDays(-5)
                });

            var anime = new Anime { Key = "admin-anime", Title = "Anime title" };
            var episode = new Episode
            {
                AnimeId = anime.Id,
                SeasonNumber = 2,
                Number = 4,
                Title = "Episode title"
            };
            db.Add(anime);
            db.Add(episode);
            var work = new NovelWork
            {
                SourceProvider = "fake",
                SourceKey = "admin-novel",
                SourceUrl = "https://example.invalid/admin-novel",
                Title = "Novel title"
            };
            var volume = new NovelVolume { WorkId = work.Id, Number = 1, SourceKey = "web" };
            var chapter = new NovelChapter
            {
                WorkId = work.Id,
                VolumeId = volume.Id,
                Number = 12,
                SourceUrl = "https://example.invalid/admin-novel/12",
                Title = "Chapter title"
            };
            db.Add(work);
            db.Add(volume);
            db.Add(chapter);
            db.NovelProgress.Add(new NovelProgress
            {
                ProfileId = "reader",
                WorkId = work.Id,
                ChapterId = chapter.Id,
                PositionPermille = 730,
                UpdatedAt = now.AddMinutes(-4)
            });

            var knownTerm = new Term { Canonical = "猫" };
            var learningTerm = new Term { Canonical = "犬" };
            db.AddRange(knownTerm, learningTerm);
            await LearningTestData.SeedTermCardAsync(
                db,
                "reader",
                knownTerm,
                UserTermState.Known,
                updatedAt: now.AddMinutes(-3));
            var learningCard = await LearningTestData.SeedTermCardAsync(
                db,
                "reader",
                learningTerm,
                UserTermState.Learning,
                updatedAt: now.AddMinutes(-3));
            db.LearningCardReviews.Add(
                LearningTestData.Review("reader", learningCard.Id, now.AddMinutes(-1)));

            await db.SaveChangesAsync();
            await CanonicalProgressSeed.SetAsync(db, "reader", episode.Id, 45_000, 90_000, false, now.AddMinutes(-2));

            var service = new AdminUserProgressService(db);
            var users = await service.GetAsync();

            Assert.AreEqual(2, users.Count);

            var reader = users.Single(x => x.Account.Id == "reader");
            Assert.IsNotNull(reader.CurrentAnime);
            Assert.AreEqual("Anime title", reader.CurrentAnime.AnimeTitle);
            Assert.AreEqual(50, reader.CurrentAnime.Percent);
            Assert.AreEqual(1, reader.EpisodesStarted);
            Assert.AreEqual(0, reader.EpisodesCompleted);

            Assert.IsNotNull(reader.CurrentNovel);
            Assert.AreEqual("Novel title", reader.CurrentNovel.WorkTitle);
            Assert.AreEqual(12, reader.CurrentNovel.ChapterNumber);
            Assert.AreEqual(73, reader.CurrentNovel.Percent);
            Assert.AreEqual(1, reader.NovelsStarted);

            Assert.AreEqual(1, reader.Learning.KnownTerms);
            Assert.AreEqual(1, reader.Learning.LearningTerms);
            Assert.AreEqual(1, reader.Learning.Reviews);
            Assert.IsNotNull(reader.LastActivityAt);

            var owner = users.Single(x => x.Account.Role == AccountRole.Owner);
            Assert.IsNull(owner.CurrentAnime);
            Assert.IsNull(owner.CurrentNovel);
            Assert.AreEqual(0, owner.Learning.Reviews);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"jularr-admin-progress-{Guid.NewGuid():N}.db");

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;

        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }
}
