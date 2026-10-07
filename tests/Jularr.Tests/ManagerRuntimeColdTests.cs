using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>A module that is switched off is not just hidden: the background workers that belong to it find nothing to do.</summary>
[TestClass]
public sealed class ManagerRuntimeColdTests
{
    [TestMethod]
    public async Task TheAnimeFolderWatchersAndStartupScanHaveNoRootsWhileTheAnimeModuleIsOff()
    {
        var connection = TestPostgres.ResolveConnectionString($"Data Source=manager-cold-{Guid.NewGuid():N}.db");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        var root = new LibraryRoot { Name = "Anime", Path = "/media/anime" };
        db.LibraryRoots.Add(root);
        db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = root.Id, ContentType = LibraryContentType.Anime });
        await db.SaveChangesAsync();

        var withoutModules = new ServiceCollection().BuildServiceProvider();
        Assert.AreEqual(1, await (await db.AnimeRootsForBackgroundWorkAsync(withoutModules, CancellationToken.None)).CountAsync());

        var manager = InstanceModulePresets.Apply(InstancePreset.MediaManager, InstanceModuleSettings.Default);
        Assert.AreEqual(1, await (await db.AnimeRootsForBackgroundWorkAsync(Modules(manager), CancellationToken.None)).CountAsync(), "The Media Manager preset keeps the Anime library.");
        Assert.AreEqual(0, await (await db.AnimeRootsForBackgroundWorkAsync(Modules(manager.With(InstanceModule.Anime, false)), CancellationToken.None)).CountAsync());
    }

    private static IServiceProvider Modules(InstanceModuleSettings settings) =>
        new ServiceCollection().AddSingleton<IInstanceModuleService>(new FixedModules(settings)).BuildServiceProvider();

    private sealed class FixedModules(InstanceModuleSettings settings) : IInstanceModuleService
    {
        public Task<InstanceModuleSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);

        public Task<bool> IsEnabledAsync(InstanceModule module, CancellationToken cancellationToken = default) => Task.FromResult(settings.IsEnabled(module));

        public Task<InstanceModuleSettings> SetAsync(InstanceModule module, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InstanceModuleSettings> SaveAsync(InstanceModuleSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
