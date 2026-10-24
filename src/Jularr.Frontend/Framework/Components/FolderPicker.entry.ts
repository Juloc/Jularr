import "./FolderPicker.scss";
import { onPage } from "../../Modules/Navigation/PageSwap";
import { markTruncated } from "./Truncated";

// The chosen folder is one cut-off line; its full path shows in the tooltip of the shared Truncated text.
function showSelection(selection: HTMLElement, text: string): void {
    selection.querySelectorAll<HTMLElement>('.truncated__text, .truncated__tip').forEach(part => { part.textContent = text; });
    markTruncated(selection);
}

onPage(import.meta.url, () => document.querySelectorAll<HTMLElement>('[data-folder-picker]').forEach(picker => {
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
            if (selection) showSelection(selection, '/');
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
            markTruncated(replacement);
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

    // A folder the installation offers becomes a connection (and is looked at) before its folders are listed.
    async function connect(link: HTMLAnchorElement): Promise<void> {
        const token = picker.closest('form')?.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]')?.value ?? '';
        const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
        content!.inert = true;
        try {
            const created = await fetch('/api/v1/admin/storage/mounts', { method: 'POST', credentials: 'same-origin', headers,
                body: JSON.stringify({ name: link.dataset.folderConnectName, storageProtocolTypeId: 3, host: null, sharePath: link.dataset.folderConnect, protocolVersion: null,
                    username: null, domain: null, isReadOnly: false, password: null }) });
            if (!created.ok) throw new Error('Connecting failed.');
            const id = String((await created.json()).id);
            await fetch(`/api/v1/admin/storage/mounts/${id}/check`, { method: 'POST', credentials: 'same-origin', headers, body: '{}' });
            const url = new URL(link.href, location.href);
            url.searchParams.set('storageMountId', id);
            content!.inert = false;
            await load(url.href);
        } catch {
            content!.inert = false;
            status!.hidden = false;
            status!.setAttribute('role', 'alert');
            status!.textContent = picker.dataset.folderError ?? '';
        }
    }

    async function createFolder(button: HTMLElement): Promise<void> {
        const root = button.closest<HTMLElement>('[data-folder-fragment]');
        const input = root?.querySelector<HTMLInputElement>('[data-folder-new-name]');
        const name = input?.value.trim() ?? '';
        if (!name || name === '.' || name === '..' || name.includes('/') || name.includes('\\')) {
            input?.focus();
            return;
        }
        const url = new URL(currentUrl, location.href);
        const mountId = url.searchParams.get('storageMountId');
        if (!mountId || !/^\d+$/.test(mountId)) return;
        const relativePath = url.searchParams.get('path') ?? '';
        const token = picker.closest('form')?.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]')?.value ?? '';
        content!.inert = true;
        status!.hidden = false;
        status!.setAttribute('role', 'status');
        status!.textContent = picker.dataset.folderLoading ?? '';
        try {
            const response = await fetch(`/api/v1/admin/storage/mounts/${mountId}/download-directory`, {
                method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: token },
                body: JSON.stringify({ relativePath, directoryName: name })
            });
            if (!response.ok) throw new Error('Folder creation failed.');
            url.searchParams.set('path', relativePath ? relativePath + '/' + name : name);
            url.searchParams.delete('afterName');
            content!.inert = false;
            await load(url.href);
        } catch {
            status!.hidden = false;
            status!.setAttribute('role', 'alert');
            status!.textContent = picker.dataset.folderError ?? '';
        } finally {
            content!.inert = false;
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
        const offered = event.target.closest<HTMLAnchorElement>('[data-folder-connect]');
        if (offered) {
            event.preventDefault();
            void connect(offered);
            return;
        }
        const link = event.target.closest<HTMLAnchorElement>('[data-folder-navigate]');
        if (link) {
            event.preventDefault();
            void load(link.href);
            return;
        }
        const create = event.target.closest<HTMLElement>('[data-folder-create]');
        if (create && !content.inert) {
            event.preventDefault();
            void createFolder(create);
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
            if (selection) showSelection(selection, choose.dataset.folderLabel ?? '/' + field.value);
            dialog.close();
        }
    });
    dialog.addEventListener('close', () => {
        pending?.abort();
        opener.focus({ preventScroll: true });
    });
}));
