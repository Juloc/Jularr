using Jularr.Web.Data;
using Jularr.Web.Features.Games;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ClientApi;

public enum ClientOfflinePackageQueryStatus
{
    Success,
    Invalid,
    NotFound,
    Unavailable
}

public sealed record ClientOfflinePackageQueryResult<T>(
    ClientOfflinePackageQueryStatus Status,
    T? Value,
    string? ErrorCode = null,
    string? ErrorMessage = null)
    where T : class
{
    public static ClientOfflinePackageQueryResult<T> Success(T value) =>
        new(ClientOfflinePackageQueryStatus.Success, value);

    public static ClientOfflinePackageQueryResult<T> Invalid(string code, string message) =>
        new(ClientOfflinePackageQueryStatus.Invalid, null, code, message);

    public static ClientOfflinePackageQueryResult<T> NotFound(string code, string message) =>
        new(ClientOfflinePackageQueryStatus.NotFound, null, code, message);

    public static ClientOfflinePackageQueryResult<T> Unavailable(string code, string message) =>
        new(ClientOfflinePackageQueryStatus.Unavailable, null, code, message);
}

/// <summary>
/// Read-only application service for canonical Offline package capabilities. It intentionally does not
/// prepare renditions, enqueue client work or persist device state; a later prepare command consumes
/// the validated selection produced here.
/// </summary>
public sealed class ClientApiOfflinePackageOptionsService(
    AppDbContext db,
    IInstanceModuleService instanceModules)
{
    public async Task<ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>> GetOptionsAsync(
        ClientOfflinePackageTarget? target,
        string? intentValue,
        CancellationToken cancellationToken)
    {
        if (!ClientApiOfflinePackageContract.TryValidateTarget(target, out var targetError))
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Invalid("invalid_offline_target", targetError);
        }

        if (!ClientApiOfflinePackageContract.TryParseIntent(intentValue, out var intent))
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Invalid(
                "invalid_offline_intent",
                "Offline package intent must be watch, read, listen or play.");
        }

        return string.Equals(target!.Kind, ClientApiOfflinePackageContract.GameTarget, StringComparison.OrdinalIgnoreCase)
            ? await GetGameOptionsAsync(target, intent, cancellationToken)
            : await GetWorkOptionsAsync(target, intent, cancellationToken);
    }

    public async Task<ClientOfflinePackageQueryResult<ClientOfflinePackagePreview>> PreviewAsync(
        ClientOfflinePackageTarget? target,
        string? intentValue,
        ClientOfflinePackageSelection? requestedSelection,
        CancellationToken cancellationToken)
    {
        var optionsResult = await GetOptionsAsync(target, intentValue, cancellationToken);
        if (optionsResult.Status != ClientOfflinePackageQueryStatus.Success || optionsResult.Value is null)
        {
            return new(
                optionsResult.Status,
                null,
                optionsResult.ErrorCode,
                optionsResult.ErrorMessage);
        }

        var options = optionsResult.Value;
        var selection = requestedSelection ?? new ClientOfflinePackageSelection();
        var scope = string.IsNullOrWhiteSpace(selection.Scope)
            ? options.Scopes.FirstOrDefault(x => x.IsDefault)?.Key
            : selection.Scope.Trim().ToLowerInvariant();

        if (scope is null || !options.Scopes.Any(x => string.Equals(x.Key, scope, StringComparison.Ordinal)))
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackagePreview>.Invalid(
                "invalid_offline_scope",
                "The selected Offline scope is not available for this target.");
        }

        var unitIds = selection.UnitIds?.Where(x => x != Guid.Empty).Distinct().ToArray() ?? [];
        var validUnitIds = options.Units.Select(x => x.Id).ToHashSet();
        if (unitIds.Any(x => !validUnitIds.Contains(x)))
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackagePreview>.Invalid(
                "invalid_offline_scope",
                "At least one selected Offline unit does not belong to this target.");
        }

        var targetProvidesUnit = target!.WorkEpisodeId is not null || target.WorkChapterId is not null || target.GameReleaseId is not null;
        if (scope is ClientApiOfflinePackageContract.SelectedScope or ClientApiOfflinePackageContract.ReleaseScope && unitIds.Length == 0 && !targetProvidesUnit)
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackagePreview>.Invalid(
                "invalid_offline_scope",
                "The selected Offline scope requires at least one unit.");
        }

        if (scope == ClientApiOfflinePackageContract.ReleaseScope && unitIds.Length > 1)
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackagePreview>.Invalid(
                "invalid_offline_scope",
                "A Game Offline package targets one canonical release at a time.");
        }

        if (selection.EditionId is { } editionId && !options.Editions.Any(x => x.Id == editionId))
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackagePreview>.Invalid(
                "invalid_offline_edition",
                "The selected Edition does not belong to this target.");
        }

        if (!ValidQuality(selection.Quality, options.Qualities) || !ValidQuality(selection.ImageQuality, options.ImageQualities))
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackagePreview>.Invalid(
                "invalid_offline_quality",
                "The selected Offline quality is not available for this target.");
        }

        if (!ValidTracks(selection.AudioTrackIds, options.AudioTracks) || !ValidTracks(selection.SubtitleTrackIds, options.SubtitleTracks))
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackagePreview>.Invalid(
                "invalid_offline_track",
                "At least one selected track is not available for this target.");
        }

        if (selection.IncludeLearning && !options.LearningAvailable)
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackagePreview>.Invalid(
                "offline_learning_unavailable",
                "Learning data is not available for this Offline target.");
        }

        var normalized = selection with
        {
            Scope = scope,
            UnitIds = unitIds,
            Quality = NormalizeOptionalKey(selection.Quality),
            ImageQuality = NormalizeOptionalKey(selection.ImageQuality),
            AudioTrackIds = NormalizeIds(selection.AudioTrackIds),
            SubtitleTrackIds = NormalizeIds(selection.SubtitleTrackIds)
        };
        var estimate = string.Equals(options.Target.Kind, ClientApiOfflinePackageContract.GameTarget, StringComparison.OrdinalIgnoreCase)
            ? await EstimateGameAsync(options.Target, normalized, cancellationToken)
            : await EstimateWorkAsync(options.Target, options.Intent, normalized, cancellationToken);

        return ClientOfflinePackageQueryResult<ClientOfflinePackagePreview>.Success(new(
            ClientApiContract.ApiVersion,
            options.Target,
            options.Intent,
            options.Category,
            normalized,
            estimate));
    }

    private async Task<ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>> GetWorkOptionsAsync(
        ClientOfflinePackageTarget target,
        ClientOfflinePackageIntent intent,
        CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking()
            .Where(x => x.Id == target.WorkId!.Value)
            .Select(x => new { x.Id, x.MediaType, x.CanonicalTitle })
            .SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.NotFound(
                "offline_target_not_found",
                "The canonical Work does not exist.");
        }

        if (!Supports(work.MediaType, intent))
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Unavailable(
                "offline_not_supported",
                "This Offline intent is not supported by the target media type.");
        }

        var requiredModule = RequiredModule(work.MediaType, intent);
        if (!await instanceModules.IsEnabledAsync(requiredModule, cancellationToken))
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Unavailable(
                "offline_not_supported",
                "The required media module is disabled on this Jularr server.");
        }

        if (target.WorkEpisodeId is { } episodeId)
        {
            var validEpisode = await db.WorkEpisodes.AsNoTracking().AnyAsync(x => x.Id == episodeId && x.WorkId == work.Id, cancellationToken);
            if (!validEpisode)
            {
                return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Invalid(
                    "invalid_offline_target",
                    "The selected episode does not belong to the canonical Work.");
            }
        }

        if (target.WorkChapterId is { } chapterId)
        {
            var validChapter = await db.WorkChapters.AsNoTracking().AnyAsync(x => x.Id == chapterId && x.WorkId == work.Id, cancellationToken);
            if (!validChapter)
            {
                return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Invalid(
                    "invalid_offline_target",
                    "The selected chapter does not belong to the canonical Work.");
            }
        }

        if (target.EditionId is { } targetEditionId)
        {
            var validEdition = await db.WorkEditions.AsNoTracking().AnyAsync(x => x.Id == targetEditionId && x.WorkId == work.Id, cancellationToken);
            if (!validEdition)
            {
                return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Invalid(
                    "invalid_offline_target",
                    "The selected Edition does not belong to the canonical Work.");
            }
        }

        var intentName = ClientApiOfflinePackageContract.IntentName(intent);
        var category = Category(intent);
        var scopes = new List<ClientOfflineScopeOption>();
        var units = new List<ClientOfflineUnitOption>();

        if (intent == ClientOfflinePackageIntent.Watch && work.MediaType is WorkMediaType.Anime or WorkMediaType.Series)
        {
            var episodes = await db.WorkEpisodes.AsNoTracking()
                .Where(x => x.WorkId == work.Id)
                .OrderBy(x => x.SeasonNumber)
                .ThenBy(x => x.EpisodeNumber)
                .Select(x => new { x.Id, x.SeasonId, x.SeasonNumber, x.EpisodeNumber, x.Title })
                .ToListAsync(cancellationToken);
            units.AddRange(episodes.Select(x => new ClientOfflineUnitOption(
                x.Id,
                "episode",
                string.IsNullOrWhiteSpace(x.Title) ? $"Episode {x.EpisodeNumber}" : x.Title!,
                x.EpisodeNumber,
                x.SeasonId,
                $"Season {x.SeasonNumber}")));

            scopes.Add(new ClientOfflineScopeOption(
                target.WorkEpisodeId is null ? ClientApiOfflinePackageContract.SelectedScope : ClientApiOfflinePackageContract.SingleScope,
                IsDefault: true));
            if (target.WorkEpisodeId is not null)
            {
                scopes.Add(new(ClientApiOfflinePackageContract.SelectedScope, IsDefault: false));
            }
            if (episodes.Count > 0)
            {
                scopes.Add(new(ClientApiOfflinePackageContract.SeasonScope, IsDefault: false));
            }
        }
        else if (intent == ClientOfflinePackageIntent.Read)
        {
            var chapters = await db.WorkChapters.AsNoTracking()
                .Where(x => x.WorkId == work.Id)
                .OrderBy(x => x.Number)
                .Select(x => new { x.Id, x.VolumeId, x.Number, x.Title })
                .ToListAsync(cancellationToken);
            var volumeTitles = await db.WorkVolumes.AsNoTracking()
                .Where(x => x.WorkId == work.Id)
                .ToDictionaryAsync(x => x.Id, x => string.IsNullOrWhiteSpace(x.Title) ? $"Volume {x.Number}" : x.Title!, cancellationToken);

            units.AddRange(chapters.Select(x => new ClientOfflineUnitOption(
                x.Id,
                "chapter",
                string.IsNullOrWhiteSpace(x.Title) ? $"Chapter {x.Number:0.##}" : x.Title!,
                x.Number,
                x.VolumeId,
                x.VolumeId is { } volumeId && volumeTitles.TryGetValue(volumeId, out var volumeTitle) ? volumeTitle : null)));

            scopes.Add(new(
                target.WorkChapterId is null ? ClientApiOfflinePackageContract.WorkScope : ClientApiOfflinePackageContract.SingleScope,
                IsDefault: true));
            if (target.WorkChapterId is not null)
            {
                scopes.Add(new(ClientApiOfflinePackageContract.SelectedScope, IsDefault: false));
            }
            else if (chapters.Count > 0)
            {
                scopes.Add(new(ClientApiOfflinePackageContract.SelectedScope, IsDefault: false));
            }
            if (chapters.Any(x => x.VolumeId is not null))
            {
                scopes.Add(new(ClientApiOfflinePackageContract.VolumeScope, IsDefault: false));
            }
        }
        else
        {
            scopes.Add(new(ClientApiOfflinePackageContract.SingleScope, IsDefault: true));
        }

        var editionsQuery = db.WorkEditions.AsNoTracking().Where(x => x.WorkId == work.Id);
        if (intent == ClientOfflinePackageIntent.Listen)
        {
            editionsQuery = editionsQuery.Where(x => x.Format == LegacyWorkBridge.AudiobookEditionFormat);
        }
        else if (intent == ClientOfflinePackageIntent.Read)
        {
            editionsQuery = editionsQuery.Where(x => x.Format != LegacyWorkBridge.AudiobookEditionFormat);
        }

        var editions = await editionsQuery
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.Language)
            .Select(x => new ClientOfflineEditionOption(x.Id, x.Language, x.Format, x.Title, x.IsPrimary))
            .ToListAsync(cancellationToken);

        var qualityKeys = await db.WorkVersions.AsNoTracking()
            .Where(x => x.WorkId == work.Id && x.Quality != null && x.Quality != "")
            .Select(x => x.Quality!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        var qualities = qualityKeys
            .Select((quality, index) => new ClientOfflineQualityOption(quality, IsDefault: index == 0))
            .ToArray();

        var tracks = intent == ClientOfflinePackageIntent.Watch
            ? await GetSingleVideoTracksAsync(work.Id, target.WorkEpisodeId, cancellationToken)
            : (Audio: Array.Empty<ClientOfflineTrackOption>(), Subtitles: Array.Empty<ClientOfflineTrackOption>());

        var downloadable = await HasDownloadableWorkSourceAsync(work.Id, work.MediaType, intent, target.WorkEpisodeId, cancellationToken);
        var estimate = downloadable
            ? await EstimateWorkAsync(target, intentName, new ClientOfflinePackageSelection(), cancellationToken)
            : new ClientOfflinePackageEstimate(ClientApiOfflinePackageContract.UnknownEstimate, null);

        return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Success(new(
            ClientApiContract.ApiVersion,
            target with { Kind = ClientApiOfflinePackageContract.WorkTarget },
            intentName,
            category,
            work.CanonicalTitle,
            downloadable,
            downloadable ? null : "offline_source_unavailable",
            scopes,
            units,
            editions,
            qualities,
            [],
            tracks.Audio,
            tracks.Subtitles,
            LearningAvailable: false,
            estimate));
    }

    private async Task<ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>> GetGameOptionsAsync(
        ClientOfflinePackageTarget target,
        ClientOfflinePackageIntent intent,
        CancellationToken cancellationToken)
    {
        if (intent != ClientOfflinePackageIntent.Play)
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Unavailable(
                "offline_not_supported",
                "Games use the play Offline intent.");
        }

        var game = await db.Games.AsNoTracking()
            .Where(x => x.Id == target.GameId!.Value)
            .Select(x => new { x.Id, x.CanonicalTitle })
            .SingleOrDefaultAsync(cancellationToken);
        if (game is null)
        {
            return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.NotFound(
                "offline_target_not_found",
                "The canonical Game does not exist.");
        }

        if (target.GameReleaseId is { } targetReleaseId)
        {
            var validRelease = await db.GameReleases.AsNoTracking().AnyAsync(x => x.Id == targetReleaseId && x.GameId == game.Id, cancellationToken);
            if (!validRelease)
            {
                return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Invalid(
                    "invalid_offline_target",
                    "The selected Game release does not belong to the canonical Game.");
            }
        }

        var releases = await (
                from release in db.GameReleases.AsNoTracking()
                join platform in db.GamePlatforms.AsNoTracking() on release.GamePlatformId equals platform.Id
                where release.GameId == game.Id
                orderby platform.DisplayName, release.Region, release.Version
                select new
                {
                    release.Id,
                    Platform = platform.DisplayName,
                    release.Region,
                    release.Version
                })
            .ToListAsync(cancellationToken);

        var units = releases.Select(x =>
        {
            var details = new[] { x.Region, x.Version }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            var title = details.Length == 0 ? x.Platform : $"{x.Platform} · {string.Join(" · ", details)}";
            return new ClientOfflineUnitOption(x.Id, "release", title);
        }).ToArray();

        var selectedReleases = target.GameReleaseId is { } releaseId
            ? new[] { releaseId }
            : releases.Count == 1
                ? new[] { releases[0].Id }
                : Array.Empty<Guid>();
        var downloadable = selectedReleases.Length > 0
            ? await db.GameReleaseFiles.AsNoTracking().AnyAsync(x => selectedReleases.Contains(x.GameReleaseId), cancellationToken)
            : await (
                    from file in db.GameReleaseFiles.AsNoTracking()
                    join release in db.GameReleases.AsNoTracking() on file.GameReleaseId equals release.Id
                    where release.GameId == game.Id
                    select file.Id)
                .AnyAsync(cancellationToken);
        var estimate = await EstimateGameAsync(target, new ClientOfflinePackageSelection(UnitIds: selectedReleases), cancellationToken);

        return ClientOfflinePackageQueryResult<ClientOfflinePackageOptions>.Success(new(
            ClientApiContract.ApiVersion,
            target with { Kind = ClientApiOfflinePackageContract.GameTarget },
            ClientApiOfflinePackageContract.PlayIntent,
            ClientApiOfflinePackageContract.GameCategory,
            game.CanonicalTitle,
            downloadable,
            downloadable ? null : "offline_source_unavailable",
            [new ClientOfflineScopeOption(ClientApiOfflinePackageContract.ReleaseScope, IsDefault: true)],
            units,
            [],
            [],
            [],
            [],
            [],
            LearningAvailable: false,
            estimate));
    }

    private async Task<(ClientOfflineTrackOption[] Audio, ClientOfflineTrackOption[] Subtitles)> GetSingleVideoTracksAsync(
        Guid workId,
        Guid? workEpisodeId,
        CancellationToken cancellationToken)
    {
        var fileIds = await (
                from asset in db.MediaAssets.AsNoTracking()
                join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
                where asset.WorkId == workId
                      && asset.WorkEpisodeId == workEpisodeId
                      && asset.Kind == MediaAssetKind.Video
                orderby file.Id
                select file.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (fileIds.Count != 1)
        {
            return ([], []);
        }

        var tracks = await db.MediaTracks.AsNoTracking()
            .Where(x => x.MediaFileId == fileIds[0] && (x.Kind == MediaTrackKind.Audio || x.Kind == MediaTrackKind.Subtitle))
            .OrderBy(x => x.StreamIndex)
            .ToListAsync(cancellationToken);

        var audio = tracks
            .Where(x => x.Kind == MediaTrackKind.Audio)
            .Select(x => new ClientOfflineTrackOption(
                PlaybackTrackIds.Format(x.StreamIndex),
                "audio",
                x.Language,
                x.Title,
                x.IsDefault,
                x.IsForced))
            .ToArray();
        var subtitles = tracks
            .Where(x => x.Kind == MediaTrackKind.Subtitle)
            .Select(x => new ClientOfflineTrackOption(
                PlaybackTrackIds.Format(x.StreamIndex),
                "subtitle",
                x.Language,
                x.Title,
                x.IsDefault,
                x.IsForced))
            .ToArray();

        return (audio, subtitles);
    }

    private async Task<bool> HasDownloadableWorkSourceAsync(
        Guid workId,
        WorkMediaType mediaType,
        ClientOfflinePackageIntent intent,
        Guid? workEpisodeId,
        CancellationToken cancellationToken)
    {
        if (intent == ClientOfflinePackageIntent.Watch)
        {
            if (workEpisodeId is { } episodeId)
            {
                return await db.MediaAssets.AsNoTracking().AnyAsync(
                    x => x.WorkId == workId && x.WorkEpisodeId == episodeId && x.Kind == MediaAssetKind.Video,
                    cancellationToken);
            }

            return mediaType == WorkMediaType.Movie
                ? await db.MediaAssets.AsNoTracking().AnyAsync(x => x.WorkId == workId && x.WorkEpisodeId == null && x.Kind == MediaAssetKind.Video, cancellationToken)
                : await db.MediaAssets.AsNoTracking().AnyAsync(x => x.WorkId == workId && x.Kind == MediaAssetKind.Video, cancellationToken);
        }

        if (intent == ClientOfflinePackageIntent.Read)
        {
            var hasCanonicalReadingSource = await db.MediaAssets.AsNoTracking().AnyAsync(
                x => x.WorkId == workId && (x.Kind == MediaAssetKind.Ebook || x.Kind == MediaAssetKind.ComicArchive || x.Kind == MediaAssetKind.Image),
                cancellationToken);
            if (hasCanonicalReadingSource)
            {
                return true;
            }

            return await db.WorkChapters.AsNoTracking().AnyAsync(x => x.WorkId == workId, cancellationToken);
        }

        if (intent == ClientOfflinePackageIntent.Listen)
        {
            var hasCanonicalAudio = await db.MediaAssets.AsNoTracking().AnyAsync(x => x.WorkId == workId && x.Kind == MediaAssetKind.Audio, cancellationToken);
            if (hasCanonicalAudio)
            {
                return true;
            }

            var audiobookIds = db.WorkSourceLinks.AsNoTracking()
                .Where(x => x.WorkId == workId && x.SourceKind == WorkSourceKind.Audiobook)
                .Select(x => x.SourceId);
            return await db.AudiobookFiles.AsNoTracking().AnyAsync(x => audiobookIds.Contains(x.AudiobookId), cancellationToken);
        }

        return false;
    }

    private async Task<ClientOfflinePackageEstimate> EstimateWorkAsync(
        ClientOfflinePackageTarget target,
        string intentValue,
        ClientOfflinePackageSelection selection,
        CancellationToken cancellationToken)
    {
        if (!ClientApiOfflinePackageContract.TryParseIntent(intentValue, out var intent) || target.WorkId is not { } workId)
        {
            return new(ClientApiOfflinePackageContract.UnknownEstimate, null);
        }

        if (intent == ClientOfflinePackageIntent.Read && (selection.UnitIds?.Count ?? 0) > 0)
        {
            return new(ClientApiOfflinePackageContract.UnknownEstimate, null);
        }

        if (intent == ClientOfflinePackageIntent.Listen)
        {
            var canonical = await ExactAssetSizeAsync(workId, null, MediaAssetKind.Audio, selection.Quality, cancellationToken);
            if (canonical is not null)
            {
                return new(ClientApiOfflinePackageContract.KnownEstimate, canonical);
            }

            var audiobookIds = await db.WorkSourceLinks.AsNoTracking()
                .Where(x => x.WorkId == workId && x.SourceKind == WorkSourceKind.Audiobook)
                .Select(x => x.SourceId)
                .ToListAsync(cancellationToken);
            if (audiobookIds.Count != 1)
            {
                return new(ClientApiOfflinePackageContract.UnknownEstimate, null);
            }

            var sizes = await db.AudiobookFiles.AsNoTracking()
                .Where(x => x.AudiobookId == audiobookIds[0])
                .Select(x => x.SizeBytes)
                .ToListAsync(cancellationToken);
            return sizes.Count == 0
                ? new(ClientApiOfflinePackageContract.UnknownEstimate, null)
                : new(ClientApiOfflinePackageContract.KnownEstimate, sizes.Sum());
        }

        if (intent == ClientOfflinePackageIntent.Read)
        {
            var exact = await ExactReadableDocumentSizeAsync(workId, selection.Quality, cancellationToken);
            return exact is null
                ? new(ClientApiOfflinePackageContract.UnknownEstimate, null)
                : new(ClientApiOfflinePackageContract.KnownEstimate, exact);
        }

        var unitIds = selection.UnitIds?.Where(x => x != Guid.Empty).Distinct().ToArray() ?? [];
        if (unitIds.Length == 0 && target.WorkEpisodeId is { } targetEpisodeId)
        {
            unitIds = [targetEpisodeId];
        }

        if (unitIds.Length == 0)
        {
            var movieSize = await ExactAssetSizeAsync(workId, null, MediaAssetKind.Video, selection.Quality, cancellationToken);
            return movieSize is null
                ? new(ClientApiOfflinePackageContract.UnknownEstimate, null)
                : new(ClientApiOfflinePackageContract.KnownEstimate, movieSize);
        }

        var rows = await (
                from asset in db.MediaAssets.AsNoTracking()
                join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
                join version in db.WorkVersions.AsNoTracking() on asset.WorkVersionId equals version.Id
                where asset.WorkId == workId
                      && asset.Kind == MediaAssetKind.Video
                      && asset.WorkEpisodeId != null
                      && unitIds.Contains(asset.WorkEpisodeId.Value)
                      && (selection.Quality == null || version.Quality == selection.Quality)
                select new { EpisodeId = asset.WorkEpisodeId!.Value, file.SizeBytes })
            .ToListAsync(cancellationToken);
        var groups = rows.GroupBy(x => x.EpisodeId).ToArray();
        if (groups.Length != unitIds.Length || groups.Any(group => group.Count() != 1))
        {
            return new(ClientApiOfflinePackageContract.UnknownEstimate, null);
        }

        return new(ClientApiOfflinePackageContract.KnownEstimate, groups.Sum(group => group.Single().SizeBytes));
    }

    private async Task<long?> ExactAssetSizeAsync(
        Guid workId,
        Guid? workEpisodeId,
        MediaAssetKind kind,
        string? quality,
        CancellationToken cancellationToken)
    {
        var rows = await (
                from asset in db.MediaAssets.AsNoTracking()
                join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
                join version in db.WorkVersions.AsNoTracking() on asset.WorkVersionId equals version.Id
                where asset.WorkId == workId
                      && asset.WorkEpisodeId == workEpisodeId
                      && asset.Kind == kind
                      && (quality == null || version.Quality == quality)
                select file.SizeBytes)
            .Take(2)
            .ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    private async Task<long?> ExactReadableDocumentSizeAsync(
        Guid workId,
        string? quality,
        CancellationToken cancellationToken)
    {
        var rows = await (
                from asset in db.MediaAssets.AsNoTracking()
                join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
                join version in db.WorkVersions.AsNoTracking() on asset.WorkVersionId equals version.Id
                where asset.WorkId == workId
                      && (asset.Kind == MediaAssetKind.Ebook || asset.Kind == MediaAssetKind.ComicArchive)
                      && (quality == null || version.Quality == quality)
                select file.SizeBytes)
            .Take(2)
            .ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    private async Task<ClientOfflinePackageEstimate> EstimateGameAsync(
        ClientOfflinePackageTarget target,
        ClientOfflinePackageSelection selection,
        CancellationToken cancellationToken)
    {
        var releaseIds = selection.UnitIds?.Where(x => x != Guid.Empty).Distinct().ToArray() ?? [];
        if (releaseIds.Length == 0 && target.GameReleaseId is { } targetReleaseId)
        {
            releaseIds = [targetReleaseId];
        }

        if (releaseIds.Length == 0 && target.GameId is { } gameId)
        {
            var releases = await db.GameReleases.AsNoTracking()
                .Where(x => x.GameId == gameId)
                .Select(x => x.Id)
                .Take(2)
                .ToListAsync(cancellationToken);
            if (releases.Count == 1)
            {
                releaseIds = [releases[0]];
            }
        }

        if (releaseIds.Length == 0)
        {
            return new(ClientApiOfflinePackageContract.UnknownEstimate, null);
        }

        var validCount = await db.GameReleases.AsNoTracking()
            .CountAsync(x => x.GameId == target.GameId!.Value && releaseIds.Contains(x.Id), cancellationToken);
        if (validCount != releaseIds.Length)
        {
            return new(ClientApiOfflinePackageContract.UnknownEstimate, null);
        }

        var sizes = await db.GameReleaseFiles.AsNoTracking()
            .Where(x => releaseIds.Contains(x.GameReleaseId))
            .Select(x => x.SizeBytes)
            .ToListAsync(cancellationToken);
        return sizes.Count == 0
            ? new(ClientApiOfflinePackageContract.UnknownEstimate, null)
            : new(ClientApiOfflinePackageContract.KnownEstimate, sizes.Sum());
    }

    private static bool Supports(WorkMediaType mediaType, ClientOfflinePackageIntent intent) =>
        intent switch
        {
            ClientOfflinePackageIntent.Watch => mediaType is WorkMediaType.Movie or WorkMediaType.Series or WorkMediaType.Anime,
            ClientOfflinePackageIntent.Read => mediaType is WorkMediaType.Book or WorkMediaType.Manga or WorkMediaType.LightNovel,
            ClientOfflinePackageIntent.Listen => mediaType == WorkMediaType.Book,
            ClientOfflinePackageIntent.Play => false,
            _ => false
        };

    private static InstanceModule RequiredModule(WorkMediaType mediaType, ClientOfflinePackageIntent intent) =>
        intent == ClientOfflinePackageIntent.Listen && mediaType == WorkMediaType.Book
            ? InstanceModule.Audiobook
            : InstanceModuleMedia.For(mediaType);

    private static string Category(ClientOfflinePackageIntent intent) =>
        intent switch
        {
            ClientOfflinePackageIntent.Watch => ClientApiOfflinePackageContract.VideoCategory,
            ClientOfflinePackageIntent.Read => ClientApiOfflinePackageContract.ReadingCategory,
            ClientOfflinePackageIntent.Listen => ClientApiOfflinePackageContract.AudioCategory,
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null)
        };

    private static bool ValidQuality(string? selected, IReadOnlyList<ClientOfflineQualityOption> available) =>
        string.IsNullOrWhiteSpace(selected) || available.Any(x => string.Equals(x.Key, selected.Trim(), StringComparison.Ordinal));

    private static bool ValidTracks(IReadOnlyList<string>? selected, IReadOnlyList<ClientOfflineTrackOption> available)
    {
        if (selected is null || selected.Count == 0)
        {
            return true;
        }

        var allowed = available.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        return selected.Where(x => !string.IsNullOrWhiteSpace(x)).All(x => allowed.Contains(x.Trim()));
    }

    private static string? NormalizeOptionalKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> NormalizeIds(IReadOnlyList<string>? values) =>
        values is null
            ? []
            : values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
}
