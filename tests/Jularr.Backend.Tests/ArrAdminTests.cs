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
                new ImportCustomFormatsDataV1(TakeoverSourceType.Radarr, json));
        }

        [TestMethod]
        public async Task Groups_RenameKeepsIdentity_AndUnknownFormatsAreRefused()
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
            using var unknown = await host.SendAsync(HttpMethod.Post, Root + "profiles",
                new SaveAcquisitionProfileDataV1("Unknown format", [AcquisitionMediaKind.Movie], [], ["WEBDL-1080p"], false, null, null, null,
                    formatScores: new([new CustomFormatScoreV1(999999, 500)])));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

            SaveAcquisitionProfileDataV1 Input(string groupName, long? version = null) => new("Grouped", [AcquisitionMediaKind.Series], [], ["group:123"], true, "group:123",
                null, null, version, qualityGroups: new([new QualityGroupV1(123, groupName, ["WEBDL-1080p", "WEBRip-1080p"])]));
            async Task<AcquisitionProfileDetailsV1> ReadAsync()
            {
                using var response = await host.SendAsync(HttpMethod.Get, Root + "profiles/" + key.Id);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                return (await response.Content.ReadFromJsonAsync<AcquisitionProfileDetailsV1>())!;
            }
        }

        [TestMethod]
        public async Task ProfilesPage_OneListWithItsTypesAndDefaults_OldAddressesLeadOn_AndNoAddressIsRefused()
        {
            await using var host = await CreateAsync(razor: true);
            const string Home = "/Admin/Acquisition/Profiles";

            // One list of every profile in three tabs, without a media-type bar; each row names the types it applies to and those it is the default of.
            using var listPage = await host.SendAsync(HttpMethod.Get, Home);
            var list = await listPage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, listPage.StatusCode, list);
            Assert.IsFalse(list.Contains("capsule-group", StringComparison.Ordinal), "No media-type bar.");
            foreach (var tab in new[] { ">Profiles</a>", ">Custom formats</a>", ">Release groups</a>", ">Release profiles</a>", ">Quality</a>" }) StringAssert.Contains(list, tab);
            Assert.AreEqual(1, list.Split(">HD - 720p/1080p<", StringSplitOptions.None).Length - 1, "The film and the series profile, alike, are one.");
            foreach (var type in new[] { "Movies", "Series", "Anime", "Books", "Manga", "Light novels", "Music", "Games" }) StringAssert.Contains(list, "class=\"tag tag--accent\">" + type + "<");

            // The addresses of the former panes and of the media-type bar lead to the tab that holds them now.
            foreach (var (from, to) in new[]
            {
                ("?tab=profiles&mediaTypeId=5", Home), ("?mediaTypeId=2&subtype=Anime", Home), ("?tab=sizes&mediaTypeId=1", Home + "?tab=quality"),
                ("?tab=release&mediaTypeId=1", Home + "?tab=releases"), ("?tab=groups&mediaTypeId=3", Home + "?tab=groups"), ("?tab=tags", Home + "?tab=releases#tags")
            })
            {
                using var moved = await host.SendAsync(HttpMethod.Get, Home + from);
                Assert.AreEqual(HttpStatusCode.Redirect, moved.StatusCode, from);
                Assert.AreEqual(to, moved.Headers.Location!.OriginalString, from);
            }

            // No value of the address is refused.
            foreach (var query in new[] { "?tab=unknown", "?tab=formats&edit=abc&item=x&new=nothing&pageNumber=-3&groups=zz", "?tab=groups&rating=7&kind=Banana&sort=1&groups=-2", "?tab=quality&pageNumber=99999999999", "?new=profile&edit=%00" })
            {
                using var odd = await host.SendAsync(HttpMethod.Get, Home + query);
                Assert.AreEqual(HttpStatusCode.OK, odd.StatusCode, query);
            }

            // A new profile starts with its media type; the editor offers the types of its quality list.
            var dialog = await (await host.SendAsync(HttpMethod.Get, Home + "?new=profile")).Content.ReadAsStringAsync();
            StringAssert.Contains(dialog, "id=\"profile-new\"");
            StringAssert.Contains(dialog, Home + "/New?kind=Anime");
            var editor = await (await host.SendAsync(HttpMethod.Get, Home + "/New?kind=Anime")).Content.ReadAsStringAsync();
            StringAssert.Contains(editor, "name=\"MediaKinds\" value=\"Anime\" checked=\"checked\"");
            StringAssert.Contains(editor, "name=\"MediaKinds\" value=\"Movie\"");
            Assert.IsFalse(editor.Contains("value=\"Book\"", StringComparison.Ordinal));
            Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, Home + "/New?kind=Banana")).StatusCode);

            // The HD profile of a new installation applies to films, series and anime and is the default of all three.
            var seriesId = (await (await host.SendAsync(HttpMethod.Get, Root + "profiles")).Content.ReadFromJsonAsync<BaseDataList<AcquisitionProfileDetailsV1>>())!
                .Single(item => item.DefaultFor.Contains(AcquisitionMediaKind.Series)).Id;
            editor = await (await host.SendAsync(HttpMethod.Get, Home + "/" + seriesId)).Content.ReadAsStringAsync();
            foreach (var kind in new[] { "Series", "Anime" })
            {
                StringAssert.Contains(editor, "name=\"MediaKinds\" value=\"" + kind + "\" checked=\"checked\"");
                StringAssert.Contains(editor, "name=\"DefaultFor\" value=\"" + kind + "\" checked=\"checked\"");
            }
        }

        [TestMethod]
        public async Task AdminTabs_RenderOneNavigation_AndNativeFormsPersistChanges()
        {
            await using var host = await CreateAsync(razor: true);
            using var overview = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles");
            Assert.AreEqual(HttpStatusCode.OK, overview.StatusCode, string.Join(";", overview.Headers.Select(header => header.Key + "=" + string.Join(",", header.Value))));
            foreach (var tab in new[] { "profiles", "formats", "groups", "releases", "quality" })
            {
                using var response = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles?tab=" + tab);
                var html = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, html);
                Assert.AreEqual(tab == "groups" ? 3 : 2, html.Split("class=\"navigation-tabs navigation-tabs--filters\"", StringSplitOptions.None).Length, tab);
                Assert.IsFalse(html.Contains("delay", StringComparison.OrdinalIgnoreCase), "A delay is offered on " + tab);
                Assert.IsFalse(html.Contains("FallbackQualities", StringComparison.Ordinal));
                if (tab == "releases")
                {
                    using var saved = await host.SendAsync(HttpMethod.Post, "/Admin/Acquisition/Profiles?tab=releases&handler=Tag",
                        new FormUrlEncodedContent([new("__RequestVerificationToken", LibraryTests.Token(html)), new("Label", "arr-test")]));
                    Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
                }
            }
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Tags" WHERE "Label" = 'arr-test' """));
        }

        [TestMethod]
        public async Task ReleaseGroupsTab_ListsEveryTypeNotRatedFirst_FiltersLive_AndSavesARatingFromItsSegments()
        {
            await using var host = await CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ReleaseGroups" ("Id", "Name", "ReleaseGroupRatingId", "SeenCount", "LastSeenAt") VALUES
                    (901, 'GRP', 1, 14, now() - interval '2 hours'),
                    (902, 'FLUX', NULL, 5, now() - interval '3 days'),
                    (903, 'SeriesOnly', NULL, 1, now());
                INSERT INTO "ReleaseGroupMediaKinds" ("ReleaseGroupId", "AcquisitionMediaKindId", "SeenCount", "LastSeenAt") VALUES
                    (901, 1, 10, now()), (901, 3, 4, now()), (903, 2, 1, now());
                """);

            // A tab of its own: below the tabs the rating filter, a live search, the media type and the order; not rated first, one segmented control per group.
            const string Tab = "/Admin/Acquisition/Profiles?tab=groups";
            using var page = await host.SendAsync(HttpMethod.Get, Tab);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            Assert.AreEqual(3, html.Split("class=\"navigation-tabs navigation-tabs--filters\"", StringSplitOptions.None).Length, "The tabs, then the filters with the search.");
            StringAssert.Contains(html, "navigation-tabs__link navigation-tabs__link--active\" aria-current=\"page\">Release groups</a>");
            StringAssert.Contains(html, "id=\"release-groups\" data-region=\"\"");
            StringAssert.Contains(html, "data-search-live=\"\"");
            StringAssert.Contains(html, "placeholder=\"Find a group\"");
            StringAssert.Contains(html, "href=\"/Admin/Acquisition/Profiles?tab=groups&amp;rating=Blocked\"");
            StringAssert.Contains(html, "name=\"kind\"");
            StringAssert.Contains(html, "name=\"sort\"");
            StringAssert.Contains(html, "seen 5 times, last 3 days ago");
            StringAssert.Contains(html, "Movies, Anime &#xB7; seen 14 times, last 2 hours ago");
            Assert.IsTrue(html.IndexOf(">FLUX<", StringComparison.Ordinal) < html.IndexOf(">GRP<", StringComparison.Ordinal), "Not rated groups come first.");
            Assert.AreEqual(3, html.Split("class=\"ui-segments\"", StringSplitOptions.None).Length - 1);
            StringAssert.Contains(html, "data-form-submit=\"\"");
            Assert.AreEqual(9, html.Split("data-form-clearable=\"\"", StringSplitOptions.None).Length - 1);
            Assert.IsFalse(html.Contains("Add &#x201C;", StringComparison.Ordinal), "Without words nothing is offered to add.");

            // The filter, the media type and the order narrow the list and stay in every address of the tab.
            var anime = await (await host.SendAsync(HttpMethod.Get, Tab + "&kind=Anime&sort=Name")).Content.ReadAsStringAsync();
            StringAssert.Contains(anime, ">GRP<");
            Assert.IsFalse(anime.Contains(">FLUX<", StringComparison.Ordinal) || anime.Contains(">SeriesOnly<", StringComparison.Ordinal));
            StringAssert.Contains(anime, "href=\"/Admin/Acquisition/Profiles?tab=groups&amp;rating=Good&amp;kind=Anime&amp;sort=Name\"");
            var open = await (await host.SendAsync(HttpMethod.Get, Tab + "&rating=NotRated")).Content.ReadAsStringAsync();
            Assert.IsFalse(open.Contains(">GRP<", StringComparison.Ordinal));
            StringAssert.Contains(open, ">SeriesOnly<");

            // A segment saves the rating at once and the tab shows again.
            using var rated = await host.SendAsync(HttpMethod.Post, Tab + "&edit=902&item=group&handler=Rate",
                new FormUrlEncodedContent([new("__RequestVerificationToken", LibraryTests.Token(html)), new("ReleaseGroupRatingId", "3")]));
            Assert.AreEqual(HttpStatusCode.Redirect, rated.StatusCode, await rated.Content.ReadAsStringAsync());
            Assert.AreEqual("/Admin/Acquisition/Profiles?tab=groups", rated.Headers.Location!.OriginalString);
            Assert.AreEqual(3, Convert.ToInt32(await host.ScalarAsync("""SELECT "ReleaseGroupRatingId" FROM "ReleaseGroups" WHERE "Id" = 902""")));

            // The chosen segment clicked again sends no rating: the group is not rated again.
            using var cleared = await host.SendAsync(HttpMethod.Post, Tab + "&edit=901&item=group&handler=Rate",
                new FormUrlEncodedContent([new("__RequestVerificationToken", LibraryTests.Token(html))]));
            Assert.AreEqual(HttpStatusCode.Redirect, cleared.StatusCode, await cleared.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "ReleaseGroupRatingId" IS NULL FROM "ReleaseGroups" WHERE "Id" = 901"""));
            var sorted = await (await host.SendAsync(HttpMethod.Get, Tab)).Content.ReadAsStringAsync();
            Assert.IsTrue(sorted.IndexOf(">GRP<", StringComparison.Ordinal) < sorted.IndexOf(">FLUX<", StringComparison.Ordinal), "The group no longer rated comes first again.");

            // Words that name no group offer to add it; the added group shows with the words.
            var unknown = await (await host.SendAsync(HttpMethod.Get, Tab + "&q=danke-Empire")).Content.ReadAsStringAsync();
            StringAssert.Contains(unknown, "Add &#x201C;danke-Empire&#x201D; as a group");
            StringAssert.Contains(unknown, "No group matches.");
            using var added = await host.SendAsync(HttpMethod.Post, Tab + "&q=danke-Empire&handler=Group",
                new FormUrlEncodedContent([new("__RequestVerificationToken", LibraryTests.Token(html)), new("Name", " danke-Empire ")]));
            Assert.AreEqual(HttpStatusCode.Redirect, added.StatusCode, await added.Content.ReadAsStringAsync());
            StringAssert.Contains(added.Headers.Location!.OriginalString, "tab=groups");
            StringAssert.Contains(added.Headers.Location!.OriginalString, "q=danke-Empire");
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "ReleaseGroups" WHERE "Name" = 'danke-Empire' AND "SeenCount" = 0"""));
            var found = await (await host.SendAsync(HttpMethod.Get, added.Headers.Location.OriginalString)).Content.ReadAsStringAsync();
            StringAssert.Contains(found, "added by hand, not seen yet");
            Assert.IsFalse(found.Contains(">GRP<", StringComparison.Ordinal));
            Assert.IsFalse(found.Contains("Add &#x201C;", StringComparison.Ordinal), "A group that exists is not offered again.");
        }

        [TestMethod]
        public async Task ProfileEditor_ScoresEveryFormat_AndKeepsOnlyScoresThatCount()
        {
            await using var host = await CreateAsync(razor: true);
            using var imported = await host.SendAsync(HttpMethod.Post, Root + "custom-formats/import", new ImportCustomFormatsDataV1(TakeoverSourceType.Radarr,
                """{"name":"Matrix format","specifications":[{"name":"Title","implementation":"ReleaseTitleSpecification","fields":{"value":"HDR"}}]}"""));
            var format = (await imported.Content.ReadFromJsonAsync<BaseDataList<SavedCustomFormatV1>>())!.Single();
            var profileId = (long)(await host.ScalarAsync("""SELECT "AcquisitionProfileId" FROM "AcquisitionProfileDefaults" WHERE "AcquisitionMediaKindId" = 1"""))!;
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"AcquisitionProfiles\" WHERE \"Id\" = " + profileId))!;

            // The editor lists every format with its score, not only those already scored.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles/" + profileId);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Matrix format");
            StringAssert.Contains(html, "name=\"FormatScores[0].Score\"");
            Assert.IsFalse(html.Contains("addFormatId", StringComparison.Ordinal));

            // A score is saved with the profile; a format left at zero is not stored.
            using var saved = await host.SendAsync(HttpMethod.Post, "/Admin/Acquisition/Profiles/" + profileId, new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", LibraryTests.Token(html)), new("Version", version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("MediaKinds", "Movie"), new("MediaKinds", "Series"), new("MediaKinds", "Anime"), new("DefaultFor", "Movie"), new("DefaultFor", "Series"), new("DefaultFor", "Anime"),
                new("Name", "Matrix"), new("AllowedQualities", "WEBDL-1080p"), new("MinimumFormatScore", "10"),
                new("FormatScores[0].FormatId", format.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("FormatScores[0].Score", "75"),
                new("FormatScores[1].FormatId", "999999"), new("FormatScores[1].Score", "0")
            ]));
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var stored = await host.SendAsync(HttpMethod.Get, Root + "profiles/" + profileId);
            var scores = (await stored.Content.ReadFromJsonAsync<AcquisitionProfileDetailsV1>())!.FormatScores;
            Assert.AreEqual(1, scores.Count);
            Assert.AreEqual((format.Id, 75), (scores[0].FormatId, scores[0].Score));

            // The format list shows it as used and its editor is a dialog; no page offers a waiting time.
            using var formats = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles?tab=formats&item=format&edit=" + format.Id);
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
                new SaveAcquisitionProfileDataV1("Scored group", [AcquisitionMediaKind.Movie], [], ["group:55"], true, "group:55", null, null,
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
            using var changed = await host.SendAsync(HttpMethod.Put, Root + "custom-formats", new SaveCustomFormatDataV1(1, "Prefer Proper", false,
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
