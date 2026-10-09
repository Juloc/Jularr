using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Tests;

/// <summary>
/// The stores a Wanted pass reads for every search (profiles, indexers) parse their file once and keep the result until the file changes, so a pass costs one
/// read instead of one per request, and an edit made by anyone (another instance, the owner's own save) is seen at the next read.
/// </summary>
[TestClass]
public sealed class AcquisitionStoreCacheTests
{
    [TestMethod]
    public async Task TheProfileFileIsParsedOnceUntilItChanges()
    {
        var directory = Directory.CreateTempSubdirectory("jularr-profile-cache-");
        try
        {
            var registry = new MediaAcquisitionRegistry([new AnimeAcquisitionRegistration()]);
            var store = new QualityProfileStore(directory, registry);
            var other = new QualityProfileStore(directory, registry);
            var seeded = await store.LoadAsync();
            await store.UpsertAsync(seeded.Profiles[0] with { Name = "Seeded" });

            var first = await store.LoadAsync();
            var second = await store.LoadAsync();
            var edited = first.Profiles[0] with { Id = "anime-edited", Name = "Edited elsewhere" };
            await other.UpsertAsync(edited);
            var third = await store.LoadAsync();

            Assert.AreSame(first, second, "Nothing changed, so nothing was parsed again.");
            Assert.AreNotSame(second, third);
            Assert.IsTrue(third.Profiles.Any(profile => profile.Id == "anime-edited"), "An edit made by another instance is seen at the next read.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task TheIndexerFileIsParsedOnceUntilItChangesAndAnOwnSaveIsSeenAtOnce()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            var provider = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
            var store = new IndexerStore(provider, directory);
            var entry = new IndexerEntry(Guid.NewGuid(), "One", IndexerType.Newznab, true, 1, new IndexerSettings("https://one.example", [5070], [], 100), "key");
            await store.SaveAsync(entry);

            var first = await store.LoadAllAsync();
            var second = await store.LoadAllAsync();
            await store.SaveAsync(entry with { Name = "Renamed" });
            var third = await store.LoadAllAsync();

            Assert.AreSame(first, second);
            Assert.AreEqual("One", first.Single().Name);
            Assert.AreEqual("Renamed", third.Single().Name);
            Assert.AreEqual("key", third.Single().ApiKey, "The decrypted key survives the cache.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
