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
        const section = (id, sig, state = 'ready') => ({ id, sig, state });
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
    public void ANewerGenerationReplacesAStagedOneAndAViewersOwnRetryIsAppliedAtOnce()
    {
        var result = Json("""
            const controller = make(view(section('a', '1')));
            context = { ...idle(), focusSection: 'a' };
            arrive(controller, view(section('a', '2')));
            arrive(controller, view(section('a', '3')));
            const staged = committed.length;
            arrive(controller, view(section('a', '4')), true);
            return { staged, committed: committed.slice(), current: controller.current.sections[0].sig };
            """);

        Assert.AreEqual("{\"staged\":0,\"committed\":[[\"replace:a\"]],\"current\":\"4\"}", result);
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

    private static string Json(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "discover-staging.js")));
        return engine.Evaluate($"JSON.stringify((() => {{ {Harness} {script} }})())").AsString();
    }
}
