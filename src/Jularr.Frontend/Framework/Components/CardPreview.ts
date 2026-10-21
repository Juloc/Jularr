// The preview of a card: after the mouse rested on the cover (a keyboard focus counts the same, a touch device that has a card linger in view starts it by itself)
// the card opens a panel over the row, as high as the card, with the title's description or, where it has one, its trailer: the one official embed Jularr frames,
// muted, lazily loaded, wide and cropped so that none of the provider's own chrome shows, with Jularr's own pause, sound and progress. Nothing is requested before
// the delay, only one preview is open at a time, and the cards of the row never change width or position (the card under the panel is only hidden).

export const previewDelay = 550;
export const settleDelay = 120;
export const dwellDelay = 2000;
const trailerStartDelay = 210;
const trailerTimeout = 7000;
const progressTimeout = 4000;
const handshakeInterval = 500;
const providerOrigin = 'https://www.youtube-nocookie.com';
const widthFactor = 1.84;
const edge = 4;

// Parts of a card that are controls of their own: the hover over them never counts.
const controlsOfCard = 'button, a.button, form, input, .media-card__actions, .media-card__bookmark, .media-availability-badge, .media-card__preview';

export function isCoverTarget(target: Element | null): boolean {
    return !!target?.closest('.media-card__surface') && !target.closest(controlsOfCard);
}

export function isVideoKey(key: string | undefined): key is string {
    return !!key && /^[A-Za-z0-9_-]{11}$/.test(key);
}

// The official embed without the provider's controls; the commands and messages below drive and read it.
export function trailerAddress(key: string, origin: string): string {
    const base = `https://www.youtube-nocookie.com/embed/${key}?autoplay=1&mute=1&playsinline=1&controls=0&modestbranding=1&rel=0&enablejsapi=1`;
    return origin.startsWith('http') ? `${base}&origin=${encodeURIComponent(origin)}` : base;
}

export function trailerCommand(func: 'playVideo' | 'pauseVideo' | 'mute' | 'unMute'): string {
    return JSON.stringify({ event: 'command', func, args: [] });
}

// The provider's player only reports to a listener that said so.
export function listeningMessage(): string {
    return JSON.stringify({ event: 'listening', id: 'jularr-preview', channel: 'widget' });
}

export interface Box { left: number; top: number; width: number; height: number }
export interface Bounds { left: number; right: number }

// The panel is a little under twice as wide as the card.
export function previewWidth(cardWidth: number): number {
    return Math.round(cardWidth * widthFactor);
}

// How far the panel moves sideways so that it stays inside the row (and so inside the viewport): the shift of the mockup.
export function panelShift(cardLeft: number, bounds: Bounds, panelWidth: number): number {
    const left = cardLeft - bounds.left;
    const right = bounds.right - cardLeft;
    return Math.max(edge - left, Math.min(0, right - panelWidth - 8));
}

// The box of the panel from the card, the row's visible span and the viewport: as high as the card, shifted but never wider than the room.
export function previewBox(card: Box, row: Bounds | null, viewport: { width: number; height: number }): Box {
    const bounds: Bounds = { left: Math.max(row?.left ?? 0, 0), right: Math.min(row?.right ?? viewport.width, viewport.width) };
    const width = Math.min(previewWidth(card.width), viewport.width - 2 * edge);
    const shifted = card.left + Math.round(panelShift(card.left, bounds, width));
    const height = Math.min(card.height, viewport.height - 2 * edge);
    return {
        left: Math.max(edge, Math.min(shifted, viewport.width - width - edge)),
        top: Math.max(edge, Math.min(card.top, viewport.height - height - edge)),
        width,
        height
    };
}

