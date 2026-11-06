import "./game.scss";
import { emulatorLanguage, fromBase64, sameBytes, toBase64 } from "./Game";

type SaveType = "Memory" | "State";

interface Emulator {
    paused: boolean;
    started: boolean;
    on(event: string, handler: (data: never) => void): void;
    displayMessage(message: string): void;
    gameManager: {
        FS: { analyzePath(path: string): { exists: boolean }; mkdir(path: string): void; readFile(path: string): Uint8Array; writeFile(path: string, data: Uint8Array): void };
        getSaveFilePath(): string;
        loadSaveFiles(): void;
        saveSaveFiles(): void;
        loadState(state: Uint8Array): void;
    };
}

const game = document.querySelector<HTMLElement>(".game[data-emulator]");
if (game?.dataset.api) start(game, game.dataset.api);
else if (game) load(game, {}, { "save-state-location": "browser" });

// EmulatorJS from Jularr itself; a file without its game keeps EmulatorJS's own saves in this browser.
// A touch-first device (phone, tablet, also an iPad that names itself a Mac) gets the on-screen controls.
function load(game: HTMLElement, settings: Record<string, unknown>, options: Record<string, string>): void {
    Object.assign(window, {
        EJS_player: "#game-screen",
        EJS_core: game.dataset.emulator,
        EJS_gameUrl: game.dataset.file,
        EJS_gameName: game.dataset.title,
        EJS_pathtodata: "/emulator/data/",
        EJS_language: emulatorLanguage(game.dataset.language ?? "en"),
        EJS_disableAutoLang: false,
        EJS_defaultOptions: { "save-save-interval": "60", "virtual-gamepad": matchMedia("(pointer: coarse)").matches ? "enabled" : "disabled", ...options },
        ...settings
    });
    const loader = document.createElement("script");
    loader.src = "/emulator/data/loader.js";
    document.body.append(loader);
    if (game.dataset.emulator === "melonds") touchAsMouse(game.querySelector<HTMLElement>("#game-screen")!);
}

// The DS core reads its touch screen from the mouse; a finger on the picture presses, moves and lifts it like one (the on-screen buttons keep their own touches).
function touchAsMouse(screen: HTMLElement): void {
    let finger: number | null = null;
    const send = (type: string, touch: Touch) => touch.target.dispatchEvent(new MouseEvent(type, {
        bubbles: true, cancelable: true, view: window, clientX: touch.clientX, clientY: touch.clientY, screenX: touch.screenX, screenY: touch.screenY,
        button: 0, buttons: type === "mouseup" ? 0 : 1
    }));
    const follow = (event: TouchEvent, type: string, ends: boolean) => {
        const touch = [...event.changedTouches].find(item => item.identifier === finger);
        if (!touch) return;
        event.preventDefault();
        send(type, touch);
        if (ends) finger = null;
    };
    screen.addEventListener("touchstart", event => {
        const touch = event.changedTouches[0];
        if (finger !== null || !(touch?.target instanceof HTMLCanvasElement)) return;
        finger = touch.identifier;
        event.preventDefault();
        send("mousemove", touch);
        send("mousedown", touch);
    }, { passive: false });
    screen.addEventListener("touchmove", event => follow(event, "mousemove", false), { passive: false });
    screen.addEventListener("touchend", event => follow(event, "mouseup", true), { passive: false });
    screen.addEventListener("touchcancel", event => follow(event, "mouseup", true), { passive: false });
}

