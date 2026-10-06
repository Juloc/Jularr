// Staged generations of the Discover body (docs/mockups/discover/SPEC.md, "Staged late results and zero-shift ghost hints").
// The server renders a generation of the body from the sources that have answered. A source that has not answered leaves a reserved
// ghost section; when the next generation arrives, a ghost row is filled in place (the same height, so nothing moves) and every other change is
// staged: it waits for a safe moment, so a card is never replaced, inserted or removed under a pointer, a focus, a touch, a scroll or a key
// press that is still in progress. The rules are DOM-free (time, interaction state, network and the document are passed in), so they are
// testable without a page; wwwroot/js/discover.js connects them to the real document and the real network.
(() => {
    "use strict";

    // How long after the last scroll or key press the viewer is still considered to be navigating. Ordinary repeated remote or
    // keyboard navigation produces events well inside these windows, so it never triggers a rearrangement.
    const scrollQuietMs = 700;
    const keyQuietMs = 1200;
    const evaluateEveryMs = 300;

    const failedStates = new Set(["unavailable", "busy", "authfailed", "notconfigured", "disabled"]);

    /**
     * What changes between the body the viewer sees and the next generation. A section that is kept as it is needs nothing. A ghost row that
     * becomes a row of titles is a neutral fill: the ghost already has the height of the row. Everything else (a changed section, one that
     * appears or goes away, a ghost that becomes a message, a filled row that gains a note line, a grid, whose height depends on its titles)
     * can move content and is staged.
     */
    const reconcile = (current, next) => {
        if (current.state !== "sections" || next.state !== "sections") {
            if (current.state === next.state && current.state !== "sections") {
                return [];
            }

            const onlyGhosts = current.state === "sections" && current.sections.every((section) => section.state === "pending");
            return [{ id: "*", kind: "replace-all", index: 0, neutral: onlyGhosts }];
        }

        const operations = [];
        const currentById = new Map(current.sections.map((section, index) => [section.id, { section, index }]));
        const nextIds = new Set(next.sections.map((section) => section.id));

        next.sections.forEach((section, nextIndex) => {
            const existing = currentById.get(section.id);
            if (!existing) {
                operations.push({ id: section.id, kind: "insert", index: nextIndex, neutral: false });
            } else if (existing.section.sig !== section.sig) {
                const fill = existing.section.state === "pending" && section.state === "ready" && section.layout !== "grid" && !section.note;
                operations.push({ id: section.id, kind: fill ? "fill" : "replace", index: existing.index, neutral: fill });
            }
        });

        current.sections.forEach((section, index) => {
            if (!nextIds.has(section.id)) {
                operations.push({ id: section.id, kind: "remove", index, neutral: false });
            }
        });

        return operations;
    };

    /**
     * The changes a viewer asked for by pressing a retry: the sections that are failed now, and the sections that appear where a failed row stood
     * (several failed rows are one sentence, which the retry replaces by their rows). Nothing else: an unrelated change keeps waiting.
     */
    const askedFor = (operations, current) => {
        const failed = new Set(current.sections.filter((section) => failedStates.has(section.state)).map((section) => section.id));
        const replacesFailure = operations.some((operation) => failed.has(operation.id));
        return new Set(operations.filter((operation) => failed.has(operation.id) || (replacesFailure && operation.kind === "insert")).map((operation) => operation.id));
    };

    /**
     * Whether one staged change may be applied now. A neutral fill always may. A change the viewer asked for may. Otherwise it waits while a
     * pointer button or a touch is down, a sheet is open, a scroll or a key press is recent, or the pointer (over something to click) or the
     * focus is inside the section the change would replace. Moving the pointer alone does not freeze the page.
     */
    const canCommit = (operation, context, now, asked = false) => {
        if (operation.neutral || asked) {
            return true;
        }

        if (context.pointerDown || context.touchActive || context.modalOpen) {
            return false;
        }

        if (now - context.lastScrollAt < scrollQuietMs || now - context.lastKeyAt < keyQuietMs) {
            return false;
        }

        if (operation.id === "*") {
            return context.pointerSection === null && context.focusSection === null;
        }

        return context.pointerSection !== operation.id && context.focusSection !== operation.id;
    };

    /**
     * Holds the latest generation that is not applied yet and applies the allowed part of it whenever <c>evaluate</c> runs. <c>commit</c>
     * applies operations to the document and returns the view of the document afterwards; <c>show</c> receives the operations that are still
     * waiting, to mark them (the ghost hint).
     */
    const createController = ({ now = () => Date.now(), timers, getContext, commit, show = () => {} }) => {
        let current = { state: "sections", sections: [] };
        let staged = null;
        let timer = null;

        const stop = () => {
            if (timer !== null) {
                timers.clearTimeout(timer);
                timer = null;
            }
        };

        const evaluate = () => {
            stop();
            if (!staged) {
                return;
            }

            const operations = reconcile(current, staged.next);
            if (operations.length === 0) {
                staged = null;
                show([]);
                return;
            }

            const context = getContext();
            const asked = staged.explicit ? askedFor(operations, current) : new Set();
            const allowed = operations.filter((operation) => canCommit(operation, context, now(), asked.has(operation.id)));
            if (allowed.length > 0) {
                current = commit(allowed, staged.payload);
            }

            const waiting = reconcile(current, staged.next);
            show(waiting);
            if (waiting.length === 0) {
                staged = null;
                return;
            }

            timer = timers.setTimeout(evaluate, evaluateEveryMs);
        };

        return {
            /** A new address replaced the whole body: nothing staged for the old one survives. */
            reset(view) {
                stop();
                staged = null;
                current = view;
                show([]);
            },
            /** The next generation. A newer one replaces a staged one that was not applied. <c>explicit</c>: the viewer asked for it by a retry. */
            stage(next, payload, explicit = false) {
                staged = { next, payload, explicit };
                evaluate();
            },
            evaluate,
            get waiting() {
                return staged !== null;
            },
            get current() {
                return current;
            }
        };
    };

    /**
     * Applies operations to the document through <c>dom</c>: sections are replaced, inserted after the section that precedes them in the new
     * generation or removed, then the scroll anchor and the focus are put back, so what the viewer looks at and where the keyboard is stay where
     * they were. Returns the view of the document afterwards (<c>dom.view</c>).
     */
    const createApplier = (dom) => ({
        apply(operations, nextIds) {
            const anchor = dom.captureAnchor();
            const focus = dom.captureFocus();
            dom.hideHover();
            if (operations.some((operation) => operation.kind === "replace-all")) {
                dom.replaceAll();
            } else {
                for (const operation of operations) {
                    if (operation.kind === "remove") {
                        dom.remove(operation.id);
                    } else if (dom.has(operation.id)) {
                        dom.swap(operation.id);
                    } else {
                        const before = nextIds.slice(0, nextIds.indexOf(operation.id)).reverse().find((id) => dom.has(id)) ?? null;
                        dom.insertAfter(before, operation.id);
                    }
                }

                dom.updateGeneration();
            }

            dom.restoreAnchor(anchor);
            dom.restoreFocus(focus);
            return dom.view();
        }
    });

    /**
     * The asking for the next generation while sources are pending: one loop at a time, however often it is started (a retry while the loop runs
     * does not start a second one), a bounded number of rounds, and a failure ends the loop without touching what is on the page: <c>onFailure</c>
     * tells the viewer, and <c>run</c> starts it again. <c>fetchNext</c> receives the number of settled sources the page already knows.
     */
    const createFollowUp = ({ fetchNext, stage, generation, isCurrent, onFailure, maxRounds = 8 }) => {
        let running = false;
        return {
            get running() {
                return running;
            },
            async run(version) {
                if (running) {
                    return;
                }

                running = true;
                try {
                    for (let round = 0; round < maxRounds; round++) {
                        if (!isCurrent(version) || generation().pending === 0) {
                            return;
                        }

                        const answer = await fetchNext(generation().settled);
                        if (!isCurrent(version)) {
                            return;
                        }

                        stage(answer);
                    }
                } catch (error) {
                    if (isCurrent(version)) {
                        onFailure(error);
                    }
                } finally {
                    running = false;
                }
            }
        };
    };

    const firstPollMs = 1500;
    const maxPollMs = 5000;
    const pollBackoff = 1.5;

    /**
     * When to read the live state of a requested title again: soon after it changed, slower while it does not, and never sooner than a server that
     * asked to be left alone (<c>retryAfterMs</c>, from a 429) allows.
     */
    const nextPollDelay = (previousMs, changed, retryAfterMs = 0) => {
        const base = changed || !previousMs ? firstPollMs : Math.min(maxPollMs, Math.round(previousMs * pollBackoff));
        return Math.max(base, retryAfterMs);
    };

    window.JularrDiscoverStaging = { reconcile, askedFor, canCommit, createController, createApplier, createFollowUp, nextPollDelay, scrollQuietMs, keyQuietMs, evaluateEveryMs };
})();
