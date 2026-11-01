import { readerLifetime } from "../Lifetime";

// Reader translation: a section's blocks in another language, from the shared cache or translated here and then shared.
// A language is its code as Mozilla's models name it, such as "de" or "ja".
export type Language = string;

const Bridge = "en";
const Stopwords: Record<string, string[]> = {
    en: ["the", "and", "of", "to", "was", "that", "he", "it", "with", "is"],
    de: ["der", "die", "und", "das", "nicht", "ist", "ein", "sich", "mit", "zu"],
    fr: ["le", "la", "les", "et", "des", "est", "une", "que", "pas", "dans"],
    es: ["el", "la", "que", "de", "los", "las", "una", "por", "con", "para"],
    it: ["il", "che", "di", "la", "non", "una", "per", "sono", "gli", "del"],
    nl: ["de", "het", "een", "en", "van", "niet", "dat", "zijn", "met", "voor"],
    id: ["yang", "dan", "di", "itu", "dengan", "tidak", "ini", "ke", "dari", "untuk"]
};
const Batch = 24;

// A server that needs more than half a minute for a block, or twenty seconds on average over its last three, cannot keep up with a reader (a GPU needs a few seconds).
export const SlowBlockMs = 30_000;
export const SlowAverageMs = 20_000;
// What the browser keeps of its own translations: a week, and at most 300 MB; the oldest go first.
export const CacheMaxAgeMs = 7 * 24 * 3600 * 1000;
export const CacheMaxBytes = 300 * 1024 * 1024;

export function serverIsSlow(durations: number[]): boolean {
    if (durations.length === 0) return false;
    if (durations[durations.length - 1] > SlowBlockMs) return true;
    const last = durations.slice(-3);
    return last.length === 3 && last.reduce((sum, ms) => sum + ms, 0) / 3 > SlowAverageMs;
}

export interface CacheEntry { key: string; savedAt: number; size: number }

// The keys to drop: what is older than the age limit, then the oldest of the rest until the size fits.
export function entriesToDrop(entries: CacheEntry[], now: number, maxAgeMs = CacheMaxAgeMs, maxBytes = CacheMaxBytes): string[] {
    const drop = entries.filter(entry => now - entry.savedAt > maxAgeMs).map(entry => entry.key);
    const kept = entries.filter(entry => now - entry.savedAt <= maxAgeMs).sort((a, b) => a.savedAt - b.savedAt);
    let total = kept.reduce((sum, entry) => sum + entry.size, 0);
    for (const entry of kept) {
        if (total <= maxBytes) break;
        drop.push(entry.key);
        total -= entry.size;
    }
    return drop;
}

const CacheDatabase = "jularr-translation";
const CacheStore = "blocks";

function openCache(): Promise<IDBDatabase | undefined> {
    return new Promise(resolve => {
        if (typeof indexedDB === "undefined") return resolve(undefined);
        const request = indexedDB.open(CacheDatabase, 1);
        request.onupgradeneeded = () => request.result.createObjectStore(CacheStore, { keyPath: "key" });
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => resolve(undefined);
    });
}

function transact<T>(db: IDBDatabase, mode: IDBTransactionMode, work: (store: IDBObjectStore) => IDBRequest<T>): Promise<T | undefined> {
    return new Promise(resolve => {
        try {
            const request = work(db.transaction(CacheStore, mode).objectStore(CacheStore));
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => resolve(undefined);
        } catch {
            resolve(undefined);
        }
    });
}

// A section's blocks the browser translated, kept for a week; nothing of it goes to the server.
async function cacheRead(key: string, count: number): Promise<string[] | undefined> {
    const db = await openCache();
    if (!db) return undefined;
    const entry = await transact(db, "readonly", store => store.get(key)) as { blocks: string[]; savedAt: number } | undefined;
    db.close();
    return entry && Date.now() - entry.savedAt <= CacheMaxAgeMs && entry.blocks.length === count ? entry.blocks : undefined;
}

async function cacheWrite(key: string, blocks: string[]): Promise<void> {
    const db = await openCache();
    if (!db) return;
    const size = blocks.reduce((sum, text) => sum + text.length * 2, 0);
    await transact(db, "readwrite", store => store.put({ key, blocks, savedAt: Date.now(), size }));
    const all = await transact(db, "readonly", store => store.getAll()) as CacheEntry[] | undefined;
    for (const drop of entriesToDrop((all ?? []).map(entry => ({ key: entry.key, savedAt: entry.savedAt, size: entry.size })), Date.now()))
        await transact(db, "readwrite", store => store.delete(drop));
    db.close();
}

