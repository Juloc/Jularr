using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    // Collections belong to one profile; only hand-picked ones take Works, and only Works the profile sees.
    internal static class CollectionLogic
    {
        public static async Task<long> CreateCollectionAsync(this LogicContext ctx, CollectionSaveData data, CancellationToken ct)
        {
            // A profile keeps at most 100 collections, so every list shows all of them.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                FOR UPDATE OF
                    profile;
                """;
            sql.Parameters.Add(data);
            _ = await ctx.ReadRequiredAsync<ProfileParameters>(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                    AND (SELECT count(*) FROM "Collections" AS collection WHERE collection."ProfileId" = profile."Id") < 100;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<ProfileParameters>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.Name), "collection_limit");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Collections" ("ProfileId", "Name", "IsSmart")
                VALUES
                    (@ProfileId, @Name, @IsSmart)
                ON CONFLICT ("ProfileId", "Name") DO NOTHING
                RETURNING
                    "Id" AS "CollectionId",
                    "ProfileId";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadOptionalAsync<CollectionOwnerData>(sql, ct) ?? throw new DataValidationException(nameof(data.Name), "duplicate_value")).CollectionId;
        }

        public static async Task UpdateCollectionAsync(this LogicContext ctx, CollectionSaveData data, CancellationToken ct)
        {
            // The kind comes from the collection itself.
            var current = await ctx.LockCollectionAsync(new CollectionOwnerData(data.ProfileId, data.CollectionId!.Value), ct);
            if (current.Version != data.Version)
            {
                throw new DataValidationException(nameof(data.Version), "outdated_version");
            }

            if (!current.IsSmart && (data.MediaTypeId is not null || data.MediaSubtypeId is not null || data.ReleaseYearFrom is not null || data.ReleaseYearTo is not null
                || data.OnlyAvailable || data.OnlyUnfinished))
            {
                throw new DataValidationException(nameof(data.IsSmart), "invalid_transition");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Collections" AS collection
                SET
                    "Name" = @Name,
                    "MediaTypeId" = @MediaTypeId,
                    "MediaSubtypeId" = @MediaSubtypeId,
                    "ReleaseYearFrom" = @ReleaseYearFrom,
                    "ReleaseYearTo" = @ReleaseYearTo,
                    "OnlyAvailable" = @OnlyAvailable,
                    "OnlyUnfinished" = @OnlyUnfinished,
                    "Version" = collection."Version" + 1,
                    "UpdatedAt" = now()
                WHERE
                    collection."Id" = @CollectionId
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "Collections" AS other
                        WHERE
                            other."ProfileId" = @ProfileId
                            AND other."Name" = @Name
                            AND other."Id" <> @CollectionId
                    );
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Name), "duplicate_value");
            }
        }

        public static async Task DeleteCollectionAsync(this LogicContext ctx, CollectionOwnerData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "Collections"
                WHERE
                    "Id" = @CollectionId
                    AND "ProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The collection was not found.");
            }
        }

        // New Works go to the end; the lock keeps positions apart.
        public static async Task AddCollectionWorkAsync(this LogicContext ctx, CollectionWorkData data, CancellationToken ct)
        {
            var current = await ctx.LockCollectionAsync(new CollectionOwnerData(data.ProfileId, data.CollectionId), ct);
            if (current.IsSmart)
            {
                throw new DataValidationException(nameof(data.CollectionId), "invalid_transition");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "CollectionWorks" ("CollectionId", "WorkId", "Position")
                SELECT
                    @CollectionId,
                    visible."WorkId",
                    coalesce(
                        (
                            SELECT
                                max(item."Position")
                            FROM
                                "CollectionWorks" AS item
                            WHERE
                                item."CollectionId" = @CollectionId
                        ),
                        0
                    ) + 1
                FROM
                    "ProfileWorks" AS visible
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND visible."WorkId" = @WorkId
                    AND NOT visible."IsHidden"
                ON CONFLICT ("CollectionId", "WorkId") DO UPDATE
                SET
                    "Position" = "CollectionWorks"."Position";
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The title was not found.");
            }
        }

        public static async Task RemoveCollectionWorkAsync(this LogicContext ctx, CollectionWorkData data, CancellationToken ct)
        {
            await ctx.LockCollectionAsync(new CollectionOwnerData(data.ProfileId, data.CollectionId), ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "CollectionWorks"
                WHERE
                    "CollectionId" = @CollectionId
                    AND "WorkId" = @WorkId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        private static async Task<CollectionStateData> LockCollectionAsync(this LogicContext ctx, CollectionOwnerData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    collection."IsSmart",
                    collection."Version"
                FROM
                    "Collections" AS collection
                WHERE
                    collection."Id" = @CollectionId
                    AND collection."ProfileId" = @ProfileId
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadOptionalAsync<CollectionStateData>(sql, ct) ?? throw new KeyNotFoundException("The collection was not found.");
        }
    }
}
