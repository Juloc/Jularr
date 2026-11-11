using System.Net;
using System.Net.Sockets;
using System.Text;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ManualTime = Jularr.Backend.Tests.WorkerJobTests.ManualTime;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ExternalCallLimitTests
    {
        private static readonly ExternalCallLimits Limits = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10));

        [TestMethod]
        public async Task CallLimits_AServerThatNeverAnswers_FailsAfterFirstByte()
        {
            await using var server = new SilentTcpServer();
            var time = new ManualTime(DateTimeOffset.UtcNow);
            using var http = Client(time);

            var call = http.GetAsync(server.Address);
            await server.RequestAsync();
            time.Advance(TimeSpan.FromSeconds(4.9));
            await Task.Delay(50);
            Assert.IsFalse(call.IsCompleted, "The limit is not reached yet.");

            time.Advance(TimeSpan.FromSeconds(0.1));
            var timeout = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => call);
            Assert.IsInstanceOfType<TimeoutException>(timeout.InnerException);
            StringAssert.Contains(timeout.InnerException.Message, "did not answer within 5 s");
        }

        [TestMethod]
        public async Task CallLimits_HeadersThenSilence_FailsAfterIdle()
        {
            // Read by the caller.
            await using (var server = new SilentTcpServer())
            {
                var time = new ManualTime(DateTimeOffset.UtcNow);
                using var http = Client(time);
                var call = http.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
                await server.SendAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nabc");
                using var response = await call;
                await using var body = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[100];
                Assert.AreEqual(3, await body.ReadAsync(buffer));

                var read = body.ReadAsync(buffer).AsTask();
                time.Advance(TimeSpan.FromSeconds(4));
                var timeout = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => read);
                Assert.IsInstanceOfType<TimeoutException>(timeout.InnerException);
                StringAssert.Contains(timeout.Message, "did not send more data within 4 s");
            }

            // Buffered by HttpClient.
            await using (var server = new SilentTcpServer())
            {
                var time = new ManualTime(DateTimeOffset.UtcNow);
                using var http = Client(time);
                var call = http.GetAsync(server.Address);
                await server.SendAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nabc");
                await time.WaitForTimerAsync(Limits.Idle);
                time.Advance(TimeSpan.FromSeconds(4));
                var timeout = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => call);
                Assert.IsInstanceOfType<TimeoutException>(timeout.InnerException);
            }
        }

        [TestMethod]
        public async Task CallLimits_ATricklingServer_FailsAfterTotal()
        {
            await using var server = new SilentTcpServer();
            var time = new ManualTime(DateTimeOffset.UtcNow);
            using var http = Client(time);
            var call = http.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
            await server.SendAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n");
            using var response = await call;
            await using var body = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[100];

            // A byte every three seconds never trips the idle limit, only the whole call's.
            for (var second = 3; second <= 9; second += 3)
            {
                var read = body.ReadAsync(buffer).AsTask();
                time.Advance(TimeSpan.FromSeconds(3));
                await server.SendAsync("x");
                Assert.AreEqual(1, await read);
            }

            var last = body.ReadAsync(buffer).AsTask();
            time.Advance(TimeSpan.FromSeconds(3));
            var timeout = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => last);
            Assert.IsInstanceOfType<TimeoutException>(timeout.InnerException);
            StringAssert.Contains(timeout.Message, "did not finish within 10 s");
        }

        [TestMethod]
        public async Task CallLimits_ACallerWhoGivesUp_GetsAnOrdinaryCancellation()
        {
            // While waiting for the answer.
            await using (var server = new SilentTcpServer())
            {
                using var http = Client(new ManualTime(DateTimeOffset.UtcNow));
                using var stop = new CancellationTokenSource();
                var call = http.GetAsync(server.Address, stop.Token);
                await server.RequestAsync();
                await stop.CancelAsync();
                var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => call);
                Assert.AreEqual(stop.Token, cancelled.CancellationToken);
                Assert.IsNotInstanceOfType<TimeoutException>(cancelled.InnerException);
            }

            // While reading the body.
            await using (var server = new SilentTcpServer())
            {
                using var http = Client(new ManualTime(DateTimeOffset.UtcNow));
                var call = http.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
                await server.SendAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n");
                using var response = await call;
                await using var body = await response.Content.ReadAsStreamAsync();
                using var stop = new CancellationTokenSource();
                var read = body.ReadAsync(new byte[100], stop.Token).AsTask();
                await stop.CancelAsync();
                var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => read);
                Assert.IsNotInstanceOfType<TimeoutException>(cancelled.InnerException);
            }
        }

        [TestMethod]
        public async Task CallLimits_ARequestWithItsOwnLimits_WaitsForThose()
        {
            await using var server = new SilentTcpServer();
            var time = new ManualTime(DateTimeOffset.UtcNow);
            using var http = Client(time);
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Address);
            request.Options.Set(CallLimitHandler.OwnLimits, ExternalCallLimits.Download(20, TimeSpan.FromHours(1)));

            var call = http.SendAsync(request);
            await server.RequestAsync();
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(50);
            Assert.IsFalse(call.IsCompleted, "The client's own five seconds do not apply.");
            time.Advance(TimeSpan.FromSeconds(10));
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => call);
        }

        [TestMethod]
        public void Registration_EveryExternalClient_HasTheTransactionGuardCallLimitsAndAConnectTimeout()
        {
            // The DSM certificate check needs the request, which only HttpClientHandler hands over; its answer limit bounds the connect as well.
            var withoutConnectTimeout = new Dictionary<string, string> { ["SynologyClient"] = "certificate pin per request" };

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JULARR_DB"] = "Host=127.0.0.1;Port=1;Database=none",
                ["DataPath"] = Path.Combine(Path.GetTempPath(), "jularr-tests", Guid.NewGuid().ToString("N"))
            }).Build();
            var services = new ServiceCollection().AddLogging().AddJularrServices(configuration);
            using var provider = services.BuildServiceProvider();
            var handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>();
            var clients = provider.GetRequiredService<IHttpClientFactory>();

            var typed = services.Select(service => service.ServiceType).Distinct()
                .Where(type => type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(HttpClient))))
                .ToArray();
            Assert.IsGreaterThan(25, typed.Length, "The external clients were not found.");

            foreach (var type in typed)
            {
                var chain = new List<HttpMessageHandler>();
                for (HttpMessageHandler? handler = handlers.CreateHandler(type.Name); handler is not null; handler = (handler as DelegatingHandler)?.InnerHandler)
                {
                    chain.Add(handler);
                }

                var guard = chain.FindIndex(handler => handler is ExternalCallGuard);
                Assert.IsTrue(guard >= 0 && guard < chain.FindIndex(handler => handler is CallLimitHandler), type.Name + " lets calls out while a transaction is open.");
                var limits = chain.OfType<CallLimitHandler>().SingleOrDefault()?.Limits;
                Assert.IsNotNull(limits, type.Name + " has no call limits.");
                Assert.IsTrue(limits.Connect > TimeSpan.Zero && limits.Connect <= ExternalCallLimits.ConnectLimit && limits.FirstByte <= limits.Total && limits.Idle <= limits.Total, type.Name);
                Assert.AreEqual(Timeout.InfiniteTimeSpan, clients.CreateClient(type.Name).Timeout, type.Name + " keeps a second timeout.");
                if (chain[^1] is SocketsHttpHandler sockets)
                {
                    Assert.AreEqual(limits.Connect, sockets.ConnectTimeout, type.Name);
                }
                else
                {
                    Assert.IsTrue(withoutConnectTimeout.ContainsKey(type.Name), type.Name + " connects without a limit: " + chain[^1].GetType().Name);
                }
            }
        }

        private static HttpClient Client(TimeProvider time) =>
            new(new CallLimitHandler(Limits, time) { InnerHandler = new SocketsHttpHandler { ConnectTimeout = Limits.Connect } }) { Timeout = Timeout.InfiniteTimeSpan };

        // A real server on the loopback that reads the request and then writes only what the test hands it.
        private sealed class SilentTcpServer : IAsyncDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly Task<(TcpClient Client, NetworkStream Stream)> _request;

            public SilentTcpServer()
            {
                _listener.Start();
                Address = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/file");
                _request = AcceptAsync();
            }

            public Uri Address { get; }

            public async Task RequestAsync() => await _request.WaitAsync(TimeSpan.FromSeconds(10));

            public async Task SendAsync(string text)
            {
                var (_, stream) = await _request.WaitAsync(TimeSpan.FromSeconds(10));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
                await stream.FlushAsync();
            }

            private async Task<(TcpClient Client, NetworkStream Stream)> AcceptAsync()
            {
                var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var received = new StringBuilder();
                var buffer = new byte[1024];
                while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0) break;
                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                return (client, stream);
            }

            public async ValueTask DisposeAsync()
            {
                _listener.Stop();
                if (_request.IsCompletedSuccessfully)
                {
                    (await _request).Client.Dispose();
                }
            }
        }
    }
}
