// What the player does when its stream breaks and the server may have ended it itself (idle expiry, cache policy,
// encoder crash). One rule, loaded before episode-player.js, so the decision is testable without a page.
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

    // The budget of recoveries returns only after real playback: stableSeconds of media time past the recovery
    // point. A stream that dies seconds after every restart must not respawn ffmpeg forever.
    const recoveriesAfterProgress = (recoveries, mediaSecondsSinceRecovery) =>
        recoveries > 0 && mediaSecondsSinceRecovery >= stableSeconds ? 0 : recoveries;

    window.JularrStreamRecovery = Object.freeze({ maxRecoveries, stableSeconds, shouldReplanSameMode, recoveriesAfterProgress });
})();
