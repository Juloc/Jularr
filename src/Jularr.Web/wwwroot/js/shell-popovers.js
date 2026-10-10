(() => {
    const triggers = [...document.querySelectorAll('.app-header [popovertarget]')];
    const panels = triggers.map(button => document.getElementById(button.getAttribute('popovertarget'))).filter(Boolean);
    const requests = new WeakMap();

    const position = (panel, trigger) => {
        const box = trigger.getBoundingClientRect();
        const viewportWidth = document.documentElement.clientWidth;
        const viewportHeight = document.documentElement.clientHeight;
        panel.style.maxWidth = Math.max(0, viewportWidth - 16) + 'px';
        const width = panel.offsetWidth;
        panel.style.inset = 'auto';
        panel.style.left = Math.max(8, Math.min(box.right - width, viewportWidth - width - 8)) + 'px';
        panel.style.top = Math.max(8, Math.min(box.bottom + 8, viewportHeight - panel.offsetHeight - 8)) + 'px';
    };

    const update = (panel, html) => {
        panel.querySelector('[data-preview-content]').innerHTML = html;
        panel.querySelectorAll('[data-local-time]').forEach(time => {
            const date = new Date(time.dateTime);
            if (!Number.isNaN(date.getTime())) time.textContent = date.toLocaleString(document.documentElement.lang);
        });
        const count = panel.querySelector('[data-preview-unread]');
        if (count) {
            const button = triggers.find(trigger => trigger.getAttribute('popovertarget') === panel.id);
            let badge = button.querySelector('.notification-bell-badge');
            const unread = Number(count.dataset.previewUnread);
            button.setAttribute('aria-label', count.dataset.previewLabel);
            if (unread > 0) {
                if (!badge) {
                    badge = document.createElement('span');
                    badge.className = 'notification-bell-badge';
                    badge.setAttribute('aria-hidden', 'true');
                    button.append(badge);
                }
                badge.textContent = unread > 99 ? '99+' : String(unread);
            } else badge?.remove();
        }
    };

    const load = async (panel, form) => {
        requests.get(panel)?.abort();
        const controller = new AbortController();
        requests.set(panel, controller);
        const timeout = window.setTimeout(() => controller.abort(), 15000);
        const loading = panel.querySelector('[data-preview-loading]');
        const error = panel.querySelector('[data-preview-error]');
        loading.hidden = false;
        error.hidden = true;
        panel.setAttribute('aria-busy', 'true');
        try {
            const response = await fetch(form ? form.action : panel.dataset.previewUrl, {
                method: form ? 'POST' : 'GET',
                body: form ? new FormData(form) : undefined,
                credentials: 'same-origin',
                signal: controller.signal
            });
            if (!response.ok || response.redirected) throw new Error('Preview unavailable');
            const html = await response.text();
            if (requests.get(panel) === controller) update(panel, html);
        } catch (failure) {
            if (requests.get(panel) === controller && panel.matches(':popover-open')) error.hidden = false;
        } finally {
            window.clearTimeout(timeout);
            if (requests.get(panel) === controller) {
                loading.hidden = true;
                panel.removeAttribute('aria-busy');
                position(panel, triggers.find(button => button.getAttribute('popovertarget') === panel.id));
            }
        }
    };

    panels.forEach(panel => {
        const trigger = triggers.find(button => button.getAttribute('popovertarget') === panel.id);
        panel.addEventListener('beforetoggle', event => {
            if (event.newState === 'open') panels.filter(other => other !== panel && other.matches(':popover-open')).forEach(other => other.hidePopover());
        });
        panel.addEventListener('toggle', event => {
            const open = event.newState === 'open';
            trigger.setAttribute('aria-expanded', String(open));
            if (open) {
                position(panel, trigger);
                if (panel.hasAttribute('data-header-preview')) load(panel);
            } else {
                requests.get(panel)?.abort();
                requests.delete(panel);
                if (document.activeElement === document.body || panel.contains(document.activeElement)) trigger.focus({ preventScroll: true });
            }
        });
        panel.addEventListener('submit', event => {
            if (!event.target.matches('[data-preview-mark-read]')) return;
            event.preventDefault();
            const submit = event.target.querySelector('button');
            submit.disabled = true;
            load(panel, event.target).finally(() => { if (submit.isConnected) submit.disabled = false; });
        });
        panel.addEventListener('jularr:header-preview-updated', () => {
            if (panel.matches(':popover-open')) position(panel, trigger);
        });
    });
    window.addEventListener('resize', () => panels.filter(panel => panel.matches(':popover-open')).forEach(panel => position(panel, triggers.find(button => button.getAttribute('popovertarget') === panel.id))));
})();
