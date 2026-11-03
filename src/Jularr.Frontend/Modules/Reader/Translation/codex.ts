// The Owner's re-translation by hand with Codex: this chapter, or the whole book chapter by chapter with progress and Stop. The server translates a chapter in whole-passage
// requests and makes the result what every reader of the book sees; the page reloads when it is done to show it.

function format(template: string | undefined, ...values: (string | number)[]): string {
    return values.reduce<string>((text, value, index) => text.replace("{" + index + "}", String(value)), template ?? "");
}

export function codexRetranslate(reader: HTMLElement): void {
    const found = reader.querySelector<HTMLElement>("[data-codex-retranslate]");
    if (!found) return;
    const panel: HTMLElement = found;
    const status = panel.querySelector<HTMLElement>("[data-codex-status]")!;
    const stopButton = panel.querySelector<HTMLButtonElement>("[data-codex-stop]")!;
    const runButtons = Array.from(panel.querySelectorAll<HTMLButtonElement>("[data-codex-run]"));
    const model = panel.dataset.codexModel!;
    const sections = Number(panel.dataset.codexSections);
    const section = Number(panel.dataset.codexSection);
    let stopping = false;
    let running = false;

    function say(text: string | undefined): void {
        status.hidden = !text;
        status.textContent = text ?? "";
    }

    async function run(first: number, last: number): Promise<void> {
        const target = reader.dataset.readingLanguage ?? "original";
        const source = reader.dataset.language;
        if (target === "original" || !source) {
            say(panel.dataset.codexPickLabel);
            return;
        }

        running = true;
        stopping = false;
        runButtons.forEach(button => button.disabled = true);
        stopButton.hidden = false;
        let translated = 0;
        let failed = 0;
        for (let index = first; index <= last && !stopping; index++) {
            say(format(panel.dataset.codexChapterLabel, index - first + 1, last - first + 1));
            const url = (panel.dataset.codexUrl ?? "") + index + "/local-translations/" + encodeURIComponent(target) + "/" + encodeURIComponent(model) + "/retranslate";
            try {
                const response = await fetch(url, {
                    method: "POST", credentials: "same-origin",
                    headers: { "Content-Type": "application/json", RequestVerificationToken: reader.dataset.antiforgery! },
                    body: JSON.stringify({ sourceLanguage: source })
                });
                if (response.ok) translated += ((await response.json()) as { translated: number }).translated;
                else {
                    failed++;
                    // Codex unavailable or its limit reached: the next chapters would fail the same way.
                    if (response.status === 503) break;
                }
            } catch {
                failed++;
                break;
            }
        }

        running = false;
        runButtons.forEach(button => button.disabled = false);
        stopButton.hidden = true;
        say(format(panel.dataset.codexDoneLabel, translated) + (failed > 0 ? " " + format(panel.dataset.codexFailedLabel, failed) : ""));
        if (translated > 0) window.setTimeout(() => location.reload(), 2500);
    }

    runButtons.forEach(button => button.addEventListener("click", () => {
        if (running) return;
        if (button.dataset.codexRun === "chapter") void run(section, section);
        else void run(0, Math.max(0, sections - 1));
    }));
    stopButton.addEventListener("click", () => { stopping = true; });
}
