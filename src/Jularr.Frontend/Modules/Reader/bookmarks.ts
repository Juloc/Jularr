// The profile's bookmarks in this book: the button marks or clears the current place, the list leads back to it.
export interface Place {
    section: number;
    block: number;
    progression: number;
    title: string | null;
}

export interface Bookmarks {
    refresh(): void;
}

export function initializeBookmarks(reader: HTMLElement, current: () => Place): Bookmarks {
    const url = reader.dataset.bookmarksUrl!;
    const antiforgery = reader.dataset.antiforgery!;
    const images = reader.dataset.format === 'comic';
    const list = reader.querySelector<HTMLUListElement>('[data-role=bookmark-list]')!;
    const group = list.closest<HTMLElement>('[data-role=bookmarks]')!;
    const buttons = Array.from(reader.querySelectorAll<HTMLButtonElement>('[data-reader-bookmark]'));
    const items = () => Array.from(list.querySelectorAll<HTMLLIElement>('li[data-bookmark-id]'));
    const at = (place: Place) => items().find(item => Number(item.dataset.section) === place.section && Number(item.dataset.block) === place.block);
    const share = (progression: number) => `${Math.round(progression * 100)} %`;

    function refresh(): void {
        const marked = at(current()) !== undefined;
        buttons.forEach(button => button.setAttribute('aria-pressed', String(marked)));
        group.hidden = items().length === 0;
    }

    async function send(target: string, method: string, body?: object): Promise<Response | undefined> {
        const feedback = reader.querySelector<HTMLElement>('[data-role=save-error]');
        try {
            const response = await fetch(target, {
                method,
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: antiforgery },
                body: body ? JSON.stringify(body) : undefined
            });
            if (feedback) feedback.hidden = response.ok;
            return response.ok ? response : undefined;
        } catch {
            if (feedback) feedback.hidden = false;
            return undefined;
        }
    }

    function entry(id: number, place: Place): HTMLLIElement {
        const item = document.createElement('li');
        item.className = 'reader__note';
        Object.assign(item.dataset, { bookmarkId: String(id), section: String(place.section), block: String(place.block), progression: String(place.progression) });
        const link = document.createElement('a');
        link.className = 'reader__note-quote';
        link.href = images ? `?p=${place.section}` : `?s=${place.section}&b=${place.block}`;
        link.textContent = images ? `${reader.dataset.pageLabel} ${place.section + 1}` : place.title ?? share(place.progression);
        const position = document.createElement('p');
        position.className = 'reader__note-text';
        position.textContent = share(place.progression);
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'button';
        remove.dataset.bookmarkDelete = '';
        remove.textContent = reader.dataset.bookmarkDeleteLabel!;
        item.append(link, position, remove);
        return item;
    }

    // The list stays in reading order.
    function insert(item: HTMLLIElement): void {
        const after = (other: HTMLLIElement) => Number(other.dataset.section) > Number(item.dataset.section)
            || Number(other.dataset.section) === Number(item.dataset.section) && Number(other.dataset.block) > Number(item.dataset.block);
        list.insertBefore(item, items().find(after) ?? null);
    }

    async function toggle(): Promise<void> {
        const place = current();
        const existing = at(place);
        if (existing) {
            if (await send(`${url}/${existing.dataset.bookmarkId}`, 'DELETE')) existing.remove();
        } else {
            const response = await send(url, 'POST', { sectionIndex: place.section, block: place.block, progression: Number(place.progression.toFixed(4)), title: place.title });
            if (response) insert(entry((await response.json() as { id: number }).id, place));
        }
        refresh();
    }

    buttons.forEach(button => button.addEventListener('click', () => void toggle()));
    list.addEventListener('click', async event => {
        const button = (event.target as Element).closest<HTMLButtonElement>('[data-bookmark-delete]');
        const item = button?.closest<HTMLLIElement>('li[data-bookmark-id]');
        if (!item) return;
        if (await send(`${url}/${item.dataset.bookmarkId}`, 'DELETE')) item.remove();
        refresh();
    });
    refresh();
    return { refresh };
}
