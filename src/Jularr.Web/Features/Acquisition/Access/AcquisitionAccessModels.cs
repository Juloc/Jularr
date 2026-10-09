using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// The media types a profile can add to the server. A media type is registered on the shared
/// acquisition spine by its value here plus: a download-client category
/// (<c>DownloadClientSettings.Categories</c>), an <c>ICompletedDownloadImportAdapter</c> behind the
/// completed-download dispatcher, and optionally folders and remote path mappings
/// (<c>MediaLibraryTarget</c>). Everything that is per media type (categories, folders, path
/// mappings) is keyed by this enum and picks up a new value without a new setting.
/// </summary>
public enum MediaAcquisitionKind
{
    Anime,
    Manga,
    LightNovel,
    Book,
    Movie,
    Tv,
    Audiobook,
    Music
}

/// <summary>Who may use the manual add controls (file upload, URL, NZB, inbox import).</summary>
public static class AcquisitionInstanceModules
{
    public static InstanceModule For(MediaAcquisitionKind kind) =>
        kind switch
        {
            MediaAcquisitionKind.Anime => InstanceModule.Anime,
            MediaAcquisitionKind.Manga => InstanceModule.Manga,
            MediaAcquisitionKind.LightNovel => InstanceModule.Novel,
            MediaAcquisitionKind.Book => InstanceModule.Book,
            MediaAcquisitionKind.Movie => InstanceModule.Movie,
            MediaAcquisitionKind.Tv => InstanceModule.Tv,
            MediaAcquisitionKind.Audiobook => InstanceModule.Audiobook,
            MediaAcquisitionKind.Music => InstanceModule.Music,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}

public enum ManualAddMode
{
    OwnerOnly,
    Users
}

public enum AcquisitionRequestStatus
{
    /// <summary>Waiting for the owner.</summary>
    Pending,

    /// <summary>Accepted; waiting for acquisition to start or for the owner to add it by hand.</summary>
    Approved,

    Searching,
    Downloading,
    Importing,
    Completed,
    Rejected,
    Failed
}

/// <summary>
/// The owner's per-media-type rule for the manual add tools. Whether a profile may request or add
/// instantly is not part of it: that is the profile's <see cref="MediaCapability"/> (the capability
/// matrix, #436), the one source <see cref="AcquisitionCapabilities.Resolve"/> reads.
/// </summary>
public sealed record AcquisitionAccessPolicy(
    MediaAcquisitionKind Kind,
    ManualAddMode Manual)
{
    public static AcquisitionAccessPolicy Default(MediaAcquisitionKind kind) =>
        new(kind, ManualAddMode.OwnerOnly);
}

/// <summary>
/// What the current profile may do for one media type — the only thing pages check. Every profile that
/// <see cref="CanRequest"/> sees the same Request action; <see cref="AutoApproves"/> is approval policy
/// only and must never change a label or a button.
/// </summary>
public sealed record AcquisitionCapabilities(
    MediaAcquisitionKind Kind,
    bool CanRequest,
    bool AutoApproves,
    bool CanAddManually,
    bool IsOwner)
{
    /// <summary>What a plain user gets from the built-in defaults: may request, no manual tools. Page models start from it until they resolve the real thing.</summary>
    public static AcquisitionCapabilities Default(MediaAcquisitionKind kind) =>
        Resolve(kind, MediaCapability.Request, AcquisitionAccessPolicy.Default(kind).Manual, isOwner: false);

    /// <summary>
    /// A capability of <see cref="MediaCapability.Request"/> or above may request; <see cref="MediaCapability.Instant"/>
    /// approves that request right away, <see cref="MediaCapability.Request"/> waits for an approver or an
    /// auto-approval rule. Managers of media (the owner and media managers) may always use the manual add tools;
    /// everyone else follows the media type's manual rule.
    /// </summary>
    public static AcquisitionCapabilities Resolve(
        MediaAcquisitionKind kind,
        MediaCapability capability,
        ManualAddMode manual,
        bool isOwner) =>
        new(
            kind,
            CanRequest: capability >= MediaCapability.Request,
            AutoApproves: capability >= MediaCapability.Instant,
            CanAddManually: isOwner || manual == ManualAddMode.Users,
            IsOwner: isOwner);
}

/// <summary>
/// One wish to have a title on the server, from any profile. The same row carries the owner's
/// decision and the acquisition progress, so there is one place to see what was asked for and
/// what happened to it.
/// </summary>
public sealed record AcquisitionRequest(
    Guid Id,
    MediaAcquisitionKind Kind,
    string Provider,
    string ExternalId,
    string Title,
    string? Subtitle,
    string? CoverImageUrl,
    string? PayloadJson,
    string RequestedByProfileId,
    AcquisitionRequestStatus Status,
    string? StatusMessage,
    Guid? OperationId,
    string? ResultUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? DecidedByProfileId,
    DateTime? DecidedAt,
    Guid? WorkId = null)
{
    /// <summary>
    /// Whether the request is bound to its canonical Work: the one Work Library, profile assignment, search and import all use. Provider and external id
    /// stay evidence of it. Null for a request made before the binding or whose identity could not be resolved safely; it is bound lazily and never guessed.
    /// </summary>
    public bool IsBoundToWork => WorkId is not null;

    public bool IsOpen => AcquisitionAccessNames.IsOpen(Status);

    /// <summary>Whether an auto-approval rule (not a person) approved this request.</summary>
    public bool WasAutoApproved => AcquisitionAutoApproval.TryParseRuleId(DecidedByProfileId, out _);

    /// <summary>The address of the finished title on this server; a request can carry any result address, only a path of this server is followed.</summary>
    public string? LocalResultPath => ResultUrl is { Length: > 0 } url && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal) ? url : null;

