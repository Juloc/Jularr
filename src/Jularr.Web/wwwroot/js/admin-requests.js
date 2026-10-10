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
    const selectionValue = select => [...select.selectedOptions].map(option => option.value).filter(Boolean).sort().join('\u0000');
    const initialFilters = new Map([...(filterForm?.querySelectorAll('select[multiple]') || [])].map(select => [select, selectionValue(select)]));
    let filtersSubmitting = false;
    const applyChangedFilters = () => {
        if (filtersSubmitting || ![...initialFilters].some(([select, initial]) => selectionValue(select) !== initial)) return;
        filtersSubmitting = true;
        submitForm(filterForm);
    };
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
            filterDialog.querySelector('[data-ui-select-trigger]')?.focus();
        });
        filterDialog.querySelectorAll('[data-admreq-close-filters]').forEach(button => {
            button.addEventListener('click', () => filterDialog.close());
        });
        filterDialog.addEventListener('close', () => {
            closeCustomSelects();
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
        select.addEventListener('change', () => {
            if (!select.multiple || select.closest('[data-ui-select-enhanced]')?.dataset.uiSelectEnhanced === undefined) submitForm(filterForm);
        });
    });

    const closeCustomSelects = () => filterForm?.querySelectorAll('[data-ui-select-panel]').forEach(panel => {
        if (panel.matches(':popover-open')) panel.hidePopover();
    });
    filterForm?.addEventListener('ui:select-commit', applyChangedFilters);

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
        if (event.key === 'Enter') {
            event.preventDefault();
            if (pageSizeInput.value && pageSizeInput.checkValidity()) submitForm(pageSizeForm);
        }
        else if (event.key === 'ArrowDown' && pageSizePanel && !pageSizePanel.matches(':popover-open')) {
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
        const bottomInset = window.matchMedia('(max-width: 820px)').matches ? 82 : 8;
        window.JularrPopover.place(pageSizePanel, pageSizeTrigger.closest('.admreq-page-size-combobox'), bottomInset);
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

    const selection = [...table.querySelectorAll('[data-ui-select-row]')];
    const bulk = document.querySelector('[data-admreq-bulk]');
    const selectedCount = document.querySelector('[data-admreq-selected-count]');
    const clearSelection = document.querySelector('[data-admreq-clear-selection]');
    const commandDialog = document.querySelector('[data-admreq-command-dialog]');
    const commandForm = commandDialog?.querySelector('[data-admreq-command-form]');
    let commandTrigger;
    const openCommand = (button, action, ids) => {
        if (!commandDialog || !ids.length || ids.length > 100) return;
        document.querySelectorAll('.admin-menu[open]').forEach(menu => { menu.open = false; });
        commandTrigger = button;
        commandForm.reset();
        commandForm.querySelector('[data-admreq-command-action]').value = action;
        const targets = commandForm.querySelector('[data-admreq-command-ids]');
        targets.replaceChildren(...ids.map(id => {
            const input = document.createElement('input');
            input.type = 'hidden';
            input.name = 'ids';
            input.value = id;
            return input;
        }));
        const label = button.textContent.trim();
        commandForm.querySelector('[data-admreq-command-title]').textContent = label;
        commandForm.querySelector('[data-admreq-command-label]').textContent = label;
        commandForm.querySelector('[data-admreq-command-count]').textContent = String(ids.length);
        commandForm.querySelector('[data-admreq-delete-warning]').hidden = action !== 'Delete';
        commandForm.querySelector('[data-admreq-command-note]').hidden = action !== 'Reject';
        const profile = commandForm.querySelector('[data-admreq-command-profile]');
        profile.hidden = action !== 'Profile' && action !== 'Approve';
        profile.querySelector('select').disabled = profile.hidden;
        profile.querySelector('select').required = action === 'Profile';
        commandForm.querySelector('[data-admreq-command-submit]').disabled = action === 'Profile' && !profile.querySelector('select').value;
        commandDialog.showModal();
    };
    document.addEventListener('click', event => {
        const button = event.target.closest('button[data-admreq-command]');
        if (!button) return;
        event.preventDefault();
        const row = button.closest('[data-ui-row]');
        openCommand(button, button.dataset.admreqCommand, row ? [row.dataset.requestId] : selection.filter(input => input.checked).map(input => input.value));
    });
    table.addEventListener('submit', event => {
        const button = event.submitter;
        if (!button?.matches('[data-admreq-action]')) return;
        event.preventDefault();
        const row = button.closest('[data-ui-row]');
        openCommand(button, button.dataset.admreqAction === 'retry' ? 'Retry' : 'Approve', [row.dataset.requestId]);
    });
    commandDialog?.querySelectorAll('[data-admreq-command-close]').forEach(button => {
        button.addEventListener('click', () => commandDialog.close());
    });
    commandDialog?.addEventListener('click', event => {
        if (event.target === commandDialog) commandDialog.close();
    });
    commandDialog?.addEventListener('close', () => commandTrigger?.focus());
    commandForm?.addEventListener('submit', () => {
        commandForm.querySelector('[data-admreq-command-submit]').disabled = true;
        commandForm.setAttribute('aria-busy', 'true');
    });
    commandForm?.querySelector('select[name="profileId"]')?.addEventListener('change', event => {
        if (commandForm.querySelector('[data-admreq-command-action]').value === 'Profile') {
            commandForm.querySelector('[data-admreq-command-submit]').disabled = !event.target.value;
        }
    });
    const syncSelection = () => {
        const selected = selection.filter(input => input.checked);
        if (selectedCount) {
            selectedCount.textContent = String(selected.length);
        }

        if (bulk) {
            bulk.hidden = selected.length === 0;
            bulk.querySelectorAll('[data-admreq-command]').forEach(button => {
                const action = button.dataset.admreqCommand;
                const eligible = selected.some(input => {
                    const row = input.closest('[data-ui-row]');
                    const status = row.dataset.requestStatus;
                    if (['Approve', 'Reject', 'Cancel'].includes(action)) return status === 'pending';
                    if (action === 'Retry') return ['approved', 'failed'].includes(status);
                    if (action === 'Complete' || action === 'Profile') return ['pending', 'approved', 'failed'].includes(status) && (action !== 'Profile' || !row.dataset.requestOperation);
                    return !['searching', 'downloading', 'importing'].includes(status);
                });
                button.disabled = selected.length > 100 || !eligible;
            });
            document.body.classList.toggle('has-admreq-selection', selected.length > 0);
        }
    };

    const tableSelection = window.JularrTableSelection.init(table, syncSelection);
    clearSelection?.addEventListener('click', () => tableSelection.clear());
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
