import { readerLifetime } from "../Lifetime";
import { minutesLeft, translateChapter, type ChapterResult, type Problem } from "./CodexChapter";

// The chapter on screen as the translator holds it: the source texts, which blocks have a translation, and how an arriving text is shown.
export interface CodexView {
    model: string;
    source: string;
    target: string;
    list: string[];
    translated(index: number): boolean;
    put(blocks: { index: number; text: string }[]): void;
    // The block at the top of the page, where the translation starts.
    top(): number;
    alive(): boolean;
}

// The bar shows while a language other than the original is read.
export function showCodexBar(reader: HTMLElement, visible: boolean): void {
    const bar = reader.parentElement?.querySelector<HTMLElement>("[data-role=codex]");
    if (bar) bar.hidden = !visible;
}

const Problems: Record<Problem, string> = { limit: "limit", notConnected: "notConnected", busy: "busy", failed: "error" };

// The bar under the Reader while no local model translates: what is translated, what runs and how long it takes, with the buttons that start a chapter or the next
// one, stop it, or try again. Codex never starts by itself; every request follows a press here.
export function codexBar(reader: HTMLElement, view: CodexView): void {
    const bar = reader.parentElement?.querySelector<HTMLElement>("[data-role=codex]");
    if (!bar) return;
    const text = bar.querySelector<HTMLElement>("[data-role=codex-text]")!;
    const button = (name: string) => bar.querySelector<HTMLButtonElement>(`[data-codex=${name}]`);
    const [start, next, stop, retry] = ["start", "next", "stop", "retry"].map(button);
    const texts = JSON.parse(bar.dataset.texts ?? "{}") as Record<string, string>;
    const say = (key: string, ...values: (string | number)[]) =>
        text.textContent = values.reduce<string>((line, value, index) => line.replace(`{${index}}`, String(value)), texts[`reader.codex.${key}`] ?? "");
    const sectionsUrl = reader.dataset.sectionsUrl ?? "";
    const section = Number(reader.dataset.section);
    const hasNext = bar.dataset.hasNext === "true";
    const lifetime = readerLifetime();
    const headers = { "Content-Type": "application/json", RequestVerificationToken: reader.dataset.antiforgery! };
    let running = false;
    let stopping = false;
    let last = true;
    let request: AbortController | undefined;
    // Leaving drops the request in flight, which stops Codex on the server.
    lifetime.addEventListener("abort", () => request?.abort());

    function missing(): number {
        return view.list.filter((block, index) => block.trim() && !view.translated(index)).length;
    }

    // The mood of the bar: its icon and tint, and a pulse while Codex works.
    const mood = (tone: "accent" | "success" | "warning" | "danger", busy = false) => {
        bar.dataset.tone = tone;
        if (busy) bar.dataset.busy = "true";
        else delete bar.dataset.busy;
    };

    const show = (...visible: (HTMLButtonElement | null | undefined)[]) =>
        [start, next, stop, retry].forEach(item => { if (item) item.hidden = !visible.includes(item); });

    // At rest: either something is missing (Translate) or the chapter is done (the next chapter's button).
    function rest(line?: string) {
        if (bar!.dataset.problem) {
            const problem = bar!.dataset.problem;
            say(problem === "limit" ? "limit" : "unavailable");
            mood(problem === "limit" ? "warning" : "danger");
            show(retry);
            return;
        }

        const complete = missing() === 0;
        if (line) text.textContent = line;
        else say(complete ? "chapterDone" : "idle");
        mood(complete ? "success" : "accent");
        show(complete ? hasNext ? next : undefined : start);
    }

    function failure(result: ChapterResult) {
        say(Problems[result.problem ?? "failed"]);
        mood(result.problem === "limit" || result.problem === "busy" ? "warning" : "danger");
        show(retry);
    }

    function finish(result: ChapterResult, ownChapter: boolean) {
        running = false;
        if (result.state === "error") return failure(result);
        if (result.state === "stopped") return ownChapter ? rest(texts["reader.codex.stopped"]) : (say("stopped"), mood("accent"), show(hasNext ? next : undefined));
        if (!ownChapter) {
            say(result.failed.length > 0 ? "skipped" : "nextDone", result.failed.length);
            mood(result.failed.length > 0 ? "warning" : "success");
            show(undefined);
            return;
        }
        if (result.failed.length > 0) {
            say("skipped", result.failed.length);
            mood("warning");
            show(hasNext ? next : undefined);
        } else rest();
    }

    // One chapter: this one (its texts appear in the page) or the next (only stored, ready when the reader arrives).
    async function run(own: boolean) {
        if (running) return;
        running = true;
        stopping = false;
        last = own;
        show(stop);
        mood("accent", true);
        say(own ? "runningStart" : "runningNextStart");
        const base = own ? reader.dataset.localTranslationUrl! : `${sectionsUrl}${section + 1}/local-translations/`;
        const url = `${base}${encodeURIComponent(view.target)}/${encodeURIComponent(view.model)}`;
        request = new AbortController();
        const signal = request.signal;
        const started = Date.now();
        let doneCharacters = 0;
        // The time left from how fast the text came since the press, for the chapter on screen.
        const estimate = () => {
            const left = view.list.reduce((sum, block, index) => sum + (block.trim() && !view.translated(index) ? block.length : 0), 0);
            const seconds = doneCharacters > 0 ? left * (Date.now() - started) / 1000 / doneCharacters : undefined;
            if (seconds === undefined) return;
            const minutes = minutesLeft(seconds);
            if (minutes === 0) return say("runningShort");
            say("running", texts[minutes === 1 ? "reader.codex.time.minute" : "reader.codex.time.minutes"].replace("{0}", String(minutes)));
        };
        const result = await translateChapter({
            from: own ? view.top() : 0,
            post: (from, skip) => fetch(`${url}/passages`, { method: "POST", credentials: "same-origin", headers, signal, body: JSON.stringify({ sourceLanguage: view.source, from, skip }) }),
            // The blocks stored so far, shown while the request runs.
            poll: own ? async () => {
                const response = await fetch(`${url}?sourceLanguage=${encodeURIComponent(view.source)}`, { credentials: "same-origin", signal });
                if (!response.ok) return;
                const stored = JSON.parse((await response.json() as { blocks: string }).blocks) as string[];
                const fresh = stored.flatMap((text, index) => typeof text === "string" && text.trim() && view.list[index]?.trim() && !view.translated(index) ? [{ index, text }] : []);
                if (fresh.length > 0 && view.alive()) {
                    doneCharacters += fresh.reduce((sum, block) => sum + view.list[block.index].length, 0);
                    view.put(fresh);
                    estimate();
                }
            } : undefined,
            arrived: blocks => {
                const fresh = blocks.filter(block => !view.translated(block.index));
                if (own && view.alive() && fresh.length > 0) {
                    doneCharacters += fresh.reduce((sum, block) => sum + (view.list[block.index]?.length ?? 0), 0);
                    view.put(fresh);
                }
            },
            progress: ({ seconds, busy }) => {
                if (busy) return (mood("warning", true), say("busy"));
                mood("accent", true);
                if (own) return estimate();
                if (seconds === undefined) return say("runningNextStart");
                const minutes = minutesLeft(seconds);
                if (minutes === 0) return say("runningNextShort");
                say("runningNext", texts[minutes === 1 ? "reader.codex.time.minute" : "reader.codex.time.minutes"].replace("{0}", String(minutes)));
            },
            stopped: () => stopping || lifetime.aborted || own && !view.alive(),
            pause: milliseconds => new Promise(resolve => window.setTimeout(resolve, milliseconds))
        });
        request = undefined;
        if (lifetime.aborted || own && !view.alive()) return;
        finish(result, own);
    }

    start!.onclick = () => void run(true);
    if (next) next.onclick = () => void run(false);
    stop!.onclick = () => { stopping = true; request?.abort(); };
    retry!.onclick = () => { if (bar.dataset.problem) location.reload(); else void run(last); };

    bar.hidden = false;
    rest();
}
