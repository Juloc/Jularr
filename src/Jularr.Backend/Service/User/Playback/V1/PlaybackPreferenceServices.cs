using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Playback.V1
{
    [ServiceEndpoint("GET", "/api/v1/playback/preferences")]
    public sealed class ReadPlaybackPreferencesV1 : UserService<PlaybackPreferencesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackPreferencesV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackPreferencesParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackPreferencesParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The profile's choices (or the defaults) beside the quality of the asking device.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    preference."AudioLanguage",
                    preference."SubtitleLanguage",
                    coalesce(preference."SubtitleModeTypeId", 4)::smallint AS "SubtitleModeTypeId",
                    coalesce(preference."Speed", 1)::numeric(3, 2) AS "Speed",
                    coalesce(preference."AutoplayNext", TRUE) AS "AutoplayNext",
                    CASE
                        WHEN lower(split_part(locale."Locale", '-', 1)) ~ '^[a-z]{2}$' THEN lower(split_part(locale."Locale", '-', 1))
                        ELSE 'en'
                    END AS "ProfileLanguage",
                    coalesce((
                        SELECT
                            session."PlaybackQuality"
                        FROM
                            "AccountSessions" AS session
                        WHERE
                            session."PublicId" = @SessionPublicId
                    ), 1)::smallint AS "DeviceQuality"
                FROM
                    "Profiles" AS profile
                    JOIN "UiLocales" AS locale ON locale."Id" = profile."UiLocaleId"
                    LEFT JOIN "ProfilePlaybackPreferences" AS preference ON preference."ProfileId" = profile."Id"
                WHERE
                    profile."Id" = @ProfileId;
                """;
            sql.Parameters.Add(new PlaybackPreferenceLookupData(Auth.ProfileId!.Value, Auth.SessionPublicId));
            return await Context.ReadSql.ReadRequiredAsync<PlaybackPreferencesV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/playback/preferences")]
    public sealed class SavePlaybackPreferencesV1 : UserService<PlaybackPreferencesParametersV1, SavePlaybackPreferencesDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackPreferencesParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackPreferencesParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SavePlaybackPreferencesAsync(new PlaybackPreferenceData(Auth.ProfileId!.Value, Data.AudioLanguage, Data.SubtitleLanguage, Data.SubtitleModeTypeId, Data.Speed,
                Data.AutoplayNext), CancellationToken);
            return new UpdateResult(true);
        }
    }

    // The player remembers the speed a viewer chose.
    [ServiceEndpoint("PUT", "/api/v1/playback/preferences/speed")]
    public sealed class SavePlaybackSpeedV1 : UserService<PlaybackPreferencesParametersV1, SavePlaybackSpeedDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackPreferencesParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackPreferencesParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SavePlaybackSpeedAsync(new PlaybackSpeedData(Auth.ProfileId!.Value, Data.Speed), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