function start(game: HTMLElement, api: string): void {
    const antiforgery = game.dataset.antiforgery ?? "";
    const saveError = game.querySelector<HTMLElement>("[data-role=save-error]")!;
    const settings = window as unknown as Record<string, unknown>;
    // Set while this browser holds save memory the server has not acknowledged.
    const unsyncedKey = "jularr.game.unsynced." + api;
    let emulator: Emulator | undefined;
    let memory: Uint8Array | undefined;
    let restored = false;
    let saves = Promise.resolve();
    let playedMs = 0;

    load(game, {
        EJS_ready: () => {
            emulator = settings.EJS_emulator as Emulator;
            emulator.on("saveSaveFiles", (data: Uint8Array) => {
                if (restored) queue(() => saveMemory(data.slice()));
            });
        },
        EJS_onGameStart: () => queue(restoreMemory),
        EJS_onSaveState: (data: { state: Uint8Array }) => queue(() => saveState(data.state.slice())),
        EJS_onLoadState: () => queue(loadState)
    }, {});

    // Saves and restores run one after another, so an older save never lands last.
    function queue(work: () => Promise<void>): void {
        saves = saves.then(work).catch(() => undefined);
    }

    async function request(type: SaveType, method: "GET" | "PUT", data?: Uint8Array): Promise<Uint8Array | null> {
        const response = await fetch(`${api}/saves/${type}`, {
            method,
            credentials: "same-origin",
            headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
            body: data ? JSON.stringify({ data: toBase64(data) }) : undefined
        });
        if (method === "GET" && response.status === 404) return null;
        if (!response.ok) throw new Error(`Game save ${method} failed`);
        return method === "GET" ? fromBase64((await response.json() as { data: string }).data) : data!;
    }

    // The profile's save replaces the browser copy, unless this browser still holds a newer one the server never received.
    async function restoreMemory(): Promise<void> {
        if (!emulator) return;
        const files = emulator.gameManager.FS;
        const path = emulator.gameManager.getSaveFilePath();
        try {
            if (localStorage.getItem(unsyncedKey) && files.analyzePath(path).exists) {
                await saveMemory(files.readFile(path).slice());
                return;
            }

            const saved = await request("Memory", "GET");
            if (!saved) return;
            let folder = "";
            for (const part of path.split("/").slice(0, -1).filter(Boolean)) {
                folder += "/" + part;
                if (!files.analyzePath(folder).exists) files.mkdir(folder);
            }
            files.writeFile(path, saved);
            emulator.gameManager.loadSaveFiles();
            memory = saved;
        } catch {
            saveError.hidden = false;
        } finally {
            restored = true;
        }
    }

    async function saveMemory(data: Uint8Array): Promise<void> {
        if (!data.length || sameBytes(memory, data)) return;
        try {
            localStorage.setItem(unsyncedKey, "1");
            await request("Memory", "PUT", data);
            memory = data;
            localStorage.removeItem(unsyncedKey);
            saveError.hidden = true;
        } catch {
            saveError.hidden = false;
        }
    }

    // States are large; they travel compressed and only the browser unpacks them.
    async function saveState(state: Uint8Array<ArrayBuffer>): Promise<void> {
        try {
            const packed = new Uint8Array(await new Response(new Blob([state]).stream().pipeThrough(new CompressionStream("gzip"))).arrayBuffer());
            await request("State", "PUT", packed);
            saveError.hidden = true;
            emulator?.displayMessage(game.dataset.stateSaved ?? "");
        } catch {
            saveError.hidden = false;
        }
    }

    async function loadState(): Promise<void> {
        const packed = await request("State", "GET").catch(() => null);
        if (!packed) {
            emulator?.displayMessage(game.dataset.stateMissing ?? "");
            return;
        }

        const state = new Uint8Array(await new Response(new Blob([packed.slice()]).stream().pipeThrough(new DecompressionStream("gzip"))).arrayBuffer());
        emulator?.gameManager.loadState(state);
    }

    // Play time counts while the game runs in a visible page; it is reported every minute and when the page goes away.
    function reportPlay(leaving: boolean): void {
        const seconds = Math.min(300, Math.floor(playedMs / 1000));
        if (seconds < 1) return;
        playedMs -= seconds * 1000;
        void fetch(`${api}/play-time`, {
            method: "POST",
            credentials: "same-origin",
            keepalive: leaving,
            headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
            body: JSON.stringify({ seconds })
        }).catch(() => undefined);
    }

    window.setInterval(() => {
        if (document.hidden || !emulator?.started || emulator.paused) return;
        playedMs += 5000;
        if (playedMs >= 60_000) reportPlay(false);
    }, 5000);
    document.addEventListener("visibilitychange", () => {
        if (!document.hidden) return;
        reportPlay(true);
        if (restored) emulator?.gameManager.saveSaveFiles();
    });
    window.addEventListener("pagehide", () => reportPlay(true));
}
