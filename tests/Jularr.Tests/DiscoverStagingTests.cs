using Jint;

namespace Jularr.Tests;

/// <summary>
/// The staged generations of Discover (docs/mockups/discover/SPEC.md, "Staged late results and zero-shift ghost hints"), run under Jint against the
/// real discover-staging.js: what changes between two generations, when a change may be applied, and that a late arrival never replaces,
/// inserts or removes anything under a pointer, a focus, a touch, a scroll or a key press that is still in progress.
/// </summary>
[TestClass]
public sealed class DiscoverStagingTests
{
    // Time and interaction are fakes the test moves: nothing happens unless the test says so.
    private const string Harness = """
        const staging = window.JularrDiscoverStaging;
        const timers = {
            clock: 0, nextId: 1, queue: new Map(),
            setTimeout(fn, ms) { const id = timers.nextId++; timers.queue.set(id, { fn, at: timers.clock + ms }); return id; },
            clearTimeout(id) { timers.queue.delete(id); },
            advance(ms) {
                const until = timers.clock + ms;
                for (;;) {
                    const due = [...timers.queue.entries()].filter(([, t]) => t.at <= until).sort((a, b) => a[1].at - b[1].at)[0];
                    if (!due) break;
                    timers.queue.delete(due[0]); timers.clock = due[1].at; due[1].fn();
                }
                timers.clock = until;
            }
        };
        const idle = () => ({ pointerDown: false, touchActive: false, modalOpen: false, lastScrollAt: -100000, lastKeyAt: -100000, pointerSection: null, focusSection: null });
        let context = idle();
        const section = (id, sig, state = 'ready', extra = {}) => ({ id, sig, state, ...extra });
        const view = (...sections) => ({ state: 'sections', sections });
        const committed = [];
        const shown = [];
        // The document: applying operations replaces, adds or removes sections of the view on screen, exactly as discover.js does with the markup.
        let dom = null;
        const apply = (operations, next) => {
            committed.push(operations.map((o) => o.kind + ':' + o.id));
            const byId = new Map(next.sections.map((s) => [s.id, s]));
            let sections = dom.sections.slice();
            for (const o of operations) {
                if (o.kind === 'remove') sections = sections.filter((s) => s.id !== o.id);
                else if (o.kind === 'replace-all') sections = next.sections.slice();
                else if (sections.some((s) => s.id === o.id)) sections = sections.map((s) => (s.id === o.id ? byId.get(o.id) : s));
                else sections.push(byId.get(o.id));
            }
            dom = { state: next.state, sections };
            return dom;
        };
        const make = (initial) => {
            dom = initial;
            const controller = staging.createController({
                now: () => timers.clock, timers, getContext: () => context, commit: apply,
                show: (operations) => shown.push(operations.map((o) => o.id))
            });
            controller.reset(initial);
            return controller;
        };
        const arrive = (controller, next, explicit = false) => controller.stage(next, next, explicit);
        const ids = (operations) => operations.map((o) => o.kind + ':' + o.id + (o.neutral ? '!' : ''));
        """;

    [TestMethod]
    public void AGenerationThatChangesNothingNeedsNoOperation()
    {
        var result = Json("""
            const a = view(section('a', '1'), section('b', '2'));
            return ids(staging.reconcile(a, view(section('a', '1'), section('b', '2'))));
            """);

        Assert.AreEqual("[]", result);
    }

    [TestMethod]
    public void AGhostThatBecomesARowIsANeutralFillAndEveryOtherChangeIsNot()
    {
        var result = Json("""
            const current = view(section('ghost', 'g', 'pending'), section('row', 'r1'), section('gone', 'x'), section('failing', 'f', 'pending'));
            const next = view(section('ghost', 'g2'), section('row', 'r2'), section('failing', 'f2', 'unavailable'), section('fresh', 'n'));
            return ids(staging.reconcile(current, next));
            """);

        Assert.AreEqual("[\"fill:ghost!\",\"replace:row\",\"replace:failing\",\"insert:fresh\",\"remove:gone\"]", result);
    }