    /// <summary>The message a request carries once it was cancelled; a cancelled request is stored as rejected with exactly this message.</summary>
    public const string CancelledMessage = "Withdrawn.";

    /// <summary>Whether the request ended because it was cancelled rather than rejected by an approver.</summary>
    public bool IsCancelled => Status == AcquisitionRequestStatus.Rejected && StatusMessage == CancelledMessage;

    /// <summary>Only a request that still waits for approval can be cancelled; once approved the acquisition is shared state of the title.</summary>
    public bool CanBeCancelled => Status == AcquisitionRequestStatus.Pending;

    /// <summary>
    /// Only a request that still waits for approval can change its scope or languages: afterwards the executor has copied them into the
    /// acquisition. Only media types whose Request dialog has settings (series scope, anime languages) can be edited.
    /// </summary>
    public bool CanBeEdited => Status == AcquisitionRequestStatus.Pending && Kind is MediaAcquisitionKind.Tv or MediaAcquisitionKind.Anime;

    /// <summary>A request that failed after approval can run again with the same intent.</summary>
    public bool CanBeRetried => Status == AcquisitionRequestStatus.Failed;

    /// <summary>The richer options the requester chose (anime only); the default options when none were chosen.</summary>
    public AcquisitionRequestOptions Options => Kind == MediaAcquisitionKind.Anime
        ? AcquisitionRequestOptions.FromPayload(PayloadJson)
        : AcquisitionRequestOptions.Default;
}

/// <summary>
/// What a page submits when a profile adds or requests a title found in search. <see cref="Options"/>
/// carry the richer choices (which seasons or episodes, languages, quality profile); the request
/// service validates them and keeps them in the payload of the request.
/// </summary>
public sealed record AcquisitionRequestDraft(
    MediaAcquisitionKind Kind,
    string Provider,
    string ExternalId,
    string Title,
    string? Subtitle,
    string? CoverImageUrl,
    string? PayloadJson = null,
    AcquisitionRequestOptions? Options = null,
    Guid? WorkId = null);

/// <summary>The request a submit ended with, and whether it was an open request for the title already.</summary>
public sealed record AcquisitionSubmission(AcquisitionRequest Request, bool AlreadyRequested);

/// <summary>The status and message a request had just before a conditional status change took it over.</summary>
public sealed record AcquisitionStatusTransition(AcquisitionRequestStatus PreviousStatus, string? PreviousMessage);

/// <summary>The status a request moves to, with its message and result address.</summary>
public sealed record AcquisitionStatusOutcome(AcquisitionRequestStatus Status, string? Message, string? ResultUrl = null, bool ClearOperation = false);

public sealed record AcquisitionExecution(
    AcquisitionRequestStatus Status,
    string? Message,
    Guid? OperationId = null,
    string? ResultUrl = null)
{
    /// <summary>
    /// For a result that ends the request because of what the run read (monitoring off, everything available): given the payload stored when
    /// the result is written, whether that is still true. When it is not, somebody changed the request meanwhile and the request goes back
    /// to Approved to be looked at again instead of being ended by a stale result.
    /// </summary>
    public Func<string?, bool>? StillApplies { get; init; }
}

/// <summary>Starts the automatic acquisition for one media type.</summary>
public interface IAcquisitionRequestExecutor
{
    MediaAcquisitionKind Kind { get; }

    Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken);
}

/// <summary>What cancelling a request came to; a request that is no longer pending is reported, never overwritten.</summary>
public enum RequestCancelOutcome
{
    Cancelled,

    /// <summary>It was cancelled already (a second tab, a repeated post); nothing changed.</summary>
    AlreadyCancelled,

    /// <summary>Somebody decided it first, so it can no longer be cancelled.</summary>
    NoLongerPending
}

public enum RequestEditOutcome
{
    Saved,

