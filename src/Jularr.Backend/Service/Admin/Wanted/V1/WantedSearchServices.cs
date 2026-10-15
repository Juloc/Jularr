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

            // Manga searches the asked volume, else like the automatic search the whole series as a pack or the next missing volume; Series one season or episode.
            UnitScope? scope = null;
            if (target.MediaSubtypeId == MediaSubtype.Manga)
            {
                var units = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadUnitStatesAsync(target.WantedItemId, ct), CancellationToken);
                scope = Data.UnitNumber is { } volume ? AcquisitionSelectionLogic.ReadUnitScope(units, null, volume) : AcquisitionSelectionLogic.ReadNextUnitScope(units, null, firstAttempt: true);
            }
            else if (target.MediaTypeId == MediaType.Series && Data.ParentUnitNumber is not null)
            {
                var units = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadUnitStatesAsync(target.WantedItemId, ct), CancellationToken);
                scope = AcquisitionSelectionLogic.ReadUnitScope(units, Data.ParentUnitNumber, Data.UnitNumber);
            }
            else if (Data.UnitNumber is not null)
            {
                // An episode needs its season; other Works have no units.
                throw target.MediaTypeId == MediaType.Series
                    ? new DataValidationException(nameof(SearchWantedDataV1.ParentUnitNumber), "required")
                    : new DataValidationException(nameof(SearchWantedDataV1.UnitNumber), "invalid_transition");
            }

            // A manual search shows every candidate and keeps the automatic schedule.
            var result = await _acquisition.SearchAsync(Context, target, indexers, SearchDepth.Normal, automatic: false, CancellationToken, scope);
            if (result.IndexersUnavailable)
            {
                throw new ServiceUnavailableException("acquisition_unavailable");
            }

            var winner = result.Selection.Winner;
            return new BaseDataList<WantedReleaseCandidateV1>(result.Selection.Ranked.Select(evaluation =>
            {
                var offer = result.Offers[evaluation.Candidate.Id];
                var reason = ReferenceEquals(evaluation, winner) && result.Selection.WinnerReason is { } won
                    ? (won.Code, won.Value)
                    : evaluation.Reasons.Count > 0 ? (evaluation.Reasons[^1].Code, evaluation.Reasons[^1].Value) : ("Eligible", null);
                return new WantedReleaseCandidateV1(evaluation.Candidate.Id, offer.Title, offer.ConnectionName, offer.SizeBytes, offer.PublishedAt?.UtcDateTime, evaluation.Score?.QualityKey,
                    evaluation.Decision switch { SelectionDecision.Eligible => ReleaseDecisionType.Eligible, SelectionDecision.ManualReview => ReleaseDecisionType.ManualReview, _ => ReleaseDecisionType.Rejected },
                    evaluation.PreferenceScore, reason.Item1, TextLimit.Cut(reason.Item2, 1000));
            }).ToArray());
        }
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
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests", "Acquisition");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only releases of the last search of this target can be taken.
            var offer = _acquisition.FindOffer(Parameters.WantedItemId, Data.CandidateId)
                ?? throw new DataValidationException(nameof(GrabWantedReleaseDataV1.CandidateId), "outdated_version");
            var (target, clients) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await ctx.ReadWantedTargetAsync(Parameters.WantedItemId, ct), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Download, ct)), CancellationToken);
            if (target.IsDownloading && target.MediaTypeId != MediaType.Series)
            {
                throw new DataValidationException(nameof(WantedItemParametersV1.WantedItemId), "in_use");
            }

            var quality = AcquisitionSelectionLogic.QualityKey(AcquisitionSelectionLogic.GetKind(target.MediaTypeId, target.MediaSubtypeId), offer.Title, target.PlatformIgdbId);
            await _acquisition.GrabAsync(Context, target, offer, quality, clients, CancellationToken);
            return new UpdateResult(true);
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
                """
                SELECT
                    release."Id",
                    release."Title",
                    connection."Name" AS "ConnectionName",
                    release."QualityKey",
                    release."AcquisitionReleaseStatusTypeId",
                    release."ReasonKey",
                    operation."ProgressPercent"::integer AS "ProgressPercent",
                    release."CreatedAt",
                    release."AcquisitionReleaseStatusTypeId" = 4 OR release."AcquisitionReleaseStatusTypeId" = 3 AND release."ReasonKey" = 'download_failed' AS "IsBlocked"
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
            return new UpdateResult(true);
        }
    }
}
