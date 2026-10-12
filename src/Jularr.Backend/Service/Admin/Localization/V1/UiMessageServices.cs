using System.Data;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Localization.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Localization;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Localization;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Localization;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Localization.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/localization/messages")]
    public sealed class ReadUiMessagesV1 : AdminService<ReadUiMessagesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<UiMessageDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadUiMessagesParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadUiMessagesParametersV1 parameters, Type resultType) => new("localization.read");
        protected override PageRequest GetPageRequest(ReadUiMessagesParametersV1 parameters) => new(parameters.Page, parameters.PageSize);
        protected override IsolationLevel? GetTransaction(ReadUiMessagesParametersV1 parameters) => IsolationLevel.RepeatableRead;

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read editable catalog rows.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    message."Id",
                    message."Key",
                    message."EnglishText",
                    message."Description",
                    message."Version",
                    translation."Text",
                    translation."UiTranslationStatusTypeId",
                    translation."Version" AS "TranslationVersion",
                    translation."MessageVersion"
                FROM
                    "UiTranslationMessages" AS message
                LEFT JOIN
                    "UiTranslations" AS translation ON translation."UiTranslationMessageId" = message."Id"
                        AND translation."UiLocaleId" = @UiLocaleId
                ORDER BY
                    message."Key",
                    message."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new UiLocaleParameters(Parameters.UiLocaleId));
            sql.Page = Page;

            var rows = await Context.ReadSql.ReadPageAsync<UiMessageDetailsV1>(sql, CancellationToken);

            // Probe the next row exactly.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT EXISTS (
                    SELECT
                        message."Id"
                    FROM
                        "UiTranslationMessages" AS message
                    ORDER BY
                        message."Key",
                        message."Id"
                    LIMIT 1
                    OFFSET (@Offset + @PageSize)
                );
                """;
            sql.Page = Page;
            var hasMore = (bool)(await Context.ReadSql.ExecuteScalarAsync(sql, CancellationToken))!;
            return new PageResult<UiMessageDetailsV1>(rows.Items, Page, hasMore: hasMore);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/localization/messages/{uiTranslationMessageId:long}/{uiLocaleId:long}")]
    public sealed class ReadUiMessageV1 : AdminService<UiMessageParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UiMessageDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(UiMessageParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UiMessageParametersV1 parameters, Type resultType) => new("localization.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Never hide stale translations.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    message."Id",
                    message."Key",
                    message."EnglishText",
                    message."Description",
                    message."Version",
                    translation."Text",
                    translation."UiTranslationStatusTypeId",
                    translation."Version" AS "TranslationVersion",
                    translation."MessageVersion"
                FROM
                    "UiTranslationMessages" AS message
                LEFT JOIN
                    "UiTranslations" AS translation ON translation."UiTranslationMessageId" = message."Id"
                        AND translation."UiLocaleId" = @UiLocaleId
                WHERE
                    message."Id" = @UiTranslationMessageId;
                """;
            sql.Parameters.Add(Parameters);

            return await Context.ReadRequiredAsync<UiMessageDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/localization/messages/{uiTranslationMessageId:long}/{uiLocaleId:long}/description")]
    public sealed class UpdateUiDescriptionV1 : AdminService<UiMessageParametersV1, UpdateUiDescriptionDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UiMessageParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UiMessageParametersV1 parameters, Type resultType) => new("localization.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Update database-owned context.
            await Context.UpdateUiDescriptionAsync(new UiDescriptionUpdateData(Parameters.UiTranslationMessageId, Data.MessageVersion, Data.Description), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/localization/messages/{uiTranslationMessageId:long}/{uiLocaleId:long}/translation")]
    public sealed class SaveUiTranslationV1 : AdminService<UiMessageParametersV1, SaveUiTranslationDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UiMessageParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UiMessageParametersV1 parameters, Type resultType) => new("localization.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Human edits stay manual.
            var data = new UiTranslationSaveData(Parameters.UiLocaleId, Parameters.UiTranslationMessageId, Data.MessageVersion, Data.Text,
                UiTranslationStatusType.Manual, Data.TranslationVersion);
            await Context.SaveUiTranslationAsync(data, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
