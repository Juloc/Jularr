using Jularr.Web.Features.ExternalPlayback.Plex;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexWebDestinationTests
{
    [TestMethod]
    public void HostedWebDetailLinkContainsOnlyValidatedMachineAndMetadataIdentifiers()
    {
        var uri = PlexWebDestinationService.BuildWebDetailUri(
            "plex-machine-1234", "734");
        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("app.plex.tv", uri.Host);
        StringAssert.Contains(
            uri.AbsoluteUri,
            "/#!/server/plex-machine-1234/details?key=%2Flibrary%2Fmetadata%2F734");
        Assert.IsFalse(uri.AbsoluteUri.Contains(
            "X-Plex-Token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void HostedWebDetailLinkRejectsUntrustedMachineOrRatingKey()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlexWebDestinationService.BuildWebDetailUri(
                "other/../server", "734"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlexWebDestinationService.BuildWebDetailUri(
                "plex-machine-1234", "734?X-Plex-Token=secret"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlexWebDestinationService.BuildWebDetailUri(
                "plex-machine-1234", "not-a-number"));
    }
}
