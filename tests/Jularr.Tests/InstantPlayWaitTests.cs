using Jint;

namespace Jularr.Tests;

/// <summary>
/// The wait state machine of instant-play.js (docs/mockups/instant-play, sections 5-10), run under Jint against the real script: polling
/// back-off and its bounds, terminal states, the visibility pause, leaving the page, opening the Player only while the viewer is still
/// there, Stop waiting without any network, and the DTO fields the script consumes.
/// </summary>
[TestClass]
public sealed class InstantPlayWaitTests
{
    // Network, timers, visibility and navigation are fakes the test drives step by step: a reply is delivered only when the test says so.
    private const string Harness = """
        const calls = [];
        const pending = { intent: null, status: null };
        const api = {
            postIntent(target, callback) { calls.push({ kind: 'intent', target }); pending.intent = callback; return { abort() { pending.intent = null; } }; },
            getStatus(id, episode, callback) { calls.push({ kind: 'status', id, episode }); pending.status = callback; return { abort() { pending.status = null; } }; }
        };
        const reply = (which, status, body) => { const callback = pending[which]; pending[which] = null; callback(null, { status, body }); };
        const fail = (which) => { const callback = pending[which]; pending[which] = null; callback(new Error('network')); };
        const timers = {
            clock: 0, nextId: 1, queue: new Map(),
            setTimeout(fn, ms) { const id = timers.nextId++; timers.queue.set(id, { fn, at: timers.clock + ms, ms }); return id; },
            clearTimeout(id) { timers.queue.delete(id); },
            next() { const [id, timer] = [...timers.queue.entries()].sort((a, b) => a[1].at - b[1].at)[0]; timers.queue.delete(id); timers.clock = timer.at; timer.fn(); return timer.ms; },
            scheduled() { return [...timers.queue.values()].map((t) => t.ms); }
        };
        let visible = true;
        const navigations = [];
        let reloads = 0;
        const snapshots = [];
        const make = (extra = {}) => window.JularrInstantPlay.createWait({
            target: { workId: 'w1', workEpisodeId: 'e1' }, api, timers, isVisible: () => visible, watchHref: '/Library/Watch/w1/e1',
            navigate: (href) => navigations.push(href), reload: () => { reloads++; }, onChange: (s) => snapshots.push(s), ...extra
        });
        const view = (state, extra = {}) => ({ state, mediaUnit: 'episode', progressPercent: null, isMonitoring: false, ...extra });
        const acquiring = (v) => ({ outcome: 'acquiring', target: { workId: 'w1', workEpisodeId: 'e1' }, requestId: 'r1', acquisition: v });
        const status = (v) => ({ requestId: 'r1', target: { workId: 'w1', workEpisodeId: 'e1' }, acquisition: v });
        const step = (v) => { const ms = timers.next(); reply('status', 200, status(v)); return ms; };
        """;

