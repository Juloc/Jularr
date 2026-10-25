export function initializeRemoteBrowser(): void {
    document.querySelectorAll<HTMLElement>('[data-remote-browser]').forEach(root => {
        if (root.dataset.initialized) return;
        root.dataset.initialized = 'true';
        const image = root.querySelector<HTMLImageElement>('[data-browser-image]');
        const feedback = root.querySelector<HTMLElement>('[data-browser-feedback]');
        const text = root.querySelector<HTMLTextAreaElement>('[data-browser-text]');
        if (!image || !feedback || !text) return;
        const api = root.dataset.remoteBrowser!;
        const width = Number(root.dataset.width);
        const height = Number(root.dataset.height);
        const controller = new AbortController();
        let stopped = false;
        let picture: string | undefined;
        let inputs = Promise.resolve();
        let dragging = false;
        let movedAt = 0;

        const fail = () => {
            stopped = true;
            controller.abort();
            feedback.textContent = root.dataset.failed ?? '';
            image.removeAttribute('src');
            if (picture) URL.revokeObjectURL(picture);
        };
        const send = (input: object) => {
            if (stopped) return;
            inputs = inputs.then(async () => {
                const response = await fetch(`${api}/input`, {
                    method: 'POST', cache: 'no-store', credentials: 'same-origin', signal: controller.signal,
                    headers: { 'Content-Type': 'application/json', RequestVerificationToken: root.dataset.csrf ?? '' }, body: JSON.stringify(input)
                });
                if (!response.ok) throw new Error('Browser input failed');
            }).catch(fail);
        };
        const point = (event: PointerEvent | WheelEvent) => {
            const box = image.getBoundingClientRect();
            return { x: Math.max(0, Math.min(width - 1, Math.floor((event.clientX - box.left) * width / box.width))),
                y: Math.max(0, Math.min(height - 1, Math.floor((event.clientY - box.top) * height / box.height))) };
        };

        image.addEventListener('pointerdown', event => {
            if (event.button !== 0) return;
            event.preventDefault();
            image.focus({ preventScroll: true });
            dragging = true;
            image.setPointerCapture(event.pointerId);
            send({ action: 'down', ...point(event) });
        }, { signal: controller.signal });
        image.addEventListener('pointermove', event => {
            if (!dragging || performance.now() - movedAt < 80) return;
            movedAt = performance.now();
            send({ action: 'move', ...point(event) });
        }, { signal: controller.signal });
        const release = (event: PointerEvent) => {
            if (!dragging) return;
            dragging = false;
            send({ action: 'up', ...point(event) });
        };
        image.addEventListener('pointerup', release, { signal: controller.signal });
        image.addEventListener('pointercancel', release, { signal: controller.signal });
        image.addEventListener('wheel', event => {
            event.preventDefault();
            send({ action: 'wheel', ...point(event), delta: Math.max(-2000, Math.min(2000, Math.round(event.deltaY))) });
        }, { passive: false, signal: controller.signal });
        image.addEventListener('keydown', event => {
            if (event.ctrlKey || event.metaKey || event.altKey || event.isComposing) return;
            event.preventDefault();
            if (event.key.length === 1) send({ action: 'text', text: event.key });
            else if (['Enter', 'Tab', 'Backspace', 'Delete', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Escape', 'Home', 'End'].includes(event.key)) {
                send({ action: 'key', key: event.key });
            }
        }, { signal: controller.signal });
        root.querySelector('[data-browser-send]')?.addEventListener('click', () => {
            if (!text.value) return;
            send({ action: 'text', text: text.value });
            text.value = '';
            image.focus({ preventScroll: true });
        }, { signal: controller.signal });
        root.querySelectorAll<HTMLElement>('[data-browser-key]').forEach(button => button.addEventListener('click', () => {
            send({ action: 'key', key: button.dataset.browserKey });
            image.focus({ preventScroll: true });
        }, { signal: controller.signal }));
        root.querySelectorAll<HTMLElement>('[data-browser-wheel]').forEach(button => button.addEventListener('click', () => {
            send({ action: 'wheel', x: Math.floor(width / 2), y: Math.floor(height / 2), delta: Number(button.dataset.browserWheel) });
        }, { signal: controller.signal }));

        const frames = async () => {
            while (!stopped && root.isConnected) {
                if (!document.hidden) {
                    try {
                        const response = await fetch(`${api}/frame`, { cache: 'no-store', credentials: 'same-origin', signal: controller.signal });
                        if (!response.ok) throw new Error('Browser frame failed');
                        const next = URL.createObjectURL(await response.blob());
                        const previous = picture;
                        picture = next;
                        image.src = next;
                        if (previous) URL.revokeObjectURL(previous);
                        feedback.textContent = root.dataset.ready ?? '';
                    } catch {
                        if (root.isConnected) fail();
                        break;
                    }
                }
                await new Promise(resolve => setTimeout(resolve, 500));
            }
            controller.abort();
            if (picture) URL.revokeObjectURL(picture);
        };
        window.addEventListener('pagehide', () => controller.abort(), { once: true, signal: controller.signal });
        void frames();
    });
}
