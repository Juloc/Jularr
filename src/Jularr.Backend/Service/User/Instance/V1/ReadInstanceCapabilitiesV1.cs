using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.User.Instance.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Instance.V1
{
    [ServiceEndpoint("GET", "/api/v1/instance/capabilities")]
    public sealed class ReadInstanceCapabilitiesV1 : UserService<ReadInstanceCapabilitiesParametersV1>
    {
        private readonly MediaOptions _media;

        internal ReadInstanceCapabilitiesV1(IOptions<MediaOptions> media)
        {
            _media = media.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<InstanceCapabilitiesDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadInstanceCapabilitiesParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadInstanceCapabilitiesParametersV1 parameters, Type resultType) => new("instance.capabilities.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read actual module availability.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    module."Key",
                    module."IsEnabled"
                FROM
                    "InstanceModules" AS module;
                """;
            var modules = await Context.ReadSql.ReadListAsync<InstanceModuleData>(sql, CancellationToken);

            // Seek thumbnails and translation also need their folder.
            var usable = modules.Where(module => module.IsEnabled && (module.Key != "Trickplay" || _media.TrickplayPath is not null)
                && (module.Key != "Translation" || _media.TranslationPath is not null) && (module.Key != "Ocr" || _media.OcrPath is not null)).Select(module => module.Key);
            var permissions = ServiceGate.GetPermissions(Auth.AccountId, Auth.ProfileId, Auth.ProfileOwnerAccountId, Auth.AccountRoleTypeId, Auth.IsDevice);
            return new InstanceCapabilitiesDetailsV1([.. permissions], [.. usable]);
        }
    }
}
