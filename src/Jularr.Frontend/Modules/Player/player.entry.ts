import "./player.scss";
import { updateRangeValue } from "../../Framework/Components/Range";
import { progressReporter } from "../Tracking/Progress";
import { inserted, keepAddress, pushAddress, releaseAddress, visit } from "../Navigation/PageSwap";
import type { CastSender, CastState } from "./Cast";
import { awake, focus } from "../Playback/MediaFocus";
import { activeMarker, chapterIndexAt, markerKey, modeFor, parseMarkers, parseMode, shouldAutoSkip, skipPlan, skipShown, SkipShowSeconds, type Marker } from "./Skip";

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
const MaxRecoveries = 5;
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
    const gesture = player.querySelector<HTMLElement>("[data-role=gesture]")!;
    // Fullscreen belongs to the page's main element, which stays when the next episode replaces the player inside it.
    const host = player.closest<HTMLElement>(".player-main") ?? player;
    // Everything that listens beyond the player's own elements ends with it.
    const lifetime = new AbortController();
    const { signal } = lifetime;
    let loadingTimer = 0;
    let feedbackTimer = 0;
    let lastPointerType = "mouse";

    // Avoid showing a spinner for very short buffer/seek events.
    function stopLoading(): void {
        clearTimeout(loadingTimer);
        loading.hidden = true;
    }

    function showLoading(): void {
        clearTimeout(loadingTimer);
        loadingTimer = window.setTimeout(() => {
            // A transient waiting/seek event is not buffering while future frames are ready.
            if (status.hidden && !video.error && !video.ended
                && (video.readyState < HTMLMediaElement.HAVE_FUTURE_DATA)
                && (!video.paused || !video.currentSrc)) loading.hidden = false;
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
    let hlsPlayer: { recoverMediaError(): void } | null = null;
    let save: (value: object, leaving?: boolean) => void = () => undefined;
    let switching = false;
    // A Chromecast showing this playback: its own session, its own position.
    let casting = false;
    let castSender: CastSender | null = null;
    let castState: CastState = { connected: false, paused: true, currentTime: 0, duration: 0, device: "" };
    let castWasPlaying = false;
    // What the Info pane shows besides the server's facts: measured while it is open.
    let hlsCodecs = "";
    let hlsSegmentMs = 0;
    let hlsBandwidth: () => number = () => 0;
    let idle = 0;
    let seeking = false;
    let recoveries = 0;
    let sourceReady = false;
    let sourcePosition = 0;
    let lastSubtitle: { key: string; language: string | undefined } | undefined;
    // The profile's choices come with the page; the settings change them for this playback only, the quality is this playback's own (the device's applies until then).
    const preferredSpeed = Number(player.dataset.preferredSpeed) || 1;
    const countdownSeconds = Number(player.dataset.countdown) || 0;
    let autoplay = player.dataset.autoplay === "true";
    let audioIndex: number | null = selectedAudio();
    // Audio and subtitles moved against the picture, in milliseconds: the audio is delivered by a new conversion, the subtitles are shifted in the browser.
    let audioOffset = 0;
    let subtitleOffset = 0;
    const cueBase = new WeakMap<TextTrackCue, { start: number; end: number }>();
    let quality: number | null = null;
    const initialQuality = player.querySelector<HTMLSelectElement>('select[data-setting=quality]')?.value ?? "";
    let countdown = 0;
    const next = player.querySelector<HTMLElement>("[data-role=next]")!;

    // Skippable stretches come with the page, so a new plan (another quality or audio track) keeps them.
    const markers = parseMarkers(player.dataset.markers);
    let introMode = parseMode(player.dataset.skipIntro);
    let outroMode = parseMode(player.dataset.skipOutro);
    const skip = player.querySelector<HTMLButtonElement>("[data-action=skip]")!;
    const skipped = player.querySelector<HTMLElement>("[data-role=skipped]")!;
    const chapters = Array.from(player.querySelectorAll<HTMLButtonElement>(".player__chapter[data-chapter]"));
    const handled = new Set<string>();
    let offered: Marker | null = null;
    let skipAged = false;
    let skipAgeTimer = 0;
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
    player.querySelector('#player-chapters')?.addEventListener('toggle', wake);
    player.querySelectorAll<HTMLInputElement>('[data-setting=speed-range]').forEach(updateRangeValue);
    sidebar?.addEventListener('toggle', wake);
    narrow.addEventListener('change', layout, { signal });
    document.addEventListener('fullscreenchange', () => { layout(); orient(); }, { signal });
    try { player.classList.toggle('player--theater', localStorage.getItem('jularr.player.layout') === 'wide'); } catch { /* Storage is optional. */ }
    layout();

    showLoading();
    video.playbackRate = preferredSpeed;
    plan = await requestPlan(audioIndex, quality);
    if (!plan) return;
    await attach(plan, plan.resumePositionMs / 1000);

    // The subtitle the profile's preferences picked shows from the start.
    const preferredSubtitle = player.querySelector<HTMLOptionElement>('select[data-setting=subtitle] option[data-chosen]:not([value=""]):not(:disabled)');
    if (preferredSubtitle) {
        lastSubtitle = { key: preferredSubtitle.value, language: preferredSubtitle.dataset.language };
        showSubtitle(preferredSubtitle.value, preferredSubtitle.dataset.language);
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
            positionMs: Math.floor((casting ? castState.currentTime : sourceReady ? video.currentTime : sourcePosition) * 1000),
            durationMs: casting ? (castState.duration > 0 ? Math.round(castState.duration * 1000) : null)
                : Number.isFinite(video.duration) && video.duration > 0 ? Math.round(video.duration * 1000) : null,
            isEnded: ended,
            isCompleted: completed
        }, ended);
        if (ended) plan = null;
    };
    const reportTimer = setInterval(() => { if (casting ? !castState.paused : !video.paused) report(false); }, ReportInterval);
    // The pause before an end is no position worth saving.
    video.addEventListener("pause", () => { if (!video.ended) report(false); });
    video.addEventListener("ended", () => {
        report(true);
        // An album continues with the next track at once; a season's next episode follows after a countdown, and not at all when the profile turned autoplay off.
        // The mini player stops where it is.
        const href = player.querySelector<HTMLElement>("[data-action=next][data-href]")?.dataset.href;
        if (!href || player.classList.contains("player--mini")) return;
        if (countdownSeconds === 0) void openEpisode(href);
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
            if (countdown <= 0) { stopCountdown(); void openEpisode(href); } else tick();
        }, 1000));
    }

    function stopCountdown(): void {
        window.clearInterval(Number(next.dataset.timer));
        delete next.dataset.timer;
        next.hidden = true;
    }
    document.addEventListener("visibilitychange", () => { if (document.hidden) report(false); }, { signal });
    window.addEventListener("pagehide", event => report(!event.persisted, false), { signal });

    function go(direction: "previous" | "next") {
        const href = player.querySelector<HTMLElement>(`[data-action=${direction}][data-href]`)?.dataset.href;
        if (!href) return;
        report(true, false);
        void openEpisode(href);
    }

    // The next episode replaces the player inside the page's main element; in fullscreen this keeps fullscreen, otherwise the page changes as always.
    async function openEpisode(href: string): Promise<void> {
        if (document.fullscreenElement !== host || host === player || player.classList.contains("player--mini")) { location.assign(href); return; }
        try {
            const response = await fetch(href, { credentials: "same-origin", headers: { Accept: "text/html" } });
            const address = new URL(response.url);
            if (!response.ok || address.origin !== location.origin) throw new Error("episode");
            const page = new DOMParser().parseFromString(await response.text(), "text/html");
            const main = page.querySelector<HTMLElement>(".player-main");
            if (!main?.querySelector('.player[data-start-url]:not([data-start-url=""])')) throw new Error("episode");
            dispose();
            host.replaceChildren(...Array.from(main.childNodes));
            document.title = page.title;
            pushAddress(address.pathname + address.search);
            inserted(host);
            const replacement = host.querySelector<HTMLElement>('.player[data-start-url]:not([data-start-url=""])')!;
            void start(replacement).catch(() => undefined);
        } catch {
            location.assign(href);
        }
    }

    // The old playback ends without touching the file's progress: the next episode starts its own session.
    function dispose(): void {
        lifetime.abort();
        clearInterval(reportTimer);
        clearTimeout(idle);
        clearTimeout(loadingTimer);
        stopCountdown();
        video.pause();
        hls?.destroy();
        hls = null;
        hlsPlayer = null;
        video.removeAttribute("src");
        video.load();
    }

    // Always show the target time while hovering or dragging; trickplay images are optional.
    async function previews(): Promise<void> {
        const preview = player.querySelector<HTMLElement>("[data-role=preview]");
        if (!preview) return;
        const url = player.dataset.trickplayUrl;
        type Layout = { intervalSeconds: number; frames: number; columns: number; rows: number; tileWidth: number; tileHeight: number };
        let layout: Layout | null = null;
        let dragging = false;
        const time = document.createElement("span");
        time.className = "player__preview-time";
        const chapterName = document.createElement("span");
        chapterName.className = "player__preview-chapter";
        preview.append(time, chapterName);

        const show = (seconds: number, x: number) => {
            const duration = Number(timeline.max);
            if (!Number.isFinite(duration) || duration <= 0) return;
            const target = Math.min(duration, Math.max(0, seconds));
            time.textContent = clock(target);
            const title = chapterTitleAt(target);
            chapterName.textContent = title;
            chapterName.hidden = title === "";
            if (url && layout && layout.frames > 0 && layout.intervalSeconds > 0 && layout.columns > 0 && layout.rows > 0) {
                const frame = Math.min(layout.frames - 1, Math.max(0, Math.floor(target / layout.intervalSeconds)));
                const perSheet = layout.columns * layout.rows;
                const tile = frame % perSheet;
                preview.style.backgroundImage = `url("${url}/${Math.floor(frame / perSheet) + 1}")`;
                preview.style.backgroundPosition = `-${(tile % layout.columns) * layout.tileWidth}px -${Math.floor(tile / layout.columns) * layout.tileHeight}px`;
            }
            preview.hidden = false;
            const area = (preview.offsetParent ?? timeline).getBoundingClientRect();
            preview.style.left = `${Math.max(0, Math.min(x - area.left - preview.offsetWidth / 2, area.width - preview.offsetWidth))}px`;
        };
        const showAtPointer = (clientX: number) => {
            const bounds = timeline.getBoundingClientRect();
            show(Number(timeline.max) * Math.min(1, Math.max(0, (clientX - bounds.left) / Math.max(bounds.width, 1))), clientX);
        };
        timeline.addEventListener("pointermove", event => showAtPointer(event.clientX));
        timeline.addEventListener("pointerdown", event => { dragging = true; showAtPointer(event.clientX); });
        timeline.addEventListener("input", () => {
            const bounds = timeline.getBoundingClientRect();
            show(Number(timeline.value), bounds.left + bounds.width * (Number(timeline.value) / Math.max(1, Number(timeline.max))));
        });
        timeline.addEventListener("pointerleave", () => { if (!dragging) preview.hidden = true; });
        for (const name of ["pointerup", "pointercancel", "change"]) timeline.addEventListener(name, () => { dragging = false; preview.hidden = true; });
        if (!url) return;
        const response = await fetch(url, { credentials: "same-origin" }).catch(() => null);
        if (!response?.ok) return;
        const result = await (response.json() as Promise<Layout>).catch(() => null);
        if (!result || result.frames < 1 || result.intervalSeconds <= 0 || result.columns <= 0 || result.rows <= 0) return;
        layout = result;
        preview.classList.add("player__preview--image");
        preview.style.width = `${result.tileWidth}px`;
        preview.style.height = `${result.tileHeight}px`;
    }

    // The audio track the profile's preferences picked, where the server marked one.
    function selectedAudio(): number | null {
        const chosen = player.querySelector<HTMLOptionElement>("select[data-setting=audio] option[data-chosen]:not(:disabled)")?.value;
        return chosen === undefined || chosen === "" ? null : Number(chosen);
    }

    // Planning needs the real browser capabilities; another audio track or quality asks for a new plan.
    async function requestPlan(audioStreamIndex: number | null, qualityChoice: number | null): Promise<PlaybackPlan | null> {
        for (let attempt = 0; ; attempt++) {
            const response = await fetch(player.dataset.startUrl!, {
                method: "POST",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
                body: JSON.stringify({ ...capabilities(), audioStreamIndex, quality: qualityChoice, audioOffsetMs: audioOffset })
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
        hlsPlayer = null;
        hlsCodecs = "";
        hlsSegmentMs = 0;
        hlsBandwidth = () => 0;
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
                if (data.response?.code === 410) { instance.destroy(); void failure(); return; }
                if (!data.fatal) return;
                // A dropped connection or a broken segment in the middle of the film is retried in place a few times before the player gives up,
                // so the page needs no reload; a picture that plays again resets the count.
                if (recoveries < MaxRecoveries) {
                    recoveries++;
                    if (data.type === Hls.ErrorTypes.NETWORK_ERROR) { instance.startLoad(); return; }
                    if (data.type === Hls.ErrorTypes.MEDIA_ERROR) { instance.recoverMediaError(); return; }
                }
                void failure();
            });
            instance.on(Hls.Events.BUFFER_CODECS, (_, data) => { hlsCodecs = [data.video?.codec, data.audio?.codec].filter(Boolean).join(" \u00b7 "); });
            instance.on(Hls.Events.FRAG_LOADED, (_, data) => { hlsSegmentMs = data.frag.stats.loading.end - data.frag.stats.loading.start; });
            hlsBandwidth = () => instance.bandwidthEstimate;
            instance.loadSource(next.streamUrl);
            instance.attachMedia(video);
            hls = instance;
            hlsPlayer = instance;
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
            placeMarks();
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
            setChoice("subtitle", "");
            player.querySelector('[data-action=subtitle-toggle]')!.setAttribute('aria-pressed', 'false');
        });
        video.append(track);
        track.track.mode = "showing";
        track.addEventListener("load", applySubtitleOffset);
        applySubtitleOffset();
    }

    // The picture element reports a broken stretch as well (decoding, a segment that does not fit): an HLS playback is repaired in place first, like a fatal error of the loader.
    video.addEventListener("error", () => {
        if (hlsPlayer && recoveries < MaxRecoveries) { recoveries++; hlsPlayer.recoverMediaError(); return; }
        void failure();
    });
    video.addEventListener("waiting", () => { if (!video.paused) showLoading(); });
    video.addEventListener("stalled", () => { if (!video.paused) showLoading(); });
    video.addEventListener("seeking", () => { if (!video.paused) showLoading(); });
    video.addEventListener("canplay", stopLoading);
    video.addEventListener("seeked", () => { if (video.readyState >= HTMLMediaElement.HAVE_FUTURE_DATA) stopLoading(); });
    video.addEventListener("playing", () => { recoveries = 0; stopLoading(); });
    video.addEventListener("pause", stopLoading);
    video.addEventListener("ended", stopLoading);

    // Transport state.
    video.addEventListener("play", () => setPlaying(true));
    video.addEventListener("pause", () => setPlaying(false));
    video.addEventListener("timeupdate", () => {
        if (video.readyState >= HTMLMediaElement.HAVE_FUTURE_DATA) stopLoading();
        if (!seeking) timeline.value = String(Math.floor(video.currentTime));
        timeline.style.setProperty('--played', `${Number(timeline.value) / Math.max(1, Number(timeline.max)) * 100}%`);
        player.querySelector("[data-role=time]")!.textContent = clock(video.currentTime);
        const currentProgress = player.querySelector<HTMLProgressElement>('.episode-card[aria-current=true] progress');
        if (currentProgress && Number.isFinite(video.duration)) { currentProgress.max = video.duration; currentProgress.value = video.currentTime; }
        offerSkip();
        markChapter();
        showBuffered();
    });
    video.addEventListener("progress", showBuffered);

    // The timeline shows how far the picture is loaded beyond the position.
    function showBuffered(): void {
        let end = 0;
        for (let index = 0; index < video.buffered.length; index++) {
            if (video.buffered.start(index) <= video.currentTime + 0.5) end = Math.max(end, video.buffered.end(index));
        }
        timeline.style.setProperty("--buffered", `${Math.min(100, end / Math.max(1, Number(timeline.max)) * 100)}%`);
    }

    // Chapter ticks stand at their share of the duration once it is known.
    function placeMarks(): void {
        const marks = player.querySelector<HTMLElement>(".player__marks");
        if (!marks || !Number.isFinite(video.duration) || video.duration <= 0) return;
        marks.querySelectorAll<HTMLElement>("[data-chapter]").forEach(mark => mark.style.setProperty("--at", String(Number(mark.dataset.chapter) / video.duration * 100)));
        marks.dataset.ready = "";
    }

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
        ageSkip();
        if (marker) skip.querySelector<HTMLElement>("[data-role=skip-text]")!.textContent = text("data-skip-label", marker.kind);
    }

    // The button shows for a few seconds when a stretch begins; afterwards it returns only while the controls are shown and the stretch lasts.
    function ageSkip(): void {
        clearTimeout(skipAgeTimer);
        skipAged = false;
        if (offered) skipAgeTimer = window.setTimeout(() => { skipAged = true; refreshSkip(); }, SkipShowSeconds * 1000);
        refreshSkip();
    }

    function refreshSkip(): void {
        skip.hidden = !skipShown(offered !== null, skipAged, !player.classList.contains("player--idle"));
    }
    const controlsWatch = new MutationObserver(refreshSkip);
    controlsWatch.observe(player, { attributes: true, attributeFilter: ["class"] });
    signal.addEventListener("abort", () => { clearTimeout(skipAgeTimer); controlsWatch.disconnect(); awake.drop("player"); focus.release("player"); });

    // Jump to the end of the stretch, or to the next episode where credits run out and it follows by itself.
    function skipMarker(marker: Marker, automatic: boolean): void {
        handled.add(markerKey(marker));
        offered = null;
        ageSkip();
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

    // The chapter at a position; the hover preview names it.
    function chapterTitleAt(seconds: number): string {
        if (chapters.length === 0) return "";
        const index = chapterIndexAt(chapters.map(chapter => ({ start: Number(chapter.dataset.chapter) })), seconds);
        return index < 0 ? "" : chapters[index].dataset.title ?? "";
    }

    // The chapter playing is marked in the menu and named under the timeline.
    function markChapter(): void {
        if (chapters.length === 0) return;
        const index = chapterIndexAt(chapters.map(chapter => ({ start: Number(chapter.dataset.chapter) })), video.currentTime);
        if (index === currentChapter) return;
        currentChapter = index;
        const now = player.querySelector<HTMLElement>("[data-role=chapter-now]");
        if (now) { now.textContent = chapterTitleAt(video.currentTime); now.hidden = index < 0; }
        chapters.forEach((chapter, position) => chapter.setAttribute("aria-pressed", String(position === index)));
    }

    video.addEventListener("volumechange", () => {
        volume.value = String(video.muted ? 0 : video.volume);
        volume.style.setProperty('--played', `${Number(volume.value) * 100}%`);
        mute.setAttribute("aria-pressed", String(video.muted));
        player.classList.toggle("player--muted", video.muted);
        player.dataset.volumeLevel = video.muted || video.volume === 0 ? "0" : video.volume > 0.66 ? "3" : video.volume > 0.33 ? "2" : "1";
    });
    timeline.addEventListener('input', () => {
        seeking = true;
        timeline.style.setProperty('--played', `${Number(timeline.value) / Math.max(1, Number(timeline.max)) * 100}%`);
    });
    timeline.addEventListener('change', () => { if (casting) castSender?.seek(Number(timeline.value)); else video.currentTime = Number(timeline.value); seeking = false; wake(); });
    timeline.addEventListener('pointercancel', () => { seeking = false; timeline.value = String(Math.floor(video.currentTime)); wake(); });
    void previews();
    volume.addEventListener("input", () => { video.volume = Number(volume.value); video.muted = video.volume === 0; });
    player.querySelector<HTMLElement>("[data-action=pip]")!.hidden = !document.pictureInPictureEnabled;
    player.querySelector<HTMLElement>('[data-action=fullscreen]')!.hidden = !document.fullscreenEnabled;
    volume.style.setProperty('--played', `${video.volume * 100}%`);

    const actions: Record<string, () => void> = {
        "audio-offset-reset": () => void setAudioOffset(0),
        "subtitle-offset-reset": () => setSubtitleOffset(0),
        cast: () => void (casting ? castSender?.stop() : startCastRequest()),
        "copy-info": () => void copyInfo(),
        toggle: () => {
            if (casting) { castSender?.playOrPause(); return; }
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
        fullscreen: () => void (document.fullscreenElement ? document.exitFullscreen() : host.requestFullscreen()).catch(() => show('failed')),
        pip: () => void (document.pictureInPictureElement ? document.exitPictureInPicture() : video.requestPictureInPicture()).catch(() => show('failed')),
        theater: () => {
            const wide = player.classList.toggle('player--theater');
            try { localStorage.setItem('jularr.player.layout', wide ? 'wide' : 'normal'); } catch { /* Storage is optional. */ }
            layout();
        },
        'subtitle-toggle': () => {
            const on = player.querySelector('[data-action=subtitle-toggle]')!.getAttribute('aria-pressed') === 'true';
            const choice = on ? { key: "", language: undefined } : lastSubtitle ?? firstSubtitle();
            if (choice) { setChoice('subtitle', choice.key); showSubtitle(choice.key, choice.language); }
        },
        close: () => void minimize(),
        'next-now': () => { const href = next.dataset.href; stopCountdown(); if (href) void openEpisode(href); },
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
        const chapter = target.closest<HTMLButtonElement>("[data-chapter]");
        if (chapter) {
            video.currentTime = Number(chapter.dataset.chapter);
            chapter.closest<HTMLElement>("[popover]")?.hidePopover();
            return;
        }

        // The usual speeds are one tap away.
        const point = target.closest<HTMLElement>(".player__speed-point");
        if (point) { setSpeed(Number(point.dataset.speed), true); return; }

        const action = target.closest<HTMLElement>("[data-action]")?.dataset.action;
        if (action && actions[action]) { if (action === 'close') event.preventDefault(); actions[action](); }
        else if (target === video && lastPointerType === "mouse") actions.toggle();
    });
    // A double click of the mouse toggles fullscreen; a double tap of a finger seeks (below).
    video.addEventListener("dblclick", () => { if (lastPointerType === "mouse") actions.fullscreen(); });

    // The settings are native fields; the same choice shown in two panes stays one choice.
    settings.addEventListener("change", event => {
        const field = event.target as HTMLInputElement | HTMLSelectElement;
        switch (field.dataset.setting) {
            case "audio": {
                const previous = audioIndex === null ? field.value : String(audioIndex);
                if (switching || field.value === "") { setChoice("audio", previous); break; }
                void replan(Number(field.value), quality).then(success => setChoice("audio", success ? field.value : previous));
                break;
            }
            case "quality": {
                const previous = quality === null ? initialQuality : String(quality);
                if (switching) { setChoice("quality", previous); break; }
                void replan(audioIndex, Number(field.value)).then(success => setChoice("quality", success ? field.value : previous));
                break;
            }
            case "subtitle": {
                const option = (field as HTMLSelectElement).selectedOptions[0];
                if (field.value) lastSubtitle = { key: field.value, language: option?.dataset.language };
                setChoice("subtitle", field.value);
                showSubtitle(field.value, option?.dataset.language);
                break;
            }
            case "speed-range": case "speed-number": setSpeed(Number(field.value), true); break;
            case "fit": for (const mode of ["cover", "original"]) player.classList.toggle(`player--fit-${mode}`, field.value === mode); break;
            case "autoplay": autoplay = (field as HTMLInputElement).checked; break;
            case "skip-intro": introMode = parseMode(field.value); handled.clear(); break;
            case "skip-outro": outroMode = parseMode(field.value); handled.clear(); break;
        }
    });
    settings.addEventListener("input", event => {
        const field = event.target as HTMLInputElement;
        if (field.dataset.setting === "speed-range") setSpeed(Number(field.value), false);
    });

    // One speed for every control that shows it; the profile remembers it once the viewer has chosen.
    function setSpeed(value: number, remember: boolean): void {
        if (!Number.isFinite(value)) return;
        const speed = Math.min(4, Math.max(0.25, Math.round(value * 100) / 100));
        video.playbackRate = speed;
        player.querySelectorAll<HTMLInputElement>("[data-setting^=speed-]").forEach(input => {
            if (input !== document.activeElement || input.type === "range") input.value = String(speed);
            if (input.type === "range") updateRangeValue(input);
        });
        player.querySelectorAll("[data-speed]").forEach(item => item.setAttribute("aria-pressed", String(Number((item as HTMLElement).dataset.speed) === speed)));
        // The profile keeps every speed the player offers, 0.25 to 4.
        if (remember && player.dataset.speedUrl) {
            void fetch(player.dataset.speedUrl, {
                method: "PUT",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
                body: JSON.stringify({ speed })
            }).catch(() => undefined);
        }
    }

    // Every field of one setting shows the same value; the facts of the Info pane follow.
    function setChoice(name: string, value: string): void {
        player.querySelectorAll<HTMLSelectElement>(`select[data-setting=${name}]`).forEach(select => { select.value = value; });
        const fact = name === "audio" || name === "quality" ? player.querySelector(`[data-role=fact-${name}]`) : null;
        if (fact) fact.textContent = player.querySelector<HTMLSelectElement>(`select[data-setting=${name}]`)?.selectedOptions[0]?.textContent ?? "";
    }

    function firstSubtitle(): { key: string; language: string | undefined } | undefined {
        const option = player.querySelector<HTMLOptionElement>('select[data-setting=subtitle] option:not([value=""]):not(:disabled)');
        return option ? { key: option.value, language: option.dataset.language } : undefined;
    }

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
    }, { signal });

    // Chrome hides only while playing without an open panel.
    player.addEventListener("pointermove", event => { if (event.pointerType !== "touch") wake(); });
    player.addEventListener("pointerdown", event => { lastPointerType = event.pointerType; if (event.pointerType === "touch" && event.target !== video) wake(); }, { capture: true });
    touchGestures();
    mediaSession();
    castSupport();
    infoPane();
    offsetFields();
    // A mouse leaving the picture hides the controls at once; touch and open panels keep them.
    player.addEventListener("pointerleave", event => {
        if (event.pointerType !== "mouse" || video.paused || seeking || player.classList.contains("player--mini") || player.querySelector(":popover-open")) return;
        clearTimeout(idle);
        player.classList.add("player--idle");
    });
    player.addEventListener("focusin", wake);
    wake();

    // Minimize keeps the same session in a bar while the app is browsed around it.
    async function minimize(): Promise<void> {
        shrink();
        keepAddress(playerAddress, () => expand(false), shrink);
        await visit(backHref);
    }

    function shrink(): void {
        player.querySelectorAll<HTMLElement>('.player__settings, .player__chapters').forEach(panel => panel.hidePopover());
        sidebar?.hasAttribute('popover') && sidebar.hidePopover();
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
        if (navigator.mediaSession) { navigator.mediaSession.metadata = null; navigator.mediaSession.playbackState = "none"; }
        releaseAddress();
        document.body.classList.remove("player-active", "player-mini");
        player.closest(".player-main")?.remove();
    }

    function seek(seconds: number): void {
        if (casting) { castSender?.seek(Math.max(0, Math.min(castState.duration || 0, castState.currentTime + seconds))); return; }
        video.currentTime = Math.max(0, Math.min(video.duration || 0, video.currentTime + seconds));
    }

    // The two offset fields: the audio asks the server for a new conversion that plays on from here, the subtitles move at once.
    function offsetFields(): void {
        const value = (input: HTMLInputElement) => Math.max(-10_000, Math.min(10_000, Math.round(Number(input.value) || 0)));
        const audioInput = player.querySelector<HTMLInputElement>("[data-setting=audio-offset]");
        const subtitleInput = player.querySelector<HTMLInputElement>("[data-setting=subtitle-offset]");
        audioInput?.addEventListener("change", () => void setAudioOffset(value(audioInput)));
        subtitleInput?.addEventListener("input", () => setSubtitleOffset(value(subtitleInput)));
    }

    async function setAudioOffset(milliseconds: number): Promise<void> {
        const previous = audioOffset;
        audioOffset = milliseconds;
        if (milliseconds !== previous && !await replan(audioIndex, quality)) audioOffset = previous;
        player.querySelectorAll<HTMLInputElement>("[data-setting=audio-offset]").forEach(input => { input.value = String(audioOffset); });
    }

    function setSubtitleOffset(milliseconds: number): void {
        subtitleOffset = milliseconds;
        player.querySelectorAll<HTMLInputElement>("[data-setting=subtitle-offset]").forEach(input => { if (input !== document.activeElement) input.value = String(milliseconds); });
        applySubtitleOffset();
    }

    // Every cue keeps its own times; the offset is added to those, so moving it again never adds up.
    function applySubtitleOffset(): void {
        const cues = Array.from(video.textTracks).find(track => track.mode === "showing")?.cues;
        if (!cues) return;
        for (const cue of Array.from(cues)) {
            let base = cueBase.get(cue);
            if (!base) { base = { start: cue.startTime, end: cue.endTime }; cueBase.set(cue, base); }
            cue.startTime = Math.max(0, base.start + subtitleOffset / 1000);
            cue.endTime = Math.max(cue.startTime, base.end + subtitleOffset / 1000);
        }
    }

    // The Info pane: what the server knows of the file and its conversion, and what the player measures meanwhile, refreshed every second while the settings are open.
    function infoPane(): void {
        const info = player.querySelector<HTMLElement>(".player__info");
        if (!info) return;
        const set = (role: string, value: string) => { const target = player.querySelector(`[data-role=${role}]`); if (target) target.textContent = value; };
        const rate = (bits: number) => (bits / 1_000_000).toFixed(1) + " Mbit/s";
        const copyText = info.dataset.copied ?? "";
        const convertedText = info.dataset.converted ?? "";
        let factsFor = "";
        let sample: { at: number; frames: number } | null = null;
        let measured = "";
        let timer = 0;

        const loadFacts = async () => {
            if (!plan || factsFor === plan.playbackSessionId) return;
            const id = plan.playbackSessionId;
            factsFor = id;
            try {
                const response = await fetch(`/api/v1/playback/sessions/${id}/facts`, { credentials: "same-origin" });
                if (!response.ok) { factsFor = ""; return; }
                const facts = await response.json() as {
                    container: string | null; bitRate: number | null; videoCodec: string | null; videoWidth: number | null; videoHeight: number | null; bitDepth: number | null; dynamicRange: string | null;
                    audioCodec: string | null; audioChannels: number | null; audioLanguage: string | null; copyVideo: boolean | null; copyAudio: boolean | null;
                    targetHeight: number | null; videoKilobits: number | null; audioKilobits: number | null; encoder: string | null;
                };
                const join = (...parts: (string | number | null | undefined | false)[]) => parts.filter(part => part !== null && part !== undefined && part !== false && part !== "").join(" \u00b7 ") || "\u2013";
                set("fact-container", facts.container ?? "\u2013");
                set("fact-bitrate", facts.bitRate ? rate(facts.bitRate) : "\u2013");
                set("fact-video", join(facts.videoCodec?.toUpperCase(), facts.videoWidth && facts.videoHeight && `${facts.videoWidth}\u00d7${facts.videoHeight}`, facts.bitDepth && `${facts.bitDepth}-bit`, facts.dynamicRange));
                set("fact-audiofile", join(facts.audioCodec?.toUpperCase(), facts.audioChannels && `${facts.audioChannels} ch`, facts.audioLanguage));
                set("fact-videopath", facts.copyVideo === null ? "\u2013" : facts.copyVideo ? copyText
                    : join(convertedText, facts.encoder, facts.targetHeight && `${facts.targetHeight}p`, facts.videoKilobits && `${facts.videoKilobits} kbit/s`));
                set("fact-audiopath", facts.copyAudio === null ? "\u2013" : facts.copyAudio ? copyText : join(convertedText, facts.audioKilobits && `${facts.audioKilobits} kbit/s`));
            } catch {
                factsFor = "";
            }
        };

        const refresh = () => {
            const quality = video.getVideoPlaybackQuality?.();
            if (quality && !video.paused) {
                const now = performance.now();
                if (sample && now > sample.at) measured = ((quality.totalVideoFrames - sample.frames) / ((now - sample.at) / 1000)).toFixed(2);
                sample = { at: now, frames: quality.totalVideoFrames };
            } else {
                sample = null;
            }
            set("fact-frames", quality ? `${quality.totalVideoFrames} / ${quality.droppedVideoFrames}${measured ? " \u00b7 " + measured : ""}` : "\u2013");
            let ahead = 0;
            for (let index = 0; index < video.buffered.length; index++) {
                if (video.buffered.start(index) <= video.currentTime && video.buffered.end(index) >= video.currentTime) ahead = video.buffered.end(index) - video.currentTime;
            }
            set("fact-buffer", `${ahead.toFixed(1)} s`);
            set("fact-codecs", hlsCodecs || "\u2013");
            set("fact-network", [hlsBandwidth() > 0 ? rate(hlsBandwidth()) : "", hlsSegmentMs > 0 ? `${Math.round(hlsSegmentMs)} ms` : ""].filter(Boolean).join(" \u00b7 ") || "\u2013");
            if (video.videoWidth > 0) set("resolution", `${video.videoWidth} \u00d7 ${video.videoHeight}`);
        };

        settings.addEventListener("toggle", () => {
            clearInterval(timer);
            if (!settings.matches(":popover-open")) { sample = null; return; }
            void loadFacts();
            refresh();
            timer = window.setInterval(refresh, 1000);
        }, { signal });
        const copy = info.querySelector<HTMLElement>("[data-action=copy-info]");
        if (copy && navigator.clipboard) copy.hidden = false;
    }

    // The facts as plain text for a bug report.
    async function copyInfo(): Promise<void> {
        const lines = Array.from(player.querySelectorAll(".player__facts > div")).map(row => `${row.querySelector("dt")?.textContent}: ${row.querySelector("dd")?.textContent}`);
        try { await navigator.clipboard.writeText(lines.join("\n")); } catch { /* The browser refused the clipboard. */ }
    }

    // The Cast button shows where Chrome and a secure page allow it; Google's library loads only when it is pressed.
    function castSupport(): void {
        const button = player.querySelector<HTMLElement>("[data-action=cast]");
        if (button) button.hidden = !/Chrome\//.test(navigator.userAgent) || !window.isSecureContext;
    }

    // A device cannot sign in and cannot see this computer's own address.
    async function startCastRequest(): Promise<void> {
        if (["localhost", "127.0.0.1", "[::1]"].includes(location.hostname)) { show("cast_unreachable"); return; }
        try {
            if (!castSender) {
                const { loadCast } = await import("./Cast");
                castSender = await loadCast(state => void onCastUpdate(state));
            }
            if (!castSender) { show("failed"); return; }
            await castSender.request();
        } catch {
            show("failed");
        }
    }

    // A device connected or disconnected, or its player changed: the playback moves there, comes back, or the controls follow it.
    async function onCastUpdate(state: CastState): Promise<void> {
        castState = state;
        if (state.connected && !casting) { await startCasting(); return; }
        if (!state.connected && casting) { await endCasting(); return; }
        if (!casting) return;
        if (state.duration > 0) {
            timeline.max = String(Math.floor(state.duration));
            player.querySelector("[data-role=duration]")!.textContent = clock(state.duration);
        }
        if (!seeking) timeline.value = String(Math.floor(state.currentTime));
        timeline.style.setProperty("--played", `${Number(timeline.value) / Math.max(1, Number(timeline.max)) * 100}%`);
        player.querySelector("[data-role=time]")!.textContent = clock(state.currentTime);
        setPlaying(!state.paused);
    }

    async function startCasting(): Promise<void> {
        if (switching || !plan) { castSender?.stop(); return; }
        switching = true;
        casting = true;
        castWasPlaying = !video.paused;
        const position = video.currentTime;
        video.pause();
        try {
            // The device plays HLS of H.264 and AAC from an address of its own session.
            const response = await fetch(player.dataset.startUrl!, {
                method: "POST",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
                body: JSON.stringify({ containers: ["hls"], videoCodecs: ["h264"], audioCodecs: ["aac"], audioStreamIndex: audioIndex, quality })
            });
            if (!response.ok) throw new Error("cast_plan");
            const planned = await response.json() as PlaybackPlan;
            const address = await fetch(`/api/v1/playback/sessions/${planned.playbackSessionId}/cast`, { credentials: "same-origin" });
            if (!address.ok) throw new Error("cast_address");
            const { url } = await address.json() as { url: string };
            const title = player.querySelector(".player__title")?.textContent ?? "";
            const context = player.querySelector(".player__context")?.textContent ?? "";
            await castSender!.load(location.origin + url, title, context, position);
            report(true, false);
            plan = planned;
            save = progressReporter(player.dataset.sessionUrl + planned.playbackSessionId, antiforgery, failed => {
                const feedback = player.querySelector<HTMLElement>('[data-role=save-error]');
                if (feedback) feedback.hidden = !failed;
            });
            const panel = player.querySelector<HTMLElement>("[data-role=casting]")!;
            const label = panel.querySelector<HTMLElement>("[data-role=casting-text]")!;
            label.textContent = (label.dataset.template ?? "").replace("{0}", castState.device);
            panel.hidden = false;
            player.classList.add("player--casting");
            stopLoading();
        } catch {
            casting = false;
            castSender?.stop();
            show("failed");
        } finally {
            switching = false;
        }
    }

    // The device let go: the playback continues here at the place it reached.
    async function endCasting(): Promise<void> {
        casting = false;
        const position = castState.currentTime;
        report(true, false);
        player.classList.remove("player--casting");
        player.querySelector<HTMLElement>("[data-role=casting]")!.hidden = true;
        video.currentTime = position;
        if (await replan(audioIndex, quality) && castWasPlaying) void video.play().catch(() => undefined);
    }

    // Android and desktop browsers show the playback as media: notification, lock screen and headset keys name the title and carry play, pause, seek and the neighbouring episodes.
    function mediaSession(): void {
        const session = navigator.mediaSession;
        if (!session) return;
        const text = (selector: string) => player.querySelector(selector)?.textContent?.trim() ?? "";
        const poster = video.getAttribute("poster");
        session.metadata = new MediaMetadata({
            title: text(".player__title"),
            artist: text(".player__context"),
            artwork: poster ? [{ src: new URL(poster, location.href).href }] : []
        });
        const set = (action: string, handler: MediaSessionActionHandler | null) => {
            try { session.setActionHandler(action as MediaSessionAction, handler); } catch { /* The browser does not know this action. */ }
        };
        const neighbour = (direction: "previous" | "next") => player.querySelector<HTMLElement>(`[data-action=${direction}][data-href]`) ? () => go(direction) : null;
        set("play", () => { if (casting) { if (castState.paused) castSender?.playOrPause(); } else void video.play(); });
        set("pause", () => { if (casting) { if (!castState.paused) castSender?.playOrPause(); } else video.pause(); });
        set("seekbackward", details => seek(-(details.seekOffset ?? 10)));
        set("seekforward", details => seek(details.seekOffset ?? 30));
        set("seekto", details => { if (details.seekTime === undefined) return; if (casting) castSender?.seek(details.seekTime); else video.currentTime = details.seekTime; });
        set("previoustrack", neighbour("previous"));
        set("nexttrack", neighbour("next"));
        // A phone leaving the app with the video running may carry it into the small window.
        set("enterpictureinpicture", () => void video.requestPictureInPicture().catch(() => undefined));
        const position = () => {
            if (!Number.isFinite(video.duration) || video.duration <= 0) return;
            try { session.setPositionState({ duration: video.duration, playbackRate: video.playbackRate || 1, position: Math.min(video.currentTime, video.duration) }); } catch { /* A position outside the duration is refused. */ }
        };
        for (const name of ["timeupdate", "ratechange", "durationchange", "seeked"]) video.addEventListener(name, position, { signal });
        video.addEventListener("play", () => { session.playbackState = "playing"; }, { signal });
        video.addEventListener("pause", () => { session.playbackState = "paused"; }, { signal });
        video.addEventListener("ended", () => { session.playbackState = "none"; }, { signal });
    }

    function setPlaying(playing: boolean): void {
        player.classList.toggle("player--playing", playing);
        // A playing video keeps the screen on and takes the focus from another medium of this browser; a paused one gives both up.
        if (playing) { awake.hold("player"); focus.claim("player", () => video.pause()); }
        else { awake.drop("player"); focus.release("player"); }
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

    // A phone turns to landscape while the player is fullscreen, where the browser allows it (Android); leaving fullscreen releases it.
    function orient(): void {
        const orientation = screen.orientation as (ScreenOrientation & { lock?(orientation: string): Promise<void> }) | undefined;
        if (!orientation || !matchMedia("(pointer: coarse)").matches) return;
        if (document.fullscreenElement === host) void orientation.lock?.("landscape").catch(() => undefined);
        else orientation.unlock?.();
    }

    // Touch: double tap left/right seeks back/forward, a swipe up or down changes the volume (right half) or the picture's brightness (left half),
    // holding plays at double speed, a single tap shows or hides the controls.
    function touchGestures(): void {
        const DoubleTap = 320;
        const Hold = 450;
        const Slop = 14;
        let start: { x: number; y: number; at: number; side: "left" | "center" | "right"; width: number; height: number } | null = null;
        let kind: "" | "volume" | "brightness" | "speed" = "";
        let origin = 0;
        // The brightness slider rests at Auto on the bottom (no filter, the device decides); above it the picture goes from dim to bright.
        const AutoBelow = 0.04;
        const Dim = 0.3;
        const Bright = 1.6;
        let brightnessAt = 0;
        const brightnessOf = (position: number) => Dim + (position - AutoBelow) / (1 - AutoBelow) * (Bright - Dim);
        const positionOf = (value: number) => AutoBelow + (value - Dim) / (Bright - Dim) * (1 - AutoBelow);
        let heldRate = 1;
        let holdTimer = 0;
        let tapTimer = 0;
        let lastTap = 0;
        let lastSide = "";
        let hudTimer = 0;
        let seekTotal = 0;
        let seekTimer = 0;
        let seekSide = "";
        const text = gesture.querySelector<HTMLElement>("[data-role=gesture-text]")!;
        const fill = gesture.querySelector<HTMLElement>(".player__gesture-fill")!;

        const hud = (name: string, label: string, level?: number) => {
            gesture.dataset.kind = name;
            text.textContent = label;
            if (level !== undefined) fill.style.setProperty("--level", String(Math.min(1, Math.max(0, level))));
            gesture.hidden = false;
            clearTimeout(hudTimer);
        };
        const hideHud = (delay = 600) => { clearTimeout(hudTimer); hudTimer = window.setTimeout(() => { gesture.hidden = true; }, delay); };
        // The tapped half lights up with arrows and the total of this series of taps.
        const seekBy = (side: "left" | "right") => {
            const step = side === "left" ? -10 : 30;
            if (seekSide !== side) seekTotal = 0;
            seekSide = side;
            seek(step);
            seekTotal += step;
            const flashes = Array.from(player.querySelectorAll<HTMLElement>(".player__seek"));
            const flash = flashes.find(item => item.dataset.side === side)!;
            for (const other of flashes) if (other !== flash) { other.hidden = true; other.classList.remove("player__seek--show"); }
            flash.querySelector("[data-role=seek-text]")!.textContent = (seekTotal > 0 ? "+" : "−") + Math.abs(seekTotal) + " s";
            flash.hidden = false;
            flash.classList.remove("player__seek--show");
            void flash.offsetWidth;
            flash.classList.add("player__seek--show");
            clearTimeout(seekTimer);
            seekTimer = window.setTimeout(() => { flash.hidden = true; flash.classList.remove("player__seek--show"); seekTotal = 0; seekSide = ""; }, 700);
            wake();
        };
        const toggleChrome = () => {
            if (player.classList.contains("player--idle")) wake();
            else if (!video.paused) { clearTimeout(idle); player.classList.add("player--idle"); }
        };
        const endHold = () => {
            clearTimeout(holdTimer);
            if (kind === "speed") video.playbackRate = heldRate;
            if (kind) hideHud(kind === "speed" ? 0 : 700);
            kind = "";
        };

        video.addEventListener("pointerdown", event => {
            if (event.pointerType !== "touch" || player.classList.contains("player--mini")) return;
            const bounds = video.getBoundingClientRect();
            const x = event.clientX - bounds.left;
            start = { x: event.clientX, y: event.clientY, at: Date.now(), side: x < bounds.width / 3 ? "left" : x > bounds.width * 2 / 3 ? "right" : "center", width: bounds.width, height: bounds.height };
            kind = "";
            try { video.setPointerCapture(event.pointerId); } catch { /* A finger that is already gone cannot be captured. */ }
            holdTimer = window.setTimeout(() => {
                if (!start || kind || video.paused) return;
                kind = "speed";
                heldRate = video.playbackRate;
                video.playbackRate = 2;
                hud("speed", "2× ▶▶");
            }, Hold);
        });
        video.addEventListener("pointermove", event => {
            if (event.pointerType !== "touch" || !start || kind === "speed") return;
            const dx = event.clientX - start.x;
            const dy = event.clientY - start.y;
            if (!kind) {
                if (Math.abs(dy) < Slop && Math.abs(dx) < Slop) return;
                clearTimeout(holdTimer);
                if (Math.abs(dy) <= Math.abs(dx)) { start = null; return; }
                const left = start.x - video.getBoundingClientRect().left < start.width / 2;
                kind = left ? "brightness" : "volume";
                origin = left ? (brightnessAt === 0 ? positionOf(1) : brightnessAt) : video.muted ? 0 : video.volume;
            }
            const level = Math.min(1, Math.max(0, origin - dy / (start.height * 0.7)));
            if (kind === "volume") {
                video.muted = false;
                video.volume = level;
                hud(level === 0 ? "muted" : "volume", String(Math.round(level * 100)), level);
            } else {
                brightnessAt = level < AutoBelow ? 0 : level;
                video.style.filter = brightnessAt === 0 ? "" : `brightness(${brightnessOf(level)})`;
                hud("brightness", brightnessAt === 0 ? gesture.dataset.auto ?? "Auto" : String(Math.round(level * 100)), level);
            }
        });
        const release = (event: PointerEvent) => {
            if (event.pointerType !== "touch" || !start) return;
            const tap = !kind && Date.now() - start.at < Hold && Math.abs(event.clientX - start.x) < Slop && Math.abs(event.clientY - start.y) < Slop;
            const side = start.side;
            start = null;
            endHold();
            if (!tap || event.type === "pointercancel") return;
            const now = Date.now();
            const second = now - lastTap < DoubleTap && lastSide === side;
            if (side !== "center" && second) {
                clearTimeout(tapTimer);
                seekBy(side);
                lastTap = now;
                return;
            }
            lastTap = now;
            lastSide = side;
            clearTimeout(tapTimer);
            if (side === "center") toggleChrome();
            else tapTimer = window.setTimeout(toggleChrome, DoubleTap);
        };
        video.addEventListener("pointerup", release);
        video.addEventListener("pointercancel", release);
        video.addEventListener("contextmenu", event => { if (lastPointerType === "touch") event.preventDefault(); });
    }

    function layout(): void {
        const wide = player.classList.contains('player--theater');
        player.querySelector('[data-action=theater]')!.setAttribute('aria-pressed', String(wide));
        const overlay = wide || narrow.matches || document.fullscreenElement === host;
        player.classList.toggle('player--fullscreen', document.fullscreenElement === host);
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
