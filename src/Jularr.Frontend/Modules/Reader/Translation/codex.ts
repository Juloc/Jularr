// Re-translating with a model that translates whole passages (Codex), chapter by chapter with progress and Stop: the server translates a chapter in
// whole-passage requests and makes the result what every reader of the book sees; the page reloads when it is done to show it. The Reader's one
// re-translation (Retranslate.ts) runs it for the chapter in view or the whole book.

export function format(template: string | undefined, ...values: (string | number)[]): string {
    return values.reduce<string>((text, value, index) => text.replace("{" + index + "}", String(value)), template ?? "");
}

// `area` carries the chapter count, the chapter in view, the sections' address and the labels; `stopped` is asked before each chapter.
export async function retranslatePassages(reader: HTMLElement, area: HTMLElement, model: string, scope: "section" | "book", stopped: () => boolean, say: (text: string) => void): Promise<void> {
    const target = reader.dataset.readingLanguage ?? "original";
    const source = reader.dataset.language;
    if (target === "original" || !source) return;
    const sections = Number(area.dataset.codexSections);
    const section = Number(area.dataset.codexSection);
    const [first, last] = scope === "section" ? [section, section] : [0, Math.max(0, sections - 1)];

    let translated = 0;
    let failed = 0;
    for (let index = first; index <= last && !stopped(); index++) {
        say(format(area.dataset.codexChapterLabel, index - first + 1, last - first + 1));
        const url = (area.dataset.codexUrl ?? "") + index + "/local-translations/" + encodeURIComponent(target) + "/" + encodeURIComponent(model) + "/retranslate";
        try {
            const response = await fetch(url, {
                method: "POST", credentials: "same-origin",
                headers: { "Content-Type": "application/json", RequestVerificationToken: reader.dataset.antiforgery! },
                body: JSON.stringify({ sourceLanguage: source })
            });
            if (response.ok) translated += ((await response.json()) as { translated: number }).translated;
            else {
                failed++;
                // The model unavailable or its limit reached: the next chapters would fail the same way.
                if (response.status === 503) break;
            }
        } catch {
            failed++;
            break;
        }
    }

    say(format(area.dataset.codexDoneLabel, translated) + (failed > 0 ? " " + format(area.dataset.codexFailedLabel, failed) : ""));
    if (translated > 0) window.setTimeout(() => location.reload(), 2500);
}
