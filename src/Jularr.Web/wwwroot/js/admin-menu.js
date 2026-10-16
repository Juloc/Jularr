(() => {
    const instances = new WeakMap();
    const init = (root = document) => {
        for (const menu of root.querySelectorAll('details.admin-menu')) {
            if (instances.has(menu)) continue;
            const panel = menu.querySelector('.admin-menu-list');
            const trigger = menu.querySelector('summary');
            if (!panel || !trigger) continue;
            const events = new AbortController();
            const listen = (target, name, handler, capture = false) => target.addEventListener(name, handler, { capture, signal: events.signal });
            panel.setAttribute('popover', 'manual');
            const close = (restoreFocus = false) => {
                menu.open = false;
                if (panel.matches(':popover-open')) panel.hidePopover();
                trigger.setAttribute('aria-expanded', 'false');
                if (restoreFocus) trigger.focus();
            };
            const place = () => {
                if (panel.matches(':popover-open')) window.JularrPopover.place(panel, trigger, 8, 'end');
            };
            const open = () => {
                menu.open = true;
                document.querySelectorAll('details.admin-menu[open]').forEach(other => { if (other !== menu) other.open = false; });
                if (!panel.matches(':popover-open')) panel.showPopover();
                trigger.setAttribute('aria-expanded', 'true');
                place();
            };
            listen(menu, 'toggle', () => { if (menu.open) open(); else close(); });
            listen(document, 'click', event => { if (!menu.contains(event.target)) close(); });
            listen(document, 'focusin', event => { if (menu.open && !menu.contains(event.target)) close(); });
            listen(panel, 'click', event => { if (event.target.closest('a[href], button:not(:disabled)')) close(); });
            listen(menu, 'keydown', event => {
                if (event.key === 'Escape') { event.preventDefault(); close(true); }
                if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
                event.preventDefault();
                open();
                const items = [...panel.querySelectorAll('a[href], button:not(:disabled)')];
                const index = items.indexOf(document.activeElement);
                const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 : index < 0 ? (event.key === 'ArrowUp' ? items.length - 1 : 0) : (index + (event.key === 'ArrowUp' ? -1 : 1) + items.length) % items.length;
                items[next]?.focus();
            });
            listen(document, 'keydown', event => { if (event.key === 'Escape' && menu.open) close(true); });
            listen(document, 'scroll', place, true);
            listen(window, 'resize', place);
            trigger.setAttribute('aria-expanded', 'false');
            instances.set(menu, () => { close(); events.abort(); panel.removeAttribute('popover'); trigger.removeAttribute('aria-expanded'); instances.delete(menu); });
        }
    };
    window.JularrMenu = { init, dispose: root => { for (const menu of root.querySelectorAll('details.admin-menu')) instances.get(menu)?.(); } };
    init();
})();
