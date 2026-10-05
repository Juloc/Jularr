using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Mapping;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Tracking;

public sealed class AniListAccountException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed record AniListViewer(
    int Id,
    string Name,
    string? AvatarUrl);

public sealed record AniListAccountStatus(
    bool IsConnected,
    int? ClientId,
    int? ViewerId,
    string? ViewerName,
    string? ViewerAvatarUrl,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? TokenExpiresAt)
{
    public static AniListAccountStatus Disconnected { get; } =
        new(false, null, null, null, null, null, null);

    public bool IsExpired =>
        TokenExpiresAt is not null &&
        TokenExpiresAt <= DateTimeOffset.UtcNow;
}

public sealed record AniListFuzzyDate(
    int? Year,
    int? Month,
    int? Day);

public sealed record AniListRemoteListEntry(
    int Id,
    int UserId,
    int MediaId,
    string? Status,
    int Progress,
    double? Score,
    int Repeat,
    int Priority,
    bool Private,
    string? Notes,
    bool HiddenFromStatusLists,
    JsonNode? CustomLists,
    JsonNode? AdvancedScores,
    AniListFuzzyDate? StartedAt,
    AniListFuzzyDate? CompletedAt,
    long? UpdatedAt,
    int ProgressVolumes = 0)
{
    public bool ProtectedFieldsEqual(AniListRemoteListEntry other) =>
        string.Equals(Status, other.Status, StringComparison.Ordinal) &&
        Equals(StartedAt, other.StartedAt) &&
        ProtectedFieldsEqualExceptTrackingStart(other);

    public bool ProtectedFieldsEqualExceptTrackingStart(AniListRemoteListEntry other) =>
        Nullable.Equals(Score, other.Score) &&
        Repeat == other.Repeat &&
        Priority == other.Priority &&
        Private == other.Private &&
        string.Equals(Notes, other.Notes, StringComparison.Ordinal) &&
        HiddenFromStatusLists == other.HiddenFromStatusLists &&
        JsonNode.DeepEquals(CustomLists, other.CustomLists) &&
        JsonNode.DeepEquals(AdvancedScores, other.AdvancedScores) &&
        Equals(CompletedAt, other.CompletedAt);
}

public enum AniListLibraryMediaType
{
    Anime,
    Manga
}

public sealed record AniListLibraryMedia(
    int MediaId,
    string MediaType,
    string? Format,
    string Title,
    string? NativeTitle,
    string? CoverImageUrl,
    string? MediaStatus,
    string? ListStatus,
    int Progress,
    int? TotalProgress,
    int? VolumeCount,
    int? Year,
    long? UpdatedAt,
    IReadOnlyList<string> Genres)
{
    public bool IsNovel =>
        string.Equals(MediaType, "MANGA", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Format, "NOVEL", StringComparison.OrdinalIgnoreCase);
}

public sealed record AniListProgressPreview(
    bool CanSync,
    bool IsNoOp,
    string Message,
    string? MediaTitle,
    int RequestedProgress,
    int? RemoteProgress,
    string? RemoteStatus,
    int? AniListEpisodeCount)
{
    public static AniListProgressPreview Blocked(
        string message,
        int requestedProgress = 0,
        string? mediaTitle = null,
        int? remoteProgress = null,
        string? remoteStatus = null,
        int? aniListEpisodeCount = null) =>
        new(
            false,
            false,
            message,
            mediaTitle,
            requestedProgress,
            remoteProgress,
            remoteStatus,
            aniListEpisodeCount);
}

public sealed record AniListReadingProgressPreview(
    bool CanSync,
    bool IsNoOp,
    string Message,
    string? MediaTitle,
    int RequestedProgress,
    int? RemoteProgress,
    string? RemoteStatus,
    int? AniListChapterCount,
    int? RequestedVolumeProgress = null,
    int? RemoteVolumeProgress = null)
{
    public static AniListReadingProgressPreview Blocked(
        string message,
        int requestedProgress = 0,
        string? mediaTitle = null,
        int? remoteProgress = null,
        string? remoteStatus = null,
        int? aniListChapterCount = null,
        int? requestedVolumeProgress = null,
        int? remoteVolumeProgress = null) =>
        new(
            false,
            false,
            message,
            mediaTitle,
            requestedProgress,
            remoteProgress,
            remoteStatus,
            aniListChapterCount,
            requestedVolumeProgress,
            remoteVolumeProgress);
}

public enum AniListExternalProgressStateKind
{
    Synced,
    LocalAhead,
    AniListAhead,
    NotOnList,
    MappingNeedsReview,
    NotConnected,
    NoLocalProgress,
    Blocked,
    RemoteUnavailable
}

public sealed record AniListExternalProgressState(
    AniListExternalProgressStateKind Kind,
    string Message,
    string? MediaTitle,
    int LocalProgress,
    int? RemoteProgress,
    int? LocalVolumeProgress = null,
    int? RemoteVolumeProgress = null,
    bool CanSync = false,
    int? MediaId = null,
    string? RemoteStatus = null)
{
    public bool IsSynced => Kind == AniListExternalProgressStateKind.Synced;
    public bool NeedsAttention =>
        Kind is AniListExternalProgressStateKind.MappingNeedsReview
            or AniListExternalProgressStateKind.NotOnList
            or AniListExternalProgressStateKind.Blocked;
}

public sealed record AniListProgressSyncResult(
    bool Success,
    bool Changed,
    string Message);

public sealed record AniListProgressBackup(
    string ProfileId,
    DateTimeOffset CapturedAt,
    string ViewerName,
    int RequestedProgress,
    AniListRemoteListEntry RemoteEntry,
    string MediaType = "ANIME",
    int? RequestedVolumeProgress = null);

