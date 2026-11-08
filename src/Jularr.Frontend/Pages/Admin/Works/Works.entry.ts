import { inserted, onPage, regionSwapped } from '../../../Modules/Navigation/PageSwap';
import { hasNewBuild } from '../../../Modules/Navigation/Navigation';

const boxSelector = 'input[name="workIds"][form="works-bulk"]';

// The titles of a delete are written into its dialog when it opens: the chosen ones of the list (the dock's bulk zone opens it).
function initializeDelete(): void {
    const dialog = document.getElementById('works-delete') as HTMLDialogElement | null;
    const holder = dialog?.querySelector<HTMLElement>('[data-works-ids]');
    document.querySelectorAll<HTMLElement>('[data-works-delete]').forEach(button => {
        if (button.dataset.worksReady || !dialog || !holder) return;
        button.dataset.worksReady = 'true';
        button.addEventListener('click', () => {
            const form = dialog.querySelector<HTMLFormElement>('.works-delete-form')!;
            if (form.dataset.deleteRefreshFailed) { dialog.showModal(); return; }
            const ids = Array.from(document.querySelectorAll<HTMLInputElement>(boxSelector)).filter(box => box.checked).map(box => box.value);
            if (ids.length === 0) return;
            holder.replaceChildren(...ids.map(id => Object.assign(document.createElement('input'), { type: 'hidden', name: 'workIds', value: id })));
            const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]')!;
            submit.disabled = false;
            submit.textContent = form.dataset.confirm ?? '';
            dialog.querySelector<HTMLElement>('.works-delete-progress')!.hidden = true;
            dialog.showModal();
        });
    });
}

const fill = (format: string | undefined, ...values: (string | number)[]): string => (format ?? '').replace(/\{(\d+)\}/g, (_, index: string) => String(values[Number(index)] ?? ''));

// Each confirmed server result advances the counter. One failed title stays selected while the others continue.
document.addEventListener('submit', event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || !form.matches('.works-delete-form')) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (!form.dataset.deleteRunning) void deleteSelection(form);
}, true);