// 2:24, or 1:02:24 for a long one.
export function formatTime(seconds: number): string {
    const total = Math.max(0, Math.floor(seconds));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const rest = String(total % 60).padStart(2, '0');
    return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${rest}` : `${minutes}:${rest}`;
}

export interface TrailerInfo { current?: number; duration?: number; state?: number }

// What the provider's player reports; anything else it says is ignored.
export function readTrailerInfo(data: unknown): TrailerInfo | undefined {
    let message: unknown = data;
    if (typeof data === 'string') {
        try { message = JSON.parse(data); } catch { return undefined; }
    }

    const info = (message as { info?: Record<string, unknown> } | null)?.info;
    if (!info || typeof info !== 'object') return undefined;
    const result: TrailerInfo = {};
    if (typeof info.currentTime === 'number' && Number.isFinite(info.currentTime)) result.current = info.currentTime;
    if (typeof info.duration === 'number' && Number.isFinite(info.duration) && info.duration > 0) result.duration = info.duration;
    if (typeof info.playerState === 'number') result.state = info.playerState;
    return result;
}

// A card on a touch device counts as lingering when it is mostly in view and near the middle of the screen; of several the one nearest the middle, and the one
// that was chosen before stays chosen for as long as it qualifies.
export interface Candidate { id: string; ratio: number; centerX: number; centerY: number }

export function pickCandidate(candidates: readonly Candidate[], viewport: { width: number; height: number }, incumbent?: string): string | undefined {
    let best: string | undefined;
    let distance = Infinity;
    for (const item of candidates) {
        if (item.ratio < 0.8) continue;
        if (item.centerY < viewport.height * 0.2 || item.centerY > viewport.height * 0.8) continue;
        if (item.id === incumbent) return incumbent;
        const away = Math.hypot(item.centerX - viewport.width / 2, item.centerY - viewport.height / 2);
        if (away < distance) {
            distance = away;
            best = item.id;
        }
    }

    return best;
}

// A card that stays the candidate for the whole delay fires once; any other candidate starts the delay again.
export function createDwell(delay: number, schedule: (action: () => void, milliseconds: number) => unknown, cancel: (handle: unknown) => void, fire: (id: string) => void) {
    let key: string | undefined;
    let timer: unknown;
    return {
        get key(): string | undefined { return key; },
        update(id: string | undefined): void {
            if (id === key) return;
            cancel(timer);
            key = id;
            if (id !== undefined) timer = schedule(() => fire(id), delay);
        },
        reset(): void {
            cancel(timer);
            key = undefined;
        }
    };
}

export interface PreviewEnvironment {
    document: Document;
    window: Window;
    fetch(address: string, init: { signal: AbortSignal; headers: Record<string, string> }): Promise<{ ok: boolean; status: number; text(): Promise<string> }>;
    setTimeout(action: () => void, milliseconds: number): unknown;
    clearTimeout(handle: unknown): void;
    canHover(): boolean;
    // Whether a card may start by itself (data saver, offline).
    canAutoplay(): boolean;
    parse(html: string): HTMLElement | null;
    measure(card: HTMLElement): { card: Box; row: Bounds | null; viewport: { width: number; height: number } };
    viewport(): { width: number; height: number };
    // The cards currently near the viewport, as the observer reports them.
    candidates(): { card: HTMLElement; ratio: number; centerX: number; centerY: number }[];
    origin: string;
}

type Source = 'hover' | 'focus' | 'touch';

export function attachCardPreview(env: PreviewEnvironment): (() => void) & { evaluate(): void } {
    let timer: unknown;
    let closing: unknown;
    let fallback: unknown;
    let starting: unknown;
    let handshake: unknown;
    let progress: unknown;
    let current: { card: HTMLElement; panel?: HTMLElement; request: AbortController; heard?: boolean; duration?: number } | undefined;
    let settling: unknown;
    const answers = new Map<string, string>();
    const lingering = new Map<string, HTMLElement>();

    const cancelTimers = (): void => {
        env.clearTimeout(timer);
        env.clearTimeout(closing);
        env.clearTimeout(fallback);
        env.clearTimeout(starting);
        env.clearTimeout(handshake);
        env.clearTimeout(progress);
    };

    // Nothing stays loaded: the panel and its embed leave with it, and the card under it shows again.
    const stop = (): void => {
        cancelTimers();
        if (!current) return;
        current.request.abort();
        const { panel, card } = current;
        current = undefined;
        delete card.dataset.previewOpen;
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

    function setState(panel: HTMLElement, key: 'playing' | 'muted', active: boolean): void {
        panel.dataset[key] = String(active);
        const button = panel.querySelector<HTMLElement>(`[data-trailer-action=${key === 'playing' ? 'toggle' : 'mute'}]`);
        const label = button ? (active ? button.dataset.labelOn : button.dataset.labelOff) : undefined;
        if (button && label) button.setAttribute('aria-label', label);
    }

    // The line and the time follow the provider's player; without a time from it the line only runs on.
    function showProgress(panel: HTMLElement, info: TrailerInfo): void {
        if (!current) return;
        if (info.duration !== undefined) current.duration = info.duration;
        if (info.state === 1 || info.state === 2 || info.state === 0) setState(panel, 'playing', info.state === 1);
        const total = current.duration;
        if (info.current === undefined || total === undefined) return;
        current.heard = true;
        panel.dataset.progress = 'live';
        panel.style.setProperty('--trailer-progress', `${Math.min(100, Math.max(0, info.current / total * 100)).toFixed(2)}%`);
        const time = panel.querySelector<HTMLElement>('[data-trailer-time]');
        if (time) time.textContent = `${formatTime(info.current)} / ${formatTime(total)}`;
    }

    function listen(panel: HTMLElement, attempt: number): void {
        const frame = frameOf(panel);
        if (!frame || current?.panel !== panel || current.heard || attempt > 12) return;
        frame.contentWindow?.postMessage(listeningMessage(), '*');
        handshake = env.setTimeout(() => listen(panel, attempt + 1), handshakeInterval);
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
        setState(panel, 'playing', true);
        setState(panel, 'muted', true);
        frame.addEventListener('load', () => {
            env.clearTimeout(fallback);
            panel.dataset.trailerReady = 'true';
            listen(panel, 0);
        });
        frame.addEventListener('error', () => showDescription(panel));
        fallback = env.setTimeout(() => showDescription(panel), trailerTimeout);
        progress = env.setTimeout(() => { if (current?.panel === panel && !current.heard) panel.dataset.progress = 'fake'; }, progressTimeout);
        holder.replaceChildren(frame);
    }

    // The card's own action and heart move into the foot of the panel, as a pill and a disc.
    function moveControls(card: HTMLElement, panel: HTMLElement): void {
        const extra = panel.querySelector('.media-card__preview-extra');
        if (!extra) return;
        for (const [selector, style] of [['.media-card__actions', 'media-card__preview-primary'], ['.media-card__bookmark', 'media-card__preview-save']] as const) {
            const control = card.querySelector(selector)?.firstElementChild;
            if (!control) continue;
            const copy = control.cloneNode(true) as HTMLElement;
            (copy.matches('.button') ? copy : copy.querySelector('.button'))?.classList.add(style);
            extra.append(copy);
        }
    }

    // The panel sits over the card: its box comes from the card, the row and the viewport.
    function place(card: HTMLElement, panel: HTMLElement): void {
        const { card: box, row, viewport } = env.measure(card);
        const target = previewBox(box, row, viewport);
        panel.style.setProperty('--preview-left', `${target.left}px`);
        panel.style.setProperty('--preview-top', `${target.top}px`);
        panel.style.setProperty('--preview-width', `${target.width}px`);
        panel.style.setProperty('--preview-height', `${target.height}px`);
    }

    // Whether the reason for the preview still holds when the answer is there.
    function wanted(card: HTMLElement, source: Source): boolean {
        if (source === 'hover') return card.matches(':hover');
        if (source === 'focus') return card.contains(env.document.activeElement);
        return true;
    }

    async function open(card: HTMLElement, source: Source): Promise<void> {
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

        // The reason may have ended while the answer came.
        if (current?.request !== request || request.signal.aborted || !card.isConnected || !wanted(card, source)) {
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
        moveControls(card, panel);
        card.append(panel);
        place(card, panel);
        current.panel = panel;
        card.dataset.previewOpen = 'true';
        panel.showPopover();
        starting = env.setTimeout(() => { if (current?.panel === panel) startTrailer(panel); }, trailerStartDelay);
    }

    // The trailer's own controls: the embed is driven by messages, so the state is kept here.
    function control(button: HTMLElement): void {
        const panel = current?.panel;
        const frame = panel ? frameOf(panel) : null;
        if (!panel || !frame) return;
        const action = button.dataset.trailerAction;
        const key = action === 'toggle' ? 'playing' : 'muted';
        const active = panel.dataset[key] !== 'true';
        setState(panel, key, active);
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
        if (card && isCoverTarget(target) && env.canHover()) timer = env.setTimeout(() => { void open(card, 'hover'); }, previewDelay);
    };

    const onOut = (event: PointerEvent): void => {
        if (event.pointerType !== 'mouse' || !(event.target instanceof Element)) return;
        const card = event.target.closest<HTMLElement>('.media-card[data-preview-src]');
        if (!card || (event.relatedTarget instanceof Node && card.contains(event.relatedTarget))) return;
        env.clearTimeout(timer);
        if (current) closing = env.setTimeout(stop, settleDelay);
    };

    // The keyboard: a card that takes the focus opens after the same rest.
    const onFocusIn = (event: FocusEvent): void => {
        if (!(event.target instanceof Element) || !env.canHover()) return;
        const card = event.target.closest<HTMLElement>('.media-card[data-preview-src]');
        if (current && card === current.card) {
            env.clearTimeout(closing);
            return;
        }

        env.clearTimeout(timer);
        if (current) stop();
        if (card && event.target.matches(':focus-visible') && !event.target.closest('.media-card__preview')) timer = env.setTimeout(() => { void open(card, 'focus'); }, previewDelay);
    };

    const onFocusOut = (event: FocusEvent): void => {
        if (!(event.target instanceof Element)) return;
        const card = event.target.closest<HTMLElement>('.media-card[data-preview-src]');
        if (!card || (event.relatedTarget instanceof Node && card.contains(event.relatedTarget))) return;
        env.clearTimeout(timer);
        if (current?.card === card) closing = env.setTimeout(stop, settleDelay);
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

    const onMessage = (event: MessageEvent): void => {
        const panel = current?.panel;
        const frame = panel ? frameOf(panel) : null;
        if (!panel || !frame || event.origin !== providerOrigin || event.source !== frame.contentWindow) return;
        const info = readTrailerInfo(event.data);
        if (info) showProgress(panel, info);
    };

    // A touch device has no hover: a card that stays in view near the middle for the dwell opens by itself, one at a time, only where the profile allows it.
    const allowed = (card: HTMLElement): boolean => !env.canHover() && env.canAutoplay() && !env.document.hidden
        && card.closest('[data-autoplay-previews]')?.getAttribute('data-autoplay-previews') === 'true';
    const dwell = createDwell(dwellDelay, (action, milliseconds) => env.setTimeout(action, milliseconds), handle => env.clearTimeout(handle), id => {
        const card = lingering.get(id);
        if (!card?.isConnected || !allowed(card)) return;
        if (current) stop();
        void open(card, 'touch');
    });

    const evaluate = (): void => {
        if (env.canHover() || !env.canAutoplay() || env.document.hidden) {
            dwell.reset();
            return;
        }

        lingering.clear();
        const found: Candidate[] = [];
        for (const item of env.candidates()) {
            if (!allowed(item.card)) continue;
            const id = item.card.dataset.previewSrc ?? '';
            lingering.set(id, item.card);
            found.push({ id, ratio: item.ratio, centerX: item.centerX, centerY: item.centerY });
        }

        const best = pickCandidate(found, env.viewport(), dwell.key);

        // The card that is open stays until it leaves; another one takes over after its own dwell.
        dwell.update(best);
    };

    // Moving the page ends a preview and starts the dwell anew.
    const onScroll = (event: Event): void => {
        // The panel's own text scrolls without ending it.
        if (event.target instanceof Node && current?.panel?.contains(event.target)) return;
        stop();
        dwell.reset();
        env.clearTimeout(settling);
        settling = env.setTimeout(evaluate, 200);
    };

    env.document.addEventListener('pointerover', onOver as EventListener);
    env.document.addEventListener('pointerout', onOut as EventListener);
    env.document.addEventListener('focusin', onFocusIn as EventListener);
    env.document.addEventListener('focusout', onFocusOut as EventListener);
    env.document.addEventListener('click', onClick as EventListener);
    env.document.addEventListener('keydown', onKey as EventListener);
    env.document.addEventListener('scroll', onScroll, { capture: true, passive: true });
    env.document.addEventListener('visibilitychange', onHidden);
    env.window.addEventListener('message', onMessage as EventListener);
    env.window.addEventListener('resize', stop);
    env.window.addEventListener('pagehide', stop);
    return Object.assign(stop, { evaluate });
}
