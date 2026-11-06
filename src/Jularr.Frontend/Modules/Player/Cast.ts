// Chromecast: Google's sender library, loaded only when the viewer asks to cast, and the queue of episodes a device plays.
declare const cast: any;
declare const chrome: any;

export interface CastTrack {
    url: string;
    language: string;
    name: string;
}

// One item of the device's queue: the address of its own playback session and the subtitles that the device may show.
export interface CastEntry {
    url: string;
    title: string;
    subtitle: string;
    startSeconds: number;
    autoplay: boolean;
    tracks: CastTrack[];
    activeTrack: number | null;
}

export interface CastState {
    connected: boolean;
    paused: boolean;
    currentTime: number;
    duration: number;
    device: string;
    volume: number;
    muted: boolean;
    // The address of the item that plays now; each item has an address of its own.
    contentId: string;
}

export interface CastSender {
    request(): Promise<void>;
    stop(): void;
    load(entries: CastEntry[], startIndex: number): Promise<void>;
    append(entry: CastEntry): Promise<void>;
    skip(direction: 1 | -1): void;
    setSubtitle(track: number | null): void;
    playOrPause(): void;
    seek(seconds: number): void;
    setVolume(level: number): void;
    setMuted(muted: boolean): void;
}

const Library = "https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1";
// The device asks for the next item this many seconds into the one before it, so the change needs no wait.
const PreloadSeconds = 20;
let library: Promise<boolean> | undefined;

function loadLibrary(): Promise<boolean> {
    library ??= new Promise<boolean>(resolve => {
        (window as unknown as { __onGCastApiAvailable: (available: boolean) => void }).__onGCastApiAvailable = available => resolve(available);
        const script = document.createElement("script");
        script.src = Library;
        script.async = true;
        script.onerror = () => resolve(false);
        document.head.append(script);
    });
    return library;
}

// The default receiver of Google plays HLS and queues of it, and shows WebVTT texts, without a registration of its own.
function queueItem(entry: CastEntry): unknown {
    const info = new chrome.cast.media.MediaInfo(entry.url, "application/x-mpegURL");
    info.streamType = chrome.cast.media.StreamType.BUFFERED;
    if (chrome.cast.media.HlsSegmentFormat) info.hlsSegmentFormat = chrome.cast.media.HlsSegmentFormat.TS;
    info.metadata = new chrome.cast.media.GenericMediaMetadata();
    info.metadata.title = entry.title;
    info.metadata.subtitle = entry.subtitle;
    info.tracks = entry.tracks.map((track, index) => {
        const text = new chrome.cast.media.Track(index + 1, chrome.cast.media.TrackType.TEXT);
        text.trackContentId = track.url;
        text.trackContentType = "text/vtt";
        text.subtype = chrome.cast.media.TextTrackType.SUBTITLES;
        text.language = track.language;
        text.name = track.name;
        return text;
    });
    const item = new chrome.cast.media.QueueItem(info);
    item.autoplay = entry.autoplay;
    item.startTime = entry.startSeconds;
    item.preloadTime = PreloadSeconds;
    item.activeTrackIds = entry.activeTrack === null ? [] : [entry.activeTrack + 1];
    return item;
}

// The sender, or nothing where the library or a browser with Cast is missing. Changes of the device's state come through update.
export async function loadCast(update: (state: CastState) => void): Promise<CastSender | null> {
    if (!await loadLibrary() || typeof cast === "undefined") return null;
    const context = cast.framework.CastContext.getInstance();
    context.setOptions({ receiverApplicationId: chrome.cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID, autoJoinPolicy: chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED });
    const remote = new cast.framework.RemotePlayer();
    const controller = new cast.framework.RemotePlayerController(remote);
    const publish = () => update({
        connected: remote.isConnected,
        paused: remote.isPaused,
        currentTime: remote.currentTime,
        duration: remote.duration,
        device: context.getCurrentSession()?.getCastDevice()?.friendlyName ?? "",
        volume: remote.volumeLevel,
        muted: remote.isMuted,
        contentId: remote.mediaInfo?.contentId ?? ""
    });
    controller.addEventListener(cast.framework.RemotePlayerEventType.ANY_CHANGE, publish);
    context.addEventListener(cast.framework.CastContextEventType.SESSION_STATE_CHANGED, publish);
    const session = () => context.getCurrentSession() ?? (() => { throw new Error("cast_session"); })();
    const wait = (call: (done: () => void, failed: (error: { code?: string }) => void) => void) =>
        new Promise<void>((resolve, reject) => call(() => resolve(), error => reject(new Error(String(error?.code ?? "cast_failed")))));
    return {
        request: async () => { await context.requestSession(); },
        stop: () => context.endCurrentSession(true),
        load: (entries, startIndex) => {
            const request = new chrome.cast.media.QueueLoadRequest(entries.map(queueItem));
            request.startIndex = startIndex;
            request.repeatMode = chrome.cast.media.RepeatMode.OFF;
            return wait((done, failed) => session().getSessionObj().queueLoad(request, done, failed));
        },
        append: entry => wait((done, failed) => {
            const media = session().getMediaSession();
            if (!media) throw new Error("cast_session");
            media.queueAppendItem(queueItem(entry), done, failed);
        }),
        skip: direction => {
            const media = session().getMediaSession();
            if (direction > 0) media?.queueNext(() => undefined, () => undefined);
            else media?.queuePrev(() => undefined, () => undefined);
        },
        setSubtitle: track => {
            const ids = track === null ? [] : [track + 1];
            session().getMediaSession()?.editTracksInfo(new chrome.cast.media.EditTracksInfoRequest(ids), () => undefined, () => undefined);
        },
        playOrPause: () => controller.playOrPause(),
        seek: seconds => { remote.currentTime = seconds; controller.seek(); },
        setVolume: level => { remote.volumeLevel = Math.max(0, Math.min(1, level)); controller.setVolumeLevel(); },
        setMuted: muted => { if (remote.isMuted !== muted) controller.muteOrUnmute(); }
    };
}
