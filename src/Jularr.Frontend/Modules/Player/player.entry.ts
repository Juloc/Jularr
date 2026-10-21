import "./player.scss";
import { progressReporter } from "../Tracking/Progress";
import { keepAddress, pushAddress, releaseAddress, visit } from "../Navigation/PageSwap";
import { activeMarker, chapterIndexAt, markerKey, modeFor, parseMarkers, parseMode, shouldAutoSkip, skipPlan, type Marker } from "./Skip";

// Playback client: capability probe, server PlaybackPlan, transport and progress reporting.
interface PlaybackPlan {
    playbackSessionId: string;
    playbackModeTypeId: number;
    streamUrl: string;
    resumePositionMs: number;
    isReduced?: boolean;
}

const Modes = ["", "DirectPlay", "Remux", "Transcode"];
const ReportInterval = 15_000;
const IdleDelay = 3_000;
const WakeRetries = 6;

const element = document.querySelector<HTMLElement>('.player[data-start-url]:not([data-start-url=""])');
if (element) {
    void start(element).catch(() => {
        const status = element.querySelector<HTMLElement>('[data-role=status]');
        if (status) {
            status.textContent = element.querySelector('[data-status=failed]')?.textContent ?? '';
            status.hidden = false;
        }
        const loading = element.querySelector<HTMLElement>('[data-role=loading]');
        if (loading) loading.hidden = true;
    });
}

