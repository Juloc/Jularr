using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Codex;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class CodexTranslationTests
    {
        // A stand-in for the CLI: `exec` writes "Translated by <model>: <last prompt line>" to the -o file and reports 150 tokens; the model "broken" fails.
        private const string Cli = """
            #!/bin/sh
            case "$1" in
              --version) echo "codex-cli 0.166.0" ;;
              login) if [ -f "$CODEX_HOME/auth" ]; then echo "Logged in using ChatGPT"; else echo "Not logged in"; exit 1; fi ;;
              app-server)
                while IFS= read -r line; do
                  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
                  case "$line" in
                    *'"method":"initialize"'*) printf '{"id":%s,"result":{}}\n' "$id" ;;
                    *'"method":"model/list"'*) printf '{"id":%s,"result":{"data":[{"model":"gpt-test"},{"model":"gpt-ok"}],"nextCursor":null}}\n' "$id" ;;
                    *'"method":"account/rateLimits/read"'*) printf '{"id":%s,"result":{}}\n' "$id" ;;
                  esac
                done ;;
              exec)
                prompt=$(cat)
                effort=""
                while [ $# -gt 0 ]; do
                  case "$1" in
                    -o) out="$2"; shift ;;
                    -m) model="$2"; shift ;;
                    -c) effort="$2"; shift ;;
                  esac
                  shift
                done
                echo "$model $effort" >> "$CODEX_HOME/asked"
                if [ "$model" = broken ]; then exit 1; fi
                printf 'Translated by %s: %s' "$model" "$(printf '%s' "$prompt" | tail -n 1)" > "$out"
                printf '{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":5,"output_tokens":50}}\n' ;;
            esac
            """;

        [TestMethod]
        public async Task Provider_TranslatesOnlyWhenAskedByName_AndOnlyWhileConnected()
        {
            using var folder = new Folder();
            var provider = new CodexTranslationProvider(folder.Connection(), TimeProvider.System);
            var setup = new TranslationSetupV1([], null);

            Assert.IsNull(await provider.ChooseAsync(setup, "gpt-test", default), "not signed in");
            folder.SignIn();
            using var connection = folder.Connection();
            provider = new CodexTranslationProvider(connection, TimeProvider.System);
            Assert.IsNull(await provider.ChooseAsync(setup, null, default), "never the automatic choice");
            var machine = await provider.ChooseAsync(setup, "gpt-test", default);
            Assert.AreEqual("codex:gpt-test", machine?.ModelId);
            Assert.AreEqual("codex-gpt-test", await provider.DigestAsync(machine!, default));
        }

        [TestMethod]
        public async Task Provider_TranslatesABlockWithItsContext_CountsTheTokens_AndKeepsTheEffort()
        {
            using var folder = new Folder();
            folder.SignIn();
            using var connection = folder.Connection();
            var provider = new CodexTranslationProvider(connection, TimeProvider.System);
            var machine = (await provider.ChooseAsync(new TranslationSetupV1([], null, "high", null, 0, 0), "gpt-test", default))!;

            var text = await provider.TranslateBlockAsync(machine, "ja", "de", "Rina went home", "Earlier text", default);

            Assert.AreEqual("Translated by gpt-test: Rina went home", text);
            Assert.AreEqual((150L, 150L), connection.Usage());
            StringAssert.Contains(File.ReadAllText(Path.Combine(folder.Home, "asked")), "gpt-test model_reasoning_effort=\"high\"");
        }

        [TestMethod]
        public async Task Provider_WhenTheModelFails_TheFallbackModelAnswers()
        {
            using var folder = new Folder();
            folder.SignIn();
            using var connection = folder.Connection();
            var provider = new CodexTranslationProvider(connection, TimeProvider.System);
            var machine = (await provider.ChooseAsync(new TranslationSetupV1([], null, null, "gpt-ok", 0, 0), "broken", default))!;

            Assert.AreEqual("Translated by gpt-ok: Rina went home", await provider.TranslateBlockAsync(machine, "ja", "de", "Rina went home", null, default));
            CollectionAssert.AreEqual(new[] { "broken ", "gpt-ok " }, File.ReadAllLines(Path.Combine(folder.Home, "asked")));

            // Without a fallback the failure goes to the caller, which lets the machine rest.
            var alone = (await provider.ChooseAsync(new TranslationSetupV1([], null), "broken", default))!;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provider.TranslateBlockAsync(alone, "ja", "de", "Rina went home", null, default));
            provider.Fail(alone);
            Assert.IsNull(await provider.ChooseAsync(new TranslationSetupV1([], null), "gpt-test", default), "a failed machine rests");
        }

        [TestMethod]
        public async Task Provider_StopsAtTheDailyLimit_AndTheDayCountSurvivesARestart()
        {
            using var folder = new Folder();
            folder.SignIn();
            using (var connection = folder.Connection())
            {
                var provider = new CodexTranslationProvider(connection, TimeProvider.System);
                var setup = new TranslationSetupV1([], null, null, null, 200, 0);
                var machine = (await provider.ChooseAsync(setup, "gpt-test", default))!;

                await provider.TranslateBlockAsync(machine, "ja", "de", "Rina went home", null, default);
                Assert.IsNotNull(await provider.ChooseAsync(setup, "gpt-test", default), "150 of 200 tokens used");
                await provider.TranslateBlockAsync(machine, "ja", "de", "Rina went home", null, default);

                Assert.IsNull(await provider.ChooseAsync(setup, "gpt-test", default), "300 of 200 tokens used");
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provider.TranslateBlockAsync(machine, "ja", "de", "Rina went home", null, default));
            }

            // A new Jularr run: the day's count is read from the file, the count since start begins at 0.
            using var restarted = folder.Connection();
            Assert.AreEqual((300L, 0L), restarted.Usage());
            Assert.IsTrue(restarted.LimitReached(200, 0));
            Assert.IsFalse(restarted.LimitReached(0, 0), "0 is unlimited");
        }

        [TestMethod]
        public async Task Provider_KnowsTheModelsTheAccountOffered_NotBefore()
        {
            using var folder = new Folder();
            folder.SignIn();
            using var connection = folder.Connection();
            var provider = new CodexTranslationProvider(connection, TimeProvider.System);
            Assert.AreEqual(0, connection.KnownModels.Length);

            await connection.ReadCatalogAsync(default);

            CollectionAssert.AreEqual(new[] { "gpt-test", "gpt-ok" }, provider.Models.ToArray());
            Assert.IsNotNull(new TranslationProviders([provider]).Find("codex:gpt-ok"));
            Assert.IsNull(new TranslationProviders([provider]).Find("codex:made-up"));
        }

        internal sealed class Folder : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "jularr-codex-tr-" + Guid.NewGuid().ToString("N"));

            public Folder()
            {
                Directory.CreateDirectory(Home);
                Executable = Path.Combine(_root, "codex");
                File.WriteAllText(Executable, Cli.Replace("\r\n", "\n"));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            public string Executable { get; }
            public string Home => Path.Combine(_root, "home");

            public void SignIn() => File.WriteAllText(Path.Combine(Home, "auth"), "");

            public CodexConnection Connection() => new(new CodexCli { ExecutablePath = Executable, HomePath = Home });

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
