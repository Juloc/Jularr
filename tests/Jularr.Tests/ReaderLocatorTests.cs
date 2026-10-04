using Jularr.Web.Features.ReaderCore;

namespace Jularr.Tests;

[TestClass]
public sealed class ReaderLocatorTests
{
    [TestMethod]
    public void ReflowLocatorKeepsLogicalAnchorIndependentOfRenderedPages()
    {
        var workId = Guid.NewGuid();
        var sourceIdentity = new ReaderDocumentIdentity(workId, assetId: Guid.NewGuid(), fileId: Guid.NewGuid(), variantKey: "original");
        var identity = new ReaderDocumentIdentity(workId, chapterId: Guid.NewGuid(), editionId: Guid.NewGuid(), variantKey: "de-generated");
        var source = new ReaderSourcePageReference(
            11,
            new ReaderSourceRegion(.1, .2, .7, .3),
            "pdf:block:42",
            sourceIdentity);
        var locator = new ReflowTextReaderLocator(identity, "paragraph:17", characterOffset: 83, source: source, fallbackPercent: 41.5);

        Assert.AreEqual("paragraph:17", locator.BlockId);
        Assert.AreEqual(83, locator.CharacterOffset);
        var pageSource = (ReaderSourcePageReference)locator.Source!;
        Assert.AreEqual(11, pageSource.SourcePageIndex);
        Assert.AreEqual("pdf:block:42", pageSource.SourceItemId);
        Assert.AreEqual(sourceIdentity.FileId, pageSource.SourceDocument!.FileId);
        Assert.AreEqual(41.5, locator.FallbackPercent);
    }

    [TestMethod]
    public void ReflowLocatorCanMapToSourceItemWithoutPageIdentity()
    {
        var workId = Guid.NewGuid();
        var sourceIdentity = new ReaderDocumentIdentity(workId, editionId: Guid.NewGuid(), versionId: Guid.NewGuid(), variantKey: "original");
        var identity = new ReaderDocumentIdentity(workId, editionId: Guid.NewGuid(), variantKey: "translated");
        var source = new ReaderSourceItemReference("source-paragraph:17", sourceIdentity);
        var locator = new ReflowTextReaderLocator(identity, "translated-paragraph:17", source: source);

        var itemSource = (ReaderSourceItemReference)locator.Source!;
        Assert.AreEqual("source-paragraph:17", itemSource.SourceItemId);
        Assert.AreEqual(sourceIdentity.VersionId, itemSource.SourceDocument!.VersionId);
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

        Assert.ThrowsExactly<ArgumentException>(() => new ReflowTextReaderLocator(identity, ""));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReflowTextReaderLocator(identity, "p:1", characterOffset: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ImageSequenceReaderLocator(identity, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FixedPageReaderLocator(identity, 0, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ImageSequenceReaderLocator(identity, 0, fallbackPercent: 101));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReaderViewportPosition(0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReaderViewportPosition(double.NaN, 0, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReaderSourceRegion(.8, 0, .3, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReaderSourceRegion(0, 0, double.PositiveInfinity, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ImageSequenceReaderLocator(identity, 0, fallbackPercent: double.NaN));
    }
}
