using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // Everything the management page of one title shows: its facts, its seasons and episodes with where they stand, and its video files graded against the profile of the title.
    [ServiceEndpoint("GET", "/api/v1/admin/works/{workId:long}/management")]
    public sealed class ReadWorkManagementV1 : AdminService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkManagementV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Facts
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    wanted."Id" AS "WantedItemId",
                    work."MediaTypeId",
                    work."MediaSubtypeId",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."RuntimeMinutes"::integer AS "RuntimeMinutes",
                    work."ReleasedOn",
                    facts."Overview",
                    facts."Genres",
                    facts."Studio",
                    facts."PosterUrl",
                    facts."BackdropUrl"
                FROM
                    "Works" AS work
                    LEFT JOIN LATERAL (
                        SELECT
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Overview'))[1] AS "Overview",
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Genres'))[1] AS "Genres",
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Studio'))[1] AS "Studio",
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Poster'))[1] AS "PosterUrl",
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Backdrop'))[1] AS "BackdropUrl"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                    ) AS facts ON TRUE
                    LEFT JOIN LATERAL (
                        SELECT
                            target."Id"
                        FROM
                            "WantedItems" AS target
                        WHERE
                            target."WorkId" = work."Id"
                        ORDER BY
                            target."Id"
                        LIMIT 1
                    ) AS wanted ON TRUE
                WHERE
                    work."Id" = @WorkId;
                """;
            sql.Parameters.Add(Parameters);
            var hero = await Context.ReadSql.ReadOptionalAsync<WorkManagementHeroData>(sql, CancellationToken) ?? throw new KeyNotFoundException("The title does not exist.");

            // Units
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    unit."Id",
                    unit."ParentWorkUnitId",
                    unit."WorkUnitTypeId",
                    unit."Number",
                    unit."Title",
                    unit."ReleasedOn",
                    CASE
                        WHEN unit."WorkUnitTypeId" = 1 THEN EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkUnits" AS episode
                            WHERE
                                episode."ParentWorkUnitId" = unit."Id"
                                AND episode."WorkUnitTypeId" = 2
                                AND episode."IsMonitored"
                        )
                        ELSE unit."IsMonitored"
                    END AS "IsMonitored",
                    CASE
                        WHEN unit."WorkUnitTypeId" = 1 THEN 1
                        WHEN EXISTS (
                            SELECT
                                1
                            FROM
                                "MediaAssets" AS asset
                                JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL AND NOT available."IsSubtitle"
                            WHERE
                                asset."WorkUnitId" = unit."Id"
                        ) THEN 1
                        WHEN EXISTS (
                            SELECT
                                1
                            FROM
                                "WantedItems" AS target
                                JOIN "AcquisitionReleases" AS covering ON covering."WantedItemId" = target."Id"
                                JOIN "Operations" AS download ON download."AcquisitionReleaseId" = covering."Id"
                            WHERE
                                target."WorkId" = unit."WorkId"
                                AND download."OperationStatusTypeId" IN (1, 2)
                                AND covering."ParentUnitNumber" IS NOT DISTINCT FROM parent."Number"
                                AND (covering."UnitStart" IS NULL OR unit."Number" BETWEEN covering."UnitStart" AND covering."UnitEnd")
                        ) THEN 3
                        WHEN (unit."WorkUnitTypeId" = 2 AND (unit."ReleasedOn" IS NULL OR unit."ReleasedOn" > current_date))
                            OR (unit."WorkUnitTypeId" = 3 AND unit."ReleasedOn" > current_date) THEN 4
                        WHEN unit."IsMonitored" THEN 2
                        ELSE 5
                    END AS "WorkUnitStatusId",
                    CASE WHEN unit."WorkUnitTypeId" = 2 AND parent."Number" > 0 THEN (
                        SELECT
                            count(*)::integer
                        FROM
                            "WorkUnits" AS earlier
                            JOIN "WorkUnits" AS season ON season."Id" = earlier."ParentWorkUnitId"
                        WHERE
                            earlier."WorkId" = unit."WorkId"
                            AND earlier."WorkUnitTypeId" = 2
                            AND season."Number" > 0
                            AND (season."Number", earlier."Number") <= (parent."Number", unit."Number")
                    ) END AS "AbsoluteNumber",
                    CASE WHEN unit."WorkUnitTypeId" = 2 THEN (
                        SELECT
                            unit."Number" - entry."EpisodeOffset"
                        FROM
                            "WorkUnitExternalIdentities" AS entry
                        WHERE
                            entry."WorkUnitId" = unit."ParentWorkUnitId"
                            AND entry."Namespace" = 'anilist'
                            AND entry."EpisodeOffset" < unit."Number"
                        ORDER BY
                            entry."EpisodeOffset" DESC
                        LIMIT 1
                    ) END AS "AniListNumber",
                    unit."DiscNumber"::integer AS "DiscNumber"
                FROM
                    "WorkUnits" AS unit
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                WHERE
                    unit."WorkId" = @WorkId
                    AND unit."WorkUnitTypeId" IN (1, 2, 3, 5)
                ORDER BY
                    coalesce(parent."Number", unit."Number") DESC,
                    unit."ParentWorkUnitId" NULLS FIRST,
                    unit."Number" DESC;
                """;
            sql.Parameters.Add(Parameters);
            var units = await Context.ReadSql.ReadListAsync<WorkManagementUnitV1>(sql, CancellationToken);

            // Files
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id",
                    asset."Id" AS "MediaAssetId",
                    asset."WorkUnitId",
                    file."RelativePath",
                    file."SizeBytes",
                    file."QualityKey",
                    file."ReleaseGroup",
                    file."DurationMs",
                    file."MissingSince" IS NOT NULL AS "IsMissing",
                    video."Codec" AS "VideoCodec",
                    video."Height",
                    video."DynamicRange",
                    coalesce(audio."Languages", '{}') AS "AudioLanguages",
                    coalesce(subtitle."Languages", '{}') AS "SubtitleLanguages",
                    evidence."Evidence"::text AS "Evidence",
                    file."PageCount",
                    coalesce(game."GamePlatformId", version."WorkEditionId") AS "GroupId",
                    CASE
                        WHEN game."GamePlatformId" IS NULL OR file."Sha1" IS NULL THEN NULL
                        ELSE EXISTS (
                            SELECT
                                1
                            FROM
                                "GameRoms" AS rom
                                JOIN "GameRomCatalogs" AS catalog ON catalog."Id" = rom."GameRomCatalogId"
                            WHERE
                                rom."Sha1" = file."Sha1"
                                AND catalog."GamePlatformId" = game."GamePlatformId"
                        )
                    END AS "IsRomVerified"
                FROM
                    "MediaAssets" AS asset
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND NOT file."IsSubtitle"
                    JOIN "WorkVersions" AS version ON version."Id" = asset."WorkVersionId"
                    LEFT JOIN "GameReleases" AS game ON game."WorkVersionId" = asset."WorkVersionId"
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    LEFT JOIN "StoredFileFormatEvidence" AS evidence ON evidence."StoredFileId" = file."Id"
                    LEFT JOIN LATERAL (
                        SELECT
                            track."Codec",
                            track."Height",
                            track."DynamicRange"
                        FROM
                            "MediaTracks" AS track
                        WHERE
                            track."StoredFileId" = file."Id"
                            AND track."MediaTrackTypeId" = 1
                        ORDER BY
                            track."StreamIndex"
                        LIMIT 1
                    ) AS video ON TRUE
                    LEFT JOIN LATERAL (
                        SELECT
                            array_agg(DISTINCT track."Language") FILTER (WHERE track."Language" IS NOT NULL) AS "Languages"
                        FROM
                            "MediaTracks" AS track
                        WHERE
                            track."StoredFileId" = file."Id"
                            AND track."MediaTrackTypeId" = 2
                    ) AS audio ON TRUE
                    LEFT JOIN LATERAL (
                        SELECT
                            array_agg(DISTINCT track."Language") FILTER (WHERE track."Language" IS NOT NULL) AS "Languages"
                        FROM
                            "MediaTracks" AS track
                        WHERE
                            track."StoredFileId" = file."Id"
                            AND track."MediaTrackTypeId" = 3
                    ) AS subtitle ON TRUE
                WHERE
                    asset."WorkId" = @WorkId
                ORDER BY
                    coalesce(parent."Number", unit."Number") DESC NULLS LAST,
                    unit."Number" DESC NULLS LAST,
                    file."SizeBytes" DESC,
                    file."Id";
                """;
            sql.Parameters.Add(Parameters);
            var rows = await Context.ReadSql.ReadListAsync<WorkManagementFileData>(sql, CancellationToken);

            // The audio tracks of the files, for a language of their own
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    track."StoredFileId",
                    track."StreamIndex",
                    track."Codec",
                    track."Channels",
                    track."Language",
                    track."Title"
                FROM
                    "MediaTracks" AS track
                    JOIN "StoredFiles" AS file ON file."Id" = track."StoredFileId"
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                WHERE
                    asset."WorkId" = @WorkId
                    AND track."MediaTrackTypeId" = 2
                ORDER BY
                    track."StoredFileId",
                    track."StreamIndex";
                """;
            sql.Parameters.Add(Parameters);
            var audioTracks = (await Context.ReadSql.ReadListAsync<WorkManagementTrackV1>(sql, CancellationToken)).ToLookup(track => track.StoredFileId);

            // The editions of a book and the platforms of a game
            IReadOnlyList<WorkManagementGroupV1> groups = [];
            if (hero.MediaTypeId == MediaType.Book)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        edition."Id",
                        1 AS "WorkGroupKindId",
                        edition."Name",
                        edition."Title",
                        edition."LanguageCode",
                        edition."EditionFormatId",
                        edition."Publisher",
                        edition."ReleaseYear"::integer AS "ReleaseYear",
                        (
                            SELECT
                                string_agg(identity."Value", ', ' ORDER BY identity."Value")
                            FROM
                                "WorkEditionExternalIdentities" AS identity
                            WHERE
                                identity."WorkEditionId" = edition."Id"
                                AND identity."Namespace" = 'isbn'
                        ) AS "Isbn",
                        NULL::bigint AS "WantedItemId",
                        NULL::boolean AS "IsPaused"
                    FROM
                        "WorkEditions" AS edition
                    WHERE
                        edition."WorkId" = @WorkId
                    ORDER BY
                        edition."Id";
                    """;
                sql.Parameters.Add(Parameters);
                groups = await Context.ReadSql.ReadListAsync<WorkManagementGroupV1>(sql, CancellationToken);
            }
            else if (hero.MediaTypeId == MediaType.Game)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        platform."Id",
                        2 AS "WorkGroupKindId",
                        platform."Name",
                        platform."Abbreviation" AS "Title",
                        NULL::varchar AS "LanguageCode",
                        NULL::smallint AS "EditionFormatId",
                        NULL::varchar AS "Publisher",
                        NULL::integer AS "ReleaseYear",
                        NULL::text AS "Isbn",
                        wanted."Id" AS "WantedItemId",
                        wanted."IsPaused"
                    FROM
                        "GamePlatforms" AS platform
                        LEFT JOIN "WantedItems" AS wanted ON wanted."WorkId" = @WorkId AND wanted."GamePlatformId" = platform."Id"
                    WHERE
                        platform."Id" IN (
                            SELECT
                                listed."GamePlatformId"
                            FROM
                                "WorkPlatforms" AS listed
                            WHERE
                                listed."WorkId" = @WorkId
                            UNION
                            SELECT
                                target."GamePlatformId"
                            FROM
                                "WantedItems" AS target
                            WHERE
                                target."WorkId" = @WorkId
                                AND target."GamePlatformId" IS NOT NULL
                            UNION
                            SELECT
                                release."GamePlatformId"
                            FROM
                                "GameReleases" AS release
                                JOIN "WorkVersions" AS owned ON owned."Id" = release."WorkVersionId"
                            WHERE
                                owned."WorkId" = @WorkId
                        )
                    ORDER BY
                        platform."Name",
                        platform."Id";
                    """;
                sql.Parameters.Add(Parameters);
                groups = await Context.ReadSql.ReadListAsync<WorkManagementGroupV1>(sql, CancellationToken);
            }

            // What readers had translated of the book's files
            IReadOnlyList<WorkManagementTranslationV1> translations = [];
            if (hero.MediaTypeId == MediaType.Book)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        document."Id" AS "DocumentId",
                        split_part(translated."Path", '/', 2) AS "Language",
                        version."WorkEditionId",
                        least(100, round(100.0 * count(*) / greatest(1, (
                            SELECT
                                count(*)
                            FROM
                                "ReadingResources" AS section
                            WHERE
                                section."ReadingDocumentId" = document."Id"
                                AND section."Path" LIKE ':section/%'
                        ))))::integer AS "CoveragePercent",
                        sum(length(translated."Bytes"))::bigint AS "Bytes"
                    FROM
                        "ReadingDocuments" AS document
                        JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "WorkVersions" AS version ON version."Id" = asset."WorkVersionId"
                        JOIN "ReadingResources" AS translated ON translated."ReadingDocumentId" = document."Id" AND translated."Path" LIKE ':translation/%'
                    WHERE
                        asset."WorkId" = @WorkId
                    GROUP BY
                        document."Id",
                        split_part(translated."Path", '/', 2),
                        version."WorkEditionId"
                    ORDER BY
                        "Language",
                        document."Id";
                    """;
                sql.Parameters.Add(Parameters);
                translations = await Context.ReadSql.ReadListAsync<WorkManagementTranslationV1>(sql, CancellationToken);
            }

            // Further units of a file with several episodes
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    additional."MediaAssetId",
                    additional."WorkUnitId"
                FROM
                    "MediaAssetAdditionalUnits" AS additional
                WHERE
                    additional."WorkId" = @WorkId;
                """;
            sql.Parameters.Add(Parameters);
            var additional = (await Context.ReadSql.ReadListAsync<WorkManagementAssetUnitData>(sql, CancellationToken)).ToLookup(unit => unit.MediaAssetId, unit => unit.WorkUnitId);

            // The profile of the title grades what lies on disk
            QualityProfile? profile = null;
            if (hero.WantedItemId is { } wantedItemId)
            {
                var target = await Context.ReadWantedTargetAsync(wantedItemId, CancellationToken);
                profile = target.Definition is null ? null : AcquisitionSelectionLogic.ReadProfile(target);
            }

            var files = rows.Select(row =>
            {
                var (sizeGrade, perMinute) = WorkGradeLogic.Size(profile, row.QualityKey, row.SizeBytes, row.DurationMs, row.VideoCodec);
                int? score = profile is not null && row.Evidence is { } json && JsonSerializer.Deserialize<StoredReleaseEvidence>(json) is { } facts && StoredFileFormatLogic.Candidate(facts) is { } candidate
                    ? ReleaseScorer.Score(profile, candidate).FormatScore : null;
                long[] unitIds = [.. row.WorkUnitId is { } own ? new[] { own } : [], .. additional[row.MediaAssetId]];
                var (region, version) = hero.MediaTypeId == MediaType.Game ? GameFileName.Facts(row.RelativePath) : (null, null);
                return new WorkManagementFileV1(row.Id, unitIds, row.RelativePath, row.SizeBytes, row.QualityKey, WorkGradeLogic.Quality(profile, row.QualityKey), score, sizeGrade, perMinute, row.VideoCodec,
                    row.Height, row.DynamicRange, row.AudioLanguages, row.SubtitleLanguages, row.ReleaseGroup, row.DurationMs, row.IsMissing, row.GroupId, row.IsRomVerified, region, version, new BaseDataList<WorkManagementTrackV1>(audioTracks[row.Id].ToArray()), row.PageCount);
            }).ToArray();

            // Figures: an episode, or a film as a whole, wants an upgrade while none of its files meets the cutoff and one is below it
            var episodes = units.Where(unit => unit.WorkUnitTypeId == WorkUnitType.Episode).ToArray();
            var leaves = units.Where(unit => unit.WorkUnitTypeId is WorkUnitType.Episode or WorkUnitType.Volume or WorkUnitType.Track).ToArray();
            var present = files.Where(file => !file.IsMissing).ToArray();
            bool WantsUpgrade(IEnumerable<WorkManagementFileV1> own) => own.Any(file => file.FileQualityGradeId == FileQualityGrade.Upgrade) && !own.Any(file => file.FileQualityGradeId == FileQualityGrade.Met);
            var upgrades = leaves.Length > 0 ? leaves.Count(unit => WantsUpgrade(present.Where(file => file.WorkUnitIds.Contains(unit.Id)))) : WantsUpgrade(present) ? 1 : 0;
            return new WorkManagementV1(hero.Id, hero.WantedItemId, hero.MediaTypeId, hero.MediaSubtypeId, hero.CanonicalTitle, hero.ReleaseYear, hero.RuntimeMinutes, hero.ReleasedOn, hero.Overview, hero.Genres,
                hero.Studio, hero.PosterUrl, hero.BackdropUrl, present.Sum(file => file.SizeBytes), leaves.Count(unit => unit.WorkUnitStatusId == WorkUnitStatus.Missing), upgrades,
                leaves.Count(unit => unit.WorkUnitStatusId == WorkUnitStatus.Unaired), units.Count(unit => unit.WorkUnitTypeId == WorkUnitType.Season && unit.Number > 0), episodes.Length,
                new BaseDataList<WorkManagementUnitV1>(units), new BaseDataList<WorkManagementFileV1>(files), new BaseDataList<WorkManagementGroupV1>(groups), new BaseDataList<WorkManagementTranslationV1>(translations));
        }
    }
}
