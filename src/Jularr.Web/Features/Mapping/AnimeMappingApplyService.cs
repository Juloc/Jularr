using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Tracking;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Mapping;

/// <summary>Result of a preview/apply request: the computed preview and, for apply, whether it committed.</summary>
public sealed record AnimeMappingApplyResult(
    bool Applied,
    AnimeMappingPreview Preview,
    string? Error = null);

/// <summary>
/// Owner-facing apply workflow for anime provider range mappings (issue #525). It sits on top of the
/// canonical episode-mapping store (<see cref="AniListAccountStore"/>) that
/// <c>AnimeMetadataService.ResolveEpisodeAsync</c> already consumes, so there is no second source of
/// truth for a mapping. Applying replaces a work's provider ranges wholesale and records an audit
/// entry.
///
/// Safe remap: provider mappings are keyed by <c>AnimeId</c> + local episode range; watch progress
/// (canonical <c>MediaProgress</c>) is keyed by the canonical work episode. Changing a mapping only rewrites
/// provider coordinates and never touches progress rows.
/// </summary>
public sealed class AnimeMappingApplyService(
    AppDbContext db,
    AniListAccountStore mappingStore,
    MappingAuditStore auditStore)
{
    /// <summary>Local episodes of a work, ordered, as the side-by-side view and planner see them.</summary>
    public async Task<IReadOnlyList<LocalEpisodeRef>> LoadLocalEpisodesAsync(
        Guid animeId,
        CancellationToken cancellationToken) =>
        await db.Episodes
            .AsNoTracking()
            .Where(x => x.AnimeId == animeId)
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.Number)
            .Select(x => new LocalEpisodeRef(x.SeasonNumber, x.Number))
            .ToListAsync(cancellationToken);

    /// <summary>The work's currently applied provider ranges, as planner ranges.</summary>
    public async Task<IReadOnlyList<AnimeMappingRange>> LoadAppliedRangesAsync(
        Guid animeId,
        CancellationToken cancellationToken) =>
        (await mappingStore.LoadEpisodeMappingsAsync(animeId, cancellationToken))
            .Select(ToRange)
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.LocalStart)
            .ToArray();

    /// <summary>Preview applying <paramref name="desiredRanges"/> without committing anything.</summary>
    public async Task<AnimeMappingPreview> BuildPreviewAsync(
        Guid animeId,
        IReadOnlyList<AnimeMappingRange> desiredRanges,
        CancellationToken cancellationToken)
    {
        var localEpisodes = await LoadLocalEpisodesAsync(animeId, cancellationToken);
        return AnimeMappingPlanner.BuildPreview(localEpisodes, desiredRanges);
    }

    /// <summary>
    /// Applies a proposed set of ranges: previews first and refuses to commit when the preview has
    /// conflicts or invalid ranges. On success it replaces the work's provider ranges with the mapped
    /// (non-unmapped) ranges and writes an audit entry. Explicitly-unmapped ranges are recorded in the
    /// audit but intentionally leave no mapping row (absence is what "unmapped" means downstream).
    /// </summary>
    public async Task<AnimeMappingApplyResult> ApplyAsync(
        Guid animeId,
        IReadOnlyList<AnimeMappingRange> desiredRanges,
        string actor,
        CancellationToken cancellationToken)
    {
        var localEpisodes = await LoadLocalEpisodesAsync(animeId, cancellationToken);
        var preview = AnimeMappingPlanner.BuildPreview(localEpisodes, desiredRanges);
        if (!preview.CanApply)
        {
            return new AnimeMappingApplyResult(false, preview, "The proposed mapping has conflicts and was not applied.");
        }

        await ReplaceRangesAsync(animeId, desiredRanges, cancellationToken);

        var mapped = desiredRanges.Where(x => !x.Unmapped).ToArray();
        var unmapped = desiredRanges.Where(x => x.Unmapped).ToArray();
        await auditStore.AppendAsync(
            animeId,
            MappingAuditStore.ActionApply,
            $"Applied {mapped.Length} range mapping(s)"
                + (unmapped.Length > 0 ? $", {unmapped.Length} left unmapped" : "")
                + ".",
            DescribeRanges(desiredRanges),
            actor,
            cancellationToken);

        return new AnimeMappingApplyResult(true, preview);
    }

    /// <summary>Clears every provider range for a work and records it as an explicit "unmapped" action.</summary>
    public async Task MarkUnmappedAsync(
        Guid animeId,
        string actor,
        CancellationToken cancellationToken)
    {
        await ReplaceRangesAsync(animeId, [], cancellationToken);
        await auditStore.AppendAsync(
            animeId,
            MappingAuditStore.ActionUnmapped,
            "Marked the work explicitly unmapped; cleared all provider ranges.",
            "",
            actor,
            cancellationToken);
    }

    private async Task ReplaceRangesAsync(
        Guid animeId,
        IReadOnlyList<AnimeMappingRange> desiredRanges,
        CancellationToken cancellationToken)
    {
        var existing = await mappingStore.LoadEpisodeMappingsAsync(animeId, cancellationToken);
        foreach (var mapping in existing)
        {
            await mappingStore.RemoveEpisodeMappingAsync(animeId, mapping.Id, cancellationToken);
        }

        foreach (var range in desiredRanges.Where(x => !x.Unmapped))
        {
            var added = await mappingStore.TryAddEpisodeMappingAsync(
                new AnimeEpisodeMetadataMapping(
                    Guid.NewGuid(),
                    animeId,
                    range.SeasonNumber,
                    range.LocalStart,
                    range.LocalEnd,
                    range.RemoteStart,
                    MappingProviders.Normalize(range.Provider),
                    range.ExternalId.Trim(),
                    string.IsNullOrWhiteSpace(range.PreferredTitle) ? range.ExternalId.Trim() : range.PreferredTitle!.Trim(),
                    range.RemoteEpisodeCount,
                    DateTimeOffset.UtcNow),
                cancellationToken);

            if (!added)
            {
                throw new InvalidOperationException(
                    "A validated range could not be stored; it overlapped an existing mapping.");
            }
        }
    }

    private static AnimeMappingRange ToRange(AnimeEpisodeMetadataMapping mapping) =>
        new(
            mapping.SeasonNumber,
            mapping.LocalEpisodeStart,
            mapping.LocalEpisodeEnd,
            mapping.Provider,
            mapping.ExternalId,
            mapping.RemoteEpisodeStart,
            mapping.PreferredTitle,
            mapping.EpisodeCount);

    private static string DescribeRanges(IReadOnlyList<AnimeMappingRange> ranges)
    {
        var builder = new StringBuilder();
        foreach (var range in ranges.OrderBy(x => x.SeasonNumber).ThenBy(x => x.LocalStart))
        {
            builder.Append($"S{range.SeasonNumber:00}E{range.LocalStart:00}-{range.LocalEnd:00} -> ");
            builder.AppendLine(range.Unmapped
                ? "unmapped"
                : $"{MappingProviders.Normalize(range.Provider)}:{range.ExternalId.Trim()} ep {range.RemoteStart}");
        }

        return builder.ToString().TrimEnd();
    }
}
