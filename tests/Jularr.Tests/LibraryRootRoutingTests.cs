using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class LibraryRootRoutingTests
{
    [TestMethod]
    public async Task ResolveDefaultReturnsConfiguredEnabledRootAndPlacementPolicy()
    {
        await using var db = await CreateDbAsync();
        var first = new LibraryRoot { Name = "Games A", Path = "/media/games-a" };
        var second = new LibraryRoot { Name = "Games B", Path = "/media/games-b", PlacementPolicy = LibraryPlacementPolicy.Copy };
        db.LibraryRoots.AddRange(first, second);
        await db.SaveChangesAsync();

        var routing = new LibraryRootRoutingService(db);
        await routing.SetSupportedAsync(first.Id, LibraryContentType.Game, true);
        await routing.SetSupportedAsync(second.Id, LibraryContentType.Game, true);
        await routing.SetDefaultAsync(LibraryContentType.Game, second.Id);

        var route = await routing.ResolveDefaultAsync(LibraryContentType.Game);
        Assert.IsNotNull(route);
        Assert.AreEqual(second.Id, route.LibraryRootId);
        Assert.AreEqual(LibraryPlacementPolicy.Copy, route.PlacementPolicy);

        var roots = await routing.ListAsync(LibraryContentType.Game);
        Assert.AreEqual(2, roots.Count);
        Assert.AreEqual(1, roots.Count(root => root.IsDefault));
    }

    [TestMethod]
    public async Task ChangingDefaultLeavesExactlyOneDefault()
    {
        await using var db = await CreateDbAsync();
        var first = new LibraryRoot { Name = "One", Path = "/media/one" };
        var second = new LibraryRoot { Name = "Two", Path = "/media/two" };
        db.LibraryRoots.AddRange(first, second);
        await db.SaveChangesAsync();

        var routing = new LibraryRootRoutingService(db);
        await routing.SetSupportedAsync(first.Id, LibraryContentType.Game, true);
        await routing.SetSupportedAsync(second.Id, LibraryContentType.Game, true);
        await routing.SetDefaultAsync(LibraryContentType.Game, first.Id);
        await routing.SetDefaultAsync(LibraryContentType.Game, second.Id);

        var assignments = await db.LibraryRootContentAssignments.AsNoTracking().Where(row => row.ContentType == LibraryContentType.Game).ToListAsync();
        Assert.AreEqual(1, assignments.Count(row => row.IsDefault));
        Assert.IsTrue(assignments.Single(row => row.LibraryRootId == second.Id).IsDefault);
    }

    [TestMethod]
    public async Task DatabaseRejectsTwoDefaultsForOneContentType()
    {
        await using var db = await CreateDbAsync();
        var first = new LibraryRoot { Name = "One", Path = "/media/one" };
        var second = new LibraryRoot { Name = "Two", Path = "/media/two" };
        db.LibraryRoots.AddRange(first, second);
        db.LibraryRootContentAssignments.AddRange(
            new LibraryRootContentAssignment { LibraryRootId = first.Id, ContentType = LibraryContentType.Game, IsDefault = true },
            new LibraryRootContentAssignment { LibraryRootId = second.Id, ContentType = LibraryContentType.Game, IsDefault = true });

        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task DisabledDefaultIsNotResolvedAndCannotBeSelected()
    {
        await using var db = await CreateDbAsync();
        var disabled = new LibraryRoot { Name = "Disabled", Path = "/media/disabled", IsEnabled = false };
        db.LibraryRoots.Add(disabled);
        db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = disabled.Id, ContentType = LibraryContentType.Game, IsDefault = true });
        await db.SaveChangesAsync();

        var routing = new LibraryRootRoutingService(db);
        Assert.IsNull(await routing.ResolveDefaultAsync(LibraryContentType.Game));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => routing.SetDefaultAsync(LibraryContentType.Game, disabled.Id));
    }

    [TestMethod]
    public async Task DefaultRequiresContentSupportAndNeverFallsBackAcrossTypes()
    {
        await using var db = await CreateDbAsync();
        var root = new LibraryRoot { Name = "Games", Path = "/media/games" };
        db.LibraryRoots.Add(root);
        await db.SaveChangesAsync();

        var routing = new LibraryRootRoutingService(db);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => routing.SetDefaultAsync(LibraryContentType.Game, root.Id));
        await routing.SetSupportedAsync(root.Id, LibraryContentType.Game, true);
        await routing.SetDefaultAsync(LibraryContentType.Game, root.Id);

        Assert.IsNotNull(await routing.ResolveDefaultAsync(LibraryContentType.Game));
        Assert.IsNull(await routing.ResolveDefaultAsync(LibraryContentType.Movie));
    }

    [TestMethod]
    public async Task RemovingSupportRemovesTheRoutingAssignmentWithoutDeletingTheRoot()
    {
        await using var db = await CreateDbAsync();
        var root = new LibraryRoot { Name = "Games", Path = "/media/games" };
        db.LibraryRoots.Add(root);
        await db.SaveChangesAsync();

        var routing = new LibraryRootRoutingService(db);
        await routing.SetSupportedAsync(root.Id, LibraryContentType.Game, true);
        await routing.SetDefaultAsync(LibraryContentType.Game, root.Id);
        await routing.SetSupportedAsync(root.Id, LibraryContentType.Game, false);

        Assert.IsNull(await routing.ResolveDefaultAsync(LibraryContentType.Game));
        Assert.IsTrue(await db.LibraryRoots.AsNoTracking().AnyAsync(candidate => candidate.Id == root.Id));
    }

    [TestMethod]
    public async Task AssignmentForeignKeyDoesNotCascadeWhenRootDeletionIsAttempted()
    {
        await using var db = await CreateDbAsync();
        var root = new LibraryRoot { Name = "Games", Path = "/media/games" };
        db.LibraryRoots.Add(root);
        db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = root.Id, ContentType = LibraryContentType.Game });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Entry(new LibraryRoot { Id = root.Id, Name = root.Name, Path = root.Path }).State = EntityState.Deleted;
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task PlacementPolicyPersistsOnLibraryRoot()
    {
        await using var db = await CreateDbAsync();
        var root = new LibraryRoot { Name = "Games", Path = "/media/games", PlacementPolicy = LibraryPlacementPolicy.Hardlink };
        db.LibraryRoots.Add(root);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.LibraryRoots.AsNoTracking().SingleAsync(candidate => candidate.Id == root.Id);
        Assert.AreEqual(LibraryPlacementPolicy.Hardlink, stored.PlacementPolicy);
    }

    [TestMethod]
    public async Task OnlyStorageRootsGiveTheReadingAndAudiobookTypesADestinationAndTheirInboxAndMappingsAreKept()
    {
        await using var db = await CreateDbAsync();
        var manga = new LibraryRoot { Name = "Manga", Path = "/media/manga", PlacementPolicy = LibraryPlacementPolicy.Copy };
        var disabled = new LibraryRoot { Name = "Books", Path = "/media/books" };
        db.LibraryRoots.AddRange(manga, disabled);
        await db.SaveChangesAsync();
        var routing = new LibraryRootRoutingService(db);
        await routing.AssignDefaultAsync(LibraryContentType.Manga, manga.Id, LibraryPlacementPolicy.Copy);
        await routing.SetSupportedAsync(disabled.Id, LibraryContentType.Book, true);
        await routing.SetDefaultAsync(LibraryContentType.Book, disabled.Id);
        db.LibraryRoots.Single(root => root.Id == disabled.Id).IsEnabled = false;
        await db.SaveChangesAsync();
        var legacy = Jularr.Web.Features.Acquisition.Import.AnimeImportSettingsState.Empty() with
        {
            MediaLibraries = new()
            {
                [Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind.Manga] = new Jularr.Web.Features.Acquisition.Import.MediaLibraryTarget(null, null, "/inbox/manga"),
                [Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind.Book] = new Jularr.Web.Features.Acquisition.Import.MediaLibraryTarget(null, null, "/inbox/books")
            }
        };

        var effective = await routing.WithRoutedLibrariesAsync(legacy);

        var mangaTarget = effective.LibraryFor(Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind.Manga)!;
        Assert.AreEqual("/media/manga", mangaTarget.LibraryRoot);
        Assert.AreEqual(Jularr.Web.Features.Acquisition.Import.ImportMode.Copy, mangaTarget.ImportMode);
        Assert.AreEqual("/inbox/manga", mangaTarget.InboxRoot, "The inbox is not a destination and stays as configured.");
        Assert.IsNull(effective.LibraryFor(Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind.Book), "A type without an enabled default root has no destination.");
        Assert.AreEqual("/inbox/books", effective.InboxFor(Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind.Book));
    }

    [TestMethod]
    public void EveryManagedContentTypeHasItsAcquisitionKind()
    {
        foreach (var type in LibraryRootRoutingService.ManagedTypes)
        {
            Assert.IsNotNull(LibraryRootRoutingService.KindOf(type), type.ToString());
        }

        CollectionAssert.AreEquivalent(
            new[] { LibraryContentType.Anime, LibraryContentType.Movie, LibraryContentType.Tv, LibraryContentType.Music, LibraryContentType.Manga, LibraryContentType.LightNovel, LibraryContentType.Book, LibraryContentType.Audiobook },
            LibraryRootRoutingService.ManagedTypes.ToArray());
    }

    private static async Task<AppDbContext> CreateDbAsync()
    {
        var connection = TestPostgres.ResolveConnectionString($"Data Source=library-routing-{Guid.NewGuid():N}.db");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }
}
