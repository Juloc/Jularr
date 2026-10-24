import { onPage } from '../../../Modules/Navigation/PageSwap';

interface CompanionGrant {
    id: string;
    playbackSessionId: string;
}

interface CompanionState {
    playbackSessionId: string;
    revision: number;
    isPlaying: boolean;
    positionMs: number;
    durationMs: number | null;
    reportedAt: string | null;
    lastCommandId: string | null;
    lastCommandSucceeded: boolean | null;
}

interface QueuedCommand {
    id: string;
    revision: number;
}

const grantStorageKey = 'jularr.companion.grant.v1';
const pollIntervalMs = 2000;
const liveWithinMs = 15000;
const ackWithinMs = 13000;

onPage(import.meta.url, setup);

function setup(): void {
    const root = document.querySelector<HTMLElement>('[data-companion]');
    if (!root) return;

    const pair = root.querySelector<HTMLElement>('[data-companion-pair]')!;
    const claim = root.querySelector<HTMLElement>('[data-companion-claim]')!;
    const tokenInput = root.querySelector<HTMLInputElement>('#companion-token')!;
    const controls = root.querySelector<HTMLElement>('[data-companion-controls]')!;
    const status = root.querySelector<HTMLElement>('[data-companion-status]')!;
    const feedback = root.querySelector<HTMLElement>('[data-companion-feedback]')!;
    const seek = root.querySelector<HTMLInputElement>('#companion-seek')!;
    const time = root.querySelector<HTMLElement>('[data-companion-time]')!;
    const play = root.querySelector<HTMLButtonElement>('[data-companion-command="play"]')!;
    const pause = root.querySelector<HTMLButtonElement>('[data-companion-command="pause"]')!;
    const disconnect = root.querySelector<HTMLButtonElement>('[data-companion-disconnect]')!;
    const connect = claim.querySelector<HTMLButtonElement>('button')!;
    const csrf = root.dataset.csrf ?? '';

    let grant: CompanionGrant | null = readGrant();
    let current: CompanionState | null = null;
    let pending: { id: string; revision: number; until: number } | null = null;
    let stopped = false;

    const message = (key: string) => root.dataset[key] ?? '';
    function show(messageText: string, error = false): void {
        feedback.textContent = error ? messageText : '';
        status.textContent = error ? message('statusOffline') : messageText;
    }

    function render(): void {
        const paired = grant !== null;
        pair.hidden = paired;
        controls.hidden = !paired;
        const live = !!current?.reportedAt && Date.now() - Date.parse(current.reportedAt) < liveWithinMs;
        const enabled = paired && live && !pending;

        play.disabled = !enabled || current?.isPlaying === true;
        pause.disabled = !enabled || current?.isPlaying !== true;
        seek.disabled = !enabled || !current?.durationMs;
        if (current) {
            seek.max = String(Math.max(1, current.durationMs ?? 1));
            if (document.activeElement !== seek) seek.value = String(current.positionMs);
            time.textContent = formatTime(current.positionMs) + ' / ' + formatTime(current.durationMs ?? 0);
        }
        if (paired && !pending) {
            status.textContent = live ? message('statusOnline') : message('statusOffline');
        } else if (pending) {
            status.textContent = message('statusPending');
        }
    }

    async function api<T>(url: string, method = 'GET', body?: unknown): Promise<T> {
        const controller = new AbortController();
        const timeout = window.setTimeout(() => controller.abort(), 8000);
        try {
            const response = await fetch(url, {
                method, credentials: 'same-origin', cache: 'no-store',
                headers: {
                    'Accept': 'application/json',
                    ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
                    ...(method !== 'GET' ? { RequestVerificationToken: csrf } : {})
                },
                body: body !== undefined ? JSON.stringify(body) : undefined,
                signal: controller.signal
            });
            if (!response.ok) {
                const error = new Error('Request failed: ' + response.status) as Error & { status?: number };
                error.status = response.status;
                throw error;
            }
            return await response.json() as T;
        } finally {
            window.clearTimeout(timeout);
        }
    }

    function clearGrant(): void {
        grant = null;
        current = null;
        pending = null;
        sessionStorage.removeItem(grantStorageKey);
        render();
    }

    function readGrant(): CompanionGrant | null {
        try {
            const value = JSON.parse(sessionStorage.getItem(grantStorageKey) || 'null') as CompanionGrant | null;
            return value?.id && value.playbackSessionId ? value : null;
        } catch {
            return null;
        }
    }

    function rememberGrant(value: CompanionGrant): void {
        grant = value;
        current = null;
        pending = null;
        sessionStorage.setItem(grantStorageKey, JSON.stringify(value));
        feedback.textContent = '';
        show(message('statusPaired'));
        render();
    }

    async function poll(): Promise<void> {
        if (stopped || !root.isConnected) return;
        if (grant) {
            try {
                const next = await api<CompanionState>('/api/v1/playback/companion/' + encodeURIComponent(grant.id));
                current = next;
                if (pending && next.lastCommandId === pending.id) {
                    const success = next.lastCommandSucceeded === true;
                    pending = null;
                    feedback.textContent = success ? '' : message('statusRejected');
                } else if (pending && (Date.now() > pending.until || next.revision > pending.revision)) {
                    pending = null;
                    feedback.textContent = message('statusFailed');
                }
                render();
            } catch (error) {
                const http = (error as Error & { status?: number }).status;
                if (http === 401 || http === 403 || http === 404 || http === 410) {
                    clearGrant();
                    show(message('statusEnded'), true);
                } else {
                    current = null;
                    render();
                    feedback.textContent = message('statusOffline');
                }
            }
        }
        if (root.isConnected && !stopped) window.setTimeout(() => void poll(), pollIntervalMs);
    }

    async function sendCommand(action: string, positionMs: number | null = null): Promise<void> {
        if (!grant || !current || pending || !current.reportedAt || Date.now() - Date.parse(current.reportedAt) >= liveWithinMs) return;
        const id = newCommandId();
        pending = { id, revision: current.revision + 1, until: Date.now() + ackWithinMs };
        feedback.textContent = '';
        render();
        try {
            const command = await api<QueuedCommand>('/api/v1/playback/companion/' + encodeURIComponent(grant.id) + '/commands',
                'POST', { commandId: id, expectedRevision: current.revision, action, positionMs });
            if (pending?.id === command.id) pending.revision = command.revision;
        } catch (error) {
            pending = null;
            const http = (error as Error & { status?: number }).status;
            if (http === 401 || http === 403 || http === 404) {
                clearGrant();
                show(message('statusEnded'), true);
            } else {
                feedback.textContent = message('statusFailed');
                render();
            }
        }
    }

    async function claimInvitation(): Promise<void> {
        const token = tokenInput.value.trim();
        if (!token || grant) return;
        connect.disabled = true;
        feedback.textContent = '';
        try {
            const result = await api<CompanionGrant>('/api/v1/playback/companion/claims', 'POST', { token });
            tokenInput.value = '';
            rememberGrant(result);
        } catch {
            show(message('statusFailed'), true);
        } finally {
            connect.disabled = false;
        }
    }

    connect.addEventListener('click', () => void claimInvitation());
    tokenInput.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            void claimInvitation();
        }
    });

    play.addEventListener('click', () => void sendCommand('play'));
    pause.addEventListener('click', () => void sendCommand('pause'));
    seek.addEventListener('change', () => void sendCommand('seek', Number(seek.value)));

    disconnect.addEventListener('click', async () => {
        if (!grant) return;
        disconnect.disabled = true;
        try {
            await api('/api/v1/playback/companion/' + encodeURIComponent(grant.id), 'DELETE');
            clearGrant();
        } catch {
            feedback.textContent = message('statusFailed');
        } finally {
            disconnect.disabled = false;
        }
    });

    // The QR address carries its one-time secret only in a URL fragment, never a query or referrer.
    const invite = new URLSearchParams(location.hash.slice(1)).get('invite');
    if (invite) {
        tokenInput.value = invite;
        history.replaceState(history.state, '', location.pathname + location.search);
        if (!grant) void claimInvitation();
    }

    render();
    void poll();
    window.addEventListener('pagehide', () => { stopped = true; }, { once: true });
}

export function formatTime(milliseconds: number): string {
    const total = Math.max(0, Math.floor(milliseconds / 1000));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor(total / 60) % 60;
    const seconds = total % 60;
    return (hours ? hours + ':' + String(minutes).padStart(2, '0') : minutes) + ':' + String(seconds).padStart(2, '0');
}

export function newCommandId(): string {
    if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, item => item.toString(16).padStart(2, '0')).join('');
    return [hex.slice(0, 8), hex.slice(8, 12), hex.slice(12, 16), hex.slice(16, 20), hex.slice(20)].join('-');
}
