import { blockBox, capitals, letterSpacing, lineBoxes, splitText, spreads, startSize } from './SmartPageFit';

// Smart Pages: a scanned page in its own layout, its text erased from the scan and set anew into the original's lines in the look measured for each run,
// or its translation in the look of the main text of each sentence. The server prepares the pages as reading units from 100000 on; the server's TranslateGemma
// translates them like any section, the page in view first.
export interface SmartPages {
    has(page: number): boolean;
    page(page: number): Promise<HTMLElement | undefined>;
    fit(element: HTMLElement): void;
}

const Units = 100_000;

// A word to set, in the look of its run (a span the server wrote).
interface Word { text: string; template: HTMLElement; look: string }

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
        for (const block of element.querySelectorAll<HTMLElement>('[data-box]')) if (!blockBox(block.dataset.box ?? '')) block.remove();
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
            // A block holding others (a lead-in in its own look and its sentence) is translated through them.
            const texts = blocks.map(block => block.querySelector('[data-b]') ? '' : block.textContent?.trim() ?? '');
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

    // The translation takes the look of the block's main text; text flowing around a picture is shared out over its boxes by their share of the original.
    function show(block: HTMLElement, text: string): void {
        const parts = Array.from(block.querySelectorAll<HTMLElement>(':scope > [data-part]'));
        if (parts.length === 0) replace(block, text);
        else {
            // Shared out by the original's letters until the page is laid out; then it flows box by box (fit).
            splitText(text, parts.map(part => part.textContent?.length ?? 0)).forEach((piece, index) => replace(parts[index], piece));
        }
        block.dataset.translated = 'true';
    }

    function replace(box: HTMLElement, text: string): void {
        const spans = Array.from(box.querySelectorAll<HTMLElement>('span[data-face]'));
        const main = spans.reduce<HTMLElement | undefined>((longest, span) => !longest || (span.textContent?.length ?? 0) > (longest.textContent?.length ?? 0) ? span : longest, undefined);
        // The main look keeps its size against the block's: 1 for the block's own text, larger for a bold lead-in.
        const run = (main?.cloneNode(false) as HTMLElement | undefined) ?? document.createElement('span');
        // A sentence after a lead-in keeps the space between them; text in capitals stays in capitals (ALWAYS / SOMETIMES / NEVER, running heads).
        const original = box.textContent ?? '';
        run.textContent = (!box.dataset.box && /^\s/.test(original) ? ' ' : '') + (capitals(original) ? text.toLocaleUpperCase() : text);
        box.replaceChildren(run);
    }

    // The text is set line by line into the original's lines (their left edge, baseline and width): the original breaks where the print broke, a
    // translation flows on through the lines of its block, a lead-in on a line of its own keeps it. All of a block gets smaller together only where its
    // text does not fit its lines. Labels of one series share the smallest size; a label in capitals of a word or two and a running head in capitals
    // are spread over their line like the print.
    function fit(element: HTMLElement): void {
        const height = element.clientHeight;
        if (!height) return;
        const page = element.querySelector<HTMLElement>('[data-smart-page]') ?? element;
        let layer = page.querySelector<HTMLElement>(':scope > [data-layer]');
        if (!layer) {
            layer = document.createElement('div');
            layer.dataset.layer = '';
            page.append(layer);
        }
        layer.replaceChildren();
        const series = new Map<string, HTMLElement[]>();
        const spread: { row: HTMLElement; translated: boolean }[] = [];
        for (const block of page.querySelectorAll<HTMLElement>(':scope > [data-b]')) {
            const parts = Array.from(block.querySelectorAll<HTMLElement>(':scope > [data-part]'));
            const boxes = parts.length > 0 ? parts : [block];
            const rows = set(layer, boxes, height);
            const label = block.dataset.series;
            if (label) series.set(label, [...series.get(label) ?? [], ...rows]);
            const text = block.textContent ?? '';
            if (rows.length === 1 && (block.dataset.label !== undefined && spreads(text) || block.dataset.running !== undefined && capitals(text)))
                spread.push({ row: rows[0], translated: !!block.querySelector('[data-translated]') || block.dataset.translated === 'true' });
        }
        for (const group of series.values()) {
            const size = Math.min(...group.map(row => parseFloat(row.style.fontSize)));
            for (const row of group) row.style.fontSize = `${size}px`;
        }
        for (const { row, translated } of spread) {
            const natural = Array.from(row.children).reduce((sum, child) => sum + child.getBoundingClientRect().width, 0);
            row.style.letterSpacing = `${letterSpacing(row.clientWidth - natural, row.textContent?.length ?? 0, parseFloat(row.style.fontSize), translated)}px`;
        }
    }

    // Sets a block's words into its lines, at its measured size or as much smaller as it takes; returns the lines used.
    function set(layer: HTMLElement, boxes: HTMLElement[], height: number): HTMLElement[] {
        const width = layer.clientWidth || height;
        const lines = boxes.flatMap(box => {
            const own = lineBoxes(box.dataset.rows ?? '', blockBox(box.dataset.box ?? ''));
            // The last line of a box may run as wide as its widest, so a longer translation finds room.
            const right = Math.max(...own.map(line => line.left + line.width));
            return own.map((line, index) => ({ ...line, width: index === own.length - 1 ? right - line.left : line.width, size: startSize(Number(box.dataset.base), height) }));
        });
        const tokens = boxes.flatMap(box => words(box));
        if (lines.length === 0 || tokens.length === 0) return [];
        // A line may run a little past the original's ink, as the print's margin allows. The original keeps its own lines, each made smaller only where
        // our face runs wider; a translation wraps from line to line, and all of it gets smaller where it does not fit the lines.
        const over = (row: HTMLElement) => row.scrollWidth > row.clientWidth * 1.06 + 1;
        const lined = tokens.filter(token => token === '\n').length >= lines.length - 1;
        let placed: HTMLElement[] = [];
        for (let scale = 1, step = 0; step < 60; step++, scale *= 0.97) {
            for (const row of placed) row.remove();
            placed = [];
            let index = 0, count = 0;
            const open = () => {
                const line = lines[index];
                const row = document.createElement('div');
                row.className = 'reader__smart-line';
                const size = line.size * scale;
                Object.assign(row.style, { left: `${line.left * width}px`, width: `${line.width * width}px`, top: `${line.baseline * height - size * 0.8}px`, fontSize: `${size}px` });
                layer.append(row);
                placed.push(row);
                return row;
            };
            let row = open();
            let fits = true;
            for (const token of tokens) {
                if (token === '\n') {
                    if (count === 0 || index + 1 >= lines.length && lined) continue;
                    if (++index >= lines.length) { fits = false; break; }
                    row = open();
                    count = 0;
                    continue;
                }
                const span = add(row, token, count > 0);
                if (!lined && over(row) && count > 0) {
                    span.textContent = span.textContent!.slice(0, -(token.text.length + 1));
                    if (!span.textContent) span.remove();
                    if (++index >= lines.length) { fits = false; break; }
                    row = open();
                    add(row, token, false);
                    count = 0;
                }
                count++;
            }
            if (fits && (lined || !placed.some(row => over(row) && row.childNodes.length > 0 && (row.textContent ?? '').includes(' ')))) break;
        }
        for (const row of placed.filter(over)) row.style.fontSize = `${parseFloat(row.style.fontSize) * row.clientWidth * 1.03 / row.scrollWidth}px`;
        return placed;
    }

    // A word in the look of its run; the next word of the same run goes into the same span.
    function add(row: HTMLElement, token: Word, spaced: boolean): HTMLElement {
        const last = row.lastElementChild as HTMLElement | null;
        const text = (spaced ? ' ' : '') + token.text;
        if (last && last.dataset.look === token.look) { last.textContent += text; return last; }
        const span = token.template.cloneNode(false) as HTMLElement;
        span.dataset.look = token.look;
        span.textContent = text;
        row.append(span);
        return span;
    }

    // A box's words in their runs' looks, with a line break where the original broke a line (or keeps a lead-in apart).
    function words(box: HTMLElement): (Word | '\n')[] {
        const tokens: (Word | '\n')[] = [];
        let look = 0;
        const walk = (node: Node, template: HTMLElement | undefined, id: string): void => {
            if (node instanceof HTMLElement) {
                if (node.tagName === 'WBR' || node.tagName === 'BR') {
                    // A lead-in's own break and the original's line break after it are one break.
                    if (tokens.length > 0 && tokens[tokens.length - 1] !== '\n') tokens.push('\n');
                    return;
                }
                if (node.dataset.face) { template = node; id = String(look++); }
                for (const child of Array.from(node.childNodes)) walk(child, template, id);
                return;
            }
            if (node.nodeType === Node.TEXT_NODE && template)
                for (const text of (node.textContent ?? '').split(/\s+/).filter(Boolean)) tokens.push({ text, template, look: id });
        };
        walk(box, undefined, '');
        return tokens;
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
