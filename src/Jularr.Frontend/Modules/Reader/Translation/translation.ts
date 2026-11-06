import { readerLifetime } from "../Lifetime";
import { codexBar, showCodexBar } from "./CodexBar";

// Reader translation: a section's blocks in another language from the server's TranslateGemma; Mozilla in the browser steps in only as an emergency, and its results stay in this browser.
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
// Mozilla translates the page in view and this many pages after it; the window moves along with the reader.
export const AheadPages = 3;

export function serverIsSlow(durations: number[]): boolean {
    if (durations.length === 0) return false;
    if (durations[durations.length - 1] > SlowBlockMs) return true;
    const last = durations.slice(-3);
    return last.length === 3 && last.reduce((sum, ms) => sum + ms, 0) / 3 > SlowAverageMs;
}

// The blocks in Mozilla's window: those reaching into the view (a page or a spread) or the next three pages after it; a scrolled text counts its
// screen as a page. Each block's extents are its boxes as offsets from the start of the view along the reading direction.
export function windowOf(extents: [number, number][][], view: number, page: number): number[] {
    const end = view + AheadPages * page;
    return extents.flatMap((boxes, index) => boxes.some(([from, to]) => to > 0 && from < end) ? [index] : []);
}

// The pages in Mozilla's window of a book shown page by page: the page in view and the next three, those that have blocks to translate.
export function pagesAhead(page: number, has: (page: number) => boolean): number[] {
    return Array.from({ length: AheadPages + 1 }, (_, offset) => page + offset).filter(has);
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

// A server model as the Reader names it: the model alone for the local Ollama machines ("translategemma:12b"), else with its provider ("codex · gpt-5").
export function modelName(id: string): string {
    const separator = id.indexOf(":");
    if (separator <= 0) return id;
    const provider = id.slice(0, separator);
    return provider === "ollama" ? id.slice(separator + 1) : provider + " · " + id.slice(separator + 1);
}

// What a re-translation covers, as the server numbers it.
export type RetranslationScope = "block" | "section" | "book";
export const ScopeIds: Record<RetranslationScope, number> = { block: 1, section: 2, book: 3 };

// The blocks a re-translation asks the model for now: the one paragraph, else every block of the section that has text of its own.
export function retranslated(scope: RetranslationScope, index: number, list: string[]): number[] {
    if (scope === "block") return list[index]?.trim() ? [index] : [];
    return list.flatMap((text, i) => text.trim() ? [i] : []);
}

// The models of a route are its steps: one, or into English and out of it.
export function steps(source: Language, target: Language): [string, string][] {
    return source === Bridge || target === Bridge ? [[source, target]] : [[source, Bridge], [Bridge, target]];
}

interface RouteFile { step: number; fileType: string; url: string; precision: string | null }

// A section's blocks as both translators fill them: the source texts, the text shown for each (the server's, or the browser's where `bridge` is set,
// which the server's replaces) and the blocks Mozilla was given already, so none is translated twice. Its key is where this browser keeps them.
export interface BlockSet { key: string; list: string[]; result: string[]; bridge: boolean[]; asked: Set<number> }

export function blockSet(prefix: string, target: Language, list: string[]): BlockSet {
    return { key: prefix + target + "#" + fingerprint(list), list, result: list.map(() => ""), bridge: list.map(() => false), asked: new Set() };
}

// What this browser translated of the blocks earlier stands in for those that have no text yet; true when it found any.
export async function recall(set: BlockSet): Promise<boolean> {
    const earlier = await cacheRead(set.key, set.list.length);
    let found = false;
    earlier?.forEach((text, i) => {
        if (set.result[i].trim() || !text.trim()) return;
        set.result[i] = text;
        set.bridge[i] = true;
        found = true;
    });
    return found;
}

export interface BrowserTranslator {
    // Translates the given blocks that have neither text nor were given to Mozilla before, one batch after another and after the work asked for earlier;
    // false when Mozilla has no models for this language. `shown` gets the blocks it filled, `progress` a share of them and undefined at the end.
    fill(target: Language, set: BlockSet, indices: number[], alive: () => boolean, shown: (filled: number[]) => void, progress?: (percent: number | undefined) => void): Promise<boolean>;
}

// Mozilla in the browser, the emergency translator of the Reader and of Smart Pages: one engine per route, loaded once; nothing of it reaches the server.
export function browserTranslator(reader: HTMLElement, source: Language | undefined, createWorker: () => Worker): BrowserTranslator {
    let loading: { route: string; worker: Promise<Worker> } | undefined;
    let queue: Promise<unknown> = Promise.resolve();
    let requests = 0;
    const pending = new Map<number, { resolve: (texts: string[]) => void; reject: (error: Error) => void }>();
    const stop = () => {
        void loading?.worker.then(worker => worker.terminate(), () => undefined);
        loading = undefined;
        for (const request of pending.values()) request.reject(new Error("The browser translator stopped."));
        pending.clear();
    };
    const signal = readerLifetime();
    signal.addEventListener("abort", stop);

    async function start(route: string, target: Language): Promise<Worker> {
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
        started.onmessage = event => {
            const request = pending.get(event.data.id);
            if (!request) return;
            pending.delete(event.data.id);
            event.data.type === "translated" ? request.resolve(event.data.texts) : request.reject(new Error(event.data.message));
        };
        return started;
    }

    // A route Mozilla has no models for is asked for once.
    function engine(target: Language): Promise<Worker> {
        const route = `${source}/${target}`;
        if (loading?.route !== route) {
            stop();
            loading = { route, worker: start(route, target) };
        }
        return loading.worker;
    }

    function run(active: Worker, list: string[]): Promise<string[]> {
        const id = requests++;
        return new Promise((resolve, reject) => {
            pending.set(id, { resolve, reject });
            active.postMessage({ type: "translate", id, texts: list });
        });
    }

    function fill(target: Language, set: BlockSet, indices: number[], alive: () => boolean, shown: (filled: number[]) => void, progress?: (percent: number | undefined) => void): Promise<boolean> {
        const wanted = indices.filter(i => set.list[i]?.trim() && !set.result[i].trim() && !set.asked.has(i));
        wanted.forEach(i => set.asked.add(i));
        if (wanted.length === 0) return Promise.resolve(true);
        const work = queue.then(async () => {
            if (!alive() || signal.aborted) return true;
            let active: Worker;
            try {
                active = await engine(target);
            } catch {
                return false;
            }
            for (let start = 0; start < wanted.length && alive(); start += Batch) {
                const part = wanted.slice(start, start + Batch);
                const out = await run(active, part.map(i => set.list[i]));
                if (!alive()) return true;
                // A block the server translated meanwhile keeps the server's text.
                const filled = part.filter((i, offset) => {
                    if (set.result[i].trim()) return false;
                    set.result[i] = out[offset];
                    set.bridge[i] = true;
                    return true;
                });
                shown(filled);
                progress?.(Math.min(100, Math.round((start + Batch) * 100 / wanted.length)));
            }
            if (!alive()) return true;
            await cacheWrite(set.key, set.result.map((text, i) => set.bridge[i] ? text : ""));
            progress?.(undefined);
            return true;
        });
        queue = work.catch(() => undefined);
        return work;
    }

    return { fill };
}

// `ahead` gives the blocks of the page in view and the three pages after it (all of the section when it is not given); `moved` follows the reader's page turns.
export function translator(reader: HTMLElement, content: HTMLElement, createWorker: () => Worker, ahead?: () => HTMLElement[]) {
    const blocks = Array.from(content.querySelectorAll<HTMLElement>("[data-b]"));
    // Parent blocks are never translated. Replacing a parent's innerHTML would detach
    // all nested [data-b] nodes, leaving the translator's block references stale.
    const originals = new Map(blocks.filter(block => !block.querySelector('[data-b]'))
        .map(block => [block, block.innerHTML]));
    const positions = new Map(blocks.map((block, index) => [block, index]));
    const status = reader.querySelector<HTMLElement>("[data-role=translation-status]");
    const source = languageOf(reader.dataset.language, content.innerText.slice(0, 20_000));
    const browser = browserTranslator(reader, source, createWorker);
    // A chapter opened in place ends this reader; a re-translation still running stops with it (the background job finishes the chosen blocks).
    const lifetime = readerLifetime();
    let current: Language | "" = "";
    let revision = 0;
    let runs = 0;
    // Once Mozilla stepped in, each page turn moves its window along.
    let emergency: (() => void) | undefined;
    // The server's text in view: the model each block's text is from, and the blocks re-translated here, which the default model's work never overwrites.
    let shown: { result: string[]; bridge: boolean[]; models: (string | null)[]; chosen: Set<number> } | undefined;

    function say(text: string | undefined) {
        if (!status) return;
        status.hidden = text === undefined;
        status.textContent = text ?? "";
    }

    // The blocks marked as another model's text.
    const labeled = new Set<HTMLElement>();

    function restore() {
        for (const [block, html] of originals) block.innerHTML = html;
        for (const block of labeled) delete block.dataset.translationModel;
        labeled.clear();
        shown = undefined;
        delete reader.dataset.translated;
    }

    // A block from another model than the chapter's own carries its model, shown as a quiet line beside it; the language panel names it.
    function label() {
        if (!shown) return;
        const own = reader.dataset.localModel || reader.dataset.codexModel;
        shown.models.forEach((model, index) => {
            const block = blocks[index];
            if (model && model !== own) {
                block.dataset.translationModel = model;
                labeled.add(block);
            } else if (labeled.delete(block)) delete block.dataset.translationModel;
        });
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

    // The blocks in Mozilla's window, by their place in the section.
    function windowed(): number[] {
        if (!ahead) return blocks.map((_, index) => index);
        return ahead().flatMap(block => positions.has(block) ? [positions.get(block)!] : []);
    }

    // Local TranslateGemma uses the same section/block identities. Only validated completed
    // blocks enter the provider-specific cache; a failed block cannot complete the section.
    async function translateLocal(target: Language, generation: number) {
        // Without a local model Codex's model stands in for reading what it translated; it translates only when its button is pressed.
        const model = reader.dataset.localModel || reader.dataset.codexModel;
        const prefix = reader.dataset.localTranslationUrl;
        const run = ++runs;
        const alive = () => current === target && revision === generation && runs === run;
        // Codex that cannot translate now (not connected, its limit reached): the bar says why, nothing is asked.
        if (!model && prefix && source && reader.parentElement?.querySelector("[data-role=codex][data-problem]:not([data-problem=''])")) {
            codexBar(reader, { model: "", source, target, list: [], translated: () => false, put: () => undefined, top: () => 0, alive });
            return;
        }
        if (!model || !prefix || !source) throw new Error("Local translation is unavailable.");
        const url = prefix + encodeURIComponent(target) + "/" + encodeURIComponent(model);
        const set = blockSet(prefix, target, texts());
        const { list, result, bridge } = set;
        const models: (string | null)[] = list.map(() => null);
        const chosen = new Set<number>();
        // The server's texts of the section; each replaces what stood in for it.
        async function load() {
            const cached = await fetch(url + "?sourceLanguage=" + encodeURIComponent(source!), { credentials: "same-origin" });
            if (!cached.ok) return;
            const data = await cached.json() as { blocks: string; models?: (string | null)[] };
            const saved = JSON.parse(data.blocks) as string[];
            if (Array.isArray(saved) && saved.length === result.length) saved.forEach((text, i) => {
                if (typeof text === "string" && !chosen.has(i)) {
                    result[i] = text;
                    models[i] = data.models?.[i] ?? model!;
                    bridge[i] = false;
                }
            });
        }
        await load();
        if (!alive()) return;
        shown = { result, bridge, models, chosen };
        show(result);
        label();

        // What the browser translated earlier stands in for the blocks the server has not done yet; the server's text replaces it as soon as it arrives.
        if (await recall(set)) {
            if (!alive()) return;
            show(result);
        }

        // Codex never starts by itself: the bar under the Reader offers its button for this chapter and the next one.
        if (!reader.dataset.localModel) {
            codexBar(reader, {
                model, source, target, list,
                translated: index => result[index].trim().length > 0 && !bridge[index],
                put: blocks => {
                    for (const { index, text } of blocks) {
                        if (chosen.has(index)) continue;
                        result[index] = text;
                        models[index] = model;
                        bridge[index] = false;
                    }
                    show(result);
                    label();
                },
                top: () => windowed()[0] ?? 0,
                alive
            });
            return;
        }

        const total = list.filter(text => text.trim().length > 0).length;
        let completed = list.filter((text, i) => text.trim().length > 0 && result[i].trim().length > 0 && !bridge[i]).length;
        const durations: number[] = [];
        let unsupported = false;
        for (let index = 0; index < list.length && alive(); index++) {
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
            if (!alive()) return;
            if (!translated.text?.trim()) throw new Error("Local model returned an empty block.");
            completed++;
            // A block re-translated meanwhile keeps the chosen model's text.
            if (chosen.has(index)) continue;
            result[index] = translated.text;
            models[index] = model;
            bridge[index] = false;
            show(result);

            // A server that cannot keep up: Mozilla translates the page in view and the three after it while the server goes on; its text replaces Mozilla's.
            durations.push(Date.now() - started);
            if (!emergency && reader.dataset.browserTranslation === "true" && serverIsSlow(durations)) {
                emergency = () => void browser.fill(target, set, windowed(), alive, () => show(result)).then(found => { if (!found) unsupported = true; }, () => undefined);
                emergency();
            }
        }
        if (alive()) say(undefined);
    }

    // The Reader page picks the server's TranslateGemma. The Mozilla path below runs only as an emergency: when the Mozilla module is on
    // (data-browser-translation) and the server could not translate, see the fallback in choose(). It translates the page in view and the
    // three after it, and follows the reader. Mozilla's result is this browser's own: the server's translation cache is neither read nor written.
    async function translate(target: Language, generation: number) {
        if (reader.dataset.translationEngine === "gemma") return translateLocal(target, generation);

        const run = ++runs;
        const alive = () => current === target && revision === generation && runs === run;
        const set = blockSet(reader.dataset.localTranslationUrl ?? "", target, texts());
        const found = await recall(set);
        if (!alive()) return;
        if (found) show(set.result);

        say(reader.dataset.translatingLabel + " 0 %");
        const fill = () => browser.fill(target, set, windowed(), alive, () => show(set.result),
            percent => { if (alive()) say(percent === undefined ? undefined : `${reader.dataset.translatingLabel} ${percent} %`); });
        emergency = () => void fill().catch(() => undefined);
        if (!await fill()) throw new Error("The browser has no models for this language.");
        if (alive()) say(undefined);
    }

    async function choose(target: Language | "") {
        const generation = ++revision;
        current = target;
        emergency = undefined;
        restore();
        say(undefined);
        reader.dataset.translationState = 'ready';
        showCodexBar(reader, target !== "" && !!source && source !== target);
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
                    emergency = undefined;
                    reader.dataset.translationEngine = 'gemma';
                    const provider = reader.querySelector<HTMLElement>('[data-translation-provider]');
                    if (provider) provider.textContent =
                        (reader.dataset.providerLocalLabel ?? 'TranslateGemma') + ' · ' + modelName(reader.dataset.localModel);
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
            if (current === target && generation === revision) { emergency = undefined; reader.dataset.translationState = 'failed'; restore(); say(reader.dataset.translationFailedLabel); }
        }
    }

    // The leaf block a place in the text belongs to: the block itself, else the first block inside it.
    function leaf(place: HTMLElement | undefined): number | undefined {
        const block = place && (originals.has(place) ? place : Array.from(place.querySelectorAll<HTMLElement>("[data-b]")).find(inner => originals.has(inner)));
        return block ? positions.get(block) : undefined;
    }

    // Re-translates with another server model, for every reader of the book: the choice is kept first, then the paragraph or the section's blocks
    // are asked for one after the other (the whole book's other sections follow in the background job). Each block keeps its earlier text until
    // its new one is there. `progress` gets the blocks done and their number; false when the model could not translate.
    async function retranslate(model: string, scope: RetranslationScope, place: HTMLElement | undefined, progress: (done: number, total: number) => void): Promise<boolean> {
        const prefix = reader.dataset.localTranslationUrl;
        const target = current;
        const view = shown;
        const index = leaf(place) ?? (scope === "block" ? -1 : 0);
        if (!prefix || !source || !target || !view || index < 0 || reader.dataset.translationEngine !== "gemma") return false;
        const url = prefix + encodeURIComponent(target) + "/" + encodeURIComponent(model);
        const headers = { "Content-Type": "application/json", RequestVerificationToken: reader.dataset.antiforgery! };
        const choice = await fetch(url + "/choices", {
            method: "PUT", credentials: "same-origin", headers,
            body: JSON.stringify({ scope: ScopeIds[scope], blockIndex: scope === "block" ? index : null })
        });
        if (!choice.ok) return false;

        const alive = () => !lifetime.aborted && current === target && shown === view;
        const wanted = retranslated(scope, index, texts());
        let done = 0;
        progress(done, wanted.length);
        for (const block of wanted) {
            if (!alive()) return true;
            const response = await fetch(url + "/blocks/" + block, { method: "POST", credentials: "same-origin", headers, body: JSON.stringify({ sourceLanguage: source }) });
            if (!response.ok) return false;
            const translated = await response.json() as { text: string };
            if (!alive()) return true;
            if (translated.text?.trim()) {
                view.chosen.add(block);
                view.result[block] = translated.text;
                view.bridge[block] = false;
                view.models[block] = model;
                show(view.result);
                label();
            }
            progress(++done, wanted.length);
        }
        return true;
    }

    // The model the text of the block at this place is from; undefined while no server translation shows.
    const modelAt = (place: HTMLElement | undefined): string | null | undefined => {
        const index = leaf(place);
        return shown && index !== undefined ? shown.models[index] : undefined;
    };

    return { choose, moved: () => emergency?.(), retranslate, modelAt, showsServer: () => shown !== undefined && reader.dataset.translationEngine === "gemma" };
}
