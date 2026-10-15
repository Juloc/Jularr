// Reader translation: a section's blocks in another language, from the shared cache or translated here and then shared.
export type Language = "English" | "German" | "Indonesian" | "Japanese";

const Codes: Record<Language, string> = { English: "en", German: "de", Indonesian: "id", Japanese: "ja" };
const Stopwords: Record<Exclude<Language, "Japanese">, string[]> = {
    English: ["the", "and", "of", "to", "was", "that", "he", "it", "with", "is"],
    German: ["der", "die", "und", "das", "nicht", "ist", "ein", "sich", "mit", "zu"],
    Indonesian: ["yang", "dan", "di", "itu", "dengan", "tidak", "ini", "ke", "dari", "untuk"]
};
const Batch = 24;

// A book's language tag, else its text: kana and kanji, otherwise the most frequent common words.
export function languageOf(tag: string | undefined, text: string): Language | undefined {
    const code = tag?.toLowerCase().split(/[-_]/)[0];
    const tagged = (Object.keys(Codes) as Language[]).find(language => Codes[language] === code || (language === "Indonesian" && code === "in"));
    if (tagged) return tagged;

    const letters = text.replace(/[\s\p{P}\d]/gu, "");
    if (letters.length === 0) return undefined;
    const japanese = (letters.match(/[぀-ヿ一-鿿]/g) ?? []).length;
    if (japanese / letters.length > 0.2) return "Japanese";

    const words = text.toLowerCase().match(/\p{L}+/gu) ?? [];
    let best: Language | undefined;
    let most = 3;
    for (const [language, list] of Object.entries(Stopwords) as [Language, string[]][]) {
        const count = words.filter(word => list.includes(word)).length;
        if (count > most) { best = language; most = count; }
    }

    return best;
}

// The models of a route are its steps: one, or into English and out of it.
export function steps(source: Language, target: Language): [string, string][] {
    return source === "English" || target === "English" ? [[Codes[source], Codes[target]]] : [[Codes[source], "en"], ["en", Codes[target]]];
}

interface RouteFile { step: number; fileType: string; url: string; precision: string | null }

export function translator(reader: HTMLElement, content: HTMLElement, createWorker: () => Worker) {
    const blocks = Array.from(content.querySelectorAll<HTMLElement>("[data-b]"));
    const originals = new Map(blocks.map(block => [block, block.innerHTML]));
    const status = reader.querySelector<HTMLElement>("[data-role=translation-status]");
    const source = languageOf(reader.dataset.language, content.innerText.slice(0, 20_000));
    let current: Language | "" = "";
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
        const response = await fetch(reader.dataset.routeUrl! + route, { credentials: "same-origin" });
        if (!response.ok) throw new Error("No translation route.");
        const files = await response.json() as RouteFile[];
        const models = steps(source!, target).map(([from, to], index) => {
            const step = files.filter(file => file.step === index + 1);
            return { source: from, target: to, precision: step[0]?.precision ?? "int8shiftAlphaAll", files: Object.fromEntries(step.map(file => [file.fileType, file.url])) };
        });
        const started = createWorker();
        await new Promise<void>((resolve, reject) => {
            started.onmessage = event => event.data.type === "ready" ? resolve() : reject(new Error(event.data.message));
            started.postMessage({ type: "load", engine: files.find(file => file.step === 0)!.url, models });
        });
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

    // Translated batch by batch so the page fills in while it works; the finished section is shared.
    async function translate(target: Language) {
        const shared = await cached(target);
        if (shared) { show(shared); return; }

        say(reader.dataset.translatingLabel + " 0 %");
        const active = await engine(target);
        const list = texts();
        const result: string[] = list.map(() => "");
        for (let start = 0; start < list.length && current === target; start += Batch) {
            const part = await run(active, list.slice(start, start + Batch), start);
            part.forEach((text, offset) => { result[start + offset] = text; });
            show(result);
            say(`${reader.dataset.translatingLabel} ${Math.min(100, Math.round((start + Batch) * 100 / list.length))} %`);
        }

        if (current !== target) return;
        say(undefined);
        await fetch(reader.dataset.translationUrl! + target, {
            method: "PUT", credentials: "same-origin",
            headers: { "Content-Type": "application/json", RequestVerificationToken: reader.dataset.antiforgery! },
            body: JSON.stringify({ blocks: JSON.stringify(result) })
        });
    }

    return async function choose(target: Language | "") {
        current = target;
        restore();
        say(undefined);
        if (target === "" || !source || source === target) return;
        try {
            await translate(target);
        } catch {
            if (current === target) { restore(); say(reader.dataset.translationFailedLabel); }
        }
    };
}
