// What the player does when its stream breaks and the server may have ended it itself (idle expiry, cache policy,
// encoder crash), and when the server advises another quality while playback is healthy. One rule each, loaded before
// episode-player.js, so the decisions are testable without a page.
(() => {
    "use strict";

    const maxRecoveries = 3;
    const stableSeconds = 30;

    // status is { state, reason, recoverable } from GET stream-session, { gone: true } when the session no longer exists, or null
    // when the server could not be asked. The server says whether planning the same mode again makes sense (an encoder crash is
    // a verdict on the mode, so it takes the normal fallback to another mode); the player only bounds the number of attempts.
    const shouldReplanSameMode = (status, recoveries) => {
        if (!status || recoveries >= maxRecoveries) {
            return false;
        }

        return status.gone === true || (status.state === "ended" && status.recoverable === true);
    };

    // The budget of recoveries returns only after real playback: stableSeconds of media actually played since the recovery.
    // A stream that dies seconds after every restart must not respawn ffmpeg forever.
    const recoveriesAfterProgress = (recoveries, playedSeconds) =>
        recoveries > 0 && playedSeconds >= stableSeconds ? 0 : recoveries;

    // Played time grows by the media time between two timeupdate events, but only for small forward steps: a seek jumps
    // by more than maxStepSeconds and a backwards step is no playback, so neither can earn the budget back.
    const maxStepSeconds = 2;
    const accumulatePlayed = (playedSeconds, previousTime, currentTime) => {
        const step = currentTime - previousTime;
        return step > 0 && step <= maxStepSeconds ? playedSeconds + step : playedSeconds;
    };

    // The server advises step_down / step_up in the answer of a telemetry report (#403). The server already paces its advice (a step up
    // needs a stable minute and comes at most every two minutes), so this only bounds a misbehaving or very noisy answer: never while
    // paused or while a plan is being replaced, at least minIntervalMs between two switches and at most maxSwitches in the windowMs; after
    // a plan that failed or was not playable the advice is left alone for backoffMs (the stream that still plays beats a retry loop).
    // The numbers come from the server's one adaptation policy (page data); the client keeps no copy. Following an advice never touches
    // the recovery budget or the failed modes: a stall is not a verdict on a mode.
    const createAdviceGate = ({ minIntervalMs, maxSwitches, windowMs, backoffMs }) => {
        let switches = [];
        let blockedUntil = 0;
        return {
            minIntervalMs,
            maxSwitches,
            windowMs,
            backoffMs,
            blocked: nowMs => nowMs < blockedUntil,
            backOff(nowMs) {
                blockedUntil = nowMs + backoffMs;
            },
            // The switch times still inside the window, oldest first.
            recent(nowMs) {
                switches = switches.filter(at => nowMs - at < windowMs);
                return switches;
            },
            record(nowMs) {
                switches.push(nowMs);
            }
        };
    };

    // True (and the switch recorded) when the advice may be followed now.
    // handedOver: the system player or picture-in-picture owns the picture; whether that survives a source swap is not known, so the stream
    // that plays is left alone.
    const followAdvice = (gate, advice, { paused, busy, handedOver }, nowMs) => {
        // A gate without its numbers (the page did not provide them) follows nothing.
        if ((advice !== "step_down" && advice !== "step_up") || paused || busy || handedOver === true || !Number.isFinite(gate.maxSwitches) || gate.blocked(nowMs)) {
            return false;
        }

        const recent = gate.recent(nowMs);
        if (recent.length >= gate.maxSwitches || (recent.length > 0 && nowMs - recent[recent.length - 1] < gate.minIntervalMs)) {
            return false;
        }

        gate.record(nowMs);
        return true;
    };

    // Asks for the advised plan while the current stream keeps playing and only installs it once it is confirmed playable. Resolves to
    // "swapped", "stale" (another plan took over meanwhile; the orphan session is discarded), "unplayable" (unavailable or without a
    // delivery) or "failed" (the request threw); the last two pause following advice and leave the playing stream untouched.
    // "blocked": the presentation was handed over to the system player or picture-in-picture while the plan was requested; the plan is
    // dropped without a backoff because the situation is temporary.
    const followAdvisedPlan = async ({ advice, requestPlan, isStale, isBlocked, discardOrphan, backOff, warn, install }) => {
        try {
            const response = await requestPlan(advice);
            if (isStale()) {
                discardOrphan(response.sessionId);
                return "stale";
            }

            if (isBlocked()) {
                discardOrphan(response.sessionId);
                return "blocked";
            }

            if (!response.plan || response.plan.mode === "unavailable" || !response.delivery) {
                backOff();
                warn("The advised playback plan is not playable; the current stream keeps playing.", response.plan?.mode);
                return "unplayable";
            }

            install(response);
            return "swapped";
        } catch (error) {
            backOff();
            warn("The advised playback plan could not be requested; the current stream keeps playing.", error);
            return "failed";
        }
    };

    // A fixed tier the server's encoder could not sustain: the selector keeps the viewer's choice and this says what plays instead (the plan's
    // limit), or null when the plan is not limited that way. The caller formats the registered reason text.
    const speedLimitedHint = quality =>
        quality && quality.limitSource === "transcode_speed" && quality.requested !== "auto" && quality.limitKbps > 0
            ? { reason: "transcode_too_slow", limitKbps: quality.limitKbps }
            : null;

    window.JularrStreamRecovery = Object.freeze({
        maxRecoveries,
        stableSeconds,
        shouldReplanSameMode,
        recoveriesAfterProgress,
        accumulatePlayed,
        createAdviceGate,
        speedLimitedHint,
        followAdvice,
        followAdvisedPlan
    });
})();
