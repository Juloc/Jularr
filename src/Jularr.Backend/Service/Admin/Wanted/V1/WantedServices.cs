using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/wanted")]
    public sealed class ReadWantedV1 : AdminService<ReadWantedParametersV1>
    {
        // The entries of the list: a target, and for a book one entry per language it is wanted in (its own status: the language is there or missing). The rest of the statement
        // continues the body of "wanted_state".
        internal static readonly string WantedEntries = Entries(WantedStateLogic.LiveState, "\"WantedBookLanguages\"");

        // The same entries from the states the worker kept, while they are current (WantedStateLogic).
        internal static readonly string StoredWantedEntries = Entries(WantedStateLogic.StoredState, "\"WantedBookLanguageStates\"");

        // The kept states while they are current, else the states worked out now.
        internal static async Task<string> EntriesAsync(LogicContext ctx, CancellationToken ct) => await ctx.IsWantedStateCurrentAsync(ct) ? StoredWantedEntries : WantedEntries;

        private static string Entries(string state, string bookLanguages) =>
            ("WITH wanted_base AS (" + state + """
                ), book_languages AS MATERIALIZED (
                    -- Once: joined as it is, the view is evaluated again for every row of the list.
                    SELECT
                        language."WantedItemId",
                        language."Language",
                        language."IsCovered"
                    FROM
                        BOOK_LANGUAGES AS language
                ), wanted_state AS (
                    SELECT
                        base."Id",
                        base."WorkId",
                        base."MediaTypeId",
                        base."MediaSubtypeId",
                        base."CanonicalTitle",
                        base."IsPaused",
                        base."WantedPriorityTypeId",
                        base."MonitorsNewUnits",
                        CASE WHEN language."Language" IS NULL THEN base."LastSearchedAt" ELSE searched."LastSearchedAt" END AS "LastSearchedAt",
                        base."LastOutcomeKey",
                        base."AcquisitionProfileId",
                        base."LibraryRootId",
                        base."GamePlatformId",
                        base."Version",
                        coalesce(language."IsCovered", base."HasMedia") AS "HasMedia",
                        base."IsCutoffUnmet" AND coalesce(language."IsCovered", TRUE) AS "IsCutoffUnmet",
                        base."IsLanguageOpen",
                        language."Language" AS "BookLanguage"
                    FROM
                        wanted_base AS base
                        LEFT JOIN book_languages AS language ON language."WantedItemId" = base."Id"
                        LEFT JOIN "WantedBookLanguageSearches" AS searched ON searched."WantedItemId" = language."WantedItemId" AND searched."Language" = language."Language"
            """).Replace("BOOK_LANGUAGES", bookLanguages, StringComparison.Ordinal);

        // One page of the entries of a view with everything its rows show, as "wanted_rows"; the rest of the statement continues the body of "wanted_state".
        internal const string WantedPageRows =
            """
                ), wanted_page AS (
                    SELECT
                        wanted_state."Id",
                        wanted_state."WorkId",
                        wanted_state."MediaTypeId",
                        wanted_state."MediaSubtypeId",
                        wanted_state."CanonicalTitle",
                        wanted_state."IsPaused",
                        wanted_state."WantedPriorityTypeId",
                        wanted_state."MonitorsNewUnits",
                        wanted_state."LastSearchedAt",
                        wanted_state."LastOutcomeKey",
                        wanted_state."AcquisitionProfileId",
                        wanted_state."LibraryRootId",
                        wanted_state."GamePlatformId",
                        wanted_state."Version",
                        wanted_state."HasMedia",
                        wanted_state."IsLanguageOpen",
                        wanted_state."BookLanguage"
                    FROM
                        wanted_state
                    WHERE
                        @WantedViewTypeId = 0
                        OR @WantedViewTypeId = 1 AND NOT wanted_state."IsPaused" AND NOT wanted_state."HasMedia"
                        OR @WantedViewTypeId = 2 AND NOT wanted_state."IsPaused" AND wanted_state."IsCutoffUnmet"
                    ORDER BY
                        wanted_state."Id" DESC,
                        wanted_state."BookLanguage"
                    LIMIT @PageSize
                    OFFSET @Offset
                ), wanted_rows AS (
                    SELECT
                        wanted_page."Id",
                        wanted_page."WorkId",
                        wanted_page."MediaTypeId",
                        wanted_page."MediaSubtypeId",
                        wanted_page."CanonicalTitle",
                        wanted_page."IsPaused",
                        wanted_page."WantedPriorityTypeId",
                        ARRAY(SELECT tagged."TagId" FROM "WorkTags" AS tagged WHERE tagged."WorkId" = wanted_page."WorkId" ORDER BY tagged."TagId") AS "TagIds",
                        wanted_page."MonitorsNewUnits",
                        wanted_page."Version",
                        wanted_page."MediaTypeId" = 2 OR wanted_page."MediaSubtypeId" IS NOT DISTINCT FROM 2 AS "HasUnits",
                        wanted_page."HasMedia",
                        wanted_page."BookLanguage",
                        wanted_page."IsLanguageOpen",
                        download."Id" IS NOT NULL AS "IsDownloading",
                        download."ProgressPercent"::integer AS "ProgressPercent",
                        wanted_page."LastSearchedAt",
                        wanted_page."LastOutcomeKey" AS "OutcomeKey",
                        wanted_page."AcquisitionProfileId",
                        wanted_page."LibraryRootId",
                        (SELECT platform."Name" FROM "GamePlatforms" AS platform WHERE platform."Id" = wanted_page."GamePlatformId") AS "PlatformName",
                        ARRAY(
                            SELECT DISTINCT
                                locale."Name"
                            FROM
                                "RequestTargets" AS target
                            INNER JOIN
                                "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                            INNER JOIN
                                "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
                            WHERE
                                target."WantedItemId" = wanted_page."Id"
                            ORDER BY
                                locale."Name"
                        ) AS "RequestedLanguages"
                    FROM
                        wanted_page
                        LEFT JOIN LATERAL (
                            SELECT
                                operation."Id",
                                operation."ProgressPercent"
                            FROM
                                "AcquisitionReleases" AS release
                                JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id"
                            WHERE
                                release."WantedItemId" = wanted_page."Id"
                                AND operation."OperationStatusTypeId" IN (1, 2)
                            LIMIT 1
                        ) AS download ON TRUE
            """;

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WantedDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWantedParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadWantedParametersV1 parameters, Type resultType) => new("wanted.read");
        protected override PageRequest GetPageRequest(ReadWantedParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Enrich only the bounded page. Missing: nothing or a monitored aired unit without media; cutoff unmet: the view WantedBelowCutoff.
            var sql = new SqlStatement();
            sql.Statement = await EntriesAsync(Context, CancellationToken) + WantedPageRows +
                """
                )
                SELECT
                    wanted_rows."Id",
                    wanted_rows."WorkId",
                    wanted_rows."MediaTypeId",
                    wanted_rows."MediaSubtypeId",
                    wanted_rows."CanonicalTitle",
                    wanted_rows."IsPaused",
                    wanted_rows."WantedPriorityTypeId",
                    wanted_rows."TagIds",
                    wanted_rows."MonitorsNewUnits",
                    wanted_rows."Version",
                    wanted_rows."HasUnits",
                    wanted_rows."HasMedia",
                    wanted_rows."BookLanguage",
                    wanted_rows."IsLanguageOpen",
                    wanted_rows."IsDownloading",
                    wanted_rows."ProgressPercent",
                    wanted_rows."LastSearchedAt",
                    wanted_rows."OutcomeKey",
                    wanted_rows."AcquisitionProfileId",
                    wanted_rows."LibraryRootId",
                    wanted_rows."PlatformName",
                    wanted_rows."RequestedLanguages"
                FROM
                    wanted_rows
                ORDER BY
                    wanted_rows."Id" DESC,
                    wanted_rows."BookLanguage";
                """;
            sql.Parameters.Add(new WantedViewFilterData(Parameters.WantedViewTypeId, Parameters.MediaTypeIds, Parameters.MediaSubtypeIds));
            sql.Page = Page;
            sql.DisableJit = true;
            return await Context.ReadSql.ReadPageAsync<WantedDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/wanted/summary")]
    public sealed class ReadWantedSummaryV1 : AdminService<ReadWantedParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WantedSummaryV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadWantedParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadWantedParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // How many targets each view of the list holds, and how many are high priority or paused.
            var sql = new SqlStatement();
            sql.Statement = await ReadWantedV1.EntriesAsync(Context, CancellationToken) +
                """
                )
                SELECT
                    count(*)::integer AS "TotalCount",
                    count(*) FILTER (WHERE NOT wanted_state."IsPaused" AND NOT wanted_state."HasMedia")::integer AS "MissingCount",
                    count(*) FILTER (WHERE NOT wanted_state."IsPaused" AND wanted_state."IsCutoffUnmet")::integer AS "CutoffUnmetCount",
                    count(*) FILTER (WHERE wanted_state."WantedPriorityTypeId" = 2)::integer AS "HighCount",
                    count(*) FILTER (WHERE wanted_state."IsPaused")::integer AS "PausedCount"
                FROM
                    wanted_state;
                """;
            sql.Parameters.Add(new WantedViewFilterData(Parameters.WantedViewTypeId, Parameters.MediaTypeIds, Parameters.MediaSubtypeIds));
            sql.DisableJit = true;
            return await Context.ReadSql.ReadRequiredAsync<WantedSummaryV1>(sql, CancellationToken);
        }
    }

    // The Wanted page in one statement: the state of every target is worked out once for the page and for the numbers of all views.
    [ServiceEndpoint("GET", "/api/v1/admin/wanted/page")]
    public sealed class ReadWantedPageV1 : AdminService<ReadWantedParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WantedPageV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadWantedParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadWantedParametersV1 parameters, Type resultType) => new("wanted.read");
        protected override PageRequest GetPageRequest(ReadWantedParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Every row carries the numbers; an empty page is one row of numbers without an entry.
            var sql = new SqlStatement();
            sql.Statement = await ReadWantedV1.EntriesAsync(Context, CancellationToken) +
                """
                ), wanted_summary AS (
                    SELECT
                        count(*)::integer AS "TotalCount",
                        count(*) FILTER (WHERE NOT wanted_state."IsPaused" AND NOT wanted_state."HasMedia")::integer AS "MissingCount",
                        count(*) FILTER (WHERE NOT wanted_state."IsPaused" AND wanted_state."IsCutoffUnmet")::integer AS "CutoffUnmetCount",
                        count(*) FILTER (WHERE wanted_state."WantedPriorityTypeId" = 2)::integer AS "HighCount",
                        count(*) FILTER (WHERE wanted_state."IsPaused")::integer AS "PausedCount"
                    FROM
                        wanted_state
                """ + ReadWantedV1.WantedPageRows +
                """
                )
                SELECT
                    wanted_rows."Id",
                    wanted_rows."WorkId",
                    wanted_rows."MediaTypeId",
                    wanted_rows."MediaSubtypeId",
                    wanted_rows."CanonicalTitle",
                    wanted_rows."IsPaused",
                    wanted_rows."WantedPriorityTypeId",
                    wanted_rows."TagIds",
                    wanted_rows."MonitorsNewUnits",
                    wanted_rows."Version",
                    wanted_rows."HasUnits",
                    wanted_rows."HasMedia",
                    wanted_rows."BookLanguage",
                    wanted_rows."IsLanguageOpen",
                    wanted_rows."IsDownloading",
                    wanted_rows."ProgressPercent",
                    wanted_rows."LastSearchedAt",
                    wanted_rows."OutcomeKey",
                    wanted_rows."AcquisitionProfileId",
                    wanted_rows."LibraryRootId",
                    wanted_rows."PlatformName",
                    wanted_rows."RequestedLanguages",
                    wanted_summary."TotalCount",
                    wanted_summary."MissingCount",
                    wanted_summary."CutoffUnmetCount",
                    wanted_summary."HighCount",
                    wanted_summary."PausedCount"
                FROM
                    wanted_summary
                    LEFT JOIN wanted_rows ON TRUE
                ORDER BY
                    wanted_rows."Id" DESC,
                    wanted_rows."BookLanguage";
                """;
            sql.Parameters.Add(new WantedViewFilterData(Parameters.WantedViewTypeId, Parameters.MediaTypeIds, Parameters.MediaSubtypeIds));
            sql.Page = Page;
            sql.DisableJit = true;
            var rows = await Context.ReadSql.ReadPageAsync<WantedPageRow>(sql, CancellationToken);
            var first = rows.Items[0];
            var entries = rows.Items.Where(row => row.Id is not null).Select(row => new WantedDetailsV1(row.Id!.Value, row.WorkId!.Value, row.MediaTypeId!.Value, row.MediaSubtypeId, row.CanonicalTitle!,
                row.PlatformName, row.IsPaused!.Value, row.WantedPriorityTypeId!.Value, row.Version!.Value, row.RequestedLanguages, row.HasMedia!.Value, row.IsDownloading!.Value, row.ProgressPercent,
                row.LastSearchedAt, row.OutcomeKey, row.AcquisitionProfileId, row.LibraryRootId, row.MonitorsNewUnits!.Value, row.HasUnits!.Value, row.TagIds, row.BookLanguage, row.IsLanguageOpen!.Value));
            return new WantedPageV1(new PageResult<WantedDetailsV1>([.. entries], Page),
                new WantedSummaryV1(first.TotalCount, first.MissingCount, first.CutoffUnmetCount, first.HighCount, first.PausedCount));
        }

        // An entry of the page and the numbers beside it; the entry's columns are empty on the row of an empty page.
        private sealed record WantedPageRow(long? Id, long? WorkId, MediaType? MediaTypeId, MediaSubtype? MediaSubtypeId, string? CanonicalTitle, bool? IsPaused, WantedPriorityType? WantedPriorityTypeId,
            long[] TagIds, bool? MonitorsNewUnits, long? Version, bool? HasUnits, bool? HasMedia, string? BookLanguage, bool? IsLanguageOpen, bool? IsDownloading, int? ProgressPercent,
            DateTime? LastSearchedAt, string? OutcomeKey, long? AcquisitionProfileId, long? LibraryRootId, string? PlatformName, string[] RequestedLanguages,
            int TotalCount, int MissingCount, int CutoffUnmetCount, int HighCount, int PausedCount);
    }

    [ServiceEndpoint("GET", "/api/v1/admin/wanted/{wantedItemId:long}")]
    public sealed class ReadWantedItemV1 : AdminService<WantedItemParametersV1>
    {
        // The chosen targets with what their own pages show, the first-made first.
        internal const string WantedItems =
            """
                WITH wanted_page AS (
                    SELECT
                        wanted."Id",
                        wanted."WorkId",
                        work."MediaTypeId",
                        work."MediaSubtypeId",
                        work."CanonicalTitle",
                        wanted."IsPaused",
                        wanted."WantedPriorityTypeId",
                        wanted."MonitorsNewUnits",
                        wanted."LastSearchedAt",
                        wanted."LastOutcomeKey",
                        wanted."AcquisitionProfileId",
                        wanted."LibraryRootId",
                        wanted."GamePlatformId",
                        wanted."Version"
                    FROM
                        "WantedItems" AS wanted
                    INNER JOIN
                        "Works" AS work ON work."Id" = wanted."WorkId"
                    WHERE
                        wanted."Id" = ANY(@WantedItemIds)
                )
                SELECT
                    wanted_page."Id",
                    wanted_page."WorkId",
                    wanted_page."MediaTypeId",
                    wanted_page."MediaSubtypeId",
                    wanted_page."CanonicalTitle",
                    wanted_page."IsPaused",
                    wanted_page."WantedPriorityTypeId",
                    ARRAY(SELECT tagged."TagId" FROM "WorkTags" AS tagged WHERE tagged."WorkId" = wanted_page."WorkId" ORDER BY tagged."TagId") AS "TagIds",
                    wanted_page."MonitorsNewUnits",
                    wanted_page."Version",
                    wanted_page."MediaTypeId" = 2 OR wanted_page."MediaSubtypeId" IS NOT DISTINCT FROM 2 AS "HasUnits",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                        WHERE
                            asset."WorkId" = wanted_page."WorkId"
                            AND (
                                wanted_page."GamePlatformId" IS NULL
                                OR EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "GameReleases" AS game
                                    WHERE
                                        game."WorkVersionId" = asset."WorkVersionId"
                                        AND game."GamePlatformId" = wanted_page."GamePlatformId"
                                )
                            )
                    ) AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkUnits" AS unit
                        WHERE
                            unit."WorkId" = wanted_page."WorkId"
                            AND unit."IsMonitored" AND (unit."WorkUnitTypeId" = 2 OR unit."WorkUnitTypeId" = 3 AND wanted_page."MediaSubtypeId" = 2)
                            AND (unit."ReleasedOn" <= current_date OR unit."WorkUnitTypeId" = 3 AND unit."ReleasedOn" IS NULL)
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "MediaAssets" AS asset
                                    JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                                WHERE
                                    asset."WorkUnitId" = unit."Id"
                            )
                    ) AS "HasMedia",
                    NULL::text AS "BookLanguage",
                    FALSE AS "IsLanguageOpen",
                    download."Id" IS NOT NULL AS "IsDownloading",
                    download."ProgressPercent"::integer AS "ProgressPercent",
                    wanted_page."LastSearchedAt",
                    wanted_page."LastOutcomeKey" AS "OutcomeKey",
                    wanted_page."AcquisitionProfileId",
                    wanted_page."LibraryRootId",
                    (SELECT platform."Name" FROM "GamePlatforms" AS platform WHERE platform."Id" = wanted_page."GamePlatformId") AS "PlatformName",
                    ARRAY(
                        SELECT DISTINCT
                            locale."Name"
                        FROM
                            "RequestTargets" AS target
                        INNER JOIN
                            "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                        INNER JOIN
                            "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
                        WHERE
                            target."WantedItemId" = wanted_page."Id"
                        ORDER BY
                            locale."Name"
                    ) AS "RequestedLanguages"
                FROM
                    wanted_page
                    LEFT JOIN LATERAL (
                        SELECT
                            operation."Id",
                            operation."ProgressPercent"
                        FROM
                            "AcquisitionReleases" AS release
                            JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id"
                        WHERE
                            release."WantedItemId" = wanted_page."Id"
                            AND operation."OperationStatusTypeId" IN (1, 2)
                        LIMIT 1
                    ) AS download ON TRUE
                ORDER BY
                    wanted_page."Id";
            """;

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WantedDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement = WantedItems;
            sql.Parameters.Add(new ReadWantedItemsParametersV1([Parameters.WantedItemId]));
            return await Context.ReadSql.ReadRequiredAsync<WantedDetailsV1>(sql, CancellationToken);
        }
    }

    // Several targets in one read, such as the platforms of a game.
    [ServiceEndpoint("GET", "/api/v1/admin/wanted/items")]
    public sealed class ReadWantedItemsV1 : AdminService<ReadWantedItemsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WantedDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWantedItemsParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadWantedItemsParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement = ReadWantedItemV1.WantedItems;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<WantedDetailsV1>(await Context.ReadSql.ReadListAsync<WantedDetailsV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/wanted/{wantedItemId:long}/libraries")]
    public sealed class ReadWantedLibrariesV1 : AdminService<WantedItemParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WantedLibraryV1>>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Libraries of the Work's type that accept its subtype.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    root."Id",
                    root."Name"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    JOIN "LibraryRoots" AS root ON root."MediaTypeId" = work."MediaTypeId"
                WHERE
                    wanted."Id" = @WantedItemId
                    AND (root."MediaSubtypeId" IS NULL OR root."MediaSubtypeId" = work."MediaSubtypeId")
                ORDER BY
                    root."Name",
                    root."Id";
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<WantedLibraryV1>(await Context.ReadSql.ReadListAsync<WantedLibraryV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/wanted/{wantedItemId:long}")]
    public sealed class UpdateWantedV1 : AdminService<UpdateWantedParametersV1, UpdateWantedDataV1>
    {
        private readonly AcquisitionSearchSignal _searchSignal;
        private bool _resumed;

        internal UpdateWantedV1(AcquisitionSearchSignal searchSignal)
        {
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateWantedParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(UpdateWantedParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            _resumed = await Context.UpdateWantedItemAsync(new WantedItemUpdateData(Parameters.WantedItemId, Data.IsPaused, Data.AcquisitionProfileId, Data.LibraryRootId, Data.MonitorsNewUnits, Data.Version, Data.TagIds), CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted()
        {
            if (_resumed) _searchSignal.RequestSearch();
        }
    }

    // Several targets get one priority; the automatic search takes the high ones first and waits for nobody.
    [ServiceEndpoint("POST", "/api/v1/admin/wanted/priority")]
    public sealed class UpdateWantedPriorityV1 : AdminService<UpdateWantedPriorityParametersV1, UpdateWantedPriorityDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateWantedPriorityParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(UpdateWantedPriorityParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SetWantedPriorityAsync(new WantedPriorityData(Data.WantedItemIds, Data.WantedPriorityTypeId), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }

    // The chosen targets, or all that are due, are searched now instead of on schedule.
    [ServiceEndpoint("POST", "/api/v1/admin/wanted/search")]
    public sealed class StartWantedSearchV1 : AdminService<StartWantedSearchParametersV1, StartWantedSearchDataV1>
    {
        private readonly AcquisitionSearchSignal _searchSignal;

        internal StartWantedSearchV1(AcquisitionSearchSignal searchSignal)
        {
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StartWantedSearchParametersV1 parameters) => ModuleRequirement.All("Requests", "Acquisition");
        protected override ServicePermission GetPermission(StartWantedSearchParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.StartWantedSearchAsync(new WantedSearchStartData(Data.WantedItemIds, Data.MediaTypeIds, Data.MediaSubtypeIds), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted() => _searchSignal.RequestSearch();
    }

    [ServiceEndpoint("GET", "/api/v1/admin/wanted/{wantedItemId:long}/units")]
    public sealed class ReadWantedUnitsV1: AdminService<ReadWantedUnitsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WantedUnitV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWantedUnitsParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadWantedUnitsParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Seasons and volumes in their order, one season's episodes when asked; a season is monitored while one of its episodes is.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    unit."Id",
                    unit."ParentWorkUnitId",
                    unit."WorkUnitTypeId",
                    unit."Number",
                    unit."Title",
                    unit."ReleasedOn",
                    CASE WHEN unit."WorkUnitTypeId" = 1 THEN episodes."MonitoredEpisodes" > 0 ELSE unit."IsMonitored" END AS "IsMonitored",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                        WHERE
                            asset."WorkUnitId" = unit."Id"
                    ) AS "HasMedia",
                    episodes."Episodes",
                    episodes."MonitoredEpisodes"
                FROM
                    "WantedItems" AS wanted
                    JOIN "WorkUnits" AS unit ON unit."WorkId" = wanted."WorkId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    CROSS JOIN LATERAL (
                        SELECT
                            count(*)::integer AS "Episodes",
                            (count(*) FILTER (WHERE episode."IsMonitored"))::integer AS "MonitoredEpisodes"
                        FROM
                            "WorkUnits" AS episode
                        WHERE
                            episode."ParentWorkUnitId" = unit."Id"
                            AND episode."WorkUnitTypeId" = 2
                    ) AS episodes
                WHERE
                    wanted."Id" = @WantedItemId
                    AND unit."WorkUnitTypeId" IN (1, 2, 3)
                    AND (unit."ParentWorkUnitId" IS NULL OR unit."ParentWorkUnitId" = @SeasonId)
                ORDER BY
                    coalesce(parent."Number", unit."Number"),
                    unit."ParentWorkUnitId" NULLS FIRST,
                    unit."Number";
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<WantedUnitV1>(await Context.ReadSql.ReadListAsync<WantedUnitV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/wanted/{wantedItemId:long}/units/{workUnitId:long}")]
    public sealed class UpdateWantedUnitV1 : AdminService<WantedUnitParametersV1, UpdateWantedUnitDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WantedUnitParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedUnitParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SetUnitMonitoringAsync(new WantedUnitMonitoringData(Parameters.WantedItemId, Parameters.WorkUnitId, Data.IsMonitored), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
