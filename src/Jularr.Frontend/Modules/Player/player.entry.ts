import "./player.scss";
import { progressReporter } from "../Tracking/Progress";
import { disableNavigation, enableNavigation, navigate } from "../Navigation/Navigation";

// Playback client: capability probe, server PlaybackPlan, transport and progress reporting.
interface PlaybackPlan {
    playbackSessionId: string;
    playbackModeTypeId: number;
    streamUrl: string;
    resumePositionMs: number;
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
    });
}

async function start(player: HTMLElement): Promise<void> {
    const video = player.querySelector<HTMLVideoElement>(".player__video")!;
    const status = player.querySelector<HTMLElement>("[data-role=status]")!;
    const timeline = player.querySelector<HTMLInputElement>("[data-role=timeline]")!;
    const volume = player.querySelector<HTMLInputElement>("[data-role=volume]")!;
    const toggle = player.querySelector<HTMLButtonElement>("[data-action=toggle]")!;
    const mute = player.querySelector<HTMLButtonElement>("[data-action=mute]")!;
    const antiforgery = player.dataset.antiforgery ?? "";
    let plan: PlaybackPlan | null = null;
    let hls: { destroy(): void } | null = null;
    let save: (value: object, leaving?: boolean) => void = () => undefined;
    let switching = false;
    let idle = 0;

    plan = await requestPlan(null);
    if (!plan) return;
    await attach(plan, plan.resumePositionMs / 1000);
    document.body.classList.add("player-active");
    const minimizeButton = player.querySelector<HTMLButtonElement>("[data-action=minimize]");
    if (minimizeButton) minimizeButton.hidden = false;
    const playerAddress = location.pathname + location.search;
    const backHref = player.querySelector<HTMLAnchorElement>("[data-action=close]")?.href ?? "/";

    // Save acknowledged progress only; each plan has its own session.
    const report = (ended: boolean, completed = ended) => {
        if (!plan) return;
        save({
            positionMs: Math.floor(video.currentTime * 1000),
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
        // An album or season continues with the next unit in the full player; the mini player stops where it is.
        const next = player.querySelector<HTMLElement>("[data-action=next][data-href]")?.dataset.href;
        if (next && !player.classList.contains("player--mini")) location.assign(next);
    });
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

    // Planning needs the real browser capabilities; another audio track asks for a new plan.
    async function requestPlan(audioStreamIndex: number | null): Promise<PlaybackPlan | null> {
        for (let attempt = 0; ; attempt++) {
            const response = await fetch(player.dataset.startUrl!, {
                method: "POST",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
                body: JSON.stringify({ ...capabilities(), audioStreamIndex })
            });
            if (response.ok) {
                status.hidden = true;
                return await response.json() as PlaybackPlan;
            }

            const code = await errorCode(response);
            show(code);
            if (code !== "storage_waking" || attempt >= WakeRetries) return null;
            await new Promise(resolve => setTimeout(resolve, 10_000));
        }
    }

    // Conversions use HLS: native where available, otherwise hls.js on demand.
    async function attach(next: PlaybackPlan, position: number): Promise<void> {
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
            instance.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) show("failed"); });
            instance.loadSource(next.streamUrl);
            instance.attachMedia(video);
            hls = instance;
        } else {
            // Metadata loads at once so the position applies before the first frame.
            video.preload = "metadata";
            video.src = next.streamUrl;
        }

        // A new source resets the speed; the chosen one stays.
        const rate = video.playbackRate;
        video.addEventListener("loadedmetadata", () => {
            timeline.max = String(Math.floor(video.duration));
            player.querySelector("[data-role=duration]")!.textContent = clock(video.duration);
            if (position > 0 && position < video.duration) video.currentTime = position;
            video.playbackRate = rate;
            play();
        }, { once: true });
    }

    // The new plan comes first; only then the old session ends and playback continues at the same position.
    async function switchAudio(streamIndex: number): Promise<void> {
        if (switching) return;
        switching = true;
        try {
            const position = video.currentTime;
            const next = await requestPlan(streamIndex);
            if (!next) return;
            report(true, false);
            plan = next;
            await attach(next, position);
        } finally {
            switching = false;
        }
    }

    // Subtitles are WebVTT tracks on the same video; Off removes them.
    function showSubtitle(key: string, language: string | undefined): void {
        video.querySelectorAll("track").forEach(track => track.remove());
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
        });
        video.append(track);
        track.track.mode = "showing";
    }

    video.addEventListener("error", () => show("failed"));

    // The real decoded/buffered ranges are read from the media element; no client-side guessing.
    const bufferBar = player.querySelector<HTMLElement>("[data-role=buffered]");
    const updateBuffer = () => {
        if (!bufferBar) return;
        const duration = video.duration;
        if (!Number.isFinite(duration) || duration <= 0) {
            bufferBar.style.width = "0%";
            return;
        }
        let end = 0;
        for (let i = 0; i < video.buffered.length; i++) {
            if (video.buffered.start(i) <= video.currentTime + 0.5 && video.buffered.end(i) >= video.currentTime) {
                end = video.buffered.end(i);
                break;
            }
        }
        bufferBar.style.width = `${(Math.min(duration, end) / duration * 100).toFixed(2)}%`;
    };
    for (const name of ["progress", "durationchange", "loadedmetadata", "seeked", "timeupdate", "emptied"])
        video.addEventListener(name, updateBuffer);

    // Transport state.
    video.addEventListener("play", () => { status.hidden = true; setPlaying(true); });
    video.addEventListener("pause", () => setPlaying(false));
    video.addEventListener("timeupdate", () => {
        timeline.value = String(Math.floor(video.currentTime));
        player.querySelector("[data-role=time]")!.textContent = clock(video.currentTime);
    });
    video.addEventListener("volumechange", () => {
        volume.value = String(video.muted ? 0 : video.volume);
        mute.setAttribute("aria-pressed", String(video.muted));
        player.classList.toggle("player--muted", video.muted);
    });
    timeline.addEventListener("input", () => { video.currentTime = Number(timeline.value); });
    void previews();
    volume.addEventListener("input", () => { video.volume = Number(volume.value); video.muted = video.volume === 0; });
    player.querySelector<HTMLElement>("[data-action=pip]")!.hidden = !document.pictureInPictureEnabled;

    function play(): void {
        void video.play().catch(error => {
            if (error instanceof DOMException && error.name === "AbortError") return;
            show(error instanceof DOMException && error.name === "NotAllowedError" ? "play_gesture_required" : "failed");
        });
    }

    const actions: Record<string, () => void> = {
        toggle: () => { if (video.paused) play(); else video.pause(); },
        back: () => seek(-10),
        forward: () => seek(30),
        mute: () => { video.muted = !video.muted; },
        fullscreen: () => void (document.fullscreenElement ? document.exitFullscreen() : player.requestFullscreen()),
        pip: () => void (document.pictureInPictureElement ? document.exitPictureInPicture() : video.requestPictureInPicture()),
        speed: () => panel("speed"),
        audio: () => panel("audio"),
        subtitles: () => panel("subtitles"),
        more: () => panel("more"),
        close: () => report(true, false),
        previous: () => go("previous"),
        next: () => go("next"),
        minimize: () => void minimize(),
        expand: () => expand(true),
        stop
    };
    player.addEventListener("click", event => {
        const target = event.target as Element;
        const audio = target.closest<HTMLButtonElement>("[data-audio]");
        if (audio && !audio.disabled && !switching) {
            choose("data-audio", audio);
            void switchAudio(Number(audio.dataset.audio));
            return;
        }

        const subtitle = target.closest<HTMLButtonElement>("[data-subtitle]");
        if (subtitle && !subtitle.disabled) {
            choose("data-subtitle", subtitle);
            showSubtitle(subtitle.dataset.subtitle ?? "", subtitle.dataset.language);
            return;
        }

        const speed = target.closest<HTMLButtonElement>("[data-speed]");
        if (speed) {
            setSpeed(Number(speed.dataset.speed));
            panel(null);
            return;
        }

        const action = target.closest<HTMLElement>("[data-action]")?.dataset.action;
        if (action && actions[action]) actions[action]();
        else if (target === video) actions.toggle();
    });
    // Fine speed control shares the same state as the presets and survives delivery changes.
    const speedControl = player.querySelector<HTMLInputElement>("[data-role=speed-control]");
    speedControl?.addEventListener("input", () => setSpeed(Number(speedControl.value)));
    function setSpeed(value: number): void {
        if (!Number.isFinite(value)) return;
        const speed = Math.min(4, Math.max(0.25, Math.round(value * 20) / 20));
        video.playbackRate = speed;
        const label = Number(speed.toFixed(2)).toString() + "×";
        player.querySelector("[data-role=speed-label]")!.textContent = label;
        const current = player.querySelector("[data-role=speed-current]");
        if (current) current.textContent = label;
        if (speedControl) speedControl.value = String(speed);
        player.querySelectorAll<HTMLElement>("[data-speed]").forEach(button =>
            button.setAttribute("aria-pressed", String(Number(button.dataset.speed) === speed)));
    }
    video.addEventListener("dblclick", actions.fullscreen);

    // Keyboard baseline from the Player specification; the mini player leaves the page's keys alone.
    document.addEventListener("keydown", event => {
        if (event.target instanceof HTMLInputElement && event.target.type !== "range") return;
        if (player.classList.contains("player--mini") && !player.contains(event.target as Node)) return;
        const keys: Record<string, () => void> = {
            " ": actions.toggle, k: actions.toggle, ArrowLeft: () => seek(-10), j: () => seek(-10), ArrowRight: () => seek(30), l: () => seek(30),
            ArrowUp: () => { video.volume = Math.min(1, video.volume + 0.05); }, ArrowDown: () => { video.volume = Math.max(0, video.volume - 0.05); },
            m: actions.mute, f: actions.fullscreen, Escape: () => panel(null)
        };
        const handler = keys[event.key.length === 1 ? event.key.toLowerCase() : event.key];
        if (!handler || (event.target instanceof HTMLInputElement && event.key.startsWith("Arrow"))) return;
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
        panel(null);
        player.classList.add("player--mini");
        document.body.classList.add("player-mini");
        document.body.classList.remove("player-page");
        enableNavigation(url => {
            if (url.pathname + url.search !== playerAddress) return false;
            expand(false);
            return true;
        });
        if (!document.querySelector(".app-layout")) await navigate(backHref, true);
    }

    function expand(push: boolean): void {
        player.classList.remove("player--mini");
        document.body.classList.remove("player-mini");
        document.body.classList.add("player-page");
        if (push) history.pushState(null, "", playerAddress);
    }

    // Stop ends the session and leaves the app as it is; progress stays.
    function stop(): void {
        report(true, false);
        video.pause();
        hls?.destroy();
        video.removeAttribute("src");
        video.load();
        disableNavigation();
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
            if (!video.paused && !player.classList.contains("player--mini") && !player.querySelector(".player__panel:not([hidden])") && !player.contains(document.activeElement)) {
                player.classList.add("player--idle");
            }
        }, IdleDelay);
    }

    function panel(name: string | null): void {
        player.querySelectorAll<HTMLElement>("[data-panel]").forEach(item => { item.hidden = item.dataset.panel !== name || !item.hidden; });
        player.querySelectorAll<HTMLElement>("[data-action=speed], [data-action=audio], [data-action=subtitles], [data-action=more]")
            .forEach(button => button.setAttribute("aria-expanded", String(!player.querySelector<HTMLElement>(`[data-panel=${button.dataset.action}]`)!.hidden)));
        wake();
    }

    function choose(attribute: string, chosen: HTMLElement): void {
        player.querySelectorAll(`[${attribute}]`).forEach(item => item.setAttribute("aria-checked", String(item === chosen)));
        panel(null);
    }

    function show(code: string): void {
        status.textContent = text("data-status", code) || text("data-status", "failed");
        status.hidden = false;
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
