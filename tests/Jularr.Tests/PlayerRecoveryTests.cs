using Jint;

namespace Jularr.Tests;

/// <summary>
/// The player's rule for a stream the server ended, run under Jint: only a server-ended or lost stream is planned again
/// with the same mode, never more than a few times, never after an encoder crash, and the budget only comes back after
/// real playback. The player calls this rule before it blames the mode.
/// </summary>
[TestClass]
public sealed class PlayerRecoveryTests
{
    [TestMethod]
    [DataRow("{ state: 'ended', reason: 'idle', recoverable: true }", 0, true)]
    [DataRow("{ state: 'ended', reason: 'cache_budget', recoverable: true }", 2, true)]
    [DataRow("{ state: 'ended', reason: null, recoverable: false }", 0, false)]
    [DataRow("{ gone: true }", 1, true)]
    [DataRow("{ state: 'ended', reason: 'encoder_exited', recoverable: false }", 0, false)]
    [DataRow("{ state: 'active', reason: null, recoverable: false }", 0, false)]
    [DataRow("null", 0, false)]
    [DataRow("{ state: 'ended', reason: 'idle', recoverable: true }", 3, false)]
    [DataRow("{ gone: true }", 3, false)]
    public void OnlyAServerEndedStreamWithinTheBudgetIsPlannedAgainWithTheSameMode(string status, int recoveries, bool expected)
    {
        Assert.AreEqual(expected, Run($"return window.JularrStreamRecovery.shouldReplanSameMode({status}, {recoveries});"));
    }

    [TestMethod]
    public void TheBudgetOnlyReturnsAfterThirtySecondsOfRealPlayback()
    {
        Assert.AreEqual(1.5, RunNumber("return window.JularrStreamRecovery.accumulatePlayed(1, 10, 10.5);"), "A normal timeupdate step counts as played.");
        Assert.AreEqual(1.0, RunNumber("return window.JularrStreamRecovery.accumulatePlayed(1, 10, 400);"), "A forward seek is not playback.");
        Assert.AreEqual(1.0, RunNumber("return window.JularrStreamRecovery.accumulatePlayed(1, 400, 10);"), "Neither is a step backwards.");
        Assert.AreEqual(2.0, RunNumber("return window.JularrStreamRecovery.recoveriesAfterProgress(2, 29.9);"), "A stream that dies seconds after every restart keeps its count.");
        Assert.AreEqual(0.0, RunNumber("return window.JularrStreamRecovery.recoveriesAfterProgress(2, 30);"));
        Assert.AreEqual(0.0, RunNumber("return window.JularrStreamRecovery.recoveriesAfterProgress(0, 500);"));
        Assert.AreEqual(3.0, RunNumber("return window.JularrStreamRecovery.recoveriesAfterProgress(3, -10);"), "Seeking backwards is not progress.");
    }

    [TestMethod]
    public void AStreamThatKeepsDyingEndsInTheOrdinaryFallbackAfterThreeRecoveries()
    {
        const string script = """
            const recovery = window.JularrStreamRecovery;
            let recoveries = 0;
            let replans = 0;
            for (let attempt = 0; attempt < 20; attempt++) {
                if (recovery.shouldReplanSameMode({ state: 'ended', reason: 'cache_budget', recoverable: true }, recoveries)) {
                    recoveries += 1;
                    replans += 1;
                    recoveries = recovery.recoveriesAfterProgress(recoveries, 5);
                }
            }
            return replans;
            """;

        Assert.AreEqual(3.0, RunNumber(script));
    }

    // The gate runs on the numbers of the server's one adaptation policy, exactly as the page hands them to the player.
    private const string GateSetup = "const g = window.JularrStreamRecovery.createAdviceGate(gateOptions); " +
                                     "const follow = (advice, state, nowMs = 1000) => window.JularrStreamRecovery.followAdvice(g, advice, state, nowMs); ";

