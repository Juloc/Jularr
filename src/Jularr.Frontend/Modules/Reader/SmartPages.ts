import { blockBox, letterSpacing, spreads, startSize } from './SmartPageFit';

// Smart Pages: a scanned page in its own layout, its text erased from the scan and set anew in the original boxes in the look measured for each run, or
// its translation in the look of the block's main text. The server prepares the pages as reading units from 100000 on; the server's TranslateGemma
// translates them like any section, the page in view first.
export interface SmartPages {
    has(page: number): boolean;
    page(page: number): Promise<HTMLElement | undefined>;
    fit(element: HTMLElement): void;
}

const Units = 100_000;

export function smartPages(reader: HTMLElement): SmartPages | undefined {
    const numbers = (reader.dataset.smartPages ?? '').split(',').filter(Boolean).map(Number);
    const url = reader.dataset.smartUrl;
    if (numbers.length === 0 || !url) return undefined;
    const units = new Map(numbers.map((number, index) => [number, Units + index]));
    const built = new Map<number, Promise<HTMLElement | undefined>>();
    const model = reader.dataset.localModel;
    const source = (reader.dataset.language ?? '').split(/[-_]/)[0].toLowerCase();
    const target = reader.dataset.readingLanguage ?? 'original';
    const status = reader.querySelector<HTMLElement>('[data-role=translation-status]');
    const translates = !!model && !!source && target !== 'original' && target !== source;

    async function build(number: number): Promise<HTMLElement | undefined> {
        const response = await fetch(url + String(units.get(number)), { credentials: 'same-origin' });
        if (!response.ok) return undefined;
        const { html } = await response.json() as { html: string };
        const element = document.createElement('div');
        element.className = 'reader__smart';
        // The server wrote this markup itself from the recognized text; nothing in it comes from the file.
        element.innerHTML = html;
        for (const block of element.querySelectorAll<HTMLElement>('[data-box]')) {
            const box = blockBox(block.dataset.box ?? '');
            if (!box) { block.remove(); continue; }
            Object.assign(block.style, { left: `${box.left * 100}%`, top: `${box.top * 100}%`, width: `${box.width * 100}%`, height: `${box.height * 100}%` });
        }
        return element;
    }

    // The page in view: only its blocks are translated, one after another. What the server's job translated meanwhile comes from the cache; while the
    // model is busy with other work, the page waits for it.
    let current = 0;
    const running = new Set<number>();
    const pause = (milliseconds: number) => new Promise(resolve => setTimeout(resolve, milliseconds));
    async function translate(number: number): Promise<void> {
        if (!translates || running.has(number)) return;
        running.add(number);
        try {
            const element = await built.get(number);
            if (!element) return;
            const address = `${url}${units.get(number)}/local-translations/${encodeURIComponent(target)}/${encodeURIComponent(model!)}`;
            const blocks = Array.from(element.querySelectorAll<HTMLElement>('[data-b]'));
            const texts = blocks.map(block => block.textContent?.trim() ?? '');
            const wanted = texts.filter(Boolean).length;
            while (current === number) {
                const cached = await fetch(`${address}?sourceLanguage=${encodeURIComponent(source)}`, { credentials: 'same-origin' });
                if (cached.ok) {
                    const saved = JSON.parse((await cached.json() as { blocks: string }).blocks) as (string | null)[];
                    if (Array.isArray(saved) && saved.length === blocks.length) saved.forEach((text, index) => {
                        if (typeof text === 'string' && text.trim() && !blocks[index].dataset.translated) show(blocks[index], text);
                    });
                }
                fit(element);
                const index = blocks.findIndex((block, at) => texts[at] && !block.dataset.translated);
                if (index < 0) { say(undefined); return; }
                say(`${reader.dataset.translatingLabel ?? ''} ${Math.round(blocks.filter(block => block.dataset.translated).length * 100 / Math.max(1, wanted))} %`);
                const response = await fetch(`${address}/blocks/${index}`, {
                    method: 'POST', credentials: 'same-origin',
                    headers: { 'Content-Type': 'application/json', RequestVerificationToken: reader.dataset.antiforgery! },
                    body: JSON.stringify({ sourceLanguage: source })
                });
                if (response.status === 503 && (await response.text()).includes('translation_local_busy')) { await pause(4000); continue; }
                if (!response.ok) throw new Error('The block was not translated.');
                const { text } = await response.json() as { text: string };
                show(blocks[index], text?.trim() ? text : texts[index]);
            }
        } catch {
            if (current === number) say(reader.dataset.translationFailedLabel);
        } finally {
            running.delete(number);
        }
    }

    function say(text: string | undefined): void {
        if (!status) return;
        status.hidden = text === undefined;
        status.textContent = text ?? '';
    }

    // The translation takes the look of the block's main text.
    function show(block: HTMLElement, text: string): void {
        const spans = Array.from(block.querySelectorAll<HTMLElement>('span'));
        const main = spans.reduce<HTMLElement | undefined>((longest, span) => !longest || (span.textContent?.length ?? 0) > (longest.textContent?.length ?? 0) ? span : longest, undefined);
        const run = (main?.cloneNode(false) as HTMLElement | undefined) ?? document.createElement('span');
        run.style.fontSize = '1em';
        run.textContent = text;
        block.replaceChildren(run);
        block.dataset.translated = 'true';
    }

    // The measured size first; smaller only where the text does not fit its box. Labels of one series share the smallest size, and a short label spans its
    // box like the print, by the original's letter spacing (a translation only a little).
    function fit(element: HTMLElement): void {
        const height = element.clientHeight;
        if (!height) return;
        const blocks = Array.from(element.querySelectorAll<HTMLElement>('[data-box]'));
        for (const block of blocks) {
            let size = startSize(Number(block.dataset.base), height);
            const single = block.dataset.lines === '1';
            block.style.whiteSpace = single ? 'nowrap' : '';
            block.style.letterSpacing = '0px';
            block.style.fontSize = `${size}px`;
            for (let step = 0; step < 60 && size > 4 && (block.scrollHeight > block.clientHeight * 1.04 + 1 || block.scrollWidth > block.clientWidth + 1); step++) {
                size *= 0.95;
                block.style.fontSize = `${size}px`;
            }
        }
        const series = new Map<string, HTMLElement[]>();
        for (const block of blocks) if (block.dataset.series) series.set(block.dataset.series, [...series.get(block.dataset.series) ?? [], block]);
        for (const group of series.values()) {
            const size = Math.min(...group.map(block => parseFloat(block.style.fontSize)));
            for (const block of group) block.style.fontSize = `${size}px`;
        }
        // Only a label in capitals of a word or two is spread like the print (ALWAYS / SOMETIMES / NEVER); a line of text keeps its own spacing.
        for (const block of blocks.filter(block => block.dataset.lines === '1' && block.dataset.label !== undefined && spreads(block.textContent ?? ''))) {
            const natural = Array.from(block.children).reduce((sum, child) => sum + child.getBoundingClientRect().width, 0);
            block.style.letterSpacing = `${letterSpacing(block.clientWidth - natural, block.textContent?.length ?? 0, parseFloat(block.style.fontSize), block.dataset.translated === 'true')}px`;
        }
    }

    // A language chosen here is kept for the profile; the pages then show in it.
    const choices = Array.from(reader.querySelectorAll<HTMLButtonElement>('[data-language-choice]'));
    choices.forEach(button => button.addEventListener('click', async () => {
        if (button.dataset.languageChoice === target) return;
        choices.forEach(choice => choice.disabled = true);
        try {
            const response = await fetch(reader.dataset.languageUrl!, {
                method: 'PUT', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: reader.dataset.antiforgery! },
                body: JSON.stringify({ readingLanguage: button.dataset.languageChoice })
            });
            if (!response.ok) throw new Error('The language was not saved.');
            location.reload();
        } catch {
            choices.forEach(choice => choice.disabled = false);
            say(reader.dataset.translationFailedLabel);
        }
    }));

    return {
        has: number => units.has(number),
        page(number) {
            let element = built.get(number);
            if (!element) {
                element = build(number).catch(() => undefined);
                built.set(number, element);
            }
            if (current !== number) say(undefined);
            current = number;
            void translate(number);
            return element;
        },
        fit
    };
}
