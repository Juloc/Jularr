// Instant Play on the Movie and Series pages (docs/mockups/instant-play/SPEC.md, sections 5-10): the primary playback action
// morphs in place from "Start watching" through Looking for media, Getting episode, Preparing and Starting playback into the
// Player. The server owns every fact: this module sends the explicit playback intent, reads the consumer projection of the
// request and renders it; it never invents a state or a percentage. The wait is a transient client intent: leaving the page,
// hiding the tab or pressing Stop waiting clears it, so a later "ready" never opens the Player and nothing shared is cancelled.
// The rules are DOM-free (timers, visibility, network and navigation are passed in), so they are testable without a page; the
// form it enhances posts to the page handler by itself when this script does not run.
(() => {
    "use strict";

    const firstPollDelayMs = 1500;
    const maxPollDelayMs = 5000;
    const pollBackoff = 1.5;
    // A wait on one page that has lasted this long (visible time only) stops with an honest "status temporarily unavailable".
    const maxWaitMs = 30 * 60 * 1000;
    const maxConsecutiveFailures = 5;
    // A percentage that moved is not news every poll: the page asks no more often than this while only the percentage changes, which
    // keeps a few open tabs well inside the per-account status limit.
    const percentChangeDelayMs = 2500;
    const maxRetryAfterMs = 60000;
    const requestTimeoutMs = 15000;
    // The live region speaks state changes and progress in steps this wide, never every percent.
    const announceStepPercent = 25;

    // The states that still change on their own. Every other state the projection answers ends the wait: nothing more will
    // change until someone acts, and the state is shown as it is.
    const workingStates = new Set(["looking_for_media", "getting_media", "preparing"]);
    const readyState = "ready_to_watch";
    const milestoneOrder = ["approved", "looking", "found", "getting", "preparing", "ready"];

    /** The generic milestones reached for a projected state, never more than the consumer may know (SPEC section 7). */
    const milestones = (view) => {
        const reached = { looking_for_media: 1, getting_media: 3, preparing: 4, ready_to_watch: 5, available: 5 }[view?.state];
        if (reached === undefined) {
            return [];
        }

        const finished = view.state === readyState || view.state === "available";
        return milestoneOrder.slice(0, reached + 1).map((key, index) => ({
            key,
            status: index < reached || finished ? "done" : "current",
            percent: key === "getting" && view.state === "getting_media" ? view.progressPercent ?? null : null
        }));
    };

    const percentOf = (view) => (view?.state === "getting_media" && Number.isInteger(view.progressPercent) ? view.progressPercent : null);

    // The coarse step a progress change is announced at; null while there is no reliable percentage or it has not reached a step.
    const announceStep = (percent) => (percent === null || percent < announceStepPercent ? null : Math.floor(percent / announceStepPercent) * announceStepPercent);

    /**
     * One transient wait for playback of one target. Phases: idle, starting (the intent is on its way), waiting (the request is
     * being worked on and this page waits for playback), observing (the same, but nothing waits for playback: the viewer left,
     * or only looks), handingOver (the Player is opening) and ended (see <c>notice</c> and <c>view</c> for why).
     */
    const createWait = ({ target, requestId = null, observeOnly = false, api, timers, isVisible = () => true, watchHref = null, onChange = () => {}, navigate = () => {}, reload = () => {} }) => {
        let phase = observeOnly ? "observing" : "idle";
        let view = null;
        let notice = null;
        let currentRequestId = requestId;
        let episodeId = target.workEpisodeId ?? null;
        let holdsIntent = false;
        // Another control reads the same request while this one rests: one poller per request.
        let rested = false;
        let timer = null;
        let inFlight = null;
        let generation = 0;
        let delay = firstPollDelayMs;
        let waitedMs = 0;
        let failures = 0;
        let announceId = 0;
        let lastAnnounced = null;
        let announcement = null;

        const clearTimer = () => {
            if (timer !== null) {
                timers.clearTimeout(timer);
                timer = null;
            }
        };

        const cancelInFlight = () => {
            generation++;
            inFlight?.abort?.();
            inFlight = null;
        };

        const publish = () => {
            const percent = percentOf(view);
            const key = `${phase}|${notice ?? ""}|${view?.state ?? ""}|${announceStep(percent) ?? ""}`;
            if (key !== lastAnnounced) {
                lastAnnounced = key;
                announcement = { id: ++announceId, phase, notice, state: view?.state ?? null, mediaUnit: view?.mediaUnit ?? null, percent: announceStep(percent) };
            }

            onChange(snapshot());
        };

        const snapshot = () => ({
            phase,
            view,
            notice,
            requestId: currentRequestId,
            workEpisodeId: episodeId,
            holdsIntent,
            percent: percentOf(view),
            milestones: milestones(view),
            announcement
        });

        const end = (reason) => {
            clearTimer();
            cancelInFlight();
            holdsIntent = false;
            phase = "ended";
            notice = reason;
            publish();
        };

        // The Player opens only for a viewer who is still here, still holds the wait and was given a place to go.
        const mayOpenPlayer = () => Boolean(watchHref) && holdsIntent && isVisible();

        const handOver = () => {
            clearTimer();
            cancelInFlight();
            phase = "handingOver";
            notice = null;
            publish();
            navigate(watchHref);
        };

        const scheduleNext = () => {
            clearTimer();
            if (phase !== "waiting" && phase !== "observing") {
                return;
            }

            // A hidden page or a resting control is paused, not ended: becoming visible or active polls at once (see resume).
            if (!isVisible() || rested) {
                return;
            }

            if (waitedMs >= maxWaitMs) {
                end("unavailable");
                return;
            }

            timer = timers.setTimeout(poll, delay);
        };

        // Records the projection the server answered and decides what happens next: keep polling, open the Player, or end.
        const adopt = (next) => {
            const stateChanged = view?.state !== next.state;
            const percentChanged = percentOf(view) !== percentOf(next);
            view = next;
            if (next.state === readyState && mayOpenPlayer()) {
                handOver();
                return;
            }

            if (workingStates.has(next.state)) {
                delay = stateChanged ? firstPollDelayMs : percentChanged ? percentChangeDelayMs : Math.min(maxPollDelayMs, Math.round(delay * pollBackoff));
                publish();
                scheduleNext();
                return;
            }

            clearTimer();
            cancelInFlight();
            holdsIntent = false;
            phase = "ended";
            notice = null;
            publish();
        };

        function poll() {
            timer = null;
            waitedMs += delay;
            const mine = ++generation;
            inFlight = api.getStatus(currentRequestId, episodeId, (error, response) => {
                if (mine !== generation) {
                    return;
                }

                inFlight = null;
                if (!error && response.status === 200 && response.body?.acquisition) {
                    failures = 0;
                    adopt(response.body.acquisition);
                    return;
                }

                // Too many requests is the server asking for room, not a failure: wait as long as it says (bounded) and do not count it.
                if (!error && response.status === 429) {
                    delay = Math.min(maxRetryAfterMs, Math.max(response.retryAfterMs ?? 0, Math.round(delay * pollBackoff), percentChangeDelayMs));
                    scheduleNext();
                    return;
                }

                // A request that is gone or hidden answers 404 for good; anything else may be a moment: retry within the bounds.
                failures = !error && response.status === 404 ? maxConsecutiveFailures : failures + 1;
                if (failures >= maxConsecutiveFailures) {
                    end("unavailable");
                    return;
                }

                delay = Math.min(maxPollDelayMs, Math.round(delay * pollBackoff));
                scheduleNext();
            });
        }

        const answered = (body) => {
            if (body.requestId) {
                currentRequestId = body.requestId;
            }

            if (body.target?.workEpisodeId) {
                episodeId = body.target.workEpisodeId;
            }

            switch (body.outcome) {
                case "play_now":
                    if (mayOpenPlayer()) {
                        handOver();
                    } else {
                        end(null);
                    }

                    return;
                case "acquiring":
                case "awaiting_approval":
                    if (!body.acquisition) {
                        end("unavailable");
                        return;
                    }

                    phase = holdsIntent ? "waiting" : "observing";
                    adopt(body.acquisition);
                    return;
                case "request_required":
                case "target_not_found":
                    // The server resolves a different primary action now (Request, or none): it renders it.
                    end(null);
                    reload();
                    return;
                case "limit_reached":
                    end("limit_reached");
                    return;
                default:
                    end("not_available");
            }
        };

        /** The viewer pressed the action: send the explicit playback intent. The server reuses an equivalent request and never duplicates one. */
        const start = () => {
            if (phase === "starting" || phase === "waiting" || phase === "handingOver") {
                return;
            }

            clearTimer();
            cancelInFlight();
            view = null;
            notice = null;
            failures = 0;
            waitedMs = 0;
            delay = firstPollDelayMs;
            holdsIntent = true;
            phase = "starting";
            publish();
            const mine = ++generation;
            inFlight = api.postIntent(target, (error, response) => {
                if (mine !== generation) {
                    return;
                }

                inFlight = null;
                if (error || response.status < 200 || response.status > 299) {
                    end(!error && response.status === 404 ? "not_available" : "unavailable");
                    return;
                }

                answered(response.body ?? {});
            });
        };

        /** "Stop waiting": only this page's wait ends. Nothing is sent to the server, so no request or acquisition is touched. */
        const stop = () => {
            if (!holdsIntent) {
                return;
            }

            clearTimer();
            cancelInFlight();
            holdsIntent = false;
            phase = "ended";
            notice = "stopped";
            publish();
        };

        /**
         * The viewer left (page hidden, a link followed, the page going away): the transient intent is cleared, polling pauses and an
         * answer still on its way is ignored. A wait whose Player was opening goes back to its action, so a page restored from the
         * back/forward cache never shows a stuck "Starting playback".
         */
        const leave = () => {
            clearTimer();
            // An intent already sent keeps going (the request exists once it answers); a poll is abandoned.
            if (phase !== "starting") {
                cancelInFlight();
            }

            if (phase === "handingOver") {
                holdsIntent = false;
                phase = observeOnly ? "observing" : "idle";
                notice = null;
                view = observeOnly ? view : null;
                publish();
                return;
            }

            if (!holdsIntent) {
                return;
            }

            holdsIntent = false;
            if (phase === "waiting") {
                phase = "observing";
            }

            publish();
        };

        const canPoll = () => phase === "observing" && Boolean(currentRequestId) && !(view && !workingStates.has(view.state)) && !rested && timer === null && inFlight === null && isVisible();

        /** The page is visible again: a viewer who only observes sees the current state at once, and the Player never opens. */
        const resume = () => {
            if (canPoll()) {
                delay = firstPollDelayMs;
                timer = timers.setTimeout(poll, 0);
            }
        };

        /** Another control reads this request now (or stops doing so): a resting wait neither polls nor keeps an answer. */
        const rest = (on) => {
            rested = on;
            if (on) {
                clearTimer();
                cancelInFlight();
                return;
            }

            resume();
        };

        /** Back to the normal action, for example after a notice was dismissed; an observer starts looking again. */
        const reset = () => {
            clearTimer();
            cancelInFlight();
            holdsIntent = false;
            phase = observeOnly ? "observing" : "idle";
            notice = null;
            view = observeOnly ? view : null;
            if (observeOnly && currentRequestId) {
                failures = 0;
                waitedMs = 0;
                delay = firstPollDelayMs;
            }

            publish();
            if (observeOnly) {
                resume();
            }
        };

        // The server just projected the state it was rendered with, so the first look is a poll interval away.
        if (observeOnly && currentRequestId) {
            timer = timers.setTimeout(poll, firstPollDelayMs);
        }

        return Object.freeze({ start, stop, leave, resume, rest, reset, snapshot });
    };

    // The Retry-After of an answer in milliseconds, whether the server sent seconds or a date; null when there is none.
    const retryAfterOf = (response) => {
        const value = response.headers?.get?.("Retry-After");
        if (!value) {
            return null;
        }

        const seconds = Number(value);
        const waitMs = Number.isFinite(seconds) ? seconds * 1000 : Date.parse(value) - Date.now();
        return Number.isFinite(waitMs) && waitMs > 0 ? waitMs : null;
    };

    // The only network this feature uses: the existing client API, same-origin with the page's session, bounded by a timeout.
    const createApi = ({ intentUrl, statusUrl, fetchImpl, timers }) => {
        const send = (url, init, callback) => {
            const controller = typeof AbortController === "function" ? new AbortController() : null;
            const timeout = timers.setTimeout(() => controller?.abort(), requestTimeoutMs);
            fetchImpl(url, { credentials: "same-origin", cache: "no-store", signal: controller?.signal, ...init })
                .then((response) => response.json().catch(() => null).then((body) => ({ status: response.status, body, retryAfterMs: retryAfterOf(response) })))
                .then((result) => callback(null, result), (error) => callback(error ?? new Error("request failed")))
                .finally(() => timers.clearTimeout(timeout));
            return { abort: () => controller?.abort() };
        };

        return {
            postIntent: (target, callback) => send(
                intentUrl,
                { method: "POST", headers: { "Content-Type": "application/json", Accept: "application/json" }, body: JSON.stringify({ target: { workId: target.workId, workEpisodeId: target.workEpisodeId ?? null } }) },
                callback),
            getStatus: (requestId, workEpisodeId, callback) => {
                const url = `${statusUrl.replace("{id}", encodeURIComponent(requestId))}${workEpisodeId ? `?workEpisodeId=${encodeURIComponent(workEpisodeId)}` : ""}`;
                return send(url, { method: "GET", headers: { Accept: "application/json" } }, callback);
            }
        };
    };

    /**
     * What the controls of one page share: at most one wait holding the viewer's intent, one poller per request (the observers beside an
     * action rest while it works), and the page-level events. The DOM binding below only feeds it the events of the browser.
     */
    const createCoordinator = ({ reload }) => {
        const waits = [];
        const observers = [];
        const holders = new Set();
        let active = null;

        return {
            addAction: (wait) => { waits.push(wait); },
            addObserver: (wait) => { waits.push(wait); observers.push(wait); },
            /** The action holds the request (any phase but idle) or lets go of it. */
            holding: (wait, on) => {
                if (on) {
                    holders.add(wait);
                } else {
                    holders.delete(wait);
                }

                observers.forEach((observer) => observer.rest(holders.size > 0));
            },
            /** Pressing a playback action ends the previous wait without a notice. */
            begin: (wait) => {
                if (active && active !== wait) {
                    active.reset();
                }

                active = wait;
                wait.start();
            },
            leaveAll: () => waits.forEach((wait) => wait.leave()),
            resumeAll: () => waits.forEach((wait) => wait.resume()),
            /** A page restored from the back/forward cache shows what it showed when it was left: the server is asked again. */
            pageShown: (persisted) => {
                if (persisted && waits.length > 0) {
                    reload();
                }
            }
        };
    };

    window.JularrInstantPlay = Object.freeze({
        firstPollDelayMs,
        maxPollDelayMs,
        maxWaitMs,
        maxConsecutiveFailures,
        announceStepPercent,
        milestones,
        createWait,
        createApi,
        createCoordinator
    });

    if (typeof document === "undefined" || typeof document.querySelectorAll !== "function") {
        return;
    }

    // ---- Page binding -------------------------------------------------------------------------------------------------

    const text = (() => {
        try {
            return JSON.parse(document.getElementById("instant-play-text")?.textContent || "{}");
        } catch {
            return {};
        }
    })();
    // A missing text or an unknown state is a defect of the page, not something to paper over: it is logged once and the viewer sees a
    // neutral wording instead of a made-up state.
    const warned = new Set();
    const warnOnce = (what) => {
        if (!warned.has(what)) {
            warned.add(what);
            console.warn(`instant-play: ${what}`);
        }
    };
    const label = (key, values = {}) => {
        if (!(key in text)) {
            warnOnce(`no text for ${key}`);
        }

        return Object.entries(values).reduce((result, [name, value]) => result.replace(`{${name}}`, String(value)), text[key] ?? "");
    };

    const stateKeys = {
        waiting_for_approval: "acquisition.state.waitingForApproval",
        looking_for_media: "acquisition.state.lookingForMedia",
        preparing: "acquisition.state.preparing",
        ready_to_watch: "acquisition.state.readyToWatch",
        available: "acquisition.state.available",
        monitoring_future_releases: "acquisition.state.monitoringFutureReleases",
        not_available_yet: "acquisition.state.notAvailableYet",
        not_available: "acquisition.state.notAvailable",
        needs_attention: "acquisition.state.needsAttention",
        rejected: "acquisition.state.rejected"
    };
    const gettingKeys = { episode: "acquisition.state.gettingEpisode", movie: "acquisition.state.gettingMovie", media: "acquisition.state.gettingMedia" };
    const failureKeys = { episode: "acquisition.instant.failed.episode", movie: "acquisition.instant.failed.movie", media: "acquisition.instant.failed.media" };
    const milestoneKeys = { approved: "acquisition.instant.milestone.approved", looking: "acquisition.state.lookingForMedia", found: "acquisition.instant.milestone.found", preparing: "acquisition.instant.milestone.preparing" };

    const stateText = (state, mediaUnit) => {
        if (state === "getting_media") {
            return label(gettingKeys[mediaUnit] ?? gettingKeys.media);
        }

        if (!(state in stateKeys)) {
            warnOnce(`unknown state ${state}`);
            return label("acquisition.instant.unavailable");
        }

        return label(stateKeys[state]);
    };
    const progressText = (state, mediaUnit, percent) => label("acquisition.instant.progress", { state: stateText(state, mediaUnit), percent });
    const workingText = (state, mediaUnit, percent) => (percent === null ? label("acquisition.instant.working", { state: stateText(state, mediaUnit) }) : progressText(state, mediaUnit, percent));

    const timers = { setTimeout: (fn, ms) => window.setTimeout(fn, ms), clearTimeout: (id) => window.clearTimeout(id) };

    // The Player is a page of this application: anything else the data might name is not opened.
    const openPlayer = (href) => {
        const url = new URL(href, window.location.href);
        if (url.origin === window.location.origin) {
            window.location.assign(url.href);
        }
    };

    const coordinator = createCoordinator({ reload: () => window.location.reload() });

    // What the notice of an ended wait says: its title, hint and which actions it offers.
    const noticeOf = (snapshot, mediaUnit) => {
        const notice = (title, hint, tone, actions) => ({ title, hint, tone, actions });
        switch (snapshot.notice) {
            case "stopped":
                return notice(label("acquisition.instant.stopped"), label("acquisition.instant.stoppedHint"), "neutral", ["back"]);
            case "limit_reached":
                // Message only: the action stays as it was, nothing was started.
                return notice("", label("acquisition.playback.limitReached"), "neutral", []);
            case "not_available":
                return notice(label("acquisition.state.notAvailable"), "", "neutral", ["back"]);
            case "unavailable":
                return notice(label("acquisition.instant.unavailable"), label("acquisition.instant.unavailableHint"), "neutral", ["retry", "view", "back"]);
            default:
        }

        switch (snapshot.view?.state) {
            case "needs_attention":
                return notice(label(failureKeys[mediaUnit] ?? failureKeys.media), label("acquisition.instant.failed.hint"), "error", ["retry", "view", "back"]);
            case "not_available_yet":
                return notice(label("acquisition.state.notAvailableYet"), label("acquisition.state.notAvailableYetHint"), "neutral", ["keep", "view", "back"]);
            case "not_available":
                return notice(label("acquisition.state.notAvailable"), "", "neutral", ["view", "back"]);
            case "rejected":
                return notice(label("acquisition.state.rejected"), label("acquisition.instant.rejectedHint"), "neutral", ["view", "back"]);
            case "waiting_for_approval":
                return notice(label("acquisition.state.waitingForApproval"), label("acquisition.instant.waitingHint"), "waiting", ["view", "back"]);
            case "monitoring_future_releases":
                return notice(label("acquisition.state.monitoringFutureReleases"), label("acquisition.instant.monitoringHint"), "waiting", ["view", "back"]);
            default:
                return null;
        }
    };

    const announcementText = (announcement, mediaUnit, noticeInfo) => {
        // An ended wait speaks the notice the viewer reads, not the raw state behind it.
        if (noticeInfo) {
            return [noticeInfo.title, noticeInfo.hint].filter((part) => part !== "").join(". ");
        }

        if (announcement.phase === "handingOver") {
            return label("acquisition.playback.starting");
        }

        if (announcement.phase === "starting") {
            return stateText("looking_for_media", mediaUnit);
        }

        if (announcement.state === "getting_media" && announcement.percent !== null) {
            return label("acquisition.instant.progressAnnounce", { state: stateText("getting_media", announcement.mediaUnit ?? mediaUnit), percent: announcement.percent });
        }

        if (announcement.state) {
            return stateText(announcement.state, announcement.mediaUnit ?? mediaUnit);
        }

        return "";
    };

    const isWorking = (snapshot) => snapshot.phase === "starting" || snapshot.phase === "handingOver" || (snapshot.view !== null && workingStates.has(snapshot.view.state) && snapshot.phase !== "ended");

    // The playback action: the form posts the intent and the button morphs through the states.
    const bindAction = (root) => {
        const form = root.querySelector("[data-ip-form]");
        const button = root.querySelector("[data-ip-button]");
        const labelNode = root.querySelector("[data-ip-label]");
        if (!form || !button || !labelNode) {
            return;
        }

        const progress = root.querySelector("[data-ip-progress]");
        const stopButton = root.querySelector("[data-ip-stop]");
        const details = root.querySelector("[data-ip-details]");
        const milestoneList = root.querySelector("[data-ip-milestones]");
        const icons = root.querySelector("[data-ip-icons]");
        const noticeBox = root.querySelector("[data-ip-notice]");
        const live = root.querySelector("[data-ip-live]");
        const idleText = labelNode.textContent;
        const mediaUnit = root.dataset.ipUnit || "media";
        let lastAnnouncement = 0;
        let wait = null;

        const render = (snapshot) => {
            // The request is read by this wait while it runs; the observers beside the action rest meanwhile.
            coordinator.holding(wait, snapshot.phase !== "idle");

            const working = isWorking(snapshot);
            const noticeInfo = snapshot.phase === "ended" ? noticeOf(snapshot, mediaUnit) : null;
            // A notice with a title replaces the action's state; one without only adds a message beside the unchanged action.
            const replacesAction = noticeInfo !== null && noticeInfo.title !== "";
            const unit = snapshot.view?.mediaUnit ?? mediaUnit;
            root.dataset.ipPhase = snapshot.phase;
            const isReady = snapshot.phase === "ended" && snapshot.view?.state === readyState;
            root.dataset.ipState = replacesAction ? `notice-${noticeInfo.tone}` : working ? "working" : isReady ? "ready" : "idle";

            if (snapshot.phase === "handingOver") {
                labelNode.textContent = label("acquisition.instant.working", { state: label("acquisition.playback.starting") });
            } else if (snapshot.phase === "starting") {
                labelNode.textContent = workingText("looking_for_media", mediaUnit, null);
            } else if (working) {
                labelNode.textContent = workingText(snapshot.view.state, unit, snapshot.percent);
            } else if (replacesAction) {
                labelNode.textContent = noticeInfo.title;
            } else {
                labelNode.textContent = snapshot.phase !== "idle" && snapshot.view ? stateText(snapshot.view.state, unit) : idleText;
            }

            button.setAttribute("aria-disabled", working || replacesAction ? "true" : "false");
            button.setAttribute("aria-busy", working ? "true" : "false");
            if (progress) {
                progress.hidden = snapshot.percent === null || !working;
                progress.style.setProperty("--ip-progress", `${snapshot.percent ?? 0}%`);
            }

            if (stopButton) {
                stopButton.hidden = !snapshot.holdsIntent || snapshot.phase === "handingOver";
            }

            if (details && milestoneList) {
                details.hidden = snapshot.milestones.length === 0 || noticeInfo !== null || snapshot.phase === "handingOver";
                if (details.hidden) {
                    details.open = false;
                }

                milestoneList.replaceChildren(...snapshot.milestones.map((milestone) => {
                    const item = document.createElement("li");
                    item.dataset.status = milestone.status;
                    const icon = icons?.content.querySelector(`[data-ip-icon="${milestone.key}"]`)?.cloneNode(true);
                    if (icon) {
                        icon.removeAttribute("data-ip-icon");
                        item.append(icon);
                    }

                    const textNode = document.createElement("span");
                    if (milestone.key === "getting") {
                        textNode.textContent = milestone.percent === null ? stateText("getting_media", unit) : progressText("getting_media", unit, milestone.percent);
                    } else if (milestone.key === "ready") {
                        textNode.textContent = stateText(snapshot.view?.state === "available" ? "available" : readyState, unit);
                    } else {
                        textNode.textContent = label(milestoneKeys[milestone.key]);
                    }

                    item.append(textNode);
                    return item;
                }));
            }

            if (noticeBox) {
                noticeBox.hidden = noticeInfo === null;
                if (noticeInfo) {
                    noticeBox.dataset.tone = noticeInfo.tone;
                    const hint = noticeBox.querySelector("[data-ip-notice-hint]");
                    hint.textContent = noticeInfo.hint;
                    hint.hidden = noticeInfo.hint === "";
                    for (const action of noticeBox.querySelectorAll("[data-ip-action]")) {
                        action.hidden = !noticeInfo.actions.includes(action.dataset.ipAction);
                    }
                }
            }

            if (live && snapshot.announcement && snapshot.announcement.id !== lastAnnouncement) {
                lastAnnouncement = snapshot.announcement.id;
                live.textContent = announcementText(snapshot.announcement, mediaUnit, noticeInfo);
            }
        };

        wait = createWait({
            target: { workId: root.dataset.ipWorkId, workEpisodeId: root.dataset.ipEpisodeId || null },
            api: createApi({ intentUrl: root.dataset.ipIntentUrl, statusUrl: root.dataset.ipStatusUrl, fetchImpl: (...args) => window.fetch(...args), timers }),
            timers,
            isVisible: () => document.visibilityState !== "hidden",
            watchHref: root.dataset.ipWatchHref || null,
            navigate: openPlayer,
            reload: () => window.location.reload(),
            onChange: render
        });
        coordinator.addAction(wait);

        const begin = () => coordinator.begin(wait);

        form.addEventListener("submit", (event) => {
            event.preventDefault();
            if (button.getAttribute("aria-disabled") !== "true") {
                begin();
            }
        });
        stopButton?.addEventListener("click", () => {
            wait.stop();
            // The button that was pressed is gone: keep the keyboard where it was, on the action that now says Stopped waiting.
            button.focus();
        });
        for (const name of ["retry", "keep"]) {
            root.querySelector(`[data-ip-action=${name}]`)?.addEventListener("click", () => {
                wait.reset();
                begin();
                // The control that was pressed is hidden by now: the keyboard stays on the action.
                button.focus();
            });
        }

        root.querySelector("[data-ip-action=back]")?.addEventListener("click", () => {
            wait.reset();
            button.focus();
        });
    };

    // The state of a request that already exists, shown beside the action: it only follows the server, never opens the Player.
    const bindObserver = (root) => {
        const labelNode = root.querySelector("[data-ip-label]");
        if (!labelNode) {
            return;
        }

        const mediaUnit = root.dataset.ipUnit || "media";
        const wait = createWait({
            target: { workId: root.dataset.ipWorkId, workEpisodeId: root.dataset.ipEpisodeId || null },
            requestId: root.dataset.ipRequestId,
            observeOnly: true,
            api: createApi({ intentUrl: "", statusUrl: root.dataset.ipStatusUrl, fetchImpl: (...args) => window.fetch(...args), timers }),
            timers,
            isVisible: () => document.visibilityState !== "hidden",
            onChange: (snapshot) => {
                // A wait that ended without an answer says so, and the pill itself is the way to look again.
                if (snapshot.phase === "ended" && snapshot.notice === "unavailable") {
                    labelNode.textContent = `${label("acquisition.instant.unavailable")} · ${label("acquisition.instant.retry")}`;
                    root.dataset.ipState = "unavailable";
                    return;
                }

                if (!snapshot.view) {
                    return;
                }

                const unit = snapshot.view.mediaUnit ?? mediaUnit;
                labelNode.textContent = workingStates.has(snapshot.view.state) && snapshot.percent !== null ? progressText(snapshot.view.state, unit, snapshot.percent) : stateText(snapshot.view.state, unit);
                root.dataset.ipState = snapshot.view.state;
                const href = root.dataset.ipWatchHref;
                if (snapshot.view.state === readyState && href) {
                    root.setAttribute("href", href);
                }
            }
        });
        root.addEventListener("click", (event) => {
            if (root.dataset.ipState === "unavailable") {
                event.preventDefault();
                wait.reset();
            }
        });
        coordinator.addObserver(wait);
    };

    for (const root of document.querySelectorAll("[data-instant-play]")) {
        bindAction(root);
    }

    for (const root of document.querySelectorAll("[data-instant-play-observe]")) {
        bindObserver(root);
    }

    // Leaving by any road clears the transient intent: the tab hidden, a link followed, another form submitted, the page going away.
    // Handlers that take over a click or a submit have prevented the default by now and do not leave.
    const followsLink = (event) => {
        const link = event.target instanceof Element ? event.target.closest("a[href]") : null;
        return link !== null && event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey
            && (!link.target || link.target === "_self") && !link.getAttribute("href").startsWith("#");
    };

    document.addEventListener("click", (event) => {
        if (!event.defaultPrevented && followsLink(event)) {
            coordinator.leaveAll();
        }
    });
    document.addEventListener("submit", (event) => {
        if (!event.defaultPrevented) {
            coordinator.leaveAll();
        }
    });
    document.addEventListener("visibilitychange", () => (document.visibilityState === "hidden" ? coordinator.leaveAll() : coordinator.resumeAll()));
    window.addEventListener("pagehide", coordinator.leaveAll);
    window.addEventListener("beforeunload", coordinator.leaveAll);
    window.addEventListener("pageshow", (event) => coordinator.pageShown(event.persisted));
})();
