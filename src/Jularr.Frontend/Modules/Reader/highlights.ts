// Highlights and notes: drawn from the saved offsets, created from a selection, edited in place.
interface Annotation {
    id: number;
    sectionIndex: number;
    startBlock: number;
    startOffset: number;
    endBlock: number;
    endOffset: number;
    quote: string;
    note: string | null;
    highlightColorTypeId: number;
}

// Yellow, the first colour, when a note is saved without choosing one.
const DefaultColor = 1;

interface Point {
    block: number;
    offset: number;
}

export function highlights(reader: HTMLElement, content: HTMLElement, section: number): void {
    const url = reader.dataset.annotationsUrl!;
    const antiforgery = reader.dataset.antiforgery!;
    const toolbar = reader.querySelector<HTMLElement>("[data-role=toolbar]")!;
    const popover = reader.querySelector<HTMLElement>("[data-role=popover]")!;
    const noteField = popover.querySelector<HTMLTextAreaElement>("textarea")!;
    const annotations = new Map<number, Annotation>();
    let pending: { start: Point; end: Point; quote: string } | undefined;
    let open: Annotation | undefined;

    for (const annotation of JSON.parse(reader.dataset.annotations ?? "[]") as Annotation[]) {
        annotations.set(annotation.id, annotation);
        draw(annotation);
    }

    // A selection offers colours and a note; pressing a colour keeps the selection.
    document.addEventListener("selectionchange", () => window.setTimeout(offer, 0));
    toolbar.addEventListener("mousedown", event => event.preventDefault());
    toolbar.querySelectorAll<HTMLButtonElement>("[data-color]").forEach(button => button.addEventListener("click", () => void create(Number(button.dataset.color), null)));
    toolbar.querySelector<HTMLButtonElement>("[data-role=add-note]")?.addEventListener("click", () => {
        if (!pending) return;
        open = undefined;
        noteField.value = "";
        show(popover, toolbar.getBoundingClientRect());
        noteField.focus();
    });

    // A highlight opens its note.
    content.addEventListener("click", event => {
        const mark = (event.target as HTMLElement).closest<HTMLElement>("mark[data-annotation]");
        if (!mark) return;
        open = annotations.get(Number(mark.dataset.annotation));
        if (!open) return;
        pending = undefined;
        noteField.value = open.note ?? "";
        show(popover, mark.getBoundingClientRect());
        event.stopPropagation();
    });
    popover.querySelectorAll<HTMLButtonElement>("[data-color]").forEach(button => button.addEventListener("click", () => {
        if (open) void update(open, noteField.value, Number(button.dataset.color));
        else void create(Number(button.dataset.color), noteField.value);
    }));
    popover.querySelector<HTMLButtonElement>("[data-role=save-note]")?.addEventListener("click", () => {
        if (open) void update(open, noteField.value, open.highlightColorTypeId);
        else void create(DefaultColor, noteField.value);
    });
    popover.querySelector<HTMLButtonElement>("[data-role=delete-note]")?.addEventListener("click", () => { if (open) void remove(open); });
    document.addEventListener("keydown", event => {
        if (event.key === "Escape") hide();
    });

    function offer() {
        const selection = window.getSelection();
        if (!selection || selection.isCollapsed || selection.rangeCount === 0 || !popover.hidden) {
            toolbar.hidden = true;
            return;
        }

        const range = selection.getRangeAt(0);
        const start = point(range.startContainer, range.startOffset);
        const end = point(range.endContainer, range.endOffset);
        const quote = selection.toString().replace(/\s+/g, " ").trim();
        if (!start || !end || quote.length === 0 || (start.block === end.block && start.offset >= end.offset)) {
            toolbar.hidden = true;
            return;
        }

        pending = { start, end, quote: quote.length > 1000 ? quote.slice(0, 999) + "…" : quote };
        show(toolbar, range.getBoundingClientRect());
    }

    async function create(color: number, note: string | null) {
        if (!pending) return;
        const body = {
            sectionIndex: section, startBlock: pending.start.block, startOffset: pending.start.offset, endBlock: pending.end.block, endOffset: pending.end.offset,
            quote: pending.quote, note: note?.trim() || null, highlightColorTypeId: color
        };
        const response = await send(url, "POST", body);
        if (!response) return;
        const annotation: Annotation = { id: (await response.json() as { id: number }).id, ...body };
        annotations.set(annotation.id, annotation);
        window.getSelection()?.removeAllRanges();
        draw(annotation);
        hide();
    }

    async function update(annotation: Annotation, note: string, color: number) {
        const body = { note: note.trim() || null, highlightColorTypeId: color };
        if (!await send(`${url}/${annotation.id}`, "PUT", body)) return;
        annotation.note = body.note;
        annotation.highlightColorTypeId = color;
        content.querySelectorAll<HTMLElement>(`mark[data-annotation="${annotation.id}"]`).forEach(mark => {
            mark.dataset.color = String(color);
            mark.classList.toggle("has-note", annotation.note !== null);
        });
        hide();
    }

    async function remove(annotation: Annotation) {
        if (!await send(`${url}/${annotation.id}`, "DELETE")) return;
        annotations.delete(annotation.id);
        content.querySelectorAll<HTMLElement>(`mark[data-annotation="${annotation.id}"]`).forEach(mark => mark.replaceWith(...Array.from(mark.childNodes)));
        content.normalize();
        hide();
    }

    async function send(target: string, method: string, body?: object): Promise<Response | undefined> {
        try {
            const response = await fetch(target, {
                method,
                headers: { "Content-Type": "application/json", RequestVerificationToken: antiforgery },
                body: body ? JSON.stringify(body) : undefined
            });
            reader.querySelector<HTMLElement>("[data-role=save-error]")!.hidden = response.ok;
            return response.ok ? response : undefined;
        } catch {
            reader.querySelector<HTMLElement>("[data-role=save-error]")!.hidden = false;
            return undefined;
        }
    }

    // Block number and character offset of a DOM position inside the content.
    function point(node: Node, offset: number): Point | undefined {
        const block = (node instanceof Element ? node : node.parentElement)?.closest<HTMLElement>("[data-b]");
        if (!block || !content.contains(block)) return undefined;
        const before = document.createRange();
        before.setStart(block, 0);
        before.setEnd(node, offset);
        return { block: Number(block.dataset.b), offset: before.toString().length };
    }

    // The text position at a block's character offset.
    function locate(block: number, offset: number): { node: Text; offset: number } | undefined {
        const element = content.querySelector<HTMLElement>(`[data-b="${block}"]`);
        if (!element) return undefined;
        let count = 0;
        let last: Text | undefined;
        const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        for (let text = walker.nextNode() as Text | null; text; text = walker.nextNode() as Text | null) {
            if (count + text.length >= offset) return { node: text, offset: offset - count };
            count += text.length;
            last = text;
        }

        return last ? { node: last, offset: last.length } : undefined;
    }

    // Wraps exactly the saved range; a book that changed underneath keeps its text unmarked.
    function draw(annotation: Annotation) {
        const start = locate(annotation.startBlock, annotation.startOffset);
        const end = locate(annotation.endBlock, annotation.endOffset);
        if (!start || !end) return;
        const range = document.createRange();
        range.setStart(start.node, start.offset);
        range.setEnd(end.node, end.offset);
        const quote = annotation.quote.replace(/…$/, "");
        if (!range.toString().replace(/\s+/g, " ").trim().startsWith(quote)) return;

        const texts: Text[] = [];
        const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
        for (let text = walker.nextNode() as Text | null; text; text = walker.nextNode() as Text | null) {
            if (range.intersectsNode(text)) texts.push(text);
        }

        for (const text of texts) {
            const from = text === start.node ? start.offset : 0;
            const to = text === end.node ? end.offset : text.length;
            if (from >= to) continue;
            const target = from > 0 ? text.splitText(from) : text;
            if (to - from < target.length) target.splitText(to - from);
            const mark = document.createElement("mark");
            mark.className = "reader__mark" + (annotation.note ? " has-note" : "");
            mark.dataset.annotation = String(annotation.id);
            mark.dataset.color = String(annotation.highlightColorTypeId);
            target.replaceWith(mark);
            mark.append(target);
        }
    }

    function show(panel: HTMLElement, anchor: DOMRect) {
        panel.hidden = false;
        panel.style.top = `${Math.min(anchor.bottom + 8, window.innerHeight - panel.offsetHeight - 8)}px`;
        panel.style.left = `${Math.min(Math.max(8, anchor.left), window.innerWidth - panel.offsetWidth - 8)}px`;
    }

    function hide() {
        toolbar.hidden = true;
        popover.hidden = true;
        open = undefined;
        pending = undefined;
    }
}

// Marks every occurrence of the searched words in the section and shows the first.
export function findings(content: HTMLElement, query: string): void {
    const needle = query.trim().toLocaleLowerCase();
    if (needle.length < 2) return;
    const texts: Text[] = [];
    const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
    for (let text = walker.nextNode(); text; text = walker.nextNode()) texts.push(text as Text);
    let first: HTMLElement | undefined;
    for (const text of texts) {
        let target: Text | null = text;
        while (target) {
            const index = target.data.toLocaleLowerCase().indexOf(needle);
            if (index < 0) break;
            const match = target.splitText(index);
            const rest = match.splitText(needle.length);
            const mark = document.createElement("mark");
            mark.className = "reader__found";
            match.replaceWith(mark);
            mark.append(match);
            first ??= mark;
            target = rest;
        }
    }

    first?.scrollIntoView({ block: "center" });
}
