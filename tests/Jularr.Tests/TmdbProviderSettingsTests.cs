using System.Net;
using System.Text.Json;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>TMDB as a provider of the shared Provider UI: its view, save, test and remove, the health they re-evaluate and the secrets they must never show.</summary>
[TestClass]
public sealed class TmdbProviderSettingsTests
{
    private const string ValidToken = "valid-token-123";
    private const string WrongToken = "wrong-token-456";

    private sealed class Rig : IAsyncDisposable
    {
        public required Jularr.Web.Data.AppDbContext Db { get; init; }

        public required TmdbSettingsService Service { get; init; }

        public required TmdbCredentialStore Store { get; init; }

        public required ProviderHealthTracker Health { get; init; }

        public required ProviderResponseCache Cache { get; init; }

        public required DiscoverySourceFlights Flights { get; init; }

        public required InstanceModuleStore Modules { get; init; }

        public required List<string> Requests { get; init; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<Rig> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage>? handler = null, string? deploymentApiKey = null)
    {
        var db = await MediaCoreTestSupport.CreateDbAsync();
        var directory = Path.Combine(Path.GetTempPath(), "jularr-tmdb-" + Guid.NewGuid().ToString("N"));
        var store = TmdbTestSupport.Credentials(apiKey: deploymentApiKey, directory: directory);
        var health = new ProviderHealthTracker(TimeProvider.System);
        var requests = new List<string>();
        var client = TmdbDiscoveryTests.Client(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            if (handler is not null)
            {
                return handler(request);
            }

            var authorized = request.Headers.Authorization?.Parameter == ValidToken || request.RequestUri.Query.Contains("api_key=" + ValidToken, StringComparison.Ordinal);
            return authorized ? TmdbDiscoveryTests.Json("{\"images\":{}}") : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var provider = TmdbDiscoveryTests.Provider(db, client, store, health);
        var cache = new ProviderResponseCache(TimeProvider.System);
        var flights = DiscoveryTestSupport.Flights();
        var modules = new InstanceModuleStore(directory);
        var rig = new Rig { Requests = requests, Db = db, Store = store, Health = health, Cache = cache, Flights = flights, Modules = modules, Service = new TmdbSettingsService(store, provider, health, cache, flights, modules) };
        return rig;
    }

    private static Dictionary<string, string?> Typed(string? token = null, string? apiKey = null) => new() { [TmdbSettingsService.TokenField] = token, [TmdbSettingsService.ApiKeyField] = apiKey };

    [TestMethod]
    public async Task AFreshInstanceWithMovieAndTvEnabledNeedsTmdbAndBlocksSetup()
    {
        await using var rig = await CreateAsync();

        var view = await rig.Service.GetViewAsync(CancellationToken.None);

        Assert.AreEqual(ProviderConnectionState.NotConfigured, view.State);
        Assert.IsNotNull(view.RequiredReasonKey);
        Assert.IsNotNull(view.Blocking, "Setup must not finish while Movie or TV is enabled and TMDB cannot be used.");
        Assert.AreEqual("setup.provider.blocked.title", view.Blocking.TitleKey);
        Assert.AreEqual(2, view.Fields.Count);
    }

    [TestMethod]
    public async Task DisablingMovieAndTvLiftsTheRequirementAndTheBlock()
    {
        await using var rig = await CreateAsync();

        await rig.Service.DisableDependentFeaturesAsync(CancellationToken.None);
        var view = await rig.Service.GetViewAsync(CancellationToken.None);

        Assert.IsNull(view.RequiredReasonKey);
        Assert.IsNull(view.Blocking);
        var modules = await rig.Modules.GetAsync(CancellationToken.None);
        Assert.IsFalse(modules.IsEnabled(InstanceModule.Movie));
        Assert.IsFalse(modules.IsEnabled(InstanceModule.Tv));
        Assert.IsTrue(modules.IsEnabled(InstanceModule.Anime), "Only the modules that need TMDB are turned off.");
    }

    [TestMethod]
    public async Task SavingMasksTheSecretAndNeverReturnsItInTheViewOrAnyMessage()
    {
        await using var rig = await CreateAsync();

        var feedback = await rig.Service.SaveAsync(true, Typed(ValidToken), CancellationToken.None);
        var view = await rig.Service.GetViewAsync(CancellationToken.None);

        Assert.AreEqual(ProviderFeedback.Saved, feedback);
        Assert.IsTrue(view.Fields.Single(field => field.Name == TmdbSettingsService.TokenField).HasSavedValue);
        Assert.IsFalse(view.Fields.Single(field => field.Name == TmdbSettingsService.ApiKeyField).HasSavedValue);
        Assert.IsNull(view.Blocking);
        var everything = JsonSerializer.Serialize(view) + view;
        Assert.IsFalse(everything.Contains(ValidToken, StringComparison.Ordinal), "A view carries no secret, not even a fragment.");
        Assert.IsFalse(everything.Contains("valid-tok", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnInvalidInputIsRefusedAndStoresNothing()
    {
        await using var rig = await CreateAsync();

        Assert.AreEqual(ProviderFeedback.InvalidInput, await rig.Service.SaveAsync(true, Typed("two words"), CancellationToken.None));
        Assert.AreEqual(ProviderFeedback.InvalidInput, await rig.Service.TestAsync(Typed(apiKey: new string('k', TmdbCredentialStore.MaxApiKeyLength + 1)), CancellationToken.None));

        Assert.AreEqual(TmdbCredentialSource.None, (await rig.Store.GetAsync(CancellationToken.None)).Source);
        Assert.AreEqual(0, rig.Requests.Count, "An invalid value never leaves the process.");
    }

    [TestMethod]
    public async Task TestingATypedCredentialReportsTheAnswerAndNeverTouchesHealth()
    {
        await using var rig = await CreateAsync();

        Assert.AreEqual(ProviderFeedback.TestSucceeded, await rig.Service.TestAsync(Typed(ValidToken), CancellationToken.None));
        Assert.AreEqual(ProviderFeedback.TestAuthenticationFailed, await rig.Service.TestAsync(Typed(WrongToken), CancellationToken.None));
        Assert.AreEqual(ProviderFeedback.TestSucceeded, await rig.Service.TestAsync(Typed(apiKey: ValidToken), CancellationToken.None));

        Assert.AreEqual(ProviderHealthStatus.Unknown, rig.Health.Get(ProviderKeys.Tmdb).Status, "A candidate that was never saved says nothing about the configured provider.");
    }

    [TestMethod]
    public async Task TestingTheSavedCredentialRecordsHealthAndARefusalIsAnAuthenticationFailure()
    {
        await using var rig = await CreateAsync();
        await rig.Service.SaveAsync(true, Typed(WrongToken), CancellationToken.None);

        Assert.AreEqual(ProviderFeedback.TestAuthenticationFailed, await rig.Service.TestAsync(Typed(), CancellationToken.None));
        var refused = await rig.Service.GetViewAsync(CancellationToken.None);
        Assert.AreEqual(ProviderConnectionState.AuthenticationFailed, refused.State);
        Assert.IsNull(refused.LastSuccessUtc, "A refusal is not a successful call.");
        Assert.IsNotNull(refused.LastFailureUtc);
        Assert.IsNotNull(refused.Blocking, "A refused credential is no usable provider.");

        await rig.Service.SaveAsync(true, Typed(ValidToken), CancellationToken.None);
        Assert.AreEqual(ProviderConnectionState.Unknown, (await rig.Service.GetViewAsync(CancellationToken.None)).State, "Saving forgets what was observed with the old credential.");
        Assert.AreEqual(ProviderFeedback.TestSucceeded, await rig.Service.TestAsync(Typed(), CancellationToken.None));
        Assert.AreEqual(ProviderConnectionState.Healthy, (await rig.Service.GetViewAsync(CancellationToken.None)).State);
    }

    [TestMethod]
    public async Task ATestWithoutAnyCredentialSaysSoWithoutCallingTheProvider()
    {
        await using var rig = await CreateAsync();

        Assert.AreEqual(ProviderFeedback.TestNotConfigured, await rig.Service.TestAsync(Typed(), CancellationToken.None));

        Assert.AreEqual(0, rig.Requests.Count);
    }

    [TestMethod]
    public async Task AnUnreachableProviderIsReportedAsSuchAndNotAsARefusal()
    {
        await using var rig = await CreateAsync(_ => throw new HttpRequestException("connection refused"));

        Assert.AreEqual(ProviderFeedback.TestUnreachable, await rig.Service.TestAsync(Typed(ValidToken), CancellationToken.None));
        await rig.Service.SaveAsync(true, Typed(ValidToken), CancellationToken.None);
        Assert.AreEqual(ProviderFeedback.TestUnreachable, await rig.Service.TestAsync(Typed(), CancellationToken.None));
        Assert.AreEqual(ProviderConnectionState.Degraded, (await rig.Service.GetViewAsync(CancellationToken.None)).State);
    }

    [TestMethod]
    public async Task ADeploymentCredentialIsShownAsManagedAndStaysUntouchedByRemove()
    {
        await using var rig = await CreateAsync(deploymentApiKey: ValidToken);
        await rig.Service.SaveAsync(true, Typed(WrongToken), CancellationToken.None);

        var view = await rig.Service.GetViewAsync(CancellationToken.None);

        Assert.IsTrue(view.ExternallyManaged);
        Assert.IsTrue(view.HasSavedValue, "The ignored stored credential is reported so the conflict is never silent.");
        Assert.AreEqual("admin.providers.fact.sourceEnvironment", view.CredentialSourceKey);
        Assert.IsNull(view.Blocking);
        Assert.AreEqual(ProviderFeedback.TestSucceeded, await rig.Service.TestAsync(Typed(), CancellationToken.None), "The effective credential is the deployment one.");
    }

    [TestMethod]
    public async Task ChangingTheConfigurationForgetsRememberedAnswersSoDiscoverIsAskedAgain()
    {
        await using var rig = await CreateAsync();
        rig.Cache.Set("tmdb:browse:Series:Trending", "old answer");
        rig.Cache.Set("anilist:schedule", "other provider");

        await rig.Service.SaveAsync(true, Typed(ValidToken), CancellationToken.None);

        Assert.IsFalse(rig.Cache.TryGetFresh<string>("tmdb:browse:Series:Trending", TimeSpan.FromHours(1), out _));
        Assert.IsTrue(rig.Cache.TryGetFresh<string>("anilist:schedule", TimeSpan.FromHours(1), out _), "Other providers keep their answers.");
    }

    [TestMethod]
    public async Task TheHttpClientPipelineOfTmdbLogsNoRequestAddressSoAnApiKeyNeverReachesALog()
    {
        // Program registers the TMDB client with RemoveAllLoggers(): an API key travels in the query string, which the factory logging would print.
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddHttpClient("tmdb").RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new ConstantHandler());
        using var provider = services.BuildServiceProvider();

        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("tmdb").GetAsync("https://api.themoviedb.org/3/movie/popular?api_key=secret-api-key-777");

        Assert.IsTrue(response.IsSuccessStatusCode);
        Assert.IsFalse(logs.Messages.Any(message => message.Contains("secret-api-key-777", StringComparison.Ordinal)));
    }

    private sealed class ConstantHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    [TestMethod]
    public async Task TheAdapterSignsWithExactlyOneCredentialAndFailsTypedWithoutOne()
    {
        await using var rig = await CreateAsync();
        var seen = new List<(string? Bearer, string Query)>();
        var tokenStore = TmdbTestSupport.Credentials(apiKey: "key-a", readAccessToken: "token-a", directory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var provider = TmdbDiscoveryTests.Provider(rig.Db, TmdbDiscoveryTests.Client(request =>
        {
            seen.Add((request.Headers.Authorization?.Parameter, request.RequestUri!.Query));
            return TmdbDiscoveryTests.Json("{\"results\":[]}");
        }), tokenStore);

        await provider.BrowseAsync(TmdbDiscoveryMediaType.Movie, DiscoveryMode.Top, 5, "", CancellationToken.None);

        Assert.AreEqual("token-a", seen.Single().Bearer);
        Assert.IsFalse(seen.Single().Query.Contains("api_key", StringComparison.Ordinal), "The token is sent as a header; the API key is not sent along with it.");

        var empty = TmdbTestSupport.Credentials(apiKey: null, directory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var none = TmdbDiscoveryTests.Provider(rig.Db, TmdbDiscoveryTests.Client(_ => TmdbDiscoveryTests.Json("{}")), empty);
        await Assert.ThrowsExactlyAsync<ProviderNotConfiguredException>(() => none.BrowseAsync(TmdbDiscoveryMediaType.Movie, DiscoveryMode.Top, 5, "", CancellationToken.None));
    }

    [TestMethod]
    public async Task ARefusedCredentialSurfacesAsATypedAuthenticationFailureOnTheRealPath()
    {
        await using var rig = await CreateAsync();
        var provider = TmdbDiscoveryTests.Provider(rig.Db, TmdbDiscoveryTests.Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), health: rig.Health);

        var failure = await Assert.ThrowsExactlyAsync<ProviderAuthenticationException>(() => provider.SearchAsync(TmdbDiscoveryMediaType.Series, "x", 5, "", CancellationToken.None));

        Assert.AreEqual(ProviderHealthStatus.AuthenticationFailed, rig.Health.Get(ProviderKeys.Tmdb).Status);
        Assert.IsFalse(failure.ToString().Contains("test-key", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(Messages);

        public void Dispose()
        {
        }

        private sealed class Capture(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception) + exception);
                }
            }
        }
    }
}
