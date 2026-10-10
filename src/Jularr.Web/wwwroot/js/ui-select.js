(() => {
    const instances = new WeakMap();
    let nextId = 0;
    const init = (root = document) => {
        const customSelects = [...root.querySelectorAll('select[data-ui-select]')];
        const closeSelects = except => {
            for (const select of customSelects) {
                const control = select.closest('[data-ui-select-enhanced]');
                const trigger = control?.querySelector('[data-ui-select-trigger]');
                const panel = control?.querySelector('[data-ui-select-panel]');
                if (!control || control === except || !panel?.hasAttribute('popover')) {
                    continue;
                }

                if (panel.matches(':popover-open')) panel.hidePopover();
                trigger?.setAttribute('aria-expanded', 'false');
            }
        };

        for (const select of customSelects) {
            if (instances.has(select)) continue;
            if (!select.closest('.ui-select-control')) {
                continue;
            }

            const control = select.closest('.ui-select-control');
            const originalTabIndex = select.getAttribute('tabindex');
            const originalAriaHidden = select.getAttribute('aria-hidden');
            const events = new AbortController();
            const listen = (target, type, callback, capture = false) => target.addEventListener(type, callback, { capture, signal: events.signal });
            const trigger = document.createElement('button');
            trigger.type = 'button';
            trigger.className = 'ui-select-trigger';
            trigger.setAttribute('role', 'combobox');
            trigger.setAttribute('aria-haspopup', 'listbox');
            trigger.setAttribute('aria-expanded', 'false');
            const label = select.getAttribute('aria-label') || [...select.labels || []].map(item => item.textContent.trim()).join(' ');
            trigger.setAttribute('aria-label', label);
            trigger.dataset.uiSelectTrigger = '';
            const triggerText = document.createElement('span');
            triggerText.dataset.uiSelectValue = '';
            const chevron = document.createElement('span');
            chevron.className = 'ui-select-chevron';
            chevron.setAttribute('aria-hidden', 'true');
            const filterIcon = control.querySelector('.ui-select-icon');
            const originalIconHidden = filterIcon?.hidden;
            if (filterIcon) {
                trigger.append(filterIcon);
            }
            trigger.append(triggerText, chevron);

            const panel = document.createElement('div');
            panel.className = 'ui-select-panel';
            if (select.multiple) panel.classList.add('ui-select-multiple');
            panel.setAttribute('popover', 'auto');
            panel.id = `ui-select-${++nextId}`;
            panel.dataset.uiSelectPanel = '';
            const listbox = document.createElement('div');
            listbox.id = `${panel.id}-listbox`;
            listbox.setAttribute('role', 'listbox');
            listbox.setAttribute('aria-label', label);
            if (select.multiple) listbox.setAttribute('aria-multiselectable', 'true');
            trigger.setAttribute('aria-controls', listbox.id);
            const options = [...select.options];
            let initialValues = options.filter(option => option.selected).map(option => option.value);
            const commit = () => {
                const values = options.filter(option => option.selected).map(option => option.value);
                if (values.slice().sort().join('\0') === initialValues.slice().sort().join('\0')) return;
                initialValues = values;
                select.dispatchEvent(new CustomEvent('ui:select-commit', { bubbles: true, detail: { values } }));
            };
            const templates = [...(select.form || root).querySelectorAll('template[data-ui-select-option]')]
                .filter(template => template.dataset.uiSelectOption === select.name);
            const renderOption = (option, target) => {
                const template = templates.find(candidate => candidate.dataset.value === option?.value);
                target.replaceChildren();
                if (template) target.append(template.content.cloneNode(true));
                else target.textContent = option?.textContent.trim() || '';
                return Boolean(template);
            };
            let searchBox;
            if (options.length > 8) {
                searchBox = document.createElement('input');
                searchBox.type = 'search';
                searchBox.className = 'ui-select-search';
                searchBox.placeholder = label;
                searchBox.setAttribute('aria-label', searchBox.placeholder);
                listen(searchBox, 'input', () => {
                    const query = searchBox.value.trim().toLocaleLowerCase();
                    for (const optionButton of panel.querySelectorAll('[role="option"]')) {
                        optionButton.hidden = !optionButton.textContent.toLocaleLowerCase().includes(query);
                    }
                });
                panel.append(searchBox);
            }

            panel.append(listbox);
            const optionButtons = options.map(option => {
                const optionButton = document.createElement('button');
                optionButton.type = 'button';
                optionButton.className = 'ui-select-option';
                optionButton.setAttribute('role', 'option');
                optionButton.setAttribute('aria-label', option.textContent.trim());
                optionButton.dataset.value = option.value;
                optionButton.tabIndex = -1;
                optionButton.disabled = option.disabled;
                const optionText = document.createElement('span');
                optionText.className = 'ui-select-option-text';
                renderOption(option, optionText);
                if (select.multiple) {
                    const checkbox = document.createElement('span');
                    checkbox.className = 'ui-select-checkbox';
                    checkbox.setAttribute('aria-hidden', 'true');
                    optionButton.append(checkbox);
                }
                optionButton.append(optionText);
                listen(optionButton, 'click', () => {
                    if (select.multiple) {
                        if (!option.value) options.forEach(candidate => { candidate.selected = false; });
                        else {
                            option.selected = !option.selected;
                            options.filter(candidate => !candidate.value).forEach(candidate => { candidate.selected = false; });
                        }
                    }
                    else select.value = option.value;
                    select.dispatchEvent(new Event('change', { bubbles: true }));
                    if (select.multiple) return;
                    if (panel.matches(':popover-open')) panel.hidePopover();
                    trigger.setAttribute('aria-expanded', 'false');
                    trigger.focus();
                });
                listen(optionButton, 'keydown', event => {
                    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                        event.preventDefault();
                        const direction = event.key === 'ArrowDown' ? 1 : -1;
                        const visible = optionButtons.filter(button => !button.hidden && !button.disabled);
                        const current = visible.indexOf(optionButton);
                        visible[(current + direction + visible.length) % visible.length]?.focus();
                    }
                    else if (event.key === 'Home' || event.key === 'End') {
                        event.preventDefault();
                        const visible = optionButtons.filter(button => !button.hidden && !button.disabled);
                        (event.key === 'Home' ? visible[0] : visible.at(-1))?.focus();
                    }
                });
                listbox.append(optionButton);
                return optionButton;
            });
            if (searchBox) listen(searchBox, 'keydown', event => {
                if (!['ArrowDown', 'ArrowUp'].includes(event.key)) return;
                event.preventDefault();
                const visible = optionButtons.filter(button => !button.hidden && !button.disabled);
                (event.key === 'ArrowUp' ? visible.at(-1) : visible[0])?.focus();
            });

            const syncSelect = () => {
                trigger.disabled = select.matches(':disabled');
                for (const attribute of ['aria-describedby', 'aria-invalid']) {
                    const value = select.getAttribute(attribute);
                    if (value) trigger.setAttribute(attribute, value);
                    else trigger.removeAttribute(attribute);
                }
                trigger.setAttribute('aria-required', String(select.required));
                const selectedOptions = [...select.selectedOptions].filter(option => option.value);
                const selectedOption = selectedOptions[0] || options[0];
                const rich = renderOption(selectedOption, triggerText);
                if (select.multiple && selectedOptions.length > 1) {
                    const count = document.createElement('span');
                    count.className = 'ui-select-extra';
                    count.textContent = `+${selectedOptions.length - 1}`;
                    triggerText.append(count);
                }
                trigger.title = selectedOptions.map(option => option.textContent.trim()).join(', ');
                trigger.classList.toggle('ui-select-rich', rich);
                if (filterIcon) filterIcon.hidden = rich;
                for (const optionButton of optionButtons) {
                    const selected = select.multiple
                        ? selectedOptions.some(option => option.value === optionButton.dataset.value) || (!selectedOptions.length && !optionButton.dataset.value)
                        : optionButton.dataset.value === select.value;
                    optionButton.setAttribute('aria-selected', String(selected));
                    optionButton.classList.toggle('is-selected', selected);
                }
            };
            if (select.multiple) {
                const footer = document.createElement('div');
                footer.className = 'ui-select-foot';
                const cancel = document.createElement('button');
                cancel.type = 'button';
                cancel.className = 'button button-secondary';
                cancel.textContent = select.form?.dataset.cancelLabel || control.dataset.cancelLabel;
                listen(cancel, 'click', () => {
                    options.forEach(option => { option.selected = initialValues.includes(option.value); });
                    select.dispatchEvent(new Event('change', { bubbles: true }));
                    panel.hidePopover();
                    trigger.focus();
                });
                const apply = document.createElement('button');
                apply.type = 'button';
                apply.className = 'button button-primary';
                apply.textContent = select.form?.dataset.applyLabel || control.dataset.applyLabel;
                listen(apply, 'click', () => {
                    panel.hidePopover();
                    trigger.focus();
                    commit();
                });
                footer.append(cancel, apply);
                panel.append(footer);
            }
            const positionPanel = () => {
                if (!panel.matches(':popover-open')) return;
                window.JularrPopover.place(panel, trigger);
            };
            listen(trigger, 'click', () => {
                if (select.matches(':disabled')) return;
                const isOpen = trigger.getAttribute('aria-expanded') === 'true';
                closeSelects(control);
                if (isOpen) {
                    panel.hidePopover();
                    trigger.setAttribute('aria-expanded', 'false');
                    return;
                }

                initialValues = options.filter(option => option.selected).map(option => option.value);
                panel.showPopover();
                trigger.setAttribute('aria-expanded', 'true');
                positionPanel();
                const selectedIndex = options.findIndex(option => option.value === select.value);
                const selectedButton = optionButtons[selectedIndex];
                const focusTarget = selectedButton && !selectedButton.disabled && !selectedButton.hidden ? selectedButton : optionButtons.find(button => !button.disabled && !button.hidden);
                (searchBox || focusTarget)?.focus();
            });
            listen(document, 'scroll', event => {
                if (!event.target.closest?.('[data-ui-select-panel]')) positionPanel();
            }, true);
            listen(trigger, 'keydown', event => {
                if (event.key === 'ArrowDown' || event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault();
                    trigger.click();
                }
            });
            listen(panel, 'keydown', event => {
                if (event.key === 'Escape') {
                    event.preventDefault();
                    panel.hidePopover();
                    trigger.focus();
                }
            });
            listen(panel, 'toggle', () => {
                const open = panel.matches(':popover-open');
                trigger.setAttribute('aria-expanded', String(open));
                if (select.multiple && !open) commit();
            });
            listen(select, 'change', syncSelect);
            const disabledObserver = new MutationObserver(syncSelect);
            disabledObserver.observe(select, { attributes: true, attributeFilter: ['disabled', 'required', 'aria-describedby', 'aria-invalid'] });
            for (const fieldset of [...control.closest('form')?.querySelectorAll('fieldset') || []].filter(fieldset => fieldset.contains(control))) {
                disabledObserver.observe(fieldset, { attributes: true, attributeFilter: ['disabled'] });
            }
            if (select.form) listen(select.form, 'reset', () => queueMicrotask(syncSelect));
            select.tabIndex = -1;
            select.setAttribute('aria-hidden', 'true');
            control.dataset.uiSelectEnhanced = '';
            control.append(trigger, panel);
            syncSelect();
            instances.set(select, () => {
                events.abort();
                disabledObserver.disconnect();
                panel.remove();
                trigger.remove();
                if (filterIcon) {
                    filterIcon.hidden = originalIconHidden;
                    control.prepend(filterIcon);
                }
                if (originalAriaHidden === null || originalAriaHidden === undefined) select.removeAttribute('aria-hidden');
                else select.setAttribute('aria-hidden', originalAriaHidden);
                if (originalTabIndex === null || originalTabIndex === undefined) select.removeAttribute('tabindex');
                else select.setAttribute('tabindex', originalTabIndex);
                delete control.dataset.uiSelectEnhanced;
                instances.delete(select);
            });
            listen(document, 'click', event => {
                if (!event.target.closest('[data-ui-select-enhanced], [data-ui-select-panel]')) {
                    if (panel.matches(':popover-open')) panel.hidePopover();
                }
            });
            listen(document, 'focusin', event => {
                if (panel.matches(':popover-open') && !control.contains(event.target)) panel.hidePopover();
            });
            listen(document, 'keydown', event => {
                if (event.key === 'Escape' && panel.matches(':popover-open')) {
                    panel.hidePopover();
                    trigger.focus();
                }
            });
            listen(window, 'resize', positionPanel);

        }

    };
    const dispose = root => {
        for (const select of root.querySelectorAll('select[data-ui-select]')) instances.get(select)?.();
    };
    window.JularrSelect = { init, dispose };
    init();
})();
