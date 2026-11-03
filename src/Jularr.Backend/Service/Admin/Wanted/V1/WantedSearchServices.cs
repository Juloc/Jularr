using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/wanted/{wantedItemId:long}/search")]
    public sealed class SearchWantedV1 : AdminService<WantedItemParametersV1, SearchWantedDataV1>
    {
        private readonly WantedAcquisition _acquisition;

        internal SearchWantedV1(WantedAcquisition acquisition)
        {
            _acquisition = acquisition;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WantedReleaseCandidateV1>>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests", "Acquisition");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (target, indexers) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await ctx.ReadWantedTargetAsync(Parameters.WantedItemId, ct), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, ct)), CancellationToken);

            // One source of the list can be asked on its own; a manual search shows every candidate and keeps the automatic schedule.
            if (Data.AcquisitionConnectionId is { } connectionId)
            {
                indexers = _acquisition.ReadSources(target, indexers).All.Where(connection => connection.Id == connectionId).ToArray();
                if (indexers.Count == 0)
                {
                    throw new DataValidationException(nameof(SearchWantedDataV1.AcquisitionConnectionId), "source_not_available");
                }
            }

            var scope = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadSearchScopeAsync(target, Data.ParentUnitNumber, Data.UnitNumber, ct), CancellationToken);
            var result = await _acquisition.SearchAsync(Context, target, indexers, SearchDepth.Normal, automatic: false, CancellationToken, scope, Data.Language);
            if (result.IndexersUnavailable)
            {
                throw new ServiceUnavailableException(result.ErrorKey ?? "acquisition_unavailable");
            }

            var winner = result.Selection.Winner;
            return new BaseDataList<WantedReleaseCandidateV1>(result.Selection.Ranked.Select(evaluation =>
            {
                var offer = result.Offers[evaluation.Candidate.Id];
                var reason = ReferenceEquals(evaluation, winner) && result.Selection.WinnerReason is { } won
                    ? (won.Code, won.Value)
                    : evaluation.Reasons.Count > 0 ? (evaluation.Reasons[^1].Code, evaluation.Reasons[^1].Value) : ("Eligible", null);
                // Every rule with its points: the profile's custom formats, then the media type's rules.
                var book = evaluation.Candidate.Parsed?.Book;
                var points = (evaluation.Score?.FormatPoints ?? []).Concat(evaluation.Candidate.ContextPoints)
                    .Select(point => new ReleaseScorePointV1(point.Code, TextLimit.Cut(point.Value, 1000), point.Points));
                return new WantedReleaseCandidateV1(evaluation.Candidate.Id, offer.Title, offer.ConnectionName, offer.SizeBytes, offer.PublishedAt?.UtcDateTime, evaluation.Score?.QualityKey,
                    evaluation.Decision switch { SelectionDecision.Eligible => ReleaseDecisionType.Eligible, SelectionDecision.ManualReview => ReleaseDecisionType.ManualReview, _ => ReleaseDecisionType.Rejected },
                    evaluation.PreferenceScore, reason.Item1, TextLimit.Cut(reason.Item2, 1000), book?.Kind, book?.VolumeStart, book?.VolumeEnd, book?.IsComplete ?? false,
                    evaluation.Candidate.SizeMark, new BaseDataList<ReleaseScorePointV1>(points), TextLimit.Cut(string.Join(", ", ReleaseGroupLogic.Split(evaluation.Candidate.Parsed?.ReleaseGroup)) is { Length: > 0 } groups ? groups : null, 64));
            }).ToArray());
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/wanted/{wantedItemId:long}/search/sources")]
    public sealed class ReadWantedSearchSourcesV1 : AdminService<WantedItemParametersV1, SearchWantedDataV1>
    {
        private readonly WantedAcquisition _acquisition;

        internal ReadWantedSearchSourcesV1(WantedAcquisition acquisition)
        {
            _acquisition = acquisition;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WantedSearchSourceV1>>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests", "Acquisition");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The sources a manual search asks, in the order of their priority; the search itself is one request per source.
            var (target, connections) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await ctx.ReadWantedTargetAsync(Parameters.WantedItemId, ct), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, ct)), CancellationToken);
            return new BaseDataList<WantedSearchSourceV1>(_acquisition.ReadSources(target, connections).All
                .Select(connection => new WantedSearchSourceV1(connection.Id, connection.Name, connection.AcquisitionConnectionTypeId)).ToArray());
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/wanted/{wantedItemId:long}/search/automatic")]
    public sealed class SearchWantedAutomaticallyV1 : AdminService<WantedItemParametersV1, SearchWantedDataV1>
    {
        private readonly WantedAcquisition _acquisition;

        internal SearchWantedAutomaticallyV1(WantedAcquisition acquisition)
        {
            _acquisition = acquisition;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AutomaticSearchResultV1>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests", "Acquisition");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute() =>
            await _acquisition.SearchAutomaticallyAsync(Context, Parameters.WantedItemId, Data.ParentUnitNumber, Data.UnitNumber, Data.Language, Auth.AccountId, CancellationToken);
    }

    [ServiceEndpoint("POST", "/api/v1/admin/wanted/{wantedItemId:long}/grab")]
    public sealed class GrabWantedReleaseV1 : AdminService<WantedItemParametersV1, GrabWantedReleaseDataV1>
    {
        private readonly WantedAcquisition _acquisition;

        internal GrabWantedReleaseV1(WantedAcquisition acquisition)
        {
            _acquisition = acquisition;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<GrabWantedReleaseResultV1>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests", "Acquisition");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only releases of the last searches of this target can be taken; a rejected one only when the person overrides the rejection.
            var offer = _acquisition.FindOffer(Parameters.WantedItemId, Data.CandidateId, Data.OverridesRejection);
            if (offer is null)
            {
                throw new DataValidationException(nameof(GrabWantedReleaseDataV1.CandidateId), !Data.OverridesRejection && _acquisition.FindOffer(Parameters.WantedItemId, Data.CandidateId, true) is not null
                    ? "release_rejected" : "outdated_version");
            }

            var (target, clients) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await ctx.ReadWantedTargetAsync(Parameters.WantedItemId, ct), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Download, ct)), CancellationToken);
            if (target.IsDownloading && target.MediaTypeId != MediaType.Series)
            {
                throw new DataValidationException(nameof(WantedItemParametersV1.WantedItemId), "in_use");
            }

            var quality = AcquisitionSelectionLogic.QualityKey(AcquisitionSelectionLogic.GetKind(target.MediaTypeId, target.MediaSubtypeId), offer.Title, target.PlatformIgdbId);
            var prioritized = await _acquisition.GrabAsync(Context, target, offer, quality, clients, CancellationToken, Auth.AccountId, startsNow: Data.StartsNow);
            return new GrabWantedReleaseResultV1(true, prioritized);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/wanted/{wantedItemId:long}/releases")]
    public sealed class ReadWantedReleasesV1 : AdminService<WantedItemParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WantedReleaseV1>>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Grabbed, imported, failed and blocked releases of one target; release faults block like a manual block.
            var sql = new SqlStatement();
            sql.Statement =
                $"""
                SELECT
                    release."Id",
                    release."Title",
                    connection."Name" AS "ConnectionName",
                    release."QualityKey",
                    release."AcquisitionReleaseStatusTypeId",
                    release."ReasonKey",
                    operation."ProgressPercent"::integer AS "ProgressPercent",
                    release."CreatedAt",
                    release."AcquisitionReleaseStatusTypeId" = 4 OR release."AcquisitionReleaseStatusTypeId" = 3 AND {DownloadLogic.ReleaseFaultSql} AS "IsBlocked"
                FROM
                    "AcquisitionReleases" AS release
                    LEFT JOIN "AcquisitionConnections" AS connection ON connection."Id" = release."AcquisitionConnectionId"
                    LEFT JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id" AND operation."OperationTypeId" = 2
                WHERE
                    release."WantedItemId" = @WantedItemId
                ORDER BY
                    release."CreatedAt" DESC,
                    release."Id" DESC
                LIMIT 50;
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<WantedReleaseV1>(await Context.ReadSql.ReadListAsync<WantedReleaseV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/wanted/{wantedItemId:long}/releases/{acquisitionReleaseId:long}")]
    public sealed class MarkWantedReleaseV1 : AdminService<WantedReleaseParametersV1, MarkWantedReleaseDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WantedReleaseParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedReleaseParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.MarkAcquisitionReleaseAsync(new AcquisitionReleaseMarkData(Parameters.WantedItemId, Parameters.AcquisitionReleaseId, Data.AcquisitionReleaseStatusTypeId), CancellationToken);
            if (Data.AcquisitionReleaseStatusTypeId == AcquisitionReleaseStatusType.Blocked)
            {
                await Context.RecordActivityAsync(new ActivityEventData(ActivityEventType.Blocked, acquisitionReleaseId: Parameters.AcquisitionReleaseId, accountId: Auth.AccountId), CancellationToken);
            }

            return new UpdateResult(true);
        }
    }
}