    [TestMethod]
    public void OnlyAStepAdviceOfTheServerIsFollowedAndNeverWhilePausedReplanningOrHandedOver()
    {
        Assert.IsTrue(Run(GateSetup + "return follow('step_down', { paused: false, busy: false });"));
        Assert.IsTrue(Run(GateSetup + "return follow('step_up', { paused: false, busy: false });"));
        foreach (var advice in new[] { "'none'", "undefined", "null", "''", "'STEP_DOWN'", "'step_sideways'", "1" })
        {
            Assert.IsFalse(Run(GateSetup + $"return follow({advice}, {{ paused: false, busy: false }});"), advice);
        }

        Assert.IsFalse(Run(GateSetup + "return follow('step_down', { paused: true, busy: false });"), "A paused player is not re-planned.");
        Assert.IsFalse(Run(GateSetup + "return follow('step_down', { paused: false, busy: true });"), "A plan is already being replaced.");
        Assert.IsFalse(Run(GateSetup + "return follow('step_down', { paused: false, busy: false, handedOver: true });"), "Native fullscreen or picture-in-picture keeps its source.");
        const string numberless = "const g = window.JularrStreamRecovery.createAdviceGate({}); ";
        Assert.IsFalse(Run(numberless + "return window.JularrStreamRecovery.followAdvice(g, 'step_down', { paused: false, busy: false }, 1000);"), "A gate without the server's numbers follows nothing.");
    }

    [TestMethod]
    public void FollowingAdviceIsBoundedByAnIntervalAndAWindowAndADeniedAnswerCostsNothing()
    {
        const string spacing = """
            const recovery = window.JularrStreamRecovery;
            const gate = recovery.createAdviceGate(gateOptions);
            const idle = { paused: false, busy: false };
            const results = [];
            results.push(recovery.followAdvice(gate, 'step_down', idle, 0));
            results.push(recovery.followAdvice(gate, 'step_down', idle, gateOptions.minIntervalMs - 1));
            results.push(recovery.followAdvice(gate, 'step_down', idle, gateOptions.minIntervalMs - 1));
            results.push(recovery.followAdvice(gate, 'step_up', idle, gateOptions.minIntervalMs));
            return results.join(',');
            """;
        Assert.AreEqual("true,false,false,true", Evaluate(spacing).AsString(), "The interval counts from the last followed advice; a refused answer does not extend it.");

        const string flood = """
            const recovery = window.JularrStreamRecovery;
            const gate = recovery.createAdviceGate(gateOptions);
            const idle = { paused: false, busy: false };
            let followed = 0;
            for (let step = 0; step < 100; step++) {
                if (recovery.followAdvice(gate, 'step_down', idle, step * gateOptions.minIntervalMs)) {
                    followed += 1;
                }
            }
            return followed;
            """;
        Assert.AreEqual(30.0, RunNumber(flood), "At most 6 per 10 minutes: an advice every 30 s for 50 minutes is followed 6 times in each of the five windows.");

        const string reopening = """
            const recovery = window.JularrStreamRecovery;
            const gate = recovery.createAdviceGate(gateOptions);
            const idle = { paused: false, busy: false };
            for (let step = 0; step < gateOptions.maxSwitches; step++) {
                recovery.followAdvice(gate, 'step_up', idle, step * gateOptions.minIntervalMs);
            }
            const denied = recovery.followAdvice(gate, 'step_up', idle, gateOptions.maxSwitches * gateOptions.minIntervalMs);
            const later = recovery.followAdvice(gate, 'step_up', idle, gateOptions.windowMs + 1);
            return `${denied},${later}`;
            """;
        Assert.AreEqual("false,true", Evaluate(reopening).AsString(), "The sixth switch inside 10 minutes is the last; the window then opens again.");
    }

    [TestMethod]
    public void ABackoffAfterAFailedPlanLeavesAdviceAloneForAWhile()
    {
        const string script = """
            const recovery = window.JularrStreamRecovery;
            const gate = recovery.createAdviceGate(gateOptions);
            const idle = { paused: false, busy: false, handedOver: false };
            gate.backOff(1000);
            const during = recovery.followAdvice(gate, 'step_down', idle, 1000 + gateOptions.backoffMs - 1);
            const after = recovery.followAdvice(gate, 'step_down', idle, 1000 + gateOptions.backoffMs);
            return `${during},${after}`;
            """;
        Assert.AreEqual("false,true", Evaluate(script).AsString(), "After a plan that failed or was not playable advice is left alone for a while.");
    }

