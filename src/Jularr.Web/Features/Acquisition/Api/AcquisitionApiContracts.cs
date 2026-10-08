using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Operations;
using Microsoft.AspNetCore.Http;

namespace Jularr.Web.Features.Acquisition.Api;

/// <summary>Route helpers for the acquisition automation API, mirroring <c>ClientApiRoutes</c>.</summary>
public static class AcquisitionApiRoutes
{
    public const string BasePath = "/api/acquisition/v1";

    public static bool IsAcquisitionApi(PathString path) =>
        path.StartsWithSegments(BasePath, out _);
}

public sealed record MonitoredAnimeResponse(
    Guid AnimeId,
    string AnimeKey,
    string Title,
    string Mode,
    string QualityProfileId,
    int WantedEpisodes,
    int[] IndexerIds)
{
    public static MonitoredAnimeResponse From(AnimeMonitoredRow row) =>
        new(row.AnimeId, row.AnimeKey, row.Title, row.Mode.ToString(), row.ProfileId, row.WantedCount, row.IndexerIds);
}

public sealed record AnimeMonitoringResponse(
    Guid AnimeId,
    string AnimeKey,
    string Mode,
    bool CanAcquire,
    bool Monitored,
    bool SearchOnAdd,
    string QualityProfileId,
    IReadOnlyList<string> AvailableQualityProfileIds,
    int[] IndexerIds,
    Guid? TargetRootId,
    int WantedEpisodes,
    int ActiveDownloads,
    string? LastEvent)
{
    public static AnimeMonitoringResponse From(AnimeAcquisitionPanel panel) =>
        new(
            panel.AnimeId,
            panel.AnimeKey,
            panel.Mode.ToString(),
            panel.CanAcquire,
            panel.Monitored,
            panel.Settings?.SearchOnAdd ?? false,
            panel.ProfileId,
            panel.Profiles.Select(profile => profile.Id).ToArray(),
            panel.Settings?.IndexerIds ?? [],
            panel.Settings?.TargetRootId,
            panel.WantedCount,
            panel.ActiveDownloads,
            panel.LastEvent);
}

/// <summary>
/// Sets per-anime monitoring. Every field is authoritative: omitted array fields clear the
/// existing value, matching the owner settings form this endpoint shares its service call with.
/// </summary>
public sealed record SetAnimeMonitoringRequest(
    bool Monitored,
    bool SearchOnAdd,
    string? QualityProfileId,
    int[]? IndexerIds,
    Guid? TargetRootId);

public sealed record WantedEpisodeResponse(
    Guid? AnimeId,
    string AnimeKey,
    string Title,
    int SeasonNumber,
    int EpisodeNumber,
    int? AbsoluteEpisodeNumber,
    string Reason,
    DateTimeOffset BecameWantedAtUtc,
    string? AttemptStatus)
{
    public static WantedEpisodeResponse From(AnimeWantedRow row) =>
        new(
            row.AnimeId,
            row.Key.AnimeKey,
            row.Title,
            row.Key.SeasonNumber,
            row.Key.EpisodeNumber,
            row.Key.AbsoluteEpisodeNumber,
            row.Reason.ToString(),
            row.SinceUtc,
            row.Attempt?.Status.ToString());
}

public sealed record SearchQueuedResponse(Guid OperationId, string Message);

public sealed record OperationResponse(
    Guid Id,
    string Kind,
    string Category,
    string Status,
    string Title,
    string? Subject,
    int? ProgressPercent,
    string? Message,
    string? Error,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc)
{
    public static OperationResponse From(OperationSnapshot snapshot) =>
        new(
            snapshot.Id,
            snapshot.Kind,
            snapshot.Category,
            snapshot.Status.ToString(),
            snapshot.Title,
            snapshot.Subject,
            snapshot.ProgressPercent,
            snapshot.Message,
            snapshot.Error,
            snapshot.CreatedAtUtc,
            snapshot.StartedAtUtc,
            snapshot.FinishedAtUtc);
}

