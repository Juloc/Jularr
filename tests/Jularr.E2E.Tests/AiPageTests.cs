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
