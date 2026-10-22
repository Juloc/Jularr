using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Speech.V1;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Logic.Speech;
using Jularr.Integrations.Logic.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Jularr.Backend.Service.Core;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SpeechDiscoveryTests
    {
        private const string Key = "de_DE-fixture-medium";
        private const string FileName = "vits-piper-" + Key + ".tar.bz2";
        private const string Source = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/" + FileName;
        private static readonly byte[] Package = Encoding.UTF8.GetBytes("synthetic speech discovery fixture; not an inference model");
        private static string Hash => Convert.ToHexStringLower(SHA256.HashData(Package));

        private static string Release() => JsonSerializer.Serialize(new { assets = new[] { new { name = FileName, size = Package.Length, digest = "sha256:" + Hash, browser_download_url = Source } } });
        private static string Voices() => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [Key] = new { key = Key, name = "fixture", quality = "medium", language = new { code = "de_DE", family = "de" }, num_speakers = 1,
                files = new Dictionary<string, object> { ["de/de_DE/fixture/medium/MODEL_CARD"] = new { } } }
        });

        [TestMethod]
        public async Task Source_ExplicitOfficialMetadataSmoke()
        {
            if (Environment.GetEnvironmentVariable("JULARR_SPEECH_LIVE_METADATA") != "1") Assert.Inconclusive("Explicit external metadata smoke only.");
            using var http = new HttpClient();
            var packages = await new SpeechCatalogClient(http).ReadAsync(default);
            Assert.IsTrue(packages.Count > 3);
            Assert.IsTrue(packages.Any(package => package.Key == "de_DE-eva_k-x_low"));
            Console.WriteLine("Official compatible packages: " + packages.Count);
            using var models = new TemporaryDirectory();
            var files = new SpeechFiles(Options.Create(new MediaOptions { SpeechPath = models.Path }), new SpeechModelClient(http), new SpeechCatalogClient(http));
            var discovered = await files.DiscoverAsync(default);
            var parsed = SpeechCatalog.Parse(JsonSerializer.Serialize(new { models = discovered }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.AreEqual(discovered.Count, parsed.Count, "Excluded metadata: " + string.Join(", ", discovered.Where(model => !parsed.Any(item => item.Id == model.Id)).Select(model => model.Id)));
            await files.SaveDiscoveredAsync(discovered, default);
            Assert.IsTrue((await files.CatalogAsync(default)).Count > 3);
        }

        [TestMethod]
        public void Catalog_GenderRequiresKnownClassificationAndSource()
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(new { models = new[] { new { id = "fixture", version = "1", name = "Fixture", language = "de-DE", voice = "0",
                license = "Fixture", licenseUrl = "https://example.test/license", source = Source, sizeBytes = Package.Length, sha256 = Hash } } }))!;
            var model = node["models"]![0]!;
            Assert.AreEqual(Jularr.Backend.Data.Enums.Speech.SpeechVoiceGenderType.Unknown, SpeechCatalog.Parse(node.ToJsonString()).Single().Gender);
            model["gender"] = 2;
            Assert.AreEqual(0, SpeechCatalog.Parse(node.ToJsonString()).Count, "No inferred classification without provenance.");
            model["genderSource"] = "https://example.test/synthetic-gender";
            Assert.AreEqual(Jularr.Backend.Data.Enums.Speech.SpeechVoiceGenderType.Female, SpeechCatalog.Parse(node.ToJsonString()).Single().Gender);
            model["gender"] = 42;
            Assert.AreEqual(0, SpeechCatalog.Parse(node.ToJsonString()).Count);
        }

        [TestMethod]
        public void Source_OnlyHashPinnedCompatiblePackagesWithModelCardsAreAccepted()
        {
            var package = SpeechCatalogClient.Parse(Release(), Voices()).Single();
            Assert.AreEqual((Key, "de-DE", Hash, Package.Length), (package.Key, package.Language, package.Sha256, (int)package.SizeBytes));
            StringAssert.Contains(package.ModelCard, "/de/de_DE/fixture/medium/MODEL_CARD");
            StringAssert.Contains(package.SampleUrl, "/de_DE/vits-piper-" + Key + "/0.mp3");
            var release = JsonNode.Parse(Release())!;
            ((JsonArray)release["assets"]!).Add(release["assets"]![0]!.DeepClone());
            Assert.AreEqual(1, SpeechCatalogClient.Parse(release.ToJsonString(), Voices()).Count);
        }

        [TestMethod]
        [DataRow("digest", "md5:1234")]
        [DataRow("digest", "sha256:bad")]
        [DataRow("browser_download_url", "https://untrusted.example.test/model.tar.bz2")]
        [DataRow("name", "supertonic-3-de.tar.bz2")]
        [DataRow("name", "vits-piper-../../model.tar.bz2")]
        public void Source_UnverifiedOrIncompatiblePackagesDoNotBecomeInstallOptions(string field, string value)
        {
            var release = JsonNode.Parse(Release())!;
            release["assets"]![0]![field] = value;
            Assert.ThrowsExactly<InvalidDataException>(() => SpeechCatalogClient.Parse(release.ToJsonString(), Voices()));
        }

        [TestMethod]
        public void Source_OversizedPackagesAndMissingModelCardsAreExcluded()
        {
            var release = JsonNode.Parse(Release())!;
            release["assets"]![0]!["size"] = 134_217_729;
            Assert.ThrowsExactly<InvalidDataException>(() => SpeechCatalogClient.Parse(release.ToJsonString(), Voices()));
            var voices = JsonNode.Parse(Voices())!;
            voices[Key]!["files"] = new JsonObject();
            Assert.ThrowsExactly<InvalidDataException>(() => SpeechCatalogClient.Parse(Release(), voices.ToJsonString()));
        }

        [TestMethod]
        public async Task Discovery_AllUnsupportedLocalesFailClosedDuringTheCooldown()
        {
            using var models = new TemporaryDirectory();
            using var source = new CatalogSource { UnsupportedLocale = true };
            using var http = new HttpClient(source);
            var files = new SpeechFiles(Options.Create(new MediaOptions { SpeechPath = models.Path }), new SpeechModelClient(http), new SpeechCatalogClient(http));
            await Assert.ThrowsExactlyAsync<Jularr.Backend.Data.Common.ServiceUnavailableException>(() => files.DiscoverAsync(default));
            await Assert.ThrowsExactlyAsync<Jularr.Backend.Data.Common.ServiceUnavailableException>(() => files.DiscoverAsync(default));
            Assert.AreEqual(2, source.MetadataRequests);
            Assert.AreEqual(0, source.Downloads);
            Assert.AreEqual(0, Directory.GetFiles(models.Path).Length);
        }

        [TestMethod]
        public async Task Discovery_ExplicitRefreshSearchConfirmationAndDeviceDeliveryUseOneCatalog()
        {
            using var storage = new TemporaryDirectory();
            using var models = new TemporaryDirectory();
            using var source = new CatalogSource();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["Media:SpeechPath"] = models.Path }, services: services =>
                {
                    services.AddHttpClient<SpeechCatalogClient>().ConfigurePrimaryHttpMessageHandler(() => source);
                    services.AddHttpClient<SpeechModelClient>().ConfigurePrimaryHttpMessageHandler(() => source);
                });

            using var initial = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Speech");
            Assert.AreEqual(HttpStatusCode.OK, initial.StatusCode, await initial.Content.ReadAsStringAsync());
            Assert.AreEqual((0, 0), (source.MetadataRequests, source.Downloads));
            using var refresh = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/speech/models/refresh", new { });
            Assert.AreEqual(HttpStatusCode.OK, refresh.StatusCode, await refresh.Content.ReadAsStringAsync());
            Assert.AreEqual((2, 0), (source.MetadataRequests, source.Downloads));
            var persisted = await File.ReadAllTextAsync(Path.Combine(models.Path, "discovered.json"));
            Assert.AreEqual(4, SpeechCatalog.Parse(persisted).Count);
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/speech/models/refresh", new { });
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
            Assert.AreEqual(2, source.MetadataRequests, "Repeated clicks share a bounded metadata refresh.");

            using var search = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/speech/models?query=fixture");
            var found = (await search.Content.ReadFromJsonAsync<SpeechModelCatalogV1>())!.Models.Items.Single();
            Assert.AreEqual((Hash, true, "0"), (found.Sha256, found.RequiresLicenseAcceptance, found.Voice));
            using var secondPage = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/speech/models?query=fixture&page=2&pageSize=1");
            Assert.AreEqual(0, (await secondPage.Content.ReadFromJsonAsync<SpeechModelCatalogV1>())!.Models.Items.Count);
            using var details = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Speech?q=fixture&modelHash=" + Hash);
            var markup = await details.Content.ReadAsStringAsync();
            StringAssert.Contains(markup, "name=\"AcceptLicense\"");
            StringAssert.Contains(markup, found.SampleUrl!);

            var activation = "/api/v1/admin/speech/models/" + Hash + "/activation";
            using var unconfirmed = await host.SendAsync(HttpMethod.Post, activation, new UpdateSpeechModelDataV1(true, 0));
            Assert.AreEqual(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
            Assert.AreEqual(0, source.Downloads);
            using var confirmed = await host.SendAsync(HttpMethod.Post, activation, new UpdateSpeechModelDataV1(true, 0, true));
            Assert.AreEqual(HttpStatusCode.OK, confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
            Assert.AreEqual(1, source.Downloads);
            using var installed = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/speech/models?query=fixture&language=de-DE&gender=0&isInstalled=true&pageSize=1");
            var filtered = (await installed.Content.ReadFromJsonAsync<SpeechModelCatalogV1>())!;
            Assert.AreEqual(Hash, filtered.Models.Items.Single().Sha256);
            Assert.IsTrue(filtered.Models.Items.Single().IsInstalled);
            CollectionAssert.AreEquivalent(new[] { "de-DE", "id-ID", "en-US" }, filtered.Languages.ToArray());
            using var notInstalled = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/speech/models?query=fixture&isInstalled=false");
            Assert.AreEqual(0, (await notInstalled.Content.ReadFromJsonAsync<SpeechModelCatalogV1>())!.Models.Items.Count);
            using var male = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/speech/models?gender=1");
            Assert.AreEqual("piper-de-thorsten", (await male.Content.ReadFromJsonAsync<SpeechModelCatalogV1>())!.Models.Items.Single().Id);
            using var female = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/speech/models?gender=2");
            Assert.AreEqual("piper-en-ljspeech", (await female.Content.ReadFromJsonAsync<SpeechModelCatalogV1>())!.Models.Items.Single().Id);
            using var invalidGender = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/speech/models?gender=42");
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidGender.StatusCode);
            using var available = await host.SendAsync(HttpMethod.Get, "/api/v1/speech/models");
            var delivered = (await available.Content.ReadFromJsonAsync<Jularr.Backend.Data.Common.BaseDataList<SpeechModelV1>>())!.Single();
            Assert.AreEqual(found.Id, delivered.Id);
            using var bytes = await host.SendAsync(HttpMethod.Get, delivered.DownloadUrl);
            CollectionAssert.AreEqual(Package, await bytes.Content.ReadAsByteArrayAsync());
            using var deactivate = await host.SendAsync(HttpMethod.Post, activation, new UpdateSpeechModelDataV1(false, delivered.Revision));
            Assert.AreEqual(HttpStatusCode.OK, deactivate.StatusCode);
            using var retained = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/speech/models?isInstalled=true");
            var retainedModel = (await retained.Content.ReadFromJsonAsync<SpeechModelCatalogV1>())!.Models.Items.Single();
            Assert.IsTrue(retainedModel.IsInstalled);
            Assert.IsFalse(retainedModel.IsEnabled, "Deactivation retains the installed archive.");

            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Reading'");
            using var disabled = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/speech/models/refresh", new { });
            Assert.AreEqual(HttpStatusCode.Forbidden, disabled.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var forbidden = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/speech/models/refresh", new { });
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.AreEqual(2, source.MetadataRequests);
        }

        [TestMethod]
        public async Task Discovery_SourceFailurePreservesSnapshotAndDoesNotBlockLocalReads()
        {
            using var storage = new TemporaryDirectory();
            using var models = new TemporaryDirectory();
            var snapshot = JsonSerializer.Serialize(new { models = new[] { new { id = "saved", version = "1", name = "Saved voice", language = "de-DE", voice = "0",
                license = "Review model card", licenseUrl = "https://example.test/license", source = Source, sizeBytes = Package.Length, sha256 = Hash, requiresLicenseAcceptance = true } } });
            await File.WriteAllTextAsync(Path.Combine(models.Path, "discovered.json"), snapshot);
            using var source = new CatalogSource { Fail = true };
            await using var host = await LibraryTests.CreateHostAsync(storage.Path,
                settings: new Dictionary<string, string> { ["Media:SpeechPath"] = models.Path },
                services: services => services.AddHttpClient<SpeechCatalogClient>().ConfigurePrimaryHttpMessageHandler(() => source));
            using var refresh = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/speech/models/refresh", new { });
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refresh.StatusCode);
            Assert.AreEqual(snapshot, await File.ReadAllTextAsync(Path.Combine(models.Path, "discovered.json")));
            using var search = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/speech/models?query=Saved");
            Assert.AreEqual("saved", (await search.Content.ReadFromJsonAsync<SpeechModelCatalogV1>())!.Models.Items.Single().Id);
            Assert.AreEqual(0, source.Downloads);
        }

        private sealed class CatalogSource : HttpMessageHandler
        {
            internal bool Fail { get; init; }
            internal bool UnsupportedLocale { get; init; }
            internal int MetadataRequests { get; private set; }
            internal int Downloads { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var address = request.RequestUri!.AbsoluteUri;
                if (address == Source)
                {
                    Downloads++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) });
                }

                MetadataRequests++;
                Assert.IsTrue(address is SpeechCatalogClient.Releases or SpeechCatalogClient.Voices);
                var metadata = address == SpeechCatalogClient.Releases ? Release() : Voices();
                if (UnsupportedLocale) metadata = metadata.Replace("de_DE", "zz_ZZ", StringComparison.Ordinal).Replace("\"family\":\"de\"", "\"family\":\"zz\"", StringComparison.Ordinal);
                return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) { Content = new StringContent(metadata) });
            }
        }
    }
}
