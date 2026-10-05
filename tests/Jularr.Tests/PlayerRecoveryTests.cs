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
    [DataRow("{ state: 'ended', reason: 'idle' }", 0, true)]
    [DataRow("{ state: 'ended', reason: 'cache_budget' }", 2, true)]
    [DataRow("{ state: 'ended', reason: null }", 0, true)]
    [DataRow("{ gone: true }", 1, true)]
    [DataRow("{ state: 'ended', reason: 'encoder_exited' }", 0, false)]
    [DataRow("{ state: 'active', reason: null }", 0, false)]
    [DataRow("null", 0, false)]
    [DataRow("{ state: 'ended', reason: 'idle' }", 3, false)]
    [DataRow("{ gone: true }", 3, false)]
    public void OnlyAServerEndedStreamWithinTheBudgetIsPlannedAgainWithTheSameMode(string status, int recoveries, bool expected)
    {
        Assert.AreEqual(expected, Run($"return window.JularrStreamRecovery.shouldReplanSameMode({status}, {recoveries});"));
    }

    [TestMethod]
    public void TheBudgetOnlyReturnsAfterThirtySecondsOfRealPlayback()
    {
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
                if (recovery.shouldReplanSameMode({ state: 'ended', reason: 'cache_budget' }, recoveries)) {
                    recoveries += 1;
                    replans += 1;
                    recoveries = recovery.recoveriesAfterProgress(recoveries, 5);
                }
            }
            return replans;
            """;

        Assert.AreEqual(3.0, RunNumber(script));
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

    private static bool Run(string script) => Evaluate(script).AsBoolean();

    private static double RunNumber(string script) => Evaluate(script).AsNumber();

    private static Jint.Native.JsValue Evaluate(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "player-recovery.js")));
        return engine.Evaluate($"(() => {{ {script} }})()");
    }
}
