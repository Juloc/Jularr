using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Codex connection card: Connect shows the address and code, the card turns Connected by itself, Disconnect asks first.
    [TestClass]
    public sealed class AiPageTests
    {
        // A stand-in for the CLI: it signs in once the test creates `approve`.
        private const string Cli = """
            #!/bin/sh
            case "$1" in
              --version) echo "codex-cli 0.166.0" ;;
              logout) rm -f "$CODEX_HOME/auth" ;;
              app-server)
                while IFS= read -r line; do
                  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
                  case "$line" in
                    *'"method":"initialize"'*) printf '{"id":%s,"result":{"userAgent":"codex"}}\n' "$id" ;;
                    *'"method":"model/list"'*) printf '{"id":%s,"result":{"data":[{"model":"gpt-one","displayName":"GPT One","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"medium"},{"reasoningEffort":"high"}],"isDefault":true},{"model":"gpt-two","displayName":"GPT Two","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"high"}]}],"nextCursor":null}}\n' "$id" ;;
                    *'"method":"account/rateLimits/read"'*) printf '{"id":%s,"result":{"rateLimits":{"limitId":"codex","planType":"plus","primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":1900000000},"secondary":{"usedPercent":8,"windowDurationMins":10080,"resetsAt":1900300000}}}}\n' "$id" ;;
                  esac
                done ;;
              login)
                case "$2" in
                  status) if [ -f "$CODEX_HOME/auth" ]; then echo "Logged in using ChatGPT"; else echo "Not logged in"; exit 1; fi ;;
                  --device-auth)
                    printf '1. Open this link\n   https://auth.openai.com/codex/device\n\n2. Enter this one-time code\n   ABCD-EFGH5\n'
                    for i in $(seq 1 600); do
                      if [ -f "$CODEX_HOME/approve" ]; then touch "$CODEX_HOME/auth"; exit 0; fi
                      sleep 0.1
                    done
                    exit 1 ;;
                esac ;;
            esac
            """;

        [TestMethod]
        [Timeout(300_000)]
        public async Task Codex_ConnectsByItself_AndDisconnectsAfterAsking()
        {
            if (OperatingSystem.IsWindows()) Assert.Inconclusive("The stand-in CLI is a shell script.");
            var folder = Path.Combine(Path.GetTempPath(), "jularr-codex-e2e-" + Guid.NewGuid().ToString("N"));
            var home = Path.Combine(folder, "home");
            Directory.CreateDirectory(home);
            var executable = Path.Combine(folder, "codex");
            File.WriteAllText(executable, Cli.Replace("\r\n", "\n"));
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Ai:CodexPath"] = executable, ["Ai:CodexHome"] = home });
                await using var browser = await BrowserSession.StartAsync(jularr);
                var page = browser.Page;
                await Owner.SetUpAndSignInAsync(page);

                await page.GotoAsync("/Admin/Ai");
                var card = page.Locator("#codex-card");
                await Assertions.Expect(card.Locator(".state-chip")).ToHaveTextAsync("Not connected");

                // Connect: the card shows the address and the one-time code with Cancel.
                await card.GetByRole(AriaRole.Button, new() { Name = "Connect" }).ClickAsync();
                await Assertions.Expect(card.Locator(".state-chip")).ToHaveTextAsync("Waiting for sign-in");
                await Assertions.Expect(card.GetByText("ABCD-EFGH5")).ToBeVisibleAsync();
                await Assertions.Expect(card.GetByRole(AriaRole.Link, new() { Name = "Open sign-in page" })).ToBeVisibleAsync();
                await Assertions.Expect(card.GetByRole(AriaRole.Button, new() { Name = "Cancel login" })).ToBeVisibleAsync();

                // The person signs in elsewhere: the card turns Connected without a reload by hand.
                File.WriteAllText(Path.Combine(home, "approve"), "");
                await Assertions.Expect(card.Locator(".state-chip")).ToHaveTextAsync("Connected", new() { Timeout = 15_000 });
                await Assertions.Expect(card.GetByText("ChatGPT account", new() { Exact = true })).ToBeVisibleAsync();

                // Connected: the plan usage and the Owner's choices follow, and a saved choice is still there after a reload.
                await Assertions.Expect(page.Locator("#codex-usage")).ToContainTextAsync("42 %");
                await Assertions.Expect(page.Locator("#codex-usage")).ToContainTextAsync("Weekly window");
                var settings = page.Locator("#codex-settings");
                await settings.Locator("select[name=Model]").SelectOptionAsync("gpt-two");
                await settings.Locator("select[name=Effort]").SelectOptionAsync("high");
                await settings.Locator("select[name=FallbackModel]").SelectOptionAsync("gpt-one");
                await settings.Locator("input[name=DailyTokens]").FillAsync("500000");
                await settings.Locator("input[name=RunTokens]").FillAsync("100000");
                var response = page.WaitForResponseAsync(reply => reply.Url.Contains("handler=Settings", StringComparison.Ordinal) && reply.Request.Method == "POST");
                await page.Locator("[data-save-pill]").GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
                await response;
                await page.ReloadAsync();
                await Assertions.Expect(page.Locator("#codex-settings select[name=Model]")).ToHaveValueAsync("gpt-two");
                await Assertions.Expect(page.Locator("#codex-settings input[name=DailyTokens]")).ToHaveValueAsync("500000");

                // A run limit above the daily one is refused and says why.
                await settings.Locator("input[name=RunTokens]").FillAsync("900000");
                await page.Locator("[data-save-pill]").GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("#codex-settings")).ToContainTextAsync("cannot be higher than the daily limit");

                // Disconnect asks first; Cancel keeps the connection, confirming removes it.
                await card.GetByRole(AriaRole.Button, new() { Name = "Disconnect" }).ClickAsync();
                var dialog = page.Locator("dialog#codex-disconnect");
                await Assertions.Expect(dialog).ToBeVisibleAsync();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Assertions.Expect(card.Locator(".state-chip")).ToHaveTextAsync("Connected");
                await card.GetByRole(AriaRole.Button, new() { Name = "Disconnect" }).ClickAsync();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Disconnect" }).ClickAsync();
                await Assertions.Expect(page.Locator("#codex-card .state-chip")).ToHaveTextAsync("Not connected");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
