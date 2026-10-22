using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Logic.Narou;

namespace Jularr.Backend.Service.Core
{
    // Narō entries are standalone, public Japanese web-novel Works.
    // Do not use SaveWorkMetadataAsync's cross-provider book-title matching here:
    // a published AniList Light Novel and its web original must not merge.
    internal sealed class NarouCatalog(NarouClient client)
    {
        public const string Namespace = "syosetu";

        public Task<IReadOnlyList<NarouSearchResult>> SearchAsync(string query, CancellationToken ct) =>
            client.SearchAsync(query, ct);

        public Task<NarouWork> ReadAsync(string ncode, CancellationToken ct) =>
            client.ReadWorkAsync(ncode, ct);

        public async Task<long> ImportAsync(LogicContext context, string ncode, CancellationToken ct)
        {
            if (!NarouClient.IsNcode(ncode))
                throw new KeyNotFoundException("Invalid Narō Ncode.");
            var work = await ReadAsync(ncode, ct);
            return await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var identity = new WorkIdentityProbeData(Namespace, work.Ncode);
                var lockSql = new SqlStatement
                {
                    Statement = "SELECT pg_advisory_xact_lock(hashtext('syosetu:' || @Value));"
                };
                lockSql.Parameters.Add(identity);
                await ctx.ReadSql.ExecuteScalarAsync(lockSql, token);

                var sql = new SqlStatement
                {
                    Statement = """
                        SELECT works."Id"
                        FROM "WorkExternalIdentities" AS identity
                        JOIN "Works" AS works ON works."Id" = identity."WorkId"
                        WHERE identity."Namespace" = @Namespace AND identity."Value" = @Value
                        FOR UPDATE OF works;
                        """
                };
                sql.Parameters.Add(identity);
                if (await ctx.ReadOptionalAsync<WorkIdData>(sql, token) is { } existing)
                    return existing.Id;

                var title = work.Title.Length <= 500 ? work.Title : work.Title[..500];
                var created = await ctx.CreateWorkAsync(
                    new WorkCreationData(MediaType.Book, MediaSubtype.LightNovel, true, title), token);

                sql = new SqlStatement
                {
                    Statement = """
                        INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value", "IsManual")
                        VALUES (@WorkId, @Namespace, @Value, TRUE);
                        """
                };
                sql.Parameters.Add(new WorkIdentityData(Namespace, work.Ncode, created.Id));
                await ctx.ExecuteAsyncForAffectedRows(sql, token);

                sql = new SqlStatement
                {
                    Statement = """
                        UPDATE "Works" SET "OriginalLanguage" = 'ja' WHERE "Id" = @Id;
                        """
                };
                sql.Parameters.Add(new WorkIdData(created.Id));
                await ctx.ExecuteAsyncForAffectedRows(sql, token);

                if (!string.IsNullOrWhiteSpace(work.Author))
                {
                    await ctx.SaveWorkPeopleAsync(WorkPeopleLogic.Data(created.Id, Namespace,
                    [
                        new
                        {
                            ExternalId = (string?)null,
                            Name = work.Author.Length <= 300 ? work.Author : work.Author[..300],
                            ImageUrl = (string?)null,
                            PersonRoleId = 7,
                            Position = 1,
                            Credit = (string?)null,
                            WorkUnitTypeId = (int?)null,
                            ParentNumber = (decimal?)null,
                            Number = (decimal?)null,
                            EditionLanguage = (string?)null,
                            EditionFormatId = (int?)null
                        }
                    ]), token);
                }

                return created.Id;
            }, ct);
        }
    }
}
