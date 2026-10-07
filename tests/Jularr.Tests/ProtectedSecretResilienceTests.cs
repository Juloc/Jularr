using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Tests;

/// <summary>A changed Data Protection key ring leaves stored secrets unreadable; the stores must report them as absent instead of failing every page that lists them.</summary>
[TestClass]
public sealed class ProtectedSecretResilienceTests
{
    [TestMethod]
    public async Task IndexersAndDownloadClientsWithUnreadableSecretsStillLoadAndAreEnteredAgain()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            var before = new EphemeralDataProtectionProvider();
            var indexer = new IndexerEntry(Guid.NewGuid(), "Indexer", IndexerType.Newznab, true, 1, new IndexerSettings("https://indexer.example", [5070], [], 100), "indexer-key");
            var client = new DownloadClientEntry(Guid.NewGuid(), "Client", DownloadClientType.Sabnzbd, true, 1, new DownloadClientSettings("http://client.example"), "client-secret");
            await new IndexerStore(before, directory).SaveAsync(indexer);
            await new DownloadClientStore(before, directory).SaveAsync(client);
            Assert.AreEqual("indexer-key", (await new IndexerStore(before, directory).LoadAllAsync()).Single().ApiKey, "The same keys read the secret.");

            var after = new EphemeralDataProtectionProvider();
            var indexers = await new IndexerStore(after, directory).LoadAllAsync();
            var clients = await new DownloadClientStore(after, directory).LoadAllAsync();

            Assert.AreEqual(indexer.Name, indexers.Single().Name, "The entry itself is still there.");
            Assert.AreEqual(string.Empty, indexers.Single().ApiKey);
            Assert.AreEqual(client.Name, clients.Single().Name);
            Assert.IsNull(clients.Single().Secret);

            await new IndexerStore(after, directory).SaveAsync(indexers.Single() with { ApiKey = "entered-again" });
            Assert.AreEqual("entered-again", (await new IndexerStore(after, directory).LoadAllAsync()).Single().ApiKey);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