public sealed record OperationLogEntryResponse(
    long Id,
    DateTime CreatedAtUtc,
    string Level,
    string Module,
    string Message)
{
    public static OperationLogEntryResponse From(OperationLogEntry entry) =>
        new(entry.Id, entry.CreatedAtUtc, entry.Level.ToString(), entry.Module, entry.Message);
}

public sealed record OperationDetailResponse(
    OperationResponse Operation,
    IReadOnlyList<OperationLogEntryResponse> Logs);

public sealed record AcquisitionHistoryEntryResponse(
    Guid AnimeId,
    int SeasonNumber,
    int EpisodeNumber,
    int? AbsoluteEpisodeNumber,
    string EventKind,
    string? ReleaseTitle,
    int? Score,
    string? QualityKey,
    string? Indexer,
    string Reason,
    DateTime OccurredAtUtc)
{
    public static AcquisitionHistoryEntryResponse From(AcquisitionHistoryEntry entry) =>
        new(
            entry.AnimeId,
            entry.SeasonNumber,
            entry.EpisodeNumber,
            entry.AbsoluteEpisodeNumber,
            entry.EventKind.ToString(),
            entry.ReleaseTitle,
            entry.Score,
            entry.QualityKey,
            entry.Indexer,
            entry.Reason,
            entry.OccurredAtUtc);
}

public sealed record ManualImportFileResponse(
    string SourcePath,
    long SizeBytes,
    string Status,
    double Confidence,
    string[] Reasons,
    string? ImportedPath,
    string? Error)
{
    public static ManualImportFileResponse From(AnimeImportFileRecord record) =>
        new(record.SourcePath, record.SizeBytes, record.Status.ToString(), record.Confidence, record.Reasons, record.ImportedPath, record.Error);
}

public sealed record ManualImportItemResponse(
    Guid Id,
    string AnimeKey,
    string AnimeTitle,
    string Status,
    bool NeedsAttention,
    string? DownloadPath,
    string? Message,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ManualImportFileResponse> Files)
{
    public static ManualImportItemResponse From(AnimeImportRecord record) =>
        new(
            record.Id,
            record.AnimeKey,
            record.AnimeTitle,
            record.Status.ToString(),
            record.NeedsAttention,
            record.DownloadPath,
            record.Message,
            record.UpdatedAtUtc,
            record.Files.Select(ManualImportFileResponse.From).ToArray());
}

public sealed record ResolveManualImportRequest(string SourcePath, int Season, int Episode);

public sealed record AcquisitionActionResponse(bool Success, string Message);

public sealed record AcquisitionHealthEntryResponse(
    Guid Id,
    string Name,
    bool Enabled,
    bool? Healthy,
    string? LastError,
    DateTimeOffset? LastCheckedUtc);

public sealed record AcquisitionHealthSummaryResponse(
    IReadOnlyList<AcquisitionHealthEntryResponse> Indexers,
    IReadOnlyList<AcquisitionHealthEntryResponse> DownloadClients);

/// <summary>
/// Thrown by <see cref="AcquisitionApiService"/> for a request the canonical service path refused
/// (Sonarr ownership, not found, validation). <see cref="StatusCode"/> maps directly onto
/// <c>Results.Problem</c> so every acquisition API endpoint returns the same problem-details shape.
/// </summary>
public sealed class AcquisitionApiException(int statusCode, string title, string detail)
    : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string Detail { get; } = detail;

    public static AcquisitionApiException NotFound(string detail) =>
        new(StatusCodes.Status404NotFound, "Not found.", detail);

    public static AcquisitionApiException Conflict(string detail) =>
        new(StatusCodes.Status409Conflict, "Refused.", detail);

    public static AcquisitionApiException BadRequest(string detail) =>
        new(StatusCodes.Status400BadRequest, "Invalid request.", detail);

    public static AcquisitionApiException SonarrOwned(string animeKey, AnimeManagementMode mode) =>
        Conflict(
            $"'{animeKey}' is in {mode} mode; Sonarr owns this anime, so Jularr will not search, " +
            "grab or change its acquisition settings. Choose parallel acquisition or Jularr-managed " +
            "under Sonarr migration first.");
}
