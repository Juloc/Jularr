using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Logic.Playback;
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
                    *'"method":"thread/start"'*) printf '{"id":%s,"result":{"thread":{"id":"thread-1"}}}\n' "$id" ;;
                    *'"method":"turn/start"'*)
                      printf '{"id":%s,"result":{"turn":{"id":"turn-1"}}}\n' "$id"
                      model=$(printf '%s' "$line" | sed -n 's/.*"model":"\([^"]*\)".*/\1/p')
                      text=$(printf '%s' "$line" | sed -n 's/.*"text":"\(.*\)","text_elements".*/\1/p')
                      prompt=$(printf '%b' "$text")
                      echo "$model" >> "$CODEX_HOME/asked"
                      if [ "$model" = limited ]; then
                        printf '{"method":"error","params":{"threadId":"thread-1","turnId":"turn-1","willRetry":false,"error":{"message":"You hit your usage limit. Upgrade to Pro, or try again at Nov 4th, 2099 7:41 AM."}}}\n'
                        printf '{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"failed","error":{"message":"You hit your usage limit."}}}}\n'
                        continue
                      fi
                      if [ "$model" = broken ]; then
                        printf '{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"failed","error":{"message":"The model failed."}}}}\n'
                        continue
                      fi
                      printf '%s\n' "$prompt" | awk -v m="$model" '/^\[\[[0-9]+\]\]$/ { print; getline; print "Translated by " m ": " $0; print "" }' | while IFS= read -r out; do
                        printf '{"method":"item/agentMessage/delta","params":{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","delta":"%s\\n"}}\n' "$out"
                      done
                      printf '{"method":"thread/tokenUsage/updated","params":{"threadId":"thread-1","turnId":"turn-1","tokenUsage":{"last":{"inputTokens":100,"cachedInputTokens":0,"outputTokens":50,"reasoningOutputTokens":0,"totalTokens":150},"total":{"inputTokens":100,"cachedInputTokens":0,"outputTokens":50,"reasoningOutputTokens":0,"totalTokens":150}}}}\n'
                      printf '{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed"}}}\n' ;;
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
                if [ "$model" = limited ]; then
                  printf '{"type":"turn.started"}\n{"type":"error","message":"You hit your usage limit. Upgrade to Pro, or try again at %s."}\n{"type":"turn.failed","error":{"message":"You hit your usage limit."}}\n' "Nov 4th, 2099 7:41 AM"
                  exit 1
                fi
                case "$prompt" in
                  *'[[1]]'*) printf '%s\n' "$prompt" | awk -v m="$model" '/^\[\[[0-9]+\]\]$/ { print; getline; print "Translated by " m ": " $0; print "" }' > "$out" ;;
                  *'[1] '*) printf '%s\n' "$prompt" | grep '^\[[0-9]*\] ' | sed "s/^\(\[[0-9]*\]\) /\1 Translated by $model: /" > "$out" ;;
                  *) printf 'Translated by %s: %s' "$model" "$(printf '%s' "$prompt" | tail -n 1)" > "$out" ;;
                esac
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

        [TestMethod]
        public async Task Provider_TranslatesSeveralBlocksInOneRequest_AndAsksAgainForAMissingOne()
        {
            using var folder = new Folder();
            folder.SignIn();
            using var connection = folder.Connection();
            var provider = new CodexTranslationProvider(connection, TimeProvider.System);
            var machine = (await provider.ChooseAsync(new TranslationSetupV1([], null), "gpt-test", default))!;

            var answers = await provider.TranslateBlocksAsync(machine, "ja", "de", ["Rina went home", "The rain stopped", "Kai waited"], "Before", default);

            CollectionAssert.AreEqual(new[] { "Translated by gpt-test: Rina went home", "Translated by gpt-test: The rain stopped", "Translated by gpt-test: Kai waited" }, answers);
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(folder.Home, "asked")).Length, "one request for three blocks");
            Assert.AreEqual((150L, 150L), connection.Usage());

            // The answer's parts by their numbers: a missing or empty part is null, order and extra lines do not matter.
            CollectionAssert.AreEqual(new string?[] { "A", null, "C\nmore" }, CodexTranslationProvider.Numbered("[[1]]\nA\n\n[[3]]\nC\nmore\n[[2]]\n\n", 3));
            CollectionAssert.AreEqual(new string?[] { "A", "B" }, CodexTranslationProvider.Numbered("noise\n[[2]]\nB\n[[1]]\nA\n[[9]]\nignored", 2));
        }

        [TestMethod]
        public async Task Provider_StreamsBlocks_HandingEachOneOnWhenItIsComplete_CountsTheTokens_AndNamesAFailure()
        {
            using var folder = new Folder();
            folder.SignIn();
            using var connection = folder.Connection();
            var provider = new CodexTranslationProvider(connection, TimeProvider.System);
            var setup = new TranslationSetupV1([], null);
            var machine = (await provider.ChooseAsync(setup, "gpt-test", default))!;

            // One request for three blocks; each block is handed on as it is complete, in order, and the answers come back as well.
            var arrived = new List<(int Index, string Text)>();
            var answers = await provider.TranslateBlocksStreamAsync(machine, "ja", "de", ["Rina went home", "The rain stopped", "Kai waited"], null, (index, text) => arrived.Add((index, text)), default);
            CollectionAssert.AreEqual(new[] { "Translated by gpt-test: Rina went home", "Translated by gpt-test: The rain stopped", "Translated by gpt-test: Kai waited" }, answers);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, arrived.Select(item => item.Index).ToArray());
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(folder.Home, "asked")).Length);
            Assert.AreEqual((150L, 150L), connection.Usage());
            Assert.AreEqual("translate 3 blocks", connection.Requests().Single().What);

            // A failed turn is an exception the caller names; the plan's limit rests Codex as with exec.
            var broken = (await provider.ChooseAsync(setup, "broken", default))!;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provider.TranslateBlocksStreamAsync(broken, "ja", "de", ["Rina went home"], null, (_, _) => { }, default));
            Assert.IsNull(connection.PlanLimitedUntil);
            var limited = (await provider.ChooseAsync(setup, "limited", default))!;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provider.TranslateBlocksStreamAsync(limited, "ja", "de", ["Rina went home"], null, (_, _) => { }, default));
            Assert.IsNotNull(connection.PlanLimitedUntil);
        }

        [TestMethod]
        public void NumberedStream_CutsPartsAsTheAnswerComes_FirstTextOfANumberWins_AndIgnoresOtherNumbers()
        {
            var parts = new List<string>();
            var stream = new NumberedStream(2, (index, text) => parts.Add(index + "=" + text));
            foreach (var piece in new[] { "[[1", "]]\nA", "bc\n\n[[2]", "]\r\nB\nB2\n[[1]]\nagain\n[[9]]\nignored" }) stream.Add(piece);
            Assert.AreEqual(3, parts.Count, "A part is complete when the next marker comes, not when the stream ends.");
            stream.Complete();
            CollectionAssert.AreEqual(new[] { "0=Abc", "1=B\nB2", "0=again" }, parts, "A repeated number is reported again; the caller keeps the first.");
        }

        [TestMethod]
        public async Task Provider_TranslatesNumberedSubtitleCues_SoTheSubtitleParserAcceptsThem()
        {
            using var folder = new Folder();
            folder.SignIn();
            using var connection = folder.Connection();
            var provider = new CodexTranslationProvider(connection, TimeProvider.System);
            var machine = (await provider.ChooseAsync(new TranslationSetupV1([], null), "gpt-test", default))!;

            var answer = await provider.TranslateAsync(machine, "en", "de", "[1] Hello.\n[2] Bye.", default);

            CollectionAssert.AreEqual(new[] { "Translated by gpt-test: Hello.", "Translated by gpt-test: Bye." }, SubtitleTranslationLogic.ParseBatch(answer, 2));
        }

        [TestMethod]
        public async Task Provider_WhenThePlanLimitIsReached_RestsUntilCodexSaysAndNotesIt()
        {
            using var folder = new Folder();
            folder.SignIn();
            using var connection = folder.Connection();
            var provider = new CodexTranslationProvider(connection, TimeProvider.System);
            var setup = new TranslationSetupV1([], null);
            var machine = (await provider.ChooseAsync(setup, "limited", default))!;
            Assert.IsNull(connection.PlanLimitedUntil);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provider.TranslateBlockAsync(machine, "ja", "de", "Rina went home", null, default));

            // A date far ahead is capped at a day; until then nothing asks Codex again.
            var until = connection.PlanLimitedUntil;
            Assert.IsNotNull(until);
            Assert.IsTrue(until > DateTimeOffset.UtcNow.AddHours(23) && until <= DateTimeOffset.UtcNow.AddDays(1).AddMinutes(1), until.ToString());
            Assert.IsNull(await provider.ChooseAsync(setup, "gpt-test", default));
            Assert.AreEqual((byte)3, connection.Requests().Single().State);

            // Parsed from the CLI's own words: "try again at Nov 4th, 2026 7:41 AM."
            var parsed = CodexCli.PlanLimitOf("{\"type\":\"turn.failed\",\"error\":{\"message\":\"You've hit your usage limit. Upgrade to Pro. Try again at Nov 4th, 2026 7:41 AM.\"}}\n");
            Assert.IsNotNull(parsed);
            Assert.AreEqual(new DateTime(2026, 11, 4, 7, 41, 0, DateTimeKind.Local).ToUniversalTime(), parsed.Until!.Value.UtcDateTime);
            Assert.IsNull(CodexCli.PlanLimitOf("{\"type\":\"turn.failed\",\"error\":{\"message\":\"Something else.\"}}\n"));
        }

        [TestMethod]
        public async Task Activity_ListsTheRequests_AndTheTokensPerDayAreKeptAcrossARestart()
        {
            using var folder = new Folder();
            folder.SignIn();
            using (var connection = folder.Connection())
            {
                var provider = new CodexTranslationProvider(connection, TimeProvider.System);
                var machine = (await provider.ChooseAsync(new TranslationSetupV1([], null), "gpt-test", default))!;
                await provider.TranslateBlockAsync(machine, "ja", "de", "Rina went home", null, default);
                await provider.TranslateBlocksAsync(machine, "ja", "de", ["Rina went home", "Kai waited"], null, default);

                var requests = connection.Requests();
                Assert.AreEqual(2, requests.Length);
                Assert.AreEqual("translate 2 blocks", requests[0].What, "newest first");
                Assert.AreEqual("translate a block", requests[1].What);
                Assert.IsTrue(requests.All(request => request.State == 1 && request.Tokens == 150 && request.Model == "gpt-test"));
                Assert.AreEqual(300L, connection.History().Single().Tokens);
            }

            // The first run of a new Jularr: the earlier requests are gone, the days are not.
            using var restarted = folder.Connection();
            Assert.AreEqual(0, restarted.Requests().Length);
            Assert.AreEqual(300L, restarted.History().Single().Tokens);
        }

        [TestMethod]
        public async Task Runs_ARequestOfTheSameChapterSoonAfterwards_AddsToItsRow_AndAnotherModelGetsItsOwn()
        {
            using var folder = new Folder();
            folder.SignIn();
            using var connection = folder.Connection();
            using (connection.BeginRun(3, "Chapter", "gpt-test", "low")) await connection.ExecAsync("gpt-test", "low", "one", "translate", default);
            using (connection.BeginRun(3, "Chapter", "gpt-test", "low")) await connection.ExecAsync("gpt-test", "low", "two", "translate", default);
            using (connection.BeginRun(3, "Chapter", "gpt-ok", "low")) await connection.ExecAsync("gpt-ok", "low", "three", "translate", default);

            var runs = connection.Runs();
            Assert.AreEqual(2, runs.Length);
            Assert.AreEqual(2, runs.Single(run => run.Model == "gpt-test").Requests);
            Assert.AreEqual(300L, runs.Single(run => run.Model == "gpt-test").Tokens);
            Assert.AreEqual(1, runs.Single(run => run.Model == "gpt-ok").Requests);
            Assert.IsTrue(runs.All(run => run.State == 1));
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
