namespace Jularr.Web.Features.ReaderCore;

public enum ReaderPageDirection
{
    Auto,
    LeftToRight,
    RightToLeft
}

public enum ReaderTextDirection
{
    Auto,
    LeftToRight,
    RightToLeft
}

public enum ReaderWritingMode
{
    HorizontalTb,
    VerticalRl,
    VerticalLr
}

public enum ReaderTextBlockKind
{
    Paragraph,
    Heading,
    Quote,
    Preformatted,
    List,
    Table,
    Footnote,
    Formula
}

public enum ReaderVariantKind
{
    Original,
    OfficialTranslation,
    GeneratedTranslation,
    LegacyTranslation
}

public sealed record ReaderDocumentVariant
{
    public ReaderDocumentVariant(
        string key,
        string languageTag,
        ReaderVariantKind kind,
        bool supportsSourceMapping,
        string? providerId = null,
        string? version = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Reader variant requires a stable key.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(languageTag))
        {
            throw new ArgumentException("Reader variant requires a language tag.", nameof(languageTag));
        }

        Key = key.Trim();
        LanguageTag = languageTag.Trim().Replace('_', '-').ToLowerInvariant();
        Kind = kind;
        SupportsSourceMapping = supportsSourceMapping;
        ProviderId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
    }

    public string Key { get; }

    public string LanguageTag { get; }

    public ReaderVariantKind Kind { get; }

    public bool SupportsSourceMapping { get; }

    public string? ProviderId { get; }

    public string? Version { get; }
}


/// <summary>
/// Canonical identity of the readable document variant. Layout and visual page
/// numbers are deliberately excluded so relayout does not create a new identity.
/// </summary>
public sealed record ReaderDocumentIdentity
{
    public ReaderDocumentIdentity(Guid workId, Guid? volumeId = null, Guid? chapterId = null, Guid? editionId = null, Guid? versionId = null, Guid? assetId = null, Guid? fileId = null, string? variantKey = null)
    {
        if (workId == Guid.Empty)
        {
            throw new ArgumentException("Reader document identity requires a Work.", nameof(workId));
        }

        WorkId = workId;
        VolumeId = volumeId;
        ChapterId = chapterId;
        EditionId = editionId;
        VersionId = versionId;
        AssetId = assetId;
        FileId = fileId;
        VariantKey = string.IsNullOrWhiteSpace(variantKey) ? null : variantKey.Trim();
    }

    public Guid WorkId { get; }

    public Guid? VolumeId { get; }

    public Guid? ChapterId { get; }

    public Guid? EditionId { get; }

    public Guid? VersionId { get; }

    public Guid? AssetId { get; }

    public Guid? FileId { get; }

    public string? VariantKey { get; }
}

/// <summary>
/// Normalized region on a physical/source page. Coordinates are fractions in
/// the inclusive 0..1 page coordinate space so mappings survive pixel-size changes.
/// </summary>
public sealed record ReaderSourceRegion
{
    public ReaderSourceRegion(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || x is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (!double.IsFinite(y) || y is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        if (!double.IsFinite(width) || width <= 0 || width > 1 || x + width > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (!double.IsFinite(height) || height <= 0 || height > 1 || y + height > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }
}

/// <summary>
/// Mapping from derived/variant Reader content back to its immutable source.
/// Reflow sources use stable item ids; fixed sources use zero-based source pages.
/// </summary>
public abstract record ReaderSourceReference
{
    protected ReaderSourceReference(ReaderDocumentIdentity? sourceDocument)
    {
        SourceDocument = sourceDocument;
    }

    public ReaderDocumentIdentity? SourceDocument { get; }
}

public sealed record ReaderSourceItemReference : ReaderSourceReference
{
    public ReaderSourceItemReference(string sourceItemId, ReaderDocumentIdentity? sourceDocument = null)
        : base(sourceDocument)
    {
        if (string.IsNullOrWhiteSpace(sourceItemId))
        {
            throw new ArgumentException("Source item reference requires a stable id.", nameof(sourceItemId));
        }

        SourceItemId = sourceItemId.Trim();
    }

    public string SourceItemId { get; }
}

public sealed record ReaderSourcePageReference : ReaderSourceReference
{
    public ReaderSourcePageReference(
        int sourcePageIndex,
        ReaderSourceRegion? region = null,
        string? sourceItemId = null,
        ReaderDocumentIdentity? sourceDocument = null)
        : base(sourceDocument)
    {
        if (sourcePageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourcePageIndex));
        }

        SourcePageIndex = sourcePageIndex;
        Region = region;
        SourceItemId = string.IsNullOrWhiteSpace(sourceItemId) ? null : sourceItemId.Trim();
    }

    public int SourcePageIndex { get; }

    public ReaderSourceRegion? Region { get; }

    public string? SourceItemId { get; }
}

public abstract record ReaderContentNode
{
    protected ReaderContentNode(string stableId, ReaderSourceReference? source)
    {
        if (string.IsNullOrWhiteSpace(stableId))
        {
            throw new ArgumentException("Reader content requires a stable source-derived id.", nameof(stableId));
        }

        StableId = stableId.Trim();
        Source = source;
    }

    public string StableId { get; }

    public ReaderSourceReference? Source { get; }
}

public sealed record ReaderInlineRun
{
    public ReaderInlineRun(string text, string? ruby = null, bool emphasis = false, bool strong = false)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Ruby = string.IsNullOrWhiteSpace(ruby) ? null : ruby.Trim();
        Emphasis = emphasis;
        Strong = strong;
    }

