// Staged generations of the Discover body (docs/mockups/discover/SPEC.md, "Staged late results and zero-shift ghost hints").
// The server renders a generation of the body from the sources that have answered. A source that has not answered leaves a reserved
// ghost section; when the next generation arrives, a ghost is filled in place (the same height, so nothing moves) and every other change
// is staged: it waits for a safe moment, so a card is never replaced, inserted or removed under a pointer, a focus, a touch, a scroll
// or a key press that is still in progress. The rules are DOM-free (time and interaction state are passed in), so they are testable
// without a page; wwwroot/js/discover.js applies the resulting operations to the document.
(() => {
    "use strict";

    // How long after the last scroll or key press the viewer is still considered to be navigating. Ordinary repeated remote or
    // keyboard navigation produces events well inside these windows, so it never triggers a rearrangement.
    const scrollQuietMs = 700;
    const keyQuietMs = 1200;
    const evaluateEveryMs = 300;

    /**
     * What changes between the body the viewer sees and the next generation. A section that is kept as it is needs nothing. A ghost that
     * becomes a section with titles is a fill and neutral: the ghost already has the height of the row. Everything else (a changed
     * section, one that appears or goes away, a ghost that becomes a message) can move content and is staged.
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
                const fill = existing.section.state === "pending" && section.state === "ready";
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
     * Whether one staged change may be applied now. A neutral fill always may. A change the viewer asked for (a retry) may. Otherwise it waits
     * while a pointer button or a touch is down, a sheet is open, a scroll or a key press is recent, or the pointer or the focus is inside the
     * section the change would replace. Moving the pointer alone does not freeze the page: only a pointer that rests on the affected section does.
     */
    const canCommit = (operation, context, now, explicit = false) => {
        if (operation.neutral || explicit) {
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
            const allowed = operations.filter((operation) => canCommit(operation, context, now(), staged.explicit));
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
            /** The next generation. A newer one replaces a staged one that was not applied. */
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

    window.JularrDiscoverStaging = { reconcile, canCommit, createController, scrollQuietMs, keyQuietMs, evaluateEveryMs };
})();
