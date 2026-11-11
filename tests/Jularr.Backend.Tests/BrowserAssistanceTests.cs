using System.Net;
using System.Net.Http.Json;
using System.Security;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Browser;
using Microsoft.Extensions.DependencyInjection;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class BrowserAssistanceTests
    {
        public TestContext TestContext { get; set; } = null!;
        private static readonly Uri Source = new("https://annas-archive.gl/");

        [TestMethod]
        public async Task Lease_OnlyOpeningSignInSessionCanReadTypeAndClose()
        {
            var browser = new Interaction();
            using var assistance = new BrowserAssistance(browser, new Clock());
            var owner = Guid.NewGuid();
            var other = Guid.NewGuid();
            var view = await assistance.OpenAsync(owner, 7, Source, default);
            Assert.AreEqual(view.LeaseId, (await assistance.OpenAsync(owner, 7, Source, default)).LeaseId);
            Assert.AreEqual(2, browser.Starts);
            Assert.IsNull(await assistance.ReadOwnAsync(other, default));
            await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => assistance.OpenAsync(other, 7, Source, default));
            await Assert.ThrowsExactlyAsync<SecurityException>(() => assistance.FrameAsync(other, view.LeaseId, default));
            await Assert.ThrowsExactlyAsync<SecurityException>(() => assistance.InputAsync(other, view.LeaseId, new("text", text: "secret"), default));
            await Assert.ThrowsExactlyAsync<SecurityException>(() => assistance.CloseAsync(other, view.LeaseId, default));
            Assert.AreEqual(0, browser.Inputs);
            await assistance.InputAsync(owner, view.LeaseId, new("text", text: " user text "), default);
            Assert.AreEqual(" user text ", browser.Text);
            await assistance.CloseAsync(owner, view.LeaseId, default);
            Assert.AreEqual(1, browser.Ends);
            Assert.IsFalse(browser.HasManualControl);
            await Assert.ThrowsExactlyAsync<ServiceGoneException>(() => assistance.FrameAsync(owner, view.LeaseId, default));
        }

        [TestMethod]
        public async Task Lease_ExpiresAndBrowserResetInvalidatesIt()
        {
            var clock = new Clock();
            var browser = new Interaction();
            using var assistance = new BrowserAssistance(browser, clock);
            var owner = Guid.NewGuid();
            var view = await assistance.OpenAsync(owner, 7, Source, default);
            clock.Now += TimeSpan.FromMinutes(3);
            var expired = await assistance.ReadOwnAsync(owner, default);
            Assert.IsNotNull(expired);
            Assert.IsFalse(expired.IsActive);
            Assert.AreEqual(7L, expired.AcquisitionConnectionId);
            await Assert.ThrowsExactlyAsync<ServiceGoneException>(() => assistance.InputAsync(owner, view.LeaseId, new("key", key: "Enter"), default));
            var next = await assistance.OpenAsync(owner, 7, Source, default);
            Assert.AreNotEqual(view.LeaseId, next.LeaseId);
            browser.HasManualControl = false;
            await Assert.ThrowsExactlyAsync<ServiceGoneException>(() => assistance.FrameAsync(owner, next.LeaseId, default));
            Assert.IsFalse((await assistance.ReadOwnAsync(owner, default))!.IsActive);
        }

        [TestMethod]
        public async Task DownloadLease_RetainsItsTargetAndInvalidatesControlsForThePreviousDownload()
        {
            var clock = new Clock();
            var browser = new Interaction();
            using var assistance = new BrowserAssistance(browser, clock);
            var owner = Guid.NewGuid();
            var first = await assistance.OpenAsync(owner, 7, Source, default, 101);
            Assert.AreEqual(first.LeaseId, (await assistance.OpenAsync(owner, 7, Source, default, 101)).LeaseId);
            var page = new Uri(Source, "slow_download/second/0/0");
            var second = await assistance.OpenAsync(owner, 7, page, default, 102);
            Assert.AreNotEqual(first.LeaseId, second.LeaseId);
            await Assert.ThrowsExactlyAsync<ServiceGoneException>(() => assistance.FrameAsync(owner, first.LeaseId, default));
            clock.Now += TimeSpan.FromMinutes(3);
            var expired = (await assistance.ReadOwnAsync(owner, default))!;
            Assert.IsFalse(expired.IsActive);
            Assert.AreEqual(102L, expired.OperationId);
            var reopened = await assistance.OpenAsync(owner, 7, page, default, expired.OperationId);
            Assert.IsTrue(reopened.IsActive);
            Assert.AreEqual(102L, reopened.OperationId);
            Assert.AreEqual(page, browser.Address);
            Assert.IsTrue(browser.OpensPage);
        }

        [TestMethod]
        public async Task Frames_AreBrieflyReusedAndInputInvalidatesThem()
        {
            var clock = new Clock();
            var browser = new Interaction();
            using var assistance = new BrowserAssistance(browser, clock);
            var owner = Guid.NewGuid();
            var view = await assistance.OpenAsync(owner, 7, Source, default);
            await assistance.FrameAsync(owner, view.LeaseId, default);
            await assistance.FrameAsync(owner, view.LeaseId, default);
            Assert.AreEqual(1, browser.Captures);
            clock.Now += TimeSpan.FromMilliseconds(501);
            await assistance.FrameAsync(owner, view.LeaseId, default);
            Assert.AreEqual(2, browser.Captures);
            await assistance.InputAsync(owner, view.LeaseId, new("down", 20, 40), default);
            await assistance.FrameAsync(owner, view.LeaseId, default);
            Assert.AreEqual(3, browser.Captures);
        }

        [TestMethod]
        public async Task Input_RejectsCommandsKeysAndCoordinatesOutsideSupportedControls()
        {
            var browser = new Interaction();
            using var assistance = new BrowserAssistance(browser, new Clock());
            var owner = Guid.NewGuid();
            var view = await assistance.OpenAsync(owner, 7, Source, default);
            foreach (var input in new BrowserInputDataV1[] { new("Runtime.evaluate"), new("down", -1, 0), new("down", 800, 0),
                new("down", 0, 600), new("wheel", delta: int.MinValue), new("key", key: "F12"), new("text"), new("text", text: new string('x', 1001)) })
            {
                await Assert.ThrowsExactlyAsync<DataValidationException>(() => assistance.InputAsync(owner, view.LeaseId, input, default));
            }
            Assert.AreEqual(0, browser.Inputs);
        }

        [TestMethod]
        public async Task FrameFailure_PreservesOpeningSessionForRecovery()
        {
            var browser = new Interaction { FailNextCapture = true };
            using var assistance = new BrowserAssistance(browser, new Clock());
            var owner = Guid.NewGuid();
            var view = await assistance.OpenAsync(owner, 7, Source, default);
            await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => assistance.FrameAsync(owner, view.LeaseId, default));
            var retained = await assistance.ReadOwnAsync(owner, default);
            Assert.IsNotNull(retained);
            Assert.IsTrue(retained.IsActive);
            Assert.AreEqual(view.LeaseId, retained.LeaseId);
            CollectionAssert.AreEqual(Interaction.Picture, await assistance.FrameAsync(owner, view.LeaseId, default));
            await Assert.ThrowsExactlyAsync<SecurityException>(() => assistance.FrameAsync(Guid.NewGuid(), view.LeaseId, default));
            Assert.AreEqual(1, browser.Starts);
            Assert.AreEqual(0, browser.Inputs);
        }

        [TestMethod]
        public async Task ExpiredView_CanBeClosedOrReopenedOnlyByItsOwner()
        {
            var clock = new Clock();
            var browser = new Interaction();
            using var assistance = new BrowserAssistance(browser, clock);
            var owner = Guid.NewGuid();
            var view = await assistance.OpenAsync(owner, 7, Source, default);
            clock.Now += TimeSpan.FromMinutes(3);
            Assert.IsNull(await assistance.ReadOwnAsync(Guid.NewGuid(), default));
            await Assert.ThrowsExactlyAsync<SecurityException>(() => assistance.CloseAsync(Guid.NewGuid(), view.LeaseId, default));
            await assistance.CloseAsync(owner, view.LeaseId, default);
            Assert.IsNull(await assistance.ReadOwnAsync(owner, default));
            var next = await assistance.OpenAsync(owner, 7, Source, default);
            Assert.IsTrue(next.IsActive);
            await assistance.OpenAsync(owner, 8, new Uri("https://annas-archive.pk/"), default);
            await Assert.ThrowsExactlyAsync<ServiceGoneException>(() => assistance.FrameAsync(owner, next.LeaseId, default));
            Assert.AreEqual(8L, (await assistance.ReadOwnAsync(owner, default))!.AcquisitionConnectionId);
        }

        [TestMethod]
        public async Task FailedReconnect_KeepsTheOwnersSourceDescriptor()
        {
            var clock = new Clock();
            var browser = new Interaction();
            using var assistance = new BrowserAssistance(browser, clock);
            var owner = Guid.NewGuid();
            await assistance.OpenAsync(owner, 7, Source, default);
            clock.Now += TimeSpan.FromMinutes(3);
            browser.FailNextStart = true;
            await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => assistance.OpenAsync(owner, 7, Source, default));
            var retained = await assistance.ReadOwnAsync(owner, default);
            Assert.IsNotNull(retained);
            Assert.IsFalse(retained.IsActive);
            Assert.AreEqual(7L, retained.AcquisitionConnectionId);
            Assert.IsNull(await assistance.ReadOwnAsync(Guid.NewGuid(), default));
            Assert.IsTrue((await assistance.OpenAsync(owner, 7, Source, default)).IsActive);
        }

        // The click is answered at once with what the browser is doing; the view follows its state until the browser is ready.
        [TestMethod]
        public async Task SlowOpen_AnswersAtOnceWithItsStep_FollowsTheState_AndIsReadyWhenTheBrowserIs()
        {
            var browser = new Interaction { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var assistance = new BrowserAssistance(browser, new Clock());
            var owner = Guid.NewGuid();

            var started = System.Diagnostics.Stopwatch.StartNew();
            var view = await assistance.OpenAsync(owner, 7, Source, default);
            Assert.IsLessThan(2000, started.ElapsedMilliseconds);
            Assert.AreEqual("starting", view.Phase);
            Assert.IsFalse(view.IsActive);
            Assert.AreEqual("starting", (await assistance.StateAsync(owner, view.LeaseId, default)).Phase);
            Assert.AreEqual("starting", (await assistance.ReadOwnAsync(owner, default))!.Phase);

            // Nothing can be seen or typed yet, and a second click joins the opening instead of starting the browser again.
            await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => assistance.FrameAsync(owner, view.LeaseId, default));
            await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => assistance.InputAsync(owner, view.LeaseId, new("key", key: "Enter"), default));
            Assert.AreEqual(view.LeaseId, (await assistance.OpenAsync(owner, 7, Source, default)).LeaseId);
            Assert.AreEqual(1, browser.Starts);
            await Assert.ThrowsExactlyAsync<SecurityException>(() => assistance.StateAsync(Guid.NewGuid(), view.LeaseId, default));

            browser.Hold.SetResult(new BrowserViewport(1000, 700));
            BrowserViewV1 state;
            do
            {
                state = await assistance.StateAsync(owner, view.LeaseId, default);
                await Task.Delay(10);
            }
            while (state.Phase != "ready");
            Assert.IsTrue(state.IsActive);
            Assert.AreEqual((1000, 700), (state.Width, state.Height));
            CollectionAssert.AreEqual(Interaction.Picture, await assistance.FrameAsync(owner, view.LeaseId, default));
        }

        [TestMethod]
        public async Task SlowOpen_ThatFails_ShowsItsReason_AndCanBeOpenedAgain()
        {
            var browser = new Interaction { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var assistance = new BrowserAssistance(browser, new Clock());
            var owner = Guid.NewGuid();
            var view = await assistance.OpenAsync(owner, 7, Source, default);
            Assert.AreEqual("starting", view.Phase);

            browser.Hold.SetException(new SourceBrowserException("The browser did not open its debugging port."));
            BrowserViewV1 state;
            do
            {
                state = await assistance.StateAsync(owner, view.LeaseId, default);
                await Task.Delay(10);
            }
            while (state.Phase != "failed");
            Assert.AreEqual("no_start", state.Error);
            Assert.IsFalse(state.IsActive);
            await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => assistance.FrameAsync(owner, view.LeaseId, default));

            browser.Hold = null;
            var again = await assistance.OpenAsync(owner, 7, Source, default);
            Assert.AreEqual("ready", again.Phase);
            Assert.IsTrue(again.IsActive);
            Assert.AreEqual(2, browser.Starts);
        }

        [TestMethod]
        public async Task SlowOpen_ClosedWhileOpening_IsCancelled()
        {
            var browser = new Interaction { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var assistance = new BrowserAssistance(browser, new Clock());
            var owner = Guid.NewGuid();
            var view = await assistance.OpenAsync(owner, 7, Source, default);
            await assistance.CloseAsync(owner, view.LeaseId, default);
            Assert.AreEqual(1, browser.Ends);
            await Assert.ThrowsExactlyAsync<ServiceGoneException>(() => assistance.StateAsync(owner, view.LeaseId, default));
        }

        [TestMethod]
        [DataRow("Manual assistance requires a local browser and a public HTTPS source.", "unsupported")]
        [DataRow("Chrome is not installed.", "not_installed")]
        [DataRow("The browser did not open its debugging port.", "no_start")]
        [DataRow("The browser did not offer a page.", "no_start")]
        [DataRow("anything else", "failed")]
        public void FailureReasons_AreKeysThePageHasTextsFor(string message, string reason) =>
            Assert.AreEqual(reason, BrowserAssistance.ReasonOf(new SourceBrowserException(message)));

        [TestMethod]
        public void FailureReason_OfATimeout_IsTimeout() => Assert.AreEqual("timeout", BrowserAssistance.ReasonOf(new OperationCanceledException()));

        // Through the page and the API: the click returns with the step, the state endpoint follows it, the page says what it waits for.
        [TestMethod]
        public async Task Api_SlowOpen_ShowsTheStepOnThePage_AndTheStateEndpointFollowsIt()
        {
            using var storage = new TemporaryDirectory();
            var browser = new Interaction { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                services: services =>
                {
                    services.AddSingleton<IBrowserInteraction>(browser);
                    services.AddSingleton<ISourceBrowser>(browser);
                });
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('Acquisition', 'Browser');
                INSERT INTO "AcquisitionConnections" ("Name", "AcquisitionConnectionTypeId", "BaseUrl", "IsEnabled", "Priority")
                VALUES ('Anna', 5, 'https://annas-archive.gl', TRUE, 50);
                """);
            var id = Convert.ToInt64(await host.ScalarAsync("SELECT max(\"Id\") FROM \"AcquisitionConnections\""));
            using var opened = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/browser/assistance/" + id);
            Assert.AreEqual(HttpStatusCode.OK, opened.StatusCode, await opened.Content.ReadAsStringAsync());
            var view = (await opened.Content.ReadFromJsonAsync<BrowserViewV1>())!;
            Assert.AreEqual("starting", view.Phase);
            var lease = "/api/v1/admin/browser/assistance/" + view.LeaseId;

            using var state = await host.SendAsync(HttpMethod.Get, lease + "/state");
            Assert.AreEqual(HttpStatusCode.OK, state.StatusCode, await state.Content.ReadAsStringAsync());
            StringAssert.Contains(await state.Content.ReadAsStringAsync(), "\"phase\":\"starting\"");
            using var early = await host.SendAsync(HttpMethod.Get, lease + "/frame");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, early.StatusCode);

            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Browser");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "data-phase=\"starting\"");
            StringAssert.Contains(html, "Starting the browser");
            StringAssert.Contains(html, "data-browser-progress");
            Assert.DoesNotContain("data-browser-image=\"\" src", html);

            browser.Hold.SetException(new SourceBrowserException("Chrome is not installed."));
            string failed;
            do
            {
                await Task.Delay(20);
                using var again = await host.SendAsync(HttpMethod.Get, lease + "/state");
                failed = await again.Content.ReadAsStringAsync();
            }
            while (!failed.Contains("\"phase\":\"failed\"", StringComparison.Ordinal));
            StringAssert.Contains(failed, "\"error\":\"not_installed\"");
            using var failedPage = await host.SendAsync(HttpMethod.Get, "/Admin/Browser");
            var failedHtml = await failedPage.Content.ReadAsStringAsync();
            StringAssert.Contains(failedHtml, "The browser is not installed.");
            StringAssert.Contains(failedHtml, "Reconnect");
        }

        [TestMethod]
        public async Task Api_RequiresPermissionEnabledModuleSessionAndCsrf_FrameIsNeverCached()
        {
            using var storage = new TemporaryDirectory();
            var browser = new Interaction();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                services: services =>
                {
                    services.AddSingleton<IBrowserInteraction>(browser);
                    services.AddSingleton<ISourceBrowser>(browser);
                });
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('Acquisition', 'Browser');
                INSERT INTO "AcquisitionConnections" ("Name", "AcquisitionConnectionTypeId", "BaseUrl", "IsEnabled", "Priority")
                VALUES ('Anna', 5, 'https://annas-archive.gl', TRUE, 50);
                """);
            var id = Convert.ToInt64(await host.ScalarAsync("SELECT max(\"Id\") FROM \"AcquisitionConnections\""));
            var open = "/api/v1/admin/browser/assistance/" + id;
            using var opened = await host.SendAsync(HttpMethod.Post, open);
            Assert.AreEqual(HttpStatusCode.OK, opened.StatusCode, await opened.Content.ReadAsStringAsync());
            var view = (await opened.Content.ReadFromJsonAsync<BrowserViewV1>())!;
            var lease = "/api/v1/admin/browser/assistance/" + view.LeaseId;
            using var frame = await host.SendAsync(HttpMethod.Get, lease + "/frame");
            Assert.AreEqual(HttpStatusCode.OK, frame.StatusCode, await frame.Content.ReadAsStringAsync());
            Assert.AreEqual("image/jpeg", frame.Content.Headers.ContentType!.MediaType);
            Assert.IsTrue(frame.Headers.CacheControl!.NoStore && frame.Headers.CacheControl.Private);
            CollectionAssert.AreEqual(Interaction.Picture, await frame.Content.ReadAsByteArrayAsync());
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Browser");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "data-remote-browser");
            StringAssert.Contains(html, "Done and close");
            StringAssert.Contains(html, "Reconnect");

            // The browser's fixed notes read as texts of the page, not as the English the browser wrote them in.
            browser.Events = [new SourceBrowserEvent(DateTime.UtcNow, "refused", "the source is not a public HTTPS address")];
            using var withEvents = await host.SendAsync(HttpMethod.Get, "/Admin/Browser");
            var eventsHtml = await withEvents.Content.ReadAsStringAsync();
            StringAssert.Contains(eventsHtml, "The source is not a public HTTPS address");
            StringAssert.Contains(eventsHtml, ">Refused<");
            Assert.IsFalse(eventsHtml.Contains("the source is not a public HTTPS address", StringComparison.Ordinal));
            browser.Events = [];
            Assert.IsTrue(html.IndexOf("handler=OpenView", StringComparison.Ordinal) < html.IndexOf("What the browser did", StringComparison.Ordinal));
            using var openedFromPage = await host.SendAsync(HttpMethod.Post, "/Admin/Browser?handler=OpenView", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html), ["AcquisitionConnectionId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            Assert.AreEqual(HttpStatusCode.OK, openedFromPage.StatusCode, await openedFromPage.Content.ReadAsStringAsync());
            StringAssert.Contains(await openedFromPage.Content.ReadAsStringAsync(), "data-active=\"true\"");
            browser.HasManualControl = false;
            using var expiredPage = await host.SendAsync(HttpMethod.Get, "/Admin/Browser");
            StringAssert.Contains(await expiredPage.Content.ReadAsStringAsync(), "data-active=\"false\"");
            using var expiredInput = await host.SendAsync(HttpMethod.Post, lease + "/input", new BrowserInputDataV1("key", key: "Enter"));
            Assert.AreEqual(HttpStatusCode.Gone, expiredInput.StatusCode);
            using var reopened = await host.SendAsync(HttpMethod.Post, open);
            Assert.AreEqual(HttpStatusCode.OK, reopened.StatusCode);
            view = (await reopened.Content.ReadFromJsonAsync<BrowserViewV1>())!;
            lease = "/api/v1/admin/browser/assistance/" + view.LeaseId;
            var rendered = Path.Combine(TestContext.TestRunResultsDirectory!, "browser-assistance.html");
            await File.WriteAllTextAsync(rendered, html);
            TestContext.AddResultFile(rendered);
            using var noCsrf = new HttpRequestMessage(HttpMethod.Post, lease + "/input");
            noCsrf.Headers.Add("Cookie", string.Join("; ", host.Cookies.Select(pair => pair.Key + "=" + pair.Value)));
            noCsrf.Content = JsonContent.Create(new BrowserInputDataV1("key", key: "Enter"));
            using var refused = await host.Client.SendAsync(noCsrf);
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.AreEqual(0, browser.Inputs);
            using var typed = await host.SendAsync(HttpMethod.Post, lease + "/input", new BrowserInputDataV1("text", text: "Austen"));
            Assert.AreEqual(HttpStatusCode.OK, typed.StatusCode, await typed.Content.ReadAsStringAsync());
            Assert.AreEqual("Austen", browser.Text);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            await SignInAsync();
            using var denied = await host.SendAsync(HttpMethod.Get, lease + "/frame");
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using var deniedOpen = await host.SendAsync(HttpMethod.Post, open);
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedOpen.StatusCode);
            using var deniedDownload = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/999/browser");
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedDownload.StatusCode);
            await host.Fixture.RunSqlAsync("""
                UPDATE "Accounts" SET "AccountRoleTypeId" = 1 WHERE "Id" = 41;
                UPDATE "InstanceModules" SET "IsEnabled" = FALSE WHERE "Key" = 'Browser';
                """);
            await SignInAsync();
            using var disabled = await host.SendAsync(HttpMethod.Get, lease + "/frame");
            Assert.AreNotEqual(HttpStatusCode.OK, disabled.StatusCode);
            using var openDisabled = await host.SendAsync(HttpMethod.Post, open);
            Assert.AreNotEqual(HttpStatusCode.OK, openDisabled.StatusCode);
            using var downloadDisabled = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/999/browser");
            Assert.AreNotEqual(HttpStatusCode.OK, downloadDisabled.StatusCode);
            Assert.AreEqual(3, browser.Starts);

            async Task SignInAsync()
            {
                using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
                using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
                Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            }
        }

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        internal sealed class Interaction : IBrowserInteraction, ISourceBrowser
        {
            public static readonly byte[] Picture = [0xff, 0xd8, 0xff, 0xd9];
            public bool HasManualControl { get; set; }
            public bool IsAvailable => true;
            public string? LastOutcome { get; set; }
            public SourceBrowserStatus Status => new("fixture-browser", null, "fixture-profile", true, null, null, LastOutcome, null, null, 0);
            public IReadOnlyList<SourceBrowserEvent> Events { get; set; } = [];
            public int Starts { get; private set; }
            public int Ends { get; private set; }
            public int Inputs { get; private set; }
            public int Captures { get; private set; }
            public bool FailNextCapture { get; set; }
            public bool FailNextStart { get; set; }
            public TaskCompletionSource<BrowserViewport>? Hold { get; set; }
            public string? Text { get; private set; }
            public Uri? Address { get; private set; }
            public bool OpensPage { get; private set; }
            public Func<Uri, CancellationToken, Task<string?>>? ReadHtml { get; init; }
            public Task<BrowserViewport> BeginManualAsync(Uri address, DateTimeOffset expiresAt, CancellationToken ct, bool opensPage = false, Action<BrowserOpenPhase>? progress = null)
            {
                Starts++;
                if (Hold is { } hold)
                {
                    // A browser that is still starting: it tells its step and answers when the test lets it.
                    progress?.Invoke(BrowserOpenPhase.Checking);
                    progress?.Invoke(BrowserOpenPhase.Starting);
                    HasManualControl = true;
                    return hold.Task.WaitAsync(ct);
                }

                Address = address;
                OpensPage = opensPage;
                if (FailNextStart)
                {
                    FailNextStart = false;
                    HasManualControl = false;
                    throw new SourceBrowserException("Temporary open failure.");
                }
                HasManualControl = true;
                return Task.FromResult(new BrowserViewport(800, 600));
            }
            public Task<byte[]> CaptureManualAsync(DateTimeOffset expiresAt, CancellationToken ct)
            {
                Captures++;
                if (FailNextCapture)
                {
                    FailNextCapture = false;
                    throw new SourceBrowserException("Temporary frame failure.");
                }
                return Task.FromResult(Picture);
            }
            public Task InputManualAsync(string action, int x, int y, int delta, string? key, string? text, DateTimeOffset expiresAt, CancellationToken ct)
            {
                Inputs++;
                Text = text;
                return Task.CompletedTask;
            }
            public Task EndManualAsync(CancellationToken ct)
            {
                Ends++;
                HasManualControl = false;
                return Task.CompletedTask;
            }
            public Task<string?> ReadPageAsync(Uri uri, CancellationToken ct) =>
                HasManualControl || ReadHtml is null ? Task.FromResult<string?>(null) : ReadHtml(uri, ct);
            public async Task<BrowserPage?> ReadPagesAsync(IReadOnlyList<Uri> addresses, CancellationToken ct)
            {
                foreach (var address in addresses)
                {
                    if (await ReadPageAsync(address, ct) is { } html) return new BrowserPage(address, html);
                }
                return null;
            }
            public Task SaveFileAsync(Uri file, string destination, long? maximumBytes, Func<long, IDisposable>? reserveWrite, CancellationToken ct) => Task.CompletedTask;
            public Task<SourceBrowserStatus> StartAsync(CancellationToken ct) => Task.FromResult(Status);
            public Task StopAsync() => Task.CompletedTask;
            public Task ResetAsync() => Task.CompletedTask;
        }
    }
}