public sealed partial class AniListAccountService(
    HttpClient httpClient,
    AniListAccountStore store,
    AppDbContext db,
    AnimeMetadataService metadataService,
    ReadingSegmentMappingStore segmentMappings,
    MediaMappingReviewStore mappingReviewStore,
    CurrentAccountContext currentAccount,
    ILogger<AniListAccountService> logger,
    TimeProvider? timeProvider = null,
    IHttpContextAccessor? httpContextAccessor = null,
    IInstanceModuleService? instanceModules = null)
{
    private const string ViewerQuery = """
        query {
          Viewer {
            id
            name
            avatar { medium }
          }
        }
        """;

    private const string MediaListQuery = """
        query ($userId: Int!, $mediaId: Int!) {
          MediaList(userId: $userId, mediaId: $mediaId, type: ANIME) {
            id
            userId
            mediaId
            status
            progress
            score
            repeat
            priority
            private
            notes
            hiddenFromStatusLists
            customLists
            advancedScores
            startedAt { year month day }
            completedAt { year month day }
            updatedAt
          }
        }
        """;

    private const string MangaListQuery = """
        query ($userId: Int!, $mediaId: Int!) {
          MediaList(userId: $userId, mediaId: $mediaId, type: MANGA) {
            id
            userId
            mediaId
            status
            progress
            progressVolumes
            score
            repeat
            priority
            private
            notes
            hiddenFromStatusLists
            customLists
            advancedScores
            startedAt { year month day }
            completedAt { year month day }
            updatedAt
          }
        }
        """;

    private const string MangaMetadataQuery = """
        query ($id: Int!) {
          Media(id: $id, type: MANGA) {
            id
            chapters
          }
        }
        """;

    private const string PersonalLibraryQuery = """
        query ($userId: Int!, $type: MediaType!) {
          MediaListCollection(userId: $userId, type: $type) {
            lists {
              status
              entries {
                id
                status
                progress
                repeat
                updatedAt
                media {
                  id
                  type
                  format
                  title { romaji english native }
                  coverImage { extraLarge large }
                  status
                  episodes
                  chapters
                  volumes
                  seasonYear
                  startDate { year }
                  genres
                  isAdult
                }
              }
            }
          }
        }
        """;

    // Safety invariant: the mutation has exactly two variables and only one mutable
    // list field: progress. Do not add status, score, notes, dates or list settings.
    private const string SaveProgressMutation = """
        mutation ($id: Int!, $progress: Int!) {
          SaveMediaListEntry(id: $id, progress: $progress) {
            id
            userId
            mediaId
            status
            progress
            progressVolumes
            score
            repeat
            priority
            private
            notes
            hiddenFromStatusLists
            customLists
            advancedScores
            startedAt { year month day }
            completedAt { year month day }
            updatedAt
          }
        }
        """;

    // A PLANNING entry is intentionally started in the same write as its first
    // safe forward progress. Status is fixed to CURRENT and startedAt is the
    // only additional list field accepted by this transition.
    private const string SaveStartingProgressMutation = """
        mutation ($id: Int!, $progress: Int!, $startYear: Int, $startMonth: Int, $startDay: Int) {
          SaveMediaListEntry(
            id: $id,
            progress: $progress,
            status: CURRENT,
            startedAt: { year: $startYear, month: $startMonth, day: $startDay }) {
            id
            userId
            mediaId
            status
            progress
            progressVolumes
            score
            repeat
            priority
            private
            notes
            hiddenFromStatusLists
            customLists
            advancedScores
            startedAt { year month day }
            completedAt { year month day }
            updatedAt
          }
        }
        """;

    // Reading media may additionally update progressVolumes, but only when an
    // explicit segment mapping resolves a higher volume. No other list fields
    // are accepted by this mutation.
    private const string SaveReadingProgressMutation = """
        mutation ($id: Int!, $progress: Int!, $progressVolumes: Int!) {
          SaveMediaListEntry(id: $id, progress: $progress, progressVolumes: $progressVolumes) {
            id
            userId
            mediaId
            status
            progress
            progressVolumes
            score
            repeat
            priority
            private
            notes
            hiddenFromStatusLists
            customLists
            advancedScores
            startedAt { year month day }
            completedAt { year month day }
            updatedAt
          }
        }
        """;

    private const string SaveStartingReadingProgressMutation = """
        mutation ($id: Int!, $progress: Int!, $progressVolumes: Int!, $startYear: Int, $startMonth: Int, $startDay: Int) {
          SaveMediaListEntry(
            id: $id,
            progress: $progress,
            progressVolumes: $progressVolumes,
            status: CURRENT,
            startedAt: { year: $startYear, month: $startMonth, day: $startDay }) {
            id
            userId
            mediaId
            status
            progress
            progressVolumes
            score
            repeat
            priority
            private
            notes
            hiddenFromStatusLists
            customLists
            advancedScores
            startedAt { year month day }
            completedAt { year month day }
            updatedAt
          }
        }
        """;


    public static string BuildAuthorizationUrl(int clientId)
    {
        if (clientId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientId));
        }

        return $"https://anilist.co/api/v2/oauth/authorize?client_id={clientId}&response_type=token";
    }

    public async Task<AniListAccountStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        var stored = await store.LoadAsync(
            currentAccount.ProfileId,
            cancellationToken);
        return stored is null
            ? AniListAccountStatus.Disconnected
            : ToStatus(stored);
    }

    public async Task<AniListAccountStatus> ConnectAsync(
        int clientId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (clientId <= 0)
        {
            throw new AniListAccountException("Enter a valid AniList client ID.");
        }

        var token = accessToken.Trim();
        if (token.Length < 20)
        {
            throw new AniListAccountException("The AniList access token is not valid.");
        }

        var viewer = await FetchViewerAsync(token, cancellationToken);
        // Refreshing the token of the same AniList user keeps the automatic
        // sync setting; connecting a different AniList user starts at Off.
        var previous = await store.LoadAsync(
            currentAccount.ProfileId,
            cancellationToken);
        var keepSync = previous?.ViewerId == viewer.Id;
        var account = new StoredAniListAccount(
            clientId,
            viewer.Id,
            viewer.Name,
            viewer.AvatarUrl,
            token,
            DateTimeOffset.UtcNow,
            TryReadTokenExpiry(token),
            keepSync ? previous!.SyncMode : AniListSyncMode.Off,
            keepSync ? previous!.SyncEnabledAt : null);

        await store.SaveAsync(
            currentAccount.ProfileId,
            account,
            cancellationToken);
        return ToStatus(account);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        store.DisconnectAsync(currentAccount.ProfileId, cancellationToken);

    public async Task<IReadOnlyList<AniListLibraryMedia>> GetLibraryAsync(
        AniListLibraryMediaType mediaType,
        CancellationToken cancellationToken)
    {
        var account = await store.LoadAsync(
            currentAccount.ProfileId,
            cancellationToken);
        if (account is null)
        {
            return [];
        }

        if (account.TokenExpiresAt is not null &&
            account.TokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new AniListAccountException(
                "Your AniList connection has expired. Reconnect it in Settings.");
        }

        var body = await SendAuthenticatedAsync(
            account.AccessToken,
            PersonalLibraryQuery,
            new
            {
                userId = account.ViewerId,
                type = mediaType == AniListLibraryMediaType.Anime
                    ? "ANIME"
                    : "MANGA"
            },
            "reading your AniList library",
            cancellationToken);

        return ParseLibraryResponse(body);
    }

    public async Task<AniListExternalProgressState> GetMangaProgressStateAsync(
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        var review = await mappingReviewStore.FindPendingAsync(
            "manga",
            seriesId.ToString(),
            ["identity", "reading-segments"],
            cancellationToken);
        if (review is not null)
        {
            return ClassifyProgressState(
                review.LocalTitle,
                0,
                null,
                review.Reason,
                canSync: false,
                forcedKind: AniListExternalProgressStateKind.MappingNeedsReview);
        }

        try
        {
            return ToExternalProgressState(
                await BuildMangaProgressContextAsync(
                    seriesId,
                    cancellationToken));
        }
        catch (AniListAccountException exception)
        {
            return ClassifyProgressState(
                null,
                0,
                null,
                exception.Message,
                canSync: false,
                forcedKind: AniListExternalProgressStateKind.Blocked);
        }
    }

    public async Task<AniListExternalProgressState> GetNovelProgressStateAsync(
        Guid workId,
        CancellationToken cancellationToken)
    {
        var review = await mappingReviewStore.FindPendingAsync(
            "novel",
            workId.ToString(),
            ["identity", "reading-segments"],
            cancellationToken);
        if (review is not null)
        {
            return ClassifyProgressState(
                review.LocalTitle,
                0,
                null,
                review.Reason,
                canSync: false,
                forcedKind: AniListExternalProgressStateKind.MappingNeedsReview);
        }

        try
        {
            return ToExternalProgressState(
                await BuildNovelProgressContextAsync(
                    workId,
                    cancellationToken));
        }
        catch (AniListAccountException exception)
        {
            return ClassifyProgressState(
                null,
                0,
                null,
                exception.Message,
                canSync: false,
                forcedKind: AniListExternalProgressStateKind.Blocked);
        }
    }

    public async Task<AniListExternalProgressState> GetEpisodeProgressStateAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        var animeId = await db.Episodes
            .AsNoTracking()
            .Where(x => x.Id == episodeId)
            .Select(x => (Guid?)x.AnimeId)
            .SingleOrDefaultAsync(cancellationToken);

        if (animeId is not null)
        {
            var review = await mappingReviewStore.FindPendingAsync(
                "anime",
                animeId.Value.ToString(),
                ["identity", "episode-ranges"],
                cancellationToken);
            if (review is not null)
            {
                return ClassifyProgressState(
                    review.LocalTitle,
                    0,
                    null,
                    review.Reason,
                    canSync: false,
                    forcedKind: AniListExternalProgressStateKind.MappingNeedsReview);
            }
        }

        try
        {
            return ToExternalProgressState(
                await BuildProgressContextAsync(
                    episodeId,
                    cancellationToken));
        }
        catch (AniListAccountException exception)
        {
            return ClassifyProgressState(
                null,
                0,
                null,
                exception.Message,
                canSync: false,
                forcedKind: AniListExternalProgressStateKind.Blocked);
        }
    }

    public static AniListExternalProgressState ClassifyProgressState(
        string? mediaTitle,
        int localProgress,
        int? remoteProgress,
        string message,
        bool canSync,
        int? localVolumeProgress = null,
        int? remoteVolumeProgress = null,
        AniListExternalProgressStateKind? forcedKind = null,
        int? mediaId = null,
        string? remoteStatus = null)
    {
        var kind = forcedKind ??
            ResolveComparableState(
                localProgress,
                remoteProgress,
                localVolumeProgress,
                remoteVolumeProgress);

        // Equal numbers are not "synchronized" while a write is still offered, for example a
        // PLANNING entry that the sync button would switch to CURRENT.
        if (kind == AniListExternalProgressStateKind.Synced && canSync)
        {
            kind = AniListExternalProgressStateKind.LocalAhead;
        }

        var resolvedMessage = kind == AniListExternalProgressStateKind.Synced
            ? "Local and AniList progress are synchronized."
            : message;

        return new AniListExternalProgressState(
            kind,
            resolvedMessage,
            mediaTitle,
            localProgress,
            remoteProgress,
            localVolumeProgress,
            remoteVolumeProgress,
            canSync,
            mediaId,
            remoteStatus);
    }

    private static AniListExternalProgressStateKind ResolveComparableState(
        int localProgress,
        int? remoteProgress,
        int? localVolumeProgress,
        int? remoteVolumeProgress)
    {
        if (remoteProgress is null)
        {
            return AniListExternalProgressStateKind.Blocked;
        }

        if (localProgress > remoteProgress.Value)
        {
            return AniListExternalProgressStateKind.LocalAhead;
        }

        if (remoteProgress.Value > localProgress)
        {
            return AniListExternalProgressStateKind.AniListAhead;
        }

        if (localVolumeProgress is not null &&
            remoteVolumeProgress is not null)
        {
            if (localVolumeProgress.Value > remoteVolumeProgress.Value)
            {
                return AniListExternalProgressStateKind.LocalAhead;
            }

            if (remoteVolumeProgress.Value > localVolumeProgress.Value)
            {
                return AniListExternalProgressStateKind.AniListAhead;
            }
        }

        return AniListExternalProgressStateKind.Synced;
    }

    private static AniListExternalProgressState ToExternalProgressState(
        ReadingProgressContext context) =>
        ClassifyProgressState(
            context.Preview.MediaTitle,
            // A volume-only write keeps the remote chapter count as the value to
            // send; the state must still compare the real local chapter progress.
            context.LocalProgress ?? context.Preview.RequestedProgress,
            context.Preview.RemoteProgress,
            context.Preview.Message,
            context.Preview.CanSync,
            context.Preview.RequestedVolumeProgress,
            context.Preview.RemoteVolumeProgress,
            context.StateHint,
            context.RemoteEntry?.MediaId ?? context.MediaId,
            context.Preview.RemoteStatus);

    private static AniListExternalProgressState ToExternalProgressState(
        ProgressContext context) =>
        ClassifyProgressState(
            context.Preview.MediaTitle,
            context.Preview.RequestedProgress,
            context.Preview.RemoteProgress,
            context.Preview.Message,
            context.Preview.CanSync,
            forcedKind: context.StateHint,
            mediaId: context.RemoteEntry?.MediaId ?? context.MediaId,
            remoteStatus: context.Preview.RemoteStatus);

    public async Task<AniListProgressSyncResult> SyncMangaProgressAsync(
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = await BuildMangaProgressContextAsync(
                seriesId,
                cancellationToken);
            if (!context.Preview.CanSync)
            {
                return new AniListProgressSyncResult(
                    Success: context.Preview.IsNoOp,
                    Changed: false,
                    context.Preview.Message);
            }

            var remote = context.RemoteEntry
                ?? throw new AniListAccountException("AniList list entry is missing.");
            var account = context.Account
                ?? throw new AniListAccountException("AniList account context is missing.");

            await store.AppendProgressBackupAsync(
                new AniListProgressBackup(
                    currentAccount.ProfileId,
                    DateTimeOffset.UtcNow,
                    account.ViewerName,
                    context.RequestedProgress,
                    remote,
                    "MANGA",
                    context.RequestedVolumeProgress),
                cancellationToken);

            AniListRemoteListEntry updated;
            if (context.RequestedVolumeProgress is int requestedVolumeProgress)
            {
                updated = await SaveReadingProgressAsync(
                    account.AccessToken,
                    remote,
                    context.RequestedProgress,
                    requestedVolumeProgress,
                    cancellationToken);

                ValidateReadingProgressUpdate(
                    remote,
                    updated,
                    context.RequestedProgress,
                    requestedVolumeProgress);
            }
            else
            {
                updated = await SaveProgressAsync(
                    account.AccessToken,
                    remote,
                    context.RequestedProgress,
                    cancellationToken);

                ValidateProgressOnlyUpdate(
                    remote,
                    updated,
                    context.RequestedProgress);
            }

            var startedTracking = IsPlanningStatus(remote.Status);
            return new AniListProgressSyncResult(
                Success: true,
                Changed: true,
                startedTracking
                    ? $"AniList moved from Planning to Reading and started tracking at chapter {updated.Progress}."
                    : context.RequestedVolumeProgress is int
                        ? $"AniList manga progress updated: chapters {remote.Progress} → {updated.Progress}, volumes {remote.ProgressVolumes} → {updated.ProgressVolumes}. No other list fields were sent."
                        : $"AniList manga chapter progress updated from {remote.Progress} to {updated.Progress}. No other list fields were sent.");
        }
        catch (AniListAccountException exception)
        {
            return new AniListProgressSyncResult(
                Success: false,
                Changed: false,
                exception.Message);
        }
    }

    public async Task<AniListReadingProgressPreview> GetNovelProgressPreviewAsync(
        Guid workId,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = await BuildNovelProgressContextAsync(
                workId,
                cancellationToken);
            return context.Preview;
        }
        catch (AniListAccountException exception)
        {
            return AniListReadingProgressPreview.Blocked(exception.Message);
        }
    }

    public async Task<AniListProgressSyncResult> SyncNovelProgressAsync(
        Guid workId,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = await BuildNovelProgressContextAsync(
                workId,
                cancellationToken);
            if (!context.Preview.CanSync)
            {
                return new AniListProgressSyncResult(
                    Success: context.Preview.IsNoOp,
                    Changed: false,
                    context.Preview.Message);
            }

            var remote = context.RemoteEntry
                ?? throw new AniListAccountException("AniList list entry is missing.");
            var account = context.Account
                ?? throw new AniListAccountException("AniList account context is missing.");

            await store.AppendProgressBackupAsync(
                new AniListProgressBackup(
                    currentAccount.ProfileId,
                    DateTimeOffset.UtcNow,
                    account.ViewerName,
                    context.RequestedProgress,
                    remote,
                    "MANGA",
                    context.RequestedVolumeProgress),
                cancellationToken);

            AniListRemoteListEntry updated;
            if (context.RequestedVolumeProgress is int requestedVolumeProgress)
            {
                updated = await SaveReadingProgressAsync(
                    account.AccessToken,
                    remote,
                    context.RequestedProgress,
                    requestedVolumeProgress,
                    cancellationToken);

                ValidateReadingProgressUpdate(
                    remote,
                    updated,
                    context.RequestedProgress,
                    requestedVolumeProgress);
            }
            else
            {
                updated = await SaveProgressAsync(
                    account.AccessToken,
                    remote,
                    context.RequestedProgress,
                    cancellationToken);

                ValidateProgressOnlyUpdate(
                    remote,
                    updated,
                    context.RequestedProgress);
            }

            var startedTracking = IsPlanningStatus(remote.Status);
            return new AniListProgressSyncResult(
                Success: true,
                Changed: true,
                startedTracking
                    ? $"AniList moved from Planning to Reading and started tracking at chapter {updated.Progress}."
                    : context.RequestedVolumeProgress is int
                        ? $"AniList reading progress updated: chapters {remote.Progress} → {updated.Progress}, volumes {remote.ProgressVolumes} → {updated.ProgressVolumes}. No other list fields were sent."
                        : $"AniList chapter progress updated from {remote.Progress} to {updated.Progress}. No other list fields were sent.");
        }
        catch (AniListAccountException exception)
        {
            return new AniListProgressSyncResult(
                Success: false,
                Changed: false,
                exception.Message);
        }
    }

    public async Task<AniListProgressSyncResult> SyncEpisodeProgressAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Read immediately before every write. Never trust a stale page preview.
            var context = await BuildProgressContextAsync(episodeId, cancellationToken);
            if (!context.Preview.CanSync)
            {
                return new AniListProgressSyncResult(
                    Success: context.Preview.IsNoOp,
                    Changed: false,
                    context.Preview.Message);
            }

            var remote = context.RemoteEntry
                ?? throw new AniListAccountException("AniList list entry is missing.");

            var account = context.Account
                ?? throw new AniListAccountException("AniList account context is missing.");

            await store.AppendProgressBackupAsync(
                new AniListProgressBackup(
                    currentAccount.ProfileId,
                    DateTimeOffset.UtcNow,
                    account.ViewerName,
                    context.RequestedProgress,
                    remote),
                cancellationToken);

            var updated = await SaveProgressAsync(
                account.AccessToken,
                remote,
                context.RequestedProgress,
                cancellationToken);

            ValidateProgressOnlyUpdate(
                remote,
                updated,
                context.RequestedProgress);

            return new AniListProgressSyncResult(
                Success: true,
                Changed: true,
                IsPlanningStatus(remote.Status)
                    ? $"AniList moved from Planning to Watching and started tracking at episode {updated.Progress}."
                    : $"AniList progress updated from {remote.Progress} to {updated.Progress}. No other list fields were sent.");
        }
        catch (AniListAccountException exception)
        {
            return new AniListProgressSyncResult(
                Success: false,
                Changed: false,
                exception.Message);
        }
    }

    private async Task<ReadingProgressContext> BuildMangaProgressContextAsync(
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        var repository = new MangaRepository(db);
        var local = await repository.GetAniListProgressContextAsync(
            currentAccount.ProfileId,
            seriesId,
            cancellationToken);

        if (local is null)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    "Read part of the manga in Jularr before syncing progress."),
                AniListExternalProgressStateKind.NoLocalProgress);
        }

        var segment = await segmentMappings.ResolveAsync(
            "manga",
            seriesId.ToString(),
            local.ChapterNumber,
            local.VolumeNumber,
            local.PageIndex,
            Math.Max(0, local.PageCount - 1),
            cancellationToken);

        int mediaId;
        int requestedProgress;
        int? requestedVolumeProgress = null;
        int? configuredChapterCount;
        var displayTitle = local.Title;

        if (segment is not null)
        {
            if (!segment.CanSync)
            {
                return ReadingProgressContext.Blocked(
                    AniListReadingProgressPreview.Blocked(
                        segment.Reason ?? "The current manga segment cannot be mapped safely.",
                        segment.Progress,
                        segment.PreferredTitle ?? local.Title,
                        aniListChapterCount: segment.RemoteChapterCount));
            }

            if (!string.Equals(
                    segment.Provider,
                    "anilist",
                    StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(segment.ExternalId, out mediaId) ||
                mediaId <= 0)
            {
                return ReadingProgressContext.Blocked(
                    AniListReadingProgressPreview.Blocked(
                        "The active manga segment does not point to a valid AniList media entry.",
                        segment.Progress,
                        segment.PreferredTitle ?? local.Title,
                        aniListChapterCount: segment.RemoteChapterCount),
                    AniListExternalProgressStateKind.MappingNeedsReview);
            }

            requestedProgress = segment.Progress;
            requestedVolumeProgress = segment.VolumeProgress;
            configuredChapterCount = segment.RemoteChapterCount;
            displayTitle = segment.PreferredTitle ?? local.Title;
        }
        else
        {
            if (!int.TryParse(local.MetadataExternalId, out mediaId) ||
                mediaId <= 0)
            {
                return ReadingProgressContext.Blocked(
                    AniListReadingProgressPreview.Blocked(
                        "Match this manga to AniList before syncing progress.",
                        mediaTitle: local.Title),
                    AniListExternalProgressStateKind.MappingNeedsReview);
            }

            var resolved = AutomaticMediaMatcher.ResolveReadingProgress(
                local.ChapterNumber,
                local.PageIndex,
                Math.Max(0, local.PageCount - 1));

            if (!resolved.CanSync)
            {
                return ReadingProgressContext.Blocked(
                    AniListReadingProgressPreview.Blocked(
                        resolved.Reason ?? "The local manga progress cannot be mapped safely.",
                        resolved.Progress,
                        local.Title));
            }

            requestedProgress = resolved.Progress;
            configuredChapterCount = null;
        }

        var account = await store.LoadAsync(
            currentAccount.ProfileId,
            cancellationToken);
        if (account is null)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    "Connect your AniList account in Settings before syncing progress.",
                    requestedProgress,
                    displayTitle,
                    aniListChapterCount: configuredChapterCount,
                    requestedVolumeProgress: requestedVolumeProgress),
                AniListExternalProgressStateKind.NotConnected,
                mediaId);
        }

        if (account.TokenExpiresAt is not null &&
            account.TokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    "Your AniList connection has expired. Reconnect it in Settings.",
                    requestedProgress,
                    displayTitle,
                    aniListChapterCount: configuredChapterCount,
                    requestedVolumeProgress: requestedVolumeProgress),
                AniListExternalProgressStateKind.NotConnected,
                mediaId);
        }

        AniListRemoteListEntry? remote;
        int? chapterCount;
        try
        {
            remote = await FetchMangaListEntryAsync(
                account,
                mediaId,
                cancellationToken);
            chapterCount = remote is null
                ? null
                : await FetchMangaChapterCountAsync(
                    account,
                    mediaId,
                    cancellationToken)
                  ?? configuredChapterCount;
        }
        catch (AniListAccountException exception)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    exception.Message,
                    requestedProgress,
                    displayTitle,
                    aniListChapterCount: configuredChapterCount,
                    requestedVolumeProgress: requestedVolumeProgress),
                AniListExternalProgressStateKind.RemoteUnavailable,
                mediaId);
        }

        if (remote is null)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    "The mapped manga entry is not on your AniList list. Add it in AniList first.",
                    requestedProgress,
                    displayTitle,
                    aniListChapterCount: configuredChapterCount,
                    requestedVolumeProgress: requestedVolumeProgress),
                AniListExternalProgressStateKind.NotOnList,
                mediaId);
        }

        var preview = EvaluateRemoteChapterProgressSafety(
            remote,
            requestedProgress,
            chapterCount,
            displayTitle,
            "manga");

        return WithMappedVolumeProgress(
            account,
            remote,
            preview.RequestedProgress,
            requestedVolumeProgress,
            preview,
            chapterCount,
            displayTitle,
            localProgress: requestedProgress);
    }

    /// <summary>
    /// Adds mapped volume progress (Manga volumes, EPUB light-novel volumes)
    /// to a chapter-progress context. Volume progress is only written forward,
    /// while the entry is CURRENT/REPEATING or while starting a PLANNING entry,
    /// and never lowers chapter progress.
    /// </summary>
    private static ReadingProgressContext WithMappedVolumeProgress(
        StoredAniListAccount account,
        AniListRemoteListEntry remote,
        int requestedProgress,
        int? requestedVolumeProgress,
        AniListReadingProgressPreview preview,
        int? chapterCount,
        string displayTitle,
        int? localProgress)
    {
        var volumeProgressToWrite =
            requestedVolumeProgress is > 0 &&
            requestedVolumeProgress.Value > remote.ProgressVolumes
                ? requestedVolumeProgress
                : null;

        var progressToWrite = preview.RequestedProgress;
        if (volumeProgressToWrite is not null && preview.IsNoOp)
        {
            if (!IsProgressTrackableStatus(remote.Status))
            {
                preview = AniListReadingProgressPreview.Blocked(
                    $"AniList status is {remote.Status ?? "unknown"}. Jularr tracks CURRENT/REPEATING entries and can start PLANNING entries.",
                    requestedProgress,
                    displayTitle,
                    remote.Progress,
                    remote.Status,
                    chapterCount,
                    requestedVolumeProgress,
                    remote.ProgressVolumes);
                volumeProgressToWrite = null;
            }
            else if (chapterCount is not > 0 ||
                     remote.Progress >= chapterCount.Value)
            {
                preview = AniListReadingProgressPreview.Blocked(
                    "Jularr will not update volume progress while the remote chapter state is final or cannot be verified safely.",
                    requestedProgress,
                    displayTitle,
                    remote.Progress,
                    remote.Status,
                    chapterCount,
                    requestedVolumeProgress,
                    remote.ProgressVolumes);
                volumeProgressToWrite = null;
            }
            else
            {
                progressToWrite = remote.Progress;
                preview = new AniListReadingProgressPreview(
                    CanSync: true,
                    IsNoOp: false,
                    $"Ready to increase AniList volume progress from {remote.ProgressVolumes} to {volumeProgressToWrite.Value} without lowering chapter progress.",
                    displayTitle,
                    progressToWrite,
                    remote.Progress,
                    remote.Status,
                    chapterCount,
                    requestedVolumeProgress,
                    remote.ProgressVolumes);
            }
        }
        else
        {
            preview = preview with
            {
                RequestedVolumeProgress = requestedVolumeProgress,
                RemoteVolumeProgress = remote.ProgressVolumes
            };
        }

        return new ReadingProgressContext(
            account,
            remote,
            progressToWrite,
            preview,
            volumeProgressToWrite,
            LocalProgress: localProgress);
    }

    private async Task<ReadingProgressContext> BuildNovelProgressContextAsync(
        Guid workId,
        CancellationToken cancellationToken)
    {
        var work = await db.NovelWorks
            .AsNoTracking()
            .Where(x => x.Id == workId)
            .Select(x => new
            {
                Title = x.MetadataTitle ?? x.Title,
                x.MetadataProvider,
                x.MetadataExternalId,
                x.MetadataChapterCount
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (work is null)
        {
            throw new AniListAccountException("Novel work was not found.");
        }

        var localProgress = await db.NovelProgress
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.ProfileId == currentAccount.ProfileId &&
                    x.WorkId == workId,
                cancellationToken);

        if (localProgress is null)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    "Read part of the novel in Jularr before syncing progress.",
                    mediaTitle: work.Title,
                    aniListChapterCount: work.MetadataChapterCount),
                AniListExternalProgressStateKind.NoLocalProgress);
        }

        // EPUB light-novel volumes are published volumes and may map to
        // AniList volume progress; implicit web/book volumes never do.
        var current = await (
            from chapter in db.NovelChapters.AsNoTracking()
            join volume in db.NovelVolumes.AsNoTracking()
                on chapter.VolumeId equals volume.Id
            where chapter.Id == localProgress.ChapterId &&
                  chapter.WorkId == workId
            select new
            {
                chapter.Number,
                VolumeNumber = volume.Kind == NovelVolumeKinds.Epub
                    ? (int?)volume.Number
                    : null
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (current is null)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    "The current local reading chapter could not be resolved.",
                    mediaTitle: work.Title,
                    aniListChapterCount: work.MetadataChapterCount));
        }

        var chapterNumber = current.Number;
        var segment = await segmentMappings.ResolveAsync(
            "novel",
            workId.ToString(),
            chapterNumber,
            current.VolumeNumber,
            localProgress.PositionPermille,
            completedThreshold: 950,
            cancellationToken);

        int mediaId;
        int requestedProgress;
        int? requestedVolumeProgress = null;
        int? configuredChapterCount;
        var displayTitle = work.Title;

        if (segment is not null)
        {
            if (!segment.CanSync)
            {
                return ReadingProgressContext.Blocked(
                    AniListReadingProgressPreview.Blocked(
                        segment.Reason ?? "The current light-novel segment cannot be mapped safely.",
                        segment.Progress,
                        segment.PreferredTitle ?? work.Title,
                        aniListChapterCount: segment.RemoteChapterCount));
            }

            if (!string.Equals(
                    segment.Provider,
                    "anilist",
                    StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(segment.ExternalId, out mediaId) ||
                mediaId <= 0)
            {
                return ReadingProgressContext.Blocked(
                    AniListReadingProgressPreview.Blocked(
                        "The active light-novel segment does not point to a valid AniList media entry.",
                        segment.Progress,
                        segment.PreferredTitle ?? work.Title,
                        aniListChapterCount: segment.RemoteChapterCount),
                    AniListExternalProgressStateKind.MappingNeedsReview);
            }

            requestedProgress = segment.Progress;
            requestedVolumeProgress = segment.VolumeProgress;
            configuredChapterCount = segment.RemoteChapterCount;
            displayTitle = segment.PreferredTitle ?? work.Title;
        }
        else
        {
            if (!string.Equals(
                    work.MetadataProvider,
                    "anilist",
                    StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(work.MetadataExternalId, out mediaId) ||
                mediaId <= 0)
            {
                return ReadingProgressContext.Blocked(
                    AniListReadingProgressPreview.Blocked(
                        "Match this light novel to AniList before syncing progress.",
                        mediaTitle: work.Title,
                        aniListChapterCount: work.MetadataChapterCount),
                    AniListExternalProgressStateKind.MappingNeedsReview);
            }

            var resolved = AutomaticMediaMatcher.ResolveReadingProgress(
                chapterNumber,
                localProgress.PositionPermille,
                completedThreshold: 950);

            if (!resolved.CanSync)
            {
                return ReadingProgressContext.Blocked(
                    AniListReadingProgressPreview.Blocked(
                        resolved.Reason ?? "The local light-novel progress cannot be mapped safely.",
                        resolved.Progress,
                        work.Title,
                        aniListChapterCount: work.MetadataChapterCount));
            }

            requestedProgress = resolved.Progress;
            configuredChapterCount = work.MetadataChapterCount;
        }

        var account = await store.LoadAsync(
            currentAccount.ProfileId,
            cancellationToken);
        if (account is null)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    "Connect your AniList account in Settings before syncing progress.",
                    requestedProgress,
                    displayTitle,
                    aniListChapterCount: configuredChapterCount),
                AniListExternalProgressStateKind.NotConnected,
                mediaId);
        }

        if (account.TokenExpiresAt is not null &&
            account.TokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    "Your AniList connection has expired. Reconnect it in Settings.",
                    requestedProgress,
                    displayTitle,
                    aniListChapterCount: configuredChapterCount),
                AniListExternalProgressStateKind.NotConnected,
                mediaId);
        }

        AniListRemoteListEntry? remote;
        int? chapterCount;
        try
        {
            remote = await FetchMangaListEntryAsync(
                account,
                mediaId,
                cancellationToken);
            chapterCount = remote is null
                ? null
                : await FetchMangaChapterCountAsync(
                    account,
                    mediaId,
                    cancellationToken)
                  ?? configuredChapterCount;
        }
        catch (AniListAccountException exception)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    exception.Message,
                    requestedProgress,
                    displayTitle,
                    aniListChapterCount: configuredChapterCount),
                AniListExternalProgressStateKind.RemoteUnavailable,
                mediaId);
        }

        if (remote is null)
        {
            return ReadingProgressContext.Blocked(
                AniListReadingProgressPreview.Blocked(
                    "The mapped light novel is not on your AniList list. Add it in AniList first.",
                    requestedProgress,
                    displayTitle,
                    aniListChapterCount: configuredChapterCount),
                AniListExternalProgressStateKind.NotOnList,
                mediaId);
        }

        var preview = EvaluateRemoteChapterProgressSafety(
            remote,
            requestedProgress,
            chapterCount,
            displayTitle);

        return WithMappedVolumeProgress(
            account,
            remote,
            requestedProgress,
            requestedVolumeProgress,
            preview,
            chapterCount,
            displayTitle,
            localProgress: null);
    }

    private async Task<ProgressContext> BuildProgressContextAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        var episode = await (
            from localEpisode in db.Episodes.AsNoTracking()
            join anime in db.Anime.AsNoTracking() on localEpisode.AnimeId equals anime.Id
            where localEpisode.Id == episodeId
            select new
            {
                localEpisode.AnimeId,
                localEpisode.Number,
                localEpisode.SeasonNumber,
                AnimeTitle = anime.Title
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (episode is null)
        {
            throw new AniListAccountException("Local episode was not found.");
        }

        if (episode.Number <= 0)
        {
            return ProgressContext.Blocked(
                AniListProgressPreview.Blocked(
                    "Special/unnumbered episodes are not synced automatically.",
                    episode.Number,
                    episode.AnimeTitle));
        }

        // AniList progress means completed episodes only: the episode must be completed in canonical MediaProgress
        // and, for regular episodes, lie within the contiguous CompletedThrough prefix of the anime.
        var localProgress = (await new VideoProgressService(db).GetLegacyEpisodeProgressAsync(
                currentAccount.ProfileId,
                [episode.AnimeId],
                cancellationToken))
            .SingleOrDefault(x => x.EpisodeId == episodeId);
        var completedThrough = episode.SeasonNumber > 0
            ? await FindLatestWatchedEpisodeAsync(episode.AnimeId, cancellationToken)
            : null;
        var isCompletedLocally = localProgress is { IsCompleted: true } &&
            (episode.SeasonNumber <= 0 ||
             (completedThrough is not null &&
              (episode.SeasonNumber, episode.Number).CompareTo((completedThrough.SeasonNumber, completedThrough.Number)) <= 0));
        if (!isCompletedLocally)
        {
            return ProgressContext.Blocked(
                AniListProgressPreview.Blocked(
                    $"Finish S{episode.SeasonNumber:00}E{episode.Number:00} and every episode before it in Jularr before syncing. An episode that is only in progress is never written to AniList.",
                    episode.Number,
                    episode.AnimeTitle),
                AniListExternalProgressStateKind.NoLocalProgress);
        }

        ResolvedAnimeEpisodeMetadata? resolved;
        try
        {
            resolved = await metadataService.ResolveEpisodeAsync(
                episodeId,
                cancellationToken);
        }
        catch (AniListAccountException exception)
        {
            return ProgressContext.Blocked(
                AniListProgressPreview.Blocked(
                    exception.Message,
                    episode.Number,
                    episode.AnimeTitle));
        }

        // Issue #568: AniList progress sync only runs while this anime's resolved
        // ProgressTracking role is AniList (the built-in default, reproducing today's behaviour).
        // An anime whose ProgressTracking role was explicitly assigned to something else is
        // treated the same as having no AniList episode mapping, so it is never synced here.
        var progressRole = await new ProviderRoleAssignmentStore(db).ResolveRoleForWorkAsync(
            episode.AnimeId,
            MappingProviderRole.ProgressTracking,
            cancellationToken);

        if (resolved is null ||
            !string.Equals(
                resolved.Provider,
                progressRole.Provider,
                StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(resolved.ExternalId, out var mediaId) ||
            mediaId <= 0)
        {
            return ProgressContext.Blocked(
                AniListProgressPreview.Blocked(
                    $"No AniList episode mapping exists for S{episode.SeasonNumber:00}E{episode.Number:00}. Add a range mapping on the anime page before syncing progress.",
                    episode.Number,
                    episode.AnimeTitle),
                AniListExternalProgressStateKind.MappingNeedsReview);
        }

        if (resolved.EpisodeCount is > 0 &&
            resolved.RemoteEpisodeNumber > resolved.EpisodeCount.Value)
        {
            return ProgressContext.Blocked(
                AniListProgressPreview.Blocked(
                    $"Mapped AniList episode {resolved.RemoteEpisodeNumber} is above the known episode count ({resolved.EpisodeCount}). Sync blocked.",
                    resolved.RemoteEpisodeNumber,
                    resolved.PreferredTitle,
                    aniListEpisodeCount: resolved.EpisodeCount),
                AniListExternalProgressStateKind.MappingNeedsReview,
                mediaId);
        }

        var account = await store.LoadAsync(
            currentAccount.ProfileId,
            cancellationToken);
        if (account is null)
        {
            return ProgressContext.Blocked(
                AniListProgressPreview.Blocked(
                    "Connect your AniList account in Settings before syncing progress.",
                    resolved.RemoteEpisodeNumber,
                    resolved.PreferredTitle,
                    aniListEpisodeCount: resolved.EpisodeCount),
                AniListExternalProgressStateKind.NotConnected,
                mediaId);
        }

        if (account.TokenExpiresAt is not null &&
            account.TokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            return ProgressContext.Blocked(
                AniListProgressPreview.Blocked(
                    "Your AniList connection has expired. Reconnect it in Settings.",
                    resolved.RemoteEpisodeNumber,
                    resolved.PreferredTitle,
                    aniListEpisodeCount: resolved.EpisodeCount),
                AniListExternalProgressStateKind.NotConnected,
                mediaId);
        }

        AniListRemoteListEntry? remote;
        try
        {
            remote = await FetchListEntryAsync(
                account,
                mediaId,
                cancellationToken);
        }
        catch (AniListAccountException exception)
        {
            return ProgressContext.Blocked(
                AniListProgressPreview.Blocked(
                    exception.Message,
                    resolved.RemoteEpisodeNumber,
                    resolved.PreferredTitle,
                    aniListEpisodeCount: resolved.EpisodeCount),
                AniListExternalProgressStateKind.RemoteUnavailable,
                mediaId);
        }

        if (remote is null)
        {
            return ProgressContext.Blocked(
                AniListProgressPreview.Blocked(
                    "This anime is not on your AniList list. Jularr will not create a list entry automatically.",
                    resolved.RemoteEpisodeNumber,
                    resolved.PreferredTitle,
                    aniListEpisodeCount: resolved.EpisodeCount),
                AniListExternalProgressStateKind.NotOnList,
                mediaId);
        }

        var remoteSafety = EvaluateRemoteProgressSafety(
            remote,
            resolved.RemoteEpisodeNumber,
            resolved.EpisodeCount,
            resolved.PreferredTitle);

        return new ProgressContext(
            account,
            remote,
            remoteSafety.RequestedProgress,
            remoteSafety);
    }

    private static bool IsProgressWritableStatus(string? status) =>
        string.Equals(status, "CURRENT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "REPEATING", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlanningStatus(string? status) =>
        string.Equals(status, "PLANNING", StringComparison.OrdinalIgnoreCase);

    private static bool IsProgressTrackableStatus(string? status) =>
        IsProgressWritableStatus(status) || IsPlanningStatus(status);

    public static AniListProgressPreview EvaluateRemoteProgressSafety(
        AniListRemoteListEntry remote,
        int requestedProgress,
        int? aniListEpisodeCount,
        string? mediaTitle)
    {
        if (requestedProgress <= 0)
        {
            return AniListProgressPreview.Blocked(
                "Special/unnumbered episodes are not synced automatically.",
                requestedProgress,
                mediaTitle,
                remote.Progress,
                remote.Status,
                aniListEpisodeCount);
        }

        var startsTracking = IsPlanningStatus(remote.Status);
        if (!startsTracking && remote.Progress >= requestedProgress)
        {
            return new AniListProgressPreview(
                CanSync: false,
                IsNoOp: true,
                $"AniList already has progress {remote.Progress}; Jularr never lowers progress.",
                mediaTitle,
                requestedProgress,
                remote.Progress,
                remote.Status,
                aniListEpisodeCount);
        }

        if (!IsProgressTrackableStatus(remote.Status))
        {
            return AniListProgressPreview.Blocked(
                $"AniList status is {remote.Status ?? "unknown"}. Jularr tracks CURRENT/REPEATING entries and can start PLANNING entries.",
                requestedProgress,
                mediaTitle,
                remote.Progress,
                remote.Status,
                aniListEpisodeCount);
        }

        var progressToWrite = startsTracking
            ? Math.Max(remote.Progress, requestedProgress)
            : requestedProgress;

        if (aniListEpisodeCount is > 0 &&
            progressToWrite >= aniListEpisodeCount.Value)
        {
            return AniListProgressPreview.Blocked(
                "This is the final AniList episode. Jularr does not sync the last episode automatically because AniList may also change completion status/date. Finish the entry in AniList itself.",
                requestedProgress,
                mediaTitle,
                remote.Progress,
                remote.Status,
                aniListEpisodeCount);
        }

        return new AniListProgressPreview(
            CanSync: true,
            IsNoOp: false,
            startsTracking
                ? $"Ready to start AniList tracking and set progress to {progressToWrite}."
                : $"Ready to increase AniList progress from {remote.Progress} to {progressToWrite}.",
            mediaTitle,
            progressToWrite,
            remote.Progress,
            remote.Status,
            aniListEpisodeCount);
    }

    public static AniListReadingProgressPreview EvaluateRemoteChapterProgressSafety(
        AniListRemoteListEntry remote,
        int requestedProgress,
        int? aniListChapterCount,
        string? mediaTitle,
        string mediaKind = "light novel")
    {
        var startsTracking = IsPlanningStatus(remote.Status);
        if (!startsTracking && remote.Progress >= requestedProgress)
        {
            return new AniListReadingProgressPreview(
                CanSync: false,
                IsNoOp: true,
                $"AniList already has chapter progress {remote.Progress}; Jularr never lowers progress.",
                mediaTitle,
                requestedProgress,
                remote.Progress,
                remote.Status,
                aniListChapterCount);
        }

        if (aniListChapterCount is not > 0)
        {
            return AniListReadingProgressPreview.Blocked(
                $"AniList does not expose a reliable chapter count for this {mediaKind}, so Jularr cannot safely assume the local chapter numbering matches.",
                requestedProgress,
                mediaTitle,
                remote.Progress,
                remote.Status,
                aniListChapterCount);
        }

        if (!IsProgressTrackableStatus(remote.Status))
        {
            return AniListReadingProgressPreview.Blocked(
                $"AniList status is {remote.Status ?? "unknown"}. Jularr tracks CURRENT/REPEATING entries and can start PLANNING entries.",
                requestedProgress,
                mediaTitle,
                remote.Progress,
                remote.Status,
                aniListChapterCount);
        }

        var progressToWrite = startsTracking
            ? Math.Max(remote.Progress, requestedProgress)
            : requestedProgress;

        if (aniListChapterCount is > 0 &&
            progressToWrite >= aniListChapterCount.Value)
        {
            return AniListReadingProgressPreview.Blocked(
                "This would reach the final AniList chapter. Jularr leaves completion status/date to AniList.",
                requestedProgress,
                mediaTitle,
                remote.Progress,
                remote.Status,
                aniListChapterCount);
        }

        return new AniListReadingProgressPreview(
            CanSync: true,
            IsNoOp: false,
            startsTracking
                ? $"Ready to start AniList tracking and set chapter progress to {progressToWrite}."
                : $"Ready to increase AniList chapter progress from {remote.Progress} to {progressToWrite}.",
            mediaTitle,
            progressToWrite,
            remote.Progress,
            remote.Status,
            aniListChapterCount);
    }

    private async Task<AniListViewer> FetchViewerAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var body = await SendAuthenticatedAsync(
            accessToken,
            ViewerQuery,
            new { },
            "validating the account",
            cancellationToken);

        return ParseViewerResponse(body);
    }

    private Task<AniListRemoteListEntry?> FetchListEntryAsync(
        StoredAniListAccount account,
        int mediaId,
        CancellationToken cancellationToken) =>
        FetchListEntryResponseAsync(
            account,
            MediaListQuery,
            mediaId,
            "reading list progress",
            cancellationToken);

    private Task<AniListRemoteListEntry?> FetchMangaListEntryAsync(
        StoredAniListAccount account,
        int mediaId,
        CancellationToken cancellationToken) =>
        FetchListEntryResponseAsync(
            account,
            MangaListQuery,
            mediaId,
            "reading manga/novel progress",
            cancellationToken);

    private async Task<int?> FetchMangaChapterCountAsync(
        StoredAniListAccount account,
        int mediaId,
        CancellationToken cancellationToken)
    {
        var body = await SendAuthenticatedAsync(
            account.AccessToken,
            MangaMetadataQuery,
            new { id = mediaId },
            "reading manga metadata",
            cancellationToken);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Media", out var media) ||
            media.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadInt(media, "chapters");
    }

    private async Task<AniListRemoteListEntry> SaveProgressAsync(
        string accessToken,
        AniListRemoteListEntry remote,
        int progress,
        CancellationToken cancellationToken)
    {
        var startsTracking = IsPlanningStatus(remote.Status);
        var startedAt = startsTracking
            ? ResolveTrackingStartDate(remote)
            : null;
        var body = await SendAuthenticatedAsync(
            accessToken,
            startsTracking ? SaveStartingProgressMutation : SaveProgressMutation,
            startsTracking
                ? BuildStartingProgressMutationVariables(remote.Id, progress, startedAt!)
                : BuildProgressMutationVariables(remote.Id, progress),
            startsTracking ? "starting list progress tracking" : "saving list progress",
            cancellationToken);

        return ParseListEntryResponse(body, "SaveMediaListEntry")
            ?? throw new AniListAccountException(
                "AniList returned no list entry after saving progress.");
    }

    private async Task<AniListRemoteListEntry> SaveReadingProgressAsync(
        string accessToken,
        AniListRemoteListEntry remote,
        int progress,
        int progressVolumes,
        CancellationToken cancellationToken)
    {
        var startsTracking = IsPlanningStatus(remote.Status);
        var startedAt = startsTracking
            ? ResolveTrackingStartDate(remote)
            : null;
        var body = await SendAuthenticatedAsync(
            accessToken,
            startsTracking ? SaveStartingReadingProgressMutation : SaveReadingProgressMutation,
            startsTracking
                ? BuildStartingReadingProgressMutationVariables(
                    remote.Id,
                    progress,
                    progressVolumes,
                    startedAt!)
                : BuildReadingProgressMutationVariables(
                    remote.Id,
                    progress,
                    progressVolumes),
            startsTracking ? "starting reading progress tracking" : "saving reading progress",
            cancellationToken);

        return ParseListEntryResponse(body, "SaveMediaListEntry")
            ?? throw new AniListAccountException(
                "AniList returned no list entry after saving reading progress.");
    }

    private AniListFuzzyDate ResolveTrackingStartDate(AniListRemoteListEntry remote)
    {
        if (remote.StartedAt is not null)
        {
            return remote.StartedAt;
        }

        var clock = timeProvider ?? TimeProvider.System;
        return TrackingStartDate(clock.GetUtcNow(), ResolveTrackingTimeZone(clock));
    }

    /// <summary>
    /// AniList start dates are calendar days, so "today" is the viewer's day, not the UTC day:
    /// an episode watched just after midnight in Germany starts tracking on that new day.
    /// </summary>
    public static AniListFuzzyDate TrackingStartDate(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        return new AniListFuzzyDate(local.Year, local.Month, local.Day);
    }

    // The browser zone the calendar already reports (jularr-tz cookie) on interactive syncs;
    // background syncs have no request and use the server's configured zone (TZ).
    private TimeZoneInfo ResolveTrackingTimeZone(TimeProvider clock)
    {
        var browserZone = httpContextAccessor?.HttpContext?.Request.Cookies[CalendarTimeZone.CookieName];
        return string.IsNullOrWhiteSpace(browserZone)
            ? clock.LocalTimeZone
            : CalendarTimeZone.Resolve(browserZone);
    }

    private void ValidateProgressOnlyUpdate(
        AniListRemoteListEntry remote,
        AniListRemoteListEntry updated,
        int requestedProgress)
    {
        if (updated.Id != remote.Id ||
            updated.UserId != remote.UserId ||
            updated.MediaId != remote.MediaId ||
            updated.Progress != requestedProgress ||
            updated.ProgressVolumes != remote.ProgressVolumes)
        {
            logger.LogCritical(
                "AniList returned an unexpected list entry after progress sync. Entry {EntryId}, media {MediaId}.",
                remote.Id,
                remote.MediaId);
            throw new AniListAccountException(
                "AniList returned an unexpected progress response. No further sync was attempted.");
        }

        if (!ProtectedFieldsMatchExpectedProgressWrite(remote, updated))
        {
            logger.LogCritical(
                "AniList protected list fields changed unexpectedly while updating progress for entry {EntryId}. A pre-write backup was saved under /data/integrations.",
                remote.Id);
            throw new AniListAccountException(
                "AniList changed fields outside the allowed progress/start transition unexpectedly. Sync stopped and a pre-write backup was saved.");
        }
    }

    private void ValidateReadingProgressUpdate(
        AniListRemoteListEntry remote,
        AniListRemoteListEntry updated,
        int requestedProgress,
        int requestedVolumeProgress)
    {
        if (updated.Id != remote.Id ||
            updated.UserId != remote.UserId ||
            updated.MediaId != remote.MediaId ||
            updated.Progress != requestedProgress ||
            updated.ProgressVolumes != requestedVolumeProgress)
        {
            logger.LogCritical(
                "AniList returned an unexpected reading list entry after chapter/volume sync. Entry {EntryId}, media {MediaId}.",
                remote.Id,
                remote.MediaId);
            throw new AniListAccountException(
                "AniList returned an unexpected reading-progress response. No further sync was attempted.");
        }

        if (!ProtectedFieldsMatchExpectedProgressWrite(remote, updated))
        {
            logger.LogCritical(
                "AniList protected list fields changed unexpectedly while updating chapter/volume progress for entry {EntryId}. A pre-write backup was saved under /data/integrations.",
                remote.Id);
            throw new AniListAccountException(
                "AniList changed fields outside the allowed reading-progress/start transition unexpectedly. Sync stopped and a pre-write backup was saved.");
        }
    }

    private static bool ProtectedFieldsMatchExpectedProgressWrite(
        AniListRemoteListEntry remote,
        AniListRemoteListEntry updated)
    {
        if (!IsPlanningStatus(remote.Status))
        {
            return remote.ProtectedFieldsEqual(updated);
        }

        return string.Equals(updated.Status, "CURRENT", StringComparison.OrdinalIgnoreCase) &&
               updated.StartedAt is not null &&
               (remote.StartedAt is null || Equals(remote.StartedAt, updated.StartedAt)) &&
               remote.ProtectedFieldsEqualExceptTrackingStart(updated);
    }

    public static IReadOnlyDictionary<string, int> BuildProgressMutationVariables(
        int listEntryId,
        int progress)
    {
        if (listEntryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(listEntryId));
        }

        if (progress < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progress));
        }

        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["id"] = listEntryId,
            ["progress"] = progress
        };
    }

    public static IReadOnlyDictionary<string, int?> BuildStartingProgressMutationVariables(
        int listEntryId,
        int progress,
        AniListFuzzyDate startedAt)
    {
        ValidateProgressMutationInput(listEntryId, progress);

        return new Dictionary<string, int?>(StringComparer.Ordinal)
        {
            ["id"] = listEntryId,
            ["progress"] = progress,
            ["startYear"] = startedAt.Year,
            ["startMonth"] = startedAt.Month,
            ["startDay"] = startedAt.Day
        };
    }

    public static IReadOnlyDictionary<string, int> BuildReadingProgressMutationVariables(
        int listEntryId,
        int progress,
        int progressVolumes)
    {
        if (listEntryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(listEntryId));
        }

        if (progress < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progress));
        }

        if (progressVolumes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressVolumes));
        }

        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["id"] = listEntryId,
            ["progress"] = progress,
            ["progressVolumes"] = progressVolumes
        };
    }

    public static IReadOnlyDictionary<string, int?> BuildStartingReadingProgressMutationVariables(
        int listEntryId,
        int progress,
        int progressVolumes,
        AniListFuzzyDate startedAt)
    {
        ValidateProgressMutationInput(listEntryId, progress);
        if (progressVolumes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressVolumes));
        }

        return new Dictionary<string, int?>(StringComparer.Ordinal)
        {
            ["id"] = listEntryId,
            ["progress"] = progress,
            ["progressVolumes"] = progressVolumes,
            ["startYear"] = startedAt.Year,
            ["startMonth"] = startedAt.Month,
            ["startDay"] = startedAt.Day
        };
    }

    private static void ValidateProgressMutationInput(int listEntryId, int progress)
    {
        if (listEntryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(listEntryId));
        }

        if (progress < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progress));
        }
    }

    private async Task<string> SendAuthenticatedAsync(
        string accessToken,
        string query,
        object variables,
        string operation,
        CancellationToken cancellationToken) =>
        (await SendAuthenticatedCoreAsync(
            accessToken,
            query,
            variables,
            operation,
            missingListEntryIsNull: false,
            cancellationToken))!;

    // AniList answers a MediaList lookup for media that is not on the viewer's
    // list with HTTP 404 and a GraphQL "Not Found" error. That is the canonical
    // "not on list" answer, not an outage.
    private async Task<AniListRemoteListEntry?> FetchListEntryResponseAsync(
        StoredAniListAccount account,
        string query,
        int mediaId,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await SendAuthenticatedCoreAsync(
            account.AccessToken,
            query,
            new
            {
                userId = account.ViewerId,
                mediaId
            },
            operation,
            missingListEntryIsNull: true,
            cancellationToken);

        return body is null
            ? null
            : ParseListEntryResponse(body, "MediaList");
    }

    public static bool IsMissingListEntryResponse(
        System.Net.HttpStatusCode statusCode,
        string body)
    {
        if (statusCode != System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("errors", out var errors) ||
                errors.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    ReadInt(error, "status") == 404)
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<string?> SendAuthenticatedCoreAsync(
        string accessToken,
        string query,
        object variables,
        string operation,
        bool missingListEntryIsNull,
        CancellationToken cancellationToken)
    {
        if (instanceModules is not null
            && !await instanceModules.IsEnabledAsync(
                InstanceModule.Tracking,
                cancellationToken))
        {
            throw new AniListAccountException(
                "Tracking and external sync are disabled on this Jularr instance.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = JsonContent.Create(new
            {
                query,
                variables
            });

            using var response = await httpClient.SendAsync(
                request,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (missingListEntryIsNull &&
                    IsMissingListEntryResponse(response.StatusCode, body))
                {
                    return null;
                }

                throw new AniListAccountException(
                    $"AniList returned HTTP {(int)response.StatusCode} while {operation}.");
            }

            ThrowIfGraphQlErrors(body);
            return body;
        }
        catch (AniListAccountException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            JsonException)
        {
            logger.LogWarning(exception, "AniList request failed while {Operation}.", operation);
            throw new AniListAccountException(
                $"AniList is currently unavailable while {operation}.",
                exception);
        }
    }

    public static IReadOnlyList<AniListLibraryMedia> ParseLibraryResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        ThrowIfGraphQlErrors(document.RootElement);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("MediaListCollection", out var collection) ||
            collection.ValueKind != JsonValueKind.Object ||
            !collection.TryGetProperty("lists", out var lists) ||
            lists.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<AniListLibraryMedia>();
        foreach (var list in lists.EnumerateArray())
        {
            if (!list.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("media", out var media) ||
                    media.ValueKind != JsonValueKind.Object ||
                    ReadBool(media, "isAdult") ||
                    !TryReadInt(media, "id", out var mediaId))
                {
                    continue;
                }

                var type = ReadString(media, "type") ?? "";
                if (type.Length == 0)
                {
                    continue;
                }

                var titleElement = media.TryGetProperty("title", out var title)
                    ? title
                    : default;
                var preferredTitle =
                    ReadString(titleElement, "english") ??
                    ReadString(titleElement, "romaji") ??
                    ReadString(titleElement, "native") ??
                    $"AniList {mediaId}";

                string? cover = null;
                if (media.TryGetProperty("coverImage", out var coverElement) &&
                    coverElement.ValueKind == JsonValueKind.Object)
                {
                    cover =
                        ReadString(coverElement, "extraLarge") ??
                        ReadString(coverElement, "large");
                }

                int? year = ReadInt(media, "seasonYear");
                if (year is null &&
                    media.TryGetProperty("startDate", out var startDate) &&
                    startDate.ValueKind == JsonValueKind.Object)
                {
                    year = ReadInt(startDate, "year");
                }

                var totalProgress = string.Equals(
                        type,
                        "ANIME",
                        StringComparison.OrdinalIgnoreCase)
                    ? ReadInt(media, "episodes")
                    : ReadInt(media, "chapters");

                result.Add(new AniListLibraryMedia(
                    mediaId,
                    type,
                    ReadString(media, "format"),
                    preferredTitle,
                    ReadString(titleElement, "native"),
                    cover,
                    ReadString(media, "status"),
                    ReadString(entry, "status"),
                    ReadInt(entry, "progress") ?? 0,
                    totalProgress,
                    ReadInt(media, "volumes"),
                    year,
                    ReadLong(entry, "updatedAt"),
                    ReadStringArray(media, "genres")));
            }
        }

        return result
            .OrderByDescending(x => x.UpdatedAt ?? 0)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static AniListViewer ParseViewerResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        ThrowIfGraphQlErrors(document.RootElement);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Viewer", out var viewer) ||
            viewer.ValueKind != JsonValueKind.Object ||
            !viewer.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt32(out var id) ||
            !viewer.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            throw new AniListAccountException(
                "AniList returned an unexpected account response.");
        }

        var name = nameElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AniListAccountException(
                "AniList returned an account without a username.");
        }

        string? avatarUrl = null;
        if (viewer.TryGetProperty("avatar", out var avatar) &&
            avatar.ValueKind == JsonValueKind.Object &&
            avatar.TryGetProperty("medium", out var medium) &&
            medium.ValueKind == JsonValueKind.String)
        {
            avatarUrl = medium.GetString();
        }

        return new AniListViewer(id, name, avatarUrl);
    }

    public static AniListRemoteListEntry? ParseListEntryResponse(
        string json,
        string fieldName)
    {
        using var document = JsonDocument.Parse(json);
        ThrowIfGraphQlErrors(document.RootElement);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty(fieldName, out var entry) ||
            entry.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (entry.ValueKind != JsonValueKind.Object ||
            !TryReadInt(entry, "id", out var id) ||
            !TryReadInt(entry, "userId", out var userId) ||
            !TryReadInt(entry, "mediaId", out var mediaId))
        {
            throw new AniListAccountException(
                "AniList returned an unexpected list-entry response.");
        }

        return new AniListRemoteListEntry(
            id,
            userId,
            mediaId,
            ReadString(entry, "status"),
            ReadInt(entry, "progress") ?? 0,
            ReadDouble(entry, "score"),
            ReadInt(entry, "repeat") ?? 0,
            ReadInt(entry, "priority") ?? 0,
            ReadBool(entry, "private"),
            ReadString(entry, "notes"),
            ReadBool(entry, "hiddenFromStatusLists"),
            ReadJsonNode(entry, "customLists"),
            ReadJsonNode(entry, "advancedScores"),
            ReadFuzzyDate(entry, "startedAt"),
            ReadFuzzyDate(entry, "completedAt"),
            ReadLong(entry, "updatedAt"),
            ReadInt(entry, "progressVolumes") ?? 0);
    }

    public static DateTimeOffset? TryReadTokenExpiry(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');

            payload = payload.PadRight(
                payload.Length + ((4 - payload.Length % 4) % 4),
                '=');

            using var document = JsonDocument.Parse(
                Encoding.UTF8.GetString(Convert.FromBase64String(payload)));

            if (!document.RootElement.TryGetProperty("exp", out var expiry) ||
                !expiry.TryGetInt64(out var unixSeconds))
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (Exception exception) when (
            exception is FormatException or
            JsonException or
            ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void ThrowIfGraphQlErrors(string json)
    {
        using var document = JsonDocument.Parse(json);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    private static void ThrowIfGraphQlErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array ||
            errors.GetArrayLength() == 0)
        {
            return;
        }

        var message = errors[0].TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : null;

        throw new AniListAccountException(
            string.IsNullOrWhiteSpace(message)
                ? "AniList returned a GraphQL error."
                : $"AniList: {message}");
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool TryReadInt(
        JsonElement element,
        string propertyName,
        out int value)
    {
        var parsed = ReadInt(element, propertyName);
        value = parsed ?? 0;
        return parsed.HasValue;
    }

    private static long? ReadLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;

    private static double? ReadDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool ReadBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
    }

    private static JsonNode? ReadJsonNode(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonNode.Parse(value.GetRawText());
    }

    private static AniListFuzzyDate? ReadFuzzyDate(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var year = ReadInt(value, "year");
        var month = ReadInt(value, "month");
        var day = ReadInt(value, "day");

        return year is null && month is null && day is null
            ? null
            : new AniListFuzzyDate(year, month, day);
    }

    private static AniListAccountStatus ToStatus(StoredAniListAccount account) =>
        new(
            true,
            account.ClientId,
            account.ViewerId,
            account.ViewerName,
            account.ViewerAvatarUrl,
            account.ConnectedAt,
            account.TokenExpiresAt);

    private sealed record ReadingProgressContext(
        StoredAniListAccount? Account,
        AniListRemoteListEntry? RemoteEntry,
        int RequestedProgress,
        AniListReadingProgressPreview Preview,
        int? RequestedVolumeProgress = null,
        AniListExternalProgressStateKind? StateHint = null,
        int? MediaId = null,
        int? LocalProgress = null)
    {
        public static ReadingProgressContext Blocked(
            AniListReadingProgressPreview preview,
            AniListExternalProgressStateKind stateHint =
                AniListExternalProgressStateKind.Blocked,
            int? mediaId = null) =>
            new(
                null,
                null,
                preview.RequestedProgress,
                preview,
                preview.RequestedVolumeProgress,
                stateHint,
                mediaId);
    }

    private sealed record ProgressContext(
        StoredAniListAccount? Account,
        AniListRemoteListEntry? RemoteEntry,
        int RequestedProgress,
        AniListProgressPreview Preview,
        AniListExternalProgressStateKind? StateHint = null,
        int? MediaId = null)
    {
        public static ProgressContext Blocked(
            AniListProgressPreview preview,
            AniListExternalProgressStateKind stateHint =
                AniListExternalProgressStateKind.Blocked,
            int? mediaId = null) =>
            new(
                null,
                null,
                preview.RequestedProgress,
                preview,
                stateHint,
                mediaId);
    }
}
