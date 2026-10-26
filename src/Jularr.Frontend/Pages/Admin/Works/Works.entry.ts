import { onPage } from '../../../Modules/Navigation/PageSwap';

const boxSelector = 'input[name="workIds"][form="works-bulk"]';

// The titles of a delete are written into its dialog when it opens: the chosen ones of the list (the dock's bulk zone opens it).
function initializeDelete(): void {
    const dialog = document.getElementById('works-delete') as HTMLDialogElement | null;
    const holder = dialog?.querySelector<HTMLElement>('[data-works-ids]');
    document.querySelectorAll<HTMLElement>('[data-works-delete]').forEach(button => {
        if (button.dataset.worksReady || !dialog || !holder) return;
        button.dataset.worksReady = 'true';
        button.addEventListener('click', () => {
            const ids = Array.from(document.querySelectorAll<HTMLInputElement>(boxSelector)).filter(box => box.checked).map(box => box.value);
            if (ids.length === 0) return;
            holder.replaceChildren(...ids.map(id => Object.assign(document.createElement('input'), { type: 'hidden', name: 'workIds', value: id })));
            dialog.showModal();
        });
    });
}

// A filter that lets everything through stays out of the address.
document.addEventListener('submit', event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const empty = Array.from(form.querySelectorAll<HTMLSelectElement>('select[data-works-filter]')).filter(select => select.value === '' && !select.disabled);
    empty.forEach(select => { select.disabled = true; });
    setTimeout(() => empty.forEach(select => { select.disabled = false; }), 0);
}, true);

let searchTimer: ReturnType<typeof setTimeout> | undefined;
let searchRequest: AbortController | undefined;

// Only the small status fragment refreshes. The list, selected titles and expanded results stay in place.
function initializeSearchProgress(): void {
    clearTimeout(searchTimer);
    searchRequest?.abort();
    const current = document.querySelector<HTMLElement>('[data-search-progress][data-running="true"]');
    if (!current) return;
    searchRequest = new AbortController();
    const signal = searchRequest.signal;
    const refresh = async (): Promise<void> => {
        let root = document.querySelector<HTMLElement>('[data-search-progress]');
        if (!root || root.dataset.running !== 'true' || signal.aborted) return;
        try {
            if (!document.hidden && !root.contains(document.activeElement)) {
                const response = await fetch(root.dataset.refresh!, { signal, credentials: 'same-origin', headers: { 'Accept': 'text/html' } });
                if (!response.ok) return;
                const updated = new DOMParser().parseFromString(await response.text(), 'text/html').querySelector<HTMLElement>('[data-search-progress]');
                if (!updated || !root.isConnected || signal.aborted) return;
                root.replaceWith(updated);
                root = updated;
                if (root.dataset.running === 'false') await refreshSearchResults(signal);
            }
        } catch {
            // The native Refresh link remains available after a disconnected request.
            return;
        }
        if (root.dataset.running === 'true' && !signal.aborted) searchTimer = setTimeout(() => void refresh(), 2000);
    };
    searchTimer = setTimeout(() => void refresh(), 2000);
}

async function refreshSearchResults(signal: AbortSignal): Promise<void> {
    const rows = document.querySelector<HTMLElement>('[data-search-result-rows]');
    if (!rows?.dataset.refresh) return;
    const response = await fetch(rows.dataset.refresh, { signal, credentials: 'same-origin', headers: { 'Accept': 'text/html' } });
    if (!response.ok) return;
    const updated = new DOMParser().parseFromString(await response.text(), 'text/html').querySelector<HTMLElement>('[data-search-result-rows]');
    if (!updated || !rows.isConnected || signal.aborted) return;
    const active = document.activeElement instanceof HTMLAnchorElement && rows.contains(document.activeElement) ? document.activeElement.getAttribute('href') : null;
    rows.replaceWith(updated);
    if (active) Array.from(updated.querySelectorAll('a')).find(link => link.getAttribute('href') === active)?.focus({ preventScroll: true });
}

onPage(import.meta.url, () => { initializeDelete(); initializeSearchProgress(); }, { partial: true });
