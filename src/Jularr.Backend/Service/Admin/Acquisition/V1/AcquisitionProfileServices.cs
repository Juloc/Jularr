using System.Text.Json;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/profiles")]
    public sealed class ReadAcquisitionProfilesV1 : AdminService<ReadAcquisitionProfilesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<AcquisitionProfileDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadAcquisitionProfilesParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReadAcquisitionProfilesParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."Name",
                    profile."MediaTypeId",
                    profile."MediaSubtypeId",
                    profile."IsDefault",
                    profile."Definition"::text AS "Definition",
                    (SELECT count(*)::integer FROM "WantedItems" AS wanted WHERE wanted."AcquisitionProfileId" = profile."Id") AS "WantedCount",
                    profile."Version"
                FROM
                    "AcquisitionProfiles" AS profile
                WHERE
                    (@MediaTypeId::smallint IS NULL OR profile."MediaTypeId" = @MediaTypeId)
                    AND (@WantedItemId::bigint IS NULL OR EXISTS (
                        SELECT 1
                        FROM "WantedItems" AS wanted
                            JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                        WHERE wanted."Id" = @WantedItemId
                            AND profile."MediaTypeId" = work."MediaTypeId"
                            AND (profile."MediaSubtypeId" IS NULL OR profile."MediaSubtypeId" = work."MediaSubtypeId")
                    ))
                ORDER BY
                    profile."MediaTypeId",
                    profile."MediaSubtypeId" NULLS FIRST,
                    profile."IsDefault" DESC,
                    profile."Name",
                    profile."Id";
                """;
            sql.Parameters.Add(Parameters);
            var rows = await Context.ReadSql.ReadListAsync<AcquisitionProfileReadData>(sql, CancellationToken);
            return new BaseDataList<AcquisitionProfileDetailsV1>(rows.Select(AcquisitionProfileContracts.Read));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/profiles/{acquisitionProfileId:long}")]
    public sealed class ReadAcquisitionProfileV1 : AdminService<AcquisitionProfileParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AcquisitionProfileDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionProfileParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionProfileParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."Name",
                    profile."MediaTypeId",
                    profile."MediaSubtypeId",
                    profile."IsDefault",
                    profile."Definition"::text AS "Definition",
                    (SELECT count(*)::integer FROM "WantedItems" AS wanted WHERE wanted."AcquisitionProfileId" = profile."Id") AS "WantedCount",
                    profile."Version"
                FROM
                    "AcquisitionProfiles" AS profile
                WHERE
                    profile."Id" = @AcquisitionProfileId;
                """;
            sql.Parameters.Add(Parameters);
            return AcquisitionProfileContracts.Read(await Context.ReadSql.ReadRequiredAsync<AcquisitionProfileReadData>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/profiles")]
    public sealed class CreateAcquisitionProfileV1 : AdminService<CreateAcquisitionProfileParametersV1, SaveAcquisitionProfileDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateAcquisitionProfileResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateAcquisitionProfileParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(CreateAcquisitionProfileParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var id = await Context.CreateAcquisitionProfileAsync(AcquisitionProfileContracts.Input(null, Data), CancellationToken);
            return new CreateAcquisitionProfileResultV1(id);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/profiles/{acquisitionProfileId:long}")]
    public sealed class UpdateAcquisitionProfileV1 : AdminService<AcquisitionProfileParametersV1, SaveAcquisitionProfileDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionProfileParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionProfileParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateAcquisitionProfileAsync(AcquisitionProfileContracts.Input(Parameters.AcquisitionProfileId, Data), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/acquisition/profiles/{acquisitionProfileId:long}")]
    public sealed class DeleteAcquisitionProfileV1 : AdminService<DeleteAcquisitionProfileParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DeleteAcquisitionProfileParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DeleteAcquisitionProfileParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteAcquisitionProfileAsync(new AcquisitionProfileKeyData(Parameters.AcquisitionProfileId, Parameters.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }

    internal static class AcquisitionProfileContracts
    {
        public static AcquisitionProfileInputData Input(long? id, SaveAcquisitionProfileDataV1 data) =>
            new(id, data.Name, data.MediaTypeId, data.MediaSubtypeId, data.IsDefault, data.AllowedQualities, data.UpgradeAllowed, data.UpgradeCutoffQuality,
                data.MinimumSizeMb, data.MaximumSizeMb, data.Version)
            {
                QualityOrder = data.QualityOrder,
                QualityGroups = [.. data.QualityGroups.Select(group => new QualityGroup(group.Id, group.Name, group.Qualities))],
                FormatScores = [.. data.FormatScores.Select(score => new FormatScore(score.FormatId, score.Score))],
                MinimumFormatScore = data.MinimumFormatScore,
                CutoffFormatScore = data.CutoffFormatScore,
                MinimumUpgradeFormatScore = data.MinimumUpgradeFormatScore,
                LanguageOrder = data.LanguageOrder,
                LanguageFallback = data.LanguageFallback,
                GoodGroupScore = data.GoodGroupScore,
                BadGroupScore = data.BadGroupScore
            };

        public static AcquisitionProfileDetailsV1 Read(AcquisitionProfileReadData row)
        {
            var definition = JsonSerializer.Deserialize<AcquisitionProfileDefinition>(row.Definition, AcquisitionSelectionLogic.DefinitionOptions)
                ?? throw new InvalidOperationException("The acquisition profile is invalid.");
            return new AcquisitionProfileDetailsV1(row.Id, row.Name, row.MediaTypeId, row.IsDefault, definition.AllowedQualities, definition.UpgradeAllowed,
                definition.UpgradeCutoffQuality, (int?)(definition.MinimumSizeBytes / 1048576), (int?)(definition.MaximumSizeBytes / 1048576), row.MediaSubtypeId,
                definition.QualityOrder, new BaseDataList<QualityGroupV1>(definition.QualityGroups.Select(group => new QualityGroupV1(group.Id, group.Name, group.Qualities))),
                new BaseDataList<CustomFormatScoreV1>(definition.FormatScores.Select(score => new CustomFormatScoreV1(score.FormatId, score.Score))),
                definition.MinimumFormatScore, definition.CutoffFormatScore, definition.MinimumUpgradeFormatScore, row.WantedCount, row.Version,
                [.. (definition.Monitoring?.PrimaryTypes ?? []).Concat(definition.Monitoring?.SecondaryTypes ?? []).Concat(definition.Monitoring?.ReleaseStatuses ?? [])],
                definition.Monitoring?.Languages ?? [], definition.Monitoring?.MinimumPages, definition.LanguageOrder, definition.LanguageFallback,
                definition.GoodGroupScore, definition.BadGroupScore);
        }
    }
}
