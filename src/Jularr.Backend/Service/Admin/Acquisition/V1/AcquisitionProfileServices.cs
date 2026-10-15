using Jularr.Backend.Data;
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
            // Few profiles; editor values come from the stored definition.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."Name",
                    profile."MediaTypeId",
                    profile."MediaSubtypeId",
                    profile."IsDefault",
                    ARRAY(SELECT jsonb_array_elements_text(profile."Definition" -> 'AllowedQualities')) AS "AllowedQualities",
                    coalesce((profile."Definition" ->> 'UpgradeAllowed')::boolean, FALSE) AS "UpgradeAllowed",
                    profile."Definition" ->> 'UpgradeCutoffQuality' AS "UpgradeCutoffQuality",
                    ((profile."Definition" ->> 'MinimumSizeBytes')::bigint / 1048576)::integer AS "MinimumSizeMb",
                    ((profile."Definition" ->> 'MaximumSizeBytes')::bigint / 1048576)::integer AS "MaximumSizeMb",
                    ARRAY(SELECT jsonb_array_elements_text(profile."Definition" -> 'MustContain')) AS "MustContain",
                    ARRAY(SELECT jsonb_array_elements_text(profile."Definition" -> 'MustNotContain')) AS "MustNotContain",
                    coalesce((profile."Definition" ->> 'DelayMinutes')::integer, 0) AS "DelayMinutes",
                    ARRAY(SELECT jsonb_array_elements_text(coalesce(profile."Definition" -> 'FallbackQualities', '[]'::jsonb))) AS "FallbackQualities",
                    (profile."Definition" ->> 'FallbackAfterHours')::integer AS "FallbackAfterHours",
                    (
                        SELECT
                            count(*)::integer
                        FROM
                            "WantedItems" AS wanted
                        WHERE
                            wanted."AcquisitionProfileId" = profile."Id"
                    ) AS "WantedCount",
                    profile."Version"
                FROM
                    "AcquisitionProfiles" AS profile
                ORDER BY
                    profile."MediaTypeId",
                    profile."MediaSubtypeId" NULLS FIRST,
                    profile."IsDefault" DESC,
                    profile."Name",
                    profile."Id";
                """;
            return new BaseDataList<AcquisitionProfileDetailsV1>(await Context.ReadSql.ReadListAsync<AcquisitionProfileDetailsV1>(sql, CancellationToken));
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
                    ARRAY(SELECT jsonb_array_elements_text(profile."Definition" -> 'AllowedQualities')) AS "AllowedQualities",
                    coalesce((profile."Definition" ->> 'UpgradeAllowed')::boolean, FALSE) AS "UpgradeAllowed",
                    profile."Definition" ->> 'UpgradeCutoffQuality' AS "UpgradeCutoffQuality",
                    ((profile."Definition" ->> 'MinimumSizeBytes')::bigint / 1048576)::integer AS "MinimumSizeMb",
                    ((profile."Definition" ->> 'MaximumSizeBytes')::bigint / 1048576)::integer AS "MaximumSizeMb",
                    ARRAY(SELECT jsonb_array_elements_text(profile."Definition" -> 'MustContain')) AS "MustContain",
                    ARRAY(SELECT jsonb_array_elements_text(profile."Definition" -> 'MustNotContain')) AS "MustNotContain",
                    coalesce((profile."Definition" ->> 'DelayMinutes')::integer, 0) AS "DelayMinutes",
                    ARRAY(SELECT jsonb_array_elements_text(coalesce(profile."Definition" -> 'FallbackQualities', '[]'::jsonb))) AS "FallbackQualities",
                    (profile."Definition" ->> 'FallbackAfterHours')::integer AS "FallbackAfterHours",
                    (
                        SELECT
                            count(*)::integer
                        FROM
                            "WantedItems" AS wanted
                        WHERE
                            wanted."AcquisitionProfileId" = profile."Id"
                    ) AS "WantedCount",
                    profile."Version"
                FROM
                    "AcquisitionProfiles" AS profile
                WHERE
                    profile."Id" = @AcquisitionProfileId;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<AcquisitionProfileDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/profiles")]
    public sealed class CreateAcquisitionProfileV1 : AdminService<CreateAcquisitionProfileParametersV1, SaveAcquisitionProfileDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateAcquisitionProfileResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateAcquisitionProfileParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(CreateAcquisitionProfileParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnCreate() =>
            new CreateAcquisitionProfileResultV1(await Context.CreateAcquisitionProfileAsync(AcquisitionProfileInput.Create(null, Data), CancellationToken));
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
            if (Data.Version is null)
            {
                throw new DataValidationException(nameof(SaveAcquisitionProfileDataV1.Version), "required");
            }

            await Context.UpdateAcquisitionProfileAsync(AcquisitionProfileInput.Create(Parameters.AcquisitionProfileId, Data), CancellationToken);
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

    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/profiles/{acquisitionProfileId:long}/rules")]
    public sealed class ReadAcquisitionProfileRulesV1 : AdminService<AcquisitionProfileParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ReleaseRuleV1>>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionProfileParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionProfileParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead() =>
            new BaseDataList<ReleaseRuleV1>((await Context.ReadProfileRulesAsync(Parameters.AcquisitionProfileId, CancellationToken))
                .Select((rule, position) => new ReleaseRuleV1(position, rule.Name, rule.Field, rule.Match, rule.Value, rule.EffectiveEffect, rule.Score)).ToArray());
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/profiles/{acquisitionProfileId:long}/rules")]
    public sealed class AddAcquisitionProfileRuleV1 : AdminService<AcquisitionProfileParametersV1, SaveReleaseRuleDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionProfileParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionProfileParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnCreate()
        {
            await Context.AddProfileRuleAsync(new AcquisitionProfileKeyData(Parameters.AcquisitionProfileId, Data.Version),
                new ReleaseScoreRule(Data.Name, Data.Field, Data.Match, Data.Value, Data.Score) { Effect = Data.Effect }, CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/profiles/{acquisitionProfileId:long}/rules/{position:int}")]
    public sealed class UpdateAcquisitionProfileRuleV1 : AdminService<ReleaseRulePositionParametersV1, SaveReleaseRuleDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReleaseRulePositionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReleaseRulePositionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateProfileRuleAsync(new AcquisitionProfileKeyData(Parameters.AcquisitionProfileId, Data.Version), Parameters.Position,
                new ReleaseScoreRule(Data.Name, Data.Field, Data.Match, Data.Value, Data.Score) { Effect = Data.Effect }, CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/acquisition/profiles/{acquisitionProfileId:long}/rules/{position:int}")]
    public sealed class DeleteAcquisitionProfileRuleV1 : AdminService<ReleaseRuleParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReleaseRuleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReleaseRuleParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.RemoveProfileRuleAsync(new AcquisitionProfileKeyData(Parameters.AcquisitionProfileId, Parameters.Version), Parameters.Position, CancellationToken);
            return new UpdateResult(true);
        }
    }

    internal static class AcquisitionProfileInput
    {
        public static AcquisitionProfileInputData Create(long? id, SaveAcquisitionProfileDataV1 data) =>
            new(id, data.Name, data.MediaTypeId, data.MediaSubtypeId, data.IsDefault, data.AllowedQualities, data.UpgradeAllowed, data.UpgradeCutoffQuality, data.MinimumSizeMb, data.MaximumSizeMb,
                data.MustContain, data.MustNotContain, data.Version, data.DelayMinutes, data.FallbackQualities, data.FallbackAfterHours);
    }
}
