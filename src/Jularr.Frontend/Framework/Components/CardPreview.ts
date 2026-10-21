// The delayed hover preview of a card: after a few seconds of the mouse resting on the cover (never on a button) the card opens a panel over itself with the
// title's description or, where it has one, its trailer: the one official embed Jularr frames, muted, lazily loaded, wide and cropped so that none of the
// provider's own chrome shows, with Jularr's own play/pause and sound controls. Touch devices never start it and nothing is requested before the delay.

export const previewDelay = 1800;
const settleDelay = 150;
const trailerTimeout = 7000;

// Parts of a card that are controls of their own: the hover over them never counts.
const controlsOfCard = 'button, a.button, form, input, .media-card__actions, .media-card__bookmark, .media-availability-badge, .media-card__preview';

export function isCoverTarget(target: Element | null): boolean {
    return !!target?.closest('.media-card__surface') && !target.closest(controlsOfCard);
}

export function isVideoKey(key: string | undefined): key is string {
    return !!key && /^[A-Za-z0-9_-]{11}$/.test(key);
}

// The official embed without the provider's controls; the commands below drive it.
export function trailerAddress(key: string, origin: string): string {
    return `https://www.youtube-nocookie.com/embed/${key}?autoplay=1&mute=1&controls=0&disablekb=1&fs=0&iv_load_policy=3&modestbranding=1&playsinline=1&rel=0&loop=1&playlist=${key}&enablejsapi=1&origin=${encodeURIComponent(origin)}`;
}

export function trailerCommand(func: 'playVideo' | 'pauseVideo' | 'mute' | 'unMute'): string {
    return JSON.stringify({ event: 'command', func, args: [] });
}

export interface PreviewEnvironment {
    document: Document;
    window: Window;
    fetch(address: string, init: { signal: AbortSignal; headers: Record<string, string> }): Promise<{ ok: boolean; status: number; text(): Promise<string> }>;
    setTimeout(action: () => void, milliseconds: number): unknown;
    clearTimeout(handle: unknown): void;
    canHover(): boolean;
    parse(html: string): HTMLElement | null;
    origin: string;
}

