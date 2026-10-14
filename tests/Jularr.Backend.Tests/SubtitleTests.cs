using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Integrations.Logic.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SubtitleTests
    {
        [TestMethod]
        public async Task Subtitles_MissingLanguage_IsFetchedBesideTheVideoOnce()
        {
            var provider = new FakeOpenSubtitles();
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services => services.AddHttpClient<OpenSubtitlesClient>().ConfigurePrimaryHttpMessageHandler(() => provider));
            var mountId = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Film (2001)");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Film.2001.1080p.WEB-DL-GRP.mkv"), "video");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment", new AssignLibraryFileDataV1(null, "Film", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());

            // English is embedded; German is wanted too.
            await host.Fixture.RunSqlAsync($"""
                UPDATE "StoredFiles" SET "ProbedAt" = now() WHERE "Id" = {fileId};
                INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Codec", "Language", "IsDefault", "IsForced") VALUES ({fileId}, 2, 3, 'subrip', 'eng', FALSE, FALSE);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT "Id", 'imdb', 'tt0133093' FROM "Works";
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'Subtitles';
                """);
            using var unknown = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings", new UpdateInstanceSettingsDataV1("Jularr", 1, 1, subtitleLanguages: ["xx"]));
            StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "unknown_language");
            using var languages = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings", new UpdateInstanceSettingsDataV1("Jularr", 1, 1, subtitleLanguages: ["ger", "en"]));
            Assert.AreEqual(HttpStatusCode.OK, languages.StatusCode, await languages.Content.ReadAsStringAsync());

            // A provider is a connection like an indexer; its test logs in.
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/connections", new SaveAcquisitionConnectionDataV1("OpenSubtitles",
                AcquisitionConnectionType.OpenSubtitles, "https://api.opensubtitles.com/api/v1", "consumer-key", true, 25, [], null, null, null, "viewer", null, "account-secret"));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var connectionId = (await created.Content.ReadFromJsonAsync<CreateAcquisitionConnectionResultV1>())!.Id;
            using var tested = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/connections/" + connectionId + "/test");
            var check = (await tested.Content.ReadFromJsonAsync<AcquisitionConnectionTestV1>())!;
            Assert.IsTrue(check.IsAvailable, check.ErrorKey);

            // Only German is fetched, the matching release preferred, and saved beside the video for its asset.
            await using var scope = host.Services.CreateAsyncScope();
            var fetch = scope.ServiceProvider.GetRequiredService<FetchSubtitlesV1>();
            Assert.IsTrue((await fetch.ExecuteAsync<UpdateResult>(new SubtitleParametersV1(), default, CancellationToken.None)).Success);
            StringAssert.Contains(provider.Search!, "languages=de");
            StringAssert.Contains(provider.Search!, "imdb_id=133093");
            Assert.AreEqual("{\"file_id\":2}", provider.Downloaded);
            Assert.AreEqual(FakeOpenSubtitles.Subtitle, File.ReadAllText(Path.Combine(folder, "Film.2001.1080p.WEB-DL-GRP.de.srt")));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%.de.srt' AND \"MediaAssetId\" IS NOT NULL"));
            Assert.IsFalse((await fetch.ExecuteAsync<UpdateResult>(new SubtitleParametersV1(), default, CancellationToken.None)).Success);
        }

        private sealed class FakeOpenSubtitles : HttpMessageHandler
        {
            public const string Subtitle = "1\n00:00:01,000 --> 00:00:02,000\nHallo\n";

            public string? Search { get; private set; }
            public string? Downloaded { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.RequestUri.Host == "files.opensubtitles.test")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Subtitle) };
                }

                if (!request.Headers.TryGetValues("Api-Key", out var keys) || keys.Single() != "consumer-key")
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                if (path.EndsWith("/login", StringComparison.Ordinal))
                {
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    return body.Contains("account-secret", StringComparison.Ordinal) ? Json("{\"token\":\"session\"}") : new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                if (path.EndsWith("/subtitles", StringComparison.Ordinal))
                {
                    Search = request.RequestUri.Query;
                    return Json("""
                        {"data":[
                          {"attributes":{"language":"de","download_count":900,"hearing_impaired":false,"release":"Film.2001.720p.BluRay-OTHER","files":[{"file_id":1}]}},
                          {"attributes":{"language":"de","download_count":10,"hearing_impaired":false,"release":"Film.2001.1080p.WEB-DL-GRP","files":[{"file_id":2}]}}
                        ]}
                        """);
                }

                if (path.EndsWith("/download", StringComparison.Ordinal) && request.Headers.Authorization?.Parameter == "session")
                {
                    Downloaded = await request.Content!.ReadAsStringAsync(cancellationToken);
                    return Json("{\"link\":\"https://files.opensubtitles.test/file.srt\"}");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