    [TestMethod]
    public void ABodyThatStopsBeingSectionsIsReplacedAsAWholeAndIsNeutralOnlyWhileItIsGhosts()
    {
        var result = Json("""
            const unavailable = { state: 'unavailable', sections: [] };
            return {
                fromGhosts: ids(staging.reconcile(view(section('a', '1', 'pending')), unavailable)),
                fromTitles: ids(staging.reconcile(view(section('a', '1')), unavailable)),
                same: ids(staging.reconcile(unavailable, { state: 'unavailable', sections: [] }))
            };
            """);

        Assert.AreEqual("{\"fromGhosts\":[\"replace-all:*!\"],\"fromTitles\":[\"replace-all:*\"],\"same\":[]}", result);
    }

    [TestMethod]
    public void AChangeWaitsWhileAPointerAFocusATouchASheetAScrollOrAKeyPressIsInProgress()
    {
        var result = Json("""
            const op = { id: 'row', kind: 'replace', neutral: false };
            const now = 10000;
            const allowed = (change) => staging.canCommit(op, { ...idle(), ...change }, now);
            return {
                free: allowed({}),
                pointerDown: allowed({ pointerDown: true }),
                touch: allowed({ touchActive: true }),
                sheet: allowed({ modalOpen: true }),
                scrolling: allowed({ lastScrollAt: now - 100 }),
                scrolled: allowed({ lastScrollAt: now - staging.scrollQuietMs - 1 }),
                typing: allowed({ lastKeyAt: now - 100 }),
                typed: allowed({ lastKeyAt: now - staging.keyQuietMs - 1 }),
                pointerOver: allowed({ pointerSection: 'row' }),
                pointerElsewhere: allowed({ pointerSection: 'other' }),
                focusInside: allowed({ focusSection: 'row' }),
                focusElsewhere: allowed({ focusSection: 'other' }),
                neutral: staging.canCommit({ ...op, neutral: true }, { ...idle(), pointerDown: true, pointerSection: 'row' }, now),
                asked: staging.canCommit(op, { ...idle(), pointerSection: 'row' }, now, true),
                wholeBodyWithPointer: staging.canCommit({ id: '*', kind: 'replace-all', neutral: false }, { ...idle(), pointerSection: 'row' }, now),
                wholeBodyIdle: staging.canCommit({ id: '*', kind: 'replace-all', neutral: false }, idle(), now)
            };
            """);

        Assert.AreEqual(
            "{\"free\":true,\"pointerDown\":false,\"touch\":false,\"sheet\":false,\"scrolling\":false,\"scrolled\":true,\"typing\":false,\"typed\":true,"
            + "\"pointerOver\":false,\"pointerElsewhere\":true,\"focusInside\":false,\"focusElsewhere\":true,\"neutral\":true,\"asked\":true,"
            + "\"wholeBodyWithPointer\":false,\"wholeBodyIdle\":true}",
            result);
    }

    [TestMethod]
    public void ALateResultNeverMovesTheSectionUnderThePointerAndIsAppliedOnceThePointerLeaves()
    {
        var result = Json("""
            const controller = make(view(section('a', '1'), section('b', '1'), section('c', '1')));
            context = { ...idle(), pointerSection: 'b' };
            arrive(controller, view(section('a', '2'), section('b', '2'), section('c', '2')));
            const whileOver = committed.slice();
            const hintWhileOver = shown[shown.length - 1];
            timers.advance(5000);
            const stillOver = committed.length;
            context = idle();
            timers.advance(staging.evaluateEveryMs);
            return { whileOver, hintWhileOver, stillOver, afterwards: committed.slice(), waiting: controller.waiting, hintAfter: shown[shown.length - 1] };
            """);

        Assert.AreEqual(
            "{\"whileOver\":[[\"replace:a\",\"replace:c\"]],\"hintWhileOver\":[\"b\"],\"stillOver\":1,\"afterwards\":[[\"replace:a\",\"replace:c\"],[\"replace:b\"]],\"waiting\":false,\"hintAfter\":[]}",
            result);
    }

