using Jularr.Web.Features.Acquisition.Access;

namespace Jularr.Web.Features.Acquisition.Indexers;

public enum IndexerReadinessLevel
{
    /// <summary>Added before setup verified indexers, or never checked.</summary>
    NotChecked,

    /// <summary>Connection and key work and a real search was answered.</summary>
    Ready,

    /// <summary>Something found by the last check or refresh needs the owner.</summary>
    NeedsAttention
}

/// <summary>What is known about searching one media type on one indexer.</summary>
public enum IndexerKindState
{
    /// <summary>A search in the type's categories was answered.</summary>
    Verified,

    /// <summary>Categories are known, but no search in them was run yet.</summary>
    Unverified,

    /// <summary>The type has no category of its own and uses the Books categories; the owner should verify it.</summary>
    Shared,

    /// <summary>The indexer offers no category for this media type; it is never searched there unless the owner chooses categories.</summary>
    Unavailable,

    /// <summary>A search in the type's categories failed, or the indexer's plain search does not work.</summary>
    Failed
}

public sealed record IndexerKindReadiness(MediaAcquisitionKind Kind, IndexerKindCategories Categories, IndexerKindState State, string? Message);

/// <summary>The one reading of an entry's stored verification, shared by the Usenet page and the setup result, so no page invents its own notion of "working".</summary>
public static class IndexerReadiness
{
    public static IndexerReadinessLevel Level(IndexerEntry entry)
    {
        if (entry.Type != IndexerType.Newznab || entry.Settings.Verification is not { } verification)
        {
            return IndexerReadinessLevel.NotChecked;
        }

        return verification is { Connected: true, Authenticated: true, RefreshError: null } && verification.Answered(IndexerSearchMode.Search)
            ? IndexerReadinessLevel.Ready
            : IndexerReadinessLevel.NeedsAttention;
    }

    public static IndexerKindReadiness ForKind(IndexerEntry entry, MediaAcquisitionKind kind)
    {
        var categories = IndexerCategoryMapper.Resolve(kind, entry);
        if (!categories.IsAvailable)
        {
            return new IndexerKindReadiness(kind, categories, IndexerKindState.Unavailable, null);
        }

        var verification = entry.Settings.Verification;
        if (verification is not null && !verification.Answered(IndexerSearchMode.Search))
        {
            return new IndexerKindReadiness(kind, categories, IndexerKindState.Failed, verification.Searches.GetValueOrDefault(IndexerSearchMode.Search)?.Message);
        }

        if (verification?.KindChecks?.GetValueOrDefault(kind) is { } check)
        {
            return check.State is IndexerCheckState.Valid or IndexerCheckState.NoResults
                ? new IndexerKindReadiness(kind, categories, categories.Evidence == IndexerCategoryEvidence.Shared ? IndexerKindState.Shared : IndexerKindState.Verified, null)
                : new IndexerKindReadiness(kind, categories, IndexerKindState.Failed, check.Message);
        }

        return new IndexerKindReadiness(kind, categories, categories.Evidence == IndexerCategoryEvidence.Shared ? IndexerKindState.Shared : IndexerKindState.Unverified, null);
    }
}