async function start(player: HTMLElement): Promise<void> {
    const video = player.querySelector<HTMLVideoElement>(".player__video")!;
    const status = player.querySelector<HTMLElement>("[data-role=status]")!;
    const timeline = player.querySelector<HTMLInputElement>("[data-role=timeline]")!;
    const volume = player.querySelector<HTMLInputElement>("[data-role=volume]")!;
    const toggle = player.querySelector<HTMLButtonElement>("[data-action=toggle]")!;
    const mute = player.querySelector<HTMLButtonElement>("[data-action=mute]")!;
    const loading = player.querySelector<HTMLElement>("[data-role=loading]")!;
    const feedback = player.querySelector<HTMLElement>("[data-role=transport-feedback]")!;
    let loadingTimer = 0;
    let feedbackTimer = 0;

    // Avoid showing a spinner for very short buffer/seek events.
    function stopLoading(): void {
        clearTimeout(loadingTimer);
        loading.hidden = true;
    }

    function showLoading(): void {
        clearTimeout(loadingTimer);
        loadingTimer = window.setTimeout(() => {
            if (status.hidden && !video.error && !video.ended) loading.hidden = false;
        }, 220);
    }

    // Only explicit transport actions flash a symbol; automatic starts and track changes do not.
    function showTransportFeedback(action: "play" | "pause"): void {
        if (player.classList.contains("player--mini")) return;
        clearTimeout(feedbackTimer);
        feedback.dataset.state = action;
        feedback.hidden = false;
        feedback.classList.remove("player__feedback--animate");
        void feedback.offsetWidth; // Restart the animation on rapid repeated taps.
        feedback.classList.add("player__feedback--animate");
        feedbackTimer = window.setTimeout(() => {
            feedback.hidden = true;
            feedback.classList.remove("player__feedback--animate");
        }, 600);
    }

    const antiforgery = player.dataset.antiforgery ?? "";
    let plan: PlaybackPlan | null = null;
    let hls: { destroy(): void } | null = null;
    let save: (value: object, leaving?: boolean) => void = () => undefined;
    let switching = false;
    let idle = 0;
    let seeking = false;
    let sourceReady = false;
    let sourcePosition = 0;
    let lastSubtitle: HTMLButtonElement | undefined;
    // The profile's choices come with the page; the quality is this playback's own once the viewer picks one (the device's applies until then).
    const preferredSpeed = Number(player.dataset.preferredSpeed) || 1;
    const countdownSeconds = Number(player.dataset.countdown) || 0;
    const autoplay = player.dataset.autoplay === "true";
    let audioIndex: number | null = selectedAudio();
    let quality: number | null = null;
    let countdown = 0;
    const next = player.querySelector<HTMLElement>("[data-role=next]")!;

    // Skippable stretches come with the page, so a new plan (another quality or audio track) keeps them.
    const markers = parseMarkers(player.dataset.markers);
    const introMode = parseMode(player.dataset.skipIntro);
    const outroMode = parseMode(player.dataset.skipOutro);
    const skip = player.querySelector<HTMLButtonElement>("[data-action=skip]")!;
    const skipped = player.querySelector<HTMLElement>("[data-role=skipped]")!;
    const chapters = Array.from(player.querySelectorAll<HTMLButtonElement>("[data-chapter]"));
    const handled = new Set<string>();
    let offered: Marker | null = null;
    let currentChapter = -1;
    let undoPosition: number | null = null;
    let skippedTimer = 0;
    let leaving = false;
    const settings = player.querySelector<HTMLElement>('#player-settings')!;
    const sidebar = player.querySelector<HTMLElement>('#player-sidebar');
    const narrow = matchMedia('(max-width: 1100px)');
    player.querySelectorAll<HTMLElement>('[data-player-tabs]').forEach(wrapper => {
        const nav = wrapper.querySelector<HTMLElement>('nav')!;
        nav.setAttribute('role', 'tablist');
        const links = Array.from(nav.querySelectorAll<HTMLAnchorElement>('a'));
        selectTab(wrapper, links.find(link => link.hasAttribute('aria-current'))!.hash);
        nav.addEventListener('click', event => {
            const link = (event.target as Element).closest<HTMLAnchorElement>('a');
            if (link) { event.preventDefault(); selectTab(wrapper, link.hash); }
        });
        nav.addEventListener('keydown', event => {
            const index = links.indexOf(document.activeElement as HTMLAnchorElement);
            const delta = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
            if (!delta && event.key !== 'Home' && event.key !== 'End') return;
            event.preventDefault();
            const target = links[event.key === 'Home' ? 0 : event.key === 'End' ? links.length - 1 : (index + delta + links.length) % links.length];
            selectTab(wrapper, target.hash); target.focus();
        });
    });
    settings.addEventListener('toggle', wake);
    sidebar?.addEventListener('toggle', wake);
    narrow.addEventListener('change', layout);
    document.addEventListener('fullscreenchange', layout);
    try { player.classList.toggle('player--theater', localStorage.getItem('jularr.player.layout') === 'wide'); } catch { /* Storage is optional. */ }
    layout();

    showLoading();
    video.playbackRate = preferredSpeed;
    plan = await requestPlan(audioIndex, quality);
    if (!plan) return;
    await attach(plan, plan.resumePositionMs / 1000);

    // The subtitle the profile's preferences picked shows from the start.
    const preferredSubtitle = player.querySelector<HTMLButtonElement>('[data-subtitle][aria-pressed=true]:not([data-subtitle=""]):not(:disabled)');
    if (preferredSubtitle) {
        lastSubtitle = preferredSubtitle;
        showSubtitle(preferredSubtitle.dataset.subtitle ?? "", preferredSubtitle.dataset.language);
    }
    document.body.classList.add("player-active");
    const minimizeButton = player.querySelector<HTMLButtonElement>("[data-action=minimize]");
    if (minimizeButton) minimizeButton.hidden = false;
    const playerAddress = location.pathname + location.search;
    const backHref = player.querySelector<HTMLAnchorElement>("[data-action=close]")?.href ?? "/";

    // Save acknowledged progress only; each plan has its own session.
    const report = (ended: boolean, completed = ended) => {
        if (!plan || !sourceReady && !ended) return;
        save({
            positionMs: Math.floor((sourceReady ? video.currentTime : sourcePosition) * 1000),
            durationMs: Number.isFinite(video.duration) && video.duration > 0 ? Math.round(video.duration * 1000) : null,
            isEnded: ended,
            isCompleted: completed
        }, ended);
        if (ended) plan = null;
    };
    setInterval(() => { if (!video.paused) report(false); }, ReportInterval);
    // The pause before an end is no position worth saving.
    video.addEventListener("pause", () => { if (!video.ended) report(false); });
    video.addEventListener("ended", () => {
        report(true);
        // An album continues with the next track at once; a season's next episode follows after a countdown, and not at all when the profile turned autoplay off.
        // The mini player stops where it is.
        const href = player.querySelector<HTMLElement>("[data-action=next][data-href]")?.dataset.href;
        if (!href || player.classList.contains("player--mini")) return;
        if (countdownSeconds === 0) location.assign(href);
        else if (autoplay) startCountdown(href);
    });
    video.addEventListener("play", stopCountdown);

    // The countdown ends with a tap on Cancel, a new play, or Play now; at zero the next episode opens.
    function startCountdown(href: string): void {
        stopCountdown();
        countdown = countdownSeconds;
        const label = next.querySelector<HTMLElement>("[data-role=next-text]")!;
        const tick = () => { label.textContent = (label.dataset.template ?? "").replace("{0}", String(countdown)); };
        tick();
        next.hidden = false;
        next.dataset.href = href;
        next.dataset.timer = String(window.setInterval(() => {
            countdown--;
            if (countdown <= 0) { stopCountdown(); location.assign(href); } else tick();
        }, 1000));
    }

    function stopCountdown(): void {
        window.clearInterval(Number(next.dataset.timer));
        delete next.dataset.timer;
        next.hidden = true;
    }
    document.addEventListener("visibilitychange", () => { if (document.hidden) report(false); });
    window.addEventListener("pagehide", event => report(!event.persisted, false));

    function go(direction: "previous" | "next") {
        const href = player.querySelector<HTMLElement>(`[data-action=${direction}][data-href]`)?.dataset.href;
        if (!href) return;
        report(true, false);
        location.assign(href);
    }

    // Seek thumbnails over the timeline where the server has rendered them.
    async function previews(): Promise<void> {
        const url = player.dataset.trickplayUrl;
        const preview = player.querySelector<HTMLElement>("[data-role=preview]");
        if (!url || !preview) return;
        const response = await fetch(url, { credentials: "same-origin" }).catch(() => null);
        if (!response?.ok) return;
        const layout = await (response.json() as Promise<{ intervalSeconds: number; frames: number; columns: number; rows: number; tileWidth: number; tileHeight: number }>).catch(() => null);
        if (!layout || layout.frames < 1) return;
        preview.style.width = `${layout.tileWidth}px`;
        preview.style.height = `${layout.tileHeight}px`;
        const show = (seconds: number, x: number) => {
            const frame = Math.min(layout.frames - 1, Math.max(0, Math.floor(seconds / layout.intervalSeconds)));
            const perSheet = layout.columns * layout.rows;
            const tile = frame % perSheet;
            preview.style.backgroundImage = `url("${url}/${Math.floor(frame / perSheet) + 1}")`;
            preview.style.backgroundPosition = `-${(tile % layout.columns) * layout.tileWidth}px -${Math.floor(tile / layout.columns) * layout.tileHeight}px`;
            // A hidden element has no offset parent; it is shown before it is placed.
            preview.hidden = false;
            const area = (preview.offsetParent ?? timeline).getBoundingClientRect();
            preview.style.left = `${Math.max(0, Math.min(x - area.left - layout.tileWidth / 2, area.width - layout.tileWidth))}px`;
        };
        timeline.addEventListener("pointermove", event => {
            const bounds = timeline.getBoundingClientRect();
            show(Number(timeline.max) * Math.min(1, Math.max(0, (event.clientX - bounds.left) / bounds.width)), event.clientX);
        });
        timeline.addEventListener("input", () => {
            const bounds = timeline.getBoundingClientRect();
            show(Number(timeline.value), bounds.left + bounds.width * (Number(timeline.value) / Math.max(1, Number(timeline.max))));
        });
        for (const name of ["pointerleave", "change"]) timeline.addEventListener(name, () => { preview.hidden = true; });
    }

    // The audio track the profile's preferences picked, where the server marked one.
    function selectedAudio(): number | null {
        const chosen = player.querySelector<HTMLButtonElement>("[data-audio][aria-pressed=true]:not(:disabled)")?.dataset.audio;
        return chosen === undefined ? null : Number(chosen);
    }

    // Planning needs the real browser capabilities; another audio track or quality asks for a new plan.
    async function requestPlan(audioStreamIndex: number | null, qualityChoice: number | null): Promise<PlaybackPlan | null> {
        for (let attempt = 0; ; attempt++) {
            const response = await fetch(player.dataset.startUrl!, {
                method: "POST",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
                body: JSON.stringify({ ...capabilities(), audioStreamIndex, quality: qualityChoice })
            });
            if (response.ok) {
                status.hidden = true;
                const planned = await response.json() as PlaybackPlan;
                if (planned.isReduced) notice("playback_reduced");
                return planned;
            }

            const code = await errorCode(response);
            show(code);
            if (code !== "storage_waking" || attempt >= WakeRetries) return null;
            await new Promise(resolve => setTimeout(resolve, 10_000));
        }
    }

    // Conversions use HLS: native where available, otherwise hls.js on demand.
    async function attach(next: PlaybackPlan, position: number, autoplay = true): Promise<void> {
        if (autoplay) showLoading();
        else stopLoading();
        sourceReady = false;
        sourcePosition = position;
        const rate = video.playbackRate;
        hls?.destroy();
        hls = null;
        player.querySelector<HTMLElement>("[data-role=mode]")!.textContent = text("data-mode", Modes[next.playbackModeTypeId] ?? "");
        save = progressReporter(player.dataset.sessionUrl + next.playbackSessionId, antiforgery, failed => {
            const feedback = player.querySelector<HTMLElement>('[data-role=save-error]');
            if (feedback) feedback.hidden = !failed;
        });
        if (next.playbackModeTypeId !== 1 && video.canPlayType("application/vnd.apple.mpegurl") === "") {
            const { default: Hls } = await import("hls.js/light");
            // The server holds a segment until FFmpeg has it (up to two minutes) and answers a passing stall with 503: wait that long and try
            // again instead of giving up, and keep a minute ahead so a slow moment of the storage does not stop the picture.
            const instance = new Hls({
                startPosition: position,
                maxBufferLength: 60,
                fragLoadPolicy: {
                    default: {
                        maxTimeToFirstByteMs: 125_000,
                        maxLoadTimeMs: 180_000,
                        timeoutRetry: { maxNumRetry: 2, retryDelayMs: 0, maxRetryDelayMs: 0 },
                        errorRetry: { maxNumRetry: 6, retryDelayMs: 1_000, maxRetryDelayMs: 8_000 }
                    }
                }
            });
            instance.on(Hls.Events.ERROR, (_, data) => {
                // A conversion stopped for room answers Gone; the server says which.
                if (data.response?.code === 410) instance.destroy();
                if (data.fatal || data.response?.code === 410) void failure();
            });
            instance.loadSource(next.streamUrl);
            instance.attachMedia(video);
            hls = instance;
        } else {
            // Metadata loads at once so the position applies before the first frame.
            video.preload = "metadata";
            video.src = next.streamUrl;
        }

        // A new source resets the speed; the chosen one stays.
        video.addEventListener("loadedmetadata", () => {
            timeline.max = String(Math.floor(video.duration));
            player.querySelector("[data-role=duration]")!.textContent = clock(video.duration);
            if (position > 0 && position < video.duration) video.currentTime = position;
            video.playbackRate = rate;
            sourceReady = true;
            player.querySelector('[data-role=resolution]')!.textContent = video.videoWidth > 0 ? `${video.videoWidth} × ${video.videoHeight}` : '';
            if (autoplay) void video.play().catch(() => stopLoading());
            else video.pause();
        }, { once: true });
    }

    // The new plan comes first; only then the old session ends and playback continues at the same position.
    async function replan(streamIndex: number | null, qualityChoice: number | null): Promise<boolean> {
        if (switching) return false;
        switching = true;
        try {
            const position = video.currentTime;
            const playing = !video.paused;
            const replacement = await requestPlan(streamIndex, qualityChoice);
            if (!replacement) return false;
            report(true, false);
            plan = replacement;
            audioIndex = streamIndex;
            quality = qualityChoice;
            await attach(replacement, position, playing);
            return true;
        } catch {
            show('failed');
            return false;
        } finally {
            switching = false;
        }
    }

    // Subtitles are WebVTT tracks on the same video; Off removes them.
    function showSubtitle(key: string, language: string | undefined): void {
        video.querySelectorAll("track").forEach(track => track.remove());
        player.querySelector('[data-action=subtitle-toggle]')!.setAttribute('aria-pressed', String(!!key));
        if (!key) return;
        const track = document.createElement("track");
        track.kind = "subtitles";
        track.src = (player.dataset.subtitleUrl ?? "") + encodeURIComponent(key);
        if (language) track.srclang = language;
        track.default = true;
        track.addEventListener("load", () => { track.track.mode = "showing"; });
        track.addEventListener("error", () => {
            show("failed");
            track.remove();
            const off = player.querySelector<HTMLElement>('[data-subtitle=""]');
            if (off) choose("data-subtitle", off);
            player.querySelector('[data-action=subtitle-toggle]')!.setAttribute('aria-pressed', 'false');
        });
        video.append(track);
        track.track.mode = "showing";
    }

    video.addEventListener("error", () => void failure());
    video.addEventListener("waiting", () => { if (!video.paused) showLoading(); });
    video.addEventListener("stalled", () => { if (!video.paused) showLoading(); });
    video.addEventListener("seeking", () => { if (!video.paused) showLoading(); });
    video.addEventListener("playing", stopLoading);
    video.addEventListener("pause", stopLoading);
    video.addEventListener("ended", stopLoading);

    // Transport state.
    video.addEventListener("play", () => setPlaying(true));
    video.addEventListener("pause", () => setPlaying(false));
    video.addEventListener("timeupdate", () => {
        if (!seeking) timeline.value = String(Math.floor(video.currentTime));
        timeline.style.setProperty('--played', `${Number(timeline.value) / Math.max(1, Number(timeline.max)) * 100}%`);
        player.querySelector("[data-role=time]")!.textContent = clock(video.currentTime);
        const currentProgress = player.querySelector<HTMLProgressElement>('.episode-card[aria-current=true] progress');
        if (currentProgress && Number.isFinite(video.duration)) { currentProgress.max = video.duration; currentProgress.value = video.currentTime; }
        offerSkip();
        markChapter();
    });

    // A Skip button while playback is inside a stretch the profile wants offered; Automatic skips once on arrival and says so with a way back.
    function offerSkip(): void {
        const marker = leaving || !sourceReady || player.classList.contains("player--mini") ? null : activeMarker(markers, video.currentTime, introMode, outroMode);
        if (marker && modeFor(marker.kind, introMode, outroMode) === "Automatic" && shouldAutoSkip(marker, video.currentTime, handled)) {
            handled.add(markerKey(marker));
            skipMarker(marker, true);
            return;
        }
        if (marker === offered) return;
        offered = marker;
        skip.hidden = !marker;
        if (marker) skip.querySelector<HTMLElement>("[data-role=skip-text]")!.textContent = text("data-skip-label", marker.kind);
    }

    // Jump to the end of the stretch, or to the next episode where credits run out and it follows by itself.
    function skipMarker(marker: Marker, automatic: boolean): void {
        handled.add(markerKey(marker));
        skip.hidden = true;
        offered = null;
        const hasNext = !!player.querySelector("[data-action=next][data-href]");
        const plan = skipPlan(marker, video.duration, hasNext, autoplay);
        const from = video.currentTime;
        if (plan.type === "next") {
            if (!automatic) { go("next"); return; }
            // The note is the chance to cancel before the next episode opens.
            leaving = true;
            announceSkip(text("data-skipped-label", "Next"), null);
            skippedTimer = window.setTimeout(() => { hideSkipped(); go("next"); }, 5_000);
            return;
        }
        video.currentTime = plan.to;
        if (automatic) announceSkip(text("data-skipped-label", marker.kind), from);
    }

    function announceSkip(text: string, position: number | null): void {
        clearTimeout(skippedTimer);
        undoPosition = position;
        skipped.querySelector<HTMLElement>("[data-role=skipped-text]")!.textContent = text;
        skipped.hidden = false;
        skippedTimer = window.setTimeout(hideSkipped, 6_000);
    }

    function hideSkipped(): void {
        clearTimeout(skippedTimer);
        leaving = false;
        skipped.hidden = true;
        undoPosition = null;
    }

    function undoSkip(): void {
        const position = undoPosition;
        hideSkipped();
        if (position !== null) video.currentTime = position;
    }

    // The chapter playing is marked in the menu.
    function markChapter(): void {
        if (chapters.length === 0) return;
        const index = chapterIndexAt(chapters.map(chapter => ({ start: Number(chapter.dataset.chapter) })), video.currentTime);
        if (index === currentChapter) return;
        currentChapter = index;
        chapters.forEach((chapter, position) => chapter.setAttribute("aria-pressed", String(position === index)));
    }

    video.addEventListener("volumechange", () => {
        volume.value = String(video.muted ? 0 : video.volume);
        volume.style.setProperty('--played', `${Number(volume.value) * 100}%`);
        mute.setAttribute("aria-pressed", String(video.muted));
        player.classList.toggle("player--muted", video.muted);
    });
    timeline.addEventListener('input', () => {
        seeking = true;
        timeline.style.setProperty('--played', `${Number(timeline.value) / Math.max(1, Number(timeline.max)) * 100}%`);
    });
    timeline.addEventListener('change', () => { video.currentTime = Number(timeline.value); seeking = false; wake(); });
    timeline.addEventListener('pointercancel', () => { seeking = false; timeline.value = String(Math.floor(video.currentTime)); wake(); });
    void previews();
    volume.addEventListener("input", () => { video.volume = Number(volume.value); video.muted = video.volume === 0; });
    player.querySelector<HTMLElement>("[data-action=pip]")!.hidden = !document.pictureInPictureEnabled;
    player.querySelector<HTMLElement>('[data-action=fullscreen]')!.hidden = !document.fullscreenEnabled;
    volume.style.setProperty('--played', `${video.volume * 100}%`);

    const actions: Record<string, () => void> = {
        toggle: () => {
            if (video.paused) {
                showTransportFeedback("play");
                void video.play().catch(() => {
                    feedback.hidden = true;
                    stopLoading();
                });
            } else {
                video.pause();
                showTransportFeedback("pause");
            }
        },
        back: () => seek(-10),
        forward: () => seek(30),
        mute: () => { video.muted = !video.muted; },
        fullscreen: () => void (document.fullscreenElement ? document.exitFullscreen() : player.requestFullscreen()).catch(() => show('failed')),
        pip: () => void (document.pictureInPictureElement ? document.exitPictureInPicture() : video.requestPictureInPicture()).catch(() => show('failed')),
        theater: () => {
            const wide = player.classList.toggle('player--theater');
            try { localStorage.setItem('jularr.player.layout', wide ? 'wide' : 'normal'); } catch { /* Storage is optional. */ }
            layout();
        },
        'subtitle-toggle': () => {
            const on = player.querySelector('[data-action=subtitle-toggle]')!.getAttribute('aria-pressed') === 'true';
            const choice = on ? player.querySelector<HTMLButtonElement>('[data-subtitle=""]')
                : lastSubtitle ?? player.querySelector<HTMLButtonElement>('[data-subtitle]:not([data-subtitle=""]):not(:disabled)');
            if (choice) { choose('data-subtitle', choice); showSubtitle(choice.dataset.subtitle ?? '', choice.dataset.language); }
        },
        close: () => void minimize(),
        'next-now': () => { const href = next.dataset.href; stopCountdown(); if (href) location.assign(href); },
        'next-cancel': stopCountdown,
        skip: () => { if (offered) skipMarker(offered, false); },
        'skip-undo': undoSkip,
        previous: () => go("previous"),
        next: () => go("next"),
        minimize: () => void minimize(),
        expand: () => expand(true),
        stop
    };
    player.addEventListener("click", event => {
        const target = event.target as Element;
        const pane = target.closest<HTMLElement>('[data-setting-pane]')?.dataset.settingPane;
        if (pane) {
            selectTab(settings.querySelector('[data-player-tabs]')!, '#player-' + pane);
            settings.querySelector<HTMLElement>('[role=tab][aria-selected=true]')!.focus();
            return;
        }
        const chapter = target.closest<HTMLButtonElement>("[data-chapter]");
        if (chapter) {
            video.currentTime = Number(chapter.dataset.chapter);
            settings.hidePopover();
            return;
        }

        const audio = target.closest<HTMLButtonElement>("[data-audio]");
        if (audio && !audio.disabled && !switching) {
            void replan(Number(audio.dataset.audio), quality).then(success => { if (success) choose('data-audio', audio); });
            return;
        }

        // This playback's quality; the device keeps its own choice.
        const level = target.closest<HTMLButtonElement>("[data-quality]");
        if (level && !level.disabled && !switching) {
            void replan(audioIndex, Number(level.dataset.quality)).then(success => {
                if (!success) return;
                player.querySelectorAll("[data-quality]").forEach(item => item.setAttribute("aria-pressed", String(item === level)));
                player.querySelector("[data-current=quality]")!.textContent = level.textContent;
            });
            return;
        }

        const subtitle = target.closest<HTMLButtonElement>("[data-subtitle]");
        if (subtitle && !subtitle.disabled) {
            choose("data-subtitle", subtitle);
            if (subtitle.dataset.subtitle) lastSubtitle = subtitle;
            showSubtitle(subtitle.dataset.subtitle ?? "", subtitle.dataset.language);
            return;
        }

        const speed = target.closest<HTMLElement>("[data-speed]");
        if (speed) {
            video.playbackRate = Number(speed.dataset.speed);
            player.querySelector('[data-current=speed]')!.textContent = speed.dataset.speed + '×';
            player.querySelectorAll("[data-speed]").forEach(item => item.setAttribute("aria-pressed", String(item === speed)));

            // The profile remembers the speed; a failed save leaves this playback as it is.
            if (player.dataset.speedUrl) {
                void fetch(player.dataset.speedUrl, {
                    method: "PUT",
                    credentials: "same-origin",
                    headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
                    body: JSON.stringify({ speed: Number(speed.dataset.speed) })
                }).catch(() => undefined);
            }
            return;
        }

        const action = target.closest<HTMLElement>("[data-action]")?.dataset.action;
        if (action && actions[action]) { if (action === 'close') event.preventDefault(); actions[action](); }
        else if (target === video) actions.toggle();
    });
    video.addEventListener("dblclick", actions.fullscreen);

    // Keyboard baseline from the Player specification; the mini player leaves the page's keys alone.
    document.addEventListener("keydown", event => {
        if ((event.target as Element).closest('input,select,textarea,button,a,[role=tablist]')) return;
        if (player.classList.contains("player--mini") && !player.contains(event.target as Node)) return;
        const keys: Record<string, () => void> = {
            " ": actions.toggle, k: actions.toggle, ArrowLeft: () => seek(-10), j: () => seek(-10), ArrowRight: () => seek(30), l: () => seek(30),
            ArrowUp: () => { video.volume = Math.min(1, video.volume + 0.05); }, ArrowDown: () => { video.volume = Math.max(0, video.volume - 0.05); },
            m: actions.mute, f: actions.fullscreen
        };
        const handler = keys[event.key.length === 1 ? event.key.toLowerCase() : event.key];
        if (!handler) return;
        event.preventDefault();
        handler();
        wake();
    });

    // Chrome hides only while playing without an open panel.
    player.addEventListener("pointermove", wake);
    player.addEventListener("focusin", wake);
    wake();

    // Minimize keeps the same session in a bar while the app is browsed around it.
    async function minimize(): Promise<void> {
        shrink();
        keepAddress(playerAddress, () => expand(false), shrink);
        await visit(backHref);
    }

    function shrink(): void {
        settings.hidePopover(); sidebar?.hasAttribute('popover') && sidebar.hidePopover();
        player.classList.add("player--mini");
        document.body.classList.add("player-mini");
        document.body.classList.remove("player-page");
    }

    function expand(push: boolean): void {
        player.classList.remove("player--mini");
        document.body.classList.remove("player-mini");
        document.body.classList.add("player-page");
        if (push) pushAddress(playerAddress);
    }

    // Stop ends the session and leaves the app as it is; progress stays.
    function stop(): void {
        report(true, false);
        video.pause();
        hls?.destroy();
        video.removeAttribute("src");
        video.load();
        releaseAddress();
        document.body.classList.remove("player-active", "player-mini");
        player.closest(".player-main")?.remove();
    }

    function seek(seconds: number): void {
        video.currentTime = Math.max(0, Math.min(video.duration || 0, video.currentTime + seconds));
    }

    function setPlaying(playing: boolean): void {
        player.classList.toggle("player--playing", playing);
        toggle.setAttribute("aria-label", (playing ? toggle.dataset.labelPause : toggle.dataset.labelPlay) ?? "");
        wake();
    }

    function wake(): void {
        player.classList.remove("player--idle");
        clearTimeout(idle);
        idle = window.setTimeout(() => {
            if (!video.paused && !seeking && !player.classList.contains("player--mini") && !player.querySelector(':popover-open') && !player.querySelector(':focus-visible')) {
                player.classList.add("player--idle");
            }
        }, IdleDelay);
    }

    function layout(): void {
        const wide = player.classList.contains('player--theater');
        player.querySelector('[data-action=theater]')!.setAttribute('aria-pressed', String(wide));
        const overlay = wide || narrow.matches || document.fullscreenElement === player;
        player.querySelector<HTMLElement>('[data-action=episodes]')!.hidden = !sidebar || !overlay;
        if (!sidebar) return;
        if (sidebar.hasAttribute('popover')) sidebar.hidePopover();
        sidebar.toggleAttribute('popover', overlay);
        if (overlay) sidebar.setAttribute('popover', 'auto');
    }

    function selectTab(wrapper: HTMLElement, hash: string): void {
        const links = wrapper.querySelectorAll<HTMLAnchorElement>('a');
        links.forEach(link => {
            const active = link.hash === hash;
            link.id = link.hash.slice(1) + '-tab';
            link.setAttribute('role', 'tab'); link.setAttribute('aria-selected', String(active));
            link.setAttribute('aria-controls', link.hash.slice(1)); link.removeAttribute('aria-current');
            link.classList.toggle('navigation-tabs__link--active', active); link.tabIndex = active ? 0 : -1;
            const pane = wrapper.parentElement!.querySelector<HTMLElement>(link.hash)!;
            pane.hidden = !active; pane.setAttribute('role', 'tabpanel'); pane.setAttribute('aria-labelledby', link.id);
        });
    }

    function choose(attribute: string, chosen: HTMLElement): void {
        player.querySelectorAll(`[${attribute}]`).forEach(item => item.setAttribute("aria-pressed", String(item === chosen)));
        const label = player.querySelector(`[data-current=${attribute === 'data-audio' ? 'audio' : 'subtitles'}]`);
        if (label) label.textContent = chosen.textContent;
    }

    function show(code: string): void {
        stopLoading();
        status.textContent = text("data-status", code) || text("data-status", "failed");
        status.hidden = false;
    }

    // A message that goes away by itself.
    function notice(code: string): void {
        show(code);
        const shown = status.textContent;
        window.setTimeout(() => { if (status.textContent === shown) status.hidden = true; }, 8_000);
    }

    // A conversion the server stopped to make room says so; anything else is a plain failure.
    async function failure(): Promise<void> {
        stopLoading();
        if (!plan || plan.playbackModeTypeId === 1) {
            show("failed");
            return;
        }

        try {
            const response = await fetch(plan.streamUrl, { credentials: "same-origin" });
            show(response.ok ? "failed" : await errorCode(response));
        } catch {
            show("failed");
        }
    }

    function text(attribute: string, key: string): string {
        return player.querySelector(`[${attribute}="${CSS.escape(key)}"]`)?.textContent ?? "";
    }
}

