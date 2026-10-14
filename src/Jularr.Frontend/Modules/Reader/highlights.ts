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
        else void create(1, noteField.value);
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

    // Wraps the text between both points block by block.
    function draw(annotation: Annotation) {
        for (const block of Array.from(content.querySelectorAll<HTMLElement>("[data-b]"))) {
            const number = Number(block.dataset.b);
            if (number < annotation.startBlock || number > annotation.endBlock) continue;
            const from = number === annotation.startBlock ? annotation.startOffset : 0;
            const to = number === annotation.endBlock ? annotation.endOffset : Number.MAX_SAFE_INTEGER;
            let count = 0;
            const texts: Text[] = [];
            const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT);
            for (let text = walker.nextNode(); text; text = walker.nextNode()) texts.push(text as Text);
            for (const text of texts) {
                const length = text.length;
                const start = Math.max(from - count, 0);
                const end = Math.min(to - count, length);
                count += length;
                if (start >= end) continue;
                const target = start > 0 ? text.splitText(start) : text;
                if (end - start < target.length) target.splitText(end - start);
                const mark = document.createElement("mark");
                mark.className = "reader__mark" + (annotation.note ? " has-note" : "");
                mark.dataset.annotation = String(annotation.id);
                mark.dataset.color = String(annotation.highlightColorTypeId);
                target.replaceWith(mark);
                mark.append(target);
            }
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
