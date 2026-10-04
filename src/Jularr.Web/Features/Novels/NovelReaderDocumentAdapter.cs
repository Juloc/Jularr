using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Features.ReaderPreferences;

namespace Jularr.Web.Features.Novels;

/// <summary>
/// Converts the current Novel/Light-Novel reader projection into the canonical
/// renderer-facing Reader document without leaking Razor routes into the engine.
/// </summary>
public static class NovelReaderDocumentAdapter
{
    public const string OriginalVariantKey = "original";
    public const string LegacyGermanVariantKey = "legacy-ai:de";
    public const string TranslateGemmaGermanVariantKey = "translate-gemma:de";

    public static ReaderDocument Create(
        NovelReaderChapter chapter,
        ReaderDocumentIdentity identity,
        string sourceLanguage = NovelReadingLanguage.Japanese,
        string selectedVariantKey = OriginalVariantKey)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(identity);

        var variants = BuildVariants(chapter, sourceLanguage);
        var selected = variants.FirstOrDefault(variant =>
            string.Equals(variant.Key, selectedVariantKey, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"Reader variant '{selectedVariantKey}' is not available for this chapter.",
                nameof(selectedVariantKey));

        var contentType = ReaderContentTypes.FromNovelMetadata(
            chapter.WorkFormat,
            chapter.SourceProvider);
        var descriptor = ReaderDocumentDescriptor.Create(
            identity.WorkId,
            contentType,
            chapter.WorkTitle,
            ReaderPreferenceRules.ParseGenres(chapter.GenresJson),
            languages: variants
                .Select(variant => new ReaderDocumentLanguage(
                    variant.LanguageTag,
                    variant.LanguageTag))
                .ToArray());

        var documentIdentity = identity.WithVariant(selected.Key);
        var content = selected.Key switch
        {
            OriginalVariantKey => BuildOriginalContent(chapter),
            LegacyGermanVariantKey => BuildTranslatedContent(
                chapter.OriginalText,
                chapter.TranslationText!,
                documentIdentity,
                selected.SupportsSourceMapping),
            TranslateGemmaGermanVariantKey => BuildTranslatedContent(
                chapter.OriginalText,
                chapter.TranslateGemmaTranslationText!,
                documentIdentity,
                selected.SupportsSourceMapping),
            _ => throw new InvalidOperationException("Unknown Reader variant.")
        };

        return ReaderDocument.Create(
            descriptor,
            documentIdentity,
            content,
            sourceLanguage,
            selected,
            variants);
    }

    private static IReadOnlyList<ReaderDocumentVariant> BuildVariants(
        NovelReaderChapter chapter,
        string sourceLanguage)
    {
        var variants = new List<ReaderDocumentVariant>
        {
            new(
                OriginalVariantKey,
                sourceLanguage,
                ReaderVariantKind.Original,
                supportsSourceMapping: true,
                providerId: chapter.SourceProvider)
        };

        if (chapter.HasTranslation)
        {
            variants.Add(new ReaderDocumentVariant(
                LegacyGermanVariantKey,
                NovelReadingLanguage.German,
                ReaderVariantKind.LegacyTranslation,
                ParagraphsAlign(chapter.OriginalText, chapter.TranslationText!),
                providerId: "legacy-ai"));
        }

        if (chapter.HasTranslateGemmaTranslation)
        {
            variants.Add(new ReaderDocumentVariant(
                TranslateGemmaGermanVariantKey,
                NovelReadingLanguage.German,
                ReaderVariantKind.GeneratedTranslation,
                ParagraphsAlign(chapter.OriginalText, chapter.TranslateGemmaTranslationText!),
                providerId: "translate-gemma"));
        }

        return variants;
    }

    private static IReadOnlyList<ReaderContentNode> BuildOriginalContent(
        NovelReaderChapter chapter)
    {
        var blocks = NovelChapterDocument.BuildReaderBlocks(
            chapter.OriginalText,
            chapter.ContentJson);
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

            var kind = block.IsHeading
                ? ReaderTextBlockKind.Heading
                : ReaderTextBlockKind.Paragraph;
            content.Add(new ReaderTextContentNode(
                paragraphId,
                kind,
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

    private static IReadOnlyList<ReaderContentNode> BuildTranslatedContent(
        string sourceText,
        string translatedText,
        ReaderDocumentIdentity translatedIdentity,
        bool supportsSourceMapping)
    {
        var sourceParagraphs = NovelTextLayout.SplitParagraphs(sourceText);
        var translatedParagraphs = NovelTextLayout.SplitParagraphs(translatedText);
        var sourceIdentity = translatedIdentity.WithVariant(OriginalVariantKey);

        return translatedParagraphs
            .Select((paragraph, index) => new ReaderTextContentNode(
                $"paragraph:{index}",
                ReaderTextBlockKind.Paragraph,
                paragraph,
                supportsSourceMapping && index < sourceParagraphs.Count
                    ? new ReaderSourceItemReference(
                        $"paragraph:{index}",
                        sourceIdentity)
                    : null))
            .Cast<ReaderContentNode>()
            .ToArray();
    }

    private static bool ParagraphsAlign(string sourceText, string translatedText) =>
        NovelTextLayout.SplitParagraphs(sourceText).Count
        == NovelTextLayout.SplitParagraphs(translatedText).Count;
}
