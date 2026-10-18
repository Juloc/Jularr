using System.Net;
using System.Text;
using Jularr.Integrations.Data.Tvdb;
using Jularr.Integrations.Logic.Tvdb;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TvdbTests
    {
        [TestMethod]
        public async Task Login_UsesProjectKey_WithoutSendingPinWhenAbsent()
        {
            using var client = CreateClient((request, _) =>
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("/v4/login", request.RequestUri!.AbsolutePath);
                var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                StringAssert.Contains(payload, "\"apikey\":\"project-key\"");
                Assert.IsFalse(payload.Contains("pin", StringComparison.Ordinal));
                return Reply("""{"data":{"token":"bearer-secret"}}""");
            });

            var session = await new TvdbClient(client).LoginAsync("project-key", null, CancellationToken.None);
            Assert.AreEqual("bearer-secret", session.Token);
            Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(session).Contains("bearer-secret", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task ImdbLookup_RejectsMultipleSeries_AndIgnoresMovies()
        {
            using var client = CreateClient((request, _) =>
            {
                Assert.AreEqual("/v4/search/remoteid/tt1234567", request.RequestUri!.AbsolutePath);
                Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
                Assert.AreEqual("session", request.Headers.Authorization.Parameter);
                return Reply("""{"data":[{"movie":{"id":2}},{"series":{"id":71}},{"series":{"id":72}}]}""");
            });
            var service = new TvdbClient(client);
            var error = await Assert.ThrowsExactlyAsync<TvdbException>(() => service.FindSeriesByImdbAsync(new TvdbSession("session"), "tt1234567", CancellationToken.None));
            Assert.AreEqual(TvdbFailure.Ambiguous, error.Failure);
        }

        [TestMethod]
        public async Task Episodes_IncludeSpecialsAndAllPages_WithoutRepeats()
        {
            using var client = CreateClient((request, _) =>
            {
                Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
                Assert.AreEqual("session", request.Headers.Authorization.Parameter);
                return request.RequestUri!.Query.Contains("page=0", StringComparison.Ordinal)
                    ? Reply("""{"data":{"episodes":[{"id":11,"seasonNumber":0,"number":1,"absoluteNumber":0,"name":"OVA","aired":"2025-01-01"}]},"links":{"next":"?page=1"}}""")
                    : Reply("""{"data":{"episodes":[{"id":12,"seasonNumber":1,"number":1,"absoluteNumber":1,"name":"Pilot","runtime":24}]},"links":{"next":null}}""");
            });
            var result = await new TvdbClient(client).ReadEpisodesAsync(new TvdbSession("session"), 123, CancellationToken.None);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual(0, result[0].SeasonNumber);
            Assert.AreEqual(1, result[1].SeasonNumber);
            Assert.AreEqual(1, result[1].AbsoluteNumber);
        }

        [TestMethod]
        public async Task Unauthorized_FailsWithoutRevealingCredential()
        {
            using var client = CreateClient((_, _) => Reply("{}", HttpStatusCode.Unauthorized));
            var error = await Assert.ThrowsExactlyAsync<TvdbException>(() =>
                new TvdbClient(client).ReadSeriesAsync(new TvdbSession("private-token"), 10, CancellationToken.None));
            Assert.AreEqual(TvdbFailure.AuthenticationFailed, error.Failure);
            Assert.IsFalse(error.Message.Contains("private-token", StringComparison.Ordinal));
        }

        private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) =>
            new(new Handler(response)) { BaseAddress = new Uri("https://api4.thetvdb.com/v4/") };

        private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(response(request, cancellationToken));
        }
    }
}
