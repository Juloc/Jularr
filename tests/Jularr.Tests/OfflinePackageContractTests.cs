using Jularr.Web.Features.ClientApi;

namespace Jularr.Tests;

[TestClass]
public sealed class OfflinePackageContractTests
{
    [TestMethod]
    public void WorkTargetAcceptsCanonicalWorkStructureButRejectsGameIdentity()
    {
        var target = new ClientOfflinePackageTarget(
            ClientApiOfflinePackageContract.WorkTarget,
            WorkId: Guid.NewGuid(),
            WorkEpisodeId: Guid.NewGuid());

        Assert.IsTrue(ClientApiOfflinePackageContract.TryValidateTarget(target, out var validMessage));
        Assert.AreEqual("", validMessage);

        var mixed = target with { GameId = Guid.NewGuid() };
        Assert.IsFalse(ClientApiOfflinePackageContract.TryValidateTarget(mixed, out var invalidMessage));
        StringAssert.Contains(invalidMessage, "Game identifiers");
    }

    [TestMethod]
    public void GameTargetIsFirstClassAndDoesNotRequireMediaCoreWork()
    {
        var target = new ClientOfflinePackageTarget(
            ClientApiOfflinePackageContract.GameTarget,
            GameId: Guid.NewGuid(),
            GameReleaseId: Guid.NewGuid());

        Assert.IsTrue(ClientApiOfflinePackageContract.TryValidateTarget(target, out var message));
        Assert.AreEqual("", message);
    }

    [TestMethod]
    public void TargetRejectsAmbiguousEpisodeAndChapter()
    {
        var target = new ClientOfflinePackageTarget(
            ClientApiOfflinePackageContract.WorkTarget,
            WorkId: Guid.NewGuid(),
            WorkEpisodeId: Guid.NewGuid(),
            WorkChapterId: Guid.NewGuid());

        Assert.IsFalse(ClientApiOfflinePackageContract.TryValidateTarget(target, out var message));
        StringAssert.Contains(message, "episode and a chapter");
    }

    [TestMethod]
    public void IntentNamesAreStableWireValues()
    {
        Assert.IsTrue(ClientApiOfflinePackageContract.TryParseIntent("WATCH", out var watch));
        Assert.AreEqual(ClientOfflinePackageIntent.Watch, watch);
        Assert.AreEqual("watch", ClientApiOfflinePackageContract.IntentName(watch));

        Assert.IsTrue(ClientApiOfflinePackageContract.TryParseIntent("play", out var play));
        Assert.AreEqual(ClientOfflinePackageIntent.Play, play);
        Assert.AreEqual("play", ClientApiOfflinePackageContract.IntentName(play));

        Assert.IsFalse(ClientApiOfflinePackageContract.TryParseIntent("install", out _));
    }
}
