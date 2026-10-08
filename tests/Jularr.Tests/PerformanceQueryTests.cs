using System.Data;
using Jularr.Web.Data;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Search;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// Focused performance/query characteristics on a representative large dataset (#570): search must
/// use an indexed plan rather than a sequential scan, results stay bounded, and library-style paging
/// is keyset-based rather than loading whole tables into memory.
/// </summary>
[TestClass]
public sealed class PerformanceQueryTests
{
    [TestMethod]
    public async Task LargeLibrarySearchIsIndexedAndBounded()
    {
        await using var db = NewContext();
        await DatabaseMigrationBridge.UpgradeAsync(db);

        var works = new List<NovelWork>(3000);
        for (var i = 0; i < 3000; i++)
        {
            works.Add(new NovelWork
            {
                Id = Guid.NewGuid(),
                SourceProvider = "perf",
                SourceKey = $"k{i}",
                SourceUrl = $"https://example/{i}",
                Title = $"Series Number {i:D5} Adventure",
                ImportedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        db.NovelWorks.AddRange(works);
        await db.SaveChangesAsync();

        var search = new MediaSearchService(
            db,
            new Jularr.Web.Features.Acquisition.Monitoring.MonitoringStore(Path.GetTempPath()),
            MonitoringTestSupport.Anime(db),
            new Jularr.Web.Features.Acquisition.Access.AcquisitionAccessStore(db));
        var results = await search.SearchAsync(new MediaSearchRequest("Number 02500", Limit: 20));
        Assert.IsTrue(results.Items.Count is > 0 and <= 20, "Search must return bounded results.");
        Assert.IsTrue(results.Items.Any(h => h.Title.Contains("02500")), "Search must find the target on a large set.");
        Assert.IsTrue(results.Total <= MediaSearchService.CandidateCap, "The candidate window bounds the work a query does.");

        // The full-text branch must be served by the GIN index (it is chosen over a sequential
        // scan when scans are disabled, proving the index exists and covers the query).
        var plan = await ExplainAsync(
            db,
            "SELECT \"Id\" FROM \"NovelWorks\" WHERE \"SearchVector\" @@ websearch_to_tsquery('simple', 'Adventure') LIMIT 20;");
        StringAssert.Contains(plan, "Index", $"Full-text search must use an index. Plan:\n{plan}");
    }

    [TestMethod]
    public async Task KeysetPagingOverChaptersReturnsDistinctBoundedPages()
    {
        await using var db = NewContext();
        await DatabaseMigrationBridge.UpgradeAsync(db);

        var work = new NovelWork
        {
            Id = Guid.NewGuid(),
            SourceProvider = "perf",
            SourceKey = "keyset",
            SourceUrl = "https://example/keyset",
            Title = "Keyset Work",
            ImportedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.NovelWorks.Add(work);
        await db.SaveChangesAsync();

        var volume = new NovelVolume
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Number = 1,
            SourceKey = "v1"
        };
        db.NovelVolumes.Add(volume);
        await db.SaveChangesAsync();

        var chapters = new List<NovelChapter>(250);
        for (var i = 1; i <= 250; i++)
        {
            chapters.Add(new NovelChapter
            {
                Id = Guid.NewGuid(),
                WorkId = work.Id,
                VolumeId = volume.Id,
                Number = i,
                SourceUrl = $"https://example/keyset/{i}",
                Title = $"Chapter {i}",
                OriginalText = "text",
                SourceHash = $"h{i}",
                ImportedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        db.NovelChapters.AddRange(chapters);
        await db.SaveChangesAsync();

        const int pageSize = 100;
        var seen = new List<int>();
        var after = 0;
        while (true)
        {
            var page = await db.NovelChapters
                .AsNoTracking()
                .Where(x => x.WorkId == work.Id && x.Number > after)
                .OrderBy(x => x.Number)
                .Take(pageSize)
                .Select(x => x.Number)
                .ToListAsync();

            if (page.Count == 0)
            {
                break;
            }

            Assert.IsTrue(page.Count <= pageSize, "Each keyset page stays bounded.");
            seen.AddRange(page);
            after = page[^1];
        }

        Assert.AreEqual(250, seen.Count);
        Assert.AreEqual(250, seen.Distinct().Count(), "Keyset pages must not overlap.");
        CollectionAssert.AreEqual(Enumerable.Range(1, 250).ToList(), seen);
    }

    private static async Task<string> ExplainAsync(AppDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using (var off = connection.CreateCommand())
            {
                off.CommandText = "SET enable_seqscan = off;";
                await off.ExecuteNonQueryAsync();
            }

            var lines = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "EXPLAIN " + sql;
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    lines.Add(reader.GetString(0));
                }
            }

            await using (var on = connection.CreateCommand())
            {
                on.CommandText = "SET enable_seqscan = on;";
                await on.ExecuteNonQueryAsync();
            }

            return string.Join('\n', lines);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"jularr-perf-{Guid.NewGuid():N}.db")}")
            .Options);
}
