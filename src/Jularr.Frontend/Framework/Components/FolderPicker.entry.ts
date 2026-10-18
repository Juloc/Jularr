import "./FolderPicker.scss";

document.querySelectorAll<HTMLElement>('[data-folder-picker]').forEach(picker => {
    if (picker.dataset.folderReady) return;
    const dialog = picker.querySelector<HTMLDialogElement>('dialog');
    const content = picker.querySelector<HTMLElement>('[data-folder-content]');
    const status = picker.querySelector<HTMLElement>('[data-folder-status]');
    const opener = picker.querySelector<HTMLAnchorElement>('[data-folder-open]');
    const field = picker.closest('form')?.querySelector<HTMLInputElement>(`input[name="${CSS.escape(picker.dataset.folderField ?? '')}"]`);
    if (!dialog || !content || !status || !opener || !field) return;
    picker.dataset.folderReady = 'true';
    let pending: AbortController | undefined;
    let currentUrl = opener.dataset.folderUrl ?? '';
    const mount = picker.dataset.folderMountField
        ? picker.closest('form')?.querySelector<HTMLSelectElement | HTMLInputElement>(`[name="${CSS.escape(picker.dataset.folderMountField)}"]`) : null;
    if (mount && picker.dataset.folderChoosesStorage !== 'true') {
        picker.hidden = !mount.value;
        mount.addEventListener('change', () => {
            pending?.abort();
            if (dialog.open) dialog.close();
            picker.hidden = !mount.value;
            field.value = '';
            const selection = picker.querySelector<HTMLElement>('[data-folder-selection]');
            if (selection) selection.textContent = '/';
            const url = new URL(opener.dataset.folderUrl ?? '', location.href);
            url.searchParams.set('storageMountId', mount.value);
            url.searchParams.set('path', '');
            currentUrl = url.href;
        });
    }

    async function load(address: string): Promise<void> {
        const url = new URL(address, location.href);
        if (url.origin !== location.origin) return;
        pending?.abort();
        const controller = pending = new AbortController();
        status!.hidden = false;
        status!.setAttribute('role', 'status');
        status!.textContent = picker.dataset.folderLoading ?? '';
        content!.setAttribute('aria-busy', 'true');
        content!.inert = true;
        let failure = picker.dataset.folderError ?? '';
        try {
            const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'text/html' },
                signal: AbortSignal.any([controller.signal, AbortSignal.timeout(20_000)]) });
            if (!response.headers.get('Content-Type')?.includes('text/html')) throw new Error('Expected folder HTML.');
            const page = new DOMParser().parseFromString(await response.text(), 'text/html');
            const replacement = page.querySelector<HTMLElement>('[data-folder-fragment]');
            if (!response.ok || !replacement) {
                failure = page.querySelector('.ui-page-error')?.textContent?.trim() || failure;
                throw new Error('Folder read failed.');
            }
            if (controller.signal.aborted || !dialog!.open) return;
            content!.replaceChildren(replacement);
            currentUrl = url.href;
            status!.hidden = true;
        } catch {
            if (!controller.signal.aborted) {
                status!.setAttribute('role', 'alert');
                status!.textContent = failure;
            }
        } finally {
            if (!controller.signal.aborted) {
                content!.removeAttribute('aria-busy');
                content!.inert = false;
                if (status!.hidden && dialog!.open) content!.querySelector<HTMLElement>('[data-folder-choose]')?.focus({ preventScroll: true });
            }
        }
    }

    // Opening and folder navigation preserve the surrounding form draft.
    opener.addEventListener('click', event => {
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        dialog.showModal();
        void load(currentUrl);
    });
    dialog.addEventListener('click', event => {
        if (!(event.target instanceof Element)) return;
        if (event.target.closest('[data-folder-close]')) {
            dialog.close();
            return;
        }
        const link = event.target.closest<HTMLAnchorElement>('[data-folder-navigate]');
        if (link) {
            event.preventDefault();
            void load(link.href);
            return;
        }
        const choose = event.target.closest<HTMLElement>('[data-folder-choose]');
        if (choose && !content.inert) {
            field.value = choose.dataset.folderChoose ?? '';
            if (mount && picker.dataset.folderChoosesStorage === 'true') {
                mount.value = choose.dataset.folderMountId ?? '';
                mount.dispatchEvent(new Event('change', { bubbles: true }));
            }
            field.dispatchEvent(new Event('change', { bubbles: true }));
            const selection = picker.querySelector<HTMLElement>('[data-folder-selection]');
            if (selection) selection.textContent = choose.dataset.folderLabel ?? '/' + field.value;
            dialog.close();
        }
    });
    dialog.addEventListener('close', () => {
        pending?.abort();
        opener.focus({ preventScroll: true });
    });
});