async function deleteSelection(form: HTMLFormElement): Promise<void> {
    const dialog = form.closest<HTMLDialogElement>('dialog');
    const holder = form.querySelector<HTMLElement>('[data-works-ids]');
    const panel = dialog?.querySelector<HTMLElement>('.works-delete-progress');
    if (!dialog || !holder || !panel) return;
    const chosen = Array.from(holder.querySelectorAll<HTMLInputElement>('input[name="workIds"]'), input => input.value);
    if (chosen.length === 0) return;
    const data = new FormData(form);
    data.delete('workIds');
    const count = panel.querySelector<HTMLElement>('[data-delete-count]')!;
    const current = panel.querySelector<HTMLElement>('[data-delete-current]')!;
    const failures = panel.querySelector<HTMLElement>('[data-delete-failures]')!;
    const progress = panel.querySelector<HTMLProgressElement>('progress')!;
    const controls = Array.from(dialog.querySelectorAll<HTMLInputElement | HTMLButtonElement>('input, button'));
    const disabled = controls.map(control => control.disabled);
    const preventClose = (event: Event): void => event.preventDefault();
    const preventBackdrop = (event: Event): void => { if (event.target === dialog) event.stopPropagation(); };
    form.dataset.deleteRunning = 'true';
    panel.dataset.running = 'true';
    panel.hidden = false;
    failures.replaceChildren();
    progress.max = chosen.length;
    progress.value = 0;
    count.textContent = fill(form.dataset.progress, 0, chosen.length);
    controls.forEach(control => { control.disabled = true; });
    dialog.addEventListener('cancel', preventClose);
    dialog.addEventListener('click', preventBackdrop);
    let deleted = 0;
    for (const id of chosen) {
        if (!dialog.isConnected) break;
        const box = Array.from(document.querySelectorAll<HTMLInputElement>(boxSelector)).find(input => input.value === id);
        const title = box?.dataset.workTitle ?? id;
        current.textContent = fill(form.dataset.current, title);
        data.set('workId', id);
        let stop = false;
        try {
            const response = await fetch(form.dataset.deleteOne!, { method: 'POST', body: data, credentials: 'same-origin', headers: { Accept: 'text/html' } });
            stop = response.status === 401 || response.status === 403 || response.redirected;
            const answer = new DOMParser().parseFromString(await response.text(), 'text/html').querySelector<HTMLElement>('[data-delete-outcome]');
            if (!response.ok || answer?.dataset.deleteId !== id) throw new Error('No confirmed outcome');
            if (answer.dataset.deleteOutcome === 'done') {
                deleted++;
                if (box) {
                    box.checked = false;
                    box.dispatchEvent(new Event('change', { bubbles: true }));
                    box.closest('.list-row, .media-card')?.remove();
                }
            } else failures.append(answer);
        } catch {
            // An interrupted response may have committed. Keep it selected, show uncertainty and never automatically repeat it.
            const row = document.createElement('li');
            const name = document.createElement('span');
            name.className = 'works-report__title';
            name.textContent = title;
            const reason = document.createElement('span');
            reason.className = 'works-report__reason';
            reason.textContent = form.dataset.unconfirmed ?? '';
            row.append(name, reason);
            failures.append(row);
        }
        progress.value++;
        count.textContent = fill(form.dataset.progress, progress.value, chosen.length);
        if (stop) {
            current.textContent = form.dataset.stopped ?? '';
            break;
        }
        current.textContent = '';
    }
    count.textContent = fill(form.dataset.summary, deleted, chosen.length);
    let refreshed = false;
    if (dialog.isConnected) {
        try {
            await refreshDeletedList();
            refreshed = true;
        } catch {
            current.textContent = form.dataset.refreshFailed ?? '';
        }
    }
    controls.forEach((control, index) => { control.disabled = disabled[index]; });
    dialog.removeEventListener('cancel', preventClose);
    dialog.removeEventListener('click', preventBackdrop);
    delete form.dataset.deleteRunning;
    if (!refreshed) form.dataset.deleteRefreshFailed = 'true';
    panel.dataset.running = 'false';
    const remaining = Array.from(document.querySelectorAll<HTMLInputElement>(boxSelector)).filter(box => box.checked);
    holder.replaceChildren(...remaining.map(box => Object.assign(document.createElement('input'), { type: 'hidden', name: 'workIds', value: box.value })));
    const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]')!;
    submit.textContent = form.dataset.retry ?? '';
    submit.disabled = !refreshed || remaining.length === 0;
}

async function refreshDeletedList(): Promise<void> {
    const selected = new Set(Array.from(document.querySelectorAll<HTMLInputElement>(boxSelector)).filter(box => box.checked).map(box => box.value));
    const response = await fetch(location.href, { credentials: 'same-origin', headers: { Accept: 'text/html' } });
    if (!response.ok) throw new Error('List unavailable');
    const html = await response.text();
    if (hasNewBuild(document.documentElement.dataset.build, html)) throw new Error('Page build changed');
    const page = new DOMParser().parseFromString(html, 'text/html');
    const regions = ['[data-works-catalog]', '[data-works-dock]'].map(selector => ({ before: document.querySelector(selector), after: page.querySelector(selector) }));
    if (regions.some(region => !region.before || !region.after)) throw new Error('List unavailable');
    const position = { x: window.scrollX, y: window.scrollY };
    for (const { before, after } of regions) {
        after!.querySelectorAll<HTMLInputElement>(boxSelector).forEach(box => { box.checked = selected.has(box.value); });
        before!.replaceWith(after!);
        inserted(after!);
    }
    regionSwapped();
    document.querySelector<HTMLInputElement>(boxSelector)?.dispatchEvent(new Event('change', { bubbles: true }));
    window.scrollTo(position.x, position.y);
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
