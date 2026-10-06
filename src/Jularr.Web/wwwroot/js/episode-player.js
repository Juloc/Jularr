(() => {
    const root = document.querySelector("[data-episode-player]");
    if (!root) {
        return;
    }

    const design = window.JularrPlayerDesign;
    if (!design) {
        return;
    }

    const profileId = document.body?.dataset.profileId || "unknown";
    // Device-local choices (mode override, quality) live in this browser only.
    const preferenceKey = `anilingo.profile.${profileId}.playbackMode`;
    const legacyQualityKey = `anilingo.profile.${profileId}.qualityCap`;
    const qualityKey = `anilingo.profile.${profileId}.qualityPreset`;
    const progressUrl = root.dataset.progressUrl || "";
    let offlineMediaUrl = "";
    const planUrl = root.dataset.playbackPlanUrl || "";
    // A Movie or Series page plays a canonical target (a Work, or a WorkEpisode within it): the plan, progress and
    // bootstrap routes then take that target in the request body instead of a legacy episode id in the address.
    const videoTarget = root.dataset.videoTargetWork
        ? { workId: root.dataset.videoTargetWork, workEpisodeId: root.dataset.videoTargetEpisode || null }
        : null;
    const targetBody = videoTarget ? { target: videoTarget } : {};
    const persistedResumeSeconds = Number(root.dataset.resumeSeconds);
    const completionThreshold = Number(root.dataset.completionThreshold);
    const video = root.querySelector("[data-playback-video]");
    const stage = root.querySelector("[data-video-stage]");
    const placeholder = root.querySelector("[data-playback-placeholder]");
    const playbackStatus = root.querySelector("[data-playback-status]");
    const playbackSummary = root.querySelector("[data-playback-summary]");
    const playbackBadge = root.querySelector("[data-playback-badge]");
    const modeSelect = root.querySelector("[data-playback-mode]");
    const reasonsBlock = root.querySelector("[data-playback-reasons]");
    const reasonList = root.querySelector("[data-playback-reason-list]");
    const diagnosticsBlock = root.querySelector("[data-playback-diagnostics]");
    const diagnosticsList = root.querySelector("[data-playback-diagnostics-list]");
    const capabilityProbe = window.JularrPlaybackCapabilities;
    const text = (() => {
        try {
            return JSON.parse(root.querySelector("[data-player-text]")?.textContent || "{}");
        } catch {
            return {};
        }
    })();

    const overlay = root.querySelector("[data-subtitle-overlay]");
    const data = root.querySelector("[data-cue-data]");
    const inspector = root.querySelector("[data-word-inspector]");
    const learningKicker = root.querySelector("[data-learning-kicker]");
    const word = root.querySelector("[data-word]");
    const reading = root.querySelector("[data-reading]");
    const meaning = root.querySelector("[data-meaning]");
    const state = root.querySelector("[data-state]");
    const replay = root.querySelector("[data-replay]");
    const closeLearning = root.querySelector("[data-close-learning]");
    const error = root.querySelector("[data-player-error]");
    const timeline = root.querySelector("[data-playback-timeline]");
    const timelineCurrent = root.querySelector("[data-playback-current]");
    const timelineDuration = root.querySelector("[data-playback-duration]");
    const storageActions = root.querySelector("[data-storage-actions]");
    const storageRetry = root.querySelector("[data-storage-retry]");
    const storageWake = root.querySelector("[data-storage-wake]");
    const failureActions = root.querySelector("[data-failure-actions]");
    const failureRetry = root.querySelector("[data-failure-retry]");
    // The failure text names what is being played: a movie is not an episode.
    const failedKey = root.dataset.videoKind === "movie" ? "playback.status.failedMovie" : "playback.status.failed";

    const nextUrl = root.dataset.nextUrl || "";
    const preferencesUrl = root.dataset.playbackPreferencesUrl || "";
    let autoplayNext = root.dataset.autoplayNext === "true";
    const restartButton = root.querySelector("[data-restart]");
    const autoplayToggle = root.querySelector("[data-autoplay-toggle]");
    const postPlay = root.querySelector("[data-post-play]");
    const postPlayReplay = root.querySelector("[data-post-play-replay]");
    const postPlayCountdown = root.querySelector("[data-post-play-countdown]");
    const postPlayCancel = root.querySelector("[data-post-play-cancel]");
    const autoplayDelaySeconds = 10;

    const playbackSubtitle = root.querySelector("[data-playback-subtitle]");
    const speedSelect = root.querySelector("[data-playback-speed]");
    const audioSelect = root.querySelector("[data-audio-track]");
    const subtitleSelect = root.querySelector("[data-subtitle-track]");
    const qualitySelect = root.querySelector("[data-quality-cap]");
    const qualityHint = root.querySelector("[data-quality-hint]");
    const saveDefaults = root.querySelector("[data-save-playback-defaults]");
    const repeatLineButton = root.querySelector("[data-repeat-line]");
    const controlsData = (() => {
        try {
            return JSON.parse(root.querySelector("[data-player-controls-data]")?.textContent || "{}");
        } catch {
            return {};
        }
    })();
    const seekSeconds = design.seekSeconds(root);

    if (!video || !stage || !placeholder || !playbackStatus ||
        !playbackSummary || !playbackBadge || !modeSelect || !overlay || !data ||
        !timeline || !timelineCurrent || !timelineDuration) {
        return;
    }

    // The learning sheet is only rendered when player learning tools are
    // enabled for this scope; normal playback must work without it.
    const learningTools = Boolean(
        inspector && learningKicker && word && reading && meaning && state &&
        replay && closeLearning);

    let durationSeconds = Number(root.dataset.durationSeconds);
    let hasKnownDuration = Number.isFinite(durationSeconds) && durationSeconds > 0;

    const readStored = (key) => {
        try {
            return window.localStorage.getItem(key);
        } catch {
            return null;
        }
    };

    const store = (key, value) => {
        try {
            window.localStorage.setItem(key, value);
        } catch {
        }
    };

    // Earlier player versions stored "device"/"server"; device-only maps onto Direct only.
    const modePreferences = ["auto", "direct_only", "always_transcode"];
    const readPreference = () => {
        const value = readStored(preferenceKey);
        return value === "device" ? "direct_only" : modePreferences.includes(value) ? value : "auto";
    };

    const qualityPresets = ["auto", "original", "20mbps", "12mbps", "8mbps", "4mbps", "2mbps", "1mbps"];
    const legacyQuality = { "1080p": "8mbps", "720p": "4mbps", low: "2mbps" };
    // No stored choice means the server's network default (Original at home, Automatic away).
    const readQualityPreset = () => {
        const value = readStored(qualityKey) ?? legacyQuality[readStored(legacyQualityKey)] ?? null;
        return qualityPresets.includes(value) ? value : null;
    };

    // Session-only selections: they survive fallback and stream restarts of
    // this page but are never stored; "Save as my defaults" writes the
    // profile-scoped preference instead.
    let selectedAudioTrackId = audioSelect?.value || controlsData.initialAudioTrackId || null;
    let qualityPreset = readQualityPreset();
    let playbackSpeed = Number(speedSelect?.value) > 0 ? Number(speedSelect.value) : 1;
    let subtitleChoice = subtitleSelect?.value || "learning";
    if (qualitySelect) {
        qualitySelect.value = qualityPreset || "auto";
    }

    // The resolved plan of the current playback session. The server decides; this player
    // only follows the plan and reports failures and its selections back.
    let plan = null;
    let planCapabilitiesInferred = false;
    let delivery = null;
    let streamSessionId = null;
    let planGeneration = 0;
    const failedModes = new Set();
    // The server ends a stream itself when it sits idle or the cache policy needs room. That says nothing against the mode,
    // so the same mode is planned again (see player-recovery.js for the bounds).
    const streamRecovery = window.JularrStreamRecovery;
    // What this player observes about its buffer (ranges, stalls, receive rate) and reports to the server (player-buffering.js).
    const buffering = window.JularrPlayerBuffering;
    const stalls = buffering.createStallTracker();
    const throughput = buffering.createThroughputEstimator();
    // How often the player may follow the server's quality advice (player-recovery.js); the server paces its advice, this bounds a bad one.
    const adviceGate = streamRecovery.createAdviceGate(JSON.parse(root.dataset.adviceGate || "{}"));
    // What the server measured while converting this session's video, from the telemetry answers; shown in the diagnostics only.
    let transcodeReading = null;
    let sessionRecoveries = 0;
    let playedSinceRecovery = 0;
    let lastPlayedTime = 0;
    const streamIsLive = () => delivery !== null && delivery.transport !== "file";

    const readSceneStartSeconds = () => {
        const value = new URL(window.location.href).searchParams.get("at");
        if (value === null || !/^\d+$/.test(value)) {
            return null;
        }

        const milliseconds = Number(value);
        if (!Number.isSafeInteger(milliseconds) || milliseconds < 0) {
            return null;
        }

        return milliseconds / 1000;
    };

    let preference = readPreference();
    const sceneStartSeconds = readSceneStartSeconds();
    let pendingResumeTime = sceneStartSeconds !== null
        ? sceneStartSeconds
        : Number.isFinite(persistedResumeSeconds) && persistedResumeSeconds > 0
            ? persistedResumeSeconds
            : null;
    let resumeShouldPlay = false;
    let streamStartSeconds = 0;
    let loadedStreamLive = false;
    let timelinePreviewing = false;
    let playbackWasRequested = false;
    let storageState = root.dataset.storageState || "unknown";
    let storageRetryStartedAt = null;
    let storageRetryAttempt = 0;
    let storageRetryTimer = null;
    let storageRecoveryActive = false;
    // Canonical storage health from the server; a sleeping (Wake-on-LAN) NAS is only started
    // once playback is requested, never by merely opening the page.
    let storageHealth = root.dataset.storageHealth || "";
    let storageDiagnostic = "";
    let storageWakeRequested = false;
    modeSelect.value = preference;

    const clampToDuration = (seconds) => {
        const safe = Number.isFinite(seconds) ? Math.max(0, seconds) : 0;
        if (!hasKnownDuration) {
            return safe;
        }

        return Math.min(safe, Math.max(0, durationSeconds - 0.05));
    };

    const formatTime = (seconds) => {
        if (!Number.isFinite(seconds) || seconds < 0) {
            return "--:--";
        }

        const totalSeconds = Math.floor(seconds);
        const hours = Math.floor(totalSeconds / 3600);
        const minutes = Math.floor((totalSeconds % 3600) / 60);
        const remainder = totalSeconds % 60;

        if (hours > 0) {
            return `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`;
        }

        return `${minutes}:${String(remainder).padStart(2, "0")}`;
    };

    const absoluteCurrentTime = () => {
        const localTime = Number.isFinite(video.currentTime) ? video.currentTime : 0;
        const absolute = loadedStreamLive
            ? streamStartSeconds + localTime
            : localTime;
        return clampToDuration(absolute);
    };

    // The buffered ranges of the element in absolute media time: a live stream's ranges start at the position it was started at.
    const bufferedRanges = () => buffering.rangesOf(video.buffered, loadedStreamLive ? streamStartSeconds : 0);
    const bufferAheadSeconds = () => buffering.bufferAhead(bufferedRanges(), absoluteCurrentTime());

    // The buffered media as its own layer of the timeline, behind the played fill (player.css), and its end for assistive technology.
    const renderBuffered = () => {
        timeline.style.setProperty("--buffered-ranges", buffering.bufferedGradient(bufferedRanges(), hasKnownDuration ? durationSeconds : 0));
    };

    // The slider's spoken value: where it is and how far media is loaded; the same while scrubbing and while playing.
    const describeTimeline = (position) => {
        const loadedUntil = buffering.bufferedEnd(bufferedRanges(), position);
        if (loadedUntil !== null && loadedUntil > position) {
            timeline.setAttribute("aria-valuetext", format("playback.timeline.valueText", {
                position: formatTime(position),
                duration: formatTime(durationSeconds),
                buffered: formatTime(loadedUntil)
            }));
        } else {
            timeline.removeAttribute("aria-valuetext");
        }
    };

    const updateTimeline = () => {
        if (!hasKnownDuration) {
            timeline.disabled = true;
            timelineDuration.textContent = "--:--";
            return;
        }

        timeline.disabled = false;
        timeline.max = String(durationSeconds);
        timelineDuration.textContent = formatTime(durationSeconds);

        if (!timelinePreviewing) {
            const current = absoluteCurrentTime();
            timeline.value = String(current);
            timelineCurrent.textContent = formatTime(current);
            describeTimeline(current);
        }
    };

    let lastProgressSentAt = Date.now();
    let lastProgressPositionMs = -1;

    // The server never infers completion from a position, so a seek or scrub that lands at or beyond the threshold
    // stays a resume point and only `ended` (or Mark watched) completes it. Threshold completion is declared only
    // after playback itself crossed the threshold: naturalPositionMs follows the position while it advances in
    // small playback steps, a jump (seek) clears it together with the crossing.
    const naturalStepMs = 2500;
    let naturalPositionMs = -1;
    let lastClockPositionMs = -1;
    let crossedThresholdByPlayback = false;

    const trackNaturalPlayback = () => {
        const positionMs = Math.round(absoluteCurrentTime() * 1000);
        const stepMs = positionMs - lastClockPositionMs;
        lastClockPositionMs = positionMs;
        if (video.seeking || stepMs < 0 || stepMs > naturalStepMs * playbackSpeed) {
            naturalPositionMs = -1;
            crossedThresholdByPlayback = false;
            return;
        }

        if (video.paused) {
            return;
        }

        const thresholdMs = hasKnownDuration && Number.isFinite(completionThreshold)
            ? durationSeconds * 1000 * completionThreshold
            : Infinity;
        if (naturalPositionMs >= 0 && naturalPositionMs < thresholdMs && positionMs >= thresholdMs) {
            crossedThresholdByPlayback = true;
        }

        naturalPositionMs = positionMs;
    };

    const reachedCompletionNaturally = (positionMs) =>
        crossedThresholdByPlayback &&
        naturalPositionMs >= 0 &&
        Math.abs(positionMs - naturalPositionMs) <= naturalStepMs * playbackSpeed;

    // Bounded checkpoints: at most one regular write per 15 seconds while
    // playing; pause, end, restart and page close flush immediately.
    const persistProgress = (completed = false, force = false) => {
        if (!progressUrl) {
            return;
        }

        const positionMs = Math.max(0, Math.round(absoluteCurrentTime() * 1000));
        const now = Date.now();

        if (!force && now - lastProgressSentAt < 15000) {
            return;
        }

        if (!force && positionMs === lastProgressPositionMs) {
            return;
        }

        sendProgress(positionMs, completed || reachedCompletionNaturally(positionMs), force);
    };

    const sendProgress = (positionMs, completed, keepalive) => {
        const durationMs = hasKnownDuration
            ? Math.max(0, Math.round(durationSeconds * 1000))
            : null;

        lastProgressSentAt = Date.now();
        lastProgressPositionMs = positionMs;

        void fetch(progressUrl, {
            method: "PUT",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                ...targetBody,
                positionMs,
                durationMs,
                completed
            }),
            keepalive
        }).then(response => {
            if (!response.ok) throw new Error("Progress sync failed.");
        }).catch(() => {
            window.dispatchEvent(new CustomEvent("jularr:episode-progress", {
                detail: { episodeId: root.dataset.episodeId, positionMs, durationMs, completed }
            }));
        });
    };

    // A new source resets playbackRate to defaultPlaybackRate, so both are set.
    const applySpeed = () => {
        video.defaultPlaybackRate = playbackSpeed;
        video.playbackRate = playbackSpeed;
    };

    const format = (key, values = {}) =>
        (text[key] || "").replace(/\{([A-Za-z0-9_]+)\}/g, (_, name) =>
            name in values ? displayName(String(values[name])) : "?");

    const codecNames = {
        h264: "H.264", hevc: "HEVC", av1: "AV1", vp9: "VP9", vp8: "VP8", mpeg4: "MPEG-4",
        aac: "AAC", mp3: "MP3", opus: "Opus", flac: "FLAC", ac3: "AC-3", eac3: "E-AC-3",
        dts: "DTS", truehd: "TrueHD", vorbis: "Vorbis", alac: "ALAC",
        matroska: "MKV", mp4: "MP4", webm: "WebM", mpegts: "MPEG-TS", avi: "AVI"
    };
    const displayName = (value) => codecNames[value?.toLowerCase?.()] || value;

    const modeLabel = (mode) => text[`playback.mode.${mode}`] || mode;

    // Compact status for normal users: "Direct Play · HEVC · 1080p · AAC".
    const compactStatus = () => {
        if (!plan || plan.mode === "unavailable") {
            return modeLabel("unavailable");
        }

        const parts = [modeLabel(plan.mode)];
        const output = plan.video;
        if (output) {
            parts.push(output.copy || !output.sourceCodec || output.sourceCodec === output.outputCodec
                ? displayName(output.outputCodec)
                : `${displayName(output.sourceCodec)} → ${displayName(output.outputCodec)}`);
            const height = output.copy
                ? output.sourceHeight
                : Math.min(output.sourceHeight || Infinity, output.maxOutputHeight || Infinity);
            if (Number.isFinite(height)) {
                parts.push(`${height}p`);
            }
        }

        if (plan.audio) {
            parts.push(displayName(plan.audio.outputCodec));
        }

        return parts.join(" · ");
    };

    // "Why not Direct Play?": the reasons that ruled out the untouched file (and, for a
    // transcode, the remux), then at most two notes. Never a text wall.
    const renderReasons = () => {
        if (!reasonsBlock || !reasonList) {
            return;
        }

        reasonList.replaceChildren();
        if (!plan) {
            reasonsBlock.hidden = true;
            return;
        }

        const shown = new Set();
        const lines = [];
        const add = (reason) => {
            const key = `${reason.code}|${JSON.stringify(reason.values || {})}`;
            if (shown.has(reason.code) || shown.has(key) || !text[`playback.reason.${reason.code}`]) {
                return;
            }

            shown.add(reason.code);
            lines.push(format(`playback.reason.${reason.code}`, reason.values || {}));
        };

        const reasons = plan.reasons || [];
        reasons.filter(x => x.rulesOut === "direct_play").forEach(add);
        if (plan.mode === "transcode" || plan.mode === "unavailable") {
            reasons.filter(x => x.rulesOut === "direct_stream" || x.rulesOut === "transcode").forEach(add);
        }

        const blockers = lines.length;
        reasons.filter(x => !x.rulesOut && x.severity !== "info").slice(0, 2).forEach(add);
        if (plan.mode !== "direct_play") {
            reasons.filter(x => !x.rulesOut && x.severity === "info").slice(0, 2).forEach(add);
        }

        for (const line of lines.slice(0, 6)) {
            const item = document.createElement("li");
            item.textContent = line;
            reasonList.append(item);
        }

        const heading = reasonsBlock.querySelector("[data-playback-reasons-heading]");
        if (heading) {
            heading.hidden = blockers === 0;
        }

        reasonsBlock.hidden = lines.length === 0;
    };

    const setBadge = () => {
        playbackBadge.classList.remove("status-ok", "status-warning", "status-error");
        if (!plan) {
            playbackBadge.classList.add("status-warning");
            playbackBadge.textContent = text["playback.status.checking"] || "…";
            return;
        }

        playbackBadge.classList.add(
            plan.mode === "direct_play" ? "status-ok" : plan.mode === "unavailable" ? "status-error" : "status-warning");
        playbackBadge.textContent = modeLabel(plan.mode);
    };

    // Folded "Playback details" in the settings panel: source → delivered, how sure the
    // device support is, what the server does, and the live buffer/dropped frames.
    const mbps = (kbps) => Number.isFinite(kbps) && kbps > 0
        ? `${(kbps / 1000).toFixed(kbps >= 10000 ? 0 : 1)} Mbps`
        : null;
    const channelLayout = (channels) =>
        ({ 1: "1.0", 2: "2.0", 6: "5.1", 8: "7.1" })[channels] || (channels ? `${channels} ch` : null);
    const bitDepth = (pixelFormat) =>
        /p12/.test(pixelFormat || "") ? "12-bit" : /p10|p010/.test(pixelFormat || "") ? "10-bit" : null;
    const qualityLabel = (preset) => {
        const step = /^mbps(\d+)$/.exec(preset || "");
        return step
            ? format("playback.quality.mbps", { value: step[1] })
            : text[`playback.quality.${preset}`] || preset;
    };
    const joined = (...parts) => parts.filter(Boolean).join(" · ");

    // "PGS · Burned in by the server" / "ASS · Shown by the player"; nothing while subtitles are off.
    const subtitleDiagnostic = () => {
        if (plan?.subtitle) {
            return joined(plan.subtitle.format, text[`playback.subtitle.${plan.subtitle.delivery}`]);
        }

        const option = subtitleSelect?.selectedOptions[0];
        return option && option.value !== "off"
            ? joined(option.dataset.format || null, text["playback.subtitle.client"])
            : null;
    };

    const diagnosticRows = () => {
        const output = plan.video;
        const audio = plan.audio;
        const quality = plan.quality || {};
        const hdr = output?.sourceDynamicRange && output.sourceDynamicRange !== "SDR" ? output.sourceDynamicRange : null;
        const rows = [
            ["mode", modeLabel(plan.mode)],
            ["source", joined(
                displayName(plan.sourceContainer),
                output && joined(displayName(output.sourceCodec), bitDepth(output.sourcePixelFormat), hdr),
                output?.sourceWidth && output?.sourceHeight ? `${output.sourceWidth}×${output.sourceHeight}` : null,
                mbps(quality.sourceBitrateKbps))]
        ];

        if (plan.mode === "direct_play") {
            rows.push(["delivered", text["playback.diagnostics.untouched"]]);
        } else if (plan.mode !== "unavailable") {
            const height = output && (output.copy ? output.sourceHeight : output.maxOutputHeight);
            rows.push(["delivered", joined(
                `${displayName(plan.container)}${plan.transport === "hls" ? " (HLS)" : ""}`,
                output && displayName(output.outputCodec),
                height ? `${height}p` : null,
                mbps(quality.deliveredBitrateKbps))]);
        }

        if (audio) {
            const source = joined(displayName(audio.sourceCodec), channelLayout(audio.sourceChannels));
            rows.push(["audio", joined(
                audio.copy ? source : `${source} → ${joined(displayName(audio.outputCodec), channelLayout(audio.outputChannels))}`,
                audio.language)]);
        }

        rows.push(["quality", joined(
            qualityLabel(quality.requested),
            text[`playback.network.${quality.network}`],
            quality.limitKbps ? `≤ ${mbps(quality.limitKbps)}` : null)]);
        rows.push(["support", joined(
            text[`playback.support.${plan.confidence}`] || plan.confidence,
            planCapabilitiesInferred ? text["playback.diagnostics.inferredDocument"] : null)]);
        rows.push(["subtitles", subtitleDiagnostic()]);
        rows.push(["processing", plan.mode === "direct_stream"
            ? text["playback.diagnostics.processingRemux"]
            : plan.mode === "transcode"
                ? format("playback.diagnostics.processingTranscode", { encoder: output?.encoder || "ffmpeg" })
                : text["playback.diagnostics.processingNone"]]);

        if (!video.hidden) {
            // What the player really has (buffer ahead, stalls, receive rate) next to what the plan asks for (buffer target).
            const nowMs = performance.now();
            rows.push(["buffer", format("playback.diagnostics.seconds", { value: Math.round(bufferAheadSeconds()) })]);
            if (plan.buffer) {
                rows.push(["bufferPolicy", format("playback.diagnostics.bufferPolicyValue", {
                    preset: text[`playback.buffer.${plan.buffer.preset}`] || plan.buffer.preset,
                    target: plan.buffer.targetAheadSeconds,
                    low: plan.buffer.lowWaterSeconds,
                    startup: plan.buffer.startupSeconds
                })]);
            }

            const stallSummary = stalls.snapshot(nowMs);
            rows.push(["stalls", format("playback.diagnostics.stallsValue", { count: stallSummary.count, seconds: (stallSummary.totalMs / 1000).toFixed(1) })]);
            rows.push(["throughput", mbps(throughput.value(nowMs))]);
            if (transcodeReading) {
                rows.push(["transcodeSpeed", joined(
                    format("playback.diagnostics.speedValue", { speed: transcodeReading.speed }),
                    transcodeReading.fps ? format("playback.diagnostics.fpsValue", { fps: transcodeReading.fps }) : null)]);
            }
            const frames = typeof video.getVideoPlaybackQuality === "function" ? video.getVideoPlaybackQuality() : null;
            if (frames && frames.totalVideoFrames > 0) {
                rows.push(["droppedFrames", `${frames.droppedVideoFrames} / ${frames.totalVideoFrames}`]);
            }
        }

        return rows.filter(([, value]) => value);
    };

    const renderDiagnostics = () => {
        if (!diagnosticsBlock || !diagnosticsList) {
            return;
        }

        diagnosticsBlock.hidden = !plan;
        if (!plan) {
            diagnosticsList.replaceChildren();
            return;
        }

        const items = [];
        for (const [name, value] of diagnosticRows()) {
            const term = document.createElement("dt");
            term.textContent = text[`playback.diagnostics.${name}`] || name;
            const detail = document.createElement("dd");
            detail.textContent = value;
            items.push(term, detail);
        }

        diagnosticsList.replaceChildren(...items);
    };

    // Buffer and dropped frames change while playing; refresh them only while the details are open.
    let diagnosticsTimer = null;
    diagnosticsBlock?.addEventListener("toggle", () => {
        window.clearInterval(diagnosticsTimer);
        diagnosticsTimer = diagnosticsBlock.open ? window.setInterval(renderDiagnostics, 2000) : null;
        renderDiagnostics();
    });

    // The small note under the subtitle menu: picture subtitles are burned in by the server.
    const subtitleHint = root.querySelector("[data-subtitle-hint]");
    const renderSubtitleHint = () => {
        if (!subtitleHint) {
            return;
        }

        const trackId = burnInSubtitleTrackId();
        const delivery = plan?.subtitle && `stream:${plan.subtitle.streamIndex}` === trackId
            ? plan.subtitle.delivery
            : null;
        subtitleHint.textContent = !trackId
            ? ""
            : delivery === "burn_in"
                ? text["playback.subtitle.burnInHint"] || ""
                : delivery === "unavailable"
                    ? text["playback.reason.subtitle_burn_in_unavailable"] || ""
                    : text["playback.subtitle.preparingHint"] || "";
    };

    const renderPlan = () => {
        setBadge();
        playbackSummary.textContent = plan ? compactStatus() : text["playback.status.checking"] || "";
        renderReasons();
        renderDiagnostics();
        renderSubtitleHint();
        renderQualityHint();
        // Not "playbackMode": data-playback-mode is the mode selector inside this root.
        root.dataset.playbackDelivery = plan?.mode || "";
    };

    // A fixed tier the server's encoder could not sustain: the selector keeps the viewer's choice, this note says what plays instead and why.
    let qualityHintFromPlan = false;
    const renderQualityHint = () => {
        if (!qualityHint) {
            return;
        }

        const hint = streamRecovery.speedLimitedHint(plan?.quality);
        if (hint) {
            qualityHint.textContent = format(`playback.reason.${hint.reason}`, { limit: mbps(hint.limitKbps) });
            qualityHintFromPlan = true;
        } else if (qualityHintFromPlan) {
            qualityHint.textContent = "";
            qualityHintFromPlan = false;
        }
    };

    const hideVideo = () => {
        video.pause();
        video.hidden = true;
        placeholder.hidden = false;
        stage.classList.add("player-placeholder");
    };

    window.addEventListener("jularr:offline-media-ready", event => {
        const pkg = event.detail?.package;
        if (pkg?.id === `episode:${root.dataset.episodeId || ""}`) {
            offlineMediaUrl = event.detail.url || "";
            video.querySelectorAll("track[data-offline-media-track]").forEach(track => track.remove());
            for (const resource of pkg.resources || []) {
                if (resource.type !== "subtitle") continue;
                const track = document.createElement("track");
                track.kind = "subtitles";
                track.label = resource.fileName || "Subtitle";
                track.srclang = "und";
                track.src = window.JularrOfflineMediaManager?.localUrl(profileId, pkg.id, resource.id) || "";
                track.dataset.offlineMediaTrack = "true";
                video.append(track);
            }
            video.dataset.playbackSource = "";
            if (offlineMediaUrl) loadSource(0);
        }
    });

    const buildMediaUrl = (startSeconds) => {
        if (offlineMediaUrl) return offlineMediaUrl;
        const url = new URL(delivery.url, window.location.origin);
        if (streamIsLive() && delivery.startParameter && startSeconds > 0) {
            url.searchParams.set(delivery.startParameter, startSeconds.toFixed(3));
        }

        return url.toString();
    };

    // The spinner is shown only while playback is genuinely waiting for media (the first data after Play, a stall, a seek that has not
    // delivered data) and only after it lasted a moment. The element is never paused on purpose to wait: a paused element loads only a couple of seconds, so
    // such a wait would delay the start without loading anything more.
    const spinner = root.querySelector("[data-player-spinner]");
    const waitingIndicator = buffering.createDelayedIndicator({
        timers: window,
        delayMs: buffering.waitingIndicatorDelayMs,
        apply: (shown) => {
            if (spinner) {
                spinner.hidden = !shown;
            }
        }
    });
    // The system player and picture-in-picture own the picture while handed over; the presentation handler below keeps this current.
    let presentationHandedOver = false;
    const syncWaiting = () => waitingIndicator.set(buffering.isWaitingForMedia({
        hidden: video.hidden,
        paused: video.paused,
        ended: video.ended,
        failed: video.error !== null,
        handedOver: presentationHandedOver,
        readyState: video.readyState
    }));

    const loadSource = (requestedStart = 0) => {
        if (!delivery) {
            return false;
        }

        const live = streamIsLive();
        const start = live ? clampToDuration(requestedStart) : 0;
        const sourceKey = live
            ? `${streamSessionId}:${start.toFixed(3)}`
            : `${streamSessionId}`;

        if (video.dataset.playbackSource === sourceKey) {
            return false;
        }

        streamStartSeconds = start;
        loadedStreamLive = live;
        video.dataset.playbackSource = sourceKey;
        video.src = buildMediaUrl(streamStartSeconds);
        video.load();
        applySpeed();
        return true;
    };

    const showVideo = () => {
        placeholder.hidden = true;
        video.hidden = false;
        stage.classList.remove("player-placeholder");

        const live = streamIsLive();
        const requestedStart = pendingResumeTime !== null && Number.isFinite(pendingResumeTime)
            ? clampToDuration(pendingResumeTime)
            : 0;

        if (live) {
            pendingResumeTime = null;
        }

        const changed = loadSource(live ? requestedStart : 0);
        if (!changed && !live && pendingResumeTime !== null && video.readyState >= 1) {
            video.currentTime = requestedStart;
            pendingResumeTime = null;

            if (resumeShouldPlay) {
                resumeShouldPlay = false;
                void video.play().catch(() => {});
            }
        }

        updateTimeline();
    };

    const storageIsAvailable = () => storageState === "available";

    const stopStorageRetry = () => {
        if (storageRetryTimer !== null) {
            window.clearTimeout(storageRetryTimer);
            storageRetryTimer = null;
        }

        storageRecoveryActive = false;
        storageRetryStartedAt = null;
        storageRetryAttempt = 0;
    };

    const storageStartFailed = () =>
        storageHealth === "error" &&
        (storageDiagnostic === "wake_timeout" || storageDiagnostic === "wake_send_failed");

    const storageSleeping = () =>
        storageHealth === "offline_expected" && !storageWakeRequested;

    const showStorageState = (availability, exhausted = false) => {
        storageState = availability?.state || storageState || "unknown";
        if (availability?.health) {
            storageHealth = availability.health;
            storageDiagnostic = availability.diagnosticCode || "";
        }

        const retryable = availability?.retryable !== false &&
            storageState !== "file_missing" &&
            storageState !== "source_unreachable";

        if (storageIsAvailable()) {
            if (storageActions) {
                storageActions.hidden = true;
            }
            return;
        }

        storageRecoveryActive = true;
        hideVideo();
        if (failureActions) {
            failureActions.hidden = true;
        }

        playbackBadge.classList.remove("status-ok", "status-warning", "status-error");

        const sleeping = storageSleeping();
        const startFailed = storageStartFailed();

        if (storageState === "file_missing") {
            playbackStatus.textContent = "This media file is missing.";
            playbackSummary.textContent = "Media file missing";
            playbackBadge.classList.add("status-error");
            playbackBadge.textContent = "Missing";
        } else if (startFailed) {
            playbackStatus.textContent = storageDiagnostic === "wake_send_failed"
                ? "Wake-on-LAN could not be sent to the media storage."
                : "Media storage did not start.";
            playbackSummary.textContent = "Storage did not start";
            playbackBadge.classList.add("status-error");
            playbackBadge.textContent = "Storage";
        } else if (storageState === "source_unreachable") {
            playbackStatus.textContent = "Media storage cannot currently be read.";
            playbackSummary.textContent = "Storage unreachable";
            playbackBadge.classList.add("status-error");
            playbackBadge.textContent = "Storage";
        } else if (sleeping) {
            playbackStatus.textContent = "Media storage is asleep.";
            playbackSummary.textContent = "Storage asleep";
            playbackBadge.classList.add("status-warning");
            playbackBadge.textContent = "Asleep";
        } else if (storageState === "source_starting" || (storageWakeRequested && storageHealth === "offline_expected")) {
            playbackStatus.textContent = "Starting storage…";
            playbackSummary.textContent = "Storage starting";
            playbackBadge.classList.add("status-warning");
            playbackBadge.textContent = "Starting";
        } else if (exhausted) {
            playbackStatus.textContent = "Media storage is still offline.";
            playbackSummary.textContent = "Storage offline";
            playbackBadge.classList.add("status-error");
            playbackBadge.textContent = "Offline";
        } else {
            playbackStatus.textContent = "Waiting for media storage… retrying automatically.";
            playbackSummary.textContent = "Storage unavailable";
            playbackBadge.classList.add("status-warning");
            playbackBadge.textContent = "Waiting";
        }

        if (storageActions) {
            storageActions.hidden = false;
        }

        if (storageRetry) {
            storageRetry.hidden = sleeping || (!exhausted && !startFailed && retryable);
        }

        if (storageWake) {
            storageWake.hidden = !sleeping;
        }
    };

    const readStorageAvailability = async () => {
        const url = root.dataset.storageAvailabilityUrl;
        if (!url) {
            return null;
        }

        try {
            const probeUrl = new URL(url, window.location.origin);
            probeUrl.searchParams.set("fresh", "true");
            if (storageWakeRequested) {
                probeUrl.searchParams.set("wake", "true");
            }
            const response = await fetch(probeUrl, {
                credentials: "same-origin",
                headers: { "Accept": "application/json" }
            });

            if (!response.ok) {
                return null;
            }

            return await response.json();
        } catch {
            return null;
        }
    };

    const refreshPlayerBootstrap = async () => {
        const url = root.dataset.playerBootstrapUrl;
        if (!url) {
            return false;
        }

        try {
            const response = await fetch(url, videoTarget
                ? {
                    method: "POST",
                    credentials: "same-origin",
                    headers: { "Accept": "application/json", "Content-Type": "application/json" },
                    body: JSON.stringify(targetBody)
                }
                : {
                    credentials: "same-origin",
                    headers: { "Accept": "application/json" }
                });
            if (!response.ok) {
                return false;
            }

            const bootstrap = await response.json();
            if (!bootstrap?.media) {
                return false;
            }

            durationSeconds = Number(bootstrap.media.durationMs) / 1000;
            hasKnownDuration = Number.isFinite(durationSeconds) && durationSeconds > 0;

            if (bootstrap.media.availability) {
                storageState = bootstrap.media.availability.state || "unknown";
                storageHealth = bootstrap.media.availability.health || storageHealth;
            }

            failedModes.clear();
            video.removeAttribute("src");
            delete video.dataset.playbackSource;
            video.load();
            return storageIsAvailable();
        } catch {
            return false;
        }
    };

    const scheduleStorageRetry = (delayMs) => {
        if (!storageRecoveryActive) {
            return;
        }

        if (storageRetryTimer !== null) {
            window.clearTimeout(storageRetryTimer);
        }

        storageRetryTimer = window.setTimeout(() => {
            void pollStorageAvailability();
        }, Math.max(0, delayMs));
    };

    const pollStorageAvailability = async () => {
        if (!storageRecoveryActive) {
            return;
        }

        const startedAt = storageRetryStartedAt ?? Date.now();
        storageRetryStartedAt = startedAt;

        // A NAS that is starting gets the server's bounded start window; the server reports
        // the failure itself when it gives up.
        const windowMs = storageState === "source_starting" ? 180000 : 60000;
        if (Date.now() - startedAt >= windowMs) {
            storageRetryTimer = null;
            showStorageState({ state: storageState, retryable: true }, true);
            return;
        }

        const availability = await readStorageAvailability();
        if (availability?.state === "available") {
            storageState = "available";
            const refreshed = await refreshPlayerBootstrap();
            if (refreshed) {
                stopStorageRetry();
                if (storageActions) {
                    storageActions.hidden = true;
                }
                applyPlayback();
                return;
            }
        }

        if (availability) {
            showStorageState(availability, false);
            if (availability.retryable === false || storageStartFailed()) {
                storageRetryTimer = null;
                showStorageState(availability, true);
                return;
            }

            if (storageSleeping()) {
                // Waits for Play instead of polling a NAS that sleeps on purpose.
                storageRetryTimer = null;
                return;
            }
        }

        const delays = [0, 1000, 2000, 4000, 5000];
        const delay = delays[Math.min(storageRetryAttempt, delays.length - 1)];
        storageRetryAttempt += 1;
        scheduleStorageRetry(delay);
    };

    const startStorageRetry = (preservePlaybackIntent = false) => {
        if (preservePlaybackIntent) {
            pendingResumeTime = absoluteCurrentTime();
            resumeShouldPlay = playbackWasRequested;
        }

        if (storageRecoveryActive) {
            return;
        }

        storageRecoveryActive = true;
        storageRetryStartedAt = Date.now();
        storageRetryAttempt = 1;
        showStorageState({ state: storageState, retryable: true }, false);
        scheduleStorageRetry(0);
    };

    // Picture (bitmap) subtitles cannot be drawn by this player; choosing one asks the
    // server to render it into the video. Text subtitles stay client-side cues.
    const burnInSubtitleTrackId = () => {
        const option = subtitleSelect?.selectedOptions[0];
        return option?.dataset.image === "true" ? option.value : null;
    };

    // Runtime telemetry (#403): every few seconds the buffer, the smoothed receive rate and the stalls of this session go to the
    // server, which keeps them in memory for the next plan and for diagnostics. Best effort: a lost report costs one sample and
    // never playback. The rate is sampled once a second from how fast the buffered range grows, which says nothing while the
    // browser is not fetching, so those samples are skipped.
    const telemetryUrlTemplate = root.dataset.streamSessionTelemetryUrlTemplate || "";
    let telemetrySequence = 0;
    let lastReportedState = null;
    let telemetryAvailable = false;

    const beginTelemetrySession = () => {
        stalls.resetSession();
        throughput.reset();
        transcodeReading = null;
        telemetrySequence = 0;
        lastReportedState = null;
        telemetryAvailable = Boolean(streamSessionId && telemetryUrlTemplate);
    };

    const sampleThroughput = () => {
        if (!telemetryAvailable || !streamSessionId || video.hidden || !plan) {
            return;
        }

        const position = absoluteCurrentTime();
        const end = buffering.bufferedEnd(bufferedRanges(), position);
        const target = plan.buffer?.targetAheadSeconds ?? Infinity;
        const idle = video.networkState === HTMLMediaElement.NETWORK_IDLE ||
            (end !== null && (end - position >= target || (hasKnownDuration && end >= durationSeconds - 1)));
        throughput.observe({
            nowMs: performance.now(),
            bufferedEndSeconds: end,
            bitrateKbps: plan.quality?.deliveredBitrateKbps ?? plan.quality?.sourceBitrateKbps,
            idle
        });
    };

    // The server asked for another quality. The replacement plan is requested in the background while the current stream keeps playing; the
    // source is only swapped once the new plan is confirmed playable, at the position of that moment and with the same selections. A failed or
    // unavailable plan changes nothing and pauses following advice for a while. Unlike a choice of the viewer this keeps failedModes (a stall
    // is no verdict on a mode), resets no recovery budget and shows no error; the new plan's reasons and the compact status say what changed.
    let adviceInFlight = false;
    const followQualityAdvice = async advice => {
        const generation = planGeneration;
        adviceInFlight = true;
        try {
            await streamRecovery.followAdvisedPlan({
                advice,
                requestPlan: followed => requestPlan({ followedAdvice: followed }),
                // Another plan took over meanwhile (the viewer changed a selection).
                isStale: () => generation !== planGeneration,
                // Re-checked right before the swap: picture-in-picture may have started while the plan was requested.
                isBlocked: () => presentationHandedOver,
                discardOrphan: discardOrphanSession,
                backOff: () => adviceGate.backOff(performance.now()),
                warn: (message, detail) => console.warn(message, detail),
                install: response => {
                    planGeneration += 1;
                    pendingResumeTime = absoluteCurrentTime();
                    resumeShouldPlay = !video.paused && !video.ended;
                    installPlan(response);
                    showVideo();
                }
            });
        } finally {
            adviceInFlight = false;
        }
    };

    const discardOrphanSession = sessionId => {
        const template = root.dataset.streamSessionUrlTemplate;
        if (sessionId && template) {
            void fetch(template.replace("__session__", sessionId), { method: "DELETE", credentials: "same-origin", keepalive: true }).catch(() => {});
        }
    };

    // The answer of a report: the server's conversion speed for the diagnostics and its advice, which is followed at most as often as the
    // gate allows and never while a plan is being replaced. An answer for a session that was replaced meanwhile is stale.
    const applyTelemetryAnswer = (answer, sessionAtSend) => {
        if (!answer || sessionAtSend !== streamSessionId) {
            return;
        }

        transcodeReading = Number.isFinite(answer.transcodeSpeed)
            ? { speed: answer.transcodeSpeed, fps: Number.isFinite(answer.transcodeFps) ? answer.transcodeFps : null }
            : null;
        const busy = !plan || video.hidden || storageRecoveryActive || adviceInFlight;
        const state = { paused: video.paused || video.ended, busy, handedOver: presentationHandedOver };
        if (streamRecovery.followAdvice(adviceGate, answer.advice, state, performance.now())) {
            void followQualityAdvice(answer.advice);
        }
    };

    // force: the evidence goes out even when the state did not change, e.g. just before a new plan replaces this session.
    const sendTelemetry = async (force = false) => {
        if (!telemetryAvailable || !streamSessionId || video.hidden) {
            return;
        }

        // A seek that has not delivered media yet is playback waiting for media, not a pause.
        const state = video.paused || video.ended || video.error ? "paused" : stalls.isStalled() || video.seeking ? "buffering" : "playing";
        if (!force && !buffering.shouldReport(state, lastReportedState)) {
            return;
        }

        lastReportedState = state;
        const sessionAtSend = streamSessionId;
        const nowMs = performance.now();
        const body = buffering.buildReport({
            sequence: ++telemetrySequence,
            state,
            bufferAheadSeconds: bufferAheadSeconds(),
            throughputKbps: throughput.value(nowMs),
            stalls: stalls.snapshot(nowMs),
            positionSeconds: absoluteCurrentTime()
        });
        try {
            // A re-plan waits for the flush, so it is bounded tightly; a refusal (429, 400) never holds the re-plan back.
            const response = await fetch(telemetryUrlTemplate.replace("__session__", streamSessionId), {
                method: "PUT",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(body),
                signal: AbortSignal.timeout ? AbortSignal.timeout(force ? 1000 : 4000) : undefined
            });
            if (response.status === 404) {
                // The server no longer knows the session: the broken stream is handled by the error path, so reporting stops here.
                telemetryAvailable = false;
            } else if (!response.ok) {
                console.warn("The playback telemetry was refused.", response.status);
            } else if (!force) {
                // The flush before a re-plan only delivers evidence; its answer must not start another re-plan.
                let answer = null;
                try {
                    answer = await response.json();
                } catch (error) {
                    console.warn("The playback telemetry answer could not be read.", error);
                }

                applyTelemetryAnswer(answer, sessionAtSend);
            }
        } catch (error) {
            console.warn("The playback telemetry could not be sent.", error);
        }
    };

    window.setInterval(sampleThroughput, 1000);
    window.setInterval(() => void sendTelemetry(), buffering.reportIntervalMs);

    const requestPlan = async (options = {}) => {
        // The replaced session's last evidence (buffer, stalls) must be on the server before it plans the replacement.
        await sendTelemetry(true);

        let capabilities = null;
        try {
            capabilities = capabilityProbe ? await capabilityProbe.detect() : null;
        } catch {
            capabilities = null;
        }

        const response = await fetch(planUrl, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Accept": "application/json",
                "Content-Type": "application/json"
            },
            body: JSON.stringify({
                ...targetBody,
                capabilities,
                audioTrackId: selectedAudioTrackId,
                subtitleTrackId: burnInSubtitleTrackId(),
                quality: qualityPreset,
                mode: preference,
                network: capabilityProbe?.networkReport() || null,
                failedModes: [...failedModes],
                replacesSessionId: streamSessionId,
                followedAdvice: options.followedAdvice,
                // Opening the page never wakes sleeping storage; pressing Play does.
                wake: storageWakeRequested || playbackWasRequested
            })
        });

        if (!response.ok) {
            throw new Error(`Playback plan failed with ${response.status}.`);
        }

        return response.json();
    };

    const showPlayerError = (message) => {
        if (error) {
            error.hidden = !message;
            error.textContent = message || "";
        }
    };

    // The one final failure state: the centred status with Try again. A transient retry notice never stays above it, so the player
    // never says "trying another way" and "cannot be played" at the same time.
    const showFailure = (message) => {
        hideVideo();
        showPlayerError(null);
        playbackStatus.textContent = message || "";
        if (failureActions) {
            failureActions.hidden = false;
        }
    };

    const installPlan = response => {
        plan = response.plan;
        planCapabilitiesInferred = response.capabilitiesInferred === true;
        streamSessionId = response.sessionId || null;
        delivery = response.delivery || null;
        beginTelemetrySession();
        renderPlan();
    };

    const applyPlayback = async () => {
        if (!storageIsAvailable()) {
            startStorageRetry(false);
            return;
        }

        const generation = ++planGeneration;
        plan = null;
        renderPlan();
        if (failureActions) {
            failureActions.hidden = true;
        }

        let response;
        try {
            response = await requestPlan();
        } catch {
            if (generation === planGeneration) {
                showFailure(text["playback.status.planFailed"]);
            }
            return;
        }

        // A newer selection (quality, audio, mode) superseded this request.
        if (generation !== planGeneration) {
            return;
        }

        installPlan(response);

        if (plan.mode === "unavailable" || !delivery) {
            // Storage is not readable: the canonical storage flow (#411) takes over — asleep
            // storage waits for Play, starting storage is polled until playback can continue.
            if (response.availability && response.availability.state !== "available") {
                storageState = response.availability.state || "unknown";
                if (response.availability.health) {
                    storageHealth = response.availability.health;
                    storageDiagnostic = response.availability.diagnosticCode || "";
                }

                startStorageRetry(false);
                return;
            }

            showFailure(text[failedKey]);
            return;
        }

        showPlayerError(null);
        showVideo();
    };

    modeSelect.addEventListener("change", () => {
        const resumeAt = absoluteCurrentTime();
        const shouldResume = !video.paused && !video.ended;

        preference = modePreferences.includes(modeSelect.value) ? modeSelect.value : "auto";
        failedModes.clear();
        pendingResumeTime = resumeAt;
        resumeShouldPlay = shouldResume;
        store(preferenceKey, preference);
        showPlayerError(null);
        void applyPlayback();
    });

    let cues = [];
    try {
        cues = JSON.parse(data.textContent || "[]");
    } catch {
        if (error) {
            error.hidden = false;
            error.textContent = "Subtitle data could not be loaded.";
        }
        return;
    }

    let activeIndex = -2;
    let selectedCueStartMs = 0;
    let learningResumeOnClose = false;

    // Plain playback subtitle (an embedded text stream chosen for display).
    // It is independent of the learning overlay: when it differs from the
    // learning text, both stay visible.
    let playbackCues = [];
    let playbackCueKey = "";
    const playbackCueCache = new Map();

    // The learning overlay belongs to the "learning" choice (or the embedded stream
    // that is the learning source); any other track is a plain subtitle.
    const learningOverlayVisible = () =>
        cues.length > 0 && (subtitleChoice === "learning" || selectedSubtitleIsLearningSource());

    function selectedSubtitleIsLearningSource() {
        return subtitleSelect?.selectedOptions[0]?.dataset.learningSource === "true";
    }

    // Text tracks become cues drawn here; picture tracks are burned in by the server instead.
    const playbackTrackId = () =>
        subtitleChoice.startsWith("stream:") && !selectedSubtitleIsLearningSource() && !burnInSubtitleTrackId()
            ? subtitleChoice
            : null;

    const renderPlaybackSubtitle = (timeMs) => {
        if (!playbackSubtitle) {
            return;
        }

        const lines = playbackTrackId()
            ? design.activeCuesAt(playbackCues, timeMs).map(cue => cue.text)
            : [];
        const key = lines.join("\n");
        if (key === playbackCueKey) {
            return;
        }

        playbackCueKey = key;
        playbackSubtitle.textContent = key;
        playbackSubtitle.hidden = key.length === 0;
    };

    const loadPlaybackCues = async (trackId) => {
        if (!trackId) {
            playbackCues = [];
            return;
        }

        if (playbackCueCache.has(trackId)) {
            playbackCues = playbackCueCache.get(trackId);
            return;
        }

        const template = controlsData.subtitleCuesUrlTemplate || "";
        try {
            const response = await fetch(template.replace("__track__", encodeURIComponent(trackId)), {
                credentials: "same-origin",
                headers: { "Accept": "application/json" }
            });
            if (!response.ok) {
                throw new Error("Subtitle track unavailable.");
            }

            const payload = await response.json();
            const loaded = (payload.cues || []).slice().sort((a, b) => a.startMs - b.startMs);
            playbackCueCache.set(trackId, loaded);
            if (playbackTrackId() === trackId) {
                playbackCues = loaded;
            }
        } catch {
            playbackCues = [];
            showPlayerError(text["playback.subtitle.loadFailed"] || "");
        }
    };

    const updateRepeatAvailability = () => {
        if (repeatLineButton) {
            repeatLineButton.disabled = !(learningOverlayVisible() && cues.length > 0) &&
                playbackCues.length === 0;
        }
    };

    const currentLineStartMs = () => {
        const nowMs = Math.floor(absoluteCurrentTime() * 1000);
        const source = learningOverlayVisible() ? cues : playbackCues;
        return design.lineStartAt(source, nowMs);
    };

    // The shared language inspector (issue #233) replaces this player's own
    // learning sheet wherever the resolved scope renders it; the sheet below
    // stays only as the fallback for scopes without PlayerTools.
    const sharedInspector = window.JularrLanguageInspector;
    const sharedInspectorAvailable = sharedInspector?.available === true;

    const openLearning = (cue, token = null, selectedElement = null) => {
        overlay.querySelectorAll('[aria-pressed="true"]').forEach(element =>
            element.removeAttribute("aria-pressed"));
        if (selectedElement instanceof HTMLElement) {
            selectedElement.setAttribute("aria-pressed", "true");
        }

        selectedCueStartMs = cue.startMs;

        if (sharedInspectorAvailable) {
            const sentence = design.cueText(cue);
            void sharedInspector.open(token ? token.surface : sentence, {
                sentence,
                cueStartMs: cue.startMs
            });
            return;
        }

        if (!learningTools) {
            return;
        }

        if (inspector.hidden) {
            learningResumeOnClose = !video.paused && !video.ended;
        }

        video.pause();

        if (token) {
            learningKicker.textContent = "Word";
            word.textContent = token.canonical || token.surface;
            reading.textContent = token.reading || "";
            meaning.textContent = token.meaning || "No local meaning available yet.";
            state.textContent = token.state || "New";
            state.hidden = false;
        } else {
            learningKicker.textContent = "Sentence";
            word.textContent = design.cueText(cue);
            reading.textContent = "";
            meaning.textContent = "Tap a highlighted word in the subtitle to inspect its reading and meaning.";
            state.textContent = "";
            state.hidden = true;
        }

        inspector.hidden = false;
        closeLearning.focus();
    };

    const closeLearningSheet = (resume = true) => {
        if (!learningTools || inspector.hidden) {
            return;
        }

        inspector.hidden = true;
        overlay.querySelectorAll('[aria-pressed="true"]').forEach(element =>
            element.removeAttribute("aria-pressed"));

        const shouldResume = resume && learningResumeOnClose;
        learningResumeOnClose = false;
        if (shouldResume) {
            void video.play().catch(() => {});
        }
    };

    const renderCue = (index) => {
        design.renderCue(root, overlay, index < 0 ? null : cues[index]);
    };

    if (sharedInspectorAvailable) {
        let inspectorResumeOnClose = false;

        sharedInspector.addEventListener("open", () => {
            inspectorResumeOnClose = !video.paused && !video.ended;
            video.pause();
        });

        sharedInspector.addEventListener("close", () => {
            const shouldResume = inspectorResumeOnClose;
            inspectorResumeOnClose = false;
            if (shouldResume) {
                void video.play().catch(() => {});
            }
        });

        // Update the cached cue tokens so a word saved/learned/known/ignored
        // through the inspector re-renders with its new state right away,
        // through the same renderCue design.js already uses for the overlay.
        sharedInspector.addEventListener("statechange", event => {
            const detail = event.detail || {};
            if (!detail.text) {
                return;
            }

            let changed = false;
            for (const cue of cues) {
                for (const cueToken of cue.tokens || []) {
                    if ((cueToken.canonical || cueToken.surface) === detail.text) {
                        cueToken.state = detail.state;
                        changed = true;
                    }
                }
            }

            if (changed && activeIndex >= 0) {
                renderCue(activeIndex);
            }
        });
    }

    root.addEventListener(design.actionEvent, event => {
        const detail = event.detail || {};
        switch (detail.action) {
            case design.actions.openWord:
                openLearning(detail.cue, detail.token, detail.element);
                break;
            case design.actions.learnCurrentCue:
                openLearning(detail.cue);
                break;
            case design.actions.repeatCurrentCue: {
                // From the learning sheet repeat the inspected line; from the
                // transport controls repeat the line at the playhead.
                const startMs = learningTools && !inspector.hidden
                    ? selectedCueStartMs
                    : currentLineStartMs();
                closeLearningSheet(false);
                if (startMs !== null) {
                    seekToAbsolute(Math.max(0, startMs / 1000), true);
                }
                break;
            }
            case design.actions.seekBack10:
                seekToAbsolute(seekBase() - seekSeconds.back, undefined, true);
                break;
            case design.actions.seekForward10:
                seekToAbsolute(seekBase() + seekSeconds.forward, undefined, true);
                break;
            case design.actions.seekTo:
                if (Number.isFinite(detail.seconds)) {
                    seekToAbsolute(detail.seconds);
                }
                break;
            case design.actions.closeOverlay:
                closeLearningSheet(true);
                break;
        }
    });

    root.querySelectorAll("[data-player-controls] [data-player-action]").forEach(button =>
        button.addEventListener("click", () =>
            design.dispatch(root, button.dataset.playerAction)));

    replay?.addEventListener("click", () =>
        design.dispatch(root, design.actions.repeatCurrentCue));
    closeLearning?.addEventListener("click", () =>
        design.dispatch(root, design.actions.closeOverlay));

    root.addEventListener("keydown", event => {
        if (event.key === "Escape" && learningTools && !inspector.hidden) {
            event.preventDefault();
            design.dispatch(root, design.actions.closeOverlay);
        }
    });

    // Both subtitle layers follow the media clock, so any playback speed keeps
    // cue timing exact; the frame loop only raises the sampling rate.
    const sync = () => {
        const nowMs = Math.floor(absoluteCurrentTime() * 1000);
        renderPlaybackSubtitle(nowMs);

        const index = learningOverlayVisible() ? design.cueIndexAt(cues, nowMs) : -1;
        if (index === activeIndex) {
            return;
        }

        activeIndex = index;
        renderCue(index);
    };

    let frameSyncActive = false;
    const frameSync = () => {
        if (video.paused || video.ended || video.hidden) {
            frameSyncActive = false;
            return;
        }

        sync();
        if (typeof video.requestVideoFrameCallback === "function") {
            video.requestVideoFrameCallback(frameSync);
        } else {
            window.requestAnimationFrame(frameSync);
        }
    };

    const startFrameSync = () => {
        if (!frameSyncActive) {
            frameSyncActive = true;
            frameSync();
        }
    };

    const applySubtitleChoice = async () => {
        activeIndex = -2;
        playbackCueKey = null;
        await loadPlaybackCues(playbackTrackId());
        updateRepeatAvailability();
        sync();
    };

    // The first plan already carries a picture subtitle chosen before playback.
    let plannedBurnIn = burnInSubtitleTrackId();
    subtitleSelect?.addEventListener("change", () => {
        subtitleChoice = subtitleSelect.value;
        showPlayerError(null);
        void applySubtitleChoice();
        // Picture subtitles change the stream itself, so they need a new plan.
        if (burnInSubtitleTrackId() !== plannedBurnIn) {
            plannedBurnIn = burnInSubtitleTrackId();
            restartWithSelection();
        }
        renderSubtitleHint();
    });

    speedSelect?.addEventListener("change", () => {
        const requested = Number(speedSelect.value);
        playbackSpeed = Number.isFinite(requested) && requested > 0 ? requested : 1;
        applySpeed();
    });

    function restartWithSelection() {
        pendingResumeTime = absoluteCurrentTime();
        resumeShouldPlay = !video.paused && !video.ended;
        failedModes.clear();
        showPlayerError(null);
        void applyPlayback();
    }

    audioSelect?.addEventListener("change", () => {
        selectedAudioTrackId = audioSelect.value || null;
        restartWithSelection();
    });

    // A quality picked here overrides the network default for this device.
    qualitySelect?.addEventListener("change", () => {
        qualityPreset = qualityPresets.includes(qualitySelect.value) ? qualitySelect.value : "auto";
        store(qualityKey, qualityPreset);
        restartWithSelection();
    });

    saveDefaults?.addEventListener("click", async () => {
        if (!preferencesUrl) {
            return;
        }

        const subtitleLanguage = subtitleChoice === "off"
            ? "off"
            : subtitleSelect?.selectedOptions[0]?.dataset.language || "";
        const body = {
            preferredAudioLanguage: audioSelect?.selectedOptions[0]?.dataset.language || "",
            preferredSubtitleLanguage: subtitleLanguage,
            defaultPlaybackSpeed: playbackSpeed
        };
        if (!audioSelect) {
            delete body.preferredAudioLanguage;
        }

        saveDefaults.disabled = true;
        try {
            const response = await fetch(preferencesUrl, {
                method: "PUT",
                credentials: "same-origin",
                headers: {
                    "Accept": "application/json",
                    "Content-Type": "application/json"
                },
                body: JSON.stringify(body)
            });
            if (!response.ok) {
                throw new Error("Preference update failed.");
            }

            if (qualityHint) {
                qualityHint.textContent = "Saved as the default for every episode on this profile.";
            }
        } catch {
            if (error) {
                error.hidden = false;
                error.textContent = "Your playback defaults could not be saved.";
            }
        } finally {
            saveDefaults.disabled = false;
        }
    });

    // A live (remuxed or transcoded) stream restarts on every seek, so quick repeated seeks
    // (double-tap series, arrow keys) are collected into one restart at the final position.
    let liveSeekTarget = null;
    let liveSeekTimer = null;
    const liveSeekDelayMs = 350;
    const seekBase = () => liveSeekTarget ?? absoluteCurrentTime();

    const seekToAbsolute = (requestedSeconds, shouldPlay = !video.paused && !video.ended, coalesce = false) => {
        const target = clampToDuration(requestedSeconds);
        window.clearTimeout(liveSeekTimer);
        liveSeekTarget = null;

        if (loadedStreamLive && coalesce) {
            liveSeekTarget = target;
            timelinePreviewing = true;
            timeline.value = String(target);
            timelineCurrent.textContent = formatTime(target);
            liveSeekTimer = window.setTimeout(() => {
                liveSeekTimer = null;
                liveSeekTarget = null;
                seekToAbsolute(target, shouldPlay);
            }, liveSeekDelayMs);
            return;
        }

        if (loadedStreamLive) {
            pendingResumeTime = null;
            resumeShouldPlay = shouldPlay;
            loadSource(target);
        } else if (video.readyState >= 1) {
            video.currentTime = target;
            if (shouldPlay) {
                void video.play().catch(() => {});
            }
        } else {
            pendingResumeTime = target;
            resumeShouldPlay = shouldPlay;
        }

        timelinePreviewing = false;
        updateTimeline();
        sync();
    };

    timeline.addEventListener("input", () => {
        if (!hasKnownDuration) {
            return;
        }

        timelinePreviewing = true;
        timelineCurrent.textContent = formatTime(Number(timeline.value));
        describeTimeline(Number(timeline.value));
    });

    timeline.addEventListener("change", () => {
        seekToAbsolute(Number(timeline.value));
    });

    let autoplayTimer = null;

    const stopAutoplayCountdown = () => {
        if (autoplayTimer !== null) {
            window.clearInterval(autoplayTimer);
            autoplayTimer = null;
        }

        if (postPlayCountdown) {
            postPlayCountdown.hidden = true;
            postPlayCountdown.textContent = "";
        }

        if (postPlayCancel) {
            postPlayCancel.hidden = true;
        }
    };

    const hidePostPlay = () => {
        stopAutoplayCountdown();
        if (postPlay) {
            postPlay.hidden = true;
        }
    };

    const showPostPlay = () => {
        if (!postPlay) {
            return;
        }

        postPlay.hidden = false;
        stopAutoplayCountdown();

        const focusTarget = postPlay.querySelector("[data-post-play-next]") || postPlayReplay;
        focusTarget?.focus();

        if (!autoplayNext || !nextUrl || !postPlayCountdown) {
            return;
        }

        let remaining = autoplayDelaySeconds;
        const render = () => {
            postPlayCountdown.textContent = `Next episode in ${remaining}s`;
        };

        postPlayCountdown.hidden = false;
        if (postPlayCancel) {
            postPlayCancel.hidden = false;
        }
        render();

        autoplayTimer = window.setInterval(() => {
            remaining -= 1;
            if (remaining <= 0) {
                stopAutoplayCountdown();
                window.location.assign(nextUrl);
                return;
            }

            render();
        }, 1000);
    };

    postPlayReplay?.addEventListener("click", () => {
        hidePostPlay();
        seekToAbsolute(0, true);
    });

    postPlayCancel?.addEventListener("click", () => {
        stopAutoplayCountdown();
        postPlayReplay?.focus();
    });

    restartButton?.addEventListener("click", () => {
        hidePostPlay();
        pendingResumeTime = null;
        seekToAbsolute(0, true);
        if (video.paused) {
            void video.play().catch(() => {});
        }

        if (progressUrl) {
            sendProgress(0, false, false);
        }

        restartButton.hidden = true;
    });

    autoplayToggle?.addEventListener("change", async () => {
        const requested = autoplayToggle.checked;
        if (!preferencesUrl) {
            autoplayToggle.checked = autoplayNext;
            return;
        }

        autoplayToggle.disabled = true;
        try {
            const response = await fetch(preferencesUrl, {
                method: "PUT",
                credentials: "same-origin",
                headers: {
                    "Accept": "application/json",
                    "Content-Type": "application/json"
                },
                body: JSON.stringify({ autoplayNext: requested })
            });

            if (!response.ok) {
                throw new Error("Preference update failed.");
            }

            const preferences = await response.json();
            autoplayNext = preferences.autoplayNext === true;
        } catch {
            if (error) {
                error.hidden = false;
                error.textContent = "The autoplay preference could not be saved.";
            }
        } finally {
            autoplayToggle.checked = autoplayNext;
            autoplayToggle.disabled = false;
        }

        if (!autoplayNext) {
            stopAutoplayCountdown();
        }
    });

    video.addEventListener("timeupdate", () => {
        trackNaturalPlayback();
        updateTimeline();
        sync();
        persistProgress();
    });
    video.addEventListener("seeked", () => {
        updateTimeline();
        sync();
        // While playing the regular throttle applies; a seek while paused is
        // an explicit position and is flushed like a pause.
        persistProgress(false, video.paused);
    });
    video.addEventListener("loadedmetadata", () => {
        applySpeed();
        if (!loadedStreamLive &&
            pendingResumeTime !== null &&
            Number.isFinite(pendingResumeTime)) {
            const target = clampToDuration(pendingResumeTime);
            video.currentTime = Number.isFinite(video.duration) && video.duration >= 0
                ? Math.min(target, video.duration)
                : target;
            pendingResumeTime = null;
        }

        updateTimeline();
        sync();

        if (resumeShouldPlay) {
            resumeShouldPlay = false;
            void video.play().catch(() => {});
        }
    });
    video.addEventListener("play", () => {
        playbackWasRequested = true;
        hidePostPlay();
        startFrameSync();
    });

    // Stalls: waiting for media after playback had started. The tracker ignores the initial start, seeks and paused time.
    video.addEventListener("loadstart", () => {
        stalls.sourceChanged(performance.now());
        throughput.reset();
    });
    video.addEventListener("seeking", () => {
        stalls.seeking(performance.now());
        throughput.reset();
    });
    video.addEventListener("seeked", () => stalls.seeked(video.readyState));
    video.addEventListener("waiting", () => stalls.waiting(performance.now(), video.paused));
    video.addEventListener("playing", () => stalls.playing(performance.now()));
    video.addEventListener("ended", () => stalls.paused(performance.now()));
    video.addEventListener("emptied", () => stalls.sourceChanged(performance.now()));
    for (const name of ["waiting", "playing", "seeking", "seeked", "loadeddata", "canplay", "play", "pause", "ended", "emptied", "error"]) {
        video.addEventListener(name, syncWaiting);
    }

    for (const name of ["progress", "timeupdate", "seeked", "loadedmetadata", "durationchange", "emptied"]) {
        video.addEventListener(name, renderBuffered);
    }

    video.addEventListener("pause", () => {
        stalls.paused(performance.now());
        if (!video.ended) {
            persistProgress(false, true);
        }

        if (!storageRecoveryActive && !video.ended) {
            playbackWasRequested = false;
        }
    });

    video.addEventListener("ended", () => {
        persistProgress(true, true);
        playbackWasRequested = false;
        if (restartButton) {
            restartButton.hidden = true;
        }
        showPostPlay();
    });

    video.addEventListener("timeupdate", () => {
        const now = absoluteCurrentTime();
        playedSinceRecovery = streamRecovery.accumulatePlayed(playedSinceRecovery, lastPlayedTime, now);
        lastPlayedTime = now;
        sessionRecoveries = streamRecovery.recoveriesAfterProgress(sessionRecoveries, playedSinceRecovery);
    });

    // A failed video element carries no HTTP status, so the stream session is asked whether the server ended it.
    // Null means the server could not be asked; the caller then takes the ordinary mode fallback.
    const readStreamSessionStatus = async () => {
        const template = root.dataset.streamSessionUrlTemplate;
        if (!streamSessionId || !template || !streamIsLive()) {
            return null;
        }

        try {
            const response = await fetch(template.replace("__session__", streamSessionId), {
                credentials: "same-origin",
                cache: "no-store",
                headers: { "Accept": "application/json" }
            });
            if (response.status === 404) {
                return { gone: true };
            }

            return response.ok ? await response.json() : null;
        } catch (error) {
            console.warn("The stream session status could not be read.", error);
            return null;
        }
    };

    video.addEventListener("error", async () => {
        stalls.paused(performance.now());
        storageWakeRequested = storageWakeRequested || playbackWasRequested;
        const availability = await readStorageAvailability();
        if (availability && availability.state !== "available") {
            storageState = availability.state || "unknown";
            pendingResumeTime = absoluteCurrentTime();
            resumeShouldPlay = playbackWasRequested;

            if (availability.retryable === false) {
                storageRecoveryActive = true;
                showStorageState(availability, true);
            } else {
                storageRecoveryActive = false;
                startStorageRetry(false);
            }
            return;
        }

        if (streamRecovery.shouldReplanSameMode(await readStreamSessionStatus(), sessionRecoveries)) {
            sessionRecoveries += 1;
            pendingResumeTime = absoluteCurrentTime();
            playedSinceRecovery = 0;
            lastPlayedTime = pendingResumeTime;
            resumeShouldPlay = playbackWasRequested;
            showPlayerError(text["playback.status.retrying"]);
            void applyPlayback();
            return;
        }

        // Report the failed mode and let the server choose the next one (Direct Play →
        // Direct Stream → Transcode) instead of deciding a fallback here.
        if (plan && plan.mode !== "unavailable" && !failedModes.has(plan.mode)) {
            failedModes.add(plan.mode);
            pendingResumeTime = absoluteCurrentTime();
            resumeShouldPlay = playbackWasRequested;
            showPlayerError(text["playback.status.retrying"]);
            void applyPlayback();
            return;
        }

        showFailure(text[failedKey]);
    });

    failureRetry?.addEventListener("click", () => {
        failedModes.clear();
        sessionRecoveries = 0;
        pendingResumeTime = absoluteCurrentTime();
        resumeShouldPlay = true;
        void applyPlayback();
    });

    storageRetry?.addEventListener("click", () => {
        storageWakeRequested = true;
        storageDiagnostic = "";
        playbackWasRequested = true;
        resumeShouldPlay = true;
        stopStorageRetry();
        storageRecoveryActive = true;
        storageRetryStartedAt = Date.now();
        storageRetryAttempt = 1;
        showStorageState({ state: storageState, retryable: true }, false);
        scheduleStorageRetry(0);
    });

    // Play on sleeping storage: the next availability poll carries wake=true, the server
    // starts the NAS and playback continues on its own once the storage is readable.
    storageWake?.addEventListener("click", () => {
        storageWakeRequested = true;
        storageDiagnostic = "";
        playbackWasRequested = true;
        resumeShouldPlay = true;
        stopStorageRetry();
        storageRecoveryActive = true;
        storageRetryStartedAt = Date.now();
        storageRetryAttempt = 1;
        storageState = "source_starting";
        showStorageState({ state: storageState, retryable: true }, false);
        scheduleStorageRetry(0);
    });

    window.addEventListener("pagehide", () => {
        if (absoluteCurrentTime() > 0) {
            persistProgress(false, true);
        }

        // Ending the stream session stops its server remux/transcode right away instead of
        // waiting for the idle cleanup.
        if (streamSessionId && root.dataset.streamSessionUrlTemplate) {
            void fetch(root.dataset.streamSessionUrlTemplate.replace("__session__", streamSessionId), {
                method: "DELETE",
                credentials: "same-origin",
                keepalive: true
            }).catch(() => {});
        }
    });

    // Handing the video to the system player or picture-in-picture, and coming back, changes the surface and
    // not the session: the position is flushed like a pause and nothing else is touched. Moving to the
    // background is the last moment the page is guaranteed to run, so it flushes too.
    const presentation = window.JularrPlayerPresentation;
    const handedOverModes = new Set([presentation.modes.nativeFullscreen, presentation.modes.pictureInPicture]);
    root.addEventListener(presentation.changeEvent, event => {
        presentationHandedOver = handedOverModes.has(event.detail.mode);
        syncWaiting();
        if (absoluteCurrentTime() > 0 && (handedOverModes.has(event.detail.mode) || handedOverModes.has(event.detail.previousMode))) {
            persistProgress(false, true);
        }
    });
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "hidden" && absoluteCurrentTime() > 0) {
            persistProgress(false, true);
        }
    });

    // Back from the page cache: the stream session was ended on pagehide, so plan again.
    window.addEventListener("pageshow", event => {
        if (event.persisted && streamSessionId) {
            pendingResumeTime = absoluteCurrentTime();
            streamSessionId = null;
            delete video.dataset.playbackSource;
            void applyPlayback();
        }
    });

    updateTimeline();
    applySpeed();
    void applyPlayback();
    void applySubtitleChoice();
})();
