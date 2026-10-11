using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Localization;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Localization;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Localization;
using Jularr.Backend.Service.Core.Localization;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Public.Localization.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Fixture = Jularr.Backend.Tests.ProfileReferenceVerticalTests.ProfileFixture;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class UiLocalizationTests
    {
        [TestMethod]
        public async Task Synchronization_Idempotent_PreservesManualContextAndTranslations()
        {
            await using var fixture = await Fixture.CreateAsync();
            await SynchronizeAsync(fixture, new UiTextResource("greeting", "Hello {name}"));
            var id = await ScalarAsync<long>(fixture, "SELECT \"Id\" FROM \"UiTranslationMessages\" WHERE \"Key\" = 'greeting'");
            await MutateAsync(fixture, context => context.UpdateUiDescriptionAsync(
                new UiDescriptionUpdateData(id, 1, "Friendly greeting. Preserve the name placeholder."), default));
            await MutateAsync(fixture, context => context.SaveUiTranslationAsync(
                new UiTranslationSaveData(1, id, 2, "Hallo {name}", UiTranslationStatusType.Manual), default));
            var version = await ScalarAsync<long>(fixture, "SELECT \"CatalogVersion\" FROM \"UiLocales\" WHERE \"Id\" = 1");

            await SynchronizeAsync(fixture, new UiTextResource("greeting", "Hello {name}"));
            Assert.AreEqual(version, await ScalarAsync<long>(fixture, "SELECT \"CatalogVersion\" FROM \"UiLocales\" WHERE \"Id\" = 1"));
            await SynchronizeAsync(fixture, new UiTextResource("greeting", "Welcome {name}"));
            await MutateAsync(fixture, context => context.SaveUiTranslationAsync(
                new UiTranslationSaveData(1, id, 3, "Willkommen {name}", UiTranslationStatusType.Generated), default));
            Assert.AreEqual("Hallo {name}", await ScalarAsync<string>(fixture, "SELECT \"Text\" FROM \"UiTranslations\""));
            Assert.AreEqual("Friendly greeting. Preserve the name placeholder.",
                await ScalarAsync<string>(fixture, "SELECT \"Description\" FROM \"UiTranslationMessages\" WHERE \"Key\" = 'greeting'"));

            // A stale administrator edit must not silently overwrite another edit.
            await Assert.ThrowsExactlyAsync<DataValidationException>(() => MutateAsync(fixture,
                context => context.UpdateUiDescriptionAsync(new UiDescriptionUpdateData(id, 1, "Stale edit"), default)));
            Assert.AreEqual(3L, await ScalarAsync<long>(fixture, "SELECT \"Version\" FROM \"UiTranslationMessages\" WHERE \"Key\" = 'greeting'"));
        }

        [TestMethod]
        public async Task Bundle_ExactParentEnglish_RefreshesVersionsAndSkipsOutdated()
        {
            await using var fixture = await Fixture.CreateAsync();
            await SynchronizeAsync(fixture, new UiTextResource("a", "English A"),
                new UiTextResource("b", "English B"), new UiTextResource("c", "English C"));
            await fixture.RunSqlAsync(
                """
                INSERT INTO "UiLocales" ("Id", "Locale", "Name") VALUES (4, 'de', 'Deutsch'), (5, 'de-CH', 'Schweiz');
                INSERT INTO "UiTranslations" ("UiLocaleId", "UiTranslationMessageId", "Text", "UiTranslationStatusTypeId", "MessageVersion")
                SELECT 4, "Id", 'Parent A', 3, "Version" FROM "UiTranslationMessages" WHERE "Key" = 'a';
                INSERT INTO "UiTranslations" ("UiLocaleId", "UiTranslationMessageId", "Text", "UiTranslationStatusTypeId", "MessageVersion")
                SELECT 5, "Id", 'Exact B', 4, "Version" FROM "UiTranslationMessages" WHERE "Key" = 'b';
                """);
            var time = new CatalogClock();
            using var cache = new UiLocalizationCache(time);
            var runtime = new ServiceRuntime(fixture.Database,
                new ServiceGate(new SessionAuthentication(fixture.Database), new HttpContextAccessor()));
            var service = runtime.Create(new ReadUiTextsV1(cache));
            var bundle = await service.ExecuteAsync<ReadUiTextsResultV1>(new ReadUiTextsParametersV1(5));
            Assert.AreEqual("Parent A", bundle["a"]);
            Assert.AreEqual("Exact B", bundle["b"]);
            Assert.AreEqual("English C", bundle["c"]);
            Assert.AreEqual("unregistered.key", bundle["unregistered.key"]);
            var requests = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                service.ExecuteAsync<ReadUiTextsResultV1>(new ReadUiTextsParametersV1(5))));
            Assert.IsTrue(requests.All(result => ReferenceEquals(bundle, result)));

            await SynchronizeAsync(fixture, new UiTextResource("a", "Changed A"));
            Assert.AreEqual((short)UiTranslationStatusType.Outdated,
                await ScalarAsync<short>(fixture, "SELECT \"UiTranslationStatusTypeId\" FROM \"UiTranslations\" WHERE \"UiLocaleId\" = 4"));
            time.Advance();
            var refreshed = await service.ExecuteAsync<ReadUiTextsResultV1>(new ReadUiTextsParametersV1(5));
            Assert.AreNotEqual(bundle.Version, refreshed.Version);
            Assert.AreEqual("Changed A", refreshed["a"]);
            Assert.AreEqual("Exact B", refreshed["b"]);

            await fixture.RunSqlAsync("UPDATE \"UiLocales\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 5");
            time.Advance();
            Assert.AreEqual("de", (await service.ExecuteAsync<ReadUiTextsResultV1>(new ReadUiTextsParametersV1(5))).Locale);
            Assert.AreEqual("en", (await service.ExecuteAsync<ReadUiTextsResultV1>(new ReadUiTextsParametersV1(999))).Locale);

            // A warm Public read needs no connection, but still applies canonical parameter validation.
            await using var unavailable = new NpgsqlDataSourceBuilder(
                "Host=127.0.0.1;Port=1;Username=unused;Database=unused;Timeout=1").Build();
            var offlineRuntime = new ServiceRuntime(unavailable,
                new ServiceGate(new SessionAuthentication(unavailable), new HttpContextAccessor()));
            var offlineService = offlineRuntime.Create(new ReadUiTextsV1(cache));
            Assert.AreEqual("English C", (await offlineService.ExecuteAsync<ReadUiTextsResultV1>(new ReadUiTextsParametersV1(999)))["c"]);
            await Assert.ThrowsExactlyAsync<DataValidationException>(() =>
                offlineService.ExecuteAsync<ReadUiTextsResultV1>(new ReadUiTextsParametersV1(0)));

            // The API adapter exposes the same services and JSON contract without authentication.
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration["JULARR_DB"] = fixture.ConnectionString;
            builder.Services.AddJularrServices(builder.Configuration);
            builder.Services.AddJularrLocalization([]);
            await using var app = builder.Build();
            app.UseExceptionHandler();
            app.UseRateLimiter();
            app.MapJularrServices();
            await app.StartAsync();
            using var client = app.GetTestClient();
            var response = await client.GetFromJsonAsync<ReadUiTextsResultV1>("/api/v1/localization/texts/5");
            Assert.AreEqual("de", response!.Locale);
            Assert.AreEqual("Changed A", response["a"]);
            var locales = await client.GetFromJsonAsync<BaseDataList<UiLocaleDetailsV1>>("/api/v1/localization/locales");
            Assert.IsNotNull(locales);
            Assert.IsTrue(locales.Any(locale => locale.Locale == "en"));
            Assert.IsFalse(locales.Any(locale => locale.Id == 5));
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/localization/texts/0")).StatusCode);
        }

        [TestMethod]
        public async Task Translation_PlaceholderFailure_RollsBackAndConstraintsRejectDuplicates()
        {
            await using var fixture = await Fixture.CreateAsync();
            await SynchronizeAsync(fixture, new UiTextResource("named", "Hello {name}"));
            var id = await ScalarAsync<long>(fixture, "SELECT \"Id\" FROM \"UiTranslationMessages\" WHERE \"Key\" = 'named'");
            await Assert.ThrowsExactlyAsync<DataValidationException>(() => MutateAsync(fixture, async context =>
            {
                await context.UpdateUiDescriptionAsync(new UiDescriptionUpdateData(id, 1, "Greeting"), default);
                await context.SaveUiTranslationAsync(new UiTranslationSaveData(1, id, 2, "Hallo", UiTranslationStatusType.Manual), default);
            }));
            Assert.AreEqual(1L, await ScalarAsync<long>(fixture, "SELECT \"Version\" FROM \"UiTranslationMessages\" WHERE \"Key\" = 'named'"));
            await Assert.ThrowsExactlyAsync<PostgresException>(() => fixture.RunSqlAsync(
                "INSERT INTO \"UiTranslationMessages\" (\"Key\", \"EnglishText\") VALUES ('named', 'Duplicate')"));
            await Assert.ThrowsExactlyAsync<PostgresException>(() => fixture.RunSqlAsync(
                "INSERT INTO \"UiTranslations\" (\"UiLocaleId\", \"UiTranslationMessageId\", \"MessageVersion\", \"UiTranslationStatusTypeId\") VALUES (1, 999, 1, 99)"));
            Assert.ThrowsExactly<InvalidOperationException>(() => new ServiceCollection().AddJularrLocalization(
                [new UiTextResource("same.key", "First"), new UiTextResource("same.key", "Conflicting")]));
        }

        private static async Task SynchronizeAsync(Fixture fixture, params UiTextResource[] resources)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new SqlDatabase(fixture.Database));
            services.AddJularrLocalization(resources);
            // The fixture retains ownership of its data source; this provider is registration-only.
            using var provider = services.BuildServiceProvider();
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(default);
            }
        }

        private static async Task MutateAsync(Fixture fixture, Func<LogicContext, Task> action)
        {
            await using var sql = new SqlContext(fixture.Database);
            await sql.BeginAsync(SqlAccessMode.ReadWrite);
            var context = (LogicContext)Activator.CreateInstance(typeof(LogicContext),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [sql], null)!;
            await action(context);
            await sql.CommitAsync();
        }

        private static async Task<T> ScalarAsync<T>(Fixture fixture, string statement)
        {
            await using var connection = await fixture.Database.OpenConnectionAsync();
            await using var sql = new NpgsqlCommand(statement, connection);
            return (T)(await sql.ExecuteScalarAsync())!;
        }

        private sealed class CatalogClock : TimeProvider
        {
            private DateTimeOffset _now = DateTimeOffset.UtcNow;
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance() => _now = _now.AddSeconds(31);
        }
    }
}