    public string Text { get; }

    public string? Ruby { get; }

    public bool Emphasis { get; }

    public bool Strong { get; }
}

public sealed record ReaderTextContentNode : ReaderContentNode
{
    public ReaderTextContentNode(string stableId, ReaderTextBlockKind kind, string text, ReaderSourceReference? source = null, int headingLevel = 0)
        : this(stableId, kind, [new ReaderInlineRun(text)], source, headingLevel)
    {
    }

    public ReaderTextContentNode(string stableId, ReaderTextBlockKind kind, IReadOnlyList<ReaderInlineRun> runs, ReaderSourceReference? source = null, int headingLevel = 0)
        : base(stableId, source)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
        {
            throw new ArgumentException("Reader text content requires at least one inline run.", nameof(runs));
        }

        if (kind == ReaderTextBlockKind.Heading)
        {
            if (headingLevel is < 1 or > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(headingLevel));
            }
        }
        else if (headingLevel != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(headingLevel));
        }

        Kind = kind;
        Runs = runs.ToArray();
        Text = string.Concat(Runs.Select(run => run.Text));
        HeadingLevel = headingLevel;
    }

    public ReaderTextBlockKind Kind { get; }

    public IReadOnlyList<ReaderInlineRun> Runs { get; }

    public string Text { get; }

    public int HeadingLevel { get; }
}

public sealed record ReaderImageContentNode : ReaderContentNode
{
    public ReaderImageContentNode(string stableId, string assetKey, string? altText = null, string? caption = null, ReaderSourceReference? source = null) : base(stableId, source)
    {
        if (string.IsNullOrWhiteSpace(assetKey))
        {
            throw new ArgumentException("Reader image content requires an asset key.", nameof(assetKey));
        }

        AssetKey = assetKey.Trim();
        AltText = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim();
        Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
    }

    public string AssetKey { get; }

    public string? AltText { get; }

    public string? Caption { get; }
}

public sealed record ReaderSceneBreakContentNode : ReaderContentNode
{
    public ReaderSceneBreakContentNode(string stableId, ReaderSourceReference? source = null) : base(stableId, source)
    {
    }
}

public sealed record ReaderFixedRegionContentNode : ReaderContentNode
{
    public ReaderFixedRegionContentNode(string stableId, ReaderSourcePageReference source) : base(stableId, source)
    {
    }
}

/// <summary>
/// Renderer-facing readable document. Source adapters build this contract;
/// renderers must not infer document behavior from Razor routes or file extensions.
/// </summary>
public sealed record ReaderDocument
{
    private ReaderDocument(
        ReaderDocumentDescriptor descriptor,
        ReaderDocumentIdentity identity,
        string sourceLanguage,
        ReaderDocumentVariant selectedVariant,
        IReadOnlyList<ReaderDocumentVariant> availableVariants,
        IReadOnlyList<ReaderContentNode> content)
    {
        Descriptor = descriptor;
        Identity = identity;
        SourceLanguage = sourceLanguage;
        SelectedVariant = selectedVariant;
        AvailableVariants = availableVariants;
        Content = content;
    }

    public ReaderDocumentDescriptor Descriptor { get; }

    public ReaderDocumentIdentity Identity { get; }

    public string SourceLanguage { get; }

    public ReaderDocumentVariant SelectedVariant { get; }

    public IReadOnlyList<ReaderDocumentVariant> AvailableVariants { get; }

    public IReadOnlyList<ReaderContentNode> Content { get; }

    public static ReaderDocument Create(
        ReaderDocumentDescriptor descriptor,
        ReaderDocumentIdentity identity,
        IReadOnlyList<ReaderContentNode>? content = null,
        string sourceLanguage = "und",
        ReaderDocumentVariant? selectedVariant = null,
        IReadOnlyList<ReaderDocumentVariant>? availableVariants = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(identity);

        if (descriptor.WorkId != identity.WorkId)
        {
            throw new ArgumentException("Reader descriptor and document identity must target the same Work.", nameof(identity));
        }

        var normalizedSourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage)
            ? "und"
            : sourceLanguage.Trim().Replace('_', '-').ToLowerInvariant();
        var selected = selectedVariant
            ?? new ReaderDocumentVariant("original", normalizedSourceLanguage, ReaderVariantKind.Original, supportsSourceMapping: true);
        var variants = (availableVariants ?? [selected]).ToArray();
        if (variants.Length == 0
            || variants.Select(variant => variant.Key).Distinct(StringComparer.Ordinal).Count() != variants.Length)
        {
            throw new ArgumentException("Reader variants require unique stable keys.", nameof(availableVariants));
        }

        if (!variants.Any(variant => string.Equals(variant.Key, selected.Key, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Selected Reader variant must be part of the available variants.", nameof(selectedVariant));
        }

        if (identity.VariantKey is not null
            && !string.Equals(identity.VariantKey, selected.Key, StringComparison.Ordinal))
        {
            throw new ArgumentException("Reader document identity variant must match the selected variant.", nameof(identity));
        }

        var nodes = content is null ? Array.Empty<ReaderContentNode>() : content.ToArray();
        var stableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!stableIds.Add(node.StableId))
            {
                throw new ArgumentException($"Reader content id '{node.StableId}' is duplicated.", nameof(content));
            }
        }

        return new ReaderDocument(
            descriptor,
            identity,
            normalizedSourceLanguage,
            selected,
            variants,
            nodes);
    }
}
