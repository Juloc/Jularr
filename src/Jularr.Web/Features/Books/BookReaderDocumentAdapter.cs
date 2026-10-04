using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Features.ReaderPreferences;

namespace Jularr.Web.Features.Books;

/// <summary>
/// Adapts current Books projections into canonical Reader documents. The caller
/// supplies canonical MediaCore identity; legacy Book/Novel ids stay source data.
/// </summary>
public static class BookReaderDocumentAdapter
{
    public const string OriginalVariantKey = "original";

    public static ReaderDocument CreateReflow(
        BookReaderChapter reader,
        ReaderDocumentIdentity identity,
        string? selectedLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(identity);

        var selected = string.IsNullOrWhiteSpace(selectedLanguage)
            ? reader.SourceLanguage
            : selectedLanguage.Trim();
        var variants = BuildVariants(reader);
        var selectedVariant = variants.FirstOrDefault(variant =>
            string.Equals(variant.LanguageTag, selected, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"Language '{selected}' is not available for this book chapter.",
                nameof(selectedLanguage));

        var descriptor = ReaderDocumentDescriptor.Create(
            identity.WorkId,
            ReaderContentType.Book,
            reader.Work.MetadataTitle ?? reader.Work.Title,
            ReaderPreferenceRules.ParseGenres(reader.Work.MetadataGenresJson),
            ReaderLayoutKind.ReflowableText,
            variants
                .Select(variant => new ReaderDocumentLanguage(
                    variant.LanguageTag,
                    BookLanguageCatalog.GetName(variant.LanguageTag)))
                .ToArray());

        var documentIdentity = identity.WithVariant(selectedVariant.Key);
        var content = selectedVariant.Kind == ReaderVariantKind.Original
            ? BuildOriginalContent(reader)
            : BuildTranslationContent(
                reader,
                documentIdentity,
                selectedVariant.SupportsSourceMapping);

        return ReaderDocument.Create(
            descriptor,
            documentIdentity,
            content,
            reader.SourceLanguage,
            selectedVariant,
            variants);
    }

    public static ReaderDocument CreateFixed(
        BookReaderChapter reader,
        ReaderDocumentIdentity identity,
        IReadOnlyList<Guid> physicalPageChapterIds,
        bool hasReliableTextLayer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(physicalPageChapterIds);

        var capabilities = ReaderCapabilities.For(
            ReaderContentType.Book,
            ReaderLayoutKind.FixedPages) with
        {
            SupportsTextSelection = hasReliableTextLayer,
            SupportsHighlights = hasReliableTextLayer,
            SupportsTts = hasReliableTextLayer,
            SupportsTranslation = false,
            SupportsDualLanguage = false
        };
        var descriptor = ReaderDocumentDescriptor.Create(
            identity.WorkId,
            ReaderContentType.Book,
            reader.Work.MetadataTitle ?? reader.Work.Title,
            ReaderPreferenceRules.ParseGenres(reader.Work.MetadataGenresJson),
            ReaderLayoutKind.FixedPages,
            [new ReaderDocumentLanguage(
                reader.SourceLanguage,
                BookLanguageCatalog.GetName(reader.SourceLanguage))],
            capabilities);

        var variant = new ReaderDocumentVariant(
            OriginalVariantKey,
            reader.SourceLanguage,
            ReaderVariantKind.Original,
            supportsSourceMapping: true);
        var documentIdentity = identity.WithVariant(variant.Key);
        var pages = physicalPageChapterIds
            .Select((chapterId, pageIndex) => (ReaderContentNode)new ReaderFixedRegionContentNode(
                $"page:{pageIndex}",
                new ReaderSourcePageReference(
                    pageIndex,
                    sourceItemId: $"chapter:{chapterId:N}",
                    sourceDocument: documentIdentity)))
            .ToArray();

        return ReaderDocument.Create(
            descriptor,
            documentIdentity,
            pages,
            reader.SourceLanguage,
            variant,
            [variant]);
    }

    private static IReadOnlyList<ReaderDocumentVariant> BuildVariants(
        BookReaderChapter reader)
    {
        var variants = new List<ReaderDocumentVariant>
        {
            new(
                OriginalVariantKey,
                reader.SourceLanguage,
                ReaderVariantKind.Original,
                supportsSourceMapping: true)
        };

        if (reader.Translation is { } translation
            && !reader.SourceLanguage.Equals(
                reader.TargetLanguage,
                StringComparison.OrdinalIgnoreCase))
        {
            variants.Add(new ReaderDocumentVariant(
                $"translation:{reader.TargetLanguage.ToLowerInvariant()}",
                reader.TargetLanguage,
                ReaderVariantKind.LegacyTranslation,
                reader.OriginalParagraphs.Count == reader.TranslatedParagraphs.Count,
                translation.ProviderId,
                translation.PromptVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)));
        }

        return variants;
    }

    private static IReadOnlyList<ReaderContentNode> BuildOriginalContent(
        BookReaderChapter reader)
    {
        var blocks = NovelChapterDocument.BuildReaderBlocks(
            reader.Chapter.OriginalText,
            reader.Chapter.ContentJson);
        var content = new List<ReaderContentNode>(blocks.Count);

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (block.IsImage)
            {
                content.Add(new ReaderImageContentNode(
                    $"block:{index}",
                    block.ImageAsset!,
                    block.ImageAlt));
                continue;
            }

            var paragraphId = $"paragraph:{block.ParagraphIndex!.Value}";
            if (block.IsSceneBreak)
            {
                content.Add(new ReaderSceneBreakContentNode(paragraphId));
                continue;
            }

            content.Add(new ReaderTextContentNode(
                paragraphId,
                block.IsHeading
                    ? ReaderTextBlockKind.Heading
                    : ReaderTextBlockKind.Paragraph,
                block.Runs
                    .Select(run => new ReaderInlineRun(
                        run.Text,
                        run.Ruby,
                        run.Emphasis,
                        run.Strong))
                    .ToArray(),
                headingLevel: block.IsHeading ? block.HeadingLevel : 0));
        }

        return content;
    }

    private static IReadOnlyList<ReaderContentNode> BuildTranslationContent(
        BookReaderChapter reader,
        ReaderDocumentIdentity translatedIdentity,
        bool supportsSourceMapping)
    {
        var sourceIdentity = translatedIdentity.WithVariant(OriginalVariantKey);
        return reader.TranslatedParagraphs
            .Select((paragraph, index) => (ReaderContentNode)new ReaderTextContentNode(
                $"paragraph:{index}",
                ReaderTextBlockKind.Paragraph,
                paragraph,
                supportsSourceMapping
                    ? new ReaderSourceItemReference(
                        $"paragraph:{index}",
                        sourceIdentity)
                    : null))
            .ToArray();
    }
}
