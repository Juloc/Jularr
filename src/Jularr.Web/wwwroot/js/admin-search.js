(() => {
    const form = document.querySelector('[data-admin-search]');
    const input = form?.querySelector('input[type="search"]');
    const panel = document.querySelector('[data-admin-search-preview]');
    if (!form || !input || !panel) return;

    const results = panel.querySelector('[data-admin-search-results]');
    const loading = panel.querySelector('[data-admin-search-loading]');
    const error = panel.querySelector('[data-admin-search-error]');
    const all = panel.querySelector('[data-admin-search-all]');
    let timer;
    let controller;
    let revision = 0;
    let composing = false;
    let restoringFocus = false;
    const position = () => {
        if (!panel.matches(':popover-open')) return;
        const bounds = form.getBoundingClientRect();
        panel.style.width = `${Math.min(Math.max(bounds.width, 320), window.innerWidth - 24)}px`;
        panel.style.left = `${Math.max(12, Math.min(bounds.left, window.innerWidth - panel.offsetWidth - 12))}px`;
        panel.style.top = `${bounds.bottom + 8}px`;
        panel.style.maxHeight = `${Math.max(80, Math.min(560, window.innerHeight - bounds.bottom - 20))}px`;
    };
    const search = async () => {
        const query = input.value.trim();
        controller?.abort();
        const requestRevision = ++revision;
        if (!query) {
            panel.hidePopover();
            results.replaceChildren();
            return;
        }

        const requestController = new AbortController();
        controller = requestController;
        const timeout = setTimeout(() => requestController.abort(), 10000);
        loading.hidden = false;
        error.hidden = true;
        results.replaceChildren();
        all.href = `/Admin/Search?q=${encodeURIComponent(query)}`;
        panel.showPopover();
        position();
        try {
            const response = await fetch(`/Admin/Search?handler=Preview&q=${encodeURIComponent(query)}`, { signal: requestController.signal, credentials: 'same-origin', headers: { Accept: 'text/html' } });
            if (!response.ok || response.redirected) throw new Error('Admin search unavailable');
            const html = await response.text();
            if (requestRevision !== revision || input.value.trim() !== query) return;
            // Only the same-origin, permission-scoped Razor fragment supplies result markup; user text is server-encoded.
            const fragment = new DOMParser().parseFromString(html, 'text/html');
            results.replaceChildren(...fragment.body.childNodes);
        }
        catch (exception) {
            if (requestRevision === revision && input.value.trim() === query) error.hidden = false;
        }
        finally {
            clearTimeout(timeout);
            if (requestRevision === revision) loading.hidden = true;
        }
    };
    const schedule = () => {
        clearTimeout(timer);
        if (!composing) timer = setTimeout(search, 250);
    };
    input.addEventListener('input', schedule);
    input.addEventListener('compositionstart', () => { composing = true; clearTimeout(timer); });
    input.addEventListener('compositionend', () => { composing = false; schedule(); });
    input.addEventListener('focus', () => { if (!restoringFocus && input.value.trim()) schedule(); });
    input.addEventListener('keydown', event => {
        if (event.key === 'ArrowDown' && panel.matches(':popover-open')) {
            event.preventDefault();
            results.querySelector('a')?.focus();
        }
        if (event.key === 'Escape') { clearTimeout(timer); controller?.abort(); ++revision; panel.hidePopover(); }
    });
    panel.addEventListener('keydown', event => {
        const links = [...panel.querySelectorAll('a')];
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            const direction = event.key === 'ArrowDown' ? 1 : -1;
            links[(links.indexOf(document.activeElement) + direction + links.length) % links.length]?.focus();
        }
        if (event.key === 'Escape') {
            clearTimeout(timer);
            controller?.abort();
            ++revision;
            panel.hidePopover();
            restoringFocus = true;
            input.focus();
            restoringFocus = false;
        }
    });
    panel.addEventListener('toggle', () => input.setAttribute('aria-expanded', String(panel.matches(':popover-open'))));
    window.addEventListener('resize', position);
    document.addEventListener('scroll', position, true);
})();
