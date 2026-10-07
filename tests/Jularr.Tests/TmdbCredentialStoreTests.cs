using System.Text.Json;
using Jularr.Web.Features.Discovery;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Tests;

/// <summary>The one TMDB credential owner: what is stored and how, what wins between the deployment and the stored credential, and that no secret leaks.</summary>
[TestClass]
public sealed class TmdbCredentialStoreTests
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.stored-token.signature";
    private const string DeploymentToken = "eyJhbGciOiJIUzI1NiJ9.deployment-token.signature";

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "jularr-tmdb-" + Guid.NewGuid().ToString("N"));

    [TestMethod]
    public async Task ASavedCredentialIsEncryptedOnDiskAndRoundTrips()
    {
        var directory = NewDirectory();
        var store = TmdbTestSupport.Credentials(apiKey: null, directory: directory);

        await store.SaveAsync(new TmdbSettingsUpdate(true, Token, null), CancellationToken.None);

        var configuration = await store.GetAsync(CancellationToken.None);
        Assert.AreEqual(TmdbCredentialSource.Stored, configuration.Source);
        Assert.AreEqual(TmdbCredentialKind.ReadAccessToken, configuration.Credential!.Kind);
        Assert.AreEqual(Token, configuration.Credential.Secret);
        var json = await File.ReadAllTextAsync(Path.Combine(directory, TmdbCredentialStore.FileName));
        Assert.IsFalse(json.Contains(Token, StringComparison.Ordinal), "The secret is only ever on disk as a Data Protection blob.");
        Assert.IsTrue(JsonDocument.Parse(json).RootElement.GetProperty("protectedReadAccessToken").GetString()!.StartsWith("CfDJ8", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnEditThatGivesNoSecretKeepsTheStoredOneAndRemovingKeepsTheSwitch()
    {
        var store = TmdbTestSupport.Credentials(apiKey: null, directory: NewDirectory());
        await store.SaveAsync(new TmdbSettingsUpdate(true, Token, null), CancellationToken.None);

        await store.SaveAsync(new TmdbSettingsUpdate(false, null, null), CancellationToken.None);
        var switchedOff = await store.GetAsync(CancellationToken.None);
        Assert.IsFalse(switchedOff.Enabled);
        Assert.AreEqual(TmdbAvailability.Disabled, switchedOff.Availability);
        Assert.AreEqual(Token, switchedOff.Credential!.Secret, "An unrelated edit never asks for the secret again.");

        await store.RemoveCredentialAsync(CancellationToken.None);
        var removed = await store.GetAsync(CancellationToken.None);
        Assert.IsNull(removed.Credential);
        Assert.IsFalse(removed.Enabled);
        Assert.AreEqual(TmdbAvailability.NotConfigured, removed.Availability);
    }

    [TestMethod]
    public async Task TheDeploymentCredentialWinsOverTheStoredOneAndTheTokenWinsOverTheApiKey()
    {
        var directory = NewDirectory();
        var protection = new EphemeralDataProtectionProvider();
        var stored = TmdbTestSupport.Credentials(apiKey: null, directory: directory, protection: protection);
        await stored.SaveAsync(new TmdbSettingsUpdate(true, Token, "stored-api-key"), CancellationToken.None);

        var withDeploymentKey = await TmdbTestSupport.Credentials(apiKey: "deployment-key", directory: directory, protection: protection).GetAsync(CancellationToken.None);
        Assert.AreEqual(TmdbCredentialSource.Environment, withDeploymentKey.Source);
        Assert.AreEqual(TmdbCredentialKind.ApiKey, withDeploymentKey.Credential!.Kind);
        Assert.AreEqual("deployment-key", withDeploymentKey.Credential.Secret);
        Assert.IsTrue(withDeploymentKey.HasStoredCredential, "The stored credential is still known, so the Admin UI can say it is ignored.");

        var withBoth = await TmdbTestSupport.Credentials(apiKey: "deployment-key", readAccessToken: DeploymentToken, directory: directory, protection: protection).GetAsync(CancellationToken.None);
        Assert.AreEqual(TmdbCredentialKind.ReadAccessToken, withBoth.Credential!.Kind);
        Assert.AreEqual(DeploymentToken, withBoth.Credential.Secret);

        var withNone = await TmdbTestSupport.Credentials(apiKey: null, directory: directory, protection: protection).GetAsync(CancellationToken.None);
        Assert.AreEqual(TmdbCredentialSource.Stored, withNone.Source);
        Assert.AreEqual(Token, withNone.Credential!.Secret);
    }

    [TestMethod]
    public async Task TheSwitchIsAlwaysTheStoredOneEvenWhenTheDeploymentSuppliesTheCredential()
    {
        var directory = NewDirectory();
        var store = TmdbTestSupport.Credentials(apiKey: "deployment-key", directory: directory);

        await store.SaveAsync(new TmdbSettingsUpdate(false, null, null), CancellationToken.None);

        var configuration = await store.GetAsync(CancellationToken.None);
        Assert.AreEqual(TmdbCredentialSource.Environment, configuration.Source);
        Assert.AreEqual(TmdbAvailability.Disabled, configuration.Availability);
    }

    [TestMethod]
    public async Task ACredentialThatCanNoLongerBeDecryptedCountsAsAbsentAndIsReportedNotHidden()
    {
        var directory = NewDirectory();
        await TmdbTestSupport.Credentials(apiKey: null, directory: directory, protection: DataProtectionProvider.Create(new DirectoryInfo(NewDirectory())))
            .SaveAsync(new TmdbSettingsUpdate(true, Token, null), CancellationToken.None);

        var configuration = await TmdbTestSupport.Credentials(apiKey: null, directory: directory, protection: DataProtectionProvider.Create(new DirectoryInfo(NewDirectory())))
            .GetAsync(CancellationToken.None);

        Assert.IsNull(configuration.Credential);
        Assert.IsTrue(configuration.StoredCredentialUnreadable);
        Assert.AreEqual(TmdbAvailability.NotConfigured, configuration.Availability);
    }

    [TestMethod]
    public async Task ASavedCredentialStillCountsAfterARestartWithTheSameKeys()
    {
        var directory = NewDirectory();
        var keys = new DirectoryInfo(NewDirectory());
        await TmdbTestSupport.Credentials(apiKey: null, directory: directory, protection: DataProtectionProvider.Create(keys))
            .SaveAsync(new TmdbSettingsUpdate(true, Token, null), CancellationToken.None);

        var afterRestart = await TmdbTestSupport.Credentials(apiKey: null, directory: directory, protection: DataProtectionProvider.Create(keys)).GetAsync(CancellationToken.None);

        Assert.AreEqual(TmdbCredentialKind.ReadAccessToken, afterRestart.Credential!.Kind);
        Assert.AreEqual(Token, afterRestart.Credential.Secret);
        Assert.AreEqual(TmdbCredentialSource.Stored, afterRestart.Source);
        Assert.AreNotEqual(TmdbAvailability.NotConfigured, afterRestart.Availability);
    }

    [TestMethod]
    public async Task SwitchingBetweenTheTokenAndTheApiKeyAlwaysLeavesTheOneEnteredLastInEffect()
    {
        var store = TmdbTestSupport.Credentials(apiKey: null, directory: NewDirectory());

        await store.SaveAsync(new TmdbSettingsUpdate(true, Token, null), CancellationToken.None);
        var viaToken = await store.GetAsync(CancellationToken.None);
        await store.SaveAsync(new TmdbSettingsUpdate(true, null, "api-key-789"), CancellationToken.None);
        var viaKey = await store.GetAsync(CancellationToken.None);
        await store.SaveAsync(new TmdbSettingsUpdate(true, Token, null), CancellationToken.None);
        var backToToken = await store.GetAsync(CancellationToken.None);
        await store.SaveAsync(new TmdbSettingsUpdate(false, null, null), CancellationToken.None);
        var edited = await store.GetAsync(CancellationToken.None);

        Assert.AreEqual(TmdbCredentialKind.ReadAccessToken, viaToken.Credential!.Kind);
        Assert.AreEqual(TmdbCredentialKind.ApiKey, viaKey.Credential!.Kind, "A newly entered API key is not ignored behind a token saved earlier.");
        Assert.IsFalse(viaKey.HasStoredReadAccessToken);
        Assert.AreEqual(TmdbCredentialKind.ReadAccessToken, backToToken.Credential!.Kind);
        Assert.IsFalse(backToToken.HasStoredApiKey);
        Assert.AreEqual(TmdbCredentialKind.ReadAccessToken, edited.Credential!.Kind, "An edit that gives no secret keeps what is stored.");
        Assert.IsFalse(edited.Enabled);
    }

    [TestMethod]
    public async Task ACredentialThatCannotBeDecryptedDoesNotPreventEnteringANewOne()
    {
        var directory = NewDirectory();
        await TmdbTestSupport.Credentials(apiKey: null, directory: directory, protection: DataProtectionProvider.Create(new DirectoryInfo(NewDirectory())))
            .SaveAsync(new TmdbSettingsUpdate(true, Token, null), CancellationToken.None);
        var store = TmdbTestSupport.Credentials(apiKey: null, directory: directory, protection: DataProtectionProvider.Create(new DirectoryInfo(NewDirectory())));
        Assert.IsTrue((await store.GetAsync(CancellationToken.None)).StoredCredentialUnreadable);

        await store.SaveAsync(new TmdbSettingsUpdate(true, null, "replacement-key"), CancellationToken.None);

        var replaced = await store.GetAsync(CancellationToken.None);
        Assert.IsFalse(replaced.StoredCredentialUnreadable);
        Assert.AreEqual(TmdbCredentialKind.ApiKey, replaced.Credential!.Kind);
        Assert.AreEqual("replacement-key", replaced.Credential.Secret);
    }

    [TestMethod]
    public async Task AnInvalidSecretIsRefusedBeforeItIsStored()
    {
        var store = TmdbTestSupport.Credentials(apiKey: null, directory: NewDirectory());

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(new TmdbSettingsUpdate(true, "has a space", null), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(new TmdbSettingsUpdate(true, null, "line\nbreak"), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(new TmdbSettingsUpdate(true, new string('a', TmdbCredentialStore.MaxReadAccessTokenLength + 1), null), CancellationToken.None));
        Assert.IsTrue(TmdbCredentialStore.IsValidSecret(Token, TmdbCredentialStore.MaxReadAccessTokenLength));
        Assert.IsFalse(TmdbCredentialStore.IsValidSecret("", TmdbCredentialStore.MaxApiKeyLength));
    }

    [TestMethod]
    public async Task TheCredentialNeverPrintsOrSerializesItsSecret()
    {
        var store = TmdbTestSupport.Credentials(apiKey: null, readAccessToken: DeploymentToken, directory: NewDirectory());

        var configuration = await store.GetAsync(CancellationToken.None);

        Assert.IsFalse(configuration.ToString().Contains(DeploymentToken, StringComparison.Ordinal));
        Assert.IsFalse(configuration.Credential!.ToString().Contains(DeploymentToken, StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(configuration.Credential).Contains(DeploymentToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public void NothingButTheCredentialStoreReadsTheTmdbDeploymentSettings()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Jularr.sln")))
        {
            root = root.Parent;
        }

        var offenders = Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "Jularr.Web"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => Path.GetFileName(file) != "TmdbCredentialStore.cs" && File.ReadAllText(file).Contains("Providers:Tmdb", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.AreEqual(0, offenders.Length, "One owner reads the TMDB credential: " + string.Join(", ", offenders));
    }
}
