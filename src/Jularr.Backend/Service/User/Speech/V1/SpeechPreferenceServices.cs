using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Speech;
using Jularr.Backend.Data.Logic.Speech;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Speech;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Speech.V1
{
    [ServiceEndpoint("GET", "/api/v1/profiles/{profileId:long}/speech")]
    public sealed class ReadSpeechPreferencesV1 : UserService<ReadProfileParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SpeechPreferencesV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadProfileParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadProfileParametersV1 parameters, Type resultType) => new("profile.self.read");
        protected override Task ValidatePermissions(ReadProfileParametersV1 parameters) =>
            parameters.ProfileId == Auth.ProfileId ? Task.CompletedTask : throw new KeyNotFoundException("The authorized profile was not found.");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    coalesce(settings."Rate", 1) AS "Rate",
                    coalesce(settings."Pitch", 1) AS "Pitch",
                    coalesce(settings."Volume", 1) AS "Volume",
                    coalesce(settings."Version", 0) AS "Version"
                FROM
                    "Profiles" AS profile
                    LEFT JOIN "ProfileSpeechSettings" AS settings ON settings."ProfileId" = profile."Id"
                WHERE
                    profile."Id" = @ProfileId;
                """;
            sql.Parameters.Add(Parameters);
            var settings = await Context.ReadRequiredAsync<SpeechSettingsData>(sql, CancellationToken);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    voice."Language",
                    voice."ProviderTypeId",
                    voice."ModelId",
                    voice."VoiceId"
                FROM
                    "ProfileSpeechVoices" AS voice
                WHERE
                    voice."ProfileId" = @ProfileId
                ORDER BY
                    voice."Language"
                LIMIT 32;
                """;
            sql.Parameters.Add(Parameters);
            var voices = await Context.ReadSql.ReadListAsync<SpeechVoiceV1>(sql, CancellationToken);
            return new SpeechPreferencesV1(settings.Rate, settings.Pitch, settings.Volume, settings.Version, new BaseDataList<SpeechVoiceV1>(voices));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/profiles/{profileId:long}/speech")]
    public sealed class SaveSpeechPreferencesV1 : UserService<UpdateProfileParametersV1, SaveSpeechPreferencesDataV1>
    {
        private readonly SpeechFiles _files;
        internal SaveSpeechPreferencesV1(SpeechFiles files) { _files = files; }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateProfileParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UpdateProfileParametersV1 parameters, Type resultType) => new("profile.self.update");
        protected override Task ValidatePermissions(UpdateProfileParametersV1 parameters) =>
            parameters.ProfileId == Auth.ProfileId ? Task.CompletedTask : throw new KeyNotFoundException("The authorized profile was not found.");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Device voice identifiers are device facts; only neural choices need the catalog.
            var model = Data.ProviderTypeId == SpeechProviderType.OfflineNeural
                ? (await _files.CatalogAsync(CancellationToken)).FirstOrDefault(item => item.Id == Data.ModelId)
                : null;
            await Context.SaveSpeechPreferencesAsync(new SpeechPreferencesData(Parameters.ProfileId, Data.Rate, Data.Pitch, Data.Volume, Data.Version,
                Data.Language, Data.ProviderTypeId, Data.ModelId, Data.VoiceId), model, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
