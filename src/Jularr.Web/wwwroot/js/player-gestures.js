// DOM-independent touch and mouse gesture decisions for the shared video surface.
(() => {
    const createTapDecider = (options = {}) => {
        const delayMs = options.delayMs ?? 280;
        const seriesMs = options.seriesMs ?? 700;
        const edge = options.edgeFraction ?? 0.35;
        const steps = { back: options.backSeconds, forward: options.forwardSeconds };
        if (!(steps.back > 0) || !(steps.forward > 0)) {
            throw new TypeError("createTapDecider needs positive backSeconds and forwardSeconds.");
        }
        let pending = null;
        let series = null;

        const zoneOf = fraction =>
            fraction < edge ? "back" : fraction > 1 - edge ? "forward" : "center";

        return Object.freeze({
            delayMs,

            // `fraction` is the tap's horizontal position in the video (0 = left edge).
            // "wait" means: call settle() once delayMs has passed.
            tap(fraction, now) {
                const zone = zoneOf(Number.isFinite(fraction) ? fraction : 0.5);

                if (series && series.zone === zone && now - series.at <= seriesMs) {
                    series.total += steps[zone];
                    series.at = now;
                    pending = null;
                    return { action: "seek", zone, seconds: steps[zone], total: series.total };
                }

                series = null;
                if (pending && pending.zone === zone && now - pending.at <= delayMs) {
                    pending = null;
                    if (zone === "center") {
                        return { action: "doubleTapCenter", zone };
                    }

                    series = { zone, total: steps[zone], at: now };
                    return { action: "seek", zone, seconds: steps[zone], total: steps[zone] };
                }

                pending = { zone, at: now };
                return { action: "wait", zone };
            },

            // A tap that was not followed by a second one toggles the controls.
            settle(now) {
                if (pending && now - pending.at >= delayMs) {
                    pending = null;
                    return { action: "toggleControls" };
                }

                return { action: "none" };
            },

            reset() {
                pending = null;
                series = null;
            }
        });
    };

    const createMouseClickDecider = (options = {}) => {
        const delayMs = options.delayMs ?? 280;
        const maxDistancePx = options.maxDistancePx ?? 18;
        let pending = null;

        return Object.freeze({
            delayMs,

            click(x, y, now) {
                const previous = pending;
                pending = null;

                if (previous && now - previous.at <= delayMs &&
                    Math.hypot(x - previous.x, y - previous.y) <= maxDistancePx) {
                    return { action: "fullscreen" };
                }

                pending = { x, y, at: now };
                return { action: previous ? "playPauseAndWait" : "wait" };
            },

            settle(now) {
                if (pending && now - pending.at >= delayMs) {
                    pending = null;
                    return { action: "playPause" };
                }

                return { action: "none" };
            }
        });
    };

    window.JularrPlayerGestures = Object.freeze({ createTapDecider, createMouseClickDecider });
})();
