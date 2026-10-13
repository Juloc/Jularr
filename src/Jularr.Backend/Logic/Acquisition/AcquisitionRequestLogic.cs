using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Localization;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class AcquisitionRequestLogic
    {
        public static async Task<AcquisitionRequestData> CreateAcquisitionRequestAsync(this LogicContext ctx, AcquisitionRequestCreationData data, CancellationToken ct)
        {
            // Lock an enabled language.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    locale."Id"
                FROM
                    "UiLocales" AS locale
                WHERE
                    locale."Id" = @UiLocaleId
                    AND locale."IsEnabled"
                FOR SHARE OF
                    locale;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<UiLocaleData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.UiLocaleId), "unknown_reference");
            }

            // Only Works the profile may open; a game names one of its platforms, nothing else names one.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."MediaTypeId" = 7 AS "IsGame",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkPlatforms" AS listed
                        WHERE
                            listed."WorkId" = work."Id"
                            AND listed."GamePlatformId" = @GamePlatformId
                    ) AS "IsPlatformListed"
                FROM
                    "ProfileWorks" AS visible
                    JOIN "Works" AS work ON work."Id" = visible."WorkId"
                WHERE
                    visible."ProfileId" = @RequestedByProfileId
                    AND work."Id" = @WorkId;
                """;
            sql.Parameters.Add(data);
            var work = await ctx.ReadOptionalAsync<RequestPlatformCheckData>(sql, ct) ?? throw new KeyNotFoundException("The requested work was not found.");

            // A game is requested for one of its platforms; other media name none.
            if (work.IsGame && data.GamePlatformId is null)
            {
                throw new DataValidationException(nameof(data.GamePlatformId), "required");
            }

            if (work.IsGame != work.IsPlatformListed)
            {
                throw new DataValidationException(nameof(data.GamePlatformId), "unknown_reference");
            }

            // Repeated submissions share intent.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "AcquisitionRequests" (
                    "WorkId",
                    "RequestedByProfileId",
                    "UiLocaleId",
                    "GamePlatformId"
                )
                VALUES (
                    @WorkId,
                    @RequestedByProfileId,
                    @UiLocaleId,
                    @GamePlatformId
                )
                ON CONFLICT ("RequestedByProfileId", "WorkId", "UiLocaleId", "GamePlatformId")
                    WHERE "AcquisitionRequestStatusTypeId" IN (1, 2, 3, 4, 5)
                DO UPDATE SET
                    "WorkId" = EXCLUDED."WorkId"
                RETURNING
                    "Id",
                    "WorkId",
                    "GamePlatformId";
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadRequiredAsync<AcquisitionRequestData>(sql, ct);
        }

        public static async Task UpdateAcquisitionRequestAsync(this LogicContext ctx, AcquisitionRequestUpdateData data, CancellationToken ct)
        {
            // Only explicit moderation transitions.
            if (data.AcquisitionRequestStatusTypeId is not (AcquisitionRequestStatusType.Approved or AcquisitionRequestStatusType.Rejected))
            {
                throw new DataValidationException(nameof(data.AcquisitionRequestStatusTypeId), "invalid_transition");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionRequests" AS request
                SET
                    "AcquisitionRequestStatusTypeId" = @AcquisitionRequestStatusTypeId,
                    "DecidedByAccountId" = @ActorAccountId,
                    "DecidedAt" = now(),
                    "UpdatedAt" = now(),
                    "Version" = request."Version" + 1
                WHERE
                    request."Id" = @Id
                    AND request."Version" = @Version
                    AND request."AcquisitionRequestStatusTypeId" = 1
                RETURNING
                    request."Id",
                    request."WorkId",
                    request."GamePlatformId";
                """;
            sql.Parameters.Add(data);
            var request = await ctx.ReadOptionalAsync<AcquisitionRequestData>(sql, ct)
                ?? throw new DataValidationException(nameof(data.Version), "invalid_transition");

            // Approval and target are atomic.
            if (data.AcquisitionRequestStatusTypeId == AcquisitionRequestStatusType.Approved)
            {
                await ctx.RecordRequestTargetAsync(request, ct);
            }
        }
    }
}
