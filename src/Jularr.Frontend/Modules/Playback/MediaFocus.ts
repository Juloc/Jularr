// The screen stays on while a video plays or a book is read, and in this browser one medium at a time plays: the player, read aloud
// and a second tab share it. Nothing here reaches another device.
interface Sentinel {
    release(): Promise<void>;
    addEventListener(type: "release", listener: () => void): void;
}

export interface AwakeEnvironment {
    request(): Promise<Sentinel>;
    visible(): boolean;
    // Whether the profile and the browser allow a screen lock at all.
    allowed(): boolean;
}

// Owners (the player, the reader, read aloud) ask to keep the screen on; the lock is held while at least one does, the page is shown and the profile allows it.
export function createAwake(environment: AwakeEnvironment) {
    const owners = new Set<string>();
    let sentinel: Sentinel | null = null;
    let settling = false;
    const wanted = () => owners.size > 0 && environment.allowed() && environment.visible();

    // One pass at a time; whatever changed meanwhile is looked at again before it ends.
    async function refresh(): Promise<void> {
        if (settling) return;
        settling = true;
        try {
            for (;;) {
                if (wanted() && !sentinel) {
                    try {
                        const lock = await environment.request();
                        sentinel = lock;
                        lock.addEventListener("release", () => { if (sentinel === lock) sentinel = null; });
                    } catch {
                        return;
                    }
                } else if (!wanted() && sentinel) {
                    const held = sentinel;
                    sentinel = null;
                    await held.release().catch(() => {});
                } else {
                    return;
                }
            }
        } finally {
            settling = false;
        }
    }

    return {
        hold(owner: string): void { owners.add(owner); void refresh(); },
        drop(owner: string): void { owners.delete(owner); void refresh(); },
        refresh,
        holding: () => sentinel !== null
    };
}

export interface MediaChannel {
    postMessage(message: string): void;
    onmessage: ((event: { data: unknown }) => void) | null;
}

// A medium that starts takes the focus; the one that had it pauses. The same goes for another tab of the same browser.
export function createFocus(channel: MediaChannel | null, tab = Math.random().toString(36).slice(2)) {
    let current: { id: string; pause: () => void } | null = null;
    if (channel) {
        channel.onmessage = event => {
            if (event.data === tab || !current) return;
            const loser = current;
            current = null;
            loser.pause();
        };
    }

    return {
        claim(id: string, pause: () => void): void {
            if (current && current.id !== id) current.pause();
            current = { id, pause };
            channel?.postMessage(tab);
        },
        release(id: string): void { if (current?.id === id) current = null; },
        owner: () => current?.id ?? null
    };
}

// The two shared instances of this page; the player and the reader load them from their own bundles.
const shared = globalThis as typeof globalThis & { __jularrAwake?: ReturnType<typeof createAwake>; __jularrFocus?: ReturnType<typeof createFocus> };

export const awake = shared.__jularrAwake ??= (() => {
    const lock = createAwake({
        request: () => (navigator as Navigator & { wakeLock: { request(type: "screen"): Promise<Sentinel> } }).wakeLock.request("screen"),
        visible: () => !document.hidden,
        allowed: () => "wakeLock" in navigator && document.querySelector("#app-content")?.getAttribute("data-keep-screen-on") !== "false"
    });
    // A hidden page loses the lock by itself; it is taken again when the page is shown.
    document.addEventListener("visibilitychange", () => void lock.refresh());
    return lock;
})();

export const focus = shared.__jularrFocus ??= createFocus(typeof BroadcastChannel === "function" ? new BroadcastChannel("jularr-media") as unknown as MediaChannel : null);
