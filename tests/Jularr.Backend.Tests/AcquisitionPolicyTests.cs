using System.Net;
using System.Net.Http.Json;
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
            using var created = await host.SendAsync(HttpMethod.Put, Root + "custom-formats", new SaveCustomFormatDataV1(null, MediaType.Movie, "German", false, conditions, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var key = (await created.Content.ReadFromJsonAsync<SavedCustomFormatV1>())!;
            using var read = await host.SendAsync(HttpMethod.Get, Root + "custom-formats/" + key.Id);
            var format = (await read.Content.ReadFromJsonAsync<CustomFormatDetailsV1>())!;
            Assert.AreEqual(2, format.Conditions.Count);
            Assert.IsTrue(format.Conditions[1].Negate);
            using var edit = await host.SendAsync(HttpMethod.Put, Root + "custom-formats", new SaveCustomFormatDataV1(key.Id, MediaType.Movie, "German release", false, conditions, key.Version));
            Assert.AreEqual(HttpStatusCode.OK, edit.StatusCode, await edit.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, Root + "custom-formats", new SaveCustomFormatDataV1(key.Id, MediaType.Movie, "Stale", false, conditions, key.Version));
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
                using var read = await host.SendAsync(HttpMethod.Get, Root + "custom-formats?mediaTypeId=1&page=1&pageSize=25");
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
