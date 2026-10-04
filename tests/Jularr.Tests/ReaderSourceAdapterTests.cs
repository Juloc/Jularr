using Jularr.Web.Features.Books;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReaderCore;

namespace Jularr.Tests;

[TestClass]
public sealed class ReaderSourceAdapterTests
{
    [TestMethod]
    public void NovelAdapterPreservesStructuredSourceAndMappedTranslation()
    {
        var legacyWorkId = Guid.NewGuid();
        var canonicalWorkId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var volumeId = Guid.NewGuid();
        var contentJson = NovelChapterDocument.Serialize(
        [
            new NovelContentBlock(
                NovelContentBlock.HeadingKind,
                [new NovelInlineRun("第一章", "だいいっしょう", Strong: true)],
                Level: 2),
            new NovelContentBlock(
                NovelContentBlock.ParagraphKind,
                [new NovelInlineRun("漢字", "かんじ"), new NovelInlineRun("です。")])
        ]);
        var chapter = new NovelReaderChapter(
            legacyWorkId,
            "Novel",
            "LIGHT_NOVEL",
            "epub",
            null,
            chapterId,
            1,
            "Chapter",
            "第一章\n\n漢字です。",
            "Kapitel Eins\n\nKanji.",
            null,
            null,
            null,
            volumeId,
            1,
            "Volume 1",
            NovelVolumeKinds.Epub,
            contentJson);
        var identity = new ReaderDocumentIdentity(
            canonicalWorkId,
            volumeId: Guid.NewGuid(),
            chapterId: Guid.NewGuid());

        var original = NovelReaderDocumentAdapter.Create(chapter, identity);
        var translated = NovelReaderDocumentAdapter.Create(
            chapter,
            identity,
            selectedVariantKey: NovelReaderDocumentAdapter.LegacyGermanVariantKey);

        Assert.AreEqual(canonicalWorkId, original.Descriptor.WorkId);
        Assert.AreNotEqual(legacyWorkId, original.Descriptor.WorkId);
        Assert.AreEqual(ReaderLayoutKind.ReflowableText, original.Descriptor.LayoutKind);
        var heading = (ReaderTextContentNode)original.Content[0];
        Assert.AreEqual(2, heading.HeadingLevel);
        Assert.AreEqual("だいいっしょう", heading.Runs[0].Ruby);

        Assert.AreEqual(ReaderVariantKind.LegacyTranslation, translated.SelectedVariant.Kind);
        Assert.IsTrue(translated.SelectedVariant.SupportsSourceMapping);
        var translatedParagraph = (ReaderTextContentNode)translated.Content[0];
        var source = (ReaderSourceItemReference)translatedParagraph.Source!;
        Assert.AreEqual("paragraph:0", source.SourceItemId);
        Assert.AreEqual("original", source.SourceDocument!.VariantKey);
        Assert.AreEqual(canonicalWorkId, source.SourceDocument.WorkId);
    }

    [TestMethod]
    public void NovelAdapterDisablesParallelMappingWhenParagraphCountsDiverge()
    {
        var chapter = NovelChapter(
            originalText: "One\n\nTwo",
            translationText: "Ein einzelner Absatz");
        var identity = new ReaderDocumentIdentity(Guid.NewGuid(), chapterId: Guid.NewGuid());

        var translated = NovelReaderDocumentAdapter.Create(
            chapter,
            identity,
            selectedVariantKey: NovelReaderDocumentAdapter.LegacyGermanVariantKey);

        Assert.IsFalse(translated.SelectedVariant.SupportsSourceMapping);
        Assert.IsNull(translated.Content[0].Source);
    }

    [TestMethod]
    public void BookFixedAdapterDistinguishesLogicalChapterFromPhysicalPage()
    {
        var reader = BookChapter();
        var canonicalWorkId = Guid.NewGuid();
        var identity = new ReaderDocumentIdentity(
            canonicalWorkId,
            editionId: Guid.NewGuid(),
            assetId: Guid.NewGuid(),
            fileId: Guid.NewGuid());
        var logicalPages = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var scan = BookReaderDocumentAdapter.CreateFixed(
            reader,
            identity,
            logicalPages,
            hasReliableTextLayer: false);
        var digital = BookReaderDocumentAdapter.CreateFixed(
            reader,
            identity,
            logicalPages,
            hasReliableTextLayer: true);

        Assert.AreEqual(ReaderLayoutKind.FixedPages, scan.Descriptor.LayoutKind);
        Assert.IsFalse(scan.Descriptor.Capabilities.SupportsTextSelection);
        Assert.IsFalse(scan.Descriptor.Capabilities.SupportsTts);
        Assert.IsTrue(digital.Descriptor.Capabilities.SupportsTextSelection);
        Assert.IsTrue(digital.Descriptor.Capabilities.SupportsTts);

        var page = (ReaderFixedRegionContentNode)digital.Content[1];
        var source = (ReaderSourcePageReference)page.Source!;
        Assert.AreEqual(1, source.SourcePageIndex);
        Assert.AreEqual($"chapter:{logicalPages[1]:N}", source.SourceItemId);
        Assert.AreEqual(identity.FileId, source.SourceDocument!.FileId);
    }

