// What the player observes about its own buffer and what it reports to the server (#403): the media buffered ahead of
// the playhead, stalls, a smoothed receive rate, the startup wait and the telemetry payload. Pure rules with the clock passed
// in, loaded before episode-player.js, so every rule is testable without a page. The server decides plans from this evidence;
// the player only measures it.
(() => {
    "use strict";

    const reportIntervalMs = 5000;
    // A waiting event that clears within this is a decoder or timer blip, not a viewer-visible stall.
    const minStallMs = 250;
    const throughputWindowMs = 15000;
    // A rate sampled longer ago than this no longer describes the connection.
    const throughputStaleMs = 60000;
    // The spinner appears only when playback has been waiting this long, so tiny fluctuations do not flash it.
    const waitingIndicatorDelayMs = 300;
    // A play position this close to the end of a buffered range still counts as inside it.
    const rangeToleranceSeconds = 0.25;

    // TimeRanges (or a test double) as [start, end] pairs in absolute media seconds. A live stream's ranges are relative to
    // the position it started at, so the caller passes that offset.
    const rangesOf = (buffered, offsetSeconds = 0) => {
        const ranges = [];
        for (let index = 0; buffered && index < buffered.length; index++) {
            const start = buffered.start(index) + offsetSeconds;
            const end = buffered.end(index) + offsetSeconds;
            if (Number.isFinite(start) && Number.isFinite(end) && end > start) {
                ranges.push([start, end]);
            }
        }

        return ranges;
    };

    const rangeAt = (ranges, position) =>
        ranges.find(([start, end]) => start <= position + rangeToleranceSeconds && end > position) || null;

    const bufferedEnd = (ranges, position) => rangeAt(ranges, position)?.[1] ?? null;

    const bufferAhead = (ranges, position) => {
        const end = bufferedEnd(ranges, position);
        return end === null ? 0 : Math.max(0, end - position);
    };

    // The buffered ranges as one gradient layer of the timeline: lit inside a range, transparent elsewhere, so several
    // ranges (after seeks in a file) show up exactly where the media is loaded.
    const bufferedGradient = (ranges, durationSeconds, color = "var(--player-buffered, rgba(255,255,255,.6))") => {
        if (!(durationSeconds > 0)) {
            return "linear-gradient(transparent, transparent)";
        }

        const percent = (seconds) => `${(Math.min(Math.max(seconds, 0), durationSeconds) / durationSeconds * 100).toFixed(3)}%`;
        const stops = [];
        let cursor = 0;
        for (const [start, end] of [...ranges].sort((a, b) => a[0] - b[0])) {
            if (end <= cursor) {
                continue;
            }

            const from = Math.max(start, cursor);
            if (from >= durationSeconds) {
                break;
            }

            stops.push(`transparent ${percent(cursor)} ${percent(from)}`, `${color} ${percent(from)} ${percent(end)}`);
            cursor = Math.min(end, durationSeconds);
        }

        return stops.length === 0
            ? "linear-gradient(transparent, transparent)"
            : `linear-gradient(to right, ${stops.join(", ")}, transparent ${percent(cursor)} 100%)`;
    };

    // A stall is playback waiting for media after it had started. Neither the initial start nor a user seek is one, and
    // neither is time spent paused. A waiting that clears within minStallMs is not counted. Counts never go back: a stall that
    // already lasted minStallMs is counted when a seek or a new source ends it, because a report may already have carried it.
    const createStallTracker = () => {
        let started = false;
        let seeking = false;
        let ongoingSince = null;
        let count = 0;
        let totalMs = 0;

        const settle = (nowMs) => {
            if (ongoingSince !== null) {
                const duration = nowMs - ongoingSince;
                if (duration >= minStallMs) {
                    count += 1;
                    totalMs += duration;
                }

                ongoingSince = null;
            }
        };

        return {
            // A new source (first load, live restart after a seek, a new plan) starts over: its startup is not a stall.
            sourceChanged(nowMs) {
                settle(nowMs);
                started = false;
                seeking = false;
            },
            // A new session starts over completely.
            resetSession() {
                started = false;
                seeking = false;
                ongoingSince = null;
                count = 0;
                totalMs = 0;
            },
            seeking(nowMs) {
                settle(nowMs);
                seeking = true;
            },
            // A seek inside buffered media completes without a new playing event; once the element has data again the seek is over,
            // so the next genuine stall is counted.
            seeked(readyState) {
                if (readyState >= 3) {
                    seeking = false;
                }
            },
            waiting(nowMs, paused) {
                if (started && !seeking && !paused && ongoingSince === null) {
                    ongoingSince = nowMs;
                }
            },
            playing(nowMs) {
                settle(nowMs);
                started = true;
                seeking = false;
            },
            // Pausing, ending or failing stops the waiting at that moment; time spent paused or dead is not stall time, and a failed
            // stream never keeps a stall running (nor a session alive through the reported stall time).
            paused(nowMs) {
                settle(nowMs);
            },
            isStalled: () => ongoingSince !== null,
            snapshot(nowMs) {
                const ongoing = ongoingSince !== null ? nowMs - ongoingSince : 0;
                const countsOngoing = ongoing >= minStallMs;
                return {
                    count: count + (countsOngoing ? 1 : 0),
                    totalMs: Math.round(totalMs + (countsOngoing ? ongoing : 0))
                };
            }
        };
    };

    // The rate at which media arrived, from how fast the buffered range grew: growth in media seconds times the bitrate of
    // what is delivered, smoothed over throughputWindowMs. It is a delivery rate, not link capacity: a browser that is not
    // fetching (buffer full, download paused) measures nothing, so the caller marks such samples idle and they are skipped.
    const createThroughputEstimator = () => {
        let lastEnd = null;
        let lastAt = null;
        let value = null;
        let valueAt = null;

        return {
            reset() {
                lastEnd = null;
                lastAt = null;
            },
            observe({ nowMs, bufferedEndSeconds, bitrateKbps, idle }) {
                if (bufferedEndSeconds === null || !(bitrateKbps > 0)) {
                    lastEnd = null;
                    lastAt = null;
                    return;
                }

                const previousEnd = lastEnd;
                const previousAt = lastAt;
                lastEnd = bufferedEndSeconds;
                lastAt = nowMs;
                const elapsedMs = previousAt === null ? 0 : nowMs - previousAt;
                // A shrinking range is a flush or a seek, not a measurement.
                if (previousEnd === null || elapsedMs <= 0 || bufferedEndSeconds < previousEnd || idle) {
                    return;
                }

                const sample = (bufferedEndSeconds - previousEnd) * bitrateKbps / (elapsedMs / 1000);
                const weight = 1 - Math.exp(-elapsedMs / throughputWindowMs);
                value = value === null ? sample : value + weight * (sample - value);
                valueAt = nowMs;
            },
            value(nowMs) {
                return value !== null && nowMs - valueAt <= throughputStaleMs ? Math.round(value) : null;
            }
        };
    };

    // Whether playback is genuinely waiting for media: the viewer asked to play (not paused, not ended), the element has no data to
    // play (readyState below HAVE_FUTURE_DATA) and nothing says it never will: a failed element, a hidden video, or a presentation
    // handed to the system player or picture-in-picture shows no waiting of ours.
    const haveFutureData = 3;
    const isWaitingForMedia = ({ hidden, paused, ended, failed, handedOver, readyState }) =>
        !hidden && !paused && !ended && !failed && !handedOver && readyState < haveFutureData;

    // Shows a state only once it has lasted delayMs and hides it at once.
    const createDelayedIndicator = ({ timers, delayMs, apply }) => {
        let timer = null;
        let shown = false;
        return {
            get shown() {
                return shown;
            },
            set(wanted) {
                if (wanted) {
                    if (!shown && timer === null) {
                        timer = timers.setTimeout(() => {
                            timer = null;
                            shown = true;
                            apply(true);
                        }, delayMs);
                    }

                    return;
                }

                if (timer !== null) {
                    timers.clearTimeout(timer);
                    timer = null;
                }

                if (shown) {
                    shown = false;
                    apply(false);
                }
            }
        };
    };

    const clamp = (value, max) => Math.min(Math.max(value, 0), max);

    // The body of PUT stream-sessions/{id}/telemetry. Numbers are rounded to what a person could read off a diagnostics row.
    const buildReport = ({ sequence, state, bufferAheadSeconds, throughputKbps, stalls, positionSeconds }) => ({
        sequence,
        state,
        bufferAheadSeconds: Math.round(clamp(bufferAheadSeconds, 3600) * 10) / 10,
        throughputKbps: Number.isFinite(throughputKbps) ? Math.round(clamp(throughputKbps, 10000000)) : null,
        stallCount: Math.min(stalls.count, 100000),
        stallTotalMs: Math.round(stalls.totalMs),
        positionSeconds: Math.round(clamp(positionSeconds, 604800) * 10) / 10
    });

    // A paused player reports once, when it pauses, and is silent until it plays again: a silent session is an idle one.
    const shouldReport = (state, lastReportedState) => state !== "paused" || lastReportedState !== "paused";

    window.JularrPlayerBuffering = Object.freeze({
        reportIntervalMs,
        minStallMs,
        throughputWindowMs,
        waitingIndicatorDelayMs,
        rangesOf,
        bufferedEnd,
        bufferAhead,
        bufferedGradient,
        createStallTracker,
        createThroughputEstimator,
        createDelayedIndicator,
        isWaitingForMedia,
        buildReport,
        shouldReport
    });
})();