// A short fingerprint of the section's text, so a changed book never shows an old translation.
function fingerprint(list: string[]): string {
    let hash = 5381;
    for (const text of list) for (let i = 0; i < text.length; i++) hash = ((hash << 5) + hash + text.charCodeAt(i)) | 0;
    return (hash >>> 0).toString(36) + "." + list.length;
}

// A book's language tag, else the language it was written in, else its text: kana and kanji, otherwise the most frequent common words.
export function languageOf(tag: string | undefined, text: string): Language | undefined {
    const code = tag?.toLowerCase().split(/[-_]/)[0];
    if (code && /^[a-z]{2,3}$/.test(code)) return code === "in" ? "id" : code;

    const letters = text.replace(/[\s\p{P}\d]/gu, "");
    if (letters.length === 0) return undefined;
    const japanese = (letters.match(/[぀-ヿ一-鿿]/g) ?? []).length;
    if (japanese / letters.length > 0.2) return "ja";

    const words = text.toLowerCase().match(/\p{L}+/gu) ?? [];
    let best: Language | undefined;
    let most = 3;
    for (const [language, list] of Object.entries(Stopwords)) {
        const count = words.filter(word => list.includes(word)).length;
        if (count > most) { best = language; most = count; }
    }

    return best;
}

// The models of a route are its steps: one, or into English and out of it.
export function steps(source: Language, target: Language): [string, string][] {
    return source === Bridge || target === Bridge ? [[source, target]] : [[source, Bridge], [Bridge, target]];
}

interface RouteFile { step: number; fileType: string; url: string; precision: string | null }

