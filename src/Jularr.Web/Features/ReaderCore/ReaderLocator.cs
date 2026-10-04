namespace Jularr.Web.Features.ReaderCore;

/// <summary>
/// Optional viewport within a page/image. X/Y are normalized 0..1 coordinates;
/// Zoom is a positive presentation scale and is never a replacement for page identity.
/// </summary>
public sealed record ReaderViewportPosition
{
    public ReaderViewportPosition(double x, double y, double zoom)
    {
        if (x is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (y is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        if (zoom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom));
        }

        X = x;
        Y = y;
        Zoom = zoom;
    }

    public double X { get; }

    public double Y { get; }

    public double Zoom { get; }
}

/// <summary>
/// Layout-independent durable Reader location. FallbackPercent is only a recovery
/// fallback when the exact typed anchor can no longer be resolved.
/// </summary>
public abstract record ReaderLocator
{
    protected ReaderLocator(ReaderDocumentIdentity document, double? fallbackPercent)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (fallbackPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackPercent));
        }

        Document = document;
        FallbackPercent = fallbackPercent;
    }

    public ReaderDocumentIdentity Document { get; }

    public double? FallbackPercent { get; }
}

public sealed record ReflowTextReaderLocator : ReaderLocator
{
    public ReflowTextReaderLocator(
        ReaderDocumentIdentity document,
        string blockId,
        int characterOffset = 0,
        ReaderSourceReference? source = null,
        double? fallbackPercent = null) : base(document, fallbackPercent)
    {
        if (string.IsNullOrWhiteSpace(blockId))
        {
            throw new ArgumentException("Reflow Reader location requires a stable block id.", nameof(blockId));
        }

        if (characterOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterOffset));
        }

        BlockId = blockId.Trim();
        CharacterOffset = characterOffset;
        Source = source;
    }

    public string BlockId { get; }

    public int CharacterOffset { get; }

    public ReaderSourceReference? Source { get; }
}

public sealed record ImageSequenceReaderLocator : ReaderLocator
{
    public ImageSequenceReaderLocator(ReaderDocumentIdentity document, int pageIndex, ReaderViewportPosition? viewport = null, double? fallbackPercent = null) : base(document, fallbackPercent)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        PageIndex = pageIndex;
        Viewport = viewport;
    }

    public int PageIndex { get; }

    public ReaderViewportPosition? Viewport { get; }
}

public sealed record FixedPageReaderLocator : ReaderLocator
{
    public FixedPageReaderLocator(
        ReaderDocumentIdentity document,
        int logicalPageIndex,
        int sourcePageIndex,
        ReaderSourceRegion? sourceRegion = null,
        ReaderViewportPosition? viewport = null,
        double? fallbackPercent = null) : base(document, fallbackPercent)
    {
        if (logicalPageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalPageIndex));
        }

        if (sourcePageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourcePageIndex));
        }

        LogicalPageIndex = logicalPageIndex;
        SourcePageIndex = sourcePageIndex;
        SourceRegion = sourceRegion;
        Viewport = viewport;
    }

    public int LogicalPageIndex { get; }

    public int SourcePageIndex { get; }

    public ReaderSourceRegion? SourceRegion { get; }

    public ReaderViewportPosition? Viewport { get; }
}