export function attachCardPreview(env: PreviewEnvironment): () => void {
    let timer: unknown;
    let closing: unknown;
    let fallback: unknown;
    let current: { card: HTMLElement; panel?: HTMLElement; request: AbortController } | undefined;
    const answers = new Map<string, string>();

    const cancelTimers = (): void => {
        env.clearTimeout(timer);
        env.clearTimeout(closing);
        env.clearTimeout(fallback);
    };

    // Nothing stays loaded: the panel and its embed leave with it.
    const stop = (): void => {
        cancelTimers();
        if (!current) return;
        current.request.abort();
        const panel = current.panel;
        current = undefined;
        if (panel) {
            if (panel.matches(':popover-open')) panel.hidePopover();
            panel.remove();
        }
    };

    const frameOf = (panel: HTMLElement): HTMLIFrameElement | null => panel.querySelector<HTMLIFrameElement>('iframe');

    function showDescription(panel: HTMLElement): void {
        panel.querySelector('[data-card-trailer]')?.replaceChildren();
        panel.dataset.previewMode = 'description';
    }

    function startTrailer(panel: HTMLElement): void {
        const key = panel.dataset.trailerId;
        const holder = panel.querySelector<HTMLElement>('[data-card-trailer]');
        if (!holder || !isVideoKey(key)) return;
        const frame = env.document.createElement('iframe');
        frame.title = panel.dataset.trailerLabel ?? '';
        frame.src = trailerAddress(key, env.origin);
        frame.allow = 'autoplay; encrypted-media';
        frame.referrerPolicy = 'strict-origin-when-cross-origin';
        panel.dataset.playing = 'true';
        panel.dataset.muted = 'true';
        frame.addEventListener('load', () => {
            env.clearTimeout(fallback);
            panel.dataset.trailerReady = 'true';
        });
        frame.addEventListener('error', () => showDescription(panel));
        fallback = env.setTimeout(() => showDescription(panel), trailerTimeout);
        holder.replaceChildren(frame);
    }

    async function open(card: HTMLElement): Promise<void> {
        const address = card.dataset.previewSrc;
        if (!address || !card.isConnected) return;
        const request = new AbortController();
        current = { card, request };
        let html = answers.get(address);
        if (html === undefined) {
            try {
                const response = await env.fetch(address, { signal: request.signal, headers: { Accept: 'text/html' } });
                if (!response.ok || response.status === 204) {
                    if (current?.request === request) current = undefined;
                    return;
                }

                html = await response.text();
                answers.set(address, html);
            } catch {
                if (current?.request === request) current = undefined;
                return;
            }
        }

        // The hover may have ended while the answer came.
        if (current?.request !== request || request.signal.aborted || !card.isConnected || !card.matches(':hover')) {
            if (current?.request === request) current = undefined;
            return;
        }

        const panel = env.parse(html);
        if (!panel) {
            current = undefined;
            return;
        }

        panel.setAttribute('popover', 'manual');
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-label', card.querySelector('.media-card__surface')?.getAttribute('aria-label') ?? '');

        // The card's own action and heart move into the foot of the panel.
        const extra = panel.querySelector('.media-card__preview-extra');
        for (const selector of ['.media-card__actions', '.media-card__bookmark']) {
            const control = card.querySelector(selector)?.firstElementChild;
            if (control && extra) extra.append(control.cloneNode(true));
        }

        card.append(panel);
        current.panel = panel;
        panel.showPopover();
        startTrailer(panel);
    }

    // The trailer's own controls: the embed is driven by messages, so the state is kept here.
    function control(button: HTMLElement): void {
        const panel = current?.panel;
        const frame = panel ? frameOf(panel) : null;
        if (!panel || !frame) return;
        const action = button.dataset.trailerAction;
        const key = action === 'toggle' ? 'playing' : 'muted';
        const active = panel.dataset[key] !== 'true';
        panel.dataset[key] = String(active);
        const label = active ? button.dataset.labelOn : button.dataset.labelOff;
        if (label) button.setAttribute('aria-label', label);
        const command = action === 'toggle' ? (active ? 'playVideo' : 'pauseVideo') : (active ? 'mute' : 'unMute');
        frame.contentWindow?.postMessage(trailerCommand(command), '*');
    }

    const onOver = (event: PointerEvent): void => {
        if (event.pointerType !== 'mouse' || !(event.target instanceof Element)) return;
        const target = event.target;
        const card = target.closest<HTMLElement>('.media-card[data-preview-src]');
        if (current && card === current.card) {
            env.clearTimeout(closing);

            // Moving onto a control before the panel is there cancels it.
            if (!current.panel && !isCoverTarget(target)) stop();
            return;
        }

        // Another card ends the open preview; a control or the text beside the cover starts nothing.
        env.clearTimeout(timer);
        if (current) stop();
        if (card && isCoverTarget(target) && env.canHover()) timer = env.setTimeout(() => { void open(card); }, previewDelay);
    };

    const onOut = (event: PointerEvent): void => {
        if (event.pointerType !== 'mouse' || !(event.target instanceof Element)) return;
        const card = event.target.closest<HTMLElement>('.media-card[data-preview-src]');
        if (!card || (event.relatedTarget instanceof Node && card.contains(event.relatedTarget))) return;
        env.clearTimeout(timer);
        if (current) closing = env.setTimeout(stop, settleDelay);
    };

    const onClick = (event: MouseEvent): void => {
        if (!(event.target instanceof Element)) return;
        const button = event.target.closest<HTMLElement>('[data-trailer-action]');
        if (button) {
            control(button);
            return;
        }

        // A click on the panel's own actions goes on (the page changes or the form is sent); anywhere else it ends the preview.
        if (!event.target.closest('.media-card__preview')) stop();
    };

    const onKey = (event: KeyboardEvent): void => {
        if (event.key === 'Escape') stop();
    };

    const onHidden = (): void => {
        if (env.document.hidden) stop();
    };

    env.document.addEventListener('pointerover', onOver as EventListener);
    env.document.addEventListener('pointerout', onOut as EventListener);
    env.document.addEventListener('click', onClick as EventListener);
    env.document.addEventListener('keydown', onKey as EventListener);
    env.document.addEventListener('scroll', stop, { capture: true, passive: true });
    env.document.addEventListener('visibilitychange', onHidden);
    env.window.addEventListener('resize', stop);
    env.window.addEventListener('pagehide', stop);
    return stop;
}