export function translator(reader: HTMLElement, content: HTMLElement, createWorker: () => Worker) {
    const blocks = Array.from(content.querySelectorAll<HTMLElement>("[data-b]"));
    // Parent blocks are never translated. Replacing a parent's innerHTML would detach
    // all nested [data-b] nodes, leaving the translator's block references stale.
    const originals = new Map(blocks.filter(block => !block.querySelector('[data-b]'))
        .map(block => [block, block.innerHTML]));
    const status = reader.querySelector<HTMLElement>("[data-role=translation-status]");
    const source = languageOf(reader.dataset.language, content.innerText.slice(0, 20_000));
    let current: Language | "" = "";
    let revision = 0;
    let worker: Worker | undefined;
    readerLifetime().addEventListener("abort", () => { worker?.terminate(); worker = undefined; });
    let loaded: string | undefined;

    function say(text: string | undefined) {
        if (!status) return;
        status.hidden = text === undefined;
        status.textContent = text ?? "";
    }

    function restore() {
        for (const [block, html] of originals) block.innerHTML = html;
        delete reader.dataset.translated;
    }

    // Leaf blocks carry their text; a block holding others keeps its own.
    function texts(): string[] {
        return blocks.map(block => block.querySelector("[data-b]") ? "" : block.innerText);
    }

    // A picture stays; only its caption takes the translation.
    function show(translated: string[]) {
        translated.forEach((text, index) => {
            if (text.length > 0) (blocks[index].querySelector("figcaption") ?? blocks[index]).textContent = text;
        });
        reader.dataset.translated = "true";
    }

    async function cached(target: Language): Promise<string[] | undefined> {
        const response = await fetch(reader.dataset.translationUrl! + target, { credentials: "same-origin" });
        if (!response.ok) return undefined;
        const blocksJson = (await response.json() as { blocks: string }).blocks;
        const parsed = JSON.parse(blocksJson) as string[];
        return parsed.length === blocks.length ? parsed : undefined;
    }

    async function engine(target: Language): Promise<Worker> {
        const route = `${source}/${target}`;
        if (worker && loaded === route) return worker;
        worker?.terminate();
        worker = undefined;
        loaded = undefined;
        const response = await fetch(reader.dataset.routeUrl! + route, { credentials: "same-origin" });
        if (!response.ok) throw new Error("No translation route.");
        const files = await response.json() as RouteFile[];
        const models = steps(source!, target).map(([from, to], index) => {
            const step = files.filter(file => file.step === index + 1);
            return { source: from, target: to, precision: step[0]?.precision ?? "int8shiftAlphaAll", files: Object.fromEntries(step.map(file => [file.fileType, file.url])) };
        });
        const started = createWorker();
        try {
            await new Promise<void>((resolve, reject) => {
                started.onmessage = event => event.data.type === "ready" ? resolve() : reject(new Error(event.data.message));
                started.postMessage({ type: "load", engine: files.find(file => file.step === 0)!.url, models });
            });
        } catch (error) {
            started.terminate();
            throw error;
        }
        worker = started;
        loaded = route;
        return started;
    }

    function run(active: Worker, list: string[], id: number): Promise<string[]> {
        return new Promise((resolve, reject) => {
            active.onmessage = event => {
                if (event.data.id !== id) return;
                event.data.type === "translated" ? resolve(event.data.texts) : reject(new Error(event.data.message));
            };
            active.postMessage({ type: "translate", id, texts: list });
        });
    }

    // Local TranslateGemma uses the same section/block identities. Only validated completed
    // blocks enter the provider-specific cache; a failed block cannot complete the section.
    async function translateLocal(target: Language, generation: number) {
        const model = reader.dataset.localModel;
        const prefix = reader.dataset.localTranslationUrl;
        if (!model || !prefix || !source) throw new Error("Local translation is unavailable.");
        const url = prefix + encodeURIComponent(target) + "/" + encodeURIComponent(model);
        const list = texts();
        const result = list.map(() => "");
        const cached = await fetch(url + "?sourceLanguage=" + encodeURIComponent(source), { credentials: "same-origin" });
        if (cached.ok) {
            const data = await cached.json() as { blocks: string };
            const saved = JSON.parse(data.blocks) as string[];
            if (Array.isArray(saved) && saved.length === result.length) saved.forEach((text, i) => {
                if (typeof text === "string") result[i] = text;
            });
        }
        if (current !== target || revision !== generation) return;
        show(result);

        // What the browser translated earlier stands in for the blocks the server has not done yet; the server's text replaces it as soon as it arrives.
        const key = reader.dataset.translationUrl + target + "#" + fingerprint(list);
        const bridge: boolean[] = list.map(() => false);
        const earlier = await cacheRead(key, list.length);
        if (earlier) {
            earlier.forEach((text, i) => { if (!result[i].trim() && text.trim()) { result[i] = text; bridge[i] = true; } });
            if (current !== target || revision !== generation) return;
            show(result);
        }

        const total = list.filter(text => text.trim().length > 0).length;
        let completed = list.filter((text, i) => text.trim().length > 0 && result[i].trim().length > 0 && !bridge[i]).length;
        const durations: number[] = [];
        let unsupported = false;
        for (let index = 0; index < list.length && current === target && revision === generation; index++) {
            if (!list[index].trim() || result[index].trim() && !bridge[index]) continue;
            say(`${reader.dataset.translatingLabel} ${Math.round(completed * 100 / Math.max(1, total))} %` + (unsupported ? " · " + reader.dataset.browserUnsupportedLabel : ""));
            const started = Date.now();
            const response = await fetch(url + "/blocks/" + index, {
                method: "POST", credentials: "same-origin",
                headers: { "Content-Type": "application/json", RequestVerificationToken: reader.dataset.antiforgery! },
                body: JSON.stringify({ sourceLanguage: source })
            });
            if (!response.ok) throw new Error("Local model could not translate the requested block.");
            const translated = await response.json() as { text: string };
            if (current !== target || revision !== generation) return;
            if (!translated.text?.trim()) throw new Error("Local model returned an empty block.");
            result[index] = translated.text;
            bridge[index] = false;
            completed++;
            show(result);

            // A server that cannot keep up: the browser translates the blocks nobody has, with Mozilla, and keeps them for a week.
            durations.push(Date.now() - started);
            if (!unsupported && reader.dataset.browserTranslation === "true" && serverIsSlow(durations)) {
                if (await browserFill(target, generation, list, result, bridge, key)) { say(undefined); return; }
                unsupported = true;
            }
        }
        if (current === target && revision === generation) say(undefined);
    }

    // Mozilla translates the blocks that still have no text; they stay marked as the browser's, so the server's text replaces them later.
    async function browserFill(target: Language, generation: number, list: string[], result: string[], bridge: boolean[], key: string): Promise<boolean> {
        let active: Worker;
        try {
            active = await engine(target);
        } catch {
            return false;
        }
        const missing = list.map((text, i) => text.trim() && !result[i].trim() ? i : -1).filter(i => i >= 0);
        for (let start = 0; start < missing.length && current === target && revision === generation; start += Batch) {
            const part = missing.slice(start, start + Batch);
            const out = await run(active, part.map(i => list[i]), start);
            if (current !== target || revision !== generation) return true;
            part.forEach((i, offset) => { result[i] = out[offset]; bridge[i] = true; });
            show(result);
            say(`${reader.dataset.translatingLabel} ${Math.min(100, Math.round((start + Batch) * 100 / Math.max(1, missing.length)))} %`);
        }

        if (current === target && revision === generation) await cacheWrite(key, result.map((text, i) => bridge[i] ? text : ""));
        return true;
    }

    // The Reader page picks the server's TranslateGemma. The Mozilla path below runs only as an emergency: when the Mozilla module is on
    // (data-browser-translation) and the server could not translate, see the fallback in choose().
    // Translated batch by batch so the page fills in while it works; the finished section is shared.
    async function translate(target: Language, generation: number) {
        if (reader.dataset.translationEngine === "gemma") return translateLocal(target, generation);
        const shared = await cached(target);
        if (revision !== generation) return;
        if (shared) { show(shared); return; }

        say(reader.dataset.translatingLabel + " 0 %");
        const active = await engine(target);
        const list = texts();
        const result: string[] = list.map(() => "");
        for (let start = 0; start < list.length && current === target && revision === generation; start += Batch) {
            const part = await run(active, list.slice(start, start + Batch), start);
            if (revision !== generation) return;
            part.forEach((text, offset) => { result[start + offset] = text; });
            show(result);
            say(`${reader.dataset.translatingLabel} ${Math.min(100, Math.round((start + Batch) * 100 / list.length))} %`);
        }

        if (current !== target || revision !== generation) return;
        say(undefined);
        await fetch(reader.dataset.translationUrl! + target, {
            method: "PUT", credentials: "same-origin",
            headers: { "Content-Type": "application/json", RequestVerificationToken: reader.dataset.antiforgery! },
            body: JSON.stringify({ blocks: JSON.stringify(result) })
        });
    }

    return async function choose(target: Language | "") {
        const generation = ++revision;
        current = target;
        restore();
        say(undefined);
        reader.dataset.translationState = 'ready';
        if (target === "" || !source || source === target) return;
        reader.dataset.translationState = 'loading';
        try {
            await translate(target, generation);
            if (current === target && generation === revision) reader.dataset.translationState = 'ready';
        } catch {
            // A catalogue entry is not a guarantee that the browser can download or run
            // its model. Keep the Reader usable and try the installed local provider.
            if (reader.dataset.translationEngine !== 'gemma' && reader.dataset.localModel && reader.dataset.localTranslationUrl
                && current === target && generation === revision) {
                try {
                    // A failed browser batch may already have altered some text.
                    // Local TranslateGemma must receive the original source, not that partial translation.
                    restore();
                    reader.dataset.translationEngine = 'gemma';
                    const provider = reader.querySelector<HTMLElement>('[data-translation-provider]');
                    if (provider) provider.textContent =
                        (reader.dataset.providerLocalLabel ?? 'TranslateGemma') + ' · ' + reader.dataset.localModel;
                    reader.querySelector<HTMLButtonElement>('[data-local-quality-toggle]')?.setAttribute('aria-pressed', 'true');
                    await translateLocal(target, generation);
                    if (current === target && generation === revision) reader.dataset.translationState = 'ready';
                    return;
                } catch {
                    // If both providers fail, keep the original document readable.
                }
            }
            if (reader.dataset.translationEngine === 'gemma' && reader.dataset.browserTranslation === 'true' && current === target && generation === revision) {
                try {
                    // The server could not translate: start over from the original text in the browser.
                    restore();
                    reader.dataset.translationEngine = 'browser';
                    await translate(target, generation);
                    if (current === target && generation === revision) reader.dataset.translationState = 'ready';
                    return;
                } catch {
                    reader.dataset.translationEngine = 'gemma';
                }
            }
            if (current === target && generation === revision) { reader.dataset.translationState = 'failed'; restore(); say(reader.dataset.translationFailedLabel); }
        }
    };
}
