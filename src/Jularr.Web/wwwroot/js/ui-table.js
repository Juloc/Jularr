(() => {
    const instances = new WeakMap();
    const init = (element, changed = () => {}) => {
        instances.get(element)?.dispose();
        const events = new AbortController();
        const inputs = [...element.querySelectorAll('[data-ui-select-row]')];
        const all = element.querySelector('[data-ui-select-all]');
        const clear = element.querySelector('[data-ui-selection-clear]');
        let lastSelectedIndex = -1;
        let extendsSelection = false;
        const sync = () => {
            const selected = inputs.filter(input => input.checked);
            for (const input of inputs) {
                const row = input.closest('[data-ui-row]');
                row?.classList.toggle('is-selected', input.checked);
                row?.setAttribute('aria-selected', String(input.checked));
            }
            if (all) {
                all.checked = inputs.length > 0 && selected.length === inputs.length;
                all.indeterminate = selected.length > 0 && selected.length < inputs.length;
            }
            changed(selected);
        };
        for (const [index, input] of inputs.entries()) {
            input.addEventListener('click', event => {
                extendsSelection = event.shiftKey;
            }, { signal: events.signal });
            input.addEventListener('keydown', event => {
                extendsSelection = event.shiftKey;
            }, { signal: events.signal });
            input.addEventListener('change', () => {
                const nextState = input.checked;
                if (extendsSelection && lastSelectedIndex >= 0) {
                    const first = Math.min(lastSelectedIndex, index);
                    const last = Math.max(lastSelectedIndex, index);
                    for (let selectedIndex = first; selectedIndex <= last; selectedIndex += 1) {
                        inputs[selectedIndex].checked = nextState;
                    }
                }

                extendsSelection = false;
                lastSelectedIndex = index;
                sync();
            }, { signal: events.signal });
        }

        all?.addEventListener('change', () => {
            for (const input of inputs) {
                input.checked = all.checked;
            }

            sync();
        }, { signal: events.signal });

        clear?.addEventListener('click', () => {
            for (const input of inputs) {
                input.checked = false;
            }

            sync();
            inputs[0]?.focus();
        }, { signal: events.signal });

        element.addEventListener('click', event => {
            if (event.target.closest('a, button, input, select, summary, label')) {
                return;
            }

            const row = event.target.closest('[data-ui-row]');
            const input = row?.querySelector('[data-ui-select-row]');
            if (!input) {
                return;
            }

            input.checked = !input.checked;
            extendsSelection = event.shiftKey;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }, { signal: events.signal });

        element.addEventListener('keydown', event => {
            if (event.key !== ' ' && event.key !== 'Enter') {
                return;
            }

            const row = event.target.closest('[data-ui-row]');
            const input = row?.querySelector('[data-ui-select-row]');
            if (!input || event.target !== row) {
                return;
            }

            event.preventDefault();
            input.checked = !input.checked;
            extendsSelection = event.shiftKey;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }, { signal: events.signal });


        sync();
        const instance = { clear: () => { inputs.forEach(input => { input.checked = false; }); sync(); inputs[0]?.focus?.(); }, dispose: () => { events.abort(); instances.delete(element); } };
        instances.set(element, instance);
        return instance;
    };
    window.JularrTableSelection = { init, dispose: element => instances.get(element)?.dispose() };
})();
