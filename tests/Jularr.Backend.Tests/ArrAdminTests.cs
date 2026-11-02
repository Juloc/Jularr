using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ArrAdminTests
    {
        private const string Root = "/api/v1/admin/acquisition/";

        [TestMethod]
        public async Task JsonFormats_KeepEveryCondition_AndImportAtomically()
        {
            await using var host = await CreateAsync();
            const string Export = """
                {"name":"Full movie format","includeCustomFormatWhenRenaming":true,"specifications":[
                  {"name":"Title","implementation":"ReleaseTitleSpecification","required":true,"fields":{"value":" HDR "}},
                  {"name":"Edition","implementation":"EditionSpecification","negate":true,"fields":{"value":"uncut"}},
                  {"name":"Language","implementation":"LanguageSpecification","fields":{"value":1,"exceptLanguage":true}},
                  {"name":"Group","implementation":"ReleaseGroupSpecification","fields":{"value":"^TEAM$"}},
                  {"name":"Source","implementation":"SourceSpecification","fields":{"value":7}},
                  {"name":"Resolution","implementation":"ResolutionSpecification","fields":{"value":1080}},
                  {"name":"Modifier","implementation":"QualityModifierSpecification","fields":{"value":5}},
                  {"name":"Flag","implementation":"IndexerFlagSpecification","fields":{"value":1}},
                  {"name":"Size","implementation":"SizeSpecification","fields":{"min":1.5,"max":50}},
                  {"name":"Year","implementation":"YearSpecification","fields":{"min":1999,"max":2025}}
                ]}
                """;
            using var created = await ImportAsync(Export);
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var keys = (await created.Content.ReadFromJsonAsync<BaseDataList<SavedCustomFormatV1>>())!;
            var key = keys.Single();
            using var read = await host.SendAsync(HttpMethod.Get, Root + "custom-formats/" + key.Id);
            var format = (await read.Content.ReadFromJsonAsync<CustomFormatDetailsV1>())!;
            Assert.AreEqual(10, format.Conditions.Count);
            Assert.IsTrue(format.IncludeWhenRenaming);
            Assert.IsTrue(format.Conditions[0].Required);
            Assert.AreEqual(" HDR ", format.Conditions[0].Value);
            Assert.IsTrue(format.Conditions[1].Negate);
            Assert.AreEqual(("en", true), (format.Conditions[2].Value, format.Conditions[2].ExceptLanguage));
            Assert.AreEqual("webdl", format.Conditions[4].Value);
            Assert.AreEqual(1080, format.Conditions[5].Resolution);
            Assert.AreEqual("remux", format.Conditions[6].Value);
            Assert.AreEqual("G_Freeleech", format.Conditions[7].Value);
            Assert.AreEqual(((decimal?)1.5m, (decimal?)50), (format.Conditions[8].Minimum, format.Conditions[8].Maximum));

            using var repeated = await ImportAsync(Export);
            Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode, await repeated.Content.ReadAsStringAsync());
            var again = (await repeated.Content.ReadFromJsonAsync<BaseDataList<SavedCustomFormatV1>>())!.Single();
            Assert.AreEqual((key.Id, key.Version), (again.Id, again.Version));

            var changed = Export.Replace("\"value\":\" HDR \"", "\"value\":\" SDR \"", StringComparison.Ordinal);
            var fresh = Export.Replace("Full movie format", "Must roll back", StringComparison.Ordinal);
            using var conflict = await ImportAsync("[" + fresh + "," + changed + "]");
            Assert.AreEqual(HttpStatusCode.BadRequest, conflict.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "CustomFormats" WHERE "Name" = 'Must roll back' """));
            using var invalid = await ImportAsync("[" + fresh + "," + Export.Replace("\"negate\":true", "\"negate\":\"true\"", StringComparison.Ordinal) + "]");
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "CustomFormats" WHERE "Name" = 'Must roll back' """));
            using var malformed = await ImportAsync("[42]");
            Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);

            Task<HttpResponseMessage> ImportAsync(string json) => host.SendAsync(HttpMethod.Post, Root + "custom-formats/import",
                new ImportCustomFormatsDataV1(MediaType.Movie, TakeoverSourceType.Radarr, json));
        }

        [TestMethod]
        public async Task Groups_RenameKeepsIdentity_AndCrossMediaFormatsAreRefused()
        {
            await using var host = await CreateAsync();
            using var created = await host.SendAsync(HttpMethod.Post, Root + "profiles", Input("Web group"));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var key = (await created.Content.ReadFromJsonAsync<CreateAcquisitionProfileResultV1>())!;
            var profile = await ReadAsync();
            Assert.AreEqual("group:123", profile.UpgradeCutoffQuality);
            using var renamed = await host.SendAsync(HttpMethod.Put, Root + "profiles/" + key.Id, Input("Renamed group", profile.Version));
            Assert.AreEqual(HttpStatusCode.OK, renamed.StatusCode, await renamed.Content.ReadAsStringAsync());
            profile = await ReadAsync();
            CollectionAssert.AreEqual(new[] { "group:123" }, profile.AllowedQualities);
            Assert.AreEqual("Renamed group", profile.QualityGroups.Single().Name);
            Assert.AreEqual("group:123", profile.UpgradeCutoffQuality);
            using var wrongType = await host.SendAsync(HttpMethod.Post, Root + "profiles",
                new SaveAcquisitionProfileDataV1("Wrong type", MediaType.Movie, false, ["WEBDL-1080p"], false, null, null, null,
                    formatScores: new([new CustomFormatScoreV1(3, 500)])));
            Assert.AreEqual(HttpStatusCode.BadRequest, wrongType.StatusCode);

            SaveAcquisitionProfileDataV1 Input(string groupName, long? version = null) => new("Grouped", MediaType.Series, false, ["group:123"], true, "group:123",
                null, null, version, qualityGroups: new([new QualityGroupV1(123, groupName, ["WEBDL-1080p", "WEBRip-1080p"])]));
            async Task<AcquisitionProfileDetailsV1> ReadAsync()
            {
                using var response = await host.SendAsync(HttpMethod.Get, Root + "profiles/" + key.Id);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                return (await response.Content.ReadFromJsonAsync<AcquisitionProfileDetailsV1>())!;
            }
        }

        [TestMethod]
        public async Task AdminTabs_RenderOneNavigation_AndNativeFormsPersistChanges()
        {
            await using var host = await CreateAsync(razor: true);
            using var overview = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles");
            Assert.AreEqual(HttpStatusCode.OK, overview.StatusCode, string.Join(";", overview.Headers.Select(header => header.Key + "=" + string.Join(",", header.Value))));
            foreach (var tab in new[] { "profiles", "formats", "sizes", "release", "groups", "tags" })
            {
                using var response = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles?tab=" + tab + "&mediaTypeId=1");
                var html = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, html);
                Assert.AreEqual(2, html.Split("class=\"navigation-tabs navigation-tabs--filters\"", StringSplitOptions.None).Length);
                Assert.IsFalse(html.Contains("delay", StringComparison.OrdinalIgnoreCase), "A delay is offered on " + tab);
                Assert.IsFalse(html.Contains("FallbackQualities", StringComparison.Ordinal));
                if (tab == "tags")
                {
                    using var saved = await host.SendAsync(HttpMethod.Post, "/Admin/Acquisition/Profiles?tab=tags&mediaTypeId=1&handler=Tag",
                        new FormUrlEncodedContent([new("__RequestVerificationToken", LibraryTests.Token(html)), new("Label", "arr-test")]));
                    Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
                }
            }
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Tags" WHERE "Label" = 'arr-test' """));
        }

        [TestMethod]
        public async Task ReleaseGroupsTab_ListsNotRatedFirst_AndSavesARatingFromItsSegments()
        {
            await using var host = await CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ReleaseGroups" ("Id", "MediaTypeId", "Name", "ReleaseGroupRatingId", "SeenCount", "LastSeenAt") VALUES
                    (901, 1, 'GRP', 1, 14, now() - interval '2 hours'),
                    (902, 1, 'FLUX', NULL, 5, now() - interval '3 days'),
                    (903, 2, 'SeriesOnly', NULL, 1, now());
                """);

            // The media type's groups: its figures, the find-or-add field, not rated first, one segmented control per group with the rating chosen.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles?tab=groups&mediaTypeId=1");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            Assert.AreEqual(2, html.Split("class=\"navigation-tabs navigation-tabs--filters\"", StringSplitOptions.None).Length);
            StringAssert.Contains(html, "navigation-tabs__link navigation-tabs__link--active\" aria-current=\"page\">Release groups</a>");
            StringAssert.Contains(html, "placeholder=\"Find or add a group\"");
            StringAssert.Contains(html, "Not rated &#xB7; seen 5 times, last 3 days ago");
            StringAssert.Contains(html, "seen 14 times, last 2 hours ago");
            Assert.IsTrue(html.IndexOf(">FLUX<", StringComparison.Ordinal) < html.IndexOf(">GRP<", StringComparison.Ordinal), "Not rated groups come first.");
            Assert.IsFalse(html.Contains("SeriesOnly", StringComparison.Ordinal));
            Assert.AreEqual(2, html.Split("class=\"ui-segments\"", StringSplitOptions.None).Length - 1);
            Assert.AreEqual(1, html.Split("value=\"1\" checked=\"checked\"", StringSplitOptions.None).Length - 1);
            StringAssert.Contains(html, "data-form-submit=\"\"");
            StringAssert.Contains(html, "id=\"release-groups\" data-region=\"\"");
            Assert.AreEqual(6, html.Split("data-form-clearable=\"\"", StringSplitOptions.None).Length - 1);
            Assert.AreEqual(1, html.Split("title=\"Click again to clear\"", StringSplitOptions.None).Length - 1);
            foreach (var caption in new[] { "Groups", "Good", "Not rated", "Blocked" }) StringAssert.Contains(html, "kpi-tile__caption\">" + caption + "<");

            // A segment saves the rating at once; the field adds a group and shows it.
            using var rated = await host.SendAsync(HttpMethod.Post, "/Admin/Acquisition/Profiles?tab=groups&mediaTypeId=1&edit=902&handler=Rate",
                new FormUrlEncodedContent([new("__RequestVerificationToken", LibraryTests.Token(html)), new("ReleaseGroupRatingId", "3")]));
            Assert.AreEqual(HttpStatusCode.Redirect, rated.StatusCode, await rated.Content.ReadAsStringAsync());
            Assert.AreEqual(3, Convert.ToInt32(await host.ScalarAsync("""SELECT "ReleaseGroupRatingId" FROM "ReleaseGroups" WHERE "Id" = 902""")));

            // The chosen segment clicked again sends no rating: the group is not rated again.
            using var cleared = await host.SendAsync(HttpMethod.Post, "/Admin/Acquisition/Profiles?tab=groups&mediaTypeId=1&edit=901&handler=Rate",
                new FormUrlEncodedContent([new("__RequestVerificationToken", LibraryTests.Token(html))]));
            Assert.AreEqual(HttpStatusCode.Redirect, cleared.StatusCode, await cleared.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "ReleaseGroupRatingId" IS NULL FROM "ReleaseGroups" WHERE "Id" = 901"""));
            var sorted = await (await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles?tab=groups&mediaTypeId=1")).Content.ReadAsStringAsync();
            Assert.IsTrue(sorted.IndexOf(">GRP<", StringComparison.Ordinal) < sorted.IndexOf(">FLUX<", StringComparison.Ordinal), "The group no longer rated comes first again.");
            StringAssert.Contains(sorted, "kpi-tile__value\">1</div><div class=\"kpi-tile__caption\">Blocked<");
            using var added = await host.SendAsync(HttpMethod.Post, "/Admin/Acquisition/Profiles?tab=groups&mediaTypeId=1&handler=Group",
                new FormUrlEncodedContent([new("__RequestVerificationToken", LibraryTests.Token(html)), new("Name", " danke-Empire ")]));
            Assert.AreEqual(HttpStatusCode.Redirect, added.StatusCode, await added.Content.ReadAsStringAsync());
            StringAssert.Contains(added.Headers.Location!.OriginalString, "q=danke-Empire");
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "ReleaseGroups" WHERE "MediaTypeId" = 1 AND "Name" = 'danke-Empire' AND "SeenCount" = 0"""));
            var found = await (await host.SendAsync(HttpMethod.Get, added.Headers.Location.OriginalString)).Content.ReadAsStringAsync();
            StringAssert.Contains(found, "Not rated &#xB7; added by hand, not seen yet");
            Assert.IsFalse(found.Contains(">GRP<", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task ProfileEditor_ScoresEveryFormat_AndKeepsOnlyScoresThatCount()
        {
            await using var host = await CreateAsync(razor: true);
            using var imported = await host.SendAsync(HttpMethod.Post, Root + "custom-formats/import", new ImportCustomFormatsDataV1(MediaType.Movie, TakeoverSourceType.Radarr,
                """{"name":"Matrix format","specifications":[{"name":"Title","implementation":"ReleaseTitleSpecification","fields":{"value":"HDR"}}]}"""));
            var format = (await imported.Content.ReadFromJsonAsync<BaseDataList<SavedCustomFormatV1>>())!.Single();
            var profileId = (long)(await host.ScalarAsync("""SELECT "Id" FROM "AcquisitionProfiles" WHERE "MediaTypeId" = 1 AND "IsDefault" """))!;
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"AcquisitionProfiles\" WHERE \"Id\" = " + profileId))!;

            // The editor lists every format of the media type with its score, not only those already scored.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles/" + profileId);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Matrix format");
            StringAssert.Contains(html, "name=\"FormatScores[0].Score\"");
            Assert.IsFalse(html.Contains("addFormatId", StringComparison.Ordinal));

            // A score is saved with the profile; a format left at zero is not stored.
            using var saved = await host.SendAsync(HttpMethod.Post, "/Admin/Acquisition/Profiles/" + profileId, new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", LibraryTests.Token(html)), new("MediaTypeId", "1"), new("Version", version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("Name", "Matrix"), new("IsDefault", "True"), new("AllowedQualities", "WEBDL-1080p"), new("MinimumFormatScore", "10"),
                new("FormatScores[0].FormatId", format.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("FormatScores[0].Score", "75"),
                new("FormatScores[1].FormatId", "999999"), new("FormatScores[1].Score", "0")
            ]));
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var stored = await host.SendAsync(HttpMethod.Get, Root + "profiles/" + profileId);
            var scores = (await stored.Content.ReadFromJsonAsync<AcquisitionProfileDetailsV1>())!.FormatScores;
            Assert.AreEqual(1, scores.Count);
            Assert.AreEqual((format.Id, 75), (scores[0].FormatId, scores[0].Score));

            // The format list shows it as used and its editor is a dialog; no page offers a waiting time.
            using var formats = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles?tab=formats&mediaTypeId=1&edit=" + format.Id);
            var list = await formats.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, formats.StatusCode, list);
            StringAssert.Contains(list, "data-open=\"true\"");
            StringAssert.Contains(list, "id=\"format-editor\"");
            Assert.IsFalse(list.Contains("delay", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public async Task FormatCutoff_TracksStoredEvidenceAndConditionChanges_WithinQualityGroups()
        {
            await using var host = await CreateAsync();
            using var created = await host.SendAsync(HttpMethod.Post, Root + "profiles",
                new SaveAcquisitionProfileDataV1("Scored group", MediaType.Movie, false, ["group:55"], true, "group:55", null, null,
                    qualityGroups: new([new QualityGroupV1(55, "Web", ["WEBDL-1080p", "WEBRip-1080p"])]),
                    formatScores: new([new CustomFormatScoreV1(1, 100)]), cutoffFormatScore: 100));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var profile = (await created.Content.ReadFromJsonAsync<CreateAcquisitionProfileResultV1>())!;
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "StorageMounts" ("Id", "Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") OVERRIDING SYSTEM VALUE VALUES (801, 'Scored', 3, '/media/scored', FALSE);
                INSERT INTO "LibraryRoots" ("Id", "Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES (801, 'Scored', 1, 801, 'movies');
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (801, 1, 'Scored Film', 2020);
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (801, 801);
                INSERT INTO "WorkEditions" ("Id", "WorkId") VALUES (801, 801);
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") VALUES (801, 801, 801);
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (801, 801, 801, 1);
                INSERT INTO "StoredFiles" ("Id", "LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId", "QualityKey", "ReleaseName")
                    VALUES (801, 801, 'Scored Film.mkv', 1000000000, now(), now(), 801, 'WEBRip-1080p', 'Scored.Film.2020.1080p.WEBRip.x264-TEAM');
                """);
            await host.Fixture.RunSqlAsync($"UPDATE \"WantedItems\" SET \"AcquisitionProfileId\" = {profile.Id} WHERE \"Id\" = 801");
            using var tagCreated = await host.SendAsync(HttpMethod.Put, Root + "tags", new SaveAcquisitionTagDataV1(null, "scored", null));
            var tag = (await tagCreated.Content.ReadFromJsonAsync<SavedAcquisitionPolicyV1>())!;
            using var tagged = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/801", new UpdateWantedDataV1(false, 1, profile.Id, tagIds: [tag.Id]));
            Assert.AreEqual(HttpStatusCode.OK, tagged.StatusCode, await tagged.Content.ReadAsStringAsync());
            using var wantedRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/801");
            CollectionAssert.AreEqual(new[] { tag.Id }, (await wantedRead.Content.ReadFromJsonAsync<WantedDetailsV1>())!.TagIds);
            using var usedTag = await host.SendAsync(HttpMethod.Delete, Root + "tags/" + tag.Id + "?version=1");
            Assert.AreEqual(HttpStatusCode.BadRequest, usedTag.StatusCode);
            using var untagged = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/801", new UpdateWantedDataV1(false, 2, profile.Id, tagIds: []));
            Assert.AreEqual(HttpStatusCode.OK, untagged.StatusCode, await untagged.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkTags" WHERE "WorkId" = 801 """));
            await RefreshAsync();
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WantedBelowCutoff" WHERE "WantedItemId" = 801 """));
            await host.Fixture.RunSqlAsync("""UPDATE "StoredFiles" SET "ReleaseName" = 'Scored.Film.2020.PROPER.1080p.WEBRip.x264-TEAM' WHERE "Id" = 801 """);
            await RefreshAsync();
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WantedBelowCutoff" WHERE "WantedItemId" = 801 """));
            Assert.AreEqual(100, await host.ScalarAsync($"SELECT \"FormatScore\" FROM \"StoredFileProfileScores\" WHERE \"StoredFileId\" = 801 AND \"AcquisitionProfileId\" = {profile.Id}"));
            using var changed = await host.SendAsync(HttpMethod.Put, Root + "custom-formats", new SaveCustomFormatDataV1(1, MediaType.Movie, "Prefer Proper", false,
                new([new FormatConditionV1("Changed", FormatConditionKind.ReleaseTitle, false, true, "^Never matches$", false, null, null, null)]), 1));
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            await RefreshAsync();
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WantedBelowCutoff" WHERE "WantedItemId" = 801 """));

            async Task RefreshAsync()
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RefreshStoredFileFormatsV1>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
            }
        }

        private static async Task<Host> CreateAsync(bool razor = false)
        {
            var host = await Host.CreateAsync(library: true, razor: razor);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            return host;
        }
    }
}