    [TestMethod]
    public void AFillOfAGhostIsAppliedAtOnceEvenWhileTheViewerIsScrollingAndNothingIsHinted()
    {
        var result = Json("""
            const controller = make(view(section('row', 'g', 'pending')));
            context = { ...idle(), pointerDown: true, lastScrollAt: 0, lastKeyAt: 0 };
            arrive(controller, view(section('row', 'r')));
            return { committed: committed.slice(), waiting: controller.waiting, queued: timers.queue.size };
            """);

        Assert.AreEqual("{\"committed\":[[\"fill:row\"]],\"waiting\":false,\"queued\":0}", result);
    }

    [TestMethod]
    public void ANewerGenerationReplacesAStagedOneAndAViewersRetryAppliesOnlyWhatItAskedFor()
    {
        var result = Json("""
            const controller = make(view(section('a', '1'), section('failed', 'f', 'unavailable'), section('c', '1')));
            context = { ...idle(), focusSection: 'a', pointerSection: 'failed' };
            arrive(controller, view(section('a', '2'), section('failed', 'f2', 'unavailable'), section('c', '2')));
            const first = committed.slice();
            arrive(controller, view(section('a', '3'), section('failed', 'f3'), section('c', '2')), true);
            return { first, committed: committed.slice(), waiting: shown[shown.length - 1] };
            """);

        Assert.AreEqual("{\"first\":[[\"replace:c\"]],\"committed\":[[\"replace:c\"],[\"replace:failed\"]],\"waiting\":[\"a\"]}", result);
    }

    [TestMethod]
    public void ARetryReplacesTheSentenceOfSeveralFailedRowsByTheirRowsButNothingUnrelated()
    {
        var result = Json("""
            const current = view(section('trending-anime', 'a'), section('notice-unavailable-movies', 'n', 'unavailable'), section('top-anime', 'b'));
            const next = view(section('trending-anime', 'a2'), section('trending-movie', 'm'), section('top-movie', 'tm'), section('top-anime', 'b'));
            const operations = staging.reconcile(current, next);
            return [...staging.askedFor(operations, current)].sort();
            """);

        Assert.AreEqual("[\"notice-unavailable-movies\",\"top-movie\",\"trending-movie\"]", result);
    }

    [TestMethod]
    public void AGridRowThatGainsANoteOrAFilledRowWithAPartialFailureIsStagedBecauseItsHeightIsNotTheGhosts()
    {
        var result = Json("""
            const current = view(section('row', 'g', 'pending'), section('grid', 'g', 'pending'), section('noted', 'g', 'pending'));
            const next = view(section('row', 'r', 'ready', { layout: 'track' }), section('grid', 'x', 'ready', { layout: 'grid' }), section('noted', 'n', 'ready', { layout: 'track', note: true }));
            return ids(staging.reconcile(current, next));
            """);

        Assert.AreEqual("[\"fill:row!\",\"replace:grid\",\"replace:noted\"]", result);
    }

    [TestMethod]
    public void ANewAddressDropsWhatWasStagedForTheOldOne()
    {
        var result = Json("""
            const controller = make(view(section('a', '1')));
            context = { ...idle(), pointerSection: 'a' };
            arrive(controller, view(section('a', '2')));
            controller.reset(view(section('z', '1')));
            context = idle();
            timers.advance(5000);
            return { committed: committed.length, waiting: controller.waiting, queued: timers.queue.size, hint: shown[shown.length - 1] };
            """);

        Assert.AreEqual("{\"committed\":0,\"waiting\":false,\"queued\":0,\"hint\":[]}", result);
    }