    [TestMethod]
    public void PollingStartsAtOnePointFiveSecondsBacksOffToFiveAndStartsOverWhenSomethingChanges()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            const delays = [step(view('looking_for_media')), step(view('looking_for_media')), step(view('looking_for_media')), step(view('looking_for_media'))];
            const afterCap = timers.scheduled();
            step(view('getting_media', { progressPercent: 10 }));
            return { delays, afterCap, afterChange: timers.scheduled(), phase: wait.snapshot().phase };
            """);

        Assert.AreEqual("{\"delays\":[1500,2250,3375,5000],\"afterCap\":[5000],\"afterChange\":[1500],\"phase\":\"waiting\"}", result);
    }

    [TestMethod]
    public void OnlyOneIntentIsSentWhileTheWaitIsRunningAndItNamesTheTarget()
    {
        var result = Json("""
            const wait = make(); wait.start(); wait.start();
            reply('intent', 200, acquiring(view('looking_for_media')));
            wait.start();
            return calls;
            """);

        Assert.AreEqual("[{\"kind\":\"intent\",\"target\":{\"workId\":\"w1\",\"workEpisodeId\":\"e1\"}}]", result);
    }

    [TestMethod]
    public void TheStatusIsReadForTheRequestAndTheEpisodeTheServerNamed()
    {
        var result = Json("""
            const wait = make({ target: { workId: 'w1', workEpisodeId: null } }); wait.start();
            reply('intent', 200, { outcome: 'acquiring', target: { workId: 'w1', workEpisodeId: 'e9' }, requestId: 'r7', acquisition: view('looking_for_media') });
            timers.next();
            return calls.filter((c) => c.kind === 'status');
            """);

        Assert.AreEqual("[{\"kind\":\"status\",\"id\":\"r7\",\"episode\":\"e9\"}]", result);
    }

    [TestMethod]
    public void ReadyOpensThePlayerOnceWhileTheViewerIsStillThereAndPollingStops()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            step(view('getting_media', { progressPercent: 42 })); step(view('preparing'));
            step(view('ready_to_watch'));
            return { navigations, phase: wait.snapshot().phase, scheduled: timers.scheduled(), statusCalls: calls.filter((c) => c.kind === 'status').length };
            """);

        Assert.AreEqual("{\"navigations\":[\"/Library/Watch/w1/e1\"],\"phase\":\"handingOver\",\"scheduled\":[],\"statusCalls\":3}", result);
    }

    [TestMethod]
    public void AlreadyLocalTargetsOpenThePlayerFromTheIntentAnswerWithoutPolling()
    {
        var local = Json("const wait = make(); wait.start(); reply('intent', 200, { outcome: 'play_now', target: { workId: 'w1', workEpisodeId: 'e1' } }); return { navigations, calls: calls.length };");
        var ready = Json("const wait = make(); wait.start(); reply('intent', 200, acquiring(view('ready_to_watch'))); return { navigations, calls: calls.length };");

        Assert.AreEqual("{\"navigations\":[\"/Library/Watch/w1/e1\"],\"calls\":1}", local);
        Assert.AreEqual("{\"navigations\":[\"/Library/Watch/w1/e1\"],\"calls\":1}", ready);
    }

    [TestMethod]
    public void LeavingThePageOrTheTabClearsTheIntentSoALaterReadyNeverOpensThePlayer()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            step(view('getting_media', { progressPercent: 30 }));
            visible = false; wait.leave();
            const whileHidden = { scheduled: timers.scheduled(), holds: wait.snapshot().holdsIntent, phase: wait.snapshot().phase };
            visible = true; wait.resume();
            timers.next(); reply('status', 200, status(view('ready_to_watch')));
            const shown = wait.snapshot();
            return { whileHidden, navigations, phase: shown.phase, state: shown.view.state, scheduled: timers.scheduled() };
            """);

        Assert.AreEqual(
            "{\"whileHidden\":{\"scheduled\":[],\"holds\":false,\"phase\":\"observing\"},\"navigations\":[],\"phase\":\"ended\",\"state\":\"ready_to_watch\",\"scheduled\":[]}",
            result,
            "The viewer returns to Ready to watch, not to a Player that opens on its own.");
    }

    [TestMethod]
    public void AnIntentThatAnswersAfterTheViewerLeftNeverOpensThePlayerEither()
    {
        var result = Json("""
            const wait = make(); wait.start();
            wait.leave();
            reply('intent', 200, acquiring(view('ready_to_watch')));
            return { navigations, phase: wait.snapshot().phase };
            """);

        Assert.AreEqual("{\"navigations\":[],\"phase\":\"ended\"}", result);
    }

    [TestMethod]
    public void AHiddenPageDoesNotPollEvenWhenAPollWasInFlight()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            timers.next(); // a poll is in flight
            visible = false; wait.leave();
            reply('status', 200, status(view('looking_for_media')));
            return { scheduled: timers.scheduled(), statusCalls: calls.filter((c) => c.kind === 'status').length };
            """);

        Assert.AreEqual("{\"scheduled\":[],\"statusCalls\":1}", result);
    }

    [TestMethod]
    public void StopWaitingEndsOnlyThisPagesWaitAndSendsNothingToTheServer()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('getting_media', { progressPercent: 20 })));
            const before = calls.length;
            wait.stop();
            const after = wait.snapshot();
            return { newCalls: calls.length - before, scheduled: timers.scheduled(), phase: after.phase, notice: after.notice, holds: after.holdsIntent, kinds: [...new Set(calls.map((c) => c.kind))] };
            """);

        Assert.AreEqual("{\"newCalls\":0,\"scheduled\":[],\"phase\":\"ended\",\"notice\":\"stopped\",\"holds\":false,\"kinds\":[\"intent\"]}", result, "Stop waiting adds no request of any kind and keeps no timer running.");
    }

    [TestMethod]
    public void StopWaitingDuringAPollIgnoresItsLateAnswerAndNeverOpensThePlayer()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            timers.next();
            wait.stop();
            return { lateAnswerHandled: pending.status === null, navigations, phase: wait.snapshot().phase };
            """);

        Assert.AreEqual("{\"lateAnswerHandled\":true,\"navigations\":[],\"phase\":\"ended\"}", result);
    }

    [TestMethod]
    [DataRow("limit_reached", "limit_reached")]
    [DataRow("not_available", "not_available")]
    public void OutcomesWithNothingToWaitForEndTheWaitWithTheirNotice(string outcome, string notice)
    {
        var result = Json($"const wait = make(); wait.start(); reply('intent', 200, {{ outcome: '{outcome}', target: {{ workId: 'w1', workEpisodeId: 'e1' }} }}); const s = wait.snapshot(); return {{ phase: s.phase, notice: s.notice, scheduled: timers.scheduled(), reloads }};");

        Assert.AreEqual($"{{\"phase\":\"ended\",\"notice\":\"{notice}\",\"scheduled\":[],\"reloads\":0}}", result);
    }

    [TestMethod]
    [DataRow("request_required")]
    [DataRow("target_not_found")]
    public void WhenTheServerResolvesAnotherActionThePageReloadsToShowIt(string outcome)
    {
        var result = Json($"const wait = make(); wait.start(); reply('intent', 200, {{ outcome: '{outcome}', target: {{ workId: 'w1', workEpisodeId: null }} }}); return {{ reloads, phase: wait.snapshot().phase }};");

        Assert.AreEqual("{\"reloads\":1,\"phase\":\"ended\"}", result);
    }

    [TestMethod]
    public void AwaitingApprovalShowsTheSavedStateAndNeverBypassesIt()
    {
        var result = Json("""
            const wait = make(); wait.start();
            reply('intent', 200, { outcome: 'awaiting_approval', target: { workId: 'w1', workEpisodeId: 'e1' }, requestId: 'r1', acquisition: view('waiting_for_approval') });
            const s = wait.snapshot();
            return { phase: s.phase, state: s.view.state, scheduled: timers.scheduled(), navigations, holds: s.holdsIntent };
            """);

        Assert.AreEqual("{\"phase\":\"ended\",\"state\":\"waiting_for_approval\",\"scheduled\":[],\"navigations\":[],\"holds\":false}", result);
    }

    [TestMethod]
    [DataRow("not_available_yet")]
    [DataRow("needs_attention")]
    [DataRow("rejected")]
    [DataRow("not_available")]
    [DataRow("monitoring_future_releases")]
    [DataRow("available")]
    public void EveryStateThatNothingWillChangeEndsPollingAndIsShownAsItIs(string state)
    {
        var result = Json($"const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media'))); step(view('{state}')); const s = wait.snapshot(); return {{ phase: s.phase, scheduled: timers.scheduled(), navigations, state: s.view.state }};");

        Assert.AreEqual($"{{\"phase\":\"ended\",\"scheduled\":[],\"navigations\":[],\"state\":\"{state}\"}}", result);
    }

    [TestMethod]
    public void TransientFailuresRetryWithinBoundsAndFiveInARowEndWithAnHonestUnavailableNotice()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            timers.next(); fail('status'); timers.next(); reply('status', 503, null); timers.next(); fail('status'); timers.next(); fail('status');
            const stillWaiting = wait.snapshot().phase;
            timers.next(); fail('status');
            const s = wait.snapshot();
            return { stillWaiting, phase: s.phase, notice: s.notice, scheduled: timers.scheduled() };
            """);

        Assert.AreEqual("{\"stillWaiting\":\"waiting\",\"phase\":\"ended\",\"notice\":\"unavailable\",\"scheduled\":[]}", result);
    }

    [TestMethod]
    public void ASuccessfulReadResetsTheFailureCount()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            for (let i = 0; i < 4; i++) { timers.next(); fail('status'); }
            step(view('looking_for_media'));
            for (let i = 0; i < 4; i++) { timers.next(); fail('status'); }
            return wait.snapshot().phase;
            """);

        Assert.AreEqual("\"waiting\"", result);
    }

    [TestMethod]
    public void ARequestThatIsGoneEndsTheWaitAtOnceInsteadOfRetrying()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            timers.next(); reply('status', 404, null);
            const s = wait.snapshot();
            return { phase: s.phase, notice: s.notice, statusCalls: calls.filter((c) => c.kind === 'status').length };
            """);

        Assert.AreEqual("{\"phase\":\"ended\",\"notice\":\"unavailable\",\"statusCalls\":1}", result);
    }

    [TestMethod]
    public void AFailedOrRefusedIntentIsShownHonestlyAndCanBeRetriedAfterReset()
    {
        var result = Json("""
            const wait = make(); wait.start(); fail('intent');
            const network = wait.snapshot().notice;
            wait.reset(); wait.start(); reply('intent', 429, null);
            const refused = wait.snapshot().notice;
            wait.reset(); wait.start(); reply('intent', 404, null);
            const missing = wait.snapshot().notice;
            wait.reset(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            return { network, refused, missing, phase: wait.snapshot().phase, intents: calls.filter((c) => c.kind === 'intent').length };
            """);

        Assert.AreEqual("{\"network\":\"unavailable\",\"refused\":\"unavailable\",\"missing\":\"not_available\",\"phase\":\"waiting\",\"intents\":4}", result);
    }

    [TestMethod]
    public void AWaitStopsAfterTheMaximumVisibleTime()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            let rounds = 0;
            while (wait.snapshot().phase === 'waiting' && rounds < 2000) { step(view('looking_for_media')); rounds++; }
            const s = wait.snapshot();
            return { phase: s.phase, notice: s.notice, minutes: Math.round(rounds * 5 / 60), limit: window.JularrInstantPlay.maxWaitMs / 60000 };
            """);

        StringAssert.StartsWith(result, "{\"phase\":\"ended\",\"notice\":\"unavailable\"");
        StringAssert.Contains(result, "\"limit\":30");
    }

    [TestMethod]
    public void ProgressIsOnlyAPercentageWhenTheServerSentOneAndIsNeverInvented()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('getting_media')));
            const unknown = wait.snapshot().percent;
            step(view('getting_media', { progressPercent: 42 }));
            const known = wait.snapshot().percent;
            step(view('preparing', { progressPercent: 77 }));
            const afterPreparing = wait.snapshot().percent;
            return { unknown, known, afterPreparing, getting: window.JularrInstantPlay.milestones(view('getting_media')).map((m) => m.percent), gettingKnown: window.JularrInstantPlay.milestones(view('getting_media', { progressPercent: 42 })).map((m) => m.percent) };
            """);

        Assert.AreEqual("{\"unknown\":null,\"known\":42,\"afterPreparing\":null,\"getting\":[null,null,null,null],\"gettingKnown\":[null,null,null,42]}", result);
    }

    [TestMethod]
    public void MilestonesAreGenericAndFollowTheProjectedState()
    {
        var result = Json("""
            const names = (state) => window.JularrInstantPlay.milestones(view(state)).map((m) => m.key + ':' + m.status);
            return { approval: names('waiting_for_approval'), looking: names('looking_for_media'), preparing: names('preparing'), ready: names('ready_to_watch'), failed: names('needs_attention') };
            """);

        Assert.AreEqual(
            "{\"approval\":[],\"looking\":[\"approved:done\",\"looking:current\"],\"preparing\":[\"approved:done\",\"looking:done\",\"found:done\",\"getting:done\",\"preparing:current\"],"
            + "\"ready\":[\"approved:done\",\"looking:done\",\"found:done\",\"getting:done\",\"preparing:done\",\"ready:done\"],\"failed\":[]}",
            result);
    }

    [TestMethod]
    public void TheLiveRegionAnnouncesStateChangesAndCoarseProgressNotEveryPercent()
    {
        var result = Json("""
            const wait = make(); wait.start(); reply('intent', 200, acquiring(view('looking_for_media')));
            const spoken = [];
            const note = () => { const a = wait.snapshot().announcement; if (spoken[spoken.length - 1]?.id !== a.id) spoken.push({ id: a.id, state: a.state, percent: a.percent }); };
            note();
            for (const p of [3, 9, 24, 25, 31, 49, 50, 99]) { step(view('getting_media', { progressPercent: p })); note(); }
            step(view('preparing')); note();
            return spoken.map((a) => a.state + ':' + a.percent);
            """);

        Assert.AreEqual("[\"looking_for_media:null\",\"getting_media:null\",\"getting_media:25\",\"getting_media:50\",\"getting_media:75\",\"preparing:null\"]", result);
    }

    [TestMethod]
    public void AnObservedRequestNeverOpensThePlayerAndStopsAtItsEnd()
    {
        var result = Json("""
            const wait = make({ observeOnly: true, requestId: 'r1' });
            const first = timers.scheduled();
            step(view('getting_media', { progressPercent: 5 })); step(view('ready_to_watch'));
            const s = wait.snapshot();
            return { first, navigations, phase: s.phase, state: s.view.state, scheduled: timers.scheduled() };
            """);

        Assert.AreEqual("{\"first\":[1500],\"navigations\":[],\"phase\":\"ended\",\"state\":\"ready_to_watch\",\"scheduled\":[]}", result);
    }

    [TestMethod]
    public void AnObserverPausesWhileTheTabIsHiddenAndLooksAgainWhenItIsBack()
    {
        var result = Json("""
            const wait = make({ observeOnly: true, requestId: 'r1' });
            visible = false; wait.leave();
            const hidden = timers.scheduled();
            visible = true; wait.resume();
            return { hidden, resumed: timers.scheduled() };
            """);

        Assert.AreEqual("{\"hidden\":[],\"resumed\":[0]}", result);
    }

    [TestMethod]
    public void TheApiUsesTheSessionTheJsonBodyAndTheStatusAddress()
    {
        var result = Json("""
            const requests = [];
            const thenable = (value) => ({ then: (ok) => thenable(ok(value)), catch: () => thenable(value), finally: (fn) => { fn(); return thenable(value); } });
            const fetchImpl = (url, init) => { requests.push({ url, method: init.method, credentials: init.credentials, body: init.body ?? null, type: init.headers['Content-Type'] ?? null }); return thenable({ status: 200, json: () => thenable({ ok: true }) }); };
            const real = window.JularrInstantPlay.createApi({ intentUrl: '/api/client/v1/video/playback-intents', statusUrl: '/api/client/v1/requests/{id}', fetchImpl, timers });
            real.postIntent({ workId: 'w 1', workEpisodeId: null }, () => {});
            real.getStatus('r/1', 'e1', () => {});
            real.getStatus('r1', null, () => {});
            return requests;
            """);

        Assert.AreEqual(
            "[{\"url\":\"/api/client/v1/video/playback-intents\",\"method\":\"POST\",\"credentials\":\"same-origin\",\"body\":\"{\\\"target\\\":{\\\"workId\\\":\\\"w 1\\\",\\\"workEpisodeId\\\":null}}\",\"type\":\"application/json\"},"
            + "{\"url\":\"/api/client/v1/requests/r%2F1?workEpisodeId=e1\",\"method\":\"GET\",\"credentials\":\"same-origin\",\"body\":null,\"type\":null},"
            + "{\"url\":\"/api/client/v1/requests/r1\",\"method\":\"GET\",\"credentials\":\"same-origin\",\"body\":null,\"type\":null}]",
            result);
    }

    private static string Json(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "instant-play.js")));
        return engine.Evaluate($"JSON.stringify((() => {{ {Harness} {script} }})())").AsString();
    }
}
