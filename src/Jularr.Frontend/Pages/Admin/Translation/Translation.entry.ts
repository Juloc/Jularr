export {};

// Admin-only model status polling. A GET never starts inference or a download.
const status = document.querySelector<HTMLElement>('[data-translation-download]');
if (status) {
    const endpoint = status.dataset.modelsUrl!;
    let previousState = status.dataset.downloadState ?? 'idle';
    const active = (state: string) => state === 'queued' || state === 'downloading';

    async function refresh(): Promise<void> {
        if (document.hidden || !status?.isConnected) return;
        try {
            const response = await fetch(endpoint, { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok) return;
            const data = await response.json() as {
                downloadState: string; downloadModel: string; downloadProgress: number;
            };
            const state = data.downloadState;
            if (!['idle', 'queued', 'downloading', 'installed', 'failed', 'interrupted'].includes(state)) return;
            status.hidden = state === 'idle';
            const label = status.getAttribute('data-state-' + state) ?? state;
            status.textContent = state === 'idle' ? '' : `${label}: ${data.downloadModel} (${data.downloadProgress}%)`;
            status.dataset.downloadState = state;
            if (active(previousState) && !active(state)) {
                // Reload server-rendered model cards, their buttons and the offline message.
                location.reload();
                return;
            }
            previousState = state;
        } catch {
            // Keep the last known state; the manual refresh link still works.
        }
    }
    void refresh();
    const timer = window.setInterval(() => { void refresh(); }, 3000);
    window.addEventListener('pagehide', () => window.clearInterval(timer), { once: true });
    document.addEventListener('visibilitychange', () => { if (!document.hidden) void refresh(); });
}


// The progress of the book translation: the server renders the part, the page swaps it in every ten seconds while it is visible.
{
    const progressUrl = location.pathname + '?handler=Progress';
    async function refreshProgress(): Promise<void> {
        const current = document.querySelector<HTMLElement>('[data-translation-progress]');
        if (document.hidden || !current?.isConnected) return;
        try {
            const response = await fetch(progressUrl, { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok) return;
            const template = document.createElement('template');
            template.innerHTML = (await response.text()).trim();
            const next = template.content.firstElementChild;
            if (next?.hasAttribute('data-translation-progress')) current.replaceWith(next);
        } catch {
            // Keep what is shown; the next round tries again.
        }
    }
    if (document.querySelector('[data-translation-progress]')) {
        const timer = window.setInterval(() => { void refreshProgress(); }, 10000);
        window.addEventListener('pagehide', () => window.clearInterval(timer), { once: true });
        document.addEventListener('visibilitychange', () => { if (!document.hidden) void refreshProgress(); });
    }
}

// The translations this browser kept (reader: Mozilla in the browser) live in its own database; this button empties it.
document.querySelector<HTMLButtonElement>('[data-translation-clear-browser-cache]')?.addEventListener('click', () => {
    const done = () => document.querySelector<HTMLElement>('[data-translation-browser-cache-cleared]')?.removeAttribute('hidden');
    if (typeof indexedDB === 'undefined') { done(); return; }
    const request = indexedDB.deleteDatabase('jularr-translation');
    request.onsuccess = done;
    request.onerror = done;
    request.onblocked = done;
});

// A benchmark may take minutes. Acknowledge the form submission immediately and
// prevent accidental duplicate downloads/benchmarks until the response navigates.
document.querySelectorAll<HTMLFormElement>('form[data-translation-submit]').forEach(form => {
    form.addEventListener('submit', () => {
        form.setAttribute('aria-busy', 'true');
        const submit = form.querySelector<HTMLButtonElement>('button[type=submit]');
        if (submit) {
            submit.disabled = true;
            submit.textContent = (submit.textContent?.trim() ?? '') + '…';
        }
    });
});
