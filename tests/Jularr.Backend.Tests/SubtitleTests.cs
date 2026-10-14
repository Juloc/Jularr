using System.IO.Compression;
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
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.Subtitles;
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
            using var missing = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings", new UpdateInstanceSettingsDataV1("Jularr", 1, 1, subtitleLanguages: ["de", null!]));
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode, await missing.Content.ReadAsStringAsync());
            using var tooLong = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings", new UpdateInstanceSettingsDataV1("Jularr", 1, 1, subtitleLanguages: ["de", "deutsch-lang"]));
            Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode, await tooLong.Content.ReadAsStringAsync());
            using var languages = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings", new UpdateInstanceSettingsDataV1("Jularr", 1, 1, subtitleLanguages: ["ger, en"]));
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

            // A provider out of quota ends the run without setting the file aside for a day.
            provider.RateLimited = true;
            Assert.IsFalse((await fetch.ExecuteAsync<UpdateResult>(new SubtitleParametersV1(), default, CancellationToken.None)).Success);
            Assert.IsInstanceOfType<DBNull>(await host.ScalarAsync("SELECT \"SubtitlesCheckedAt\" FROM \"StoredFiles\" WHERE \"Id\" = " + fileId));
            provider.RateLimited = false;
            Assert.IsTrue((await fetch.ExecuteAsync<UpdateResult>(new SubtitleParametersV1(), default, CancellationToken.None)).Success);
            StringAssert.Contains(provider.Search!, "languages=de");
            StringAssert.Contains(provider.Search!, "imdb_id=133093");
            Assert.AreEqual("{\"file_id\":2,\"sub_format\":\"srt\"}", provider.Downloaded);
            Assert.AreEqual(FakeOpenSubtitles.Subtitle, File.ReadAllText(Path.Combine(folder, "Film.2001.1080p.WEB-DL-GRP.de.srt")));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%.de.srt' AND \"MediaAssetId\" IS NOT NULL"));
            Assert.IsFalse((await fetch.ExecuteAsync<UpdateResult>(new SubtitleParametersV1(), default, CancellationToken.None)).Success);
        }

        [TestMethod]
        public async Task SubDl_SeasonPack_GivesTheAskedEpisodeInItsLanguage()
        {
            var client = new SubDlClient(new HttpClient(new FakeSubDl()));
            var query = new SubtitleQuery(true, "tt0903747", null, 1, 2, ["pt"]);
            var offer = (await client.SearchAsync(new SubtitleAccess("https://api.subdl.com/api/v1", "key", null, null), query, CancellationToken.None)).Single();
            Assert.AreEqual("pt", offer.Language);
            Assert.AreEqual("two", Encoding.UTF8.GetString(await client.DownloadAsync(offer, query, CancellationToken.None)));

            // Codes as streams and files spell them.
            Assert.AreEqual("de", LanguageCodes.ToTwoLetter("ger"));
            Assert.AreEqual("pt", LanguageCodes.ToTwoLetter("pt-BR"));
            Assert.IsNull(LanguageCodes.ToTwoLetter("xx"));
        }

        private sealed class FakeSubDl : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.Host == "dl.subdl.com")
                {
                    var archive = new MemoryStream();
                    using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
                    {
                        foreach (var (name, text) in new[] { ("Show.S01E01.srt", "one"), ("Show.S01E02.srt", "two") })
                        {
                            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                            writer.Write(text);
                        }
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) });
                }

                StringAssert.Contains(request.RequestUri.Query, "season_number=1");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":true,\"subtitles\":[{\"release_name\":\"Show.S01\",\"language\":\"BR_PT\",\"url\":\"/subtitle/1-2.zip\",\"hi\":false}]}",
                        Encoding.UTF8, "application/json")
                });
            }
        }

        private sealed class FakeOpenSubtitles : HttpMessageHandler
        {
            public const string Subtitle = "1\n00:00:01,000 --> 00:00:02,000\nHallo\n";

            public string? Search { get; private set; }
            public bool RateLimited { get; set; }
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
                    if (RateLimited)
                    {
                        return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    }

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
