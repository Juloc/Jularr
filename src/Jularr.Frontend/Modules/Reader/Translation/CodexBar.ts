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

    function missing(): number {
        return view.list.filter((block, index) => block.trim() && !view.translated(index)).length;
    }

    const show = (...visible: (HTMLButtonElement | null | undefined)[]) =>
        [start, next, stop, retry].forEach(item => { if (item) item.hidden = !visible.includes(item); });

    // At rest: either something is missing (Translate) or the chapter is done (the next chapter's button).
    function rest(line?: string) {
        if (bar!.dataset.problem) {
            const problem = bar!.dataset.problem;
            say(problem === "not_connected" ? "notConnected" : problem === "limit" ? "limit" : "unavailable");
            show(retry);
            return;
        }

        const complete = missing() === 0;
        if (line) text.textContent = line;
        else say(complete ? "chapterDone" : "idle");
        show(complete ? hasNext ? next : undefined : start);
    }

    function failure(result: ChapterResult) {
        say(Problems[result.problem ?? "failed"]);
        show(retry);
    }

    function finish(result: ChapterResult, ownChapter: boolean) {
        running = false;
        if (result.state === "error") return failure(result);
        if (result.state === "stopped") return ownChapter ? rest(texts["reader.codex.stopped"]) : (say("stopped"), show(hasNext ? next : undefined));
        if (!ownChapter) {
            say(result.failed.length > 0 ? "skipped" : "nextDone", result.failed.length);
            show(undefined);
            return;
        }
        if (result.failed.length > 0) {
            say("skipped", result.failed.length);
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
        say(own ? "runningStart" : "runningNextStart");
        const url = (own ? reader.dataset.localTranslationUrl! : `${sectionsUrl}${section + 1}/local-translations/`) + `${encodeURIComponent(view.target)}/${encodeURIComponent(view.model)}/passages`;
        const result = await translateChapter({
            from: own ? view.top() : 0,
            post: (from, skip) => fetch(url, { method: "POST", credentials: "same-origin", headers, body: JSON.stringify({ sourceLanguage: view.source, from, skip }) }),
            arrived: blocks => { if (own && view.alive()) view.put(blocks); },
            progress: ({ seconds, busy }) => {
                if (!busy && seconds === undefined) return say(own ? "runningStart" : "runningNextStart");
                if (busy) return say("busy");
                const minutes = minutesLeft(seconds!);
                if (minutes === 0) return say(own ? "runningShort" : "runningNextShort");
                say(own ? "running" : "runningNext", texts[minutes === 1 ? "reader.codex.time.minute" : "reader.codex.time.minutes"].replace("{0}", String(minutes)));
            },
            stopped: () => stopping || lifetime.aborted || own && !view.alive(),
            pause: milliseconds => new Promise(resolve => window.setTimeout(resolve, milliseconds))
        });
        if (lifetime.aborted || own && !view.alive()) return;
        finish(result, own);
    }

    start!.onclick = () => void run(true);
    if (next) next.onclick = () => void run(false);
    stop!.onclick = () => { stopping = true; };
    retry!.onclick = () => { if (bar.dataset.problem) location.reload(); else void run(last); };

    bar.hidden = false;
    rest();
}