    [TestMethod]
    public void BookReflowTranslationMapsOnlyWhenParagraphsAlign()
    {
        var reader = BookChapter();
        var identity = new ReaderDocumentIdentity(
            Guid.NewGuid(),
            volumeId: Guid.NewGuid(),
            chapterId: Guid.NewGuid());

        var translated = BookReaderDocumentAdapter.CreateReflow(
            reader,
            identity,
            reader.TargetLanguage);

        Assert.AreEqual(ReaderVariantKind.LegacyTranslation, translated.SelectedVariant.Kind);
        Assert.IsTrue(translated.SelectedVariant.SupportsSourceMapping);
        var source = (ReaderSourceItemReference)translated.Content[0].Source!;
        Assert.AreEqual("original", source.SourceDocument!.VariantKey);
    }

    [TestMethod]
    public void MangaAdapterUsesCanonicalWorkAndMetadataDirection()
    {
        var legacySeriesId = Guid.NewGuid();
        var legacyChapterId = Guid.NewGuid();
        var canonicalWorkId = Guid.NewGuid();
        var chapter = new MangaChapterRead(
            legacyChapterId,
            legacySeriesId,
            "Manga",
            12,
            2,
            "Chapter 12",
            3,
            "rtl");
        var identity = new ReaderDocumentIdentity(
            canonicalWorkId,
            volumeId: Guid.NewGuid(),
            chapterId: Guid.NewGuid());
        var pages = Enumerable.Range(0, 3)
            .Select(index => new MangaPageItem(
                legacyChapterId,
                index,
                $"/cache/{index}.webp",
                "image/webp"))
            .ToArray();

        var document = MangaReaderDocumentAdapter.Create(chapter, identity, pages);

        Assert.AreEqual(canonicalWorkId, document.Descriptor.WorkId);
        Assert.AreNotEqual(legacySeriesId, document.Descriptor.WorkId);
        Assert.AreEqual(ReaderPageDirection.RightToLeft, document.Descriptor.PageDirection);
        Assert.AreEqual(ReaderLayoutKind.ImageSequence, document.Descriptor.LayoutKind);
        Assert.AreEqual(3, document.Content.Count);
        var first = (ReaderImageContentNode)document.Content[0];
        Assert.AreEqual($"manga:{legacyChapterId:N}:0", first.AssetKey);
    }

    [TestMethod]
    public void MangaAdapterRejectsForeignOrDuplicatePages()
    {
        var chapter = new MangaChapterRead(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Comic",
            1,
            null,
            "One",
            2,
            "ltr");
        var identity = new ReaderDocumentIdentity(Guid.NewGuid());
        var foreign = new[]
        {
            new MangaPageItem(Guid.NewGuid(), 0, "/cache/0.webp", "image/webp")
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => MangaReaderDocumentAdapter.Create(chapter, identity, foreign));
    }

    private static NovelReaderChapter NovelChapter(
        string originalText = "One\n\nTwo",
        string? translationText = "Eins\n\nZwei") =>
        new(
            Guid.NewGuid(),
            "Novel",
            "NOVEL",
            "narou",
            null,
            Guid.NewGuid(),
            1,
            "Chapter",
            originalText,
            translationText,
            null,
            null,
            null,
            Guid.NewGuid(),
            1,
            null,
            NovelVolumeKinds.Web,
            null);

    private static BookReaderChapter BookChapter()
    {
        var work = new NovelWork
        {
            Id = Guid.NewGuid(),
            Title = "Book",
            MetadataTitle = "Book",
            MetadataGenresJson = "[]"
        };
        var chapter = new NovelChapter
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            VolumeId = Guid.NewGuid(),
            Number = 1,
            Title = "Chapter",
            OriginalText = "Source one\n\nSource two"
        };
        var translation = new NovelTranslation
        {
            Id = Guid.NewGuid(),
            ChapterId = chapter.Id,
            TargetLanguage = "de",
            ProviderId = "test",
            PromptVersion = 4,
            Text = "Ziel eins\n\nZiel zwei"
        };

        return new BookReaderChapter(
            work,
            chapter,
            translation,
            ["Source one", "Source two"],
            ["Ziel eins", "Ziel zwei"],
            null,
            null,
            null,
            [],
            "en",
            "de");
    }
}
