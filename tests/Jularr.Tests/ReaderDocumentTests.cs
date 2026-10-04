using Jularr.Web.Features.ReaderCore;

namespace Jularr.Tests;

[TestClass]
public sealed class ReaderDocumentTests
{
    [TestMethod]
    [DataRow("NOVEL", "narou", ReaderContentType.LightNovel)]
    [DataRow("LIGHT_NOVEL", "narou", ReaderContentType.LightNovel)]
    [DataRow(null, "narou", ReaderContentType.WebNovel)]
    public void NovelMetadataResolvesContentType(string? format, string? sourceProvider, ReaderContentType expected)
    {
        Assert.AreEqual(expected, ReaderContentTypes.FromNovelMetadata(format, sourceProvider));
    }

    [TestMethod]
    public void ReflowReadersExposeSharedCapabilities()
    {
        var book = ReaderDocumentDescriptor.Create(Guid.NewGuid(), ReaderContentType.Book, "Book");
        var lightNovel = ReaderDocumentDescriptor.Create(Guid.NewGuid(), ReaderContentType.LightNovel, "LN");

        Assert.AreEqual(ReaderLayoutKind.ReflowableText, book.LayoutKind);
        Assert.IsTrue(book.Capabilities.SupportsTypography);
        Assert.IsTrue(book.Capabilities.SupportsPaged);
        Assert.IsTrue(lightNovel.Capabilities.SupportsContinuous);
        Assert.IsTrue(lightNovel.Capabilities.SupportsEmbeddedImages);
    }

    [TestMethod]
    public void LayoutKindOverridesMediaTypeDefaults()
    {
        var fixedBook = ReaderDocumentDescriptor.Create(Guid.NewGuid(), ReaderContentType.Book, "Fixed EPUB", layoutKind: ReaderLayoutKind.FixedPages);
        var semanticFixedDocument = ReaderDocumentDescriptor.Create(Guid.NewGuid(), ReaderContentType.FixedDocument, "Smart document", layoutKind: ReaderLayoutKind.ReflowableText);

        Assert.IsTrue(fixedBook.Capabilities.SupportsZoom);
        Assert.IsFalse(fixedBook.Capabilities.SupportsTypography);
        Assert.IsFalse(fixedBook.Capabilities.SupportsHighlights);
        Assert.IsTrue(semanticFixedDocument.Capabilities.SupportsTypography);
        Assert.IsFalse(semanticFixedDocument.Capabilities.SupportsZoom);
    }

    [TestMethod]
    public void ParsedDocumentCanNarrowCapabilitiesAndKeepDirectionsSeparate()
    {
        var capabilities = ReaderCapabilities.For(ReaderContentType.Book, ReaderLayoutKind.FixedPages) with
        {
            SupportsTextSelection = false,
            SupportsHighlights = false,
            SupportsTts = false
        };

        var scan = ReaderDocumentDescriptor.Create(
            Guid.NewGuid(),
            ReaderContentType.Book,
            "Scan",
            layoutKind: ReaderLayoutKind.FixedPages,
            capabilities: capabilities,
            pageDirection: ReaderPageDirection.RightToLeft,
            textDirection: ReaderTextDirection.LeftToRight,
            writingMode: ReaderWritingMode.HorizontalTb);

        Assert.IsFalse(scan.Capabilities.SupportsTts);
        Assert.AreEqual(ReaderPageDirection.RightToLeft, scan.PageDirection);
        Assert.AreEqual(ReaderTextDirection.LeftToRight, scan.TextDirection);
        Assert.AreEqual(ReaderWritingMode.HorizontalTb, scan.WritingMode);
    }

    [TestMethod]
    public void RendererDocumentRequiresMatchingWorkAndStableContentIds()
    {
        var workId = Guid.NewGuid();
        var descriptor = ReaderDocumentDescriptor.Create(workId, ReaderContentType.Book, "Book");
        var identity = new ReaderDocumentIdentity(workId, chapterId: Guid.NewGuid(), editionId: Guid.NewGuid(), variantKey: "original");
        var document = ReaderDocument.Create(descriptor, identity, [new ReaderTextContentNode("p:1", ReaderTextBlockKind.Paragraph, "Text")]);

        Assert.AreEqual(workId, document.Identity.WorkId);
        Assert.AreEqual("p:1", document.Content[0].StableId);

        Assert.ThrowsExactly<ArgumentException>(() => ReaderDocument.Create(descriptor, new ReaderDocumentIdentity(Guid.NewGuid())));
        Assert.ThrowsExactly<ArgumentException>(() => ReaderDocument.Create(
            descriptor,
            identity,
            [
                new ReaderTextContentNode("same", ReaderTextBlockKind.Paragraph, "A"),
                new ReaderSceneBreakContentNode("same")
            ]));
    }

    [TestMethod]
    public void BuiltInPresetsDifferByTypeWithoutChangingTheEngine()
    {
        var book = ReaderPresetCatalog.For(ReaderContentType.Book);
        var lightNovel = ReaderPresetCatalog.For(ReaderContentType.LightNovel);
        var webNovel = ReaderPresetCatalog.For(ReaderContentType.WebNovel);

        Assert.AreEqual("paged", book.ReadingMode);
        Assert.AreEqual("paged", lightNovel.ReadingMode);
        Assert.AreEqual("light-novel", lightNovel.ChapterStyle);
        Assert.AreEqual("continuous", webNovel.ReadingMode);
        Assert.IsFalse(webNovel.TwoPageSpread);
    }
}
