using Jularr.Web.Features.Operations;

namespace Jularr.Web.Features.Library;

/// <summary>
/// The one owner of forcing a single stored file through technical analysis again as a visible, non-retryable operation. The caller
/// (an Admin media page) has already checked that the file belongs to the title it shows.
/// </summary>
public sealed class MediaFileReanalysisService(MediaInventoryService inventory, OperationRunner operations)
{
    /// <summary>The status of the analysis the file has afterwards, or null when the operation produced none.</summary>
    public async Task<MediaAnalysisStatus?> ReanalyzeAsync(Guid fileId, OperationDescriptor descriptor, CancellationToken cancellationToken)
    {
        var entry = await operations.RunAsync(
            descriptor with { Lane = OperationLane.Normal, Retryable = false },
            async (_, token) =>
            {
                await inventory.InvalidateAsync([fileId], token);
                return await inventory.EnsureAnalyzedAsync(fileId, token);
            },
            null,
            cancellationToken);
        return entry?.Status;
    }
}