    /// <summary>The request is no longer pending, or its media type has nothing to edit.</summary>
    NotEditable
}

public enum RequestRetryOutcome
{
    Retried,

    /// <summary>It runs already (a second tab, a repeated post); nothing changed.</summary>
    AlreadyRetried,

    /// <summary>It is not failed (any more), the profile may not request this media type, or the title has another open request.</summary>
    NotRetryable
}

public sealed class AcquisitionAccessDeniedException(string message) : Exception(message);

public static class AcquisitionAccessNames
{
    /// <summary>The statuses of an approved request whose acquisition is underway: the ones a worker may take over, or bring to the state of its download.</summary>
    public static readonly IReadOnlyList<AcquisitionRequestStatus> UnderwayStatuses =
        [AcquisitionRequestStatus.Approved, AcquisitionRequestStatus.Downloading, AcquisitionRequestStatus.Importing];

    public static string Kind(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Anime => "anime",
        MediaAcquisitionKind.Manga => "manga",
        MediaAcquisitionKind.LightNovel => "lightNovel",
        MediaAcquisitionKind.Book => "book",
        MediaAcquisitionKind.Movie => "movie",
        MediaAcquisitionKind.Tv => "tv",
        MediaAcquisitionKind.Audiobook => "audiobook",
        MediaAcquisitionKind.Music => "music",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static MediaAcquisitionKind ParseKind(string value) => value switch
    {
        "anime" => MediaAcquisitionKind.Anime,
        "manga" => MediaAcquisitionKind.Manga,
        "lightNovel" => MediaAcquisitionKind.LightNovel,
        "book" => MediaAcquisitionKind.Book,
        "movie" => MediaAcquisitionKind.Movie,
        "tv" => MediaAcquisitionKind.Tv,
        "audiobook" => MediaAcquisitionKind.Audiobook,
        "music" => MediaAcquisitionKind.Music,
        _ => throw new ArgumentException($"Unknown media kind '{value}'.", nameof(value))
    };

    /// <summary>The media type of the capability matrix (#436) this acquisition kind belongs to.</summary>
    public static WorkMediaType WorkType(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Anime => WorkMediaType.Anime,
        MediaAcquisitionKind.Manga => WorkMediaType.Manga,
        MediaAcquisitionKind.LightNovel => WorkMediaType.LightNovel,
        MediaAcquisitionKind.Book => WorkMediaType.Book,
        MediaAcquisitionKind.Movie => WorkMediaType.Movie,
        MediaAcquisitionKind.Tv => WorkMediaType.Series,
        // An audiobook is an audio edition of a book, so it lives on the same Book capability matrix
        // (#436) and the media core represents it as a Book Work (#440).
        MediaAcquisitionKind.Audiobook => WorkMediaType.Book,
        MediaAcquisitionKind.Music => WorkMediaType.Music,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Whether a request in this status still waits for a decision or for its title to arrive.</summary>
    public static bool IsOpen(AcquisitionRequestStatus status) => status is AcquisitionRequestStatus.Pending
        or AcquisitionRequestStatus.Approved
        or AcquisitionRequestStatus.Searching
        or AcquisitionRequestStatus.Downloading
        or AcquisitionRequestStatus.Importing;

    public static string Manual(ManualAddMode mode) => mode switch
    {
        ManualAddMode.OwnerOnly => "ownerOnly",
        ManualAddMode.Users => "users",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static ManualAddMode ParseManual(string value) => value switch
    {
        "ownerOnly" => ManualAddMode.OwnerOnly,
        "users" => ManualAddMode.Users,
        _ => throw new ArgumentException($"Unknown manual add mode '{value}'.", nameof(value))
    };

    public static string Status(AcquisitionRequestStatus status) => status switch
    {
        AcquisitionRequestStatus.Pending => "pending",
        AcquisitionRequestStatus.Approved => "approved",
        AcquisitionRequestStatus.Searching => "searching",
        AcquisitionRequestStatus.Downloading => "downloading",
        AcquisitionRequestStatus.Importing => "importing",
        AcquisitionRequestStatus.Completed => "completed",
        AcquisitionRequestStatus.Rejected => "rejected",
        AcquisitionRequestStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static AcquisitionRequestStatus ParseStatus(string value) => value switch
    {
        "pending" => AcquisitionRequestStatus.Pending,
        "approved" => AcquisitionRequestStatus.Approved,
        "searching" => AcquisitionRequestStatus.Searching,
        "downloading" => AcquisitionRequestStatus.Downloading,
        "importing" => AcquisitionRequestStatus.Importing,
        "completed" => AcquisitionRequestStatus.Completed,
        "rejected" => AcquisitionRequestStatus.Rejected,
        "failed" => AcquisitionRequestStatus.Failed,
        _ => throw new ArgumentException($"Unknown request status '{value}'.", nameof(value))
    };
}
