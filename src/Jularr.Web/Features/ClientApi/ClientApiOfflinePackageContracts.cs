namespace Jularr.Web.Features.ClientApi;

/// <summary>
/// Stable wire vocabulary for the cross-media device-local Offline package flow.
/// Canonical MediaCore works and canonical Games identities stay separate; this contract only
/// describes what the client wants to keep on its own device.
/// </summary>
public static class ClientApiOfflinePackageContract
{
    public const string WorkTarget = "work";
    public const string GameTarget = "game";

    public const string WatchIntent = "watch";
    public const string ReadIntent = "read";
    public const string ListenIntent = "listen";
    public const string PlayIntent = "play";

    public const string VideoCategory = "video";
    public const string ReadingCategory = "reading";
    public const string AudioCategory = "audio";
    public const string GameCategory = "game";

    public const string SingleScope = "single";
    public const string SelectedScope = "selected";
    public const string SeasonScope = "season";
    public const string VolumeScope = "volume";
    public const string WorkScope = "work";
    public const string ReleaseScope = "release";

    public const string KnownEstimate = "known";
    public const string ApproximateEstimate = "approximate";
    public const string UnknownEstimate = "unknown";

    public static bool TryParseIntent(string? value, out ClientOfflinePackageIntent intent)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case WatchIntent:
                intent = ClientOfflinePackageIntent.Watch;
                return true;
            case ReadIntent:
                intent = ClientOfflinePackageIntent.Read;
                return true;
            case ListenIntent:
                intent = ClientOfflinePackageIntent.Listen;
                return true;
            case PlayIntent:
                intent = ClientOfflinePackageIntent.Play;
                return true;
            default:
                intent = default;
                return false;
        }
    }

    public static string IntentName(ClientOfflinePackageIntent intent) =>
        intent switch
        {
            ClientOfflinePackageIntent.Watch => WatchIntent,
            ClientOfflinePackageIntent.Read => ReadIntent,
            ClientOfflinePackageIntent.Listen => ListenIntent,
            ClientOfflinePackageIntent.Play => PlayIntent,
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null)
        };

    public static bool TryValidateTarget(ClientOfflinePackageTarget? target, out string message)
    {
        if (target is null)
        {
            message = "An offline package target is required.";
            return false;
        }

        var kind = target.Kind?.Trim().ToLowerInvariant();
        if (kind == WorkTarget)
        {
            if (target.WorkId is null || target.WorkId == Guid.Empty)
            {
                message = "A work target requires workId.";
                return false;
            }

            if (target.GameId is not null || target.GameReleaseId is not null)
            {
                message = "A work target cannot contain Game identifiers.";
                return false;
            }

            if (target.WorkEpisodeId == Guid.Empty || target.WorkChapterId == Guid.Empty || target.EditionId == Guid.Empty)
            {
                message = "Offline target identifiers cannot be empty GUIDs.";
                return false;
            }

            if (target.WorkEpisodeId is not null && target.WorkChapterId is not null)
            {
                message = "A work target cannot select an episode and a chapter at the same time.";
                return false;
            }

            message = "";
            return true;
        }

        if (kind == GameTarget)
        {
            if (target.GameId is null || target.GameId == Guid.Empty)
            {
                message = "A Game target requires gameId.";
                return false;
            }

            if (target.WorkId is not null || target.WorkEpisodeId is not null || target.WorkChapterId is not null || target.EditionId is not null)
            {
                message = "A Game target cannot contain MediaCore Work identifiers.";
                return false;
            }

            if (target.GameReleaseId == Guid.Empty)
            {
                message = "Offline target identifiers cannot be empty GUIDs.";
                return false;
            }

            message = "";
            return true;
        }

        message = "Offline target kind must be work or game.";
        return false;
    }
}

public static class ClientApiOfflinePackageRoutes
{
    public const string OptionsPath = "/offline/packages/options";
    public const string PreviewPath = "/offline/packages/preview";

    public static string Options => $"{ClientApiContract.BasePath}{OptionsPath}";
    public static string Preview => $"{ClientApiContract.BasePath}{PreviewPath}";
}

public enum ClientOfflinePackageIntent
{
    Watch,
    Read,
    Listen,
    Play
}

/// <summary>
/// Canonical target of a device-local package. Work and Game identities are intentionally a
/// discriminated union because Games does not belong to MediaCore.
/// </summary>
public sealed record ClientOfflinePackageTarget(
    string? Kind,
    Guid? WorkId = null,
    Guid? WorkEpisodeId = null,
    Guid? WorkChapterId = null,
    Guid? EditionId = null,
    Guid? GameId = null,
    Guid? GameReleaseId = null);

public sealed record ClientOfflinePackageOptionsRequest(
    ClientOfflinePackageTarget? Target,
    string? Intent);

public sealed record ClientOfflinePackagePreviewRequest(
    ClientOfflinePackageTarget? Target,
    string? Intent,
    ClientOfflinePackageSelection? Selection = null);

/// <summary>
/// Exact user choice that a later prepare command will validate again. Product keys such as quality
/// are server-defined capabilities, never arbitrary codec/container/FFmpeg settings.
/// </summary>
public sealed record ClientOfflinePackageSelection(
    string? Scope = null,
    IReadOnlyList<Guid>? UnitIds = null,
    Guid? EditionId = null,
    string? Quality = null,
    string? ImageQuality = null,
    IReadOnlyList<string>? AudioTrackIds = null,
    IReadOnlyList<string>? SubtitleTrackIds = null,
    bool IncludeLearning = false);

public sealed record ClientOfflinePackageRequest(
    ClientOfflinePackageTarget? Target,
    string? Intent,
    ClientOfflinePackageSelection? Selection);

public sealed record ClientOfflinePackageOptions(
    int ApiVersion,
    ClientOfflinePackageTarget Target,
    string Intent,
    string Category,
    string Title,
    bool Downloadable,
    string? UnavailableReason,
    IReadOnlyList<ClientOfflineScopeOption> Scopes,
    IReadOnlyList<ClientOfflineUnitOption> Units,
    IReadOnlyList<ClientOfflineEditionOption> Editions,
    IReadOnlyList<ClientOfflineQualityOption> Qualities,
    IReadOnlyList<ClientOfflineQualityOption> ImageQualities,
    IReadOnlyList<ClientOfflineTrackOption> AudioTracks,
    IReadOnlyList<ClientOfflineTrackOption> SubtitleTracks,
    bool LearningAvailable,
    ClientOfflinePackageEstimate Estimate);

public sealed record ClientOfflineScopeOption(
    string Key,
    bool IsDefault);

public sealed record ClientOfflineUnitOption(
    Guid Id,
    string Kind,
    string Title,
    double? Number = null,
    Guid? GroupId = null,
    string? GroupTitle = null);

public sealed record ClientOfflineEditionOption(
    Guid Id,
    string Language,
    string? Format,
    string? Title,
    bool IsPrimary);

public sealed record ClientOfflineQualityOption(
    string Key,
    bool IsDefault);

public sealed record ClientOfflineTrackOption(
    string Id,
    string Kind,
    string? Language,
    string? Title,
    bool IsDefault,
    bool IsForced);

public sealed record ClientOfflinePackageEstimate(
    string Status,
    long? Bytes);

public sealed record ClientOfflinePackagePreview(
    int ApiVersion,
    ClientOfflinePackageTarget Target,
    string Intent,
    string Category,
    ClientOfflinePackageSelection Selection,
    ClientOfflinePackageEstimate Estimate);
