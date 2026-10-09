(() => {
    const adminNav = document.querySelector('.admin-nav');
    if (adminNav && window.matchMedia('(max-width: 820px)').matches) {
        const activeNavItem = adminNav.querySelector('.admin-nav-item.active');
        if (activeNavItem) {
            window.requestAnimationFrame(() => {
                const navBounds = adminNav.getBoundingClientRect();
                const itemBounds = activeNavItem.getBoundingClientRect();
                if (itemBounds.left < navBounds.left + 6 || itemBounds.right > navBounds.right - 6) {
                    adminNav.scrollLeft += itemBounds.left - navBounds.left - 6;
                }
            });
        }
    }

    const submitForm = form => {
        if (typeof form?.requestSubmit === 'function') {
            form.requestSubmit();
        }
        else {
            form?.submit();
        }
    };

    const filterForm = document.querySelector('[data-admreq-filters]');
    const filterGroup = filterForm?.querySelector('.admreq-selects');
    const filterRow = filterForm?.querySelector('.admreq-filter-row');
    const sortControl = filterForm?.querySelector('.admreq-sort-control');
    const filterDialog = filterForm?.querySelector('[data-admreq-filter-dialog]');
    const filterDialogContent = filterDialog?.querySelector('[data-admreq-filter-dialog-content]');
    const mobileFilterTrigger = filterForm?.querySelector('[data-admreq-open-filters]');
    if (filterGroup && filterRow && sortControl && filterDialog && filterDialogContent && mobileFilterTrigger) {
        const mobileFilters = window.matchMedia('(max-width: 820px)');
        const positionFilters = () => {
            if (mobileFilters.matches) {
                filterDialogContent.append(filterGroup);
            }
            else {
                if (filterDialog.open) filterDialog.close();
                filterRow.insertBefore(filterGroup, sortControl);
            }
        };

        document.documentElement.classList.add('admreq-js');
        positionFilters();
        mobileFilters.addEventListener('change', positionFilters);
        mobileFilterTrigger.addEventListener('click', () => {
            filterDialog.showModal();
            mobileFilterTrigger.setAttribute('aria-expanded', 'true');
            filterDialog.querySelector('[data-admreq-select-trigger]')?.focus();
        });
        filterDialog.querySelectorAll('[data-admreq-close-filters]').forEach(button => {
            button.addEventListener('click', () => filterDialog.close());
        });
        filterDialog.addEventListener('close', () => {
            mobileFilterTrigger.setAttribute('aria-expanded', 'false');
            if (mobileFilters.matches) mobileFilterTrigger.focus();
        });
        filterDialog.addEventListener('click', event => {
            if (event.target === filterDialog) filterDialog.close();
        });
    }

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
        if (!select.closest('.admreq-select-control')) {
            continue;
        }

        const control = select.closest('.admreq-select-control');
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
        const filterIcon = control.querySelector('.admreq-filter-icon');
        if (filterIcon) {
            trigger.append(filterIcon);
        }
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
    const pageSizeInput = pageSizeForm?.querySelector('[data-admreq-page-size-input]');
    const pageSizeTrigger = pageSizeForm?.querySelector('[data-admreq-page-size-trigger]');
    const pageSizePanel = pageSizeForm?.querySelector('[data-admreq-page-size-options]');
    const pageSizeOptions = [...(pageSizePanel?.querySelectorAll('[data-admreq-page-size-option]') || [])];

    pageSizeInput?.addEventListener('change', () => {
        if (pageSizeInput.value && pageSizeInput.checkValidity()) {
            submitForm(pageSizeForm);
        }
    });
    pageSizeInput?.addEventListener('keydown', event => {
        if (event.key === 'ArrowDown' && pageSizePanel && !pageSizePanel.matches(':popover-open')) {
            event.preventDefault();
            pageSizeTrigger?.click();
        }
    });
    pageSizeTrigger?.addEventListener('click', () => {
        if (!pageSizePanel) {
            return;
        }

        if (pageSizePanel.matches(':popover-open')) {
            pageSizePanel.hidePopover();
            pageSizeInput?.focus();
            return;
        }

        closeCustomSelects();
        pageSizePanel.showPopover();
        const bounds = pageSizeTrigger.closest('.admreq-page-size-combobox').getBoundingClientRect();
        const panelWidth = Math.max(pageSizePanel.offsetWidth, bounds.width);
        const panelHeight = pageSizePanel.offsetHeight;
        pageSizePanel.style.left = `${Math.max(8, Math.min(bounds.right - panelWidth, window.innerWidth - panelWidth - 8))}px`;
        const bottomInset = window.matchMedia('(max-width: 820px)').matches ? 82 : 8;
        pageSizePanel.style.top = `${bounds.bottom + panelHeight <= window.innerHeight - bottomInset ? bounds.bottom + 6 : Math.max(8, bounds.top - panelHeight - 6)}px`;
        pageSizeOptions.find(option => option.getAttribute('aria-selected') === 'true')?.focus();
    });
    pageSizePanel?.addEventListener('toggle', () => pageSizeTrigger?.setAttribute('aria-expanded', String(pageSizePanel.matches(':popover-open'))));
    window.addEventListener('resize', () => {
        if (pageSizePanel?.matches(':popover-open')) {
            pageSizePanel.hidePopover();
        }
    });
    document.addEventListener('scroll', () => {
        if (pageSizePanel?.matches(':popover-open')) {
            pageSizePanel.hidePopover();
        }
    }, true);
    pageSizeOptions.forEach((option, index) => {
        option.addEventListener('click', () => {
            pageSizeInput.value = option.dataset.admreqPageSizeOption;
            pageSizePanel.hidePopover();
            submitForm(pageSizeForm);
        });
        option.addEventListener('keydown', event => {
            if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                event.preventDefault();
                pageSizeOptions[(index + (event.key === 'ArrowDown' ? 1 : -1) + pageSizeOptions.length) % pageSizeOptions.length].focus();
            }
            else if (event.key === 'Escape') {
                pageSizePanel.hidePopover();
                pageSizeInput?.focus();
            }
        });
    });

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
