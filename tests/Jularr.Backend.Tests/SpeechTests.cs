using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jularr.Backend.Data.Admin.Speech.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Speech;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Logic.Speech;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Speech;
using Microsoft.Extensions.DependencyInjection;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SpeechTests
    {
        private static readonly byte[] Package = Encoding.UTF8.GetBytes("verified-local-speech-package");
        private static string Hash => Convert.ToHexStringLower(SHA256.HashData(Package));

        [TestMethod]
        public void Recommendations_HaveOrderedLanguagesAndVerifiedMetadata()
        {
            using var reader = new StreamReader(typeof(ServiceApi).Assembly.GetManifestResourceStream("Jularr.Speech.models.json")!);
            var catalog = SpeechCatalog.Parse(reader.ReadToEnd());
            CollectionAssert.AreEqual(new[] { "de-DE", "id-ID", "en-US" }, catalog.Select(model => model.Language).ToArray());
            Assert.IsTrue(catalog.All(model => model.SizeBytes > 0 && SpeechCatalog.Hash().IsMatch(model.Sha256) && model.License.Length > 0));
            StringAssert.Contains(catalog[1].License, "NC");
        }

        [TestMethod]
        [DataRow("license", "")]
        [DataRow("sha256", "not-a-hash")]
        [DataRow("source", "http://models.example.test/file")]
        [DataRow("source", "https://user:password@models.example.test/file")]
        [DataRow("language", "not-a-language")]
        [DataRow("version", "../model")]
        public void Catalog_InvalidEntry_DoesNotBecomeAnInstallOption(string field, string value)
        {
            var document = JsonNode.Parse(Catalog())!;
            document["models"]![0]![field] = value;
            Assert.AreEqual(0, SpeechCatalog.Parse(document.ToJsonString()).Count);
        }

        [TestMethod]
        public void Catalog_DuplicatesAndInvalidSize_AreExcluded()
        {
            var document = JsonNode.Parse(Catalog())!;
            var models = (JsonArray)document["models"]!;
            models.Add(models[0]!.DeepClone());
            Assert.AreEqual(1, SpeechCatalog.Parse(document.ToJsonString()).Count);
            models[0]!["sizeBytes"] = 0;
            models[1]!["sha256"] = "bad";
            Assert.AreEqual(0, SpeechCatalog.Parse(document.ToJsonString()).Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Download_ChecksHashAndNeverPublishesAPartialPackage(bool tampered)
        {
            using var folder = new TemporaryDirectory();
            using var source = new ModelSource { Tampered = tampered };
            var client = new SpeechModelClient(new HttpClient(source));
            var destination = Path.Combine(folder.Path, "package.tar.bz2");
            if (tampered)
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.DownloadAsync(new Uri("https://models.example.test/package"), Package.Length, Hash, destination, default));
                Assert.IsFalse(File.Exists(destination));
            }
            else
            {
                await client.DownloadAsync(new Uri("https://models.example.test/package"), Package.Length, Hash, destination, default);
                CollectionAssert.AreEqual(Package, await File.ReadAllBytesAsync(destination));
            }

            Assert.AreEqual(tampered ? 0 : 1, Directory.GetFiles(folder.Path).Length);
        }

        [TestMethod]
        public async Task Models_RequireExplicitOwnerActivationAndRetainTheFileWhenDisabled()
        {
            using var storage = new TemporaryDirectory();
            using var models = new TemporaryDirectory();
            await File.WriteAllTextAsync(Path.Combine(models.Path, "catalog.json"), Catalog());
            using var source = new ModelSource();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["Media:SpeechPath"] = models.Path },
                services: services => services.AddHttpClient<SpeechModelClient>().ConfigurePrimaryHttpMessageHandler(() => source));

            using var administration = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Modules");
            StringAssert.Contains(await administration.Content.ReadAsStringAsync(), "href=\"/Admin/Instance/Speech\"");
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Speech");
            var markup = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, markup);
            StringAssert.Contains(markup, "Available to devices");
            StringAssert.Contains(markup, "name=\"IsEnabled\"");
            Assert.IsFalse(markup.Contains("Download and activate", StringComparison.Ordinal), "Activation is a switch in the row, not a button.");
            StringAssert.Contains(markup, "CC-BY-NC-SA-4.0");
            StringAssert.Contains(markup, Hash);
            Assert.AreEqual(0, source.Downloads);

            using var before = await host.SendAsync(HttpMethod.Get, "/api/v1/speech/models");
            Assert.AreEqual(0, (await before.Content.ReadFromJsonAsync<BaseDataList<SpeechModelV1>>())!.Count);
            var route = "/api/v1/admin/speech/models/" + Hash + "/activation";
            using var activated = await host.SendAsync(HttpMethod.Post, route, new UpdateSpeechModelDataV1(true, 0));
            Assert.AreEqual(HttpStatusCode.OK, activated.StatusCode, await activated.Content.ReadAsStringAsync());
            Assert.AreEqual(1, source.Downloads);
            using var available = await host.SendAsync(HttpMethod.Get, "/api/v1/speech/models");
            var model = (await available.Content.ReadFromJsonAsync<BaseDataList<SpeechModelV1>>())!.Single();
            Assert.IsTrue(model.IsEnabled);
            using var downloaded = await host.SendAsync(HttpMethod.Get, model.DownloadUrl);
            Assert.AreEqual(HttpStatusCode.OK, downloaded.StatusCode, await downloaded.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(Package, await downloaded.Content.ReadAsByteArrayAsync());
            StringAssert.Contains(downloaded.Headers.CacheControl!.ToString(), "immutable");

            using var stale = await host.SendAsync(HttpMethod.Post, route, new UpdateSpeechModelDataV1(false, 0));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var disabled = await host.SendAsync(HttpMethod.Post, route, new UpdateSpeechModelDataV1(false, 1));
            Assert.AreEqual(HttpStatusCode.OK, disabled.StatusCode);
            using var unavailable = await host.SendAsync(HttpMethod.Get, model.DownloadUrl);
            Assert.AreEqual(HttpStatusCode.NotFound, unavailable.StatusCode);
            Assert.IsTrue(File.Exists(Path.Combine(models.Path, Hash + ".tar.bz2")));

            // Cached bytes are verified again when the Owner reactivates; no further download is needed.
            using var enabled = await host.SendAsync(HttpMethod.Post, route, new UpdateSpeechModelDataV1(true, 2));
            Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode);
            Assert.AreEqual(1, source.Downloads);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var forbidden = await host.SendAsync(HttpMethod.Post, route, new UpdateSpeechModelDataV1(false, 3));
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.AreEqual(1, source.Downloads);
        }

        [TestMethod]
        public async Task Models_FailedVerificationAndDisabledReading_DoNotActivatePackages()
        {
            using var storage = new TemporaryDirectory();
            using var models = new TemporaryDirectory();
            await File.WriteAllTextAsync(Path.Combine(models.Path, "catalog.json"), Catalog());
            using var source = new ModelSource { Tampered = true };
            await using var host = await LibraryTests.CreateHostAsync(storage.Path,
                settings: new Dictionary<string, string> { ["Media:SpeechPath"] = models.Path },
                services: services => services.AddHttpClient<SpeechModelClient>().ConfigurePrimaryHttpMessageHandler(() => source));
            var route = "/api/v1/admin/speech/models/" + Hash + "/activation";
            using var tampered = await host.SendAsync(HttpMethod.Post, route, new UpdateSpeechModelDataV1(true, 0));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, tampered.StatusCode, await tampered.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"SpeechModels\""));
            Assert.AreEqual(1, Directory.GetFiles(models.Path).Length);

            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Reading'");
            using var off = await host.SendAsync(HttpMethod.Post, route, new UpdateSpeechModelDataV1(true, 0));
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            Assert.AreEqual(1, source.Downloads);
        }

        [TestMethod]
        public async Task Preferences_AreProfileScopedPreserveOtherLanguagesAndRejectStaleSaves()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            const string route = "/api/v1/profiles/101/speech";
            using var initial = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual(HttpStatusCode.OK, initial.StatusCode, await initial.Content.ReadAsStringAsync());
            var defaults = (await initial.Content.ReadFromJsonAsync<SpeechPreferencesV1>())!;
            Assert.AreEqual((1m, 1m, 1m, 0L, 0), (defaults.Rate, defaults.Pitch, defaults.Volume, defaults.Version, defaults.Voices.Count));

            using var saved = await host.SendAsync(HttpMethod.Put, route, new SaveSpeechPreferencesDataV1(1.25m, 1m, .8m, 0, "de-DE", SpeechProviderType.Device, null, "device:de"));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var next = await host.SendAsync(HttpMethod.Put, route, new SaveSpeechPreferencesDataV1(1.1m, .9m, .7m, 1, "id-ID", SpeechProviderType.Device, null, "device:id"));
            Assert.AreEqual(HttpStatusCode.OK, next.StatusCode, await next.Content.ReadAsStringAsync());
            using var read = await host.SendAsync(HttpMethod.Get, route);
            var preferences = (await read.Content.ReadFromJsonAsync<SpeechPreferencesV1>())!;
            Assert.AreEqual((1.1m, .9m, .7m, 2L), (preferences.Rate, preferences.Pitch, preferences.Volume, preferences.Version));
            CollectionAssert.AreEqual(new[] { "device:de", "device:id" }, preferences.Voices.Select(item => item.VoiceId).ToArray());

            using var stale = await host.SendAsync(HttpMethod.Put, route, new SaveSpeechPreferencesDataV1(2m, 1m, 1m, 1, "de-DE", SpeechProviderType.Device, null, "wrong"));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var foreign = await host.SendAsync(HttpMethod.Get, "/api/v1/profiles/102/speech");
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            using var foreignSave = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/102/speech", new SaveSpeechPreferencesDataV1(1m, 1m, 1m, 0, "de", SpeechProviderType.Device, null, "foreign"));
            Assert.AreEqual(HttpStatusCode.NotFound, foreignSave.StatusCode);

            // Clearing one preference inherits the device default without deleting another language.
            using var cleared = await host.SendAsync(HttpMethod.Put, route, new SaveSpeechPreferencesDataV1(1.1m, .9m, .7m, 2, "de-DE", SpeechProviderType.Device, null, null));
            Assert.AreEqual(HttpStatusCode.OK, cleared.StatusCode);
            using var after = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual("device:id", (await after.Content.ReadFromJsonAsync<SpeechPreferencesV1>())!.Voices.Single().VoiceId);
        }

        [TestMethod]
        public async Task Preferences_NeuralChoiceRequiresActivatedCompatibleModelAndVoice()
        {
            using var storage = new TemporaryDirectory();
            using var models = new TemporaryDirectory();
            await File.WriteAllTextAsync(Path.Combine(models.Path, "catalog.json"), Catalog());
            using var source = new ModelSource();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path,
                settings: new Dictionary<string, string> { ["Media:SpeechPath"] = models.Path },
                services: services => services.AddHttpClient<SpeechModelClient>().ConfigurePrimaryHttpMessageHandler(() => source));
            const string route = "/api/v1/profiles/101/speech";
            var input = new SaveSpeechPreferencesDataV1(1m, 1m, 1m, 0, "id-ID", SpeechProviderType.OfflineNeural, "piper-test", "0");
            using var missing = await host.SendAsync(HttpMethod.Put, route, input);
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ProfileSpeechSettings\""));
            using var activated = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/speech/models/" + Hash + "/activation", new UpdateSpeechModelDataV1(true, 0));
            Assert.AreEqual(HttpStatusCode.OK, activated.StatusCode);
            using var incompatible = await host.SendAsync(HttpMethod.Put, route, new SaveSpeechPreferencesDataV1(1m, 1m, 1m, 0, "de", SpeechProviderType.OfflineNeural, "piper-test", "0"));
            Assert.AreEqual(HttpStatusCode.BadRequest, incompatible.StatusCode);
            using var wrongVoice = await host.SendAsync(HttpMethod.Put, route, new SaveSpeechPreferencesDataV1(1m, 1m, 1m, 0, "id", SpeechProviderType.OfflineNeural, "piper-test", "unknown"));
            Assert.AreEqual(HttpStatusCode.BadRequest, wrongVoice.StatusCode);
            using var saved = await host.SendAsync(HttpMethod.Put, route, input);
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var read = await host.SendAsync(HttpMethod.Get, route);
            var choice = (await read.Content.ReadFromJsonAsync<SpeechPreferencesV1>())!.Voices.Single();
            Assert.AreEqual((SpeechProviderType.OfflineNeural, "piper-test", "0"), (choice.ProviderTypeId, choice.ModelId, choice.VoiceId));
        }

        [TestMethod]
        [DataRow(0.49)]
        [DataRow(2.51)]
        [DataRow(1.111)]
        public void Preferences_InvalidRate_IsRejected(double rate)
        {
            var input = new SaveSpeechPreferencesDataV1((decimal)rate, 1m, 1m, 0, "de", SpeechProviderType.Device, null, null);
            Assert.ThrowsExactly<DataValidationException>(() => input.Validate());
        }

        private static string Catalog() => JsonSerializer.Serialize(new
        {
            models = new[] { new { id = "piper-test", version = "1", name = "Test voice", language = "id-ID", voice = "0", license = "CC-BY-NC-SA-4.0",
                licenseUrl = "https://models.example.test/license", source = "https://models.example.test/package", sizeBytes = Package.Length, sha256 = Hash } }
        });

        private sealed class ModelSource : HttpMessageHandler
        {
            internal bool Tampered { get; init; }
            internal int Downloads { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Downloads++;
                Assert.AreEqual("models.example.test", request.RequestUri!.Host);
                var bytes = Package.ToArray();
                if (Tampered) bytes[0]++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
        }
    }
}
