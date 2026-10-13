import "./player.scss";
import { progressReporter } from "../Tracking/Progress";

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
    video.addEventListener("pause", () => report(false));
    video.addEventListener("ended", () => report(true));
    document.addEventListener("visibilitychange", () => { if (document.hidden) report(false); });
    window.addEventListener("pagehide", event => report(!event.persisted, false));

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
            const instance = new Hls({ startPosition: position });
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
            void video.play().catch(() => undefined);
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

    // Transport state.
    video.addEventListener("play", () => setPlaying(true));
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
    volume.addEventListener("input", () => { video.volume = Number(volume.value); video.muted = video.volume === 0; });
    player.querySelector<HTMLElement>("[data-action=pip]")!.hidden = !document.pictureInPictureEnabled;

    const actions: Record<string, () => void> = {
        toggle: () => void (video.paused ? video.play() : video.pause()),
        back: () => seek(-10),
        forward: () => seek(30),
        mute: () => { video.muted = !video.muted; },
        fullscreen: () => void (document.fullscreenElement ? document.exitFullscreen() : player.requestFullscreen()),
        pip: () => void (document.pictureInPictureElement ? document.exitPictureInPicture() : video.requestPictureInPicture()),
        speed: () => panel("speed"),
        audio: () => panel("audio"),
        subtitles: () => panel("subtitles"),
        more: () => panel("more"),
        close: () => report(true, false)
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

        const speed = target.closest<HTMLElement>("[data-speed]");
        if (speed) {
            video.playbackRate = Number(speed.dataset.speed);
            player.querySelector("[data-role=speed-label]")!.textContent = speed.dataset.speed + "×";
            player.querySelectorAll("[data-speed]").forEach(item => item.setAttribute("aria-checked", String(item === speed)));
            panel(null);
            return;
        }

        const action = target.closest<HTMLElement>("[data-action]")?.dataset.action;
        if (action && actions[action]) actions[action]();
        else if (target === video) actions.toggle();
    });
    video.addEventListener("dblclick", actions.fullscreen);

    // Keyboard baseline from the Player specification.
    document.addEventListener("keydown", event => {
        if (event.target instanceof HTMLInputElement && event.target.type !== "range") return;
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
            if (!video.paused && !player.querySelector(".player__panel:not([hidden])") && !player.contains(document.activeElement)) player.classList.add("player--idle");
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
