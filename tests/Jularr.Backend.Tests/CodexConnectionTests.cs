using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Ai.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Codex;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class CodexConnectionTests
    {
        // A stand-in for the CLI: it signs in (writes `auth`) once the test creates `approve`; the address and code come as the real CLI prints them.
        private const string Cli = """
            #!/bin/sh
            case "$1" in
              --version) echo "codex-cli 0.166.0" ;;
              logout) rm -f "$CODEX_HOME/auth" ;;
              login)
                case "$2" in
                  status) if [ -f "$CODEX_HOME/auth" ]; then echo "Logged in using ChatGPT"; else echo "Not logged in"; exit 1; fi ;;
                  --device-auth)
                    printf '1. Open this link\n   \033[94m%s\033[0m\n\n2. Enter this one-time code\n   \033[94mABCD-EFGH5\033[0m\n' "$CODEX_URL"
                    for i in $(seq 1 100); do
                      if [ -f "$CODEX_HOME/approve" ]; then touch "$CODEX_HOME/auth"; exit 0; fi
                      if [ -f "$CODEX_HOME/fail" ]; then exit 1; fi
                      sleep 0.1
                    done
                    exit 1 ;;
                esac ;;
            esac
            """;

        [TestMethod]
        public async Task Connection_WithoutTheCli_IsNotInstalled_AndRefusesToStartALogin()
        {
            using var connection = new CodexConnection(new CodexCli { ExecutablePath = null, HomePath = Path.Combine(Path.GetTempPath(), "jularr-codex-none") });

            Assert.AreEqual(CodexPhase.NotInstalled, (await connection.ReadAsync(default)).Phase);
            var error = await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => connection.StartLoginAsync(default));
            Assert.AreEqual("codex_not_installed", error.Code);
        }

        [TestMethod]
        public async Task Login_ShowsTheCode_RefusesASecondOne_AndEndsWhenThePersonSignedIn()
        {
            using var folder = new Folder();
            using var connection = folder.Connection();
            Assert.AreEqual(CodexPhase.NotConnected, (await connection.ReadAsync(default)).Phase);

            var pending = await connection.StartLoginAsync(default);
            Assert.AreEqual(CodexPhase.LoginPending, pending.Phase);
            Assert.AreEqual("https://auth.openai.com/codex/device", pending.VerificationUrl);
            Assert.AreEqual("ABCD-EFGH5", pending.UserCode);

            var busy = await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => connection.StartLoginAsync(default));
            Assert.AreEqual("codex_login_busy", busy.Code);
            Assert.AreEqual("ABCD-EFGH5", (await connection.ReadAsync(default)).UserCode);

            File.WriteAllText(Path.Combine(folder.Home, "approve"), "");
            var connected = await folder.UntilAsync(connection, CodexPhase.Connected);
            Assert.AreEqual("chatgpt", connected.SignIn);
            Assert.AreEqual("0.166.0", connected.Version);

            await connection.LogoutAsync(default);
            Assert.AreEqual(CodexPhase.NotConnected, (await connection.ReadAsync(default)).Phase);
        }

        [TestMethod]
        public async Task Login_Cancelled_OrFailed_ReturnsToNotConnected_AndMayStartAgain()
        {
            using var folder = new Folder();
            using var connection = folder.Connection();

            await connection.StartLoginAsync(default);
            connection.CancelLogin();
            var cancelled = await connection.ReadAsync(default);
            Assert.AreEqual(CodexPhase.NotConnected, cancelled.Phase);
            Assert.IsNull(cancelled.ErrorKey);

            // The CLI ends without a sign-in: the reason shows once, as a fixed key and never the CLI's own text.
            await connection.StartLoginAsync(default);
            File.WriteAllText(Path.Combine(folder.Home, "fail"), "");
            var failed = await folder.UntilAsync(connection, CodexPhase.NotConnected);
            Assert.AreEqual("codex_login_expired", failed.ErrorKey);
        }

        [TestMethod]
        public async Task Login_AddressOutsideOpenAi_IsNeverShown()
        {
            using var folder = new Folder("https://evil.example.test/device");
            using var connection = folder.Connection();
            File.WriteAllText(Path.Combine(folder.Home, "fail"), "");

            var error = await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => connection.StartLoginAsync(default));

            Assert.AreEqual("codex_login_failed", error.Code);
            Assert.AreEqual("codex_login_failed", (await connection.ReadAsync(default)).ErrorKey);
        }

        [TestMethod]
        public async Task Services_AdminStartsAndCancelsALogin_AndASecondStartIsRefused()
        {
            using var folder = new Folder();
            await using var host = await Host.CreateAsync(library: true, settings: new Dictionary<string, string>
            {
                ["Ai:CodexPath"] = folder.Executable, ["Ai:CodexHome"] = folder.Home
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);

            using var idle = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/ai/codex");
            Assert.AreEqual((int)CodexPhase.NotConnected, (await idle.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("phase").GetInt32());

            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/ai/codex/login");
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            Assert.AreEqual("ABCD-EFGH5", (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userCode").GetString());

            using var second = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/ai/codex/login");
            Assert.AreNotEqual(HttpStatusCode.OK, second.StatusCode);
            StringAssert.Contains(await second.Content.ReadAsStringAsync(), "codex_login_busy");

            using var cancelled = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/ai/codex/login");
            Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode, await cancelled.Content.ReadAsStringAsync());
            using var after = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/ai/codex");
            Assert.AreEqual((int)CodexPhase.NotConnected, (await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("phase").GetInt32());
        }

        // The stand-in CLI in a folder of its own, with its own CODEX_HOME.
        private sealed class Folder : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "jularr-codex-" + Guid.NewGuid().ToString("N"));

            public Folder(string url = "https://auth.openai.com/codex/device")
            {
                Directory.CreateDirectory(Home);
                Executable = Path.Combine(_root, "codex");
                File.WriteAllText(Executable, Cli.Replace("$CODEX_URL", url).Replace("\r\n", "\n"));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            public string Executable { get; }
            public string Home => Path.Combine(_root, "home");

            public CodexConnection Connection() => new(new CodexCli { ExecutablePath = Executable, HomePath = Home });

            public async Task<CodexConnectionV1> UntilAsync(CodexConnection connection, CodexPhase phase)
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    var state = await connection.ReadAsync(default);
                    if (state.Phase == phase) return state;
                    await Task.Delay(100);
                }

                Assert.Fail("The connection did not reach " + phase + ".");
                return null!;
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(_root, true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