function capabilities(): { containers: string[]; videoCodecs: string[]; audioCodecs: string[] } {
    const probe = document.createElement("video");
    const video = (type: string) => probe.canPlayType(type) !== "";
    const supported = (types: Record<string, string>) => Object.keys(types).filter(name => video(types[name]));
    const hls = video("application/vnd.apple.mpegurl") || typeof MediaSource !== "undefined" && MediaSource.isTypeSupported('video/mp4; codecs="avc1.42E01E,mp4a.40.2"');
    return {
        containers: [...(hls ? ["hls"] : []), ...supported({
            mp4: "video/mp4", webm: "video/webm", mkv: "video/x-matroska", mp3: "audio/mpeg", ogg: "audio/ogg", flac: "audio/flac", wav: "audio/wav", aac: "audio/aac"
        })],
        videoCodecs: supported({
            h264: 'video/mp4; codecs="avc1.640028"', hevc: 'video/mp4; codecs="hvc1.1.6.L120.90"', vp9: 'video/webm; codecs="vp9"', av1: 'video/mp4; codecs="av01.0.05M.08"'
        }),
        audioCodecs: supported({
            aac: 'audio/mp4; codecs="mp4a.40.2"', mp3: "audio/mpeg", opus: 'audio/webm; codecs="opus"', vorbis: 'audio/webm; codecs="vorbis"',
            flac: "audio/flac", ac3: 'audio/mp4; codecs="ac-3"', eac3: 'audio/mp4; codecs="ec-3"'
        })
    };
}

async function errorCode(response: Response): Promise<string> {
    try {
        const problem = await response.json() as { code?: string };
        return problem.code ?? "failed";
    } catch {
        return "failed";
    }
}

function clock(seconds: number): string {
    const total = Math.max(0, Math.floor(seconds || 0));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor(total / 60) % 60;
    const rest = String(total % 60).padStart(2, "0");
    return hours > 0 ? `${hours}:${String(minutes).padStart(2, "0")}:${rest}` : `${minutes}:${rest}`;
}