    [TestMethod]
    public void ThePageGivesThePlayerTheServersGateNumbersAndNothingElse()
    {
        using var gate = System.Text.Json.JsonDocument.Parse(Jularr.Web.Features.Playback.Decision.PlaybackAdaptationPolicy.Default.ClientGateJson());

        Assert.AreEqual(30_000, gate.RootElement.GetProperty("minIntervalMs").GetDouble());
        Assert.AreEqual(6, gate.RootElement.GetProperty("maxSwitches").GetInt32());
        Assert.AreEqual(600_000, gate.RootElement.GetProperty("windowMs").GetDouble());
        Assert.AreEqual(300_000, gate.RootElement.GetProperty("backoffMs").GetDouble());
        var page = File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "Pages", "Library", "_VideoPlayerStage.cshtml"));
        StringAssert.Contains(page, "data-advice-gate=\"@PlaybackAdaptationPolicy.Default.ClientGateJson()\"");
    }

    [TestMethod]
    public void AFixedTierTheServersEncoderCouldNotSustainShowsAHintWhileAutomaticDoesNot()
    {
        const string script = """
            const hint = window.JularrStreamRecovery.speedLimitedHint;
            return JSON.stringify([
                hint({ requested: 'mbps8', limitSource: 'transcode_speed', limitKbps: 4000 }),
                hint({ requested: 'auto', limitSource: 'transcode_speed', limitKbps: 4000 }),
                hint({ requested: 'mbps8', limitSource: 'preset', limitKbps: 8000 }),
                hint({ requested: 'original', limitSource: 'transcode_speed', limitKbps: 0 }),
                hint(null),
                hint(undefined)
            ]);
            """;

        Assert.AreEqual("""[{"reason":"transcode_too_slow","limitKbps":4000},null,null,null,null,null]""", Evaluate(script).AsString());
    }

    [TestMethod]
    public void TheAdvisedPlanOnlyReplacesTheStreamOnceItIsConfirmedPlayable()
    {
        const string swapped = """
            const log = [];
            const outcome = await window.JularrStreamRecovery.followAdvisedPlan({
                advice: 'step_up',
                requestPlan: async advice => { log.push('request:' + advice); return { sessionId: 'new', plan: { mode: 'transcode' }, delivery: { url: '/s' } }; },
                isStale: () => false,
                isBlocked: () => false,
                discardOrphan: id => log.push('discard:' + id),
                backOff: () => log.push('backoff'),
                warn: () => log.push('warn'),
                install: response => log.push('install:' + response.sessionId)
            });
            return outcome + '|' + log.join(',');
            """;
        Assert.AreEqual("swapped|request:step_up,install:new", RunAsync(swapped), "The advice is named in the request so the server applies exactly that.");

        const string unplayable = """
            const log = [];
            const run = async plan => window.JularrStreamRecovery.followAdvisedPlan({
                advice: 'step_down',
                requestPlan: async () => plan,
                isStale: () => false,
                isBlocked: () => false,
                discardOrphan: () => log.push('discard'),
                backOff: () => log.push('backoff'),
                warn: () => log.push('warn'),
                install: () => log.push('install')
            });
            const unavailable = await run({ sessionId: null, plan: { mode: 'unavailable' }, delivery: null });
            const noDelivery = await run({ sessionId: 'x', plan: { mode: 'transcode' }, delivery: null });
            const noPlan = await run({ sessionId: 'x' });
            return [unavailable, noDelivery, noPlan].join(',') + '|' + log.join(',');
            """;
        Assert.AreEqual("unplayable,unplayable,unplayable|backoff,warn,backoff,warn,backoff,warn", RunAsync(unplayable), "Nothing is installed, the playing stream is untouched and advice backs off.");

        const string failed = """
            const log = [];
            const outcome = await window.JularrStreamRecovery.followAdvisedPlan({
                advice: 'step_down',
                requestPlan: async () => { throw new Error('network'); },
                isStale: () => false,
                isBlocked: () => false,
                discardOrphan: () => log.push('discard'),
                backOff: () => log.push('backoff'),
                warn: (message, detail) => log.push('warn:' + detail.message),
                install: () => log.push('install')
            });
            return outcome + '|' + log.join(',');
            """;
        Assert.AreEqual("failed|backoff,warn:network", RunAsync(failed), "A failed request is logged, never swallowed, and never takes the player over.");

        const string blocked = """
            const log = [];
            const outcome = await window.JularrStreamRecovery.followAdvisedPlan({
                advice: 'step_up',
                requestPlan: async () => ({ sessionId: 'late', plan: { mode: 'transcode' }, delivery: { url: '/s' } }),
                isStale: () => false,
                isBlocked: () => true,
                discardOrphan: id => log.push('discard:' + id),
                backOff: () => log.push('backoff'),
                warn: () => log.push('warn'),
                install: () => log.push('install')
            });
            return outcome + '|' + log.join(',');
            """;
        Assert.AreEqual("blocked|discard:late", RunAsync(blocked), "Picture-in-picture started while the plan was requested: nothing is swapped, and no backoff for a passing situation.");

        const string stale = """
            const log = [];
            const outcome = await window.JularrStreamRecovery.followAdvisedPlan({
                advice: 'step_up',
                requestPlan: async () => ({ sessionId: 'orphan', plan: { mode: 'transcode' }, delivery: { url: '/s' } }),
                isStale: () => true,
                isBlocked: () => false,
                discardOrphan: id => log.push('discard:' + id),
                backOff: () => log.push('backoff'),
                warn: () => log.push('warn'),
                install: () => log.push('install')
            });
            return outcome + '|' + log.join(',');
            """;
        Assert.AreEqual("stale|discard:orphan", RunAsync(stale), "A plan the viewer's own change overtook is dropped and its session released.");
    }

    [TestMethod]
    public void ThePlayerNamesTheFollowedAdviceKeepsTheStreamPlayingAndNeverBlamesAMode()
    {
        var root = PlayerControlsTests.RepositoryRoot();
        var player = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js")).Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = player.IndexOf("const followQualityAdvice = async advice => {", StringComparison.Ordinal);
        var follow = player[start..player.IndexOf("\n    };", start, StringComparison.Ordinal)];
        StringAssert.Contains(follow, "streamRecovery.followAdvisedPlan(");
        StringAssert.Contains(follow, "requestPlan({ followedAdvice: followed })", "The re-plan names the advice it follows.");
        StringAssert.Contains(follow, "pendingResumeTime = absoluteCurrentTime();", "The position of the swap is kept.");
        foreach (var forbidden in new[] { "failedModes", "sessionRecoveries", "showPlayerError", "hideVideo", "plan = null" })
        {
            Assert.IsFalse(follow.Contains(forbidden, StringComparison.Ordinal), $"Following advice must not touch {forbidden}.");
        }

        StringAssert.Contains(player, "handedOver: presentationHandedOver");
        StringAssert.Contains(player, "\"The playback telemetry answer could not be read.\"", "A malformed answer is logged, not swallowed.");
    }

    [TestMethod]
    public void ThePlayerAsksTheRuleBeforeItBlamesTheModeAndLoadsItFirst()
    {
        var root = PlayerControlsTests.RepositoryRoot();
        var player = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));
        var scripts = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Library", "_VideoPlayerScripts.cshtml"));

        var recovery = player.IndexOf("streamRecovery.shouldReplanSameMode(", StringComparison.Ordinal);
        var blame = player.IndexOf("failedModes.add(plan.mode)", StringComparison.Ordinal);
        Assert.IsTrue(recovery > 0 && blame > recovery, "A server-ended stream is checked before the mode is blamed.");
        StringAssert.Contains(player, "streamRecovery.recoveriesAfterProgress(");
        Assert.IsTrue(scripts.IndexOf("player-recovery.js", StringComparison.Ordinal) is > 0 and var rule && rule < scripts.IndexOf("episode-player.js", StringComparison.Ordinal));
    }

    // Runs an async script body under Jint and returns its resolved string.
    private static string RunAsync(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute($"var gateOptions = {Jularr.Web.Features.Playback.Decision.PlaybackAdaptationPolicy.Default.ClientGateJson()};");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "player-recovery.js")));
        engine.Execute($"var result = null; (async () => {{ {script} }})().then(value => {{ result = value; }}, error => {{ result = 'rejected:' + error; }});");
        engine.Advanced.ProcessTasks();
        return engine.Evaluate("result").AsString();
    }

    private static bool Run(string script) => Evaluate(script).AsBoolean();

    private static double RunNumber(string script) => Evaluate(script).AsNumber();

    private static Jint.Native.JsValue Evaluate(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute($"var gateOptions = {Jularr.Web.Features.Playback.Decision.PlaybackAdaptationPolicy.Default.ClientGateJson()};");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "player-recovery.js")));
        return engine.Evaluate($"(() => {{ {script} }})()");
    }
}
