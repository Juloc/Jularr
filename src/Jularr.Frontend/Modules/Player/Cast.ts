// Chromecast: Google's sender library, loaded only when the viewer asks to cast, and the one playback a device shows.
declare const cast: any;
declare const chrome: any;

export interface CastState {
    connected: boolean;
    paused: boolean;
    currentTime: number;
    duration: number;
    device: string;
}

export interface CastSender {
    request(): Promise<void>;
    stop(): void;
    load(url: string, title: string, subtitle: string, startSeconds: number): Promise<void>;
    playOrPause(): void;
    seek(seconds: number): void;
}

const Library = "https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1";
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

// The sender, or nothing where the library or a browser with Cast is missing. Changes of the device's state come through update.
export async function loadCast(update: (state: CastState) => void): Promise<CastSender | null> {
    if (!await loadLibrary() || typeof cast === "undefined") return null;
    const context = cast.framework.CastContext.getInstance();
    // The default receiver of Google plays HLS and needs no registration of its own.
    context.setOptions({ receiverApplicationId: chrome.cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID, autoJoinPolicy: chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED });
    const remote = new cast.framework.RemotePlayer();
    const controller = new cast.framework.RemotePlayerController(remote);
    const publish = () => update({
        connected: remote.isConnected,
        paused: remote.isPaused,
        currentTime: remote.currentTime,
        duration: remote.duration,
        device: context.getCurrentSession()?.getCastDevice()?.friendlyName ?? ""
    });
    controller.addEventListener(cast.framework.RemotePlayerEventType.ANY_CHANGE, publish);
    context.addEventListener(cast.framework.CastContextEventType.SESSION_STATE_CHANGED, publish);
    return {
        request: async () => { await context.requestSession(); },
        stop: () => context.endCurrentSession(true),
        load: async (url, title, subtitle, startSeconds) => {
            const session = context.getCurrentSession();
            if (!session) throw new Error("cast_session");
            const info = new chrome.cast.media.MediaInfo(url, "application/x-mpegURL");
            info.streamType = chrome.cast.media.StreamType.BUFFERED;
            if (chrome.cast.media.HlsSegmentFormat) info.hlsSegmentFormat = chrome.cast.media.HlsSegmentFormat.TS;
            info.metadata = new chrome.cast.media.GenericMediaMetadata();
            info.metadata.title = title;
            info.metadata.subtitle = subtitle;
            const request = new chrome.cast.media.LoadRequest(info);
            request.currentTime = startSeconds;
            request.autoplay = true;
            const error = await session.loadMedia(request);
            if (error) throw new Error(String(error));
        },
        playOrPause: () => controller.playOrPause(),
        seek: seconds => { remote.currentTime = seconds; controller.seek(); }
    };
}
