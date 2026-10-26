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

    function show(translated: string[]) {
        translated.forEach((text, index) => { if (text.length > 0) blocks[index].textContent = text; });
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
        const total = list.filter(text => text.trim().length > 0).length;
        let completed = list.filter((text, i) => text.trim().length > 0 && result[i].trim().length > 0).length;
        for (let index = 0; index < list.length && current === target && revision === generation; index++) {
            if (!list[index].trim() || result[index].trim()) continue;
            say(`${reader.dataset.translatingLabel} ${Math.round(completed * 100 / Math.max(1, total))} %`);
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
            completed++;
            show(result);
        }
        if (current === target && revision === generation) say(undefined);
    }

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
            if (current === target && generation === revision) { reader.dataset.translationState = 'failed'; restore(); say(reader.dataset.translationFailedLabel); }
        }
    };
}
