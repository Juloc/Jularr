using System.Net;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    // The quiet hint of an action: a script's request gets it with the page it swaps, a page without script shows it itself, once.
    [TestClass]
    public sealed class ActionHintTests
    {
        [TestMethod]
        public async Task Action_TellsItsResultAsHintToScriptAndPage()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);

            // A script's request (htmx) gets the hint in the header of the answering page and the page itself does not repeat it.
            var answer = await PostAndFollowAsync(host, htmx: true);
            var trigger = answer.Headers.GetValues("HX-Trigger").Single();
            StringAssert.Contains(trigger, "\"jularr:hint\"");
            StringAssert.Contains(trigger, "Added:");
            Assert.IsFalse((await answer.Content.ReadAsStringAsync()).Contains("action-toast--show", StringComparison.Ordinal));

            // Without script the page shows the hint; nothing of it is left for the next page.
            var plain = await PostAndFollowAsync(host, htmx: false);
            Assert.IsFalse(plain.Headers.Contains("HX-Trigger"));
            var html = await plain.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "action-toast action-toast--show");
            StringAssert.Contains(html, "Added:");
            using var next = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Sources");
            Assert.IsFalse((await next.Content.ReadAsStringAsync()).Contains("action-toast--show", StringComparison.Ordinal));
        }

        // Posts the form of the sources page and follows the redirect with or without the header of a script's request.
        private static async Task<HttpResponseMessage> PostAndFollowAsync(Host host, bool htmx)
        {
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Sources");
            var token = LibraryTests.Token(await page.Content.ReadAsStringAsync());
            using var posted = await host.SendAsync(HttpMethod.Post, "/Admin/Acquisition/Sources?handler=SetUp", new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
            Assert.AreEqual(HttpStatusCode.Redirect, posted.StatusCode, await posted.Content.ReadAsStringAsync());
            using var request = new HttpRequestMessage(HttpMethod.Get, posted.Headers.Location);
            request.Headers.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            if (htmx) request.Headers.Add("HX-Request", "true");
            var answer = await host.Client.SendAsync(request);

            // The answer takes the hint's cookie back, as a browser does.
            if (answer.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var cookie in cookies)
                {
                    var pair = cookie.Split(';')[0].Split('=', 2);
                    host.Cookies[pair[0]] = pair[1];
                }
            }

            return answer;
        }
    }
}
