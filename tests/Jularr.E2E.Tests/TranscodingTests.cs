using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Transcoding page: FFmpeg's encoders, a benchmark run on demand with a real FFmpeg, and the limits saved through the pill.
    [TestClass]
    public sealed class TranscodingTests
    {
        [TestMethod]
        [Timeout(600_000)]
        public async Task Benchmark_RunOnDemand_ProposesAndTheSettingsStay()
        {
            if (Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG") is not { } ffmpeg)
            {
                JularrInstance.Unavailable("JULARR_TEST_FFMPEG is not configured; the benchmark was not run.");
                return;
            }

            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Media:FfmpegPath"] = ffmpeg });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);

            // Software is always an option; nothing ran yet and nothing runs by itself.
            await page.GotoAsync("/Admin/Transcoding");
            await Assertions.Expect(page.Locator("tr", new() { HasText = "libx264" })).ToContainTextAsync("Yes");
            await Assertions.Expect(page.GetByText("Never", new() { Exact = true })).ToBeVisibleAsync();
            await Evidence.SaveAsync(page, "transcoding-before");

            // Run benchmark queues it in the background; the page reads itself again until the result is there.
            await Steps.SubmitAsync(page, Steps.FormWith(page, "Run benchmark"));
            await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "The benchmark is running." })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Proposed maximum", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 240_000 });
            await Assertions.Expect(page.Locator("tr", new() { HasText = "libx264" })).Not.ToContainTextAsync("Not run");
            await Evidence.SaveAsync(page, "transcoding-benchmark");

            // The limits are edited and saved with the pill; the ladder keeps its choice.
            var settings = page.Locator("form[data-save]").Filter(new() { Has = page.GetByLabel("Conversions at once") });
            await settings.GetByLabel("Conversions at once").FillAsync("3");
            await settings.GetByLabel("480p").UncheckAsync();
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(settings.GetByLabel("Conversions at once")).ToHaveValueAsync("3");
            await Assertions.Expect(settings.GetByLabel("480p")).Not.ToBeCheckedAsync();
            await Assertions.Expect(settings.GetByLabel("720p")).ToBeCheckedAsync();

            // The Owner starts with priority; it is edited on the account like its role.
            await page.GotoAsync("/Admin/Accounts");
            await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).First);
            var priority = page.Locator("fieldset", new() { HasText = "Transcoding priority" });
            await Assertions.Expect(priority.GetByRole(AriaRole.Radio, new() { Name = "Yes" })).ToBeCheckedAsync();
            await Evidence.SaveAsync(page, "account-transcoding-priority");
            CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
        }
    }
}