    [TestMethod]
    public void WhileAKeyIsHeldDownNothingMovesAndTheChangeFollowsAfterTheKeyboardIsQuiet()
    {
        var result = Json("""
            const controller = make(view(section('a', '1')));
            timers.clock = 20000;
            context = { ...idle(), lastKeyAt: 20000 };
            arrive(controller, view(section('a', '2')));
            timers.advance(500); context = { ...idle(), lastKeyAt: 20000 };
            const early = committed.length;
            timers.advance(900);
            return { early, late: committed.length };
            """);

        Assert.AreEqual("{\"early\":0,\"late\":1}", result);
    }

    // The document, as discover.js presents it to the staging rules: every call is recorded, so the order the rules use is the order that is asserted.
    private const string DocumentHarness = """
        const log = [];
        const sections = new Set(['a', 'b', 'c']);
        const makeDom = (focus = null) => ({
            captureAnchor: () => { log.push('anchor'); return 'anchor-token'; },
            restoreAnchor: (token) => log.push('restore:' + token),
            captureFocus: () => { log.push('focus'); return focus; },
            restoreFocus: (token) => log.push('restoreFocus:' + (token ? token.control : 'none')),
            hideHover: () => log.push('hover'),
            replaceAll: () => log.push('replaceAll'),
            has: (id) => sections.has(id),
            swap: (id) => log.push('swap:' + id),
            insertAfter: (before, id) => { log.push('insert:' + id + '<-' + before); sections.add(id); },
            remove: (id) => { log.push('remove:' + id); sections.delete(id); },
            updateGeneration: () => log.push('generation'),
            view: () => ({ state: 'sections', sections: [...sections].map((id) => ({ id, sig: '1', state: 'ready' })) })
        });
        """;

    [TestMethod]
    public void OperationsAreAppliedBetweenTakingAndPuttingBackTheAnchorAndTheFocusAndAnInsertGoesAfterItsPredecessor()
    {
        var result = Json(DocumentHarness + """
            const applier = staging.createApplier(makeDom({ control: 'retry-button' }));
            const after = applier.apply(
                [{ id: 'a', kind: 'replace' }, { id: 'new', kind: 'insert' }, { id: 'c', kind: 'remove' }],
                ['a', 'b', 'new']);
            return { log, ids: after.sections.map((s) => s.id) };
            """);

        Assert.AreEqual(
            "{\"log\":[\"anchor\",\"focus\",\"hover\",\"swap:a\",\"insert:new<-b\",\"remove:c\",\"generation\",\"restore:anchor-token\",\"restoreFocus:retry-button\"],\"ids\":[\"a\",\"b\",\"new\"]}",
            result);
    }

    [TestMethod]
    public void ASectionThatLeadsTheNewGenerationIsInsertedAtTheTopAndAWholeBodyIsReplacedWithoutTouchingSections()
    {
        var result = Json(DocumentHarness + """
            const applier = staging.createApplier(makeDom());
            applier.apply([{ id: 'first', kind: 'insert' }], ['first', 'a', 'b', 'c']);
            const top = log.filter((entry) => entry.startsWith('insert'));
            log.length = 0;
            applier.apply([{ id: '*', kind: 'replace-all' }], []);
            return { top, whole: log };
            """);

        Assert.AreEqual("{\"top\":[\"insert:first<-null\"],\"whole\":[\"anchor\",\"focus\",\"hover\",\"replaceAll\",\"restore:anchor-token\",\"restoreFocus:none\"]}", result);
    }

    // The follow-up loop with a fake network the test answers one request at a time.
    private const string FollowUpHarness = """
        let page = { settled: 1, pending: 2 };
        let version = 1;
        const requests = [];
        const staged = [];
        const failures = [];
        const makeLoop = () => staging.createFollowUp({
            fetchNext: (settled) => new Promise((resolve, reject) => requests.push({ settled, resolve, reject })),
            stage: (answer) => { staged.push(answer.html); page = answer.page; },
            generation: () => page,
            isCurrent: (v) => v === version,
            onFailure: (error) => failures.push(error.status),
            maxRounds: 3
        });
        const settle = async () => { for (let i = 0; i < 12; i++) await null; };
        """;

