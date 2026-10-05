// What the player does when its stream breaks and the server may have ended it itself (idle expiry, cache policy,
// encoder crash). One rule, loaded before episode-player.js, so the decision is testable without a page.
(() => {
    "use strict";

    const maxRecoveries = 3;
    const stableSeconds = 30;

    // status is { state, reason } from GET stream-session, { gone: true } when the session no longer exists, or null
    // when the server could not be asked. Only a stream the server ended (or lost) is planned again with the same
    // mode. A crashed encoder is a verdict on the mode, so that case takes the normal fallback to another mode.
    const shouldReplanSameMode = (status, recoveries) => {
        if (!status || recoveries >= maxRecoveries) {
            return false;
        }

        return status.gone === true || (status.state === "ended" && status.reason !== "encoder_exited");
    };

    // The budget of recoveries returns only after real playback: stableSeconds of media time past the recovery
    // point. A stream that dies seconds after every restart must not respawn ffmpeg forever.
    const recoveriesAfterProgress = (recoveries, mediaSecondsSinceRecovery) =>
        recoveries > 0 && mediaSecondsSinceRecovery >= stableSeconds ? 0 : recoveries;

    window.JularrStreamRecovery = Object.freeze({ maxRecoveries, stableSeconds, shouldReplanSameMode, recoveriesAfterProgress });
})();
