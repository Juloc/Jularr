using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Profile;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Profile.V1
{
    [ServiceEndpoint("GET", "/api/v1/profiles/{profileId:long}")]
    public sealed class ReadProfileV1 : UserService<ReadProfileParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() =>
            [ServiceResultType.Default<ReadProfileResultV1>()];

        protected override ModuleRequirement GetInstanceModules(ReadProfileParametersV1 parameters) =>
            ModuleRequirement.None;

        protected override ServicePermission GetPermission(ReadProfileParametersV1 parameters, Type resultType) =>
            new("profile.self.read");

        protected override Task ValidatePermissions(ReadProfileParametersV1 parameters)
        {
            if (parameters.ProfileId != Auth.ProfileId)
            {
                throw new KeyNotFoundException("The authorized profile was not found.");
            }

            return Task.CompletedTask;
        }

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."DisplayName",
                    profile."UiLocaleId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId;
                """;
            sql.Parameters.Add(Parameters);

            return await Context.ReadRequiredAsync<ReadProfileResultV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/profiles/{profileId:long}")]
    public sealed class UpdateProfileV1 : UserService<UpdateProfileParametersV1, UpdateProfileDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() =>
            [ServiceResultType.Default<UpdateResult>()];

        protected override ModuleRequirement GetInstanceModules(UpdateProfileParametersV1 parameters) =>
            ModuleRequirement.None;

        protected override ServicePermission GetPermission(UpdateProfileParametersV1 parameters, Type resultType) =>
            new("profile.self.update");

        protected override Task ValidatePermissions(UpdateProfileParametersV1 parameters)
        {
            if (parameters.ProfileId != Auth.ProfileId)
            {
                throw new KeyNotFoundException("The authorized profile was not found.");
            }

            return Task.CompletedTask;
        }

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var updateData = await Context.ReadProfileForUpdateAsync(Parameters.ProfileId, CancellationToken);

            // Parameter and entity IDs have different names, so assign explicitly.
            updateData.Id = Parameters.ProfileId;
            updateData.SetData(Data);

            await Context.UpdateProfileAsync(updateData, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