    [TestMethod]
    public void TheFollowUpAsksFromWhatThePageKnowsOneRequestAtATimeAndEndsWhenNothingIsPending()
    {
        var result = JsonAsync(FollowUpHarness + """
            const loop = makeLoop();
            loop.run(1); loop.run(1);
            const concurrent = requests.length;
            requests[0].resolve({ html: 'one', page: { settled: 2, pending: 1 } });
            await settle();
            requests[1].resolve({ html: 'two', page: { settled: 3, pending: 0 } });
            await settle();
            return { concurrent, asked: requests.map((r) => r.settled), staged, running: loop.running, failures };
            """);

        Assert.AreEqual("{\"concurrent\":1,\"asked\":[1,2],\"staged\":[\"one\",\"two\"],\"running\":false,\"failures\":[]}", result);
    }

    [TestMethod]
    public void AFailedFollowUpAfterATooManyRequestsAnswerStagesNothingSoWhatIsOnThePageStaysAndTheViewerCanAskAgain()
    {
        var result = JsonAsync(FollowUpHarness + """
            const loop = makeLoop();
            loop.run(1);
            requests[0].reject({ status: '429' });
            await settle();
            const afterFailure = { staged: staged.length, failures: failures.slice(), running: loop.running };
            loop.run(1);
            requests[1].resolve({ html: 'late', page: { settled: 3, pending: 0 } });
            await settle();
            return { afterFailure, staged, failures };
            """);

        Assert.AreEqual("{\"afterFailure\":{\"staged\":0,\"failures\":[\"429\"],\"running\":false},\"staged\":[\"late\"],\"failures\":[\"429\"]}", result);
    }

    [TestMethod]
    public void ALoopOfAnOldAddressStopsSilentlyAndTheNumberOfRoundsIsBounded()
    {
        var result = JsonAsync(FollowUpHarness + """
            const stale = makeLoop();
            stale.run(1);
            version = 2;
            requests[0].reject({ status: 'aborted' });
            await settle();
            const silent = failures.length;
            version = 3;
            const bounded = makeLoop();
            bounded.run(3);
            for (let i = 0; i < 5; i++) { if (requests.length > 1 + i) requests[1 + i].resolve({ html: 'same', page: { settled: 1, pending: 2 } }); await settle(); }
            return { silent, rounds: requests.length - 1, staged: staged.length };
            """);

        Assert.AreEqual("{\"silent\":0,\"rounds\":3,\"staged\":3}", result);
    }

    [TestMethod]
    public void TheStatusOfARequestedTitleIsReadSoonAfterAChangeSlowerWhileItStaysAndNeverSoonerThanAServerAsked()
    {
        var result = Json("""
            const delays = [];
            let previous = 0;
            for (const changed of [true, false, false, false, false, false, true]) { previous = staging.nextPollDelay(previous, changed); delays.push(previous); }
            return { delays, rateLimited: staging.nextPollDelay(1500, false, 20000), unaskedFor: staging.nextPollDelay(1500, false, 0) };
            """);

        Assert.AreEqual("{\"delays\":[1500,2250,3375,5000,5000,5000,1500],\"rateLimited\":20000,\"unaskedFor\":2250}", result);
    }

    private static string Json(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "discover-staging.js")));
        return engine.Evaluate($"JSON.stringify((() => {{ {Harness} {script} }})())").AsString();
    }

    /// <summary>The same, for scripts that await: the engine settles the promises between the steps of the script, so no network or timer is involved.</summary>
    private static string JsonAsync(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "discover-staging.js")));
        var promise = engine.Evaluate($"(async () => {{ {Harness} {script} }})().then((value) => JSON.stringify(value))");
        return promise.UnwrapIfPromise().AsString();
    }
}
