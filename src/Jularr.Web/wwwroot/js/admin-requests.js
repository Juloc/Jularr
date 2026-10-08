(() => {
    const submitForm = form => {
        if (typeof form?.requestSubmit === 'function') {
            form.requestSubmit();
        }
        else {
            form?.submit();
        }
    };

    const filterForm = document.querySelector('[data-admreq-filters]');
    const search = filterForm?.querySelector('[data-admreq-search]');
    let searchTimer;
    let isComposing = false;

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

        searchTimer = window.setTimeout(() => submitForm(filterForm), 320);
    });
    filterForm?.querySelectorAll('select').forEach(select => {
        select.addEventListener('change', () => submitForm(filterForm));
    });

    const customSelects = [...document.querySelectorAll('select[data-admreq-custom-select]')];
    const closeCustomSelects = except => {
        for (const select of customSelects) {
            const control = select.closest('[data-admreq-enhanced]');
            const trigger = control?.querySelector('[data-admreq-select-trigger]');
            const panel = control?.querySelector('[data-admreq-select-panel]');
            if (!control || control === except || !panel?.hasAttribute('popover')) {
                continue;
            }

            panel.hidePopover();
            trigger?.setAttribute('aria-expanded', 'false');
        }
    };

    for (const select of customSelects) {
        if (!select.closest('.admreq-select-control, .admreq-page-size')) {
            continue;
        }

        const control = select.closest('.admreq-select-control, .admreq-page-size');
        const trigger = document.createElement('button');
        trigger.type = 'button';
        trigger.className = 'admreq-select-trigger';
        trigger.setAttribute('role', 'combobox');
        trigger.setAttribute('aria-haspopup', 'listbox');
        trigger.setAttribute('aria-expanded', 'false');
        trigger.setAttribute('aria-label', select.getAttribute('aria-label') || '');
        trigger.dataset.admreqSelectTrigger = '';
        const triggerText = document.createElement('span');
        triggerText.dataset.admreqSelectValue = '';
        const chevron = document.createElement('span');
        chevron.className = 'admreq-select-chevron';
        chevron.setAttribute('aria-hidden', 'true');
        trigger.append(triggerText, chevron);

        const panel = document.createElement('div');
        panel.className = 'admreq-dropdown-panel';
        panel.setAttribute('popover', 'auto');
        panel.setAttribute('role', 'listbox');
        panel.id = `admreq-options-${select.name}`;
        panel.dataset.admreqSelectPanel = '';
        trigger.setAttribute('aria-controls', panel.id);
        const options = [...select.options];
        if (options.length > 8) {
            const searchBox = document.createElement('input');
            searchBox.type = 'search';
            searchBox.className = 'admreq-dropdown-search';
            searchBox.placeholder = select.getAttribute('aria-label') || '';
            searchBox.setAttribute('aria-label', searchBox.placeholder);
            searchBox.addEventListener('input', () => {
                const query = searchBox.value.trim().toLocaleLowerCase();
                for (const optionButton of panel.querySelectorAll('[role="option"]')) {
                    optionButton.hidden = !optionButton.textContent.toLocaleLowerCase().includes(query);
                }
            });
            panel.append(searchBox);
        }

        const optionButtons = options.map((option, index) => {
            const optionButton = document.createElement('button');
            optionButton.type = 'button';
            optionButton.className = 'admreq-dropdown-option';
            optionButton.setAttribute('role', 'option');
            optionButton.dataset.value = option.value;
            optionButton.tabIndex = -1;
            optionButton.textContent = option.textContent.trim();
            optionButton.addEventListener('click', () => {
                select.value = option.value;
                select.dispatchEvent(new Event('change', { bubbles: true }));
                panel.hidePopover();
                trigger.setAttribute('aria-expanded', 'false');
                trigger.focus();
            });
            optionButton.addEventListener('keydown', event => {
                if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                    event.preventDefault();
                    const direction = event.key === 'ArrowDown' ? 1 : -1;
                    const visible = optionButtons.filter(button => !button.hidden);
                    const current = visible.indexOf(optionButton);
                    visible[(current + direction + visible.length) % visible.length]?.focus();
                }
                else if (event.key === 'Home' || event.key === 'End') {
                    event.preventDefault();
                    (event.key === 'Home' ? optionButtons[0] : optionButtons.at(-1))?.focus();
                }
            });
            panel.append(optionButton);
            return optionButton;
        });

        const syncSelect = () => {
            const selectedOption = select.selectedOptions[0];
            triggerText.textContent = selectedOption?.textContent.trim() || '';
            for (const optionButton of optionButtons) {
                const selected = optionButton.dataset.value === select.value;
                optionButton.setAttribute('aria-selected', String(selected));
                optionButton.classList.toggle('is-selected', selected);
            }
        };
        trigger.addEventListener('click', () => {
            const isOpen = trigger.getAttribute('aria-expanded') === 'true';
            closeCustomSelects(control);
            if (isOpen) {
                panel.hidePopover();
                trigger.setAttribute('aria-expanded', 'false');
                return;
            }

            panel.showPopover();
            trigger.setAttribute('aria-expanded', 'true');
            const triggerBounds = trigger.getBoundingClientRect();
            const panelWidth = Math.max(panel.offsetWidth, triggerBounds.width);
            const panelHeight = panel.offsetHeight;
            const left = Math.max(8, Math.min(triggerBounds.left, window.innerWidth - panelWidth - 8));
            const top = triggerBounds.bottom + panelHeight <= window.innerHeight - 8
                ? triggerBounds.bottom + 6
                : Math.max(8, triggerBounds.top - panelHeight - 6);
            panel.style.minWidth = `${triggerBounds.width}px`;
            panel.style.left = `${left}px`;
            panel.style.top = `${top}px`;
            const selectedIndex = options.findIndex(option => option.value === select.value);
            (optionButtons[selectedIndex] || optionButtons[0])?.focus();
        });
        trigger.addEventListener('keydown', event => {
            if (event.key === 'ArrowDown' || event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                trigger.click();
            }
        });
        panel.addEventListener('toggle', () => trigger.setAttribute('aria-expanded', String(panel.matches(':popover-open'))));
        select.addEventListener('change', syncSelect);
        select.tabIndex = -1;
        select.setAttribute('aria-hidden', 'true');
        control.dataset.admreqEnhanced = '';
        control.append(trigger, panel);
        syncSelect();
    }

    document.addEventListener('click', event => {
        if (!event.target.closest('[data-admreq-enhanced], [data-admreq-select-panel]')) {
            closeCustomSelects();
        }
    });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            closeCustomSelects();
        }
    });
    window.addEventListener('resize', () => closeCustomSelects());
    document.addEventListener('scroll', () => closeCustomSelects(), true);

    const pageSizeForm = document.querySelector('[data-admreq-page-size]');
    const pageSizeSelect = pageSizeForm?.querySelector('[data-admreq-page-size-select]');
    const customPageSize = pageSizeForm?.querySelector('[data-admreq-custom-page-size]');
    const syncCustomPageSize = () => {
        if (!pageSizeSelect || !customPageSize) {
            return false;
        }

        const custom = pageSizeSelect.value === '0';
        customPageSize.disabled = !custom;
        return custom;
    };

    pageSizeSelect?.addEventListener('change', () => {
        if (syncCustomPageSize()) {
            customPageSize?.focus();
            return;
        }

        submitForm(pageSizeForm);
    });
    customPageSize?.addEventListener('change', () => submitForm(pageSizeForm));

    const table = document.querySelector('[data-admin-requests]');
    if (!table) {
        return;
    }

    const selection = [...table.querySelectorAll('[data-admreq-select]')];
    const selectAll = table.querySelector('[data-admreq-select-all]');
    const bulk = document.querySelector('[data-admreq-bulk]');
    const selectedCount = document.querySelector('[data-admreq-selected-count]');
    const clearSelection = document.querySelector('[data-admreq-clear-selection]');
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
            document.body.classList.toggle('has-admreq-selection', selected.length > 0);
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

    clearSelection?.addEventListener('click', () => {
        for (const input of selection) {
            input.checked = false;
        }

        syncSelection();
        selection[0]?.focus();
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

    syncSelection();
})();

(() => {
    const dialog = document.querySelector('[data-admreq-dialog]');
    if (!dialog) {
        return;
    }

    const fields = {
        title: dialog.querySelector('[data-admreq-dialog-title]'),
        requester: dialog.querySelector('[data-admreq-dialog-requester]'),
        requested: dialog.querySelector('[data-admreq-dialog-requested]'),
        approval: dialog.querySelector('[data-admreq-dialog-approval]'),
        media: dialog.querySelector('[data-admreq-dialog-media]'),
        message: dialog.querySelector('[data-admreq-dialog-message]')
    };
    document.addEventListener('click', event => {
        const button = event.target.closest('[data-admreq-details]');
        if (!button) {
            return;
        }

        for (const [key, field] of Object.entries(fields)) {
            if (field) {
                field.textContent = button.dataset[key] || '—';
            }
        }

        if (fields.requested && button.dataset.requested) {
            fields.requested.textContent = window.JularrLocalTime?.format(button.dataset.requested) || button.dataset.requested;
        }

        dialog.showModal();
    });
    dialog.querySelector('[data-admreq-dialog-close]')?.addEventListener('click', () => dialog.close());
    dialog.addEventListener('click', event => {
        if (event.target === dialog) {
            dialog.close();
        }
    });
})();
