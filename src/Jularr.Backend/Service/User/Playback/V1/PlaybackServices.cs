using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Nas;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Playback.V1
{
    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}")]
    public sealed class ReadPlaybackItemV1 : UserService<MediaAssetParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackItemV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(MediaAssetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Canonical identity and this profile's resume point.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    asset."Id" AS "MediaAssetId",
                    work."Id" AS "WorkId",
                    visible."Title" AS "CanonicalTitle",
                    asset."MediaAssetTypeId",
                    unit."WorkUnitTypeId",
                    unit."Number" AS "UnitNumber",
                    parent."Number" AS "ParentUnitNumber",
                    unit."Title" AS "UnitTitle",
                    progress."PositionMs" AS "ResumePositionMs",
                    (
                        SELECT
                            other."Id"
                        FROM
                            "MediaAssets" AS other
                            JOIN "WorkUnits" AS otherUnit ON otherUnit."Id" = other."WorkUnitId"
                            LEFT JOIN "WorkUnits" AS otherParent ON otherParent."Id" = otherUnit."ParentWorkUnitId"
                        WHERE
                            other."WorkId" = asset."WorkId"
                            AND other."MediaAssetTypeId" = asset."MediaAssetTypeId"
                            AND (coalesce(otherParent."Number", 0), otherUnit."Number") < (coalesce(parent."Number", 0), unit."Number")
                            AND EXISTS (
                                SELECT
                                    1
                                FROM
                                    "StoredFiles" AS file
                                WHERE
                                    file."MediaAssetId" = other."Id"
                                    AND file."MissingSince" IS NULL
                                    AND NOT file."IsSubtitle"
                            )
                        ORDER BY
                            coalesce(otherParent."Number", 0) DESC,
                            otherUnit."Number" DESC,
                            other."WorkVersionId" = asset."WorkVersionId" DESC,
                            other."Id"
                        LIMIT 1
                    ) AS "PreviousMediaAssetId",
                    (
                        SELECT
                            other."Id"
                        FROM
                            "MediaAssets" AS other
                            JOIN "WorkUnits" AS otherUnit ON otherUnit."Id" = other."WorkUnitId"
                            LEFT JOIN "WorkUnits" AS otherParent ON otherParent."Id" = otherUnit."ParentWorkUnitId"
                        WHERE
                            other."WorkId" = asset."WorkId"
                            AND other."MediaAssetTypeId" = asset."MediaAssetTypeId"
                            AND (coalesce(otherParent."Number", 0), otherUnit."Number") > (coalesce(parent."Number", 0), unit."Number")
                            AND EXISTS (
                                SELECT
                                    1
                                FROM
                                    "StoredFiles" AS file
                                WHERE
                                    file."MediaAssetId" = other."Id"
                                    AND file."MissingSince" IS NULL
                                    AND NOT file."IsSubtitle"
                            )
                        ORDER BY
                            coalesce(otherParent."Number", 0),
                            otherUnit."Number",
                            other."WorkVersionId" = asset."WorkVersionId" DESC,
                            other."Id"
                        LIMIT 1
                    ) AS "NextMediaAssetId"
                FROM
                    "MediaAssets" AS asset
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                        AND progress."WorkId" = asset."WorkId"
                        AND progress."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                WHERE
                    asset."Id" = @MediaAssetId;
                """;
            sql.Parameters.Add(new PlaybackSourceParameters(Parameters.MediaAssetId, Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadRequiredAsync<PlaybackItemV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}/tracks")]
    public sealed class ReadPlaybackTracksV1 : UserService<MediaAssetParametersV1>
    {
        private readonly PlaybackSubtitles _subtitles;

        internal ReadPlaybackTracksV1(PlaybackSubtitles subtitles)
        {
            _subtitles = subtitles;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<PlaybackTrackV1>>()];
        protected override ModuleRequirement GetInstanceModules(MediaAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(MediaAssetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The played file's audio and subtitle streams, then subtitle files beside it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH media AS (
                    SELECT
                        file."Id"
                    FROM
                        "MediaAssets" AS asset
                        JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                    WHERE
                        asset."Id" = @MediaAssetId
                        AND file."MissingSince" IS NULL
                        AND NOT file."IsSubtitle"
                    ORDER BY
                        file."Id"
                    LIMIT 1
                )
                SELECT
                    track."StreamIndex",
                    NULL::bigint AS "StoredFileId",
                    NULL::varchar AS "RelativePath",
                    track."MediaTrackTypeId",
                    track."Codec",
                    track."Language",
                    track."Title",
                    track."Channels",
                    track."IsDefault",
                    track."IsForced"
                FROM
                    media
                    JOIN "MediaTracks" AS track ON track."StoredFileId" = media."Id"
                WHERE
                    track."MediaTrackTypeId" IN (2, 3)
                UNION ALL
                SELECT
                    NULL,
                    sidecar."Id",
                    sidecar."RelativePath",
                    3,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    FALSE,
                    FALSE
                FROM
                    media
                    JOIN "StoredFiles" AS sidecar ON sidecar."MediaAssetId" = @MediaAssetId
                WHERE
                    sidecar."MissingSince" IS NULL
                    AND sidecar."IsSubtitle"
                ORDER BY
                    4,
                    1 NULLS LAST,
                    2;
                """;
            sql.Parameters.Add(new PlaybackSourceParameters(Parameters.MediaAssetId, Auth.ProfileId!.Value));
            var rows = await Context.ReadSql.ReadListAsync<PlaybackTrackRowData>(sql, CancellationToken);
            var described = rows.Select(row =>
            {
                if (row.StoredFileId is { } fileId)
                {
                    var (language, isForced) = PlaybackTracks.Describe(row.RelativePath!);
                    var supported = PlaybackTracks.IsTextFile(row.RelativePath!) && (_subtitles.CanConvert || row.RelativePath!.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                        || row.RelativePath!.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase));
                    return new PlaybackTrackV1(PlaybackTracks.FileKey(fileId), MediaTrackType.Subtitle, language, null, null, false, isForced, supported, false);
                }

                // Another audio track and embedded text subtitles need FFmpeg.
                var playable = row.MediaTrackTypeId == MediaTrackType.Audio ? row.IsDefault || _subtitles.CanConvert : PlaybackTracks.IsText(row.Codec) && _subtitles.CanConvert;
                return new PlaybackTrackV1(PlaybackTracks.StreamKey(row.StreamIndex!.Value), row.MediaTrackTypeId, row.Language, row.Title, row.Channels, row.IsDefault, row.IsForced, playable, false);
            }).ToList();

            // The profile's preferences pick one audio and at most one subtitle track among the playable ones.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    preference."AudioLanguage",
                    preference."SubtitleLanguage",
                    coalesce(preference."SubtitleModeTypeId", 4)::smallint AS "SubtitleModeTypeId",
                    CASE
                        WHEN lower(split_part(locale."Locale", '-', 1)) ~ '^[a-z]{2}$' THEN lower(split_part(locale."Locale", '-', 1))
                        ELSE 'en'
                    END AS "ProfileLanguage"
                FROM
                    "Profiles" AS profile
                    JOIN "UiLocales" AS locale ON locale."Id" = profile."UiLocaleId"
                    LEFT JOIN "ProfilePlaybackPreferences" AS preference ON preference."ProfileId" = profile."Id"
                WHERE
                    profile."Id" = @ProfileId;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var preference = await Context.ReadSql.ReadRequiredAsync<PlaybackChoiceData>(sql, CancellationToken);
            var choices = described.Where(track => track.IsSupported)
                .Select(track => new PlaybackChoiceTrack(track.Key, track.MediaTrackTypeId, track.Language, track.IsDefault, track.IsForced)).ToList();
            var audio = PlaybackTracks.ChooseAudio(choices, preference.AudioLanguage);
            var subtitle = PlaybackTracks.ChooseSubtitle(choices, preference, audio);
            return new BaseDataList<PlaybackTrackV1>([.. described.Select(track => new PlaybackTrackV1(track.Key, track.MediaTrackTypeId, track.Language, track.Title, track.Channels,
                track.IsDefault, track.IsForced, track.IsSupported, track.Key == audio?.Key || track.Key == subtitle?.Key))]);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}/subtitles/{key}")]
    public sealed class ReadPlaybackSubtitleV1 : UserService<PlaybackSubtitleParametersV1>
    {
        private readonly StorageAccess _storage;
        private readonly PlaybackSubtitles _subtitles;

        internal ReadPlaybackSubtitleV1(StorageAccess storage, PlaybackSubtitles subtitles)
        {
            _storage = storage;
            _subtitles = subtitles;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackSubtitleV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSubtitleParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSubtitleParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // A subtitle file of the asset, or a text stream of its played file.
            var (streamIndex, fileId) = PlaybackTracks.ParseKey(Parameters.Key);
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id" AS "StoredFileId",
                    file."RelativePath",
                    file."ModifiedAt",
                    root."RelativePath" AS "RootRelativePath",
                    mount."Id" AS "StorageMountId",
                    track."Codec"
                FROM
                    "MediaAssets" AS asset
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                    LEFT JOIN "MediaTracks" AS track ON track."StoredFileId" = file."Id" AND track."StreamIndex" = @StreamIndex AND track."MediaTrackTypeId" = 3
                WHERE
                    asset."Id" = @MediaAssetId
                    AND file."MissingSince" IS NULL
                    AND (file."Id" = @StoredFileId AND file."RelativePath" ~* '\.(srt|vtt|ass|ssa)$'
                        OR @StoredFileId::bigint IS NULL AND track."StreamIndex" IS NOT NULL AND file."Id" = (
                            SELECT
                                media."Id"
                            FROM
                                "StoredFiles" AS media
                            WHERE
                                media."MediaAssetId" = asset."Id"
                                AND media."MissingSince" IS NULL
                                AND NOT media."IsSubtitle"
                            ORDER BY
                                media."Id"
                            LIMIT 1
                        ));
                """;
            sql.Parameters.Add(new PlaybackSubtitleLookupData(Parameters.MediaAssetId, Auth.ProfileId!.Value, streamIndex, fileId));
            var source = await Context.ReadSql.ReadOptionalAsync<PlaybackSubtitleSourceData>(sql, CancellationToken) ?? throw new KeyNotFoundException("The subtitle was not found.");
            if (streamIndex is not null && !PlaybackTracks.IsText(source.Codec))
            {
                throw new KeyNotFoundException("The subtitle was not found.");
            }

            var files = _storage.Open(await Context.ReadStorageMountAsync(source.StorageMountId, CancellationToken));
            var path = StoragePath.Combine(source.RootRelativePath, source.RelativePath);
            var cacheKey = source.StoredFileId.ToString(CultureInfo.InvariantCulture) + "-" + (streamIndex is { } stream ? PlaybackTracks.StreamKey(stream) : "file") + "-"
                + source.ModifiedAt.Ticks.ToString(CultureInfo.InvariantCulture);
            return new PlaybackSubtitleV1(await _subtitles.ReadAsync(files, path, streamIndex, cacheKey, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/playback/assets/{mediaAssetId:long}/sessions")]
    public sealed class StartPlaybackV1 : UserService<MediaAssetParametersV1, StartPlaybackDataV1>
    {
        private readonly StorageAccess _storage;
        private readonly WakeOnLanClient _wake;
        private readonly PlaybackTranscodes _transcodes;

        internal StartPlaybackV1(StorageAccess storage, WakeOnLanClient wake, PlaybackTranscodes transcodes)
        {
            _storage = storage;
            _wake = wake;
            _transcodes = transcodes;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackPlanV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(MediaAssetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Stored source, streams and resume point.
            var lookup = new PlaybackSourceParameters(Parameters.MediaAssetId, Auth.ProfileId!.Value, Auth.SessionPublicId);
            var (source, tracks, preference) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        file."Id" AS "StoredFileId",
                        file."RelativePath",
                        root."RelativePath" AS "RootRelativePath",
                        mount."Id" AS "StorageMountId",
                        mount."WakeMacAddress",
                        mount."WakeBroadcastAddress",
                        mount."WakeOnDemand",
                        file."DurationMs",
                        progress."PositionMs" AS "ResumePositionMs",
                        file."BitRate",
                        COALESCE((
                            SELECT
                                session."PlaybackQuality"
                            FROM
                                "AccountSessions" AS session
                            WHERE
                                session."PublicId" = @SessionPublicId
                        ), 1) AS "PlaybackQuality"
                    FROM
                        "MediaAssets" AS asset
                        JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                        JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                        JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                        LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                            AND progress."WorkId" = asset."WorkId"
                            AND progress."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                    WHERE
                        asset."Id" = @MediaAssetId
                        AND file."MissingSince" IS NULL
                        AND NOT file."IsSubtitle"
                    ORDER BY
                        file."Id"
                    LIMIT 1;
                    """;
                sql.Parameters.Add(lookup);
                var source = await ctx.ReadSql.ReadOptionalAsync<PlaybackSourceData>(sql, ct) ?? throw new KeyNotFoundException("No playable file is available.");

                sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        track."StreamIndex",
                        track."MediaTrackTypeId",
                        track."Codec",
                        track."IsDefault",
                        track."Language",
                        track."Height"
                    FROM
                        "MediaTracks" AS track
                    WHERE
                        track."StoredFileId" = @Id
                    ORDER BY
                        track."StreamIndex";
                    """;
                sql.Parameters.Add(new StoredFileIdData(source.StoredFileId));
                var fileTracks = await ctx.ReadSql.ReadListAsync<PlaybackTrackData>(sql, ct);

                // The profile's language choices.
                sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        preference."AudioLanguage",
                        preference."SubtitleLanguage",
                        coalesce(preference."SubtitleModeTypeId", 4)::smallint AS "SubtitleModeTypeId",
                        CASE
                            WHEN lower(split_part(locale."Locale", '-', 1)) ~ '^[a-z]{2}$' THEN lower(split_part(locale."Locale", '-', 1))
                            ELSE 'en'
                        END AS "ProfileLanguage"
                    FROM
                        "Profiles" AS profile
                        JOIN "UiLocales" AS locale ON locale."Id" = profile."UiLocaleId"
                        LEFT JOIN "ProfilePlaybackPreferences" AS preference ON preference."ProfileId" = profile."Id"
                    WHERE
                        profile."Id" = @ProfileId;
                    """;
                sql.Parameters.Add(new ProfileParameters(lookup.ProfileId));
                return (source, fileTracks, await ctx.ReadSql.ReadRequiredAsync<PlaybackChoiceData>(sql, ct));
            }, CancellationToken);

            // Delivery follows the client's actual capabilities and the chosen audio track.
            if (Data.AudioStreamIndex is { } requested && !tracks.Any(track => track.StreamIndex == requested && track.MediaTrackTypeId == MediaTrackType.Audio))
            {
                throw new DataValidationException(nameof(Data.AudioStreamIndex), "unknown_reference");
            }

            var containers = Data.Containers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var videoCodecs = Data.VideoCodecs.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var audioCodecs = Data.AudioCodecs.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Another audio track than the file's default needs a conversion; without a request the profile's language decides among the playable ones.
            var canConvert = _transcodes.IsAvailable && source.DurationMs is not null && containers.Contains("hls");
            var audioStreamIndex = Data.AudioStreamIndex;
            if (audioStreamIndex is null)
            {
                var playable = tracks.Where(track => track.MediaTrackTypeId == MediaTrackType.Audio && (track.IsDefault || canConvert))
                    .Select(track => new PlaybackChoiceTrack(PlaybackTracks.StreamKey(track.StreamIndex), track.MediaTrackTypeId, track.Language, track.IsDefault, false)).ToList();
                audioStreamIndex = PlaybackTracks.ChooseAudio(playable, preference.AudioLanguage) is { } preferred ? PlaybackTracks.ParseKey(preferred.Key).StreamIndex : null;
            }

            // The device's quality, or this playback's own, caps what is converted.
            var quality = Data.Quality ?? source.PlaybackQuality;
            var target = PlaybackLogic.QualityTarget(quality);
            var direct = PlaybackLogic.CanPlayDirectly(LibraryFileTypes.GetStream(source.RelativePath).Container, tracks, containers, videoCodecs, audioCodecs, audioStreamIndex, target, source.BitRate);

            // A device asking for smaller music converts it when it can; otherwise the original plays.
            var kilobits = _transcodes.IsAvailable && source.DurationMs is not null ? PlaybackLogic.AudioKilobits(target, tracks, source.BitRate) : null;
            var smaller = kilobits is null ? null : PlaybackLogic.PlanConversion(tracks, containers, videoCodecs, audioCodecs, audioStreamIndex, kilobits, target, source.BitRate);
            direct &= smaller is null;
            var conversion = direct ? null : smaller ?? PlaybackLogic.PlanConversion(tracks, containers, videoCodecs, audioCodecs, audioStreamIndex, null, target, source.BitRate);
            if (!direct && (conversion is null || source.DurationMs is null || !_transcodes.IsAvailable))
            {
                throw new ServiceUnavailableException("playback_conversion_unavailable");
            }

            // Sleeping storage wakes only when the Owner allowed it.
            var path = StoragePath.Combine(source.RootRelativePath, source.RelativePath);
            StorageFiles files;
            try
            {
                var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(source.StorageMountId, ct), CancellationToken);
                files = _storage.Open(mount);
                if (await files.GetAsync(path, CancellationToken) is not { IsDirectory: false })
                {
                    throw new NasMountException("storage_unavailable");
                }
            }
            catch (NasMountException) when (source is { WakeOnDemand: true, WakeMacAddress: { } macAddress })
            {
                await _wake.SendAsync(macAddress, source.WakeBroadcastAddress, CancellationToken);
                throw new ServiceUnavailableException("storage_waking");
            }

            var resume = source.ResumePositionMs ?? 0;
            var mode = conversion?.PlaybackModeTypeId ?? PlaybackModeType.DirectPlay;
            var sessionId = await Context.ExecuteBatchAsync((ctx, ct) =>
                ctx.CreatePlaybackSessionAsync(new PlaybackSessionCreationData(lookup.ProfileId, Parameters.MediaAssetId, source.StoredFileId, mode, resume), ct), CancellationToken);

            // Conversions stream on-demand HLS segments.
            var url = "/api/v1/playback/sessions/" + sessionId.ToString("D");
            if (conversion is { } plan)
            {
                _transcodes.Register(new PlaybackTranscodeSource(sessionId, lookup.ProfileId, _storage.Input(files, path), plan.VideoStreamIndex, plan.AudioStreamIndex, plan.CopyVideo, plan.CopyAudio,
                    source.DurationMs!.Value, plan.AudioKilobits, plan.MaxHeight, plan.VideoKilobits));
                return new PlaybackPlanV1(sessionId, mode, url + "/playlist.m3u8", resume, source.DurationMs, audioStreamIndex, quality);
            }

            return new PlaybackPlanV1(sessionId, mode, url + "/stream", resume, source.DurationMs, audioStreamIndex, quality);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/sessions/{playbackSessionId:guid}/stream")]
    public sealed class ReadPlaybackStreamV1 : UserService<PlaybackSessionParametersV1>
    {
        private readonly StorageAccess _storage;

        internal ReadPlaybackStreamV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackStreamV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Only the owning profile's active session streams.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."RelativePath",
                    root."RelativePath" AS "RootRelativePath",
                    mount."Id" AS "StorageMountId"
                FROM
                    "PlaybackSessions" AS session
                    JOIN "StoredFiles" AS file ON file."Id" = session."StoredFileId"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    session."PublicId" = @PlaybackSessionId
                    AND session."ProfileId" = @ProfileId
                    AND session."EndedAt" IS NULL;
                """;
            sql.Parameters.Add(new PlaybackSessionLookupData(Parameters.PlaybackSessionId, Auth.ProfileId!.Value));
            var stream = await Context.ReadSql.ReadRequiredAsync<PlaybackStreamData>(sql, CancellationToken);

            // The HTTP adapter streams it with ranges.
            var files = _storage.Open(await Context.ReadStorageMountAsync(stream.StorageMountId, CancellationToken));
            var content = await files.OpenReadAsync(StoragePath.Combine(stream.RootRelativePath, stream.RelativePath), CancellationToken);
            return new PlaybackStreamV1(content, LibraryFileTypes.GetStream(stream.RelativePath).ContentType);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/sessions/{playbackSessionId:guid}/playlist.m3u8")]
    public sealed class ReadPlaybackPlaylistV1 : UserService<PlaybackSessionParametersV1>
    {
        private readonly PlaybackTranscodes _transcodes;

        internal ReadPlaybackPlaylistV1(PlaybackTranscodes transcodes)
        {
            _transcodes = transcodes;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackPlaylistV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override Task<IServiceOutput> OnRead()
        {
            // Only the planning profile's conversion is listed.
            var source = _transcodes.Find(Parameters.PlaybackSessionId, Auth.ProfileId!.Value) ?? throw new KeyNotFoundException("The playback session is not active.");
            return Task.FromResult<IServiceOutput>(new PlaybackPlaylistV1(PlaybackTranscodes.BuildPlaylist(source)));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/sessions/{playbackSessionId:guid}/segments/{index:int}.ts")]
    public sealed class ReadPlaybackSegmentV1 : UserService<PlaybackSegmentParametersV1>
    {
        private readonly PlaybackTranscodes _transcodes;

        internal ReadPlaybackSegmentV1(PlaybackTranscodes transcodes)
        {
            _transcodes = transcodes;
        }

        // Segments of an authorized session wait without database work.
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackStreamV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSegmentParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSegmentParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            var path = await _transcodes.GetSegmentAsync(Parameters.PlaybackSessionId, Auth.ProfileId!.Value, Parameters.Index, CancellationToken);
            return new PlaybackStreamV1(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous), "video/mp2t");
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/playback/sessions/{playbackSessionId:guid}")]
    public sealed class UpdatePlaybackV1 : UserService<PlaybackSessionParametersV1, UpdatePlaybackDataV1>
    {
        private readonly PlaybackTranscodes _transcodes;

        internal UpdatePlaybackV1(PlaybackTranscodes transcodes)
        {
            _transcodes = transcodes;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var data = new PlaybackUpdateData(Parameters.PlaybackSessionId, Auth.ProfileId!.Value, Data.PositionMs, Data.DurationMs, Data.IsEnded, Data.IsCompleted);
            await Context.UpdatePlaybackAsync(data, CancellationToken);

            return new UpdateResult(true);
        }

        protected override void OnCommitted()
        {
            // The conversion must survive any rollback of the session update.
            if (Data.IsEnded) _transcodes.Stop(Parameters.PlaybackSessionId);
        }
    }
}
