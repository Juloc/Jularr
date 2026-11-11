using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // What the host logs on its own: in normal operation the admin log holds real messages, not a warning per request.
    [TestClass]
    public sealed class HostPipelineTests
    {
        private const string AntiforgeryCategory = "Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery";

        // A Plex sign-in that shows its page again, signing in, switching the profile and a hundred page views leave no overridden-cache-header
        // warning: each answer that carries an antiforgery token already has the values the token sets, so caching stays off without the warning.
        [TestMethod]
        public async Task Antiforgery_AHundredPageViews_LeaveNoOverriddenHeaderWarning()
        {
            await using var host = await Host.CreateAsync(plexEnabled: true, razor: true);
            var logs = host.Services.GetRequiredService<RecentLogs>();
            var offenders = new List<string>();
            async Task VisitAsync(HttpMethod method, string path, object? data = null)
            {
                var before = Warnings(logs);
                using var response = await host.SendAsync(method, path, data);
                Assert.IsLessThan(500, (int)response.StatusCode, method + " " + path);
                if (Warnings(logs) > before)
                {
                    offenders.Add(method + " " + path + " (" + (int)response.StatusCode + ")");
                }
            }

            // The Plex sign-in starts and waits for approval: both answers show the sign-in page again after the authentication was applied.
            using (var login = await host.SendAsync(HttpMethod.Get, "/Account/Login"))
            {
                var started = await StartPlexAsync(host, await login.Content.ReadAsStringAsync(), offenders, logs);
                var id = Regex.Match(started, "name=\"AuthChallengePublicId\" value=\"([^\"]+)\"").Groups[1].Value;
                await PostFormAsync(host, "/Account/Login?handler=PlexComplete", started, offenders, logs, ("AuthChallengePublicId", id));
            }

            using (var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth"))
            {
                Assert.AreEqual(HttpStatusCode.OK, state.StatusCode);
            }

            await VisitAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            var profile = (long)(await host.ScalarAsync("SELECT min(\"ProfileId\") FROM \"AccountProfiles\" WHERE \"AccountId\" = 41"))!;
            await VisitAsync(HttpMethod.Put, "/api/v1/auth/profile/" + profile);
            await VisitAsync(HttpMethod.Post, "/Account/Profiles?profileId=" + profile + "&returnUrl=%2F", new Dictionary<string, string>());

            // Every page without a required route value, as the signed-in Owner, until a hundred views.
            var pages = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null && endpoint.RoutePattern.Parameters.All(parameter => parameter.IsOptional))
                .Select(endpoint => "/" + string.Join('/', endpoint.RoutePattern.PathSegments.Select(segment => segment.Parts.OfType<Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart>().FirstOrDefault()?.Content).TakeWhile(part => part is not null)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            Assert.IsGreaterThan(20, pages.Length, string.Join(", ", pages));
            for (var view = 0; view < Math.Max(100, pages.Length); view++)
            {
                await VisitAsync(HttpMethod.Get, pages[view % pages.Length]);
            }

            Assert.AreEqual(0, offenders.Count, "Overridden cache headers on: " + string.Join(", ", offenders.Distinct()));
            Assert.AreEqual(0, Warnings(logs));
        }

        // Without an HTTPS port or endpoint (HTTPS is the reverse proxy's job) nothing is redirected and no request warns that no port was found;
        // the start says so once.
        [TestMethod]
        public async Task HttpsRedirect_WithoutAnHttpsPort_StaysOff_AndSaysSoOnce()
        {
            var log = new CollectingLog();
            await using var app = await StartAsync(log, httpsPort: null);
            using var client = app.GetTestClient();
            for (var request = 0; request < 3; request++)
            {
                using var response = await client.GetAsync("/page");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.IsFalse(log.Messages.Any(message => message.StartsWith("Failed to determine the https port", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", log.Messages));
            Assert.AreEqual(1, log.Messages.Count(message => message == "HTTPS redirection is off: no HTTPS port or endpoint is configured."), string.Join(" | ", log.Messages));
        }

        // With a configured HTTPS port pages are redirected to it; the container's probes still answer over plain HTTP.
        [TestMethod]
        public async Task HttpsRedirect_WithAConfiguredHttpsPort_Redirects_ButNotTheProbes()
        {
            var log = new CollectingLog();
            await using var app = await StartAsync(log, httpsPort: "8443");
            using var client = app.GetTestClient();
            using var page = await client.GetAsync("/page");
            Assert.AreEqual(HttpStatusCode.TemporaryRedirect, page.StatusCode);
            Assert.AreEqual("https://localhost:8443/page", page.Headers.Location!.ToString());
            using var probe = await client.GetAsync(Health.LivePath);
            Assert.AreEqual(HttpStatusCode.OK, probe.StatusCode);
            Assert.IsFalse(log.Messages.Any(message => message.StartsWith("HTTPS redirection is off", StringComparison.Ordinal)));
        }

        private static async Task<WebApplication> StartAsync(CollectingLog log, string? httpsPort)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders().AddProvider(log);
            if (httpsPort is not null)
            {
                builder.Configuration["HTTPS_PORT"] = httpsPort;
            }

            builder.Services.AddHealthChecks();
            var app = builder.Build();
            app.UseJularrHttpsRedirection();
            app.MapGet("/page", () => "page");
            app.MapJularrHealth();
            await app.StartAsync();
            return app;
        }

        private static Task<string> StartPlexAsync(Host host, string html, List<string> offenders, RecentLogs logs) =>
            PostFormAsync(host, "/Account/Login?handler=Plex", html, offenders, logs);

        private static async Task<string> PostFormAsync(Host host, string path, string html, List<string> offenders, RecentLogs logs, params (string Name, string Value)[] values)
        {
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            Assert.IsTrue(token.Success, path);
            var form = values.ToDictionary(value => value.Name, value => value.Value);
            form.Add("__RequestVerificationToken", WebUtility.HtmlDecode(token.Groups[1].Value));
            var before = Warnings(logs);
            using var response = await host.SendAsync(HttpMethod.Post, path, form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
            if (Warnings(logs) > before)
            {
                offenders.Add("POST " + path);
            }

            return await response.Content.ReadAsStringAsync();
        }

        private static int Warnings(RecentLogs logs) => logs.Read(1000).Count(entry => entry.Category == AntiforgeryCategory);

        private sealed class CollectingLog : ILoggerProvider, ILogger
        {
            private readonly ConcurrentQueue<string> _messages = new();

            public IReadOnlyList<string> Messages => [.. _messages];

            public ILogger CreateLogger(string categoryName) => this;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    _messages.Enqueue(formatter(state, exception));
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
