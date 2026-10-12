import "./player.scss";

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

const element = document.querySelector<HTMLElement>(".player[data-start-url]");
if (element) {
    void start(element);
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
    let reported = 0;
    let idle = 0;

    // Planning needs the real browser capabilities.
    for (let attempt = 0; ; attempt++) {
        const response = await fetch(player.dataset.startUrl!, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
            body: JSON.stringify(capabilities())
        });
        if (response.ok) {
            plan = await response.json() as PlaybackPlan;
            break;
        }

        const code = await errorCode(response);
        show(code);
        if (code !== "storage_waking" || attempt >= WakeRetries) return;
        await new Promise(resolve => setTimeout(resolve, 10_000));
    }

    status.hidden = true;
    player.querySelector<HTMLElement>("[data-role=mode]")!.textContent = text("data-mode", Modes[plan.playbackModeTypeId] ?? "");
    // Conversions use HLS: native where available, otherwise hls.js on demand.
    if (plan.playbackModeTypeId !== 1 && video.canPlayType("application/vnd.apple.mpegurl") === "") {
        const { default: Hls } = await import("hls.js/light");
        const hls = new Hls({ startPosition: plan.resumePositionMs / 1000 });
        hls.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) show("failed"); });
        hls.loadSource(plan.streamUrl);
        hls.attachMedia(video);
    } else {
        // Metadata loads at once so resume applies before the first frame.
        video.preload = "metadata";
        video.src = plan.streamUrl;
    }
    video.addEventListener("loadedmetadata", () => {
        timeline.max = String(Math.floor(video.duration));
        player.querySelector("[data-role=duration]")!.textContent = clock(video.duration);
        if (plan!.resumePositionMs > 0 && plan!.resumePositionMs / 1000 < video.duration) video.currentTime = plan!.resumePositionMs / 1000;
        void video.play().catch(() => undefined);
    }, { once: true });
    video.addEventListener("error", () => show("failed"));

    // Progress is reported, never inferred as completion.
    const report = (ended: boolean) => {
        if (!plan || (!ended && Math.abs(video.currentTime * 1000 - reported) < 1000)) return;
        reported = Math.floor(video.currentTime * 1000);
        void fetch(player.dataset.sessionUrl + plan.playbackSessionId, {
            method: "PUT",
            credentials: "same-origin",
            keepalive: true,
            headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
            body: JSON.stringify({
                positionMs: reported,
                durationMs: Number.isFinite(video.duration) && video.duration > 0 ? Math.round(video.duration * 1000) : null,
                isEnded: ended,
                isCompleted: ended
            })
        }).catch(() => undefined);
        if (ended) plan = null;
    };
    setInterval(() => { if (!video.paused) report(false); }, ReportInterval);
    video.addEventListener("pause", () => report(false));
    video.addEventListener("ended", () => report(true));
    document.addEventListener("visibilitychange", () => { if (document.hidden) report(false); });
    window.addEventListener("pagehide", () => report(false));

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
        more: () => panel("more"),
        close: () => report(false)
    };
    player.addEventListener("click", event => {
        const target = event.target as Element;
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
            if (!video.paused && !player.querySelector(".player__panel:not([hidden])")) player.classList.add("player--idle");
        }, IdleDelay);
    }

    function panel(name: string | null): void {
        player.querySelectorAll<HTMLElement>("[data-panel]").forEach(item => { item.hidden = item.dataset.panel !== name || !item.hidden; });
        player.querySelectorAll<HTMLElement>("[data-action=speed], [data-action=more]")
            .forEach(button => button.setAttribute("aria-expanded", String(!player.querySelector<HTMLElement>(`[data-panel=${button.dataset.action}]`)!.hidden)));
        wake();
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
