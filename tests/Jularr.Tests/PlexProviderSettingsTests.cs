using Jularr.Web.Features.Plex;
using Jularr.Web.Features.Providers;
using Microsoft.Extensions.Configuration;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexProviderSettingsTests
{
    [TestMethod]
    public async Task ProviderSettings_SavePersistsIndependentCapabilities()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-settings-{Guid.NewGuid():N}");
        try
        {
            var config = new ConfigurationBuilder().Build();
            var store = new PlexIdentitySettingsStore(config, directory);
            var provider = new PlexIdentityProviderSettings(
                store,
                new PlexAuthClient(new HttpClient
                {
                    BaseAddress = new Uri("https://plex.tv/")
                }));

            var values = new Dictionary<string, string?>
            {
                ["loginEnabled"] = "true",
                ["linkEnabled"] = "true",
                ["autoProvisionEnabled"] = "false",
                ["requireApproval"] = "true",
                ["mediaConnectionEnabled"] = "true"
            };

            Assert.AreEqual(
                ProviderFeedback.Saved,
                await provider.SaveAsync(true, values, CancellationToken.None));

            var saved = await new PlexIdentitySettingsStore(
                config, directory).GetAsync();
            Assert.IsTrue(saved.Enabled);
            Assert.IsTrue(saved.CanLogin);
            Assert.IsTrue(saved.CanLink);
            Assert.IsFalse(saved.AutoProvisionEnabled);
            Assert.IsTrue(saved.RequireApproval);
            Assert.IsTrue(saved.CanConnectMedia);
            Assert.AreEqual(32, saved.ClientIdentifier.Length);

            var providerView = await provider.GetViewAsync(CancellationToken.None);
            Assert.AreEqual(ProviderFamily.Identity, providerView.Family);
            Assert.AreEqual(5, providerView.Options.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task MediaConnection_IsOffByDefaultAndDoesNotEnableLogin()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"plex-media-optin-{Guid.NewGuid():N}");
        try
        {
            var store = new PlexIdentitySettingsStore(
                new ConfigurationBuilder().Build(), root);
            var defaults = await store.GetAsync();
            Assert.IsFalse(defaults.CanConnectMedia);

            await store.SaveAsync(
                true, false, false, false, true,
                true, CancellationToken.None);
            var mediaOnly = await store.GetAsync();
            Assert.IsTrue(mediaOnly.CanConnectMedia);
            Assert.IsFalse(mediaOnly.CanLogin);
            Assert.IsFalse(mediaOnly.CanLink);

            await store.SaveAsync(
                false, false, false, false, true,
                true, CancellationToken.None);
            Assert.IsFalse((await store.GetAsync()).CanConnectMedia);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task ProviderSettings_DisablingDoesNotEraseLinkedAccountConfiguration()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new PlexIdentitySettingsStore(
                new ConfigurationBuilder().Build(), directory);

            await store.SaveAsync(
                true, true, true, false, true, CancellationToken.None);
            var id = (await store.GetAsync()).ClientIdentifier;
            await store.SaveAsync(
                false, true, true, false, true, CancellationToken.None);
            var disabled = await store.GetAsync();

            Assert.IsFalse(disabled.CanLogin);
            Assert.IsFalse(disabled.CanLink);
            Assert.AreEqual(id, disabled.ClientIdentifier);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ProviderSettings_ExternallyConfiguredCannotBeOverwritten()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Plex:LoginEnabled"] = "true",
                ["Plex:ClientIdentifier"] = "fixed-plex-id"
            }).Build();
        var directory = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-settings-{Guid.NewGuid():N}");
        var store = new PlexIdentitySettingsStore(config, directory);
        var options = await store.GetAsync();

        Assert.IsTrue(store.ExternallyManaged);
        Assert.IsTrue(options.CanLogin);
        Assert.IsFalse(options.AutoProvisionEnabled);
        Assert.IsTrue(options.RequireApproval);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => store.SaveAsync(
                false, false, false, false, true, CancellationToken.None));
        Assert.IsFalse(Directory.Exists(directory));
    }
}
