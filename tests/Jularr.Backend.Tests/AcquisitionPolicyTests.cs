using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AcquisitionPolicyTests
    {
        private const string Root = "/api/v1/admin/acquisition/";

        [TestMethod]
        public async Task Formats_EditAndDelete_RejectStaleAndUsedDefinitions()
        {
            await using var host = await CreateHostAsync();
            var conditions = new BaseDataList<FormatConditionV1>([new("German", FormatConditionKind.Language, false, true, "de", false, null, null, null),
                new("Group", FormatConditionKind.ReleaseGroup, true, false, "bad", false, null, null, null)]);
            using var created = await host.SendAsync(HttpMethod.Put, Root + "custom-formats", new SaveCustomFormatDataV1(null, "German", false, conditions, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var key = (await created.Content.ReadFromJsonAsync<SavedCustomFormatV1>())!;
            using var read = await host.SendAsync(HttpMethod.Get, Root + "custom-formats/" + key.Id);
            var format = (await read.Content.ReadFromJsonAsync<CustomFormatDetailsV1>())!;
            Assert.AreEqual(2, format.Conditions.Count);
            Assert.IsTrue(format.Conditions[1].Negate);
            using var edit = await host.SendAsync(HttpMethod.Put, Root + "custom-formats", new SaveCustomFormatDataV1(key.Id, "German release", false, conditions, key.Version));
            Assert.AreEqual(HttpStatusCode.OK, edit.StatusCode, await edit.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, Root + "custom-formats", new SaveCustomFormatDataV1(key.Id, "Stale", false, conditions, key.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);

            await host.Fixture.RunSqlAsync($"UPDATE \"AcquisitionProfiles\" SET \"Definition\" = jsonb_set(\"Definition\", '{{FormatScores}}', '[{{\"FormatId\":{key.Id},\"Score\":100}}]') WHERE \"Id\" = 1");
            using var used = await host.SendAsync(HttpMethod.Delete, Root + "custom-formats/" + key.Id + "?version=2");
            Assert.AreEqual(HttpStatusCode.BadRequest, used.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"CustomFormats\" WHERE \"Id\" = {key.Id}"));
            await host.Fixture.RunSqlAsync("UPDATE \"AcquisitionProfiles\" SET \"Definition\" = \"Definition\" - 'FormatScores' WHERE \"Id\" = 1");
            using var deleted = await host.SendAsync(HttpMethod.Delete, Root + "custom-formats/" + key.Id + "?version=2");
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task AutoTags_FollowTheTitles_StayBesideAPersonsTags_AndCannotBeChanged()
        {
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "OriginalLanguage", "GenreIds") VALUES (9001, 1, 'Auto Movie', 'en', '{1,3}');
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (9101, 9001);
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId")
                SELECT 9201, 9001, profile."Id", profile."OwnerAccountId", 1, 1 FROM "Profiles" AS profile ORDER BY profile."Id" LIMIT 1;
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "OriginalLanguage") VALUES (9002, 1, 'Catalog only', 'fr');
                """);
            var profileId = await host.ScalarAsync("""SELECT "RequestedByProfileId" FROM "AcquisitionRequests" WHERE "Id" = 9201""");

            // A run gives the title with a target its media type, language, genres and requester; a title without a target gets none.
            await RefreshAsync();
            Assert.AreEqual($"genre:1,genre:3,kind:movie,language:en,requester:{profileId}", await Keys(9001));
            Assert.AreEqual("", await Keys(9002));
            using (var listed = await host.SendAsync(HttpMethod.Get, Root + "tags?automatic=true&page=1&pageSize=50"))
            {
                var tags = (await listed.Content.ReadFromJsonAsync<PageResult<AcquisitionTagDetailsV1>>())!.Items;
                Assert.AreEqual(5, tags.Count);
                Assert.IsNotNull(tags.Single(tag => tag.AutoKey!.StartsWith("requester:", StringComparison.Ordinal)).AutoName);
            }

            // A person's tag joins and leaves beside them; the kept ones stay.
            using var mine = await host.SendAsync(HttpMethod.Put, Root + "tags", new SaveAcquisitionTagDataV1(null, "mine", null));
            var mineId = (await mine.Content.ReadFromJsonAsync<SavedAcquisitionPolicyV1>())!.Id;
            var autoId = (long)(await host.ScalarAsync("""SELECT "Id" FROM "Tags" WHERE "AutoKey" = 'kind:movie'"""))!;
            await SetTagsAsync([mineId, autoId]);
            Assert.AreEqual(6L, await host.ScalarAsync("""SELECT count(*) FROM "WorkTags" WHERE "WorkId" = 9001"""));
            await SetTagsAsync([]);
            Assert.AreEqual(5L, await host.ScalarAsync("""SELECT count(*) FROM "WorkTags" WHERE "WorkId" = 9001"""));

            // A kept tag is neither renamed nor removed by hand.
            using (var renamed = await host.SendAsync(HttpMethod.Put, Root + "tags", new SaveAcquisitionTagDataV1(autoId, "renamed", 1)))
                Assert.AreEqual(HttpStatusCode.BadRequest, renamed.StatusCode);
            using (var removed = await host.SendAsync(HttpMethod.Delete, Root + "tags/" + autoId + "?version=1"))
                Assert.AreEqual(HttpStatusCode.BadRequest, removed.StatusCode);

            // Another language replaces the old tag, which leaves once nothing carries it.
            await host.Fixture.RunSqlAsync("""UPDATE "Works" SET "OriginalLanguage" = 'ja', "GenreIds" = '{1}' WHERE "Id" = 9001""");
            await RefreshAsync();
            Assert.AreEqual($"genre:1,kind:movie,language:ja,requester:{profileId}", await Keys(9001));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "Tags" WHERE "AutoKey" IN ('language:en', 'genre:3')"""));

            async Task RefreshAsync()
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<Service.System.Acquisition.V1.RefreshAutoTagsV1>()
                    .ExecuteAsync<UpdateResult>(new Data.System.Acquisition.V1.AutoTagParametersV1(), default);
            }

            async Task<string> Keys(long workId) => (string)(await host.ScalarAsync($"""
                SELECT coalesce(string_agg(tag."AutoKey", ',' ORDER BY tag."AutoKey"), '') FROM "WorkTags" AS tagged JOIN "Tags" AS tag ON tag."Id" = tagged."TagId"
                WHERE tagged."WorkId" = {workId} AND tag."AutoKey" IS NOT NULL
                """))!;

            async Task SetTagsAsync(long[] tagIds)
            {
                var version = (long)(await host.ScalarAsync("""SELECT "Version" FROM "WantedItems" WHERE "Id" = 9101"""))!;
                using var updated = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/9101", new Data.Admin.Wanted.V1.UpdateWantedDataV1(false, version, tagIds: tagIds));
                Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
            }
        }

        [TestMethod]
        public async Task Policies_TagReferencesAndMediaScope_ProtectUsedTags()
        {
            await using var host = await CreateHostAsync();
            using var tag = await host.SendAsync(HttpMethod.Put, Root + "tags", new SaveAcquisitionTagDataV1(null, "anime", null));
            Assert.AreEqual(HttpStatusCode.OK, tag.StatusCode, await tag.Content.ReadAsStringAsync());
            var key = (await tag.Content.ReadFromJsonAsync<SavedAcquisitionPolicyV1>())!;
            using var release = await host.SendAsync(HttpMethod.Put, Root + "release-profiles", new SaveReleaseProfileDataV1(null, MediaType.Series, "Anime", true, ["dual", "/german/i"], ["dubbed"], null, [key.Id, key.Id], null));
            Assert.AreEqual(HttpStatusCode.OK, release.StatusCode, await release.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT cardinality(\"TagIds\")::bigint FROM \"ReleaseProfiles\""));
            using var movie = await host.SendAsync(HttpMethod.Get, Root + "release-profiles?mediaTypeId=1&page=1&pageSize=25");
            Assert.AreEqual(HttpStatusCode.OK, movie.StatusCode, await movie.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await movie.Content.ReadFromJsonAsync<PageResult<ReleaseProfileDetailsV1>>())!.Items.Count);
            using var series = await host.SendAsync(HttpMethod.Get, Root + "release-profiles?mediaTypeId=2&page=1&pageSize=25");
            Assert.AreEqual(HttpStatusCode.OK, series.StatusCode, await series.Content.ReadAsStringAsync());
            Assert.AreEqual(1, (await series.Content.ReadFromJsonAsync<PageResult<ReleaseProfileDetailsV1>>())!.Items.Count);
            using var inUse = await host.SendAsync(HttpMethod.Delete, Root + "tags/" + key.Id + "?version=1");
            Assert.AreEqual(HttpStatusCode.BadRequest, inUse.StatusCode);
        }

        [TestMethod]
        public async Task Sizes_OneStaleRow_RollsTheWholeCatalogBack()
        {
            await using var host = await CreateHostAsync();
            using var read = await host.SendAsync(HttpMethod.Get, Root + "quality-definitions?mediaTypeId=1");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var original = (await read.Content.ReadFromJsonAsync<BaseDataList<QualityDefinitionV1>>())!;
            var edited = original.Select((row, index) => new QualityDefinitionV1(row.Quality, 1, 10, 20, index == 0 ? row.Version + 1 : row.Version)).ToArray();
            using var stale = await host.SendAsync(HttpMethod.Put, Root + "quality-definitions?mediaTypeId=1", new SaveQualityDefinitionsDataV1(new(edited)));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"QualityDefinitions\" WHERE \"Version\" <> 1"));
            var valid = original.Select(row => new QualityDefinitionV1(row.Quality, 1, 10, 20, row.Version)).ToArray();
            using var saved = await host.SendAsync(HttpMethod.Put, Root + "quality-definitions?mediaTypeId=1", new SaveQualityDefinitionsDataV1(new(valid)));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual((long)original.Count, await host.ScalarAsync("SELECT count(*) FROM \"QualityDefinitions\" WHERE \"Version\" = 2"));
        }

        [TestMethod]
        public async Task Policies_MediaManagerAndUser_CannotReadOrChangeConfiguration()
        {
            await using var host = await CreateHostAsync();
            foreach (var role in new[] { 3, 2 })
            {
                await host.Fixture.RunSqlAsync($"UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = {role} WHERE \"Id\" = 41");
                var denied = role == 3 ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
                using var read = await host.SendAsync(HttpMethod.Get, Root + "custom-formats?page=1&pageSize=25");
                Assert.AreEqual(denied, read.StatusCode);
                using var write = await host.SendAsync(HttpMethod.Put, Root + "tags", new SaveAcquisitionTagDataV1(null, "blocked", null));
                Assert.AreEqual(denied, write.StatusCode);
            }

            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Tags\""));
        }

        private static async Task<Host> CreateHostAsync()
        {
            var host = await Host.CreateAsync(library: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            return host;
        }
    }
}
