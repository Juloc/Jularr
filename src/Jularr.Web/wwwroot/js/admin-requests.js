(() => {
    const table = document.querySelector('[data-admin-requests]');
    if (!table) {
        return;
    }

    const selection = [...table.querySelectorAll('[data-admreq-select]')];
    const selectAll = table.querySelector('[data-admreq-select-all]');
    const bulk = document.querySelector('[data-admreq-bulk]');
    const selectedCount = document.querySelector('[data-admreq-selected-count]');
    let lastSelectedIndex = -1;
    let extendsSelection = false;

    const syncSelection = () => {
        const selected = selection.filter(input => input.checked);
        for (const input of selection) {
            const row = input.closest('[data-admreq-row]');
            row?.classList.toggle('is-selected', input.checked);
            row?.setAttribute('aria-selected', input.checked ? 'true' : 'false');
        }

        if (selectAll) {
            selectAll.checked = selected.length === selection.length && selection.length > 0;
            selectAll.indeterminate = selected.length > 0 && selected.length < selection.length;
        }

        if (selectedCount) {
            selectedCount.textContent = String(selected.length);
        }

        if (bulk) {
            bulk.hidden = selected.length === 0;
        }
    };

    for (const [index, input] of selection.entries()) {
        input.addEventListener('click', event => {
            extendsSelection = event.shiftKey;
        });
        input.addEventListener('keydown', event => {
            extendsSelection = event.shiftKey;
        });
        input.addEventListener('change', () => {
            const nextState = input.checked;
            if (extendsSelection && lastSelectedIndex >= 0) {
                const first = Math.min(lastSelectedIndex, index);
                const last = Math.max(lastSelectedIndex, index);
                for (let selectedIndex = first; selectedIndex <= last; selectedIndex += 1) {
                    selection[selectedIndex].checked = nextState;
                }
            }

            extendsSelection = false;
            lastSelectedIndex = index;
            syncSelection();
        });
    }

    selectAll?.addEventListener('change', () => {
        for (const input of selection) {
            input.checked = selectAll.checked;
        }

        syncSelection();
    });

    table.addEventListener('click', event => {
        if (event.target.closest('a, button, input, select, summary, label')) {
            return;
        }

        const row = event.target.closest('[data-admreq-row]');
        const input = row?.querySelector('[data-admreq-select]');
        if (!input) {
            return;
        }

        input.checked = !input.checked;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    });

    table.addEventListener('keydown', event => {
        if (event.key !== ' ' && event.key !== 'Enter') {
            return;
        }

        const row = event.target.closest('[data-admreq-row]');
        const input = row?.querySelector('[data-admreq-select]');
        if (!input || event.target !== row) {
            return;
        }

        event.preventDefault();
        input.checked = !input.checked;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    });

    const filterForm = document.querySelector('[data-admreq-filters]');
    const search = filterForm?.querySelector('[data-admreq-search]');
    let searchTimer;
    let isComposing = false;
    const submitFilters = () => {
        if (typeof filterForm?.requestSubmit === 'function') {
            filterForm.requestSubmit();
        }
        else {
            filterForm?.submit();
        }
    };

    search?.addEventListener('compositionstart', () => {
        isComposing = true;
    });
    search?.addEventListener('compositionend', () => {
        isComposing = false;
        search.dispatchEvent(new Event('input', { bubbles: true }));
    });
    search?.addEventListener('input', () => {
        window.clearTimeout(searchTimer);
        if (isComposing) {
            return;
        }

        searchTimer = window.setTimeout(submitFilters, 320);
    });
    filterForm?.querySelectorAll('select').forEach(select => {
        select.addEventListener('change', submitFilters);
    });

    syncSelection();
})();
