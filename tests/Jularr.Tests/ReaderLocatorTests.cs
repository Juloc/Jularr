using Jularr.Web.Features.ReaderCore;

namespace Jularr.Tests;

[TestClass]
public sealed class ReaderLocatorTests
{
    [TestMethod]
    public void ReflowLocatorKeepsLogicalAnchorIndependentOfRenderedPages()
    {
        var identity = new ReaderDocumentIdentity(Guid.NewGuid(), chapterId: Guid.NewGuid(), editionId: Guid.NewGuid(), variantKey: "de-generated");
        var source = new ReaderSourceReference(11, new ReaderSourceRegion(.1, .2, .7, .3), "pdf:block:42");
        var locator = new ReflowTextReaderLocator(identity, "paragraph:17", characterOffset: 83, source: source, fallbackPercent: 41.5);

        Assert.AreEqual("paragraph:17", locator.BlockId);
        Assert.AreEqual(83, locator.CharacterOffset);
        Assert.AreEqual(11, locator.Source!.SourcePageIndex);
        Assert.AreEqual("pdf:block:42", locator.Source.SourceItemId);
        Assert.AreEqual(41.5, locator.FallbackPercent);
    }

    [TestMethod]
    public void ImageLocatorUsesLogicalPageAndOptionalViewport()
    {
        var identity = new ReaderDocumentIdentity(Guid.NewGuid(), chapterId: Guid.NewGuid());
        var viewport = new ReaderViewportPosition(.5, .75, 2);
        var locator = new ImageSequenceReaderLocator(identity, pageIndex: 14, viewport: viewport, fallbackPercent: 37);

        Assert.AreEqual(14, locator.PageIndex);
        Assert.AreEqual(.5, locator.Viewport!.X);
        Assert.AreEqual(.75, locator.Viewport.Y);
        Assert.AreEqual(2, locator.Viewport.Zoom);
    }

    [TestMethod]
    public void FixedLocatorKeepsLogicalAndPhysicalPagesDistinct()
    {
        var identity = new ReaderDocumentIdentity(Guid.NewGuid(), assetId: Guid.NewGuid());
        var leftHalf = new ReaderSourceRegion(0, 0, .5, 1);
        var locator = new FixedPageReaderLocator(identity, logicalPageIndex: 18, sourcePageIndex: 9, sourceRegion: leftHalf, fallbackPercent: 28);

        Assert.AreEqual(18, locator.LogicalPageIndex);
        Assert.AreEqual(9, locator.SourcePageIndex);
        Assert.AreEqual(.5, locator.SourceRegion!.Width);
    }

    [TestMethod]
    public void LocatorsRejectInvalidAnchorsAndFallbacks()
    {
        var identity = new ReaderDocumentIdentity(Guid.NewGuid());

        Assert.ThrowsException<ArgumentException>(() => new ReflowTextReaderLocator(identity, ""));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ReflowTextReaderLocator(identity, "p:1", characterOffset: -1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ImageSequenceReaderLocator(identity, -1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FixedPageReaderLocator(identity, 0, -1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ImageSequenceReaderLocator(identity, 0, fallbackPercent: 101));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ReaderViewportPosition(0, 0, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ReaderSourceRegion(.8, 0, .3, 1));
    }
}
